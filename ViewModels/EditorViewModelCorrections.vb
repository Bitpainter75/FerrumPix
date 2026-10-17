Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Die weiteren Korrekturen: KANALMIXER und VERLAUFSUMSETZUNG im Werkzeug "Farbe",
    ''' TONTRENNUNG, SCHWELLENWERT und TILT-SHIFT im Werkzeug "Effekte".
    '''
    ''' Es sind gewoehnliche Pixelregler des Rezepts (<see cref="ImageAdjustments"/>), keine eigene
    ''' Ebenenart: was hier steht, wirkt auf das ganze Bild, auf ein Objekt im Anpassen-Modus und auf
    ''' eine markierte Korrekturebene, denn alle drei gehen ueber BuildAdjustmentsFromFields und
    ''' ApplyAdjustments. In den Hauptteil fuehrt deshalb je Stelle nur ein Aufruf hierher.
    '''
    ''' Der Kanalmixer hat mehr Werte, als ein Panel Regler zeigen soll. Er zeigt deshalb nur EINE
    ''' Zeile: die gewaehlte Ausgabe, im Monochrom-Modus die Grauzeile. Welche Zeile gerade zu sehen
    ''' ist, ist reine Ansicht und kein Bearbeitungsschritt.</summary>
    Partial Public Class EditorViewModel

        ' Kanalmixer: vier Zeilen (Rot, Gruen, Blau, Grau) zu je vier Spalten (Rot, Gruen, Blau,
        ' Konstante), Prozent. Die Grauzeile gilt nur im Monochrom-Modus.
        Private Shared ReadOnly ChannelMixerNeutral As Double() =
            {100, 0, 0, 0, 0, 100, 0, 0, 0, 0, 100, 0, 40, 40, 20, 0}
        Private ReadOnly _channelMixer As Double() = CType(ChannelMixerNeutral.Clone(), Double())
        Private _channelMixerMonochrome As Boolean
        Private _channelMixerOutput As Integer

        Private _gradientMapShadowColor As String = "#FF000000"
        Private _gradientMapHighlightColor As String = "#FFFFFFFF"
        Private _gradientMapAmount As Double

        Private _posterizeLevels As Double
        Private _thresholdLevel As Double

        Private _resetChannelMixerCommand As ICommand
        Private _resetGradientMapCommand As ICommand
        Private _resetPosterizeCommand As ICommand
        Private _resetThresholdCommand As ICommand

        Private _tiltShiftAmount As Double
        Private _tiltShiftCenterX As Double = 50
        Private _tiltShiftCenterY As Double = 50
        Private _tiltShiftAngle As Double
        Private _tiltShiftFocus As Double = 20
        Private _tiltShiftTransition As Double = 40
        Private _resetTiltShiftCommand As ICommand

#Region "Kanalmixer"

        ''' <summary>Die drei Ausgaben in der Auswahl. Im Monochrom-Modus gibt es nur eine.</summary>
        Public ReadOnly Property ChannelMixerOutputOptions As IReadOnlyList(Of String)
            Get
                Return New String() {LocalizationService.T("Rot"), LocalizationService.T("Grün"), LocalizationService.T("Blau")}
            End Get
        End Property

        Public Property ChannelMixerOutputIndex As Integer
            Get
                Return _channelMixerOutput
            End Get
            Set(value As Integer)
                Dim clamped = Math.Max(0, Math.Min(2, value))
                If _channelMixerOutput = clamped Then Return
                _channelMixerOutput = clamped
                Me.RaisePropertyChanged(NameOf(ChannelMixerOutputIndex))
                RaiseChannelMixerRowChanged()
            End Set
        End Property

        Public Property ChannelMixerMonochrome As Boolean
            Get
                Return _channelMixerMonochrome
            End Get
            Set(value As Boolean)
                If _channelMixerMonochrome = value Then Return
                CaptureUndoState(NameOf(ChannelMixerMonochrome))
                _channelMixerMonochrome = value
                Me.RaisePropertyChanged(NameOf(ChannelMixerMonochrome))
                Me.RaisePropertyChanged(NameOf(IsChannelMixerColor))
                RaiseChannelMixerRowChanged()
                RaiseResetButtonStateChanged()
                SchedulePreviewForCurrentTarget()
            End Set
        End Property

        ''' <summary>Die Ausgabeauswahl gilt nur fuer den farbigen Kanalmixer.</summary>
        Public ReadOnly Property IsChannelMixerColor As Boolean
            Get
                Return Not _channelMixerMonochrome
            End Get
        End Property

        ''' <summary>Die Zeile, die die Regler gerade zeigen: die gewaehlte Ausgabe, im
        ''' Monochrom-Modus die Grauzeile.</summary>
        Private ReadOnly Property ChannelMixerRow As Integer
            Get
                Return If(_channelMixerMonochrome, 3, _channelMixerOutput)
            End Get
        End Property

        Public Property ChannelMixerRed As Double
            Get
                Return _channelMixer(ChannelMixerRow * 4)
            End Get
            Set(value As Double)
                SetUndoableDouble(_channelMixer(ChannelMixerRow * 4), Math.Max(-200, Math.Min(200, value)), NameOf(ChannelMixerRed))
            End Set
        End Property

        Public Property ChannelMixerGreen As Double
            Get
                Return _channelMixer(ChannelMixerRow * 4 + 1)
            End Get
            Set(value As Double)
                SetUndoableDouble(_channelMixer(ChannelMixerRow * 4 + 1), Math.Max(-200, Math.Min(200, value)), NameOf(ChannelMixerGreen))
            End Set
        End Property

        Public Property ChannelMixerBlue As Double
            Get
                Return _channelMixer(ChannelMixerRow * 4 + 2)
            End Get
            Set(value As Double)
                SetUndoableDouble(_channelMixer(ChannelMixerRow * 4 + 2), Math.Max(-200, Math.Min(200, value)), NameOf(ChannelMixerBlue))
            End Set
        End Property

        Public Property ChannelMixerConstant As Double
            Get
                Return _channelMixer(ChannelMixerRow * 4 + 3)
            End Get
            Set(value As Double)
                SetUndoableDouble(_channelMixer(ChannelMixerRow * 4 + 3), Math.Max(-100, Math.Min(100, value)), NameOf(ChannelMixerConstant))
            End Set
        End Property

        ''' <summary>Der Rueckfallwert der drei Anteilsregler (Doppelklick) haengt an der gezeigten
        ''' Zeile: 100 auf dem eigenen Kanal, sonst 0, in der Grauzeile 40, 40 und 20.</summary>
        Public ReadOnly Property ChannelMixerRedDefault As Double
            Get
                Return ChannelMixerNeutral(ChannelMixerRow * 4)
            End Get
        End Property

        Public ReadOnly Property ChannelMixerGreenDefault As Double
            Get
                Return ChannelMixerNeutral(ChannelMixerRow * 4 + 1)
            End Get
        End Property

        Public ReadOnly Property ChannelMixerBlueDefault As Double
            Get
                Return ChannelMixerNeutral(ChannelMixerRow * 4 + 2)
            End Get
        End Property

        Private Sub RaiseChannelMixerRowChanged()
            Me.RaisePropertyChanged(NameOf(ChannelMixerRed))
            Me.RaisePropertyChanged(NameOf(ChannelMixerGreen))
            Me.RaisePropertyChanged(NameOf(ChannelMixerBlue))
            Me.RaisePropertyChanged(NameOf(ChannelMixerConstant))
            Me.RaisePropertyChanged(NameOf(ChannelMixerRedDefault))
            Me.RaisePropertyChanged(NameOf(ChannelMixerGreenDefault))
            Me.RaisePropertyChanged(NameOf(ChannelMixerBlueDefault))
        End Sub

        Public ReadOnly Property ResetChannelMixerCommand As ICommand
            Get
                If _resetChannelMixerCommand Is Nothing Then
                    _resetChannelMixerCommand = ReactiveCommand.Create(Sub() ResetCorrectionGroup("Kanalmixer", AddressOf ResetChannelMixerValues))
                End If
                Return _resetChannelMixerCommand
            End Get
        End Property

        Private Sub ResetChannelMixerValues()
            Array.Copy(ChannelMixerNeutral, _channelMixer, ChannelMixerNeutral.Length)
            _channelMixerMonochrome = False
            Me.RaisePropertyChanged(NameOf(ChannelMixerMonochrome))
            Me.RaisePropertyChanged(NameOf(IsChannelMixerColor))
            RaiseChannelMixerRowChanged()
        End Sub

#End Region

#Region "Verlaufsumsetzung"

        Public Property GradientMapShadowColorValue As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_gradientMapShadowColor, Avalonia.Media.Colors.Black)
            End Get
            Set(value As Avalonia.Media.Color)
                Dim text = value.ToString()
                If String.Equals(_gradientMapShadowColor, text, StringComparison.OrdinalIgnoreCase) Then Return
                CaptureUndoState(NameOf(GradientMapShadowColorValue))
                _gradientMapShadowColor = text
                Me.RaisePropertyChanged(NameOf(GradientMapShadowColorValue))
                RaiseResetButtonStateChanged()
                SchedulePreviewForCurrentTarget()
            End Set
        End Property

        Public Property GradientMapHighlightColorValue As Avalonia.Media.Color
            Get
                Return ParseAvaloniaColorOrDefault(_gradientMapHighlightColor, Avalonia.Media.Colors.White)
            End Get
            Set(value As Avalonia.Media.Color)
                Dim text = value.ToString()
                If String.Equals(_gradientMapHighlightColor, text, StringComparison.OrdinalIgnoreCase) Then Return
                CaptureUndoState(NameOf(GradientMapHighlightColorValue))
                _gradientMapHighlightColor = text
                Me.RaisePropertyChanged(NameOf(GradientMapHighlightColorValue))
                RaiseResetButtonStateChanged()
                SchedulePreviewForCurrentTarget()
            End Set
        End Property

        Public Property GradientMapAmount As Double
            Get
                Return _gradientMapAmount
            End Get
            Set(value As Double)
                SetUndoableDouble(_gradientMapAmount, Math.Max(0, Math.Min(100, value)), NameOf(GradientMapAmount))
            End Set
        End Property

        Public ReadOnly Property ResetGradientMapCommand As ICommand
            Get
                If _resetGradientMapCommand Is Nothing Then
                    _resetGradientMapCommand = ReactiveCommand.Create(Sub() ResetCorrectionGroup("Verlaufsumsetzung", AddressOf ResetGradientMapValues))
                End If
                Return _resetGradientMapCommand
            End Get
        End Property

        Private Sub ResetGradientMapValues()
            _gradientMapShadowColor = "#FF000000"
            _gradientMapHighlightColor = "#FFFFFFFF"
            _gradientMapAmount = 0
            Me.RaisePropertyChanged(NameOf(GradientMapShadowColorValue))
            Me.RaisePropertyChanged(NameOf(GradientMapHighlightColorValue))
            Me.RaisePropertyChanged(NameOf(GradientMapAmount))
        End Sub

#End Region

#Region "Tontrennung und Schwellenwert"

        ''' <summary>Tonstufen je Kanal. 0 ist aus; eine einzige Stufe gibt es nicht, der Regler
        ''' springt von 0 direkt auf 2.</summary>
        Public Property PosterizeLevels As Double
            Get
                Return _posterizeLevels
            End Get
            Set(value As Double)
                Dim levels = Math.Round(Math.Max(0, Math.Min(64, value)))
                If levels > 0 AndAlso levels < 2 Then levels = 2
                SetUndoableDouble(_posterizeLevels, levels, NameOf(PosterizeLevels))
            End Set
        End Property

        Public Property ThresholdLevel As Double
            Get
                Return _thresholdLevel
            End Get
            Set(value As Double)
                SetUndoableDouble(_thresholdLevel, Math.Round(Math.Max(0, Math.Min(255, value))), NameOf(ThresholdLevel))
            End Set
        End Property

        Public ReadOnly Property ResetPosterizeCommand As ICommand
            Get
                If _resetPosterizeCommand Is Nothing Then
                    _resetPosterizeCommand = ReactiveCommand.Create(Sub() ResetCorrectionGroup("Tontrennung", AddressOf ResetPosterizeValues))
                End If
                Return _resetPosterizeCommand
            End Get
        End Property

        Public ReadOnly Property ResetThresholdCommand As ICommand
            Get
                If _resetThresholdCommand Is Nothing Then
                    _resetThresholdCommand = ReactiveCommand.Create(Sub() ResetCorrectionGroup("Schwellenwert", AddressOf ResetThresholdValues))
                End If
                Return _resetThresholdCommand
            End Get
        End Property

        Private Sub ResetPosterizeValues()
            _posterizeLevels = 0
            Me.RaisePropertyChanged(NameOf(PosterizeLevels))
        End Sub

        Private Sub ResetThresholdValues()
            _thresholdLevel = 0
            Me.RaisePropertyChanged(NameOf(ThresholdLevel))
        End Sub

#End Region

#Region "Tilt-Shift"

        Public Property TiltShiftAmount As Double
            Get
                Return _tiltShiftAmount
            End Get
            Set(value As Double)
                SetUndoableDouble(_tiltShiftAmount, Math.Max(0, Math.Min(100, value)), NameOf(TiltShiftAmount))
            End Set
        End Property

        Public Property TiltShiftCenterX As Double
            Get
                Return _tiltShiftCenterX
            End Get
            Set(value As Double)
                SetUndoableDouble(_tiltShiftCenterX, Math.Max(0, Math.Min(100, value)), NameOf(TiltShiftCenterX))
            End Set
        End Property

        Public Property TiltShiftCenterY As Double
            Get
                Return _tiltShiftCenterY
            End Get
            Set(value As Double)
                SetUndoableDouble(_tiltShiftCenterY, Math.Max(0, Math.Min(100, value)), NameOf(TiltShiftCenterY))
            End Set
        End Property

        Public Property TiltShiftAngle As Double
            Get
                Return _tiltShiftAngle
            End Get
            Set(value As Double)
                SetUndoableDouble(_tiltShiftAngle, Math.Max(-90, Math.Min(90, value)), NameOf(TiltShiftAngle))
            End Set
        End Property

        Public Property TiltShiftFocus As Double
            Get
                Return _tiltShiftFocus
            End Get
            Set(value As Double)
                SetUndoableDouble(_tiltShiftFocus, Math.Max(0, Math.Min(100, value)), NameOf(TiltShiftFocus))
            End Set
        End Property

        Public Property TiltShiftTransition As Double
            Get
                Return _tiltShiftTransition
            End Get
            Set(value As Double)
                SetUndoableDouble(_tiltShiftTransition, Math.Max(0, Math.Min(100, value)), NameOf(TiltShiftTransition))
            End Set
        End Property

        Public ReadOnly Property ResetTiltShiftCommand As ICommand
            Get
                If _resetTiltShiftCommand Is Nothing Then
                    _resetTiltShiftCommand = ReactiveCommand.Create(Sub() ResetCorrectionGroup("Tilt-Shift", AddressOf ResetTiltShiftValues))
                End If
                Return _resetTiltShiftCommand
            End Get
        End Property

        Private Sub ResetTiltShiftValues()
            _tiltShiftAmount = 0
            _tiltShiftCenterX = 50
            _tiltShiftCenterY = 50
            _tiltShiftAngle = 0
            _tiltShiftFocus = 20
            _tiltShiftTransition = 40
            RaiseTiltShiftChanged()
        End Sub

        Private Sub RaiseTiltShiftChanged()
            For Each n In {NameOf(TiltShiftAmount), NameOf(TiltShiftCenterX), NameOf(TiltShiftCenterY),
                           NameOf(TiltShiftAngle), NameOf(TiltShiftFocus), NameOf(TiltShiftTransition)}
                Me.RaisePropertyChanged(n)
            Next
        End Sub

#End Region

#Region "Anbindung an Rezept, Zuruecksetzen und Historie"

        ''' <summary>Ein Gruppen-Zuruecksetzer: ein benannter Schritt in der Historie, dann die Werte.</summary>
        Private Sub ResetCorrectionGroup(group As String, reset As Action)
            PushUndo(ResetHistoryLabel(group))
            reset()
            RaiseResetButtonStateChanged()
            SchedulePreviewForCurrentTarget()
        End Sub

        ''' <summary>Die vier Farbgruppen zurueck auf neutral, ohne eigenen Historienschritt. Gerufen
        ''' vom Zuruecksetzen des Werkzeugs "Farbe" und von den weiten Zuruecksetzern.</summary>
        Private Sub ResetColorCorrectionsInternal()
            ResetChannelMixerValues()
            ResetGradientMapValues()
        End Sub

        ''' <summary>Tontrennung, Schwellenwert und Tilt-Shift zurueck, fuer das Werkzeug "Effekte".</summary>
        Private Sub ResetEffectCorrectionsInternal()
            ResetPosterizeValues()
            ResetThresholdValues()
            ResetTiltShiftValues()
        End Sub

        ''' <summary>Schreibt die Werte aller sechs Gruppen ins Rezept.</summary>
        Private Sub WriteCorrectionsInto(adj As ImageAdjustments)
            Dim m = _channelMixer
            adj.ChannelMixerRedRed = CSng(m(0)) : adj.ChannelMixerRedGreen = CSng(m(1))
            adj.ChannelMixerRedBlue = CSng(m(2)) : adj.ChannelMixerRedConstant = CSng(m(3))
            adj.ChannelMixerGreenRed = CSng(m(4)) : adj.ChannelMixerGreenGreen = CSng(m(5))
            adj.ChannelMixerGreenBlue = CSng(m(6)) : adj.ChannelMixerGreenConstant = CSng(m(7))
            adj.ChannelMixerBlueRed = CSng(m(8)) : adj.ChannelMixerBlueGreen = CSng(m(9))
            adj.ChannelMixerBlueBlue = CSng(m(10)) : adj.ChannelMixerBlueConstant = CSng(m(11))
            adj.ChannelMixerGrayRed = CSng(m(12)) : adj.ChannelMixerGrayGreen = CSng(m(13))
            adj.ChannelMixerGrayBlue = CSng(m(14)) : adj.ChannelMixerGrayConstant = CSng(m(15))
            adj.ChannelMixerMonochrome = _channelMixerMonochrome
            adj.GradientMapShadowColor = _gradientMapShadowColor
            adj.GradientMapHighlightColor = _gradientMapHighlightColor
            adj.GradientMapAmount = CSng(_gradientMapAmount)
            adj.PosterizeLevels = CSng(_posterizeLevels)
            adj.ThresholdLevel = CSng(_thresholdLevel)
            adj.TiltShiftAmount = CSng(_tiltShiftAmount)
            adj.TiltShiftCenterX = CSng(_tiltShiftCenterX)
            adj.TiltShiftCenterY = CSng(_tiltShiftCenterY)
            adj.TiltShiftAngle = CSng(_tiltShiftAngle)
            adj.TiltShiftFocus = CSng(_tiltShiftFocus)
            adj.TiltShiftTransition = CSng(_tiltShiftTransition)
        End Sub

        ''' <summary>Uebernimmt die Werte aller sechs Gruppen aus dem Rezept und meldet sie der
        ''' Oberflaeche. Die gewaehlte Ausgabe und der gewaehlte Farbbereich bleiben stehen.</summary>
        Private Sub ReadCorrectionsFrom(adj As ImageAdjustments)
            Dim values = {adj.ChannelMixerRedRed, adj.ChannelMixerRedGreen, adj.ChannelMixerRedBlue, adj.ChannelMixerRedConstant,
                          adj.ChannelMixerGreenRed, adj.ChannelMixerGreenGreen, adj.ChannelMixerGreenBlue, adj.ChannelMixerGreenConstant,
                          adj.ChannelMixerBlueRed, adj.ChannelMixerBlueGreen, adj.ChannelMixerBlueBlue, adj.ChannelMixerBlueConstant,
                          adj.ChannelMixerGrayRed, adj.ChannelMixerGrayGreen, adj.ChannelMixerGrayBlue, adj.ChannelMixerGrayConstant}
            For i = 0 To values.Length - 1
                _channelMixer(i) = values(i)
            Next
            _channelMixerMonochrome = adj.ChannelMixerMonochrome
            _gradientMapShadowColor = If(String.IsNullOrWhiteSpace(adj.GradientMapShadowColor), "#FF000000", adj.GradientMapShadowColor)
            _gradientMapHighlightColor = If(String.IsNullOrWhiteSpace(adj.GradientMapHighlightColor), "#FFFFFFFF", adj.GradientMapHighlightColor)
            _gradientMapAmount = adj.GradientMapAmount
            _posterizeLevels = adj.PosterizeLevels
            _thresholdLevel = adj.ThresholdLevel
            _tiltShiftAmount = adj.TiltShiftAmount
            _tiltShiftCenterX = adj.TiltShiftCenterX
            _tiltShiftCenterY = adj.TiltShiftCenterY
            _tiltShiftAngle = adj.TiltShiftAngle
            _tiltShiftFocus = adj.TiltShiftFocus
            _tiltShiftTransition = adj.TiltShiftTransition
            RaiseCorrectionsChanged()
        End Sub

        Private Sub RaiseCorrectionsChanged()
            Me.RaisePropertyChanged(NameOf(ChannelMixerMonochrome))
            Me.RaisePropertyChanged(NameOf(IsChannelMixerColor))
            RaiseChannelMixerRowChanged()
            Me.RaisePropertyChanged(NameOf(GradientMapShadowColorValue))
            Me.RaisePropertyChanged(NameOf(GradientMapHighlightColorValue))
            Me.RaisePropertyChanged(NameOf(GradientMapAmount))
            Me.RaisePropertyChanged(NameOf(PosterizeLevels))
            Me.RaisePropertyChanged(NameOf(ThresholdLevel))
            RaiseTiltShiftChanged()
        End Sub

        ''' <summary>Der Name eines Handgriffs in der Historie, oder Nothing, wenn er nicht hierher
        ''' gehoert. Gruppe und Regler gehen einzeln durch die Uebersetzung.</summary>
        Private Function CorrectionHistoryLabel(propertyName As String) As String
            Select Case propertyName
                Case NameOf(ChannelMixerMonochrome) : Return CombineHistoryLabel("Kanalmixer", "Monochrom")
                Case NameOf(ChannelMixerRed), NameOf(ChannelMixerGreen), NameOf(ChannelMixerBlue), NameOf(ChannelMixerConstant)
                    Dim output = If(_channelMixerMonochrome, "Grau", {"Rot", "Grün", "Blau"}(_channelMixerOutput))
                    Return CombineHistoryLabel("Kanalmixer", output)
                Case NameOf(GradientMapShadowColorValue) : Return CombineHistoryLabel("Verlaufsumsetzung", "Tiefen")
                Case NameOf(GradientMapHighlightColorValue) : Return CombineHistoryLabel("Verlaufsumsetzung", "Lichter")
                Case NameOf(GradientMapAmount) : Return CombineHistoryLabel("Verlaufsumsetzung", "Stärke")
                Case NameOf(PosterizeLevels) : Return LocalizationService.T("Tontrennung")
                Case NameOf(ThresholdLevel) : Return LocalizationService.T("Schwellenwert")
                Case NameOf(TiltShiftAmount) : Return CombineHistoryLabel("Tilt-Shift", "Stärke")
                Case NameOf(TiltShiftCenterX) : Return CombineHistoryLabel("Tilt-Shift", "Mitte X")
                Case NameOf(TiltShiftCenterY) : Return CombineHistoryLabel("Tilt-Shift", "Mitte Y")
                Case NameOf(TiltShiftAngle) : Return CombineHistoryLabel("Tilt-Shift", "Winkel")
                Case NameOf(TiltShiftFocus) : Return CombineHistoryLabel("Tilt-Shift", "Schärfebereich")
                Case NameOf(TiltShiftTransition) : Return CombineHistoryLabel("Tilt-Shift", "Übergang")
            End Select
            Return Nothing
        End Function

        ''' <summary>Das Symbol eines Handgriffs in der Historie: das des Werkzeugs, in dem er sitzt.</summary>
        Private Shared Function CorrectionHistoryIcon(propertyName As String) As String
            Const outline = "avares://FerrumPix/Assets/Icons/outline/"
            Select Case propertyName
                Case NameOf(ChannelMixerMonochrome), NameOf(ChannelMixerRed), NameOf(ChannelMixerGreen),
                     NameOf(ChannelMixerBlue), NameOf(ChannelMixerConstant),
                     NameOf(GradientMapShadowColorValue), NameOf(GradientMapHighlightColorValue), NameOf(GradientMapAmount)
                    Return outline & "color-filter.svg"
                Case NameOf(PosterizeLevels), NameOf(ThresholdLevel),
                     NameOf(TiltShiftAmount), NameOf(TiltShiftCenterX), NameOf(TiltShiftCenterY),
                     NameOf(TiltShiftAngle), NameOf(TiltShiftFocus), NameOf(TiltShiftTransition)
                    Return outline & "sparkles.svg"
            End Select
            Return Nothing
        End Function

#End Region

    End Class

End Namespace
