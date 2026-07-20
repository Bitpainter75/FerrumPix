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
Imports FerrumPix.Services
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

        ' Für die Pipette dekodierte Fassung des gerade angezeigten Bildes (siehe SampleDisplayedColor).
        Private _pickSampleSource As Bitmap = Nothing
        Private _pickSampleBitmap As SKBitmap = Nothing

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
        ' Schriftgrad beim Griff der Maus: der Ziehvorgang skaliert IHN, nicht den zuletzt gesetzten -
        ' sonst multiplizierte sich die Skalierung Frame für Frame in sich selbst hinein.
        Private _textDragInitialFontSize As Double = 0
        Private _textRotateStartAngle As Double
        Private _textRotateStartRotation As Double
        Private _textRotateCenter As Avalonia.Point
        Private _isBrushDrawing As Boolean = False
        Private _hideBrushPreviewAfterBake As Boolean = False
        Private ReadOnly _brushPoints As New List(Of Avalonia.Point)()
        Private _isRetouching As Boolean = False
        Private _lastRetouchPoint As Avalonia.Point
        Private _isSelectionDragging As Boolean = False
        Private _selectionStart As Avalonia.Point
        Private _selectionEnd As Avalonia.Point
        Private _isSelectionMoveDragging As Boolean = False
        Private _selectionMoveLastPoint As Avalonia.Point
        Private _selectionDragReplacesExisting As Boolean = False
        Private _isLassoDrawing As Boolean = False
        Private ReadOnly _lassoPoints As New List(Of Avalonia.Point)()

        ' Lineale und Hilfslinien. Die Hilfslinien werden in Bildpixeln gespeichert, nicht in
        ' Canvas-Koordinaten - so bleiben sie beim Zoomen und Schwenken an derselben Stelle im Bild.
        Private _showRulers As Boolean = False
        Private _showGrid As Boolean = False
        Private ReadOnly _guidesX As New List(Of Double)()
        Private ReadOnly _guidesY As New List(Of Double)()
        Private _isGuideDragging As Boolean = False
        Private _guideDragIsVertical As Boolean = False
        Private _guideDragIndex As Integer = -1

        Private Const GuideHitTolerance As Double = 4.0
        Private Shared ReadOnly GuideBrush As IBrush = New SolidColorBrush(Color.Parse("#FF00C8FF"))
        Private Shared ReadOnly GuideCursorVertical As New Cursor(StandardCursorType.SizeWestEast)
        Private Shared ReadOnly GuideCursorHorizontal As New Cursor(StandardCursorType.SizeNorthSouth)
        Private Shared ReadOnly TransparentEraserPreviewBrush As IBrush = BuildTransparentEraserPreviewBrush()

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

        Private Sub SetZoomAtCanvasPoint(sliderValue As Double, anchor As Avalonia.Point)
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas Is Nothing OrElse vm Is Nothing OrElse vm.DisplayImage Is Nothing Then
                SetZoom(sliderValue)
                Return
            End If

            Dim cw = canvas.Bounds.Width
            Dim ch = canvas.Bounds.Height
            If cw <= 0 OrElse ch <= 0 Then
                SetZoom(sliderValue)
                Return
            End If

            Dim effectiveSize = GetEffectiveDisplaySize(vm)
            If effectiveSize.Width <= 0 OrElse effectiveSize.Height <= 0 Then
                SetZoom(sliderValue)
                Return
            End If

            Dim oldScale = SliderToZoom(_zoomSliderValue) / 100.0
            Dim oldW = Math.Round(effectiveSize.Width * oldScale, MidpointRounding.AwayFromZero)
            Dim oldH = Math.Round(effectiveSize.Height * oldScale, MidpointRounding.AwayFromZero)
            Dim oldLeft = Math.Round((cw - oldW) / 2.0 + _panX, MidpointRounding.AwayFromZero)
            Dim oldTop = Math.Round((ch - oldH) / 2.0 + _panY, MidpointRounding.AwayFromZero)
            Dim imageX = (anchor.X - oldLeft) / Math.Max(0.0001, oldScale)
            Dim imageY = (anchor.Y - oldTop) / Math.Max(0.0001, oldScale)

            _zoomSliderValue = Math.Max(0, Math.Min(100, sliderValue))
            Dim newScale = SliderToZoom(_zoomSliderValue) / 100.0
            Dim newW = Math.Round(effectiveSize.Width * newScale, MidpointRounding.AwayFromZero)
            Dim newH = Math.Round(effectiveSize.Height * newScale, MidpointRounding.AwayFromZero)
            _panX = anchor.X - (cw - newW) / 2.0 - imageX * newScale
            _panY = anchor.Y - (ch - newH) / 2.0 - imageY * newScale

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

        ''' <paramref name="includeReadOnlyFormats"/>: PSD/PSB nur beim OEFFNEN eines Dokuments
        ''' anbieten - als eingefuegtes Bildobjekt zeichnet DrawImageAnnotation sie nicht
        ''' (SKBitmap.Decode kennt das Format nicht), die Auswahl waere dort ein stiller No-Op.
        Private Async Function PickSingleImagePathAsync(title As String,
                                                        Optional includeReadOnlyFormats As Boolean = False) As Task(Of String)
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return Nothing
                Dim patterns As New List(Of String) From {"*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.tif", "*.tiff", "*.avif", "*.ico"}
                If includeReadOnlyFormats Then
                    patterns.Add("*.psd")
                    patterns.Add("*.psb")
                End If
                Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                    .Title = title,
                    .AllowMultiple = False,
                    .FileTypeFilter = New List(Of FilePickerFileType) From {
                        New FilePickerFileType(LocalizationService.T("Bilder")) With {
                            .Patterns = patterns.ToArray()
                        }
                    }
                })
                Return files?.FirstOrDefault()?.Path.LocalPath
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>„Bild öffnen" aus dem Leerzustand. Nutzt denselben Dateidialog wie das Einfügen
        ''' eines Bildobjekts und gibt den Pfad an den regulären Editor-Einstieg weiter.</summary>
        Public Async Sub OnPlaceholderOpenImageClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            If mainVm Is Nothing Then Return
            Dim path = Await PickSingleImagePathAsync(LocalizationService.T("Bild öffnen"), includeReadOnlyFormats:=True)
            If String.IsNullOrWhiteSpace(path) Then Return
            Await mainVm.OpenImageInEditor(path)
        End Sub

        Private Async Sub PlacePendingImageAsync(xPercent As Double, yPercent As Double)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return

            Try
                Dim path = Await PickSingleImagePathAsync(LocalizationService.T("Bild auswählen"))
                If Not String.IsNullOrWhiteSpace(path) Then
                    vm.AddImageAnnotationAt(path, xPercent, yPercent)
                End If
            Catch
            End Try
        End Sub

        Public Async Sub OnWatermarkChooseImageClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            Dim path = Await PickSingleImagePathAsync(LocalizationService.T("Wasserzeichen-Bild auswählen"))
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
                    .Title = LocalizationService.T("Lightroom-Preset laden"),
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

        Public Sub OnRulerModeToggleClick(sender As Object, e As RoutedEventArgs)
            _showRulers = Not _showRulers
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm IsNot Nothing Then
                vm.EditorShowRulers = _showRulers
            Else
                AppSettingsService.SaveEditorShowRulers(_showRulers)
            End If
            ApplyRulerState()
            UpdateSliderLayout()
        End Sub

        Public Sub OnGridModeToggleClick(sender As Object, e As RoutedEventArgs)
            _showGrid = Not _showGrid
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm IsNot Nothing Then
                vm.EditorShowGrid = _showGrid
            Else
                AppSettingsService.SaveEditorShowGrid(_showGrid)
            End If
            ApplyGridState()
            UpdateGridOverlay()
        End Sub

        ''' Für jeden Editor-Besuch wird eine neue EditorView gebaut (siehe HandleDataContextChanged) -
        ''' Lineale und Raster kämen sonst jedes Mal ausgeschaltet zurück.
        Private Sub RestoreRulerAndGridState()
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm IsNot Nothing Then
                _showRulers = vm.EditorShowRulers
                _showGrid = vm.EditorShowGrid
            Else
                Dim settings = AppSettingsService.Load()
                _showRulers = settings.EditorShowRulers
                _showGrid = settings.EditorShowGrid
            End If
            ApplyRulerState()
            ApplyGridState()
            UpdateGridOverlay()
        End Sub

        Private Sub ApplyRulerState()
            Dim btn = Me.FindControl(Of Button)("RulerModeButton")
            If btn IsNot Nothing Then
                If _showRulers Then btn.Classes.Add("active") Else btn.Classes.Remove("active")
            End If
            ApplyRulerVisibility()
        End Sub

        Private Sub ApplyGridState()
            Dim btn = Me.FindControl(Of Button)("GridModeButton")
            If btn IsNot Nothing Then
                If _showGrid Then btn.Classes.Add("active") Else btn.Classes.Remove("active")
            End If
            Dim overlay = Me.FindControl(Of PixelGridOverlay)("GridOverlay")
            If overlay IsNot Nothing Then overlay.IsVisible = _showGrid
        End Sub

        Public Sub OnClearGuidesClick(sender As Object, e As RoutedEventArgs)
            ClearGuides()
        End Sub

        Private Sub ApplyRulerVisibility()
            Dim topRuler = Me.FindControl(Of RulerControl)("TopRuler")
            Dim leftRuler = Me.FindControl(Of RulerControl)("LeftRuler")
            Dim corner = Me.FindControl(Of Button)("RulerCornerButton")
            If topRuler IsNot Nothing Then topRuler.IsVisible = _showRulers
            If leftRuler IsNot Nothing Then leftRuler.IsVisible = _showRulers
            If corner IsNot Nothing Then corner.IsVisible = _showRulers
        End Sub

        Private Sub ClearGuides()
            If _guidesX.Count = 0 AndAlso _guidesY.Count = 0 Then Return
            _guidesX.Clear()
            _guidesY.Clear()
            _isGuideDragging = False
            _guideDragIndex = -1
            UpdateGuideLines()
        End Sub

        Public Sub OnToggleFilmstripClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            If mainVm Is Nothing OrElse mainVm.Settings Is Nothing Then Return
            ' Bei einem neuen Bild bleibt der Filmstreifen aus - sonst schriebe der Klick die
            ' Einstellung um, ohne dass sich sichtbar etwas täte (der Knopf ist auch gesperrt).
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm IsNot Nothing AndAlso Not vm.CanToggleFilmstrip Then Return
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
            Dim pointerPoint = e.GetCurrentPoint(canvas)
            Dim wantsZoom = pointerPoint.Properties.IsRightButtonPressed OrElse e.KeyModifiers.HasFlag(KeyModifiers.Control)
            If Not wantsZoom Then Return
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm IsNot Nothing Then vm.ActiveZoomPreset = ZoomPresetMode.Manual
            Dim anchor = e.GetPosition(canvas)
            SetZoomAtCanvasPoint(_zoomSliderValue + If(e.Delta.Y > 0, 6.0, -6.0), anchor)
            ' Rad-Zoom bei GEDRUECKTER rechter Maustaste: das Zoomen hat _panX/_panY (Anker-Korrektur)
            ' veraendert, die beim Pointer-Down gemerkte Pan-Basis ist damit veraltet. Ohne Neu-Verankern
            ' springt die Ansicht beim anschliessenden Ziehen auf den Stand VOR dem Zoomen zurueck
            ' (typisch: Bildmitte). Deshalb die Drag-Basis auf JETZT setzen.
            If _isPanDragging Then
                _panStartX = anchor.X
                _panStartY = anchor.Y
                _panStartOffsetX = _panX
                _panStartOffsetY = _panY
            End If
            e.Handled = True
        End Sub

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            _filmstripController = New FilmstripInteractionController(Me, New ViewportThumbnailTracker(),
                Function() TryCast(DataContext, EditorViewModel)?.FilmstripItems,
                Function() If(TryCast(DataContext, EditorViewModel) Is Nothing, -1, TryCast(DataContext, EditorViewModel).CurrentFilmstripIndex))
            AddHandler DataContextChanged, AddressOf HandleDataContextChanged
            Me.AddHandler(InputElement.KeyDownEvent, AddressOf OnEditorKeyDownTunnel, RoutingStrategies.Tunnel)
            AddHandler Loaded, Sub(s, e)
                ' Die Symbolgalerie ("Formen und Symbole") enthält mehrere tausend SVGs. Sie werden hier
                ' einmalig im Hintergrund geparst, damit das Aufklappen später nicht ruckelt.
                Dim ignored = SvgIcon.PreloadOutlineIconsAsync()
                RestoreRulerAndGridState()
                UpdateSliderLayout()
                UpdateInfoSidebarLayout()
                UpdateLayersPanelLayout()
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

        ''' <summary>Fenster-Tunnel: diese Kürzel müssen auch dann noch greifen, wenn zuvor ein
        ''' Overlay-Dialog den Fokus hatte - die View-Kürzel sind danach tot.</summary>
        Private Sub OnEditorKeyDownTunnel(sender As Object, e As KeyEventArgs)
            If e.Handled OrElse Not e.KeyModifiers.HasFlag(KeyModifiers.Control) Then Return
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return

            Select Case e.Key
                Case Key.S
                    If vm.CanSaveInPlace Then
                        vm.SaveCommand.Execute(Nothing)
                    Else
                        vm.SaveAsCommand.Execute(Nothing)
                    End If
                    e.Handled = True
            End Select
        End Sub

        Private Sub HandleDataContextChanged(sender As Object, e As EventArgs)
            If _currentVm IsNot Nothing Then
                RemoveHandler _currentVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
                RemoveHandler _currentVm.ImageGeometryChanged, AddressOf OnEditorImageGeometryChanged
                RemoveHandler _currentVm.SceneInvalidated, AddressOf OnSceneInvalidated
            End If
            _currentVm = TryCast(DataContext, EditorViewModel)
            If _currentVm IsNot Nothing Then
                AddHandler _currentVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
                AddHandler _currentVm.ImageGeometryChanged, AddressOf OnEditorImageGeometryChanged
                AddHandler _currentVm.SceneInvalidated, AddressOf OnSceneInvalidated
            End If
            _filmstripController.Reset()
        End Sub

        ''' STUFE 2: Region-Blits schreiben in DIESELBE WriteableBitmap-Instanz - fuer das Binding
        ''' aendert sich nichts, das Image-Control muss explizit neu zeichnen. Ausserdem ist DIES
        ''' jetzt der "gebackener Stand ist da"-Moment: die Mal-Vorschaulinie wartete frueher auf
        ''' einen DisplayImage-Wechsel, den es mit der persistenten Anzeige nicht mehr gibt.
        Private Sub OnSceneInvalidated(sender As Object, e As EventArgs)
            Me.FindControl(Of Image)("AfterImageControl")?.InvalidateVisual()
            HideBrushPreviewLineAfterBake()
        End Sub

        ''' <summary>Der KeyDown-Handler hängt an dieser UserControl - er sieht eine Taste nur, wenn der
        ''' Tastaturfokus INNERHALB der EditorView liegt. Beim Öffnen des Editors blieb er bisher dort,
        ''' wo er vorher war (Galerie/Viewer), und keins der Editor-Kürzel griff, bis man zufällig einen
        ''' fokussierbaren Regler oder ein Filmstreifen-Bild angeklickt hatte. Galerie und Viewer holen
        ''' sich den Fokus aus demselben Grund beim Anhängen.</summary>
        Protected Overrides Sub OnAttachedToVisualTree(e As Avalonia.VisualTreeAttachmentEventArgs)
            MyBase.OnAttachedToVisualTree(e)
            Dispatcher.UIThread.Post(Sub() Me.Focus(), DispatcherPriority.Background)
        End Sub

        ''' EditorViewModel lebt für die gesamte App-Laufzeit (eine Instanz, wiederverwendet bei
        ''' jedem Wechsel Galerie/Editor), während für jeden Editor-Aufruf eine neue EditorView
        ''' erzeugt wird (ViewLocator.Build -> Activator.CreateInstance). Ohne dieses Abmelden
        ''' würde die langlebige VM über den PropertyChanged-Delegate jede alte View-Instanz für
        ''' immer am Leben halten (Memory-Leak, wächst mit jedem Galerie<->Editor-Wechsel).
        Protected Overrides Sub OnDetachedFromVisualTree(e As Avalonia.VisualTreeAttachmentEventArgs)
            MyBase.OnDetachedFromVisualTree(e)
            ReleasePickSampleBitmap()
            If _currentVm IsNot Nothing Then
                RemoveHandler _currentVm.PropertyChanged, AddressOf OnViewModelPropertyChanged
                RemoveHandler _currentVm.ImageGeometryChanged, AddressOf OnEditorImageGeometryChanged
                RemoveHandler _currentVm.SceneInvalidated, AddressOf OnSceneInvalidated
                _currentVm = Nothing
            End If
        End Sub

        ''' Das angezeigte Bild hat neue Maße - entweder weil ein anderes geladen wurde oder weil ein
        ''' Beschnitt angewendet wurde. Der Zoom-Modus bleibt dabei erhalten (siehe ActiveZoomPreset):
        ''' bei Fit wird neu eingepasst, bei Actual auf 100% gesprungen, bei Manual bleiben Zoom und
        ''' Schwenk des Nutzers stehen.
        Private Sub ResetZoomForNewGeometry(vm As EditorViewModel)
            Select Case If(vm IsNot Nothing, vm.ActiveZoomPreset, ZoomPresetMode.Fit)
                Case ZoomPresetMode.Fit
                    _zoomInitialized = False
                Case ZoomPresetMode.Actual
                    _panX = 0
                    _panY = 0
                    SetZoom(ZoomToSlider(100.0))
                Case Else
                    ' Manual: _zoomSliderValue/_panX/_panY unverändert lassen
            End Select
            ' Hilfslinien liegen auf Bildpixeln - nach einem Beschnitt oder Bildwechsel bezeichnen
            ' dieselben Zahlen eine andere Stelle im Bild, deshalb werden sie verworfen.
            _guidesX.Clear()
            _guidesY.Clear()
            _isGuideDragging = False
            _guideDragIndex = -1
            UpdateSliderLayout()
        End Sub

        Private Sub OnEditorImageGeometryChanged(sender As Object, e As EventArgs)
            ResetZoomForNewGeometry(TryCast(sender, EditorViewModel))
        End Sub

        Private Sub OnViewModelPropertyChanged(sender As Object, e As PropertyChangedEventArgs)
            Select Case e.PropertyName
                Case NameOf(EditorViewModel.CurrentImage)
                    _sliderPosition = 0.5
                    ResetZoomForNewGeometry(TryCast(sender, EditorViewModel))
                Case NameOf(EditorViewModel.DisplayImage)
                    UpdateSliderLayout()
                    HideBrushPreviewLineAfterBake()
                Case NameOf(EditorViewModel.RetouchLivePatchImage),
                     NameOf(EditorViewModel.HasRetouchLivePatch),
                     NameOf(EditorViewModel.RetouchLivePatchLeftPercent),
                     NameOf(EditorViewModel.RetouchLivePatchTopPercent),
                     NameOf(EditorViewModel.RetouchLivePatchWidthPercent),
                     NameOf(EditorViewModel.RetouchLivePatchHeightPercent)
                    UpdateSliderLayout()
                Case NameOf(EditorViewModel.ShowBeforeImage)
                    UpdateSliderLayout()
                Case NameOf(EditorViewModel.ZoomDetailImage),
                     NameOf(EditorViewModel.ZoomDetailBeforeImage)
                    ' STUFE 3: asynchron gelandeter Detail-Ausschnitt - Overlay (neu) positionieren.
                    ' Terminiert: der Folge-Durchlauf trifft einen passenden Cache und loest nichts aus.
                    UpdateSliderLayout()
                Case NameOf(EditorViewModel.CurrentTool)
                    UpdateCropOverlayVisibility()
                    UpdateTextOverlayVisibility()
                    UpdateSelectionOverlayVisibility()
                    _hideBrushPreviewAfterBake = False
                    ShowBrushPreviewLine(False)
                Case NameOf(EditorViewModel.SelectedAnnotationIndex),
                     NameOf(EditorViewModel.HasSelectedAnnotation)
                    UpdateTextOverlayVisibility()
                    UpdateSliderLayout()
                Case NameOf(EditorViewModel.HasActiveSelection),
                     NameOf(EditorViewModel.SelectionMode),
                     NameOf(EditorViewModel.SelectionXPercent),
                     NameOf(EditorViewModel.SelectionYPercent),
                     NameOf(EditorViewModel.SelectionWidthPercent),
                     NameOf(EditorViewModel.SelectionHeightPercent),
                     NameOf(EditorViewModel.SelectionMaskPreviewImage),
                     NameOf(EditorViewModel.SelectionMaskEdgePointsX),
                     NameOf(EditorViewModel.SelectionMaskEdgePointsY),
                     NameOf(EditorViewModel.SelectionShapeMode),
                     NameOf(EditorViewModel.SelectionShapePointsX),
                     NameOf(EditorViewModel.SelectionShapePointsY)
                    UpdateSliderLayout()
                Case NameOf(EditorViewModel.IsPickingColorFromImage)
                    Dim pickCanvas = Me.FindControl(Of Canvas)("PreviewCanvas")
                    Dim vmForPick = TryCast(DataContext, EditorViewModel)
                    If pickCanvas IsNot Nothing AndAlso vmForPick IsNot Nothing Then
                        pickCanvas.Cursor = If(vmForPick.IsPickingColorFromImage, GetPipetteCursor(), Nothing)
                    End If
                    If vmForPick Is Nothing OrElse Not vmForPick.IsPickingColorFromImage Then ReleasePickSampleBitmap()
                Case NameOf(EditorViewModel.CurrentFilmstripIndex)
                    _filmstripController.ScrollToCurrent()
                Case NameOf(EditorViewModel.IsInfoSidebarVisible)
                    UpdateInfoSidebarLayout()
                Case NameOf(EditorViewModel.IsLayersPanelVisible)
                    UpdateLayersPanelLayout()
                Case NameOf(EditorViewModel.EditorGridSize)
                    UpdateGridOverlay()
                Case NameOf(EditorViewModel.AnnotationText),
                     NameOf(EditorViewModel.AnnotationFillColor),
                     NameOf(EditorViewModel.AnnotationStrokeColor),
                     NameOf(EditorViewModel.AnnotationFontSize),
                     NameOf(EditorViewModel.AnnotationFontFamily),
                     NameOf(EditorViewModel.AnnotationOpacity),
                     NameOf(EditorViewModel.AnnotationRotation),
                     NameOf(EditorViewModel.AnnotationStrokeWidth),
                     NameOf(EditorViewModel.AnnotationFillKind),
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

        ''' Für Zoom-Fit/Anzeige wird - wenn das Seitenverhältnis übereinstimmt (also nicht beschnitten/
        ''' geometrisch verändert) - auf die echten Bildmaße zurückgegriffen, damit z.B. "100% Zoom"
        ''' wirklich 1 Bildpixel = 1 Bildschirmpixel bedeutet. GetDisplayedImageRect MUSS dieselbe Größe
        ''' verwenden, da sonst Klick-/Zieh-Koordinaten nicht mehr zur tatsächlich angezeigten Bildgröße
        ''' passen.
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

            PositionRetouchLivePatch(ix, iy, iw, ih, vm)
            UpdateZoomDetailOverlay(ix, iy, iw, ih, cw, ch, vm, displayBitmap, showBefore)

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

            ' Breite Griffzone deckungsgleich über der Trennlinie (Linie ziehen + Cursor).
            Dim dividerHit = Me.FindControl(Of Border)("SliderDividerHit")
            If dividerHit IsNot Nothing Then
                dividerHit.IsVisible = showBefore
                Avalonia.Controls.Canvas.SetLeft(dividerHit, sliderX - 7)
                Avalonia.Controls.Canvas.SetTop(dividerHit, iy)
                dividerHit.Height = ih
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
                ' Selbstheilung: eine ohne aktiven Zug sichtbare Einrast-Hilfslinie ist immer ein
                ' Überbleibsel (verlorenes Release/Capture) - beim nächsten Layout-Durchlauf weg.
                HideTextSnapGuides()
            End If
            If Not _isSelectionDragging Then
                PositionSelectionOverlayFromViewModel(ix, iy, iw, ih)
            End If

            UpdateRulers()
            UpdateGuideLines()
            UpdateGridOverlay()
        End Sub

        ''' <summary>STUFE 3 (Zoom-Detail): meldet dem ViewModel den sichtbaren Bildausschnitt und ob
        ''' die Anzeige die Szenen-Aufloesung uebersteigt, und positioniert danach den vom ViewModel
        ''' gelieferten hochaufgeloesten Ausschnitt bildverankert ueber dem AfterImage. Waehrend
        ''' Vorher/Nachher laeuft das Detail zweigleisig (Nutzerwunsch 2026-07-17): die Nachher-Seite
        ''' wird rechts der Vergleichslinie geclippt, die Vorher-Seite (Original nur mit Geometrie)
        ''' links davon - beide Seiten werden beim Zoomen scharf.</summary>
        Private Sub UpdateZoomDetailOverlay(ix As Double, iy As Double, iw As Double, ih As Double,
                                            cw As Double, ch As Double, vm As EditorViewModel,
                                            displayBitmap As Bitmap, showBefore As Boolean)
            Dim detailImg = Me.FindControl(Of Image)("ZoomDetailImageControl")
            Dim beforeDetailImg = Me.FindControl(Of Image)("ZoomDetailBeforeImageControl")
            If detailImg Is Nothing OrElse vm Is Nothing Then Return

            Dim scenePx = Math.Max(1, If(displayBitmap IsNot Nothing, displayBitmap.PixelSize.Width, 1))
            Dim displayScale = iw / scenePx
            ' Sichtbarer Bildausschnitt in Bildanteilen 0..1 (Canvas-Schnittmenge).
            Dim visL = Math.Max(0.0, Math.Min(1.0, (-ix) / iw))
            Dim visT = Math.Max(0.0, Math.Min(1.0, (-iy) / ih))
            Dim visR = Math.Max(0.0, Math.Min(1.0, (cw - ix) / iw))
            Dim visB = Math.Max(0.0, Math.Min(1.0, (ch - iy) / ih))
            ' Früher aktivieren (0,8 statt 1,02): so ist das hochaufgelöste Detail schon geladen,
            ' BEVOR die Anzeige die Szenen-Auflösung übersteigt - bei hochauflösenden Quellen
            ' (z. B. 5760×8640, Szene auf ~3840 gedeckelt) fing das Nachschärfen sonst spürbar
            ' zu spät an (Nutzer-Befund 2026-07-17). Unterhalb 1,0 ist das Overlay optisch
            ' identisch zur Szene - der frühe Start ist reines Vorladen.
            Dim active = displayScale > 0.8 AndAlso visR > visL AndAlso visB > visT

            vm.UpdateZoomDetailViewport(visL, visT, visR, visB, active, showBefore)

            Dim bmp = vm.ZoomDetailImage
            If bmp Is Nothing OrElse Not active Then
                detailImg.IsVisible = False
                detailImg.Source = Nothing
                detailImg.Clip = Nothing
                If beforeDetailImg IsNot Nothing Then
                    beforeDetailImg.IsVisible = False
                    beforeDetailImg.Source = Nothing
                    beforeDetailImg.Clip = Nothing
                End If
                Return
            End If

            Dim detailLeft = ix + vm.ZoomDetailFracLeft * iw
            Dim detailTop = iy + vm.ZoomDetailFracTop * ih
            Dim detailW = Math.Max(1.0, vm.ZoomDetailFracWidth * iw)
            Dim detailH = Math.Max(1.0, vm.ZoomDetailFracHeight * ih)
            detailImg.Source = bmp
            Avalonia.Controls.Canvas.SetLeft(detailImg, detailLeft)
            Avalonia.Controls.Canvas.SetTop(detailImg, detailTop)
            detailImg.Width = detailW
            detailImg.Height = detailH

            If showBefore Then
                ' An der Vergleichslinie clippen (in lokalen Koordinaten des Detail-Overlays).
                Dim sliderX = ix + iw * _sliderPosition
                Dim clipX = Math.Max(0.0, Math.Min(detailW, sliderX - detailLeft))
                detailImg.Clip = New RectangleGeometry(New Avalonia.Rect(clipX, 0, Math.Max(0.0, detailW - clipX), detailH))

                Dim beforeBmp = vm.ZoomDetailBeforeImage
                If beforeDetailImg IsNot Nothing AndAlso beforeBmp IsNot Nothing Then
                    beforeDetailImg.Source = beforeBmp
                    Avalonia.Controls.Canvas.SetLeft(beforeDetailImg, detailLeft)
                    Avalonia.Controls.Canvas.SetTop(beforeDetailImg, detailTop)
                    beforeDetailImg.Width = detailW
                    beforeDetailImg.Height = detailH
                    beforeDetailImg.Clip = New RectangleGeometry(New Avalonia.Rect(0, 0, clipX, detailH))
                    beforeDetailImg.IsVisible = True
                ElseIf beforeDetailImg IsNot Nothing Then
                    beforeDetailImg.IsVisible = False
                    beforeDetailImg.Source = Nothing
                End If
            Else
                detailImg.Clip = Nothing
                If beforeDetailImg IsNot Nothing Then
                    beforeDetailImg.IsVisible = False
                    beforeDetailImg.Source = Nothing
                    beforeDetailImg.Clip = Nothing
                End If
            End If
            detailImg.IsVisible = True
        End Sub

        Private Sub PositionRetouchLivePatch(ix As Double, iy As Double, iw As Double, ih As Double, vm As EditorViewModel)
            Dim patch = Me.FindControl(Of Image)("RetouchLivePatchImage")
            If patch Is Nothing OrElse vm Is Nothing Then Return

            patch.IsVisible = vm.HasRetouchLivePatch
            If Not vm.HasRetouchLivePatch Then Return

            Dim left = ix + vm.RetouchLivePatchLeftPercent / 100.0 * iw
            Dim top = iy + vm.RetouchLivePatchTopPercent / 100.0 * ih
            Dim width = Math.Max(1.0, vm.RetouchLivePatchWidthPercent / 100.0 * iw)
            Dim height = Math.Max(1.0, vm.RetouchLivePatchHeightPercent / 100.0 * ih)

            Avalonia.Controls.Canvas.SetLeft(patch, left)
            Avalonia.Controls.Canvas.SetTop(patch, top)
            patch.Width = width
            patch.Height = height
        End Sub

        Private Sub OnSliderPointerPressed(sender As Object, e As PointerPressedEventArgs)
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            If canvas Is Nothing Then Return
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim pointerPoint = e.GetCurrentPoint(Nothing)

            If pointerPoint.Properties.IsRightButtonPressed Then
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

            If Not pointerPoint.Properties.IsLeftButtonPressed Then Return

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

            If vm IsNot Nothing Then
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                If imageRect.Width > 0 AndAlso imageRect.Height > 0 AndAlso
                   Not imageRect.Contains(e.GetPosition(canvas)) AndAlso
                   Not BrushCircleTouchesImage(e.GetPosition(canvas), imageRect, vm) Then
                    ' Anfasser (und Objektteile) koennen AUSSERHALB des Bildes liegen, wenn das Objekt
                    ' am Rand steht: erst pruefen, ob der Klick eine Zone der aktuellen Selektion
                    ' trifft - sonst waere ein solcher Griff nie greifbar, weil der Klick sofort
                    ' deselektierte.
                    If vm.HasSelectedAnnotation Then
                        Dim overlayOutside = Me.FindControl(Of Border)("TextOverlay")
                        If overlayOutside IsNot Nothing AndAlso overlayOutside.IsVisible Then
                            Dim outsideMode = GetTextDragMode(e.GetPosition(canvas), GetTextOverlayRect(), vm.AnnotationRotation)
                            If outsideMode <> TextDragMode.None Then
                                OnTextOverlayPointerPressed(overlayOutside, e)
                                Return
                            End If
                        End If
                    End If
                    ClearEditorSelections(vm)
                    e.Handled = True
                    Return
                End If
            End If

            ' Eine bereits gesetzte Hilfslinie lässt sich direkt im Bild greifen. Das muss vor allen
            ' Werkzeug-Zweigen passieren, sonst zeichnet der Pinsel darüber statt sie zu verschieben.
            If _showRulers AndAlso vm IsNot Nothing Then
                Dim guideRect = GetDisplayedImageRect(canvas, vm)
                Dim guideIsVertical As Boolean
                Dim guideIndex = HitTestGuide(e.GetPosition(canvas), guideRect, vm, guideIsVertical)
                If guideIndex >= 0 Then
                    BeginGuideDrag(guideIsVertical, guideIndex)
                    e.Pointer.Capture(canvas)
                    e.Handled = True
                    Return
                End If
            End If

            ' Der Griff des Vergleichsreglers liegt als Kind auf diesem Canvas, ein Druck darauf landet
            ' also hier. Ohne diesen Zweig liefe er weiter unten in den Objekt-Hit-Test und würde ein
            ' zufällig darunterliegendes Objekt selektieren (samt Werkzeugwechsel), statt den Regler zu
            ' ziehen. Das Verschieben durch Klick irgendwo aufs Bild bleibt der Rückfall am Ende.
            If vm IsNot Nothing AndAlso vm.ShowBeforeImage AndAlso IsWithinComparisonSlider(e.Source) Then
                _isDraggingSlider = True
                e.Pointer.Capture(canvas)
                MoveSlider(e.GetPosition(canvas).X, canvas)
                e.Handled = True
                Return
            End If

            ' Die Resize-/Rotier-Griffe ragen per Margin über die Bounds des TextOverlay-Borders hinaus,
            ' daher landen Klicks genau darauf hier auf dem Canvas statt auf dem Border - Griff-Erkennung
            ' deshalb zusätzlich hier prüfen. Das muss vor den werkzeugspezifischen Zweigen geschehen:
            ' das Verschieben-Werkzeug soll die Griffe greifen, bevor der allgemeine Canvas-Klickpfad
            ' greift.
            ' Bei verstecktem Overlay liefert GetTextOverlayRect ein leeres Rechteck im Canvas-Ursprung,
            ' dessen Griffpunkte ein Klick oben links zufällig träfe - daher die IsVisible-Prüfung.
            If vm IsNot Nothing AndAlso vm.HasSelectedAnnotation Then
                Dim overlayForHandles = Me.FindControl(Of Border)("TextOverlay")
                If overlayForHandles IsNot Nothing AndAlso overlayForHandles.IsVisible Then
                    Dim handleRect = GetTextOverlayRect()
                    Dim handleMode = GetTextDragMode(e.GetPosition(canvas), handleRect, vm.AnnotationRotation)
                    If handleMode <> TextDragMode.None AndAlso handleMode <> TextDragMode.Move Then
                        OnTextOverlayPointerPressed(overlayForHandles, e)
                        Return
                    End If
                End If
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
                        ' Auswahl + Ziehen in EINER Geste (siehe gleicher Block im allgemeinen Pfad).
                        Dim overlayAfterSelect = Me.FindControl(Of Border)("TextOverlay")
                        If overlayAfterSelect IsNot Nothing AndAlso overlayAfterSelect.IsVisible Then
                            OnTextOverlayPointerPressed(overlayAfterSelect, e)
                        End If
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
                Dim rawPos = e.GetPosition(canvas)
                Dim pos = ClampPointToRect(rawPos, imageRect)
                If vm.SelectionMode = "Move" Then
                    Dim xPct = (pos.X - imageRect.Left) / imageRect.Width * 100.0
                    Dim yPct = (pos.Y - imageRect.Top) / imageRect.Height * 100.0
                    Const hitSlopPixels As Double = 10.0
                    Dim hitSlopXPercent = hitSlopPixels / imageRect.Width * 100.0
                    Dim hitSlopYPercent = hitSlopPixels / imageRect.Height * 100.0
                    Dim hitIndex = vm.HitTestAnnotation(xPct, yPct, hitSlopXPercent, hitSlopYPercent)
                    If hitIndex >= 0 Then
                        vm.SelectedAnnotationIndex = hitIndex
                        If vm.CurrentTool = EditorTool.Text Then FocusTextOverlayEditor()
                        ' Auswahl + Ziehen in EINER Geste: der Selektions-Setter hat das TextOverlay
                        ' synchron positioniert - den Move-Drag direkt auf DIESEM Press starten, statt
                        ' ein Loslassen und erneutes Anklicken zu verlangen.
                        Dim overlayAfterSelect = Me.FindControl(Of Border)("TextOverlay")
                        If overlayAfterSelect IsNot Nothing AndAlso overlayAfterSelect.IsVisible Then
                            OnTextOverlayPointerPressed(overlayAfterSelect, e)
                        End If
                        e.Handled = True
                        Return
                    ElseIf vm.HasSelectedAnnotation Then
                        vm.SelectedAnnotationIndex = -1
                    End If
                End If
                Dim clickedInsideSelection = vm.HasActiveSelection AndAlso vm.SelectionMode <> "MagicWand" AndAlso IsPointInsideSelection(rawPos, imageRect, vm)
                If clickedInsideSelection Then
                    If vm.SelectionMode = "Move" OrElse vm.SelectionCombineMode = "New" Then
                        _isSelectionMoveDragging = True
                        _selectionMoveLastPoint = pos
                        e.Pointer.Capture(canvas)
                        e.Handled = True
                        Return
                    End If
                    ' Add/Subtract/Intersect starten auch innerhalb der bestehenden Auswahl eine neue
                    ' Kandidatenform; nur "Neu" nutzt den Treffer zum Verschieben der Auswahl.
                End If
                _selectionDragReplacesExisting = vm.HasActiveSelection AndAlso
                                                 vm.SelectionMode <> "MagicWand" AndAlso
                                                 vm.SelectionMode <> "Move" AndAlso
                                                 vm.SelectionCombineMode = "New" AndAlso
                                                 Not clickedInsideSelection
                If _selectionDragReplacesExisting Then SetCurrentSelectionOverlayVisible(False)
                Select Case vm.SelectionMode
                    Case "Move"
                        ' Verschieben ist der Default im Auswahlpanel: außerhalb einer bestehenden Auswahl
                        ' wird keine neue Auswahl gestartet.
                    Case "MagicWand"
                        ' Einzelklick: zusammenhängende Farbfläche wählen (kein Ziehen).
                        Dim xPct = (pos.X - imageRect.Left) / imageRect.Width * 100.0
                        Dim yPct = (pos.Y - imageRect.Top) / imageRect.Height * 100.0
                        vm.SetSelectionMagicWand(xPct, yPct)
                    Case "Lasso"
                        _lassoPoints.Clear()
                        _lassoPoints.Add(rawPos)
                        _isLassoDrawing = True
                        e.Pointer.Capture(canvas)
                    Case Else   ' Rectangle, Ellipse - Rechteck aufziehen
                        _selectionStart = rawPos
                        _selectionEnd = rawPos
                        _isSelectionDragging = True
                        e.Pointer.Capture(canvas)
                        UpdateSelectionOverlayFromDrag()
                End Select
                e.Handled = True
                Return
            End If

            If vm IsNot Nothing AndAlso vm.CurrentTool = EditorTool.Retouch Then
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
                ' NICHT auf den Bildrand klemmen: ein Ansetzen knapp ausserhalb meint den Teil des
                ' Pinselkreises, der ueber dem Bild liegt. AddRetouchSpot begrenzt den Mittelpunkt
                ' auf einen Radius Abstand, das Zeichnen klemmt auf die Bitmapgrenzen.
                Dim pos = e.GetPosition(canvas)
                Dim xPct = (pos.X - imageRect.Left) / imageRect.Width * 100.0
                Dim yPct = (pos.Y - imageRect.Top) / imageRect.Height * 100.0

                ' Alt+Klick setzt nur die Quelle des Stempels und beginnt keinen Zug - wie in Photoshop
                ' und GIMP. Beim Verwischen gibt es keine Quelle, dort bleibt der Modifikator wirkungslos.
                If vm.IsCloneMode AndAlso e.KeyModifiers.HasFlag(KeyModifiers.Alt) Then
                    vm.SetCloneSource(xPct, yPct)
                    UpdateCloneSourceMarker(pos, imageRect, vm)
                    e.Handled = True
                    Return
                End If

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
                _hideBrushPreviewAfterBake = False
                _brushPoints.Clear()
                AddBrushPoint(e.GetPosition(canvas), imageRect)
                _isBrushDrawing = True
                ShowBrushPreviewLine(True)
                UpdateBrushPreviewLine(imageRect, vm)
                e.Pointer.Capture(canvas)
                e.Handled = True
                Return
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
                        ' Auswahl + Ziehen in EINER Geste: der Selektions-Setter hat das TextOverlay
                        ' synchron positioniert - den Move-Drag direkt auf DIESEM Press starten, statt
                        ' ein Loslassen und erneutes Anklicken zu verlangen.
                        Dim overlayAfterSelect = Me.FindControl(Of Border)("TextOverlay")
                        If overlayAfterSelect IsNot Nothing AndAlso overlayAfterSelect.IsVisible Then
                            OnTextOverlayPointerPressed(overlayAfterSelect, e)
                        End If
                        e.Handled = True
                        Return
                    ElseIf vm.HasSelectedAnnotation Then
                        vm.SelectedAnnotationIndex = -1
                        If IsLayerPlacementTool(vm.CurrentTool) Then
                            e.Handled = True
                            Return
                        End If
                    ElseIf Not String.IsNullOrEmpty(vm.PendingInsertKind) Then
                        Dim pendingKind = vm.PendingInsertKind
                        If pendingKind = "Image" Then
                            PlacePendingImageAsync(xPct, yPct)
                        Else
                            vm.AddAnnotationAt(pendingKind, xPct, yPct)
                            If vm.CurrentTool = EditorTool.Text Then FocusTextOverlayEditor()
                        End If
                        e.Handled = True
                        Return
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

        Private Sub ClearEditorSelections(vm As EditorViewModel)
            If vm Is Nothing Then Return

            If _isSelectionDragging OrElse _isLassoDrawing OrElse _isSelectionMoveDragging Then
                CancelSelectionDrag()
            End If
            _isCropDragging = False
            _cropDragMode = CropDragMode.None
            If _isTextDragging Then vm.EndSelectedAnnotationPlacementEdit()
            _isTextDragging = False
            _textDragMode = TextDragMode.None
            HideTextSizeBadge()
            HideTextSnapGuides()

            If vm.HasSelectedAnnotation OrElse Not String.IsNullOrEmpty(vm.PendingInsertKind) Then
                vm.SelectedAnnotationIndex = -1
                vm.PendingInsertKind = ""
            End If
            If vm.HasActiveSelection Then vm.ClearSelection()
            If vm.HasCropChanges Then vm.ClearPendingCrop()

            HideSelectionDragOverlay()
            UpdateSelectionOverlayVisibility()
            UpdateSliderLayout()
        End Sub

        Private Sub OnSliderPointerMoved(sender As Object, e As PointerEventArgs)
            Dim cursorCanvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim cursorVm = TryCast(DataContext, EditorViewModel)
            If cursorCanvas IsNot Nothing AndAlso cursorVm IsNot Nothing Then
                UpdateBrushCursorPreview(e.GetPosition(cursorCanvas), GetDisplayedImageRect(cursorCanvas, cursorVm), cursorVm)
                UpdateMousePositionText(e.GetPosition(cursorCanvas), GetDisplayedImageRect(cursorCanvas, cursorVm), cursorVm)
                UpdateRulerMarkers(e.GetPosition(cursorCanvas))
            End If

            Dim guideHoverIsVertical As Boolean = False
            Dim guideHoverIndex As Integer = -1
            If _showRulers AndAlso Not _isGuideDragging AndAlso cursorCanvas IsNot Nothing AndAlso cursorVm IsNot Nothing Then
                guideHoverIndex = HitTestGuide(e.GetPosition(cursorCanvas), GetDisplayedImageRect(cursorCanvas, cursorVm),
                                               cursorVm, guideHoverIsVertical)
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

                ' Live-Vorschau für den Farbkreis im ColorMixer: nur solange der Zeiger wirklich über dem
                ' Bild steht, sonst zeigt der Kreis wieder die gewählte Farbe.
                Dim pickRect = GetDisplayedImageRect(cursorCanvas, cursorVm)
                Dim pickPoint = e.GetPosition(cursorCanvas)
                If pickRect.Contains(pickPoint) Then
                    cursorVm.ColorPickPreview = SampleDisplayedColor(cursorVm, pickRect, pickPoint)
                Else
                    cursorVm.ColorPickPreview = Nothing
                End If
            ElseIf _isGuideDragging AndAlso cursorCanvas IsNot Nothing Then
                cursorCanvas.Cursor = If(_guideDragIsVertical, GuideCursorVertical, GuideCursorHorizontal)
            ElseIf guideHoverIndex >= 0 AndAlso cursorCanvas IsNot Nothing Then
                cursorCanvas.Cursor = If(guideHoverIsVertical, GuideCursorVertical, GuideCursorHorizontal)
            ElseIf Not _isPanDragging AndAlso Not _isCropDragging AndAlso Not _isBrushDrawing AndAlso
               Not _isRetouching AndAlso Not _isTextDragging AndAlso Not _isDraggingSlider AndAlso Not _isSelectionDragging AndAlso Not _isSelectionMoveDragging AndAlso Not _isLassoDrawing AndAlso
               cursorCanvas IsNot Nothing AndAlso cursorVm IsNot Nothing AndAlso
               cursorVm.HasSelectedAnnotation AndAlso IsLayerPlacementTool(cursorVm.CurrentTool) Then
                Dim mode = GetTextDragMode(e.GetPosition(cursorCanvas), GetTextOverlayRect(), cursorVm.AnnotationRotation)
                cursorCanvas.Cursor = GetCursorForTextDragMode(mode, IsSelectedAnnotationTextLayer(cursorVm))
            ElseIf cursorCanvas IsNot Nothing Then
                cursorCanvas.Cursor = Nothing
            End If

            If _isGuideDragging Then
                If cursorCanvas Is Nothing Then Return
                UpdateGuideDrag(e.GetPosition(cursorCanvas))
                e.Handled = True
                Return
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
            If _isSelectionMoveDragging Then
                Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
                Dim vm = TryCast(DataContext, EditorViewModel)
                If canvas Is Nothing OrElse vm Is Nothing Then Return
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
                Dim pos = ClampPointToRect(e.GetPosition(canvas), imageRect)
                Dim dxPercent = (pos.X - _selectionMoveLastPoint.X) / imageRect.Width * 100.0
                Dim dyPercent = (pos.Y - _selectionMoveLastPoint.Y) / imageRect.Height * 100.0
                vm.MoveSelection(dxPercent, dyPercent)
                _selectionMoveLastPoint = pos
                e.Handled = True
                Return
            End If
            If _isLassoDrawing Then
                Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
                Dim vm = TryCast(DataContext, EditorViewModel)
                If canvas Is Nothing OrElse vm Is Nothing Then Return
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
                _lassoPoints.Add(e.GetPosition(canvas))
                UpdateLassoOverlayFromPoints()
                e.Handled = True
                Return
            End If
            If _isSelectionDragging Then
                Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
                Dim vm = TryCast(DataContext, EditorViewModel)
                If canvas Is Nothing OrElse vm Is Nothing Then Return
                Dim imageRect = GetDisplayedImageRect(canvas, vm)
                If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
                _selectionEnd = e.GetPosition(canvas)
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
            If _isGuideDragging Then
                Dim guideCanvas = Me.FindControl(Of Canvas)("PreviewCanvas")
                If guideCanvas IsNot Nothing Then EndGuideDrag(e.GetPosition(guideCanvas))
                e.Pointer.Capture(Nothing)
                e.Handled = True
                Return
            End If

            Dim wasCropDragging = _isCropDragging
            Dim wasSelectionDragging = _isSelectionDragging
            Dim wasLassoDrawing = _isLassoDrawing
            If _isCropDragging Then
                CommitCropDrag()
            End If
            If _isSelectionDragging Then
                CommitSelectionDrag()
            End If
            If _isLassoDrawing Then
                CommitLassoSelection()
                _lassoPoints.Clear()
            End If
            _isDraggingSlider = False
            _isPanDragging = False
            _isCropDragging = False
            _isSelectionDragging = False
            _isSelectionMoveDragging = False
            _selectionDragReplacesExisting = False
            _isLassoDrawing = False
            If wasSelectionDragging OrElse wasLassoDrawing Then UpdateSelectionOverlayVisibility()
            If _isBrushDrawing Then
                Dim vm = TryCast(DataContext, EditorViewModel)
                Dim shouldWaitForBakedPreview = vm IsNot Nothing AndAlso _brushPoints.Count >= 2
                If vm IsNot Nothing Then vm.AddBrushStroke(_brushPoints, vm.IsEraserMode)
                _brushPoints.Clear()
                _hideBrushPreviewAfterBake = shouldWaitForBakedPreview
                If Not _hideBrushPreviewAfterBake Then ShowBrushPreviewLine(False)
            End If
            _isBrushDrawing = False
            If _isRetouching Then
                Dim vm = TryCast(DataContext, EditorViewModel)
                vm?.CommitRetouchStroke()
            End If
            _isRetouching = False
            _cropDragMode = CropDragMode.None
            e.Pointer.Capture(Nothing)

            ' Ein zu kleiner Zieh-Vorgang (z. B. ein bloßer Klick) wird von CommitCropDrag verworfen,
            ' ohne den ViewModel-Zuschnitt zu ändern - der Overlay muss dann wieder auf den tatsächlichen
            ' Zuschnitt zurückgesetzt werden, sonst bleiben die Anfasspunkte an der kollabierten Vorschau hängen.
            If wasCropDragging Then
                UpdateSliderLayout()
            End If
            If wasSelectionDragging OrElse wasLassoDrawing Then
                HideSelectionDragOverlay()
                UpdateSliderLayout()
            End If
        End Sub

        Private Sub AddBrushPoint(position As Avalonia.Point, imageRect As Avalonia.Rect)
            If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
            ' Wie bei der Retusche: bis zu einem Pinselradius ausserhalb zulassen, damit am Bildrand
            ' der sichtbare Teilkreis wirkt statt eines auf den Rand gezogenen Vollkreises.
            Dim reach = BrushDiameterOnScreen(imageRect, TryCast(DataContext, EditorViewModel)) / 2.0
            Dim pos = ClampPointToRect(position, imageRect.Inflate(reach))
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
            Dim outline = Me.FindControl(Of Polyline)("BrushPreviewOutlineLine")
            If line IsNot Nothing Then
                line.IsVisible = visible
                If Not visible Then line.Points = New Avalonia.Collections.AvaloniaList(Of Avalonia.Point)()
            End If
            If outline IsNot Nothing Then
                outline.IsVisible = False
                If Not visible Then outline.Points = New Avalonia.Collections.AvaloniaList(Of Avalonia.Point)()
            End If
        End Sub

        Private Sub HideBrushPreviewLineAfterBake()
            If Not _hideBrushPreviewAfterBake Then Return
            _hideBrushPreviewAfterBake = False
            ShowBrushPreviewLine(False)
        End Sub

        Private Sub UpdateBrushPreviewLine(imageRect As Avalonia.Rect, vm As EditorViewModel)
            If Not _isBrushDrawing Then Return
            Dim line = Me.FindControl(Of Polyline)("BrushPreviewLine")
            Dim outline = Me.FindControl(Of Polyline)("BrushPreviewOutlineLine")
            If line Is Nothing OrElse imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return

            Dim pts As New Avalonia.Collections.AvaloniaList(Of Avalonia.Point)()
            For Each p In _brushPoints
                pts.Add(New Avalonia.Point(imageRect.Left + p.X / 100.0 * imageRect.Width, imageRect.Top + p.Y / 100.0 * imageRect.Height))
            Next
            line.Points = pts
            If outline IsNot Nothing Then outline.Points = pts

            Dim isEraser = vm.IsEraserMode
            Dim scale = 1.0
            If vm.CurrentImage IsNot Nothing AndAlso vm.CurrentImage.PixelSize.Width > 0 AndAlso vm.CurrentImage.PixelSize.Height > 0 Then
                scale = Math.Min(imageRect.Width / vm.CurrentImage.PixelSize.Width, imageRect.Height / vm.CurrentImage.PixelSize.Height)
            End If
            Dim strokeThickness = Math.Max(1.0, vm.BrushSize * scale)
            line.StrokeThickness = strokeThickness
            If isEraser Then
                Dim eraserFill = vm.EraserFillColorValue
                If eraserFill.A <= 0 Then
                    line.Stroke = TransparentEraserPreviewBrush
                Else
                    line.Stroke = New SolidColorBrush(eraserFill)
                End If
                line.StrokeDashArray = Nothing
                line.Opacity = Math.Max(0.15, Math.Min(1.0, vm.BrushFlow / 100.0))
                If outline IsNot Nothing Then
                    outline.IsVisible = True
                    outline.Stroke = New SolidColorBrush(Color.Parse("#CC1A1D24"))
                    outline.StrokeThickness = strokeThickness + 2.0
                    outline.StrokeDashArray = Nothing
                    outline.Opacity = 0.72
                End If
            Else
                line.Stroke = vm.AnnotationStrokeBrush
                line.StrokeDashArray = Nothing
                line.Opacity = Math.Max(0.15, (vm.BrushOpacity / 100.0) * (vm.BrushFlow / 100.0))
                If outline IsNot Nothing Then outline.IsVisible = False
            End If
        End Sub

        Private Shared Function BuildTransparentEraserPreviewBrush() As IBrush
            Const tileSize As Double = 12.0
            Dim half = tileSize / 2.0
            Dim group As New DrawingGroup()
            group.Children.Add(New GeometryDrawing With {
                .Brush = New SolidColorBrush(Color.FromRgb(224, 224, 224)),
                .Geometry = New RectangleGeometry(New Avalonia.Rect(0, 0, tileSize, tileSize))
            })
            group.Children.Add(New GeometryDrawing With {
                .Brush = New SolidColorBrush(Color.FromRgb(142, 142, 142)),
                .Geometry = New RectangleGeometry(New Avalonia.Rect(0, 0, half, half))
            })
            group.Children.Add(New GeometryDrawing With {
                .Brush = New SolidColorBrush(Color.FromRgb(142, 142, 142)),
                .Geometry = New RectangleGeometry(New Avalonia.Rect(half, half, half, half))
            })
            Return New DrawingBrush(group) With {
                .TileMode = TileMode.Tile,
                .Stretch = Stretch.None,
                .DestinationRect = New RelativeRect(New Avalonia.Rect(0, 0, tileSize, tileSize), RelativeUnit.Absolute)
            }
        End Function

        ''' <summary>Durchmesser des Pinsel-/Retuschekreises in BILDSCHIRM-Pixeln - dieselbe Rechnung,
        ''' die auch den angezeigten Cursorring bemisst, damit Ring und Wirkung nie auseinanderlaufen.
        ''' 0, wenn gerade kein Pixelwerkzeug aktiv ist.</summary>
        Private Function BrushDiameterOnScreen(imageRect As Avalonia.Rect, vm As EditorViewModel) As Double
            If vm Is Nothing Then Return 0
            If vm.CurrentTool <> EditorTool.Draw AndAlso vm.CurrentTool <> EditorTool.Retouch Then Return 0
            Dim scale = 1.0
            If vm.CurrentImage IsNot Nothing AndAlso vm.CurrentImage.PixelSize.Width > 0 AndAlso vm.CurrentImage.PixelSize.Height > 0 Then
                scale = Math.Min(imageRect.Width / vm.CurrentImage.PixelSize.Width, imageRect.Height / vm.CurrentImage.PixelSize.Height)
            End If
            Return Math.Max(4.0, If(vm.CurrentTool = EditorTool.Draw, vm.BrushSize, vm.RetouchRadius * 2.0) * scale)
        End Function

        ''' <summary>Ragt der Pinselkreis noch ins Bild, obwohl der Zeiger daneben steht?
        '''
        ''' Das entscheidet, ob ein Druck am Bildrand ein Strich ist oder ein Klick ins Leere. Vorher
        ''' zaehlte allein die Zeigerposition: wer mit grossem Pinsel bewusst am Rand ansetzte, dessen
        ''' Klick wurde komplett verworfen, obwohl der halbe Kreis ueber dem Bild lag - Pinsel und
        ''' Retusche "machten dann nichts" (Nutzer-Befund 2026-07-20). Alle tieferen Ebenen klemmen
        ''' den Punkt laengst sauber ins Bild (ClampPointToRect, AddRetouchSpot, DrawRetouchSpot);
        ''' sie wurden nur nie erreicht. Ein Klick weit weg vom Bild deselektiert weiterhin.</summary>
        Private Function BrushCircleTouchesImage(position As Avalonia.Point, imageRect As Avalonia.Rect, vm As EditorViewModel) As Boolean
            Dim radius = BrushDiameterOnScreen(imageRect, vm) / 2.0
            If radius <= 0 Then Return False
            ' Abstand vom Zeiger zum naechsten Punkt des Bildrechtecks.
            Dim dx = Math.Max(imageRect.Left - position.X, Math.Max(0.0, position.X - imageRect.Right))
            Dim dy = Math.Max(imageRect.Top - position.Y, Math.Max(0.0, position.Y - imageRect.Bottom))
            Return (dx * dx + dy * dy) < radius * radius
        End Function

        Private Sub UpdateBrushCursorPreview(position As Avalonia.Point, imageRect As Avalonia.Rect, vm As EditorViewModel)
            Dim cursor = Me.FindControl(Of Ellipse)("BrushCursorPreview")
            If cursor Is Nothing Then Return

            Dim showCursor = imageRect.Width > 0 AndAlso imageRect.Height > 0 AndAlso
                              (vm.CurrentTool = EditorTool.Draw OrElse vm.CurrentTool = EditorTool.Retouch)
            cursor.IsVisible = showCursor
            If Not showCursor Then Return

            Dim diameter = BrushDiameterOnScreen(imageRect, vm)
            cursor.Width = diameter
            cursor.Height = diameter
            Canvas.SetLeft(cursor, position.X - diameter / 2.0)
            Canvas.SetTop(cursor, position.Y - diameter / 2.0)

            UpdateCloneSourceMarker(position, imageRect, vm)
        End Sub

        ''' Zeichnet den gestrichelten Ring an der Stelle, aus der gerade kopiert wird. Vor dem ersten
        ''' Punkt eines Zuges ist das der per Alt+Klick gesetzte Quellpunkt; danach wandert er im
        ''' gemerkten Versatz mit dem Zeiger mit, damit sichtbar ist, was übertragen wird.
        Private Sub UpdateCloneSourceMarker(position As Avalonia.Point, imageRect As Avalonia.Rect, vm As EditorViewModel)
            Dim marker = Me.FindControl(Of Ellipse)("CloneSourceMarker")
            If marker Is Nothing Then Return

            If vm Is Nothing OrElse Not vm.IsCloneMode OrElse Not vm.HasCloneSource OrElse
               imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then
                marker.IsVisible = False
                Return
            End If

            Dim xPct = (position.X - imageRect.Left) / imageRect.Width * 100.0
            Dim yPct = (position.Y - imageRect.Top) / imageRect.Height * 100.0
            Dim sample = vm.GetCloneSamplePercent(xPct, yPct)
            If Not sample.IsValid Then
                marker.IsVisible = False
                Return
            End If

            Dim scale = 1.0
            If vm.CurrentImage IsNot Nothing AndAlso vm.CurrentImage.PixelSize.Width > 0 AndAlso vm.CurrentImage.PixelSize.Height > 0 Then
                scale = Math.Min(imageRect.Width / vm.CurrentImage.PixelSize.Width, imageRect.Height / vm.CurrentImage.PixelSize.Height)
            End If
            Dim diameter = Math.Max(4.0, vm.RetouchRadius * 2.0 * scale)

            marker.IsVisible = True
            marker.Width = diameter
            marker.Height = diameter
            Canvas.SetLeft(marker, imageRect.Left + sample.X / 100.0 * imageRect.Width - diameter / 2.0)
            Canvas.SetTop(marker, imageRect.Top + sample.Y / 100.0 * imageRect.Height - diameter / 2.0)
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

        ' Klappt die Ebenen-Panel-Spalte (Index 4) auf 0 zusammen, wenn das Panel aus ist - wie die
        ' Info-Seitenleiste (Spalte 3). Die Border-Sichtbarkeit selbst hängt zusätzlich an der Bindung.
        Private Sub UpdateLayersPanelLayout()
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim root = Me.FindControl(Of Grid)("EditorRootGrid")
            If root Is Nothing Then Return

            If root.ColumnDefinitions.Count >= 5 Then
                root.ColumnDefinitions(4).Width = If(vm IsNot Nothing AndAlso vm.IsLayersPanelVisible, New GridLength(300), New GridLength(0))
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
            If Not _isGuideDragging Then HideRulerMarkers()
        End Sub

        Private Sub UpdateRulers()
            Dim topRuler = Me.FindControl(Of RulerControl)("TopRuler")
            Dim leftRuler = Me.FindControl(Of RulerControl)("LeftRuler")
            If topRuler Is Nothing OrElse leftRuler Is Nothing OrElse Not _showRulers Then Return

            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas Is Nothing OrElse vm Is Nothing OrElse vm.CurrentImage Is Nothing Then Return

            ' Die Skala zählt echte Bildpixel - dieselbe Bezugsgröße wie die Positionsanzeige in
            ' UpdateMousePositionText, damit Lineal und Statuszeile denselben Wert nennen.
            Dim pixelSize = vm.CurrentImage.PixelSize
            If pixelSize.Width <= 0 OrElse pixelSize.Height <= 0 Then Return
            Dim imageRect = GetDisplayedImageRect(canvas, vm)
            If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return

            topRuler.Origin = imageRect.Left
            topRuler.PixelsPerUnit = imageRect.Width / pixelSize.Width
            topRuler.ImageLength = pixelSize.Width

            leftRuler.Origin = imageRect.Top
            leftRuler.PixelsPerUnit = imageRect.Height / pixelSize.Height
            leftRuler.ImageLength = pixelSize.Height
        End Sub

        Private Sub UpdateGridOverlay()
            Dim overlay = Me.FindControl(Of PixelGridOverlay)("GridOverlay")
            If overlay Is Nothing OrElse Not _showGrid Then Return

            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas Is Nothing OrElse vm Is Nothing OrElse vm.CurrentImage Is Nothing Then Return

            Dim pixelSize = vm.CurrentImage.PixelSize
            If pixelSize.Width <= 0 OrElse pixelSize.Height <= 0 Then Return
            Dim imageRect = GetDisplayedImageRect(canvas, vm)
            If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return

            Avalonia.Controls.Canvas.SetLeft(overlay, 0)
            Avalonia.Controls.Canvas.SetTop(overlay, 0)
            overlay.Width = canvas.Bounds.Width
            overlay.Height = canvas.Bounds.Height
            overlay.ImageOrigin = imageRect.TopLeft
            overlay.PixelsPerUnit = imageRect.Width / pixelSize.Width
            overlay.ImageSize = New Avalonia.Size(pixelSize.Width, pixelSize.Height)
            overlay.GridSize = vm.EditorGridSize
        End Sub

        ''' Zeigt auf beiden Linealen, wo der Mauszeiger steht. NaN blendet die Markierung aus.
        Private Sub UpdateRulerMarkers(canvasPosition As Avalonia.Point)
            If Not _showRulers Then Return
            Dim topRuler = Me.FindControl(Of RulerControl)("TopRuler")
            Dim leftRuler = Me.FindControl(Of RulerControl)("LeftRuler")
            If topRuler IsNot Nothing Then topRuler.PointerOffset = canvasPosition.X
            If leftRuler IsNot Nothing Then leftRuler.PointerOffset = canvasPosition.Y
        End Sub

        Private Sub HideRulerMarkers()
            UpdateRulerMarkers(New Avalonia.Point(Double.NaN, Double.NaN))
        End Sub

        Private Sub UpdateGuideLines()
            Dim layer = Me.FindControl(Of Canvas)("GuideLayer")
            If layer Is Nothing Then Return
            layer.Children.Clear()

            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas Is Nothing Then Return
            layer.Width = canvas.Bounds.Width
            layer.Height = canvas.Bounds.Height
            If Not _showRulers OrElse vm Is Nothing OrElse vm.CurrentImage Is Nothing Then Return

            Dim pixelSize = vm.CurrentImage.PixelSize
            Dim imageRect = GetDisplayedImageRect(canvas, vm)
            If pixelSize.Width <= 0 OrElse pixelSize.Height <= 0 Then Return
            If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return

            For Each guideX In _guidesX
                Dim x = Math.Floor(imageRect.Left + guideX / pixelSize.Width * imageRect.Width) + 0.5
                layer.Children.Add(CreateGuideLine(New Avalonia.Point(x, 0), New Avalonia.Point(x, layer.Height)))
            Next
            For Each guideY In _guidesY
                Dim y = Math.Floor(imageRect.Top + guideY / pixelSize.Height * imageRect.Height) + 0.5
                layer.Children.Add(CreateGuideLine(New Avalonia.Point(0, y), New Avalonia.Point(layer.Width, y)))
            Next
        End Sub

        Private Shared Function CreateGuideLine(startPoint As Avalonia.Point, endPoint As Avalonia.Point) As Line
            Return New Line With {
                .StartPoint = startPoint,
                .EndPoint = endPoint,
                .Stroke = GuideBrush,
                .StrokeThickness = 1,
                .IsHitTestVisible = False
            }
        End Function

        ''' Wandelt eine Hilfslinie (Bildpixel) in ihre Canvas-Koordinate um.
        Private Shared Function GuideToCanvas(guide As Double, axisStart As Double, axisLength As Double, imageLength As Double) As Double
            If imageLength <= 0 Then Return axisStart
            Return axisStart + guide / imageLength * axisLength
        End Function

        ''' Index der Hilfslinie unter dem Zeiger, oder -1. Senkrechte Linien haben Vorrang, wenn beide
        ''' gleich nah liegen - sonst ließe sich eine Kreuzung nie in beide Richtungen auflösen.
        Private Function HitTestGuide(position As Avalonia.Point, imageRect As Avalonia.Rect, vm As EditorViewModel,
                                      ByRef isVertical As Boolean) As Integer
            isVertical = False
            If vm Is Nothing OrElse vm.CurrentImage Is Nothing Then Return -1
            If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return -1

            Dim pixelSize = vm.CurrentImage.PixelSize
            Dim bestIndex = -1
            Dim bestDistance = GuideHitTolerance

            For i = 0 To _guidesY.Count - 1
                Dim distance = Math.Abs(position.Y - GuideToCanvas(_guidesY(i), imageRect.Top, imageRect.Height, pixelSize.Height))
                If distance <= bestDistance Then
                    bestDistance = distance
                    bestIndex = i
                End If
            Next
            For i = 0 To _guidesX.Count - 1
                Dim distance = Math.Abs(position.X - GuideToCanvas(_guidesX(i), imageRect.Left, imageRect.Width, pixelSize.Width))
                If distance <= bestDistance Then
                    bestDistance = distance
                    bestIndex = i
                    isVertical = True
                End If
            Next
            Return bestIndex
        End Function

        Private Sub BeginGuideDrag(isVertical As Boolean, index As Integer)
            _guideDragIsVertical = isVertical
            _guideDragIndex = index
            _isGuideDragging = True
        End Sub

        Private Sub UpdateGuideDrag(canvasPosition As Avalonia.Point)
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas Is Nothing OrElse vm Is Nothing OrElse vm.CurrentImage Is Nothing Then Return
            Dim imageRect = GetDisplayedImageRect(canvas, vm)
            If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return

            Dim pixelSize = vm.CurrentImage.PixelSize
            Dim axisStart = If(_guideDragIsVertical, imageRect.Left, imageRect.Top)
            Dim axisLength = If(_guideDragIsVertical, imageRect.Width, imageRect.Height)
            Dim imageLength = CDbl(If(_guideDragIsVertical, pixelSize.Width, pixelSize.Height))
            If imageLength <= 0 Then Return

            Dim pointerOnAxis = If(_guideDragIsVertical, canvasPosition.X, canvasPosition.Y)
            Dim value = Math.Round((pointerOnAxis - axisStart) / axisLength * imageLength)
            value = Math.Max(0, Math.Min(imageLength, value))

            Dim guides = If(_guideDragIsVertical, _guidesX, _guidesY)
            If _guideDragIndex < 0 OrElse _guideDragIndex >= guides.Count Then Return
            guides(_guideDragIndex) = value
            UpdateGuideLines()
        End Sub

        ''' Eine Hilfslinie, die außerhalb des Canvas oder neben dem Bild losgelassen wird, wird
        ''' verworfen - das ist zugleich die Geste zum Löschen (zurück aufs Lineal ziehen) und der
        ''' Grund, warum ein bloßer Klick aufs Lineal noch keine Linie erzeugt.
        Private Sub EndGuideDrag(canvasPosition As Avalonia.Point)
            If Not _isGuideDragging Then Return
            _isGuideDragging = False

            Dim guides = If(_guideDragIsVertical, _guidesX, _guidesY)
            Dim index = _guideDragIndex
            _guideDragIndex = -1
            If index < 0 OrElse index >= guides.Count Then Return

            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas Is Nothing OrElse vm Is Nothing Then Return
            Dim imageRect = GetDisplayedImageRect(canvas, vm)

            Dim pointerOnAxis = If(_guideDragIsVertical, canvasPosition.X, canvasPosition.Y)
            Dim canvasLength = If(_guideDragIsVertical, canvas.Bounds.Width, canvas.Bounds.Height)
            Dim imageStart = If(_guideDragIsVertical, imageRect.Left, imageRect.Top)
            Dim imageEnd = If(_guideDragIsVertical, imageRect.Right, imageRect.Bottom)

            Dim droppedOutside = pointerOnAxis < 0 OrElse pointerOnAxis > canvasLength OrElse
                                 pointerOnAxis < imageStart OrElse pointerOnAxis > imageEnd
            If droppedOutside Then guides.RemoveAt(index)
            UpdateGuideLines()
        End Sub

        Public Sub OnRulerPointerPressed(sender As Object, e As PointerPressedEventArgs)
            If Not _showRulers Then Return
            If Not e.GetCurrentPoint(Nothing).Properties.IsLeftButtonPressed Then Return
            Dim ruler = TryCast(sender, RulerControl)
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If ruler Is Nothing OrElse canvas Is Nothing OrElse vm Is Nothing OrElse vm.CurrentImage Is Nothing Then Return

            ' Das obere Lineal liefert waagerechte Hilfslinien, das linke senkrechte.
            Dim isVertical = Not ruler.IsHorizontal
            Dim guides = If(isVertical, _guidesX, _guidesY)
            guides.Add(0.0)
            BeginGuideDrag(isVertical, guides.Count - 1)
            e.Pointer.Capture(ruler)
            UpdateGuideDrag(e.GetPosition(canvas))
            e.Handled = True
        End Sub

        Public Sub OnRulerPointerMoved(sender As Object, e As PointerEventArgs)
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            If canvas Is Nothing Then Return
            Dim position = e.GetPosition(canvas)
            UpdateRulerMarkers(position)
            If Not _isGuideDragging Then Return
            UpdateGuideDrag(position)
            e.Handled = True
        End Sub

        Public Sub OnRulerPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            If Not _isGuideDragging Then Return
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            If canvas Is Nothing Then Return
            EndGuideDrag(e.GetPosition(canvas))
            e.Pointer.Capture(Nothing)
            e.Handled = True
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

                        ' Die Kontur-Icons bestehen aus dünnen dunklen Linien und verschwinden als Mauszeiger
                        ' über dunklem Bildinhalt. Deshalb liegt darunter eine geweitete weiße Silhouette des
                        ' Icons: erst die Zeichnung um HaloRadius aufdicken, dann komplett weiß einfärben.
                        Const haloRadius As Single = 2.0F
                        Using haloPaint As New SKPaint()
                            haloPaint.ImageFilter = SKImageFilter.CreateColorFilter(
                                SKColorFilter.CreateBlendMode(SKColors.White, SKBlendMode.SrcIn),
                                SKImageFilter.CreateDilate(haloRadius, haloRadius))
                            canvas.SaveLayer(haloPaint)
                            canvas.Translate(offsetX, offsetY)
                            canvas.Scale(scale)
                            canvas.DrawPicture(picture)
                            canvas.Restore()
                        End Using

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

        ''' Pipette: nimmt die Farbe an der Zeigerposition direkt aus dem aktuell angezeigten
        ''' DisplayImage (voll bearbeitete Live-Vorschau inkl. Objekte) - "what you see is what you
        ''' get". Avalonia-Bitmaps erlauben keinen direkten Pixelzugriff, daher über PNG-Bytes nach
        ''' SkiaSharp dekodiert - die Live-Vorschau fragt bei jeder Mausbewegung, deshalb hält
        ''' GetPickSampleBitmap das Ergebnis fest, bis eine andere Bildfassung angezeigt wird.
        Private Function SampleDisplayedColor(vm As EditorViewModel, imageRect As Avalonia.Rect, screenPoint As Avalonia.Point) As Color?
            Dim bitmap = vm?.DisplayImage
            If bitmap Is Nothing OrElse imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return Nothing
            Dim decoded = GetPickSampleBitmap(bitmap)
            If decoded Is Nothing Then Return Nothing

            Dim pos = ClampPointToRect(screenPoint, imageRect)
            Dim xPct = (pos.X - imageRect.Left) / imageRect.Width
            Dim yPct = (pos.Y - imageRect.Top) / imageRect.Height
            Dim px = Math.Max(0, Math.Min(decoded.Width - 1, CInt(xPct * decoded.Width)))
            Dim py = Math.Max(0, Math.Min(decoded.Height - 1, CInt(yPct * decoded.Height)))
            Dim sampled = decoded.GetPixel(px, py)
            Return Color.FromArgb(sampled.Alpha, sampled.Red, sampled.Green, sampled.Blue)
        End Function

        Private Function GetPickSampleBitmap(bitmap As Bitmap) As SKBitmap
            If _pickSampleBitmap IsNot Nothing AndAlso _pickSampleSource Is bitmap Then Return _pickSampleBitmap
            ReleasePickSampleBitmap()
            Try
                Using ms = New MemoryStream()
                    bitmap.Save(ms, PngBitmapEncoderOptions.Default)
                    ms.Seek(0, SeekOrigin.Begin)
                    _pickSampleBitmap = SKBitmap.Decode(ms)
                End Using
            Catch
                _pickSampleBitmap = Nothing
            End Try
            If _pickSampleBitmap IsNot Nothing Then _pickSampleSource = bitmap
            Return _pickSampleBitmap
        End Function

        Private Sub ReleasePickSampleBitmap()
            _pickSampleBitmap?.Dispose()
            _pickSampleBitmap = Nothing
            _pickSampleSource = Nothing
        End Sub

        Private Sub UpdateSelectionOverlayFromDrag()
            Dim overlay = Me.FindControl(Of SelectionOverlayControl)("SelectionDragOverlay")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If overlay Is Nothing Then Return
            Dim left = Math.Min(_selectionStart.X, _selectionEnd.X)
            Dim top = Math.Min(_selectionStart.Y, _selectionEnd.Y)
            Dim width = Math.Abs(_selectionEnd.X - _selectionStart.X)
            Dim height = Math.Abs(_selectionEnd.Y - _selectionStart.Y)
            overlay.ShapeMode = If(vm IsNot Nothing AndAlso vm.SelectionMode = "Ellipse", "Ellipse", "Rectangle")
            overlay.CombineMode = If(vm Is Nothing, "New", vm.SelectionCombineMode)
            overlay.Points = Nothing
            overlay.EdgePoints = Nothing
            overlay.IsVisible = True
            Avalonia.Controls.Canvas.SetLeft(overlay, left)
            Avalonia.Controls.Canvas.SetTop(overlay, top)
            overlay.Width = Math.Max(1, width)
            overlay.Height = Math.Max(1, height)
        End Sub

        ''' Repositioniert das Auswahl-Overlay anhand der im ViewModel gespeicherten Auswahl (Prozent-
        ''' vom-Bild), z.B. nach Zoom/Pan-Änderungen - analog zu PositionCropOverlayFromViewModel.
        Private Sub PositionSelectionOverlayFromViewModel(ix As Double, iy As Double, iw As Double, ih As Double)
            Dim overlay = Me.FindControl(Of SelectionOverlayControl)("SelectionOverlay")
            Dim maskOverlay = Me.FindControl(Of Image)("SelectionMaskOverlay")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing OrElse Not IsSelectionScopeTool(vm.CurrentTool) OrElse Not vm.HasActiveSelection Then
                HideCurrentSelectionOverlay()
                Return
            End If
            Dim left = ix + iw * vm.SelectionXPercent / 100.0
            Dim top = iy + ih * vm.SelectionYPercent / 100.0
            Dim width = Math.Max(1, iw * vm.SelectionWidthPercent / 100.0)
            Dim height = Math.Max(1, ih * vm.SelectionHeightPercent / 100.0)
            Dim maskEdgePoints = BuildOverlayMaskEdgePoints(vm, width, height)
            Dim hasMaskEdgePoints = maskEdgePoints IsNot Nothing AndAlso maskEdgePoints.Count > 0

            If maskOverlay IsNot Nothing Then
                maskOverlay.IsVisible = False
                Avalonia.Controls.Canvas.SetLeft(maskOverlay, left)
                Avalonia.Controls.Canvas.SetTop(maskOverlay, top)
                maskOverlay.Width = width
                maskOverlay.Height = height
            End If

            If overlay IsNot Nothing Then
                overlay.IsVisible = True
                overlay.ShapeMode = vm.SelectionShapeMode
                overlay.CombineMode = "New"
                overlay.EdgePoints = maskEdgePoints
                overlay.Points = If(hasMaskEdgePoints, Nothing, BuildOverlayPoints(vm, ix, iy, iw, ih, left, top))
                Avalonia.Controls.Canvas.SetLeft(overlay, left)
                Avalonia.Controls.Canvas.SetTop(overlay, top)
                overlay.Width = width
                overlay.Height = height
            End If
        End Sub

        ''' <summary>Werkzeuge, in denen das Auswahl-Overlay (nur als Anzeige, nicht interaktiv) sichtbar
        ''' bleiben soll: das Auswahl-Werkzeug selbst UND die pixel-anpassenden Werkzeuge, deren Regler jetzt
        ''' nur innerhalb der Auswahl wirken - so sieht der Nutzer, warum sich nur ein Teil ändert. In
        ''' Geometrie-/Ebenen-Werkzeugen dagegen ausgeblendet (dort wird die Auswahl ohnehin verworfen).</summary>
        Private Shared Function IsSelectionScopeTool(tool As EditorTool) As Boolean
            Select Case tool
                Case EditorTool.Selection, EditorTool.Adjust, EditorTool.Color, EditorTool.Filters, EditorTool.Effects
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        Private Sub UpdateSelectionOverlayVisibility()
            Dim overlay = Me.FindControl(Of SelectionOverlayControl)("SelectionOverlay")
            Dim maskOverlay = Me.FindControl(Of Image)("SelectionMaskOverlay")
            Dim dragOverlay = Me.FindControl(Of SelectionOverlayControl)("SelectionDragOverlay")
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim showSelection = vm IsNot Nothing AndAlso IsSelectionScopeTool(vm.CurrentTool) AndAlso vm.HasActiveSelection
            If showSelection Then
                If maskOverlay IsNot Nothing Then maskOverlay.IsVisible = False
                If overlay IsNot Nothing Then overlay.IsVisible = True
            Else
                HideCurrentSelectionOverlay()
            End If
            If dragOverlay IsNot Nothing AndAlso Not _isSelectionDragging AndAlso Not _isLassoDrawing Then dragOverlay.IsVisible = False
            UpdateSliderLayout()
        End Sub

        Private Sub SetCurrentSelectionOverlayVisible(isVisible As Boolean)
            Dim overlay = Me.FindControl(Of SelectionOverlayControl)("SelectionOverlay")
            Dim maskOverlay = Me.FindControl(Of Image)("SelectionMaskOverlay")
            If Not isVisible Then
                HideCurrentSelectionOverlay()
                Return
            End If
            If overlay IsNot Nothing Then overlay.IsVisible = True
            If maskOverlay IsNot Nothing Then maskOverlay.IsVisible = False
        End Sub

        Private Sub HideCurrentSelectionOverlay()
            Dim overlay = Me.FindControl(Of SelectionOverlayControl)("SelectionOverlay")
            Dim maskOverlay = Me.FindControl(Of Image)("SelectionMaskOverlay")
            If maskOverlay IsNot Nothing Then
                maskOverlay.IsVisible = False
            End If
            If overlay Is Nothing Then Return
            overlay.IsVisible = False
            overlay.Points = Nothing
            overlay.EdgePoints = Nothing
            overlay.CombineMode = "New"
            overlay.Width = 0
            overlay.Height = 0
        End Sub

        ''' <summary>Bricht ein laufendes Aufziehen/Lasso/Verschieben ab (Esc), ohne es zu übernehmen: die
        ''' Kandidatenform wird verworfen und die bestehende Auswahl steht wieder da, wie sie war. Ein
        ''' Verschieben ist zu diesem Zeitpunkt schon im ViewModel angekommen - dafür ist Rückgängig da.</summary>
        Private Sub CancelSelectionDrag()
            _isSelectionDragging = False
            _isLassoDrawing = False
            _isSelectionMoveDragging = False
            _selectionDragReplacesExisting = False
            _lassoPoints.Clear()
            HideSelectionDragOverlay()
            UpdateSelectionOverlayVisibility()
        End Sub

        Private Sub CommitSelectionDrag()
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas Is Nothing OrElse vm Is Nothing Then
                HideSelectionDragOverlay()
                Return
            End If
            Dim rect = GetDisplayedImageRect(canvas, vm)
            If rect.Width <= 0 OrElse rect.Height <= 0 Then
                HideSelectionDragOverlay()
                Return
            End If
            Dim leftPx = Math.Min(_selectionStart.X, _selectionEnd.X)
            Dim topPx = Math.Min(_selectionStart.Y, _selectionEnd.Y)
            Dim rightPx = Math.Max(_selectionStart.X, _selectionEnd.X)
            Dim bottomPx = Math.Max(_selectionStart.Y, _selectionEnd.Y)
            If Math.Abs(rightPx - leftPx) < 8 OrElse Math.Abs(bottomPx - topPx) < 8 Then
                HideSelectionDragOverlay()
                Return
            End If

            Dim xPercent = (leftPx - rect.Left) / rect.Width * 100.0
            Dim yPercent = (topPx - rect.Top) / rect.Height * 100.0
            Dim widthPercent = (rightPx - leftPx) / rect.Width * 100.0
            Dim heightPercent = (bottomPx - topPx) / rect.Height * 100.0
            If vm.SelectionMode = "Ellipse" Then
                vm.SetSelectionEllipse(xPercent, yPercent, widthPercent, heightPercent)
            Else
                vm.SetSelectionRect(xPercent, yPercent, widthPercent, heightPercent)
            End If
            _selectionDragReplacesExisting = False
            HideSelectionDragOverlay()
        End Sub

        ' Zeigt während des Lasso-Zeichnens den tatsächlichen Freihand-Pfad statt nur dessen Bounding-Box.
        Private Sub UpdateLassoOverlayFromPoints()
            Dim overlay = Me.FindControl(Of SelectionOverlayControl)("SelectionDragOverlay")
            If overlay Is Nothing OrElse _lassoPoints.Count = 0 Then Return
            Dim minX = Double.MaxValue, minY = Double.MaxValue, maxX = Double.MinValue, maxY = Double.MinValue
            For Each p In _lassoPoints
                minX = Math.Min(minX, p.X) : minY = Math.Min(minY, p.Y)
                maxX = Math.Max(maxX, p.X) : maxY = Math.Max(maxY, p.Y)
            Next
            overlay.ShapeMode = "Lasso"
            overlay.CombineMode = If(TryCast(DataContext, EditorViewModel)?.SelectionCombineMode, "New")
            overlay.EdgePoints = Nothing
            overlay.Points = _lassoPoints.Select(Function(p) New Avalonia.Point(p.X - minX, p.Y - minY)).ToList()
            overlay.IsVisible = True
            Avalonia.Controls.Canvas.SetLeft(overlay, minX)
            Avalonia.Controls.Canvas.SetTop(overlay, minY)
            overlay.Width = Math.Max(1, maxX - minX)
            overlay.Height = Math.Max(1, maxY - minY)
        End Sub

        Private Sub HideSelectionDragOverlay()
            Dim overlay = Me.FindControl(Of SelectionOverlayControl)("SelectionDragOverlay")
            If overlay Is Nothing Then Return
            overlay.IsVisible = False
            overlay.Points = Nothing
            overlay.EdgePoints = Nothing
            overlay.CombineMode = "New"
        End Sub

        Private Shared Function BuildOverlayPoints(vm As EditorViewModel,
                                                   imageLeft As Double,
                                                   imageTop As Double,
                                                   imageWidth As Double,
                                                   imageHeight As Double,
                                                   overlayLeft As Double,
                                                   overlayTop As Double) As IList(Of Avalonia.Point)
            Dim xs = vm.SelectionShapePointsX
            Dim ys = vm.SelectionShapePointsY
            If xs Is Nothing OrElse ys Is Nothing OrElse xs.Length < 3 OrElse xs.Length <> ys.Length Then Return Nothing

            Dim result As New List(Of Avalonia.Point)(xs.Length)
            For i = 0 To xs.Length - 1
                result.Add(New Avalonia.Point(
                    imageLeft + imageWidth * xs(i) / 100.0 - overlayLeft,
                    imageTop + imageHeight * ys(i) / 100.0 - overlayTop))
            Next
            Return result
        End Function

        Private Shared Function BuildOverlayMaskEdgePoints(vm As EditorViewModel,
                                                           overlayWidth As Double,
                                                           overlayHeight As Double) As IList(Of Avalonia.Point)
            Dim xs = vm.SelectionMaskEdgePointsX
            Dim ys = vm.SelectionMaskEdgePointsY
            If xs Is Nothing OrElse ys Is Nothing OrElse xs.Length = 0 OrElse xs.Length <> ys.Length Then Return Nothing

            Dim result As New List(Of Avalonia.Point)(xs.Length)
            For i = 0 To xs.Length - 1
                result.Add(New Avalonia.Point(overlayWidth * xs(i) / 100.0,
                                              overlayHeight * ys(i) / 100.0))
            Next
            Return result
        End Function

        Private Shared Function IsPointInsideSelection(point As Avalonia.Point, imageRect As Avalonia.Rect, vm As EditorViewModel) As Boolean
            Dim left = imageRect.Left + imageRect.Width * vm.SelectionXPercent / 100.0
            Dim top = imageRect.Top + imageRect.Height * vm.SelectionYPercent / 100.0
            Dim width = imageRect.Width * vm.SelectionWidthPercent / 100.0
            Dim height = imageRect.Height * vm.SelectionHeightPercent / 100.0
            If width <= 0 OrElse height <= 0 Then Return False
            Dim bounds = New Avalonia.Rect(left, top, width, height)
            If Not bounds.Contains(point) Then Return False

            If vm.HasSelectionMask Then
                Dim xPercent = (point.X - imageRect.Left) / imageRect.Width * 100.0
                Dim yPercent = (point.Y - imageRect.Top) / imageRect.Height * 100.0
                Return vm.IsPointInsideSelectionPercent(xPercent, yPercent)
            End If

            Select Case vm.SelectionShapeMode
                Case "Ellipse"
                    Dim cx = left + width / 2.0
                    Dim cy = top + height / 2.0
                    Dim rx = width / 2.0
                    Dim ry = height / 2.0
                    If rx <= 0 OrElse ry <= 0 Then Return False
                    Dim nx = (point.X - cx) / rx
                    Dim ny = (point.Y - cy) / ry
                    Return nx * nx + ny * ny <= 1.0
                Case "Lasso", "MagicWand"
                    Dim xs = vm.SelectionShapePointsX
                    Dim ys = vm.SelectionShapePointsY
                    If xs Is Nothing OrElse ys Is Nothing OrElse xs.Length < 3 OrElse xs.Length <> ys.Length Then Return False
                    Dim polygon As New List(Of Avalonia.Point)(xs.Length)
                    For i = 0 To xs.Length - 1
                        polygon.Add(New Avalonia.Point(imageRect.Left + imageRect.Width * xs(i) / 100.0,
                                                       imageRect.Top + imageRect.Height * ys(i) / 100.0))
                    Next
                    Return IsPointInPolygon(point, polygon)
                Case Else
                    Return True
            End Select
        End Function

        Private Shared Function IsPointInPolygon(point As Avalonia.Point, polygon As IList(Of Avalonia.Point)) As Boolean
            Dim inside = False
            Dim j = polygon.Count - 1
            For i = 0 To polygon.Count - 1
                Dim pi = polygon(i)
                Dim pj = polygon(j)
                If ((pi.Y > point.Y) <> (pj.Y > point.Y)) AndAlso
                   (point.X < (pj.X - pi.X) * (point.Y - pi.Y) / Math.Max(0.000001, pj.Y - pi.Y) + pi.X) Then
                    inside = Not inside
                End If
                j = i
            Next
            Return inside
        End Function

        Private Sub CommitLassoSelection()
            Dim canvas = Me.FindControl(Of Canvas)("PreviewCanvas")
            Dim vm = TryCast(DataContext, EditorViewModel)
            If canvas Is Nothing OrElse vm Is Nothing OrElse _lassoPoints.Count < 3 Then
                HideSelectionDragOverlay()
                Return
            End If
            Dim rect = GetDisplayedImageRect(canvas, vm)
            If rect.Width <= 0 OrElse rect.Height <= 0 Then
                HideSelectionDragOverlay()
                Return
            End If
            Dim xs(_lassoPoints.Count - 1) As Double
            Dim ys(_lassoPoints.Count - 1) As Double
            For i = 0 To _lassoPoints.Count - 1
                xs(i) = (_lassoPoints(i).X - rect.Left) / rect.Width * 100.0
                ys(i) = (_lassoPoints(i).Y - rect.Top) / rect.Height * 100.0
            Next
            vm.SetSelectionLasso(xs, ys)
            _selectionDragReplacesExisting = False
            HideSelectionDragOverlay()
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

            ' Bezugsgröße ist das angezeigte, also bereits beschnittene Bild - nicht das Original,
            ' sonst zeigte die Plakette nach einem Beschnitt zu große Maße an.
            Dim width = CInt(Math.Round(Math.Max(1, cropRect.Width / imageRect.Width * vm.EffectiveImageWidthPixels)))
            Dim height = CInt(Math.Round(Math.Max(1, cropRect.Height / imageRect.Height * vm.EffectiveImageHeightPixels)))
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
            ' Der Selektionsrahmen folgt bewusst der Objekt-Konturfarbe (Nutzer-Entscheidung 2026-07-16:
            ' "so wie frueher darstellen", nicht Akzentfarbe).
            If frame IsNot Nothing Then frame.Stroke = New SolidColorBrush(ParseAvaloniaColor(vm.AnnotationStrokeColor, Colors.White))
            If overlayImage IsNot Nothing Then
                overlayImage.Margin = ComputeSelectedOverlayImageMargin(vm, width, height)
                ' KEIN IsVisible hier setzen: die Sichtbarkeit gehoert allein dem Binding
                ' (ShowSelectedSvgOverlay = Drag-Ghost nur waehrend Placement-Edit). Ein lokales
                ' False wuerde das Binding uebersteuern und den Ghost bei jeder Mausbewegung
                ' (UpdateSliderLayout) verstecken.
            End If

            Dim selectedKind = If(vm.SelectedAnnotationKind, "")
            Dim isTextLayer = selectedKind.Equals("Text", StringComparison.OrdinalIgnoreCase) OrElse
                              (selectedKind.Equals("Watermark", StringComparison.OrdinalIgnoreCase) AndAlso vm.ShowTextContentControls)
            overlay.Cursor = If(isTextLayer, New Cursor(StandardCursorType.IBeam), New Cursor(StandardCursorType.SizeAll))

            If editor IsNot Nothing Then
                editor.IsVisible = (vm.CurrentTool = EditorTool.Text OrElse vm.CurrentTool = EditorTool.Insert) AndAlso
                                   vm.ShowTextContentControls AndAlso
                                   Not selectedKind.Equals("QR", StringComparison.OrdinalIgnoreCase)

                ' Die Schriftgröße ist in Basisbild-Pixeln gespeichert; das gebackene Bild skaliert sie
                ' uniform auf die Zielauflösung (ImageProcessor.ScaleAnnotationForSource). Die Live-Textbox
                ' muss exakt dieselbe Umrechnung machen - sonst weicht der Text im Editor deutlich von der
                ' gebackenen Größe ab. Die Größe darf insbesondere NICHT aus der Objekt-Box abgeleitet
                ' werden: die Box umschließt den Text zwar eng (EditorViewModel.EstimateTextAnnotationSizePercent),
                ' der Nutzer kann sie aber jederzeit an den Griffen aufziehen - der Schriftgrad bleibt dabei.
                Dim displayScale = ComputeBasePixelToDisplayScale(vm, iw, ih, scale)
                Dim bakedFontSize = Math.Max(8.0, vm.AnnotationFontSize)
                Dim displayFontSize = Math.Max(1.0, bakedFontSize * displayScale)
                editor.FontSize = displayFontSize
                ' Kein LineHeight setzen: DrawWrappedText benutzt inzwischen denselben Zeilenabstand aus
                ' den Schriftmetriken, den Avalonia von sich aus nimmt. Ein gesetztes LineHeight schöbe
                ' die erste Zeile um die zusätzliche Durchschusshöhe nach unten - sichtbar als Sprung von
                ' ein bis zwei Pixeln beim Selektieren und zurück beim Abwählen.
                ' Avalonia setzt Text über HarfBuzz und wendet dabei Kerning und Ligaturen an. Skias
                ' SKCanvas.DrawText im gebackenen Bild tut das nicht, es reiht die Glyphen mit ihren
                ' nackten Vorschubbreiten. Bei Paaren wie "Te" rückt Avalonia die Buchstaben deshalb
                ' enger zusammen als im Ergebnis. Damit der Editor zeigt, was herauskommt, sind beide
                ' Merkmale in der Live-Textbox abgeschaltet - dieselbe Annahme trifft auch die
                ' Breitenschätzung in EditorViewModel.EstimateTextAnnotationSizePercent, die mit
                ' SKPaint.MeasureText misst.
                editor.FontFeatures = New FontFeatureCollection() From {
                    New FontFeature With {.Tag = "kern", .Value = 0},
                    New FontFeature With {.Tag = "liga", .Value = 0}
                }
                editor.FontFamily = New FontFamily(vm.AnnotationFontFamily)
                ' Kontur und Verlaufsfüllung kann die TextBox nicht: solche Objekte zeichnet das
                ' Overlay darunter vollständig, die Textbox bleibt nur noch für Eingabe und Schreibmarke da.
                Dim textColor = ParseAvaloniaColor(vm.AnnotationFillColor, Colors.White)
                editor.Foreground = Brushes.Transparent
                editor.CaretBrush = New SolidColorBrush(textColor)
                ' Skia hängt die erste Zeile an der Grundlinie unter der Oberkante auf, Avalonia an der
                ' Glyphen-Oberkante - ohne diesen Versatz säße der Live-Text über dem gebackenen.
                editor.RenderTransform = New TranslateTransform(0, ImageProcessor.GetBakedTextTopOffset(vm.AnnotationFontFamily, CSng(displayFontSize)))
            End If
        End Sub

        ''' Umrechnungsfaktor von Basisbild-Pixeln (Speichereinheit der Annotationen) in Display-Pixel.
        ''' Uniform (Wurzel aus x*y), genau wie ImageProcessor.ScaleAnnotationForSource beim Backen.
        Private Shared Function ComputeBasePixelToDisplayScale(vm As EditorViewModel, iw As Double, ih As Double, fallbackScale As Double) As Double
            Dim baseImage = vm?.CurrentImage
            If baseImage Is Nothing Then Return fallbackScale
            Dim baseWidth = CDbl(baseImage.PixelSize.Width)
            Dim baseHeight = CDbl(baseImage.PixelSize.Height)
            If baseWidth <= 0 OrElse baseHeight <= 0 OrElse iw <= 0 OrElse ih <= 0 Then Return fallbackScale
            Return Math.Sqrt((iw / baseWidth) * (ih / baseHeight))
        End Function

        ''' Das Overlay-Bitmap ist um die Schatten-/Glow-Ränder größer als das Objekt selbst und wird per
        ''' Stretch="Fill" in die Objekt-Border gezogen. Die negativen Margins schieben genau diese Ränder
        ''' wieder nach außen, sodass der Objekt-Teil des Bitmaps deckungsgleich auf der Border liegt.
        ''' Die Ränder werden dabei aus Bitmap-Pixeln in Display-Pixel umgerechnet - sie hier aus den
        ''' Prozent-Slidern nachzurechnen wäre falsch, weil ImageProcessor sie in der (gedeckelten)
        ''' Bildpixel-Auflösung des Objekts bemisst, nicht in dessen Bildschirmgröße.
        Private Shared Function ComputeSelectedOverlayImageMargin(vm As EditorViewModel, width As Double, height As Double) As Thickness
            ' BEWUSST KEIN ShowSelectedSvgOverlay-Guard mehr: die Margin gehoert IMMER zu den Metrics
            ' des aktuell gesetzten Ghost-Bitmaps - die Sichtbarkeit regelt allein IsVisible (Binding).
            ' Der fruehere Guard war eine Timing-Falle: landete der (asynchrone) Ghost, waehrend die
            ' Property gerade False lieferte, blieb die Margin 0 und die Bitmap-Innenraender
            ' (4 px Basis + Effekt-Pads) wurden mit in die Box gequetscht -> Objekt schrumpfte beim
            ' Selektieren/Ziehen (~2 px ohne Effekte, mit Schatten deutlich; Log-Befund GhostMargin
            ' show=False margin=0 bei bmp 847x587 / obj@54,54).
            If vm Is Nothing OrElse width <= 0 OrElse height <= 0 Then
                Return New Thickness(0)
            End If

            Return ComputeAnnotationOverlayImageMargin(vm.SelectedAnnotationOverlayMetrics, width, height)
        End Function

        Private Shared Function ComputeAnnotationOverlayImageMargin(metrics As ImageProcessor.AnnotationOverlayRender, width As Double, height As Double) As Thickness
            If width <= 0 OrElse height <= 0 Then Return New Thickness(0)
            If metrics Is Nothing OrElse metrics.ObjectWidth <= 0 OrElse metrics.ObjectHeight <= 0 Then
                Return New Thickness(0)
            End If

            Dim scaleX = width / metrics.ObjectWidth
            Dim scaleY = height / metrics.ObjectHeight
            Dim rightPad = metrics.BitmapWidth - metrics.ObjectX - metrics.ObjectWidth
            Dim bottomPad = metrics.BitmapHeight - metrics.ObjectY - metrics.ObjectHeight

            Return New Thickness(-metrics.ObjectX * scaleX,
                                 -metrics.ObjectY * scaleY,
                                 -rightPad * scaleX,
                                 -bottomPad * scaleY)
        End Function

        ''' <summary>Werkzeuge, in denen der Objektrahmen samt Griffen sichtbar ist. Verschieben ist der
        ''' explizite Objekt-Auswahlmodus; das Drehen-Werkzeug gehört ebenfalls dazu, weil seine Knöpfe
        ''' auf das markierte Objekt wirken.</summary>
        Private Shared Function IsLayerPlacementTool(tool As EditorTool) As Boolean
            Return tool = EditorTool.Text OrElse tool = EditorTool.Geometry OrElse tool = EditorTool.Insert OrElse
                   tool = EditorTool.Move OrElse EditorViewModel.IsObjectScopeTool(tool)
        End Function


        Private Shared Function ParseAvaloniaColor(value As String, fallback As Color) As Color
            If String.IsNullOrWhiteSpace(value) Then Return fallback
            Try
                Return Color.Parse(value)
            Catch
                Return fallback
            End Try
        End Function

        ' Feste Anrast-Ziele: Ränder und Mitte. Der Sicherheitsabstand (früher fest 4/96) kommt
        ' seit 17.07. aus den Einstellungen (EditorSnapMarginPercent, 0 = aus) - siehe
        ' GetSnapTargets; eingelesen je Zug-Start in OnTextOverlayPointerPressed.
        Private Shared ReadOnly TextSnapPercents As Double() = {0, 50, 100}
        Private _snapMarginPercent As Integer = 4
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
            Dim pointerPoint = e.GetCurrentPoint(Nothing)
            If pointerPoint.Properties.IsRightButtonPressed Then
                ClearEditorSelections(TryCast(DataContext, EditorViewModel))
                e.Handled = True
                Return
            End If
            If Not pointerPoint.Properties.IsLeftButtonPressed Then Return
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
            _textDragInitialFontSize = vm.AnnotationFontSize
            _isTextDragging = True
            ' Placement-Edit (Ghost-Übergabe Szene->Overlay) NICHT hier starten, sondern erst
            ' bei echter Bewegung in OnTextOverlayPointerMoved: der reine Auswahl-Klick
            ' ("Auswahl + Ziehen in einer Geste") löste sonst bei jedem Maus-Selektieren die
            ' komplette Übergabe aus - das kurze Flackern (Nutzer-Befund 2026-07-17).
            _textDragPlacementStarted = False
            ' Smart Guides: Anrast-Ziele der ANDEREN Objekte einmal beim Zug-Start einsammeln
            ' (stabil und billig; die Objekte bewegen sich während des Zugs nicht). Der
            ' Rand-Sicherheitsabstand kommt je Zug frisch aus den Einstellungen.
            If mode <> TextDragMode.Rotate Then
                _snapMarginPercent = Math.Max(0, Math.Min(20, AppSettingsService.Load().EditorSnapMarginPercent))
                CollectObjectSnapTargets(vm, canvas)
            Else
                _objectSnapTargetsX.Clear()
                _objectSnapTargetsY.Clear()
            End If
            e.Pointer.Capture(overlay)
            If mode = TextDragMode.Rotate Then
                _textRotateCenter = New Avalonia.Point(rect.Left + rect.Width / 2.0, rect.Top + rect.Height / 2.0)
                _textRotateStartAngle = Math.Atan2(pos.Y - _textRotateCenter.Y, pos.X - _textRotateCenter.X) * 180.0 / Math.PI
                _textRotateStartRotation = vm.AnnotationRotation
            ElseIf mode = TextDragMode.Move AndAlso (vm.CurrentTool = EditorTool.Text OrElse vm.CurrentTool = EditorTool.Insert) Then
                FocusTextOverlayEditor()
            End If
            e.Handled = True
        End Sub

        ''' True, sobald der laufende Overlay-Drag den Placement-Edit tatsächlich gestartet hat
        ''' (erst ab ~3 px Bewegung) - ein reiner Auswahl-Klick bleibt dadurch flackerfrei.
        Private _textDragPlacementStarted As Boolean = False

        Private Const RotateHandleDistance As Double = 28
        Private Const RotateHandleHitRadius As Double = 12

        ''' <summary>Dreht einen Punkt um <paramref name="center"/>. Positive Winkel drehen im
        ''' Uhrzeigersinn - dieselbe Richtung wie die RotateTransform des Overlays.</summary>
        Private Shared Function RotatePoint(point As Avalonia.Point, center As Avalonia.Point, degrees As Double) As Avalonia.Point
            If degrees = 0 Then Return point
            Dim rad = degrees * Math.PI / 180.0
            Dim cosR = Math.Cos(rad)
            Dim sinR = Math.Sin(rad)
            Dim dx = point.X - center.X
            Dim dy = point.Y - center.Y
            Return New Avalonia.Point(center.X + dx * cosR - dy * sinR,
                                      center.Y + dx * sinR + dy * cosR)
        End Function

        ''' <summary>
        ''' Ordnet einer Zeigerposition die Anfasser-Zone des Objekts zu. Das Overlay wird per
        ''' RenderTransform um seinen Mittelpunkt gedreht, während Canvas.Left/Top und Width/Height
        ''' ungedreht bleiben - <paramref name="rect"/> ist also das ungedrehte Rechteck. Der Zeiger
        ''' wird deshalb um denselben Winkel zurückgedreht; danach liegen alle Zonen wieder
        ''' achsenparallel und die Prüfung stimmt mit dem überein, was auf dem Bild zu sehen ist.
        ''' </summary>
        Private Function GetTextDragMode(point As Avalonia.Point, rect As Avalonia.Rect, rotationDegrees As Double) As TextDragMode
            If rect.Width < 4 OrElse rect.Height < 4 Then Return TextDragMode.None

            Dim center = rect.Center
            Dim local = RotatePoint(point, center, -rotationDegrees)

            Dim rotateHandle = New Avalonia.Point(center.X, rect.Top - RotateHandleDistance)
            Dim rdx = local.X - rotateHandle.X
            Dim rdy = local.Y - rotateHandle.Y
            If Math.Sqrt(rdx * rdx + rdy * rdy) <= RotateHandleHitRadius Then Return TextDragMode.Rotate

            Const hitSlop As Double = 14
            Dim hitRect = rect.Inflate(hitSlop)
            If Not hitRect.Contains(local) Then Return TextDragMode.None

            Const handleSize As Double = 16
            Dim nearLeft = Math.Abs(local.X - rect.Left) <= handleSize
            Dim nearRight = Math.Abs(local.X - rect.Right) <= handleSize
            Dim nearTop = Math.Abs(local.Y - rect.Top) <= handleSize
            Dim nearBottom = Math.Abs(local.Y - rect.Bottom) <= handleSize

            If nearLeft AndAlso nearTop Then Return TextDragMode.TopLeft
            If nearRight AndAlso nearTop Then Return TextDragMode.TopRight
            If nearLeft AndAlso nearBottom Then Return TextDragMode.BottomLeft
            If nearRight AndAlso nearBottom Then Return TextDragMode.BottomRight
            If nearLeft Then Return TextDragMode.Left
            If nearRight Then Return TextDragMode.Right
            If nearTop Then Return TextDragMode.Top
            If nearBottom Then Return TextDragMode.Bottom
            If Not rect.Contains(local) Then Return TextDragMode.None
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

            If Not _textDragPlacementStarted Then
                ' Erst ab echter Bewegung wird aus dem Klick ein Zug (siehe PointerPressed).
                If Math.Abs(pos.X - _textDragPointerStart.X) < 3 AndAlso
                   Math.Abs(pos.Y - _textDragPointerStart.Y) < 3 Then Return
                _textDragPlacementStarted = True
                vm.BeginSelectedAnnotationPlacementEdit()
                If _textDragMode <> TextDragMode.Rotate Then ShowTextSizeBadge()
            End If

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
                If e.KeyModifiers.HasFlag(KeyModifiers.Alt) Then
                    ' Alt = frei verschieben ohne Einrasten (Hilfslinien aus).
                    HideTextSnapGuides()
                Else
                    left = ApplyTextSnap(left, width, imageRect.Left, imageRect.Width, True)
                    top = ApplyTextSnap(top, height, imageRect.Top, imageRect.Height, False)
                End If
                left = ClampOverlayOriginToReachable(left, width, imageRect.Left, imageRect.Width)
                top = ClampOverlayOriginToReachable(top, height, imageRect.Top, imageRect.Height)
                right = left + width
                bottom = top + height
            Else
                ' Die Kanten des Rechtecks liegen im ungedrehten Raum, die Zeigerbewegung kommt aus dem
                ' Canvas. Ohne Rückdrehung schöbe das Ziehen am rechten Rand eines gekippten Objekts
                ' dessen Kante schräg - siehe GetTextDragMode.
                Dim localDelta = RotatePoint(New Avalonia.Point(dx, dy), New Avalonia.Point(0, 0), -vm.AnnotationRotation)
                Select Case _textDragMode
                    Case TextDragMode.Left, TextDragMode.TopLeft, TextDragMode.BottomLeft
                        left = Math.Min(right - minSize, left + localDelta.X)
                    Case TextDragMode.Right, TextDragMode.TopRight, TextDragMode.BottomRight
                        right = Math.Max(left + minSize, right + localDelta.X)
                End Select
                Select Case _textDragMode
                    Case TextDragMode.Top, TextDragMode.TopLeft, TextDragMode.TopRight
                        top = Math.Min(bottom - minSize, top + localDelta.Y)
                    Case TextDragMode.Bottom, TextDragMode.BottomLeft, TextDragMode.BottomRight
                        bottom = Math.Max(top + minSize, bottom + localDelta.Y)
                End Select

                If e.KeyModifiers.HasFlag(KeyModifiers.Alt) Then
                    ' Alt = frei skalieren ohne Einrasten (wie beim Verschieben).
                    HideTextSnapGuides()
                Else
                    ApplyTextResizeSnap(left, top, right, bottom, imageRect, minSize)
                End If

                Dim isQr = String.Equals(vm.EffectiveAnnotationKind, "QR", StringComparison.OrdinalIgnoreCase)
                ' "Seitenverhältnis beibehalten": pro Objekt schaltbar (Checkbox im Panel) fuer
                ' Bild-Objekte und Wasserzeichen-Bilder - frueher war es fuer Bilder hart verdrahtet
                ' und Wasserzeichen-Bilder fehlten ganz. Shift erzwingt weiterhin, QR bleibt hart 1:1.
                Dim isAspectLockedKind = (String.Equals(vm.EffectiveAnnotationKind, "Image", StringComparison.OrdinalIgnoreCase) OrElse
                                          vm.IsWatermarkImageSource) AndAlso vm.AnnotationLockAspect
                ' Text auf einem KREISPFAD verhaelt sich wie QR: hart 1:1. Der Kreisradius ist
                ' min(Breite, Hoehe) - eine nicht-quadratische Box liesse den Selektionsrahmen weit
                ' um den Text herum stehen (Nutzerbefund 2026-07-20).
                Dim isCircleText = EditorViewModel.IsCircleTextPath(vm.AnnotationTextPathKind)
                Dim keepAspect = (e.KeyModifiers.HasFlag(KeyModifiers.Shift) OrElse
                                  isAspectLockedKind OrElse
                                  isQr OrElse isCircleText) AndAlso
                                 _textDragAspect > 0 AndAlso IsTextCornerMode(_textDragMode)
                If keepAspect Then
                    Dim aspect = If(isQr OrElse isCircleText, 1.0, _textDragAspect)
                    Dim targetHeight = Math.Max(minSize, (right - left) / aspect)
                    Select Case _textDragMode
                        Case TextDragMode.TopLeft, TextDragMode.TopRight
                            top = bottom - targetHeight
                        Case TextDragMode.BottomLeft, TextDragMode.BottomRight
                            bottom = top + targetHeight
                    End Select
                End If
                If isAspectLockedKind AndAlso _textDragAspect > 0 AndAlso Not IsTextCornerMode(_textDragMode) Then
                    ' Auch Kanten-Anfasser skalieren bei aktivem Lock proportional (zentriert auf
                    ' der Gegenachse) - sonst bliebe das Verzerren durch die Hintertuer moeglich.
                    Select Case _textDragMode
                        Case TextDragMode.Left, TextDragMode.Right
                            Dim targetHeight = Math.Max(minSize, (right - left) / _textDragAspect)
                            Dim centerY = (top + bottom) / 2.0
                            top = centerY - targetHeight / 2.0
                            bottom = centerY + targetHeight / 2.0
                        Case TextDragMode.Top, TextDragMode.Bottom
                            Dim targetWidth = Math.Max(minSize, (bottom - top) * _textDragAspect)
                            Dim centerX = (left + right) / 2.0
                            left = centerX - targetWidth / 2.0
                            right = centerX + targetWidth / 2.0
                    End Select
                End If
                If isQr AndAlso Not IsTextCornerMode(_textDragMode) Then
                    Select Case _textDragMode
                        Case TextDragMode.Left, TextDragMode.Right
                            Dim targetHeight = Math.Max(minSize, right - left)
                            Dim centerY = (top + bottom) / 2.0
                            top = centerY - targetHeight / 2.0
                            bottom = centerY + targetHeight / 2.0
                        Case TextDragMode.Top, TextDragMode.Bottom
                            Dim targetWidth = Math.Max(minSize, bottom - top)
                            Dim centerX = (left + right) / 2.0
                            left = centerX - targetWidth / 2.0
                            right = centerX + targetWidth / 2.0
                    End Select
                End If
                ' Gedreht wird um den Mittelpunkt des Rechtecks. Wächst es an einer Kante, wandert der
                ' Mittelpunkt - und mit ihm erscheint die gegenüberliegende, im Rechteck unveränderte
                ' Kante an einer neuen Stelle auf dem Bild. Das Rechteck wird deshalb um genau den
                ' Betrag nachgeschoben, den diese Mittelpunktsverschiebung durch die Drehung erzeugt:
                ' t = (C_alt - C_neu) - Rot(C_alt - C_neu). Bei Rotation 0 ist t null.
                If vm.AnnotationRotation <> 0 Then
                    Dim oldCenter = _textDragInitialRect.Center
                    Dim shift = New Avalonia.Point(oldCenter.X - (left + right) / 2.0,
                                                   oldCenter.Y - (top + bottom) / 2.0)
                    Dim rotatedShift = RotatePoint(shift, New Avalonia.Point(0, 0), vm.AnnotationRotation)
                    left += shift.X - rotatedShift.X
                    right += shift.X - rotatedShift.X
                    top += shift.Y - rotatedShift.Y
                    bottom += shift.Y - rotatedShift.Y
                End If
            End If

            Dim finalRect = ClampOverlayRectToReachable(New Avalonia.Rect(Math.Min(left, right), Math.Min(top, bottom), Math.Abs(right - left), Math.Abs(bottom - top)), imageRect)
            Avalonia.Controls.Canvas.SetLeft(overlay, finalRect.Left)
            Avalonia.Controls.Canvas.SetTop(overlay, finalRect.Top)
            overlay.Width = finalRect.Width
            overlay.Height = finalRect.Height
            UpdateTextPixels(finalRect, imageRect, vm)
            UpdateTextSizeBadge(finalRect, imageRect, vm)
            e.Handled = True
        End Sub

        ''' Prüft, ob left/left+width/2/left+width nahe an einem Snap-Ziel (Hilfslinie, Bildkante, Mitte,
        ''' Sicherheitsabstand) liegt und rastet ggf. ein.
        Private Function ApplyTextSnap(value As Double, size As Double, axisStart As Double, axisLength As Double, isVerticalLine As Boolean) As Double
            Const tolerance As Double = 7.0
            For Each target In GetSnapTargets(axisStart, axisLength, isVerticalLine)
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

        ''' Beim Skalieren ueber Objekt-Anfasser sollen dieselben Smart Guides sichtbar sein wie
        ''' beim Verschieben: die gezogene Kante und die neue Mitte koennen an Kanten/Mitten anderer
        ''' Objekte, Hilfslinien sowie Bildraster-Zielen einrasten. Die gegenueberliegende Kante
        ''' bleibt dabei der Anker des jeweiligen Anfassers.
        Private Sub ApplyTextResizeSnap(ByRef left As Double, ByRef top As Double,
                                        ByRef right As Double, ByRef bottom As Double,
                                        imageRect As Avalonia.Rect, minSize As Double)
            Dim snappedX = False
            Dim snappedY = False

            Select Case _textDragMode
                Case TextDragMode.Left, TextDragMode.TopLeft, TextDragMode.BottomLeft
                    snappedX = ApplyTextResizeSnapAxis(left, right, True, imageRect.Left, imageRect.Width, True, minSize)
                Case TextDragMode.Right, TextDragMode.TopRight, TextDragMode.BottomRight
                    snappedX = ApplyTextResizeSnapAxis(right, left, False, imageRect.Left, imageRect.Width, True, minSize)
            End Select

            Select Case _textDragMode
                Case TextDragMode.Top, TextDragMode.TopLeft, TextDragMode.TopRight
                    snappedY = ApplyTextResizeSnapAxis(top, bottom, True, imageRect.Top, imageRect.Height, False, minSize)
                Case TextDragMode.Bottom, TextDragMode.BottomLeft, TextDragMode.BottomRight
                    snappedY = ApplyTextResizeSnapAxis(bottom, top, False, imageRect.Top, imageRect.Height, False, minSize)
            End Select

            If Not snappedX Then HideTextSnapGuide(True)
            If Not snappedY Then HideTextSnapGuide(False)
        End Sub

        Private Function ApplyTextResizeSnapAxis(ByRef draggedEdge As Double,
                                                 fixedEdge As Double,
                                                 draggedIsStart As Boolean,
                                                 axisStart As Double,
                                                 axisLength As Double,
                                                 isVerticalLine As Boolean,
                                                 minSize As Double) As Boolean
            Const tolerance As Double = 7.0
            For Each target In GetSnapTargets(axisStart, axisLength, isVerticalLine)
                If Math.Abs(draggedEdge - target) <= tolerance Then
                    Dim candidate = target
                    If IsValidResizeEdge(candidate, fixedEdge, draggedIsStart, minSize) Then
                        draggedEdge = candidate
                        ShowTextSnapGuide(target, isVerticalLine)
                        Return True
                    End If
                End If

                Dim center = (draggedEdge + fixedEdge) / 2.0
                If Math.Abs(center - target) <= tolerance Then
                    Dim candidate = target * 2.0 - fixedEdge
                    If IsValidResizeEdge(candidate, fixedEdge, draggedIsStart, minSize) Then
                        draggedEdge = candidate
                        ShowTextSnapGuide(target, isVerticalLine)
                        Return True
                    End If
                End If
            Next
            Return False
        End Function

        Private Shared Function IsValidResizeEdge(candidate As Double,
                                                  fixedEdge As Double,
                                                  draggedIsStart As Boolean,
                                                  minSize As Double) As Boolean
            If draggedIsStart Then Return candidate <= fixedEdge - minSize
            Return candidate >= fixedEdge + minSize
        End Function

        ''' Canvas-Koordinaten, an denen ein Objekt einrastet. Reihenfolge = Priorität: die vom
        ''' Nutzer gesetzten Hilfslinien zuerst, dann die Kanten/Mitten der ANDEREN Objekte
        ''' (Smart Guides - Objekte passgenau aneinander ausrichten), zuletzt die festen
        ''' Prozentziele des Bildes (Ränder/Mitte).
        Private Iterator Function GetSnapTargets(axisStart As Double, axisLength As Double, isVerticalLine As Boolean) As IEnumerable(Of Double)
            If _showRulers Then
                Dim vm = TryCast(DataContext, EditorViewModel)
                If vm IsNot Nothing AndAlso vm.CurrentImage IsNot Nothing Then
                    Dim guides = If(isVerticalLine, _guidesX, _guidesY)
                    Dim imageLength = CDbl(If(isVerticalLine, vm.CurrentImage.PixelSize.Width, vm.CurrentImage.PixelSize.Height))
                    If imageLength > 0 Then
                        For Each guide In guides
                            Yield GuideToCanvas(guide, axisStart, axisLength, imageLength)
                        Next
                    End If
                End If
            End If
            For Each target In If(isVerticalLine, _objectSnapTargetsX, _objectSnapTargetsY)
                Yield target
            Next
            For Each pct In TextSnapPercents
                Yield axisStart + axisLength * pct / 100.0
            Next
            ' Sicherheitsabstand zu den Rändern (die pinken Einrast-Linien nahe der Kante) -
            ' einstellbar und abschaltbar (Nutzerwunsch 2026-07-17, präzisiert: DIESE Linien
            ' waren gemeint, nicht das Zuschneiden-Werkzeug).
            If _snapMarginPercent > 0 Then
                Yield axisStart + axisLength * _snapMarginPercent / 100.0
                Yield axisStart + axisLength * (100 - _snapMarginPercent) / 100.0
            End If
        End Function

        ''' Anrast-Ziele aus den ANDEREN sichtbaren Objekten (linke Kante, Mitte, rechte Kante bzw.
        ''' oben/Mitte/unten), einmal beim Zug-Start in Canvas-Koordinaten eingesammelt.
        Private ReadOnly _objectSnapTargetsX As New List(Of Double)()
        Private ReadOnly _objectSnapTargetsY As New List(Of Double)()

        Private Sub CollectObjectSnapTargets(vm As EditorViewModel, canvas As Canvas)
            _objectSnapTargetsX.Clear()
            _objectSnapTargetsY.Clear()
            If vm Is Nothing OrElse canvas Is Nothing Then Return
            Dim imageRect = GetDisplayedImageRect(canvas, vm)
            If imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return
            For Each rectPct In vm.GetAnnotationSnapRectsPercent()
                Dim left = imageRect.Left + imageRect.Width * rectPct.X / 100.0
                Dim top = imageRect.Top + imageRect.Height * rectPct.Y / 100.0
                Dim width = imageRect.Width * rectPct.Width / 100.0
                Dim height = imageRect.Height * rectPct.Height / 100.0
                _objectSnapTargetsX.Add(left)
                _objectSnapTargetsX.Add(left + width / 2.0)
                _objectSnapTargetsX.Add(left + width)
                _objectSnapTargetsY.Add(top)
                _objectSnapTargetsY.Add(top + height / 2.0)
                _objectSnapTargetsY.Add(top + height)
            Next
        End Sub

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
            Dim vm = TryCast(DataContext, EditorViewModel)
            _isTextDragging = False
            _textDragMode = TextDragMode.None
            HideTextSizeBadge()
            HideTextSnapGuides()
            ' Ohne echte Bewegung war es nur ein Auswahl-Klick: es gab keinen Placement-Edit,
            ' also auch nichts zu beenden oder in die Szene zu backen (kein Flackern).
            If _textDragPlacementStarted Then
                _textDragPlacementStarted = False
                vm?.EndSelectedAnnotationPlacementEdit()
                vm?.CommitSelectedAnnotationPlacementEdit()
            End If
            e.Pointer.Capture(Nothing)
            e.Handled = True
        End Sub

        ''' Capture-Verlust während eines Objekt-Zugs (Fokuswechsel, Popup, Fenster verliert die
        ''' Maus): das Release-Event kommt dann NIE an - ohne dieses Aufräumen blieb u. a. die
        ''' pinke Einrast-Hilfslinie dauerhaft im Bild stehen (Nutzer-Screenshot 17.07.).
        ''' Gleiche Abwicklung wie OnTextOverlayPointerReleased.
        Public Sub OnTextOverlayCaptureLost(sender As Object, e As PointerCaptureLostEventArgs)
            If Not _isTextDragging Then Return
            Dim vm = TryCast(DataContext, EditorViewModel)
            _isTextDragging = False
            _textDragMode = TextDragMode.None
            HideTextSizeBadge()
            HideTextSnapGuides()
            If _textDragPlacementStarted Then
                _textDragPlacementStarted = False
                vm?.EndSelectedAnnotationPlacementEdit()
                vm?.CommitSelectedAnnotationPlacementEdit()
            End If
        End Sub

        Private Function GetTextOverlayRect() As Avalonia.Rect
            Dim overlay = Me.FindControl(Of Border)("TextOverlay")
            If overlay Is Nothing OrElse Not overlay.IsVisible Then Return New Avalonia.Rect()
            Dim left = Avalonia.Controls.Canvas.GetLeft(overlay)
            Dim top = Avalonia.Controls.Canvas.GetTop(overlay)
            If Double.IsNaN(left) Then left = 0
            If Double.IsNaN(top) Then top = 0
            ' Width/Height-PROPERTIES lesen, nicht Bounds: PositionTextOverlayFromViewModel setzt
            ' die Properties synchron, Bounds folgt erst im nächsten Layout-Pass. Beim
            ' "Auswahl + Ziehen in EINER Geste" startete der Drag sonst mit der Größe der
            ' VORHERIGEN Selektion und schrieb sie beim ersten Move ins neue Objekt
            ' (Nutzer-Befund 2026-07-17: Bild sprang auf QR-Code-Größe).
            Dim width = If(Double.IsNaN(overlay.Width), overlay.Bounds.Width, overlay.Width)
            Dim height = If(Double.IsNaN(overlay.Height), overlay.Bounds.Height, overlay.Height)
            Return New Avalonia.Rect(left, top, Math.Max(0, width), Math.Max(0, height))
        End Function

        Private Sub UpdateTextPixels(textRect As Avalonia.Rect, imageRect As Avalonia.Rect, vm As EditorViewModel)
            If vm Is Nothing OrElse vm.CurrentImage Is Nothing Then Return
            Dim baseWidth = vm.CurrentImage.PixelSize.Width
            Dim baseHeight = vm.CurrentImage.PixelSize.Height
            If baseWidth <= 0 OrElse baseHeight <= 0 OrElse imageRect.Width <= 0 OrElse imageRect.Height <= 0 Then Return

            ' Beim Textobjekt zuerst die Schrift skalieren: das Rechteck ist bei ihm kein freier Rahmen,
            ' sondern der gemessene Textkasten - das ViewModel setzt es aus der Schrift neu. Beim
            ' Verschieben bleibt die Schrift unangetastet (siehe ScaleSelectedTextFontFromDrag).
            If IsSelectedAnnotationTextLayer(vm) Then ScaleSelectedTextFontFromDrag(textRect, vm)

            vm.SetSelectedAnnotationRectPixels(
                (textRect.Left - imageRect.Left) / imageRect.Width * baseWidth,
                (textRect.Top - imageRect.Top) / imageRect.Height * baseHeight,
                textRect.Width / imageRect.Width * baseWidth,
                textRect.Height / imageRect.Height * baseHeight)
        End Sub

        ''' <summary>Skaliert den Schriftgrad eines Textobjekts aus dem gezogenen Griff - gleichmäßig aus
        ''' dem Wert bei Zieh-Beginn und dem Verhältnis zum Rechteck bei Zieh-Beginn.
        ''' Beim VERSCHIEBEN passiert nichts: früher wurde die Schriftgröße bei jedem Frame aus dem
        ''' Rechteck geschätzt (Höhe · 0,68 bzw. Breite / Zeichenzahl), auch wenn sich das Rechteck gar
        ''' nicht änderte. Solange die Box deutlich größer war als der Text, fiel das kaum auf; seit sie
        ''' den Text eng umschließt, liefert die Schätzung einen kleineren Wert als den echten Schriftgrad
        ''' - und der Text wurde bei jedem Verschieben ein Stück kleiner.</summary>
        Private Sub ScaleSelectedTextFontFromDrag(textRect As Avalonia.Rect, vm As EditorViewModel)
            If _textDragMode = TextDragMode.Move OrElse _textDragMode = TextDragMode.Rotate OrElse _textDragMode = TextDragMode.None Then Return
            If _textDragInitialFontSize <= 0 Then Return
            If _textDragInitialRect.Width <= 0 OrElse _textDragInitialRect.Height <= 0 Then Return

            Dim widthScale = textRect.Width / _textDragInitialRect.Width
            Dim heightScale = textRect.Height / _textDragInitialRect.Height
            Dim scale As Double
            Select Case _textDragMode
                Case TextDragMode.Left, TextDragMode.Right
                    scale = widthScale
                Case TextDragMode.Top, TextDragMode.Bottom
                    scale = heightScale
                Case Else
                    ' Eckgriff ohne Seitenverhältnis-Zwang: die Schrift soll in das gezogene Rechteck
                    ' passen, nicht darüber hinausschießen - also die kleinere der beiden Skalen.
                    scale = Math.Min(widthScale, heightScale)
            End Select
            If Double.IsNaN(scale) OrElse Double.IsInfinity(scale) OrElse scale <= 0 Then Return

            vm.AnnotationFontSizePixels = CInt(Math.Round(Math.Max(8.0, _textDragInitialFontSize * scale)))
        End Sub

        ''' True, wenn der Zeiger auf dem Griff oder der Trennlinie des Vergleichsreglers steht. Geprüft
        ''' wird der Vorfahrenpfad, weil der Druck auf dem Pfeil-Path im Griff ankommt, nicht auf dem Border.
        Private Shared Function IsWithinComparisonSlider(source As Object) As Boolean
            Dim current = TryCast(source, Control)
            While current IsNot Nothing
                If String.Equals(current.Name, "SliderHandleCircle", StringComparison.Ordinal) OrElse
                   String.Equals(current.Name, "SliderDivider", StringComparison.Ordinal) OrElse
                   String.Equals(current.Name, "SliderDividerHit", StringComparison.Ordinal) Then
                    Return True
                End If
                current = TryCast(current.Parent, Control)
            End While
            Return False
        End Function

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

        Private Function IsEditorInputControlFocused(source As Object) As Boolean
            Return IsEditorInputControl(TryCast(source, Control)) OrElse
                   IsEditorInputControl(TryCast(TopLevel.GetTopLevel(Me)?.FocusManager?.GetFocusedElement(), Control))
        End Function

        Private Shared Function IsEditorInputControl(control As Control) As Boolean
            Dim current = control
            While current IsNot Nothing
                If TypeOf current Is TextBox OrElse
                   TypeOf current Is ComboBox OrElse
                   TypeOf current Is NumericUpDown OrElse
                   TypeOf current Is Slider Then
                    Return True
                End If
                current = TryCast(current.Parent, Control)
            End While
            Return False
        End Function

        Public Sub OnFullscreenClick(sender As Object, e As RoutedEventArgs)
            Dim mainVm = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
            mainVm?.EnterFullscreen()
            e.Handled = True
        End Sub

        Private Async Sub CopySelectionToSystemClipboardAsync(vm As EditorViewModel)
            If vm Is Nothing Then Return
            Dim tempPath = vm.CopySelectionToClipboardFile()
            If String.IsNullOrWhiteSpace(tempPath) Then Return
            Try
                Dim owner = TopLevel.GetTopLevel(Me)
                Await ClipboardPathService.CopyPathsAsync(owner?.Clipboard, owner?.StorageProvider, {tempPath}, cut:=False)
            Catch
            End Try
        End Sub

        Public Shadows Sub OnKeyDown(sender As Object, e As KeyEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            Dim isTextInputFocused = TypeOf e.Source Is TextBox
            Dim isInputControlFocused = IsEditorInputControlFocused(e.Source)

            If e.KeyModifiers.HasFlag(KeyModifiers.Control) Then
                Select Case e.Key
                    Case Key.Z
                        vm.UndoCommand.Execute(Nothing)
                        e.Handled = True
                    Case Key.Y
                        vm.RedoCommand.Execute(Nothing)
                        e.Handled = True
                    Case Key.N
                        vm.ShowNewDocumentDialogCommand.Execute(Nothing)
                        e.Handled = True
                    Case Key.S
                        ' Ist Speichern gesperrt (RAW-Datei, oder Immich-Bild ohne "Vorhandene Assets
                        ' aktualisieren"), soll Strg+S nicht wirkungslos verpuffen, sondern das anbieten,
                        ' was hier möglich ist: Speichern unter.
                        If vm.CanSaveInPlace Then
                            vm.SaveCommand.Execute(Nothing)
                        Else
                            vm.SaveAsCommand.Execute(Nothing)
                        End If
                        e.Handled = True
                    Case Key.A
                        ' Strg+A wählt das ganze Bild aus - aber nur dort, wo eine Auswahl überhaupt etwas
                        ' bewirkt (Auswahl-Werkzeug und die Werkzeuge, deren Regler auf die Auswahl wirken).
                        ' In einem Textfeld bleibt es das gewohnte „alles markieren".
                        If Not isTextInputFocused AndAlso IsSelectionScopeTool(vm.CurrentTool) Then
                            vm.SelectAll()
                            e.Handled = True
                        End If
                    Case Key.D
                        ' Belegt bleibt „Objekt duplizieren", solange eines markiert ist; sonst hebt Strg+D
                        ' die Auswahl auf (wie in den üblichen Bildbearbeitungen).
                        If vm.HasSelectedAnnotation Then
                            vm.DuplicateSelectedAnnotationCommand.Execute(Nothing)
                            e.Handled = True
                        ElseIf Not isTextInputFocused AndAlso vm.HasActiveSelection Then
                            vm.ClearSelection()
                            e.Handled = True
                        End If
                    Case Key.P
                        ' Strg+P ist seit 2026-07-18 durchgängig „Drucken" (Weltstandard, im
                        ' Fenster-Tunnel abgefangen). „Vorschau anwenden" liegt jetzt auf
                        ' Strg+UMSCHALT+P; ohne die Umschalt-Prüfung würden beide feuern.
                        If Not isTextInputFocused AndAlso e.KeyModifiers.HasFlag(KeyModifiers.Shift) Then
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
                            CopySelectionToSystemClipboardAsync(vm)
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
                        If vm.HasSelectedAnnotation AndAlso Not isInputControlFocused Then
                            vm.NudgeSelectedAnnotation(-If(e.KeyModifiers.HasFlag(KeyModifiers.Shift), 5.0, 1.0), 0)
                            e.Handled = True
                        End If
                    Case Key.Right
                        If vm.HasSelectedAnnotation AndAlso Not isInputControlFocused Then
                            vm.NudgeSelectedAnnotation(If(e.KeyModifiers.HasFlag(KeyModifiers.Shift), 5.0, 1.0), 0)
                            e.Handled = True
                        End If
                    Case Key.Up
                        If vm.HasSelectedAnnotation AndAlso Not isInputControlFocused Then
                            vm.NudgeSelectedAnnotation(0, -If(e.KeyModifiers.HasFlag(KeyModifiers.Shift), 5.0, 1.0))
                            e.Handled = True
                        End If
                    Case Key.Down
                        If vm.HasSelectedAnnotation AndAlso Not isInputControlFocused Then
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
                        ElseIf _isSelectionDragging OrElse _isLassoDrawing OrElse _isSelectionMoveDragging Then
                            ' Ein laufendes Aufziehen/Ziehen abbrechen, ohne es zu übernehmen - vorher
                            ' verließ Esc mitten im Zug den Editor.
                            CancelSelectionDrag()
                        ElseIf vm.HasSelectedAnnotation OrElse Not String.IsNullOrEmpty(vm.PendingInsertKind) Then
                            vm.SelectedAnnotationIndex = -1
                            vm.PendingInsertKind = ""
                        ElseIf IsSelectionScopeTool(vm.CurrentTool) AndAlso vm.HasActiveSelection Then
                            vm.ClearSelection()
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
