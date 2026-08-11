Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>
    ''' Die Gesichtssuche ueber die UEBERWACHTEN ORDNER - denselben Bestand, den auch der
    ''' Katalogindex durchgeht.
    '''
    ''' Warum neben dem Knopf in der Galerie: der dort sucht in dem, was gerade zu sehen ist, also
    ''' in einem Ordner oder einer Auswahl. Wer seinen ganzen Bestand einmal durchsuchen lassen
    ''' will, muesste sonst Ordner fuer Ordner hineingehen. Die ueberwachten Ordner stehen ohnehin
    ''' schon da; sie hier ein zweites Mal einzutragen waere Arbeit ohne Gewinn.
    '''
    ''' <para>NICHT MIT force. Der Knopf in der Galerie sucht jedes Bild neu, auch ein schon
    ''' durchsuchtes - dort meint jemand genau diesen einen Ordner und will ein Ergebnis sehen.
    ''' Ueber den ganzen Bestand waere das bei jedem Klick von vorn, und der Lauf dauert Stunden.
    ''' Eine verbesserte Erkennung kommt trotzdem an: die Fassung steht im Vermerk
    ''' (<see cref="FaceScanRunner.ScanVersion"/>), und der Altbestand faellt dann von selbst
    ''' durch.</para>
    '''
    ''' <para>VIDEOS BLEIBEN DRAUSSEN. Die Erkennung dekodiert ein Standbild; bei einer Videodatei
    ''' kommt dabei nichts heraus, der Lauf zaehlte sie als Fehlschlag und vermerkte sie trotzdem
    ''' als durchsucht. In einem Bestand mit vielen Videos waere die gemeldete Zahl damit zur
    ''' Haelfte gelogen.</para>
    ''' </summary>
    Public Class FaceIndexViewModel
        Inherits BackgroundRunViewModel

        Private _cancellation As CancellationTokenSource

        ''' <summary>Startbar, wenn die Personenerkennung an ist und NIRGENDS ein Gesichtsdurchlauf
        ''' laeuft - auch nicht der aus der Galerie. Der Dienst laesst nur einen zu; ein Knopf, der
        ''' klickbar aussieht und nichts tut, ist schlechter als einer, der grau ist.
        '''
        ''' Ob Ordner eingetragen sind, zaehlt hier NICHT mehr: gestartet wird ueber die Ordnerliste
        ''' fuer eine Zeile oder eine gefilterte Menge, und dort steht auch der Ordner, der gar nicht
        ''' ueberwacht wird.</summary>
        Public Overrides ReadOnly Property CanStart As Boolean
            Get
                Return Not IsRunning AndAlso Not FaceScanRunner.IsRunning AndAlso FaceDetectionService.Enabled
            End Get
        End Property

        ''' <summary>Warum der Knopf grau ist. Ohne diesen Satz sucht jemand den Fehler bei sich:
        ''' die Personenerkennung ist eine eigene Einstellung weiter unten, und dass sie aus ist,
        ''' sieht man dem Knopf nicht an.</summary>
        Public ReadOnly Property DisabledHint As String
            Get
                If IsRunning OrElse FaceScanRunner.IsRunning Then Return ""
                If Not FaceDetectionService.Available Then
                    Return LocalizationService.T("Die Personenerkennung steht auf diesem Gerät nicht bereit")
                End If
                If Not FaceDetectionService.Enabled Then
                    Return LocalizationService.T("Die Personenerkennung ist ausgeschaltet")
                End If
                Return ""
            End Get
        End Property

        Public ReadOnly Property HasDisabledHint As Boolean
            Get
                Return DisabledHint.Length > 0
            End Get
        End Property

        ''' <summary>Meldet Knopf UND Begruendung nach. Die beiden haengen an denselben Bedingungen,
        ''' und eine grau gewordene Schaltflaeche ohne den Satz daneben erklaert nichts.</summary>
        Public Shadows Sub RefreshCanStart()
            MyBase.RefreshCanStart()
            Me.RaisePropertyChanged(NameOf(DisabledHint))
            Me.RaisePropertyChanged(NameOf(HasDisabledHint))
        End Sub

        Protected Overrides Sub RequestCancel()
            Try
                _cancellation?.Cancel()
            Catch ex As ObjectDisposedException
                ' Der Lauf war in derselben Sekunde von selbst fertig.
            End Try
            ' Zusaetzlich der eigene Weg des Dienstes: der haelt auch einen Lauf an, der von
            ' woanders gestartet wurde, und kostet nichts, wenn keiner laeuft.
            FaceScanRunner.RequestCancel()
        End Sub

        ''' <summary>Ueber ALLE eingetragenen Ordner. Nur noch der Weg fuer einen Aufruf ohne
        ''' Angabe; bedient wird sie ueber die Ordnerliste, je Zeile oder ueber die gefilterte
        ''' Menge.</summary>
        Public Overrides Function StartAsync() As Task
            Return StartForFoldersAsync(Nothing)
        End Function

        ''' <summary>Die Gesichtssuche ueber GENAU DIESE Ordner, samt Unterordnern.
        '''
        ''' Der Weg fuer die Ordnerliste: ein Bestand von Zehntausenden Bildern laeuft Stunden, und
        ''' wer nur wissen will, wer auf den Bildern EINER Reise ist, soll nicht den ganzen Bestand
        ''' anwerfen muessen. Nothing heisst: alle eingetragenen.</summary>
        Public Async Function StartForFoldersAsync(folders As IReadOnlyList(Of String)) As Task
            If FaceScanRunner.IsRunning OrElse Not FaceDetectionService.Enabled Then Return

            ' Der Riegel VOR jeder Anzeige - siehe BackgroundRunViewModel.TryEnterRun.
            If Not TryEnterRun() Then Return

            Await SetRunningAsync(True).ConfigureAwait(False)
            Await SetStatusAsync(LocalizationService.T("Bilder werden gesucht..."), 0, False).ConfigureAwait(False)

            Dim cts As New CancellationTokenSource()
            _cancellation = cts
            Dim result As FaceScanResult = Nothing
            Try
                ' Das Aufzaehlen selbst geht in den Hintergrund: ueber einen grossen Bestand laeuft
                ' es Sekunden, und solange stuende sonst die Oberflaeche.
                Dim paths = Await Task.Run(Function() CollectFaceTargets(folders, cts.Token), cts.Token).ConfigureAwait(False)
                If paths.Count > 0 Then
                    Dim progress = New Progress(Of (Done As Integer, Total As Integer, File As String))(
                        Sub(p) ReportThrottled(
                            String.Format(LocalizationService.T("Gesichter werden gesucht: {0} von {1}"),
                                          p.Done, p.Total),
                            p.Done, p.Total))
                    result = Await FaceScanRunner.RunAsync(paths, progress, cts.Token,
                                                           force:=False).ConfigureAwait(False)
                End If
            Catch ex As OperationCanceledException
                ' Abgebrochen, bevor der Lauf begann. Was bis dahin steht, bleibt stehen.
            Catch ex As Exception
                DiagnosticLogService.LogException("Gesichter.UeberwachteOrdner", ex)
            End Try

            _cancellation = Nothing
            cts.Dispose()

            ' Die Anzeige zurueckstellen, und ERST DANACH den Riegel freigeben (siehe LeaveRun):
            ' zwischen beidem koennte sonst ein neuer Lauf durchkommen und seine Anzeige setzen, die
            ' dieser hier gleich darauf ueberschreibt.
            '
            ' Abgewiesen kann hier nur noch ein FREMDER Lauf haben - der eigene zweite kam wegen des
            ' Riegels gar nicht bis hierher. Der fremde ist der der GALERIE (siehe
            ' GalleryViewModel.ScanFacesAsync), und der hat seinen eigenen Anzeigezustand; wer hier
            ' auf True stehenbliebe, haette einen Stopp-Knopf, der nie wieder verschwindet.
            Try
                Await SetRunningAsync(False).ConfigureAwait(False)
                If result IsNot Nothing AndAlso result.BlockedByOtherWindow Then
                    Await SetStatusAsync(LocalizationService.T("Ein anderes Fenster arbeitet gerade am Katalog"),
                                         0, False).ConfigureAwait(False)
                ElseIf result IsNot Nothing AndAlso result.NotStarted Then
                    Await SetStatusAsync(LocalizationService.T("Es läuft bereits eine Suche"), 0, False).ConfigureAwait(False)
                Else
                    Await SetStatusAsync(SummarizeResult(result), 100, False).ConfigureAwait(False)
                End If
            Catch ex As Exception
                ' Der Dispatcher kann beim Herunterfahren werfen. Ohne dieses Netz bliebe der
                ' Riegel unten liegen und die Suche waere fuer den Rest der Sitzung gesperrt.
                DiagnosticLogService.LogException("Gesichter.Abschluss", ex)
            End Try
            LeaveRun()
        End Function

        ''' <summary>Die Bilder der ueberwachten Ordner, ohne Videos.</summary>
        ''' <param name="folders">Nothing heisst: die aus den Einstellungen. Ausdruecklich uebergeben
        ''' wird nur beim Messen.</param>
        Public Shared Function CollectFaceTargets(Optional folders As IReadOnlyList(Of String) = Nothing,
                                                  Optional token As CancellationToken = Nothing) As List(Of String)
            Return CatalogIndexRunner.CollectImageFiles(folders, token).
                Where(Function(p) Not VideoPreviewService.IsSupportedVideo(p)).
                ToList()
        End Function

        ''' <summary>Was der Lauf gebracht hat. Ein ABGEBROCHENER Lauf hat trotzdem etwas gefunden,
        ''' und das bleibt gespeichert - die Zahl wegzulassen saehe aus, als waere alles umsonst
        ''' gewesen.</summary>
        Private Shared Function SummarizeResult(result As FaceScanResult) As String
            If result Is Nothing Then Return LocalizationService.T("Keine Bilder gefunden")
            If result.Cancelled Then
                Return String.Format(LocalizationService.T("Suche abgebrochen, {0} Gesichter gefunden"),
                                     result.FacesFound)
            End If
            If result.Scanned = 0 AndAlso result.Skipped > 0 Then
                Return LocalizationService.T("Alle Bilder waren schon durchsucht")
            End If
            Dim text = String.Format(LocalizationService.T("{0} Gesichter gefunden"), result.FacesFound)
            If result.Skipped > 0 Then
                text &= ", " & String.Format(LocalizationService.T("{0} schon durchsucht"), result.Skipped)
            End If
            Return text
        End Function

    End Class

End Namespace
