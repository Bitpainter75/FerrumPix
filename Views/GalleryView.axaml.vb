Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Markup.Xaml
Imports Avalonia.Interactivity
Imports Avalonia.Threading
Imports Avalonia.Media.Imaging
Imports Avalonia.VisualTree
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

        Private _initialSelectionDone As Boolean = False
        Private _dragStartPoint As Avalonia.Point
        Private _dragStartItem As ImageItem
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
        End Sub

        Private Sub OnDescendantGotFocus(sender As Object, e As GotFocusEventArgs)
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
            Dispatcher.UIThread.Post(Sub() Me.Focus(), DispatcherPriority.Background)

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
            Dim maxOffsetY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height)
            Dim newOffsetY = Math.Max(0, Math.Min(scrollViewer.Offset.Y - e.Delta.Y * GalleryWheelScrollStepPx, maxOffsetY))
            scrollViewer.Offset = New Vector(scrollViewer.Offset.X, newOffsetY)
            e.Handled = True
        End Sub

        Private Sub OnGalleryDetachedFromVisualTree(sender As Object, e As VisualTreeAttachmentEventArgs)
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

        Private Sub OnViewDataContextChanged(sender As Object, e As EventArgs)
            Dim vm = GetVm()
            If _observedVm IsNot Nothing Then
                RemoveHandler _observedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
                RemoveHandler _observedVm.RequestScrollToItem, AddressOf OnRequestScrollToItem
                If _observedVm.Items IsNot Nothing Then RemoveHandler _observedVm.Items.CollectionChanged, AddressOf OnGalleryItemsCollectionChanged
            End If
            _observedVm = vm
            If _observedVm IsNot Nothing Then
                AddHandler _observedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
                AddHandler _observedVm.RequestScrollToItem, AddressOf OnRequestScrollToItem
                If _observedVm.Items IsNot Nothing Then AddHandler _observedVm.Items.CollectionChanged, AddressOf OnGalleryItemsCollectionChanged
            End If
            QueueViewportThumbnailRefresh()

            If _initialSelectionDone Then Return
            If vm Is Nothing OrElse vm.InitialFolderNode Is Nothing Then Return
            _initialSelectionDone = True
            Dispatcher.UIThread.Post(Sub()
                Dim tree = Me.FindControl(Of TreeView)("FolderTreeView")
                If tree IsNot Nothing Then
                    tree.SelectedItem = vm.InitialFolderNode
                    BringTreeItemIntoView(tree, vm.InitialFolderNode)
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

            If e.PropertyName = NameOf(GalleryViewModel.SelectedFolderNode) Then
                Dispatcher.UIThread.Post(Sub()
                                             Dim vm = GetVm()
                                             Dim tree = Me.FindControl(Of TreeView)("FolderTreeView")
                                             If vm IsNot Nothing AndAlso tree IsNot Nothing AndAlso vm.SelectedFolderNode IsNot Nothing Then
                                                 tree.SelectedItem = vm.SelectedFolderNode
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

        End Sub

        Private Sub OnGalleryItemsCollectionChanged(sender As Object, e As NotifyCollectionChangedEventArgs)
            If e.Action = NotifyCollectionChangedAction.Reset Then
                _thumbnailTracker.Reset()
            End If
            QueueViewportThumbnailRefresh()
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

        Private Sub QueueViewportThumbnailRefresh()
            If _viewportThumbnailRefreshQueued Then Return
            _viewportThumbnailRefreshQueued = True
            Dispatcher.UIThread.Post(Sub()
                                         _viewportThumbnailRefreshQueued = False
                                         RequestViewportThumbnails()
                                     End Sub, DispatcherPriority.Input)
        End Sub

        Private Sub RequestViewportThumbnails()
            Dim vm = GetVm()
            If vm Is Nothing OrElse vm.Items Is Nothing OrElse vm.Items.Count = 0 Then Return

            If vm.IsGridView Then
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)("GalleryGridScrollViewer")
                If scrollViewer Is Nothing OrElse scrollViewer.Bounds.Height <= 0 Then Return

                Dim cols = Math.Max(1, GetGridColumnCount())
                Dim itemSlotHeight = vm.GridItemSlotHeight
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

                Dim cols = Math.Max(1, GetGridColumnCount())
                Dim row = idx \ cols
                Dim itemSlotHeight = vm.GridItemSlotHeight
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
                vm.SelectedFolderNode = node
                node.EnsureChildrenLoaded()
                If _isDragging Then Return
                If String.Equals(NormalizePath(vm.CurrentFolder), NormalizePath(node.FullPath), StringComparison.OrdinalIgnoreCase) Then Return
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
            ' "Neue Suche" per Dialog abgebrochen: der Ordner-/Suchbaum blieb oben bereits ohne
            ' Auswahl (ClearOtherNavigationSelections) - sichtbare Baumauswahl wieder auf den
            ' tatsächlich aktiven Ordner zurücksetzen, statt sie auf "Neue Suche" hängen zu lassen.
            If Not opened AndAlso String.Equals(node.Kind, "NewSearch", StringComparison.Ordinal) Then
                ClearVirtualTreeSelections()
                Dim folderTree = Me.FindControl(Of TreeView)("FolderTreeView")
                RestoreFolderTreeSelection(folderTree, vm)
            End If
        End Sub

        Private Sub ClearVirtualTreeSelections()
            _clearingNavigationSelection = True
            Try
                Dim searchTree = Me.FindControl(Of TreeView)("SearchTreeView")
                If searchTree IsNot Nothing Then searchTree.SelectedItem = Nothing
            Finally
                _clearingNavigationSelection = False
            End Try
        End Sub

        Private Sub ClearOtherNavigationSelections(activeTree As TreeView)
            _clearingNavigationSelection = True
            Try
                Dim searchTree = Me.FindControl(Of TreeView)("SearchTreeView")
                Dim folderTree = Me.FindControl(Of TreeView)("FolderTreeView")
                If searchTree IsNot Nothing AndAlso Not Object.ReferenceEquals(searchTree, activeTree) Then searchTree.SelectedItem = Nothing
                If folderTree IsNot Nothing Then folderTree.SelectedItem = Nothing
            Finally
                _clearingNavigationSelection = False
            End Try
        End Sub

        Public Sub OnRemoveVirtualSearchClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim node = TryCast(button?.DataContext, VirtualNavigationNode)
            GetVm()?.RemoveVirtualSearchNode(node)
            e.Handled = True
        End Sub

        Private Sub OnFolderTreePointerPressedTunnel(sender As Object, e As PointerPressedEventArgs)
            Dim properties = e.GetCurrentPoint(Nothing).Properties
            If properties.IsRightButtonPressed Then
                _folderTreeContextNode = GetFolderNodeFromSource(e.Source)
                _suppressFolderTreeSelectionChange = _folderTreeContextNode IsNot Nothing
            ElseIf properties.IsLeftButtonPressed Then
                _folderTreeContextNode = Nothing
            End If
        End Sub

        Private Sub RestoreFolderTreeSelection(sender As Object, vm As GalleryViewModel)
            Dim tree = TryCast(sender, TreeView)
            If tree Is Nothing OrElse vm.SelectedFolderNode Is Nothing Then Return
            _restoringFolderTreeSelection = True
            Try
                tree.SelectedItem = vm.SelectedFolderNode
            Finally
                _restoringFolderTreeSelection = False
            End Try
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
                    Me.Focus()
                    e.Handled = True
                    Return
                End If
                ApplyPointerSelection(vm, item, e.KeyModifiers)
                _dragStartItem = item
                _dragStartPoint = e.GetPosition(Me)
                Me.Focus()
                e.Handled = True
            End If
        End Sub

        Public Sub OnThumbnailContextRequested(sender As Object, e As ContextRequestedEventArgs)
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
            vm.SelectedItem = item
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
            If dragItem Is Nothing Then Return
            Dim useSelection = vm.SelectedItems IsNot Nothing AndAlso vm.SelectedItems.Contains(dragItem)
            Dim paths = If(useSelection,
                           vm.SelectedItems.Select(Function(i) i.FilePath).ToList(),
                           New List(Of String) From {dragItem.FilePath})
            _dragStartItem = Nothing

            Dim data = New DataObject()
            data.Set("FerrumPixPaths", paths)
            _isDragging = True
            Try
                Await DragDrop.DoDragDrop(e, data, DragDropEffects.Move Or DragDropEffects.Copy)
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
                                                 Using preview = RawPreviewService.ExtractPreview(item.FilePath)
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

        Private Function GetFolderTreeContextNode() As FolderNode
            Return If(_folderTreeContextNode, GetVm()?.SelectedFolderNode)
        End Function

        Private Sub SetMenuItemVisible(name As String, visible As Boolean)
            Dim item = Me.FindControl(Of MenuItem)(name)
            If item IsNot Nothing Then item.IsVisible = visible
        End Sub

        Private Sub UpdateGalleryItemContextMenu(sender As Object, item As ImageItem)
            Dim border = TryCast(sender, Border)
            Dim menu = border?.ContextMenu
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
            SetMenuItemVisible(menu, "GridContextOpenMenuItem", showSingleItemActions)
            SetMenuItemVisible(menu, "GridContextEditMenuItem", showSingleItemActions AndAlso item.CanEditFile AndAlso item.IsImage)
            SetMenuControlVisible(menu, "GridContextTopSeparator", showSingleItemActions)
            SetMenuItemVisible(menu, "GridContextCreateFolderMenuItem", Not isParentEntry AndAlso Not isVirtual AndAlso vm IsNot Nothing AndAlso vm.CanPasteIntoFolder(vm.CurrentFolder))
            SetMenuItemVisible(menu, "GridContextRenameMenuItem", showRename)
            SetMenuItemVisible(menu, "GridContextCopyMenuItem", Not isParentEntry AndAlso item.CanFileOperationCopy)
            SetMenuItemVisible(menu, "GridContextCutMenuItem", Not isParentEntry AndAlso item.CanFileOperationRename)
            SetMenuItemVisible(menu, "GridContextPasteMenuItem", Not isVirtual AndAlso item.CanFileOperationPasteInto)
            SetMenuItemVisible(menu, "GridContextDuplicateMenuItem", Not isVirtual AndAlso Not isParentEntry AndAlso item.CanFileOperationCopy)
            SetMenuItemVisible(menu, "GridContextResizeMenuItem", showImageBatchActions)
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
            SetMenuItemVisible(menu, "ListContextResizeMenuItem", showImageBatchActions)
            SetMenuItemVisible(menu, "ListContextBatchConvertMenuItem", showImageBatchActions)
            SetMenuItemVisible(menu, "ListContextRemoveMetadataMenuItem", showImageBatchActions)
            SetMenuItemVisible(menu, "ListContextCreateCollageMenuItem", showCollage)
            SetMenuControlVisible(menu, "ListContextPathSeparator", showSingleItemActions)
            SetMenuItemVisible(menu, "ListContextCopyPathMenuItem", showSingleItemActions)
            SetMenuItemVisible(menu, "ListContextRevealMenuItem", showSingleItemActions)
            SetMenuControlVisible(menu, "ListContextDeleteSeparator", Not isParentEntry AndAlso item.CanFileOperationDelete)
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
                         If clip IsNot Nothing Then Await clip.SetTextAsync(path)
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

        Public Sub OnFolderTreeDragOver(sender As Object, e As DragEventArgs)
            Dim target = GetDropFolder(e)
            Dim paths = GetDraggedPaths(e)
            Dim vm = GetVm()
            If target Is Nothing OrElse vm Is Nothing OrElse Not vm.CanMovePathsToFolder(paths, target.FullPath) Then
                e.DragEffects = DragDropEffects.None
            Else
                e.DragEffects = DragDropEffects.Move
            End If
            e.Handled = True
        End Sub

        Public Async Sub OnFolderTreeDrop(sender As Object, e As DragEventArgs)
            Dim target = GetDropFolder(e)
            If target Is Nothing Then Return
            Dim vm = GetVm()
            If vm IsNot Nothing Then Await vm.MovePathsToFolderAsync(GetDraggedPaths(e), target.FullPath)
            e.Handled = True
        End Sub

        Public Sub OnItemDragOver(sender As Object, e As DragEventArgs)
            Dim item = TryCast(TryCast(sender, Border)?.DataContext, ImageItem)
            Dim paths = GetDraggedPaths(e)
            Dim vm = GetVm()
            If item IsNot Nothing AndAlso item.IsFolder AndAlso vm IsNot Nothing AndAlso vm.CanMovePathsToFolder(paths, item.FilePath) Then
                e.DragEffects = DragDropEffects.Move
            Else
                e.DragEffects = DragDropEffects.None
            End If
            e.Handled = True
        End Sub

        Public Async Sub OnItemDrop(sender As Object, e As DragEventArgs)
            Dim item = TryCast(TryCast(sender, Border)?.DataContext, ImageItem)
            If item Is Nothing OrElse Not item.IsFolder Then Return
            Dim vm = GetVm()
            If vm IsNot Nothing Then Await vm.MovePathsToFolderAsync(GetDraggedPaths(e), item.FilePath)
            e.Handled = True
        End Sub

        Private Function GetDropFolder(e As DragEventArgs) As FolderNode
            Dim current = TryCast(e.Source, Control)
            While current IsNot Nothing
                Dim node = TryCast(current.DataContext, FolderNode)
                If node IsNot Nothing Then Return node
                current = TryCast(current.Parent, Control)
            End While
            Return GetVm()?.SelectedFolderNode
        End Function

        Private Function GetDraggedPaths(e As DragEventArgs) As List(Of String)
            Try
                Dim internal = TryCast(e.Data.Get("FerrumPixPaths"), IEnumerable(Of String))
                If internal IsNot Nothing Then
                    Return internal.
                        Where(Function(p) Not String.IsNullOrEmpty(p)).
                        Distinct(StringComparer.OrdinalIgnoreCase).
                        ToList()
                End If
            Catch
            End Try
            Return New List(Of String)()
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
                    Case Key.V
                        Await PasteClipboardIntoFolder(vm.CurrentFolder)
                        e.Handled = True
                        Return
                    Case Key.F
                        FocusSearchBox()
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
            Dim availableWidth = If(scrollViewer IsNot Nothing AndAlso scrollViewer.Bounds.Width > 0,
                                    scrollViewer.Bounds.Width - 36,
                                    Bounds.Width - 36)
            availableWidth = Math.Max(1, availableWidth)
            Dim itemWidth = Math.Max(1, vm.GridColumnPitch)
            Return Math.Max(1, CInt(Math.Floor(availableWidth / itemWidth)))
        End Function

        Private Function GetVisibleRowCount() As Integer
            Dim vm = GetVm()
            If vm Is Nothing Then Return 1
            Dim scrollViewerName = If(vm.IsGridView, "GalleryGridScrollViewer", "GalleryListScrollViewer")
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)(scrollViewerName)
            Dim viewportHeight = If(scrollViewer IsNot Nothing AndAlso scrollViewer.Viewport.Height > 0,
                                    scrollViewer.Viewport.Height,
                                    Bounds.Height)
            Dim itemHeight = If(vm.IsGridView, Math.Max(1, vm.GridItemSlotHeight), 78.0)
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

                Dim cols = Math.Max(1, GetGridColumnCount())
                Dim itemSlotHeight = vm.GridItemSlotHeight
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

        Private Sub SelectFolderInTree(folderPath As String)
            Dim vm = GetVm()
            Dim tree = Me.FindControl(Of TreeView)("FolderTreeView")
            If vm Is Nothing OrElse tree Is Nothing OrElse String.IsNullOrEmpty(folderPath) Then Return

            Dim node = FindFolderNode(vm.FolderTree, folderPath)
            If node IsNot Nothing Then
                tree.SelectedItem = node
                node.EnsureChildrenLoaded()
                node.IsExpanded = True
                BringTreeItemIntoView(tree, node)
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
            Dim desiredY = topLeft.Value.Y - (viewportHeight / 2) + (itemHeight / 2)
            Dim maxY = Math.Max(0.0, scrollViewer.Extent.Height - viewportHeight)
            desiredY = Math.Max(0.0, Math.Min(maxY, desiredY))
            scrollViewer.Offset = New Avalonia.Vector(scrollViewer.Offset.X, desiredY)
        End Sub

        Private Function FindFolderNode(nodes As IEnumerable(Of FolderNode), folderPath As String) As FolderNode
            For Each node In nodes
                If String.Equals(NormalizePath(node.FullPath), NormalizePath(folderPath), StringComparison.OrdinalIgnoreCase) Then
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

            Return child.Equals(parent, StringComparison.OrdinalIgnoreCase) OrElse
                   child.StartsWith(parent.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar) & IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
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
