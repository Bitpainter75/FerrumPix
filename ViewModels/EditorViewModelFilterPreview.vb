Imports System.Collections.ObjectModel
Imports System.Reflection
Imports System.Threading
Imports System.Windows.Input
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports FerrumPix.Services
Imports ReactiveUI
Imports SkiaSharp

Namespace ViewModels

    ''' <summary>Eine Kachel im Werkzeug Filter: der Filter, sein angezeigter Name und eine kleine
    ''' Vorschau des Bildes mit genau dem Rezept, das ein Klick auf die Kachel setzt.</summary>
    Public NotInheritable Class FilterPreviewTile
        Inherits ReactiveObject

        Public Sub New(id As String)
            Me.Id = id
            DisplayName = LocalizationService.T(id)
        End Sub

        ''' <summary>Der interne Name des Filters, wie er im Rezept steht.</summary>
        Public ReadOnly Property Id As String

        Private _displayName As String
        Public Property DisplayName As String
            Get
                Return _displayName
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_displayName, value)
            End Set
        End Property

        Private _image As Bitmap
        Public Property Image As Bitmap
            Get
                Return _image
            End Get
            Set(value As Bitmap)
                Me.RaiseAndSetIfChanged(_image, value)
            End Set
        End Property

        Private _isActive As Boolean
        Public Property IsActive As Boolean
            Get
                Return _isActive
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isActive, value)
            End Set
        End Property
    End Class

    Partial Public Class EditorViewModel

        ''' <summary>Die Filter als Kacheln mit Vorschau.
        '''
        ''' WAS DIE KACHEL ZEIGT, IST WAS DER KLICK SETZT. Ein Filter ersetzt den Look: der Klick
        ''' nimmt Licht, Farbe, Details, Effekte, Farbmischer, Farbgradierung, Kalibrierung, Kurven und
        ''' LUT zurück (<see cref="ResetFilterInternal"/>) und legt dann den Filter auf
        ''' (<see cref="ApplyBuiltInLook"/>, <see cref="ApplyExclusiveFilterPreset"/>). Die Kachel
        ''' rechnet dasselbe als Rezept nach (<see cref="FilterPreviewAdjustments"/>), ohne den Editor
        ''' anzufassen; Geometrie, Ebenen und Auswahl bleiben, wie sie sind. Dass beide Wege dasselbe
        ''' Rezept ergeben, hält eine Prüfung im Prüfstand fest: wer am Klick etwas ändert, muss es
        ''' hier nachziehen.
        '''
        ''' WANN GERECHNET WIRD. Nur wenn die Kacheln gewählt sind, das Werkzeug Filter steht (nicht
        ''' unter Alle Anpassungen, nicht in einem anderen Werkzeug) und
        ''' seine Gruppe aufgeklappt ist, und erst, wenn das große Bild fertig ist: die Kacheln
        ''' sollen den Regler nicht bremsen. Gerechnet wird auf einer kleinen Fassung des
        ''' Arbeitsbilds, ein Filter nach dem anderen, mit den Objekten; jede neue Änderung bricht
        ''' den laufenden Durchgang ab.
        '''
        ''' NICHT BEI EINER LOKALEN ANPASSUNG. Bedienen die Regler ein Objekt oder eine Auswahl,
        ''' legt der Klick den Filter dort an, die Kachel zeigte aber das ganze Bild. Das Panel
        ''' zeigt dann die Knöpfe (<see cref="FilterPreviewTilesVisible"/>).</summary>
        Private ReadOnly _filterPreviewTiles As New ObservableCollection(Of FilterPreviewTile)()
        Private _showFilterPreviewTiles As Boolean
        Private _filterPanelExpanded As Boolean = True
        Private _filterPreviewTimer As DispatcherTimer
        Private _filterPreviewCts As CancellationTokenSource
        Private _filterPreviewRenderedKey As String = ""
        Private ReadOnly _filterPreviewRenderGate As New SemaphoreSlim(1, 1)
        ''' Die kleine Fassung des Arbeitsbilds und wofür sie gilt. Nur innerhalb des Durchgangs
        ''' gelesen und geschrieben, der über _filterPreviewRenderGate einzeln läuft.
        Private _filterPreviewSource As SKBitmap
        Private _filterPreviewSourceKey As String = ""
        ''' Angehalten zwischen „Nicht speichern" und dem nächsten Bild: das Verwerfen setzt die
        ''' Regler zurück und rendert das alte Bild neu, ein Durchgang dafür wäre vergeudet.
        Private _filterPreviewsHeld As Boolean

        ''' Längste Kante der Kacheln in Bildpunkten: doppelt so groß wie die Kachel am Schirm,
        ''' damit sie auf einem Schirm mit doppelter Dichte scharf bleibt.
        Private Const FilterPreviewMaxDimension As Integer = 320
        ''' Wartezeit nach der letzten Änderung, bevor die Kacheln gerechnet werden.
        Private Const FilterPreviewDelayMs As Integer = 350

        Public ReadOnly Property FilterPreviewTiles As ObservableCollection(Of FilterPreviewTile)
            Get
                Return _filterPreviewTiles
            End Get
        End Property

        ''' <summary>Zeigt das Werkzeug Filter Kacheln mit Vorschau statt Knöpfen?</summary>
        Public Property ShowFilterPreviewTiles As Boolean
            Get
                Return _showFilterPreviewTiles
            End Get
            Set(value As Boolean)
                If _showFilterPreviewTiles = value Then Return
                _showFilterPreviewTiles = value
                Me.RaisePropertyChanged(NameOf(ShowFilterPreviewTiles))
                Me.RaisePropertyChanged(NameOf(FilterPreviewTilesVisible))
                AppSettingsService.Update(Sub(s) s.FilterPreviewTiles = value)
                If value Then
                    ScheduleFilterPreviews()
                Else
                    CancelFilterPreviews()
                End If
            End Set
        End Property

        ''' <summary>Ist die Gruppe Filter aufgeklappt? Gesetzt von der Ansicht, die den Zustand
        ''' selbst merkt (ExpanderState).</summary>
        Public Property FilterPanelExpanded As Boolean
            Get
                Return _filterPanelExpanded
            End Get
            Set(value As Boolean)
                If _filterPanelExpanded = value Then Return
                _filterPanelExpanded = value
                If value Then ScheduleFilterPreviews()
            End Set
        End Property

        ''' <summary>Stehen die Kacheln im Panel? Nur gewählt und ohne lokale Anpassung, sonst die Knöpfe.</summary>
        Public ReadOnly Property FilterPreviewTilesVisible As Boolean
            Get
                Return _showFilterPreviewTiles AndAlso _currentTool = EditorTool.Filters AndAlso
                       Not IsObjectAdjustModeActive() AndAlso Not IsSelectionAdjustModeActive()
            End Get
        End Property

        ''' <summary>Steht das Werkzeug Filter? Nur dort gibt es Kacheln und ihren Umschalter; unter
        ''' Alle Anpassungen zeigt die Gruppe immer die Knöpfe.</summary>
        Public ReadOnly Property IsFilterToolActive As Boolean
            Get
                Return _currentTool = EditorTool.Filters
            End Get
        End Property

        Public ReadOnly Property ToggleFilterPreviewTilesCommand As ICommand
        Public ReadOnly Property ApplyFilterTileCommand As ICommand

        ''' <summary>Aus dem Konstruktor: Kacheln anlegen, gemerkte Wahl lesen, Befehle bauen.</summary>
        Private Sub InitializeFilterPreviews()
            For Each name In ImageAdjustments.FilterPresetNames
                If String.Equals(name, "Keine", StringComparison.Ordinal) Then Continue For
                _filterPreviewTiles.Add(New FilterPreviewTile(name))
            Next
            _showFilterPreviewTiles = AppSettingsService.Load().FilterPreviewTiles
            AddHandler LocalizationService.LanguageChanged,
                Sub()
                    For Each tile In _filterPreviewTiles
                        tile.DisplayName = LocalizationService.T(tile.Id)
                    Next
                End Sub
            AddHandler Me.PropertyChanged,
                Sub(sender, e)
                    Select Case e.PropertyName
                        Case NameOf(LastAppliedFilterPresetName)
                            SyncFilterTilesActive()
                        Case NameOf(ShowFilterAdjustments)
                            ' Kommt mit jedem Werkzeugwechsel. Außerhalb des Werkzeugs Filter wird nichts
                            ' gerechnet, ein laufender Durchgang bricht ab.
                            Me.RaisePropertyChanged(NameOf(FilterPreviewTilesVisible))
                            Me.RaisePropertyChanged(NameOf(IsFilterToolActive))
                            If FilterPreviewsWanted() Then ScheduleFilterPreviews() Else CancelFilterPreviews()
                        Case NameOf(CurrentImagePath)
                            ClearFilterPreviewImages()
                            Me.RaisePropertyChanged(NameOf(FilterPreviewTilesVisible))
                        Case NameOf(IsAdjustingObject), NameOf(IsAdjustingSelection)
                            Me.RaisePropertyChanged(NameOf(FilterPreviewTilesVisible))
                            ScheduleFilterPreviews()
                    End Select
                End Sub
            SyncFilterTilesActive()
        End Sub

        Private Function CreateToggleFilterPreviewTilesCommand() As ICommand
            Return ReactiveCommand.Create(Sub() ShowFilterPreviewTiles = Not ShowFilterPreviewTiles)
        End Function

        ''' <summary>Der Klick auf eine Kachel: dasselbe wie der Knopf mit dem Namen.</summary>
        Private Function CreateApplyFilterTileCommand() As ICommand
            Return ReactiveCommand.Create(Of FilterPreviewTile)(
                Sub(tile)
                    If tile Is Nothing Then Return
                    ' Direkt statt über die Befehle der Knöpfe: deren CanExecute steht nach einem
                    ' Klick kurz auf falsch, bis ReactiveUI das Ende meldet, und ein schneller
                    ' zweiter Klick fiele still unter den Tisch.
                    PushUndo(LocalizationService.T("Filter"))
                    If ImageAdjustments.IsBuiltInLook(tile.Id) Then
                        ApplyBuiltInLook(tile.Id)
                    Else
                        ApplyExclusiveFilterPreset(tile.Id)
                    End If
                    BeginLookRenderWait()
                End Sub)
        End Function

        Private Sub SyncFilterTilesActive()
            Dim active = LastAppliedFilterPresetName
            For Each tile In _filterPreviewTiles
                tile.IsActive = String.Equals(tile.Id, active, StringComparison.Ordinal)
            Next
        End Sub

        ''' <summary>Werden die Kacheln gerade gebraucht?</summary>
        Private Function FilterPreviewsWanted() As Boolean
            Return _showFilterPreviewTiles AndAlso Not _filterPreviewsHeld AndAlso _filterPanelExpanded AndAlso _currentTool = EditorTool.Filters AndAlso
                   Not String.IsNullOrEmpty(RenderSourcePath) AndAlso
                   Not IsObjectAdjustModeActive() AndAlso Not IsSelectionAdjustModeActive()
        End Function

        ''' <summary>Woran die Kacheln hängen: Bild, Arbeitsbild, jede Änderung am Rezept.</summary>
        Private Function FilterPreviewKey() As String
            Return String.Concat(RenderSourcePath, "|", _workingImage.Version.ToString(Globalization.CultureInfo.InvariantCulture),
                                 "|", _previewChangeId.ToString(Globalization.CultureInfo.InvariantCulture),
                                 "|", _previewSourceSwapId.ToString(Globalization.CultureInfo.InvariantCulture),
                                 "|", If(_pixelLayerHidden, "u", "b"))
        End Function

        ''' <summary>Gerufen, wenn das große Bild fertig ist, und wenn die Kacheln sichtbar werden.
        ''' Wartet kurz, damit eine Folge von Änderungen nur einen Durchgang auslöst.</summary>
        Private Sub ScheduleFilterPreviews()
            If Not FilterPreviewsWanted() Then Return
            If String.Equals(FilterPreviewKey(), _filterPreviewRenderedKey, StringComparison.Ordinal) Then Return
            If _filterPreviewTimer Is Nothing Then
                _filterPreviewTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(FilterPreviewDelayMs)}
                AddHandler _filterPreviewTimer.Tick, Sub() OnFilterPreviewTimer()
            End If
            _filterPreviewTimer.Stop()
            _filterPreviewTimer.Start()
        End Sub

        ''' <summary>Aus dem Ende eines Renders, möglicherweise nicht auf dem Anzeigefaden.</summary>
        Private Sub NoteMainPreviewSettledForFilterTiles()
            If Not _showFilterPreviewTiles Then Return
            Dispatcher.UIThread.Post(Sub() ScheduleFilterPreviews())
        End Sub

        Private Sub CancelFilterPreviews()
            _filterPreviewTimer?.Stop()
            _filterPreviewCts?.Cancel()
            _filterPreviewRenderedKey = ""
        End Sub

        ''' <summary>Aus dem Verlassen-Dialog bei „Nicht speichern": Kacheln leeren und anhalten,
        ''' bis das nächste Bild geladen ist (<see cref="ReleaseFilterPreviewHold"/>).</summary>
        Private Sub HoldFilterPreviewsUntilNextImage()
            _filterPreviewsHeld = True
            ClearFilterPreviewImages()
        End Sub

        ''' <summary>Aus dem Laden eines Bildes, auch wenn es dasselbe ist: dann meldet
        ''' CurrentImagePath keine Änderung, und die Kacheln blieben sonst angehalten.</summary>
        Private Sub ReleaseFilterPreviewHold()
            If Not _filterPreviewsHeld Then Return
            _filterPreviewsHeld = False
            ClearFilterPreviewImages()
        End Sub

        ''' <summary>Beim Bildwechsel: die Kacheln zeigen sonst das vorige Bild, bis der neue
        ''' Durchgang sie eine nach der anderen ersetzt, und der kommt erst nach dem großen Bild.
        ''' Lieber leer als falsch.</summary>
        Private Sub ClearFilterPreviewImages()
            CancelFilterPreviews()
            For Each tile In _filterPreviewTiles
                Dim old = tile.Image
                If old Is Nothing Then Continue For
                tile.Image = Nothing
                old.Dispose()
            Next
        End Sub

        Private Sub OnFilterPreviewTimer()
            _filterPreviewTimer.Stop()
            If Not FilterPreviewsWanted() Then Return
            ' Das große Bild hat Vorrang: läuft es noch, kommt der Aufruf aus seinem Ende wieder.
            If _previewPending OrElse Volatile.Read(_activePreviewRenders) <> 0 Then Return
            Dim key = FilterPreviewKey()
            If String.Equals(key, _filterPreviewRenderedKey, StringComparison.Ordinal) Then Return
            _filterPreviewRenderedKey = key

            _filterPreviewCts?.Cancel()
            Dim cts = New CancellationTokenSource()
            _filterPreviewCts = cts

            ' Die Rezepte entstehen hier auf dem Anzeigefaden, aus dem Zustand des Editors.
            ' Mit den Objekten: der Klick lässt sie stehen, die Kachel zeigt sie also auch.
            Dim current = BuildAdjustmentsFromFields(forPreview:=True)
            Dim jobs = _filterPreviewTiles.Select(Function(t) (Tile:=t, Adjustments:=FilterPreviewAdjustments(current, t.Id))).ToList()
            Dim path = RenderSourcePath
            Dim usesUnbaked = _pixelLayerHidden
            Dim sourceKey = String.Concat(path, "|", _workingImage.Version.ToString(Globalization.CultureInfo.InvariantCulture),
                                          "|", If(usesUnbaked, "u", "b"))
            Dim ignored = RenderFilterPreviewsAsync(jobs, path, usesUnbaked, sourceKey, cts.Token)
        End Sub

        Private Async Function RenderFilterPreviewsAsync(jobs As List(Of (Tile As FilterPreviewTile, Adjustments As ImageAdjustments)),
                                                         path As String, usesUnbaked As Boolean, sourceKey As String,
                                                         token As CancellationToken) As Task
            Try
                Await _filterPreviewRenderGate.WaitAsync(token)
            Catch ex As OperationCanceledException
                Return
            End Try
            Try
                Await Task.Run(
                    Sub()
                        If _filterPreviewSource Is Nothing OrElse
                           Not String.Equals(_filterPreviewSourceKey, sourceKey, StringComparison.Ordinal) Then
                            ' Wie die kleine Quelle des Reglerzugs (BuildSliderPreviewSourceAsync).
                            Dim built = If(usesUnbaked,
                                           ImageProcessor.LoadPreviewSource(path, FilterPreviewMaxDimension),
                                           _workingImage.RenderDownscale(FilterPreviewMaxDimension))
                            If built Is Nothing Then Return
                            _filterPreviewSource?.Dispose()
                            _filterPreviewSource = built
                            _filterPreviewSourceKey = sourceKey
                        End If
                        For Each job In jobs
                            If token.IsCancellationRequested Then Return
                            Dim rendered As SKBitmap = Nothing
                            Try
                                rendered = ImageProcessor.RenderPreviewSkBitmap(_filterPreviewSource, job.Adjustments)
                                If rendered Is Nothing Then Continue For
                                Dim image = ImageProcessor.ToAvaloniaBitmap(rendered)
                                Dim tile = job.Tile
                                Dispatcher.UIThread.Post(
                                    Sub()
                                        If token.IsCancellationRequested Then
                                            image.Dispose()
                                            Return
                                        End If
                                        Dim old = tile.Image
                                        tile.Image = image
                                        old?.Dispose()
                                    End Sub)
                            Finally
                                rendered?.Dispose()
                            End Try
                        Next
                    End Sub, token)
            Catch ex As OperationCanceledException
                ' Eine neue Änderung kam dazwischen; ihr Durchgang folgt.
            Catch ex As Exception
                DiagnosticLogService.LogException("EditorViewModel.RenderFilterPreviews", ex)
            Finally
                _filterPreviewRenderGate.Release()
            End Try
        End Function

        ''' Was der Klick auf einen Filter zurücknimmt (<see cref="ResetFilterInternal"/>), als Felder
        ''' des Rezepts. Belichtung und Kalibrierung gehen dabei auf die Startwerte des Bildes, nicht
        ''' auf null; das setzt <see cref="FilterPreviewAdjustments"/> eigens.
        Private Shared ReadOnly FilterResetPropertyNames As String() = BuildFilterResetPropertyNames()

        ''' Was ein eingebauter Look in die Regler schreibt (<see cref="ApplyLookAdjustments"/>), ohne
        ''' den Weißabgleich, der dort eigene Wege geht.
        Private Shared ReadOnly LookPropertyNames As String() = BuildLookPropertyNames()

        Private Shared Function HslPropertyNames() As IEnumerable(Of String)
            Return From band In {"Red", "Orange", "Yellow", "Green", "Aqua", "Blue", "Purple", "Magenta"}
                   From part In {"Hue", "Saturation", "Luminance"}
                   Select band & part
        End Function

        Private Shared Function BuildFilterResetPropertyNames() As String()
            Dim names As New List(Of String) From {
                "Brightness", "Contrast", "Highlights", "ShadowsLevel", "Whites", "Blacks", "Exposure",
                "WhiteBalanceMode", "Temperature", "Tint", "WhiteBalanceKelvin", "WhiteBalanceKelvinTint",
                "Vibrance", "Saturation",
                "Clarity", "Sharpness", "SharpenRadius", "SharpenDetail", "SharpenMasking", "SharpenMethod",
                "NoiseReduction", "NoiseReductionDetail", "Blur", "ColorNoiseReduction", "FarbrauschGrob",
                "ColorNoiseCoarseScale", "NoiseReductionMethod", "Haze", "AddNoise", "Structure", "Glow",
                "Vignette", "VignetteTransition", "VignetteRoundness", "VignetteFeather", "VignetteCenterX",
                "VignetteCenterY", "VignetteStyle",
                "Grain", "GrainSize", "GrainFrequency", "GrainColor", "ColorNoiseAdd", "DustScratches",
                "PointColors", "LutPath", "LutStrength", "FilterPreset", "FilterStrength"
            }
            names.AddRange(HslPropertyNames())
            Dim groups = {"ColorGrade", "Calibration", "Curve", "ChannelMixer", "GradientMap", "Posterize", "Threshold", "TiltShift"}
            For Each p In GetType(ImageAdjustments).GetProperties(BindingFlags.Public Or BindingFlags.Instance)
                If p.CanRead AndAlso p.CanWrite AndAlso groups.Any(Function(g) p.Name.StartsWith(g, StringComparison.Ordinal)) Then names.Add(p.Name)
            Next
            Return names.Distinct(StringComparer.Ordinal).ToArray()
        End Function

        Private Shared Function BuildLookPropertyNames() As String()
            Dim names As New List(Of String) From {
                "Exposure", "Brightness", "Contrast", "Highlights", "ShadowsLevel", "Whites", "Blacks",
                "Clarity", "Structure", "Haze", "Vibrance", "Saturation",
                "Sharpness", "SharpenRadius", "SharpenDetail", "SharpenMasking",
                "NoiseReduction", "NoiseReductionDetail", "Blur", "ColorNoiseReduction", "ColorNoiseAdd",
                "Grain", "GrainSize", "GrainFrequency", "GrainColor",
                "Vignette", "VignetteTransition", "VignetteFeather", "VignetteRoundness", "VignetteStyle",
                "PointColors",
                "CalibrationRedHue", "CalibrationRedSaturation", "CalibrationGreenHue", "CalibrationGreenSaturation",
                "CalibrationBlueHue", "CalibrationBlueSaturation", "CalibrationShadowTint",
                "CurveRgbPoints", "CurveRedPoints", "CurveGreenPoints", "CurveBluePoints"
            }
            names.AddRange(HslPropertyNames())
            For Each p In GetType(ImageAdjustments).GetProperties(BindingFlags.Public Or BindingFlags.Instance)
                If p.CanRead AndAlso p.CanWrite AndAlso p.Name.StartsWith("ColorGrade", StringComparison.Ordinal) Then names.Add(p.Name)
            Next
            Return names.Distinct(StringComparer.Ordinal).ToArray()
        End Function

        Private Shared Sub CopyProperties(target As ImageAdjustments, source As ImageAdjustments, names As IEnumerable(Of String))
            Dim type = GetType(ImageAdjustments)
            For Each name In names
                Dim p = type.GetProperty(name, BindingFlags.Public Or BindingFlags.Instance)
                If p Is Nothing OrElse Not p.CanWrite Then Continue For
                p.SetValue(target, p.GetValue(source))
            Next
        End Sub

        ''' <summary>Das Rezept nach einem Klick auf den Filter <paramref name="id"/>, ausgehend von
        ''' <paramref name="current"/>. Rechnet nur, ändert nichts am Editor.</summary>
        Private Function FilterPreviewAdjustments(current As ImageAdjustments, id As String,
                                                  Optional lookStrength As Double = 50) As ImageAdjustments
            Dim adj = current.Clone()

            ' 1. Wie ResetFilterInternal.
            CopyProperties(adj, FilterResetValues(), FilterResetPropertyNames)
            If adj.MaskedAdjustmentLayers IsNot Nothing Then
                adj.MaskedAdjustmentLayers = adj.MaskedAdjustmentLayers.Where(Function(l) l Is Nothing OrElse Not l.FromPreset).ToList()
            End If
            Return ApplyFilterToResetAdjustments(adj, id, lookStrength)
        End Function

        ''' <summary>Die Werte, auf die ein Filterklick die Felder in
        ''' <see cref="FilterResetPropertyNames"/> setzt: neutral, Belichtung und Kalibrierung auf den
        ''' Startwerten des Bildes.</summary>
        Private Function FilterResetValues() As ImageAdjustments
            Dim reset = New ImageAdjustments()
            reset.Exposure = ImageAdjustments.ForUneditedRaw(
                If(RawPreviewService.IsSupportedRaw(RenderSourcePath), RenderSourcePath, Nothing),
                forNegative:=ImageNegativeEnabled()).Exposure
            Dim calibration = CalibrationStartValues()
            reset.CalibrationRedHue = calibration.CalibrationRedHue
            reset.CalibrationRedSaturation = calibration.CalibrationRedSaturation
            reset.CalibrationGreenHue = calibration.CalibrationGreenHue
            reset.CalibrationGreenSaturation = calibration.CalibrationGreenSaturation
            reset.CalibrationBlueHue = calibration.CalibrationBlueHue
            reset.CalibrationBlueSaturation = calibration.CalibrationBlueSaturation
            reset.CalibrationShadowTint = calibration.CalibrationShadowTint
            Return reset
        End Function

        ''' <summary>Schritt 2 eines Filterklicks auf ein schon zurückgesetztes Rezept.</summary>
        Private Function ApplyFilterToResetAdjustments(adj As ImageAdjustments, id As String,
                                                       Optional lookStrength As Double = 50) As ImageAdjustments
            ' 2a. Ein Filter mit eigener Matrix (ApplyExclusiveFilterPreset).
            If Not ImageAdjustments.IsBuiltInLook(id) Then
                adj.FilterPreset = id
                adj.FilterStrength = CSng(ImageAdjustments.DefaultFilterStrength(id))
                Return adj
            End If

            ' 2b. Ein eingebauter Look bei der Stärke, mit der der Klick ihn setzt (ApplyBuiltInLook),
            ' oder bei einer anderen, wenn der Zurücksetzer den Stand eines geladenen Looks nachrechnet.
            Dim look = ImageAdjustments.CreateBuiltInLook(id, lookStrength)
            If look Is Nothing Then Return adj
            CopyProperties(adj, look, LookPropertyNames)
            ' Der Weißabgleich wie in ApplyLookAdjustments, nach dem Zurücksetzen (Kelvin 0).
            If look.WhiteBalanceKelvin >= WhiteBalanceAdaptation.MinKelvin Then
                adj.WhiteBalanceKelvin = look.WhiteBalanceKelvin
                adj.WhiteBalanceKelvinTint = look.WhiteBalanceKelvinTint
                adj.Temperature = 0
                adj.Tint = 0
            ElseIf HasAbsoluteWhiteBalance Then
                If look.Temperature = 0.0F Then
                    adj.WhiteBalanceKelvin = 0.0
                    adj.WhiteBalanceKelvinTint = look.Tint
                Else
                    adj.WhiteBalanceKelvin = MiredShiftToKelvin(look.Temperature)
                    adj.WhiteBalanceKelvinTint = CaptureTint() + look.Tint
                End If
                adj.Temperature = 0
                adj.Tint = 0
            Else
                adj.WhiteBalanceKelvin = 0
                adj.WhiteBalanceKelvinTint = 0
                adj.Temperature = look.Temperature
                adj.Tint = look.Tint
            End If
            adj.FilterPreset = id
            adj.FilterStrength = CSng(lookStrength)
            Return adj
        End Function

    End Class

End Namespace
