Imports Avalonia
Imports Avalonia.Media

Namespace Controls

    ''' <summary>Die Farbspuren der Regler mit Farbbedeutung (<see cref="RoundSlider.ColorTrack"/>).
    ''' Sie zeigen, wohin der Regler führt, und erscheinen nur mit der Einstellung „Farbige Regler".
    ''' Die Richtung folgt der Wirkung: links ist das, was der Regler bei seinem kleinsten Wert
    ''' ins Bild bringt.</summary>
    Public NotInheritable Class SliderTracks

        Private Sub New()
        End Sub

        ''' <summary>Farbtemperatur, relativ wie in Kelvin: kälter links, wärmer rechts.</summary>
        Public Shared ReadOnly Property Temperature As IBrush = Gradient(Color.Parse("#3D7BD9"), Color.Parse("#C8CCD0"), Color.Parse("#E3C13B"))

        ''' <summary>Tönung: grün links, magenta rechts. Ebenso die Schattentönung der Kalibrierung.</summary>
        Public Shared ReadOnly Property Tint As IBrush = Gradient(Color.Parse("#3FA34D"), Color.Parse("#C8CCD0"), Color.Parse("#C24FB6"))

        ''' <summary>Kalibrierung, Farbton der Primärfarben: wohin die Primärfarbe kippt.</summary>
        Public Shared ReadOnly Property CalibrationRedHue As IBrush = HueSpan(0.0, 35.0)
        Public Shared ReadOnly Property CalibrationGreenHue As IBrush = HueSpan(120.0, 35.0)
        Public Shared ReadOnly Property CalibrationBlueHue As IBrush = HueSpan(240.0, 35.0)

        ''' <summary>Kalibrierung, Sättigung der Primärfarben: grau nach der Primärfarbe.</summary>
        Public Shared ReadOnly Property CalibrationRedSaturation As IBrush = SaturationOf(0.0)
        Public Shared ReadOnly Property CalibrationGreenSaturation As IBrush = SaturationOf(120.0)
        Public Shared ReadOnly Property CalibrationBlueSaturation As IBrush = SaturationOf(240.0)

        ''' <summary>Kanalmixer: was ein Quellkanal in den Ausgang bringt. Negativ zieht ihn ab und
        ''' schiebt zur Gegenfarbe, positiv gibt mehr von ihm; die Mitte ist grau, weil der Regler
        ''' dort nichts beiträgt. Die Konstante hebt oder senkt den Ausgang gleichmässig.</summary>
        Public Shared ReadOnly Property ChannelRed As IBrush = Gradient(Color.Parse("#3BB8C4"), Color.Parse("#8A8F94"), Color.Parse("#D9443A"))
        Public Shared ReadOnly Property ChannelGreen As IBrush = Gradient(Color.Parse("#C24FB6"), Color.Parse("#8A8F94"), Color.Parse("#3FA34D"))
        Public Shared ReadOnly Property ChannelBlue As IBrush = Gradient(Color.Parse("#D9C23A"), Color.Parse("#8A8F94"), Color.Parse("#3D6BD9"))
        Public Shared ReadOnly Property ChannelConstant As IBrush = Gradient(Color.Parse("#1E2226"), Color.Parse("#8A8F94"), Color.Parse("#F2F4F6"))

        ''' <summary>Farbton um eine Mitte, <paramref name="spanDegrees"/> zu jeder Seite.</summary>
        Public Shared Function HueSpan(centerDegrees As Double, spanDegrees As Double) As IBrush
            Dim colors = New Color(6) {}
            For i = 0 To 6
                Dim h = centerDegrees - spanDegrees + 2.0 * spanDegrees * i / 6.0
                colors(i) = New HsvColor(1.0, ((h Mod 360.0) + 360.0) Mod 360.0, 0.72, 0.88).ToRgb()
            Next
            Return Gradient(colors)
        End Function

        ''' <summary>Grau nach einer gesättigten Farbe dieses Farbtons.</summary>
        Public Shared Function SaturationOf(hueDegrees As Double) As IBrush
            Return Gradient(New HsvColor(1.0, hueDegrees, 0.0, 0.62).ToRgb(),
                            New HsvColor(1.0, hueDegrees, 0.85, 0.85).ToRgb())
        End Function

        ''' <summary>Dunkel nach hell in einer Farbe dieses Farbtons.</summary>
        Public Shared Function LuminanceOf(hueDegrees As Double) As IBrush
            Return Gradient(New HsvColor(1.0, hueDegrees, 0.7, 0.25).ToRgb(),
                            New HsvColor(1.0, hueDegrees, 0.7, 0.62).ToRgb(),
                            New HsvColor(1.0, hueDegrees, 0.35, 1.0).ToRgb())
        End Function

        Private Shared Function Gradient(ParamArray colors As Color()) As IBrush
            Dim brush As New LinearGradientBrush With {
                .StartPoint = New RelativePoint(0, 0.5, RelativeUnit.Relative),
                .EndPoint = New RelativePoint(1, 0.5, RelativeUnit.Relative)}
            For i = 0 To colors.Length - 1
                brush.GradientStops.Add(New GradientStop(colors(i), If(colors.Length = 1, 0.0, i / CDbl(colors.Length - 1))))
            Next
            Return brush
        End Function

    End Class

End Namespace
