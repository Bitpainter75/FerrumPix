Imports System
Imports System.Threading
Imports System.Threading.Tasks
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>
    ''' „Datenbank bereinigen" fuer die Oberflaeche. Anzeige, Drossel und Dispatcher-Wechsel stehen
    ''' in <see cref="BackgroundRunViewModel"/>; hier steht nur, was diesen Lauf ausmacht.
    '''
    ''' <para>WARUM UEBERHAUPT EIN LAUF MIT ANZEIGE: das Aufraeumen sah aus wie ein gewoehnlicher
    ''' Knopf, arbeitete aber je Katalogzeile ein <c>File.Exists</c> ab. Bei einem Bestand auf einem
    ''' Netzlaufwerk sind das Minuten. Es lief dabei auf dem Anzeigefaden - die Anwendung stand, ohne
    ''' zu sagen warum, und wer nichts geschehen sieht, klickt noch einmal (Nutzerbefund). Jetzt
    ''' laeuft es im Hintergrund, meldet seinen Stand und laesst sich anhalten.</para>
    ''' </summary>
    Public Class CatalogCleanupViewModel
        Inherits BackgroundRunViewModel

        Private _cancellation As CancellationTokenSource

        Protected Overrides Sub RequestCancel()
            _cancellation?.Cancel()
        End Sub

        ''' <summary>Der Knopf „Bereinigen": verwaiste Katalogzeilen suchen und entfernen.</summary>
        Public Overrides Async Function StartAsync() As Task
            Dim ignored = Await RunWithDisplayAsync(LocalizationService.T("Katalog wird gelesen..."),
                                                    AddressOf PurgeOrphans).ConfigureAwait(False)
        End Function

        ''' <summary>Das Aufraeumen ausgewaehlter Ordner - dieselbe Anzeige, dieselbe Sperre.
        '''
        ''' EIN Anzeigeobjekt fuer beide Wege, weil ohnehin nur einer laufen darf: sie teilen sich
        ''' die prozessuebergreifende Sperre. Zwei Anzeigen koennten gleichzeitig etwas behaupten,
        ''' und eine davon waere falsch.
        '''
        ''' <para>GIBT DEN ABSCHLUSSSATZ ZURUECK. Die Anzeige dieses Laufs steht ueber der
        ''' Ordnerliste und ist nur sichtbar, SOLANGE er laeuft - das Ergebnis gehoert danach unter
        ''' die Liste, wo es auch vorher stand. Leer heisst: es lief gar nicht (schon ein Lauf
        ''' unterwegs), dann bleibt dort stehen, was steht.</para></summary>
        Public Function StartFolderCleanupAsync(work As Func(Of Action(Of Integer, Integer), CancellationToken, String)) As Task(Of String)
            Return RunWithDisplayAsync(LocalizationService.T("Wird entfernt..."),
                                       Function(token) RunUnderLock(Function() work(AddressOf ReportFolders, token)))
        End Function

        Private Sub ReportFolders(done As Integer, total As Integer)
            ReportThrottled(String.Format(LocalizationService.T("Ordner {0} von {1}"), done, total), done, total)
        End Sub

        Private Async Function RunWithDisplayAsync(startText As String, work As Func(Of CancellationToken, String)) As Task(Of String)
            If Not TryEnterRun() Then Return ""

            Dim cts As New CancellationTokenSource()
            _cancellation = cts
            Await SetRunningAsync(True).ConfigureAwait(False)
            Await SetStatusAsync(startText, 0, False).ConfigureAwait(False)

            Dim summary As String
            Try
                summary = Await Task.Run(Function() work(cts.Token)).ConfigureAwait(False)
            Catch ex As Exception
                DiagnosticLogService.LogException("Katalog.Bereinigen", ex)
                summary = LocalizationService.T("Das Bereinigen ist fehlgeschlagen")
            End Try

            ' Anzeige zuruecksetzen, ERST DANACH den Riegel freigeben - siehe LeaveRun. Und in einem
            ' eigenen Try, damit LeaveRun auch dann erreicht wird, wenn der Dispatcher beim
            ' Herunterfahren wirft; sonst waere das Aufraeumen fuer den Rest der Sitzung gesperrt.
            Try
                Await SetRunningAsync(False).ConfigureAwait(False)
                Await SetStatusAsync(summary, 100, False).ConfigureAwait(False)
            Catch ex As Exception
                DiagnosticLogService.LogException("Katalog.Bereinigen.Abschluss", ex)
            End Try

            _cancellation = Nothing
            cts.Dispose()
            LeaveRun()
            Return summary
        End Function

        ''' <summary>Haelt die prozessuebergreifende Sperre ueber der eigentlichen Arbeit.
        '''
        ''' Ueber die AUFRAEUMKLAMMER und nicht ueber TryAcquire: die Loeschwege holen sich dieselbe
        ''' Sperre gleich noch einmal, und ein zweiter echter Erwerb scheitert an der EIGENEN
        ''' Sperrdatei (FileShare.None gilt auch fuer den, der sie haelt). Der Loeschweg saehe dann
        ''' aus wie von einem fremden Fenster blockiert und gaebe wortlos 0 zurueck.</summary>
        Private Shared Function RunUnderLock(work As Func(Of String)) As String
            Dim crossProcess = BackgroundRunLock.TryEnterCleanup()
            If crossProcess Is Nothing Then
                Return LocalizationService.T("In einem anderen Fenster läuft gerade ein Hintergrundlauf. Bitte danach noch einmal versuchen.")
            End If
            Try
                Return work()
            Finally
                crossProcess.Dispose()
            End Try
        End Function

        ''' <summary>Der Lauf des Knopfes, auf einem Hintergrundfaden. Gibt den Abschlusssatz zurueck.</summary>
        Private Function PurgeOrphans(token As CancellationToken) As String
            Return RunUnderLock(
                Function()
                    Dim removed = LibraryService.Instance.PurgeOrphanedRecords(
                        Sub(done, total) ReportThrottled(
                            String.Format(LocalizationService.T("Prüfe {0} von {1}"), done, total), done, total),
                        token)

                    If token.IsCancellationRequested Then Return LocalizationService.T("Abgebrochen, es wurde nichts entfernt")
                    If removed = 0 Then Return LocalizationService.T("Keine verwaisten Einträge gefunden.")
                    Return String.Format(LocalizationService.T("{0} verwaiste Einträge entfernt."), removed)
                End Function)
        End Function

    End Class

End Namespace
