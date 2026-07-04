Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Linq
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
        Private _currentImage As Bitmap
        Private _previewImage As Bitmap
        Private _comparisonImage As Bitmap
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
        Private _exposure As Double = 0
        Private _sharpness As Double = 0
        Private _noiseReduction As Double = 0
        Private _noiseReductionMethod As NoiseReductionMethod = NoiseReductionMethod.Gaussian
        Private _vibrance As Double = 0
        Private _vignette As Double = 0
        Private _grain As Double = 0
        Private _borderSize As Double = 0
        Private _borderColor As String = "#FFFFFFFF"
        Private _clarity As Double = 0
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
        Private _orangeHue As Double = 0
        Private _orangeSaturation As Double = 0
        Private _yellowHue As Double = 0
        Private _yellowSaturation As Double = 0
        Private _greenHue As Double = 0
        Private _greenSaturation As Double = 0
        Private _aquaHue As Double = 0
        Private _aquaSaturation As Double = 0
        Private _blueHue As Double = 0
        Private _blueSaturation As Double = 0
        Private _purpleHue As Double = 0
        Private _purpleSaturation As Double = 0
        Private _magentaHue As Double = 0
        Private _magentaSaturation As Double = 0
        Private _retouchRadius As Double = 2.0
        Private _brushSize As Double = 2.5
        Private _brushHardness As Double = 100
        Private _brushOpacity As Double = 100
        Private _isEraserMode As Boolean = False
        Private ReadOnly _retouchSpots As New List(Of RetouchSpot)()
        Private _filterPreset As String = "Keine"
        Private _filterStrength As Double = 100
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
        Private _annotationFillColor As String = "#FFFFFFFF"
        Private _annotationStrokeColor As String = "#FF000000"
        Private _annotationFontSize As Double = 6
        Private _annotationStrokeWidth As Double = 0
        Private _annotationFontFamily As String = "Arial"
        Private _annotationOpacity As Double = 100
        Private _annotationRotation As Double = 0
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
        Private _annotationShadowBlur As Double = 6
        Private _annotationShadowStrength As Double = 100
        Private _annotationShadowColor As String = "#80000000"
        Private _annotationGlowEnabled As Boolean = False
        Private _annotationGlowBlur As Double = 10
        Private _annotationGlowStrength As Double = 100
        Private _annotationGlowColor As String = "#FFFFFF00"
        Private _hasActiveSelection As Boolean = False
        Private _selectionXPercent As Double = 0
        Private _selectionYPercent As Double = 0
        Private _selectionWidthPercent As Double = 0
        Private _selectionHeightPercent As Double = 0
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
        Private _saveQuality As Integer = 90
        Private _exportFormat As String = "JPG"
        Private _histogramImage As Bitmap
        Private _exifInfo As ExifData
        Private _showExifInfo As Boolean = True
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
        Private _livePreviewEnabled As Boolean = True
        Private _showBeforeImage As Boolean = False
        Private _folderPaths As New List(Of String)()
        Private _currentIndex As Integer = -1
        Private _selectedInfoTab As InfoSidebarTab = InfoSidebarTab.General
        Private _selectedLayersPanelTab As LayersPanelTab = LayersPanelTab.Tool
        Private Const PreviewMaxDimension As Integer = 1600
        Private Const UndoCaptureWindowMs As Double = 650
        Private _suppressUndoCapture As Boolean
        Private _lastUndoProperty As String = ""
        Private _lastUndoCapturedAt As DateTime = DateTime.MinValue

        Public Property WhiteBalanceOptions As New System.Collections.ObjectModel.ObservableCollection(Of String) From {
            "Wie Aufnahme", "Automatisch", "Tageslicht", "Bewölkt", "Schatten",
            "Glühlampe", "Leuchtstoff", "Blitz", "Benutzerdefiniert"
        }

        Public Property FilterPresetOptions As New System.Collections.ObjectModel.ObservableCollection(Of String) From {
            "Keine", "S/W", "Warm", "Kühl", "Fade", "Kontrast", "Sepia", "Matt", "Cross", "Dramatisch", "Weich",
            "Noir", "Duoton", "Polaroid", "VHS"
        }

        Public Property ExportFormatOptions As New ObservableCollection(Of String) From {
            "JPG", "PNG", "WEBP"
        }

        ' Undo-Stack
        Private ReadOnly _undoStack As New Stack(Of ImageAdjustments)()
        Private ReadOnly _redoStack As New Stack(Of ImageAdjustments)()

        Public Property FilmstripItems As BulkObservableCollection(Of ImageItem)
        Public Property Tags As ObservableCollection(Of String)
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
        Private ReadOnly _filteredShapeIcons As New ObservableCollection(Of ShapeIconEntry)()

        Public ReadOnly Property FilteredShapeIcons As ObservableCollection(Of ShapeIconEntry)
            Get
                Return _filteredShapeIcons
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
                Dim assets = Avalonia.Platform.AssetLoader.GetAssets(New Uri("avares://FerrumPix/Assets/Icons/"), Nothing)
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

        Private Shared Function FormatIconDisplayName(assetPath As String) As String
            Dim fileName = IO.Path.GetFileNameWithoutExtension(assetPath)
            Dim m = Text.RegularExpressions.Regex.Match(fileName, "^\d+_(?<rest>.+)$")
            Dim name = If(m.Success, m.Groups("rest").Value, fileName)
            Return name.Replace("_", " ")
        End Function

        Private Sub RefreshFilteredShapeIcons()
            Dim query = _shapeIconSearchText.Trim()
            Dim matches = If(String.IsNullOrEmpty(query),
                              _allShapeIcons,
                              _allShapeIcons.Where(Function(e) e.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) OrElse
                                                                e.SourceName.Contains(query, StringComparison.OrdinalIgnoreCase) OrElse
                                                                e.IconPath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList())
            _filteredShapeIcons.Clear()
            For Each item In matches
                _filteredShapeIcons.Add(item)
            Next
        End Sub

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
                    PendingInsertKind = ""
                    Dim targetTool = AnnotationKindToTool(_annotations(clamped).Kind)
                    If targetTool <> _currentTool Then
                        _overlayNotifySuppressDepth += 1
                        Try
                            CurrentTool = targetTool
                        Finally
                            _overlayNotifySuppressDepth -= 1
                        End Try
                    End If
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
                Me.RaisePropertyChanged(NameOf(SelectedAnnotationText))
                Me.RaisePropertyChanged(NameOf(ShowAnnotationProperties))
                Me.RaisePropertyChanged(NameOf(EffectiveAnnotationKind))
                Me.RaisePropertyChanged(NameOf(ShowTextContentControls))
                Me.RaisePropertyChanged(NameOf(ShowFontControls))
                Me.RaisePropertyChanged(NameOf(ShowFillColorControls))
                Me.RaisePropertyChanged(NameOf(ShowGradientFillControls))
                Me.RaisePropertyChanged(NameOf(ShowLinearGradientAngleControl))
                Me.RaisePropertyChanged(NameOf(ShowRadialGradientControl))
                Me.RaisePropertyChanged(NameOf(ShowStrokeWidthControls))
                Me.RaisePropertyChanged(NameOf(FillColorLabel))
                Me.RaisePropertyChanged(NameOf(StrokeColorLabel))
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
                If Not String.IsNullOrEmpty(v) Then SeedAnnotationDefaultsForKind(v)
                Me.RaisePropertyChanged(NameOf(PendingInsertKind))
                Me.RaisePropertyChanged(NameOf(HasPendingInsertKind))
                Me.RaisePropertyChanged(NameOf(ShowAnnotationProperties))
                Me.RaisePropertyChanged(NameOf(EffectiveAnnotationKind))
                Me.RaisePropertyChanged(NameOf(ShowTextContentControls))
                Me.RaisePropertyChanged(NameOf(ShowFontControls))
                Me.RaisePropertyChanged(NameOf(ShowFillColorControls))
                Me.RaisePropertyChanged(NameOf(ShowGradientFillControls))
                Me.RaisePropertyChanged(NameOf(ShowLinearGradientAngleControl))
                Me.RaisePropertyChanged(NameOf(ShowRadialGradientControl))
                Me.RaisePropertyChanged(NameOf(ShowStrokeWidthControls))
                Me.RaisePropertyChanged(NameOf(FillColorLabel))
                Me.RaisePropertyChanged(NameOf(StrokeColorLabel))
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
                Return k = "Text" OrElse k = "Watermark" OrElse k = "QR" OrElse k = "Symbol"
            End Get
        End Property

        ''' Schrift/Größe nur dort relevant, wo tatsächlich Text gerendert wird.
        Public ReadOnly Property ShowFontControls As Boolean
            Get
                Dim k = EffectiveAnnotationKind
                Return k = "Text" OrElse k = "Watermark"
            End Get
        End Property

        ''' Füllung ergibt bei eingefügten Bildern keinen Sinn (DrawImageAnnotation zeichnet nur das
        ''' Bild selbst, keine Füllfarbe dahinter) - dort blendet das Eigenschaften-Panel den Regler aus.
        Public ReadOnly Property ShowFillColorControls As Boolean
            Get
                Return EffectiveAnnotationKind <> "Image"
            End Get
        End Property

        Public ReadOnly Property ShowGradientFillControls As Boolean
            Get
                Return ShowFillColorControls AndAlso Not String.Equals(_annotationFillKind, "Solid", StringComparison.OrdinalIgnoreCase)
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
                            .Radius = 0.5
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
            AnnotationFontSize = 6
            AnnotationFontFamily = "Arial"
            AnnotationOpacity = 100
            AnnotationRotation = 0
            AnnotationIsVisible = True
            If normalizedKind = "Text" OrElse normalizedKind = "Watermark" Then
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
                If _currentTool = EditorTool.Retouch Then Return "Blur"
                If _currentTool = EditorTool.Draw AndAlso _isEraserMode Then Return "Eraser"
                If _currentTool = EditorTool.Draw Then Return "Brush"
                Return ""
            End Get
        End Property

        Public ReadOnly Property IsPaintToolSelected As Boolean
            Get
                Return _currentTool = EditorTool.Draw OrElse _currentTool = EditorTool.Retouch
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
                If Not TransparencyBrushService.CanHaveTransparency(_currentImagePath) Then
                    Return Avalonia.Media.Brushes.Transparent
                End If
                Dim settings = AppSettingsService.Load()
                Return TransparencyBrushService.GetBrush(settings.TransparencyBackgroundMode, settings.TransparencyBackgroundColor)
            End Get
        End Property

        Public Property LivePreviewEnabled As Boolean
            Get
                Return _livePreviewEnabled
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_livePreviewEnabled, value)
                If value AndAlso _hasChanges Then SchedulePreviewUpdate()
            End Set
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

        Public ReadOnly Property CanShowBeforeAfter As Boolean
            Get
                Return _currentTool <> EditorTool.Crop AndAlso
                       _currentTool <> EditorTool.Resize AndAlso
                       _currentTool <> EditorTool.Rotate AndAlso
                       _currentTool <> EditorTool.Transform AndAlso
                       _currentTool <> EditorTool.Text AndAlso
                       _currentTool <> EditorTool.Draw AndAlso
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
                Me.RaisePropertyChanged(NameOf(CurrentToolLabel))
                RaiseToolContextProperties()
                RequestOverlayStateNotify()
            End Set
        End Property

        Public ReadOnly Property CurrentToolLabel As String
            Get
                Select Case _currentTool
                    Case EditorTool.Crop : Return "Zuschneiden"
                    Case EditorTool.Resize : Return "Bildgröße"
                    Case EditorTool.Rotate : Return "Drehen"
                    Case EditorTool.Adjust : Return "Anpassen"
                    Case EditorTool.Filters : Return "Filter"
                    Case EditorTool.Effects : Return "Effekte"
                    Case EditorTool.Color : Return "Farbe"
                    Case EditorTool.Transform : Return "Transformieren"
                    Case EditorTool.Retouch : Return "Verwischen"
                    Case EditorTool.Text : Return "Text und Bild"
                    Case EditorTool.Draw : Return "Malen"
                    Case EditorTool.Geometry : Return "Formen und Symbole"
                    Case EditorTool.Insert : Return "Formen und Symbole"
                    Case EditorTool.Selection : Return "Auswahl"
                    Case Else : Return "Werkzeug"
                End Select
            End Get
        End Property

        Public ReadOnly Property ShowHistogramAdjustments As Boolean
            Get
                Return True
            End Get
        End Property

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

        Public ReadOnly Property ShowTextInsertControls As Boolean
            Get
                Return _currentTool = EditorTool.Text
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

        Public ReadOnly Property ShowExportAdjustments As Boolean
            Get
                Return True
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

        Public Property RetouchRadius As Double
            Get
                Return _retouchRadius
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_retouchRadius, Math.Max(0.2, Math.Min(10, value)))
                RaiseResetButtonStateChanged()
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property BrushSize As Double
            Get
                Return _brushSize
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_brushSize, Math.Max(0.2, Math.Min(15, value)))
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
                Me.RaisePropertyChanged(NameOf(IsBrushPaintMode))
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
                    Dim fullStrengthPreset = String.Equals(normalized, "S/W", StringComparison.OrdinalIgnoreCase) OrElse
                                              String.Equals(normalized, "Sepia", StringComparison.OrdinalIgnoreCase)
                    _filterStrength = If(fullStrengthPreset, 100, 50)
                    Me.RaisePropertyChanged(NameOf(FilterStrength))
                End If
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

        Public Property CropLeft As Double
            Get
                Return _cropLeft
            End Get
            Set(value As Double)
                Dim clamped = ClampCropEdge(value, _cropRight)
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
                Dim clamped = ClampCropEdge(value, _cropBottom)
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
                Dim clamped = ClampCropEdge(value, _cropLeft)
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
                Dim clamped = ClampCropEdge(value, _cropTop)
                If Math.Abs(_cropBottom - clamped) < 0.0001 Then Return
                Me.RaiseAndSetIfChanged(_cropBottom, clamped)
                AfterCropChanged()
                RaiseResetButtonStateChanged()
            End Set
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
                Me.RaiseAndSetIfChanged(_annotationFontSize, Math.Max(0.2, Math.Min(100, value)))
                Me.RaisePropertyChanged(NameOf(AnnotationFontSizePixels))
                UpdatePendingTextAnnotationSize()
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationFontSizePixels As Integer
            Get
                Return CInt(Math.Round(GetBaseHeight() * _annotationFontSize / 100.0))
            End Get
            Set(value As Integer)
                Dim baseHeight = GetBaseHeight()
                If baseHeight <= 0 Then Return
                AnnotationFontSize = value / CDbl(baseHeight) * 100.0
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
                SyncSelectedAnnotation()
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
            Dim minValue = -Math.Max(0.0, size - AnnotationMinVisiblePercent)
            Dim maxValue = 100.0 - AnnotationMinVisiblePercent
            Return Math.Max(minValue, Math.Min(maxValue, value))
        End Function

        Public Property AnnotationXPercent As Double
            Get
                Return _annotationXPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationXPercent, ClampAnnotationPositionPercent(value, _annotationWidthPercent))
                Me.RaisePropertyChanged(NameOf(AnnotationXPixels))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationYPercent As Double
            Get
                Return _annotationYPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationYPercent, ClampAnnotationPositionPercent(value, _annotationHeightPercent))
                Me.RaisePropertyChanged(NameOf(AnnotationYPixels))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationWidthPercent As Double
            Get
                Return _annotationWidthPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationWidthPercent, Math.Max(5, Math.Min(90, value)))
                Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationHeightPercent As Double
            Get
                Return _annotationHeightPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationHeightPercent, Math.Max(4, Math.Min(90, value)))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
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
                Me.RaiseAndSetIfChanged(_annotationShadowOffsetX, Math.Max(-20, Math.Min(20, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationShadowOffsetY As Double
            Get
                Return _annotationShadowOffsetY
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationShadowOffsetY, Math.Max(-20, Math.Min(20, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

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

        ''' Wird von EditorView beim Loslassen der Maus nach dem Aufziehen eines neuen
        ''' Auswahlrechtecks aufgerufen (ersetzt eine ggf. vorhandene alte Auswahl komplett - v1
        ''' kennt kein Verschieben/Skalieren einer bestehenden Auswahl).
        Public Sub SetSelectionRect(xPercent As Double, yPercent As Double, widthPercent As Double, heightPercent As Double)
            _selectionXPercent = Math.Max(0, xPercent)
            _selectionYPercent = Math.Max(0, yPercent)
            _selectionWidthPercent = Math.Max(0.5, widthPercent)
            _selectionHeightPercent = Math.Max(0.5, heightPercent)
            Me.RaisePropertyChanged(NameOf(SelectionXPercent))
            Me.RaisePropertyChanged(NameOf(SelectionYPercent))
            Me.RaisePropertyChanged(NameOf(SelectionWidthPercent))
            Me.RaisePropertyChanged(NameOf(SelectionHeightPercent))
            HasActiveSelection = True
        End Sub

        Public Sub ClearSelection()
            HasActiveSelection = False
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
            Dim pixelRect = New SKRectI(left, top, left + width, top + height)
            If Not ImageProcessor.ExtractRegionToFile(_currentImagePath, adj, pixelRect, tempPath) Then Return Nothing
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
                .XPercent = CSng(xPercent),
                .YPercent = CSng(yPercent),
                .WidthPercent = CSng(Math.Max(1.0, widthPercent)),
                .HeightPercent = CSng(Math.Max(1.0, heightPercent)),
                .FillColor = "#00FFFFFF",
                .StrokeColor = _annotationStrokeColor,
                .StrokeWidth = CSng(_annotationStrokeWidth),
                .FontSizePercent = CSng(_annotationFontSize),
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

        ''' Von ColorPickerButton.OnEyedropperClick aufgerufen: merkt sich, WELCHE Farbe (per Closure)
        ''' beim nächsten Bildklick gesetzt werden soll, statt eine feste ViewModel-Farbe zu kennen -
        ''' dadurch bleibt die Pipette für jede beliebige ColorPickerButton-Instanz wiederverwendbar.
        Public Sub BeginColorPick(onPicked As Action(Of Avalonia.Media.Color))
            _pendingColorPickCallback = onPicked
            IsPickingColorFromImage = True
        End Sub

        Public Sub CompleteColorPick(color As Avalonia.Media.Color)
            Dim callback = _pendingColorPickCallback
            _pendingColorPickCallback = Nothing
            IsPickingColorFromImage = False
            callback?.Invoke(color)
        End Sub

        Public Sub CancelColorPick()
            _pendingColorPickCallback = Nothing
            IsPickingColorFromImage = False
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
            Dim tempPath = CropSelectionToTempFile()
            If tempPath Is Nothing Then Return
            _selectionClipboardPath = tempPath
            _selectionClipboardXPercent = _selectionXPercent
            _selectionClipboardYPercent = _selectionYPercent
            _selectionClipboardWidthPercent = _selectionWidthPercent
            _selectionClipboardHeightPercent = _selectionHeightPercent
            _selectionClipboardPasteCount = 0
            AddHistoryEntry("Auswahl kopiert")
        End Sub

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
            PushUndo()
            Dim annotation = New ImageAnnotation With {
                .Kind = "SelectionFill",
                .Text = NextSelectionObjectLabel(),
                .ImagePath = "",
                .XPercent = CSng(_selectionXPercent),
                .YPercent = CSng(_selectionYPercent),
                .WidthPercent = CSng(_selectionWidthPercent),
                .HeightPercent = CSng(_selectionHeightPercent),
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
            _annotationWidthPercent = Math.Max(5, Math.Min(90, widthPercent))
            _annotationHeightPercent = Math.Max(4, Math.Min(90, heightPercent))
            _annotationXPercent = ClampAnnotationPositionPercent(xPercent, _annotationWidthPercent)
            _annotationYPercent = ClampAnnotationPositionPercent(yPercent, _annotationHeightPercent)
            Me.RaisePropertyChanged(NameOf(AnnotationXPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationYPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationXPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationYPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
            SyncSelectedAnnotation()
        End Sub

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
                Dim clamped = Math.Max(-45, Math.Min(45, value))
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
                If Not String.IsNullOrEmpty(_currentImagePath) Then
                    LibraryService.Instance.SetRating(_currentImagePath, value)
                End If
            End Set
        End Property

        Public Property IsFavorite As Boolean
            Get
                Return _isFavorite
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isFavorite, value)
                If Not String.IsNullOrEmpty(_currentImagePath) Then
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

        Public Property ExportFormat As String
            Get
                Return _exportFormat
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_exportFormat, If(String.IsNullOrWhiteSpace(value), "JPG", value.ToUpperInvariant()))
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

        Public Property ShowExifInfo As Boolean
            Get
                Return _showExifInfo
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_showExifInfo, value)
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

        Public ReadOnly Property CropSummary As String
            Get
                Dim w = GetCroppedWidth()
                Dim h = GetCroppedHeight()
                If w <= 0 OrElse h <= 0 Then Return ""
                Return $"{w} × {h} px"
            End Get
        End Property

        Public ReadOnly Property RecipePath As String
            Get
                If String.IsNullOrEmpty(_currentImagePath) Then Return ""
                Return _currentImagePath & ".fpxedit"
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
        Public ReadOnly Property CanSaveInPlace As Boolean
            Get
                Return Not IsCurrentImageRaw
            End Get
        End Property

        Public ReadOnly Property HasCropChanges As Boolean
            Get
                Return Math.Abs(_cropLeft - _appliedCropLeft) > 0.0001 OrElse
                       Math.Abs(_cropTop - _appliedCropTop) > 0.0001 OrElse
                       Math.Abs(_cropRight - _appliedCropRight) > 0.0001 OrElse
                       Math.Abs(_cropBottom - _appliedCropBottom) > 0.0001
            End Get
        End Property

        Public ReadOnly Property HasResizeChanges As Boolean
            Get
                Return _resizeWidth > 0 OrElse _resizeHeight > 0 OrElse _canvasWidth > 0 OrElse _canvasHeight > 0
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

        Public ReadOnly Property HasLightChanges As Boolean
            Get
                Return _brightness <> 0 OrElse _contrast <> 0 OrElse _highlights <> 0 OrElse
                       _shadowsLevel <> 0 OrElse _whites <> 0 OrElse _blacks <> 0 OrElse _exposure <> 0
            End Get
        End Property

        Public ReadOnly Property HasColorChanges As Boolean
            Get
                Return Not String.Equals(_whiteBalance, "Wie Aufnahme", StringComparison.Ordinal) OrElse
                       _temperature <> 0 OrElse _tint <> 0 OrElse _vibrance <> 0 OrElse _saturation <> 0
            End Get
        End Property

        Public ReadOnly Property HasDetailChanges As Boolean
            Get
                Return _clarity <> 0 OrElse _sharpness <> 0 OrElse _noiseReduction <> 0
            End Get
        End Property

        Public ReadOnly Property HasEffectsChanges As Boolean
            Get
                Return _vignette <> 0 OrElse _grain <> 0 OrElse _borderSize <> 0
            End Get
        End Property

        Public ReadOnly Property HasRetouchChanges As Boolean
            Get
                Return _retouchRadius <> 2.0 OrElse _retouchSpots.Count > 0
            End Get
        End Property

        Public ReadOnly Property HasCurveChanges As Boolean
            Get
                Return Not IsIdentityCurvePoints(_curveRgbPoints) OrElse Not IsIdentityCurvePoints(_curveRedPoints) OrElse
                       Not IsIdentityCurvePoints(_curveGreenPoints) OrElse Not IsIdentityCurvePoints(_curveBluePoints) OrElse
                       Not IsIdentityCurvePoints(_curveLuminancePoints)
            End Get
        End Property

        Public ReadOnly Property HasHslChanges As Boolean
            Get
                Return _redHue <> 0 OrElse _redSaturation <> 0 OrElse
                       _orangeHue <> 0 OrElse _orangeSaturation <> 0 OrElse
                       _yellowHue <> 0 OrElse _yellowSaturation <> 0 OrElse
                       _greenHue <> 0 OrElse _greenSaturation <> 0 OrElse
                       _aquaHue <> 0 OrElse _aquaSaturation <> 0 OrElse
                       _blueHue <> 0 OrElse _blueSaturation <> 0 OrElse
                       _purpleHue <> 0 OrElse _purpleSaturation <> 0 OrElse
                       _magentaHue <> 0 OrElse _magentaSaturation <> 0
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
        Public ReadOnly Property SetPaintModeCommand As ICommand
        Public ReadOnly Property SetPendingInsertKindCommand As ICommand
        Public ReadOnly Property SetRatingCommand As ICommand
        Public ReadOnly Property ToggleFavoriteCommand As ICommand
        Public ReadOnly Property AddTagCommand As ICommand
        Public ReadOnly Property RemoveTagCommand As ICommand
        Public ReadOnly Property ResetAdjustmentsCommand As ICommand
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
        Public ReadOnly Property ResetResizeCommand As ICommand
        Public ReadOnly Property SetResizePresetCommand As ICommand
        Public ReadOnly Property ResetCanvasCommand As ICommand
        Public ReadOnly Property SetCanvasAnchorCommand As ICommand
        Public ReadOnly Property AddTextAnnotationCommand As ICommand
        Public ReadOnly Property AddAnnotationCommand As ICommand
        Public ReadOnly Property DeleteSelectedAnnotationCommand As ICommand
        Public ReadOnly Property DeleteAnnotationCommand As ICommand
        Public ReadOnly Property ToggleAnnotationVisibilityCommand As ICommand
        Public ReadOnly Property SelectAnnotationCommand As ICommand
        Public ReadOnly Property DuplicateSelectedAnnotationCommand As ICommand
        Public ReadOnly Property MoveSelectedAnnotationUpCommand As ICommand
        Public ReadOnly Property MoveSelectedAnnotationDownCommand As ICommand
        Public ReadOnly Property AlignSelectedAnnotationCommand As ICommand
        Public ReadOnly Property ResetLightCommand As ICommand
        Public ReadOnly Property ResetColorCommand As ICommand
        Public ReadOnly Property ResetDetailCommand As ICommand
        Public ReadOnly Property ResetEffectsCommand As ICommand
        Public ReadOnly Property ResetRetouchCommand As ICommand
        Public ReadOnly Property CopySelectionCommand As ICommand
        Public ReadOnly Property FillSelectionCommand As ICommand
        Public ReadOnly Property SetAnnotationFillKindCommand As ICommand
        Public ReadOnly Property ResetTransformCommand As ICommand
        Public ReadOnly Property SetFilterPresetCommand As ICommand
        Public ReadOnly Property ExportCommand As ICommand
        Public ReadOnly Property SaveRecipeCommand As ICommand
        Public ReadOnly Property LoadRecipeCommand As ICommand
        Public ReadOnly Property ResetCurveCommand As ICommand
        Public ReadOnly Property SetCurveChannelCommand As ICommand
        Public ReadOnly Property ResetHslCommand As ICommand
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
            HistoryItems = New ObservableCollection(Of String)()
            LoadAllShapeIcons()
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
                                                       If Not String.IsNullOrEmpty(_currentImagePath) Then
                                                           LibraryService.Instance.SetTags(_currentImagePath, Tags)
                                                       End If
                                                   End Sub)

            RemoveTagCommand = ReactiveCommand.Create(Of String)(Sub(tag)
                                                                     If Tags.Remove(tag) AndAlso Not String.IsNullOrEmpty(_currentImagePath) Then
                                                                         LibraryService.Instance.SetTags(_currentImagePath, Tags)
                                                                     End If
                                                                 End Sub)

            SetToolCommand = ReactiveCommand.Create(Of String)(Sub(toolName)
                                                                   Dim parsed As EditorTool
                                                                   If [Enum].TryParse(toolName, parsed) Then
                                                                       _overlayNotifySuppressDepth += 1
                                                                       Try
                                                                           PendingInsertKind = ""
                                                                           SelectedAnnotationIndex = -1
                                                                           CurrentTool = parsed
                                                                       Finally
                                                                           _overlayNotifySuppressDepth -= 1
                                                                       End Try
                                                                       NotifyAnnotationOverlayStateChanged()
                                                                   End If
                                                               End Sub)
            SetPaintModeCommand = ReactiveCommand.Create(Of String)(Sub(mode)
                                                                        SetPaintMode(mode)
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

            ResetAdjustmentsCommand = ReactiveCommand.Create(Sub()
                                                                 PushUndo()
                                                                 ResetAdjustmentsInternal()
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
            AddTextAnnotationCommand = ReactiveCommand.Create(Sub()
                                                                  AddTextAnnotation()
                                                              End Sub)
            AddAnnotationCommand = ReactiveCommand.Create(Of String)(Sub(kind)
                                                                         AddAnnotation(kind)
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
            SelectAnnotationCommand = ReactiveCommand.Create(Of ImageAnnotation)(Sub(annotation)
                                                                                      SelectedAnnotationIndex = _annotations.IndexOf(annotation)
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
            AlignSelectedAnnotationCommand = ReactiveCommand.Create(Of String)(Sub(target)
                                                                                   AlignSelectedAnnotation(target)
                                                                               End Sub)
            ResetLightCommand = ReactiveCommand.Create(Sub()
                                                           PushUndo()
                                                           ResetLightInternal()
                                                       End Sub)
            ResetColorCommand = ReactiveCommand.Create(Sub()
                                                           PushUndo()
                                                           ResetColorInternal()
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
            CopySelectionCommand = ReactiveCommand.Create(Sub() CopySelectionToNewObject())
            FillSelectionCommand = ReactiveCommand.Create(Sub() FillSelection())
            SetAnnotationFillKindCommand = ReactiveCommand.Create(Of String)(Sub(kind) SetAnnotationFillKind(kind))
            ResetTransformCommand = ReactiveCommand.Create(Sub()
                                                               PushUndo()
                                                               ResetTransformInternal()
                                                           End Sub)
            SetFilterPresetCommand = ReactiveCommand.Create(Of String)(Sub(preset)
                                                                          PushUndo()
                                                                          FilterPreset = preset
                                                                      End Sub)
            ExportCommand = ReactiveCommand.Create(Sub() ExportImage())
            SaveRecipeCommand = ReactiveCommand.Create(Sub() SaveRecipe())
            LoadRecipeCommand = ReactiveCommand.Create(Sub() LoadRecipe())
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
            If Not String.IsNullOrEmpty(_currentImagePath) Then
                _mainVm.Viewer.OpenImage(_currentImagePath, _folderPaths.ToList())
                _mainVm.CurrentMode = AppMode.Viewer
            Else
                _mainVm.CurrentMode = AppMode.Viewer
            End If
        End Function

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
            ResetAdjustmentsInternal()
            ClearUndoHistory()
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
                StatusText = "Fehler beim Laden"
            End Try
        End Sub

        Public Sub OpenImage(imagePath As String, Optional allPaths As List(Of String) = Nothing)
            Dim ignored = OpenImageAsync(imagePath, allPaths)
        End Sub

        Public Async Function OpenImageAsync(imagePath As String, Optional allPaths As List(Of String) = Nothing) As Task(Of Boolean)
            If String.IsNullOrEmpty(imagePath) OrElse Not File.Exists(imagePath) Then Return False
            If Not String.IsNullOrEmpty(_currentImagePath) AndAlso Not String.Equals(_currentImagePath, imagePath, StringComparison.OrdinalIgnoreCase) Then
                If Not Await ConfirmSaveBeforeLeavingAsync("ein anderes Bild öffnest") Then Return False
            End If
            CurrentImagePath = imagePath
            _currentImagePath = imagePath
            SelectedInfoTab = InfoSidebarTab.General
            ResetAdjustmentsInternal()
            ClearUndoHistory()
            PreviewImage = Nothing
            ComparisonImage = Nothing
            PreparePreviewSource(imagePath)
            If allPaths IsNot Nothing Then
                LoadFilmstripContext(imagePath, allPaths)
            Else
                LoadFilmstripContext(imagePath)
            End If
            LoadLibraryMeta(imagePath)

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
                StatusText = "Fehler beim Laden"
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
            Task.Run(Sub() LibraryService.Instance.SetExifData(imagePath, exifForSearch))

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
                    Dim editableExts = {".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".heic", ".avif", ".ico"}
                    _folderPaths = allPaths.
                        Where(Function(p) Not String.IsNullOrEmpty(p)).
                        Where(Function(p) editableExts.Contains(IO.Path.GetExtension(p).ToLowerInvariant())).
                        Distinct(StringComparer.OrdinalIgnoreCase).
                        ToList()

                    FilmstripItems.ReplaceAll(_folderPaths.Select(Function(path) ImageItem.CreateLightweight(path)))

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

                Dim exts = {".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".heic", ".avif", ".ico"}
                _folderPaths = Directory.GetFiles(folder).
                    Where(Function(f) exts.Contains(IO.Path.GetExtension(f).ToLowerInvariant())).
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

        Private Sub SchedulePreviewUpdate()
            _hasChanges = True
            If Not _livePreviewEnabled Then Return
            _previewPending = True
            StatusText = "Vorschau wird aktualisiert..."
            _previewTimer.Stop()
            _previewTimer.Start()
        End Sub

        ''' Wie SchedulePreviewUpdate, markiert das Dokument aber NICHT als geändert (_hasChanges) -
        ''' für Werkzeuge mit expliziter "Anwenden"-Bestätigung (Zuschneiden/Bildgröße/Leinwandgröße/
        ''' Drehen): Live-Werte sollen sofort in der Vorschau sichtbar sein (siehe
        ''' GetCurrentAdjustments(forPreview:=True)), aber weder als ungespeicherte Änderung zählen
        ''' noch das kanonische Ergebnis beeinflussen, bis der Nutzer "Anwenden" klickt.
        Private Sub ScheduleToolPreviewUpdate()
            If Not _livePreviewEnabled Then Return
            _previewPending = True
            StatusText = "Vorschau wird aktualisiert..."
            _previewTimer.Stop()
            _previewTimer.Start()
        End Sub

        Private Function IsTextualAnnotationKind(kind As String) As Boolean
            Dim k = NormalizeAnnotationKind(kind)
            Return k = "Text" OrElse k = "Watermark"
        End Function

        ''' Ob das selektierte Text-/Wasserzeichen-Objekt aktuell per Live-Overlay dargestellt wird
        ''' und deshalb im gebackenen Vorschaubild ausgeblendet werden muss (siehe GetCurrentAdjustments).
        Private Function ComputesOverlayHidesSelection() As Boolean
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return False
            If _currentTool <> EditorTool.Text AndAlso _currentTool <> EditorTool.Insert Then Return False
            Return IsTextualAnnotationKind(_annotations(_selectedAnnotationIndex).Kind)
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
            StatusText = "Vorschau bereit"
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
            If oldCts IsNot Nothing Then
                oldCts.Cancel()
                oldCts.Dispose()
            End If
        End Sub

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

        Private Async Function ApplyCropAsync() As Task
            If Not HasCropChanges Then Return
            PushUndo()
            _appliedCropLeft = _cropLeft
            _appliedCropTop = _cropTop
            _appliedCropRight = _cropRight
            _appliedCropBottom = _cropBottom
            _hasChanges = True
            RaiseResetButtonStateChanged()
            Await UpdatePreviewAsync()
            CurrentTool = EditorTool.Adjust
        End Function

        Private Async Function ApplyResizeAsync() As Task
            If Not HasImageResizeChanges Then Return
            PushUndo()
            _appliedResizeWidth = _resizeWidth
            _appliedResizeHeight = _resizeHeight
            _hasChanges = True
            RaiseResetButtonStateChanged()
            Await UpdatePreviewAsync()
            CurrentTool = EditorTool.Adjust
        End Function

        Private Async Function ApplyCanvasAsync() As Task
            If Not HasCanvasSizeChanges Then Return
            PushUndo()
            _appliedCanvasWidth = _canvasWidth
            _appliedCanvasHeight = _canvasHeight
            _hasChanges = True
            RaiseResetButtonStateChanged()
            Await UpdatePreviewAsync()
            CurrentTool = EditorTool.Adjust
        End Function

        Private Async Function ApplyTransformAsync() As Task
            If Not HasTransformChanges Then Return
            PushUndo()
            _appliedRotationDegrees = _rotationDegrees
            _appliedStraightenDegrees = _straightenDegrees
            _appliedStraightenExpandCanvas = _straightenExpandCanvas
            _appliedFlipH = _flipH
            _appliedFlipV = _flipV
            _hasChanges = True
            RaiseResetButtonStateChanged()
            Await UpdatePreviewAsync()
            CurrentTool = EditorTool.Adjust
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
            Dim oldCts = Interlocked.Exchange(_previewRenderCts, cts)
            If oldCts IsNot Nothing Then
                oldCts.Cancel()
                oldCts.Dispose()
            End If

            Try
                StatusText = "Vorschau wird berechnet…"
                Dim needsComparison = _showBeforeImage
                Dim result = Await Task.Run(Function()
                                                cts.Token.ThrowIfCancellationRequested()
                                                Dim previewBmp = ImageProcessor.ApplyAdjustments(previewSource, adj)
                                                cts.Token.ThrowIfCancellationRequested()
                                                ' Das Vorher/Nachher-Vergleichsbild wird nur berechnet, wenn der
                                                ' Vorher/Nachher-Regler gerade sichtbar ist (ShowBeforeImage) - sonst
                                                ' wäre das bei jedem einzelnen Live-Vorschau-Frame verschwendete Arbeit.
                                                Dim comparisonBmp As Bitmap = Nothing
                                                If needsComparison Then
                                                    comparisonBmp = ImageProcessor.ApplyGeometryAdjustments(previewSource, adj)
                                                End If
                                                Return New PreviewRenderResult(previewBmp, comparisonBmp)
                                            End Function, cts.Token)

                If cts.IsCancellationRequested OrElse requestId <> _previewRequestId Then
                    result.Dispose()
                    Return
                End If

                PreviewImage = result.Preview
                ComparisonImage = result.Comparison
                _previewPending = False
                StatusText = "Vorschau bereit"
                result.Preview = Nothing
                result.Comparison = Nothing
                result.Dispose()
            Catch ex As OperationCanceledException
            Catch ex As Exception
                StatusText = "Vorschau-Fehler: " & ex.Message
                LogPreviewError(ex)
            Finally
                If ReferenceEquals(_previewRenderCts, cts) Then
                    _previewRenderCts = Nothing
                End If
                cts.Dispose()
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
            If Not saveAs AndAlso IsCurrentImageRaw Then Return False
            Dim targetPath = _currentImagePath
            Dim targetQuality = SaveQuality
            If saveAs Then
                Dim dir = IO.Path.GetDirectoryName(_currentImagePath)
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
                targetPath = IO.Path.Combine(dir, cleanBaseName & saveAsResult.Extension)
                targetQuality = saveAsResult.JpgQuality
            End If

            Dim errorMessage As String = Nothing
            Try
                StatusText = LocalizationService.T("Wird gespeichert…")
                Dim adj = GetCurrentAdjustments()
                Dim ok = ImageProcessor.SaveImage(_currentImagePath, targetPath, adj, targetQuality)
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
            Return Await SaveImageAsync(IsCurrentImageRaw)
        End Function

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
                .Vignette = CSng(_vignette),
                .Grain = CSng(_grain),
                .BorderSize = CSng(_borderSize),
                .BorderColor = _borderColor,
                .Clarity = CSng(_clarity),
                .CurveRgbPoints = PointsToCurveString(_curveRgbPoints),
                .CurveRedPoints = PointsToCurveString(_curveRedPoints),
                .CurveGreenPoints = PointsToCurveString(_curveGreenPoints),
                .CurveBluePoints = PointsToCurveString(_curveBluePoints),
                .CurveLuminancePoints = PointsToCurveString(_curveLuminancePoints),
                .RedHue = CSng(_redHue),
                .RedSaturation = CSng(_redSaturation),
                .OrangeHue = CSng(_orangeHue),
                .OrangeSaturation = CSng(_orangeSaturation),
                .YellowHue = CSng(_yellowHue),
                .YellowSaturation = CSng(_yellowSaturation),
                .GreenHue = CSng(_greenHue),
                .GreenSaturation = CSng(_greenSaturation),
                .AquaHue = CSng(_aquaHue),
                .AquaSaturation = CSng(_aquaSaturation),
                .BlueHue = CSng(_blueHue),
                .BlueSaturation = CSng(_blueSaturation),
                .PurpleHue = CSng(_purpleHue),
                .PurpleSaturation = CSng(_purpleSaturation),
                .MagentaHue = CSng(_magentaHue),
                .MagentaSaturation = CSng(_magentaSaturation),
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
                .FilterPreset = _filterPreset,
                .FilterStrength = CSng(_filterStrength),
                .RetouchSpots = _retouchSpots.Select(Function(s) s.Clone()).ToList(),
                .Annotations = _annotations.Select(Function(a) a.Clone()).ToList()
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
        ''' der Farbrad-Bandmischer (HslWheelPicker + ActiveHslHue/ActiveHslSaturation) zeigt/bearbeitet
        ''' immer nur das GERADE per Rad ausgewählte Band, muss also bei JEDER Bandänderung benachrichtigt
        ''' werden, egal ob die Änderung von diesem Reglerpaar selbst oder direkt (Undo/Reset) kommt.
        Private Function SetUndoableHslDouble(ByRef field As Double, value As Double, propertyName As String) As Boolean
            Dim changed = SetUndoableDouble(field, value, propertyName)
            If changed Then
                Me.RaisePropertyChanged(NameOf(ActiveHslHue))
                Me.RaisePropertyChanged(NameOf(ActiveHslSaturation))
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
            Me.RaisePropertyChanged(NameOf(HasResizeChanges))
            Me.RaisePropertyChanged(NameOf(HasImageResizeChanges))
            Me.RaisePropertyChanged(NameOf(HasCanvasSizeChanges))
            Me.RaisePropertyChanged(NameOf(HasLightChanges))
            Me.RaisePropertyChanged(NameOf(HasColorChanges))
            Me.RaisePropertyChanged(NameOf(HasDetailChanges))
            Me.RaisePropertyChanged(NameOf(HasEffectsChanges))
            Me.RaisePropertyChanged(NameOf(HasRetouchChanges))
            Me.RaisePropertyChanged(NameOf(HasCurveChanges))
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
                        SetCropValues(_appliedCropLeft, _appliedCropTop, _appliedCropRight, _appliedCropBottom)
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
                Case NameOf(Vignette), NameOf(Grain), NameOf(BorderSize), NameOf(BorderColor)
                    Return "Effekte"
                Case NameOf(FilterPreset)
                    Return "Filter"
                Case NameOf(FilterStrength)
                    Return "Filterstärke"
                Case NameOf(WhiteBalance), NameOf(Temperature), NameOf(Tint)
                    Return "Weißabgleich"
                Case Else
                    Return "Anpassung"
            End Select
        End Function

        Private Sub PushUndo()
            ResetUndoCapture()
            _undoStack.Push(GetCurrentAdjustments())
            _redoStack.Clear()
            AddHistoryEntry(BuildHistoryLabel(GetCurrentAdjustments()))
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
            _vignette = adj.Vignette
            _grain = adj.Grain
            _borderSize = adj.BorderSize
            _borderColor = If(String.IsNullOrWhiteSpace(adj.BorderColor), "#FFFFFFFF", adj.BorderColor)
            _clarity = adj.Clarity
            LoadCurvePointsFromString(_curveRgbPoints, adj.CurveRgbPoints)
            LoadCurvePointsFromString(_curveRedPoints, adj.CurveRedPoints)
            LoadCurvePointsFromString(_curveGreenPoints, adj.CurveGreenPoints)
            LoadCurvePointsFromString(_curveBluePoints, adj.CurveBluePoints)
            LoadCurvePointsFromString(_curveLuminancePoints, adj.CurveLuminancePoints)
            _redHue = adj.RedHue
            _redSaturation = adj.RedSaturation
            _orangeHue = adj.OrangeHue
            _orangeSaturation = adj.OrangeSaturation
            _yellowHue = adj.YellowHue
            _yellowSaturation = adj.YellowSaturation
            _greenHue = adj.GreenHue
            _greenSaturation = adj.GreenSaturation
            _aquaHue = adj.AquaHue
            _aquaSaturation = adj.AquaSaturation
            _blueHue = adj.BlueHue
            _blueSaturation = adj.BlueSaturation
            _purpleHue = adj.PurpleHue
            _purpleSaturation = adj.PurpleSaturation
            _magentaHue = adj.MagentaHue
            _magentaSaturation = adj.MagentaSaturation
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
            _cropLeft = adj.CropLeftPercent
            _cropTop = adj.CropTopPercent
            _cropRight = adj.CropRightPercent
            _cropBottom = adj.CropBottomPercent
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
            Me.RaisePropertyChanged(NameOf(Vignette))
            Me.RaisePropertyChanged(NameOf(Grain))
            Me.RaisePropertyChanged(NameOf(BorderSize))
            Me.RaisePropertyChanged(NameOf(BorderColor))
            Me.RaisePropertyChanged(NameOf(BorderColorValue))
            Me.RaisePropertyChanged(NameOf(BorderColorBrush))
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
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationIndex))
            Me.RaisePropertyChanged(NameOf(HasSelectedAnnotation))
            RaiseCropPropertiesChanged()
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub DoRotate(degrees As Integer)
            _rotationDegrees = ((_rotationDegrees + degrees) Mod 360 + 360) Mod 360
            RaiseResetButtonStateChanged()
            UpdatePreview()
        End Sub

        Private Sub DoFlipH()
            _flipH = Not _flipH
            RaiseResetButtonStateChanged()
            UpdatePreview()
        End Sub

        Private Sub DoFlipV()
            _flipV = Not _flipV
            RaiseResetButtonStateChanged()
            UpdatePreview()
        End Sub

        Private Sub ResetAdjustmentsInternal()
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
            _exposure = 0
            _sharpness = 0
            _noiseReduction = 0
            _noiseReductionMethod = NoiseReductionMethod.Gaussian
            _vignette = 0
            _grain = 0
            _borderSize = 0
            _borderColor = "#FFFFFFFF"
            _clarity = 0
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
            _retouchSpots.Clear()
            _annotations.Clear()
            _selectedAnnotationIndex = -1
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
            Me.RaisePropertyChanged(NameOf(Vignette))
            Me.RaisePropertyChanged(NameOf(Grain))
            Me.RaisePropertyChanged(NameOf(BorderSize))
            Me.RaisePropertyChanged(NameOf(BorderColor))
            Me.RaisePropertyChanged(NameOf(BorderColorValue))
            Me.RaisePropertyChanged(NameOf(BorderColorBrush))
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
            RaiseCropPropertiesChanged()
            RaiseResetButtonStateChanged()
            PreviewImage = Nothing
            ComparisonImage = Nothing
            Me.RaisePropertyChanged(NameOf(DisplayImage))
            Me.RaisePropertyChanged(NameOf(BeforeDisplayImage))
        End Sub

        Private Shared Function ClampPercent(value As Double) As Double
            Return Math.Max(0, Math.Min(95, value))
        End Function

        Private Shared Function ClampCropEdge(value As Double, oppositeEdge As Double) As Double
            Return Math.Max(0, Math.Min(95 - ClampPercent(oppositeEdge), ClampPercent(value)))
        End Function

        Private Sub AfterCropChanged()
            If _lockResizeAspect AndAlso _resizeWidth > 0 Then
                SyncResizeHeightFromWidth()
            End If
            RaiseCropPropertiesChanged()
        End Sub

        Private Sub RaiseCropPropertiesChanged()
            Me.RaisePropertyChanged(NameOf(CropSummary))
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

        Private Function EstimateTextAnnotationSizePercent(text As String, fontSizePercent As Double, fontFamily As String) As (WidthPercent As Double, HeightPercent As Double)
            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return (_annotationWidthPercent, _annotationHeightPercent)

            Dim content = If(text, "").Trim()
            If content.Length = 0 Then content = "Text"

            Dim fontSizePx = Math.Max(8.0, baseHeight * Math.Max(0.2, fontSizePercent) / 100.0)
            Using paint = New SKPaint With {
                .TextSize = CSng(fontSizePx),
                .Typeface = SKTypeface.FromFamilyName(If(String.IsNullOrWhiteSpace(fontFamily), "Arial", fontFamily)),
                .IsAntialias = True
            }
                Dim maxLineWidth As Single = 0
                Dim lineCount As Integer = 0
                For Each line In content.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(ControlChars.Lf)
                    lineCount += 1
                    Dim bounds As SKRect
                    paint.MeasureText(If(String.IsNullOrEmpty(line), " ", line), bounds)
                    maxLineWidth = Math.Max(maxLineWidth, bounds.Width)
                Next
                If lineCount = 0 Then lineCount = 1

                Dim widthPx = Math.Max(72.0, maxLineWidth + fontSizePx * 1.1)
                Dim heightPx = Math.Max(fontSizePx * 1.35 * lineCount, fontSizePx * 1.7)
                Dim widthPercent = widthPx / baseWidth * 100.0
                Dim heightPercent = heightPx / baseHeight * 100.0
                Return (Math.Max(5.0, Math.Min(60.0, widthPercent)),
                        Math.Max(4.0, Math.Min(60.0, heightPercent)))
            End Using
        End Function

        Private Sub UpdatePendingTextAnnotationSize()
            If HasSelectedAnnotation Then
                UpdateSelectedTextAnnotationSizeIfNeeded()
                Return
            End If
            If Not HasPendingInsertKind Then Return
            Dim kind = NormalizeAnnotationKind(_pendingInsertKind)
            If kind <> "Text" AndAlso kind <> "Watermark" Then Return

            Dim size = EstimateTextAnnotationSizePercent(_annotationText, _annotationFontSize, _annotationFontFamily)
            _annotationWidthPercent = size.WidthPercent
            _annotationHeightPercent = size.HeightPercent
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
        End Sub

        ''' Vergrößert (nie automatisch verkleinert) die gespeicherte Box eines bereits platzierten
        ''' Text-/Wasserzeichen-Objekts, wenn der aktuelle Textinhalt/Schriftgrad größer ist als die
        ''' zuletzt gespeicherte Größe - damit Hit-Test und gebackenes Rendering nie hinter dem
        ''' sichtbaren Text zurückbleiben. Während LoadSelectedAnnotationIntoEditor die Felder aus dem
        ''' Modell befüllt, stehen hier noch die Werte des vorher selektierten Objekts - daher kein
        ''' Aufruf während _isLoadingAnnotation (die echte Größe wird dort direkt gesetzt).
        Private Sub UpdateSelectedTextAnnotationSizeIfNeeded()
            If _isLoadingAnnotation Then Return
            If Not IsTextualAnnotationKind(SelectedAnnotationKind) Then Return
            Dim size = EstimateTextAnnotationSizePercent(_annotationText, _annotationFontSize, _annotationFontFamily)
            Dim newWidth = Math.Min(90.0, Math.Max(_annotationWidthPercent, size.WidthPercent))
            Dim newHeight = Math.Min(90.0, Math.Max(_annotationHeightPercent, size.HeightPercent))
            If newWidth > _annotationWidthPercent OrElse newHeight > _annotationHeightPercent Then
                _annotationWidthPercent = newWidth
                _annotationHeightPercent = newHeight
                Me.RaisePropertyChanged(NameOf(AnnotationWidthPercent))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightPercent))
                Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
            End If
        End Sub

        Private Function GetCroppedWidth() As Integer
            Dim width = GetBaseWidth()
            If width <= 0 Then Return 0
            Dim remaining = 1.0 - (_cropLeft + _cropRight) / 100.0
            Return Math.Max(1, CInt(Math.Round(width * Math.Max(0.01, remaining))))
        End Function

        Private Function GetCroppedHeight() As Integer
            Dim height = GetBaseHeight()
            If height <= 0 Then Return 0
            Dim remaining = 1.0 - (_cropTop + _cropBottom) / 100.0
            Return Math.Max(1, CInt(Math.Round(height * Math.Max(0.01, remaining))))
        End Function

        Private Sub SetCropValues(left As Double, top As Double, right As Double, bottom As Double)
            _cropLeft = ClampPercent(left)
            _cropRight = ClampCropEdge(right, _cropLeft)
            _cropTop = ClampPercent(top)
            _cropBottom = ClampCropEdge(bottom, _cropTop)
            Me.RaisePropertyChanged(NameOf(CropLeft))
            Me.RaisePropertyChanged(NameOf(CropTop))
            Me.RaisePropertyChanged(NameOf(CropRight))
            Me.RaisePropertyChanged(NameOf(CropBottom))
            AfterCropChanged()
            RaiseResetButtonStateChanged()
        End Sub

        Private Sub SetCropSizePixels(widthPixels As Integer, heightPixels As Integer)
            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return

            Dim leftPx = CInt(Math.Round(baseWidth * _cropLeft / 100.0))
            Dim topPx = CInt(Math.Round(baseHeight * _cropTop / 100.0))
            Dim clampedWidth = Math.Max(1, Math.Min(Math.Max(1, widthPixels), Math.Max(1, baseWidth - leftPx)))
            Dim clampedHeight = Math.Max(1, Math.Min(Math.Max(1, heightPixels), Math.Max(1, baseHeight - topPx)))
            Dim rightPx = Math.Max(0, baseWidth - leftPx - clampedWidth)
            Dim bottomPx = Math.Max(0, baseHeight - topPx - clampedHeight)

            SetCropValues(leftPx / CDbl(baseWidth) * 100.0,
                          topPx / CDbl(baseHeight) * 100.0,
                          rightPx / CDbl(baseWidth) * 100.0,
                          bottomPx / CDbl(baseHeight) * 100.0)
        End Sub

        Private Sub ApplyCropPreset(preset As String)
            Dim width = GetBaseWidth()
            Dim height = GetBaseHeight()
            If width <= 0 OrElse height <= 0 Then Return

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

        Private Sub ApplyCenteredAspectCrop(targetAspect As Double)
            Dim width = CDbl(GetBaseWidth())
            Dim height = CDbl(GetBaseHeight())
            If width <= 0 OrElse height <= 0 OrElse targetAspect <= 0 Then Return

            Dim currentAspect = width / height
            If Math.Abs(currentAspect - targetAspect) < 0.001 Then
                SetCropValues(0, 0, 0, 0)
                Return
            End If

            If currentAspect > targetAspect Then
                Dim targetWidth = height * targetAspect
                Dim eachSide = (width - targetWidth) / width * 50.0
                SetCropValues(eachSide, 0, eachSide, 0)
            Else
                Dim targetHeight = width / targetAspect
                Dim eachSide = (height - targetHeight) / height * 50.0
                SetCropValues(0, eachSide, 0, eachSide)
            End If
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

        Private Sub AddTextAnnotation()
            AddTextAnnotationAt(_annotationXPercent, _annotationYPercent)
        End Sub

        Private Sub AddAnnotation(kind As String)
            AddAnnotationAt(kind, _annotationXPercent, _annotationYPercent)
        End Sub

        Public Sub AddTextAnnotationAt(xPercent As Double, yPercent As Double)
            AddAnnotationAt("Text", xPercent, yPercent)
        End Sub

        Public Sub AddAnnotationAt(kind As String, xPercent As Double, yPercent As Double)
            PushUndo()
            Dim normalizedKind = NormalizeAnnotationKind(kind)
            Dim isShape = IsCustomShapeKind(normalizedKind)
            Dim width = If(normalizedKind = "Line" OrElse normalizedKind = "Arrow", 30.0, If(normalizedKind = "QR" OrElse normalizedKind = "Image", 22.0, If(isShape, 22.0, _annotationWidthPercent)))
            Dim height = If(normalizedKind = "Line" OrElse normalizedKind = "Arrow", 16.0, If(normalizedKind = "QR" OrElse normalizedKind = "Symbol" OrElse normalizedKind = "Image", 22.0, If(isShape, 22.0, _annotationHeightPercent)))
            Dim x = Math.Max(-width + 1, Math.Min(100 - 1, xPercent))
            Dim y = Math.Max(-height + 1, Math.Min(100 - 1, yPercent))
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
                .ImagePath = If(normalizedKind = "Svg", ExtractSvgIconPath(kind), ""),
                .XPercent = CSng(x),
                .YPercent = CSng(y),
                .WidthPercent = CSng(width),
                .HeightPercent = CSng(height),
                .FillColor = fill,
                .StrokeColor = stroke,
                .StrokeWidth = CSng(strokeWidth),
                .FontSizePercent = CSng(If(normalizedKind = "Watermark", Math.Max(8, _annotationFontSize), _annotationFontSize)),
                .FontFamily = _annotationFontFamily,
                .Opacity = CSng(_annotationOpacity),
                .RotationDegrees = CSng(_annotationRotation),
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
                .XPercent = CSng(x),
                .YPercent = CSng(y),
                .WidthPercent = CSng(width),
                .HeightPercent = CSng(height),
                .FillColor = "#00FFFFFF",
                .StrokeColor = _annotationStrokeColor,
                .StrokeWidth = CSng(_annotationStrokeWidth),
                .FontSizePercent = CSng(_annotationFontSize),
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
                Case "Brush", "Eraser" : Return EditorTool.Draw
                Case "SelectionFill", "SelectionImage" : Return EditorTool.Selection
                Case Else : Return EditorTool.Insert
            End Select
        End Function

        Private Shared Function NormalizeAnnotationKind(kind As String) As String
            Dim normalized = If(kind, "").Trim().ToLowerInvariant()
            If normalized.StartsWith("symbol:", StringComparison.OrdinalIgnoreCase) Then Return "Symbol"
            If normalized.StartsWith("svg:", StringComparison.OrdinalIgnoreCase) Then Return "Svg"
            Select Case normalized
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
            Dim minX = Math.Max(0, normalized.Min(Function(p) p.X))
            Dim minY = Math.Max(0, normalized.Min(Function(p) p.Y))
            Dim maxX = Math.Min(100, normalized.Max(Function(p) p.X))
            Dim maxY = Math.Min(100, normalized.Max(Function(p) p.Y))
            Dim pointText = String.Join(" ", normalized.Select(Function(p) $"{p.X.ToString("F3", Globalization.CultureInfo.InvariantCulture)},{p.Y.ToString("F3", Globalization.CultureInfo.InvariantCulture)}"))

            Dim expectedKind = If(isEraser, "Eraser", "Brush")
            Dim canAppend = _activeStrokeAnnotation IsNot Nothing AndAlso
                             _activeStrokeIsEraser = isEraser AndAlso
                             String.Equals(_activeStrokeAnnotation.Kind, expectedKind, StringComparison.OrdinalIgnoreCase) AndAlso
                             _annotations.Contains(_activeStrokeAnnotation)

            If canAppend Then
                Dim existing = _activeStrokeAnnotation
                Dim unionMinX = Math.Min(existing.XPercent, CSng(minX))
                Dim unionMinY = Math.Min(existing.YPercent, CSng(minY))
                Dim unionMaxX = Math.Max(existing.XPercent + existing.WidthPercent, CSng(maxX))
                Dim unionMaxY = Math.Max(existing.YPercent + existing.HeightPercent, CSng(maxY))
                existing.Text = existing.Text & ";" & pointText
                existing.XPercent = unionMinX
                existing.YPercent = unionMinY
                existing.WidthPercent = Math.Max(1, unionMaxX - unionMinX)
                existing.HeightPercent = Math.Max(1, unionMaxY - unionMinY)
            Else
                Dim width = Math.Max(1, maxX - minX)
                Dim height = Math.Max(1, maxY - minY)
                Dim newAnnotation = New ImageAnnotation With {
                    .Kind = expectedKind,
                    .Text = pointText,
                    .XPercent = CSng(minX),
                    .YPercent = CSng(minY),
                    .WidthPercent = CSng(width),
                    .HeightPercent = CSng(height),
                    .FillColor = _annotationFillColor,
                    .StrokeColor = _annotationStrokeColor,
                    .StrokeWidth = CSng(_brushSize),
                    .Opacity = CSng(_brushOpacity),
                    .HardnessPercent = CSng(_brushHardness),
                    .FontSizePercent = CSng(_annotationFontSize),
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
            copy.XPercent = CSng(Math.Min(99, copy.XPercent + 3))
            copy.YPercent = CSng(Math.Min(99, copy.YPercent + 3))
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

        Private Sub AlignSelectedAnnotation(target As String)
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return
            CaptureUndoState("LayerAlign")
            Select Case If(target, "").Trim().ToLowerInvariant()
                Case "left"
                    AnnotationXPercent = 0
                Case "center-x"
                    AnnotationXPercent = Math.Max(0, (100 - _annotationWidthPercent) / 2.0)
                Case "right"
                    AnnotationXPercent = Math.Max(0, 100 - _annotationWidthPercent)
                Case "top"
                    AnnotationYPercent = 0
                Case "center-y"
                    AnnotationYPercent = Math.Max(0, (100 - _annotationHeightPercent) / 2.0)
                Case "bottom"
                    AnnotationYPercent = Math.Max(0, 100 - _annotationHeightPercent)
                Case "center"
                    AnnotationXPercent = Math.Max(0, (100 - _annotationWidthPercent) / 2.0)
                    AnnotationYPercent = Math.Max(0, (100 - _annotationHeightPercent) / 2.0)
                Case "safe-bottom-right"
                    AnnotationXPercent = Math.Max(0, 96 - _annotationWidthPercent)
                    AnnotationYPercent = Math.Max(0, 96 - _annotationHeightPercent)
                Case "safe-bottom-left"
                    AnnotationXPercent = 4
                    AnnotationYPercent = Math.Max(0, 96 - _annotationHeightPercent)
                Case "safe-top-right"
                    AnnotationXPercent = Math.Max(0, 96 - _annotationWidthPercent)
                    AnnotationYPercent = 4
                Case "safe-top-left"
                    AnnotationXPercent = 4
                    AnnotationYPercent = 4
            End Select
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
                If xPercent >= a.XPercent - hitSlopXPercent AndAlso xPercent <= a.XPercent + a.WidthPercent + hitSlopXPercent AndAlso
                   yPercent >= a.YPercent - hitSlopYPercent AndAlso yPercent <= a.YPercent + a.HeightPercent + hitSlopYPercent Then
                    Return i
                End If
            Next
            Return -1
        End Function

        Private Shared Function IsLayerTool(tool As EditorTool) As Boolean
            Return tool = EditorTool.Text OrElse tool = EditorTool.Draw OrElse tool = EditorTool.Geometry OrElse tool = EditorTool.Insert OrElse tool = EditorTool.Selection
        End Function

        Private Sub LoadSelectedAnnotationIntoEditor()
            _isLoadingAnnotation = True
            Try
                If _selectedAnnotationIndex >= 0 AndAlso _selectedAnnotationIndex < _annotations.Count Then
                    Dim a = _annotations(_selectedAnnotationIndex)
                    AnnotationText = a.Text
                    AnnotationFillColor = a.FillColor
                    AnnotationStrokeColor = a.StrokeColor
                    AnnotationStrokeWidth = a.StrokeWidth
                    AnnotationFontSize = a.FontSizePercent
                    AnnotationFontFamily = a.FontFamily
                    AnnotationOpacity = a.Opacity
                    AnnotationRotation = a.RotationDegrees
                    AnnotationIsVisible = a.IsVisible
                    AnnotationXPercent = a.XPercent
                    AnnotationYPercent = a.YPercent
                    AnnotationWidthPercent = a.WidthPercent
                    AnnotationHeightPercent = a.HeightPercent
                    AnnotationFillKind = a.FillKind
                    AnnotationFillColor2 = a.FillColor2
                    AnnotationGradientAngleDegrees = a.GradientAngleDegrees
                    AnnotationGradientInverted = a.GradientInverted
                    AnnotationShadowEnabled = a.ShadowEnabled
                    AnnotationShadowOffsetX = a.ShadowOffsetXPercent
                    AnnotationShadowOffsetY = a.ShadowOffsetYPercent
                    AnnotationShadowBlur = a.ShadowBlur
                    AnnotationShadowStrength = a.ShadowStrength
                    AnnotationShadowColor = a.ShadowColor
                    AnnotationGlowEnabled = a.GlowEnabled
                    AnnotationGlowBlur = a.GlowBlur
                    AnnotationGlowStrength = a.GlowStrength
                    AnnotationGlowColor = a.GlowColor
                End If
            Finally
                _isLoadingAnnotation = False
            End Try
        End Sub

        Private Sub SyncSelectedAnnotation()
            If _isLoadingAnnotation Then Return
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return
            CaptureUndoState("TextAnnotation")
            Dim a = _annotations(_selectedAnnotationIndex)
            ' Bei Pinsel-/Radiergummi-Ebenen enthält a.Text die per ";" getrennten Strichpfade
            ' (siehe AddBrushStroke), die dort direkt am Modell wachsen, ohne je durch
            ' _annotationText zu laufen. Ein Rücksynchronisieren von _annotationText (das nur die
            ' beim Anlegen der Ebene geladene Momentaufnahme des ERSTEN Strichs enthält) würde
            ' spätere Striche verwerfen, z.B. beim Ein-/Ausblenden der Ebene (AnnotationIsVisible).
            Dim normalizedKind = NormalizeAnnotationKind(a.Kind)
            If normalizedKind <> "Brush" AndAlso normalizedKind <> "Eraser" Then
                a.Text = _annotationText
            End If
            a.FillColor = _annotationFillColor
            a.StrokeColor = _annotationStrokeColor
            a.StrokeWidth = CSng(_annotationStrokeWidth)
            a.FontSizePercent = CSng(_annotationFontSize)
            a.FontFamily = _annotationFontFamily
            a.Opacity = CSng(_annotationOpacity)
            a.RotationDegrees = CSng(_annotationRotation)
            a.IsVisible = _annotationIsVisible
            a.XPercent = CSng(_annotationXPercent)
            a.YPercent = CSng(_annotationYPercent)
            a.WidthPercent = CSng(_annotationWidthPercent)
            a.HeightPercent = CSng(_annotationHeightPercent)
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
            a.GlowEnabled = _annotationGlowEnabled
            a.GlowBlur = CSng(_annotationGlowBlur)
            a.GlowStrength = CSng(_annotationGlowStrength)
            a.GlowColor = _annotationGlowColor
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationText))
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
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

        Public Sub SetCropPercentages(left As Double, top As Double, right As Double, bottom As Double)
            SetCropValues(left, top, right, bottom)
        End Sub

        Public Sub AddRetouchSpot(xPercent As Double, yPercent As Double, Optional captureUndo As Boolean = True)
            If captureUndo Then PushUndo()
            _retouchSpots.Add(New RetouchSpot With {
                .XPercent = CSng(Math.Max(0, Math.Min(100, xPercent))),
                .YPercent = CSng(Math.Max(0, Math.Min(100, yPercent))),
                .RadiusPercent = CSng(_retouchRadius)
            })
            AddHistoryEntry("Verwischen")
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

        Private Sub ResetDetailInternal()
            _clarity = 0
            _sharpness = 0
            _noiseReduction = 0
            _noiseReductionMethod = NoiseReductionMethod.Gaussian
            RaiseDetailPropertiesChanged()
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub ResetEffectsInternal()
            _vignette = 0
            _grain = 0
            _borderSize = 0
            _borderColor = "#FFFFFFFF"
            Me.RaisePropertyChanged(NameOf(Vignette))
            Me.RaisePropertyChanged(NameOf(Grain))
            Me.RaisePropertyChanged(NameOf(BorderSize))
            Me.RaisePropertyChanged(NameOf(BorderColor))
            Me.RaisePropertyChanged(NameOf(BorderColorValue))
            Me.RaisePropertyChanged(NameOf(BorderColorBrush))
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub ResetRetouchInternal()
            _retouchRadius = 2.0
            _retouchSpots.Clear()
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

        Private Shared Function IsIdentityCurvePoints(points As ObservableCollection(Of Avalonia.Point)) As Boolean
            Return points.Count = 2 AndAlso points(0).X = 0 AndAlso points(0).Y = 0 AndAlso points(1).X = 255 AndAlso points(1).Y = 255
        End Function

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
            _redHue = 0 : _redSaturation = 0
            _orangeHue = 0 : _orangeSaturation = 0
            _yellowHue = 0 : _yellowSaturation = 0
            _greenHue = 0 : _greenSaturation = 0
            _aquaHue = 0 : _aquaSaturation = 0
            _blueHue = 0 : _blueSaturation = 0
            _purpleHue = 0 : _purpleSaturation = 0
            _magentaHue = 0 : _magentaSaturation = 0
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
            RaiseResetButtonStateChanged()
        End Sub

        Private Sub RaiseExtendedAdjustmentProperties()
            For Each name In {
                NameOf(Clarity), NameOf(ActiveCurvePoints), NameOf(ActiveCurveHistogramCounts),
                NameOf(RedHue), NameOf(RedSaturation), NameOf(OrangeHue), NameOf(OrangeSaturation),
                NameOf(YellowHue), NameOf(YellowSaturation), NameOf(GreenHue), NameOf(GreenSaturation),
                NameOf(AquaHue), NameOf(AquaSaturation), NameOf(BlueHue), NameOf(BlueSaturation),
                NameOf(PurpleHue), NameOf(PurpleSaturation), NameOf(MagentaHue), NameOf(MagentaSaturation),
                NameOf(ActiveHslHue), NameOf(ActiveHslSaturation),
                NameOf(StraightenDegrees), NameOf(StraightenExpandCanvas)}
                Me.RaisePropertyChanged(name)
            Next
            RaiseResetButtonStateChanged()
        End Sub

        Private Sub RaiseToolContextProperties()
            Me.RaisePropertyChanged(NameOf(ShowHistogramAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowCropAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowResizeAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowLightAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowColorAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowDetailAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowFrameAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowFilterAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowRetouchAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowSelectionAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowDrawControls))
            Me.RaisePropertyChanged(NameOf(ShowLayerToolOptions))
            Me.RaisePropertyChanged(NameOf(ShowTextInsertControls))
            Me.RaisePropertyChanged(NameOf(ShowGeometryControls))
            Me.RaisePropertyChanged(NameOf(ShowTransformAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowExportAdjustments))
            Me.RaisePropertyChanged(NameOf(SelectedPaintMode))
            Me.RaisePropertyChanged(NameOf(IsPaintToolSelected))
        End Sub

        Private Sub SetPaintMode(mode As String)
            Dim normalized = If(mode, "").Trim().ToLowerInvariant()
            Select Case normalized
                Case "brush", "pinsel"
                    CurrentTool = EditorTool.Draw
                    IsEraserMode = False
                Case "eraser", "radiergummi"
                    CurrentTool = EditorTool.Draw
                    IsEraserMode = True
                Case "blur", "verwischen"
                    CurrentTool = EditorTool.Retouch
                Case Else
                    CurrentTool = EditorTool.Draw
                    IsEraserMode = False
            End Select
            Me.RaisePropertyChanged(NameOf(SelectedPaintMode))
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
            If adj.Clarity <> 0 OrElse adj.Sharpness <> 0 OrElse adj.NoiseReduction <> 0 Then Return "Details"
            If adj.Vignette <> 0 OrElse adj.Grain <> 0 OrElse adj.BorderSize <> 0 Then Return "Effekte"
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
                Case Else
                    SelectedInfoTab = InfoSidebarTab.General
            End Select
        End Sub

        Private Sub RaiseInfoTabStateChanged()
            Me.RaisePropertyChanged(NameOf(IsInfoTabGeneral))
            Me.RaisePropertyChanged(NameOf(IsInfoTabExif))
            Me.RaisePropertyChanged(NameOf(IsInfoTabIptc))
            Me.RaisePropertyChanged(NameOf(IsInfoTabXmp))
        End Sub

        Private Async Sub ExportImage()
            If String.IsNullOrEmpty(_currentImagePath) Then Return
            Dim dir = IO.Path.GetDirectoryName(_currentImagePath)
            Dim baseName = IO.Path.GetFileNameWithoutExtension(_currentImagePath)
            Dim proposedName = $"{baseName}_export"
            Dim saveAsResult = Await _mainVm.ShowSaveAsAsync("Exportieren",
                                                             "Dateiname eingeben",
                                                             proposedName,
                                                             ExportFormat,
                                                             SaveQuality,
                                                             "Exportieren",
                                                             "Abbrechen")
            If saveAsResult Is Nothing OrElse String.IsNullOrWhiteSpace(saveAsResult.BaseName) Then Return

            Dim cleanBaseName = IO.Path.GetFileNameWithoutExtension(saveAsResult.BaseName.Trim())
            If HasInvalidFileNameChars(cleanBaseName) Then
                Await _mainVm.ShowMessageAsync("Export fehlgeschlagen", "Der Dateiname enthält ungültige Zeichen.")
                Return
            End If

            ExportFormat = saveAsResult.Format
            Dim targetPath = IO.Path.Combine(dir, cleanBaseName & saveAsResult.Extension)
            Dim ok = ImageProcessor.SaveImage(_currentImagePath, targetPath, GetCurrentAdjustments(), saveAsResult.JpgQuality)
            StatusText = If(ok,
                            $"{LocalizationService.T("Exportiert:")} {IO.Path.GetFileName(targetPath)}",
                            LocalizationService.T("Export fehlgeschlagen"))
        End Sub

        Private Sub SaveRecipe()
            If String.IsNullOrEmpty(_currentImagePath) Then Return
            Try
                Dim adj = GetCurrentAdjustments()
                Dim lines = New List(Of String) From {
                    "FerrumPixEdit=1",
                    $"Exposure={adj.Exposure}",
                    $"Brightness={adj.Brightness}",
                    $"Contrast={adj.Contrast}",
                    $"Saturation={adj.Saturation}",
                    $"Vibrance={adj.Vibrance}",
                    $"Highlights={adj.Highlights}",
                    $"Shadows={adj.ShadowsLevel}",
                    $"Whites={adj.Whites}",
                    $"Blacks={adj.Blacks}",
                    $"Temperature={adj.Temperature}",
                    $"Tint={adj.Tint}",
                    $"Sharpness={adj.Sharpness}",
                    $"NoiseReduction={adj.NoiseReduction}",
                    $"NoiseReductionMethod={adj.NoiseReductionMethod}",
                    $"Vignette={adj.Vignette}",
                    $"Grain={adj.Grain}",
                    $"BorderSize={adj.BorderSize}",
                    $"BorderColor={adj.BorderColor}",
                    $"Clarity={adj.Clarity}",
                    $"CurveRgbPoints={adj.CurveRgbPoints}",
                    $"CurveRedPoints={adj.CurveRedPoints}",
                    $"CurveGreenPoints={adj.CurveGreenPoints}",
                    $"CurveBluePoints={adj.CurveBluePoints}",
                    $"CurveLuminancePoints={adj.CurveLuminancePoints}",
                    $"RedHue={adj.RedHue}",
                    $"RedSaturation={adj.RedSaturation}",
                    $"OrangeHue={adj.OrangeHue}",
                    $"OrangeSaturation={adj.OrangeSaturation}",
                    $"YellowHue={adj.YellowHue}",
                    $"YellowSaturation={adj.YellowSaturation}",
                    $"GreenHue={adj.GreenHue}",
                    $"GreenSaturation={adj.GreenSaturation}",
                    $"AquaHue={adj.AquaHue}",
                    $"AquaSaturation={adj.AquaSaturation}",
                    $"BlueHue={adj.BlueHue}",
                    $"BlueSaturation={adj.BlueSaturation}",
                    $"PurpleHue={adj.PurpleHue}",
                    $"PurpleSaturation={adj.PurpleSaturation}",
                    $"MagentaHue={adj.MagentaHue}",
                    $"MagentaSaturation={adj.MagentaSaturation}",
                    $"RotationDegrees={adj.RotationDegrees}",
                    $"StraightenDegrees={adj.StraightenDegrees}",
                    $"StraightenExpandCanvas={adj.StraightenExpandCanvas}",
                    $"FlipHorizontal={adj.FlipHorizontal}",
                    $"FlipVertical={adj.FlipVertical}",
                    $"CropLeft={adj.CropLeftPercent}",
                    $"CropTop={adj.CropTopPercent}",
                    $"CropRight={adj.CropRightPercent}",
                    $"CropBottom={adj.CropBottomPercent}",
                    $"ResizeWidth={adj.ResizeWidth}",
                    $"ResizeHeight={adj.ResizeHeight}",
                    $"LockResizeAspect={adj.LockResizeAspect}",
                    $"ResizeInterpolation={adj.ResizeInterpolation}",
                    $"CanvasWidth={adj.CanvasWidth}",
                    $"CanvasHeight={adj.CanvasHeight}",
                    $"LockCanvasAspect={adj.LockCanvasAspect}",
                    $"CanvasAnchor={adj.CanvasAnchor}",
                    $"CanvasBackgroundColor={adj.CanvasBackgroundColor}",
                    $"FilterPreset={adj.FilterPreset}",
                    $"FilterStrength={adj.FilterStrength}"
                }
                File.WriteAllLines(RecipePath, lines)
                If adj.RetouchSpots IsNot Nothing Then
                    File.AppendAllLines(RecipePath, adj.RetouchSpots.Select(Function(s) $"RetouchSpot={s.XPercent};{s.YPercent};{s.RadiusPercent}"))
                End If
                If adj.Annotations IsNot Nothing Then
                    File.AppendAllLines(RecipePath, adj.Annotations.Select(Function(a) $"Annotation={Uri.EscapeDataString(a.Kind)};{Uri.EscapeDataString(a.Text)};{Uri.EscapeDataString(a.ImagePath)};{a.XPercent};{a.YPercent};{a.WidthPercent};{a.HeightPercent};{Uri.EscapeDataString(a.FillColor)};{Uri.EscapeDataString(a.StrokeColor)};{a.StrokeWidth};{a.FontSizePercent};{Uri.EscapeDataString(a.FontFamily)};{a.Opacity};{a.RotationDegrees};{a.IsVisible};{a.HardnessPercent};{Uri.EscapeDataString(a.FillKind)};{Uri.EscapeDataString(a.FillColor2)};{a.GradientAngleDegrees};{a.ShadowEnabled};{a.ShadowOffsetXPercent};{a.ShadowOffsetYPercent};{a.ShadowBlur};{Uri.EscapeDataString(a.ShadowColor)};{a.GlowEnabled};{a.GlowBlur};{Uri.EscapeDataString(a.GlowColor)};{a.GradientInverted};{a.GlowStrength};{a.ShadowStrength}"))
                End If
                StatusText = LocalizationService.T("Bearbeitungsrezept gespeichert")
            Catch ex As Exception
                StatusText = LocalizationService.T("Rezept konnte nicht gespeichert werden: ") & ex.Message
            End Try
        End Sub

        Private Sub LoadRecipe()
            If String.IsNullOrEmpty(RecipePath) OrElse Not File.Exists(RecipePath) Then
                StatusText = LocalizationService.T("Kein Bearbeitungsrezept gefunden")
                Return
            End If

            Try
                PushUndo()
                Dim adj = GetCurrentAdjustments()
                adj.RetouchSpots.Clear()
                For Each line In File.ReadAllLines(RecipePath)
                    Dim idx = line.IndexOf("="c)
                    If idx <= 0 Then Continue For
                    Dim key = line.Substring(0, idx)
                    Dim value = line.Substring(idx + 1)
                    ApplyRecipeValue(adj, key, value)
                Next
                ApplyAdjustments(adj)
                StatusText = LocalizationService.T("Bearbeitungsrezept geladen")
            Catch ex As Exception
                StatusText = LocalizationService.T("Rezept konnte nicht geladen werden: ") & ex.Message
            End Try
        End Sub

        Private Shared Function HasInvalidFileNameChars(fileName As String) As Boolean
            If String.IsNullOrEmpty(fileName) Then Return True
            If fileName.IndexOf(IO.Path.DirectorySeparatorChar) >= 0 OrElse
               fileName.IndexOf(IO.Path.AltDirectorySeparatorChar) >= 0 Then Return True

            Dim invalidChars = IO.Path.GetInvalidFileNameChars()
            Return invalidChars IsNot Nothing AndAlso invalidChars.Length > 0 AndAlso fileName.IndexOfAny(invalidChars) >= 0
        End Function

        Private Sub ApplyRecipeValue(adj As ImageAdjustments, key As String, value As String)
            Dim f As Single
            Dim i As Integer
            Dim b As Boolean
            Select Case key
                Case "RetouchSpot"
                    Dim parts = value.Split(";"c)
                    If parts.Length = 3 Then
                        Dim x As Single
                        Dim y As Single
                        Dim r As Single
                        If Single.TryParse(parts(0), x) AndAlso Single.TryParse(parts(1), y) AndAlso Single.TryParse(parts(2), r) Then
                            adj.RetouchSpots.Add(New RetouchSpot With {.XPercent = x, .YPercent = y, .RadiusPercent = r})
                        End If
                    End If
                Case "Annotation"
                    Dim parts = value.Split(";"c)
                    If parts.Length >= 16 Then
                        Dim x As Single
                        Dim y As Single
                        Dim w As Single
                        Dim h As Single
                        Dim strokeWidth As Single
                        Dim fontSize As Single
                        Dim opacity As Single = 100
                        Dim rotation As Single = 0
                        Dim isVisible As Boolean = True
                        Dim hardness As Single = 100
                        If Single.TryParse(parts(3), x) AndAlso Single.TryParse(parts(4), y) AndAlso
                           Single.TryParse(parts(5), w) AndAlso Single.TryParse(parts(6), h) AndAlso
                           Single.TryParse(parts(9), strokeWidth) AndAlso Single.TryParse(parts(10), fontSize) Then
                            Single.TryParse(parts(12), opacity)
                            Single.TryParse(parts(13), rotation)
                            Boolean.TryParse(parts(14), isVisible)
                            Single.TryParse(parts(15), hardness)
                            adj.Annotations.Add(New ImageAnnotation With {
                                .Kind = Uri.UnescapeDataString(parts(0)),
                                .Text = Uri.UnescapeDataString(parts(1)),
                                .ImagePath = Uri.UnescapeDataString(parts(2)),
                                .XPercent = x,
                                .YPercent = y,
                                .WidthPercent = w,
                                .HeightPercent = h,
                                .FillColor = Uri.UnescapeDataString(parts(7)),
                                .StrokeColor = Uri.UnescapeDataString(parts(8)),
                                .StrokeWidth = strokeWidth,
                                .FontSizePercent = fontSize,
                                .FontFamily = Uri.UnescapeDataString(parts(11)),
                                .Opacity = opacity,
                                .RotationDegrees = rotation,
                                .IsVisible = isVisible,
                                .HardnessPercent = hardness
                            })
                        End If
                    ElseIf parts.Length >= 15 Then
                        Dim x As Single
                        Dim y As Single
                        Dim w As Single
                        Dim h As Single
                        Dim strokeWidth As Single
                        Dim fontSize As Single
                        Dim opacity As Single = 100
                        Dim rotation As Single = 0
                        Dim isVisible As Boolean = True
                        Dim hardness As Single = 100
                        If Single.TryParse(parts(2), x) AndAlso Single.TryParse(parts(3), y) AndAlso
                           Single.TryParse(parts(4), w) AndAlso Single.TryParse(parts(5), h) AndAlso
                           Single.TryParse(parts(8), strokeWidth) AndAlso Single.TryParse(parts(9), fontSize) Then
                            Single.TryParse(parts(11), opacity)
                            Single.TryParse(parts(12), rotation)
                            Boolean.TryParse(parts(13), isVisible)
                            Single.TryParse(parts(14), hardness)
                            adj.Annotations.Add(New ImageAnnotation With {
                                .Kind = Uri.UnescapeDataString(parts(0)),
                                .Text = Uri.UnescapeDataString(parts(1)),
                                .ImagePath = "",
                                .XPercent = x,
                                .YPercent = y,
                                .WidthPercent = w,
                                .HeightPercent = h,
                                .FillColor = Uri.UnescapeDataString(parts(6)),
                                .StrokeColor = Uri.UnescapeDataString(parts(7)),
                                .StrokeWidth = strokeWidth,
                                .FontSizePercent = fontSize,
                                .FontFamily = Uri.UnescapeDataString(parts(10)),
                                .Opacity = opacity,
                                .RotationDegrees = rotation,
                                .IsVisible = isVisible,
                                .HardnessPercent = hardness
                            })
                        End If
                    End If
                    ' Ältere Rezepte enthalten die Fill-Verlauf-/Schatten-/Glow-Felder noch nicht -
                    ' in dem Fall bleiben die per Klassendefault gesetzten Werte (Solid, kein
                    ' Schatten/Glow) unverändert, siehe ImageAnnotation-Feld-Defaults.
                    If parts.Length >= 27 AndAlso adj.Annotations.Count > 0 Then
                        Dim extra = adj.Annotations(adj.Annotations.Count - 1)
                        Dim gradientAngle As Single
                        Dim shadowEnabled As Boolean
                        Dim shadowOffsetX As Single
                        Dim shadowOffsetY As Single
                        Dim shadowBlur As Single
                        Dim glowEnabled As Boolean
                        Dim glowBlur As Single
                        extra.FillKind = Uri.UnescapeDataString(parts(16))
                        extra.FillColor2 = Uri.UnescapeDataString(parts(17))
                        If Single.TryParse(parts(18), gradientAngle) Then extra.GradientAngleDegrees = gradientAngle
                        If Boolean.TryParse(parts(19), shadowEnabled) Then extra.ShadowEnabled = shadowEnabled
                        If Single.TryParse(parts(20), shadowOffsetX) Then extra.ShadowOffsetXPercent = shadowOffsetX
                        If Single.TryParse(parts(21), shadowOffsetY) Then extra.ShadowOffsetYPercent = shadowOffsetY
                        If Single.TryParse(parts(22), shadowBlur) Then extra.ShadowBlur = shadowBlur
                        extra.ShadowColor = Uri.UnescapeDataString(parts(23))
                        If Boolean.TryParse(parts(24), glowEnabled) Then extra.GlowEnabled = glowEnabled
                        If Single.TryParse(parts(25), glowBlur) Then extra.GlowBlur = glowBlur
                        extra.GlowColor = Uri.UnescapeDataString(parts(26))

                        ' Noch ältere Rezepte (aus der ersten Schatten/Glow-Version) kennen Invertieren/
                        ' Glow-Stärke noch nicht - Klassendefaults (nicht invertiert, volle Stärke) gelten dann.
                        If parts.Length >= 29 Then
                            Dim gradientInverted As Boolean
                            Dim glowStrength As Single
                            If Boolean.TryParse(parts(27), gradientInverted) Then extra.GradientInverted = gradientInverted
                            If Single.TryParse(parts(28), glowStrength) Then extra.GlowStrength = glowStrength
                        End If
                        If parts.Length >= 30 Then
                            Dim shadowStrength As Single
                            If Single.TryParse(parts(29), shadowStrength) Then extra.ShadowStrength = shadowStrength
                        End If
                    End If
                Case "Exposure" : If Single.TryParse(value, f) Then adj.Exposure = f
                Case "Brightness" : If Single.TryParse(value, f) Then adj.Brightness = f
                Case "Contrast" : If Single.TryParse(value, f) Then adj.Contrast = f
                Case "Saturation" : If Single.TryParse(value, f) Then adj.Saturation = f
                Case "Vibrance" : If Single.TryParse(value, f) Then adj.Vibrance = f
                Case "Highlights" : If Single.TryParse(value, f) Then adj.Highlights = f
                Case "Shadows" : If Single.TryParse(value, f) Then adj.ShadowsLevel = f
                Case "Whites" : If Single.TryParse(value, f) Then adj.Whites = f
                Case "Blacks" : If Single.TryParse(value, f) Then adj.Blacks = f
                Case "Temperature" : If Single.TryParse(value, f) Then adj.Temperature = f
                Case "Tint" : If Single.TryParse(value, f) Then adj.Tint = f
                Case "Sharpness" : If Single.TryParse(value, f) Then adj.Sharpness = f
                Case "NoiseReduction" : If Single.TryParse(value, f) Then adj.NoiseReduction = f
                Case "NoiseReductionMethod"
                    Dim nrMethod As NoiseReductionMethod
                    If [Enum].TryParse(value, nrMethod) Then adj.NoiseReductionMethod = nrMethod
                Case "Vignette" : If Single.TryParse(value, f) Then adj.Vignette = f
                Case "Grain" : If Single.TryParse(value, f) Then adj.Grain = f
                Case "BorderSize" : If Single.TryParse(value, f) Then adj.BorderSize = f
                Case "BorderColor" : adj.BorderColor = value
                Case "Clarity" : If Single.TryParse(value, f) Then adj.Clarity = f
                Case "CurveRgbPoints" : adj.CurveRgbPoints = value
                Case "CurveRedPoints" : adj.CurveRedPoints = value
                Case "CurveGreenPoints" : adj.CurveGreenPoints = value
                Case "CurveBluePoints" : adj.CurveBluePoints = value
                Case "CurveLuminancePoints" : adj.CurveLuminancePoints = value
                Case "RedHue" : If Single.TryParse(value, f) Then adj.RedHue = f
                Case "RedSaturation" : If Single.TryParse(value, f) Then adj.RedSaturation = f
                Case "OrangeHue" : If Single.TryParse(value, f) Then adj.OrangeHue = f
                Case "OrangeSaturation" : If Single.TryParse(value, f) Then adj.OrangeSaturation = f
                Case "YellowHue" : If Single.TryParse(value, f) Then adj.YellowHue = f
                Case "YellowSaturation" : If Single.TryParse(value, f) Then adj.YellowSaturation = f
                Case "GreenHue" : If Single.TryParse(value, f) Then adj.GreenHue = f
                Case "GreenSaturation" : If Single.TryParse(value, f) Then adj.GreenSaturation = f
                Case "AquaHue" : If Single.TryParse(value, f) Then adj.AquaHue = f
                Case "AquaSaturation" : If Single.TryParse(value, f) Then adj.AquaSaturation = f
                Case "BlueHue" : If Single.TryParse(value, f) Then adj.BlueHue = f
                Case "BlueSaturation" : If Single.TryParse(value, f) Then adj.BlueSaturation = f
                Case "PurpleHue" : If Single.TryParse(value, f) Then adj.PurpleHue = f
                Case "PurpleSaturation" : If Single.TryParse(value, f) Then adj.PurpleSaturation = f
                Case "MagentaHue" : If Single.TryParse(value, f) Then adj.MagentaHue = f
                Case "MagentaSaturation" : If Single.TryParse(value, f) Then adj.MagentaSaturation = f
                Case "RotationDegrees" : If Single.TryParse(value, f) Then adj.RotationDegrees = f
                Case "StraightenDegrees" : If Single.TryParse(value, f) Then adj.StraightenDegrees = f
                Case "StraightenExpandCanvas" : If Boolean.TryParse(value, b) Then adj.StraightenExpandCanvas = b
                Case "FlipHorizontal" : If Boolean.TryParse(value, b) Then adj.FlipHorizontal = b
                Case "FlipVertical" : If Boolean.TryParse(value, b) Then adj.FlipVertical = b
                Case "CropLeft" : If Single.TryParse(value, f) Then adj.CropLeftPercent = f
                Case "CropTop" : If Single.TryParse(value, f) Then adj.CropTopPercent = f
                Case "CropRight" : If Single.TryParse(value, f) Then adj.CropRightPercent = f
                Case "CropBottom" : If Single.TryParse(value, f) Then adj.CropBottomPercent = f
                Case "ResizeWidth" : If Integer.TryParse(value, i) Then adj.ResizeWidth = i
                Case "ResizeHeight" : If Integer.TryParse(value, i) Then adj.ResizeHeight = i
                Case "LockResizeAspect" : If Boolean.TryParse(value, b) Then adj.LockResizeAspect = b
                Case "ResizeInterpolation"
                    Dim mode As ResizeInterpolationMode
                    If [Enum].TryParse(value, mode) Then adj.ResizeInterpolation = mode
                Case "CanvasWidth" : If Integer.TryParse(value, i) Then adj.CanvasWidth = i
                Case "CanvasHeight" : If Integer.TryParse(value, i) Then adj.CanvasHeight = i
                Case "LockCanvasAspect" : If Boolean.TryParse(value, b) Then adj.LockCanvasAspect = b
                Case "CanvasAnchor" : adj.CanvasAnchor = value
                Case "CanvasBackgroundColor" : adj.CanvasBackgroundColor = value
                Case "FilterPreset" : adj.FilterPreset = value
                Case "FilterStrength" : If Single.TryParse(value, f) Then adj.FilterStrength = f
            End Select
        End Sub

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
