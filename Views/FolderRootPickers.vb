Imports System.Collections.Generic
Imports System.Collections.Specialized
Imports System.ComponentModel
Imports System.Linq
Imports System.Threading.Tasks
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Dialogs
Imports Avalonia.Dialogs.Internal
Imports Avalonia.Interactivity
Imports Avalonia.Layout
Imports Avalonia.Platform.Storage
Imports Avalonia.Threading
Imports FerrumPix.Services

Namespace Views

    ''' <summary>Alle Datei- und Ordnerwaehler der Anwendung gehen hier durch. Ohne feste
    ''' Fotoordner (<see cref="FolderRoots"/>) reichen sie nur durch; mit ihnen beginnt jeder
    ''' Waehler in einer Wurzel, und eine Antwort ausserhalb wird mit einer Meldung abgewiesen.
    '''
    ''' <para>ZWEI STUFEN, weil sich nicht jeder Waehler fuehren laesst. Der Waehler des Systems
    ''' (am Rechner) laesst sich nur mit einem Startordner versorgen; nach oben navigieren kann man
    ''' darin immer, also bleibt die Pruefung der Antwort. Im Browser kommt der Waehler vom Toolkit
    ''' (UseManagedSystemDialogs), und den haelt <see cref="InstallChooserGuard"/> in den Wurzeln:
    ''' Schnellzugriffe nur auf sie, und wer darueber hinaus navigiert (nach oben, ein eingetippter
    ''' Pfad), landet wieder in der Wurzel. Die Pruefung der Antwort bleibt auch dort, fuer einen
    ''' Dateinamen wie "../../x" beim Speichern.</para></summary>
    Friend NotInheritable Class FolderRootPickers
        Private Sub New()
        End Sub

        Public Shared Async Function OpenFolderPickerAsync(topLevel As TopLevel, options As FolderPickerOpenOptions) As Task(Of IReadOnlyList(Of IStorageFolder))
            If topLevel Is Nothing Then Return Array.Empty(Of IStorageFolder)()
            options.SuggestedStartLocation = Await StartInRootAsync(topLevel, options.SuggestedStartLocation)
            Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(options)
            If Not FolderRoots.IsActive OrElse folders Is Nothing Then Return folders
            Dim kept = folders.Where(Function(f) FolderRoots.Allows(f?.TryGetLocalPath())).ToList()
            If kept.Count < folders.Count Then Await ShowOutsideNoticeAsync(topLevel)
            Return kept
        End Function

        Public Shared Async Function OpenFilePickerAsync(topLevel As TopLevel, options As FilePickerOpenOptions) As Task(Of IReadOnlyList(Of IStorageFile))
            If topLevel Is Nothing Then Return Array.Empty(Of IStorageFile)()
            options.SuggestedStartLocation = Await StartInRootAsync(topLevel, options.SuggestedStartLocation)
            Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(options)
            If Not FolderRoots.IsActive OrElse files Is Nothing Then Return files
            Dim kept = files.Where(Function(f) FolderRoots.Allows(f?.TryGetLocalPath())).ToList()
            If kept.Count < files.Count Then Await ShowOutsideNoticeAsync(topLevel)
            Return kept
        End Function

        Public Shared Async Function SaveFilePickerAsync(topLevel As TopLevel, options As FilePickerSaveOptions) As Task(Of IStorageFile)
            If topLevel Is Nothing Then Return Nothing
            options.SuggestedStartLocation = Await StartInRootAsync(topLevel, options.SuggestedStartLocation)
            Dim file = Await topLevel.StorageProvider.SaveFilePickerAsync(options)
            If Not FolderRoots.IsActive OrElse file Is Nothing Then Return file
            If FolderRoots.Allows(file.TryGetLocalPath()) Then Return file
            Await ShowOutsideNoticeAsync(topLevel)
            Return Nothing
        End Function

        ''' <summary>Der Startordner: der vorgeschlagene, wenn er in einer Wurzel liegt, sonst die
        ''' erste Wurzel. Ohne feste Fotoordner unveraendert, auch Nothing (dann entscheidet das
        ''' System).</summary>
        Friend Shared Async Function StartInRootAsync(topLevel As TopLevel, suggested As IStorageFolder) As Task(Of IStorageFolder)
            If Not FolderRoots.IsActive Then Return suggested
            Dim suggestedPath = suggested?.TryGetLocalPath()
            If Not String.IsNullOrEmpty(suggestedPath) AndAlso FolderRoots.Allows(suggestedPath) Then Return suggested
            Try
                Return Await topLevel.StorageProvider.TryGetFolderFromPathAsync(FolderRoots.FirstRoot())
            Catch
                Return suggested
            End Try
        End Function

        ''' <summary>Die Meldung, wenn ein Waehler etwas ausserhalb geliefert hat. Ein eigenes
        ''' kleines Fenster und nicht der Dialog der Anwendung: viele Waehler oeffnen sich AUS einem
        ''' Dialog (Speichern unter, Exportieren), und dessen Meldung ersetzte ihn.</summary>
        Private Shared Async Function ShowOutsideNoticeAsync(topLevel As TopLevel) As Task
            Try
                Dim message As New TextBlock With {
                    .Text = LocalizationService.T("Dieser Ort liegt außerhalb der freigegebenen Fotoordner."),
                    .TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    .MaxWidth = 420}
                Dim confirm As New Button With {
                    .Content = LocalizationService.T("OK"),
                    .HorizontalAlignment = HorizontalAlignment.Right,
                    .Padding = New Thickness(18, 6)}
                Dim panel As New StackPanel With {.Spacing = 16, .Margin = New Thickness(24)}
                panel.Children.Add(message)
                panel.Children.Add(confirm)
                Dim window As New Window With {
                    .Title = LocalizationService.T("Ort nicht verwendbar"),
                    .SizeToContent = SizeToContent.WidthAndHeight,
                    .CanResize = False,
                    .WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    .Content = panel}
                window.Bind(Window.BackgroundProperty, window.GetResourceObservable("FP.Bg.Elevated"))
                message.Bind(TextBlock.ForegroundProperty, message.GetResourceObservable("FP.Text.Primary"))
                AddHandler confirm.Click, Sub(s, e) window.Close()
                Dim owner = TryCast(topLevel, Window)
                If owner IsNot Nothing Then
                    Await window.ShowDialog(owner)
                Else
                    window.Show()
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("FolderRootPickers.Notice", ex)
            End Try
        End Function

        ''' <summary>Haelt den Waehler des Toolkits (Browser) in den festen Fotoordnern. Gerufen
        ''' einmal beim Aufbau; ohne feste Fotoordner tut der Waechter nichts.
        '''
        ''' <para>Das Modell des Waehlers ist oeffentlich (ManagedFileChooserViewModel), seine
        ''' Quellen der Schnellzugriffe nicht (ManagedFileChooserSources ist intern, und die
        ''' Anmeldung beim Toolkit liefe ueber dessen PrivateApi). Deshalb setzt der Waechter am
        ''' fertigen Modell an, sobald der Waehler geladen ist: er ersetzt die Schnellzugriffe
        ''' durch die Wurzeln und fuehrt jeden Ort ausserhalb zurueck in die erste Wurzel.</para></summary>
        Public Shared Sub InstallChooserGuard()
            Control.LoadedEvent.AddClassHandler(Of ManagedFileChooser)(Sub(chooser, e) GuardChooser(chooser))
        End Sub

        Friend Shared Sub GuardChooser(chooser As ManagedFileChooser)
            Dim model = TryCast(chooser?.DataContext, ManagedFileChooserViewModel)
            If model Is Nothing Then Return
            HideSystemFolders(model)
            If Not FolderRoots.IsActive Then Return
            GuardModel(model)
        End Sub

        ''' <summary>Nimmt die Systemordner von NAS und Betriebssystem ("@eaDir", "#snapshot", siehe
        ''' FileOperationPolicy.IsSystemFolderName) aus der Liste des Waehlers, auch ohne feste
        ''' Fotoordner. Der Waehler liest den Ordner selbst (Navigate) und kennt nur versteckte;
        ''' deshalb wird nach jedem Fuellen nachgefegt. Ein eingetippter Pfad in so einen Ordner
        ''' fuehrt weiter hinein, nur aufgefuehrt wird er nicht.
        '''
        ''' <para>Sofort im Ereignis und nicht nachgereicht: die Liste wird in EINEM Schritt gefuellt
        ''' (AddRange), und die Anzeige hat ihn vor diesem Handler schon gesehen; das Entfernen
        ''' folgt ihr als eigenes Ereignis.</para></summary>
        Friend Shared Sub HideSystemFolders(model As ManagedFileChooserViewModel)
            If model Is Nothing Then Return
            Dim sweep =
                Sub()
                    For index = model.Items.Count - 1 To 0 Step -1
                        Dim item = model.Items(index)
                        If item IsNot Nothing AndAlso item.ItemType = ManagedFileChooserItemType.Folder AndAlso
                           FileOperationPolicy.IsSystemFolderName(item.DisplayName) Then
                            model.Items.RemoveAt(index)
                        End If
                    Next
                End Sub
            sweep()
            AddHandler model.Items.CollectionChanged,
                Sub(s As Object, e As NotifyCollectionChangedEventArgs)
                    If e.Action = NotifyCollectionChangedAction.Add OrElse e.Action = NotifyCollectionChangedAction.Reset Then sweep()
                End Sub
        End Sub

        ''' <summary>Der Waechter am Modell selbst, ohne Fenster; so misst ihn auch der Pruefstand.</summary>
        Friend Shared Sub GuardModel(model As ManagedFileChooserViewModel)
            If model Is Nothing OrElse Not FolderRoots.IsActive Then Return
            Dim replacing = False
            Dim confineLinks =
                Sub()
                    If replacing Then Return
                    Dim wanted = FolderRoots.Roots.Where(Function(r) IO.Directory.Exists(r)).ToList()
                    If model.QuickLinks.Count = wanted.Count AndAlso
                       model.QuickLinks.Select(Function(q) q.Path).SequenceEqual(wanted) Then Return
                    replacing = True
                    Try
                        model.QuickLinks.Clear()
                        For Each root In wanted
                            Dim name = IO.Path.GetFileName(root)
                            model.QuickLinks.Add(New ManagedFileChooserItemViewModel With {
                                .DisplayName = If(String.IsNullOrEmpty(name), root, name),
                                .Path = root,
                                .ItemType = ManagedFileChooserItemType.Folder})
                        Next
                    Finally
                        replacing = False
                    End Try
                End Sub
            confineLinks()
            ' Die Laufwerksliste des Toolkits fuellt die Schnellzugriffe spaeter neu; danach gleich
            ' wieder auf die Wurzeln.
            AddHandler model.QuickLinks.CollectionChanged,
                Sub(s As Object, e As NotifyCollectionChangedEventArgs)
                    If Not replacing Then Dispatcher.UIThread.Post(confineLinks)
                End Sub
            ' NACH dem Navigieren zurueck, nicht mitten darin: Navigate setzt erst den Ort und
            ' fuellt danach die Liste; ein Sprung im Ereignis wuerde von der Liste des alten Ziels
            ' ueberschrieben.
            AddHandler model.PropertyChanged,
                Sub(s As Object, e As PropertyChangedEventArgs)
                    If e.PropertyName <> NameOf(ManagedFileChooserViewModel.Location) Then Return
                    If FolderRoots.Allows(model.Location) Then Return
                    Dispatcher.UIThread.Post(Sub() ReturnToRoot(model))
                End Sub
            ReturnToRoot(model)
        End Sub

        Private Shared Sub ReturnToRoot(model As ManagedFileChooserViewModel)
            If FolderRoots.Allows(model.Location) Then Return
            Dim root = FolderRoots.FirstRoot()
            If IO.Directory.Exists(root) Then model.Navigate(root)
        End Sub
    End Class

End Namespace
