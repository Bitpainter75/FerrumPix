Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Input.Platform
Imports Avalonia.Markup.Xaml
Imports Avalonia.Interactivity
Imports Avalonia.LogicalTree
Imports Avalonia.Threading
Imports Avalonia.Media.Imaging
Imports Avalonia.VisualTree
Imports FerrumPix.Controls
Imports FerrumPix.Models
Imports FerrumPix.Services
Imports FerrumPix.ViewModels
Imports System.Collections
Imports System.Collections.Generic
Imports System.Collections.Specialized
Imports System.ComponentModel
Imports System.Linq
Imports System.Threading.Tasks

Namespace Views

    Public Class GalleryView
        Inherits UserControl

        Private Shared ReadOnly FerrumPixPathsFormat As DataFormat(Of String) =
            DataFormat.CreateStringApplicationFormat("FerrumPixPaths")

        Private _initialSelectionDone As Boolean = False
        Private _dragStartPoint As Avalonia.Point
        Private _dragStartItem As ImageItem
        ' DoDragDropAsync verlangt genau das Press-Ereignis, das die Geste ausgelöst hat - das
        ' PointerMoved-Argument taugt dafür nicht.
        Private _dragStartArgs As PointerPressedEventArgs
        Private _selectionAnchor As ImageItem
        Private _observedVm As GalleryViewModel
        Private _spaceOverviewActive As Boolean = False
        Private _isDragging As Boolean = False
        Private _contextMenuItem As ImageItem
        Private _folderTreeContextNode As FolderNode
        Private _suppressFolderTreeSelectionChange As Boolean = False
        Private _restoringFolderTreeSelection As Boolean = False
        Private _clearingNavigationSelection As Boolean = False
        Private _viewportThumbnailRefreshQueued As Boolean = False
        Private _scrollHandlersAttached As Boolean = False
        Private _isAttached As Boolean = False
        Private _suppressNextGalleryContextMenu As Boolean = False
        Private ReadOnly _thumbnailTracker As New ViewportThumbnailTracker()

        ' Die gemessene Zeilenhoehe wird festgehalten, statt sie bei jedem Scroll-Tick neu zu
        ' uebernehmen. Sie ist eine Eigenschaft der Kachelvorlage und aendert sich nur mit der
        ' Kachelgroesse oder der Schrift, nicht mit der Scrollposition.
        Private _latchedSlotHeight As Double = 0
        Private _latchedSlotThumbnailSize As Double = -1

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            AddHandler DataContextChanged, AddressOf OnViewDataContextChanged
            ContextMenuAttachment.Attach(Me.FindControl(Of Grid)("GalleryRootGrid"), AddressOf OnContextRequested)
            AddHandler AttachedToVisualTree, AddressOf OnGalleryAttachedToVisualTree
            AddHandler DetachedFromVisualTree, AddressOf OnGalleryDetachedFromVisualTree
            Me.AddHandler(InputElement.GotFocusEvent, AddressOf OnDescendantGotFocus, RoutingStrategies.Bubble)
            Dim tree = Me.FindControl(Of TreeView)("FolderTreeView")
            If tree IsNot Nothing Then
                tree.AddHandler(InputElement.PointerPressedEvent, AddressOf OnFolderTreePointerPressedTunnel, RoutingStrategies.Tunnel)
            End If
            ' Das Control gehört dieser View-Instanz - kein Abmelden nötig, sie sterben gemeinsam.
            Dim scrubber = Me.FindControl(Of GalleryTimelineScrubber)("GalleryTimelineScrubber")
            If scrubber IsNot Nothing Then AddHandler scrubber.ScrubRequested, AddressOf OnTimelineScrubRequested
        End Sub

        ''' <summary>Die Stichwortliste vor dem Aufklappen neu einlesen. Sie kommt aus dem Katalog
        ''' und aendert sich, sobald irgendwo ein Stichwort vergeben oder entfernt wird - eine
        ''' einmal gebaute Liste waere schon beim naechsten Bild veraltet.</summary>
        Private Sub OnTagFilterButtonClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.RefreshTagFilterOptions()
        End Sub

        ''' <summary>Die Personenliste entsteht beim Oeffnen neu - nach einem Durchlauf sind neue
        ''' Gruppen dabei, nach einer Benennung neue Namen.</summary>
        Private Sub OnPersonFilterButtonClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.RefreshPersonFilterOptions()
        End Sub

        ''' <summary>Eine Person dazunehmen oder abwaehlen. Ueber den Datenkontext der Schaltflaeche,
        ''' nicht ueber eine Bindung: der Inhalt eines Aufklappfensters haengt nicht im Baum der
        ''' Ansicht (gleicher Grund wie bei den Stichwoertern).</summary>
        Private Sub OnPersonFilterItemClick(sender As Object, e As RoutedEventArgs)
            Dim entry = TryCast(TryCast(sender, Button)?.DataContext, PersonFilterOption)
            If entry Is Nothing Then Return
            GetVm()?.TogglePersonFilter(entry.Id)
        End Sub

        ''' <summary>Die Ortsliste entsteht beim Oeffnen neu - nach einem Einlesen sind neue Orte
        ''' dabei, und das Nachziehen aelterer Eintraege laeuft im Hintergrund.</summary>
        Private Sub OnPlaceFilterButtonClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.RefreshPlaceFilterOptions()
        End Sub

        ''' <summary>Einen Ort dazunehmen oder abwaehlen. Ueber den Datenkontext der Schaltflaeche,
        ''' gleicher Grund wie bei Personen und Stichwoertern. Es geht der GANZE Eintrag hinueber,
        ''' nicht nur der Name: dieselbe Stadt kann lokal und auf dem Server stehen, und nur der
        ''' Eintrag weiss, welcher von beiden gemeint war.</summary>
        Private Sub OnPlaceFilterItemClick(sender As Object, e As RoutedEventArgs)
            Dim entry = TryCast(TryCast(sender, Button)?.DataContext, PlaceFilterOption)
            If entry Is Nothing Then Return
            GetVm()?.TogglePlaceFilter(entry)
        End Sub

        ''' <summary>Mausradklick auf einen Filter- oder Sortierknopf setzt ihn auf den Standard
        ''' zurueck, ohne das Menue zu oeffnen.
        '''
        ''' Am Druck und nicht am Klick: ein Klick entsteht nur mit der LINKEN Taste, und genau die
        ''' oeffnet das Aufklappfenster. Aus demselben Grund kommt sich hier nichts in die Quere -
        ''' die mittlere Taste laesst der Knopf von sich aus liegen.
        '''
        ''' Welcher Knopf gemeint ist, sagt sein Tag. Ein eigener Handler je Knopf waere viermal
        ''' derselbe Rumpf mit einer anderen Zeile in der Mitte.</summary>
        Private Sub OnFilterButtonPointerPressed(sender As Object, e As PointerPressedEventArgs)
            Dim button = TryCast(sender, Button)
            If button Is Nothing Then Return
            If Not e.GetCurrentPoint(button).Properties.IsMiddleButtonPressed Then Return
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            Select Case TryCast(button.Tag, String)
                Case "Person" : vm.ClearPersonFilter()
                Case "Place" : vm.ClearPlaceFilter()
                Case "Tag" : vm.ClearTagFilter()
                Case "Filter" : vm.ClearFilters()
                Case "Sort" : vm.ResetSort()
            End Select
            e.Handled = True
        End Sub

        ''' <summary>Ein Stichwort dazunehmen oder abwaehlen. Ueber den Datenkontext der
        ''' Schaltflaeche, nicht ueber eine Bindung: der Inhalt eines Aufklappfensters haengt nicht
        ''' im Baum der Ansicht, und eine Bindung ueber den Vorfahren findet dort nichts.</summary>
        Private Sub OnTagFilterItemClick(sender As Object, e As RoutedEventArgs)
            Dim entry = TryCast(TryCast(sender, Button)?.DataContext, TagFilterOption)
            If entry Is Nothing Then Return
            ' Den EINTRAG durchreichen, nicht nur seinen Namen: nur er weiss, ob das Stichwort vom
            ' Server kommt und welche Kennung es dort hat.
            GetVm()?.ToggleTagFilter(entry)
        End Sub

        Private Sub OnLocalizedFlyoutOpened(sender As Object, e As EventArgs)
            Dim flyout = TryCast(sender, Flyout)
            Dim content = TryCast(flyout?.Content, ILogical)
            If content IsNot Nothing Then LocalizationService.ApplyTo(content)
        End Sub

        ''' <summary>Ein Kontextmenue entsteht erst aus dem Template, wenn es geoeffnet wird - beim
        ''' einmaligen Durchlauf ueber das Fenster gibt es es noch nicht. Ohne diesen Einstieg bleiben
        ''' seine Eintraege in der Ausgangssprache stehen, obwohl die uebrige Oberflaeche umgeschaltet
        ''' hat. Der LOGISCHE Baum genuegt hier: die Menueeintraege sind Kinder des Menues.</summary>
        Private Sub OnLocalizedMenuOpened(sender As Object, e As Avalonia.Interactivity.RoutedEventArgs)
            Dim menu = TryCast(sender, ILogical)
            If menu IsNot Nothing Then LocalizationService.ApplyTo(menu)
        End Sub

        ''' <summary>Die Galerie erzeugt Karten und Listenzeilen erst aus dem DataTemplate, nachdem
        ''' die einmalige Fenster-Lokalisierung bereits gelaufen sein kann. Jedes neu materialisierte
        ''' Element lokalisiert deshalb nur seinen eigenen Unterbaum - auch nach Scrollen oder einem
        ''' Wechsel des DisplayItems-Fensters.</summary>
        Private Sub OnGalleryItemAttachedToVisualTree(sender As Object, e As VisualTreeAttachmentEventArgs)
            Dim itemRoot = TryCast(sender, Visual)
            If itemRoot IsNot Nothing Then LocalizationService.ApplyToVisualTree(itemRoot)
        End Sub

        Private Sub OnDescendantGotFocus(sender As Object, e As FocusChangedEventArgs)
            Dim focused = TryCast(e.Source, Control)
            If focused Is Nothing OrElse Object.ReferenceEquals(focused, Me) Then Return
            If PlatformShortcutService.IsInputFieldSource(focused) Then Return
            If IsWithinNamedControl(focused, "FolderTreeView") Then Return
            If _spaceOverviewActive Then Return
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            If mainVm IsNot Nothing AndAlso mainVm.IsDialogOpen Then Return
            Me.Focus()
        End Sub

        Private Shared Function IsWithinNamedControl(control As Control, name As String) As Boolean
            Dim ctrl = control
            While ctrl IsNot Nothing
                If String.Equals(ctrl.Name, name, StringComparison.Ordinal) Then Return True
                ctrl = TryCast(ctrl.Parent, Control)
            End While
            Return False
        End Function

        Private Sub OnGalleryAttachedToVisualTree(sender As Object, e As VisualTreeAttachmentEventArgs)
            _isAttached = True
            RebindViewModel()
            Dispatcher.UIThread.Post(Sub() Me.Focus(), DispatcherPriority.Background)
            RestoreFolderTreeSelectionAfterRecreation()
            ' Den Stand der Info-Leiste aus den Einstellungen holen. Erst hier steht alles bereit:
            ' im Konstruktor des ViewModels gibt es die Einstellungen unter Umstaenden noch nicht.
            GetVm()?.RefreshInfoSidebarState()

            If _scrollHandlersAttached Then
                QueueViewportThumbnailRefresh()
                Return
            End If

            AddHandler Me.PropertyChanged, AddressOf OnGalleryScrollPropertyChanged

            Dim gridScroll = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
            If gridScroll IsNot Nothing Then
                AddHandler gridScroll.PropertyChanged, AddressOf OnGalleryScrollPropertyChanged
                AddHandler gridScroll.ScrollChanged, AddressOf OnGalleryScrollChanged
                gridScroll.AddHandler(InputElement.PointerWheelChangedEvent, AddressOf OnGalleryScrollWheelChanged, RoutingStrategies.Tunnel)
            End If

            Dim listScroll = Me.FindControl(Of ScrollViewer)("GalleryListScrollViewer")
            If listScroll IsNot Nothing Then
                AddHandler listScroll.PropertyChanged, AddressOf OnGalleryScrollPropertyChanged
                AddHandler listScroll.ScrollChanged, AddressOf OnGalleryScrollChanged
                listScroll.AddHandler(InputElement.PointerWheelChangedEvent, AddressOf OnGalleryScrollWheelChanged, RoutingStrategies.Tunnel)
            End If

            _scrollHandlersAttached = True
            QueueViewportThumbnailRefresh()
        End Sub

        ' Avalonias Standard-ScrollViewer bietet keine Geschwindigkeits-Einstellung - hier wird das
        ' Wheel-Event abgefangen und der Offset direkt mit einem festen, höheren Pixelbetrag pro
        ' Notch gesetzt (statt das eingebaute Scrollen zusätzlich laufen zu lassen, was zu doppelter
        ' Geschwindigkeit führen würde). ~90px/Notch liegt spürbar über dem Avalonia-Standardgefühl.
        Private Const GalleryWheelScrollStepPx As Double = 90

        Private Sub OnGalleryScrollWheelChanged(sender As Object, e As PointerWheelEventArgs)
            Dim scrollViewer = TryCast(sender, ScrollViewer)
            If scrollViewer Is Nothing Then Return
            Dim rightButtonZoom = e.GetCurrentPoint(scrollViewer).Properties.IsRightButtonPressed
            If rightButtonZoom OrElse e.KeyModifiers.HasFlag(KeyModifiers.Control) Then
                If rightButtonZoom Then _suppressNextGalleryContextMenu = True
                ZoomGalleryAtViewportPoint(scrollViewer, e.GetPosition(scrollViewer), If(e.Delta.Y > 0, 24.0, -24.0))
                e.Handled = True
                Return
            End If

            Dim maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height)
            Dim newOffsetY = Math.Max(0, Math.Min(scrollViewer.Offset.Y - e.Delta.Y * GalleryWheelScrollStepPx, maxOffsetY))
            scrollViewer.Offset = New Vector(scrollViewer.Offset.X, newOffsetY)
            e.Handled = True
        End Sub

        Private Sub ZoomGalleryAtViewportPoint(scrollViewer As ScrollViewer, viewportPoint As Avalonia.Point, deltaSize As Double)
            Dim vm = TryCast(DataContext, GalleryViewModel)
            If vm Is Nothing OrElse Math.Abs(deltaSize) < 0.01 Then Return

            Dim oldSize = Math.Max(1.0, vm.ThumbnailSize)
            Dim contentY = scrollViewer.Offset.Y + viewportPoint.Y
            vm.ThumbnailSize = oldSize + deltaSize
            Dim scale = vm.ThumbnailSize / oldSize

            Dim applyOffset =
                Sub()
                    Dim maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height)
                    Dim targetY = contentY * scale - viewportPoint.Y
                    Dim newOffsetY = Math.Max(0, Math.Min(targetY, maxOffsetY))
                    scrollViewer.Offset = New Vector(scrollViewer.Offset.X, newOffsetY)
                End Sub
            applyOffset()
            Dispatcher.UIThread.Post(applyOffset, DispatcherPriority.Background)
        End Sub

        Private Sub OnGalleryDetachedFromVisualTree(sender As Object, e As VisualTreeAttachmentEventArgs)
            ' Das GalleryViewModel lebt über die ganze Sitzung, diese View wird bei jedem Moduswechsel
            ' neu gebaut. Ohne Abmelden bliebe sie samt Item-Baum an den drei Abos hängen - DataContextChanged,
            ' wo sie sonst gelöst werden, feuert beim Verwerfen der View nicht.
            _isAttached = False
            UnsubscribeViewModel()

            If Not _scrollHandlersAttached Then Return

            RemoveHandler Me.PropertyChanged, AddressOf OnGalleryScrollPropertyChanged

            Dim gridScroll = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
            If gridScroll IsNot Nothing Then
                RemoveHandler gridScroll.PropertyChanged, AddressOf OnGalleryScrollPropertyChanged
                RemoveHandler gridScroll.ScrollChanged, AddressOf OnGalleryScrollChanged
                gridScroll.RemoveHandler(InputElement.PointerWheelChangedEvent, AddressOf OnGalleryScrollWheelChanged)
            End If

            Dim listScroll = Me.FindControl(Of ScrollViewer)("GalleryListScrollViewer")
            If listScroll IsNot Nothing Then
                RemoveHandler listScroll.PropertyChanged, AddressOf OnGalleryScrollPropertyChanged
                RemoveHandler listScroll.ScrollChanged, AddressOf OnGalleryScrollChanged
                listScroll.RemoveHandler(InputElement.PointerWheelChangedEvent, AddressOf OnGalleryScrollWheelChanged)
            End If

            _scrollHandlersAttached = False
        End Sub

        Private Sub RebindViewModel()
            UnsubscribeViewModel()
            If Not _isAttached Then Return
            _observedVm = GetVm()
            If _observedVm Is Nothing Then Return
            AddHandler _observedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            AddHandler _observedVm.RequestScrollToItem, AddressOf OnRequestScrollToItem
            If _observedVm.Items IsNot Nothing Then AddHandler _observedVm.Items.CollectionChanged, AddressOf OnGalleryItemsCollectionChanged
            If _observedVm.DisplayItems IsNot Nothing Then AddHandler _observedVm.DisplayItems.CollectionChanged, AddressOf OnDisplayItemsCollectionChanged
            ' Die neue View-Instanz startet ohne Zeitleisten-Daten - vom (langlebigen) VM-Stand aufbauen.
            RebuildTimelineSegments()
        End Sub

        Private Sub UnsubscribeViewModel()
            If _observedVm Is Nothing Then Return
            RemoveHandler _observedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            RemoveHandler _observedVm.RequestScrollToItem, AddressOf OnRequestScrollToItem
            If _observedVm.Items IsNot Nothing Then RemoveHandler _observedVm.Items.CollectionChanged, AddressOf OnGalleryItemsCollectionChanged
            If _observedVm.DisplayItems IsNot Nothing Then RemoveHandler _observedVm.DisplayItems.CollectionChanged, AddressOf OnDisplayItemsCollectionChanged
            _observedVm = Nothing
        End Sub

        ''' Der Ordner wird asynchron geladen: DisplayItems füllt sich, nachdem der ItemsControl bereits
        ''' (leer) vermessen und angeordnet wurde. Sein Reset-Ereignis erneuert zwar seine eigene
        ''' Wunschgröße, der umgebende StackPanel wird davon aber nicht neu vermessen - der ItemsControl
        ''' bleibt mit Höhe 0 angeordnet, die Galerie sieht leer aus, bis ein Ordnerwechsel sie neu baut.
        ''' Bei vielen Bildern fiel das nie auf, weil dort die Platzhalter-Höhen des Sichtfensters von 0
        ''' abweichen und damit ohnehin eine neue Messung auslösen.
        Private Sub OnDisplayItemsCollectionChanged(sender As Object, e As NotifyCollectionChangedEventArgs)
            If e.Action <> NotifyCollectionChangedAction.Reset Then Return
            Dispatcher.UIThread.Post(AddressOf InvalidateGalleryItemsLayout, DispatcherPriority.Loaded)
        End Sub

        Private Sub InvalidateGalleryItemsLayout()
            For Each scrollViewerName In {"GalleryGridScrollViewer", "GalleryListScrollViewer"}
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)(scrollViewerName)
                Dim itemsControl = scrollViewer?.GetVisualDescendants().OfType(Of ItemsControl)().FirstOrDefault()
                If itemsControl Is Nothing Then Continue For
                itemsControl.InvalidateMeasure()
                TryCast(itemsControl.GetVisualParent(), Control)?.InvalidateMeasure()
            Next
        End Sub

        Private Sub OnViewDataContextChanged(sender As Object, e As EventArgs)
            Dim vm = GetVm()
            RebindViewModel()
            QueueViewportThumbnailRefresh()

            If _initialSelectionDone Then Return
            If vm Is Nothing Then Return

            ' Kommen wir aus Viewer/Editor in eine aktive Suchliste zurück, hat der ContentControl die
            ' GalleryView neu instanziiert (_initialSelectionDone ist am neuen Objekt wieder False). Dann
            ' darf NICHT der letzte Ordner im Baum selektiert werden - das würde OnFolderTreeSelectionChanged
            ' auslösen und aus der Suchliste heraus in den Ordner navigieren. Stattdessen die Suchlisten-
            ' Auswahl im Suchbaum wiederherstellen (mit _clearingNavigationSelection, damit die Suche nicht
            ' neu gestartet wird).
            ' Gilt für JEDEN virtuellen Ordner (Suchliste ODER Immich): nach Neuinstanziierung nicht in
            ' den Startordner navigieren, sondern die Auswahl im passenden Baum wiederherstellen.
            ' Nicht an IsVirtualFolder haengen: ein ORDNER-Favorit oeffnet einen echten Ordner,
            ' waere damit durchgefallen und der Favoriten-Eintrag verlor beim Zurueckkommen seinen
            ' Rahmen. Massgeblich ist allein, ob das ViewModel ein
            ' wiederherzustellendes Navigationsziel kennt.
            If vm.NavigationRestoreNode IsNot Nothing Then
                _initialSelectionDone = True
                ' Baum UND Tab kommen aus dem ViewModel (das den Moduswechsel ueberlebt) - seit dem
                ' Tab-Umbau gibt es je Tab eine eigene Suchliste, "SearchTreeView" waere zu wenig.
                Dim treeName = vm.NavigationRestoreTreeName
                Dim targetNode = vm.NavigationRestoreNode
                Dispatcher.UIThread.Post(Sub()
                    Dim tree = If(String.IsNullOrEmpty(treeName), Nothing, Me.FindControl(Of TreeView)(treeName))
                    If tree IsNot Nothing AndAlso targetNode IsNot Nothing Then
                        _clearingNavigationSelection = True
                        Try
                            tree.SelectedItem = targetNode
                            BringTreeItemIntoView(tree, targetNode)
                        Finally
                            _clearingNavigationSelection = False
                        End Try
                    End If
                    If vm.SelectedItem IsNot Nothing Then ScrollToSelectedItem()
                End Sub, DispatcherPriority.Loaded)
                Return
            End If

            Dim folderNode = If(vm.SelectedFolderNode, vm.InitialFolderNode)
            If folderNode Is Nothing Then Return
            _initialSelectionDone = True
            Dispatcher.UIThread.Post(Sub()
                Dim tree = Me.FindControl(Of TreeView)("FolderTreeView")
                If tree IsNot Nothing Then
                    RestoreFolderTreeSelection(tree, folderNode)
                    BringTreeItemIntoView(tree, folderNode)
                End If
                If vm.SelectedItem IsNot Nothing Then ScrollToSelectedItem()
            End Sub, DispatcherPriority.Loaded)
        End Sub

        Private Sub OnViewModelPropertyChanged(sender As Object, e As PropertyChangedEventArgs)
            If e.PropertyName = NameOf(GalleryViewModel.CurrentFolder) Then
                Dispatcher.UIThread.Post(Sub()
                                             Dim vm = GetVm()
                                             If vm IsNot Nothing AndAlso Not vm.IsVirtualFolder Then SelectFolderInTree(vm.CurrentFolder)
                                             ResetGalleryScroll()
                                             ScrollToSelectedItem()
                                             QueueViewportThumbnailRefresh()
                                         End Sub, DispatcherPriority.Loaded)
                Return
            End If

            If e.PropertyName = NameOf(GalleryViewModel.ViewMode) OrElse
               e.PropertyName = NameOf(GalleryViewModel.ThumbnailSize) Then
                QueueViewportThumbnailRefresh()
                Return
            End If

            If e.PropertyName = NameOf(GalleryViewModel.SortMode) OrElse
               e.PropertyName = NameOf(GalleryViewModel.SortAscending) Then
                ' Sortierung bestimmt die Achse der Zeitleiste (Jahre/Buchstaben/keine).
                RebuildTimelineSegments()
                Return
            End If

            If e.PropertyName = NameOf(GalleryViewModel.SelectedFolderNode) Then
                Dispatcher.UIThread.Post(Sub()
                                             Dim vm = GetVm()
                                             Dim tree = Me.FindControl(Of TreeView)("FolderTreeView")
                                             If vm IsNot Nothing AndAlso tree IsNot Nothing AndAlso vm.SelectedFolderNode IsNot Nothing Then
                                                 RestoreFolderTreeSelection(tree, vm.SelectedFolderNode)
                                                 BringTreeItemIntoView(tree, vm.SelectedFolderNode)
                                             End If
                                         End Sub, DispatcherPriority.Loaded)
                Return
            End If

            If e.PropertyName = NameOf(GalleryViewModel.SelectedSearchNode) Then
                Dispatcher.UIThread.Post(Sub()
                                             Dim vm = GetVm()
                                             Dim tree = Me.FindControl(Of TreeView)("SearchTreeView")
                                             If vm IsNot Nothing AndAlso tree IsNot Nothing AndAlso vm.SelectedSearchNode IsNot Nothing Then
                                                 _clearingNavigationSelection = True
                                                 Try
                                                     tree.SelectedItem = vm.SelectedSearchNode
                                                     BringTreeItemIntoView(tree, vm.SelectedSearchNode)
                                                 Finally
                                                     _clearingNavigationSelection = False
                                                 End Try
                                             End If
                                         End Sub, DispatcherPriority.Loaded)
                Return
            End If

            If e.PropertyName = NameOf(GalleryViewModel.SelectedImmichNode) Then
                Dispatcher.UIThread.Post(Sub()
                                             Dim vm = GetVm()
                                             Dim tree = Me.FindControl(Of TreeView)("ImmichTreeView")
                                             If vm IsNot Nothing AndAlso tree IsNot Nothing AndAlso vm.SelectedImmichNode IsNot Nothing Then
                                                 _clearingNavigationSelection = True
                                                 Try
                                                     tree.SelectedItem = vm.SelectedImmichNode
                                                     BringTreeItemIntoView(tree, vm.SelectedImmichNode)
                                                 Finally
                                                     _clearingNavigationSelection = False
                                                 End Try
                                             End If
                                         End Sub, DispatcherPriority.Loaded)
                Return
            End If

        End Sub

        Private Sub OnGalleryItemsCollectionChanged(sender As Object, e As NotifyCollectionChangedEventArgs)
            If e.Action = NotifyCollectionChangedAction.Reset Then
                _thumbnailTracker.Reset()
            End If
            RebuildTimelineSegments()
            QueueViewportThumbnailRefresh()
        End Sub

        ''' Zeitleisten-Segmente neu aufbauen: bei Listen-Reset (Ordnerwechsel, Nachladen) und bei
        ''' Sortierwechsel. Ein O(n)-Durchlauf, auch bei 30k Immich-Assets unkritisch - aber nicht
        ''' pro Scroll-Tick, deshalb getrennt vom Viewport-Refresh.
        Private Sub RebuildTimelineSegments()
            Dim scrubber = Me.FindControl(Of GalleryTimelineScrubber)("GalleryTimelineScrubber")
            Dim vm = GetVm()
            If scrubber Is Nothing Then Return
            If vm Is Nothing OrElse vm.Items Is Nothing OrElse Not TimelineAllowedForCurrentView(vm) Then
                scrubber.SetData(Nothing, 0)
                scrubber.IsVisible = False
                Return
            End If
            Dim segments = vm.BuildTimelineSegments()
            scrubber.SetData(segments, vm.Items.Count)
            ' Sichtbar, sobald die Sortierung eine Achse hergibt - die fruehere 60-Bilder-Schwelle
            ' ist raus (Nutzer-Feedback: "kann auch immer da sein, stoert nicht").
            scrubber.IsVisible = segments IsNot Nothing
        End Sub

        ''' Einstellung "Zeitleiste am rechten Rand": nur noch an oder aus. Die frueheren Werte nach
        ''' Bildherkunft sind entfallen (siehe AppSettingsService.NormalizeGalleryTimelineMode).
        Private Function TimelineAllowedForCurrentView(vm As GalleryViewModel) As Boolean
            Return Not String.Equals(
                AppSettingsService.NormalizeGalleryTimelineMode(AppSettingsService.Load().GalleryTimelineMode),
                "Off", StringComparison.Ordinal)
        End Function

        Private Sub OnTimelineScrubRequested(sender As Object, offsetFraction As Double)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)(If(vm.IsListView, "GalleryListScrollViewer", "GalleryGridScrollViewer"))
            If scrollViewer Is Nothing Then Return
            Dim range = Math.Max(0.0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height)
            scrollViewer.Offset = New Avalonia.Vector(scrollViewer.Offset.X, offsetFraction * range)
        End Sub

        ''' Positions-Band der Zeitleiste nachführen - läuft im (gedrosselten) Viewport-Refresh mit.
        Private Sub UpdateTimelineScrollState(scrollViewer As ScrollViewer)
            Dim scrubber = Me.FindControl(Of GalleryTimelineScrubber)("GalleryTimelineScrubber")
            If scrubber Is Nothing OrElse scrollViewer Is Nothing Then Return
            Dim extent = scrollViewer.Extent.Height
            Dim viewport = scrollViewer.Viewport.Height
            If extent <= 0 OrElse viewport <= 0 Then Return
            Dim range = Math.Max(1.0, extent - viewport)
            scrubber.SetScrollState(scrollViewer.Offset.Y / range, Math.Min(1.0, viewport / extent))
        End Sub

        Private Sub OnGalleryScrollPropertyChanged(sender As Object, e As Avalonia.AvaloniaPropertyChangedEventArgs)
            If e.Property <> ScrollViewer.OffsetProperty AndAlso
               e.Property <> ScrollViewer.ExtentProperty AndAlso
               e.Property <> Visual.BoundsProperty Then Return
            QueueViewportThumbnailRefresh()
        End Sub

        Private Sub OnGalleryScrollChanged(sender As Object, e As ScrollChangedEventArgs)
            QueueViewportThumbnailRefresh()
        End Sub

        Private _lastViewportRefreshUtc As DateTime = DateTime.MinValue
        Private _viewportRefreshTrailingTimer As DispatcherTimer

        ''' <summary>Drossel (Immich "Alle Fotos" mit 30k Bildern): Scroll-Ereignisse feuern im
        ''' Millisekundentakt, und jedes Fenster-Update ohne Ueberlappung (schnelles Ziehen des
        ''' Balkens) baut dutzende Kacheln inkl. eager erzeugter Kontextmenues neu - ungedrosselt
        ''' wird die UI dabei zaeh. Hoechstens alle ~90 ms aktualisieren; waehrend schnellen
        ''' Scrollens laeuft EIN nachlaufender Aufruf, damit die Endposition immer frisch ist.</summary>
        Private Sub QueueViewportThumbnailRefresh()
            Dim elapsed = (DateTime.UtcNow - _lastViewportRefreshUtc).TotalMilliseconds
            If elapsed >= 90.0 Then
                _viewportRefreshTrailingTimer?.Stop()
                If _viewportThumbnailRefreshQueued Then Return
                _viewportThumbnailRefreshQueued = True
                _lastViewportRefreshUtc = DateTime.UtcNow
                Dispatcher.UIThread.Post(Sub()
                                             _viewportThumbnailRefreshQueued = False
                                             RequestViewportThumbnails()
                                         End Sub, DispatcherPriority.Input)
                Return
            End If

            If _viewportRefreshTrailingTimer Is Nothing Then
                _viewportRefreshTrailingTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(90)}
                AddHandler _viewportRefreshTrailingTimer.Tick, Sub()
                                                                   _viewportRefreshTrailingTimer.Stop()
                                                                   _lastViewportRefreshUtc = DateTime.UtcNow
                                                                   RequestViewportThumbnails()
                                                               End Sub
            End If
            _viewportRefreshTrailingTimer.Stop()
            _viewportRefreshTrailingTimer.Start()
        End Sub

        Private Sub RequestViewportThumbnails()
            Dim vm = GetVm()
            If vm Is Nothing OrElse vm.Items Is Nothing OrElse vm.Items.Count = 0 Then Return

            If vm.IsGroupView Then
                ' Die Gruppenansicht teilt sich die Flaeche mit dem Raster, rechnet aber anders: die
                ' Zeilen sind unterschiedlich hoch, deshalb bekommt das ViewModel den Scrollversatz und
                ' schlaegt in seiner Zeilentabelle nach, was dort steht.
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
                If scrollViewer Is Nothing OrElse scrollViewer.Bounds.Height <= 0 Then Return

                Dim cols = 1
                Dim itemSlotHeight = 0.0
                GetGridLayoutMetrics(scrollViewer, vm, cols, itemSlotHeight, forGroupView:=True)
                Dim contentOffset = Math.Max(0.0, scrollViewer.Offset.Y - 12.0)
                Dim viewHeight = scrollViewer.Bounds.Height
                vm.SetGroupDisplayWindow(contentOffset, viewHeight, itemSlotHeight, cols)

                Dim firstIndex = -1
                Dim lastIndex = -1
                vm.GetGroupVisibleItemRange(contentOffset, viewHeight, firstIndex, lastIndex)
                ' An den Raendern die exakte Grenze nehmen - dieselbe Begruendung wie im Raster: sonst
                ' bleiben die letzten Kacheln im Vorhaltepuffer stehen und fragen nie ein Vorschaubild an.
                If scrollViewer.Offset.Y + viewHeight >= scrollViewer.Extent.Height - 1.0 Then lastIndex = vm.Items.Count - 1
                If scrollViewer.Offset.Y <= 1.0 Then firstIndex = 0
                If firstIndex >= 0 AndAlso lastIndex >= firstIndex Then RequestThumbnailRange(vm, firstIndex, lastIndex)
                UpdateTimelineScrollState(scrollViewer)
                Return
            End If

            If vm.IsGridView Then
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
                If scrollViewer Is Nothing OrElse scrollViewer.Bounds.Height <= 0 Then Return

                Dim cols = 1
                Dim itemSlotHeight = 0.0
                GetGridLayoutMetrics(scrollViewer, vm, cols, itemSlotHeight)
                Dim firstRow = Math.Max(0, CInt(Math.Floor(Math.Max(0.0, scrollViewer.Offset.Y - 12.0) / itemSlotHeight)) - 1)
                Dim lastRow = CInt(Math.Ceiling((scrollViewer.Offset.Y + scrollViewer.Bounds.Height - 12.0) / itemSlotHeight)) + 1
                Dim firstIndex = Math.Max(0, firstRow * cols)
                Dim lastIndex = Math.Min(vm.Items.Count - 1, ((lastRow + 1) * cols) - 1)
                ' Am oberen/unteren Rand des Scrollbereichs die exakte Grenze verwenden statt der
                ' zeilenhöhenbasierten Schätzung - kleine Abweichungen zwischen GridItemSlotHeight und
                ' der tatsächlich gerenderten Zeilenhöhe können sonst dazu führen, dass die letzten
                ' Elemente zwar im breiteren Display-Fenster angezeigt, aber nie für ein Thumbnail
                ' angefragt werden (sie landen nur im Keep-Alive-Puffer, der nicht selbst anfragt).
                If scrollViewer.Offset.Y + scrollViewer.Bounds.Height >= scrollViewer.Extent.Height - 1.0 Then
                    lastIndex = vm.Items.Count - 1
                End If
                If scrollViewer.Offset.Y <= 1.0 Then
                    firstIndex = 0
                End If
                Dim rowCount = Math.Max(1, lastRow - firstRow + 1)
                Dim displayFirst = Math.Max(0, (firstRow - rowCount * 2) * cols)
                Dim displayLast = Math.Min(vm.Items.Count - 1, ((lastRow + rowCount * 2 + 1) * cols) - 1)
                vm.SetDisplayWindow(displayFirst, displayLast, itemSlotHeight, cols)
                RequestThumbnailRange(vm, firstIndex, lastIndex)
                UpdateTimelineScrollState(scrollViewer)
            Else
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryListScrollViewer")
                If scrollViewer Is Nothing OrElse scrollViewer.Bounds.Height <= 0 Then Return

                Const itemSlotHeight As Double = 78
                Dim firstIndex = Math.Max(0, CInt(Math.Floor(Math.Max(0.0, scrollViewer.Offset.Y - 12.0) / itemSlotHeight)) - 4)
                Dim lastIndex = CInt(Math.Ceiling((scrollViewer.Offset.Y + scrollViewer.Bounds.Height - 12.0) / itemSlotHeight)) + 4
                If scrollViewer.Offset.Y + scrollViewer.Bounds.Height >= scrollViewer.Extent.Height - 1.0 Then
                    lastIndex = vm.Items.Count - 1
                End If
                If scrollViewer.Offset.Y <= 1.0 Then
                    firstIndex = 0
                End If
                Dim itemCount = Math.Max(1, lastIndex - firstIndex + 1)
                Dim displayFirst = Math.Max(0, firstIndex - itemCount * 2)
                Dim displayLast = Math.Min(vm.Items.Count - 1, lastIndex + itemCount * 2)
                vm.SetDisplayWindow(displayFirst, displayLast, itemSlotHeight, 1)
                RequestThumbnailRange(vm, firstIndex, lastIndex)
                UpdateTimelineScrollState(scrollViewer)
            End If
        End Sub

        Private Sub RequestThumbnailRange(vm As GalleryViewModel, firstIndex As Integer, lastIndex As Integer)
            If vm Is Nothing OrElse vm.Items Is Nothing Then Return
            _thumbnailTracker.RequestRange(vm.Items, firstIndex, lastIndex)
        End Sub

        Private Sub ResetGalleryScroll()
            Dim gridScroll = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
            If gridScroll IsNot Nothing Then gridScroll.Offset = New Avalonia.Vector(0, 0)

            Dim listScroll = Me.FindControl(Of ScrollViewer)("GalleryListScrollViewer")
            If listScroll IsNot Nothing Then listScroll.Offset = New Avalonia.Vector(0, 0)
        End Sub

        Private Sub OnRequestScrollToItem(sender As Object, e As EventArgs)
            Dispatcher.UIThread.Post(Sub() ScrollToSelectedItem(), DispatcherPriority.Loaded)
        End Sub

        Private Sub ScrollToSelectedItem()
            Dim vm = GetVm()
            If vm Is Nothing OrElse vm.SelectedItem Is Nothing Then Return

            Dim idx = vm.Items.IndexOf(vm.SelectedItem)
            If idx < 0 Then Return

            If vm.IsGroupView Then
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
                If scrollViewer Is Nothing OrElse scrollViewer.Bounds.Height <= 0 Then Return

                Dim cols = 1
                Dim itemSlotHeight = 0.0
                GetGridLayoutMetrics(scrollViewer, vm, cols, itemSlotHeight, forGroupView:=True)

                Dim rowTop = 0.0
                Dim rowHeight = itemSlotHeight
                If Not vm.TryGetGroupItemPosition(idx, cols, itemSlotHeight, rowTop, rowHeight) Then Return

                Dim itemTop = 12.0 + rowTop
                Dim itemBottom = itemTop + rowHeight
                Dim viewHeight = scrollViewer.Bounds.Height
                If itemTop >= scrollViewer.Offset.Y AndAlso itemBottom <= scrollViewer.Offset.Y + viewHeight Then Return

                Dim targetOffset = Math.Max(0.0, itemTop + rowHeight / 2 - viewHeight / 2)
                ' Erst das Fenster um die Zielzeile aufziehen, dann den Versatz setzen - sonst klemmt
                ' der ScrollViewer ihn gegen seine noch veraltete Gesamthoehe.
                vm.SetGroupDisplayWindowAround(rowTop, viewHeight, itemSlotHeight, cols)
                scrollViewer.UpdateLayout()
                Dim maxOffset = Math.Max(0.0, scrollViewer.Extent.Height - viewHeight)
                scrollViewer.Offset = New Avalonia.Vector(0, Math.Min(targetOffset, maxOffset))
                Return
            End If

            If vm.IsGridView Then
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
                If scrollViewer Is Nothing OrElse scrollViewer.Bounds.Height <= 0 Then Return

                Dim cols = 1
                Dim itemSlotHeight = 0.0
                GetGridLayoutMetrics(scrollViewer, vm, cols, itemSlotHeight)
                Dim row = idx \ cols
                Dim itemTop = 12.0 + row * itemSlotHeight
                Dim itemBottom = itemTop + itemSlotHeight

                Dim viewHeight = scrollViewer.Bounds.Height
                If itemTop >= scrollViewer.Offset.Y AndAlso itemBottom <= scrollViewer.Offset.Y + viewHeight Then Return

                Dim targetOffset = Math.Max(0.0, itemTop + itemSlotHeight / 2 - viewHeight / 2)
                Dim totalRows = CInt(Math.Ceiling(vm.Items.Count / CDbl(cols)))
                ' Erst das Virtualisierungsfenster um die Zielzeile aufziehen, damit die Hoehen der
                ' Platzhalter - und damit die Gesamthoehe, mit der der ScrollViewer rechnet - schon
                ' stimmen, BEVOR der Versatz gesetzt wird. Sonst klemmt Avalonia ihn gegen seine
                ' eigene veraltete Gesamthoehe, ganz gleich was oben als Hoechstwert steht.
                Dim windowRowRadius = CInt(Math.Ceiling(viewHeight / itemSlotHeight)) + 4
                Dim windowFirstRow = Math.Max(0, row - windowRowRadius)
                Dim windowLastRow = Math.Min(totalRows - 1, row + windowRowRadius)
                vm.SetDisplayWindow(windowFirstRow * cols, Math.Min(vm.Items.Count - 1, ((windowLastRow + 1) * cols) - 1), itemSlotHeight, cols)
                scrollViewer.UpdateLayout()
                ' Original position calculation, now against the freshly-updated Extent.
                Dim maxOffset = Math.Max(0.0, scrollViewer.Extent.Height - viewHeight)
                scrollViewer.Offset = New Avalonia.Vector(0, Math.Min(targetOffset, maxOffset))
            Else
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryListScrollViewer")
                If scrollViewer Is Nothing OrElse scrollViewer.Bounds.Height <= 0 Then Return

                Const itemSlotHeight As Double = 78  ' Height=72 + Margin="5,3" (3+3)
                Dim itemTop = 12.0 + idx * itemSlotHeight
                Dim itemBottom = itemTop + itemSlotHeight

                Dim viewHeight = scrollViewer.Bounds.Height
                If itemTop >= scrollViewer.Offset.Y AndAlso itemBottom <= scrollViewer.Offset.Y + viewHeight Then Return

                Dim targetOffset = Math.Max(0.0, itemTop + itemSlotHeight / 2 - viewHeight / 2)
                ' Erst das Virtualisierungsfenster um das Zielelement aufziehen, damit die Hoehen der
                ' Platzhalter - und damit die Gesamthoehe, mit der der ScrollViewer rechnet - schon
                ' stimmen, BEVOR der Versatz gesetzt wird. Sonst klemmt Avalonia ihn gegen seine
                ' eigene veraltete Gesamthoehe, ganz gleich was oben als Hoechstwert steht.
                Dim windowRadius = CInt(Math.Ceiling(viewHeight / itemSlotHeight)) + 8
                Dim windowFirst = Math.Max(0, idx - windowRadius)
                Dim windowLast = Math.Min(vm.Items.Count - 1, idx + windowRadius)
                vm.SetDisplayWindow(windowFirst, windowLast, itemSlotHeight, 1)
                scrollViewer.UpdateLayout()
                ' Original position calculation, now against the freshly-updated Extent.
                Dim maxOffset = Math.Max(0.0, scrollViewer.Extent.Height - viewHeight)
                scrollViewer.Offset = New Avalonia.Vector(0, Math.Min(targetOffset, maxOffset))
            End If
        End Sub

        Private Function GetVm() As GalleryViewModel
            Return TryCast(DataContext, GalleryViewModel)
        End Function

        Private Function GetItemFromSender(sender As Object) As ImageItem
            Dim border = TryCast(sender, Border)
            If border IsNot Nothing Then Return TryCast(border.DataContext, ImageItem)
            Dim menuItem = TryCast(sender, MenuItem)
            If menuItem IsNot Nothing Then
                Dim menu = TryCast(menuItem.Parent, ContextMenu)
                If menu IsNot Nothing Then
                    Dim placementTarget = TryCast(menu.PlacementTarget, Control)
                    Dim targetItem = GetImageItemFromControl(placementTarget)
                    If targetItem IsNot Nothing Then Return targetItem
                End If
                Return _contextMenuItem
            End If
            Return Nothing
        End Function

        Private Function GetImageItemFromControl(control As Control) As ImageItem
            Dim current = control
            While current IsNot Nothing
                Dim item = TryCast(current.DataContext, ImageItem)
                If item IsNot Nothing Then Return item
                current = TryCast(current.Parent, Control)
            End While
            Return Nothing
        End Function

        Private Function GetFolderNodeFromSource(source As Object) As FolderNode
            Dim current = TryCast(source, Control)
            While current IsNot Nothing
                Dim node = TryCast(current.DataContext, FolderNode)
                If node IsNot Nothing Then Return node
                current = TryCast(current.Parent, Control)
            End While
            Return Nothing
        End Function

        Public Sub OnFolderTreeSelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            If _clearingNavigationSelection Then Return
            If _restoringFolderTreeSelection Then Return
            If _suppressFolderTreeSelectionChange Then
                _suppressFolderTreeSelectionChange = False
                ' Rechtsklick: nur die Markierung zurueckholen, NICHT scrollen. In einem grossen Baum
                ' liegt der markierte Ordner oft weit weg vom angeklickten - das Zentrieren riss die
                ' Ansicht dorthin, waehrend das Kontextmenue ueber einer nun unsichtbaren Zeile stand.
                RestoreFolderTreeSelection(sender, vm, bringIntoView:=False)
                Return
            End If
            If e.AddedItems Is Nothing OrElse e.AddedItems.Count = 0 Then Return
            Dim node = TryCast(e.AddedItems.Item(0), FolderNode)
            If node IsNot Nothing Then
                ClearVirtualTreeSelections()
                ' Tab auf "Ordner" ziehen: die Rueckkehr aus Viewer/Editor soll denselben Tab
                ' zeigen, in dem zuletzt navigiert wurde.
                vm.NoteFolderNavigation()
                vm.SelectedFolderNode = node
                node.EnsureChildrenLoaded()
                If _isDragging Then Return
                If String.Equals(NormalizePath(vm.CurrentFolder), NormalizePath(node.FullPath), PathIdentity.Comparison) Then Return
                vm.NavigateToFolder(node.FullPath)
            End If
        End Sub

        Public Async Sub OnVirtualTreeSelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            Try
                Dim vm = GetVm()
                If vm Is Nothing OrElse e.AddedItems Is Nothing OrElse e.AddedItems.Count = 0 Then Return
                If _clearingNavigationSelection Then Return
                ' EIN UNSICHTBARER BAUM KANN NICHT ANGEKLICKT WORDEN SEIN. Die Baeume der nicht
                ' offenen Tabs stehen weiter im Visual Tree; werden ihre Knoten nachtraeglich
                ' gefuellt (die Alben kommen aus dem Netz), meldet Avalonia dafuer ein
                ' SelectionChanged. Ohne diese Sperre navigierte die Galerie beim Start von selbst
                ' in das zuletzt eingehaengte Album - beobachtet am Nextcloud-Baum, der seine Alben
                ' im Hintergrund nachlaedt, und es haette jede weitere Quelle genauso getroffen.
                Dim sourceTree = TryCast(sender, TreeView)
                If sourceTree IsNot Nothing AndAlso Not sourceTree.IsEffectivelyVisible Then Return
                Dim node = TryCast(e.AddedItems.Item(0), VirtualNavigationNode)
                If node Is Nothing Then Return
                ClearOtherNavigationSelections(TryCast(sender, TreeView))
                Dim opened = Await vm.OpenVirtualNavigationNode(node)
                ' Personen/Orte sind reine Auf-/Zuklapp-Knoten (öffnen keine Ansicht): Auswahl sofort
                ' wieder lösen, damit der NÄCHSTE Klick erneut ein SelectionChanged auslöst - sonst
                ' ließe sich der Knoten nach dem Aufklappen nie wieder zuklappen (der Chevron ist im
                ' Immich-Baum ausgeblendet, siehe Style im XAML) - und die Ordner-Markierung des
                ' weiterhin aktiven Ordners zurückholen.
                If String.Equals(node.Kind, "ImmichPeopleRoot", StringComparison.Ordinal) OrElse
                   String.Equals(node.Kind, "ImmichPlacesRoot", StringComparison.Ordinal) Then
                    ClearVirtualTreeSelections()
                    Dim activeFolderTree = Me.FindControl(Of TreeView)("FolderTreeView")
                    RestoreFolderTreeSelection(activeFolderTree, vm)
                    Return
                End If
                ' "Neue Suche" per Dialog abgebrochen: der Ordner-/Suchbaum blieb oben bereits ohne
                ' Auswahl (ClearOtherNavigationSelections) - sichtbare Baumauswahl wieder auf den
                ' tatsächlich aktiven Ordner zurücksetzen, statt sie auf "Neue Suche" hängen zu lassen.
                If String.Equals(node.Kind, "NewSearch", StringComparison.Ordinal) Then
                    If Not opened Then
                        ClearVirtualTreeSelections()
                        Dim folderTree = Me.FindControl(Of TreeView)("FolderTreeView")
                        RestoreFolderTreeSelection(folderTree, vm)
                    Else
                        ' Angelegt: die Markierung gehoert auf die NEUE Suche, nicht auf den Eintrag
                        ' "Neue Suche" - sonst sieht es aus, als sei nichts entstanden.
                        SelectNavigationNode(vm.NavigationRestoreTreeName, vm.NavigationRestoreNode)
                    End If
                End If
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnVirtualTreeSelectionChanged", ex)
            End Try
        End Sub

        Public Async Sub OnImmichNodePointerPressed(sender As Object, e As PointerPressedEventArgs)
            Try
                Dim point = e.GetCurrentPoint(TryCast(sender, Control))
                If Not point.Properties.IsLeftButtonPressed Then Return
                Dim node = TryCast(TryCast(sender, Control)?.DataContext, VirtualNavigationNode)
                If node Is Nothing Then Return
                If Not (String.Equals(node.Kind, "ImmichPeopleRoot", StringComparison.Ordinal) OrElse
                        String.Equals(node.Kind, "ImmichPlacesRoot", StringComparison.Ordinal)) Then Return

                e.Handled = True
                Dim vm = GetVm()
                If vm Is Nothing Then Return
                Await vm.OpenVirtualNavigationNode(node)
                ClearVirtualTreeSelections()
                RestoreFolderTreeSelection(Me.FindControl(Of TreeView)("FolderTreeView"), vm)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnImmichNodePointerPressed", ex)
            End Try
        End Sub

        ''' Namen aller virtuellen Baeume - je Tab eigene Suchliste plus Immich-, Nextcloud- und
        ''' Favoritenbaum. Wer hier einen vergisst, bekommt zwei gleichzeitig markierte Baeume.
        Private Shared ReadOnly VirtualTreeNames As String() =
            {"SearchTreeView", "ImmichSearchTreeView", "NextcloudSearchTreeView",
             "ImmichTreeView", "NextcloudTreeView", "FavoritesTreeView"}

        ''' <summary>Setzt die sichtbare Baumauswahl auf einen bestimmten Knoten. Nachgelagert, weil
        ''' ein gerade erst eingehaengter Knoten seinen TreeViewItem noch nicht hat.</summary>
        Private Sub SelectNavigationNode(treeName As String, targetNode As VirtualNavigationNode)
            If String.IsNullOrEmpty(treeName) OrElse targetNode Is Nothing Then Return
            Dispatcher.UIThread.Post(Sub()
                                         Dim tree = Me.FindControl(Of TreeView)(treeName)
                                         If tree Is Nothing Then Return
                                         _clearingNavigationSelection = True
                                         Try
                                             tree.SelectedItem = targetNode
                                             BringTreeItemIntoView(tree, targetNode)
                                         Finally
                                             _clearingNavigationSelection = False
                                         End Try
                                     End Sub, DispatcherPriority.Loaded)
        End Sub

        Private Sub ClearVirtualTreeSelections()
            _clearingNavigationSelection = True
            Try
                For Each treeName As String In VirtualTreeNames
                    Dim tree = Me.FindControl(Of TreeView)(treeName)
                    If tree IsNot Nothing Then tree.SelectedItem = Nothing
                Next
            Finally
                _clearingNavigationSelection = False
            End Try
        End Sub

        Private Sub ClearOtherNavigationSelections(activeTree As TreeView)
            _clearingNavigationSelection = True
            Try
                For Each treeName As String In VirtualTreeNames
                    Dim tree = Me.FindControl(Of TreeView)(treeName)
                    If tree IsNot Nothing AndAlso Not Object.ReferenceEquals(tree, activeTree) Then tree.SelectedItem = Nothing
                Next
                Dim folderTree = Me.FindControl(Of TreeView)("FolderTreeView")
                If folderTree IsNot Nothing Then folderTree.SelectedItem = Nothing
            Finally
                _clearingNavigationSelection = False
            End Try
        End Sub

        Public Sub OnRemoveVirtualSearchClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.RemoveVirtualSearchNode(GetVirtualNodeFromSender(sender))
            e.Handled = True
        End Sub

        Public Sub OnEditVirtualSearchClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.EditVirtualSearchNode(GetVirtualNodeFromSender(sender))
            e.Handled = True
        End Sub

        Public Sub OnImmichNewAlbumClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.CreateImmichAlbum()
            e.Handled = True
        End Sub

        Public Sub OnImmichRenameAlbumClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.RenameImmichAlbum(GetVirtualNodeFromSender(sender))
            e.Handled = True
        End Sub

        Public Sub OnImmichDeleteAlbumClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.DeleteImmichAlbum(GetVirtualNodeFromSender(sender))
            e.Handled = True
        End Sub

        ''' <summary>Papierkorb leeren - fuer beide Serverquellen derselbe Eintrag, die Quelle sagt
        ''' der Knoten. Die Rueckfrage stellt das ViewModel, damit sie an EINER Stelle steht.</summary>
        Public Sub OnEmptyTrashClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Dim node = GetVirtualNodeFromSender(sender)
            If node Is Nothing Then Return
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            Dim ignored = vm.EmptyTrashAsync(node)
        End Sub

        Public Sub OnNextcloudNewAlbumClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.CreateNextcloudAlbum()
            e.Handled = True
        End Sub

        Public Sub OnNextcloudRenameAlbumClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.RenameNextcloudAlbum(GetVirtualNodeFromSender(sender))
            e.Handled = True
        End Sub

        Public Sub OnNextcloudDeleteAlbumClick(sender As Object, e As RoutedEventArgs)
            GetVm()?.DeleteNextcloudAlbum(GetVirtualNodeFromSender(sender))
            e.Handled = True
        End Sub

        ''' <summary>Rechtsklick auf einen Nextcloud-Knoten: dasselbe wie im Immich-Baum - die Zeile
        ''' unter dem Zeiger wird markiert, damit das Menue sich auf SIE bezieht und nicht auf die
        ''' zuletzt geoeffnete.</summary>
        Public Sub OnNextcloudNodePointerPressed(sender As Object, e As PointerPressedEventArgs)
            Try
                OnImmichNodePointerPressed(sender, e)
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryView.OnNextcloudNodePointerPressed", ex)
            End Try
        End Sub

        ' --- Drag&Drop lokal → Immich (Datei-Payload auf einen Immich-Knoten ablegen = Upload) ---

        ''' <summary>Knoten unter dem Zeiger. Laeuft erst den LOGISCHEN, dann den VISUELLEN Elternpfad
        ''' hoch: bei Inhalten aus einem ItemTemplate reisst die logische Kette ab, der Knoten waere sonst
        ''' je nach getroffenem Element mal auffindbar und mal nicht - genau daraus entstand die
        ''' Abweichung zwischen Mauszeiger und tatsaechlichem Ablegen.</summary>
        ''' <summary>Das Ergebnis der letzten Elternsuche, samt Element, für das es galt.
        '''
        ''' WARUM GEMERKT: Ein Zug erzeugt hunderte Zeigerberichte je Sekunde, und der Zeiger steht
        ''' dabei fast immer über DEMSELBEN Element. Die Suche selbst ist teuer - sie liest je Stufe
        ''' <c>DataContext</c>, und der wird VERERBT, geht also seinerseits die Kette hoch. Gemessen
        ''' an einer hängenden Anwendung (2026-08-10, dotnet-trace): GetImmichDropNode war mit
        ''' Abstand die teuerste aktive Arbeit im Prozess, alles andere waren Wartezustände. Bei
        ''' zwei Suchen je Bericht (Knoten und Zeile) kam der UI-Faden nicht mehr hinterher.</summary>
        Private _lastDropSource As Control = Nothing
        Private _lastDropNode As VirtualNavigationNode = Nothing
        Private _lastDropRow As Control = Nothing

        Private Sub ResolveDropSource(e As DragEventArgs)
            Dim source = TryCast(e.Source, Control)
            If Object.ReferenceEquals(source, _lastDropSource) Then Return

            _lastDropSource = source
            _lastDropNode = Nothing
            _lastDropRow = Nothing
            ' EIN Durchlauf für beides: der Knoten (wohin abgelegt wird) und die Zeile (was
            ' hervorgehoben wird) liegen auf demselben Elternpfad. Zwei Durchläufe waren doppelte
            ' Arbeit an derselben Kette.
            Dim current = source
            Dim depth = 0
            While current IsNot Nothing
                If _lastDropNode Is Nothing Then
                    Dim node = TryCast(current.DataContext, VirtualNavigationNode)
                    If node IsNot Nothing Then _lastDropNode = node
                End If
                If _lastDropRow Is Nothing AndAlso TypeOf current Is TreeViewItem Then _lastDropRow = current
                If _lastDropNode IsNot Nothing AndAlso _lastDropRow IsNot Nothing Then Exit While
                ' DIE HARTE GRENZE, und sie ist der eigentliche Fix (gemessen 2026-08-10 an einer
                ' hängenden Anwendung, dotnet-trace: der UI-Faden stand die ganze Messung in dieser
                ' Schleife). Ein Elternpfad wird ueber die LOGISCHE Kette gegangen und faellt auf
                ' die VISUELLE zurueck - und diese Mischung kann einen RING bilden: A zeigt logisch
                ' auf B, B visuell wieder auf A. Ein Schutz gegen den direkten Selbstbezug faengt
                ' das nicht, die Schleife laeuft ewig, der Faden kommt nie zurueck, und weil auch
                ' die Zeitgeber auf ihm laufen, meldet sich nicht einmal ein Zeitablauf.
                ' Ein echter Elternpfad ist nie so tief; tiefer heisst: hier stimmt etwas nicht.
                depth += 1
                If depth > 64 Then
                    DiagnosticLogService.LogAlways("Drag", "Elternpfad tiefer als 64 Stufen - abgebrochen (Ring?)")
                    Exit While
                End If
                Dim logicalParent = TryCast(current.Parent, Control)
                Dim nextParent = If(logicalParent, current.GetVisualParent(Of Control)())
                If Object.ReferenceEquals(nextParent, current) Then Exit While
                current = nextParent
            End While
        End Sub

        Private Function GetImmichDropNode(e As DragEventArgs) As VirtualNavigationNode
            ResolveDropSource(e)
            Return _lastDropNode
        End Function

        ''' <summary>Ziel und Nutzlast eines Immich-Drops - EINE Quelle fuer Mauszeiger und Ablegen, damit
        ''' die beiden nicht auseinanderlaufen koennen.
        ''' <paramref name="requireExistingFiles"/>: beim Ablegen muessen die Dateien wirklich da sein;
        ''' beim Ueberfliegen nicht - unter X11 reicht ein fremder Ziehvorgang die Dateiliste erst BEIM
        ''' Ablegen heraus, waehrend DragOver liest sie sich leer. Der Zeiger zeigte deshalb "geht nicht",
        ''' obwohl der Upload danach lief.</summary>
        ''' <summary>Was auf einem Serverknoten abgelegt wird, zerfaellt in ZWEI Sorten: lokale
        ''' Dateien werden hochgeladen, Bilder DERSELBEN Serverquelle nur zugeordnet. Frueher fielen
        ''' die Serverpfade hier ganz heraus, und ein Ziehen aus "Alle Fotos" in ein Album tat
        ''' schlicht nichts.</summary>
        Private Function ResolveImmichDrop(e As DragEventArgs, requireExistingFiles As Boolean) _
            As (Node As VirtualNavigationNode, LocalPaths As List(Of String), RemotePaths As List(Of String), PayloadUnreadable As Boolean)
            Dim node = GetImmichDropNode(e)
            Dim leer = New List(Of String)()
            If node Is Nothing OrElse Not (node.IsImmichNode OrElse node.IsNextcloudNode) Then Return (Nothing, leer, leer, False)
            Dim payload = GetDragPayload(e)
            Dim localPaths = payload.Paths.
                Where(Function(p) Not ImmichService.IsImmichPseudoPath(p) AndAlso
                                  Not NextcloudService.IsNextcloudPseudoPath(p) AndAlso
                                  (Not requireExistingFiles OrElse IO.File.Exists(p))).ToList()
            ' Nur Pfade der EIGENEN Quelle: ein Immich-Bild in ein Nextcloud-Album zu ziehen waere
            ' ein Umzug ueber die Leitung, keine Zuordnung.
            Dim remotePaths = payload.Paths.
                Where(Function(p) If(node.IsImmichAlbumNode,
                                     ImmichService.IsImmichPseudoPath(p),
                                     node.IsNextcloudAlbumNode AndAlso NextcloudService.IsNextcloudPseudoPath(p))).ToList()
            Return (node, localPaths, remotePaths, payload.Paths.Count = 0)
        End Function

        ''' <summary>Waehrend des Ziehens entscheidet der ZIELKNOTEN, nicht die Nutzlast.
        '''
        ''' Unter X11 laeuft auch ein anwendungsinterner Zug ueber das Fenstersystem, und die Daten
        ''' kommen dort erst beim Ablegen heraus - waehrend der Bewegung liest sich die Last leer.
        ''' Wer daran den Mauszeiger festmacht, zeigt "geht nicht" ueber einem Ziel, auf dem der Drop
        ''' anschliessend laeuft (Nutzerbefund 2026-08-10: Verbotszeichen an allen Baumzielen).
        ''' Angeboten wird deshalb, sobald der Knoten ueberhaupt etwas annehmen KANN; was wirklich
        ''' ankommt, entscheidet der Drop.</summary>
        Public Sub OnImmichTreeDragOver(sender As Object, e As DragEventArgs)
            Dim node = GetImmichDropNode(e)
            DragTrace.Over(If(node?.Kind, "kein Knoten"))
            Dim nimmtAn = node IsNot Nothing AndAlso Not node.IsTrashNode AndAlso
                          (node.IsImmichNode OrElse node.IsNextcloudNode)
            e.DragEffects = If(nimmtAn, DragDropEffects.Copy, DragDropEffects.None)
            HighlightDropRow(e, nimmtAn)
            e.Handled = True
        End Sub

        Public Sub OnImmichTreeDrop(sender As Object, e As DragEventArgs)
            ClearDropHighlight()
            Dim drop = ResolveImmichDrop(e, requireExistingFiles:=True)
            DiagnosticLogService.LogAlways("Drag", $"drop ziel={If(drop.Node?.Kind, "-")} lokal={drop.LocalPaths.Count} server={drop.RemotePaths.Count}")
            If drop.Node Is Nothing Then Return
            e.Handled = True
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            ' Bilder derselben Quelle werden ZUGEORDNET, lokale Dateien hochgeladen. Beides kann in
            ' einer Ablage stecken, wenn jemand gemischt ausgewaehlt hat.
            '
            ' ERST NACH DEM EREIGNIS, nicht darin: die Ziehgeste laeuft noch, solange dieser Handler
            ' arbeitet, und alles, was hier beginnt, haengt an ihr. Serverarbeit gehoert dahinter.
            If drop.RemotePaths.Count > 0 Then
                Dim targetNode = drop.Node
                Dim remotePaths = drop.RemotePaths
                Dispatcher.UIThread.Post(Sub()
                                             Dim ignored = vm.AddRemotePathsToAlbumAsync(targetNode, remotePaths)
                                         End Sub, DispatcherPriority.Background)
            End If
            If drop.LocalPaths.Count > 0 Then
                ' Jede Quelle bekommt ihre eigenen Dateien: der Knoten sagt, wohin hochgeladen wird.
                If drop.Node.IsImmichNode Then
                    vm.UploadToImmich(drop.Node, drop.LocalPaths)
                ElseIf drop.Node.IsNextcloudNode Then
                    vm.UploadToNextcloud(drop.Node, drop.LocalPaths)
                End If
            End If
        End Sub

        Public Sub OnNextcloudTreeDragOver(sender As Object, e As DragEventArgs)
            OnImmichTreeDragOver(sender, e)
        End Sub

        Public Sub OnNextcloudTreeDrop(sender As Object, e As DragEventArgs)
            OnImmichTreeDrop(sender, e)
        End Sub

        ''' <summary>Einfuegen auf einem Nextcloud-Knoten: Bilder DERSELBEN Quelle werden dem Album
        ''' zugeordnet, lokale Dateien in den Dateibaum hochgeladen.
        '''
        ''' Ein Immich-Bild bleibt draussen - es einzufuegen hiesse, es erst herunter- und wieder
        ''' hochzuladen. Das ist ein Umzug und keine Zuordnung.</summary>
        Public Async Sub OnNextcloudPasteClick(sender As Object, e As RoutedEventArgs)
            Try
                e.Handled = True
                Dim vm = GetVm()
                If vm Is Nothing Then Return
                Dim node = GetVirtualNodeFromSender(sender)
                If node Is Nothing OrElse Not node.IsNextcloudNode Then Return
                Dim clipboardData = Await ClipboardPathService.ReadPathDataAsync(TopLevel.GetTopLevel(Me)?.Clipboard)
                Dim remotePaths = clipboardData.Paths.Where(AddressOf NextcloudService.IsNextcloudPseudoPath).ToList()
                If remotePaths.Count > 0 AndAlso node.IsNextcloudAlbumNode Then
                    Await vm.AddRemotePathsToAlbumAsync(node, remotePaths)
                End If
                Dim localPaths = clipboardData.Paths.Where(Function(p) Not ImmichService.IsImmichPseudoPath(p) AndAlso
                                                                       Not NextcloudService.IsNextcloudPseudoPath(p) AndAlso
                                                                       IO.File.Exists(p)).ToList()
                If localPaths.Count = 0 Then Return
                vm.UploadToNextcloud(node, localPaths)
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryView.OnNextcloudPasteClick", ex)
            End Try
        End Sub

        ''' <summary>Bilder ueber den Dateiwaehler nach Nextcloud hochladen (Kontextmenue im
        ''' Nextcloud-Baum). Gegenstueck zu <see cref="OnImmichUploadClick"/>.</summary>
        Public Async Sub OnNextcloudUploadClick(sender As Object, e As RoutedEventArgs)
            Try
                Dim vm = GetVm()
                If vm Is Nothing Then Return
                Dim node = GetVirtualNodeFromSender(sender)
                Dim storageProvider = TopLevel.GetTopLevel(Me)?.StorageProvider
                If storageProvider Is Nothing Then Return
                e.Handled = True
                Dim mediaType = New Avalonia.Platform.Storage.FilePickerFileType(LocalizationService.T("Bilder & Videos")) With {
                    .Patterns = New List(Of String) From {
                        "*.jpg", "*.jpeg", "*.png", "*.gif", "*.bmp", "*.tif", "*.tiff", "*.webp",
                        "*.heic", "*.heif", "*.avif", "*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm"}
                }
                Dim files = Await storageProvider.OpenFilePickerAsync(New Avalonia.Platform.Storage.FilePickerOpenOptions With {
                    .Title = LocalizationService.T("Bilder/Videos zum Hochladen wählen"),
                    .AllowMultiple = True,
                    .FileTypeFilter = New List(Of Avalonia.Platform.Storage.FilePickerFileType) From {mediaType}
                })
                If files Is Nothing Then Return
                Dim paths = files.Select(Function(f) f.Path.LocalPath).Where(Function(p) Not String.IsNullOrEmpty(p)).ToList()
                If paths.Count = 0 Then Return
                vm.UploadToNextcloud(node, paths)
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryView.OnNextcloudUploadClick", ex)
            End Try
        End Sub

        Public Async Sub OnImmichPasteClick(sender As Object, e As RoutedEventArgs)
            Try
                e.Handled = True
                Dim vm = GetVm()
                If vm Is Nothing Then Return
                Dim node = GetVirtualNodeFromSender(sender)
                If node Is Nothing OrElse Not node.IsImmichNode Then Return
                Dim clipboardData = Await ClipboardPathService.ReadPathDataAsync(TopLevel.GetTopLevel(Me)?.Clipboard)
                ' Immich-Bilder in der Zwischenablage gehoeren dem Album ZUGEORDNET, nicht erneut
                ' hochgeladen - sonst entstuende von jedem eine zweite Fassung.
                Dim immichPaths = clipboardData.Paths.Where(AddressOf ImmichService.IsImmichPseudoPath).ToList()
                If immichPaths.Count > 0 AndAlso node.IsImmichAlbumNode Then
                    Await vm.AddRemotePathsToAlbumAsync(node, immichPaths)
                End If
                Dim localPaths = clipboardData.Paths.Where(Function(p) Not ImmichService.IsImmichPseudoPath(p) AndAlso
                                                                       Not NextcloudService.IsNextcloudPseudoPath(p) AndAlso
                                                                       IO.File.Exists(p)).ToList()
                If localPaths.Count = 0 Then Return
                vm.UploadToImmich(node, localPaths)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnImmichPasteClick", ex)
            End Try
        End Sub

        Public Async Sub OnImmichUploadClick(sender As Object, e As RoutedEventArgs)
            Try
                Dim vm = GetVm()
                If vm Is Nothing Then Return
                Dim node = GetVirtualNodeFromSender(sender)
                Dim storageProvider = TopLevel.GetTopLevel(Me)?.StorageProvider
                If storageProvider Is Nothing Then Return
                e.Handled = True
                Dim mediaType = New Avalonia.Platform.Storage.FilePickerFileType(LocalizationService.T("Bilder & Videos")) With {
                    .Patterns = New List(Of String) From {
                        "*.jpg", "*.jpeg", "*.png", "*.gif", "*.bmp", "*.tif", "*.tiff", "*.webp",
                        "*.heic", "*.heif", "*.avif", "*.mp4", "*.mov", "*.mkv", "*.avi", "*.webm"}
                }
                Dim files = Await storageProvider.OpenFilePickerAsync(New Avalonia.Platform.Storage.FilePickerOpenOptions With {
                    .Title = LocalizationService.T("Bilder/Videos zum Hochladen wählen"),
                    .AllowMultiple = True,
                    .FileTypeFilter = New List(Of Avalonia.Platform.Storage.FilePickerFileType) From {mediaType}
                })
                If files Is Nothing Then Return
                Dim paths = files.Select(Function(f) f.Path.LocalPath).Where(Function(p) Not String.IsNullOrEmpty(p)).ToList()
                If paths.Count = 0 Then Return
                vm.UploadToImmich(node, paths)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnImmichUploadClick", ex)
            End Try
        End Sub

        ' Kein Kontextmenü für den festen "Neue Suche"-Knoten (nicht bearbeit-/entfernbar) - würde
        ' sonst als leeres Popup erscheinen, da beide Einträge auf IsRemovable ausgeblendet sind.
        Private Sub OnSearchNodeContextRequested(sender As Object, e As ContextRequestedEventArgs)
            Dim node = TryCast(TryCast(sender, Control)?.DataContext, VirtualNavigationNode)
            If node Is Nothing OrElse Not node.IsRemovable Then e.Handled = True
        End Sub

        ' sender ist je nach Auslöser ein Button (X-Symbol, im Visual Tree - DataContext erbt normal) oder
        ' ein MenuItem (im ContextMenu-Popup, eigener Visual Tree). Für das MenuItem den Knoten über das
        ' PlacementTarget des Menüs auflösen (analog GetItemFromSender), da MenuItem.DataContext hier nicht
        ' zuverlässig vom Item-Template erbt.
        Private Function GetVirtualNodeFromSender(sender As Object) As VirtualNavigationNode
            Dim direct = TryCast(TryCast(sender, Control)?.DataContext, VirtualNavigationNode)
            If direct IsNot Nothing Then Return direct
            Dim menuItem = TryCast(sender, MenuItem)
            If menuItem IsNot Nothing Then
                Dim menu = TryCast(menuItem.Parent, ContextMenu)
                Dim target = TryCast(menu?.PlacementTarget, Control)
                Return TryCast(target?.DataContext, VirtualNavigationNode)
            End If
            Return Nothing
        End Function

        Private Sub OnFolderTreePointerPressedTunnel(sender As Object, e As PointerPressedEventArgs)
            Dim properties = e.GetCurrentPoint(Nothing).Properties
            If properties.IsRightButtonPressed Then
                _folderTreeContextNode = GetFolderNodeFromSource(e.Source)
                _suppressFolderTreeSelectionChange = _folderTreeContextNode IsNot Nothing
            ElseIf properties.IsLeftButtonPressed Then
                _folderTreeContextNode = Nothing
            End If
        End Sub

        ' Der ContentControl in MainWindow baut die GalleryView bei jedem Moduswechsel neu auf (z.B.
        ' Galerie -> Einstellungen -> Galerie). Das ViewModel überlebt und kennt den Ordner weiterhin,
        ' die frisch erzeugte TreeView startet aber ohne SelectedItem - ohne dieses Nachziehen bliebe
        ' der aktive Ordner im Baum unmarkiert. Bei aktiver Suchliste (virtueller Ordner) hat die
        ' Auswahl im Ordnerbaum nichts zu suchen.
        Private Sub RestoreFolderTreeSelectionAfterRecreation()
            Dispatcher.UIThread.Post(
                Sub()
                    Dim vm = GetVm()
                    If vm Is Nothing OrElse vm.IsVirtualFolder OrElse vm.SelectedFolderNode Is Nothing Then Return
                    RestoreFolderTreeSelection(Me.FindControl(Of TreeView)("FolderTreeView"), vm)
                End Sub, DispatcherPriority.Background)
        End Sub

        Private Sub RestoreFolderTreeSelection(sender As Object, vm As GalleryViewModel, Optional bringIntoView As Boolean = True)
            Dim tree = TryCast(sender, TreeView)
            If tree Is Nothing OrElse vm.SelectedFolderNode Is Nothing Then Return
            RestoreFolderTreeSelection(tree, vm.SelectedFolderNode, bringIntoView)
        End Sub

        Private Sub RestoreFolderTreeSelection(tree As TreeView, node As FolderNode, Optional bringIntoView As Boolean = True)
            If tree Is Nothing OrElse node Is Nothing Then Return
            _restoringFolderTreeSelection = True
            Try
                tree.SelectedItem = node
            Finally
                _restoringFolderTreeSelection = False
            End Try
            ' NUR beim initialen Anzeigen der (neu aufgebauten) TreeView den aktuellen Ordner in den
            ' sichtbaren Bereich holen - Auto-Scrollen bei normaler Navigation stoerte den Nutzer
            ' (der Baum "zog" jeden angeklickten Ordner in die Mitte). Der Baum selbst scrollt nicht
            ' von sich aus: AutoScrollToSelectedItem ist im XAML abgeschaltet, sonst riss das
            ' Zurueckholen der Markierung nach einem Rechtsklick die Ansicht weg (und ein
            ' nachtraegliches Zurueckstellen des Scroll-Stands flackerte sichtbar).
            If bringIntoView Then BringTreeItemIntoView(tree, node)
        End Sub

        Public Sub OnThumbnailPointerPressed(sender As Object, e As PointerPressedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            Dim item = GetItemFromSender(sender)
            If item IsNot Nothing Then
                Dim properties = e.GetCurrentPoint(Nothing).Properties
                If properties.IsMiddleButtonPressed Then
                    If item.IsImage Then ShowQuickPreview(item)
                    e.Handled = True
                    Return
                End If
                If Not properties.IsLeftButtonPressed Then Return
                If item.IsParentFolderEntry Then
                    vm.SelectOnly(item)
                    _selectionAnchor = item
                    e.Handled = True
                    Return
                End If
                If Not e.KeyModifiers.HasFlag(KeyModifiers.Shift) AndAlso
                   Not PlatformShortcutService.HasSelectionModifier(e.KeyModifiers) AndAlso
                   vm.SelectedItems IsNot Nothing AndAlso
                   vm.SelectedItems.Count > 1 AndAlso
                   vm.SelectedItems.Contains(item) Then
                    _dragStartItem = item
                    _dragStartPoint = e.GetPosition(Me)
                    _dragStartArgs = e
                    Me.Focus()
                    e.Handled = True
                    Return
                End If
                ApplyPointerSelection(vm, item, e.KeyModifiers)
                _dragStartItem = item
                _dragStartPoint = e.GetPosition(Me)
                _dragStartArgs = e
                Me.Focus()
                e.Handled = True
            End If
        End Sub

        ''' <summary>Klick in den LEEREN Bereich der Galerie hebt die Auswahl auf.
        '''
        ''' Läuft in der Blasenphase am ScrollViewer: ein Klick auf eine Kachel wird in
        ''' <see cref="OnThumbnailPointerPressed"/> als behandelt markiert und kommt hier gar nicht
        ''' erst an. Die zweite Prüfung über <see cref="HasImageItemContext"/> ist trotzdem kein
        ''' Zierrat - Teile einer Kachel (Bewertungssterne, Abzeichen) können den Klick zwar
        ''' verarbeiten, ohne ihn als behandelt zu melden, und dann läge die Kachel unter dem Zeiger,
        ''' obwohl das Ereignis durchkommt.
        '''
        ''' Nur die linke Taste: ein Rechtsklick ins Leere öffnet das Kontextmenü des Bereichs und
        ''' darf die Auswahl nicht wegnehmen - sonst zielte "Einfügen" plötzlich ins Nichts.</summary>
        Public Sub OnGalleryAreaPointerPressed(sender As Object, e As PointerPressedEventArgs)
            If e.Handled Then Return
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            If Not e.GetCurrentPoint(Nothing).Properties.IsLeftButtonPressed Then Return
            If HasImageItemContext(e.Source) Then Return
            If vm.SelectedItems Is Nothing OrElse vm.SelectedItems.Count = 0 Then Return
            vm.ClearSelection()
            _selectionAnchor = Nothing
        End Sub

        ''' <summary>Sichtbarkeit eines benannten Menueeintrags. Wird noch von den Menues des
        ''' LEEREN Bereichs und der Ordner gebraucht - die BILD-Kontextmenues bauen sich dagegen
        ''' als Daten ueber <see cref="ContextMenuBuilder"/> auf und brauchen so etwas nicht mehr.</summary>
        Private Sub SetMenuItemVisible(name As String, visible As Boolean)
            Dim entry = Me.FindControl(Of MenuItem)(name)
            If entry IsNot Nothing Then entry.IsVisible = visible
        End Sub

        Private Function ConsumeSuppressedGalleryContextMenu(e As ContextRequestedEventArgs) As Boolean
            If Not _suppressNextGalleryContextMenu Then Return False
            _suppressNextGalleryContextMenu = False
            If e IsNot Nothing Then e.Handled = True
            Return True
        End Function

        Public Sub OnSelectionBadgeClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim item = TryCast(button?.DataContext, ImageItem)
            Dim vm = GetVm()
            If vm Is Nothing OrElse item Is Nothing Then Return
            vm.ToggleSelection(item)
            _selectionAnchor = item
            Me.Focus()
            e.Handled = True
        End Sub

        ''' Der Kreis an der Kopfzeile einer Gruppe. Er wirkt wie der auf der Kachel, nur auf alle
        ''' Bilder der Gruppe zugleich, und laesst eine Auswahl ausserhalb der Gruppe stehen.
        Public Sub OnGroupSelectionBadgeClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim header = TryCast(button?.DataContext, ImageItem)
            Dim vm = GetVm()
            If vm Is Nothing OrElse header Is Nothing Then Return
            vm.ToggleGroupSelection(header)
            _selectionAnchor = vm.SelectedItem
            Me.Focus()
            e.Handled = True
        End Sub

        ''' Ein einzelner Klick auf das Play-Badge öffnet das Video direkt im Viewer, statt wie
        ''' bei normalen Kacheln einen Doppelklick zu verlangen - Videos will man in der Regel
        ''' sofort ansehen, nicht erst auswählen.
        Public Sub OnVideoPlayBadgeClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim item = TryCast(button?.DataContext, ImageItem)
            Dim vm = GetVm()
            If vm Is Nothing OrElse item Is Nothing Then Return
            vm.SelectOnly(item)
            _selectionAnchor = item
            OpenGalleryItem(item)
            e.Handled = True
        End Sub

        Public Sub OnHoverOpenViewerClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim item = TryCast(button?.DataContext, ImageItem)
            If item Is Nothing Then Return

            Dim vm = GetVm()
            If vm Is Nothing Then Return
            vm.SelectOnly(item)
            _selectionAnchor = item
            ' Das Auge auf der Kachel heisst „anzeigen" - daneben sitzt der Stift zum Bearbeiten.
            OpenGalleryItemInViewer(item)
            e.Handled = True
        End Sub

        Public Sub OnHoverOpenEditorClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim item = TryCast(button?.DataContext, ImageItem)
            Dim vm = GetVm()
            If vm Is Nothing OrElse item Is Nothing OrElse Not item.CanEditFile Then Return

            vm.SelectOnly(item)
            _selectionAnchor = item
            vm.OpenSelectedInEditor()
            e.Handled = True
        End Sub

        Public Sub OnHoverDeleteClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim item = TryCast(button?.DataContext, ImageItem)
            Dim vm = GetVm()
            If vm Is Nothing OrElse item Is Nothing Then Return

            vm.SelectOnly(item)
            _selectionAnchor = item
            vm.DeleteSelectedCommand.Execute(Nothing)
            e.Handled = True
        End Sub

        ''' Farbetikett-Punktreihe unten im Kontextmenü: Tag = Hex-Farbe der Akzentpalette bzw. ""
        ''' (Etikett entfernen). Trifft der Rechtsklick ein markiertes Bild, wendet das ViewModel das
        ''' Etikett auf die ganze Auswahl an. Buttons schließen das Menü nicht automatisch (nur
        ''' MenuItems tun das) - deshalb explizit schließen.</summary>
        Public Sub OnContextSetColorLabel(sender As Object, e As RoutedEventArgs)
            Dim control = TryCast(sender, Control)
            Dim item = TryCast(control?.DataContext, ImageItem)
            Dim vm = GetVm()
            If control Is Nothing OrElse item Is Nothing OrElse vm Is Nothing Then Return
            vm.SetItemColorLabel(item, If(control.Tag, "").ToString())
            control.FindAncestorOfType(Of ContextMenu)()?.Close()
            e.Handled = True
        End Sub

        Public Sub OnHoverSetRatingClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim item = TryCast(button?.DataContext, ImageItem)
            Dim vm = GetVm()
            Dim rating As Integer
            If button Is Nothing OrElse item Is Nothing OrElse vm Is Nothing Then Return
            If Not Integer.TryParse(If(button.Tag, "").ToString(), rating) Then Return

            vm.SetItemRating(item, rating)
            e.Handled = True
        End Sub

        Public Sub OnMetadataBadgePointerEntered(sender As Object, e As PointerEventArgs)
            Dim control = TryCast(sender, Control)
            Dim item = TryCast(control?.DataContext, ImageItem)
            Dim vm = GetVm()
            If item Is Nothing OrElse vm Is Nothing Then Return

            Dim kind = If(control?.Tag, "").ToString()
            item.HoveredMetadataKind = kind
            Select Case kind
                Case "Exif"
                    vm.HoveredMetadataTitle = "EXIF"
                    vm.HoveredMetadataText = item.ExifMetadataSummary
                Case "Iptc"
                    vm.HoveredMetadataTitle = "IPTC"
                    vm.HoveredMetadataText = item.IptcMetadataSummary
                Case "Xmp"
                    vm.HoveredMetadataTitle = "XMP"
                    vm.HoveredMetadataText = item.XmpMetadataSummary
                Case "Icc"
                    vm.HoveredMetadataTitle = "ICC"
                    vm.HoveredMetadataText = item.IccMetadataSummary
                Case Else
                    vm.HoveredMetadataTitle = ""
                    vm.HoveredMetadataText = ""
            End Select
        End Sub

        Public Sub OnMetadataBadgePointerExited(sender As Object, e As PointerEventArgs)
            Dim control = TryCast(sender, Control)
            Dim item = TryCast(control?.DataContext, ImageItem)
            Dim vm = GetVm()
            If item IsNot Nothing Then item.HoveredMetadataKind = ""
            If vm IsNot Nothing Then
                vm.HoveredMetadataTitle = ""
                vm.HoveredMetadataText = ""
            End If
        End Sub

        Private Sub ApplyPointerSelection(vm As GalleryViewModel, item As ImageItem, modifiers As KeyModifiers)
            If item Is Nothing OrElse item.IsParentFolderEntry Then Return
            If modifiers.HasFlag(KeyModifiers.Shift) Then
                vm.SelectRange(_selectionAnchor, item)
            ElseIf PlatformShortcutService.HasSelectionModifier(modifiers) Then
                vm.ToggleSelection(item)
                _selectionAnchor = item
            Else
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
        End Sub

        Public Async Sub OnThumbnailPointerMoved(sender As Object, e As PointerEventArgs)
            ' Kein zweiter Zug, solange einer laeuft. Die Ziehquelle des Fenstersystems ist ein
            ' EINZELNER Haken am Ereignisverteiler und ein einzelner Zeigergriff; ein zweiter Zug
            ' daneben nimmt dem ersten die Ereignisse weg, und beide warten dann auf etwas, das
            ' nicht mehr kommt.
            If _isDragging Then Return
            If _dragStartItem Is Nothing OrElse Not e.GetCurrentPoint(Nothing).Properties.IsLeftButtonPressed Then Return
            Dim delta = e.GetPosition(Me) - _dragStartPoint
            If Math.Abs(delta.X) < 6 AndAlso Math.Abs(delta.Y) < 6 Then Return

            Dim vm = GetVm()
            If vm Is Nothing Then Return
            Dim dragItem = _dragStartItem
            Dim pressedArgs = _dragStartArgs
            If dragItem Is Nothing OrElse pressedArgs Is Nothing Then Return
            Dim useSelection = vm.SelectedItems IsNot Nothing AndAlso vm.SelectedItems.Contains(dragItem)
            Dim dragItems = If(useSelection,
                               vm.SelectedItems.ToList(),
                               New List(Of ImageItem) From {dragItem})
            _dragStartItem = Nothing
            _dragStartArgs = Nothing

            ' SERVERBILDER ZIEHEN IHREN PSEUDO-PFAD, keine Datei.
            '
            ' Vorher holte diese Stelle für jedes Immich-Asset das ORIGINAL in eine Temp-Datei und
            ' legte deren Pfad in die Ziehlast. Zwei Schäden auf einmal (Nutzerbefund 2026-08-10:
            ' "CPU geht hoch, App nicht mehr bedienbar"):
            '
            ' 1. Der Zug begann erst NACH dem Herunterladen. Bei mehreren markierten Bildern zieht
            '    man damit den ganzen Bestand über die Leitung, bevor sich überhaupt etwas bewegt -
            '    und weiß beim Loslassen noch gar nicht, ob das Ziel eine Datei braucht.
            ' 2. Beim Ablegen auf einem Album kam ein DATEIPFAD an, kein Pseudo-Pfad. Der Drop hielt
            '    das für "lokale Datei" und LUD SIE ERNEUT HOCH, statt das vorhandene Asset dem
            '    Album zuzuordnen: aus einem Zuordnen wurde ein Download samt Upload samt Warten auf
            '    das Vorschaubild, je Bild.
            '
            ' Was für ein fremdes Ziel wie Dolphin fehlt, ist die Datei. Dafür gibt es "Exportieren
            ' nach" und Kopieren; ein Zug aus der Galerie meint innerhalb der Anwendung fast immer
            ' die Zuordnung, und dafür ist der Pseudo-Pfad genau das Richtige.
            Dim paths As New List(Of String)()
            Dim hatServerbilder = False
            For Each it In dragItems
                If it Is Nothing OrElse String.IsNullOrEmpty(it.FilePath) Then Continue For
                If it.IsRemoteAsset Then hatServerbilder = True
                paths.Add(it.FilePath)
            Next
            If paths.Count = 0 Then Return

            ' Die Ziehlast trägt das anwendungseigene Format, an dem der interne Drop erkennt, was
            ' gemeint ist. Die Dateien selbst kommen nur dazu, wenn es welche GIBT - ein fremdes Ziel
            ' sieht sonst nichts und lehnt den Drop ab, aber einen Pseudo-Pfad als Datei anzubieten
            ' wäre ein Versprechen, das niemand einlösen kann.
            Dim data As DataTransfer = Nothing
            If Not hatServerbilder Then
                Dim storageProvider = TopLevel.GetTopLevel(Me)?.StorageProvider
                data = Await ClipboardPathService.BuildFileTransferAsync(storageProvider, paths,
                    Sub(firstItem) firstItem.Set(FerrumPixPathsFormat, String.Join(ControlChars.Lf, paths)))
            End If
            If data Is Nothing OrElse data.Items.Count = 0 Then
                data = New DataTransfer()
                data.Add(DataTransferItem.Create(FerrumPixPathsFormat, String.Join(ControlChars.Lf, paths)))
            End If

            If Not _isAttached OrElse TopLevel.GetTopLevel(Me) Is Nothing Then Return

            ' RoutedEventArgs.Source ist nach dem Ende der PointerPressed-Route nicht garantiert
            ' erhalten. Der X11-Drag-Backend ermittelt daraus aber unmittelbar das Quellfenster und
            ' wirft bei Nothing/einem inzwischen virtualisierten Thumbnail "Invalid drag source".
            ' Die weiterhin sichtbare GalleryView ist derselben TopLevel zugeordnet und damit die
            ' stabile, plattformuebergreifende Quelle fuer die bereits validierte Press-Geste.
            pressedArgs.Source = Me

            _isDragging = True
            ' Die Pfade IM PROZESS merken, bevor der Zug beginnt: alles, was waehrend des Ziehens
            ' wissen will, was gezogen wird, liest sie von dort statt ueber das Fenstersystem.
            DragPayloadCache.BeginDrag(paths)
            DragTrace.Begin(If(hatServerbilder, "Server", "lokal"), paths.Count, data.Items.Count > 1)
            Try
                Await DragDrop.DoDragDropAsync(pressedArgs, data, DragDropEffects.Move Or DragDropEffects.Copy)
            Catch ex As ArgumentOutOfRangeException When String.Equals(ex.ParamName, "triggerEvent", StringComparison.Ordinal)
                ' Ein gleichzeitig stattfindender View-/Fensterwechsel darf eine asynchrone
                ' Drag-Geste abbrechen, aber niemals als Async-Sub-Ausnahme die App beenden.
                DiagnosticLogService.LogException("Gallery.DragDrop.InvalidSource", ex)
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.DragDrop", ex)
            Finally
                ' IMMER zuruecknehmen: bliebe der Stand liegen, hielte die Anwendung einen fremden
                ' Zug spaeter fuer den eigenen und zoege die falschen Pfade heran.
                DragTrace.Finish("Geste beendet")
                DragPayloadCache.EndDrag()
                _isDragging = False
            End Try
        End Sub

        Public Sub OnItemsSelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            ' Absichtlich leer: die Auswahl entsteht ueber Klicks auf die Kacheln und ueber die
            ' Auswahl-Abzeichen, nicht ueber dieses Ereignis der Liste.
        End Sub

        ''' <summary>Die Schnellvorschau liegt in <see cref="QuickPreviewController"/> - derselbe Weg
        ''' wie am Filmstreifen, samt Serverbild, ueberholtem Decode und Freigabe der Bitmap.</summary>
        Private ReadOnly _quickPreview As New QuickPreviewController()

        Private Sub ShowQuickPreview(item As ImageItem)
            Dim overlay = Me.FindControl(Of Panel)("PreviewOverlay")
            Dim img = Me.FindControl(Of Avalonia.Controls.Image)("PreviewImage")
            If overlay Is Nothing OrElse img Is Nothing Then Return
            _quickPreview.Show(overlay, img, item)
            Me.Focus()
        End Sub

        Private Sub HideQuickPreview()
            _quickPreview.Hide(Me.FindControl(Of Panel)("PreviewOverlay"),
                               Me.FindControl(Of Avalonia.Controls.Image)("PreviewImage"))
        End Sub

        Public Sub OnGlobalPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            If e.InitialPressMouseButton = MouseButton.Middle Then HideQuickPreview()
        End Sub

        Public Sub OnThumbnailDoubleTapped(sender As Object, e As TappedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            Dim item = GetItemFromSender(sender)
            If item IsNot Nothing Then
                vm.SelectedItem = item
                _selectionAnchor = item
                OpenGalleryItem(item)
            End If
        End Sub

        Public Sub OnContextOpen(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not IsSingleGallerySelection(vm) Then Return
            Dim item = GetItemFromSender(sender)
            ' „Anzeigen" im Kontext- und Fussmenue, daneben steht „Bearbeiten".
            OpenGalleryItemInViewer(If(item, vm.SelectedItem))
        End Sub

        ''' <summary>Zwei markierte Bilder nebeneinander im Betrachter oeffnen. Genau ZWEI - bei einem
        ''' gibt es nichts zu vergleichen, bei mehr waere die Buehne nicht mehr lesbar.</summary>
        Public Sub OnContextCompare(sender As Object, e As RoutedEventArgs)
            GetVm()?.CompareSelectedInViewer()
        End Sub

        Public Sub OnContextEdit(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not IsSingleGallerySelection(vm) Then Return
            vm.OpenSelectedInEditor()
        End Sub

        ''' <summary>Ohne Auswahl meint beides den OFFENEN ORDNER - so bietet der Bauplan die
        ''' Eintraege auch an. Mit Auswahl geht es um das eine markierte Element.</summary>
        Public Async Sub OnContextCopyPath(sender As Object, e As RoutedEventArgs)
            ' Eine Ausnahme in einem Async Sub landet sonst beim Dispatcher und beendet den Prozess.
            ' Der Kopierhelfer faengt zwar selbst, alles davor lief aber ungeschuetzt.
            Try
                Dim vm = GetVm()
                If vm Is Nothing Then Return
                If IsSingleGallerySelection(vm) Then
                    Dim paths = vm.GetSelectedPaths()
                    If paths.Count = 0 Then Return
                    Await CopyTextToClipboardAsync(paths(0), "GalleryView.OnContextCopyPath")
                    Return
                End If
                If vm.SelectedItems IsNot Nothing AndAlso vm.SelectedItems.Count > 0 Then Return
                If String.IsNullOrEmpty(vm.CurrentFolder) Then Return
                Await CopyTextToClipboardAsync(vm.CurrentFolder, "GalleryView.OnContextCopyPath")
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryView.OnContextCopyPath", ex)
            End Try
        End Sub

        Public Sub OnContextReveal(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            ' Der Befehl faellt selbst auf den offenen Ordner zurueck, wenn nichts markiert ist.
            If vm.SelectedItems IsNot Nothing AndAlso vm.SelectedItems.Count > 1 Then Return
            vm.OpenFileManagerCommand.Execute(Nothing)
        End Sub

        Public Sub OnContextDelete(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            vm?.DeleteSelectedCommand.Execute(Nothing)
        End Sub

        ''' Ein einziger Menüpunkt für Einzel- UND Mehrfachauswahl - RenameSelectedCommand
        ''' entscheidet selbst anhand von SelectedItems.Count, ob Einzel- oder Stapel-Umbenennen
        ''' greift. Ist das rechtsgeklickte Item bereits Teil der aktuellen Mehrfachauswahl, bleibt
        ''' diese erhalten, statt sie auf das einzelne Item zurückzusetzen.
        Public Sub OnContextRename(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            Dim item = GetItemFromSender(sender)
            If item IsNot Nothing AndAlso (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            vm.RenameSelectedCommand.Execute(Nothing)
        End Sub

        Public Async Sub OnContextCopy(sender As Object, e As RoutedEventArgs)
            Dim item = GetItemFromSender(sender)
            Dim vm = GetVm()
            If item IsNot Nothing AndAlso vm IsNot Nothing AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            If vm Is Nothing Then Return

            ' Das dynamische Menü merkt beim Öffnen bereits präzise, welche Kachel bzw. Zeile es
            ' meint. Der Aufruf aus seinem Command hat keinen Sender; die globale Auswahl hier
            ' nochmals auszulesen konnte deshalb leer oder inzwischen eine andere sein.
            Dim paths = If(vm.ContextItems, Enumerable.Empty(Of ImageItem)()).
                Select(Function(i) If(i?.FilePath, "")).
                Where(Function(p) vm.CanCopyPath(p)).
                Distinct(PathIdentity.Comparer).
                ToList()
            If paths.Count = 0 Then Return

            vm.StoreClipboardPaths(paths, cut:=False)
            Try
                Dim owner = TopLevel.GetTopLevel(Me)
                Await ClipboardPathService.CopyPathsAsync(owner?.Clipboard, owner?.StorageProvider, paths, cut:=False)
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryView.OnContextCopy", ex)
            End Try
        End Sub

        Public Sub OnContextCut(sender As Object, e As RoutedEventArgs)
            Dim item = GetItemFromSender(sender)
            Dim vm = GetVm()
            If item IsNot Nothing AndAlso vm IsNot Nothing AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            CopySelectionToClipboard(True)
        End Sub

        Public Async Sub OnContextPaste(sender As Object, e As RoutedEventArgs)
            Try
                Dim item = GetItemFromSender(sender)
                Dim targetFolder = If(item IsNot Nothing AndAlso item.IsFolder, item.FilePath, GetVm()?.CurrentFolder)
                Await PasteClipboardIntoFolder(targetFolder)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnContextPaste", ex)
            End Try
        End Sub

        Public Sub OnContextDuplicate(sender As Object, e As RoutedEventArgs)
            GetVm()?.DuplicateSelectedCommand.Execute(Nothing)
        End Sub

        Public Sub OnContextApplyFilter(sender As Object, e As RoutedEventArgs)
            Dim item = GetItemFromSender(sender)
            Dim vm = GetVm()
            If item IsNot Nothing AndAlso vm IsNot Nothing AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            vm?.ApplyFilterSelectedCommand.Execute(Nothing)
        End Sub

        Public Sub OnContextBatchConvert(sender As Object, e As RoutedEventArgs)
            Dim item = GetItemFromSender(sender)
            Dim vm = GetVm()
            If item IsNot Nothing AndAlso vm IsNot Nothing AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            vm?.BatchConvertSelectedCommand.Execute(Nothing)
        End Sub

        Public Sub OnContextExportTo(sender As Object, e As RoutedEventArgs)
            Dim item = GetItemFromSender(sender)
            Dim vm = GetVm()
            If item IsNot Nothing AndAlso vm IsNot Nothing AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            vm?.ExportSelectedCommand.Execute(Nothing)
        End Sub

        Public Sub OnContextResize(sender As Object, e As RoutedEventArgs)
            Dim item = GetItemFromSender(sender)
            Dim vm = GetVm()
            If item IsNot Nothing AndAlso vm IsNot Nothing AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            vm?.ResizeSelectedCommand.Execute(Nothing)
        End Sub

        Public Sub OnContextApplyWatermark(sender As Object, e As RoutedEventArgs)
            Dim item = GetItemFromSender(sender)
            Dim vm = GetVm()
            If item IsNot Nothing AndAlso vm IsNot Nothing AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            vm?.ApplyWatermarkSelectedCommand.Execute(Nothing)
        End Sub

        Public Sub OnContextRemoveMetadata(sender As Object, e As RoutedEventArgs)
            Dim item = GetItemFromSender(sender)
            Dim vm = GetVm()
            If item IsNot Nothing AndAlso vm IsNot Nothing AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            vm?.RemoveMetadataSelectedCommand.Execute(Nothing)
        End Sub

        Public Sub OnContextPrint(sender As Object, e As RoutedEventArgs)
            Dim item = GetItemFromSender(sender)
            Dim vm = GetVm()
            If item IsNot Nothing AndAlso vm IsNot Nothing AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            vm?.PrintSelectedCommand.Execute(Nothing)
        End Sub

        Public Sub OnContextCreateCollage(sender As Object, e As RoutedEventArgs)
            Dim item = GetItemFromSender(sender)
            Dim vm = GetVm()
            If item IsNot Nothing AndAlso vm IsNot Nothing AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            vm?.OpenCollageDialog()
        End Sub

        Public Sub OnGalleryAreaCreateFolder(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            vm.CreateFolderIn(vm.CurrentFolder)
        End Sub

        Public Async Sub OnGalleryAreaPaste(sender As Object, e As RoutedEventArgs)
            Try
                Await PasteClipboardIntoFolder(GetVm()?.CurrentFolder)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnGalleryAreaPaste", ex)
            End Try
        End Sub

        Public Sub OnFolderTreeContextRequested(sender As Object, e As ContextRequestedEventArgs)
            Dim vm = GetVm()
            Dim tree = TryCast(sender, TreeView)
            If vm Is Nothing OrElse tree Is Nothing Then Return
            Dim contextNode = If(_folderTreeContextNode, TryCast(tree.SelectedItem, FolderNode))
            _folderTreeContextNode = contextNode
            _suppressFolderTreeSelectionChange = False
            Dim path = contextNode?.FullPath
            SetMenuItemVisible("FolderTreeCreateFolderMenuItem", vm.CanPasteIntoFolder(path))
            SetMenuItemVisible("FolderTreeRenameMenuItem", vm.CanRenamePath(path))
            SetMenuItemVisible("FolderTreeCopyMenuItem", vm.CanCopyPath(path))
            SetMenuItemVisible("FolderTreeCutMenuItem", vm.CanCutPath(path))
            SetMenuItemVisible("FolderTreePasteMenuItem", vm.CanPasteIntoFolder(path))
            SetMenuItemVisible("FolderTreeDeleteMenuItem", vm.CanDeletePath(path))
        End Sub

        ' ── Favoriten ────────────────────────────────────────────────────────────

        ''' <summary>"Als Favorit" im Ordnerbaum - nimmt den Knoten unter dem Rechtsklick
        ''' (GetFolderTreeContextNode faellt auf den markierten Ordner zurueck).</summary>
        Public Sub OnAddFolderFavoriteClick(sender As Object, e As RoutedEventArgs)
            Dim node = GetFolderTreeContextNode()
            If node Is Nothing Then Return
            GetVm()?.AddFolderFavorite(node.FullPath)
        End Sub

        ''' <summary>Ordnerpfad eines Ordner-Favoriten aus dem angeklickten Menuepunkt. Die
        ''' Ordner-Aktionen arbeiten alle mit PFADEN, nicht mit FolderNode-Objekten - der Favorit
        ''' braucht deshalb keinen Umweg ueber den (womoeglich noch gar nicht geladenen) Ordnerbaum.</summary>
        Private Function GetFavoriteFolderPath(sender As Object) As String
            Dim node = GetVirtualNodeFromSender(sender)
            If node Is Nothing OrElse Not node.IsFolderFavorite Then Return Nothing
            Dim path = node.RootFolder
            Return If(String.IsNullOrWhiteSpace(path), Nothing, path)
        End Function

        Public Sub OnFavoriteCreateFolderClick(sender As Object, e As RoutedEventArgs)
            Dim path = GetFavoriteFolderPath(sender)
            If path Is Nothing Then Return
            GetVm()?.CreateFolderIn(path)
        End Sub

        Public Sub OnFavoriteRenameFolderClick(sender As Object, e As RoutedEventArgs)
            Dim path = GetFavoriteFolderPath(sender)
            If path Is Nothing Then Return
            GetVm()?.RenamePath(path)
        End Sub

        Public Sub OnFavoriteCopyFolderClick(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            Dim path = GetFavoriteFolderPath(sender)
            If vm Is Nothing OrElse path Is Nothing Then Return
            vm.StoreClipboardPaths({path}, False)
            CopyPathsToClipboard(New List(Of String) From {path}, False)
        End Sub

        Public Sub OnFavoriteCutFolderClick(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            Dim path = GetFavoriteFolderPath(sender)
            If vm Is Nothing OrElse path Is Nothing Then Return
            vm.StoreClipboardPaths({path}, True)
            CopyPathsToClipboard(New List(Of String) From {path}, True)
        End Sub

        Public Async Sub OnFavoritePasteFolderClick(sender As Object, e As RoutedEventArgs)
            Try
                Dim path = GetFavoriteFolderPath(sender)
                If path Is Nothing Then Return
                Await PasteClipboardIntoFolder(path)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnFavoritePasteFolderClick", ex)
            End Try
        End Sub

        Public Async Sub OnFavoriteCopyFolderPathClick(sender As Object, e As RoutedEventArgs)
            Try
                Dim path = GetFavoriteFolderPath(sender)
                If path Is Nothing Then Return
                Await CopyTextToClipboardAsync(path, "GalleryView.OnFavoriteCopyFolderPathClick")
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnFavoriteCopyFolderPathClick", ex)
            End Try
        End Sub

        ''' <summary>Loescht den ORDNER (nicht nur den Favoriten) - wie im Ordnerbaum. Der Favorit
        ''' bleibt danach als "fehlt"-Eintrag stehen, bis er entfernt wird.</summary>
        Public Sub OnFavoriteDeleteFolderClick(sender As Object, e As RoutedEventArgs)
            Dim path = GetFavoriteFolderPath(sender)
            If path Is Nothing Then Return
            GetVm()?.DeletePaths({path})
        End Sub

        ''' <summary>"Als Favorit" im Immich- oder Suchbaum.</summary>
        Public Sub OnAddNodeFavoriteClick(sender As Object, e As RoutedEventArgs)
            Dim node = GetVirtualNodeFromSender(sender)
            If node Is Nothing Then Return
            GetVm()?.AddNodeFavorite(node)
        End Sub

        Public Sub OnRemoveFavoriteClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Dim node = GetVirtualNodeFromSender(sender)
            If node Is Nothing Then Return
            GetVm()?.RemoveFavorite(node)
        End Sub


        Private Function GetFolderTreeContextNode() As FolderNode
            Return If(_folderTreeContextNode, GetVm()?.SelectedFolderNode)
        End Function



        ''' Der Menü-Button der Werkzeugleiste zeigt dasselbe Menü wie ein Rechtsklick auf das Bild - viele
        ''' Funktionen stecken inzwischen nur dort. Anders als in den Kachel-/Listen-Vorlagen ist der
        ''' DataContext hier das ViewModel, nicht das Bild: die IsVisible-Bindungen der Einträge
        ''' (CanEditFile, CanFileOperationCopy, ...) hängen aber am Bild, deshalb wird er vor dem Öffnen
        ''' auf das ausgewählte Element gesetzt.
        ''' <summary>Neues Bild und Vollbild. Bewusst ueber Click und nicht ueber eine Bindung:
        ''' alle uebrigen Eintraege dieses Menues arbeiten so, und im Popup ist nicht garantiert,
        ''' dass der DataContext ankommt - ein leeres Kommando faerbt den Eintrag stumm grau.</summary>
        Private Sub OnContextNewDocument(sender As Object, e As RoutedEventArgs)
            GetVm()?.NewDocumentCommand.Execute(Nothing)
        End Sub

        Private Sub OnContextToggleFullscreen(sender As Object, e As RoutedEventArgs)
            GetVm()?.ToggleFullscreenCommand.Execute(Nothing)
        End Sub

        ''' <summary>Das Kommando-Buendel fuer das Kontextmenue. Es entsteht HIER und nicht im
        ''' ViewModel, weil ein grosser Teil der Galerie-Aktionen an der View haengt: Zwischenablage,
        ''' Ordner anlegen, Vergleichen und das Farbetikett arbeiten mit dem Fensterrahmen.
        '''
        ''' Die vorhandenen Behandler werden dabei WIEDERVERWENDET statt nachgebaut - sie sind
        ''' erprobt, und zwei Umsetzungen derselben Aktion liefen hier schon einmal auseinander.</summary>
        Private Function BuildContextCommands() As MenuCommands
            Dim vm = GetVm()
            If vm Is Nothing Then Return Nothing
            Return New MenuCommands With {
                .NewImage = vm.NewDocumentCommand,
                .Fullscreen = vm.ToggleFullscreenCommand,
                .ShowImage = New DelegateCommand(Sub() OnContextOpen(Nothing, Nothing)),
                .Adjust = New DelegateCommand(Sub() OnContextEdit(Nothing, Nothing)),
                .Compare = New DelegateCommand(Sub() OnContextCompare(Nothing, Nothing)),
                .NewFolder = New DelegateCommand(Sub() OnGalleryAreaCreateFolder(Nothing, Nothing)),
                .Rename = vm.RenameSelectedCommand,
                .Copy = New DelegateCommand(Sub() OnContextCopy(Nothing, Nothing)),
                .Cut = New DelegateCommand(Sub() OnContextCut(Nothing, Nothing)),
                .Paste = New DelegateCommand(Sub() OnContextPaste(Nothing, Nothing)),
                .Duplicate = vm.DuplicateSelectedCommand,
                .ResizeImage = vm.ResizeSelectedCommand,
                .ApplyWatermark = vm.ApplyWatermarkSelectedCommand,
                .ApplyFilter = vm.ApplyFilterSelectedCommand,
                .ConvertTo = vm.BatchConvertSelectedCommand,
                .ExportTo = vm.ExportSelectedCommand,
                .RemoveMetadata = vm.RemoveMetadataSelectedCommand,
                .CreateCollage = New DelegateCommand(Sub() OnContextCreateCollage(Nothing, Nothing)),
                .Print = vm.PrintSelectedCommand,
                .Favorite = vm.ToggleSelectedFavoriteCommand,
                .Rating = vm.SetSelectedRatingCommand,
                .ColorLabel = New DelegateCommand(Sub(color) ApplyColorLabel(vm, color)),
                .CopyPlace = vm.CopyPlaceCommand,
                .OpenPlaceInOsm = vm.OpenPlaceInOsmCommand,
                .PastePlace = vm.PastePlaceCommand,
                .SetPlace = vm.SetPlaceCommand,
                .RemovePlace = vm.RemovePlaceCommand,
                .SetCopyright = vm.SetCopyrightCommand,
                .SetCaptureDate = vm.SetCaptureDateCommand,
                .CopyPath = New DelegateCommand(Sub() OnContextCopyPath(Nothing, Nothing)),
                .ShowInFileManager = vm.OpenFileManagerCommand,
                .Delete = vm.DeleteSelectedCommand,
                .RestoreFromTrash = New DelegateCommand(Sub()
                                                            Dim ignored = vm.RestoreSelectedFromTrashAsync()
                                                        End Sub)}
        End Function

        ''' <summary>Farbetikett aus dem Kontextmenue setzen.
        '''
        ''' NUR EIN Aufruf, auch bei Mehrfachauswahl: SetItemColorLabel nimmt sich die ganze Auswahl
        ''' selbst vor und SCHALTET dabei um - dieselbe Farbe noch einmal nimmt sie weg. Eine
        ''' Schleife darueber setzte und entfernte das Etikett abwechselnd, bei gerader Anzahl
        ''' markierter Bilder blieb am Ende alles beim Alten.</summary>
        Private Shared Sub ApplyColorLabel(vm As GalleryViewModel, color As Object)
            If vm Is Nothing Then Return
            Dim first = vm.ContextItems?.FirstOrDefault(Function(i) i IsNot Nothing AndAlso i.IsImage)
            If first Is Nothing Then Return
            vm.SetItemColorLabel(first, If(color, "").ToString())
        End Sub

        ''' <summary>Rechtsklick irgendwo in der Galerie. EIN Handler fuer Kachel, Zeile und
        ''' Fusszeile. Welche Elemente gemeint sind, beantwortet <see cref="ContextTarget"/>.</summary>
        Private Sub OnContextRequested(sender As Object, e As Avalonia.Input.ContextRequestedEventArgs)
            If ConsumeSuppressedGalleryContextMenu(e) Then Return

            Dim vm = GetVm()
            Dim menu = Me.FindControl(Of ContextMenu)("GalleryContextMenu")
            If vm Is Nothing OrElse menu Is Nothing Then Return

            Dim grid = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
            Dim rows = Me.FindControl(Of ScrollViewer)("GalleryListScrollViewer")
            Dim hit = ContextTarget.UnderPointer(e, grid)
            Dim fromGrid = hit IsNot Nothing
            If hit Is Nothing Then hit = ContextTarget.UnderPointer(e, rows)

            ' Bisheriges Verhalten beibehalten: ein Rechtsklick auf ein Element AUSSERHALB der
            ' Auswahl macht es zur Auswahl. Das sieht der Nutzer, und es entscheidet danach, worauf
            ' die Stapelaktionen wirken.
            If hit IsNot Nothing AndAlso Not hit.IsParentFolderEntry AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(hit)) Then
                vm.SelectOnly(hit)
                _selectionAnchor = hit
            End If

            ' Rechtsklick in den LEEREN Bereich: die Auswahl bleibt bestehen, das Menue meint aber
            ' nicht sie, sondern den Ordner. Sonst zeigte ein Klick ins Nichts alle Stapelaktionen
            ' fuer Bilder, die man gar nicht angeklickt hat. Uebrig bleiben die Eintraege, die ohne
            ' Bild auskommen - Neuer Ordner, Einfuegen, Neues Bild, Vollbild.
            Dim inEmptyArea = hit Is Nothing AndAlso
                              (PointerIsWithin(e, grid) OrElse PointerIsWithin(e, rows))

            _contextMenuItem = hit
            vm.ContextSite = If(hit Is Nothing AndAlso Not inEmptyArea, MenuSite.GalleryFooter,
                                If(fromGrid OrElse Not vm.IsListView, MenuSite.GalleryTile, MenuSite.GalleryRow))
            vm.ContextItems = If(inEmptyArea, New List(Of ImageItem)(),
                                 ContextTarget.Affected(hit, vm.SelectedItems, Nothing))
            vm.ContextCommands = BuildContextCommands()
            vm.RefreshContextActions()

            ' NICHT selbst oeffnen und NICHT als behandelt melden: das Menue haengt am selben
            ' Element und oeffnet sich gleich nach diesem Handler von allein - siehe
            ' ContextMenuAttachment. Die Lage muss zurueckgesetzt werden, weil die Schaltflaeche
            ' in der Fusszeile sie vorher auf eine feste Kante gestellt haben kann.
            menu.PlacementTarget = Nothing
            menu.Placement = PlacementMode.Pointer
        End Sub

        ''' <summary>Die Schaltflaeche in der Fusszeile oeffnet DAS Menue der Ansicht, nicht ein
        ''' zweites daneben. Sie meldet nur den Aufrufort; gebaut wird wie beim Rechtsklick.</summary>
        Private Sub OnGalleryMenuButtonClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = GetVm()
            Dim menu = Me.FindControl(Of ContextMenu)("GalleryContextMenu")
            If button Is Nothing OrElse vm Is Nothing OrElse menu Is Nothing Then Return

            _contextMenuItem = If(vm.SelectedItem, vm.SelectedItems?.FirstOrDefault())
            vm.ContextSite = MenuSite.GalleryFooter
            vm.ContextItems = ContextTarget.Affected(Nothing, vm.SelectedItems, Nothing)
            vm.ContextCommands = BuildContextCommands()
            vm.RefreshContextActions()

            ' Open(control) besteht darauf, dass control genau das Element ist, an dem das Menue
            ' haengt - das ist die Wurzel, nicht die Schaltflaeche. Der parameterlose Aufruf oeffnet
            ' dort; wo es erscheint, steuert PlacementTarget.
            menu.PlacementTarget = button
            menu.Placement = PlacementMode.TopEdgeAlignedLeft
            menu.Open()
            e.Handled = True
        End Sub

        ''' <summary>Liegt der Zeiger ueber diesem Bereich? Rein geometrisch geprueft, weil die
        ''' Quelle des Ereignisses bei geschachtelten Elementen nichts darueber sagt.</summary>
        Private Shared Function PointerIsWithin(e As Avalonia.Input.ContextRequestedEventArgs, area As Control) As Boolean
            If area Is Nothing OrElse Not area.IsVisible Then Return False
            Dim pos As Avalonia.Point
            If Not e.TryGetPosition(area, pos) Then Return False
            Return pos.X >= 0 AndAlso pos.Y >= 0 AndAlso
                   pos.X <= area.Bounds.Width AndAlso pos.Y <= area.Bounds.Height
        End Function




        Private Function FindMenuControl(items As IEnumerable, name As String) As Control
            If items Is Nothing Then Return Nothing
            For Each entry In items
                Dim control = TryCast(entry, Control)
                If control IsNot Nothing AndAlso String.Equals(control.Name, name, StringComparison.Ordinal) Then Return control

                Dim menuItem = TryCast(entry, MenuItem)
                If menuItem IsNot Nothing Then
                    Dim child = FindMenuControl(menuItem.Items, name)
                    If child IsNot Nothing Then Return child
                End If
            Next
            Return Nothing
        End Function

        Private Function IsSingleGallerySelection(vm As GalleryViewModel) As Boolean
            Return vm IsNot Nothing AndAlso vm.SelectedItems IsNot Nothing AndAlso vm.SelectedItems.Count = 1
        End Function

        Private Function HasImageItemContext(source As Object) As Boolean
            Dim ctrl = TryCast(source, Control)
            While ctrl IsNot Nothing
                If TypeOf ctrl.DataContext Is ImageItem Then Return True
                ctrl = TryCast(ctrl.Parent, Control)
            End While
            Return False
        End Function

        Public Sub OnContextRenameFolder(sender As Object, e As RoutedEventArgs)
            Dim node = GetFolderTreeContextNode()
            If node Is Nothing Then Return
            GetVm()?.RenamePath(node.FullPath)
        End Sub

        Public Sub OnContextCreateFolder(sender As Object, e As RoutedEventArgs)
            Dim node = GetFolderTreeContextNode()
            If node Is Nothing Then Return
            GetVm()?.CreateFolderIn(node.FullPath)
        End Sub

        Public Sub OnContextDeleteFolder(sender As Object, e As RoutedEventArgs)
            Dim node = GetFolderTreeContextNode()
            If node Is Nothing Then Return
            GetVm()?.DeletePaths({node.FullPath})
        End Sub

        Public Async Sub OnContextCopyFolderPath(sender As Object, e As RoutedEventArgs)
            Try
                Dim node = GetFolderTreeContextNode()
                If node Is Nothing Then Return
                Await CopyTextToClipboardAsync(node.FullPath, "GalleryView.OnContextCopyFolderPath")
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnContextCopyFolderPath", ex)
            End Try
        End Sub

        Public Sub OnContextCopyFolder(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            Dim node = GetFolderTreeContextNode()
            If vm Is Nothing OrElse node Is Nothing Then Return
            vm.StoreClipboardPaths({node.FullPath}, False)
            CopyPathsToClipboard(New List(Of String) From {node.FullPath}, False)
        End Sub

        Public Sub OnContextCutFolder(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            Dim node = GetFolderTreeContextNode()
            If vm Is Nothing OrElse node Is Nothing Then Return
            vm.StoreClipboardPaths({node.FullPath}, True)
            CopyPathsToClipboard(New List(Of String) From {node.FullPath}, True)
        End Sub

        Public Async Sub OnContextPasteFolder(sender As Object, e As RoutedEventArgs)
            Try
                Dim node = GetFolderTreeContextNode()
                If node Is Nothing Then Return
                Await PasteClipboardIntoFolder(node.FullPath)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnContextPasteFolder", ex)
            End Try
        End Sub

        ''' <summary>Stammt die Ziehlast von einem Server? Der Pseudo-Pfad allein reicht NICHT: eine
        ''' Ziehgeste aus der Galerie laedt Serverelemente vorher in TEMPORAERE Dateien (siehe
        ''' OnItemsDragStart) und traegt danach nur noch deren echte Pfade. Ohne die Temp-Pruefung
        ''' fiel so ein Drop in den "intern = verschieben"-Zweig, und Verschieben ist auf Temp-Dateien
        ''' nicht erlaubt (FileOperationPolicy.CanMove verlangt einen Pfad im persoenlichen Ordner).
        ''' Ergebnis: Mauszeiger "geht nicht", und Server→Ordner ging gar nicht.
        '''
        ''' Die Frage gilt BEIDEN Servern. Sie stand einmal nur fuer Immich hier, und ein
        ''' Nextcloud-Bild liess sich deshalb nicht in einen Ordner ziehen - obwohl der Weg dahinter
        ''' (PastePathsIntoFolderAsync) ihn laengst haette bedienen koennen.</summary>
        Private Shared Function PayloadHasServerAsset(payload As (Paths As List(Of String), IsInternal As Boolean)) As Boolean
            Return payload.Paths.Any(Function(p) LibraryService.IsServerPseudoPath(p) OrElse
                                                 LibraryService.IsServerTempPath(p))
        End Function

        Private Function GetDropEffects(payload As (Paths As List(Of String), IsInternal As Boolean), targetFolder As String) As DragDropEffects
            Dim vm = GetVm()
            If vm Is Nothing OrElse String.IsNullOrEmpty(targetFolder) OrElse payload.Paths.Count = 0 Then Return DragDropEffects.None
            ' Serverelemente in einen lokalen Ordner ziehen = herunterladen (Kopie), nie "verschieben".
            If PayloadHasServerAsset(payload) Then
                Return If(vm.CanPasteIntoFolder(targetFolder), DragDropEffects.Copy, DragDropEffects.None)
            End If
            If payload.IsInternal Then
                Return If(vm.CanMovePathsToFolder(payload.Paths, targetFolder), DragDropEffects.Move, DragDropEffects.None)
            End If
            Return If(vm.CanPasteIntoFolder(targetFolder), DragDropEffects.Copy, DragDropEffects.None)
        End Function

        Private Async Function ApplyDropAsync(payload As (Paths As List(Of String), IsInternal As Boolean), targetFolder As String) As Task
            Dim vm = GetVm()
            If vm Is Nothing OrElse String.IsNullOrEmpty(targetFolder) OrElse payload.Paths.Count = 0 Then Return
            If PayloadHasServerAsset(payload) Then
                Await vm.PastePathsIntoFolderAsync(payload.Paths, targetFolder, cut:=False)
            ElseIf payload.IsInternal Then
                Await vm.MovePathsToFolderAsync(payload.Paths, targetFolder)
            Else
                Await vm.PastePathsIntoFolderAsync(payload.Paths, targetFolder, cut:=False)
            End If
        End Function

        Public Sub OnFolderTreeDragOver(sender As Object, e As DragEventArgs)
            Dim target = GetDropFolder(e)
            DragTrace.Over(If(target?.Name, "kein Ordner"))
            Dim payload = GetDragPayload(e)
            ' Wie im Serverbaum: liest sich die Last waehrend der Bewegung leer (X11 reicht sie erst
            ' beim Ablegen heraus), entscheidet der ZIELORDNER. Sonst stuende ueber jedem Ordner das
            ' Verbotszeichen, obwohl der Drop danach laeuft.
            If payload.Paths.Count = 0 AndAlso target IsNot Nothing Then
                e.DragEffects = If(GetVm()?.CanPasteIntoFolder(target.FullPath), DragDropEffects.Copy, DragDropEffects.None)
            Else
                e.DragEffects = GetDropEffects(payload, target?.FullPath)
            End If
            HighlightDropRow(e, e.DragEffects <> DragDropEffects.None)
            e.Handled = True
        End Sub

        ''' <summary>Hebt die Zeile unter dem Zeiger hervor, solange dort abgelegt werden darf.
        ''' Avalonia stellt unter X11 bei anwendungsinternem Ziehen KEINE effektabhaengigen Mauszeiger dar -
        ''' gemessen: Ziel, Nutzlast und Effekt stimmen (Move bzw. Copy, beides im erlaubten
        ''' Satz), der Zeiger zeigte trotzdem durchgehend "verboten", auch mit AllowDrop direkt am
        ''' getroffenen Element. Diese Rueckmeldung liegt dafuer vollstaendig in unserer Hand.</summary>
        Private _dropHighlightRow As Control

        Private Sub HighlightDropRow(e As DragEventArgs, erlaubt As Boolean)
            ' Die Zeile kommt aus DERSELBEN Suche wie der Knoten (siehe ResolveDropSource) und wird
            ' für dasselbe Quellelement wiederverwendet. Vorher lief hier eine zweite Elternsuche je
            ' Zeigerbericht - bei hunderten Berichten je Sekunde die halbe Last umsonst.
            ResolveDropSource(e)
            Dim row As Control = If(erlaubt, _lastDropRow, Nothing)
            If Object.ReferenceEquals(row, _dropHighlightRow) Then Return
            _dropHighlightRow?.Classes.Remove("drop-target")
            _dropHighlightRow = row
            _dropHighlightRow?.Classes.Add("drop-target")
        End Sub

        Private Sub ClearDropHighlight()
            _dropHighlightRow?.Classes.Remove("drop-target")
            _dropHighlightRow = Nothing
        End Sub

        Public Sub OnTreeDragLeave(sender As Object, e As RoutedEventArgs)
            ClearDropHighlight()
        End Sub

        Public Async Sub OnFolderTreeDrop(sender As Object, e As DragEventArgs)
            Try
                ClearDropHighlight()
                Dim target = GetDropFolder(e)
                If target Is Nothing Then Return
                Await ApplyDropAsync(GetDragPayload(e), target.FullPath)
                e.Handled = True
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnFolderTreeDrop", ex)
            End Try
        End Sub

        Public Sub OnItemDragOver(sender As Object, e As DragEventArgs)
            Dim item = TryCast(TryCast(sender, Border)?.DataContext, ImageItem)
            DragTrace.Over("Kachel")
            Dim targetFolder = If(item IsNot Nothing AndAlso item.IsFolder, item.FilePath, Nothing)
            e.DragEffects = GetDropEffects(GetDragPayload(e), targetFolder)
            e.Handled = True
        End Sub

        Public Async Sub OnItemDrop(sender As Object, e As DragEventArgs)
            Try
                Dim item = TryCast(TryCast(sender, Border)?.DataContext, ImageItem)
                If item Is Nothing OrElse Not item.IsFolder Then Return
                Await ApplyDropAsync(GetDragPayload(e), item.FilePath)
                e.Handled = True
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnItemDrop", ex)
            End Try
        End Sub

        ''' Ablegen auf der freien Fläche der Galerie: fremde Dateien landen im gerade angezeigten Ordner.
        ''' Für eine Ziehgeste aus der Galerie selbst ergibt das nichts - die Dateien liegen schon dort.
        Public Sub OnGalleryAreaDragOver(sender As Object, e As DragEventArgs)
            DragTrace.Over("Galeriefläche")
            Dim payload = GetDragPayload(e)
            Dim vm = GetVm()
            ' Steht gerade eine Immich-Ansicht (Album oder „Alle Fotos") offen, landen fremde Dateien
            ' als Upload dort - genau wie beim Ablegen auf dem Baumknoten. Keine Immich-Pseudo-Pfade.
            If Not payload.IsInternal AndAlso IsImmichAlbumView(vm) AndAlso
               payload.Paths.Any(Function(p) Not ImmichService.IsImmichPseudoPath(p)) Then
                e.DragEffects = DragDropEffects.Copy
            ElseIf payload.IsInternal Then
                e.DragEffects = DragDropEffects.None
            Else
                e.DragEffects = GetDropEffects(payload, vm?.CurrentFolder)
            End If
            e.Handled = True
        End Sub

        Public Async Sub OnGalleryAreaDrop(sender As Object, e As DragEventArgs)
            Try
                Dim payload = GetDragPayload(e)
                If payload.IsInternal Then Return
                Dim vm = GetVm()
                If IsImmichAlbumView(vm) Then
                    Dim immichPaths = payload.Paths.Where(Function(p) Not ImmichService.IsImmichPseudoPath(p) AndAlso IO.File.Exists(p)).ToList()
                    e.Handled = True
                    If immichPaths.Count > 0 Then vm.UploadToImmich(vm.SelectedImmichNode, immichPaths)
                    Return
                End If
                Await ApplyDropAsync(payload, vm?.CurrentFolder)
                e.Handled = True
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.OnGalleryAreaDrop", ex)
            End Try
        End Sub

        ''' <summary>True, wenn die Galerie gerade eine Immich-Ansicht (Album oder „Alle Fotos") zeigt -
        ''' dann sind Drops fremder Dateien Uploads nach Immich statt Kopien in einen lokalen Ordner.</summary>
        Private Shared Function IsImmichAlbumView(vm As GalleryViewModel) As Boolean
            Return vm IsNot Nothing AndAlso vm.IsVirtualFolder AndAlso
                   vm.SelectedImmichNode IsNot Nothing AndAlso vm.SelectedImmichNode.IsImmichNode
        End Function

        ''' <summary>Ordnerknoten unter dem Zeiger. Wie GetImmichDropNode erst der LOGISCHE, dann der
        ''' VISUELLE Elternpfad: bei Inhalten aus einem ItemTemplate reisst die logische Kette ab, und der
        ''' Rueckfall unten liefert dann den MARKIERTEN statt des ueberflogenen Ordners - der Mauszeiger
        ''' beschriebe also einen anderen Ordner als den, auf dem man steht.</summary>
        Private Function GetDropFolder(e As DragEventArgs) As FolderNode
            Dim current = TryCast(e.Source, Control)
            Dim depth = 0
            While current IsNot Nothing
                Dim node = TryCast(current.DataContext, FolderNode)
                If node IsNot Nothing Then Return node
                ' Dieselbe harte Grenze wie in ResolveDropSource: die Mischung aus logischer und
                ' visueller Kette kann einen Ring bilden, und der haelt den UI-Faden fuer immer.
                depth += 1
                If depth > 64 Then
                    DiagnosticLogService.LogAlways("Drag", "Ordner-Elternpfad tiefer als 64 Stufen - abgebrochen (Ring?)")
                    Exit While
                End If
                Dim logicalParent = TryCast(current.Parent, Control)
                Dim nextParent = If(logicalParent, current.GetVisualParent(Of Control)())
                If Object.ReferenceEquals(nextParent, current) Then Exit While
                current = nextParent
            End While
            Return GetVm()?.SelectedFolderNode
        End Function

        ''' Die Ziehlast kommt entweder aus der Galerie selbst (dann verschieben wir) oder aus einem fremden
        ''' Dateimanager (dann kopieren wir - dessen Dateien liegen woanders und sollen dort bleiben).
        Private Function GetDragPayload(e As DragEventArgs) As (Paths As List(Of String), IsInternal As Boolean)
            ' ZIEHT DIE ANWENDUNG SELBST, kommt die Antwort aus dem eigenen Gedaechtnis. Das Lesen
            ' ueber das Fenstersystem blockiert unter X11 den Faden, auf dem die QUELLE antworten
            ' muesste - bei jedem Zeigerbericht neu (siehe DragPayloadCache).
            If DragPayloadCache.IsDragging Then
                Dim eigene = DragPayloadCache.Paths()
                If eigene.Count > 0 Then Return (eigene, True)
            End If

            ' Ab hier geht es über das FENSTERSYSTEM, und genau das ist der Schritt, der stehen
            ' bleiben kann. Beide Aufrufe stehen deshalb zwischen zwei Protokollzeilen: fehlt die
            ' zweite, war hier Schluss (siehe DragTrace).
            Try
                Dim internal = DragTrace.Measure("eigenes Format", Function() e.DataTransfer.TryGetValue(FerrumPixPathsFormat),
                                                 Function(wert) If(String.IsNullOrEmpty(wert), 0, 1))
                If Not String.IsNullOrWhiteSpace(internal) Then
                    Dim internalPaths = internal.
                        Split({ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries).
                        Where(Function(p) Not String.IsNullOrEmpty(p)).
                        Distinct(PathIdentity.Comparer).
                        ToList()
                    If internalPaths.Count > 0 Then Return (internalPaths, True)
                End If
            Catch
            End Try

            Try
                Dim files = DragTrace.Measure("Dateiliste", Function() e.DataTransfer.TryGetFiles(),
                                              Function(liste) If(liste Is Nothing, 0, liste.Count))
                If files IsNot Nothing Then
                    Dim externalPaths = ClipboardPathService.ToLocalPaths(files)
                    If externalPaths.Count > 0 Then Return (externalPaths, False)
                End If
            Catch
            End Try

            Return (New List(Of String)(), False)
        End Function

        Public Sub OnManagePeopleClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            mainVm?.OpenPeople()
            e.Handled = True
        End Sub

        Public Sub OnSettingsClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            mainVm?.OpenSettings()
            e.Handled = True
        End Sub

        Public Sub OnFullscreenClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            mainVm?.EnterFullscreen()
            e.Handled = True
        End Sub

        Public Shadows Async Sub OnKeyDown(sender As Object, e As KeyEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            If PlatformShortcutService.IsInputFieldSource(e.Source) Then Return

            If PlatformShortcutService.HasPrimaryModifier(e.KeyModifiers) Then
                Select Case e.Key
                Case Key.A
                    vm.SelectAllVisible()
                    e.Handled = True
                    Return
                    Case Key.C
                        CopySelectionToClipboard(False)
                        e.Handled = True
                        Return
                    Case Key.X
                        CopySelectionToClipboard(True)
                        e.Handled = True
                        Return
                    ' e.Handled vor dem Await - sonst läuft die Weiterleitung mit einem scheinbar
                    ' unbehandelten Ereignis weiter (siehe MainWindow.OnWindowKeyDown).
                    Case Key.V
                        e.Handled = True
                        ' Der einzige Await in diesem Ereignis. Ohne diese Grenze waere eine
                        ' Ausnahme daraus eine unbeobachtete Aufgabe: sie kaeme nirgends an und
                        ' koennte je nach Einstellung den Prozess beenden. Async Sub hat keinen
                        ' Aufrufer, der sie auffangen koennte - die Grenze muss hier stehen.
                        Try
                            Await PasteClipboardIntoFolder(vm.CurrentFolder)
                        Catch ex As Exception
                            DiagnosticLogService.LogException("Gallery.Paste", ex)
                            vm.StatusText = LocalizationService.T("Einfügen aus der Zwischenablage ist fehlgeschlagen")
                        End Try
                        Return
                    Case Key.F
                        ' Strg+F bleibt die Suche; „Filter anwenden" liegt auf Strg+W
                        ' (Strg+F war schon belegt).
                        FocusSearchBox()
                        e.Handled = True
                        Return
                End Select
            End If

            ' Strg+W (Filter), Strg+D (Konvertieren) und Strg+T (Exportieren) liegen im
            ' Fenster-Tunnel, nicht mehr hier: sie gelten in Galerie UND Betrachter, und im Tunnel
            ' greifen sie unabhaengig vom Fokus, im Vollbild und nach einem Overlay-Dialog. Zwei
            ' Stellen fuer dasselbe Kuerzel waeren eine doppelte Ausfuehrung.

            Select Case e.Key
                Case Key.Return, Key.Enter
                    If vm.SelectedItem IsNot Nothing Then
                        OpenGalleryItem(vm.SelectedItem)
                        e.Handled = True
                    End If
                Case Key.Space
                    If _spaceOverviewActive Then
                        e.Handled = True
                        Return
                    End If
                    If vm.SelectedItem IsNot Nothing AndAlso vm.SelectedItem.IsImage Then
                        _spaceOverviewActive = True
                        ShowQuickPreview(vm.SelectedItem)
                        e.Handled = True
                    End If
                Case Key.Right, Key.Down
                    HandleKeyboardNavigation(vm, GetNavigationOffset(e.Key), e.KeyModifiers)
                    e.Handled = True
                Case Key.Left, Key.Up
                    HandleKeyboardNavigation(vm, GetNavigationOffset(e.Key), e.KeyModifiers)
                    e.Handled = True
                Case Key.PageDown
                    HandleKeyboardNavigation(vm, ClampNavigationOffset(vm, GetPageOffset(1)), e.KeyModifiers)
                    e.Handled = True
                Case Key.PageUp
                    HandleKeyboardNavigation(vm, ClampNavigationOffset(vm, GetPageOffset(-1)), e.KeyModifiers)
                    e.Handled = True
                Case Key.Home
                    HandleHomeEndNavigation(vm, toLast:=False, modifiers:=e.KeyModifiers)
                    e.Handled = True
                Case Key.End
                    HandleHomeEndNavigation(vm, toLast:=True, modifiers:=e.KeyModifiers)
                    e.Handled = True
                Case Key.Delete
                    vm.DeleteSelectedCommand.Execute(Nothing)
                    e.Handled = True
                Case Key.F2
                    vm.RenameSelectedCommand.Execute(Nothing)
                    e.Handled = True
                Case Key.F5
                    vm.RefreshCommand.Execute(Nothing)
                    e.Handled = True
                Case Key.Escape
                    vm.SelectedItem = Nothing
                    vm.ReplaceSelection(Enumerable.Empty(Of ImageItem)())
                    _selectionAnchor = Nothing
                    e.Handled = True
                Case Key.F3, Key.F7
                    FocusSearchBox()
                    e.Handled = True
            End Select
        End Sub

        Private Sub FocusSearchBox()
            Dim searchBox = Me.FindControl(Of TextBox)("GallerySearchBox")
            If searchBox Is Nothing Then Return
            searchBox.Focus()
            searchBox.SelectAll()
        End Sub

        Public Sub OnSearchBoxKeyDown(sender As Object, e As KeyEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return

            Select Case e.Key
                Case Key.Escape
                    vm.SearchText = ""
                    Me.Focus()
                    e.Handled = True
                Case Key.Tab
                    Me.Focus()
                    e.Handled = True
            End Select
        End Sub

        Public Sub HandleKeyUp(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Space OrElse Not _spaceOverviewActive Then Return
            HideQuickPreview()
            _spaceOverviewActive = False
            e.Handled = True
        End Sub

        Private Sub CopySelectionToClipboard(cut As Boolean)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            vm.StoreClipboard(cut)

            Dim paths = If(vm.SelectedItems IsNot Nothing AndAlso vm.SelectedItems.Count > 0,
                           vm.SelectedItems.Select(Function(i) i.FilePath).Where(Function(p) Not String.IsNullOrEmpty(p)).ToList(),
                           If(vm.SelectedItem Is Nothing, New List(Of String)(), New List(Of String) From {vm.SelectedItem.FilePath}))
            paths = paths.Where(Function(p) If(cut, vm.CanCutPath(p), vm.CanCopyPath(p))).ToList()
            If paths.Count = 0 Then Return
            CopyPathsToClipboard(paths, cut)
        End Sub

        ''' <summary>Legt einen Text in die Zwischenablage - auf dem UI-Faden, wie es sich fuer
        ''' Avalonia gehoert.
        '''
        ''' Die beiden "Ordnerpfad kopieren" liefen einmal in einem Task.Run: darin holten sie sich
        ''' das TopLevel aus dem Sichtbaum und riefen die Zwischenablage auf - beides gehoert dem
        ''' UI-Faden. Je nach Unterbau ging das gut oder gar nicht, und weil niemand den Task
        ''' abwartete, blieb ein Fehlschlag unsichtbar: der Nutzer klickte, und die Ablage blieb
        ''' leer. Die Ablage ist ohnehin nichts, worauf man wartet - sie ist in Sekundenbruchteilen
        ''' beschrieben.</summary>
        Private Async Function CopyTextToClipboardAsync(text As String, quelle As String) As Task
            Try
                If String.IsNullOrEmpty(text) Then Return
                Dim clip = TopLevel.GetTopLevel(Me)?.Clipboard
                If clip Is Nothing Then Return
                Await clip.SetTextAsync(text)
            Catch ex As Exception
                DiagnosticLogService.LogException(quelle, ex)
            End Try
        End Function

        Private Async Sub CopyPathsToClipboard(paths As List(Of String), cut As Boolean)
            Try
                Dim owner = TopLevel.GetTopLevel(Me)
                Await ClipboardPathService.CopyPathsAsync(owner?.Clipboard, owner?.StorageProvider, paths, cut)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryView.CopyPathsToClipboard", ex)
            End Try
        End Sub

        Private Async Function PasteClipboardIntoFolder(targetFolder As String) As Task
            Dim vm = GetVm()
            If vm Is Nothing OrElse String.IsNullOrEmpty(targetFolder) Then Return
            If Not vm.CanPasteIntoFolder(targetFolder) Then Return

            Dim clipboardData = Await ClipboardPathService.ReadPathDataAsync(TopLevel.GetTopLevel(Me)?.Clipboard)
            If clipboardData.Paths.Count > 0 Then
                Await vm.PastePathsIntoFolderAsync(clipboardData.Paths, targetFolder, clipboardData.IsCut)
            ElseIf clipboardData.ClipboardWasReadable Then
                Return
            Else
                Await vm.PasteIntoFolderAsync(targetFolder)
            End If
        End Function

        Private Sub HandleKeyboardNavigation(vm As GalleryViewModel, offset As Integer, modifiers As KeyModifiers)
            If modifiers.HasFlag(KeyModifiers.Shift) Then
                If _selectionAnchor Is Nothing Then _selectionAnchor = vm.SelectedItem
                vm.ExtendSelectionByOffset(_selectionAnchor, offset)
            ElseIf modifiers.HasFlag(KeyModifiers.Control) Then
                Dim focused = vm.MoveCurrentByOffset(offset)
                _selectionAnchor = focused
            Else
                vm.SelectByOffset(offset)
                _selectionAnchor = vm.SelectedItem
            End If

            ' Bild auf und Bild ab landen auf dem ersten oder letzten Element, sobald sie ueber das
            ' Listenende hinausschiessen. In dem Fall direkt an den echten Rand springen statt ueber
            ' die Faustregel "schon sichtbar" weiter unten.
            Dim landedIdx = If(vm.SelectedItem IsNot Nothing, vm.Items.IndexOf(vm.SelectedItem), -1)
            If landedIdx = 0 Then
                Dispatcher.UIThread.Post(Sub() ScrollToExtreme(toEnd:=False), DispatcherPriority.Loaded)
            ElseIf landedIdx >= 0 AndAlso landedIdx = vm.Items.Count - 1 Then
                Dispatcher.UIThread.Post(Sub() ScrollToExtreme(toEnd:=True), DispatcherPriority.Loaded)
            Else
                Dispatcher.UIThread.Post(Sub() ScrollToSelectedItem(), DispatcherPriority.Loaded)
            End If
        End Sub

        Private Function GetNavigationOffset(key As Key) As Integer
            Select Case key
                Case Key.Right
                    Return 1
                Case Key.Left
                    Return -1
                Case Key.Down
                    Return If(GetVm()?.IsGroupView, GetGroupRowOffset(1), If(GetVm()?.IsGridView, GetGridColumnCount(), 1))
                Case Key.Up
                    Return If(GetVm()?.IsGroupView, GetGroupRowOffset(-1), If(GetVm()?.IsGridView, -GetGridColumnCount(), -1))
                Case Else
                    Return 0
            End Select
        End Function

        ''' <summary>Eine Zeile auf oder ab in der Gruppenansicht. Die Spaltenzahl taugt dort nicht als
        ''' Versatz: die letzte Zeile einer Gruppe ist meist nur teilweise gefuellt, und die naechste
        ''' Zeile gehoert schon zur naechsten Gruppe.</summary>
        Private Function GetGroupRowOffset(rowDelta As Integer) As Integer
            Dim vm = GetVm()
            If vm Is Nothing OrElse vm.SelectedItem Is Nothing Then Return rowDelta
            Dim idx = vm.Items.IndexOf(vm.SelectedItem)
            If idx < 0 Then Return rowDelta

            Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
            Dim cols = 1
            Dim itemSlotHeight = 0.0
            GetGridLayoutMetrics(scrollViewer, vm, cols, itemSlotHeight, forGroupView:=True)
            Return vm.GroupRowNavigationOffset(idx, rowDelta, cols, itemSlotHeight)
        End Function

        Private Function GetGridColumnCount() As Integer
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not vm.IsTileView Then Return 1
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
            Dim cols = 1
            Dim itemSlotHeight = 0.0
            GetGridLayoutMetrics(scrollViewer, vm, cols, itemSlotHeight, forGroupView:=vm.IsGroupView)
            Return cols
        End Function

        Private Sub GetGridLayoutMetrics(scrollViewer As ScrollViewer, vm As GalleryViewModel, ByRef columns As Integer, ByRef itemSlotHeight As Double,
                                         Optional forGroupView As Boolean = False)
            columns = 1
            itemSlotHeight = If(vm IsNot Nothing, Math.Max(1, vm.GridItemSlotHeight), 1)
            If vm Is Nothing Then Return

            Dim measuredColumns = 0
            Dim measuredSlotHeight = 0.0
            Dim measured = TryGetRenderedGridMetrics(scrollViewer, measuredColumns, measuredSlotHeight, forGroupView)
            itemSlotHeight = LatchSlotHeight(vm, If(measured, measuredSlotHeight, 0.0))
            If measured AndAlso Not forGroupView Then
                columns = Math.Max(1, measuredColumns)
                Return
            End If

            Dim itemsControl = scrollViewer?.GetVisualDescendants().OfType(Of ItemsControl)().FirstOrDefault()
            Dim availableWidth = If(itemsControl IsNot Nothing AndAlso itemsControl.Bounds.Width > 0,
                                    itemsControl.Bounds.Width,
                                    If(scrollViewer IsNot Nothing AndAlso scrollViewer.Viewport.Width > 0,
                                       scrollViewer.Viewport.Width - 30,
                                       Bounds.Width - 30))
            availableWidth = Math.Max(1, availableWidth)
            Dim itemWidth = Math.Max(1, vm.GridColumnPitch)
            columns = Math.Max(1, CInt(Math.Floor(availableWidth / itemWidth)))

            ' In der Gruppenansicht kommt die Spaltenzahl IMMER aus der Breite, nie aus dem Gezeichneten:
            ' dort kann jede sichtbare Zeile eine teilweise gefuellte letzte Zeile einer Gruppe sein, und
            ' eine zu klein gemessene Spaltenzahl braecht die Zeilentabelle. Die Rechnung ist dieselbe,
            ' die auch das WrapPanel anstellt (ganze Kachelbreiten in die Zeilenbreite), also exakt.
            ' Die Zeilenhoehe steht bereits fest (LatchSlotHeight).
        End Sub

        ''' <summary>Die gemessene Zeilenhoehe halten und nur bei einer echten Aenderung uebernehmen.
        ''' Aus der Zeilenhoehe folgt die Gesamthoehe des Inhalts und damit der Scrollbereich. Wechselt
        ''' sie zwischen zwei Werten, springt am UNTEREN Ende der Scrollversatz mit: dort klemmt ihn der
        ''' ScrollViewer gegen die Gesamthoehe. Der veraenderte Versatz fuehrt zu einem anderen
        ''' Anzeigefenster, dessen Neuaufbau die naechste Messung wieder ins Leere laufen laesst (noch
        ''' keine Kachel vermessen, Rueckfall auf den Schaetzwert des ViewModels) - die Ansicht flackert
        ''' dann dauerhaft zwischen zwei Staenden. Deshalb gilt: einmal gemessen, bleibt der Wert stehen,
        ''' bis sich die Kachelgroesse aendert oder wirklich anders gemessen wird (Schriftgroesse). Ein
        ''' fehlgeschlagener Messversuch meldet 0 und aendert nichts.</summary>
        Private Function LatchSlotHeight(vm As GalleryViewModel, measuredSlotHeight As Double) As Double
            Dim estimate = Math.Max(1, vm.GridItemSlotHeight)
            If _latchedSlotThumbnailSize <> vm.ThumbnailSize Then
                _latchedSlotThumbnailSize = vm.ThumbnailSize
                _latchedSlotHeight = 0
            End If

            ' Ein Messwert weit ab vom Schaetzwert stammt aus einem halb fertigen Layout und wird
            ' verworfen; ein kleiner Unterschied zum gehaltenen Wert ist Rundung im Layout.
            If measuredSlotHeight > 0 AndAlso
               measuredSlotHeight >= estimate * 0.5 AndAlso measuredSlotHeight <= estimate * 2.0 Then
                If _latchedSlotHeight <= 0 OrElse Math.Abs(_latchedSlotHeight - measuredSlotHeight) >= 4.0 Then
                    _latchedSlotHeight = measuredSlotHeight
                End If
            End If

            Return If(_latchedSlotHeight > 0, Math.Max(1, _latchedSlotHeight), estimate)
        End Function

        ''' <summary>Spaltenzahl und Zeilenhoehe aus dem, was wirklich gezeichnet ist.
        ''' In der Gruppenansicht darf dabei NICHT die erste Zeile allein zaehlen: die letzte Zeile einer
        ''' Gruppe ist meist nur teilweise gefuellt (zu wenige Spalten). Dort gilt deshalb die BREITESTE
        ''' Zeile, und die Zeilenhoehe kommt aus der Kachel selbst statt aus einem Zeilenabstand - der
        ''' fuehrt dort in die Irre, sobald eine Kopfzeile dazwischen steht.</summary>
        Private Function TryGetRenderedGridMetrics(scrollViewer As ScrollViewer, ByRef columns As Integer, ByRef itemSlotHeight As Double,
                                                   Optional forGroupView As Boolean = False) As Boolean
            columns = 0
            itemSlotHeight = 0
            If scrollViewer Is Nothing Then Return False

            Dim thumbBorders = scrollViewer.GetVisualDescendants().
                OfType(Of Border)().
                Where(Function(b) String.Equals(b.Name, "ThumbBorder", StringComparison.Ordinal) AndAlso b.Bounds.Width > 0 AndAlso b.Bounds.Height > 0).
                Select(Function(b)
                           Dim origin = b.TranslatePoint(New Avalonia.Point(0, 0), scrollViewer)
                           If Not origin.HasValue Then Return Nothing
                           Return New With {
                               .Border = b,
                               .X = origin.Value.X,
                               .Y = origin.Value.Y
                           }
                       End Function).
                Where(Function(x) x IsNot Nothing).
                OrderBy(Function(x) x.Y).
                ThenBy(Function(x) x.X).
                ToList()
            If thumbBorders.Count = 0 Then Return False

            Const tolerance As Double = 2.0
            Dim firstRowY = thumbBorders(0).Y

            If forGroupView Then
                Dim rowCounts As New Dictionary(Of Integer, Integer)()
                For Each entry In thumbBorders
                    Dim bucket = CInt(Math.Round(entry.Y / tolerance))
                    Dim seen = 0
                    rowCounts(bucket) = If(rowCounts.TryGetValue(bucket, seen), seen + 1, 1)
                Next
                columns = Math.Max(1, rowCounts.Values.Max())

                ' Die Zeilenhoehe kommt aus der KACHEL selbst - Hoehe des Rahmens plus sein
                ' Aussenabstand -, nicht aus dem Abstand zwischen zwei gezeichneten Zeilen. Der
                ' Abstand taeuscht: stehen im Anzeigefenster keine zwei Kachelzeilen DERSELBEN Gruppe
                ' untereinander (kleine Gruppen, oder ganz unten ein kurzes Fenster), liegt zwischen
                ' je zwei Kachelzeilen eine Kopfzeile, und jeder Abstand ist um deren 48 px zu gross.
                ' Die Gesamthoehe des Inhalts sprang dadurch je nach Fensterinhalt hin und her, der
                ' Scrollbereich mit ihr, und die Ansicht flackerte unten dauerhaft zwischen zwei
                ' Staenden (Mitschrift 2026-08-12: 227,98 gegen 276,19 px - genau eine Kopfzeile).
                Dim slotFromTile = thumbBorders.
                    Select(Function(x) x.Border.Bounds.Height + x.Border.Margin.Top + x.Border.Margin.Bottom).
                    Min()
                itemSlotHeight = slotFromTile
                Return itemSlotHeight > 0
            End If

            Dim firstRow = thumbBorders.Where(Function(x) Math.Abs(x.Y - firstRowY) <= tolerance).ToList()
            columns = Math.Max(1, firstRow.Count)

            Dim nextRow = thumbBorders.FirstOrDefault(Function(x) x.Y > firstRowY + tolerance)
            If nextRow IsNot Nothing Then
                itemSlotHeight = nextRow.Y - firstRowY
            Else
                itemSlotHeight = thumbBorders(0).Border.Bounds.Height + 10.0
            End If

            Return itemSlotHeight > 0
        End Function

        Private Function GetVisibleRowCount() As Integer
            Dim vm = GetVm()
            If vm Is Nothing Then Return 1
            Dim scrollViewerName = If(vm.IsListView, "GalleryListScrollViewer", "GalleryGridScrollViewer")
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)(scrollViewerName)
            Dim viewportHeight = If(scrollViewer IsNot Nothing AndAlso scrollViewer.Viewport.Height > 0,
                                    scrollViewer.Viewport.Height,
                                    Bounds.Height)
            Dim itemHeight = 78.0
            If vm.IsTileView Then
                Dim cols = 1
                GetGridLayoutMetrics(scrollViewer, vm, cols, itemHeight, forGroupView:=vm.IsGroupView)
            End If
            Return Math.Max(1, CInt(Math.Floor(viewportHeight / itemHeight)))
        End Function

        ''' <summary>Versatz fuer eine ganze Seite. Die Richtung geht mit hinein, weil sie in der
        ''' Gruppenansicht nicht einfach umgedreht werden kann: nach oben liegen andere Gruppengrenzen
        ''' als nach unten.</summary>
        Private Function GetPageOffset(direction As Integer) As Integer
            Dim vm = GetVm()
            If vm Is Nothing Then Return direction
            Dim rows = GetVisibleRowCount()
            If vm.IsGroupView Then Return GetGroupRowOffset(rows * Math.Sign(direction))
            Return If(vm.IsGridView, rows * GetGridColumnCount(), rows) * Math.Sign(direction)
        End Function

        Private Function ClampNavigationOffset(vm As GalleryViewModel, offset As Integer) As Integer
            If vm Is Nothing OrElse vm.Items.Count = 0 OrElse offset = 0 Then Return offset
            Dim currentIndex = If(vm.SelectedItem IsNot Nothing, vm.Items.IndexOf(vm.SelectedItem), -1)
            If currentIndex < 0 Then Return offset
            If offset > 0 Then Return Math.Min(offset, vm.Items.Count - 1 - currentIndex)
            Return Math.Max(offset, -currentIndex)
        End Function

        Private Sub HandleHomeEndNavigation(vm As GalleryViewModel, toLast As Boolean, modifiers As KeyModifiers)
            If modifiers.HasFlag(KeyModifiers.Shift) Then
                If _selectionAnchor Is Nothing Then _selectionAnchor = vm.SelectedItem
                If toLast Then
                    vm.ExtendSelectionToLast(_selectionAnchor)
                Else
                    vm.ExtendSelectionToFirst(_selectionAnchor)
                End If
            ElseIf modifiers.HasFlag(KeyModifiers.Control) Then
                Dim focused = If(toLast, vm.MoveCurrentToLast(), vm.MoveCurrentToFirst())
                _selectionAnchor = focused
            Else
                If toLast Then vm.SelectLast() Else vm.SelectFirst()
                _selectionAnchor = vm.SelectedItem
            End If
            Dispatcher.UIThread.Post(Sub() ScrollToExtreme(toLast), DispatcherPriority.Loaded)
        End Sub

        ' Pos1 und Ende muessen IMMER den echten ersten bzw. letzten Bildpunkt der Liste erreichen.
        ' Deshalb umgeht das hier die Faustregel "schon sichtbar" und springt direkt auf Versatz 0
        ' oder auf den Hoechstwert.
        Private Sub ScrollToExtreme(toEnd As Boolean)
            Dim vm = GetVm()
            If vm Is Nothing OrElse vm.Items Is Nothing OrElse vm.Items.Count = 0 Then Return

            If vm.IsGroupView Then
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
                If scrollViewer Is Nothing OrElse scrollViewer.Bounds.Height <= 0 Then Return

                Dim cols = 1
                Dim itemSlotHeight = 0.0
                GetGridLayoutMetrics(scrollViewer, vm, cols, itemSlotHeight, forGroupView:=True)
                Dim viewHeight = scrollViewer.Bounds.Height
                ' Erst die Zeilentabelle bauen lassen, dann die Gesamthoehe lesen: vor dem ersten
                ' Aufbau steht sie auf 0, und der Sprung ans Ende landete im Nichts.
                vm.SetGroupDisplayWindow(0.0, viewHeight, itemSlotHeight, cols)
                If toEnd Then vm.SetGroupDisplayWindow(Math.Max(0.0, vm.ContentHeight - viewHeight), viewHeight, itemSlotHeight, cols)
                scrollViewer.UpdateLayout()
                Dim targetY = If(toEnd, Math.Max(0.0, scrollViewer.Extent.Height - viewHeight), 0.0)
                scrollViewer.Offset = New Avalonia.Vector(0, targetY)
                Return
            End If

            If vm.IsGridView Then
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
                If scrollViewer Is Nothing OrElse scrollViewer.Bounds.Height <= 0 Then Return

                Dim cols = 1
                Dim itemSlotHeight = 0.0
                GetGridLayoutMetrics(scrollViewer, vm, cols, itemSlotHeight)
                Dim totalRows = CInt(Math.Ceiling(vm.Items.Count / CDbl(cols)))
                Dim viewHeight = scrollViewer.Bounds.Height
                Dim windowRows = CInt(Math.Ceiling(viewHeight / itemSlotHeight)) + 4

                If toEnd Then
                    Dim windowFirstRow = Math.Max(0, totalRows - windowRows)
                    vm.SetDisplayWindow(windowFirstRow * cols, vm.Items.Count - 1, itemSlotHeight, cols)
                Else
                    vm.SetDisplayWindow(0, Math.Min(vm.Items.Count - 1, (windowRows * cols) - 1), itemSlotHeight, cols)
                End If
                scrollViewer.UpdateLayout()
                Dim targetY = If(toEnd, Math.Max(0.0, scrollViewer.Extent.Height - viewHeight), 0.0)
                scrollViewer.Offset = New Avalonia.Vector(0, targetY)
            Else
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryListScrollViewer")
                If scrollViewer Is Nothing OrElse scrollViewer.Bounds.Height <= 0 Then Return

                Const itemSlotHeight As Double = 78
                Dim viewHeight = scrollViewer.Bounds.Height
                Dim windowItems = CInt(Math.Ceiling(viewHeight / itemSlotHeight)) + 8

                If toEnd Then
                    Dim windowFirst = Math.Max(0, vm.Items.Count - windowItems)
                    vm.SetDisplayWindow(windowFirst, vm.Items.Count - 1, itemSlotHeight, 1)
                Else
                    vm.SetDisplayWindow(0, Math.Min(vm.Items.Count - 1, windowItems), itemSlotHeight, 1)
                End If
                scrollViewer.UpdateLayout()
                Dim targetY = If(toEnd, Math.Max(0.0, scrollViewer.Extent.Height - viewHeight), 0.0)
                scrollViewer.Offset = New Avalonia.Vector(0, targetY)
            End If
        End Sub

        ''' <summary>Der unbenannte Weg hinein: Doppelklick auf die Kachel, Eingabetaste, Play-Badge.
        ''' NUR hier gilt die Einstellung „Bild aus der Galerie öffnet mit".</summary>
        Private Sub OpenGalleryItem(item As ImageItem)
            Dim vm = GetVm()
            If vm Is Nothing OrElse item Is Nothing Then Return

            If item.IsFolder Then
                vm.NavigateToFolder(item.FilePath)
                SelectFolderInTree(item.FilePath)
            ElseIf ShouldOpenInEditor(item) Then
                vm.OpenSelectedInEditor()
            Else
                vm.OpenSelectedInViewer()
            End If
        End Sub

        ''' <summary>Der benannte Weg: „Anzeigen" im Kontext- und Fußmenü und das Auge auf der
        ''' Kachel. Der Eintrag sagt, wohin es geht, also geht es dorthin - die Einstellung hat hier
        ''' nichts zu entscheiden (Nutzerbefund 2026-08-06: beide landeten im Editor, sobald die
        ''' Einstellung auf Editor stand). Wer bearbeiten will, hat daneben „Bearbeiten".</summary>
        Private Sub OpenGalleryItemInViewer(item As ImageItem)
            Dim vm = GetVm()
            If vm Is Nothing OrElse item Is Nothing Then Return

            If item.IsFolder Then
                vm.NavigateToFolder(item.FilePath)
                SelectFolderInTree(item.FilePath)
            Else
                vm.OpenSelectedInViewer()
            End If
        End Sub

        ''' <summary>Womit ein Bild geoeffnet wird, wenn der Weg hinein nichts anderes sagt -
        ''' Doppelklick, Eingabetaste, Play-Badge. Videos gehen immer in den Betrachter: der Editor
        ''' kann sie nicht, und ein leerer Editor waere schlechter als eine Einstellung, die einmal
        ''' nicht greift.</summary>
        Private Shared Function ShouldOpenInEditor(item As ImageItem) As Boolean
            If item Is Nothing OrElse item.IsVideoFile Then Return False
            Return AppSettingsService.Load().GalleryOpenTarget = "Editor"
        End Function

        ''' Selektiert und expandiert den Ordner im Baum. Bewusst OHNE Auto-Scrollen: das Nachziehen
        ''' in die Mitte bei jeder Navigation stoerte (Nutzer-Feedback) - nur das initiale
        ''' Anzeigen der TreeView stellt Sichtbarkeit her (RestoreFolderTreeSelection).
        ''' <summary>Gleicht die Baummarkierung an den bereits gewechselten Ordner an (laeuft ueber
        ''' CurrentFolder-PropertyChanged, also NACH der Navigation). Das ist ein Wiederherstellen,
        ''' keine Nutzer-Navigation - deshalb unter _restoringFolderTreeSelection, damit
        ''' OnFolderTreeSelectionChanged nicht erneut navigiert UND den Seitenleisten-Tab nicht auf
        ''' "Ordner" umreisst. Genau das passierte beim Klick auf einen Ordner-Favoriten: die Ansicht
        ''' wechselte korrekt, aber der Tab sprang von Favoriten weg.</summary>
        Private Sub SelectFolderInTree(folderPath As String)
            Dim vm = GetVm()
            Dim tree = Me.FindControl(Of TreeView)("FolderTreeView")
            If vm Is Nothing OrElse tree Is Nothing OrElse String.IsNullOrEmpty(folderPath) Then Return

            Dim node = FindFolderNode(vm.FolderTree, folderPath)
            If node IsNot Nothing Then
                _restoringFolderTreeSelection = True
                Try
                    tree.SelectedItem = node
                Finally
                    _restoringFolderTreeSelection = False
                End Try
                ' Der uebersprungene Handler haette das mitgesetzt - der Ordnerbaum muss den
                ' aktiven Ordner trotzdem als markiert kennen (Kontextmenue, Wiederherstellung).
                vm.SelectedFolderNode = node
                node.EnsureChildrenLoaded()
                node.IsExpanded = True
            End If
        End Sub

        ''' Avalonias TreeView scrollt eine per Code gesetzte SelectedItem nur dann automatisch ins
        ''' Sichtfeld, wenn der zugehörige TreeViewItem-Container zum Zeitpunkt der Zuweisung bereits
        ''' realisiert ist. Direkt nach dem Aufklappen der Vorfahren ist das noch nicht der Fall (Layout
        ''' läuft erst beim nächsten Layout-Pass) - daher hier per Post (niedrige Priorität, nach Layout)
        ''' im Visual Tree nach dem passenden Container suchen. Statt des Standard-BringIntoView (das nur
        ''' minimal an den Rand scrollt) wird der Eintrag vertikal MITTIG im sichtbaren Bereich platziert.
        Private Sub BringTreeItemIntoView(tree As TreeView, item As Object)
            If tree Is Nothing OrElse item Is Nothing Then Return
            Dispatcher.UIThread.Post(Sub()
                                          Dim container = FindTreeViewItemForData(tree, item)
                                          If container IsNot Nothing Then CenterTreeItemInScrollViewer(tree, container)
                                      End Sub, DispatcherPriority.Background)
        End Sub

        Private Function FindTreeViewItemForData(root As Visual, item As Object) As TreeViewItem
            For Each child In root.GetVisualChildren()
                Dim tvi = TryCast(child, TreeViewItem)
                If tvi IsNot Nothing AndAlso tvi.DataContext Is item Then Return tvi
                Dim found = FindTreeViewItemForData(child, item)
                If found IsNot Nothing Then Return found
            Next
            Return Nothing
        End Function

        ''' Position wird relativ zu "tree" (nicht zum ScrollViewer) berechnet: der ScrollViewer
        ''' clippt/verschiebt nur die Anzeige, sein Kind (TreeView) selbst wird weiterhin in voller,
        ''' ungescrollter Höhe angeordnet - TranslatePoint dorthin liefert also die absolute Position
        ''' innerhalb des Gesamtinhalts, unabhängig vom aktuellen Scroll-Offset (kein Doppel-Zählen).
        Private Sub CenterTreeItemInScrollViewer(tree As TreeView, container As Control)
            Dim scrollViewer = tree.GetVisualAncestors().OfType(Of ScrollViewer)().FirstOrDefault()
            If scrollViewer Is Nothing OrElse scrollViewer.Viewport.Height <= 0 Then
                container.BringIntoView()
                Return
            End If

            Dim topLeft = container.TranslatePoint(New Avalonia.Point(0, 0), tree)
            If Not topLeft.HasValue Then
                container.BringIntoView()
                Return
            End If

            Dim viewportHeight = scrollViewer.Viewport.Height
            Dim itemHeight = container.Bounds.Height
            ' Bereits komplett sichtbar? Dann NICHT scrollen - es soll nur Sichtbarkeit
            ' sichergestellt werden, kein Zwangs-Zentrieren (Nutzer-Feedback).
            Dim currentOffset = scrollViewer.Offset.Y
            If topLeft.Value.Y >= currentOffset AndAlso
               topLeft.Value.Y + itemHeight <= currentOffset + viewportHeight Then
                Return
            End If
            Dim desiredY = topLeft.Value.Y - (viewportHeight / 2) + (itemHeight / 2)
            Dim maxY = Math.Max(0.0, scrollViewer.Extent.Height - viewportHeight)
            desiredY = Math.Max(0.0, Math.Min(maxY, desiredY))
            scrollViewer.Offset = New Avalonia.Vector(scrollViewer.Offset.X, desiredY)
        End Sub

        Private Function FindFolderNode(nodes As IEnumerable(Of FolderNode), folderPath As String) As FolderNode
            For Each node In nodes
                If String.Equals(NormalizePath(node.FullPath), NormalizePath(folderPath), PathIdentity.Comparison) Then
                    Return node
                End If

                If IsAncestorOrSelf(node.FullPath, folderPath) Then
                    node.EnsureChildrenLoaded()
                    Dim child = FindFolderNode(node.Children, folderPath)
                    If child IsNot Nothing Then
                        node.IsExpanded = True
                        Return child
                    End If
                End If
            Next

            Return Nothing
        End Function

        Private Shared Function IsAncestorOrSelf(parentPath As String, childPath As String) As Boolean
            Dim parent = NormalizePath(parentPath)
            Dim child = NormalizePath(childPath)
            If String.IsNullOrEmpty(parent) OrElse String.IsNullOrEmpty(child) Then Return False

            Return child.Equals(parent, PathIdentity.Comparison) OrElse
                   child.StartsWith(parent.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar) & IO.Path.DirectorySeparatorChar, PathIdentity.Comparison)
        End Function

        Private Shared Function NormalizePath(path As String) As String
            If String.IsNullOrEmpty(path) Then Return ""
            Try
                Dim fullPath = IO.Path.GetFullPath(path)
                Dim trimmed = fullPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
                Return If(String.IsNullOrEmpty(trimmed), fullPath, trimmed)
            Catch
                Dim trimmed = path.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
                Return If(String.IsNullOrEmpty(trimmed), path, trimmed)
            End Try
        End Function
    End Class

End Namespace
