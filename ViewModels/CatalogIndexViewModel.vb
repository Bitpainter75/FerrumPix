Imports System
Imports System.Threading.Tasks
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>
    ''' Der Katalogindex fuer die Oberflaeche. Anzeige, Drossel und Dispatcher-Wechsel stehen in
    ''' <see cref="BackgroundRunViewModel"/>; hier steht nur, was diesen Lauf ausmacht.
    '''
    ''' EIN Objekt fuer beide Anzeigeorte. Der Fortschritt steht in den Einstellungen und in der
    ''' Fusszeile der Galerie; mit einem Zustand je Ansicht haetten die beiden auseinanderlaufen
    ''' koennen, sobald einer der Wege den Lauf anstoesst und der andere nichts davon mitbekommt.
    ''' Galerie und Einstellungen zeigen deshalb auf dasselbe Objekt (siehe MainWindowViewModel).
    ''' </summary>
    Public Class CatalogIndexViewModel
        Inherits BackgroundRunViewModel

        ''' <summary>Wie lange nach dem Start gewartet wird, bevor der Lauf beginnt.
        '''
        ''' Nicht sofort: der Start baut Fenster, Ordnerbaum und die ersten Kacheln auf, und genau
        ''' das braucht Platte und Kerne. Ein Indexlauf mittendrin macht den Start sichtbar zaeh,
        ''' obwohl auf ihn selbst niemand wartet. Fuenf Sekunden reichen, bis die Galerie steht.</summary>
        Public Const StartupDelayMilliseconds As Integer = 5000

        ''' <summary>Startbar, solange keiner laeuft UND ueberhaupt ein Ordner eingetragen ist.</summary>
        Public Overrides ReadOnly Property CanStart As Boolean
            Get
                Return Not IsRunning AndAlso CatalogIndexRunner.ConfiguredFolders().Count > 0
            End Get
        End Property

        Protected Overrides Sub RequestCancel()
            CatalogIndexRunner.RequestCancel()
        End Sub

        ''' <summary>Der Lauf kurz nach dem Programmstart, wenn die Einstellung es verlangt.
        '''
        ''' Er wird NICHT erwartet: der Start soll weiterlaufen. Was schiefgeht, steht im Protokoll
        ''' und nicht in einem Dialog - niemand hat diesen Lauf gerade angestossen, also darf er
        ''' auch niemanden mit einer Meldung aufhalten.</summary>
        Public Sub StartAfterStartupIfConfigured()
            If Not AppSettingsService.Load().CatalogIndexOnStartup Then Return
            If CatalogIndexRunner.ConfiguredFolders().Count = 0 Then Return

            Dim ignored = Task.Run(Async Function()
                                       Try
                                           Await Task.Delay(StartupDelayMilliseconds).ConfigureAwait(False)
                                           Await StartAsync().ConfigureAwait(False)
                                       Catch ex As Exception
                                           DiagnosticLogService.LogException("Katalogindex.Start", ex)
                                       End Try
                                   End Function)
        End Sub

        ''' <summary>Startet einen Lauf und haelt die Anzeige nach. Laeuft schon einer, geschieht
        ''' nichts - der Dienst laesst ohnehin nur einen zu, und hier faengt es der Knopf ab.</summary>
        Public Overrides Async Function StartAsync() As Task
            Await StartForFoldersAsync(Nothing).ConfigureAwait(False)
        End Function

        ''' <summary>Startet denselben sicheren Kataloglauf für ausdrücklich gewählte Wurzeln.
        ''' Der Ordner-Aktionsweg nutzt dies für KI-Stichwörter: auch unveränderte Dateien werden
        ''' dabei auf fehlende bzw. veraltete KI-Ergebnisse geprüft, normale Metadaten bleiben
        ''' unangetastet, solange sie frisch sind.</summary>
        Public Async Function StartForFoldersAsync(folders As IReadOnlyList(Of String)) As Task
            If CatalogIndexRunner.IsRunning Then Return

            ' Der Riegel VOR jeder Anzeige - siehe BackgroundRunViewModel.TryEnterRun. Hier greift
            ' er, wenn der Lauf nach dem Programmstart und ein Klick auf "Starten" sich ueberholen.
            If Not TryEnterRun() Then Return

            Await SetRunningAsync(True).ConfigureAwait(False)
            Await SetStatusAsync(LocalizationService.T("Bilder werden gesucht..."), 0, False).ConfigureAwait(False)

            ' Der Merker oben ist nur die BILLIGE Vorabsage. Zwischen ihm und dem Start kann ein
            ' anderer Lauf beginnen - etwa wenn der Start nach dem Programmstart und ein Klick auf
            ' "Starten" sich ueberholen. Wer dann abgewiesen wird, darf die Anzeige NICHT
            ' zuruecksetzen: sie gehoert dem Lauf, der wirklich arbeitet.
            Dim result As CatalogIndexResult = Nothing
            Try
                ' Der Fortschritt kommt vom Hintergrund-Thread und geht in gebundene Eigenschaften -
                ' also ueber den Dispatcher. Progress(Of T) faengt den Kontext des ERZEUGERS ein, und
                ' der ist hier bereits der Hintergrund: deshalb wechselt ReportThrottled ausdruecklich.
                Dim progress = New Progress(Of CatalogIndexProgress)(
                    Sub(p) ReportThrottled(
                        String.Format(LocalizationService.T("Indiziere {0} von {1}"), p.Done, p.Total),
                        p.Done, p.Total))
                result = Await CatalogIndexRunner.RunAsync(folders:=folders, progress:=progress).ConfigureAwait(False)
            Catch ex As Exception
                DiagnosticLogService.LogException("Katalogindex.Lauf", ex)
            End Try

            ' Die Anzeige zurueckstellen, und ERST DANACH den Riegel freigeben (siehe LeaveRun).
            '
            ' Abgewiesen? Dann laeuft ein anderer noch, und ihm gehoert die Anzeige - hier still
            ' zurueckziehen, ohne ihm Stopp-Knopf und Fortschritt wegzunehmen. Seit dem Riegel kann
            ' das nur noch ein Lauf sein, der gar nicht ueber dieses ViewModel gestartet wurde.
            '
            ' NICHT in einem Finally: VB laesst dort kein Await zu. Der Block faengt deshalb selbst,
            ' damit LeaveRun auf jedem Weg erreicht wird - sonst bliebe der Riegel liegen und der
            ' Index waere fuer den Rest der Sitzung gesperrt.
            Try
                If result IsNot Nothing AndAlso result.BlockedByOtherWindow Then
                    ' Ein anderes Fenster schreibt gerade. Hier warten hilft nicht - also
                    ' zuruecksetzen und sagen, woran es liegt.
                    Await SetRunningAsync(False).ConfigureAwait(False)
                    Await SetStatusAsync(LocalizationService.T("Ein anderes Fenster arbeitet gerade am Katalog"),
                                         0, False).ConfigureAwait(False)
                ElseIf result Is Nothing OrElse Not result.NotStarted Then
                    Await SetRunningAsync(False).ConfigureAwait(False)
                    Await SetStatusAsync(SummarizeResult(result), 100, False).ConfigureAwait(False)
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Katalogindex.Abschluss", ex)
            End Try
            LeaveRun()
        End Function

        ''' <summary>Fasst zusammen, was der Lauf gebracht hat. Bewusst in Saetzen, die ein Mensch
        ''' liest - Zahlen ohne Wort sagen niemandem, was sie zaehlen.</summary>
        Private Shared Function SummarizeResult(result As CatalogIndexResult) As String
            If result Is Nothing Then Return LocalizationService.T("Die Indizierung ist fehlgeschlagen")
            If result.Cancelled Then
                Return String.Format(LocalizationService.T("Abgebrochen nach {0} Bildern"), result.Indexed)
            End If
            If result.Indexed = 0 AndAlso result.Unchanged = 0 Then
                Return LocalizationService.T("Keine Bilder gefunden")
            End If
            Dim text = String.Format(LocalizationService.T("{0} Bilder indiziert, {1} unverändert"),
                                     result.Indexed, result.Unchanged)
            If result.ThumbnailsCreated > 0 Then
                text &= ", " & String.Format(LocalizationService.T("{0} Vorschaubilder"), result.ThumbnailsCreated)
            End If
            If result.PlacesResolved > 0 Then
                text &= ", " & String.Format(LocalizationService.T("{0} Aufnahmeorte"), result.PlacesResolved)
            End If
            If result.AiTagged > 0 Then
                text &= ", " & String.Format(LocalizationService.T("{0} KI-analysiert"), result.AiTagged)
            End If
            ' WAS INS LEERE ZEIGT, wird hier gesagt und nicht still weggeraeumt: Bewertungen,
            ' Stichwoerter und Personen sind Handarbeit. Wegraeumen kann man sie in den Einstellungen
            ' mit "Datenbank bereinigen" - dann aber auf Ansage.
            If result.Orphaned > 0 Then
                text &= ", " & String.Format(LocalizationService.T("{0} Einträge ohne Datei"), result.Orphaned)
            End If
            Return text
        End Function

    End Class

End Namespace
