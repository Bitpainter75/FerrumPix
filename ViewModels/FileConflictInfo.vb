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

        ''' <summary>Ueberschrift und Unterzeile der Karte. Sie gehoeren zum ANLASS und nicht zur
        ''' Datei: beim Kopieren kommt eine vorhandene Datei herueber, beim Stapel und beim Speichern
        ''' entsteht eine neue. Vorher stand beides fest im XAML, und der Stapel behauptete damit
        ''' "Datei, die kopiert/verschoben wird" ueber einem Bild, das erst noch gerechnet wird.</summary>
        Public Property Headline As String = ""
        Public Property Subtitle As String = ""

        ''' <summary>Die Datei, die GESCHRIEBEN werden soll - es gibt sie noch nicht.
        '''
        ''' Groesse, Zeitstempel und Masse stehen deshalb NICHT da: sie entstehen erst beim Rechnen.
        ''' Vorher las der Dialog hier die QUELLdatei aus und zeigte deren Werte als die der neuen
        ''' Datei - beim Verkleinern auf 800x800 stand dort weiter die Groesse und "1600 x 1600" des
        ''' Originals (Nutzerbefund 2026-08-06). Die Vorschau kommt weiter aus der Quelle, denn das
        ''' MOTIV stimmt ja; nur die Zahlen daneben duerfen nicht von ihr kommen.</summary>
        Public Shared Function ForPlannedWrite(targetPath As String, previewSourcePath As String) As FileConflictInfo
            Dim pending = LocalizationService.T("wird berechnet")
            Dim info As New FileConflictInfo With {
                .FilePath = targetPath,
                .FileName = IO.Path.GetFileName(targetPath),
                .FileSizeText = pending,
                .ModifiedText = "-",
                .DimensionsText = pending,
                .FileTypeText = $"{IO.Path.GetExtension(targetPath).TrimStart("."c).ToUpperInvariant()}-Datei"
            }
            ' Nur die Vorschau, nicht die Masse: TryLoadImageInfo wuerde beides setzen.
            If Not String.IsNullOrEmpty(previewSourcePath) AndAlso File.Exists(previewSourcePath) Then
                Dim previewOnly As New FileConflictInfo()
                TryLoadImageInfo(previewOnly, previewSourcePath)
                info.Preview = previewOnly.Preview
            End If
            Return info
        End Function

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
                    Using preview = RawPreviewService.ExtractPreviewWithFallback(path)
                        If preview IsNot Nothing Then bitmap = ImageOrientationService.LoadOrientedAvaloniaBitmap(preview)
                    End Using
                Else
                    bitmap = ImageOrientationService.LoadOrientedAvaloniaBitmapAuto(path)
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
