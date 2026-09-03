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

' Die Masken: aufbauen, vereinigen, in den Ein- und Ausgaberaum bringen, aus einer Auswahl
' einfrieren, als Verlauf oder weiche Kante rechnen, den Wirkbereich einer Auswahl begrenzen,
' und die Rasterarbeit daran (Zauberstab, Ausschneiden, Fuellen, Verschieben, Drehen, Spiegeln).
' Eigener Zustand: der Deckungs-Zwischenspeicher der Objektmasken.
' Herausgeloest am 2026-08-06 aus ImageProcessor.vb, Zeile fuer Zeile unveraendert.
' Die Regeln dahinter stehen in Audits/MASKEN_EBENEN_AUSWAHL.md.
Namespace Services

    Partial Public Class ImageProcessor

        ''' <summary>Die Wirkmasken aller Geschwister mit derselben Anpassung zu EINER vereinigen
        ''' (je Pixel das Maximum). Liefert Nothing, wenn es nichts zu vereinigen gibt - dann bleibt
        ''' die Maske der ersten Ebene unveraendert in Gebrauch.
        '''
        ''' Maximum, nicht Addition: die Deckung eines Pixels soll die STAERKSTE der beteiligten
        ''' Masken sein. Zwei Masken mit je 60 % ergeben 60 %, nicht 120 % - sonst waere die
        ''' Ueberschneidung wieder ein Sonderfall, nur ein leiserer.</summary>
        Private Shared Function MergeEffectMasks(geschwister As List(Of MaskedAdjustmentLayer),
                                                    ersteEbene As MaskedAdjustmentLayer,
                                                    ersteMaske As SKBitmap,
                                                    adj As ImageAdjustments,
                                                    masksById As Dictionary(Of String, ImageMask),
                                                    pipelineInputWidth As Integer, pipelineInputHeight As Integer,
                                                    zielBreite As Integer, zielHoehe As Integer,
                                                    onlyStackedAboveId As String) As SKBitmap
            Dim result As SKBitmap = Nothing
            For Each g In geschwister
                If g Is Nothing OrElse ReferenceEquals(g, ersteEbene) Then Continue For
                If Not adj.IsMaskedLayerRenderVisible(g) Then Continue For
                ' Dieselbe Skopus-Weiche wie in der Hauptschleife: eine Ebene aus dem Objektstapel
                ' darf den Basisdurchlauf nicht mitfaerben und umgekehrt.
                Dim stacked = If(g.StackAboveAnnotationId, "")
                If onlyStackedAboveId Is Nothing Then
                    If stacked.Length > 0 Then Continue For
                ElseIf Not String.Equals(stacked, onlyStackedAboveId, StringComparison.Ordinal) Then
                    Continue For
                End If
                Dim md As ImageMask = Nothing
                If Not masksById.TryGetValue(If(g.MaskId, ""), md) Then Continue For
                Dim modFill = g.HasFill() AndAlso (g.IsMaskLayer OrElse
                                                   (g.Adjustments IsNot Nothing AndAlso g.Adjustments.HasPixelAdjustments()))
                Using m = BuildPersistentMaskForOutput(md, adj, pipelineInputWidth, pipelineInputHeight,
                                                       zielBreite, zielHoehe, g.Opacity, If(modFill, g, Nothing))
                    If m Is Nothing Then Continue For
                    ' DIE SCHNITTMASKE DES GESCHWISTERS GILT AUCH HIER. Sie steckt NICHT in der
                    ' Maske, sondern wird beim Anwenden aufmultipliziert - die Hauptschleife tut das
                    ' fuer die erste Ebene, und ohne dieselben Zeilen hier ging die Beschraenkung
                    ' jedes weiteren Geschwisters beim Vereinigen verloren: seine Korrektur wirkte
                    ' dann ausserhalb der Ebene, auf die sie beschraenkt war (Nutzerbefund
                    ' 2026-08-08). Dieselbe Quelle wie dort, damit die zwei Wege nicht auseinander
                    ' laufen.
                    If g.ClipToLayerBelow AndAlso stacked.Length > 0 Then
                        Dim clipBase = FindAnnotationById(adj, stacked)
                        If clipBase IsNot Nothing AndAlso adj.IsAnnotationRenderVisible(clipBase) Then
                            Dim clip = BuildClipBaseCoverage(adj, clipBase, pipelineInputWidth, pipelineInputHeight,
                                                             0, 0, zielBreite, zielHoehe)
                            If clip IsNot Nothing Then MultiplyMaskByCoverage(m, clip)
                        End If
                    End If
                    If m.Width <> ersteMaske.Width OrElse m.Height <> ersteMaske.Height Then Continue For
                    If result Is Nothing Then result = CloneBitmap(ersteMaske)
                    MaskMaximum(result, m)
                End Using
            Next
            Return result
        End Function

        ''' <summary>Verrechnet einen weiteren Maskenbestandteil in das bisherige Ergebnis.
        '''
        ''' HINZUFÜGEN nimmt je Pixel das MAXIMUM und nicht die Summe: zweimal über dieselbe Stelle
        ''' soll dasselbe ergeben wie einmal, sonst gäbe es an Überlappungen Stufen. Dieselbe
        ''' Entscheidung wie beim Zusammenführen der Pinselkorrektur eines Verlaufs.</summary>
        Private Shared Sub CombineMaskInto(target As SKBitmap, source As SKBitmap, mode As String)
            If target Is Nothing OrElse source Is Nothing Then Return
            If target.Width <> source.Width OrElse target.Height <> source.Height Then Return
            If target.GetPixels() = IntPtr.Zero OrElse source.GetPixels() = IntPtr.Zero Then Return
            Dim tStride As Integer, sStride As Integer
            Dim tb = ReadMaskBytes(target, tStride)
            Dim sb = ReadMaskBytes(source, sStride)
            ' Die BILDBREITE begrenzt die Zeile, nicht der Stride: der Stride ist die Zeilenlaenge in
            ' BYTES und darf gepolstert sein. Bei Alpha8 sind beide heute gleich, gaebe Skia je
            ' gepolsterte Zeilen zurueck, wuerden sonst Polsterbytes mitverrechnet.
            Dim width = target.Width
            Dim normalized = If(mode, "").Trim().ToLowerInvariant()
            For y = 0 To target.Height - 1
                Dim tOffset = y * tStride, sOffset = y * sStride
                For x = 0 To width - 1
                    Dim a = CInt(tb(tOffset + x)), b = CInt(sb(sOffset + x))
                    Dim v As Integer
                    Select Case normalized
                        Case "subtract"
                            v = a - b
                        Case "intersect"
                            v = Math.Min(a, b)
                        Case Else
                            v = Math.Max(a, b)
                    End Select
                    If v < 0 Then v = 0
                    If v > 255 Then v = 255
                    tb(tOffset + x) = CByte(v)
                Next
            Next
            Marshal.Copy(tb, 0, target.GetPixels(), tb.Length)
        End Sub

        ''' <summary>Kehrt eine Alpha8-Maske an Ort und Stelle um (255 minus Deckung). Die EINE
        ''' Umsetzung von <see cref="ImageMask.InvertResult"/>; beide Stellen, die Bestandteile
        ''' zusammensetzen, rufen sie am Ende.</summary>
        Private Shared Sub InvertAlphaMaskInPlace(mask As SKBitmap)
            If mask Is Nothing OrElse mask.GetPixels() = IntPtr.Zero Then Return
            Dim stride As Integer
            Dim buffer = ReadMaskBytes(mask, stride)
            ' Bildbreite statt Stride, aus demselben Grund wie in CombineMaskInto.
            Dim width = mask.Width
            For y = 0 To mask.Height - 1
                Dim offset = y * stride
                For x = 0 To width - 1
                    buffer(offset + x) = CByte(255 - CInt(buffer(offset + x)))
                Next
            Next
            Marshal.Copy(buffer, 0, mask.GetPixels(), buffer.Length)
        End Sub

        ''' <summary>Je Pixel das Maximum aus zwei gleich grossen Alpha8-Masken, in die erste.</summary>
        Private Shared Sub MaskMaximum(target As SKBitmap, source As SKBitmap)
            If target Is Nothing OrElse source Is Nothing Then Return
            If target.Width <> source.Width OrElse target.Height <> source.Height Then Return
            If target.GetPixels() = IntPtr.Zero OrElse source.GetPixels() = IntPtr.Zero Then Return
            Dim zStride As Integer, qStride As Integer
            Dim zb = ReadMaskBytes(target, zStride)
            Dim qb = ReadMaskBytes(source, qStride)
            ' Bildbreite statt Stride, aus demselben Grund wie in CombineMaskInto.
            Dim width = target.Width
            For y = 0 To target.Height - 1
                Dim zo = y * zStride, qo = y * qStride
                For x = 0 To width - 1
                    If qb(qo + x) > zb(zo + x) Then zb(zo + x) = qb(qo + x)
                Next
            Next
            Marshal.Copy(zb, 0, target.GetPixels(), zb.Length)
        End Sub

        ''' <summary>EIN Bestandteil einer Maske, gerastert in der EINGANGSgröße der Geometriekette.
        ''' Nothing heißt: dieser Bestandteil trägt nichts bei (fehlend, leer oder beschädigt) - er
        ''' wird dann übersprungen, statt die ganze Maske zu verwerfen.
        '''
        ''' <paramref name="sourceWidth"/>/<paramref name="sourceHeight"/> sind die QUELLMASSE DER
        ''' MASKE; alle Bestandteile teilen sie sich, ihre Rechtecke und Verlaufspunkte beziehen sich
        ''' darauf.</summary>
        Private Shared Function BuildComponentMaskForInput(maskData As MaskComponent,
                                                           sourceWidth As Integer, sourceHeight As Integer,
                                                           pipelineInputWidth As Integer, pipelineInputHeight As Integer,
                                                           fillLayer As MaskedAdjustmentLayer) As SKBitmap
            If maskData Is Nothing OrElse pipelineInputWidth <= 0 OrElse pipelineInputHeight <= 0 OrElse
               sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return Nothing
            ' Ein VERLAUF traegt weder PNG noch Bounding-Box - er wird gleich gerechnet.
            If Not maskData.IsGradient AndAlso
               (maskData.Right <= maskData.Left OrElse maskData.Bottom <= maskData.Top OrElse
                Not maskData.HasPixelData) Then Return Nothing
            Try
                ' Beim Verlauf gibt es gar kein Raster; die Deckung entsteht pro Pixel aus der
                ' Projektion auf die Verlaufsachse (siehe unten). Das spart bei 45 MP rund 45 MB
                ' Zwischenpuffer und haelt den Verlauf ausserdem verlustfrei aenderbar.
                ' Pinselkorrektur eines Verlaufs: die beiden Raster einmal auspacken, danach kostet
                ' der Zugriff je Pixel nur noch einen Indexzugriff.
                Dim korrHinzu As Byte() = Nothing, korrWeg As Byte() = Nothing
                Dim corrWidth = 0, korrHoehe = 0
                If maskData.IsGradient AndAlso maskData.HasBrushCorrection Then
                    corrWidth = maskData.BrushRight - maskData.BrushLeft
                    korrHoehe = maskData.BrushBottom - maskData.BrushTop
                    korrHinzu = PixelsOfSize(maskData.BrushAddRaster, corrWidth, korrHoehe)
                    korrWeg = PixelsOfSize(maskData.BrushSubtractRaster, corrWidth, korrHoehe)
                    If korrHinzu Is Nothing AndAlso korrWeg Is Nothing Then corrWidth = 0
                End If
                ' DAS RASTER DER MASKE, nicht ihr PNG: es liegt bereits im Speicher, und wo nur
                ' die Speicherform da ist, entpackt die Maske sie EINMAL und behaelt sie.
                Dim maskRaster As AlphaRaster = If(maskData.IsGradient, Nothing, maskData.Raster)
                If Not maskData.IsGradient AndAlso maskRaster Is Nothing Then Return Nothing
                Dim dWidth = If(maskRaster Is Nothing, 0, maskRaster.Width)
                Dim dHeight = If(maskRaster Is Nothing, 0, maskRaster.Height)
                Dim dStride = dWidth
                ' Der Puffer GEHOERT DER MASKE und ist unveraenderlich. Wer hineinschreiben will
                ' (nur die Fuellung tut das), legt sich vorher eine eigene Abschrift an.
                Dim dBuf As Byte() = If(maskRaster Is Nothing, Nothing, maskRaster.Pixels)

                ' Verlaufsachse EINMAL vorbereiten: Start- und Endpunkt in Quellpixeln, dazu
                ' der Kehrwert des Achsenquadrats fuer die Projektion je Pixel.
                Dim gx0, gy0, gAchseX, gAchseY, gInvLen2 As Double
                Dim gRadius, gEx, gEy, gRatio, gInner As Double
                If maskData.IsGradient Then
                    gx0 = maskData.GradientStartXPercent / 100.0 * sourceWidth
                    gy0 = maskData.GradientStartYPercent / 100.0 * sourceHeight
                    gAchseX = maskData.GradientEndXPercent / 100.0 * sourceWidth - gx0
                    gAchseY = maskData.GradientEndYPercent / 100.0 * sourceHeight - gy0
                    Dim len2 = gAchseX * gAchseX + gAchseY * gAchseY
                    ' Beide Punkte aufeinander = keine Achse bzw. kein Radius: dann waere die
                    ' Maske ueberall halb gedeckt statt eines Verlaufs. Lieber gar keine Maske.
                    If len2 < 0.000001 Then Return Nothing
                    gInvLen2 = 1.0 / len2
                    If maskData.IsLinearGradient Then
                        ' Der Regler "Weiche Kante" bekommt beim linearen Verlauf eine
                        ' Bedeutung, statt wirkungslos zu bleiben: er staucht den Uebergang um
                        ' die MITTE der Achse zusammen. 100 % = voller Weg zwischen den beiden
                        ' Punkten (Standard), 0 % = harte Kante genau in der Mitte. Ein Weichzeichner
                        ' waere hier der falsche Weg - die Rampe ist schon glatt, und Blur kostet
                        ' bei 45 MP echte Zeit, ohne etwas zu aendern.
                        gInner = Math.Max(0.02, Math.Min(1.0, maskData.GradientFeatherPercent / 100.0))
                    End If
                    If maskData.IsRadialGradient Then
                        ' Radial: die Achse ist die erste Halbachse. Ihre Richtung ist zugleich
                        ' die Drehung der Ellipse, deshalb wird jeder Punkt in dieses gedrehte
                        ' System gerechnet und die zweite Achse ueber das Verhaeltnis skaliert.
                        gRadius = Math.Sqrt(len2)
                        gEx = gAchseX / gRadius
                        gEy = gAchseY / gRadius
                        gRatio = Math.Max(0.05, maskData.GradientRadiusRatio)
                        ' Innerer Anteil mit voller Deckung; der Rest ist der weiche Uebergang.
                        gInner = Math.Max(0.0, Math.Min(1.0, 1.0 - maskData.GradientFeatherPercent / 100.0))
                    End If
                End If

                ' MASKEN-Ebene mit deklarativer Füllung: die LUMINANZ der Füllung stuft die Maskenform ab
                ' (Schwarz→0, Weiß→voll, Verlauf→Rampe), bevor sie durch die Geometrie läuft. So bestimmt
                ' die Füllung, WIE STARK die Anpassung je Bereich wirkt - ohne die Maskenform zu verlieren.
                ' Bei einem Verlauf gibt es kein Raster - eine Fuellung hat dort nichts zu
                ' stufen, die Rampe IST schon die Abstufung.
                If dBuf IsNot Nothing AndAlso fillLayer IsNot Nothing AndAlso fillLayer.HasFill() Then
                    Dim lum = ComputeFillLuminance(dWidth, dHeight, fillLayer.FillKind,
                        fillLayer.FillColor, fillLayer.FillColor2, CSng(fillLayer.FillAngle), fillLayer.FillInverted)
                    If lum IsNot Nothing Then
                        ' EIGENE ABSCHRIFT: der Puffer darueber gehoert der Maske und wird von
                        ' anderen Wegen gleichzeitig gelesen.
                        dBuf = CType(dBuf.Clone(), Byte())
                        For y = 0 To dHeight - 1
                            Dim dRow = y * dStride, lRow = y * dWidth
                            For x = 0 To dWidth - 1
                                dBuf(dRow + x) = CByte(CInt(dBuf(dRow + x)) * CInt(lum(lRow + x)) \ 255)
                            Next
                        Next
                    End If
                End If

                ' Zunächst in die tatsächliche Pipeline-Eingangsgröße (Vollbild oder Preview)
                ' rasterisieren. Dadurch benutzt die anschließende Geometrie exakt dieselben
                ' Pixelrundungen wie das Bild und braucht keine zweite, leicht abweichende Matrix.
                Dim inputMask = New SKBitmap(pipelineInputWidth, pipelineInputHeight, SKColorType.Alpha8, SKAlphaType.Premul)
                Dim iStride = inputMask.RowBytes
                Dim iBuf = New Byte(iStride * pipelineInputHeight - 1) {}
                For y = 0 To pipelineInputHeight - 1
                    Dim sySource = CInt(Math.Floor((y + 0.5) * sourceHeight / pipelineInputHeight))
                    Dim sy = sySource - maskData.Top
                    Dim iRow = y * iStride
                    For x = 0 To pipelineInputWidth - 1
                        Dim sx = CInt(Math.Floor((x + 0.5) * sourceWidth / pipelineInputWidth)) - maskData.Left
                        Dim alpha = 0
                        If maskData.IsRadialGradient Then
                            ' Abstand im gedrehten Ellipsensystem: 0 = Mittelpunkt, 1 = Rand.
                            Dim dx = sx + maskData.Left - gx0
                            Dim dy = sySource - gy0
                            Dim laengs = (dx * gEx + dy * gEy) / gRadius
                            Dim quer = (-dx * gEy + dy * gEx) / (gRadius * gRatio)
                            Dim d = Math.Sqrt(laengs * laengs + quer * quer)
                            If d <= gInner Then
                                alpha = 255
                            ElseIf d >= 1.0 Then
                                alpha = 0
                            Else
                                Dim t2 = (d - gInner) / Math.Max(0.000001, 1.0 - gInner)
                                Dim s2 = t2 * t2 * (3.0 - 2.0 * t2)
                                alpha = CInt(Math.Round((1.0 - s2) * 255.0))
                            End If
                        ElseIf maskData.IsGradient Then
                            ' Projektion des Pixels auf die Verlaufsachse: t = 0 am Startpunkt
                            ' (volle Deckung), t = 1 am Endpunkt (keine). Ausserhalb wird
                            ' geklemmt, der Verlauf gilt also fuer das GANZE Bild.
                            Dim t = ((sx + maskData.Left - gx0) * gAchseX + (sySource - gy0) * gAchseY) * gInvLen2
                            ' Weichheit um die Mitte: t=0,5 bleibt der Wendepunkt, gInnen ist die
                            ' Breite des Uebergangs (siehe oben).
                            t = 0.5 + (t - 0.5) / gInner
                            If t <= 0.0 Then
                                alpha = 255
                            ElseIf t >= 1.0 Then
                                alpha = 0
                            Else
                                ' Smoothstep statt linear: eine lineare Rampe zeigt an ihren
                                ' beiden Enden eine sichtbare Kante (Mach-Band), gerade in
                                ' Himmelsflaechen. Der weiche Ein- und Ausstieg ist das, was
                                ' einen Verlaufsfilter unauffaellig macht.
                                Dim s = t * t * (3.0 - 2.0 * t)
                                alpha = CInt(Math.Round((1.0 - s) * 255.0))
                            End If
                        ElseIf sx >= 0 AndAlso sy >= 0 AndAlso sx < dWidth AndAlso sy < dHeight Then
                            alpha = dBuf(sy * dStride + sx)
                        End If
                        ' Pinselkorrektur NACH dem Verlauf und VOR dem Umkehren: "Umkehren"
                        ' soll das fertige Ergebnis spiegeln, nicht nur den Verlaufsanteil -
                        ' sonst kaeme ein weggepinselter Bereich beim Umkehren zurueck.
                        If corrWidth > 0 Then
                            Dim kx = sx + maskData.Left - maskData.BrushLeft
                            Dim ky = sySource - maskData.BrushTop
                            If kx >= 0 AndAlso ky >= 0 AndAlso kx < corrWidth AndAlso ky < korrHoehe Then
                                Dim ki = ky * corrWidth + kx
                                If korrHinzu IsNot Nothing Then alpha += korrHinzu(ki)
                                If korrWeg IsNot Nothing Then alpha -= korrWeg(ki)
                                If alpha < 0 Then
                                    alpha = 0
                                ElseIf alpha > 255 Then
                                    alpha = 255
                                End If
                            End If
                        End If
                        If maskData.Inverted Then alpha = 255 - alpha
                        iBuf(iRow + x) = CByte(alpha)
                    Next
                Next
                Marshal.Copy(iBuf, 0, inputMask.GetPixels(), iBuf.Length)

                ' Verlaeufe sind bereits glatt - ihr Weichheits-Regler sitzt in der Geometrie
                ' (GradientFeatherPercent), nicht in einem nachgeschalteten Weichzeichner.
                If maskData.FeatherPixels > 0.05F AndAlso Not maskData.IsGradient Then
                    Dim initialScale = (pipelineInputWidth / CSng(sourceWidth) +
                                        pipelineInputHeight / CSng(sourceHeight)) / 2.0F
                    Dim blurred = BlurAlphaMask(inputMask, maskData.FeatherPixels * initialScale)
                    If blurred IsNot Nothing Then inputMask = ReplaceBitmap(inputMask, blurred)
                End If
                Return inputMask
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Die fertige Maske in AUSGABEgröße: alle Bestandteile der Reihe nach gerastert,
        ''' miteinander verrechnet und danach durch dieselbe Geometriekette geschickt wie das Bild.
        '''
        ''' Der ERSTE Bestandteil setzt das Ergebnis, unabhängig von seinem Modus - vor ihm gibt es
        ''' nichts, worauf man rechnen könnte. Jeder weitere kommt mit seinem Modus hinzu:
        ''' Hinzufügen nimmt je Pixel das MAXIMUM (nicht die Summe: zweimal dieselbe Stelle soll
        ''' dasselbe ergeben wie einmal), Abziehen die Differenz, Schneiden das Minimum.
        '''
        ''' Ein Bestandteil, der nichts liefert, wird ÜBERSPRUNGEN und verwirft nicht die ganze
        ''' Maske - dieselbe Regel wie bisher für eine beschädigte Maske, nur eine Ebene tiefer.</summary>
        Private Shared Function BuildPersistentMaskForOutput(maskData As ImageMask, geometry As ImageAdjustments,
                                                              pipelineInputWidth As Integer, pipelineInputHeight As Integer,
                                                              targetW As Integer, targetH As Integer,
                                                              layerOpacity As Single,
                                                              Optional fillLayer As MaskedAdjustmentLayer = Nothing) As SKBitmap
            If maskData Is Nothing OrElse pipelineInputWidth <= 0 OrElse pipelineInputHeight <= 0 OrElse
               targetW <= 0 OrElse targetH <= 0 OrElse
               maskData.SourceWidthPixels <= 0 OrElse maskData.SourceHeightPixels <= 0 Then Return Nothing

            ' MASKE AUS: sie begrenzt nichts mehr, also volle Deckung ueberall. Das ist bewusst
            ' etwas anderes als eine FEHLENDE Maske - die wirkt gar nicht (Nothing weiter unten),
            ' waehrend diese hier die Anpassung ueberall wirken laesst. Genau dafuer schaltet man
            ' sie ab: um zu sehen, was ohne sie geschaehe. Die Dichte gehoert der Maske und ist
            ' damit ebenfalls aus; die Deckkraft der EBENE bleibt.
            If maskData.IsDisabled Then
                Dim opaque = New SKBitmap(targetW, targetH, SKColorType.Alpha8, SKAlphaType.Premul)
                Dim value = CByte(Math.Round(255.0 * Clamp(layerOpacity, 0, 1)))
                Dim oStride = opaque.RowBytes
                Dim oBuf = New Byte(oStride * targetH - 1) {}
                For i = 0 To oBuf.Length - 1
                    oBuf(i) = value
                Next
                Marshal.Copy(oBuf, 0, opaque.GetPixels(), oBuf.Length)
                Return opaque
            End If

            ' GEMERKT: dieselbe Maske in denselben Massen. Der Aufbau haengt an Maskeninhalt,
            ' Geometrie, Massen und Deckkraft - NICHT an den Farbreglern. Beim Ziehen eines
            ' Belichtungsreglers an einer Korrekturebene entsteht also bei jedem Vorschaubild
            ' dieselbe Maske neu; gemessen sind das 248 ms je Ebene bei 45 MP.
            ' Der Fuellungs-Fall bleibt aussen vor: dort geht der Inhalt der EBENE mit ein.
            Dim cacheKey As String = Nothing
            If fillLayer Is Nothing Then
                cacheKey = String.Join("|", MaskFingerprint(maskData),
                                       MaskGeometryKey(BuildMaskGeometry(geometry)),
                                       pipelineInputWidth, pipelineInputHeight, targetW, targetH,
                                       KeyPart(layerOpacity))
                Dim cached = TryTakeMaskRaster(cacheKey, targetW, targetH)
                If cached IsNot Nothing Then Return cached
            End If

            Dim components = maskData.GetComponents()
            If components.Count = 0 Then Return Nothing
            Try
                Dim inputMask As SKBitmap = Nothing
                For Each component In components
                    ' Ein ausgeschalteter Bestandteil faellt hier weg, an derselben Stelle wie ein
                    ' leerer. Damit gilt "der ERSTE setzt das Ergebnis" fuer den ersten SICHTBAREN.
                    If Not component.IsVisible Then Continue For
                    Dim part = BuildComponentMaskForInput(component, maskData.SourceWidthPixels, maskData.SourceHeightPixels,
                                                          pipelineInputWidth, pipelineInputHeight, fillLayer)
                    If part Is Nothing Then Continue For
                    If inputMask Is Nothing Then
                        inputMask = part
                    Else
                        CombineMaskInto(inputMask, part, component.Mode)
                        part.Dispose()
                    End If
                Next
                If inputMask Is Nothing Then Return Nothing
                ' Umkehrung des FERTIGEN Ergebnisses, nach allen Bestandteilen und vor der Geometrie.
                ' Traegt kein Bestandteil etwas bei, bleibt es bei Nothing: eine leere Maske wirkt
                ' gar nicht, und daran aendert auch das Umkehren nichts.
                If maskData.InvertResult Then InvertAlphaMaskInPlace(inputMask)

                Using inputMask
                    ' Ebenen-Deckkraft UND Maskendichte, beide an derselben Stelle: die eine gehoert
                    ' der Ebene, die andere der Maske. Ein Objekt mit Ebenenmaske kommt hier mit
                    ' layerOpacity = 1 herein und bekommt seine Abstufung damit ueber die Dichte.
                    Dim opacity = Clamp(layerOpacity, 0, 1) *
                                  Clamp(CSng(maskData.Density / 100.0), 0, 1)
                    Dim maskGeometry = BuildMaskGeometry(geometry)

                    ' ABKUERZUNG bei neutraler Geometrie und unveraenderten Massen: dann sind die
                    ' sieben Stufen unten die Identitaet, und das Aufblasen der Alpha8-Maske auf vier
                    ' Kanaele waere Arbeit fuer nichts (bei 45 MP 181 MB statt 45 MB durch sieben
                    ' Stufen, gemessen 166 ms). Die Neutralitaet wird NICHT ueber eine eigene
                    ' Feldliste bestimmt, sondern ueber denselben Schluessel, den auch der
                    ' Deckungs-Speicher benutzt - eine dritte Liste liefe irgendwann auseinander.
                    If pipelineInputWidth = targetW AndAlso pipelineInputHeight = targetH AndAlso
                       String.Equals(MaskGeometryKey(maskGeometry), NeutralMaskGeometryKey, StringComparison.Ordinal) Then
                        Return StoreMaskRaster(cacheKey, BuildAlpha8FromAlphaMask(inputMask, targetW, targetH, opacity))
                    End If

                    Dim maskPixels = New SKBitmap(pipelineInputWidth, pipelineInputHeight, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Dim pStride = maskPixels.RowBytes
                    Dim pBuf = New Byte(pStride * pipelineInputHeight - 1) {}
                    Dim alphaBuf = New Byte(inputMask.RowBytes * inputMask.Height - 1) {}
                    Marshal.Copy(inputMask.GetPixels(), alphaBuf, 0, alphaBuf.Length)
                    Dim inputStride = inputMask.RowBytes
                    ForEachRow(pipelineInputWidth, pipelineInputHeight,
                        Sub(y As Integer)
                            Dim aRow = y * inputStride, pRow = y * pStride
                            For x = 0 To pipelineInputWidth - 1
                                Dim a = alphaBuf(aRow + x), o = pRow + x * 4
                                pBuf(o) = a : pBuf(o + 1) = a : pBuf(o + 2) = a : pBuf(o + 3) = a
                            Next
                        End Sub)
                    Marshal.Copy(pBuf, 0, maskPixels.GetPixels(), pBuf.Length)

                    maskPixels = ApplyGeometryPipeline(maskPixels, maskGeometry)

                    ' Derselbe Pfad sollte dieselbe Größe liefern. Der Fallback schützt dennoch vor
                    ' alten/inkonsistenten Rezeptmaßen, ohne die Korrektur global werden zu lassen.
                    If maskPixels.Width <> targetW OrElse maskPixels.Height <> targetH Then
                        Dim scaled = New SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul)
                        Using canvas = New SKCanvas(scaled)
                            canvas.Clear(SKColors.Transparent)
                            Using paint = New SKPaint With {.IsAntialias = True}
                                DrawBitmapSampled(canvas, maskPixels, New SKRect(0, 0, maskPixels.Width, maskPixels.Height),
                                                  New SKRect(0, 0, targetW, targetH),
                                                  New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint)
                            End Using
                        End Using
                        maskPixels = ReplaceBitmap(maskPixels, scaled)
                    End If

                    Dim transformed As Byte() = Nothing, transformedStride As Integer = 0
                    If Not TryBorrowBgraBuffer(maskPixels, transformed, transformedStride) Then
                        maskPixels.Dispose()
                        Return Nothing
                    End If
                    ' Deckkraft und Dichte stehen schon oben, vor der Abkuerzung.
                    Dim result = New SKBitmap(targetW, targetH, SKColorType.Alpha8, SKAlphaType.Premul)
                    Dim rStride = result.RowBytes
                    Dim rBuf = New Byte(rStride * targetH - 1) {}
                    ForEachRow(targetW, targetH,
                        Sub(y As Integer)
                            Dim pRow = y * transformedStride, rRow = y * rStride
                            For x = 0 To targetW - 1
                                rBuf(rRow + x) = CByte(Math.Round(transformed(pRow + x * 4 + 3) * opacity))
                            Next
                        End Sub)
                    Marshal.Copy(rBuf, 0, result.GetPixels(), rBuf.Length)
                    maskPixels.Dispose()
                    Return StoreMaskRaster(cacheKey, result)
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Rand um das Maskenrechteck, in Bildpunkten. GEMESSEN, nicht geschätzt
        ''' (<c>Diagnostics/Kettenmessung</c>, Befehl <c>raender</c>): die Reichweiten der
        ''' Nachbarschaftsstufen sind absolut und wachsen NICHT mit der Bildgröße - Klarheit 4,
        ''' Glühen 4, Rauschminderung 3, Struktur 2, Weichzeichner 2. Acht deckt alle ab.</summary>
        Private Const MaskScopeMargin As Integer = 8

        ''' <summary>Das Rechteck wird auf Vielfache dieser Zahl ausgerichtet, und das ist keine
        ''' Kosmetik: der Dither der Quantisierung ist ein geordnetes 8x8-Muster aus den
        ''' BILDKOORDINATEN (siehe <c>ImageProcessorPointOps</c>). Auf einem nicht ausgerichteten
        ''' Ausschnitt sitzt es verschoben, und das Ergebnis weicht an Zehntausenden Stellen um je
        ''' eine Stufe ab - unsichtbar, aber nicht mehr bitgleich nachweisbar. Ausgerichtet steht
        ''' es an derselben Stelle wie im Vollbild.</summary>
        Private Const MaskScopeAlignment As Integer = 8

        ''' <summary>Unterhalb dieses Anteils der Bildfläche lohnt der Ausschnitt. Darüber kostet
        ''' das Ausschneiden und Zurückschreiben mehr, als die kleinere Kette einspart.</summary>
        Private Const MaskScopeMaxAreaShare As Double = 0.7

        ''' <summary>NUR FÜR DEN PRÜFSTAND: erzwingt den Vollbildweg, damit sich beide Wege
        ''' gegeneinander halten lassen (dasselbe Rezept einmal so, einmal so, und die Ergebnisse
        ''' müssen bitgleich sein).
        '''
        ''' Ohne diesen Schalter prüft sich der Ausschnittweg nur selbst: man kann seine Bausteine
        ''' einzeln messen, aber nicht den ECHTEN Weg durch <c>ApplyMaskedAdjustmentLayersCore</c>
        ''' samt Besitzprüfung, Rechteckwahl und Rückschreiben. Genau dort sitzen die Fehler, die
        ''' eine Bausteinprüfung nicht sieht.
        '''
        ''' Im Betrieb wird das Feld nie geschrieben.</summary>
        Private Shared _maskScopeEnabled As Boolean = True

        ''' <summary>Wie oft der Ausschnittweg tatsächlich gelaufen ist. NUR damit eine Prüfung
        ''' feststellen kann, dass sie ihn überhaupt getroffen hat.
        '''
        ''' Der Grund steht in der Geschichte dieser Zeile: die erste Fassung der Whitelist-Prüfung
        ''' war grün, obwohl sie den Weg nie erreichte - ihr Testrezept trug keine globalen Regler,
        ''' also blieb das Bild fremder Besitz, und der Ausschnittweg lehnte ab. Eine Prüfung, die
        ''' ihr Messobjekt nicht trifft, prüft nur, dass nichts da ist.</summary>
        Private Shared _maskScopeUseCount As Integer = 0

        ''' <summary>Darf die Kette dieser Ebene über einem AUSSCHNITT laufen?
        '''
        ''' Die Liste ist gemessen und nicht hergeleitet (<c>Diagnostics/Kettenmessung raender</c>).
        ''' Ausgeschlossen sind zwei Sorten: Stufen, die an der POSITION im Bild hängen (Vignette
        ''' weicht um bis zu 92 Stufen ab, Körnung 56, Staub und Kratzer 76), und Stufen, die auch
        ''' ausgerichtet nur dither-gleich bleiben (Farbrauschen 2, grobes Farbrauschen 1).
        '''
        ''' Die SCHÄRFE ist bewusst ganz draußen, obwohl sie bei halbem Radius bitgleich war: bei
        ''' vollem Radius ist sie es nicht mehr, und wo genau die Grenze liegt, ist nicht gemessen.
        ''' Eine ungemessene Grenze im Quelltext wäre schlimmer als der entgangene Gewinn.
        '''
        ''' <para>ZWEITE AUSSCHLUSSKLASSE, und sie ist die tückischere: Stufen, die das GANZE BILD
        ''' ANALYSIEREN und aus dem Ergebnis ihre Parameter ableiten. Sie fallen bei einem Vergleich
        ''' Vollbild gegen Ausschnitt nur dann auf, wenn der Ausschnitt zufällig eine andere
        ''' Statistik trägt - eine Messung allein findet sie also nicht verlässlich, man muss sie
        ''' im Quelltext suchen. Bei uns ist das genau eine: das FILMNEGATIV. Fehlen Basis- und
        ''' Dichtefarbe im Rezept, schätzt <c>ResolveFilmNegativeStats</c> beide aus dem Bild, das
        ''' gerade vorliegt (Stapelverarbeitung, wiederhergestellte Anpassungen). Über einem
        ''' Ausschnitt käme eine andere Schätzung heraus, und dieselbe lokale Korrektur sähe anders
        ''' aus als auf dem Vollbild. Der Regler bleibt deshalb GANZ draußen, auch wenn beide Farben
        ''' gesetzt sind - eine Bedingung, die von zwei Zeichenketten im Rezept abhängt, ist zu
        ''' zerbrechlich für diese Stelle.</para>
        '''
        ''' Abgesichert durch die Prüfung „Ebenen-Ausschnitt: die erlaubten Regler sind bitgleich",
        ''' die jeden hier erlaubten Regler einzeln gegen den Vollbildweg hält.</summary>
        Private Shared Function LayerAdjustmentsAreCropSafe(a As ImageAdjustments) As Boolean
            If a Is Nothing Then Return False
            ' Ohne gespeicherte Basis-/Dichtefarbe misst Filmnegativ aus dem Bild. Ein
            ' lokaler Ausschnitt hätte andere Werte; auch mit gesetzten Farben bleibt es
            ' bewusst außerhalb der Whitelist (siehe Erläuterung oben).
            Return a.Vignette = 0 AndAlso
                   a.Grain = 0 AndAlso
                   a.DustScratches = 0 AndAlso
                   a.AddNoise = 0 AndAlso
                   a.ColorNoiseReduction = 0 AndAlso
                   a.FarbrauschGrob = 0 AndAlso
                   a.ColorNoiseAdd = 0 AndAlso
                   a.Sharpness = 0 AndAlso
                   Not a.NegativeEnabled
        End Function

        ''' <summary>Das ausgerichtete Rechteck, in dem diese Maske überhaupt deckt, oder Nothing,
        ''' wenn sich der Ausschnitt nicht lohnt (zu groß, oder die Maske deckt nirgends).</summary>
        Private Shared Function TryGetMaskScopeRect(mask As SKBitmap, imageWidth As Integer, imageHeight As Integer) As SKRectI?
            If Not _maskScopeEnabled Then Return Nothing
            If mask Is Nothing OrElse imageWidth <= 0 OrElse imageHeight <= 0 Then Return Nothing
            If mask.ColorType <> SKColorType.Alpha8 Then Return Nothing

            Dim stride = mask.RowBytes
            Dim buffer = New Byte(stride * mask.Height - 1) {}
            Marshal.Copy(mask.GetPixels(), buffer, 0, buffer.Length)

            Dim left = Integer.MaxValue, top = Integer.MaxValue
            Dim right = Integer.MinValue, bottom = Integer.MinValue
            ' Nur der Teil, den Maske UND Bild gemeinsam haben - eine Maske darf groesser sein.
            Dim scanWidth = Math.Min(mask.Width, imageWidth)
            Dim scanHeight = Math.Min(mask.Height, imageHeight)
            For y = 0 To scanHeight - 1
                Dim row = y * stride
                For x = 0 To scanWidth - 1
                    If buffer(row + x) = 0 Then Continue For
                    If x < left Then left = x
                    If x > right Then right = x
                    If y < top Then top = y
                    If y > bottom Then bottom = y
                Next
            Next
            If right < left OrElse bottom < top Then Return Nothing   ' die Maske deckt nirgends

            ' Rand dazu, dann nach aussen auf die Ausrichtung runden, dann aufs Bild klemmen.
            left = Math.Max(0, left - MaskScopeMargin)
            top = Math.Max(0, top - MaskScopeMargin)
            right = Math.Min(imageWidth - 1, right + MaskScopeMargin)
            bottom = Math.Min(imageHeight - 1, bottom + MaskScopeMargin)
            left -= (left Mod MaskScopeAlignment)
            top -= (top Mod MaskScopeAlignment)
            ' Ab hier die AUSSCHLIESSENDEN Kanten des Rechtecks; oben waren es die letzten Punkte,
            ' die die Maske noch deckt.
            Dim rightEdge = Math.Min(imageWidth, right + 1)
            Dim bottomEdge = Math.Min(imageHeight, bottom + 1)

            Dim area = CDbl(rightEdge - left) * CDbl(bottomEdge - top)
            If area <= 0 Then Return Nothing
            If area > CDbl(imageWidth) * CDbl(imageHeight) * MaskScopeMaxAreaShare Then Return Nothing

            Return New SKRectI(left, top, rightEdge, bottomEdge)
        End Function

        ''' <summary>Kette und Zusammensetzen NUR im Rechteck, direkt in <paramref name="target"/>.
        ''' Der Aufrufer muss Besitzer des Bildes sein - ausserhalb des Rechtecks bleibt es
        ''' unverändert, und genau das ist richtig: dort deckt die Maske nicht.</summary>
        Private Shared Function ApplyLayerAdjustmentsInRect(target As SKBitmap, effectMask As SKBitmap,
                                                            layerAdjustments As ImageAdjustments,
                                                            rect As SKRectI) As Boolean
            If target Is Nothing OrElse effectMask Is Nothing OrElse layerAdjustments Is Nothing Then Return False
            Dim width = rect.Width, height = rect.Height
            If width <= 0 OrElse height <= 0 Then Return False

            Using crop = CopyBgraRegion(target, rect)
                If crop Is Nothing Then Return False
                Threading.Interlocked.Increment(_maskScopeUseCount)
                Using maskCrop = CopyAlphaRegion(effectMask, rect)
                    If maskCrop Is Nothing Then Return False
                    Using adjusted = ApplyPixelAdjustmentStages(crop, layerAdjustments)
                        Using composited = CompositeSelectionScoped(crop, adjusted, maskCrop)
                            If composited Is Nothing Then Return False
                            Return WriteBgraRegion(composited, target, rect)
                        End Using
                    End Using
                End Using
            End Using
        End Function

        ' ZEILENWEISE zwischen Bitmap und Puffer, nie ueber das ganze Bild. Der erste Anlauf las in
        ' allen drei Funktionen das VOLLE Bild in ein Byte-Feld, aenderte darin das Rechteck und
        ' schrieb alles zurueck: bei 45 MP sind das 90 MB Kopie je Ebene, und genau die Ersparnis,
        ' um die es hier geht, war damit wieder weg (gemessen 18 statt der moeglichen Prozente).
        Private Shared Function CopyBgraRegion(source As SKBitmap, rect As SKRectI) As SKBitmap
            If source.ColorType <> SKColorType.Bgra8888 AndAlso source.ColorType <> SKColorType.Rgba8888 Then Return Nothing
            Dim sourceStride = source.RowBytes
            Dim sourcePixels = source.GetPixels()

            Dim result = New SKBitmap(rect.Width, rect.Height, source.ColorType, source.AlphaType)
            Dim resultStride = result.RowBytes
            Dim rowBytes = rect.Width * 4
            Dim row = New Byte(rowBytes - 1) {}
            For y = 0 To rect.Height - 1
                Marshal.Copy(IntPtr.Add(sourcePixels, (y + rect.Top) * sourceStride + rect.Left * 4), row, 0, rowBytes)
                Marshal.Copy(row, 0, IntPtr.Add(result.GetPixels(), y * resultStride), rowBytes)
            Next
            Return result
        End Function

        Private Shared Function CopyAlphaRegion(source As SKBitmap, rect As SKRectI) As SKBitmap
            Dim sourceStride = source.RowBytes
            Dim sourcePixels = source.GetPixels()

            Dim result = New SKBitmap(rect.Width, rect.Height, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim resultStride = result.RowBytes
            Dim rowBytes = Math.Min(rect.Width, Math.Max(0, source.Width - rect.Left))
            If rowBytes <= 0 Then Return result
            Dim row = New Byte(rowBytes - 1) {}
            For y = 0 To rect.Height - 1
                Dim sourceRow = y + rect.Top
                If sourceRow >= source.Height Then Exit For
                Marshal.Copy(IntPtr.Add(sourcePixels, sourceRow * sourceStride + rect.Left), row, 0, rowBytes)
                Marshal.Copy(row, 0, IntPtr.Add(result.GetPixels(), y * resultStride), rowBytes)
            Next
            Return result
        End Function

        Private Shared Function WriteBgraRegion(source As SKBitmap, target As SKBitmap, rect As SKRectI) As Boolean
            If source.ColorType <> target.ColorType Then Return False
            Dim sourceStride = source.RowBytes
            Dim targetStride = target.RowBytes
            Dim sourcePixels = source.GetPixels()
            Dim targetPixels = target.GetPixels()

            Dim rowBytes = rect.Width * 4
            Dim row = New Byte(rowBytes - 1) {}
            For y = 0 To rect.Height - 1
                Marshal.Copy(IntPtr.Add(sourcePixels, y * sourceStride), row, 0, rowBytes)
                Marshal.Copy(row, 0, IntPtr.Add(targetPixels, (y + rect.Top) * targetStride + rect.Left * 4), rowBytes)
            Next
            Return True
        End Function

        Private NotInheritable Class MaskRasterEntry
            Public Property Key As String
            ''' <summary>Dicht gepackt, ein Byte je Bildpunkt, Zeilenlaenge = Breite. Absichtlich
            ''' NICHT der Zeilenabstand der Bitmap: der haengt an Skias Ausrichtung und muss beim
            ''' Herausgeben ohnehin neu gesetzt werden.</summary>
            Public Property Raster As Byte()
            Public Property Width As Integer
            Public Property Height As Integer
            Public Property LastUse As Long
        End Class

        ' Gemerkte FERTIGE Masken der Korrekturebenen, in Ausgabegroesse. Anders als der
        ' Deckungs-Speicher darueber, der Objekt-Ebenenmasken haelt: dieser hier bedient den
        ' Renderweg der Korrekturebenen, der bisher gar nichts merkte.
        Private Shared ReadOnly _maskRasterCache As New List(Of MaskRasterEntry)()
        Private Shared ReadOnly _maskRasterLock As New Object()
        Private Shared _maskRasterClock As Long = 0
        ' Reicht fuer mehrere Ebenen in Vorschaugroesse (3072x2048 sind rund 6 MB je Ebene). In
        ' voller Aufloesung passt nur eine Handvoll, und dort laeuft jede Ebene ohnehin einmal.
        Private Const MaskRasterBudgetBytes As Long = 96L * 1024L * 1024L

        ''' <summary>Wirft die gemerkten Masken weg. Gerufen aus <see cref="ClearBaseCache"/>, also
        ''' beim Bildwechsel und beim Verlassen des Editors.
        '''
        ''' Das Budget allein reicht als Aufraeumen NICHT: es begrenzt, wie viel liegen bleibt,
        ''' nicht wie lange. Ohne diese Zeile behielte ein statisches Feld bis zum Programmende bis
        ''' zu 96 MB Masken eines Bildes, das niemand mehr offen hat.</summary>
        Friend Shared Sub ClearMaskRasterCache()
            SyncLock _maskRasterLock
                _maskRasterCache.Clear()
            End SyncLock
        End Sub

        ''' <summary>Fertige Maske aus dem Speicher, als EIGENE Bitmap. Der Aufrufer entsorgt sie
        ''' (er hat sie in einem Using), der Speicher behaelt sein Byte-Feld.</summary>
        Private Shared Function TryTakeMaskRaster(key As String, targetW As Integer, targetH As Integer) As SKBitmap
            If String.IsNullOrEmpty(key) Then Return Nothing
            Dim raster As Byte() = Nothing
            SyncLock _maskRasterLock
                For Each entry In _maskRasterCache
                    If String.Equals(entry.Key, key, StringComparison.Ordinal) AndAlso
                       entry.Width = targetW AndAlso entry.Height = targetH Then
                        _maskRasterClock += 1
                        entry.LastUse = _maskRasterClock
                        raster = entry.Raster
                        Exit For
                    End If
                Next
            End SyncLock
            If raster Is Nothing Then Return Nothing

            Dim result = New SKBitmap(targetW, targetH, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim rStride = result.RowBytes
            Dim rBuf = New Byte(rStride * targetH - 1) {}
            For y = 0 To targetH - 1
                Array.Copy(raster, y * targetW, rBuf, y * rStride, targetW)
            Next
            Marshal.Copy(rBuf, 0, result.GetPixels(), rBuf.Length)
            Return result
        End Function

        ''' <summary>Legt die fertige Maske ab und reicht sie unveraendert durch. Ohne Schluessel
        ''' (Fuellungs-Fall) passiert nichts.</summary>
        Private Shared Function StoreMaskRaster(key As String, mask As SKBitmap) As SKBitmap
            If String.IsNullOrEmpty(key) OrElse mask Is Nothing Then Return mask
            Dim width = mask.Width, height = mask.Height
            If width <= 0 OrElse height <= 0 Then Return mask

            Dim stride = mask.RowBytes
            Dim source = New Byte(stride * height - 1) {}
            Marshal.Copy(mask.GetPixels(), source, 0, source.Length)
            Dim raster = New Byte(width * height - 1) {}
            For y = 0 To height - 1
                Array.Copy(source, y * stride, raster, y * width, width)
            Next

            SyncLock _maskRasterLock
                _maskRasterClock += 1
                For Each entry In _maskRasterCache
                    If String.Equals(entry.Key, key, StringComparison.Ordinal) Then
                        entry.Raster = raster
                        entry.Width = width
                        entry.Height = height
                        entry.LastUse = _maskRasterClock
                        Return mask
                    End If
                Next
                _maskRasterCache.Add(New MaskRasterEntry With {
                    .Key = key, .Raster = raster, .Width = width, .Height = height,
                    .LastUse = _maskRasterClock})
                Dim total As Long = 0
                For Each entry In _maskRasterCache
                    total += entry.Raster.LongLength
                Next
                While _maskRasterCache.Count > 1 AndAlso total > MaskRasterBudgetBytes
                    Dim victim = _maskRasterCache(0)
                    For Each entry In _maskRasterCache
                        If entry.LastUse < victim.LastUse Then victim = entry
                    Next
                    total -= victim.Raster.LongLength
                    _maskRasterCache.Remove(victim)
                End While
            End SyncLock
            Return mask
        End Function

        ''' <summary>Der Schluessel einer Geometrie, die nichts tut. Gegen ihn wird verglichen, um die
        ''' sieben Stufen im Maskenbau zu ueberspringen.
        '''
        ''' Absichtlich aus <see cref="MaskGeometryKey"/> und einem frischen Rezept gebildet statt aus
        ''' einer Aufzaehlung neutraler Werte: so kann er gar nicht von dem abweichen, was die Kette
        ''' tatsaechlich liest. Kommt ein Feld hinzu, wandert es ueber MaskGeometryKey automatisch
        ''' auch hierher.</summary>
        Private Shared ReadOnly NeutralMaskGeometryKey As String =
            MaskGeometryKey(BuildMaskGeometry(New ImageAdjustments()))

        ''' <summary>Fertige Alpha8-Maske direkt aus dem Alpha8-Raster, mit Deckkraft.
        '''
        ''' Bitgleich zum langen Weg: dort bekommen beim Aufblasen alle vier Kanaele denselben Wert,
        ''' gelesen wird am Ende nur der Alphakanal, und die Geometriestufen dazwischen sind in
        ''' diesem Fall die Identitaet. Es bleibt also genau dieselbe Rechnung auf demselben Byte.</summary>
        Private Shared Function BuildAlpha8FromAlphaMask(inputMask As SKBitmap, targetW As Integer, targetH As Integer,
                                                         opacity As Single) As SKBitmap
            Dim sourceStride = inputMask.RowBytes
            Dim sourceBuf = New Byte(sourceStride * inputMask.Height - 1) {}
            Marshal.Copy(inputMask.GetPixels(), sourceBuf, 0, sourceBuf.Length)

            Dim result = New SKBitmap(targetW, targetH, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim rStride = result.RowBytes
            Dim rBuf = New Byte(rStride * targetH - 1) {}
            ForEachRow(targetW, targetH,
                Sub(y As Integer)
                    Dim sRow = y * sourceStride, rRow = y * rStride
                    For x = 0 To targetW - 1
                        rBuf(rRow + x) = CByte(Math.Round(sourceBuf(sRow + x) * opacity))
                    Next
                End Sub)
            Marshal.Copy(rBuf, 0, result.GetPixels(), rBuf.Length)
            Return result
        End Function

        ''' <summary>Genau der Teil des Rezepts, den eine Maske auf ihrem Weg vom Quellraum in die
        ''' Ausgabe durchläuft. Ausgelagert, weil zwei Stellen ihn brauchen: das Rastern selbst und
        ''' der Schlüssel des Deckungs-Speichers (<see cref="GetAnnotationMaskCoverage"/>). Zwei
        ''' getrennte Feldlisten würden auseinanderlaufen, und der Speicher gäbe dann nach einem
        ''' Zuschnitt die alte Maske zurück.</summary>
        ' ImageWarp gehoert MIT hinein: ohne sie bliebe die Maske ungebogen liegen, waehrend das
        ' Bild sich verzieht - derselbe Weg wie bei der Perspektive.
        Private Shared Function BuildMaskGeometry(geometry As ImageAdjustments) As ImageAdjustments
            Return New ImageAdjustments With {
                .CropLeftPercent = geometry.CropLeftPercent, .CropTopPercent = geometry.CropTopPercent,
                .CropRightPercent = geometry.CropRightPercent, .CropBottomPercent = geometry.CropBottomPercent,
                .RotationDegrees = geometry.RotationDegrees,
                .FlipHorizontal = geometry.FlipHorizontal, .FlipVertical = geometry.FlipVertical,
                .StraightenDegrees = geometry.StraightenDegrees,
                .StraightenExpandCanvas = geometry.StraightenExpandCanvas,
                .PerspectiveHorizontal = geometry.PerspectiveHorizontal,
                .PerspectiveVertical = geometry.PerspectiveVertical,
                .PerspectiveAspect = geometry.PerspectiveAspect,
                .PerspectiveScale = geometry.PerspectiveScale,
                .PerspectiveCorner0X = geometry.PerspectiveCorner0X, .PerspectiveCorner0Y = geometry.PerspectiveCorner0Y,
                .PerspectiveCorner1X = geometry.PerspectiveCorner1X, .PerspectiveCorner1Y = geometry.PerspectiveCorner1Y,
                .PerspectiveCorner2X = geometry.PerspectiveCorner2X, .PerspectiveCorner2Y = geometry.PerspectiveCorner2Y,
                .PerspectiveCorner3X = geometry.PerspectiveCorner3X, .PerspectiveCorner3Y = geometry.PerspectiveCorner3Y,
                .GeometryOperations = If(geometry.GeometryOperations, New List(Of GeometryOperation)()).
                    Where(Function(operation) operation IsNot Nothing).Select(Function(operation) operation.Clone()).ToList(),
                .ImageWarp = geometry.ImageWarp,
                .ResizeWidth = geometry.ResizeWidth, .ResizeHeight = geometry.ResizeHeight,
                .ResizeInterpolation = geometry.ResizeInterpolation,
                .CanvasWidth = geometry.CanvasWidth, .CanvasHeight = geometry.CanvasHeight,
                .CanvasAnchor = geometry.CanvasAnchor,
                .CanvasBackgroundColor = "#00000000"
            }
        End Function

        ''' <summary>Derselbe Ausschnitt des Rezepts als kurzer Schluessel - fuer den Deckungs-Speicher
        ''' in <see cref="GetAnnotationMaskCoverage"/>.
        '''
        ''' WER HIER ETWAS AENDERT, AENDERT ES AUCH IN <see cref="BuildMaskGeometry"/> - und
        ''' umgekehrt. Die Reihenfolge ist absichtlich dieselbe, damit sich beide Zeile fuer Zeile
        ''' vergleichen lassen. Ein zu schmaler Schluessel gibt nach einer Aenderung der Geometrie
        ''' die ALTE Deckung zurueck, und die Maske sitzt sichtbar neben dem Objekt.
        '''
        ''' Vorher stand hier JsonSerializer.Serialize(BuildMaskGeometry(...)): das baute bei JEDEM
        ''' Aufruf ein volles ImageAdjustments auf und schrieb dessen mehrere hundert Eigenschaften
        ''' in eine Zeichenkette - auch bei einem Treffer im Speicher, und beim Ziehen eines Objekts
        ''' mit Ebenenmaske je Region-Patch und Bild.
        '''
        ''' CanvasBackgroundColor steht nicht drin: BuildMaskGeometry setzt sie fest auf
        ''' durchsichtig, sie kann sich also gar nicht unterscheiden.</summary>
        Private Shared Function MaskGeometryKey(geometry As ImageAdjustments) As String
            If geometry Is Nothing Then Return ""
            Return String.Join(":",
                KeyPart(geometry.CropLeftPercent), KeyPart(geometry.CropTopPercent),
                KeyPart(geometry.CropRightPercent), KeyPart(geometry.CropBottomPercent),
                KeyPart(geometry.RotationDegrees),
                KeyPart(geometry.FlipHorizontal), KeyPart(geometry.FlipVertical),
                KeyPart(geometry.StraightenDegrees),
                KeyPart(geometry.StraightenExpandCanvas),
                KeyPart(geometry.PerspectiveHorizontal),
                KeyPart(geometry.PerspectiveVertical),
                KeyPart(geometry.PerspectiveAspect),
                KeyPart(geometry.PerspectiveScale),
                KeyPart(geometry.PerspectiveCorner0X), KeyPart(geometry.PerspectiveCorner0Y),
                KeyPart(geometry.PerspectiveCorner1X), KeyPart(geometry.PerspectiveCorner1Y),
                KeyPart(geometry.PerspectiveCorner2X), KeyPart(geometry.PerspectiveCorner2Y),
                KeyPart(geometry.PerspectiveCorner3X), KeyPart(geometry.PerspectiveCorner3Y),
                GeometryOperationsSignature(geometry.GeometryOperations),
                ImageWarpSignature(geometry.ImageWarp),
                KeyPart(geometry.ResizeWidth), KeyPart(geometry.ResizeHeight),
                KeyPart(geometry.ResizeInterpolation),
                KeyPart(geometry.CanvasWidth), KeyPart(geometry.CanvasHeight),
                KeyPart(geometry.CanvasAnchor))
        End Function

        ''' <summary>Die Schritte gehen ueber DENSELBEN Fingerabdruck in den Maskenschluessel wie in
        ''' den Bildschluessel. Eine zweite Feldliste hier war eine Feldliste zu viel: sie fuehrte die
        ''' drei Schalter der Groessenaenderung nicht mit, waehrend die Maske durch dieselbe
        ''' Geometriekette laeuft wie das Bild.</summary>
        Private Shared Function GeometryOperationsSignature(operations As List(Of GeometryOperation)) As String
            Return GeometryOperationsKey(operations)
        End Function

        ''' <summary>Trägt dieses Objekt eine eigene Deckung, muss also über eine Ebene gezeichnet
        ''' werden? Ebenenmaske oder Schnittmaske. Auch der Kompositor fragt hier: beides entsteht
        ''' beim KOMPONIEREN und steckt nicht in der Objekt-Bitmap.</summary>
        Friend Shared Function UsesLayerCoverage(annotation As ImageAnnotation) As Boolean
            Return annotation IsNot Nothing AndAlso
                   (Not String.IsNullOrEmpty(annotation.MaskId) OrElse annotation.ClipToLayerBelow)
        End Function

        ''' <summary>Pipeline-Eingangsmaße, mit denen das Rastern einer Maske genau in der
        ''' angeforderten Ausgabegröße landet.
        '''
        ''' <see cref="BuildPersistentMaskForOutput"/> rastert die Maske zuerst in der EINGANGSgröße
        ''' der Geometriekette und schickt sie danach durch dieselbe Kette wie das Bild. Den
        ''' Korrekturebenen reicht die echte Eingangsgröße durch; beim Zeichnen der Objekte ist sie
        ''' nicht zur Hand, wohl aber die Ausgabegröße. Also wird zurückgerechnet: die Kette auf die
        ''' vollen Quellmaße der Maske angewendet ergibt die volle Ausgabe, deren Verhältnis zur
        ''' angeforderten Ausgabe ist der gesuchte Maßstab. Einen Rest fängt die Nachskalierung am
        ''' Ende von BuildPersistentMaskForOutput auf.</summary>
        Private Shared Function MaskPipelineInputSize(maskData As ImageMask, geometry As ImageAdjustments,
                                                      targetW As Integer, targetH As Integer) As SKSizeI
            Dim sw = maskData.SourceWidthPixels, sh = maskData.SourceHeightPixels
            If sw <= 0 OrElse sh <= 0 Then Return New SKSizeI(0, 0)
            Dim full = ComputeGeometryOutputSize(sw, sh, geometry)
            If full.Width <= 0 OrElse full.Height <= 0 Then Return New SKSizeI(sw, sh)
            Dim scale = Math.Min(targetW / CDbl(full.Width), targetH / CDbl(full.Height))
            If Double.IsNaN(scale) OrElse scale <= 0.0 OrElse scale >= 1.0 Then Return New SKSizeI(sw, sh)
            Return New SKSizeI(Math.Max(1, CInt(Math.Round(sw * scale))),
                               Math.Max(1, CInt(Math.Round(sh * scale))))
        End Function

        Private Class MaskCoverageEntry
            Public Property Key As String
            Public Property Coverage As Byte()
            Public Property LastUse As Long
        End Class

        ' Gemerkte Deckungsraster von OBJEKT-Ebenenmasken, in Ausgabegröße. Ohne das Merken baute der
        ' Region-Patch-Weg beim Ziehen für JEDEN Patch eine Maske über das ganze Bild auf - der Patch
        ' ist ein paar hundert Pixel groß, die Maske wäre das Vielfache davon. Der Schlüssel trägt
        ' Maskeninhalt, Geometrie und Zielmaße; ändert sich eines, entsteht ein neuer Eintrag.
        Private Shared ReadOnly _maskCoverageCache As New List(Of MaskCoverageEntry)()
        Private Shared ReadOnly _maskCoverageLock As New Object()
        Private Shared _maskCoverageClock As Long = 0
        Private Const MaskCoverageBudgetBytes As Long = 48L * 1024L * 1024L

        ''' <summary>Deckungsraster einer Objekt-Ebenenmaske in Ausgabegröße, ein Byte je Pixel,
        ''' Zeilenlänge = <paramref name="targetW"/>.
        '''
        ''' Das zurückgegebene Feld gehört dem Speicher und wird geteilt: NUR LESEN.</summary>
        Private Shared Function GetAnnotationMaskCoverage(maskData As ImageMask, geometry As ImageAdjustments,
                                                          targetW As Integer, targetH As Integer) As Byte()
            If maskData Is Nothing OrElse geometry Is Nothing OrElse targetW <= 0 OrElse targetH <= 0 Then Return Nothing
            Dim key = String.Join("|", MaskFingerprint(maskData),
                                  MaskGeometryKey(geometry),
                                  targetW, targetH)
            SyncLock _maskCoverageLock
                For Each entry In _maskCoverageCache
                    If String.Equals(entry.Key, key, StringComparison.Ordinal) Then
                        _maskCoverageClock += 1
                        entry.LastUse = _maskCoverageClock
                        ' Ein LEERES Feld ist der gemerkte Fall "diese Maske liefert nichts" - siehe
                        ' unten. Eine echte Deckung hat immer targetW mal targetH Bytes.
                        Return If(entry.Coverage.Length = 0, Nothing, entry.Coverage)
                    End If
                Next
            End SyncLock

            Dim input = MaskPipelineInputSize(maskData, geometry, targetW, targetH)
            If input.Width <= 0 OrElse input.Height <= 0 Then Return Nothing
            Dim coverage As Byte() = Nothing
            Using mask = BuildPersistentMaskForOutput(maskData, geometry, input.Width, input.Height,
                                                      targetW, targetH, 1.0F)
                ' Eine fehlende oder beschädigte Maske wirkt NICHT - und darf niemals dazu führen,
                ' dass das Objekt ganz verschwindet. Nothing heißt hier "volle Deckung".
                If mask IsNot Nothing Then
                    coverage = New Byte(targetW * targetH - 1) {}
                    Dim stride = mask.RowBytes
                    Dim raw = New Byte(stride * targetH - 1) {}
                    Marshal.Copy(mask.GetPixels(), raw, 0, raw.Length)
                    For y = 0 To targetH - 1
                        Array.Copy(raw, y * stride, coverage, y * targetW, targetW)
                    Next
                End If
            End Using

            SyncLock _maskCoverageLock
                _maskCoverageClock += 1
                ' AUCH DAS NICHTS WIRD GEMERKT. Ohne diesen Eintrag baute eine dauerhaft defekte
                ' Objektmaske beim Ziehen für JEDEN Region-Patch die volle Maske neu auf - der
                ' teuerste Weg für das Ergebnis "wirkt gar nicht".
                _maskCoverageCache.Add(New MaskCoverageEntry With {
                    .Key = key, .Coverage = If(coverage, Array.Empty(Of Byte)()), .LastUse = _maskCoverageClock})
                Dim total As Long = 0
                For Each entry In _maskCoverageCache
                    total += entry.Coverage.LongLength
                Next
                While _maskCoverageCache.Count > 1 AndAlso total > MaskCoverageBudgetBytes
                    Dim victim = _maskCoverageCache(0)
                    For Each entry In _maskCoverageCache
                        If entry.LastUse < victim.LastUse Then victim = entry
                    Next
                    total -= victim.Coverage.LongLength
                    _maskCoverageCache.Remove(victim)
                End While
            End SyncLock
            Return coverage
        End Function

        ''' <summary>Packt ein Alpha8-PNG in einen dichten Bytepuffer der erwarteten Groesse aus.
        ''' Nothing bei leerem String, unlesbaren Daten, falschem Farbtyp oder abweichender Groesse -
        ''' ein stiller Rueckfall auf halbe Deckung waere hier schlimmer als gar keine Korrektur.</summary>
        ''' <summary>Die Bildpunkte eines Rasters, aber nur wenn es die erwarteten Masse hat.
        ''' Sonst Nothing - der Aufrufer behandelt das wie ein fehlendes Raster.</summary>
        Private Shared Function PixelsOfSize(raster As AlphaRaster, width As Integer, height As Integer) As Byte()
            If raster Is Nothing OrElse raster.Width <> width OrElse raster.Height <> height Then Return Nothing
            Return raster.Pixels
        End Function

        ''' <summary>EINE Stelle, an der ein Maskenpinsel-Strich sein Ziel kennt. Die drei Fälle -
        ''' gemalte Maske, Verlauf mit Pinselkorrektur, kein Ziel - standen vorher als If-Zweige im
        ''' EditorViewModel verstreut, und genau dort ist derselbe Fehler dreimal passiert (Commit,
        ''' Abbruch und Live-Vorschau riefen den Auswahl-Weg, obwohl das Ziel ein Verlauf war).
        '''
        ''' Hier unten statt im ViewModel, weil das ViewModel headless nicht instanziierbar ist (sein
        ''' einziger Konstruktor verlangt das MainWindowViewModel) - alle Prüfungen dort können nur
        ''' Quelltext abgleichen. In der Engine sind es echte Verhaltensprüfungen.
        '''
        ''' <paramref name="strich"/> ist der Strich als Quellraum-Maske, wie ihn
        ''' CreateSourceMaskFromSelection liefert.
        '''
        ''' <paramref name="mode"/> ist der Verknuepfungsmodus der Modusreihe des Masken-Panels
        ''' ("Add", "Subtract", "Intersect"). Leer heisst: aus <paramref name="subtract"/> ableiten -
        ''' so bleiben die vorhandenen Aufrufe mit dem blossen Schalter unveraendert richtig.
        '''
        ''' STAND: die Anwendung ruft das bisher NUR fuer Verlaufsmasken. Striche auf eine gemalte
        ''' Ebenen-Maske laufen weiterhin ueber die Auswahl (ApplySelectionCandidate ->
        ''' WriteSelectionMaskBackToLayer), weil deren Raster zugleich die Quelle des roten Overlays
        ''' ist. MergePaintedMaskStroke ist der vorbereitete Weg dorthin und wird heute nur von der
        ''' Diagnose gefahren - siehe Audits/OFFENE_PUNKTE.md.</summary>
        Public Shared Function ApplyMaskBrushStroke(target As ImageMask, stroke As ImageMask,
                                                    subtract As Boolean,
                                                    Optional mode As String = "") As Boolean
            If target Is Nothing OrElse stroke Is Nothing Then Return False
            Dim effective = NormalizeMaskStrokeMode(mode, subtract)
            If target.IsGradient Then
                ' SCHNEIDEN laesst sich an einem GERECHNETEN Verlauf nicht ausdruecken: seine
                ' Pinselkorrektur sind zwei begrenzte Raster ueber der Verlaufsdeckung, und "nur
                ' behalten, was der Strich deckt" hiesse, alles AUSSERHALB des Strichs abzuziehen -
                ' ein Raster ueber das ganze Bild, genau der Aufwand, den der begrenzte Weg
                ' vermeidet. Der Strich bleibt deshalb wirkungslos, statt still zu einem
                ' Hinzufuegen zu werden und die Flaeche wachsen zu lassen.
                If String.Equals(effective, "Intersect", StringComparison.Ordinal) Then Return False
                Return MergeGradientBrushCorrection(target, stroke, String.Equals(effective, "Subtract", StringComparison.Ordinal))
            End If
            Return MergePaintedMaskStroke(target, stroke, String.Equals(effective, "Subtract", StringComparison.Ordinal), effective)
        End Function

        ''' <summary>Ein Pinselstrich in GENAU EINEN Bestandteil.
        '''
        ''' Der Bestandteil wird dafuer kurz als eigenstaendige Maske verpackt und danach
        ''' zurueckgelesen - dieselbe Rechnung wie am ersten Bestandteil, nur mit den Feldern des
        ''' angefassten. Ohne das landete jeder Strich in Bestandteil eins: bei einer Maske aus
        ''' Verlauf plus Pinsel also in der Pinselkorrektur des Verlaufs, und ein Strich in einem
        ''' Bereich, den ein SPAETERER Bestandteil deckt, aenderte die Summe dort gar nicht - er
        ''' wurde von diesem ueberschrieben.
        '''
        ''' Die Quellmasse gehoeren der MASKE, nicht dem Bestandteil: alle teilen denselben
        ''' Quellraum. Der MODUS des Bestandteils (hinzufuegen, abziehen, schneiden) gehoert
        ''' ebenfalls ihm und darf vom Strich nicht ueberschrieben werden - der Strich sagt nur,
        ''' wie er MIT DIESEM Bestandteil verrechnet wird.
        '''
        ''' Die Zuordnung Stelle zu Feldern ist dieselbe wie beim Zurueckschreiben eines Bestandteils
        ''' im ViewModel: Stelle 0 sind die Maskenfelder, alles weitere steht in
        ''' <see cref="ImageMask.ExtraComponents"/>.</summary>
        Public Shared Function ApplyMaskBrushStrokeToComponent(mask As ImageMask, componentIndex As Integer,
                                                              stroke As ImageMask, subtract As Boolean,
                                                              Optional mode As String = "") As Boolean
            If mask Is Nothing OrElse stroke Is Nothing Then Return False
            Dim components = mask.GetComponents()
            If componentIndex < 0 OrElse componentIndex >= components.Count Then Return False
            ' Der erste Bestandteil LIEGT in den Maskenfeldern - dort braucht es keinen Umweg.
            If componentIndex = 0 Then Return ApplyMaskBrushStroke(mask, stroke, subtract, mode)
            If mask.ExtraComponents Is Nothing OrElse componentIndex - 1 >= mask.ExtraComponents.Count Then Return False

            Dim wrapper As New ImageMask With {
                .SourceWidthPixels = mask.SourceWidthPixels,
                .SourceHeightPixels = mask.SourceHeightPixels
            }
            wrapper.SetPrimaryFromComponent(components(componentIndex))
            If Not ApplyMaskBrushStroke(wrapper, stroke, subtract, mode) Then Return False
            Dim updated = wrapper.PrimaryAsComponent()
            updated.Mode = components(componentIndex).Mode
            mask.ExtraComponents(componentIndex - 1) = updated
            Return True
        End Function

        ''' <summary>Der wirksame Verknuepfungsmodus eines Pinselstrichs. Ein leerer oder
        ''' unbekannter Modus faellt auf den Schalter zurueck; "New" ist hier kein eigener Fall,
        ''' der erste Strich auf eine leere Maske setzt sie ohnehin.</summary>
        Private Shared Function NormalizeMaskStrokeMode(mode As String, subtract As Boolean) As String
            If String.Equals(mode, "Intersect", StringComparison.OrdinalIgnoreCase) Then Return "Intersect"
            If String.Equals(mode, "Subtract", StringComparison.OrdinalIgnoreCase) Then Return "Subtract"
            If String.Equals(mode, "Add", StringComparison.OrdinalIgnoreCase) Then Return "Add"
            Return If(subtract, "Subtract", "Add")
        End Function

        ''' <summary>Strich in eine GEMALTE Maske einrechnen: hinzufügen nimmt das Maximum, abziehen
        ''' zieht ab, schneiden nimmt das Minimum. Das Rechteck wächst beim Hinzufügen mit; beim
        ''' Abziehen bleibt es stehen, statt es teuer neu zu vermessen - ein zu großes Rechteck mit
        ''' Nullen kostet nur etwas Speicher, ein zu kleines würde Maskenteile abschneiden. Beim
        ''' SCHNEIDEN schrumpft es auf die Ueberschneidung: alles ausserhalb ist danach leer.
        '''
        ''' <paramref name="mode"/> ist der Verknuepfungsmodus; leer heisst "aus subtract ableiten".</summary>
        Public Shared Function MergePaintedMaskStroke(mask As ImageMask, stroke As ImageMask,
                                                      subtract As Boolean,
                                                      Optional mode As String = "") As Boolean
            If mask Is Nothing OrElse stroke Is Nothing OrElse mask.IsGradient Then Return False
            If stroke.Right <= stroke.Left OrElse stroke.Bottom <= stroke.Top Then Return False
            Dim effective = NormalizeMaskStrokeMode(mode, subtract)
            Dim isIntersect = String.Equals(effective, "Intersect", StringComparison.Ordinal)
            subtract = String.Equals(effective, "Subtract", StringComparison.Ordinal)
            Dim sWidth = stroke.Right - stroke.Left, sHeight = stroke.Bottom - stroke.Top
            ' BEIDE Seiten kommen als Raster herein und gehen als Raster hinaus. Frueher wurde je
            ' Strich die ganze Maske entpackt, verrechnet und wieder gepackt - bei einer bildgrossen
            ' Maske ein halbe Sekunde, fuer einen Strich von wenigen Bildpunkten.
            Dim strokeRaster = stroke.Raster
            If strokeRaster Is Nothing OrElse strokeRaster.Width <> sWidth OrElse strokeRaster.Height <> sHeight Then Return False
            Dim strokeBuffer = strokeRaster.Pixels

            Dim oldWidth = mask.Right - mask.Left, oldHeight = mask.Bottom - mask.Top
            Dim oldBuffer As Byte() = Nothing
            If oldWidth > 0 AndAlso oldHeight > 0 Then
                Dim oldRaster = mask.Raster
                If oldRaster IsNot Nothing AndAlso oldRaster.Width = oldWidth AndAlso oldRaster.Height = oldHeight Then
                    oldBuffer = oldRaster.Pixels
                End If
            End If

            ' Ohne bisherige Maske ist ein ABZIEHEN gegenstandslos - sonst entstünde aus dem ersten
            ' Radiergummi-Strich eine leere Maske, die als "es gibt eine Maske" gilt. Fuer das
            ' SCHNEIDEN gilt dasselbe: der Schnitt mit nichts ist nichts.
            If oldBuffer Is Nothing Then
                If subtract OrElse isIntersect Then Return False
                mask.SourceWidthPixels = stroke.SourceWidthPixels
                mask.SourceHeightPixels = stroke.SourceHeightPixels
                mask.Left = stroke.Left : mask.Top = stroke.Top
                mask.Right = stroke.Right : mask.Bottom = stroke.Bottom
                mask.CopyPixelDataFrom(stroke)
                Return True
            End If

            ' SCHNEIDEN steht fuer sich: das Ergebnis ist die Ueberschneidung beider Rechtecke, und
            ' je Pixel das MINIMUM. Ueber den gemeinsamen Weg unten ginge es nicht - der kopiert die
            ' alte Maske mit einem nicht-negativen Versatz in den neuen Puffer, und beim Schneiden
            ' liegt das neue Rechteck INNERHALB des alten.
            If isIntersect Then
                Dim clipLeft = Math.Max(mask.Left, stroke.Left)
                Dim clipTop = Math.Max(mask.Top, stroke.Top)
                Dim clipRight = Math.Min(mask.Right, stroke.Right)
                Dim clipBottom = Math.Min(mask.Bottom, stroke.Bottom)
                Dim clipWidth = clipRight - clipLeft, clipHeight = clipBottom - clipTop
                If clipWidth > 0 AndAlso clipHeight > 0 Then
                    Dim clipped = New Byte(clipWidth * clipHeight - 1) {}
                    For y = 0 To clipHeight - 1
                        Dim maskRow = (y + clipTop - mask.Top) * oldWidth + (clipLeft - mask.Left)
                        Dim strokeRow = (y + clipTop - stroke.Top) * sWidth + (clipLeft - stroke.Left)
                        Dim targetRow = y * clipWidth
                        For x = 0 To clipWidth - 1
                            clipped(targetRow + x) = Math.Min(oldBuffer(maskRow + x), strokeBuffer(strokeRow + x))
                        Next
                    Next
                    Dim clippedRaster = New AlphaRaster(clipWidth, clipHeight, clipped)
                    If clippedRaster.HasCoverage() Then
                        mask.Left = clipLeft : mask.Top = clipTop
                        mask.Right = clipRight : mask.Bottom = clipBottom
                        mask.Raster = clippedRaster
                        Return True
                    End If
                End If
                ' Keine Ueberschneidung oder nichts uebrig: die Maske ist leer, nicht unveraendert.
                mask.PngBase64 = ""
                mask.Left = 0 : mask.Top = 0 : mask.Right = 0 : mask.Bottom = 0
                Return True
            End If

            Dim left = If(subtract, mask.Left, Math.Min(mask.Left, stroke.Left))
            Dim top = If(subtract, mask.Top, Math.Min(mask.Top, stroke.Top))
            Dim right = If(subtract, mask.Right, Math.Max(mask.Right, stroke.Right))
            Dim bottom = If(subtract, mask.Bottom, Math.Max(mask.Bottom, stroke.Bottom))
            Dim width = right - left, height = bottom - top
            If width <= 0 OrElse height <= 0 Then Return False

            Dim target = New Byte(width * height - 1) {}
            Dim dx0 = mask.Left - left, dy0 = mask.Top - top
            For y = 0 To oldHeight - 1
                Buffer.BlockCopy(oldBuffer, y * oldWidth, target, (y + dy0) * width + dx0, oldWidth)
            Next

            Dim sx0 = stroke.Left - left, sy0 = stroke.Top - top
            For y = 0 To sHeight - 1
                Dim zy = y + sy0
                If zy < 0 OrElse zy >= height Then Continue For
                Dim sRow = y * sWidth, zRow = zy * width
                For x = 0 To sWidth - 1
                    Dim zx = x + sx0
                    If zx < 0 OrElse zx >= width Then Continue For
                    Dim v = strokeBuffer(sRow + x)
                    If v = 0 Then Continue For
                    Dim i = zRow + zx
                    If subtract Then
                        target(i) = CByte(Math.Max(0, CInt(target(i)) - CInt(v)))
                    ElseIf v > target(i) Then
                        target(i) = v
                    End If
                Next
            Next

            Dim merged = New AlphaRaster(width, height, target)
            If Not merged.HasCoverage() Then
                ' Alles weggeradiert: die Maske ist leer, nicht "unverändert".
                mask.PngBase64 = ""
                mask.Left = 0 : mask.Top = 0 : mask.Right = 0 : mask.Bottom = 0
                Return True
            End If
            mask.Left = left : mask.Top = top : mask.Right = right : mask.Bottom = bottom
            mask.Raster = merged
            Return True
        End Function

        ''' <summary>Verrechnet einen Pinselstrich (als Quellraum-Maske, wie ihn
        ''' CreateSourceMaskFromSelection liefert) in die Pinselkorrektur eines VERLAUFS.
        '''
        ''' <paramref name="abziehen"/> steuert, in welches der beiden Raster der Strich geht. Beide
        ''' werden mit MAXIMUM verrechnet, nicht addiert: zweimal ueber dieselbe Stelle zu streichen
        ''' soll sie nicht "doppelt" wegnehmen (das gaebe sichtbare Stufen an Ueberlappungen), sondern
        ''' dasselbe Ergebnis liefern wie einmal. Ein Strich in die eine Richtung LOESCHT ausserdem
        ''' die Gegenrichtung an denselben Stellen - sonst liesse sich ein versehentliches Abziehen
        ''' nie wieder zurueckholen, weil beide Raster gegeneinander stehen blieben.
        '''
        ''' Gerechnet wird nur auf der VEREINIGUNG der bemalten Rechtecke, nicht auf dem ganzen Bild:
        ''' bei 50 MP waeren das sonst zwei Puffer von je 50 MB pro Strich.</summary>
        Public Shared Function MergeGradientBrushCorrection(mask As ImageMask, stroke As ImageMask,
                                                            subtract As Boolean) As Boolean
            If mask Is Nothing OrElse stroke Is Nothing OrElse Not mask.IsGradient Then Return False
            If stroke.Right <= stroke.Left OrElse stroke.Bottom <= stroke.Top Then Return False
            Dim sWidth = stroke.Right - stroke.Left, sHeight = stroke.Bottom - stroke.Top
            Dim strokeBuffer = PixelsOfSize(stroke.Raster, sWidth, sHeight)
            If strokeBuffer Is Nothing Then Return False

            Dim oldWidth = mask.BrushRight - mask.BrushLeft, oldHeight = mask.BrushBottom - mask.BrushTop
            Dim altHinzu As Byte() = Nothing, altWeg As Byte() = Nothing
            If mask.HasBrushCorrection Then
                altHinzu = PixelsOfSize(mask.BrushAddRaster, oldWidth, oldHeight)
                altWeg = PixelsOfSize(mask.BrushSubtractRaster, oldWidth, oldHeight)
            End If
            Dim hasOld = altHinzu IsNot Nothing OrElse altWeg IsNot Nothing

            Dim left = If(hasOld, Math.Min(mask.BrushLeft, stroke.Left), stroke.Left)
            Dim top = If(hasOld, Math.Min(mask.BrushTop, stroke.Top), stroke.Top)
            Dim right = If(hasOld, Math.Max(mask.BrushRight, stroke.Right), stroke.Right)
            Dim bottom = If(hasOld, Math.Max(mask.BrushBottom, stroke.Bottom), stroke.Bottom)
            Dim width = right - left, height = bottom - top
            If width <= 0 OrElse height <= 0 Then Return False

            Dim hinzu = New Byte(width * height - 1) {}
            Dim weg = New Byte(width * height - 1) {}
            If hasOld Then
                Dim dx = mask.BrushLeft - left, dy = mask.BrushTop - top
                For y = 0 To oldHeight - 1
                    If altHinzu IsNot Nothing Then Buffer.BlockCopy(altHinzu, y * oldWidth, hinzu, (y + dy) * width + dx, oldWidth)
                    If altWeg IsNot Nothing Then Buffer.BlockCopy(altWeg, y * oldWidth, weg, (y + dy) * width + dx, oldWidth)
                Next
            End If

            Dim targetTo = If(subtract, weg, hinzu)
            Dim gegen = If(subtract, hinzu, weg)
            Dim sx0 = stroke.Left - left, sy0 = stroke.Top - top
            For y = 0 To sHeight - 1
                Dim sRow = y * sWidth, zRow = (y + sy0) * width + sx0
                For x = 0 To sWidth - 1
                    Dim v = strokeBuffer(sRow + x)
                    If v = 0 Then Continue For
                    Dim i = zRow + x
                    If v > targetTo(i) Then targetTo(i) = v
                    ' Gegenrichtung an dieser Stelle zuruecknehmen (siehe Zusammenfassung).
                    Dim remainder = CInt(gegen(i)) - CInt(v)
                    gegen(i) = CByte(Math.Max(0, remainder))
                Next
            Next

            mask.BrushLeft = left
            mask.BrushTop = top
            mask.BrushRight = right
            mask.BrushBottom = bottom
            ' Ein Raster ohne jede Deckung ist KEINE Korrektur - sonst zaehlte ein leeres Paar
            ' weiter als "diese Maske traegt eine Pinselkorrektur".
            Dim addRaster = New AlphaRaster(width, height, hinzu)
            Dim subtractRaster = New AlphaRaster(width, height, weg)
            mask.BrushAddRaster = If(addRaster.HasCoverage(), addRaster, Nothing)
            mask.BrushSubtractRaster = If(subtractRaster.HasCoverage(), subtractRaster, Nothing)
            If Not mask.HasBrushCorrectionData Then
                mask.BrushLeft = 0 : mask.BrushTop = 0 : mask.BrushRight = 0 : mask.BrushBottom = 0
            End If
            Return True
        End Function

        ''' <summary>Friert die momentan aktive Auswahl (Anzeigeraum) als persistente Maske im
        ''' Quellraum ein.</summary>
        ''' <paramref name="displayBounds"/>: wenn gesetzt, wird NUR der Quellbereich abgetastet, der
        ''' auf dieses Anzeige-Rechteck abbilden kann - fuer einen Pinselstempel ist das ein Fleck
        ''' statt des ganzen Bildes. Gemessen kostete der volle Durchlauf bei 20 MP 2,3 Sekunden JE
        ''' STRICH; mit Grenze ist er vernachlaessigbar. Ohne den Parameter bleibt alles wie bisher.
        Public Shared Function CreateSourceMaskFromSelection(adj As ImageAdjustments,
                                                             Optional name As String = "Auswahlmaske",
                                                             Optional displayBounds As SKRectI? = Nothing,
                                                             Optional progress As IProgress(Of Double) = Nothing) As ImageMask
            If adj Is Nothing OrElse adj.SourceWidthPixels <= 0 OrElse adj.SourceHeightPixels <= 0 Then Return Nothing
            Dim displaySize = ComputeGeometryOutputSize(adj.SourceWidthPixels, adj.SourceHeightPixels, adj)
            Dim sourceW = adj.SourceWidthPixels, sourceH = adj.SourceHeightPixels
            ' Der haeufigste Editorfall: die Auswahl stammt schon aus genau diesem Bildraum. Ihr
            ' Raster IST dann bereits das dauerhafte Maskenformat und braucht die Rueckprojektion
            ' ueber jeden Bildpunkt nicht. Gespart wird dabei vor allem das NEU SCHREIBEN des PNG -
            ' der teure Teil; gelesen wird es weiterhin, denn nur so bleiben leere Auswahl und
            ' enges Rechteck genauso behandelt wie auf dem langen Weg.
            Dim reuseHandled = False
            Dim reused = TryReuseSelectionMaskAsSourceMask(adj, name, reuseHandled)
            If reuseHandled Then Return reused
            Try
                ' Ohne Raster ist die Auswahl eine reine Geometrie (Rechteck) - dann sagen die
                ' Prozentfelder weiter unten, was drin liegt.
                Dim selectionRaster = If(adj.HasSelectionMaskData, adj.SelectionMaskRaster, Nothing)
                If adj.HasSelectionMaskData AndAlso selectionRaster Is Nothing Then Return Nothing
                Dim dStride = If(selectionRaster Is Nothing, 0, selectionRaster.Width)
                Dim dBuf As Byte() = If(selectionRaster Is Nothing, Nothing, selectionRaster.Pixels)
                Dim full = New Byte(sourceW * sourceH - 1) {}
                Dim left = sourceW, top = sourceH, right = 0, bottom = 0
                ' Abtastgrenzen: den RAND des Anzeige-Rechtecks in Schritten in den Quellraum
                ' zuruecklegen und die Huelle der Treffer nehmen. Frueher reichten die vier Ecken,
                ' kommentiert mit "die Abbildung ist affin" - das Knotenraster ist aber keine:
                ' das Urbild eines Rechtecks kann ausserhalb der Eckenhuelle liegen, und eine
                ' gewoelbte Auswahl wurde beim Einfrieren an den gewoelbten Stellen abgeschnitten.
                ' Ohne Verzerrung liegen die Extreme weiter auf den Ecken, dann aendert die
                ' Randabtastung nichts. Ein paar Pixel Rand fangen Rundung ab.
                Dim vonY = 0, bisY = sourceH - 1, vonX = 0, bisX = sourceW - 1
                If displayBounds.HasValue Then
                    Dim db = displayBounds.Value
                    Dim minSx = Double.MaxValue, minSy = Double.MaxValue
                    Dim maxSx = Double.MinValue, maxSy = Double.MinValue
                    Dim randpunkte As New List(Of (X As Double, Y As Double))()
                    Dim schritt = Math.Max(4, Math.Min(db.Width, db.Height) \ 32)
                    For x = db.Left To db.Right Step schritt
                        randpunkte.Add((CDbl(x), CDbl(db.Top)))
                        randpunkte.Add((CDbl(x), CDbl(db.Bottom)))
                    Next
                    For y = db.Top To db.Bottom Step schritt
                        randpunkte.Add((CDbl(db.Left), CDbl(y)))
                        randpunkte.Add((CDbl(db.Right), CDbl(y)))
                    Next
                    ' Die Ecke rechts unten, falls der Schritt sie verfehlt.
                    randpunkte.Add((CDbl(db.Right), CDbl(db.Bottom)))
                    Dim alleGetroffen = True
                    For Each randpunkt In randpunkte
                        Dim sp As SKPoint
                        If Not TryGeometryOutputToSourcePoint(randpunkt.X, randpunkt.Y, sourceW, sourceH, adj, sp) Then
                            alleGetroffen = False
                            Exit For
                        End If
                        minSx = Math.Min(minSx, sp.X) : maxSx = Math.Max(maxSx, sp.X)
                        minSy = Math.Min(minSy, sp.Y) : maxSy = Math.Max(maxSy, sp.Y)
                    Next
                    ' Faellt ein Randpunkt aus dem Bild, wird nicht begrenzt - lieber langsam als
                    ' abgeschnitten.
                    If alleGetroffen Then
                        vonX = Math.Max(0, CInt(Math.Floor(minSx)) - 2)
                        vonY = Math.Max(0, CInt(Math.Floor(minSy)) - 2)
                        bisX = Math.Min(sourceW - 1, CInt(Math.Ceiling(maxSx)) + 2)
                        bisY = Math.Min(sourceH - 1, CInt(Math.Ceiling(maxSy)) + 2)
                    End If
                End If
                Dim totalRows = Math.Max(1, bisY - vonY + 1)
                Dim lastReportedPercent = -1
                For sy = vonY To bisY
                    For sx = vonX To bisX
                        Dim dp As SKPoint
                        If Not TrySourcePointToGeometryOutput(sx + 0.5, sy + 0.5, sourceW, sourceH, adj, dp) Then Continue For
                        Dim dx = CInt(Math.Floor(dp.X)), dy = CInt(Math.Floor(dp.Y))
                        If dx < 0 OrElse dy < 0 OrElse dx >= displaySize.Width OrElse dy >= displaySize.Height Then Continue For
                        Dim alpha As Byte
                        If selectionRaster IsNot Nothing Then
                            Dim lx = dx - adj.SelectionMaskLeft, ly = dy - adj.SelectionMaskTop
                            If lx < 0 OrElse ly < 0 OrElse lx >= selectionRaster.Width OrElse ly >= selectionRaster.Height Then Continue For
                            alpha = dBuf(ly * dStride + lx)
                        Else
                            Dim inside = dx >= displaySize.Width * adj.SelectionXPercent / 100.0 AndAlso
                                         dy >= displaySize.Height * adj.SelectionYPercent / 100.0 AndAlso
                                         dx < displaySize.Width * (adj.SelectionXPercent + adj.SelectionWidthPercent) / 100.0 AndAlso
                                         dy < displaySize.Height * (adj.SelectionYPercent + adj.SelectionHeightPercent) / 100.0
                            If Not inside Then Continue For
                            alpha = 255
                        End If
                        full(sy * sourceW + sx) = alpha
                        If alpha > 0 Then
                            left = Math.Min(left, sx) : top = Math.Min(top, sy)
                            right = Math.Max(right, sx + 1) : bottom = Math.Max(bottom, sy + 1)
                        End If
                    Next
                    ' Diese Umrechnung kann bei einer bildgrossen Tiefenmaske mehrere Sekunden
                    ' dauern. Nicht jede Zeile melden: der UI-Faden soll Fortschritt zeigen, nicht
                    ' von zehntausenden Dispatcher-Auftraegen beschaeftigt sein.
                    Dim percent = CInt((CLng(sy - vonY + 1) * 100L) \ totalRows)
                    If percent > lastReportedPercent Then
                        lastReportedPercent = percent
                        progress?.Report(percent / 100.0)
                    End If
                Next
                If right <= left OrElse bottom <= top Then Return Nothing

                ' Das Ergebnis bleibt ein RASTER. Gepackt wird es erst, wenn das Rezept in die
                ' Datei geht - siehe ImageMask.PngBase64.
                Dim fullRaster = New AlphaRaster(sourceW, sourceH, full)
                Return New ImageMask With {
                    .Name = If(String.IsNullOrWhiteSpace(name), LocalizationService.T("Auswahlmaske"), name),
                    .SourceWidthPixels = sourceW, .SourceHeightPixels = sourceH,
                    .Left = left, .Top = top, .Right = right, .Bottom = bottom,
                    .Raster = fullRaster.Crop(New SKRectI(left, top, right, bottom)),
                    .FeatherPixels = adj.SelectionFeatherPixels
                }
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Der Kurzweg von <see cref="CreateSourceMaskFromSelection"/>: liegt die Auswahl
        ''' bereits im Quellraum, entsteht die Maske direkt aus ihrem Raster statt aus der
        ''' Rueckprojektion. <paramref name="handled"/> sagt, ob dieser Weg zustaendig war - NUR
        ''' dann ist sein Ergebnis verbindlich, ein Nothing also die Antwort "diese Auswahl deckt
        ''' nichts" und nicht "bitte den langen Weg nehmen".
        '''
        ''' Die Grenzen werden dabei genau wie auf dem langen Weg auf die tatsaechlich gedeckten
        ''' Bildpunkte gezogen. Ohne das truege die Maske das (moeglicherweise viel weitere)
        ''' Auswahlrechteck, und der Abgleich gegen vorhandene Masken - er vergleicht Rechteck UND
        ''' Inhalt - fande dieselbe Form nicht wieder.</summary>
        Private Shared Function TryReuseSelectionMaskAsSourceMask(adj As ImageAdjustments, name As String,
                                                                   ByRef handled As Boolean) As ImageMask
            handled = False
            Dim sourceW = adj.SourceWidthPixels, sourceH = adj.SourceHeightPixels
            If Not adj.HasSelectionMaskData OrElse
               adj.SelectionMaskLeft < 0 OrElse adj.SelectionMaskTop < 0 OrElse
               adj.SelectionMaskRight <= adj.SelectionMaskLeft OrElse
               adj.SelectionMaskBottom <= adj.SelectionMaskTop OrElse
               adj.SelectionMaskRight > sourceW OrElse adj.SelectionMaskBottom > sourceH Then Return Nothing
            If Not HasIdentityMaskGeometry(adj) Then Return Nothing

            Dim width = adj.SelectionMaskRight - adj.SelectionMaskLeft
            Dim height = adj.SelectionMaskBottom - adj.SelectionMaskTop
            Dim raster = adj.SelectionMaskRaster
            If raster Is Nothing OrElse raster.Width <> width OrElse raster.Height <> height Then Return Nothing

            ' AB HIER ist der Kurzweg zustaendig: das Raster liegt vor und passt zum Rechteck.
            handled = True
            Dim bounds = raster.CoverageBounds()
            If bounds.Width <= 0 OrElse bounds.Height <= 0 Then Return Nothing
            Dim cut = raster.Crop(bounds)
            If cut Is Nothing Then
                handled = False
                Return Nothing
            End If
            Return New ImageMask With {
                .Name = If(String.IsNullOrWhiteSpace(name), LocalizationService.T("Auswahlmaske"), name),
                .SourceWidthPixels = sourceW, .SourceHeightPixels = sourceH,
                .Left = adj.SelectionMaskLeft + bounds.Left, .Top = adj.SelectionMaskTop + bounds.Top,
                .Right = adj.SelectionMaskLeft + bounds.Right, .Bottom = adj.SelectionMaskTop + bounds.Bottom,
                .Raster = cut,
                .FeatherPixels = adj.SelectionFeatherPixels
            }
        End Function

        ''' <summary>Schneidet ein Alpha-Raster auf das angegebene Rechteck und kodiert es als PNG.</summary>
        ''' Der Puffer heisst bewusst NICHT "buffer": ein lokaler Name verdeckt in VB die Klasse
        ''' <c>System.Buffer</c>, und <c>Buffer.BlockCopy</c> darunter uebersetzt nicht mehr.
        Private Shared Function EncodeCroppedAlphaRaster(raster As Byte(), stride As Integer,
                                                          left As Integer, top As Integer,
                                                          right As Integer, bottom As Integer) As String
            Using cropped = New SKBitmap(right - left, bottom - top, SKColorType.Alpha8, SKAlphaType.Premul)
                Dim cStride = cropped.RowBytes
                Dim cBuf = New Byte(cStride * cropped.Height - 1) {}
                For y = 0 To cropped.Height - 1
                    Buffer.BlockCopy(raster, (top + y) * stride + left, cBuf, y * cStride, cropped.Width)
                Next
                Marshal.Copy(cBuf, 0, cropped.GetPixels(), cBuf.Length)
                Using image = SKImage.FromBitmap(cropped)
                    Using data = image.Encode(SKEncodedImageFormat.Png, FastPngCompressionQuality)
                        Return Convert.ToBase64String(data.ToArray())
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Eine Maske, die ueberall voll deckt. Ausgangspunkt fuer "Ebenenmaske hinzufuegen"
        ''' ohne aktive Auswahl: erst deckt sie alles, dann nimmt der Masken-Pinsel weg.
        '''
        ''' Sie liegt in denselben Quellmassen wie jede andere Maske. Ein winziges Raster waere
        ''' billiger und beim RENDERN auch richtig - <see cref="BuildSelectionMaskFromLayerMask"/>
        ''' rechnet aber mit Maskenkoordinaten IM QUELLRAUM DES BILDES, und ein Massstab dazwischen
        ''' liesse die Maske beim Bearbeiten daneben liegen.</summary>
        Public Shared Function CreateFullCoverageMask(adj As ImageAdjustments, name As String) As ImageMask
            If adj Is Nothing OrElse adj.SourceWidthPixels <= 0 OrElse adj.SourceHeightPixels <= 0 Then Return Nothing
            Dim w = adj.SourceWidthPixels, h = adj.SourceHeightPixels
            Try
                Dim pixels = New Byte(w * h - 1) {}
                Array.Fill(pixels, CByte(255))
                Return New ImageMask With {
                    .Name = If(String.IsNullOrWhiteSpace(name), LocalizationService.T("Ebenenmaske"), name),
                    .SourceWidthPixels = w, .SourceHeightPixels = h,
                    .Left = 0, .Top = 0, .Right = w, .Bottom = h,
                    .Raster = New AlphaRaster(w, h, pixels)
                }
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Miniatur einer MASKE fuer das Ebenenpanel: hell, wo sie deckt, dunkel, wo nicht -
        ''' im Seitenverhaeltnis des BILDES, damit man sieht, wo im Bild sie liegt.
        '''
        ''' Gerechnet wird auf dem zusammengesetzten Quellraster, also ueber ALLE Bestandteile.
        ''' Ohne Bild-Seitenverhaeltnis waere die Miniatur zwar groesser, aber ohne Aussage: bei
        ''' mehreren Korrekturebenen ist genau die Frage, WELCHE Maske WO liegt.</summary>
        Public Shared Function BuildMaskThumbnail(mask As ImageMask, boxSize As Integer) As SKBitmap
            If mask Is Nothing OrElse boxSize <= 0 Then Return Nothing
            If mask.SourceWidthPixels <= 0 OrElse mask.SourceHeightPixels <= 0 Then Return Nothing
            Dim raster As SKBitmap = Nothing
            Try
                ' KLEIN rastern, nicht in Quellgroesse und dann verkleinern: die Maske eines
                ' 45-Megapixel-Fotos ergaebe ein 45-MB-Raster - je Maske, bei jedem Aufbau des
                ' Panels. Der Rasterisierer nimmt die Zielgroesse ohnehin als Parameter.
                Dim size = FitIntoBox(mask.SourceWidthPixels, mask.SourceHeightPixels, boxSize)
                raster = BuildCombinedMaskRaster(mask, size.Width, size.Height)
                If raster Is Nothing Then Return Nothing
                Dim thumb = New SKBitmap(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(thumb)
                    ' Der Grund ist dunkel, die Deckung hell - dieselbe Lesart wie eine Maske im
                    ' Graustufenblick. Ein transparenter Grund liesse die Zeile durchscheinen und
                    ' machte "deckt nicht" von "keine Maske" ununterscheidbar.
                    canvas.Clear(New SKColor(32, 32, 32, 255))
                    Dim dst = New SKRect(0, 0, size.Width, size.Height)
                    Using paint = New SKPaint With {.Color = SKColors.White}
                        ' Alpha8 zeichnet als DECKUNG der Farbe - genau das, was die Maske meint.
                        DrawBitmapSampled(canvas, raster, New SKRect(0, 0, raster.Width, raster.Height),
                                          dst, SamplingHigh, paint)
                    End Using
                End Using
                Return thumb
            Catch
                Return Nothing
            Finally
                raster?.Dispose()
            End Try
        End Function

        ''' <summary>So viele Zeichen zeigt die Miniatur eines Textobjekts.</summary>
        Public Const ThumbnailTextLength As Integer = 5

        ''' <summary>Miniatur eines OBJEKTS: sein Inhalt, gezeichnet mit demselben Weg wie im Bild
        ''' (<see cref="DrawAnnotationOnCanvas"/>), nur in einen kleinen Kasten eingepasst. Bezug ist
        ''' das Rechteck des Objekts, nicht das Bild - sonst waere ein kleines Objekt in der Miniatur
        ''' ein Punkt.</summary>
        Public Shared Function BuildAnnotationThumbnail(annotation As ImageAnnotation,
                                                        sourceWidth As Integer, sourceHeight As Integer,
                                                        boxSize As Integer) As SKBitmap
            If annotation Is Nothing OrElse boxSize <= 0 Then Return Nothing
            If sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return Nothing
            Try
                Dim kind = If(annotation.Kind, "Text").Trim().ToLowerInvariant()
                ' TEXT: nur die ersten Zeichen. Ein ganzer Satz auf dreissig Punkte gequetscht ist
                ' ein grauer Strich - fuenf Zeichen sagen, WAS auf der Ebene steht. Die Breite geht
                ' anteilig mit, sonst stuende der gekuerzte Text winzig links in einem Kasten, der
                ' noch fuer den vollen Text bemessen ist.
                If (kind = "text" OrElse kind = "watermark") AndAlso
                   Not String.IsNullOrEmpty(annotation.Text) AndAlso
                   annotation.Text.Length > ThumbnailTextLength Then
                    Dim shortened = annotation.Clone()
                    Dim keptShare = ThumbnailTextLength / CSng(annotation.Text.Length)
                    shortened.Text = annotation.Text.Substring(0, ThumbnailTextLength)
                    shortened.WidthPixels = Math.Max(1.0F, annotation.WidthPixels * keptShare)
                    annotation = shortened
                End If
                Dim rect = ComputeAnnotationRect(sourceWidth, sourceHeight, kind, annotation)
                Dim rectWidth = Math.Max(1.0F, rect.Width), rectHeight = Math.Max(1.0F, rect.Height)
                ' BILD-Objekte bekommen einen eigenen, schlanken Weg: der gewoehnliche Zeichenweg
                ' dekodiert die Datei in VOLLER Aufloesung, um sie auf dreissig Punkte zu malen -
                ' gemessen 97 ms je Ebene, und bei mehreren Bildebenen ist das die Pause, die man
                ' beim Ebenenwechsel spuert. Hier wird gleich verkleinert dekodiert.
                If (kind = "image" OrElse kind = "selectionimage") AndAlso
                   Not String.IsNullOrWhiteSpace(annotation.ImagePath) AndAlso File.Exists(annotation.ImagePath) Then
                    Dim fromImageFile = BuildImageThumbnailFromFile(annotation.ImagePath, boxSize)
                    If fromImageFile IsNot Nothing Then Return fromImageFile
                End If
                Dim size = FitIntoBox(CInt(Math.Ceiling(rectWidth)), CInt(Math.Ceiling(rectHeight)), boxSize)
                Dim thumb = New SKBitmap(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(thumb)
                    canvas.Clear(SKColors.Transparent)
                    ' Auf den Kasten skalieren und das Objektrechteck in den Ursprung legen. Gezeichnet
                    ' wird danach unveraendert - Schrift, Form, Bild und Strich kommen alle aus
                    ' derselben Routine wie im Foto.
                    canvas.Scale(size.Width / rectWidth, size.Height / rectHeight)
                    canvas.Translate(-rect.Left, -rect.Top)
                    DrawAnnotationOnCanvas(canvas, kind, annotation, rect, sourceWidth, sourceHeight)
                End Using
                Return thumb
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Miniatur direkt aus einer Bilddatei - VERKLEINERT dekodiert.
        '''
        ''' Der Kodierer liefert die Datei auf Wunsch schon kleiner (`GetScaledDimensions`), und
        ''' genau darum geht es: fuer einen Kasten von dreissig Punkten ein 24-Megapixel-Foto voll
        ''' zu dekodieren kostet rund hundert Millisekunden - je Ebene, bei jedem Aufbau des
        ''' Panels.</summary>
        Private Shared Function BuildImageThumbnailFromFile(path As String, boxSize As Integer) As SKBitmap
            Try
                Using stream = File.OpenRead(path)
                    Using codec = SKCodec.Create(stream)
                        If codec Is Nothing Then Return Nothing
                        Dim info = codec.Info
                        If info.Width <= 0 OrElse info.Height <= 0 Then Return Nothing
                        Dim size = FitIntoBox(info.Width, info.Height, boxSize)
                        Dim wantedScale = Math.Max(size.Width, size.Height) / CSng(Math.Max(info.Width, info.Height))
                        Dim codecSize = codec.GetScaledDimensions(wantedScale)
                        Dim decodeInfo = New SKImageInfo(Math.Max(1, codecSize.Width), Math.Max(1, codecSize.Height),
                                                         SKColorType.Bgra8888, SKAlphaType.Premul)
                        Using decoded = New SKBitmap(decodeInfo)
                            Dim decodeResult = codec.GetPixels(decodeInfo, decoded.GetPixels())
                            If decodeResult <> SKCodecResult.Success AndAlso decodeResult <> SKCodecResult.IncompleteInput Then Return Nothing
                            ' Der Kodierer trifft die gewuenschte Groesse nur in seinen eigenen
                            ' Stufen (haelftig bei JPEG) - der Rest ist eine gewoehnliche Skalierung
                            ' auf einem bereits kleinen Bild und kostet nichts mehr.
                            Dim thumb = New SKBitmap(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                            Using canvas = New SKCanvas(thumb)
                                canvas.Clear(SKColors.Transparent)
                                DrawBitmapSampled(canvas, decoded, New SKRect(0, 0, decoded.Width, decoded.Height),
                                                  New SKRect(0, 0, size.Width, size.Height), SamplingHigh, Nothing)
                            End Using
                            Return thumb
                        End Using
                    End Using
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Groesse, die ein Bild mit diesem Seitenverhaeltnis in einem quadratischen Kasten
        ''' bekommt - mindestens ein Bildpunkt je Seite.</summary>
        Private Shared Function FitIntoBox(width As Integer, height As Integer, boxSize As Integer) As (Width As Integer, Height As Integer)
            If width <= 0 OrElse height <= 0 Then Return (boxSize, boxSize)
            Dim scale = Math.Min(boxSize / CDbl(width), boxSize / CDbl(height))
            Return (Math.Max(1, CInt(Math.Round(width * scale))),
                    Math.Max(1, CInt(Math.Round(height * scale))))
        End Function

        ''' <summary>Alle Bestandteile einer Maske zu EINEM Alpha8-Raster in QUELLGROESSE
        ''' zusammensetzen - dieselbe Rechnung wie im Renderweg, nur ohne die Geometriestufen
        ''' danach. Nothing, wenn kein Bestandteil etwas liefert.</summary>
        Private Shared Function BuildCombinedMaskRaster(mask As ImageMask) As SKBitmap
            If mask Is Nothing Then Return Nothing
            Return BuildCombinedMaskRaster(mask, mask.SourceWidthPixels, mask.SourceHeightPixels)
        End Function

        ''' <summary>Dasselbe in einer beliebigen ZIELGROESSE - der Rasterisierer nimmt sie ohnehin
        ''' entgegen. Fuer eine Miniatur ist das der Unterschied zwischen einem Raster von dreissig
        ''' Punkten und einem von fuenfundvierzig Megapixeln.</summary>
        Private Shared Function BuildCombinedMaskRaster(mask As ImageMask,
                                                        targetWidth As Integer, targetHeight As Integer) As SKBitmap
            If mask Is Nothing OrElse mask.SourceWidthPixels <= 0 OrElse mask.SourceHeightPixels <= 0 Then Return Nothing
            If targetWidth <= 0 OrElse targetHeight <= 0 Then Return Nothing
            Dim components = mask.GetComponents()
            If components.Count = 0 Then Return Nothing
            Dim combined As SKBitmap = Nothing
            For Each component In components
                ' Wie im Renderweg: ausgeschaltet heisst uebersprungen. Sonst zeigte die Miniatur
                ' eine andere Form als das Bild.
                If Not component.IsVisible Then Continue For
                Dim part = BuildComponentMaskForInput(component, mask.SourceWidthPixels, mask.SourceHeightPixels,
                                                      targetWidth, targetHeight, Nothing)
                If part Is Nothing Then Continue For
                If combined Is Nothing Then
                    combined = part
                Else
                    CombineMaskInto(combined, part, component.Mode)
                    part.Dispose()
                End If
            Next
            ' Dieselbe Umkehrung wie im Renderweg - sonst zeigten Miniatur und Arbeitskopie eine
            ' andere Form als das Bild. Die DICHTE bleibt hier bewusst draussen (sie wuerde beim
            ' Zurueckschreiben ein zweites Mal wirken), die Umkehrung gehoert dagegen zur Form.
            If mask.InvertResult Then InvertAlphaMaskInPlace(combined)
            Return combined
        End Function

        ''' <summary>Inverse zu CreateSourceMaskFromSelection: projiziert eine Ebenen-Maske (ImageMask,
        ''' Quellraum) in den ANZEIGE-Bildraum und liefert eine Alpha8-Maske + Rechteck für die editierbare
        ''' Auswahlmaske (_selectionMask). Damit lässt sich eine Ebenen-Maske im Masken-Pinsel wieder als
        ''' rotes Overlay anzeigen und per +/- ändern. Inverted wird beim Sampeln aufgelöst (die editierbare
        ''' Kopie ist danach nicht invertiert); die weiche Kante (FeatherPixels) bleibt Sache der Ebene und
        ''' wird hier NICHT eingerechnet - der Pinsel bearbeitet die harte Form, der Feather wirkt beim
        ''' Rendern (BuildPersistentMaskForOutput).
        '''
        ''' Gelesen wird die SUMME ALLER BESTANDTEILE, nicht nur der erste. Vorher stand hier
        ''' `mask.PngBase64`, also allein der erste: ein per Plus angehaengter Verlauf wirkte im Bild
        ''' weiter, war beim Bearbeiten aber unsichtbar - es sah aus, als waeren frühere
        ''' Bearbeitungen verloren (Nutzerbefund 2026-08-04). Zusammengesetzt wird mit DENSELBEN
        ''' Bausteinen wie beim Rendern (BuildComponentMaskForInput plus CombineMaskInto), damit
        ''' Anzeige und Ergebnis nicht auseinanderlaufen koennen.</summary>
        Public Shared Function BuildSelectionMaskFromLayerMask(mask As ImageMask, adj As ImageAdjustments,
                                                               ByRef rectPx As SKRectI) As SKBitmap
            rectPx = SKRectI.Empty
            If mask Is Nothing OrElse adj Is Nothing OrElse adj.SourceWidthPixels <= 0 OrElse adj.SourceHeightPixels <= 0 Then Return Nothing
            Dim profile = Diagnostics.Stopwatch.StartNew()

            ' Der bei weitem haeufigste Fall: eine einzige, gemalte Pinselmaske auf einem Bild ohne
            ' Geometrieaenderung. Das PNG liegt bereits exakt im Anzeige- (= Quell-)raum und ist
            ' auf seine belegte Flaeche beschnitten. Es erst auf ein 20/45-MP-Vollbild aufzublasen,
            ' dann jeden Bildpunkt rueckzuprojezieren und danach wieder auf denselben kleinen
            ' Ausschnitt zu schneiden, machte das erneute Anklicken selbst einer 300x300-Maske
            ' sekundenlang. Die Anzahl der Striche ist fuer die fertige Alpha-Maske unerheblich;
            ' entscheidend ist, dass wir ihren kompakten Raster direkt weiterreichen.
            Dim direct = TryDecodeDirectSelectionMask(mask, adj, rectPx)
            If direct IsNot Nothing Then
                Dim directRect = rectPx
                TraceMaskProfile(Function() $"Raster direkt id={ShortMaskId(mask.Id)} " &
                                            $"{directRect.Width}x{directRect.Height} in {profile.ElapsedMilliseconds}ms")
                Return direct
            End If

            Dim displaySize = ComputeGeometryOutputSize(adj.SourceWidthPixels, adj.SourceHeightPixels, adj)
            If displaySize.Width <= 0 OrElse displaySize.Height <= 0 Then Return Nothing

            Dim decoded As SKBitmap = Nothing
            Try
                Dim phase = Diagnostics.Stopwatch.StartNew()
                decoded = BuildCombinedMaskRaster(mask)
                If decoded Is Nothing Then Return Nothing
                Dim combineMs = phase.ElapsedMilliseconds
                Dim mStride = decoded.RowBytes
                Dim mBuf = New Byte(mStride * decoded.Height - 1) {}
                Marshal.Copy(decoded.GetPixels(), mBuf, 0, mBuf.Length)

                Dim dw = displaySize.Width, dh = displaySize.Height
                Dim sourceW = adj.SourceWidthPixels, sourceH = adj.SourceHeightPixels
                Dim full = New Byte(dw * dh - 1) {}
                Dim left = dw, top = dh, right = 0, bottom = 0
                For dy = 0 To dh - 1
                    For dx = 0 To dw - 1
                        Dim sp As SKPoint
                        If Not TryGeometryOutputToSourcePoint(dx + 0.5, dy + 0.5, sourceW, sourceH, adj, sp) Then Continue For
                        ' Das zusammengesetzte Raster hat QUELLGROESSE und liegt bei 0,0 - der
                        ' Versatz der einzelnen Bestandteile steckt schon darin. Inverted ebenso:
                        ' jeder Bestandteil bringt seine eigene Umkehrung mit.
                        Dim mx = CInt(Math.Floor(sp.X))
                        Dim my = CInt(Math.Floor(sp.Y))
                        Dim alpha As Byte = 0
                        If mx >= 0 AndAlso my >= 0 AndAlso mx < decoded.Width AndAlso my < decoded.Height Then
                            alpha = mBuf(my * mStride + mx)
                        End If
                        If alpha = 0 Then Continue For
                        full(dy * dw + dx) = alpha
                        left = Math.Min(left, dx) : top = Math.Min(top, dy)
                        right = Math.Max(right, dx + 1) : bottom = Math.Max(bottom, dy + 1)
                    Next
                Next
                Dim projectMs = phase.ElapsedMilliseconds - combineMs
                If right <= left OrElse bottom <= top Then Return Nothing

                Dim result = New SKBitmap(right - left, bottom - top, SKColorType.Alpha8, SKAlphaType.Premul)
                Dim cStride = result.RowBytes
                Dim cBuf = New Byte(cStride * result.Height - 1) {}
                For y = 0 To result.Height - 1
                    Buffer.BlockCopy(full, (top + y) * dw + left, cBuf, y * cStride, result.Width)
                Next
                Marshal.Copy(cBuf, 0, result.GetPixels(), cBuf.Length)
                rectPx = New SKRectI(left, top, right, bottom)
                Dim resultRect = rectPx
                Dim sourceRaster = $"{decoded.Width}x{decoded.Height}"
                TraceMaskProfile(Function() $"Raster projiziert id={ShortMaskId(mask.Id)} " &
                                            $"quelle={sourceRaster} anzeige={dw}x{dh} " &
                                            $"ergebnis={resultRect.Width}x{resultRect.Height} kombiniert={combineMs}ms " &
                                            $"projektion={projectMs}ms gesamt={profile.ElapsedMilliseconds}ms")
                Return result
            Catch
                Return Nothing
            Finally
                decoded?.Dispose()
            End Try
        End Function

        ''' <summary>Liefert das kompakte Raster einer einfachen gemalten Maske direkt. Jede
        ''' Geometrie, Umkehrung oder mehrere Bestandteile fallen bewusst auf den allgemeinen Weg
        ''' zurueck, weil nur der garantiert dieselbe zusammengesetzte Form erzeugen kann.</summary>
        Private Shared Function TryDecodeDirectSelectionMask(mask As ImageMask, adj As ImageAdjustments,
                                                              ByRef rectPx As SKRectI) As SKBitmap
            If mask Is Nothing OrElse adj Is Nothing Then Return Nothing
            If mask.IsDisabled OrElse mask.Inverted OrElse mask.InvertResult OrElse Not mask.PrimaryVisible Then
                TraceMaskProfile(Function() $"Direktpfad aus: zustand disabled={mask.IsDisabled} inverted={mask.Inverted} resultInvert={mask.InvertResult} sichtbar={mask.PrimaryVisible}")
                Return Nothing
            End If
            ' Nur ein echter VERLAUF braucht den allgemeinen Rasterisierer - er hat kein Raster,
            ' sondern wird gerechnet. Alles andere ist ein fertiges Alpha-Raster, auch eine
            ' Bereichs- oder Tiefenmaske: deren Herkunft steht in RangeKind, nicht in Kind. Die
            ' fruehere Pruefung auf jedes nichtleere Kind war der Grund, warum eine 5.784x8.660
            ' grosse Tiefenmaske trotz identischem Bildraum 91 Sekunden rueckprojiziert wurde.
            If mask.IsGradient OrElse mask.HasBrushCorrection Then
                TraceMaskProfile(Function() $"Direktpfad aus: gradient={mask.IsGradient} pinselkorrektur={mask.HasBrushCorrection}")
                Return Nothing
            End If
            If mask.ExtraComponents IsNot Nothing AndAlso mask.ExtraComponents.Count <> 0 Then
                TraceMaskProfile(Function() $"Direktpfad aus: zusatzbestandteile={mask.ExtraComponents.Count}")
                Return Nothing
            End If
            If Not mask.HasPixelData OrElse mask.Right <= mask.Left OrElse mask.Bottom <= mask.Top Then
                TraceMaskProfile(Function() "Direktpfad aus: kein gueltiges Rasterrechteck")
                Return Nothing
            End If
            If mask.SourceWidthPixels <> adj.SourceWidthPixels OrElse mask.SourceHeightPixels <> adj.SourceHeightPixels Then
                TraceMaskProfile(Function() $"Direktpfad aus: mask={mask.SourceWidthPixels}x{mask.SourceHeightPixels} rezept={adj.SourceWidthPixels}x{adj.SourceHeightPixels}")
                Return Nothing
            End If
            If Not HasIdentityMaskGeometry(adj) Then
                TraceMaskProfile(Function() "Direktpfad aus: Bildgeometrie nicht identisch")
                Return Nothing
            End If

            Try
                Dim raster = mask.Raster
                If raster Is Nothing Then
                    TraceMaskProfile(Function() "Direktpfad aus: das Raster liess sich nicht lesen")
                    Return Nothing
                End If
                If raster.Width <> mask.Right - mask.Left OrElse raster.Height <> mask.Bottom - mask.Top Then
                    TraceMaskProfile(Function() $"Direktpfad aus: Raster={raster.Width}x{raster.Height}, " &
                                                $"erwartet={mask.Right - mask.Left}x{mask.Bottom - mask.Top}")
                    Return Nothing
                End If
                rectPx = New SKRectI(mask.Left, mask.Top, mask.Right, mask.Bottom)
                Return raster.ToBitmap()
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Eine Zeile des Maskenprofils. Die Zeichenkette entsteht NUR bei
        ''' eingeschalteter Diagnose - diese Wege laufen je Render und je Maske, und ein
        ''' zusammengebauter Text waere dort auch dann bezahlt, wenn niemand mitschreibt.</summary>
        Private Shared Sub TraceMaskProfile(text As Func(Of String))
            If Not DiagnosticLogService.IsVerboseEnabled Then Return
            DiagnosticLogService.LogAlways("Maskenprofil", text())
        End Sub

        ''' <summary>Der Anfang einer Kennung fuer das Protokoll - auch wenn sie fehlt oder kuerzer
        ''' ist als die acht Zeichen.</summary>
        Private Shared Function ShortMaskId(id As String) As String
            Dim value = If(id, "")
            Return If(value.Length <= 8, value, value.Substring(0, 8))
        End Function

        ''' <summary>Bei identischer Geometrie sind Quell- und Anzeige-Pixel gleich. Diese enge
        ''' Pruefung ist absichtlich konservativ: im Zweifel nutzt der Aufrufer die vollstaendige
        ''' Projektion, nie eine falsch platzierte Maske.</summary>
        Private Shared Function HasIdentityMaskGeometry(adj As ImageAdjustments) As Boolean
            If adj Is Nothing Then Return False
            ' Ein Hochskalier-Modell aendert die Bildmasse - beim Speichern, nicht in der Vorschau.
            ' Der Riegel bleibt trotzdem stehen: er kostet nichts, und eine Maske, die im
            ' hochskalierten Raum daneben liegt, waere teuer.
            If Not String.IsNullOrEmpty(adj.UpscaleModel) Then Return False
            ' Eine nichtleere Schrittfolge ist nicht automatisch eine Transformation. Nach
            ' "Zuruecksetzen" bleiben beispielsweise leere Crop-/Transform-Schritte im Rezept,
            ' deren Ausfuehrung pixelgenau die Identitaet ist. Sie bisher pauschal abzulehnen
            ' zwang eine 50-MP-Maske trotzdem durch die Rueckprojektion.
            Dim quarterTurns = 0
            For Each operation In GeometrySteps(adj)
                If IsIdentityMaskGeometryOperation(operation) Then Continue For

                ' Vierteldrehungen sind verlustlose Pixelumordnungen. Zwei gespeicherte Schritte
                ' 90° + 270° (so liegt dieses Bild in der fpxmp vor) heben sich exakt auf, auch bei
                ' nicht quadratischen Bildern. Die allgemeine Projektion behandelte sie bisher als
                ' zwei Transformationen und lief dadurch ueber jeden einzelnen Bildpunkt.
                Dim a = operation?.Adjustments
                If String.Equals(If(operation?.Kind, "").Trim(), "transform", StringComparison.OrdinalIgnoreCase) AndAlso
                   a IsNot Nothing AndAlso a.StraightenDegrees = 0 AndAlso
                   Not a.FlipHorizontal AndAlso Not a.FlipVertical Then
                    Dim quarter = CInt(Math.Round(a.RotationDegrees / 90.0))
                    If Math.Abs(a.RotationDegrees - quarter * 90.0) < 0.0001 Then
                        quarterTurns = (quarterTurns + quarter) Mod 4
                        Continue For
                    End If
                End If
                Return False
            Next
            Return quarterTurns = 0
        End Function

        ''' <summary>Prueft exakt die Felder, welche die jeweilige Pipeline-Stufe liest. Unbekannte
        ''' Schrittarten sind ebenfalls neutral: die Ausfuehrungspipeline ignoriert sie.</summary>
        Private Shared Function IsIdentityMaskGeometryOperation(operation As GeometryOperation) As Boolean
            If operation?.Adjustments Is Nothing Then Return True
            Dim a = operation.Adjustments
            Select Case If(operation.Kind, "").Trim().ToLowerInvariant()
                Case "crop"
                    Return a.CropLeftPercent = 0 AndAlso a.CropTopPercent = 0 AndAlso
                           a.CropRightPercent = 0 AndAlso a.CropBottomPercent = 0
                Case "transform"
                    Return a.RotationDegrees = 0 AndAlso a.StraightenDegrees = 0 AndAlso
                           Not a.FlipHorizontal AndAlso Not a.FlipVertical
                Case "perspective"
                    Return a.PerspectiveHorizontal = 0 AndAlso a.PerspectiveVertical = 0 AndAlso
                           a.PerspectiveAspect = 0 AndAlso a.PerspectiveScale = 0 AndAlso
                           a.PerspectiveCorner0X = 0 AndAlso a.PerspectiveCorner0Y = 0 AndAlso
                           a.PerspectiveCorner1X = 0 AndAlso a.PerspectiveCorner1Y = 0 AndAlso
                           a.PerspectiveCorner2X = 0 AndAlso a.PerspectiveCorner2Y = 0 AndAlso
                           a.PerspectiveCorner3X = 0 AndAlso a.PerspectiveCorner3Y = 0
                Case "warp"
                    Return a.ImageWarp Is Nothing OrElse a.ImageWarp.IsEmpty
                Case "resize"
                    Return a.ResizeWidth <= 0 AndAlso a.ResizeHeight <= 0 AndAlso a.ResizeScalePercent <= 0
                Case "canvas"
                    Return a.CanvasWidth <= 0 AndAlso a.CanvasHeight <= 0
                Case Else
                    Return True
            End Select
        End Function

        ''' <summary>Baut eine ImageMask (Quellraum) aus einem vollbildgroßen Alpha-Puffer: schneidet auf
        ''' die Bounding-Box der gesetzten Pixel zu und kodiert Alpha8-PNG. Nothing, wenn leer.</summary>
        Private Shared Function EncodeSourceMaskFromAlpha(full As Byte(), sourceW As Integer, sourceH As Integer, name As String) As ImageMask
            Dim left = sourceW, top = sourceH, right = 0, bottom = 0
            For sy = 0 To sourceH - 1
                Dim row = sy * sourceW
                For sx = 0 To sourceW - 1
                    If full(row + sx) > 0 Then
                        If sx < left Then left = sx
                        If sy < top Then top = sy
                        If sx + 1 > right Then right = sx + 1
                        If sy + 1 > bottom Then bottom = sy + 1
                    End If
                Next
            Next
            If right <= left OrElse bottom <= top Then Return Nothing
            Dim fullRaster = New AlphaRaster(sourceW, sourceH, full)
            Return New ImageMask With {
                .Name = If(String.IsNullOrWhiteSpace(name), LocalizationService.T("Auswahlmaske"), name),
                .SourceWidthPixels = sourceW, .SourceHeightPixels = sourceH,
                .Left = left, .Top = top, .Right = right, .Bottom = bottom,
                .Raster = fullRaster.Crop(New SKRectI(left, top, right, bottom)),
                .FeatherPixels = 0
            }
        End Function

        ''' <summary>Rastert eine LINEARE Verlaufsmaske (crs "Mask/Gradient") in den Quellraum.
        ''' Zero/Full sind Bruchkoordinaten des Bildes (0..1, dürfen außerhalb liegen). Die Maske ist 0 an
        ''' der Zero-Linie und rampt entlang der Senkrechten linear auf maskValue an der Full-Linie (danach
        ''' konstant).</summary>
        Public Shared Function BuildLinearGradientMask(sourceW As Integer, sourceH As Integer,
                                                       zeroX As Double, zeroY As Double, fullX As Double, fullY As Double,
                                                       maskValue As Double, name As String) As ImageMask
            If sourceW <= 0 OrElse sourceH <= 0 Then Return Nothing
            Dim dirX = fullX - zeroX, dirY = fullY - zeroY
            Dim len2 = dirX * dirX + dirY * dirY
            If len2 <= 0.0000001 Then Return Nothing
            Dim mv = Math.Max(0.0, Math.Min(1.0, maskValue))
            Dim full = New Byte(sourceW * sourceH - 1) {}
            For sy = 0 To sourceH - 1
                Dim fy = (sy + 0.5) / sourceH
                Dim row = sy * sourceW
                For sx = 0 To sourceW - 1
                    Dim fx = (sx + 0.5) / sourceW
                    Dim t = ((fx - zeroX) * dirX + (fy - zeroY) * dirY) / len2
                    If t <= 0.0 Then Continue For
                    If t > 1.0 Then t = 1.0
                    full(row + sx) = CByte(Math.Round(t * mv * 255.0))
                Next
            Next
            Return EncodeSourceMaskFromAlpha(full, sourceW, sourceH, name)
        End Function

        ''' <summary>Rastert eine RADIALE Verlaufsmaske (crs "Mask/CircularGradient") in den
        ''' Quellraum. Top/Left/Bottom/Right in Bruchkoordinaten; angleDeg Grad; feather 0..100 (Anteil des
        ''' Radius mit weichem Auslauf); Flipped=True → Wirkung INNEN, sonst AUSSEN (der uebliche Standard).
        ''' Roundness/Midpoint werden in v1 vereinfacht (reine Ellipse, Halbwert = 1-feather).</summary>
        Public Shared Function BuildRadialGradientMask(sourceW As Integer, sourceH As Integer,
                                                       top As Double, left As Double, bottom As Double, right As Double,
                                                       angleDeg As Double, feather As Double, flipped As Boolean,
                                                       maskValue As Double, name As String) As ImageMask
            If sourceW <= 0 OrElse sourceH <= 0 Then Return Nothing
            Dim cx = (left + right) / 2.0, cy = (top + bottom) / 2.0
            Dim rx = Math.Max(0.0001, (right - left) / 2.0)
            Dim ry = Math.Max(0.0001, (bottom - top) / 2.0)
            Dim ang = -angleDeg * Math.PI / 180.0
            Dim cosA = Math.Cos(ang), sinA = Math.Sin(ang)
            Dim mv = Math.Max(0.0, Math.Min(1.0, maskValue))
            Dim inner = 1.0 - Math.Max(0.0, Math.Min(1.0, feather / 100.0))
            Dim full = New Byte(sourceW * sourceH - 1) {}
            For sy = 0 To sourceH - 1
                Dim fy = (sy + 0.5) / sourceH
                Dim row = sy * sourceW
                For sx = 0 To sourceW - 1
                    Dim fx = (sx + 0.5) / sourceW
                    Dim ddx = fx - cx, ddy = fy - cy
                    Dim ex = (ddx * cosA - ddy * sinA) / rx
                    Dim ey = (ddx * sinA + ddy * cosA) / ry
                    Dim d = Math.Sqrt(ex * ex + ey * ey)
                    Dim cover As Double
                    If d <= inner Then
                        cover = 1.0
                    ElseIf d >= 1.0 Then
                        cover = 0.0
                    Else
                        Dim tt = (1.0 - d) / Math.Max(0.0001, 1.0 - inner)
                        cover = tt * tt * (3.0 - 2.0 * tt)
                    End If
                    Dim m = If(flipped, cover, 1.0 - cover)
                    If m <= 0.0 Then Continue For
                    full(row + sx) = CByte(Math.Round(Math.Min(1.0, m) * mv * 255.0))
                Next
            Next
            Return EncodeSourceMaskFromAlpha(full, sourceW, sourceH, name)
        End Function

        ''' <summary>Weiche Kante: zeichnet die Alpha8-Maske mit Weichzeichner in eine neue Maske gleicher
        ''' Größe. Skias Sigma entspricht etwa dem halben Radius. Nothing, wenn nichts zu tun ist.</summary>
        Private Shared Function BlurAlphaMask(mask As SKBitmap, radiusPixels As Single) As SKBitmap
            If mask Is Nothing OrElse radiusPixels <= 0.05F Then Return Nothing
            Dim sigma = Math.Max(0.1F, radiusPixels * 0.5F)
            Dim result = New SKBitmap(mask.Width, mask.Height, SKColorType.Alpha8, SKAlphaType.Premul)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                Using paint = New SKPaint()
                    paint.ImageFilter = SKImageFilter.CreateBlur(sigma, sigma)
                    paint.Color = SKColors.White
                    canvas.DrawBitmap(mask, 0, 0, paint)
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Liefert eine weichgezeichnete Kopie der Maske - um den Radius nach AUSSEN erweitert,
        ''' damit die Kante symmetrisch ausläuft und nicht am Maskenrand abgeschnitten wird (ein Lasso berührt
        ''' seinen Rahmen per Definition). <paramref name="expandedRect"/> ist das dazugehörige, ebenfalls
        ''' erweiterte Bildrechteck. Nothing, wenn keine weiche Kante gewünscht ist.</summary>
        Public Shared Function BuildFeatheredMask(mask As SKBitmap, rect As SKRectI, radiusPixels As Single,
                                                  ByRef expandedRect As SKRectI) As SKBitmap
            expandedRect = rect
            If mask Is Nothing OrElse radiusPixels <= 0.05F Then Return Nothing

            Dim pad = Math.Max(1, CInt(Math.Ceiling(radiusPixels * 2.0F)))
            Dim padded = New SKBitmap(mask.Width + 2 * pad, mask.Height + 2 * pad, SKColorType.Alpha8, SKAlphaType.Premul)
            Using canvas = New SKCanvas(padded)
                canvas.Clear(SKColors.Transparent)
                canvas.DrawBitmap(mask, pad, pad)
            End Using

            Dim blurred = BlurAlphaMask(padded, radiusPixels)
            padded.Dispose()
            If blurred Is Nothing Then Return Nothing

            expandedRect = New SKRectI(rect.Left - pad, rect.Top - pad, rect.Right + pad, rect.Bottom + pad)
            Return blurred
        End Function

        ''' <summary>Die Skopus-Maske in Zielgröße, mit weicher Kante falls eingestellt. Hier darf die Kante
        ''' frei nach außen auslaufen: die Maske hat bereits die volle Bildgröße, es wird nichts abgeschnitten.
        ''' Der Radius wird auf die Zielgröße mitskaliert - sonst wäre die Kante in der (kleineren) Vorschau
        ''' breiter als im gespeicherten Bild.</summary>
        Private Shared Function BuildSelectionScopeMask(adj As ImageAdjustments, targetW As Integer, targetH As Integer) As SKBitmap
            Dim mask = BuildSelectionScopeMaskCore(adj, targetW, targetH)
            ' Masken-Pinsel trägt seine weiche Kante bereits in den Alpha-Werten (siehe
            ' SelectionMaskSoftBaked) - dann NICHT erneut weichzeichnen, sonst doppelt weiche Kante.
            If mask Is Nothing OrElse adj.SelectionMaskSoftBaked OrElse adj.SelectionFeatherPixels <= 0.05F Then Return mask

            Dim referenceSize = ComputeGeometryOutputSize(adj.SourceWidthPixels, adj.SourceHeightPixels, adj)
            Dim scaleX = If(referenceSize.Width > 0, targetW / CSng(referenceSize.Width), 1.0F)
            Dim scaleY = If(referenceSize.Height > 0, targetH / CSng(referenceSize.Height), 1.0F)
            Dim scale = (scaleX + scaleY) / 2.0F
            Dim blurred = BlurAlphaMask(mask, adj.SelectionFeatherPixels * scale)
            If blurred Is Nothing Then Return mask
            mask.Dispose()
            Return blurred
        End Function

        ''' <summary>Baut aus der aktiven Auswahl eine Alpha8-Maske in der Größe des verarbeiteten Bildes
        ''' (<paramref name="targetW"/>×<paramref name="targetH"/>). Unregelmäßige Auswahlen kommen aus der
        ''' gespeicherten Alpha8-Maske (Display-Pixel, per Nearest-Sampling auf die Zielgröße gebracht),
        ''' Rechtecke direkt aus den Prozentwerten. Liefert Nothing, wenn keine nutzbare Auswahl vorliegt.</summary>
        Private Shared Function BuildSelectionScopeMaskCore(adj As ImageAdjustments, targetW As Integer, targetH As Integer) As SKBitmap
            If targetW <= 0 OrElse targetH <= 0 Then Return Nothing
            ' Maskenpixel und Masken-Rechteck werden vom Editor im sichtbaren Display-Raum gespeichert.
            ' Bei 90/270 Grad ist dessen Breite die Source-Hoehe; eine Skalierung ueber SourceWidth
            ' verschob bzw. leerte Lasso-/Zauberstabmasken in der Vorschau.
            Dim referenceSize = ComputeGeometryOutputSize(adj.SourceWidthPixels, adj.SourceHeightPixels, adj)
            Dim scaleX = If(referenceSize.Width > 0, targetW / CDbl(referenceSize.Width), 1.0)
            Dim scaleY = If(referenceSize.Height > 0, targetH / CDbl(referenceSize.Height), 1.0)

            If adj.HasSelectionMaskData Then
                Dim boundsW = adj.SelectionMaskRight - adj.SelectionMaskLeft
                Dim boundsH = adj.SelectionMaskBottom - adj.SelectionMaskTop
                If boundsW <= 0 OrElse boundsH <= 0 Then Return Nothing
                Try
                    Dim selectionRaster = adj.SelectionMaskRaster
                    If selectionRaster Is Nothing Then Return Nothing
                    Dim dStride = selectionRaster.Width
                    Dim dBuf = selectionRaster.Pixels
                    Dim dW = selectionRaster.Width, dH = selectionRaster.Height

                    Dim mask = New SKBitmap(targetW, targetH, SKColorType.Alpha8, SKAlphaType.Premul)
                    Dim mStride = mask.RowBytes
                    Dim mBuf = New Byte(mStride * targetH - 1) {}
                    For ty = 0 To targetH - 1
                        Dim baseY = If(scaleY > 0, CInt(ty / scaleY), ty)
                        Dim ly = baseY - adj.SelectionMaskTop
                        If ly < 0 OrElse ly >= dH Then Continue For
                        Dim mRow = ty * mStride, dRow = ly * dStride
                        For tx = 0 To targetW - 1
                            Dim baseX = If(scaleX > 0, CInt(tx / scaleX), tx)
                            Dim lx = baseX - adj.SelectionMaskLeft
                            If lx < 0 OrElse lx >= dW Then Continue For
                            mBuf(mRow + tx) = dBuf(dRow + lx)
                        Next
                    Next
                    Marshal.Copy(mBuf, 0, mask.GetPixels(), mBuf.Length)
                    Return mask
                Catch
                    Return Nothing
                End Try
            End If

            ' Rechteckdaten können entweder für den expliziten Legacy-Skopus ODER zum Einfrieren der
            ' momentan aktiven UI-Auswahl angefordert werden.
            If SelectionScopeIsEnabled(adj) OrElse adj.HasActiveSelection Then
                Dim left = Clamp3(CInt(Math.Round(targetW * adj.SelectionXPercent / 100.0)), 0, targetW)
                Dim top = Clamp3(CInt(Math.Round(targetH * adj.SelectionYPercent / 100.0)), 0, targetH)
                Dim right = Clamp3(CInt(Math.Round(targetW * (adj.SelectionXPercent + adj.SelectionWidthPercent) / 100.0)), 0, targetW)
                Dim bottom = Clamp3(CInt(Math.Round(targetH * (adj.SelectionYPercent + adj.SelectionHeightPercent) / 100.0)), 0, targetH)
                If right <= left OrElse bottom <= top Then Return Nothing
                Dim mask = New SKBitmap(targetW, targetH, SKColorType.Alpha8, SKAlphaType.Premul)
                Dim mStride = mask.RowBytes
                Dim mBuf = New Byte(mStride * targetH - 1) {}
                For y = top To bottom - 1
                    Dim mRow = y * mStride
                    For x = left To right - 1
                        mBuf(mRow + x) = 255
                    Next
                Next
                Marshal.Copy(mBuf, 0, mask.GetPixels(), mBuf.Length)
                Return mask
            End If

            Return Nothing
        End Function

        ''' <summary>Nur der explizite Rezeptzustand begrenzt globale Anpassungen. HasActiveSelection
        ''' steuert ausschließlich die Editor-Überlagerung und darf das Rendering nie verändern.</summary>
        Private Shared Function SelectionScopeIsEnabled(adj As ImageAdjustments) As Boolean
            Return adj IsNot Nothing AndAlso adj.SelectionScopeEnabled
        End Function

        Private Shared Function Clamp3(value As Integer, lo As Integer, hi As Integer) As Integer
            Return Math.Max(lo, Math.Min(hi, value))
        End Function

        ''' <summary>Mischt <paramref name="adjusted"/> (angepasst) über <paramref name="baseline"/> (unverändert)
        ''' anhand der Alpha8-<paramref name="mask"/>: out = baseline·(255−m) + adjusted·m. Alle drei müssen
        ''' dieselben Maße haben. Fällt bei ungeeignetem Farbformat auf eine Kopie der BASELINE zurück.</summary>
        Private Shared Function CompositeSelectionScoped(baseline As SKBitmap, adjusted As SKBitmap, mask As SKBitmap) As SKBitmap
            Dim w = adjusted.Width, h = adjusted.Height
            Dim aStride As Integer, bStride As Integer
            Dim aBuf As Byte() = Nothing, bBuf As Byte() = Nothing
            If Not TryBorrowBgraBuffer(adjusted, aBuf, aStride) OrElse Not TryBorrowBgraBuffer(baseline, bBuf, bStride) Then
                ' Fallback-Richtung BASELINE, nicht adjusted: eine Kopie von
                ' adjusted machte die auswahl-skopierte Anpassung still GLOBAL - exakt der Bruch
                ' der Garantie aus ProcessBitmapBase ("darf niemals still auf eine globale
                ' Anpassung zurueckfallen"; der Masken-Decode-Fehlerfall dort nimmt ebenfalls die
                ' Baseline). Loggen statt schweigen - stiller Farbtyp-Rueckfall ist eine bekannte
                ' Fallenklasse dieses Projekts.
                DiagnosticLogService.LogAlways("ImageProcessor.SelectionScope",
                    $"composite skipped: non-BGRA pipeline bitmap adjusted={adjusted.ColorType} baseline={baseline.ColorType} - returning baseline")
                Return CloneBitmap(baseline)
            End If
            Dim mStride = mask.RowBytes
            Dim mBuf = New Byte(mStride * mask.Height - 1) {}
            Marshal.Copy(mask.GetPixels(), mBuf, 0, mBuf.Length)

            Dim outBuf = New Byte(aBuf.Length - 1) {}
            Dim maskW = mask.Width, maskH = mask.Height
            ForEachRow(w, h, Sub(y)
                                 Dim aRow = y * aStride, bRow = y * bStride, mRow = y * mStride
                                 For x = 0 To w - 1
                                     Dim ao = aRow + x * 4, bo = bRow + x * 4
                                     Dim m = If(x < maskW AndAlso y < maskH, CInt(mBuf(mRow + x)), 0)
                                     If m = 0 Then
                                         outBuf(ao) = bBuf(bo) : outBuf(ao + 1) = bBuf(bo + 1) : outBuf(ao + 2) = bBuf(bo + 2) : outBuf(ao + 3) = bBuf(bo + 3)
                                     ElseIf m >= 255 Then
                                         outBuf(ao) = aBuf(ao) : outBuf(ao + 1) = aBuf(ao + 1) : outBuf(ao + 2) = aBuf(ao + 2) : outBuf(ao + 3) = aBuf(ao + 3)
                                     Else
                                         Dim inv = 255 - m
                                         outBuf(ao) = CByte((CInt(aBuf(ao)) * m + CInt(bBuf(bo)) * inv) \ 255)
                                         outBuf(ao + 1) = CByte((CInt(aBuf(ao + 1)) * m + CInt(bBuf(bo + 1)) * inv) \ 255)
                                         outBuf(ao + 2) = CByte((CInt(aBuf(ao + 2)) * m + CInt(bBuf(bo + 2)) * inv) \ 255)
                                         outBuf(ao + 3) = CByte((CInt(aBuf(ao + 3)) * m + CInt(bBuf(bo + 3)) * inv) \ 255)
                                     End If
                                 Next
                             End Sub)

            Dim result = New SKBitmap(w, h, adjusted.ColorType, adjusted.AlphaType)
            CommitBgraBuffer(result, outBuf)
            Return result
        End Function

        ''' <summary>Kopiert eine Region eines Bitmaps in ein eigenes Bitmap gleicher Art. Der
        ''' VORHER-Ausschnitt für <see cref="RestoreOutsideCoverage"/>: dort wird er wieder
        ''' eingeblendet, wo eine Auswahl den Strich nicht durchlässt.</summary>
        Friend Shared Function CopyRegion(source As SKBitmap, rect As SKRectI) As SKBitmap
            If source Is Nothing OrElse rect.Width <= 0 OrElse rect.Height <= 0 Then Return Nothing
            Dim copy = New SKBitmap(rect.Width, rect.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(copy)
                ' Src statt SrcOver: der Ausschnitt wird exakt uebernommen, Alpha eingeschlossen.
                Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                    canvas.DrawBitmap(source,
                                      New SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                                      New SKRect(0, 0, rect.Width, rect.Height), paint)
                End Using
            End Using
            Return copy
        End Function

        ''' <summary>Blendet den VORHER-Stand dort zurück, wo die Deckung nicht reicht:
        ''' out = vorher·(255−Deckung) + jetzt·Deckung, über die Region <paramref name="rect"/> von
        ''' <paramref name="target"/>. So bleibt ein gemalter oder radierter Strich auf eine Auswahl
        ''' begrenzt, OHNE dass die Zeichenroutine davon wissen muss - sie malt wie immer, und was
        ''' außerhalb liegt, wird danach zurückgenommen. Weiche Auswahlkanten laufen dadurch von
        ''' selbst mit.
        '''
        ''' <paramref name="before"/> und <paramref name="coverage"/> haben die Maße von
        ''' <paramref name="rect"/>. False, wenn das Farbformat nicht passt - der Aufrufer muss den
        ''' Zug dann verwerfen, denn der Strich stünde sonst ungebremst über der ganzen Fläche.</summary>
        Friend Shared Function RestoreOutsideCoverage(target As SKBitmap, before As SKBitmap,
                                                      coverage As SKBitmap, rect As SKRectI) As Boolean
            If target Is Nothing OrElse before Is Nothing OrElse coverage Is Nothing Then Return False
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return False
            If before.Width <> rect.Width OrElse before.Height <> rect.Height Then Return False
            If coverage.Width <> rect.Width OrElse coverage.Height <> rect.Height Then Return False
            If coverage.ColorType <> SKColorType.Alpha8 Then Return False

            Dim bBuf As Byte() = Nothing, bStride As Integer
            If target.ColorType <> SKColorType.Bgra8888 OrElse Not TryBorrowBgraBuffer(before, bBuf, bStride) Then
                DiagnosticLogService.LogAlways("ImageProcessor.StrokeCoverage",
                    $"skipped: non-BGRA bitmap target={target.ColorType} before={before.ColorType}")
                Return False
            End If

            Dim cStride = coverage.RowBytes
            Dim cBuf = New Byte(cStride * coverage.Height - 1) {}
            Marshal.Copy(coverage.GetPixels(), cBuf, 0, cBuf.Length)

            ' NUR die Region anfassen: das Ziel ist das Arbeitsbild in voller Auflösung, ein
            ' Puffer darüber wären bei 45 Megapixeln 180 MB je Strich. Gelesen und geschrieben
            ' wird deshalb zeilenweise direkt im Bitmapspeicher.
            Dim targetPtr = target.GetPixels()
            Dim tStride = target.RowBytes
            Dim rowBytes = rect.Width * 4
            Dim left = rect.Left, top = rect.Top
            ForEachRow(rect.Width, rect.Height,
                       Sub(y)
                           Dim rowPtr = IntPtr.Add(targetPtr, (top + y) * tStride + left * 4)
                           Dim tBuf = New Byte(rowBytes - 1) {}
                           Marshal.Copy(rowPtr, tBuf, 0, rowBytes)
                           Dim bRow = y * bStride
                           Dim cRow = y * cStride
                           Dim changed = False
                           For x = 0 To rect.Width - 1
                               Dim m = CInt(cBuf(cRow + x))
                               If m >= 255 Then Continue For
                               changed = True
                               Dim toff = x * 4, boff = bRow + x * 4
                               If m = 0 Then
                                   tBuf(toff) = bBuf(boff) : tBuf(toff + 1) = bBuf(boff + 1)
                                   tBuf(toff + 2) = bBuf(boff + 2) : tBuf(toff + 3) = bBuf(boff + 3)
                               Else
                                   Dim inv = 255 - m
                                   tBuf(toff) = CByte((CInt(tBuf(toff)) * m + CInt(bBuf(boff)) * inv) \ 255)
                                   tBuf(toff + 1) = CByte((CInt(tBuf(toff + 1)) * m + CInt(bBuf(boff + 1)) * inv) \ 255)
                                   tBuf(toff + 2) = CByte((CInt(tBuf(toff + 2)) * m + CInt(bBuf(boff + 2)) * inv) \ 255)
                                   tBuf(toff + 3) = CByte((CInt(tBuf(toff + 3)) * m + CInt(bBuf(boff + 3)) * inv) \ 255)
                               End If
                           Next
                           If changed Then Marshal.Copy(tBuf, 0, rowPtr, rowBytes)
                       End Sub)
            Return True
        End Function

        ''' Für das Auswahlwerkzeug "Kopieren": rendert dieselbe voll bearbeitete Pipeline wie
        ''' SaveImage (Original decodiert + alle Anpassungen/Objekte gebacken), schneidet daraus
        ''' aber nur pixelRect aus und speichert das Ergebnis als eigenständige PNG-Datei -
        ''' Grundlage für ein neues, frei verschiebbares Bild-Objekt (AddImageAnnotationAt).
        ''' <paramref name="workingFull"/>: Arbeitsbild statt Datei-Decode (siehe SaveImage; Besitz wechselt hierher).
        Public Shared Function ExtractRegionToFile(sourcePath As String, adj As ImageAdjustments, pixelRect As SKRectI, targetPngPath As String,
                                                   Optional workingFull As SKBitmap = Nothing) As Boolean
            Try
                Using original = If(workingFull, DecodeOriented(sourcePath))
                    If original Is Nothing Then Return False
                    Using processed = ProcessBitmap(original, adj)
                        Dim left = Math.Max(0, pixelRect.Left)
                        Dim top = Math.Max(0, pixelRect.Top)
                        Dim right = Math.Min(processed.Width, pixelRect.Right)
                        Dim bottom = Math.Min(processed.Height, pixelRect.Bottom)
                        Dim width = right - left
                        Dim height = bottom - top
                        If width <= 0 OrElse height <= 0 Then Return False

                        Using cropped = New SKBitmap(width, height, processed.ColorType, processed.AlphaType)
                            Using canvas = New SKCanvas(cropped)
                                canvas.DrawBitmap(processed, New SKRect(left, top, right, bottom), New SKRect(0, 0, width, height))
                            End Using
                            Using image = SKImage.FromBitmap(cropped)
                                Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                                    WriteFileAtomic(targetPngPath, Sub(fs) data.SaveTo(fs))
                                End Using
                            End Using
                        End Using
                    End Using
                End Using
                Return True
            Catch ex As Exception
                Return False
            End Try
        End Function

        ' ── Freie Selektion (Kern) ────────────────────────────────────────────────
        ' Grundbausteine für Lasso-/Zauberstab-Auswahl. Eine Maske ist ein Alpha8-Bitmap in der Größe
        ' des umschließenden Rechtecks: 255 = innerhalb der Auswahl, 0 = außerhalb. Alle Funktionen sind
        ' rein (Bitmap rein, Bitmap raus) und damit ohne laufenden Editor prüfbar.

        ''' <summary>Zauberstab: wählt ab dem Klickpunkt die zusammenhängende Fläche ähnlicher Farbe (4er-
        ''' Nachbarschaft, Toleranz 0..1 als maximaler Kanalabstand). Liefert eine Alpha8-Maske in der Größe
        ''' des umschließenden Rechtecks, oder Nothing. <paramref name="bounds"/> erhält dieses Rechteck in
        ''' Bildpixeln.
        '''
        ''' <paramref name="confineRect"/> und <paramref name="confine"/> BEGRENZEN den Lauf auf eine
        ''' markierte Ebene: außerhalb des Rechtecks, und innerhalb dort, wo die Alpha8-Deckung nichts
        ''' hergibt, gilt jedes Pixel als abgelehnt. Gelesen wird weiterhin die fertige SZENE - das ist
        ''' die Farbe, die der Nutzer anklickt; ohne die Grenze lief die Füllung über die Ebenenkante
        ''' hinaus ins Foto weiter. Ein leeres Rechteck heißt „nicht begrenzen"; ein Rechteck ohne
        ''' Deckung begrenzt allein auf das Rechteck.</summary>
        Public Shared Function BuildMagicWandMask(image As SKBitmap, seedX As Integer, seedY As Integer,
                                                  tolerance As Single, ByRef bounds As SKRectI,
                                                  Optional confineRect As SKRectI = Nothing,
                                                  Optional confine As SKBitmap = Nothing) As SKBitmap
            bounds = SKRectI.Empty
            If image Is Nothing Then Return Nothing
            Dim w = image.Width, h = image.Height
            If seedX < 0 OrElse seedY < 0 OrElse seedX >= w OrElse seedY >= h Then Return Nothing

            ' Die Deckung EINMAL in ein verwaltetes Feld holen: die Schleife unten fragt sie je Pixel,
            ' und SkiaSharps GetPixel geht dabei jedes Mal durch P/Invoke.
            Dim confineActive = confineRect.Width > 0 AndAlso confineRect.Height > 0
            Dim confineBuf As Byte() = Nothing
            Dim confineStride = 0
            If confineActive AndAlso confine IsNot Nothing Then
                If confine.ColorType <> SKColorType.Alpha8 Then Return Nothing
                confineStride = confine.RowBytes
                Dim confineLength = confineStride * confine.Height
                If confineLength <= 0 OrElse confine.GetPixels() = IntPtr.Zero Then Return Nothing
                confineBuf = New Byte(confineLength - 1) {}
                Marshal.Copy(confine.GetPixels(), confineBuf, 0, confineLength)
            End If
            Dim confineWidth = If(confine Is Nothing, 0, confine.Width)
            Dim confineHeight = If(confine Is Nothing, 0, confine.Height)
            ' Der Klick selbst muss auf der Ebene liegen. Von einem Punkt daneben aus liefe die
            ' Füllung sofort gegen die Grenze und ergäbe eine Auswahl aus einem einzigen Pixel -
            ' „nichts gefunden" ist die ehrlichere Antwort.
            If confineActive Then
                If Not PointIsInsideConfine(seedX, seedY, confineRect, confineBuf, confineStride,
                                            confineWidth, confineHeight) Then Return Nothing
            End If

            ' ProcessBitmap bleibt ohne Objekt-Overlays meist Bgra8888, ApplyAnnotations liefert
            ' dagegen bewusst Rgba8888. Der Zauberstab muss beide Szenenformate lesen können; die
            ' frühere Bindung an TryBorrowBgraBuffer ließ ihn bei Bildern mit Objekten still mit
            ' Nothing aussteigen.
            Dim rIdx, gIdx, bIdx As Integer
            Select Case image.ColorType
                Case SKColorType.Bgra8888
                    bIdx = 0 : gIdx = 1 : rIdx = 2
                Case SKColorType.Rgba8888
                    rIdx = 0 : gIdx = 1 : bIdx = 2
                Case Else
                    Return Nothing
            End Select
            Dim stride = image.RowBytes
            Dim length = stride * h
            If length <= 0 OrElse image.GetPixels() = IntPtr.Zero Then Return Nothing
            Dim buf(length - 1) As Byte
            Marshal.Copy(image.GetPixels(), buf, 0, length)

            Dim tol = CInt(Math.Round(Clamp(tolerance, 0, 1) * 255))
            Dim seedO = seedY * stride + seedX * 4
            Dim sr = CInt(buf(seedO + rIdx)), sg = CInt(buf(seedO + gIdx)), sb = CInt(buf(seedO + bIdx))

            ' 0 = unbekannt, 1 = bereits eingereiht, 2 = verworfen, 3 = ausgewählt. Die frühere
            ' Boolean-Maske markierte nur Treffer; dasselbe noch ungeprüfte oder abgelehnte Pixel
            ' konnte deshalb von mehreren Nachbarn immer wieder auf den Stack gelangen.
            Dim state = New Byte(w * h - 1) {}
            Dim stack As New Stack(Of Integer)()
            Dim seedIndex = seedY * w + seedX
            stack.Push(seedIndex)
            state(seedIndex) = 1
            Dim minX = w, minY = h, maxX = -1, maxY = -1

            While stack.Count > 0
                Dim idx = stack.Pop()
                Dim x = idx Mod w, y = idx \ w
                If confineActive AndAlso Not PointIsInsideConfine(x, y, confineRect, confineBuf, confineStride,
                                                                  confineWidth, confineHeight) Then
                    state(idx) = 2
                    Continue While
                End If
                Dim o = y * stride + x * 4
                If Math.Abs(CInt(buf(o + rIdx)) - sr) > tol OrElse
                   Math.Abs(CInt(buf(o + gIdx)) - sg) > tol OrElse
                   Math.Abs(CInt(buf(o + bIdx)) - sb) > tol Then
                    state(idx) = 2
                    Continue While
                End If
                state(idx) = 3
                If x < minX Then minX = x
                If x > maxX Then maxX = x
                If y < minY Then minY = y
                If y > maxY Then maxY = y
                If x > 0 AndAlso state(idx - 1) = 0 Then
                    state(idx - 1) = 1
                    stack.Push(idx - 1)
                End If
                If x < w - 1 AndAlso state(idx + 1) = 0 Then
                    state(idx + 1) = 1
                    stack.Push(idx + 1)
                End If
                If y > 0 AndAlso state(idx - w) = 0 Then
                    state(idx - w) = 1
                    stack.Push(idx - w)
                End If
                If y < h - 1 AndAlso state(idx + w) = 0 Then
                    state(idx + w) = 1
                    stack.Push(idx + w)
                End If
            End While

            If maxX < minX Then Return Nothing
            Dim bw = maxX - minX + 1, bh = maxY - minY + 1
            bounds = New SKRectI(minX, minY, maxX + 1, maxY + 1)

            Dim mask = New SKBitmap(bw, bh, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim mstride = mask.RowBytes
            Dim mbuf = New Byte(mstride * bh - 1) {}
            For y = 0 To bh - 1
                For x = 0 To bw - 1
                    If state((minY + y) * w + (minX + x)) = 3 Then mbuf(y * mstride + x) = 255
                Next
            Next
            Marshal.Copy(mbuf, 0, mask.GetPixels(), mbuf.Length)
            Return mask
        End Function

        ''' <summary>Erstellt eine weiche, NICHT zusammenhängende Farbbereichsmaske. Anders als der
        ''' Zauberstab trifft sie jede passende Farbe im Bild - genau das braucht man etwa für einen
        ''' blauen Himmel, der durch einen Baum unterbrochen ist.</summary>
        ''' <param name="contiguous">Nur die zusammenhaengende Flaeche am Klickpunkt. Gerechnet wird
        ''' mit DERSELBEN Aehnlichkeit wie sonst, begrenzt wird erst danach - der Regler bedeutet in
        ''' beiden Faellen dasselbe, und der Uebergang wirkt auch hier.</param>
        Public Shared Function BuildColorRangeMask(image As SKBitmap, seedX As Integer, seedY As Integer,
                                                   tolerancePct As Double, featherPct As Double,
                                                   ByRef bounds As SKRectI,
                                                   Optional contiguous As Boolean = False) As SKBitmap
            bounds = SKRectI.Empty
            If image Is Nothing OrElse seedX < 0 OrElse seedY < 0 OrElse seedX >= image.Width OrElse seedY >= image.Height Then Return Nothing
            Dim rIdx, gIdx, bIdx As Integer
            If image.ColorType = SKColorType.Bgra8888 Then
                bIdx = 0 : gIdx = 1 : rIdx = 2
            ElseIf image.ColorType = SKColorType.Rgba8888 Then
                rIdx = 0 : gIdx = 1 : bIdx = 2
            Else
                Return Nothing
            End If
            Dim stride = image.RowBytes, raw(stride * image.Height - 1) As Byte
            Marshal.Copy(image.GetPixels(), raw, 0, raw.Length)
            Dim seed = seedY * stride + seedX * 4
            Dim sr = CDbl(raw(seed + rIdx)), sg = CDbl(raw(seed + gIdx)), sb = CDbl(raw(seed + bIdx))
            ' RGB-Abstand ist für eine Auswahl bewusst auf 0..100 normiert. Die weiche Zone sitzt
            ' ausserhalb der harten Toleranz, damit 0 wirklich nur die angeklickte Farbe trifft.
            '
            ' DER REGLER GEHT QUADRATISCH IN DEN ABSTAND, nicht linear. An vier Fotos gemessen lag
            ' linear der ganze nutzbare Bereich zwischen 5 und 25: dort sprang die gefasste Flaeche
            ' von wenigen Prozent auf ueber 80, und die oberen zwei Drittel des Reglers taten nichts
            ' mehr (Nutzerbefund 2026-08-16). Quadratisch liegt derselbe Uebergang etwa in der Mitte
            ' des Wegs und ist ueber rund 30 Reglerpunkte gestreckt. Der Wert selbst bleibt, was er
            ' war - die Kennlinie sitzt hier an EINER Stelle und nicht in den Aufrufern.
            Dim norm = Math.Max(0.0, Math.Min(100.0, tolerancePct)) / 100.0
            Dim hard = norm * norm * 441.6729559
            Dim soft = Math.Max(1.0, Math.Min(100.0, featherPct)) / 100.0 * 220.8364779
            Dim output = New SKBitmap(image.Width, image.Height, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim outStride = output.RowBytes, data(outStride * image.Height - 1) As Byte
            Dim minX = image.Width, minY = image.Height, maxX = -1, maxY = -1
            For y = 0 To image.Height - 1
                Dim row = y * stride, outRow = y * outStride
                For x = 0 To image.Width - 1
                    Dim p = row + x * 4
                    Dim dr = CDbl(raw(p + rIdx)) - sr, dg = CDbl(raw(p + gIdx)) - sg, db = CDbl(raw(p + bIdx)) - sb
                    Dim distance = Math.Sqrt(dr * dr + dg * dg + db * db)
                    Dim a As Double
                    If distance <= hard Then
                        a = 255
                    ElseIf distance >= hard + soft Then
                        a = 0
                    Else
                        Dim t = (distance - hard) / soft
                        a = (1.0 - t * t * (3.0 - 2.0 * t)) * 255.0
                    End If
                    If a >= 1 Then
                        data(outRow + x) = CByte(Math.Round(a))
                        If x < minX Then minX = x
                        If x > maxX Then maxX = x
                        If y < minY Then minY = y
                        If y > maxY Then maxY = y
                    End If
                Next
            Next
            If maxX < minX Then output.Dispose() : Return Nothing

            If contiguous Then
                ' Flutfuellung ueber alles, was ueberhaupt Deckung hat - vom Klickpunkt aus, vier
                ' Nachbarn. Was von dort nicht erreichbar ist, faellt weg; die weiche Zone am Rand
                ' bleibt erhalten, weil sie mitgeflutet wird.
                Dim seedIndex = seedY * outStride + seedX
                If data(seedIndex) = 0 Then output.Dispose() : Return Nothing
                Dim reached(data.Length - 1) As Byte
                Dim stack As New Stack(Of Integer)()
                stack.Push(seedIndex)
                reached(seedIndex) = 1
                Dim cMinX = image.Width, cMinY = image.Height, cMaxX = -1, cMaxY = -1
                While stack.Count > 0
                    Dim idx = stack.Pop()
                    Dim py = idx \ outStride
                    Dim px = idx - py * outStride
                    If px < cMinX Then cMinX = px
                    If px > cMaxX Then cMaxX = px
                    If py < cMinY Then cMinY = py
                    If py > cMaxY Then cMaxY = py
                    If px > 0 AndAlso reached(idx - 1) = 0 AndAlso data(idx - 1) > 0 Then
                        reached(idx - 1) = 1 : stack.Push(idx - 1)
                    End If
                    If px < image.Width - 1 AndAlso reached(idx + 1) = 0 AndAlso data(idx + 1) > 0 Then
                        reached(idx + 1) = 1 : stack.Push(idx + 1)
                    End If
                    If py > 0 AndAlso reached(idx - outStride) = 0 AndAlso data(idx - outStride) > 0 Then
                        reached(idx - outStride) = 1 : stack.Push(idx - outStride)
                    End If
                    If py < image.Height - 1 AndAlso reached(idx + outStride) = 0 AndAlso data(idx + outStride) > 0 Then
                        reached(idx + outStride) = 1 : stack.Push(idx + outStride)
                    End If
                End While
                For i = 0 To data.Length - 1
                    If reached(i) = 0 Then data(i) = 0
                Next
                If cMaxX < cMinX Then output.Dispose() : Return Nothing
                minX = cMinX : minY = cMinY : maxX = cMaxX : maxY = cMaxY
            End If

            Marshal.Copy(data, 0, output.GetPixels(), data.Length)
            bounds = New SKRectI(minX, minY, maxX + 1, maxY + 1)
            Return output
        End Function

        ''' <summary>Erstellt eine Luminanzbereichsmaske mit zwei weichen Grenzen. 0 ist schwarz,
        ''' 100 weiß; die Maske berücksichtigt alle Bildbereiche, nicht nur eine zusammenhängende Fläche.</summary>
        Public Shared Function BuildLuminanceRangeMask(image As SKBitmap, fromPct As Double, toPct As Double,
                                                       featherPct As Double, ByRef bounds As SKRectI) As SKBitmap
            bounds = SKRectI.Empty
            If image Is Nothing Then Return Nothing
            Dim rIdx, gIdx, bIdx As Integer
            If image.ColorType = SKColorType.Bgra8888 Then
                bIdx = 0 : gIdx = 1 : rIdx = 2
            ElseIf image.ColorType = SKColorType.Rgba8888 Then
                rIdx = 0 : gIdx = 1 : bIdx = 2
            Else
                Return Nothing
            End If
            Dim low = Math.Max(0.0, Math.Min(100.0, Math.Min(fromPct, toPct))) / 100.0
            Dim high = Math.Max(0.0, Math.Min(100.0, Math.Max(fromPct, toPct))) / 100.0
            Dim feather = Math.Max(0.0, Math.Min(50.0, featherPct)) / 100.0
            Dim stride = image.RowBytes, raw(stride * image.Height - 1) As Byte
            Marshal.Copy(image.GetPixels(), raw, 0, raw.Length)
            Dim output = New SKBitmap(image.Width, image.Height, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim outStride = output.RowBytes, data(outStride * image.Height - 1) As Byte
            Dim minX = image.Width, minY = image.Height, maxX = -1, maxY = -1
            For y = 0 To image.Height - 1
                Dim row = y * stride, outRow = y * outStride
                For x = 0 To image.Width - 1
                    Dim p = row + x * 4
                    Dim l = (0.2126 * raw(p + rIdx) + 0.7152 * raw(p + gIdx) + 0.0722 * raw(p + bIdx)) / 255.0
                    Dim a As Double
                    If l < low Then
                        a = If(feather = 0, 0, Math.Max(0, 1 - (low - l) / feather))
                    ElseIf l > high Then
                        a = If(feather = 0, 0, Math.Max(0, 1 - (l - high) / feather))
                    Else
                        a = 1
                    End If
                    If a > 0 Then
                        data(outRow + x) = CByte(Math.Round(a * 255))
                        If x < minX Then minX = x
                        If x > maxX Then maxX = x
                        If y < minY Then minY = y
                        If y > maxY Then maxY = y
                    End If
                Next
            Next
            If maxX < minX Then output.Dispose() : Return Nothing
            Marshal.Copy(data, 0, output.GetPixels(), data.Length)
            bounds = New SKRectI(minX, minY, maxX + 1, maxY + 1)
            Return output
        End Function

        ''' <summary>Liegt dieser Bildpunkt auf der Ebene, auf die begrenzt wird? Ohne Deckung
        ''' entscheidet allein das Rechteck; mit Deckung zusätzlich, ob dort überhaupt etwas von der
        ''' Ebene liegt. Ein halb durchsichtiger Rand zählt dazu - abgeschnitten wird erst bei
        ''' vollständiger Durchsichtigkeit, sonst franste die Auswahl an weichen Kanten aus.</summary>
        Private Shared Function PointIsInsideConfine(x As Integer, y As Integer, confineRect As SKRectI,
                                                     confineBuf As Byte(), confineStride As Integer,
                                                     confineWidth As Integer, confineHeight As Integer) As Boolean
            If x < confineRect.Left OrElse y < confineRect.Top OrElse
               x >= confineRect.Right OrElse y >= confineRect.Bottom Then Return False
            If confineBuf Is Nothing Then Return True
            Dim lx = x - confineRect.Left, ly = y - confineRect.Top
            If lx < 0 OrElse ly < 0 OrElse lx >= confineWidth OrElse ly >= confineHeight Then Return False
            Return confineBuf(ly * confineStride + lx) > 0
        End Function

        Private Shared Function ReadMaskBytes(mask As SKBitmap, ByRef stride As Integer) As Byte()
            stride = mask.RowBytes
            Dim mbuf = New Byte(stride * mask.Height - 1) {}
            Marshal.Copy(mask.GetPixels(), mbuf, 0, mbuf.Length)
            Return mbuf
        End Function

        ''' <summary>Schneidet <paramref name="source"/> mit der Maske aus: RGB bleibt, Alpha wird mit dem
        ''' Masken-Alpha multipliziert (außerhalb der Maske also transparent). Gleiche Größe vorausgesetzt.
        ''' Ergebnis ist Unpremul - passend für die PNG-Ausgabe.</summary>
        Public Shared Function ApplyMaskCutout(source As SKBitmap, mask As SKBitmap) As SKBitmap
            Return ApplyMaskCutoutCore(source, mask, inverted:=False)
        End Function

        ''' <summary>Umkehrung von <see cref="ApplyMaskCutout"/> für "Löschen/Freistellen": innerhalb der
        ''' Maske wird das Bild transparent, außerhalb bleibt es erhalten.</summary>
        Public Shared Function ApplyMaskErase(source As SKBitmap, mask As SKBitmap) As SKBitmap
            Return ApplyMaskCutoutCore(source, mask, inverted:=True)
        End Function

        ''' <summary>Der gemeinsame Kern beider Maskenschnitte. <paramref name="inverted"/> dreht nur um,
        ''' welche Seite der Maske durchlässt.
        '''
        ''' Das RGB bleibt unangetastet, nur das Alpha wird mit dem Masken-Alpha multipliziert. Das Ziel
        ''' ist UNPREMUL (so will es die PNG-Ausgabe) - dorthin schreibt SetPixel die Farbe unverändert,
        ''' es wird hier also NICHT premultipliziert. Die Quelle ist dagegen meist premultipliziert und
        ''' muss beim Lesen zurückgerechnet werden, genau wie GetPixel es täte.</summary>
        Private Shared Function ApplyMaskCutoutCore(source As SKBitmap, mask As SKBitmap,
                                                    inverted As Boolean) As SKBitmap
            Dim w = source.Width, h = source.Height
            Dim result = New SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul)
            Dim mstride As Integer
            Dim mbuf = ReadMaskBytes(mask, mstride)
            Dim maskW = mask.Width, maskH = mask.Height

            Dim srcBuf As Byte() = Nothing
            Dim sStride, ri, gi, bi, ai As Integer
            If TryBorrowRgbaLikeBuffer(source, srcBuf, sStride, ri, gi, bi, ai) Then
                Dim premultiplied = (source.AlphaType = SKAlphaType.Premul)
                Dim dStride = result.RowBytes
                Dim dstBuf = New Byte(dStride * h - 1) {}
                ForEachRow(w, h,
                    Sub(y)
                        Dim so = y * sStride
                        Dim dof = y * dStride
                        Dim mo = y * mstride
                        For x = 0 To w - 1
                            Dim o = so + x * 4
                            Dim a = srcBuf(o + ai)
                            Dim cr = srcBuf(o + ri), cg = srcBuf(o + gi), cb = srcBuf(o + bi)
                            If premultiplied Then
                                cr = UnpremultiplyByte(cr, a)
                                cg = UnpremultiplyByte(cg, a)
                                cb = UnpremultiplyByte(cb, a)
                            End If
                            Dim m = If(x < maskW AndAlso y < maskH, CInt(mbuf(mo + x)), 0)
                            If inverted Then m = 255 - m
                            Dim outAlpha = CByte(CInt(a) * m \ 255)
                            Dim d = dof + x * 4
                            ' Ziel ist Bgra8888, deshalb hier fest B, G, R, A.
                            If outAlpha = 0 Then
                                ' SetPixel NULLT bei Alpha 0 auch die Farbkanaele - gemessen an allen
                                ' 65536 Kombinationen. Ohne diesen Zweig bliebe hinter vollstaendig
                                ' ausmaskierten Stellen die alte Farbe stehen; sichtbar wird das erst,
                                ' wenn jemand das PNG spaeter weiterverarbeitet.
                                dstBuf(d) = 0
                                dstBuf(d + 1) = 0
                                dstBuf(d + 2) = 0
                                dstBuf(d + 3) = 0
                            Else
                                dstBuf(d) = cb
                                dstBuf(d + 1) = cg
                                dstBuf(d + 2) = cr
                                dstBuf(d + 3) = outAlpha
                            End If
                        Next
                    End Sub)
                Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
                Return result
            End If

            ' Rückfall für Farbtypen, die der Puffer nicht abdeckt.
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim c = source.GetPixel(x, y)
                    Dim m = If(x < maskW AndAlso y < maskH, CInt(mbuf(y * mstride + x)), 0)
                    If inverted Then m = 255 - m
                    result.SetPixel(x, y, New SKColor(c.Red, c.Green, c.Blue, CByte(CInt(c.Alpha) * m \ 255)))
                Next
            Next
            Return result
        End Function

        ''' <summary>Füllt die Maske mit einer einzelnen Farbe (Alpha = Farb-Alpha × Masken-Alpha). Grundlage
        ''' für "Auswahl mit Farbe füllen".</summary>
        Public Shared Function RenderMaskedFill(mask As SKBitmap, colorHex As String) As SKBitmap
            Return RenderMaskedFill(mask, colorHex, "Solid", "", 0, False)
        End Function

        ''' <summary>Füllt die Maske mit Vollfarbe oder Verlauf (Alpha = Füll-Alpha × Masken-Alpha).
        ''' Grundlage für "Auswahl füllen", inklusive weicher Rechteckkante.</summary>
        Public Shared Function RenderMaskedFill(mask As SKBitmap, colorHex As String, fillKind As String,
                                                color2Hex As String, gradientAngleDegrees As Single,
                                                gradientInverted As Boolean) As SKBitmap
            Dim col = ParseColor(colorHex, SKColors.White)
            Dim w = mask.Width, h = mask.Height
            Dim fill = New SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul)
            Using canvas = New SKCanvas(fill)
                canvas.Clear(SKColors.Transparent)
                Dim rect = New SKRect(0, 0, w, h)
                Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
                If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                    Dim col2 = ParseColor(color2Hex, col)
                    Using shader = CreateFillGradientShader(rect, normalizedFillKind, col, col2, gradientAngleDegrees, gradientInverted)
                        Using paint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                            canvas.DrawRect(rect, paint)
                        End Using
                    End Using
                Else
                    Using paint = New SKPaint With {.Color = col, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                        canvas.DrawRect(rect, paint)
                    End Using
                End If
            End Using

            Try
                Return ApplyMaskCutout(fill, mask)
            Finally
                fill.Dispose()
            End Try
        End Function

        ''' <summary>Rendert die LUMINANZ einer Füllung (Vollfarbe/Verlauf/radial) als dichten w×h-Byte-Puffer
        ''' (0..255): Schwarz → 0, Weiß → 255, Verlauf → Rampe. Kern für die deklarative Masken-Abstufung -
        ''' die Luminanz stuft, WIE STARK die Anpassung einer Masken-Ebene je Bereich wirkt.</summary>
        Friend Shared Function ComputeFillLuminance(w As Integer, h As Integer, fillKind As String, colorHex As String,
                                                    color2Hex As String, gradientAngleDegrees As Single,
                                                    gradientInverted As Boolean) As Byte()
            If w <= 0 OrElse h <= 0 Then Return Nothing
            Dim col = ParseColor(colorHex, SKColors.White)
            Using fill = New SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Using canvas = New SKCanvas(fill)
                    canvas.Clear(SKColors.Transparent)
                    Dim rect = New SKRect(0, 0, w, h)
                    Dim nk = If(fillKind, "Solid").Trim().ToLowerInvariant()
                    If nk = "lineargradient" OrElse nk = "radialgradient" Then
                        Dim col2 = ParseColor(color2Hex, col)
                        Using shader = CreateFillGradientShader(rect, nk, col, col2, gradientAngleDegrees, gradientInverted)
                            Using paint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                                canvas.DrawRect(rect, paint)
                            End Using
                        End Using
                    Else
                        Using paint = New SKPaint With {.Color = col, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                            canvas.DrawRect(rect, paint)
                        End Using
                    End If
                End Using
                Dim fillStride = fill.RowBytes
                Dim fillBuf = New Byte(fillStride * h - 1) {}
                Marshal.Copy(fill.GetPixels(), fillBuf, 0, fillBuf.Length)
                Dim lum = New Byte(w * h - 1) {}
                For y = 0 To h - 1
                    Dim fRow = y * fillStride, lRow = y * w
                    For x = 0 To w - 1
                        Dim o = fRow + x * 4
                        ' Bgra8888 (Unpremul): 0=B, 1=G, 2=R, 3=A. Deckung = Luminanz (Rec.601) × EIGEN-ALPHA.
                        ' Der Alpha-Anteil MUSS mitzählen: die Standard-Füllfarbe ist "#00FFFFFF" (transparentes
                        ' WEISS). Ohne Alpha ergäbe sie Luminanz 255 = überall volle Deckung, ein Verlauf von
                        ' "transparent" nach Weiß wäre also flach und stufte gar nichts ab
                        ' ("auf der Maske wirkt die Füllung nicht als Deckungsverlauf").
                        Dim l = fillBuf(o + 2) * 0.299 + fillBuf(o + 1) * 0.587 + fillBuf(o) * 0.114
                        lum(lRow + x) = CByte(Math.Round(l * fillBuf(o + 3) / 255.0))
                    Next
                Next
                Return lum
            End Using
        End Function

        ''' <summary>Multipliziert die LUMINANZ einer Füllung in eine vorhandene Alpha8-Maske. Weiterhin genutzt
        ''' für die headless-Diagnose; der interaktive Weg speichert die Füllung deklarativ auf der Ebene.</summary>
        Public Shared Function RenderMaskFilledWithGradient(mask As SKBitmap, colorHex As String, fillKind As String,
                                                            color2Hex As String, gradientAngleDegrees As Single,
                                                            gradientInverted As Boolean) As SKBitmap
            If mask Is Nothing OrElse mask.Width <= 0 OrElse mask.Height <= 0 Then Return Nothing
            Dim w = mask.Width, h = mask.Height
            Dim lum = ComputeFillLuminance(w, h, fillKind, colorHex, color2Hex, gradientAngleDegrees, gradientInverted)
            If lum Is Nothing Then Return Nothing
            Dim mStride = mask.RowBytes
            Dim mBuf = New Byte(mStride * h - 1) {}
            Marshal.Copy(mask.GetPixels(), mBuf, 0, mBuf.Length)
            Dim result = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim rStride = result.RowBytes
            Dim rBuf = New Byte(rStride * h - 1) {}
            For y = 0 To h - 1
                Dim mRow = y * mStride, rRow = y * rStride, lRow = y * w
                For x = 0 To w - 1
                    rBuf(rRow + x) = CByte(CInt(mBuf(mRow + x)) * CInt(lum(lRow + x)) \ 255)
                Next
            Next
            Marshal.Copy(rBuf, 0, result.GetPixels(), rBuf.Length)
            Return result
        End Function

        Public Shared Function RenderMaskedFillToFile(mask As SKBitmap, colorHex As String, fillKind As String,
                                                      color2Hex As String, gradientAngleDegrees As Single,
                                                      gradientInverted As Boolean, targetPngPath As String) As Boolean
            Try
                If mask Is Nothing Then Return False
                Using filled = RenderMaskedFill(mask, colorHex, fillKind, color2Hex, gradientAngleDegrees, gradientInverted)
                    Return SaveBitmapPng(filled, targetPngPath)
                End Using
            Catch ex As Exception
                Return False
            End Try
        End Function

        Public Shared Function RenderMaskedFillToFile(mask As SKBitmap, colorHex As String, targetPngPath As String) As Boolean
            Try
                If mask Is Nothing Then Return False
                Using filled = RenderMaskedFill(mask, colorHex)
                    Return SaveBitmapPng(filled, targetPngPath)
                End Using
            Catch ex As Exception
                Return False
            End Try
        End Function

        Private Shared Function SaveBitmapPng(bmp As SKBitmap, targetPngPath As String) As Boolean
            Using image = SKImage.FromBitmap(bmp)
                Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                    WriteFileAtomic(targetPngPath, Sub(fs) data.SaveTo(fs))
                End Using
            End Using
            Return True
        End Function

        ''' <summary>Wie <see cref="ExtractRegionToFile"/>, aber schneidet den Ausschnitt zusätzlich mit einer
        ''' Maske frei (unregelmäßige Auswahl). Die Maske muss die Größe des (geklemmten) Rechtecks haben.</summary>
        ''' <paramref name="workingFull"/>: Arbeitsbild statt Datei-Decode (siehe SaveImage; Besitz wechselt hierher).
        Public Shared Function ExtractRegionToFileMasked(sourcePath As String, adj As ImageAdjustments,
                                                         pixelRect As SKRectI, mask As SKBitmap, targetPngPath As String,
                                                         Optional workingFull As SKBitmap = Nothing) As Boolean
            Try
                If mask Is Nothing Then
                    workingFull?.Dispose()
                    Return False
                End If
                Using original = If(workingFull, DecodeOriented(sourcePath))
                    If original Is Nothing Then Return False
                    Using processed = ProcessBitmap(original, adj)
                        Dim left = Math.Max(0, pixelRect.Left)
                        Dim top = Math.Max(0, pixelRect.Top)
                        Dim right = Math.Min(processed.Width, pixelRect.Right)
                        Dim bottom = Math.Min(processed.Height, pixelRect.Bottom)
                        Dim width = right - left, height = bottom - top
                        If width <= 0 OrElse height <= 0 Then Return False

                        Using cropped = New SKBitmap(width, height, processed.ColorType, processed.AlphaType)
                            Using canvas = New SKCanvas(cropped)
                                canvas.DrawBitmap(processed, New SKRect(left, top, right, bottom), New SKRect(0, 0, width, height))
                            End Using
                            Using cutout = ApplyMaskCutout(cropped, mask)
                                Return SaveBitmapPng(cutout, targetPngPath)
                            End Using
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                Return False
            End Try
        End Function

        ''' <summary>Speichert eine mit <paramref name="colorHex"/> gefüllte Maske als PNG - Grundlage für ein
        ''' Füll-Objekt in exakter Auswahlgröße.</summary>
        ' Baut aus dem Alpha-Kanal eines gezeichneten RGBA-Bitmaps die Alpha8-Maske (weiche Kanten bleiben
        ' als Teil-Alpha erhalten - dadurch werden Ellipse/Lasso-Auswahlen antialiased ausgeschnitten).
        Private Shared Function AlphaMaskFrom(rgba As SKBitmap) As SKBitmap
            Dim w = rgba.Width, h = rgba.Height
            Dim mask = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim mstride = mask.RowBytes
            Dim mbuf = New Byte(mstride * h - 1) {}
            Dim src As Byte() = Nothing, stride As Integer = 0
            If TryBorrowBgraBuffer(rgba, src, stride) Then
                For y = 0 To h - 1
                    For x = 0 To w - 1
                        mbuf(y * mstride + x) = src(y * stride + x * 4 + 3)
                    Next
                Next
            End If
            Marshal.Copy(mbuf, 0, mask.GetPixels(), mbuf.Length)
            Return mask
        End Function

        ''' <summary>Alpha8-Maske einer in das Rechteck (0,0,width,height) eingepassten Ellipse - für das
        ''' Kreis-/Ellipse-Auswahlwerkzeug.</summary>
        Public Shared Function BuildEllipseMask(width As Integer, height As Integer) As SKBitmap
            Return BuildEllipseMask(width, height, New SKRect(0, 0, width, height))
        End Function

        Public Shared Function BuildEllipseMask(width As Integer, height As Integer, ovalRect As SKRect) As SKBitmap
            If width <= 0 OrElse height <= 0 Then Return Nothing
            Using rgba = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Using canvas = New SKCanvas(rgba)
                    canvas.Clear(SKColors.Transparent)
                    Using paint = New SKPaint With {.Color = SKColors.White, .IsAntialias = True, .Style = SKPaintStyle.Fill}
                        canvas.DrawOval(ovalRect, paint)
                    End Using
                End Using
                Return AlphaMaskFrom(rgba)
            End Using
        End Function

        ''' <summary>Alpha8-Maske eines gefüllten Polygons (Lasso). Punkte in lokalen Koordinaten des
        ''' Rechtecks (0..width / 0..height); der Pfad wird automatisch geschlossen.</summary>
        Public Shared Function BuildPolygonMask(pointsX As Single(), pointsY As Single(), width As Integer, height As Integer) As SKBitmap
            If width <= 0 OrElse height <= 0 OrElse pointsX Is Nothing OrElse pointsY Is Nothing Then Return Nothing
            If pointsX.Length < 3 OrElse pointsX.Length <> pointsY.Length Then Return Nothing
            Using rgba = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Using canvas = New SKCanvas(rgba)
                    canvas.Clear(SKColors.Transparent)
                    Using path = New SKPath()
                        path.MoveTo(pointsX(0), pointsY(0))
                        For i = 1 To pointsX.Length - 1
                            path.LineTo(pointsX(i), pointsY(i))
                        Next
                        path.Close()
                        Using paint = New SKPaint With {.Color = SKColors.White, .IsAntialias = True, .Style = SKPaintStyle.Fill}
                            canvas.DrawPath(path, paint)
                        End Using
                    End Using
                End Using
                Return AlphaMaskFrom(rgba)
            End Using
        End Function

        ''' <summary>Zeichnet einen weichen runden Pinselstrich (Masken-Pinsel). <paramref name="pts"/>
        ''' sind die Strich-Stützpunkte im Zielraum des Canvas; <paramref name="radius"/> der Pinselradius,
        ''' <paramref name="softnessPx"/> die Randweichheit in Pixeln (Gauß-Sigma = softness/2), gemeinsam
        ''' genutzt für den weißen Masken-Stempel UND die rote Live-Vorschau (Farbe frei). Ein einzelner
        ''' Punkt (Klick) wird als gefüllte Scheibe gezeichnet. <paramref name="erase"/> = True stanzt statt
        ''' zu malen (DstOut) - für Subtrahieren in der Live-Vorschau. MaskFilter wirkt hier (DrawPath/
        ''' DrawCircle, NICHT DrawBitmap - dort wäre er wirkungslos).</summary>
        Public Shared Sub DrawSoftMaskStroke(canvas As SKCanvas, pts As IReadOnlyList(Of SKPoint),
                                             radius As Single, softnessPx As Single, color As SKColor,
                                             Optional eraseMode As Boolean = False)
            If canvas Is Nothing OrElse pts Is Nothing OrElse pts.Count = 0 OrElse radius <= 0 Then Return
            Dim blend = If(eraseMode, SKBlendMode.DstOut, SKBlendMode.SrcOver)
            Dim sigma = If(softnessPx > 0.05F, softnessPx * 0.5F, 0.0F)
            If pts.Count = 1 Then
                Using paint = New SKPaint With {.Color = color, .Style = SKPaintStyle.Fill, .IsAntialias = True, .BlendMode = blend}
                    If sigma > 0.0F Then paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma)
                    canvas.DrawCircle(pts(0).X, pts(0).Y, radius, paint)
                End Using
                Return
            End If
            Using paint = New SKPaint With {.Color = color, .Style = SKPaintStyle.Stroke, .StrokeCap = SKStrokeCap.Round,
                                            .StrokeJoin = SKStrokeJoin.Round, .StrokeWidth = radius * 2.0F, .IsAntialias = True, .BlendMode = blend}
                If sigma > 0.0F Then paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma)
                Using path = New SKPath()
                    path.MoveTo(pts(0).X, pts(0).Y)
                    For i = 1 To pts.Count - 1
                        path.LineTo(pts(i).X, pts(i).Y)
                    Next
                    canvas.DrawPath(path, paint)
                End Using
            End Using
        End Sub

        ''' <summary>Eine Punktabbildung im QUELLRAUM, in Bildpunkten. Die drei Transformationen
        ''' unten geben sie an die GERECHNETEN Bestandteile weiter - Verläufe haben keine Bildpunkte,
        ''' die man verschieben könnte, sondern zwei Punkte und ein Verhältnis.</summary>
        Private Delegate Function MaskPointMap(x As Double, y As Double) As (X As Double, Y As Double)

        ''' <summary>Welcher Teil einer Maske mitgenommen wird. Der Grund für die Trennung ist der
        ''' PREIS: ein gerechneter Verlauf sind vier Zahlen und wandert praktisch umsonst mit, ein
        ''' gemaltes Raster muss neu gerastert werden. Deshalb folgen die Verläufe schon WÄHREND
        ''' eines Zuges, die Raster erst beim Loslassen - und dann darf der Verlauf nicht ein
        ''' zweites Mal wandern.</summary>
        Public Enum MaskTransformPart
            All = 0
            GradientsOnly = 1
            RasterOnly = 2
        End Enum

        ''' <summary>Trägt die Maske einen gerechneten Verlauf? Nur dann lohnt die Live-Nachführung
        ''' während eines Zuges.</summary>
        Friend Shared Function HasGradientComponent(mask As ImageMask) As Boolean
            If mask Is Nothing Then Return False
            If IsGradientMaskKind(mask.Kind) Then Return True
            If mask.ExtraComponents Is Nothing Then Return False
            For Each c In mask.ExtraComponents
                If c IsNot Nothing AndAlso IsGradientMaskKind(c.Kind) Then Return True
            Next
            Return False
        End Function

        ''' <summary>Nimmt die GERECHNETEN Anteile einer Maske mit: die Verlaufspunkte jedes
        ''' Bestandteils. Sie stehen in Prozent des Quellraums, die Abbildung rechnet in Bildpunkten -
        ''' also hin und zurück.
        '''
        ''' Ohne das bleibt eine Verlaufsmaske liegen, während ihr Objekt wandert. Sichtbar wurde es
        ''' an einer Ebenenmaske, deren gemalter Bestandteil gelöscht war: dann gibt es gar kein
        ''' Raster mehr, die drei Funktionen stiegen ganz oben aus, und die Maske klebte am Bild
        ''' statt an der Ebene (Nutzerbefund 2026-08-05).</summary>
        Private Shared Sub MapGradientComponents(mask As ImageMask, map As MaskPointMap)
            If mask Is Nothing OrElse map Is Nothing Then Return
            Dim sw = CDbl(Math.Max(1, mask.SourceWidthPixels))
            Dim sh = CDbl(Math.Max(1, mask.SourceHeightPixels))

            Dim mapPercent = Sub(ByRef xPercent As Double, ByRef yPercent As Double)
                                 Dim mapped = map(xPercent / 100.0 * sw, yPercent / 100.0 * sh)
                                 xPercent = mapped.X / sw * 100.0
                                 yPercent = mapped.Y / sh * 100.0
                             End Sub

            If IsGradientMaskKind(mask.Kind) Then
                Dim sx = mask.GradientStartXPercent, sy = mask.GradientStartYPercent
                Dim ex = mask.GradientEndXPercent, ey = mask.GradientEndYPercent
                mapPercent(sx, sy)
                mapPercent(ex, ey)
                mask.GradientStartXPercent = sx : mask.GradientStartYPercent = sy
                mask.GradientEndXPercent = ex : mask.GradientEndYPercent = ey
            End If

            If mask.ExtraComponents Is Nothing Then Return
            For Each c In mask.ExtraComponents
                If c Is Nothing OrElse Not IsGradientMaskKind(c.Kind) Then Continue For
                Dim csx = c.GradientStartXPercent, csy = c.GradientStartYPercent
                Dim cex = c.GradientEndXPercent, cey = c.GradientEndYPercent
                mapPercent(csx, csy)
                mapPercent(cex, cey)
                c.GradientStartXPercent = csx : c.GradientStartYPercent = csy
                c.GradientEndXPercent = cex : c.GradientEndYPercent = cey
            Next
        End Sub

        ''' <summary>Schiebt die Pinselkorrektur eines Verlaufs mit. Sie liegt als eigenes Raster mit
        ''' eigenem Rechteck neben dem gerechneten Verlauf; bei einer reinen VERSCHIEBUNG genügt es,
        ''' das Rechteck zu versetzen - die Bildpunkte bleiben dieselben. Skalieren, Drehen und
        ''' Spiegeln rastern neu, im Raster-Teil der drei Region-Funktionen
        ''' (<see cref="TransformBrushCorrections"/>).</summary>
        Private Shared Sub ShiftBrushCorrections(mask As ImageMask, offsetX As Integer, offsetY As Integer)
            If mask Is Nothing OrElse (offsetX = 0 AndAlso offsetY = 0) Then Return
            Dim shift = Sub(c As MaskComponent)
                            If c Is Nothing Then Return
                            If String.IsNullOrEmpty(c.BrushAddPngBase64) AndAlso String.IsNullOrEmpty(c.BrushSubtractPngBase64) Then Return
                            c.BrushLeft += offsetX : c.BrushRight += offsetX
                            c.BrushTop += offsetY : c.BrushBottom += offsetY
                        End Sub
            If Not String.IsNullOrEmpty(mask.BrushAddPngBase64) OrElse Not String.IsNullOrEmpty(mask.BrushSubtractPngBase64) Then
                mask.BrushLeft += offsetX : mask.BrushRight += offsetX
                mask.BrushTop += offsetY : mask.BrushBottom += offsetY
            End If
            If mask.ExtraComponents Is Nothing Then Return
            For Each c In mask.ExtraComponents
                shift(c)
            Next
        End Sub

        ''' <summary>Trägt dieser Bestandteil ein GEMALTES Raster? Ein Verlauf wird gerechnet und
        ''' hat keines; ein leeres oder entartetes Rechteck zählt nicht.</summary>
        Private Shared Function HasPaintedRaster(c As MaskComponent) As Boolean
            Return c IsNot Nothing AndAlso Not c.IsGradient AndAlso
                   c.HasPixelData AndAlso
                   c.Right > c.Left AndAlso c.Bottom > c.Top
        End Function

        ''' <summary>Führt eine Arbeit auf JEDEM gemalten Raster einer Maske aus - dem in den Feldern
        ''' der Maske selbst und dem jedes Bestandteils in ExtraComponents. Dasselbe Muster wie
        ''' <see cref="ForEachBrushCarrier"/>, und aus demselben Grund: die drei Region-Funktionen
        ''' fassten nur das PRIMÄRE Raster an. Ein gemalter Bestandteil, der einem Verlauf
        ''' hinzugefügt wurde, blieb beim Verschieben, Skalieren, Drehen und Spiegeln des Objekts am
        ''' Bild kleben, während Verlauf und Pinselkorrektur mitgingen.</summary>
        Private Shared Function ForEachRasterCarrier(mask As ImageMask, op As Func(Of MaskComponent, Boolean)) As Boolean
            If mask Is Nothing OrElse op Is Nothing Then Return True
            Dim primary = mask.PrimaryAsComponent()
            If HasPaintedRaster(primary) Then
                If Not op(primary) Then Return False
                mask.SetPrimaryFromComponent(primary)
            End If
            If mask.ExtraComponents IsNot Nothing Then
                For Each c In mask.ExtraComponents
                    If c IsNot Nothing AndAlso HasPaintedRaster(c) AndAlso Not op(c) Then Return False
                Next
            End If
            Return True
        End Function

        ''' <summary>Rastert EIN gemaltes Raster für eine Drehung um einen Punkt neu. Das neue Rechteck
        ''' ist die Hülle der vier gedrehten Ecken; gezeichnet wird im Quellraum.</summary>
        Private Shared Function RotateComponentRaster(c As MaskComponent, degrees As Double,
                                                      pivotX As Double, pivotY As Double) As Boolean
            Dim rad = degrees * Math.PI / 180.0
            Dim cosR = Math.Cos(rad), sinR = Math.Sin(rad)
            Dim corners = {(CDbl(c.Left), CDbl(c.Top)), (CDbl(c.Right), CDbl(c.Top)),
                           (CDbl(c.Right), CDbl(c.Bottom)), (CDbl(c.Left), CDbl(c.Bottom))}
            Dim minX = Double.MaxValue, minY = Double.MaxValue, maxX = Double.MinValue, maxY = Double.MinValue
            For Each e In corners
                Dim dx = e.Item1 - pivotX, dy = e.Item2 - pivotY
                Dim nx = pivotX + dx * cosR - dy * sinR
                Dim ny = pivotY + dx * sinR + dy * cosR
                minX = Math.Min(minX, nx) : maxX = Math.Max(maxX, nx)
                minY = Math.Min(minY, ny) : maxY = Math.Max(maxY, ny)
            Next

            Dim l = CInt(Math.Floor(minX)), t = CInt(Math.Floor(minY))
            Dim w = Math.Max(1, CInt(Math.Ceiling(maxX)) - l), h = Math.Max(1, CInt(Math.Ceiling(maxY)) - t)

            Dim decoded As SKBitmap = Nothing
            Try
                decoded = If(c.Raster Is Nothing, Nothing, c.Raster.ToBitmap())
                If decoded Is Nothing Then Return False
                Using rotated = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(rotated)
                        canvas.Clear(SKColors.Transparent)
                        ' Im QUELLRAUM zeichnen: erst den Ursprung des neuen Rechtecks wegschieben,
                        ' dann um den Drehpunkt drehen, dann die Maske an ihrer alten Stelle absetzen.
                        canvas.Translate(CSng(-l), CSng(-t))
                        canvas.RotateDegrees(CSng(degrees), CSng(pivotX), CSng(pivotY))
                        Using paint = New SKPaint With {.IsAntialias = True}
                            ' Ueber SKImage, weil nur DrawImage die Abtastung entgegennimmt -
                            ' DrawBitmap hat dafuer keine Ueberladung mehr.
                            Using image = SKImage.FromBitmap(decoded)
                                canvas.DrawImage(image, New SKRect(c.Left, c.Top,
                                                                  c.Left + decoded.Width, c.Top + decoded.Height),
                                                 New SKSamplingOptions(SKCubicResampler.Mitchell), paint)
                            End Using
                        End Using
                    End Using
                    ' Das Ergebnis bleibt ein Raster; gepackt wird erst beim Schreiben.
                    Dim transformed = AlphaRaster.FromBitmap(rotated)
                    If transformed Is Nothing Then Return False
                    c.Raster = transformed
                End Using
                c.Left = l : c.Top = t : c.Right = l + w : c.Bottom = t + h
                Return True
            Catch
                Return False
            Finally
                decoded?.Dispose()
            End Try
        End Function

        ''' <summary>Spiegelt EIN gemaltes Raster pixelgenau an einer Achse. Die Auflösung bleibt
        ''' erhalten, nur die Lage wechselt die Seite.</summary>
        Private Shared Function FlipComponentRaster(c As MaskComponent, horizontal As Boolean, axis As Double) As Boolean
            Dim decoded As SKBitmap = Nothing
            Try
                decoded = If(c.Raster Is Nothing, Nothing, c.Raster.ToBitmap())
                If decoded Is Nothing Then Return False
                Dim w = decoded.Width, h = decoded.Height
                Dim l = c.Left, t = c.Top
                If horizontal Then
                    l = CInt(Math.Round(2 * axis - c.Right))
                Else
                    t = CInt(Math.Round(2 * axis - c.Bottom))
                End If

                Using mirrored = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(mirrored)
                        canvas.Clear(SKColors.Transparent)
                        If horizontal Then
                            canvas.Scale(-1.0F, 1.0F, w / 2.0F, 0.0F)
                        Else
                            canvas.Scale(1.0F, -1.0F, 0.0F, h / 2.0F)
                        End If
                        canvas.DrawBitmap(decoded, 0.0F, 0.0F)
                    End Using
                    ' Das Ergebnis bleibt ein Raster; gepackt wird erst beim Schreiben.
                    Dim transformed = AlphaRaster.FromBitmap(mirrored)
                    If transformed Is Nothing Then Return False
                    c.Raster = transformed
                End Using
                c.Left = l : c.Top = t : c.Right = l + w : c.Bottom = t + h
                Return True
            Catch
                Return False
            Finally
                decoded?.Dispose()
            End Try
        End Function

        ''' <summary>Verschiebt oder skaliert EIN gemaltes Raster. Eine reine Verschiebung behält die
        ''' Bildpunkte und versetzt nur das Rechteck - kein erneutes Abtasten, keine weicher werdende
        ''' Kante nach dem dritten Zug.</summary>
        Private Shared Function TransformComponentRaster(c As MaskComponent,
                                                         scaleX As Double, scaleY As Double,
                                                         pivotX As Double, pivotY As Double,
                                                         offsetX As Double, offsetY As Double) As Boolean
            Dim newLeft = pivotX + (c.Left - pivotX) * scaleX + offsetX
            Dim newTop = pivotY + (c.Top - pivotY) * scaleY + offsetY
            Dim newRight = pivotX + (c.Right - pivotX) * scaleX + offsetX
            Dim newBottom = pivotY + (c.Bottom - pivotY) * scaleY + offsetY

            Dim l = CInt(Math.Round(newLeft)), t = CInt(Math.Round(newTop))
            Dim r = CInt(Math.Round(newRight)), b = CInt(Math.Round(newBottom))
            Dim w = Math.Max(1, r - l), h = Math.Max(1, b - t)

            Dim decoded As SKBitmap = Nothing
            Try
                decoded = If(c.Raster Is Nothing, Nothing, c.Raster.ToBitmap())
                If decoded Is Nothing Then Return False
                If w = decoded.Width AndAlso h = decoded.Height Then
                    ' Reine Verschiebung: die Pixel bleiben, nur das Rechteck wandert.
                    c.Left = l : c.Top = t : c.Right = l + decoded.Width : c.Bottom = t + decoded.Height
                    Return True
                End If
                Using scaled = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(scaled)
                        canvas.Clear(SKColors.Transparent)
                        Using paint = New SKPaint With {.IsAntialias = True}
                            Using image = SKImage.FromBitmap(decoded)
                                canvas.DrawImage(image, New SKRect(0, 0, w, h),
                                                 New SKSamplingOptions(SKCubicResampler.Mitchell), paint)
                            End Using
                        End Using
                    End Using
                    ' Das Ergebnis bleibt ein Raster; gepackt wird erst beim Schreiben.
                    Dim transformed = AlphaRaster.FromBitmap(scaled)
                    If transformed Is Nothing Then Return False
                    c.Raster = transformed
                End Using
                c.Left = l : c.Top = t : c.Right = l + w : c.Bottom = t + h
                Return True
            Catch
                Return False
            Finally
                decoded?.Dispose()
            End Try
        End Function

        ''' <summary>Führt eine Arbeit auf JEDEM Träger einer Pinselkorrektur aus: den Feldern der
        ''' Maske selbst und jedem Bestandteil in ExtraComponents. Die Maskenfelder laufen über die
        ''' Abschrift PrimaryAsComponent/SetPrimaryFromComponent, damit die Arbeit nur eine Form
        ''' kennen muss. Zurückgeschrieben wird je Träger erst nach Erfolg - ein Fehlschlag lässt
        ''' den betroffenen Träger unangetastet.</summary>
        Private Shared Function ForEachBrushCarrier(mask As ImageMask, op As Func(Of MaskComponent, Boolean)) As Boolean
            If mask Is Nothing OrElse op Is Nothing Then Return True
            Dim primary = mask.PrimaryAsComponent()
            If primary.HasBrushCorrection Then
                If Not op(primary) Then Return False
                mask.SetPrimaryFromComponent(primary)
            End If
            If mask.ExtraComponents IsNot Nothing Then
                For Each c In mask.ExtraComponents
                    If c IsNot Nothing AndAlso c.HasBrushCorrection AndAlso Not op(c) Then Return False
                Next
            End If
            Return True
        End Function

        ''' <summary>Rastert die Pinselkorrektur aller Träger einer Maske für eine Transformation
        ''' neu - das Gegenstück zu ShiftBrushCorrections für alles, was KEINE reine Verschiebung
        ''' ist. <paramref name="map"/> ist dieselbe Punktabbildung wie beim Verlauf (sie liefert
        ''' das neue Rechteck als Hülle der vier abgebildeten Ecken), <paramref name="canvasSetup"/>
        ''' dieselbe Transformation als Canvas-Matrix (sie zeichnet die alten Bildpunkte an die
        ''' neue Stelle). Beide Raster eines Trägers teilen sich ein Rechteck, es wird deshalb
        ''' EINMAL gerechnet und beide werden hineingezeichnet.</summary>
        Private Shared Function TransformBrushCorrections(mask As ImageMask, map As MaskPointMap,
                                                          canvasSetup As Action(Of SKCanvas),
                                                          sampling As SKSamplingOptions) As Boolean
            Return ForEachBrushCarrier(mask,
                Function(c)
                    Dim corners = {(CDbl(c.BrushLeft), CDbl(c.BrushTop)), (CDbl(c.BrushRight), CDbl(c.BrushTop)),
                                   (CDbl(c.BrushRight), CDbl(c.BrushBottom)), (CDbl(c.BrushLeft), CDbl(c.BrushBottom))}
                    Dim minX = Double.MaxValue, minY = Double.MaxValue
                    Dim maxX = Double.MinValue, maxY = Double.MinValue
                    For Each e In corners
                        Dim p = map(e.Item1, e.Item2)
                        minX = Math.Min(minX, p.X) : maxX = Math.Max(maxX, p.X)
                        minY = Math.Min(minY, p.Y) : maxY = Math.Max(maxY, p.Y)
                    Next
                    Dim l = CInt(Math.Floor(minX)), t = CInt(Math.Floor(minY))
                    Dim w = Math.Max(1, CInt(Math.Ceiling(maxX)) - l)
                    Dim h = Math.Max(1, CInt(Math.Ceiling(maxY)) - t)

                    Dim oldRect = New SKRect(c.BrushLeft, c.BrushTop, c.BrushRight, c.BrushBottom)
                    Dim newAdd As String = Nothing
                    Dim newSubtract As String = Nothing
                    If Not RerasterBrushAlpha(c.BrushAddPngBase64, oldRect, l, t, w, h, canvasSetup, sampling, newAdd) Then Return False
                    If Not RerasterBrushAlpha(c.BrushSubtractPngBase64, oldRect, l, t, w, h, canvasSetup, sampling, newSubtract) Then Return False
                    c.BrushAddPngBase64 = newAdd
                    c.BrushSubtractPngBase64 = newSubtract
                    c.BrushLeft = l : c.BrushTop = t : c.BrushRight = l + w : c.BrushBottom = t + h
                    Return True
                End Function)
        End Function

        ''' <summary>Ein einzelnes Alpha8-Korrekturraster an seine neue Stelle zeichnen. Leerer
        ''' Eingang bleibt leer (einseitige Korrekturen sind normal); ein nicht lesbares Raster
        ''' heißt False, und der Träger bleibt dann unangetastet.</summary>
        Private Shared Function RerasterBrushAlpha(png As String, oldRect As SKRect,
                                                   l As Integer, t As Integer, w As Integer, h As Integer,
                                                   canvasSetup As Action(Of SKCanvas),
                                                   sampling As SKSamplingOptions,
                                                   ByRef result As String) As Boolean
            result = ""
            If String.IsNullOrEmpty(png) Then Return True
            Dim decoded As SKBitmap = Nothing
            Try
                decoded = SKBitmap.Decode(Convert.FromBase64String(png))
                If decoded Is Nothing OrElse decoded.ColorType <> SKColorType.Alpha8 Then Return False
                Using neu = New SKBitmap(w, h, SKColorType.Alpha8, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(neu)
                        canvas.Clear(SKColors.Transparent)
                        ' Im QUELLRAUM zeichnen, wie beim Hauptraster: erst den Ursprung des neuen
                        ' Rechtecks wegschieben, dann die Transformation, dann das alte Raster an
                        ' seiner alten Stelle absetzen.
                        canvas.Translate(CSng(-l), CSng(-t))
                        canvasSetup(canvas)
                        Using paint = New SKPaint With {.IsAntialias = True}
                            Using image = SKImage.FromBitmap(decoded)
                                canvas.DrawImage(image, oldRect, sampling, paint)
                            End Using
                        End Using
                    End Using
                    Using img = SKImage.FromPixels(neu.PeekPixels())
                        Using data = img.Encode(SKEncodedImageFormat.Png, 100)
                            If data Is Nothing Then Return False
                            result = Convert.ToBase64String(data.ToArray())
                        End Using
                    End Using
                End Using
                Return True
            Catch
                Return False
            Finally
                decoded?.Dispose()
            End Try
        End Function

        ''' <summary>Spiegelt die Pinselkorrektur aller Träger - pixelgenau wie das Hauptraster in
        ''' FlipMaskRegion: das Raster wird in sich gespiegelt, das Rechteck wechselt die Seite.
        ''' Kein Resampling, damit nichts weicher wird.</summary>
        Private Shared Function FlipBrushCorrections(mask As ImageMask, horizontal As Boolean, axis As Double) As Boolean
            Return ForEachBrushCarrier(mask,
                Function(c)
                    Dim newAdd = MirrorAlphaRaster(c.BrushAddPngBase64, horizontal)
                    Dim newSubtract = MirrorAlphaRaster(c.BrushSubtractPngBase64, horizontal)
                    If newAdd Is Nothing OrElse newSubtract Is Nothing Then Return False
                    c.BrushAddPngBase64 = newAdd
                    c.BrushSubtractPngBase64 = newSubtract
                    Dim width = c.BrushRight - c.BrushLeft
                    Dim height = c.BrushBottom - c.BrushTop
                    If horizontal Then
                        c.BrushLeft = CInt(Math.Round(2 * axis - c.BrushRight))
                        c.BrushRight = c.BrushLeft + width
                    Else
                        c.BrushTop = CInt(Math.Round(2 * axis - c.BrushBottom))
                        c.BrushBottom = c.BrushTop + height
                    End If
                    Return True
                End Function)
        End Function

        ''' <summary>Ein Alpha8-Raster in sich spiegeln. Leer bleibt leer; Nothing heißt Fehler.</summary>
        Private Shared Function MirrorAlphaRaster(png As String, horizontal As Boolean) As String
            If String.IsNullOrEmpty(png) Then Return ""
            Dim decoded As SKBitmap = Nothing
            Try
                decoded = SKBitmap.Decode(Convert.FromBase64String(png))
                If decoded Is Nothing OrElse decoded.ColorType <> SKColorType.Alpha8 Then Return Nothing
                Using gespiegelt = New SKBitmap(decoded.Width, decoded.Height, SKColorType.Alpha8, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(gespiegelt)
                        canvas.Clear(SKColors.Transparent)
                        If horizontal Then
                            canvas.Scale(-1.0F, 1.0F, decoded.Width / 2.0F, 0.0F)
                        Else
                            canvas.Scale(1.0F, -1.0F, 0.0F, decoded.Height / 2.0F)
                        End If
                        canvas.DrawBitmap(decoded, 0.0F, 0.0F)
                    End Using
                    Using img = SKImage.FromPixels(gespiegelt.PeekPixels())
                        Using data = img.Encode(SKEncodedImageFormat.Png, 100)
                            If data Is Nothing Then Return Nothing
                            Return Convert.ToBase64String(data.ToArray())
                        End Using
                    End Using
                End Using
            Catch
                Return Nothing
            Finally
                decoded?.Dispose()
            End Try
        End Function

        Private Shared Function IsGradientMaskKind(kind As String) As Boolean
            Return String.Equals(kind, "Linear", StringComparison.OrdinalIgnoreCase) OrElse
                   String.Equals(kind, "Radial", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Trägt die Maske überhaupt etwas, das sich mitnehmen lässt? Ein gerechneter
        ''' Verlauf hat kein Raster - die Prüfung auf ein leeres PngBase64 allein hätte ihn
        ''' abgewiesen.</summary>
        Private Shared Function HasMovableMaskContent(mask As ImageMask) As Boolean
            If mask Is Nothing Then Return False
            If mask.HasPixelData Then Return True
            If IsGradientMaskKind(mask.Kind) Then Return True
            If mask.ExtraComponents IsNot Nothing Then
                For Each c In mask.ExtraComponents
                    If c Is Nothing Then Continue For
                    ' Ein GEMALTER Zusatzbestandteil zaehlt genauso: eine Maske mit leerem ersten und
                    ' nur gemaltem zweiten Bestandteil galt vorher als unbeweglich und blieb liegen.
                    If IsGradientMaskKind(c.Kind) OrElse c.HasPixelData Then Return True
                Next
            End If
            Return False
        End Function

        ''' <summary>Dreht die Maskenregion um einen Punkt - das Gegenstück zu
        ''' <see cref="TransformMaskRegion"/> für Gruppen-Drehungen. Ohne das blieb die Korrektur einer
        ''' mitgedrehten Gruppe an Ort und Lage liegen, während ihre Objekte wanderten.
        '''
        ''' Die Raster werden dabei NEU GERASTERT (Alpha8 kennt keine Drehung im Rechteck), verlieren
        ''' also bei jeder Drehung ein wenig Kantenschärfe - dieselbe bewusst in Kauf genommene Einbuße
        ''' wie beim Skalieren. Gerechnet wird im Quellraum, in dem die Maske gespeichert ist.</summary>
        Public Shared Function RotateMaskRegion(mask As ImageMask, degrees As Double,
                                                pivotX As Double, pivotY As Double,
                                                Optional part As MaskTransformPart = MaskTransformPart.All) As Boolean
            If Not HasMovableMaskContent(mask) Then Return False
            If Math.Abs(degrees) < 0.0001 Then Return True
            If part = MaskTransformPart.RasterOnly Then GoTo RasterTeil

            Dim radForPoints = degrees * Math.PI / 180.0
            MapGradientComponents(mask, Function(x, y)
                                            Dim dx = x - pivotX, dy = y - pivotY
                                            Return (pivotX + dx * Math.Cos(radForPoints) - dy * Math.Sin(radForPoints),
                                                    pivotY + dx * Math.Sin(radForPoints) + dy * Math.Cos(radForPoints))
                                        End Function)
            If part = MaskTransformPart.GradientsOnly Then Return True
RasterTeil:
            ' Die Pinselkorrektur ist ebenfalls ein Raster und wandert deshalb HIER (beim
            ' Loslassen), nicht im Verlaufsteil. Vor dem fruehen Ausstieg: ein reiner Verlauf
            ' hat kein Hauptraster, seine Korrektur muss trotzdem mit.
            Dim radBrush = degrees * Math.PI / 180.0
            Dim cosBrush = Math.Cos(radBrush), sinBrush = Math.Sin(radBrush)
            If Not TransformBrushCorrections(mask,
                    Function(x, y)
                        Dim bdx = x - pivotX, bdy = y - pivotY
                        Return (pivotX + bdx * cosBrush - bdy * sinBrush,
                                pivotY + bdx * sinBrush + bdy * cosBrush)
                    End Function,
                    Sub(cv) cv.RotateDegrees(CSng(degrees), CSng(pivotX), CSng(pivotY)),
                    New SKSamplingOptions(SKCubicResampler.Mitchell)) Then Return False
            ' JEDES gemalte Raster, nicht nur das primaere: ein gemalter Bestandteil neben einem
            ' Verlauf gehoert genauso zur Maske.
            Return ForEachRasterCarrier(mask, Function(c) RotateComponentRaster(c, degrees, pivotX, pivotY))
        End Function

        ''' <summary>Spiegelt die Maskenregion an einer Achse (Gruppen-Spiegelung). Hier bleibt die
        ''' Auflösung erhalten - gespiegelt wird pixelgenau, nur die Lage wechselt die Seite.</summary>
        Public Shared Function FlipMaskRegion(mask As ImageMask, horizontal As Boolean, axis As Double,
                                              Optional part As MaskTransformPart = MaskTransformPart.All) As Boolean
            If Not HasMovableMaskContent(mask) Then Return False
            If part = MaskTransformPart.RasterOnly Then GoTo RasterTeil

            MapGradientComponents(mask, Function(x, y)
                                            If horizontal Then Return (2.0 * axis - x, y)
                                            Return (x, 2.0 * axis - y)
                                        End Function)
            If part = MaskTransformPart.GradientsOnly Then Return True
RasterTeil:
            ' Die Pinselkorrektur spiegelt pixelgenau mit - vor dem fruehen Ausstieg, damit auch
            ' ein reiner Verlauf (ohne Hauptraster) seine Korrektur behaelt.
            If Not FlipBrushCorrections(mask, horizontal, axis) Then Return False
            ' JEDES gemalte Raster, aus demselben Grund wie beim Drehen.
            Return ForEachRasterCarrier(mask, Function(c) FlipComponentRaster(c, horizontal, axis))
        End Function

        ''' <summary>Bildet eine gespeicherte Maske im QUELLRAUM auf ein neues Rechteck ab - dieselbe
        ''' Abbildung, die eine Gruppen-Transformation auf ihre Objekte anwendet. Ohne das bliebe die
        ''' Korrektur einer mitbewegten Ebene an Ort und Größe stehen.
        '''
        ''' Beim echten Skalieren werden die Raster neu gerastert - das kostet etwas Kantenschärfe,
        ''' ist aber die einzige Möglichkeit, solange die Ursprungsform nicht als Geometrie
        ''' aufbewahrt wird. Eine reine Verschiebung behält die Bildpunkte.</summary>
        Public Shared Function TransformMaskRegion(mask As ImageMask,
                                                   scaleX As Double, scaleY As Double,
                                                   pivotX As Double, pivotY As Double,
                                                   offsetX As Double, offsetY As Double,
                                                   Optional part As MaskTransformPart = MaskTransformPart.All) As Boolean
            If Not HasMovableMaskContent(mask) Then Return False
            If scaleX <= 0 OrElse scaleY <= 0 Then Return False
            If part = MaskTransformPart.RasterOnly Then GoTo RasterTeil

            MapGradientComponents(mask, Function(x, y) (pivotX + (x - pivotX) * scaleX + offsetX,
                                                        pivotY + (y - pivotY) * scaleY + offsetY))
            ' Reine Verschiebung: die Pinselkorrektur eines Verlaufs wandert exakt mit - hier im
            ' Verlaufsteil, damit sie waehrend eines Zuges LIVE folgt (das Versetzen des Rechtecks
            ' kostet nichts). Beim Skalieren muss sie dagegen neu gerastert werden und folgt wie
            ' das Hauptraster erst beim Loslassen (Raster-Teil unten).
            If Math.Abs(scaleX - 1.0) < 0.0001 AndAlso Math.Abs(scaleY - 1.0) < 0.0001 Then
                ShiftBrushCorrections(mask, CInt(Math.Round(offsetX)), CInt(Math.Round(offsetY)))
            End If
            If part = MaskTransformPart.GradientsOnly Then Return True
RasterTeil:
            ' Beim echten Skalieren die Korrektur neu rastern - vor dem fruehen Ausstieg, damit
            ' auch ein reiner Verlauf (ohne Hauptraster) seine Korrektur behaelt. Die reine
            ' Verschiebung ist hier NICHT zu behandeln: sie laeuft im Verlaufsteil oben, und zwar
            ' fuer part=All wie fuer die Zug-Folge GradientsOnly-dann-RasterOnly genau einmal.
            If Not (Math.Abs(scaleX - 1.0) < 0.0001 AndAlso Math.Abs(scaleY - 1.0) < 0.0001) Then
                If Not TransformBrushCorrections(mask,
                        Function(x, y) (pivotX + (x - pivotX) * scaleX + offsetX,
                                        pivotY + (y - pivotY) * scaleY + offsetY),
                        Sub(cv)
                            cv.Translate(CSng(pivotX + offsetX), CSng(pivotY + offsetY))
                            cv.Scale(CSng(scaleX), CSng(scaleY))
                            cv.Translate(CSng(-pivotX), CSng(-pivotY))
                        End Sub,
                        New SKSamplingOptions(SKCubicResampler.Mitchell)) Then Return False
            End If
            ' JEDES gemalte Raster, aus demselben Grund wie beim Drehen.
            Return ForEachRasterCarrier(mask,
                Function(c) TransformComponentRaster(c, scaleX, scaleY, pivotX, pivotY, offsetX, offsetY))
        End Function

        ''' <summary>Weicher Pinselstrich als Alpha8-Maske in der Größe von <paramref name="rect"/>
        ''' (Display-Bildraum). Die Strichpunkte liegen im Display-Bildraum; sie werden um rect.Left/Top
        ''' in die Stempel-lokalen Koordinaten verschoben. Für den Commit eines Strichs, danach über
        ''' ApplySelectionCandidate mit dem aktuellen Kombiniermodus verrechnet.</summary>
        Public Shared Function BuildSoftBrushStampMask(pts As IReadOnlyList(Of SKPoint), radius As Single,
                                                       softnessPx As Single, rect As SKRectI) As SKBitmap
            If pts Is Nothing OrElse pts.Count = 0 OrElse rect.Width <= 0 OrElse rect.Height <= 0 Then Return Nothing
            Dim local As New List(Of SKPoint)(pts.Count)
            For Each p In pts
                local.Add(New SKPoint(p.X - rect.Left, p.Y - rect.Top))
            Next
            Using rgba = New SKBitmap(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Using canvas = New SKCanvas(rgba)
                    canvas.Clear(SKColors.Transparent)
                    DrawSoftMaskStroke(canvas, local, radius, softnessPx, SKColors.White)
                End Using
                Return AlphaMaskFrom(rgba)
            End Using
        End Function

    End Class

End Namespace
