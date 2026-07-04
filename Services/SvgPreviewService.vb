Imports System
Imports System.IO
Imports SkiaSharp

Namespace Services

    ''' Rastert SVG-Dateien zu PNG, da SkiaSharps eigener Codec kein SVG (Vektorformat)
    ''' dekodieren kann. Liefert wie RawPreviewService einen dekodierbaren MemoryStream,
    ''' den die bestehenden Thumbnail-/Vorschau-Pfade wie ein normales Bild weiterverarbeiten.
    Public Class SvgPreviewService

        Public Shared Function IsSupportedSvg(filePath As String) As Boolean
            Return String.Equals(Path.GetExtension(filePath), ".svg", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' Rastert die SVG auf die längere Kante = maxDimension (Vektorgrafik verliert dabei
        ''' nichts, daher wird - anders als bei Rasterbildern - auch hochskaliert).
        ''' Liefert einen MemoryStream mit PNG-Daten (Position 0) oder Nothing bei Fehler.
        Public Shared Function ExtractPreview(filePath As String, Optional maxDimension As Integer = 2048) As MemoryStream
            Try
                Using svg As New Svg.Skia.SKSvg()
                    Dim picture = svg.Load(filePath)
                    If picture Is Nothing Then Return Nothing

                    Dim bounds = picture.CullRect
                    If bounds.Width <= 0 OrElse bounds.Height <= 0 Then Return Nothing

                    Dim longest = Math.Max(bounds.Width, bounds.Height)
                    Dim scale = CSng(maxDimension / CDbl(longest))

                    Dim ms As New MemoryStream()
                    If Not svg.Save(ms, SKColors.Transparent, SKEncodedImageFormat.Png, 100, scale, scale) Then Return Nothing
                    ms.Position = 0
                    Return ms
                End Using
            Catch
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
