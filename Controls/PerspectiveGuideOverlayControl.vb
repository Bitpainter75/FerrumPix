Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media

Namespace Controls

    ''' <summary>Die Perspektivlinien der gefuehrten Perspektive.
    '''
    ''' Jede Linie ist eine Strecke mit zwei Griffen. Die Farbe sagt die Rolle: senkrecht in der
    ''' Akzentfarbe, waagerecht hell. Die Rolle folgt dem Winkel, und man soll sehen, wofuer eine
    ''' Linie genommen wird, bevor man sich ueber das Ergebnis wundert. Die gefasste oder zuletzt
    ''' gesetzte Linie ist dicker - sie ist die, die ENTF wegnimmt.</summary>
    Public Class PerspectiveGuideOverlayControl
        Inherits Control

        ''' <summary>Sichtbarer Radius der Griffe, passend zur Greifweite der Ansicht.</summary>
        Private Const HandleRadius As Double = 7.0

        ''' <summary>Die Linien in EIGENEN Koordinaten (Pixel): [Anzahl, gefasste Linie, dann je Linie
        ''' x0, y0, x1, y1, Rolle (1 senkrecht, 0 waagerecht)]. Ein einziges Feld, damit die Bindung
        ''' EINE Eigenschaft ist.</summary>
        Public Shared ReadOnly GuideValuesProperty As StyledProperty(Of Double()) =
            AvaloniaProperty.Register(Of PerspectiveGuideOverlayControl, Double())(NameOf(GuideValues), Nothing)

        Public Shared ReadOnly StrokeBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of PerspectiveGuideOverlayControl, IBrush)(NameOf(StrokeBrush),
                New SolidColorBrush(Color.FromRgb(240, 138, 26)))

        Shared Sub New()
            AffectsRender(Of PerspectiveGuideOverlayControl)(GuideValuesProperty, StrokeBrushProperty)
        End Sub

        Public Property GuideValues As Double()
            Get
                Return GetValue(GuideValuesProperty)
            End Get
            Set(value As Double())
                SetValue(GuideValuesProperty, value)
            End Set
        End Property

        Public Property StrokeBrush As IBrush
            Get
                Return GetValue(StrokeBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(StrokeBrushProperty, value)
            End Set
        End Property

        Public Overrides Sub Render(context As DrawingContext)
            Dim g = GuideValues
            If g Is Nothing OrElse g.Length < 2 Then Return
            Dim count = CInt(g(0))
            Dim selected = CInt(g(1))
            If count < 1 OrElse g.Length < 2 + count * 5 Then Return

            Dim shadow = New Pen(New SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 3.4)
            Dim shadowThick = New Pen(New SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 5.0)
            Dim lightBrush = New SolidColorBrush(Color.FromArgb(235, 255, 255, 255))
            Dim fill = New SolidColorBrush(Color.FromArgb(230, 255, 255, 255))
            Dim border = New Pen(New SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1.0)

            For i = 0 To count - 1
                Dim b = 2 + i * 5
                Dim pa = New Point(g(b), g(b + 1))
                Dim pb = New Point(g(b + 2), g(b + 3))
                ' Ein Endpunkt ohne Anzeigeort kommt als NaN: die Linie zoege sonst ins Nirgendwo.
                If Double.IsNaN(pa.X) OrElse Double.IsNaN(pa.Y) OrElse Double.IsNaN(pb.X) OrElse Double.IsNaN(pb.Y) Then Continue For
                Dim isVertical = g(b + 4) > 0.5
                Dim isSelected = (i = selected)
                Dim brush As IBrush = If(isVertical, StrokeBrush, lightBrush)
                Dim stroke = New Pen(brush, If(isSelected, 3.0, 1.8))
                context.DrawLine(If(isSelected, shadowThick, shadow), pa, pb)
                context.DrawLine(stroke, pa, pb)
                Dim handleFill As IBrush = If(isSelected, brush, fill)
                context.DrawEllipse(handleFill, border, pa, HandleRadius, HandleRadius)
                context.DrawEllipse(handleFill, border, pb, HandleRadius, HandleRadius)
            Next
        End Sub
    End Class

End Namespace
