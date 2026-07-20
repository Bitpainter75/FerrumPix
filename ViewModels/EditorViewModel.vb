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
Imports FerrumPix.Controls
Imports SkiaSharp
Imports ReactiveUI
Imports FerrumPix.Services
Imports FerrumPix.Models

Namespace ViewModels

    Public Class EditorViewModel
        Inherits ViewModelBase

        ''' Die vollste Leiste der App: links Navigation + Dateiname, mittig die vier
        ''' Modus-Schalter, rechts Speichern/Speichern unter und acht Symbolschalter.
        Protected Overrides ReadOnly Property ToolbarLabelWidthThreshold As Double
            Get
                Return 1500
            End Get
        End Property

        Private ReadOnly _mainVm As MainWindowViewModel
        ' Identität des bearbeiteten Bildes (Filmstreifen, Metadaten, Bewertung, Navigation, Dateiname). Bei
        ' einem geöffneten .fpx ist das der PROJEKTpfad, während die Pipeline das entpackte Basisbild dekodiert.
        Private _currentImagePath As String = ""
        ' Überschreibt für .fpx-Projekte die tatsächlich zu dekodierende Bildquelle (das entpackte Basisbild im
        ' Temp-Ordner). Leer = normales Bild; dann ist RenderSourcePath identisch zu _currentImagePath.
        Private _renderSourcePathOverride As String = ""
        ' True, wenn die Quelle nicht in-place überschrieben werden darf (z.B. eine Immich-Temp-Kopie):
        ' "Speichern" ist dann gesperrt und leitet überall auf "Speichern unter" um.
        Private _forceSaveAsOnly As Boolean = False
        ' Pfad der geöffneten .fpx-Projektdatei (leer = normales Bild). Die eigentliche Arbeitsquelle ist das
        ' entpackte Basisbild im Temp-Ordner; dieser Pfad liefert für "Speichern unter" aber Name, Ordner und
        ' Formatvorschlag, damit der Dialog den Projektnamen statt "base" vorschlägt.
        Private _currentFpxPath As String = ""
        Private _currentFpxTempDir As String = ""
        ' Neu angelegtes, noch nie gespeichertes Dokument. Die leere Fläche liegt als PNG in einem
        ' Temp-Ordner (siehe CreateNewDocumentAsync) - genau wie das entpackte Basisbild eines .fpx.
        ' Dadurch bleiben alle File.Exists-/FileInfo-/Exif-Invarianten der Ladekette gültig; das Flag
        ' unterdrückt nur, was für eine Temp-Datei sinnlos wäre (Pfadanzeige, Namensvorschlag).
        Private _isNewDocument As Boolean = False
        Private _currentNewDocTempDir As String = ""
        ' Übergabe an OpenImageAsync: dort wird der ALTE Temp-Ordner aufgeräumt, bevor der neue
        ' übernommen wird. Ohne diese Staffelübergabe würde derselbe Aufruf den Ordner löschen, aus
        ' dem er gerade lädt (dasselbe Problem löst der .fpx-Zweig über newFpxTempDir).
        Private _pendingNewDocTempDir As String = ""
        ' Ein transparent angelegtes Dokument bringt seine Alpha-Löcher schon im Decode mit. Ohne
        ' dieses Flag hielte das Arbeitsbild sie für „voll deckend" - kein Schachbrett, und beim
        ' Speichern fiele die Transparenz weg (siehe WorkingImageService.HasAlphaHoles).
        Private _newDocTransparentBackground As Boolean = False
        Private _pendingNewDocTransparent As Boolean = False
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
        Private _retouchLivePatchImage As Bitmap
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
        Private _colorNoiseReduction As Double = 0
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
        Private _brushFlow As Double = 100
        Private _brushPreset As String = "soft"
        Private _brushPresets As System.Collections.ObjectModel.ObservableCollection(Of BrushPresetItem) = Nothing
        Private _isEraserMode As Boolean = False
        Private _eraserFillColor As String = "#00FFFFFF"
        Private _isRepairMode As Boolean = False

        ''' Pinsel, Radiergummi, Verwischen, Reparaturpinsel und Stempel arbeiten auf denselben Feldern (_brushSize,
        ''' _retouchRadius, _annotationStrokeColor). Damit beim Wechsel keines die Werte des anderen
        ''' erbt, sichert SetPaintMode den Stand des verlassenen Werkzeugs hier und holt den des neuen
        ''' zurück - beim ersten Aufruf sind das die Startwerte aus ResetEditorUiStateForNewImage.
        Private NotInheritable Class PaintToolState
            Public Property Size As Double = 24.0
            Public Property Hardness As Double = 100
            Public Property Opacity As Double = 100
            Public Property Flow As Double = 100
            Public Property StrokeColor As String = "#FF000000"
            Public Property EraserFillColor As String = "#00FFFFFF"
        End Class

        ''' <summary>Ein Eintrag im visuellen Pinsel-Picker: stabiler Key, angezeigter Name, gerendertes
        ''' Beispielstrich-Bild und Auswahlzustand (für die Hervorhebung der aktiven Kachel).</summary>
        Public NotInheritable Class BrushPresetItem
            Inherits ViewModelBase

            Private _isSelected As Boolean

            Public Sub New(key As String, label As String, preview As Avalonia.Media.Imaging.Bitmap)
                Me.Key = key
                Me.Label = label
                Me.Preview = preview
            End Sub

            Public ReadOnly Property Key As String
            Public ReadOnly Property Label As String
            Public ReadOnly Property Preview As Avalonia.Media.Imaging.Bitmap

            Public Property IsSelected As Boolean
                Get
                    Return _isSelected
                End Get
                Set(value As Boolean)
                    Me.RaiseAndSetIfChanged(_isSelected, value)
                End Set
            End Property
        End Class

        Public NotInheritable Class AnnotationBlendModeOption
            Public Sub New(key As String, displayName As String)
                Me.Key = key
                Me.DisplayName = displayName
            End Sub

            Public ReadOnly Property Key As String
            Public ReadOnly Property DisplayName As String

            Public Overrides Function ToString() As String
                Return DisplayName
            End Function
        End Class

        Private Shared ReadOnly _annotationBlendModeOptions As IReadOnlyList(Of AnnotationBlendModeOption) =
            New List(Of AnnotationBlendModeOption) From {
                New AnnotationBlendModeOption("Normal", "Normal"),
                New AnnotationBlendModeOption("Darken", "Abdunkeln"),
                New AnnotationBlendModeOption("Multiply", "Multiplizieren"),
                New AnnotationBlendModeOption("ColorBurn", "Farben nachbelichten"),
                New AnnotationBlendModeOption("Lighten", "Aufhellen"),
                New AnnotationBlendModeOption("Screen", "Negativ multiplizieren"),
                New AnnotationBlendModeOption("ColorDodge", "Farben abwedeln"),
                New AnnotationBlendModeOption("Plus", "Hinzufügen"),
                New AnnotationBlendModeOption("Overlay", "Ineinanderkopieren"),
                New AnnotationBlendModeOption("SoftLight", "Weiches Licht"),
                New AnnotationBlendModeOption("HardLight", "Hartes Licht"),
                New AnnotationBlendModeOption("Difference", "Differenz"),
                New AnnotationBlendModeOption("Exclusion", "Ausschluss"),
                New AnnotationBlendModeOption("Hue", "Farbton"),
                New AnnotationBlendModeOption("Saturation", "Sättigung"),
                New AnnotationBlendModeOption("Color", "Farbe"),
                New AnnotationBlendModeOption("Luminosity", "Luminanz")
            }

        Private _paintToolStates As Dictionary(Of String, PaintToolState) = NewPaintToolStates()

        Private Shared Function NewPaintToolStates() As Dictionary(Of String, PaintToolState)
            Return New Dictionary(Of String, PaintToolState)(StringComparer.Ordinal) From {
                {"Brush", New PaintToolState()},
                {"Eraser", New PaintToolState()},
                {"Blur", New PaintToolState()},
                {"Repair", New PaintToolState()},
                {"Clone", New PaintToolState()}
            }
        End Function

        Private Shared Function NormalizeAnnotationBlendMode(value As String) As String
            Dim raw = If(value, "Normal").Trim()
            Dim match = _annotationBlendModeOptions.FirstOrDefault(Function(o) String.Equals(o.Key, raw, StringComparison.OrdinalIgnoreCase))
            Return If(match?.Key, "Normal")
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
        ' Die Retusche-Modi setzen dieselben RetouchSpots; Stempel hängt ihnen eine Klonquelle an,
        ' Reparatur markiert sie als Heal, Verwischen bleibt der einfache Weich-Mittelwert.
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
        Private _annotationBlendMode As String = "Normal"
        ' Hintergrund-Ebene (Basisbild) im Ebenen-Panel aus-/eingeblendet. Wirkt strukturell übers
        ' Compositing (siehe ImageProcessor.ApplyAnnotations), nicht als Pixel-Anpassung.
        Private _backgroundHidden As Boolean = False
        Private _pixelLayerHidden As Boolean = False

        ''' True, solange die eingebettete RAW-Vorschau angezeigt wird und die echte Entwicklung noch
        ''' laeuft. In diesem Fenster gibt es noch KEIN Arbeitsbild - Pinsel und Retusche muessen
        ''' gesperrt bleiben, sonst liefen ihre Commits ins Leere.
        Private _workingImagePending As Boolean = False
        Private _annotationRotation As Double = 0
        Private _annotationFlipH As Boolean = False
        ' Objekt-Anpassungsmodus: Solange ein Objekt markiert ist UND ein objektfähiges Werkzeug aktiv ist,
        ' beschreiben die Regler-Felder das Objekt; die Werte des BILDES liegen so lange hier geparkt.
        Private _objectAdjustIndex As Integer = -1
        Private _imagePixelAdjustments As ImageAdjustments = Nothing
        Private _objectAdjustSwapInProgress As Boolean = False
        Private _annotationFlipV As Boolean = False
        Private _annotationLockAspect As Boolean = True
        Private _annotationAnchor As String = "BottomRight"
        Private _annotationIsVisible As Boolean = True
        Private _annotationXPercent As Double = 35
        Private _annotationYPercent As Double = 35
        Private _annotationWidthPercent As Double = 30
        Private _annotationHeightPercent As Double = 12
        Private _annotationFillKind As String = "Solid"
        Private _annotationTextPathKind As String = ""
        Private _annotationTextPathBend As Double = 50
        Private _annotationTextPathStartOffset As Double = 0
        Private _calibrationRedHue As Double = 0
        Private _calibrationRedSaturation As Double = 0
        Private _calibrationGreenHue As Double = 0
        Private _calibrationGreenSaturation As Double = 0
        Private _calibrationBlueHue As Double = 0
        Private _calibrationBlueSaturation As Double = 0
        Private _calibrationShadowTint As Double = 0
        Private _annotationLetterSpacingPercent As Double = 0
        Private _annotationBold As Boolean = False
        Private _annotationItalic As Boolean = False
        Private _annotationFillColor2 As String = "#FFFFFFFF"
        Private _annotationGradientAngle As Double = 0
        Private _annotationGradientInverted As Boolean = False
        Private _annotationShadowEnabled As Boolean = False
        Private _annotationShadowOffsetX As Double = 4
        Private _annotationShadowOffsetY As Double = 4
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
        Private _selectionMode As String = "Move"
        Private _selectionTolerance As Double = 15
        ' Weiche Kante der Auswahl in Bildpixeln (0 = harte Kante). Die MASKE bleibt hart gespeichert.
        Private _selectionFeather As Double = 0
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
        ' Raster-Paint-Werkzeuge (Pinsel/Radierer) sind keine Overlay-Objekte und erscheinen nicht im
        ' Ebenenstapel. Der Renderer wandelt diese Daten nur intern in seine bestehende Zeichenroutine um.
        Private ReadOnly _pixelEditLayer As New PixelEditLayer()

        ' ARBEITSBILD (Umbau 2026-07-17): voll aufgelöstes Bild, aus dem die Vorschau
        ' abgeleitet wird; Retusche/Striche/gerasterte Ebenen werden regional eingebacken.
        ' Besitz: der Service hält das Voll-Bitmap, das Vorschau-Bitmap gehört
        ' weiter der _previewSource-/Stale-Mechanik (siehe WorkingImageService-Kopf).
        Private ReadOnly _workingImage As New WorkingImageService()

        ''' Serielle FIFO-Queue für Region-Commits ins Arbeitsbild (Striche, Retusche, Rastern).
        ''' Die Reihenfolge hält die Undo-Patch-Zuordnung korrekt (je Zug EIN PushUndo direkt
        ''' vor dem Enqueue); CloneFull (Export) wartet über den Service-Lock automatisch auf
        ''' einen gerade laufenden Commit.
        Private _workingCommitChain As Task = Task.CompletedTask

        ''' .fpx mit voll aufgelöstem retouch.png (= gebackenes Arbeitsbild): Pfad-Override für den
        ''' Arbeitsbild-Decode in PreparePreviewSource (statt des Basisbilds) plus Alpha-Flag aus dem
        ''' Rezept. Leer bei normalen Bildern; die Maße-Prüfung gegen die Basis passiert beim Decode.
        Private _workingImageOverridePath As String = ""
        Private _workingImageOverrideHasAlpha As Boolean = False

        ''' Anzahl der Commits, die gerade in der Queue stecken (UI-Thread-Zugriff): solange > 0
        ''' sind Undo/Redo gesperrt (siehe CanUndo) - die Undo-Zuordnung wäre sonst unvollständig.
        Private _pendingWorkingCommits As Integer = 0

        Private Sub EnqueueWorkingCommit(work As Func(Of WorkingImagePatch), onDoneUi As Action(Of WorkingImagePatch))
            _pendingWorkingCommits += 1
            Me.RaisePropertyChanged(NameOf(CanUndo))
            Me.RaisePropertyChanged(NameOf(CanRedo))
            ' Identität des Arbeitsbilds beim Einreihen merken: kommt der Commit erst NACH einem
            ' Bildwechsel an die Reihe, verfällt er (sonst malte er in das falsche Bild).
            Dim initStamp = _workingImage.InitStamp
            _workingCommitChain = _workingCommitChain.ContinueWith(
                Sub(prev)
                    Dim patch As WorkingImagePatch = Nothing
                    Try
                        If _workingImage.InitStamp = initStamp Then
                            ' ms mitloggen: teure Einback-Renders (z. B. Klecks-Pinsel in
                            ' Vollauflösung, Befund 17.07.) sind sonst im Log unsichtbar -
                            ' zwischen zwei Einträgen klaffte nur eine unerklärte Lücke.
                            Dim sw = Diagnostics.Stopwatch.StartNew()
                            patch = work()
                            DiagnosticLogService.LogAlways("Editor.WorkingCommit",
                                $"done rect={If(patch Is Nothing, "-", $"{patch.Rect.Left},{patch.Rect.Top},{patch.Rect.Width}x{patch.Rect.Height}")} ms={sw.ElapsedMilliseconds}")
                        Else
                            DiagnosticLogService.LogAlways("Editor.WorkingCommit", "skipped=imageChanged")
                        End If
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Editor.WorkingCommit", ex)
                    End Try
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        Sub()
                            _pendingWorkingCommits = Math.Max(0, _pendingWorkingCommits - 1)
                            Try
                                If onDoneUi IsNot Nothing Then onDoneUi(patch)
                            Finally
                                ' Ein Commit kann der erste GEBACKENE Inhalt sein - dann kippt bei
                                ' RAW-Quellen der Speichern-Weg von Sidecar auf "Speichern unter".
                                Me.RaisePropertyChanged(NameOf(CanSaveSidecar))
                                Me.RaisePropertyChanged(NameOf(CanSaveInPlace))
                                Me.RaisePropertyChanged(NameOf(CanUndo))
                                Me.RaisePropertyChanged(NameOf(CanRedo))
                            End Try
                        End Sub)
                End Sub, TaskScheduler.Default)
        End Sub

        ' STUFE 2 (Szenen-Renderer): die persistente Szene = Basis + Pixelanpassungen + Striche + ALLE
        ' Overlay-Objekte in Z-Order (Preview-Aufloesung), gerendert von denselben Routinen wie der
        ' Export. PreviewImage zeigt IMMER eine Konvertierung dieser Szene; Aenderungen laufen als
        ' Dirty-Rect-Region-Renders in die Szene (TryRenderSceneRegionSync) statt als separate
        ' Composite-Patches ueber der Anzeige. Nur auf dem UI-Thread anfassen.
        Private _sceneSk As SKBitmap = Nothing
        ' Asynchroner Region-Worker: Regler-Bursts und Drag-Starts duerfen den UI-Thread nicht mit
        ' 200-800-ms-Effekt-Renders blockieren. Anforderungen sammeln sich als Rect-Union in
        ' _sceneRegionPendingRect; ein einzelner Worker rendert im Hintergrund und der UI-Thread
        ' wendet nur noch an (~20 ms). Natuerliche Gegendrossel: solange gerendert wird, waechst
        ' nur die Union.
        Private _sceneRegionPendingRect As SKRectI = SKRectI.Empty
        Private _sceneRegionWorkerBusy As Boolean = False
        ' Inhalts-Version der Szene: JEDER Szene-Write (sync-Region, Worker-Apply, Vollrender) zaehlt
        ' hoch. Der Worker verwirft sein Ergebnis, wenn sich der Inhalt seit seinem Snapshot geaendert
        ' hat, und rendert neu - sonst ueberschreibt ein langer Hintergrund-Render (grosse weiche
        ' Pinselstriche!) z.B. ein zwischenzeitlich angelegtes Objekt mit seinem alten Stand.
        Private _sceneContentVersion As Long = 0
        ' Persistente Anzeige der Szene: EINE WriteableBitmap, in die nur die geaenderten Zeilen
        ' geblittet werden. Ein neues 40-MB-Bitmap pro Update (fruehere Vollkonvertierung) erzeugte
        ' GC-/Textur-Upload-Stalls, die Maus-Events schluckten - Regler sprangen grob.
        Private _sceneDisplay As WriteableBitmap = Nothing
        ' Sequenz fuer asynchrone Ghost-Renders (Drag-Start): nur das juengste Ergebnis zaehlt.
        Private _ghostRenderSeq As Long = 0

        ' STUFE 3 (Zoom-Detail): Bei Zoom ueber Szenen-1:1 wird asynchron EINE hochaufgeloeste
        ' Detail-Szene gerendert (voller Renderer auf separat geladener Quelle, Deckel
        ' ZoomDetailMaxDimension, Base-Cache unberuehrt) und pro _sceneContentVersion gecacht.
        ' Angezeigt wird nur der sichtbare Viewport-Ausschnitt (+Rand) als kleines Overlay-Bitmap.
        ' Quelle+Detail leben nur solange Zoom > 1; alles nur auf dem UI-Thread anfassen.
        ''' 6144 statt urspruenglich 8192 (Befund: Nachschaerfen bei 45-MP-Bild dauerte 3,5 s
        ''' pro Versuch) - halbiert grob Renderzeit und Speicher, bleibt 2,4x schaerfer als die Szene.
        Public Const ZoomDetailMaxDimension As Integer = 6144
        Private _zoomDetailSource As SKBitmap = Nothing        ' hochaufgeloeste Arbeitsbild-Ableitung (gecacht)
        Private _zoomDetailSourcePath As String = Nothing
        ''' Arbeitsbild-Version, aus der _zoomDetailSource abgeleitet wurde: nach einem
        ''' eingebackenen Commit (Stufe D+) ist der alte Downscale inhaltlich veraltet.
        Private _zoomDetailSourceWorkingVersion As Long = -1
        Private _zoomDetailSk As SKBitmap = Nothing            ' fertige Detail-Szene
        Private _zoomDetailVersion As Long = -1                ' _sceneContentVersion des Detail-Standes
        Private _zoomDetailRendering As Boolean = False
        Private _zoomDetailDisposePending As Boolean = False   ' Reset waehrend laufendem Render: deferred
        Private _zoomDetailRenderSeq As Long = 0
        Private _zoomDetailImage As Bitmap = Nothing           ' Viewport-Ausschnitt fuer die View
        Private _zoomDetailFracLeft As Double                  ' Lage des Ausschnitts in Bildanteilen 0..1
        Private _zoomDetailFracTop As Double
        Private _zoomDetailFracWidth As Double
        Private _zoomDetailFracHeight As Double
        Private _zoomDetailWanted As Boolean = False           ' View meldet: Zoom > Szenen-1:1 aktiv
        Private _zoomDetailVisLeft As Double                   ' zuletzt gemeldeter sichtbarer Ausschnitt
        Private _zoomDetailVisTop As Double
        Private _zoomDetailVisRight As Double
        Private _zoomDetailVisBottom As Double
        Private _zoomDetailTimer As DispatcherTimer = Nothing  ' Debounce fuer den teuren Detail-Render
        Private _zoomDetailExtracting As Boolean = False        ' Guard gegen synchrones PropertyChanged-Reentry
        ' Vorher/Nachher im Zoom (Nutzerwunsch 2026-07-17): zweites Detail der Vorher-Seite aus dem
        ' ORIGINAL-Decode (nur Geometrie, keine Farb-Pipeline) - die View clippt beide Details an der
        ' Vergleichslinie. Vorher-Detail nur, wenn die Maße zur Nachher-Detail-Szene passen.
        Private _zoomDetailWantBefore As Boolean = False       ' View meldet: Vergleich sichtbar
        Private _zoomDetailBeforeSource As SKBitmap = Nothing  ' hochaufgeloester Original-Decode (gecacht)
        Private _zoomDetailBeforeSourcePath As String = Nothing
        Private _zoomDetailBeforeSk As SKBitmap = Nothing      ' fertige Vorher-Detail-Szene
        Private _zoomDetailBeforeImage As Bitmap = Nothing     ' Viewport-Ausschnitt Vorher fuer die View

        ''' <summary>Feuert nach einem Region-Blit in die (unveraenderte Instanz der) Szene-Anzeige -
        ''' die View muss das Image-Control dann per InvalidateVisual neu zeichnen lassen, weil sich
        ''' fuer das Binding nichts geaendert hat.</summary>
        Public Event SceneInvalidated As EventHandler
        ' Anzeige-Reihenfolge fürs Ebenen-Panel: _annotations UMGEKEHRT (vorderste Ebene oben, hinterste
        ' unten, wie in üblichen Bildbearbeitungen). Wird bei jeder Stapeländerung synchron neu aufgebaut;
        ' die Auswahl läuft objektbasiert über SelectedLayer, damit Umsortieren/Neuaufbau die Markierung
        ' nicht verliert. _annotations bleibt die Wahrheit (Index 0 = zuerst gezeichnet = hinten).
        Private ReadOnly _layerRows As New ObservableCollection(Of ImageAnnotation)()
        Private _suppressLayerRowSelectionSync As Boolean = False
        Private _selectedAnnotationIndex As Integer = -1
        ' Verfolgt den Raster-Paint-Eintrag, an den die aktuell laufende Pinsel-/Radiergummi-"Sitzung"
        ' noch weitere Striche anhängt, statt für jeden einzelnen Strich einen neuen Eintrag anzulegen (siehe
        ' AddBrushStroke). Per Objektreferenz statt Index verfolgt, damit ein zwischenzeitliches
        ' Undo/Redo (das die Rasterliste komplett durch geklonte Objekte ersetzt) automatisch über die
        ' Contains-Prüfung in AddBrushStroke erkannt wird, ohne hier explizit zurückgesetzt werden zu
        ' müssen. Endet explizit bei Werkzeugwechsel oder Pinsel/Radiergummi-Umschalten.
        Private _isLoadingAnnotation As Boolean
        Private _annotationFontOptionsCache As IReadOnlyList(Of String)
        Private _isUpdatingResize As Boolean
        Private _rating As Integer = 0
        Private _isFavorite As Boolean = False
        Private _colorLabel As String = ""
        Private _newTagText As String = ""
        Private _hasChanges As Boolean
        Private _statusText As String = ""
        ' True, solange die Statuszeile nichts Wichtiges sagt (leer oder „Vorschau bereit"). Nur dann
        ' darf die Mausposition ihren Platz übernehmen - siehe FooterStatusText.
        Private _statusIsIdle As Boolean = True
        Private _mousePositionText As String = ""
        Private _activeZoomPreset As ZoomPresetMode = ZoomPresetMode.Fit
        Private _saveQuality As Integer = 90
        Private _histogramImage As Bitmap
        Private _exifInfo As ExifData
        Private _previewPending As Boolean
        Private _annotationCompositePreviewPending As Boolean
        Private _annotationCompositePreviewRetries As Integer
        Private _suppressPreviewDirty As Boolean = False
        ' Verhindert, dass eine einzelne logische Nutzeraktion (z.B. Werkzeugwechsel, der intern
        ' sowohl SelectedAnnotationIndex als auch CurrentTool setzt) NotifyAnnotationOverlayStateChanged
        ' mehrfach hintereinander auslöst.
        Private _overlayNotifySuppressDepth As Integer = 0
        Private ReadOnly _previewTimer As DispatcherTimer
        Private ReadOnly _filmstripNavDebouncer As FilmstripNavigationDebouncer
        Private ReadOnly _previewSync As New Object()
        Private ReadOnly _stalePreviewSources As New List(Of SKBitmap)()
        ''' „Vorher"-Quelle des Vergleichs: eigener Vorschau-Decode der ORIGINAL-Datei. Seit dem
        ''' Arbeitsbild-Umbau enthält _previewSource (ab Stufe D) gebackene Retusche/Striche -
        ''' „Vorher" soll aber das echte Original zeigen. Faul erzeugt (nur solange der Regler
        ''' aktiv ist), entsorgt über die Stale-Liste (in-flight-Renders!) bei Bildwechsel/Regler-aus.
        Private _comparisonOriginalSource As SKBitmap
        Private _comparisonOriginalPath As String
        Private _previewSource As SKBitmap
        Private _previewRenderCts As CancellationTokenSource
        Private _previewRequestId As Integer

        ''' Marke des QUELLWECHSELS (Bild öffnen/wechseln). Bewusst getrennt von _previewRequestId,
        ''' den auch jeder Render-Start hochzählt - sonst würde ein währenddessen anlaufender Render
        ''' einen völlig gültigen Decode verwerfen lassen.
        Private _previewSourceSwapId As Long = 0
        Private _lastRetouchLivePreviewUtc As DateTime = DateTime.MinValue
        Private Const RetouchLivePreviewMinIntervalMs As Double = 70.0
        Private _retouchStrokeActive As Boolean = False
        Private _retouchStrokeStartSpotIndex As Integer = 0
        Private _nextRetouchStrokeId As Integer = 1
        Private _activeRetouchStrokeId As Integer = 0
        Private _retouchLiveBitmap As SKBitmap = Nothing
        Private _retouchLiveSampleBitmap As SKBitmap = Nothing
        Private _retouchLivePatchRect As SKRectI = SKRectI.Empty
        Private _retouchLiveMaskBitmapWidth As Integer = 0
        Private _retouchLiveMaskBitmapHeight As Integer = 0
        Private _clearRetouchLivePatchAfterPreview As Boolean = False
        Private _retouchLivePatchLeftPercent As Double = 0
        Private _retouchLivePatchTopPercent As Double = 0
        Private _retouchLivePatchWidthPercent As Double = 0
        Private _retouchLivePatchHeightPercent As Double = 0
        Private _annotationDirtyRect As SKRectI = SKRectI.Empty
        Private _annotationPlacementEditActive As Boolean = False
        Private _annotationPlacementStartDirtyRect As SKRectI = SKRectI.Empty
        Private _activePreviewRenders As Integer
        Private _showBeforeImage As Boolean = False
        ' Zuletzt vom Nutzer gewählter Vergleichs-Zustand; kommt aus den Einstellungen und wird dort beim
        ' Umschalten wieder hinterlegt (SetComparisonVisibleFromUser).
        Private _comparisonAutoEnabled As Boolean = AppSettingsService.Load().EditorShowComparison
        Private _folderPaths As New List(Of String)()
        ' Cache-Scope für die Filmstreifen-Thumbnails (Suchlisten-Scope statt je Ursprungsordner) - siehe ViewerViewModel.
        Private _thumbCacheScopeId As String = Nothing
        Private _thumbCacheScopeName As String = Nothing
        Private _currentIndex As Integer = -1
        Private _selectedInfoTab As InfoSidebarTab = InfoSidebarTab.General
        Private _selectedLayersPanelTab As LayersPanelTab = LayersPanelTab.Tool
        ' Vorschau-Deckel (längste Kante). Früher 0 = volle Quellauflösung - damit lief die GESAMTE
        ' Editor-Pipeline z.B. in 49 MP (gemessen: Full-Render 7-8 s, Composite-Patch 1-2 s), und die
        ' fragile Overlay-Hybrid-Architektur existierte nur, um diese langsamen Patches zu umgehen.
        ' Jetzt adaptiv an der längsten Monitorkante (min 2560, Fallback 3072): scharf in der
        ' Fit-Ansicht, ~10x schnellere Pipeline. Detail-Zoom über der Vorschau-Auflösung wird
        ' übergangsweise weicher, bis der Viewport-1:1-Region-Render kommt (Stufe 3, siehe
        ' EDITOR_RENDERING_NOTES.md). Export/Speichern rendern unverändert in voller Quellauflösung.
        Private Shared _previewMaxDimensionResolved As Integer = 0

        Private Shared ReadOnly Property PreviewMaxDimension As Integer
            Get
                If _previewMaxDimensionResolved > 0 Then Return _previewMaxDimensionResolved
                Dim resolved = 3072
                Try
                    Dim lifetime = TryCast(Avalonia.Application.Current?.ApplicationLifetime,
                                           Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)
                    Dim screens = lifetime?.MainWindow?.Screens
                    If screens IsNot Nothing Then
                        Dim longest = 0
                        For Each s In screens.All
                            longest = Math.Max(longest, Math.Max(s.Bounds.Width, s.Bounds.Height))
                        Next
                        If longest > 0 Then resolved = Math.Max(2560, longest)
                    End If
                Catch
                    ' Kein Fenster/Screen ermittelbar (z.B. sehr früher Aufruf): Fallback bleibt 3072.
                End Try
                _previewMaxDimensionResolved = resolved
                Return resolved
            End Get
        End Property
        Private Const FpxCompositeMaxDimension As Integer = 2560
        Private Const PreviewDebounceMs As Double = 90.0
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
        ''' Undo-Eintrag des Arbeitsbild-Umbaus (Stufe B): Anpassungs-Snapshot plus optionaler
        ''' Pixel-Patch (Vorher-Ausschnitt eines eingebackenen Region-Commits; belegt ab Stufe D).
        ''' Ein Patch gehört immer GENAU EINEM Stapel-Eintrag - beim Undo wandert er mit
        ''' getauschtem Inhalt in den Redo-Eintrag und umgekehrt (Tausch-Schema im Service).
        Private NotInheritable Class UndoEntry
            Public Adjustments As ImageAdjustments
            Public Patch As WorkingImagePatch
        End Class

        Private ReadOnly _undoStack As New Stack(Of UndoEntry)()
        Private ReadOnly _redoStack As New Stack(Of UndoEntry)()
        ''' Zuletzt per PushUndo erzeugter Eintrag: der zugehörige Region-Commit (ab Stufe D)
        ''' hängt seinen Pixel-Patch hier an. Je Zug genau EIN PushUndo unmittelbar vor dem
        ''' Commit + strikt serielle Commit-Reihenfolge halten die Zuordnung korrekt.
        Private _lastPushedUndoEntry As UndoEntry

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

        ''' <summary>Objektstapel in ANZEIGE-Reihenfolge fürs Ebenen-Panel: _annotations umgekehrt (vorderste
        ''' Ebene zuerst/oben). Wird von RebuildLayerRows synchron gehalten.</summary>
        Public ReadOnly Property LayerRows As ObservableCollection(Of ImageAnnotation)
            Get
                Return _layerRows
            End Get
        End Property

        ''' <summary>Die markierte Ebene als Objekt (statt Index) - so bleibt die Markierung beim Umkehren/
        ''' Neuaufbau der Anzeigeliste erhalten. Setzen übersetzt zurück auf SelectedAnnotationIndex.</summary>
        Public Property SelectedLayer As ImageAnnotation
            Get
                If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return Nothing
                Return _annotations(_selectedAnnotationIndex)
            End Get
            Set(value As ImageAnnotation)
                ' Während RebuildLayerRows die Liste leert/neu füllt, meldet die ListBox kurz SelectedItem=Nothing.
                ' Ohne diese Sperre würde das die Selektion bei jeder Stapeländerung fälschlich aufheben.
                If _suppressLayerRowSelectionSync Then Return
                If value Is Nothing Then
                    SelectedAnnotationIndex = -1
                Else
                    SelectedAnnotationIndex = _annotations.IndexOf(value)
                End If
            End Set
        End Property

        Private Sub RebuildLayerRows()
            _suppressLayerRowSelectionSync = True
            Try
                _layerRows.Clear()
                For i = _annotations.Count - 1 To 0 Step -1
                    _layerRows.Add(_annotations(i))
                Next
            Finally
                _suppressLayerRowSelectionSync = False
            End Try
            Me.RaisePropertyChanged(NameOf(SelectedLayer))
        End Sub

        Private ReadOnly _allShapeIcons As New List(Of ShapeIconEntry)()
        Private ReadOnly _fixedShapeItems As New ObservableCollection(Of ShapeIconEntry)()
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

        Public ReadOnly Property FixedShapeItems As ObservableCollection(Of ShapeIconEntry)
            Get
                Return _fixedShapeItems
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

        ''' <summary>STUFE 2: Die Szene enthaelt IMMER alle Objekte. Das Selektions-Overlay ist nur noch
        ''' der transiente Drag-GHOST waehrend eines Placement-Edits (die Szene blendet das aktiv
        ''' bearbeitete Objekt dann aus, siehe GetSceneAdjustments). Ausserhalb des Ziehens wuerde das
        ''' Overlay das bereits in der Szene gerenderte Objekt doppeln (Schatten doppelt deckend usw.).</summary>
        ''' Ghost nach Drag-Ende STEHEN LASSEN, bis der Szene-Render mit dem Objekt gelandet ist
        ''' (Region-Renders brauchen je nach Effekten 300-700 ms) - sonst fehlt das Objekt für
        ''' diese Spanne im Bild: das "Flackern nach dem Verschieben" (Nutzer-Befund 2026-07-17).
        Private _placementGhostLinger As Boolean = False

        Public ReadOnly Property ShowSelectedSvgOverlay As Boolean
            Get
                Return _selectedAnnotationOverlayImage IsNot Nothing AndAlso
                       (_annotationPlacementEditActive OrElse _placementGhostLinger)
            End Get
        End Property

        ''' Vom Szene-Worker/Vollrender aufgerufen, sobald frischer Szeneninhalt sichtbar ist:
        ''' der nachlaufende Ghost darf jetzt weg.
        Private Sub ClearPlacementGhostLinger()
            If Not _placementGhostLinger Then Return
            _placementGhostLinger = False
            Me.RaisePropertyChanged(NameOf(ShowSelectedSvgOverlay))
        End Sub

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

        Private Sub LoadFixedShapeItems()
            _fixedShapeItems.Clear()
            Const base As String = "avares://FerrumPix/Assets/Icons/outline/"
            AddFixedShape("Rectangle", "Rechteck", base & "rectangle.svg")
            AddFixedShape("RoundedRectangle", "Abgerundetes Rechteck", base & "square-rounded.svg")
            AddFixedShape("Ellipse", "Kreis/Ellipse", base & "circle.svg")
            AddFixedShape("Triangle", "Dreieck", base & "triangle.svg")
            AddFixedShape("Trapezoid", "Trapez", base & "trapezoid.svg")
            AddFixedShape("Diamond", "Raute", base & "square-rotated.svg")
            AddFixedShape("Polygon", "Polygon", base & "hexagon.svg")
            AddFixedShape("Star", "Stern", base & "star.svg")
            AddFixedShape("DoubleStar", "Doppelstern", base & "eight-point-star.svg")
            AddFixedShape("Line", "Linie", base & "line-shape.svg")
            AddFixedShape("Arrow", "Pfeil", base & "arrow-right.svg")
            AddFixedShape("SpeechBubble", "Sprechblase", base & "speech-bubble-shape.svg")
            AddFixedShape("EllipseSpeechBubble", "Ellipsen-Sprechblase", base & "ellipse-speech-bubble-shape.svg")
            AddFixedShape("Droplet", "Tropfen", base & "droplet-shape.svg")
            AddFixedShape("Cloud", "Wolke", base & "cloud.svg")
            AddFixedShape("Heart", "Herz", base & "heart.svg")
            AddFixedShape("Spiral", "Spirale", base & "spiral.svg")
        End Sub

        Private Sub AddFixedShape(kind As String, displayName As String, iconPath As String)
            _fixedShapeItems.Add(New ShapeIconEntry With {
                .IconPath = iconPath,
                .SourceName = displayName,
                .DisplayName = displayName,
                .PendingKind = kind
            })
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
            SchedulePreviewForCurrentTarget()
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
            Me.RaisePropertyChanged(NameOf(ShowAnnotationAspectLock))
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
                Me.RaisePropertyChanged(NameOf(ShowTextPathRow))
                Me.RaisePropertyChanged(NameOf(ShowTextPathControls))
            Me.RaisePropertyChanged(NameOf(ShowFillColorControls))
            Me.RaisePropertyChanged(NameOf(ShowFillColorPicker))
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
            ' Überschreibt der Nutzer die bereits gewählte Vorlage, ändert sich der Name nicht - der
            ' Setter oben bricht dann ab und stempelt das Objekt nicht. Deshalb hier direkt.
            If HasSelectedAnnotation AndAlso EffectiveAnnotationKind = "Watermark" Then
                _annotations(_selectedAnnotationIndex).WatermarkPresetName = name
            End If
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

        ''' <summary>Bei Text auf einem KREISPFAD ist der gemessene Textkasten die falsche Box: er
        ''' ist breit und flach, der Kreis nutzt davon aber nur min(Breite, Hoehe) - der
        ''' Selektionsrahmen stand dadurch weit um einen kleinen Kreis herum (Nutzerbefund
        ''' 2026-07-20, zweimal gemeldet).
        ''' Stattdessen eine QUADRATISCHE Box, deren Umfang zum Text passt: der Text laeuft einmal
        ''' herum, also Durchmesser = Textbreite / Pi. Damit umschliesst der Rahmen den Kreis, und
        ''' die Groesse folgt weiterhin der Schrift - genau wie beim geraden Text.
        ''' Aendert nichts, wenn kein Kreispfad aktiv ist.</summary>
        Private Sub FitBoxToCircleTextPath()
            If Not IsCircleTextPath(_annotationTextPathKind) Then Return
            Dim baseW = GetBaseWidth()
            Dim baseH = GetBaseHeight()
            If baseW <= 0 OrElse baseH <= 0 Then Return

            ' Breite des gemessenen Textkastens in Pixeln -> Durchmesser des Kreises.
            Dim textBreite = _annotationWidthPercent / 100.0 * baseW
            If textBreite <= 0 Then Return
            Dim seite = textBreite / Math.PI
            ' Untergrenze, damit ein sehr kurzer Text nicht zu einem Punkt schrumpft.
            seite = Math.Max(seite, _annotationFontSize * 2.5)

            ' Um den MITTELPUNKT wachsen, nicht von der oberen linken Ecke: der Textkasten ist
            ' breit und flach, der Kreis deutlich schmaler - ohne das saesse er am linken Ende
            ' dessen, wo vorher der Text stand, und spraenge beim Tippen mit.
            Dim alteBreite = _annotationWidthPercent
            Dim alteHoehe = _annotationHeightPercent
            Dim neueBreite = Math.Max(2.0, Math.Min(100.0, seite / baseW * 100.0))
            Dim neueHoehe = Math.Max(2.0, Math.Min(100.0, seite / baseH * 100.0))
            _annotationWidthPercent = neueBreite
            _annotationHeightPercent = neueHoehe

            ' Verankerte Wasserzeichen rechnen ihre Lage aus dem Anker - dort nicht eingreifen.
            If Not ShowWatermarkAnchorControls Then
                _annotationXPercent = Math.Max(-neueBreite + 1, Math.Min(100 - 1, _annotationXPercent + (alteBreite - neueBreite) / 2.0))
                _annotationYPercent = Math.Max(-neueHoehe + 1, Math.Min(100 - 1, _annotationYPercent + (alteHoehe - neueHoehe) / 2.0))
            End If
        End Sub

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
                ' Ebenenwechsel beendet eine laufende Pinsel-/Radiergummi-Mal-Sitzung (siehe AddBrushStroke).
                _pixelEditLayer.ResetActiveStroke()
                _selectedAnnotationIndex = clamped
                If clamped >= 0 Then
                    ' Im Drehen-Werkzeug wird KEIN Platzierungstyp scharfgestellt: dort will man ein Objekt
                    ' drehen, nicht ein weiteres anlegen - der nächste Klick auf freie Fläche würde sonst
                    ' eines setzen.
                    PendingInsertKind = If(IsObjectScopeTool(_currentTool), "", PlacementKindForAnnotation(_annotations(clamped)))
                    Dim targetTool = AnnotationKindToTool(_annotations(clamped).Kind)
                    ' Im Drehen-Werkzeug NICHT ins Werkzeug des Objekts springen: dort markiert man ein Objekt,
                    ' um es zu drehen oder zu spiegeln. Ein Sprung nach „Text"/„Einfügen" würde einen Klick auf
                    ' das Objekt aussehen lassen, als hätte er gar nicht selektiert.
                    If IsObjectScopeTool(_currentTool) Then targetTool = _currentTool
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
                Me.RaisePropertyChanged(NameOf(SelectedLayer))
                Me.RaisePropertyChanged(NameOf(HasSelectedAnnotation))
                Me.RaisePropertyChanged(NameOf(CanRasterizeSelectedAnnotation))
                Me.RaisePropertyChanged(NameOf(SelectedAnnotationKind))
                Me.RaisePropertyChanged(NameOf(SelectedAnnotationToolbarKind))
                Me.RaisePropertyChanged(NameOf(CurrentToolLabel))
                Me.RaisePropertyChanged(NameOf(CurrentToolIconSource))
                Me.RaisePropertyChanged(NameOf(ShowLayerToolOptions))
                Me.RaisePropertyChanged(NameOf(ShowDrawControls))
                Me.RaisePropertyChanged(NameOf(ShowBrushStrokeAdjustments))
                Me.RaisePropertyChanged(NameOf(IsBrushPaintMode))
                Me.RaisePropertyChanged(NameOf(IsEraserPaintMode))
                Me.RaisePropertyChanged(NameOf(SelectedPaintMode))
                Me.RaisePropertyChanged(NameOf(SelectedAnnotationText))
            Me.RaisePropertyChanged(NameOf(ShowAnnotationAspectLock))
                Me.RaisePropertyChanged(NameOf(ShowAnnotationProperties))
                Me.RaisePropertyChanged(NameOf(EffectiveAnnotationKind))
                Me.RaisePropertyChanged(NameOf(ShowTextContentControls))
                Me.RaisePropertyChanged(NameOf(ShowFontControls))
                Me.RaisePropertyChanged(NameOf(ShowTextPathRow))
                Me.RaisePropertyChanged(NameOf(ShowTextPathControls))
                Me.RaisePropertyChanged(NameOf(ShowFillColorControls))
                Me.RaisePropertyChanged(NameOf(ShowFillColorPicker))
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
                ' Ein anderes (oder gar kein) Objekt heißt: die Regler bedienen ein anderes Ziel.
                RefreshObjectAdjustMode()
                ' Objekt-Layer sind im Editor echte Overlays. Der Wechsel aktualisiert nur den
                ' Overlay-Zustand, nicht die Pixelvorschau.
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
            Me.RaisePropertyChanged(NameOf(ShowAnnotationAspectLock))
                Me.RaisePropertyChanged(NameOf(CurrentToolIconSource))
                Me.RaisePropertyChanged(NameOf(ShowAnnotationProperties))
                Me.RaisePropertyChanged(NameOf(EffectiveAnnotationKind))
                Me.RaisePropertyChanged(NameOf(ShowTextContentControls))
                Me.RaisePropertyChanged(NameOf(ShowFontControls))
                Me.RaisePropertyChanged(NameOf(ShowTextPathRow))
                Me.RaisePropertyChanged(NameOf(ShowTextPathControls))
                Me.RaisePropertyChanged(NameOf(ShowFillColorControls))
                Me.RaisePropertyChanged(NameOf(ShowFillColorPicker))
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
                Return (HasSelectedAnnotation AndAlso Not IsSelectedStrokeAnnotation()) OrElse HasPendingInsertKind
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
        ''' Beim QR-Code ist die Füllfarbe der Hintergrund (siehe FillColorLabel).
        Public ReadOnly Property ShowFillColorPicker As Boolean
            Get
                Return EffectiveAnnotationKind <> "Image" AndAlso Not IsWatermarkImageSource
            End Get
        End Property

        ''' Die Füllart (Vollfarbe/Verlauf/Radial) ist beim QR-Code wirkungslos: DrawQrCode zeichnet
        ''' die Module immer als Vollfarbe auf Vollfarbe und kennt keinen Verlauf. Dort stehen
        ''' stattdessen Vordergrund- und Hintergrundfarbe im Eigenschaften-Panel.
        Public ReadOnly Property ShowFillColorControls As Boolean
            Get
                Return ShowFillColorPicker AndAlso EffectiveAnnotationKind <> "QR"
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
            AnnotationStrokeWidth = If(normalizedKind = "Text" OrElse normalizedKind = "Watermark" OrElse normalizedKind = "QR" OrElse normalizedKind = "Image",
                                       0,
                                       If(normalizedKind = "Arrow" OrElse normalizedKind = "Line", 5, 2))
            AnnotationText = GetDefaultAnnotationText(normalizedKind, rawKind)
            AnnotationFontSize = If(normalizedKind = "Text" OrElse normalizedKind = "Watermark",
                                    GetDefaultTextAnnotationFontSizePixels(),
                                    48)
            AnnotationFontFamily = "Arial"
            AnnotationOpacity = 100
            AnnotationBlendMode = "Normal"
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
            AnnotationShadowOffsetX = 4
            AnnotationShadowOffsetY = 4
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
            AnnotationTextPathKind = ""
            AnnotationTextPathBend = 50
            AnnotationTextPathStartOffset = 0
            AnnotationLetterSpacingPercent = 0
            AnnotationBold = False
            AnnotationItalic = False
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
            Dim selectedKind = If(_selectedAnnotationIndex >= 0 AndAlso _selectedAnnotationIndex < _annotations.Count,
                                  NormalizeAnnotationKind(_annotations(_selectedAnnotationIndex).Kind),
                                  "")
            For Each item In _fixedShapeItems
                item.IsPending = String.Equals(NormalizeAnnotationKind(item.PendingKind), NormalizeAnnotationKind(_pendingInsertKind), StringComparison.OrdinalIgnoreCase)
                item.IsSelectedKind = Not String.IsNullOrEmpty(selectedKind) AndAlso String.Equals(NormalizeAnnotationKind(item.PendingKind), selectedKind, StringComparison.OrdinalIgnoreCase)
            Next
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

        Public ReadOnly Property SelectedAnnotationToolbarKind As String
            Get
                If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return ""
                Return ToolbarKindForAnnotation(_annotations(_selectedAnnotationIndex)?.Kind)
            End Get
        End Property

        Public ReadOnly Property SelectedAnnotationText As String
            Get
                If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return ""
                Return If(_annotations(_selectedAnnotationIndex)?.Text, "")
            End Get
        End Property

        Private Function SelectedAnnotationNormalizedKind() As String
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return ""
            Return NormalizeAnnotationKind(_annotations(_selectedAnnotationIndex)?.Kind)
        End Function

        Private Function IsSelectedBrushAnnotation() As Boolean
            Return String.Equals(SelectedAnnotationNormalizedKind(), "Brush", StringComparison.Ordinal)
        End Function

        Private Function IsSelectedEraserAnnotation() As Boolean
            Return String.Equals(SelectedAnnotationNormalizedKind(), "Eraser", StringComparison.Ordinal)
        End Function

        Private Function IsSelectedStrokeAnnotation() As Boolean
            Dim kind = SelectedAnnotationNormalizedKind()
            Return kind = "Brush" OrElse kind = "Eraser"
        End Function

        Public ReadOnly Property SelectedPaintMode As String
            Get
                If IsSelectedBrushAnnotation() Then Return "Brush"
                If IsSelectedEraserAnnotation() Then Return "Eraser"
                If _currentTool = EditorTool.Retouch Then Return If(_isCloneMode, "Clone", If(_isRepairMode, "Repair", "Blur"))
                If _currentTool = EditorTool.Draw AndAlso _isEraserMode Then Return "Eraser"
                If _currentTool = EditorTool.Draw Then Return "Brush"
                Return ""
            End Get
        End Property

        ''' True, wenn das Stempel-Werkzeug aktiv ist (klont von einer Quelle).
        Public ReadOnly Property IsCloneMode As Boolean
            Get
                Return _currentTool = EditorTool.Retouch AndAlso _isCloneMode
            End Get
        End Property

        Public ReadOnly Property IsRepairMode As Boolean
            Get
                Return _currentTool = EditorTool.Retouch AndAlso _isRepairMode AndAlso Not _isCloneMode
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

        ''' <summary>Bei einem neuen, nie gespeicherten Bild bleibt der Filmstreifen AUS: er zeigte
        ''' nur die eigene Temp-Datei („1 / 1"), und jede Navigation darin verwürfe das Dokument.
        ''' Die Einstellung des Nutzers wird dabei nicht verändert - sie greift wieder, sobald ein
        ''' echtes Bild geöffnet ist.</summary>
        Public ReadOnly Property ShowFilmstrip As Boolean
            Get
                If _isNewDocument Then Return False
                Return _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing AndAlso _mainVm.Settings.EditorShowFilmstrip
            End Get
        End Property

        ''' <summary>Solange ein neues Bild offen ist, lässt sich der Filmstreifen nicht einschalten.</summary>
        Public ReadOnly Property CanToggleFilmstrip As Boolean
            Get
                Return Not _isNewDocument
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

        ''' <summary>Ob das Ebenen-Panel rechts eingeblendet ist. Umschaltbar wie die Info-Leiste
        ''' (ToggleLayersPanelCommand), gemerkt in den Einstellungen (EditorLayersPanelExpanded).</summary>
        Public ReadOnly Property IsLayersPanelVisible As Boolean
            Get
                Return _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing AndAlso _mainVm.Settings.EditorLayersPanelExpanded
            End Get
        End Property

        Public Property CurrentImagePath As String
            Get
                Return _currentImagePath
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_currentImagePath, value)
                Me.RaisePropertyChanged(NameOf(CurrentFileName))
                Me.RaisePropertyChanged(NameOf(IsRawDeveloped))
                Me.RaisePropertyChanged(NameOf(RawFooterTooltip))
                Me.RaisePropertyChanged(NameOf(IsCurrentImageRaw))
                Me.RaisePropertyChanged(NameOf(IsCurrentImagePsd))
                Me.RaisePropertyChanged(NameOf(CanSaveSidecar))
                Me.RaisePropertyChanged(NameOf(CanSaveInPlace))
                Me.RaisePropertyChanged(NameOf(TransparencyBackgroundBrush))
                Me.RaisePropertyChanged(NameOf(HasDocument))
            End Set
        End Property

        ''' <summary>Die Datei, die die Pipeline tatsächlich dekodiert: bei einem .fpx das entpackte Basisbild,
        ''' sonst identisch zu _currentImagePath. Alle Vorschau-/Render-/Speicher-/Histogramm-Pfade lesen HIER,
        ''' die Identität (Filmstreifen/Metadaten/Navigation) bleibt _currentImagePath.</summary>
        Private ReadOnly Property RenderSourcePath As String
            Get
                Return If(String.IsNullOrEmpty(_renderSourcePathOverride), _currentImagePath, _renderSourcePathOverride)
            End Get
        End Property

        Private Shared Function LoadFpxCompositePreview(fpxPath As String) As Bitmap
            If String.IsNullOrWhiteSpace(fpxPath) Then Return Nothing
            Try
                Using preview = FpxService.ExtractComposite(fpxPath)
                    Return If(preview IsNot Nothing, New Bitmap(preview), Nothing)
                End Using
            Catch
                Return Nothing
            End Try
        End Function

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
                RaiseImageGeometryDependentProperties()
                If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, value) Then DisposeDeferred(previous)
            End Set
        End Property

        Private Sub RaiseImageGeometryDependentProperties()
            Me.RaisePropertyChanged(NameOf(EffectiveImageWidthPixels))
            Me.RaisePropertyChanged(NameOf(EffectiveImageHeightPixels))
            RaiseCropPropertiesChanged()
            Me.RaisePropertyChanged(NameOf(ResizeWidth))
            Me.RaisePropertyChanged(NameOf(ResizeHeight))
            Me.RaisePropertyChanged(NameOf(CanvasWidth))
            Me.RaisePropertyChanged(NameOf(CanvasHeight))
            Me.RaisePropertyChanged(NameOf(OutputSizeText))
            Me.RaisePropertyChanged(NameOf(AnnotationXPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationYPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
            Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
        End Sub

        Public Property PreviewImage As Bitmap
            Get
                Return _previewImage
            End Get
            Set(value As Bitmap)
                Dim previous = _previewImage
                ' Zeigt PreviewImage kuenftig NICHT mehr auf die Szene-Anzeige (z.B. Nothing beim
                ' Bildwechsel, FPX-Vorschau), wird die alte Instanz unten disposed - das Feld
                ' _sceneDisplay MUSS dann mit, sonst greift der naechste Region-Apply auf ein
                ' dispostes Bitmap zu (Absturz: ObjectDisposedException in EnsureSceneDisplay).
                If _sceneDisplay IsNot Nothing AndAlso Not Object.ReferenceEquals(value, _sceneDisplay) Then
                    _sceneDisplay = Nothing
                End If
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

        Public Property RetouchLivePatchImage As Bitmap
            Get
                Return _retouchLivePatchImage
            End Get
            Private Set(value As Bitmap)
                Dim previous = _retouchLivePatchImage
                Me.RaiseAndSetIfChanged(_retouchLivePatchImage, value)
                Me.RaisePropertyChanged(NameOf(HasRetouchLivePatch))
                If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, value) Then DisposeDeferred(previous)
            End Set
        End Property

        Public ReadOnly Property HasRetouchLivePatch As Boolean
            Get
                Return _retouchLivePatchImage IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property RetouchLivePatchLeftPercent As Double
            Get
                Return _retouchLivePatchLeftPercent
            End Get
        End Property

        Public ReadOnly Property RetouchLivePatchTopPercent As Double
            Get
                Return _retouchLivePatchTopPercent
            End Get
        End Property

        Public ReadOnly Property RetouchLivePatchWidthPercent As Double
            Get
                Return _retouchLivePatchWidthPercent
            End Get
        End Property

        Public ReadOnly Property RetouchLivePatchHeightPercent As Double
            Get
                Return _retouchLivePatchHeightPercent
            End Get
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
                Dim hasTransparentEditorOutput = HasTransparentEraserOutput() OrElse _backgroundHidden OrElse Not String.IsNullOrEmpty(_currentFpxPath)
                If Not hasTransparentEditorOutput AndAlso Not TransparencyBrushService.CanHaveTransparency(RenderSourcePath) Then
                    Return Avalonia.Media.Brushes.Transparent
                End If
                If Not hasTransparentEditorOutput Then
                    ' Alpha-Scan im HINTERGRUND (frueher Volldekode im Binding-Getter = UI-Haenger
                    ' bei grossen PNGs); solange unbekannt, erst mal kein Schachbrett - der Callback
                    ' zieht den Brush nach, sobald das Ergebnis vorliegt.
                    Dim hasTransparency As Boolean = False
                    If Not TransparencyBrushService.TryGetTransparency(RenderSourcePath, hasTransparency,
                            Sub() Me.RaisePropertyChanged(NameOf(TransparencyBackgroundBrush))) Then
                        Return Avalonia.Media.Brushes.Transparent
                    End If
                    If Not hasTransparency Then Return Avalonia.Media.Brushes.Transparent
                End If
                Dim settings = AppSettingsService.Load()
                Return TransparencyBrushService.GetBrush(settings.TransparencyBackgroundMode, settings.TransparencyBackgroundColor)
            End Get
        End Property

        Private Function HasTransparentEraserOutput() As Boolean
            If _currentTool = EditorTool.Draw AndAlso _isEraserMode AndAlso EraserFillColorValue.A < 250 Then Return True
            ' ARBEITSBILD (Stufe D): Radierer-Löcher stecken im Arbeitsbild selbst (DstOut beim
            ' Region-Commit) - der Service merkt sich das; die alte Strich-Listen-Abfrage entfällt.
            Return _workingImage.HasAlphaHoles
        End Function

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
                ' Beim Ausschalten die „Vorher"-Originalquelle freigeben (~40 MB Vorschau-Bitmap).
                If wasShowing AndAlso Not value Then
                    ReleaseComparisonOriginalSource()
                    TryDisposeStalePreviewSources()
                End If
            End Set
        End Property

        ''' <summary>Der Vergleich ist ein Bedienzustand, den der Nutzer setzt - und der über Bildwechsel
        ''' UND Programmstarts hinweg stehen bleiben soll (gemerkt in den Einstellungen, wie die Info-Leiste;
        ''' kein Schalter im Einstellungsdialog).</summary>
        Public Sub SetComparisonVisibleFromUser(value As Boolean)
            If _comparisonAutoEnabled <> value Then AppSettingsService.SaveEditorShowComparison(value)
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
                       _currentTool <> EditorTool.Move AndAlso
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
                If value = EditorTool.Frame Then value = EditorTool.Effects
                Dim previousTool = _currentTool
                Me.RaiseAndSetIfChanged(_currentTool, value)
                If previousTool <> value Then
                    DiscardUncommittedToolEdits(previousTool)
                    ' Werkzeugwechsel beendet eine laufende Pinsel-/Radiergummi-Mal-Sitzung (siehe
                    ' AddBrushStroke) - auch zwischen zwei Ebenen-Werkzeugen (Draw -> Text usw.), wo
                    ' SelectedAnnotationIndex sonst unverändert bliebe.
                    _pixelEditLayer.ResetActiveStroke()
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
                Me.RaisePropertyChanged(NameOf(ShowSelectedSvgOverlay))
                RaiseToolContextProperties()
                ' Werkzeugwechsel kann das Ziel der Regler umschalten (Objekt <-> Bild).
                RefreshObjectAdjustMode()
                RequestOverlayStateNotify()
                ' STEMPEL-LIVE: Live-Puffer (Ziel + Sample, 2 Pipeline-Renders)
                ' schon beim Werkzeugwechsel asynchron vorwaermen - erst beim ersten Spot gebaut,
                ' war ein kurzer Zug vorbei, bevor sie landeten (Aenderung erst nach dem Commit
                ' sichtbar).
                If value = EditorTool.Retouch AndAlso previousTool <> EditorTool.Retouch Then
                    BeginRetouchLiveBuffersAsync()
                End If
            End Set
        End Property

        ''' <summary>
        ''' Beschriftet den ersten Tab des rechten Panels. Die Namen sind wörtlich die der Werkzeugleiste.
        ''' </summary>
        Public ReadOnly Property CurrentToolLabel As String
            Get
                Select Case _currentTool
                    Case EditorTool.Crop : Return "Zuschneiden"
                    Case EditorTool.Resize : Return "Bildgröße"
                    Case EditorTool.Rotate : Return "Drehen"
                    Case EditorTool.Adjust : Return "Anpassen"
                    Case EditorTool.Color : Return "Farbe"
                    Case EditorTool.Effects, EditorTool.Frame : Return "Details und Effekte"
                    Case EditorTool.Filters : Return "Filter"
                    Case EditorTool.Transform : Return "Transformieren"
                    Case EditorTool.Move : Return "Verschieben"
                    Case EditorTool.Selection : Return "Auswahl"
                    Case EditorTool.Retouch : Return If(_isCloneMode, "Stempel", If(_isRepairMode, "Reparaturpinsel", "Verwischen"))
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
                    Case EditorTool.Move : Return base & "pointer.svg"
                    Case EditorTool.Selection : Return base & "rectangle.svg"
                    Case EditorTool.Retouch : Return base & If(_isCloneMode, "rubber-stamp.svg", If(_isRepairMode, "bandage.svg", "blur.svg"))
                    Case EditorTool.Draw : Return base & If(_isEraserMode, "eraser.svg", "brush.svg")
                    Case EditorTool.Geometry, EditorTool.Insert : Return base & "shape.svg"
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
                Return False
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
                Return _currentTool = EditorTool.Draw OrElse _currentTool = EditorTool.Retouch OrElse IsSelectedStrokeAnnotation()
            End Get
        End Property

        Public ReadOnly Property ShowLayerToolOptions As Boolean
            Get
                If IsSelectedStrokeAnnotation() Then Return False
                Return _currentTool = EditorTool.Text OrElse _currentTool = EditorTool.Geometry OrElse
                       _currentTool = EditorTool.Insert OrElse (_currentTool = EditorTool.Move AndAlso HasSelectedAnnotation)
            End Get
        End Property


        Public ReadOnly Property ShowGeometryControls As Boolean
            Get
                Return _currentTool = EditorTool.Geometry OrElse _currentTool = EditorTool.Insert
            End Get
        End Property

        Public ReadOnly Property ShowShapeControls As Boolean
            Get
                Return ShowGeometryControls
            End Get
        End Property

        Public ReadOnly Property ShowSymbolControls As Boolean
            Get
                Return ShowGeometryControls
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
                SchedulePreviewForCurrentTarget()
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
                SchedulePreviewForCurrentTarget()
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
                SchedulePreviewForCurrentTarget()
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

        ''' Farb-Rauschreduzierung (Chroma): glaettet nur die Farbanteile, Helligkeit bleibt.
        Public Property ColorNoiseReduction As Double
            Get
                Return _colorNoiseReduction
            End Get
            Set(value As Double)
                SetUndoableDouble(_colorNoiseReduction, Math.Max(0, Math.Min(100, value)), NameOf(ColorNoiseReduction))
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
                SetUndoableDouble(_addNoise, Math.Max(-100, Math.Min(100, value)), NameOf(AddNoise))
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
                Me.RaiseAndSetIfChanged(_retouchRadius, Math.Max(1, Math.Min(2000, value)))
                RaiseResetButtonStateChanged()
                ' KEIN SchedulePreviewUpdate (Log 23:16): Der Radius ist ein
                ' WERKZEUG-Parameter fuer KUENFTIGE Punkte - am Bild aendert er nichts. Der
                ' Regler-Zug loeste frueher pro Tick einen Full-Render aus (Dutzende Renders,
                ' CPU hoch, Dauerstatus "Vorschau wird berechnet").
            End Set
        End Property

        Public ReadOnly Property HasCloneSource As Boolean
            Get
                Return _cloneSourceXPercent >= 0 AndAlso _cloneSourceYPercent >= 0
            End Get
        End Property

        Public ReadOnly Property RetouchHintText As String
            Get
                If IsRepairMode Then Return "Repariert aus der Umgebung und blendet die ersetzte Textur weich ein."
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
            ' Alt+Klick kuendigt einen Stempel-Zug an: Live-Puffer vorwaermen (siehe CurrentTool).
            BeginRetouchLiveBuffersAsync()
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
                Me.RaiseAndSetIfChanged(_brushSize, Math.Max(1, Math.Min(2000, value)))
                SyncSelectedAnnotationIfStroke()
                RaiseResetButtonStateChanged()
            End Set
        End Property

        Public Property BrushHardness As Double
            Get
                Return _brushHardness
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_brushHardness, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotationIfStroke()
                RaiseResetButtonStateChanged()
            End Set
        End Property

        Public Property BrushOpacity As Double
            Get
                Return _brushOpacity
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_brushOpacity, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotationIfStroke()
                RaiseResetButtonStateChanged()
            End Set
        End Property

        Public Property BrushFlow As Double
            Get
                Return _brushFlow
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_brushFlow, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotationIfStroke()
                RaiseResetButtonStateChanged()
            End Set
        End Property

        ''' <summary>Die Pinsel-Varianten für den visuellen Picker (Stufe 2). Lazy erzeugt, weil jede
        ''' Kachel einen echten Beispielstrich rendert - so entspricht die Vorschau exakt dem Ergebnis.</summary>
        Public ReadOnly Property BrushPresets As System.Collections.ObjectModel.ObservableCollection(Of BrushPresetItem)
            Get
                If _brushPresets Is Nothing Then _brushPresets = BuildBrushPresetItems()
                Return _brushPresets
            End Get
        End Property

        ' Key -> deutscher Ausgangs-Anzeigename (wird über LocalizationService.T() übersetzt).
        ' Reihenfolge = Reihenfolge im Picker.
        Private Shared ReadOnly _brushPresetLabels As (Key As String, Label As String)() = {
            ("soft", "Rund weich"),
            ("pencil", "Bleistift"),
            ("marker", "Marker"),
            ("acrylic", "Acryl körnig"),
            ("sandpaper", "Sandpapier"),
            ("smear", "Schmieren"),
            ("spatter", "Farbkleckse"),
            ("charcoal", "Kohle"),
            ("crayon", "Wachsmalstift"),
            ("airbrush", "Sprühdose"),
            ("calligraphy", "Kalligrafie"),
            ("stipple", "Punktraster"),
            ("watercolor", "Aquarell")
        }

        Private Function BuildBrushPresetItems() As System.Collections.ObjectModel.ObservableCollection(Of BrushPresetItem)
            Dim items = New System.Collections.ObjectModel.ObservableCollection(Of BrushPresetItem)()
            ' Helles Grau auf transparentem Grund - lesbar auf dem dunklen Panel, wie in Pinsel-Bibliotheken.
            Dim previewColor = New SKColor(225, 225, 225, 255)
            For Each entry In _brushPresetLabels
                Dim preview As Avalonia.Media.Imaging.Bitmap
                Using sk = ImageProcessor.RenderBrushStrokePreview(entry.Key, 200, 46, previewColor)
                    preview = ImageProcessor.ToAvaloniaBitmap(sk)
                End Using
                items.Add(New BrushPresetItem(entry.Key, LocalizationService.T(entry.Label), preview) With {.IsSelected = entry.Key = _brushPreset})
            Next
            Return items
        End Function

        Private Sub SelectBrushPreset(key As String)
            Dim normalized = If(String.IsNullOrWhiteSpace(key), "soft", key.Trim().ToLowerInvariant())
            If Array.IndexOf(ImageProcessor.BrushPresetKeys, normalized) < 0 Then normalized = "soft"
            _brushPreset = normalized
            ' Eine neue Variante beginnt einen neuen Raster-Paint-Eintrag (siehe AddBrushStroke).
            _pixelEditLayer.ResetActiveStroke()
            If _brushPresets IsNot Nothing Then
                For Each item In _brushPresets
                    item.IsSelected = String.Equals(item.Key, normalized, StringComparison.Ordinal)
                Next
            End If
            SyncSelectedAnnotationIfStroke()
        End Sub

        Public Property IsEraserMode As Boolean
            Get
                Return _isEraserMode
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isEraserMode, value)
                ' Umschalten zwischen Pinsel und Radiergummi beendet die laufende Mal-Sitzung (siehe
                ' AddBrushStroke) - der nächste Strich landet in einem neuen Raster-Paint-Eintrag.
                _pixelEditLayer.ResetActiveStroke()
                Me.RaisePropertyChanged(NameOf(CurrentToolLabel))
                Me.RaisePropertyChanged(NameOf(CurrentToolIconSource))
                Me.RaisePropertyChanged(NameOf(IsBrushPaintMode))
                Me.RaisePropertyChanged(NameOf(ShowBrushStrokeAdjustments))
                Me.RaisePropertyChanged(NameOf(IsEraserPaintMode))
                Me.RaisePropertyChanged(NameOf(IsSmudgePaintMode))
                Me.RaisePropertyChanged(NameOf(SelectedPaintMode))
                Me.RaisePropertyChanged(NameOf(TransparencyBackgroundBrush))
            End Set
        End Property

        Public ReadOnly Property IsBrushPaintMode As Boolean
            Get
                Return IsSelectedBrushAnnotation() OrElse (_currentTool = EditorTool.Draw AndAlso Not _isEraserMode)
            End Get
        End Property

        ''' Größe, Härte und Deckkraft gelten für Pinsel UND Radiergummi - beide legen denselben
        ''' Strich an (siehe AppendBrushStroke), nur die Farbe braucht der Radiergummi nicht.
        Public ReadOnly Property ShowBrushStrokeAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Draw OrElse IsSelectedStrokeAnnotation()
            End Get
        End Property

        Public ReadOnly Property IsEraserPaintMode As Boolean
            Get
                Return IsSelectedEraserAnnotation() OrElse (_currentTool = EditorTool.Draw AndAlso _isEraserMode)
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
                SchedulePreviewForCurrentTarget()
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
                SchedulePreviewForCurrentTarget()
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
                        Return LocalizationService.T("Nächstgelegen")
                    Case ResizeInterpolationMode.Bilinear
                        Return LocalizationService.T("Bilinear")
                    Case Else
                        Return LocalizationService.T("Bikubisch")
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

        Public Property EraserFillColor As String
            Get
                Return _eraserFillColor
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_eraserFillColor, NormalizeAvaloniaColor(value, "#00FFFFFF"))
                Me.RaisePropertyChanged(NameOf(EraserFillColorValue))
                Me.RaisePropertyChanged(NameOf(EraserFillBrush))
                Me.RaisePropertyChanged(NameOf(TransparencyBackgroundBrush))
                _pixelEditLayer.ResetActiveStroke()
                SyncSelectedAnnotationIfStroke()
            End Set
        End Property

        Public Property EraserFillColorValue As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_eraserFillColor, Avalonia.Media.Colors.Transparent)
            End Get
            Set(value As Avalonia.Media.Color)
                EraserFillColor = value.ToString()
            End Set
        End Property

        Public ReadOnly Property AnnotationStrokeBrush As Avalonia.Media.IBrush
            Get
                Return New Avalonia.Media.SolidColorBrush(AnnotationStrokeColorValue)
            End Get
        End Property

        Public ReadOnly Property EraserFillBrush As Avalonia.Media.IBrush
            Get
                Return New Avalonia.Media.SolidColorBrush(EraserFillColorValue)
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

        Public ReadOnly Property AnnotationBlendModeOptions As IReadOnlyList(Of AnnotationBlendModeOption)
            Get
                Return _annotationBlendModeOptions
            End Get
        End Property

        Public Property AnnotationBlendMode As String
            Get
                Return _annotationBlendMode
            End Get
            Set(value As String)
                Dim normalized = NormalizeAnnotationBlendMode(value)
                If String.Equals(_annotationBlendMode, normalized, StringComparison.Ordinal) Then Return
                Dim wasBakedOnly = HasSelectedAnnotation AndAlso
                                   Not String.Equals(If(_annotationBlendMode, "Normal").Trim(), "Normal", StringComparison.OrdinalIgnoreCase)
                Dim willBeBakedOnly = HasSelectedAnnotation AndAlso
                                     Not String.Equals(normalized, "Normal", StringComparison.OrdinalIgnoreCase)
                _annotationBlendMode = normalized
                Me.RaisePropertyChanged(NameOf(AnnotationBlendMode))
                Me.RaisePropertyChanged(NameOf(SelectedAnnotationBlendModeOption))
                Me.RaisePropertyChanged(NameOf(ShowSelectedSvgOverlay))
                SyncSelectedAnnotation()
                If wasBakedOnly <> willBeBakedOnly Then RequestOverlayStateNotify()
            End Set
        End Property

        Public Property SelectedAnnotationBlendModeOption As AnnotationBlendModeOption
            Get
                Return _annotationBlendModeOptions.FirstOrDefault(Function(o) String.Equals(o.Key, _annotationBlendMode, StringComparison.Ordinal))
            End Get
            Set(value As AnnotationBlendModeOption)
                AnnotationBlendMode = If(value?.Key, "Normal")
            End Set
        End Property

        ''' <summary>Sichtbarkeit der Hintergrund-Ebene (Basisbild) im Ebenen-Panel. True = sichtbar. Das
        ''' Auge in der Hintergrundzeile bindet hierauf; Umschalten läuft über ToggleBackgroundVisibilityCommand.</summary>
        Public ReadOnly Property IsBackgroundVisible As Boolean
            Get
                Return Not _backgroundHidden
            End Get
        End Property

        ''' <summary>Sichtbarkeit der Pixel-Ebene "Retusche und Pinsel". Retusche, Striche und gerasterte
        ''' Ebenen liegen alle in EINEM Arbeitsbild (siehe WorkingImageService) und lassen sich deshalb nur
        ''' gemeinsam ein-/ausblenden. True = sichtbar.</summary>
        Public ReadOnly Property IsPixelLayerVisible As Boolean
            Get
                Return Not _pixelLayerHidden
            End Get
        End Property

        ''' <summary>Solange die Pixel-Ebene ausgeblendet ist, sind Pinsel, Radiergummi, Verwischen,
        ''' Stempel und Reparaturpinsel gesperrt (Nutzerentscheidung 2026-07-19, wie das Malen auf einer
        ''' unsichtbaren Ebene in ueblichen Bildbearbeitungen). Sonst liefen die Commits in ein
        ''' Arbeitsbild, das gerade gar nicht angezeigt wird.
        ''' Dieselbe Sperre greift, solange nur die eingebettete RAW-Vorschau steht und die echte
        ''' Entwicklung noch laeuft - dann gibt es das Arbeitsbild schlicht noch nicht.</summary>
        Public ReadOnly Property CanUsePixelTools As Boolean
            Get
                Return Not _pixelLayerHidden AndAlso Not _workingImagePending
            End Get
        End Property

        ''' <summary>Rastern backt ins Arbeitsbild - bei ausgeblendeter Pixel-Ebene gesperrt.</summary>
        Public ReadOnly Property CanRasterizeSelectedAnnotation As Boolean
            Get
                Return HasSelectedAnnotation AndAlso CanUsePixelTools
            End Get
        End Property

        Public ReadOnly Property PixelToolsLockedHint As String
            Get
                If _workingImagePending Then Return LocalizationService.T("RAW wird entwickelt …")
                Return If(_pixelLayerHidden, LocalizationService.T("Ebene ausgeblendet"), "")
            End Get
        End Property

        Public Property AnnotationRotation As Double
            Get
                Return _annotationRotation
            End Get
            Set(value As Double)
                ' Nur 1 Nachkommastelle: der Slider-Drag liefert sonst beliebig krumme Gradwerte.
                Me.RaiseAndSetIfChanged(_annotationRotation, Math.Round(Math.Max(-180, Math.Min(180, value)), 1))
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
                ' refreshOverlay:=True - anders als die Drehung (die legt die View als Transformation über das
                ' Overlay) muss die Spiegelung in das Overlay-Bitmap hineingerendert werden.
                SyncSelectedAnnotation(refreshOverlay:=True)
            End Set
        End Property

        ''' "Seitenverhältnis beibehalten" beim Grössenziehen von Bild-Objekten und
        ''' Wasserzeichen-Bildern (wie bei Bildgrösse) - wird pro Objekt gespeichert.
        Public Property AnnotationLockAspect As Boolean
            Get
                Return _annotationLockAspect
            End Get
            Set(value As Boolean)
                Dim turnedOn = value AndAlso Not _annotationLockAspect
                Me.RaiseAndSetIfChanged(_annotationLockAspect, value)
                ' refreshOverlay:=True - das Flag steuert auch das Zeichnen (ohne Sperre wird das
                ' Bild auf die Box gestreckt statt uniform eingepasst), nicht nur das Ziehen.
                SyncSelectedAnnotation(refreshOverlay:=True)
                If turnedOn Then SnapAnnotationBoxToImageAspect()
            End Set
        End Property

        ''' Beim AKTIVIEREN der Seitenverhaeltnis-Sperre die Objekt-Box auf den Bereich schrumpfen,
        ''' den das uniform eingepasste Bild tatsaechlich belegt - sonst staenden Rahmen und Bild
        ''' nicht mehr deckungsgleich. Bild-Objekte behalten dabei ihre sichtbare Position (das
        ''' Fit-Rechteck liegt zentriert in der Box); beim verankerten Wasserzeichen bleiben die
        ''' Anker-Offsets unangetastet, dort haelt der Anker die Lage.
        Private Sub SnapAnnotationBoxToImageAspect()
            If Not ShowAnnotationAspectLock Then Return
            Dim path = SelectedAnnotationImagePath
            If String.IsNullOrWhiteSpace(path) Then Return
            Dim size = ImageProcessor.GetImageSize(path)
            If size.Width <= 0 OrElse size.Height <= 0 Then Return

            Dim boxW = CDbl(AnnotationWidthPixels)
            Dim boxH = CDbl(AnnotationHeightPixels)
            If boxW <= 0 OrElse boxH <= 0 Then Return

            Dim imageAspect = size.Width / CDbl(size.Height)
            Dim newW = boxW
            Dim newH = boxH
            If boxW / boxH > imageAspect Then
                newW = boxH * imageAspect
            Else
                newH = boxW / imageAspect
            End If
            If Math.Abs(newW - boxW) < 0.5 AndAlso Math.Abs(newH - boxH) < 0.5 Then Return

            If Not IsWatermarkImageSource Then
                AnnotationXPixels = CInt(Math.Round(AnnotationXPixels + (boxW - newW) / 2.0))
                AnnotationYPixels = CInt(Math.Round(AnnotationYPixels + (boxH - newH) / 2.0))
            End If
            AnnotationWidthPixels = CInt(Math.Round(newW))
            AnnotationHeightPixels = CInt(Math.Round(newH))
        End Sub

        ''' Sichtbarkeit der Checkbox: nur wo Verzerren real droht - Bild-Objekte und
        ''' Wasserzeichen mit Bilddatei (QR bleibt hart 1:1, Formen/Text duerfen frei).
        Public ReadOnly Property ShowAnnotationAspectLock As Boolean
            Get
                Return String.Equals(EffectiveAnnotationKind, "Image", StringComparison.OrdinalIgnoreCase) OrElse
                       IsWatermarkImageSource
            End Get
        End Property

        Public Property AnnotationFlipVertical As Boolean
            Get
                Return _annotationFlipV
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationFlipV, value)
                SyncSelectedAnnotation(refreshOverlay:=True)
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
                Dim isTextual = IsTextualAnnotationKind(EffectiveAnnotationKind) AndAlso Not IsWatermarkImageSource
                Dim minWidth = If(isTextual, MinTextAnnotationWidthPercent, 5.0)
                Me.RaiseAndSetIfChanged(_annotationWidthPercent, Math.Max(minWidth, Math.Min(90, value)))
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
                Dim isTextual = IsTextualAnnotationKind(EffectiveAnnotationKind) AndAlso Not IsWatermarkImageSource
                Dim minHeight = If(isTextual, MinTextAnnotationHeightPercent, 4.0)
                Me.RaiseAndSetIfChanged(_annotationHeightPercent, Math.Max(minHeight, Math.Min(90, value)))
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

        ''' <summary>Pfadform des Text-Objekts: "" (gerade), "Arc", "Circle", "Wave".</summary>
        Public Property AnnotationTextPathKind As String
            Get
                Return _annotationTextPathKind
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_annotationTextPathKind, If(value, ""))
                Me.RaisePropertyChanged(NameOf(ShowTextPathControls))
                Me.RaisePropertyChanged(NameOf(IsTextPathNone))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationTextPathBend As Double
            Get
                Return _annotationTextPathBend
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationTextPathBend, Math.Max(-100, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationTextPathStartOffset As Double
            Get
                Return _annotationTextPathStartOffset
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationTextPathStartOffset, Math.Max(0, Math.Min(100, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        ''' <summary>Zeichenabstand in Prozent der Schriftgroesse. Prozent statt Pixel, damit der
        ''' Abstand beim Skalieren des Objekts mitwaechst.</summary>

        ''' KAMERAKALIBRIERUNG: dreht und saettigt die Primaerfarben, wirkt vor Weissabgleich und
        ''' Saettigung. Macht einen guten Teil des Farbstichs importierter Presets aus.
        Public Property CalibrationRedHue As Double
            Get
                Return _calibrationRedHue
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_calibrationRedHue, Math.Max(-100, Math.Min(100, value)))
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property CalibrationRedSaturation As Double
            Get
                Return _calibrationRedSaturation
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_calibrationRedSaturation, Math.Max(-100, Math.Min(100, value)))
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property CalibrationGreenHue As Double
            Get
                Return _calibrationGreenHue
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_calibrationGreenHue, Math.Max(-100, Math.Min(100, value)))
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property CalibrationGreenSaturation As Double
            Get
                Return _calibrationGreenSaturation
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_calibrationGreenSaturation, Math.Max(-100, Math.Min(100, value)))
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property CalibrationBlueHue As Double
            Get
                Return _calibrationBlueHue
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_calibrationBlueHue, Math.Max(-100, Math.Min(100, value)))
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property CalibrationBlueSaturation As Double
            Get
                Return _calibrationBlueSaturation
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_calibrationBlueSaturation, Math.Max(-100, Math.Min(100, value)))
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property CalibrationShadowTint As Double
            Get
                Return _calibrationShadowTint
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_calibrationShadowTint, Math.Max(-100, Math.Min(100, value)))
                SchedulePreviewUpdate()
            End Set
        End Property

        Public Property AnnotationLetterSpacingPercent As Double
            Get
                Return _annotationLetterSpacingPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_annotationLetterSpacingPercent, Math.Max(-20, Math.Min(200, value)))
                SyncSelectedAnnotation()
            End Set
        End Property

        ''' <summary>Fett/Kursiv des Text-Objekts. Wirkt nur, wenn die gewaehlte Schriftfamilie
        ''' einen solchen Schnitt mitbringt - Skia erzeugt keinen synthetischen.</summary>
        Public Property AnnotationBold As Boolean
            Get
                Return _annotationBold
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationBold, value)
                SyncSelectedAnnotation()
            End Set
        End Property

        Public Property AnnotationItalic As Boolean
            Get
                Return _annotationItalic
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_annotationItalic, value)
                SyncSelectedAnnotation()
            End Set
        End Property

        ''' <summary>Die Pfad-Zeile gilt fuer alles, was Text zeichnet - also auch fuer das
        ''' Wasserzeichen. Frueher war sie dort ausgeblendet, weil die Zeichenstelle die
        ''' Pfad-Parameter nicht durchreichte; der Renderer selbst konnte es immer schon
        ''' (Nutzerbefund 2026-07-20).
        ''' Ein Wasserzeichen mit BILD hat keinen Text und damit auch keinen Pfad.</summary>
        Public ReadOnly Property ShowTextPathRow As Boolean
            Get
                If EffectiveAnnotationKind = "Text" Then Return True
                Return EffectiveAnnotationKind = "Watermark" AndAlso String.IsNullOrWhiteSpace(SelectedAnnotationImagePath)
            End Get
        End Property

        ''' <summary>Kruemmungs-/Startregler nur, wenn ueberhaupt eine Pfadform gewaehlt ist.</summary>
        Public ReadOnly Property ShowTextPathControls As Boolean
            Get
                Return ShowTextPathRow AndAlso Not String.IsNullOrWhiteSpace(_annotationTextPathKind)
            End Get
        End Property

        Public ReadOnly Property IsTextPathNone As Boolean
            Get
                Return String.IsNullOrWhiteSpace(_annotationTextPathKind)
            End Get
        End Property

        ''' "None" kommt vom "Kein"-Knopf: ein leeres CommandParameter laesst sich in XAML nicht
        ''' ausdruecken, ohne den XAML-Compiler zu brechen (AVLN2000 bei ConverterParameter=).
        ''' <summary>True fuer beide Kreisformen. Bewusst StartsWith - "CircleInverted" ist
        ''' geometrisch derselbe Kreis, und ein Equals-Vergleich hat den invertierten Modus schon
        ''' einmal stillschweigend anders behandelt.</summary>
        Friend Shared Function IsCircleTextPath(kind As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(kind) AndAlso
                   kind.StartsWith("Circle", StringComparison.OrdinalIgnoreCase)
        End Function

        Public Sub SetAnnotationTextPathKind(kind As String)
            AnnotationTextPathKind = If(String.Equals(kind, "None", StringComparison.OrdinalIgnoreCase), "", If(kind, ""))
            ' Ein Kreis braucht eine quadratische Box, sonst steht der Selektionsrahmen weit um den
            ' Text herum: der Radius ist min(Breite, Hoehe), eine breite Textbox laesst also den
            ' groessten Teil des Rahmens leer (Nutzerbefund 2026-07-20, mit Bild).
            ' Die Alternative - den Kreis ueber das Rechteck strecken - ergaebe bei breiten Objekten
            ' eine flache Ellipse und damit faktisch einen Bogen; ausprobiert und verworfen.
            ' Box sofort an den Kreis anpassen. MakeAnnotationBoxSquare reicht dafuer NICHT:
            ' bei Textobjekten wird die Box laufend aus dem gemessenen Textkasten neu berechnet und
            ' das Quadrat sofort wieder ueberschrieben - deshalb FitBoxToCircleTextPath, das an
            ' genau diesen Stellen mitlaeuft.
            If IsCircleTextPath(AnnotationTextPathKind) Then
                FitBoxToCircleTextPath()
                Me.RaisePropertyChanged(NameOf(AnnotationWidthPixels))
                Me.RaisePropertyChanged(NameOf(AnnotationHeightPixels))
                SyncSelectedAnnotation()
            End If
        End Sub

        ''' <summary>Macht die Objektbox quadratisch (kleinere Seite gewinnt) und behaelt dabei den
        ''' Mittelpunkt, damit der Text nicht wegspringt.</summary>
        Private Sub MakeAnnotationBoxSquare()
            Dim breite = AnnotationWidthPixels
            Dim hoehe = AnnotationHeightPixels
            If breite <= 0 OrElse hoehe <= 0 OrElse breite = hoehe Then Return

            Dim seite = Math.Min(breite, hoehe)
            Dim mitteX = AnnotationXPixels + breite \ 2
            Dim mitteY = AnnotationYPixels + hoehe \ 2

            AnnotationWidthPixels = seite
            AnnotationHeightPixels = seite
            AnnotationXPixels = Math.Max(0, mitteX - seite \ 2)
            AnnotationYPixels = Math.Max(0, mitteY - seite \ 2)
        End Sub

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

        ''' Aktueller Auswahlmodus: "Move", "Rectangle", "Ellipse", "Lasso" oder "MagicWand". Steuert, wie
        ''' EditorView den Zeiger interpretiert (Auswahl verschieben, Rechteck aufziehen, Freihand zeichnen, klicken) und
        ''' welche Zusatzregler (Toleranz) sichtbar sind.
        Public Property SelectionMode As String
            Get
                Return _selectionMode
            End Get
            Set(value As String)
                Dim v = If(String.IsNullOrWhiteSpace(value), "Move", value)
                If _selectionMode = v Then Return
                Me.RaiseAndSetIfChanged(_selectionMode, v)
                Me.RaisePropertyChanged(NameOf(ShowMagicWandControls))
                Me.RaisePropertyChanged(NameOf(IsMoveSelectionMode))
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

        Public ReadOnly Property IsMoveSelectionMode As Boolean
            Get
                Return _selectionMode = "Move"
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

        ''' <summary>Weiche Kante der Auswahl in Bildpixeln. Wirkt auf Anpassungen innerhalb der Auswahl,
        ''' auf „Kopieren" und auf „Auswahl füllen" - die gespeicherte Maske bleibt pixelgenau, weich wird
        ''' erst das Ergebnis. Darum lässt sich der Wert jederzeit nachträglich ändern.</summary>
        Public Property SelectionFeather As Double
            Get
                Return _selectionFeather
            End Get
            Set(value As Double)
                Dim clamped = Math.Max(0, Math.Min(200, value))
                If Math.Abs(_selectionFeather - clamped) < 0.0001 Then Return
                CaptureUndoState("SelectionFeather")
                Me.RaiseAndSetIfChanged(_selectionFeather, clamped)
                SchedulePreviewUpdate()
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
            Using mask = ImageProcessor.BuildMagicWandMaskFromFile(RenderSourcePath, GetCurrentAdjustments(),
                                                                   seedX, seedY, CSng(_selectionTolerance / 100.0), bounds,
                                                                   workingFull:=CloneWorkingFullForRender())
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
            Dim placement As SKRectI
            Return CropSelectionToTempFile(placement)
        End Function

        ''' <param name="placementPx">Das Rechteck, an dem das Ergebnis im Bild sitzt. Mit weicher Kante ist
        ''' es GRÖSSER als die Auswahl - die Kante läuft nach außen aus, und der Ausschnitt muss den Platz
        ''' dafür mitbringen, sonst wäre die weiche Kante an der Auswahlgrenze abgeschnitten.</param>
        Private Function CropSelectionToTempFile(ByRef placementPx As SKRectI) As String
            If Not _hasActiveSelection OrElse String.IsNullOrWhiteSpace(_currentImagePath) Then Return Nothing
            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return Nothing

            Dim left = CInt(Math.Round(baseWidth * _selectionXPercent / 100.0))
            Dim top = CInt(Math.Round(baseHeight * _selectionYPercent / 100.0))
            Dim width = CInt(Math.Round(baseWidth * _selectionWidthPercent / 100.0))
            Dim height = CInt(Math.Round(baseHeight * _selectionHeightPercent / 100.0))
            If width <= 0 OrElse height <= 0 Then Return Nothing
            placementPx = New SKRectI(left, top, left + width, top + height)

            Dim tempPath = IO.Path.Combine(IO.Path.GetTempPath(), $"ferrumpix_selection_{Guid.NewGuid():N}.png")
            Dim adj = GetCurrentAdjustments()
            If _selectionMask IsNot Nothing OrElse _selectionFeather > 0.05 Then
                ' Maskierte Auswahl: unregelmäßige Auswahl immer, Rechtecke sobald eine weiche Kante aktiv ist.
                Dim ownsMask As Boolean
                Dim maskRect As SKRectI
                Dim mask = GetSelectionMaskForOutput(maskRect, ownsMask)
                Try
                    If mask Is Nothing Then Return Nothing
                    placementPx = maskRect
                    If Not ImageProcessor.ExtractRegionToFileMasked(RenderSourcePath, adj, maskRect, mask, tempPath,
                                                                    workingFull:=CloneWorkingFullForRender()) Then Return Nothing
                Finally
                    If ownsMask Then mask.Dispose()
                End Try
            Else
                If Not ImageProcessor.ExtractRegionToFile(RenderSourcePath, adj, placementPx, tempPath,
                                                          workingFull:=CloneWorkingFullForRender()) Then Return Nothing
            End If
            Return tempPath
        End Function

        ''' <summary>Die Maske, mit der ausgeschnitten oder gefüllt wird: ohne weiche Kante die gespeicherte
        ''' (harte, pixelgenaue) Maske selbst, mit weicher Kante eine weichgezeichnete, nach außen erweiterte
        ''' Kopie samt passendem Rechteck. <paramref name="ownsMask"/> sagt dem Aufrufer, ob er sie freigeben
        ''' muss - die gespeicherte Maske darf er NICHT freigeben.</summary>
        Private Function GetSelectionMaskForOutput(ByRef rectPx As SKRectI, ByRef ownsMask As Boolean) As SKBitmap
            ownsMask = False
            If _selectionMask IsNot Nothing Then
                rectPx = _selectionMaskRect
                If _selectionFeather <= 0.05 Then Return _selectionMask

                Dim expanded As SKRectI
                Dim feathered = ImageProcessor.BuildFeatheredMask(_selectionMask, _selectionMaskRect, CSng(_selectionFeather), expanded)
                If feathered Is Nothing Then Return _selectionMask
                ownsMask = True
                Return ClampOutputMaskToImage(feathered, expanded, rectPx)
            End If

            rectPx = SelectionRectPixels()
            If rectPx.Width <= 0 OrElse rectPx.Height <= 0 OrElse _selectionFeather <= 0.05 Then Return Nothing

            Using hardMask = CreateSolidMask(rectPx.Width, rectPx.Height)
                Dim expanded As SKRectI
                Dim feathered = ImageProcessor.BuildFeatheredMask(hardMask, rectPx, CSng(_selectionFeather), expanded)
                If feathered Is Nothing Then Return Nothing
                ownsMask = True
                Return ClampOutputMaskToImage(feathered, expanded, rectPx)
            End Using
        End Function

        Private Function ClampOutputMaskToImage(mask As SKBitmap, expandedRect As SKRectI, ByRef clampedRect As SKRectI) As SKBitmap
            clampedRect = expandedRect
            Dim bw = GetBaseWidth(), bh = GetBaseHeight()
            If mask Is Nothing OrElse bw <= 0 OrElse bh <= 0 Then Return mask

            Dim left = Math.Max(0, expandedRect.Left)
            Dim top = Math.Max(0, expandedRect.Top)
            Dim right = Math.Min(bw, expandedRect.Right)
            Dim bottom = Math.Min(bh, expandedRect.Bottom)
            If left = expandedRect.Left AndAlso top = expandedRect.Top AndAlso
               right = expandedRect.Right AndAlso bottom = expandedRect.Bottom Then
                Return mask
            End If

            Dim width = right - left, height = bottom - top
            If width <= 0 OrElse height <= 0 Then Return mask

            Dim cropped = New SKBitmap(width, height, SKColorType.Alpha8, SKAlphaType.Premul)
            Using canvas = New SKCanvas(cropped)
                canvas.Clear(SKColors.Transparent)
                Dim srcLeft = left - expandedRect.Left
                Dim srcTop = top - expandedRect.Top
                canvas.DrawBitmap(mask,
                                  New SKRect(srcLeft, srcTop, srcLeft + width, srcTop + height),
                                  New SKRect(0, 0, width, height))
            End Using
            mask.Dispose()
            clampedRect = New SKRectI(left, top, right, bottom)
            Return cropped
        End Function

        Private Function PixelRectToPercent(rectPx As SKRectI) As (X As Double, Y As Double, W As Double, H As Double)
            Dim bw = GetBaseWidth(), bh = GetBaseHeight()
            If bw <= 0 OrElse bh <= 0 Then Return (0, 0, 0, 0)
            Return (rectPx.Left * 100.0 / bw, rectPx.Top * 100.0 / bh,
                    rectPx.Width * 100.0 / bw, rectPx.Height * 100.0 / bh)
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
                .BlendMode = _annotationBlendMode,
                .RotationDegrees = CSng(_annotationRotation),
                .IsVisible = _annotationIsVisible
            }
            HardenAnnotationBuffersForNewObject()
            _annotations.Add(annotation)
            CurrentTool = EditorTool.Move
            SelectedAnnotationIndex = _annotations.Count - 1
            RaiseResetButtonStateChanged()
            RefreshPreviewImmediately()
        End Sub

        Public Sub CopySelectionToNewObject()
            Dim placement As SKRectI
            Dim tempPath = CropSelectionToTempFile(placement)
            If tempPath Is Nothing Then Return
            ' Das Objekt sitzt am AUSGESCHNITTENEN Rechteck - mit weicher Kante ist das größer als die
            ' Auswahl. An den Auswahlwerten platziert, würde der Ausschnitt gestaucht.
            Dim p = PixelRectToPercent(placement)
            AddSelectionImageAnnotationAt(tempPath, p.X, p.Y, p.W, p.H)
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
            Me.RaisePropertyChanged(NameOf(TransparencyBackgroundBrush))
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
            Dim placement As SKRectI
            Dim tempPath = CropSelectionToTempFile(placement)
            If tempPath Is Nothing Then Return Nothing
            Dim p = PixelRectToPercent(placement)
            _selectionClipboardPath = tempPath
            _selectionClipboardXPercent = p.X
            _selectionClipboardYPercent = p.Y
            _selectionClipboardWidthPercent = p.W
            _selectionClipboardHeightPercent = p.H
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

            ' Maskierte Auswahl: unregelmäßige Auswahl immer, Rechtecke sobald eine weiche Kante aktiv ist.
            If _selectionMask IsNot Nothing OrElse _selectionFeather > 0.05 Then
                Dim tempPath = IO.Path.Combine(IO.Path.GetTempPath(), $"ferrumpix_fill_{Guid.NewGuid():N}.png")
                Dim ownsMask As Boolean
                Dim maskRect As SKRectI
                Dim mask = GetSelectionMaskForOutput(maskRect, ownsMask)
                Try
                    If mask Is Nothing Then Return
                    If ImageProcessor.RenderMaskedFillToFile(mask, _annotationFillColor, _annotationFillKind,
                                                             _annotationFillColor2, CSng(_annotationGradientAngle),
                                                             _annotationGradientInverted, tempPath) Then
                        Dim p = PixelRectToPercent(maskRect)
                        AddSelectionImageAnnotationAt(tempPath, p.X, p.Y, p.W, p.H)
                        AddHistoryEntry("Auswahl gefüllt")
                    End If
                Finally
                    If ownsMask Then mask.Dispose()
                End Try
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
                .BlendMode = _annotationBlendMode,
                .IsVisible = True
            }
            HardenAnnotationBuffersForNewObject()
            _annotations.Add(annotation)
            CurrentTool = EditorTool.Move
            SelectedAnnotationIndex = _annotations.Count - 1
            RaiseResetButtonStateChanged()
            AddHistoryEntry("Auswahl gefüllt")
            RefreshPreviewImmediately()
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
                FitBoxToCircleTextPath()
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

        ''' <summary>Anzeige-Rechtecke (Prozent des Bildes) aller sichtbaren, NICHT selektierten
        ''' Objekte - die Anrast-Ziele der Ausricht-Hilfslinien beim Verschieben (Smart Guides:
        ''' Objekte passgenau an bereits gesetzten ausrichten, Nutzerwunsch 2026-07-17).
        ''' Verankerte Wasserzeichen bleiben außen vor: ihre effektive Lage folgt dem Anker,
        ''' nicht den gespeicherten XPixels.</summary>
        Public Function GetAnnotationSnapRectsPercent() As List(Of Avalonia.Rect)
            Dim result As New List(Of Avalonia.Rect)()
            Dim baseW = GetBaseWidth()
            Dim baseH = GetBaseHeight()
            If baseW <= 0 OrElse baseH <= 0 Then Return result
            For i = 0 To _annotations.Count - 1
                If i = _selectedAnnotationIndex Then Continue For
                Dim a = _annotations(i)
                If a Is Nothing OrElse Not a.IsVisible Then Continue For
                If Not String.IsNullOrEmpty(a.Anchor) Then Continue For
                Dim w = a.WidthPixels / CDbl(baseW) * 100.0
                Dim h = a.HeightPixels / CDbl(baseH) * 100.0
                If w <= 0 OrElse h <= 0 Then Continue For
                result.Add(New Avalonia.Rect(a.XPixels / CDbl(baseW) * 100.0,
                                             a.YPixels / CDbl(baseH) * 100.0, w, h))
            Next
            Return result
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
                ' Nur 1 Nachkommastelle - siehe AnnotationRotation.
                Dim clamped = Math.Round(Math.Max(-180, Math.Min(180, value)), 1)
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
                SchedulePreviewForCurrentTarget()
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

        ''' Farbetikett (Hex der Akzentfarben-Palette, "" = keins) - lokal in der Bibliotheks-DB;
        ''' bei Immich-Assets unter dem Pseudo-Pfad, damit die Galerie-Kachel denselben Eintrag sieht.
        Public Property ColorLabel As String
            Get
                Return _colorLabel
            End Get
            Set(value As String)
                Dim normalized = If(value, "")
                If String.Equals(_colorLabel, normalized, StringComparison.OrdinalIgnoreCase) Then Return
                _colorLabel = normalized
                RaiseColorLabelProperties()
                Dim immichAssetId = CurrentImmichAssetId()
                If immichAssetId IsNot Nothing Then
                    LibraryService.Instance.SetColorLabelForMany(
                        {ImmichService.MakePseudoPath(immichAssetId, _immichSourceFileName)}, normalized)
                ElseIf Not String.IsNullOrEmpty(_currentImagePath) Then
                    LibraryService.Instance.SetColorLabelForMany({_currentImagePath}, normalized)
                End If
            End Set
        End Property

        Private Sub RaiseColorLabelProperties()
            Me.RaisePropertyChanged(NameOf(ColorLabel))
            Me.RaisePropertyChanged(NameOf(IsColorLabelOrange))
            Me.RaisePropertyChanged(NameOf(IsColorLabelRed))
            Me.RaisePropertyChanged(NameOf(IsColorLabelPink))
            Me.RaisePropertyChanged(NameOf(IsColorLabelPurple))
            Me.RaisePropertyChanged(NameOf(IsColorLabelBlue))
            Me.RaisePropertyChanged(NameOf(IsColorLabelCyan))
            Me.RaisePropertyChanged(NameOf(IsColorLabelTeal))
            Me.RaisePropertyChanged(NameOf(IsColorLabelGreen))
            Me.RaisePropertyChanged(NameOf(HasColorLabel))
            Me.RaisePropertyChanged(NameOf(ColorLabelBrush))
        End Sub

        Public ReadOnly Property HasColorLabel As Boolean
            Get
                Return Not String.IsNullOrEmpty(_colorLabel)
            End Get
        End Property

        ''' Punkt in der Fussleiste vor dem Dateinamen (gleiche Darstellung wie die Galerie-Kachel).
        Public ReadOnly Property ColorLabelBrush As Avalonia.Media.IBrush
            Get
                If String.IsNullOrEmpty(_colorLabel) Then Return Avalonia.Media.Brushes.Transparent
                Try
                    Return New Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(_colorLabel))
                Catch
                    Return Avalonia.Media.Brushes.Transparent
                End Try
            End Get
        End Property

        Private Function IsColorLabelValue(hex As String) As Boolean
            Return String.Equals(_colorLabel, hex, StringComparison.OrdinalIgnoreCase)
        End Function

        Public ReadOnly Property IsColorLabelOrange As Boolean
            Get
                Return IsColorLabelValue("#F08A1A")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelRed As Boolean
            Get
                Return IsColorLabelValue("#E74C3C")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelPink As Boolean
            Get
                Return IsColorLabelValue("#F03B88")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelPurple As Boolean
            Get
                Return IsColorLabelValue("#8B5CF6")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelBlue As Boolean
            Get
                Return IsColorLabelValue("#3B82F6")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelCyan As Boolean
            Get
                Return IsColorLabelValue("#0891B2")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelTeal As Boolean
            Get
                Return IsColorLabelValue("#0F766E")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelGreen As Boolean
            Get
                Return IsColorLabelValue("#22C55E")
            End Get
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
                ' Ruhezustand JETZT festhalten, nicht später vergleichen: der gespeicherte Text ist
                ' bereits übersetzt, ein Vergleich zur Anzeigezeit wäre nach einem Sprachwechsel
                ' falsch. Leer zählt mit dazu - dort verdeckt die Mausposition nichts.
                _statusIsIdle = String.IsNullOrEmpty(value) OrElse
                                String.Equals(value, LocalizationService.T("Vorschau bereit"), StringComparison.Ordinal)
                Me.RaiseAndSetIfChanged(_statusText, value)
                RaiseFooterStatusChanged()
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
                RaiseFooterStatusChanged()
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
                RaiseFooterStatusChanged()
            End Set
        End Property

        ''' <summary>Was rechts unten in der Fußleiste steht: die Mausposition, solange der Zeiger über
        ''' dem Bild ist UND der Status im Ruhezustand ist. Jede echte Meldung - „wird aktualisiert",
        ''' „gespeichert als …", die Maße nach dem Laden, ein Fehler - hat Vorrang und bleibt stehen;
        ''' sie soll nicht ausgerechnet dann verschwinden, wenn man über das Bild fährt.</summary>
        Public ReadOnly Property FooterStatusText As String
            Get
                If _statusIsIdle AndAlso Not String.IsNullOrEmpty(_mousePositionText) Then Return _mousePositionText
                Return _statusText
            End Get
        End Property

        ''' <summary>Rot nur, wenn dort wirklich der Fehlerstatus steht - eine Mausposition darf nie
        ''' rot erscheinen, auch wenn die letzte Vorschau fehlgeschlagen ist.</summary>
        Public ReadOnly Property IsFooterStatusFailed As Boolean
            Get
                Return _previewFailed AndAlso Not IsShowingMousePosition
            End Get
        End Property

        Private ReadOnly Property IsShowingMousePosition As Boolean
            Get
                Return _statusIsIdle AndAlso Not String.IsNullOrEmpty(_mousePositionText)
            End Get
        End Property

        Private Sub RaiseFooterStatusChanged()
            Me.RaisePropertyChanged(NameOf(FooterStatusText))
            Me.RaisePropertyChanged(NameOf(IsFooterStatusFailed))
        End Sub

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

        ''' <summary>True, wenn das aktuelle Bild WIRKLICH ueber libraw entwickelt wurde (der
        ''' Entwicklungs-Cache ist nach dem Decode warm) - die Fusszeile faerbt den Dateinamen dann
        ''' in der Akzentfarbe, damit sichtbar ist, ob echte Sensordaten oder nur die eingebettete
        ''' Vorschau bearbeitet werden.</summary>
        Public ReadOnly Property IsRawDeveloped As Boolean
            Get
                Return RawPreviewService.IsSupportedRaw(RenderSourcePath) AndAlso
                       RawDecodeService.IsAvailable AndAlso
                       RawDecodeService.TryGetCachedSize(RenderSourcePath).Width > 0
            End Get
        End Property

        Public ReadOnly Property RawFooterTooltip As String
            Get
                If Not RawPreviewService.IsSupportedRaw(RenderSourcePath) Then Return Nothing
                Return If(IsRawDeveloped,
                          LocalizationService.T("RAW entwickelt"),
                          LocalizationService.T("RAW-Vorschau"))
            End Get
        End Property

        ''' <summary>False, solange der Editor gar kein Dokument hält - beim Start ohne Bilddatei oder
        ''' nachdem der Neu-Dialog abgebrochen wurde. Steuert den Platzhalter in EditorView; die
        ''' Werkzeugleisten und Panels liegen darunter und sind dann verdeckt.</summary>
        Public ReadOnly Property HasDocument As Boolean
            Get
                Return Not String.IsNullOrEmpty(_currentImagePath)
            End Get
        End Property

        ''' <summary>True, solange das Dokument nur im Temp-Ordner liegt und noch nie gespeichert wurde.
        ''' Blendet die Pfadzeile der Info-Seitenleiste aus - ein Temp-Pfad hilft niemandem.</summary>
        Public ReadOnly Property IsNewDocument As Boolean
            Get
                Return _isNewDocument
            End Get
        End Property

#Region "Neues Bild - Dialog"

        ' Der Dialogzustand sitzt am EditorViewModel und nicht am MainWindowViewModel: „Neues Bild" ist
        ' rein editorseitig. Der Druckdialog hängt nur deshalb am Fenster, weil er aus Galerie,
        ' Betrachter UND Editor erreichbar ist. Vorbild hier ist der Collage-Dialog am GalleryViewModel.
        Private _isNewDocumentDialogOpen As Boolean = False
        Private _newDocPresetId As String = "A4"
        Private _newDocWidth As Double = 210
        Private _newDocHeight As Double = 297
        Private _newDocUnit As String = "mm"
        Private _newDocDpi As Integer = 300
        Private _newDocIsLandscape As Boolean = False
        Private _newDocBackgroundMode As String = "White"
        Private _newDocBackgroundColor As String = "#FFFFFFFF"

        Public ReadOnly Property NewDocPhotoPresets As New ObservableCollection(Of NewDocPresetItem)(
            DocumentPresetService.ByGroup(DocumentPresetService.GroupPhoto).Select(Function(p) New NewDocPresetItem(p)))
        Public ReadOnly Property NewDocScreenPresets As New ObservableCollection(Of NewDocPresetItem)(
            DocumentPresetService.ByGroup(DocumentPresetService.GroupScreen).Select(Function(p) New NewDocPresetItem(p)))
        Public ReadOnly Property NewDocPaperPresets As New ObservableCollection(Of NewDocPresetItem)(
            DocumentPresetService.ByGroup(DocumentPresetService.GroupPaper).Select(Function(p) New NewDocPresetItem(p)))

        Private Iterator Function AllNewDocPresetItems() As IEnumerable(Of NewDocPresetItem)
            For Each item In NewDocPhotoPresets : Yield item : Next
            For Each item In NewDocScreenPresets : Yield item : Next
            For Each item In NewDocPaperPresets : Yield item : Next
        End Function

        Private Sub SyncNewDocPresetSelection()
            For Each item In AllNewDocPresetItems()
                item.IsSelected = String.Equals(item.Id, _newDocPresetId, StringComparison.Ordinal)
            Next
        End Sub

        Public ReadOnly Property NewDocUnitOptions As New ObservableCollection(Of String) From {"mm", "cm", "in", "px"}
        Public ReadOnly Property NewDocDpiOptions As New ObservableCollection(Of Integer) From {72, 150, 300, 600}

        Public Property IsNewDocumentDialogOpen As Boolean
            Get
                Return _isNewDocumentDialogOpen
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isNewDocumentDialogOpen, value)
            End Set
        End Property

        Public Property NewDocPresetId As String
            Get
                Return _newDocPresetId
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_newDocPresetId, value)
                Me.RaisePropertyChanged(NameOf(NewDocSelectedPresetId))
            End Set
        End Property

        ''' <summary>Die Kacheln vergleichen ihre Id hiermit (Konverter in der View). Eigene Maße
        ''' setzen die Id auf leer, damit keine Kachel mehr markiert ist.</summary>
        Public ReadOnly Property NewDocSelectedPresetId As String
            Get
                Return _newDocPresetId
            End Get
        End Property

        Public Property NewDocWidth As Double
            Get
                Return _newDocWidth
            End Get
            Set(value As Double)
                If value <= 0 Then Return
                Me.RaiseAndSetIfChanged(_newDocWidth, value)
                ClearNewDocPreset()
                RaiseNewDocumentPixelProperties()
            End Set
        End Property

        Public Property NewDocHeight As Double
            Get
                Return _newDocHeight
            End Get
            Set(value As Double)
                If value <= 0 Then Return
                Me.RaiseAndSetIfChanged(_newDocHeight, value)
                ClearNewDocPreset()
                RaiseNewDocumentPixelProperties()
            End Set
        End Property

        Public Property NewDocUnit As String
            Get
                Return _newDocUnit
            End Get
            Set(value As String)
                Dim normalized = DocumentPresetService.NormalizeUnit(value)
                If String.Equals(_newDocUnit, normalized, StringComparison.Ordinal) Then Return
                ' Die MASSE bleiben dieselbe Fläche - nur ihre Schreibweise wechselt. Also über Pixel
                ' umrechnen statt die Zahl stehen zu lassen (sonst würde aus 210 mm plötzlich 210 px).
                Dim wPx = NewDocPixelWidth
                Dim hPx = NewDocPixelHeight
                Me.RaiseAndSetIfChanged(_newDocUnit, normalized)
                _newDocWidth = DocumentPresetService.FromPixels(wPx, normalized, _newDocDpi)
                _newDocHeight = DocumentPresetService.FromPixels(hPx, normalized, _newDocDpi)
                Me.RaisePropertyChanged(NameOf(NewDocWidth))
                Me.RaisePropertyChanged(NameOf(NewDocHeight))
                RaiseNewDocumentUnitProperties()
                RaiseNewDocumentPixelProperties()
            End Set
        End Property

        Public Property NewDocDpi As Integer
            Get
                Return _newDocDpi
            End Get
            Set(value As Integer)
                If value <= 0 Then Return
                Me.RaiseAndSetIfChanged(_newDocDpi, value)
                ' Bei physischen Einheiten ändert eine andere Auflösung die PIXEL, nicht die Maße.
                ' Bei "px" ist es umgekehrt - dort bleibt die Pixelzahl und das physische Maß driftet.
                RaiseNewDocumentDpiProperties()
                RaiseNewDocumentPixelProperties()
            End Set
        End Property

        Public Property NewDocIsLandscape As Boolean
            Get
                Return _newDocIsLandscape
            End Get
            Set(value As Boolean)
                If _newDocIsLandscape = value Then Return
                Me.RaiseAndSetIfChanged(_newDocIsLandscape, value)
                Dim w = _newDocWidth
                _newDocWidth = _newDocHeight
                _newDocHeight = w
                Me.RaisePropertyChanged(NameOf(NewDocWidth))
                Me.RaisePropertyChanged(NameOf(NewDocHeight))
                Me.RaisePropertyChanged(NameOf(IsNewDocPortrait))
                RaiseNewDocumentPixelProperties()
            End Set
        End Property

        Public ReadOnly Property IsNewDocPortrait As Boolean
            Get
                Return Not _newDocIsLandscape
            End Get
        End Property

        Public Property NewDocBackgroundMode As String
            Get
                Return _newDocBackgroundMode
            End Get
            Set(value As String)
                Dim normalized = If(value, "").Trim()
                If normalized <> "Transparent" AndAlso normalized <> "Color" Then normalized = "White"
                Me.RaiseAndSetIfChanged(_newDocBackgroundMode, normalized)
                Me.RaisePropertyChanged(NameOf(IsNewDocBackgroundWhite))
                Me.RaisePropertyChanged(NameOf(IsNewDocBackgroundTransparent))
                Me.RaisePropertyChanged(NameOf(IsNewDocBackgroundColor))
            End Set
        End Property

        Public ReadOnly Property IsNewDocBackgroundWhite As Boolean
            Get
                Return _newDocBackgroundMode = "White"
            End Get
        End Property

        Public ReadOnly Property IsNewDocBackgroundTransparent As Boolean
            Get
                Return _newDocBackgroundMode = "Transparent"
            End Get
        End Property

        Public ReadOnly Property IsNewDocBackgroundColor As Boolean
            Get
                Return _newDocBackgroundMode = "Color"
            End Get
        End Property

        Public Property NewDocBackgroundColor As String
            Get
                Return _newDocBackgroundColor
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_newDocBackgroundColor, value)
                Me.RaisePropertyChanged(NameOf(NewDocBackgroundColorValue))
                Me.RaisePropertyChanged(NameOf(NewDocBackgroundBrush))
            End Set
        End Property

        ''' <summary>Dieselbe Farbe als Color für den ColorPicker. Ein unlesbarer Hex-Wert im Textfeld
        ''' darf den Dialog nicht sprengen - dann gilt Weiß (wie beim Collage-Hintergrund).</summary>
        Public Property NewDocBackgroundColorValue As Avalonia.Media.Color
            Get
                Try
                    Return Avalonia.Media.Color.Parse(_newDocBackgroundColor)
                Catch
                    Return Avalonia.Media.Colors.White
                End Try
            End Get
            Set(value As Avalonia.Media.Color)
                NewDocBackgroundColor = value.ToString()
            End Set
        End Property

        ''' <summary>Das Farbfeld auf dem Knopf, der den Farbwähler aufklappt.</summary>
        Public ReadOnly Property NewDocBackgroundBrush As Avalonia.Media.IBrush
            Get
                Return New Avalonia.Media.SolidColorBrush(NewDocBackgroundColorValue)
            End Get
        End Property

        Public ReadOnly Property IsNewDocUnitMm As Boolean
            Get
                Return _newDocUnit = "mm"
            End Get
        End Property

        Public ReadOnly Property IsNewDocUnitCm As Boolean
            Get
                Return _newDocUnit = "cm"
            End Get
        End Property

        Public ReadOnly Property IsNewDocUnitIn As Boolean
            Get
                Return _newDocUnit = "in"
            End Get
        End Property

        Public ReadOnly Property IsNewDocUnitPx As Boolean
            Get
                Return _newDocUnit = "px"
            End Get
        End Property

        ''' <summary>Bei „px" ist die Auflösung bedeutungslos - das Feld wird dann ausgeblendet.</summary>
        Public ReadOnly Property IsNewDocDpiRelevant As Boolean
            Get
                Return _newDocUnit <> "px"
            End Get
        End Property

        Public ReadOnly Property NewDocPixelWidth As Integer
            Get
                Return DocumentPresetService.ToPixels(_newDocWidth, _newDocUnit, _newDocDpi)
            End Get
        End Property

        Public ReadOnly Property NewDocPixelHeight As Integer
            Get
                Return DocumentPresetService.ToPixels(_newDocHeight, _newDocUnit, _newDocDpi)
            End Get
        End Property

        ''' <summary>Die Zusammenfassung unter den Feldern: was tatsächlich angelegt wird.</summary>
        Public ReadOnly Property NewDocSummaryText As String
            Get
                Return $"{NewDocPixelWidth} × {NewDocPixelHeight} px"
            End Get
        End Property

        Private Sub ClearNewDocPreset()
            If String.IsNullOrEmpty(_newDocPresetId) Then Return
            _newDocPresetId = ""
            Me.RaisePropertyChanged(NameOf(NewDocPresetId))
            Me.RaisePropertyChanged(NameOf(NewDocSelectedPresetId))
            SyncNewDocPresetSelection()
        End Sub

        Private Sub RaiseNewDocumentUnitProperties()
            Me.RaisePropertyChanged(NameOf(IsNewDocUnitMm))
            Me.RaisePropertyChanged(NameOf(IsNewDocUnitCm))
            Me.RaisePropertyChanged(NameOf(IsNewDocUnitIn))
            Me.RaisePropertyChanged(NameOf(IsNewDocUnitPx))
            Me.RaisePropertyChanged(NameOf(IsNewDocDpiRelevant))
        End Sub

        Private Sub RaiseNewDocumentDpiProperties()
            Me.RaisePropertyChanged(NameOf(NewDocDpi))
        End Sub

        Private Sub RaiseNewDocumentPixelProperties()
            Me.RaisePropertyChanged(NameOf(NewDocPixelWidth))
            Me.RaisePropertyChanged(NameOf(NewDocPixelHeight))
            Me.RaisePropertyChanged(NameOf(NewDocSummaryText))
        End Sub

        ''' <summary>Öffnet den Dialog. Die Vorgabe wird dabei neu angewandt, damit Breite/Höhe zur
        ''' aktuellen Einheit und Auflösung passen.</summary>
        Public Sub ShowNewDocumentDialog()
            ApplyNewDocPreset(_newDocPresetId)
            IsNewDocumentDialogOpen = True
        End Sub

        Public Sub CloseNewDocumentDialog()
            ' Kein Sonderfall nötig: der Platzhalter hängt an HasDocument und erscheint von selbst
            ' wieder, wenn hier gar kein Dokument dahinterliegt.
            IsNewDocumentDialogOpen = False
        End Sub

        Public Sub ApplyNewDocPreset(presetId As String)
            Dim preset = DocumentPresetService.ById(presetId)
            If preset Is Nothing Then Return

            ' Pixelvorgaben (Full HD, 4K, Quadrat) haben kein physisches Maß - die Einheit springt
            ' deshalb auf "px", sonst stünde im Feld ein sinnloser Millimeterwert.
            If preset.IsPixelPreset Then
                _newDocUnit = "px"
                RaiseNewDocumentUnitProperties()
                Me.RaisePropertyChanged(NameOf(NewDocUnit))
                _newDocWidth = preset.WidthPx
                _newDocHeight = preset.HeightPx
            Else
                If _newDocUnit = "px" Then
                    _newDocUnit = "mm"
                    RaiseNewDocumentUnitProperties()
                    Me.RaisePropertyChanged(NameOf(NewDocUnit))
                End If
                _newDocWidth = DocumentPresetService.FromPixels(
                    DocumentPresetService.MmToPixels(preset.WidthMm, _newDocDpi), _newDocUnit, _newDocDpi)
                _newDocHeight = DocumentPresetService.FromPixels(
                    DocumentPresetService.MmToPixels(preset.HeightMm, _newDocDpi), _newDocUnit, _newDocDpi)
            End If

            ' Die Vorgaben sind im Hochformat hinterlegt; bei Querformat werden sie gedreht.
            If _newDocIsLandscape Then
                Dim w = _newDocWidth
                _newDocWidth = _newDocHeight
                _newDocHeight = w
            End If

            _newDocPresetId = preset.Id
            Me.RaisePropertyChanged(NameOf(NewDocWidth))
            Me.RaisePropertyChanged(NameOf(NewDocHeight))
            Me.RaisePropertyChanged(NameOf(NewDocPresetId))
            Me.RaisePropertyChanged(NameOf(NewDocSelectedPresetId))
            SyncNewDocPresetSelection()
            RaiseNewDocumentPixelProperties()
        End Sub

#End Region

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
                ' Solange ein Region-Commit in der Hintergrund-Queue läuft, ist Undo gesperrt:
                ' der Undo-Eintrag des laufenden Zugs hat seinen Pixel-Patch noch nicht - ein
                ' Undo JETZT stellte nur die Regler zurück, die Pixel kämen danach trotzdem an
                ' (Nutzerwunsch 2026-07-17: keine Aktionen, solange das Bild nicht final ist).
                Return _undoStack.Count > 0 AndAlso _pendingWorkingCommits = 0
            End Get
        End Property

        Public ReadOnly Property CanRedo As Boolean
            Get
                Return _redoStack.Count > 0 AndAlso _pendingWorkingCommits = 0
            End Get
        End Property

        Public ReadOnly Property HasUnsavedChanges As Boolean
            Get
                Return _hasChanges
            End Get
        End Property

        ''' <summary>Anhang für die Statuszeile bei RAW-Quellen: unterscheidet echte Entwicklung
        ''' (System-libraw: volles Demosaic mit Kamera-Weißabgleich) von der eingebetteten
        ''' JPEG-Vorschau, auf die ohne libraw zurückgefallen wird.
        '''
        ''' Der Zustand wird derzeit über die Cache-Wärme erschlossen, nicht über den tatsächlich
        ''' gegangenen Decode-Weg. Das ist die schwächere Auskunft: unmittelbar nach dem Leeren des
        ''' Caches kann sie kurz "Vorschau" zeigen, obwohl entwickelt wurde. Sauberer wäre, dass
        ''' DecodeOriented mitteilt, welchen Weg es genommen hat.
        '''
        ''' Unabhängig davon gilt: die RAW-Datei ist nie Speicherziel - Export schreibt in eine neue
        ''' Datei, Reglerstände gehen in das .fpxmp-Sidecar.</summary>
        Private Function RawStatusSuffix() As String
            If Not RawPreviewService.IsSupportedRaw(RenderSourcePath) Then Return ""
            Dim developed = RawDecodeService.IsAvailable AndAlso
                            RawDecodeService.TryGetCachedSize(RenderSourcePath).Width > 0
            Return "  •  " & If(developed,
                                LocalizationService.T("RAW entwickelt"),
                                LocalizationService.T("RAW-Vorschau"))
        End Function

        Public ReadOnly Property IsCurrentImageRaw As Boolean
            Get
                Return Not String.IsNullOrEmpty(_currentImagePath) AndAlso RawPreviewService.IsSupportedRaw(RenderSourcePath)
            End Get
        End Property

        ''' PSD/PSB sind nur-lesend: die Pipeline arbeitet auf dem zusammengesetzten Gesamtbild
        ''' (PsdPreviewService), ein Zurückschreiben würde die Ebenen der Datei zerstören.
        Public ReadOnly Property IsCurrentImagePsd As Boolean
            Get
                Return Not String.IsNullOrEmpty(_currentImagePath) AndAlso PsdPreviewService.IsSupportedPsd(RenderSourcePath)
            End Get
        End Property

        ''' <summary>RAW oder PSD - beide behalten ihre Bearbeitung im .fpxmp-Sidecar, weil wir
        ''' keines der beiden Formate schreiben koennen.</summary>
        Public ReadOnly Property IsCurrentImageSidecarFormat As Boolean
            Get
                Return Not String.IsNullOrEmpty(_currentImagePath) AndAlso RawSidecarService.IsSidecarFormat(RenderSourcePath)
            End Get
        End Property

        ''' Steuert, ob der "Speichern"-Button (in-place) aktiv ist. Bei RAW und PSD schreibt
        ''' "Speichern" nicht die Datei, sondern das Rezept in die Begleitdatei - der Knopf ist
        ''' also aktiv, solange dieser Weg offen steht (CanSaveSidecar).
        ''' Immich-Bilder liegen als Temp-Kopie vor (_forceSaveAsOnly): dort ist "Speichern" nur dann
        ''' sinnvoll, wenn es das Quell-Asset ersetzen darf - siehe SavesBackToImmich.
        Public ReadOnly Property CanSaveInPlace As Boolean
            Get
                Return (Not IsCurrentImageSidecarFormat OrElse CanSaveSidecar) AndAlso
                       (Not _forceSaveAsOnly OrElse SavesBackToImmich OrElse Not String.IsNullOrEmpty(_currentFpxPath))
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
                ' Striche/Radierer stecken seit Stufe D als gebackener Inhalt im Arbeitsbild.
                Return _annotations.Count > 0 OrElse _workingImage.HasBakedContent
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
        Public ReadOnly Property ClearShapeIconSearchCommand As ICommand
        Public ReadOnly Property SetRatingCommand As ICommand
        Public ReadOnly Property ToggleFavoriteCommand As ICommand
        Public ReadOnly Property SetColorLabelCommand As ICommand
        Public ReadOnly Property AddTagCommand As ICommand
        Public ReadOnly Property RemoveTagCommand As ICommand
        Public ReadOnly Property SaveCommand As ICommand
        Public ReadOnly Property SaveAsCommand As ICommand
        Public ReadOnly Property ShowNewDocumentDialogCommand As ICommand
        Public ReadOnly Property CancelNewDocumentCommand As ICommand
        Public ReadOnly Property ConfirmNewDocumentCommand As ICommand
        Public ReadOnly Property SetNewDocPresetCommand As ICommand
        Public ReadOnly Property SetNewDocUnitCommand As ICommand
        Public ReadOnly Property SetNewDocDpiCommand As ICommand
        Public ReadOnly Property SetNewDocOrientationCommand As ICommand
        Public ReadOnly Property SetNewDocBackgroundCommand As ICommand
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
        Public ReadOnly Property RasterizeSelectedAnnotationCommand As ICommand
        Public ReadOnly Property MoveSelectedAnnotationUpCommand As ICommand
        Public ReadOnly Property MoveSelectedAnnotationDownCommand As ICommand
        Public ReadOnly Property TogglePixelLayerVisibilityCommand As ICommand
        Public ReadOnly Property ToggleBackgroundVisibilityCommand As ICommand
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
        Public ReadOnly Property SetAnnotationTextPathKindCommand As ICommand
        Public ReadOnly Property SetAnnotationFillKindCommand As ICommand
        Public ReadOnly Property SetAnnotationAnchorCommand As ICommand
        Public ReadOnly Property ResetTransformCommand As ICommand
        Public ReadOnly Property SetBrushPresetCommand As ICommand
        Public ReadOnly Property SetFilterPresetCommand As ICommand
        Public ReadOnly Property ResetFilterCommand As ICommand
        Public ReadOnly Property ResetCurveCommand As ICommand
        Public ReadOnly Property SetCurveChannelCommand As ICommand
        Public ReadOnly Property ResetHslCommand As ICommand
        Public ReadOnly Property ResetCalibrationCommand As ICommand
        Public ReadOnly Property ResetSplitToningCommand As ICommand
        Public ReadOnly Property PickNegativeBaseCommand As ICommand
        Public ReadOnly Property AutoNegativeBaseCommand As ICommand
        Public ReadOnly Property ResetNegativeCommand As ICommand
        Public ReadOnly Property ToggleInfoSidebarCommand As ICommand
        Public ReadOnly Property ToggleLayersPanelCommand As ICommand
        Public ReadOnly Property SetInfoTabCommand As ICommand
        Public ReadOnly Property SetLayersPanelTabCommand As ICommand
        Public ReadOnly Property BackToViewerCommand As ICommand
        Public ReadOnly Property BackToGalleryCommand As ICommand
        Public ReadOnly Property PreviousCommand As ICommand
        Public ReadOnly Property NextCommand As ICommand
        Public ReadOnly Property PrintCommand As ICommand
        Public ReadOnly Property DeleteCurrentCommand As ICommand

        Public Sub New(mainVm As MainWindowViewModel)
            _mainVm = mainVm
            FilmstripItems = New BulkObservableCollection(Of ImageItem)()
            Tags = New ObservableCollection(Of String)()
            TagSuggestions = New ObservableCollection(Of String)(LibraryService.Instance.GetAllTags())
            HistoryItems = New ObservableCollection(Of String)()
            ' Ebenen-Panel-Anzeige (umgekehrte Reihenfolge) an den Objektstapel koppeln.
            AddHandler _annotations.CollectionChanged, Sub(s, e) RebuildLayerRows()
            RebuildLayerRows()
            LoadFixedShapeItems()
            LoadAllShapeIcons()
            LoadWatermarkPresets()
            LoadSavedLightroomPresets()
            LoadSavedLutPresets()
            _previewTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(PreviewDebounceMs)}
            AddHandler _previewTimer.Tick, Sub()
                                               _previewTimer.Stop()
                                               OnPreviewTimerTick()
                                           End Sub

            _filmstripNavDebouncer = New FilmstripNavigationDebouncer(wrapAround:=True,
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
            ' Gleiche Farbe erneut = Etikett entfernen (wie im Galerie-Kontextmenü).
            SetColorLabelCommand = ReactiveCommand.Create(Of String)(
                Sub(hex) ColorLabel = If(String.Equals(_colorLabel, If(hex, ""), StringComparison.OrdinalIgnoreCase), "", If(hex, "")))

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
                                                                       Case "brush", "pinsel", "eraser", "radiergummi", "blur", "verwischen", "repair", "reparatur", "reparaturpinsel", "heal", "heilen", "retusche", "clone", "stempel"
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
                                                                           If Not IsObjectScopeTool(parsed) Then SelectedAnnotationIndex = -1
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
            ClearShapeIconSearchCommand = ReactiveCommand.Create(Sub() ShapeIconSearchText = "")

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
            RasterizeSelectedAnnotationCommand = ReactiveCommand.Create(Sub()
                                                                            RasterizeSelectedAnnotation()
                                                                        End Sub)
            MoveSelectedAnnotationUpCommand = ReactiveCommand.Create(Sub()
                                                                         MoveSelectedAnnotation(1)
                                                                     End Sub)
            MoveSelectedAnnotationDownCommand = ReactiveCommand.Create(Sub()
                                                                           MoveSelectedAnnotation(-1)
                                                                       End Sub)
            ToggleBackgroundVisibilityCommand = ReactiveCommand.Create(Sub() ToggleBackgroundVisibility())
            TogglePixelLayerVisibilityCommand = ReactiveCommand.Create(Sub() TogglePixelLayerVisibility())
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
                                                                 SchedulePreviewForCurrentTarget()
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
            SetAnnotationTextPathKindCommand = ReactiveCommand.Create(Of String)(Sub(kind) SetAnnotationTextPathKind(kind))
            ResetTransformCommand = ReactiveCommand.Create(Sub()
                                                               PushUndo()
                                                               ResetTransformInternal()
                                                           End Sub)
            SetBrushPresetCommand = ReactiveCommand.Create(Of String)(AddressOf SelectBrushPreset)
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
                                                           SchedulePreviewForCurrentTarget()
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

            ResetCalibrationCommand = ReactiveCommand.Create(Sub()
                                                                 PushUndo()
                                                                 CalibrationRedHue = 0
                                                                 CalibrationRedSaturation = 0
                                                                 CalibrationGreenHue = 0
                                                                 CalibrationGreenSaturation = 0
                                                                 CalibrationBlueHue = 0
                                                                 CalibrationBlueSaturation = 0
                                                                 CalibrationShadowTint = 0
                                                             End Sub)

            ToggleInfoSidebarCommand = ReactiveCommand.Create(Sub()
                                                                   If _mainVm Is Nothing OrElse _mainVm.Settings Is Nothing Then Return
                                                                   _mainVm.Settings.EditorInfoSidebarExpanded = Not _mainVm.Settings.EditorInfoSidebarExpanded
                                                                   Me.RaisePropertyChanged(NameOf(IsInfoSidebarVisible))
                                                               End Sub)
            ToggleLayersPanelCommand = ReactiveCommand.Create(Sub()
                                                                   If _mainVm Is Nothing OrElse _mainVm.Settings Is Nothing Then Return
                                                                   _mainVm.Settings.EditorLayersPanelExpanded = Not _mainVm.Settings.EditorLayersPanelExpanded
                                                                   Me.RaisePropertyChanged(NameOf(IsLayersPanelVisible))
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
            PrintCommand = ReactiveCommand.CreateFromTask(Function() PrintCurrentAsync())

            ShowNewDocumentDialogCommand = ReactiveCommand.Create(Sub() ShowNewDocumentDialog())
            CancelNewDocumentCommand = ReactiveCommand.Create(Sub() CloseNewDocumentDialog())
            ConfirmNewDocumentCommand = ReactiveCommand.CreateFromTask(Function() ConfirmNewDocumentAsync())
            ' Logikschlüssel als Parameter, nie der übersetzte Anzeigetext (Hauskonvention, siehe
            ' PrintDialogOverlayView.axaml.vb).
            SetNewDocPresetCommand = ReactiveCommand.Create(Of String)(Sub(id) ApplyNewDocPreset(id))
            SetNewDocUnitCommand = ReactiveCommand.Create(Of String)(Sub(u) NewDocUnit = u)
            SetNewDocDpiCommand = ReactiveCommand.Create(Of String)(Sub(d)
                                                                       Dim parsed As Integer
                                                                       If Integer.TryParse(d, parsed) Then NewDocDpi = parsed
                                                                   End Sub)
            SetNewDocOrientationCommand = ReactiveCommand.Create(Of String)(Sub(o) NewDocIsLandscape = String.Equals(o, "Landscape", StringComparison.Ordinal))
            SetNewDocBackgroundCommand = ReactiveCommand.Create(Of String)(Sub(b) NewDocBackgroundMode = b)

            If _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing Then
                _saveQuality = _mainVm.Settings.JpgSaveQuality
            End If
        End Sub

        ''' <summary>Druckt den AKTUELLEN BEARBEITUNGSSTAND, nicht die Quelldatei: Anpassungen,
        ''' Objekte und gebackene Striche werden - über exakt dieselbe Export-Route wie
        ''' „Speichern unter" - in eine Temp-Datei gerendert, die dann in den Druckdialog geht.
        ''' Der Dialog löscht sie beim Schließen wieder.
        ''' Identität kommt aus _currentImagePath, die Pixel aus RenderSourcePath (bei .fpx das
        ''' entpackte Basisbild) - siehe Kommentar an RenderSourcePath.</summary>
        Private Async Function PrintCurrentAsync() As Task
            If String.IsNullOrEmpty(_currentImagePath) Then Return

            ' Wie im Speichern-Pfad: laufenden Strich und die Eigenschaften des ausgewählten
            ' Objekts erst ins Modell übernehmen, sonst fehlen sie im Druck.
            If _retouchStrokeActive Then CommitRetouchStroke()
            If HasSelectedAnnotation Then SyncSelectedAnnotation(refreshOverlay:=False)
            CommitObjectAdjustModeToModel()

            Dim adj = GetCurrentAdjustments()
            Dim sourcePath = RenderSourcePath
            Dim tempDir = IO.Path.Combine(IO.Path.GetTempPath(), "FerrumPix", "Print")
            Dim tempPath = IO.Path.Combine(tempDir,
                                           IO.Path.GetFileNameWithoutExtension(_currentImagePath) &
                                           $"_{DateTime.Now:yyyyMMdd_HHmmssfff}.png")

            StatusText = LocalizationService.T("Druck wird vorbereitet…")
            Dim ok = Await Task.Run(Function() As Boolean
                                        Try
                                            IO.Directory.CreateDirectory(tempDir)
                                        Catch
                                            Return False
                                        End Try
                                        ' PNG als Zwischenformat: verlustfrei, und das Einbetten ins
                                        ' PDF komprimiert ohnehin erst PrintService.
                                        ' preserveMetadata:=False - eine Temp-Datei braucht kein EXIF.
                                        Return ImageProcessor.SaveImage(sourcePath, tempPath, adj, 100,
                                                                        preserveMetadata:=False,
                                                                        workingFull:=CloneWorkingFullForRender())
                                    End Function)
            StatusText = ""

            If Not ok OrElse Not IO.File.Exists(tempPath) Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Drucken fehlgeschlagen"),
                                               LocalizationService.T("Der Bearbeitungsstand konnte nicht für den Druck aufbereitet werden."))
                Return
            End If

            _mainVm?.ShowPrintDialog(New List(Of String) From {tempPath},
                                     tempFile:=tempPath)
        End Function

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
                CleanupCurrentFpxTempDir()
                CleanupCurrentNewDocTempDir()
            End If
        End Sub

        ''' Aus welchem Modus der Editor betreten wurde - bestimmt, wohin „Zurück" führt
        ''' (Galerie-Einstieg -> Galerie, sonst Viewer). Gesetzt vom CurrentMode-Setter.
        Private _entryMode As AppMode = AppMode.Viewer

        Public Sub SetEntryMode(mode As AppMode)
            _entryMode = mode
        End Sub

        Public Async Function BackToViewerAsync() As Task
            If Not Await ConfirmSaveBeforeLeavingAsync("den Editor verlässt") Then Return

            ' Ein NIE GESPEICHERTES neues Bild liegt nur im Temp-Ordner. Dessen Pfad darf hier auf
            ' keinen Fall als Ziel dienen - Galerie bzw. Betrachter landeten sonst im Temp-Ordner.
            ' (Wurde per „Speichern unter" gesichert, ist _isNewDocument längst wieder aus, weil
            ' SaveImageAsync die gespeicherte Datei neu öffnet - dann greift dieser Zweig nicht.)
            If _isNewDocument Then
                DiscardNewDocument()
                _mainVm.ShowGalleryAtRealFolder()
                Return
            End If

            ' Galerie-Einstieg: zurück in die Galerie, auf dem zuletzt bearbeiteten Bild -
            ' nicht in den Viewer (Nutzerwunsch 2026-07-17).
            If _entryMode = AppMode.Gallery Then
                If Not String.IsNullOrEmpty(_currentImagePath) Then _mainVm.Gallery?.SelectItemByPath(_currentImagePath)
                _mainVm.CurrentMode = AppMode.Gallery
                Return
            End If
            ' Immich-Edit: nach dem Speichern kann der Viewer noch die alte Temp-Kopie oder das alte Asset
            ' halten. Deshalb wird hier bewusst frisch auf den Editor-Pfad geöffnet, damit beim Zurückkehren
            ' der tatsächlich gespeicherte Stand sichtbar ist.
            Dim editorIsImmich = _immichSourceAlbumId IsNot Nothing OrElse ImmichService.IsImmichTempPath(_currentImagePath)
            If editorIsImmich AndAlso _mainVm.Viewer IsNot Nothing AndAlso _mainVm.Viewer.IsImmichSession Then
                If Not String.IsNullOrEmpty(_currentImagePath) AndAlso IO.File.Exists(_currentImagePath) Then
                    _mainVm.Viewer.OpenImage(_currentImagePath, _folderPaths.ToList(), _thumbCacheScopeId, _thumbCacheScopeName)
                Else
                    _mainVm.Viewer.ReloadCurrentImageFromDisk()
                End If
                _mainVm.CurrentMode = AppMode.Viewer
                Return
            End If
            If Not String.IsNullOrEmpty(_currentImagePath) Then
                _mainVm.Viewer.OpenImage(_currentImagePath, _folderPaths.ToList(), _thumbCacheScopeId, _thumbCacheScopeName)
                _mainVm.Viewer.ReloadCurrentImageFromDisk()
                _mainVm.CurrentMode = AppMode.Viewer
            Else
                _mainVm.CurrentMode = AppMode.Viewer
            End If
        End Function

        Public Sub ResetTransientUiState()
            _selectedAnnotationIndex = -1
            ResetEditorUiStateForNewImage(resetTool:=True)
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationIndex))
            Me.RaisePropertyChanged(NameOf(SelectedLayer))
            Me.RaisePropertyChanged(NameOf(HasSelectedAnnotation))
            Me.RaisePropertyChanged(NameOf(CanRasterizeSelectedAnnotation))
        End Sub

        Public Sub ActivateDefaultToolForModeEntry()
            _overlayNotifySuppressDepth += 1
            Try
                PendingInsertKind = ""
                SelectedAnnotationIndex = -1
                CurrentTool = EditorTool.Selection
                SelectedLayersPanelTab = LayersPanelTab.Tool
            Finally
                _overlayNotifySuppressDepth -= 1
            End Try
            NotifyAnnotationOverlayStateChanged()
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
            Await LoadImageContent(item.FilePath)
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
            Await LoadImageContent(_folderPaths(_currentIndex))
        End Function

        Public Async Function NavigateNextAsync() As Task
            If _folderPaths.Count = 0 Then Return
            Dim nextIndex = (_currentIndex + 1) Mod _folderPaths.Count
            If nextIndex = _currentIndex Then Return
            Await NavigateToFilmstripIndexAsync(nextIndex)
        End Function

        Public Async Function NavigatePreviousAsync() As Task
            If _folderPaths.Count = 0 Then Return
            Dim previousIndex = ((_currentIndex - 1) Mod _folderPaths.Count + _folderPaths.Count) Mod _folderPaths.Count
            If previousIndex = _currentIndex Then Return
            Await NavigateToFilmstripIndexAsync(previousIndex)
        End Function

        Private Async Function LoadImageContent(path As String) As Task
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
                    _renderSourcePathOverride = ""
                    _workingImageOverridePath = ""
                    _workingImageOverrideHasAlpha = False
                    _currentFpxPath = ""
                    CleanupCurrentFpxTempDir()
                    CleanupCurrentNewDocTempDir()
                    StatusText = ""
                    Return
                End If

                Dim fallbackIndex = Math.Max(0, Math.Min(_currentIndex, _folderPaths.Count - 1))
                Await LoadImageContent(_folderPaths(fallbackIndex))
                Return
            End If

            ' .fpx-Projekt im Filmstreifen: Bündel entpacken, Basisbild wird zur Render-Quelle, gespeicherter
            ' Zustand wird wiederhergestellt. Identität (path) bleibt der .fpx-Pfad.
            Dim newFpxPath = ""
            Dim newRenderSourcePathOverride = ""
            Dim newFpxTempDir = ""
            Dim newForceSaveAsOnly = ImmichService.IsImmichTempPath(path)
            Dim fpxAdjustments As ImageAdjustments = Nothing
            Dim newWorkingOverridePath = ""
            Dim newWorkingOverrideHasAlpha = False
            If FpxService.IsFpx(path) Then
                Dim loaded = FpxService.Load(path)
                If loaded Is Nothing OrElse loaded.Adjustments Is Nothing OrElse String.IsNullOrEmpty(loaded.BaseImagePath) OrElse Not File.Exists(loaded.BaseImagePath) Then
                    StatusText = LocalizationService.T("Fehler beim Laden")
                    Return
                End If
                fpxAdjustments = loaded.Adjustments
                newRenderSourcePathOverride = loaded.BaseImagePath
                newFpxPath = path
                newFpxTempDir = loaded.TempDir
                newForceSaveAsOnly = True
                ' Gebackenes Arbeitsbild (voll aufgelöstes retouch.png): PreparePreviewSource
                ' dekodiert es statt des Basisbilds (Maße-Prüfung passiert dort).
                newWorkingOverridePath = If(loaded.RetouchStagePath, "")
                newWorkingOverrideHasAlpha = loaded.Adjustments.WorkingImageHasTransparency
            ElseIf RawSidecarService.IsSidecarFormat(path) AndAlso
                   RawSidecarService.Exists(path) Then
                ' Rezept-Begleitdatei (.fpxmp): die zuletzt gespeicherten Regler kommen wie beim
                ' .fpx-Laden wieder an - ein defekter Sidecar wird still ignoriert.
                fpxAdjustments = RawSidecarService.TryRead(path)
            End If

            CleanupCurrentFpxTempDir()
            ' Filmstreifen-Navigation verlässt ein neues Dokument endgültig - Temp-Ordner weg.
            CleanupCurrentNewDocTempDir()
            _currentFpxPath = newFpxPath
            _renderSourcePathOverride = newRenderSourcePathOverride
            _currentFpxTempDir = newFpxTempDir
            _forceSaveAsOnly = newForceSaveAsOnly
            _workingImageOverridePath = newWorkingOverridePath
            _workingImageOverrideHasAlpha = newWorkingOverrideHasAlpha
            CurrentImagePath = path
            _currentImagePath = path
            SelectedInfoTab = InfoSidebarTab.General
            ClearSelection(captureUndo:=False)   ' pixelbasierte Auswahlmaske gilt nur fürs alte Bild
            ResetAdjustmentsInternal(resetEditorUi:=True)
            ClearUndoHistory()
            Dim previousSuppressPreviewDirty = _suppressPreviewDirty
            _suppressPreviewDirty = True
            Try
                ShowBeforeImage = _comparisonAutoEnabled AndAlso CanShowBeforeAfter
                PreviewImage = Nothing
                ComparisonImage = Nothing
                CurrentImage = Nothing
                If Not String.IsNullOrEmpty(_currentFpxPath) Then PreviewImage = LoadFpxCompositePreview(_currentFpxPath)
                ExifInfo = Nothing
                ClearHistogramData()
                Await PreparePreviewSourceAsync(RenderSourcePath)
                If fpxAdjustments IsNot Nothing Then
                    ApplyAdjustments(fpxAdjustments)
                    _hasChanges = False
                    Me.RaisePropertyChanged(NameOf(HasUnsavedChanges))
                End If
            Finally
                _suppressPreviewDirty = previousSuppressPreviewDirty
            End Try
            LoadLibraryMeta(path)
            Me.RaisePropertyChanged(NameOf(CurrentFilmstripIndex))
            Me.RaisePropertyChanged(NameOf(PositionText))
            MarkCurrentFilmstripItem()
            Try
                ' PreparePreviewSource leitet CurrentImage bereits aus dem Arbeitsbild ab - ein
                ' zweiter Decode derselben Datei entfaellt damit im Normalfall.
                ' Der Rueckfall bleibt trotzdem stehen: die beiden Wege koennen divergieren. Genau
                ' das war der PSD-Befund vom 2026-07-19 - die Render-Pipeline konnte das Format,
                ' der Anzeigeweg nicht. Hier ist es die Gegenrichtung, aber dasselbe Risiko.
                If CurrentImage Is Nothing Then
                    ' applySidecarRotation:=False - der Editor wendet die Sidecar-Drehung schon als Teil des
                    ' Rezepts in der Render-Pipeline an; hier nochmal drehen hiesse doppelt drehen.
                    CurrentImage = ImageOrientationService.LoadOrientedAvaloniaBitmapAuto(RenderSourcePath, applySidecarRotation:=False)
                End If
                If CurrentImage Is Nothing Then
                    StatusText = If(RawPreviewService.IsSupportedRaw(RenderSourcePath),
                                    LocalizationService.T("Keine Vorschau aus dieser RAW-Datei extrahierbar"),
                                    If(PsdPreviewService.IsSupportedPsd(RenderSourcePath),
                                       LocalizationService.T("PSD ohne lesbares Gesamtbild - in Photoshop mit maximaler Kompatibilität speichern"),
                                       LocalizationService.T("Fehler beim Laden")))
                    Return
                End If
                VerifyWorkingImageDimensions()
                ExifInfo = BuildImageInfo(RenderSourcePath)
                RefreshHistogram()
                Dim info = New FileInfo(path)
                Dim kb = info.Length / 1024.0
                Dim sizeStr = If(kb < 1024, $"{kb:F0} KB", $"{kb / 1024:F1} MB")
                Dim mp = CurrentImage.Size.Width * CurrentImage.Size.Height / 1_000_000.0
                ' Bei RAW-Quellen zeigt die Statuszeile, WORAUF gearbeitet wird: echte Entwicklung
                ' (System-libraw, voller Sensor-Decode) oder nur die eingebettete JPEG-Vorschau.
                StatusText = $"{CInt(CurrentImage.Size.Width)} × {CInt(CurrentImage.Size.Height)}  {mp:F1} MP  •  {sizeStr}{RawStatusSuffix()}"
                Me.RaisePropertyChanged(NameOf(IsRawDeveloped))
                Me.RaisePropertyChanged(NameOf(RawFooterTooltip))
            Catch
                StatusText = LocalizationService.T("Fehler beim Laden")
            End Try
            If fpxAdjustments IsNot Nothing Then ScheduleToolPreviewUpdate()
        End Function

        Public Sub OpenImage(imagePath As String, Optional allPaths As List(Of String) = Nothing)
            Dim ignored = OpenImageAsync(imagePath, allPaths)
        End Sub

        Public Async Function OpenImageAsync(imagePath As String, Optional allPaths As List(Of String) = Nothing, Optional cacheScopeId As String = Nothing, Optional cacheScopeName As String = Nothing, Optional forceSaveAsOnly As Boolean = False, Optional immichAlbumId As String = Nothing) As Task(Of Boolean)
            If String.IsNullOrEmpty(imagePath) OrElse Not File.Exists(imagePath) Then Return False
            If Not String.IsNullOrEmpty(_currentImagePath) AndAlso Not String.Equals(_currentImagePath, imagePath, StringComparison.OrdinalIgnoreCase) Then
                If Not Await ConfirmSaveBeforeLeavingAsync("ein anderes Bild öffnest") Then Return False
            End If

            ' .fpx-Projektdatei: Bündel entpacken; ab hier ist das entpackte Basisbild die Arbeitsquelle, und
            ' der gespeicherte Bearbeitungszustand wird unten (nach PreparePreviewSource) wiederhergestellt.
            Dim newFpxPath = ""
            Dim newRenderSourcePathOverride = ""
            Dim newFpxTempDir = ""
            Dim fpxAdjustments As ImageAdjustments = Nothing
            Dim newWorkingOverridePath = ""
            Dim newWorkingOverrideHasAlpha = False
            If FpxService.IsFpx(imagePath) Then
                Dim fpxSource = imagePath
                Dim loaded = Await Task.Run(Function() FpxService.Load(fpxSource))
                If loaded Is Nothing OrElse loaded.Adjustments Is Nothing OrElse String.IsNullOrEmpty(loaded.BaseImagePath) OrElse Not File.Exists(loaded.BaseImagePath) Then
                    Await _mainVm.ShowMessageAsync(LocalizationService.T("Öffnen fehlgeschlagen"), LocalizationService.T("Diese .fpx-Projektdatei konnte nicht gelesen werden."))
                    Return False
                End If
                ' imagePath bleibt der .fpx-Pfad (Identität für Filmstreifen/Metadaten); die Pipeline dekodiert
                ' das entpackte Basisbild über den Render-Override.
                fpxAdjustments = loaded.Adjustments
                newRenderSourcePathOverride = loaded.BaseImagePath
                newFpxPath = fpxSource
                newFpxTempDir = loaded.TempDir
                forceSaveAsOnly = True   ' das Basisbild ist eine Temp-Kopie -> Speichern nur als neue Datei
                ' Gebackenes Arbeitsbild (voll aufgelöstes retouch.png): PreparePreviewSource
                ' dekodiert es statt des Basisbilds (Maße-Prüfung passiert dort).
                newWorkingOverridePath = If(loaded.RetouchStagePath, "")
                newWorkingOverrideHasAlpha = loaded.Adjustments.WorkingImageHasTransparency
            ElseIf RawSidecarService.IsSidecarFormat(imagePath) AndAlso
                   RawSidecarService.Exists(imagePath) Then
                fpxAdjustments = RawSidecarService.TryRead(imagePath)
            End If

            CleanupCurrentFpxTempDir()
            ' Staffelübergabe: erst den ALTEN Ordner des vorigen neuen Dokuments löschen, dann den
            ' frisch angelegten übernehmen. Andernfalls löschte dieser Aufruf die Datei weg, die er
            ' gerade öffnet.
            Dim incomingNewDocTempDir = _pendingNewDocTempDir
            Dim incomingNewDocTransparent = _pendingNewDocTransparent
            _pendingNewDocTempDir = ""
            _pendingNewDocTransparent = False
            CleanupCurrentNewDocTempDir()
            _currentNewDocTempDir = incomingNewDocTempDir
            _isNewDocument = Not String.IsNullOrEmpty(incomingNewDocTempDir)
            _newDocTransparentBackground = _isNewDocument AndAlso incomingNewDocTransparent
            RaiseNewDocumentStateChanged()
            _currentFpxPath = newFpxPath
            _renderSourcePathOverride = newRenderSourcePathOverride
            _currentFpxTempDir = newFpxTempDir
            _workingImageOverridePath = newWorkingOverridePath
            _workingImageOverrideHasAlpha = newWorkingOverrideHasAlpha
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
            Dim previousSuppressPreviewDirty = _suppressPreviewDirty
            _suppressPreviewDirty = True
            Try
                ShowBeforeImage = _comparisonAutoEnabled AndAlso CanShowBeforeAfter
                PreviewImage = Nothing
                ComparisonImage = Nothing
                CurrentImage = Nothing
                If Not String.IsNullOrEmpty(_currentFpxPath) Then PreviewImage = LoadFpxCompositePreview(_currentFpxPath)
                ExifInfo = Nothing
                ClearHistogramData()
                Await PreparePreviewSourceAsync(RenderSourcePath)
                ' Gespeicherten Bearbeitungszustand aus der .fpx wiederherstellen (Regler, Ebenenstapel, Auswahl …)
                ' und als "keine ungespeicherten Änderungen" markieren - es ist ja gerade der gespeicherte Stand.
                If fpxAdjustments IsNot Nothing Then
                    ApplyAdjustments(fpxAdjustments)
                    _hasChanges = False
                    Me.RaisePropertyChanged(NameOf(HasUnsavedChanges))
                    ScheduleToolPreviewUpdate()
                End If
            Finally
                _suppressPreviewDirty = previousSuppressPreviewDirty
            End Try
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
                ' PreparePreviewSource leitet CurrentImage bereits aus dem Arbeitsbild ab - ein
                ' zweiter Decode derselben Datei entfaellt damit im Normalfall.
                ' Der Rueckfall bleibt trotzdem stehen: die beiden Wege koennen divergieren. Genau
                ' das war der PSD-Befund vom 2026-07-19 - die Render-Pipeline konnte das Format,
                ' der Anzeigeweg nicht. Hier ist es die Gegenrichtung, aber dasselbe Risiko.
                If CurrentImage Is Nothing Then
                    ' applySidecarRotation:=False - der Editor wendet die Sidecar-Drehung schon als Teil des
                    ' Rezepts in der Render-Pipeline an; hier nochmal drehen hiesse doppelt drehen.
                    CurrentImage = ImageOrientationService.LoadOrientedAvaloniaBitmapAuto(RenderSourcePath, applySidecarRotation:=False)
                End If
                If CurrentImage Is Nothing Then
                    Dim message = If(RawPreviewService.IsSupportedRaw(RenderSourcePath),
                        LocalizationService.T("Aus dieser RAW-Datei konnte keine Vorschau extrahiert werden."),
                        LocalizationService.T("Diese Datei konnte nicht geöffnet werden."))
                    Await _mainVm.ShowMessageAsync(LocalizationService.T("Öffnen fehlgeschlagen"), message)
                    CurrentImagePath = ""
                    _currentImagePath = ""
                    _renderSourcePathOverride = ""
                    _workingImageOverridePath = ""
                    _workingImageOverrideHasAlpha = False
                    Return False
                End If
                VerifyWorkingImageDimensions()
                ExifInfo = BuildImageInfo(RenderSourcePath)
                RefreshHistogram()
                Dim info = New FileInfo(imagePath)
                Dim kb = info.Length / 1024.0
                Dim sizeStr = If(kb < 1024, $"{kb:F0} KB", $"{kb / 1024:F1} MB")
                Dim mp = CurrentImage.Size.Width * CurrentImage.Size.Height / 1_000_000.0
                ' Bei RAW-Quellen zeigt die Statuszeile, WORAUF gearbeitet wird: echte Entwicklung
                ' (System-libraw, voller Sensor-Decode) oder nur die eingebettete JPEG-Vorschau.
                StatusText = $"{CInt(CurrentImage.Size.Width)} × {CInt(CurrentImage.Size.Height)}  {mp:F1} MP  •  {sizeStr}{RawStatusSuffix()}"
                Me.RaisePropertyChanged(NameOf(IsRawDeveloped))
                Me.RaisePropertyChanged(NameOf(RawFooterTooltip))
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
            _colorLabel = LibraryService.Instance.GetColorLabel(imagePath)
            RaiseColorLabelProperties()
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
            ' Etikett ist rein lokal - unter dem Pseudo-Pfad des Assets abgelegt (wie in der Galerie).
            _colorLabel = LibraryService.Instance.GetColorLabel(ImmichService.MakePseudoPath(asset.Id, asset.FileName))
            RaiseColorLabelProperties()
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

        ''' <summary>Leert den Filmstreifen samt Thumbnails. Herausgezogen, damit das Verwerfen eines
        ''' neuen Bildes dieselbe Räumung nutzt wie der Neuaufbau beim Laden.</summary>
        Private Sub ClearFilmstrip()
            For Each filmItem In FilmstripItems
                filmItem?.EvictThumbnail()
            Next
            FilmstripItems.Clear()
            _folderPaths.Clear()
            _currentIndex = -1
        End Sub

        Private Sub LoadFilmstripContext(imagePath As String, Optional allPaths As List(Of String) = Nothing)
            ClearFilmstrip()

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
            ' .fpx-Projekte sind im Editor voll bearbeitbar (Rezept wird wiederhergestellt) und
            ' gehoeren deshalb in den Filmstreifen - solange das Format aktiviert ist.
            Return editableExts.Contains(ext) OrElse RawPreviewService.IsSupportedRaw(path) OrElse
                   PsdPreviewService.IsSupportedPsd(path) OrElse FpxService.IsFpx(path)
        End Function

        Private Sub SchedulePreviewUpdate()
            If Not _suppressPreviewDirty Then
                _hasChanges = True
                Me.RaisePropertyChanged(NameOf(HasUnsavedChanges))
            End If
            _previewPending = True
            StatusText = LocalizationService.T("Vorschau wird aktualisiert...")
            RestartPreviewTimer(PreviewDebounceMs)
        End Sub

        Private Sub RefreshPreviewImmediately()
            If Not _suppressPreviewDirty Then
                _hasChanges = True
                Me.RaisePropertyChanged(NameOf(HasUnsavedChanges))
            End If
            _previewTimer.Stop()
            _previewPending = False
            If Not TryRenderAnnotationOverlaySync() Then
                UpdatePreview()
            End If
        End Sub

        ''' <summary>Strukturaenderung der Objektliste (Anlegen/Loeschen/Duplizieren/Umsortieren/
        ''' Sichtbarkeit): rendert NUR die betroffene Objekt-Region ASYNCHRON in die Szene. Ein
        ''' synchroner Vollaufbau blockierte die UI sekundenlang, sobald ein grosser weicher
        ''' Pinselstrich existiert (der Strich wird bei jedem ueberlappenden Render komplett neu
        ''' gezeichnet). Ohne Rect (leer) wird die ganze Szene asynchron erneuert.</summary>
        Private Sub RefreshOverlayAfterAnnotationChange(Optional dirtyRect As SKRectI = Nothing)
            If Not _suppressPreviewDirty Then
                _hasChanges = True
                Me.RaisePropertyChanged(NameOf(HasUnsavedChanges))
            End If
            _previewTimer.Stop()
            _previewPending = False
            If _sceneSk Is Nothing Then
                ' Kalter Start: der asynchrone Vollrender baut die Szene inkl. Objekten auf.
                SchedulePreviewUpdate()
                Return
            End If
            Dim rect = dirtyRect
            If rect.IsEmpty Then rect = New SKRectI(0, 0, _sceneSk.Width, _sceneSk.Height)
            RequestSceneRegionRender(rect)
        End Sub

        ''' <summary>Preview-Raum-Dirty-Rect eines Objekts (inkl. Effektraender) - fuer regionsbezogene
        ''' Struktur-Updates.</summary>
        Private Function ComputeSceneDirtyRectFor(annotation As ImageAnnotation) As SKRectI
            Dim previewSource = GetPreviewSource()
            If previewSource Is Nothing OrElse annotation Is Nothing Then Return SKRectI.Empty
            Return ImageProcessor.ComputeAnnotationDirtyRect(previewSource.Width, previewSource.Height, annotation,
                                                             GetBaseWidth(), GetBaseHeight())
        End Function

        ''' <summary>Ob (und wo) die Szene einen Blend-Composite braucht - unabhaengig davon, ob er gerade
        ''' renderbar ist. Billige Rechteck-Rechnung ohne Cache-Zugriff.</summary>
        Private Function SceneBlendCompositeRequiredRect() As (RequiresComposite As Boolean, Rect As SKRectI)
            Dim previewSource = GetPreviewSource()
            If previewSource Is Nothing Then Return (False, SKRectI.Empty)
            Return OverlaySceneRenderer.ComputeSceneBlendCompositeRect(_annotations,
                                                                       previewSource.Width, previewSource.Height,
                                                                       GetBaseWidth(), GetBaseHeight())
        End Function

        ''' Wie SchedulePreviewUpdate, markiert das Dokument aber NICHT als geändert (_hasChanges) -
        ''' für Werkzeuge mit expliziter "Anwenden"-Bestätigung (Zuschneiden/Bildgröße/Leinwandgröße/
        ''' Drehen): Live-Werte sollen sofort in der Vorschau sichtbar sein (siehe
        ''' GetCurrentAdjustments(forPreview:=True)), aber weder als ungespeicherte Änderung zählen
        ''' noch das kanonische Ergebnis beeinflussen, bis der Nutzer "Anwenden" klickt.
        Private Sub ScheduleToolPreviewUpdate()
            _previewPending = True
            StatusText = LocalizationService.T("Vorschau wird aktualisiert...")
            RestartPreviewTimer(PreviewDebounceMs)
        End Sub

        Private Sub ScheduleAnnotationCompositePreviewUpdate(Optional delayMs As Double = PreviewDebounceMs)
            _annotationCompositePreviewPending = True
            _annotationCompositePreviewRetries = 0
            _previewPending = True
            StatusText = LocalizationService.T("Vorschau wird aktualisiert...")
            RestartPreviewTimer(delayMs)
        End Sub

        Private Sub OnPreviewTimerTick()
            If _annotationCompositePreviewPending Then
                Dim hasDirtyPatch = Not _annotationDirtyRect.IsEmpty
                If TryRenderAnnotationPatchSync() OrElse ((Not hasDirtyPatch) AndAlso TryRenderAnnotationOverlaySync()) Then
                    _annotationCompositePreviewPending = False
                    _annotationCompositePreviewRetries = 0
                    Return
                End If

                ' Annotation-only bleibt Annotation-only: bei gesperrtem/kaltem Cache nicht auf den
                ' teuren Full-Render wechseln, sondern kurz später erneut versuchen. Das Live-Overlay
                ' bleibt sichtbar und die UI wird nicht durch Objekt-Loslassen blockiert.
                _annotationCompositePreviewRetries += 1
                If _annotationCompositePreviewRetries >= 6 Then
                    _annotationCompositePreviewPending = False
                    _annotationCompositePreviewRetries = 0
                    _previewPending = False
                    StatusText = LocalizationService.T("Vorschau bereit")
                    Return
                End If
                RestartPreviewTimer(Math.Max(40.0, PreviewDebounceMs * 0.5))
                Return
            End If

            _previewPending = False
            UpdatePreview()
        End Sub

        Private Sub RestartPreviewTimer(delayMs As Double)
            _previewTimer.Stop()
            _previewTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(1.0, delayMs))
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

        Private Shared Function AnnotationRequiresBakedPreview(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing Then Return False
            Return Not String.Equals(If(annotation.BlendMode, "Normal").Trim(), "Normal", StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function EditorRendersAnnotationAsOverlay(annotation As ImageAnnotation) As Boolean
            Return OverlaySceneRenderer.IsOverlayAnnotation(annotation)
        End Function

        Private Function SelectedAnnotationRequiresBakedPreview() As Boolean
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return False
            Return AnnotationRequiresBakedPreview(_annotations(_selectedAnnotationIndex))
        End Function

        ''' <summary>STUFE 2: Anpassungssatz fuer die SZENE - wie die Vorschau, aber MIT allen
        ''' Overlay-Objekten (dieselbe Zeichnung wie beim Export). Waehrend eines Placement-Edits wird
        ''' das aktiv bearbeitete Objekt ausgeblendet: seine Live-Darstellung kommt vom Ghost
        ''' (Selektions-Overlay), in der Szene stuende es doppelt bzw. stale an der alten Position.</summary>
        Private Function GetSceneAdjustments() As ImageAdjustments
            Dim adj = GetCurrentAdjustments(forPreview:=True, includeEditorOverlayAnnotations:=True)
            If _annotationPlacementEditActive AndAlso
               _selectedAnnotationIndex >= 0 AndAlso adj.Annotations IsNot Nothing AndAlso
               _selectedAnnotationIndex < adj.Annotations.Count Then
                adj.Annotations(_selectedAnnotationIndex).IsVisible = False
            End If
            Return adj
        End Function

        ''' <summary>Ersetzt die persistente Szene komplett (nach einem Vollrender) und blittet sie in
        ''' die persistente Anzeige. Uebernimmt die Ownership von sceneSk.</summary>
        Private Sub SetSceneBitmap(sceneSk As SKBitmap)
            If sceneSk Is Nothing Then Return
            Dim previous = _sceneSk
            _sceneSk = sceneSk
            If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, sceneSk) Then previous.Dispose()
            _sceneContentVersion += 1
            InvalidateZoomDetail()
            EnsureSceneDisplay()
            BlitSceneRegionToDisplay(New SKRectI(0, 0, _sceneSk.Width, _sceneSk.Height))
        End Sub

        ''' <summary>Stellt sicher, dass die persistente Anzeige-Bitmap existiert und zur Szene passt
        ''' (Groesse). Nur bei Groessenwechsel entsteht eine neue Instanz (PreviewImage-Setter disposed
        ''' die alte).</summary>
        Private Sub EnsureSceneDisplay()
            If _sceneSk Is Nothing Then Return
            ' ABSICHERUNG (Ursache offen): eine bereits disposte Anzeige wie Nothing
            ' behandeln und neu aufbauen, statt mit ObjectDisposedException abzustuerzen. Die
            ' Log-Zeile haelt die Faehrte zur eigentlichen Dispose-Quelle offen.
            Dim displayWidth = -1
            Dim displayHeight = -1
            If _sceneDisplay IsNot Nothing Then
                Try
                    displayWidth = _sceneDisplay.PixelSize.Width
                    displayHeight = _sceneDisplay.PixelSize.Height
                Catch ex As ObjectDisposedException
                    DiagnosticLogService.LogAlways("Editor.SceneDisplay",
                                                   "disposedDetected=EnsureSceneDisplay - Anzeige wird neu aufgebaut")
                    _sceneDisplay = Nothing
                End Try
            End If
            If _sceneDisplay Is Nothing OrElse
               displayWidth <> _sceneSk.Width OrElse
               displayHeight <> _sceneSk.Height Then
                _sceneDisplay = New WriteableBitmap(New Avalonia.PixelSize(_sceneSk.Width, _sceneSk.Height),
                                                    New Avalonia.Vector(96, 96),
                                                    Avalonia.Platform.PixelFormat.Rgba8888,
                                                    Avalonia.Platform.AlphaFormat.Premul)
                PreviewImage = _sceneDisplay
            End If
        End Sub

        ''' <summary>Kopiert NUR die Zeilen des Rects aus der Szene (Rgba8888) in die persistente
        ''' Anzeige-Bitmap und meldet der View das Neuzeichnen (SceneInvalidated). Kein neues Bitmap,
        ''' kein Vollbild-Upload - der Grund, warum Regler wieder fein bedienbar sind.</summary>
        Private Sub BlitSceneRegionToDisplay(rect As SKRectI)
            If _sceneSk Is Nothing OrElse _sceneDisplay Is Nothing OrElse rect.IsEmpty Then Return
            Dim clamped = New SKRectI(Math.Max(0, rect.Left), Math.Max(0, rect.Top),
                                      Math.Min(_sceneSk.Width, rect.Right), Math.Min(_sceneSk.Height, rect.Bottom))
            If clamped.Width <= 0 OrElse clamped.Height <= 0 Then Return
            Try
                Using fb = _sceneDisplay.Lock()
                    Dim srcStride = _sceneSk.RowBytes
                    Dim dstStride = fb.RowBytes
                    Dim srcBase = _sceneSk.GetPixels()
                    Dim bytes = clamped.Width * 4
                    Dim buffer(bytes - 1) As Byte
                    For y = clamped.Top To clamped.Bottom - 1
                        Runtime.InteropServices.Marshal.Copy(IntPtr.Add(srcBase, y * srcStride + clamped.Left * 4), buffer, 0, bytes)
                        Runtime.InteropServices.Marshal.Copy(buffer, 0, IntPtr.Add(fb.Address, y * dstStride + clamped.Left * 4), bytes)
                    Next
                End Using
            Catch ex As ObjectDisposedException
                ' ABSICHERUNG: Anzeige wurde unter uns disposed - neu aufbauen und
                ' die KOMPLETTE Szene blitten (die neue Bitmap ist leer, nicht nur die Region).
                DiagnosticLogService.LogAlways("Editor.SceneDisplay",
                                               "disposedDetected=BlitSceneRegionToDisplay - Anzeige wird neu aufgebaut")
                _sceneDisplay = Nothing
                EnsureSceneDisplay()
                If _sceneDisplay Is Nothing Then Return
                BlitSceneRegionToDisplay(New SKRectI(0, 0, _sceneSk.Width, _sceneSk.Height))
                Return
            End Try
            RaiseEvent SceneInvalidated(Me, EventArgs.Empty)
        End Sub

        ''' <summary>STUFE 2: rendert eine Region (Basis + Striche + ALLE Objekte in Z-Order) in die
        ''' persistente Szene und aktualisiert die Anzeige. Schneidet die Aenderung einen
        ''' Mischmodus-Abhaengigkeitsbereich, wird dieser automatisch mitgerendert (das Blend-Ergebnis
        ''' haengt vom Untergrund ab). False bei kaltem/gesperrtem Base-Cache oder fehlender Szene -
        ''' der Aufrufer plant dann den asynchronen Vollrender.</summary>
        Private Function TryRenderSceneRegionSync(dirtyRect As SKRectI) As Boolean
            Dim previewSource = GetPreviewSource()
            If previewSource Is Nothing OrElse _sceneSk Is Nothing OrElse dirtyRect.IsEmpty Then Return False

            Dim rect = dirtyRect
            Dim blendDep = SceneBlendCompositeRequiredRect()
            If blendDep.RequiresComposite AndAlso Not blendDep.Rect.IsEmpty AndAlso
               OverlaySceneRenderer.Intersects(rect, blendDep.Rect) Then
                rect = ImageProcessor.UnionRects(rect, blendDep.Rect)
            End If

            Dim sw = Diagnostics.Stopwatch.StartNew()
            Dim clamped As SKRectI
            Dim patch As SKBitmap
            Try
                patch = ImageProcessor.TryRenderAnnotationsPatchSkOnCachedBase(previewSource, GetSceneAdjustments(), rect, clamped)
            Catch ex As Exception
                DiagnosticLogService.LogException("EditorSceneRegion", ex)
                Return False
            End Try
            If patch Is Nothing Then
                DiagnosticLogService.LogAlways("Editor.SceneRegion",
                                               $"fallback=true reason=cacheMissOrBusy rect={rect.Left},{rect.Top},{rect.Width}x{rect.Height} ms={sw.ElapsedMilliseconds}")
                Return False
            End If

            Using patch
                Using canvas = New SKCanvas(_sceneSk)
                    ' Region ERSETZEN statt mischen (BlendMode.Src): das Patch ist das fertige Komposit,
                    ' inkl. Transparenz bei ausgeblendetem Hintergrund.
                    Using replacePaint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        canvas.DrawBitmap(patch, clamped.Left, clamped.Top, replacePaint)
                    End Using
                End Using
            End Using
            _sceneContentVersion += 1
            InvalidateZoomDetail()
            EnsureSceneDisplay()
            BlitSceneRegionToDisplay(clamped)
            _annotationDirtyRect = SKRectI.Empty
            _previewPending = False
            StatusText = LocalizationService.T("Vorschau bereit")
            DiagnosticLogService.LogAlways("Editor.SceneRegion",
                                           $"rect={clamped.Left},{clamped.Top},{clamped.Width}x{clamped.Height} pixels={CLng(clamped.Width) * CLng(clamped.Height)} ms={sw.ElapsedMilliseconds}")
            Return True
        End Function

        ''' <summary>Baut die komplette Szene synchron auf dem gecachten Base neu - fuer
        ''' Strukturaenderungen (Anlegen/Loeschen/Umsortieren/Sichtbarkeit), bei denen kein enges
        ''' Dirty-Rect vorliegt.</summary>
        Private Function TryRenderSceneFullSync() As Boolean
            If _sceneSk Is Nothing Then Return False
            Return TryRenderSceneRegionSync(New SKRectI(0, 0, _sceneSk.Width, _sceneSk.Height))
        End Function

        ''' <summary>ASYNCHRONER Region-Render: reiht das Rect in die Pending-Union ein und startet bei
        ''' Bedarf den Worker. Fuer Regler-Bursts und Drag-Starts - der UI-Thread bleibt frei, waehrend
        ''' der Effekt-Render (Schatten/Gluehen grosser Objekte: 200-800 ms) im Hintergrund laeuft; das
        ''' Anwenden auf die Szene kostet nur ~20 ms. Ueberholende Anforderungen verschmelzen zur Union;
        ''' ein waehrenddessen ausgetauschtes _sceneSk (Vollrender) verwirft das Ergebnis und rendert neu.</summary>
        Private Sub RequestSceneRegionRender(dirtyRect As SKRectI)
            If dirtyRect.IsEmpty Then Return
            _sceneRegionPendingRect = ImageProcessor.UnionRects(_sceneRegionPendingRect, dirtyRect)
            If _sceneRegionWorkerBusy Then Return
            RunSceneRegionWorker()
        End Sub

        Private Async Sub RunSceneRegionWorker()
            _sceneRegionWorkerBusy = True
            Try
                While Not _sceneRegionPendingRect.IsEmpty
                    Dim previewSource = GetPreviewSource()
                    Dim sceneAtStart = _sceneSk
                    If previewSource Is Nothing OrElse sceneAtStart Is Nothing Then
                        ' Keine Szene (kalter Start): der Vollrender ist unterwegs bzw. wird geplant.
                        _sceneRegionPendingRect = SKRectI.Empty
                        Return
                    End If

                    Dim rect = _sceneRegionPendingRect
                    _sceneRegionPendingRect = SKRectI.Empty
                    Dim blendDep = SceneBlendCompositeRequiredRect()
                    If blendDep.RequiresComposite AndAlso Not blendDep.Rect.IsEmpty AndAlso
                       OverlaySceneRenderer.Intersects(rect, blendDep.Rect) Then
                        rect = ImageProcessor.UnionRects(rect, blendDep.Rect)
                    End If

                    Dim adj = GetSceneAdjustments()
                    Dim versionAtStart = _sceneContentVersion
                    ' Merken, ob der Snapshot das aktiv gezogene Objekt AUSBLENDET: gilt die Ausblendung
                    ' beim Anwenden nicht mehr (Loslassen/Commit), wuerde der Patch das Objekt loeschen.
                    Dim excludedPlacementIndex = If(_annotationPlacementEditActive, _selectedAnnotationIndex, -1)
                    Dim sw = Diagnostics.Stopwatch.StartNew()
                    Dim clamped As SKRectI = SKRectI.Empty
                    Dim patch As SKBitmap = Nothing
                    Try
                        patch = Await Task.Run(Function()
                                                   Dim localClamped As SKRectI
                                                   Dim p = ImageProcessor.TryRenderAnnotationsPatchSkOnCachedBase(previewSource, adj, rect, localClamped)
                                                   clamped = localClamped
                                                   Return p
                                               End Function)
                    Catch ex As Exception
                        DiagnosticLogService.LogException("EditorSceneRegionAsync", ex)
                        Return
                    End Try

                    If patch Is Nothing Then
                        ' Base-Cache kalt/gesperrt: kurz debounced nachziehen (Timer-Pfad rendert dann).
                        DiagnosticLogService.LogAlways("Editor.SceneRegion",
                                                       $"fallback=true reason=cacheMissOrBusy async=1 rect={rect.Left},{rect.Top},{rect.Width}x{rect.Height}")
                        _annotationDirtyRect = ImageProcessor.UnionRects(_annotationDirtyRect, rect)
                        ScheduleAnnotationCompositePreviewUpdate(60.0)
                        Return
                    End If

                    Dim placementExclusionStale = excludedPlacementIndex >= 0 AndAlso
                                                  (Not _annotationPlacementEditActive OrElse _selectedAnnotationIndex <> excludedPlacementIndex)
                    If placementExclusionStale OrElse
                       Not Object.ReferenceEquals(_sceneSk, sceneAtStart) OrElse _sceneContentVersion <> versionAtStart Then
                        ' Szene wurde waehrenddessen ersetzt (Vollrender) ODER ihr INHALT hat sich
                        ' geaendert (z.B. Objekt synchron angelegt, waehrend ein langer Strich-Render
                        ' lief): das Ergebnis basiert auf einem alten Snapshot und wuerde die Region
                        ' mit veraltetem Stand ueberschreiben (Sichtbefund: Objekt verschwindet, sobald
                        ' Pinselstriche im Spiel sind). Verwerfen und mit frischem Snapshot neu rendern.
                        patch.Dispose()
                        _sceneRegionPendingRect = ImageProcessor.UnionRects(_sceneRegionPendingRect, rect)
                        Continue While
                    End If

                    Using patch
                        Using canvas = New SKCanvas(_sceneSk)
                            Using replacePaint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                                canvas.DrawBitmap(patch, clamped.Left, clamped.Top, replacePaint)
                            End Using
                        End Using
                    End Using
                    _sceneContentVersion += 1
                    InvalidateZoomDetail()
                    EnsureSceneDisplay()
                    BlitSceneRegionToDisplay(clamped)
                    ClearPlacementGhostLinger()
                    _previewPending = False
                    StatusText = LocalizationService.T("Vorschau bereit")
                    DiagnosticLogService.LogAlways("Editor.SceneRegion",
                                                   $"async=1 rect={clamped.Left},{clamped.Top},{clamped.Width}x{clamped.Height} pixels={CLng(clamped.Width) * CLng(clamped.Height)} ms={sw.ElapsedMilliseconds}")
                End While
            Finally
                _sceneRegionWorkerBusy = False
            End Try
        End Sub

        ''' <summary>Beim App-Beenden aufrufen (MainWindow.HandleWindowClosing): legt laufende
        ''' Szene-/Zoom-Arbeiten still. Absturzbild (beim Beenden): der Fenster-
        ''' Teardown disposed die Anzeige-Bitmap, waehrend eine Region-Worker-Fortsetzung noch
        ''' aussteht - EnsureSceneDisplay griff dann auf die disposte Instanz. Hier wird NICHTS
        ''' disposed (in-flight-Renders koennten noch lesen), nur Referenzen gekappt und die
        ''' Version gebumpt, damit jede Fortsetzung ihr Ergebnis verwirft.</summary>
        Public Sub ShutdownSceneWork()
            _sceneContentVersion += 1
            _sceneRegionPendingRect = SKRectI.Empty
            _sceneDisplay = Nothing
            _sceneSk = Nothing
            ResetZoomDetail()
        End Sub

        ' ===================== STUFE 3: Zoom-Detail =====================

        Public ReadOnly Property ZoomDetailImage As Bitmap
            Get
                Return _zoomDetailImage
            End Get
        End Property

        ''' Vorher-Seite des Zoom-Details (Original nur mit Geometrie) - Nothing, solange kein
        ''' Vergleich aktiv ist oder das Vorher-Detail noch nicht gerendert wurde.
        Public ReadOnly Property ZoomDetailBeforeImage As Bitmap
            Get
                Return _zoomDetailBeforeImage
            End Get
        End Property

        Public ReadOnly Property ZoomDetailFracLeft As Double
            Get
                Return _zoomDetailFracLeft
            End Get
        End Property

        Public ReadOnly Property ZoomDetailFracTop As Double
            Get
                Return _zoomDetailFracTop
            End Get
        End Property

        Public ReadOnly Property ZoomDetailFracWidth As Double
            Get
                Return _zoomDetailFracWidth
            End Get
        End Property

        Public ReadOnly Property ZoomDetailFracHeight As Double
            Get
                Return _zoomDetailFracHeight
            End Get
        End Property

        ''' <summary>STUFE 3: Die View meldet bei jedem Layout-Durchlauf den sichtbaren Bildausschnitt
        ''' (Anteile 0..1) und ob die Anzeige die Szenen-Aufloesung uebersteigt. Passt der gecachte
        ''' Detail-Stand (Version + Abdeckung), passiert nichts bzw. nur ein billiger Region-Blit;
        ''' sonst wird der teure Detail-Render debounced geplant. active=False raeumt alles weg.</summary>
        Public Sub UpdateZoomDetailViewport(visLeft As Double, visTop As Double,
                                            visRight As Double, visBottom As Double,
                                            active As Boolean,
                                            Optional wantBefore As Boolean = False)
            _zoomDetailVisLeft = visLeft
            _zoomDetailVisTop = visTop
            _zoomDetailVisRight = visRight
            _zoomDetailVisBottom = visBottom

            ' SetZoomDetailImage/SetZoomDetailBeforeImage feuern PropertyChanged synchron. Die View
            ' positioniert daraufhin sofort das Overlay und meldet den Viewport erneut. Ohne Guard
            ' kann das mitten in ExtractZoomDetailRegion wieder eine Extraktion ausloesen - besonders
            ' im Vergleichsmodus, bevor die Vorher-Seite gesetzt ist.
            If _zoomDetailExtracting Then
                If active Then
                    _zoomDetailWanted = True
                    _zoomDetailWantBefore = wantBefore
                End If
                Return
            End If

            If Not active Then
                ResetZoomDetail()
                Return
            End If
            _zoomDetailWanted = True
            _zoomDetailWantBefore = wantBefore
            If Not wantBefore AndAlso _zoomDetailBeforeImage IsNot Nothing Then SetZoomDetailBeforeImage(Nothing)

            ' Waehrend Placement-Edit/Retusche-Zug aendert sich der Szeneninhalt laufend - kein
            ' Detail zeigen (es waere sofort veraltet); der Commit bumpt die Version und plant neu.
            If _annotationPlacementEditActive OrElse _retouchStrokeActive Then
                SetZoomDetailImage(Nothing)
                SetZoomDetailBeforeImage(Nothing)
                Return
            End If

            ' Detail-Stand passt nur, wenn auch die Vorher-Szene da ist, falls sie gebraucht wird -
            ' sonst neu rendern (der Render laedt dann beide Seiten).
            If _zoomDetailSk IsNot Nothing AndAlso _zoomDetailVersion = _sceneContentVersion AndAlso
               (Not wantBefore OrElse _zoomDetailBeforeSk IsNot Nothing) Then
                If _zoomDetailImage IsNot Nothing AndAlso ZoomDetailExtractCoversVisible() AndAlso
                   (Not wantBefore OrElse _zoomDetailBeforeImage IsNot Nothing) Then Return
                ExtractZoomDetailRegion()
                Return
            End If

            ' Kein passender Detail-Stand: nichts Veraltetes zeigen, Render debounced anstossen.
            SetZoomDetailImage(Nothing)
            SetZoomDetailBeforeImage(Nothing)
            RestartZoomDetailTimer()
        End Sub

        ''' <summary>Szeneninhalt hat sich geaendert (Versions-Bump): veraltetes Detail sofort
        ''' ausblenden und - falls der Zoom noch aktiv ist - den Neu-Render debounced planen.</summary>
        Private Sub InvalidateZoomDetail()
            If _zoomDetailImage IsNot Nothing Then SetZoomDetailImage(Nothing)
            If _zoomDetailBeforeImage IsNot Nothing Then SetZoomDetailBeforeImage(Nothing)
            If _zoomDetailWanted Then RestartZoomDetailTimer()
        End Sub

        ''' <summary>Zoom verlassen/Bildwechsel: Overlay aus, Caches freigeben. Laeuft gerade ein
        ''' Render, wird das Dispose deferred (Radiergummi-Lektion: nie unter einem laufenden
        ''' Hintergrund-Render wegdisposen).</summary>
        Private Sub ResetZoomDetail()
            _zoomDetailWanted = False
            _zoomDetailWantBefore = False
            _zoomDetailTimer?.Stop()
            SetZoomDetailImage(Nothing)
            SetZoomDetailBeforeImage(Nothing)
            If _zoomDetailRendering Then
                _zoomDetailDisposePending = True
                Return
            End If
            _zoomDetailSk?.Dispose()
            _zoomDetailSk = Nothing
            _zoomDetailVersion = -1
            _zoomDetailSource?.Dispose()
            _zoomDetailSource = Nothing
            _zoomDetailSourcePath = Nothing
            _zoomDetailSourceWorkingVersion = -1
            _zoomDetailBeforeSk?.Dispose()
            _zoomDetailBeforeSk = Nothing
            _zoomDetailBeforeSource?.Dispose()
            _zoomDetailBeforeSource = Nothing
            _zoomDetailBeforeSourcePath = Nothing
        End Sub

        Private Sub SetZoomDetailImage(value As Bitmap)
            If Object.ReferenceEquals(_zoomDetailImage, value) Then Return
            Dim previous = _zoomDetailImage
            _zoomDetailImage = value
            If previous IsNot Nothing Then DisposeDeferred(previous)
            Me.RaisePropertyChanged(NameOf(ZoomDetailImage))
        End Sub

        Private Sub SetZoomDetailBeforeImage(value As Bitmap)
            If Object.ReferenceEquals(_zoomDetailBeforeImage, value) Then Return
            Dim previous = _zoomDetailBeforeImage
            _zoomDetailBeforeImage = value
            If previous IsNot Nothing Then DisposeDeferred(previous)
            Me.RaisePropertyChanged(NameOf(ZoomDetailBeforeImage))
        End Sub

        Private Sub RestartZoomDetailTimer()
            If _zoomDetailTimer Is Nothing Then
                _zoomDetailTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(350)}
                AddHandler _zoomDetailTimer.Tick, Sub()
                                                      _zoomDetailTimer.Stop()
                                                      BeginZoomDetailRenderAsync()
                                                  End Sub
            End If
            _zoomDetailTimer.Stop()
            _zoomDetailTimer.Start()
        End Sub

        ''' <summary>Rendert die Detail-Szene asynchron (Task.Run): Quelle laden (bzw. Cache), voller
        ''' Renderer mit den aktuellen Szenen-Einstellungen. Sequenz- und Versions-Guards verwerfen
        ''' ueberholte Ergebnisse; ein Versions-Wechsel waehrend des Renders plant automatisch neu.</summary>
        Private Async Sub BeginZoomDetailRenderAsync()
            If Not _zoomDetailWanted OrElse _zoomDetailRendering Then Return
            If _annotationPlacementEditActive OrElse _retouchStrokeActive Then Return
            If _sceneSk Is Nothing Then Return

            ' Lohnt nur, wenn die Quelle spuerbar mehr Aufloesung hat als die Szene.
            Dim baseLongest = Math.Max(GetBaseWidth(), GetBaseHeight())
            Dim sceneLongest = Math.Max(_sceneSk.Width, _sceneSk.Height)
            Dim detailTarget = Math.Min(ZoomDetailMaxDimension, baseLongest)
            If detailTarget <= CInt(sceneLongest * 1.15) Then Return

            Dim path = RenderSourcePath
            If String.IsNullOrWhiteSpace(path) Then Return

            _zoomDetailRendering = True
            _zoomDetailRenderSeq += 1
            Dim seq = _zoomDetailRenderSeq
            Dim versionAtStart = _sceneContentVersion
            Dim adj = GetSceneAdjustments()
            ' Quelle nur wiederverwenden, wenn sie zu Pfad UND Arbeitsbild-Version passt (die
            ' Ziel-Aufloesung ist je Bild konstant; die Version wandert bei jedem Commit weiter).
            Dim workingVersionAtStart = _workingImage.Version
            Dim cachedSource = If(String.Equals(_zoomDetailSourcePath, path, StringComparison.Ordinal) AndAlso
                                  _zoomDetailSourceWorkingVersion = workingVersionAtStart, _zoomDetailSource, Nothing)
            ' Vorher-Seite (Vergleich sichtbar): hochaufgeloester ORIGINAL-Decode, nur pfadabhaengig
            ' (das Original aendert sich durch Commits nicht).
            Dim wantBefore = _zoomDetailWantBefore
            Dim cachedBeforeSource = If(String.Equals(_zoomDetailBeforeSourcePath, path, StringComparison.Ordinal),
                                        _zoomDetailBeforeSource, Nothing)

            Dim sw = Diagnostics.Stopwatch.StartNew()
            Dim source As SKBitmap = Nothing
            Dim rendered As SKBitmap = Nothing
            Dim beforeSource As SKBitmap = Nothing
            Dim beforeRendered As SKBitmap = Nothing
            Try
                Await Task.Run(Sub()
                                   ' Arbeitsbild statt Datei-Decode (Stufe C): RenderDownscale ist
                                   ' threadsicher; Rueckfall auf den Datei-Decode nur, falls das
                                   ' Arbeitsbild (noch) nicht initialisiert ist.
                                   source = If(cachedSource,
                                               If(_workingImage.RenderDownscale(detailTarget),
                                                  ImageProcessor.LoadPreviewSource(path, detailTarget)))
                                   If source Is Nothing Then Return
                                   rendered = ImageProcessor.RenderPreviewSkBitmap(source, adj)
                                   If wantBefore Then
                                       beforeSource = If(cachedBeforeSource,
                                                         ImageProcessor.LoadPreviewSource(path, detailTarget))
                                       If beforeSource IsNot Nothing Then
                                           beforeRendered = ImageProcessor.ApplyGeometryAdjustmentsSk(beforeSource, adj)
                                       End If
                                   End If
                               End Sub)
            Catch ex As Exception
                DiagnosticLogService.LogException("EditorZoomDetail", ex)
            Finally
                _zoomDetailRendering = False
            End Try

            ' Reset lief waehrenddessen (Zoom raus/Bildwechsel): alles Frische entsorgen.
            If _zoomDetailDisposePending OrElse Not _zoomDetailWanted OrElse seq <> _zoomDetailRenderSeq Then
                _zoomDetailDisposePending = False
                rendered?.Dispose()
                beforeRendered?.Dispose()
                If source IsNot Nothing AndAlso Not Object.ReferenceEquals(source, _zoomDetailSource) Then source.Dispose()
                If beforeSource IsNot Nothing AndAlso Not Object.ReferenceEquals(beforeSource, _zoomDetailBeforeSource) Then beforeSource.Dispose()
                If Not _zoomDetailWanted Then ResetZoomDetail()
                Return
            End If

            ' Frisch geladene Quellen in den Cache uebernehmen (fuer die naechsten Renders).
            If source IsNot Nothing AndAlso Not Object.ReferenceEquals(source, _zoomDetailSource) Then
                _zoomDetailSource?.Dispose()
                _zoomDetailSource = source
                _zoomDetailSourcePath = path
                _zoomDetailSourceWorkingVersion = workingVersionAtStart
            End If
            If beforeSource IsNot Nothing AndAlso Not Object.ReferenceEquals(beforeSource, _zoomDetailBeforeSource) Then
                _zoomDetailBeforeSource?.Dispose()
                _zoomDetailBeforeSource = beforeSource
                _zoomDetailBeforeSourcePath = path
            End If
            If rendered Is Nothing Then Return

            If versionAtStart <> _sceneContentVersion Then
                ' Inhalt hat sich waehrend des Renders geaendert: verwerfen und SOFORT neu starten
                ' (der Bump liegt in der Vergangenheit - der 350-ms-Timer wuerde nur Zeit kosten).
                ' Die Log-Zeile zeigt, falls Renders dauerhaft im Kreis verworfen werden.
                rendered.Dispose()
                beforeRendered?.Dispose()
                DiagnosticLogService.LogAlways("Editor.ZoomDetail",
                                               $"discarded=version ms={sw.ElapsedMilliseconds}")
                BeginZoomDetailRenderAsync()
                Return
            End If

            _zoomDetailSk?.Dispose()
            _zoomDetailSk = rendered
            _zoomDetailVersion = versionAtStart
            If beforeRendered IsNot Nothing Then
                _zoomDetailBeforeSk?.Dispose()
                _zoomDetailBeforeSk = beforeRendered
            ElseIf Not wantBefore AndAlso _zoomDetailBeforeSk IsNot Nothing Then
                _zoomDetailBeforeSk.Dispose()
                _zoomDetailBeforeSk = Nothing
            End If
            DiagnosticLogService.LogAlways("Editor.ZoomDetail",
                                           $"rendered={rendered.Width}x{rendered.Height} before={beforeRendered IsNot Nothing} ms={sw.ElapsedMilliseconds}")
            ExtractZoomDetailRegion()
        End Sub

        ''' <summary>Blittet den sichtbaren Ausschnitt (+50 % Rand je Seite) aus der Detail-Szene in
        ''' ein kleines Anzeige-Bitmap. Der Rand macht Pans billig: das Overlay ist bildverankert und
        ''' wandert mit; erst ausserhalb des Rands wird neu geblittet.</summary>
        Private Sub ExtractZoomDetailRegion()
            If _zoomDetailExtracting Then Return
            If _zoomDetailSk Is Nothing Then Return
            _zoomDetailExtracting = True
            Try
                Dim dw = _zoomDetailSk.Width
                Dim dh = _zoomDetailSk.Height
                If dw <= 0 OrElse dh <= 0 Then Return

                Dim marginX = Math.Max(0.01, (_zoomDetailVisRight - _zoomDetailVisLeft) * 0.5)
                Dim marginY = Math.Max(0.01, (_zoomDetailVisBottom - _zoomDetailVisTop) * 0.5)
                Dim fracL = Math.Max(0.0, _zoomDetailVisLeft - marginX)
                Dim fracT = Math.Max(0.0, _zoomDetailVisTop - marginY)
                Dim fracR = Math.Min(1.0, _zoomDetailVisRight + marginX)
                Dim fracB = Math.Min(1.0, _zoomDetailVisBottom + marginY)

                Dim rect = New SKRectI(CInt(Math.Floor(fracL * dw)), CInt(Math.Floor(fracT * dh)),
                                       CInt(Math.Ceiling(fracR * dw)), CInt(Math.Ceiling(fracB * dh)))
                rect = New SKRectI(Math.Max(0, rect.Left), Math.Max(0, rect.Top),
                                   Math.Min(dw, rect.Right), Math.Min(dh, rect.Bottom))
                If rect.Width <= 0 OrElse rect.Height <= 0 Then
                    SetZoomDetailImage(Nothing)
                    Return
                End If

                Dim bmp = ImageProcessor.RenderBitmapPatch(_zoomDetailSk, rect)
                If bmp Is Nothing Then
                    SetZoomDetailImage(Nothing)
                    Return
                End If
                _zoomDetailFracLeft = rect.Left / CDbl(dw)
                _zoomDetailFracTop = rect.Top / CDbl(dh)
                _zoomDetailFracWidth = rect.Width / CDbl(dw)
                _zoomDetailFracHeight = rect.Height / CDbl(dh)
                SetZoomDetailImage(bmp)

                ' Vorher-Seite: gleicher Ausschnitt aus der Vorher-Detail-Szene. Nur bei identischen
                ' Maßen (beide Seiten laufen durch dieselbe Geometrie auf gleich großen Quellen) - bei
                ' Abweichung lieber kein Vorher-Detail als ein verschobenes.
                If _zoomDetailWantBefore AndAlso _zoomDetailBeforeSk IsNot Nothing AndAlso
                   _zoomDetailBeforeSk.Width = dw AndAlso _zoomDetailBeforeSk.Height = dh Then
                    SetZoomDetailBeforeImage(ImageProcessor.RenderBitmapPatch(_zoomDetailBeforeSk, rect))
                Else
                    SetZoomDetailBeforeImage(Nothing)
                End If
            Finally
                _zoomDetailExtracting = False
            End Try
        End Sub

        ''' Deckt der aktuelle Ausschnitt (inkl. Rand) den sichtbaren Bereich noch ab?
        Private Function ZoomDetailExtractCoversVisible() As Boolean
            Const eps As Double = 0.0005
            Return _zoomDetailVisLeft >= _zoomDetailFracLeft - eps AndAlso
                   _zoomDetailVisTop >= _zoomDetailFracTop - eps AndAlso
                   _zoomDetailVisRight <= _zoomDetailFracLeft + _zoomDetailFracWidth + eps AndAlso
                   _zoomDetailVisBottom <= _zoomDetailFracTop + _zoomDetailFracHeight + eps
        End Function

        ' ===================== ENDE STUFE 3 =====================

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

            ' STUFE 2: die "komplette Annotations-Neukomposition" ist jetzt ein Szenen-Vollaufbau auf
            ' dem gecachten Base (gleiche Kosten, aber die Szene bleibt die einzige Wahrheit).
            If Not TryRenderSceneFullSync() Then Return False
            Return True
        End Function

        Private Function TryRenderAnnotationPatchSync() As Boolean
            Dim previewSource = GetPreviewSource()
            If previewSource Is Nothing Then Return False

            Dim rect = _annotationDirtyRect
            If rect.IsEmpty AndAlso _selectedAnnotationIndex >= 0 AndAlso _selectedAnnotationIndex < _annotations.Count Then
                rect = ImageProcessor.ComputeAnnotationDirtyRect(previewSource.Width, previewSource.Height, _annotations(_selectedAnnotationIndex),
                                                                 GetBaseWidth(), GetBaseHeight())
            End If
            If rect.IsEmpty Then Return False
            If _sceneSk Is Nothing Then Return False

            ' STUFE 2: ASYNCHRON einreihen statt synchron zu rendern - der Worker haelt waehrend
            ' langer Renders (grosse Striche/Effekte) den Base-Cache-Lock; ein synchroner Versuch
            ' liefe mit TryEnter(12ms) ins Leere, die Retries erschoepften sich und der finale
            ' Stand (z.B. der Text nach dem Aufziehen) wuerde NIE gebacken. Die Pending-Union und
    ' der Versions-/Placement-Guard des Workers stellen die richtige Endfassung sicher.
            _annotationDirtyRect = SKRectI.Empty
            RequestSceneRegionRender(rect)
            Return True
        End Function

        Private Sub RefreshSelectedAnnotationPreviewImmediatelyIfNeeded()
            ' Im Objekt-Anpassungsmodus (Anpassen/Farbe/Details+Effekte/Filter auf ein Objekt) leben die
            ' Reglerwerte bis zum Commit NUR im Editor-Puffer - das echte Objekt (das die Layer zeichnet)
            ' bekommt sie erst bei der Deselektion. Die Vorschau muss deshalb ueber den BAKED-Zweig laufen
            ' (Patch mit GetCurrentAdjustments, das die Livewerte in den Klon schreibt), sonst wirken die
            ' Regler scheinbar erst nach der Deselektion. Der Overlay-Zweig gilt nur ausserhalb davon.
            If _selectedAnnotationIndex >= 0 AndAlso _selectedAnnotationIndex < _annotations.Count AndAlso
               EditorRendersAnnotationAsOverlay(_annotations(_selectedAnnotationIndex)) AndAlso
               Not IsObjectAdjustTool(_currentTool) Then
                _previewTimer.Stop()
                _previewPending = False
                ' Ghost-Bitmap nur aktualisieren, wenn es sichtbar ist (Placement-Edit) - der Render
                ' inkl. Schatten/Gluehen ist teuer und waere pro Regler-Tick reine Verschwendung.
                If _annotationPlacementEditActive Then UpdateSelectedAnnotationOverlayPreview()
                ' STUFE 2: Eigenschafts-Aenderung des selektierten Objekts ASYNCHRON in die Szene
                ' rendern - Effekt-Renders (Schatten/Gluehen grosser Objekte) kosten 200-800 ms und
                ' wuerden synchron den UI-Thread wuergen (Regler "quasi nicht bedienbar"). Der Worker
                ' koalesziert Bursts per Rect-Union von selbst.
                Dim previewSourceForRect = GetPreviewSource()
                Dim rect = _annotationDirtyRect
                If rect.IsEmpty AndAlso previewSourceForRect IsNot Nothing AndAlso
                   _selectedAnnotationIndex >= 0 AndAlso _selectedAnnotationIndex < _annotations.Count Then
                    rect = ImageProcessor.ComputeAnnotationDirtyRect(previewSourceForRect.Width, previewSourceForRect.Height,
                                                                     _annotations(_selectedAnnotationIndex),
                                                                     GetBaseWidth(), GetBaseHeight())
                End If
                _annotationDirtyRect = SKRectI.Empty
                RequestSceneRegionRender(rect)
                Return
            End If

            If SelectedAnnotationRequiresBakedPreview() OrElse IsObjectAdjustTool(_currentTool) Then
                ' DEBOUNCED statt sofort: Presets (XMP/LUT/Filter auf ein Objekt) setzen DUTZENDE
                ' Properties in einem Rutsch - ein synchroner Patch-Render pro Property (je ~200-400 ms)
                ' fror die UI viele Sekunden ein (CPU hoch, "nichts passiert"). Der Debounce buendelt
                ' den Burst zu EINEM Patch-Render kurz nach dem letzten Wert; einzelne Reglerzuege
                ' bekommen dieselben ~90 ms wie die normalen Bild-Regler.
                ScheduleAnnotationCompositePreviewUpdate()
                Return
            End If

            SchedulePreviewUpdate()
        End Sub

        Private Sub SchedulePreviewForCurrentTarget()
            If HasSelectedAnnotation AndAlso IsObjectAdjustTool(_currentTool) Then
                RefreshSelectedAnnotationPreviewImmediatelyIfNeeded()
            Else
                SchedulePreviewUpdate()
            End If
        End Sub

        Public Sub CommitSelectedAnnotationPlacementEdit()
            If Not HasSelectedAnnotation Then Return
            _previewTimer.Stop()
            _previewPending = False
            UpdateSelectedAnnotationOverlayPreview()
            ' STUFE 2: das Objekt an der Endposition in die Szene backen (End lief vor Commit,
            ' die Placement-Ausblendung ist also schon aufgehoben). Dirty = alte + neue Bounds
            ' aus der Zieh-Sitzung (_annotationDirtyRect).
            If Not TryRenderAnnotationPatchSync() Then ScheduleAnnotationCompositePreviewUpdate(40.0)
        End Sub

        Public Sub BeginSelectedAnnotationPlacementEdit()
            If Not HasSelectedAnnotation Then Return
            _annotationPlacementEditActive = True
            _placementGhostLinger = False
            _annotationPlacementStartDirtyRect = SKRectI.Empty
            _previewTimer.Stop()
            _previewPending = False
            Me.RaisePropertyChanged(NameOf(ShowSelectedSvgOverlay))
            ' STUFE 2: einheitlich fuer ALLE Objektarten - die Szene rendert die Startregion OHNE das
            ' Objekt (GetSceneAdjustments blendet es waehrend _annotationPlacementEditActive aus); die
            ' Live-Darstellung waehrend des Ziehens uebernimmt der Ghost (Selektions-Overlay).
            Dim previewSource = GetPreviewSource()
            If previewSource IsNot Nothing Then
                _annotationPlacementStartDirtyRect = ImageProcessor.ComputeAnnotationDirtyRect(previewSource.Width,
                                                                                              previewSource.Height,
                                                                                              _annotations(_selectedAnnotationIndex),
                                                                                              GetBaseWidth(), GetBaseHeight())
                ' _annotationDirtyRect bleibt gesetzt, damit der Commit alte+neue Bounds vereint.
                _annotationDirtyRect = _annotationPlacementStartDirtyRect
            End If
            ' KOMPLETT ASYNCHRON: weder Ghost-Render (Effekte: 100-300 ms) noch das Herausloesen aus
            ' der Szene duerfen den Drag-Start blockieren (~1 s Haenger). Bis der Ghost steht, bleibt
            ' die Szene-Kopie sichtbar (kein Loch); danach uebernimmt der Ghost und die Kopie
            ' verschwindet einen Wimpernschlag spaeter.
            BeginPlacementGhostAsync()
        End Sub

        ''' <summary>Rendert den Drag-Ghost im Hintergrund und loest ERST DANACH die Szene-Kopie heraus -
        ''' Reihenfolge wichtig, sonst klafft am Drag-Start kurz ein Loch. Ueberholende Aufrufe
        ''' (schnelles erneutes Greifen) verwerfen aeltere Ergebnisse per Sequenznummer.</summary>
        Private Async Sub BeginPlacementGhostAsync()
            Dim idx = _selectedAnnotationIndex
            If idx < 0 OrElse idx >= _annotations.Count Then Return
            Dim annotation = _annotations(idx)
            If Not UsesRenderedSelectionOverlay(annotation) AndAlso Not TextRendersInOverlay(annotation) Then
                SetSelectedAnnotationOverlay(Nothing)
                Return
            End If

            Dim clone = annotation.Clone()
            Dim pixelWidth = Math.Max(48, CInt(Math.Round(annotation.WidthPixels)))
            Dim pixelHeight = Math.Max(48, CInt(Math.Round(annotation.HeightPixels)))
            Dim seq = Threading.Interlocked.Increment(_ghostRenderSeq)
            Dim render As ImageProcessor.AnnotationOverlayRender = Nothing
            Try
                render = Await Task.Run(Function() ImageProcessor.RenderAnnotationOverlay(clone, pixelWidth, pixelHeight))
            Catch ex As Exception
                DiagnosticLogService.LogException("EditorGhostRender", ex)
                Return
            End Try
            If seq <> _ghostRenderSeq OrElse Not _annotationPlacementEditActive OrElse _selectedAnnotationIndex <> idx Then
                render?.Image?.Dispose()
                Return
            End If

            SetSelectedAnnotationOverlay(render)
            Me.RaisePropertyChanged(NameOf(ShowSelectedSvgOverlay))
            If Not _annotationPlacementStartDirtyRect.IsEmpty Then
                ' ATOMAR im selben UI-Frame (Nutzer-Befund 17.07.: Objekt kurz doppelt am Zug-START):
                ' der Ghost erscheint und die Szene verliert das Objekt in EINEM Durchlauf. Der
                ' Lösch-Render ist billig - das gezogene Objekt ist ausgeblendet, gerendert werden
                ' nur Basis + andere Objekte der Region (keine Effekt-Kosten des Objekts selbst).
                ' Nur bei kaltem/gesperrtem Base-Cache bleibt der asynchrone Weg (kurzes Doppel
                ' möglich, aber selten). Das Leeren von _annotationDirtyRect im Sync-Pfad ist
                ' korrekt: die Altregion ist danach bereits sauber, der Commit braucht nur noch
                ' die Endposition (Fallback in TryRenderAnnotationPatchSync).
                If Not TryRenderSceneRegionSync(_annotationPlacementStartDirtyRect) Then
                    RequestSceneRegionRender(_annotationPlacementStartDirtyRect)
                End If
            End If
        End Sub

        Public Sub EndSelectedAnnotationPlacementEdit()
            _annotationPlacementEditActive = False
            _annotationPlacementStartDirtyRect = SKRectI.Empty
            ' Ghost weiterzeigen, bis die Szene das Objekt an der Endposition gerendert hat
            ' (ClearPlacementGhostLinger im Region-Worker/Vollrender) - sonst fehlt das Objekt
            ' für die Renderdauer im Bild.
            If _selectedAnnotationOverlayImage IsNot Nothing Then _placementGhostLinger = True
            Me.RaisePropertyChanged(NameOf(ShowSelectedSvgOverlay))
        End Sub

        ''' Wrapper um NotifyAnnotationOverlayStateChanged, der Aufrufe unterdrückt, solange
        ''' _overlayNotifySuppressDepth > 0 - siehe Kommentar am Feld. Aufrufer, die mehrere
        ''' zusammengehörige Statements klammern wollen, erhöhen/verringern die Tiefe und rufen
        ''' NotifyAnnotationOverlayStateChanged() danach genau einmal direkt auf.
        Private Sub RequestOverlayStateNotify()
            If _overlayNotifySuppressDepth > 0 Then Return
            NotifyAnnotationOverlayStateChanged()
        End Sub

        ''' STUFE 2: Selektions-/Werkzeugwechsel aendern den Szeneninhalt NICHT mehr (alle Objekte
        ''' sind immer in der Szene; der Ghost existiert nur waehrend eines Placement-Edits).
        Private Sub NotifyAnnotationOverlayStateChanged()
            _previewPending = False
            StatusText = LocalizationService.T("Vorschau bereit")
        End Sub

        ''' <summary>Quellwechsel Teil 1: alles aufräumen, was zur ALTEN Quelle gehört. Rein
        ''' UI-Thread, billig. Liefert die Marke des Wechsels zurück - oder -1, wenn es nichts zu
        ''' laden gibt (dann ist bereits geleert).</summary>
        Private Function BeginPreviewSourceSwap(imagePath As String) As Long
            InvalidatePreviewWork()
            DisposeRetouchLiveBuffers()
            ClearRetouchLivePatch()
            ResetZoomDetail()
            ' Szene gehoert zur alten Quelle: Felder zuruecksetzen, BEVOR irgendein Worker/Apply sie
            ' wieder anfasst (alles UI-Thread, daher rennt hier nichts dazwischen). Der Versions-Bump
            ' laesst in-flight Worker-Ergebnisse sauber verwerfen.
            _annotationDirtyRect = SKRectI.Empty
            _sceneRegionPendingRect = SKRectI.Empty
            _sceneContentVersion += 1
            _sceneSk?.Dispose()
            _sceneSk = Nothing
            _sceneDisplay = Nothing

            ' EIGENER Zähler, nicht _previewRequestId: der wird auch von jedem Render-Start
            ' hochgezählt (RegisterPreviewRenderStart). Als Veraltungs-Marke für den Quellwechsel
            ' wäre er untauglich - ein Render, der während des Decodes anläuft, würde ein völlig
            ' gültiges Ergebnis verwerfen lassen.
            Dim token = Threading.Interlocked.Increment(_previewSourceSwapId)

            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then
                ClearPreviewSource()
                Return -1
            End If

            ' Farbraum-Diagnose (Untersuchung "Vorher/Nachher dunkler"): NUR auf Anforderung.
            ' Sie war als "temporär" markiert, lief aber bei JEDEM Laden mit - und sie ist nicht
            ' billig: voller Datei-Decode plus zwei bildgroße Zeichenvorgänge, gemessen 243 ms bei
            ' einem 12-MP-NEF, obendrauf auf den ohnehin teuren RAW-Weg. Die Untersuchung ist noch
            ' offen, deshalb bleibt das Werkzeug erhalten - aber hinter einem Schalter:
            '   FERRUMPIX_COLOR_DIAG=1 ferrumpix
            If Environment.GetEnvironmentVariable("FERRUMPIX_COLOR_DIAG") = "1" Then
                ImageProcessor.LogDecodeColorDiagnostics(RenderSourcePath)
            End If
            Return token
        End Function

        ''' <summary>Quellwechsel Teil 2: der TEURE Decode. Fasst keinen UI-Zustand an und darf
        ''' deshalb im Hintergrund laufen - bei RAW steckt hier die komplette Entwicklung.
        '''
        ''' Bei einer .fpx mit voll aufgelöstem retouch.png ist DAS BÜNDEL-Arbeitsbild der Decode
        ''' (Striche/Retusche bereits eingebacken); ein Vorschauauflösungs-Altbestand
        ''' (Seed 2026-07-17) fällt über die Maße-Prüfung sauber auf das Basisbild zurück.</summary>
        Private Shared Function DecodeForPreviewSource(imagePath As String, overridePath As String) As (Full As SKBitmap, Baked As Boolean)
            Dim fullDecode As SKBitmap = Nothing
            Dim bakedFromFpx = False
            If Not String.IsNullOrEmpty(overridePath) AndAlso File.Exists(overridePath) Then
                fullDecode = ImageProcessor.DecodeWorkingImage(overridePath)
                If fullDecode IsNot Nothing Then
                    Dim baseSize = ImageProcessor.GetOrientedImageSize(imagePath)
                    If baseSize.Width > 0 AndAlso (fullDecode.Width <> baseSize.Width OrElse fullDecode.Height <> baseSize.Height) Then
                        ' NIE hochskalieren - lieber Basis dekodieren (Striche älterer Dateien fehlen dann).
                        DiagnosticLogService.LogAlways("Editor.WorkingImage",
                            $"fpxOverride rejected stage={fullDecode.Width}x{fullDecode.Height} base={baseSize.Width}x{baseSize.Height}")
                        fullDecode.Dispose()
                        fullDecode = Nothing
                    Else
                        bakedFromFpx = True
                        DiagnosticLogService.LogAlways("Editor.WorkingImage",
                            $"fpxOverride used size={fullDecode.Width}x{fullDecode.Height}")
                    End If
                End If
            End If
            If fullDecode Is Nothing Then fullDecode = ImageProcessor.DecodeWorkingImage(imagePath)
            Return (fullDecode, bakedFromFpx)
        End Function

        ''' <summary>Quellwechsel Teil 3: das Ergebnis übernehmen - wieder UI-Thread.
        ''' Ist die Marke veraltet (der Nutzer hat inzwischen weitergeblättert), wird das
        ''' Dekodierte verworfen statt über die neue Quelle geschrieben.</summary>
        Private Function CompletePreviewSourceSwap(decoded As (Full As SKBitmap, Baked As Boolean),
                                                   token As Long, scheduleInitialRender As Boolean) As Boolean
            If token < 0 OrElse Threading.Interlocked.Read(_previewSourceSwapId) <> token Then
                ' Veraltet: NICHT entsperren - der neue Wechsel hat seine eigene Sperre gesetzt und
                ' ist noch unterwegs. Ein Entsperren hier gäbe die Werkzeuge frei, obwohl das
                ' Arbeitsbild der NEUEN Quelle noch fehlt.
                decoded.Full?.Dispose()
                Return False
            End If
            SetWorkingImagePending(False)

            Dim source = If(decoded.Full IsNot Nothing,
                            _workingImage.Init(decoded.Full, PreviewMaxDimension,
                                               hasBakedContent:=decoded.Baked,
                                               hasAlphaHoles:=(decoded.Baked AndAlso _workingImageOverrideHasAlpha) OrElse _newDocTransparentBackground),
                            Nothing)
            If source Is Nothing Then
                ClearPreviewSource()
                Return False
            End If

            ' ANZEIGE-BILD aus dem gerade dekodierten Arbeitsbild ableiten, statt die Datei ein
            ' ZWEITES Mal zu lesen. Vorher rief der Aufrufer direkt danach
            ' ImageOrientationService.LoadOrientedAvaloniaBitmapAuto(RenderSourcePath) - ein
            ' kompletter zweiter Decode desselben Bildes, bei RAW ohne brauchbare eingebettete
            ' Vorschau sogar eine zweite volle Entwicklung.
            ' WithFull statt CloneFull: ToAvaloniaBitmap kopiert die Pixel ohnehin in ein
            ' Avalonia-Bitmap, eine 180-MB-Zwischenkopie waere reine Verschwendung.
            CurrentImage = _workingImage.WithFull(Function(full) ImageProcessor.ToAvaloniaBitmap(full))

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
            ReleaseComparisonOriginalSource()
            ImageProcessor.ClearBaseCache()
            TryDisposeStalePreviewSources()

            ' Den Base-Cache sofort wärmen: EIN initialer Voll-Render nach dem Quellwechsel (async,
            ' gedeckelte Auflösung ~1 s). Ohne ihn bleibt der Cache kalt, bis der Nutzer die erste
            ' Anpassung macht - und ALLE Patch-Pfade (Blend, Malen, Objekt-Move) schlagen bis dahin
            ' still mit cacheMissOrBusy fehl, weil der Annotation-Pfad bewusst nie zum Full-Render
            ' eskaliert (Log-Befund 2026-07-16).
            If scheduleInitialRender Then ScheduleToolPreviewUpdate()
            Return True
        End Function

        ''' <summary>Synchrone Fassung - für Aufrufer, die das Ergebnis SOFORT brauchen
        ''' (UpdatePreviewAsync liest direkt danach GetPreviewSource, ResetAdjustmentsInternal baut
        ''' das Arbeitsbild neu auf). Die Öffnen-Pfade nehmen PreparePreviewSourceAsync.</summary>
        Private Sub PreparePreviewSource(imagePath As String, Optional scheduleInitialRender As Boolean = True)
            Dim token = BeginPreviewSourceSwap(imagePath)
            If token < 0 Then Return
            CompletePreviewSourceSwap(DecodeForPreviewSource(imagePath, _workingImageOverridePath),
                                      token, scheduleInitialRender)
        End Sub

        ''' <summary>Dasselbe, aber der teure Decode läuft im Hintergrund - die Oberfläche friert
        ''' beim Öffnen nicht mehr ein. Blättert der Nutzer währenddessen weiter, verwirft
        ''' CompletePreviewSourceSwap das Ergebnis anhand der Marke.</summary>
        Private Async Function PreparePreviewSourceAsync(imagePath As String,
                                                         Optional scheduleInitialRender As Boolean = True) As Task
            Dim token = BeginPreviewSourceSwap(imagePath)
            If token < 0 Then Return

            ' RAW: die EINGEBETTETE Vorschau sofort zeigen, damit nicht Sekunden lang eine leere
            ' Fläche steht. Bewusst ExtractPreview und NICHT ExtractPreviewWithFallback - dessen
            ' dritte Stufe entwickelt die Datei selbst und würde genau die Blockade zurückholen,
            ' die dieser Umbau beseitigt. Findet der Scanner nichts (etwa bei Leica-RAWs), bleibt
            ' es beim bisherigen Verhalten: kurz leer, bis die Entwicklung steht.
            If RawPreviewService.IsSupportedRaw(imagePath) Then
                Try
                    Using vorschau = RawPreviewService.ExtractPreview(imagePath)
                        If vorschau IsNot Nothing AndAlso vorschau.Length > 0 Then
                            CurrentImage = ImageOrientationService.LoadOrientedAvaloniaBitmap(vorschau)
                        End If
                    End Using
                Catch ex As Exception
                    DiagnosticLogService.LogException("Editor.RawQuickPreview", ex)
                End Try
                ' Auch ohne Vorschau sperren: entscheidend ist, dass es noch KEIN Arbeitsbild gibt.
                SetWorkingImagePending(True)
            End If

            ' Feld VOR dem Wechsel in den Hintergrund lesen - es gehört dem UI-Thread.
            Dim overridePath = _workingImageOverridePath
            Dim decoded = Await Task.Run(Function() DecodeForPreviewSource(imagePath, overridePath))
            CompletePreviewSourceSwap(decoded, token, scheduleInitialRender)
        End Function

        Private Sub ClearPreviewSource()
            ' Ohne Quelle gibt es auch nichts mehr zu entwickeln - eine noch stehende
            ' "RAW wird entwickelt"-Sperre bliebe sonst fuer immer haengen.
            SetWorkingImagePending(False)
            InvalidatePreviewWork()
            DisposeRetouchLiveBuffers()
            ClearRetouchLivePatch()
            ResetZoomDetail()
            ' Szene-Felder zuruecksetzen - siehe Kommentar in PreparePreviewSource.
            _annotationDirtyRect = SKRectI.Empty
            _sceneRegionPendingRect = SKRectI.Empty
            _sceneContentVersion += 1
            _sceneSk?.Dispose()
            _sceneSk = Nothing
            _sceneDisplay = Nothing
            Dim oldSource As SKBitmap = Nothing
            SyncLock _previewSync
                oldSource = _previewSource
                _previewSource = Nothing
                If oldSource IsNot Nothing Then
                    _stalePreviewSources.Add(oldSource)
                End If
            End SyncLock
            ' Arbeitsbild gehört zur alten Quelle (das Vorschau-Bitmap entsorgt die
            ' Stale-Mechanik oben, das Voll-Bitmap der Service selbst).
            _workingImage.Clear()
            ReleaseComparisonOriginalSource()
            ImageProcessor.ClearBaseCache()
            TryDisposeStalePreviewSources()
        End Sub

        Private Sub CleanupCurrentFpxTempDir()
            Dim tempDir = _currentFpxTempDir
            _currentFpxTempDir = ""
            If String.IsNullOrWhiteSpace(tempDir) Then Return
            Try
                If Directory.Exists(tempDir) Then Directory.Delete(tempDir, True)
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.FpxTempCleanup", ex)
            End Try
        End Sub

        ''' <summary>Meldet alles, was am Zustand „neues Bild" hängt. An JEDER Stelle aufzurufen, die
        ''' _isNewDocument ändert - sonst bliebe etwa der Filmstreifen in seinem alten Zustand.</summary>
        Private Sub RaiseNewDocumentStateChanged()
            Me.RaisePropertyChanged(NameOf(IsNewDocument))
            Me.RaisePropertyChanged(NameOf(ShowFilmstrip))
            Me.RaisePropertyChanged(NameOf(CanToggleFilmstrip))
        End Sub

        ''' <summary>Räumt den Temp-Ordner eines neuen, nie gespeicherten Dokuments weg. Analog zu
        ''' CleanupCurrentFpxTempDir und an denselben Stellen aufgerufen.</summary>
        Private Sub CleanupCurrentNewDocTempDir()
            Dim tempDir = _currentNewDocTempDir
            _currentNewDocTempDir = ""
            _isNewDocument = False
            _newDocTransparentBackground = False
            RaiseNewDocumentStateChanged()
            If String.IsNullOrWhiteSpace(tempDir) Then Return
            Try
                If Directory.Exists(tempDir) Then Directory.Delete(tempDir, True)
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.NewDocTempCleanup", ex)
            End Try
        End Sub

        ''' <summary>Verwirft ein nie gespeichertes neues Bild: Temp-Ordner weg, Editor leer. Danach
        ''' greift der Platzhalter (HasDocument = False), und nichts zeigt mehr in den Temp-Ordner.
        ''' Die Rückfrage nach ungespeicherten Änderungen ist zu diesem Zeitpunkt bereits gelaufen.</summary>
        Private Sub DiscardNewDocument()
            CurrentImage = Nothing
            PreviewImage = Nothing
            ComparisonImage = Nothing
            ClearPreviewSource()
            CleanupCurrentNewDocTempDir()
            CleanupCurrentFpxTempDir()
            CurrentImagePath = ""
            _currentImagePath = ""
            _renderSourcePathOverride = ""
            _workingImageOverridePath = ""
            _workingImageOverrideHasAlpha = False
            _currentFpxPath = ""
            _forceSaveAsOnly = False
            ClearFilmstrip()
            ClearUndoHistory()
            ExifInfo = Nothing
            ClearHistogramData()
            StatusText = ""
            _hasChanges = False
            Me.RaisePropertyChanged(NameOf(HasUnsavedChanges))
            Me.RaisePropertyChanged(NameOf(IsNewDocument))
        End Sub

        ''' <summary>Übernimmt die Dialogwerte und legt das leere Dokument an.</summary>
        Private Async Function ConfirmNewDocumentAsync() As Task
            Dim widthPx = NewDocPixelWidth
            Dim heightPx = NewDocPixelHeight
            If widthPx <= 0 OrElse heightPx <= 0 Then Return

            ' Deckel gegen Vertipper: 30000 px Kantenlänge sind bereits ~3,6 GB im Arbeitsbild.
            If widthPx > 30000 OrElse heightPx > 30000 Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Neues Bild"),
                                               LocalizationService.T("Die gewünschte Größe ist zu groß. Erlaubt sind bis zu 30000 Pixel je Kante."))
                Return
            End If

            IsNewDocumentDialogOpen = False
            Await CreateNewDocumentAsync(widthPx, heightPx, _newDocBackgroundMode, _newDocBackgroundColor)
        End Function

        ''' <summary>Legt ein leeres Dokument an und öffnet es im Editor.
        '''
        ''' Der Weg führt bewusst über eine TEMP-DATEI statt über einen pfadlosen Zustand: die ganze
        ''' Ladekette (PreparePreviewSource, ExifService, FileInfo, FpxService.Save) setzt eine
        ''' existierende Datei voraus, und ~15 Stellen lesen einen leeren _currentImagePath als „kein
        ''' Dokument". Genau dieses Muster benutzt schon der .fpx-Zweig von OpenImageAsync für sein
        ''' entpacktes Basisbild. Nebeneffekt, der uns entgegenkommt: die Nachfrage beim Verlassen
        ''' (OpenImageAsync-Kopf) greift für ein ungespeichertes neues Dokument von allein.</summary>
        Private Async Function CreateNewDocumentAsync(widthPx As Integer, heightPx As Integer,
                                                      backgroundMode As String, backgroundColor As String) As Task(Of Boolean)
            Dim transparent = String.Equals(backgroundMode, "Transparent", StringComparison.Ordinal)
            Dim tempDir = ""
            Dim tempPath = ""
            Dim createFailed = False

            Try
                tempDir = IO.Path.Combine(IO.Path.GetTempPath(), "FerrumPix", "NewDocument", Guid.NewGuid().ToString("N"))
                Directory.CreateDirectory(tempDir)
                ' PNG, damit ein transparenter Hintergrund die Datei übersteht.
                tempPath = IO.Path.Combine(tempDir, LocalizationService.T("Unbenannt") & ".png")

                Await Task.Run(Sub()
                                   ' Direkt in Bgra8888/Premul anlegen - das Format, auf das
                                   ' WorkingImageService.Init sonst erst umkopieren müsste.
                                   Using bmp As New SKBitmap(New SKImageInfo(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Premul))
                                       Using canvas As New SKCanvas(bmp)
                                           If transparent Then
                                               canvas.Clear(SKColors.Transparent)
                                           Else
                                               canvas.Clear(ParseNewDocColor(backgroundMode, backgroundColor))
                                           End If
                                       End Using
                                       Using image = SKImage.FromBitmap(bmp)
                                           Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                                               Using fs = File.Create(tempPath)
                                                   data.SaveTo(fs)
                                               End Using
                                           End Using
                                       End Using
                                   End Using
                               End Sub)
            Catch ex As Exception
                ' Await ist in VB in einem Catch-Block nicht erlaubt - deshalb nur merken und die
                ' Meldung unten zeigen.
                DiagnosticLogService.LogException("Editor.CreateNewDocument", ex)
                createFailed = True
            End Try

            If createFailed Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Neues Bild"),
                                               LocalizationService.T("Das leere Bild konnte nicht angelegt werden."))
                Return False
            End If

            ' Erst NACH dem erfolgreichen Schreiben übernehmen: OpenImageAsync räumt den alten
            ' Temp-Ordner auf und darf dabei nicht den gerade angelegten erwischen.
            _pendingNewDocTempDir = tempDir
            _pendingNewDocTransparent = transparent

            ' Die einelementige Pfadliste ist Absicht: ohne allPaths liest LoadFilmstripContext das
            ' VERZEICHNIS des Pfads aus - also den Temp-Ordner. So zeigt der Filmstreifen „1 / 1".
            Dim opened = Await OpenImageAsync(tempPath, New List(Of String) From {tempPath}, forceSaveAsOnly:=True)
            If Not opened Then
                _pendingNewDocTempDir = ""
                _pendingNewDocTransparent = False
                Try
                    If Directory.Exists(tempDir) Then Directory.Delete(tempDir, True)
                Catch
                End Try
                Return False
            End If

            ' Ein frisch angelegtes Dokument ist noch unverändert - kein „ungespeicherte Änderungen".
            _hasChanges = False
            Me.RaisePropertyChanged(NameOf(HasUnsavedChanges))
            Me.RaisePropertyChanged(NameOf(IsNewDocument))
            Return True
        End Function

        Private Shared Function ParseNewDocColor(backgroundMode As String, backgroundColor As String) As SKColor
            If Not String.Equals(backgroundMode, "Color", StringComparison.Ordinal) Then Return SKColors.White
            Dim parsed As SKColor
            If SKColor.TryParse(backgroundColor, parsed) Then Return parsed
            Return SKColors.White
        End Function

        Private Function GetPreviewSource() As SKBitmap
            ' Ausgeblendete Pixel-Ebene: der Render laeuft auf dem UNGEBACKENEN Originaldecode statt auf
            ' der Arbeitsbild-Ableitung. Beide haben dieselben Masse (gleicher Pfad, gleiches
            ' PreviewMaxDimension), Dirty-Rects und Objektgeometrie bleiben also gueltig. Malen ist
            ' waehrenddessen gesperrt (siehe CanUsePixelTools), deshalb kann kein Commit ins Leere laufen.
            If _pixelLayerHidden Then
                Dim unbaked = GetComparisonOriginalSource()
                If unbaked IsNot Nothing Then Return unbaked
            End If
            SyncLock _previewSync
                Return _previewSource
            End SyncLock
        End Function

        ''' <summary>Arbeitsbild fuer den Voll-Render (Export/Speichern). Nothing bei ausgeblendeter
        ''' Pixel-Ebene - der Renderer faellt dann auf den Datei-Decode des Basisbilds zurueck, also auf
        ''' denselben ungebackenen Stand, den die Vorschau zeigt.</summary>
        Private Function CloneWorkingFullForRender() As SKBitmap
            If _pixelLayerHidden Then Return Nothing
            Return _workingImage.CloneFull()
        End Function

        ''' <summary>Liefert (und cached) den Vorschau-Decode der ORIGINAL-Datei für das
        ''' „Vorher"-Bild. Läuft im Hintergrund-Render (Task.Run); der Tausch geht über die
        ''' Stale-Liste, damit ein in-flight-Render seine Quelle behalten darf.</summary>
        Private Function GetComparisonOriginalSource() As SKBitmap
            Dim path = RenderSourcePath
            If String.IsNullOrWhiteSpace(path) Then Return Nothing
            SyncLock _previewSync
                If _comparisonOriginalSource IsNot Nothing AndAlso
                   String.Equals(_comparisonOriginalPath, path, StringComparison.Ordinal) Then
                    Return _comparisonOriginalSource
                End If
            End SyncLock
            Dim decoded = ImageProcessor.LoadPreviewSource(path, PreviewMaxDimension)
            If decoded Is Nothing Then Return Nothing
            SyncLock _previewSync
                If _comparisonOriginalSource IsNot Nothing Then _stalePreviewSources.Add(_comparisonOriginalSource)
                _comparisonOriginalSource = decoded
                _comparisonOriginalPath = path
            End SyncLock
            Return decoded
        End Function

        Private Sub ReleaseComparisonOriginalSource()
            SyncLock _previewSync
                If _comparisonOriginalSource IsNot Nothing Then
                    _stalePreviewSources.Add(_comparisonOriginalSource)
                    _comparisonOriginalSource = Nothing
                    _comparisonOriginalPath = Nothing
                End If
            End SyncLock
        End Sub

        ''' <summary>Maße-Invariante des Arbeitsbild-Umbaus: die gesamte Koordinatenmathematik
        ''' (GetBaseWidth/Height) fußt auf CurrentImage - das Arbeitsbild muss exakt gleich groß
        ''' sein, sonst verrutschen Spots/Striche/Objekte. Nur Diagnose-Log, kein Eingriff
        ''' (bekannter Kandidat: ICO, das CurrentImage anders dekodiert als die Pipeline).</summary>
        Private Sub VerifyWorkingImageDimensions()
            If CurrentImage Is Nothing OrElse Not _workingImage.IsInitialized Then Return
            If _workingImage.FullWidth <> CurrentImage.PixelSize.Width OrElse
               _workingImage.FullHeight <> CurrentImage.PixelSize.Height Then
                DiagnosticLogService.LogAlways("Editor.WorkingImage",
                    $"dimensionMismatch working={_workingImage.FullWidth}x{_workingImage.FullHeight} currentImage={CurrentImage.PixelSize.Width}x{CurrentImage.PixelSize.Height} path={IO.Path.GetFileName(RenderSourcePath)}")
            End If
        End Sub

        Private Function SaveCurrentPreviewImageToPngStream() As IO.MemoryStream
            Dim preview = PreviewImage
            If preview Is Nothing Then Return Nothing
            Dim ms As New IO.MemoryStream()
            preview.Save(ms, PngBitmapEncoderOptions.Default)
            ms.Position = 0
            Return ms
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
                ' Kein zweiter Render nötig - dieser Aufruf rendert gleich selbst.
                PreparePreviewSource(RenderSourcePath, scheduleInitialRender:=False)
                previewSource = GetPreviewSource()
                If previewSource Is Nothing Then Return
            End If

            RegisterPreviewRenderStart()
            Dim requestId = _previewRequestId
            ' Arbeitsbild-Stand beim Render-START: landet waehrend des asynchronen Renders ein
            ' neuer Commit (Strich/Retusche), ist die frisch gebaute Szene veraltet - der
            ' Commit-Callback plant dann ohnehin einen neuen Render (SchedulePreviewUpdate).
            ' STUFE 2: Die Szene enthaelt ALLE Overlay-Objekte (gleiche Zeichnung wie beim Export);
            ' waehrend eines Placement-Edits ist das aktiv bearbeitete Objekt ausgeblendet (Ghost).
            Dim adj = GetSceneAdjustments()
            Dim cts = New CancellationTokenSource()
            Dim token = cts.Token
            Dim oldCts = Interlocked.Exchange(_previewRenderCts, cts)
            CancelAndDisposePreviewCts(oldCts)

            Try
                StatusText = LocalizationService.T("Vorschau wird berechnet…")
                PreviewFailed = False
                Dim needsComparison = _showBeforeImage
                Dim fullRenderSw = Diagnostics.Stopwatch.StartNew()
                Dim result = Await Task.Run(Function()
                                                token.ThrowIfCancellationRequested()
                                                ' Szene als SKBitmap (persistente Wahrheit) + Avalonia-Konvertierung fuer
                                                ' die Anzeige - beides im Hintergrund, der UI-Thread uebernimmt nur noch.
                                                ' WICHTIG: die cache-waermende Variante - sonst schlagen alle
                                                ' Region-Renders dauerhaft mit cacheMissOrBusy fehl.
                                                Dim sceneSk = ImageProcessor.RenderSceneSkCached(previewSource, adj)
                                                Dim comparisonBmp As Bitmap = Nothing
                                                Try
                                                    token.ThrowIfCancellationRequested()
                                                    ' Das Vorher/Nachher-Vergleichsbild wird nur berechnet, wenn der
                                                    ' Vorher/Nachher-Regler gerade sichtbar ist (ShowBeforeImage) - sonst
                                                    ' wäre das bei jedem einzelnen Live-Vorschau-Frame verschwendete Arbeit.
                                                    If needsComparison Then
                                                        ' „Vorher" = ORIGINAL-Datei (eigener Decode), nicht die Arbeitsbild-
                                                        ' Vorschau - die enthält ab Stufe D gebackene Retusche/Striche.
                                                        Dim comparisonSource = GetComparisonOriginalSource()
                                                        comparisonBmp = ImageProcessor.ApplyGeometryAdjustments(If(comparisonSource, previewSource), adj)
                                                    End If
                                                    ' Anzeige laeuft ueber die persistente WriteableBitmap (Region-Blit
                                                    ' auf dem UI-Thread) - keine Vollkonvertierung mehr noetig.
                                                    Return New PreviewRenderResult(Nothing, comparisonBmp) With {.SceneSk = sceneSk}
                                                Catch
                                                    sceneSk?.Dispose()
                                                    comparisonBmp?.Dispose()
                                                    Throw
                                                End Try
                                            End Function, token)

                If token.IsCancellationRequested OrElse requestId <> _previewRequestId Then
                    result.Dispose()
                    Return
                End If

                SetSceneBitmap(result.SceneSk)
                result.SceneSk = Nothing
                ClearPlacementGhostLinger()
                ComparisonImage = result.Comparison
                _previewPending = False
                StatusText = LocalizationService.T("Vorschau bereit")
                PreviewFailed = False
                If _clearRetouchLivePatchAfterPreview Then
                    _clearRetouchLivePatchAfterPreview = False
                    ' Waehrend eines AKTIVEN Zugs zeigt die Bruecke bereits den neueren Stand des
                    ' laufenden Zugs - nicht wegziehen.
                    If Not _retouchStrokeActive Then ClearRetouchLivePatch()
                End If
                DiagnosticLogService.LogAlways("Editor.FullPreviewRender",
                                               $"pixels={CLng(previewSource.Width) * CLng(previewSource.Height)} size={previewSource.Width}x{previewSource.Height} retouch={If(adj.RetouchSpots Is Nothing, 0, adj.RetouchSpots.Count)} annotations={If(adj.Annotations Is Nothing, 0, adj.Annotations.Count)} ms={fullRenderSw.ElapsedMilliseconds}")
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
            ' "Speichern" bei RAW oder PSD = Rezept in die Begleitdatei (.fpxmp) schreiben -
            ' keines dieser Formate ist je ein Schreibziel.
            If Not saveAs AndAlso IsCurrentImageSidecarFormat Then Return TrySaveSidecar()
            Dim targetPath = _currentImagePath
            Dim targetQuality = SaveQuality
            Dim saveToImmich As Boolean = False
            Dim isFpxSave As Boolean = FpxService.IsFpx(targetPath)
            ' Außerhalb des If-Blocks: die Einzeloptionen (Metadaten-Übernahme) werden erst nach
            ' dem erfolgreichen Speichern ausgewertet. Nothing nur bei saveAs=False - dort wird
            ' der Übernahme-Zweig nie erreicht.
            Dim saveAsResult As SaveAsDialogResult = Nothing
            If saveAs Then
                ' Aus einem geöffneten .fpx-Projekt Name/Ordner/Formatvorschlag von der Projektdatei ableiten
                ' (nicht vom entpackten Basisbild "base" im Temp-Ordner); sonst wie gehabt.
                Dim fromFpx = Not String.IsNullOrEmpty(_currentFpxPath)
                ' Externe Quellen (Immich-Temp-Kopie) liegen im Temp-Verzeichnis - als Ziel taugt das nicht,
                ' daher den Bilder-Ordner vorschlagen statt den Temp-Pfad.
                Dim dir = If(fromFpx, IO.Path.GetDirectoryName(_currentFpxPath),
                             If(_forceSaveAsOnly, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), IO.Path.GetDirectoryName(_currentImagePath)))
                Dim name = IO.Path.GetFileNameWithoutExtension(If(fromFpx, _currentFpxPath, _currentImagePath))
                ' Ein neu angelegtes Dokument wurde nie „bearbeitet" - es heißt schlicht „Unbenannt".
                ' Ohne diesen Fall schlüge die Anwendung „Unbenannt_bearbeitet" vor.
                Dim proposedName = If(_isNewDocument, name, name & "_bearbeitet")
                ' FPX als Standard-Vorschlag (Nutzerwunsch 2026-07-17): das Projektformat erhält
                ' Regler + Objekte editierbar - der Export in JPG/PNG/WEBP bleibt eine bewusste Wahl.
                ' NormalizeSaveAsFormat fällt auf JPG zurück, falls FPX deaktiviert ist.
                Dim initialFormat = If(FpxService.Enabled, "FPX", "JPG")
                saveAsResult = Await _mainVm.ShowSaveAsAsync(LocalizationService.T("Speichern unter"),
                                                             LocalizationService.T("Dateiname eingeben"),
                                                             proposedName,
                                                             initialFormat,
                                                             SaveQuality,
                                                             LocalizationService.T("Speichern"),
                                                             LocalizationService.T("Abbrechen"))
                If saveAsResult Is Nothing OrElse String.IsNullOrWhiteSpace(saveAsResult.BaseName) Then Return False

                Dim cleanBaseName = IO.Path.GetFileNameWithoutExtension(saveAsResult.BaseName.Trim())
                If HasInvalidFileNameChars(cleanBaseName) Then
                    Await _mainVm.ShowMessageAsync(LocalizationService.T("Speichern fehlgeschlagen"), LocalizationService.T("Der Dateiname enthält ungültige Zeichen."))
                    Return False
                End If
                isFpxSave = FpxService.Enabled AndAlso saveAsResult.IsFpx
                ' Ein .fpx-Projekt geht immer lokal (Immich speichert Bilder, keine Projektdateien).
                ' Ein PDF ebenso - Immich führt Bild-Assets, keine Dokumente.
                saveToImmich = String.Equals(saveAsResult.Target, "Immich", StringComparison.OrdinalIgnoreCase) AndAlso ImmichService.IsConfigured AndAlso Not isFpxSave AndAlso Not saveAsResult.IsPdf
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
                If _retouchStrokeActive Then CommitRetouchStroke()
                If HasSelectedAnnotation Then SyncSelectedAnnotation(refreshOverlay:=False)
                CommitObjectAdjustModeToModel()
                Dim adj = GetCurrentAdjustments()
                Dim preserveMetadata = If(saveAs AndAlso _mainVm?.Settings IsNot Nothing, _mainVm.Settings.PreserveMetadataOnSave, True)
                Dim ok As Boolean
                If isFpxSave Then
                    ' Nicht-destruktiv als .fpx-Bündel sichern: das gerenderte Komposit (für die Anzeige) plus
                    ' das Rezept + Basisbild + Objekt-Assets. Das lebende Bild bleibt als Quelle unangetastet.
                    Dim sourcePath = RenderSourcePath
                    ' SELBSTHEILUNG (Nutzer-Befund 2026-07-17 „Basisbild fehlt" bei fpx→fpx): ist das
                    ' entpackte Basisbild des offenen Projekts verschwunden, das aktuelle Bündel frisch
                    ' entpacken statt mit FileNotFound zu scheitern - und den Hergang loggen, damit die
                    ' eigentliche Ursache (wer räumt den Temp-Ordner ab?) greifbar wird.
                    If Not IO.File.Exists(sourcePath) AndAlso
                       Not String.IsNullOrEmpty(_currentFpxPath) AndAlso IO.File.Exists(_currentFpxPath) Then
                        DiagnosticLogService.LogAlways("Editor.FpxSave",
                            $"missingBase source={sourcePath} tempDirExists={IO.Directory.Exists(_currentFpxTempDir)} -> reextract {IO.Path.GetFileName(_currentFpxPath)}")
                        Dim currentFpx = _currentFpxPath
                        Dim reloaded = Await Task.Run(Function() FpxService.Load(currentFpx))
                        If reloaded IsNot Nothing AndAlso IO.File.Exists(reloaded.BaseImagePath) Then
                            CleanupCurrentFpxTempDir()
                            _currentFpxTempDir = reloaded.TempDir
                            _renderSourcePathOverride = reloaded.BaseImagePath
                            _workingImageOverridePath = If(reloaded.RetouchStagePath, "")
                            sourcePath = reloaded.BaseImagePath
                        End If
                    End If
                    Dim decodeMs As Long = 0
                    Dim processMs As Long = 0
                    Dim encodeMs As Long = 0
                    Dim renderMs As Long = 0
                    Dim packageMs As Long = 0
                    Dim preparedComposite As IO.MemoryStream = Nothing
                    ' Gebackener Inhalt (Striche/Retusche) erzwingt den Vorschau-Pfad: der
                    ' Datei-Render (RenderPngStream vom Basisbild) kennt das Arbeitsbild nicht.
                    If _workingImage.HasBakedContent Then
                        Dim swPreview = Diagnostics.Stopwatch.StartNew()
                        Await UpdatePreviewAsync()
                        processMs = swPreview.ElapsedMilliseconds
                        Dim swEncode = Diagnostics.Stopwatch.StartNew()
                        preparedComposite = SaveCurrentPreviewImageToPngStream()
                        encodeMs = swEncode.ElapsedMilliseconds
                    End If
                    Dim retouchStageIncluded As Boolean = False
                    ok = Await Task.Run(Function() As Boolean
                                            Dim sw = Diagnostics.Stopwatch.StartNew()
                                            Dim composite As IO.MemoryStream = Nothing
                                            If preparedComposite IsNot Nothing Then
                                                decodeMs = 0
                                                composite = preparedComposite
                                            Else
                                                composite = ImageProcessor.RenderPngStream(sourcePath, adj, FpxCompositeMaxDimension, decodeMs, processMs, encodeMs)
                                            End If
                                            renderMs = sw.ElapsedMilliseconds
                                            If composite Is Nothing Then Return False
                                            ' retouch.png = das gebackene ARBEITSBILD in Vollauflösung; ohne
                                            ' gebackenen Inhalt entfällt der Eintrag (Basisbild reicht).
                                            Dim retouchStage = If(_workingImage.HasBakedContent,
                                                                  _workingImage.EncodeFullPng(), Nothing)
                                            retouchStageIncluded = retouchStage IsNot Nothing
                                            Try
                                                sw.Restart()
                                                FpxService.Save(targetPath, adj, sourcePath, composite, retouchStage)
                                                packageMs = sw.ElapsedMilliseconds
                                            Finally
                                                composite?.Dispose()
                                                retouchStage?.Dispose()
                                            End Try
                                            Return IO.File.Exists(targetPath)
                                        End Function)
                    DiagnosticLogService.LogAlways("Editor.FpxSave", $"{IO.Path.GetFileName(targetPath)} baked={_workingImage.HasBakedContent} annotations={adj.Annotations?.Count} retouchPng={retouchStageIncluded} decode={decodeMs}ms process={processMs}ms encodePng={encodeMs}ms renderPng={renderMs}ms package={packageMs}ms")
                Else
                    ' Arbeitsbild als Pipeline-Eingang (Stufe C): CloneFull ist threadsicher und
                    ' wartet automatisch auf einen laufenden Region-Commit.
                    ok = Await Task.Run(Function() ImageProcessor.SaveImage(RenderSourcePath, targetPath, adj, targetQuality, preserveMetadata,
                                                                            workingFull:=CloneWorkingFullForRender()))
                End If
                If ok AndAlso saveToImmich Then
                    ' Ziel Immich: Mit "Vorhandene Assets aktualisieren" UND einer Immich-Quelle wird
                    ' das Quell-Asset ERSETZT (Nutzerentscheidung 2026-07-16: das Setting steuert
                    ' beim Speichern-unter, ob ein neues Asset entsteht oder das Original
                    ' ueberschrieben wird); ohne Immich-Quelle oder mit ausgeschaltetem Setting
                    ' entsteht wie bisher ein neues Asset.
                    Dim replaceAssetId = CurrentImmichAssetId()
                    If Not String.IsNullOrEmpty(replaceAssetId) AndAlso AppSettingsService.Load().ImmichUpdateExistingAssets Then
                        StatusText = LocalizationService.T("Immich-Asset wird aktualisiert…")
                        Dim newAssetId = Await ImmichService.ReplaceAssetAsync(replaceAssetId, targetPath)
                        Try : IO.File.Delete(targetPath) : Catch : End Try
                        If String.IsNullOrEmpty(newAssetId) Then
                            StatusText = LocalizationService.T("Immich-Upload fehlgeschlagen")
                            Return False
                        End If
                        Await ImmichService.WaitForThumbnailReadyAsync(newAssetId)
                        _hasChanges = False
                        ClearPreviewSource()
                        ' Auf das ERGEBNIS umschalten (wie beim direkten Speichern): die alte
                        ' Temp-Kopie zeigt ein Asset, das es auf dem Server nicht mehr gibt.
                        Await SwitchToReplacedImmichAssetAsync(newAssetId, IO.Path.GetFileName(targetPath))
                        StatusText = LocalizationService.T("Immich-Asset aktualisiert")
                        Return True
                    End If

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
                    If saveAs Then
                        ' Katalog-Metadaten zur neuen Datei übernehmen - das Original behält seine.
                        ' Was mitwandert, bestimmen die Einzeloptionen des Dialogs (2026-07-17).
                        Dim metaSource = If(Not String.IsNullOrEmpty(_currentFpxPath), _currentFpxPath, _currentImagePath)
                        LibraryService.Instance.CopyEntryMeta(metaSource, targetPath,
                                                              saveAsResult.CopyRating, saveAsResult.CopyFavorite,
                                                              saveAsResult.CopyColorLabel, saveAsResult.CopyKeywords)
                    End If
                    ExifService.Invalidate(targetPath)
                    _mainVm?.Gallery?.RefreshChangedFiles({targetPath})
                    If _mainVm?.Viewer IsNot Nothing AndAlso String.Equals(_mainVm.Viewer.CurrentImagePath, targetPath, StringComparison.OrdinalIgnoreCase) Then
                        _mainVm.Viewer.ReloadCurrentImageFromDisk()
                    End If
                    ' Ein PDF ist ein AUSGABEformat, keine Arbeitsdatei: der Editor kann es nicht
                    ' dekodieren, der Wechsel endete in einem leeren Editor (Nutzer-Befund 2026-07-18).
                    ' Deshalb bleibt nach „Speichern unter → PDF" das aktuelle Bild geöffnet.
                    Dim savedAsPdf = saveAsResult IsNot Nothing AndAlso saveAsResult.IsPdf
                    If saveAs AndAlso Not savedAsPdf AndAlso Not String.Equals(targetPath, _currentImagePath, StringComparison.OrdinalIgnoreCase) Then
                        ' Nutzerwunsch 2026-07-17: nach „Speichern unter" arbeitet der Editor auf der
                        ' GESPEICHERTEN Datei weiter (.fpx bzw. exportiertes Bild), nicht mehr auf dem
                        ' Ursprungsbild. _hasChanges ist False, der Wechsel fragt also nicht nach.
                        Dim statusAfterSave = StatusText
                        Await OpenImageAsync(targetPath)
                        StatusText = statusAfterSave
                    Else
                        ClearPreviewSource()
                    End If
                    Return True
                Else
                    StatusText = LocalizationService.T("Speichern fehlgeschlagen")
                    Return False
                End If
            Catch ex As Exception
                StatusText = LocalizationService.T("Fehler: ") & ex.Message
                errorMessage = ex.Message
                ' Speicherfehler landeten bisher NUR im Dialog - fürs Log-Debugging (z.B.
                ' „Basisbild fehlt") braucht es den vollen Stacktrace in der Diagnose.
                DiagnosticLogService.LogException("Editor.Save", ex)
            End Try
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Speichern fehlgeschlagen"), errorMessage)
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

            Dim sourcePath = RenderSourcePath
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
                Dim ok = Await Task.Run(Function() ImageProcessor.SaveImage(sourcePath, renderPath, adj, SaveQuality, preserveMetadata,
                                                                            workingFull:=CloneWorkingFullForRender()))
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
                Await SwitchToReplacedImmichAssetAsync(newAssetId, fileName)
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

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Speichern fehlgeschlagen"), errorMessage)
            Return False
        End Function

        ''' <summary>Nach einem Immich-Ersetzen (Speichern ODER Speichern-unter mit "Vorhandene
        ''' Assets aktualisieren"): Die Temp-Kopie des alten Assets ist mit dem Ersetzen weggeräumt.
        ''' Auf die des ERGEBNISSES umschalten (im Filmstreifen an derselben Stelle), damit
        ''' Vorher/Nachher, erneutes Speichern und die Metadaten-Leiste wieder auf dem echten
        ''' Serverzustand stehen; danach die Immich-Galerieansicht auffrischen.</summary>
        Private Async Function SwitchToReplacedImmichAssetAsync(newAssetId As String, fileName As String) As Task
            Dim previousSourcePath = RenderSourcePath
            Dim localPath = Await ImmichService.DownloadOriginalToTempAsync(newAssetId, fileName)
            If Not String.IsNullOrEmpty(localPath) Then
                Dim paths = _folderPaths.ToList()
                Dim index = paths.FindIndex(Function(p) String.Equals(p, previousSourcePath, StringComparison.OrdinalIgnoreCase))
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
        End Function

        ''' <summary>Ob "Speichern" (in-place) bei der aktuellen RAW-Quelle den Rezept-Sidecar
        ''' schreiben kann: Einstellung an, kein offenes .fpx, nichts ins Arbeitsbild gebacken
        ''' (Pinsel/Retusche/Rastern stecken NICHT im Rezept - dafuer bleibt die .fpx zustaendig).
        ''' Geschrieben wird AUSSCHLIESSLICH ueber die Speichern-Funktion, nie nebenbei beim
        ''' Verlassen (Nutzerentscheidung 2026-07-19: Dateien entstehen nur durch bewusstes
        ''' Speichern).</summary>
        Public ReadOnly Property CanSaveSidecar As Boolean
            Get
                Return RawSidecarService.IsSidecarFormat(RenderSourcePath) AndAlso
                       String.IsNullOrEmpty(_currentFpxPath) AndAlso
                       Not _workingImage.HasBakedContent
            End Get
        End Property

        ''' <summary>"Speichern" fuer RAW/PSD: schreibt das Rezept in die Begleitdatei
        ''' (die Quelldatei selbst wird nie angefasst).</summary>
        Private Function TrySaveSidecar() As Boolean
            If Not CanSaveSidecar Then Return False
            If Not RawSidecarService.TryWrite(RenderSourcePath, GetCurrentAdjustments()) Then
                StatusText = LocalizationService.T("Begleitdatei konnte nicht geschrieben werden")
                Return False
            End If
            _hasChanges = False
            Me.RaisePropertyChanged(NameOf(HasUnsavedChanges))
            ' Die Quelldatei ist unveraendert - ohne diesen Anstoss behielten Galerie und
            ' Filmstreifen ihre alte Kachel, obwohl das Rezept (z.B. eine Drehung) jetzt anders ist.
            _mainVm?.ReloadThumbnailsForFile(RenderSourcePath)
            StatusText = LocalizationService.T("Bearbeitung in Begleitdatei gespeichert")
            Return True
        End Function

        Public Async Function ConfirmSaveBeforeLeavingAsync(actionDescription As String) As Task(Of Boolean)
            If Not _hasChanges Then Return True
            If _mainVm Is Nothing Then Return True

            Dim message As String
            If String.IsNullOrWhiteSpace(actionDescription) Then
                message = LocalizationService.T("Es gibt ungespeicherte Änderungen. Möchtest du sie speichern?")
            Else
                message = String.Format(
                    LocalizationService.T("Es gibt ungespeicherte Änderungen. Möchtest du sie speichern, bevor du {0}?"),
                    LocalizationService.T(actionDescription))
            End If

            Dim save = Await _mainVm.ShowConfirmAsync(
                LocalizationService.T("Änderungen speichern"),
                message,
                LocalizationService.T("Speichern"),
                LocalizationService.T("Nicht speichern"))
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
        ''' <summary>Der Anpassungssatz, mit dem gerendert, gespeichert und rückgängig gemacht wird.
        '''
        ''' Ist ein Objekt markiert und ein objektfähiges Werkzeug aktiv (Anpassen/Farbe/Details+Effekte/
        ''' Filter), dann beschreiben die Regler-Felder das OBJEKT, nicht das Bild: die Werte wandern hier in
        ''' das Objekt, und das Bild bekommt seine eigenen (in _imagePixelAdjustments geparkten) zurück. Nach
        ''' außen sieht die Pipeline damit immer dasselbe: Bild-Anpassungen + Objekte, die ihre eigenen
        ''' mitbringen.</summary>
        Private Function GetCurrentAdjustments(Optional forPreview As Boolean = False,
                                               Optional includeEditorOverlayAnnotations As Boolean = False) As ImageAdjustments
            Dim adj = BuildAdjustmentsFromFields(forPreview, includeEditorOverlayAnnotations)
            If Not IsObjectAdjustModeActive() Then Return adj

            Dim objectValues = adj.ExtractPixelAdjustments()
            If _objectAdjustIndex >= 0 AndAlso _objectAdjustIndex < adj.Annotations.Count Then
                adj.Annotations(_objectAdjustIndex).Adjustments = If(objectValues.HasPixelAdjustments(), objectValues, Nothing)
            End If
            adj.CopyPixelAdjustmentsFrom(_imagePixelAdjustments)
            Return adj
        End Function

        Private Function BuildAdjustmentsFromFields(Optional forPreview As Boolean = False,
                                                    Optional includeEditorOverlayAnnotations As Boolean = False) As ImageAdjustments
            Dim adj = New ImageAdjustments With {
                .Brightness = CSng(_brightness),
                .Contrast = CSng(_contrast),
                .Saturation = CSng(_saturation),
                .Vibrance = CSng(_vibrance),
                .CalibrationRedHue = CSng(_calibrationRedHue),
                .CalibrationRedSaturation = CSng(_calibrationRedSaturation),
                .CalibrationGreenHue = CSng(_calibrationGreenHue),
                .CalibrationGreenSaturation = CSng(_calibrationGreenSaturation),
                .CalibrationBlueHue = CSng(_calibrationBlueHue),
                .CalibrationBlueSaturation = CSng(_calibrationBlueSaturation),
                .CalibrationShadowTint = CSng(_calibrationShadowTint),
                .Highlights = CSng(_highlights),
                .ShadowsLevel = CSng(_shadowsLevel),
                .Whites = CSng(_whites),
                .Blacks = CSng(_blacks),
                .Temperature = CSng(_temperature),
                .Tint = CSng(_tint),
                .Exposure = CSng(_exposure),
                .Sharpness = CSng(_sharpness),
                .NoiseReduction = CSng(_noiseReduction),
                .ColorNoiseReduction = CSng(_colorNoiseReduction),
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
                .WorkingImageVersion = _workingImage.Version,
                .WorkingImageHasTransparency = _workingImage.HasAlphaHoles,
                .FilterPreset = _filterPreset,
                .FilterStrength = CSng(_filterStrength),
                .LutPath = _lutPath,
                .LutStrength = CSng(_lutStrength),
                .Annotations = _annotations.Select(Function(a) a.Clone()).ToList(),
                .BackgroundHidden = _backgroundHidden,
                .PixelLayerHidden = _pixelLayerHidden,
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
                .SelectionMaskPngBase64 = EncodeSelectionMaskBase64(),
                .SelectionFeatherPixels = CSng(_selectionFeather)
            }

            ' Editor-Arbeitsmodus: Objekt-Layer werden als UI-Overlays gezeichnet und bleiben aus dem
            ' teuren Pixel-Preview-Render heraus. Pinsel/Radierer bleiben vorerst in der Pixelpipeline,
            ' weil sie aktuell als malende Ebenen modelliert sind und separat optimiert werden müssen.
            If forPreview AndAlso Not includeEditorOverlayAnnotations Then
                For Each annotation In adj.Annotations
                    If EditorRendersAnnotationAsOverlay(annotation) Then annotation.IsVisible = False
                Next
            End If

            Return adj
        End Function

        Private Function SetUndoableDouble(ByRef field As Double, value As Double, propertyName As String) As Boolean
            If Math.Abs(field - value) < 0.0001 Then Return False
            CaptureUndoState(propertyName)
            field = value
            Me.RaisePropertyChanged(propertyName)
            RaiseResetButtonStateChanged()
            If HasSelectedAnnotation AndAlso IsObjectAdjustTool(_currentTool) Then
                RefreshSelectedAnnotationPreviewImmediatelyIfNeeded()
            Else
                SchedulePreviewUpdate()
            End If
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

            _undoStack.Push(New UndoEntry With {.Adjustments = GetCurrentAdjustments()})
            ClearRedoStack()
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
            _lastPushedUndoEntry = Nothing
            For Each entry In _undoStack
                If entry.Patch IsNot Nothing Then _workingImage.DiscardPatch(entry.Patch)
            Next
            _undoStack.Clear()
            ClearRedoStack()
            Me.RaisePropertyChanged(NameOf(CanUndo))
            Me.RaisePropertyChanged(NameOf(CanRedo))
        End Sub

        ''' Redo-Einträge können Pixel-Patches halten (Wiederholen-Inhalte) - nach einer neuen
        ''' Aktion sind die unerreichbar und geben ihren Speicher sofort zurück.
        Private Sub ClearRedoStack()
            For Each entry In _redoStack
                If entry.Patch IsNot Nothing Then _workingImage.DiscardPatch(entry.Patch)
            Next
            _redoStack.Clear()
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
            Dim entry As New UndoEntry With {.Adjustments = snapshot}
            _undoStack.Push(entry)
            _lastPushedUndoEntry = entry
            ClearRedoStack()
            AddHistoryEntry(BuildHistoryLabel(snapshot))
            Me.RaisePropertyChanged(NameOf(CanUndo))
            Me.RaisePropertyChanged(NameOf(CanRedo))
        End Sub

        Private Sub UndoAction()
            ' Auch Tastatur-Pfade (Strg+Z) respektieren die Commit-Sperre - siehe CanUndo.
            If _undoStack.Count = 0 OrElse _pendingWorkingCommits > 0 Then Return
            ResetUndoCapture()
            _lastPushedUndoEntry = Nothing
            Dim entry = _undoStack.Pop()
            ' Der Patch wandert in den Redo-Eintrag: RevertPatch tauscht die Region und hält
            ' danach die Wiederholen-Pixel im selben Objekt (Tausch-Schema im Service).
            _redoStack.Push(New UndoEntry With {.Adjustments = GetCurrentAdjustments(), .Patch = entry.Patch})
            _suppressUndoCapture = True
            Try
                ApplyAdjustments(entry.Adjustments)
            Finally
                _suppressUndoCapture = False
            End Try
            If entry.Patch IsNot Nothing AndAlso _workingImage.RevertPatch(entry.Patch) Then
                OnWorkingImageRegionChanged(entry.Patch.Rect)
            End If
            AddHistoryEntry("Rückgängig")
            Me.RaisePropertyChanged(NameOf(CanUndo))
            Me.RaisePropertyChanged(NameOf(CanRedo))
        End Sub

        Private Sub RedoAction()
            If _redoStack.Count = 0 OrElse _pendingWorkingCommits > 0 Then Return
            ResetUndoCapture()
            _lastPushedUndoEntry = Nothing
            Dim entry = _redoStack.Pop()
            _undoStack.Push(New UndoEntry With {.Adjustments = GetCurrentAdjustments(), .Patch = entry.Patch})
            _suppressUndoCapture = True
            Try
                ApplyAdjustments(entry.Adjustments)
            Finally
                _suppressUndoCapture = False
            End Try
            If entry.Patch IsNot Nothing AndAlso _workingImage.ReapplyPatch(entry.Patch) Then
                OnWorkingImageRegionChanged(entry.Patch.Rect)
            End If
            AddHistoryEntry("Wiederholt")
            Me.RaisePropertyChanged(NameOf(CanUndo))
            Me.RaisePropertyChanged(NameOf(CanRedo))
        End Sub

        ''' Nach einem Patch-Tausch (Undo/Redo eines eingebackenen Commits): die Vorschau-Region
        ''' hat der Service bereits nachgezogen - hier zieht die ANZEIGE nach. Voller
        ''' Vorschau-Render; regionsgenauer Feinschliff kommt mit den Commits in Stufe D.
        Private Sub OnWorkingImageRegionChanged(rect As SKRectI)
            SchedulePreviewUpdate()
        End Sub

        Private Sub ApplyAdjustments(adj As ImageAdjustments)
            _brightness = adj.Brightness
            _contrast = adj.Contrast
            _saturation = adj.Saturation
            _vibrance = adj.Vibrance
            _calibrationRedHue = adj.CalibrationRedHue
            _calibrationRedSaturation = adj.CalibrationRedSaturation
            _calibrationGreenHue = adj.CalibrationGreenHue
            _calibrationGreenSaturation = adj.CalibrationGreenSaturation
            _calibrationBlueHue = adj.CalibrationBlueHue
            _calibrationBlueSaturation = adj.CalibrationBlueSaturation
            _calibrationShadowTint = adj.CalibrationShadowTint
            _highlights = adj.Highlights
            _shadowsLevel = adj.ShadowsLevel
            _whites = adj.Whites
            _blacks = adj.Blacks
            _temperature = adj.Temperature
            _tint = adj.Tint
            _exposure = adj.Exposure
            _sharpness = adj.Sharpness
            _noiseReduction = adj.NoiseReduction
            _colorNoiseReduction = adj.ColorNoiseReduction
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
            ' ARBEITSBILD (Stufe E): Retusche-Spots sind transient (nur der aktive Zug) - alte
            ' Snapshots/Rezepte mit Spot-Listen werden bewusst ignoriert (Pixel sind die Wahrheit).
            _retouchSpots.Clear()
            _annotations.Clear()
            _pixelEditLayer.Clear()
            _selectedAnnotationIndex = -1
            If adj.Annotations IsNot Nothing Then
                For Each annotation In adj.Annotations
                    _annotations.Add(annotation.Clone())
                Next
            End If
            ClearSelectionMask()
            _hasActiveSelection = adj.HasActiveSelection
            _backgroundHidden = adj.BackgroundHidden
            Me.RaisePropertyChanged(NameOf(IsBackgroundVisible))
            _pixelLayerHidden = adj.PixelLayerHidden
            RaisePixelLayerVisibilityChanged()
            _selectionFeather = adj.SelectionFeatherPixels
            Me.RaisePropertyChanged(NameOf(SelectionFeather))
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
            Me.RaisePropertyChanged(NameOf(CalibrationRedHue))
            Me.RaisePropertyChanged(NameOf(CalibrationRedSaturation))
            Me.RaisePropertyChanged(NameOf(CalibrationGreenHue))
            Me.RaisePropertyChanged(NameOf(CalibrationGreenSaturation))
            Me.RaisePropertyChanged(NameOf(CalibrationBlueHue))
            Me.RaisePropertyChanged(NameOf(CalibrationBlueSaturation))
            Me.RaisePropertyChanged(NameOf(CalibrationShadowTint))
            Me.RaisePropertyChanged(NameOf(Highlights))
            Me.RaisePropertyChanged(NameOf(ShadowsLevel))
            Me.RaisePropertyChanged(NameOf(Whites))
            Me.RaisePropertyChanged(NameOf(Blacks))
            Me.RaisePropertyChanged(NameOf(Temperature))
            Me.RaisePropertyChanged(NameOf(Tint))
            Me.RaisePropertyChanged(NameOf(Exposure))
            Me.RaisePropertyChanged(NameOf(Sharpness))
            Me.RaisePropertyChanged(NameOf(NoiseReduction))
            Me.RaisePropertyChanged(NameOf(ColorNoiseReduction))
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
            Me.RaisePropertyChanged(NameOf(SelectedLayer))
            Me.RaisePropertyChanged(NameOf(HasSelectedAnnotation))
            Me.RaisePropertyChanged(NameOf(CanRasterizeSelectedAnnotation))
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
            ' Nach Rückgängig/Wiederholen: Bedienen die Regler gerade ein Objekt, dann tragen die Felder
            ' jetzt die BILD-Werte aus dem Schnappschuss. Also Bildwerte wieder parken und die Werte des
            ' Objekts (die im Schnappschuss am Objekt hängen) in die Regler holen.
            If IsObjectAdjustModeActive() AndAlso Not _objectAdjustSwapInProgress Then
                _objectAdjustSwapInProgress = True
                Try
                    If _objectAdjustIndex < 0 OrElse _objectAdjustIndex >= _annotations.Count Then
                        ' Im wiederhergestellten Stand gibt es das Objekt nicht mehr - Regler bedienen wieder
                        ' das Bild (dessen Werte stehen bereits in den Feldern).
                        _imagePixelAdjustments = Nothing
                        _objectAdjustIndex = -1
                    Else
                        _imagePixelAdjustments = adj.ExtractPixelAdjustments()
                        Dim objectAdj = _annotations(_objectAdjustIndex).Adjustments
                        Dim keepIndex = _objectAdjustIndex
                        Dim target = BuildAdjustmentsFromFields()
                        target.CopyPixelAdjustmentsFrom(If(objectAdj, New ImageAdjustments()))
                        ApplyAdjustmentsKeepingSelection(target, keepIndex)
                    End If
                Finally
                    _objectAdjustSwapInProgress = False
                End Try
            End If
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
            ' ARBEITSBILD (Stufe D): Zurücksetzen entfernt auch gebackene Striche/Retusche -
            ' das Arbeitsbild wird frisch vom Original (bzw. .fpx-Basisbild) aufgebaut. Die
            ' Undo-Pixel-Patches sterben dabei (Init räumt sie ab): ein Undo nach dem
            ' Zurücksetzen stellt Regler/Objekte wieder her, gebackene Pixel nicht.
            ' Bei den Öffnen-Pfaden (resetEditorUi:=True) folgt ohnehin PreparePreviewSource.
            If Not resetEditorUi AndAlso _workingImage.HasBakedContent Then
                _workingImageOverridePath = ""
                _workingImageOverrideHasAlpha = False
                PreparePreviewSource(RenderSourcePath, scheduleInitialRender:=False)
            End If
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
            _colorNoiseReduction = 0
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
            _pixelEditLayer.Clear()
            _selectedAnnotationIndex = -1
            If resetEditorUi Then ResetEditorUiStateForNewImage(resetTool:=False)
            _hasChanges = False
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationIndex))
            Me.RaisePropertyChanged(NameOf(SelectedLayer))
            Me.RaisePropertyChanged(NameOf(HasSelectedAnnotation))
            Me.RaisePropertyChanged(NameOf(CanRasterizeSelectedAnnotation))
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
            Me.RaisePropertyChanged(NameOf(ColorNoiseReduction))
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
            _pixelEditLayer.ResetActiveStroke()
            _isEraserMode = False
            _eraserFillColor = "#00FFFFFF"
            _isCloneMode = False
            _isRepairMode = False
            _retouchRadius = 24.0
            _brushSize = 24.0
            _brushHardness = 100
            _brushOpacity = 100
            _brushFlow = 100
            _paintToolStates = NewPaintToolStates()

            _annotationText = "Text"
            _annotationFillColor = "#00FFFFFF"
            _annotationStrokeColor = "#FF000000"
            _annotationStrokeWidth = 0
            _annotationFontSize = 48
            _annotationFontFamily = "Arial"
            _annotationOpacity = 100
            _annotationBlendMode = "Normal"
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
            _annotationTextPathKind = ""
            _annotationTextPathBend = 50
            _annotationTextPathStartOffset = 0
            Me.RaisePropertyChanged(NameOf(AnnotationTextPathKind))
            Me.RaisePropertyChanged(NameOf(AnnotationTextPathBend))
            Me.RaisePropertyChanged(NameOf(AnnotationTextPathStartOffset))
            Me.RaisePropertyChanged(NameOf(AnnotationLetterSpacingPercent))
            Me.RaisePropertyChanged(NameOf(AnnotationBold))
            Me.RaisePropertyChanged(NameOf(AnnotationItalic))
            Me.RaisePropertyChanged(NameOf(ShowTextPathControls))
            _annotationFillColor2 = "#FFFFFFFF"
            _annotationGradientAngle = 0
            _annotationGradientInverted = False
            _annotationShadowEnabled = False
            _annotationShadowOffsetX = 4
            _annotationShadowOffsetY = 4
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

            ClearSelection(captureUndo:=False)
            _backgroundHidden = False
            _pixelLayerHidden = False
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
            Me.RaisePropertyChanged(NameOf(ShowAnnotationAspectLock))
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
            Me.RaisePropertyChanged(NameOf(IsBackgroundVisible))
            RaisePixelLayerVisibilityChanged()
            Me.RaisePropertyChanged(NameOf(ShapeIconSearchText))
            UpdateShapeIconStates()

            Me.RaisePropertyChanged(NameOf(RetouchRadius))
            Me.RaisePropertyChanged(NameOf(BrushSize))
            Me.RaisePropertyChanged(NameOf(BrushHardness))
            Me.RaisePropertyChanged(NameOf(BrushOpacity))
            Me.RaisePropertyChanged(NameOf(BrushFlow))
            Me.RaisePropertyChanged(NameOf(IsEraserMode))
            Me.RaisePropertyChanged(NameOf(EraserFillColor))
            Me.RaisePropertyChanged(NameOf(EraserFillColorValue))
            Me.RaisePropertyChanged(NameOf(EraserFillBrush))
            Me.RaisePropertyChanged(NameOf(IsCloneMode))
            Me.RaisePropertyChanged(NameOf(IsRepairMode))
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
            Me.RaisePropertyChanged(NameOf(AnnotationBlendMode))
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationBlendModeOption))
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

        Private Function GetDefaultTextAnnotationFontSizePixels() As Double
            Dim minSide = Math.Min(GetBaseWidth(), GetBaseHeight())
            If minSide <= 0 Then Return 48
            Return Math.Max(48.0, Math.Min(5000.0, minSide * 0.045))
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
            FitBoxToCircleTextPath()
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
            FitBoxToCircleTextPath()
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
            If normalizedKind = "Text" OrElse (normalizedKind = "Watermark" AndAlso Not IsWatermarkImageSource) Then
                Dim textSize = EstimateTextAnnotationSizePercent(_annotationText, _annotationFontSize, _annotationFontFamily)
                width = textSize.WidthPercent
                height = textSize.HeightPercent
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
                .BlendMode = _annotationBlendMode,
                .RotationDegrees = CSng(_annotationRotation),
                .Anchor = If(normalizedKind = "Watermark", NormalizeAnnotationAnchor(_annotationAnchor), ""),
                .IsVisible = _annotationIsVisible,
                .FillKind = _annotationFillKind,
                .FillColor2 = _annotationFillColor2,
                .GradientAngleDegrees = CSng(_annotationGradientAngle),
                .GradientInverted = _annotationGradientInverted
            }
            HardenAnnotationBuffersForNewObject()
            _annotations.Add(annotation)
            SelectedAnnotationIndex = _annotations.Count - 1
            RaiseResetButtonStateChanged()
            ' Nur die Region des NEUEN Objekts rendern: ein Vollaufbau wuerde grosse weiche
            ' Pinselstriche komplett neu zeichnen (Sekunden), obwohl sich dort nichts geaendert hat.
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(annotation))
        End Sub

        ''' <summary>Haertet die Puffer-Felder, die die Objekt-ERZEUGUNG nicht bewusst uebernimmt, gegen
        ''' das [Annotation-Puffer-Leck]-Fenster: zwischen "SelectedAnnotationIndex = neu" und
        ''' LoadSelectedAnnotationIntoEditor wechselt der Selektions-Setter das Werkzeug - ein Sync in
        ''' diesem Fenster schriebe sonst die noch ALTEN Puffer des zuvor selektierten Objekts in das
        ''' neue (beobachtet: Schatten/Gluehen-Aktiv; gleiche Gefahr: Spiegelungen). Direktfelder ohne
        ''' Setter-Nebenwirkungen; Load setzt danach ohnehin konsistent aus dem neuen Objekt.
        ''' Bewusst uebernommen bleiben die Panel-Vorgaben (Farben, Kontur, Schrift, Mischmodus,
        ''' Drehung, Verlauf) - die setzt der Initializer explizit.</summary>
        Private Sub HardenAnnotationBuffersForNewObject()
            _annotationShadowEnabled = False
            _annotationGlowEnabled = False
            _annotationFlipH = False
            _annotationFlipV = False
        End Sub

        Private Function GetDefaultAnnotationSizePercent(normalizedKind As String, rawKind As String) As (WidthPercent As Double, HeightPercent As Double)
            Select Case normalizedKind
                Case "Line", "Arrow"
                    Return (30.0, 16.0)
                Case "QR", "Image", "Symbol", "Rectangle", "RoundedRectangle", "Ellipse", "Square", "Triangle", "Cone", "Pyramid", "Trapezoid", "Diamond", "Polygon", "Star", "DoubleStar", "Spiral", "Droplet", "SpeechBubble", "EllipseSpeechBubble", "RectSpeechBubble", "Heart", "Cloud"
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
                .BlendMode = _annotationBlendMode,
                .RotationDegrees = CSng(_annotationRotation),
                .IsVisible = _annotationIsVisible
            }
            HardenAnnotationBuffersForNewObject()
            _annotations.Add(annotation)
            SelectedAnnotationIndex = _annotations.Count - 1
            RaiseResetButtonStateChanged()
            ' Nur die Region des NEUEN Objekts rendern: ein Vollaufbau wuerde grosse weiche
            ' Pinselstriche komplett neu zeichnen (Sekunden), obwohl sich dort nichts geaendert hat.
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(annotation))
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
                Case "Rectangle", "RoundedRectangle", "Ellipse", "Square", "Triangle", "Cone", "Pyramid", "Trapezoid", "Diamond", "Polygon", "Star", "DoubleStar", "Spiral", "Droplet", "SpeechBubble", "EllipseSpeechBubble", "RectSpeechBubble", "Heart", "Cloud", "Line", "Arrow"
                    Return EditorTool.Geometry
                Case "Symbol", "Svg"
                    Return EditorTool.Insert
                Case "Brush", "Eraser" : Return EditorTool.Draw
                Case "SelectionFill", "SelectionImage" : Return EditorTool.Selection
                Case Else : Return EditorTool.Insert
            End Select
        End Function

        Private Shared Function ToolbarKindForAnnotation(kind As String) As String
            Select Case NormalizeAnnotationKind(kind)
                Case "SelectionFill", "SelectionImage"
                    Return "Selection"
                Case "Rectangle", "RoundedRectangle", "Ellipse", "Square", "Triangle", "Cone", "Pyramid", "Trapezoid", "Diamond", "Polygon", "Star", "DoubleStar", "Spiral", "Droplet", "SpeechBubble", "EllipseSpeechBubble", "RectSpeechBubble", "Heart", "Cloud", "Line", "Arrow", "Symbol", "Svg"
                    Return "Insert"
                Case Else
                    Return NormalizeAnnotationKind(kind)
            End Select
        End Function

        Private Shared Function PlacementKindForAnnotation(annotation As ImageAnnotation) As String
            If annotation Is Nothing Then Return ""

            Dim normalized = NormalizeAnnotationKind(annotation.Kind)
            Select Case normalized
                Case "Text", "Image", "QR", "Watermark", "Rectangle", "RoundedRectangle", "Ellipse", "Square", "Triangle", "Cone", "Pyramid", "Trapezoid", "Diamond", "Polygon", "Star", "DoubleStar", "Spiral", "Droplet", "SpeechBubble", "EllipseSpeechBubble", "RectSpeechBubble", "Heart", "Cloud", "Line", "Arrow", "Symbol"
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
                Case "roundedrectangle", "rounded-rectangle", "abgerundetesrechteck", "abgerundetes rechteck" : Return "RoundedRectangle"
                Case "ellipse", "circle", "kreis" : Return "Ellipse"
                Case "square", "quadrat" : Return "Square"
                Case "triangle", "dreieck" : Return "Triangle"
                Case "cone", "kegel" : Return "Cone"
                Case "pyramid", "pyramide" : Return "Pyramid"
                Case "trapezoid", "trapez" : Return "Trapezoid"
                Case "diamond", "raute" : Return "Diamond"
                Case "polygon", "polygonal", "vieleck" : Return "Polygon"
                Case "star", "stern" : Return "Star"
                Case "doublestar", "double-star", "doppelstern" : Return "DoubleStar"
                Case "spiral", "spirale" : Return "Spiral"
                Case "droplet", "drop", "tropfen", "traene", "träne" : Return "Droplet"
                Case "speechbubble", "speech-bubble", "sprechblase", "bubble" : Return "SpeechBubble"
                Case "ellipsespeechbubble", "ellipse-speech-bubble", "runde sprechblase", "ellipse sprechblase" : Return "EllipseSpeechBubble"
                Case "rectspeechbubble", "rect-speech-bubble", "rectangle-speech-bubble", "rechteck sprechblase" : Return "RectSpeechBubble"
                Case "cloud", "wolke" : Return "Cloud"
                Case "heart", "herz" : Return "Heart"
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
                Case "Square", "Triangle", "Cone", "Pyramid", "Trapezoid", "Diamond", "Polygon", "Star", "DoubleStar", "Spiral", "Droplet", "SpeechBubble", "EllipseSpeechBubble", "RectSpeechBubble", "Heart", "Cloud", "RoundedRectangle", "Svg"
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

        ''' ARBEITSBILD (Stufe D): ein Pinsel-/Radiererstrich wird nicht mehr als Rezept
        ''' gespeichert und abgespielt, sondern REGIONAL in Vollauflösung ins Arbeitsbild
        ''' eingebacken (Hintergrund-Queue). Undo läuft über den Vorher-Patch des Commits.
        ''' Pinsel und Radierer sind keine Overlay-Objekte und erzeugen keine Ebenen.
        Public Sub AddBrushStroke(points As IEnumerable(Of Avalonia.Point), Optional isEraser As Boolean = False)
            If Not CanUsePixelTools Then Return
            If points Is Nothing Then Return
            Dim normalized = points.ToList()
            If normalized.Count < 2 Then Return

            Dim baseW = GetBaseWidth()
            Dim baseH = GetBaseHeight()
            Dim pixelPoints = normalized.Select(Function(p) New Avalonia.Point(PercentXToPixels(p.X), PercentYToPixels(p.Y))).ToList()
            Dim dirtyFull As SKRectI
            Dim stroke = PixelEditLayer.CreateTransientStroke(pixelPoints, BuildPixelPaintOptions(isEraser), baseW, baseH, dirtyFull)
            If stroke Is Nothing OrElse dirtyFull.Width <= 0 OrElse dirtyFull.Height <= 0 Then Return

            PushUndo()
            Dim undoEntry = _lastPushedUndoEntry
            Dim renderAnn = stroke.ToRenderAnnotation()
            ' Radierer mit transparenter Füllfarbe stanzt echte Alpha-Löcher (DstOut).
            Dim punchesAlpha = isEraser AndAlso Not EraserFillColorHasInk()

            _hasChanges = True
            RaiseResetButtonStateChanged()
            _previewTimer.Stop()
            _previewPending = False

            ' SOFORT-Brücke: den Strich in die bestehende Szene zeichnen (Vorschau-Auflösung),
            ' damit zwischen Loslassen und Einbacken nichts flackert. Optisch ist das der alte
            ' Stand „Strich ÜBER der Farbpipeline und über Objekten" - der korrekte Stand
            ' (Strich unter Reglern und Objekten) kommt mit dem Voll-Render nach dem Commit.
            DrawStrokeBridgeIntoScene(renderAnn, dirtyFull, baseW, baseH)

            EnqueueWorkingCommit(
                Function()
                    Return _workingImage.CommitRegion(dirtyFull,
                        Sub(full)
                            Using canvas = New SKCanvas(full)
                                canvas.ClipRect(SKRect.Create(dirtyFull.Left, dirtyFull.Top, dirtyFull.Width, dirtyFull.Height))
                                Dim adjDraw As New ImageAdjustments With {.SourceWidthPixels = baseW, .SourceHeightPixels = baseH}
                                ImageProcessor.DrawAnnotationsOnCanvas(canvas, adjDraw, full.Width, full.Height,
                                                                       0, 0, full.Width, full.Height,
                                                                       New List(Of ImageAnnotation) From {renderAnn})
                            End Using
                        End Sub,
                        punchesAlpha:=punchesAlpha)
                End Function,
                Sub(patch)
                    If undoEntry IsNot Nothing Then undoEntry.Patch = patch
                    If isEraser Then Me.RaisePropertyChanged(NameOf(TransparencyBackgroundBrush))
                    ' Der eingebackene Stand (Strich unter der Farbpipeline) zieht per Voll-Render
                    ' nach; die Brücke oben bleibt bis dahin stehen.
                    SchedulePreviewUpdate()
                End Sub)
        End Sub

        ''' True, wenn die Radierer-Füllfarbe DECKT (Alpha > 0) - dann malt der Radierer diese
        ''' Farbe, statt Transparenz zu stanzen (siehe DrawBrushStroke).
        Private Function EraserFillColorHasInk() As Boolean
            Dim parsed As SKColor
            If Not SKColor.TryParse(If(_eraserFillColor, ""), parsed) Then Return False
            Return parsed.Alpha > 0
        End Function

        ''' Zeichnet den frischen Strich synchron in die persistente Szene (Vorschau-Auflösung)
        ''' und blittet die Region in die Anzeige - die Brücke zwischen Loslassen und dem
        ''' asynchronen Einbacken ins Arbeitsbild.
        Private Sub DrawStrokeBridgeIntoScene(renderAnn As ImageAnnotation, dirtyFull As SKRectI,
                                              baseW As Integer, baseH As Integer)
            If _sceneSk Is Nothing OrElse baseW <= 0 OrElse baseH <= 0 Then Return
            Dim previewRect = ImageProcessor.ScaleRectBetweenSpaces(dirtyFull, baseW, baseH, _sceneSk.Width, _sceneSk.Height)
            If previewRect.Width <= 0 OrElse previewRect.Height <= 0 Then Return
            Try
                Using canvas = New SKCanvas(_sceneSk)
                    canvas.ClipRect(SKRect.Create(previewRect.Left, previewRect.Top, previewRect.Width, previewRect.Height))
                    Dim adjDraw As New ImageAdjustments With {.SourceWidthPixels = baseW, .SourceHeightPixels = baseH}
                    ImageProcessor.DrawAnnotationsOnCanvas(canvas, adjDraw, _sceneSk.Width, _sceneSk.Height,
                                                           0, 0, _sceneSk.Width, _sceneSk.Height,
                                                           New List(Of ImageAnnotation) From {renderAnn})
                End Using
                _sceneContentVersion += 1
                InvalidateZoomDetail()
                EnsureSceneDisplay()
                BlitSceneRegionToDisplay(previewRect)
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.StrokeBridge", ex)
            End Try
        End Sub

        Private Function BuildPixelPaintOptions(isEraser As Boolean) As PixelPaintOptions
            Return New PixelPaintOptions With {
                .Kind = If(isEraser, "Eraser", "Brush"),
                .StrokeColor = _annotationStrokeColor,
                .EraserFillColor = If(isEraser, _eraserFillColor, ""),
                .StrokeWidth = CSng(_brushSize),
                .Opacity = CSng(_brushOpacity),
                .FlowPercent = CSng(_brushFlow),
                .HardnessPercent = CSng(_brushHardness),
                .BrushPreset = If(isEraser, "soft", _brushPreset),
                .ShadowEnabled = (Not isEraser) AndAlso _annotationShadowEnabled,
                .ShadowOffsetXPercent = CSng(_annotationShadowOffsetX),
                .ShadowOffsetYPercent = CSng(_annotationShadowOffsetY),
                .ShadowBlur = CSng(_annotationShadowBlur),
                .ShadowStrength = CSng(_annotationShadowStrength),
                .ShadowColor = _annotationShadowColor,
                .ShadowSizePercent = CSng(_annotationShadowSize),
                .GlowEnabled = (Not isEraser) AndAlso _annotationGlowEnabled,
                .GlowBlur = CSng(_annotationGlowBlur),
                .GlowStrength = CSng(_annotationGlowStrength),
                .GlowColor = _annotationGlowColor
            }
        End Function

        Private Sub DeleteSelectedAnnotation()
            DeleteAnnotationAt(_selectedAnnotationIndex)
        End Sub

        Private Sub DeleteAnnotation(annotation As ImageAnnotation)
            If annotation Is Nothing Then Return
            DeleteAnnotationAt(_annotations.IndexOf(annotation))
        End Sub

        ''' <summary>Blendet die Hintergrund-Ebene (Basisbild) im Ebenen-Panel ein/aus - wie
        ''' ToggleAnnotationVisibility, nur für den strukturellen BackgroundHidden-Schalter.</summary>
        Private Sub ToggleBackgroundVisibility()
            CaptureUndoState("BackgroundVisibility")
            _backgroundHidden = Not _backgroundHidden
            Me.RaisePropertyChanged(NameOf(IsBackgroundVisible))
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Blendet die Pixel-Ebene (Retusche/Striche/gerastertes) ein/aus. Waehrend noch Commits
        ''' in der Queue stecken, wird nicht umgeschaltet: der Commit meint das Arbeitsbild, das gerade
        ''' aus dem Render fliegt - sein Ergebnis waere sonst weder sichtbar noch nachvollziehbar.</summary>
        Private Sub TogglePixelLayerVisibility()
            If _pendingWorkingCommits > 0 Then Return
            CaptureUndoState("PixelLayerVisibility")
            _pixelLayerHidden = Not _pixelLayerHidden
            ' Ein ausgeblendetes Arbeitsbild darf nicht weiter bemalt werden - eine laufende
            ' Mal-Sitzung endet hier, sonst haenge der naechste Zug an einem unsichtbaren Strich.
            If _pixelLayerHidden Then _pixelEditLayer.ResetActiveStroke()
            RaisePixelLayerVisibilityChanged()
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Setzt den "Arbeitsbild fehlt noch"-Zustand und meldet die davon abhaengigen
        ''' Eigenschaften. Auch die Statuszeile haengt daran.</summary>
        Private Sub SetWorkingImagePending(pending As Boolean)
            If _workingImagePending = pending Then Return
            _workingImagePending = pending
            RaisePixelLayerVisibilityChanged()
            If pending Then
                StatusText = LocalizationService.T("RAW wird entwickelt …")
            ElseIf StatusText = LocalizationService.T("RAW wird entwickelt …") Then
                StatusText = ""
            End If
        End Sub

        Private Sub RaisePixelLayerVisibilityChanged()
            Me.RaisePropertyChanged(NameOf(IsPixelLayerVisible))
            Me.RaisePropertyChanged(NameOf(CanUsePixelTools))
            Me.RaisePropertyChanged(NameOf(CanRasterizeSelectedAnnotation))
            Me.RaisePropertyChanged(NameOf(PixelToolsLockedHint))
        End Sub

        ''' <summary>Startet das Inline-Umbenennen einer Ebene (Doppelklick im Panel): Undo-Punkt setzen und
        ''' den Bearbeitungszustand exklusiv auf diese Ebene legen (nur eine wird gleichzeitig bearbeitet).</summary>
        Public Sub BeginLayerRename(annotation As ImageAnnotation)
            If annotation Is Nothing Then Return
            PushUndo()
            ' Das Eingabefeld mit der aktuellen (ggf. automatischen) Beschriftung vorbefüllen.
            If String.IsNullOrWhiteSpace(annotation.CustomName) Then annotation.CustomName = annotation.LayerLabel
            For Each a In _annotations
                a.IsRenaming = Object.ReferenceEquals(a, annotation)
            Next
        End Sub

        ''' <summary>Beendet das Inline-Umbenennen (Enter/Verlassen des Feldes). Der Name selbst steht durch
        ''' die Live-Bindung bereits im Objekt; hier wird nur der Bearbeitungszustand aufgehoben.</summary>
        Public Sub EndLayerRename()
            For Each a In _annotations
                a.IsRenaming = False
            Next
        End Sub

        Public Sub MarkLayerMetadataChanged()
            _hasChanges = True
            Me.RaisePropertyChanged(NameOf(HasUnsavedChanges))
        End Sub

        ''' <summary>Setzt die Ebene <paramref name="dragged"/> per Drag &amp; Drop an die Einfüge-Lücke
        ''' <paramref name="displayGap"/> der PANEL-Anzeige (0 = ganz oben/vorne … Count = ganz unten/hinten,
        ''' direkt über dem Hintergrund). Rechnet die Anzeige (umgekehrt zu _annotations) auf die Z-Reihenfolge
        ''' um. Ändert die Z-Reihenfolge, also neu rendern; No-Op, wenn sich nichts ändert.</summary>
        Public Sub ReorderLayerToDisplayGap(dragged As ImageAnnotation, displayGap As Integer)
            If dragged Is Nothing Then Return
            Dim draggedIndex = _annotations.IndexOf(dragged)
            CommitObjectAdjustModeToModel()
            If draggedIndex >= 0 AndAlso draggedIndex < _annotations.Count Then dragged = _annotations(draggedIndex)
            ' Aktuelle Anzeige-Reihenfolge (umgekehrt zu _annotations).
            Dim disp As New List(Of ImageAnnotation)()
            For i = _annotations.Count - 1 To 0 Step -1
                disp.Add(_annotations(i))
            Next
            Dim from = disp.IndexOf(dragged)
            If from < 0 Then Return
            Dim insert = Math.Max(0, Math.Min(displayGap, disp.Count))
            disp.RemoveAt(from)
            If insert > from Then insert -= 1
            insert = Math.Max(0, Math.Min(insert, disp.Count))
            disp.Insert(insert, dragged)

            ' Zielreihenfolge in _annotations = Anzeige umgekehrt.
            Dim target As New List(Of ImageAnnotation)()
            For i = disp.Count - 1 To 0 Step -1
                target.Add(disp(i))
            Next
            Dim unchanged = target.Count = _annotations.Count
            If unchanged Then
                For i = 0 To target.Count - 1
                    If Not Object.ReferenceEquals(target(i), _annotations(i)) Then
                        unchanged = False
                        Exit For
                    End If
                Next
            End If
            If unchanged Then Return

            PushUndo()
            _annotations.Clear()
            For Each a In target
                _annotations.Add(a)
            Next
            ' Selektion aufheben: nach dem Umsortieren zeigt die Szene damit sofort die neue
            ' Reihenfolge, ohne dass ein Placement-Ghost eine alte Z-Position weiterzeigt.
            SelectedAnnotationIndex = -1
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        Private Sub ToggleAnnotationVisibility(annotation As ImageAnnotation)
            If annotation Is Nothing Then Return
            CaptureUndoState("LayerVisibility")
            annotation.IsVisible = Not annotation.IsVisible
            If _selectedAnnotationIndex >= 0 AndAlso _annotations(_selectedAnnotationIndex) Is annotation Then
                AnnotationIsVisible = annotation.IsVisible
            End If
            RaiseResetButtonStateChanged()
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(annotation))
        End Sub

        Private Sub DeleteAnnotationAt(index As Integer)
            If index < 0 OrElse index >= _annotations.Count Then Return
            CommitObjectAdjustModeToModel()
            PushUndo()
            ' Rect VOR dem Entfernen erfassen - danach ist das Objekt weg.
            Dim deletedRect = ComputeSceneDirtyRectFor(_annotations(index))
            _annotations.RemoveAt(index)
            If _selectedAnnotationIndex = index Then
                SelectedAnnotationIndex = -1
            ElseIf _selectedAnnotationIndex > index Then
                _selectedAnnotationIndex -= 1
                Me.RaisePropertyChanged(NameOf(SelectedAnnotationIndex))
                Me.RaisePropertyChanged(NameOf(SelectedLayer))
            End If
            RaiseResetButtonStateChanged()
            RefreshOverlayAfterAnnotationChange(deletedRect)
        End Sub

        ''' Läuft gerade ein Raster-Commit? Sperrt den Befehl gegen Doppelklick.
        Private _rasterizeInFlight As Boolean = False

        ''' „Ebene rastern" (Stufe F): das Objekt wird in das ARBEITSBILD eingebacken, verlässt
        ''' den Ebenenstapel und ist nur noch per Undo (Anpassungs-Snapshot + Pixel-Patch)
        ''' zurückholbar; Retusche/Pinsel können danach direkt darauf arbeiten. Semantik:
        ''' gerasterter Inhalt rückt UNTER die Farb-Regler und unter alle verbliebenen Objekte;
        ''' Mischmodi rechnen ab jetzt gegen das unangepasste Arbeitsbild - der Look kann beim
        ''' Rastern mit aktiven Anpassungen leicht umspringen (bewusst, siehe Rendering-Notizen).
        Private Sub RasterizeSelectedAnnotation()
            If Not CanUsePixelTools Then Return
            If _rasterizeInFlight Then Return
            Dim index = _selectedAnnotationIndex
            If index < 0 OrElse index >= _annotations.Count Then Return
            Dim annotation = _annotations(index)
            If annotation Is Nothing Then Return

            Dim baseW = GetBaseWidth()
            Dim baseH = GetBaseHeight()
            If baseW <= 0 OrElse baseH <= 0 OrElse Not _workingImage.IsInitialized Then Return
            CommitObjectAdjustModeToModel()
            Dim rect = ImageProcessor.ComputeAnnotationDirtyRect(baseW, baseH, annotation, baseW, baseH)
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return

            ' Snapshot enthält das Objekt noch: Undo stellt Ebene UND Pixel wieder her.
            PushUndo()
            Dim undoEntry = _lastPushedUndoEntry
            Dim annClone = annotation.Clone()
            _rasterizeInFlight = True
            StatusText = LocalizationService.T("Ebene wird gerastert…")

            EnqueueWorkingCommit(
                Function()
                    Return _workingImage.CommitRegion(rect,
                        Sub(full)
                            Using canvas = New SKCanvas(full)
                                canvas.ClipRect(SKRect.Create(rect.Left, rect.Top, rect.Width, rect.Height))
                                Dim adjDraw As New ImageAdjustments With {.SourceWidthPixels = baseW, .SourceHeightPixels = baseH}
                                ImageProcessor.DrawAnnotationsOnCanvas(canvas, adjDraw, full.Width, full.Height,
                                                                       0, 0, full.Width, full.Height,
                                                                       New List(Of ImageAnnotation) From {annClone})
                            End Using
                        End Sub)
                End Function,
                Sub(patch)
                    _rasterizeInFlight = False
                    If patch Is Nothing Then
                        StatusText = LocalizationService.T("Rastern fehlgeschlagen")
                        Return
                    End If
                    If undoEntry IsNot Nothing Then undoEntry.Patch = patch
                    ' Objekt aus dem Stapel nehmen - OHNE eigenen Undo-Push (der kam oben) - und
                    ' die Anzeige in EINEM Schritt auf den gebackenen Stand ziehen.
                    Dim idx = _annotations.IndexOf(annotation)
                    If idx >= 0 Then
                        _annotations.RemoveAt(idx)
                        If _selectedAnnotationIndex = idx Then
                            SelectedAnnotationIndex = -1
                        ElseIf _selectedAnnotationIndex > idx Then
                            _selectedAnnotationIndex -= 1
                            Me.RaisePropertyChanged(NameOf(SelectedAnnotationIndex))
                            Me.RaisePropertyChanged(NameOf(SelectedLayer))
                        End If
                    End If
                    _hasChanges = True
                    RaiseResetButtonStateChanged()
                    AddHistoryEntry(LocalizationService.T("Ebene gerastert"))
                    StatusText = LocalizationService.T("Ebene gerastert")
                    SchedulePreviewUpdate()
                End Sub)
        End Sub

        Private Sub DuplicateSelectedAnnotation()
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return
            CommitObjectAdjustModeToModel()
            PushUndo()
            Dim copy = _annotations(_selectedAnnotationIndex).Clone()
            copy.XPixels += 24
            copy.YPixels += 24
            _annotations.Insert(_selectedAnnotationIndex + 1, copy)
            SelectedAnnotationIndex = _selectedAnnotationIndex + 1
            RaiseResetButtonStateChanged()
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(copy))
        End Sub

        Private Sub MoveSelectedAnnotation(direction As Integer)
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return
            CommitObjectAdjustModeToModel()
            Dim target = _selectedAnnotationIndex + If(direction >= 0, 1, -1)
            If target < 0 OrElse target >= _annotations.Count Then Return
            PushUndo()
            Dim item = _annotations(_selectedAnnotationIndex)
            _annotations.RemoveAt(_selectedAnnotationIndex)
            _annotations.Insert(target, item)
            SelectedAnnotationIndex = target
            ' Z-Order-Wechsel wirkt nur dort, wo sich Objekte ueberlappen - das eigene Rect genuegt
            ' (Blend-Abhaengigkeiten erweitert der Region-Renderer automatisch).
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(item))
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
                   tool = EditorTool.Insert OrElse tool = EditorTool.Move OrElse
                   IsObjectScopeTool(tool)
        End Function

        ''' <summary>Das Drehen-Werkzeug: hier wirken Drehen/Spiegeln auf das markierte Objekt. Es darf weder
        ''' die Markierung verlieren (Werkzeugwechsel) noch beim Anklicken eines Objekts in dessen Werkzeug
        ''' springen - sonst könnte man ein Objekt hier gar nicht auswählen.</summary>
        Public Shared Function IsObjectTransformTool(tool As EditorTool) As Boolean
            Return tool = EditorTool.Rotate OrElse tool = EditorTool.Transform OrElse tool = EditorTool.Move
        End Function

        ''' <summary>Werkzeuge, deren REGLER auf ein markiertes Objekt wirken statt aufs Bild: Anpassen,
        ''' Farbe, Details+Effekte, Filter. Ohne markiertes Objekt bleibt alles wie immer (ganzes Bild).
        ''' „Rahmen" gehört bewusst NICHT dazu - der Rahmen sitzt an den Bildkanten; ein Rahmen um ein Objekt
        ''' wäre eine eigene, neue Funktion.</summary>
        Public Shared Function IsObjectAdjustTool(tool As EditorTool) As Boolean
            Return tool = EditorTool.Adjust OrElse tool = EditorTool.Color OrElse
                   tool = EditorTool.Effects OrElse tool = EditorTool.Filters
        End Function

        ''' <summary>Werkzeuge, in denen ein markiertes Objekt das Ziel ist (drehen/spiegeln oder anpassen).
        ''' In ihnen bleibt die Markierung bestehen, ein Klick markiert, und der Editor springt nicht ins
        ''' Werkzeug des Objekts.</summary>
        Public Shared Function IsObjectScopeTool(tool As EditorTool) As Boolean
            Return IsObjectTransformTool(tool) OrElse IsObjectAdjustTool(tool)
        End Function

        ''' <summary>Verwirft den Objekt-Modus OHNE Rückschreiben - für den Fall, dass das Objekt gerade
        ''' verschwindet (Löschen). Die Bildwerte kommen zurück in die Regler.</summary>
        ''' <summary>Schreibt aktive Objekt-Reglerwerte in die aktuell bearbeitete Ebene und stellt die
        ''' Bild-Regler wieder her, bevor der Ebenenstapel seine Indizes ändert.</summary>
        Private Sub CommitObjectAdjustModeToModel()
            If Not IsObjectAdjustModeActive() Then Return
            Dim keepIndex = _selectedAnnotationIndex
            _objectAdjustSwapInProgress = True
            Try
                Dim objectValues = BuildAdjustmentsFromFields().ExtractPixelAdjustments()
                If _objectAdjustIndex >= 0 AndAlso _objectAdjustIndex < _annotations.Count Then
                    _annotations(_objectAdjustIndex).Adjustments = If(objectValues.HasPixelAdjustments(), objectValues, Nothing)
                End If

                Dim restored = BuildAdjustmentsFromFields()
                restored.CopyPixelAdjustmentsFrom(_imagePixelAdjustments)
                _imagePixelAdjustments = Nothing
                _objectAdjustIndex = -1
                ApplyAdjustmentsKeepingSelection(restored, keepIndex)
            Finally
                _objectAdjustSwapInProgress = False
            End Try
            Me.RaisePropertyChanged(NameOf(IsAdjustingObject))
        End Sub

        Private Function IsObjectAdjustModeActive() As Boolean
            Return _objectAdjustIndex >= 0 AndAlso _imagePixelAdjustments IsNot Nothing
        End Function

        ''' <summary>Hält den Bearbeitungs-Zielwechsel in Gang: Sobald ein Objekt markiert ist UND ein
        ''' objektfähiges Werkzeug aktiv ist, beschreiben die Regler das Objekt - sonst das Bild. Beim
        ''' Umschalten werden die Werte des jeweils anderen Ziels geparkt bzw. zurückgeholt, damit weder das
        ''' Bild die Objektwerte abbekommt noch umgekehrt.</summary>
        Private Sub RefreshObjectAdjustMode()
            If _objectAdjustSwapInProgress Then Return
            ' ApplyAdjustments baut die Objektliste neu auf und hebt dabei die Markierung auf
            ' (_selectedAnnotationIndex = -1). Der gewünschte Index wird deshalb HIER festgehalten und nach
            ' jedem Umschalten wiederhergestellt - sonst rechnete der zweite Schritt mit -1 weiter und griff
            ' ins Leere (Absturz beim Anklicken eines Objekts im Anpassen-Werkzeug).
            Dim targetIndex = _selectedAnnotationIndex
            Dim shouldBeActive = targetIndex >= 0 AndAlso
                                 targetIndex < _annotations.Count AndAlso
                                 IsObjectAdjustTool(_currentTool)

            If shouldBeActive AndAlso IsObjectAdjustModeActive() AndAlso _objectAdjustIndex = targetIndex Then Return
            If Not shouldBeActive AndAlso Not IsObjectAdjustModeActive() Then Return

            _objectAdjustSwapInProgress = True
            Try
                If IsObjectAdjustModeActive() Then
                    ' Aktuelle Reglerwerte gehören dem bisherigen Objekt - dort hineinschreiben ...
                    Dim objectValues = BuildAdjustmentsFromFields().ExtractPixelAdjustments()
                    If _objectAdjustIndex >= 0 AndAlso _objectAdjustIndex < _annotations.Count Then
                        _annotations(_objectAdjustIndex).Adjustments = If(objectValues.HasPixelAdjustments(), objectValues, Nothing)
                    End If
                    ' ... und die geparkten Bildwerte zurück in die Regler.
                    Dim restored = BuildAdjustmentsFromFields()
                    restored.CopyPixelAdjustmentsFrom(_imagePixelAdjustments)
                    _imagePixelAdjustments = Nothing
                    _objectAdjustIndex = -1
                    ApplyAdjustmentsKeepingSelection(restored, targetIndex)
                End If

                If shouldBeActive AndAlso targetIndex < _annotations.Count Then
                    ' Bildwerte parken, Objektwerte in die Regler.
                    _imagePixelAdjustments = BuildAdjustmentsFromFields().ExtractPixelAdjustments()
                    _objectAdjustIndex = targetIndex
                    Dim target = BuildAdjustmentsFromFields()
                    target.CopyPixelAdjustmentsFrom(If(_annotations(targetIndex).Adjustments, New ImageAdjustments()))
                    ApplyAdjustmentsKeepingSelection(target, targetIndex)
                End If
            Finally
                _objectAdjustSwapInProgress = False
            End Try

            Me.RaisePropertyChanged(NameOf(IsAdjustingObject))
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>ApplyAdjustments verwirft die Objektmarkierung (es baut die Objektliste neu auf). Beim
        ''' Umschalten des Reglerziels darf das markierte Objekt aber nicht verlorengehen - die Markierung
        ''' wird deshalb direkt am Feld wiederhergestellt (der Property-Setter würde RefreshObjectAdjustMode
        ''' erneut auslösen).</summary>
        Private Sub ApplyAdjustmentsKeepingSelection(adj As ImageAdjustments, keepIndex As Integer)
            ApplyAdjustments(adj)
            If keepIndex < 0 OrElse keepIndex >= _annotations.Count Then Return
            _selectedAnnotationIndex = keepIndex
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationIndex))
            Me.RaisePropertyChanged(NameOf(SelectedLayer))
            Me.RaisePropertyChanged(NameOf(HasSelectedAnnotation))
            Me.RaisePropertyChanged(NameOf(CanRasterizeSelectedAnnotation))
        End Sub

        ''' <summary>True, während die Regler ein Objekt bedienen.</summary>
        Public ReadOnly Property IsAdjustingObject As Boolean
            Get
                Return IsObjectAdjustModeActive()
            End Get
        End Property

        Private Sub LoadSelectedAnnotationIntoEditor()
            _isLoadingAnnotation = True
            Try
                If _selectedAnnotationIndex >= 0 AndAlso _selectedAnnotationIndex < _annotations.Count Then
                    Dim a = _annotations(_selectedAnnotationIndex)
                    Dim normalizedKind = NormalizeAnnotationKind(a.Kind)
                    _watermarkImagePath = If(NormalizeAnnotationKind(a.Kind) = "Watermark", a.ImagePath, "")
                    ' Der Vorlagenname des zuvor selektierten Objekts passt nicht mehr zu den gleich
                    ' geladenen Werten - stattdessen die am Objekt hinterlegte Vorlage übernehmen
                    ' (leer, wenn es nicht aus einer Vorlage stammt).
                    Dim presetOfAnnotation = If(normalizedKind = "Watermark", If(a.WatermarkPresetName, ""), "")
                    If Not WatermarkPresetNames.Contains(presetOfAnnotation) Then presetOfAnnotation = ""
                    _selectedWatermarkPresetName = presetOfAnnotation
                    _watermarkPresetNameDraft = presetOfAnnotation
                    Me.RaisePropertyChanged(NameOf(SelectedWatermarkPresetName))
                    Me.RaisePropertyChanged(NameOf(WatermarkPresetNameDraft))
                    AnnotationText = a.Text
                    AnnotationFillColor = a.FillColor
                    AnnotationStrokeColor = a.StrokeColor
                    AnnotationStrokeWidth = a.StrokeWidth
                    AnnotationFontSize = a.FontSizePixels
                    AnnotationFontFamily = a.FontFamily
                    AnnotationOpacity = a.Opacity
                    AnnotationBlendMode = a.BlendMode
                    AnnotationRotation = a.RotationDegrees
                    AnnotationFlipHorizontal = a.FlipHorizontal
                    AnnotationFlipVertical = a.FlipVertical
                    AnnotationLockAspect = a.LockAspect
                    _annotationAnchor = NormalizeAnnotationAnchor(a.Anchor)
                    AnnotationIsVisible = a.IsVisible
                    AnnotationXPercent = AnnotationStoredXToPercent(a)
                    AnnotationYPercent = AnnotationStoredYToPercent(a)
                    AnnotationWidthPercent = AnnotationStoredWidthToPercent(a)
                    AnnotationHeightPercent = AnnotationStoredHeightToPercent(a)
                    AnnotationFillKind = a.FillKind
                    AnnotationTextPathKind = a.TextPathKind
                    AnnotationTextPathBend = a.TextPathBend
                    AnnotationTextPathStartOffset = a.TextPathStartOffset
                    AnnotationLetterSpacingPercent = a.LetterSpacingPercent
                    AnnotationBold = a.Bold
                    AnnotationItalic = a.Italic
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
                    If normalizedKind = "Brush" OrElse normalizedKind = "Eraser" Then
                        _isEraserMode = normalizedKind = "Eraser"
                        _brushSize = Math.Max(1, Math.Min(300, CDbl(a.StrokeWidth)))
                        _brushHardness = Math.Max(0, Math.Min(100, CDbl(a.HardnessPercent)))
                        _brushOpacity = Math.Max(0, Math.Min(100, CDbl(a.Opacity)))
                        _brushFlow = Math.Max(0, Math.Min(100, CDbl(a.FlowPercent)))
                        _brushPreset = If(String.IsNullOrWhiteSpace(a.BrushPreset), "soft", a.BrushPreset.Trim().ToLowerInvariant())
                        _eraserFillColor = If(String.IsNullOrWhiteSpace(a.EraserFillColor), "#00FFFFFF", a.EraserFillColor)
                        If _brushPresets IsNot Nothing Then
                            For Each item In _brushPresets
                                item.IsSelected = String.Equals(item.Key, _brushPreset, StringComparison.Ordinal)
                            Next
                        End If
                        Me.RaisePropertyChanged(NameOf(BrushSize))
                        Me.RaisePropertyChanged(NameOf(BrushHardness))
                        Me.RaisePropertyChanged(NameOf(BrushOpacity))
                        Me.RaisePropertyChanged(NameOf(BrushFlow))
                        Me.RaisePropertyChanged(NameOf(EraserFillColor))
                        Me.RaisePropertyChanged(NameOf(EraserFillColorValue))
                        Me.RaisePropertyChanged(NameOf(EraserFillBrush))
                    End If
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
            Dim previewSource = GetPreviewSource()
            Dim oldDirtyRect = If(previewSource Is Nothing, SKRectI.Empty,
                                  ImageProcessor.ComputeAnnotationDirtyRect(previewSource.Width, previewSource.Height, a,
                                                                            GetBaseWidth(), GetBaseHeight()))
            ' Pinsel- und Radiergummi-Ebenen haben keinen Text: ihre Züge liegen in a.Strokes. Das
            ' Text-Feld bleibt bei ihnen leer, damit nicht der Textpuffer des Editors hineinläuft.
            Dim normalizedKind = NormalizeAnnotationKind(a.Kind)
            If normalizedKind <> "Brush" AndAlso normalizedKind <> "Eraser" Then
                a.Text = _annotationText
            End If
            If normalizedKind = "Watermark" Then
                a.ImagePath = _watermarkImagePath
                ' Vorlagenbezug am Objekt festhalten, damit das Namensfeld beim erneuten Markieren
                ' wieder gefüllt ist und "Speichern" dieselbe Vorlage überschreibt.
                a.WatermarkPresetName = If(_selectedWatermarkPresetName, "")
            End If
            a.FillColor = _annotationFillColor
            a.StrokeColor = _annotationStrokeColor
            a.StrokeWidth = If(normalizedKind = "Brush" OrElse normalizedKind = "Eraser", CSng(_brushSize), CSng(_annotationStrokeWidth))
            a.FontSizePixels = CSng(_annotationFontSize)
            a.FontFamily = _annotationFontFamily
            a.Opacity = If(normalizedKind = "Brush" OrElse normalizedKind = "Eraser", CSng(_brushOpacity), CSng(_annotationOpacity))
            If normalizedKind = "Brush" OrElse normalizedKind = "Eraser" Then
                a.HardnessPercent = CSng(_brushHardness)
                a.FlowPercent = CSng(_brushFlow)
                a.BrushPreset = If(normalizedKind = "Eraser", "soft", _brushPreset)
                a.EraserFillColor = _eraserFillColor
            End If
            a.BlendMode = _annotationBlendMode
            a.RotationDegrees = CSng(_annotationRotation)
            a.FlipHorizontal = _annotationFlipH
            a.FlipVertical = _annotationFlipV
            a.LockAspect = _annotationLockAspect
            a.Anchor = If(normalizedKind = "Watermark", NormalizeAnnotationAnchor(_annotationAnchor), "")
            a.IsVisible = _annotationIsVisible
            a.XPixels = CSng(AnnotationXPixels)
            a.YPixels = CSng(AnnotationYPixels)
            a.WidthPixels = CSng(Math.Max(1, AnnotationWidthPixels))
            a.HeightPixels = CSng(Math.Max(1, AnnotationHeightPixels))
            a.FillKind = _annotationFillKind
            a.TextPathKind = _annotationTextPathKind
            a.TextPathBend = CSng(_annotationTextPathBend)
            a.TextPathStartOffset = CSng(_annotationTextPathStartOffset)
            a.LetterSpacingPercent = CSng(_annotationLetterSpacingPercent)
            a.Bold = _annotationBold
            a.Italic = _annotationItalic
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
            If previewSource IsNot Nothing Then
                Dim newDirtyRect = ImageProcessor.ComputeAnnotationDirtyRect(previewSource.Width, previewSource.Height, a,
                                                                             GetBaseWidth(), GetBaseHeight())
                If _annotationPlacementEditActive Then
                    _annotationDirtyRect = ImageProcessor.UnionRects(_annotationPlacementStartDirtyRect, newDirtyRect)
                Else
                    _annotationDirtyRect = ImageProcessor.UnionRects(_annotationDirtyRect,
                                                                     ImageProcessor.UnionRects(oldDirtyRect, newDirtyRect))
                End If
            End If
            Me.RaisePropertyChanged(NameOf(SelectedAnnotationText))
            If refreshOverlay Then UpdateSelectedAnnotationOverlayPreview()
            RaiseResetButtonStateChanged()
            If _annotationPlacementEditActive Then
                _previewTimer.Stop()
                _previewPending = True
                StatusText = LocalizationService.T("Vorschau wird aktualisiert...")
                Return
            End If
            ' STUFE 2: alle Objekt-Eigenschafts-Aenderungen laufen einheitlich in die Szene
            ' (RefreshSelected... rendert die Dirty-Region oder debounced sie bei Bursts).
            RefreshSelectedAnnotationPreviewImmediatelyIfNeeded()
        End Sub

        Private Sub SyncSelectedAnnotationIfStroke()
            If _isLoadingAnnotation OrElse Not IsSelectedStrokeAnnotation() Then Return
            SyncSelectedAnnotation()
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

            If AnnotationRequiresBakedPreview(annotation) Then
                If Not _annotationPlacementEditActive Then
                    SetSelectedAnnotationOverlay(Nothing)
                    Return
                End If
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

        Public Sub ClearPendingCrop(Optional captureUndo As Boolean = True)
            If Not HasCropChanges Then Return
            If captureUndo Then PushUndo()
            SetCropPercentages(0, 0, 0, 0)
        End Sub

        ''' captureUndo=True markiert den Beginn eines Zuges (Mausklick), False die Zwischenpunkte
        ''' beim Ziehen.
        Public Sub AddRetouchSpot(xPercent As Double, yPercent As Double, Optional captureUndo As Boolean = True)
            If Not CanUsePixelTools Then Return
            ' Der Stempel braucht eine Quelle. Ohne sie würde er stillschweigend zur Retusche -
            ' der Nutzer soll stattdessen erst Alt+Klick machen (siehe RetouchHintText).
            If IsCloneMode AndAlso Not HasCloneSource Then Return
            If captureUndo Then
                PushUndo()
                _retouchStrokeActive = True
                _retouchStrokeStartSpotIndex = _retouchSpots.Count
                _activeRetouchStrokeId = _nextRetouchStrokeId
                _nextRetouchStrokeId += 1
                _previewTimer.Stop()
                If _clearRetouchLivePatchAfterPreview Then
                    ' BEFUND ("Reparatur 1 bei Reparatur 2 wieder weg"): Der Commit-Render
                    ' des VORHERIGEN Zugs laeuft noch. Ihn abzubrechen (InvalidatePreviewWork)
                    ' hiesse, dass Zug 1 NIE in die Szene gebacken wird; die Live-Bruecke samt
                    ' Rect zu loeschen, liesse Zug 1 bis dahin verschwinden. Beides stehen
                    ' lassen - das Patch-Rect waechst mit dem neuen Zug einfach weiter.
                Else
                    _retouchLivePatchRect = SKRectI.Empty
                    ClearRetouchLivePatch()
                    InvalidatePreviewWork()
                End If
                ' STEMPEL-LIVE: vorgewaermte Puffer NICHT blind wegwerfen - sonst
                ' beginnt jeder Zug wieder ohne Live-Ansicht. Der BaseKey (enthaelt Spots UND alle
                ' Anpassungen) entscheidet, ob der Puffer noch den committeten Stand zeigt.
                If Not RetouchLiveBuffersMatchCommittedState() Then
                    DiagnosticLogService.LogAlways("Editor.RetouchBuffers",
                        $"discardAtStrokeStart hadBuffers={_retouchLiveBitmap IsNot Nothing} hadKey={_retouchBuffersKey IsNot Nothing}")
                    DisposeRetouchLiveBuffers()
                End If
            End If

            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            ' Der Mittelpunkt darf bis zu EINEN RADIUS ausserhalb liegen: wer am Bildrand ansetzt,
            ' meint den Teil des Kreises, der ueber dem Bild liegt. Frueher wurde hart auf den Rand
            ' geklemmt - dann wirkte dort ein VOLLER Kreis, also mehr als der Cursorring anzeigte.
            ' Weiter als einen Radius hinaus beruehrt der Kreis das Bild ohnehin nicht mehr; das
            ' Zeichnen selbst klemmt zusaetzlich auf die Bitmapgrenzen (DrawRetouchSpot).
            Dim reach = Math.Max(1, CInt(Math.Ceiling(_retouchRadius)))
            Dim targetX = Math.Max(-reach, Math.Min(baseWidth + reach, PercentXToPixels(xPercent)))
            Dim targetY = Math.Max(-reach, Math.Min(baseHeight + reach, PercentYToPixels(yPercent)))

            Dim spot = New RetouchSpot With {
                .XPixels = CSng(targetX),
                .YPixels = CSng(targetY),
                .RadiusPixels = CSng(_retouchRadius),
                .StrengthPercent = CSng(_brushHardness),
                .OpacityPercent = CSng(_brushOpacity),
                .FlowPercent = CSng(_brushFlow),
                .Mode = If(_isRepairMode AndAlso Not _isCloneMode, "Heal", "Blur"),
                .StrokeId = If(_isRepairMode AndAlso Not _isCloneMode, _activeRetouchStrokeId, 0)
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
            ' die 30 Einträge fluten. Beim Ziehen rendert der Stempel/Retusche live gedrosselt:
            ' schnell genug für sichtbares Zeichnen, aber ohne für jeden Pointer-Punkt einen kompletten
            ' Pipeline-Render zu starten.
            If captureUndo Then AddHistoryEntry(If(IsCloneMode, "Stempeln", If(IsRepairMode, "Reparatur", "Verwischen")))
            UpdateRetouchLivePreview(spot, captureUndo)
        End Sub

        Private Sub UpdateRetouchLivePreview(spot As RetouchSpot, forcePublish As Boolean)
            If spot Is Nothing Then Return
            If Not _retouchStrokeActive Then
                ScheduleRetouchPreviewUpdate(forcePublish)
                Return
            End If

            If String.Equals(spot.Mode, "Heal", StringComparison.OrdinalIgnoreCase) AndAlso IsRepairMode Then
                If Not EnsureRetouchMaskPreviewSize() Then
                    ScheduleRetouchPreviewUpdate(forcePublish)
                    Return
                End If
                ExpandRetouchMaskPatchRect(spot)
                PublishRetouchMaskPreview(forcePublish)
                Return
            End If

            ' BEFUND: Die Schwelle gilt im PREVIEW-Raum - die Kosten der Live-Anwendung
            ' haengen am preview-skalierten Radius, nicht an den Basis-Pixeln. Der alte Vergleich
            ' in Basis-Pixeln schickte grosse Pinsel immer in die Masken-Vorschau ("keine
            ' Live-Ansicht"), obwohl der effektive Radius harmlos war.
            ' STEMPEL IMMER LIVE ("Live nur beim ersten Zug"): Klon-Spots sind ein
            ' billiger Shader-Draw (DrawCloneStamp) - die Schwelle schuetzt nur vor dem teuren
            ' Box-Blur des VERWISCHENS.
            If Not spot.HasCloneSource AndAlso spot.RadiusPixels * RetouchPreviewRadiusScale() > 240 Then
                ' Grosser Radius: Live-Anwendung pro Mauspunkt waere zu teuer - aber GAR KEIN Feedback
                ' ("es passiert nichts") ist schlimmer. Die orangene Masken-Vorschau zeigt den
                ' bearbeiteten Bereich; der Commit backt das Ergebnis.
                If EnsureRetouchMaskPreviewSize() Then
                    ExpandRetouchMaskPatchRect(spot)
                    PublishRetouchMaskPreview(forcePublish)
                End If
                Return
            End If

            If _retouchLiveBitmap Is Nothing OrElse _retouchLiveSampleBitmap Is Nothing Then
                ' STUFE 5: Puffer-Aufbau ASYNCHRON - der synchrone Aufbau rendert bis zu zwei volle
                ' Pipelines (~1-1,5 s je) und fror die UI beim ersten Punkt ein ("CPU hoch, nichts
                ' passiert"). Bis die Puffer stehen, zeigt die Masken-Vorschau den Strich; danach
                ' zieht der Init-Abschluss alle aufgelaufenen Zug-Punkte nach.
                BeginRetouchLiveBuffersAsync()
                If EnsureRetouchMaskPreviewSize() Then
                    ExpandRetouchMaskPatchRect(spot)
                    PublishRetouchMaskPreview(forcePublish)
                End If
                Return
            End If

            ImageProcessor.ApplyRetouchSpotInPlace(_retouchLiveBitmap, _retouchLiveSampleBitmap, spot, GetBaseWidth(), GetBaseHeight())
            ExpandRetouchLivePatchRect(spot)
            PublishRetouchLivePreview(forcePublish)
        End Sub

        Private _retouchBuffersInitSeq As Long = 0
        Private _retouchBuffersInitializing As Boolean = False
        ''' BaseKey (Spots + Anpassungen), zu dem die Live-Puffer passen - Gueltigkeitsstempel
        ''' fuers Vorwaermen (siehe AddRetouchSpot).
        Private _retouchBuffersKey As String = Nothing

        ''' Preview-Pixel je Basis-Pixel fuer Retusche-Radien - dieselbe sqrt(sx*sy)-Formel wie der
        ''' Renderer (DrawRetouchSpot/DrawHealingRegion).
        Private Function RetouchPreviewRadiusScale() As Single
            Dim src = GetPreviewSource()
            Dim baseW = GetBaseWidth()
            Dim baseH = GetBaseHeight()
            If src Is Nothing OrElse baseW <= 0 OrElse baseH <= 0 Then Return 1.0F
            Return CSng(Math.Sqrt((src.Width / CDbl(baseW)) * (src.Height / CDbl(baseH))))
        End Function

        ''' <summary>True, wenn die vorhandenen Live-Puffer exakt den committeten Stand spiegeln -
        ''' dann darf das Vorwaermen sie behalten. Seit Stufe E steckt die committete Retusche im
        ''' Arbeitsbild (WorkingImageVersion ist Teil des Keys) - kein Spot-Jonglieren mehr.</summary>
        Private Function RetouchLiveBuffersMatchCommittedState() As Boolean
            If _retouchLiveBitmap Is Nothing OrElse _retouchLiveSampleBitmap Is Nothing OrElse
               _retouchBuffersKey Is Nothing Then Return False
            Return String.Equals(_retouchBuffersKey, ImageProcessor.ComputeBaseKey(GetCurrentAdjustments(forPreview:=True)), StringComparison.Ordinal)
        End Function

        Private Sub BeginRetouchLiveBuffersAsync()
            If _retouchBuffersInitializing Then Return
            ' Vorwaermen (Werkzeugwechsel/Alt+Klick): passende Puffer nicht neu bauen.
            If RetouchLiveBuffersMatchCommittedState() Then Return
            _retouchBuffersInitializing = True
            RunRetouchBufferInit()
        End Sub

        ''' <summary>Baut Ziel- und Sample-Bitmap im Hintergrund auf. Seit Stufe E braucht es nur
        ''' noch EINEN Render: die committete Retusche steckt im Arbeitsbild, Ziel = Klon der
        ''' warmen Basis (sonst Voll-Render), Sample = Kopie des Ziels (die Werkzeuge lesen beim
        ''' Commit ebenfalls vom aktuellen Stand des Arbeitsbilds). Nach dem Aufbau werden alle
        ''' bis dahin aufgelaufenen Punkte des aktiven Zugs nachgezogen. Bildwechsel/Dispose
        ''' invalidieren per Sequenznummer.</summary>
        Private Async Sub RunRetouchBufferInit()
            Dim seq = Threading.Interlocked.Increment(_retouchBuffersInitSeq)
            Try
                Dim previewSource = GetPreviewSource()
                If previewSource Is Nothing Then Return

                Dim strokeStart = If(_retouchStrokeActive,
                                     Math.Max(0, Math.Min(_retouchStrokeStartSpotIndex, _retouchSpots.Count)),
                                     _retouchSpots.Count)
                Dim targetAdj = GetCurrentAdjustments(forPreview:=True)

                Dim target As SKBitmap = Nothing
                Dim sample As SKBitmap = Nothing
                Try
                    Await Task.Run(Sub()
                                       target = ImageProcessor.TryCloneBaseCachedBitmap(previewSource, targetAdj)
                                       If target Is Nothing Then target = ImageProcessor.RenderPreviewSkBitmap(previewSource, targetAdj)
                                       sample = target?.Copy()
                                   End Sub)
                Catch ex As Exception
                    target?.Dispose()
                    sample?.Dispose()
                    DiagnosticLogService.LogException("EditorRetouchInit", ex)
                    Return
                End Try

                If seq <> _retouchBuffersInitSeq OrElse target Is Nothing OrElse sample Is Nothing OrElse
                   Not Object.ReferenceEquals(GetPreviewSource(), previewSource) Then
                    target?.Dispose()
                    sample?.Dispose()
                    Return
                End If

                DisposeRetouchLiveBuffers(keepInitSeq:=True)
                _retouchLiveBitmap = target
                _retouchLiveSampleBitmap = sample
                _retouchBuffersKey = ImageProcessor.ComputeBaseKey(targetAdj)

                ' Aufgelaufene Punkte des aktiven Zugs nachziehen und die echte Vorschau uebernehmen.
                If _retouchStrokeActive Then
                    Dim pendingSpots = _retouchSpots.Skip(strokeStart).Where(Function(s) s IsNot Nothing).ToList()
                    If pendingSpots.Count > 0 Then
                        ImageProcessor.ApplyRetouchSpotsInPlace(_retouchLiveBitmap, _retouchLiveSampleBitmap, pendingSpots,
                                                                GetBaseWidth(), GetBaseHeight())
                        For Each s In pendingSpots
                            ExpandRetouchLivePatchRect(s)
                        Next
                        PublishRetouchLivePreview(True)
                    End If
                End If
            Finally
                _retouchBuffersInitializing = False
            End Try
        End Sub

        Private Sub PublishRetouchLivePreview(force As Boolean)
            If _retouchLiveBitmap Is Nothing OrElse _retouchLivePatchRect.IsEmpty Then Return
            Dim now = DateTime.UtcNow
            If force OrElse (now - _lastRetouchLivePreviewUtc).TotalMilliseconds >= 24.0 Then
                _lastRetouchLivePreviewUtc = now
                Dim patch = ImageProcessor.RenderBitmapPatch(_retouchLiveBitmap, _retouchLivePatchRect)
                If patch IsNot Nothing Then
                    RetouchLivePatchImage = patch
                    UpdateRetouchLivePatchPercentages()
                End If
                _previewPending = True
                StatusText = LocalizationService.T("Vorschau wird aktualisiert...")
            End If
        End Sub

        Private Function EnsureRetouchMaskPreviewSize() As Boolean
            If _retouchLiveMaskBitmapWidth > 0 AndAlso _retouchLiveMaskBitmapHeight > 0 Then Return True
            Dim previewSource = GetPreviewSource()
            If previewSource Is Nothing OrElse previewSource.Width <= 0 OrElse previewSource.Height <= 0 Then Return False
            _retouchLiveMaskBitmapWidth = previewSource.Width
            _retouchLiveMaskBitmapHeight = previewSource.Height
            Return True
        End Function

        Private Sub PublishRetouchMaskPreview(force As Boolean)
            If _retouchLivePatchRect.IsEmpty OrElse _retouchLiveMaskBitmapWidth <= 0 OrElse _retouchLiveMaskBitmapHeight <= 0 Then Return
            Dim now = DateTime.UtcNow
            If force OrElse (now - _lastRetouchLivePreviewUtc).TotalMilliseconds >= 24.0 Then
                _lastRetouchLivePreviewUtc = now
                Dim strokeSpots = _retouchSpots.
                    Skip(Math.Max(0, Math.Min(_retouchStrokeStartSpotIndex, _retouchSpots.Count))).
                    Where(Function(s) s IsNot Nothing AndAlso String.Equals(s.Mode, "Heal", StringComparison.OrdinalIgnoreCase)).
                    Select(Function(s) s.Clone()).
                    ToList()
                Dim patch = ImageProcessor.RenderRetouchMaskPatch(strokeSpots,
                                                                  _retouchLivePatchRect,
                                                                  _retouchLiveMaskBitmapWidth,
                                                                  _retouchLiveMaskBitmapHeight,
                                                                  GetBaseWidth(),
                                                                  GetBaseHeight())
                If patch IsNot Nothing Then
                    RetouchLivePatchImage = patch
                    UpdateRetouchLivePatchPercentages()
                End If
            End If
        End Sub

        Private Sub ExpandRetouchLivePatchRect(spot As RetouchSpot)
            If spot Is Nothing OrElse _retouchLiveBitmap Is Nothing Then Return

            Dim scaleX As Single = 1.0F
            Dim scaleY As Single = 1.0F
            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            If baseWidth > 0 AndAlso baseHeight > 0 Then
                scaleX = _retouchLiveBitmap.Width / CSng(baseWidth)
                scaleY = _retouchLiveBitmap.Height / CSng(baseHeight)
            End If

            Dim radiusScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))
            Dim cx = CSng(spot.XPixels * scaleX)
            Dim cy = CSng(spot.YPixels * scaleY)
            Dim radius = CSng(Math.Max(2.0F, spot.RadiusPixels * radiusScale + 3.0F))
            Dim rect = New SKRectI(Math.Max(0, CInt(Math.Floor(cx - radius))),
                                   Math.Max(0, CInt(Math.Floor(cy - radius))),
                                   Math.Min(_retouchLiveBitmap.Width, CInt(Math.Ceiling(cx + radius))),
                                   Math.Min(_retouchLiveBitmap.Height, CInt(Math.Ceiling(cy + radius))))
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return

            If _retouchLivePatchRect.IsEmpty Then
                _retouchLivePatchRect = rect
            Else
                _retouchLivePatchRect = New SKRectI(Math.Min(_retouchLivePatchRect.Left, rect.Left),
                                                    Math.Min(_retouchLivePatchRect.Top, rect.Top),
                                                    Math.Max(_retouchLivePatchRect.Right, rect.Right),
                                                    Math.Max(_retouchLivePatchRect.Bottom, rect.Bottom))
            End If
        End Sub

        Private Sub ExpandRetouchMaskPatchRect(spot As RetouchSpot)
            If spot Is Nothing OrElse _retouchLiveMaskBitmapWidth <= 0 OrElse _retouchLiveMaskBitmapHeight <= 0 Then Return

            Dim scaleX As Single = 1.0F
            Dim scaleY As Single = 1.0F
            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            If baseWidth > 0 AndAlso baseHeight > 0 Then
                scaleX = _retouchLiveMaskBitmapWidth / CSng(baseWidth)
                scaleY = _retouchLiveMaskBitmapHeight / CSng(baseHeight)
            End If

            Dim radiusScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))
            Dim cx = CSng(spot.XPixels * scaleX)
            Dim cy = CSng(spot.YPixels * scaleY)
            Dim radius = CSng(Math.Max(2.0F, spot.RadiusPixels * radiusScale + 3.0F))
            Dim rect = New SKRectI(Math.Max(0, CInt(Math.Floor(cx - radius))),
                                   Math.Max(0, CInt(Math.Floor(cy - radius))),
                                   Math.Min(_retouchLiveMaskBitmapWidth, CInt(Math.Ceiling(cx + radius))),
                                   Math.Min(_retouchLiveMaskBitmapHeight, CInt(Math.Ceiling(cy + radius))))
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return

            If _retouchLivePatchRect.IsEmpty Then
                _retouchLivePatchRect = rect
            Else
                _retouchLivePatchRect = New SKRectI(Math.Min(_retouchLivePatchRect.Left, rect.Left),
                                                    Math.Min(_retouchLivePatchRect.Top, rect.Top),
                                                    Math.Max(_retouchLivePatchRect.Right, rect.Right),
                                                    Math.Max(_retouchLivePatchRect.Bottom, rect.Bottom))
            End If
        End Sub

        Private Sub UpdateRetouchLivePatchPercentages()
            Dim bitmapWidth = If(_retouchLiveBitmap IsNot Nothing, _retouchLiveBitmap.Width, _retouchLiveMaskBitmapWidth)
            Dim bitmapHeight = If(_retouchLiveBitmap IsNot Nothing, _retouchLiveBitmap.Height, _retouchLiveMaskBitmapHeight)
            If _retouchLivePatchRect.IsEmpty OrElse bitmapWidth <= 0 OrElse bitmapHeight <= 0 Then Return

            _retouchLivePatchLeftPercent = _retouchLivePatchRect.Left / CDbl(bitmapWidth) * 100.0
            _retouchLivePatchTopPercent = _retouchLivePatchRect.Top / CDbl(bitmapHeight) * 100.0
            _retouchLivePatchWidthPercent = _retouchLivePatchRect.Width / CDbl(bitmapWidth) * 100.0
            _retouchLivePatchHeightPercent = _retouchLivePatchRect.Height / CDbl(bitmapHeight) * 100.0
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchLeftPercent))
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchTopPercent))
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchWidthPercent))
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchHeightPercent))
        End Sub

        Private Sub ClearRetouchLivePatch()
            RetouchLivePatchImage = Nothing
            _retouchLivePatchRect = SKRectI.Empty
            _retouchLivePatchLeftPercent = 0
            _retouchLivePatchTopPercent = 0
            _retouchLivePatchWidthPercent = 0
            _retouchLivePatchHeightPercent = 0
            _retouchLiveMaskBitmapWidth = 0
            _retouchLiveMaskBitmapHeight = 0
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchLeftPercent))
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchTopPercent))
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchWidthPercent))
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchHeightPercent))
        End Sub

        Private Sub ScheduleRetouchPreviewUpdate(forceImmediate As Boolean)
            Dim now = DateTime.UtcNow
            If forceImmediate OrElse (now - _lastRetouchLivePreviewUtc).TotalMilliseconds >= RetouchLivePreviewMinIntervalMs Then
                _lastRetouchLivePreviewUtc = now
                UpdatePreview()
            Else
                SchedulePreviewUpdate()
            End If
        End Sub

        ''' ARBEITSBILD (Stufe E): der Zug wird REGIONAL in Vollauflösung ins Arbeitsbild
        ''' eingebacken (Hintergrund-Queue) - kein Rezept-Replay mehr. Der Live-Patch (bzw. die
        ''' Orange-Maske bei Heal/Großradius) überbrückt, bis der Commit-Render landet; Undo
        ''' läuft über den Vorher-Patch des Commits.
        Public Sub CommitRetouchStroke()
            If Not _retouchStrokeActive Then Return
            _retouchStrokeActive = False
            Dim strokeStart = Math.Max(0, Math.Min(_retouchStrokeStartSpotIndex, _retouchSpots.Count))
            Dim strokeSpots = _retouchSpots.Skip(strokeStart).
                Where(Function(s) s IsNot Nothing).
                Select(Function(s) s.Clone()).
                ToList()
            Dim strokeHasHeal = strokeSpots.Any(
                Function(s) Not s.HasCloneSource AndAlso String.Equals(s.Mode, "Heal", StringComparison.OrdinalIgnoreCase))
            ' Spots sind TRANSIENT: ab dem Commit sind die Pixel des Arbeitsbilds die Wahrheit.
            _retouchSpots.Clear()
            _retouchStrokeStartSpotIndex = 0
            _lastRetouchLivePreviewUtc = DateTime.MinValue
            _previewTimer.Stop()
            _previewPending = False
            If strokeSpots.Count = 0 Then Return

            Dim undoEntry = _lastPushedUndoEntry   ' PushUndo kam beim Zugstart (AddRetouchSpot)
            Dim baseW = GetBaseWidth()
            Dim baseH = GetBaseHeight()
            Dim rect = ComputeRetouchStrokeFullRect(strokeSpots, baseW, baseH)
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return

            ' Live-Patch/Maske stehen lassen - die Brücke, bis der Commit-Render landet.
            PublishRetouchLivePreview(True)
            _clearRetouchLivePatchAfterPreview = True
            StatusText = LocalizationService.T("Vorschau wird aktualisiert...")

            Dim sw = Diagnostics.Stopwatch.StartNew()
            EnqueueWorkingCommit(
                Function()
                    Return _workingImage.CommitRegion(rect,
                        Sub(full)
                            ' Schreibt nur innerhalb der Spot-Masken (liegen in rect); die
                            ' Heal-Kandidatensuche LIEST frei aus der Umgebung des Vollbilds.
                            ImageProcessor.ApplyRetouchSpotsInPlace(full, full, strokeSpots, baseW, baseH)
                        End Sub)
                End Function,
                Sub(patch)
                    If undoEntry IsNot Nothing Then undoEntry.Patch = patch
                    DiagnosticLogService.LogAlways("Editor.RetouchCommit",
                        $"spots={strokeSpots.Count} heal={strokeHasHeal} rect={rect.Left},{rect.Top},{rect.Width}x{rect.Height} pixels={CLng(rect.Width) * CLng(rect.Height)} ms={sw.ElapsedMilliseconds}")
                    ' Nicht-Heal-Zug: das Live-Target enthält den Zug bereits (live gemalt) und das
                    ' Arbeitsbild jetzt auch - Sample auf den neuen Stand kopieren und NUR den
                    ' Gültigkeitsstempel auf die neue Arbeitsbild-Version nachziehen. Ohne das warf
                    ' jeder Zugstart die Puffer weg (Log: discardAtStrokeStart hadBuffers=True) und
                    ' der Stempel verlor seine Live-Ansicht ab Zug 2 (alter Nutzertest-24-Befund).
                    If Not strokeHasHeal AndAlso _retouchLiveBitmap IsNot Nothing Then
                        Dim refreshedSample = _retouchLiveBitmap.Copy()
                        If refreshedSample IsNot Nothing Then
                            _retouchLiveSampleBitmap?.Dispose()
                            _retouchLiveSampleBitmap = refreshedSample
                            _retouchBuffersKey = ImageProcessor.ComputeBaseKey(GetCurrentAdjustments(forPreview:=True))
                        Else
                            DisposeRetouchLiveBuffers()
                        End If
                    End If
                    SchedulePreviewUpdate()
                End Sub)

            ' Live-Puffer nach Heal-Zügen: das Target zeigt den ungeheilten Stand -> wegwerfen;
            ' der nächste Zugstart baut sie asynchron neu auf (dann inkl. Heilung).
            If strokeHasHeal Then
                DisposeRetouchLiveBuffers()
            End If
        End Sub

        ''' Zug-Region in Basis-Bildpixeln: Vereinigung aller Spot-Kreise plus Rand für weiche
        ''' Kanten und Blur (DrawBlurSpot-Pad = r + 3*sigma + 2 mit sigma <= 0,22r - der Faktor 2
        ''' deckt das großzügig ab). Geschrieben wird nur innerhalb dieser Region (harte
        ''' Anforderung des Umbaus); gelesen werden darf außerhalb.
        Private Shared Function ComputeRetouchStrokeFullRect(spots As List(Of RetouchSpot),
                                                             baseW As Integer, baseH As Integer) As SKRectI
            If spots Is Nothing OrElse spots.Count = 0 OrElse baseW <= 0 OrElse baseH <= 0 Then Return SKRectI.Empty
            Dim left As Double = Double.MaxValue, top As Double = Double.MaxValue
            Dim right As Double = Double.MinValue, bottom As Double = Double.MinValue
            For Each s In spots
                Dim margin = s.RadiusPixels * 2.0F + 16.0F
                left = Math.Min(left, s.XPixels - margin)
                top = Math.Min(top, s.YPixels - margin)
                right = Math.Max(right, s.XPixels + margin)
                bottom = Math.Max(bottom, s.YPixels + margin)
            Next
            If left > right OrElse top > bottom Then Return SKRectI.Empty
            Return New SKRectI(Math.Max(0, CInt(Math.Floor(left))),
                               Math.Max(0, CInt(Math.Floor(top))),
                               Math.Min(baseW, CInt(Math.Ceiling(right))),
                               Math.Min(baseH, CInt(Math.Ceiling(bottom))))
        End Function

        Private Sub DisposeRetouchLiveBuffers(Optional keepInitSeq As Boolean = False)
            ' Laufende asynchrone Puffer-Initialisierung invalidieren (Bildwechsel, Werkzeugwechsel) -
            ' ausser der Init-Abschluss selbst raeumt gerade die alten Puffer weg (keepInitSeq).
            If Not keepInitSeq Then _retouchBuffersInitSeq += 1
            If _retouchLiveBitmap IsNot Nothing Then
                _retouchLiveBitmap.Dispose()
                _retouchLiveBitmap = Nothing
            End If
            If _retouchLiveSampleBitmap IsNot Nothing Then
                _retouchLiveSampleBitmap.Dispose()
                _retouchLiveSampleBitmap = Nothing
            End If
            _retouchBuffersKey = Nothing
            _retouchLiveMaskBitmapWidth = 0
            _retouchLiveMaskBitmapHeight = 0
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
                    ResetEffectsInternal()
                Case EditorTool.Frame
                    ResetDetailInternal()
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
            _colorNoiseReduction = 0
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
            ' ARBEITSBILD (Stufe E): committete Retusche ist eingebacken und wird hier bewusst
            ' NICHT entfernt (das würde auch Pinselstriche wegwischen - dafür gibt es Undo bzw.
            ' das globale Zurücksetzen). Hier nur Werkzeug-Zustand + evtl. offenen Zug verwerfen.
            _retouchRadius = 24.0
            _retouchSpots.Clear()
            _retouchStrokeActive = False
            _retouchStrokeStartSpotIndex = 0
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
            SchedulePreviewForCurrentTarget()
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
            Me.RaisePropertyChanged(NameOf(ColorNoiseReduction))
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
            Me.RaisePropertyChanged(NameOf(IsRepairMode))
            Me.RaisePropertyChanged(NameOf(RetouchHintText))
            Me.RaisePropertyChanged(NameOf(ShowSelectionAdjustments))
            Me.RaisePropertyChanged(NameOf(ShowDrawControls))
            Me.RaisePropertyChanged(NameOf(ShowBrushStrokeAdjustments))
            Me.RaisePropertyChanged(NameOf(IsBrushPaintMode))
            Me.RaisePropertyChanged(NameOf(IsEraserPaintMode))
            Me.RaisePropertyChanged(NameOf(EraserFillColor))
            Me.RaisePropertyChanged(NameOf(EraserFillColorValue))
            Me.RaisePropertyChanged(NameOf(EraserFillBrush))
            Me.RaisePropertyChanged(NameOf(IsSmudgePaintMode))
            Me.RaisePropertyChanged(NameOf(BrushFlow))
            Me.RaisePropertyChanged(NameOf(ShowLayerToolOptions))
            Me.RaisePropertyChanged(NameOf(ShowGeometryControls))
            Me.RaisePropertyChanged(NameOf(ShowShapeControls))
            Me.RaisePropertyChanged(NameOf(ShowSymbolControls))
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
                        _isRepairMode = False
                        CurrentTool = EditorTool.Retouch
                    Case "repair", "reparatur", "reparaturpinsel", "heal", "heilen", "retusche"
                        _isCloneMode = False
                        _isRepairMode = True
                        CurrentTool = EditorTool.Retouch
                    Case "clone", "stempel"
                        _isCloneMode = True
                        _isRepairMode = False
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
            Me.RaisePropertyChanged(NameOf(IsRepairMode))
            ' Verwischen <-> Reparatur <-> Stempel wechselt das Werkzeug nicht, wohl aber seinen Namen.
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
                    state.Flow = _brushFlow
                Case "Eraser"
                    state.Size = _brushSize
                    state.Hardness = _brushHardness
                    state.Opacity = _brushOpacity
                    state.Flow = _brushFlow
                    state.EraserFillColor = _eraserFillColor
                Case "Blur", "Repair", "Clone"
                    state.Size = _retouchRadius
                    state.Hardness = _brushHardness
                    state.Opacity = _brushOpacity
                    state.Flow = _brushFlow
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
                    BrushFlow = state.Flow
                Case "Eraser"
                    BrushSize = state.Size
                    BrushHardness = state.Hardness
                    BrushOpacity = state.Opacity
                    BrushFlow = state.Flow
                    EraserFillColor = state.EraserFillColor
                Case "Blur", "Repair", "Clone"
                    RetouchRadius = state.Size
                    BrushHardness = state.Hardness
                    BrushOpacity = state.Opacity
                    BrushFlow = state.Flow
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
            If adj.RasterPaintStrokes IsNot Nothing AndAlso adj.RasterPaintStrokes.Count > 0 Then Return "Pinsel/Radierer"
            If adj.Annotations IsNot Nothing AndAlso adj.Annotations.Count > 0 Then Return "Text"
            If adj.RotationDegrees <> 0 OrElse adj.StraightenDegrees <> 0 OrElse adj.FlipHorizontal OrElse adj.FlipVertical Then Return "Transformieren"
            If Not ImageAdjustments.IsIdentityCurve(adj.CurveRgbPoints) OrElse Not ImageAdjustments.IsIdentityCurve(adj.CurveRedPoints) OrElse
               Not ImageAdjustments.IsIdentityCurve(adj.CurveGreenPoints) OrElse Not ImageAdjustments.IsIdentityCurve(adj.CurveBluePoints) OrElse
               Not ImageAdjustments.IsIdentityCurve(adj.CurveLuminancePoints) Then Return "Tonwertkurve"
            If adj.HasHslChanges() Then Return "Farbmischer"
            If adj.Clarity <> 0 OrElse adj.Sharpness <> 0 OrElse adj.NoiseReduction <> 0 OrElse adj.ColorNoiseReduction <> 0 OrElse adj.Grain <> 0 Then Return "Details"
            If adj.Vignette <> 0 OrElse adj.BorderSize <> 0 Then Return "Vignette/Rahmen"
            If Not String.Equals(adj.FilterPreset, "Keine", StringComparison.OrdinalIgnoreCase) Then Return "Filter"
            Return "Anpassung"
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
                SchedulePreviewForCurrentTarget()
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
            ColorNoiseReduction = look.ColorNoiseReduction
            Grain = look.Grain
            Vignette = look.Vignette
            VignetteTransition = look.VignetteTransition
            VignetteFeather = look.VignetteFeather
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
            SchedulePreviewForCurrentTarget()
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
                ClearHistogramData()
                Return
            End If
            Dim previewSource = GetPreviewSource()
            If previewSource IsNot Nothing Then
                HistogramImage = ImageProcessor.BuildHistogramImage(previewSource, 240, 120)
                _curveHistogramCounts = ImageProcessor.BuildChannelHistogramCounts(previewSource)
            Else
                HistogramImage = ImageProcessor.BuildHistogramImage(RenderSourcePath, 240, 120)
                _curveHistogramCounts = ImageProcessor.BuildChannelHistogramCounts(RenderSourcePath)
            End If
            Me.RaisePropertyChanged(NameOf(ActiveCurveHistogramCounts))
        End Sub

        Private Sub ClearHistogramData()
            HistogramImage = Nothing
            _curveHistogramCounts = (New Integer(255) {}, New Integer(255) {}, New Integer(255) {}, New Integer(255) {})
            Me.RaisePropertyChanged(NameOf(ActiveCurveHistogramCounts))
        End Sub

        Public Sub RefreshLocalization()
            Me.RaisePropertyChanged(NameOf(CurrentFileName))
                Me.RaisePropertyChanged(NameOf(IsRawDeveloped))
                Me.RaisePropertyChanged(NameOf(RawFooterTooltip))
            Me.RaisePropertyChanged(NameOf(StatusText))
        End Sub

        Private Class PreviewRenderResult
            Implements IDisposable

            Public Property Preview As Bitmap
            Public Property Comparison As Bitmap
            ''' STUFE 2: die frisch gerenderte Szene (SKBitmap); der Erfolgs-Pfad uebernimmt die
            ''' Ownership (SetSceneBitmap) und setzt das Feld auf Nothing, sonst raeumt Dispose auf.
            Public Property SceneSk As SKBitmap

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
                SceneSk?.Dispose()
                SceneSk = Nothing
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
        Move
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
