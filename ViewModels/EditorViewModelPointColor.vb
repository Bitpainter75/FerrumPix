Imports System.Collections.ObjectModel
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    Partial Public Class EditorViewModel

        ''' <summary>Ein Farbfeld der Punktfarbe als Kreis in der Zeile über den Reglern.</summary>
        Public NotInheritable Class PointColorChip
            Inherits ViewModelBase

            Public Property Index As Integer
            Public Property Fill As Avalonia.Media.IBrush

            Private _isSelected As Boolean
            Public Property IsSelected As Boolean
                Get
                    Return _isSelected
                End Get
                Set(value As Boolean)
                    Me.RaiseAndSetIfChanged(_isSelected, value)
                End Set
            End Property
        End Class

        Private ReadOnly _pointColorChips As New ObservableCollection(Of PointColorChip)()
        Private _selectedPointColorIndex As Integer = -1
        Private _isPointColorTab As Boolean
        Private _isPointColorRangeVisible As Boolean

        ''' <summary>Die Farbfelder als Kreise, in der Reihenfolge des Rezepts.</summary>
        Public ReadOnly Property PointColorChips As ObservableCollection(Of PointColorChip)
            Get
                Return _pointColorChips
            End Get
        End Property

        ''' <summary>Zeigt der Farbmischer den Reiter Punktfarbe statt der acht Bänder? Reiner
        ''' Zustand der Oberfläche; im Rezept stehen beide nebeneinander.</summary>
        Public Property IsPointColorTab As Boolean
            Get
                Return _isPointColorTab
            End Get
            Set(value As Boolean)
                If _isPointColorTab = value Then Return
                _isPointColorTab = value
                Me.RaisePropertyChanged(NameOf(IsPointColorTab))
                Me.RaisePropertyChanged(NameOf(IsMixerTab))
                ' Die Anzeige des Bereichs gilt nur, solange der Reiter offen ist.
                If _isPointColorRangeVisible Then SchedulePreviewForCurrentTarget()
            End Set
        End Property

        Public Property IsMixerTab As Boolean
            Get
                Return Not _isPointColorTab
            End Get
            Set(value As Boolean)
                IsPointColorTab = Not value
            End Set
        End Property

        ''' <summary>Zeigt die Vorschau, was das gewählte Farbfeld trifft? Alles andere wird grau.
        ''' Nur in der Vorschau, nie im gespeicherten Bild.</summary>
        Public Property IsPointColorRangeVisible As Boolean
            Get
                Return _isPointColorRangeVisible
            End Get
            Set(value As Boolean)
                If _isPointColorRangeVisible = value Then Return
                _isPointColorRangeVisible = value
                Me.RaisePropertyChanged(NameOf(IsPointColorRangeVisible))
                SchedulePreviewForCurrentTarget()
            End Set
        End Property

        ''' <summary>Die Farbfelder für das Vorschau-Rezept: bei eingeschalteter Bereichsanzeige
        ''' trägt das gewählte Feld die Marke der Anzeige (<see cref="PointColorSwatch.Visualize"/>).
        ''' Sie steht nur hier und nie im gespeicherten Rezept, und weil sie im Text steht, geht sie
        ''' mit den Reglern dorthin, wo sie gerade gelten: aufs Foto, auf eine markierte Ebene oder
        ''' eine Korrekturebene.</summary>
        Private Function PointColorsForPreview() As String
            Dim index = If(_isPointColorTab AndAlso _isPointColorRangeVisible, _selectedPointColorIndex, -1)
            If index < 0 Then Return _pointColors
            Dim list = PointColorSwatch.ParseList(_pointColors)
            If index >= list.Count Then Return _pointColors
            list(index).Visualize = True
            Return PointColorSwatch.FormatList(list, includeVisualize:=True)
        End Function

        Public ReadOnly Property HasPointColors As Boolean
            Get
                Return _pointColorChips.Count > 0
            End Get
        End Property

        Public ReadOnly Property HasSelectedPointColor As Boolean
            Get
                Return _selectedPointColorIndex >= 0 AndAlso _selectedPointColorIndex < _pointColorChips.Count
            End Get
        End Property

        ''' <summary>Die aufgenommene Farbe des gewählten Felds und das, was es daraus macht.</summary>
        Public ReadOnly Property SelectedPointColorSourceBrush As Avalonia.Media.IBrush
            Get
                Dim s = SelectedPointColorSwatch()
                Return If(s Is Nothing, Nothing, BrushOf(s.SourceSrgb()))
            End Get
        End Property

        Public ReadOnly Property SelectedPointColorResultBrush As Avalonia.Media.IBrush
            Get
                Dim s = SelectedPointColorSwatch()
                Return If(s Is Nothing, Nothing, BrushOf(s.ResultSrgb()))
            End Get
        End Property

        ''' <summary>Die Spuren der drei Verschiebungsregler als Farbverlauf: was das gewählte Feld
        ''' aus der aufgenommenen Farbe macht, wenn dieser eine Regler von -100 bis +100 läuft und
        ''' die übrigen stehen bleiben. Der Farbton reicht dabei bewusst nur um 35 Grad zu jeder
        ''' Seite, wie beim Referenzprogramm; der Verlauf zeigt genau diesen Weg.</summary>
        Public ReadOnly Property PointColorHueTrackBrush As Avalonia.Media.IBrush
            Get
                Return SwatchTrack(Sub(s, v) s.HueShift = v)
            End Get
        End Property

        Public ReadOnly Property PointColorSaturationTrackBrush As Avalonia.Media.IBrush
            Get
                Return SwatchTrack(Sub(s, v) s.SaturationShift = v)
            End Get
        End Property

        Public ReadOnly Property PointColorLuminanceTrackBrush As Avalonia.Media.IBrush
            Get
                Return SwatchTrack(Sub(s, v) s.LuminanceShift = v)
            End Get
        End Property

        Private Function SwatchTrack(setValue As Action(Of PointColorSwatch, Double)) As Avalonia.Media.IBrush
            Dim swatch = SelectedPointColorSwatch()
            If swatch Is Nothing Then Return Nothing
            Dim brush As New Avalonia.Media.LinearGradientBrush With {
                .StartPoint = New Avalonia.RelativePoint(0, 0.5, Avalonia.RelativeUnit.Relative),
                .EndPoint = New Avalonia.RelativePoint(1, 0.5, Avalonia.RelativeUnit.Relative)}
            Const steps As Integer = 8
            For i = 0 To steps
                Dim probe = swatch.Clone()
                setValue(probe, -1.0 + 2.0 * i / steps)
                Dim rgb = probe.ResultSrgb()
                Dim toByte = Function(v As Double) CByte(Math.Max(0, Math.Min(255, Math.Round(v * 255.0))))
                brush.GradientStops.Add(New Avalonia.Media.GradientStop(
                    Avalonia.Media.Color.FromRgb(toByte(rgb(0)), toByte(rgb(1)), toByte(rgb(2))), i / CDbl(steps)))
            Next
            Return brush
        End Function

        ' ── Die Regler des gewählten Felds, am Regler -100 bis 100, der Bereich 0 bis 100 ──

        Public Property PointColorHueShift As Double
            Get
                Return SwatchValue(Function(s) s.HueShift * 100.0)
            End Get
            Set(value As Double)
                UpdateSelectedSwatch(NameOf(PointColorHueShift), Sub(s) s.HueShift = ClampUnit(value / 100.0))
            End Set
        End Property

        Public Property PointColorSaturationShift As Double
            Get
                Return SwatchValue(Function(s) s.SaturationShift * 100.0)
            End Get
            Set(value As Double)
                UpdateSelectedSwatch(NameOf(PointColorSaturationShift), Sub(s) s.SaturationShift = ClampUnit(value / 100.0))
            End Set
        End Property

        Public Property PointColorLuminanceShift As Double
            Get
                Return SwatchValue(Function(s) s.LuminanceShift * 100.0)
            End Get
            Set(value As Double)
                UpdateSelectedSwatch(NameOf(PointColorLuminanceShift), Sub(s) s.LuminanceShift = ClampUnit(value / 100.0))
            End Set
        End Property

        Public Property PointColorVariance As Double
            Get
                Return SwatchValue(Function(s) s.Variance * 100.0)
            End Get
            Set(value As Double)
                UpdateSelectedSwatch(NameOf(PointColorVariance), Sub(s) s.Variance = ClampUnit(value / 100.0))
            End Set
        End Property

        Public Property PointColorRange As Double
            Get
                Return SwatchValue(Function(s) s.Range * 100.0, 50.0)
            End Get
            Set(value As Double)
                UpdateSelectedSwatch(NameOf(PointColorRange), Sub(s) s.Range = Math.Max(0.0, Math.Min(1.0, value / 100.0)))
            End Set
        End Property

        Private _selectHslTabCommand As ICommand

        ''' <summary>Wechselt den Reiter des Farbmischers: "mixer" oder "point".</summary>
        Public ReadOnly Property SelectHslTabCommand As ICommand
            Get
                If _selectHslTabCommand Is Nothing Then
                    _selectHslTabCommand = ReactiveCommand.Create(Of String)(
                        Sub(tab) IsPointColorTab = String.Equals(tab, "point", StringComparison.Ordinal))
                End If
                Return _selectHslTabCommand
            End Get
        End Property

        Private _pickPointColorCommand As ICommand
        Private _selectPointColorCommand As ICommand
        Private _deletePointColorCommand As ICommand

        ''' <summary>Startet die Pipette: der nächste Klick ins Bild nimmt eine Farbe auf.</summary>
        Public ReadOnly Property PickPointColorCommand As ICommand
            Get
                If _pickPointColorCommand Is Nothing Then
                    _pickPointColorCommand = ReactiveCommand.Create(Sub() BeginColorPickAt(AddressOf AddPointColorFromPick, sampleRadius:=2))
                End If
                Return _pickPointColorCommand
            End Get
        End Property

        Public ReadOnly Property SelectPointColorCommand As ICommand
            Get
                If _selectPointColorCommand Is Nothing Then
                    _selectPointColorCommand = ReactiveCommand.Create(Of Integer)(Sub(index) SelectPointColor(index))
                End If
                Return _selectPointColorCommand
            End Get
        End Property

        ''' <summary>Löscht ein Farbfeld; ein eigener Schritt in der Historie.</summary>
        Public ReadOnly Property DeletePointColorCommand As ICommand
            Get
                If _deletePointColorCommand Is Nothing Then
                    _deletePointColorCommand = ReactiveCommand.Create(Of Integer)(Sub(index) DeletePointColor(index))
                End If
                Return _deletePointColorCommand
            End Get
        End Property

        Private Sub SelectPointColor(index As Integer)
            If index < 0 OrElse index >= _pointColorChips.Count OrElse index = _selectedPointColorIndex Then Return
            _selectedPointColorIndex = index
            RaisePointColorSelection()
            If _isPointColorTab AndAlso _isPointColorRangeVisible Then SchedulePreviewForCurrentTarget()
        End Sub

        Private Sub DeletePointColor(index As Integer)
            Dim list = PointColorSwatch.ParseList(_pointColors)
            If index < 0 OrElse index >= list.Count Then Return
            PushUndo(CombineHistoryLabel("Punktfarbe", "Farbfeld löschen"))
            list.RemoveAt(index)
            If _selectedPointColorIndex >= list.Count Then _selectedPointColorIndex = list.Count - 1
            SetPointColorsWithoutUndo(PointColorSwatch.FormatList(list))
        End Sub

        ''' <summary>Die Pipette hat geklickt: die Farbe an dieser Stelle wird ein neues Farbfeld.
        '''
        ''' <para>NACHGESCHLAGEN WIRD, WAS DIE PUNKTFARBE AN IHRER STELLE SIEHT, nicht der Bildpunkt
        ''' auf dem Schirm: in einem eigens gerechneten Vergleichsbild ohne Punktfarbe und ohne die
        ''' Stufen dahinter (Farbgradierung, Filter, LUT, Kanalmixer, Verlaufsumsetzung, Tontrennung,
        ''' Schwellenwert). Sonst nähme sie eine Farbe auf, die sie selbst nie zu sehen bekommt, und
        ''' das Feld träfe daneben. Derselbe Weg wie bei der Pipette des Weissabgleichs.</para></summary>
        Private Sub AddPointColorFromPick(picked As Avalonia.Media.Color, xPercent As Double, yPercent As Double)
            Dim list = PointColorSwatch.ParseList(_pointColors)
            If list.Count >= PointColorSwatch.MaxSwatches Then
                StatusText = LocalizationService.T("Die Punktfarbe nimmt höchstens acht Farben auf")
                Return
            End If
            Dim sample As SkiaSharp.SKColor?
            If IsObjectAdjustModeActive() Then
                ' DIE REGLER BEDIENEN EINE EBENE: dann zählt ihre Farbe, nicht die des Fotos
                ' darunter. Das Vergleichsbild unten rechnet nur das Foto; auf einer Ebene kam so
                ' die Farbe des Hintergrunds an (Nutzerbefund). Genommen wird die angezeigte Farbe
                ' an der Stelle, also die Ebene samt ihren Reglern - für ein neues Farbfeld trifft
                ' das, solange die Ebene dort noch keine starke Farbbearbeitung trägt.
                sample = New SkiaSharp.SKColor(picked.R, picked.G, picked.B)
            Else
                Dim probe = GetCurrentAdjustments(forPreview:=True)
                If probe Is Nothing Then Return
                NeutralizeStagesAfterPointColor(probe)
                sample = SampleProbePixel(probe, xPercent, yPercent)
            End If
            If Not sample.HasValue Then
                StatusText = LocalizationService.T("Diese Stelle lässt sich nicht messen")
                Return
            End If
            Dim swatch = PointColorSwatch.SampleFromSrgb(sample.Value.Red / 255.0, sample.Value.Green / 255.0, sample.Value.Blue / 255.0)
            If swatch Is Nothing Then
                StatusText = LocalizationService.T("Diese Farbe ist zu blass oder zu dunkel, bitte eine kräftigere Stelle anklicken")
                Return
            End If
            ' Dieselbe Farbe zweimal ergäbe zwei Felder, die sich gegenseitig verstärken.
            Dim near = list.Any(Function(s) Math.Min(Math.Abs(s.Hue - swatch.Hue), 6.0 - Math.Abs(s.Hue - swatch.Hue)) < 0.05 AndAlso
                                            Math.Abs(s.Saturation - swatch.Saturation) < 0.02 AndAlso
                                            Math.Abs(s.Value - swatch.Value) < 0.02)
            If near Then
                StatusText = LocalizationService.T("Für diese Farbe gibt es schon ein Farbfeld")
                Return
            End If
            PushUndo(CombineHistoryLabel("Punktfarbe", "Farbe aufnehmen"))
            list.Add(swatch)
            _selectedPointColorIndex = list.Count - 1
            SetPointColorsWithoutUndo(PointColorSwatch.FormatList(list))
            StatusText = LocalizationService.T("Farbe aufgenommen")
        End Sub

        ''' <summary>Die Stufen hinter der Punktfarbe auf neutral, für das Vergleichsbild der Pipette.</summary>
        Private Shared Sub NeutralizeStagesAfterPointColor(probe As ImageAdjustments)
            Dim neutral = New ImageAdjustments()
            probe.PointColors = ""
            For Each p In ImageAdjustments.PixelAdjustmentProperties()
                If p.Name.StartsWith("ColorGrade", StringComparison.Ordinal) OrElse
                   p.Name.StartsWith("ChannelMixer", StringComparison.Ordinal) OrElse
                   p.Name.StartsWith("GradientMap", StringComparison.Ordinal) OrElse
                   p.Name.StartsWith("Filter", StringComparison.Ordinal) OrElse
                   p.Name.StartsWith("Lut", StringComparison.Ordinal) OrElse
                   p.Name = NameOf(ImageAdjustments.PosterizeLevels) OrElse
                   p.Name = NameOf(ImageAdjustments.ThresholdLevel) Then
                    p.SetValue(probe, p.GetValue(neutral))
                End If
            Next
        End Sub

        Private Function SelectedPointColorSwatch() As PointColorSwatch
            Dim list = PointColorSwatch.ParseList(_pointColors)
            Return If(_selectedPointColorIndex >= 0 AndAlso _selectedPointColorIndex < list.Count, list(_selectedPointColorIndex), Nothing)
        End Function

        Private Function SwatchValue(read As Func(Of PointColorSwatch, Double), Optional fallback As Double = 0.0) As Double
            Dim s = SelectedPointColorSwatch()
            Return If(s Is Nothing, fallback, Math.Round(read(s), 4))
        End Function

        ''' <summary>Ändert das gewählte Feld. Ein Regelzug ist EIN Schritt in der Historie, wie bei
        ''' jedem anderen Regler: CaptureUndoState fasst gleiche Namen zusammen.</summary>
        Private Sub UpdateSelectedSwatch(propertyName As String, change As Action(Of PointColorSwatch))
            Dim list = PointColorSwatch.ParseList(_pointColors)
            If _selectedPointColorIndex < 0 OrElse _selectedPointColorIndex >= list.Count Then Return
            Dim swatch = list(_selectedPointColorIndex)
            Dim before = PointColorSwatch.FormatList({swatch})
            change(swatch)
            If String.Equals(before, PointColorSwatch.FormatList({swatch}), StringComparison.Ordinal) Then Return
            CaptureUndoState(propertyName)
            SetPointColorsWithoutUndo(PointColorSwatch.FormatList(list))
        End Sub

        Private Sub SetPointColorsWithoutUndo(text As String)
            _pointColors = If(text, "")
            Me.RaisePropertyChanged(NameOf(PointColors))
            RefreshPointColorChips()
            RaiseResetButtonStateChanged()
            SchedulePreviewForCurrentTarget()
        End Sub

        ''' <summary>Baut die Kreise aus dem Rezept neu auf und meldet die Regler des gewählten
        ''' Felds. Läuft nach jeder Änderung des Rezepttexts, auch nach Rückgängig und Laden.</summary>
        Private Sub RefreshPointColorChips()
            Dim list = PointColorSwatch.ParseList(_pointColors)
            If _selectedPointColorIndex >= list.Count Then _selectedPointColorIndex = list.Count - 1
            If _selectedPointColorIndex < 0 AndAlso list.Count > 0 Then _selectedPointColorIndex = 0
            _pointColorChips.Clear()
            For i = 0 To list.Count - 1
                _pointColorChips.Add(New PointColorChip With {
                    .Index = i,
                    .Fill = BrushOf(list(i).SourceSrgb()),
                    .IsSelected = i = _selectedPointColorIndex})
            Next
            Me.RaisePropertyChanged(NameOf(HasPointColors))
            RaisePointColorSelection()
        End Sub

        Private Sub RaisePointColorSelection()
            For Each chip In _pointColorChips
                chip.IsSelected = chip.Index = _selectedPointColorIndex
            Next
            For Each n In {NameOf(HasSelectedPointColor), NameOf(SelectedPointColorSourceBrush), NameOf(SelectedPointColorResultBrush),
                           NameOf(PointColorHueShift), NameOf(PointColorSaturationShift), NameOf(PointColorLuminanceShift),
                           NameOf(PointColorVariance), NameOf(PointColorRange),
                           NameOf(PointColorHueTrackBrush), NameOf(PointColorSaturationTrackBrush), NameOf(PointColorLuminanceTrackBrush)}
                Me.RaisePropertyChanged(n)
            Next
        End Sub

        Private Shared Function BrushOf(rgb As Double()) As Avalonia.Media.IBrush
            Dim toByte = Function(v As Double) CByte(Math.Max(0, Math.Min(255, Math.Round(v * 255.0))))
            Return New Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(toByte(rgb(0)), toByte(rgb(1)), toByte(rgb(2))))
        End Function

        Private Shared Function ClampUnit(v As Double) As Double
            Return Math.Max(-1.0, Math.Min(1.0, v))
        End Function

    End Class

End Namespace
