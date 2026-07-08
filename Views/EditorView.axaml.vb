Imports System.ComponentModel
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Controls.Shapes
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Media
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform.Storage
Imports Avalonia.Threading
Imports Avalonia.Vector
Imports Avalonia.VisualTree
Imports FerrumPix.Controls
Imports FerrumPix.Models
Imports FerrumPix.ViewModels
Imports System.Threading.Tasks
Imports System.Linq
Imports System.Collections.Generic
Imports System.IO
Imports SkiaSharp
Imports Svg.Skia

Namespace Views

    Public Class EditorView
        Inherits UserControl

        Private _isDraggingSlider As Boolean = False
        Private _sliderPosition As Double = 0.5
        Private _currentVm As EditorViewModel

        ' Zoom state (0-100 slider maps exponentially to 10%-500%)
        Private _zoomSliderValue As Double = 50
        Private _zoomInitialized As Boolean = False
        Private _ignoreSliderChange As Boolean = False

        ' Pan state
        Private _panX As Double = 0
        Private _panY As Double = 0
        Private _isPanMode As Boolean = False
        Private _spacePanActive As Boolean = False
        Private _isPanDragging As Boolean = False
        Private _panStartX As Double = 0
        Private _panStartY As Double = 0
        Private _panStartOffsetX As Double = 0
        Private _panStartOffsetY As Double = 0
        Private _isCropDragging As Boolean = False
        Private _cropStart As Avalonia.Point
        Private _cropEnd As Avalonia.Point
        Private _cropDragMode As CropDragMode = CropDragMode.None
        Private _cropDragInitialRect As Avalonia.Rect
        Private _cropDragPointerStart As Avalonia.Point
        Private _isTextDragging As Boolean = False
        Private _textDragMode As TextDragMode = TextDragMode.None
        Private _textDragInitialRect As Avalonia.Rect
        Private _textDragPointerStart As Avalonia.Point
        Private _textDragAspect As Double = 1.0
        Private _textRotateStartAngle As Double
        Private _textRotateStartRotation As Double
        Private _textRotateCenter As Avalonia.Point
        Private _isBrushDrawing As Boolean = False
        Private ReadOnly _brushPoints As New List(Of Avalonia.Point)()
        Private _isRetouching As Boolean = False
        Private _lastRetouchPoint As Avalonia.Point
        Private _isSelectionDragging As Boolean = False
        Private _selectionStart As Avalonia.Point
        Private _selectionEnd As Avalonia.Point
        Private ReadOnly _filmstripController As FilmstripInteractionController

        Private Enum CropDragMode
            None
            NewSelection
            Move
            Left
            Top
            Right
            Bottom
            TopLeft
            TopRight
            BottomLeft
            BottomRight
        End Enum

        Private Enum TextDragMode
            None
            Move
            Left
            Top
            Right
            Bottom
            TopLeft
            TopRight
            BottomLeft
            BottomRight
            Rotate
        End Enum

        Private Function SliderToZoom(s As Double) As Double
            Return 10.0 * Math.Pow(50.0, s / 100.0)
        End Function

        Private Function ZoomToSlider(zoomPct As Double) As Double
            Dim clamped = Math.Max(10.0, Math.Min(500.0, zoomPct))
            Return Math.Max(0, Math.Min(100, Math.Log(clamped / 10.0) / Math.Log(50.0) * 100.0))
        End Function

        Private Sub SetZoom(sliderValue As Double)
            _zoomSliderValue = Math.Max(0, Math.Min(100, sliderValue))
            _ignoreSliderChange = True
            Dim zs = Me.FindControl(Of RoundSlider)("EditorZoomSlider")
            If zs IsNot Nothing Then zs.Value = _zoomSliderValue
            _ignoreSliderChange = False
            UpdateSliderLayout()
            UpdateZoomDisplay()
        End Sub

        Private Sub UpdateZoomDisplay()
            Dim pct = CInt(Math.Round(SliderToZoom(_zoomSliderValue)))
            Dim txt = Me.FindControl(Of TextBlock)("ZoomPercentText")
            If txt IsNot Nothing Then txt.Text = $"{pct}%"
        End Sub

        Public Sub OnZoomOutClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm IsNot Nothing Then vm.ActiveZoomPreset = ZoomPresetMode.Manual
            SetZoom(_zoomSliderValue - 8)
        End Sub

        Public Sub OnZoomInClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm IsNot Nothing Then vm.ActiveZoomPreset = ZoomPresetMode.Manual
            SetZoom(_zoomSliderValue + 8)
        End Sub

        Public Sub OnZoomFitClick(sender As Object, e As RoutedEventArgs)
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas Is Nothing OrElse vm Is Nothing OrElse vm.DisplayImage Is Nothing Then Return
            Dim cw = canvas.Bounds.Width
            Dim ch = canvas.Bounds.Height
            Dim effectiveSize = GetEffectiveDisplaySize(vm)
            Dim imgW = effectiveSize.Width
            Dim imgH = effectiveSize.Height
            If cw <= 0 OrElse ch <= 0 OrElse imgW <= 0 OrElse imgH <= 0 Then Return
            vm.ActiveZoomPreset = ZoomPresetMode.Fit
            _panX = 0
            _panY = 0
            Dim fitPct = Math.Min(cw / imgW, ch / imgH) * 100.0
            If IsOnlyWhenLargerFitBehavior() Then fitPct = Math.Min(fitPct, 100.0)
            SetZoom(ZoomToSlider(fitPct))
        End Sub

        Public Sub OnZoomActualClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm IsNot Nothing Then vm.ActiveZoomPreset = ZoomPresetMode.Actual
            _panX = 0
            _panY = 0
            SetZoom(ZoomToSlider(100.0))
        End Sub

        ''' <summary>Liest die gemeinsame Viewer/Editor-Einstellung "Einpassen-Verhalten" - "OnlyWhenLarger"
        ''' verkleinert größere Bilder auf die Fläche, skaliert kleinere Bilder aber nicht hoch (100%).</summary>
        Private Function IsOnlyWhenLargerFitBehavior() As Boolean
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            Return String.Equals(mainVm?.Settings?.ViewerFitBehavior, "OnlyWhenLarger", StringComparison.OrdinalIgnoreCase)
        End Function

        Private Async Function PickSingleImagePathAsync(title As String) As Task(Of String)
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return Nothing
                Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                    .Title = title,
                    .AllowMultiple = False,
                    .FileTypeFilter = New List(Of FilePickerFileType) From {
                        New FilePickerFileType("Bilder") With {
                            .Patterns = New String() {"*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.tif", "*.tiff", "*.avif", "*.ico"}
                        }
                    }
                })
                Return files?.FirstOrDefault()?.Path.LocalPath
            Catch
                Return Nothing
            End Try
        End Function

        Private Async Sub PlacePendingImageAsync(xPercent As Double, yPercent As Double)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return

            Try
                Dim path = Await PickSingleImagePathAsync("Bild auswählen")
                If Not String.IsNullOrWhiteSpace(path) Then
                    vm.AddImageAnnotationAt(path, xPercent, yPercent)
                End If
            Catch
            End Try
        End Sub

        Public Async Sub OnWatermarkChooseImageClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            Dim path = Await PickSingleImagePathAsync("Wasserzeichen-Bild auswählen")
            If Not String.IsNullOrWhiteSpace(path) Then vm.SetWatermarkImagePath(path)
        End Sub

        Public Sub OnWatermarkClearImageClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            vm.ClearWatermarkImagePath()
        End Sub

        Public Sub OnWatermarkSavePresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            vm.SaveCurrentWatermarkPreset()
        End Sub

        Public Sub OnWatermarkDeletePresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            vm.DeleteCurrentWatermarkPreset()
        End Sub

        Public Async Sub OnLoadLightroomPresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                    .Title = "Lightroom-Preset laden",
                    .AllowMultiple = False,
                    .FileTypeFilter = New List(Of FilePickerFileType) From {
                        New FilePickerFileType("Lightroom XMP") With {
                            .Patterns = New String() {"*.xmp"}
                        }
                    }
                })
                Dim file = files?.FirstOrDefault()
                If file Is Nothing Then Return
                vm.SaveLightroomPresetToSettings(file.Path.LocalPath)
                vm.ApplyLightroomPreset(file.Path.LocalPath)
            Catch
            End Try
        End Sub

        Public Sub OnApplySavedLightroomPresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim preset = TryCast(TryCast(sender, Control)?.DataContext, FerrumPix.Services.LightroomPresetSettings)
            If vm Is Nothing OrElse preset Is Nothing Then Return
            vm.ApplyLightroomPreset(preset.Path)
        End Sub

        Public Sub OnRemoveSavedLightroomPresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim preset = TryCast(TryCast(sender, Control)?.DataContext, FerrumPix.Services.LightroomPresetSettings)
            If vm Is Nothing OrElse preset Is Nothing Then Return
            vm.RemoveLightroomPresetFromSettings(preset.Path)
            e.Handled = True
        End Sub

        Public Sub OnPanModeToggleClick(sender As Object, e As RoutedEventArgs)
            _isPanMode = Not _isPanMode
            Dim btn = Me.FindControl(Of Button)("PanModeButton")
            If btn IsNot Nothing Then
                If _isPanMode Then btn.Classes.Add("active") Else btn.Classes.Remove("active")
            End If
        End Sub

        Public Sub OnToggleFilmstripClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            If mainVm Is Nothing OrElse mainVm.Settings Is Nothing Then Return
            mainVm.Settings.EditorShowFilmstrip = Not mainVm.Settings.EditorShowFilmstrip
        End Sub

        Public Sub OnToggleBeforeAfterClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            If Not vm.CanShowBeforeAfter Then
                vm.SetComparisonVisibleFromUser(False)
                UpdateSliderLayout()
                Return
            End If
            vm.SetComparisonVisibleFromUser(Not vm.ShowBeforeImage)
            UpdateSliderLayout()
        End Sub

        Public Sub OnCanvasWheelZoom(sender As Object, e As PointerWheelEventArgs)
            Dim canvas = TryCast(sender, Canvas)
            If canvas Is Nothing Then Return
            Dim sourceCtrl = TryCast(e.Source, Control)
            If sourceCtrl Is Nothing Then Return
            Dim current = sourceCtrl
            Dim isInsideCanvas As Boolean = False
            While current IsNot Nothing
                If Object.ReferenceEquals(current, canvas) Then
                    isInsideCanvas = True
                    Exit While
                End If
                current = TryCast(current.Parent, Control)
            End While
            If Not isInsideCanvas Then Return
            If e.Delta.Y = 0 Then Return
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm IsNot Nothing Then vm.ActiveZoomPreset = ZoomPresetMode.Manual
            SetZoom(_zoomSliderValue + If(e.Delta.Y > 0, 6.0, -6.0))
            e.Handled = True
        End Sub

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            _filmstripController = New FilmstripInteractionController(Me, New ViewportThumbnailTracker(),
                Function() TryCast(DataContext, EditorViewModel)?.FilmstripItems,
                Function() If(TryCast(DataContext, EditorViewModel) Is Nothing, -1, TryCast(DataContext, EditorViewModel).CurrentFilmstripIndex))
            AddHandler DataContextChanged, AddressOf HandleDataContextChanged
            AddHandler Loaded, Sub(s, e)
                UpdateSliderLayout()
                UpdateInfoSidebarLayout()
                Dim filmstrip = Me.FindControl(Of ListBox)("FilmstripListBox")
                _filmstripController.AttachTo(filmstrip)
                If filmstrip IsNot Nothing Then
                    filmstrip.AddHandler(InputElement.PointerWheelChangedEvent, AddressOf OnFilmstripWheelChanged, Avalonia.Interactivity.RoutingStrategies.Tunnel)
                End If
                _filmstripController.QueueThumbnailRefresh()
                Dispatcher.UIThread.Post(Sub() _filmstripController.RefreshThumbnails(), DispatcherPriority.Background)
                Dim zs = Me.FindControl(Of RoundSlider)("EditorZoomSlider")
                If zs IsNot Nothing Then
                    AddHandler zs.PropertyChanged, Sub(sender As Object, args As Avalonia.AvaloniaPropertyChangedEventArgs)
                                                       If _ignoreSliderChange Then Return
                                                       If args.Property = RoundSlider.ValueProperty Then
                                                           Dim sliderVm = TryCast(DataContext, EditorViewModel)
                                                           If sliderVm IsNot Nothing Then sliderVm.ActiveZoomPreset = ZoomPresetMode.Manual
                                                           _zoomSliderValue = zs.Value
                                                           UpdateSliderLayout()
                                                           UpdateZoomDisplay()
                                                       End If
                                                   End Sub
                End If
            End Sub
        End Sub

        Private Sub HandleDataContextChanged(sender As Object, e As EventArgs)
            If _currentVm IsNot Nothing Then
                RemoveHandler _currentVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            End If
            _currentVm = TryCast(DataContext, EditorViewModel)
            If _currentVm IsNot Nothing Then
                AddHandler _currentVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
            End If
            _filmstripController.Reset()
        End Sub

        ''' EditorViewModel lebt für die gesamte App-Laufzeit (eine Instanz, wiederverwendet bei
        ''' jedem Wechsel Galerie/Editor), während für jeden Editor-Aufruf eine neue EditorView
        ''' erzeugt wird (ViewLocator.Build -> Activator.CreateInstance). Ohne dieses Abmelden
        ''' würde die langlebige VM über den PropertyChanged-Delegate jede alte View-Instanz für
        ''' immer am Leben halten (Memory-Leak, wächst mit jedem Galerie<->Editor-Wechsel).
        Protected Overrides Sub OnDetachedFromVisualTree(e As Avalonia.VisualTreeAttachmentEventArgs)
            MyBase.OnDetachedFromVisualTree(e)
            If _currentVm IsNot Nothing Then
                RemoveHandler _currentVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
                _currentVm = Nothing
            End If
        End Sub

        Private Sub OnViewModelPropertyChanged(sender As Object, e As PropertyChangedEventArgs)
            Select Case e.PropertyName
                Case NameOf(EditorViewModel.CurrentImage)
                    _sliderPosition = 0.5
                    ' Zoom-Modus bleibt über den Bildwechsel erhalten (siehe ActiveZoomPreset) - nur
                    ' bei Fit wird wie bisher unbedingt neu eingepasst, bei Actual auf 100% gesprungen,
                    ' bei Manual bleiben Zoom/Pan des Nutzers unverändert stehen.
                    Dim currentImageVm = TryCast(sender, EditorViewModel)
                    Select Case If(currentImageVm IsNot Nothing, currentImageVm.ActiveZoomPreset, ZoomPresetMode.Fit)
                        Case ZoomPresetMode.Fit
                            _zoomInitialized = False
                        Case ZoomPresetMode.Actual
                            _panX = 0
                            _panY = 0
                            SetZoom(ZoomToSlider(100.0))
                        Case Else
                            ' Manual: _zoomSliderValue/_panX/_panY unverändert lassen
                    End Select
                    UpdateSliderLayout()
                Case NameOf(EditorViewModel.DisplayImage)
                    UpdateSliderLayout()
                Case NameOf(EditorViewModel.ShowBeforeImage)
                    UpdateSliderLayout()
                Case NameOf(EditorViewModel.CurrentTool)
                    UpdateCropOverlayVisibility()
                    UpdateTextOverlayVisibility()
                    UpdateSelectionOverlayVisibility()
                Case NameOf(EditorViewModel.SelectedAnnotationIndex),
                     NameOf(EditorViewModel.HasSelectedAnnotation)
                    UpdateTextOverlayVisibility()
                Case NameOf(EditorViewModel.HasActiveSelection),
                     NameOf(EditorViewModel.SelectionXPercent),
                     NameOf(EditorViewModel.SelectionYPercent),
                     NameOf(EditorViewModel.SelectionWidthPercent),
                     NameOf(EditorViewModel.SelectionHeightPercent)
                    UpdateSliderLayout()
                Case NameOf(EditorViewModel.IsPickingColorFromImage)
                    Dim pickCanvas = Me.FindControl(Of Canvas)("PreviewCanvas")
                    Dim vmForPick = TryCast(DataContext, EditorViewModel)
                    If pickCanvas IsNot Nothing AndAlso vmForPick IsNot Nothing Then
                        pickCanvas.Cursor = If(vmForPick.IsPickingColorFromImage, GetPipetteCursor(), Nothing)
                    End If
                Case NameOf(EditorViewModel.CurrentFilmstripIndex)
                    _filmstripController.ScrollToCurrent()
                Case NameOf(EditorViewModel.IsInfoSidebarVisible)
                    UpdateInfoSidebarLayout()
                Case NameOf(EditorViewModel.AnnotationText),
                     NameOf(EditorViewModel.AnnotationFillColor),
                     NameOf(EditorViewModel.AnnotationStrokeColor),
                     NameOf(EditorViewModel.AnnotationFontSize),
                     NameOf(EditorViewModel.AnnotationFontFamily),
                     NameOf(EditorViewModel.AnnotationOpacity),
                     NameOf(EditorViewModel.AnnotationRotation),
                     NameOf(EditorViewModel.AnnotationAnchor),
                     NameOf(EditorViewModel.AnnotationIsVisible),
                     NameOf(EditorViewModel.AnnotationXPercent),
                     NameOf(EditorViewModel.AnnotationYPercent),
                     NameOf(EditorViewModel.AnnotationWidthPercent),
                     NameOf(EditorViewModel.AnnotationHeightPercent),
                     NameOf(EditorViewModel.SelectedAnnotationImagePath),
                     NameOf(EditorViewModel.ShowSelectedSvgOverlay)
                    UpdateSliderLayout()
                Case NameOf(EditorViewModel.CropLeft),
                     NameOf(EditorViewModel.CropTop),
                     NameOf(EditorViewModel.CropRight),
                     NameOf(EditorViewModel.CropBottom),
                     NameOf(EditorViewModel.CropWidthPixels),
                     NameOf(EditorViewModel.CropHeightPixels)
                    UpdateSliderLayout()
            End Select
        End Sub

        Private Sub OnPreviewCanvasSizeChanged(sender As Object, e As SizeChangedEventArgs)
            UpdateSliderLayout()
        End Sub

        ''' Das Vorschaubild (DisplayImage) ist zur Performance auf PreviewMaxDimension herunterskaliert
        ''' und hat deshalb i.d.R. andere Pixelmaße als das Originalbild. Für Zoom-Fit/Anzeige wird hier
        ''' - wenn das Seitenverhältnis übereinstimmt (also nicht beschnitten/geometrisch verändert) -
        ''' auf die echten Bildmaße zurückgegriffen, damit z.B. "100% Zoom" wirklich 1 Bildpixel = 1
        ''' Bildschirmpixel bedeutet. GetDisplayedImageRect MUSS dieselbe Größe verwenden, da sonst
        ''' Klick-/Zieh-Koordinaten (dort in Prozent umgerechnet) nicht mehr zur tatsächlich auf dem
        ''' Bildschirm angezeigten Bildgröße passen.
        Private Function GetEffectiveDisplaySize(vm As EditorViewModel) As Avalonia.Size
            Dim displayBitmap = vm?.DisplayImage
            If displayBitmap Is Nothing Then Return New Avalonia.Size(0, 0)
            Dim imgW = displayBitmap.Size.Width
            Dim imgH = displayBitmap.Size.Height
            If vm.CurrentImage IsNot Nothing Then
                Dim baseW = vm.CurrentImage.Size.Width
                Dim baseH = vm.CurrentImage.Size.Height
                If baseW > 0 AndAlso baseH > 0 AndAlso imgW > 0 AndAlso imgH > 0 Then
                    Dim previewAspect = imgW / imgH
                    Dim baseAspect = baseW / baseH
                    If Math.Abs(previewAspect - baseAspect) < 0.01 Then
                        imgW = baseW
                        imgH = baseH
                    End If
                End If
            End If
            Return New Avalonia.Size(imgW, imgH)
        End Function

        Private Sub UpdateSliderLayout()
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim displayBitmap = vm?.DisplayImage
            If canvas Is Nothing OrElse vm Is Nothing OrElse displayBitmap Is Nothing Then Return

            Dim cw = canvas.Bounds.Width
            Dim ch = canvas.Bounds.Height
            If cw <= 0 OrElse ch <= 0 Then Return

            Dim effectiveSize = GetEffectiveDisplaySize(vm)
            Dim imgW = effectiveSize.Width
            Dim imgH = effectiveSize.Height
            Dim showBefore = vm.ShowBeforeImage

            ' On first load reset zoom to fit-to-window
            If Not _zoomInitialized Then
                _panX = 0
                _panY = 0
                Dim fitPct = Math.Min(cw / imgW, ch / imgH) * 100.0
                If IsOnlyWhenLargerFitBehavior() Then fitPct = Math.Min(fitPct, 100.0)
                _zoomSliderValue = ZoomToSlider(fitPct)
                _ignoreSliderChange = True
                Dim zs = Me.FindControl(Of RoundSlider)("EditorZoomSlider")
                If zs IsNot Nothing Then zs.Value = _zoomSliderValue
                _ignoreSliderChange = False
                UpdateZoomDisplay()
                _zoomInitialized = True
            End If

            Dim scale = SliderToZoom(_zoomSliderValue) / 100.0
            ' Auf ganze Geräte-Pixel runden: Hintergrund-Border (Schachbrettmuster) und die
            ' Bild-Controls teilen sich zwar dieselben Variablen, aber fraktionale Werte können beim
            ' Rendern trotzdem zu einem ~1-2px durchscheinenden Schachbrett-Rand führen, auch bei
            ' komplett opaken Bildern (siehe analoger Fix in ViewerView.ApplyImageFitMode).
            Dim iw = Math.Round(imgW * scale, MidpointRounding.AwayFromZero)
            Dim ih = Math.Round(imgH * scale, MidpointRounding.AwayFromZero)

            ' Clamp pan so image stays partially on screen
            Dim maxPanX = Math.Max(0, (iw - cw) / 2.0)
            Dim maxPanY = Math.Max(0, (ih - ch) / 2.0)
            _panX = Math.Max(-maxPanX, Math.Min(maxPanX, _panX))
            _panY = Math.Max(-maxPanY, Math.Min(maxPanY, _panY))

            Dim ix = Math.Round((cw - iw) / 2.0 + _panX, MidpointRounding.AwayFromZero)
            Dim iy = Math.Round((ch - ih) / 2.0 + _panY, MidpointRounding.AwayFromZero)
            Dim sliderX = ix + iw * _sliderPosition

            Dim backgroundBorder = Me.FindControl(Of Border)("ImageBackgroundBorder")
            If backgroundBorder IsNot Nothing Then
                Avalonia.Controls.Canvas.SetLeft(backgroundBorder, ix)
                Avalonia.Controls.Canvas.SetTop(backgroundBorder, iy)
                backgroundBorder.Width = iw
                backgroundBorder.Height = ih
            End If

            Dim afterImg = Me.FindControl(Of Image)("AfterImageControl")
            If afterImg IsNot Nothing Then
                Avalonia.Controls.Canvas.SetLeft(afterImg, ix)
                Avalonia.Controls.Canvas.SetTop(afterImg, iy)
                afterImg.Width = iw
                afterImg.Height = ih
            End If

            Dim beforeImg = Me.FindControl(Of Image)("BeforeImageControl")
            If beforeImg IsNot Nothing Then
                beforeImg.IsVisible = showBefore
                Avalonia.Controls.Canvas.SetLeft(beforeImg, ix)
                Avalonia.Controls.Canvas.SetTop(beforeImg, iy)
                beforeImg.Width = iw
                beforeImg.Height = ih
                If showBefore Then
                    beforeImg.Clip = New RectangleGeometry(New Avalonia.Rect(0, 0, Math.Max(0, iw * _sliderPosition), ih))
                Else
                    beforeImg.Clip = Nothing
                End If
            End If

            Dim frame = Me.FindControl(Of Border)("ImageFrameBorder")
            If frame IsNot Nothing Then
                Avalonia.Controls.Canvas.SetLeft(frame, ix)
                Avalonia.Controls.Canvas.SetTop(frame, iy)
                frame.Width = iw
                frame.Height = ih
            End If

            Dim divider = Me.FindControl(Of Border)("SliderDivider")
            If divider IsNot Nothing Then
                divider.IsVisible = showBefore
                Avalonia.Controls.Canvas.SetLeft(divider, sliderX - 1)
                Avalonia.Controls.Canvas.SetTop(divider, iy)
                divider.Height = ih
            End If

            Dim handle = Me.FindControl(Of Border)("SliderHandleCircle")
            If handle IsNot Nothing Then
                handle.IsVisible = showBefore
                Avalonia.Controls.Canvas.SetLeft(handle, sliderX - 18)
                Avalonia.Controls.Canvas.SetTop(handle, iy + ih / 2 - 18)
            End If

            Dim beforeLabel = Me.FindControl(Of Border)("BeforeLabel")
            If beforeLabel IsNot Nothing Then
                beforeLabel.IsVisible = showBefore
                Avalonia.Controls.Canvas.SetLeft(beforeLabel, ix + 12)
                Avalonia.Controls.Canvas.SetTop(beforeLabel, iy + 8)
            End If

            Dim afterLabel = Me.FindControl(Of Border)("AfterLabel")
            If afterLabel IsNot Nothing Then
                afterLabel.IsVisible = showBefore
                Avalonia.Controls.Canvas.SetLeft(afterLabel, ix + iw - 90)
                Avalonia.Controls.Canvas.SetTop(afterLabel, iy + 8)
            End If

            If Not _isCropDragging Then
                PositionCropOverlayFromViewModel(ix, iy, iw, ih)
            End If
            If Not _isTextDragging Then
                PositionTextOverlayFromViewModel(ix, iy, iw, ih, scale)
            End If
            If Not _isSelectionDragging Then
                PositionSelectionOverlayFromViewModel(ix, iy, iw, ih)
            End If
        End Sub

        Private Sub OnSliderPointerPressed(sender As Object, e As PointerPressedEventArgs)
            If Not e.GetCurrentPoint(Nothing).Properties.IsLeftButtonPressed Then Return
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            If canvas Is Nothing Then Return
            Dim vm = TryCast(DataContext, EditorViewModel)

            If vm IsNot Nothing AndAlso vm.IsPickingColorFromImage Then
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                Dim sampledColor = SampleDisplayedColor(vm, imageRect, e.GetPosition(canvas))
                If sampledColor.HasValue Then vm.CompleteColorPick(sampledColor.Value) Else vm.CancelColorPick()
                e.Handled = True
                Return
            End If

            If _isPanMode Then
                Dim pos = e.GetPosition(canvas)
                _panStartX = pos.X
                _panStartY = pos.Y
                _panStartOffsetX = _panX
                _panStartOffsetY = _panY
                _isPanDragging = True
                e.Pointer.Capture(canvas)
                e.Handled = True
                Return
            End If

            If vm IsNot Nothing AndAlso vm.CurrentTool = EditorTool.Crop Then
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
                Dim rawPos = e.GetPosition(canvas)
                Dim pos = ClampPointToRect(rawPos, imageRect)
                _cropDragMode = GetCropDragMode(rawPos)
                If _cropDragMode = CropDragMode.None Then
                    ' Ein Klick auf ein vorhandenes Objekt soll dieses selektieren (und dabei automatisch
                    ' ins passende Werkzeug wechseln, siehe SelectedAnnotationIndex-Setter im ViewModel)
                    ' statt sofort eine neue Freistellungsauswahl aufzuziehen - sonst lässt sich ein Objekt
                    ' nie per Klick anwählen, solange noch "Zuschneiden" (der Standard-Werkzeug) aktiv ist.
                    Dim xPct = (pos.X - imageRect.Left) / imageRect.Width * 100.0
                    Dim yPct = (pos.Y - imageRect.Top) / imageRect.Height * 100.0
                    Const hitSlopPixels As Double = 10.0
                    Dim hitSlopXPercent = hitSlopPixels / imageRect.Width * 100.0
                    Dim hitSlopYPercent = hitSlopPixels / imageRect.Height * 100.0
                    Dim hitIndex = vm.HitTestAnnotation(xPct, yPct, hitSlopXPercent, hitSlopYPercent)
                    If hitIndex >= 0 Then
                        vm.SelectedAnnotationIndex = hitIndex
                        If vm.CurrentTool = EditorTool.Text Then FocusTextOverlayEditor()
                        e.Handled = True
                        Return
                    End If
                    _cropDragMode = CropDragMode.NewSelection
                    _cropStart = pos
                    _cropEnd = pos
                Else
                    _cropDragInitialRect = GetCropOverlayRect()
                    _cropDragPointerStart = rawPos
                    RectToCropPoints(_cropDragInitialRect, _cropStart, _cropEnd)
                End If
                _isCropDragging = True
                e.Pointer.Capture(canvas)
                UpdateCropOverlayFromDrag()
                e.Handled = True
                Return
            End If

            If vm IsNot Nothing AndAlso vm.CurrentTool = EditorTool.Selection Then
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
                Dim pos = ClampPointToRect(e.GetPosition(canvas), imageRect)
                _selectionStart = pos
                _selectionEnd = pos
                _isSelectionDragging = True
                e.Pointer.Capture(canvas)
                UpdateSelectionOverlayFromDrag()
                e.Handled = True
                Return
            End If

            If vm IsNot Nothing AndAlso vm.CurrentTool = EditorTool.Retouch Then
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
                Dim pos = ClampPointToRect(e.GetPosition(canvas), imageRect)
                Dim xPct = (pos.X - imageRect.Left) / imageRect.Width * 100.0
                Dim yPct = (pos.Y - imageRect.Top) / imageRect.Height * 100.0
                vm.AddRetouchSpot(xPct, yPct)
                _lastRetouchPoint = New Avalonia.Point(xPct, yPct)
                _isRetouching = True
                e.Pointer.Capture(canvas)
                e.Handled = True
                Return
            End If

            If vm IsNot Nothing AndAlso vm.CurrentTool = EditorTool.Draw AndAlso String.IsNullOrEmpty(vm.PendingInsertKind) Then
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
                _brushPoints.Clear()
                AddBrushPoint(e.GetPosition(canvas), imageRect)
                _isBrushDrawing = True
                ShowBrushPreviewLine(True)
                UpdateBrushPreviewLine(imageRect, vm)
                e.Pointer.Capture(canvas)
                e.Handled = True
                Return
            End If

            If vm IsNot Nothing AndAlso vm.HasSelectedAnnotation Then
                ' Die Resize-/Rotier-Griffe ragen per Margin über die Bounds des TextOverlay-Borders hinaus,
                ' daher landen Klicks genau darauf hier auf dem Canvas statt auf dem Border - Griff-Erkennung
                ' deshalb zusätzlich hier prüfen, bevor ein Klick als "Objekt selektieren/anlegen" interpretiert wird.
                Dim overlayForHandles = Me.FindControl(Of Border)("TextOverlay")
                If overlayForHandles IsNot Nothing Then
                    Dim handleRect = GetTextOverlayRect()
                    Dim handleMode = GetTextDragMode(e.GetPosition(canvas), handleRect, vm.AnnotationRotation)
                    If handleMode <> TextDragMode.None AndAlso handleMode <> TextDragMode.Move Then
                        OnTextOverlayPointerPressed(overlayForHandles, e)
                        Return
                    End If
                End If
            End If

            If vm IsNot Nothing Then
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                If imageRect.Width > 0 AndAlso imageRect.Height > 0 Then
                    Dim pos = ClampPointToRect(e.GetPosition(canvas), imageRect)
                    Dim xPct = (pos.X - imageRect.Left) / imageRect.Width * 100.0
                    Dim yPct = (pos.Y - imageRect.Top) / imageRect.Height * 100.0
                    Const hitSlopPixels As Double = 10.0
                    Dim hitSlopXPercent = hitSlopPixels / imageRect.Width * 100.0
                    Dim hitSlopYPercent = hitSlopPixels / imageRect.Height * 100.0
                    Dim hitIndex = vm.HitTestAnnotation(xPct, yPct, hitSlopXPercent, hitSlopYPercent)

                    If hitIndex >= 0 Then
                        vm.SelectedAnnotationIndex = hitIndex
                        If vm.CurrentTool = EditorTool.Text Then FocusTextOverlayEditor()
                        e.Handled = True
                        Return
                    ElseIf Not String.IsNullOrEmpty(vm.PendingInsertKind) Then
                        Dim pendingKind = vm.PendingInsertKind
                        vm.PendingInsertKind = ""
                        If pendingKind = "Image" Then
                            PlacePendingImageAsync(xPct, yPct)
                        Else
                            vm.AddAnnotationAt(pendingKind, xPct, yPct)
                            If vm.CurrentTool = EditorTool.Text Then FocusTextOverlayEditor()
                        End If
                        e.Handled = True
                        Return
                    ElseIf vm.HasSelectedAnnotation Then
                        vm.SelectedAnnotationIndex = -1
                        If IsLayerPlacementTool(vm.CurrentTool) Then
                            e.Handled = True
                            Return
                        End If
                    ElseIf IsLayerPlacementTool(vm.CurrentTool) Then
                        e.Handled = True
                        Return
                    End If
                End If
            End If

            If vm IsNot Nothing AndAlso Not vm.ShowBeforeImage Then
                Return
            End If

            _isDraggingSlider = True
            e.Pointer.Capture(canvas)
            MoveSlider(e.GetPosition(canvas).X, canvas)
            e.Handled = True
        End Sub

        Private Sub OnSliderPointerMoved(sender As Object, e As PointerEventArgs)
            Dim cursorCanvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim cursorVm = TryCast(DataContext, EditorViewModel)
            If cursorCanvas IsNot Nothing AndAlso cursorVm IsNot Nothing Then
                UpdateBrushCursorPreview(e.GetPosition(cursorCanvas), GetDisplayedImageRect(cursorCanvas, cursorVm), cursorVm)
                UpdateMousePositionText(e.GetPosition(cursorCanvas), GetDisplayedImageRect(cursorCanvas, cursorVm), cursorVm)
            End If

            ' Die Ecken der Resize-Griffe und der Rotier-Griff ragen per negativem Margin über die
            ' Bounds des TextOverlay-Borders hinaus (siehe Kommentar in OnSliderPointerPressed) -
            ' Zeigerpositionen dort landen direkt auf diesem Canvas statt auf dem Border, weshalb
            ' der passende Cursor hier zusätzlich zu UpdateTextOverlayHoverCursor gesetzt werden muss.
            If cursorCanvas IsNot Nothing AndAlso cursorVm IsNot Nothing AndAlso cursorVm.IsPickingColorFromImage Then
                ' Muss vor den übrigen Zweigen geprüft werden - sonst überschreibt der ElseIf-Fallback
                ' unten bei jeder Mausbewegung den einmalig in OnViewModelPropertyChanged gesetzten
                ' Pipetten-Cursor sofort wieder mit Nothing.
                cursorCanvas.Cursor = GetPipetteCursor()
            ElseIf Not _isPanDragging AndAlso Not _isCropDragging AndAlso Not _isBrushDrawing AndAlso
               Not _isRetouching AndAlso Not _isTextDragging AndAlso Not _isDraggingSlider AndAlso Not _isSelectionDragging AndAlso
               cursorCanvas IsNot Nothing AndAlso cursorVm IsNot Nothing AndAlso
               cursorVm.HasSelectedAnnotation AndAlso IsLayerPlacementTool(cursorVm.CurrentTool) Then
                Dim mode = GetTextDragMode(e.GetPosition(cursorCanvas), GetTextOverlayRect(), cursorVm.AnnotationRotation)
                cursorCanvas.Cursor = GetCursorForTextDragMode(mode, IsSelectedAnnotationTextLayer(cursorVm))
            ElseIf cursorCanvas IsNot Nothing Then
                cursorCanvas.Cursor = Nothing
            End If

            If _isPanDragging Then
                Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
                If canvas Is Nothing Then Return
                Dim pos = e.GetPosition(canvas)
                _panX = _panStartOffsetX + (pos.X - _panStartX)
                _panY = _panStartOffsetY + (pos.Y - _panStartY)
                UpdateSliderLayout()
                e.Handled = True
                Return
            End If
            If _isCropDragging Then
                Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
                Dim vm = TryCast(DataContext, EditorViewModel)
                If canvas Is Nothing OrElse vm Is Nothing Then Return
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                UpdateCropDrag(e.GetPosition(canvas), imageRect)
                UpdateCropOverlayFromDrag()
                e.Handled = True
                Return
            End If
            If _isBrushDrawing Then
                Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
                Dim vm = TryCast(DataContext, EditorViewModel)
                If canvas Is Nothing OrElse vm Is Nothing Then Return
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                AddBrushPoint(e.GetPosition(canvas), imageRect)
                UpdateBrushPreviewLine(imageRect, vm)
                e.Handled = True
                Return
            End If
            If _isRetouching Then
                Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
                Dim vm = TryCast(DataContext, EditorViewModel)
                If canvas Is Nothing OrElse vm Is Nothing Then Return
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
                Dim pos = ClampPointToRect(e.GetPosition(canvas), imageRect)
                Dim xPct = (pos.X - imageRect.Left) / imageRect.Width * 100.0
                Dim yPct = (pos.Y - imageRect.Top) / imageRect.Height * 100.0
                If Math.Abs(_lastRetouchPoint.X - xPct) >= 0.4 OrElse Math.Abs(_lastRetouchPoint.Y - yPct) >= 0.4 Then
                    vm.AddRetouchSpot(xPct, yPct, captureUndo:=False)
                    _lastRetouchPoint = New Avalonia.Point(xPct, yPct)
                End If
                e.Handled = True
                Return
            End If
            If _isSelectionDragging Then
                Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
                Dim vm = TryCast(DataContext, EditorViewModel)
                If canvas Is Nothing OrElse vm Is Nothing Then Return
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
                _selectionEnd = ClampPointToRect(e.GetPosition(canvas), imageRect)
                UpdateSelectionOverlayFromDrag()
                e.Handled = True
                Return
            End If
            If Not _isDraggingSlider Then Return
            Dim cv = Me.FindControl(Of Canvas)("PreviewCanvas")
            If cv Is Nothing Then Return
            MoveSlider(e.GetPosition(cv).X, cv)
            e.Handled = True
        End Sub

        Private Sub OnSliderPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            Dim wasCropDragging = _isCropDragging
            If _isCropDragging Then
                CommitCropDrag()
            End If
            If _isSelectionDragging Then
                CommitSelectionDrag()
            End If
            _isDraggingSlider = False
            _isPanDragging = False
            _isCropDragging = False
            _isSelectionDragging = False
            If _isBrushDrawing Then
                Dim vm = TryCast(DataContext, EditorViewModel)
                vm?.AddBrushStroke(_brushPoints, vm.IsEraserMode)
                _brushPoints.Clear()
                ShowBrushPreviewLine(False)
            End If
            _isBrushDrawing = False
            _isRetouching = False
            _cropDragMode = CropDragMode.None
            e.Pointer.Capture(Nothing)

            ' Ein zu kleiner Zieh-Vorgang (z. B. ein bloßer Klick) wird von CommitCropDrag verworfen,
            ' ohne den ViewModel-Zuschnitt zu ändern - der Overlay muss dann wieder auf den tatsächlichen
            ' Zuschnitt zurückgesetzt werden, sonst bleiben die Anfasspunkte an der kollabierten Vorschau hängen.
            If wasCropDragging Then
                UpdateSliderLayout()
            End If
        End Sub

        Private Sub AddBrushPoint(position As Avalonia.Point, imageRect As Avalonia.Rect)
            If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
            Dim pos = ClampPointToRect(position, imageRect)
            Dim xPct = (pos.X - imageRect.Left) / imageRect.Width * 100.0
            Dim yPct = (pos.Y - imageRect.Top) / imageRect.Height * 100.0
            If _brushPoints.Count > 0 Then
                Dim last = _brushPoints(_brushPoints.Count - 1)
                If Math.Abs(last.X - xPct) < 0.15 AndAlso Math.Abs(last.Y - yPct) < 0.15 Then Return
            End If
            _brushPoints.Add(New Avalonia.Point(xPct, yPct))
        End Sub

        Private Sub ShowBrushPreviewLine(visible As Boolean)
            Dim line = Me.FindControl(Of Polyline)("BrushPreviewLine")
            If line Is Nothing Then Return
            line.IsVisible = visible
            If Not visible Then line.Points = New Avalonia.Collections.AvaloniaList(Of Avalonia.Point)()
        End Sub

        Private Sub UpdateBrushPreviewLine(imageRect As Avalonia.Rect, vm As EditorViewModel)
            If Not _isBrushDrawing Then Return
            Dim line = Me.FindControl(Of Polyline)("BrushPreviewLine")
            If line Is Nothing OrElse imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return

            Dim pts As New Avalonia.Collections.AvaloniaList(Of Avalonia.Point)()
            For Each p In _brushPoints
                pts.Add(New Avalonia.Point(imageRect.Left + p.X / 100.0 * imageRect.Width, imageRect.Top + p.Y / 100.0 * imageRect.Height))
            Next
            line.Points = pts

            Dim isEraser = vm.IsEraserMode
            Dim diameterPx = Math.Max(1.0, Math.Min(imageRect.Width, imageRect.Height) * vm.BrushSize / 100.0)
            line.StrokeThickness = diameterPx
            If isEraser Then
                line.Stroke = New SolidColorBrush(Colors.White)
                line.StrokeDashArray = New Avalonia.Collections.AvaloniaList(Of Double) From {4, 3}
                line.Opacity = 0.85
            Else
                line.Stroke = vm.AnnotationStrokeBrush
                line.StrokeDashArray = Nothing
                line.Opacity = Math.Max(0.15, vm.BrushOpacity / 100.0)
            End If
        End Sub

        Private Sub UpdateBrushCursorPreview(position As Avalonia.Point, imageRect As Avalonia.Rect, vm As EditorViewModel)
            Dim cursor = Me.FindControl(Of Ellipse)("BrushCursorPreview")
            If cursor Is Nothing Then Return

            Dim showCursor = imageRect.Width > 0 AndAlso imageRect.Height > 0 AndAlso
                              (vm.CurrentTool = EditorTool.Draw OrElse vm.CurrentTool = EditorTool.Retouch)
            cursor.IsVisible = showCursor
            If Not showCursor Then Return

            Dim percent = If(vm.CurrentTool = EditorTool.Draw, vm.BrushSize, vm.RetouchRadius)
            Dim diameter = Math.Max(4.0, Math.Min(imageRect.Width, imageRect.Height) * percent / 100.0 * 2.0)
            cursor.Width = diameter
            cursor.Height = diameter
            Canvas.SetLeft(cursor, position.X - diameter / 2.0)
            Canvas.SetTop(cursor, position.Y - diameter / 2.0)
        End Sub

        Private Sub UpdateCropOverlayVisibility()
            Dim overlay = Me.FindControl(Of Border)("CropOverlay")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If overlay IsNot Nothing Then overlay.IsVisible = vm IsNot Nothing AndAlso vm.CurrentTool = EditorTool.Crop
            UpdateSliderLayout()
        End Sub

        Private Sub UpdateTextOverlayVisibility()
            Dim overlay = Me.FindControl(Of Border)("TextOverlay")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If overlay IsNot Nothing Then overlay.IsVisible = vm IsNot Nothing AndAlso IsLayerPlacementTool(vm.CurrentTool) AndAlso vm.HasSelectedAnnotation
            UpdateSliderLayout()
        End Sub

        Private Sub UpdateInfoSidebarLayout()
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim root = Me.FindControl(Of Grid)("EditorRootGrid")
            Dim sidebar = Me.FindControl(Of Border)("InfoSidebarBorder")
            If root Is Nothing Then Return

            If root.ColumnDefinitions.Count >= 4 Then
                root.ColumnDefinitions(3).Width = If(vm IsNot Nothing AndAlso vm.IsInfoSidebarVisible, New GridLength(300), New GridLength(0))
            End If

            If sidebar IsNot Nothing Then
                sidebar.IsVisible = vm IsNot Nothing AndAlso vm.IsInfoSidebarVisible
            End If
        End Sub

        Public Sub OnAdjustmentExpanderExpanded(sender As Object, e As RoutedEventArgs)
            Dim expanded = TryCast(sender, Expander)
            If expanded Is Nothing Then Return

            Dim stack = Me.FindControl(Of Panel)("AdjustmentsStackPanel")
            If stack Is Nothing Then Return

            For Each child In stack.Children
                Dim other = TryCast(child, Expander)
                If other IsNot Nothing AndAlso Not Object.ReferenceEquals(other, expanded) Then
                    other.IsExpanded = False
                End If
            Next
        End Sub

        ''' <summary>Rechnet die Canvas-Zeigerposition in eine Bildpixel-Koordinate um (für die
        ''' Fußleisten-Anzeige) - gleiche Umrechnung wie bei der Pipette (rect-relative Position,
        ''' skaliert auf die echte CurrentImage.PixelSize statt der ggf. verkleinerten Vorschau).</summary>
        Private Sub UpdateMousePositionText(pointerPos As Avalonia.Point, imageRect As Avalonia.Rect, vm As EditorViewModel)
            If vm Is Nothing OrElse vm.CurrentImage Is Nothing OrElse imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
            If Not imageRect.Contains(pointerPos) Then
                vm.MousePositionText = ""
                Return
            End If
            Dim px = CInt(Math.Round((pointerPos.X - imageRect.Left) / imageRect.Width * vm.CurrentImage.PixelSize.Width))
            Dim py = CInt(Math.Round((pointerPos.Y - imageRect.Top) / imageRect.Height * vm.CurrentImage.PixelSize.Height))
            vm.MousePositionText = $"{px}, {py}"
        End Sub

        Private Sub OnPreviewCanvasPointerExited(sender As Object, e As PointerEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm IsNot Nothing Then vm.MousePositionText = ""
        End Sub

        Private Function GetDisplayedImageRect(canvas As Canvas, vm As EditorViewModel) As Avalonia.Rect
            Dim displayBitmap = vm?.DisplayImage
            If canvas Is Nothing OrElse vm Is Nothing OrElse displayBitmap Is Nothing Then Return New Avalonia.Rect()
            Dim effectiveSize = GetEffectiveDisplaySize(vm)
            Dim scale = SliderToZoom(_zoomSliderValue) / 100.0
            Dim iw = effectiveSize.Width * scale
            Dim ih = effectiveSize.Height * scale
            Dim ix = (canvas.Bounds.Width - iw) / 2.0 + _panX
            Dim iy = (canvas.Bounds.Height - ih) / 2.0 + _panY
            Return New Avalonia.Rect(ix, iy, iw, ih)
        End Function

        Private Function ClampPointToRect(point As Avalonia.Point, rect As Avalonia.Rect) As Avalonia.Point
            Return New Avalonia.Point(Math.Max(rect.Left, Math.Min(rect.Right, point.X)),
                                      Math.Max(rect.Top, Math.Min(rect.Bottom, point.Y)))
        End Function

        Private Shared _pipetteCursor As Cursor = Nothing

        ''' Rastert den Pipetten-Mauszeiger einmalig aus dem Outline-SVG-Asset und cached ihn -
        ''' Cursor-Erstellung ist nicht ganz billig und ändert sich nie.
        Private Shared Function GetPipetteCursor() As Cursor
            If _pipetteCursor Is Nothing Then
                Try
                    _pipetteCursor = CreateCursorFromSvgAsset(
                        "avares://FerrumPix/Assets/Icons/outline/color-picker.svg",
                        New PixelPoint(5, 27))
                Catch
                    _pipetteCursor = New Cursor(StandardCursorType.Cross)
                End Try
            End If
            Return _pipetteCursor
        End Function

        Private Shared Function CreateCursorFromSvgAsset(source As String, hotspot As PixelPoint, Optional canvasSize As Integer = 32) As Cursor
            Dim resolvedSource = SvgIcon.ResolveIconSource(source)
            Using stream = Avalonia.Platform.AssetLoader.Open(New Uri(resolvedSource))
                Using svg As New SKSvg()
                    Dim picture = svg.Load(stream)
                    If picture Is Nothing Then Throw New InvalidOperationException("Cursor SVG could not be loaded.")

                    Dim bounds = picture.CullRect
                    If bounds.Width <= 0 OrElse bounds.Height <= 0 Then Throw New InvalidOperationException("Cursor SVG has invalid bounds.")

                    Dim padding = 2.0F
                    Dim scale = Math.Min((canvasSize - padding * 2) / bounds.Width, (canvasSize - padding * 2) / bounds.Height)
                    Dim offsetX = (canvasSize - bounds.Width * scale) / 2.0F - bounds.Left * scale
                    Dim offsetY = (canvasSize - bounds.Height * scale) / 2.0F - bounds.Top * scale

                    Using surface = SKSurface.Create(New SKImageInfo(canvasSize, canvasSize, SKColorType.Bgra8888, SKAlphaType.Premul))
                        Dim canvas = surface.Canvas
                        canvas.Clear(SKColors.Transparent)
                        canvas.Translate(offsetX, offsetY)
                        canvas.Scale(scale)
                        canvas.DrawPicture(picture)
                        canvas.Flush()

                        Using image = surface.Snapshot()
                            Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                                Using bitmapStream As New MemoryStream(data.ToArray())
                                    Dim bitmap = New Bitmap(bitmapStream)
                                    Return New Cursor(bitmap, hotspot)
                                End Using
                            End Using
                        End Using
                    End Using
                End Using
            End Using
        End Function

        ''' Pipette: nimmt die Farbe an der Klickposition direkt aus dem aktuell angezeigten
        ''' DisplayImage (voll bearbeitete Live-Vorschau inkl. Objekte) - "what you see is what you
        ''' get". Avalonia-Bitmaps erlauben keinen direkten Pixelzugriff, daher einmalig (nur bei
        ''' diesem expliziten Nutzerklick, nicht pro Frame) über PNG-Bytes nach SkiaSharp dekodiert.
        Private Function SampleDisplayedColor(vm As EditorViewModel, imageRect As Avalonia.Rect, screenPoint As Avalonia.Point) As Color?
            Dim bitmap = vm?.DisplayImage
            If bitmap Is Nothing OrElse imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return Nothing
            Dim pos = ClampPointToRect(screenPoint, imageRect)
            Dim xPct = (pos.X - imageRect.Left) / imageRect.Width
            Dim yPct = (pos.Y - imageRect.Top) / imageRect.Height

            Try
                Using ms = New MemoryStream()
                    bitmap.Save(ms)
                    ms.Seek(0, SeekOrigin.Begin)
                    Using decoded = SKBitmap.Decode(ms)
                        If decoded Is Nothing Then Return Nothing
                        Dim px = Math.Max(0, Math.Min(decoded.Width - 1, CInt(xPct * decoded.Width)))
                        Dim py = Math.Max(0, Math.Min(decoded.Height - 1, CInt(yPct * decoded.Height)))
                        Dim sampled = decoded.GetPixel(px, py)
                        Return Color.FromArgb(sampled.Alpha, sampled.Red, sampled.Green, sampled.Blue)
                    End Using
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        Private Sub UpdateSelectionOverlayFromDrag()
            Dim overlay = Me.FindControl(Of Border)("SelectionOverlay")
            If overlay Is Nothing Then Return
            Dim left = Math.Min(_selectionStart.X, _selectionEnd.X)
            Dim top = Math.Min(_selectionStart.Y, _selectionEnd.Y)
            Dim width = Math.Abs(_selectionEnd.X - _selectionStart.X)
            Dim height = Math.Abs(_selectionEnd.Y - _selectionStart.Y)
            overlay.IsVisible = True
            Avalonia.Controls.Canvas.SetLeft(overlay, left)
            Avalonia.Controls.Canvas.SetTop(overlay, top)
            overlay.Width = Math.Max(1, width)
            overlay.Height = Math.Max(1, height)
        End Sub

        ''' Repositioniert das Auswahl-Overlay anhand der im ViewModel gespeicherten Auswahl (Prozent-
        ''' vom-Bild), z.B. nach Zoom/Pan-Änderungen - analog zu PositionCropOverlayFromViewModel.
        Private Sub PositionSelectionOverlayFromViewModel(ix As Double, iy As Double, iw As Double, ih As Double)
            Dim overlay = Me.FindControl(Of Border)("SelectionOverlay")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If overlay Is Nothing OrElse vm Is Nothing OrElse vm.CurrentTool <> EditorTool.Selection OrElse Not vm.HasActiveSelection Then Return
            Dim left = ix + iw * vm.SelectionXPercent / 100.0
            Dim top = iy + ih * vm.SelectionYPercent / 100.0
            overlay.IsVisible = True
            Avalonia.Controls.Canvas.SetLeft(overlay, left)
            Avalonia.Controls.Canvas.SetTop(overlay, top)
            overlay.Width = Math.Max(1, iw * vm.SelectionWidthPercent / 100.0)
            overlay.Height = Math.Max(1, ih * vm.SelectionHeightPercent / 100.0)
        End Sub

        Private Sub UpdateSelectionOverlayVisibility()
            Dim overlay = Me.FindControl(Of Border)("SelectionOverlay")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If overlay IsNot Nothing Then overlay.IsVisible = vm IsNot Nothing AndAlso vm.CurrentTool = EditorTool.Selection AndAlso vm.HasActiveSelection
            UpdateSliderLayout()
        End Sub

        Private Sub CommitSelectionDrag()
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas Is Nothing OrElse vm Is Nothing Then Return
            Dim rect = GetDisplayedImageRect(canvas, vm)
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return
            Dim leftPx = Math.Min(_selectionStart.X, _selectionEnd.X)
            Dim topPx = Math.Min(_selectionStart.Y, _selectionEnd.Y)
            Dim rightPx = Math.Max(_selectionStart.X, _selectionEnd.X)
            Dim bottomPx = Math.Max(_selectionStart.Y, _selectionEnd.Y)
            If Math.Abs(rightPx - leftPx) < 8 OrElse Math.Abs(bottomPx - topPx) < 8 Then Return

            Dim xPercent = (leftPx - rect.Left) / rect.Width * 100.0
            Dim yPercent = (topPx - rect.Top) / rect.Height * 100.0
            Dim widthPercent = (rightPx - leftPx) / rect.Width * 100.0
            Dim heightPercent = (bottomPx - topPx) / rect.Height * 100.0
            vm.SetSelectionRect(xPercent, yPercent, widthPercent, heightPercent)
        End Sub

        Private Sub UpdateCropOverlayFromDrag()
            Dim overlay = Me.FindControl(Of Border)("CropOverlay")
            If overlay Is Nothing Then Return
            Dim left = Math.Min(_cropStart.X, _cropEnd.X)
            Dim top = Math.Min(_cropStart.Y, _cropEnd.Y)
            Dim width = Math.Abs(_cropEnd.X - _cropStart.X)
            Dim height = Math.Abs(_cropEnd.Y - _cropStart.Y)
            overlay.IsVisible = True
            Avalonia.Controls.Canvas.SetLeft(overlay, left)
            Avalonia.Controls.Canvas.SetTop(overlay, top)
            overlay.Width = Math.Max(1, width)
            overlay.Height = Math.Max(1, height)
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas IsNot Nothing AndAlso vm IsNot Nothing Then
                UpdateCropSizeBadge(New Avalonia.Rect(left, top, width, height), GetDisplayedImageRect(canvas, vm), vm)
            End If
        End Sub

        Private Sub PositionCropOverlayFromViewModel(ix As Double, iy As Double, iw As Double, ih As Double)
            Dim overlay = Me.FindControl(Of Border)("CropOverlay")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If overlay Is Nothing OrElse vm Is Nothing OrElse vm.CurrentTool <> EditorTool.Crop Then Return
            Dim left = ix + iw * vm.CropLeft / 100.0
            Dim top = iy + ih * vm.CropTop / 100.0
            Dim right = ix + iw * (1.0 - vm.CropRight / 100.0)
            Dim bottom = iy + ih * (1.0 - vm.CropBottom / 100.0)
            overlay.IsVisible = True
            Avalonia.Controls.Canvas.SetLeft(overlay, left)
            Avalonia.Controls.Canvas.SetTop(overlay, top)
            overlay.Width = Math.Max(1, right - left)
            overlay.Height = Math.Max(1, bottom - top)
            UpdateCropSizeBadge(New Avalonia.Rect(left, top, right - left, bottom - top),
                                New Avalonia.Rect(ix, iy, iw, ih),
                                vm)
        End Sub

        Private Sub UpdateCropSizeBadge(cropRect As Avalonia.Rect, imageRect As Avalonia.Rect, vm As EditorViewModel)
            Dim badge = Me.FindControl(Of TextBlock)("CropSizeBadgeText")
            If badge Is Nothing OrElse vm Is Nothing OrElse vm.CurrentImage Is Nothing Then Return
            If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then
                badge.Text = ""
                Return
            End If

            Dim width = CInt(Math.Round(Math.Max(1, cropRect.Width / imageRect.Width * vm.CurrentImage.PixelSize.Width)))
            Dim height = CInt(Math.Round(Math.Max(1, cropRect.Height / imageRect.Height * vm.CurrentImage.PixelSize.Height)))
            badge.Text = $"{width} × {height} px"
        End Sub

        Private Function GetCropOverlayRect() As Avalonia.Rect
            Dim overlay = Me.FindControl(Of Border)("CropOverlay")
            If overlay Is Nothing OrElse Not overlay.IsVisible Then Return New Avalonia.Rect()
            Dim left = Avalonia.Controls.Canvas.GetLeft(overlay)
            Dim top = Avalonia.Controls.Canvas.GetTop(overlay)
            If Double.IsNaN(left) Then left = 0
            If Double.IsNaN(top) Then top = 0
            Return New Avalonia.Rect(left, top, Math.Max(0, overlay.Bounds.Width), Math.Max(0, overlay.Bounds.Height))
        End Function

        Private Sub RectToCropPoints(rect As Avalonia.Rect, ByRef startPoint As Avalonia.Point, ByRef endPoint As Avalonia.Point)
            startPoint = New Avalonia.Point(rect.Left, rect.Top)
            endPoint = New Avalonia.Point(rect.Right, rect.Bottom)
        End Sub

        Private Function GetCropDragMode(point As Avalonia.Point) As CropDragMode
            Dim rect = GetCropOverlayRect()
            Const hitSlop As Double = 18
            If rect.Width < 8 OrElse rect.Height < 8 Then Return CropDragMode.None
            Dim hitRect = rect.Inflate(hitSlop)
            If Not hitRect.Contains(point) Then Return CropDragMode.None

            Const handleSize As Double = 20
            Dim nearLeft = Math.Abs(point.X - rect.Left) <= handleSize
            Dim nearRight = Math.Abs(point.X - rect.Right) <= handleSize
            Dim nearTop = Math.Abs(point.Y - rect.Top) <= handleSize
            Dim nearBottom = Math.Abs(point.Y - rect.Bottom) <= handleSize

            If nearLeft AndAlso nearTop Then Return CropDragMode.TopLeft
            If nearRight AndAlso nearTop Then Return CropDragMode.TopRight
            If nearLeft AndAlso nearBottom Then Return CropDragMode.BottomLeft
            If nearRight AndAlso nearBottom Then Return CropDragMode.BottomRight
            If nearLeft Then Return CropDragMode.Left
            If nearRight Then Return CropDragMode.Right
            If nearTop Then Return CropDragMode.Top
            If nearBottom Then Return CropDragMode.Bottom
            If Not rect.Contains(point) Then Return CropDragMode.None
            Return CropDragMode.Move
        End Function

        Private Sub UpdateCropDrag(pointerPosition As Avalonia.Point, imageRect As Avalonia.Rect)
            If _cropDragMode = CropDragMode.NewSelection Then
                _cropEnd = ClampPointToRect(pointerPosition, imageRect)
                Return
            End If

            Dim dx = pointerPosition.X - _cropDragPointerStart.X
            Dim dy = pointerPosition.Y - _cropDragPointerStart.Y
            Dim left = _cropDragInitialRect.Left
            Dim top = _cropDragInitialRect.Top
            Dim right = _cropDragInitialRect.Right
            Dim bottom = _cropDragInitialRect.Bottom
            Const minSize As Double = 12

            Select Case _cropDragMode
                Case CropDragMode.Move
                    Dim width = _cropDragInitialRect.Width
                    Dim height = _cropDragInitialRect.Height
                    left = Math.Max(imageRect.Left, Math.Min(imageRect.Right - width, left + dx))
                    top = Math.Max(imageRect.Top, Math.Min(imageRect.Bottom - height, top + dy))
                    right = left + width
                    bottom = top + height
                Case CropDragMode.Left, CropDragMode.TopLeft, CropDragMode.BottomLeft
                    left = Math.Max(imageRect.Left, Math.Min(right - minSize, left + dx))
                Case CropDragMode.Right, CropDragMode.TopRight, CropDragMode.BottomRight
                    right = Math.Min(imageRect.Right, Math.Max(left + minSize, right + dx))
            End Select

            Select Case _cropDragMode
                Case CropDragMode.Top, CropDragMode.TopLeft, CropDragMode.TopRight
                    top = Math.Max(imageRect.Top, Math.Min(bottom - minSize, top + dy))
                Case CropDragMode.Bottom, CropDragMode.BottomLeft, CropDragMode.BottomRight
                    bottom = Math.Min(imageRect.Bottom, Math.Max(top + minSize, bottom + dy))
            End Select

            _cropStart = New Avalonia.Point(left, top)
            _cropEnd = New Avalonia.Point(right, bottom)
        End Sub

        Private Sub CommitCropDrag()
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas Is Nothing OrElse vm Is Nothing Then Return
            Dim rect = GetDisplayedImageRect(canvas, vm)
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return
            Dim leftPx = Math.Min(_cropStart.X, _cropEnd.X)
            Dim topPx = Math.Min(_cropStart.Y, _cropEnd.Y)
            Dim rightPx = Math.Max(_cropStart.X, _cropEnd.X)
            Dim bottomPx = Math.Max(_cropStart.Y, _cropEnd.Y)
            If Math.Abs(rightPx - leftPx) < 8 OrElse Math.Abs(bottomPx - topPx) < 8 Then Return

            Dim left = (leftPx - rect.Left) / rect.Width * 100.0
            Dim top = (topPx - rect.Top) / rect.Height * 100.0
            Dim right = (rect.Right - rightPx) / rect.Width * 100.0
            Dim bottom = (rect.Bottom - bottomPx) / rect.Height * 100.0
            vm.SetCropPercentages(left, top, right, bottom)
        End Sub

        Private Sub FocusTextOverlayEditor()
            Dispatcher.UIThread.Post(Sub()
                                          Dim editor = Me.FindControl(Of TextBox)("TextOverlayEditor")
                                          If editor IsNot Nothing AndAlso editor.IsVisible Then
                                              editor.Focus()
                                              editor.CaretIndex = If(editor.Text, "").Length
                                          End If
                                      End Sub, DispatcherPriority.Background)
        End Sub

        Private Sub PositionTextOverlayFromViewModel(ix As Double, iy As Double, iw As Double, ih As Double, scale As Double)
            Dim overlay = Me.FindControl(Of Border)("TextOverlay")
            Dim editor = Me.FindControl(Of TextBox)("TextOverlayEditor")
            Dim frame = Me.FindControl(Of Rectangle)("TextOverlayFrame")
            Dim overlayImage = Me.FindControl(Of Image)("SelectedAnnotationOverlayImage")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If overlay Is Nothing OrElse vm Is Nothing OrElse Not IsLayerPlacementTool(vm.CurrentTool) OrElse Not vm.HasSelectedAnnotation Then
                If overlay IsNot Nothing Then overlay.IsVisible = False
                Return
            End If

            Dim rectPercent = vm.GetSelectedAnnotationDisplayRectPercent()
            Dim width = iw * rectPercent.Width / 100.0
            Dim height = ih * rectPercent.Height / 100.0
            Dim left = ix + iw * rectPercent.X / 100.0
            Dim top = iy + ih * rectPercent.Y / 100.0

            Dim reachableRect = ClampOverlayRectToReachable(New Avalonia.Rect(left, top, width, height), New Avalonia.Rect(ix, iy, iw, ih))
            left = reachableRect.Left
            top = reachableRect.Top

            Avalonia.Controls.Canvas.SetLeft(overlay, left)
            Avalonia.Controls.Canvas.SetTop(overlay, top)
            overlay.Width = width
            overlay.Height = height
            overlay.RenderTransformOrigin = New RelativePoint(0.5, 0.5, RelativeUnit.Relative)
            overlay.RenderTransform = New RotateTransform(vm.AnnotationRotation)
            overlay.IsVisible = True
            If frame IsNot Nothing Then frame.Stroke = New SolidColorBrush(ParseAvaloniaColor(vm.AnnotationStrokeColor, Colors.White))
            If overlayImage IsNot Nothing Then
                overlayImage.Margin = ComputeSelectedOverlayImageMargin(vm, width, height)
            End If

            Dim selectedKind = If(vm.SelectedAnnotationKind, "")
            Dim isTextLayer = selectedKind.Equals("Text", StringComparison.OrdinalIgnoreCase) OrElse
                              (selectedKind.Equals("Watermark", StringComparison.OrdinalIgnoreCase) AndAlso vm.ShowTextContentControls)
            overlay.Cursor = If(isTextLayer, New Cursor(StandardCursorType.IBeam), New Cursor(StandardCursorType.SizeAll))

            If editor IsNot Nothing Then
                editor.IsVisible = (vm.CurrentTool = EditorTool.Text OrElse vm.CurrentTool = EditorTool.Insert) AndAlso
                                   vm.ShowTextContentControls AndAlso
                                   Not selectedKind.Equals("QR", StringComparison.OrdinalIgnoreCase)
                ' Der gebackene Renderer wendet seine 8px-Mindestschriftgröße in seinem eigenen
                ' (nicht gezoomten) Vorschaubild-Pixelraum an, bevor dieses per Stretch="Fill" auf
                ' iw x ih skaliert wird. Damit die Live-Vorschau bei dieser Mindestgröße nicht vom
                ' Zoom-Faktor abweicht, wird der Floor hier ebenfalls mit dem Zoom skaliert.
                editor.FontSize = Math.Max(8.0 * scale, ih * vm.AnnotationFontSize / 100.0)
                editor.FontFamily = New FontFamily(vm.AnnotationFontFamily)
                editor.Foreground = New SolidColorBrush(ParseAvaloniaColor(vm.AnnotationFillColor, Colors.White))
            End If
        End Sub

        Private Shared Function ComputeSelectedOverlayImageMargin(vm As EditorViewModel, width As Double, height As Double) As Thickness
            If vm Is Nothing OrElse width <= 0 OrElse height <= 0 OrElse Not vm.ShowSelectedSvgOverlay Then
                Return New Thickness(0)
            End If

            Dim objSize = Math.Max(1.0, Math.Min(width, height))
            ' Muss exakt der Padding-Formel in ImageProcessor.RenderAnnotationOverlay entsprechen.
            Dim shadowGrow = If(vm.AnnotationShadowEnabled, Math.Max(width, height) * Math.Max(0.0, vm.AnnotationShadowSize / 100.0 - 1.0) * 0.5, 0.0)
            Dim glowPad = If(vm.AnnotationGlowEnabled, objSize * vm.AnnotationGlowBlur / 100.0 * 2.4, 0.0)
            Dim shadowPad = If(vm.AnnotationShadowEnabled, objSize * vm.AnnotationShadowBlur / 100.0 * 1.8 + shadowGrow, 0.0)
            Dim offsetX = If(vm.AnnotationShadowEnabled, objSize * vm.AnnotationShadowOffsetX / 100.0, 0.0)
            Dim offsetY = If(vm.AnnotationShadowEnabled, objSize * vm.AnnotationShadowOffsetY / 100.0, 0.0)
            Dim effectPad = Math.Max(glowPad, shadowPad)

            Dim leftPad = 4.0 + effectPad + Math.Max(0.0, -offsetX)
            Dim rightPad = 4.0 + effectPad + Math.Max(0.0, offsetX)
            Dim topPad = 4.0 + effectPad + Math.Max(0.0, -offsetY)
            Dim bottomPad = 4.0 + effectPad + Math.Max(0.0, offsetY)

            Return New Thickness(-leftPad, -topPad, -rightPad, -bottomPad)
        End Function

        Private Shared Function IsLayerPlacementTool(tool As EditorTool) As Boolean
            Return tool = EditorTool.Text OrElse tool = EditorTool.Geometry OrElse tool = EditorTool.Insert OrElse tool = EditorTool.Selection
        End Function


        Private Shared Function ParseAvaloniaColor(value As String, fallback As Color) As Color
            If String.IsNullOrWhiteSpace(value) Then Return fallback
            Try
                Return Color.Parse(value)
            Catch
                Return fallback
            End Try
        End Function

        Private Shared ReadOnly TextSnapPercents As Double() = {0, 4, 50, 96, 100}
        Private Const OverlayMinVisiblePixels As Double = 24.0

        Private Shared Function ClampOverlayOriginToReachable(origin As Double, size As Double, axisStart As Double, axisLength As Double) As Double
            If size <= 0 OrElse axisLength <= 0 Then Return origin
            Dim minVisible = Math.Min(OverlayMinVisiblePixels, Math.Max(1.0, Math.Min(size, axisLength)))
            Dim minOrigin = axisStart + minVisible - size
            Dim maxOrigin = axisStart + axisLength - minVisible
            If minOrigin > maxOrigin Then Return axisStart + (axisLength - size) / 2.0
            Return Math.Max(minOrigin, Math.Min(maxOrigin, origin))
        End Function

        Private Shared Function ClampOverlayRectToReachable(rect As Avalonia.Rect, imageRect As Avalonia.Rect) As Avalonia.Rect
            Dim left = ClampOverlayOriginToReachable(rect.Left, rect.Width, imageRect.Left, imageRect.Width)
            Dim top = ClampOverlayOriginToReachable(rect.Top, rect.Height, imageRect.Top, imageRect.Height)
            Return New Avalonia.Rect(left, top, rect.Width, rect.Height)
        End Function

        Public Sub OnTextOverlayPointerPressed(sender As Object, e As PointerPressedEventArgs)
            If Not e.GetCurrentPoint(Nothing).Properties.IsLeftButtonPressed Then Return
            Dim overlay = TryCast(sender, Border)
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If overlay Is Nothing OrElse canvas Is Nothing OrElse vm Is Nothing Then Return
            Dim pos = e.GetPosition(canvas)
            Dim rect = GetTextOverlayRect()
            Dim mode = GetTextDragMode(pos, rect, vm.AnnotationRotation)
            If mode = TextDragMode.None Then Return
            _textDragMode = mode
            _textDragInitialRect = rect
            _textDragPointerStart = pos
            _textDragAspect = If(rect.Height > 0, rect.Width / rect.Height, 1.0)
            _isTextDragging = True
            e.Pointer.Capture(overlay)
            If mode = TextDragMode.Rotate Then
                _textRotateCenter = New Avalonia.Point(rect.Left + rect.Width / 2.0, rect.Top + rect.Height / 2.0)
                _textRotateStartAngle = Math.Atan2(pos.Y - _textRotateCenter.Y, pos.X - _textRotateCenter.X) * 180.0 / Math.PI
                _textRotateStartRotation = vm.AnnotationRotation
            Else
                ShowTextSizeBadge()
                If mode = TextDragMode.Move AndAlso (vm.CurrentTool = EditorTool.Text OrElse vm.CurrentTool = EditorTool.Insert) Then
                    FocusTextOverlayEditor()
                End If
            End If
            e.Handled = True
        End Sub

        Private Const RotateHandleDistance As Double = 28
        Private Const RotateHandleHitRadius As Double = 12

        Private Function GetRotateHandlePoint(rect As Avalonia.Rect, rotationDegrees As Double) As Avalonia.Point
            Dim centerX = rect.Left + rect.Width / 2.0
            Dim centerY = rect.Top + rect.Height / 2.0
            Dim localY = -(rect.Height / 2.0 + RotateHandleDistance)
            Dim rad = rotationDegrees * Math.PI / 180.0
            Dim rotatedX = -localY * Math.Sin(rad)
            Dim rotatedY = localY * Math.Cos(rad)
            Return New Avalonia.Point(centerX + rotatedX, centerY + rotatedY)
        End Function

        Private Function GetTextDragMode(point As Avalonia.Point, rect As Avalonia.Rect, rotationDegrees As Double) As TextDragMode
            If rect.Width < 4 OrElse rect.Height < 4 Then Return TextDragMode.None

            Dim rotateHandle = GetRotateHandlePoint(rect, rotationDegrees)
            Dim rdx = point.X - rotateHandle.X
            Dim rdy = point.Y - rotateHandle.Y
            If Math.Sqrt(rdx * rdx + rdy * rdy) <= RotateHandleHitRadius Then Return TextDragMode.Rotate

            Const hitSlop As Double = 14
            Dim hitRect = rect.Inflate(hitSlop)
            If Not hitRect.Contains(point) Then Return TextDragMode.None

            Const handleSize As Double = 16
            Dim nearLeft = Math.Abs(point.X - rect.Left) <= handleSize
            Dim nearRight = Math.Abs(point.X - rect.Right) <= handleSize
            Dim nearTop = Math.Abs(point.Y - rect.Top) <= handleSize
            Dim nearBottom = Math.Abs(point.Y - rect.Bottom) <= handleSize

            If nearLeft AndAlso nearTop Then Return TextDragMode.TopLeft
            If nearRight AndAlso nearTop Then Return TextDragMode.TopRight
            If nearLeft AndAlso nearBottom Then Return TextDragMode.BottomLeft
            If nearRight AndAlso nearBottom Then Return TextDragMode.BottomRight
            If nearLeft Then Return TextDragMode.Left
            If nearRight Then Return TextDragMode.Right
            If nearTop Then Return TextDragMode.Top
            If nearBottom Then Return TextDragMode.Bottom
            If Not rect.Contains(point) Then Return TextDragMode.None
            Return TextDragMode.Move
        End Function

        Private Shared Function IsTextCornerMode(mode As TextDragMode) As Boolean
            Return mode = TextDragMode.TopLeft OrElse mode = TextDragMode.TopRight OrElse
                   mode = TextDragMode.BottomLeft OrElse mode = TextDragMode.BottomRight
        End Function

        ''' Ordnet einer erkannten Anfasser-Zone (Ecke/Kante/Drehen/Verschieben) den passenden
        ''' Maus-Cursor zu - TopLeftCorner/TopSide/etc. sind dieselben StandardCursorType-Werte,
        ''' die bereits für die Fenster-Resize-Ränder in MainWindow.axaml verwendet werden. Für
        ''' den Rotier-Griff gibt es in Avalonia keinen dedizierten Cursor, Hand ist die gängige
        ''' Annäherung dafür.
        Private Shared Function GetCursorForTextDragMode(mode As TextDragMode, isTextLayer As Boolean) As Cursor
            Select Case mode
                Case TextDragMode.TopLeft : Return New Cursor(StandardCursorType.TopLeftCorner)
                Case TextDragMode.TopRight : Return New Cursor(StandardCursorType.TopRightCorner)
                Case TextDragMode.BottomLeft : Return New Cursor(StandardCursorType.BottomLeftCorner)
                Case TextDragMode.BottomRight : Return New Cursor(StandardCursorType.BottomRightCorner)
                Case TextDragMode.Left : Return New Cursor(StandardCursorType.LeftSide)
                Case TextDragMode.Right : Return New Cursor(StandardCursorType.RightSide)
                Case TextDragMode.Top : Return New Cursor(StandardCursorType.TopSide)
                Case TextDragMode.Bottom : Return New Cursor(StandardCursorType.BottomSide)
                Case TextDragMode.Rotate : Return New Cursor(StandardCursorType.Hand)
                Case TextDragMode.Move : Return New Cursor(If(isTextLayer, StandardCursorType.IBeam, StandardCursorType.SizeAll))
                Case Else : Return Nothing
            End Select
        End Function

        Private Function IsSelectedAnnotationTextLayer(vm As EditorViewModel) As Boolean
            Dim selectedKind = If(vm.SelectedAnnotationKind, "")
            Return selectedKind.Equals("Text", StringComparison.OrdinalIgnoreCase) OrElse
                   (selectedKind.Equals("Watermark", StringComparison.OrdinalIgnoreCase) AndAlso vm.ShowTextContentControls)
        End Function

        ''' Aktualisiert den Cursor des TextOverlay-Borders anhand der Anfasser-Zone unter dem
        ''' Zeiger, solange nur gehovert (nicht gezogen) wird. Deckt den Bereich innerhalb der
        ''' Border-Bounds ab (Verschieben-Zone sowie die Innenhälfte der Kanten-/Eckenzonen) -
        ''' der außerhalb der Bounds überhängende Teil der Ecken- und der Rotier-Griff werden
        ''' separat in OnSliderPointerMoved behandelt, da Zeigerpositionen dort gar nicht erst
        ''' auf diesem Border landen (siehe Kommentar bei OnSliderPointerPressed).
        Private Sub UpdateTextOverlayHoverCursor(overlay As Border, e As PointerEventArgs)
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas Is Nothing OrElse vm Is Nothing Then Return
            Dim mode = GetTextDragMode(e.GetPosition(canvas), GetTextOverlayRect(), vm.AnnotationRotation)
            overlay.Cursor = GetCursorForTextDragMode(mode, IsSelectedAnnotationTextLayer(vm))
        End Sub

        Public Sub OnTextOverlayPointerMoved(sender As Object, e As PointerEventArgs)
            If Not _isTextDragging Then
                Dim hoverOverlay = TryCast(sender, Border)
                If hoverOverlay IsNot Nothing Then UpdateTextOverlayHoverCursor(hoverOverlay, e)
                Return
            End If
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim overlay = Me.FindControl(Of Border)("TextOverlay")
            If canvas Is Nothing OrElse vm Is Nothing OrElse overlay Is Nothing Then Return
            Dim imageRect = GetDisplayedImageRect(canvas, vm)
            If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return

            Dim pos = e.GetPosition(canvas)

            If _textDragMode = TextDragMode.Rotate Then
                Dim currentAngle = Math.Atan2(pos.Y - _textRotateCenter.Y, pos.X - _textRotateCenter.X) * 180.0 / Math.PI
                Dim newRotation = _textRotateStartRotation + (currentAngle - _textRotateStartAngle)
                newRotation = ((newRotation + 180.0) Mod 360.0 + 360.0) Mod 360.0 - 180.0
                If e.KeyModifiers.HasFlag(KeyModifiers.Shift) Then newRotation = Math.Round(newRotation / 15.0) * 15.0
                vm.AnnotationRotation = newRotation
                overlay.RenderTransformOrigin = New RelativePoint(0.5, 0.5, RelativeUnit.Relative)
                overlay.RenderTransform = New RotateTransform(newRotation)
                e.Handled = True
                Return
            End If

            Dim dx = pos.X - _textDragPointerStart.X
            Dim dy = pos.Y - _textDragPointerStart.Y
            Dim left = _textDragInitialRect.Left
            Dim top = _textDragInitialRect.Top
            Dim right = _textDragInitialRect.Right
            Dim bottom = _textDragInitialRect.Bottom
            Const minSize As Double = 24

            If _textDragMode = TextDragMode.Move Then
                Dim width = _textDragInitialRect.Width
                Dim height = _textDragInitialRect.Height
                left = ClampOverlayOriginToReachable(left + dx, width, imageRect.Left, imageRect.Width)
                top = ClampOverlayOriginToReachable(top + dy, height, imageRect.Top, imageRect.Height)
                left = ApplyTextSnap(left, width, imageRect.Left, imageRect.Width, True)
                top = ApplyTextSnap(top, height, imageRect.Top, imageRect.Height, False)
                left = ClampOverlayOriginToReachable(left, width, imageRect.Left, imageRect.Width)
                top = ClampOverlayOriginToReachable(top, height, imageRect.Top, imageRect.Height)
                right = left + width
                bottom = top + height
            Else
                Select Case _textDragMode
                    Case TextDragMode.Left, TextDragMode.TopLeft, TextDragMode.BottomLeft
                        left = Math.Min(right - minSize, left + dx)
                    Case TextDragMode.Right, TextDragMode.TopRight, TextDragMode.BottomRight
                        right = Math.Max(left + minSize, right + dx)
                End Select
                Select Case _textDragMode
                    Case TextDragMode.Top, TextDragMode.TopLeft, TextDragMode.TopRight
                        top = Math.Min(bottom - minSize, top + dy)
                    Case TextDragMode.Bottom, TextDragMode.BottomLeft, TextDragMode.BottomRight
                        bottom = Math.Max(top + minSize, bottom + dy)
                End Select

                Dim keepAspect = (e.KeyModifiers.HasFlag(KeyModifiers.Shift) OrElse String.Equals(vm.EffectiveAnnotationKind, "Image", StringComparison.OrdinalIgnoreCase)) AndAlso
                                 _textDragAspect > 0 AndAlso IsTextCornerMode(_textDragMode)
                If keepAspect Then
                    Dim targetHeight = Math.Max(minSize, (right - left) / _textDragAspect)
                    Select Case _textDragMode
                        Case TextDragMode.TopLeft, TextDragMode.TopRight
                            top = bottom - targetHeight
                        Case TextDragMode.BottomLeft, TextDragMode.BottomRight
                            bottom = top + targetHeight
                    End Select
                End If
                HideTextSnapGuides()
            End If

            Dim finalRect = ClampOverlayRectToReachable(New Avalonia.Rect(Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(bottom - top)), imageRect)
            Avalonia.Controls.Canvas.SetLeft(overlay, finalRect.Left)
            Avalonia.Controls.Canvas.SetTop(overlay, finalRect.Top)
            overlay.Width = finalRect.Width
            overlay.Height = finalRect.Height
            UpdateTextPercentages(finalRect, imageRect, vm)
            UpdateTextSizeBadge(finalRect, imageRect, vm)
            e.Handled = True
        End Sub

        ''' Prüft, ob left/left+width/2/left+width nahe an einem Snap-Ziel (Bildkante/Mitte/Sicherheitsabstand) liegt und rastet ggf. ein.
        Private Function ApplyTextSnap(value As Double, size As Double, axisStart As Double, axisLength As Double, isVerticalLine As Boolean) As Double
            Const tolerance As Double = 7.0
            For Each pct In TextSnapPercents
                Dim target = axisStart + axisLength * pct / 100.0
                If Math.Abs(value - target) <= tolerance Then
                    ShowTextSnapGuide(target, isVerticalLine)
                    Return target
                End If
                If Math.Abs(value + size / 2.0 - target) <= tolerance Then
                    ShowTextSnapGuide(target, isVerticalLine)
                    Return target - size / 2.0
                End If
                If Math.Abs(value + size - target) <= tolerance Then
                    ShowTextSnapGuide(target, isVerticalLine)
                    Return target - size
                End If
            Next
            HideTextSnapGuide(isVerticalLine)
            Return value
        End Function

        Private Sub ShowTextSnapGuide(position As Double, isVerticalLine As Boolean)
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            If canvas Is Nothing Then Return
            Dim line = Me.FindControl(Of Line)(If(isVerticalLine, "SnapGuideV", "SnapGuideH"))
            If line Is Nothing Then Return
            If isVerticalLine Then
                line.StartPoint = New Avalonia.Point(position, 0)
                line.EndPoint = New Avalonia.Point(position, canvas.Bounds.Height)
            Else
                line.StartPoint = New Avalonia.Point(0, position)
                line.EndPoint = New Avalonia.Point(canvas.Bounds.Width, position)
            End If
            line.IsVisible = True
        End Sub

        Private Sub HideTextSnapGuide(isVerticalLine As Boolean)
            Dim line = Me.FindControl(Of Line)(If(isVerticalLine, "SnapGuideV", "SnapGuideH"))
            If line IsNot Nothing Then line.IsVisible = False
        End Sub

        Private Sub HideTextSnapGuides()
            HideTextSnapGuide(True)
            HideTextSnapGuide(False)
        End Sub

        Private Sub ShowTextSizeBadge()
            Dim badge = Me.FindControl(Of Border)("TextSizeBadge")
            If badge IsNot Nothing Then badge.IsVisible = True
        End Sub

        Private Sub UpdateTextSizeBadge(rect As Avalonia.Rect, imageRect As Avalonia.Rect, vm As EditorViewModel)
            Dim badgeText = Me.FindControl(Of TextBlock)("TextSizeBadgeText")
            If badgeText Is Nothing OrElse vm.CurrentImage Is Nothing Then Return
            Dim pxW = CInt(Math.Round(rect.Width / imageRect.Width * vm.CurrentImage.PixelSize.Width))
            Dim pxH = CInt(Math.Round(rect.Height / imageRect.Height * vm.CurrentImage.PixelSize.Height))
            Dim pxX = CInt(Math.Round((rect.Left - imageRect.Left) / imageRect.Width * vm.CurrentImage.PixelSize.Width))
            Dim pxY = CInt(Math.Round((rect.Top - imageRect.Top) / imageRect.Height * vm.CurrentImage.PixelSize.Height))
            badgeText.Text = $"{pxW} × {pxH} px  ·  X {pxX}, Y {pxY}"
        End Sub

        Private Sub HideTextSizeBadge()
            Dim badge = Me.FindControl(Of Border)("TextSizeBadge")
            If badge IsNot Nothing Then badge.IsVisible = False
        End Sub

        Public Sub OnTextOverlayPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            If Not _isTextDragging Then Return
            _isTextDragging = False
            _textDragMode = TextDragMode.None
            HideTextSizeBadge()
            HideTextSnapGuides()
            e.Pointer.Capture(Nothing)
            e.Handled = True
        End Sub

        Private Function GetTextOverlayRect() As Avalonia.Rect
            Dim overlay = Me.FindControl(Of Border)("TextOverlay")
            If overlay Is Nothing OrElse Not overlay.IsVisible Then Return New Avalonia.Rect()
            Dim left = Avalonia.Controls.Canvas.GetLeft(overlay)
            Dim top = Avalonia.Controls.Canvas.GetTop(overlay)
            If Double.IsNaN(left) Then left = 0
            If Double.IsNaN(top) Then top = 0
            Return New Avalonia.Rect(left, top, Math.Max(0, overlay.Bounds.Width), Math.Max(0, overlay.Bounds.Height))
        End Function

        Private Sub UpdateTextPercentages(textRect As Avalonia.Rect, imageRect As Avalonia.Rect, vm As EditorViewModel)
            vm.SetSelectedAnnotationRect(
                (textRect.Left - imageRect.Left) / imageRect.Width * 100.0,
                (textRect.Top - imageRect.Top) / imageRect.Height * 100.0,
                textRect.Width / imageRect.Width * 100.0,
                textRect.Height / imageRect.Height * 100.0)
        End Sub

        Private Sub MoveSlider(mouseX As Double, canvas As Canvas)
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim displayBitmap = vm?.DisplayImage
            If vm Is Nothing OrElse displayBitmap Is Nothing Then Return

            Dim cw = canvas.Bounds.Width
            Dim ch = canvas.Bounds.Height
            If cw <= 0 OrElse ch <= 0 Then Return

            Dim imgW = GetEffectiveDisplaySize(vm).Width
            Dim scale = SliderToZoom(_zoomSliderValue) / 100.0
            Dim iw = imgW * scale
            Dim ix = (cw - iw) / 2.0 + _panX

            _sliderPosition = Math.Max(0.0, Math.Min(1.0, (mouseX - ix) / iw))
            UpdateSliderLayout()
        End Sub

        Private Sub OnFilmstripWheelChanged(sender As Object, e As PointerWheelEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            vm.NavigateByWheel(e.Delta.Y)
            e.Handled = True
        End Sub

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
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            vm.NavigateToFilmstripItem(item)
            Me.Focus()
        End Sub

        Public Sub OnGlobalPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            If e.InitialPressMouseButton = MouseButton.Middle Then
                _filmstripController.HidePreview()
            End If
        End Sub

        ' Avalonias ComboBox-Popup richtet seine Breite standardmäßig am Inhalt aus, nicht an der
        ' ComboBox selbst - hier wird die Popup-Breite beim Öffnen explizit an die ComboBox angeglichen.
        Public Sub OnMatchWidthDropDownOpened(sender As Object, e As EventArgs)
            Dim comboBox = TryCast(sender, ComboBox)
            If comboBox Is Nothing Then Return
            Dim popup = comboBox.GetVisualDescendants().OfType(Of Popup)().FirstOrDefault()
            If popup IsNot Nothing Then
                popup.Width = comboBox.Bounds.Width
            End If
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

        Public Shadows Sub OnKeyDown(sender As Object, e As KeyEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            Dim isTextInputFocused = TypeOf e.Source Is TextBox

            If e.KeyModifiers.HasFlag(KeyModifiers.Control) Then
                Select Case e.Key
                    Case Key.Z
                        vm.UndoCommand.Execute(Nothing)
                        e.Handled = True
                    Case Key.Y
                        vm.RedoCommand.Execute(Nothing)
                        e.Handled = True
                    Case Key.S
                        If vm.CanSaveInPlace Then vm.SaveCommand.Execute(Nothing)
                        e.Handled = True
                    Case Key.D
                        If vm.HasSelectedAnnotation Then
                            vm.DuplicateSelectedAnnotationCommand.Execute(Nothing)
                            e.Handled = True
                        End If
                    Case Key.P
                        If Not isTextInputFocused Then
                            vm.ApplyPreviewCommand.Execute(Nothing)
                            e.Handled = True
                        End If
                    Case Key.R
                        If Not isTextInputFocused Then
                            vm.CurrentTool = EditorTool.Rotate
                            e.Handled = True
                        End If
                    Case Key.T
                        If Not isTextInputFocused Then
                            vm.CurrentTool = EditorTool.Text
                            e.Handled = True
                        End If
                    Case Key.B
                        If Not isTextInputFocused Then
                            vm.CurrentTool = EditorTool.Draw
                            e.Handled = True
                        End If
                    Case Key.G
                        If Not isTextInputFocused Then
                            vm.CurrentTool = EditorTool.Insert
                            e.Handled = True
                        End If
                    Case Key.E
                        If Not isTextInputFocused AndAlso vm.CurrentTool = EditorTool.Draw Then
                            vm.IsEraserMode = Not vm.IsEraserMode
                            e.Handled = True
                        End If
                    Case Key.C
                        If Not isTextInputFocused AndAlso vm.CurrentTool = EditorTool.Selection AndAlso vm.HasActiveSelection Then
                            vm.CopySelectionToClipboard()
                            e.Handled = True
                        End If
                    Case Key.V
                        If Not isTextInputFocused AndAlso vm.CurrentTool = EditorTool.Selection Then
                            vm.PasteSelectionClipboard()
                            e.Handled = True
                        End If
                End Select
            Else
                Select Case e.Key
                    Case Key.Left
                        If vm.HasSelectedAnnotation Then
                            vm.NudgeSelectedAnnotation(-If(e.KeyModifiers.HasFlag(KeyModifiers.Shift), 5.0, 1.0), 0)
                            e.Handled = True
                        End If
                    Case Key.Right
                        If vm.HasSelectedAnnotation Then
                            vm.NudgeSelectedAnnotation(If(e.KeyModifiers.HasFlag(KeyModifiers.Shift), 5.0, 1.0), 0)
                            e.Handled = True
                        End If
                    Case Key.Up
                        If vm.HasSelectedAnnotation Then
                            vm.NudgeSelectedAnnotation(0, -If(e.KeyModifiers.HasFlag(KeyModifiers.Shift), 5.0, 1.0))
                            e.Handled = True
                        End If
                    Case Key.Down
                        If vm.HasSelectedAnnotation Then
                            vm.NudgeSelectedAnnotation(0, If(e.KeyModifiers.HasFlag(KeyModifiers.Shift), 5.0, 1.0))
                            e.Handled = True
                        End If
                    Case Key.Delete
                        If vm.HasSelectedAnnotation Then
                            vm.DeleteSelectedAnnotationCommand.Execute(Nothing)
                        Else
                            vm.DeleteCurrentCommand.Execute(Nothing)
                        End If
                        e.Handled = True
                    Case Key.Escape
                        If vm.IsPickingColorFromImage Then
                            vm.CancelColorPick()
                        ElseIf vm.HasSelectedAnnotation OrElse Not String.IsNullOrEmpty(vm.PendingInsertKind) Then
                            vm.SelectedAnnotationIndex = -1
                            vm.PendingInsertKind = ""
                        Else
                            vm.BackToViewerCommand.Execute(Nothing)
                        End If
                        e.Handled = True
                    Case Key.Space
                        If Not isTextInputFocused AndAlso Not _isPanMode Then
                            _isPanMode = True
                            _spacePanActive = True
                            Dim btn = Me.FindControl(Of Button)("PanModeButton")
                            If btn IsNot Nothing Then btn.Classes.Add("active")
                            e.Handled = True
                        End If
                    Case Key.OemOpenBrackets
                        If Not isTextInputFocused Then
                            AdjustActiveToolSize(vm, -1)
                            e.Handled = True
                        End If
                    Case Key.OemCloseBrackets
                        If Not isTextInputFocused Then
                            AdjustActiveToolSize(vm, 1)
                            e.Handled = True
                        End If
                End Select
            End If
        End Sub

        Public Shadows Sub OnKeyUp(sender As Object, e As KeyEventArgs)
            If e.Key = Key.Space AndAlso _spacePanActive Then
                _spacePanActive = False
                _isPanMode = False
                Dim btn = Me.FindControl(Of Button)("PanModeButton")
                If btn IsNot Nothing Then btn.Classes.Remove("active")
                e.Handled = True
            End If
        End Sub

        Private Sub AdjustActiveToolSize(vm As EditorViewModel, direction As Integer)
            If vm.CurrentTool = EditorTool.Draw Then
                vm.BrushSize = vm.BrushSize + direction * If(vm.BrushSize >= 5, 1.0, 0.2)
            ElseIf vm.CurrentTool = EditorTool.Retouch Then
                vm.RetouchRadius = vm.RetouchRadius + direction * If(vm.RetouchRadius >= 5, 1.0, 0.2)
            End If
        End Sub

    End Class

End Namespace
