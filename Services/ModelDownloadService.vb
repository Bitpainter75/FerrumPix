Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net.Http
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Holt Modelldateien auf ausdrueckliche Anforderung.
    '''
    ''' BEWUSST ein eigener Dienst, getrennt von KiModellService. Der darf nichts aus dem Netz holen
    ''' koennen, und ein Waechter haelt das fest - waere der Netzzugriff dort eingebaut, waere die
    ''' Zusage "laedt nie von selbst" nur noch ein Vorsatz. Hier gibt es Netzzugriff, und er hat
    ''' genau einen Aufrufer: den Knopf in den Einstellungen.
    '''
    ''' Zwei Dinge, die nicht verhandelbar sind:
    '''
    ''' Die PRUEFSUMME wird geprueft, und sie steht in der Anwendung. Ein Modell wird einer nativen
    ''' Laufzeit zum Fressen gegeben; wer die Datei unterschiebt, bestimmt, was dort laeuft. Eine
    ''' Pruefsumme, die mit der Datei kaeme, wuerde derselbe Angreifer mitliefern. Stimmt sie nicht,
    ''' wird die Datei geloescht statt benutzt.
    '''
    ''' Geschrieben wird ATOMAR: erst in eine Nachbardatei, dann umbenannt. Ein abgebrochener
    ''' Download darf keine halbe Datei am Zielort hinterlassen, die beim naechsten Start als
    ''' vorhandenes Modell gilt.</summary>
    Public NotInheritable Class ModelDownloadService

        Private Sub New()
        End Sub

        Private Shared ReadOnly _client As New Lazy(Of HttpClient)(
            Function()
                Dim k = New HttpClient()
                k.Timeout = TimeSpan.FromMinutes(30)
                ' Kein Kennzeichen, das ueber die Anwendung hinaus etwas verraet.
                k.DefaultRequestHeaders.UserAgent.ParseAdd("FerrumPix")
                Return k
            End Function)

        Public Enum Result
            Done
            AlreadyPresent
            NetworkError
            ChecksumMismatch
            Cancelled
        End Enum

        ''' <summary>Ein Modell holen. <paramref name="progress"/> bekommt 0 bis 1.</summary>
        Public Shared Async Function FetchAsync(entry As AiModelService.ModelEntry,
                                               Optional progress As IProgress(Of Double) = Nothing,
                                               Optional cancel As CancellationToken = Nothing) As Task(Of Result)
            If entry Is Nothing Then Return Result.NetworkError
            If AiModelService.IsIntact(entry) Then Return Result.AlreadyPresent

            Dim folder = AiModelService.ModelFolder
            Dim target = Path.Combine(folder, entry.FileName)
            ' Die Nachbardatei liegt im SELBEN Ordner - nur dann ist das Umbenennen ein
            ' Verzeichniseintrag und keine Kopie ueber Dateisystemgrenzen hinweg.
            Dim half = target & ".unvollstaendig"
            Try
                Directory.CreateDirectory(folder)
                If File.Exists(half) Then File.Delete(half)

                Using response = Await _client.Value.GetAsync(entry.Address,
                                                             HttpCompletionOption.ResponseHeadersRead, cancel)
                    If Not response.IsSuccessStatusCode Then Return Result.NetworkError
                    Dim total = If(response.Content.Headers.ContentLength.HasValue,
                                    response.Content.Headers.ContentLength.Value, entry.Bytes)
                    Using source = Await response.Content.ReadAsStreamAsync(cancel)
                        Using sink = New FileStream(half, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                            Dim buffer(81919) As Byte
                            Dim bytesRead As Integer
                            Dim sum As Long = 0
                            Do
                                bytesRead = Await source.ReadAsync(buffer, 0, buffer.Length, cancel)
                                If bytesRead <= 0 Then Exit Do
                                Await sink.WriteAsync(buffer, 0, bytesRead, cancel)
                                sum += bytesRead
                                If progress IsNot Nothing AndAlso total > 0 Then
                                    progress.Report(Math.Min(1.0, sum / CDbl(total)))
                                End If
                            Loop
                        End Using
                    End Using
                End Using

                ' ERST pruefen, DANN an den Zielort. Eine Datei, die den Namen des Modells traegt,
                ' soll nie einen Augenblick lang ungeprueft dort liegen.
                Dim actualSum = AiModelService.ChecksumOf(half)
                If Not String.Equals(actualSum, entry.Sha256, StringComparison.OrdinalIgnoreCase) Then
                    Try
                        File.Delete(half)
                    Catch
                    End Try
                    DiagnosticLogService.LogAlways("ModellDownload",
                        $"{entry.FileName}: Pruefsumme {actualSum} statt {entry.Sha256}")
                    Return Result.ChecksumMismatch
                End If

                If File.Exists(target) Then File.Delete(target)
                File.Move(half, target)
                AiModelService.CheckAgain()
                Return Result.Done
            Catch ex As OperationCanceledException
                Try
                    If File.Exists(half) Then File.Delete(half)
                Catch
                End Try
                Return Result.Cancelled
            Catch ex As Exception
                Try
                    If File.Exists(half) Then File.Delete(half)
                Catch
                End Try
                DiagnosticLogService.LogAlways("ModellDownload", entry.FileName & ": " & ex.Message)
                Return Result.NetworkError
            End Try
        End Function

    End Class

End Namespace
