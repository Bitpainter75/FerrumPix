Imports System
Imports System.Buffers
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.IO
Imports System.Linq
Imports System.Runtime.CompilerServices
Imports System.Threading
Imports System.Threading.Tasks
Imports SkiaSharp
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform
Imports System.Text.RegularExpressions
Imports System.Text.Json.Serialization
Imports System.Runtime.InteropServices
Imports QRCoder

' Retusche, Reparaturpinsel, Klonstempel und das gerechnete Auffuellen einer Luecke.
' Eigener Zustand: RetouchFeatherStops (die weiche Kante der Retusche-Scheibe).
' Herausgeloest am 2026-08-06 aus ImageProcessor.vb, Zeile fuer Zeile unveraendert.
' Hier liegt der letzte zusammenhaengende Bereich, der noch pixelweise ueber GetPixel und
' SetPixel arbeitet - die Umstellung auf Rohpuffer wird damit eine lokale Arbeit.
Namespace Services

    Partial Public Class ImageProcessor

        ' Weiche Kante der Retusche-Scheibe: bis 55% des Radius voll deckend, danach linear auslaufend.
        ' Entspricht der früheren Formel edge = (radius - d) / (radius * 0.45).
        Private Shared ReadOnly RetouchFeatherStops As Single() = {0.0F, 0.55F, 1.0F}
        ''' <summary>Radius der Patches, mit denen die Reparatur fuellt - MIT DER AUFLOESUNG
        ''' SKALIERT, nicht mehr fest. Er war fest 6 und damit auf allem oberhalb einer Web-Groesse
        ''' zu klein: ein Patch muss ungefaehr EINE STRUKTURPERIODE ueberspannen, sonst kann die
        ''' Quellensuche gar nicht unterscheiden, ob ein Treffer phasenrichtig sitzt, und ein
        ''' Fenstergitter zerfaellt zu Matsch.
        '''
        ''' Der Teiler kommt aus einem Kontrollversuch mit DEMSELBEN Motiv in zwei Aufloesungen
        ''' (echtes 50-MP-Foto einer Hochhausfassade, einmal nativ 5784x8660, einmal auf 800x1198
        ''' verkleinert), jeweils gegen das unbeschaedigte Original gemessen: nativ liegt das
        ''' Optimum bei Radius 40 (Fehler 15,7, Kontrast 16,0 gegen 16,7 im Original; Radius 24
        ''' liefert nur 11,8 Kontrast, also sichtbar Matsch), verkleinert bei Radius 6 (Fehler
        ''' 14,3; Radius 40 gibt dort 19,7). Optimum-Verhaeltnis 6,7 bei Aufloesungsverhaeltnis 7,2
        ''' - also linear. 8660/40 und 1198/6 ergeben 217 und 200; 210 liegt dazwischen.
        '''
        ''' Grenzen: unter 4 traegt ein Patch keine Struktur mehr. Die Obergrenze 40 ist NICHT nur
        ''' der gemessene Bestwert, sie haelt auch HealingSearchMargin bei 188 - mit 64 waere der
        ''' Suchrand 212, und das ist gemessen KEIN freier Zugewinn: am 50-MP-Foto verschlechterte
        ''' der groessere Rand den Kontrast im reparierten Bereich von 16,0 auf 17,8 (er findet
        ''' weiter entfernte, schlechter passende Quellen), waehrend er am 800-px-Bild half. Wer die
        ''' Obergrenze anhebt, muss den Suchrand getrennt davon festhalten und beides neu messen.
        ''' Laufzeit ist ueber den ganzen Bereich unauffaellig (3,0 s an 50 MP): groessere Patches
        ''' heissen weniger Kopien und weniger Suchen.
        '''
        ''' Gerechnet wird auf dem ARBEITSBILD, nicht auf der Datei: grosse Regionen werden vorher
        ''' auf HealingMaxNativeExtent normalisiert, und dort sind die Strukturen entsprechend
        ''' kleiner. Ein fester Wert waere dort wieder zu gross.
        '''
        ''' VORSICHT bei Gegenbeispielen aus SYNTHETISCHEN Testbildern: eine kuenstliche Fassade mit
        ''' 76 px Fensterabstand in einem 5,6-MP-Bild braucht Radius 64 und widerspricht der Regel -
        ''' sie hat aber ein Strukturmass, das in echten Fotos so nicht vorkommt. Der eigentliche
        ''' Vorhersagewert ist die Strukturperiode; die Aufloesung ist ihr praktischer Stellvertreter
        ''' und fuer dasselbe Motiv exakt richtig. Wer es besser will, misst die Periode aus der
        ''' Umgebung der Reparaturstelle (Autokorrelation) statt zu schaetzen.</summary>
        Private Const HealingPatchRadiusDivisor As Integer = 210
        Private Const HealingPatchRadiusMin As Integer = 4
        Private Const HealingPatchRadiusMax As Integer = 40
        Private Const HealingSearchBaseMargin As Integer = 140
        ''' <summary>Kontextrand fuer die Quellensuche. Rechnet mit dem GROESSTEN moeglichen Patch,
        ''' damit der Rand nie zu knapp wird - er kostet nur etwas mehr kopierten Kontext.</summary>
        Private Const HealingSearchMargin As Integer = HealingSearchBaseMargin + HealingPatchRadiusMax + 8
        Private Const HealingMaxNativeExtent As Integer = 1200

        ''' <summary>Patch-Radius zur Bildkante, in der gerechnet wird.</summary>
        Private Shared Function HealingPatchRadiusFor(resolutionLongEdge As Integer) As Integer
            Return CInt(Clamp(CSng(resolutionLongEdge) / HealingPatchRadiusDivisor,
                              HealingPatchRadiusMin, HealingPatchRadiusMax))
        End Function

        ''' <summary>Die durchgereichte Bildkante, oder - wenn keine gesetzt ist - die des Bildes
        ''' selbst. NICHT die des Arbeitsausschnitts: der ist Region plus Suchrand und waere auf
        ''' einem 50-MP-Foto nur rund 1100 px breit, der Radius damit um das Achtfache zu klein.</summary>
        Private Shared Function EffectiveHealingLongEdge(result As SKBitmap, resolutionLongEdge As Integer) As Integer
            If resolutionLongEdge > 0 Then Return resolutionLongEdge
            If result Is Nothing Then Return 0
            Return Math.Max(result.Width, result.Height)
        End Function

        ''' ARBEITSBILD-Umbau Stufe E: Das Rezept-Replay der Retusche ist entfernt.
        ''' Retusche wird beim Commit REGIONAL in Vollauflösung ins Arbeitsbild eingebacken
        ''' (EditorViewModel.CommitRetouchStroke -> WorkingImageService.CommitRegion ->
        ''' ApplyRetouchSpotsInPlace). Damit entfielen: ApplyRetouch, der Retusche-Stufen-Cache
        ''' (Primary/Secondary/Seed-Slots), ComputeRetouchSpotsKey, das Praefix-Anhaengen und der
        ''' .fpx-Seed. Erhalten bleiben die Zeichen-Engines DrawRetouchSpot/DrawHealingRegion und
        ''' die InPlace-Anwendungen darunter (Commit + Live-Vorschau).

        Public Shared Sub ApplyRetouchSpotInPlace(target As SKBitmap, sampleSource As SKBitmap, spot As RetouchSpot,
                                                  sourceWidthPixels As Integer, sourceHeightPixels As Integer)
            If target Is Nothing OrElse sampleSource Is Nothing OrElse spot Is Nothing Then Return
            Using canvas = New SKCanvas(target)
                If IsHealingSpot(spot) Then
                    DrawHealingRegion(target, canvas, target, {spot}, sourceWidthPixels, sourceHeightPixels)
                Else
                    DrawRetouchSpot(target, sampleSource, canvas, spot, sourceWidthPixels, sourceHeightPixels)
                End If
            End Using
        End Sub

        Public Shared Sub ApplyRetouchSpotsInPlace(target As SKBitmap, sampleSource As SKBitmap, spots As IReadOnlyList(Of RetouchSpot),
                                                   sourceWidthPixels As Integer, sourceHeightPixels As Integer)
            If target Is Nothing OrElse sampleSource Is Nothing OrElse spots Is Nothing OrElse spots.Count = 0 Then Return
            Using canvas = New SKCanvas(target)
                Dim pendingHeal As New List(Of RetouchSpot)()
                Dim pendingHealStrokeId As Integer? = Nothing
                For Each spot In spots
                    If spot Is Nothing Then Continue For
                    If IsHealingSpot(spot) Then
                        If pendingHeal.Count > 0 AndAlso pendingHealStrokeId.HasValue AndAlso spot.StrokeId <> pendingHealStrokeId.Value Then
                            DrawHealingRegion(target, canvas, target, pendingHeal, sourceWidthPixels, sourceHeightPixels)
                            pendingHeal.Clear()
                        End If
                        pendingHeal.Add(spot)
                        pendingHealStrokeId = spot.StrokeId
                        Continue For
                    End If

                    If pendingHeal.Count > 0 Then
                        DrawHealingRegion(target, canvas, target, pendingHeal, sourceWidthPixels, sourceHeightPixels)
                        pendingHeal.Clear()
                        pendingHealStrokeId = Nothing
                    End If
                    DrawRetouchSpot(target, sampleSource, canvas, spot, sourceWidthPixels, sourceHeightPixels)
                Next
                If pendingHeal.Count > 0 Then
                    DrawHealingRegion(target, canvas, target, pendingHeal, sourceWidthPixels, sourceHeightPixels)
                End If
            End Using
        End Sub

        Private Shared Sub DrawRetouchSpot(result As SKBitmap, source As SKBitmap, canvas As SKCanvas,
                                           spot As RetouchSpot, sourceWidthPixels As Integer, sourceHeightPixels As Integer)
            If result Is Nothing OrElse source Is Nothing OrElse canvas Is Nothing OrElse spot Is Nothing Then Return

            Dim scaleX As Single = 1.0F
            Dim scaleY As Single = 1.0F
            If sourceWidthPixels > 0 AndAlso sourceHeightPixels > 0 AndAlso source.Width > 0 AndAlso source.Height > 0 Then
                scaleX = source.Width / CSng(sourceWidthPixels)
                scaleY = source.Height / CSng(sourceHeightPixels)
            End If
            Dim radiusScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))
            Dim cx = Clamp(spot.XPixels * scaleX, 0, source.Width)
            Dim cy = Clamp(spot.YPixels * scaleY, 0, source.Height)
            Dim radius = Clamp(spot.RadiusPixels * radiusScale, 1, Math.Max(source.Width, source.Height))
            Dim flow = Clamp(spot.FlowPercent, 0, 100) / 100.0F
            Dim opacity = Clamp(spot.OpacityPercent, 0, 100) / 100.0F
            Dim strength = Clamp(spot.StrengthPercent, 0, 100) / 100.0F
            Dim alphaFactor = flow * opacity * strength

            If spot.HasCloneSource Then
                Dim sx = Clamp(spot.SourceXPixels * scaleX, 0, source.Width)
                Dim sy = Clamp(spot.SourceYPixels * scaleY, 0, source.Height)
                ' Der Stempel soll denselben bereits bearbeiteten Stand sehen wie Reparatur und
                ' Verwischen. Sonst kopiert er nach nachfolgenden Retuschen wieder Textur aus einem
                ' älteren, retuschefreien Zwischenstand zurück.
                DrawCloneStamp(canvas, result, cx, cy, sx, sy, radius, alphaFactor)
            Else
                ' Verwischen soll auf dem bereits retuschierten Ergebnis aufbauen, damit nach einer
                ' Reparatur nicht wieder Textur aus dem Ursprungsbild "hineingewischt" wird.
                ' BEFUND: KEINE Umgebungsfarb-Scheibe mehr darueber - beim Ziehen ueberlappen
                ' dutzende Spots, und die 28-%-Scheiben konvergierten gegen eine flache Fremdfarbe
                ' (brauner Schmier). Die Scheibe war Fleckentferner-Logik, kein Verwischen.
                DrawBlurSpot(result, canvas, cx, cy, radius, alphaFactor)
            End If
        End Sub

        Private Shared Function IsHealingSpot(spot As RetouchSpot) As Boolean
            Return spot IsNot Nothing AndAlso Not spot.HasCloneSource AndAlso
                   String.Equals(spot.Mode, "Heal", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' Kopiert eine weich auslaufende Scheibe von (sx, sy) nach (cx, cy). Der Bitmap-Shader wird
        ''' um den Versatz verschoben, ein Radial-Verlauf liefert per DstIn die Kantenmaske.
        Private Shared Sub DrawCloneStamp(canvas As SKCanvas, source As SKBitmap,
                                          cx As Single, cy As Single, sx As Single, sy As Single, radius As Single, flow As Single)
            Dim offset = SKMatrix.CreateTranslation(cx - sx, cy - sy)
            Using bitmapShader = SKShader.CreateBitmap(source, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, offset)
                Using mask = SKShader.CreateRadialGradient(New SKPoint(cx, cy), radius,
                                                           {SKColors.White.WithAlpha(CByte(255 * flow)), SKColors.White.WithAlpha(CByte(255 * flow)), SKColors.Transparent},
                                                           RetouchFeatherStops, SKShaderTileMode.Clamp)
                    Using masked = SKShader.CreateCompose(bitmapShader, mask, SKBlendMode.DstIn)
                        Using paint = New SKPaint With {.Shader = masked, .IsAntialias = True}
                            canvas.DrawCircle(cx, cy, radius, paint)
                        End Using
                    End Using
                End Using
            End Using
        End Sub

        Private Shared Sub DrawSoftDisc(canvas As SKCanvas, cx As Single, cy As Single, radius As Single, fill As SKColor, flow As Single)
            Dim alphaFill = fill.WithAlpha(CByte(fill.Alpha * flow))
            Using shader = SKShader.CreateRadialGradient(New SKPoint(cx, cy), radius,
                                                         {alphaFill, alphaFill, fill.WithAlpha(0)},
                                                         RetouchFeatherStops, SKShaderTileMode.Clamp)
                Using paint = New SKPaint With {.Shader = shader, .IsAntialias = True}
                    canvas.DrawCircle(cx, cy, radius, paint)
                End Using
            End Using
        End Sub

        Private Shared Sub DrawBlurSpot(result As SKBitmap, canvas As SKCanvas,
                                        cx As Single, cy As Single, radius As Single, flow As Single)
            If result Is Nothing OrElse canvas Is Nothing Then Return
            flow = Clamp(flow, 0.0F, 1.0F)
            If radius <= 0.5F OrElse flow <= 0.001F Then Return

            ' BEFUND: sigma gedeckelt - 0,45r machte das Kreisinnere bei grossen Radien
            ' strukturlos, und ueberlappende Zug-Spots verstaerkten das zu Brei.
            Dim sigma = Clamp(radius * 0.22F, 1.25F, 24.0F)
            Dim pad = CInt(Math.Ceiling(radius + sigma * 3.0F + 2.0F))
            Dim left = Math.Max(0, CInt(Math.Floor(cx - pad)))
            Dim top = Math.Max(0, CInt(Math.Floor(cy - pad)))
            Dim right = Math.Min(result.Width, CInt(Math.Ceiling(cx + pad)))
            Dim bottom = Math.Min(result.Height, CInt(Math.Ceiling(cy + pad)))
            Dim width = right - left
            Dim height = bottom - top
            If width <= 0 OrElse height <= 0 Then Return

            ' Die Region direkt aus dem Arbeitsbild lesen. Eine zusaetzliche Patch-Bitmap samt
            ' SKCanvas-Kopie pro Spot verdoppelte zuvor den nativen Speicherverkehr, obwohl der
            ' Box-Blur die Pixel danach ohnehin in verwaltete Puffer uebernahm.
            Using blurred = FastBoxBlurRegion(result, left, top, width, height,
                                              Math.Max(1, CInt(Math.Round(sigma * 1.35F))))
                Dim bounds = New SKRect(left, top, right, bottom)
                canvas.SaveLayer(bounds, Nothing)
                canvas.DrawBitmap(blurred, left, top)
                Using mask = SKShader.CreateRadialGradient(New SKPoint(cx, cy), radius,
                                                           {SKColors.White.WithAlpha(CByte(255 * flow)),
                                                            SKColors.White.WithAlpha(CByte(255 * flow)),
                                                            SKColors.Transparent},
                                                           RetouchFeatherStops, SKShaderTileMode.Clamp)
                    Using maskPaint = New SKPaint With {.Shader = mask, .IsAntialias = True, .BlendMode = SKBlendMode.DstIn}
                        ' BEFUND (der 4x-Schmier): DstIn wirkt nur, wo auch GEZEICHNET
                        ' wird. DrawCircle liess den Layer AUSSERHALB des Kreises unangetastet -
                        ' die Blur-Kopie des gesamten Pads (2,35r je Seite) wurde beim Restore
                        ' voll einkomposittiert. Das volle Rechteck zeichnen: der Radial-Verlauf
                        ' ist ausserhalb r transparent und nullt den Layer dort.
                        canvas.DrawRect(bounds, maskPaint)
                    End Using
                End Using
                canvas.Restore()
            End Using
        End Sub

        Private Shared Function FastBoxBlurRegion(source As SKBitmap,
                                                  left As Integer, top As Integer,
                                                  width As Integer, height As Integer,
                                                  radius As Integer) As SKBitmap
            If source Is Nothing Then Return Nothing
            If width <= 0 OrElse height <= 0 Then Return Nothing
            radius = Math.Max(1, Math.Min(radius, Math.Max(width, height)))
            Dim count = width * height
            If count <= 0 OrElse count > Integer.MaxValue \ 4 Then Return Nothing
            Dim byteCount = count * 4
            Dim rowByteCount = width * 4
            Dim src = ArrayPool(Of Byte).Shared.Rent(byteCount)
            Dim tmp = ArrayPool(Of Byte).Shared.Rent(byteCount)
            Dim output = ArrayPool(Of Byte).Shared.Rent(byteCount)
            Dim result As SKBitmap = Nothing

            ' Drei gepoolte Byte-Puffer ersetzen zwoelf Integer-Arrays. Der Algorithmus bleibt
            ' derselbe zweistufige Box-Blur inklusive seiner Randbehandlung; pro Patch-Pixel
            ' sinkt der verwaltete Arbeitsbereich damit von 48 auf 12 Byte und wird zwischen
            ' den Spots wiederverwendet, statt den GC bei jedem Mauspunkt zu belasten.
            Dim rawSupported = source.BytesPerPixel = 4 AndAlso source.GetPixels() <> IntPtr.Zero
            Try
                If rawSupported Then
                    Dim basePtr = source.GetPixels()
                    Dim srcStride = source.RowBytes
                    For y = 0 To height - 1
                        Marshal.Copy(IntPtr.Add(basePtr, (top + y) * srcStride + left * 4),
                                     src, y * rowByteCount, rowByteCount)
                    Next
                Else
                    For y = 0 To height - 1
                        Dim row = y * rowByteCount
                        For x = 0 To width - 1
                            Dim c = source.GetPixel(left + x, top + y)
                            Dim o = row + x * 4
                            src(o) = c.Red
                            src(o + 1) = c.Green
                            src(o + 2) = c.Blue
                            src(o + 3) = c.Alpha
                        Next
                    Next
                End If

                For y = 0 To height - 1
                    Dim row = y * rowByteCount
                    Dim sum0 As Integer = 0, sum1 As Integer = 0, sum2 As Integer = 0, sum3 As Integer = 0
                    Dim samples As Integer = 0
                    For x = 0 To Math.Min(width - 1, radius)
                        Dim o = row + x * 4
                        sum0 += src(o) : sum1 += src(o + 1) : sum2 += src(o + 2) : sum3 += src(o + 3)
                        samples += 1
                    Next
                    For x = 0 To width - 1
                        Dim o = x * 4
                        If x - radius - 1 >= 0 Then
                            Dim removeOffset = row + (x - radius - 1) * 4
                            sum0 -= src(removeOffset) : sum1 -= src(removeOffset + 1)
                            sum2 -= src(removeOffset + 2) : sum3 -= src(removeOffset + 3)
                            samples -= 1
                        End If
                        If x + radius < width AndAlso x > 0 Then
                            Dim addOffset = row + (x + radius) * 4
                            sum0 += src(addOffset) : sum1 += src(addOffset + 1)
                            sum2 += src(addOffset + 2) : sum3 += src(addOffset + 3)
                            samples += 1
                        End If
                        Dim outOffset = row + o
                        tmp(outOffset) = CByte(sum0 \ samples)
                        tmp(outOffset + 1) = CByte(sum1 \ samples)
                        tmp(outOffset + 2) = CByte(sum2 \ samples)
                        tmp(outOffset + 3) = CByte(sum3 \ samples)
                    Next
                Next

                For x = 0 To width - 1
                    Dim columnOffset = x * 4
                    Dim sum0 As Integer = 0, sum1 As Integer = 0, sum2 As Integer = 0, sum3 As Integer = 0
                    Dim samples As Integer = 0
                    For y = 0 To Math.Min(height - 1, radius)
                        Dim o = y * rowByteCount + columnOffset
                        sum0 += tmp(o) : sum1 += tmp(o + 1) : sum2 += tmp(o + 2) : sum3 += tmp(o + 3)
                        samples += 1
                    Next
                    For y = 0 To height - 1
                        If y - radius - 1 >= 0 Then
                            Dim removeOffset = (y - radius - 1) * rowByteCount + columnOffset
                            sum0 -= tmp(removeOffset) : sum1 -= tmp(removeOffset + 1)
                            sum2 -= tmp(removeOffset + 2) : sum3 -= tmp(removeOffset + 3)
                            samples -= 1
                        End If
                        If y + radius < height AndAlso y > 0 Then
                            Dim addOffset = (y + radius) * rowByteCount + columnOffset
                            sum0 += tmp(addOffset) : sum1 += tmp(addOffset + 1)
                            sum2 += tmp(addOffset + 2) : sum3 += tmp(addOffset + 3)
                            samples += 1
                        End If
                        Dim outOffset = y * rowByteCount + columnOffset
                        output(outOffset) = CByte(sum0 \ samples)
                        output(outOffset + 1) = CByte(sum1 \ samples)
                        output(outOffset + 2) = CByte(sum2 \ samples)
                        output(outOffset + 3) = CByte(sum3 \ samples)
                    Next
                Next

                result = New SKBitmap(width, height, source.ColorType, source.AlphaType)
                Dim resultRaw = result.BytesPerPixel = 4 AndAlso result.GetPixels() <> IntPtr.Zero
                If rawSupported AndAlso resultRaw Then
                    Dim basePtr = result.GetPixels()
                    Dim dstStride = result.RowBytes
                    For y = 0 To height - 1
                        Marshal.Copy(output, y * rowByteCount,
                                     IntPtr.Add(basePtr, y * dstStride), rowByteCount)
                    Next
                Else
                    For y = 0 To height - 1
                        Dim row = y * rowByteCount
                        For x = 0 To width - 1
                            Dim o = row + x * 4
                            result.SetPixel(x, y, New SKColor(output(o), output(o + 1), output(o + 2), output(o + 3)))
                        Next
                    Next
                End If

                Return result
            Catch
                result?.Dispose()
                Throw
            Finally
                ArrayPool(Of Byte).Shared.Return(src)
                ArrayPool(Of Byte).Shared.Return(tmp)
                ArrayPool(Of Byte).Shared.Return(output)
            End Try
        End Function

        Private Shared Sub DrawHealingRegion(result As SKBitmap, canvas As SKCanvas, source As SKBitmap,
                                             spots As IReadOnlyList(Of RetouchSpot),
                                             sourceWidthPixels As Integer, sourceHeightPixels As Integer)
            If result Is Nothing OrElse source Is Nothing OrElse spots Is Nothing OrElse spots.Count = 0 Then Return

            Dim scaleX As Single = 1.0F
            Dim scaleY As Single = 1.0F
            If sourceWidthPixels > 0 AndAlso sourceHeightPixels > 0 AndAlso source.Width > 0 AndAlso source.Height > 0 Then
                scaleX = source.Width / CSng(sourceWidthPixels)
                scaleY = source.Height / CSng(sourceHeightPixels)
            End If
            Dim radiusScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))

            Dim scaled As New List(Of (X As Single, Y As Single, Radius As Single, Flow As Single, EffectCeiling As Single))()
            Dim left = source.Width
            Dim top = source.Height
            Dim right = 0
            Dim bottom = 0
            Dim maxRadius As Single = 1.0F
            For Each spot In spots
                If spot Is Nothing Then Continue For
                Dim radius = Clamp(spot.RadiusPixels * radiusScale + 2.0F, 1, Math.Max(source.Width, source.Height))
                Dim cx = Clamp(spot.XPixels * scaleX, 0, source.Width)
                Dim cy = Clamp(spot.YPixels * scaleY, 0, source.Height)
                Dim flow = Clamp(spot.FlowPercent, 0, 100) / 100.0F
                Dim effectCeiling = Clamp(spot.OpacityPercent, 0, 100) / 100.0F *
                                    Clamp(spot.StrengthPercent, 0, 100) / 100.0F
                If flow <= 0.001F OrElse effectCeiling <= 0.001F Then Continue For
                scaled.Add((cx, cy, radius, flow, effectCeiling))
                maxRadius = Math.Max(maxRadius, radius)
                left = Math.Min(left, CInt(Math.Floor(cx - radius - 2)))
                top = Math.Min(top, CInt(Math.Floor(cy - radius - 2)))
                right = Math.Max(right, CInt(Math.Ceiling(cx + radius + 2)))
                bottom = Math.Max(bottom, CInt(Math.Ceiling(cy + radius + 2)))
            Next
            If scaled.Count = 0 Then Return

            left = Math.Max(0, left)
            top = Math.Max(0, top)
            right = Math.Min(source.Width, right)
            bottom = Math.Min(source.Height, bottom)
            Dim width = right - left
            Dim height = bottom - top
            If width <= 0 OrElse height <= 0 Then Return

            ' Zwei Masken mit getrennten Aufgaben:
            ' - defectMask beschreibt die komplette Pinselgeometrie. Sie verhindert, dass die
            '   Quellensuche noch Pixel aus dem zu reparierenden Defekt verwendet.
            ' - blendMask beschreibt die sichtbare Wirkung. Fluss darf sich entlang des Zugs
            '   aufbauen, Deckkraft und Staerke deckeln danach aber den gesamten Zug.
            Using defectMask = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using blendMask = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Using defectCanvas = New SKCanvas(defectMask)
                        Using blendCanvas = New SKCanvas(blendMask)
                            defectCanvas.Clear(SKColors.Transparent)
                            blendCanvas.Clear(SKColors.Transparent)
                            defectCanvas.Translate(-left, -top)
                            blendCanvas.Translate(-left, -top)
                            For Each s In scaled
                                Using defectShader = SKShader.CreateRadialGradient(New SKPoint(s.X, s.Y), s.Radius,
                                                                                   {SKColors.White, SKColors.White, SKColors.Transparent},
                                                                                   RetouchFeatherStops, SKShaderTileMode.Clamp)
                                    Using defectPaint = New SKPaint With {.Shader = defectShader, .IsAntialias = True, .BlendMode = SKBlendMode.SrcOver}
                                        defectCanvas.DrawCircle(s.X, s.Y, s.Radius, defectPaint)
                                    End Using
                                End Using
                                Using blendShader = SKShader.CreateRadialGradient(New SKPoint(s.X, s.Y), s.Radius,
                                                                                  {SKColors.White.WithAlpha(CByte(255 * s.Flow)),
                                                                                   SKColors.White.WithAlpha(CByte(255 * s.Flow)),
                                                                                   SKColors.Transparent},
                                                                                  RetouchFeatherStops, SKShaderTileMode.Clamp)
                                    Using blendPaint = New SKPaint With {.Shader = blendShader, .IsAntialias = True, .BlendMode = SKBlendMode.SrcOver}
                                        blendCanvas.DrawCircle(s.X, s.Y, s.Radius, blendPaint)
                                    End Using
                                End Using
                            Next
                            blendCanvas.ResetMatrix()
                            Dim effectCeiling = scaled(0).EffectCeiling
                            Using ceilingPaint = New SKPaint With {
                                .Color = SKColors.White.WithAlpha(CByte(255 * effectCeiling)),
                                .BlendMode = SKBlendMode.DstIn,
                                .IsAntialias = False
                            }
                                blendCanvas.DrawRect(New SKRect(0, 0, width, height), ceilingPaint)
                            End Using
                        End Using
                    End Using

                    If DrawInpaintedHealingRegion(result, defectMask, blendMask, left, top) Then
                        Return
                    End If

                    Dim targetAverage = AverageRegionSurroundingColor(source, defectMask, left, top, maxRadius)
                    If Not targetAverage.HasValue Then
                        For Each s In scaled
                            Dim visibleFlow = s.Flow * s.EffectCeiling
                            Dim fill = AverageSurroundingColor(source, s.X, s.Y, s.Radius)
                            If fill.HasValue Then DrawSoftDisc(canvas, s.X, s.Y, s.Radius, fill.Value, visibleFlow)
                        Next
                        Return
                    End If

                    Dim sample = FindHealingRegionPatch(source, defectMask, left, top, width, height, maxRadius, targetAverage.Value)
                    If Not sample.Found Then
                        For Each s In scaled
                            DrawSoftDisc(canvas, s.X, s.Y, s.Radius, targetAverage.Value, s.Flow * s.EffectCeiling)
                        Next
                        Return
                    End If

                    DrawAdjustedHealingRegion(result, source, blendMask, left, top, sample.Left, sample.Top,
                                              targetAverage.Value, sample.Average)
                End Using
            End Using
        End Sub

        ''' <paramref name="resolutionLongEdge"/>: laengste Kante des BILDES in der Aufloesung, in
        ''' der hier gerechnet wird - daran haengt der Patch-Radius. 0 heisst "aus result ableiten"
        ''' und gilt fuer den nativen Weg, wo result das ganze Bild ist. Der skalierte Weg reicht den
        ''' heruntergerechneten Wert durch, weil sein result nur ein verkleinerter Ausschnitt ist.
        Private Shared Function DrawInpaintedHealingRegion(result As SKBitmap, defectMask As SKBitmap, blendMask As SKBitmap,
                                                           targetLeft As Integer, targetTop As Integer,
                                                           Optional resolutionLongEdge As Integer = 0) As Boolean
            If result Is Nothing OrElse defectMask Is Nothing OrElse blendMask Is Nothing OrElse
               defectMask.Width <= 0 OrElse defectMask.Height <= 0 OrElse
               defectMask.Width <> blendMask.Width OrElse defectMask.Height <> blendMask.Height Then Return False

            If Math.Max(defectMask.Width, defectMask.Height) > HealingMaxNativeExtent Then
                Return DrawScaledInpaintedHealingRegion(result, defectMask, blendMask, targetLeft, targetTop,
                                                       EffectiveHealingLongEdge(result, resolutionLongEdge))
            End If

            Dim width = defectMask.Width
            Dim height = defectMask.Height
            Dim count = width * height
            Dim maskAlpha(count - 1) As Byte
            Dim blendAlpha(count - 1) As Byte
            Dim filled(count - 1) As Boolean
            Dim queued(count - 1) As Boolean
            Dim maskedCount = 0

            For maskY = 0 To height - 1
                Dim y = targetTop + maskY
                For mx = 0 To width - 1
                    Dim index = maskY * width + mx
                    Dim alpha = defectMask.GetPixel(mx, maskY).Alpha
                    maskAlpha(index) = NormalizeHealingMaskAlpha(alpha)
                    blendAlpha(index) = blendMask.GetPixel(mx, maskY).Alpha
                    Dim isMasked = alpha > 8 AndAlso y >= 0 AndAlso y < result.Height AndAlso
                                   targetLeft + mx >= 0 AndAlso targetLeft + mx < result.Width
                    filled(index) = Not isMasked
                    If isMasked Then maskedCount += 1
                Next
            Next

            If maskedCount = 0 Then Return False

            ' Nur Zielregion plus Suchrand kopieren. Die fruehere CloneBitmap(result)-Kopie
            ' allokierte bei 49 MP rund 196 MB pro Zug, obwohl nur ein kleiner Fleck veraendert wird.
            Dim workMargin = HealingSearchMargin
            Dim workLeft = Math.Max(0, targetLeft - workMargin)
            Dim workTop = Math.Max(0, targetTop - workMargin)
            Dim workRight = Math.Min(result.Width, targetLeft + width + workMargin)
            Dim workBottom = Math.Min(result.Height, targetTop + height + workMargin)
            Dim workWidth = workRight - workLeft
            Dim workHeight = workBottom - workTop
            If workWidth <= 0 OrElse workHeight <= 0 Then Return False

            Using work = New SKBitmap(workWidth, workHeight, result.ColorType, result.AlphaType)
                Using workCanvas = New SKCanvas(work)
                    workCanvas.DrawBitmap(result,
                                          New SKRect(workLeft, workTop, workRight, workBottom),
                                          New SKRect(0, 0, workWidth, workHeight))
                End Using
                Dim localTargetLeft = targetLeft - workLeft
                Dim localTargetTop = targetTop - workTop

                If DrawPatchBasedInpaintedHealingRegion(EffectiveHealingLongEdge(result, resolutionLongEdge),
                                                       result, work, maskAlpha, blendAlpha,
                                                         localTargetLeft, localTargetTop, width, height,
                                                         workLeft, workTop) Then
                    Return True
                End If

                Dim queue As New Queue(Of Integer)()
                For maskY = 0 To height - 1
                    For mx = 0 To width - 1
                        Dim index = maskY * width + mx
                        If filled(index) OrElse Not HasFilledNeighbor(filled, width, height, mx, maskY) Then Continue For
                        queue.Enqueue(index)
                        queued(index) = True
                    Next
                Next

                Dim repairedCount = 0
                While queue.Count > 0
                    Dim index = queue.Dequeue()
                    queued(index) = False
                    If filled(index) Then Continue While

                    Dim mx = index Mod width
                    Dim maskY = index \ width
                    Dim x = localTargetLeft + mx
                    Dim y = localTargetTop + maskY
                    Dim average = AverageUnmaskedRays(work, maskAlpha, localTargetLeft, localTargetTop, width, height, mx, maskY)
                    If Not average.HasValue Then
                        average = AverageFilledNeighborhood(work, filled, localTargetLeft, localTargetTop, width, height, mx, maskY)
                    End If
                    If Not average.HasValue Then Continue While

                    work.SetPixel(x, y, average.Value)
                    filled(index) = True
                    repairedCount += 1

                    For oy = -1 To 1
                        For ox = -1 To 1
                            If ox = 0 AndAlso oy = 0 Then Continue For
                            Dim nx = mx + ox
                            Dim ny = maskY + oy
                            If nx < 0 OrElse ny < 0 OrElse nx >= width OrElse ny >= height Then Continue For
                            Dim ni = ny * width + nx
                            If filled(ni) OrElse queued(ni) OrElse maskAlpha(ni) <= 8 Then Continue For
                            queue.Enqueue(ni)
                            queued(ni) = True
                        Next
                    Next
                End While

                If repairedCount = 0 Then Return False
                If ShouldSmoothInpaintedRegion(work, maskAlpha, localTargetLeft, localTargetTop, width, height) Then
                    SmoothInpaintedRegion(work, maskAlpha, localTargetLeft, localTargetTop, width, height)
                End If

                For maskY = 0 To height - 1
                    Dim workY = localTargetTop + maskY
                    Dim resultY = workTop + workY
                    If resultY < 0 OrElse resultY >= result.Height Then Continue For
                    For mx = 0 To width - 1
                        Dim index = maskY * width + mx
                        Dim alpha = blendAlpha(index)
                        If alpha <= 8 OrElse Not filled(index) Then Continue For
                        Dim workX = localTargetLeft + mx
                        Dim resultX = workLeft + workX
                        If resultX < 0 OrElse resultX >= result.Width Then Continue For

                        Dim localAlpha = Clamp(alpha / 255.0F, 0.0F, 1.0F)
                        If localAlpha <= 0.001F Then Continue For
                        Dim target = result.GetPixel(resultX, resultY)
                        Dim repaired = work.GetPixel(workX, workY)
                        result.SetPixel(resultX, resultY, New SKColor(
                            BlendByte(target.Red, repaired.Red, localAlpha),
                            BlendByte(target.Green, repaired.Green, localAlpha),
                            BlendByte(target.Blue, repaired.Blue, localAlpha),
                            BlendByte(target.Alpha, repaired.Alpha, localAlpha)))
                    Next
                Next
            End Using

            Return True
        End Function

        ''' <summary>Aufloesungsnormalisierter Heal-Pfad fuer sehr grosse Masken. Feste 13x13-Patches,
        ''' 160 Durchlaeufe und das Suchbudget reichen bei einem 1200-px-Ziel gut aus, bei einer
        ''' mehrere tausend Pixel langen 49-MP-Maske faellt die Mitte dagegen in den weichen Fallback.
        ''' Die Struktur wird deshalb in einem begrenzten Kontext rekonstruiert und anschliessend mit
        ''' der unveraenderten Vollaufloesungs-Deckungsmaske zurueckkomponiert.</summary>
        Private Shared Function DrawScaledInpaintedHealingRegion(result As SKBitmap,
                                                                 defectMask As SKBitmap,
                                                                 blendMask As SKBitmap,
                                                                 targetLeft As Integer,
                                                                 targetTop As Integer,
                                                                 resolutionLongEdge As Integer) As Boolean
            Dim scale = HealingMaxNativeExtent / CDbl(Math.Max(defectMask.Width, defectMask.Height))
            If scale <= 0.0 OrElse scale >= 1.0 Then Return False

            ' Der Suchrand soll auch NACH dem Downscale dieselben 154 Arbeits-Pixel behalten.
            Dim contextMargin = Math.Max(HealingSearchMargin,
                                         CInt(Math.Ceiling(HealingSearchMargin / scale)))
            Dim contextLeft = Math.Max(0, targetLeft - contextMargin)
            Dim contextTop = Math.Max(0, targetTop - contextMargin)
            Dim contextRight = Math.Min(result.Width, targetLeft + defectMask.Width + contextMargin)
            Dim contextBottom = Math.Min(result.Height, targetTop + defectMask.Height + contextMargin)
            Dim contextWidth = contextRight - contextLeft
            Dim contextHeight = contextBottom - contextTop
            If contextWidth <= 0 OrElse contextHeight <= 0 Then Return False

            Dim scaledContextWidth = Math.Max(1, CInt(Math.Round(contextWidth * scale)))
            Dim scaledContextHeight = Math.Max(1, CInt(Math.Round(contextHeight * scale)))
            Dim scaleX = scaledContextWidth / CDbl(contextWidth)
            Dim scaleY = scaledContextHeight / CDbl(contextHeight)
            Dim scaledTargetLeft = CInt(Math.Round((targetLeft - contextLeft) * scaleX))
            Dim scaledTargetTop = CInt(Math.Round((targetTop - contextTop) * scaleY))
            Dim scaledMaskWidth = Math.Max(1, CInt(Math.Round(defectMask.Width * scaleX)))
            Dim scaledMaskHeight = Math.Max(1, CInt(Math.Round(defectMask.Height * scaleY)))

            Using scaledContext = New SKBitmap(scaledContextWidth, scaledContextHeight,
                                               result.ColorType, result.AlphaType)
                Using scaledCanvas = New SKCanvas(scaledContext)
                    DrawBitmapSampled(scaledCanvas, result,
                                      New SKRect(contextLeft, contextTop, contextRight, contextBottom),
                                      New SKRect(0, 0, scaledContextWidth, scaledContextHeight),
                                      SamplingHigh, Nothing)
                End Using

                Using scaledDefect = New SKBitmap(scaledMaskWidth, scaledMaskHeight,
                                                  SKColorType.Bgra8888, SKAlphaType.Premul)
                    Using maskCanvas = New SKCanvas(scaledDefect)
                        maskCanvas.Clear(SKColors.Transparent)
                        DrawBitmapSampled(maskCanvas, defectMask,
                                          New SKRect(0, 0, defectMask.Width, defectMask.Height),
                                          New SKRect(0, 0, scaledMaskWidth, scaledMaskHeight),
                                          SamplingHigh, Nothing)
                    End Using

                    ' Innerhalb des kleinen Arbeitsbilds voll rekonstruieren. Die sichtbare
                    ' Staerke/Deckkraft wird erst beim Rueckweg durch blendMask angewendet.
                    Using scaledFullBlend = New SKBitmap(scaledMaskWidth, scaledMaskHeight,
                                                         SKColorType.Bgra8888, SKAlphaType.Premul)
                        Using fullBlendCanvas = New SKCanvas(scaledFullBlend)
                            fullBlendCanvas.Clear(SKColors.White)
                        End Using
                        If Not DrawInpaintedHealingRegion(scaledContext, scaledDefect, scaledFullBlend,
                                                         scaledTargetLeft, scaledTargetTop,
                                                         Math.Max(1, CInt(resolutionLongEdge * scale))) Then Return False

                        Using resultCanvas = New SKCanvas(result)
                            Dim bounds = New SKRect(targetLeft, targetTop,
                                                    targetLeft + defectMask.Width,
                                                    targetTop + defectMask.Height)
                            resultCanvas.SaveLayer(bounds, Nothing)
                            DrawBitmapSampled(resultCanvas, scaledContext,
                                              New SKRect(scaledTargetLeft, scaledTargetTop,
                                                         scaledTargetLeft + scaledMaskWidth,
                                                         scaledTargetTop + scaledMaskHeight),
                                              bounds, SamplingHigh, Nothing)
                            Using maskPaint = New SKPaint With {
                                .BlendMode = SKBlendMode.DstIn,
                                .IsAntialias = False
                            }
                                resultCanvas.DrawBitmap(blendMask, targetLeft, targetTop, maskPaint)
                            End Using
                            resultCanvas.Restore()
                        End Using
                    End Using
                End Using
            End Using

            Return True
        End Function

        Private Shared Function DrawPatchBasedInpaintedHealingRegion(resolutionLongEdge As Integer,
                                                                     result As SKBitmap, work As SKBitmap,
                                                                     maskAlpha As Byte(), blendAlpha As Byte(),
                                                                     targetLeft As Integer, targetTop As Integer,
                                                                     width As Integer, height As Integer,
                                                                     resultOriginX As Integer, resultOriginY As Integer) As Boolean
            If result Is Nothing OrElse work Is Nothing OrElse maskAlpha Is Nothing OrElse blendAlpha Is Nothing OrElse
               maskAlpha.Length <> blendAlpha.Length OrElse width <= 0 OrElse height <= 0 Then Return False

            Dim known(width * height - 1) As Boolean
            Dim remaining = 0
            For i = 0 To known.Length - 1
                known(i) = maskAlpha(i) <= 8
                If Not known(i) Then remaining += 1
            Next
            If remaining = 0 Then Return False

            Dim repairExtent = Math.Max(width, height)
            ' BEFUND: groessere Patches (Radius 6) tragen mehr Struktur pro Kopie und brauchen
            ' weniger Suchen - bezahlbar, seit die Kandidaten vorberechnet sind.
            Dim patchRadius = HealingPatchRadiusFor(resolutionLongEdge)
            Dim maxPasses = Math.Min(repairExtent + patchRadius * 2, 160)
            Dim maxPatchCopiesPerPass = If(repairExtent > 360, 72, If(repairExtent > 240, 96, If(repairExtent > 120, 96, 64)))
            Dim repaired = 0

            ' QUALITAET: Pixelpuffer ueber den gesamten Suchbereich (Zielrechteck +
            ' maximale Suchreichweite) - damit wird HealingPatchScore rein managed und die Suche kann
            ' sich DICHTE leisten (kleinere Schrittweite, mehr Kandidaten, Verfeinerung immer), statt
            ' angrenzende Struktur wegen GetPixel-Kosten grob zu ueberspringen. CopyHealingPatch
            ' spiegelt seine Schreibzugriffe in den Puffer, damit Scores im selben Pass frisch bleiben.
            Dim searchMargin = HealingSearchBaseMargin + patchRadius + 8
            Dim pixels = RegionPixelBuffer.FromRegion(work, targetLeft - searchMargin, targetTop - searchMargin,
                                                      targetLeft + width - 1 + searchMargin, targetTop + height - 1 + searchMargin)

            ' BEFUND: gueltige Quell-Patches EINMAL vorberechnen (Praefixsumme + Bucket-Grid);
            ' die Suche pro Randpixel zieht daraus nur noch die naechsten echten Kandidaten.
            Dim candidates = New HealSourceCandidates(maskAlpha, targetLeft, targetTop, width, height,
                                                      patchRadius, searchMargin, work.Width, work.Height)
            Dim candidateScratch As New List(Of (X As Integer, Y As Integer))(256)
            If candidates.Count = 0 Then Return False

            ' BUDGET: Die Patch-Suche ist fuer FLECKEN gebaut. Ein langer Pinselzug
            ' erzeugt eine Riesen-Region, in der fast alle Kandidaten verworfen werden (Umgebung
            ' selbst maskiert) - ungedeckelt wurden daraus Milliarden Array-Reads (Log: 64 s fuer
            ' EINEN Zug). Das Budget zaehlt geprüfte Kandidaten ueber die GESAMTE Region; ist es
            ' erschoepft, fuellt der bestehende Fallback (FillRemainingInpaintedPixels + Rand-
            ' Blending) den Rest - sichtbar weicher, aber in Sekundenbruchteilen statt Minuten.
            Dim searchBudget As Long = 3_000_000

            For pass = 0 To maxPasses - 1
                Dim boundary As New List(Of Integer)()
                For maskY = 0 To height - 1
                    For mx = 0 To width - 1
                        Dim index = maskY * width + mx
                        If known(index) Then Continue For
                        If HasKnownNeighbor(known, width, height, mx, maskY) Then boundary.Add(index)
                    Next
                Next

                If boundary.Count = 0 Then Exit For
                OrderHealingBoundaryByKnownNeighbors(boundary, known, width, height)
                Dim changedThisPass = 0
                Dim patchCopiesThisPass = 0
                For Each index In boundary
                    If known(index) Then Continue For
                    If searchBudget <= 0 Then Exit For
                    Dim mx = index Mod width
                    Dim maskY = index \ width
                    Dim sourcePatch = FindBestHealingSourcePatch(work, maskAlpha, known, targetLeft, targetTop,
                                                                 width, height, mx, maskY, patchRadius, pixels,
                                                                 candidates, candidateScratch, searchBudget)
                    If Not sourcePatch.Found Then Continue For

                    changedThisPass += CopyHealingPatch(work, maskAlpha, known, targetLeft, targetTop,
                                                        width, height, mx, maskY, sourcePatch.X, sourcePatch.Y, patchRadius, pixels)
                    patchCopiesThisPass += 1
                    If patchCopiesThisPass >= maxPatchCopiesPerPass Then Exit For
                Next

                If searchBudget <= 0 Then Exit For
                If changedThisPass = 0 Then Exit For
                repaired += changedThisPass
                remaining -= changedThisPass
                If remaining <= 0 Then Exit For
            Next

            If remaining > 0 Then
                Dim filledByFallback = FillRemainingInpaintedPixels(work, maskAlpha, known, targetLeft, targetTop, width, height)
                repaired += filledByFallback
                remaining -= filledByFallback
            End If

            If repaired = 0 Then Return False
            BlendInpaintedBoundary(work, maskAlpha, targetLeft, targetTop, width, height)
            For maskY = 0 To height - 1
                Dim y = targetTop + maskY
                If y < 0 OrElse y >= work.Height Then Continue For
                For mx = 0 To width - 1
                    Dim index = maskY * width + mx
                    If blendAlpha(index) <= 8 OrElse Not known(index) Then Continue For
                    Dim workX = targetLeft + mx
                    Dim resultX = resultOriginX + workX
                    Dim resultY = resultOriginY + y
                    If resultX < 0 OrElse resultX >= result.Width OrElse
                       resultY < 0 OrElse resultY >= result.Height Then Continue For

                    Dim localAlpha = Clamp(blendAlpha(index) / 255.0F, 0.0F, 1.0F)
                    Dim target = result.GetPixel(resultX, resultY)
                    Dim repairedColor = work.GetPixel(workX, y)
                    result.SetPixel(resultX, resultY, New SKColor(
                        BlendByte(target.Red, repairedColor.Red, localAlpha),
                        BlendByte(target.Green, repairedColor.Green, localAlpha),
                        BlendByte(target.Blue, repairedColor.Blue, localAlpha),
                        BlendByte(target.Alpha, repairedColor.Alpha, localAlpha)))
                Next
            Next

            Return True
        End Function

        Private Shared Function FillRemainingInpaintedPixels(work As SKBitmap, maskAlpha As Byte(), known As Boolean(),
                                                             targetLeft As Integer, targetTop As Integer,
                                                             width As Integer, height As Integer) As Integer
            If work Is Nothing OrElse maskAlpha Is Nothing OrElse known Is Nothing Then Return 0

            Dim queued(width * height - 1) As Boolean
            Dim queue As New Queue(Of Integer)()
            For maskY = 0 To height - 1
                For mx = 0 To width - 1
                    Dim index = maskY * width + mx
                    If known(index) OrElse maskAlpha(index) <= 8 Then Continue For
                    If Not HasKnownNeighbor(known, width, height, mx, maskY) Then Continue For
                    queue.Enqueue(index)
                    queued(index) = True
                Next
            Next

            Dim repaired = 0
            While queue.Count > 0
                Dim index = queue.Dequeue()
                queued(index) = False
                If known(index) OrElse maskAlpha(index) <= 8 Then Continue While

                Dim mx = index Mod width
                Dim maskY = index \ width
                Dim x = targetLeft + mx
                Dim y = targetTop + maskY
                If x < 0 OrElse y < 0 OrElse x >= work.Width OrElse y >= work.Height Then
                    known(index) = True
                    Continue While
                End If

                Dim average = AverageFilledNeighborhood(work, known, targetLeft, targetTop, width, height, mx, maskY)
                If Not average.HasValue Then average = AverageUnmaskedRays(work, maskAlpha, targetLeft, targetTop, width, height, mx, maskY)
                If Not average.HasValue Then Continue While

                work.SetPixel(x, y, average.Value)
                known(index) = True
                repaired += 1

                For oy = -1 To 1
                    For ox = -1 To 1
                        If ox = 0 AndAlso oy = 0 Then Continue For
                        Dim nx = mx + ox
                        Dim ny = maskY + oy
                        If nx < 0 OrElse ny < 0 OrElse nx >= width OrElse ny >= height Then Continue For
                        Dim ni = ny * width + nx
                        If known(ni) OrElse queued(ni) OrElse maskAlpha(ni) <= 8 Then Continue For
                        queue.Enqueue(ni)
                        queued(ni) = True
                    Next
                Next
            End While

            Return repaired
        End Function

        Private Shared Sub BlendInpaintedBoundary(work As SKBitmap, maskAlpha As Byte(),
                                                  targetLeft As Integer, targetTop As Integer,
                                                  width As Integer, height As Integer)
            If work Is Nothing OrElse maskAlpha Is Nothing Then Return

            Dim nextColors(width * height - 1) As SKColor
            Dim hasNext(width * height - 1) As Boolean
            For maskY = 0 To height - 1
                Dim y = targetTop + maskY
                If y < 0 OrElse y >= work.Height Then Continue For
                For mx = 0 To width - 1
                    Dim index = maskY * width + mx
                    If maskAlpha(index) <= 8 OrElse Not IsMaskBoundary(maskAlpha, width, height, mx, maskY) Then Continue For
                    Dim x = targetLeft + mx
                    If x < 0 OrElse x >= work.Width Then Continue For

                    Dim avg = AverageBoundaryBlendColor(work, maskAlpha, targetLeft, targetTop, width, height, mx, maskY)
                    If avg.HasValue Then
                        nextColors(index) = avg.Value
                        hasNext(index) = True
                    End If
                Next
            Next

            For maskY = 0 To height - 1
                Dim y = targetTop + maskY
                If y < 0 OrElse y >= work.Height Then Continue For
                For mx = 0 To width - 1
                    Dim index = maskY * width + mx
                    If Not hasNext(index) Then Continue For
                    Dim x = targetLeft + mx
                    If x < 0 OrElse x >= work.Width Then Continue For
                    Dim current = work.GetPixel(x, y)
                    Dim blended = nextColors(index)
                    work.SetPixel(x, y, New SKColor(
                        BlendByte(current.Red, blended.Red, 0.45F),
                        BlendByte(current.Green, blended.Green, 0.45F),
                        BlendByte(current.Blue, blended.Blue, 0.45F),
                        BlendByte(current.Alpha, blended.Alpha, 0.45F)))
                Next
            Next
        End Sub

        Private Shared Function IsMaskBoundary(maskAlpha As Byte(), width As Integer, height As Integer,
                                               mx As Integer, my As Integer) As Boolean
            For oy = -1 To 1
                For ox = -1 To 1
                    If ox = 0 AndAlso oy = 0 Then Continue For
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    If nx < 0 OrElse ny < 0 OrElse nx >= width OrElse ny >= height Then Return True
                    If maskAlpha(ny * width + nx) <= 8 Then Return True
                Next
            Next
            Return False
        End Function

        Private Shared Function AverageBoundaryBlendColor(work As SKBitmap, maskAlpha As Byte(),
                                                          targetLeft As Integer, targetTop As Integer,
                                                          width As Integer, height As Integer,
                                                          mx As Integer, my As Integer) As SKColor?
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim weightSum = 0
            For oy = -2 To 2
                For ox = -2 To 2
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    Dim x = targetLeft + nx
                    Dim y = targetTop + ny
                    If x < 0 OrElse y < 0 OrElse x >= work.Width OrElse y >= work.Height Then Continue For

                    Dim weight = If(Math.Abs(ox) <= 1 AndAlso Math.Abs(oy) <= 1, 3, 1)
                    If nx >= 0 AndAlso ny >= 0 AndAlso nx < width AndAlso ny < height AndAlso
                       maskAlpha(ny * width + nx) > 8 Then
                        weight += 1
                    End If

                    Dim c = work.GetPixel(x, y)
                    sr += CInt(c.Red) * weight
                    sg += CInt(c.Green) * weight
                    sb += CInt(c.Blue) * weight
                    sa += CInt(c.Alpha) * weight
                    weightSum += weight
                Next
            Next
            If weightSum = 0 Then Return Nothing
            Return New SKColor(CByte(sr \ weightSum), CByte(sg \ weightSum),
                               CByte(sb \ weightSum), CByte(sa \ weightSum))
        End Function

        ''' <summary>Reihenfolge der Fuellfront: die Pixel mit den meisten bekannten Nachbarn zuerst.
        '''
        ''' GEPRUEFT UND VERWORFEN: ein zusaetzlicher STRUKTUR-Term nach Art der Isophoten-Prioritaet
        ''' (Helligkeitsgradient ueber die bekannten Nachbarn, Anteil entlang der Frontnormalen,
        ''' multiplikativ auf die Nachbarzahl). Er sollte den reproduzierten Fehler an einer Grenze
        ''' zwischen zwei Bereichen beheben (Horizont Himmel/Wiese: die Linie bricht ein, Himmel
        ''' blutet nach unten). Gemessen brachte er dort nur 22,6 -> 19,7 px Abweichung bei
        ''' unveraendertem Maximum, also keine Loesung, und am 50-MP-Foto zog er den Kontrast im
        ''' reparierten Bereich von 16,0 auf 17,5 weg vom Original (16,7). Auch eine strikte
        ''' Prioritaetsreihenfolge (Kopierbudget je Durchgang von 96 auf 6) blieb bei 18,9 px.
        ''' Der Grund liegt tiefer: eine Reihenfolge nuetzt wenig, solange die Patch-BEWERTUNG nur
        ''' den bekannten Teil des Zielfensters sieht - am oberen Rand ist der bekannt Teil reiner
        ''' Himmel, also gewinnt ein reiner Himmel-Patch und wird nach unten kopiert.</summary>
        Private Shared Sub OrderHealingBoundaryByKnownNeighbors(boundary As List(Of Integer), known As Boolean(),
                                                                width As Integer, height As Integer)
            If boundary Is Nothing OrElse boundary.Count < 2 Then Return
            boundary.Sort(Function(a, b)
                              Dim ax = a Mod width
                              Dim ay = a \ width
                              Dim bx = b Mod width
                              Dim by = b \ width
                              Return CountKnownNeighbors(known, width, height, bx, by).CompareTo(
                                     CountKnownNeighbors(known, width, height, ax, ay))
                          End Function)
        End Sub

        Private Shared Function CountKnownNeighbors(known As Boolean(), width As Integer, height As Integer,
                                                    mx As Integer, my As Integer) As Integer
            Dim count = 0
            For oy = -1 To 1
                For ox = -1 To 1
                    If ox = 0 AndAlso oy = 0 Then Continue For
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    If nx < 0 OrElse ny < 0 OrElse nx >= width OrElse ny >= height Then
                        count += 1
                    ElseIf known(ny * width + nx) Then
                        count += 1
                    End If
                Next
            Next
            Return count
        End Function

        ''' <summary>BEFUND: Vorberechnete Quell-Kandidaten fuer die Heal-Patch-Suche. Die alte
        ''' Blindsuche prüfte pro Randpixel ein Raster um den ZIELPUNKT - mitten in einem breiten Zug
        ''' ist dort alles selbst maskiert, fast alle Kandidaten wurden verworfen (Budget verbrannt)
        ''' und die Zugmitte fiel auf den strukturlosen Mittelwert-Fallback zurück. Hier wird EINMAL
        ''' pro Region über eine 2D-Präfixsumme der Maske ein Gitter aller Positionen bestimmt, deren
        ''' kompletter Patch unmaskiert im Bild liegt, räumlich in 32-px-Buckets abgelegt. Die Suche
        ''' zieht dann pro Randpixel nur noch die NÄCHSTEN echten Kandidaten - ohne harten
        ''' Radius-Deckel, die Mitte bekommt Struktur von den Rändern.</summary>
        Private NotInheritable Class HealSourceCandidates
            Private Const BucketSize As Integer = 32
            Private ReadOnly _buckets As Dictionary(Of Long, List(Of (X As Integer, Y As Integer)))
            Private ReadOnly _integral As Integer()   ' Präfixsumme "maskiert" über das Suchfenster
            Private ReadOnly _winLeft As Integer
            Private ReadOnly _winTop As Integer
            Private ReadOnly _winWidth As Integer
            Private ReadOnly _winHeight As Integer
            Public ReadOnly Count As Integer

            Public Sub New(maskAlpha As Byte(), targetLeft As Integer, targetTop As Integer,
                           width As Integer, height As Integer, patchRadius As Integer,
                           margin As Integer, bitmapWidth As Integer, bitmapHeight As Integer)
                _winLeft = Math.Max(0, targetLeft - margin)
                _winTop = Math.Max(0, targetTop - margin)
                Dim winRight = Math.Min(bitmapWidth - 1, targetLeft + width - 1 + margin)
                Dim winBottom = Math.Min(bitmapHeight - 1, targetTop + height - 1 + margin)
                _winWidth = Math.Max(0, winRight - _winLeft + 1)
                _winHeight = Math.Max(0, winBottom - _winTop + 1)
                _buckets = New Dictionary(Of Long, List(Of (X As Integer, Y As Integer)))()
                If _winWidth <= 0 OrElse _winHeight <= 0 Then
                    _integral = Array.Empty(Of Integer)()
                    Return
                End If

                ' Präfixsumme: integral(y,x) = Anzahl maskierter Pixel im Rechteck [0..x)x[0..y).
                _integral = New Integer((_winWidth + 1) * (_winHeight + 1) - 1) {}
                Dim stride = _winWidth + 1
                For y = 0 To _winHeight - 1
                    Dim rowSum = 0
                    Dim absY = _winTop + y
                    Dim maskRow = (absY - targetTop) * width
                    For x = 0 To _winWidth - 1
                        Dim absX = _winLeft + x
                        Dim masked = 0
                        If absX >= targetLeft AndAlso absY >= targetTop AndAlso
                           absX < targetLeft + width AndAlso absY < targetTop + height AndAlso
                           maskAlpha(maskRow + (absX - targetLeft)) > 8 Then
                            masked = 1
                        End If
                        rowSum += masked
                        _integral((y + 1) * stride + (x + 1)) = _integral(y * stride + (x + 1)) + rowSum
                    Next
                Next

                ' Kandidaten-Gitter (Schritt 3): kompletter Patch im Bild UND unmaskiert.
                Dim total = 0
                For y = patchRadius To _winHeight - 1 - patchRadius Step 3
                    For x = patchRadius To _winWidth - 1 - patchRadius Step 3
                        Dim absX = _winLeft + x
                        Dim absY = _winTop + y
                        If absX < patchRadius OrElse absY < patchRadius OrElse
                           absX >= bitmapWidth - patchRadius OrElse absY >= bitmapHeight - patchRadius Then Continue For
                        If Not IsPatchClear(absX, absY, patchRadius) Then Continue For
                        Dim key = BucketKey(absX, absY)
                        Dim list As List(Of (X As Integer, Y As Integer)) = Nothing
                        If Not _buckets.TryGetValue(key, list) Then
                            list = New List(Of (X As Integer, Y As Integer))()
                            _buckets(key) = list
                        End If
                        list.Add((absX, absY))
                        total += 1
                    Next
                Next
                Count = total
            End Sub

            Private Shared Function BucketKey(x As Integer, y As Integer) As Long
                Return (CLng(y \ BucketSize) << 24) Or CLng(x \ BucketSize)
            End Function

            ''' Patch um (x,y) komplett unmaskiert? O(1) über die Präfixsumme (Bereiche ausserhalb
            ''' des Fensters gelten als unmaskiert - dort liegt keine Maske).
            Public Function IsPatchClear(x As Integer, y As Integer, patchRadius As Integer) As Boolean
                If _winWidth <= 0 Then Return True
                Dim x0 = Math.Max(0, x - patchRadius - _winLeft)
                Dim y0 = Math.Max(0, y - patchRadius - _winTop)
                Dim x1 = Math.Min(_winWidth - 1, x + patchRadius - _winLeft)
                Dim y1 = Math.Min(_winHeight - 1, y + patchRadius - _winTop)
                If x1 < x0 OrElse y1 < y0 Then Return True
                Dim stride = _winWidth + 1
                Dim masked = _integral((y1 + 1) * stride + (x1 + 1)) -
                             _integral(y0 * stride + (x1 + 1)) -
                             _integral((y1 + 1) * stride + x0) +
                             _integral(y0 * stride + x0)
                Return masked = 0
            End Function

            ''' Sammelt bis zu maxCount Kandidaten in ringförmig wachsenden Bucket-Schalen um das
            ''' Ziel - grob nach Nähe geordnet; die Feinordnung erledigt der Distanz-Malus im Score.
            Public Sub CollectNearest(targetX As Integer, targetY As Integer, maxCount As Integer,
                                      results As List(Of (X As Integer, Y As Integer)))
                results.Clear()
                If _buckets.Count = 0 Then Return
                Dim centerBx = targetX \ BucketSize
                Dim centerBy = targetY \ BucketSize
                Dim maxRing = Math.Max(_winWidth, _winHeight) \ BucketSize + 2
                For ring = 0 To maxRing
                    For by = centerBy - ring To centerBy + ring
                        For bx = centerBx - ring To centerBx + ring
                            If bx < 0 OrElse by < 0 Then Continue For
                            ' Nur die Schale, nicht das Innere (das lieferten schon kleinere Ringe).
                            If ring > 0 AndAlso Math.Abs(bx - centerBx) <> ring AndAlso Math.Abs(by - centerBy) <> ring Then Continue For
                            Dim list As List(Of (X As Integer, Y As Integer)) = Nothing
                            If Not _buckets.TryGetValue((CLng(by) << 24) Or CLng(bx), list) Then Continue For
                            results.AddRange(list)
                        Next
                    Next
                    If results.Count >= maxCount Then Exit For
                Next
                If results.Count > maxCount Then results.RemoveRange(maxCount, results.Count - maxCount)
            End Sub
        End Class

        ''' QUALITAET: Suche über vorberechnete Kandidaten (HealSourceCandidates) -
        ''' das Budget fliesst komplett in echte Struktur-Vergleiche, und ohne harten Radius-Deckel
        ''' erreicht auch die Mitte breiter Züge die Struktur der Ränder. Der Distanz-Malus im Score
        ''' lässt bei gleicher Ähnlichkeit weiterhin die NÄCHSTE Struktur gewinnen.
        Private Shared Function FindBestHealingSourcePatch(work As SKBitmap, maskAlpha As Byte(), known As Boolean(),
                                                           targetLeft As Integer, targetTop As Integer,
                                                           width As Integer, height As Integer,
                                                           mx As Integer, my As Integer,
                                                           patchRadius As Integer,
                                                           pixels As RegionPixelBuffer,
                                                           candidates As HealSourceCandidates,
                                                           scratch As List(Of (X As Integer, Y As Integer)),
                                                           ByRef searchBudget As Long) As (X As Integer, Y As Integer, Found As Boolean)
            Dim targetX = targetLeft + mx
            Dim targetY = targetTop + my
            Dim extent = Math.Max(width, height)
            Dim maxCandidates = If(extent > 360, 140, 220)

            candidates.CollectNearest(targetX, targetY, maxCandidates, scratch)

            Dim bestX = 0
            Dim bestY = 0
            Dim bestScore = Double.MaxValue
            Dim found = False
            For Each candidate In scratch
                If Math.Abs(candidate.X - targetX) <= patchRadius AndAlso Math.Abs(candidate.Y - targetY) <= patchRadius Then Continue For
                searchBudget -= 1
                If searchBudget <= 0 Then Exit For
                Dim score = HealingPatchScore(work, maskAlpha, known, targetLeft, targetTop,
                                              width, height, mx, my, candidate.X, candidate.Y, patchRadius, pixels)
                If score < bestScore Then
                    bestScore = score
                    bestX = candidate.X
                    bestY = candidate.Y
                    found = True
                End If
            Next

            If found AndAlso searchBudget > 0 Then
                ' Pixelgenaue Verfeinerung um den besten Treffer (das Kandidaten-Gitter hat Schritt 3).
                For sy = Math.Max(patchRadius, bestY - 2) To Math.Min(work.Height - patchRadius - 1, bestY + 2)
                    For sx = Math.Max(patchRadius, bestX - 2) To Math.Min(work.Width - patchRadius - 1, bestX + 2)
                        If sx = bestX AndAlso sy = bestY Then Continue For
                        If Math.Abs(sx - targetX) <= patchRadius AndAlso Math.Abs(sy - targetY) <= patchRadius Then Continue For
                        If Not candidates.IsPatchClear(sx, sy, patchRadius) Then Continue For
                        searchBudget -= 1
                        Dim score = HealingPatchScore(work, maskAlpha, known, targetLeft, targetTop,
                                                      width, height, mx, my, sx, sy, patchRadius, pixels)
                        If score < bestScore Then
                            bestScore = score
                            bestX = sx
                            bestY = sy
                        End If
                    Next
                Next
            End If

            Return (bestX, bestY, found)
        End Function

        Private Shared Function HealingPatchScore(work As SKBitmap, maskAlpha As Byte(), known As Boolean(),
                                                  targetLeft As Integer, targetTop As Integer,
                                                  width As Integer, height As Integer,
                                                  mx As Integer, my As Integer,
                                                  sx As Integer, sy As Integer,
                                                  patchRadius As Integer,
                                                  pixels As RegionPixelBuffer) As Double
            Dim score = 0.0
            Dim count = 0
            Dim targetX = targetLeft + mx
            Dim targetY = targetTop + my

            For oy = -patchRadius To patchRadius
                Dim oySq = oy * oy
                Dim ty = targetY + oy
                Dim py = sy + oy
                If ty < 0 OrElse ty >= work.Height OrElse py < 0 OrElse py >= work.Height Then Continue For
                For ox = -patchRadius To patchRadius
                    If ox * ox + oySq > patchRadius * patchRadius Then Continue For
                    Dim tx = targetX + ox
                    Dim px = sx + ox
                    If tx < 0 OrElse tx >= work.Width OrElse px < 0 OrElse px >= work.Width Then Continue For

                    Dim lx = mx + ox
                    Dim ly = my + oy
                    Dim targetKnown = True
                    If lx >= 0 AndAlso ly >= 0 AndAlso lx < width AndAlso ly < height Then
                        targetKnown = known(ly * width + lx)
                    End If
                    If Not targetKnown Then Continue For

                    Dim distance = Math.Max(Math.Abs(ox), Math.Abs(oy))
                    Dim weight = If(distance <= 1, 5.0, If(distance <= 3, 2.0, 1.0))
                    Dim targetColor = If(pixels IsNot Nothing AndAlso pixels.Contains(tx, ty), pixels.GetColor(tx, ty), work.GetPixel(tx, ty))
                    Dim patchColor = If(pixels IsNot Nothing AndAlso pixels.Contains(px, py), pixels.GetColor(px, py), work.GetPixel(px, py))
                    score += ColorDistanceSquared(targetColor, patchColor) * weight
                    count += CInt(weight)
                Next
            Next

            If count < Math.Max(8, patchRadius * patchRadius \ 2) Then Return Double.MaxValue
            Dim dx = sx - targetX
            Dim dy = sy - targetY
            Dim distancePenalty = Math.Sqrt(dx * dx + dy * dy) * 1.8
            Return score / count + distancePenalty
        End Function

        Private Shared Function CopyHealingPatch(work As SKBitmap, maskAlpha As Byte(), known As Boolean(),
                                                 targetLeft As Integer, targetTop As Integer,
                                                 width As Integer, height As Integer,
                                                 mx As Integer, my As Integer,
                                                 sx As Integer, sy As Integer,
                                                 patchRadius As Integer,
                                                 Optional pixels As RegionPixelBuffer = Nothing) As Integer
            Dim copied = 0
            Dim targetX = targetLeft + mx
            Dim targetY = targetTop + my
            ' UEBERLAPPUNG: bereits gefuellte Zielpixel werden mit einem STETIGEN Gewicht
            ' ueberblendet, das von der Patch-Mitte (voll) zum Patch-Rand (null) ausläuft.
            ' Ungefuellte bekommen weiterhin die volle Kopie (kein Durchbluten des Defekts).
            '
            ' BEFUND, der dazu gefuehrt hat: vorher blieben gefuellte Pixel INNERHALB von
            ' Radius-1,5 unangetastet und nur der aeussere Ring wurde hart 50/50 gemischt - also
            ' genau umgekehrt zur Verlaesslichkeit (die Patch-Mitte traegt die beste Struktur, der
            ' Rand die schlechteste) und mit ZWEI Stufen im Verlauf statt einer Rampe. Die Naehte
            ' daraus sammeln sich dort, wo zuletzt gefuellt wird: auf der MITTELLINIE des Zuges.
            ' Gemessen an einem 120x2000-Zug ueber Wolkentextur lag die Hochfrequenz dort bei 2,23
            ' gegen 1,27 in der Nachbarschaft - sichtbar als unsaubere Bahn in der Zugmitte.
            Dim featherRadius = Math.Max(1.0F, CSng(patchRadius))

            For oy = -patchRadius To patchRadius
                Dim oySq = oy * oy
                Dim y = targetY + oy
                Dim py = sy + oy
                If y < 0 OrElse y >= work.Height OrElse py < 0 OrElse py >= work.Height Then Continue For
                For ox = -patchRadius To patchRadius
                    Dim distSq = ox * ox + oySq
                    If distSq > patchRadius * patchRadius Then Continue For
                    Dim lx = mx + ox
                    Dim ly = my + oy
                    If lx < 0 OrElse ly < 0 OrElse lx >= width OrElse ly >= height Then Continue For
                    Dim index = ly * width + lx
                    If maskAlpha(index) <= 8 Then Continue For

                    Dim x = targetX + ox
                    Dim px = sx + ox
                    If x < 0 OrElse x >= work.Width OrElse px < 0 OrElse px >= work.Width Then Continue For

                    If known(index) Then
                        Dim w = 1.0F - CSng(Math.Sqrt(distSq)) / featherRadius
                        If w > 0.0F Then
                            ' Smoothstep: am Patch-Rand laeuft auch die ABLEITUNG auf null aus,
                            ' sonst bleibt dort eine sichtbare Knickkante stehen.
                            w = w * w * (3.0F - 2.0F * w)
                            Dim existing = work.GetPixel(x, y)
                            Dim incoming = work.GetPixel(px, py)
                            Dim blended = New SKColor(
                                BlendByte(existing.Red, incoming.Red, w),
                                BlendByte(existing.Green, incoming.Green, w),
                                BlendByte(existing.Blue, incoming.Blue, w),
                                existing.Alpha)
                            work.SetPixel(x, y, blended)
                            If pixels IsNot Nothing AndAlso pixels.Contains(x, y) Then pixels.SetColor(x, y, blended)
                        End If
                        Continue For
                    End If

                    Dim sourceColor = work.GetPixel(px, py)
                    work.SetPixel(x, y, sourceColor)
                    ' Puffer synchron halten - Scores im selben Pass sehen sonst den alten (defekten)
                    ' Inhalt unter frisch kopierten Pixeln.
                    If pixels IsNot Nothing AndAlso pixels.Contains(x, y) Then pixels.SetColor(x, y, sourceColor)
                    known(index) = True
                    copied += 1
                Next
            Next

            Return copied
        End Function

        Private Shared Function IsOriginalKnownPatch(maskAlpha As Byte(),
                                                     targetLeft As Integer, targetTop As Integer,
                                                     width As Integer, height As Integer,
                                                     sx As Integer, sy As Integer,
                                                     patchRadius As Integer) As Boolean
            For oy = -patchRadius To patchRadius
                Dim y = sy + oy
                Dim oySq = oy * oy
                For ox = -patchRadius To patchRadius
                    If ox * ox + oySq > patchRadius * patchRadius Then Continue For
                    Dim x = sx + ox
                    Dim mx = x - targetLeft
                    Dim my = y - targetTop
                    If mx >= 0 AndAlso my >= 0 AndAlso mx < width AndAlso my < height AndAlso
                       maskAlpha(my * width + mx) > 8 Then Return False
                Next
            Next
            Return True
        End Function

        Private Shared Function HasKnownNeighbor(known As Boolean(), width As Integer, height As Integer,
                                                 mx As Integer, my As Integer) As Boolean
            For oy = -1 To 1
                For ox = -1 To 1
                    If ox = 0 AndAlso oy = 0 Then Continue For
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    If nx < 0 OrElse ny < 0 OrElse nx >= width OrElse ny >= height Then Return True
                    If known(ny * width + nx) Then Return True
                Next
            Next
            Return False
        End Function

        Private Shared Function ShouldSmoothInpaintedRegion(work As SKBitmap, maskAlpha As Byte(),
                                                            targetLeft As Integer, targetTop As Integer,
                                                            width As Integer, height As Integer) As Boolean
            Dim stepSize = Math.Max(1, Math.Max(width, height) \ 28)
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0
            Dim sr2 As Long = 0, sg2 As Long = 0, sb2 As Long = 0
            Dim count = 0

            For maskY = 0 To height - 1 Step stepSize
                Dim y = targetTop + maskY
                If y < 0 OrElse y >= work.Height Then Continue For
                For mx = 0 To width - 1 Step stepSize
                    If maskAlpha(maskY * width + mx) < 255 Then Continue For
                    Dim x = targetLeft + mx
                    If x < 0 OrElse x >= work.Width Then Continue For
                    Dim c = work.GetPixel(x, y)
                    sr += c.Red : sg += c.Green : sb += c.Blue
                    sr2 += CInt(c.Red) * CInt(c.Red)
                    sg2 += CInt(c.Green) * CInt(c.Green)
                    sb2 += CInt(c.Blue) * CInt(c.Blue)
                    count += 1
                Next
            Next

            If count < 12 Then Return False
            Dim ar = CDbl(sr) / count
            Dim ag = CDbl(sg) / count
            Dim ab = CDbl(sb) / count
            Dim variance = Math.Max(0.0, CDbl(sr2) / count - ar * ar) +
                           Math.Max(0.0, CDbl(sg2) / count - ag * ag) +
                           Math.Max(0.0, CDbl(sb2) / count - ab * ab)
            Return variance < 95.0
        End Function

        Private Shared Sub SmoothInpaintedRegion(work As SKBitmap, maskAlpha As Byte(),
                                                 targetLeft As Integer, targetTop As Integer,
                                                 width As Integer, height As Integer)
            If work Is Nothing OrElse maskAlpha Is Nothing OrElse width <= 0 OrElse height <= 0 Then Return

            Dim iterations = If(Math.Max(width, height) > 72, 4, 3)
            ' EINMAL angelegt und je Durchlauf wiederverwendet. Vorher entstanden beide Puffer bei
            ' jeder der drei bis vier Iterationen neu; bei einer grossen Reparaturstelle sind das
            ' vier Wuerfe Speicher, die der Sammler hinterher wieder einsammeln muss, ohne dass ein
            ' einziges Pixel anders herauskaeme.
            '
            ' Geleert werden muss dabei nur hasNext: nextColors wird ausschliesslich dort gelesen,
            ' wo hasNext wahr ist. Eine zweite Leerung waere Arbeit fuer nichts.
            Dim nextColors(width * height - 1) As SKColor
            Dim hasNext(width * height - 1) As Boolean
            For iteration = 1 To iterations
                If iteration > 1 Then Array.Clear(hasNext, 0, hasNext.Length)

                For maskY = 0 To height - 1
                    Dim y = targetTop + maskY
                    If y < 0 OrElse y >= work.Height Then Continue For
                    For mx = 0 To width - 1
                        Dim index = maskY * width + mx
                        If maskAlpha(index) < 255 Then Continue For
                        Dim x = targetLeft + mx
                        If x < 0 OrElse x >= work.Width Then Continue For

                        Dim smoothed = AverageRepairNeighborhood(work, maskAlpha, targetLeft, targetTop, width, height, mx, maskY)
                        If smoothed.HasValue Then
                            nextColors(index) = smoothed.Value
                            hasNext(index) = True
                        End If
                    Next
                Next

                For maskY = 0 To height - 1
                    Dim y = targetTop + maskY
                    If y < 0 OrElse y >= work.Height Then Continue For
                    For mx = 0 To width - 1
                        Dim index = maskY * width + mx
                        If Not hasNext(index) Then Continue For
                        Dim x = targetLeft + mx
                        If x < 0 OrElse x >= work.Width Then Continue For
                        work.SetPixel(x, y, nextColors(index))
                    Next
                Next
            Next
        End Sub

        Private Shared Function AverageRepairNeighborhood(work As SKBitmap, maskAlpha As Byte(),
                                                          targetLeft As Integer, targetTop As Integer,
                                                          width As Integer, height As Integer,
                                                          mx As Integer, my As Integer) As SKColor?
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim weightSum = 0

            For oy = -2 To 2
                For ox = -2 To 2
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    Dim x = targetLeft + nx
                    Dim y = targetTop + ny
                    If x < 0 OrElse y < 0 OrElse x >= work.Width OrElse y >= work.Height Then Continue For

                    Dim distance = Math.Max(Math.Abs(ox), Math.Abs(oy))
                    Dim weight = If(distance = 0, 8, If(distance = 1, 4, 1))
                    If nx >= 0 AndAlso ny >= 0 AndAlso nx < width AndAlso ny < height Then
                        Dim alpha = maskAlpha(ny * width + nx)
                        If alpha <= 8 Then
                            weight = 1
                        ElseIf alpha < 255 Then
                            weight = 2
                        End If
                    End If

                    Dim c = work.GetPixel(x, y)
                    sr += CInt(c.Red) * weight
                    sg += CInt(c.Green) * weight
                    sb += CInt(c.Blue) * weight
                    sa += CInt(c.Alpha) * weight
                    weightSum += weight
                Next
            Next

            If weightSum = 0 Then Return Nothing
            Return New SKColor(CByte(sr \ weightSum), CByte(sg \ weightSum),
                               CByte(sb \ weightSum), CByte(sa \ weightSum))
        End Function

        Private Shared Function NormalizeHealingMaskAlpha(alpha As Byte) As Byte
            If alpha <= 8 Then Return 0
            If alpha >= 24 Then Return 255
            Return CByte(Math.Min(255, CInt(alpha) * 11))
        End Function

        Private Shared Function HasFilledNeighbor(filled As Boolean(), width As Integer, height As Integer,
                                                  mx As Integer, my As Integer) As Boolean
            For oy = -1 To 1
                For ox = -1 To 1
                    If ox = 0 AndAlso oy = 0 Then Continue For
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    If nx < 0 OrElse ny < 0 OrElse nx >= width OrElse ny >= height Then Return True
                    If filled(ny * width + nx) Then Return True
                Next
            Next
            Return False
        End Function

        Private Shared Function AverageFilledNeighborhood(work As SKBitmap, filled As Boolean(),
                                                          targetLeft As Integer, targetTop As Integer,
                                                          width As Integer, height As Integer,
                                                          mx As Integer, my As Integer) As SKColor?
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim weightSum = 0

            For oy = -2 To 2
                For ox = -2 To 2
                    If ox = 0 AndAlso oy = 0 Then Continue For
                    Dim nx = mx + ox
                    Dim ny = my + oy
                    Dim x = targetLeft + nx
                    Dim y = targetTop + ny
                    If x < 0 OrElse y < 0 OrElse x >= work.Width OrElse y >= work.Height Then Continue For
                    If nx >= 0 AndAlso ny >= 0 AndAlso nx < width AndAlso ny < height AndAlso
                       Not filled(ny * width + nx) Then Continue For

                    Dim distance = Math.Max(Math.Abs(ox), Math.Abs(oy))
                    Dim weight = If(distance <= 1, 4, 1)
                    Dim c = work.GetPixel(x, y)
                    sr += CInt(c.Red) * weight
                    sg += CInt(c.Green) * weight
                    sb += CInt(c.Blue) * weight
                    sa += CInt(c.Alpha) * weight
                    weightSum += weight
                Next
            Next

            If weightSum = 0 Then Return Nothing
            Return New SKColor(CByte(sr \ weightSum), CByte(sg \ weightSum),
                               CByte(sb \ weightSum), CByte(sa \ weightSum))
        End Function

        Private Shared Function AverageUnmaskedRays(work As SKBitmap, maskAlpha As Byte(),
                                                    targetLeft As Integer, targetTop As Integer,
                                                    width As Integer, height As Integer,
                                                    mx As Integer, my As Integer) As SKColor?
            Dim directions = {
                (X:=0, Y:=-1, Weight:=7),
                (X:=-1, Y:=0, Weight:=5),
                (X:=1, Y:=0, Weight:=5),
                (X:=0, Y:=1, Weight:=4),
                (X:=-1, Y:=-1, Weight:=3),
                (X:=1, Y:=-1, Weight:=3),
                (X:=-1, Y:=1, Weight:=2),
                (X:=1, Y:=1, Weight:=2)
            }
            Dim samples As New List(Of (Color As SKColor, Weight As Integer))()
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim weightSum = 0
            Dim maxDistance = Math.Max(width, height)

            For Each direction In directions
                For distance = 1 To maxDistance
                    Dim nx = mx + direction.X * distance
                    Dim ny = my + direction.Y * distance
                    Dim x = targetLeft + nx
                    Dim y = targetTop + ny
                    If x < 0 OrElse y < 0 OrElse x >= work.Width OrElse y >= work.Height Then Exit For

                    If nx >= 0 AndAlso ny >= 0 AndAlso nx < width AndAlso ny < height AndAlso
                       maskAlpha(ny * width + nx) > 8 Then Continue For

                    Dim weight = Math.Max(1, (direction.Weight * 256) \ (distance * distance))
                    Dim c = work.GetPixel(x, y)
                    samples.Add((c, weight))
                    Exit For
                Next
            Next

            If samples.Count = 0 Then Return Nothing
            Dim median = MedianSampleColor(samples)
            Dim accepted = 0
            For Each sample In samples
                If samples.Count > 3 AndAlso ColorDistanceSquared(sample.Color, median) > 62 * 62 Then Continue For
                sr += CInt(sample.Color.Red) * sample.Weight
                sg += CInt(sample.Color.Green) * sample.Weight
                sb += CInt(sample.Color.Blue) * sample.Weight
                sa += CInt(sample.Color.Alpha) * sample.Weight
                weightSum += sample.Weight
                accepted += 1
            Next

            If accepted = 0 Then
                For Each sample In samples
                    sr += CInt(sample.Color.Red) * sample.Weight
                    sg += CInt(sample.Color.Green) * sample.Weight
                    sb += CInt(sample.Color.Blue) * sample.Weight
                    sa += CInt(sample.Color.Alpha) * sample.Weight
                    weightSum += sample.Weight
                Next
            End If

            If weightSum = 0 Then Return Nothing
            Return New SKColor(CByte(sr \ weightSum), CByte(sg \ weightSum),
                               CByte(sb \ weightSum), CByte(sa \ weightSum))
        End Function

        Private Shared Function MedianSampleColor(samples As List(Of (Color As SKColor, Weight As Integer))) As SKColor
            Dim reds As New List(Of Integer)(samples.Count)
            Dim greens As New List(Of Integer)(samples.Count)
            Dim blues As New List(Of Integer)(samples.Count)
            Dim alphas As New List(Of Integer)(samples.Count)
            For Each sample In samples
                reds.Add(sample.Color.Red)
                greens.Add(sample.Color.Green)
                blues.Add(sample.Color.Blue)
                alphas.Add(sample.Color.Alpha)
            Next
            reds.Sort()
            greens.Sort()
            blues.Sort()
            alphas.Sort()
            Dim mid = samples.Count \ 2
            Return New SKColor(CByte(reds(mid)), CByte(greens(mid)), CByte(blues(mid)), CByte(alphas(mid)))
        End Function

        Private Shared Function FindHealingRegionPatch(source As SKBitmap, mask As SKBitmap,
                                                       targetLeft As Integer, targetTop As Integer,
                                                       width As Integer, height As Integer,
                                                       radius As Single,
                                                       targetAverage As SKColor) As (Left As Integer, Top As Integer, Average As SKColor, Found As Boolean)
            Dim cx = targetLeft + width / 2.0F
            Dim cy = targetTop + height / 2.0F
            Dim reach = Math.Max(width, height) / 2.0F + radius
            Dim distances = {Math.Max(reach * 1.12F, radius * 1.65F),
                             Math.Max(reach * 1.38F, radius * 2.05F),
                             Math.Max(reach * 1.68F, radius * 2.55F)}
            Dim bestLeft = 0
            Dim bestTop = 0
            Dim bestAverage = SKColors.Transparent
            Dim bestScore = Double.MaxValue
            Dim found = False
            Dim targetStats = SampleRingPatchStats(source, cx, cy, Math.Max(width, height) * 0.55F, Math.Max(width, height) * 0.95F)

            Dim avoid = New SKRectI(Math.Max(0, targetLeft - CInt(Math.Ceiling(radius))),
                                    Math.Max(0, targetTop - CInt(Math.Ceiling(radius))),
                                    Math.Min(source.Width, targetLeft + width + CInt(Math.Ceiling(radius))),
                                    Math.Min(source.Height, targetTop + height + CInt(Math.Ceiling(radius))))

            For Each distance In distances
                ' 24 Winkel pro Ring - der Teiler unten. "0 To 31" wiederholte die Winkel von
                ' i=0..7 exakt (i Mod 24) und rechnete pro Ring 8 von 32 Kandidaten samt
                ' Statistik/Randbewertung doppelt: ~33 % verschenkte Suchzeit ohne jede Wirkung
                ' aufs Ergebnis, ein Duplikat gewinnt nie gegen sich selbst.
                For i = 0 To 23
                    Dim angle = (Math.PI * 2.0 * i) / 24.0
                    Dim sampleCenterX = cx + CSng(Math.Cos(angle) * distance)
                    Dim sampleCenterY = cy + CSng(Math.Sin(angle) * distance)
                    Dim sampleLeft = CInt(Math.Round(sampleCenterX - width / 2.0F))
                    Dim sampleTop = CInt(Math.Round(sampleCenterY - height / 2.0F))
                    Dim sampleRect = New SKRectI(sampleLeft, sampleTop, sampleLeft + width, sampleTop + height)
                    If sampleRect.Left < 0 OrElse sampleRect.Top < 0 OrElse
                       sampleRect.Right >= source.Width OrElse sampleRect.Bottom >= source.Height Then Continue For
                    If RectsIntersect(avoid, sampleRect) Then Continue For

                    Dim stats = SampleMaskedSourceStats(source, mask, sampleLeft, sampleTop)
                    If stats.Count <= 0 Then Continue For
                    Dim boundaryScore = RegionBoundaryScore(source, targetLeft, targetTop, sampleLeft, sampleTop, width, height)
                    Dim colorDistance = ColorDistanceSquared(stats.Average, targetAverage)
                    Dim varianceDelta = If(targetStats.Count > 0, Math.Abs(stats.Variance - targetStats.Variance), 0.0)
                    Dim outlierPenalty = MaskedPatchOutlierPenalty(source, mask, sampleLeft, sampleTop, targetAverage)
                    Dim textureBonus = If(targetStats.Count > 0 AndAlso targetStats.Variance > 120.0,
                                          Math.Min(stats.Variance, targetStats.Variance) * 0.22,
                                          0.0)
                    Dim score = boundaryScore * 1.7 + colorDistance * 0.5 + varianceDelta * 0.035 + outlierPenalty - textureBonus
                    If score < bestScore Then
                        bestScore = score
                        bestLeft = sampleLeft
                        bestTop = sampleTop
                        bestAverage = stats.Average
                        found = True
                    End If
                Next
            Next

            Return (bestLeft, bestTop, bestAverage, found)
        End Function

        Private Shared Function RegionBoundaryScore(source As SKBitmap, targetLeft As Integer, targetTop As Integer,
                                                    sampleLeft As Integer, sampleTop As Integer,
                                                    width As Integer, height As Integer) As Double
            Dim stepSize = Math.Max(2, CInt(Math.Round(Math.Max(width, height) / 12.0)))
            Dim score = 0.0
            Dim count = 0
            For x = 0 To width - 1 Step stepSize
                AddBoundaryPairScore(source, targetLeft + x, targetTop - 1, sampleLeft + x, sampleTop - 1, score, count)
                AddBoundaryPairScore(source, targetLeft + x, targetTop + height, sampleLeft + x, sampleTop + height, score, count)
            Next
            For y = 0 To height - 1 Step stepSize
                AddBoundaryPairScore(source, targetLeft - 1, targetTop + y, sampleLeft - 1, sampleTop + y, score, count)
                AddBoundaryPairScore(source, targetLeft + width, targetTop + y, sampleLeft + width, sampleTop + y, score, count)
            Next
            If count = 0 Then Return Double.MaxValue
            Return score / count
        End Function

        Private Shared Function RectsIntersect(a As SKRectI, b As SKRectI) As Boolean
            Return a.Left < b.Right AndAlso b.Left < a.Right AndAlso
                   a.Top < b.Bottom AndAlso b.Top < a.Bottom
        End Function

        Private Shared Sub AddBoundaryPairScore(source As SKBitmap, tx As Integer, ty As Integer,
                                                sx As Integer, sy As Integer,
                                                ByRef score As Double, ByRef count As Integer)
            If tx < 0 OrElse ty < 0 OrElse tx >= source.Width OrElse ty >= source.Height OrElse
               sx < 0 OrElse sy < 0 OrElse sx >= source.Width OrElse sy >= source.Height Then Return
            score += ColorDistanceSquared(source.GetPixel(tx, ty), source.GetPixel(sx, sy))
            count += 1
        End Sub

        Private Shared Sub DrawAdjustedHealingRegion(result As SKBitmap, source As SKBitmap, mask As SKBitmap,
                                                     targetLeft As Integer, targetTop As Integer,
                                                     sampleLeft As Integer, sampleTop As Integer,
                                                     targetAverage As SKColor, sourceAverage As SKColor)
            Dim dr = Math.Max(-56, Math.Min(56, CInt(targetAverage.Red) - CInt(sourceAverage.Red)))
            Dim dg = Math.Max(-56, Math.Min(56, CInt(targetAverage.Green) - CInt(sourceAverage.Green)))
            Dim db = Math.Max(-56, Math.Min(56, CInt(targetAverage.Blue) - CInt(sourceAverage.Blue)))

            For maskY = 0 To mask.Height - 1
                Dim y = targetTop + maskY
                Dim sy = sampleTop + maskY
                If y < 0 OrElse y >= result.Height OrElse sy < 0 OrElse sy >= source.Height Then Continue For
                For mx = 0 To mask.Width - 1
                    Dim maskAlpha = mask.GetPixel(mx, maskY).Alpha
                    If maskAlpha = 0 Then Continue For

                    Dim x = targetLeft + mx
                    Dim sx = sampleLeft + mx
                    If x < 0 OrElse x >= result.Width OrElse sx < 0 OrElse sx >= source.Width Then Continue For

                    Dim localAlpha = maskAlpha / 255.0F
                    Dim sample = source.GetPixel(sx, sy)
                    sample = SuppressHealingOutlier(sample, sourceAverage, targetAverage)
                    Dim target = result.GetPixel(x, y)
                    If ColorDistanceSquared(target, targetAverage) > 90 * 90 Then
                        localAlpha = 1.0F
                    End If

                    result.SetPixel(x, y, New SKColor(
                        BlendByte(target.Red, ClampByte(CInt(sample.Red) + dr), localAlpha),
                        BlendByte(target.Green, ClampByte(CInt(sample.Green) + dg), localAlpha),
                        BlendByte(target.Blue, ClampByte(CInt(sample.Blue) + db), localAlpha),
                        BlendByte(target.Alpha, sample.Alpha, localAlpha)))
                Next
            Next
        End Sub

        Private Shared Function SampleMaskedSourceStats(source As SKBitmap, mask As SKBitmap,
                                                        sampleLeft As Integer, sampleTop As Integer) As (Average As SKColor, Variance As Double, Count As Integer)
            Dim stepSize = Math.Max(1, CInt(Math.Round(Math.Max(mask.Width, mask.Height) / 18.0)))
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0
            Dim sr2 As Long = 0, sg2 As Long = 0, sb2 As Long = 0
            Dim count = 0
            For maskY = 0 To mask.Height - 1 Step stepSize
                Dim sy = sampleTop + maskY
                If sy < 0 OrElse sy >= source.Height Then Continue For
                For mx = 0 To mask.Width - 1 Step stepSize
                    If mask.GetPixel(mx, maskY).Alpha < 24 Then Continue For
                    Dim sx = sampleLeft + mx
                    If sx < 0 OrElse sx >= source.Width Then Continue For
                    Dim c = source.GetPixel(sx, sy)
                    sr += c.Red : sg += c.Green : sb += c.Blue
                    sr2 += CInt(c.Red) * CInt(c.Red)
                    sg2 += CInt(c.Green) * CInt(c.Green)
                    sb2 += CInt(c.Blue) * CInt(c.Blue)
                    count += 1
                Next
            Next
            If count = 0 Then Return (SKColors.Transparent, Double.MaxValue, 0)
            Dim ar = CDbl(sr) / count
            Dim ag = CDbl(sg) / count
            Dim ab = CDbl(sb) / count
            Dim variance = Math.Max(0.0, (CDbl(sr2) / count - ar * ar) +
                                     (CDbl(sg2) / count - ag * ag) +
                                     (CDbl(sb2) / count - ab * ab))
            Return (New SKColor(CByte(Math.Round(ar)), CByte(Math.Round(ag)), CByte(Math.Round(ab))), variance, count)
        End Function

        Private Shared Function MaskedPatchOutlierPenalty(source As SKBitmap, mask As SKBitmap,
                                                          sampleLeft As Integer, sampleTop As Integer,
                                                          targetAverage As SKColor) As Double
            Dim stepSize = Math.Max(1, CInt(Math.Round(Math.Max(mask.Width, mask.Height) / 16.0)))
            Dim outliers = 0
            Dim count = 0
            For maskY = 0 To mask.Height - 1 Step stepSize
                Dim sy = sampleTop + maskY
                If sy < 0 OrElse sy >= source.Height Then Continue For
                For mx = 0 To mask.Width - 1 Step stepSize
                    If mask.GetPixel(mx, maskY).Alpha < 24 Then Continue For
                    Dim sx = sampleLeft + mx
                    If sx < 0 OrElse sx >= source.Width Then Continue For
                    count += 1
                    If ColorDistanceSquared(source.GetPixel(sx, sy), targetAverage) > 92 * 92 Then outliers += 1
                Next
            Next
            If count = 0 Then Return 1000000.0
            Dim ratio = CDbl(outliers) / count
            Return ratio * ratio * 220000.0
        End Function

        ''' PERF: Masken-Alphas und Quell-Region einmal puffern, Abtastung mit
        ''' Schrittweite - der alte Doppel-Scan (pro Region-Pixel eine GetPixel-Nachbarschaftssuche
        ''' auf der Maske) kostete bei grossen Heal-Flaechen zweistellige Millionen Interop-Calls.
        Private Shared Function AverageRegionSurroundingColor(source As SKBitmap, mask As SKBitmap,
                                                              left As Integer, top As Integer,
                                                              radius As Single) As SKColor?
            Dim reach = Math.Max(3, CInt(Math.Ceiling(radius * 1.5F)))
            Dim minX = Math.Max(0, left - reach)
            Dim minY = Math.Max(0, top - reach)
            Dim maxX = Math.Min(source.Width - 1, left + mask.Width + reach)
            Dim maxY = Math.Min(source.Height - 1, top + mask.Height + reach)
            If maxX < minX OrElse maxY < minY Then Return Nothing

            Dim maskBuffer = RegionPixelBuffer.FromRegion(mask, 0, 0, mask.Width - 1, mask.Height - 1)
            Dim sourceBuffer = RegionPixelBuffer.FromRegion(source, minX, minY, maxX, maxY)
            Dim stride = Math.Max(1, CInt(Math.Ceiling(Math.Max(maxX - minX + 1, maxY - minY + 1) / 128.0)))
            Dim neighborStep = Math.Max(1, reach \ 3)

            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim count = 0
            For y = minY To maxY Step stride
                For x = minX To maxX Step stride
                    Dim mx = x - left
                    Dim my = y - top
                    Dim insideMaskArea = mx >= 0 AndAlso my >= 0 AndAlso mx < mask.Width AndAlso my < mask.Height
                    If insideMaskArea AndAlso MaskAlphaAt(maskBuffer, mask, mx, my) > 0 Then Continue For

                    Dim nearMask = False
                    For oy = -reach To reach Step neighborStep
                        If nearMask Then Exit For
                        For ox = -reach To reach Step neighborStep
                            Dim nx = mx + ox
                            Dim ny = my + oy
                            If nx >= 0 AndAlso ny >= 0 AndAlso nx < mask.Width AndAlso ny < mask.Height AndAlso
                               MaskAlphaAt(maskBuffer, mask, nx, ny) > 32 Then
                                nearMask = True
                                Exit For
                            End If
                        Next
                    Next
                    If Not nearMask Then Continue For

                    Dim c = If(sourceBuffer IsNot Nothing, sourceBuffer.GetColor(x, y), source.GetPixel(x, y))
                    sr += c.Red : sg += c.Green : sb += c.Blue : sa += c.Alpha
                    count += 1
                Next
            Next
            If count = 0 Then Return Nothing
            Return New SKColor(CByte(sr \ count), CByte(sg \ count), CByte(sb \ count), CByte(sa \ count))
        End Function

        Private Shared Function MaskAlphaAt(buffer As RegionPixelBuffer, mask As SKBitmap, x As Integer, y As Integer) As Byte
            If buffer IsNot Nothing Then Return buffer.GetAlpha(x, y)
            Return mask.GetPixel(x, y).Alpha
        End Function

        Private Shared Function SuppressHealingOutlier(sample As SKColor, sourceAverage As SKColor, targetAverage As SKColor) As SKColor
            If ColorDistanceSquared(sample, targetAverage) <= 92 * 92 Then Return sample

            Dim repaired = New SKColor(
                BlendByte(sample.Red, sourceAverage.Red, 0.78F),
                BlendByte(sample.Green, sourceAverage.Green, 0.78F),
                BlendByte(sample.Blue, sourceAverage.Blue, 0.78F),
                sample.Alpha)
            If ColorDistanceSquared(repaired, targetAverage) <= ColorDistanceSquared(sample, targetAverage) Then Return repaired
            Return New SKColor(sourceAverage.Red, sourceAverage.Green, sourceAverage.Blue, sample.Alpha)
        End Function

        Private Shared Function SampleRingPatchStats(source As SKBitmap, cx As Single, cy As Single,
                                                     innerRadius As Single, outerRadius As Single) As (Average As SKColor, Variance As Double, Count As Integer)
            Dim stepSize = Math.Max(1, CInt(Math.Round((outerRadius - innerRadius) / 1.5F)))
            Dim innerSq = innerRadius * innerRadius
            Dim outerSq = outerRadius * outerRadius
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0
            Dim sr2 As Long = 0, sg2 As Long = 0, sb2 As Long = 0
            Dim count = 0

            For y = Math.Max(0, CInt(Math.Floor(cy - outerRadius))) To Math.Min(source.Height - 1, CInt(Math.Ceiling(cy + outerRadius))) Step stepSize
                Dim dy = CSng(y) - cy
                For x = Math.Max(0, CInt(Math.Floor(cx - outerRadius))) To Math.Min(source.Width - 1, CInt(Math.Ceiling(cx + outerRadius))) Step stepSize
                    Dim dx = CSng(x) - cx
                    Dim dSq = dx * dx + dy * dy
                    If dSq < innerSq OrElse dSq > outerSq Then Continue For
                    Dim c = source.GetPixel(x, y)
                    sr += c.Red : sg += c.Green : sb += c.Blue
                    sr2 += CInt(c.Red) * CInt(c.Red)
                    sg2 += CInt(c.Green) * CInt(c.Green)
                    sb2 += CInt(c.Blue) * CInt(c.Blue)
                    count += 1
                Next
            Next

            If count = 0 Then Return (SKColors.Transparent, Double.MaxValue, 0)
            Dim ar = CDbl(sr) / count
            Dim ag = CDbl(sg) / count
            Dim ab = CDbl(sb) / count
            Dim variance = Math.Max(0.0, (CDbl(sr2) / count - ar * ar) +
                                     (CDbl(sg2) / count - ag * ag) +
                                     (CDbl(sb2) / count - ab * ab))
            Return (New SKColor(CByte(Math.Round(ar)), CByte(Math.Round(ag)), CByte(Math.Round(ab))), variance, count)
        End Function

        Private Shared Function ColorDistanceSquared(a As SKColor, b As SKColor) As Double
            Dim dr = CInt(a.Red) - CInt(b.Red)
            Dim dg = CInt(a.Green) - CInt(b.Green)
            Dim db = CInt(a.Blue) - CInt(b.Blue)
            Return dr * dr + dg * dg + db * db
        End Function

        Private Shared Function MedianByte(values As List(Of Byte)) As Byte
            If values Is Nothing OrElse values.Count = 0 Then Return 0
            values.Sort()
            Return values(values.Count \ 2)
        End Function

        ''' <summary>Kompakter, rein lesender Regionen-Pixelpuffer: kopiert den
        ''' benoetigten Ausschnitt EINMAL zeilenweise in ein Byte-Array und liefert Farben rein
        ''' managed. SKBitmap.GetPixel ist ein Interop-Call pro Pixel - grosse Ring-/Regionsscans
        ''' (Verwischen, Heal-Umgebung, Patch-Suche) wurden damit minutenlang. Nur Bgra8888/
        ''' Rgba8888 (Pipeline-Standard); andere Formate -> FromRegion liefert Nothing, der
        ''' Aufrufer faellt auf GetPixel zurueck.</summary>
        Private NotInheritable Class RegionPixelBuffer
            Public ReadOnly Left As Integer
            Public ReadOnly Top As Integer
            Public ReadOnly Width As Integer
            Public ReadOnly Height As Integer
            Private ReadOnly _bytes As Byte()
            Private ReadOnly _rIdx As Integer
            Private ReadOnly _gIdx As Integer
            Private ReadOnly _bIdx As Integer

            Private Sub New(left As Integer, top As Integer, width As Integer, height As Integer,
                            bytes As Byte(), rIdx As Integer, gIdx As Integer, bIdx As Integer)
                Me.Left = left
                Me.Top = top
                Me.Width = width
                Me.Height = height
                _bytes = bytes
                _rIdx = rIdx
                _gIdx = gIdx
                _bIdx = bIdx
            End Sub

            ''' <summary>x0..x1/y0..y1 einschliesslich, werden aufs Bitmap geklemmt.</summary>
            Public Shared Function FromRegion(bmp As SKBitmap, x0 As Integer, y0 As Integer,
                                              x1 As Integer, y1 As Integer) As RegionPixelBuffer
                If bmp Is Nothing Then Return Nothing
                Dim rIdx, gIdx, bIdx As Integer
                Select Case bmp.ColorType
                    Case SKColorType.Bgra8888 : bIdx = 0 : gIdx = 1 : rIdx = 2
                    Case SKColorType.Rgba8888 : rIdx = 0 : gIdx = 1 : bIdx = 2
                    Case Else
                        Return Nothing
                End Select
                x0 = Math.Max(0, x0) : y0 = Math.Max(0, y0)
                x1 = Math.Min(bmp.Width - 1, x1) : y1 = Math.Min(bmp.Height - 1, y1)
                If x1 < x0 OrElse y1 < y0 Then Return Nothing
                Dim width = x1 - x0 + 1
                Dim height = y1 - y0 + 1
                Dim bytes(width * height * 4 - 1) As Byte
                Dim basePtr = bmp.GetPixels()
                If basePtr = IntPtr.Zero Then Return Nothing
                Dim srcStride = bmp.RowBytes
                For row = 0 To height - 1
                    Runtime.InteropServices.Marshal.Copy(IntPtr.Add(basePtr, (y0 + row) * srcStride + x0 * 4),
                                                         bytes, row * width * 4, width * 4)
                Next
                Return New RegionPixelBuffer(x0, y0, width, height, bytes, rIdx, gIdx, bIdx)
            End Function

            ''' <summary>Bitmap-Koordinaten; entpremultipliziert bei Alpha &lt; 255 (GetPixel-Verhalten).</summary>
            Public Function GetColor(x As Integer, y As Integer) As SKColor
                Dim idx = ((y - Top) * Width + (x - Left)) * 4
                Dim a = _bytes(idx + 3)
                Dim r = CInt(_bytes(idx + _rIdx))
                Dim g = CInt(_bytes(idx + _gIdx))
                Dim b = CInt(_bytes(idx + _bIdx))
                If a > 0 AndAlso a < 255 Then
                    r = Math.Min(255, r * 255 \ a)
                    g = Math.Min(255, g * 255 \ a)
                    b = Math.Min(255, b * 255 \ a)
                End If
                Return New SKColor(CByte(r), CByte(g), CByte(b), a)
            End Function

            Public Function GetAlpha(x As Integer, y As Integer) As Byte
                Return _bytes(((y - Top) * Width + (x - Left)) * 4 + 3)
            End Function

            Public Function Contains(x As Integer, y As Integer) As Boolean
                Return x >= Left AndAlso y >= Top AndAlso x < Left + Width AndAlso y < Top + Height
            End Function

            ''' <summary>Spiegel-Schreibzugriff (Heal-Patch-Kopien): haelt den Puffer synchron zum
            ''' Bitmap, damit Scores innerhalb eines Passes frisch kopierte Pixel sehen. Schreibt
            ''' premultipliziert (Puffer-Layout).</summary>
            Public Sub SetColor(x As Integer, y As Integer, color As SKColor)
                Dim idx = ((y - Top) * Width + (x - Left)) * 4
                Dim a = CInt(color.Alpha)
                Dim r = CInt(color.Red)
                Dim g = CInt(color.Green)
                Dim b = CInt(color.Blue)
                If a < 255 Then
                    r = r * a \ 255
                    g = g * a \ 255
                    b = b * a \ 255
                End If
                _bytes(idx + _rIdx) = CByte(r)
                _bytes(idx + _gIdx) = CByte(g)
                _bytes(idx + _bIdx) = CByte(b)
                _bytes(idx + 3) = CByte(a)
            End Sub
        End Class

        ''' Mittelt den Ring zwischen dem 1,25- und dem 2-fachen Radius um das Ziel - der Rückfall,
        ''' wenn keine Klonquelle gesetzt wurde. Liefert Nothing, wenn der Ring komplett außerhalb
        ''' des Bildes liegt.
        ''' PERF: Ringabtastung mit Regionen-Puffer + Schrittweite statt GetPixel
        ''' ueber JEDES Pixel - bei grossen Verwisch-Radien wurden aus einem Zug sonst Milliarden
        ''' Interop-Calls (Minuten CPU). ~10k Samples liefern denselben Mittelwert.
        Private Shared Function AverageSurroundingColor(source As SKBitmap, cx As Single, cy As Single, radius As Single,
                                                        Optional innerFactor As Single = 1.25F,
                                                        Optional outerFactor As Single = 2.0F) As SKColor?
            Dim inner = radius * innerFactor
            Dim outer = radius * outerFactor
            Dim innerSq = inner * inner
            Dim outerSq = outer * outer

            Dim icx = CInt(Math.Round(cx))
            Dim icy = CInt(Math.Round(cy))
            Dim reach = CInt(Math.Ceiling(outer))
            Dim stride = Math.Max(1, CInt(Math.Ceiling(reach / 56.0)))

            Dim y0 = Math.Max(0, icy - reach)
            Dim y1 = Math.Min(source.Height - 1, icy + reach)
            Dim x0 = Math.Max(0, icx - reach)
            Dim x1 = Math.Min(source.Width - 1, icx + reach)
            If y1 < y0 OrElse x1 < x0 Then Return Nothing
            Dim buffer = RegionPixelBuffer.FromRegion(source, x0, y0, x1, y1)

            Dim samples As New List(Of SKColor)()
            Dim sr As Long = 0, sg As Long = 0, sb As Long = 0, sa As Long = 0
            Dim count As Integer = 0
            For yy = y0 To y1 Step stride
                Dim dy = CSng(yy - icy)
                Dim dySq = dy * dy
                For xx = x0 To x1 Step stride
                    Dim dx = CSng(xx - icx)
                    Dim dSq = dx * dx + dySq
                    If dSq >= innerSq AndAlso dSq <= outerSq Then
                        Dim c = If(buffer IsNot Nothing, buffer.GetColor(xx, yy), source.GetPixel(xx, yy))
                        samples.Add(c)
                        sr += c.Red : sg += c.Green : sb += c.Blue : sa += c.Alpha
                        count += 1
                    End If
                Next
            Next
            If count = 0 Then Return Nothing

            If samples.Count >= 12 Then
                Dim reds As New List(Of Byte)(samples.Count)
                Dim greens As New List(Of Byte)(samples.Count)
                Dim blues As New List(Of Byte)(samples.Count)
                For Each sample In samples
                    reds.Add(sample.Red)
                    greens.Add(sample.Green)
                    blues.Add(sample.Blue)
                Next

                Dim median = New SKColor(MedianByte(reds), MedianByte(greens), MedianByte(blues), CByte(sa \ count))
                Dim filteredR As Long = 0, filteredG As Long = 0, filteredB As Long = 0, filteredA As Long = 0
                Dim filteredCount = 0
                For Each sample In samples
                    If ColorDistanceSquared(sample, median) > 54 * 54 Then Continue For
                    filteredR += sample.Red
                    filteredG += sample.Green
                    filteredB += sample.Blue
                    filteredA += sample.Alpha
                    filteredCount += 1
                Next

                If filteredCount >= Math.Max(6, samples.Count \ 5) Then
                    Return New SKColor(CByte(filteredR \ filteredCount), CByte(filteredG \ filteredCount),
                                       CByte(filteredB \ filteredCount), CByte(filteredA \ filteredCount))
                End If
            End If

            Return New SKColor(CByte(sr \ count), CByte(sg \ count), CByte(sb \ count), CByte(sa \ count))
        End Function

    End Class

End Namespace
