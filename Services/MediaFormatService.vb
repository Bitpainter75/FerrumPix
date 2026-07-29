Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq

Namespace Services

    ''' <summary>
    ''' Kanonische Medienformat-Liste. Dateisuche, Kachelmodell, Viewer und Video-Vorschau müssen
    ''' dieselbe Antwort geben; getrennte Kopien ließen z.B. 3GP als Bild in die Galerie fallen.
    ''' </summary>
    Public NotInheritable Class MediaFormatService

        Private Sub New()
        End Sub

        Public Shared ReadOnly ImageExtensions As String() = {
            ".jpg", ".jpeg", ".jpe", ".insp", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp",
            ".heic", ".heif", ".hif", ".avif", ".jp2", ".j2k", ".jxl", ".mpo",
            ".ico", ".svg", ".fpx", ".psd", ".psb"
        }

        Public Shared ReadOnly VideoExtensions As String() = {
            ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".webm",
            ".3gp", ".3gpp", ".3g2", ".mts", ".m2ts", ".m2t", ".ts",
            ".mpeg", ".mpg", ".mpe", ".insv", ".mxf",
            ".wmv", ".flv", ".ogv", ".vob"
        }

        Public Shared ReadOnly DisplayMediaExtensions As String() =
            ImageExtensions.Concat(VideoExtensions).
                Concat(RawPreviewService.SupportedExtensions).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToArray()

        Public Shared Function IsVideo(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Return VideoExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase)
        End Function

        Public Shared Function IsSvg(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Return String.Equals(Path.GetExtension(filePath), ".svg", StringComparison.OrdinalIgnoreCase)
        End Function

        Public Shared Function IsDisplayMedia(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Return DisplayMediaExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase)
        End Function

        Public Shared Function VideoPickerPatterns() As IEnumerable(Of String)
            Return VideoExtensions.Select(Function(extension) "*" & extension)
        End Function

        Public Shared Function GuessMimeType(filePath As String) As String
            Select Case Path.GetExtension(filePath).ToLowerInvariant()
                Case ".jpg", ".jpeg", ".jpe", ".insp" : Return "image/jpeg"
                Case ".png" : Return "image/png"
                Case ".gif" : Return "image/gif"
                Case ".webp" : Return "image/webp"
                Case ".bmp" : Return "image/bmp"
                Case ".tif", ".tiff" : Return "image/tiff"
                Case ".heic", ".hif" : Return "image/heic"
                Case ".heif" : Return "image/heif"
                Case ".avif" : Return "image/avif"
                Case ".jp2", ".j2k" : Return "image/jp2"
                Case ".jxl" : Return "image/jxl"
                Case ".mpo" : Return "image/jpeg"
                Case ".svg" : Return "image/svg+xml"
                Case ".psd", ".psb" : Return "image/vnd.adobe.photoshop"
                Case ".mp4", ".insv" : Return "video/mp4"
                Case ".m4v" : Return "video/x-m4v"
                Case ".mov" : Return "video/quicktime"
                Case ".mkv" : Return "video/x-matroska"
                Case ".avi" : Return "video/x-msvideo"
                Case ".webm" : Return "video/webm"
                Case ".3gp", ".3gpp" : Return "video/3gpp"
                Case ".3g2" : Return "video/3gpp2"
                Case ".mts", ".m2ts", ".m2t", ".ts" : Return "video/mp2t"
                Case ".mpeg", ".mpg", ".mpe", ".vob" : Return "video/mpeg"
                Case ".mxf" : Return "application/mxf"
                Case ".wmv" : Return "video/x-ms-wmv"
                Case ".flv" : Return "video/x-flv"
                Case ".ogv" : Return "video/ogg"
                Case Else : Return "application/octet-stream"
            End Select
        End Function

    End Class

End Namespace
