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

Namespace Services

    ' Partial: die Gleitkomma-Tonwertkette liegt in ImageProcessorPointOps.vb, die automatische
    ' Bildverbesserung in ImageProcessorAutoAdjust.vb.
    ' Die Datenmodelle standen bis 2026-08-06 im Kopf dieser Datei und liegen jetzt daneben:
    ' ImageAnnotationModels.vb (Objekte), ImageMaskModels.vb (Masken),
    ' ImageAdjustmentsModels.vb (Rezept).
    Partial Public Class ImageProcessor

        Private Const FastPngCompressionQuality As Integer = 60

        ''' SKPaint trug bis SkiaSharp 2 die Schrift selbst. Sein interner Ersatz-SKFont hat
        ''' LinearMetrics=True - ein frisch erzeugter SKFont dagegen False, was Textbreiten und das
        ''' Rendering messbar verändert (geprüft: identische Bytes erst mit LinearMetrics=True).
        Private Shared Function CreateFont(fontFamily As String, fontSize As Single,
                                           Optional bold As Boolean = False, Optional italic As Boolean = False) As SKFont
            Return New SKFont(GetTypeface(fontFamily, bold, italic), fontSize) With {.LinearMetrics = True}
        End Function

        ''' SkiaSharp hat SKFilterQuality zugunsten von SKSamplingOptions abgekündigt. Diese Werte sind
        ''' exakt die, auf die SkiaSharp die alten Stufen intern abbildet (siehe SkiaExtensions.ToSamplingOptions):
        ''' High = kubisch (Mitchell), Medium = linear mit Mipmaps.
        ''' Friend: auch PrintService skaliert Bilder auf die Druckseite und braucht dieselbe Abtastung.
        Friend Shared ReadOnly SamplingHigh As New SKSamplingOptions(SKCubicResampler.Mitchell)

        ''' Die mittlere Stufe derselben Abbildung. Friend, weil die Gesichtsausschnitte der
        ''' Infoleiste und der Personenverwaltung sie brauchen: ein Feld von 88 Punkten rechtfertigt
        ''' die kubische Abtastung nicht, und ohne ausdrueckliche Angabe faellt es auf Nearest.
        Friend Shared ReadOnly SamplingMedium As New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)

        ''' Zeichnet eine Bitmap mit ausdrücklicher Abtastung. SKCanvas.DrawBitmap kennt keine
        ''' SKSamplingOptions-Überladung, DrawImage schon - ohne sie fiele die Skalierung auf
        ''' Nearest zurück, weil SKSamplingOptions.Default nicht filtert.
        Friend Shared Sub DrawBitmapSampled(canvas As SKCanvas, bitmap As SKBitmap, source As SKRect, dest As SKRect,
                                             sampling As SKSamplingOptions, paint As SKPaint)
            Using image = SKImage.FromBitmap(bitmap)
                canvas.DrawImage(image, source, dest, sampling, paint)
            End Using
        End Sub

        ''' Friend wie die Rechteck-Fassung darueber: das Vergleichsmodell zeichnet sein Gesicht
        ''' ueber eine gesetzte Matrix und braucht deshalb genau diese Ueberladung.
        Friend Shared Sub DrawBitmapSampled(canvas As SKCanvas, bitmap As SKBitmap, x As Single, y As Single,
                                             sampling As SKSamplingOptions, paint As SKPaint)
            Using image = SKImage.FromBitmap(bitmap)
                canvas.DrawImage(image, x, y, sampling, paint)
            End Using
        End Sub


        ' Cache des zuletzt berechneten Bildes VOR dem Einzeichnen der Objekte (Annotations).
        ' Beim Live-Verschieben/Bearbeiten eines Objekts ändert sich nur dieser letzte Schritt,
        ' daher muss die teure Pipeline (Belichtung, Kurven, Filter, Schärfen, ...) nicht jedes
        ' Mal neu durchlaufen werden - nur bei der Vorschau (previewSource), nicht beim finalen
        ' Export/Speichern, das immer frisch vom Originalbild rechnet.
        Private Shared ReadOnly _baseCacheLock As New Object()
        Private Shared _baseCacheKey As String = Nothing
        Private Shared _baseCacheSourceRef As SKBitmap = Nothing
        Private Shared _baseCacheBitmap As SKBitmap = Nothing

        ''' <summary>Warum ein Objekt-Region-Render kein Patch liefern konnte. Busy darf kurz
        ''' wiederholt werden; Stale braucht zwingend einen neuen Vollrender, weil nur dieser den
        ''' Basis-Cache mit den aktuellen Bildanpassungen aufbauen kann.</summary>
        Public Enum AnnotationPatchCacheState
            Unknown = 0
            Current = 1
            Busy = 2
            Stale = 3
        End Enum

        ' Zweiter Cache neben dem Base-Cache: das Basisbild MIT allen Raster-Strichen (Pinsel/Radierer),
        ' in Preview-Auflösung. Damit muss beim Malen nur das NEUE Strichsegment nachgezeichnet werden,
        ' statt alle Striche pro Maus-Batch neu zu rendern. Der Zustand + die Zeichenlogik liegen in
        ' RasterCompositeCache; hier werden die öffentlichen Einstiege unter _baseCacheLock gehalten und
        ' das gültige Base-Bitmap hereingereicht (siehe TryRenderRasterPaintIncrementalPatch).

        ' Ersetzt SKBitmap.Decode(path) an den Stellen, die das tatsächlich bearbeitete Foto laden
        ' (nicht Icons/Sticker-Assets oder reine Pixel-Statistik) - korrigiert die EXIF-Orientierung
        ' einmalig an der Quelle, damit die gesamte Anpassungs-/Export-Pipeline darauf aufbaut.
        ''' Liefert den zu dekodierenden Bild-Stream für einen Pfad: bei RAW die eingebettete
        ''' JPEG-Vorschau (RawPreviewService), bei PSD/PSB das zusammengesetzte Gesamtbild, sonst
        ''' die Datei direkt.
        '''
        ''' ACHTUNG - das ist NICHT mehr der einzige RAW-Weg: DecodeOriented versucht ZUERST die
        ''' echte RAW-Entwicklung über das System-libraw (volles Demosaic mit Kamera-Weißabgleich,
        ''' und landet erst dann hier. Diese Funktion ist also der
        ''' RÜCKFALL, wenn libraw fehlt oder die Datei nicht entwickelt werden kann.
        ''' Der Satz "Bearbeitung wirkt bei RAW nur auf die eingebettete Vorschau" stimmt seit der
        ''' libraw-Anbindung nur noch für diesen Rückfall.
        '''
        ''' Unverändert gilt: die RAW-Datei wird nie als Schreibziel berührt - Speichern schreibt
        ''' immer in eine neue Zieldatei, Reglerstände gehen in das .fpxmp-Sidecar.
        Private Shared Function OpenSourceStream(path As String) As Stream
            If RawPreviewService.IsSupportedRaw(path) Then Return RawPreviewService.ExtractPreview(path)
            ' ICO ist ein Container, den SkiaSharp nicht kennt - hier als PNG hereingereicht.
            If IcoPreviewService.IsSupportedIco(path) Then Return IcoPreviewService.ExtractPreview(path)
            ' PSD/PSB nur-lesend: das zusammengesetzte Gesamtbild als PNG (siehe PsdPreviewService).
            If PsdPreviewService.IsSupportedPsd(path) Then Return PsdPreviewService.ExtractPreview(path)
            ' HEIC/HEIF/AVIF kann SkiaSharp nicht - libheif liefert es als PNG herein (nur lesend,
            ' siehe HeifDecodeService). Fehlt die Bibliothek, faellt es auf den normalen Datei-
            ' Strom zurueck; der Decode scheitert dann wie bisher sichtbar statt still falsch.
            If HeifDecodeService.IsSupportedHeif(path) AndAlso HeifDecodeService.IsAvailable Then
                Dim heif = HeifDecodeService.ExtractPreview(path)
                If heif IsNot Nothing Then Return heif
            End If
            ' TIFF kann SkiaSharp ebenfalls nicht - LibTiff.NET liefert es als PNG herein.
            If TiffPreviewService.IsSupportedTiff(path) Then
                Dim tiff = TiffPreviewService.ExtractPreview(path)
                If tiff IsNot Nothing Then Return tiff
            End If
            Return File.OpenRead(path)
        End Function

        ''' <summary>Der Weg zum fertigen Bild für Ausgabewege (Drucken, PDF): wie DecodeOriented,
        ''' aber .fpx-Projekte werden aus Basisbild + Rezept gerendert statt als ZIP an den Codec
        ''' gereicht - dort kam bisher Nothing zurück, was in einer leeren Seite endete. Der Aufrufer
        ''' übernimmt das SKBitmap.</summary>
        Friend Shared Function DecodeForOutput(path As String, Optional developRaw As Boolean = True) As SKBitmap
            If FpxService.IsFpx(path) Then Return RenderFpxFullResolution(path)
            Return DecodeOriented(path, developRaw)
        End Function

        ''' <summary>Wie <see cref="DecodeForOutput"/>, wendet aber zusätzlich ein danebenliegendes
        ''' Rezept an: Druck und Collage zeigen damit die BEARBEITETE Fassung - also das Bild, das
        ''' der Nutzer im Editor gespeichert hat, statt der rohen Quelle daneben.
        '''
        ''' Bewusst NICHT in DecodeForOutput selbst: die automatische Bildverbesserung misst dort
        ''' die unbearbeitete Quelle und dürfte das Rezept nicht schon eingerechnet bekommen.</summary>
        ''' <param name="developRaw">Nur wirksam, solange KEIN Rezept vorliegt. Mit Rezept wird immer
        ''' entwickelt - die eingebettete Vorschau wäre dort schlicht das falsche Bild.</param>
        ''' <param name="applyPendingBaked">Sollen vermerkte, aber noch nicht in den Pixeln
        ''' steckende Vorgaenge (Entrauschen, Objektentfernen, Retusche, Striche) mitgerechnet
        ''' werden? Vorgabe NEIN, und das mit Absicht: es kostet Minuten je Bild, und wer eine
        ''' Vorschau aufbaut, wartet nicht minutenlang. Die Ausgabewege fragen den Nutzer und geben
        ''' seine Antwort hier herein.</param>
        Friend Shared Function DecodeDevelopedForOutput(path As String, Optional developRaw As Boolean = True,
                                                        Optional applyPendingBaked As Boolean = False) As SKBitmap
            If FpxService.IsFpx(path) Then Return RenderFpxFullResolution(path)

            Dim rezept As ImageAdjustments = Nothing
            If RawSidecarService.IsSidecarFormat(path) AndAlso RawSidecarService.Exists(path) Then
                rezept = RawSidecarService.TryRead(path)
            End If

            Dim decoded = DecodeOriented(path, developRaw OrElse rezept IsNot Nothing)
            If decoded Is Nothing OrElse rezept Is Nothing Then Return decoded
            Using decoded
                ' Die gebackenen Vorgaenge gehoeren VOR die Reglerkette - sie sind Teil des Bildes,
                ' nicht eine Stufe darauf. Das Entrauschen etwa arbeitet auf dem entwickelten Bild
                ' und nicht auf einem, dem schon Kontrast und Klarheit angetan wurden.
                Dim input = decoded
                Dim reapplied As SKBitmap = Nothing
                Try
                    If applyPendingBaked Then
                        reapplied = ApplyPendingBakedOperations(decoded, rezept)
                        If reapplied IsNot Nothing Then input = reapplied
                    End If
                    Return ProcessBitmap(input, rezept)
                Finally
                    reapplied?.Dispose()
                End Try
            End Using
        End Function

        ''' <summary>Wie viele dieser Dateien tragen einen offenen Vorgang? Fuer die Frage vor dem
        ''' Drucken oder vor einer Collage: "es dauert laenger" muss man begruenden koennen, und bei
        ''' null betroffenen Bildern wird gar nicht erst gefragt.</summary>
        Public Shared Function CountPathsWithPendingBakedOperations(paths As IEnumerable(Of String)) As Integer
            If paths Is Nothing Then Return 0
            Dim count = 0
            For Each path In paths
                If String.IsNullOrWhiteSpace(path) Then Continue For
                If Not RawSidecarService.IsSidecarFormat(path) Then Continue For
                If Not RawSidecarService.Exists(path) Then Continue For
                If HasPendingBakedOperations(RawSidecarService.TryRead(path)) Then count += 1
            Next
            Return count
        End Function

        ''' <summary>Liegt in diesem Rezept ein Vorgang, der in die PIXEL gehoert, aber in den Pixeln
        ''' daneben NICHT steckt?
        '''
        ''' Vier Vorgaenge fallen darunter, und alle vier haben dasselbe Problem: sie lassen sich
        ''' nicht als Regler ausdruecken, sondern nur rechnen. Bei einer .fpx liegen ihre Pixel im
        ''' Buendel und ueberleben; neben einem RAW oder PSD liegt nur das Rezept, und die Pixel
        ''' entstehen bei jedem Oeffnen neu aus den Sensordaten.
        '''
        ''' Entrauschen und Objektentfernen stehen dafuer in <see cref="ImageAdjustments.BakedOperations"/>.
        ''' Retusche und Pinselstriche standen schon immer im Rezept - sie wurden nur nirgends wieder
        ''' angewandt, womit sie neben einem RAW genauso still verlorengingen. Deshalb zaehlen sie
        ''' hier mit.</summary>
        Public Shared Function HasPendingBakedOperations(adj As ImageAdjustments) As Boolean
            If adj Is Nothing OrElse adj.BakedOperationsApplied Then Return False
            If adj.BakedOperations IsNot Nothing AndAlso adj.BakedOperations.Count > 0 Then Return True
            If adj.RetouchSpots IsNot Nothing AndAlso adj.RetouchSpots.Count > 0 Then Return True
            If adj.RasterPaintStrokes IsNot Nothing AndAlso adj.RasterPaintStrokes.Count > 0 Then Return True
            Return False
        End Function

        ''' <summary>Was offen ist, in Worten - fuer die Frage vor dem Anwenden. Eine Frage, die nur
        ''' "es liegt etwas an" sagt, kann niemand beantworten.</summary>
        Public Shared Function DescribePendingBakedOperations(adj As ImageAdjustments) As String
            If Not HasPendingBakedOperations(adj) Then Return ""
            Dim parts As New List(Of String)()
            Dim denoiseCount = 0, removalCount = 0
            If adj.BakedOperations IsNot Nothing Then
                For Each op In adj.BakedOperations
                    If op Is Nothing Then Continue For
                    If String.Equals(op.Kind, BakedOperation.KindDenoise, StringComparison.OrdinalIgnoreCase) Then
                        denoiseCount += 1
                    ElseIf String.Equals(op.Kind, BakedOperation.KindObjectRemoval, StringComparison.OrdinalIgnoreCase) Then
                        removalCount += 1
                    End If
                Next
            End If
            If denoiseCount > 0 Then parts.Add(LocalizationService.T("Entrauschen"))
            If removalCount = 1 Then
                parts.Add(LocalizationService.T("Objekt entfernen"))
            ElseIf removalCount > 1 Then
                parts.Add(String.Format(LocalizationService.T("Objekt entfernen ({0} Stellen)"), removalCount))
            End If
            If adj.RetouchSpots IsNot Nothing AndAlso adj.RetouchSpots.Count > 0 Then
                parts.Add(LocalizationService.T("Retusche"))
            End If
            If adj.RasterPaintStrokes IsNot Nothing AndAlso adj.RasterPaintStrokes.Count > 0 Then
                parts.Add(LocalizationService.T("Pinsel"))
            End If
            Return String.Join(", ", parts)
        End Function

        ''' <summary>Die offenen Vorgaenge auf ein frisch entwickeltes Bild nachziehen. Zurueck kommt
        ''' ein NEUES Bitmap, das der Aufrufer uebernimmt - oder Nothing, wenn nichts zu tun war oder
        ''' nichts gelungen ist; dann behaelt der Aufrufer sein eigenes.
        '''
        ''' DIE EINZIGE STELLE, die das tut. Nicht aus Ordnungsliebe: waeren es zwei, koennte ein
        ''' Bild durch beide laufen, und ein zweimal entrauschtes oder zweimal gefuelltes Bild sieht
        ''' man erst, wenn man es nebeneinanderlegt.
        '''
        ''' REIHENFOLGE. Zuerst das Entrauschen und das Entfernen in der Reihenfolge, in der sie
        ''' gemacht wurden, danach Retusche und Striche. Das Entrauschen gehoert nach vorn, weil es
        ''' das ganze Bild anfasst: eine retuschierte Stelle wuerde sonst mitentrauscht und saehe
        ''' anders aus als in der Sitzung, in der sie entstanden ist. Ganz deckungsgleich wird es
        ''' nicht - wer in der Sitzung erst retuschiert und dann entrauscht hat, bekommt hier die
        ''' andere Reihenfolge. Der Unterschied ist kleiner als der Aufwand, die tatsaechliche
        ''' Reihenfolge mitzuschreiben.
        '''
        ''' FEHLT EIN MODELL, wird der Vorgang UEBERSPRUNGEN und der Rest trotzdem gemacht. Alles
        ''' abzubrechen hiesse, wegen eines fehlenden Modells auch die Retusche zu verlieren.</summary>
        ''' <param name="cancel">Abbruch durch den Nutzer. Hier laufen dieselben Modelle wie beim
        ''' ersten Mal, also dauert es genauso lange - und ein Abbruch muss genauso gehen. Bricht
        ''' einer der Vorgaenge ab, wird der GANZE Nachzug verworfen: ein Bild mit dem Entrauschen,
        ''' aber ohne die Retusche waere ein Zustand, den niemand bestellt hat.</param>
        Friend Shared Function ApplyPendingBakedOperations(source As SKBitmap, adj As ImageAdjustments,
                                                           Optional cancel As Threading.CancellationToken = Nothing) As SKBitmap
            If source Is Nothing OrElse Not HasPendingBakedOperations(adj) Then Return Nothing

            Dim current = source
            Dim owned = False
            Try
                If adj.BakedOperations IsNot Nothing Then
                    For Each op In adj.BakedOperations
                        If op Is Nothing Then Continue For
                        If cancel.IsCancellationRequested Then
                            If owned Then current.Dispose()
                            Return Nothing
                        End If
                        If String.Equals(op.Kind, BakedOperation.KindDenoise, StringComparison.OrdinalIgnoreCase) Then
                            current = ReplaceBitmapOwned(current, RunBakedDenoise(current, op, cancel), owned)
                        ElseIf String.Equals(op.Kind, BakedOperation.KindObjectRemoval, StringComparison.OrdinalIgnoreCase) Then
                            current = ReplaceBitmapOwned(current, RunBakedObjectRemoval(current, op, cancel), owned)
                        End If
                    Next
                End If
                If cancel.IsCancellationRequested Then
                    If owned Then current.Dispose()
                    Return Nothing
                End If

                ' Retusche und Striche zeichnen IN das Bild statt ein neues zu liefern. Solange noch
                ' die fremde Quelle dasteht, muss sie deshalb erst kopiert werden - hineinzumalen
                ' hiesse, dem Aufrufer sein eigenes Bild unter den Haenden zu veraendern.
                Dim hasSpots = adj.RetouchSpots IsNot Nothing AndAlso adj.RetouchSpots.Count > 0
                Dim hasStrokes = adj.RasterPaintStrokes IsNot Nothing AndAlso adj.RasterPaintStrokes.Count > 0
                If hasSpots OrElse hasStrokes Then
                    If Not owned Then
                        Dim copy = current.Copy()
                        If copy IsNot Nothing Then
                            current = copy
                            owned = True
                        End If
                    End If
                    If owned Then
                        Dim baseW = If(adj.SourceWidthPixels > 0, adj.SourceWidthPixels, current.Width)
                        Dim baseH = If(adj.SourceHeightPixels > 0, adj.SourceHeightPixels, current.Height)
                        If hasSpots Then
                            ApplyRetouchSpotsInPlace(current, current, adj.RetouchSpots, baseW, baseH)
                        End If
                        If hasStrokes Then
                            Using canvas = New SKCanvas(current)
                                Dim drawAdj As New ImageAdjustments With {
                                    .SourceWidthPixels = baseW, .SourceHeightPixels = baseH}
                                For Each stroke In adj.RasterPaintStrokes
                                    If stroke Is Nothing Then Continue For
                                    DrawAnnotationsOnCanvas(canvas, drawAdj, current.Width, current.Height,
                                                            0, 0, current.Width, current.Height,
                                                            New List(Of ImageAnnotation) From {stroke.ToRenderAnnotation()})
                                Next
                            End Using
                        End If
                    End If
                End If

                If Not owned Then Return Nothing
                Return current
            Catch ex As Exception
                DiagnosticLogService.LogException("ImageProcessor.BakedOperations", ex)
                If owned Then current.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Ein vermerkter Entrausch-Durchlauf. Nothing, wenn seine Modelldatei fehlt oder
        ''' der Vermerk eine Modellart nennt, die diese Fassung nicht kennt - dann bleibt der
        ''' Vorgang liegen, statt mit einem ANDEREN Modell nachgezogen zu werden. Ein Bild, das
        ''' anders aussieht als beim letzten Mal, ist schlechter als eines, dem etwas fehlt und das
        ''' es sagt.</summary>
        Private Shared Function RunBakedDenoise(source As SKBitmap, op As BakedOperation,
                                                cancel As Threading.CancellationToken) As SKBitmap
            Dim known = DenoiseModelService.KindFromRecipeName(op.DenoiseModel)
            If Not known.HasValue Then
                DiagnosticLogService.LogAlways("Entrauschen",
                    $"Vermerk nennt die unbekannte Modellart '{op.DenoiseModel}' - der Vorgang wird nicht nachgezogen")
                Return Nothing
            End If
            Dim kind = known.Value
            If kind = DenoiseModelService.DenoiseKind.Fast Then
                If Not DenoiseModelService.FastAvailable Then Return Nothing
            ElseIf Not DenoiseModelService.Available Then
                Return Nothing
            End If
            ' Das Modell nimmt nur Bgra8888. Ein anders belegtes Bild wird einmal umgelegt, statt den
            ' Vorgang wortlos ausfallen zu lassen.
            Dim converted As SKBitmap = Nothing
            Try
                Dim input = source
                If source.ColorType <> SKColorType.Bgra8888 Then
                    converted = New SKBitmap(New SKImageInfo(source.Width, source.Height,
                                                             SKColorType.Bgra8888, source.AlphaType))
                    Using canvas = New SKCanvas(converted)
                        Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                            canvas.DrawBitmap(source, 0, 0, paint)
                        End Using
                    End Using
                    input = converted
                End If
                Return DenoiseModelService.Denoise(input, kind,
                                                   CSng(Math.Max(0.0, Math.Min(100.0, op.DenoiseStrength)) / 100.0), cancel)
            Finally
                converted?.Dispose()
            End Try
        End Function

        ''' <summary>Ein vermerktes Objektentfernen. Ohne Maske gibt es nichts nachzuziehen - sie IST
        ''' der Auftrag.</summary>
        Private Shared Function RunBakedObjectRemoval(source As SKBitmap, op As BakedOperation,
                                                      cancel As Threading.CancellationToken) As SKBitmap
            If op.Mask Is Nothing Then Return Nothing
            If Not ObjectRemovalService.Available Then Return Nothing
            Using mask = MaskAsSourceBitmap(op.Mask, source.Width, source.Height)
                If mask Is Nothing Then Return Nothing
                Return ObjectRemovalService.Fill(source, mask, cancel)
            End Using
        End Function

        ''' <summary>Eine Rezeptmaske als Alpha8-Bild in der Groesse des Zielbildes.
        '''
        ''' MASSSTAB: die Maske liegt im Raum des ANZEIGEbildes (ImageMask.SourceWidthPixels), das
        ''' Ziel hat die volle Aufloesung der Datei. Bei einem 40-Megapixel-Foto, das auf 1600 Punkte
        ''' heruntergerechnet angezeigt wird, sind das Faktor drei: ohne die Umrechnung landet die
        ''' Maske im linken oberen Viertel und trifft nichts von dem, was sie treffen soll.</summary>
        Friend Shared Function MaskAsSourceBitmap(m As ImageMask, width As Integer, height As Integer) As SKBitmap
            If m Is Nothing OrElse width <= 0 OrElse height <= 0 Then Return Nothing
            ' Den kurzen Weg nimmt nur die EINTEILIGE gemalte Maske. Alles andere - mehrere
            ' Bestandteile oder ein gerechneter Verlauf - geht über den gemeinsamen Rasterisierer,
            ' der die Zielgröße ohnehin entgegennimmt. Vorher las diese Stelle allein den ersten
            ' Bestandteil: die Masken hier sind heute konstruktionsbedingt einteilig, eine
            ' mehrteilige verlöre ihre übrigen Teile aber still.
            If m.ComponentCount > 1 OrElse m.IsGradient OrElse m.InvertResult Then
                Return BuildCombinedMaskRaster(m, width, height)
            End If
            If String.IsNullOrWhiteSpace(m.PngBase64) Then Return Nothing
            Dim w = m.Right - m.Left, h = m.Bottom - m.Top
            If w <= 0 OrElse h <= 0 Then Return Nothing
            Dim target = New SKBitmap(New SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul))
            Try
                Dim sx = If(m.SourceWidthPixels > 0, width / CDbl(m.SourceWidthPixels), 1.0)
                Dim sy = If(m.SourceHeightPixels > 0, height / CDbl(m.SourceHeightPixels), 1.0)
                Using roh = SKBitmap.Decode(Convert.FromBase64String(m.PngBase64))
                    If roh Is Nothing Then
                        target.Dispose()
                        Return Nothing
                    End If
                    Using canvas = New SKCanvas(target)
                        canvas.Clear(SKColors.Transparent)
                        Using image = SKImage.FromBitmap(roh)
                            canvas.DrawImage(image, New SKRect(CSng(m.Left * sx), CSng(m.Top * sy),
                                                              CSng((m.Left + w) * sx), CSng((m.Top + h) * sy)),
                                             New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), Nothing)
                        End Using
                    End Using
                End Using
                If m.Inverted Then
                    ' Umgekehrte Auswahl heisst hier: alles ausser dem Markierten. Selten gewollt,
                    ' aber wenn es dasteht, muss es auch gelten.
                    Dim n = width * height
                    Dim buffer(n - 1) As Byte
                    Runtime.InteropServices.Marshal.Copy(target.GetPixels(), buffer, 0, n)
                    For i = 0 To n - 1
                        buffer(i) = CByte(255 - buffer(i))
                    Next
                    Runtime.InteropServices.Marshal.Copy(buffer, 0, target.GetPixels(), n)
                End If
                Return target
            Catch
                target.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Friend statt Private, damit PrintService dieselbe Dekodier-Route benutzt -
        ''' sie ist die einzige, die RAW/ICO/WebP und die EXIF-Orientierung korrekt behandelt.</summary>
        ''' <param name="developRaw">False = eine RAW-Quelle NICHT entwickeln, sondern ihre
        ''' eingebettete JPEG-Vorschau nehmen. Fuer Stapellaeufe, in denen Geschwindigkeit vor
        ''' Aufloesung geht (Einstellung "RAWs ohne Rezept im Stapel entwickeln"). Auf alles
        ''' andere hat der Schalter keine Wirkung.</param>
        ''' <param name="lensChoice">Welche Objektivkorrekturen fuer DIESES Bild gelten. Nothing =
        ''' wie in den Einstellungen vorgegeben. Sie gehoert hierher und nicht in die Reglerkette:
        ''' sie veraendert den DECODE, nicht die Nachbearbeitung.</param>
        Friend Shared Function DecodeOriented(path As String, Optional developRaw As Boolean = True,
                                              Optional lensChoice As LensDataService.Wahl = Nothing) As SKBitmap
            ' Echte RAW-Entwicklung, wenn das System-libraw da ist: voll aufgelöstes Demosaic mit
            ' Kamera-Weißabgleich statt der eingebetteten JPEG-Vorschau. Liefert der Decode nichts
            ' (defekte Datei, exotisches Format), greift darunter der bisherige Vorschau-Weg.
            If developRaw AndAlso RawPreviewService.IsSupportedRaw(path) AndAlso RawDecodeService.IsAvailable Then
                Dim developed = RawDecodeService.TryDecode(path, lensChoice)
                If developed IsNot Nothing Then Return developed
            ElseIf Not RawPreviewService.IsSupportedRaw(path) Then
                ' Anderes Hauptbild -> der ~180-MB-Entwicklungs-Cache ist stale und kann weg.
                RawDecodeService.ClearCache()
            End If
            ' SKCodec.Create(Stream) übernimmt den Stream, und manche Codecs (insbesondere WebP) schließen
            ' ihn dabei sofort. Ein späteres stream.Seek für den Fallback-Decode wirft dann
            ' ObjectDisposedException - WebP-Quellen ließen sich deshalb weder öffnen noch konvertieren.
            ' Den Inhalt daher einmal in SKData puffern und alle Decode-Pfade daraus bedienen.
            Dim data As SKData
            Using stream = OpenSourceStream(path)
                If stream Is Nothing Then Return Nothing
                data = SKData.Create(stream)
            End Using
            If data Is Nothing Then Return Nothing

            Using data
                Using codec = SKCodec.Create(data)
                    If codec Is Nothing OrElse codec.EncodedOrigin = SKEncodedOrigin.TopLeft Then
                        ' SKBitmap.Decode nimmt das Profil der Datei in die Bitmap mit, ToSrgb liest
                        ' es dort selbst ab - ein zweiter Parameter waere hier ueberfluessig.
                        Return ToManagedSrgb(SKBitmap.Decode(data))
                    End If

                    Dim info = codec.Info
                    ' Die Zielangabe bleibt farbraumlos, der Decode also unveraendert: der Codec
                    ' liefert die Zahlen so, wie sie in der Datei stehen. Gewandelt wird erst ganz
                    ' am Ende, mit dem Profil aus dem Dateikopf.
                    Dim sourceProfile = info.ColorSpace
                    Dim decodeInfo = New SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Dim original = New SKBitmap(decodeInfo)
                    Dim result = codec.GetPixels(decodeInfo, original.GetPixels())
                    If result <> SKCodecResult.Success AndAlso result <> SKCodecResult.IncompleteInput Then
                        original.Dispose()
                        Return ToManagedSrgb(SKBitmap.Decode(data))
                    End If

                    Dim corrected = ImageOrientationService.ApplyOrientation(original, codec.EncodedOrigin)
                    If Not Object.ReferenceEquals(corrected, original) Then original.Dispose()
                    Return ToManagedSrgb(corrected, sourceProfile)
                End Using
            End Using
        End Function

        ''' <summary>Farbmanagement am Ausgang des Decodes: ein abweichendes Profil wird nach sRGB
        ''' gewandelt, alles andere laeuft unveraendert durch (siehe ColorManagementService).
        '''
        ''' Die Freigabe der Vorgaengerbitmap gehoert hierher und nicht an jede Aufrufstelle: sie
        ''' darf NUR erfolgen, wenn tatsaechlich gewandelt wurde.</summary>
        Private Shared Function ToManagedSrgb(decoded As SKBitmap,
                                              Optional sourceProfile As SKColorSpace = Nothing) As SKBitmap
            If decoded Is Nothing Then Return Nothing
            Dim managed = ColorManagementService.ToSrgb(decoded, sourceProfile)
            If Not Object.ReferenceEquals(managed, decoded) Then decoded.Dispose()
            Return managed
        End Function

        ''' TEMPORÄR ( Vorher/Nachher dunkler): protokolliert den Farbraum des rohen
        ''' Datei-Decodes und ob eine Farbkonvertierung nach sRGB den Mittelpixel ändert. So lässt sich
        ''' hart bestätigen, ob die Skia-Pipeline (farbraumlose Zwischen-Bitmaps) gegenüber dem
        ''' Avalonia-Decoder (New Bitmap) einen Helligkeits-/Farbversatz erzeugt. Nach der Auswertung
        ''' wieder entfernen.
        Public Shared Sub LogDecodeColorDiagnostics(path As String)
            Try
                If String.IsNullOrWhiteSpace(path) Then Return
                Dim data As SKData
                Using stream = OpenSourceStream(path)
                    If stream Is Nothing Then Return
                    data = SKData.Create(stream)
                End Using
                If data Is Nothing Then Return
                Using data
                    Using raw = SKBitmap.Decode(data)
                        If raw Is Nothing Then Return
                        Dim cs = raw.ColorSpace
                        Dim csDesc = If(cs Is Nothing, "null", If(cs.IsSrgb, "sRGB", "non-sRGB"))
                        Dim cx = raw.Width \ 2
                        Dim cy = raw.Height \ 2
                        Dim pRaw = raw.GetPixel(cx, cy)

                        ' Ziel farbraumlos (wie der Pipeline-Start via New SKBitmap ohne ColorSpace):
                        Dim pNull As SKColor = pRaw
                        Using drawnNull = New SKBitmap(raw.Width, raw.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                            Using canvas = New SKCanvas(drawnNull)
                                canvas.Clear(SKColors.Transparent)
                                canvas.DrawBitmap(raw, 0, 0)
                            End Using
                            pNull = drawnNull.GetPixel(cx, cy)
                        End Using

                        ' Ziel explizit sRGB (farbverwaltet):
                        Dim pSrgb As SKColor = pRaw
                        Dim srgbInfo = New SKImageInfo(raw.Width, raw.Height, SKColorType.Bgra8888, SKAlphaType.Premul, SKColorSpace.CreateSrgb())
                        Using drawnSrgb = New SKBitmap(srgbInfo)
                            Using canvas = New SKCanvas(drawnSrgb)
                                canvas.Clear(SKColors.Transparent)
                                canvas.DrawBitmap(raw, 0, 0)
                            End Using
                            pSrgb = drawnSrgb.GetPixel(cx, cy)
                        End Using

                        DiagnosticLogService.LogAlways("Editor.DecodeColorCheck",
                            $"file={IO.Path.GetFileName(path)} colorType={raw.ColorType} colorSpace={csDesc} " &
                            $"rawCenter=#{pRaw.Red:X2}{pRaw.Green:X2}{pRaw.Blue:X2} " &
                            $"nullTarget=#{pNull.Red:X2}{pNull.Green:X2}{pNull.Blue:X2} " &
                            $"srgbTarget=#{pSrgb.Red:X2}{pSrgb.Green:X2}{pSrgb.Blue:X2} " &
                            $"nullVsSrgbDiffers={pNull <> pSrgb}")
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.DecodeColorCheck", ex)
            End Try
        End Sub

        Public Shared Function ApplyAdjustments(sourcePath As String, adj As ImageAdjustments) As Bitmap
            Using original = DecodeOriented(sourcePath, True, LensChoiceFrom(adj))
                If original Is Nothing Then Return Nothing

                Using processed = ProcessBitmap(original, adj)
                    Return ToAvaloniaBitmap(processed)
                End Using
            End Using
        End Function

        ''' <summary>Obergrenze für das Anzeigebild (composite.png) in einem .fpx-Bündel. Es ist nur
        ''' die schnelle Vorschau für Galerie und Betrachter; die volle Auflösung entsteht beim
        ''' Öffnen wieder aus Basisbild + Rezept. Ohne Deckel wäre jedes Bündel doppelt so groß wie
        ''' nötig.</summary>
        Public Const FpxCompositeMaxDimension As Integer = 2560

        ''' <summary>Kodiert ein Bitmap als PNG in einen Speicherstrom, bei Bedarf auf
        ''' <paramref name="maxDimension"/> verkleinert. Der Aufrufer übernimmt den Strom.</summary>
        Friend Shared Function EncodePngStream(bitmap As SKBitmap, Optional maxDimension As Integer = 0) As MemoryStream
            If bitmap Is Nothing Then Return Nothing
            Dim scaled As SKBitmap = Nothing
            Dim source = bitmap
            Try
                If maxDimension > 0 AndAlso (bitmap.Width > maxDimension OrElse bitmap.Height > maxDimension) Then
                    Dim ratio = Math.Min(maxDimension / CDbl(bitmap.Width), maxDimension / CDbl(bitmap.Height))
                    Dim w = Math.Max(1, CInt(Math.Round(bitmap.Width * ratio)))
                    Dim h = Math.Max(1, CInt(Math.Round(bitmap.Height * ratio)))
                    scaled = bitmap.Resize(New SKImageInfo(w, h), SamplingHigh)
                    If scaled IsNot Nothing Then source = scaled
                End If

                Dim stream = New MemoryStream()
                Using image = SKImage.FromBitmap(source)
                    Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                        If data Is Nothing Then
                            stream.Dispose()
                            Return Nothing
                        End If
                        data.SaveTo(stream)
                    End Using
                End Using
                stream.Position = 0
                Return stream
            Finally
                scaled?.Dispose()
            End Try
        End Function

        Public Shared Function RenderPngStream(sourcePath As String, adj As ImageAdjustments) As MemoryStream
            Dim decodeMs As Long = 0
            Dim processMs As Long = 0
            Dim encodeMs As Long = 0
            Return RenderPngStream(sourcePath, adj, 0, decodeMs, processMs, encodeMs)
        End Function

        Public Shared Function RenderPngStream(sourcePath As String, adj As ImageAdjustments,
                                               maxDimension As Integer,
                                               ByRef decodeMs As Long, ByRef processMs As Long, ByRef encodeMs As Long,
                                               Optional preferReducedRawDecode As Boolean = False) As MemoryStream
            Dim sw = Diagnostics.Stopwatch.StartNew()
            ' Nur die Kachelpipeline darf den halben RAW-Decode anfordern. Der allgemeine Weg
            ' bleibt voll aufgeloest, damit Editor, Export und Druck nie eine Vorschauauflosung
            ' bekommen. Die Objektivwahl gehoert zum Decode und muss auch fuer die Kachel gelten.
            Using original = If(preferReducedRawDecode,
                                RawDecodeService.TryDecodeThumbnail(sourcePath, LensChoiceFrom(adj)),
                                DecodeOriented(sourcePath))
                decodeMs = sw.ElapsedMilliseconds
                If original Is Nothing Then Return Nothing

                Dim workingSource = CreatePreviewWorkingBitmap(original, maxDimension)
                If workingSource Is Nothing Then Return Nothing

                Try
                    sw.Restart()
                    Using processed = ProcessBitmap(workingSource, adj)
                        processMs = sw.ElapsedMilliseconds
                        Return EncodePngStream(processed, encodeMs)
                    End Using
                Finally
                    If Not Object.ReferenceEquals(workingSource, original) Then workingSource.Dispose()
                End Try
            End Using
        End Function

        Public Shared Function RenderPngStream(source As SKBitmap, adj As ImageAdjustments,
                                               ByRef processMs As Long, ByRef encodeMs As Long) As MemoryStream
            If source Is Nothing Then Return Nothing
            Dim sw = Diagnostics.Stopwatch.StartNew()
            Using processed = ProcessBitmap(source, adj)
                processMs = sw.ElapsedMilliseconds
                Return EncodePngStream(processed, encodeMs)
            End Using
        End Function

        Private Shared Function EncodePngStream(bitmap As SKBitmap, ByRef encodeMs As Long) As MemoryStream
            Dim sw = Diagnostics.Stopwatch.StartNew()
            Using image = SKImage.FromBitmap(bitmap)
                ' PNG bleibt verlustfrei; niedrigerer Quality-Wert reduziert hier die Encoder-Arbeit.
                Using data = image.Encode(SKEncodedImageFormat.Png, FastPngCompressionQuality)
                    Dim ms As New MemoryStream()
                    data.SaveTo(ms)
                    ms.Position = 0
                    encodeMs = sw.ElapsedMilliseconds
                    Return ms
                End Using
            End Using
        End Function

        ''' <summary>Die Objektiv-Wahl aus einem Rezept. Eigene Stelle, damit jeder Weg, der aus
        ''' einem Rezept dekodiert, dieselbe Umsetzung benutzt.</summary>
        Friend Shared Function LensChoiceFrom(adj As ImageAdjustments) As LensDataService.Wahl
            If adj Is Nothing Then Return Nothing
            Return New LensDataService.Wahl With {
                .Distortion = adj.LensDistortion,
                .ChromaticAberration = adj.LensTca,
                .Vignetting = adj.LensVignetting,
                .DistortionStrength = adj.LensDistortionAmount / 100.0,
                .ChromaticAberrationStrength = adj.LensTcaAmount / 100.0,
                .VignettingStrength = adj.LensVignettingAmount / 100.0,
                .LensModel = adj.LensModel}
        End Function

        Public Shared Function ApplyAdjustments(source As SKBitmap, adj As ImageAdjustments) As Bitmap
            Return ApplyAdjustments(source, adj, 0)
        End Function

        Public Shared Function ApplyAdjustments(source As SKBitmap, adj As ImageAdjustments, maxDimension As Integer) As Bitmap
            If source Is Nothing Then Return Nothing

            Dim workingSource = CreatePreviewWorkingBitmap(source, maxDimension)
            If workingSource Is Nothing Then Return Nothing

            If Not Object.ReferenceEquals(workingSource, source) Then
                Try
                    Using processed = ProcessBitmap(workingSource, adj)
                        Return ToAvaloniaBitmap(processed)
                    End Using
                Finally
                    workingSource.Dispose()
                End Try
            End If

            SyncLock _baseCacheLock
                Dim baseBitmap = GetOrComputeBaseLocked(workingSource, adj)
                Dim annotated = ApplyAnnotations(baseBitmap, adj)
                Try
                    Return ToAvaloniaBitmap(annotated)
                Finally
                    If Not Object.ReferenceEquals(annotated, baseBitmap) Then annotated.Dispose()
                End Try
            End SyncLock
        End Function

        Public Shared Function RenderPreviewSkBitmap(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing Then Return Nothing
            Return ProcessBitmap(source, adj)
        End Function

        ''' <summary>STUFE 5: Klon der warmen Basis (Pixel-Pipeline INKL. committeter Retusche, OHNE
        ''' Objekte) - das ist exakt das "Zielbild" der Retusche-Live-Puffer. Spart den vollen
        ''' Pipeline-Render, wenn der Cache zur aktuellen Einstellung passt; sonst Nothing.</summary>
        Public Shared Function TryCloneBaseCachedBitmap(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing Then Return Nothing
            If Not Monitor.TryEnter(_baseCacheLock, 12) Then Return Nothing
            Try
                Dim key = ComputeBaseKey(adj)
                If Not Object.ReferenceEquals(_baseCacheSourceRef, source) OrElse
                   Not String.Equals(_baseCacheKey, key, StringComparison.Ordinal) OrElse
                   _baseCacheBitmap Is Nothing Then
                    Return Nothing
                End If
                Return CloneBitmap(_baseCacheBitmap)
            Finally
                Monitor.Exit(_baseCacheLock)
            End Try
        End Function

        ''' <summary>STUFE 2: Szenen-Vollrender als SKBitmap UEBER den Base-Cache (GetOrComputeBaseLocked) -
        ''' im Gegensatz zu RenderPreviewSkBitmap/ProcessBitmap, die den Cache UMGEHEN. Ohne das Waermen
        ''' schlagen ALLE nachfolgenden Region-Renders (TryRenderAnnotationsPatchSkOnCachedBase) dauerhaft
        ''' mit cacheMissOrBusy fehl (Log-Befund). Liefert immer ein eigenes Bitmap
        ''' (Aufrufer disposed).</summary>
        Public Shared Function RenderSceneSkCached(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing Then Return Nothing
            SyncLock _baseCacheLock
                Dim baseBitmap = GetOrComputeBaseLocked(source, adj)
                Dim annotated = ApplyAnnotations(baseBitmap, adj)
                ' Ohne sichtbare Annotationen liefert ApplyAnnotations die gecachte Basis selbst zurueck -
                ' dann klonen, sonst wuerde der Aufrufer das Cache-Bitmap disposen. WICHTIG: als Rgba8888
                ' (CloneBitmapForAnnotationComposite), damit die Szene IMMER dasselbe Pixelformat hat wie
                ' der ApplyAnnotations-Ausgang - der Display-Blit kopiert rohe Bytes und wuerde bei
                ' gemischten Formaten Rot/Blau vertauschen.
                If Object.ReferenceEquals(annotated, baseBitmap) Then Return CloneBitmapForAnnotationComposite(baseBitmap)
                Return annotated
            End SyncLock
        End Function

        Public Shared Function CloneForEditing(source As SKBitmap) As SKBitmap
            If source Is Nothing Then Return Nothing
            Return CloneBitmap(source)
        End Function

        ''' <summary>Schneidet ein Rechteck aus <paramref name="source"/> als Avalonia-Bitmap aus.
        ''' <paramref name="rotationDegrees"/> (0/90/180/270) dreht den AUSGESCHNITTENEN Inhalt zusätzlich -
        ''' nötig für das Retusche-Live-Overlay: dessen Bitmap liegt im ungedrehten Arbeitsbild, das Overlay
        ''' aber über dem per Rezept gedrehten Anzeigebild.</summary>
        Public Shared Function RenderBitmapPatch(source As SKBitmap, rect As SKRectI, Optional rotationDegrees As Integer = 0) As Bitmap
            If source Is Nothing Then Return Nothing

            Dim clipped = New SKRectI(Math.Max(0, rect.Left),
                                      Math.Max(0, rect.Top),
                                      Math.Min(source.Width, rect.Right),
                                      Math.Min(source.Height, rect.Bottom))
            If clipped.Width <= 0 OrElse clipped.Height <= 0 Then Return Nothing

            Using patch = New SKBitmap(clipped.Width, clipped.Height, source.ColorType, source.AlphaType)
                Using canvas = New SKCanvas(patch)
                    canvas.Clear(SKColors.Transparent)
                    canvas.DrawBitmap(source,
                                      New SKRect(clipped.Left, clipped.Top, clipped.Right, clipped.Bottom),
                                      New SKRect(0, 0, clipped.Width, clipped.Height))
                End Using
                Dim q = (((rotationDegrees \ 90) Mod 4) + 4) Mod 4
                If q = 0 Then Return ToAvaloniaBitmap(patch)
                Using rotated = RotateBitmapQuarter(patch, q)
                    Return ToAvaloniaBitmap(If(rotated, patch))
                End Using
            End Using
        End Function

        Public Shared Function RenderChangedBitmapPatch(source As SKBitmap,
                                                        baseline As SKBitmap,
                                                        rect As SKRectI,
                                                        Optional tolerance As Integer = 1) As Bitmap
            If source Is Nothing Then Return Nothing

            Dim clipped = New SKRectI(Math.Max(0, rect.Left),
                                      Math.Max(0, rect.Top),
                                      Math.Min(source.Width, rect.Right),
                                      Math.Min(source.Height, rect.Bottom))
            If clipped.Width <= 0 OrElse clipped.Height <= 0 Then Return Nothing

            Using patch = New SKBitmap(clipped.Width, clipped.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(patch)
                    canvas.Clear(SKColors.Transparent)
                    canvas.DrawBitmap(source,
                                      New SKRect(clipped.Left, clipped.Top, clipped.Right, clipped.Bottom),
                                      New SKRect(0, 0, clipped.Width, clipped.Height))
                End Using

                If baseline IsNot Nothing AndAlso baseline.Width = source.Width AndAlso baseline.Height = source.Height Then
                    Using baselinePatch = New SKBitmap(clipped.Width, clipped.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                        Using canvas = New SKCanvas(baselinePatch)
                            canvas.Clear(SKColors.Transparent)
                            canvas.DrawBitmap(baseline,
                                              New SKRect(clipped.Left, clipped.Top, clipped.Right, clipped.Bottom),
                                              New SKRect(0, 0, clipped.Width, clipped.Height))
                        End Using

                        Dim tol = Math.Max(0, tolerance)
                        Dim rowBytes = patch.RowBytes
                        Dim activeBytes = clipped.Width * 4
                        Dim patchRow = ArrayPool(Of Byte).Shared.Rent(rowBytes)
                        Dim baselineRow = ArrayPool(Of Byte).Shared.Rent(rowBytes)
                        Try
                            ' Nur je eine Zeile statt zwei kompletter Patch-Kopien puffern. Dieser
                            ' Pfad laeuft waehrend Verwischen und Stempeln bis zu etwa 40-mal/s und
                            ' das Patch-Rechteck waechst ueber den ganzen Zug; flaechenbreite Byte-
                            ' Arrays verursachten deshalb vermeidbare LOH-/GC-Spitzen.
                            Dim patchPixels = patch.GetPixels()
                            Dim baselinePixels = baselinePatch.GetPixels()
                            For y = 0 To clipped.Height - 1
                                Dim nativeOffset = y * rowBytes
                                Marshal.Copy(IntPtr.Add(patchPixels, nativeOffset), patchRow, 0, rowBytes)
                                Marshal.Copy(IntPtr.Add(baselinePixels, nativeOffset), baselineRow, 0, rowBytes)
                                For offset = 0 To activeBytes - 4 Step 4
                                    Dim delta = Math.Abs(CInt(patchRow(offset)) - CInt(baselineRow(offset))) +
                                                Math.Abs(CInt(patchRow(offset + 1)) - CInt(baselineRow(offset + 1))) +
                                                Math.Abs(CInt(patchRow(offset + 2)) - CInt(baselineRow(offset + 2))) +
                                                Math.Abs(CInt(patchRow(offset + 3)) - CInt(baselineRow(offset + 3)))
                                    If delta <= tol Then
                                        patchRow(offset) = 0
                                        patchRow(offset + 1) = 0
                                        patchRow(offset + 2) = 0
                                        patchRow(offset + 3) = 0
                                    End If
                                Next
                                Marshal.Copy(patchRow, 0, IntPtr.Add(patchPixels, nativeOffset), rowBytes)
                            Next
                        Finally
                            ArrayPool(Of Byte).Shared.Return(patchRow)
                            ArrayPool(Of Byte).Shared.Return(baselineRow)
                        End Try
                    End Using
                End If

                Return ToAvaloniaBitmap(patch)
            End Using
        End Function

        ''' <summary>90°-Schritt-Drehung eines SKBitmap im Uhrzeigersinn (gleiche Transform wie
        ''' ApplyGeometryTransforms). q: 1=90°, 2=180°, 3=270°. Nothing bei Fehler.</summary>
        Friend Shared Function RotateBitmapQuarter(src As SKBitmap, q As Integer) As SKBitmap
            Dim n = ((q Mod 4) + 4) Mod 4
            If n = 0 OrElse src Is Nothing Then Return Nothing
            Dim swap = (n = 1 OrElse n = 3)
            Dim rw = If(swap, src.Height, src.Width)
            Dim rh = If(swap, src.Width, src.Height)
            Dim rotated As SKBitmap = Nothing
            Try
                rotated = New SKBitmap(New SKImageInfo(rw, rh, src.ColorType, src.AlphaType))
                Using canvas = New SKCanvas(rotated)
                    canvas.Clear(SKColors.Transparent)
                    Select Case n
                        Case 1
                            canvas.Translate(rw, 0) : canvas.RotateDegrees(90)
                        Case 2
                            canvas.Translate(rw, rh) : canvas.RotateDegrees(180)
                        Case 3
                            canvas.Translate(0, rh) : canvas.RotateDegrees(270)
                    End Select
                    canvas.DrawBitmap(src, 0, 0)
                End Using
                Return rotated
            Catch
                rotated?.Dispose()
                Return Nothing
            End Try
        End Function

        Public Shared Function RenderRetouchMaskPatch(spots As IEnumerable(Of RetouchSpot),
                                                      rect As SKRectI,
                                                      bitmapWidth As Integer,
                                                      bitmapHeight As Integer,
                                                      sourceWidthPixels As Integer,
                                                      sourceHeightPixels As Integer,
                                                      Optional rotationDegrees As Integer = 0) As Bitmap
            If spots Is Nothing OrElse bitmapWidth <= 0 OrElse bitmapHeight <= 0 Then Return Nothing

            Dim clipped = New SKRectI(Math.Max(0, rect.Left),
                                      Math.Max(0, rect.Top),
                                      Math.Min(bitmapWidth, rect.Right),
                                      Math.Min(bitmapHeight, rect.Bottom))
            If clipped.Width <= 0 OrElse clipped.Height <= 0 Then Return Nothing

            Dim scaleX As Single = 1.0F
            Dim scaleY As Single = 1.0F
            If sourceWidthPixels > 0 AndAlso sourceHeightPixels > 0 Then
                scaleX = bitmapWidth / CSng(sourceWidthPixels)
                scaleY = bitmapHeight / CSng(sourceHeightPixels)
            End If
            Dim radiusScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))

            Using patch = New SKBitmap(clipped.Width, clipped.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(patch)
                    canvas.Clear(SKColors.Transparent)
                    canvas.Translate(-clipped.Left, -clipped.Top)
                    Using paint = New SKPaint With {
                        .Color = New SKColor(255, 136, 0, 96),
                        .Style = SKPaintStyle.Fill,
                        .IsAntialias = True
                    }
                        For Each spot In spots
                            If spot Is Nothing Then Continue For
                            Dim cx = Clamp(spot.XPixels * scaleX, 0, bitmapWidth)
                            Dim cy = Clamp(spot.YPixels * scaleY, 0, bitmapHeight)
                            Dim radius = Math.Max(1.0F, spot.RadiusPixels * radiusScale)
                            canvas.DrawCircle(cx, cy, radius, paint)
                        Next
                    End Using
                End Using
                Dim q = (((rotationDegrees \ 90) Mod 4) + 4) Mod 4
                If q = 0 Then Return ToAvaloniaBitmap(patch)
                Using rotated = RotateBitmapQuarter(patch, q)
                    Return ToAvaloniaBitmap(If(rotated, patch))
                End Using
            End Using
        End Function

        ''' <summary>Fuegt einen einzelnen Reparaturpunkt in eine persistente Live-Maske ein.
        ''' Der Editor kann dadurch nur dessen kleine Umgebung zur Anzeige kopieren, statt die
        ''' gesamte bisherige Zugspur bei jeder Mausbewegung erneut zu rastern.</summary>
        Friend Shared Sub DrawRetouchMaskSpot(target As SKBitmap, spot As RetouchSpot,
                                              sourceWidthPixels As Integer, sourceHeightPixels As Integer)
            If target Is Nothing OrElse spot Is Nothing OrElse sourceWidthPixels <= 0 OrElse sourceHeightPixels <= 0 Then Return
            Dim scaleX = target.Width / CSng(sourceWidthPixels)
            Dim scaleY = target.Height / CSng(sourceHeightPixels)
            Dim radiusScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))
            Dim cx = Clamp(spot.XPixels * scaleX, 0, target.Width)
            Dim cy = Clamp(spot.YPixels * scaleY, 0, target.Height)
            Dim radius = Math.Max(1.0F, spot.RadiusPixels * radiusScale)
            Using canvas As New SKCanvas(target)
                Using paint As New SKPaint With {.Color = New SKColor(255, 136, 0, 96), .Style = SKPaintStyle.Fill, .IsAntialias = True}
                    canvas.DrawCircle(cx, cy, radius, paint)
                End Using
            End Using
        End Sub

        ''' Schneller, UI-Thread-tauglicher Pfad, der NUR die Annotationen auf ein bereits
        ''' gecachtes Base-Bitmap neu komposiert (kein Neuberechnen der teuren Anpassungs-Pipeline).
        ''' Wird genutzt, um beim (De-)Selektieren eines Text-/Wasserzeichen-Objekts das Ausblenden
        ''' im gebackenen Vorschaubild synchron mit dem Anzeigen des Live-Overlays zu koppeln, statt
        ''' auf einen asynchronen Task.Run-Render zu warten (siehe EditorViewModel.TryRenderAnnotationOverlaySync).
        ''' Nutzt Monitor.TryEnter mit kurzem Timeout statt eines blockierenden SyncLock, damit der
        ''' UI-Thread nie hängt, falls ein Hintergrund-Task den Cache gerade neu berechnet - in dem
        ''' Fall (oder bei kaltem Cache) liefert die Funktion Nothing, der Aufrufer fällt dann auf
        ''' den bestehenden asynchronen Renderpfad zurück.
        Public Shared Function TryRenderAnnotationsOnCachedBase(source As SKBitmap, adj As ImageAdjustments) As Bitmap
            If source Is Nothing Then Return Nothing

            If Not Monitor.TryEnter(_baseCacheLock, 12) Then Return Nothing
            Try
                Dim key = ComputeBaseKey(adj)
                If Not Object.ReferenceEquals(_baseCacheSourceRef, source) OrElse
                   Not String.Equals(_baseCacheKey, key, StringComparison.Ordinal) OrElse
                   _baseCacheBitmap Is Nothing Then
                    Return Nothing
                End If

                Dim annotated = ApplyAnnotations(_baseCacheBitmap, adj)
                Try
                    Return ToAvaloniaBitmap(annotated)
                Finally
                    If Not Object.ReferenceEquals(annotated, _baseCacheBitmap) Then annotated.Dispose()
                End Try
            Finally
                Monitor.Exit(_baseCacheLock)
            End Try
        End Function

        ''' <summary>
        ''' Rendert nur einen Bildausschnitt aus dem gecachten Basisbild plus aktueller Annotationen.
        ''' Der Pfad vermeidet beim Verschieben/Ändern von Objekten den teuren Vollbild-Composite.
        ''' </summary>
        Public Shared Function TryRenderAnnotationsPatchOnCachedBase(source As SKBitmap, adj As ImageAdjustments, dirtyRect As SKRectI) As Bitmap
            Dim clampedRect As SKRectI
            Dim patch = TryRenderAnnotationsPatchSkOnCachedBase(source, adj, dirtyRect, clampedRect)
            If patch Is Nothing Then Return Nothing
            Using patch
                Return ToAvaloniaBitmap(patch)
            End Using
        End Function

        ''' <summary>SK-Kern des Region-Renderers (Basis + Striche + Objekte im Dirty-Rect). Liefert das
        ''' Patch-SKBitmap (Aufrufer disposed) und per clampedRect die tatsächlich gerenderte Region -
        ''' der Szenen-Renderer zeichnet das Patch dort in die persistente Szene. Nothing bei kaltem/
        ''' gesperrtem Base-Cache.</summary>
        Public Shared Function TryRenderAnnotationsPatchSkOnCachedBase(source As SKBitmap, adj As ImageAdjustments, dirtyRect As SKRectI,
                                                                       ByRef clampedRect As SKRectI) As SKBitmap
            Dim ignored = AnnotationPatchCacheState.Unknown
            Dim ignoredDrawn = 0
            Return TryRenderAnnotationsPatchSkOnCachedBase(source, adj, dirtyRect, clampedRect, ignored, ignoredDrawn)
        End Function

        Public Shared Function TryRenderAnnotationsPatchSkOnCachedBase(source As SKBitmap, adj As ImageAdjustments, dirtyRect As SKRectI,
                                                                       ByRef clampedRect As SKRectI,
                                                                       ByRef cacheState As AnnotationPatchCacheState) As SKBitmap
            Dim ignoredDrawn = 0
            Return TryRenderAnnotationsPatchSkOnCachedBase(source, adj, dirtyRect, clampedRect, cacheState, ignoredDrawn)
        End Function

        ''' <param name="drawnObjects">Messpunkt fuer den Kompositor-Umbau: wie viele Objekte dieser
        ''' Patch WIRKLICH gezeichnet hat (nach Sichtbarkeits- und Clip-Pruefung).</param>
        Public Shared Function TryRenderAnnotationsPatchSkOnCachedBase(source As SKBitmap, adj As ImageAdjustments, dirtyRect As SKRectI,
                                                                       ByRef clampedRect As SKRectI,
                                                                       ByRef cacheState As AnnotationPatchCacheState,
                                                                       ByRef drawnObjects As Integer) As SKBitmap
            clampedRect = SKRectI.Empty
            cacheState = AnnotationPatchCacheState.Unknown
            drawnObjects = 0
            If source Is Nothing OrElse dirtyRect.IsEmpty Then
                cacheState = AnnotationPatchCacheState.Stale
                Return Nothing
            End If

            If Not Monitor.TryEnter(_baseCacheLock, 12) Then
                cacheState = AnnotationPatchCacheState.Busy
                Return Nothing
            End If
            Try
                Dim key = ComputeBaseKey(adj)
                If Not Object.ReferenceEquals(_baseCacheSourceRef, source) OrElse
                   Not String.Equals(_baseCacheKey, key, StringComparison.Ordinal) OrElse
                   _baseCacheBitmap Is Nothing Then
                    cacheState = AnnotationPatchCacheState.Stale
                    Return Nothing
                End If
                cacheState = AnnotationPatchCacheState.Current

                Dim rect = ClampRectToBitmap(dirtyRect, _baseCacheBitmap.Width, _baseCacheBitmap.Height)
                If rect.IsEmpty OrElse rect.Width <= 0 OrElse rect.Height <= 0 Then Return Nothing

                Dim patch = New SKBitmap(rect.Width, rect.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(patch)
                    canvas.Clear(SKColors.Transparent)
                    ' ARBEITSBILD-Umbau (Stufe D): Pinsel-/Radiererstriche sind ins Arbeitsbild
                    ' eingebacken und stecken damit bereits im Base-Cache-Bitmap - der Patch
                    ' schneidet nur noch Basis-Slice + Z-Order-Objekte (der RasterCompositeCache
                    ' und sein Strich-Stamp sind entfallen).
                    If adj Is Nothing OrElse Not adj.BackgroundHidden Then
                        canvas.DrawBitmap(_baseCacheBitmap,
                                          New SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                                          New SKRect(0, 0, rect.Width, rect.Height))
                    End If
                    If adj IsNot Nothing AndAlso adj.Annotations IsNot Nothing AndAlso adj.Annotations.Count > 0 Then
                        drawnObjects = DrawAnnotationsOnCanvas(canvas, adj, _baseCacheBitmap.Width, _baseCacheBitmap.Height,
                                                               rect.Left, rect.Top, rect.Width, rect.Height, adj.Annotations)
                    End If
                End Using
                clampedRect = rect
                Return patch
            Finally
                Monitor.Exit(_baseCacheLock)
            End Try
        End Function

        ''' <summary>Dirty-Rect eines Objekts im ZIEL-Koordinatenraum (sourceWidth/Height, z.B. die
        ''' gedeckelte Preview). Die Annotation ist in BASIS-Bildpixeln gespeichert; baseWidth/baseHeight
        ''' geben diesen Basisraum an, damit hier dieselbe Skalierung greift wie beim Zeichnen
        ''' (DrawAnnotationsOnCanvas via ScaleAnnotationForSource). 0/0 oder gleiche Masse = keine
        ''' Skalierung (historisches Verhalten, als preview==base galt).</summary>
        Public Shared Function ComputeAnnotationDirtyRect(sourceWidth As Integer, sourceHeight As Integer, annotation As ImageAnnotation,
                                                          Optional baseWidth As Integer = 0, Optional baseHeight As Integer = 0) As SKRectI
            If annotation Is Nothing OrElse sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return SKRectI.Empty
            If baseWidth > 0 AndAlso baseHeight > 0 AndAlso (baseWidth <> sourceWidth OrElse baseHeight <> sourceHeight) Then
                annotation = ScaleAnnotationForSource(annotation, sourceWidth / CSng(baseWidth), sourceHeight / CSng(baseHeight))
            End If
            Return ComputeAnnotationDirtyRectCore(sourceWidth, sourceHeight, annotation)
        End Function

        Public Shared Function ComputeAnnotationDirtyRect(sourceWidth As Integer, sourceHeight As Integer, annotation As ImageAnnotation,
                                                          adj As ImageAdjustments) As SKRectI
            If annotation Is Nothing OrElse sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return SKRectI.Empty
            annotation = TransformAnnotationForGeometry(annotation, adj, sourceWidth, sourceHeight)
            Return ComputeAnnotationDirtyRectCore(sourceWidth, sourceHeight, annotation)
        End Function

        ''' <summary>
        ''' Wo liegt eine MASKE in der Szene? Gerechnet wird über dieselbe Geometriekette wie bei
        ''' Objekten - dafür wandert das Maskenrechteck als Pseudo-Objekt hindurch, statt die Kette
        ''' (Crop, Vierteldrehung, Spiegelung, Skalierung) ein zweites Mal nachzubilden. Eine zweite
        ''' Formel wäre genau die Sorte Abweichung, die im Koordinaten-Audit steht.
        '''
        ''' Gebraucht wird das, um zu entscheiden, ob eine im Objektstapel hängende Korrektur eine
        ''' bestimmte Region überhaupt berührt (sonst darf der schnelle Region-Patch laufen).
        ''' Die weiche Kante wird großzügig mitgerechnet.
        ''' </summary>
        Public Shared Function ComputeMaskDirtyRect(sourceWidth As Integer, sourceHeight As Integer,
                                                    mask As ImageMask, adj As ImageAdjustments) As SKRectI
            If mask Is Nothing OrElse sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return SKRectI.Empty
            Dim width = mask.Right - mask.Left
            Dim height = mask.Bottom - mask.Top
            If width <= 0 OrElse height <= 0 Then Return SKRectI.Empty
            Dim border = Math.Max(0.0F, mask.FeatherPixels) + 2.0F
            Dim pseudo As New ImageAnnotation With {
                .Kind = "Rectangle",
                .XPixels = mask.Left - border,
                .YPixels = mask.Top - border,
                .WidthPixels = width + 2.0F * border,
                .HeightPixels = height + 2.0F * border
            }
            Return ComputeAnnotationDirtyRect(sourceWidth, sourceHeight, pseudo, adj)
        End Function

        Private Shared Function ComputeAnnotationDirtyRectCore(sourceWidth As Integer, sourceHeight As Integer, annotation As ImageAnnotation) As SKRectI
            If annotation Is Nothing OrElse sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return SKRectI.Empty
            Dim kind = If(annotation.Kind, "Text").Trim().ToLowerInvariant()
            If IsPaintKind(kind) Then
                Dim bounds As SKRect? = Nothing
                If annotation.Strokes IsNot Nothing Then
                    For Each stroke In annotation.Strokes
                        If stroke Is Nothing OrElse stroke.Points Is Nothing Then Continue For
                        For Each pt In stroke.Points
                            Dim pRect = New SKRect(CSng(pt.X), CSng(pt.Y), CSng(pt.X), CSng(pt.Y))
                            If bounds.HasValue Then
                                Dim b = bounds.Value
                                b.Union(pRect)
                                bounds = b
                            Else
                                bounds = pRect
                            End If
                        Next
                    Next
                End If
                If Not bounds.HasValue Then Return SKRectI.Empty
                ' Muss die RENDER-Reichweite von DrawBrushStrokeWithEffects spiegeln:
                ' dort pad = strokeWidth + |shadowDx| + |shadowDy| + 3*max(shadowSigma, glowSigma) + 4.
                ' Der alte Pauschalwert Max(4, strokeWidth*2) war kleiner als die Glow-Reichweite
                ' (glowSigma bis 0,8*strokeWidth => 3 Sigma = 2,4*strokeWidth ZUSAETZLICH zum Strich) -
                ' beim Patch-Render blieben abgeschnittene/veraltete Glow-Saeume am Patchrand stehen.
                ' Formen/Text rechnen ihre Effektraender unten laengst ein, nur Paint-Kinds nicht.
                Dim paintObjSize = Math.Max(1.0F, annotation.StrokeWidth)
                Dim paintShadowDx = If(annotation.ShadowEnabled, Clamp(annotation.ShadowOffsetXPercent, -100, 100) / 100.0F * paintObjSize, 0.0F)
                Dim paintShadowDy = If(annotation.ShadowEnabled, Clamp(annotation.ShadowOffsetYPercent, -100, 100) / 100.0F * paintObjSize, 0.0F)
                Dim paintShadowSigma = If(annotation.ShadowEnabled, Clamp(annotation.ShadowBlur, 0, 100) / 100.0F * paintObjSize * ShadowBlurSigmaFactor, 0.0F)
                Dim paintGlowSigma = If(annotation.GlowEnabled, Clamp(annotation.GlowBlur, 0, 100) / 100.0F * paintObjSize * 0.8F, 0.0F)
                Dim paintPad = Math.Max(Math.Max(4.0F, annotation.StrokeWidth * 2.0F),
                                        paintObjSize + Math.Abs(paintShadowDx) + Math.Abs(paintShadowDy) +
                                        Math.Max(paintShadowSigma, paintGlowSigma) * 3.0F + 4.0F)
                Return ClampRectToBitmap(InflateToRectI(bounds.Value, paintPad), sourceWidth, sourceHeight)
            End If

            Dim rect = ComputeAnnotationRect(sourceWidth, sourceHeight, kind, annotation)
            ' FREIER PFAD: seine Punkte duerfen ueber das Objektrechteck hinausreichen (ein Griff
            ' traegt die Kurve nach aussen, und ein gezogener Stuetzpunkt wird bewusst nicht
            ' geklemmt). Das Rechteck allein waere dann zu klein, und der Teil ausserhalb bliebe beim
            ' Auffrischen der Anzeige stehen - er sah aus, als waere er abgeschnitten.
            If Not String.IsNullOrWhiteSpace(annotation.PathPoints) Then
                Dim nodes = ParsePathPoints(annotation.PathPoints)
                If nodes.Count > 0 Then
                    Dim minX = Single.MaxValue, minY = Single.MaxValue
                    Dim maxX = Single.MinValue, maxY = Single.MinValue
                    For Each n In nodes
                        For Each p In {n.Anchor, n.HandleIn, n.HandleOut}
                            minX = Math.Min(minX, p.X) : maxX = Math.Max(maxX, p.X)
                            minY = Math.Min(minY, p.Y) : maxY = Math.Max(maxY, p.Y)
                        Next
                    Next
                    If maxX > minX OrElse maxY > minY Then
                        ' Die Grenzen sind schon sortiert (Minimum vor Maximum), es braucht also kein
                        ' Geraderuecken - eine gespiegelte Ebene steckt in der Zeichnung, nicht hier.
                        Dim pathRect = New SKRect(rect.Left + minX / 100.0F * rect.Width,
                                                  rect.Top + minY / 100.0F * rect.Height,
                                                  rect.Left + maxX / 100.0F * rect.Width,
                                                  rect.Top + maxY / 100.0F * rect.Height)
                        rect.Union(pathRect)
                    End If
                End If
            End If
            rect = RotationBounds(rect, annotation.RotationDegrees)

            Dim extent = Math.Max(rect.Width, rect.Height)
            Dim effectPad = Math.Max(8.0F, annotation.StrokeWidth * 3.0F)
            ' Text an Pfad: die Glyphen stehen SENKRECHT zum Pfad und ragen bis zu einer
            ' Schrifthoehe ueber das Layout-Rechteck hinaus (Baseline liegt AUF dem Pfad).
            If Not String.IsNullOrWhiteSpace(annotation.TextPathKind) Then
                ' EFFEKTIVE Groesse: der Kreis-Fit kann die Schrift ueber FontSizePixels hinaus wachsen lassen.
                effectPad = Math.Max(effectPad, annotation.FontSizePixels * ComputeTextPathFitRatio(annotation) * 1.2F)
            End If
            If annotation.ShadowEnabled Then
                Dim objSize = Math.Max(1.0F, Math.Min(rect.Width, rect.Height))
                Dim shadowBlurPx = objSize * Clamp(annotation.ShadowBlur, 0, 100) / 100.0F * ShadowBlurSigmaFactor
                Dim shadowOffset = Math.Max(Math.Abs(objSize * annotation.ShadowOffsetXPercent / 100.0F),
                                            Math.Abs(objSize * annotation.ShadowOffsetYPercent / 100.0F))
                Dim shadowGrow = Math.Max(0.0F, Clamp(annotation.ShadowSizePercent, 10, 400) / 100.0F - 1.0F) * objSize * 0.5F
                effectPad = Math.Max(effectPad, shadowBlurPx * 3.0F + shadowOffset + shadowGrow + 4.0F)
            End If
            If annotation.GlowEnabled Then
                Dim objSize = Math.Max(1.0F, Math.Min(rect.Width, rect.Height))
                Dim glowReach = objSize * Clamp(annotation.GlowBlur, 0, 100) / 100.0F * 1.5F
                Dim glowDilate = Math.Max(0, CInt(Math.Round(glowReach * 0.5F)))
                Dim glowSigma = Math.Max(0.1F, glowReach * 0.17F)
                effectPad = Math.Max(effectPad, glowDilate + 3.0F * glowSigma + 4.0F)
            End If
            If HasObjectAdjustments(annotation) OrElse Not IsNormalAnnotationBlendMode(annotation.BlendMode) Then
                effectPad = Math.Max(effectPad, 24.0F)
            End If

            Return ClampRectToBitmap(InflateToRectI(rect, effectPad), sourceWidth, sourceHeight)
        End Function

        Public Shared Function ComputePixelPaintDirtyRect(sourceWidth As Integer,
                                                          sourceHeight As Integer,
                                                          paintStroke As PixelPaintStroke,
                                                          Optional lastStroke As BrushStroke = Nothing) As SKRectI
            If paintStroke Is Nothing OrElse sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return SKRectI.Empty
            Dim renderStroke = paintStroke.ToRenderAnnotation()
            If lastStroke IsNot Nothing Then renderStroke.Strokes = New List(Of BrushStroke) From {lastStroke}
            Return ComputeAnnotationDirtyRect(sourceWidth, sourceHeight, renderStroke)
        End Function

        ''' <summary>Rechnet ein Rect von einem Koordinatenraum in einen anderen um (z.B. Basis-Bildpixel ->
        ''' gedeckelte Preview). Konservativ gerundet (Floor/Ceiling), damit der Zielbereich nie kleiner wird
        ''' als der Quellbereich - ein zu kleines Dirty-Rect liesse Randpixel veraltet stehen.</summary>
        Public Shared Function ScaleRectBetweenSpaces(rect As SKRectI,
                                                      fromWidth As Integer, fromHeight As Integer,
                                                      toWidth As Integer, toHeight As Integer) As SKRectI
            If rect.IsEmpty OrElse fromWidth <= 0 OrElse fromHeight <= 0 OrElse toWidth <= 0 OrElse toHeight <= 0 Then
                Return SKRectI.Empty
            End If
            If fromWidth = toWidth AndAlso fromHeight = toHeight Then Return rect
            Dim sx = toWidth / CDbl(fromWidth)
            Dim sy = toHeight / CDbl(fromHeight)
            Return New SKRectI(CInt(Math.Floor(rect.Left * sx)),
                               CInt(Math.Floor(rect.Top * sy)),
                               CInt(Math.Ceiling(rect.Right * sx)),
                               CInt(Math.Ceiling(rect.Bottom * sy)))
        End Function

        Public Shared Function UnionRects(a As SKRectI, b As SKRectI) As SKRectI
            If a.IsEmpty Then Return b
            If b.IsEmpty Then Return a
            Return New SKRectI(Math.Min(a.Left, b.Left),
                               Math.Min(a.Top, b.Top),
                               Math.Max(a.Right, b.Right),
                               Math.Max(a.Bottom, b.Bottom))
        End Function

        Private Shared Function InflateToRectI(rect As SKRect, padding As Single) As SKRectI
            Return New SKRectI(CInt(Math.Floor(rect.Left - padding)),
                               CInt(Math.Floor(rect.Top - padding)),
                               CInt(Math.Ceiling(rect.Right + padding)),
                               CInt(Math.Ceiling(rect.Bottom + padding)))
        End Function

        Friend Shared Function ClampRectToBitmap(rect As SKRectI, width As Integer, height As Integer) As SKRectI
            If width <= 0 OrElse height <= 0 OrElse rect.IsEmpty Then Return SKRectI.Empty
            Dim left = Math.Max(0, Math.Min(width, rect.Left))
            Dim top = Math.Max(0, Math.Min(height, rect.Top))
            Dim right = Math.Max(left, Math.Min(width, rect.Right))
            Dim bottom = Math.Max(top, Math.Min(height, rect.Bottom))
            If right <= left OrElse bottom <= top Then Return SKRectI.Empty
            Return New SKRectI(left, top, right, bottom)
        End Function

        Private Shared Function RotationBounds(rect As SKRect, degrees As Single) As SKRect
            If Math.Abs(degrees) < 0.01F Then Return rect
            Dim radians = degrees * Math.PI / 180.0
            Dim cos = Math.Cos(radians)
            Dim sin = Math.Sin(radians)
            Dim cx = rect.MidX
            Dim cy = rect.MidY
            Dim xs = New Single() {rect.Left, rect.Right, rect.Right, rect.Left}
            Dim ys = New Single() {rect.Top, rect.Top, rect.Bottom, rect.Bottom}
            Dim minX As Single = Single.MaxValue
            Dim minY As Single = Single.MaxValue
            Dim maxX As Single = Single.MinValue
            Dim maxY As Single = Single.MinValue
            For i = 0 To 3
                Dim dx = xs(i) - cx
                Dim dy = ys(i) - cy
                Dim x = CSng(cx + dx * cos - dy * sin)
                Dim y = CSng(cy + dx * sin + dy * cos)
                minX = Math.Min(minX, x)
                minY = Math.Min(minY, y)
                maxX = Math.Max(maxX, x)
                maxY = Math.Max(maxY, y)
            Next
            Return New SKRect(minX, minY, maxX, maxY)
        End Function

        ' Obergrenze für die interne Renderauflösung des Auswahl-Overlays. Das Bitmap wird ohnehin
        ' per Stretch="Fill" auf die Bildschirmgröße skaliert, daher genügt eine gedeckelte Auflösung.
        ' Wichtig für die Performance: RenderAnnotationOverlay wird bei JEDER Schatten-/Glow-Slider-
        ' Änderung neu aufgerufen (siehe EditorViewModel.SyncSelectedAnnotation) und das Bitmap wächst
        ' um den (bei großem Blur erheblichen) Effekt-Rand - ohne Deckelung würden mehrere hundert MB
        ' pro Slider-Tick auf dem UI-Thread alloziert.
        Private Const MaxOverlayRenderDim As Single = 720.0F

        ''' Ergebnis von RenderAnnotationOverlay: das Bitmap UND die Lage des Objekts darin (Bitmap-Pixel).
        ''' Die View braucht beides: sie legt das Bitmap per Stretch="Fill" und negativen Margins über die
        ''' Objekt-Border. Die Effekt-Ränder sind in Bitmap-Pixeln bemessen, die Border in Display-Pixeln -
        ''' die View darf die Padding-Formel deshalb nicht nachbauen, sondern rechnet dieses Rechteck um
        ''' (siehe EditorView.ComputeSelectedOverlayImageMargin).
        Public NotInheritable Class AnnotationOverlayRender
            Public Property Image As Bitmap
            Public Property BitmapWidth As Double
            Public Property BitmapHeight As Double
            Public Property ObjectX As Double
            Public Property ObjectY As Double
            Public Property ObjectWidth As Double
            Public Property ObjectHeight As Double
        End Class

        ''' <summary>Ergebnis des Skia-Kerns: die Bitmap (Eigentum geht an den Aufrufer) und die Lage
        ''' des Objekts darin, in Bitmap-Pixeln. Grundlage fuer das Auswahl-Overlay (Avalonia-Fassung
        ''' unten) UND fuer den Objekt-Bitmap-Cache des Kompositor-Umbaus - beide muessen dieselbe
        ''' Zeichnung sehen, eine zweite Formel liefe auseinander.</summary>
        Public NotInheritable Class AnnotationOverlaySkRender
            Public Property Bitmap As SKBitmap
            Public Property ObjectX As Integer
            Public Property ObjectY As Integer
            Public Property ObjectWidth As Integer
            Public Property ObjectHeight As Integer
        End Class

        ''' <summary>Zeichnet das selektierte Objekt so, wie es im gebackenen Bild aussieht - Silhouette
        ''' mit Schatten und Glühen, darüber das Objekt selbst. Die View legt das Bitmap deckungsgleich
        ''' über die Objekt-Border (siehe AnnotationOverlayRender).</summary>
        Public Shared Function RenderAnnotationOverlay(annotation As ImageAnnotation, pixelWidth As Integer, pixelHeight As Integer) As AnnotationOverlayRender
            Dim sk = RenderAnnotationOverlaySk(annotation, pixelWidth, pixelHeight)
            If sk Is Nothing Then Return Nothing
            Using sk.Bitmap
                Return New AnnotationOverlayRender With {
                    .Image = ToAvaloniaBitmap(sk.Bitmap),
                    .BitmapWidth = sk.Bitmap.Width,
                    .BitmapHeight = sk.Bitmap.Height,
                    .ObjectX = sk.ObjectX,
                    .ObjectY = sk.ObjectY,
                    .ObjectWidth = sk.ObjectWidth,
                    .ObjectHeight = sk.ObjectHeight
                }
            End Using
        End Function

        ''' <summary>Der Skia-Kern: Objekt inkl. Effekten und eigenen Pixel-Anpassungen, Drehung bewusst
        ''' NICHT eingerechnet (die legt der Anzeiger als Transformation darueber), Lage nicht enthalten.
        ''' Genau die Eigenschaften, die den Objekt-Bitmap-Cache tragen.
        '''
        ''' <paramref name="maxRenderDimension"/>: Deckel der internen Aufloesung. Der GHOST nimmt die
        ''' 720 (er wird ohnehin auf die Bildschirmgroesse gestreckt und bei jedem Effektregler-Tick
        ''' neu gerendert); der Objekt-Bitmap-Cache rendert UNGEDECKELT in Szenenaufloesung - das
        ''' komponierte Bild muss pixelgleich zur gebackenen Qualitaet sein.</summary>
        Public Shared Function RenderAnnotationOverlaySk(annotation As ImageAnnotation, pixelWidth As Integer, pixelHeight As Integer,
                                                         Optional maxRenderDimension As Single = MaxOverlayRenderDim) As AnnotationOverlaySkRender
            If annotation Is Nothing Then Return Nothing

            Dim renderAnnotation = annotation.Clone()
            renderAnnotation.RotationDegrees = 0

            ' Interne Objektauflösung (gedeckelt, Seitenverhältnis erhalten). Die Ränder sind damit in
            ' Bitmap-Pixeln bemessen, nicht in Display-Pixeln; die View rechnet sie über das zurückgegebene
            ' Objekt-Rechteck um, statt die Formel nachzubauen.
            Dim requestedLongest = CSng(Math.Max(1, Math.Max(pixelWidth, pixelHeight)))
            Dim cap = If(maxRenderDimension > 0, maxRenderDimension, MaxOverlayRenderDim)
            Dim renderScale = If(requestedLongest > cap, cap / requestedLongest, 1.0F)
            Dim objW = Math.Max(1, CInt(Math.Round(Math.Max(1, pixelWidth) * renderScale)))
            Dim objH = Math.Max(1, CInt(Math.Round(Math.Max(1, pixelHeight) * renderScale)))

            ' Schriftgrad und Konturbreite stehen in Bildpixeln und müssen mit dem Objekt-Rechteck
            ' schrumpfen - am Klon selbst, nicht nur in lokalen Variablen: DrawAnnotationShape und
            ' DrawAnnotationEffects lesen für Text und Kontur direkt aus der Annotation weiter. Beim
            ' Backen erledigt ScaleAnnotationForSource dasselbe.
            renderAnnotation.FontSizePixels *= renderScale
            renderAnnotation.StrokeWidth *= renderScale

            Dim objSize = CSng(Math.Max(1, Math.Min(objW, objH)))
            ' Schattengröße >100% lässt den (um seine Mitte skalierten) Schatten übers Objekt hinauswachsen;
            ' der Wachstumsrand wird über die größere Objektkante bemessen, damit auf keiner Achse abgeschnitten wird.
            Dim shadowGrow = If(renderAnnotation.ShadowEnabled, Math.Max(objW, objH) * Math.Max(0.0F, Clamp(renderAnnotation.ShadowSizePercent, 10, 400) / 100.0F - 1.0F) * 0.5F, 0.0F)
            ' Faktor 1.7 deckt die Glow-Reichweite (Dilate + Blur, siehe DrawAnnotationEffects: ~1.5x objSize
            ' bei Maximalwert) mit etwas Reserve ab.
            Dim glowPad = If(renderAnnotation.GlowEnabled, objSize * Clamp(renderAnnotation.GlowBlur, 0, 100) / 100.0F * 1.7F, 0.0F)
            Dim shadowPad = If(renderAnnotation.ShadowEnabled, objSize * Clamp(renderAnnotation.ShadowBlur, 0, 100) / 100.0F * ShadowBlurSigmaFactor * 3.0F + shadowGrow, 0.0F)
            Dim offsetX = If(renderAnnotation.ShadowEnabled, objSize * renderAnnotation.ShadowOffsetXPercent / 100.0F, 0.0F)
            Dim offsetY = If(renderAnnotation.ShadowEnabled, objSize * renderAnnotation.ShadowOffsetYPercent / 100.0F, 0.0F)
            Dim effectPad = Math.Max(glowPad, shadowPad)
            If Not String.IsNullOrWhiteSpace(renderAnnotation.TextPathKind) Then
                effectPad = Math.Max(effectPad, renderAnnotation.FontSizePixels * ComputeTextPathFitRatio(renderAnnotation) * 1.2F)
            End If
            ' Auf ganze Pixel aufrunden, damit das Objekt verlustfrei im Bitmap-Raster liegt: die View
            ' skaliert genau dieses Rechteck auf die Border, jeder Bruchteil würde das Objekt verzerren.
            Dim leftPad = CInt(Math.Ceiling(4.0F + effectPad + Math.Max(0.0F, -offsetX)))
            Dim rightPad = CInt(Math.Ceiling(4.0F + effectPad + Math.Max(0.0F, offsetX)))
            Dim topPad = CInt(Math.Ceiling(4.0F + effectPad + Math.Max(0.0F, -offsetY)))
            Dim bottomPad = CInt(Math.Ceiling(4.0F + effectPad + Math.Max(0.0F, offsetY)))

            ' Das Bitmap um die Effekt-Ränder VERGRÖSSERN (nicht das Objekt hineinschrumpfen): so wird
            ' der Schatten/Glow nie an der Bitmap-Kante abgeschnitten - im Gegensatz zum gebackenen Bild,
            ' das ins ganze Foto ausbluten kann.
            ' Damit entfällt auch der frühere "Reset auf 2px"-Notausgang, der bei flachen/breiten Objekten
            ' (Effekt-Rand > Objektgröße) die Auswahl-Vorschau komplett von der gebackenen Ansicht abweichen ließ.
            Dim width = objW + leftPad + rightPad
            Dim height = objH + topPad + bottomPad

            Dim rect = SKRect.Create(leftPad, topPad, objW, objH)
            Dim kind = If(renderAnnotation.Kind, "Text").Trim().ToLowerInvariant()
            Dim x = rect.Left
            Dim y = rect.Top
            Dim maxWidth = rect.Width
            Dim fontSize = Math.Max(8.0F, renderAnnotation.FontSizePixels)
            Dim alphaFactor = Clamp(renderAnnotation.Opacity, 0, 100) / 100.0F
            Dim fill = ApplyAlpha(ParseColor(renderAnnotation.FillColor, SKColors.White), alphaFactor)
            Dim stroke = ApplyAlpha(ParseColor(renderAnnotation.StrokeColor, SKColors.Black), alphaFactor)
            ' NULL HEISST KEINE KONTUR - dieselbe Regel wie im Bildrender (DrawAnnotationOnCanvas).
            ' Die Untergrenze steht hier ein ZWEITES Mal, und genau daran zeichnete der Kompositor
            ' weiter eine Haarlinie, nachdem der Bildrender sie schon nicht mehr zog: das markierte
            ' Objekt kommt aus DIESEM Weg (Nutzerbefund 2026-08-08: "beim Pfad bleibt weiterhin eine
            ' Linie zu sehen"). Zwei Stellen, eine Regel - siehe EDITOR_OBJEKTE.md.
            Dim strokeWidth = If(renderAnnotation.StrokeWidth <= 0.0F, 0.0F,
                                 Math.Max(1.0F, renderAnnotation.StrokeWidth))

            ' KEIN Using: das Bitmap gehoert ab hier dem Aufrufer (Overlay-Fassung oder Cache).
            Dim bitmap = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Using canvas = New SKCanvas(bitmap)
                canvas.Clear(SKColors.Transparent)
                ' Die DREHUNG legt die View über eine RenderTransform auf das Overlay (oben deshalb auf 0
                ' gesetzt) - die SPIEGELUNG aber nicht. Ohne sie zeigte das Overlay ein markiertes Objekt
                ' ungespiegelt an, und Spiegeln sah aus, als täte es gar nichts: das gebackene Bild, in
                ' dem die Spiegelung längst drin war, blendet das markierte Objekt ja aus.
                If renderAnnotation.FlipHorizontal OrElse renderAnnotation.FlipVertical Then
                    canvas.Translate(rect.MidX, rect.MidY)
                    canvas.Scale(If(renderAnnotation.FlipHorizontal, -1.0F, 1.0F),
                                 If(renderAnnotation.FlipVertical, -1.0F, 1.0F))
                    canvas.Translate(-rect.MidX, -rect.MidY)
                End If
                If renderAnnotation.ShadowEnabled OrElse renderAnnotation.GlowEnabled Then
                    DrawAnnotationEffects(canvas, kind, renderAnnotation, rect, x, y, maxWidth, fontSize, fill, stroke, strokeWidth, alphaFactor, width, height)
                End If
                DrawAnnotationShape(canvas, kind, renderAnnotation, rect, x, y, maxWidth, fontSize, fill, stroke, strokeWidth, alphaFactor)
            End Using

            If HasObjectAdjustments(renderAnnotation) Then
                Dim objectAdj = renderAnnotation.Adjustments.ExtractPixelAdjustments()
                objectAdj.SourceWidthPixels = width
                objectAdj.SourceHeightPixels = height
                Dim processed = ProcessBitmapBase(bitmap, objectAdj)
                If Not Object.ReferenceEquals(processed, bitmap) Then bitmap.Dispose()
                bitmap = processed
            End If

            Return New AnnotationOverlaySkRender With {
                .Bitmap = bitmap,
                .ObjectX = leftPad,
                .ObjectY = topPad,
                .ObjectWidth = objW,
                .ObjectHeight = objH
            }
        End Function

        ''' DrawWrappedText setzt die Grundlinie der ersten Zeile auf rect.Top + fontSize, die Glyphen-
        ''' Oberkante liegt also um (fontSize - Ascent) unter der Oberkante des Objekt-Rechtecks. Avalonia
        ''' setzt die Glyphen-Oberkante einer TextBox dagegen direkt auf deren Oberkante. Der Rückgabewert
        ''' ist der Versatz, um den die Live-TextBox nach unten geschoben werden muss, damit ihre Glyphen
        ''' dort landen, wo das gebackene Bild sie zeichnet.
        ''' <summary>Zeilenabstand des gebackenen Textes, aus den Metriken der Schrift statt aus einem
        ''' festen Faktor. Genau diesen Abstand benutzt auch Avalonia, wenn an der Live-Textbox kein
        ''' LineHeight gesetzt ist - beides liest dieselben hhea-Werte über SkiaSharp. Ein eigener Faktor
        ''' hier (früher 1.22) ließ mehrzeiligen Text im Editor enger stehen als im Ergebnis; ihn über
        ''' TextBox.LineHeight nachzuziehen verschob dafür die erste Zeile nach unten, weil Avalonia die
        ''' zusätzliche Durchschusshöhe über der Grundlinie verteilt.</summary>
        Private Shared Function GetLineHeight(metrics As SKFontMetrics) As Single
            Return -metrics.Ascent + metrics.Descent + metrics.Leading
        End Function

        ''' <summary>Zeilenabstand in Pixeln für einen Schriftschnitt - für die Größenschätzung des
        ''' Textrechtecks im EditorViewModel.</summary>
        Public Shared Function GetBakedTextLineHeight(fontFamily As String, fontSize As Single) As Double
            If fontSize <= 0 Then Return 0
            Using font = CreateFont(fontFamily, fontSize)
                Return GetLineHeight(font.Metrics)
            End Using
        End Function

        Public Shared Function GetBakedTextTopOffset(fontFamily As String, fontSize As Single) As Double
            If fontSize <= 0 Then Return 0
            Using font = CreateFont(fontFamily, fontSize)
                Return Math.Max(0.0F, fontSize + font.Metrics.Ascent)
            End Using
        End Function

        Public Shared Function TryGetSvgAspectRatio(iconPath As String) As Double
            If String.IsNullOrWhiteSpace(iconPath) Then Return 1.0

            Dim shape = GetShapePath(iconPath)
            If shape Is Nothing OrElse shape.Bounds.Width <= 0 OrElse shape.Bounds.Height <= 0 Then Return 1.0
            Return Math.Max(0.01, shape.Bounds.Width / shape.Bounds.Height)
        End Function

        Public Shared Function ApplyGeometryAdjustments(sourcePath As String, adj As ImageAdjustments) As Bitmap
            Using original = DecodeOriented(sourcePath)
                If original Is Nothing Then Return Nothing

                Dim processed As SKBitmap = CloneBitmap(original)
                processed = ReplaceBitmap(processed, ApplyImageWarp(processed, adj))
                processed = ReplaceBitmap(processed, ApplyCrop(processed, adj))
                processed = ReplaceBitmap(processed, ApplyGeometryTransforms(processed, adj))
                processed = ReplaceBitmap(processed, ApplyStraighten(processed, adj))
            processed = ReplaceBitmap(processed, ApplyPerspective(processed, adj))
                processed = ReplaceBitmap(processed, ApplyResize(processed, adj))
                processed = ReplaceBitmap(processed, ApplyCanvasResize(processed, adj))
                processed = ReplaceBitmap(processed, ApplyDocumentBackground(processed, adj))

                Using processedBitmap = processed
                    Return ToAvaloniaBitmap(processedBitmap)
                End Using
            End Using
        End Function

        Public Shared Function ApplyGeometryAdjustments(source As SKBitmap, adj As ImageAdjustments) As Bitmap
            Return ApplyGeometryAdjustments(source, adj, 0)
        End Function

        Public Shared Function ApplyGeometryAdjustments(source As SKBitmap, adj As ImageAdjustments, maxDimension As Integer) As Bitmap
            If source Is Nothing Then Return Nothing

            Dim workingSource = CreatePreviewWorkingBitmap(source, maxDimension)
            If workingSource Is Nothing Then Return Nothing

            Dim processed As SKBitmap = CloneBitmap(workingSource)
            If Not Object.ReferenceEquals(workingSource, source) Then workingSource.Dispose()
            processed = ReplaceBitmap(processed, ApplyImageWarp(processed, adj))
            processed = ReplaceBitmap(processed, ApplyCrop(processed, adj))
            processed = ReplaceBitmap(processed, ApplyGeometryTransforms(processed, adj))
            processed = ReplaceBitmap(processed, ApplyStraighten(processed, adj))
            processed = ReplaceBitmap(processed, ApplyPerspective(processed, adj))
            processed = ReplaceBitmap(processed, ApplyResize(processed, adj))
            processed = ReplaceBitmap(processed, ApplyCanvasResize(processed, adj))
            processed = ReplaceBitmap(processed, ApplyDocumentBackground(processed, adj))

            Using processedBitmap = processed
                Return ToAvaloniaBitmap(processedBitmap)
            End Using
        End Function

        ''' <summary>Geometrie-only-Render als SKBitmap (Vorher-Seite des Zoom-Details): dieselben
        ''' Schritte wie ApplyGeometryAdjustments, aber ohne Avalonia-Konvertierung - der Aufrufer
        ''' extrahiert daraus Viewport-Regionen. Der Aufrufer übernimmt den Besitz.</summary>
        Public Shared Function ApplyGeometryAdjustmentsSk(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing Then Return Nothing

            Dim processed As SKBitmap = CloneBitmap(source)
            processed = ReplaceBitmap(processed, ApplyImageWarp(processed, adj))
            processed = ReplaceBitmap(processed, ApplyCrop(processed, adj))
            processed = ReplaceBitmap(processed, ApplyGeometryTransforms(processed, adj))
            processed = ReplaceBitmap(processed, ApplyStraighten(processed, adj))
            processed = ReplaceBitmap(processed, ApplyPerspective(processed, adj))
            processed = ReplaceBitmap(processed, ApplyResize(processed, adj))
            processed = ReplaceBitmap(processed, ApplyCanvasResize(processed, adj))
            processed = ReplaceBitmap(processed, ApplyDocumentBackground(processed, adj))
            Return processed
        End Function

        ''' <summary>Das Analysebild zum Bild: Histogramm, Waveform oder RGB-Parade, je nach
        ''' Einstellung (siehe AppSettingsService.ScopeMode). Alle drei kosten denselben Decode.</summary>
        Public Shared Function BuildScopeImage(sourcePath As String, width As Integer, height As Integer) As Bitmap
            Try
                Using original = DecodeHistogramSource(sourcePath)
                    If original Is Nothing Then Return Nothing
                    Using scopeImage = RenderScope(original, width, height)
                        Return ToAvaloniaBitmap(scopeImage)
                    End Using
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Waehlt die Darstellung. Eine unbekannte Einstellung landet beim Histogramm,
        ''' und das ist auch der Rueckfall, wenn eine der neuen Darstellungen scheitert: lieber
        ''' das gewohnte Bild als ein leeres Feld.</summary>
        Private Shared Function RenderScope(source As SKBitmap, width As Integer, height As Integer) As SKBitmap
            Dim mode = AppSettingsService.NormalizeScopeMode(AppSettingsService.Load().ScopeMode)
            If mode = "Histogram" Then Return RenderHistogram(source, width, height)
            Try
                Return RenderWaveform(source, width, height, mode = "Parade")
            Catch ex As Exception
                DiagnosticLogService.LogException("Scope.Render", ex)
                Return RenderHistogram(source, width, height)
            End Try
        End Function

        ''' <summary>Histogramme einer FPX beschreiben das gespeicherte Ergebnis und werden deshalb
        ''' direkt aus composite.png gebildet. Die normale Decode-Pipeline kann den FPX-ZIP-Container
        ''' selbst nicht dekodieren und lieferte hier bisher ein leeres Histogramm.</summary>
        Private Shared Function DecodeHistogramSource(sourcePath As String) As SKBitmap
            If FpxService.IsFpx(sourcePath) Then
                Using composite = FpxService.ExtractComposite(sourcePath)
                    If composite Is Nothing Then Return Nothing
                    Return SKBitmap.Decode(composite)
                End Using
            End If
            Return DecodeOriented(sourcePath)
        End Function

        Public Shared Function BuildScopeImage(source As SKBitmap, width As Integer, height As Integer) As Bitmap
            If source Is Nothing Then Return Nothing
            Using scopeImage = RenderScope(source, width, height)
                Return ToAvaloniaBitmap(scopeImage)
            End Using
        End Function

        ''' <summary>Voll aufgelöster Decode für das ARBEITSBILD: öffentlicher
        ''' Zugang zum universellen Decode-Chokepoint (RAW/ICO-Sonderfälle + EXIF-Orientierung).
        ''' Der Aufrufer übernimmt den Besitz des Bitmaps.</summary>
        Public Shared Function DecodeWorkingImage(path As String,
                                                  Optional lensChoice As LensDataService.Wahl = Nothing) As SKBitmap
            Try
                Return DecodeOriented(path, True, lensChoice)
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>ORIENTIERTE Bildmaße (wie DecodeOriented sie liefert) ohne Voll-Decode - zum
        ''' Abgleich, ob ein retouch.png wirklich das voll aufgelöste Arbeitsbild ist. (0,0) wenn
        ''' nicht bestimmbar; der Aufrufer behandelt das als „Prüfung nicht möglich".</summary>
        Public Shared Function GetOrientedImageSize(path As String) As (Width As Integer, Height As Integer)
            Try
                ' RAW mit warmem Entwicklungs-Cache: dessen Maße sind die des echten Decodes.
                ' Kalter Cache -> weiter unten die eingebettete Vorschau (kein Demosaic nur für
                ' eine Größenabfrage; die Maße stimmen bei modernen Kameras überein).
                If RawPreviewService.IsSupportedRaw(path) AndAlso RawDecodeService.IsAvailable Then
                    Dim cached = RawDecodeService.TryGetCachedSize(path)
                    If cached.Width > 0 Then Return cached
                End If
                Dim data As SKData
                If FpxService.IsFpx(path) Then
                    ' Eine FPX ist ein ZIP-Container und kann nicht selbst an SKCodec gehen. Fuer
                    ' Viewer/Infopanel sind die gespeicherten Composite-Masse die richtige schnelle
                    ' Header-Antwort. Ohne diesen Sonderweg fiel der asynchrone Viewer auf seine
                    ' zuvor angezeigten Bildmasse zurueck.
                    Using stream = FpxService.ExtractComposite(path)
                        If stream Is Nothing Then Return (0, 0)
                        data = SKData.Create(stream)
                    End Using
                Else
                    Using stream = OpenSourceStream(path)
                        If stream Is Nothing Then Return (0, 0)
                        data = SKData.Create(stream)
                    End Using
                End If
                If data Is Nothing Then Return (0, 0)
                Using data
                    Using codec = SKCodec.Create(data)
                        If codec Is Nothing Then Return (0, 0)
                        Dim info = codec.Info
                        Select Case codec.EncodedOrigin
                            Case SKEncodedOrigin.LeftTop, SKEncodedOrigin.RightTop, SKEncodedOrigin.RightBottom, SKEncodedOrigin.LeftBottom
                                ' 90°/270°-Orientierungen tauschen Breite und Höhe.
                                Return (info.Height, info.Width)
                            Case Else
                                Return (info.Width, info.Height)
                        End Select
                    End Using
                End Using
            Catch
                Return (0, 0)
            End Try
        End Function

        Public Shared Function LoadPreviewSource(imagePath As String, maxDimension As Integer) As SKBitmap
            Try
                Using original = DecodeOriented(imagePath)
                    If original Is Nothing Then Return Nothing
                    Dim working = CreatePreviewWorkingBitmap(original, maxDimension)
                    If working Is Nothing Then Return Nothing
                    If Object.ReferenceEquals(working, original) Then Return CloneBitmap(original)
                    Return working
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        Public Shared Function CreatePreviewWorkingBitmap(source As SKBitmap, maxDimension As Integer) As SKBitmap
            If source Is Nothing Then Return Nothing

            Dim limit = If(maxDimension > 0, Math.Max(256, maxDimension), Integer.MaxValue)
            Dim longest = Math.Max(source.Width, source.Height)
            If longest <= limit Then Return source

            Dim scale = limit / CDbl(longest)
            Dim width = Math.Max(1, CInt(Math.Round(source.Width * scale)))
            Dim height = Math.Max(1, CInt(Math.Round(source.Height * scale)))
            Dim result = New SKBitmap(width, height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                Using paint = New SKPaint With {.IsAntialias = True}
                    DrawBitmapSampled(canvas, source, New SKRect(0, 0, source.Width, source.Height), New SKRect(0, 0, width, height), SamplingHigh, paint)
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Bildabmessungen aus den Kopfdaten, ohne vollstaendiges Dekodieren. (0,0), wenn
        ''' das Format unbekannt ist - Aufrufer MUESSEN das abfangen, statt mit 0 weiterzurechnen.</summary>
        Public Shared Function GetImageSize(imagePath As String) As (Width As Integer, Height As Integer)
            ' HEIC/HEIF/AVIF kennt SKCodec nicht - dort antwortet libheif aus dem Container.
            If HeifDecodeService.IsSupportedHeif(imagePath) AndAlso HeifDecodeService.IsAvailable Then
                Dim heifSize = HeifDecodeService.TryGetSize(imagePath)
                If heifSize.Width > 0 AndAlso heifSize.Height > 0 Then Return heifSize
            End If
            If TiffPreviewService.IsSupportedTiff(imagePath) Then
                Dim tiffSize = TiffPreviewService.TryGetSize(imagePath)
                If tiffSize.Width > 0 AndAlso tiffSize.Height > 0 Then Return tiffSize
            End If
            Try
                Using codec = SKCodec.Create(imagePath)
                    If codec IsNot Nothing Then
                        Return (codec.Info.Width, codec.Info.Height)
                    End If
                End Using
            Catch
            End Try
            Return (0, 0)
        End Function


        ''' <summary>Grundbreite von Fuß und Schulter der Tonwertkurve, in Anteilen des Tonwertumfangs.</summary>
        Private Const ToneShoulderBase As Single = 0.12F
        Private Const ToneShoulderMax As Single = 0.4F


        Private Shared Function ToneTransfer(x As Single, exposureGain As Single, contrast As Single, brightness As Single) As Single
            ' Belichtung in LINEARLICHT: frueher x*exposureGain im Gamma-Raum - das
            ' staucht die Lichter brutal (gemessen: +50 zog 64->218). Belichtung ist physikalisch ein
            ' linearer Faktor: sRGB dekodieren, multiplizieren, wieder kodieren. Ergebnis darf >1 sein
            ' (Ueberstrahlung), die weiche Schulter (SoftShoulder/rolloff) faengt es danach ab.
            ' Kontrast/Helligkeit bleiben bewusst im Gamma-Raum um 0.5 - dort ist ihre Schulter definiert.
            Dim y As Single
            If exposureGain = 1.0F Then
                y = x
            Else
                y = LinearToSrgb(SrgbToLinear(x) * exposureGain)
            End If
            y = (y - 0.5F) * contrast + 0.5F
            Return y + brightness
        End Function

        ''' <summary>sRGB-Gamma -> Linearlicht (Standard-sRGB-EOTF). Nur fuer die Belichtung; der Rest
        ''' der Kette rechnet weiter im Gamma-Raum.</summary>
        Private Shared Function SrgbToLinear(c As Single) As Single
            If c <= 0.0F Then Return 0.0F
            If c <= 0.04045F Then Return c / 12.92F
            Return CSng(Math.Pow((c + 0.055) / 1.055, 2.4))
        End Function

        ''' <summary>Linearlicht -> sRGB-Gamma. Werte >1 bleiben >1 (Ueberstrahlung), damit die
        ''' Schulter-Logik sie wie bisher abrollen kann.</summary>
        Private Shared Function LinearToSrgb(c As Single) As Single
            If c <= 0.0F Then Return 0.0F
            If c <= 0.0031308F Then Return c * 12.92F
            Return CSng(1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055)
        End Function

        ''' Identisch im mittleren Bereich, exponentiell auslaufend zu Schwarz und Weiß - erreicht die
        ''' Enden nie ganz, sodass in den Lichtern und Tiefen Zeichnung bleibt statt einer Fläche.
        Private Shared Function SoftShoulder(y As Single, toe As Single, shoulder As Single) As Single
            Dim knee = 1.0F - shoulder
            If y > knee Then Return CSng(1.0 - shoulder * Math.Exp(-(y - knee) / shoulder))
            If y < toe Then Return CSng(toe * Math.Exp((y - toe) / toe))
            Return y
        End Function

        Private Shared Function ProcessBitmap(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim processed = ProcessBitmapBase(source, adj)
            Return ReplaceBitmap(processed, ApplyAnnotations(processed, WithAnnotationSourceSpace(adj, source)))
        End Function

        ''' <summary>Stapel- und Export-Aufrufer bauen ihre Anpassungen frisch zusammen und kennen die
        ''' Maße der Quelle nicht - SourceWidthPixels bleibt dort 0. Ohne Bezugsgröße rechnet
        ''' TransformAnnotationForGeometry die Objekte gar nicht um: ein Wasserzeichen behielt seine
        ''' Pixelmaße, obwohl das Bild verkleinert wurde, und wirkte auf dem kleinen Bild riesig.
        ''' Bezug ist hier das dekodierte Bild selbst - genau der Raum, in dem die Pipeline gleich
        ''' arbeitet; eine aus der Datei geschätzte Größe läge bei RAW/PSD/.fpx daneben.</summary>
        Private Shared Function WithAnnotationSourceSpace(adj As ImageAdjustments, source As SKBitmap) As ImageAdjustments
            If adj Is Nothing OrElse source Is Nothing Then Return adj
            If adj.SourceWidthPixels > 0 AndAlso adj.SourceHeightPixels > 0 Then Return adj
            If adj.Annotations Is Nothing OrElse adj.Annotations.Count = 0 Then Return adj

            Dim ergaenzt = adj.Clone()
            ergaenzt.SourceWidthPixels = source.Width
            ergaenzt.SourceHeightPixels = source.Height
            Return ergaenzt
        End Function

        ' Alle Pipeline-Schritte AUSSER dem Einzeichnen der Objekte (Annotations). Wird von
        ' ProcessBitmap sowie vom Basis-Cache in ApplyAdjustments(source As SKBitmap, ...) genutzt.
        ' ARBEITSBILD (Stufe E): Retusche ist KEIN Pipeline-Schritt mehr - sie steckt bereits im
        ' Eingangsbild (Arbeitsbild); die Pipeline beginnt direkt mit der Geometrie.
        ''' <summary>Schaltet zwischen der alten Stufenkette und der verschmolzenen
        ''' Gleitkomma-Kette um. Waehrend der Umstellung laufen beide nebeneinander, damit
        ''' der Aequivalenztest der Diagnose sie vergleichen kann.</summary>
        Private Shared Function ProcessBitmapBase(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            ' Copy-on-write: die Kette startet auf der FREMDEN Quelle und kopiert erst, wenn eine Stufe
            ' wirklich ein neues Bild liefert. Siehe ReplaceBitmapOwned.
            Dim processed As SKBitmap = source
            Dim owned = False

            processed = ReplaceBitmapOwned(processed, ApplyImageWarp(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyCrop(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyGeometryTransforms(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyStraighten(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyPerspective(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyResize(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyCanvasResize(processed, adj), owned)
            processed = ReplaceBitmapOwned(processed, ApplyDocumentBackground(processed, adj), owned)

            ' Eine Auswahl wird im bereits gerenderten Display-Raum angelegt. Deshalb muss auch der
            ' unveraenderte Vergleichsstand fuer selektive Farb-/Detailanpassungen NACH der Geometrie
            ' aufgenommen werden. `source` ist hier noch SourceSpace und passt nach Rotation/Flip weder
            ' in den Abmessungen noch in der Pixelanordnung zur sichtbaren Auswahl.
            If adj IsNot Nothing AndAlso Not adj.GlobalAdjustmentsHidden Then
                Dim selectionBaseline As SKBitmap = Nothing
                If SelectionScopeIsEnabled(adj) Then selectionBaseline = CloneBitmap(processed)

                processed = ApplyPixelAdjustmentStagesCore(processed, adj, owned)

                ' Auswahl-Skopus: Anpassungen nur INNERHALB der aktiven Auswahl wirken lassen. Maske,
                ' Vergleichsstand und angepasstes Bild liegen hier gemeinsam im gerenderten Display-Raum.
                If selectionBaseline IsNot Nothing Then
                    Dim scopeMask = BuildSelectionScopeMask(adj, processed.Width, processed.Height)
                    If scopeMask IsNot Nothing Then
                        Using scopeMask
                            processed = ReplaceBitmapOwned(processed, CompositeSelectionScoped(selectionBaseline, processed, scopeMask), owned)
                        End Using
                    Else
                        ' Eine aktive, aber unlesbare/leer gewordene Maske darf niemals still auf eine
                        ' globale Anpassung zurueckfallen. Im Fehlerfall bleibt das Bild unveraendert.
                        processed = ReplaceBitmapOwned(processed, CloneBitmap(selectionBaseline), owned)
                    End If
                    selectionBaseline.Dispose()
                End If
            End If

            processed = ApplyMaskedAdjustmentLayersCore(processed, adj, source.Width, source.Height, Nothing, owned)
            Return TakeOwnership(processed, owned)
        End Function

        ''' <summary>Die wiederverwendbare Pixelkette ohne Geometrie, Objekte und Auswahl-Compositing.
        ''' Sie dient sowohl der globalen Bearbeitung als auch jeder lokalen Einstellungsebene.</summary>
        Private Shared Function ApplyPixelAdjustmentStages(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim owned = False
            Return TakeOwnership(ApplyPixelAdjustmentStagesCore(source, adj, owned), owned)
        End Function

        ''' <summary>Der Kern der Pixelkette. <paramref name="owned"/> wandert durch: beim Eintritt
        ''' sagt es, ob <paramref name="source"/> uns gehört, beim Austritt, ob das Ergebnis uns
        ''' gehört. Nur so kann die Kette ohne Eingangskopie beginnen - siehe
        ''' <see cref="ReplaceBitmapOwned"/>.</summary>
        Private Shared Function ApplyPixelAdjustmentStagesCore(source As SKBitmap, adj As ImageAdjustments,
                                                               ByRef owned As Boolean) As SKBitmap
            Dim processed = source

            ' Alle Farb-Punktoperationen laufen in EINER verschmolzenen Gleitkomma-Stufe
            ' (ImageProcessorPointOps.vb): Filmnegativ, Farbmatrix, Tonwertkurve, Lichter/Tiefen/
            ' Weiß/Schwarz, RGB- und Kanalkurven, Luminanzkurve, HSL-Bänder, Split-Toning,
            ' Preset-Matrix und Cube-LUT. Vorher waren das acht aufeinanderfolgende Stufen mit je
            ' einem eigenen 8-Bit-Zwischenbild - die gestapelten Rundungen waren die Streifenbildung
            ' in Himmel und Hauttönen. Jetzt wird EINMAL am Ende quantisiert (mit Dither).
            ' Die Umkehr steckt dabei ganz vorn in der Kette: Belichtung, Weißabgleich, Kurven und
            ' Filter sollen auf dem fertigen Positiv arbeiten - auf dem Negativ wären sie
            ' seitenverkehrt (Aufhellen würde abdunkeln).
            processed = ReplaceBitmapOwned(processed, ApplyPointOpChain(processed, adj), owned)

            ' "weich" steht im selben Select Case wie die 15 Farbpresets, ist aber als einziges KEINE
            ' Punktoperation, sondern eine echte räumliche Unschärfe. BuildFilterPresetMatrix liefert
            ' dafür bewusst Nothing; die Stufe läuft hier getrennt.
            If String.Equals(If(adj.FilterPreset, "").Trim(), "weich", StringComparison.OrdinalIgnoreCase) Then
                processed = ReplaceBitmapOwned(processed, ApplySoftFocusBlur(processed, Clamp(adj.FilterStrength / 100.0F, 0, 1)), owned)
            End If

            If adj.Clarity <> 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyClarity(processed, adj.Clarity / 100.0F), owned)
            End If
            If adj.[Structure] <> 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyStructure(processed, adj.[Structure] / 100.0F), owned)
            End If
            If adj.Haze <> 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyHaze(processed, adj.Haze / 100.0F), owned)
            End If

            If adj.NoiseReduction > 0 Then
                If adj.NoiseReductionMethod = NoiseReductionMethod.Median Then
                    processed = ReplaceBitmapOwned(processed, ApplyMedianBlur(processed, adj.NoiseReduction / 100.0F), owned)
                Else
                    processed = ReplaceBitmapOwned(processed, ApplyNoiseReduction(processed, adj.NoiseReduction / 100.0F, adj.NoiseReductionDetail / 100.0F), owned)
                End If
            End If
            ' Die beiden Seiten desselben Panel-Reglers: Minus glaettet die Farbanteile, Plus faerbt
            ' sie ein. Getrennte Felder, weil nur die Reduzierung eine Entsprechung in den Presets hat
            ' (crs:ColorNoiseReduction) - siehe ImageAdjustments.ColorNoiseAdd.
            If adj.ColorNoiseReduction > 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyColorNoiseReduction(processed, adj.ColorNoiseReduction / 100.0F), owned)
            End If
            ' NACH der einstufigen Glaettung: die nimmt das feine Farbkorn, diese Stufe die groben
            ' Flecken. Umgekehrt muesste die grobe Stufe erst durch das feine Korn hindurch.
            If adj.FarbrauschGrob > 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyMultiScaleDenoise(
                    processed, adj.FarbrauschGrob / 100.0F, adj.ColorNoiseCoarseScale / 100.0F), owned)
            End If
            If adj.ColorNoiseAdd > 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyColorNoiseAdd(processed, adj.ColorNoiseAdd / 100.0F), owned)
            End If
            If adj.DustScratches <> 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyDustScratches(processed, adj.DustScratches / 100.0F), owned)
            End If
            If adj.Glow <> 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyImageGlow(processed, adj.Glow / 100.0F), owned)
            End If

            If adj.Sharpness > 0 Then
                processed = ReplaceBitmapOwned(processed, ApplySharpness(processed, adj.Sharpness / 100.0F, adj.SharpenRadius / 100.0F, adj.SharpenDetail / 100.0F, adj.SharpenMasking / 100.0F), owned)
            End If

            If adj.Vignette <> 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyVignette(processed, adj.Vignette / 100.0F, adj.VignetteTransition, adj.VignetteRoundness, adj.VignetteFeather, adj.VignetteCenterX, adj.VignetteCenterY, adj.VignetteStyle), owned)
            End If

            If adj.Grain > 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyGrain(processed, adj.Grain / 100.0F, adj.GrainSize / 100.0F, adj.GrainFrequency / 100.0F, adj.GrainColor / 100.0F), owned)
            End If
            If adj.AddNoise > 0 Then
                processed = ReplaceBitmapOwned(processed, ApplyAddNoise(processed, adj.AddNoise / 100.0F), owned)
            ElseIf adj.AddNoise < 0 Then
                ' Negative Haelfte = Rauschen REDUZIEREN (gleichmaessiges Weichzeichnen wie der
                ' Gaussian-Modus der Rauschreduzierung) - ein Regler, beide Richtungen.
                processed = ReplaceBitmapOwned(processed, ApplyNoiseReduction(processed, -adj.AddNoise / 100.0F), owned)
            End If

            ' Der Rahmen war bis hierher die letzte Stufe der Pixelkette. Er ist jetzt ein OBJEKT
            ' und wird mit den anderen Objekten gezeichnet - deshalb steht hier nichts mehr.

            Return processed
        End Function


        ''' <summary>Mehrskaliges Entrauschen der FARBE: nimmt grobfleckiges Farbrauschen weg, ohne
        ''' weichzuzeichnen.
        '''
        ''' Der Unterschied zu allem, was wir sonst haben, ist die SKALA. Ein Weichzeichner - egal ob
        ''' Gauss, Median oder kantenerhaltend - arbeitet auf einer Groesse. Grobfleckiges Rauschen
        ''' aus hochgezogenen Aufnahmen ist aber zehn bis zwanzig Bildpunkte gross; um daran zu
        ''' kommen, braucht so ein Filter einen Radius, der alles andere gleich mitfrisst. Genau
        ''' deshalb sah unser Farbrausch-Regler bei solchen Bildern schwach aus - er arbeitet auf der
        ''' falschen Skala.
        '''
        ''' Hier wird das Bild stattdessen in mehrere Groessenstufen ZERLEGT. Ein 20-Punkte-Fleck
        ''' liegt auf einer groben Stufe und ist DORT ein kleiner Ausschlag - ihn wegzunehmen kostet
        ''' fast nichts. Die Kanten des Motivs sind auf jeder Stufe grosse Ausschlaege und bleiben.
        '''
        ''' Geschrumpft wird mit einer Kennlinie, die grosse Ausschlaege fast unangetastet laesst
        ''' (y = x - T hoch 2 / x oberhalb der Schwelle, null darunter). Ein glattes Abziehen der
        ''' Schwelle von allem wuerde auch die Kanten um denselben Betrag abschwaechen - das ist der
        ''' Unterschied zwischen "entrauscht" und "matt".
        '''
        ''' Angefasst werden NUR die Farbkanaele. Das Auge loest Farbdetails ohnehin kaum auf, und
        ''' die Struktur eines Bildes steckt in der Helligkeit - die bleibt hier unberuehrt. Fuer
        ''' Helligkeitsrauschen gibt es die beiden bestehenden Regler.</summary>
        ''' <param name="strength">0 bis 1.</param>
        ''' <param name="coarse">0 bis 1: wie grosse Flecken noch erfasst werden.</param>
        Public Shared Function ApplyMultiScaleDenoise(source As SKBitmap, strength As Single,
                                                          coarse As Single) As SKBitmap
            If source Is Nothing OrElse strength <= 0.001F Then Return CloneBitmap(source)
            Dim w = source.Width, h = source.Height
            If w < 8 OrElse h < 8 Then Return CloneBitmap(source)

            ' Wie viele Stufen. Jede verdoppelt die erfasste Fleckengroesse; sechs Stufen reichen bis
            ' etwa 64 Bildpunkte, und groeber als das ist kein Rauschen mehr, sondern Motiv.
            Dim steps = Math.Max(3, Math.Min(7, CInt(Math.Round(3.0 + coarse * 4.0))))
            Dim n = w * h

            ' Gelesen wird DIREKT aus der Quelle. Der frühere Arbeitsklon war eine reine Vollkopie,
            ' aus der nur gelesen wurde - bei 45 MP allein rund 145 ms.
            Dim srcBuffer As Byte() = Nothing
            Dim srcStride, ri, gi, bi, ai As Integer
            Dim hasBuffer = TryBorrowRgbaLikeBuffer(source, srcBuffer, srcStride, ri, gi, bi, ai)
            Dim target = New SKBitmap(w, h, source.ColorType, source.AlphaType)

            ' ZWEI ANNAHMEN, die der Pufferweg still voraussetzte - jetzt geprueft, statt geglaubt.
            '
            ' 1. GLEICHE ZEILENLAENGE. Geschrieben wird in einen Puffer in der Groesse der QUELLE,
            '    indiziert mit deren Stride, und am Stueck in das FRISCH angelegte Ziel kopiert. Wäre
            '    die Quelle gepolstert, liefe die Kopie ueber das Ziel hinaus. Fuer alles, was Skia
            '    hier selbst anlegt, sind beide gleich - aber die Invariante steht nirgends.
            '
            ' 2. PREMULTIPLIZIERT. Ent- und Rueckrechnung bilden nach, was GetPixel/SetPixel auf
            '    einem Premul-Bitmap tun. Auf einem UNPREMUL-Bitmap tun die beiden gar nichts, und
            '    der Pufferweg lieferte an teiltransparenten Stellen ein anderes Bild als der
            '    Rueckfallzweig darunter. Bei Opaque ist Alpha 255 und beide Rechnungen sind die
            '    Identitaet, das ist also unbedenklich.
            '
            ' Trifft eine der beiden nicht zu, geht es ueber GetPixel/SetPixel weiter - langsam, aber
            ' richtig. Das ist derselbe Rueckfall, den exotische Farbtypen ohnehin nehmen.
            If hasBuffer AndAlso (srcStride <> target.RowBytes OrElse
                                  source.AlphaType = SKAlphaType.Unpremul) Then
                hasBuffer = False
                srcBuffer = Nothing
            End If

            Dim work As SKBitmap = If(hasBuffer, Nothing, CloneBitmap(source))
            Try
                ' In Helligkeit und zwei Farbdifferenzen zerlegen. Nicht wegen der Norm, sondern weil
                ' sich nur so unterschiedlich hart schrumpfen laesst.
                Dim y(n - 1) As Single, cb(n - 1) As Single, cr(n - 1) As Single
                Dim alpha(n - 1) As Byte
                If hasBuffer Then
                    ' Über den Byte-Puffer statt über GetPixel: derselbe Zeilenblock kostete mit
                    ' GetPixel/SetPixel gemessen rund 4 s je Vorschaubild, über den Puffer und
                    ' zeilenparallel rund 7 ms. Entpremultipliziert wird mit derselben Tabelle, die
                    ' GetPixel benutzt, damit das Bild an weichen Kanten bitgleich bleibt.
                    ForEachRow(w, h,
                        Sub(row)
                            Dim ro = row * srcStride
                            Dim rk = row * w
                            For i = 0 To w - 1
                                Dim o = ro + i * 4
                                Dim k = rk + i
                                Dim a = srcBuffer(o + ai)
                                Dim pr = UnpremultiplyByte(srcBuffer(o + ri), a)
                                Dim pg = UnpremultiplyByte(srcBuffer(o + gi), a)
                                Dim pb = UnpremultiplyByte(srcBuffer(o + bi), a)
                                y(k) = 0.299F * pr + 0.587F * pg + 0.114F * pb
                                cb(k) = pb - y(k)
                                cr(k) = pr - y(k)
                                alpha(k) = a
                            Next
                        End Sub)
                Else
                    ' Rückfall für exotische Farbtypen, die der Puffer nicht abdeckt.
                    For j = 0 To h - 1
                        For i = 0 To w - 1
                            Dim p = work.GetPixel(i, j)
                            Dim k = j * w + i
                            y(k) = 0.299F * p.Red + 0.587F * p.Green + 0.114F * p.Blue
                            cb(k) = p.Blue - y(k)
                            cr(k) = p.Red - y(k)
                            alpha(k) = p.Alpha
                        Next
                    Next
                End If

                ' Die Schwellen. Die Farbkanaele bekommen ein Vielfaches - dort darf es haerter zur
                ' Sache gehen, ohne dass man es sieht.
                ' NUR die Farbkanaele. Die Helligkeit bleibt unberuehrt, und das ist eine bewusste
                ' Grenze: eine Schrumpfung ueber viele Stufen nimmt auch die Auslaeufer einer Kante
                ' mit, und die tragen deren Kontrast. Gemessen fiel eine harte Kante dadurch von 120
                ' auf 43 Stufen - am sauberen Bild genauso wie am verrauschten, es lag also nicht am
                ' Rauschen, sondern am Verfahren. Fuer die Helligkeit gibt es die beiden bestehenden
                ' Regler; hier geht es um das grobfleckige FARBrauschen, an das die nicht herankommen.
                ' Kennlinie, gemessen an Flecken der Hoehe plus/minus 26. Vorher lief die Schwelle
                ' linear von 4 auf 38, und der Regler war damit bei Stellung 25 fertig: von 25 bis
                ' 100 aenderte sich NICHTS mehr, und fast 60 Prozent der Wirkung lagen schon im
                ' ersten Schritt. Drei Viertel des Weges waren wirkungslos.
                '
                ' Der Grund ist das Verfahren, nicht die Zahl: geschrumpft wird auf mehreren Stufen
                ' nacheinander, die Ausschlaege je Stufe sind viel kleiner als der Fleck im Bild.
                ' Der nutzbare Schwellenbereich liegt deshalb bei etwa 0 bis 13 und nicht bei 38.
                '
                ' Jetzt 1 bis 20 mit einem Exponenten von 1,5: der untere Bereich wird gedehnt, wo
                ' die Wirkung sitzt, und ueber der Saettigung bleibt Luft fuer starkes Rauschen.
                ' Die Kennlinie steht als Info-Zeile im Pruefstand - sie zeigt sofort, wenn eine
                ' Aenderung den Bereich wieder zusammenschiebt.
                Dim thresholdC = 1.0F + 19.0F * CSng(Math.Pow(strength, 1.5))
                ShrinkSteps(cb, w, h, steps, thresholdC, 1.0F)
                ShrinkSteps(cr, w, h, steps, thresholdC, 1.0F)

                If hasBuffer Then
                    Dim dstBuffer = New Byte(srcBuffer.Length - 1) {}
                    ForEachRow(w, h,
                        Sub(row)
                            Dim ro = row * srcStride
                            Dim rk = row * w
                            For i = 0 To w - 1
                                Dim o = ro + i * 4
                                Dim k = rk + i
                                Dim r = y(k) + cr(k)
                                Dim b = y(k) + cb(k)
                                Dim g = (y(k) - 0.299F * r - 0.114F * b) / 0.587F
                                Dim a = alpha(k)
                                ' Premultiplizieren wie SetPixel, sonst stimmen weiche Kanten nicht.
                                dstBuffer(o + ri) = PremultiplyByte(KlemmeByte(r), a)
                                dstBuffer(o + gi) = PremultiplyByte(KlemmeByte(g), a)
                                dstBuffer(o + bi) = PremultiplyByte(KlemmeByte(b), a)
                                dstBuffer(o + ai) = a
                            Next
                        End Sub)
                    Marshal.Copy(dstBuffer, 0, target.GetPixels(), dstBuffer.Length)
                Else
                    For j = 0 To h - 1
                        For i = 0 To w - 1
                            Dim k = j * w + i
                            Dim r = y(k) + cr(k)
                            Dim b = y(k) + cb(k)
                            Dim g = (y(k) - 0.299F * r - 0.114F * b) / 0.587F
                            target.SetPixel(i, j, New SKColor(KlemmeByte(r), KlemmeByte(g), KlemmeByte(b), alpha(k)))
                        Next
                    Next
                End If
                Return target
            Catch
                target.Dispose()
                Return CloneBitmap(source)
            Finally
                If work IsNot Nothing Then work.Dispose()
            End Try
        End Function

        ''' <summary>Zerlegt einen Kanal in Groessenstufen, schrumpft jede und setzt wieder zusammen.
        ''' Der Kanal wird an Ort und Stelle geaendert.
        '''
        ''' <paramref name="growth"/> sagt, wie die Schwelle von Stufe zu Stufe waechst. Groeber
        ''' heisst mehr Flaeche und damit weniger Zufall - dort steht ein Ausschlag eher fuer etwas
        ''' Echtes und die Schwelle darf mitwachsen, sonst frisst man auf den groben Stufen den
        ''' Farbverlauf des Motivs mit.</summary>
        Private Shared Sub ShrinkSteps(channel As Single(), w As Integer, h As Integer,
                                           steps As Integer, threshold As Single, growth As Single)
            Dim n = w * h
            Dim baseline(n - 1) As Single
            Array.Copy(channel, baseline, n)
            Dim result(n - 1) As Single
            Dim smoothed(n - 1) As Single
            Dim s = threshold

            ' "Step" ist in VB ein Schlüsselwort (For ... Step) und taugt nicht als Name.
            For level = 1 To steps
                Dim radius = CInt(Math.Pow(2, level - 1))
                Array.Copy(baseline, smoothed, n)
                BoxBlur(smoothed, w, h, radius)
                ' Was die Glaettung wegnimmt, ist der Anteil DIESER Stufe.
                ' Elementweise und damit zeilenunabhängig - siehe Hinweis in BoxBlur.
                Dim sStep = s
                ForEachRow(w, h,
                    Sub(j)
                        Dim row = j * w
                        For i = 0 To w - 1
                            Dim k = row + i
                            result(k) += Shrink(baseline(k) - smoothed(k), sStep)
                        Next
                    End Sub)
                Array.Copy(smoothed, baseline, n)
                s *= growth
            Next

            ' Der Rest ist der grobe Bildaufbau - der bleibt unangetastet.
            ForEachRow(w, h,
                Sub(j)
                    Dim row = j * w
                    For i = 0 To w - 1
                        Dim k = row + i
                        channel(k) = result(k) + baseline(k)
                    Next
                End Sub)
        End Sub

        ''' <summary>Die Schrumpfkennlinie. Unterhalb der Schwelle null, darueber wird nur ein mit
        ''' dem Betrag FALLENDER Anteil abgezogen - ein grosser Ausschlag bleibt damit praktisch
        ''' erhalten. Ein glattes Abziehen der Schwelle wuerde jede Kante um denselben Betrag
        ''' abschwaechen.</summary>
        Private Shared Function Shrink(x As Single, schwelle As Single) As Single
            Dim a = Math.Abs(x)
            If a <= schwelle Then Return 0.0F
            ' Deutlich ueber der Schwelle: UNANGETASTET lassen. Die weiche Kennlinie zieht auch
            ' grossen Ausschlaegen noch etwas ab, und ueber fuenf Stufen summiert sich das - eine
            ' harte Kante fiel so von 120 auf 41, also mehr als um die Haelfte. Was so weit ueber
            ' der Schwelle liegt, ist kein Rauschen mehr, sondern das Motiv.
            ' Der Uebergangsbereich ist BEWUSST schmal. Er wird ueber alle Stufen aufsummiert: an
            ' einer harten Kante liegt der Ausschlag auf den mittleren Stufen genau in diesem Band,
            ' und ein breiter Bereich hat die Kante von 120 auf 43 Stufen fallen lassen - am
            ' sauberen Bild genauso wie am verrauschten. Das war kein Entrauschen mehr.
            If a >= schwelle * 1.5F Then Return x
            Dim soft = CSng(x - schwelle * schwelle / x)
            Dim t = (a - schwelle) / (schwelle * 0.5F)
            Return soft * (1.0F - t) + x * t
        End Function

        ''' <summary>Kastenunschaerfe, zweimal getrennt - waagerecht, dann senkrecht. Ueber laufende
        ''' Summen, damit die Kosten NICHT mit dem Radius wachsen: auf den groben Stufen ist der
        ''' Radius 32, und eine gewoehnliche Faltung waere dort unbezahlbar.</summary>
        Private Shared Sub BoxBlur(field As Single(), w As Integer, h As Integer, radius As Integer)
            If radius < 1 Then Return
            Dim intermediate(w * h - 1) As Single
            Dim window = radius * 2 + 1

            ' Beide Durchgänge laufen zeilen- bzw. spaltenWEISE und jeder schreibt nur in seine eigene
            ' Zeile bzw. Spalte - die laufende Summe ist je Durchlauf lokal. Damit ist das Ergebnis
            ' unabhängig von der Thread-Aufteilung bitgleich zum seriellen Lauf.
            ForEachRow(w, h,
                Sub(j)
                    Dim row = j * w
                    Dim sum As Single = 0
                    For i = -radius To radius
                        sum += field(row + Math.Max(0, Math.Min(w - 1, i)))
                    Next
                    For i = 0 To w - 1
                        intermediate(row + i) = sum / window
                        Dim leaving = Math.Max(0, Math.Min(w - 1, i - radius))
                        Dim entering = Math.Max(0, Math.Min(w - 1, i + radius + 1))
                        sum += field(row + entering) - field(row + leaving)
                    Next
                End Sub)

            ' Der senkrechte Durchgang läuft über SPALTEN. ForEachRow iteriert über den zweiten
            ' Parameter, deshalb stehen h und w hier vertauscht - die Lastschwelle bleibt dieselbe.
            ForEachRow(h, w,
                Sub(i)
                    Dim sum As Single = 0
                    For j = -radius To radius
                        sum += intermediate(Math.Max(0, Math.Min(h - 1, j)) * w + i)
                    Next
                    For j = 0 To h - 1
                        field(j * w + i) = sum / window
                        Dim leaving = Math.Max(0, Math.Min(h - 1, j - radius))
                        Dim entering = Math.Max(0, Math.Min(h - 1, j + radius + 1))
                        sum += intermediate(entering * w + i) - intermediate(leaving * w + i)
                    Next
                End Sub)
        End Sub

        Private Shared Function KlemmeByte(v As Single) As Byte
            If Single.IsNaN(v) Then Return 0
            Return CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(v)))))
        End Function

        ''' <summary>Wendet lokale Korrekturen in Ebenenreihenfolge an. Eine fehlende oder beschädigte
        ''' Maske bewirkt absichtlich gar nichts; sie darf nie zu einer globalen Korrektur werden.</summary>
        ''' <param name="onlyStackedAboveId">Nothing = nur die Korrekturen des BASISBILDS (ohne
        ''' Einsortierung in den Objektstapel). Sonst genau die Korrekturen, die über dem Objekt mit
        ''' dieser Id liegen - sie wirken damit auf Basis UND die bereits gezeichneten Objekte.</param>
        Private Shared Function ApplyMaskedAdjustmentLayers(source As SKBitmap, adj As ImageAdjustments,
                                                             pipelineInputWidth As Integer,
                                                             pipelineInputHeight As Integer,
                                                             Optional onlyStackedAboveId As String = Nothing) As SKBitmap
            Dim owned = False
            Return TakeOwnership(ApplyMaskedAdjustmentLayersCore(source, adj, pipelineInputWidth,
                                                                 pipelineInputHeight, onlyStackedAboveId, owned), owned)
        End Function

        ''' <summary>Der Kern der Korrekturebenen. <paramref name="owned"/> wandert wie in
        ''' <see cref="ApplyPixelAdjustmentStagesCore"/> durch: ohne sichtbare Korrektur - der Regelfall -
        ''' fällt hier gar keine Kopie mehr an.</summary>
        ''' Das Objekt mit dieser Kennung, oder Nothing.
        Private Shared Function FindAnnotationById(adj As ImageAdjustments, id As String) As ImageAnnotation
            If adj Is Nothing OrElse adj.Annotations Is Nothing OrElse String.IsNullOrEmpty(id) Then Return Nothing
            For Each a In adj.Annotations
                If a IsNot Nothing AndAlso String.Equals(a.Id, id, StringComparison.Ordinal) Then Return a
            Next
            Return Nothing
        End Function

        ''' <summary>Multipliziert eine Alpha8-Maske Punkt fuer Punkt mit einer Deckung (ein Byte je
        ''' Punkt, gleiche Maße). Damit wird aus "wo die Maske gilt" ein "wo die Maske UND die Ebene
        ''' darunter gelten".</summary>
        Private Shared Sub MultiplyMaskByCoverage(mask As SKBitmap, coverage As Byte())
            If mask Is Nothing OrElse coverage Is Nothing Then Return
            If mask.ColorType <> SKColorType.Alpha8 Then Return
            Dim w = mask.Width, h = mask.Height
            If coverage.Length < w * h Then Return
            Dim stride = mask.RowBytes
            Dim buffer = New Byte(stride * h - 1) {}
            Marshal.Copy(mask.GetPixels(), buffer, 0, buffer.Length)
            For y = 0 To h - 1
                Dim row = y * stride, cov = y * w
                For x = 0 To w - 1
                    Dim m = CInt(buffer(row + x))
                    If m = 0 Then Continue For
                    buffer(row + x) = CByte((m * CInt(coverage(cov + x)) + 127) \ 255)
                Next
            Next
            Marshal.Copy(buffer, 0, mask.GetPixels(), buffer.Length)
        End Sub

        Private Shared Function ApplyMaskedAdjustmentLayersCore(source As SKBitmap, adj As ImageAdjustments,
                                                                pipelineInputWidth As Integer,
                                                                pipelineInputHeight As Integer,
                                                                onlyStackedAboveId As String,
                                                                ByRef owned As Boolean) As SKBitmap
            Dim processed = source
            If adj Is Nothing OrElse adj.MaskedAdjustmentLayers Is Nothing OrElse adj.Masks Is Nothing Then Return processed

            Dim masksById = adj.Masks.Where(Function(m) m IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(m.Id)).
                                     GroupBy(Function(m) m.Id, StringComparer.Ordinal).
                                     ToDictionary(Function(g) g.Key, Function(g) g.First(), StringComparer.Ordinal)
            ' Ebenen mit DERSELBEN Anpassung wirken GEMEINSAM, nicht nacheinander. Sonst trifft die
            ' Anpassung jeden Pixel, den zwei Masken gemeinsam abdecken, zweimal - wer eine Gruppe
            ' oder eine Mehrfachauswahl von Maskenebenen anpasst, bekommt in der Ueberschneidung die
            ' doppelte Wirkung. Zusammengefasst wird ueber den Fingerabdruck der Pixelwerte: das ist
            ' genau das, was eine gemeinsame Anpassung hinterlaesst, und es bleibt nach dem Abwaehlen
            ' und nach dem Neuladen gleich (eine Mehrfachauswahl selbst ist fluechtig und stuende
            ' beim naechsten Oeffnen nicht mehr zur Verfuegung).
            ' Bei DISJUNKTEN Masken aendert das Zusammenfassen nichts - jeder Pixel wird ohnehin nur
            ' einmal getroffen -, es greift also genau dort, wo sich etwas ueberschneidet.
            ' Zusammengefasst wird nur INNERHALB EINER GRUPPE. Gleiche Werte allein reichen NICHT:
            ' mehrere Korrekturen duerfen dieselbe Maske mit verschiedenen Deckkraeften tragen und
            ' sollen sich dann ausdruecklich aufaddieren (eigene Pruefung). Die Gruppe ist die
            ' ausdrueckliche Aussage "das gehoert zusammen" - und sie ueberlebt das Abwaehlen und
            ' das Neuladen, anders als eine Mehrfachauswahl.
            Dim gemeinsam = adj.MaskedAdjustmentLayers.
                Where(Function(l) l IsNot Nothing AndAlso Not String.IsNullOrEmpty(l.GroupId) AndAlso
                                  l.Adjustments IsNot Nothing AndAlso l.Adjustments.HasPixelAdjustments()).
                GroupBy(Function(l) l.GroupId & "|" & PixelAdjustmentsFingerprint(l.Adjustments), StringComparer.Ordinal).
                Where(Function(g) g.Count() > 1).
                ToDictionary(Function(g) g.Key, Function(g) g.ToList(), StringComparer.Ordinal)
            Dim erledigt As New HashSet(Of String)(StringComparer.Ordinal)

            For Each layer In adj.MaskedAdjustmentLayers
                If Not adj.IsMaskedLayerRenderVisible(layer) Then Continue For
                Dim stackedAbove = If(layer.StackAboveAnnotationId, "")
                If onlyStackedAboveId Is Nothing Then
                    If stackedAbove.Length > 0 Then Continue For          ' liegt im Objektstapel
                ElseIf Not String.Equals(stackedAbove, onlyStackedAboveId, StringComparison.Ordinal) Then
                    Continue For
                End If
                Dim hasAdj = layer.Adjustments IsNot Nothing AndAlso layer.Adjustments.HasPixelAdjustments()
                Dim hasFill = layer.HasFill()
                ' Auch Ebenen OHNE Pixel-Anpassung verarbeiten, wenn sie eine deklarative Füllung tragen
                ' (sichtbare Auswahl-Füllung bzw. Masken-Abstufung).
                If Not hasAdj AndAlso Not hasFill Then Continue For
                Dim maskData As ImageMask = Nothing
                If Not masksById.TryGetValue(If(layer.MaskId, ""), maskData) Then Continue For

                ' Rolle der Füllung nach Zweck, nicht nur nach Ebenenart: Eine Füllung, die MIT einer
                ' Anpassung auf derselben Ebene liegt, existiert, um diese Anpassung ABZUSTUFEN (Luminanz →
                ' Maskenstärke) - egal ob Masken- oder Auswahl-Ebene. Eine Füllung OHNE Anpassung ist reine
                ' sichtbare Farb-/Verlaufsfläche. (Geometrische Auswahlen kommen als IsMaskLayer=False; ohne
                ' diese hasAdj-Weiche stufte eine Verlaufsfüllung die Anpassung dort nie ab.)
                Dim modulateFill = hasFill AndAlso (layer.IsMaskLayer OrElse hasAdj)
                Using mask = BuildPersistentMaskForOutput(maskData, adj, pipelineInputWidth, pipelineInputHeight,
                                                          processed.Width, processed.Height, layer.Opacity,
                                                          If(modulateFill, layer, Nothing))
                    If mask Is Nothing Then Continue For
                    ' SCHNITTMASKE: die Korrektur gilt nur, wo das Objekt deckt, ueber dem sie
                    ' einsortiert ist. Die Deckung kommt aus derselben Quelle wie die Schnittmaske
                    ' eines Objekts (BuildClipBaseCoverage) - zwei Wege dahin liefen auseinander.
                    ' Ohne Anker gibt es keine Ebene darunter, dann bleibt der Schalter wirkungslos;
                    ' dieselbe Entscheidung wie am Objekt, wo eine fehlende Basis den Schalter
                    ' verpuffen laesst, statt die Ebene verschwinden zu lassen.
                    If layer.ClipToLayerBelow AndAlso stackedAbove.Length > 0 Then
                        Dim clipBase = FindAnnotationById(adj, stackedAbove)
                        If clipBase IsNot Nothing AndAlso adj.IsAnnotationRenderVisible(clipBase) Then
                            Dim clip = BuildClipBaseCoverage(adj, clipBase, pipelineInputWidth, pipelineInputHeight,
                                                             0, 0, processed.Width, processed.Height)
                            If clip IsNot Nothing Then MultiplyMaskByCoverage(mask, clip)
                        End If
                    End If
                    If hasAdj Then
                        ' Teilt diese Ebene ihre Anpassung mit anderen, wird EINMAL ueber die
                        ' Vereinigung aller ihrer Masken angewendet - an der Stelle der ERSTEN
                        ' Ebene der Gruppe, damit die Reihenfolge gegenueber anderen Korrekturen
                        ' erhalten bleibt.
                        Dim key = If(layer.GroupId, "") & "|" & PixelAdjustmentsFingerprint(layer.Adjustments)
                        Dim geschwister As List(Of MaskedAdjustmentLayer) = Nothing
                        If gemeinsam.TryGetValue(key, geschwister) Then
                            If erledigt.Contains(key) Then Continue For
                            erledigt.Add(key)
                        End If
                        Dim effectMask = mask
                        Dim eigene As SKBitmap = Nothing
                        Try
                            If geschwister IsNot Nothing Then
                                eigene = MergeEffectMasks(geschwister, layer, mask, adj, masksById,
                                                             pipelineInputWidth, pipelineInputHeight,
                                                             processed.Width, processed.Height, onlyStackedAboveId)
                                If eigene IsNot Nothing Then effectMask = eigene
                            End If
                            ' NUR IM MASKENRECHTECK rechnen, wo das geht. Die Kette lief hier je
                            ' Ebene ueber das GANZE Bild und schnitt erst danach zu; gemessen sind
                            ' das bei 45 MP rund 100 ms je Ebene mit zwei Punktreglern. Die
                            ' Bedingungen dafuer stehen an LayerAdjustmentsAreCropSafe und
                            ' TryGetMaskScopeRect, beide gegen Messungen gesetzt.
                            ' Der Ausschnittweg schreibt DIREKT in processed und setzt deshalb
                            ' Besitz voraus - ohne ihn gehoert das Bild dem Aufrufer, und eine
                            ' Kopie waere genau die Vollkopie, die der Weg vermeiden soll.
                            Dim layerPixelAdjustments = layer.Adjustments.ExtractPixelAdjustments()
                            Dim scopeRect As SKRectI? = Nothing
                            If owned AndAlso LayerAdjustmentsAreCropSafe(layerPixelAdjustments) Then
                                scopeRect = TryGetMaskScopeRect(effectMask, processed.Width, processed.Height)
                            End If

                            If scopeRect.HasValue AndAlso
                               ApplyLayerAdjustmentsInRect(processed, effectMask, layerPixelAdjustments, scopeRect.Value) Then
                                ' Fertig: processed traegt die Korrektur bereits.
                            Else
                                Using adjusted = ApplyPixelAdjustmentStages(processed, layerPixelAdjustments)
                                    processed = ReplaceBitmapOwned(processed, CompositeSelectionScoped(processed, adjusted, effectMask), owned)
                                End Using
                            End If
                        Finally
                            eigene?.Dispose()
                        End Try
                    End If
                    ' Sichtbare Füllung nur, wenn die Füllung NICHT bereits eine Anpassung abstuft.
                    If hasFill AndAlso Not layer.IsMaskLayer AndAlso Not hasAdj Then
                        Dim filled = CompositeVisibleFill(processed, mask, layer)
                        If filled IsNot Nothing Then processed = ReplaceBitmapOwned(processed, filled, owned)
                    End If
                End Using
            Next
            Return processed
        End Function

        ''' <summary>Komponiert die deklarative Füllung einer AUSWAHL-Ebene SICHTBAR in ihre Auswahlregion:
        ''' Vollfarbe/Verlauf/radial über die Bounding-Box der Maske, Deckung = Maskenwert × Eigen-Alpha der
        ''' Füllung. So erscheint die Füllung als Farbfläche/Verlauf in der Auswahl - ohne PNG-Objekt.</summary>
        Private Shared Function CompositeVisibleFill(processed As SKBitmap, mask As SKBitmap, layer As MaskedAdjustmentLayer) As SKBitmap
            If processed Is Nothing OrElse mask Is Nothing OrElse layer Is Nothing Then Return Nothing
            Dim w = processed.Width, h = processed.Height
            If mask.Width <> w OrElse mask.Height <> h Then Return Nothing

            ' Bounding-Box der Maske, damit der Verlauf die Auswahl umspannt (nicht das ganze Bild).
            Dim mStride = mask.RowBytes
            Dim mBuf = New Byte(mStride * h - 1) {}
            Marshal.Copy(mask.GetPixels(), mBuf, 0, mBuf.Length)
            Dim minX = w, minY = h, maxX = -1, maxY = -1
            For y = 0 To h - 1
                Dim mRow = y * mStride
                For x = 0 To w - 1
                    If mBuf(mRow + x) > 0 Then
                        If x < minX Then minX = x
                        If x > maxX Then maxX = x
                        If y < minY Then minY = y
                        If y > maxY Then maxY = y
                    End If
                Next
            Next
            If maxX < minX OrElse maxY < minY Then Return Nothing

            Dim col = ParseColor(layer.FillColor, SKColors.White)
            Using fill = New SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul)
                Using canvas = New SKCanvas(fill)
                    canvas.Clear(SKColors.Transparent)
                    Dim rect = New SKRect(minX, minY, maxX + 1, maxY + 1)
                    Dim nk = If(layer.FillKind, "Solid").Trim().ToLowerInvariant()
                    If nk = "lineargradient" OrElse nk = "radialgradient" Then
                        Dim col2 = ParseColor(layer.FillColor2, col)
                        Using shader = CreateFillGradientShader(rect, nk, col, col2, CSng(layer.FillAngle), layer.FillInverted)
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

                ' Deckung = Maskenwert einmultiplizieren (premultipliziert: alle Kanäle skalieren).
                Dim fStride = fill.RowBytes
                Dim fBuf = New Byte(fStride * h - 1) {}
                Marshal.Copy(fill.GetPixels(), fBuf, 0, fBuf.Length)
                For y = 0 To h - 1
                    Dim fRow = y * fStride, mRow = y * mStride
                    For x = 0 To w - 1
                        Dim m = CInt(mBuf(mRow + x))
                        If m < 255 Then
                            Dim o = fRow + x * 4
                            fBuf(o) = CByte(CInt(fBuf(o)) * m \ 255)
                            fBuf(o + 1) = CByte(CInt(fBuf(o + 1)) * m \ 255)
                            fBuf(o + 2) = CByte(CInt(fBuf(o + 2)) * m \ 255)
                            fBuf(o + 3) = CByte(CInt(fBuf(o + 3)) * m \ 255)
                        End If
                    Next
                Next
                Marshal.Copy(fBuf, 0, fill.GetPixels(), fBuf.Length)

                Dim result = CloneBitmap(processed)
                Using canvas = New SKCanvas(result)
                    canvas.DrawBitmap(fill, 0, 0)   ' SrcOver: Füllung über das Bild
                End Using
                Return result
            End Using
        End Function

        ''' <summary>Projiziert eine SourceSpace-Maske durch exakt dieselbe Geometriekette wie das Bild:
        ''' Preview-Skalierung, Crop, Quarter-Turn/Flip, Begradigung, Resize und Canvas-Offset.</summary>
        ''' <summary>Fingerabdruck NUR der Pixelwerte einer Anpassung. Zwei Ebenen, die gemeinsam
        ''' angepasst wurden, tragen danach denselben - und genau daran werden sie beim Rendern
        ''' wieder zusammengefuehrt.</summary>
        Private Shared Function PixelAdjustmentsFingerprint(adjustments As ImageAdjustments) As String
            If adjustments Is Nothing Then Return ""
            Try
                Return System.Text.Json.JsonSerializer.Serialize(adjustments.ExtractPixelAdjustments())
            Catch
                ' Nicht serialisierbar: dann lieber NICHT zusammenfassen (jede Ebene bekommt einen
                ' eigenen Schluessel) - das ist das bisherige Verhalten und nie schlechter als falsch.
                Return Guid.NewGuid().ToString("N")
            End Try
        End Function

        ''' <summary>Exakte Ausgabemaße der Geometriekette ohne Pixelanpassungen/Objekte.</summary>
        Public Shared Function ComputeGeometryOutputSize(sourceWidth As Integer, sourceHeight As Integer,
                                                         adj As ImageAdjustments) As SKSizeI
            If sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return New SKSizeI(0, 0)
            Dim crop = ComputeGeometryCropRect(sourceWidth, sourceHeight, adj)
            Dim w = crop.Width, h = crop.Height
            Dim q = ImageGeometryMapper.NormalizeQuarterTurn(adj.RotationDegrees)
            If q = 90 OrElse q = 270 Then
                Dim swap = w : w = h : h = swap
            End If
            If Math.Abs(adj.StraightenDegrees) >= 0.01F AndAlso adj.StraightenExpandCanvas Then
                Dim radians = Math.Abs(adj.StraightenDegrees) * Math.PI / 180.0
                Dim oldW = w, oldH = h
                w = Math.Max(1, CInt(Math.Ceiling(oldW * Math.Cos(radians) + oldH * Math.Sin(radians))))
                h = Math.Max(1, CInt(Math.Ceiling(oldW * Math.Sin(radians) + oldH * Math.Cos(radians))))
            End If
            Dim resizeW = adj.ResizeWidth, resizeH = adj.ResizeHeight
            If resizeW > 0 OrElse resizeH > 0 Then
                If resizeW <= 0 Then resizeW = CInt(Math.Round(w * (resizeH / CDbl(h))))
                If resizeH <= 0 Then resizeH = CInt(Math.Round(h * (resizeW / CDbl(w))))
                w = Math.Max(1, resizeW) : h = Math.Max(1, resizeH)
            End If
            If adj.CanvasWidth > 0 Then w = adj.CanvasWidth
            If adj.CanvasHeight > 0 Then h = adj.CanvasHeight
            Return New SKSizeI(Math.Max(1, w), Math.Max(1, h))
        End Function

        Friend Shared Function ComputeGeometryCropRect(sourceWidth As Integer, sourceHeight As Integer,
                                                        adj As ImageAdjustments) As SKRectI
            Dim left = Math.Max(0, Math.Min(CInt(Math.Round(sourceWidth * Clamp(adj.CropLeftPercent, 0, 100) / 100.0F)), sourceWidth - 1))
            Dim top = Math.Max(0, Math.Min(CInt(Math.Round(sourceHeight * Clamp(adj.CropTopPercent, 0, 100) / 100.0F)), sourceHeight - 1))
            Dim right = Math.Max(left + 1, Math.Min(sourceWidth - CInt(Math.Round(sourceWidth * Clamp(adj.CropRightPercent, 0, 100) / 100.0F)), sourceWidth))
            Dim bottom = Math.Max(top + 1, Math.Min(sourceHeight - CInt(Math.Round(sourceHeight * Clamp(adj.CropBottomPercent, 0, 100) / 100.0F)), sourceHeight))
            Return New SKRectI(left, top, right, bottom)
        End Function

        ''' <summary>Bildet einen Punkt des unbeschnittenen SourceSpace durch dieselbe Geometriekette
        ''' wie der Renderer ab. False bedeutet: Der Punkt wurde vom Crop entfernt.
        ''' Public, weil der Editor dieselbe VOLLSTÄNDIGE Abbildung braucht für
        ''' Pinsel-/Retusche-Overlays - die Mapper-Kurzform (nur Drehung/Flip) saß nach
        ''' angewendetem Crop/Resize/Canvas daneben.</summary>
        Public Shared Function TrySourcePointToGeometryOutput(sourceX As Double, sourceY As Double,
                                                              sourceWidth As Integer, sourceHeight As Integer,
                                                              adj As ImageAdjustments, ByRef output As SKPoint) As Boolean
            ' --- Verzerren (Knotenraster) --- ZUERST, wie in der Bildkette: das Raster liegt im
            ' unbeschnittenen Quellraum, der Beschnitt greift erst auf dem verzogenen Bild.
            Dim warpedSource = WarpSourcePoint(sourceX, sourceY, sourceWidth, sourceHeight, adj)
            sourceX = warpedSource.X : sourceY = warpedSource.Y

            Dim crop = ComputeGeometryCropRect(sourceWidth, sourceHeight, adj)
            If sourceX < crop.Left OrElse sourceY < crop.Top OrElse sourceX >= crop.Right OrElse sourceY >= crop.Bottom Then Return False
            Dim x = sourceX - crop.Left, y = sourceY - crop.Top
            Dim w As Double = crop.Width, h As Double = crop.Height

            Dim p = ImageGeometryMapper.SourcePointToDisplay(x, y, w, h, adj.RotationDegrees,
                                                              adj.FlipHorizontal, adj.FlipVertical)
            x = p.X : y = p.Y
            Dim q = ImageGeometryMapper.NormalizeQuarterTurn(adj.RotationDegrees)
            If q = 90 OrElse q = 270 Then
                Dim swap = w : w = h : h = swap
            End If

            If Math.Abs(adj.StraightenDegrees) >= 0.01F Then
                Dim radians = adj.StraightenDegrees * Math.PI / 180.0
                Dim absRadians = Math.Abs(adj.StraightenDegrees) * Math.PI / 180.0
                Dim outW = w, outH = h, scale = 1.0
                If adj.StraightenExpandCanvas Then
                    outW = Math.Max(1, Math.Ceiling(w * Math.Cos(absRadians) + h * Math.Sin(absRadians)))
                    outH = Math.Max(1, Math.Ceiling(w * Math.Sin(absRadians) + h * Math.Cos(absRadians)))
                Else
                    scale = Math.Max(w / (w * Math.Cos(absRadians) + h * Math.Sin(absRadians)),
                                     h / (w * Math.Sin(absRadians) + h * Math.Cos(absRadians)))
                    scale = Math.Max(1.0, scale)
                End If
                Dim dx = x - w / 2.0, dy = y - h / 2.0
                Dim cosA = Math.Cos(radians), sinA = Math.Sin(radians)
                x = outW / 2.0 + scale * (cosA * dx - sinA * dy)
                y = outH / 2.0 + scale * (sinA * dx + cosA * dy)
                w = outW : h = outH
                ' Ohne Leinwand-Erweiterung schneidet die Begradigung die gedrehten Ecken
                ' ab. Der Hinweg darf sie nicht als Anzeigeort ausgeben: der Rueckweg
                ' weist denselben Punkt sonst zu Recht ab, und Verlauf-/Maskengriffe
                ' erscheinen neben dem Bild.
                If Not TryClampToRange(x, w) OrElse Not TryClampToRange(y, h) Then Return False
            End If

            ' --- Verzerren --- (dieselbe Stelle wie im Bildweg: nach der Begradigung, vor dem
            ' Skalieren; die Stufe laesst die Masse unveraendert, deshalb aendert sich hier nur der
            ' Punkt und nicht w/h)
            Dim warp = ImageGeometryMapper.WarpMatrix(w, h,
                                 adj.PerspectiveHorizontal, adj.PerspectiveVertical,
                                 adj.PerspectiveAspect, adj.PerspectiveScale,
                                 ImageGeometryMapper.CornerOffset(adj))
            If Not warp.IsIdentity Then
                Dim v = warp.MapPoint(New SKPoint(CSng(x), CSng(y)))
                x = v.X : y = v.Y
                ' Was aus dem Rahmen kippt, wird von der Stufe abgeschnitten - fuer so einen Punkt
                ' gibt es im Ausgabebild keine Stelle. Ohne diese Pruefung meldete der Hinweg einen
                ' Punkt ausserhalb des Bildes als gueltig, und der Rueckweg wiese ihn ab.
                If Not TryClampToRange(x, w) OrElse Not TryClampToRange(y, h) Then Return False
            End If

            Dim resizeW = adj.ResizeWidth, resizeH = adj.ResizeHeight
            If resizeW > 0 OrElse resizeH > 0 Then
                If resizeW <= 0 Then resizeW = CInt(Math.Round(w * (resizeH / h)))
                If resizeH <= 0 Then resizeH = CInt(Math.Round(h * (resizeW / w)))
                resizeW = Math.Max(1, resizeW) : resizeH = Math.Max(1, resizeH)
                x *= resizeW / w : y *= resizeH / h
                w = resizeW : h = resizeH
            End If

            Dim canvasW = If(adj.CanvasWidth > 0, adj.CanvasWidth, CInt(Math.Round(w)))
            Dim canvasH = If(adj.CanvasHeight > 0, adj.CanvasHeight, CInt(Math.Round(h)))
            If canvasW <> CInt(Math.Round(w)) OrElse canvasH <> CInt(Math.Round(h)) Then
                Dim offsetX As Double, offsetY As Double
                Select Case If(adj.CanvasAnchor, "Center").Trim().ToLowerInvariant()
                    Case "top-left", "left-top" : offsetX = 0 : offsetY = 0
                    Case "top", "top-center" : offsetX = (canvasW - w) / 2.0 : offsetY = 0
                    Case "top-right", "right-top" : offsetX = canvasW - w : offsetY = 0
                    Case "left", "middle-left" : offsetX = 0 : offsetY = (canvasH - h) / 2.0
                    Case "right", "middle-right" : offsetX = canvasW - w : offsetY = (canvasH - h) / 2.0
                    Case "bottom-left", "left-bottom" : offsetX = 0 : offsetY = canvasH - h
                    Case "bottom", "bottom-center" : offsetX = (canvasW - w) / 2.0 : offsetY = canvasH - h
                    Case "bottom-right", "right-bottom" : offsetX = canvasW - w : offsetY = canvasH - h
                    Case Else : offsetX = (canvasW - w) / 2.0 : offsetY = (canvasH - h) / 2.0
                End Select
                x += offsetX : y += offsetY
            End If
            ' Eine kleinere Leinwand ist ein weiterer Beschnitt. Erst hier sind die
            ' endgueltigen Ausgabemasse bekannt; Punkte ausserhalb haben im sichtbaren
            ' Bild keinen Ort und muessen wie beim Rueckweg abgewiesen werden.
            If Not TryClampToRange(x, canvasW) OrElse Not TryClampToRange(y, canvasH) Then Return False
            output = New SKPoint(CSng(x), CSng(y))
            Return True
        End Function

        ''' <summary>EXAKTE Inverse von <see cref="TrySourcePointToGeometryOutput"/>:
        ''' bildet einen Punkt des AUSGABE-Raums (Anzeigebild nach Crop/Vierteldrehung/Begradigung/
        ''' Resize/Canvas) zurück auf den unbeschnittenen SourceSpace. Jede Stufe wird in umgekehrter
        ''' Reihenfolge mit den IDENTISCHEN Maß-Formeln (inkl. Rundungen) abgelöst, damit Hin- und
        ''' Rückweg dieselben Zwischengrößen sehen. False bedeutet: der Punkt liegt außerhalb des
        ''' Bildinhalts (Canvas-Rand, leere Begradigungs-Ecke) oder des Crop-Ausschnitts - dort gibt
        ''' es keinen Source-Pixel, ein Backen (Pinsel/Retusche) muss den Punkt überspringen.
        ''' Randtoleranz: bis 0,5 px außerhalb wird auf die Kante geklemmt (Rundungsrauschen der
        ''' Anzeige), erst darüber hinaus ist der Punkt wirklich "neben dem Bild".</summary>
        Public Shared Function TryGeometryOutputToSourcePoint(outputX As Double, outputY As Double,
                                                              sourceWidth As Integer, sourceHeight As Integer,
                                                              adj As ImageAdjustments, ByRef source As SKPoint) As Boolean
            If sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse adj Is Nothing Then Return False
            Dim crop = ComputeGeometryCropRect(sourceWidth, sourceHeight, adj)
            Dim w As Double = crop.Width, h As Double = crop.Height

            ' Maße der Kette VORWÄRTS nachvollziehen (gleiche Formeln wie oben), um sie rückwärts
            ' Stufe für Stufe abzulösen.
            Dim q = ImageGeometryMapper.NormalizeQuarterTurn(adj.RotationDegrees)
            Dim rotW = If(q = 90 OrElse q = 270, h, w)
            Dim rotH = If(q = 90 OrElse q = 270, w, h)

            Dim hasStraighten = Math.Abs(adj.StraightenDegrees) >= 0.01F
            Dim outW = rotW, outH = rotH, scale = 1.0
            If hasStraighten Then
                Dim absRadians = Math.Abs(adj.StraightenDegrees) * Math.PI / 180.0
                If adj.StraightenExpandCanvas Then
                    outW = Math.Max(1, Math.Ceiling(rotW * Math.Cos(absRadians) + rotH * Math.Sin(absRadians)))
                    outH = Math.Max(1, Math.Ceiling(rotW * Math.Sin(absRadians) + rotH * Math.Cos(absRadians)))
                Else
                    scale = Math.Max(rotW / (rotW * Math.Cos(absRadians) + rotH * Math.Sin(absRadians)),
                                     rotH / (rotW * Math.Sin(absRadians) + rotH * Math.Cos(absRadians)))
                    scale = Math.Max(1.0, scale)
                End If
            End If

            Dim resizeW = adj.ResizeWidth, resizeH = adj.ResizeHeight
            If resizeW > 0 OrElse resizeH > 0 Then
                If resizeW <= 0 Then resizeW = CInt(Math.Round(outW * (resizeH / outH)))
                If resizeH <= 0 Then resizeH = CInt(Math.Round(outH * (resizeW / outW)))
                resizeW = Math.Max(1, resizeW) : resizeH = Math.Max(1, resizeH)
            Else
                resizeW = 0 : resizeH = 0
            End If
            Dim afterResizeW = If(resizeW > 0, CDbl(resizeW), outW)
            Dim afterResizeH = If(resizeH > 0, CDbl(resizeH), outH)

            Dim x = outputX, y = outputY

            ' --- Canvas-Offset zurück ---
            Dim canvasW = If(adj.CanvasWidth > 0, adj.CanvasWidth, CInt(Math.Round(afterResizeW)))
            Dim canvasH = If(adj.CanvasHeight > 0, adj.CanvasHeight, CInt(Math.Round(afterResizeH)))
            If canvasW <> CInt(Math.Round(afterResizeW)) OrElse canvasH <> CInt(Math.Round(afterResizeH)) Then
                Dim offsetX As Double, offsetY As Double
                Select Case If(adj.CanvasAnchor, "Center").Trim().ToLowerInvariant()
                    Case "top-left", "left-top" : offsetX = 0 : offsetY = 0
                    Case "top", "top-center" : offsetX = (canvasW - afterResizeW) / 2.0 : offsetY = 0
                    Case "top-right", "right-top" : offsetX = canvasW - afterResizeW : offsetY = 0
                    Case "left", "middle-left" : offsetX = 0 : offsetY = (canvasH - afterResizeH) / 2.0
                    Case "right", "middle-right" : offsetX = canvasW - afterResizeW : offsetY = (canvasH - afterResizeH) / 2.0
                    Case "bottom-left", "left-bottom" : offsetX = 0 : offsetY = canvasH - afterResizeH
                    Case "bottom", "bottom-center" : offsetX = (canvasW - afterResizeW) / 2.0 : offsetY = canvasH - afterResizeH
                    Case "bottom-right", "right-bottom" : offsetX = canvasW - afterResizeW : offsetY = canvasH - afterResizeH
                    Case Else : offsetX = (canvasW - afterResizeW) / 2.0 : offsetY = (canvasH - afterResizeH) / 2.0
                End Select
                x -= offsetX : y -= offsetY
            End If
            If Not TryClampToRange(x, afterResizeW) OrElse Not TryClampToRange(y, afterResizeH) Then Return False

            ' --- Resize zurück ---
            If resizeW > 0 Then
                x *= outW / afterResizeW
                y *= outH / afterResizeH
            End If

            ' --- Verzerren zurück --- (Gegenstueck zur Vorwaertsstufe oben; die Umkehrmatrix
            ' existiert immer, solange die Homographie nicht entartet ist)
            ' outW/outH, NICHT rotW/rotH: an dieser Stelle liegt der Punkt im Raum NACH der
            ' Begradigung, und genau darauf rechnet die Vorwaertsstufe.
            Dim warp = ImageGeometryMapper.WarpMatrix(outW, outH,
                                 adj.PerspectiveHorizontal, adj.PerspectiveVertical,
                                 adj.PerspectiveAspect, adj.PerspectiveScale,
                                 ImageGeometryMapper.CornerOffset(adj))
            If Not warp.IsIdentity Then
                Dim umkehr As SKMatrix = Nothing
                If Not warp.TryInvert(umkehr) Then Return False
                Dim v = umkehr.MapPoint(New SKPoint(CSng(x), CSng(y)))
                x = v.X : y = v.Y
            End If

            ' --- Begradigung zurück (invers: erst ent-drehen, dann ent-skalieren) ---
            If hasStraighten Then
                Dim radians = adj.StraightenDegrees * Math.PI / 180.0
                Dim cosA = Math.Cos(radians), sinA = Math.Sin(radians)
                Dim dx = x - outW / 2.0, dy = y - outH / 2.0
                Dim ux = (cosA * dx + sinA * dy) / scale
                Dim uy = (-sinA * dx + cosA * dy) / scale
                x = rotW / 2.0 + ux
                y = rotH / 2.0 + uy
                If Not TryClampToRange(x, rotW) OrElse Not TryClampToRange(y, rotH) Then Return False
            End If

            ' --- Vierteldrehung/Flip zurück (auf den beschnittenen Ausschnitt bezogen) ---
            Dim p = ImageGeometryMapper.DisplayPointToSource(x, y, w, h, adj.RotationDegrees,
                                                             adj.FlipHorizontal, adj.FlipVertical)
            Dim sx As Double = p.X, sy As Double = p.Y
            If Not TryClampToRange(sx, w) OrElse Not TryClampToRange(sy, h) Then Return False

            ' --- Verzerren (Knotenraster) zurueck --- als LETZTES, weil es in der Kette das ERSTE
            ' war. False heisst hier: an dieser Stelle liegt nach der Verzerrung kein Bildinhalt
            ' mehr, es gibt also keinen Quellpunkt - genau wie neben dem Beschnitt.
            Dim beforeWarp As SKPoint = Nothing
            If Not TryUnwarpSourcePoint(sx + crop.Left, sy + crop.Top, sourceWidth, sourceHeight, adj, beforeWarp) Then Return False
            source = beforeWarp
            Return True
        End Function

        ''' <summary>Wohin ein Punkt des unbeschnittenen Quellraums durch die Rasterverzerrung
        ''' wandert. Ohne Verzerrung bleibt er, wo er ist.</summary>
        Private Shared Function WarpSourcePoint(x As Double, y As Double,
                                                sourceWidth As Integer, sourceHeight As Integer,
                                                adj As ImageAdjustments) As SKPoint
            Dim v = adj?.ImageWarp
            If v Is Nothing OrElse v.IsEmpty OrElse Not String.Equals(v.Kind, "Gitter", StringComparison.Ordinal) Then
                Return New SKPoint(CSng(x), CSng(y))
            End If
            Return ImageGeometryMapper.MeshPoint(v.Nodes, v.Columns, v.Rows, x, y, sourceWidth, sourceHeight)
        End Function

        ''' <summary>Gegenrichtung zu <see cref="WarpSourcePoint"/>.</summary>
        Private Shared Function TryUnwarpSourcePoint(x As Double, y As Double,
                                                     sourceWidth As Integer, sourceHeight As Integer,
                                                     adj As ImageAdjustments, ByRef source As SKPoint) As Boolean
            Dim v = adj?.ImageWarp
            If v Is Nothing OrElse v.IsEmpty OrElse Not String.Equals(v.Kind, "Gitter", StringComparison.Ordinal) Then
                source = New SKPoint(CSng(x), CSng(y))
                Return True
            End If
            Return ImageGeometryMapper.MeshInversePoint(v.Nodes, v.Columns, v.Rows, x, y,
                                                        sourceWidth, sourceHeight, source)
        End Function

        ''' Klemmt einen Wert in [0, size): bis 0,5 px außerhalb ist Rundungsrauschen und wird auf
        ''' die Kante gelegt, darüber hinaus liegt der Punkt echt außerhalb (False).
        Private Shared Function TryClampToRange(ByRef value As Double, size As Double) As Boolean
            If value < -0.5 OrElse value > size + 0.5 Then Return False
            If value < 0 Then value = 0
            If value >= size Then value = Math.Max(0.0, size - 0.001)
            Return True
        End Function

        ''' <summary>Gibt das gecachte Basis-Bitmap frei. Muss aufgerufen werden, sobald die zugehörige
        ''' Quelle verschwindet (Bildwechsel, Editor verlassen) - der Cache ist statisch und hielte sonst
        ''' ein Bitmap in Vorschauauflösung sowie eine Referenz auf das bereits disposte Quell-SKBitmap
        ''' bis zum Programmende fest.</summary>
        Public Shared Sub ClearBaseCache()
            SyncLock _baseCacheLock
                _baseCacheBitmap?.Dispose()
                _baseCacheBitmap = Nothing
                _baseCacheKey = Nothing
                _baseCacheSourceRef = Nothing
            End SyncLock
        End Sub

        ' Liefert die gecachte Basis (Bild vor den Objekten) wenn sich seit dem letzten Aufruf nur
        ' die Objekte geändert haben, sonst wird die Pipeline neu berechnet und der Cache erneuert.
        Private Shared Function GetOrComputeBaseLocked(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim key = ComputeBaseKey(adj)
            If Object.ReferenceEquals(_baseCacheSourceRef, source) AndAlso
               String.Equals(_baseCacheKey, key, StringComparison.Ordinal) AndAlso
               _baseCacheBitmap IsNot Nothing Then
                Return _baseCacheBitmap
            End If

            Dim computed = ProcessBitmapBase(source, adj)
            _baseCacheBitmap?.Dispose()
            _baseCacheBitmap = computed
            _baseCacheKey = key
            _baseCacheSourceRef = source
            Return computed
        End Function

        ' Signatur aller Anpassungen AUSSER Annotations - solange sie sich nicht ändert, kann die
        ' gecachte Basis wiederverwendet werden. Friend: der Editor nutzt sie auch als
        ' Gültigkeitsstempel der vorgewärmten Retusche-Live-Puffer.
        ''' <summary>Kulturunabhaengige Textform eines Schluesselbestandteils. String.Join ruft sonst
        ''' das implizite ToString auf, und das formatiert Single/Double nach der aktuellen Kultur -
        ''' "0,5" hier und "0.5" dort. Der Schluessel ist zwar nur sitzungsintern, aber ein
        ''' Kulturwechsel zur Laufzeit (Spracheinstellung) wuerde den Cache stillschweigend
        ''' entwerten oder - schlimmer - zwei verschiedene Einstellungen gleich benennen.</summary>
        Private Shared Function KeyPart(value As Object) As String
            If value Is Nothing Then Return ""
            Dim f = TryCast(value, IFormattable)
            If f IsNot Nothing Then Return f.ToString(Nothing, Globalization.CultureInfo.InvariantCulture)
            Return value.ToString()
        End Function

        Friend Shared Function ComputeBaseKey(adj As ImageAdjustments) As String
            ' HasActiveSelection und ihre editierbare Display-Maske sind reine UI-Zustände. Seit
            ' Auswahlkorrekturen als persistente MaskedAdjustmentLayers gespeichert werden, wirken
            ' sie nur noch dann direkt auf die globale Pixelpipeline, wenn der explizite Legacy-
            ' SelectionScopeEnabled gesetzt ist. Die UI-Auswahl trotzdem in den Base-Key aufzunehmen
            ' machte den Cache unmittelbar nach jedem Zauberstab-/Lasso-Klick scheinbar veraltet,
            ' obwohl sich kein Bildpixel geändert hatte. Ein danach platziertes Objekt konnte deshalb
            ' nie als Region gerendert werden und war nur im Selektions-Ghost zu sehen.
            Dim selectionScopeKey = If(adj.SelectionScopeEnabled,
                String.Join(",", New Object() {
                    adj.SelectionXPercent, adj.SelectionYPercent,
                    adj.SelectionWidthPercent, adj.SelectionHeightPercent, adj.SelectionShapeMode,
                    adj.SelectionMaskLeft, adj.SelectionMaskTop, adj.SelectionMaskRight, adj.SelectionMaskBottom,
                    SelectionMaskFingerprint(adj.SelectionMaskPngBase64), adj.SelectionFeatherPixels
                }.Select(AddressOf KeyPart)), "")
            ' JEDES Feld, das ProcessBitmapBase liest, MUSS hier stehen: die
            ' Untergruppen-Regler SharpenRadius/SharpenDetail, NoiseReductionDetail, GrainSize/
            ' GrainFrequency und VignetteStyle fehlten - wer NUR so einen Regler bewegte, bekam
            ' das gecachte alte Bild zurueck ("Regler macht nix"), und Patch-Renderer
            ' komponierten auf veralteter Basis. Das gilt auch fuer APP-WEITE Render-Schalter,
            ' nicht nur adj-Felder - wer einen einfuehrt, muss ihn hier eintragen.
            Return String.Join("|", New Object() {
                adj.Exposure, adj.Brightness, adj.Contrast, adj.Saturation, adj.Highlights, adj.ShadowsLevel,
                adj.Whites, adj.Blacks, adj.Temperature, adj.Tint, adj.Sharpness, adj.SharpenRadius, adj.SharpenDetail,
                adj.SharpenMasking,
                adj.NoiseReduction, adj.NoiseReductionMethod, adj.NoiseReductionDetail, adj.ColorNoiseReduction,
                adj.FarbrauschGrob, adj.ColorNoiseCoarseScale,
                adj.ColorNoiseAdd,
                adj.DustScratches, adj.Haze, adj.AddNoise, adj.[Structure], adj.Glow,
                adj.PerspectiveHorizontal, adj.PerspectiveVertical, adj.PerspectiveAspect, adj.PerspectiveScale,
                adj.PerspectiveCorner0X, adj.PerspectiveCorner0Y, adj.PerspectiveCorner1X, adj.PerspectiveCorner1Y,
                adj.PerspectiveCorner2X, adj.PerspectiveCorner2Y, adj.PerspectiveCorner3X, adj.PerspectiveCorner3Y,
                ImageWarpSignature(adj.ImageWarp),
adj.CalibrationRedHue, adj.CalibrationRedSaturation,
                adj.CalibrationGreenHue, adj.CalibrationGreenSaturation,
                adj.CalibrationBlueHue, adj.CalibrationBlueSaturation, adj.CalibrationShadowTint,
                                adj.Vibrance, adj.Vignette, adj.VignetteTransition, adj.VignetteRoundness, adj.VignetteFeather,
                adj.VignetteCenterX, adj.VignetteCenterY, adj.VignetteStyle,
                adj.Grain, adj.GrainSize, adj.GrainFrequency, adj.GrainColor, adj.Clarity,
                adj.NegativeEnabled, adj.NegativeMonochrome, adj.NegativeBaseColor, adj.NegativeDensityColor, adj.NegativeGamma,
                adj.CurveRgbPoints, adj.CurveRedPoints, adj.CurveGreenPoints, adj.CurveBluePoints, adj.CurveLuminancePoints,
                adj.RedHue, adj.RedSaturation, adj.RedLuminance, adj.OrangeHue, adj.OrangeSaturation, adj.OrangeLuminance,
                adj.YellowHue, adj.YellowSaturation, adj.YellowLuminance, adj.GreenHue, adj.GreenSaturation, adj.GreenLuminance,
                adj.AquaHue, adj.AquaSaturation, adj.AquaLuminance, adj.BlueHue, adj.BlueSaturation, adj.BlueLuminance,
                adj.PurpleHue, adj.PurpleSaturation, adj.PurpleLuminance, adj.MagentaHue, adj.MagentaSaturation, adj.MagentaLuminance,
                adj.ColorGradeShadowHue, adj.ColorGradeShadowSaturation, adj.ColorGradeShadowLuminance,
                adj.ColorGradeMidtoneHue, adj.ColorGradeMidtoneSaturation, adj.ColorGradeMidtoneLuminance,
                adj.ColorGradeHighlightHue, adj.ColorGradeHighlightSaturation, adj.ColorGradeHighlightLuminance,
                adj.ColorGradeGlobalHue, adj.ColorGradeGlobalSaturation, adj.ColorGradeGlobalLuminance,
                adj.ColorGradeBalance, adj.ColorGradeBlending,
                adj.RotationDegrees, adj.StraightenDegrees, adj.StraightenExpandCanvas, adj.FlipHorizontal, adj.FlipVertical,
                adj.CropLeftPercent, adj.CropTopPercent, adj.CropRightPercent, adj.CropBottomPercent,
                adj.ResizeWidth, adj.ResizeHeight, adj.LockResizeAspect, adj.ResizeFitInsideBox, adj.ResizeScalePercent, adj.NoResizeUpscale, adj.ResizeInterpolation,
                adj.CanvasWidth, adj.CanvasHeight, adj.LockCanvasAspect, adj.CanvasAnchor, adj.CanvasBackgroundColor,
                adj.FilterPreset, adj.FilterStrength, adj.LutPath, adj.LutStrength,
                adj.SelectionScopeEnabled, selectionScopeKey,
                adj.GlobalAdjustmentsHidden,
                PersistentMasksFingerprint(adj),
                adj.WorkingImageVersion
            }.Select(AddressOf KeyPart))
        End Function

        ''' <summary>Fingerabdruck EINER Maske. Ausgelagert, weil neben dem Basis-Schlüssel auch der
        ''' Deckungs-Speicher der Objekt-Ebenenmasken ihn braucht - zwei Listen derselben Felder
        ''' liefen auseinander, und dann gäbe eine der beiden Seiten nach einem Pinselstrich die
        ''' alte Maske zurück.
        '''
        ''' Verlaufsmasken tragen ihre Geometrie statt eines PNG - sie MUSS mit hinein, sonst bliebe
        ''' die Vorschau beim Ziehen der Griffe stehen (der Cache gäbe die alte Basis zurück, und das
        ''' Werkzeug „macht nichts"). Dasselbe gilt für die Pinselkorrektur eines Verlaufs: ohne sie
        ''' bliebe die Vorschau beim Malen stehen.</summary>
        ''' <remarks>Oeffentlich, weil auch die Miniatur im Ebenenpanel daran erkennt, dass sie neu
        ''' gezeichnet werden muss - zwei Fingerabdruecke fuer dieselbe Maske liefen unweigerlich
        ''' auseinander, und dann zeigte die Zeile die Maske von vorhin.</remarks>
        Public Shared Function MaskFingerprint(m As ImageMask) As String
            ' JEDER Bestandteil gehoert hinein, nicht nur der erste: sonst gaebe der Cache nach dem
            ' Anhaengen eines Verlaufs an dieselbe Maske das alte Bild zurueck, und das Werkzeug
            ' "macht nichts".
            ' Die DICHTE gehoert dazu: sie liegt an der Maske und nicht an einem Bestandteil, und
            ' ohne sie gaebe der Zwischenspeicher nach dem Verschieben des Reglers das alte Bild
            ' zurueck - der Regler "macht nichts".
            Return String.Join(":", m.Id, m.SourceWidthPixels, m.SourceHeightPixels,
                               KeyPart(m.Density), m.IsDisabled, m.InvertResult) & ":" &
                   String.Join("/", m.GetComponents().Select(AddressOf MaskComponentFingerprint))
        End Function

        ''' <summary>Der Fingerabdruck EINES Rasters - fuer Zwischenspeicher ausserhalb des
        ''' Prozessors, die ein einzelnes Raster wiedererkennen muessen (das rote Overlay). Sie
        ''' benutzten dafuer <c>String.GetHashCode</c>, und der kollidiert grundsaetzlich; hier
        ''' laeuft derselbe gepufferte SHA-Weg wie im Rendercache.</summary>
        Public Shared Function MaskRasterFingerprint(base64 As String) As String
            Return SelectionMaskFingerprint(base64)
        End Function

        Private Shared Function MaskComponentFingerprint(c As MaskComponent) As String
            Return String.Join(":", c.Mode, c.IsVisible, c.Left, c.Top, c.Right, c.Bottom, c.FeatherPixels,
                               c.Inverted, SelectionMaskFingerprint(c.PngBase64),
                               c.Kind, KeyPart(c.GradientStartXPercent), KeyPart(c.GradientStartYPercent),
                               KeyPart(c.GradientEndXPercent), KeyPart(c.GradientEndYPercent),
                               KeyPart(c.GradientRadiusRatio), KeyPart(c.GradientFeatherPercent),
                               c.BrushLeft, c.BrushTop, c.BrushRight, c.BrushBottom,
                               SelectionMaskFingerprint(c.BrushAddPngBase64),
                               SelectionMaskFingerprint(c.BrushSubtractPngBase64))
        End Function

        Private Shared Function PersistentMasksFingerprint(adj As ImageAdjustments) As String
            Dim masks = If(adj.Masks, New List(Of ImageMask)()).
                Where(Function(m) m IsNot Nothing).
                Select(AddressOf MaskFingerprint)
            ' Ebenen IM OBJEKTSTAPEL gehören NICHT in den Basis-Schlüssel: die Basis-Stufe überspringt
            ' sie (ApplyMaskedAdjustmentLayers ohne onlyStackedAboveId), sie wirken erst im
            ' Objektdurchlauf. Stünden sie hier, würde jede Änderung an ihnen den Basis-Cache
            ' wegwerfen - und der Zug, der sie für die Live-Darstellung kurz weglässt, würde ihn
            ' gleich zweimal neu aufbauen.
            Dim layers = If(adj.MaskedAdjustmentLayers, New List(Of MaskedAdjustmentLayer)()).
                Where(Function(l) l IsNot Nothing AndAlso String.IsNullOrEmpty(l.StackAboveAnnotationId)).
                Select(Function(l)
                           Dim values = If(l.Adjustments, New ImageAdjustments())
                           Dim pixelValues = ImageAdjustments.PixelAdjustmentProperties().
                               Select(Function(p) KeyPart(p.GetValue(values)))
                           ' IsMaskLayer und die DEKLARATIVE Füllung MÜSSEN mit in den Schlüssel: beide
                           ' liest ApplyMaskedAdjustmentLayers (Art der Füll-Wirkung bzw. Farbe/Verlauf).
                           ' Ohne sie lieferte der Basis-Cache nach einem erneuten Füllen dasselbe Bild
                           ' zurück - die ERSTE Füllung blieb sichtbar und liess sich nie ersetzen
                           ' (exakt die im Kopf von ComputeBaseKey beschriebene
                           ' Fehlerklasse "Regler macht nix").
                           ' Die Sichtbarkeit der GRUPPE gehört mit hinein: liegt die Korrektur in einer
                           ' Gruppe, entscheidet deren Auge mit darüber, ob sie gerendert wird
                           ' (IsMaskedLayerRenderVisible). Ohne sie war der Schlüssel vor und nach dem
                           ' Umschalten identisch - der Vollrender bekam die gecachte Basis zurück und
                           ' die Korrektur blieb sichtbar.
                           Dim groupVisible = adj.IsMaskedLayerRenderVisible(l)
                           Return String.Join(":", l.Id, l.MaskId, l.IsVisible, groupVisible, l.Opacity, l.GroupId,
                                              l.StackAboveAnnotationId,
                                              l.IsMaskLayer, l.FillKind, l.FillColor, l.FillColor2,
                                              KeyPart(l.FillAngle), l.FillInverted,
                                              String.Join(",", pixelValues))
                       End Function)
            Return String.Join(";", masks) & "|" & String.Join(";", layers)
        End Function

        ''' <summary>Stabiler Fingerabdruck der Auswahlmaske für den Basis-Cache-Schlüssel.
        '''
        ''' Vorher stand hier nur die LÄNGE der Base64-Zeichenkette. Zwei verschiedene Masken mit
        ''' gleicher Bounding-Box und gleicher Länge bekamen damit denselben Cache-Schlüssel - die
        ''' zweite Vorschau lief dann mit dem selektiv gerechneten Ergebnis der ERSTEN Maske. Das ist
        ''' ein echter Bildfehler, kein Performance-Thema.
        '''
        ''' Und es war nicht selten: an 63 lasso-artigen Masken mit identischer Bounding-Box gemessen
        ''' teilten sich 90,5 % ihre Base64-Länge mit einer anderen Maske - PNG-Kompression
        ''' quantisiert die Längen stark (drei verschiedene Masken lagen auf exakt 1092 Zeichen).
        '''
        ''' Gemerkt werden die letzten MEHREREN Werte, nicht nur der letzte: ComputeBaseKey läuft bei
        ''' jedem Vorschaubild und geht dabei über ALLE Masken, und der Deckungs-Speicher der
        ''' Objekt-Ebenenmasken fragt gleich danach eine bestimmte noch einmal. Mit nur einem Platz
        ''' verdrängt jede Maske die vorige, und ab der zweiten Maske wird in jedem Frame über eine
        ''' womöglich megabytegroße Zeichenkette gehasht.</summary>
        Private Class MaskFingerprintEntry
            Public Property Source As String
            Public Property Value As String
        End Class

        Private Shared ReadOnly _maskFingerprints As New List(Of MaskFingerprintEntry)()
        Private Shared ReadOnly _maskFingerprintLock As New Object()
        Private Const MaskFingerprintSlots As Integer = 12

        Private Shared Function SelectionMaskFingerprint(maskBase64 As String) As String
            If String.IsNullOrEmpty(maskBase64) Then Return "0"
            SyncLock _maskFingerprintLock
                For i = 0 To _maskFingerprints.Count - 1
                    Dim entry = _maskFingerprints(i)
                    ' Referenzgleichheit zuerst: waehrend eines Reglerzugs ist es dieselbe Instanz.
                    If Object.ReferenceEquals(entry.Source, maskBase64) OrElse
                       String.Equals(entry.Source, maskBase64, StringComparison.Ordinal) Then
                        ' Nach vorn holen: der zuletzt gebrauchte Eintrag wird als naechstes wieder
                        ' gebraucht, und der aelteste faellt unten heraus.
                        _maskFingerprints.RemoveAt(i)
                        _maskFingerprints.Insert(0, entry)
                        Return entry.Value
                    End If
                Next

                Dim hash As String
                Using sha = Security.Cryptography.SHA256.Create()
                    hash = Convert.ToHexString(sha.ComputeHash(Text.Encoding.ASCII.GetBytes(maskBase64)))
                End Using
                _maskFingerprints.Insert(0, New MaskFingerprintEntry With {.Source = maskBase64, .Value = hash})
                While _maskFingerprints.Count > MaskFingerprintSlots
                    _maskFingerprints.RemoveAt(_maskFingerprints.Count - 1)
                End While
                Return hash
            End SyncLock
        End Function

        Private Shared Function ReplaceBitmap(oldBitmap As SKBitmap, newBitmap As SKBitmap) As SKBitmap
            If newBitmap Is Nothing OrElse Object.ReferenceEquals(oldBitmap, newBitmap) Then Return oldBitmap
            oldBitmap.Dispose()
            Return newBitmap
        End Function

        ''' <summary>Wie <see cref="ReplaceBitmap"/>, aber für Ketten, die MIT DER FREMDEN QUELLE
        ''' beginnen dürfen: <paramref name="owned"/> sagt, ob das aktuelle Bild uns gehört und beim
        ''' Ersetzen entsorgt werden darf.
        '''
        ''' WARUM: Die Pixelkette begann an drei Stellen mit einem unbedingten Klon, damit jede Stufe
        ''' ihren Vorgänger entsorgen darf. Eine neutrale Stufe gibt ihre Quelle aber unverändert
        ''' zurück - bei einem Bild ohne gesetzte Regler wurde also dreimal das ganze Bild kopiert,
        ''' ohne dass sich ein Pixel ändert. Gemessen an 45 MP waren das 445 ms Grundlast, also genau
        ''' drei Vollkopien zu je rund 145 ms. Jetzt wird erst kopiert, wenn eine Stufe wirklich
        ''' etwas liefert; bleibt am Ende die Quelle stehen, klont der Aufrufer EINMAL.</summary>
        Private Shared Function ReplaceBitmapOwned(oldBitmap As SKBitmap, newBitmap As SKBitmap,
                                                   ByRef owned As Boolean) As SKBitmap
            If newBitmap Is Nothing OrElse Object.ReferenceEquals(oldBitmap, newBitmap) Then Return oldBitmap
            If owned Then oldBitmap.Dispose()
            owned = True
            Return newBitmap
        End Function

        ''' <summary>Schlusspunkt einer <see cref="ReplaceBitmapOwned"/>-Kette: sorgt dafür, dass der
        ''' Aufrufer IMMER ein eigenes Bitmap bekommt. Hat keine Stufe etwas geändert, steht hier noch
        ''' die fremde Quelle und wird einmal kopiert.</summary>
        Private Shared Function TakeOwnership(bitmap As SKBitmap, owned As Boolean) As SKBitmap
            If owned OrElse bitmap Is Nothing Then Return bitmap
            Return CloneBitmap(bitmap)
        End Function

        ''' Kopiert den rohen Pixelspeicher eines Bgra8888-Bitmaps in ein verwaltetes Byte-Array, damit
        ''' Pixel-Loops per Array-Index statt über SkiaSharps P/Invoke-lastiges GetPixel/SetPixel
        ''' laufen können (bei mehreren Millionen Pixeln ein erheblicher Geschwindigkeitsunterschied).
        ''' Liefert False bei jedem anderen Farbformat - die Pipeline erzeugt Bitmaps praktisch immer
        ''' als Bgra8888 (siehe DecodeOriented), Aufrufer müssen für diesen seltenen Fall aber weiterhin
        ''' auf GetPixel/SetPixel zurückfallen können.
        Private Shared Function TryBorrowBgraBuffer(bmp As SKBitmap, ByRef buffer As Byte(), ByRef stride As Integer) As Boolean
            buffer = Nothing
            stride = 0
            If bmp Is Nothing OrElse bmp.ColorType <> SKColorType.Bgra8888 Then Return False
            stride = bmp.RowBytes
            Dim length = stride * bmp.Height
            If length <= 0 Then Return False
            buffer = New Byte(length - 1) {}
            Marshal.Copy(bmp.GetPixels(), buffer, 0, length)
            Return True
        End Function

        Private Shared Sub CommitBgraBuffer(bmp As SKBitmap, buffer As Byte())
            Marshal.Copy(buffer, 0, bmp.GetPixels(), buffer.Length)
        End Sub

        ''' <summary>Die Entpremultiplikation, die <c>SKBitmap.GetPixel</c> auf einem Premul-Bitmap
        ''' vornimmt - als Tabelle über alle 256 mal 256 Kombinationen aus Kanalwert und Alpha.
        '''
        ''' WARUM EINE TABELLE UND KEINE FORMEL: Wer eine Pixelschleife von GetPixel/SetPixel auf einen
        ''' Byte-Puffer umstellt, muss diese Umrechnung selbst machen - und sie muss BITGLEICH sein,
        ''' sonst ändert der Umbau das Bild an teiltransparenten Stellen. Gemessen über alle 65536
        ''' Kombinationen trifft keine der naheliegenden Formeln: das übliche <c>v * 255 \ a</c> weicht
        ''' in 15760 Fällen um eine Stufe ab, eine Nachbildung der Reziprok-Tabelle in fast allen. Die
        ''' Tabelle wird deshalb aus Skia SELBST erhoben statt nachgerechnet. Das bleibt auch dann
        ''' richtig, wenn ein SkiaSharp-Wechsel die Rundung ändert.
        '''
        ''' Kosten: einmalig rund 20 ms beim ERSTEN Bedarf (Lazy), danach ein Tabellenzugriff je Pixel.
        ''' Die Gegenrechnung: GetPixel/SetPixel kosteten in der mehrskaligen Entrauschung allein für
        ''' Ein- und Ausgang rund 4 s je Vorschaubild.</summary>
        Private Shared ReadOnly _unpremultiplyTable As New Lazy(Of Byte())(
            AddressOf BuildUnpremultiplyTable, Threading.LazyThreadSafetyMode.ExecutionAndPublication)

        Private Shared Function BuildUnpremultiplyTable() As Byte()
            Dim table = New Byte(256 * 256 - 1) {}
            ' Zeile = Alpha, Spalte = premultiplizierter Kanalwert. Auch die Fälle mit Wert über Alpha
            ' werden erhoben: sie kommen in sauber premultiplizierten Bildern nicht vor, können aber
            ' aus Rundung entstehen, und was Skia dort liefert, ist die maßgebliche Antwort.
            Using probe = New SKBitmap(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul)
                Dim stride = probe.RowBytes
                Dim raw = New Byte(stride * 256 - 1) {}
                For a = 0 To 255
                    For v = 0 To 255
                        Dim o = a * stride + v * 4
                        raw(o) = CByte(v) : raw(o + 1) = CByte(v) : raw(o + 2) = CByte(v) : raw(o + 3) = CByte(a)
                    Next
                Next
                Marshal.Copy(raw, 0, probe.GetPixels(), raw.Length)
                For a = 0 To 255
                    For v = 0 To 255
                        table(a * 256 + v) = probe.GetPixel(v, a).Red
                    Next
                Next
            End Using
            Return table
        End Function

        ''' <summary>Premultiplizierten Kanalwert zu dem Wert machen, den <c>GetPixel</c> melden würde.</summary>
        Private Shared Function UnpremultiplyByte(value As Byte, alpha As Byte) As Byte
            Return _unpremultiplyTable.Value(CInt(alpha) * 256 + CInt(value))
        End Function

        ''' <summary>Die Premultiplikation, die <c>SKBitmap.SetPixel</c> vornimmt: Skias
        ''' <c>SkMulDiv255Round</c>. Über alle 65536 Kombinationen gegen SetPixel geprüft, keine
        ''' Abweichung - hier ist die Formel exakt und eine Tabelle unnötig. Ein einfaches
        ''' <c>v * a \ 255</c> wäre es NICHT (31770 Abweichungen um je eine Stufe).
        ''' Überlaufsicher: der Zwischenwert bleibt unter 65536.</summary>
        Private Shared Function PremultiplyByte(value As Byte, alpha As Byte) As Byte
            Dim product = CInt(value) * CInt(alpha) + 128
            Return CByte((product + (product >> 8)) >> 8)
        End Function

        ' Ab dieser Pixelzahl lohnt der Thread-Overhead von Parallel.For. Darunter (Miniaturen,
        ' entartete Größen) bleibt es seriell.
        Private Const ParallelPixelThreshold As Integer = 65536

        ''' <summary>Führt eine zeilenweise Bildoperation über y = 0..height-1 aus - parallel, sobald sich
        ''' der Thread-Overhead lohnt, sonst seriell. Voraussetzung: Die Zeilen sind unabhängig, jeder
        ''' Aufruf schreibt nur in seine eigene Zeile und liest höchstens aus unveränderten Quellpuffern.
        ''' Dann ist das Ergebnis unabhängig von der Thread-Aufteilung bitgleich zum seriellen Lauf.</summary>
        Private Shared Sub ForEachRow(width As Integer, height As Integer, body As Action(Of Integer))
            If height <= 0 Then Return
            If CLng(width) * height < ParallelPixelThreshold Then
                For y As Integer = 0 To height - 1
                    body(y)
                Next
            Else
                Parallel.For(0, height, body)
            End If
        End Sub

        ''' <summary>Identitätstabelle (Index -> Index) für den Alpha-Kanal von SKColorFilter.CreateTable.
        ''' Seit SkiaSharp 3.119 wirft die Überladung eine ArgumentNullException, wenn die Alpha-Tabelle
        ''' Nothing ist - früher stand Nothing für "Alpha unverändert lassen". Diese Tabelle stellt genau
        ''' dieses Verhalten wieder her und wird von Skia beim Bau des nativen Filters kopiert, ist also
        ''' gefahrlos gemeinsam nutzbar.</summary>
        Private Shared ReadOnly IdentityByteTable As Byte() = BuildIdentityByteTable()

        Private Shared Function BuildIdentityByteTable() As Byte()
            Dim table = New Byte(255) {}
            For i As Integer = 0 To 255
                table(i) = CByte(i)
            Next
            Return table
        End Function

        Private Shared Function CloneBitmap(source As SKBitmap) As SKBitmap
            Dim clone = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(clone)
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return clone
        End Function

        ''' <summary>Kopiert ein Bitmap in einen anderen Farbtyp (premultipliziert). Wird gebraucht, wo
        ''' Stufen mit festem Farbtyp auf das Objekt-Komposit treffen - ohne die Umwandlung fallen sie
        ''' still durch (bekannte Fallenklasse dieses Projekts).</summary>
        Private Shared Function ConvertBitmapToColorType(source As SKBitmap, colorType As SKColorType) As SKBitmap
            If source Is Nothing Then Return Nothing
            If source.ColorType = colorType Then Return CloneBitmap(source)
            Dim clone = New SKBitmap(source.Width, source.Height, colorType, SKAlphaType.Premul)
            Using canvas = New SKCanvas(clone)
                canvas.Clear(SKColors.Transparent)
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return clone
        End Function

        Private Shared Function CloneBitmapForAnnotationComposite(source As SKBitmap) As SKBitmap
            Dim clone = New SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
            Using canvas = New SKCanvas(clone)
                canvas.Clear(SKColors.Transparent)
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return clone
        End Function

        Private Shared Function ApplyCrop(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim leftPct = Clamp(adj.CropLeftPercent, 0, 100) / 100.0F
            Dim topPct = Clamp(adj.CropTopPercent, 0, 100) / 100.0F
            Dim rightPct = Clamp(adj.CropRightPercent, 0, 100) / 100.0F
            Dim bottomPct = Clamp(adj.CropBottomPercent, 0, 100) / 100.0F

            If leftPct = 0 AndAlso topPct = 0 AndAlso rightPct = 0 AndAlso bottomPct = 0 Then Return source

            ' Der Beschnitt kommt pixelgenau aus dem Editor, wird aber prozentual transportiert (die
            ' Vorschau ist kleiner als das Original). Deshalb hier hart in gültige Pixelgrenzen zwingen,
            ' statt bei zu engem Ausschnitt den Beschnitt stillschweigend fallen zu lassen: mindestens
            ' ein Pixel bleibt stehen, die linke/obere Kante gewinnt.
            Dim left = Math.Max(0, Math.Min(CInt(Math.Round(source.Width * leftPct)), source.Width - 1))
            Dim top = Math.Max(0, Math.Min(CInt(Math.Round(source.Height * topPct)), source.Height - 1))
            Dim right = Math.Max(left + 1, Math.Min(source.Width - CInt(Math.Round(source.Width * rightPct)), source.Width))
            Dim bottom = Math.Max(top + 1, Math.Min(source.Height - CInt(Math.Round(source.Height * bottomPct)), source.Height))

            Dim cropWidth = right - left
            Dim cropHeight = bottom - top
            If cropWidth = source.Width AndAlso cropHeight = source.Height Then Return source
            Dim result = New SKBitmap(cropWidth, cropHeight, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                Dim srcRect = New SKRect(left, top, right, bottom)
                Dim dstRect = New SKRect(0, 0, cropWidth, cropHeight)
                canvas.DrawBitmap(source, srcRect, dstRect)
            End Using
            Return result
        End Function

        Private Shared Function ClampByte(value As Integer) As Byte
            If value <= 0 Then Return 0
            If value >= 255 Then Return 255
            Return CByte(value)
        End Function

        Private Shared Function BlendByte(dst As Byte, src As Byte, alpha As Single) As Byte
            Return ClampByte(CInt(Math.Round(dst + (CInt(src) - CInt(dst)) * alpha)))
        End Function

        Private Shared Function ApplyResize(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim targetWidth = adj.ResizeWidth
            Dim targetHeight = adj.ResizeHeight

            ' Prozentuale Zielgroesse auf dem TATSAECHLICHEN Bild - siehe ResizeScalePercent.
            If adj.ResizeScalePercent > 0 AndAlso source.Width > 0 AndAlso source.Height > 0 Then
                targetWidth = Math.Max(1, CInt(Math.Round(source.Width * adj.ResizeScalePercent / 100.0)))
                targetHeight = Math.Max(1, CInt(Math.Round(source.Height * adj.ResizeScalePercent / 100.0)))
            End If

            If targetWidth <= 0 AndAlso targetHeight <= 0 Then Return source

            ' KASTEN-Modus (nur Stapel/Export, siehe ResizeFitInsideBox): das Bild wird NICHT
            ' verzerrt, die Zielwerte sind eine Schranke statt eines exakten Masses:
            '   * nur EIN Wert  -> er begrenzt die LAENGSTE Kante, unabhaengig von der Ausrichtung
            '     (ein gemischter Stapel kommt so einheitlich heraus statt in zwei Groessen);
            '   * BEIDE Werte   -> das Bild wird in diesen Kasten EINGEPASST (kleinerer Faktor).
            ' Ohne den Modus gelten die Werte exakt (Editor: die zweite Kante haengt dort ohnehin
            ' am Seitenverhaeltnis, und der Nutzer sieht die Zahlen im Panel).
            If adj.ResizeFitInsideBox AndAlso adj.LockResizeAspect AndAlso source.Width > 0 AndAlso source.Height > 0 Then
                If targetWidth <= 0 Xor targetHeight <= 0 Then
                    Dim laengste = Math.Max(targetWidth, targetHeight)
                    If source.Width >= source.Height Then
                        targetWidth = laengste
                        targetHeight = 0
                    Else
                        targetHeight = laengste
                        targetWidth = 0
                    End If
                Else
                    Dim factor = Math.Min(targetWidth / CDbl(source.Width), targetHeight / CDbl(source.Height))
                    targetWidth = Math.Max(1, CInt(Math.Round(source.Width * factor)))
                    targetHeight = Math.Max(1, CInt(Math.Round(source.Height * factor)))
                End If
            End If

            If targetWidth <= 0 Then targetWidth = CInt(Math.Round(source.Width * (targetHeight / CDbl(source.Height))))
            If targetHeight <= 0 Then targetHeight = CInt(Math.Round(source.Height * (targetWidth / CDbl(source.Width))))

            ' "Nicht vergroessern": ein Bild, das schon kleiner ist als das Ziel, bleibt wie es ist.
            ' Mit gehaltenem Seitenverhaeltnis wird EINHEITLICH herunterskaliert (ein gemeinsamer
            ' Faktor), sonst wuerde die Deckelung je Achse das Bild doch wieder verzerren.
            If adj.NoResizeUpscale AndAlso source.Width > 0 AndAlso source.Height > 0 Then
                If adj.LockResizeAspect Then   ' einheitlicher Faktor, sonst verzerrt die Deckelung
                    Dim cap = Math.Min(1.0, Math.Min(targetWidth / CDbl(source.Width), targetHeight / CDbl(source.Height)))
                    targetWidth = Math.Max(1, CInt(Math.Round(source.Width * cap)))
                    targetHeight = Math.Max(1, CInt(Math.Round(source.Height * cap)))
                Else
                    targetWidth = Math.Min(targetWidth, source.Width)
                    targetHeight = Math.Min(targetHeight, source.Height)
                End If
            End If

            targetWidth = Math.Max(1, targetWidth)
            targetHeight = Math.Max(1, targetHeight)
            If targetWidth = source.Width AndAlso targetHeight = source.Height Then Return source

            Dim result = New SKBitmap(targetWidth, targetHeight, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                Using paint = New SKPaint With {.IsAntialias = True}
                    DrawBitmapSampled(canvas, source, New SKRect(0, 0, source.Width, source.Height), New SKRect(0, 0, targetWidth, targetHeight),
                                      ToSampling(adj.ResizeInterpolation), paint)
                End Using
            End Using
            Return result
        End Function

        Private Shared Function ToSampling(mode As ResizeInterpolationMode) As SKSamplingOptions
            Select Case mode
                Case ResizeInterpolationMode.Nearest
                    Return New SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
                Case ResizeInterpolationMode.Bilinear
                    Return New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)
                Case Else
                    Return SamplingHigh
            End Select
        End Function

        ''' <summary>Legt das Bild auf die Hintergrundfarbe des Dokuments. Ohne gesetzte Farbe kommt
        ''' dasselbe Objekt unveraendert zurueck - kein Umkopieren fuer nichts.
        '''
        ''' Am Ende der Kette, damit sie ALLES fuellt, was vorher durchsichtig geblieben ist:
        ''' erweiterte Leinwand, leere Ecken von Begradigen und Verzerren, weggeradierte Stellen.</summary>
        ''' <summary>Die Hintergrundfarbe des Dokuments, oder durchsichtig. EINE Stelle dafuer: sie
        ''' wird an zwei Enden gebraucht - unter dem fertigen Bild (hier) und unter den Objekten,
        ''' wenn die Hintergrundebene ausgeblendet ist. Zwei Auslegungen von "keine Farbe gesetzt"
        ''' liefen auseinander.</summary>
        Friend Shared Function DocumentBackgroundColor(adj As ImageAdjustments) As SKColor
            If adj Is Nothing OrElse String.IsNullOrWhiteSpace(adj.CanvasBackgroundColor) Then Return SKColors.Transparent
            Dim color As SKColor
            If Not SKColor.TryParse(adj.CanvasBackgroundColor, color) Then Return SKColors.Transparent
            Return color
        End Function

        Private Shared Function ApplyDocumentBackground(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing OrElse adj Is Nothing Then Return source
            Dim color = DocumentBackgroundColor(adj)
            ' Voellig durchsichtig heisst: keine Farbe gewuenscht.
            If color.Alpha = 0 Then Return source

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(color)
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return result
        End Function

        Private Shared Function ApplyCanvasResize(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim targetWidth = If(adj.CanvasWidth > 0, adj.CanvasWidth, source.Width)
            Dim targetHeight = If(adj.CanvasHeight > 0, adj.CanvasHeight, source.Height)
            If targetWidth = source.Width AndAlso targetHeight = source.Height Then Return source
            If targetWidth <= 0 OrElse targetHeight <= 0 Then Return source

            Dim offsetX As Single = 0
            Dim offsetY As Single = 0
            Dim anchor = If(adj.CanvasAnchor, "Center").Trim().ToLowerInvariant()

            Select Case anchor
                Case "top-left", "left-top" : offsetX = 0 : offsetY = 0
                Case "top", "top-center" : offsetX = (targetWidth - source.Width) / 2.0F : offsetY = 0
                Case "top-right", "right-top" : offsetX = targetWidth - source.Width : offsetY = 0
                Case "left", "middle-left" : offsetX = 0 : offsetY = (targetHeight - source.Height) / 2.0F
                Case "right", "middle-right" : offsetX = targetWidth - source.Width : offsetY = (targetHeight - source.Height) / 2.0F
                Case "bottom-left", "left-bottom" : offsetX = 0 : offsetY = targetHeight - source.Height
                Case "bottom", "bottom-center" : offsetX = (targetWidth - source.Width) / 2.0F : offsetY = targetHeight - source.Height
                Case "bottom-right", "right-bottom" : offsetX = targetWidth - source.Width : offsetY = targetHeight - source.Height
                Case Else
                    offsetX = (targetWidth - source.Width) / 2.0F
                    offsetY = (targetHeight - source.Height) / 2.0F
            End Select

            Dim result = New SKBitmap(targetWidth, targetHeight, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                ' Durchsichtig lassen: gefuellt wird EINMAL am Ende der Kette
                ' (ApplyDocumentBackground), sonst faerbte diese Stufe ihr Loch selbst und die
                ' anderen Loecher blieben durchsichtig.
                canvas.Clear(SKColors.Transparent)
                canvas.DrawBitmap(source, offsetX, offsetY)
            End Using
            Return result
        End Function

        Private Shared Function IsPaintKind(kind As String) As Boolean
            Dim normalized = If(kind, "").Trim().ToLowerInvariant()
            Return normalized = "brush" OrElse normalized = "eraser"
        End Function

        Private Shared Function ScaleAnnotationForSource(annotation As ImageAnnotation, scaleX As Single, scaleY As Single) As ImageAnnotation
            If annotation Is Nothing Then Return Nothing
            If Math.Abs(scaleX - 1.0F) < 0.0001F AndAlso Math.Abs(scaleY - 1.0F) < 0.0001F Then Return annotation

            Dim scaled = annotation.Clone()
            Dim uniformScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))
            scaled.XPixels *= scaleX
            scaled.YPixels *= scaleY
            scaled.WidthPixels *= scaleX
            scaled.HeightPixels *= scaleY
            scaled.FontSizePixels *= uniformScale
            scaled.StrokeWidth *= uniformScale
            If IsPaintKind(scaled.Kind) Then
                scaled.Strokes = scaled.Strokes.Select(Function(s) s.Scale(scaleX, scaleY)).ToList()
            End If
            Return scaled
        End Function

        Friend Shared Function TransformAnnotationForGeometry(annotation As ImageAnnotation, adj As ImageAdjustments,
                                                              outputWidth As Integer, outputHeight As Integer) As ImageAnnotation
            If annotation Is Nothing Then Return Nothing
            If adj Is Nothing OrElse adj.SourceWidthPixels <= 0 OrElse adj.SourceHeightPixels <= 0 Then Return annotation

            Dim rotation = ImageGeometryMapper.NormalizeQuarterTurn(adj.RotationDegrees)
            Dim q = rotation \ 90
            Dim preWidth = If(rotation = 90 OrElse rotation = 270, outputHeight, outputWidth)
            Dim preHeight = If(rotation = 90 OrElse rotation = 270, outputWidth, outputHeight)
            If preWidth <= 0 OrElse preHeight <= 0 Then Return annotation

            ' Annotationen leben im Pixelraum des unbeschnittenen Basisbilds. Nach ApplyCrop beginnt
            ' der Renderraum jedoch an der linken/oberen Crop-Kante. Nur auf die Restgroesse zu
            ' skalieren (das fruehere Verhalten) verschob deshalb jedes Objekt: x=500 wurde bei einem
            ' 10-%-Crop zu 450 statt zu 400. Erst den Crop-Ursprung abziehen, danach eine eventuelle
            ' Preview-/Resize-Skalierung auf die beschnittene Groesse anwenden.
            Dim crop = ComputeGeometryCropRect(adj.SourceWidthPixels, adj.SourceHeightPixels, adj)
            Dim croppedAnnotation = annotation.Clone()
            Dim kind = If(croppedAnnotation.Kind, "").Trim().ToLowerInvariant()
            Dim isAnchoredWatermark = kind = "watermark" AndAlso Not String.IsNullOrWhiteSpace(croppedAnnotation.Anchor)
            If Not isAnchoredWatermark Then
                croppedAnnotation.XPixels -= crop.Left
                croppedAnnotation.YPixels -= crop.Top
            End If
            If IsPaintKind(kind) AndAlso croppedAnnotation.Strokes IsNot Nothing AndAlso
               (crop.Left <> 0 OrElse crop.Top <> 0) Then
                croppedAnnotation.Strokes = croppedAnnotation.Strokes.Where(Function(stroke) stroke IsNot Nothing).Select(
                    Function(stroke) New BrushStroke(stroke.Points.Select(
                        Function(p) New StrokePoint(p.X - crop.Left, p.Y - crop.Top)).ToList())).ToList()
            End If

            ' „Größe behalten": ein verankertes Wasserzeichen, das NICHT mitwachsen soll, wird gar
            ' nicht erst umgerechnet - seine Zahlen gelten dann direkt im Ausgabebild. Die Verankerung
            ' rechnet ComputeAnnotationRect ohnehin gegen die Ausgabemaße, die Lage stimmt also weiter.
            Dim renderAnnotation As ImageAnnotation
            If isAnchoredWatermark AndAlso Not croppedAnnotation.ScaleWithImage Then
                renderAnnotation = croppedAnnotation
            Else
                renderAnnotation = ScaleAnnotationForSource(croppedAnnotation,
                                                            preWidth / CSng(crop.Width),
                                                            preHeight / CSng(crop.Height))
            End If
            If renderAnnotation Is Nothing Then Return Nothing
            renderAnnotation.OwnWarp = TransformOwnWarpForGeometry(renderAnnotation.OwnWarp,
                                                                    annotation.RotationDegrees,
                                                                    annotation.FlipHorizontal, annotation.FlipVertical)
            If q = 0 AndAlso Not adj.FlipHorizontal AndAlso Not adj.FlipVertical Then Return renderAnnotation

            Dim transformed = renderAnnotation.Clone()
            Dim objectGeometry = ImageGeometryMapper.SourceObjectToDisplay(
                New SKRect(transformed.XPixels, transformed.YPixels,
                           transformed.XPixels + transformed.WidthPixels,
                           transformed.YPixels + transformed.HeightPixels),
                preWidth, preHeight, outputWidth, outputHeight,
                rotation, adj.FlipHorizontal, adj.FlipVertical,
                transformed.RotationDegrees)
            transformed.XPixels = objectGeometry.Rect.Left
            transformed.YPixels = objectGeometry.Rect.Top
            transformed.WidthPixels = objectGeometry.Rect.Width
            transformed.HeightPixels = objectGeometry.Rect.Height
            transformed.RotationDegrees = objectGeometry.RotationDegrees
            If adj.FlipHorizontal Then transformed.FlipHorizontal = Not transformed.FlipHorizontal
            If adj.FlipVertical Then transformed.FlipVertical = Not transformed.FlipVertical
            ' Die Bilddrehung steckt zwar bereits in der Zeichenroutine, das eigene Warp-Feld wird
            ' aber ERST DANACH über die fertige Objekt-Ebene gelegt. Es muss daher dieselbe
            ' Vierteldrehung (und danach dieselben Bildspiegelungen) durchlaufen. Ohne diese Drehung
            ' blieb etwa ein eingezogenes oberes Feld nach „Bild um 90° drehen" oben, während der
            ' Objektinhalt nach rechts weiterdrehte.
            transformed.OwnWarp = TransformOwnWarpForGeometry(renderAnnotation.OwnWarp,
                                                               ImageGeometryMapper.SourceObjectRotationToDisplay(
                                                                   0, rotation, adj.FlipHorizontal, adj.FlipVertical),
                                                               adj.FlipHorizontal, adj.FlipVertical)
            If IsPaintKind(transformed.Kind) AndAlso transformed.Strokes IsNot Nothing Then
                transformed.Strokes = transformed.Strokes.Select(
                    Function(stroke) TransformStrokeForGeometry(stroke, preWidth, preHeight, rotation, adj.FlipHorizontal, adj.FlipVertical)).
                    Where(Function(stroke) stroke IsNot Nothing).
                    ToList()
            End If
            Return transformed
        End Function

        ''' <summary>Überträgt eine Drehung oder Spiegelung des Objekts auf dessen lokales
        ''' Verzerrungsfeld. Die gespeicherten Werte bleiben unverändert; nur die Renderkopie wird
        ''' als Gitter abgetastet.</summary>
        Private Shared Function TransformOwnWarpForGeometry(warp As ObjectWarp,
                                                             rotationDegrees As Double,
                                                             flipH As Boolean, flipV As Boolean) As ObjectWarp
            If warp Is Nothing OrElse warp.IsEmpty Then Return warp
            If Math.Abs(rotationDegrees) < 0.0001 AndAlso Not flipH AndAlso Not flipV Then Return warp
            ' Dieselbe Auswertungsfeinheit wie der Objekt-Renderer: die konjugierte Kopie darf
            ' bei Drehung/Flip keine gröberen Segmente als die sichtbare Verformung erhalten.
            Const steps As Integer = 48
            Dim nodes((steps + 1) * (steps + 1) * 2 - 1) As Double
            For row = 0 To steps
                For column = 0 To steps
                    Dim i = (row * (steps + 1) + column) * 2
                    Dim x = column / CDbl(steps) * 100.0
                    Dim y = row / CDbl(steps) * 100.0
                    Dim source = InverseObjectWarpTransform(x, y, rotationDegrees, flipH, flipV)
                    x = source.X : y = source.Y
                    Dim moved = MovePoint(warp, x, y, 100, 100)
                    Dim target = ObjectWarpTransform(moved.X, moved.Y, rotationDegrees, flipH, flipV)
                    nodes(i) = target.X
                    nodes(i + 1) = target.Y
                Next
            Next
            Return New ObjectWarp With {.Kind = "Gitter", .Columns = steps, .Rows = steps, .Nodes = nodes}
        End Function

        Private Shared Function ObjectWarpTransform(x As Double, y As Double, rotationDegrees As Double,
                                                    flipH As Boolean, flipV As Boolean) As (X As Double, Y As Double)
            Dim radians = rotationDegrees * Math.PI / 180.0
            Dim cos = Math.Cos(radians), sin = Math.Sin(radians)
            Dim dx = x - 50.0, dy = y - 50.0
            Dim resultX = 50.0 + cos * dx - sin * dy
            Dim resultY = 50.0 + sin * dx + cos * dy
            If flipH Then resultX = 100.0 - resultX
            If flipV Then resultY = 100.0 - resultY
            Return (resultX, resultY)
        End Function

        Private Shared Function InverseObjectWarpTransform(x As Double, y As Double, rotationDegrees As Double,
                                                           flipH As Boolean, flipV As Boolean) As (X As Double, Y As Double)
            If flipH Then x = 100.0 - x
            If flipV Then y = 100.0 - y
            Return ObjectWarpTransform(x, y, -rotationDegrees, False, False)
        End Function

        ''' <summary>Striche in die Ausgabegeometrie. Ueber die gemeinsame Matrix des Mappers statt
        ''' ueber eine eigene Fallunterscheidung - die war die letzte Sorte, die ihre eigene Kopie
        ''' der Dreh- und Spiegelregeln fuehrte. Zwei Kopien derselben Formel laufen frueher oder
        ''' spaeter auseinander; hier ist es nur deshalb nie passiert, weil beide gleichzeitig
        ''' geschrieben wurden.</summary>
        Private Shared Function TransformStrokeForGeometry(stroke As BrushStroke,
                                                           preWidth As Integer, preHeight As Integer,
                                                           rotationDegrees As Integer,
                                                           flipH As Boolean, flipV As Boolean) As BrushStroke
            If stroke Is Nothing OrElse stroke.Points Is Nothing Then Return Nothing
            Dim m = ImageGeometryMapper.SourceToDisplayMatrix(preWidth, preHeight, rotationDegrees, flipH, flipV)
            Dim points As New List(Of StrokePoint)(stroke.Points.Count)
            For Each p In stroke.Points
                Dim mapped = m.MapPoint(New SKPoint(CSng(p.X), CSng(p.Y)))
                points.Add(New StrokePoint(mapped.X, mapped.Y))
            Next
            Return New BrushStroke(points)
        End Function

        ''' <summary>Unschaerfemaske mit Kreuz-Kern (5 Taps):
        '''      0   -a    0
        '''     -a  1+4a  -a
        '''      0   -a    0
        '''
        ''' Lief bis ueber SKImageFilter.CreateMatrixConvolution. Skias CPU-Faltung ist
        ''' dafuer pathologisch langsam: gemessen 8,6 s bei 6,3 MP - fuer fuenf Multiplikationen je
        ''' Pixel. Zum Vergleich braucht die gesamte verschmolzene Farbkette 17 ms.
        '''
        ''' Randbehandlung wie zuvor SKShaderTileMode.Clamp: ausserhalb liegende Nachbarn werden auf
        ''' den Rand geklemmt. Alpha bleibt unveraendert (entsprach convolveAlpha:=False).</summary>
        ''' <summary>Schärfen. Ohne Radius/Detail/Maskierung (alle 0) die bisherige feste 3×3-Maske,
        ''' bitgenau unverändert; sonst eine echte Unschärfemaske mit variablem Radius, Detailanhebung
        ''' und Kantenmaskierung.</summary>
        Private Shared Function ApplySharpness(source As SKBitmap, amount As Single, radiusAmount As Single,
                                               detailAmount As Single, maskingAmount As Single) As SKBitmap
            ' Die Maskierung MUSS die 3x3-Abkuerzung mit abwaehlen: dort gibt es keine Kantenmaske, ein
            ' allein gezogener Maskierungsregler waere sonst ein stummer No-Op.
            If radiusAmount <= 0 AndAlso detailAmount <= 0 AndAlso maskingAmount <= 0 Then Return ApplySharpness3x3(source, amount)
            Return ApplyUnsharpMask(source, amount, radiusAmount, detailAmount, maskingAmount)
        End Function

        ''' <summary>Kantenstärke, ab der die Maskierung voll durchlässt - gemessen als Summe der
        ''' Helligkeitsunterschiede zu den vier Nachbarn (0..255 je Richtung). 48 liegt deutlich über dem
        ''' Rauschen eines normal belichteten Fotos und deutlich unter einer echten Motivkante.</summary>
        Private Const SharpenEdgeFullScale As Single = 48.0F

        ''' <summary>Helligkeit eines Pixels für die Kantenmessung. Über ReadUnpremultiplied, weil die
        ''' Puffer premultipliziert vorliegen können - roh gelesen wäre in halbtransparenten Bereichen die
        ''' Alpha-Kante als Motivkante durchgegangen.</summary>
        Private Shared Function LumaAt(buf As Byte(), offset As Integer, ri As Integer, gi As Integer,
                                       bi As Integer, ai As Integer) As Single
            Dim r As Integer, g As Integer, b As Integer, a As Integer
            ReadUnpremultiplied(buf, offset, ri, gi, bi, ai, r, g, b, a)
            Return 0.299F * r + 0.587F * g + 0.114F * b
        End Function

        ''' <summary>Wie sehr ein Bildpunkt als KANTE gilt: 0 auf glatter Flaeche, 1 auf einer vollen
        ''' Motivkante. Gemessen als Summe der beiden Helligkeitsgefaelle (waagerecht, senkrecht),
        ''' bezogen auf <see cref="SharpenEdgeFullScale"/> und mit Smoothstep geglaettet.
        '''
        ''' Steht als EIGENE Funktion da, weil zwei Stellen genau dieselbe Antwort brauchen: die
        ''' Schaerfung, die damit gewichtet, und die Vorschau bei gedrueckter ALT-Taste, die genau
        ''' diese Gewichtung als Bild zeigt. Zwei Abschriften derselben Formel liefen frueher oder
        ''' spaeter auseinander - und dann zeigte die Vorschau etwas anderes, als die Schaerfung
        ''' tut. Eine Vorschau, die luegt, ist schlimmer als keine.</summary>
        Private Shared Function SharpenEdgeFactor(buf As Byte(), rowOffset As Integer, upRow As Integer,
                                                  downRow As Integer, x As Integer, w As Integer,
                                                  ri As Integer, gi As Integer, bi As Integer, ai As Integer) As Single
            ' Am Bildrand auf den Rand geklemmt, wie die restliche Randbehandlung dieser Datei.
            Dim leftOff = rowOffset + If(x > 0, (x - 1) * 4, 0)
            Dim rightOff = rowOffset + If(x < w - 1, (x + 1) * 4, (w - 1) * 4)
            Dim edge = Clamp((Math.Abs(LumaAt(buf, leftOff, ri, gi, bi, ai) - LumaAt(buf, rightOff, ri, gi, bi, ai)) +
                              Math.Abs(LumaAt(buf, upRow + x * 4, ri, gi, bi, ai) - LumaAt(buf, downRow + x * 4, ri, gi, bi, ai))) /
                             SharpenEdgeFullScale, 0, 1)
            ' Smoothstep: an beiden Enden Ableitung null, der Übergang von "glatt" nach "Kante" zieht
            ' deshalb keine sichtbare Linie ins Bild.
            Return edge * edge * (3.0F - 2.0F * edge)
        End Function

        ''' <summary>Unschärfemaske: Bild − Gaußunschärfe ergibt die Hochfrequenzanteile, die verstärkt
        ''' aufaddiert werden. Radius steuert das Gauß-Sigma (Wirkgröße), Detail die Verstärkung.
        '''
        ''' MASKIERUNG (Adobes „Maskieren", crs:SharpenEdgeMasking): die Verstärkung wird je Pixel mit der
        ''' lokalen Kantenstärke gewichtet. 0 = überall voll (wie bisher), 100 = nur noch an Kanten. Die
        ''' Formel ist eine NÄHERUNG an Adobe (deren Maske steckt im geschlossenen Camera-Raw-Kern):
        ''' gewicht = (1 − m) + m · kante, mit kante = smoothstep der Gradientensumme. Sie hat die
        ''' richtigen Endpunkte (m=0 → überall 1, m=100 → glatte Fläche 0, Kante 1) und läuft dazwischen
        ''' stetig, statt eine harte Schwelle zu ziehen - eine harte Schwelle setzt bei Rauschen sichtbare
        ''' Flecken, weil benachbarte Pixel auf verschiedene Seiten fallen.</summary>
        Private Shared Function ApplyUnsharpMask(source As SKBitmap, amount As Single, radiusAmount As Single,
                                                 detailAmount As Single, maskingAmount As Single) As SKBitmap
            Dim sigma = 0.8F + Clamp(radiusAmount, 0, 1) * 2.7F
            Dim gain = amount * (1.0F + Clamp(detailAmount, 0, 1) * 1.5F)
            Dim masking = Clamp(maskingAmount, 0, 1)

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim blurred = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(blurred)
                Using filter = SKImageFilter.CreateBlur(sigma, sigma)
                    Using paint = New SKPaint With {.ImageFilter = filter}
                        canvas.DrawBitmap(source, 0, 0, paint)
                    End Using
                End Using
            End Using

            Dim srcBuf As Byte() = Nothing, blurBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            Dim bStride, bri, bgi, bbi, bai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, srcBuf, stride, ri, gi, bi, ai) OrElse
               Not TryBorrowRgbaLikeBuffer(blurred, blurBuf, bStride, bri, bgi, bbi, bai) Then
                blurred.Dispose()
                Return result
            End If
            Dim dstBuf = New Byte(srcBuf.Length - 1) {}
            Dim w = source.Width, h = source.Height

            ForEachRow(w, h,
                Sub(y)
                    Dim rowOffset = y * stride
                    Dim bRow = y * bStride
                    ' Zeilen ober-/unterhalb für den senkrechten Gradienten; am Bildrand auf die eigene
                    ' Zeile geklemmt (wie die restliche Randbehandlung dieser Datei).
                    Dim upRow = If(y > 0, (y - 1) * stride, rowOffset)
                    Dim downRow = If(y < h - 1, (y + 1) * stride, rowOffset)
                    For x = 0 To w - 1
                        Dim o = rowOffset + x * 4
                        Dim bo = bRow + x * 4
                        Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                        ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                        Dim lr As Integer, lg As Integer, lb As Integer, la As Integer
                        ReadUnpremultiplied(blurBuf, bo, bri, bgi, bbi, bai, lr, lg, lb, la)

                        Dim pixelGain = gain
                        If masking > 0 Then
                            Dim edge = SharpenEdgeFactor(srcBuf, rowOffset, upRow, downRow, x, w, ri, gi, bi, ai)
                            pixelGain = gain * ((1.0F - masking) + masking * edge)
                        End If

                        WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                            ClampToByte(cr + pixelGain * (cr - lr)),
                            ClampToByte(cg + pixelGain * (cg - lg)),
                            ClampToByte(cb + pixelGain * (cb - lb)), a)
                    Next
                End Sub)

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            blurred.Dispose()
            Return result
        End Function

        ' ── Vorschau der Maskierung ─────────────────────────────────────────────
        '
        ' Wie in Lightroom: ALT gedrueckt halten und den Maskierungsregler ziehen zeigt statt des
        ' Bildes die GEWICHTUNG - weiss heisst "hier wird voll geschaerft", schwarz "hier bleibt es,
        ' wie es ist". Ohne sie stellt man den Regler blind ein: an einer glatten Flaeche sieht man
        ' bei 40 und bei 70 dasselbe, und erst beim spaeteren Blick auf den Himmel faellt auf, dass
        ' das Rauschen mitgeschaerft wurde.
        '
        ' Zweigeteilt, und zwar aus einem Messgrund: die KANTENKARTE haengt nur vom Bild ab, die
        ' Gewichtung nur vom Reglerwert. Waehrend des Zuges bleibt die Karte also stehen, und jede
        ' Reglerbewegung kostet nur noch eine Punktrechnung statt eines Kettendurchlaufs. Sonst
        ' haenge die Anzeige dem Regler hinterher - bei einer Vorschau, die man zum EINSTELLEN
        ' benutzt, waere das der Sinn der Sache.

        ''' <summary>Die Kantenkarte eines Bildes: ein Byte je Bildpunkt, zeilenweise, 0 = glatte
        ''' Flaeche, 255 = volle Kante. Dieselbe Rechnung, mit der die Schaerfung gewichtet - siehe
        ''' <see cref="SharpenEdgeFactor"/>.
        '''
        ''' Das Bild muss der Zustand VOR der Schaerfung sein, sonst misst die Karte die eigene
        ''' Wirkung mit: ein geschaerftes Bild hat staerkere Kanten, die Karte zeigte mehr Weiss als
        ''' die Gewichtung wirklich hergibt, und man stellte den Regler zu hoch ein.</summary>
        Public Shared Function ComputeSharpenEdgeMap(source As SKBitmap) As Byte()
            If source Is Nothing OrElse source.Width <= 0 OrElse source.Height <= 0 Then Return Nothing
            Dim srcBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, srcBuf, stride, ri, gi, bi, ai) Then Return Nothing

            Dim w = source.Width, h = source.Height
            Dim map = New Byte(w * h - 1) {}
            ForEachRow(w, h,
                Sub(y)
                    Dim rowOffset = y * stride
                    Dim upRow = If(y > 0, (y - 1) * stride, rowOffset)
                    Dim downRow = If(y < h - 1, (y + 1) * stride, rowOffset)
                    Dim mapRow = y * w
                    For x = 0 To w - 1
                        Dim edge = SharpenEdgeFactor(srcBuf, rowOffset, upRow, downRow, x, w, ri, gi, bi, ai)
                        map(mapRow + x) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(edge * 255.0F)))))
                    Next
                End Sub)
            Return map
        End Function

        ''' <summary>Das Anzeigebild zur Kantenkarte bei einem bestimmten Maskierungswert: genau die
        ''' Gewichtung, die die Schaerfung an dieser Stelle anlegt, als Graustufe.
        '''
        ''' Bei 0 ist alles weiss - die Schaerfung wirkt ueberall gleich, und das ist ehrlich so
        ''' anzuzeigen, statt die Vorschau erst ab einem Wert "interessant" werden zu lassen.</summary>
        Public Shared Function RenderSharpenMaskPreview(edgeMap As Byte(), width As Integer, height As Integer,
                                                        maskingPercent As Double) As SKBitmap
            If edgeMap Is Nothing OrElse width <= 0 OrElse height <= 0 Then Return Nothing
            If edgeMap.Length < width * height Then Return Nothing

            Dim masking = Clamp(CSng(maskingPercent / 100.0), 0, 1)
            Dim result = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Dim buffer = New Byte(width * height * 4 - 1) {}
            ForEachRow(width, height,
                Sub(y)
                    Dim mapRow = y * width
                    Dim rowOffset = mapRow * 4
                    For x = 0 To width - 1
                        Dim edge = edgeMap(mapRow + x) / 255.0F
                        Dim weight = (1.0F - masking) + masking * edge
                        Dim v = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(weight * 255.0F)))))
                        Dim o = rowOffset + x * 4
                        ' Deckend grau: Bgra8888 premultipliziert, Alpha voll - ein Grauwert ist bei
                        ' vollem Alpha in beiden Formen derselbe.
                        buffer(o) = v
                        buffer(o + 1) = v
                        buffer(o + 2) = v
                        buffer(o + 3) = 255
                    Next
                End Sub)
            Runtime.InteropServices.Marshal.Copy(buffer, 0, result.GetPixels(), buffer.Length)
            Return result
        End Function

        ''' <summary>Das Bild an genau der Stelle der Kette, an der die Schaerfung ansetzt: alles
        ''' davor gerechnet, die Schaerfung selbst und die Stufen danach neutral.
        '''
        ''' Korn und Rauschen MUESSEN mit heraus. Sie kommen nach der Schaerfung und sind fuer eine
        ''' Kantenmessung genau das, was sie ueberall sind: Kanten. Mit Korn im Bild waere die halbe
        ''' Flaeche weiss, und die Vorschau zeigte etwas, das mit der Gewichtung nichts zu tun hat.
        ''' Die Vignette faellt aus demselben Grund weg, wenn auch schwaecher: sie zieht die
        ''' Helligkeit in den Ecken zusammen und daempft dort die Gefaelle.</summary>
        Public Shared Function RenderPreSharpenBase(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing Then Return Nothing
            If adj Is Nothing Then Return CloneBitmap(source)
            Return ProcessBitmapBase(source, WithoutSharpening(adj))
        End Function

        ''' <summary>Der Gueltigkeitsstempel der Kantenkarte: derselbe Schluessel, den auch der
        ''' Rendercache benutzt, aber vom Stand OHNE Schaerfung. Er aendert sich bei jeder Einstellung,
        ''' die das Bild vor der Schaerfung anfasst, und ausdruecklich NICHT bei den Schaerfe-Reglern
        ''' selbst - sonst waere die Karte genau dann hinfaellig, wenn man sie benutzt.</summary>
        Public Shared Function PreSharpenKey(adj As ImageAdjustments) As String
            If adj Is Nothing Then Return ""
            Return ComputeBaseKey(WithoutSharpening(adj))
        End Function

        ''' <summary>Ein Klon ohne Schaerfung und ohne die Stufen danach. EINE Stelle, damit Stempel
        ''' und Bild nicht auseinanderlaufen koennen.</summary>
        Private Shared Function WithoutSharpening(adj As ImageAdjustments) As ImageAdjustments
            Dim unsharpened = adj.Clone()
            unsharpened.Sharpness = 0
            unsharpened.SharpenRadius = 0
            unsharpened.SharpenDetail = 0
            unsharpened.SharpenMasking = 0
            unsharpened.Vignette = 0
            unsharpened.Grain = 0
            unsharpened.AddNoise = 0
            Return unsharpened
        End Function

        Private Shared Function ApplySharpness3x3(source As SKBitmap, amount As Single) As SKBitmap
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim srcBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, srcBuf, stride, ri, gi, bi, ai) Then Return result

            Dim dstBuf = New Byte(srcBuf.Length - 1) {}
            Dim w = source.Width
            Dim h = source.Height
            Dim center = 1.0F + 4.0F * amount

            ForEachRow(w, h,
                Sub(y)
                    Dim top = If(y > 0, (y - 1) * stride, 0)
                    Dim centered = y * stride
                    Dim bottom = If(y < h - 1, (y + 1) * stride, (h - 1) * stride)
                    For x = 0 To w - 1
                        Dim o = centered + x * 4
                        Dim left = centered + If(x > 0, (x - 1) * 4, 0)
                        Dim right = centered + If(x < w - 1, (x + 1) * 4, (w - 1) * 4)
                        Dim ob = top + x * 4
                        Dim un = bottom + x * 4

                        Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                        ReadUnpremultiplied(srcBuf, o, ri, gi, bi, ai, cr, cg, cb, a)
                        Dim lr As Integer, lg As Integer, lb As Integer, la As Integer
                        Dim rr2 As Integer, rg As Integer, rb As Integer, ra As Integer
                        Dim tr As Integer, tg As Integer, tb As Integer, ta As Integer
                        Dim br As Integer, bg As Integer, bb As Integer, ba As Integer
                        ReadUnpremultiplied(srcBuf, left, ri, gi, bi, ai, lr, lg, lb, la)
                        ReadUnpremultiplied(srcBuf, right, ri, gi, bi, ai, rr2, rg, rb, ra)
                        ReadUnpremultiplied(srcBuf, ob, ri, gi, bi, ai, tr, tg, tb, ta)
                        ReadUnpremultiplied(srcBuf, un, ri, gi, bi, ai, br, bg, bb, ba)

                        WritePremultiplied(dstBuf, o, ri, gi, bi, ai,
                            ClampToByte(cr * center - amount * (lr + rr2 + tr + br)),
                            ClampToByte(cg * center - amount * (lg + rg + tg + bg)),
                            ClampToByte(cb * center - amount * (lb + rb + tb + bb)), a)
                    Next
                End Sub)

            Runtime.InteropServices.Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            Return result
        End Function

        Private Shared Function ApplyGeometryTransforms(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If adj.RotationDegrees = 0 AndAlso Not adj.FlipHorizontal AndAlso Not adj.FlipVertical Then
                Return source
            End If

            Dim normalized = ((adj.RotationDegrees Mod 360) + 360) Mod 360
            Dim sw = source.Width
            Dim sh = source.Height
            Dim rw = If(normalized = 90 OrElse normalized = 270, sh, sw)
            Dim rh = If(normalized = 90 OrElse normalized = 270, sw, sh)

            ' Farbtyp/Alpha der Quelle DURCHREICHEN: das war die einzige Stufe der
            ' Geometriekette ohne (Crop, Straighten, Resize, Canvas tun es alle) - nach Drehung/Flip
            ' lief die Pipeline im Plattform-Default-Format. Auf Plattformen, deren N32 nicht
            ' Bgra8888 ist, fielen danach alle Bgra-only-Pfade still um (Auswahl-Composite,
            ' Filmnegativ-Messung, Vignette-Pufferpfad).
            Dim rotated = New SKBitmap(rw, rh, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(rotated)
                canvas.Clear(SKColors.Transparent)
                Select Case normalized
                    Case 90
                        canvas.Translate(rw, 0)
                        canvas.RotateDegrees(90)
                    Case 180
                        canvas.Translate(rw, rh)
                        canvas.RotateDegrees(180)
                    Case 270
                        canvas.Translate(0, rh)
                        canvas.RotateDegrees(270)
                End Select
                canvas.DrawBitmap(source, 0, 0)
            End Using

            If Not adj.FlipHorizontal AndAlso Not adj.FlipVertical Then Return rotated

            Dim w = rotated.Width
            Dim h = rotated.Height
            Dim result = New SKBitmap(w, h, rotated.ColorType, rotated.AlphaType)
            Using canvas = New SKCanvas(result)
                Dim matrix = SKMatrix.Identity
                If adj.FlipHorizontal Then
                    matrix = matrix.PostConcat(SKMatrix.CreateScale(-1, 1, w / 2.0F, h / 2.0F))
                End If
                If adj.FlipVertical Then
                    matrix = matrix.PostConcat(SKMatrix.CreateScale(1, -1, w / 2.0F, h / 2.0F))
                End If
                canvas.SetMatrix(matrix)
                canvas.DrawBitmap(rotated, 0, 0)
            End Using
            rotated.Dispose()
            Return result
        End Function

        ''' <summary>Kurzform der Rasterverzerrung fuer den Render-Schluessel. Die Knoten selbst
        ''' waeren hunderte Zahlen im Schluessel jeder Kachel - gerundete Summen genuegen, um eine
        ''' Aenderung zu erkennen, und ein gezogener Punkt aendert sie garantiert.</summary>
        Private Shared Function ImageWarpSignature(v As ObjectWarp) As String
            If v Is Nothing OrElse v.IsEmpty Then Return ""
            Dim sum = 0.0, weighted = 0.0
            For i = 0 To v.Nodes.Length - 1
                sum += v.Nodes(i)
                weighted += v.Nodes(i) * (i + 1)
            Next
            Return String.Format(Globalization.CultureInfo.InvariantCulture, "{0}:{1}x{2}:{3:F4}:{4:F4}",
                                 v.Kind, v.Columns, v.Rows, sum, weighted)
        End Function

        ''' <summary>Die Verzerrung ueber ein KNOTENRASTER (Gitter, Linien, Verformen). Sie steht als
        ''' Rezeptwert im Bild und laeuft deshalb bei jedem Render mit, statt einmal in die Pixel
        ''' gebacken zu werden.
        '''
        ''' Sie ist die ERSTE Stufe der Kette, noch vor dem Beschnitt: das Raster liegt im
        ''' unbeschnittenen Quellraum, genau dort, wo es auch gezogen wurde. Liefe sie spaeter,
        ''' bedeuteten dieselben Prozentwerte nach jedem Zuschnitt etwas anderes.
        '''
        ''' Die Masse bleiben gleich - aus demselben Grund wie bei der Perspektive: die Stufe laeuft
        ''' im Bild- UND im Maskenweg, und unterschiedliche Ausgabemasse liessen die Maske nicht mehr
        ''' aufs Bild passen. Was aus dem Rahmen geschoben wird, ist abgeschnitten; wo nichts mehr
        ''' liegt, bleibt es durchsichtig.</summary>
        Private Shared Function ApplyImageWarp(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing OrElse adj Is Nothing Then Return source
            Dim v = adj.ImageWarp
            If v Is Nothing OrElse v.IsEmpty OrElse Not String.Equals(v.Kind, "Gitter", StringComparison.Ordinal) Then Return source
            Dim count = (v.Columns + 1) * (v.Rows + 1)
            ' Unbewegt heisst unbenutzt: kein Umkopieren, keine Neuabtastung.
            Dim moved = False
            For rowIdx = 0 To v.Rows
                For colIdx = 0 To v.Columns
                    Dim i = (rowIdx * (v.Columns + 1) + colIdx) * 2
                    If Math.Abs(v.Nodes(i) - colIdx * 100.0 / v.Columns) > 0.01 OrElse
                       Math.Abs(v.Nodes(i + 1) - rowIdx * 100.0 / v.Rows) > 0.01 Then
                        moved = True
                        Exit For
                    End If
                Next
                If moved Then Exit For
            Next
            If Not moved Then Return source

            Dim zx(count - 1) As Single
            Dim zy(count - 1) As Single
            For i = 0 To count - 1
                zx(i) = CSng(v.Nodes(i * 2) / 100.0 * source.Width)
                zy(i) = CSng(v.Nodes(i * 2 + 1) / 100.0 * source.Height)
            Next
            Dim warped = ImageGeometryMapper.WarpOverGrid(source, v.Columns, v.Rows, zx, zy)
            If warped Is Nothing OrElse Object.ReferenceEquals(warped, source) Then Return source
            Return warped
        End Function

        ''' <summary>Perspektivische Verzerrung. Die Bildmasse bleiben gleich - was aus dem Rahmen
        ''' kippt, wird abgeschnitten, und wo nichts mehr liegt, bleibt es durchsichtig. Der Regler
        ''' "Groesse" ist dafuer da, die leeren Ecken wieder zuzudecken.
        '''
        ''' Warum die Masse gleich bleiben: die Stufe laeuft im Bild- UND im Maskenweg. Wuerde sie
        ''' die Groesse aendern, muessten beide Wege exakt dieselbe neue Groesse errechnen, sonst
        ''' passt die Maske nicht mehr aufs Bild. Gleiche Masse machen das zur Selbstverstaendlichkeit
        ''' statt zu einer Bedingung, an die jemand denken muss.</summary>
        Private Shared Function ApplyPerspective(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing OrElse adj Is Nothing Then Return source
            Dim m = ImageGeometryMapper.WarpMatrix(source.Width, source.Height,
                                                          adj.PerspectiveHorizontal, adj.PerspectiveVertical,
                                                          adj.PerspectiveAspect, adj.PerspectiveScale,
                                                          ImageGeometryMapper.CornerOffset(adj))
            ' Unbenutzt heisst unbenutzt: kein Umkopieren, keine Neuabtastung.
            If m.IsIdentity Then Return source

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                canvas.SetMatrix(m)
                Using paint = New SKPaint With {.IsAntialias = True}
                    DrawBitmapSampled(canvas, source, 0, 0, SamplingHigh, paint)
                End Using
            End Using
            Return result
        End Function

        Private Shared Function ApplyStraighten(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            Dim degrees = adj.StraightenDegrees
            If Math.Abs(degrees) < 0.01F Then Return source

            Dim radians = Math.Abs(degrees) * Math.PI / 180.0

            If adj.StraightenExpandCanvas Then
                Dim expandedWidth = Math.Max(1, CInt(Math.Ceiling(source.Width * Math.Cos(radians) + source.Height * Math.Sin(radians))))
                Dim expandedHeight = Math.Max(1, CInt(Math.Ceiling(source.Width * Math.Sin(radians) + source.Height * Math.Cos(radians))))

                Dim expanded = New SKBitmap(expandedWidth, expandedHeight, source.ColorType, source.AlphaType)
                Using canvas = New SKCanvas(expanded)
                    ' Siehe ApplyCanvasResize: gefuellt wird einmal am Ende der Kette.
                    canvas.Clear(SKColors.Transparent)
                    canvas.Translate(expandedWidth / 2.0F, expandedHeight / 2.0F)
                    canvas.RotateDegrees(degrees)
                    Using paint = New SKPaint With {.IsAntialias = True}
                        DrawBitmapSampled(canvas, source, -source.Width / 2.0F, -source.Height / 2.0F, SamplingHigh, paint)
                    End Using
                End Using
                Return expanded
            End If

            Dim scale = Math.Max(
                source.Width / (source.Width * Math.Cos(radians) + source.Height * Math.Sin(radians)),
                source.Height / (source.Width * Math.Sin(radians) + source.Height * Math.Cos(radians)))
            scale = Math.Max(1.0, scale)

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                canvas.Translate(source.Width / 2.0F, source.Height / 2.0F)
                canvas.Scale(CSng(scale))
                canvas.RotateDegrees(degrees)
                Using paint = New SKPaint With {.IsAntialias = True}
                    DrawBitmapSampled(canvas, source, -source.Width / 2.0F, -source.Height / 2.0F, SamplingHigh, paint)
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Dekodiert und verarbeitet das Bild (alle Anpassungen/Objekte gebacken) und liefert die
        ''' Zauberstab-Maske am Saatpunkt in Bildpixeln. <paramref name="bounds"/> ist das umschließende
        ''' Rechteck in Bildpixeln.</summary>
        ''' <paramref name="workingFull"/>: Arbeitsbild statt Datei-Decode (siehe SaveImage; Besitz wechselt hierher).
        ''' <summary>Das fertig gerechnete ANZEIGEBILD, wie es der Editor zeigt - Besitz beim
        ''' Aufrufer. Fuer alles, was auf dem gesehenen Bild arbeiten muss statt auf der Datei:
        ''' Zauberstab, Objektauswahl per Modell.
        '''
        ''' <paramref name="workingFull"/> ist das Arbeitsbild, falls schon eines vorliegt; sonst
        ''' wird die Quelle gelesen. Es wird HIER verbraucht, der Aufrufer gibt es nicht selbst
        ''' frei.</summary>
        Public Shared Function RenderDisplayImage(sourcePath As String, adj As ImageAdjustments,
                                                 Optional workingFull As SKBitmap = Nothing) As SKBitmap
            Try
                Using original = If(workingFull, DecodeOriented(sourcePath))
                    If original Is Nothing Then Return Nothing
                    Return ProcessBitmap(original, adj)
                End Using
            Catch ex As Exception
                Return Nothing
            End Try
        End Function

        Public Shared Function BuildMagicWandMaskFromFile(sourcePath As String, adj As ImageAdjustments,
                                                          seedX As Integer, seedY As Integer, tolerance As Single,
                                                          ByRef bounds As SKRectI,
                                                          Optional workingFull As SKBitmap = Nothing,
                                                          Optional confineRect As SKRectI = Nothing,
                                                          Optional confine As SKBitmap = Nothing) As SKBitmap
            bounds = SKRectI.Empty
            Using processed = RenderDisplayImage(sourcePath, adj, workingFull)
                If processed Is Nothing Then Return Nothing
                Return BuildMagicWandMask(processed, seedX, seedY, tolerance, bounds, confineRect, confine)
            End Using
        End Function

        Private Shared Function Clamp(value As Single, min As Single, max As Single) As Single
            If Single.IsNaN(value) OrElse Single.IsInfinity(value) Then Return min
            Return Math.Max(min, Math.Min(max, value))
        End Function

        Private Shared Function ClampToByte(value As Double) As Byte
            If Double.IsNaN(value) Then Return 0
            Return CByte(Math.Max(0.0, Math.Min(255.0, Math.Round(value))))
        End Function

        Private Shared Sub RgbToHsl(rByte As Byte, gByte As Byte, bByte As Byte, ByRef h As Double, ByRef s As Double, ByRef l As Double)
            Dim r = rByte / 255.0
            Dim g = gByte / 255.0
            Dim b = bByte / 255.0
            Dim maxV = Math.Max(r, Math.Max(g, b))
            Dim minV = Math.Min(r, Math.Min(g, b))
            l = (maxV + minV) / 2.0

            If Math.Abs(maxV - minV) < 0.00001 Then
                h = 0
                s = 0
                Return
            End If

            Dim d = maxV - minV
            s = If(l > 0.5, d / (2.0 - maxV - minV), d / (maxV + minV))
            If maxV = r Then
                h = (g - b) / d + If(g < b, 6.0, 0.0)
            ElseIf maxV = g Then
                h = (b - r) / d + 2.0
            Else
                h = (r - g) / d + 4.0
            End If
            h *= 60.0
        End Sub

        Private Shared Function HslToRgb(h As Double, s As Double, l As Double, alpha As Byte) As SKColor
            If s <= 0 Then
                Dim gray = ClampToByte(l * 255.0)
                Return New SKColor(gray, gray, gray, alpha)
            End If

            Dim q = If(l < 0.5, l * (1.0 + s), l + s - l * s)
            Dim p = 2.0 * l - q
            Dim hk = h / 360.0
            Dim r = HueToRgb(p, q, hk + 1.0 / 3.0)
            Dim g = HueToRgb(p, q, hk)
            Dim b = HueToRgb(p, q, hk - 1.0 / 3.0)
            Return New SKColor(ClampToByte(r * 255.0), ClampToByte(g * 255.0), ClampToByte(b * 255.0), alpha)
        End Function

        Private Shared Function HueToRgb(p As Double, q As Double, t As Double) As Double
            If t < 0 Then t += 1
            If t > 1 Then t -= 1
            If t < 1.0 / 6.0 Then Return p + (q - p) * 6.0 * t
            If t < 1.0 / 2.0 Then Return q
            If t < 2.0 / 3.0 Then Return p + (q - p) * (2.0 / 3.0 - t) * 6.0
            Return p
        End Function

        Private Shared Function RenderHistogram(source As SKBitmap, width As Integer, height As Integer) As SKBitmap
            width = Math.Max(120, width)
            height = Math.Max(70, height)
            Dim counts = BuildChannelHistogramCounts(source)
            Dim maxBin = Math.Max(1, Math.Max(counts.R.Max(), Math.Max(counts.G.Max(), counts.B.Max())))

            Dim result = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Using canvas = New SKCanvas(result)
                canvas.Clear(New SKColor(18, 20, 24, 255))
                Using gridPaint = New SKPaint With {.Color = New SKColor(255, 255, 255, 26), .StrokeWidth = 1}
                    For i As Integer = 1 To 3
                        Dim x = CSng(width * i / 4.0)
                        canvas.DrawLine(x, 0, x, height, gridPaint)
                    Next
                End Using

                DrawHistogramChannel(canvas, counts.R, maxBin, width, height, New SKColor(255, 70, 70, 165))
                DrawHistogramChannel(canvas, counts.G, maxBin, width, height, New SKColor(70, 220, 90, 165))
                DrawHistogramChannel(canvas, counts.B, maxBin, width, height, New SKColor(70, 130, 255, 165))
            End Using
            Return result
        End Function

        ''' <summary>Waveform und RGB-Parade: was das Histogramm nicht kann.
        '''
        ''' Ein Histogramm zaehlt nur, WIE OFT ein Wert vorkommt, und verliert dabei, WO er steht.
        ''' Die Waveform behaelt die waagerechte Lage: jede Bildspalte wird zu einer Spalte des
        ''' Diagramms, senkrecht steht die Helligkeit, die Schwaerzung ist die Haeufigkeit. Damit
        ''' liest man ab, ob der Himmel links ausfrisst, waehrend rechts noch Zeichnung ist - eine
        ''' Frage, die das Histogramm nicht beantworten kann.
        '''
        ''' Die Parade ist dieselbe Darstellung, aber je Farbkanal nebeneinander. Sie ist das
        ''' Werkzeug fuer Farbstiche: liegen die drei Bloecke am unteren Rand nicht auf gleicher
        ''' Hoehe, hat das Bild in den Tiefen einen Stich, und man sieht sofort, in welchem Kanal.
        '''
        ''' <para>Beide teilen sich den Zaehlweg unten. Die Kosten haengen an der Abtastung, nicht
        ''' an der Bildgroesse: gezaehlt wird ein Raster von hoechstens ein paar hunderttausend
        ''' Punkten, genau wie beim Histogramm.</para></summary>
        Private Shared Function RenderWaveform(source As SKBitmap, width As Integer, height As Integer,
                                               parade As Boolean) As SKBitmap
            width = Math.Max(120, width)
            height = Math.Max(70, height)

            ' Bei der Parade teilen sich drei Bloecke die Breite; zwei schmale Luecken trennen sie.
            Const gap As Integer = 4
            Dim blocks = If(parade, 3, 1)
            Dim blockWidth = Math.Max(1, (width - gap * (blocks - 1)) \ blocks)

            Dim result = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Dim background = New SKColor(18, 20, 24, 255)
            ' Die Farben der Parade sind die des Histogramms, damit beide dasselbe meinen.
            Dim colors = If(parade,
                            {New SKColor(255, 70, 70), New SKColor(70, 220, 90), New SKColor(70, 130, 255)},
                            {New SKColor(226, 236, 240)})

            Using canvas = New SKCanvas(result)
                canvas.Clear(background)
            End Using

            Dim pixels = result.GetPixels()
            If pixels = IntPtr.Zero Then Return result
            Dim rowBytes = result.RowBytes
            Dim row(rowBytes - 1) As Byte

            ' Je Block ein eigenes Zaehlfeld: Spalte mal Helligkeitsstufe.
            Dim counts(blocks - 1)() As Integer
            For b = 0 To blocks - 1
                counts(b) = New Integer(blockWidth * height - 1) {}
            Next
            Dim peak = 0

            ' Waagerecht auf die Blockbreite abtasten, senkrecht auf hoechstens 600 Zeilen: das
            ' reicht fuer ein ruhiges Bild und deckelt die Kosten bei grossen Aufnahmen.
            '
            ' Gelesen wird die Quelle ZEILENWEISE aus ihrem Speicher und nicht Punkt fuer Punkt ueber
            ' GetPixel. Gemessen an einem 24-Megapixel-Bild kostete der Punktweg 78 Millisekunden je
            ' Lauf; im Editor faellt der auf dem Anzeigefaden an, und dort ist eine Achtzehntelsekunde
            ' ein sichtbares Stocken. Die Kanalreihenfolge muss dabei selbst beachtet werden - das ist
            ' der Dienst, den GetPixel leistet und der ihn so teuer macht.
            Dim sourcePixels = source.GetPixels()
            Dim sourceIsBgra = source.ColorType = SKColorType.Bgra8888
            Dim sourceIsRgba = source.ColorType = SKColorType.Rgba8888
            Dim fastRead = sourcePixels <> IntPtr.Zero AndAlso (sourceIsBgra OrElse sourceIsRgba)
            ' Die Szene des Editors ist VORMULTIPLIZIERT. Der schnelle Weg liest die Bytes roh, das
            ' Histogramm liest ueber GetPixel und bekommt sie zurueckgerechnet - ohne dieselbe
            ' Rechnung zeigen die drei Analysebilder am selben Bild verschiedene Werte, sobald es
            ' Transparenz gibt (Befund 2026-08-16).
            Dim sourceIsPremul = source.AlphaType = SKAlphaType.Premul
            Dim sourceRowBytes = source.RowBytes
            Dim sourceRow(If(fastRead, sourceRowBytes, 1) - 1) As Byte

            ' Die Spaltenzuordnung ist fuer jede Zeile dieselbe - einmal ausrechnen statt millionenfach.
            Dim columnOffsets(blockWidth - 1) As Integer
            For column = 0 To blockWidth - 1
                Dim sx = CInt(CLng(column) * (source.Width - 1) \ Math.Max(1, blockWidth - 1))
                If sx >= source.Width Then sx = source.Width - 1
                columnOffsets(column) = sx * 4
            Next

            Dim stepY = Math.Max(1, source.Height \ 600)
            For y As Integer = 0 To source.Height - 1 Step stepY
                If fastRead Then Marshal.Copy(IntPtr.Add(sourcePixels, y * sourceRowBytes), sourceRow, 0, sourceRowBytes)
                For column As Integer = 0 To blockWidth - 1
                    Dim c As SKColor
                    If fastRead Then
                        Dim p = columnOffsets(column)
                        Dim r0 As Byte, g0 As Byte, b0 As Byte
                        If sourceIsBgra Then
                            b0 = sourceRow(p) : g0 = sourceRow(p + 1) : r0 = sourceRow(p + 2)
                        Else
                            r0 = sourceRow(p) : g0 = sourceRow(p + 1) : b0 = sourceRow(p + 2)
                        End If
                        Dim a0 = sourceRow(p + 3)
                        ' Bei Alpha 0 stehen in einem vormultiplizierten Bild ohnehin Nullen, bei 255
                        ' aendert die Rechnung nichts - nur dazwischen muss zurueckgerechnet werden.
                        ' CInt VOR der Multiplikation: eine Byte-Multiplikation wirft bei Ueberlauf.
                        If sourceIsPremul AndAlso a0 > 0 AndAlso a0 < 255 Then
                            r0 = CByte(Math.Min(255, CInt(r0) * 255 \ CInt(a0)))
                            g0 = CByte(Math.Min(255, CInt(g0) * 255 \ CInt(a0)))
                            b0 = CByte(Math.Min(255, CInt(b0) * 255 \ CInt(a0)))
                        End If
                        c = New SKColor(r0, g0, b0)
                    Else
                        c = source.GetPixel(columnOffsets(column) \ 4, y)
                    End If
                    For b = 0 To blocks - 1
                        Dim value As Integer
                        If parade Then
                            value = If(b = 0, CInt(c.Red), If(b = 1, CInt(c.Green), CInt(c.Blue)))
                        Else
                            value = CInt(Math.Max(0, Math.Min(255, c.Red * 0.299 + c.Green * 0.587 + c.Blue * 0.114)))
                        End If
                        ' Oben ist hell: Wert 255 gehoert in die oberste Zeile.
                        Dim targetRow = (255 - value) * (height - 1) \ 255
                        Dim index = targetRow * blockWidth + column
                        counts(b)(index) += 1
                        If counts(b)(index) > peak Then peak = counts(b)(index)
                    Next
                Next
            Next
            If peak <= 0 Then Return result

            ' Zeilenweise ausgeben. Die Wurzelkennlinie hebt duenn besetzte Stellen an: ohne sie
            ' bliebe alles ausser den Flaechen unsichtbar, und gerade die duennen Spuren sind das
            ' Interessante an einer Waveform.
            For y As Integer = 0 To height - 1
                Marshal.Copy(IntPtr.Add(pixels, y * rowBytes), row, 0, rowBytes)
                For b = 0 To blocks - 1
                    Dim offsetX = b * (blockWidth + gap)
                    For column As Integer = 0 To blockWidth - 1
                        Dim count = counts(b)(y * blockWidth + column)
                        If count <= 0 Then Continue For
                        Dim strength = Math.Pow(count / CDbl(peak), 0.35)
                        Dim x = offsetX + column
                        If x >= width Then Exit For
                        Dim p = x * 4
                        Dim tint = colors(b)
                        ' Bgra8888: Blau, Gruen, Rot, Alpha.
                        row(p) = MixChannel(row(p), tint.Blue, strength)
                        row(p + 1) = MixChannel(row(p + 1), tint.Green, strength)
                        row(p + 2) = MixChannel(row(p + 2), tint.Red, strength)
                        row(p + 3) = 255
                    Next
                Next
                Marshal.Copy(row, 0, IntPtr.Add(pixels, y * rowBytes), rowBytes)
            Next

            ' Waagerechte Marken bei 0, 25, 50, 75 und 100 Prozent - ohne sie fehlt der Waveform
            ' der Massstab, und genau der ist ihr Zweck.
            Using canvas = New SKCanvas(result)
                Using gridPaint = New SKPaint With {.Color = New SKColor(255, 255, 255, 30), .StrokeWidth = 1}
                    For i As Integer = 0 To 4
                        Dim y = CSng((height - 1) * i / 4.0)
                        canvas.DrawLine(0, y, width, y, gridPaint)
                    Next
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Eine Farbe in Richtung einer anderen ziehen, mit 0 bis 1 als Staerke.</summary>
        Private Shared Function MixChannel(background As Byte, tint As Byte, strength As Double) As Byte
            Dim mixed = background + (CInt(tint) - CInt(background)) * strength
            Return CByte(Math.Max(0, Math.Min(255, mixed)))
        End Function

        Private Shared Sub DrawHistogramChannel(canvas As SKCanvas, bins As Integer(), maxBin As Integer, width As Integer, height As Integer, color As SKColor)
            Using paint = New SKPaint With {.Color = color, .StrokeWidth = Math.Max(1.0F, width / 256.0F), .IsAntialias = True, .BlendMode = SKBlendMode.Plus}
                For i As Integer = 0 To 255
                    If bins(i) <= 0 Then Continue For
                    Dim x = CSng(i / 255.0 * (width - 1))
                    Dim bar = CSng(Math.Pow(bins(i) / CDbl(maxBin), 0.45) * (height - 6))
                    canvas.DrawLine(x, height - 2, x, height - 2 - bar, paint)
                Next
            End Using
        End Sub


        ''' Wird für JEDEN Live-Vorschau-Frame aufgerufen (Regler, Filter, Annotationen, Histogramm),
        ''' daher lohnt sich hier der schnelle Direktkopie-Pfad (ImageOrientationService.ToAvaloniaBitmapFast)
        ''' besonders: kein PNG-Encode/Decode-Umweg mehr, nur eine reine Zeilen-Speicherkopie. Die
        ''' interne Verarbeitungs-Pipeline erzeugt Bitmaps praktisch immer als Bgra8888/Premul (siehe
        ''' DecodeOriented sowie die durchgängige source.ColorType/AlphaType-Weitergabe in den
        ''' Anpassungsfunktionen), der PNG-Umweg bleibt nur als Sicherheitsnetz für den seltenen Fall
        ''' eines abweichenden Farbformats erhalten.
        Friend Shared Function ToAvaloniaBitmap(skBitmap As SKBitmap) As Bitmap
            If skBitmap.ColorType = SKColorType.Bgra8888 AndAlso skBitmap.AlphaType = SKAlphaType.Premul Then
                Return ImageOrientationService.ToAvaloniaBitmapFast(skBitmap)
            End If
            ' Rgba8888/Premul (z.B. jede Ausgabe von ApplyAnnotations und damit die SZENE) ebenfalls per
            ' reiner Zeilen-Speicherkopie - der fruehere PNG-Encode/Decode-Umweg kostete pro Vorschau-
            ' Update einen kompletten 9,8-MP-Roundtrip (CPU hoch, Regler nur "haeppchenweise").
            If skBitmap.ColorType = SKColorType.Rgba8888 AndAlso skBitmap.AlphaType = SKAlphaType.Premul Then
                Return ToAvaloniaBitmapFastRgba(skBitmap)
            End If

            ' Jedes andere Format erst auf 8 Bit bringen und dann denselben schnellen Weg nehmen.
            '
            ' NICHT ueber einen PNG-Umweg, wie es hier frueher stand: SKImage.FromBitmap liefert
            ' fuer manche Farbtypen (etwa Rgba16161616) schlicht Nothing, und der Encode lief danach
            ' in eine NullReferenceException. Das riss beim Oeffnen jeder RAW-Datei die Anwendung um,
            ' solange das Arbeitsbild 16 Bit trug. Der 16-Bit-Weg ist inzwischen wieder
            ' ausgebaut, die Konvertierung hier bleibt aber der robustere Weg fuer alles Unerwartete.
            '
            ' Bewusst OHNE Try/Catch um den Aufruf: ein erster Anlauf fing hier breit ab und gab im
            ' Fehlerfall Nothing zurueck - damit haette ein echter Plattformfehler stumm ein leeres
            ' Bild ergeben statt einer Meldung. Faellt hier etwas aus, soll es auffallen.
            Dim acht = New SKBitmap(New SKImageInfo(skBitmap.Width, skBitmap.Height,
                                                    SKColorType.Bgra8888, SKAlphaType.Premul))
            Try
                Using cv As New SKCanvas(acht)
                    cv.Clear(SKColors.Transparent)
                    cv.DrawBitmap(skBitmap, 0, 0)
                End Using
                Return ImageOrientationService.ToAvaloniaBitmapFast(acht)
            Finally
                acht.Dispose()
            End Try
        End Function

        ''' <summary>Wie ImageOrientationService.ToAvaloniaBitmapFast, nur fuer Rgba8888/Premul
        ''' (Avalonia PixelFormat.Rgba8888) - reine Zeilenkopie ohne Kompressions-Umweg.</summary>
        Private Shared Function ToAvaloniaBitmapFastRgba(skBitmap As SKBitmap) As Bitmap
            Dim width = skBitmap.Width
            Dim height = skBitmap.Height
            Dim wb = New WriteableBitmap(New Avalonia.PixelSize(width, height), New Avalonia.Vector(96, 96),
                                         Avalonia.Platform.PixelFormat.Rgba8888, Avalonia.Platform.AlphaFormat.Premul)
            Using fb = wb.Lock()
                Dim srcStride = skBitmap.RowBytes
                Dim dstStride = fb.RowBytes
                Dim rowBytes = Math.Min(srcStride, dstStride)
                Dim srcBase = skBitmap.GetPixels()
                Dim buffer(rowBytes - 1) As Byte
                For y = 0 To height - 1
                    Runtime.InteropServices.Marshal.Copy(IntPtr.Add(srcBase, y * srcStride), buffer, 0, rowBytes)
                    Runtime.InteropServices.Marshal.Copy(buffer, 0, IntPtr.Add(fb.Address, y * dstStride), rowBytes)
                Next
            End Using
            Return wb
        End Function
    End Class

End Namespace
