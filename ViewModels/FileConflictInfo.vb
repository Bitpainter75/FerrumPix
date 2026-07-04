Imports System
Imports System.IO
Imports Avalonia.Media.Imaging
Imports FerrumPix.Services

Namespace ViewModels

    Public Class FileConflictInfo
        Public Property FilePath As String
        Public Property FileName As String
        Public Property FileSizeText As String
        Public Property ModifiedText As String
        Public Property DimensionsText As String
        Public Property FileTypeText As String
        Public Property Preview As Bitmap

        Public Shared Function FromPath(path As String) As FileConflictInfo
            Dim info As New FileConflictInfo With {
                .FilePath = path,
                .FileName = IO.Path.GetFileName(path),
                .FileSizeText = "-",
                .ModifiedText = "-",
                .DimensionsText = "-",
                .FileTypeText = If(Directory.Exists(path), "Ordner", "Datei")
            }

            If File.Exists(path) Then
                Dim fileInfo As New FileInfo(path)
                info.FileSizeText = FormatBytes(fileInfo.Length)
                info.ModifiedText = fileInfo.LastWriteTime.ToString("dd.MM.yyyy, HH:mm:ss")
                info.FileTypeText = $"{IO.Path.GetExtension(path).TrimStart("."c).ToUpperInvariant()}-Datei"
                TryLoadImageInfo(info, path)
            ElseIf Directory.Exists(path) Then
                Dim dirInfo As New DirectoryInfo(path)
                info.ModifiedText = dirInfo.LastWriteTime.ToString("dd.MM.yyyy, HH:mm:ss")
            End If

            Return info
        End Function

        Private Shared Sub TryLoadImageInfo(info As FileConflictInfo, path As String)
            Try
                Dim bitmap As Bitmap = Nothing
                If RawPreviewService.IsSupportedRaw(path) Then
                    Using preview = RawPreviewService.ExtractPreview(path)
                        If preview IsNot Nothing Then bitmap = ImageOrientationService.LoadOrientedAvaloniaBitmap(preview)
                    End Using
                Else
                    bitmap = ImageOrientationService.LoadOrientedAvaloniaBitmap(path)
                End If

                If bitmap IsNot Nothing Then
                    info.Preview = bitmap
                    info.DimensionsText = $"{bitmap.PixelSize.Width} x {bitmap.PixelSize.Height}"
                End If
            Catch
            End Try
        End Sub

        Private Shared Function FormatBytes(bytes As Long) As String
            If bytes < 1024 Then Return $"{bytes:N0} B"
            Dim kb = bytes / 1024.0
            If kb < 1024 Then Return $"{kb:N1} KB"
            Dim mb = kb / 1024.0
            If mb < 1024 Then Return $"{mb:N1} MB"
            Return $"{mb / 1024.0:N1} GB"
        End Function
    End Class

End Namespace
