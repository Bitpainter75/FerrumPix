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

            ' NaN heisst: dieser eine Punkt hat nach Zuschnitt oder Begradigung keinen Anzeigeort.
            ' Er wird UEBERSPRUNGEN, nicht geklemmt, und er nimmt die uebrigen elf nicht mit. Vorher
            ' brach ein einziger NaN-Wert das ganze Zeichnen ab: das Overlay verschwand samt allen
            ' Anfassern, und das Werkzeug wirkte tot, obwohl TryBeginEnvelopeDrag NaN-Anfasser
            ' laengst einzeln uebergeht. Dieselbe Behandlung wie beim Stuetzpunktraster.
            Dim p(11) As Point
            Dim valid(11) As Boolean
            For i = 0 To 11
                Dim px = v(i * 2), py = v(i * 2 + 1)
                valid(i) = Not (Double.IsNaN(px) OrElse Double.IsNaN(py) OrElse
                                Double.IsInfinity(px) OrElse Double.IsInfinity(py))
                If valid(i) Then p(i) = New Point(px, py)
            Next

            DrawMesh(context)

            ' Zwei Stifte uebereinander, wie bei den uebrigen Overlays: ein dunkler breiter
            ' darunter, damit der Rand auch auf hellem Bild sichtbar bleibt.
            '
            ' Der gespeicherte Warp wird als regelmaessiges Mesh gerendert. Der Rahmen muss
            ' deshalb dieselben Randsegmente zeigen: eine ideale CubicBezier sah beim Ziehen etwas
            ' anders aus als die nach dem Loslassen sichtbare Mesh-Kante, besonders bei stark
            ' gebogenen Rändern. Die zwölf Punkte bleiben nur die Bediengriffe; die Mesh-Ränder
            ' sind die verbindliche Vorschau des Ergebnisses.
            Dim shadow = New Pen(New SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 3.0)
            Dim line = New Pen(StrokeBrush, 1.4)
            Dim outline = New StreamGeometry()
            Dim hasOutline = False
            Using sink = outline.Open()
                Dim mesh = MeshValues
                Dim meshColumns = If(mesh IsNot Nothing AndAlso mesh.Length >= 2, CInt(mesh(0)), 0)
                Dim meshRows = If(mesh IsNot Nothing AndAlso mesh.Length >= 2, CInt(mesh(1)), 0)
                Dim meshComplete = meshColumns > 0 AndAlso meshRows > 0 AndAlso
                                   mesh.Length >= 2 + (meshColumns + 1) * (meshRows + 1) * 2
                Dim meshPoint = Function(colIdx As Integer, rowIdx As Integer) As Point
                                    Dim i = 2 + (rowIdx * (meshColumns + 1) + colIdx) * 2
                                    Return New Point(mesh(i), mesh(i + 1))
                                End Function
                Dim finite = Function(point As Point) Not (Double.IsNaN(point.X) OrElse Double.IsNaN(point.Y) OrElse
                                                            Double.IsInfinity(point.X) OrElse Double.IsInfinity(point.Y))

                If meshComplete Then
                    Dim edges = New (StartCol As Integer, StartRow As Integer, DeltaCol As Integer, DeltaRow As Integer, Count As Integer)() {
                        (0, 0, 1, 0, meshColumns),
                        (meshColumns, 0, 0, 1, meshRows),
                        (meshColumns, meshRows, -1, 0, meshColumns),
                        (0, meshRows, 0, -1, meshRows)}
                    For Each edge In edges
                        Dim first = meshPoint(edge.StartCol, edge.StartRow)
                        If Not finite(first) Then Continue For
                        sink.BeginFigure(first, False)
                        Dim complete = True
                        For stepIndex = 1 To edge.Count
                            Dim point = meshPoint(edge.StartCol + edge.DeltaCol * stepIndex,
                                                  edge.StartRow + edge.DeltaRow * stepIndex)
                            If Not finite(point) Then
                                complete = False
                                Exit For
                            End If
                            sink.LineTo(point)
                        Next
                        sink.EndFigure(False)
                        hasOutline = hasOutline OrElse complete
                    Next
                Else
                    ' Rueckfall fuer alte/teilweise initialisierte Overlays: die Bediengriffe
                    ' bleiben auch ohne Mesh sichtbar und greifbar.
                    For edge = 0 To 3
                        Dim b = (edge + 1) Mod 4
                        If Not (valid(edge) AndAlso valid(b) AndAlso valid(4 + edge * 2) AndAlso valid(5 + edge * 2)) Then Continue For
                        sink.BeginFigure(p(edge), False)
                        sink.CubicBezierTo(p(4 + edge * 2), p(5 + edge * 2), p(b))
                        sink.EndFigure(False)
                        hasOutline = True
                    Next
                End If
            End Using
            If hasOutline Then
                For Each pen In New Pen() {shadow, line}
                    context.DrawGeometry(Nothing, pen, outline)
                Next
            End If

            ' Die duennen Fuehrungen von der Ecke zu ihren beiden Griffen: ohne sie schwebten die
            ' Griffe frei im Bild und man saehe nicht, welche Kante sie biegen.
            Dim guide = New Pen(New SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 1.0)
            For edge = 0 To 3
                Dim b = (edge + 1) Mod 4
                If valid(edge) AndAlso valid(4 + edge * 2) Then context.DrawLine(guide, p(edge), p(4 + edge * 2))
                If valid(b) AndAlso valid(5 + edge * 2) Then context.DrawLine(guide, p(b), p(5 + edge * 2))
            Next

            Dim fill = New SolidColorBrush(Color.FromArgb(235, 255, 255, 255))
            Dim border = New Pen(New SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), 1.2)
            ' Die Kantengriffe zuerst, damit eine Ecke, auf der ein Griff liegt, obenauf bleibt -
            ' sie ist die groebere Bewegung und wird zuerst gesucht.
            For i = 4 To 11
                If valid(i) Then context.DrawEllipse(fill, border, p(i), HandleRadius, HandleRadius)
            Next
            For i = 0 To 3
                If valid(i) Then context.DrawEllipse(fill, border, p(i), CornerRadius, CornerRadius)
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
            ' Das Ergebnisraster kann 48x48 fein sein. Alle Linien zu zeichnen verdeckte dann
            ' das Bild; vier Zwischenlinien genügen als Orientierung. Die Randsegmente zeichnet
            ' Render dagegen oben exakt, damit sie mit dem gespeicherten Warp übereinstimmen.
            Dim rowStride = Math.Max(1, CInt(Math.Ceiling(rows / 4.0)))
            Dim columnStride = Math.Max(1, CInt(Math.Ceiling(columns / 4.0)))
            For rowIdx = rowStride To rows - 1 Step rowStride
                For colIdx = 0 To columns - 1
                    Dim a = pt(colIdx, rowIdx), b = pt(colIdx + 1, rowIdx)
                    If gilt(a) AndAlso gilt(b) Then context.DrawLine(hilfe, a, b)
                Next
            Next
            For colIdx = columnStride To columns - 1 Step columnStride
                For rowIdx = 0 To rows - 1
                    Dim a = pt(colIdx, rowIdx), b = pt(colIdx, rowIdx + 1)
                    If gilt(a) AndAlso gilt(b) Then context.DrawLine(hilfe, a, b)
                Next
            Next
        End Sub
    End Class

End Namespace
