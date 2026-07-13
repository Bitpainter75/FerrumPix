Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports SkiaSharp
Imports ReactiveUI
Imports FerrumPix.Services
Imports FerrumPix.Models

Namespace ViewModels

    Public Class EditorViewModel
        Inherits ViewModelBase

        Private ReadOnly _mainVm As MainWindowViewModel
        Private _currentImagePath As String = ""
        ' True, wenn die Quelle nicht in-place überschrieben werden darf (z.B. eine Immich-Temp-Kopie):
        ' "Speichern" ist dann gesperrt und leitet überall auf "Speichern unter" um.
        Private _forceSaveAsOnly As Boolean = False
        ' Immich-Album, aus dem das aktuelle Bild stammt - ein Upload des bearbeiteten Bildes nach Immich
        ' landet dann gleich in diesem Album (leer = nur in die Bibliothek).
        Private _immichSourceAlbumId As String = Nothing
        ''' Originaldateiname des Immich-Assets - die Temp-Kopie heißt nach der Asset-ID, beim
        ''' Zurückschreiben soll der Name aber erhalten bleiben (siehe SaveBackToImmichAsync).
        Private _immichSourceFileName As String = Nothing
        Private _currentImage As Bitmap
        Private _previewImage As Bitmap
        Private _comparisonImage As Bitmap
        Private _selectedAnnotationOverlayImage As Bitmap
        Private _selectedAnnotationOverlayMetrics As ImageProcessor.AnnotationOverlayRender
        Private _currentTool As EditorTool = EditorTool.Crop
        Private _brightness As Double = 0
        Private _contrast As Double = 0
        Private _saturation As Double = 0
        Private _highlights As Double = 0
        Private _shadowsLevel As Double = 0
        Private _whites As Double = 0
        Private _blacks As Double = 0
        Private _temperature As Double = 0
        Private _tint As Double = 0
        Private _splitToningShadowHue As Double = 0
        Private _splitToningShadowSaturation As Double = 0
        Private _splitToningHighlightHue As Double = 0
        Private _splitToningHighlightSaturation As Double = 0
        Private _splitToningBalance As Double = 0
        Private _exposure As Double = 0
        Private _sharpness As Double = 0
        Private _noiseReduction As Double = 0
        Private _noiseReductionMethod As NoiseReductionMethod = NoiseReductionMethod.Gaussian
        Private _dustScratches As Double = 0
        Private _haze As Double = 0
        Private _addNoise As Double = 0
        Private _structure As Double = 0
        Private _glow As Double = 0
        Private _vibrance As Double = 0
        Private _vignette As Double = 0
        Private _vignetteTransition As Double = 55
        Private _vignetteRoundness As Double = 0
        Private _vignetteFeather As Double = 70
        Private _vignetteCenterX As Double = 50
        Private _vignetteCenterY As Double = 50
        Private _grain As Double = 0
        Private _borderSize As Double = 0
        Private _borderColor As String = "#FFFFFFFF"
        Private _borderCornerRadius As Double = 0
        Private _borderEffect As String = "Einfach"
        Private _clarity As Double = 0
        Private _negativeEnabled As Boolean = False
        Private _negativeMonochrome As Boolean = False
        Private _negativeBaseColor As String = ""
        Private _negativeDensityColor As String = ""
        Private _negativeGamma As Double = 0
        ''' Solange die Pipette auf die Filmbasis wartet, muss die Umkehr aus der VORSCHAU heraus: die
        ''' Pipette liest die Farbe, die auf dem Schirm steht - im umgerechneten Positiv würde der Nutzer
        ''' also die Farbe des fertigen Bildes als "Filmbasis" setzen statt die des Filmträgers.
        Private _suppressNegativeForPick As Boolean = False
        Private ReadOnly _curveRgbPoints As New ObservableCollection(Of Avalonia.Point) From {New Avalonia.Point(0, 0), New Avalonia.Point(255, 255)}
        Private ReadOnly _curveRedPoints As New ObservableCollection(Of Avalonia.Point) From {New Avalonia.Point(0, 0), New Avalonia.Point(255, 255)}
        Private ReadOnly _curveGreenPoints As New ObservableCollection(Of Avalonia.Point) From {New Avalonia.Point(0, 0), New Avalonia.Point(255, 255)}
        Private ReadOnly _curveBluePoints As New ObservableCollection(Of Avalonia.Point) From {New Avalonia.Point(0, 0), New Avalonia.Point(255, 255)}
        Private ReadOnly _curveLuminancePoints As New ObservableCollection(Of Avalonia.Point) From {New Avalonia.Point(0, 0), New Avalonia.Point(255, 255)}
        Private _selectedCurveChannel As CurveChannel = CurveChannel.Rgb
        Private _curveHistogramCounts As (R As Integer(), G As Integer(), B As Integer(), L As Integer())
        Private _suppressCurvePointsChanged As Boolean
        Private _redHue As Double = 0
        Private _redSaturation As Double = 0
        Private _redLuminance As Double = 0
        Private _orangeHue As Double = 0
        Private _orangeSaturation As Double = 0
        Private _orangeLuminance As Double = 0
        Private _yellowHue As Double = 0
        Private _yellowSaturation As Double = 0
        Private _yellowLuminance As Double = 0
        Private _greenHue As Double = 0
        Private _greenSaturation As Double = 0
        Private _greenLuminance As Double = 0
        Private _aquaHue As Double = 0
        Private _aquaSaturation As Double = 0
        Private _aquaLuminance As Double = 0
        Private _blueHue As Double = 0
        Private _blueSaturation As Double = 0
        Private _blueLuminance As Double = 0
        Private _purpleHue As Double = 0
        Private _purpleSaturation As Double = 0
        Private _purpleLuminance As Double = 0
        Private _magentaHue As Double = 0
        Private _magentaSaturation As Double = 0
        Private _magentaLuminance As Double = 0
        Private _retouchRadius As Double = 24.0
        Private _brushSize As Double = 24.0
        Private _brushHardness As Double = 100
        Private _brushOpacity As Double = 100
        Private _isEraserMode As Boolean = False

        ''' Pinsel, Radiergummi, Verwischen und Stempel arbeiten auf denselben Feldern (_brushSize,
        ''' _retouchRadius, _annotationStrokeColor). Damit beim Wechsel keines die Werte des anderen
        ''' erbt, sichert SetPaintMode den Stand des verlassenen Werkzeugs hier und holt den des neuen
        ''' zurück - beim ersten Aufruf sind das die Startwerte aus ResetEditorUiStateForNewImage.
        Private NotInheritable Class PaintToolState
            Public Property Size As Double = 24.0
            Public Property Hardness As Double = 100
            Public Property Opacity As Double = 100
            Public Property StrokeColor As String = "#FF000000"
        End Class

        Private _paintToolStates As Dictionary(Of String, PaintToolState) = NewPaintToolStates()

        Private Shared Function NewPaintToolStates() As Dictionary(Of String, PaintToolState)
            Return New Dictionary(Of String, PaintToolState)(StringComparer.Ordinal) From {
                {"Brush", New PaintToolState()},
                {"Eraser", New PaintToolState()},
                {"Blur", New PaintToolState()},
                {"Clone", New PaintToolState()}
            }
        End Function
        Private ReadOnly _retouchSpots As New List(Of RetouchSpot)()
        ' Klonquelle in Prozent der Bildkante; negativ = nicht gesetzt. Der Versatz zwischen Quelle und
        ' erstem gesetzten Punkt wird gemerkt und für den restlichen Zug beibehalten ("aligned"), damit
        ' beim Ziehen ein zusammenhängender Bildausschnitt herüberwandert statt derselbe Fleck.
        Private _cloneSourceXPercent As Double = -1
        Private _cloneSourceYPercent As Double = -1
        Private _cloneOffsetXPixels As Double = 0
        Private _cloneOffsetYPixels As Double = 0
        Private _hasCloneOffset As Boolean = False
        ' Beide Mal-Modi setzen dieselben RetouchSpots; der Stempel hängt ihnen eine Klonquelle an,
        ' das Verwischen nicht. Deshalb ein Schalter statt eines zweiten EditorTool-Werts.
        Private _isCloneMode As Boolean = False
        Private _filterPreset As String = "Keine"
        Private _filterStrength As Double = 100
        Private _lutPath As String = ""
        Private _lutStrength As Double = 100
        Private _lastAppliedFilterPresetName As String = ""
        Private _lastAppliedLightroomPresetPath As String = ""
        Private _lastAppliedLutPresetPath As String = ""
        Private _whiteBalance As String = "Wie Aufnahme"
        Private _rotationDegrees As Integer = 0
        Private _straightenDegrees As Double = 0
        Private _straightenExpandCanvas As Boolean = False
        Private _flipH As Boolean = False
        Private _flipV As Boolean = False
        Private _cropLeft As Double = 0
        Private _cropTop As Double = 0
        Private _cropRight As Double = 0
        Private _cropBottom As Double = 0
        ' _appliedCropXxx spiegelt den zuletzt per "Zuschneiden anwenden" bestätigten Ausschnitt wider.
        ' GetCurrentAdjustments() (Vorschau/Speichern) liest ausschließlich diese Werte, damit ein noch
        ' nicht bestätigter Zuschnitt (nur _cropXxx) nicht durch eine unabhängige Vorschau-Aktualisierung
        ' (z.B. beim Hinzufügen einer Text-Ebene) versehentlich mit gebacken wird.
        Private _appliedCropLeft As Double = 0
        Private _appliedCropTop As Double = 0
        Private _appliedCropRight As Double = 0
        Private _appliedCropBottom As Double = 0
        Private _resizeWidth As Integer = 0
        Private _resizeHeight As Integer = 0
        Private _lockResizeAspect As Boolean = True
        Private _resizeInterpolation As ResizeInterpolationMode = ResizeInterpolationMode.Bilinear
        Private _canvasWidth As Integer = 0
        Private _canvasHeight As Integer = 0
        Private _lockCanvasAspect As Boolean = True
        Private _canvasAnchor As String = "Center"
        Private _canvasBackgroundColor As String = "#FF000000"
        ' Analog zu _appliedCropXxx: die zuletzt per "<Werkzeug> anwenden" bestätigten Werte.
        ' GetCurrentAdjustments() (kanonisch, forPreview:=False) liest ausschließlich diese Felder -
        ' die Live-Felder oben (_resizeWidth usw.) treiben nur die Live-Vorschau
        ' (GetCurrentAdjustments(forPreview:=True)) und die Eingabefelder selbst. Ohne diese Trennung
        ' wurde jede Änderung sofort wirksam (auch beim Verlassen des Werkzeugs ohne "Anwenden").
        Private _appliedResizeWidth As Integer = 0
        Private _appliedResizeHeight As Integer = 0
        Private _appliedCanvasWidth As Integer = 0
        Private _appliedCanvasHeight As Integer = 0
        Private _appliedRotationDegrees As Integer = 0
        Private _appliedStraightenDegrees As Double = 0
        Private _appliedStraightenExpandCanvas As Boolean = False
        Private _appliedFlipH As Boolean = False
        Private _appliedFlipV As Boolean = False
        Private _isUpdatingCanvas As Boolean
        Private _annotationText As String = "Text"
        Private _annotationFillColor As String = "#00FFFFFF"
        Private _annotationStrokeColor As String = "#FF000000"
        Private _annotationFontSize As Double = 48
        Private _annotationStrokeWidth As Double = 0
        Private _annotationFontFamily As String = "Arial"
        Private _annotationOpacity As Double = 100
        Private _annotationRotation As Double = 0
        Private _annotationFlipH As Boolean = False
        Private _annotationFlipV As Boolean = False
        Private _annotationAnchor As String = "BottomRight"
        Private _annotationIsVisible As Boolean = True
        Private _annotationXPercent As Double = 35
        Private _annotationYPercent As Double = 35
        Private _annotationWidthPercent As Double = 30
        Private _annotationHeightPercent As Double = 12
        Private _annotationFillKind As String = "Solid"
        Private _annotationFillColor2 As String = "#FFFFFFFF"
        Private _annotationGradientAngle As Double = 0
        Private _annotationGradientInverted As Boolean = False
        Private _annotationShadowEnabled As Boolean = False
        Private _annotationShadowOffsetX As Double = 2
        Private _annotationShadowOffsetY As Double = 2
        Private _annotationShadowLightAngle As Double = 225
        Private _annotationShadowBlur As Double = 6
        Private _annotationShadowStrength As Double = 100
        Private _annotationShadowColor As String = "#80000000"
        Private _annotationShadowRounded As Boolean = False
        Private _annotationShadowCornerRadius As Double = 20
        Private _annotationShadowSize As Double = 100
        Private _annotationGlowEnabled As Boolean = False
        Private _annotationGlowBlur As Double = 10
        Private _annotationGlowStrength As Double = 100
        Private _annotationGlowColor As String = "#FFFFFF00"
        Private _watermarkImagePath As String = ""
        Private _hasActiveSelection As Boolean = False
        Private _selectionXPercent As Double = 0
        Private _selectionYPercent As Double = 0
        Private _selectionWidthPercent As Double = 0
        Private _selectionHeightPercent As Double = 0
        ' Auswahlmodus (Untermenü im Auswahl-Werkzeug) und - für Ellipse/Lasso/Zauberstab - die
        ' zugehörige Alpha8-Maske in Bildpixeln samt umschließendem Rechteck. Beim Rechteck ist die
        ' Maske Nothing (dann greift der einfache Rechteck-Pfad).
        Private _selectionMode As String = "Rectangle"
        Private _selectionTolerance As Double = 15
        Private _selectionCombineMode As String = "New"
        Private _selectionMask As SKBitmap = Nothing
        Private _selectionMaskRect As SKRectI = SKRectI.Empty
        Private _selectionMaskBytes As Byte() = Nothing
        Private _selectionMaskBytesStride As Integer = 0
        ' Gecachte PNG/Base64-Kodierung von _selectionMask (siehe EncodeSelectionMaskBase64).
        Private _selectionMaskBase64 As String = ""
        Private _selectionMaskPreviewImage As Bitmap = Nothing
        Private _selectionShapeMode As String = "Rectangle"
        Private _selectionShapePointsX As Double() = Nothing
        Private _selectionShapePointsY As Double() = Nothing
        Private _selectionMaskEdgePointsX As Double() = Nothing
        Private _selectionMaskEdgePointsY As Double() = Nothing
        Private ReadOnly _annotations As New ObservableCollection(Of ImageAnnotation)()
        Private _selectedAnnotationIndex As Integer = -1
        ' Verfolgt die Ebene, an die der aktuell laufende Pinsel-/Radiergummi-"Sitzung" noch weitere
        ' Striche anhängt, statt für jeden einzelnen Strich eine neue Ebene anzulegen (siehe
        ' AddBrushStroke). Per Objektreferenz statt Index verfolgt, damit ein zwischenzeitliches
        ' Undo/Redo (das _annotations komplett durch geklonte Objekte ersetzt) automatisch über die
        ' Contains-Prüfung in AddBrushStroke erkannt wird, ohne hier explizit zurückgesetzt werden zu
        ' müssen. Endet explizit bei Werkzeugwechsel, Pinsel/Radiergummi-Umschalten oder Ebenenwechsel
        ' (CurrentTool-/IsEraserMode-/SelectedAnnotationIndex-Setter).
        Private _activeStrokeAnnotation As ImageAnnotation = Nothing
        Private _activeStrokeIsEraser As Boolean = False
        Private _isLoadingAnnotation As Boolean
        Private _annotationFontOptionsCache As IReadOnlyList(Of String)
        Private _isUpdatingResize As Boolean
        Private _rating As Integer = 0
        Private _isFavorite As Boolean = False
        Private _newTagText As String = ""
        Private _hasChanges As Boolean
        Private _statusText As String = ""
        Private _mousePositionText As String = ""
        Private _activeZoomPreset As ZoomPresetMode = ZoomPresetMode.Fit
        Private _saveQuality As Integer = 90
        Private _histogramImage As Bitmap
        Private _exifInfo As ExifData
        Private _previewPending As Boolean
        Private _overlayHidesSelectionFromPreview As Boolean = False
        ' Verhindert, dass eine einzelne logische Nutzeraktion (z.B. Werkzeugwechsel, der intern
        ' sowohl SelectedAnnotationIndex als auch CurrentTool setzt) NotifyAnnotationOverlayStateChanged
        ' mehrfach hintereinander auslöst - das würde sonst einen sofortigen Render mit einem kurz
        ' danach erneut gestarteten, debounce-verzögerten Render racen (siehe RequestOverlayStateNotify).
        Private _overlayNotifySuppressDepth As Integer = 0
        Private ReadOnly _previewTimer As DispatcherTimer
        Private ReadOnly _filmstripNavDebouncer As FilmstripNavigationDebouncer
        Private ReadOnly _previewSync As New Object()
        Private ReadOnly _stalePreviewSources As New List(Of SKBitmap)()
        Private _previewSource As SKBitmap
        Private _previewRenderCts As CancellationTokenSource
        Private _previewRequestId As Integer
        Private _activePreviewRenders As Integer
        Private _showBeforeImage As Boolean = False
        Private _comparisonAutoEnabled As Boolean = True
        Private _folderPaths As New List(Of String)()
        ' Cache-Scope für die Filmstreifen-Thumbnails (Suchlisten-Scope statt je Ursprungsordner) - siehe ViewerViewModel.
        Private _thumbCacheScopeId As String = Nothing
        Private _thumbCacheScopeName As String = Nothing
        Private _currentIndex As Integer = -1
        Private _selectedInfoTab As InfoSidebarTab = InfoSidebarTab.General
        Private _selectedLayersPanelTab As LayersPanelTab = LayersPanelTab.Tool
        Private Const PreviewMaxDimension As Integer = 1600
        Private Const UndoCaptureWindowMs As Double = 650
        ' Text und Wasserzeichen dürfen kleiner werden als die 5%/4%, die für Formen gelten: ihr Rechteck
        ' wird aus dem Text berechnet und soll ihn eng umschließen. Auf einem 4000-px-Foto wären 5% bereits
        ' 200 px - ein kurzes Wort in 48 px Schrift bekäme so einen doppelt zu breiten Auswahlrahmen.
        Private Const MinTextAnnotationWidthPercent As Double = 1.0
        Private Const MinTextAnnotationHeightPercent As Double = 1.0
        Private _suppressUndoCapture As Boolean
        Private _lastUndoProperty As String = ""
        Private _lastUndoCapturedAt As DateTime = DateTime.MinValue

        Public Property WhiteBalanceOptions As New System.Collections.ObjectModel.ObservableCollection(Of String) From {
            "Wie Aufnahme", "Automatisch", "Tageslicht", "Bewölkt", "Schatten",
            "Glühlampe", "Leuchtstoff", "Blitz", "Benutzerdefiniert"
        }

        Public Property FilterPresetOptions As New System.Collections.ObjectModel.ObservableCollection(Of String)(ImageAdjustments.FilterPresetNames)


        ' Undo-Stack
        Private ReadOnly _undoStack As New Stack(Of ImageAdjustments)()
        Private ReadOnly _redoStack As New Stack(Of ImageAdjustments)()

        Public Property FilmstripItems As BulkObservableCollection(Of ImageItem)
        Public Property Tags As ObservableCollection(Of String)
        Public Property TagSuggestions As ObservableCollection(Of String)
        Public Property HistoryItems As ObservableCollection(Of String)
        Private Shared ReadOnly FallbackFontOptions As IReadOnlyList(Of String) =
            New List(Of String) From {"Arial", "Segoe UI", "DejaVu Sans", "Liberation Sans", "Times New Roman", "Georgia", "Courier New", "Consolas", "Verdana", "Tahoma"}

        Public ReadOnly Property AnnotationFontOptions As IReadOnlyList(Of String)
            Get
                If _annotationFontOptionsCache Is Nothing Then
                    Dim names = Avalonia.Media.FontManager.Current?.SystemFonts?.
                        Select(Function(f) f.Name).
                        Where(Function(n) Not String.IsNullOrWhiteSpace(n)).
                        Distinct(StringComparer.OrdinalIgnoreCase).
                        OrderBy(Function(n) n, StringComparer.OrdinalIgnoreCase).
                        ToList()
                    _annotationFontOptionsCache = If(names IsNot Nothing AndAlso names.Count > 0, names, FallbackFontOptions)
                End If
                Return _annotationFontOptionsCache
            End Get
        End Property

        Public ReadOnly Property TextAnnotations As ObservableCollection(Of ImageAnnotation)
            Get
                Return _annotations
            End Get
        End Property

        Private ReadOnly _allShapeIcons As New List(Of ShapeIconEntry)()
        Private ReadOnly _filteredShapeIcons As New BulkObservableCollection(Of ShapeIconEntry)()
        Private ReadOnly _watermarkPresets As New List(Of WatermarkPresetSettings)()
        Public ReadOnly Property SavedLightroomPresets As ObservableCollection(Of LightroomPresetSettings) = New ObservableCollection(Of LightroomPresetSettings)()
        Public ReadOnly Property SavedLutPresets As ObservableCollection(Of LutPresetSettings) = New ObservableCollection(Of LutPresetSettings)()

        Public ReadOnly Property LastAppliedFilterPresetName As String
            Get
                Return _lastAppliedFilterPresetName
            End Get
        End Property
        Public ReadOnly Property WatermarkPresetNames As ObservableCollection(Of String) = New ObservableCollection(Of String)()
        Private _selectedWatermarkPresetName As String = ""
        Private _watermarkPresetNameDraft As String = ""

        Public ReadOnly Property FilteredShapeIcons As BulkObservableCollection(Of ShapeIconEntry)
            Get
                Return _filteredShapeIcons
            End Get
        End Property

        Public Property SelectedWatermarkPresetName As String
            Get
                Return _selectedWatermarkPresetName
            End Get
            Set(value As String)
                Dim normalized = If(value, "").Trim()
                If normalized = _selectedWatermarkPresetName Then Return
                Me.RaiseAndSetIfChanged(_selectedWatermarkPresetName, normalized)
                If Not String.IsNullOrWhiteSpace(normalized) Then
                    _watermarkPresetNameDraft = normalized
                    Me.RaisePropertyChanged(NameOf(WatermarkPresetNameDraft))
                    ApplyWatermarkPreset(normalized)
                    PlacePendingWatermark()
                End If
            End Set
        End Property

        Public Property WatermarkPresetNameDraft As String
            Get
                Return _watermarkPresetNameDraft
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_watermarkPresetNameDraft, If(value, "").Trim())
            End Set
        End Property

        Public ReadOnly Property ShowWatermarkPresetControls As Boolean
            Get
                Return EffectiveAnnotationKind = "Watermark"
            End Get
        End Property

        ''' Das Bild-Werkzeug wartete bisher auf einen Klick in die Leinwand, um den Dateidialog zu
        ''' öffnen. Der Knopf im Eigenschaften-Panel bietet denselben Weg wie beim Wasserzeichen an.
        Public ReadOnly Property ShowImageSourceControls As Boolean
            Get
                Return EffectiveAnnotationKind = "Image"
            End Get
        End Property

        Public ReadOnly Property ShowWatermarkAnchorControls As Boolean
            Get
                Return EffectiveAnnotationKind = "Watermark"
            End Get
        End Property

        Public ReadOnly Property ShowFreeAnnotationPositionControls As Boolean
            Get
                Return EffectiveAnnotationKind <> "Watermark"
            End Get
        End Property

        Public ReadOnly Property AnnotationPositionMinimum As Double
            Get
                Return If(ShowWatermarkAnchorControls, -50, 0)
            End Get
        End Property

        Public ReadOnly Property AnnotationPositionMaximum As Double
            Get
                Return If(ShowWatermarkAnchorControls, 50, 99)
            End Get
        End Property

        Public ReadOnly Property AnnotationXSliderMinimum As Double
            Get
                If Not ShowWatermarkAnchorControls Then Return Math.Round(GetBaseWidth() * GetAnnotationPositionMinimumPercent(_annotationWidthPercent) / 100.0)
                Return Math.Round(GetBaseWidth() * -AnnotationOffsetLimitPercent / 100.0)
            End Get
        End Property

        Public ReadOnly Property AnnotationXSliderMaximum As Double
            Get
                If Not ShowWatermarkAnchorControls Then Return Math.Round(GetBaseWidth() * (100.0 - AnnotationMinVisiblePercent) / 100.0)
                Return Math.Round(GetBaseWidth() * AnnotationOffsetLimitPercent / 100.0)
            End Get
        End Property

        Public ReadOnly Property AnnotationYSliderMinimum As Double
            Get
                If Not ShowWatermarkAnchorControls Then Return Math.Round(GetBaseHeight() * GetAnnotationPositionMinimumPercent(_annotationHeightPercent) / 100.0)
                Return Math.Round(GetBaseHeight() * -AnnotationOffsetLimitPercent / 100.0)
            End Get
        End Property

        Public ReadOnly Property AnnotationYSliderMaximum As Double
            Get
                If Not ShowWatermarkAnchorControls Then Return Math.Round(GetBaseHeight() * (100.0 - AnnotationMinVisiblePercent) / 100.0)
                Return Math.Round(GetBaseHeight() * AnnotationOffsetLimitPercent / 100.0)
            End Get
        End Property

        Public ReadOnly Property AnnotationWidthSliderMinimum As Double
            Get
                Return Math.Round(GetBaseWidth() * 5.0 / 100.0)
            End Get
        End Property

        Public ReadOnly Property AnnotationWidthSliderMaximum As Double
            Get
                Return Math.Round(GetBaseWidth() * 90.0 / 100.0)
            End Get
        End Property

        Public ReadOnly Property AnnotationHeightSliderMinimum As Double
            Get
                Return Math.Round(GetBaseHeight() * 4.0 / 100.0)
            End Get
        End Property

        Public ReadOnly Property AnnotationHeightSliderMaximum As Double
            Get
                Return Math.Round(GetBaseHeight() * 90.0 / 100.0)
            End Get
        End Property

        Public ReadOnly Property AnnotationSliderDragIncrement As Double
            Get
                Return 1
            End Get
        End Property

        Public ReadOnly Property AnnotationSliderWheelIncrement As Double
            Get
                Return 1
            End Get
        End Property

        Public ReadOnly Property AnnotationXLabel As String
            Get
                Return If(ShowWatermarkAnchorControls, "Abst. X", "X")
            End Get
        End Property

        Public ReadOnly Property AnnotationYLabel As String
            Get
                Return If(ShowWatermarkAnchorControls, "Abst. Y", "Y")
            End Get
        End Property

        Public Property AnnotationAnchor As String
            Get
                Return NormalizeAnnotationAnchor(_annotationAnchor)
            End Get
            Set(value As String)
                Dim normalized = NormalizeAnnotationAnchor(value)
                If String.Equals(_annotationAnchor, normalized, StringComparison.OrdinalIgnoreCase) Then Return
                Me.RaiseAndSetIfChanged(_annotationAnchor, normalized)
                RaiseAnnotationPositionControlProperties()
                SyncSelectedAnnotation()
            End Set
        End Property

        Public ReadOnly Property IsWatermarkImageSource As Boolean
            Get
                Return EffectiveAnnotationKind = "Watermark" AndAlso Not String.IsNullOrWhiteSpace(_watermarkImagePath)
            End Get
        End Property

        Public ReadOnly Property ShowSelectedSvgOverlay As Boolean
            Get
                Return _selectedAnnotationOverlayImage IsNot Nothing
            End Get
        End Property

        Public Property SelectedAnnotationOverlayImage As Bitmap
            Get
                Return _selectedAnnotationOverlayImage
            End Get
            Set(value As Bitmap)
                Dim previous = _selectedAnnotationOverlayImage
                Me.RaiseAndSetIfChanged(_selectedAnnotationOverlayImage, value)
                Me.RaisePropertyChanged(NameOf(ShowSelectedSvgOverlay))
                If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, value) Then DisposeDeferred(previous)
            End Set
        End Property

        ''' Lage des Objekts innerhalb von SelectedAnnotationOverlayImage (Bitmap-Pixel). Das Bitmap ist um
        ''' die Schatten-/Glow-Ränder größer als das Objekt; die View braucht dieses Rechteck, um das Bitmap
        ''' deckungsgleich mit dem gebackenen Bild über die Objekt-Border zu legen.
        Public ReadOnly Property SelectedAnnotationOverlayMetrics As ImageProcessor.AnnotationOverlayRender
            Get
                Return _selectedAnnotationOverlayMetrics
            End Get
        End Property

        Public ReadOnly Property SelectedAnnotationImagePath As String
            Get
                If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return ""
                Return If(_annotations(_selectedAnnotationIndex)?.ImagePath, "")
            End Get
        End Property

        Private _shapeIconSearchText As String = ""
        Public Property ShapeIconSearchText As String
            Get
                Return _shapeIconSearchText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_shapeIconSearchText, If(value, ""))
                RefreshFilteredShapeIcons()
            End Set
        End Property

        Private Sub LoadAllShapeIcons()
            _allShapeIcons.Clear()
            Try
                Dim assets = Avalonia.Platform.AssetLoader.GetAssets(New Uri("avares://FerrumPix/Assets/Icons/outline/"), Nothing)
                For Each uri In assets
                    Dim path = uri.ToString()
                    If Not path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) Then Continue For
                    Dim sourceName = FormatIconDisplayName(path)
                    _allShapeIcons.Add(New ShapeIconEntry With {
                        .IconPath = path,
                        .SourceName = sourceName,
                        .DisplayName = LocalizationService.Tag(sourceName),
                        .PendingKind = "Svg:" & path
                    })
                Next
                _allShapeIcons.Sort(Function(a, b) String.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase))
            Catch
            End Try
            RefreshFilteredShapeIcons()
        End Sub

        Private Sub LoadWatermarkPresets()
            _watermarkPresets.Clear()
            WatermarkPresetNames.Clear()
            WatermarkPresetNames.Add("")
            For Each preset In AppSettingsService.Load().WatermarkPresets
                _watermarkPresets.Add(preset)
                WatermarkPresetNames.Add(preset.Name)
            Next
        End Sub

        Private Sub LoadSavedLightroomPresets()
            SavedLightroomPresets.Clear()
            For Each preset In AppSettingsService.Load().LightroomPresets
                SavedLightroomPresets.Add(preset)
            Next
            SyncLastAppliedLightroomPreset()
        End Sub

        Private Sub PersistSavedLightroomPresets()
            Dim settings = AppSettingsService.Load()
            settings.LightroomPresets = SavedLightroomPresets.Select(Function(p) New LightroomPresetSettings With {
                .Id = p.Id,
                .Name = p.Name,
                .Path = p.Path
            }).ToList()
            AppSettingsService.Save(settings)
            LoadSavedLightroomPresets()
        End Sub

        Private Sub LoadSavedLutPresets()
            SavedLutPresets.Clear()
            For Each preset In AppSettingsService.Load().LutPresets
                SavedLutPresets.Add(preset)
            Next
            SyncLastAppliedLutPreset()
        End Sub

        Private Sub SetLastAppliedFilterPreset(presetName As String)
            Dim normalized = If(presetName, "").Trim()
            Dim hasFilter = Not String.IsNullOrWhiteSpace(normalized) AndAlso
                Not String.Equals(normalized, "Keine", StringComparison.OrdinalIgnoreCase)

            Dim nextFilterPreset = If(hasFilter, normalized, "")
            If Not String.Equals(_lastAppliedFilterPresetName, nextFilterPreset, StringComparison.Ordinal) Then
                _lastAppliedFilterPresetName = nextFilterPreset
                Me.RaisePropertyChanged(NameOf(LastAppliedFilterPresetName))
            End If

            If hasFilter Then
                _lastAppliedLightroomPresetPath = ""
                _lastAppliedLutPresetPath = ""
                SyncLastAppliedLightroomPreset()
                SyncLastAppliedLutPreset()
            End If
        End Sub

        Private Sub SetLastAppliedLightroomPreset(xmpPath As String)
            _lastAppliedFilterPresetName = ""
            Me.RaisePropertyChanged(NameOf(LastAppliedFilterPresetName))
            _lastAppliedLutPresetPath = ""
            _lastAppliedLightroomPresetPath = If(xmpPath, "").Trim()
            SyncLastAppliedLightroomPreset()
            SyncLastAppliedLutPreset()
        End Sub

        Private Sub SetLastAppliedLutPreset(cubePath As String)
            _lastAppliedFilterPresetName = ""
            Me.RaisePropertyChanged(NameOf(LastAppliedFilterPresetName))
            _lastAppliedLightroomPresetPath = ""
            _lastAppliedLutPresetPath = If(cubePath, "").Trim()
            SyncLastAppliedLightroomPreset()
            SyncLastAppliedLutPreset()
        End Sub

        Private Sub ApplyExclusiveFilterPreset(preset As String)
            Dim normalized = If(String.IsNullOrWhiteSpace(preset), "Keine", preset)
            ResetFilterInternal()

            _filterPreset = normalized
            If Not String.Equals(normalized, "Keine", StringComparison.OrdinalIgnoreCase) Then
                _filterStrength = ImageAdjustments.DefaultFilterStrength(normalized)
            End If

            Me.RaisePropertyChanged(NameOf(FilterPreset))
            Me.RaisePropertyChanged(NameOf(FilterStrength))
            SetLastAppliedFilterPreset(normalized)
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub ClearLastAppliedLook()
            _lastAppliedFilterPresetName = ""
            _lastAppliedLightroomPresetPath = ""
            _lastAppliedLutPresetPath = ""
            Me.RaisePropertyChanged(NameOf(LastAppliedFilterPresetName))
            SyncLastAppliedLightroomPreset()
            SyncLastAppliedLutPreset()
        End Sub

        Private Sub SyncLastAppliedLightroomPreset()
            For Each preset In SavedLightroomPresets
                preset.IsLastApplied = Not String.IsNullOrWhiteSpace(_lastAppliedLightroomPresetPath) AndAlso
                    String.Equals(preset.Path, _lastAppliedLightroomPresetPath, StringComparison.OrdinalIgnoreCase)
            Next
        End Sub

        Private Sub SyncLastAppliedLutPreset()
            For Each preset In SavedLutPresets
                preset.IsLastApplied = Not String.IsNullOrWhiteSpace(_lastAppliedLutPresetPath) AndAlso
                    String.Equals(preset.Path, _lastAppliedLutPresetPath, StringComparison.OrdinalIgnoreCase)
            Next
        End Sub

        Private Sub PersistSavedLutPresets()
            Dim settings = AppSettingsService.Load()
            settings.LutPresets = SavedLutPresets.Select(Function(p) New LutPresetSettings With {
                .Id = p.Id,
                .Name = p.Name,
                .Path = p.Path
            }).ToList()
            AppSettingsService.Save(settings)
            LoadSavedLutPresets()
        End Sub

        Private Sub PersistWatermarkPresets()
            Dim settings = AppSettingsService.Load()
            settings.WatermarkPresets = _watermarkPresets.Select(Function(p) New WatermarkPresetSettings With {
                .Id = p.Id,
                .Name = p.Name,
                .Text = p.Text,
                .ImagePath = p.ImagePath,
                .OffsetXPixels = p.OffsetXPixels,
                .OffsetYPixels = p.OffsetYPixels,
                .WidthPixels = p.WidthPixels,
                .HeightPixels = p.HeightPixels,
                .Anchor = p.Anchor,
                .RotationDegrees = p.RotationDegrees,
                .Opacity = p.Opacity,
                .FontFamily = p.FontFamily,
                .FontSizePixels = p.FontSizePixels,
                .FillColor = p.FillColor
            }).ToList()
            AppSettingsService.Save(settings)
            LoadWatermarkPresets()
        End Sub

        Private Sub RaiseWatermarkUiChanged()
            Me.RaisePropertyChanged(NameOf(ShowWatermarkPresetControls))
            Me.RaisePropertyChanged(NameOf(ShowImageSourceControls))
            Me.RaisePropertyChanged(NameOf(IsWatermarkImageSource))
            Me.RaisePropertyChanged(NameOf(ShowWatermarkAnchorControls))
            Me.RaisePropertyChanged(NameOf(ShowFreeAnnotationPositionControls))
            Me.RaisePropertyChanged(NameOf(AnnotationPositionMinimum))
            Me.RaisePropertyChanged(NameOf(AnnotationPositionMaximum))
            RaiseAnnotationPositionControlProperties()
            Me.RaisePropertyChanged(NameOf(AnnotationXLabel))
            Me.RaisePropertyChanged(NameOf(AnnotationYLabel))
            Me.RaisePropertyChanged(NameOf(ShowTextContentControls))
            Me.RaisePropertyChanged(NameOf(ShowFontControls))
            Me.RaisePropertyChanged(NameOf(ShowFillColorControls))
            Me.RaisePropertyChanged(NameOf(ShowFillColorPicker))
            Me.RaisePropertyChanged(NameOf(ShowStrokeColorPicker))
            Me.RaisePropertyChanged(NameOf(ShowGradientFillControls))
            Me.RaisePropertyChanged(NameOf(ShowLinearGradientAngleControl))
            Me.RaisePropertyChanged(NameOf(ShowRadialGradientControl))
        End Sub

        Private Sub RaiseAnnotationPositionControlProperties()
            Me.RaisePropertyChanged(NameOf(AnnotationXSliderMinimum))
            Me.RaisePropertyChanged(NameOf(AnnotationXSliderMaximum))
            Me.RaisePropertyChanged(NameOf(AnnotationYSliderMinimum))
            Me.RaisePropertyChanged(NameOf(AnnotationYSliderMaximum))
            Me.RaisePropertyChanged(NameOf(AnnotationXSliderValue))
            Me.RaisePropertyChanged(NameOf(AnnotationYSliderValue))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthSliderMinimum))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthSliderMaximum))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightSliderMinimum))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightSliderMaximum))
        End Sub

        Private Sub ApplyWatermarkPreset(name As String)
            Dim preset = _watermarkPresets.FirstOrDefault(Function(p) String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            If preset Is Nothing Then Return

            _annotationText = If(String.IsNullOrWhiteSpace(preset.Text), "FerrumPix", preset.Text)
            _watermarkImagePath = If(preset.ImagePath, "")
            Dim baseWidth = Math.Max(1, GetBaseWidth())
            Dim baseHeight = Math.Max(1, GetBaseHeight())
            _annotationAnchor = NormalizeAnnotationAnchor(preset.Anchor)
            _annotationXPercent = ClampAnnotationOffsetPercent(preset.OffsetXPixels / CDbl(baseWidth) * 100.0)
            _annotationYPercent = ClampAnnotationOffsetPercent(preset.OffsetYPixels / CDbl(baseHeight) * 100.0)
            _annotationWidthPercent = Math.Max(1.0, Math.Min(100.0, preset.WidthPixels / CDbl(baseWidth) * 100.0))
            _annotationHeightPercent = Math.Max(1.0, Math.Min(100.0, preset.HeightPixels / CDbl(baseHeight) * 100.0))
            _annotationRotation = Math.Max(-180, Math.Min(180, preset.RotationDegrees))
            _annotationOpacity = Math.Max(0, Math.Min(100, preset.Opacity))
            _annotationFontFamily = If(String.IsNullOrWhiteSpace(preset.FontFamily), "Arial", preset.FontFamily)
            _annotationFontSize = Math.Max(8, Math.Min(5000, preset.FontSizePixels))
            _annotationFillColor = NormalizeAvaloniaColor(preset.FillColor, "#FFFFFFFF")

            Me.RaisePropertyChanged(NameOf(AnnotationText))
            Me.RaisePropertyChanged(NameOf(AnnotationAnchor))
            Me.RaisePropertyChanged(NameOf(AnnotationXPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationYPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationXPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationYPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationRotation))
            Me.RaisePropertyChanged(NameOf(AnnotationOpacity))
            Me.RaisePropertyChanged(NameOf(AnnotationFontFamily))
            Me.RaisePropertyChanged(NameOf(AnnotationFontSize))
            Me.RaisePropertyChanged(NameOf(AnnotationFontSizePixels))
            Me.RaisePropertyChanged(NameOf(AnnotationFillColor))
            Me.RaisePropertyChanged(NameOf(AnnotationFillColorValue))
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationImagePath))
            RaiseWatermarkUiChanged()
            If HasSelectedAnnotation AndAlso EffectiveAnnotationKind = "Watermark" Then SyncSelectedAnnotation()
        End Sub

        ''' Ein Wasserzeichen wartet nicht auf einen Klick in die Leinwand: sobald eine Vorlage oder ein
        ''' Bild gewählt ist, steht es an seinem Anker mit den eingestellten Abständen im Bild und lässt
        ''' sich von dort wie jedes andere Objekt greifen.
        Private Sub PlacePendingWatermark()
            If _isLoadingAnnotation OrElse HasSelectedAnnotation Then Return
            If Not String.Equals(NormalizeAnnotationKind(_pendingInsertKind), "Watermark", StringComparison.OrdinalIgnoreCase) Then Return

            AddAnnotation("Watermark")
        End Sub

        Public Sub SetWatermarkImagePath(path As String)
            _watermarkImagePath = If(path, "").Trim()
            If EffectiveAnnotationKind = "Watermark" AndAlso Not String.IsNullOrWhiteSpace(_watermarkImagePath) Then
                Dim size = GetInitialImageAnnotationSize(_watermarkImagePath)
                _annotationWidthPercent = size.WidthPercent
                _annotationHeightPercent = size.HeightPercent
                Me.RaisePropertyChanged(NameOf(AnnotationWidthPercent))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightPercent))
                Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
            End If
            RaiseWatermarkUiChanged()
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationImagePath))
            If HasSelectedAnnotation AndAlso EffectiveAnnotationKind = "Watermark" Then SyncSelectedAnnotation()
            ' Die Größe steht oben schon anhand des gewählten Bildes fest - das Objekt kann also direkt
            ' erscheinen, statt auf einen Klick in die Leinwand zu warten.
            If Not String.IsNullOrWhiteSpace(_watermarkImagePath) Then PlacePendingWatermark()
        End Sub

        Public Sub ClearWatermarkImagePath()
            SetWatermarkImagePath("")
        End Sub

        Public Sub SaveCurrentWatermarkPreset()
            Dim name = If(_watermarkPresetNameDraft, "").Trim()
            If String.IsNullOrWhiteSpace(name) Then Return

            Dim existing = _watermarkPresets.FirstOrDefault(Function(p) String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            If existing Is Nothing Then
                existing = New WatermarkPresetSettings With {.Id = Guid.NewGuid().ToString("N")}
                _watermarkPresets.Add(existing)
            End If

            existing.Name = name
            existing.Text = If(_annotationText, "")
            existing.ImagePath = If(_watermarkImagePath, "")
            existing.OffsetXPixels = AnnotationXPixels
            existing.OffsetYPixels = AnnotationYPixels
            existing.WidthPixels = AnnotationWidthPixels
            existing.HeightPixels = AnnotationHeightPixels
            existing.Anchor = NormalizeAnnotationAnchor(_annotationAnchor)
            existing.RotationDegrees = _annotationRotation
            existing.Opacity = _annotationOpacity
            existing.FontFamily = _annotationFontFamily
            existing.FontSizePixels = _annotationFontSize
            existing.FillColor = _annotationFillColor
            PersistWatermarkPresets()
            SelectedWatermarkPresetName = name
        End Sub

        Public Sub DeleteCurrentWatermarkPreset()
            Dim name = If(_selectedWatermarkPresetName, "").Trim()
            If String.IsNullOrWhiteSpace(name) Then Return
            _watermarkPresets.RemoveAll(Function(p) String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            PersistWatermarkPresets()
            _selectedWatermarkPresetName = ""
            Me.RaisePropertyChanged(NameOf(SelectedWatermarkPresetName))
        End Sub

        Private Shared Function FormatIconDisplayName(assetPath As String) As String
            Dim fileName = IO.Path.GetFileNameWithoutExtension(assetPath)
            Dim m = Text.RegularExpressions.Regex.Match(fileName, "^\d+_(?<rest>.+)$")
            Dim name = If(m.Success, m.Groups("rest").Value, fileName)
            Return name.Replace("_", " ").Replace("-", " ")
        End Function

        Private Sub RefreshFilteredShapeIcons()
            Dim query = _shapeIconSearchText.Trim()
            Dim matches = If(String.IsNullOrEmpty(query),
                              _allShapeIcons,
                              _allShapeIcons.Where(Function(e) e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) OrElse
                                                                e.SourceName.Contains(query, StringComparison.OrdinalIgnoreCase) OrElse
                                                                e.IconPath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList())
            ' Ein einzelnes Reset statt viertausend Add-Ereignisse.
            _filteredShapeIcons.ReplaceAll(matches)
        End Sub

        Private Shared Function NormalizeAnnotationAnchor(value As String) As String
            Select Case If(value, "").Trim()
                Case "TopLeft", "Top", "TopRight", "Left", "Center", "Right", "BottomLeft", "Bottom", "BottomRight"
                    Return value.Trim()
                Case Else
                    Return "BottomRight"
            End Select
        End Function

        Private Shared Function IsAnchoredWatermarkKind(kind As String) As Boolean
            Return String.Equals(NormalizeAnnotationKind(kind), "Watermark", StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function ComputeAnnotationOriginPercent(kind As String, xPercent As Double, yPercent As Double, widthPercent As Double, heightPercent As Double, anchor As String) As (X As Double, Y As Double)
            If Not IsAnchoredWatermarkKind(kind) Then
                Return (xPercent, yPercent)
            End If

            Select Case NormalizeAnnotationAnchor(anchor)
                Case "TopLeft"
                    Return (xPercent, yPercent)
                Case "Top"
                    Return ((100.0 - widthPercent) / 2.0 + xPercent, yPercent)
                Case "TopRight"
                    Return (100.0 - widthPercent - xPercent, yPercent)
                Case "Left"
                    Return (xPercent, (100.0 - heightPercent) / 2.0 + yPercent)
                Case "Center"
                    Return ((100.0 - widthPercent) / 2.0 + xPercent, (100.0 - heightPercent) / 2.0 + yPercent)
                Case "Right"
                    Return (100.0 - widthPercent - xPercent, (100.0 - heightPercent) / 2.0 + yPercent)
                Case "BottomLeft"
                    Return (xPercent, 100.0 - heightPercent - yPercent)
                Case "Bottom"
                    Return ((100.0 - widthPercent) / 2.0 + xPercent, 100.0 - heightPercent - yPercent)
                Case Else
                    Return (100.0 - widthPercent - xPercent, 100.0 - heightPercent - yPercent)
            End Select
        End Function

        Private Shared Function ComputeAnnotationOffsetPercent(kind As String, actualXPercent As Double, actualYPercent As Double, widthPercent As Double, heightPercent As Double, anchor As String) As (X As Double, Y As Double)
            If Not IsAnchoredWatermarkKind(kind) Then
                Return (actualXPercent, actualYPercent)
            End If

            Select Case NormalizeAnnotationAnchor(anchor)
                Case "TopLeft"
                    Return (actualXPercent, actualYPercent)
                Case "Top"
                    Return (actualXPercent - (100.0 - widthPercent) / 2.0, actualYPercent)
                Case "TopRight"
                    Return (100.0 - widthPercent - actualXPercent, actualYPercent)
                Case "Left"
                    Return (actualXPercent, actualYPercent - (100.0 - heightPercent) / 2.0)
                Case "Center"
                    Return (actualXPercent - (100.0 - widthPercent) / 2.0, actualYPercent - (100.0 - heightPercent) / 2.0)
                Case "Right"
                    Return (100.0 - widthPercent - actualXPercent, actualYPercent - (100.0 - heightPercent) / 2.0)
                Case "BottomLeft"
                    Return (actualXPercent, 100.0 - heightPercent - actualYPercent)
                Case "Bottom"
                    Return (actualXPercent - (100.0 - widthPercent) / 2.0, 100.0 - heightPercent - actualYPercent)
                Case Else
                    Return (100.0 - widthPercent - actualXPercent, 100.0 - heightPercent - actualYPercent)
            End Select
        End Function

        ' Negative Abstände schieben das Wasserzeichen über die verankerte Kante hinaus (angeschnitten).
        Private Const AnnotationOffsetLimitPercent As Double = 50.0

        Private Shared Function ClampAnnotationOffsetPercent(value As Double) As Double
            Return Math.Max(-AnnotationOffsetLimitPercent, Math.Min(AnnotationOffsetLimitPercent, value))
        End Function

        Private Function GetCurrentAnnotationDisplayRectPercent() As (X As Double, Y As Double, Width As Double, Height As Double)
            Dim origin = ComputeAnnotationOriginPercent(EffectiveAnnotationKind, _annotationXPercent, _annotationYPercent, _annotationWidthPercent, _annotationHeightPercent, _annotationAnchor)
            Return (origin.X, origin.Y, _annotationWidthPercent, _annotationHeightPercent)
        End Function

        Public Property SelectedAnnotationIndex As Integer
            Get
                Return _selectedAnnotationIndex
            End Get
            Set(value As Integer)
                Dim clamped = If(value >= 0 AndAlso value < _annotations.Count, value, -1)
                If clamped = _selectedAnnotationIndex Then Return
                ' Ebenenwechsel beendet eine laufende Pinsel-/Radiergummi-Mal-Sitzung (siehe
                ' AddBrushStroke) - außer es ist genau die Ebene, die AddBrushStroke selbst gerade
                ' neu angelegt und ausgewählt hat.
                Dim newlySelected = If(clamped >= 0, _annotations(clamped), Nothing)
                If Not Object.ReferenceEquals(newlySelected, _activeStrokeAnnotation) Then
                    _activeStrokeAnnotation = Nothing
                End If
                _selectedAnnotationIndex = clamped
                If clamped >= 0 Then
                    ' Im Drehen-Werkzeug wird KEIN Platzierungstyp scharfgestellt: dort will man ein Objekt
                    ' drehen, nicht ein weiteres anlegen - der nächste Klick auf freie Fläche würde sonst
                    ' eines setzen.
                    PendingInsertKind = If(IsObjectTransformTool(_currentTool), "", PlacementKindForAnnotation(_annotations(clamped)))
                    Dim targetTool = AnnotationKindToTool(_annotations(clamped).Kind)
                    ' Im Drehen-Werkzeug NICHT ins Werkzeug des Objekts springen: dort markiert man ein Objekt,
                    ' um es zu drehen oder zu spiegeln. Ein Sprung nach „Text"/„Einfügen" würde einen Klick auf
                    ' das Objekt aussehen lassen, als hätte er gar nicht selektiert.
                    If IsObjectTransformTool(_currentTool) Then targetTool = _currentTool
                    If targetTool <> _currentTool Then
                        _overlayNotifySuppressDepth += 1
                        Try
                            CurrentTool = targetTool
                        Finally
                            _overlayNotifySuppressDepth -= 1
                        End Try
                    End If
                ElseIf Not String.IsNullOrEmpty(_pendingInsertKind) Then
                    ' Ohne Selektion beschreiben die Annotation*-Puffer das nächste zu platzierende
                    ' Objekt. Nach dem Abwählen stehen dort noch die Werte des eben abgewählten
                    ' Objekts - zurück auf die Vorgaben des scharfgestellten Typs, sonst erbt das
                    ' nächste Objekt dessen Farbe, Drehung, Verlauf, Schatten usw.
                    SeedAnnotationDefaultsForKind(_pendingInsertKind)
                End If
                ' Editor-Puffer (AnnotationXPercent usw.) VOR den PropertyChanged-Events unten befüllen:
                ' Diese lösen im View das Sichtbarwerden des Live-Overlays aus (UpdateTextOverlayVisibility),
                ' das Positionieren erfolgt aber separat über die Annotation*-Properties. Liefe
                ' LoadSelectedAnnotationIntoEditor erst danach, würde das Overlay kurz an der alten
                ' (Stale-)Position/Größe der vorherigen Selektion aufblitzen, bevor es korrigiert wird.
                LoadSelectedAnnotationIntoEditor()
                Me.RaisePropertyChanged(NameOf(SelectedAnnotationIndex))
                Me.RaisePropertyChanged(NameOf(HasSelectedAnnotation))
                Me.RaisePropertyChanged(NameOf(SelectedAnnotationKind))
                Me.RaisePropertyChanged(NameOf(CurrentToolLabel))
                Me.RaisePropertyChanged(NameOf(CurrentToolIconSource))
                Me.RaisePropertyChanged(NameOf(SelectedAnnotationText))
                Me.RaisePropertyChanged(NameOf(ShowAnnotationProperties))
                Me.RaisePropertyChanged(NameOf(EffectiveAnnotationKind))
                Me.RaisePropertyChanged(NameOf(ShowTextContentControls))
                Me.RaisePropertyChanged(NameOf(ShowFontControls))
                Me.RaisePropertyChanged(NameOf(ShowFillColorControls))
                Me.RaisePropertyChanged(NameOf(ShowFillColorPicker))
                Me.RaisePropertyChanged(NameOf(ShowStrokeColorPicker))
                Me.RaisePropertyChanged(NameOf(ShowWatermarkAnchorControls))
                Me.RaisePropertyChanged(NameOf(ShowFreeAnnotationPositionControls))
                Me.RaisePropertyChanged(NameOf(ShowGradientFillControls))
                Me.RaisePropertyChanged(NameOf(ShowLinearGradientAngleControl))
                Me.RaisePropertyChanged(NameOf(ShowRadialGradientControl))
                Me.RaisePropertyChanged(NameOf(ShowStrokeWidthControls))
                Me.RaisePropertyChanged(NameOf(FillColorLabel))
                Me.RaisePropertyChanged(NameOf(StrokeColorLabel))
                Me.RaisePropertyChanged(NameOf(AnnotationPositionMinimum))
                Me.RaisePropertyChanged(NameOf(AnnotationPositionMaximum))
                Me.RaisePropertyChanged(NameOf(AnnotationXLabel))
                Me.RaisePropertyChanged(NameOf(AnnotationYLabel))
                Me.RaisePropertyChanged(NameOf(SelectedAnnotationImagePath))
                UpdateSelectedAnnotationOverlayPreview()
                RaiseWatermarkUiChanged()
                UpdateShapeIconStates()
                ' Text-/Wasserzeichen-Ebenen werden während der Selektion über das Live-Overlay
                ' gerendert und dafür im gebackenen Vorschaubild ausgeblendet (siehe GetCurrentAdjustments).
                ' NotifyAnnotationOverlayStateChanged rendert beim Verlassen der Selektion sofort statt
                ' erst nach dem Debounce neu, damit das Objekt ohne sichtbaren Sprung/Verzögerung erscheint.
                RequestOverlayStateNotify()
            End Set
        End Property

        Private _pendingInsertKind As String = ""
        Public Property PendingInsertKind As String
            Get
                Return _pendingInsertKind
            End Get
            Set(value As String)
                Dim v = If(value, "")
                If v = _pendingInsertKind Then Return
                _pendingInsertKind = v
                ' Nur für ein noch zu platzierendes Objekt. Bei einer bestehenden Selektion zieht der
                ' SelectedAnnotationIndex-Setter den Objekttyp hier nach - dort dürfen die Startwerte
                ' nicht gesetzt werden: die Annotation*-Setter schreiben über SyncSelectedAnnotation
                ' sofort in das selektierte Objekt zurück und würden es mit den Typ-Vorgaben und den
                ' noch im Editorpuffer stehenden Werten des zuvor selektierten Objekts überschreiben.
                ' Die Puffer füllt für eine Selektion stattdessen LoadSelectedAnnotationIntoEditor.
                If Not String.IsNullOrEmpty(v) AndAlso Not HasSelectedAnnotation Then SeedAnnotationDefaultsForKind(v)
                Me.RaisePropertyChanged(NameOf(PendingInsertKind))
                Me.RaisePropertyChanged(NameOf(HasPendingInsertKind))
                ' Der Objekttyp benennt beim Ebenen-Werkzeug den ersten Tab (siehe InsertKindLabel).
                Me.RaisePropertyChanged(NameOf(CurrentToolLabel))
                Me.RaisePropertyChanged(NameOf(CurrentToolIconSource))
                Me.RaisePropertyChanged(NameOf(ShowAnnotationProperties))
                Me.RaisePropertyChanged(NameOf(EffectiveAnnotationKind))
                Me.RaisePropertyChanged(NameOf(ShowTextContentControls))
                Me.RaisePropertyChanged(NameOf(ShowFontControls))
                Me.RaisePropertyChanged(NameOf(ShowFillColorControls))
                Me.RaisePropertyChanged(NameOf(ShowFillColorPicker))
                Me.RaisePropertyChanged(NameOf(ShowStrokeColorPicker))
                Me.RaisePropertyChanged(NameOf(ShowWatermarkAnchorControls))
                Me.RaisePropertyChanged(NameOf(ShowFreeAnnotationPositionControls))
                Me.RaisePropertyChanged(NameOf(ShowGradientFillControls))
                Me.RaisePropertyChanged(NameOf(ShowLinearGradientAngleControl))
                Me.RaisePropertyChanged(NameOf(ShowRadialGradientControl))
                Me.RaisePropertyChanged(NameOf(ShowStrokeWidthControls))
                Me.RaisePropertyChanged(NameOf(FillColorLabel))
                Me.RaisePropertyChanged(NameOf(StrokeColorLabel))
                Me.RaisePropertyChanged(NameOf(AnnotationPositionMinimum))
                Me.RaisePropertyChanged(NameOf(AnnotationPositionMaximum))
                Me.RaisePropertyChanged(NameOf(AnnotationXLabel))
                Me.RaisePropertyChanged(NameOf(AnnotationYLabel))
                RaiseWatermarkUiChanged()
                UpdateShapeIconStates()
            End Set
        End Property

        Public ReadOnly Property HasPendingInsertKind As Boolean
            Get
                Return Not String.IsNullOrEmpty(_pendingInsertKind)
            End Get
        End Property

        ''' Eigenschaften-Panel ist sichtbar, sobald ein Objekttyp scharf gestellt ODER ein
        ''' vorhandenes Objekt selektiert ist - unabhängig davon, aus welcher Werkzeuggruppe
        ''' (Text, Malen, Formen &amp; Symbole) der Typ ausgewählt wurde.
        Public ReadOnly Property ShowAnnotationProperties As Boolean
            Get
                Return HasSelectedAnnotation OrElse HasPendingInsertKind
            End Get
        End Property

        ''' Der Objekttyp, dessen Eigenschaften gerade angezeigt werden: das selektierte Objekt,
        ''' sonst der scharf gestellte (aber noch nicht platzierte) Typ.
        Public ReadOnly Property EffectiveAnnotationKind As String
            Get
                If HasSelectedAnnotation Then Return SelectedAnnotationKind
                If HasPendingInsertKind Then Return NormalizeAnnotationKind(_pendingInsertKind)
                Return ""
            End Get
        End Property

        ''' Textinhalt-Feld nur bei Objekttypen mit editierbarem Text (Text/Wasserzeichen/QR-Inhalt/Symbol-Zeichen).
        Public ReadOnly Property ShowTextContentControls As Boolean
            Get
                Dim k = EffectiveAnnotationKind
                Return k = "Text" OrElse k = "QR" OrElse k = "Symbol" OrElse (k = "Watermark" AndAlso Not IsWatermarkImageSource)
            End Get
        End Property

        ''' Schrift/Größe nur dort relevant, wo tatsächlich Text gerendert wird.
        Public ReadOnly Property ShowFontControls As Boolean
            Get
                Dim k = EffectiveAnnotationKind
                Return k = "Text" OrElse (k = "Watermark" AndAlso Not IsWatermarkImageSource)
            End Get
        End Property

        ''' Füllung ergibt bei eingefügten Bildern keinen Sinn (DrawImageAnnotation zeichnet nur das
        ''' Bild selbst, keine Füllfarbe dahinter) - dort blendet das Eigenschaften-Panel den Regler aus.
        Public ReadOnly Property ShowFillColorControls As Boolean
            Get
                Return EffectiveAnnotationKind <> "Image" AndAlso Not IsWatermarkImageSource
            End Get
        End Property

        Public ReadOnly Property ShowGradientFillControls As Boolean
            Get
                Return ShowFillColorControls AndAlso Not String.Equals(_annotationFillKind, "Solid", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' Beim QR-Code stehen Hintergrund (Füllfarbe) und Vordergrund (Konturfarbe) im Farbmischer.
        ''' Die Farbfelder im Eigenschaften-Panel wären dann eine zweite Stelle für dieselben Werte.
        Public ReadOnly Property ShowFillColorPicker As Boolean
            Get
                Return ShowFillColorControls AndAlso EffectiveAnnotationKind <> "QR"
            End Get
        End Property

        Public ReadOnly Property ShowStrokeColorPicker As Boolean
            Get
                Return EffectiveAnnotationKind <> "QR"
            End Get
        End Property

        Public ReadOnly Property ShowLinearGradientAngleControl As Boolean
            Get
                Return ShowGradientFillControls AndAlso String.Equals(_annotationFillKind, "LinearGradient", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property ShowRadialGradientControl As Boolean
            Get
                Return ShowGradientFillControls AndAlso String.Equals(_annotationFillKind, "RadialGradient", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public Sub SetAnnotationFillKind(kind As String)
            AnnotationFillKind = kind
        End Sub

        ''' Live-Vorschau der aktuell konfigurierten Füllung (Vollfarbe/Verlauf) direkt auf dem
        ''' Auswahl-Overlay, BEVOR "Auswahl füllen" geklickt wird - besonders bei Verläufen wichtig,
        ''' da Winkel/Farbkombination sonst erst nach dem Anlegen des Objekts sichtbar wären.
        Public ReadOnly Property SelectionFillPreviewBrush As Avalonia.Media.IBrush
            Get
                Dim startColor = If(_annotationGradientInverted, AnnotationFillColor2Value, AnnotationFillColorValue)
                Dim endColor = If(_annotationGradientInverted, AnnotationFillColorValue, AnnotationFillColor2Value)
                Select Case If(_annotationFillKind, "Solid").Trim().ToLowerInvariant()
                    Case "lineargradient"
                        Dim angleRad = _annotationGradientAngle * Math.PI / 180.0
                        Dim dx = Math.Cos(angleRad) * 0.5
                        Dim dy = Math.Sin(angleRad) * 0.5
                        Dim brush As New Avalonia.Media.LinearGradientBrush With {
                            .StartPoint = New Avalonia.RelativePoint(0.5 - dx, 0.5 - dy, Avalonia.RelativeUnit.Relative),
                            .EndPoint = New Avalonia.RelativePoint(0.5 + dx, 0.5 + dy, Avalonia.RelativeUnit.Relative)
                        }
                        brush.GradientStops.Add(New Avalonia.Media.GradientStop(startColor, 0))
                        brush.GradientStops.Add(New Avalonia.Media.GradientStop(endColor, 1))
                        Return brush
                    Case "radialgradient"
                        Dim brush As New Avalonia.Media.RadialGradientBrush With {
                            .Center = New Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative),
                            .GradientOrigin = New Avalonia.RelativePoint(0.5, 0.5, Avalonia.RelativeUnit.Relative),
                            .RadiusX = New Avalonia.RelativeScalar(0.5, Avalonia.RelativeUnit.Relative),
                            .RadiusY = New Avalonia.RelativeScalar(0.5, Avalonia.RelativeUnit.Relative)
                        }
                        brush.GradientStops.Add(New Avalonia.Media.GradientStop(startColor, 0))
                        brush.GradientStops.Add(New Avalonia.Media.GradientStop(endColor, 1))
                        Return brush
                    Case Else
                        Return New Avalonia.Media.SolidColorBrush(AnnotationFillColorValue)
                End Select
            End Get
        End Property

        ''' DrawQrCode (siehe ImageProcessor) zeichnet die Module immer randlos in voller Zellgröße
        ''' und bekommt gar keine Konturbreite übergeben - der Regler hätte beim QR-Code also nie
        ''' einen sichtbaren Effekt.
        Public ReadOnly Property ShowStrokeWidthControls As Boolean
            Get
                Return EffectiveAnnotationKind <> "QR"
            End Get
        End Property

        ''' Beim QR-Code ist FillColor die Hintergrundfarbe (StrokeColor die Modulfarbe, siehe
        ''' ApplyAnnotations) - das Eigenschaften-Panel beschriftet den Regler entsprechend um.
        Public ReadOnly Property FillColorLabel As String
            Get
                Return If(EffectiveAnnotationKind = "QR", "Hintergrund", "Füllung")
            End Get
        End Property

        ''' Beim QR-Code ist StrokeColor die Modulfarbe (siehe ApplyAnnotations) - "Kontur" wäre hier
        ''' irreführend, da nichts umrandet wird. "Vordergrund" spiegelt das Gegenstück zu "Hintergrund".
        Public ReadOnly Property StrokeColorLabel As String
            Get
                Return If(EffectiveAnnotationKind = "QR", "Vordergrund", "Kontur")
            End Get
        End Property

        ''' Setzt beim Scharfstellen eines Objekttyps sinnvolle Startwerte für Füllung/Kontur/
        ''' Konturbreite/Text ins Eigenschaften-Panel, damit man sie schon vor dem Platzieren sieht
        ''' und bearbeiten kann. AddAnnotationAt übernimmt beim Platzieren genau diese (ggf. vom
        ''' Nutzer bereits angepassten) Werte 1:1.
        Private Sub SeedAnnotationDefaultsForKind(rawKind As String)
            Dim normalizedKind = NormalizeAnnotationKind(rawKind)
            Dim isShape = IsCustomShapeKind(normalizedKind)
            AnnotationFillColor = If(normalizedKind = "Image", "#00FFFFFF",
                                   If(normalizedKind = "Rectangle" OrElse normalizedKind = "Ellipse" OrElse isShape, "#33FFFFFF", "#FFFFFFFF"))
            AnnotationStrokeColor = "#FF000000"
            AnnotationStrokeWidth = If(normalizedKind = "Text" OrElse normalizedKind = "Watermark" OrElse normalizedKind = "QR" OrElse normalizedKind = "Image", 0, 2)
            AnnotationText = GetDefaultAnnotationText(normalizedKind, rawKind)
            AnnotationFontSize = 48
            AnnotationFontFamily = "Arial"
            AnnotationOpacity = 100
            AnnotationRotation = 0
            AnnotationFlipHorizontal = False
            AnnotationFlipVertical = False
            Dim defaultSize = GetDefaultAnnotationSizePercent(normalizedKind, rawKind)
            _annotationWidthPercent = defaultSize.WidthPercent
            _annotationHeightPercent = defaultSize.HeightPercent
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
            _annotationAnchor = "BottomRight"
            Me.RaisePropertyChanged(NameOf(AnnotationAnchor))
            AnnotationIsVisible = True
            ' Füllart, Schatten und Leuchten gehören genauso zum Startzustand wie Farbe und Größe:
            ' ohne Rücksetzen erbt das nächste platzierte Objekt sie aus dem Puffer des zuvor
            ' selektierten (die Werte sind dieselben wie in ResetEditorUiStateForNewImage).
            AnnotationFillKind = "Solid"
            AnnotationFillColor2 = "#FFFFFFFF"
            AnnotationGradientAngleDegrees = 0
            AnnotationGradientInverted = False
            AnnotationShadowEnabled = False
            AnnotationShadowOffsetX = 2
            AnnotationShadowOffsetY = 2
            Me.RaisePropertyChanged(NameOf(AnnotationShadowLightAngle))
            AnnotationShadowBlur = 6
            AnnotationShadowStrength = 100
            AnnotationShadowColor = "#80000000"
            AnnotationShadowRounded = False
            AnnotationShadowCornerRadius = 20
            AnnotationShadowSize = 100
            AnnotationGlowEnabled = False
            AnnotationGlowBlur = 10
            AnnotationGlowStrength = 100
            AnnotationGlowColor = "#FFFFFF00"
            If normalizedKind = "Watermark" Then
                _annotationXPercent = 4
                _annotationYPercent = 4
                Me.RaisePropertyChanged(NameOf(AnnotationXPercent))
                Me.RaisePropertyChanged(NameOf(AnnotationYPercent))
                ClearWatermarkImagePath()
                _selectedWatermarkPresetName = ""
                _watermarkPresetNameDraft = ""
                Me.RaisePropertyChanged(NameOf(SelectedWatermarkPresetName))
                Me.RaisePropertyChanged(NameOf(WatermarkPresetNameDraft))
            End If
            If normalizedKind = "Text" OrElse (normalizedKind = "Watermark" AndAlso Not IsWatermarkImageSource) Then
                UpdatePendingTextAnnotationSize()
            End If
        End Sub

        Private Sub UpdateShapeIconStates()
            Dim selectedImagePath = If(_selectedAnnotationIndex >= 0 AndAlso _selectedAnnotationIndex < _annotations.Count AndAlso
                                        String.Equals(_annotations(_selectedAnnotationIndex).Kind, "Svg", StringComparison.OrdinalIgnoreCase),
                                        _annotations(_selectedAnnotationIndex).ImagePath, "")
            For Each item In _allShapeIcons
                item.IsPending = String.Equals(item.PendingKind, _pendingInsertKind, StringComparison.OrdinalIgnoreCase)
                item.IsSelectedKind = Not String.IsNullOrEmpty(selectedImagePath) AndAlso String.Equals(item.IconPath, selectedImagePath, StringComparison.OrdinalIgnoreCase)
            Next
        End Sub

        Public ReadOnly Property HasSelectedAnnotation As Boolean
            Get
                Return _selectedAnnotationIndex >= 0
            End Get
        End Property

        Public ReadOnly Property SelectedAnnotationKind As String
            Get
                If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return ""
                Return If(_annotations(_selectedAnnotationIndex)?.Kind, "")
            End Get
        End Property

        Public ReadOnly Property SelectedAnnotationText As String
            Get
                If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return ""
                Return If(_annotations(_selectedAnnotationIndex)?.Text, "")
            End Get
        End Property

        Public ReadOnly Property SelectedPaintMode As String
            Get
                If _currentTool = EditorTool.Retouch Then Return If(_isCloneMode, "Clone", "Blur")
                If _currentTool = EditorTool.Draw AndAlso _isEraserMode Then Return "Eraser"
                If _currentTool = EditorTool.Draw Then Return "Brush"
                Return ""
            End Get
        End Property

        ''' True, wenn das Stempel-Werkzeug aktiv ist (klont von einer Quelle) statt des
        ''' Verwischen-Werkzeugs (mittelt die Umgebung).
        Public ReadOnly Property IsCloneMode As Boolean
            Get
                Return _currentTool = EditorTool.Retouch AndAlso _isCloneMode
            End Get
        End Property


        Public ReadOnly Property IsGeometryToolSelected As Boolean
            Get
                Return _currentTool = EditorTool.Geometry OrElse _currentTool = EditorTool.Insert
            End Get
        End Property

        Public ReadOnly Property CurrentFilmstripIndex As Integer
            Get
                Return _currentIndex
            End Get
        End Property

        Public ReadOnly Property PositionText As String
            Get
                If _folderPaths.Count = 0 Then Return ""
                Return $"{_currentIndex + 1} / {_folderPaths.Count}"
            End Get
        End Property

        Public ReadOnly Property ShowFilmstrip As Boolean
            Get
                Return _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing AndAlso _mainVm.Settings.EditorShowFilmstrip
            End Get
        End Property

        ''' Kantenlänge einer Rasterzelle in Bildpixeln (Einstellungen -> Editor).
        Public ReadOnly Property EditorGridSize As Integer
            Get
                If _mainVm Is Nothing OrElse _mainVm.Settings Is Nothing Then Return 50
                Return _mainVm.Settings.EditorGridSize
            End Get
        End Property

        Public Property EditorShowRulers As Boolean
            Get
                If _mainVm Is Nothing OrElse _mainVm.Settings Is Nothing Then Return False
                Return _mainVm.Settings.EditorShowRulers
            End Get
            Set(value As Boolean)
                If _mainVm Is Nothing OrElse _mainVm.Settings Is Nothing Then Return
                _mainVm.Settings.EditorShowRulers = value
                Me.RaisePropertyChanged(NameOf(EditorShowRulers))
            End Set
        End Property

        Public Property EditorShowGrid As Boolean
            Get
                If _mainVm Is Nothing OrElse _mainVm.Settings Is Nothing Then Return False
                Return _mainVm.Settings.EditorShowGrid
            End Get
            Set(value As Boolean)
                If _mainVm Is Nothing OrElse _mainVm.Settings Is Nothing Then Return
                _mainVm.Settings.EditorShowGrid = value
                Me.RaisePropertyChanged(NameOf(EditorShowGrid))
            End Set
        End Property

        Public ReadOnly Property IsInfoSidebarVisible As Boolean
            Get
                Return _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing AndAlso _mainVm.Settings.EditorInfoSidebarExpanded
            End Get
        End Property

        Public Property CurrentImagePath As String
            Get
                Return _currentImagePath
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_currentImagePath, value)
                Me.RaisePropertyChanged(NameOf(CurrentFileName))
                Me.RaisePropertyChanged(NameOf(IsCurrentImageRaw))
                Me.RaisePropertyChanged(NameOf(CanSaveInPlace))
                Me.RaisePropertyChanged(NameOf(TransparencyBackgroundBrush))
            End Set
        End Property

        ''' Löst das alte Bitmap erst einen Dispatcher-Tick später auf (statt im selben Aufruf), da
        ''' AfterImageControl/BeforeImageControl (siehe EditorView.axaml) dieselbe Quelle noch
        ''' kurz zum Kompositieren/Rendern brauchen können, nachdem die Bindung bereits auf das
        ''' neue Bild umgestellt wurde - ein sofortiges Dispose direkt im Property-Setter lief dem
        ''' bei sehr häufigen Vorschau-Wechseln (Live-Regler) gelegentlich davon.
        Private Shared Sub DisposeDeferred(bitmap As Bitmap)
            If bitmap Is Nothing Then Return
            Dispatcher.UIThread.Post(Sub() bitmap.Dispose(), DispatcherPriority.Background)
        End Sub

        Public Property CurrentImage As Bitmap
            Get
                Return _currentImage
            End Get
            Set(value As Bitmap)
                Dim previous = _currentImage
                Me.RaiseAndSetIfChanged(_currentImage, value)
                Me.RaisePropertyChanged(NameOf(DisplayImage))
                Me.RaisePropertyChanged(NameOf(BeforeDisplayImage))
                RaiseCropPropertiesChanged()
                If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, value) Then DisposeDeferred(previous)
            End Set
        End Property

        Public Property PreviewImage As Bitmap
            Get
                Return _previewImage
            End Get
            Set(value As Bitmap)
                Dim previous = _previewImage
                Me.RaiseAndSetIfChanged(_previewImage, value)
                Me.RaisePropertyChanged(NameOf(DisplayImage))
                If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, value) Then DisposeDeferred(previous)
            End Set
        End Property

        Public Property ComparisonImage As Bitmap
            Get
                Return _comparisonImage
            End Get
            Set(value As Bitmap)
                Dim previous = _comparisonImage
                Me.RaiseAndSetIfChanged(_comparisonImage, value)
                Me.RaisePropertyChanged(NameOf(BeforeDisplayImage))
                If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, value) Then DisposeDeferred(previous)
            End Set
        End Property

        Public ReadOnly Property DisplayImage As Bitmap
            Get
                Return If(_previewImage, _currentImage)
            End Get
        End Property

        Public ReadOnly Property BeforeDisplayImage As Bitmap
            Get
                Return If(_comparisonImage, _currentImage)
            End Get
        End Property

        ''' Hintergrund hinter transparenten Bildbereichen (Schachbrettmuster oder Volltonfarbe je
        ''' nach Einstellung) - wird bei Änderung in den Settings über
        ''' MainWindowViewModel.RefreshDisplayBindings aktualisiert.
        Public ReadOnly Property TransparencyBackgroundBrush As Avalonia.Media.IBrush
            Get
                ' Formate ohne Alphakanal-Unterstützung (z.B. JPEG) können strukturell nie
                ' transparente Bereiche haben - Schachbrett/Volltonfarbe wäre dort nur an
                ' Letterbox-/Rundungsrändern fälschlich sichtbar, nie inhaltlich sinnvoll.
                If Not TransparencyBrushService.HasVisibleTransparency(_currentImagePath) Then
                    Return Avalonia.Media.Brushes.Transparent
                End If
                Dim settings = AppSettingsService.Load()
                Return TransparencyBrushService.GetBrush(settings.TransparencyBackgroundMode, settings.TransparencyBackgroundColor)
            End Get
        End Property

        Public Property ShowBeforeImage As Boolean
            Get
                Return _showBeforeImage
            End Get
            Set(value As Boolean)
                If value AndAlso Not CanShowBeforeAfter Then value = False
                Dim wasShowing = _showBeforeImage
                Me.RaiseAndSetIfChanged(_showBeforeImage, value)
                Me.RaisePropertyChanged(NameOf(DisplayImage))
                ' Das Vergleichsbild wird nur berechnet, während ShowBeforeImage sichtbar ist (siehe
                ' UpdatePreviewAsync) - beim Einschalten deshalb einmalig frisch nachrendern, damit
                ' nicht kurz ein veralteter Stand (oder gar kein Bild) zu sehen ist.
                If value AndAlso Not wasShowing Then SchedulePreviewUpdate()
            End Set
        End Property

        Public Sub SetComparisonVisibleFromUser(value As Boolean)
            _comparisonAutoEnabled = value
            ShowBeforeImage = value
        End Sub

        ''' Der Vergleichsregler liegt über der Leinwand und fängt Klicks ab. Werkzeuge, die auf der
        ''' Leinwand selbst arbeiten - malen, retuschieren, Objekte setzen, Bereiche aufziehen - können ihn
        ''' deshalb nicht gebrauchen. Verwischen und Stempel (EditorTool.Retouch) gehören dazu.
        Public ReadOnly Property CanShowBeforeAfter As Boolean
            Get
                Return _currentTool <> EditorTool.Crop AndAlso
                       _currentTool <> EditorTool.Resize AndAlso
                       _currentTool <> EditorTool.Rotate AndAlso
                       _currentTool <> EditorTool.Transform AndAlso
                       _currentTool <> EditorTool.Selection AndAlso
                       _currentTool <> EditorTool.Text AndAlso
                       _currentTool <> EditorTool.Draw AndAlso
                       _currentTool <> EditorTool.Retouch AndAlso
                       _currentTool <> EditorTool.Geometry AndAlso
                       _currentTool <> EditorTool.Insert
            End Get
        End Property

        Public Property CurrentTool As EditorTool
            Get
                Return _currentTool
            End Get
            Set(value As EditorTool)
                Dim previousTool = _currentTool
                Me.RaiseAndSetIfChanged(_currentTool, value)
                If previousTool <> value Then
                    DiscardUncommittedToolEdits(previousTool)
                    ' Werkzeugwechsel beendet eine laufende Pinsel-/Radiergummi-Mal-Sitzung (siehe
                    ' AddBrushStroke) - auch zwischen zwei Ebenen-Werkzeugen (Draw -> Text usw.), wo
                    ' SelectedAnnotationIndex sonst unverändert bliebe.
                    _activeStrokeAnnotation = Nothing
                End If
                If Not IsLayerTool(value) Then SelectedAnnotationIndex = -1
                If Not CanShowBeforeAfter AndAlso _showBeforeImage Then
                    _showBeforeImage = False
                    Me.RaisePropertyChanged(NameOf(ShowBeforeImage))
                    Me.RaisePropertyChanged(NameOf(DisplayImage))
                End If
                Me.RaisePropertyChanged(NameOf(CanShowBeforeAfter))
                If CanShowBeforeAfter AndAlso _comparisonAutoEnabled AndAlso Not _showBeforeImage Then
                    ShowBeforeImage = True
                End If
                Me.RaisePropertyChanged(NameOf(CurrentToolLabel))
                Me.RaisePropertyChanged(NameOf(CurrentToolIconSource))
                RaiseToolContextProperties()
                RequestOverlayStateNotify()
            End Set
        End Property

        ''' <summary>
        ''' Beschriftet den ersten Tab des rechten Panels. Die Namen sind wörtlich die der
        ''' Werkzeugleiste - dort steht "Details" für EditorTool.Effects und "Effekte und Rahmen" für
        ''' EditorTool.Frame, nicht umgekehrt.
        ''' </summary>
        Public ReadOnly Property CurrentToolLabel As String
            Get
                Select Case _currentTool
                    Case EditorTool.Crop : Return "Zuschneiden"
                    Case EditorTool.Resize : Return "Bildgröße"
                    Case EditorTool.Rotate : Return "Drehen"
                    Case EditorTool.Adjust : Return "Anpassen"
                    Case EditorTool.Color : Return "Farbe"
                    Case EditorTool.Effects : Return "Details"
                    Case EditorTool.Frame : Return "Effekte und Rahmen"
                    Case EditorTool.Filters : Return "Filter"
                    Case EditorTool.Transform : Return "Transformieren"
                    Case EditorTool.Selection : Return "Auswahl"
                    Case EditorTool.Retouch : Return If(_isCloneMode, "Stempel", "Verwischen")
                    Case EditorTool.Draw : Return If(_isEraserMode, "Radiergummi", "Pinsel")
                    Case EditorTool.Geometry, EditorTool.Insert : Return "Formen und Symbole"
                    Case EditorTool.Text : Return InsertKindLabel()
                    Case Else : Return "Werkzeug"
                End Select
            End Get
        End Property

        ''' <summary>Das Symbol des aktiven Werkzeugs, damit die Gruppenüberschrift im rechten Panel
        ''' dasselbe zeigt wie sein Eintrag in der Werkzeugleiste.</summary>
        Public ReadOnly Property CurrentToolIconSource As String
            Get
                Const base As String = "avares://FerrumPix/Assets/Icons/outline/"
                Select Case _currentTool
                    Case EditorTool.Selection : Return base & "rectangle.svg"
                    Case EditorTool.Retouch : Return base & If(_isCloneMode, "rubber-stamp.svg", "blur.svg")
                    Case EditorTool.Draw : Return base & If(_isEraserMode, "eraser.svg", "brush.svg")
                    Case EditorTool.Geometry, EditorTool.Insert : Return base & "cube.svg"
                    Case EditorTool.Text
                        Select Case NormalizeAnnotationKind(If(String.IsNullOrEmpty(_pendingInsertKind), SelectedAnnotationKind, _pendingInsertKind))
                            Case "Image" : Return base & "photo.svg"
                            Case "QR" : Return base & "qrcode.svg"
                            Case "Watermark" : Return base & "copyright.svg"
                            Case Else : Return base & "text-size.svg"
                        End Select
                    Case Else : Return base & "square-rounded-plus.svg"
                End Select
            End Get
        End Property

        ''' Das Ebenen-Werkzeug bedient fünf Einträge der Werkzeugleiste. Welcher gemeint ist, sagt der
        ''' scharfgestellte Objekttyp - und nach dem Platzieren die Art des ausgewählten Objekts.
        Private Function InsertKindLabel() As String
            ' Vor dem Normalisieren prüfen: NormalizeAnnotationKind("") liefert "Text", was hier
            ' fälschlich das Text-Werkzeug anzeigen würde, obwohl gar nichts gewählt ist.
            Dim raw = If(String.IsNullOrEmpty(_pendingInsertKind), SelectedAnnotationKind, _pendingInsertKind)
            If String.IsNullOrWhiteSpace(raw) Then Return "Einfügen"

            Select Case NormalizeAnnotationKind(raw)
                Case "Image" : Return "Bild"
                Case "QR" : Return "QR-Code"
                Case "Watermark" : Return "Wasserzeichen"
                Case "Text" : Return "Text"
                Case Else
                    ' Formen, Symbole, Linien und Pfeile werden ebenfalls über dieses Werkzeug gesetzt.
                    Return "Formen und Symbole"
            End Select
        End Function

        Public ReadOnly Property ShowCropAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Crop
            End Get
        End Property

        Public ReadOnly Property ShowResizeAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Resize
            End Get
        End Property

        Public ReadOnly Property ShowLightAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Adjust
            End Get
        End Property

        Public ReadOnly Property ShowColorAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Color
            End Get
        End Property

        Public ReadOnly Property ShowDetailAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Effects
            End Get
        End Property

        Public ReadOnly Property ShowFrameAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Frame
            End Get
        End Property

        Public ReadOnly Property ShowFilterAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Filters
            End Get
        End Property

        Public ReadOnly Property ShowRetouchAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Retouch
            End Get
        End Property

        Public ReadOnly Property ShowSelectionAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Selection
            End Get
        End Property

        Public ReadOnly Property ShowDrawControls As Boolean
            Get
                Return _currentTool = EditorTool.Draw OrElse _currentTool = EditorTool.Retouch
            End Get
        End Property

        Public ReadOnly Property ShowLayerToolOptions As Boolean
            Get
                Return _currentTool = EditorTool.Text OrElse _currentTool = EditorTool.Geometry OrElse _currentTool = EditorTool.Insert
            End Get
        End Property


        Public ReadOnly Property ShowGeometryControls As Boolean
            Get
                Return _currentTool = EditorTool.Geometry OrElse _currentTool = EditorTool.Insert
            End Get
        End Property

        Public ReadOnly Property ShowTransformAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Rotate OrElse _currentTool = EditorTool.Transform
            End Get
        End Property

        Public Property Brightness As Double
            Get
                Return _brightness
            End Get
            Set(value As Double)
                SetUndoableDouble(_brightness, value, NameOf(Brightness))
            End Set
        End Property

        Public Property Contrast As Double
            Get
                Return _contrast
            End Get
            Set(value As Double)
                SetUndoableDouble(_contrast, value, NameOf(Contrast))
            End Set
        End Property

        Public Property Saturation As Double
            Get
                Return _saturation
            End Get
            Set(value As Double)
                SetUndoableDouble(_saturation, value, NameOf(Saturation))
            End Set
        End Property

        Public Property Highlights As Double
            Get
                Return _highlights
            End Get
            Set(value As Double)
                SetUndoableDouble(_highlights, value, NameOf(Highlights))
            End Set
        End Property

        Public Property ShadowsLevel As Double
            Get
                Return _shadowsLevel
            End Get
            Set(value As Double)
                SetUndoableDouble(_shadowsLevel, value, NameOf(ShadowsLevel))
            End Set
        End Property

        Public Property Whites As Double
            Get
                Return _whites
            End Get
            Set(value As Double)
                SetUndoableDouble(_whites, value, NameOf(Whites))
            End Set
        End Property

        Public Property Blacks As Double
            Get
                Return _blacks
            End Get
            Set(value As Double)
                SetUndoableDouble(_blacks, value, NameOf(Blacks))
            End Set
        End Property

        Public Property Temperature As Double
            Get
                Return _temperature
            End Get
            Set(value As Double)
                SetUndoableDouble(_temperature, value, NameOf(Temperature))
            End Set
        End Property

        Public Property Tint As Double
            Get
                Return _tint
            End Get
            Set(value As Double)
                SetUndoableDouble(_tint, value, NameOf(Tint))
            End Set
        End Property

        ''' Filmnegativ umkehren. Beim Einschalten wird das Bild einmal vermessen (Filmbasis und
        ''' dichtester Punkt), damit die Umkehr sofort mit echten Werten rechnet statt zu raten.
        Public Property NegativeEnabled As Boolean
            Get
                Return _negativeEnabled
            End Get
            Set(value As Boolean)
                If _negativeEnabled = value Then Return
                CaptureUndoState(NameOf(NegativeEnabled))
                _negativeEnabled = value
                If value Then MeasureFilmNegative()
                RaiseNegativePropertiesChanged()
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property NegativeMonochrome As Boolean
            Get
                Return _negativeMonochrome
            End Get
            Set(value As Boolean)
                If _negativeMonochrome = value Then Return
                CaptureUndoState(NameOf(NegativeMonochrome))
                _negativeMonochrome = value
                RaiseNegativePropertiesChanged()
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property NegativeGamma As Double
            Get
                Return _negativeGamma
            End Get
            Set(value As Double)
                SetUndoableDouble(_negativeGamma, value, NameOf(NegativeGamma))
            End Set
        End Property

        ''' Gemessene bzw. aufgenommene Filmbasis als Farbfeld neben der Pipette - ohne diese Rückmeldung
        ''' wäre für den Nutzer nicht erkennbar, WORAUF die Umkehr sich gerade bezieht.
        Public ReadOnly Property NegativeBaseBrush As Avalonia.Media.IBrush
            Get
                If String.IsNullOrWhiteSpace(_negativeBaseColor) Then
                    Return Avalonia.Media.Brushes.Transparent
                End If
                Try
                    Return New Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(_negativeBaseColor))
                Catch
                    Return Avalonia.Media.Brushes.Transparent
                End Try
            End Get
        End Property

        Public ReadOnly Property HasNegativeBaseColor As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(_negativeBaseColor)
            End Get
        End Property

        Public Property SplitToningShadowHue As Double
            Get
                Return _splitToningShadowHue
            End Get
            Set(value As Double)
                SetUndoableDouble(_splitToningShadowHue, Math.Max(0, Math.Min(360, value)), NameOf(SplitToningShadowHue))
            End Set
        End Property

        Public Property SplitToningShadowSaturation As Double
            Get
                Return _splitToningShadowSaturation
            End Get
            Set(value As Double)
                SetUndoableDouble(_splitToningShadowSaturation, Math.Max(0, Math.Min(100, value)), NameOf(SplitToningShadowSaturation))
            End Set
        End Property

        Public Property SplitToningHighlightHue As Double
            Get
                Return _splitToningHighlightHue
            End Get
            Set(value As Double)
                SetUndoableDouble(_splitToningHighlightHue, Math.Max(0, Math.Min(360, value)), NameOf(SplitToningHighlightHue))
            End Set
        End Property

        Public Property SplitToningHighlightSaturation As Double
            Get
                Return _splitToningHighlightSaturation
            End Get
            Set(value As Double)
                SetUndoableDouble(_splitToningHighlightSaturation, Math.Max(0, Math.Min(100, value)), NameOf(SplitToningHighlightSaturation))
            End Set
        End Property

        Public Property SplitToningBalance As Double
            Get
                Return _splitToningBalance
            End Get
            Set(value As Double)
                SetUndoableDouble(_splitToningBalance, Math.Max(-100, Math.Min(100, value)), NameOf(SplitToningBalance))
            End Set
        End Property

        Public Property Exposure As Double
            Get
                Return _exposure
            End Get
            Set(value As Double)
                SetUndoableDouble(_exposure, value, NameOf(Exposure))
            End Set
        End Property

        Public Property Sharpness As Double
            Get
                Return _sharpness
            End Get
            Set(value As Double)
                SetUndoableDouble(_sharpness, value, NameOf(Sharpness))
            End Set
        End Property

        Public ReadOnly Property NoiseReductionMethodOptions As IReadOnlyList(Of String)
            Get
                Return New String() {"Gaussian", "Median"}
            End Get
        End Property

        Public Property NoiseReductionMethodLabel As String
            Get
                Select Case _noiseReductionMethod
                    Case NoiseReductionMethod.Median
                        Return "Median"
                    Case Else
                        Return "Gaussian"
                End Select
            End Get
            Set(value As String)
                Dim method = If(String.Equals(value, "Median", StringComparison.OrdinalIgnoreCase), NoiseReductionMethod.Median, NoiseReductionMethod.Gaussian)
                If _noiseReductionMethod = method Then Return
                CaptureUndoState(NameOf(NoiseReductionMethodLabel))
                Me.RaiseAndSetIfChanged(_noiseReductionMethod, method)
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property NoiseReduction As Double
            Get
                Return _noiseReduction
            End Get
            Set(value As Double)
                SetUndoableDouble(_noiseReduction, value, NameOf(NoiseReduction))
            End Set
        End Property

        Public Property DustScratches As Double
            Get
                Return _dustScratches
            End Get
            Set(value As Double)
                SetUndoableDouble(_dustScratches, Math.Max(-100, Math.Min(100, value)), NameOf(DustScratches))
            End Set
        End Property

        Public Property Haze As Double
            Get
                Return _haze
            End Get
            Set(value As Double)
                SetUndoableDouble(_haze, Math.Max(-100, Math.Min(100, value)), NameOf(Haze))
            End Set
        End Property

        Public Property AddNoise As Double
            Get
                Return _addNoise
            End Get
            Set(value As Double)
                SetUndoableDouble(_addNoise, Math.Max(0, Math.Min(100, value)), NameOf(AddNoise))
            End Set
        End Property

        Public Property [Structure] As Double
            Get
                Return _structure
            End Get
            Set(value As Double)
                SetUndoableDouble(_structure, Math.Max(-100, Math.Min(100, value)), NameOf([Structure]))
            End Set
        End Property

        Public Property Glow As Double
            Get
                Return _glow
            End Get
            Set(value As Double)
                SetUndoableDouble(_glow, Math.Max(-100, Math.Min(100, value)), NameOf(Glow))
            End Set
        End Property

        Public Property Vibrance As Double
            Get
                Return _vibrance
            End Get
            Set(value As Double)
                SetUndoableDouble(_vibrance, value, NameOf(Vibrance))
            End Set
        End Property

        Public Property Vignette As Double
            Get
                Return _vignette
            End Get
            Set(value As Double)
                SetUndoableDouble(_vignette, value, NameOf(Vignette))
            End Set
        End Property

        Public Property VignetteTransition As Double
            Get
                Return _vignetteTransition
            End Get
            Set(value As Double)
                SetUndoableDouble(_vignetteTransition, Math.Max(0, Math.Min(100, value)), NameOf(VignetteTransition))
            End Set
        End Property

        Public Property VignetteRoundness As Double
            Get
                Return _vignetteRoundness
            End Get
            Set(value As Double)
                SetUndoableDouble(_vignetteRoundness, Math.Max(-100, Math.Min(100, value)), NameOf(VignetteRoundness))
            End Set
        End Property

        Public Property VignetteFeather As Double
            Get
                Return _vignetteFeather
            End Get
            Set(value As Double)
                SetUndoableDouble(_vignetteFeather, Math.Max(0, Math.Min(100, value)), NameOf(VignetteFeather))
            End Set
        End Property

        Public Property VignetteCenterX As Double
            Get
                Return _vignetteCenterX
            End Get
            Set(value As Double)
                SetUndoableDouble(_vignetteCenterX, Math.Max(0, Math.Min(100, value)), NameOf(VignetteCenterX))
            End Set
        End Property

        Public Property VignetteCenterY As Double
            Get
                Return _vignetteCenterY
            End Get
            Set(value As Double)
                SetUndoableDouble(_vignetteCenterY, Math.Max(0, Math.Min(100, value)), NameOf(VignetteCenterY))
            End Set
        End Property

        Public Property Grain As Double
            Get
                Return _grain
            End Get
            Set(value As Double)
                SetUndoableDouble(_grain, Math.Max(0, Math.Min(100, value)), NameOf(Grain))
            End Set
        End Property

        Public Property BorderSize As Double
            Get
                Return _borderSize
            End Get
            Set(value As Double)
                SetUndoableDouble(_borderSize, Math.Max(0, Math.Min(25, value)), NameOf(BorderSize))
            End Set
        End Property

        Public Property BorderCornerRadius As Double
            Get
                Return _borderCornerRadius
            End Get
            Set(value As Double)
                SetUndoableDouble(_borderCornerRadius, Math.Max(0, Math.Min(100, value)), NameOf(BorderCornerRadius))
            End Set
        End Property

        Public ReadOnly Property BorderEffectOptions As IReadOnlyList(Of String)
            Get
                Return New String() {"Einfach", "Gestrichelt", "Gezackt", "Doppelt", "Punktiert", "Wellig"}
            End Get
        End Property

        Public Property BorderEffect As String
            Get
                Return _borderEffect
            End Get
            Set(value As String)
                Dim normalized = If(String.IsNullOrWhiteSpace(value), "Einfach", value)
                If String.Equals(_borderEffect, normalized, StringComparison.Ordinal) Then Return
                CaptureUndoState(NameOf(BorderEffect))
                Me.RaiseAndSetIfChanged(_borderEffect, normalized)
                RaiseResetButtonStateChanged()
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property BorderColor As String
            Get
                Return _borderColor
            End Get
            Set(value As String)
                Dim normalized = NormalizeAvaloniaColor(value, "#FFFFFFFF")
                If String.Equals(_borderColor, normalized, StringComparison.Ordinal) Then Return
                CaptureUndoState(NameOf(BorderColor))
                Me.RaiseAndSetIfChanged(_borderColor, normalized)
                Me.RaisePropertyChanged(NameOf(BorderColorValue))
                Me.RaisePropertyChanged(NameOf(BorderColorBrush))
                RaiseResetButtonStateChanged()
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property BorderColorValue As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_borderColor, Avalonia.Media.Colors.White)
            End Get
            Set(value As Avalonia.Media.Color)
                BorderColor = value.ToString()
            End Set
        End Property

        Public Property Clarity As Double
            Get
                Return _clarity
            End Get
            Set(value As Double)
                SetUndoableDouble(_clarity, value, NameOf(Clarity))
            End Set
        End Property

        Public Property SelectedCurveChannel As CurveChannel
            Get
                Return _selectedCurveChannel
            End Get
            Set(value As CurveChannel)
                If _selectedCurveChannel = value Then Return
                Me.RaiseAndSetIfChanged(_selectedCurveChannel, value)
                Me.RaisePropertyChanged(NameOf(ActiveCurvePoints))
                Me.RaisePropertyChanged(NameOf(ActiveCurveHistogramCounts))
                Me.RaisePropertyChanged(NameOf(ActiveCurveBrush))
                RaiseCurveChannelStateChanged()
            End Set
        End Property

        Public ReadOnly Property IsCurveChannelRgb As Boolean
            Get
                Return _selectedCurveChannel = CurveChannel.Rgb
            End Get
        End Property

        Public ReadOnly Property IsCurveChannelRed As Boolean
            Get
                Return _selectedCurveChannel = CurveChannel.Red
            End Get
        End Property

        Public ReadOnly Property IsCurveChannelGreen As Boolean
            Get
                Return _selectedCurveChannel = CurveChannel.Green
            End Get
        End Property

        Public ReadOnly Property IsCurveChannelBlue As Boolean
            Get
                Return _selectedCurveChannel = CurveChannel.Blue
            End Get
        End Property

        Public ReadOnly Property IsCurveChannelLuminance As Boolean
            Get
                Return _selectedCurveChannel = CurveChannel.Luminance
            End Get
        End Property

        Public ReadOnly Property ActiveCurvePoints As ObservableCollection(Of Avalonia.Point)
            Get
                Select Case _selectedCurveChannel
                    Case CurveChannel.Red
                        Return _curveRedPoints
                    Case CurveChannel.Green
                        Return _curveGreenPoints
                    Case CurveChannel.Blue
                        Return _curveBluePoints
                    Case CurveChannel.Luminance
                        Return _curveLuminancePoints
                    Case Else
                        Return _curveRgbPoints
                End Select
            End Get
        End Property

        Public ReadOnly Property ActiveCurveHistogramCounts As Integer()
            Get
                Select Case _selectedCurveChannel
                    Case CurveChannel.Red
                        Return _curveHistogramCounts.R
                    Case CurveChannel.Green
                        Return _curveHistogramCounts.G
                    Case CurveChannel.Blue
                        Return _curveHistogramCounts.B
                    Case Else
                        Return _curveHistogramCounts.L
                End Select
            End Get
        End Property

        Public ReadOnly Property ActiveCurveBrush As Avalonia.Media.IBrush
            Get
                Select Case _selectedCurveChannel
                    Case CurveChannel.Red
                        Return New Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#FF5C5C"))
                    Case CurveChannel.Green
                        Return New Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#5CD65C"))
                    Case CurveChannel.Blue
                        Return New Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#5C9CFF"))
                    Case CurveChannel.Luminance
                        Return New Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E7ECF0"))
                    Case Else
                        Return New Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#F08A1A"))
                End Select
            End Get
        End Property

        Public Property RedHue As Double
            Get
                Return _redHue
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_redHue, value, NameOf(RedHue))
            End Set
        End Property

        Public Property RedSaturation As Double
            Get
                Return _redSaturation
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_redSaturation, value, NameOf(RedSaturation))
            End Set
        End Property

        Public Property RedLuminance As Double
            Get
                Return _redLuminance
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_redLuminance, value, NameOf(RedLuminance))
            End Set
        End Property

        Public Property OrangeHue As Double
            Get
                Return _orangeHue
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_orangeHue, value, NameOf(OrangeHue))
            End Set
        End Property

        Public Property OrangeSaturation As Double
            Get
                Return _orangeSaturation
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_orangeSaturation, value, NameOf(OrangeSaturation))
            End Set
        End Property

        Public Property OrangeLuminance As Double
            Get
                Return _orangeLuminance
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_orangeLuminance, value, NameOf(OrangeLuminance))
            End Set
        End Property

        Public Property YellowHue As Double
            Get
                Return _yellowHue
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_yellowHue, value, NameOf(YellowHue))
            End Set
        End Property

        Public Property YellowSaturation As Double
            Get
                Return _yellowSaturation
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_yellowSaturation, value, NameOf(YellowSaturation))
            End Set
        End Property

        Public Property YellowLuminance As Double
            Get
                Return _yellowLuminance
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_yellowLuminance, value, NameOf(YellowLuminance))
            End Set
        End Property

        Public Property GreenHue As Double
            Get
                Return _greenHue
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_greenHue, value, NameOf(GreenHue))
            End Set
        End Property

        Public Property GreenSaturation As Double
            Get
                Return _greenSaturation
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_greenSaturation, value, NameOf(GreenSaturation))
            End Set
        End Property

        Public Property GreenLuminance As Double
            Get
                Return _greenLuminance
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_greenLuminance, value, NameOf(GreenLuminance))
            End Set
        End Property

        Public Property AquaHue As Double
            Get
                Return _aquaHue
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_aquaHue, value, NameOf(AquaHue))
            End Set
        End Property

        Public Property AquaSaturation As Double
            Get
                Return _aquaSaturation
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_aquaSaturation, value, NameOf(AquaSaturation))
            End Set
        End Property

        Public Property AquaLuminance As Double
            Get
                Return _aquaLuminance
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_aquaLuminance, value, NameOf(AquaLuminance))
            End Set
        End Property

        Public Property BlueHue As Double
            Get
                Return _blueHue
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_blueHue, value, NameOf(BlueHue))
            End Set
        End Property

        Public Property BlueSaturation As Double
            Get
                Return _blueSaturation
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_blueSaturation, value, NameOf(BlueSaturation))
            End Set
        End Property

        Public Property BlueLuminance As Double
            Get
                Return _blueLuminance
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_blueLuminance, value, NameOf(BlueLuminance))
            End Set
        End Property

        Public Property PurpleHue As Double
            Get
                Return _purpleHue
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_purpleHue, value, NameOf(PurpleHue))
            End Set
        End Property

        Public Property PurpleSaturation As Double
            Get
                Return _purpleSaturation
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_purpleSaturation, value, NameOf(PurpleSaturation))
            End Set
        End Property

        Public Property PurpleLuminance As Double
            Get
                Return _purpleLuminance
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_purpleLuminance, value, NameOf(PurpleLuminance))
            End Set
        End Property

        Public Property MagentaHue As Double
            Get
                Return _magentaHue
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_magentaHue, value, NameOf(MagentaHue))
            End Set
        End Property

        Public Property MagentaSaturation As Double
            Get
                Return _magentaSaturation
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_magentaSaturation, value, NameOf(MagentaSaturation))
            End Set
        End Property

        Public Property MagentaLuminance As Double
            Get
                Return _magentaLuminance
            End Get
            Set(value As Double)
                SetUndoableHslDouble(_magentaLuminance, value, NameOf(MagentaLuminance))
            End Set
        End Property

        Private _selectedHslBand As String = "Red"

        ''' Welches der 8 HSL-Bänder gerade per Farbrad ausgewählt ist - steuert, welches der 16
        ''' zugrundeliegenden Band-Paare (RedHue/RedSaturation ... MagentaHue/MagentaSaturation) die
        ''' beiden gemeinsamen Regler (ActiveHslHue/ActiveHslSaturation) gerade lesen/schreiben. Die 8
        ''' Bänder selbst bleiben dabei unverändert im Hintergrund bestehen (reine Bedienoberfläche).
        Public Property SelectedHslBand As String
            Get
                Return _selectedHslBand
            End Get
            Set(value As String)
                Dim normalized = If(String.IsNullOrWhiteSpace(value), "Red", value)
                Me.RaiseAndSetIfChanged(_selectedHslBand, normalized)
                Me.RaisePropertyChanged(NameOf(SelectedHslBandLabel))
                Me.RaisePropertyChanged(NameOf(ActiveHslHue))
                Me.RaisePropertyChanged(NameOf(ActiveHslSaturation))
                Me.RaisePropertyChanged(NameOf(ActiveHslLuminance))
            End Set
        End Property

        Public ReadOnly Property SelectedHslBandLabel As String
            Get
                Select Case _selectedHslBand
                    Case "Orange" : Return "Orange"
                    Case "Yellow" : Return "Gelb"
                    Case "Green" : Return "Grün"
                    Case "Aqua" : Return "Aqua"
                    Case "Blue" : Return "Blau"
                    Case "Purple" : Return "Lila"
                    Case "Magenta" : Return "Magenta"
                    Case Else : Return "Rot"
                End Select
            End Get
        End Property

        Public Property ActiveHslHue As Double
            Get
                Select Case _selectedHslBand
                    Case "Orange" : Return OrangeHue
                    Case "Yellow" : Return YellowHue
                    Case "Green" : Return GreenHue
                    Case "Aqua" : Return AquaHue
                    Case "Blue" : Return BlueHue
                    Case "Purple" : Return PurpleHue
                    Case "Magenta" : Return MagentaHue
                    Case Else : Return RedHue
                End Select
            End Get
            Set(value As Double)
                Select Case _selectedHslBand
                    Case "Orange" : OrangeHue = value
                    Case "Yellow" : YellowHue = value
                    Case "Green" : GreenHue = value
                    Case "Aqua" : AquaHue = value
                    Case "Blue" : BlueHue = value
                    Case "Purple" : PurpleHue = value
                    Case "Magenta" : MagentaHue = value
                    Case Else : RedHue = value
                End Select
            End Set
        End Property

        Public Property ActiveHslSaturation As Double
            Get
                Select Case _selectedHslBand
                    Case "Orange" : Return OrangeSaturation
                    Case "Yellow" : Return YellowSaturation
                    Case "Green" : Return GreenSaturation
                    Case "Aqua" : Return AquaSaturation
                    Case "Blue" : Return BlueSaturation
                    Case "Purple" : Return PurpleSaturation
                    Case "Magenta" : Return MagentaSaturation
                    Case Else : Return RedSaturation
                End Select
            End Get
            Set(value As Double)
                Select Case _selectedHslBand
                    Case "Orange" : OrangeSaturation = value
                    Case "Yellow" : YellowSaturation = value
                    Case "Green" : GreenSaturation = value
                    Case "Aqua" : AquaSaturation = value
                    Case "Blue" : BlueSaturation = value
                    Case "Purple" : PurpleSaturation = value
                    Case "Magenta" : MagentaSaturation = value
                    Case Else : RedSaturation = value
                End Select
            End Set
        End Property

        Public Property ActiveHslLuminance As Double
            Get
                Select Case _selectedHslBand
                    Case "Orange" : Return OrangeLuminance
                    Case "Yellow" : Return YellowLuminance
                    Case "Green" : Return GreenLuminance
                    Case "Aqua" : Return AquaLuminance
                    Case "Blue" : Return BlueLuminance
                    Case "Purple" : Return PurpleLuminance
                    Case "Magenta" : Return MagentaLuminance
                    Case Else : Return RedLuminance
                End Select
            End Get
            Set(value As Double)
                Select Case _selectedHslBand
                    Case "Orange" : OrangeLuminance = value
                    Case "Yellow" : YellowLuminance = value
                    Case "Green" : GreenLuminance = value
                    Case "Aqua" : AquaLuminance = value
                    Case "Blue" : BlueLuminance = value
                    Case "Purple" : PurpleLuminance = value
                    Case "Magenta" : MagentaLuminance = value
                    Case Else : RedLuminance = value
                End Select
            End Set
        End Property

        Public Property RetouchRadius As Double
            Get
                Return _retouchRadius
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_retouchRadius, Math.Max(1, Math.Min(300, value)))
                RaiseResetButtonStateChanged()
                SchedulePreviewUpdate()
            End Set
        End Property

        Public ReadOnly Property HasCloneSource As Boolean
            Get
                Return _cloneSourceXPercent >= 0 AndAlso _cloneSourceYPercent >= 0
            End Get
        End Property

        Public ReadOnly Property RetouchHintText As String
            Get
                If Not IsCloneMode Then Return "Mittelt die Umgebung des Ziels und blendet sie weich ein."
                If HasCloneSource Then Return "Quelle gesetzt - Ziehen kopiert die Textur von dort."
                Return "Alt+Klick ins Bild setzt zuerst die Quelle, aus der kopiert wird."
            End Get
        End Property

        ''' Setzt den Punkt, von dem geklont wird (Alt+Klick). Der gemerkte Versatz wird verworfen und
        ''' beim nächsten gesetzten Punkt neu bestimmt.
        Public Sub SetCloneSource(xPercent As Double, yPercent As Double)
            _cloneSourceXPercent = Math.Max(0, Math.Min(100, xPercent))
            _cloneSourceYPercent = Math.Max(0, Math.Min(100, yPercent))
            _hasCloneOffset = False
            RaiseCloneSourceProperties()
        End Sub

        Public Sub ClearCloneSource()
            If Not HasCloneSource Then Return
            _cloneSourceXPercent = -1
            _cloneSourceYPercent = -1
            _hasCloneOffset = False
            RaiseCloneSourceProperties()
        End Sub

        ''' Liefert die Bildstelle in Prozent, aus der ein Retusche-Punkt an (xPercent, yPercent)
        ''' kopieren würde - solange noch kein Versatz feststeht, ist das der gesetzte Quellpunkt
        ''' selbst. IsValid ist False, wenn keine Quelle gesetzt ist oder die Abtaststelle aus dem
        ''' Bild gewandert ist; dann greift der Ringmittelwert.
        Public Function GetCloneSamplePercent(xPercent As Double, yPercent As Double) As (X As Double, Y As Double, IsValid As Boolean)
            If Not IsCloneMode OrElse Not HasCloneSource Then Return (0, 0, False)
            If Not _hasCloneOffset Then Return (_cloneSourceXPercent, _cloneSourceYPercent, True)

            Dim baseWidth = Math.Max(1, GetBaseWidth())
            Dim baseHeight = Math.Max(1, GetBaseHeight())
            Dim sourceX = PercentXToPixels(xPercent) - _cloneOffsetXPixels
            Dim sourceY = PercentYToPixels(yPercent) - _cloneOffsetYPixels
            If sourceX < 0 OrElse sourceY < 0 OrElse sourceX > baseWidth OrElse sourceY > baseHeight Then Return (0, 0, False)

            Return (sourceX / baseWidth * 100.0, sourceY / baseHeight * 100.0, True)
        End Function

        Private Sub RaiseCloneSourceProperties()
            Me.RaisePropertyChanged(NameOf(HasCloneSource))
            Me.RaisePropertyChanged(NameOf(RetouchHintText))
        End Sub

        Public Property BrushSize As Double
            Get
                Return _brushSize
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_brushSize, Math.Max(1, Math.Min(300, value)))
                RaiseResetButtonStateChanged()
            End Set
        End Property

        Public Property BrushHardness As Double
            Get
                Return _brushHardness
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_brushHardness, Math.Max(0, Math.Min(100, value)))
                RaiseResetButtonStateChanged()
            End Set
        End Property

        Public Property BrushOpacity As Double
            Get
                Return _brushOpacity
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_brushOpacity, Math.Max(0, Math.Min(100, value)))
                RaiseResetButtonStateChanged()
            End Set
        End Property

        Public Property IsEraserMode As Boolean
            Get
                Return _isEraserMode
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isEraserMode, value)
                ' Umschalten zwischen Pinsel und Radiergummi beendet die laufende Mal-Sitzung (siehe
                ' AddBrushStroke) - der nächste Strich landet auf einer neuen Ebene.
                _activeStrokeAnnotation = Nothing
                Me.RaisePropertyChanged(NameOf(CurrentToolLabel))
                Me.RaisePropertyChanged(NameOf(CurrentToolIconSource))
                Me.RaisePropertyChanged(NameOf(IsBrushPaintMode))
                Me.RaisePropertyChanged(NameOf(ShowBrushStrokeAdjustments))
                Me.RaisePropertyChanged(NameOf(IsEraserPaintMode))
                Me.RaisePropertyChanged(NameOf(IsSmudgePaintMode))
                Me.RaisePropertyChanged(NameOf(SelectedPaintMode))
            End Set
        End Property

        Public ReadOnly Property IsBrushPaintMode As Boolean
            Get
                Return _currentTool = EditorTool.Draw AndAlso Not _isEraserMode
            End Get
        End Property

        ''' Größe, Härte und Deckkraft gelten für Pinsel UND Radiergummi - beide legen denselben
        ''' Strich an (siehe AppendBrushStroke), nur die Farbe braucht der Radiergummi nicht.
        Public ReadOnly Property ShowBrushStrokeAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Draw
            End Get
        End Property

        Public ReadOnly Property IsEraserPaintMode As Boolean
            Get
                Return _currentTool = EditorTool.Draw AndAlso _isEraserMode
            End Get
        End Property

        Public ReadOnly Property IsSmudgePaintMode As Boolean
            Get
                Return _currentTool = EditorTool.Retouch
            End Get
        End Property

        Public Property FilterPreset As String
            Get
                Return _filterPreset
            End Get
            Set(value As String)
                Dim normalized = If(String.IsNullOrWhiteSpace(value), "Keine", value)
                If String.Equals(_filterPreset, normalized, StringComparison.Ordinal) Then Return
                CaptureUndoState(NameOf(FilterPreset))
                Me.RaiseAndSetIfChanged(_filterPreset, normalized)
                If Not String.Equals(normalized, "Keine", StringComparison.OrdinalIgnoreCase) Then
                    _filterStrength = ImageAdjustments.DefaultFilterStrength(normalized)
                    Me.RaisePropertyChanged(NameOf(FilterStrength))
                End If
                SetLastAppliedFilterPreset(normalized)
                RaiseResetButtonStateChanged()
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property FilterStrength As Double
            Get
                Return _filterStrength
            End Get
            Set(value As Double)
                SetUndoableDouble(_filterStrength, Math.Max(0, Math.Min(100, value)), NameOf(FilterStrength))
            End Set
        End Property

        Public Property LutPath As String
            Get
                Return _lutPath
            End Get
            Set(value As String)
                Dim normalized = If(value, "")
                If String.Equals(_lutPath, normalized, StringComparison.Ordinal) Then Return
                CaptureUndoState(NameOf(LutPath))
                Me.RaiseAndSetIfChanged(_lutPath, normalized)
                Me.RaisePropertyChanged(NameOf(HasLutApplied))
                RaiseResetButtonStateChanged()
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property LutStrength As Double
            Get
                Return _lutStrength
            End Get
            Set(value As Double)
                SetUndoableDouble(_lutStrength, Math.Max(0, Math.Min(100, value)), NameOf(LutStrength))
            End Set
        End Property

        Public ReadOnly Property HasLutApplied As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(_lutPath)
            End Get
        End Property

        Public Property CropLeft As Double
            Get
                Return _cropLeft
            End Get
            Set(value As Double)
                Dim clamped = ClampCropEdge(value, _cropRight, MaxCropEdgePercent(EffectiveImageWidthPixels))
                If Math.Abs(_cropLeft - clamped) < 0.0001 Then Return
                Me.RaiseAndSetIfChanged(_cropLeft, clamped)
                AfterCropChanged()
                RaiseResetButtonStateChanged()
            End Set
        End Property

        Public Property CropTop As Double
            Get
                Return _cropTop
            End Get
            Set(value As Double)
                Dim clamped = ClampCropEdge(value, _cropBottom, MaxCropEdgePercent(EffectiveImageHeightPixels))
                If Math.Abs(_cropTop - clamped) < 0.0001 Then Return
                Me.RaiseAndSetIfChanged(_cropTop, clamped)
                AfterCropChanged()
                RaiseResetButtonStateChanged()
            End Set
        End Property

        Public Property CropRight As Double
            Get
                Return _cropRight
            End Get
            Set(value As Double)
                Dim clamped = ClampCropEdge(value, _cropLeft, MaxCropEdgePercent(EffectiveImageWidthPixels))
                If Math.Abs(_cropRight - clamped) < 0.0001 Then Return
                Me.RaiseAndSetIfChanged(_cropRight, clamped)
                AfterCropChanged()
                RaiseResetButtonStateChanged()
            End Set
        End Property

        Public Property CropBottom As Double
            Get
                Return _cropBottom
            End Get
            Set(value As Double)
                Dim clamped = ClampCropEdge(value, _cropTop, MaxCropEdgePercent(EffectiveImageHeightPixels))
                If Math.Abs(_cropBottom - clamped) < 0.0001 Then Return
                Me.RaiseAndSetIfChanged(_cropBottom, clamped)
                AfterCropChanged()
                RaiseResetButtonStateChanged()
            End Set
        End Property

        ''' <summary>Die Kantenregler des Zuschneiden-Panels arbeiten in Pixeln des angezeigten Bildes.
        ''' Intern bleibt der Beschnitt prozentual, weil Presets, das Ziehen im Overlay und die
        ''' gespeicherten Adjustments auflösungsunabhängig sein müssen.</summary>
        Public Property CropLeftPixels As Integer
            Get
                Return PercentToPixels(_cropLeft, EffectiveImageWidthPixels)
            End Get
            Set(value As Integer)
                Dim basePixels = EffectiveImageWidthPixels
                If basePixels <= 0 Then Return
                CropLeft = PixelsToPercent(value, basePixels)
                Me.RaisePropertyChanged(NameOf(CropLeftPixels))
            End Set
        End Property

        Public Property CropTopPixels As Integer
            Get
                Return PercentToPixels(_cropTop, EffectiveImageHeightPixels)
            End Get
            Set(value As Integer)
                Dim basePixels = EffectiveImageHeightPixels
                If basePixels <= 0 Then Return
                CropTop = PixelsToPercent(value, basePixels)
                Me.RaisePropertyChanged(NameOf(CropTopPixels))
            End Set
        End Property

        Public Property CropRightPixels As Integer
            Get
                Return PercentToPixels(_cropRight, EffectiveImageWidthPixels)
            End Get
            Set(value As Integer)
                Dim basePixels = EffectiveImageWidthPixels
                If basePixels <= 0 Then Return
                CropRight = PixelsToPercent(value, basePixels)
                Me.RaisePropertyChanged(NameOf(CropRightPixels))
            End Set
        End Property

        Public Property CropBottomPixels As Integer
            Get
                Return PercentToPixels(_cropBottom, EffectiveImageHeightPixels)
            End Get
            Set(value As Integer)
                Dim basePixels = EffectiveImageHeightPixels
                If basePixels <= 0 Then Return
                CropBottom = PixelsToPercent(value, basePixels)
                Me.RaisePropertyChanged(NameOf(CropBottomPixels))
            End Set
        End Property

        ''' Obergrenze der Kantenregler: alles bis auf ein Pixel des angezeigten Bildes. Die tatsächliche
        ''' Grenze gegen die Gegenkante zieht der Setter, wie schon bei den Prozentwerten.
        Public ReadOnly Property CropMaxHorizontalPixels As Integer
            Get
                Return Math.Max(1, EffectiveImageWidthPixels - 1)
            End Get
        End Property

        Public ReadOnly Property CropMaxVerticalPixels As Integer
            Get
                Return Math.Max(1, EffectiveImageHeightPixels - 1)
            End Get
        End Property

        Public Property CropWidthPixels As Integer
            Get
                Return GetCroppedWidth()
            End Get
            Set(value As Integer)
                SetCropSizePixels(value, GetCroppedHeight())
            End Set
        End Property

        Public Property CropHeightPixels As Integer
            Get
                Return GetCroppedHeight()
            End Get
            Set(value As Integer)
                SetCropSizePixels(GetCroppedWidth(), value)
            End Set
        End Property

        Public ReadOnly Property ResizeInterpolationOptions As IReadOnlyList(Of String)
            Get
                Return New String() {"Nächstgelegen", "Bilinear", "Bikubisch"}
            End Get
        End Property

        Public Property ResizeInterpolationLabel As String
            Get
                Select Case _resizeInterpolation
                    Case ResizeInterpolationMode.Nearest
                        Return "Nächstgelegen"
                    Case ResizeInterpolationMode.Bilinear
                        Return "Bilinear"
                    Case Else
                        Return "Bikubisch"
                End Select
            End Get
            Set(value As String)
                Dim mode As ResizeInterpolationMode
                Select Case value
                    Case "Nächstgelegen"
                        mode = ResizeInterpolationMode.Nearest
                    Case "Bilinear"
                        mode = ResizeInterpolationMode.Bilinear
                    Case Else
                        mode = ResizeInterpolationMode.Bicubic
                End Select
                If _resizeInterpolation = mode Then Return
                CaptureUndoState(NameOf(ResizeInterpolationLabel))
                Me.RaiseAndSetIfChanged(_resizeInterpolation, mode)
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property ResizeWidth As Integer
            Get
                Return If(_resizeWidth > 0, _resizeWidth, GetCroppedWidth())
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(0, value)
                If _resizeWidth = clamped Then Return
                Me.RaiseAndSetIfChanged(_resizeWidth, clamped)
                If _lockResizeAspect AndAlso Not _isUpdatingResize Then
                    SyncResizeHeightFromWidth()
                End If
                Me.RaisePropertyChanged(NameOf(OutputSizeText))
                RaiseResetButtonStateChanged()
                ScheduleToolPreviewUpdate()
            End Set
        End Property

        Public Property ResizeHeight As Integer
            Get
                Return If(_resizeHeight > 0, _resizeHeight, GetCroppedHeight())
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(0, value)
                If _resizeHeight = clamped Then Return
                Me.RaiseAndSetIfChanged(_resizeHeight, clamped)
                If _lockResizeAspect AndAlso Not _isUpdatingResize Then
                    SyncResizeWidthFromHeight()
                End If
                Me.RaisePropertyChanged(NameOf(OutputSizeText))
                RaiseResetButtonStateChanged()
                ScheduleToolPreviewUpdate()
            End Set
        End Property

        Public Property LockResizeAspect As Boolean
            Get
                Return _lockResizeAspect
            End Get
            Set(value As Boolean)
                If _lockResizeAspect = value Then Return
                CaptureUndoState(NameOf(LockResizeAspect))
                Me.RaiseAndSetIfChanged(_lockResizeAspect, value)
                If value AndAlso _resizeWidth > 0 Then SyncResizeHeightFromWidth()
                RaiseResetButtonStateChanged()
            End Set
        End Property

        Public Property LockCanvasAspect As Boolean
            Get
                Return _lockCanvasAspect
            End Get
            Set(value As Boolean)
                If _lockCanvasAspect = value Then Return
                CaptureUndoState(NameOf(LockCanvasAspect))
                Me.RaiseAndSetIfChanged(_lockCanvasAspect, value)
                RaiseResetButtonStateChanged()
            End Set
        End Property

        Public Property CanvasWidth As Integer
            Get
                Return If(_canvasWidth > 0, _canvasWidth, If(_resizeWidth > 0, _resizeWidth, GetCroppedWidth()))
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(0, value)
                Dim previousWidth = Me.CanvasWidth
                Dim previousHeight = Me.CanvasHeight
                If _canvasWidth = clamped Then Return
                Me.RaiseAndSetIfChanged(_canvasWidth, clamped)
                If _lockCanvasAspect AndAlso Not _isUpdatingCanvas AndAlso previousWidth > 0 Then
                    _isUpdatingCanvas = True
                    _canvasHeight = Math.Max(1, CInt(Math.Round(previousHeight * (clamped / CDbl(previousWidth)))))
                    _isUpdatingCanvas = False
                    Me.RaisePropertyChanged(NameOf(CanvasHeight))
                End If
                Me.RaisePropertyChanged(NameOf(OutputSizeText))
                RaiseResetButtonStateChanged()
                ScheduleToolPreviewUpdate()
            End Set
        End Property

        Public Property CanvasHeight As Integer
            Get
                Return If(_canvasHeight > 0, _canvasHeight, If(_resizeHeight > 0, _resizeHeight, GetCroppedHeight()))
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(0, value)
                Dim previousWidth = Me.CanvasWidth
                Dim previousHeight = Me.CanvasHeight
                If _canvasHeight = clamped Then Return
                Me.RaiseAndSetIfChanged(_canvasHeight, clamped)
                If _lockCanvasAspect AndAlso Not _isUpdatingCanvas AndAlso previousHeight > 0 Then
                    _isUpdatingCanvas = True
                    _canvasWidth = Math.Max(1, CInt(Math.Round(previousWidth * (clamped / CDbl(previousHeight)))))
                    _isUpdatingCanvas = False
                    Me.RaisePropertyChanged(NameOf(CanvasWidth))
                End If
                Me.RaisePropertyChanged(NameOf(OutputSizeText))
                RaiseResetButtonStateChanged()
                ScheduleToolPreviewUpdate()
            End Set
        End Property

        Public Property CanvasBackgroundColor As String
            Get
                Return _canvasBackgroundColor
            End Get
            Set(value As String)
                CaptureUndoState(NameOf(CanvasBackgroundColor))
                Me.RaiseAndSetIfChanged(_canvasBackgroundColor, NormalizeAvaloniaColor(value, "#FF000000"))
                Me.RaisePropertyChanged(NameOf(CanvasBackgroundColorValue))
                Me.RaisePropertyChanged(NameOf(CanvasBackgroundBrush))
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property CanvasBackgroundColorValue As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_canvasBackgroundColor, Avalonia.Media.Colors.Black)
            End Get
            Set(value As Avalonia.Media.Color)
                CanvasBackgroundColor = value.ToString()
            End Set
        End Property

        Public Property CanvasAnchor As String
            Get
                Return _canvasAnchor
            End Get
            Set(value As String)
                Dim normalized = If(String.IsNullOrWhiteSpace(value), "Center", value.Trim())
                If String.Equals(_canvasAnchor, normalized, StringComparison.OrdinalIgnoreCase) Then Return
                CaptureUndoState(NameOf(CanvasAnchor))
                Me.RaiseAndSetIfChanged(_canvasAnchor, normalized)
                RaiseResetButtonStateChanged()
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property AnnotationText As String
            Get
                Return _annotationText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationText, If(value, ""))
                UpdatePendingTextAnnotationSize()
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationFillColor As String
            Get
                Return _annotationFillColor
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationFillColor, NormalizeAvaloniaColor(value, "#FFFFFFFF"))
                Me.RaisePropertyChanged(NameOf(AnnotationFillColorValue))
                Me.RaisePropertyChanged(NameOf(AnnotationFillBrush))
                Me.RaisePropertyChanged(NameOf(SelectionFillPreviewBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationStrokeColor As String
            Get
                Return _annotationStrokeColor
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationStrokeColor, NormalizeAvaloniaColor(value, "#FF000000"))
                Me.RaisePropertyChanged(NameOf(AnnotationStrokeColorValue))
                Me.RaisePropertyChanged(NameOf(AnnotationStrokeBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationFillColorValue As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_annotationFillColor, Avalonia.Media.Colors.White)
            End Get
            Set(value As Avalonia.Media.Color)
                AnnotationFillColor = value.ToString()
            End Set
        End Property

        Public Property AnnotationStrokeColorValue As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_annotationStrokeColor, Avalonia.Media.Colors.Black)
            End Get
            Set(value As Avalonia.Media.Color)
                AnnotationStrokeColor = value.ToString()
            End Set
        End Property

        Public ReadOnly Property AnnotationStrokeBrush As Avalonia.Media.IBrush
            Get
                Return New Avalonia.Media.SolidColorBrush(AnnotationStrokeColorValue)
            End Get
        End Property

        ' Diese Brush-Properties werden aktuell nur noch von den jeweiligen *Value-Settern per
        ' RaisePropertyChanged(NameOf(...)) benachrichtigt (kein XAML-Binding mehr seit dem Umstieg auf
        ' ColorPickerButton, das seinen Swatch intern selbst zeichnet) - als Notify-Ziel weiterhin nötig,
        ' daher hier belassen statt entfernt.
        Public ReadOnly Property BorderColorBrush As Avalonia.Media.IBrush
            Get
                Return New Avalonia.Media.SolidColorBrush(BorderColorValue)
            End Get
        End Property

        Public ReadOnly Property CanvasBackgroundBrush As Avalonia.Media.IBrush
            Get
                Return New Avalonia.Media.SolidColorBrush(CanvasBackgroundColorValue)
            End Get
        End Property

        Public ReadOnly Property AnnotationFillBrush As Avalonia.Media.IBrush
            Get
                Return New Avalonia.Media.SolidColorBrush(AnnotationFillColorValue)
            End Get
        End Property

        Public ReadOnly Property AnnotationFillColor2Brush As Avalonia.Media.IBrush
            Get
                Return New Avalonia.Media.SolidColorBrush(AnnotationFillColor2Value)
            End Get
        End Property

        Public ReadOnly Property AnnotationShadowColorBrush As Avalonia.Media.IBrush
            Get
                Return New Avalonia.Media.SolidColorBrush(AnnotationShadowColorValue)
            End Get
        End Property

        Public ReadOnly Property AnnotationGlowColorBrush As Avalonia.Media.IBrush
            Get
                Return New Avalonia.Media.SolidColorBrush(AnnotationGlowColorValue)
            End Get
        End Property

        Public Property AnnotationFontSize As Double
            Get
                Return _annotationFontSize
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationFontSize, Math.Max(8, Math.Min(5000, value)))
                Me.RaisePropertyChanged(NameOf(AnnotationFontSizePixels))
                UpdatePendingTextAnnotationSize()
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationFontSizePixels As Integer
            Get
                Return CInt(Math.Round(_annotationFontSize))
            End Get
            Set(value As Integer)
                AnnotationFontSize = value
            End Set
        End Property

        Public Property AnnotationStrokeWidth As Double
            Get
                Return _annotationStrokeWidth
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationStrokeWidth, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationFontFamily As String
            Get
                Return _annotationFontFamily
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationFontFamily, If(String.IsNullOrWhiteSpace(value), "Arial", value))
                UpdatePendingTextAnnotationSize()
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationOpacity As Double
            Get
                Return _annotationOpacity
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationOpacity, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationRotation As Double
            Get
                Return _annotationRotation
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationRotation, Math.Max(-180, Math.Min(180, value)))
                SyncSelectedAnnotation(refreshOverlay:=False)
            End Set
        End Property

        ''' Spiegelung des markierten Objekts um seine eigene Mitte (Drehen-Werkzeug, Knöpfe Horizontal/Vertikal).
        Public Property AnnotationFlipHorizontal As Boolean
            Get
                Return _annotationFlipH
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationFlipH, value)
                SyncSelectedAnnotation(refreshOverlay:=False)
            End Set
        End Property

        Public Property AnnotationFlipVertical As Boolean
            Get
                Return _annotationFlipV
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationFlipV, value)
                SyncSelectedAnnotation(refreshOverlay:=False)
            End Set
        End Property

        Public Property AnnotationIsVisible As Boolean
            Get
                Return _annotationIsVisible
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationIsVisible, value)
                SyncSelectedAnnotation()
            End Set
        End Property

        Private Const AnnotationMinVisiblePercent As Double = 1.0

        Private Shared Function ClampAnnotationPositionPercent(value As Double, sizePercent As Double) As Double
            Dim size = Math.Max(0.0, sizePercent)
            Dim minValue = GetAnnotationPositionMinimumPercent(size)
            Dim maxValue = 100.0 - AnnotationMinVisiblePercent
            Return Math.Max(minValue, Math.Min(maxValue, value))
        End Function

        Private Shared Function GetAnnotationPositionMinimumPercent(sizePercent As Double) As Double
            Return -Math.Max(0.0, Math.Max(0.0, sizePercent) - AnnotationMinVisiblePercent)
        End Function

        Public Property AnnotationXPercent As Double
            Get
                Return _annotationXPercent
            End Get
            Set(value As Double)
                Dim normalized = If(ShowWatermarkAnchorControls,
                                    ClampAnnotationOffsetPercent(value),
                                    ClampAnnotationPositionPercent(value, _annotationWidthPercent))
                Me.RaiseAndSetIfChanged(_annotationXPercent, normalized)
                Me.RaisePropertyChanged(NameOf(AnnotationXPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationXSliderValue))
                SyncSelectedAnnotation(refreshOverlay:=False)
            End Set
        End Property

        Public Property AnnotationYPercent As Double
            Get
                Return _annotationYPercent
            End Get
            Set(value As Double)
                Dim normalized = If(ShowWatermarkAnchorControls,
                                    ClampAnnotationOffsetPercent(value),
                                    ClampAnnotationPositionPercent(value, _annotationHeightPercent))
                Me.RaiseAndSetIfChanged(_annotationYPercent, normalized)
                Me.RaisePropertyChanged(NameOf(AnnotationYPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationYSliderValue))
                SyncSelectedAnnotation(refreshOverlay:=False)
            End Set
        End Property

        Public Property AnnotationXSliderValue As Double
            Get
                Return CDbl(AnnotationXPixels)
            End Get
            Set(value As Double)
                AnnotationXPixels = CInt(Math.Round(value))
            End Set
        End Property

        Public Property AnnotationYSliderValue As Double
            Get
                Return CDbl(AnnotationYPixels)
            End Get
            Set(value As Double)
                AnnotationYPixels = CInt(Math.Round(value))
            End Set
        End Property

        Public Property AnnotationWidthPercent As Double
            Get
                Return _annotationWidthPercent
            End Get
            Set(value As Double)
                If EffectiveAnnotationKind = "QR" Then
                    Dim baseWidth = GetBaseWidth()
                    Dim baseHeight = GetBaseHeight()
                    If baseWidth > 0 AndAlso baseHeight > 0 Then
                        Dim sizePixels = Math.Max(1.0, baseWidth * Math.Max(5, Math.Min(90, value)) / 100.0)
                        _annotationWidthPercent = Math.Max(5, Math.Min(90, sizePixels / baseWidth * 100.0))
                        _annotationHeightPercent = Math.Max(4, Math.Min(90, sizePixels / baseHeight * 100.0))
                        RaiseAnnotationSizeChanged()
                        SyncSelectedAnnotation()
                        Return
                    End If
                End If
                Me.RaiseAndSetIfChanged(_annotationWidthPercent, Math.Max(5, Math.Min(90, value)))
                Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationWidthSliderMinimum))
                Me.RaisePropertyChanged(NameOf(AnnotationWidthSliderMaximum))
                RaiseAnnotationPositionControlProperties()
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationHeightPercent As Double
            Get
                Return _annotationHeightPercent
            End Get
            Set(value As Double)
                If EffectiveAnnotationKind = "QR" Then
                    Dim baseWidth = GetBaseWidth()
                    Dim baseHeight = GetBaseHeight()
                    If baseWidth > 0 AndAlso baseHeight > 0 Then
                        Dim sizePixels = Math.Max(1.0, baseHeight * Math.Max(4, Math.Min(90, value)) / 100.0)
                        _annotationWidthPercent = Math.Max(5, Math.Min(90, sizePixels / baseWidth * 100.0))
                        _annotationHeightPercent = Math.Max(4, Math.Min(90, sizePixels / baseHeight * 100.0))
                        RaiseAnnotationSizeChanged()
                        SyncSelectedAnnotation()
                        Return
                    End If
                End If
                Me.RaiseAndSetIfChanged(_annotationHeightPercent, Math.Max(4, Math.Min(90, value)))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightSliderMinimum))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightSliderMaximum))
                RaiseAnnotationPositionControlProperties()
                SyncSelectedAnnotation()
            End Set
        End Property

        ' "Solid", "LinearGradient" oder "RadialGradient" - nur für Rechteck/Ellipse-Objekte relevant,
        ' siehe ImageAnnotation.FillKind. Dient sowohl zum Bearbeiten des ausgewählten Objekts als auch
        ' (wie AnnotationFillColor) als "aktueller Stift" für neu erzeugte Objekte (FillSelection).
        Public Property AnnotationFillKind As String
            Get
                Return _annotationFillKind
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationFillKind, If(String.IsNullOrWhiteSpace(value), "Solid", value))
                Me.RaisePropertyChanged(NameOf(ShowGradientFillControls))
                Me.RaisePropertyChanged(NameOf(ShowLinearGradientAngleControl))
                Me.RaisePropertyChanged(NameOf(ShowRadialGradientControl))
                Me.RaisePropertyChanged(NameOf(SelectionFillPreviewBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationFillColor2 As String
            Get
                Return _annotationFillColor2
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationFillColor2, NormalizeAvaloniaColor(value, _annotationFillColor2))
                Me.RaisePropertyChanged(NameOf(AnnotationFillColor2Value))
                Me.RaisePropertyChanged(NameOf(AnnotationFillColor2Brush))
                Me.RaisePropertyChanged(NameOf(SelectionFillPreviewBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationFillColor2Value As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_annotationFillColor2, Avalonia.Media.Colors.White)
            End Get
            Set(value As Avalonia.Media.Color)
                AnnotationFillColor2 = value.ToString()
            End Set
        End Property

        Public Property AnnotationGradientAngleDegrees As Double
            Get
                Return _annotationGradientAngle
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationGradientAngle, Math.Max(0, Math.Min(360, value)))
                Me.RaisePropertyChanged(NameOf(SelectionFillPreviewBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationGradientInverted As Boolean
            Get
                Return _annotationGradientInverted
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationGradientInverted, value)
                Me.RaisePropertyChanged(NameOf(SelectionFillPreviewBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowEnabled As Boolean
            Get
                Return _annotationShadowEnabled
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationShadowEnabled, value)
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowOffsetX As Double
            Get
                Return _annotationShadowOffsetX
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowOffsetX, Math.Max(-100, Math.Min(100, value)))
                If _isLoadingAnnotation Then
                    _annotationShadowLightAngle = ComputeShadowLightAngle(_annotationShadowOffsetX, _annotationShadowOffsetY)
                    Me.RaisePropertyChanged(NameOf(AnnotationShadowLightAngle))
                End If
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowOffsetY As Double
            Get
                Return _annotationShadowOffsetY
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowOffsetY, Math.Max(-100, Math.Min(100, value)))
                If _isLoadingAnnotation Then
                    _annotationShadowLightAngle = ComputeShadowLightAngle(_annotationShadowOffsetX, _annotationShadowOffsetY)
                    Me.RaisePropertyChanged(NameOf(AnnotationShadowLightAngle))
                End If
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowLightAngle As Double
            Get
                Return _annotationShadowLightAngle
            End Get
            Set(value As Double)
                Dim normalized = NormalizeDegrees(value)
                If Math.Abs(normalized - _annotationShadowLightAngle) < 0.0001 Then Return
                _annotationShadowLightAngle = normalized
                Dim distance = Math.Sqrt(_annotationShadowOffsetX * _annotationShadowOffsetX + _annotationShadowOffsetY * _annotationShadowOffsetY)
                If distance < 1 Then distance = 6
                Dim shadowAngle = (_annotationShadowLightAngle + 180.0) * Math.PI / 180.0
                _annotationShadowOffsetX = Math.Max(-100, Math.Min(100, Math.Cos(shadowAngle) * distance))
                _annotationShadowOffsetY = Math.Max(-100, Math.Min(100, Math.Sin(shadowAngle) * distance))
                Me.RaisePropertyChanged(NameOf(AnnotationShadowOffsetX))
                Me.RaisePropertyChanged(NameOf(AnnotationShadowOffsetY))
                Me.RaisePropertyChanged(NameOf(AnnotationShadowLightAngle))
                SyncSelectedAnnotation()
            End Set
        End Property

        Private Shared Function ComputeShadowLightAngle(offsetX As Double, offsetY As Double) As Double
            Dim shadowAngle = Math.Atan2(offsetY, offsetX) * 180.0 / Math.PI
            Return NormalizeDegrees(shadowAngle + 180.0)
        End Function

        Private Shared Function NormalizeDegrees(value As Double) As Double
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return 0
            Return (value Mod 360.0 + 360.0) Mod 360.0
        End Function

        Public Property AnnotationShadowBlur As Double
            Get
                Return _annotationShadowBlur
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowBlur, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowStrength As Double
            Get
                Return _annotationShadowStrength
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowStrength, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowColor As String
            Get
                Return _annotationShadowColor
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationShadowColor, NormalizeAvaloniaColor(value, _annotationShadowColor))
                Me.RaisePropertyChanged(NameOf(AnnotationShadowColorValue))
                Me.RaisePropertyChanged(NameOf(AnnotationShadowColorBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowColorValue As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_annotationShadowColor, Avalonia.Media.Color.FromArgb(128, 0, 0, 0))
            End Get
            Set(value As Avalonia.Media.Color)
                AnnotationShadowColor = value.ToString()
            End Set
        End Property

        Public Property AnnotationShadowRounded As Boolean
            Get
                Return _annotationShadowRounded
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationShadowRounded, value)
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowCornerRadius As Double
            Get
                Return _annotationShadowCornerRadius
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowCornerRadius, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowSize As Double
            Get
                Return _annotationShadowSize
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowSize, Math.Max(25, Math.Min(300, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationGlowEnabled As Boolean
            Get
                Return _annotationGlowEnabled
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationGlowEnabled, value)
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationGlowBlur As Double
            Get
                Return _annotationGlowBlur
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationGlowBlur, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationGlowStrength As Double
            Get
                Return _annotationGlowStrength
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationGlowStrength, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationGlowColor As String
            Get
                Return _annotationGlowColor
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationGlowColor, NormalizeAvaloniaColor(value, _annotationGlowColor))
                Me.RaisePropertyChanged(NameOf(AnnotationGlowColorValue))
                Me.RaisePropertyChanged(NameOf(AnnotationGlowColorBrush))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationGlowColorValue As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_annotationGlowColor, Avalonia.Media.Colors.Yellow)
            End Get
            Set(value As Avalonia.Media.Color)
                AnnotationGlowColor = value.ToString()
            End Set
        End Property

        ' Auswahlrechteck des Auswahlwerkzeugs (Phase 4) - Prozent-vom-Bild wie beim Crop, aber als
        ' eigenständiges Rechteck (X/Y/Breite/Höhe) statt Rand-Abstände. HasActiveSelection steuert
        ' Sichtbarkeit des Overlays und Aktivierung von "Kopieren"/"Füllen" in der UI.
        Public Property HasActiveSelection As Boolean
            Get
                Return _hasActiveSelection
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_hasActiveSelection, value)
            End Set
        End Property

        Public Property SelectionXPercent As Double
            Get
                Return _selectionXPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_selectionXPercent, value)
            End Set
        End Property

        Public Property SelectionYPercent As Double
            Get
                Return _selectionYPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_selectionYPercent, value)
            End Set
        End Property

        Public Property SelectionWidthPercent As Double
            Get
                Return _selectionWidthPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_selectionWidthPercent, value)
            End Set
        End Property

        Public Property SelectionHeightPercent As Double
            Get
                Return _selectionHeightPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_selectionHeightPercent, value)
            End Set
        End Property

        ''' Aktueller Auswahlmodus: "Rectangle", "Ellipse", "Lasso" oder "MagicWand". Steuert, wie
        ''' EditorView den Zeiger interpretiert (Rechteck aufziehen, Freihand zeichnen, klicken) und
        ''' welche Zusatzregler (Toleranz) sichtbar sind.
        Public Property SelectionMode As String
            Get
                Return _selectionMode
            End Get
            Set(value As String)
                Dim v = If(String.IsNullOrWhiteSpace(value), "Rectangle", value)
                If _selectionMode = v Then Return
                Me.RaiseAndSetIfChanged(_selectionMode, v)
                Me.RaisePropertyChanged(NameOf(ShowMagicWandControls))
                Me.RaisePropertyChanged(NameOf(IsRectangleSelectionMode))
                Me.RaisePropertyChanged(NameOf(IsEllipseSelectionMode))
                Me.RaisePropertyChanged(NameOf(IsLassoSelectionMode))
                Me.RaisePropertyChanged(NameOf(IsMagicWandSelectionMode))
            End Set
        End Property

        Public Sub SetSelectionMode(mode As String)
            SelectionMode = mode
        End Sub

        Public Property SelectionCombineMode As String
            Get
                Return _selectionCombineMode
            End Get
            Set(value As String)
                Dim v = NormalizeSelectionCombineMode(value)
                If _selectionCombineMode = v Then Return
                Me.RaiseAndSetIfChanged(_selectionCombineMode, v)
                Me.RaisePropertyChanged(NameOf(IsSelectionCombineNew))
                Me.RaisePropertyChanged(NameOf(IsSelectionCombineAdd))
                Me.RaisePropertyChanged(NameOf(IsSelectionCombineSubtract))
                Me.RaisePropertyChanged(NameOf(IsSelectionCombineIntersect))
            End Set
        End Property

        Public Sub SetSelectionCombineMode(mode As String)
            SelectionCombineMode = mode
        End Sub

        Public ReadOnly Property IsSelectionCombineNew As Boolean
            Get
                Return _selectionCombineMode = "New"
            End Get
        End Property

        Public ReadOnly Property IsSelectionCombineAdd As Boolean
            Get
                Return _selectionCombineMode = "Add"
            End Get
        End Property

        Public ReadOnly Property IsSelectionCombineSubtract As Boolean
            Get
                Return _selectionCombineMode = "Subtract"
            End Get
        End Property

        Public ReadOnly Property IsSelectionCombineIntersect As Boolean
            Get
                Return _selectionCombineMode = "Intersect"
            End Get
        End Property

        Public ReadOnly Property IsRectangleSelectionMode As Boolean
            Get
                Return _selectionMode = "Rectangle"
            End Get
        End Property
        Public ReadOnly Property IsEllipseSelectionMode As Boolean
            Get
                Return _selectionMode = "Ellipse"
            End Get
        End Property
        Public ReadOnly Property IsLassoSelectionMode As Boolean
            Get
                Return _selectionMode = "Lasso"
            End Get
        End Property
        Public ReadOnly Property IsMagicWandSelectionMode As Boolean
            Get
                Return _selectionMode = "MagicWand"
            End Get
        End Property

        Public ReadOnly Property ShowMagicWandControls As Boolean
            Get
                Return _selectionMode = "MagicWand"
            End Get
        End Property

        Public ReadOnly Property SelectionShapeMode As String
            Get
                Return _selectionShapeMode
            End Get
        End Property

        Public ReadOnly Property SelectionShapePointsX As Double()
            Get
                Return _selectionShapePointsX
            End Get
        End Property

        Public ReadOnly Property SelectionShapePointsY As Double()
            Get
                Return _selectionShapePointsY
            End Get
        End Property

        Public ReadOnly Property SelectionMaskPreviewImage As Bitmap
            Get
                Return _selectionMaskPreviewImage
            End Get
        End Property

        Public ReadOnly Property HasSelectionMask As Boolean
            Get
                Return _selectionMask IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property SelectionMaskEdgePointsX As Double()
            Get
                Return _selectionMaskEdgePointsX
            End Get
        End Property

        Public ReadOnly Property SelectionMaskEdgePointsY As Double()
            Get
                Return _selectionMaskEdgePointsY
            End Get
        End Property

        Public Function IsPointInsideSelectionPercent(xPercent As Double, yPercent As Double) As Boolean
            If Not _hasActiveSelection Then Return False
            Dim bw = GetBaseWidth()
            Dim bh = GetBaseHeight()
            If bw <= 0 OrElse bh <= 0 Then Return False

            Dim imageX = CInt(Math.Round(bw * xPercent / 100.0))
            Dim imageY = CInt(Math.Round(bh * yPercent / 100.0))
            imageX = Math.Max(0, Math.Min(bw - 1, imageX))
            imageY = Math.Max(0, Math.Min(bh - 1, imageY))

            If _selectionMask IsNot Nothing Then
                Dim localX = imageX - _selectionMaskRect.Left
                Dim localY = imageY - _selectionMaskRect.Top
                If localX < 0 OrElse localY < 0 OrElse localX >= _selectionMask.Width OrElse localY >= _selectionMask.Height Then Return False
                If _selectionMaskBytes Is Nothing OrElse _selectionMaskBytesStride <= 0 Then Return False
                Return _selectionMaskBytes(localY * _selectionMaskBytesStride + localX) > 0
            End If

            Dim rectPx = SelectionRectPixels()
            Return imageX >= rectPx.Left AndAlso imageX < rectPx.Right AndAlso imageY >= rectPx.Top AndAlso imageY < rectPx.Bottom
        End Function

        ''' Farbtoleranz des Zauberstabs in Prozent (0..100).
        Public Property SelectionTolerance As Double
            Get
                Return _selectionTolerance
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_selectionTolerance, Math.Max(0, Math.Min(100, value)))
            End Set
        End Property

        Private Sub SetSelectionMaskData(mask As SKBitmap, rectPx As SKRectI)
            If _selectionMask IsNot Nothing AndAlso Not Object.ReferenceEquals(_selectionMask, mask) Then _selectionMask.Dispose()
            _selectionMask = mask
            _selectionMaskRect = rectPx
            _selectionMaskBytesStride = If(mask Is Nothing, 0, mask.RowBytes)
            _selectionMaskBytes = If(mask Is Nothing, Nothing, New Byte(_selectionMaskBytesStride * mask.Height - 1) {})
            If mask IsNot Nothing Then Marshal.Copy(mask.GetPixels(), _selectionMaskBytes, 0, _selectionMaskBytes.Length)
            _selectionMaskBase64 = EncodeMaskBitmapToBase64(mask)
            RefreshSelectionMaskEdgePoints()
            Me.RaisePropertyChanged(NameOf(HasSelectionMask))
        End Sub

        Private Sub ClearSelectionMask()
            _selectionMask?.Dispose()
            _selectionMask = Nothing
            _selectionMaskRect = SKRectI.Empty
            _selectionMaskBytes = Nothing
            _selectionMaskBytesStride = 0
            _selectionMaskBase64 = ""
            _selectionMaskEdgePointsX = Nothing
            _selectionMaskEdgePointsY = Nothing
            Me.RaisePropertyChanged(NameOf(SelectionMaskEdgePointsX))
            Me.RaisePropertyChanged(NameOf(SelectionMaskEdgePointsY))
            SetSelectionMaskPreviewImage(Nothing)
            Me.RaisePropertyChanged(NameOf(HasSelectionMask))
        End Sub

        Private Sub SetSelectionMaskPreviewImage(image As Bitmap)
            Dim oldImage = _selectionMaskPreviewImage
            _selectionMaskPreviewImage = image
            If oldImage IsNot Nothing AndAlso Not Object.ReferenceEquals(oldImage, image) Then oldImage.Dispose()
            Me.RaisePropertyChanged(NameOf(SelectionMaskPreviewImage))
        End Sub

        Private Sub RefreshSelectionMaskEdgePoints()
            SetSelectionMaskPreviewImage(Nothing)
            If _selectionMask Is Nothing OrElse _selectionMaskBytes Is Nothing OrElse _selectionMask.Width <= 0 OrElse _selectionMask.Height <= 0 Then
                _selectionMaskEdgePointsX = Nothing
                _selectionMaskEdgePointsY = Nothing
                Me.RaisePropertyChanged(NameOf(SelectionMaskEdgePointsX))
                Me.RaisePropertyChanged(NameOf(SelectionMaskEdgePointsY))
                Return
            End If

            ' Randpixel einsammeln (die MASKE bleibt davon unberührt - das hier ist nur die Ameisenlinie).
            Dim w = _selectionMask.Width, h = _selectionMask.Height
            Dim stride = _selectionMaskBytesStride
            Dim bytes = _selectionMaskBytes
            Dim edgeXs As New List(Of Double)()
            Dim edgeYs As New List(Of Double)()

            For y = 0 To h - 1
                Dim row = y * stride
                Dim up = row - stride
                Dim down = row + stride
                For x = 0 To w - 1
                    If bytes(row + x) = 0 Then Continue For
                    Dim isEdge = x = 0 OrElse y = 0 OrElse x = w - 1 OrElse y = h - 1 OrElse
                                 bytes(row + x - 1) = 0 OrElse
                                 bytes(row + x + 1) = 0 OrElse
                                 bytes(up + x) = 0 OrElse
                                 bytes(down + x) = 0
                    If isEdge Then
                        edgeXs.Add((x + 0.5) * 100.0 / w)
                        edgeYs.Add((y + 0.5) * 100.0 / h)
                    End If
                Next
            Next

            ' Ausdünnen erst NACH dem Einsammeln, und gleichmäßig entlang des Randes. Die alte Regel
            ' „(x+y) Mod Schritt = 0" ließ ganze Diagonalstreifen der Kontur weg - die Ameisenlinie sah
            ' dadurch löchrig aus, obwohl die Maske exakt war. Die Obergrenze hält das Zeichnen flüssig:
            ' das Overlay malt je Punkt ein Kästchen und frischt zwölfmal pro Sekunde auf.
            Const MaxEdgePoints As Integer = 4000
            If edgeXs.Count > MaxEdgePoints Then
                Dim step_ = CInt(Math.Ceiling(edgeXs.Count / CDbl(MaxEdgePoints)))
                Dim thinnedX As New List(Of Double)(MaxEdgePoints + 1)
                Dim thinnedY As New List(Of Double)(MaxEdgePoints + 1)
                For i = 0 To edgeXs.Count - 1 Step step_
                    thinnedX.Add(edgeXs(i))
                    thinnedY.Add(edgeYs(i))
                Next
                edgeXs = thinnedX
                edgeYs = thinnedY
            End If

            _selectionMaskEdgePointsX = edgeXs.ToArray()
            _selectionMaskEdgePointsY = edgeYs.ToArray()
            Me.RaisePropertyChanged(NameOf(SelectionMaskEdgePointsX))
            Me.RaisePropertyChanged(NameOf(SelectionMaskEdgePointsY))
        End Sub

        Private Sub SetSelectionShape(mode As String, xsPercent As Double(), ysPercent As Double())
            _selectionShapeMode = If(String.IsNullOrWhiteSpace(mode), "Rectangle", mode)
            _selectionShapePointsX = xsPercent
            _selectionShapePointsY = ysPercent
            Me.RaisePropertyChanged(NameOf(SelectionShapeMode))
            Me.RaisePropertyChanged(NameOf(SelectionShapePointsX))
            Me.RaisePropertyChanged(NameOf(SelectionShapePointsY))
        End Sub

        Private Shared Function NormalizeSelectionCombineMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Add", "Subtract", "Intersect"
                    Return value.Trim()
                Case Else
                    Return "New"
            End Select
        End Function

        ' Auswahlrechteck aus Prozentwerten in Bildpixel umrechnen (für Maskenerzeugung/-extraktion).
        Private Function SelectionRectPixels() As SKRectI
            Dim bw = GetBaseWidth(), bh = GetBaseHeight()
            Dim left = CInt(Math.Round(bw * _selectionXPercent / 100.0))
            Dim top = CInt(Math.Round(bh * _selectionYPercent / 100.0))
            Dim right = CInt(Math.Round(bw * (_selectionXPercent + _selectionWidthPercent) / 100.0))
            Dim bottom = CInt(Math.Round(bh * (_selectionYPercent + _selectionHeightPercent) / 100.0))
            Return New SKRectI(Math.Max(0, left), Math.Max(0, top), Math.Min(bw, right), Math.Min(bh, bottom))
        End Function

        ''' <summary>Übernimmt das Auswahlrechteck aus Bildpixeln - OHNE Mindestgröße. Das Rechteck ist der
        ''' Bezugsrahmen der Maske: Overlay-Ränder, Ausschneiden und Füllen rechnen alle relativ dazu. Eine
        ''' Untergrenze (früher 0,5 % der Bildbreite = 30 px bei 6000 px) hätte bei kleinen Zauberstab-/
        ''' Lasso-Auswahlen das Rechteck größer gemacht als die Maske - die Ameisenlinie säße dann daneben
        ''' und ein kopierter Ausschnitt käme verzerrt heraus. Die Aufrufer verwerfen leere Rechtecke selbst.</summary>
        Private Sub SetSelectionBoundsFromPixels(rectPx As SKRectI)
            Dim bw = GetBaseWidth(), bh = GetBaseHeight()
            If bw <= 0 OrElse bh <= 0 Then Return
            _selectionXPercent = Math.Max(0, rectPx.Left * 100.0 / bw)
            _selectionYPercent = Math.Max(0, rectPx.Top * 100.0 / bh)
            _selectionWidthPercent = rectPx.Width * 100.0 / bw
            _selectionHeightPercent = rectPx.Height * 100.0 / bh
            Me.RaisePropertyChanged(NameOf(SelectionXPercent))
            Me.RaisePropertyChanged(NameOf(SelectionYPercent))
            Me.RaisePropertyChanged(NameOf(SelectionWidthPercent))
            Me.RaisePropertyChanged(NameOf(SelectionHeightPercent))
        End Sub

        Private Sub SetSelectionBoundsFromPercent(xPercent As Double, yPercent As Double, widthPercent As Double, heightPercent As Double)
            _selectionXPercent = Math.Max(0, xPercent)
            _selectionYPercent = Math.Max(0, yPercent)
            _selectionWidthPercent = Math.Max(0.5, widthPercent)
            _selectionHeightPercent = Math.Max(0.5, heightPercent)
            Me.RaisePropertyChanged(NameOf(SelectionXPercent))
            Me.RaisePropertyChanged(NameOf(SelectionYPercent))
            Me.RaisePropertyChanged(NameOf(SelectionWidthPercent))
            Me.RaisePropertyChanged(NameOf(SelectionHeightPercent))
        End Sub

        ''' Wird von EditorView beim Loslassen der Maus nach dem Aufziehen eines Auswahlrechtecks aufgerufen.
        Public Sub SetSelectionRect(xPercent As Double, yPercent As Double, widthPercent As Double, heightPercent As Double,
                                    Optional captureUndo As Boolean = True)
            Dim bw = GetBaseWidth(), bh = GetBaseHeight()
            If bw <= 0 OrElse bh <= 0 Then Return
            Dim rectPx = New SKRectI(
                Math.Max(0, CInt(Math.Round(bw * xPercent / 100.0))),
                Math.Max(0, CInt(Math.Round(bh * yPercent / 100.0))),
                Math.Min(bw, CInt(Math.Round(bw * (xPercent + widthPercent) / 100.0))),
                Math.Min(bh, CInt(Math.Round(bh * (yPercent + heightPercent) / 100.0))))
            If rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then Return
            If captureUndo Then PushUndo()

            If _selectionCombineMode = "New" OrElse Not _hasActiveSelection Then
                ClearSelectionMask()
                SetSelectionBoundsFromPixels(rectPx)
                SetSelectionShape("Rectangle", Nothing, Nothing)
                HasActiveSelection = True
                Return
            End If

            Using candidate = CreateSolidMask(rectPx.Width, rectPx.Height)
                ApplySelectionCandidate(candidate, rectPx, "Rectangle", Nothing, Nothing)
            End Using
        End Sub

        ''' Ellipse-Auswahl: Rechteck wie beim Rechteck-Modus, zusätzlich eine eingepasste Ellipsen-Maske.
        Public Sub SetSelectionEllipse(xPercent As Double, yPercent As Double, widthPercent As Double, heightPercent As Double,
                                       Optional captureUndo As Boolean = True)
            Dim bw = GetBaseWidth(), bh = GetBaseHeight()
            If bw <= 0 OrElse bh <= 0 Then Return
            Dim rawLeft = CInt(Math.Round(bw * xPercent / 100.0))
            Dim rawTop = CInt(Math.Round(bh * yPercent / 100.0))
            Dim rawRight = CInt(Math.Round(bw * (xPercent + widthPercent) / 100.0))
            Dim rawBottom = CInt(Math.Round(bh * (yPercent + heightPercent) / 100.0))
            Dim rectPx = New SKRectI(
                Math.Max(0, rawLeft),
                Math.Max(0, rawTop),
                Math.Min(bw, rawRight),
                Math.Min(bh, rawBottom))
            If rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then Return
            If captureUndo Then PushUndo()
            Dim localOval = New SKRect(rawLeft - rectPx.Left,
                                       rawTop - rectPx.Top,
                                       rawRight - rectPx.Left,
                                       rawBottom - rectPx.Top)
            Using mask = ImageProcessor.BuildEllipseMask(rectPx.Width, rectPx.Height, localOval)
                If mask IsNot Nothing Then ApplySelectionCandidate(mask, rectPx, "Ellipse", Nothing, Nothing)
            End Using
        End Sub

        ''' Lasso-Auswahl aus Freihand-Punkten (Prozentkoordinaten). Bounding-Box wird zum Auswahlrechteck,
        ''' das Polygon zur Maske.
        Public Sub SetSelectionLasso(xsPercent As Double(), ysPercent As Double(), Optional captureUndo As Boolean = True)
            If xsPercent Is Nothing OrElse ysPercent Is Nothing OrElse xsPercent.Length < 3 OrElse xsPercent.Length <> ysPercent.Length Then Return
            Dim minX = xsPercent.Min(), maxX = xsPercent.Max()
            Dim minY = ysPercent.Min(), maxY = ysPercent.Max()
            If (maxX - minX) < 0.5 OrElse (maxY - minY) < 0.5 Then Return
            Dim bw = GetBaseWidth(), bh = GetBaseHeight()
            If bw <= 0 OrElse bh <= 0 Then Return
            Dim rectPx = New SKRectI(
                Math.Max(0, CInt(Math.Round(bw * minX / 100.0))),
                Math.Max(0, CInt(Math.Round(bh * minY / 100.0))),
                Math.Min(bw, CInt(Math.Round(bw * maxX / 100.0))),
                Math.Min(bh, CInt(Math.Round(bh * maxY / 100.0))))
            If rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then Return
            Dim localX(xsPercent.Length - 1) As Single
            Dim localY(ysPercent.Length - 1) As Single
            For i = 0 To xsPercent.Length - 1
                localX(i) = CSng(bw * xsPercent(i) / 100.0 - rectPx.Left)
                localY(i) = CSng(bh * ysPercent(i) / 100.0 - rectPx.Top)
            Next
            If captureUndo Then PushUndo()
            Using mask = ImageProcessor.BuildPolygonMask(localX, localY, rectPx.Width, rectPx.Height)
                If mask IsNot Nothing Then ApplySelectionCandidate(mask, rectPx, "Lasso", xsPercent.ToArray(), ysPercent.ToArray())
            End Using
        End Sub

        ''' Zauberstab: wählt die zusammenhängende Farbfläche am Klickpunkt (Prozentkoordinaten).
        Public Sub SetSelectionMagicWand(xPercent As Double, yPercent As Double)
            If String.IsNullOrWhiteSpace(_currentImagePath) Then Return
            Dim bw = GetBaseWidth(), bh = GetBaseHeight()
            If bw <= 0 OrElse bh <= 0 Then Return
            Dim seedX = CInt(Math.Round(bw * xPercent / 100.0))
            Dim seedY = CInt(Math.Round(bh * yPercent / 100.0))
            seedX = Math.Max(0, Math.Min(bw - 1, seedX))
            seedY = Math.Max(0, Math.Min(bh - 1, seedY))
            Dim bounds As SKRectI
            Using mask = ImageProcessor.BuildMagicWandMaskFromFile(_currentImagePath, GetCurrentAdjustments(),
                                                                   seedX, seedY, CSng(_selectionTolerance / 100.0), bounds)
                If mask Is Nothing OrElse bounds.Width <= 0 OrElse bounds.Height <= 0 Then Return
                PushUndo()
                ' Kein Polygonzug: für maskenbasierte Auswahlen zeichnet das Overlay die Ameisenlinie aus den
                ' Maskenrändern und die Treffererkennung fragt die Maske selbst (siehe HasSelectionMask).
                ApplySelectionCandidate(mask, bounds, "MagicWand", Nothing, Nothing)
            End Using
        End Sub

        ''' <summary>Wählt das ganze Bild aus (Strg+A). Als reines Rechteck ohne Maske - das ist die
        ''' pixelgenaue und zugleich billigste Darstellung einer Voll-Auswahl; „Umkehren" macht daraus bei
        ''' Bedarf eine echte Maske.</summary>
        Public Sub SelectAll()
            Dim bw = GetBaseWidth(), bh = GetBaseHeight()
            If bw <= 0 OrElse bh <= 0 Then Return
            PushUndo()
            ClearSelectionMask()
            SetSelectionBoundsFromPercent(0, 0, 100, 100)
            SetSelectionShape("Rectangle", Nothing, Nothing)
            HasActiveSelection = True
        End Sub

        Public Sub ClearSelection(Optional captureUndo As Boolean = True)
            If captureUndo AndAlso _hasActiveSelection Then PushUndo()
            ClearSelectionMask()
            SetSelectionShape("Rectangle", Nothing, Nothing)
            HasActiveSelection = False
        End Sub

        ''' <summary>Verwirft die aktive Auswahl beim Anwenden von Geometrie (Crop/Resize/Canvas/Transform):
        ''' die Auswahlmaske liegt im Basisbild-Raum und würde danach nicht mehr zum Bild passen - der
        ''' Anpassungs-Skopus greift ohnehin nur bei neutraler Geometrie (siehe
        ''' ImageProcessor.SelectionGeometryIsNeutral). Ohne eigenen Undo-Schritt, damit die Löschung Teil
        ''' desselben Schritts wie die Geometrie ist (der Aufrufer hat unmittelbar davor PushUndo gerufen).</summary>
        Private Sub ClearActiveSelectionForGeometry()
            If _hasActiveSelection OrElse _selectionMask IsNot Nothing Then ClearSelection(captureUndo:=False)
        End Sub

        Public Sub InvertSelection()
            If Not _hasActiveSelection Then Return
            Dim bw = GetBaseWidth()
            Dim bh = GetBaseHeight()
            If bw <= 0 OrElse bh <= 0 Then Return

            Using existingMask = BuildCurrentSelectionMask()
                Dim existingRect = SelectionRectPixels()
                If existingMask Is Nothing OrElse existingRect.Width <= 0 OrElse existingRect.Height <= 0 Then Return

                PushUndo()
                Dim fullRect = New SKRectI(0, 0, bw, bh)
                Using fullMask = CreateSolidMask(bw, bh)
                    Dim inverted = CombineSelectionMasks(fullMask, fullRect, existingMask, existingRect, fullRect, "Subtract")
                    If inverted Is Nothing OrElse Not MaskHasVisiblePixels(inverted) Then
                        inverted?.Dispose()
                        ClearSelection(captureUndo:=False)
                        Return
                    End If

                    ClearSelectionMask()
                    SetSelectionBoundsFromPixels(fullRect)
                    SetSelectionShape("MagicWand", Nothing, Nothing)
                    SetSelectionMaskData(inverted, fullRect)
                    HasActiveSelection = True
                End Using
            End Using
        End Sub

        Private Sub ApplySelectionCandidate(candidateMask As SKBitmap,
                                            candidateRect As SKRectI,
                                            candidateShapeMode As String,
                                            candidateXsPercent As Double(),
                                            candidateYsPercent As Double())
            If candidateMask Is Nothing OrElse candidateRect.Width <= 0 OrElse candidateRect.Height <= 0 Then Return
            Dim combineMode = If(_hasActiveSelection, _selectionCombineMode, "New")

            If combineMode = "New" Then
                ClearSelectionMask()
                SetSelectionBoundsFromPixels(candidateRect)
                SetSelectionShape(candidateShapeMode, candidateXsPercent, candidateYsPercent)
                SetSelectionMaskData(candidateMask.Copy(), candidateRect)
                HasActiveSelection = True
                Return
            End If

            Using existingMask = BuildCurrentSelectionMask()
                Dim existingRect = SelectionRectPixels()
                If existingMask Is Nothing OrElse existingRect.Width <= 0 OrElse existingRect.Height <= 0 Then
                    If combineMode = "Subtract" OrElse combineMode = "Intersect" Then
                        ClearSelection(captureUndo:=False)
                    Else
                        ClearSelectionMask()
                        SetSelectionBoundsFromPixels(candidateRect)
                        SetSelectionShape(candidateShapeMode, candidateXsPercent, candidateYsPercent)
                        SetSelectionMaskData(candidateMask.Copy(), candidateRect)
                        HasActiveSelection = True
                    End If
                    Return
                End If

                Dim resultRect As SKRectI
                Select Case combineMode
                    Case "Intersect"
                        resultRect = New SKRectI(Math.Max(existingRect.Left, candidateRect.Left),
                                                 Math.Max(existingRect.Top, candidateRect.Top),
                                                 Math.Min(existingRect.Right, candidateRect.Right),
                                                 Math.Min(existingRect.Bottom, candidateRect.Bottom))
                        If resultRect.Width <= 0 OrElse resultRect.Height <= 0 Then
                            ClearSelection(captureUndo:=False)
                            Return
                        End If
                    Case "Subtract"
                        resultRect = existingRect
                    Case Else
                        resultRect = New SKRectI(Math.Min(existingRect.Left, candidateRect.Left),
                                                 Math.Min(existingRect.Top, candidateRect.Top),
                                                 Math.Max(existingRect.Right, candidateRect.Right),
                                                 Math.Max(existingRect.Bottom, candidateRect.Bottom))
                End Select

                Dim combined = CombineSelectionMasks(existingMask, existingRect, candidateMask, candidateRect, resultRect, combineMode)
                If combined Is Nothing OrElse Not MaskHasVisiblePixels(combined) Then
                    combined?.Dispose()
                    ClearSelection(captureUndo:=False)
                    Return
                End If

                ClearSelectionMask()
                SetSelectionBoundsFromPixels(resultRect)
                SetSelectionShape("MagicWand", Nothing, Nothing)
                SetSelectionMaskData(combined, resultRect)
                HasActiveSelection = True
            End Using
        End Sub

        Private Function BuildCurrentSelectionMask() As SKBitmap
            If Not _hasActiveSelection Then Return Nothing
            If _selectionMask IsNot Nothing Then Return _selectionMask.Copy()
            Dim rectPx = SelectionRectPixels()
            If rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then Return Nothing
            Return CreateSolidMask(rectPx.Width, rectPx.Height)
        End Function

        Private Shared Function CreateSolidMask(width As Integer, height As Integer) As SKBitmap
            Dim mask = New SKBitmap(width, height, SKColorType.Alpha8, SKAlphaType.Premul)
            ' Nicht über Enumerable.Repeat(...).ToArray(): das baute bei einer bildgroßen Maske (24 MP) ein
            ' 24-MB-Array über einen Iterator auf. Erase füllt den Puffer direkt.
            mask.Erase(New SKColor(0, 0, 0, 255))
            Return mask
        End Function

        ''' <summary>Verknüpft zwei Alpha8-Masken (Hinzufügen/Abziehen/Schnittmenge) pixelgenau - kein
        ''' Sampling, kein Runden: jedes Ergebnispixel entsteht aus genau den beiden Quellpixeln an derselben
        ''' BILDkoordinate. Die Masken decken unterschiedliche Bildausschnitte ab, deshalb die Rechnerei mit
        ''' den Rechtecken.
        '''
        ''' Zeilenweise statt Pixel-für-Pixel: pro Zeile wird EINMAL ausgerechnet, welcher Abschnitt von
        ''' beiden Masken überhaupt überdeckt wird, der Rest der Zeile ist per Definition 0 bzw. eine reine
        ''' Kopie. Die alte Fassung rief für JEDES Pixel zweimal eine Funktion mit vier Bereichsprüfungen -
        ''' auf einem 24-MP-Bild waren das 1,4 Sekunden auf dem UI-Thread (gemessen), bei jedem Klick auf
        ''' Hinzufügen/Abziehen/Umkehren.</summary>
        Private Shared Function CombineSelectionMasks(existingMask As SKBitmap,
                                                      existingRect As SKRectI,
                                                      candidateMask As SKBitmap,
                                                      candidateRect As SKRectI,
                                                      resultRect As SKRectI,
                                                      combineMode As String) As SKBitmap
            Dim result = New SKBitmap(resultRect.Width, resultRect.Height, SKColorType.Alpha8, SKAlphaType.Premul)
            Dim existingBytes = ReadMaskBytes(existingMask)
            Dim candidateBytes = ReadMaskBytes(candidateMask)
            Dim eStride = existingMask.RowBytes, eW = existingMask.Width, eH = existingMask.Height
            Dim cStride = candidateMask.RowBytes, cW = candidateMask.Width, cH = candidateMask.Height
            Dim resultStride = result.RowBytes
            Dim resultBytes = New Byte(resultStride * result.Height - 1) {}

            ' Versatz der jeweiligen Maske gegenüber dem Ergebnisrechteck (in Ergebnis-Spalten/-Zeilen).
            Dim eDx = resultRect.Left - existingRect.Left, eDy = resultRect.Top - existingRect.Top
            Dim cDx = resultRect.Left - candidateRect.Left, cDy = resultRect.Top - candidateRect.Top

            ' Spaltenbereiche, in denen die jeweilige Maske Pixel hat (halboffen: [von, bis)).
            Dim eColFrom = Math.Max(0, -eDx), eColTo = Math.Min(resultRect.Width, eW - eDx)
            Dim cColFrom = Math.Max(0, -cDx), cColTo = Math.Min(resultRect.Width, cW - cDx)

            Dim mode = If(combineMode, "")

            For y = 0 To resultRect.Height - 1
                Dim rRow = y * resultStride
                Dim eY = y + eDy, cY = y + cDy
                Dim eRow = If(eY >= 0 AndAlso eY < eH, eY * eStride, -1)
                Dim cRow = If(cY >= 0 AndAlso cY < cH, cY * cStride, -1)

                Select Case mode
                    Case "Add"
                        ' Bestehendes übernehmen, Kandidat drüberlegen (Maximum) - beides nur dort, wo es liegt.
                        If eRow >= 0 Then
                            For x = eColFrom To eColTo - 1
                                resultBytes(rRow + x) = existingBytes(eRow + x + eDx)
                            Next
                        End If
                        If cRow >= 0 Then
                            For x = cColFrom To cColTo - 1
                                Dim b = candidateBytes(cRow + x + cDx)
                                If b > resultBytes(rRow + x) Then resultBytes(rRow + x) = b
                            Next
                        End If

                    Case "Subtract"
                        ' Ergebnis ist das Bestehende minus dem Kandidaten; außerhalb des Bestehenden: 0.
                        If eRow >= 0 Then
                            For x = eColFrom To eColTo - 1
                                resultBytes(rRow + x) = existingBytes(eRow + x + eDx)
                            Next
                            If cRow >= 0 Then
                                Dim from = Math.Max(eColFrom, cColFrom), too = Math.Min(eColTo, cColTo)
                                For x = from To too - 1
                                    Dim a = CInt(resultBytes(rRow + x)) - CInt(candidateBytes(cRow + x + cDx))
                                    resultBytes(rRow + x) = CByte(Math.Max(0, a))
                                Next
                            End If
                        End If

                    Case "Intersect"
                        ' Nur wo BEIDE liegen, sonst 0 (Array ist bereits genullt).
                        If eRow >= 0 AndAlso cRow >= 0 Then
                            Dim from = Math.Max(eColFrom, cColFrom), too = Math.Min(eColTo, cColTo)
                            For x = from To too - 1
                                Dim a = existingBytes(eRow + x + eDx)
                                Dim b = candidateBytes(cRow + x + cDx)
                                resultBytes(rRow + x) = If(a < b, a, b)
                            Next
                        End If

                    Case Else
                        ' "New": nur der Kandidat zählt.
                        If cRow >= 0 Then
                            For x = cColFrom To cColTo - 1
                                resultBytes(rRow + x) = candidateBytes(cRow + x + cDx)
                            Next
                        End If
                End Select
            Next

            Marshal.Copy(resultBytes, 0, result.GetPixels(), resultBytes.Length)
            Return result
        End Function

        Private Shared Function ReadMaskBytes(mask As SKBitmap) As Byte()
            Dim buffer = New Byte(mask.RowBytes * mask.Height - 1) {}
            Marshal.Copy(mask.GetPixels(), buffer, 0, buffer.Length)
            Return buffer
        End Function

        ''' <summary>Ist überhaupt noch etwas ausgewählt? Liest die Maske direkt aus dem unverwalteten
        ''' Speicher, statt sie erst in ein Byte-Array zu kopieren (bei 24 MP wären das 24 MB nur für die
        ''' Frage „irgendein Pixel &gt; 0?").</summary>
        Private Shared Function MaskHasVisiblePixels(mask As SKBitmap) As Boolean
            If mask Is Nothing OrElse mask.Width <= 0 OrElse mask.Height <= 0 Then Return False
            Dim span = mask.GetPixelSpan()
            For i = 0 To span.Length - 1
                If span(i) > 0 Then Return True
            Next
            Return False
        End Function

        ' Gecacht, weil GetCurrentAdjustments (und damit dies) bei aktivem Auswahl-Skopus pro Vorschau-Frame
        ' läuft - ohne Cache würde die Maske jedes Frame neu als PNG kodiert. Wird nur bei Masken-Änderung
        ' in SetSelectionMaskData/ClearSelectionMask neu befüllt.
        Private Function EncodeSelectionMaskBase64() As String
            Return _selectionMaskBase64
        End Function

        Private Shared Function EncodeMaskBitmapToBase64(mask As SKBitmap) As String
            If mask Is Nothing Then Return ""
            Try
                Using image = SKImage.FromBitmap(mask)
                    Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                        Return Convert.ToBase64String(data.ToArray())
                    End Using
                End Using
            Catch
                Return ""
            End Try
        End Function

        Private Shared Function DecodeSelectionMaskBase64(value As String) As SKBitmap
            If String.IsNullOrWhiteSpace(value) Then Return Nothing
            Try
                Dim bytes = Convert.FromBase64String(value)
                Return SKBitmap.Decode(bytes)
            Catch
                Return Nothing
            End Try
        End Function

        Public Sub MoveSelection(deltaXPercent As Double, deltaYPercent As Double)
            If Not _hasActiveSelection Then Return
            Dim maxX = Math.Max(0, 100.0 - _selectionWidthPercent)
            Dim maxY = Math.Max(0, 100.0 - _selectionHeightPercent)
            Dim newX = Math.Max(0, Math.Min(maxX, _selectionXPercent + deltaXPercent))
            Dim newY = Math.Max(0, Math.Min(maxY, _selectionYPercent + deltaYPercent))
            Dim actualDx = newX - _selectionXPercent
            Dim actualDy = newY - _selectionYPercent
            If Math.Abs(actualDx) < 0.0001 AndAlso Math.Abs(actualDy) < 0.0001 Then Return
            CaptureUndoState("Auswahl")

            SelectionXPercent = newX
            SelectionYPercent = newY

            If _selectionShapePointsX IsNot Nothing AndAlso _selectionShapePointsY IsNot Nothing Then
                _selectionShapePointsX = _selectionShapePointsX.Select(Function(x) Math.Max(0, Math.Min(100, x + actualDx))).ToArray()
                _selectionShapePointsY = _selectionShapePointsY.Select(Function(y) Math.Max(0, Math.Min(100, y + actualDy))).ToArray()
                Me.RaisePropertyChanged(NameOf(SelectionShapePointsX))
                Me.RaisePropertyChanged(NameOf(SelectionShapePointsY))
            End If

            If Not _selectionMaskRect.IsEmpty Then
                Dim bw = GetBaseWidth()
                Dim bh = GetBaseHeight()
                If bw > 0 AndAlso bh > 0 Then
                    ' Die neue Maskenposition AUS DER absoluten Prozentposition ableiten, nicht die gerundete
                    ' Einzelverschiebung aufaddieren: beim langsamen Ziehen sind die Schritte Bruchteile eines
                    ' Pixels und runden jedes Mal auf 0 - das Rechteck (und die Ameisenlinie) wanderte dann,
                    ' während die Maske stehenblieb. Breite/Höhe kommen aus der Maske selbst, damit die
                    ' Bitmap-Maße unangetastet bleiben.
                    Dim left = CInt(Math.Round(bw * _selectionXPercent / 100.0))
                    Dim top = CInt(Math.Round(bh * _selectionYPercent / 100.0))
                    _selectionMaskRect = New SKRectI(left, top,
                                                     left + _selectionMaskRect.Width,
                                                     top + _selectionMaskRect.Height)
                End If
            End If
        End Sub

        ''' Schneidet den aktuell verarbeiteten Bildinhalt (alle Anpassungen/Objekte gebacken) auf
        ''' das Auswahlrechteck zu, sichert ihn als temporäre PNG (Muster wie VideoPreviewService)
        ''' und legt ihn per AddImageAnnotationAt als neues, frei verschiebbares Bild-Objekt an.
        ''' Schneidet den aktuell verarbeiteten Bildinhalt (alle Anpassungen/Objekte gebacken) auf
        ''' das Auswahlrechteck zu und sichert ihn als temporäre PNG (Muster wie VideoPreviewService) -
        ''' gemeinsame Grundlage für den direkten "Kopieren"-Button und für Strg+C/Strg+V.
        Private Function CropSelectionToTempFile() As String
            If Not _hasActiveSelection OrElse String.IsNullOrWhiteSpace(_currentImagePath) Then Return Nothing
            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return Nothing

            Dim left = CInt(Math.Round(baseWidth * _selectionXPercent / 100.0))
            Dim top = CInt(Math.Round(baseHeight * _selectionYPercent / 100.0))
            Dim width = CInt(Math.Round(baseWidth * _selectionWidthPercent / 100.0))
            Dim height = CInt(Math.Round(baseHeight * _selectionHeightPercent / 100.0))
            If width <= 0 OrElse height <= 0 Then Return Nothing

            Dim tempPath = IO.Path.Combine(IO.Path.GetTempPath(), $"ferrumpix_selection_{Guid.NewGuid():N}.png")
            Dim adj = GetCurrentAdjustments()
            If _selectionMask IsNot Nothing Then
                ' Unregelmäßige Auswahl (Ellipse/Lasso/Zauberstab): mit Maske freischneiden.
                If Not ImageProcessor.ExtractRegionToFileMasked(_currentImagePath, adj, _selectionMaskRect, _selectionMask, tempPath) Then Return Nothing
            Else
                Dim pixelRect = New SKRectI(left, top, left + width, top + height)
                If Not ImageProcessor.ExtractRegionToFile(_currentImagePath, adj, pixelRect, tempPath) Then Return Nothing
            End If
            Return tempPath
        End Function

        Private _nextSelectionObjectNumber As Integer = 1

        ''' Monoton hochgezählte Nummer für die Objektlisten-Bezeichnung "Auswahl N" (Kopien/Füllungen
        ''' aus dem Auswahl-Werkzeug) - zählt NUR hoch, wird beim Löschen eines Objekts nicht wieder
        ''' freigegeben, damit Nummern nicht doppelt vergeben werden.
        Private Function NextSelectionObjectLabel() As String
            Dim label = "Auswahl " & _nextSelectionObjectNumber.ToString()
            _nextSelectionObjectNumber += 1
            Return label
        End Function

        ''' Legt ein Bild-Objekt mit EXAKT der übergebenen Größe an (keine Skalierung/Kappung wie bei
        ''' AddImageAnnotationAt, das für das Einfügen beliebiger externer Bilddateien eine Kappung auf
        ''' 60% der Basisbildgröße vornimmt) - für Auswahl-Kopien muss die Größe exakt der Auswahl
        ''' entsprechen, sonst wirkt die Kopie kleiner/größer als das aufgezogene Rechteck.
        Private Sub AddSelectionImageAnnotationAt(imagePath As String, xPercent As Double, yPercent As Double, widthPercent As Double, heightPercent As Double)
            If String.IsNullOrWhiteSpace(imagePath) Then Return
            PushUndo()
            Dim annotation = New ImageAnnotation With {
                .Kind = "SelectionImage",
                .Text = NextSelectionObjectLabel(),
                .ImagePath = imagePath,
                .XPixels = CSng(PercentXToPixels(xPercent)),
                .YPixels = CSng(PercentYToPixels(yPercent)),
                .WidthPixels = CSng(Math.Max(1.0, PercentXToPixels(widthPercent))),
                .HeightPixels = CSng(Math.Max(1.0, PercentYToPixels(heightPercent))),
                .FillColor = "#00FFFFFF",
                .StrokeColor = _annotationStrokeColor,
                .StrokeWidth = CSng(_annotationStrokeWidth),
                .FontSizePixels = CSng(_annotationFontSize),
                .FontFamily = _annotationFontFamily,
                .Opacity = CSng(_annotationOpacity),
                .RotationDegrees = CSng(_annotationRotation),
                .IsVisible = _annotationIsVisible
            }
            _annotations.Add(annotation)
            SelectedAnnotationIndex = _annotations.Count - 1
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Public Sub CopySelectionToNewObject()
            Dim tempPath = CropSelectionToTempFile()
            If tempPath Is Nothing Then Return
            AddSelectionImageAnnotationAt(tempPath, _selectionXPercent, _selectionYPercent, _selectionWidthPercent, _selectionHeightPercent)
            AddHistoryEntry("Auswahl kopiert")
        End Sub

        Private _pendingColorPickCallback As Action(Of Avalonia.Media.Color) = Nothing
        Private _isPickingColorFromImage As Boolean = False

        ''' Ob die Pipette gerade auf den nächsten Klick auf das Bild wartet - EditorView zeigt in
        ''' diesem Fall einen Fadenkreuz-Cursor und fängt den nächsten Canvas-Klick ab, statt ihn an das
        ''' aktuell aktive Werkzeug weiterzureichen (siehe OnSliderPointerPressed).
        Public Property IsPickingColorFromImage As Boolean
            Get
                Return _isPickingColorFromImage
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isPickingColorFromImage, value)
            End Set
        End Property

        Private _colorPickPreview As Avalonia.Media.Color? = Nothing

        ''' Die Farbe unter der Pipette, solange sie über dem Bild schwebt - EditorView schreibt sie bei
        ''' jeder Mausbewegung, der ColorMixer zeigt sie in seinem Vorschaukreis. Nothing, sobald der
        ''' Zeiger das Bild verlässt oder die Aufnahme endet.
        Public Property ColorPickPreview As Avalonia.Media.Color?
            Get
                Return _colorPickPreview
            End Get
            Set(value As Avalonia.Media.Color?)
                If Nullable.Equals(_colorPickPreview, value) Then Return
                _colorPickPreview = value
                Me.RaisePropertyChanged(NameOf(ColorPickPreview))
            End Set
        End Property

        ''' Von ColorPickerButton.OnEyedropperClick aufgerufen: merkt sich, WELCHE Farbe (per Closure)
        ''' beim nächsten Bildklick gesetzt werden soll, statt eine feste ViewModel-Farbe zu kennen -
        ''' dadurch bleibt die Pipette für jede beliebige ColorPickerButton-Instanz wiederverwendbar.
        Public Sub BeginColorPick(onPicked As Action(Of Avalonia.Media.Color))
            _pendingColorPickCallback = onPicked
            ColorPickPreview = Nothing
            IsPickingColorFromImage = True
        End Sub

        Public Sub CompleteColorPick(color As Avalonia.Media.Color)
            Dim callback = _pendingColorPickCallback
            _pendingColorPickCallback = Nothing
            ColorPickPreview = Nothing
            IsPickingColorFromImage = False
            callback?.Invoke(color)
        End Sub

        Public Sub CancelColorPick()
            _pendingColorPickCallback = Nothing
            ColorPickPreview = Nothing
            IsPickingColorFromImage = False
            EndNegativePickSuppression()
        End Sub

        ''' Startet die Pipette für die Filmbasis. Die Vorschau zeigt dafür wieder das rohe Negativ, denn
        ''' die Pipette nimmt die Farbe vom Bildschirm - im bereits umgerechneten Positiv gäbe es den
        ''' orangen Filmträger, den der Nutzer anklicken soll, gar nicht mehr zu sehen.
        Private Sub BeginNegativeBasePick()
            If _negativeEnabled Then
                _suppressNegativeForPick = True
                ScheduleToolPreviewUpdate()
            End If
            BeginColorPick(Sub(picked)
                               _suppressNegativeForPick = False
                               PushUndo()
                               _negativeBaseColor = $"#FF{picked.R:X2}{picked.G:X2}{picked.B:X2}"
                               ' Der Dichtepunkt (das andere Ende der Kurve) bleibt gemessen - von Hand
                               ' ist er nicht sinnvoll zu treffen, er liegt irgendwo im Motiv.
                               If String.IsNullOrWhiteSpace(_negativeDensityColor) Then MeasureFilmNegative(baseToo:=False)
                               _negativeEnabled = True
                               RaiseNegativePropertiesChanged()
                               SchedulePreviewUpdate()
                           End Sub)
        End Sub

        Private Sub EndNegativePickSuppression()
            If Not _suppressNegativeForPick Then Return
            _suppressNegativeForPick = False
            ScheduleToolPreviewUpdate()
        End Sub

        ''' Vermisst den Scan: Filmbasis (hellste Stelle = unbelichteter Träger) und dichtester Punkt.
        ''' Bewusst EINMAL im ViewModel statt bei jedem Rendern im Prozessor - die Vorschau ist kleiner
        ''' als das Original, eine erneute Messung beim Export würde sonst minimal andere Werte liefern
        ''' und das gespeicherte Bild anders aussehen lassen als die Vorschau.
        Private Sub MeasureFilmNegative(Optional baseToo As Boolean = True)
            Dim source = GetPreviewSource()
            If source Is Nothing Then Return
            Dim measured = ImageProcessor.AnalyzeFilmNegative(source, GetCurrentAdjustments(forPreview:=True))
            If baseToo Then _negativeBaseColor = SkColorToHex(measured.BaseColor)
            _negativeDensityColor = SkColorToHex(measured.DensityColor)
        End Sub

        Private Shared Function SkColorToHex(color As SKColor) As String
            Return $"#FF{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
        End Function

        Private Sub RaiseNegativePropertiesChanged()
            Me.RaisePropertyChanged(NameOf(NegativeEnabled))
            Me.RaisePropertyChanged(NameOf(NegativeMonochrome))
            Me.RaisePropertyChanged(NameOf(NegativeGamma))
            Me.RaisePropertyChanged(NameOf(NegativeBaseBrush))
            Me.RaisePropertyChanged(NameOf(HasNegativeBaseColor))
        End Sub

        Private Sub ResetNegativeInternal()
            _negativeEnabled = False
            _negativeMonochrome = False
            _negativeBaseColor = ""
            _negativeDensityColor = ""
            _negativeGamma = 0
            RaiseNegativePropertiesChanged()
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private _selectionClipboardPath As String = Nothing
        Private _selectionClipboardXPercent As Double = 0
        Private _selectionClipboardYPercent As Double = 0
        Private _selectionClipboardWidthPercent As Double = 0
        Private _selectionClipboardHeightPercent As Double = 0
        Private _selectionClipboardPasteCount As Integer = 0

        ''' Strg+C im Auswahl-Werkzeug: schneidet die Auswahl zu und merkt sie sich (samt Ursprungsposition
        ''' und -größe) als "Zwischenablage", statt sofort ein Objekt anzulegen - Strg+V fügt sie danach
        ''' beliebig oft ein.
        Public Sub CopySelectionToClipboard()
            Dim tempPath = CopySelectionToClipboardFile()
            If tempPath Is Nothing Then Return
            AddHistoryEntry("Auswahl kopiert")
        End Sub

        Public Function CopySelectionToClipboardFile() As String
            Dim tempPath = CropSelectionToTempFile()
            If tempPath Is Nothing Then Return Nothing
            _selectionClipboardPath = tempPath
            _selectionClipboardXPercent = _selectionXPercent
            _selectionClipboardYPercent = _selectionYPercent
            _selectionClipboardWidthPercent = _selectionWidthPercent
            _selectionClipboardHeightPercent = _selectionHeightPercent
            _selectionClipboardPasteCount = 0
            Return tempPath
        End Function

        Public Sub PasteSelectionClipboard()
            If String.IsNullOrWhiteSpace(_selectionClipboardPath) OrElse Not File.Exists(_selectionClipboardPath) Then Return
            _selectionClipboardPasteCount += 1
            Dim offset = 3.0 * _selectionClipboardPasteCount
            AddSelectionImageAnnotationAt(_selectionClipboardPath, _selectionClipboardXPercent + offset, _selectionClipboardYPercent + offset, _selectionClipboardWidthPercent, _selectionClipboardHeightPercent)
            AddHistoryEntry("Auswahl eingefügt")
        End Sub

        ''' Füllt die aktive Auswahl mit Vollfarbe oder Verlauf, indem ein neues, randloses
        ''' Rechteck-Objekt exakt in Größe/Position der Auswahl angelegt wird - so bleibt die Füllung
        ''' wie jedes andere Objekt beweglich/löschbar/mit Opacity versehen (kein separater,
        ''' nicht-destruktiver Fill-Pipeline-Mechanismus).
        Public Sub FillSelection()
            If Not _hasActiveSelection Then Return

            ' Unregelmäßige Auswahl: die Füllung entsteht als maskierte PNG und wird als bewegliches
            ' Bild-Objekt eingefügt (Vollfarbe). Verläufe bleiben dem Rechteck vorbehalten.
            If _selectionMask IsNot Nothing Then
                Dim tempPath = IO.Path.Combine(IO.Path.GetTempPath(), $"ferrumpix_fill_{Guid.NewGuid():N}.png")
                If ImageProcessor.RenderMaskedFillToFile(_selectionMask, _annotationFillColor, tempPath) Then
                    AddSelectionImageAnnotationAt(tempPath, _selectionXPercent, _selectionYPercent, _selectionWidthPercent, _selectionHeightPercent)
                    AddHistoryEntry("Auswahl gefüllt")
                End If
                Return
            End If

            PushUndo()
            Dim annotation = New ImageAnnotation With {
                .Kind = "SelectionFill",
                .Text = NextSelectionObjectLabel(),
                .ImagePath = "",
                .XPixels = CSng(PercentXToPixels(_selectionXPercent)),
                .YPixels = CSng(PercentYToPixels(_selectionYPercent)),
                .WidthPixels = CSng(Math.Max(1.0, PercentXToPixels(_selectionWidthPercent))),
                .HeightPixels = CSng(Math.Max(1.0, PercentYToPixels(_selectionHeightPercent))),
                .FillColor = _annotationFillColor,
                .FillKind = _annotationFillKind,
                .FillColor2 = _annotationFillColor2,
                .GradientAngleDegrees = CSng(_annotationGradientAngle),
                .GradientInverted = _annotationGradientInverted,
                .StrokeColor = _annotationStrokeColor,
                .StrokeWidth = 0,
                .Opacity = CSng(_annotationOpacity),
                .IsVisible = True
            }
            _annotations.Add(annotation)
            SelectedAnnotationIndex = _annotations.Count - 1
            RaiseResetButtonStateChanged()
            AddHistoryEntry("Auswahl gefüllt")
            SchedulePreviewUpdate()
        End Sub

        ' Setzt X/Y/Breite/Höhe in einem Rutsch (z.B. beim Ziehen/Skalieren im Canvas), damit
        ' SyncSelectedAnnotation (inkl. Undo-Erfassung und Vorschau-Neuberechnung) nur einmal statt
        ' viermal pro Zieh-Frame läuft.
        Public Sub SetSelectedAnnotationRect(xPercent As Double, yPercent As Double, widthPercent As Double, heightPercent As Double)
            ' Die View ruft dieselbe Methode fürs Verschieben wie fürs Größenändern. Nur im zweiten Fall
            ' muss das Overlay-Bitmap neu gezeichnet werden (siehe SyncSelectedAnnotation).
            Dim previousWidth = _annotationWidthPercent
            Dim previousHeight = _annotationHeightPercent
            ' Textobjekte umschließen ihre Glyphen eng, für sie gelten kleinere Untergrenzen als für Formen -
            ' sonst schnappt der Rahmen beim ersten Ziehen an einem Griff auf 5%/4% auf. Ein Wasserzeichen aus
            ' einer Bilddatei ist kein Text und bleibt bei den Formwerten.
            Dim isTextual = IsTextualAnnotationKind(EffectiveAnnotationKind) AndAlso Not IsWatermarkImageSource
            Dim minWidth = If(isTextual, MinTextAnnotationWidthPercent, 5.0)
            Dim minHeight = If(isTextual, MinTextAnnotationHeightPercent, 4.0)
            _annotationWidthPercent = Math.Max(minWidth, Math.Min(90, widthPercent))
            _annotationHeightPercent = Math.Max(minHeight, Math.Min(90, heightPercent))
            If EffectiveAnnotationKind = "QR" Then
                Dim baseWidth = GetBaseWidth()
                Dim baseHeight = GetBaseHeight()
                If baseWidth > 0 AndAlso baseHeight > 0 Then
                    Dim widthPixels = baseWidth * _annotationWidthPercent / 100.0
                    Dim heightPixels = baseHeight * _annotationHeightPercent / 100.0
                    Dim sizePixels = Math.Max(1.0, Math.Min(widthPixels, heightPixels))
                    _annotationWidthPercent = Math.Max(5, Math.Min(90, sizePixels / baseWidth * 100.0))
                    _annotationHeightPercent = Math.Max(4, Math.Min(90, sizePixels / baseHeight * 100.0))
                End If
            End If
            ' Ein Textobjekt hat keine freie Box: sie ist immer der gemessene Textkasten. Der Griff ändert
            ' den Schriftgrad (das macht die View im Anschluss), das Rechteck folgt der Schrift. Ohne das
            ' bliebe hier die gezogene Größe stehen, sobald der Schriftgrad auf denselben ganzen Pixel
            ' gerundet wird - und der Rahmen stünde wieder neben dem Text.
            If isTextual Then
                Dim fitted = EstimateTextAnnotationSizePercent(_annotationText, _annotationFontSize, _annotationFontFamily)
                _annotationWidthPercent = fitted.WidthPercent
                _annotationHeightPercent = fitted.HeightPercent
            End If
            If ShowWatermarkAnchorControls Then
                Dim offset = ComputeAnnotationOffsetPercent(EffectiveAnnotationKind, xPercent, yPercent, _annotationWidthPercent, _annotationHeightPercent, _annotationAnchor)
                _annotationXPercent = ClampAnnotationOffsetPercent(offset.X)
                _annotationYPercent = ClampAnnotationOffsetPercent(offset.Y)
            Else
                _annotationXPercent = ClampAnnotationPositionPercent(xPercent, _annotationWidthPercent)
                _annotationYPercent = ClampAnnotationPositionPercent(yPercent, _annotationHeightPercent)
            End If
            Me.RaisePropertyChanged(NameOf(AnnotationXPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationYPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationXPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationYPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
            RaiseAnnotationPositionControlProperties()
            Dim sizeChanged = Math.Abs(previousWidth - _annotationWidthPercent) > 0.0001 OrElse
                              Math.Abs(previousHeight - _annotationHeightPercent) > 0.0001
            SyncSelectedAnnotation(refreshOverlay:=sizeChanged)
        End Sub

        Private Sub RaiseAnnotationSizeChanged()
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthSliderMinimum))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthSliderMaximum))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightSliderMinimum))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightSliderMaximum))
            RaiseAnnotationPositionControlProperties()
        End Sub

        Public Sub SetSelectedAnnotationRectPixels(xPixels As Double, yPixels As Double, widthPixels As Double, heightPixels As Double)
            Dim baseWidth = Math.Max(1, GetBaseWidth())
            Dim baseHeight = Math.Max(1, GetBaseHeight())
            SetSelectedAnnotationRect(
                xPixels / baseWidth * 100.0,
                yPixels / baseHeight * 100.0,
                widthPixels / baseWidth * 100.0,
                heightPixels / baseHeight * 100.0)
        End Sub

        Public Function GetSelectedAnnotationDisplayRectPercent() As (X As Double, Y As Double, Width As Double, Height As Double)
            Return GetCurrentAnnotationDisplayRectPercent()
        End Function

        Private Function AnnotationStoredXToPercent(annotation As ImageAnnotation) As Double
            If annotation Is Nothing Then Return 0
            Dim baseWidth = GetBaseWidth()
            If baseWidth <= 0 Then Return 0
            Return annotation.XPixels / CDbl(baseWidth) * 100.0
        End Function

        Private Function AnnotationStoredYToPercent(annotation As ImageAnnotation) As Double
            If annotation Is Nothing Then Return 0
            Dim baseHeight = GetBaseHeight()
            If baseHeight <= 0 Then Return 0
            Return annotation.YPixels / CDbl(baseHeight) * 100.0
        End Function

        Private Function AnnotationStoredWidthToPercent(annotation As ImageAnnotation) As Double
            If annotation Is Nothing Then Return 0
            Dim baseWidth = GetBaseWidth()
            If baseWidth <= 0 Then Return 0
            Return annotation.WidthPixels / CDbl(baseWidth) * 100.0
        End Function

        Private Function AnnotationStoredHeightToPercent(annotation As ImageAnnotation) As Double
            If annotation Is Nothing Then Return 0
            Dim baseHeight = GetBaseHeight()
            If baseHeight <= 0 Then Return 0
            Return annotation.HeightPixels / CDbl(baseHeight) * 100.0
        End Function

        Private Function PercentXToPixels(value As Double) As Double
            Return GetBaseWidth() * value / 100.0
        End Function

        Private Function PercentYToPixels(value As Double) As Double
            Return GetBaseHeight() * value / 100.0
        End Function

        Public Property AnnotationXPixels As Integer
            Get
                Return CInt(Math.Round(GetBaseWidth() * _annotationXPercent / 100.0))
            End Get
            Set(value As Integer)
                Dim baseWidth = GetBaseWidth()
                If baseWidth <= 0 Then Return
                AnnotationXPercent = value / CDbl(baseWidth) * 100.0
            End Set
        End Property

        Public Property AnnotationYPixels As Integer
            Get
                Return CInt(Math.Round(GetBaseHeight() * _annotationYPercent / 100.0))
            End Get
            Set(value As Integer)
                Dim baseHeight = GetBaseHeight()
                If baseHeight <= 0 Then Return
                AnnotationYPercent = value / CDbl(baseHeight) * 100.0
            End Set
        End Property

        Public Property AnnotationWidthPixels As Integer
            Get
                Return CInt(Math.Round(GetBaseWidth() * _annotationWidthPercent / 100.0))
            End Get
            Set(value As Integer)
                Dim baseWidth = GetBaseWidth()
                If baseWidth <= 0 Then Return
                AnnotationWidthPercent = value / CDbl(baseWidth) * 100.0
            End Set
        End Property

        Public Property AnnotationHeightPixels As Integer
            Get
                Return CInt(Math.Round(GetBaseHeight() * _annotationHeightPercent / 100.0))
            End Get
            Set(value As Integer)
                Dim baseHeight = GetBaseHeight()
                If baseHeight <= 0 Then Return
                AnnotationHeightPercent = value / CDbl(baseHeight) * 100.0
            End Set
        End Property

        Public Property StraightenDegrees As Double
            Get
                Return _straightenDegrees
            End Get
            Set(value As Double)
                Dim clamped = Math.Max(-180, Math.Min(180, value))
                If Math.Abs(_straightenDegrees - clamped) < 0.0001 Then Return
                Me.RaiseAndSetIfChanged(_straightenDegrees, clamped)
                RaiseResetButtonStateChanged()
                ScheduleToolPreviewUpdate()
            End Set
        End Property

        Public Property StraightenExpandCanvas As Boolean
            Get
                Return _straightenExpandCanvas
            End Get
            Set(value As Boolean)
                If _straightenExpandCanvas = value Then Return
                Me.RaiseAndSetIfChanged(_straightenExpandCanvas, value)
                RaiseResetButtonStateChanged()
                ScheduleToolPreviewUpdate()
            End Set
        End Property

        Public Property WhiteBalance As String
            Get
                Return _whiteBalance
            End Get
            Set(value As String)
                If String.Equals(_whiteBalance, value, StringComparison.Ordinal) Then Return
                CaptureUndoState(NameOf(WhiteBalance))
                Me.RaiseAndSetIfChanged(_whiteBalance, value)
                Dim targetTemperature = _temperature
                Dim targetTint = _tint
                Select Case value
                    Case "Automatisch"
                        targetTemperature = 6
                        targetTint = 0
                    Case "Tageslicht"
                        targetTemperature = 8
                        targetTint = 0
                    Case "Bewölkt"
                        targetTemperature = 18
                        targetTint = 2
                    Case "Schatten"
                        targetTemperature = 28
                        targetTint = 4
                    Case "Glühlampe"
                        targetTemperature = -28
                        targetTint = -2
                    Case "Leuchtstoff"
                        targetTemperature = -12
                        targetTint = 8
                    Case "Blitz"
                        targetTemperature = 10
                        targetTint = 1
                End Select
                If Math.Abs(_temperature - targetTemperature) >= 0.0001 Then
                    _temperature = targetTemperature
                    Me.RaisePropertyChanged(NameOf(Temperature))
                End If
                If Math.Abs(_tint - targetTint) >= 0.0001 Then
                    _tint = targetTint
                    Me.RaisePropertyChanged(NameOf(Tint))
                End If
                RaiseResetButtonStateChanged()
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property Rating As Integer
            Get
                Return _rating
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_rating, value)
                Dim immichAssetId = CurrentImmichAssetId()
                If immichAssetId IsNot Nothing Then
                    Dim ignored = ImmichService.SetRatingAsync(immichAssetId, value)
                ElseIf Not String.IsNullOrEmpty(_currentImagePath) Then
                    LibraryService.Instance.SetRating(_currentImagePath, value, syncToXmp:=True)
                End If
            End Set
        End Property

        Public Property IsFavorite As Boolean
            Get
                Return _isFavorite
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isFavorite, value)
                Dim immichAssetId = CurrentImmichAssetId()
                If immichAssetId IsNot Nothing Then
                    Dim ignored = ImmichService.SetFavoriteAsync(immichAssetId, value)
                ElseIf Not String.IsNullOrEmpty(_currentImagePath) Then
                    LibraryService.Instance.SetFavorite(_currentImagePath, value)
                End If
            End Set
        End Property

        Public Property NewTagText As String
            Get
                Return _newTagText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_newTagText, value)
            End Set
        End Property

        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_statusText, value)
            End Set
        End Property

        Private _previewFailed As Boolean

        ''' <summary>Die letzte Vorschau ist an einer echten Ausnahme gescheitert (nicht an einem Abbruch,
        ''' weil der nächste Reglerwert schon unterwegs war). Die Statuszeile färbt sich daraufhin rot -
        ''' ohne das bliebe das ALTE Bild stehen und meldete "Vorschau bereit", ein defektes Werkzeug wäre
        ''' also nicht von einem zu unterscheiden, das am Bild nichts ändert. Bleibt gesetzt, bis eine
        ''' Vorschau wieder durchläuft.</summary>
        Public Property PreviewFailed As Boolean
            Get
                Return _previewFailed
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_previewFailed, value)
            End Set
        End Property

        ''' <summary>Bildpixel-Koordinate der Maus über dem Bild, für die Fußleiste - leer, wenn die
        ''' Maus das Bild nicht berührt.</summary>
        Public Property MousePositionText As String
            Get
                Return _mousePositionText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_mousePositionText, value)
            End Set
        End Property

        ''' <summary>Zuletzt bewusst gewählter Zoom-Modus (Fit/Actual/Manual) - Pixel-/Pan-Mechanik
        ''' bleibt bewusst im Code-Behind (EditorView.axaml.vb), nur der Modus wandert hierher, damit
        ''' die Fit/100%-Buttons als aktiv markiert werden können und der Modus bei einem Bildwechsel
        ''' erhalten bleibt statt immer auf Fit zurückzuspringen.</summary>
        Public Property ActiveZoomPreset As ZoomPresetMode
            Get
                Return _activeZoomPreset
            End Get
            Set(value As ZoomPresetMode)
                Me.RaiseAndSetIfChanged(_activeZoomPreset, value)
                Me.RaisePropertyChanged(NameOf(IsZoomFitActive))
                Me.RaisePropertyChanged(NameOf(IsZoomActualActive))
            End Set
        End Property

        Public ReadOnly Property IsZoomFitActive As Boolean
            Get
                Return _activeZoomPreset = ZoomPresetMode.Fit
            End Get
        End Property

        Public ReadOnly Property IsZoomActualActive As Boolean
            Get
                Return _activeZoomPreset = ZoomPresetMode.Actual
            End Get
        End Property

        Public Property SaveQuality As Integer
            Get
                If _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing Then
                    Return _mainVm.Settings.JpgSaveQuality
                End If
                Return _saveQuality
            End Get
            Set(value As Integer)
                value = AppSettingsService.NormalizeJpgSaveQuality(value)
                If _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing Then
                    _mainVm.Settings.JpgSaveQuality = value
                    Me.RaisePropertyChanged(NameOf(SaveQuality))
                    Return
                End If
                Me.RaiseAndSetIfChanged(_saveQuality, value)
            End Set
        End Property

        Public Property HistogramImage As Bitmap
            Get
                Return _histogramImage
            End Get
            Set(value As Bitmap)
                Dim previous = _histogramImage
                Me.RaiseAndSetIfChanged(_histogramImage, value)
                If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, value) Then DisposeDeferred(previous)
            End Set
        End Property

        Public Property ExifInfo As ExifData
            Get
                Return _exifInfo
            End Get
            Set(value As ExifData)
                Me.RaiseAndSetIfChanged(_exifInfo, value)
            End Set
        End Property

        Public Property SelectedInfoTab As InfoSidebarTab
            Get
                Return _selectedInfoTab
            End Get
            Set(value As InfoSidebarTab)
                If _selectedInfoTab = value Then Return
                Me.RaiseAndSetIfChanged(_selectedInfoTab, value)
                RaiseInfoTabStateChanged()
            End Set
        End Property

        Public ReadOnly Property IsInfoTabGeneral As Boolean
            Get
                Return _selectedInfoTab = InfoSidebarTab.General
            End Get
        End Property

        Public ReadOnly Property IsInfoTabExif As Boolean
            Get
                Return _selectedInfoTab = InfoSidebarTab.Exif
            End Get
        End Property

        Public ReadOnly Property IsInfoTabIptc As Boolean
            Get
                Return _selectedInfoTab = InfoSidebarTab.Iptc
            End Get
        End Property

        Public ReadOnly Property IsInfoTabXmp As Boolean
            Get
                Return _selectedInfoTab = InfoSidebarTab.Xmp
            End Get
        End Property

        Public ReadOnly Property IsInfoTabIcc As Boolean
            Get
                Return _selectedInfoTab = InfoSidebarTab.Icc
            End Get
        End Property

        Public Property SelectedLayersPanelTab As LayersPanelTab
            Get
                Return _selectedLayersPanelTab
            End Get
            Set(value As LayersPanelTab)
                If _selectedLayersPanelTab = value Then Return
                Me.RaiseAndSetIfChanged(_selectedLayersPanelTab, value)
                Me.RaisePropertyChanged(NameOf(IsToolTabSelected))
                Me.RaisePropertyChanged(NameOf(IsLayersTabSelected))
                Me.RaisePropertyChanged(NameOf(IsHistoryTabSelected))
            End Set
        End Property

        Public ReadOnly Property IsToolTabSelected As Boolean
            Get
                Return _selectedLayersPanelTab = LayersPanelTab.Tool
            End Get
        End Property

        Public ReadOnly Property IsLayersTabSelected As Boolean
            Get
                Return _selectedLayersPanelTab = LayersPanelTab.Layers
            End Get
        End Property

        Public ReadOnly Property IsHistoryTabSelected As Boolean
            Get
                Return _selectedLayersPanelTab = LayersPanelTab.History
            End Get
        End Property

        Public ReadOnly Property CurrentFileName As String
            Get
                If String.IsNullOrEmpty(_currentImagePath) Then Return ""
                Return IO.Path.GetFileName(_currentImagePath)
            End Get
        End Property

        Public ReadOnly Property OutputSizeText As String
            Get
                Dim w = If(_resizeWidth > 0, _resizeWidth, GetCroppedWidth())
                Dim h = If(_resizeHeight > 0, _resizeHeight, GetCroppedHeight())
                w = If(_canvasWidth > 0, _canvasWidth, w)
                h = If(_canvasHeight > 0, _canvasHeight, h)
                If w <= 0 OrElse h <= 0 Then Return ""
                Return $"{w} × {h}"
            End Get
        End Property

        Public ReadOnly Property CanUndo As Boolean
            Get
                Return _undoStack.Count > 0
            End Get
        End Property

        Public ReadOnly Property CanRedo As Boolean
            Get
                Return _redoStack.Count > 0
            End Get
        End Property

        Public ReadOnly Property HasUnsavedChanges As Boolean
            Get
                Return _hasChanges
            End Get
        End Property

        ''' Bearbeitung wirkt bei RAW-Quellen nur auf die eingebettete JPEG-Vorschau (siehe
        ''' ImageProcessor.OpenSourceStream) - die RAW-Datei selbst darf nie als Speicherziel dienen.
        Public ReadOnly Property IsCurrentImageRaw As Boolean
            Get
                Return Not String.IsNullOrEmpty(_currentImagePath) AndAlso RawPreviewService.IsSupportedRaw(_currentImagePath)
            End Get
        End Property

        ''' Steuert, ob der "Speichern"-Button (in-place) aktiv ist - bei RAW-Bildern ist nur
        ''' "Speichern unter" erlaubt (siehe SaveImageAsync/ConfirmSaveBeforeLeavingAsync).
        ''' Immich-Bilder liegen als Temp-Kopie vor (_forceSaveAsOnly): dort ist "Speichern" nur dann
        ''' sinnvoll, wenn es das Quell-Asset ersetzen darf - siehe SavesBackToImmich.
        Public ReadOnly Property CanSaveInPlace As Boolean
            Get
                Return Not IsCurrentImageRaw AndAlso (Not _forceSaveAsOnly OrElse SavesBackToImmich)
            End Get
        End Property

        ''' <summary>True, wenn "Speichern" das Immich-Quell-Asset ersetzt statt eine lokale Datei zu
        ''' schreiben (Einstellung "Vorhandene Assets aktualisieren"). Ohne die Einstellung bleibt bei
        ''' Immich-Bildern nur "Speichern unter" - dann entsteht immer ein neues Asset.</summary>
        Private ReadOnly Property SavesBackToImmich As Boolean
            Get
                Return CurrentImmichAssetId() IsNot Nothing AndAlso AppSettingsService.Load().ImmichUpdateExistingAssets
            End Get
        End Property

        ''' <summary>Die Einstellung "Vorhandene Assets aktualisieren" gibt den Speichern-Knopf für
        ''' Immich-Bilder frei; wird sie bei offenem Bild umgelegt, muss der Editor das mitbekommen.</summary>
        Public Sub RefreshImmichSaveState()
            Me.RaisePropertyChanged(NameOf(CanSaveInPlace))
        End Sub

        ''' <summary>Asset-ID, falls das aktuelle Bild eine Immich-Temp-Kopie ist (Dateiname-Stamm = UUID),
        ''' sonst Nothing. Damit landen Stichwort-Änderungen im Editor beim richtigen Immich-Asset.</summary>
        Private Function CurrentImmichAssetId() As String
            If Not ImmichService.IsImmichTempPath(_currentImagePath) Then Return Nothing
            Dim stem = IO.Path.GetFileNameWithoutExtension(_currentImagePath)
            Return If(String.IsNullOrEmpty(stem), Nothing, stem)
        End Function

        ''' _cropLeft.._cropBottom sind der noch nicht angewendete Beschnitt, gemessen am aktuell
        ''' ANGEZEIGTEN (also bereits beschnittenen) Bild. Ein offener Beschnitt liegt genau dann vor,
        ''' wenn eine der vier Kanten von Null abweicht.
        Public ReadOnly Property HasCropChanges As Boolean
            Get
                Return _cropLeft > 0.0001 OrElse _cropTop > 0.0001 OrElse
                       _cropRight > 0.0001 OrElse _cropBottom > 0.0001
            End Get
        End Property

        Public ReadOnly Property HasImageResizeChanges As Boolean
            Get
                Return _resizeWidth <> _appliedResizeWidth OrElse _resizeHeight <> _appliedResizeHeight
            End Get
        End Property

        Public ReadOnly Property HasCanvasSizeChanges As Boolean
            Get
                Return _canvasWidth <> _appliedCanvasWidth OrElse _canvasHeight <> _appliedCanvasHeight
            End Get
        End Property

        Public ReadOnly Property HasAnnotationChanges As Boolean
            Get
                Return _annotations.Count > 0
            End Get
        End Property

        Public ReadOnly Property HasColorChanges As Boolean
            Get
                Return Not String.Equals(_whiteBalance, "Wie Aufnahme", StringComparison.Ordinal) OrElse
                       _temperature <> 0 OrElse _tint <> 0 OrElse _vibrance <> 0 OrElse _saturation <> 0
            End Get
        End Property

        Public ReadOnly Property HasHslChanges As Boolean
            Get
                Return _redHue <> 0 OrElse _redSaturation <> 0 OrElse _redLuminance <> 0 OrElse
                       _orangeHue <> 0 OrElse _orangeSaturation <> 0 OrElse _orangeLuminance <> 0 OrElse
                       _yellowHue <> 0 OrElse _yellowSaturation <> 0 OrElse _yellowLuminance <> 0 OrElse
                       _greenHue <> 0 OrElse _greenSaturation <> 0 OrElse _greenLuminance <> 0 OrElse
                       _aquaHue <> 0 OrElse _aquaSaturation <> 0 OrElse _aquaLuminance <> 0 OrElse
                       _blueHue <> 0 OrElse _blueSaturation <> 0 OrElse _blueLuminance <> 0 OrElse
                       _purpleHue <> 0 OrElse _purpleSaturation <> 0 OrElse _purpleLuminance <> 0 OrElse
                       _magentaHue <> 0 OrElse _magentaSaturation <> 0 OrElse _magentaLuminance <> 0
            End Get
        End Property

        Public ReadOnly Property HasTransformChanges As Boolean
            Get
                Return _rotationDegrees <> _appliedRotationDegrees OrElse
                       Math.Abs(_straightenDegrees - _appliedStraightenDegrees) > 0.0001 OrElse
                       _straightenExpandCanvas <> _appliedStraightenExpandCanvas OrElse
                       _flipH <> _appliedFlipH OrElse _flipV <> _appliedFlipV
            End Get
        End Property

        ' Commands
        Public ReadOnly Property SetToolCommand As ICommand
        Public ReadOnly Property SetPendingInsertKindCommand As ICommand
        Public ReadOnly Property SetRatingCommand As ICommand
        Public ReadOnly Property ToggleFavoriteCommand As ICommand
        Public ReadOnly Property AddTagCommand As ICommand
        Public ReadOnly Property RemoveTagCommand As ICommand
        Public ReadOnly Property SaveCommand As ICommand
        Public ReadOnly Property SaveAsCommand As ICommand
        Public ReadOnly Property CancelCommand As ICommand
        Public ReadOnly Property UndoCommand As ICommand
        Public ReadOnly Property RedoCommand As ICommand
        Public ReadOnly Property ApplyPreviewCommand As ICommand
        Public ReadOnly Property RotateLeftCommand As ICommand
        Public ReadOnly Property RotateRightCommand As ICommand
        Public ReadOnly Property FlipHorizontalCommand As ICommand
        Public ReadOnly Property FlipVerticalCommand As ICommand
        Public ReadOnly Property ApplyCropCommand As ICommand
        Public ReadOnly Property ApplyResizeCommand As ICommand
        Public ReadOnly Property ApplyCanvasCommand As ICommand
        Public ReadOnly Property ApplyTransformCommand As ICommand
        Public ReadOnly Property ResetCropCommand As ICommand
        Public ReadOnly Property SetCropPresetCommand As ICommand
        ''' Wird ausgelöst, wenn sich die Maße des angezeigten Bildes geändert haben (z.B. nach dem
        ''' Zuschneiden). Die View passt daraufhin Zoom und Schwenk neu ein - genauso wie beim Laden
        ''' eines anderen Bildes.
        Public Event ImageGeometryChanged As EventHandler

        Public ReadOnly Property ClearCloneSourceCommand As ICommand
        Public ReadOnly Property ResetResizeCommand As ICommand
        Public ReadOnly Property SetResizePresetCommand As ICommand
        Public ReadOnly Property ResetCanvasCommand As ICommand
        Public ReadOnly Property SetCanvasAnchorCommand As ICommand
        Public ReadOnly Property DeleteSelectedAnnotationCommand As ICommand
        Public ReadOnly Property DeleteAnnotationCommand As ICommand
        Public ReadOnly Property ToggleAnnotationVisibilityCommand As ICommand
        Public ReadOnly Property DuplicateSelectedAnnotationCommand As ICommand
        Public ReadOnly Property MoveSelectedAnnotationUpCommand As ICommand
        Public ReadOnly Property MoveSelectedAnnotationDownCommand As ICommand
        Public ReadOnly Property ResetCurrentToolCommand As ICommand
        Public ReadOnly Property ResetLightCommand As ICommand
        Public ReadOnly Property ResetColorCommand As ICommand
        Public ReadOnly Property ResetDetailCommand As ICommand
        Public ReadOnly Property ResetEffectsCommand As ICommand
        Public ReadOnly Property ResetRetouchCommand As ICommand
        Public ReadOnly Property ClearSelectionCommand As ICommand
        Public ReadOnly Property InvertSelectionCommand As ICommand
        Public ReadOnly Property CopySelectionCommand As ICommand
        Public ReadOnly Property FillSelectionCommand As ICommand
        Public ReadOnly Property SetSelectionModeCommand As ICommand
        Public ReadOnly Property SetSelectionCombineModeCommand As ICommand
        Public ReadOnly Property SetAnnotationFillKindCommand As ICommand
        Public ReadOnly Property SetAnnotationAnchorCommand As ICommand
        Public ReadOnly Property ResetTransformCommand As ICommand
        Public ReadOnly Property SetFilterPresetCommand As ICommand
        Public ReadOnly Property ResetFilterCommand As ICommand
        Public ReadOnly Property ResetCurveCommand As ICommand
        Public ReadOnly Property SetCurveChannelCommand As ICommand
        Public ReadOnly Property ResetHslCommand As ICommand
        Public ReadOnly Property ResetSplitToningCommand As ICommand
        Public ReadOnly Property PickNegativeBaseCommand As ICommand
        Public ReadOnly Property AutoNegativeBaseCommand As ICommand
        Public ReadOnly Property ResetNegativeCommand As ICommand
        Public ReadOnly Property ToggleInfoSidebarCommand As ICommand
        Public ReadOnly Property SetInfoTabCommand As ICommand
        Public ReadOnly Property SetLayersPanelTabCommand As ICommand
        Public ReadOnly Property BackToViewerCommand As ICommand
        Public ReadOnly Property BackToGalleryCommand As ICommand
        Public ReadOnly Property PreviousCommand As ICommand
        Public ReadOnly Property NextCommand As ICommand
        Public ReadOnly Property DeleteCurrentCommand As ICommand

        Public Sub New(mainVm As MainWindowViewModel)
            _mainVm = mainVm
            FilmstripItems = New BulkObservableCollection(Of ImageItem)()
            Tags = New ObservableCollection(Of String)()
            TagSuggestions = New ObservableCollection(Of String)(LibraryService.Instance.GetAllTags())
            HistoryItems = New ObservableCollection(Of String)()
            LoadAllShapeIcons()
            LoadWatermarkPresets()
            LoadSavedLightroomPresets()
            LoadSavedLutPresets()
            _previewTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(220)}
            AddHandler _previewTimer.Tick, Sub()
                                               _previewTimer.Stop()
                                               _previewPending = False
                                               UpdatePreview()
                                           End Sub

            _filmstripNavDebouncer = New FilmstripNavigationDebouncer(wrapAround:=False,
                                                                        getCurrentIndex:=Function() _currentIndex,
                                                                        getCount:=Function() _folderPaths.Count,
                                                                        commit:=AddressOf NavigateToFilmstripIndexAsync)

            SetRatingCommand = ReactiveCommand.Create(Of String)(Sub(r)
                                                                     Dim v As Integer
                                                                     If Integer.TryParse(r, v) Then
                                                                         Rating = If(Rating = v, 0, v)
                                                                     End If
                                                                 End Sub)

            ToggleFavoriteCommand = ReactiveCommand.Create(Sub() IsFavorite = Not IsFavorite)

            AddTagCommand = ReactiveCommand.Create(Sub()
                                                       Dim tag = NewTagText.Trim().ToLowerInvariant()
                                                       If String.IsNullOrEmpty(tag) OrElse Tags.Contains(tag) Then Return
                                                       Tags.Add(tag)
                                                       NewTagText = ""
                                                       Dim immichAssetId = CurrentImmichAssetId()
                                                       If immichAssetId IsNot Nothing Then
                                                           Dim ignored = ImmichService.AddTagToAssetAsync(immichAssetId, tag)
                                                       ElseIf Not String.IsNullOrEmpty(_currentImagePath) Then
                                                           LibraryService.Instance.SetTags(_currentImagePath, Tags)
                                                       End If
                                                       RefreshTagSuggestions()
                                                   End Sub)

            RemoveTagCommand = ReactiveCommand.Create(Of String)(Sub(tag)
                                                                     If Not Tags.Remove(tag) Then Return
                                                                     Dim immichAssetId = CurrentImmichAssetId()
                                                                     If immichAssetId IsNot Nothing Then
                                                                         Dim ignored = ImmichService.RemoveTagFromAssetAsync(immichAssetId, tag)
                                                                     ElseIf Not String.IsNullOrEmpty(_currentImagePath) Then
                                                                         LibraryService.Instance.SetTags(_currentImagePath, Tags)
                                                                     End If
                                                                 End Sub)

            SetToolCommand = ReactiveCommand.Create(Of String)(Sub(toolName)
                                                                   Dim normalizedToolName = If(toolName, "").Trim().ToLowerInvariant()

                                                                   Select Case normalizedToolName
                                                                       Case "brush", "pinsel", "eraser", "radiergummi", "blur", "verwischen", "clone", "stempel"
                                                                           SetPaintMode(toolName)
                                                                           Return
                                                                       Case "text", "image", "bild", "qr", "qrcode", "qr-code", "watermark", "wasserzeichen"
                                                                           _overlayNotifySuppressDepth += 1
                                                                           Try
                                                                               SelectedAnnotationIndex = -1
                                                                               CurrentTool = EditorTool.Text
                                                                               PendingInsertKind = NormalizeAnnotationKind(toolName)
                                                                               SelectedLayersPanelTab = LayersPanelTab.Tool
                                                                           Finally
                                                                               _overlayNotifySuppressDepth -= 1
                                                                           End Try
                                                                           NotifyAnnotationOverlayStateChanged()
                                                                           Return
                                                                   End Select

                                                                   Dim parsed As EditorTool
                                                                   If [Enum].TryParse(toolName, parsed) Then
                                                                       _overlayNotifySuppressDepth += 1
                                                                       Try
                                                                           PendingInsertKind = ""
                                                                           ' Ins Drehen-Werkzeug nimmt man das markierte Objekt MIT - dort wirken
                                                                           ' Drehen/Spiegeln genau darauf. Für alle anderen Werkzeuge bleibt es
                                                                           ' beim Abwählen wie bisher.
                                                                           If Not IsObjectTransformTool(parsed) Then SelectedAnnotationIndex = -1
                                                                           CurrentTool = parsed
                                                                           SelectedLayersPanelTab = LayersPanelTab.Tool
                                                                       Finally
                                                                           _overlayNotifySuppressDepth -= 1
                                                                       End Try
                                                                       NotifyAnnotationOverlayStateChanged()
                                                                       Return
                                                                   End If
                                                               End Sub)
            SetPendingInsertKindCommand = ReactiveCommand.Create(Of String)(Sub(kind)
                                                                                If String.IsNullOrEmpty(kind) Then Return
                                                                                If PendingInsertKind = kind Then
                                                                                    PendingInsertKind = ""
                                                                                Else
                                                                                    SelectedAnnotationIndex = -1
                                                                                    PendingInsertKind = kind
                                                                                End If
                                                                            End Sub)


            SaveCommand = ReactiveCommand.Create(Async Function() As Task
                                                     Await SaveImageAsync(False)
                                                 End Function)
            SaveAsCommand = ReactiveCommand.Create(Async Function() As Task
                                                       Await SaveImageAsync(True)
                                                   End Function)

            CancelCommand = ReactiveCommand.Create(Async Function() As Task
                                                       Await BackToViewerAsync()
                                                   End Function)

            UndoCommand = ReactiveCommand.Create(Sub() UndoAction())
            RedoCommand = ReactiveCommand.Create(Sub() RedoAction())
            ApplyPreviewCommand = ReactiveCommand.Create(Sub() UpdatePreview())

            RotateLeftCommand = ReactiveCommand.Create(Sub() DoRotate(-90))
            RotateRightCommand = ReactiveCommand.Create(Sub() DoRotate(90))
            FlipHorizontalCommand = ReactiveCommand.Create(Sub() DoFlipH())
            FlipVerticalCommand = ReactiveCommand.Create(Sub() DoFlipV())
            ApplyCropCommand = ReactiveCommand.Create(Async Function() As Task
                                                          Await ApplyCropAsync()
                                                      End Function)
            ApplyResizeCommand = ReactiveCommand.Create(Async Function() As Task
                                                            Await ApplyResizeAsync()
                                                        End Function)
            ApplyCanvasCommand = ReactiveCommand.Create(Async Function() As Task
                                                            Await ApplyCanvasAsync()
                                                        End Function)
            ApplyTransformCommand = ReactiveCommand.Create(Async Function() As Task
                                                               Await ApplyTransformAsync()
                                                           End Function)
            ResetCropCommand = ReactiveCommand.Create(Sub()
                                                          PushUndo()
                                                          SetCropValues(0, 0, 0, 0)
                                                          _appliedCropLeft = 0
                                                          _appliedCropTop = 0
                                                          _appliedCropRight = 0
                                                          _appliedCropBottom = 0
                                                          RaiseResetButtonStateChanged()
                                                          SchedulePreviewUpdate()
                                                      End Sub)
            SetCropPresetCommand = ReactiveCommand.Create(Of String)(Sub(preset)
                                                                         ApplyCropPreset(preset)
                                                                     End Sub)
            ResetResizeCommand = ReactiveCommand.Create(Sub()
                                                            PushUndo()
                                                            _appliedResizeWidth = 0
                                                            _appliedResizeHeight = 0
                                                            _hasChanges = True
                                                            SetResizeValues(0, 0)
                                                        End Sub)
            SetResizePresetCommand = ReactiveCommand.Create(Of String)(Sub(preset)
                                                                          ApplyResizePreset(preset)
                                                                      End Sub)
            ResetCanvasCommand = ReactiveCommand.Create(Sub()
                                                            PushUndo()
                                                            _appliedCanvasWidth = 0
                                                            _appliedCanvasHeight = 0
                                                            _hasChanges = True
                                                            SetCanvasValues(0, 0, "Center")
                                                        End Sub)
            SetCanvasAnchorCommand = ReactiveCommand.Create(Of String)(Sub(anchor)
                                                                           CanvasAnchor = anchor
                                                                       End Sub)
            DeleteSelectedAnnotationCommand = ReactiveCommand.Create(Sub()
                                                                          DeleteSelectedAnnotation()
                                                                      End Sub)
            DeleteAnnotationCommand = ReactiveCommand.Create(Of ImageAnnotation)(Sub(annotation)
                                                                                      DeleteAnnotation(annotation)
                                                                                  End Sub)
            ToggleAnnotationVisibilityCommand = ReactiveCommand.Create(Of ImageAnnotation)(Sub(annotation)
                                                                                                ToggleAnnotationVisibility(annotation)
                                                                                            End Sub)
            DuplicateSelectedAnnotationCommand = ReactiveCommand.Create(Sub()
                                                                            DuplicateSelectedAnnotation()
                                                                        End Sub)
            MoveSelectedAnnotationUpCommand = ReactiveCommand.Create(Sub()
                                                                         MoveSelectedAnnotation(1)
                                                                     End Sub)
            MoveSelectedAnnotationDownCommand = ReactiveCommand.Create(Sub()
                                                                           MoveSelectedAnnotation(-1)
                                                                       End Sub)
            ResetCurrentToolCommand = ReactiveCommand.Create(Sub()
                                                                 PushUndo()
                                                                 ResetCurrentToolInternal()
                                                             End Sub)
            ResetLightCommand = ReactiveCommand.Create(Sub()
                                                           PushUndo()
                                                           ResetLightInternal()
                                                       End Sub)
            ResetColorCommand = ReactiveCommand.Create(Sub()
                                                           PushUndo()
                                                           ResetColorInternal()
                                                       End Sub)
            PickNegativeBaseCommand = ReactiveCommand.Create(Sub() BeginNegativeBasePick())
            AutoNegativeBaseCommand = ReactiveCommand.Create(Sub()
                                                                 PushUndo()
                                                                 MeasureFilmNegative()
                                                                 _negativeEnabled = True
                                                                 RaiseNegativePropertiesChanged()
                                                                 SchedulePreviewUpdate()
                                                             End Sub)
            ResetNegativeCommand = ReactiveCommand.Create(Sub()
                                                              PushUndo()
                                                              ResetNegativeInternal()
                                                          End Sub)
            ResetDetailCommand = ReactiveCommand.Create(Sub()
                                                            PushUndo()
                                                            ResetDetailInternal()
                                                        End Sub)
            ResetEffectsCommand = ReactiveCommand.Create(Sub()
                                                             PushUndo()
                                                             ResetEffectsInternal()
                                                         End Sub)
            ResetRetouchCommand = ReactiveCommand.Create(Sub()
                                                             PushUndo()
                                                             ResetRetouchInternal()
                                                         End Sub)
            ' Löst nur die Quelle - bereits gesetzte Punkte behalten ihre und bleiben unverändert.
            ClearCloneSourceCommand = ReactiveCommand.Create(Sub() ClearCloneSource())
            ClearSelectionCommand = ReactiveCommand.Create(Sub() ClearSelection())
            InvertSelectionCommand = ReactiveCommand.Create(Sub() InvertSelection())
            CopySelectionCommand = ReactiveCommand.Create(Sub() CopySelectionToNewObject())
            FillSelectionCommand = ReactiveCommand.Create(Sub() FillSelection())
            SetSelectionModeCommand = ReactiveCommand.Create(Of String)(Sub(mode) SetSelectionMode(mode))
            SetSelectionCombineModeCommand = ReactiveCommand.Create(Of String)(Sub(mode) SetSelectionCombineMode(mode))
            SetAnnotationAnchorCommand = ReactiveCommand.Create(Of String)(Sub(anchor) AnnotationAnchor = anchor)
            SetAnnotationFillKindCommand = ReactiveCommand.Create(Of String)(Sub(kind) SetAnnotationFillKind(kind))
            ResetTransformCommand = ReactiveCommand.Create(Sub()
                                                               PushUndo()
                                                               ResetTransformInternal()
                                                           End Sub)
            SetFilterPresetCommand = ReactiveCommand.Create(Of String)(Sub(preset)
                                                                          PushUndo()
                                                                          ApplyExclusiveFilterPreset(preset)
                                                                      End Sub)
            ResetFilterCommand = ReactiveCommand.Create(Sub()
                                                            PushUndo()
                                                            ResetFilterInternal()
                                                        End Sub)
            ResetCurveCommand = ReactiveCommand.Create(Sub()
                                                           PushUndo()
                                                           ResetCurvePoints()
                                                           RaiseResetButtonStateChanged()
                                                           SchedulePreviewUpdate()
                                                       End Sub)
            SetCurveChannelCommand = ReactiveCommand.Create(Of String)(Sub(channelName) SetCurveChannel(channelName))
            AddHandler _curveRgbPoints.CollectionChanged, AddressOf OnCurvePointsChanged
            AddHandler _curveRedPoints.CollectionChanged, AddressOf OnCurvePointsChanged
            AddHandler _curveGreenPoints.CollectionChanged, AddressOf OnCurvePointsChanged
            AddHandler _curveBluePoints.CollectionChanged, AddressOf OnCurvePointsChanged
            AddHandler _curveLuminancePoints.CollectionChanged, AddressOf OnCurvePointsChanged
            ResetHslCommand = ReactiveCommand.Create(Sub()
                                                         PushUndo()
                                                         ResetHslInternal()
                                                     End Sub)
            ResetSplitToningCommand = ReactiveCommand.Create(Sub()
                                                                  PushUndo()
                                                                  ResetSplitToningInternal()
                                                              End Sub)

            ToggleInfoSidebarCommand = ReactiveCommand.Create(Sub()
                                                                   If _mainVm Is Nothing OrElse _mainVm.Settings Is Nothing Then Return
                                                                   _mainVm.Settings.EditorInfoSidebarExpanded = Not _mainVm.Settings.EditorInfoSidebarExpanded
                                                                   Me.RaisePropertyChanged(NameOf(IsInfoSidebarVisible))
                                                               End Sub)
            SetInfoTabCommand = ReactiveCommand.Create(Of String)(Sub(tabName) SetInfoTab(tabName))
            SetLayersPanelTabCommand = ReactiveCommand.Create(Of String)(Sub(tabName)
                                                                              If String.Equals(tabName, "History", StringComparison.OrdinalIgnoreCase) Then
                                                                                  SelectedLayersPanelTab = LayersPanelTab.History
                                                                              ElseIf String.Equals(tabName, "Layers", StringComparison.OrdinalIgnoreCase) Then
                                                                                  SelectedLayersPanelTab = LayersPanelTab.Layers
                                                                              Else
                                                                                  SelectedLayersPanelTab = LayersPanelTab.Tool
                                                                              End If
                                                                          End Sub)
            BackToViewerCommand = ReactiveCommand.Create(Async Function() As Task
                                                             Await BackToViewerAsync()
                                                         End Function)
            BackToGalleryCommand = ReactiveCommand.Create(Sub() _mainVm.CurrentMode = AppMode.Gallery)
            PreviousCommand = ReactiveCommand.Create(Async Function() As Task
                                                         Await NavigatePreviousAsync()
                                                     End Function)
            NextCommand = ReactiveCommand.Create(Async Function() As Task
                                                     Await NavigateNextAsync()
                                                 End Function)
            DeleteCurrentCommand = ReactiveCommand.Create(Sub() DeleteCurrent())

            If _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing Then
                _saveQuality = _mainVm.Settings.JpgSaveQuality
            End If
        End Sub

        Private Sub DeleteCurrent()
            If String.IsNullOrEmpty(_currentImagePath) Then Return
            Dim deletedPath = _currentImagePath
            _mainVm.RequestDeletePaths({deletedPath}, Sub()
                                                           _folderPaths.Remove(deletedPath)
                                                           CurrentImage = Nothing
                                                           PreviewImage = Nothing
                                                           ComparisonImage = Nothing
                                                           CurrentImagePath = ""
                                                           _mainVm.CurrentMode = AppMode.Gallery
                                                       End Sub)
        End Sub

        Public Sub ReleaseCurrentImageIfAny(paths As IEnumerable(Of String))
            If String.IsNullOrEmpty(_currentImagePath) OrElse paths Is Nothing Then Return
            If paths.Any(Function(p) String.Equals(p, _currentImagePath, StringComparison.OrdinalIgnoreCase)) Then
                CurrentImage = Nothing
                PreviewImage = Nothing
                ComparisonImage = Nothing
                ClearPreviewSource()
            End If
        End Sub

        Public Async Function BackToViewerAsync() As Task
            If Not Await ConfirmSaveBeforeLeavingAsync("den Editor verlässt") Then Return
            ' Immich-Edit: der Viewer hält seine Album-Sitzung (Pseudo-Pfade) noch. Erneutes OpenImage mit
            ' dem lokalen Temp-Pfad würde sie durch eine Ein-Bild-Sitzung ersetzen und den Filmstreifen auf
            ' das aktuelle Foto reduzieren. Stattdessen einfach zurückschalten - das ganze Album bleibt.
            Dim editorIsImmich = _immichSourceAlbumId IsNot Nothing OrElse ImmichService.IsImmichTempPath(_currentImagePath)
            If editorIsImmich AndAlso _mainVm.Viewer IsNot Nothing AndAlso _mainVm.Viewer.IsImmichSession Then
                _mainVm.CurrentMode = AppMode.Viewer
                Return
            End If
            If Not String.IsNullOrEmpty(_currentImagePath) Then
                _mainVm.Viewer.OpenImage(_currentImagePath, _folderPaths.ToList(), _thumbCacheScopeId, _thumbCacheScopeName)
                _mainVm.CurrentMode = AppMode.Viewer
            Else
                _mainVm.CurrentMode = AppMode.Viewer
            End If
        End Function

        Public Sub ResetTransientUiState()
            _selectedAnnotationIndex = -1
            ResetEditorUiStateForNewImage(resetTool:=True)
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationIndex))
            Me.RaisePropertyChanged(NameOf(HasSelectedAnnotation))
        End Sub

        Public Sub NavigateToFilmstripItem(item As ImageItem)
            Dim ignored = NavigateToFilmstripItemAsync(item)
        End Sub

        Public Async Function NavigateToFilmstripItemAsync(item As ImageItem) As Task
            If item Is Nothing Then Return
            Dim idx = _folderPaths.FindIndex(Function(p) String.Equals(p, item.FilePath, StringComparison.OrdinalIgnoreCase))
            If idx < 0 Then Return
            If Not Await ConfirmSaveBeforeLeavingAsync("dieses Bild öffnest") Then Return
            _currentIndex = idx
            LoadImageContent(item.FilePath)
        End Function

        ''' Wird vom Mausrad-Handler auf dem Filmstrip (schnelles Scrollen -> viele Events kurz
        ''' hintereinander) aufgerufen. Jedes Event würde sonst sofort ein volles Bild laden (inkl.
        ''' möglichem "Änderungen speichern?"-Dialog), sodass das Blättern nach dem Aufhören zu scrollen
        ''' sichtbar weiterläuft bzw. mehrere Dialoge aufeinanderstapeln. Stattdessen wird nur der
        ''' Zielindex vorgemerkt und erst nach kurzer Pause tatsächlich geladen (einmalig, direkt zum
        ''' Nettoziel). NavigateNextAsync/NavigatePreviousAsync (Toolbar-Buttons) bleiben unverändert.
        Public Sub NavigateNext()
            _filmstripNavDebouncer.QueueNext()
        End Sub

        Public Sub NavigatePrevious()
            _filmstripNavDebouncer.QueuePrevious()
        End Sub

        ''' Für Mausrad-Navigation im Filmstrip - normalisiert per Delta-Magnitude statt pro Event
        ''' einen vollen Schritt auszulösen (siehe FilmstripNavigationDebouncer.QueueWheelDelta).
        Public Sub NavigateByWheel(deltaY As Double)
            _filmstripNavDebouncer.QueueWheelDelta(deltaY)
        End Sub

        Private Async Function NavigateToFilmstripIndexAsync(idx As Integer) As Task
            If idx < 0 OrElse idx >= _folderPaths.Count OrElse idx = _currentIndex Then Return
            If Not Await ConfirmSaveBeforeLeavingAsync("das nächste Bild öffnest") Then Return
            _currentIndex = idx
            LoadImageContent(_folderPaths(_currentIndex))
        End Function

        Public Async Function NavigateNextAsync() As Task
            If _currentIndex < _folderPaths.Count - 1 Then
                If Not Await ConfirmSaveBeforeLeavingAsync("das nächste Bild öffnest") Then Return
                _currentIndex += 1
                LoadImageContent(_folderPaths(_currentIndex))
            End If
        End Function

        Public Async Function NavigatePreviousAsync() As Task
            If _currentIndex > 0 Then
                If Not Await ConfirmSaveBeforeLeavingAsync("das vorherige Bild öffnest") Then Return
                _currentIndex -= 1
                LoadImageContent(_folderPaths(_currentIndex))
            End If
        End Function

        Private Sub LoadImageContent(path As String)
            If String.IsNullOrEmpty(path) OrElse Not File.Exists(path) Then
                If Not String.IsNullOrEmpty(path) Then
                    _folderPaths.RemoveAll(Function(p) String.Equals(p, path, StringComparison.OrdinalIgnoreCase))
                End If

                If _folderPaths.Count = 0 Then
                    CurrentImage = Nothing
                    PreviewImage = Nothing
                    ComparisonImage = Nothing
                    ClearPreviewSource()
                    CurrentImagePath = ""
                    _currentImagePath = ""
                    StatusText = ""
                    Return
                End If

                Dim fallbackIndex = Math.Max(0, Math.Min(_currentIndex, _folderPaths.Count - 1))
                LoadImageContent(_folderPaths(fallbackIndex))
                Return
            End If

            CurrentImagePath = path
            _currentImagePath = path
            SelectedInfoTab = InfoSidebarTab.General
            ClearSelection()   ' pixelbasierte Auswahlmaske gilt nur fürs alte Bild
            ResetAdjustmentsInternal(resetEditorUi:=True)
            ClearUndoHistory()
            ShowBeforeImage = _comparisonAutoEnabled AndAlso CanShowBeforeAfter
            PreviewImage = Nothing
            ComparisonImage = Nothing
            PreparePreviewSource(path)
            LoadLibraryMeta(path)
            Me.RaisePropertyChanged(NameOf(CurrentFilmstripIndex))
            Me.RaisePropertyChanged(NameOf(PositionText))
            MarkCurrentFilmstripItem()
            Try
                CurrentImage = ImageOrientationService.LoadOrientedAvaloniaBitmapAuto(path)
                If CurrentImage Is Nothing Then
                    StatusText = If(RawPreviewService.IsSupportedRaw(path), "Keine Vorschau aus dieser RAW-Datei extrahierbar", "Fehler beim Laden")
                    Return
                End If
                ExifInfo = BuildImageInfo(path)
                RefreshHistogram()
                Dim info = New FileInfo(path)
                Dim kb = info.Length / 1024.0
                Dim sizeStr = If(kb < 1024, $"{kb:F0} KB", $"{kb / 1024:F1} MB")
                Dim mp = CurrentImage.Size.Width * CurrentImage.Size.Height / 1_000_000.0
                StatusText = $"{CInt(CurrentImage.Size.Width)} × {CInt(CurrentImage.Size.Height)}  {mp:F1} MP  •  {sizeStr}"
            Catch
                StatusText = LocalizationService.T("Fehler beim Laden")
            End Try
        End Sub

        Public Sub OpenImage(imagePath As String, Optional allPaths As List(Of String) = Nothing)
            Dim ignored = OpenImageAsync(imagePath, allPaths)
        End Sub

        Public Async Function OpenImageAsync(imagePath As String, Optional allPaths As List(Of String) = Nothing, Optional cacheScopeId As String = Nothing, Optional cacheScopeName As String = Nothing, Optional forceSaveAsOnly As Boolean = False, Optional immichAlbumId As String = Nothing) As Task(Of Boolean)
            If String.IsNullOrEmpty(imagePath) OrElse Not File.Exists(imagePath) Then Return False
            If Not String.IsNullOrEmpty(_currentImagePath) AndAlso Not String.Equals(_currentImagePath, imagePath, StringComparison.OrdinalIgnoreCase) Then
                If Not Await ConfirmSaveBeforeLeavingAsync("ein anderes Bild öffnest") Then Return False
            End If
            ' Vor dem Setzen von CurrentImagePath, damit dessen PropertyChanged CanSaveInPlace korrekt neu bewertet.
            ' Pfadbasierte Erkennung ergänzt das Flag: eine Immich-Temp-Kopie ist IMMER nur „Speichern
            ' unter", egal ob aus Galerie (Flag gesetzt) oder aus dem Viewer (Flag nicht durchgereicht).
            _forceSaveAsOnly = forceSaveAsOnly OrElse ImmichService.IsImmichTempPath(imagePath)
            _immichSourceAlbumId = immichAlbumId
            _immichSourceFileName = Nothing
            CurrentImagePath = imagePath
            _currentImagePath = imagePath
            SelectedInfoTab = InfoSidebarTab.General
            ResetAdjustmentsInternal(resetEditorUi:=True)
            ClearUndoHistory()
            ShowBeforeImage = _comparisonAutoEnabled AndAlso CanShowBeforeAfter
            PreviewImage = Nothing
            ComparisonImage = Nothing
            PreparePreviewSource(imagePath)
            ' Scope nur bei expliziter Pfadliste (z.B. Suchliste) wirksam, sonst normaler Ordner-Cache.
            _thumbCacheScopeId = If(allPaths IsNot Nothing, cacheScopeId, Nothing)
            _thumbCacheScopeName = If(allPaths IsNot Nothing, cacheScopeName, Nothing)
            If allPaths IsNot Nothing Then
                LoadFilmstripContext(imagePath, allPaths)
            Else
                LoadFilmstripContext(imagePath)
            End If
            LoadLibraryMeta(imagePath)
            Dim immichAssetId = CurrentImmichAssetId()
            If immichAssetId IsNot Nothing Then Await LoadImmichMetaAsync(immichAssetId)

            Try
                CurrentImage = ImageOrientationService.LoadOrientedAvaloniaBitmapAuto(imagePath)
                If CurrentImage Is Nothing Then
                    Dim message = If(RawPreviewService.IsSupportedRaw(imagePath),
                        "Aus dieser RAW-Datei konnte keine Vorschau extrahiert werden.",
                        "Diese Datei konnte nicht geöffnet werden.")
                    Await _mainVm.ShowMessageAsync("Öffnen fehlgeschlagen", message)
                    CurrentImagePath = ""
                    _currentImagePath = ""
                    Return False
                End If
                ExifInfo = BuildImageInfo(imagePath)
                RefreshHistogram()
                Dim info = New FileInfo(imagePath)
                Dim kb = info.Length / 1024.0
                Dim sizeStr = If(kb < 1024, $"{kb:F0} KB", $"{kb / 1024:F1} MB")
                Dim mp = CurrentImage.Size.Width * CurrentImage.Size.Height / 1_000_000.0
                StatusText = $"{CInt(CurrentImage.Size.Width)} × {CInt(CurrentImage.Size.Height)}  {mp:F1} MP  •  {sizeStr}"
            Catch ex As Exception
                StatusText = LocalizationService.T("Fehler beim Laden")
            End Try
            Return True
        End Function

        Private Function BuildImageInfo(imagePath As String) As ExifData
            Dim data = ExifService.ReadExif(imagePath)

            If CurrentImage IsNot Nothing Then
                Dim width = CurrentImage.PixelSize.Width
                Dim height = CurrentImage.PixelSize.Height

                If String.IsNullOrWhiteSpace(data.ImageWidth) Then data.ImageWidth = width.ToString()
                If String.IsNullOrWhiteSpace(data.ImageHeight) Then data.ImageHeight = height.ToString()

                Dim mp = width * height / 1_000_000.0
                data.Megapixels = $"{mp:F1} MP"
                data.AspectRatio = FormatAspectRatio(width, height)
            End If

            If String.IsNullOrWhiteSpace(data.FileType) Then
                data.FileType = IO.Path.GetExtension(imagePath).TrimStart("."c).ToUpperInvariant()
            End If

            If String.IsNullOrWhiteSpace(data.ColorSpace) Then data.ColorSpace = "Unbekannt"

            ' Nebenläufig persistieren, damit das im Editor geöffnete Bild ab jetzt über EXIF
            ' durchsuchbar ist - blockiert nicht die UI.
            Dim exifForSearch = ExifService.ExtractSearchFields(data, imagePath)
            Dim catalogSummary = ExifService.BuildCatalogSummary(data, exifForSearch)
            Task.Run(Sub() LibraryService.Instance.SyncExifData(imagePath, exifForSearch, catalogSummary))

            Return data
        End Function

        Private Shared Function FormatAspectRatio(width As Integer, height As Integer) As String
            If width <= 0 OrElse height <= 0 Then Return ""

            Dim divisor = GreatestCommonDivisor(width, height)
            Return $"{width \ divisor}:{height \ divisor}"
        End Function

        Private Shared Function GreatestCommonDivisor(a As Integer, b As Integer) As Integer
            a = Math.Abs(a)
            b = Math.Abs(b)

            While b <> 0
                Dim remainder = a Mod b
                a = b
                b = remainder
            End While

            Return Math.Max(1, a)
        End Function

        Private Sub LoadLibraryMeta(imagePath As String)
            _rating = LibraryService.Instance.GetRating(imagePath)
            Me.RaisePropertyChanged(NameOf(Rating))
            _isFavorite = LibraryService.Instance.GetFavorite(imagePath)
            Me.RaisePropertyChanged(NameOf(IsFavorite))
            Tags.Clear()
            For Each tag In LibraryService.Instance.GetTags(imagePath)
                Tags.Add(tag)
            Next
            RefreshTagSuggestions()
        End Sub

        Private Async Function LoadImmichMetaAsync(assetId As String) As Task
            Dim asset = Await ImmichService.GetAssetDetailAsync(assetId)
            If asset Is Nothing Then Return

            _immichSourceFileName = asset.FileName
            _rating = asset.Rating
            Me.RaisePropertyChanged(NameOf(Rating))
            _isFavorite = asset.IsFavorite
            Me.RaisePropertyChanged(NameOf(IsFavorite))
            Tags.Clear()
            For Each tag In If(asset.Tags, New List(Of String)())
                Tags.Add(tag)
            Next
            RefreshTagSuggestions()
        End Function

        Private Sub RefreshTagSuggestions()
            TagSuggestions.Clear()
            For Each tag In LibraryService.Instance.GetAllTags()
                TagSuggestions.Add(tag)
            Next
        End Sub

        Private Sub LoadFilmstripContext(imagePath As String, Optional allPaths As List(Of String) = Nothing)
            For Each filmItem In FilmstripItems
                filmItem?.EvictThumbnail()
            Next
            FilmstripItems.Clear()
            _folderPaths.Clear()
            _currentIndex = -1

            Try
                If allPaths IsNot Nothing Then
                    _folderPaths = allPaths.
                        Where(Function(p) Not String.IsNullOrEmpty(p)).
                        Where(Function(p) CanParticipateInEditorFilmstrip(p)).
                        Distinct(StringComparer.OrdinalIgnoreCase).
                        ToList()

                    FilmstripItems.ReplaceAll(_folderPaths.Select(Function(path) ImageItem.CreateLightweight(path, Nothing, _thumbCacheScopeId, _thumbCacheScopeName)))

                    _currentIndex = _folderPaths.FindIndex(Function(p) String.Equals(p, imagePath, StringComparison.OrdinalIgnoreCase))
                    If _currentIndex < 0 Then _currentIndex = 0
                    Me.RaisePropertyChanged(NameOf(CurrentFilmstripIndex))
                    Me.RaisePropertyChanged(NameOf(PositionText))
                    MarkCurrentFilmstripItem()
                    Dim itemsSnapshotAllPaths = FilmstripItems.ToList()
                    Dispatcher.UIThread.Post(Sub() ImageItem.QueueBackgroundThumbnails(itemsSnapshotAllPaths), DispatcherPriority.Background)
                    Return
                End If

                Dim folder = IO.Path.GetDirectoryName(imagePath)
                If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then Return

                _folderPaths = Directory.GetFiles(folder).
                    Where(Function(f) CanParticipateInEditorFilmstrip(f)).
                    OrderBy(Function(f) IO.Path.GetFileName(f)).
                    ToList()

                FilmstripItems.ReplaceAll(_folderPaths.Select(Function(path) ImageItem.CreateLightweight(path)))

                _currentIndex = _folderPaths.FindIndex(Function(p) String.Equals(p, imagePath, StringComparison.OrdinalIgnoreCase))
                If _currentIndex < 0 Then _currentIndex = 0
                Me.RaisePropertyChanged(NameOf(CurrentFilmstripIndex))
                Me.RaisePropertyChanged(NameOf(PositionText))
                MarkCurrentFilmstripItem()
                Dim itemsSnapshot = FilmstripItems.ToList()
                Dispatcher.UIThread.Post(Sub() ImageItem.QueueBackgroundThumbnails(itemsSnapshot), DispatcherPriority.Background)
            Catch
            End Try
        End Sub

        Private Sub MarkCurrentFilmstripItem()
            If FilmstripItems Is Nothing Then Return
            For i = 0 To FilmstripItems.Count - 1
                FilmstripItems(i).IsSelected = (i = _currentIndex)
            Next
        End Sub

        Private Shared Function CanParticipateInEditorFilmstrip(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            If VideoPreviewService.IsSupportedVideo(path) Then Return False
            If SvgPreviewService.IsSupportedSvg(path) Then Return False

            Dim ext = IO.Path.GetExtension(path).ToLowerInvariant()
            Dim editableExts = {".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".heic", ".avif", ".ico"}
            Return editableExts.Contains(ext) OrElse RawPreviewService.IsSupportedRaw(path)
        End Function

        Private Sub SchedulePreviewUpdate()
            _hasChanges = True
            _previewPending = True
            StatusText = LocalizationService.T("Vorschau wird aktualisiert...")
            _previewTimer.Stop()
            _previewTimer.Start()
        End Sub

        ''' Wie SchedulePreviewUpdate, markiert das Dokument aber NICHT als geändert (_hasChanges) -
        ''' für Werkzeuge mit expliziter "Anwenden"-Bestätigung (Zuschneiden/Bildgröße/Leinwandgröße/
        ''' Drehen): Live-Werte sollen sofort in der Vorschau sichtbar sein (siehe
        ''' GetCurrentAdjustments(forPreview:=True)), aber weder als ungespeicherte Änderung zählen
        ''' noch das kanonische Ergebnis beeinflussen, bis der Nutzer "Anwenden" klickt.
        Private Sub ScheduleToolPreviewUpdate()
            _previewPending = True
            StatusText = LocalizationService.T("Vorschau wird aktualisiert...")
            _previewTimer.Stop()
            _previewTimer.Start()
        End Sub

        Private Function IsTextualAnnotationKind(kind As String) As Boolean
            Dim k = NormalizeAnnotationKind(kind)
            Return k = "Text" OrElse k = "Watermark"
        End Function

        ''' <summary>
        ''' Die Glyphen eines selektierten Text-/Wasserzeichenobjekts kommen aus dem gerenderten Overlay,
        ''' nicht aus der Live-Textbox. Die Textbox bleibt darüber liegen, zeichnet aber nichts mehr - sie
        ''' liefert nur Eingabe, Schreibmarke und Textauswahl.
        '''
        ''' Der Grund: Avalonia und Skia setzen Text nicht gleich. Avalonia kann Glyphen weder umranden noch
        ''' mit einem Verlauf füllen (Foreground ist eine einzelne Farbe), es shaped über HarfBuzz mit Kerning,
        ''' und es hängt die Zeile in einen Zeilenkasten, dessen Grundlinie nicht dort liegt, wo Skia sie setzt
        ''' (rect.Top + fontSize). Jede dieser Abweichungen einzeln nachzustellen hat sich als Reihe von
        ''' Ein-Pixel-Korrekturen erwiesen. Zeichnet der Renderer selbst, zeigt der Editor exakt das Ergebnis.
        ''' </summary>
        Private Function TextRendersInOverlay(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing Then Return False
            If Not IsTextualAnnotationKind(annotation.Kind) Then Return False
            Return Not UsesRenderedSelectionOverlay(annotation)
        End Function

        ''' Für die View: der selektierte Text steht bereits im Overlay-Bitmap, die Textbox darf ihn nicht
        ''' ein zweites Mal zeichnen.
        Public ReadOnly Property SelectedTextRendersInOverlay As Boolean
            Get
                If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return False
                Return TextRendersInOverlay(_annotations(_selectedAnnotationIndex))
            End Get
        End Property

        Private Function UsesRenderedSelectionOverlay(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing Then Return False

            Select Case NormalizeAnnotationKind(annotation.Kind)
                Case "Text", "Brush", "Eraser"
                    Return False
                Case "Watermark"
                    Return Not String.IsNullOrWhiteSpace(annotation.ImagePath)
                Case Else
                    Return True
            End Select
        End Function

        ''' Ob das selektierte Text-/Wasserzeichen-Objekt aktuell per Live-Overlay dargestellt wird
        ''' und deshalb im gebackenen Vorschaubild ausgeblendet werden muss (siehe GetCurrentAdjustments).
        Private Function ComputesOverlayHidesSelection() As Boolean
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return False
            If _currentTool <> EditorTool.Text AndAlso _currentTool <> EditorTool.Insert AndAlso _currentTool <> EditorTool.Geometry AndAlso _currentTool <> EditorTool.Selection Then Return False
            Dim selected = _annotations(_selectedAnnotationIndex)
            Return IsTextualAnnotationKind(selected.Kind) OrElse UsesRenderedSelectionOverlay(selected)
        End Function

        ''' Versucht, die Annotationen synchron auf dem bereits gecachten Base-Bitmap neu zu
        ''' komposieren (siehe ImageProcessor.TryRenderAnnotationsOnCachedBase), statt auf den
        ''' asynchronen Task.Run-Render zu warten. Das koppelt "Live-Overlay erscheint" und
        ''' "gebackenes Bild blendet Objekt aus/ein" atomar in denselben Aufruf-Stack und schließt
        ''' damit das Zeitfenster, in dem kurzzeitig beide (Live-Overlay UND gebackener Text)
        ''' sichtbar sind. Bei kaltem Cache oder falls der Cache-Lock gerade von einem
        ''' Hintergrund-Render gehalten wird, liefert dies False - der Aufrufer fällt dann auf den
        ''' bestehenden asynchronen Pfad zurück.
        Private Function TryRenderAnnotationOverlaySync() As Boolean
            Dim previewSource = GetPreviewSource()
            If previewSource Is Nothing Then Return False

            Dim adj = GetCurrentAdjustments(forPreview:=True)
            Dim newPreview = ImageProcessor.TryRenderAnnotationsOnCachedBase(previewSource, adj)
            If newPreview Is Nothing Then Return False

            InvalidatePreviewWork()
            PreviewImage = newPreview
            _previewPending = False
            StatusText = LocalizationService.T("Vorschau bereit")
            Return True
        End Function

        ''' Wrapper um NotifyAnnotationOverlayStateChanged, der Aufrufe unterdrückt, solange
        ''' _overlayNotifySuppressDepth > 0 - siehe Kommentar am Feld. Aufrufer, die mehrere
        ''' zusammengehörige Statements klammern wollen, erhöhen/verringern die Tiefe und rufen
        ''' NotifyAnnotationOverlayStateChanged() danach genau einmal direkt auf.
        Private Sub RequestOverlayStateNotify()
            If _overlayNotifySuppressDepth > 0 Then Return
            NotifyAnnotationOverlayStateChanged()
        End Sub

        ''' Ändert sich die Live-Overlay/gebackene-Vorschau-Umschaltung gerade (in beide Richtungen -
        ''' Selektieren blendet im gebackenen Bild aus, Verlassen der Selektion wieder ein), wird sofort
        ''' statt erst nach dem 220ms-Debounce neu gerendert. Ohne die Selektieren-Richtung bliebe das
        ''' gebackene Bild kurz auf dem alten (noch sichtbaren) Stand, während das Live-Overlay schon
        ''' erscheint - das erzeugte einen sichtbaren Versatz/Sprung direkt beim Selektieren.
        ''' Nur die beiden Aufrufer (CurrentTool-/SelectedAnnotationIndex-Setter) lösen dies aus - reine
        ''' UI-Zustandswechsel (Werkzeug wechseln, Annotation nur selektieren), keine Inhaltsänderung.
        ''' Markiert deshalb NICHT _hasChanges - echte Bearbeitungen an Annotationen dirtien bereits
        ''' unabhängig über SyncSelectedAnnotation -> SchedulePreviewUpdate.
        Private Sub NotifyAnnotationOverlayStateChanged()
            Dim isHiddenNow = ComputesOverlayHidesSelection()
            If isHiddenNow <> _overlayHidesSelectionFromPreview Then
                _previewTimer.Stop()
                _previewPending = False
                If Not TryRenderAnnotationOverlaySync() Then
                    UpdatePreview()
                End If
            Else
                ScheduleToolPreviewUpdate()
            End If
            _overlayHidesSelectionFromPreview = isHiddenNow
        End Sub

        Private Sub PreparePreviewSource(imagePath As String)
            InvalidatePreviewWork()
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then
                ClearPreviewSource()
                Return
            End If

            Dim source = ImageProcessor.LoadPreviewSource(imagePath, PreviewMaxDimension)
            If source Is Nothing Then
                ClearPreviewSource()
                Return
            End If

            Dim oldSource As SKBitmap = Nothing
            SyncLock _previewSync
                oldSource = _previewSource
                _previewSource = source
                If oldSource IsNot Nothing Then
                    _stalePreviewSources.Add(oldSource)
                End If
            End SyncLock
            ' Der Basis-Cache gehört zur alten Quelle: er würde beim nächsten Render zwar ohnehin
            ' verworfen (Referenzvergleich auf die Quelle), hielte sein Bitmap bis dahin aber fest.
            ImageProcessor.ClearBaseCache()
            TryDisposeStalePreviewSources()
        End Sub

        Private Sub ClearPreviewSource()
            InvalidatePreviewWork()
            Dim oldSource As SKBitmap = Nothing
            SyncLock _previewSync
                oldSource = _previewSource
                _previewSource = Nothing
                If oldSource IsNot Nothing Then
                    _stalePreviewSources.Add(oldSource)
                End If
            End SyncLock
            ImageProcessor.ClearBaseCache()
            TryDisposeStalePreviewSources()
        End Sub

        Private Function GetPreviewSource() As SKBitmap
            SyncLock _previewSync
                Return _previewSource
            End SyncLock
        End Function

        Private Sub InvalidatePreviewWork()
            Interlocked.Increment(_previewRequestId)
            Dim oldCts = Interlocked.Exchange(_previewRenderCts, Nothing)
            CancelAndDisposePreviewCts(oldCts)
        End Sub

        Private Shared Sub CancelAndDisposePreviewCts(cts As CancellationTokenSource)
            If cts Is Nothing Then Return

            Try
                cts.Cancel()
            Catch ex As ObjectDisposedException
            End Try

            Try
                cts.Dispose()
            Catch ex As ObjectDisposedException
            End Try
        End Sub

        Private Shared Function IsIgnorablePreviewException(ex As Exception) As Boolean
            If TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is TaskCanceledException OrElse TypeOf ex Is ObjectDisposedException Then
                Return True
            End If

            Return ex.Message.IndexOf("CancellationTokenSource has been disposed", StringComparison.OrdinalIgnoreCase) >= 0
        End Function

        Private Sub RegisterPreviewRenderStart()
            Interlocked.Increment(_previewRequestId)
            Interlocked.Increment(_activePreviewRenders)
        End Sub

        Private Sub RegisterPreviewRenderEnd()
            If Interlocked.Decrement(_activePreviewRenders) = 0 Then
                TryDisposeStalePreviewSources()
            End If
        End Sub

        Private Sub TryDisposeStalePreviewSources()
            Dim stale As List(Of SKBitmap) = Nothing
            SyncLock _previewSync
                If _activePreviewRenders <> 0 OrElse _stalePreviewSources.Count = 0 Then Return
                stale = New List(Of SKBitmap)(_stalePreviewSources)
                _stalePreviewSources.Clear()
            End SyncLock

            For Each bitmap In stale
                bitmap.Dispose()
            Next
        End Sub

        Public Async Sub UpdatePreview()
            Await UpdatePreviewAsync()
        End Sub

        ''' <summary>
        ''' Bäckt den offenen Beschnitt in den angewendeten hinein. Weil _appliedCrop* am Original
        ''' gemessen wird, _crop* aber am angezeigten (bereits beschnittenen) Bild, werden die neuen
        ''' Kanten in den verbleibenden Ausschnitt hineingerechnet - erst dadurch lässt sich mehrfach
        ''' hintereinander zuschneiden. Danach ist der offene Beschnitt leer: das Auswahlrechteck legt
        ''' sich wieder um das ganze (neue) Bild, statt im alten Maßstab stehen zu bleiben.
        ''' Addiert werden ganze Pixel des Originals: angewendeter und offener Beschnitt liegen im
        ''' selben Raster (kein Neuabtasten dazwischen), also darf hier nichts runden. Nur das Ergebnis
        ''' geht wieder als Prozent in die Adjustments.
        ''' </summary>
        Private Async Function ApplyCropAsync() As Task
            If Not HasCropChanges Then Return
            PushUndo()
            ClearActiveSelectionForGeometry()

            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            If baseWidth > 0 AndAlso baseHeight > 0 Then
                Dim leftPx = PercentToPixels(_appliedCropLeft, baseWidth) + CropLeftPixels
                Dim rightPx = PercentToPixels(_appliedCropRight, baseWidth) + CropRightPixels
                Dim topPx = PercentToPixels(_appliedCropTop, baseHeight) + CropTopPixels
                Dim bottomPx = PercentToPixels(_appliedCropBottom, baseHeight) + CropBottomPixels

                ' Ein Pixel muss stehen bleiben, sonst hätte das Ergebnis keine Fläche mehr.
                rightPx = Math.Min(rightPx, Math.Max(0, baseWidth - 1 - leftPx))
                bottomPx = Math.Min(bottomPx, Math.Max(0, baseHeight - 1 - topPx))

                _appliedCropLeft = PixelsToPercent(leftPx, baseWidth)
                _appliedCropRight = PixelsToPercent(rightPx, baseWidth)
                _appliedCropTop = PixelsToPercent(topPx, baseHeight)
                _appliedCropBottom = PixelsToPercent(bottomPx, baseHeight)
            End If

            _cropLeft = 0
            _cropTop = 0
            _cropRight = 0
            _cropBottom = 0
            Me.RaisePropertyChanged(NameOf(CropLeft))
            Me.RaisePropertyChanged(NameOf(CropTop))
            Me.RaisePropertyChanged(NameOf(CropRight))
            Me.RaisePropertyChanged(NameOf(CropBottom))
            Me.RaisePropertyChanged(NameOf(EffectiveImageWidthPixels))
            Me.RaisePropertyChanged(NameOf(EffectiveImageHeightPixels))
            RaiseCropPropertiesChanged()

            _hasChanges = True
            RaiseResetButtonStateChanged()
            Await UpdatePreviewAsync()

            ' Das Bild hat eine neue Größe - Zoom und Schwenk müssen sich neu einpassen, sonst bleibt
            ' der Ausschnitt im Maßstab des alten Bildes stehen.
            RaiseEvent ImageGeometryChanged(Me, EventArgs.Empty)
        End Function

        Private Async Function ApplyResizeAsync() As Task
            If Not HasImageResizeChanges Then Return
            PushUndo()
            ClearActiveSelectionForGeometry()
            _appliedResizeWidth = _resizeWidth
            _appliedResizeHeight = _resizeHeight
            _hasChanges = True
            RaiseResetButtonStateChanged()
            Await UpdatePreviewAsync()
        End Function

        Private Async Function ApplyCanvasAsync() As Task
            If Not HasCanvasSizeChanges Then Return
            PushUndo()
            ClearActiveSelectionForGeometry()
            _appliedCanvasWidth = _canvasWidth
            _appliedCanvasHeight = _canvasHeight
            _hasChanges = True
            RaiseResetButtonStateChanged()
            Await UpdatePreviewAsync()
        End Function

        Private Async Function ApplyTransformAsync() As Task
            If Not HasTransformChanges Then Return
            PushUndo()
            ClearActiveSelectionForGeometry()
            _appliedRotationDegrees = _rotationDegrees
            _appliedStraightenDegrees = _straightenDegrees
            _appliedStraightenExpandCanvas = _straightenExpandCanvas
            _appliedFlipH = _flipH
            _appliedFlipV = _flipV
            _hasChanges = True
            RaiseResetButtonStateChanged()
            Await UpdatePreviewAsync()
        End Function

        Private Async Function UpdatePreviewAsync() As Task
            If String.IsNullOrEmpty(_currentImagePath) Then Return

            Dim previewSource = GetPreviewSource()
            If previewSource Is Nothing Then
                PreparePreviewSource(_currentImagePath)
                previewSource = GetPreviewSource()
                If previewSource Is Nothing Then Return
            End If

            RegisterPreviewRenderStart()
            Dim requestId = _previewRequestId
            Dim adj = GetCurrentAdjustments(forPreview:=True)
            Dim cts = New CancellationTokenSource()
            Dim token = cts.Token
            Dim oldCts = Interlocked.Exchange(_previewRenderCts, cts)
            CancelAndDisposePreviewCts(oldCts)

            Try
                StatusText = LocalizationService.T("Vorschau wird berechnet…")
                PreviewFailed = False
                Dim needsComparison = _showBeforeImage
                Dim result = Await Task.Run(Function()
                                                token.ThrowIfCancellationRequested()
                                                Dim previewBmp = ImageProcessor.ApplyAdjustments(previewSource, adj)
                                                ' Ab hier ist previewBmp ein fertiges Bitmap mit unmanaged Skia-Speicher.
                                                ' Bricht der Render danach ab (neuer Slider-Tick) oder wirft das
                                                ' Vergleichsbild, würde es niemand mehr freigeben - Avalonias Bitmap hat
                                                ' keinen Finalizer. Deshalb ab jetzt alles im Try mit Aufräumpfad.
                                                Dim comparisonBmp As Bitmap = Nothing
                                                Try
                                                    token.ThrowIfCancellationRequested()
                                                    ' Das Vorher/Nachher-Vergleichsbild wird nur berechnet, wenn der
                                                    ' Vorher/Nachher-Regler gerade sichtbar ist (ShowBeforeImage) - sonst
                                                    ' wäre das bei jedem einzelnen Live-Vorschau-Frame verschwendete Arbeit.
                                                    If needsComparison Then
                                                        comparisonBmp = ImageProcessor.ApplyGeometryAdjustments(previewSource, adj)
                                                    End If
                                                    Return New PreviewRenderResult(previewBmp, comparisonBmp)
                                                Catch
                                                    previewBmp?.Dispose()
                                                    comparisonBmp?.Dispose()
                                                    Throw
                                                End Try
                                            End Function, token)

                If token.IsCancellationRequested OrElse requestId <> _previewRequestId Then
                    result.Dispose()
                    Return
                End If

                PreviewImage = result.Preview
                ComparisonImage = result.Comparison
                _previewPending = False
                StatusText = LocalizationService.T("Vorschau bereit")
                PreviewFailed = False
                result.Preview = Nothing
                result.Comparison = Nothing
                result.Dispose()
            Catch ex As OperationCanceledException
            Catch ex As Exception
                If IsIgnorablePreviewException(ex) Then
                    If requestId <> _previewRequestId OrElse _previewPending Then
                        StatusText = LocalizationService.T("Vorschau wird aktualisiert...")
                    Else
                        StatusText = LocalizationService.T("Vorschau bereit")
                    End If
                Else
                    ' Ein Fehler in der Pipeline DARF NICHT wie ein Erfolg aussehen. Vorher stand hier
                    ' "Vorschau bereit", während das alte Bild stehen blieb - ein kaputtes Werkzeug war
                    ' dadurch nicht von einem zu unterscheiden, das gerade nichts verändert. Genau so ritt
                    ' die zerschossene Tonwertkurve (SkiaSharp 3.119.4) unbemerkt durch eine Version.
                    ' Der Zustand bleibt stehen, bis eine Vorschau wieder durchläuft.
                    StatusText = LocalizationService.T("Vorschau fehlgeschlagen: ") & ex.Message
                    PreviewFailed = True
                    LogPreviewError(ex)
                End If
            Finally
                If ReferenceEquals(_previewRenderCts, cts) Then
                    _previewRenderCts = Nothing
                End If
                CancelAndDisposePreviewCts(cts)
                RegisterPreviewRenderEnd()
            End Try
        End Function

        ''' <summary>Vollständige Exception-Details (inkl. Stacktrace) für Vorschau-Fehler - die im
        ''' UI angezeigte StatusText-Meldung zeigt nur ex.Message, das reicht zur Ferndiagnose
        ''' seltener/schwer reproduzierbarer Fehler nicht aus. Nur aktiv, wenn in den Einstellungen
        ''' eingeschaltet (Settings.EnableDiagnosticLogging).</summary>
        Private Shared Sub LogPreviewError(ex As Exception)
            DiagnosticLogService.LogException("EditorPreview", ex)
        End Sub

        Private Async Function SaveImageAsync(saveAs As Boolean) As Task(Of Boolean)
            If String.IsNullOrEmpty(_currentImagePath) Then Return False
            If Not saveAs AndAlso Not CanSaveInPlace Then Return False
            ' "Speichern" bei einem Immich-Bild schreibt nicht die Temp-Kopie zurück, sondern das Asset.
            If Not saveAs AndAlso SavesBackToImmich Then Return Await SaveBackToImmichAsync()
            Dim targetPath = _currentImagePath
            Dim targetQuality = SaveQuality
            Dim saveToImmich As Boolean = False
            If saveAs Then
                ' Externe Quellen (Immich-Temp-Kopie) liegen im Temp-Verzeichnis - als Ziel taugt das nicht,
                ' daher den Bilder-Ordner vorschlagen statt den Temp-Pfad.
                Dim dir = If(_forceSaveAsOnly, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), IO.Path.GetDirectoryName(_currentImagePath))
                Dim name = IO.Path.GetFileNameWithoutExtension(_currentImagePath)
                Dim proposedName = name & "_bearbeitet"
                Dim saveAsResult = Await _mainVm.ShowSaveAsAsync("Speichern unter",
                                                                 "Dateiname eingeben",
                                                                 proposedName,
                                                                 GetFormatFromExtension(IO.Path.GetExtension(_currentImagePath)),
                                                                 SaveQuality,
                                                                 "Speichern",
                                                                 "Abbrechen")
                If saveAsResult Is Nothing OrElse String.IsNullOrWhiteSpace(saveAsResult.BaseName) Then Return False

                Dim cleanBaseName = IO.Path.GetFileNameWithoutExtension(saveAsResult.BaseName.Trim())
                If HasInvalidFileNameChars(cleanBaseName) Then
                    Await _mainVm.ShowMessageAsync("Speichern fehlgeschlagen", "Der Dateiname enthält ungültige Zeichen.")
                    Return False
                End If
                saveToImmich = String.Equals(saveAsResult.Target, "Immich", StringComparison.OrdinalIgnoreCase) AndAlso ImmichService.IsConfigured
                If saveToImmich Then
                    ' Für den Immich-Upload zunächst in eine Temp-Datei rendern (nicht in den Bilder-Ordner).
                    Dim uploadTempDir = IO.Path.Combine(IO.Path.GetTempPath(), "FerrumPix", "ImmichUpload")
                    IO.Directory.CreateDirectory(uploadTempDir)
                    targetPath = IO.Path.Combine(uploadTempDir, cleanBaseName & saveAsResult.Extension)
                Else
                    If Not String.IsNullOrWhiteSpace(saveAsResult.TargetFolder) Then dir = saveAsResult.TargetFolder
                    If Not IO.Directory.Exists(dir) Then IO.Directory.CreateDirectory(dir)
                    targetPath = IO.Path.Combine(dir, cleanBaseName & saveAsResult.Extension)
                End If
                targetQuality = saveAsResult.JpgQuality
            End If

            Dim errorMessage As String = Nothing
            Try
                StatusText = LocalizationService.T("Wird gespeichert…")
                Dim adj = GetCurrentAdjustments()
                Dim preserveMetadata = If(saveAs AndAlso _mainVm?.Settings IsNot Nothing, _mainVm.Settings.PreserveMetadataOnSave, True)
                Dim ok = Await Task.Run(Function() ImageProcessor.SaveImage(_currentImagePath, targetPath, adj, targetQuality, preserveMetadata))
                If ok AndAlso saveToImmich Then
                    StatusText = LocalizationService.T("Wird nach Immich hochgeladen…")
                    Dim assetId = Await ImmichService.UploadAssetAsync(targetPath)
                    Try : IO.File.Delete(targetPath) : Catch : End Try
                    If String.IsNullOrEmpty(assetId) Then
                        StatusText = LocalizationService.T("Immich-Upload fehlgeschlagen")
                        Return False
                    End If
                    If Not String.IsNullOrEmpty(_immichSourceAlbumId) Then
                        Await ImmichService.AddAssetsToAlbumAsync(_immichSourceAlbumId, {assetId})
                    End If
                    Await ImmichService.WaitForThumbnailReadyAsync(assetId)
                    StatusText = LocalizationService.T("Nach Immich hochgeladen")
                    _hasChanges = False
                    ClearPreviewSource()
                    Return True
                End If
                If ok Then
                    StatusText = If(saveAs,
                                    $"{LocalizationService.T("Gespeichert als")} {IO.Path.GetFileName(targetPath)}",
                                    LocalizationService.T("Gespeichert"))
                    _hasChanges = False
                    ClearPreviewSource()
                    Return True
                Else
                    StatusText = LocalizationService.T("Speichern fehlgeschlagen")
                    Return False
                End If
            Catch ex As Exception
                StatusText = LocalizationService.T("Fehler: ") & ex.Message
                errorMessage = ex.Message
            End Try
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Speichern fehlgeschlagen", errorMessage)
            Return False
        End Function

        ''' <summary>"Speichern" bei einem Immich-Bild (nur mit "Vorhandene Assets aktualisieren"): die
        ''' Bearbeitung wird in eine Temp-Datei gerendert und ersetzt damit das Quell-Asset. Ab Immich v3
        ''' bekommt das Asset dabei zwangsläufig eine neue ID (siehe ImmichService.ReplaceAssetAsync) -
        ''' der Editor holt sich danach die Temp-Kopie des ERGEBNISSES und arbeitet auf der weiter,
        ''' sonst zeigt er eine Datei an, die es auf dem Server so nicht mehr gibt.</summary>
        Private Async Function SaveBackToImmichAsync() As Task(Of Boolean)
            Dim assetId = CurrentImmichAssetId()
            If String.IsNullOrEmpty(assetId) Then Return False

            Dim sourcePath = _currentImagePath
            Dim fileName = If(String.IsNullOrWhiteSpace(_immichSourceFileName), IO.Path.GetFileName(sourcePath), _immichSourceFileName)
            Dim uploadDir = IO.Path.Combine(IO.Path.GetTempPath(), "FerrumPix", "ImmichUpload")
            IO.Directory.CreateDirectory(uploadDir)
            ' Denselben Dateinamen behalten: Immich zeigt ihn als Originalnamen des Assets an.
            Dim renderPath = IO.Path.Combine(uploadDir, IO.Path.GetFileNameWithoutExtension(fileName) & IO.Path.GetExtension(sourcePath))

            Dim errorMessage As String = Nothing
            Try
                StatusText = LocalizationService.T("Wird gespeichert…")
                Dim adj = GetCurrentAdjustments()
                Dim preserveMetadata = If(_mainVm?.Settings IsNot Nothing, _mainVm.Settings.PreserveMetadataOnSave, True)
                Dim ok = Await Task.Run(Function() ImageProcessor.SaveImage(sourcePath, renderPath, adj, SaveQuality, preserveMetadata))
                If Not ok Then
                    StatusText = LocalizationService.T("Speichern fehlgeschlagen")
                    Return False
                End If

                StatusText = LocalizationService.T("Immich-Asset wird aktualisiert…")
                Dim newAssetId = Await ImmichService.ReplaceAssetAsync(assetId, renderPath)
                If String.IsNullOrEmpty(newAssetId) Then
                    StatusText = LocalizationService.T("Immich-Upload fehlgeschlagen")
                    Return False
                End If
                Await ImmichService.WaitForThumbnailReadyAsync(newAssetId)

                _hasChanges = False
                ClearPreviewSource()

                ' Die Temp-Kopie des alten Assets ist mit dem Ersetzen weggeräumt worden. Auf die des neuen
                ' umschalten (im Filmstreifen an derselben Stelle), damit Vorher/Nachher, erneutes Speichern
                ' und die Metadaten-Leiste wieder auf dem echten Serverzustand stehen.
                Dim localPath = Await ImmichService.DownloadOriginalToTempAsync(newAssetId, fileName)
                If Not String.IsNullOrEmpty(localPath) Then
                    Dim paths = _folderPaths.ToList()
                    Dim index = paths.FindIndex(Function(p) String.Equals(p, sourcePath, StringComparison.OrdinalIgnoreCase))
                    If index >= 0 Then
                        paths(index) = localPath
                    Else
                        paths = New List(Of String) From {localPath}
                    End If
                    Await OpenImageAsync(localPath, paths, _thumbCacheScopeId, _thumbCacheScopeName,
                                         forceSaveAsOnly:=True, immichAlbumId:=_immichSourceAlbumId)
                End If

                Dim gallery = _mainVm?.Gallery
                If gallery IsNot Nothing Then Await gallery.RefreshImmichViewAsync()

                StatusText = LocalizationService.T("Immich-Asset aktualisiert")
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.SaveBackToImmich", ex)
                StatusText = LocalizationService.T("Fehler: ") & ex.Message
                errorMessage = ex.Message
            Finally
                Try
                    If IO.File.Exists(renderPath) Then IO.File.Delete(renderPath)
                Catch
                End Try
            End Try

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Speichern fehlgeschlagen", errorMessage)
            Return False
        End Function

        Public Async Function ConfirmSaveBeforeLeavingAsync(actionDescription As String) As Task(Of Boolean)
            If Not _hasChanges Then Return True
            If _mainVm Is Nothing Then Return True

            Dim message As String
            If String.IsNullOrWhiteSpace(actionDescription) Then
                message = "Es gibt ungespeicherte Änderungen. Möchtest du sie speichern?"
            Else
                message = $"Es gibt ungespeicherte Änderungen. Möchtest du sie speichern, bevor du {actionDescription}?"
            End If

            Dim save = Await _mainVm.ShowConfirmAsync("Änderungen speichern", message, "Speichern", "Nicht speichern")
            If Not save Then
                ' Nutzer hat "Nicht speichern" gewählt - alle nicht gespeicherten/nicht angewendeten
                ' Änderungen verwerfen. Ohne dies bliebe _hasChanges fälschlich True, und ein späteres
                ' Öffnen eines ANDEREN Bildes würde diesen bereits abgelehnten Speichern-Dialog ein
                ' zweites Mal spurious anzeigen (OpenImageAsync macht denselben Check erneut).
                ResetAdjustmentsInternal()
                ClearUndoHistory()
                Return True
            End If
            ' Bei RAW-Bildern ist Speichern-in-place deaktiviert (siehe CanSaveInPlace) - "Speichern"
            ' im Bestätigungsdialog leitet hier deshalb automatisch auf Speichern-unter um, statt
            ' unbedingt in-place zu speichern (das würde sonst versuchen, die RAW-Datei zu überschreiben).
            ' Dasselbe gilt für Immich-Bilder, solange sie ihr Asset nicht ersetzen dürfen.
            Return Await SaveImageAsync(Not CanSaveInPlace)
        End Function

        ''' <remarks>NegativeEnabled: nur die VORSCHAU blendet die Umkehr während der Pipetten-Aufnahme
        ''' aus (siehe _suppressNegativeForPick). Der kanonische Stand - Undo-Schnappschuss, Speichern -
        ''' bleibt davon unberührt.</remarks>
        Private Function GetCurrentAdjustments(Optional forPreview As Boolean = False) As ImageAdjustments
            Dim adj = New ImageAdjustments With {
                .Brightness = CSng(_brightness),
                .Contrast = CSng(_contrast),
                .Saturation = CSng(_saturation),
                .Vibrance = CSng(_vibrance),
                .Highlights = CSng(_highlights),
                .ShadowsLevel = CSng(_shadowsLevel),
                .Whites = CSng(_whites),
                .Blacks = CSng(_blacks),
                .Temperature = CSng(_temperature),
                .Tint = CSng(_tint),
                .Exposure = CSng(_exposure),
                .Sharpness = CSng(_sharpness),
                .NoiseReduction = CSng(_noiseReduction),
                .NoiseReductionMethod = _noiseReductionMethod,
                .DustScratches = CSng(_dustScratches),
                .Haze = CSng(_haze),
                .AddNoise = CSng(_addNoise),
                .[Structure] = CSng(_structure),
                .Glow = CSng(_glow),
                .Vignette = CSng(_vignette),
                .VignetteTransition = CSng(_vignetteTransition),
                .VignetteRoundness = CSng(_vignetteRoundness),
                .VignetteFeather = CSng(_vignetteFeather),
                .VignetteCenterX = CSng(_vignetteCenterX),
                .VignetteCenterY = CSng(_vignetteCenterY),
                .Grain = CSng(_grain),
                .BorderSize = CSng(_borderSize),
                .BorderColor = _borderColor,
                .BorderCornerRadius = CSng(_borderCornerRadius),
                .BorderEffect = _borderEffect,
                .Clarity = CSng(_clarity),
                .NegativeEnabled = _negativeEnabled AndAlso Not (forPreview AndAlso _suppressNegativeForPick),
                .NegativeMonochrome = _negativeMonochrome,
                .NegativeBaseColor = _negativeBaseColor,
                .NegativeDensityColor = _negativeDensityColor,
                .NegativeGamma = CSng(_negativeGamma),
                .CurveRgbPoints = PointsToCurveString(_curveRgbPoints),
                .CurveRedPoints = PointsToCurveString(_curveRedPoints),
                .CurveGreenPoints = PointsToCurveString(_curveGreenPoints),
                .CurveBluePoints = PointsToCurveString(_curveBluePoints),
                .CurveLuminancePoints = PointsToCurveString(_curveLuminancePoints),
                .RedHue = CSng(_redHue),
                .RedSaturation = CSng(_redSaturation),
                .RedLuminance = CSng(_redLuminance),
                .OrangeHue = CSng(_orangeHue),
                .OrangeSaturation = CSng(_orangeSaturation),
                .OrangeLuminance = CSng(_orangeLuminance),
                .YellowHue = CSng(_yellowHue),
                .YellowSaturation = CSng(_yellowSaturation),
                .YellowLuminance = CSng(_yellowLuminance),
                .GreenHue = CSng(_greenHue),
                .GreenSaturation = CSng(_greenSaturation),
                .GreenLuminance = CSng(_greenLuminance),
                .AquaHue = CSng(_aquaHue),
                .AquaSaturation = CSng(_aquaSaturation),
                .AquaLuminance = CSng(_aquaLuminance),
                .BlueHue = CSng(_blueHue),
                .BlueSaturation = CSng(_blueSaturation),
                .BlueLuminance = CSng(_blueLuminance),
                .PurpleHue = CSng(_purpleHue),
                .PurpleSaturation = CSng(_purpleSaturation),
                .PurpleLuminance = CSng(_purpleLuminance),
                .MagentaHue = CSng(_magentaHue),
                .MagentaSaturation = CSng(_magentaSaturation),
                .MagentaLuminance = CSng(_magentaLuminance),
                .SplitToningShadowHue = CSng(_splitToningShadowHue),
                .SplitToningShadowSaturation = CSng(_splitToningShadowSaturation),
                .SplitToningHighlightHue = CSng(_splitToningHighlightHue),
                .SplitToningHighlightSaturation = CSng(_splitToningHighlightSaturation),
                .SplitToningBalance = CSng(_splitToningBalance),
                .RotationDegrees = If(forPreview, _rotationDegrees, _appliedRotationDegrees),
                .StraightenDegrees = CSng(If(forPreview, _straightenDegrees, _appliedStraightenDegrees)),
                .StraightenExpandCanvas = If(forPreview, _straightenExpandCanvas, _appliedStraightenExpandCanvas),
                .FlipHorizontal = If(forPreview, _flipH, _appliedFlipH),
                .FlipVertical = If(forPreview, _flipV, _appliedFlipV),
                .CropLeftPercent = CSng(_appliedCropLeft),
                .CropTopPercent = CSng(_appliedCropTop),
                .CropRightPercent = CSng(_appliedCropRight),
                .CropBottomPercent = CSng(_appliedCropBottom),
                .ResizeWidth = If(forPreview, _resizeWidth, _appliedResizeWidth),
                .ResizeHeight = If(forPreview, _resizeHeight, _appliedResizeHeight),
                .LockResizeAspect = _lockResizeAspect,
                .ResizeInterpolation = _resizeInterpolation,
                .CanvasWidth = If(forPreview, _canvasWidth, _appliedCanvasWidth),
                .CanvasHeight = If(forPreview, _canvasHeight, _appliedCanvasHeight),
                .LockCanvasAspect = _lockCanvasAspect,
                .CanvasAnchor = _canvasAnchor,
                .CanvasBackgroundColor = _canvasBackgroundColor,
                .SourceWidthPixels = GetBaseWidth(),
                .SourceHeightPixels = GetBaseHeight(),
                .FilterPreset = _filterPreset,
                .FilterStrength = CSng(_filterStrength),
                .LutPath = _lutPath,
                .LutStrength = CSng(_lutStrength),
                .RetouchSpots = _retouchSpots.Select(Function(s) s.Clone()).ToList(),
                .Annotations = _annotations.Select(Function(a) a.Clone()).ToList(),
                .HasActiveSelection = _hasActiveSelection,
                .SelectionXPercent = _selectionXPercent,
                .SelectionYPercent = _selectionYPercent,
                .SelectionWidthPercent = _selectionWidthPercent,
                .SelectionHeightPercent = _selectionHeightPercent,
                .SelectionShapeMode = _selectionShapeMode,
                .SelectionShapePointsX = If(_selectionShapePointsX Is Nothing, Nothing, _selectionShapePointsX.ToArray()),
                .SelectionShapePointsY = If(_selectionShapePointsY Is Nothing, Nothing, _selectionShapePointsY.ToArray()),
                .SelectionMaskLeft = _selectionMaskRect.Left,
                .SelectionMaskTop = _selectionMaskRect.Top,
                .SelectionMaskRight = _selectionMaskRect.Right,
                .SelectionMaskBottom = _selectionMaskRect.Bottom,
                .SelectionMaskPngBase64 = EncodeSelectionMaskBase64()
            }

            ' Während die Text-/Wasserzeichen-Ebene per Canvas-Overlay live bearbeitet wird, zeigt das
            ' Overlay selbst schon eine gerenderte Vorschau (Textbox mit Schrift/Farbe). Damit sie nicht
            ' zusätzlich leicht versetzt im gebackenen Vorschaubild auftaucht, wird sie nur für die
            ' Live-Vorschau ausgeblendet (nicht beim Speichern oder in Undo-Snapshots).
            If forPreview AndAlso ComputesOverlayHidesSelection() Then
                adj.Annotations(_selectedAnnotationIndex).IsVisible = False
            End If

            Return adj
        End Function

        Private Function SetUndoableDouble(ByRef field As Double, value As Double, propertyName As String) As Boolean
            If Math.Abs(field - value) < 0.0001 Then Return False
            CaptureUndoState(propertyName)
            field = value
            Me.RaisePropertyChanged(propertyName)
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
            Return True
        End Function

        ''' Wie SetUndoableDouble, aber zusätzlich für die 16 HSL-Bandregler (RedHue...MagentaSaturation):
        ''' der Farbrad-Bandmischer (HslWheelPicker + ActiveHslHue/ActiveHslSaturation/ActiveHslLuminance)
        ''' zeigt/bearbeitet immer nur das GERADE per Rad ausgewählte Band, muss also bei JEDER Bandänderung
        ''' benachrichtigt werden, egal ob die Änderung von diesen Reglern selbst oder direkt (Undo/Reset) kommt.
        Private Function SetUndoableHslDouble(ByRef field As Double, value As Double, propertyName As String) As Boolean
            Dim changed = SetUndoableDouble(field, value, propertyName)
            If changed Then
                Me.RaisePropertyChanged(NameOf(ActiveHslHue))
                Me.RaisePropertyChanged(NameOf(ActiveHslSaturation))
                Me.RaisePropertyChanged(NameOf(ActiveHslLuminance))
            End If
            Return changed
        End Function

        Private Sub CaptureUndoState(propertyName As String)
            If _suppressUndoCapture Then Return

            Dim now = DateTime.UtcNow
            Dim shouldCapture =
                Not String.Equals(_lastUndoProperty, propertyName, StringComparison.Ordinal) OrElse
                (now - _lastUndoCapturedAt).TotalMilliseconds > UndoCaptureWindowMs

            If Not shouldCapture Then Return

            _undoStack.Push(GetCurrentAdjustments())
            _redoStack.Clear()
            _lastUndoProperty = propertyName
            _lastUndoCapturedAt = now
            AddHistoryEntry(GetHistoryLabelForProperty(propertyName))
            Me.RaisePropertyChanged(NameOf(CanUndo))
            Me.RaisePropertyChanged(NameOf(CanRedo))
        End Sub

        Private Sub ResetUndoCapture()
            _lastUndoProperty = ""
            _lastUndoCapturedAt = DateTime.MinValue
        End Sub

        Private Sub RaiseResetButtonStateChanged()
            Me.RaisePropertyChanged(NameOf(HasCropChanges))
            Me.RaisePropertyChanged(NameOf(HasImageResizeChanges))
            Me.RaisePropertyChanged(NameOf(HasCanvasSizeChanges))
            Me.RaisePropertyChanged(NameOf(HasColorChanges))
            Me.RaisePropertyChanged(NameOf(HasHslChanges))
            Me.RaisePropertyChanged(NameOf(HasTransformChanges))
            Me.RaisePropertyChanged(NameOf(HasAnnotationChanges))
        End Sub

        ''' Wird beim Verlassen eines Werkzeugs mit "Anwenden"-Bestätigung aufgerufen. Noch nicht
        ''' bestätigte Live-Werte werden verworfen und auf den zuletzt angewendeten Stand
        ''' zurückgesetzt, statt beim nächsten Speichern/Undo-Snapshot versehentlich als real zu
        ''' gelten (siehe GetCurrentAdjustments, das für forPreview:=False ohnehin nur die
        ''' Applied-Felder liest - dieser Reset sorgt zusätzlich dafür, dass auch die Live-Vorschau
        ''' und die Eingabefelder selbst wieder den angewendeten Stand zeigen).
        Private Sub DiscardUncommittedToolEdits(previousTool As EditorTool)
            Dim reverted = False
            Select Case previousTool
                Case EditorTool.Crop
                    If HasCropChanges Then
                        ' Nicht bestätigter Beschnitt wird verworfen: der offene Beschnitt geht auf Null
                        ' zurück, der bereits angewendete bleibt unangetastet.
                        SetCropValues(0, 0, 0, 0)
                        reverted = True
                    End If
                Case EditorTool.Resize
                    If HasImageResizeChanges Then
                        _resizeWidth = _appliedResizeWidth
                        _resizeHeight = _appliedResizeHeight
                        Me.RaisePropertyChanged(NameOf(ResizeWidth))
                        Me.RaisePropertyChanged(NameOf(ResizeHeight))
                        reverted = True
                    End If
                    If HasCanvasSizeChanges Then
                        _canvasWidth = _appliedCanvasWidth
                        _canvasHeight = _appliedCanvasHeight
                        Me.RaisePropertyChanged(NameOf(CanvasWidth))
                        Me.RaisePropertyChanged(NameOf(CanvasHeight))
                        reverted = True
                    End If
                    If reverted Then Me.RaisePropertyChanged(NameOf(OutputSizeText))
                Case EditorTool.Rotate, EditorTool.Transform
                    If HasTransformChanges Then
                        _rotationDegrees = _appliedRotationDegrees
                        _straightenDegrees = _appliedStraightenDegrees
                        _straightenExpandCanvas = _appliedStraightenExpandCanvas
                        _flipH = _appliedFlipH
                        _flipV = _appliedFlipV
                        Me.RaisePropertyChanged(NameOf(StraightenDegrees))
                        Me.RaisePropertyChanged(NameOf(StraightenExpandCanvas))
                        reverted = True
                    End If
            End Select
            If reverted Then
                RaiseResetButtonStateChanged()
                ScheduleToolPreviewUpdate()
            End If
        End Sub

        Private Sub ClearUndoHistory()
            ResetUndoCapture()
            _undoStack.Clear()
            _redoStack.Clear()
            Me.RaisePropertyChanged(NameOf(CanUndo))
            Me.RaisePropertyChanged(NameOf(CanRedo))
        End Sub

        Private Shared Function GetHistoryLabelForProperty(propertyName As String) As String
            Select Case propertyName
                Case NameOf(CropLeft), NameOf(CropTop), NameOf(CropRight), NameOf(CropBottom)
                    Return "Zuschneiden"
                Case NameOf(ResizeWidth), NameOf(ResizeHeight), NameOf(LockResizeAspect), NameOf(ResizeInterpolationLabel)
                    Return "Bildgröße"
                Case NameOf(CanvasWidth), NameOf(CanvasHeight), NameOf(LockCanvasAspect), NameOf(CanvasBackgroundColor)
                    Return "Leinwandgröße"
                Case NameOf(StraightenDegrees), NameOf(StraightenExpandCanvas)
                    Return "Gerade richten"
                Case "Tonwertkurve"
                    Return "Tonwertkurve"
                Case NameOf(RedHue), NameOf(RedSaturation), NameOf(OrangeHue), NameOf(OrangeSaturation),
                     NameOf(YellowHue), NameOf(YellowSaturation), NameOf(GreenHue), NameOf(GreenSaturation),
                     NameOf(AquaHue), NameOf(AquaSaturation), NameOf(BlueHue), NameOf(BlueSaturation),
                     NameOf(PurpleHue), NameOf(PurpleSaturation), NameOf(MagentaHue), NameOf(MagentaSaturation)
                    Return "Farbmischer"
                Case NameOf(Sharpness), NameOf(NoiseReduction), NameOf(NoiseReductionMethodLabel), NameOf(Clarity)
                    Return "Details"
                Case NameOf(Vignette), NameOf(VignetteTransition), NameOf(VignetteRoundness), NameOf(VignetteFeather),
                     NameOf(VignetteCenterX), NameOf(VignetteCenterY), NameOf(Grain), NameOf(BorderSize), NameOf(BorderColor)
                    Return "Effekte"
                Case NameOf(FilterPreset)
                    Return "Filter"
                Case NameOf(FilterStrength)
                    Return "Filterstärke"
                Case NameOf(WhiteBalance), NameOf(Temperature), NameOf(Tint)
                    Return "Weißabgleich"
                Case NameOf(NegativeEnabled), NameOf(NegativeMonochrome), NameOf(NegativeGamma)
                    Return "Filmnegativ"
                Case Else
                    Return "Anpassung"
            End Select
        End Function

        Private Sub PushUndo()
            ResetUndoCapture()
            ' GetCurrentAdjustments klont alle Objekte und Retusche-Punkte und serialisiert fünf Kurven -
            ' einmal reicht, der Schnappschuss taugt auch als Vorlage für die Beschriftung.
            Dim snapshot = GetCurrentAdjustments()
            _undoStack.Push(snapshot)
            _redoStack.Clear()
            AddHistoryEntry(BuildHistoryLabel(snapshot))
            Me.RaisePropertyChanged(NameOf(CanUndo))
            Me.RaisePropertyChanged(NameOf(CanRedo))
        End Sub

        Private Sub UndoAction()
            If _undoStack.Count = 0 Then Return
            ResetUndoCapture()
            _redoStack.Push(GetCurrentAdjustments())
            _suppressUndoCapture = True
            Try
                ApplyAdjustments(_undoStack.Pop())
            Finally
                _suppressUndoCapture = False
            End Try
            AddHistoryEntry("Rückgängig")
            Me.RaisePropertyChanged(NameOf(CanUndo))
            Me.RaisePropertyChanged(NameOf(CanRedo))
        End Sub

        Private Sub RedoAction()
            If _redoStack.Count = 0 Then Return
            ResetUndoCapture()
            _undoStack.Push(GetCurrentAdjustments())
            _suppressUndoCapture = True
            Try
                ApplyAdjustments(_redoStack.Pop())
            Finally
                _suppressUndoCapture = False
            End Try
            AddHistoryEntry("Wiederholt")
            Me.RaisePropertyChanged(NameOf(CanUndo))
            Me.RaisePropertyChanged(NameOf(CanRedo))
        End Sub

        Private Sub ApplyAdjustments(adj As ImageAdjustments)
            _brightness = adj.Brightness
            _contrast = adj.Contrast
            _saturation = adj.Saturation
            _vibrance = adj.Vibrance
            _highlights = adj.Highlights
            _shadowsLevel = adj.ShadowsLevel
            _whites = adj.Whites
            _blacks = adj.Blacks
            _temperature = adj.Temperature
            _tint = adj.Tint
            _exposure = adj.Exposure
            _sharpness = adj.Sharpness
            _noiseReduction = adj.NoiseReduction
            _noiseReductionMethod = adj.NoiseReductionMethod
            _dustScratches = adj.DustScratches
            _haze = adj.Haze
            _addNoise = adj.AddNoise
            _structure = adj.[Structure]
            _glow = adj.Glow
            _vignette = adj.Vignette
            _vignetteTransition = adj.VignetteTransition
            _vignetteRoundness = adj.VignetteRoundness
            _vignetteFeather = adj.VignetteFeather
            _vignetteCenterX = adj.VignetteCenterX
            _vignetteCenterY = adj.VignetteCenterY
            _grain = adj.Grain
            _borderSize = adj.BorderSize
            _borderColor = If(String.IsNullOrWhiteSpace(adj.BorderColor), "#FFFFFFFF", adj.BorderColor)
            _borderCornerRadius = adj.BorderCornerRadius
            _borderEffect = If(String.IsNullOrWhiteSpace(adj.BorderEffect), "Einfach", adj.BorderEffect)
            _clarity = adj.Clarity
            _negativeEnabled = adj.NegativeEnabled
            _negativeMonochrome = adj.NegativeMonochrome
            _negativeBaseColor = adj.NegativeBaseColor
            _negativeDensityColor = adj.NegativeDensityColor
            _negativeGamma = adj.NegativeGamma
            LoadCurvePointsFromString(_curveRgbPoints, adj.CurveRgbPoints)
            LoadCurvePointsFromString(_curveRedPoints, adj.CurveRedPoints)
            LoadCurvePointsFromString(_curveGreenPoints, adj.CurveGreenPoints)
            LoadCurvePointsFromString(_curveBluePoints, adj.CurveBluePoints)
            LoadCurvePointsFromString(_curveLuminancePoints, adj.CurveLuminancePoints)
            _redHue = adj.RedHue
            _redSaturation = adj.RedSaturation
            _redLuminance = adj.RedLuminance
            _orangeHue = adj.OrangeHue
            _orangeSaturation = adj.OrangeSaturation
            _orangeLuminance = adj.OrangeLuminance
            _yellowHue = adj.YellowHue
            _yellowSaturation = adj.YellowSaturation
            _yellowLuminance = adj.YellowLuminance
            _greenHue = adj.GreenHue
            _greenSaturation = adj.GreenSaturation
            _greenLuminance = adj.GreenLuminance
            _aquaHue = adj.AquaHue
            _aquaSaturation = adj.AquaSaturation
            _aquaLuminance = adj.AquaLuminance
            _blueHue = adj.BlueHue
            _blueSaturation = adj.BlueSaturation
            _blueLuminance = adj.BlueLuminance
            _purpleHue = adj.PurpleHue
            _purpleSaturation = adj.PurpleSaturation
            _purpleLuminance = adj.PurpleLuminance
            _magentaHue = adj.MagentaHue
            _magentaSaturation = adj.MagentaSaturation
            _magentaLuminance = adj.MagentaLuminance
            _splitToningShadowHue = adj.SplitToningShadowHue
            _splitToningShadowSaturation = adj.SplitToningShadowSaturation
            _splitToningHighlightHue = adj.SplitToningHighlightHue
            _splitToningHighlightSaturation = adj.SplitToningHighlightSaturation
            _splitToningBalance = adj.SplitToningBalance
            _rotationDegrees = adj.RotationDegrees
            _straightenDegrees = adj.StraightenDegrees
            _straightenExpandCanvas = adj.StraightenExpandCanvas
            _flipH = adj.FlipHorizontal
            _flipV = adj.FlipVertical
            _appliedRotationDegrees = adj.RotationDegrees
            _appliedStraightenDegrees = adj.StraightenDegrees
            _appliedStraightenExpandCanvas = adj.StraightenExpandCanvas
            _appliedFlipH = adj.FlipHorizontal
            _appliedFlipV = adj.FlipVertical
            ' Der geladene Beschnitt ist bereits angewendet; der offene Beschnitt startet leer, sonst
            ' läge das Auswahlrechteck sofort wieder im Maßstab des unbeschnittenen Originals.
            _cropLeft = 0
            _cropTop = 0
            _cropRight = 0
            _cropBottom = 0
            _appliedCropLeft = adj.CropLeftPercent
            _appliedCropTop = adj.CropTopPercent
            _appliedCropRight = adj.CropRightPercent
            _appliedCropBottom = adj.CropBottomPercent
            _resizeWidth = adj.ResizeWidth
            _resizeHeight = adj.ResizeHeight
            _appliedResizeWidth = adj.ResizeWidth
            _appliedResizeHeight = adj.ResizeHeight
            _lockResizeAspect = adj.LockResizeAspect
            _resizeInterpolation = adj.ResizeInterpolation
            _canvasWidth = adj.CanvasWidth
            _canvasHeight = adj.CanvasHeight
            _appliedCanvasWidth = adj.CanvasWidth
            _appliedCanvasHeight = adj.CanvasHeight
            _lockCanvasAspect = adj.LockCanvasAspect
            _canvasAnchor = If(String.IsNullOrWhiteSpace(adj.CanvasAnchor), "Center", adj.CanvasAnchor)
            _canvasBackgroundColor = If(String.IsNullOrWhiteSpace(adj.CanvasBackgroundColor), "#FF000000", adj.CanvasBackgroundColor)
            _filterPreset = adj.FilterPreset
            _filterStrength = If(adj.FilterStrength <= 0, 100, adj.FilterStrength)
            _lutPath = adj.LutPath
            _lutStrength = If(adj.LutStrength <= 0, 100, adj.LutStrength)
            _retouchSpots.Clear()
            If adj.RetouchSpots IsNot Nothing Then
                For Each spot In adj.RetouchSpots
                    _retouchSpots.Add(spot.Clone())
                Next
            End If
            _annotations.Clear()
            _selectedAnnotationIndex = -1
            If adj.Annotations IsNot Nothing Then
                For Each annotation In adj.Annotations
                    _annotations.Add(annotation.Clone())
                Next
            End If
            ClearSelectionMask()
            _hasActiveSelection = adj.HasActiveSelection
            _selectionXPercent = adj.SelectionXPercent
            _selectionYPercent = adj.SelectionYPercent
            _selectionWidthPercent = adj.SelectionWidthPercent
            _selectionHeightPercent = adj.SelectionHeightPercent
            _selectionShapeMode = If(String.IsNullOrWhiteSpace(adj.SelectionShapeMode), "Rectangle", adj.SelectionShapeMode)
            _selectionShapePointsX = If(adj.SelectionShapePointsX Is Nothing, Nothing, adj.SelectionShapePointsX.ToArray())
            _selectionShapePointsY = If(adj.SelectionShapePointsY Is Nothing, Nothing, adj.SelectionShapePointsY.ToArray())
            Dim restoredMask = DecodeSelectionMaskBase64(adj.SelectionMaskPngBase64)
            If restoredMask IsNot Nothing Then
                SetSelectionMaskData(restoredMask, New SKRectI(adj.SelectionMaskLeft, adj.SelectionMaskTop, adj.SelectionMaskRight, adj.SelectionMaskBottom))
            End If
            Me.RaisePropertyChanged(NameOf(Brightness))
            Me.RaisePropertyChanged(NameOf(Contrast))
            Me.RaisePropertyChanged(NameOf(Saturation))
            Me.RaisePropertyChanged(NameOf(Vibrance))
            Me.RaisePropertyChanged(NameOf(Highlights))
            Me.RaisePropertyChanged(NameOf(ShadowsLevel))
            Me.RaisePropertyChanged(NameOf(Whites))
            Me.RaisePropertyChanged(NameOf(Blacks))
            Me.RaisePropertyChanged(NameOf(Temperature))
            Me.RaisePropertyChanged(NameOf(Tint))
            Me.RaisePropertyChanged(NameOf(Exposure))
            Me.RaisePropertyChanged(NameOf(Sharpness))
            Me.RaisePropertyChanged(NameOf(NoiseReduction))
            Me.RaisePropertyChanged(NameOf(NoiseReductionMethodLabel))
            RaiseEffectsPropertiesChanged()
            RaiseExtendedAdjustmentProperties()
            Me.RaisePropertyChanged(NameOf(CropLeft))
            Me.RaisePropertyChanged(NameOf(CropTop))
            Me.RaisePropertyChanged(NameOf(CropRight))
            Me.RaisePropertyChanged(NameOf(CropBottom))
            Me.RaisePropertyChanged(NameOf(ResizeWidth))
            Me.RaisePropertyChanged(NameOf(ResizeHeight))
            Me.RaisePropertyChanged(NameOf(LockResizeAspect))
            Me.RaisePropertyChanged(NameOf(ResizeInterpolationLabel))
            Me.RaisePropertyChanged(NameOf(CanvasWidth))
            Me.RaisePropertyChanged(NameOf(CanvasHeight))
            Me.RaisePropertyChanged(NameOf(LockCanvasAspect))
            Me.RaisePropertyChanged(NameOf(CanvasAnchor))
            Me.RaisePropertyChanged(NameOf(CanvasBackgroundColor))
            Me.RaisePropertyChanged(NameOf(CanvasBackgroundColorValue))
            Me.RaisePropertyChanged(NameOf(CanvasBackgroundBrush))
            Me.RaisePropertyChanged(NameOf(FilterPreset))
            Me.RaisePropertyChanged(NameOf(FilterStrength))
            Me.RaisePropertyChanged(NameOf(LutPath))
            Me.RaisePropertyChanged(NameOf(LutStrength))
            Me.RaisePropertyChanged(NameOf(HasLutApplied))
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationIndex))
            Me.RaisePropertyChanged(NameOf(HasSelectedAnnotation))
            Me.RaisePropertyChanged(NameOf(HasActiveSelection))
            Me.RaisePropertyChanged(NameOf(SelectionXPercent))
            Me.RaisePropertyChanged(NameOf(SelectionYPercent))
            Me.RaisePropertyChanged(NameOf(SelectionWidthPercent))
            Me.RaisePropertyChanged(NameOf(SelectionHeightPercent))
            Me.RaisePropertyChanged(NameOf(SelectionShapeMode))
            Me.RaisePropertyChanged(NameOf(SelectionShapePointsX))
            Me.RaisePropertyChanged(NameOf(SelectionShapePointsY))
            RaiseCropPropertiesChanged()
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Drehen/Spiegeln wirkt auf das MARKIERTE OBJEKT, wenn eines markiert ist - Text, Bild,
        ''' Form, eine aus der Auswahl kopierte Fläche, alles gleichermaßen. Die Anfasser am Objekt können
        ''' nur frei drehen und skalieren; exakte 90°-Schritte und Spiegeln gibt es nur hier. Ohne markiertes
        ''' Objekt bleibt es beim bisherigen Verhalten: das ganze Bild dreht/spiegelt sich.</summary>
        Private Sub DoRotate(degrees As Integer)
            If HasSelectedAnnotation Then
                PushUndo()
                ' Objektdrehung läuft in [-180, 180]; nach 180 kippt sie auf -180 (identische Lage).
                Dim rotated = ((_annotationRotation + degrees + 180.0) Mod 360.0 + 360.0) Mod 360.0 - 180.0
                AnnotationRotation = rotated
                AddHistoryEntry(If(degrees < 0, "Objekt links gedreht", "Objekt rechts gedreht"))
                Return
            End If
            _rotationDegrees = ((_rotationDegrees + degrees) Mod 360 + 360) Mod 360
            RaiseResetButtonStateChanged()
            UpdatePreview()
        End Sub

        Private Sub DoFlipH()
            If HasSelectedAnnotation Then
                PushUndo()
                AnnotationFlipHorizontal = Not _annotationFlipH
                AddHistoryEntry("Objekt horizontal gespiegelt")
                Return
            End If
            _flipH = Not _flipH
            RaiseResetButtonStateChanged()
            UpdatePreview()
        End Sub

        Private Sub DoFlipV()
            If HasSelectedAnnotation Then
                PushUndo()
                AnnotationFlipVertical = Not _annotationFlipV
                AddHistoryEntry("Objekt vertikal gespiegelt")
                Return
            End If
            _flipV = Not _flipV
            RaiseResetButtonStateChanged()
            UpdatePreview()
        End Sub

        Private Sub ResetAdjustmentsInternal(Optional resetEditorUi As Boolean = False)
            _brightness = 0
            _contrast = 0
            _saturation = 0
            _vibrance = 0
            _highlights = 0
            _shadowsLevel = 0
            _whites = 0
            _blacks = 0
            _temperature = 0
            _tint = 0
            _splitToningShadowHue = 0
            _splitToningShadowSaturation = 0
            _splitToningHighlightHue = 0
            _splitToningHighlightSaturation = 0
            _splitToningBalance = 0
            _exposure = 0
            _sharpness = 0
            _noiseReduction = 0
            _noiseReductionMethod = NoiseReductionMethod.Gaussian
            _vignette = 0
            _grain = 0
            _borderSize = 0
            _borderColor = "#FFFFFFFF"
            _clarity = 0
            _negativeEnabled = False
            _negativeMonochrome = False
            _negativeBaseColor = ""
            _negativeDensityColor = ""
            _negativeGamma = 0
            ResetCurvePoints()
            ResetHslFields()
            _rotationDegrees = 0
            _straightenDegrees = 0
            _straightenExpandCanvas = False
            _flipH = False
            _flipV = False
            _appliedRotationDegrees = 0
            _appliedStraightenDegrees = 0
            _appliedStraightenExpandCanvas = False
            _appliedFlipH = False
            _appliedFlipV = False
            _cropLeft = 0
            _cropTop = 0
            _cropRight = 0
            _cropBottom = 0
            _appliedCropLeft = 0
            _appliedCropTop = 0
            _appliedCropRight = 0
            _appliedCropBottom = 0
            _resizeWidth = 0
            _resizeHeight = 0
            _appliedResizeWidth = 0
            _appliedResizeHeight = 0
            _lockResizeAspect = True
            _resizeInterpolation = ResizeInterpolationMode.Bilinear
            _canvasWidth = 0
            _canvasHeight = 0
            _appliedCanvasWidth = 0
            _appliedCanvasHeight = 0
            _lockCanvasAspect = True
            _canvasAnchor = "Center"
            _canvasBackgroundColor = "#FF000000"
            _filterPreset = "Keine"
            _filterStrength = 100
            _lutPath = ""
            _lutStrength = 100
            _retouchSpots.Clear()
            _annotations.Clear()
            _selectedAnnotationIndex = -1
            If resetEditorUi Then ResetEditorUiStateForNewImage(resetTool:=False)
            _hasChanges = False
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationIndex))
            Me.RaisePropertyChanged(NameOf(HasSelectedAnnotation))
            Me.RaisePropertyChanged(NameOf(Brightness))
            Me.RaisePropertyChanged(NameOf(Contrast))
            Me.RaisePropertyChanged(NameOf(Saturation))
            Me.RaisePropertyChanged(NameOf(Vibrance))
            Me.RaisePropertyChanged(NameOf(Highlights))
            Me.RaisePropertyChanged(NameOf(ShadowsLevel))
            Me.RaisePropertyChanged(NameOf(Whites))
            Me.RaisePropertyChanged(NameOf(Blacks))
            Me.RaisePropertyChanged(NameOf(Temperature))
            Me.RaisePropertyChanged(NameOf(Tint))
            Me.RaisePropertyChanged(NameOf(Exposure))
            Me.RaisePropertyChanged(NameOf(Sharpness))
            Me.RaisePropertyChanged(NameOf(NoiseReduction))
            Me.RaisePropertyChanged(NameOf(NoiseReductionMethodLabel))
            RaiseEffectsPropertiesChanged()
            RaiseExtendedAdjustmentProperties()
            Me.RaisePropertyChanged(NameOf(CropLeft))
            Me.RaisePropertyChanged(NameOf(CropTop))
            Me.RaisePropertyChanged(NameOf(CropRight))
            Me.RaisePropertyChanged(NameOf(CropBottom))
            Me.RaisePropertyChanged(NameOf(ResizeWidth))
            Me.RaisePropertyChanged(NameOf(ResizeHeight))
            Me.RaisePropertyChanged(NameOf(LockResizeAspect))
            Me.RaisePropertyChanged(NameOf(ResizeInterpolationLabel))
            Me.RaisePropertyChanged(NameOf(CanvasWidth))
            Me.RaisePropertyChanged(NameOf(CanvasHeight))
            Me.RaisePropertyChanged(NameOf(LockCanvasAspect))
            Me.RaisePropertyChanged(NameOf(CanvasAnchor))
            Me.RaisePropertyChanged(NameOf(CanvasBackgroundColor))
            Me.RaisePropertyChanged(NameOf(CanvasBackgroundColorValue))
            Me.RaisePropertyChanged(NameOf(CanvasBackgroundBrush))
            Me.RaisePropertyChanged(NameOf(FilterPreset))
            Me.RaisePropertyChanged(NameOf(FilterStrength))
            Me.RaisePropertyChanged(NameOf(LutPath))
            Me.RaisePropertyChanged(NameOf(LutStrength))
            Me.RaisePropertyChanged(NameOf(HasLutApplied))
            RaiseCropPropertiesChanged()
            RaiseResetButtonStateChanged()
            PreviewImage = Nothing
            ComparisonImage = Nothing
            Me.RaisePropertyChanged(NameOf(DisplayImage))
            Me.RaisePropertyChanged(NameOf(BeforeDisplayImage))
        End Sub

        Private Sub ResetEditorUiStateForNewImage(Optional resetTool As Boolean = False)
            If resetTool Then _currentTool = EditorTool.Crop
            _pendingInsertKind = ""
            _selectedWatermarkPresetName = ""
            _watermarkPresetNameDraft = ""
            _watermarkImagePath = ""
            _shapeIconSearchText = ""
            _selectedLayersPanelTab = LayersPanelTab.Tool
            _isPickingColorFromImage = False
            _pendingColorPickCallback = Nothing
            _activeStrokeAnnotation = Nothing
            _activeStrokeIsEraser = False
            _isEraserMode = False
            _retouchRadius = 24.0
            _brushSize = 24.0
            _brushHardness = 100
            _brushOpacity = 100
            _paintToolStates = NewPaintToolStates()

            _annotationText = "Text"
            _annotationFillColor = "#00FFFFFF"
            _annotationStrokeColor = "#FF000000"
            _annotationStrokeWidth = 0
            _annotationFontSize = 48
            _annotationFontFamily = "Arial"
            _annotationOpacity = 100
            _annotationRotation = 0
            _annotationFlipH = False
            _annotationFlipV = False
            _annotationAnchor = "BottomRight"
            _annotationIsVisible = True
            _annotationXPercent = 35
            _annotationYPercent = 35
            _annotationWidthPercent = 30
            _annotationHeightPercent = 12
            _annotationFillKind = "Solid"
            _annotationFillColor2 = "#FFFFFFFF"
            _annotationGradientAngle = 0
            _annotationGradientInverted = False
            _annotationShadowEnabled = False
            _annotationShadowOffsetX = 2
            _annotationShadowOffsetY = 2
            _annotationShadowLightAngle = ComputeShadowLightAngle(_annotationShadowOffsetX, _annotationShadowOffsetY)
            _annotationShadowBlur = 6
            _annotationShadowStrength = 100
            _annotationShadowColor = "#80000000"
            _annotationShadowRounded = False
            _annotationShadowCornerRadius = 20
            _annotationShadowSize = 100
            _annotationGlowEnabled = False
            _annotationGlowBlur = 10
            _annotationGlowStrength = 100
            _annotationGlowColor = "#FFFFFF00"

            _hasActiveSelection = False
            _selectionXPercent = 0
            _selectionYPercent = 0
            _selectionWidthPercent = 0
            _selectionHeightPercent = 0
            SetSelectedAnnotationOverlay(Nothing)

            RefreshFilteredShapeIcons()
            RaiseEditorUiStateChanged()
        End Sub

        Private Sub RaiseEditorUiStateChanged()
            Me.RaisePropertyChanged(NameOf(CurrentTool))
            Me.RaisePropertyChanged(NameOf(CurrentToolLabel))
            Me.RaisePropertyChanged(NameOf(CurrentToolIconSource))
            RaiseToolContextProperties()
            Me.RaisePropertyChanged(NameOf(PendingInsertKind))
            Me.RaisePropertyChanged(NameOf(HasPendingInsertKind))
            Me.RaisePropertyChanged(NameOf(ShowAnnotationProperties))
            Me.RaisePropertyChanged(NameOf(EffectiveAnnotationKind))
            Me.RaisePropertyChanged(NameOf(ShowWatermarkPresetControls))
            Me.RaisePropertyChanged(NameOf(ShowImageSourceControls))
            Me.RaisePropertyChanged(NameOf(ShowWatermarkAnchorControls))
            Me.RaisePropertyChanged(NameOf(ShowFreeAnnotationPositionControls))
            Me.RaisePropertyChanged(NameOf(AnnotationPositionMinimum))
            Me.RaisePropertyChanged(NameOf(AnnotationPositionMaximum))
            Me.RaisePropertyChanged(NameOf(AnnotationXLabel))
            Me.RaisePropertyChanged(NameOf(AnnotationYLabel))
            Me.RaisePropertyChanged(NameOf(SelectedWatermarkPresetName))
            Me.RaisePropertyChanged(NameOf(WatermarkPresetNameDraft))
            RaiseWatermarkUiChanged()

            Me.RaisePropertyChanged(NameOf(SelectedLayersPanelTab))
            Me.RaisePropertyChanged(NameOf(IsToolTabSelected))
            Me.RaisePropertyChanged(NameOf(IsLayersTabSelected))
            Me.RaisePropertyChanged(NameOf(IsHistoryTabSelected))
            Me.RaisePropertyChanged(NameOf(ShapeIconSearchText))
            UpdateShapeIconStates()

            Me.RaisePropertyChanged(NameOf(RetouchRadius))
            Me.RaisePropertyChanged(NameOf(BrushSize))
            Me.RaisePropertyChanged(NameOf(BrushHardness))
            Me.RaisePropertyChanged(NameOf(BrushOpacity))
            Me.RaisePropertyChanged(NameOf(IsEraserMode))
            Me.RaisePropertyChanged(NameOf(IsBrushPaintMode))
            Me.RaisePropertyChanged(NameOf(ShowBrushStrokeAdjustments))
            Me.RaisePropertyChanged(NameOf(IsEraserPaintMode))
            Me.RaisePropertyChanged(NameOf(IsSmudgePaintMode))
            Me.RaisePropertyChanged(NameOf(SelectedPaintMode))
            Me.RaisePropertyChanged(NameOf(IsPickingColorFromImage))

            Me.RaisePropertyChanged(NameOf(AnnotationText))
            Me.RaisePropertyChanged(NameOf(AnnotationFillColor))
            Me.RaisePropertyChanged(NameOf(AnnotationFillColorValue))
            Me.RaisePropertyChanged(NameOf(AnnotationFillBrush))
            Me.RaisePropertyChanged(NameOf(AnnotationStrokeColor))
            Me.RaisePropertyChanged(NameOf(AnnotationStrokeColorValue))
            Me.RaisePropertyChanged(NameOf(AnnotationStrokeBrush))
            Me.RaisePropertyChanged(NameOf(AnnotationStrokeWidth))
            Me.RaisePropertyChanged(NameOf(AnnotationFontSize))
            Me.RaisePropertyChanged(NameOf(AnnotationFontSizePixels))
            Me.RaisePropertyChanged(NameOf(AnnotationFontFamily))
            Me.RaisePropertyChanged(NameOf(AnnotationOpacity))
            Me.RaisePropertyChanged(NameOf(AnnotationRotation))
            Me.RaisePropertyChanged(NameOf(AnnotationAnchor))
            Me.RaisePropertyChanged(NameOf(AnnotationIsVisible))
            Me.RaisePropertyChanged(NameOf(AnnotationXPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationYPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationXPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationYPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationFillKind))
            Me.RaisePropertyChanged(NameOf(AnnotationFillColor2))
            Me.RaisePropertyChanged(NameOf(AnnotationFillColor2Value))
            Me.RaisePropertyChanged(NameOf(AnnotationFillColor2Brush))
            Me.RaisePropertyChanged(NameOf(AnnotationGradientAngleDegrees))
            Me.RaisePropertyChanged(NameOf(AnnotationGradientInverted))
            Me.RaisePropertyChanged(NameOf(ShowGradientFillControls))
            Me.RaisePropertyChanged(NameOf(ShowLinearGradientAngleControl))
            Me.RaisePropertyChanged(NameOf(ShowRadialGradientControl))
            Me.RaisePropertyChanged(NameOf(SelectionFillPreviewBrush))

            Me.RaisePropertyChanged(NameOf(AnnotationShadowEnabled))
            Me.RaisePropertyChanged(NameOf(AnnotationShadowOffsetX))
            Me.RaisePropertyChanged(NameOf(AnnotationShadowOffsetY))
            Me.RaisePropertyChanged(NameOf(AnnotationShadowLightAngle))
            Me.RaisePropertyChanged(NameOf(AnnotationShadowBlur))
            Me.RaisePropertyChanged(NameOf(AnnotationShadowStrength))
            Me.RaisePropertyChanged(NameOf(AnnotationShadowColor))
            Me.RaisePropertyChanged(NameOf(AnnotationShadowColorValue))
            Me.RaisePropertyChanged(NameOf(AnnotationShadowColorBrush))
            Me.RaisePropertyChanged(NameOf(AnnotationShadowRounded))
            Me.RaisePropertyChanged(NameOf(AnnotationShadowCornerRadius))
            Me.RaisePropertyChanged(NameOf(AnnotationShadowSize))
            Me.RaisePropertyChanged(NameOf(AnnotationGlowEnabled))
            Me.RaisePropertyChanged(NameOf(AnnotationGlowBlur))
            Me.RaisePropertyChanged(NameOf(AnnotationGlowStrength))
            Me.RaisePropertyChanged(NameOf(AnnotationGlowColor))
            Me.RaisePropertyChanged(NameOf(AnnotationGlowColorValue))
            Me.RaisePropertyChanged(NameOf(AnnotationGlowColorBrush))

            Me.RaisePropertyChanged(NameOf(HasActiveSelection))
            Me.RaisePropertyChanged(NameOf(SelectionXPercent))
            Me.RaisePropertyChanged(NameOf(SelectionYPercent))
            Me.RaisePropertyChanged(NameOf(SelectionWidthPercent))
            Me.RaisePropertyChanged(NameOf(SelectionHeightPercent))
            RequestOverlayStateNotify()
        End Sub

        ''' Grenze einer Beschnittkante: alles bis auf ein Pixel darf weg. Früher waren es pauschal 95%,
        ''' was bei Pixeleingabe willkürlich wirkt (bei 6000 px blieben zwangsweise 300 px stehen).
        Private Shared Function MaxCropEdgePercent(basePixels As Integer) As Double
            If basePixels <= 1 Then Return 0
            Return 100.0 - (100.0 / basePixels)
        End Function

        Private Shared Function ClampCropEdge(value As Double, oppositeEdge As Double, maxTotal As Double) As Double
            Dim limit = Math.Max(0, maxTotal)
            Dim opposite = Math.Max(0, Math.Min(limit, oppositeEdge))
            Return Math.Max(0, Math.Min(limit - opposite, value))
        End Function

        Private Sub AfterCropChanged()
            If _lockResizeAspect AndAlso _resizeWidth > 0 Then
                SyncResizeHeightFromWidth()
            End If
            RaiseCropPropertiesChanged()
        End Sub

        Private Sub RaiseCropPropertiesChanged()
            Me.RaisePropertyChanged(NameOf(CropLeftPixels))
            Me.RaisePropertyChanged(NameOf(CropTopPixels))
            Me.RaisePropertyChanged(NameOf(CropRightPixels))
            Me.RaisePropertyChanged(NameOf(CropBottomPixels))
            Me.RaisePropertyChanged(NameOf(CropMaxHorizontalPixels))
            Me.RaisePropertyChanged(NameOf(CropMaxVerticalPixels))
            Me.RaisePropertyChanged(NameOf(CropWidthPixels))
            Me.RaisePropertyChanged(NameOf(CropHeightPixels))
            Me.RaisePropertyChanged(NameOf(OutputSizeText))
        End Sub

        Private Function GetBaseWidth() As Integer
            If CurrentImage Is Nothing Then Return 0
            Return CurrentImage.PixelSize.Width
        End Function

        Private Function GetBaseHeight() As Integer
            If CurrentImage Is Nothing Then Return 0
            Return CurrentImage.PixelSize.Height
        End Function

        Private Function EstimateTextAnnotationSizePercent(text As String, fontSizePixels As Double, fontFamily As String) As (WidthPercent As Double, HeightPercent As Double)
            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return (_annotationWidthPercent, _annotationHeightPercent)

            Dim content = If(text, "").Trim()
            If content.Length = 0 Then content = "Text"

            Dim fontSizePx = Math.Max(8.0, fontSizePixels)
            ' Seit SkiaSharp 3 trägt SKFont die Schrift, SKPaint nur noch Farbe/Kantenglättung.
            ' LinearMetrics=True wie im internen Ersatz-Font von SKPaint, sonst weichen die Textbreiten ab.
            Using font = New SKFont(SKTypeface.FromFamilyName(If(String.IsNullOrWhiteSpace(fontFamily), "Arial", fontFamily)), CSng(fontSizePx)) With {.LinearMetrics = True}
                Using paint = New SKPaint With {.IsAntialias = True}
                ' Die Kästchengröße muss zu dem passen, was DrawWrappedText tatsächlich zeichnet, sonst
                ' steht der Auswahlrahmen sichtbar weiter außen als der Text. Dort gilt: Grundlinie der
                ' ersten Zeile auf rect.Top + fontSize, Zeilenabstand aus den Schriftmetriken, Umbruch
                ' sobald die VORSCHUBBREITE (MeasureText ohne Bounds, nicht die engere Tintenbreite)
                ' rect.Width überschreitet.
                Dim maxLineWidth As Single = 0
                Dim lineCount As Integer = 0
                For Each line In content.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(ControlChars.Lf)
                    lineCount += 1
                    maxLineWidth = Math.Max(maxLineWidth, font.MeasureText(If(String.IsNullOrEmpty(line), " ", line), paint))
                Next
                If lineCount = 0 Then lineCount = 1

                ' Etwas Luft hinter Text und Unterlänge: der Umbruch soll nicht an einem Rundungsfehler
                ' auslösen, und die Anfasser des Auswahlrahmens sollen nicht auf den Glyphen sitzen. Nur
                ' rechts und unten - links und oben zeichnet DrawWrappedText direkt an der Rechteckkante,
                ' dort entsteht die Luft von selbst aus Seitenvorbreite und Oberlänge. Mit dem Schriftgrad
                ' skaliert, damit der Abstand bei jeder Größe gleich aussieht.
                Dim padding = Math.Max(2.0, fontSizePx * 0.08)
                Dim lineHeight = ImageProcessor.GetBakedTextLineHeight(fontFamily, CSng(fontSizePx))
                Dim widthPx = maxLineWidth + padding
                Dim heightPx = fontSizePx + (lineCount - 1) * lineHeight + Math.Max(0.0F, font.Metrics.Descent) + padding
                Dim widthPercent = widthPx / baseWidth * 100.0
                Dim heightPercent = heightPx / baseHeight * 100.0
                ' Bis zur vollen Bildbreite/-höhe: ein Deckel unterhalb davon (früher 60%) hätte das
                ' Rechteck vom Text abgekoppelt, sobald der Text groß wird - und genau dann stimmen
                ' Mitte und rechte Kante des Rahmens nicht mehr mit dem Text überein. Läuft eine Zeile
                ' über die Bildbreite hinaus, greift bei dieser Breite der Umbruch in DrawWrappedText.
                Return (Math.Max(MinTextAnnotationWidthPercent, Math.Min(100.0, widthPercent)),
                        Math.Max(MinTextAnnotationHeightPercent, Math.Min(100.0, heightPercent)))
                End Using
            End Using
        End Function

        Private Sub UpdatePendingTextAnnotationSize()
            If HasSelectedAnnotation Then
                SyncSelectedTextAnnotationSize()
                Return
            End If
            If Not HasPendingInsertKind Then Return
            Dim kind = NormalizeAnnotationKind(_pendingInsertKind)
            If kind <> "Text" AndAlso kind <> "Watermark" Then Return
            ' Ein Wasserzeichen aus einer Bilddatei trägt keinen Text - seine Box darf nicht auf eine
            ' Textbreite springen.
            If IsWatermarkImageSource Then Return

            Dim size = EstimateTextAnnotationSizePercent(_annotationText, _annotationFontSize, _annotationFontFamily)
            _annotationWidthPercent = size.WidthPercent
            _annotationHeightPercent = size.HeightPercent
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
        End Sub

        ''' <summary>Legt die Box eines platzierten Text-/Wasserzeichen-Objekts exakt um seine Glyphen -
        ''' sie folgt dem Text in BEIDE Richtungen. Früher wuchs sie nur (Math.Max gegen den alten Wert)
        ''' und blieb nach kleinerem Schriftgrad oder kürzerem Text zu groß stehen. Dann liegen Mitte und
        ''' rechte Kante des Rahmens neben Mitte und rechter Kante des Textes, und der Rahmen taugt nicht
        ''' mehr zum Ausrichten - genau das ist der Zweck des Rechtecks.
        ''' Während LoadSelectedAnnotationIntoEditor die Felder aus dem Modell befüllt, stehen hier noch
        ''' die Werte des vorher selektierten Objekts - daher kein Aufruf während _isLoadingAnnotation.</summary>
        Private Sub SyncSelectedTextAnnotationSize()
            If _isLoadingAnnotation Then Return
            If Not IsTextualAnnotationKind(SelectedAnnotationKind) Then Return
            If IsWatermarkImageSource Then Return

            Dim size = EstimateTextAnnotationSizePercent(_annotationText, _annotationFontSize, _annotationFontFamily)
            If Math.Abs(size.WidthPercent - _annotationWidthPercent) < 0.0001 AndAlso
               Math.Abs(size.HeightPercent - _annotationHeightPercent) < 0.0001 Then Return

            _annotationWidthPercent = size.WidthPercent
            _annotationHeightPercent = size.HeightPercent
            ' Die Box kann jetzt auch schrumpfen: die Position muss neu in die Bildgrenzen geklemmt
            ' werden, sonst bliebe ein am Rand ausgerichtetes Objekt am alten (größeren) Rand hängen.
            If Not ShowWatermarkAnchorControls Then
                _annotationXPercent = ClampAnnotationPositionPercent(_annotationXPercent, _annotationWidthPercent)
                _annotationYPercent = ClampAnnotationPositionPercent(_annotationYPercent, _annotationHeightPercent)
                Me.RaisePropertyChanged(NameOf(AnnotationXPercent))
                Me.RaisePropertyChanged(NameOf(AnnotationYPercent))
                Me.RaisePropertyChanged(NameOf(AnnotationXPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationYPixels))
            End If
            RaiseAnnotationSizeChanged()
        End Sub

        ''' <summary>Maße des aktuell angezeigten Bildes, also nach dem bereits angewendeten Beschnitt.
        ''' Bezugsgröße für das Auswahlrechteck, die Größenanzeige und die Pixel-Eingabefelder.
        ''' Gerechnet wird kantenweise in ganzen Pixeln - exakt so, wie ImageProcessor.ApplyCrop das
        ''' Original beschneidet. Ein "remaining"-Prozentsatz auf die Gesamtbreite kann davon um ein
        ''' Pixel abweichen, und dann zeigte das Panel eine andere Größe an, als die Datei bekommt.</summary>
        Public ReadOnly Property EffectiveImageWidthPixels As Integer
            Get
                Dim width = GetBaseWidth()
                If width <= 0 Then Return 0
                Return Math.Max(1, width - PercentToPixels(_appliedCropLeft, width) - PercentToPixels(_appliedCropRight, width))
            End Get
        End Property

        Public ReadOnly Property EffectiveImageHeightPixels As Integer
            Get
                Dim height = GetBaseHeight()
                If height <= 0 Then Return 0
                Return Math.Max(1, height - PercentToPixels(_appliedCropTop, height) - PercentToPixels(_appliedCropBottom, height))
            End Get
        End Property

        ''' Maße nach dem angewendeten UND dem noch offenen Beschnitt - das, was ein "Anwenden" ergäbe.
        ''' Der offene Beschnitt liegt im selben Pixelraster wie das angezeigte Bild (zwischen angewendetem
        ''' und offenem Beschnitt wird nicht neu abgetastet), also ist das reine Ganzzahl-Arithmetik.
        Private Function GetCroppedWidth() As Integer
            Dim width = EffectiveImageWidthPixels
            If width <= 0 Then Return 0
            Return Math.Max(1, width - CropLeftPixels - CropRightPixels)
        End Function

        Private Function GetCroppedHeight() As Integer
            Dim height = EffectiveImageHeightPixels
            If height <= 0 Then Return 0
            Return Math.Max(1, height - CropTopPixels - CropBottomPixels)
        End Function

        ''' Pixel <-> Prozent: die Prozentwerte sind nur das auflösungsunabhängige Transportformat (die
        ''' Vorschau ist kleiner als das Original, Presets gelten für jede Bildgröße). Gerundet wird
        ''' überall mit derselben Regel, damit der Umweg über Prozent den eingegebenen Pixel zurückgibt.
        Private Shared Function PercentToPixels(percent As Double, basePixels As Integer) As Integer
            If basePixels <= 0 Then Return 0
            Return Math.Max(0, Math.Min(basePixels, CInt(Math.Round(basePixels * percent / 100.0))))
        End Function

        Private Shared Function PixelsToPercent(pixels As Integer, basePixels As Integer) As Double
            If basePixels <= 0 Then Return 0
            Return Math.Max(0, Math.Min(basePixels, pixels)) / CDbl(basePixels) * 100.0
        End Function

        Private Sub SetCropValues(left As Double, top As Double, right As Double, bottom As Double)
            Dim maxHorizontal = MaxCropEdgePercent(EffectiveImageWidthPixels)
            Dim maxVertical = MaxCropEdgePercent(EffectiveImageHeightPixels)
            _cropLeft = ClampCropEdge(left, 0, maxHorizontal)
            _cropRight = ClampCropEdge(right, _cropLeft, maxHorizontal)
            _cropTop = ClampCropEdge(top, 0, maxVertical)
            _cropBottom = ClampCropEdge(bottom, _cropTop, maxVertical)
            Me.RaisePropertyChanged(NameOf(CropLeft))
            Me.RaisePropertyChanged(NameOf(CropTop))
            Me.RaisePropertyChanged(NameOf(CropRight))
            Me.RaisePropertyChanged(NameOf(CropBottom))
            AfterCropChanged()
            RaiseResetButtonStateChanged()
        End Sub

        ''' Die Pixel-Eingabefelder des Zuschneiden-Panels beziehen sich auf das angezeigte Bild, nicht
        ''' auf das Original - sonst könnte man nach einem Beschnitt eine Breite eintippen, die größer
        ''' als das sichtbare Bild ist.
        Private Sub SetCropSizePixels(widthPixels As Integer, heightPixels As Integer)
            Dim baseWidth = EffectiveImageWidthPixels
            Dim baseHeight = EffectiveImageHeightPixels
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return

            Dim leftPx = CropLeftPixels
            Dim topPx = CropTopPixels
            Dim clampedWidth = Math.Max(1, Math.Min(Math.Max(1, widthPixels), Math.Max(1, baseWidth - leftPx)))
            Dim clampedHeight = Math.Max(1, Math.Min(Math.Max(1, heightPixels), Math.Max(1, baseHeight - topPx)))
            Dim rightPx = Math.Max(0, baseWidth - leftPx - clampedWidth)
            Dim bottomPx = Math.Max(0, baseHeight - topPx - clampedHeight)

            SetCropValues(PixelsToPercent(leftPx, baseWidth),
                          PixelsToPercent(topPx, baseHeight),
                          PixelsToPercent(rightPx, baseWidth),
                          PixelsToPercent(bottomPx, baseHeight))
        End Sub

        Private Sub ApplyCropPreset(preset As String)
            If EffectiveImageWidthPixels <= 0 OrElse EffectiveImageHeightPixels <= 0 Then Return

            Select Case If(preset, "").Trim()
                Case "Original", "Frei"
                    SetCropValues(0, 0, 0, 0)
                Case "1:1"
                    ApplyCenteredAspectCrop(1.0)
                Case "4:3"
                    ApplyCenteredAspectCrop(4.0 / 3.0)
                Case "3:2"
                    ApplyCenteredAspectCrop(3.0 / 2.0)
                Case "16:9"
                    ApplyCenteredAspectCrop(16.0 / 9.0)
                Case "9:16"
                    ApplyCenteredAspectCrop(9.0 / 16.0)
            End Select
        End Sub

        ''' Zielformat mittig aus dem ANGEZEIGTEN Bild schneiden (nicht aus dem Original - nach einem
        ''' ersten Beschnitt hat das Original ein anderes Seitenverhältnis). Der Rest wird in ganzen
        ''' Pixeln aufgeteilt; bei ungerader Differenz bekommt die rechte/untere Kante das Pixel mehr.
        Private Sub ApplyCenteredAspectCrop(targetAspect As Double)
            Dim width = EffectiveImageWidthPixels
            Dim height = EffectiveImageHeightPixels
            If width <= 0 OrElse height <= 0 OrElse targetAspect <= 0 Then Return

            Dim targetWidth = width
            Dim targetHeight = height
            If width / CDbl(height) > targetAspect Then
                targetWidth = Math.Max(1, Math.Min(width, CInt(Math.Round(height * targetAspect))))
            Else
                targetHeight = Math.Max(1, Math.Min(height, CInt(Math.Round(width / targetAspect))))
            End If

            Dim leftPx = (width - targetWidth) \ 2
            Dim rightPx = width - targetWidth - leftPx
            Dim topPx = (height - targetHeight) \ 2
            Dim bottomPx = height - targetHeight - topPx

            SetCropValues(PixelsToPercent(leftPx, width),
                          PixelsToPercent(topPx, height),
                          PixelsToPercent(rightPx, width),
                          PixelsToPercent(bottomPx, height))
        End Sub

        Private Sub SetResizeValues(width As Integer, height As Integer)
            _isUpdatingResize = True
            _resizeWidth = Math.Max(0, width)
            _resizeHeight = Math.Max(0, height)
            _isUpdatingResize = False
            Me.RaisePropertyChanged(NameOf(ResizeWidth))
            Me.RaisePropertyChanged(NameOf(ResizeHeight))
            Me.RaisePropertyChanged(NameOf(OutputSizeText))
            RaiseResetButtonStateChanged()
            ScheduleToolPreviewUpdate()
        End Sub

        Private Sub SetCanvasValues(width As Integer, height As Integer, anchor As String)
            _canvasWidth = Math.Max(0, width)
            _canvasHeight = Math.Max(0, height)
            _canvasAnchor = If(String.IsNullOrWhiteSpace(anchor), "Center", anchor)
            Me.RaisePropertyChanged(NameOf(CanvasWidth))
            Me.RaisePropertyChanged(NameOf(CanvasHeight))
            Me.RaisePropertyChanged(NameOf(CanvasAnchor))
            Me.RaisePropertyChanged(NameOf(OutputSizeText))
            RaiseResetButtonStateChanged()
            ScheduleToolPreviewUpdate()
        End Sub

        ''' Platziert ein Objekt an der zuletzt eingestellten Position - für Wege ohne Klick auf die
        ''' Leinwand (siehe PlacePendingWatermarkAfterPresetSelection).
        Private Sub AddAnnotation(kind As String)
            AddAnnotationAt(kind, _annotationXPercent, _annotationYPercent)
        End Sub

        Public Sub AddAnnotationAt(kind As String, xPercent As Double, yPercent As Double)
            PushUndo()
            Dim normalizedKind = NormalizeAnnotationKind(kind)
            Dim defaultSize = GetDefaultAnnotationSizePercent(normalizedKind, kind)
            Dim width = defaultSize.WidthPercent
            Dim height = defaultSize.HeightPercent
            If normalizedKind = "QR" Then
                Dim baseWidth = GetBaseWidth()
                Dim baseHeight = GetBaseHeight()
                If baseWidth > 0 AndAlso baseHeight > 0 Then
                    Dim sizePixels = Math.Max(1.0, Math.Min(PercentXToPixels(width), PercentYToPixels(height)))
                    width = sizePixels / baseWidth * 100.0
                    height = sizePixels / baseHeight * 100.0
                End If
            End If
            If normalizedKind = "Watermark" Then
                width = _annotationWidthPercent
                height = _annotationHeightPercent
            End If
            Dim x = If(normalizedKind = "Watermark",
                       ClampAnnotationOffsetPercent(_annotationXPercent),
                       Math.Max(-width + 1, Math.Min(100 - 1, xPercent)))
            Dim y = If(normalizedKind = "Watermark",
                       ClampAnnotationOffsetPercent(_annotationYPercent),
                       Math.Max(-height + 1, Math.Min(100 - 1, yPercent)))
            Dim storedX = PercentXToPixels(x)
            Dim storedY = PercentYToPixels(y)
            Dim storedWidth = Math.Max(1.0, PercentXToPixels(width))
            Dim storedHeight = Math.Max(1.0, PercentYToPixels(height))
            Dim text = If(normalizedKind = "Image" OrElse normalizedKind = "Brush" OrElse normalizedKind = "Eraser",
                          "",
                          If(String.IsNullOrWhiteSpace(_annotationText), GetDefaultAnnotationText(normalizedKind, kind), _annotationText))
            ' Füllung/Kontur/Konturbreite wurden bereits beim Scharfstellen (SeedAnnotationDefaultsForKind)
            ' mit passenden Startwerten belegt und können im Eigenschaften-Panel vor dem Platzieren
            ' angepasst worden sein - diese aktuellen Werte werden 1:1 übernommen.
            Dim fill = _annotationFillColor
            Dim stroke = _annotationStrokeColor
            Dim strokeWidth = _annotationStrokeWidth
            Dim annotation = New ImageAnnotation With {
                .Kind = normalizedKind,
                .Text = text,
                .ImagePath = If(normalizedKind = "Svg", ExtractSvgIconPath(kind), If(normalizedKind = "Watermark", _watermarkImagePath, "")),
                .XPixels = CSng(storedX),
                .YPixels = CSng(storedY),
                .WidthPixels = CSng(storedWidth),
                .HeightPixels = CSng(storedHeight),
                .FillColor = fill,
                .StrokeColor = stroke,
                .StrokeWidth = CSng(strokeWidth),
                .FontSizePixels = CSng(If(normalizedKind = "Watermark", Math.Max(8, _annotationFontSize), _annotationFontSize)),
                .FontFamily = _annotationFontFamily,
                .Opacity = CSng(_annotationOpacity),
                .RotationDegrees = CSng(_annotationRotation),
                .Anchor = If(normalizedKind = "Watermark", NormalizeAnnotationAnchor(_annotationAnchor), ""),
                .IsVisible = _annotationIsVisible,
                .FillKind = _annotationFillKind,
                .FillColor2 = _annotationFillColor2,
                .GradientAngleDegrees = CSng(_annotationGradientAngle),
                .GradientInverted = _annotationGradientInverted
            }
            _annotations.Add(annotation)
            SelectedAnnotationIndex = _annotations.Count - 1
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Function GetDefaultAnnotationSizePercent(normalizedKind As String, rawKind As String) As (WidthPercent As Double, HeightPercent As Double)
            Select Case normalizedKind
                Case "Line", "Arrow"
                    Return (30.0, 16.0)
                Case "QR", "Image", "Symbol", "Rectangle", "Ellipse", "Square", "Triangle", "Cone", "Pyramid", "Trapezoid", "Diamond", "Spiral", "Droplet", "SpeechBubble"
                    Return (22.0, 22.0)
                Case "Svg"
                    Dim aspect = ImageProcessor.TryGetSvgAspectRatio(ExtractSvgIconPath(rawKind))
                    Dim baseSize = 22.0
                    If aspect >= 1.0 Then
                        Return (baseSize, Math.Max(5.0, baseSize / aspect))
                    End If
                    Return (Math.Max(5.0, baseSize * aspect), baseSize)
                Case Else
                    Return (_annotationWidthPercent, _annotationHeightPercent)
            End Select
        End Function

        ''' Für den Weg über den Knopf im Eigenschaften-Panel: platziert das Bild an der zuletzt
        ''' eingestellten Position, ohne Klick in die Leinwand (siehe PlacePendingWatermark).
        Public Sub AddImageAnnotationAtCurrentPosition(imagePath As String)
            AddImageAnnotationAt(imagePath, _annotationXPercent, _annotationYPercent)
        End Sub

        Public Sub AddImageAnnotationAt(imagePath As String, xPercent As Double, yPercent As Double)
            If String.IsNullOrWhiteSpace(imagePath) Then Return
            PushUndo()
            Dim size = GetInitialImageAnnotationSize(imagePath)
            Dim width = size.WidthPercent
            Dim height = size.HeightPercent
            Dim x = Math.Max(-width + 1, Math.Min(100 - 1, xPercent))
            Dim y = Math.Max(-height + 1, Math.Min(100 - 1, yPercent))
            Dim annotation = New ImageAnnotation With {
                .Kind = "Image",
                .Text = "",
                .ImagePath = imagePath,
                .XPixels = CSng(PercentXToPixels(x)),
                .YPixels = CSng(PercentYToPixels(y)),
                .WidthPixels = CSng(Math.Max(1.0, PercentXToPixels(width))),
                .HeightPixels = CSng(Math.Max(1.0, PercentYToPixels(height))),
                .FillColor = "#00FFFFFF",
                .StrokeColor = _annotationStrokeColor,
                .StrokeWidth = CSng(_annotationStrokeWidth),
                .FontSizePixels = CSng(_annotationFontSize),
                .FontFamily = _annotationFontFamily,
                .Opacity = CSng(_annotationOpacity),
                .RotationDegrees = CSng(_annotationRotation),
                .IsVisible = _annotationIsVisible
            }
            _annotations.Add(annotation)
            SelectedAnnotationIndex = _annotations.Count - 1
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Function GetInitialImageAnnotationSize(imagePath As String) As (WidthPercent As Double, HeightPercent As Double)
            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return (30.0, 30.0)

            Try
                Using bitmap = SKBitmap.Decode(imagePath)
                    If bitmap Is Nothing OrElse bitmap.Width <= 0 OrElse bitmap.Height <= 0 Then Return (30.0, 30.0)

                    Dim maxWidth = baseWidth * 0.6
                    Dim maxHeight = baseHeight * 0.6
                    Dim scale = Math.Min(1.0, Math.Min(maxWidth / bitmap.Width, maxHeight / bitmap.Height))
                    Dim widthPercent = bitmap.Width * scale / baseWidth * 100.0
                    Dim heightPercent = bitmap.Height * scale / baseHeight * 100.0

                    If widthPercent < 5.0 Then
                        Dim factor = 5.0 / Math.Max(0.0001, widthPercent)
                        widthPercent *= factor
                        heightPercent *= factor
                    End If
                    If heightPercent < 4.0 Then
                        Dim factor = 4.0 / Math.Max(0.0001, heightPercent)
                        widthPercent *= factor
                        heightPercent *= factor
                    End If

                    Dim clampFactor = Math.Min(1.0, Math.Min(90.0 / Math.Max(0.0001, widthPercent), 90.0 / Math.Max(0.0001, heightPercent)))
                    Return (Math.Max(5.0, Math.Min(90.0, widthPercent * clampFactor)),
                            Math.Max(4.0, Math.Min(90.0, heightPercent * clampFactor)))
                End Using
            Catch
                Return (30.0, 30.0)
            End Try
        End Function

        Private Shared Function AnnotationKindToTool(kind As String) As EditorTool
            Select Case NormalizeAnnotationKind(kind)
                Case "Text", "Image", "QR" : Return EditorTool.Text
                Case "Watermark" : Return EditorTool.Text
                Case "Rectangle", "Ellipse", "Square", "Triangle", "Cone", "Pyramid", "Trapezoid", "Diamond", "Spiral", "Droplet", "SpeechBubble", "Line", "Arrow", "Symbol", "Svg"
                    Return EditorTool.Geometry
                Case "Brush", "Eraser" : Return EditorTool.Draw
                Case "SelectionFill", "SelectionImage" : Return EditorTool.Selection
                Case Else : Return EditorTool.Insert
            End Select
        End Function

        Private Shared Function PlacementKindForAnnotation(annotation As ImageAnnotation) As String
            If annotation Is Nothing Then Return ""

            Dim normalized = NormalizeAnnotationKind(annotation.Kind)
            Select Case normalized
                Case "Text", "Image", "QR", "Watermark", "Rectangle", "Ellipse", "Square", "Triangle", "Cone", "Pyramid", "Trapezoid", "Diamond", "Spiral", "Droplet", "SpeechBubble", "Line", "Arrow", "Symbol"
                    Return normalized
                Case "Svg"
                    If Not String.IsNullOrWhiteSpace(annotation.ImagePath) Then Return "Svg:" & annotation.ImagePath
                    Return "Svg"
                Case Else
                    Return ""
            End Select
        End Function

        Private Shared Function NormalizeAnnotationKind(kind As String) As String
            Dim normalized = If(kind, "").Trim().ToLowerInvariant()
            If normalized.StartsWith("symbol:", StringComparison.OrdinalIgnoreCase) Then Return "Symbol"
            If normalized.StartsWith("svg:", StringComparison.OrdinalIgnoreCase) Then Return "Svg"
            Select Case normalized
                Case "svg" : Return "Svg"
                Case "selectionfill" : Return "SelectionFill"
                Case "selectionimage" : Return "SelectionImage"
                Case "rectangle", "rect", "rechteck" : Return "Rectangle"
                Case "ellipse", "circle", "kreis" : Return "Ellipse"
                Case "square", "quadrat" : Return "Square"
                Case "triangle", "dreieck" : Return "Triangle"
                Case "cone", "kegel" : Return "Cone"
                Case "pyramid", "pyramide" : Return "Pyramid"
                Case "trapezoid", "trapez" : Return "Trapezoid"
                Case "diamond", "raute" : Return "Diamond"
                Case "spiral", "spirale" : Return "Spiral"
                Case "droplet", "tropfen" : Return "Droplet"
                Case "speechbubble", "speech-bubble", "sprechblase", "bubble" : Return "SpeechBubble"
                Case "line", "linie" : Return "Line"
                Case "arrow", "pfeil" : Return "Arrow"
                Case "brush", "pinsel", "draw", "malen" : Return "Brush"
                Case "eraser", "radiergummi" : Return "Eraser"
                Case "symbol", "symbol-star", "symbol-heart", "symbol-check" : Return "Symbol"
                Case "qr", "qrcode", "qr-code" : Return "QR"
                Case "image", "bild" : Return "Image"
                Case "watermark", "wasserzeichen" : Return "Watermark"
                Case Else : Return "Text"
            End Select
        End Function

        Private Function GetDefaultAnnotationText(kind As String, rawKind As String) As String
            Select Case kind
                Case "Symbol"
                    Dim raw = If(rawKind, "")
                    If raw.StartsWith("Symbol:", StringComparison.OrdinalIgnoreCase) AndAlso raw.Length > 7 Then Return raw.Substring(7)
                    Return "★"
                Case "QR"
                    Return "FerrumPix"
                Case "Image"
                    Return "Bild"
                Case "Watermark"
                    Return "FerrumPix"
                Case "Text"
                    Return "Text"
                Case Else
                    Return ""
            End Select
        End Function

        Private Shared Function IsCustomShapeKind(kind As String) As Boolean
            Select Case kind
                Case "Square", "Triangle", "Cone", "Pyramid", "Trapezoid", "Diamond", "Spiral", "Droplet", "SpeechBubble", "Svg"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        ''' Extrahiert den Icon-Pfad aus einem "Svg:avares://..."-Kind-String (siehe AddAnnotationAt).
        Private Shared Function ExtractSvgIconPath(rawKind As String) As String
            Dim raw = If(rawKind, "")
            If raw.StartsWith("Svg:", StringComparison.OrdinalIgnoreCase) AndAlso raw.Length > 4 Then Return raw.Substring(4)
            Return ""
        End Function

        ''' Hängt einen neuen Pinsel-/Radiergummi-Strich an die Ebene der laufenden Mal-"Sitzung" an
        ''' (siehe _activeStrokeAnnotation), statt wie zuvor für jeden einzelnen Strich eine eigene
        ''' Ebene anzulegen - eine Sitzung endet erst bei Werkzeugwechsel, Pinsel/Radiergummi-
        ''' Umschalten oder Ebenenwechsel. Mehrere Striche werden im .Text-Punktestring per ";"
        ''' getrennt (siehe ImageProcessor.DrawBrushStroke), damit sie beim Rendern als eigenständige
        ''' Teilpfade gezeichnet werden - eine einfache Verkettung würde sie sonst durch eine gerade
        ''' Linie verbinden.
        Public Sub AddBrushStroke(points As IEnumerable(Of Avalonia.Point), Optional isEraser As Boolean = False)
            If points Is Nothing Then Return
            Dim normalized = points.ToList()
            If normalized.Count < 2 Then Return

            PushUndo()
            Dim pixelPoints = normalized.Select(Function(p) New Avalonia.Point(PercentXToPixels(p.X), PercentYToPixels(p.Y))).ToList()
            Dim minX = Math.Max(0, pixelPoints.Min(Function(p) p.X))
            Dim minY = Math.Max(0, pixelPoints.Min(Function(p) p.Y))
            Dim maxX = Math.Min(GetBaseWidth(), pixelPoints.Max(Function(p) p.X))
            Dim maxY = Math.Min(GetBaseHeight(), pixelPoints.Max(Function(p) p.Y))
            Dim newStroke = New BrushStroke(pixelPoints.Select(Function(p) New StrokePoint(CSng(p.X), CSng(p.Y))))

            Dim expectedKind = If(isEraser, "Eraser", "Brush")
            Dim canAppend = _activeStrokeAnnotation IsNot Nothing AndAlso
                             _activeStrokeIsEraser = isEraser AndAlso
                             String.Equals(_activeStrokeAnnotation.Kind, expectedKind, StringComparison.OrdinalIgnoreCase) AndAlso
                             _annotations.Contains(_activeStrokeAnnotation)

            If canAppend Then
                Dim existing = _activeStrokeAnnotation
                Dim unionMinX = Math.Min(existing.XPixels, CSng(minX))
                Dim unionMinY = Math.Min(existing.YPixels, CSng(minY))
                Dim unionMaxX = Math.Max(existing.XPixels + existing.WidthPixels, CSng(maxX))
                Dim unionMaxY = Math.Max(existing.YPixels + existing.HeightPixels, CSng(maxY))
                existing.Strokes.Add(newStroke)
                existing.XPixels = unionMinX
                existing.YPixels = unionMinY
                existing.WidthPixels = Math.Max(1, unionMaxX - unionMinX)
                existing.HeightPixels = Math.Max(1, unionMaxY - unionMinY)
            Else
                Dim width = Math.Max(1, maxX - minX)
                Dim height = Math.Max(1, maxY - minY)
                Dim newAnnotation = New ImageAnnotation With {
                    .Kind = expectedKind,
                    .Text = "",
                    .Strokes = New List(Of BrushStroke) From {newStroke},
                    .XPixels = CSng(minX),
                    .YPixels = CSng(minY),
                    .WidthPixels = CSng(width),
                    .HeightPixels = CSng(height),
                    .FillColor = _annotationFillColor,
                    .StrokeColor = _annotationStrokeColor,
                    .StrokeWidth = CSng(_brushSize),
                    .Opacity = CSng(_brushOpacity),
                    .HardnessPercent = CSng(_brushHardness),
                    .FontSizePixels = CSng(_annotationFontSize),
                    .FontFamily = _annotationFontFamily
                }
                _annotations.Add(newAnnotation)
                SelectedAnnotationIndex = _annotations.Count - 1
                _activeStrokeAnnotation = newAnnotation
                _activeStrokeIsEraser = isEraser
            End If

            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub DeleteSelectedAnnotation()
            DeleteAnnotationAt(_selectedAnnotationIndex)
        End Sub

        Private Sub DeleteAnnotation(annotation As ImageAnnotation)
            If annotation Is Nothing Then Return
            DeleteAnnotationAt(_annotations.IndexOf(annotation))
        End Sub

        Private Sub ToggleAnnotationVisibility(annotation As ImageAnnotation)
            If annotation Is Nothing Then Return
            CaptureUndoState("LayerVisibility")
            annotation.IsVisible = Not annotation.IsVisible
            If _selectedAnnotationIndex >= 0 AndAlso _annotations(_selectedAnnotationIndex) Is annotation Then
                AnnotationIsVisible = annotation.IsVisible
            End If
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub DeleteAnnotationAt(index As Integer)
            If index < 0 OrElse index >= _annotations.Count Then Return
            PushUndo()
            _annotations.RemoveAt(index)
            If _selectedAnnotationIndex = index Then
                SelectedAnnotationIndex = -1
            ElseIf _selectedAnnotationIndex > index Then
                _selectedAnnotationIndex -= 1
                Me.RaisePropertyChanged(NameOf(SelectedAnnotationIndex))
            End If
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub DuplicateSelectedAnnotation()
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return
            PushUndo()
            Dim copy = _annotations(_selectedAnnotationIndex).Clone()
            copy.XPixels += 24
            copy.YPixels += 24
            _annotations.Insert(_selectedAnnotationIndex + 1, copy)
            SelectedAnnotationIndex = _selectedAnnotationIndex + 1
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub MoveSelectedAnnotation(direction As Integer)
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return
            Dim target = _selectedAnnotationIndex + If(direction >= 0, 1, -1)
            If target < 0 OrElse target >= _annotations.Count Then Return
            PushUndo()
            Dim item = _annotations(_selectedAnnotationIndex)
            _annotations.RemoveAt(_selectedAnnotationIndex)
            _annotations.Insert(target, item)
            SelectedAnnotationIndex = target
            SchedulePreviewUpdate()
        End Sub

        Public Sub NudgeSelectedAnnotation(dx As Double, dy As Double)
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return
            CaptureUndoState("LayerNudge")
            AnnotationXPercent = ClampAnnotationPositionPercent(_annotationXPercent + dx, _annotationWidthPercent)
            AnnotationYPercent = ClampAnnotationPositionPercent(_annotationYPercent + dy, _annotationHeightPercent)
        End Sub

        Public Function HitTestAnnotation(xPercent As Double, yPercent As Double,
                                           Optional hitSlopXPercent As Double = 1.5,
                                           Optional hitSlopYPercent As Double = 1.5) As Integer
            For i = _annotations.Count - 1 To 0 Step -1
                Dim a = _annotations(i)
                ' Pinsel-/Radiergummi-Ebenen sind keine anfassbaren Objekte, sondern Sammelbehälter für
                ' Striche: ihr Rechteck ist die Hülle aller Züge und überdeckt oft halbe Bilder. Ein Klick
                ' darin würde sie selektieren und damit (SelectedAnnotationIndex-Setter) zurück ins
                ' Pinsel-Werkzeug springen - auch wenn der Klick eigentlich einen Beschnitt aufziehen oder
                ' eine Form setzen sollte. Auswählbar bleiben sie über die Ebenenliste.
                If IsStrokeAnnotationKind(a.Kind) Then Continue For
                ' Ausgeblendete Ebenen sind auf der Leinwand nicht zu sehen; ein Klick auf ihre alte
                ' Stelle darf sie nicht selektieren (und damit ins zugehörige Werkzeug springen).
                If Not a.IsVisible Then Continue For
                Dim ax = AnnotationStoredXToPercent(a)
                Dim ay = AnnotationStoredYToPercent(a)
                Dim aw = AnnotationStoredWidthToPercent(a)
                Dim ah = AnnotationStoredHeightToPercent(a)
                Dim origin = ComputeAnnotationOriginPercent(a.Kind, ax, ay, aw, ah, a.Anchor)
                If xPercent >= origin.X - hitSlopXPercent AndAlso xPercent <= origin.X + aw + hitSlopXPercent AndAlso
                   yPercent >= origin.Y - hitSlopYPercent AndAlso yPercent <= origin.Y + ah + hitSlopYPercent Then
                    Return i
                End If
            Next
            Return -1
        End Function

        ''' Ebenen, deren Inhalt in <see cref="ImageAnnotation.Strokes"/> steckt statt in Geometrie/Text.
        Private Shared Function IsStrokeAnnotationKind(kind As String) As Boolean
            Dim normalized = NormalizeAnnotationKind(kind)
            Return normalized = "Brush" OrElse normalized = "Eraser"
        End Function

        ''' <summary>Werkzeuge, in denen ein markiertes Objekt markiert BLEIBT. Das Drehen-Werkzeug gehört
        ''' dazu, seit seine vier Knöpfe (90° links/rechts, Spiegeln) auf das markierte Objekt wirken - würde
        ''' der Wechsel dorthin die Markierung aufheben, gäbe es nie ein Objekt zu drehen.</summary>
        Private Shared Function IsLayerTool(tool As EditorTool) As Boolean
            Return tool = EditorTool.Text OrElse tool = EditorTool.Draw OrElse tool = EditorTool.Geometry OrElse
                   tool = EditorTool.Insert OrElse tool = EditorTool.Selection OrElse
                   IsObjectTransformTool(tool)
        End Function

        ''' <summary>Das Drehen-Werkzeug: hier wirken Drehen/Spiegeln auf das markierte Objekt. Es darf weder
        ''' die Markierung verlieren (Werkzeugwechsel) noch beim Anklicken eines Objekts in dessen Werkzeug
        ''' springen - sonst könnte man ein Objekt hier gar nicht auswählen.</summary>
        Public Shared Function IsObjectTransformTool(tool As EditorTool) As Boolean
            Return tool = EditorTool.Rotate OrElse tool = EditorTool.Transform
        End Function

        Private Sub LoadSelectedAnnotationIntoEditor()
            _isLoadingAnnotation = True
            Try
                If _selectedAnnotationIndex >= 0 AndAlso _selectedAnnotationIndex < _annotations.Count Then
                    Dim a = _annotations(_selectedAnnotationIndex)
                    _watermarkImagePath = If(NormalizeAnnotationKind(a.Kind) = "Watermark", a.ImagePath, "")
                    ' Der Vorlagenname beschreibt das zuvor selektierte Objekt und passt nicht mehr zu
                    ' den gleich geladenen Werten.
                    _selectedWatermarkPresetName = ""
                    _watermarkPresetNameDraft = ""
                    Me.RaisePropertyChanged(NameOf(SelectedWatermarkPresetName))
                    Me.RaisePropertyChanged(NameOf(WatermarkPresetNameDraft))
                    AnnotationText = a.Text
                    AnnotationFillColor = a.FillColor
                    AnnotationStrokeColor = a.StrokeColor
                    AnnotationStrokeWidth = a.StrokeWidth
                    AnnotationFontSize = a.FontSizePixels
                    AnnotationFontFamily = a.FontFamily
                    AnnotationOpacity = a.Opacity
                    AnnotationRotation = a.RotationDegrees
                    AnnotationFlipHorizontal = a.FlipHorizontal
                    AnnotationFlipVertical = a.FlipVertical
                    _annotationAnchor = NormalizeAnnotationAnchor(a.Anchor)
                    AnnotationIsVisible = a.IsVisible
                    AnnotationXPercent = AnnotationStoredXToPercent(a)
                    AnnotationYPercent = AnnotationStoredYToPercent(a)
                    AnnotationWidthPercent = AnnotationStoredWidthToPercent(a)
                    AnnotationHeightPercent = AnnotationStoredHeightToPercent(a)
                    AnnotationFillKind = a.FillKind
                    AnnotationFillColor2 = a.FillColor2
                    AnnotationGradientAngleDegrees = a.GradientAngleDegrees
                    AnnotationGradientInverted = a.GradientInverted
                    AnnotationShadowEnabled = a.ShadowEnabled
                    AnnotationShadowOffsetX = a.ShadowOffsetXPercent
                    AnnotationShadowOffsetY = a.ShadowOffsetYPercent
                    Me.RaisePropertyChanged(NameOf(AnnotationShadowLightAngle))
                    AnnotationShadowBlur = a.ShadowBlur
                    AnnotationShadowStrength = a.ShadowStrength
                    AnnotationShadowColor = a.ShadowColor
                    AnnotationShadowRounded = a.ShadowRounded
                    AnnotationShadowCornerRadius = a.ShadowCornerRadiusPercent
                    AnnotationShadowSize = a.ShadowSizePercent
                    AnnotationGlowEnabled = a.GlowEnabled
                    AnnotationGlowBlur = a.GlowBlur
                    AnnotationGlowStrength = a.GlowStrength
                    AnnotationGlowColor = a.GlowColor
                    Me.RaisePropertyChanged(NameOf(AnnotationAnchor))
                    RaiseWatermarkUiChanged()
                End If
            Finally
                _isLoadingAnnotation = False
            End Try
        End Sub

        ''' <param name="refreshOverlay">False, wenn sich nur Position oder Drehung geändert haben.
        ''' RenderAnnotationOverlay liest weder X/Y noch RotationDegrees (es nullt die Drehung sogar
        ''' explizit) - das Bitmap bliebe also identisch. Der Render läuft synchron auf dem UI-Thread und
        ''' zeichnet Schatten und Glow inklusive Blur neu, deshalb lohnt es sich, ihn beim Ziehen (ein
        ''' Aufruf je Mausbewegung) auszulassen. Die Positionierung übernimmt ohnehin die View über die
        ''' Margins.</param>
        Private Sub SyncSelectedAnnotation(Optional refreshOverlay As Boolean = True)
            If _isLoadingAnnotation Then Return
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return
            CaptureUndoState("TextAnnotation")
            Dim a = _annotations(_selectedAnnotationIndex)
            ' Pinsel- und Radiergummi-Ebenen haben keinen Text: ihre Züge liegen in a.Strokes. Das
            ' Text-Feld bleibt bei ihnen leer, damit nicht der Textpuffer des Editors hineinläuft.
            Dim normalizedKind = NormalizeAnnotationKind(a.Kind)
            If normalizedKind <> "Brush" AndAlso normalizedKind <> "Eraser" Then
                a.Text = _annotationText
            End If
            If normalizedKind = "Watermark" Then
                a.ImagePath = _watermarkImagePath
            End If
            a.FillColor = _annotationFillColor
            a.StrokeColor = _annotationStrokeColor
            a.StrokeWidth = CSng(_annotationStrokeWidth)
            a.FontSizePixels = CSng(_annotationFontSize)
            a.FontFamily = _annotationFontFamily
            a.Opacity = CSng(_annotationOpacity)
            a.RotationDegrees = CSng(_annotationRotation)
            a.FlipHorizontal = _annotationFlipH
            a.FlipVertical = _annotationFlipV
            a.Anchor = If(normalizedKind = "Watermark", NormalizeAnnotationAnchor(_annotationAnchor), "")
            a.IsVisible = _annotationIsVisible
            a.XPixels = CSng(AnnotationXPixels)
            a.YPixels = CSng(AnnotationYPixels)
            a.WidthPixels = CSng(Math.Max(1, AnnotationWidthPixels))
            a.HeightPixels = CSng(Math.Max(1, AnnotationHeightPixels))
            a.FillKind = _annotationFillKind
            a.FillColor2 = _annotationFillColor2
            a.GradientAngleDegrees = CSng(_annotationGradientAngle)
            a.GradientInverted = _annotationGradientInverted
            a.ShadowEnabled = _annotationShadowEnabled
            a.ShadowOffsetXPercent = CSng(_annotationShadowOffsetX)
            a.ShadowOffsetYPercent = CSng(_annotationShadowOffsetY)
            a.ShadowBlur = CSng(_annotationShadowBlur)
            a.ShadowStrength = CSng(_annotationShadowStrength)
            a.ShadowColor = _annotationShadowColor
            a.ShadowRounded = _annotationShadowRounded
            a.ShadowCornerRadiusPercent = CSng(_annotationShadowCornerRadius)
            a.ShadowSizePercent = CSng(_annotationShadowSize)
            a.GlowEnabled = _annotationGlowEnabled
            a.GlowBlur = CSng(_annotationGlowBlur)
            a.GlowStrength = CSng(_annotationGlowStrength)
            a.GlowColor = _annotationGlowColor
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationText))
            If refreshOverlay Then UpdateSelectedAnnotationOverlayPreview()
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        ''' Bild und Objekt-Rechteck gehören zusammen (die View rechnet das Rechteck in die negativen
        ''' Overlay-Margins um) und müssen deshalb immer gemeinsam gesetzt werden.
        Private Sub SetSelectedAnnotationOverlay(render As ImageProcessor.AnnotationOverlayRender)
            _selectedAnnotationOverlayMetrics = render
            SelectedAnnotationOverlayImage = render?.Image
        End Sub

        Private Sub UpdateSelectedAnnotationOverlayPreview()
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then
                SetSelectedAnnotationOverlay(Nothing)
                Return
            End If

            Dim annotation = _annotations(_selectedAnnotationIndex)
            If annotation Is Nothing OrElse Not annotation.IsVisible Then
                SetSelectedAnnotationOverlay(Nothing)
                Return
            End If

            ' Alles Sichtbare eines selektierten Objekts kommt aus dem Renderer: Formen und Symbole ohnehin,
            ' Text und Wasserzeichen seit TextRendersInOverlay. Pinsel- und Radiergummi-Ebenen haben kein
            ' Overlay - ihre Züge stehen bereits im gebackenen Bild.
            If Not UsesRenderedSelectionOverlay(annotation) AndAlso Not TextRendersInOverlay(annotation) Then
                SetSelectedAnnotationOverlay(Nothing)
                Return
            End If

            Dim previewSource = GetPreviewSource()
            Dim pixelWidth = 256
            Dim pixelHeight = 256
            If previewSource IsNot Nothing Then
                pixelWidth = Math.Max(48, CInt(Math.Round(annotation.WidthPixels)))
                pixelHeight = Math.Max(48, CInt(Math.Round(annotation.HeightPixels)))
            End If

            SetSelectedAnnotationOverlay(ImageProcessor.RenderAnnotationOverlay(annotation.Clone(), pixelWidth, pixelHeight))
        End Sub

        Private Shared Function ParseAvaloniaColorOrDefault(value As String, fallback As Avalonia.Media.Color) As Avalonia.Media.Color
            If String.IsNullOrWhiteSpace(value) Then Return fallback
            Try
                Return Avalonia.Media.Color.Parse(value)
            Catch
                Return fallback
            End Try
        End Function

        Private Shared Function NormalizeAvaloniaColor(value As String, fallback As String) As String
            If String.IsNullOrWhiteSpace(value) Then Return fallback
            Try
                ' Color.ToString() liefert für Farben, die exakt einer benannten CSS-Farbe entsprechen
                ' (z.B. "Transparent" für #00FFFFFF, "White", "Black", ...), den Namen statt Hex zurück.
                ' ImageProcessor.ParseColor versteht aber nur Hex-Werte und würde bei so einem Namen
                ' (kein gültiges Hex) still auf ihren Fallback (z.B. Weiß) zurückfallen - "Transparent"
                ' wurde dadurch beim Backen als Weiß statt durchsichtig gerendert. Deshalb hier immer
                ' explizit als #AARRGGBB-Hex formatieren statt Color.ToString() zu vertrauen.
                Dim c = Avalonia.Media.Color.Parse(value.Trim())
                Return $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}"
            Catch
                Return fallback
            End Try
        End Function

        Private Sub ApplyResizePreset(preset As String)
            Dim sourceWidth = GetCroppedWidth()
            Dim sourceHeight = GetCroppedHeight()
            If sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return

            Select Case If(preset, "").Trim().ToLowerInvariant()
                Case "original"
                    SetResizeValues(0, 0)
                Case "75%"
                    SetResizeValues(Math.Max(1, CInt(Math.Round(sourceWidth * 0.75))), Math.Max(1, CInt(Math.Round(sourceHeight * 0.75))))
                Case "50%"
                    SetResizeValues(Math.Max(1, sourceWidth \ 2), Math.Max(1, sourceHeight \ 2))
                Case "25%"
                    SetResizeValues(Math.Max(1, sourceWidth \ 4), Math.Max(1, sourceHeight \ 4))
                Case "uhd"
                    SetLongestEdge(3840)
                Case "full-hd"
                    SetLongestEdge(1920)
                Case "sd"
                    SetLongestEdge(640)
                Case "1500"
                    SetResizeValues(1500, 1500)
                Case "1000"
                    SetResizeValues(1000, 1000)
                Case "800"
                    SetResizeValues(800, 800)
                Case "500"
                    SetResizeValues(500, 500)
                Case "256"
                    SetResizeValues(256, 256)
                Case "128"
                    SetResizeValues(128, 128)
                Case "64"
                    SetResizeValues(64, 64)
                Case "32"
                    SetResizeValues(32, 32)
            End Select
        End Sub

        Private Sub SetLongestEdge(edge As Integer)
            Dim sourceWidth = GetCroppedWidth()
            Dim sourceHeight = GetCroppedHeight()
            If sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse edge <= 0 Then Return

            If sourceWidth >= sourceHeight Then
                Dim h = CInt(Math.Round(sourceHeight * (edge / CDbl(sourceWidth))))
                SetResizeValues(edge, Math.Max(1, h))
            Else
                Dim w = CInt(Math.Round(sourceWidth * (edge / CDbl(sourceHeight))))
                SetResizeValues(Math.Max(1, w), edge)
            End If
        End Sub

        Private Sub SyncResizeHeightFromWidth()
            Dim sourceWidth = GetCroppedWidth()
            Dim sourceHeight = GetCroppedHeight()
            If sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse _resizeWidth <= 0 Then Return
            _isUpdatingResize = True
            _resizeHeight = Math.Max(1, CInt(Math.Round(sourceHeight * (_resizeWidth / CDbl(sourceWidth)))))
            _isUpdatingResize = False
            Me.RaisePropertyChanged(NameOf(ResizeHeight))
            Me.RaisePropertyChanged(NameOf(OutputSizeText))
            RaiseResetButtonStateChanged()
        End Sub

        Private Sub SyncResizeWidthFromHeight()
            Dim sourceWidth = GetCroppedWidth()
            Dim sourceHeight = GetCroppedHeight()
            If sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse _resizeHeight <= 0 Then Return
            _isUpdatingResize = True
            _resizeWidth = Math.Max(1, CInt(Math.Round(sourceWidth * (_resizeHeight / CDbl(sourceHeight)))))
            _isUpdatingResize = False
            Me.RaisePropertyChanged(NameOf(ResizeWidth))
            Me.RaisePropertyChanged(NameOf(OutputSizeText))
            RaiseResetButtonStateChanged()
        End Sub

        ''' <summary>Beschnitt aus dem gezogenen Rahmen im Bild. Die Werte rasten auf ganze Bildpixel ein:
        ''' geschnitten werden ohnehin nur ganze Pixel, und der Rahmen wird aus genau diesen Prozentwerten
        ''' gezeichnet - ohne Einrasten stünde er bei hohem Zoom neben der Kante, die tatsächlich fällt.</summary>
        Public Sub SetCropPercentages(left As Double, top As Double, right As Double, bottom As Double)
            Dim width = EffectiveImageWidthPixels
            Dim height = EffectiveImageHeightPixels
            If width > 0 AndAlso height > 0 Then
                left = PixelsToPercent(PercentToPixels(left, width), width)
                right = PixelsToPercent(PercentToPixels(right, width), width)
                top = PixelsToPercent(PercentToPixels(top, height), height)
                bottom = PixelsToPercent(PercentToPixels(bottom, height), height)
            End If
            SetCropValues(left, top, right, bottom)
        End Sub

        ''' captureUndo=True markiert den Beginn eines Zuges (Mausklick), False die Zwischenpunkte
        ''' beim Ziehen.
        Public Sub AddRetouchSpot(xPercent As Double, yPercent As Double, Optional captureUndo As Boolean = True)
            ' Der Stempel braucht eine Quelle. Ohne sie würde er stillschweigend zum Verwischen -
            ' der Nutzer soll stattdessen erst Alt+Klick machen (siehe RetouchHintText).
            If IsCloneMode AndAlso Not HasCloneSource Then Return
            If captureUndo Then PushUndo()

            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            Dim targetX = Math.Max(0, Math.Min(baseWidth, PercentXToPixels(xPercent)))
            Dim targetY = Math.Max(0, Math.Min(baseHeight, PercentYToPixels(yPercent)))

            Dim spot = New RetouchSpot With {
                .XPixels = CSng(targetX),
                .YPixels = CSng(targetY),
                .RadiusPixels = CSng(_retouchRadius)
            }

            If IsCloneMode AndAlso HasCloneSource Then
                ' Der Versatz entsteht beim ersten Punkt nach dem Setzen der Quelle und bleibt dann
                ' stehen - so wandert beim Ziehen ein zusammenhängender Ausschnitt mit.
                If Not _hasCloneOffset Then
                    _cloneOffsetXPixels = targetX - PercentXToPixels(_cloneSourceXPercent)
                    _cloneOffsetYPixels = targetY - PercentYToPixels(_cloneSourceYPercent)
                    _hasCloneOffset = True
                End If

                Dim sourceX = targetX - _cloneOffsetXPixels
                Dim sourceY = targetY - _cloneOffsetYPixels
                ' Wandert die Quelle beim Ziehen aus dem Bild, bleibt der Punkt ohne Quelle und fällt
                ' auf den Ringmittelwert zurück, statt an der Bildkante Pixel zu wiederholen.
                If sourceX >= 0 AndAlso sourceY >= 0 AndAlso sourceX <= baseWidth AndAlso sourceY <= baseHeight Then
                    spot.SourceXPixels = CSng(sourceX)
                    spot.SourceYPixels = CSng(sourceY)
                End If
            End If

            _retouchSpots.Add(spot)
            ' Retusche hat das Dokument bisher nicht als geändert markiert: UpdatePreview() setzt
            ' _hasChanges nicht, und AddRetouchSpot lief nie über SchedulePreviewUpdate. Wer nur
            ' retuschierte und den Editor verließ, wurde nicht gefragt und verlor die Arbeit.
            _hasChanges = True

            ' Nur der Zugbeginn schreibt in die Historie - die Zwischenpunkte eines Zuges würden sonst
            ' die 30 Einträge fluten. Gerendert wird weiterhin bei jedem Punkt: Retusche ist direkte
            ' Manipulation und muss auch bei abgeschalteter Live-Vorschau sofort sichtbar sein.
            If captureUndo Then AddHistoryEntry(If(IsCloneMode, "Stempeln", "Verwischen"))
            UpdatePreview()
        End Sub

        Private Sub ResetHslInternal()
            ResetHslFields()
            RaiseExtendedAdjustmentProperties()
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub ResetLightInternal()
            _brightness = 0
            _contrast = 0
            _highlights = 0
            _shadowsLevel = 0
            _whites = 0
            _blacks = 0
            _exposure = 0
            RaiseLightPropertiesChanged()
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub ResetColorInternal()
            _whiteBalance = "Wie Aufnahme"
            _temperature = 0
            _tint = 0
            _vibrance = 0
            _saturation = 0
            Me.RaisePropertyChanged(NameOf(WhiteBalance))
            Me.RaisePropertyChanged(NameOf(Temperature))
            Me.RaisePropertyChanged(NameOf(Tint))
            Me.RaisePropertyChanged(NameOf(Vibrance))
            Me.RaisePropertyChanged(NameOf(Saturation))
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        ''' Eigene Gruppe im Werkzeug "Farbe" (unterhalb des HSL-Farbmischers) - wird sowohl vom
        ''' eigenen Reset-Icon der Split-Toning-Gruppe als auch vom "Farbe"-Tool-Reset aufgerufen.
        Private Sub ResetSplitToningInternal()
            _splitToningShadowHue = 0
            _splitToningShadowSaturation = 0
            _splitToningHighlightHue = 0
            _splitToningHighlightSaturation = 0
            _splitToningBalance = 0
            Me.RaisePropertyChanged(NameOf(SplitToningShadowHue))
            Me.RaisePropertyChanged(NameOf(SplitToningShadowSaturation))
            Me.RaisePropertyChanged(NameOf(SplitToningHighlightHue))
            Me.RaisePropertyChanged(NameOf(SplitToningHighlightSaturation))
            Me.RaisePropertyChanged(NameOf(SplitToningBalance))
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub ResetCurrentToolInternal()
            Select Case _currentTool
                Case EditorTool.Crop
                    SetCropValues(0, 0, 0, 0)
                    _appliedCropLeft = 0
                    _appliedCropTop = 0
                    _appliedCropRight = 0
                    _appliedCropBottom = 0
                    RaiseResetButtonStateChanged()
                    SchedulePreviewUpdate()
                Case EditorTool.Resize
                    _appliedResizeWidth = 0
                    _appliedResizeHeight = 0
                    _hasChanges = True
                    SetResizeValues(0, 0)
                Case EditorTool.Rotate, EditorTool.Transform
                    ResetTransformInternal()
                Case EditorTool.Adjust
                    ResetLightInternal()
                    ResetCurvePoints()
                    ResetNegativeInternal()
                    RaiseResetButtonStateChanged()
                    SchedulePreviewUpdate()
                Case EditorTool.Color
                    ResetColorInternal()
                    ResetHslInternal()
                    ResetSplitToningInternal()
                Case EditorTool.Effects
                    ResetDetailInternal()
                Case EditorTool.Frame
                    ResetEffectsInternal()
                Case EditorTool.Filters
                    ResetFilterInternal()
                Case EditorTool.Retouch
                    ResetRetouchInternal()
                Case Else
                    ResetAdjustmentsInternal()
            End Select
        End Sub

        Private Sub ResetDetailInternal()
            _clarity = 0
            _sharpness = 0
            _noiseReduction = 0
            _noiseReductionMethod = NoiseReductionMethod.Gaussian
            _dustScratches = 0
            _haze = 0
            _addNoise = 0
            _structure = 0
            _glow = 0
            RaiseDetailPropertiesChanged()
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub ResetEffectsInternal()
            _vignette = 0
            _vignetteTransition = 55
            _vignetteRoundness = 0
            _vignetteFeather = 70
            _vignetteCenterX = 50
            _vignetteCenterY = 50
            _grain = 0
            _borderSize = 0
            _borderCornerRadius = 0
            _borderEffect = "Einfach"
            _borderColor = "#FFFFFFFF"
            RaiseEffectsPropertiesChanged()
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        ''' Setzt neben den eigentlichen Filter-Werten (FilterPreset/FilterStrength) auch alle Werte
        ''' zurück, die ApplyLightroomPreset schreibt (Light/Color/Detail/Effects/HSL/Split-Toning/
        ''' Tonwertkurve) sowie die angewendete LUT - Lightroom-Presets und LUTs werden im selben Tab
        ''' angewendet, daher muss der "Filter zurücksetzen"-Button auch sie rückgängig machen können.
        Private Sub ResetFilterInternal()
            _filterPreset = "Keine"
            _filterStrength = 50
            _lutPath = ""
            _lutStrength = 100
            ClearLastAppliedLook()
            Me.RaisePropertyChanged(NameOf(FilterPreset))
            Me.RaisePropertyChanged(NameOf(FilterStrength))
            Me.RaisePropertyChanged(NameOf(LutPath))
            Me.RaisePropertyChanged(NameOf(LutStrength))
            Me.RaisePropertyChanged(NameOf(HasLutApplied))
            ResetLightInternal()
            ResetColorInternal()
            ResetDetailInternal()
            ResetEffectsInternal()
            ResetHslInternal()
            ResetSplitToningInternal()
            ResetCurvePoints()
            RaiseExtendedAdjustmentProperties()
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub RaiseEffectsPropertiesChanged()
            Me.RaisePropertyChanged(NameOf(Vignette))
            Me.RaisePropertyChanged(NameOf(VignetteTransition))
            Me.RaisePropertyChanged(NameOf(VignetteRoundness))
            Me.RaisePropertyChanged(NameOf(VignetteFeather))
            Me.RaisePropertyChanged(NameOf(VignetteCenterX))
            Me.RaisePropertyChanged(NameOf(VignetteCenterY))
            Me.RaisePropertyChanged(NameOf(Grain))
            Me.RaisePropertyChanged(NameOf(BorderSize))
            Me.RaisePropertyChanged(NameOf(BorderCornerRadius))
            Me.RaisePropertyChanged(NameOf(BorderEffect))
            Me.RaisePropertyChanged(NameOf(BorderColor))
            Me.RaisePropertyChanged(NameOf(BorderColorValue))
            Me.RaisePropertyChanged(NameOf(BorderColorBrush))
        End Sub

        Private Sub ResetRetouchInternal()
            _retouchRadius = 24.0
            _retouchSpots.Clear()
            ClearCloneSource()
            Me.RaisePropertyChanged(NameOf(RetouchRadius))
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub ResetTransformInternal()
            _rotationDegrees = 0
            _straightenDegrees = 0
            _straightenExpandCanvas = False
            _flipH = False
            _flipV = False
            _appliedRotationDegrees = 0
            _appliedStraightenDegrees = 0
            _appliedStraightenExpandCanvas = False
            _appliedFlipH = False
            _appliedFlipV = False
            Me.RaisePropertyChanged(NameOf(StraightenDegrees))
            Me.RaisePropertyChanged(NameOf(StraightenExpandCanvas))
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Shared Function PointsToCurveString(points As ObservableCollection(Of Avalonia.Point)) As String
            Return String.Join(";", points.Select(Function(p) $"{p.X.ToString(Globalization.CultureInfo.InvariantCulture)},{p.Y.ToString(Globalization.CultureInfo.InvariantCulture)}"))
        End Function

        Private Sub LoadCurvePointsFromString(target As ObservableCollection(Of Avalonia.Point), value As String)
            Dim parsed As New List(Of Avalonia.Point)()
            If Not String.IsNullOrWhiteSpace(value) Then
                For Each pair In value.Split(";"c)
                    Dim parts = pair.Split(","c)
                    If parts.Length = 2 Then
                        Dim x As Double
                        Dim y As Double
                        If Double.TryParse(parts(0), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, x) AndAlso
                           Double.TryParse(parts(1), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, y) Then
                            parsed.Add(New Avalonia.Point(Math.Max(0, Math.Min(255, x)), Math.Max(0, Math.Min(255, y))))
                        End If
                    End If
                Next
            End If
            If parsed.Count = 0 Then
                parsed.Add(New Avalonia.Point(0, 0))
                parsed.Add(New Avalonia.Point(255, 255))
            End If
            _suppressCurvePointsChanged = True
            Try
                target.Clear()
                For Each p In parsed
                    target.Add(p)
                Next
            Finally
                _suppressCurvePointsChanged = False
            End Try
        End Sub

        Private Sub ResetCurvePoints()
            LoadCurvePointsFromString(_curveRgbPoints, "")
            LoadCurvePointsFromString(_curveRedPoints, "")
            LoadCurvePointsFromString(_curveGreenPoints, "")
            LoadCurvePointsFromString(_curveBluePoints, "")
            LoadCurvePointsFromString(_curveLuminancePoints, "")
        End Sub

        Private Sub OnCurvePointsChanged(sender As Object, e As System.Collections.Specialized.NotifyCollectionChangedEventArgs)
            If _suppressCurvePointsChanged Then Return
            CaptureUndoState("Tonwertkurve")
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub RaiseCurveChannelStateChanged()
            Me.RaisePropertyChanged(NameOf(IsCurveChannelRgb))
            Me.RaisePropertyChanged(NameOf(IsCurveChannelRed))
            Me.RaisePropertyChanged(NameOf(IsCurveChannelGreen))
            Me.RaisePropertyChanged(NameOf(IsCurveChannelBlue))
            Me.RaisePropertyChanged(NameOf(IsCurveChannelLuminance))
        End Sub

        Private Sub SetCurveChannel(channelName As String)
            Dim parsed As CurveChannel
            If [Enum].TryParse(channelName, True, parsed) Then
                SelectedCurveChannel = parsed
            End If
        End Sub

        Private Sub ResetHslFields()
            _redHue = 0 : _redSaturation = 0 : _redLuminance = 0
            _orangeHue = 0 : _orangeSaturation = 0 : _orangeLuminance = 0
            _yellowHue = 0 : _yellowSaturation = 0 : _yellowLuminance = 0
            _greenHue = 0 : _greenSaturation = 0 : _greenLuminance = 0
            _aquaHue = 0 : _aquaSaturation = 0 : _aquaLuminance = 0
            _blueHue = 0 : _blueSaturation = 0 : _blueLuminance = 0
            _purpleHue = 0 : _purpleSaturation = 0 : _purpleLuminance = 0
            _magentaHue = 0 : _magentaSaturation = 0 : _magentaLuminance = 0
        End Sub

        Private Sub RaiseLightPropertiesChanged()
            Me.RaisePropertyChanged(NameOf(Brightness))
            Me.RaisePropertyChanged(NameOf(Contrast))
            Me.RaisePropertyChanged(NameOf(Highlights))
            Me.RaisePropertyChanged(NameOf(ShadowsLevel))
            Me.RaisePropertyChanged(NameOf(Whites))
            Me.RaisePropertyChanged(NameOf(Blacks))
            Me.RaisePropertyChanged(NameOf(Exposure))
            RaiseResetButtonStateChanged()
        End Sub

        Private Sub RaiseDetailPropertiesChanged()
            Me.RaisePropertyChanged(NameOf(Clarity))
            Me.RaisePropertyChanged(NameOf(Sharpness))
            Me.RaisePropertyChanged(NameOf(NoiseReduction))
            Me.RaisePropertyChanged(NameOf(NoiseReductionMethodLabel))
            Me.RaisePropertyChanged(NameOf(DustScratches))
            Me.RaisePropertyChanged(NameOf(Haze))
            Me.RaisePropertyChanged(NameOf(AddNoise))
            Me.RaisePropertyChanged(NameOf([Structure]))
            Me.RaisePropertyChanged(NameOf(Glow))
            RaiseResetButtonStateChanged()
        End Sub

        Private Sub RaiseExtendedAdjustmentProperties()
            For Each name In {
                NameOf(Clarity), NameOf(ActiveCurvePoints), NameOf(ActiveCurveHistogramCounts),
                NameOf(NegativeEnabled), NameOf(NegativeMonochrome), NameOf(NegativeGamma),
                NameOf(NegativeBaseBrush), NameOf(HasNegativeBaseColor),
                NameOf(RedHue), NameOf(RedSaturation), NameOf(RedLuminance),
                NameOf(OrangeHue), NameOf(OrangeSaturation), NameOf(OrangeLuminance),
                NameOf(YellowHue), NameOf(YellowSaturation), NameOf(YellowLuminance),
                NameOf(GreenHue), NameOf(GreenSaturation), NameOf(GreenLuminance),
                NameOf(AquaHue), NameOf(AquaSaturation), NameOf(AquaLuminance),
                NameOf(BlueHue), NameOf(BlueSaturation), NameOf(BlueLuminance),
                NameOf(PurpleHue), NameOf(PurpleSaturation), NameOf(PurpleLuminance),
                NameOf(MagentaHue), NameOf(MagentaSaturation), NameOf(MagentaLuminance),
                NameOf(ActiveHslHue), NameOf(ActiveHslSaturation), NameOf(ActiveHslLuminance),
                NameOf(SplitToningShadowHue), NameOf(SplitToningShadowSaturation),
                NameOf(SplitToningHighlightHue), NameOf(SplitToningHighlightSaturation), NameOf(SplitToningBalance),
                NameOf(StraightenDegrees), NameOf(StraightenExpandCanvas)}
                Me.RaisePropertyChanged(name)
            Next
            RaiseResetButtonStateChanged()
        End Sub

        Private Sub RaiseToolContextProperties()
            Me.RaisePropertyChanged(NameOf(ShowCropAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowResizeAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowLightAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowColorAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowDetailAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowFrameAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowFilterAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowRetouchAdjustments))
            Me.RaisePropertyChanged(NameOf(IsCloneMode))
            Me.RaisePropertyChanged(NameOf(RetouchHintText))
            Me.RaisePropertyChanged(NameOf(ShowSelectionAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowDrawControls))
            Me.RaisePropertyChanged(NameOf(ShowLayerToolOptions))
            Me.RaisePropertyChanged(NameOf(ShowGeometryControls))
            Me.RaisePropertyChanged(NameOf(ShowTransformAdjustments))
            Me.RaisePropertyChanged(NameOf(SelectedPaintMode))
            Me.RaisePropertyChanged(NameOf(IsGeometryToolSelected))
        End Sub

        Private Sub SetPaintMode(mode As String)
            Dim normalized = If(mode, "").Trim().ToLowerInvariant()
            Dim previousPaintMode = SelectedPaintMode
            _overlayNotifySuppressDepth += 1
            Try
                PendingInsertKind = ""
                SelectedAnnotationIndex = -1

                Select Case normalized
                    Case "brush", "pinsel"
                        CurrentTool = EditorTool.Draw
                        IsEraserMode = False
                    Case "eraser", "radiergummi"
                        CurrentTool = EditorTool.Draw
                        IsEraserMode = True
                    Case "blur", "verwischen"
                        _isCloneMode = False
                        CurrentTool = EditorTool.Retouch
                    Case "clone", "stempel"
                        _isCloneMode = True
                        CurrentTool = EditorTool.Retouch
                    Case Else
                        CurrentTool = EditorTool.Draw
                        IsEraserMode = False
                End Select
                SelectedLayersPanelTab = LayersPanelTab.Tool
                If Not String.Equals(previousPaintMode, SelectedPaintMode, StringComparison.Ordinal) Then
                    StorePaintToolState(previousPaintMode)
                    ApplyPaintToolState(SelectedPaintMode)
                End If
            Finally
                _overlayNotifySuppressDepth -= 1
            End Try
            NotifyAnnotationOverlayStateChanged()
            Me.RaisePropertyChanged(NameOf(SelectedPaintMode))
            Me.RaisePropertyChanged(NameOf(IsCloneMode))
            ' Verwischen <-> Stempel wechselt das Werkzeug nicht, wohl aber seinen Namen.
            Me.RaisePropertyChanged(NameOf(CurrentToolLabel))
            Me.RaisePropertyChanged(NameOf(CurrentToolIconSource))
            RaiseCloneSourceProperties()
        End Sub

        ''' Sichert die Regler des Werkzeugs, das gerade verlassen wird. Für ein Nicht-Malwerkzeug
        ''' (paintMode ist dann leer) gibt es nichts zu sichern.
        Private Sub StorePaintToolState(paintMode As String)
            Dim state As PaintToolState = Nothing
            If String.IsNullOrEmpty(paintMode) OrElse Not _paintToolStates.TryGetValue(paintMode, state) Then Return

            Select Case paintMode
                Case "Brush"
                    state.StrokeColor = _annotationStrokeColor
                    state.Size = _brushSize
                    state.Hardness = _brushHardness
                    state.Opacity = _brushOpacity
                Case "Eraser"
                    state.Size = _brushSize
                    state.Hardness = _brushHardness
                    state.Opacity = _brushOpacity
                Case "Blur", "Clone"
                    state.Size = _retouchRadius
            End Select
        End Sub

        Private Sub ApplyPaintToolState(paintMode As String)
            Dim state As PaintToolState = Nothing
            If String.IsNullOrEmpty(paintMode) OrElse Not _paintToolStates.TryGetValue(paintMode, state) Then Return

            Select Case paintMode
                Case "Brush"
                    AnnotationStrokeColor = state.StrokeColor
                    BrushSize = state.Size
                    BrushHardness = state.Hardness
                    BrushOpacity = state.Opacity
                Case "Eraser"
                    BrushSize = state.Size
                    BrushHardness = state.Hardness
                    BrushOpacity = state.Opacity
                Case "Blur", "Clone"
                    RetouchRadius = state.Size
            End Select
        End Sub

        Private Sub AddHistoryEntry(label As String)
            If HistoryItems Is Nothing Then Return
            If String.IsNullOrWhiteSpace(label) Then label = "Bearbeitung"
            HistoryItems.Insert(0, $"{DateTime.Now:HH:mm:ss}  {label}")
            While HistoryItems.Count > 30
                HistoryItems.RemoveAt(HistoryItems.Count - 1)
            End While
        End Sub

        Private Function BuildHistoryLabel(adj As ImageAdjustments) As String
            If adj.CropLeftPercent <> 0 OrElse adj.CropTopPercent <> 0 OrElse adj.CropRightPercent <> 0 OrElse adj.CropBottomPercent <> 0 Then Return "Zuschneiden"
            If adj.ResizeWidth > 0 OrElse adj.ResizeHeight > 0 Then Return "Bildgröße"
            If adj.CanvasWidth > 0 OrElse adj.CanvasHeight > 0 Then Return "Leinwandgröße"
            If adj.Annotations IsNot Nothing AndAlso adj.Annotations.Count > 0 Then Return "Text"
            If adj.RotationDegrees <> 0 OrElse adj.StraightenDegrees <> 0 OrElse adj.FlipHorizontal OrElse adj.FlipVertical Then Return "Transformieren"
            If Not ImageAdjustments.IsIdentityCurve(adj.CurveRgbPoints) OrElse Not ImageAdjustments.IsIdentityCurve(adj.CurveRedPoints) OrElse
               Not ImageAdjustments.IsIdentityCurve(adj.CurveGreenPoints) OrElse Not ImageAdjustments.IsIdentityCurve(adj.CurveBluePoints) OrElse
               Not ImageAdjustments.IsIdentityCurve(adj.CurveLuminancePoints) Then Return "Tonwertkurve"
            If adj.HasHslChanges() Then Return "Farbmischer"
            If adj.Clarity <> 0 OrElse adj.Sharpness <> 0 OrElse adj.NoiseReduction <> 0 OrElse adj.Grain <> 0 Then Return "Details"
            If adj.Vignette <> 0 OrElse adj.BorderSize <> 0 Then Return "Vignette/Rahmen"
            If Not String.Equals(adj.FilterPreset, "Keine", StringComparison.OrdinalIgnoreCase) Then Return "Filter"
            Return "Anpassung"
        End Function

        Private Shared Function GetFormatFromExtension(extension As String) As String
            Select Case If(extension, "").Trim().TrimStart("."c).ToUpperInvariant()
                Case "JPEG", "JPG"
                    Return "JPG"
                Case "PNG"
                    Return "PNG"
                Case "WEBP"
                    Return "WEBP"
                Case Else
                    Return "JPG"
            End Select
        End Function

        Private Sub SetInfoTab(tabName As String)
            Select Case If(tabName, "").Trim().ToLowerInvariant()
                Case "exif"
                    SelectedInfoTab = InfoSidebarTab.Exif
                Case "iptc"
                    SelectedInfoTab = InfoSidebarTab.Iptc
                Case "xmp"
                    SelectedInfoTab = InfoSidebarTab.Xmp
                Case "icc"
                    SelectedInfoTab = InfoSidebarTab.Icc
                Case Else
                    SelectedInfoTab = InfoSidebarTab.General
            End Select
        End Sub

        Private Sub RaiseInfoTabStateChanged()
            Me.RaisePropertyChanged(NameOf(IsInfoTabGeneral))
            Me.RaisePropertyChanged(NameOf(IsInfoTabExif))
            Me.RaisePropertyChanged(NameOf(IsInfoTabIptc))
            Me.RaisePropertyChanged(NameOf(IsInfoTabXmp))
            Me.RaisePropertyChanged(NameOf(IsInfoTabIcc))
        End Sub




        ''' Übernimmt den Look eines Lightroom-/Camera-Raw-Presets. Die Abbildung der crs:-Schlüssel
        ''' liegt in LightroomPresetService - die Stapelverarbeitung der Galerie braucht exakt dieselbe.
        Public Sub ApplyLightroomPreset(xmpPath As String)
            If String.IsNullOrWhiteSpace(xmpPath) OrElse Not File.Exists(xmpPath) Then Return
            Try
                Dim look = LightroomPresetService.LoadLook(xmpPath)
                If look Is Nothing Then Return

                PushUndo()
                ' Erst den bisherigen Look neutralisieren: ein Preset ersetzt ihn, es mischt sich nicht
                ' dazu. Beschnitt, Geometrie und Objekte bleiben dabei stehen.
                ResetFilterInternal()
                _suppressUndoCapture = True
                Try
                    ApplyLookAdjustments(look)
                Finally
                    _suppressUndoCapture = False
                End Try

                RaiseExtendedAdjustmentProperties()
                SetLastAppliedLightroomPreset(xmpPath)
                StatusText = LocalizationService.T("Lightroom-Preset angewendet")
                SchedulePreviewUpdate()
            Catch ex As Exception
                StatusText = LocalizationService.T("Lightroom-Preset konnte nicht geladen werden: ") & ex.Message
            End Try
        End Sub

        ''' Schreibt die Look-Felder eines ImageAdjustments in die Regler des Editors - über die
        ''' öffentlichen Eigenschaften, damit Vorschau und Bindings genauso benachrichtigt werden wie bei
        ''' Handbedienung. Geometrie, Beschnitt, Objekte und Auswahl fasst es NICHT an.
        Private Sub ApplyLookAdjustments(look As ImageAdjustments)
            Exposure = look.Exposure
            Contrast = look.Contrast
            Highlights = look.Highlights
            ShadowsLevel = look.ShadowsLevel
            Whites = look.Whites
            Blacks = look.Blacks
            Clarity = look.Clarity
            [Structure] = look.[Structure]
            Haze = look.Haze
            Vibrance = look.Vibrance
            Saturation = look.Saturation
            Sharpness = look.Sharpness
            NoiseReduction = look.NoiseReduction
            Grain = look.Grain
            Vignette = look.Vignette
            Temperature = look.Temperature
            Tint = look.Tint

            RedHue = look.RedHue : RedSaturation = look.RedSaturation : RedLuminance = look.RedLuminance
            OrangeHue = look.OrangeHue : OrangeSaturation = look.OrangeSaturation : OrangeLuminance = look.OrangeLuminance
            YellowHue = look.YellowHue : YellowSaturation = look.YellowSaturation : YellowLuminance = look.YellowLuminance
            GreenHue = look.GreenHue : GreenSaturation = look.GreenSaturation : GreenLuminance = look.GreenLuminance
            AquaHue = look.AquaHue : AquaSaturation = look.AquaSaturation : AquaLuminance = look.AquaLuminance
            BlueHue = look.BlueHue : BlueSaturation = look.BlueSaturation : BlueLuminance = look.BlueLuminance
            PurpleHue = look.PurpleHue : PurpleSaturation = look.PurpleSaturation : PurpleLuminance = look.PurpleLuminance
            MagentaHue = look.MagentaHue : MagentaSaturation = look.MagentaSaturation : MagentaLuminance = look.MagentaLuminance

            SplitToningShadowHue = look.SplitToningShadowHue
            SplitToningShadowSaturation = look.SplitToningShadowSaturation
            SplitToningHighlightHue = look.SplitToningHighlightHue
            SplitToningHighlightSaturation = look.SplitToningHighlightSaturation
            SplitToningBalance = look.SplitToningBalance

            LoadCurvePointsFromString(_curveRgbPoints, look.CurveRgbPoints)
            LoadCurvePointsFromString(_curveRedPoints, look.CurveRedPoints)
            LoadCurvePointsFromString(_curveGreenPoints, look.CurveGreenPoints)
            LoadCurvePointsFromString(_curveBluePoints, look.CurveBluePoints)
        End Sub

        Public Sub SaveLightroomPresetToSettings(xmpPath As String)
            If String.IsNullOrWhiteSpace(xmpPath) OrElse Not File.Exists(xmpPath) Then Return
            Dim normalizedPath = xmpPath.Trim()
            Dim existing = SavedLightroomPresets.FirstOrDefault(Function(p) String.Equals(p.Path, normalizedPath, StringComparison.OrdinalIgnoreCase))
            If existing Is Nothing Then
                SavedLightroomPresets.Add(New LightroomPresetSettings With {
                    .Id = Guid.NewGuid().ToString("N"),
                    .Name = IO.Path.GetFileNameWithoutExtension(normalizedPath),
                    .Path = normalizedPath
                })
                PersistSavedLightroomPresets()
            End If
        End Sub

        Public Sub RemoveLightroomPresetFromSettings(xmpPath As String)
            If String.IsNullOrWhiteSpace(xmpPath) Then Return
            Dim existing = SavedLightroomPresets.FirstOrDefault(Function(p) String.Equals(p.Path, xmpPath.Trim(), StringComparison.OrdinalIgnoreCase))
            If existing Is Nothing Then Return
            If String.Equals(_lastAppliedLightroomPresetPath, existing.Path, StringComparison.OrdinalIgnoreCase) Then
                _lastAppliedLightroomPresetPath = ""
            End If
            SavedLightroomPresets.Remove(existing)
            PersistSavedLightroomPresets()
            SyncLastAppliedLightroomPreset()
        End Sub

        ''' MatchCasing.CaseInsensitive ist auf Linux/macOS nötig - Directory.EnumerateFiles matcht
        ''' das Suchmuster dort standardmäßig case-sensitiv, ".XMP"/".Cube" (nicht unüblich bei
        ''' exportierten Presets/LUT-Packs) würden sonst stillschweigend übersprungen.
        Private Shared ReadOnly CaseInsensitiveFileSearch As New EnumerationOptions With {
            .RecurseSubdirectories = True,
            .MatchCasing = MatchCasing.CaseInsensitive
        }

        Public Sub ImportLightroomPresetsFromFolder(folderPath As String)
            If String.IsNullOrWhiteSpace(folderPath) OrElse Not Directory.Exists(folderPath) Then Return
            Dim count = 0
            Try
                For Each file In Directory.EnumerateFiles(folderPath, "*.xmp", CaseInsensitiveFileSearch)
                    SaveLightroomPresetToSettings(file)
                    count += 1
                Next
            Catch
            End Try
            StatusText = If(count > 0,
                             LocalizationService.T("Presets importiert: ") & count.ToString(),
                             LocalizationService.T("Keine XMP-Dateien im Ordner gefunden"))
        End Sub

        Public Sub ApplyLutPreset(cubePath As String)
            If String.IsNullOrWhiteSpace(cubePath) OrElse Not File.Exists(cubePath) Then Return
            PushUndo()
            ResetFilterInternal()
            _suppressUndoCapture = True
            Try
                LutPath = cubePath
                LutStrength = 100
            Finally
                _suppressUndoCapture = False
            End Try
            SetLastAppliedLutPreset(cubePath)
            StatusText = LocalizationService.T("LUT angewendet")
            SchedulePreviewUpdate()
        End Sub

        Public Sub SaveLutPresetToSettings(cubePath As String)
            If String.IsNullOrWhiteSpace(cubePath) OrElse Not File.Exists(cubePath) Then Return
            Dim normalizedPath = cubePath.Trim()
            Dim existing = SavedLutPresets.FirstOrDefault(Function(p) String.Equals(p.Path, normalizedPath, StringComparison.OrdinalIgnoreCase))
            If existing Is Nothing Then
                SavedLutPresets.Add(New LutPresetSettings With {
                    .Id = Guid.NewGuid().ToString("N"),
                    .Name = IO.Path.GetFileNameWithoutExtension(normalizedPath),
                    .Path = normalizedPath
                })
                PersistSavedLutPresets()
            End If
        End Sub

        Public Sub RemoveLutPresetFromSettings(cubePath As String)
            If String.IsNullOrWhiteSpace(cubePath) Then Return
            Dim existing = SavedLutPresets.FirstOrDefault(Function(p) String.Equals(p.Path, cubePath.Trim(), StringComparison.OrdinalIgnoreCase))
            If existing Is Nothing Then Return
            If String.Equals(_lastAppliedLutPresetPath, existing.Path, StringComparison.OrdinalIgnoreCase) Then
                _lastAppliedLutPresetPath = ""
            End If
            SavedLutPresets.Remove(existing)
            PersistSavedLutPresets()
            SyncLastAppliedLutPreset()
        End Sub

        Public Sub ImportLutPresetsFromFolder(folderPath As String)
            If String.IsNullOrWhiteSpace(folderPath) OrElse Not Directory.Exists(folderPath) Then Return
            Dim count = 0
            Try
                For Each file In Directory.EnumerateFiles(folderPath, "*.cube", CaseInsensitiveFileSearch)
                    SaveLutPresetToSettings(file)
                    count += 1
                Next
            Catch
            End Try
            StatusText = If(count > 0,
                             LocalizationService.T("LUTs importiert: ") & count.ToString(),
                             LocalizationService.T("Keine .cube-Dateien im Ordner gefunden"))
        End Sub

        Private Shared Function HasInvalidFileNameChars(fileName As String) As Boolean
            If String.IsNullOrEmpty(fileName) Then Return True
            If fileName.IndexOf(IO.Path.DirectorySeparatorChar) >= 0 OrElse
               fileName.IndexOf(IO.Path.AltDirectorySeparatorChar) >= 0 Then Return True

            Dim invalidChars = IO.Path.GetInvalidFileNameChars()
            Return invalidChars IsNot Nothing AndAlso invalidChars.Length > 0 AndAlso fileName.IndexOfAny(invalidChars) >= 0
        End Function


        Private Sub RefreshHistogram()
            If String.IsNullOrEmpty(_currentImagePath) Then
                HistogramImage = Nothing
                _curveHistogramCounts = (New Integer(255) {}, New Integer(255) {}, New Integer(255) {}, New Integer(255) {})
                Me.RaisePropertyChanged(NameOf(ActiveCurveHistogramCounts))
                Return
            End If
            Dim previewSource = GetPreviewSource()
            If previewSource IsNot Nothing Then
                HistogramImage = ImageProcessor.BuildHistogramImage(previewSource, 240, 120)
                _curveHistogramCounts = ImageProcessor.BuildChannelHistogramCounts(previewSource)
            Else
                HistogramImage = ImageProcessor.BuildHistogramImage(_currentImagePath, 240, 120)
                _curveHistogramCounts = ImageProcessor.BuildChannelHistogramCounts(_currentImagePath)
            End If
            Me.RaisePropertyChanged(NameOf(ActiveCurveHistogramCounts))
        End Sub

        Public Sub RefreshLocalization()
            Me.RaisePropertyChanged(NameOf(CurrentFileName))
            Me.RaisePropertyChanged(NameOf(StatusText))
        End Sub

        Private Class PreviewRenderResult
            Implements IDisposable

            Public Property Preview As Bitmap
            Public Property Comparison As Bitmap

            Public Sub New(preview As Bitmap, comparison As Bitmap)
                Me.Preview = preview
                Me.Comparison = comparison
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                If TypeOf Preview Is IDisposable Then
                    DirectCast(Preview, IDisposable).Dispose()
                End If
                If TypeOf Comparison Is IDisposable Then
                    DirectCast(Comparison, IDisposable).Dispose()
                End If
            End Sub
        End Class
    End Class

    Public Enum EditorTool
        None
        Crop
        Resize
        Rotate
        Adjust
        Filters
        Color
        Effects
        Transform
        Retouch
        Text
        Draw
        Geometry
        Insert
        Frame
        Selection
    End Enum

    Public Enum LayersPanelTab
        Tool
        Layers
        History
    End Enum

    Public Enum CurveChannel
        Rgb
        Red
        Green
        Blue
        Luminance
    End Enum

    ''' Ein wählbares SVG-Icon aus Assets/Icons/** im "Formen & Symbole"-Auswahlraster.
    Public Class ShapeIconEntry
        Implements ComponentModel.INotifyPropertyChanged

        Public Event PropertyChanged As ComponentModel.PropertyChangedEventHandler Implements ComponentModel.INotifyPropertyChanged.PropertyChanged

        Public Property IconPath As String
        Public Property DisplayName As String
        Public Property SourceName As String
        Public Property PendingKind As String

        Private _isPending As Boolean
        ''' Scharf gestellt (Akzentrahmen) - Panel-Icon wurde angeklickt, Objekt wird beim nächsten
        ''' Klick ins Bild platziert. Analog zu den Text-/Bild-/QR-Buttons.
        Public Property IsPending As Boolean
            Get
                Return _isPending
            End Get
            Set(value As Boolean)
                If _isPending = value Then Return
                _isPending = value
                RaiseEvent PropertyChanged(Me, New ComponentModel.PropertyChangedEventArgs(NameOf(IsPending)))
            End Set
        End Property

        Private _isSelectedKind As Boolean
        ''' Ein bereits platziertes Objekt dieses Icons ist aktuell im Bild selektiert (nur Icon-Akzent,
        ''' kein Rahmen) - analog zur selected-kind-Klasse der Text-/Bild-/QR-Buttons.
        Public Property IsSelectedKind As Boolean
            Get
                Return _isSelectedKind
            End Get
            Set(value As Boolean)
                If _isSelectedKind = value Then Return
                _isSelectedKind = value
                RaiseEvent PropertyChanged(Me, New ComponentModel.PropertyChangedEventArgs(NameOf(IsSelectedKind)))
            End Set
        End Property
    End Class

End Namespace
