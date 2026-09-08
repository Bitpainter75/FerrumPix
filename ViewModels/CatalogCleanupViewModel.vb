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

        Public Overrides Async Function StartAsync() As Task
            If Not TryEnterRun() Then Return

            Dim cts As New CancellationTokenSource()
            _cancellation = cts
            Await SetRunningAsync(True).ConfigureAwait(False)
            Await SetStatusAsync(LocalizationService.T("Katalog wird gelesen..."), 0, False).ConfigureAwait(False)

            Dim abschluss As String
            Try
                abschluss = Await Task.Run(Function() Run(cts.Token)).ConfigureAwait(False)
            Catch ex As Exception
                DiagnosticLogService.LogException("Katalog.Bereinigen", ex)
                abschluss = LocalizationService.T("Das Bereinigen ist fehlgeschlagen")
            End Try

            ' Anzeige zuruecksetzen, ERST DANACH den Riegel freigeben - siehe LeaveRun. Und in einem
            ' eigenen Try, damit LeaveRun auch dann erreicht wird, wenn der Dispatcher beim
            ' Herunterfahren wirft; sonst waere das Aufraeumen fuer den Rest der Sitzung gesperrt.
            Try
                Await SetRunningAsync(False).ConfigureAwait(False)
                Await SetStatusAsync(abschluss, 100, False).ConfigureAwait(False)
            Catch ex As Exception
                DiagnosticLogService.LogException("Katalog.Bereinigen.Abschluss", ex)
            End Try

            _cancellation = Nothing
            cts.Dispose()
            LeaveRun()
        End Function

        ''' <summary>Der Lauf selbst, auf einem Hintergrundfaden. Gibt den Abschlusssatz zurueck.</summary>
        Private Function Run(token As CancellationToken) As String
            ' Nur EIN Fenster darf am Bestand schreiben. Ueber die AUFRAEUMKLAMMER und nicht ueber
            ' TryAcquire: PurgeOrphanedRecords holt sich dieselbe Sperre gleich noch einmal, und ein
            ' zweiter echter Erwerb scheitert an der eigenen Datei (FileShare.None gilt auch fuer
            ' den, der sie haelt). Der Loeschweg saehe dann aus wie von einem fremden Fenster
            ' blockiert und gaebe wortlos 0 zurueck.
            Dim crossProcess = BackgroundRunLock.TryEnterCleanup()
            If crossProcess Is Nothing Then
                Return LocalizationService.T("In einem anderen Fenster läuft gerade ein Hintergrundlauf. Bitte danach noch einmal versuchen.")
            End If

            Dim removed As Integer
            Try
                removed = LibraryService.Instance.PurgeOrphanedRecords(
                    Sub(done, total) ReportThrottled(
                        String.Format(LocalizationService.T("Prüfe {0} von {1}"), done, total), done, total),
                    token)
            Finally
                crossProcess.Dispose()
            End Try

            If token.IsCancellationRequested Then Return LocalizationService.T("Abgebrochen, es wurde nichts entfernt")
            If removed = 0 Then Return LocalizationService.T("Keine verwaisten Einträge gefunden.")
            Return String.Format(LocalizationService.T("{0} verwaiste Einträge entfernt."), removed)
        End Function

    End Class

End Namespace
