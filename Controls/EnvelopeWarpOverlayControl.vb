Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media

Namespace Controls

    ''' <summary>Das Viereck des Verformen-Werkzeugs: vier Ecken, dazu je Kante zwei Griffe, die
    ''' den Rand zur Kurve biegen.
    '''
    ''' Gezeichnet wird, wohin die Raender GEWANDERT sind - nicht, wo sie ohne Verformung laegen.
    ''' Die Raender laufen als echte Bezierkurven, damit man die Kruemmung sieht, ohne sie am Bild
    ''' ablesen zu muessen; die Hilfslinien im Inneren kommen fertig ausgewertet aus dem ViewModel,
    ''' damit die Flaechenrechnung nur an EINER Stelle steht.</summary>
    Public Class EnvelopeWarpOverlayControl
        Inherits Control

        ''' <summary>Sichtbarer Radius der Eckgriffe. Er soll zur Greifweite passen - ein Punkt, der
        ''' kleiner aussieht als sein Fangbereich, laesst einen danebenzielen.</summary>
        Private Const CornerRadius As Double = 7.0

        ''' <summary>Die Kantengriffe sind kleiner: sie sind die Feinarbeit, die Ecken geben die
        ''' Lage vor.</summary>
        Private Const HandleRadius As Double = 5.0

        ''' <summary>Die zwoelf Anfasser in EIGENEN Koordinaten (Pixel): erst die vier Ecken links
        ''' oben, rechts oben, rechts unten, links unten, dann je Kante zwei Griffe in Laufrichtung
        ''' der Kante. Ein einziges Feld, damit die Bindung EINE Eigenschaft ist und nicht zwoelf,
        ''' die auseinanderlaufen koennen.</summary>
        Public Shared ReadOnly PointValuesProperty As StyledProperty(Of Double()) =
            AvaloniaProperty.Register(Of EnvelopeWarpOverlayControl, Double())(NameOf(PointValues), Nothing)

        ''' <summary>Die Hilfslinien im Inneren, im Format des Stuetzpunktrasters:
        ''' [spalten, zeilen, x0, y0, ...], ebenfalls in eigenen Koordinaten.</summary>
        Public Shared ReadOnly MeshValuesProperty As StyledProperty(Of Double()) =
            AvaloniaProperty.Register(Of EnvelopeWarpOverlayControl, Double())(NameOf(MeshValues), Nothing)

        Public Shared ReadOnly StrokeBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of EnvelopeWarpOverlayControl, IBrush)(NameOf(StrokeBrush),
                New SolidColorBrush(Color.FromRgb(240, 138, 26)))

        Shared Sub New()
            AffectsRender(Of EnvelopeWarpOverlayControl)(PointValuesProperty, MeshValuesProperty,
                                                         StrokeBrushProperty)
        End Sub

        Public Property PointValues As Double()
            Get
                Return GetValue(PointValuesProperty)
            End Get
            Set(value As Double())
                SetValue(PointValuesProperty, value)
            End Set
        End Property

        Public Property MeshValues As Double()
            Get
                Return GetValue(MeshValuesProperty)
            End Get
            Set(value As Double())
                SetValue(MeshValuesProperty, value)
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
            Dim v = PointValues
            If v Is Nothing OrElse v.Length < 24 Then Return
            For Each value In v
                If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return
            Next

            Dim p(11) As Point
            For i = 0 To 11
                p(i) = New Point(v(i * 2), v(i * 2 + 1))
            Next

            DrawMesh(context)

            ' Zwei Stifte uebereinander, wie bei den uebrigen Overlays: ein dunkler breiter
            ' darunter, damit der Rand auch auf hellem Bild sichtbar bleibt.
            Dim shadow = New Pen(New SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 3.0)
            Dim line = New Pen(StrokeBrush, 1.4)
            Dim outline = New StreamGeometry()
            Using sink = outline.Open()
                sink.BeginFigure(p(0), False)
                For edge = 0 To 3
                    Dim b = (edge + 1) Mod 4
                    sink.CubicBezierTo(p(4 + edge * 2), p(5 + edge * 2), p(b))
                Next
                sink.EndFigure(True)
            End Using
            For Each pen In New Pen() {shadow, line}
                context.DrawGeometry(Nothing, pen, outline)
            Next

            ' Die duennen Fuehrungen von der Ecke zu ihren beiden Griffen: ohne sie schwebten die
            ' Griffe frei im Bild und man saehe nicht, welche Kante sie biegen.
            Dim guide = New Pen(New SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 1.0)
            For edge = 0 To 3
                Dim b = (edge + 1) Mod 4
                context.DrawLine(guide, p(edge), p(4 + edge * 2))
                context.DrawLine(guide, p(b), p(5 + edge * 2))
            Next

            Dim fill = New SolidColorBrush(Color.FromArgb(235, 255, 255, 255))
            Dim border = New Pen(New SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), 1.2)
            ' Die Kantengriffe zuerst, damit eine Ecke, auf der ein Griff liegt, obenauf bleibt -
            ' sie ist die groebere Bewegung und wird zuerst gesucht.
            For i = 4 To 11
                context.DrawEllipse(fill, border, p(i), HandleRadius, HandleRadius)
            Next
            For i = 0 To 3
                context.DrawEllipse(fill, border, p(i), CornerRadius, CornerRadius)
            Next
        End Sub

        ''' <summary>Die Hilfslinien im Inneren. Randreihen ausgelassen: dort laufen die Kurven, und
        ''' eine gerade Sehne daneben liesse die Kruemmung falsch aussehen.</summary>
        Private Sub DrawMesh(context As DrawingContext)
            Dim g = MeshValues
            If g Is Nothing OrElse g.Length < 2 Then Return
            Dim columns = CInt(g(0)), rows = CInt(g(1))
            If columns < 2 OrElse rows < 2 Then Return
            If g.Length < 2 + (columns + 1) * (rows + 1) * 2 Then Return

            Dim pt = Function(colIdx As Integer, rowIdx As Integer) As Point
                         Dim i = 2 + (rowIdx * (columns + 1) + colIdx) * 2
                         Return New Point(g(i), g(i + 1))
                     End Function
            Dim gilt = Function(a As Point) As Boolean
                           Return Not (Double.IsNaN(a.X) OrElse Double.IsNaN(a.Y))
                       End Function

            Dim hilfe = New Pen(New SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1.0)
            For rowIdx = 1 To rows - 1
                For colIdx = 0 To columns - 1
                    Dim a = pt(colIdx, rowIdx), b = pt(colIdx + 1, rowIdx)
                    If gilt(a) AndAlso gilt(b) Then context.DrawLine(hilfe, a, b)
                Next
            Next
            For colIdx = 1 To columns - 1
                For rowIdx = 0 To rows - 1
                    Dim a = pt(colIdx, rowIdx), b = pt(colIdx, rowIdx + 1)
                    If gilt(a) AndAlso gilt(b) Then context.DrawLine(hilfe, a, b)
                Next
            Next
        End Sub
    End Class

End Namespace
