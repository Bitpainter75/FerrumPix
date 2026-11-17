Imports System
Imports System.Windows.Input
Imports Avalonia.Media
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels
    Partial Public Class EditorViewModel

        Private _siliconBonkEnabled As Boolean = False
        Public Property SiliconBonkEnabled As Boolean
            Get
                Return _siliconBonkEnabled
            End Get
            Set(value As Boolean)
                If _siliconBonkEnabled = value Then Return
                CaptureUndoState(NameOf(SiliconBonkEnabled))
                _siliconBonkEnabled = value
                Me.RaisePropertyChanged(NameOf(SiliconBonkEnabled))
                RaiseResetButtonStateChanged()
                SchedulePreviewForCurrentTarget()
            End Set
        End Property

        Private _siliconBonkHue As Double = 45
        Public Property SiliconBonkHue As Double
            Get
                Return _siliconBonkHue
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_siliconBonkHue, Math.Max(0, Math.Min(360, value)), NameOf(SiliconBonkHue))
                Me.RaisePropertyChanged(NameOf(SiliconBonkLumaTrack))
            End Set
        End Property

        Public ReadOnly Property SiliconBonkLumaTrack As IBrush
            Get
                Return Controls.SliderTracks.LuminanceOf(SiliconBonkHue)
            End Get
        End Property

        Private _siliconBonkLuma As Double = 0
        Public Property SiliconBonkLuma As Double
            Get
                Return _siliconBonkLuma
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_siliconBonkLuma, Math.Max(-100, Math.Min(100, value)), NameOf(SiliconBonkLuma))
            End Set
        End Property

        Private _siliconBonkSaturation As Double = 0
        Public Property SiliconBonkSaturation As Double
            Get
                Return _siliconBonkSaturation
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_siliconBonkSaturation, Math.Max(0, Math.Min(200, value)), NameOf(SiliconBonkSaturation))
            End Set
        End Property

        Private _siliconBonkMidpoint As Double = 0
        Public Property SiliconBonkMidpoint As Double
            Get
                Return _siliconBonkMidpoint
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_siliconBonkMidpoint, Math.Max(-100, Math.Min(100, value)), NameOf(SiliconBonkMidpoint))
            End Set
        End Property

        Private _siliconBonkContrast As Double = 0
        Public Property SiliconBonkContrast As Double
            Get
                Return _siliconBonkContrast
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_siliconBonkContrast, Math.Max(-50, Math.Min(200, value)), NameOf(SiliconBonkContrast))
            End Set
        End Property

        Private _siliconBonkPreserveDetails As Boolean = False
        Public Property SiliconBonkPreserveDetails As Boolean
            Get
                Return _siliconBonkPreserveDetails
            End Get
            Set(value As Boolean)
                If _siliconBonkPreserveDetails = value Then Return
                CaptureUndoState(NameOf(SiliconBonkPreserveDetails))
                _siliconBonkPreserveDetails = value
                Me.RaisePropertyChanged(NameOf(SiliconBonkPreserveDetails))
                RaiseResetButtonStateChanged()
                SchedulePreviewForCurrentTarget()
            End Set
        End Property

        Private _resetSiliconBonkCommand As ICommand
        Public ReadOnly Property ResetSiliconBonkCommand As ICommand
            Get
                If _resetSiliconBonkCommand Is Nothing Then
                    _resetSiliconBonkCommand = ReactiveCommand.Create(Sub() ResetCorrectionGroup("Silicon Bonk (Adaptive Black&White)", AddressOf ResetSiliconBonkValues))
                End If
                Return _resetSiliconBonkCommand
            End Get
        End Property

        Private Sub ResetSiliconBonkValues()
            _siliconBonkEnabled = False
            _siliconBonkHue = 45
            _siliconBonkLuma = 0
            _siliconBonkSaturation = 0
            _siliconBonkMidpoint = 0
            _siliconBonkContrast = 0
            _siliconBonkPreserveDetails = False
            RaiseFilmEffectsChanged()
        End Sub

        Private _fatToniEnabled As Boolean = False
        Public Property FatToniEnabled As Boolean
            Get
                Return _fatToniEnabled
            End Get
            Set(value As Boolean)
                If _fatToniEnabled = value Then Return
                CaptureUndoState(NameOf(FatToniEnabled))
                _fatToniEnabled = value
                Me.RaisePropertyChanged(NameOf(FatToniEnabled))
                RaiseResetButtonStateChanged()
                SchedulePreviewForCurrentTarget()
            End Set
        End Property

        Private _fatToniHighlightHue As Double = 0
        Public Property FatToniHighlightHue As Double
            Get
                Return _fatToniHighlightHue
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniHighlightHue, Math.Max(0, Math.Min(360, value)), NameOf(FatToniHighlightHue))
                Me.RaisePropertyChanged(NameOf(FatToniHighlightSaturationTrack))
            End Set
        End Property

        Public ReadOnly Property FatToniHighlightSaturationTrack As IBrush
            Get
                Return Controls.SliderTracks.SaturationOf(FatToniHighlightHue)
            End Get
        End Property

        Private _fatToniHighlightSaturation As Double = 0
        Public Property FatToniHighlightSaturation As Double
            Get
                Return _fatToniHighlightSaturation
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniHighlightSaturation, Math.Max(0, Math.Min(100, value)), NameOf(FatToniHighlightSaturation))
            End Set
        End Property

        Private _fatToniShadowHue As Double = 0
        Public Property FatToniShadowHue As Double
            Get
                Return _fatToniShadowHue
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniShadowHue, Math.Max(0, Math.Min(360, value)), NameOf(FatToniShadowHue))
                Me.RaisePropertyChanged(NameOf(FatToniShadowSaturationTrack))
            End Set
        End Property

        Public ReadOnly Property FatToniShadowSaturationTrack As IBrush
            Get
                Return Controls.SliderTracks.SaturationOf(FatToniShadowHue)
            End Get
        End Property

        Private _fatToniShadowSaturation As Double = 0
        Public Property FatToniShadowSaturation As Double
            Get
                Return _fatToniShadowSaturation
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniShadowSaturation, Math.Max(0, Math.Min(100, value)), NameOf(FatToniShadowSaturation))
            End Set
        End Property

        Private _fatToniSeparation As Double = 50
        Public Property FatToniSeparation As Double
            Get
                Return _fatToniSeparation
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniSeparation, Math.Max(0, Math.Min(100, value)), NameOf(FatToniSeparation))
            End Set
        End Property

        Private _fatToniMidpoint As Double = 0
        Public Property FatToniMidpoint As Double
            Get
                Return _fatToniMidpoint
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniMidpoint, Math.Max(-100, Math.Min(100, value)), NameOf(FatToniMidpoint))
            End Set
        End Property

        Private _fatToniContrast As Double = 85
        Public Property FatToniContrast As Double
            Get
                Return _fatToniContrast
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniContrast, Math.Max(0, Math.Min(100, value)), NameOf(FatToniContrast))
            End Set
        End Property

        Private _fatToniBaseHue As Double = 0
        Public Property FatToniBaseHue As Double
            Get
                Return _fatToniBaseHue
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniBaseHue, Math.Max(0, Math.Min(360, value)), NameOf(FatToniBaseHue))
                Me.RaisePropertyChanged(NameOf(FatToniBaseSaturationTrack))
                Me.RaisePropertyChanged(NameOf(FatToniBaseLuminanceTrack))
            End Set
        End Property

        Public ReadOnly Property FatToniBaseSaturationTrack As IBrush
            Get
                Return Controls.SliderTracks.SaturationOf(FatToniBaseHue)
            End Get
        End Property

        Public ReadOnly Property FatToniBaseLuminanceTrack As IBrush
            Get
                Return Controls.SliderTracks.LuminanceOf(FatToniBaseHue)
            End Get
        End Property

        Private _fatToniBaseSaturation As Double = 0
        Public Property FatToniBaseSaturation As Double
            Get
                Return _fatToniBaseSaturation
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniBaseSaturation, Math.Max(0, Math.Min(100, value)), NameOf(FatToniBaseSaturation))
            End Set
        End Property

        Private _fatToniBaseLuminance As Double = 100
        Public Property FatToniBaseLuminance As Double
            Get
                Return _fatToniBaseLuminance
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniBaseLuminance, Math.Max(0, Math.Min(100, value)), NameOf(FatToniBaseLuminance))
            End Set
        End Property

        Private _resetFatToniCommand As ICommand
        Public ReadOnly Property ResetFatToniCommand As ICommand
            Get
                If _resetFatToniCommand Is Nothing Then
                    _resetFatToniCommand = ReactiveCommand.Create(Sub() ResetCorrectionGroup("Fat Toni (Split Toning)", AddressOf ResetFatToniValues))
                End If
                Return _resetFatToniCommand
            End Get
        End Property

        Private Sub ResetFatToniValues()
            _fatToniEnabled = False
            _fatToniHighlightHue = 0
            _fatToniHighlightSaturation = 0
            _fatToniShadowHue = 0
            _fatToniShadowSaturation = 0
            _fatToniSeparation = 50
            _fatToniMidpoint = 0
            _fatToniContrast = 85
            _fatToniBaseHue = 0
            _fatToniBaseSaturation = 0
            _fatToniBaseLuminance = 100
            RaiseFilmEffectsChanged()
        End Sub

        Private Sub WriteFilmEffectsInto(adj As ImageAdjustments)
            adj.SiliconBonkEnabled = _siliconBonkEnabled
            adj.SiliconBonkHue = CSng(_siliconBonkHue)
            adj.SiliconBonkLuma = CSng(_siliconBonkLuma)
            adj.SiliconBonkSaturation = CSng(_siliconBonkSaturation)
            adj.SiliconBonkMidpoint = CSng(_siliconBonkMidpoint)
            adj.SiliconBonkContrast = CSng(_siliconBonkContrast)
            adj.SiliconBonkPreserveDetails = _siliconBonkPreserveDetails
            adj.FatToniEnabled = _fatToniEnabled
            adj.FatToniHighlightHue = CSng(_fatToniHighlightHue)
            adj.FatToniHighlightSaturation = CSng(_fatToniHighlightSaturation)
            adj.FatToniShadowHue = CSng(_fatToniShadowHue)
            adj.FatToniShadowSaturation = CSng(_fatToniShadowSaturation)
            adj.FatToniSeparation = CSng(_fatToniSeparation)
            adj.FatToniMidpoint = CSng(_fatToniMidpoint)
            adj.FatToniContrast = CSng(_fatToniContrast)
            adj.FatToniBaseHue = CSng(_fatToniBaseHue)
            adj.FatToniBaseSaturation = CSng(_fatToniBaseSaturation)
            adj.FatToniBaseLuminance = CSng(_fatToniBaseLuminance)
        End Sub

        Private Sub ReadFilmEffectsFrom(adj As ImageAdjustments)
            _siliconBonkEnabled = adj.SiliconBonkEnabled
            _siliconBonkHue = adj.SiliconBonkHue
            _siliconBonkLuma = adj.SiliconBonkLuma
            _siliconBonkSaturation = adj.SiliconBonkSaturation
            _siliconBonkMidpoint = adj.SiliconBonkMidpoint
            _siliconBonkContrast = adj.SiliconBonkContrast
            _siliconBonkPreserveDetails = adj.SiliconBonkPreserveDetails
            _fatToniEnabled = adj.FatToniEnabled
            _fatToniHighlightHue = adj.FatToniHighlightHue
            _fatToniHighlightSaturation = adj.FatToniHighlightSaturation
            _fatToniShadowHue = adj.FatToniShadowHue
            _fatToniShadowSaturation = adj.FatToniShadowSaturation
            _fatToniSeparation = adj.FatToniSeparation
            _fatToniMidpoint = adj.FatToniMidpoint
            _fatToniContrast = adj.FatToniContrast
            _fatToniBaseHue = adj.FatToniBaseHue
            _fatToniBaseSaturation = adj.FatToniBaseSaturation
            _fatToniBaseLuminance = adj.FatToniBaseLuminance
            RaiseFilmEffectsChanged()
        End Sub

        Private Sub RaiseFilmEffectsChanged()
            Me.RaisePropertyChanged(NameOf(SiliconBonkEnabled))
            Me.RaisePropertyChanged(NameOf(SiliconBonkHue))
            Me.RaisePropertyChanged(NameOf(SiliconBonkLuma))
            Me.RaisePropertyChanged(NameOf(SiliconBonkSaturation))
            Me.RaisePropertyChanged(NameOf(SiliconBonkMidpoint))
            Me.RaisePropertyChanged(NameOf(SiliconBonkContrast))
            Me.RaisePropertyChanged(NameOf(SiliconBonkPreserveDetails))
            Me.RaisePropertyChanged(NameOf(FatToniEnabled))
            Me.RaisePropertyChanged(NameOf(FatToniHighlightHue))
            Me.RaisePropertyChanged(NameOf(FatToniHighlightSaturation))
            Me.RaisePropertyChanged(NameOf(FatToniShadowHue))
            Me.RaisePropertyChanged(NameOf(FatToniShadowSaturation))
            Me.RaisePropertyChanged(NameOf(FatToniSeparation))
            Me.RaisePropertyChanged(NameOf(FatToniMidpoint))
            Me.RaisePropertyChanged(NameOf(FatToniContrast))
            Me.RaisePropertyChanged(NameOf(FatToniBaseHue))
            Me.RaisePropertyChanged(NameOf(FatToniBaseSaturation))
            Me.RaisePropertyChanged(NameOf(FatToniBaseLuminance))
            Me.RaisePropertyChanged(NameOf(SiliconBonkLumaTrack))
            Me.RaisePropertyChanged(NameOf(FatToniHighlightSaturationTrack))
            Me.RaisePropertyChanged(NameOf(FatToniShadowSaturationTrack))
            Me.RaisePropertyChanged(NameOf(FatToniBaseSaturationTrack))
            Me.RaisePropertyChanged(NameOf(FatToniBaseLuminanceTrack))
        End Sub

        Private Function FilmEffectHistoryLabel(propertyName As String) As String
            Select Case propertyName
                Case NameOf(SiliconBonkEnabled) : Return CombineHistoryLabel("Silicon Bonk (Adaptive Black&White)", "Aktiviert")
                Case NameOf(SiliconBonkHue) : Return CombineHistoryLabel("Silicon Bonk (Adaptive Black&White)", "Farbton")
                Case NameOf(SiliconBonkLuma) : Return CombineHistoryLabel("Silicon Bonk (Adaptive Black&White)", "Luminanz")
                Case NameOf(SiliconBonkSaturation) : Return CombineHistoryLabel("Silicon Bonk (Adaptive Black&White)", "Restfarbe")
                Case NameOf(SiliconBonkMidpoint) : Return CombineHistoryLabel("Silicon Bonk (Adaptive Black&White)", "Mittelpunkt")
                Case NameOf(SiliconBonkContrast) : Return CombineHistoryLabel("Silicon Bonk (Adaptive Black&White)", "Kontrast")
                Case NameOf(SiliconBonkPreserveDetails) : Return CombineHistoryLabel("Silicon Bonk (Adaptive Black&White)", "Lichter/Details erhalten")
                Case NameOf(FatToniEnabled) : Return CombineHistoryLabel("Fat Toni (Split Toning)", "Aktiviert")
                Case NameOf(FatToniHighlightHue) : Return CombineHistoryLabel("Fat Toni (Split Toning)", "Farbton")
                Case NameOf(FatToniHighlightSaturation) : Return CombineHistoryLabel("Fat Toni (Split Toning)", "Sättigung")
                Case NameOf(FatToniShadowHue) : Return CombineHistoryLabel("Fat Toni (Split Toning)", "Farbton")
                Case NameOf(FatToniShadowSaturation) : Return CombineHistoryLabel("Fat Toni (Split Toning)", "Sättigung")
                Case NameOf(FatToniSeparation) : Return CombineHistoryLabel("Fat Toni (Split Toning)", "Trennung")
                Case NameOf(FatToniMidpoint) : Return CombineHistoryLabel("Fat Toni (Split Toning)", "Mittelpunkt")
                Case NameOf(FatToniContrast) : Return CombineHistoryLabel("Fat Toni (Split Toning)", "Kontrast")
                Case NameOf(FatToniBaseHue) : Return CombineHistoryLabel("Fat Toni (Split Toning)", "Farbton")
                Case NameOf(FatToniBaseSaturation) : Return CombineHistoryLabel("Fat Toni (Split Toning)", "Sättigung")
                Case NameOf(FatToniBaseLuminance) : Return CombineHistoryLabel("Fat Toni (Split Toning)", "Luminanz")
            End Select
            Return Nothing
        End Function
    End Class
End Namespace
