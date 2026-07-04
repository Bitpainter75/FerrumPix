Imports System
Imports System.IO

Namespace Services

    ''' <summary>
    ''' Optionales Fehler-Logging für schwer reproduzierbare Probleme (z.B. Video-Wiedergabe,
    ''' Editor-Vorschau) - schreibt nur, wenn AppSettings.EnableDiagnosticLogging in den
    ''' Einstellungen aktiviert ist, damit im Normalbetrieb keine Datei anwächst. Über
    ''' SettingsViewModel.EnableDiagnosticLogging zuschaltbar, wenn ein Fehler untersucht werden soll.
    ''' </summary>
    Public NotInheritable Class DiagnosticLogService
        Private Sub New()
        End Sub

        Private Shared ReadOnly LogDirectory As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix", "logs")

        Private Shared ReadOnly LogPath As String = Path.Combine(LogDirectory, "diagnostics.log")

        Private Shared ReadOnly _writeLock As New Object()

        Public Shared Sub LogException(area As String, ex As Exception)
            If ex Is Nothing Then Return
            If Not AppSettingsService.Load().EnableDiagnosticLogging Then Return
            Try
                Dim entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{area}] {ex}" & Environment.NewLine & New String("-"c, 80) & Environment.NewLine
                SyncLock _writeLock
                    Directory.CreateDirectory(LogDirectory)
                    File.AppendAllText(LogPath, entry)
                End SyncLock
            Catch
            End Try
        End Sub
    End Class

End Namespace
