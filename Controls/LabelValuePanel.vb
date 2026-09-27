Imports Avalonia
Imports Avalonia.Controls

Namespace Controls

    ''' <summary>Eine Zeile der Info-Leiste: Beschriftung links, Wert rechtsbuendig.
    '''
    ''' WARUM EIN EIGENES PANEL. Vorher standen alle Zeilen in EINEM Raster mit einer gemeinsamen
    ''' Spalte fuer die Beschriftungen (Auto). Die Breite dieser Spalte bestimmte die laengste
    ''' Beschriftung, auch in einer Zeile, die sie gar nicht hat: neben "Belichtungskorrektur" blieb
    ''' fuer "Canon EOS M50" in der Zeile "Kamera" zu wenig Platz, und der Name brach um. In einer
    ''' schmalen Leiste liefen Werte ohne Leerzeichen (ein Datum) ueber den Rand hinaus.
    '''
    ''' DIE REGEL. Jede Zeile teilt ihre Breite fuer sich. Passen beide nebeneinander, stehen sie in
    ''' einer Zeile. Sonst bekommt der WERT den Vorrang: die Beschriftung behaelt hoechstens den Teil,
    ''' den der Wert nicht braucht, mindestens aber 45 Prozent, und bricht dort um; der Wert bricht
    ''' im Rest um und steht rechtsbuendig.
    '''
    ''' Erwartet genau zwei Kinder: zuerst die Beschriftung, dann den Wert.</summary>
    Public Class LabelValuePanel
        Inherits Panel

        Public Shared ReadOnly SpacingProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of LabelValuePanel, Double)(NameOf(Spacing), 10.0)

        Private Const MinimumLabelShare As Double = 0.45

        Private _labelWidth As Double
        Private _valueWidth As Double

        Shared Sub New()
            AffectsMeasure(Of LabelValuePanel)(SpacingProperty)
        End Sub

        Public Property Spacing As Double
            Get
                Return GetValue(SpacingProperty)
            End Get
            Set(value As Double)
                SetValue(SpacingProperty, value)
            End Set
        End Property

        Protected Overrides Function MeasureOverride(availableSize As Size) As Size
            If Children.Count < 2 Then Return MyBase.MeasureOverride(availableSize)
            Dim label = Children(0)
            Dim value = Children(1)
            Dim infinite As New Size(Double.PositiveInfinity, Double.PositiveInfinity)
            label.Measure(infinite)
            value.Measure(infinite)
            Dim labelNatural = label.DesiredSize.Width
            Dim valueNatural = value.DesiredSize.Width
            Dim total = availableSize.Width

            If Double.IsInfinity(total) OrElse labelNatural + Spacing + valueNatural <= total Then
                _labelWidth = labelNatural
                _valueWidth = valueNatural
                Return New Size(If(Double.IsInfinity(total), labelNatural + Spacing + valueNatural, total),
                                Math.Max(label.DesiredSize.Height, value.DesiredSize.Height))
            End If

            Dim room = Math.Max(0, total - Spacing)
            _labelWidth = Math.Min(labelNatural, Math.Max(room * MinimumLabelShare, room - valueNatural))
            _valueWidth = Math.Max(0, room - _labelWidth)
            label.Measure(New Size(_labelWidth, Double.PositiveInfinity))
            value.Measure(New Size(_valueWidth, Double.PositiveInfinity))
            Return New Size(total, Math.Max(label.DesiredSize.Height, value.DesiredSize.Height))
        End Function

        Protected Overrides Function ArrangeOverride(finalSize As Size) As Size
            If Children.Count < 2 Then Return MyBase.ArrangeOverride(finalSize)
            Dim label = Children(0)
            Dim value = Children(1)
            label.Arrange(New Rect(0, 0, Math.Min(_labelWidth, finalSize.Width), finalSize.Height))
            ' Rechtsbuendig: der Wert endet an der rechten Kante, gleich wie breit er ist. Mehrzeilig
            ' richtet ihn TextAlignment="Right" am Wert selbst aus.
            ' Aufgerundet: mit Pixelrundung wird aus x 59,5 und Breite 60,5 sonst 60 und 61, und der
            ' Wert stand einen Punkt ueber den Rand.
            Dim width = Math.Min(Math.Ceiling(Math.Max(value.DesiredSize.Width, 0)), finalSize.Width)
            value.Arrange(New Rect(finalSize.Width - width, 0, width, finalSize.Height))
            Return finalSize
        End Function

    End Class

End Namespace
