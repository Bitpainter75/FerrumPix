Imports System
Imports System.Windows.Input
Imports Avalonia.Media
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels
    Partial Public Class EditorViewModel

        Private _siliconBonkEnabled As Boolean = FilmEffectParameters.SiliconBonkEnabledDefault
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

        Private _siliconBonkHue As Double = FilmEffectParameters.SiliconBonkHue.DefaultValue
        Public Property SiliconBonkHue As Double
            Get
                Return _siliconBonkHue
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_siliconBonkHue, FilmEffectParameters.SiliconBonkHue.ClampValue(value), NameOf(SiliconBonkHue))
                Me.RaisePropertyChanged(NameOf(SiliconBonkLumaTrack))
            End Set
        End Property

        Public ReadOnly Property SiliconBonkLumaTrack As IBrush
            Get
                Return Controls.SliderTracks.LuminanceOf(SiliconBonkHue)
            End Get
        End Property

        Private _siliconBonkLuma As Double = FilmEffectParameters.SiliconBonkLuma.DefaultValue
        Public Property SiliconBonkLuma As Double
            Get
                Return _siliconBonkLuma
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_siliconBonkLuma, FilmEffectParameters.SiliconBonkLuma.ClampValue(value), NameOf(SiliconBonkLuma))
            End Set
        End Property

        Private _siliconBonkSaturation As Double = FilmEffectParameters.SiliconBonkSaturation.DefaultValue
        Public Property SiliconBonkSaturation As Double
            Get
                Return _siliconBonkSaturation
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_siliconBonkSaturation, FilmEffectParameters.SiliconBonkSaturation.ClampValue(value), NameOf(SiliconBonkSaturation))
            End Set
        End Property

        Private _siliconBonkMidpoint As Double = FilmEffectParameters.SiliconBonkMidpoint.DefaultValue
        Public Property SiliconBonkMidpoint As Double
            Get
                Return _siliconBonkMidpoint
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_siliconBonkMidpoint, FilmEffectParameters.SiliconBonkMidpoint.ClampValue(value), NameOf(SiliconBonkMidpoint))
            End Set
        End Property

        Private _siliconBonkContrast As Double = FilmEffectParameters.SiliconBonkContrast.DefaultValue
        Public Property SiliconBonkContrast As Double
            Get
                Return _siliconBonkContrast
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_siliconBonkContrast, FilmEffectParameters.SiliconBonkContrast.ClampValue(value), NameOf(SiliconBonkContrast))
            End Set
        End Property

        Private _siliconBonkPreserveDetails As Boolean = FilmEffectParameters.SiliconBonkPreserveDetailsDefault
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
            _siliconBonkEnabled = FilmEffectParameters.SiliconBonkEnabledDefault
            _siliconBonkHue = FilmEffectParameters.SiliconBonkHue.DefaultValue
            _siliconBonkLuma = FilmEffectParameters.SiliconBonkLuma.DefaultValue
            _siliconBonkSaturation = FilmEffectParameters.SiliconBonkSaturation.DefaultValue
            _siliconBonkMidpoint = FilmEffectParameters.SiliconBonkMidpoint.DefaultValue
            _siliconBonkContrast = FilmEffectParameters.SiliconBonkContrast.DefaultValue
            _siliconBonkPreserveDetails = FilmEffectParameters.SiliconBonkPreserveDetailsDefault
            RaiseFilmEffectsChanged()
        End Sub

        Private _fatToniEnabled As Boolean = FilmEffectParameters.FatToniEnabledDefault
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

        Private _fatToniHighlightHue As Double = FilmEffectParameters.FatToniHighlightHue.DefaultValue
        Public Property FatToniHighlightHue As Double
            Get
                Return _fatToniHighlightHue
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniHighlightHue, FilmEffectParameters.FatToniHighlightHue.ClampValue(value), NameOf(FatToniHighlightHue))
                Me.RaisePropertyChanged(NameOf(FatToniHighlightSaturationTrack))
            End Set
        End Property

        Public ReadOnly Property FatToniHighlightSaturationTrack As IBrush
            Get
                Return Controls.SliderTracks.SaturationOf(FatToniHighlightHue)
            End Get
        End Property

        Private _fatToniHighlightSaturation As Double = FilmEffectParameters.FatToniHighlightSaturation.DefaultValue
        Public Property FatToniHighlightSaturation As Double
            Get
                Return _fatToniHighlightSaturation
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniHighlightSaturation, FilmEffectParameters.FatToniHighlightSaturation.ClampValue(value), NameOf(FatToniHighlightSaturation))
            End Set
        End Property

        Private _fatToniShadowHue As Double = FilmEffectParameters.FatToniShadowHue.DefaultValue
        Public Property FatToniShadowHue As Double
            Get
                Return _fatToniShadowHue
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniShadowHue, FilmEffectParameters.FatToniShadowHue.ClampValue(value), NameOf(FatToniShadowHue))
                Me.RaisePropertyChanged(NameOf(FatToniShadowSaturationTrack))
            End Set
        End Property

        Public ReadOnly Property FatToniShadowSaturationTrack As IBrush
            Get
                Return Controls.SliderTracks.SaturationOf(FatToniShadowHue)
            End Get
        End Property

        Private _fatToniShadowSaturation As Double = FilmEffectParameters.FatToniShadowSaturation.DefaultValue
        Public Property FatToniShadowSaturation As Double
            Get
                Return _fatToniShadowSaturation
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniShadowSaturation, FilmEffectParameters.FatToniShadowSaturation.ClampValue(value), NameOf(FatToniShadowSaturation))
            End Set
        End Property

        Private _fatToniSeparation As Double = FilmEffectParameters.FatToniSeparation.DefaultValue
        Public Property FatToniSeparation As Double
            Get
                Return _fatToniSeparation
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniSeparation, FilmEffectParameters.FatToniSeparation.ClampValue(value), NameOf(FatToniSeparation))
            End Set
        End Property

        Private _fatToniMidpoint As Double = FilmEffectParameters.FatToniMidpoint.DefaultValue
        Public Property FatToniMidpoint As Double
            Get
                Return _fatToniMidpoint
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniMidpoint, FilmEffectParameters.FatToniMidpoint.ClampValue(value), NameOf(FatToniMidpoint))
            End Set
        End Property

        Private _fatToniContrast As Double = FilmEffectParameters.FatToniContrast.DefaultValue
        Public Property FatToniContrast As Double
            Get
                Return _fatToniContrast
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniContrast, FilmEffectParameters.FatToniContrast.ClampValue(value), NameOf(FatToniContrast))
            End Set
        End Property

        Private _fatToniBaseHue As Double = FilmEffectParameters.FatToniBaseHue.DefaultValue
        Public Property FatToniBaseHue As Double
            Get
                Return _fatToniBaseHue
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniBaseHue, FilmEffectParameters.FatToniBaseHue.ClampValue(value), NameOf(FatToniBaseHue))
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

        Private _fatToniBaseSaturation As Double = FilmEffectParameters.FatToniBaseSaturation.DefaultValue
        Public Property FatToniBaseSaturation As Double
            Get
                Return _fatToniBaseSaturation
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniBaseSaturation, FilmEffectParameters.FatToniBaseSaturation.ClampValue(value), NameOf(FatToniBaseSaturation))
            End Set
        End Property

        Private _fatToniBaseLuminance As Double = FilmEffectParameters.FatToniBaseLuminance.DefaultValue
        Public Property FatToniBaseLuminance As Double
            Get
                Return _fatToniBaseLuminance
            End Get
            Set(value As Double)
                If Not Double.IsFinite(value) Then Return
                SetUndoableDouble(_fatToniBaseLuminance, FilmEffectParameters.FatToniBaseLuminance.ClampValue(value), NameOf(FatToniBaseLuminance))
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
            _fatToniEnabled = FilmEffectParameters.FatToniEnabledDefault
            _fatToniHighlightHue = FilmEffectParameters.FatToniHighlightHue.DefaultValue
            _fatToniHighlightSaturation = FilmEffectParameters.FatToniHighlightSaturation.DefaultValue
            _fatToniShadowHue = FilmEffectParameters.FatToniShadowHue.DefaultValue
            _fatToniShadowSaturation = FilmEffectParameters.FatToniShadowSaturation.DefaultValue
            _fatToniSeparation = FilmEffectParameters.FatToniSeparation.DefaultValue
            _fatToniMidpoint = FilmEffectParameters.FatToniMidpoint.DefaultValue
            _fatToniContrast = FilmEffectParameters.FatToniContrast.DefaultValue
            _fatToniBaseHue = FilmEffectParameters.FatToniBaseHue.DefaultValue
            _fatToniBaseSaturation = FilmEffectParameters.FatToniBaseSaturation.DefaultValue
            _fatToniBaseLuminance = FilmEffectParameters.FatToniBaseLuminance.DefaultValue
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
            _siliconBonkHue = FilmEffectParameters.SiliconBonkHue.ClampValue(adj.SiliconBonkHue)
            _siliconBonkLuma = FilmEffectParameters.SiliconBonkLuma.ClampValue(adj.SiliconBonkLuma)
            _siliconBonkSaturation = FilmEffectParameters.SiliconBonkSaturation.ClampValue(adj.SiliconBonkSaturation)
            _siliconBonkMidpoint = FilmEffectParameters.SiliconBonkMidpoint.ClampValue(adj.SiliconBonkMidpoint)
            _siliconBonkContrast = FilmEffectParameters.SiliconBonkContrast.ClampValue(adj.SiliconBonkContrast)
            _siliconBonkPreserveDetails = adj.SiliconBonkPreserveDetails
            _fatToniEnabled = adj.FatToniEnabled
            _fatToniHighlightHue = FilmEffectParameters.FatToniHighlightHue.ClampValue(adj.FatToniHighlightHue)
            _fatToniHighlightSaturation = FilmEffectParameters.FatToniHighlightSaturation.ClampValue(adj.FatToniHighlightSaturation)
            _fatToniShadowHue = FilmEffectParameters.FatToniShadowHue.ClampValue(adj.FatToniShadowHue)
            _fatToniShadowSaturation = FilmEffectParameters.FatToniShadowSaturation.ClampValue(adj.FatToniShadowSaturation)
            _fatToniSeparation = FilmEffectParameters.FatToniSeparation.ClampValue(adj.FatToniSeparation)
            _fatToniMidpoint = FilmEffectParameters.FatToniMidpoint.ClampValue(adj.FatToniMidpoint)
            _fatToniContrast = FilmEffectParameters.FatToniContrast.ClampValue(adj.FatToniContrast)
            _fatToniBaseHue = FilmEffectParameters.FatToniBaseHue.ClampValue(adj.FatToniBaseHue)
            _fatToniBaseSaturation = FilmEffectParameters.FatToniBaseSaturation.ClampValue(adj.FatToniBaseSaturation)
            _fatToniBaseLuminance = FilmEffectParameters.FatToniBaseLuminance.ClampValue(adj.FatToniBaseLuminance)
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
