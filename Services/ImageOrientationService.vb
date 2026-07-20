Imports System.IO
Imports System.Runtime.InteropServices
Imports Avalonia
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform
Imports SkiaSharp

Namespace Services

    ' Liest und korrigiert das EXIF-Orientation-Tag (Werte 1-8), das Kameras/Handys setzen,
    ' wenn Bildpixel physisch anders gespeichert werden als sie angezeigt werden sollen.
    Public NotInheritable Class ImageOrientationService
        Private Sub New()
        End Sub

        ' SKCodec.Create liest nur den Datei-Header (keine Pixel-Decodierung) - günstig genug,
        ' um bei jeder Thumbnail-Erzeugung/jedem Bildladen aufgerufen zu werden.
        Public Shared Function ReadOrigin(stream As Stream) As SKEncodedOrigin
            Try
                Using codec = SKCodec.Create(stream)
                    If codec Is Nothing Then Return SKEncodedOrigin.TopLeft
                    Return codec.EncodedOrigin
                End Using
            Catch
                Return SKEncodedOrigin.TopLeft
            End Try
        End Function

        Public Shared Function ReadOrigin(filePath As String) As SKEncodedOrigin
            Try
                Using stream = File.OpenRead(filePath)
                    Return ReadOrigin(stream)
                End Using
            Catch
                Return SKEncodedOrigin.TopLeft
            End Try
        End Function

        ' Liefert bei TopLeft dieselbe Instanz zurück (kein Alloc, kein Draw). Andernfalls ein
        ' neues SKBitmap mit der EXIF-Korrektur gebacken (Breite/Höhe bei 5/6/7/8 vertauscht).
        ' Aufrufer müssen das Ergebnis nur disposen, wenn es NICHT dieselbe Referenz wie "source" ist.
        Public Shared Function ApplyOrientation(source As SKBitmap, origin As SKEncodedOrigin) As SKBitmap
            If source Is Nothing OrElse origin = SKEncodedOrigin.TopLeft Then Return source

            Dim sw = source.Width
            Dim sh = source.Height
            Dim swapped = origin = SKEncodedOrigin.LeftTop OrElse origin = SKEncodedOrigin.RightTop OrElse
                          origin = SKEncodedOrigin.RightBottom OrElse origin = SKEncodedOrigin.LeftBottom
            Dim resultWidth = If(swapped, sh, sw)
            Dim resultHeight = If(swapped, sw, sh)

            Dim result = New SKBitmap(resultWidth, resultHeight, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                Select Case origin
                    Case SKEncodedOrigin.TopRight            ' 2: horizontal spiegeln
                        canvas.Translate(sw, 0)
                        canvas.Scale(-1, 1)
                    Case SKEncodedOrigin.BottomRight          ' 3: 180 Grad drehen
                        canvas.Translate(sw, sh)
                        canvas.RotateDegrees(180)
                    Case SKEncodedOrigin.BottomLeft           ' 4: vertikal spiegeln
                        canvas.Translate(0, sh)
                        canvas.Scale(1, -1)
                    Case SKEncodedOrigin.LeftTop              ' 5: transponieren
                        canvas.RotateDegrees(90)
                        canvas.Scale(1, -1)
                    Case SKEncodedOrigin.RightTop             ' 6: 90 Grad im Uhrzeigersinn
                        canvas.Translate(sh, 0)
                        canvas.RotateDegrees(90)
                    Case SKEncodedOrigin.RightBottom          ' 7: transversal spiegeln
                        canvas.Translate(sh, sw)
                        canvas.RotateDegrees(270)
                        canvas.Scale(1, -1)
                    Case SKEncodedOrigin.LeftBottom           ' 8: 270 Grad im Uhrzeigersinn
                        canvas.Translate(0, sw)
                        canvas.RotateDegrees(270)
                End Select
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return result
        End Function

        ' Kompletter Decode-und-Korrektur-Vorgang für Stellen, die eine Avalonia-Bitmap brauchen.
        ' Der weit überwiegende Teil der Bilder braucht KEINE Korrektur (TopLeft) - für die wird
        ' sofort auf den nativen, schnellen Avalonia-Decoder zurückgefallen (New Bitmap(stream)),
        ' exakt wie vor der EXIF-Korrektur. Nur Bilder mit tatsächlich abweichender Orientierung
        ' nehmen den langsameren SKCodec-Pfad, und selbst der kopiert die Pixel direkt statt sie
        ' (wie in einer früheren Version) über einen PNG-Encode/Decode-Umweg zu schicken.
        Public Shared Function LoadOrientedAvaloniaBitmap(stream As Stream) As Bitmap
            ' Den Inhalt EINMAL puffern und alle Decode-Wege daraus bedienen.
            ' Grund: SKCodec.Create(Stream) uebernimmt den Strom, und manche Codecs - allen voran
            ' WebP - schliessen ihn dabei sofort. Das spaetere stream.Seek fuer den Rueckfall warf
            ' dann ObjectDisposedException ("Cannot access a closed file"), womit sich WebP-Dateien
            ' im Editor gar nicht anzeigen liessen (gefunden 2026-07-20 durch die neue Pruefung
            ' "Anzeigebild aus dem Arbeitsbild stimmt mit dem separaten Decode ueberein").
            ' ImageProcessor.DecodeOriented hatte dieselbe Falle und loest sie genauso - der
            ' Anzeigeweg war nur nie nachgezogen worden.
            Dim data As SKData
            Try
                data = SKData.Create(stream)
            Catch
                data = Nothing
            End Try
            If data Is Nothing Then Return Nothing

            Using data
                Using codec = SKCodec.Create(data)
                    If codec Is Nothing OrElse codec.EncodedOrigin = SKEncodedOrigin.TopLeft Then
                        Return BitmapFromData(data)
                    End If

                    Dim info = codec.Info
                    Dim decodeInfo = New SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Using original = New SKBitmap(decodeInfo)
                        Dim decodeResult = codec.GetPixels(decodeInfo, original.GetPixels())
                        If decodeResult <> SKCodecResult.Success AndAlso decodeResult <> SKCodecResult.IncompleteInput Then
                            Return BitmapFromData(data)
                        End If

                        Dim corrected = ApplyOrientation(original, codec.EncodedOrigin)
                        Try
                            Return ToAvaloniaBitmapFast(corrected)
                        Finally
                            If Not Object.ReferenceEquals(corrected, original) Then corrected.Dispose()
                        End Try
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Avalonia-Bitmap aus gepufferten Bilddaten - der Rueckfall, wenn keine
        ''' Orientierungskorrektur noetig oder moeglich ist. Eigener Strom je Aufruf, damit der
        ''' Puffer wiederverwendbar bleibt.</summary>
        Private Shared Function BitmapFromData(data As SKData) As Bitmap
            Using ms As New MemoryStream(data.ToArray())
                Return New Bitmap(ms)
            End Using
        End Function

        Public Shared Function LoadOrientedAvaloniaBitmap(filePath As String) As Bitmap
            Using stream = File.OpenRead(filePath)
                Return LoadOrientedAvaloniaBitmap(stream)
            End Using
        End Function

        ''' Wie LoadOrientedAvaloniaBitmap(filePath), erkennt aber Formate, die SkiaSharp nicht
        ''' selbst dekodiert, und reicht stattdessen deren aufbereiteten Strom herein: RAW über
        ''' RawPreviewService.ExtractPreviewWithFallback, PSD/PSB über PsdPreviewService.
        ''' Liefert Nothing, wenn daraus nichts zu holen war.
        '''
        ''' Zur Einordnung der drei RAW-Wege - sie sind bewusst verschieden:
        '''   * ANZEIGE (hier): schnelle eingebettete Vorschau; findet der Scanner nichts, stößt
        '''     ExtractPreviewWithFallback doch noch libraw an (etwa bei Leica-RAWs).
        '''   * RENDER/ARBEITSBILD (ImageProcessor.DecodeOriented): echte Entwicklung über libraw,
        '''     erst danach der Vorschau-Rückfall.
        '''   * SPEICHERN: die RAW-Datei ist nie Ziel - Export in eine neue Datei, Reglerstände ins
        '''     .fpxmp-Sidecar.
        ''' WICHTIG: das ist der ANZEIGE-Pfad des Editors (CurrentImage) - er ist von
        ''' ImageProcessor.OpenSourceStream (Render/Export) getrennt, beide muessen dieselben
        ''' Sonderformate kennen. Genau das fehlte fuer PSD: die Render-Pipeline konnte es,
        ''' der Editor zeigte trotzdem nichts (Nutzerbefund 2026-07-19).
        Public Shared Function LoadOrientedAvaloniaBitmapAuto(filePath As String) As Bitmap
            If RawPreviewService.IsSupportedRaw(filePath) Then
                Using stream = RawPreviewService.ExtractPreviewWithFallback(filePath)
                    If stream Is Nothing Then Return Nothing
                    Return LoadOrientedAvaloniaBitmap(stream)
                End Using
            End If
            If PsdPreviewService.IsSupportedPsd(filePath) Then
                Using stream = PsdPreviewService.ExtractPreview(filePath)
                    If stream Is Nothing Then Return Nothing
                    Return LoadOrientedAvaloniaBitmap(stream)
                End Using
            End If
            Return LoadOrientedAvaloniaBitmap(filePath)
        End Function

        ' Kopiert die dekodierten Pixel direkt in eine WriteableBitmap (Bgra8888/Premul passt
        ' exakt zum Decode-Format oben) - kein Kompressions-Umweg über PNG wie zuvor, deutlich
        ' schneller, da hier nur pro Zeile eine reine Speicherkopie stattfindet.
        Friend Shared Function ToAvaloniaBitmapFast(skBitmap As SKBitmap) As Bitmap
            Dim width = skBitmap.Width
            Dim height = skBitmap.Height
            Dim wb = New WriteableBitmap(New PixelSize(width, height), New Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul)
            Using fb = wb.Lock()
                Dim srcStride = skBitmap.RowBytes
                Dim dstStride = fb.RowBytes
                Dim rowBytes = Math.Min(srcStride, dstStride)
                Dim srcBase = skBitmap.GetPixels()
                Dim buffer(rowBytes - 1) As Byte
                For y = 0 To height - 1
                    Marshal.Copy(IntPtr.Add(srcBase, y * srcStride), buffer, 0, rowBytes)
                    Marshal.Copy(buffer, 0, IntPtr.Add(fb.Address, y * dstStride), rowBytes)
                Next
            End Using
            Return wb
        End Function
    End Class

End Namespace
