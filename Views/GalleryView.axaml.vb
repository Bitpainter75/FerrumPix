Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Input.Platform
Imports Avalonia.Markup.Xaml
Imports Avalonia.Interactivity
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

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            AddHandler DataContextChanged, AddressOf OnViewDataContextChanged
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

        Private Sub OnDescendantGotFocus(sender As Object, e As FocusChangedEventArgs)
            Dim focused = TryCast(e.Source, Control)
            If focused Is Nothing OrElse Object.ReferenceEquals(focused, Me) Then Return
            If IsTextInputSource(focused) Then Return
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
            ' Rahmen (Nutzerbefund 2026-07-19). Massgeblich ist allein, ob das ViewModel ein
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

        ''' Einstellung "Zeitleiste am rechten Rand": All / Immich (nur Immich-Ansichten) /
        ''' Folders (nur Ordner-/Suchansichten) / Off. Immich-Ansicht = ein Immich-Knoten ist
        ''' ausgewählt (Album oder "Alle Fotos").
        Private Function TimelineAllowedForCurrentView(vm As GalleryViewModel) As Boolean
            Dim mode = AppSettingsService.NormalizeGalleryTimelineMode(AppSettingsService.Load().GalleryTimelineMode)
            Dim isImmichView = vm.SelectedImmichNode IsNot Nothing
            Select Case mode
                Case "Off" : Return False
                Case "Immich" : Return isImmichView
                Case "Folders" : Return Not isImmichView
                Case Else : Return True
            End Select
        End Function

        Private Sub OnTimelineScrubRequested(sender As Object, offsetFraction As Double)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)(If(vm.IsGridView, "GalleryGridScrollViewer", "GalleryListScrollViewer"))
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
                ' Expand the virtualization window around the target row first so the spacer heights
                ' (and therefore the ScrollViewer's own Extent) are already correct before we set Offset -
                ' otherwise Avalonia clamps Offset against its own stale Extent regardless of maxOffset above.
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
                ' Expand the virtualization window around the target index first so the spacer heights
                ' (and therefore the ScrollViewer's own Extent) are already correct before we set Offset -
                ' otherwise Avalonia clamps Offset against its own stale Extent regardless of maxOffset above.
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
                RestoreFolderTreeSelection(sender, vm)
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
            Dim vm = GetVm()
            If vm Is Nothing OrElse e.AddedItems Is Nothing OrElse e.AddedItems.Count = 0 Then Return
            If _clearingNavigationSelection Then Return
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
            If Not opened AndAlso String.Equals(node.Kind, "NewSearch", StringComparison.Ordinal) Then
                ClearVirtualTreeSelections()
                Dim folderTree = Me.FindControl(Of TreeView)("FolderTreeView")
                RestoreFolderTreeSelection(folderTree, vm)
            End If
        End Sub

        Public Async Sub OnImmichNodePointerPressed(sender As Object, e As PointerPressedEventArgs)
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
        End Sub

        ''' Namen aller virtuellen Baeume - seit dem Tab-Umbau vier Stueck (je Tab eigene
        ''' Suchliste plus Immich-Baum und Favoriten). Wer hier einen vergisst, bekommt zwei
        ''' gleichzeitig markierte Baeume.
        Private Shared ReadOnly VirtualTreeNames As String() =
            {"SearchTreeView", "ImmichSearchTreeView", "ImmichTreeView", "FavoritesTreeView"}

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

        ' --- Drag&Drop lokal → Immich (Datei-Payload auf einen Immich-Knoten ablegen = Upload) ---

        ''' <summary>Knoten unter dem Zeiger. Laeuft erst den LOGISCHEN, dann den VISUELLEN Elternpfad
        ''' hoch: bei Inhalten aus einem ItemTemplate reisst die logische Kette ab, der Knoten waere sonst
        ''' je nach getroffenem Element mal auffindbar und mal nicht - genau daraus entstand die
        ''' Abweichung zwischen Mauszeiger und tatsaechlichem Ablegen (Nutzerbefund 2026-07-19).</summary>
        Private Function GetImmichDropNode(e As DragEventArgs) As VirtualNavigationNode
            Dim current = TryCast(e.Source, Control)
            While current IsNot Nothing
                Dim node = TryCast(current.DataContext, VirtualNavigationNode)
                If node IsNot Nothing Then Return node
                Dim logicalParent = TryCast(current.Parent, Control)
                current = If(logicalParent, current.GetVisualParent(Of Control)())
            End While
            Return Nothing
        End Function

        ''' <summary>Ziel und Nutzlast eines Immich-Drops - EINE Quelle fuer Mauszeiger und Ablegen, damit
        ''' die beiden nicht auseinanderlaufen koennen.
        ''' <paramref name="requireExistingFiles"/>: beim Ablegen muessen die Dateien wirklich da sein;
        ''' beim Ueberfliegen nicht - unter X11 reicht ein fremder Ziehvorgang die Dateiliste erst BEIM
        ''' Ablegen heraus, waehrend DragOver liest sie sich leer. Der Zeiger zeigte deshalb "geht nicht",
        ''' obwohl der Upload danach lief.</summary>
        Private Function ResolveImmichDrop(e As DragEventArgs, requireExistingFiles As Boolean) _
            As (Node As VirtualNavigationNode, LocalPaths As List(Of String), PayloadUnreadable As Boolean)
            Dim node = GetImmichDropNode(e)
            If node Is Nothing OrElse Not node.IsImmichNode Then Return (Nothing, New List(Of String)(), False)
            Dim payload = GetDragPayload(e)
            ' Nur echte lokale Dateien auf einen Immich-Knoten - keine Immich-Pseudo-Pfade (Immich→Immich hier nicht).
            Dim localPaths = payload.Paths.
                Where(Function(p) Not ImmichService.IsImmichPseudoPath(p) AndAlso
                                  (Not requireExistingFiles OrElse IO.File.Exists(p))).ToList()
            Return (node, localPaths, payload.Paths.Count = 0)
        End Function

        Public Sub OnImmichTreeDragOver(sender As Object, e As DragEventArgs)
            Dim drop = ResolveImmichDrop(e, requireExistingFiles:=False)
            If drop.Node IsNot Nothing AndAlso (drop.LocalPaths.Count > 0 OrElse drop.PayloadUnreadable) Then
                e.DragEffects = DragDropEffects.Copy
            Else
                e.DragEffects = DragDropEffects.None
            End If
            HighlightDropRow(e, e.DragEffects <> DragDropEffects.None)
            e.Handled = True
        End Sub

        Public Sub OnImmichTreeDrop(sender As Object, e As DragEventArgs)
            ClearDropHighlight()
            Dim drop = ResolveImmichDrop(e, requireExistingFiles:=True)
            If drop.Node Is Nothing Then Return
            e.Handled = True
            If drop.LocalPaths.Count = 0 Then Return
            GetVm()?.UploadToImmich(drop.Node, drop.LocalPaths)
        End Sub

        Public Async Sub OnImmichPasteClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            Dim node = GetVirtualNodeFromSender(sender)
            If node Is Nothing OrElse Not node.IsImmichNode Then Return
            Dim clipboardData = Await ClipboardPathService.ReadPathDataAsync(TopLevel.GetTopLevel(Me)?.Clipboard)
            Dim localPaths = clipboardData.Paths.Where(Function(p) Not ImmichService.IsImmichPseudoPath(p) AndAlso IO.File.Exists(p)).ToList()
            If localPaths.Count = 0 Then Return
            vm.UploadToImmich(node, localPaths)
        End Sub

        Public Async Sub OnImmichUploadClick(sender As Object, e As RoutedEventArgs)
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

        Private Sub RestoreFolderTreeSelection(sender As Object, vm As GalleryViewModel)
            Dim tree = TryCast(sender, TreeView)
            If tree Is Nothing OrElse vm.SelectedFolderNode Is Nothing Then Return
            RestoreFolderTreeSelection(tree, vm.SelectedFolderNode)
        End Sub

        Private Sub RestoreFolderTreeSelection(tree As TreeView, node As FolderNode)
            If tree Is Nothing OrElse node Is Nothing Then Return
            _restoringFolderTreeSelection = True
            Try
                tree.SelectedItem = node
            Finally
                _restoringFolderTreeSelection = False
            End Try
            ' NUR beim initialen Anzeigen der (neu aufgebauten) TreeView den aktuellen Ordner in den
            ' sichtbaren Bereich holen - Auto-Scrollen bei normaler Navigation stoerte den Nutzer
            ' (der Baum "zog" jeden angeklickten Ordner in die Mitte).
            BringTreeItemIntoView(tree, node)
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
                   Not e.KeyModifiers.HasFlag(KeyModifiers.Control) AndAlso
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

        Public Sub OnThumbnailContextRequested(sender As Object, e As ContextRequestedEventArgs)
            If ConsumeSuppressedGalleryContextMenu(e) Then Return
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            Dim item = GetItemFromSender(sender)
            If item Is Nothing Then Return
            _contextMenuItem = item

            If Not item.IsParentFolderEntry AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            UpdateGalleryItemContextMenu(sender, item)
        End Sub

        Public Sub OnGalleryAreaContextRequested(sender As Object, e As ContextRequestedEventArgs)
            If ConsumeSuppressedGalleryContextMenu(e) Then Return
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            If HasImageItemContext(e.Source) Then Return
            _contextMenuItem = Nothing

            Dim canPasteIntoCurrent = vm.CanPasteIntoFolder(vm.CurrentFolder)
            SetMenuItemVisible("GalleryGridCreateFolderMenuItem", canPasteIntoCurrent)
            SetMenuItemVisible("GalleryGridPasteMenuItem", canPasteIntoCurrent)
            SetMenuItemVisible("GalleryGridViewportCreateFolderMenuItem", canPasteIntoCurrent)
            SetMenuItemVisible("GalleryGridViewportPasteMenuItem", canPasteIntoCurrent)
            SetMenuItemVisible("GalleryListCreateFolderMenuItem", canPasteIntoCurrent)
            SetMenuItemVisible("GalleryListPasteMenuItem", canPasteIntoCurrent)
            SetMenuItemVisible("GalleryListViewportCreateFolderMenuItem", canPasteIntoCurrent)
            SetMenuItemVisible("GalleryListViewportPasteMenuItem", canPasteIntoCurrent)
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
            OpenGalleryItem(item)
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
            ElseIf modifiers.HasFlag(KeyModifiers.Control) Then
                vm.ToggleSelection(item)
                _selectionAnchor = item
            Else
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
        End Sub

        Public Async Sub OnThumbnailPointerMoved(sender As Object, e As PointerEventArgs)
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

            ' Immich-Assets sind Pseudo-Pfade (immich://…) und damit für ein fremdes Ziel wie Dolphin
            ' keine echten Dateien. Vor dem Ziehen die Originale in temporäre Dateien holen, damit der
            ' Export nach außen (und ein interner Drop) tatsächlich eine Datei liefert.
            Dim paths As New List(Of String)()
            For Each it In dragItems
                If it Is Nothing Then Continue For
                If it.IsImmichAsset Then
                    Dim assetId As String = Nothing, fileName As String = Nothing
                    If Not ImmichService.TryParsePseudoPath(it.FilePath, assetId, fileName) Then Continue For
                    Dim tmp = Await ImmichService.DownloadOriginalToTempAsync(assetId, fileName)
                    If Not String.IsNullOrEmpty(tmp) Then paths.Add(tmp)
                ElseIf Not String.IsNullOrEmpty(it.FilePath) Then
                    paths.Add(it.FilePath)
                End If
            Next
            If paths.Count = 0 Then Return

            ' Die Ziehlast trägt beides: das anwendungseigene Format, an dem der interne Drop das
            ' Verschieben erkennt, und die Dateien selbst - ohne die sieht ein fremdes Ziel wie Dolphin
            ' gar nichts und lehnt den Drop ab.
            Dim storageProvider = TopLevel.GetTopLevel(Me)?.StorageProvider
            Dim data = Await ClipboardPathService.BuildFileTransferAsync(storageProvider, paths,
                Sub(firstItem) firstItem.Set(FerrumPixPathsFormat, String.Join(ControlChars.Lf, paths)))
            If data.Items.Count = 0 Then
                data = New DataTransfer()
                data.Add(DataTransferItem.Create(FerrumPixPathsFormat, String.Join(ControlChars.Lf, paths)))
            End If

            _isDragging = True
            Try
                Await DragDrop.DoDragDropAsync(pressedArgs, data, DragDropEffects.Move Or DragDropEffects.Copy)
            Finally
                _isDragging = False
            End Try
        End Sub

        Public Sub OnItemsSelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            ' Selection is intentionally driven by thumbnail clicks and selection badges.
        End Sub

        Private Async Sub ShowQuickPreview(item As ImageItem)
            Dim overlay = Me.FindControl(Of Panel)("PreviewOverlay")
            Dim img = Me.FindControl(Of Avalonia.Controls.Image)("PreviewImage")
            If overlay Is Nothing OrElse img Is Nothing Then Return
            img.Source = item.Thumbnail
            overlay.IsVisible = True
            Me.Focus()
            Try
                Dim bmp = Await Task.Run(Function() As Bitmap
                                             If RawPreviewService.IsSupportedRaw(item.FilePath) Then
                                                 Using preview = RawPreviewService.ExtractPreviewWithFallback(item.FilePath)
                                                     Return If(preview IsNot Nothing, ImageOrientationService.LoadOrientedAvaloniaBitmap(preview), Nothing)
                                                 End Using
                                             End If
                                             Return ImageOrientationService.LoadOrientedAvaloniaBitmap(item.FilePath)
                                         End Function)
                If overlay.IsVisible AndAlso bmp IsNot Nothing Then img.Source = bmp
            Catch
            End Try
        End Sub

        Public Sub OnGlobalPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            If e.InitialPressMouseButton = MouseButton.Middle Then
                Dim overlay = Me.FindControl(Of Panel)("PreviewOverlay")
                If overlay IsNot Nothing Then overlay.IsVisible = False
            End If
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
            OpenGalleryItem(If(item, vm.SelectedItem))
        End Sub

        Public Sub OnContextEdit(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not IsSingleGallerySelection(vm) Then Return
            vm.OpenSelectedInEditor()
        End Sub

        Public Sub OnContextCopyPath(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not IsSingleGallerySelection(vm) Then Return
            Dim paths = vm.GetSelectedPaths()
            If paths.Count = 0 Then Return
            CopyPathsToClipboard(paths, False)
        End Sub

        Public Sub OnContextReveal(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not IsSingleGallerySelection(vm) Then Return
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

        Public Sub OnContextCopy(sender As Object, e As RoutedEventArgs)
            Dim item = GetItemFromSender(sender)
            Dim vm = GetVm()
            If item IsNot Nothing AndAlso vm IsNot Nothing AndAlso
               (vm.SelectedItems Is Nothing OrElse Not vm.SelectedItems.Contains(item)) Then
                vm.SelectOnly(item)
                _selectionAnchor = item
            End If
            CopySelectionToClipboard(False)
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
            Dim item = GetItemFromSender(sender)
            Dim targetFolder = If(item IsNot Nothing AndAlso item.IsFolder, item.FilePath, GetVm()?.CurrentFolder)
            Await PasteClipboardIntoFolder(targetFolder)
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
            Await PasteClipboardIntoFolder(GetVm()?.CurrentFolder)
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
            Dim path = GetFavoriteFolderPath(sender)
            If path Is Nothing Then Return
            Await PasteClipboardIntoFolder(path)
        End Sub

        Public Sub OnFavoriteCopyFolderPathClick(sender As Object, e As RoutedEventArgs)
            Dim path = GetFavoriteFolderPath(sender)
            If path Is Nothing Then Return
            Task.Run(Async Function()
                         Dim clip = TopLevel.GetTopLevel(Me)?.Clipboard
                         If clip IsNot Nothing Then Await clip.SetTextAsync(path)
                     End Function)
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

        Private Sub SetMenuItemVisible(name As String, visible As Boolean)
            Dim item = Me.FindControl(Of MenuItem)(name)
            If item IsNot Nothing Then item.IsVisible = visible
        End Sub

        ''' Dieselben Sichtbarkeitsregeln für alle drei Kopien des Menüs: Kachel-Ansicht (GridContext*),
        ''' Listen-Ansicht (ListContext*) und den Menü-Button der Werkzeugleiste (MenuContext*). Nicht
        ''' gefundene Namen überspringt SetMenuItemVisible, deshalb darf hier alles nebeneinander stehen.
        Private Sub UpdateGalleryItemContextMenu(sender As Object, item As ImageItem)
            Dim menu = TryCast(sender, Control)?.ContextMenu
            If menu Is Nothing OrElse item Is Nothing Then Return

            Dim vm = GetVm()
            Dim singleSelection = vm IsNot Nothing AndAlso IsSingleGallerySelection(vm)
            Dim isVirtual = vm IsNot Nothing AndAlso vm.IsVirtualFolder
            Dim isParentEntry = item.IsParentFolderEntry
            Dim showSingleItemActions = singleSelection AndAlso Not isParentEntry
            ''' Ein einziger "Umbenennen"-Eintrag für Einzel- UND Mehrfachauswahl (RenameSelectedCommand
            ''' entscheidet selbst, ob Einzel- oder Stapel-Umbenennen greift) - bei Mehrfachauswahl
            ''' müssen alle ausgewählten Elemente umbenennbar sein, bei Einzelauswahl nur das eine.
            Dim showRename = Not isVirtual AndAlso Not isParentEntry AndAlso
                              vm IsNot Nothing AndAlso vm.SelectedItems IsNot Nothing AndAlso
                              If(vm.SelectedItems.Count > 1,
                                 vm.SelectedItems.All(Function(i) i IsNot Nothing AndAlso Not i.IsParentFolderEntry AndAlso i.CanFileOperationRename),
                                 item.CanFileOperationRename)
            Dim showCollage = vm IsNot Nothing AndAlso
                              vm.SelectedItems IsNot Nothing AndAlso
                              vm.SelectedItems.Count >= 2 AndAlso
                              vm.SelectedItems.Where(Function(i) i IsNot Nothing AndAlso i.IsImage).Count() >= 2
            Dim showImageBatchActions = Not isParentEntry AndAlso
                                        vm IsNot Nothing AndAlso
                                        vm.SelectedItems IsNot Nothing AndAlso
                                        vm.SelectedItems.Any(Function(i) i IsNot Nothing AndAlso i.IsImage)
            Dim showResize = showImageBatchActions AndAlso
                              vm.SelectedItems.Where(Function(i) i IsNot Nothing AndAlso i.IsImage).All(Function(i) Not i.IsRawFile)
            SetMenuItemVisible(menu, "GridContextOpenMenuItem", showSingleItemActions)
            SetMenuItemVisible(menu, "GridContextEditMenuItem", showSingleItemActions AndAlso item.CanEditFile AndAlso item.IsImage)
            SetMenuControlVisible(menu, "GridContextTopSeparator", showSingleItemActions)
            SetMenuItemVisible(menu, "GridContextCreateFolderMenuItem", Not isParentEntry AndAlso Not isVirtual AndAlso vm IsNot Nothing AndAlso vm.CanPasteIntoFolder(vm.CurrentFolder))
            SetMenuItemVisible(menu, "GridContextRenameMenuItem", showRename)
            SetMenuItemVisible(menu, "GridContextCopyMenuItem", Not isParentEntry AndAlso item.CanFileOperationCopy)
            SetMenuItemVisible(menu, "GridContextCutMenuItem", Not isParentEntry AndAlso item.CanFileOperationRename)
            SetMenuItemVisible(menu, "GridContextPasteMenuItem", Not isVirtual AndAlso item.CanFileOperationPasteInto)
            SetMenuItemVisible(menu, "GridContextDuplicateMenuItem", Not isVirtual AndAlso Not isParentEntry AndAlso item.CanFileOperationCopy)
            SetMenuItemVisible(menu, "GridContextResizeMenuItem", showResize)
            SetMenuItemVisible(menu, "GridContextApplyWatermarkMenuItem", showResize)
            SetMenuItemVisible(menu, "GridContextApplyFilterMenuItem", showImageBatchActions)
            SetMenuItemVisible(menu, "GridContextBatchConvertMenuItem", showImageBatchActions)
            SetMenuItemVisible(menu, "GridContextRemoveMetadataMenuItem", showImageBatchActions)
            SetMenuItemVisible(menu, "GridContextCreateCollageMenuItem", showCollage)
            SetMenuControlVisible(menu, "GridContextPathSeparator", showSingleItemActions)
            SetMenuItemVisible(menu, "GridContextCopyPathMenuItem", showSingleItemActions)
            SetMenuItemVisible(menu, "GridContextRevealMenuItem", showSingleItemActions)
            SetMenuControlVisible(menu, "GridContextDeleteSeparator", Not isParentEntry AndAlso item.CanFileOperationDelete)
            SetMenuItemVisible(menu, "ListContextOpenMenuItem", showSingleItemActions)
            SetMenuItemVisible(menu, "ListContextEditMenuItem", showSingleItemActions AndAlso item.CanEditFile AndAlso item.IsImage)
            SetMenuControlVisible(menu, "ListContextTopSeparator", showSingleItemActions)
            SetMenuItemVisible(menu, "ListContextCreateFolderMenuItem", Not isParentEntry AndAlso Not isVirtual AndAlso vm IsNot Nothing AndAlso vm.CanPasteIntoFolder(vm.CurrentFolder))
            SetMenuItemVisible(menu, "ListContextRenameMenuItem", showRename)
            SetMenuItemVisible(menu, "ListContextCopyMenuItem", Not isParentEntry AndAlso item.CanFileOperationCopy)
            SetMenuItemVisible(menu, "ListContextCutMenuItem", Not isParentEntry AndAlso item.CanFileOperationRename)
            SetMenuItemVisible(menu, "ListContextPasteMenuItem", Not isVirtual AndAlso item.CanFileOperationPasteInto)
            SetMenuItemVisible(menu, "ListContextDuplicateMenuItem", Not isVirtual AndAlso Not isParentEntry AndAlso item.CanFileOperationCopy)
            SetMenuItemVisible(menu, "ListContextResizeMenuItem", showResize)
            SetMenuItemVisible(menu, "ListContextApplyWatermarkMenuItem", showResize)
            SetMenuItemVisible(menu, "ListContextApplyFilterMenuItem", showImageBatchActions)
            SetMenuItemVisible(menu, "ListContextBatchConvertMenuItem", showImageBatchActions)
            SetMenuItemVisible(menu, "ListContextRemoveMetadataMenuItem", showImageBatchActions)
            SetMenuItemVisible(menu, "ListContextCreateCollageMenuItem", showCollage)
            SetMenuItemVisible(menu, "ListContextPrintMenuItem", showImageBatchActions)
            SetMenuControlVisible(menu, "ListContextPathSeparator", showSingleItemActions)
            SetMenuItemVisible(menu, "ListContextCopyPathMenuItem", showSingleItemActions)
            SetMenuItemVisible(menu, "ListContextRevealMenuItem", showSingleItemActions)
            SetMenuControlVisible(menu, "ListContextDeleteSeparator", Not isParentEntry AndAlso item.CanFileOperationDelete)
            SetMenuItemVisible(menu, "MenuContextOpenMenuItem", showSingleItemActions)
            SetMenuItemVisible(menu, "MenuContextEditMenuItem", showSingleItemActions AndAlso item.CanEditFile AndAlso item.IsImage)
            SetMenuControlVisible(menu, "MenuContextTopSeparator", showSingleItemActions)
            SetMenuItemVisible(menu, "MenuContextCreateFolderMenuItem", Not isParentEntry AndAlso Not isVirtual AndAlso vm IsNot Nothing AndAlso vm.CanPasteIntoFolder(vm.CurrentFolder))
            SetMenuItemVisible(menu, "MenuContextRenameMenuItem", showRename)
            SetMenuItemVisible(menu, "MenuContextCopyMenuItem", Not isParentEntry AndAlso item.CanFileOperationCopy)
            SetMenuItemVisible(menu, "MenuContextCutMenuItem", Not isParentEntry AndAlso item.CanFileOperationRename)
            SetMenuItemVisible(menu, "MenuContextPasteMenuItem", Not isVirtual AndAlso item.CanFileOperationPasteInto)
            SetMenuItemVisible(menu, "MenuContextDuplicateMenuItem", Not isVirtual AndAlso Not isParentEntry AndAlso item.CanFileOperationCopy)
            SetMenuItemVisible(menu, "MenuContextResizeMenuItem", showResize)
            SetMenuItemVisible(menu, "MenuContextApplyWatermarkMenuItem", showResize)
            SetMenuItemVisible(menu, "MenuContextApplyFilterMenuItem", showImageBatchActions)
            SetMenuItemVisible(menu, "MenuContextBatchConvertMenuItem", showImageBatchActions)
            SetMenuItemVisible(menu, "MenuContextRemoveMetadataMenuItem", showImageBatchActions)
            SetMenuItemVisible(menu, "MenuContextCreateCollageMenuItem", showCollage)
            SetMenuItemVisible(menu, "MenuContextPrintMenuItem", showImageBatchActions)
            SetMenuControlVisible(menu, "MenuContextPathSeparator", showSingleItemActions)
            SetMenuItemVisible(menu, "MenuContextCopyPathMenuItem", showSingleItemActions)
            SetMenuItemVisible(menu, "MenuContextRevealMenuItem", showSingleItemActions)
            SetMenuControlVisible(menu, "MenuContextDeleteSeparator", Not isParentEntry AndAlso item.CanFileOperationDelete)
            SetMenuItemVisible(menu, "MenuContextDeleteMenuItem", Not isParentEntry AndAlso item.CanFileOperationDelete)
        End Sub

        ''' Der Menü-Button der Werkzeugleiste zeigt dasselbe Menü wie ein Rechtsklick auf das Bild - viele
        ''' Funktionen stecken inzwischen nur dort. Anders als in den Kachel-/Listen-Vorlagen ist der
        ''' DataContext hier das ViewModel, nicht das Bild: die IsVisible-Bindungen der Einträge
        ''' (CanEditFile, CanFileOperationCopy, ...) hängen aber am Bild, deshalb wird er vor dem Öffnen
        ''' auf das ausgewählte Element gesetzt.
        Private Sub OnGalleryMenuButtonClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = GetVm()
            If button Is Nothing OrElse vm Is Nothing Then Return

            Dim item = If(vm.SelectedItem, vm.SelectedItems?.FirstOrDefault())
            If item Is Nothing Then Return

            _contextMenuItem = item
            Dim menu = button.ContextMenu
            If menu Is Nothing Then Return
            menu.DataContext = item
            UpdateGalleryItemContextMenu(button, item)
            menu.Open(button)
            e.Handled = True
        End Sub

        Private Sub SetMenuItemVisible(menu As ContextMenu, name As String, visible As Boolean)
            Dim item = FindMenuItem(menu.Items, name)
            If item IsNot Nothing Then item.IsVisible = visible
        End Sub

        Private Sub SetMenuControlVisible(menu As ContextMenu, name As String, visible As Boolean)
            Dim item = FindMenuControl(menu.Items, name)
            If item IsNot Nothing Then item.IsVisible = visible
        End Sub

        Private Function FindMenuItem(items As IEnumerable, name As String) As MenuItem
            If items Is Nothing Then Return Nothing
            For Each entry In items
                Dim menuItem = TryCast(entry, MenuItem)
                If menuItem Is Nothing Then Continue For
                If String.Equals(menuItem.Name, name, StringComparison.Ordinal) Then Return menuItem
                Dim child = FindMenuItem(menuItem.Items, name)
                If child IsNot Nothing Then Return child
            Next
            Return Nothing
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

        Public Sub OnContextCopyFolderPath(sender As Object, e As RoutedEventArgs)
            Dim node = GetFolderTreeContextNode()
            If node Is Nothing Then Return
            Dim path = node.FullPath
            Task.Run(Async Function()
                         Dim clip = TopLevel.GetTopLevel(Me)?.Clipboard
                         If clip IsNot Nothing Then Await clip.SetValueAsync(DataFormat.Text, path)
                     End Function)
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
            Dim node = GetFolderTreeContextNode()
            If node Is Nothing Then Return
            Await PasteClipboardIntoFolder(node.FullPath)
        End Sub

        ''' <summary>Stammt die Ziehlast aus Immich? Der Pseudo-Pfad allein reicht NICHT: eine Ziehgeste
        ''' aus der Galerie laedt Immich-Assets vorher in TEMPORAERE Dateien (siehe OnItemsDragStart) und
        ''' traegt danach nur noch deren echte Pfade. Ohne die Temp-Pruefung fiel so ein Drop in den
        ''' "intern = verschieben"-Zweig, und Verschieben ist auf Temp-Dateien nicht erlaubt
        ''' (FileOperationPolicy.CanMove verlangt einen Pfad im persoenlichen Ordner). Ergebnis:
        ''' Mauszeiger "geht nicht" und Immich→Ordner ging gar nicht (Nutzerbefund 2026-07-19).</summary>
        Private Shared Function PayloadHasImmich(payload As (Paths As List(Of String), IsInternal As Boolean)) As Boolean
            Return payload.Paths.Any(Function(p) ImmichService.IsImmichPseudoPath(p) OrElse
                                                 ImmichService.IsImmichTempPath(p))
        End Function

        Private Function GetDropEffects(payload As (Paths As List(Of String), IsInternal As Boolean), targetFolder As String) As DragDropEffects
            Dim vm = GetVm()
            If vm Is Nothing OrElse String.IsNullOrEmpty(targetFolder) OrElse payload.Paths.Count = 0 Then Return DragDropEffects.None
            ' Immich-Items in einen lokalen Ordner ziehen = herunterladen (Kopie), nie "verschieben".
            If PayloadHasImmich(payload) Then
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
            If PayloadHasImmich(payload) Then
                Await vm.PastePathsIntoFolderAsync(payload.Paths, targetFolder, cut:=False)
            ElseIf payload.IsInternal Then
                Await vm.MovePathsToFolderAsync(payload.Paths, targetFolder)
            Else
                Await vm.PastePathsIntoFolderAsync(payload.Paths, targetFolder, cut:=False)
            End If
        End Function

        Public Sub OnFolderTreeDragOver(sender As Object, e As DragEventArgs)
            Dim target = GetDropFolder(e)
            e.DragEffects = GetDropEffects(GetDragPayload(e), target?.FullPath)
            HighlightDropRow(e, e.DragEffects <> DragDropEffects.None)
            e.Handled = True
        End Sub

        ''' <summary>Hebt die Zeile unter dem Zeiger hervor, solange dort abgelegt werden darf.
        ''' Avalonia stellt unter X11 bei anwendungsinternem Ziehen KEINE effektabhaengigen Mauszeiger dar -
        ''' gemessen 2026-07-19: Ziel, Nutzlast und Effekt stimmen (Move bzw. Copy, beides im erlaubten
        ''' Satz), der Zeiger zeigte trotzdem durchgehend "verboten", auch mit AllowDrop direkt am
        ''' getroffenen Element. Diese Rueckmeldung liegt dafuer vollstaendig in unserer Hand.</summary>
        Private _dropHighlightRow As Control

        Private Sub HighlightDropRow(e As DragEventArgs, erlaubt As Boolean)
            Dim zeile As Control = Nothing
            If erlaubt Then
                Dim current = TryCast(e.Source, Control)
                While current IsNot Nothing
                    If TypeOf current Is TreeViewItem Then
                        zeile = current
                        Exit While
                    End If
                    Dim logicalParent = TryCast(current.Parent, Control)
                    current = If(logicalParent, current.GetVisualParent(Of Control)())
                End While
            End If
            If Object.ReferenceEquals(zeile, _dropHighlightRow) Then Return
            _dropHighlightRow?.Classes.Remove("drop-target")
            _dropHighlightRow = zeile
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
            ClearDropHighlight()
            Dim target = GetDropFolder(e)
            If target Is Nothing Then Return
            Await ApplyDropAsync(GetDragPayload(e), target.FullPath)
            e.Handled = True
        End Sub

        Public Sub OnItemDragOver(sender As Object, e As DragEventArgs)
            Dim item = TryCast(TryCast(sender, Border)?.DataContext, ImageItem)
            Dim targetFolder = If(item IsNot Nothing AndAlso item.IsFolder, item.FilePath, Nothing)
            e.DragEffects = GetDropEffects(GetDragPayload(e), targetFolder)
            e.Handled = True
        End Sub

        Public Async Sub OnItemDrop(sender As Object, e As DragEventArgs)
            Dim item = TryCast(TryCast(sender, Border)?.DataContext, ImageItem)
            If item Is Nothing OrElse Not item.IsFolder Then Return
            Await ApplyDropAsync(GetDragPayload(e), item.FilePath)
            e.Handled = True
        End Sub

        ''' Ablegen auf der freien Fläche der Galerie: fremde Dateien landen im gerade angezeigten Ordner.
        ''' Für eine Ziehgeste aus der Galerie selbst ergibt das nichts - die Dateien liegen schon dort.
        Public Sub OnGalleryAreaDragOver(sender As Object, e As DragEventArgs)
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
            While current IsNot Nothing
                Dim node = TryCast(current.DataContext, FolderNode)
                If node IsNot Nothing Then Return node
                Dim logicalParent = TryCast(current.Parent, Control)
                current = If(logicalParent, current.GetVisualParent(Of Control)())
            End While
            Return GetVm()?.SelectedFolderNode
        End Function

        ''' Die Ziehlast kommt entweder aus der Galerie selbst (dann verschieben wir) oder aus einem fremden
        ''' Dateimanager (dann kopieren wir - dessen Dateien liegen woanders und sollen dort bleiben).
        Private Function GetDragPayload(e As DragEventArgs) As (Paths As List(Of String), IsInternal As Boolean)
            Try
                Dim internal = e.DataTransfer.TryGetValue(FerrumPixPathsFormat)
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
                Dim files = e.DataTransfer.TryGetFiles()
                If files IsNot Nothing Then
                    Dim externalPaths = ClipboardPathService.ToLocalPaths(files)
                    If externalPaths.Count > 0 Then Return (externalPaths, False)
                End If
            Catch
            End Try

            Return (New List(Of String)(), False)
        End Function

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
            If IsTextInputSource(e.Source) Then Return

            If e.KeyModifiers.HasFlag(KeyModifiers.Control) Then
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
                        Await PasteClipboardIntoFolder(vm.CurrentFolder)
                        Return
                    Case Key.F
                        ' Strg+F bleibt die Suche; „Filter anwenden" liegt auf Strg+W
                        ' (Nutzerwunsch 2026-07-17, Strg+F war schon belegt).
                        FocusSearchBox()
                        e.Handled = True
                        Return
                    Case Key.W
                        ' Strg+W: Filter anwenden (Nutzerwunsch 2026-07-17, vorher Strg+Umschalt+F).
                        DiagnosticLogService.LogAlways("Gallery.Shortcut", $"key=Ctrl+W hasSelectedImage={vm.HasSelectedImage}")
                        If vm.HasSelectedImage Then vm.ApplyFilterSelectedCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                    Case Key.R
                        ' Strg+R: Bildgröße ändern (Nutzerwunsch 2026-07-17).
                        DiagnosticLogService.LogAlways("Gallery.Shortcut", $"key=Ctrl+R hasSelectedImage={vm.HasSelectedImage}")
                        If vm.HasSelectedImage Then vm.ResizeSelectedCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                    Case Key.D
                        ' Strg+D: Konvertieren nach (Nutzerwunsch 2026-07-17).
                        DiagnosticLogService.LogAlways("Gallery.Shortcut", $"key=Ctrl+D hasSelectedImage={vm.HasSelectedImage}")
                        If vm.HasSelectedImage Then vm.BatchConvertSelectedCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                End Select
            End If

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
                    HandleKeyboardNavigation(vm, ClampNavigationOffset(vm, GetPageOffset()), e.KeyModifiers)
                    e.Handled = True
                Case Key.PageUp
                    HandleKeyboardNavigation(vm, ClampNavigationOffset(vm, -GetPageOffset()), e.KeyModifiers)
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
            Dim overlay = Me.FindControl(Of Panel)("PreviewOverlay")
            If overlay IsNot Nothing Then overlay.IsVisible = False
            _spaceOverviewActive = False
            e.Handled = True
        End Sub

        Private Function IsTextInputSource(source As Object) As Boolean
            Dim ctrl = TryCast(source, Control)
            While ctrl IsNot Nothing
                If TypeOf ctrl Is TextBox Then Return True
                ctrl = TryCast(ctrl.Parent, Control)
            End While
            Return False
        End Function

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

        Private Async Sub CopyPathsToClipboard(paths As List(Of String), cut As Boolean)
            Dim owner = TopLevel.GetTopLevel(Me)
            Await ClipboardPathService.CopyPathsAsync(owner?.Clipboard, owner?.StorageProvider, paths, cut)
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

            ' PageUp/PageDown get clamped onto the first/last item once they overshoot the list bounds -
            ' in that case jump straight to the true edge instead of the "already visible" heuristic below.
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
                    Return If(GetVm()?.IsGridView, GetGridColumnCount(), 1)
                Case Key.Up
                    Return If(GetVm()?.IsGridView, -GetGridColumnCount(), -1)
                Case Else
                    Return 0
            End Select
        End Function

        Private Function GetGridColumnCount() As Integer
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not vm.IsGridView Then Return 1
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
            Dim cols = 1
            Dim itemSlotHeight = 0.0
            GetGridLayoutMetrics(scrollViewer, vm, cols, itemSlotHeight)
            Return cols
        End Function

        Private Sub GetGridLayoutMetrics(scrollViewer As ScrollViewer, vm As GalleryViewModel, ByRef columns As Integer, ByRef itemSlotHeight As Double)
            columns = 1
            itemSlotHeight = If(vm IsNot Nothing, Math.Max(1, vm.GridItemSlotHeight), 1)
            If vm Is Nothing Then Return

            Dim measuredColumns = 0
            Dim measuredSlotHeight = 0.0
            If TryGetRenderedGridMetrics(scrollViewer, measuredColumns, measuredSlotHeight) Then
                columns = Math.Max(1, measuredColumns)
                itemSlotHeight = Math.Max(1, measuredSlotHeight)
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
        End Sub

        Private Function TryGetRenderedGridMetrics(scrollViewer As ScrollViewer, ByRef columns As Integer, ByRef itemSlotHeight As Double) As Boolean
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
            Dim scrollViewerName = If(vm.IsGridView, "GalleryGridScrollViewer", "GalleryListScrollViewer")
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)(scrollViewerName)
            Dim viewportHeight = If(scrollViewer IsNot Nothing AndAlso scrollViewer.Viewport.Height > 0,
                                    scrollViewer.Viewport.Height,
                                    Bounds.Height)
            Dim itemHeight = 78.0
            If vm.IsGridView Then
                Dim cols = 1
                GetGridLayoutMetrics(scrollViewer, vm, cols, itemHeight)
            End If
            Return Math.Max(1, CInt(Math.Floor(viewportHeight / itemHeight)))
        End Function

        Private Function GetPageOffset() As Integer
            Dim vm = GetVm()
            If vm Is Nothing Then Return 1
            Dim rows = GetVisibleRowCount()
            Return If(vm.IsGridView, rows * GetGridColumnCount(), rows)
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

        ' Home/End must always reach the true first/last pixel of the list, so this bypasses the
        ' "already visible" heuristic in ScrollToSelectedItem and jumps straight to Offset 0 / max.
        Private Sub ScrollToExtreme(toEnd As Boolean)
            Dim vm = GetVm()
            If vm Is Nothing OrElse vm.Items Is Nothing OrElse vm.Items.Count = 0 Then Return

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

        Private Sub OpenGalleryItem(item As ImageItem)
            Dim vm = GetVm()
            If vm Is Nothing OrElse item Is Nothing Then Return

            If item.IsFolder Then
                vm.NavigateToFolder(item.FilePath)
                SelectFolderInTree(item.FilePath)
            Else
                vm.OpenSelectedInViewer()
            End If
        End Sub

        ''' Selektiert und expandiert den Ordner im Baum. Bewusst OHNE Auto-Scrollen: das Nachziehen
        ''' in die Mitte bei jeder Navigation stoerte (Nutzer-Feedback 2026-07-16) - nur das initiale
        ''' Anzeigen der TreeView stellt Sichtbarkeit her (RestoreFolderTreeSelection).
        ''' <summary>Gleicht die Baummarkierung an den bereits gewechselten Ordner an (laeuft ueber
        ''' CurrentFolder-PropertyChanged, also NACH der Navigation). Das ist ein Wiederherstellen,
        ''' keine Nutzer-Navigation - deshalb unter _restoringFolderTreeSelection, damit
        ''' OnFolderTreeSelectionChanged nicht erneut navigiert UND den Seitenleisten-Tab nicht auf
        ''' "Ordner" umreisst. Genau das passierte beim Klick auf einen Ordner-Favoriten: die Ansicht
        ''' wechselte korrekt, aber der Tab sprang von Favoriten weg (Nutzerbefund 2026-07-19).</summary>
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
            ' sichergestellt werden, kein Zwangs-Zentrieren (Nutzer-Feedback 2026-07-16).
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
