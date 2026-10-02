Imports System
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Die Formate, die Skia selbst nicht lesen kann (JPEG XL, HEIF/AVIF, TIFF, PSD/PSB, ICO), als
    ''' fertiges Skia-Bitmap, OHNE PNG dazwischen.
    '''
    ''' Bis hierher reichte jeder Leser seinen Decode als PNG-Strom weiter, und der Abnehmer las ihn
    ''' sofort wieder ein. Das kostete Zeit (an 18 MP gut 0,3 s auch ungepackt, vorher gut 2 s) und
    ''' Speicher: kurz vor dem Ende lagen Bitmap, Strom und das wieder gelesene Bitmap zugleich vor.
    ''' Gearbeitet wird intern mit Bitmaps; ein PNG entsteht nur an der Grenze zur Platte
    ''' (<see cref="PngEncoder"/>).
    '''
    ''' DAS ERGEBNIS IST DASSELBE BILD WIE NACH DEM PNG-UMWEG, Byte fuer Byte, und genau das haelt
    ''' eine Pruefung fest. Die Leser liefern Bgra8888 mit geradlinigem Alpha und ohne Farbraum;
    ''' das Einlesen eines PNG ergab Bgra8888 VORMULTIPLIZIERT mit dem Farbraum sRGB (deckende Bilder
    ''' blieben deckend). <see cref="ToDecodedForm"/> macht dasselbe. Gemessen an siebzehn Vorlagen
    ''' (JPEG XL mit Alpha und gedreht, HEIC in sRGB und Display P3, TIFF mit 8 und 16 Bit und mit
    ''' Alpha, PSD mit und ohne Alpha, ICO): keine einzige abweichende Stelle.
    '''
    ''' Farbmanagement und Drehung haben die Leser schon angewandt; keine zweite Korrektur.
    ''' </summary>
    Public NotInheritable Class ForeignImageDecoder

        Private Sub New()
        End Sub

        ''' <summary>Liest die Datei ueber einen eigenen Leser? HEIF und JPEG XL nur, wenn die
        ''' Bibliothek da ist - wie bisher in OpenSourceStream.</summary>
        Public Shared Function CanDecode(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Return IcoPreviewService.IsSupportedIco(path) OrElse
                   PsdPreviewService.IsSupportedPsd(path) OrElse
                   (HeifDecodeService.IsSupportedHeif(path) AndAlso HeifDecodeService.IsAvailable) OrElse
                   (JxlDecodeService.IsSupportedJxl(path) AndAlso JxlDecodeService.IsAvailable) OrElse
                   TiffPreviewService.IsSupportedTiff(path)
        End Function

        ''' <summary>Das Bild in der Form, die das Einlesen eines PNG ergab (Besitz beim
        ''' Aufrufer), oder Nothing.</summary>
        Public Shared Function TryDecode(path As String) As SKBitmap
            If String.IsNullOrWhiteSpace(path) Then Return Nothing
            Dim raw As SKBitmap
            If IcoPreviewService.IsSupportedIco(path) Then
                raw = IcoPreviewService.TryDecode(path)
            ElseIf PsdPreviewService.IsSupportedPsd(path) Then
                raw = PsdPreviewService.TryDecode(path)
            ElseIf HeifDecodeService.IsSupportedHeif(path) Then
                raw = HeifDecodeService.TryDecode(path)
            ElseIf JxlDecodeService.IsSupportedJxl(path) Then
                raw = JxlDecodeService.TryDecode(path)
            ElseIf TiffPreviewService.IsSupportedTiff(path) Then
                raw = TiffPreviewService.TryDecode(path)
            Else
                Return Nothing
            End If
            If raw Is Nothing Then Return Nothing
            Try
                Return ToDecodedForm(raw)
            Finally
                raw.Dispose()
            End Try
        End Function

        ''' <summary>Bgra8888 vormultipliziert (deckend bleibt deckend), Farbraum sRGB: genau das,
        ''' was SKBitmap.Decode aus einem PNG dieses Bildes machte. Neues Bitmap, die Quelle bleibt
        ''' beim Aufrufer.</summary>
        Friend Shared Function ToDecodedForm(source As SKBitmap) As SKBitmap
            Dim alpha = If(source.AlphaType = SKAlphaType.Opaque, SKAlphaType.Opaque, SKAlphaType.Premul)
            Dim info = New SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, alpha,
                                       If(source.ColorSpace, SKColorSpace.CreateSrgb()))
            Dim result = New SKBitmap(info)
            Using sourcePixels = source.PeekPixels(), targetPixels = result.PeekPixels()
                If sourcePixels Is Nothing OrElse targetPixels Is Nothing OrElse
                   Not sourcePixels.ReadPixels(targetPixels) Then
                    result.Dispose()
                    Return Nothing
                End If
            End Using
            Return result
        End Function

    End Class

End Namespace
