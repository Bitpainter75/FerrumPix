Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq

Namespace Services

    ''' <summary>
    ''' Canonical media classification shared by folder discovery, gallery items,
    ''' the viewer, video previews, and upload MIME detection.
    ''' </summary>
    Public NotInheritable Class MediaFormatService

        Private Sub New()
        End Sub

        Public Shared ReadOnly DisplayImageExtensions As String() = {
            ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp",
            ".heic", ".avif",
            ".ico", ".svg", ".fpx", ".psd", ".psb"
        }

        Public Shared ReadOnly VideoExtensions As String() = {
            ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"
        }

        Public Shared ReadOnly DisplayMediaExtensions As String() =
            DisplayImageExtensions.Concat(VideoExtensions).
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
                Case ".jpg", ".jpeg" : Return "image/jpeg"
                Case ".png" : Return "image/png"
                Case ".gif" : Return "image/gif"
                Case ".webp" : Return "image/webp"
                Case ".bmp" : Return "image/bmp"
                Case ".tif", ".tiff" : Return "image/tiff"
                Case ".heic" : Return "image/heic"
                Case ".heif" : Return "image/heif"
                Case ".avif" : Return "image/avif"
                Case ".mp4" : Return "video/mp4"
                Case ".mov" : Return "video/quicktime"
                Case ".mkv" : Return "video/x-matroska"
                Case ".avi" : Return "video/x-msvideo"
                Case ".webm" : Return "video/webm"
                Case Else : Return "application/octet-stream"
            End Select
        End Function

    End Class

End Namespace
