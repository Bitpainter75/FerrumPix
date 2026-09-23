Imports System
Imports System.Collections.Concurrent
Imports System.IO
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Die von der Kamera in eine RAW-Datei gelegte JPEG-Vorschau als Bildquelle fuer den
    ''' VERGLEICH im Editor: orientiert, farbverwaltet und auf die Masse der Gegenseite gebracht.
    '''
    ''' WOZU: Das eingebettete JPEG ist die Auslegung DESSELBEN Bildes durch den Hersteller.
    ''' Danebengelegt zeigt es, wie weit die eigene Entwicklung davon abweicht.
    '''
    ''' WAS HIER NIE PASSIERT, und das ist der Punkt: Es wird NICHT entwickelt.
    ''' <see cref="RawPreviewService.ExtractPreviewWithFallback"/> faellt ohne brauchbare Vorschau
    ''' auf das echte Entwickeln der RAW zurueck - fuer den Betrachter richtig, hier falsch. Der
    ''' Vergleich zeigte dann UNSERE Entwicklung und schriebe sie der Kamera zu, und zwar ohne dass
    ''' es jemandem auffiele: das Bild saehe plausibel aus. Deshalb nur die beiden Wege, die
    ''' wirklich aus der Datei lesen; findet sich dort nichts Brauchbares, gibt es den Vergleich
    ''' fuer diese Datei nicht.
    '''
    ''' ZWEI SCHRANKEN, beide an echten Dateien gemessen (siehe RAW_UND_FARBE.md):
    ''' Eine zu kleine Vorschau (Leica M8: 320x240, Olympus C5050Z: 160x120) ist formatfuellend
    ''' gezogen ein Brei und verleitet zu dem Schluss, die eigene Entwicklung sei schaerfer - ein
    ''' Vergleich, den niemand angestellt hat. Und ein deutlich abweichendes Seitenverhaeltnis
    ''' (dieselbe M8: 4:3 gegen 3:2 der Aufnahme) liesse sich nur durch Zerren oder Beschneiden
    ''' aneinanderlegen; beides waere eine andere Aussage als die gewollte.
    ''' </summary>
    Public NotInheritable Class CameraJpegService

        Private Sub New()
        End Sub

        ''' <summary>Kuerzeste noch zugelassene laengste Kante der eingebetteten Vorschau.
        '''
        ''' <para>Gemessen an 414 RAW-Dateien aus dem Bestand, und die Zahl steht dort, wo eine
        ''' LUECKE ist. Die allermeisten Kameras betten nahezu vollaufgeloest ein. Darunter faellt
        ''' ein dichter Pulk alter Kameras auf genau 640x480 (die ganze Minolta-Reihe, Sony A100,
        ''' Ricoh GR, Epson RD1) und 570x375 (Nikon D100 und D1X); das naechstkleinere ist erst
        ''' 332x244. Zwischen 512 und 570 liegt also nichts, und die Schwelle kostet dort keine
        ''' Datei, die knapp daneben liegt.</para>
        '''
        ''' <para>Und 640x480 taugt: hochgezogen ist es weich, aber Farbe und Tonwert sind genau
        ''' die, die die Kamera erzeugt hat - und darum geht es hier. Scharf ist die Vorschau
        ''' ohnehin nie, sie wird in fast jedem Fall hochskaliert. Erst bei einem echten
        ''' Miniaturbild (160x120 und darunter) ist nicht mehr genug Bild da, um etwas zu
        ''' beurteilen. Ausgerechnet die alten Kameras haben es noetig: ihr Versatz zu unserer
        ''' Grundentwicklung ist der groesste (siehe RAW_UND_FARBE.md).</para></summary>
        Public Const MinLongestEdge As Integer = 512

        ''' <summary>Zugelassene Abweichung der Seitenverhaeltnisse. Gemessen liegen die normalen
        ''' Faelle bei 0,2 bis 0,4 Prozent (die Vorschau laesst die maskierten Randpixel des
        ''' Sensors weg); alles darueber ist ein anderer Bildausschnitt, kein Rundungsrest.</summary>
        Public Const MaxAspectDeviation As Double = 0.02

        ''' Ob eine Datei eine brauchbare Vorschau hat, kostet einen Dateiscan. Die Oberflaeche
        ''' fragt das je Bildwechsel, deshalb gemerkt - mit dem Schreibzeitpunkt im Schluessel,
        ''' damit eine ausgetauschte Datei nicht die alte Antwort behaelt.
        Private Shared ReadOnly _availability As New ConcurrentDictionary(Of String, Boolean)(StringComparer.Ordinal)

        ''' <summary>Traegt diese Datei eine eingebettete Vorschau, die als Vergleich taugt? Prueft
        ''' NUR die Groesse, nicht das Seitenverhaeltnis: dafuer braeuchte es die Gegenseite, und
        ''' die steht beim Aufbau der Werkzeugleiste noch nicht fest. Der seltene Fall faellt dann
        ''' in <see cref="TryLoadMatching"/> durch.</summary>
        Public Shared Function IsAvailable(rawPath As String) As Boolean
            If String.IsNullOrWhiteSpace(rawPath) Then Return False
            If Not RawPreviewService.IsSupportedRaw(rawPath) Then Return False

            Dim key As String
            Try
                key = rawPath & "|" & File.GetLastWriteTimeUtc(rawPath).Ticks.ToString(Globalization.CultureInfo.InvariantCulture)
            Catch
                Return False
            End Try

            Dim known As Boolean
            If _availability.TryGetValue(key, known) Then Return known

            Dim result = False
            Try
                Using stream = OpenEmbeddedPreview(rawPath)
                    result = stream IsNot Nothing
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("CameraJpegService.IsAvailable", ex)
            End Try

            ' Der Speicher ist ein Zwischenspeicher, kein Verzeichnis: bei einem langen Galerielauf
            ' waechst er sonst ueber tausende Pfade. Grob begrenzen genuegt.
            If _availability.Count > 512 Then _availability.Clear()
            _availability(key) = result
            Return result
        End Function

        ''' <summary>Die eingebettete Vorschau als sRGB-Bitmap, richtig herum gedreht. Nothing,
        ''' wenn die Datei keine brauchbare mitbringt.</summary>
        Public Shared Function TryLoadOriented(rawPath As String) As SKBitmap
            If String.IsNullOrWhiteSpace(rawPath) OrElse Not RawPreviewService.IsSupportedRaw(rawPath) Then Return Nothing
            Try
                Using stream = OpenEmbeddedPreview(rawPath)
                    If stream Is Nothing Then Return Nothing
                    Return DecodeOrientedSrgb(stream, rawPath)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("CameraJpegService.TryLoadOriented", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Die eingebettete Vorschau in EXAKT den Massen der Gegenseite, oder Nothing.
        '''
        ''' <para>Dass die Masse genau stimmen, ist keine Kosmetik: der Vergleichsregler legt beide
        ''' Bilder deckungsgleich uebereinander und schiebt eine Kante darueber. Waeren sie
        ''' verschieden gross, wanderte das Motiv beim Ziehen - und jede Aussage ueber Farbe und
        ''' Helligkeit ginge in dieser Bewegung unter.</para>
        '''
        ''' <para>Weicht das Seitenverhaeltnis um mehr als <see cref="MaxAspectDeviation"/> ab, wird
        ''' NICHT gezerrt und nicht beschnitten, sondern nichts geliefert. Ein gezerrter Vergleich
        ''' beantwortet eine Frage, die niemand gestellt hat.</para></summary>
        Public Shared Function TryLoadMatching(rawPath As String, targetWidth As Integer, targetHeight As Integer) As SKBitmap
            If targetWidth <= 0 OrElse targetHeight <= 0 Then Return Nothing

            Dim preview = TryLoadOriented(rawPath)
            If preview Is Nothing Then Return Nothing
            Try
                Dim previewAspect = preview.Width / CDbl(preview.Height)
                Dim targetAspect = targetWidth / CDbl(targetHeight)
                If Math.Abs(previewAspect - targetAspect) / targetAspect > MaxAspectDeviation Then
                    DiagnosticLogService.LogAlways("Editor.CameraJpeg",
                        $"verworfen=seitenverhaeltnis vorschau={preview.Width}x{preview.Height} ziel={targetWidth}x{targetHeight}")
                    Return Nothing
                End If

                If preview.Width = targetWidth AndAlso preview.Height = targetHeight Then
                    Dim exact = preview
                    preview = Nothing
                    Return exact
                End If

                Dim scaled = New SKBitmap(targetWidth, targetHeight, preview.ColorType, preview.AlphaType)
                Using canvas = New SKCanvas(scaled)
                    canvas.Clear(SKColors.Transparent)
                    Using paint = New SKPaint With {.IsAntialias = True}
                        ImageProcessor.DrawBitmapSampled(canvas, preview,
                                                         New SKRect(0, 0, preview.Width, preview.Height),
                                                         New SKRect(0, 0, targetWidth, targetHeight),
                                                         ImageProcessor.SamplingHigh, paint)
                    End Using
                End Using
                Return scaled
            Finally
                preview?.Dispose()
            End Try
        End Function

        ''' <summary>Der Strom mit den eingebetteten Bytes, oder Nothing. NUR die beiden Wege, die
        ''' wirklich aus der Datei lesen - erst der eigene Scanner (er sucht das groesste
        ''' eingebettete JPEG), dann LibRaws Thumbnail-API fuer die Formate, in denen der Scanner
        ''' nichts findet. Kein Entwickeln, siehe Klassenkommentar.</summary>
        Private Shared Function OpenEmbeddedPreview(rawPath As String) As MemoryStream
            Dim scanned = RawPreviewService.ExtractPreview(rawPath)
            If LongestEdge(scanned) >= MinLongestEdge Then Return scanned

            Dim thumb = RawDecodeService.TryExtractThumbnail(rawPath)
            If LongestEdge(thumb) >= MinLongestEdge Then
                scanned?.Dispose()
                Return thumb
            End If

            scanned?.Dispose()
            thumb?.Dispose()
            Return Nothing
        End Function

        Private Shared Function LongestEdge(stream As MemoryStream) As Integer
            If stream Is Nothing OrElse stream.Length = 0 Then Return 0
            Try
                Using data = SKData.CreateCopy(stream.ToArray())
                    Using codec = SKCodec.Create(data)
                        If codec Is Nothing Then Return 0
                        Return Math.Max(codec.Info.Width, codec.Info.Height)
                    End Using
                End Using
            Catch
                Return 0
            Finally
                Try
                    stream.Position = 0
                Catch
                End Try
            End Try
        End Function

        ''' <summary>Decode mit Orientierung und Farbwandlung, als SKBitmap. Derselbe Weg wie in
        ''' ImageOrientationService.LoadOrientedAvaloniaBitmap, nur ohne die Avalonia-Stufe am Ende.
        ''' Die Orientierung kommt ueber RawPreviewOrigin: eine eingebettete Vorschau traegt oft
        ''' KEIN eigenes Tag und liegt dann so quer da, wie der Sensor sie aufgenommen hat.</summary>
        Private Shared Function DecodeOrientedSrgb(stream As MemoryStream, rawPath As String) As SKBitmap
            stream.Position = 0
            Using data = SKData.CreateCopy(stream.ToArray())
                Using codec = SKCodec.Create(data)
                    If codec Is Nothing Then Return Nothing

                    Dim info = codec.Info
                    Dim origin = ImageOrientationService.RawPreviewOrigin(rawPath, codec.EncodedOrigin, info.Width, info.Height)
                    Dim sourceProfile = ColorManagementService.EffectiveProfile(info.ColorSpace, data)

                    Dim decodeInfo = New SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Dim original = New SKBitmap(decodeInfo)
                    Dim keep As SKBitmap = Nothing
                    Try
                        Dim decodeResult = codec.GetPixels(decodeInfo, original.GetPixels())
                        If decodeResult <> SKCodecResult.Success AndAlso decodeResult <> SKCodecResult.IncompleteInput Then Return Nothing

                        Dim corrected = ImageOrientationService.ApplyOrientation(original, origin)
                        Try
                            ' Nach der Geometrie, nicht davor: die Wandlung ist eine reine
                            ' Punktoperation, und danach liegt genau ein Puffer an.
                            Dim managed = ColorManagementService.ToSrgb(corrected, sourceProfile)
                            If Object.ReferenceEquals(managed, corrected) Then
                                ' Nichts zu wandeln: das Ergebnis IST der gedrehte Puffer.
                                keep = corrected
                                corrected = Nothing
                                Return keep
                            End If
                            keep = managed
                            Return keep
                        Finally
                            If corrected IsNot Nothing AndAlso Not Object.ReferenceEquals(corrected, original) AndAlso
                               Not Object.ReferenceEquals(corrected, keep) Then corrected.Dispose()
                        End Try
                    Finally
                        If Not Object.ReferenceEquals(original, keep) Then original.Dispose()
                    End Try
                End Using
            End Using
        End Function

        ''' <summary>Nur fuer den Pruefstand: die gemerkten Antworten vergessen.</summary>
        Public Shared Sub ClearAvailabilityCache()
            _availability.Clear()
        End Sub

    End Class

End Namespace
