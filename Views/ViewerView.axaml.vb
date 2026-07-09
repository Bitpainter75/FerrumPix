Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports FerrumPix.Controls
Imports FerrumPix.Models
Imports FerrumPix.Services
Imports FerrumPix.ViewModels
Imports System.ComponentModel
Imports System.Threading.Tasks
Imports LibVLCSharp.Avalonia
Imports LibVLCSharp.Shared

Namespace Views

    Public Class ViewerView
        Inherits UserControl

        Private _subscribedVm As ViewerViewModel
        Private _isAttached As Boolean = False
        Private _isPanningImage As Boolean
        Private _panStartPoint As Point
        Private _panStartOffset As Vector
        Private _isCropDragging As Boolean
        Private _cropDragMoved As Boolean
        Private _cropDragStartNorm As Point
        Private _cropDragCurrentNorm As Point
        Private Const CropDragMinPixels As Double = 12
        Private ReadOnly _filmstripController As FilmstripInteractionController

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            _filmstripController = New FilmstripInteractionController(Me, New ViewportThumbnailTracker(),
                Function() GetVm()?.FilmstripItems,
                Function() If(GetVm() Is Nothing, -1, GetVm().CurrentFilmstripIndex))
            AddHandler DataContextChanged, AddressOf HandleDataContextChanged
            AddHandler Loaded, Sub(s, e)
                                   Me.AddHandler(InputElement.PointerWheelChangedEvent, AddressOf OnPointerWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel)
                                   UpdateInfoSidebarLayout()
                                   _filmstripController.AttachTo(Me.FindControl(Of ListBox)("FilmstripListBox"))
                                   _filmstripController.QueueThumbnailRefresh()
                                   Dispatcher.UIThread.Post(Sub() _filmstripController.RefreshThumbnails(), DispatcherPriority.Background)

                                   ' RoundSlider markt PointerReleased in seiner eigenen Klassen-Behandlung bereits
                                   ' als "Handled" (siehe Controls/RoundSlider.vb) - ein normal per XAML angehängter
                                   ' Instanz-Handler würde dadurch riskieren, nie aufgerufen zu werden. handledEventsToo:=True
                                   ' erzwingt den Aufruf unabhängig davon.
                                   Dim seekSlider = Me.FindControl(Of Control)("VideoSeekSlider")
                                   If seekSlider IsNot Nothing Then
                                       seekSlider.AddHandler(InputElement.PointerReleasedEvent, AddressOf OnVideoSeekSliderPointerReleased,
                                                              Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo:=True)
                                   End If
                               End Sub
        End Sub

        Private Function GetVm() As ViewerViewModel
            Return TryCast(DataContext, ViewerViewModel)
        End Function

        Public Sub OnFilmstripItemPressed(sender As Object, e As PointerPressedEventArgs)
            Dim border = TryCast(sender, Border)
            If border Is Nothing Then Return
            Dim item = TryCast(border.DataContext, ImageItem)
            If item Is Nothing Then Return
            If e.GetCurrentPoint(Nothing).Properties.IsMiddleButtonPressed Then
                _filmstripController.ShowPreview(item)
                e.Handled = True
                Return
            End If
            If Not e.GetCurrentPoint(Nothing).Properties.IsLeftButtonPressed Then Return
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            vm.NavigateToItem(item)
            Me.Focus()
        End Sub

        Public Sub OnToggleFilmstripClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            If mainVm Is Nothing OrElse mainVm.Settings Is Nothing Then Return
            mainVm.Settings.ViewerShowFilmstrip = Not mainVm.Settings.ViewerShowFilmstrip
        End Sub

        Public Sub OnGlobalPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            If e.InitialPressMouseButton = MouseButton.Middle Then
                _filmstripController.HidePreview()
            End If
        End Sub

        Public Shadows Sub OnKeyDown(sender As Object, e As KeyEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            If IsTextInputSource(e.Source) Then Return
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)

            If mainVm IsNot Nothing AndAlso mainVm.IsFullscreen AndAlso (e.Key = Key.Escape OrElse e.Key = Key.Space) Then
                mainVm.ExitFullscreen()
                e.Handled = True
                Return
            End If

            If e.KeyModifiers.HasFlag(KeyModifiers.Control) Then
                Select Case e.Key
                    Case Key.L
                        vm.RotateLeftCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                    Case Key.R
                        vm.RotateRightCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                    Case Key.I
                        vm.ToggleInfoSidebarCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                    Case Key.E
                        vm.EditCommand.Execute(Nothing)
                        e.Handled = True
                        Return
                End Select
            End If

            Select Case e.Key
                Case Key.Left, Key.PageUp
                    vm.PreviousCommand.Execute(Nothing)
                    e.Handled = True
                Case Key.Right, Key.PageDown
                    vm.NextCommand.Execute(Nothing)
                    e.Handled = True
                Case Key.Add, Key.OemPlus
                    vm.ZoomIn()
                    e.Handled = True
                Case Key.Subtract, Key.OemMinus
                    vm.ZoomOut()
                    e.Handled = True
                Case Key.D0, Key.NumPad0
                    vm.ZoomFitCommand.Execute(Nothing)
                    ApplyImageFitMode()
                    e.Handled = True
                Case Key.Delete
                    vm.DeleteCurrentCommand.Execute(Nothing)
                    e.Handled = True
                Case Key.F2
                    vm.RenameCurrentCommand.Execute(Nothing)
                    e.Handled = True
                Case Key.Space
                    vm.ToggleSlideshowCommand.Execute(Nothing)
                    e.Handled = True
                Case Key.F11
                    OnToggleFullscreenClick(Nothing, Nothing)
                    e.Handled = True
                Case Key.Escape, Key.Back
                    If mainVm IsNot Nothing AndAlso mainVm.IsFullscreen Then
                        mainVm.ExitFullscreen()
                    Else
                        vm.BackToGalleryCommand.Execute(Nothing)
                    End If
                    e.Handled = True
            End Select
        End Sub

        Private Function IsTextInputSource(source As Object) As Boolean
            Dim ctrl = TryCast(source, Control)
            While ctrl IsNot Nothing
                If TypeOf ctrl Is TextBox Then Return True
                ctrl = TryCast(ctrl.Parent, Control)
            End While
            Return False
        End Function

        ' Kopiert die Datei über ClipboardPathService (DataFormat.File / text/uri-list), damit sie
        ' sich wie in der Galerie in einem Dateimanager (z.B. Dolphin) als echte Datei einfügen
        ' lässt - reines SetTextAsync(path) erzeugt dort keinen einfügbaren Dateiverweis.
        Public Async Sub OnCopyPathClick(sender As Object, e As RoutedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse String.IsNullOrEmpty(vm.CurrentImagePath) Then Return
            Dim owner = TopLevel.GetTopLevel(Me)
            Await ClipboardPathService.CopyPathsAsync(owner?.Clipboard, owner?.StorageProvider, {vm.CurrentImagePath}, cut:=False)
        End Sub

        Public Sub OnToggleFullscreenClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            If mainVm Is Nothing Then Return
            If mainVm.IsFullscreen Then
                mainVm.ExitFullscreen()
            Else
                mainVm.EnterFullscreen()
            End If
            If e IsNot Nothing Then e.Handled = True
        End Sub

        Public Sub OnSettingsClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            mainVm?.OpenSettings()
            e.Handled = True
        End Sub

        Public Sub OnPointerWheel(sender As Object, e As PointerWheelEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing Then Return
            If IsWithinInfoSidebar(e.Source) Then Return

            If e.KeyModifiers.HasFlag(KeyModifiers.Control) Then
                If e.Delta.Y < 0 Then
                    vm.ZoomOut()
                ElseIf e.Delta.Y > 0 Then
                    vm.ZoomIn()
                End If
                ApplyImageFitMode()
            Else
                vm.NavigateByWheel(e.Delta.Y)
            End If
            e.Handled = True
        End Sub

        Private Function IsWithinInfoSidebar(source As Object) As Boolean
            Dim ctrl = TryCast(source, Control)
            While ctrl IsNot Nothing
                If TypeOf ctrl Is InfoSidebarView Then Return True
                ctrl = TryCast(ctrl.Parent, Control)
            End While
            Return False
        End Function

        Private Sub OnImagePointerPressed(sender As Object, e As PointerPressedEventArgs)
            If Not e.GetCurrentPoint(Nothing).Properties.IsLeftButtonPressed Then Return
            Dim vm = GetVm()
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)("ImageScrollViewer")
            If vm Is Nothing OrElse scrollViewer Is Nothing Then Return

            If CanPanImage(vm, scrollViewer) Then
                _isPanningImage = True
                _panStartPoint = e.GetPosition(scrollViewer)
                _panStartOffset = scrollViewer.Offset
                If TypeOf sender Is Control Then
                    e.Pointer.Capture(DirectCast(sender, Control))
                Else
                    e.Pointer.Capture(Me)
                End If
                e.Handled = True
                Return
            End If

            If CanCropDrag(vm) Then
                Dim image = Me.FindControl(Of Image)("MainImage")
                If image Is Nothing OrElse image.Bounds.Width <= 0 OrElse image.Bounds.Height <= 0 Then Return
                _cropDragStartNorm = NormalizeImagePoint(e.GetPosition(image), image.Bounds.Size)
                _cropDragCurrentNorm = _cropDragStartNorm
                _isCropDragging = True
                _cropDragMoved = False
                If TypeOf sender Is Control Then
                    e.Pointer.Capture(DirectCast(sender, Control))
                Else
                    e.Pointer.Capture(Me)
                End If
                e.Handled = True
            End If
        End Sub

        Private Sub OnImageDoubleTapped(sender As Object, e As TappedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse Not vm.CanEdit Then Return
            EndPanning()
            vm.EditCommand.Execute(Nothing)
            e.Handled = True
        End Sub

        Private Sub OnImagePointerMoved(sender As Object, e As PointerEventArgs)
            UpdateMousePositionText(e)

            If _isPanningImage Then
                Dim scrollViewer = Me.FindControl(Of ScrollViewer)("ImageScrollViewer")
                If scrollViewer Is Nothing Then Return

                Dim currentPoint = e.GetPosition(scrollViewer)
                Dim delta = currentPoint - _panStartPoint
                Dim targetOffset = New Vector(_panStartOffset.X - delta.X, _panStartOffset.Y - delta.Y)
                scrollViewer.Offset = ClampOffset(scrollViewer, targetOffset)
                e.Handled = True
                Return
            End If

            If _isCropDragging Then
                Dim image = Me.FindControl(Of Image)("MainImage")
                If image Is Nothing Then Return
                Dim rawPoint = e.GetPosition(image)
                _cropDragCurrentNorm = NormalizeImagePoint(rawPoint, image.Bounds.Size)
                If Not _cropDragMoved Then
                    Dim startPixel = New Point(_cropDragStartNorm.X * image.Bounds.Width, _cropDragStartNorm.Y * image.Bounds.Height)
                    Dim delta = rawPoint - startPixel
                    If Math.Abs(delta.X) > CropDragMinPixels OrElse Math.Abs(delta.Y) > CropDragMinPixels Then
                        _cropDragMoved = True
                    End If
                End If
                UpdateCropSelectionVisual()
                e.Handled = True
            End If
        End Sub

        Private Sub OnImagePointerReleased(sender As Object, e As PointerReleasedEventArgs)
            If _isCropDragging Then
                Dim wasMoved = _cropDragMoved
                Dim startNorm = _cropDragStartNorm
                Dim endNorm = _cropDragCurrentNorm
                _isCropDragging = False
                _cropDragMoved = False
                HideCropSelectionVisual()
                e.Pointer.Capture(Nothing)

                If wasMoved Then
                    Dim vm = GetVm()
                    If vm IsNot Nothing Then
                        Dim left = Math.Min(startNorm.X, endNorm.X) * 100.0
                        Dim right = (1.0 - Math.Max(startNorm.X, endNorm.X)) * 100.0
                        Dim top = Math.Min(startNorm.Y, endNorm.Y) * 100.0
                        Dim bottom = (1.0 - Math.Max(startNorm.Y, endNorm.Y)) * 100.0
                        vm.OpenCropInEditor(left, top, right, bottom)
                    End If
                End If
                Return
            End If

            EndPanning()
            e.Pointer.Capture(Nothing)
        End Sub

        Private Sub OnImagePointerCaptureLost(sender As Object, e As PointerCaptureLostEventArgs)
            EndPanning()
            If _isCropDragging Then
                _isCropDragging = False
                _cropDragMoved = False
                HideCropSelectionVisual()
            End If
        End Sub

        Private Function CanCropDrag(vm As ViewerViewModel) As Boolean
            Return vm IsNot Nothing AndAlso vm.CurrentImage IsNot Nothing AndAlso vm.CanEdit AndAlso
                   vm.RotationAngle = 0 AndAlso vm.ScaleX = 1.0
        End Function

        ''' <summary>Läuft unabhängig von Pan/Crop-Dragging bei jeder Mausbewegung über dem Bild, damit
        ''' die Bildpixel-Koordinate in der Fußleiste immer aktuell ist.</summary>
        Private Sub UpdateMousePositionText(e As PointerEventArgs)
            Dim vm = GetVm()
            Dim image = Me.FindControl(Of Image)("MainImage")
            If vm Is Nothing OrElse image Is Nothing OrElse vm.CurrentImage Is Nothing Then Return
            Dim norm = NormalizeImagePoint(e.GetPosition(image), image.Bounds.Size)
            Dim px = CInt(norm.X * vm.CurrentImage.PixelSize.Width)
            Dim py = CInt(norm.Y * vm.CurrentImage.PixelSize.Height)
            vm.MousePositionText = $"{px}, {py}"
        End Sub

        Private Sub OnImagePointerExited(sender As Object, e As PointerEventArgs)
            Dim vm = GetVm()
            If vm IsNot Nothing Then vm.MousePositionText = ""
        End Sub

        Private Function NormalizeImagePoint(p As Point, size As Size) As Point
            If size.Width <= 0 OrElse size.Height <= 0 Then Return New Point(0, 0)
            Dim nx = Math.Max(0, Math.Min(1, p.X / size.Width))
            Dim ny = Math.Max(0, Math.Min(1, p.Y / size.Height))
            Return New Point(nx, ny)
        End Function

        Private Sub UpdateCropSelectionVisual()
            Dim image = Me.FindControl(Of Image)("MainImage")
            Dim canvas = Me.FindControl(Of Canvas)("CropSelectionCanvas")
            Dim rect = Me.FindControl(Of Border)("CropSelectionRect")
            If image Is Nothing OrElse canvas Is Nothing OrElse rect Is Nothing Then Return

            Dim origin = image.TranslatePoint(New Point(0, 0), canvas)
            If Not origin.HasValue Then Return

            Dim size = image.Bounds.Size
            Dim x1 = origin.Value.X + Math.Min(_cropDragStartNorm.X, _cropDragCurrentNorm.X) * size.Width
            Dim y1 = origin.Value.Y + Math.Min(_cropDragStartNorm.Y, _cropDragCurrentNorm.Y) * size.Height
            Dim x2 = origin.Value.X + Math.Max(_cropDragStartNorm.X, _cropDragCurrentNorm.X) * size.Width
            Dim y2 = origin.Value.Y + Math.Max(_cropDragStartNorm.Y, _cropDragCurrentNorm.Y) * size.Height

            Canvas.SetLeft(rect, x1)
            Canvas.SetTop(rect, y1)
            rect.Width = Math.Max(0, x2 - x1)
            rect.Height = Math.Max(0, y2 - y1)
            rect.IsVisible = True

            Dim badge = Me.FindControl(Of TextBlock)("CropSelectionSizeBadgeText")
            Dim vm = GetVm()
            If badge IsNot Nothing AndAlso vm IsNot Nothing AndAlso vm.CurrentImage IsNot Nothing Then
                Dim pixelWidth = CInt(Math.Round(Math.Max(1, Math.Abs(_cropDragCurrentNorm.X - _cropDragStartNorm.X) * vm.CurrentImage.PixelSize.Width)))
                Dim pixelHeight = CInt(Math.Round(Math.Max(1, Math.Abs(_cropDragCurrentNorm.Y - _cropDragStartNorm.Y) * vm.CurrentImage.PixelSize.Height)))
                badge.Text = $"{pixelWidth} × {pixelHeight} px"
            End If
        End Sub

        Private Sub HideCropSelectionVisual()
            Dim rect = Me.FindControl(Of Border)("CropSelectionRect")
            If rect IsNot Nothing Then rect.IsVisible = False
        End Sub

        Private Sub EndPanning()
            _isPanningImage = False
        End Sub

        Public Sub OnImageViewportSizeChanged(sender As Object, e As SizeChangedEventArgs)
            Dim vm = GetVm()
            If vm IsNot Nothing Then
                vm.SetImageViewportSize(e.NewSize.Width, e.NewSize.Height)
            End If
            ApplyImageFitMode()
        End Sub

        Public Sub OnFullscreenViewportSizeChanged(sender As Object, e As SizeChangedEventArgs)
            ApplyFullscreenImageMode()
        End Sub

        ''' Das ViewerViewModel lebt über die ganze Sitzung, diese View wird bei jedem Moduswechsel neu
        ''' gebaut. Beim Verwerfen feuert kein DataContextChanged, deshalb hängt das Abo am Entfernen aus
        ''' dem visuellen Baum - sonst bliebe je Betrachter-Besuch eine tote View am ViewModel.
        Protected Overrides Sub OnAttachedToVisualTree(e As VisualTreeAttachmentEventArgs)
            MyBase.OnAttachedToVisualTree(e)
            _isAttached = True
            RebindViewModel()
        End Sub

        Protected Overrides Sub OnDetachedFromVisualTree(e As VisualTreeAttachmentEventArgs)
            MyBase.OnDetachedFromVisualTree(e)
            _isAttached = False
            UnsubscribeViewModel()
        End Sub

        Private Sub RebindViewModel()
            UnsubscribeViewModel()
            If Not _isAttached Then Return
            _subscribedVm = GetVm()
            If _subscribedVm IsNot Nothing Then
                AddHandler _subscribedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            End If
        End Sub

        Private Sub UnsubscribeViewModel()
            If _subscribedVm Is Nothing Then Return
            RemoveHandler _subscribedVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            _subscribedVm = Nothing
        End Sub

        Private Sub HandleDataContextChanged(sender As Object, e As EventArgs)
            RebindViewModel()

            _filmstripController.Reset()
            ApplyImageFitMode()
            _filmstripController.ScrollToCurrent()
            ApplyVideoLayout()
            UpdateActiveVideoView()
        End Sub

        Private Sub OnViewModelPropertyChanged(sender As Object, e As PropertyChangedEventArgs)
            If e.PropertyName = NameOf(ViewerViewModel.IsFitToWindow) OrElse
               e.PropertyName = NameOf(ViewerViewModel.CurrentImage) OrElse
               e.PropertyName = NameOf(ViewerViewModel.ZoomLevel) OrElse
               e.PropertyName = NameOf(ViewerViewModel.RotationAngle) Then
                ApplyImageFitMode()
                ApplyFullscreenImageMode()
            End If

            If e.PropertyName = NameOf(ViewerViewModel.IsFullscreenMode) Then
                ApplyFullscreenImageMode()
                ApplyVideoLayout()
            End If

            If e.PropertyName = NameOf(ViewerViewModel.IsVideoFile) Then
                UpdateActiveVideoView()
            End If

            If e.PropertyName = NameOf(ViewerViewModel.CurrentFilmstripIndex) Then
                _filmstripController.ScrollToCurrent()
            End If

            If e.PropertyName = NameOf(ViewerViewModel.IsInfoSidebarVisible) Then
                UpdateInfoSidebarLayout()
            End If
        End Sub

        ''' Positioniert das einzige VideoOverlay-Grid je nach Vollbild-Status: im Fenstermodus
        ''' auf die Content-Zelle (Grid.Row=1/Column=0) mit demselben 88/0/80/0-Rand wie das
        ''' Bild, im Vollbildmodus über das gesamte Fenster (RowSpan=3/ColumnSpan=2, randlos) -
        ''' rein per Layout, ohne das VideoView selbst je ab- und wieder anzuhängen. Dadurch
        ''' bleibt sein natives Fenster-Handle über Vollbild-Wechsel hinweg unangetastet.
        Private Sub ApplyVideoLayout()
            Dim vm = GetVm()
            Dim overlay = Me.FindControl(Of Grid)("VideoOverlay")
            If overlay Is Nothing Then Return

            If vm IsNot Nothing AndAlso vm.IsFullscreenMode Then
                Grid.SetRow(overlay, 0)
                Grid.SetColumn(overlay, 0)
                Grid.SetRowSpan(overlay, 3)
                Grid.SetColumnSpan(overlay, 2)
                overlay.Margin = New Thickness(0)
            Else
                Grid.SetRow(overlay, 1)
                Grid.SetColumn(overlay, 0)
                Grid.SetRowSpan(overlay, 1)
                Grid.SetColumnSpan(overlay, 1)
                overlay.Margin = New Thickness(88, 0, 80, 0)
            End If
        End Sub

        ''' Weist den von ViewerViewModel gehaltenen MediaPlayer dem einzigen VideoOverlay zu
        ''' (bzw. leert es), wenn sich IsVideoFile ändert - der Vollbild-Wechsel selbst löst dies
        ''' NICHT mehr aus (siehe ApplyVideoLayout), wodurch das native Fenster-Handle über
        ''' Vollbild-Wechsel und Video-zu-Video-Navigation hinweg bestehen bleibt.
        Private _pendingVideoAttachHandler As EventHandler

        Private Sub UpdateActiveVideoView()
            Dim vm = GetVm()
            Dim videoView = Me.FindControl(Of VideoView)("TheVideoView")
            If videoView Is Nothing Then Return

            If _pendingVideoAttachHandler IsNot Nothing Then
                RemoveHandler videoView.LayoutUpdated, _pendingVideoAttachHandler
                _pendingVideoAttachHandler = Nothing
            End If

            Dim isVideoActive = vm IsNot Nothing AndAlso vm.IsVideoFile AndAlso vm.IsVideoPlaybackAvailable
            If Not isVideoActive Then
                videoView.MediaPlayer = Nothing
                Return
            End If

            AttachVideoPlayer(videoView, vm.VideoMediaPlayer, vm)
        End Sub

        ''' Das native Fenster-Handle eines VideoView-Controls entsteht erst, sobald für es
        ''' tatsächlich ein Layout-Durchlauf stattgefunden hat (insbesondere direkt nachdem sein
        ''' Container durch einen Sichtbarkeits-Wechsel gerade erst sichtbar wurde). MediaPlayer
        ''' vorher zuzuweisen kann "ins Leere" binden (Ton läuft weiter, kein Bild) oder LibVLC
        ''' dazu bringen, mangels Ausgabeziel kurz ein eigenes Fenster zu erzeugen. Statt einer
        ''' geschätzten Dispatcher-Verzögerung wird hier direkt auf LayoutUpdated gewartet, bis
        ''' das Control tatsächlich eine reale Größe hat.
        Private Sub AttachVideoPlayer(target As VideoView, mediaPlayer As MediaPlayer, vm As ViewerViewModel)
            If target.Bounds.Width > 0 AndAlso target.Bounds.Height > 0 Then
                target.MediaPlayer = mediaPlayer
                vm?.StartPendingVideoAutoplay()
                Return
            End If

            Dim handler As EventHandler = Nothing
            handler = Sub(s As Object, e As EventArgs)
                          If target.Bounds.Width <= 0 OrElse target.Bounds.Height <= 0 Then Return
                          RemoveHandler target.LayoutUpdated, handler
                          If Object.ReferenceEquals(_pendingVideoAttachHandler, handler) Then _pendingVideoAttachHandler = Nothing
                          target.MediaPlayer = mediaPlayer
                          vm?.StartPendingVideoAutoplay()
                      End Sub
            _pendingVideoAttachHandler = handler
            AddHandler target.LayoutUpdated, handler
        End Sub

        Public Sub OnVideoViewTapped(sender As Object, e As TappedEventArgs)
            Dim vm = GetVm()
            If vm Is Nothing OrElse vm.PlayPauseVideoCommand Is Nothing Then Return
            If vm.PlayPauseVideoCommand.CanExecute(Nothing) Then vm.PlayPauseVideoCommand.Execute(Nothing)
        End Sub

        Public Sub OnVideoSeekSliderPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            Dim slider = TryCast(sender, RoundSlider)
            Dim vm = GetVm()
            If slider Is Nothing OrElse vm Is Nothing OrElse vm.SeekVideoCommand Is Nothing Then Return
            If vm.SeekVideoCommand.CanExecute(slider.Value) Then vm.SeekVideoCommand.Execute(slider.Value)
        End Sub

        Private Sub UpdateInfoSidebarLayout()
            Dim vm = GetVm()
            Dim root = Me.FindControl(Of Grid)("ViewerRootGrid")
            Dim sidebar = Me.FindControl(Of Border)("ViewerInfoSidebarBorder")
            If root Is Nothing Then Return

            If root.ColumnDefinitions.Count >= 2 Then
                root.ColumnDefinitions(1).Width = If(vm IsNot Nothing AndAlso vm.IsInfoSidebarVisible, New GridLength(300), New GridLength(0))
            End If

            If sidebar IsNot Nothing Then
                sidebar.IsVisible = vm IsNot Nothing AndAlso vm.IsInfoSidebarVisible
            End If
        End Sub

        Private Sub ApplyImageFitMode()
            Dim vm = GetVm()
            Dim image = Me.FindControl(Of Image)("MainImage")
            Dim scrollViewer = Me.FindControl(Of ScrollViewer)("ImageScrollViewer")
            If vm Is Nothing OrElse image Is Nothing OrElse scrollViewer Is Nothing Then Return

            If vm.CurrentImage Is Nothing Then Return

            Dim displayZoom = Math.Max(0.05, vm.ZoomLevel)
            ' Auf ganze Geräte-Pixel runden: Border (Schachbrettmuster) und Image werden sonst mit
            ' fraktionalen Werten unabhängig voneinander gerundet/gesnappt, was am rechten/unteren
            ' Rand einen ~1-2px durchscheinenden Schachbrett-Rand verursachen kann, auch bei
            ' komplett opaken Bildern.
            Dim imageWidth = Math.Round(vm.CurrentImage.Size.Width * displayZoom, MidpointRounding.AwayFromZero)
            Dim imageHeight = Math.Round(vm.CurrentImage.Size.Height * displayZoom, MidpointRounding.AwayFromZero)

            image.Width = imageWidth
            image.Height = imageHeight
            image.MaxWidth = Double.PositiveInfinity
            image.MaxHeight = Double.PositiveInfinity
            If vm.IsZoomFitActive Then
                scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                scrollViewer.Offset = New Vector(0, 0)
            Else
                scrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
                scrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            End If

            If Not CanPanImage(vm, scrollViewer) Then
                _isPanningImage = False
            End If
        End Sub

        Private Function CanPanImage(vm As ViewerViewModel, scrollViewer As ScrollViewer) As Boolean
            If vm Is Nothing OrElse vm.CurrentImage Is Nothing OrElse scrollViewer Is Nothing Then Return False
            Dim displayZoom = Math.Max(0.05, vm.ZoomLevel)
            Dim imageWidth = vm.CurrentImage.Size.Width * displayZoom
            Dim imageHeight = vm.CurrentImage.Size.Height * displayZoom
            Return imageWidth > scrollViewer.Bounds.Width OrElse imageHeight > scrollViewer.Bounds.Height
        End Function

        Private Function ClampOffset(scrollViewer As ScrollViewer, offset As Vector) As Vector
            Dim maxX = Math.Max(0, scrollViewer.Extent.Width - scrollViewer.Bounds.Width)
            Dim maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Bounds.Height)
            Dim clampedX = Math.Max(0, Math.Min(offset.X, maxX))
            Dim clampedY = Math.Max(0, Math.Min(offset.Y, maxY))
            Return New Vector(clampedX, clampedY)
        End Function

        Private Sub ApplyFullscreenImageMode()
            Dim vm = GetVm()
            Dim image = Me.FindControl(Of Image)("FullscreenImage")
            Dim viewport = Me.FindControl(Of Grid)("FullscreenViewport")
            If vm Is Nothing OrElse image Is Nothing OrElse viewport Is Nothing OrElse vm.CurrentImage Is Nothing Then Return

            Dim vw = viewport.Bounds.Width
            Dim vh = viewport.Bounds.Height
            If vw <= 0 OrElse vh <= 0 Then Return

            Dim iw = vm.CurrentImage.Size.Width
            Dim ih = vm.CurrentImage.Size.Height

            If iw <= vw AndAlso ih <= vh Then
                image.Stretch = Avalonia.Media.Stretch.None
                image.Width = iw
                image.Height = ih
                image.MaxWidth = Double.PositiveInfinity
                image.MaxHeight = Double.PositiveInfinity
            Else
                ' Auf die tatsächliche Uniform-skalierte Größe setzen statt auf die volle
                ' Viewport-Größe - sonst sizt sich der umgebende Border (Transparenz-Hintergrund/
                ' Schachbrettmuster) auf den vollen Viewport, während das Bild darin per Stretch
                ' kleiner (letterboxed) gerendert wird. Das ließ das Schachbrettmuster auch bei
                ' völlig undurchsichtigen Bildern in den Letterbox-/Pillarbox-Rändern durchscheinen.
                Dim scale = Math.Min(vw / iw, vh / ih)
                image.Stretch = Avalonia.Media.Stretch.Uniform
                image.Width = Math.Round(iw * scale, MidpointRounding.AwayFromZero)
                image.Height = Math.Round(ih * scale, MidpointRounding.AwayFromZero)
                image.MaxWidth = vw
                image.MaxHeight = vh
            End If
        End Sub

    End Class

End Namespace
