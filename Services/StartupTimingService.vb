Imports System.Diagnostics
Imports System.IO

Namespace Services
    ' Opt-in trace for measuring process launch, UI construction and window readiness.
    Public NotInheritable Class StartupTimingService
        Public Shared Sub Mark(stage As String)
            Dim target = Environment.GetEnvironmentVariable("FERRUMPIX_STARTUP_TRACE")
            If String.IsNullOrWhiteSpace(target) Then Return
            Try
                Using process = Diagnostics.Process.GetCurrentProcess()
                    File.AppendAllText(target, $"{Environment.ProcessId} {DateTime.UtcNow.Subtract(process.StartTime.ToUniversalTime()).TotalMilliseconds:F0}ms {stage}{Environment.NewLine}")
                End Using
            Catch
                ' Diagnostics must never prevent startup.
            End Try
        End Sub
    End Class
End Namespace
