Imports System.IO

Namespace Services
    Public NotInheritable Class VideoCaptureService
        Public Shared Function ResolveFolder(configured As String) As String
            If String.IsNullOrWhiteSpace(configured) Then
                Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FerrumPix Captures")
            End If
            If Not Path.IsPathFullyQualified(configured) Then Throw New ArgumentException(LocalizationService.T("Der Speicherordner für Aufnahmen muss ein absoluter Pfad sein."))
            Return Path.GetFullPath(configured)
        End Function

        Public Shared Function SaveCapturedFrame(temporaryPath As String, configuredFolder As String, videoPath As String, seconds As Double) As String
            Dim folder = ResolveFolder(configuredFolder)
            Directory.CreateDirectory(folder)
            Dim position = TimeSpan.FromSeconds(Math.Max(0, seconds))
            Dim name = $"{Path.GetFileNameWithoutExtension(videoPath)}_{CInt(Math.Floor(position.TotalHours)):00}-{position.Minutes:00}-{position.Seconds:00}-{position.Milliseconds:000}"
            For index = 0 To 9999
                Dim suffix = If(index = 0, "", $" ({index})")
                Dim destination = Path.Combine(folder, name & suffix & ".png")
                Try
                    File.Move(temporaryPath, destination, overwrite:=False)
                    Return destination
                Catch ex As IOException When File.Exists(destination)
                    ' Another capture may have claimed the name. Never overwrite it.
                End Try
            Next
            Throw New IOException(LocalizationService.T("Zu viele Aufnahmen mit demselben Namen. Die Aufnahme konnte nicht gespeichert werden."))
        End Function
    End Class
End Namespace
