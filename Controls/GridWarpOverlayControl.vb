Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media

Namespace Controls

    ''' <summary>Das Stuetzpunktraster der Gitterverzerrung.
    '''
    ''' Gezeichnet wird das VERSCHOBENE Raster, nicht das ursprungliche: man soll sehen, wohin man
    ''' gezogen hat, nicht wo man angefangen hat. Die Linien folgen deshalb den Punkten und sind
    ''' nach dem Ziehen krumm - genau wie das Bild darunter.</summary>
    Public Class GridWarpOverlayControl
        Inherits Control

        ''' <summary>Sichtbarer Radius der Griffe. Er soll zur Greifweite in der Ansicht passen -
        ''' ein Punkt, der kleiner aussieht als sein Fangbereich, laesst einen danebenzielen.</summary>
        Private Const HandleRadius As Double = 7.0

        ''' <summary>Die Punkte in EIGENEN Koordinaten (Pixel), zeilenweise ab links oben, dazu
        ''' vorne die Rastergroesse: [spalten, zeilen, x0, y0, x1, y1, ...]. Ein einziges Feld,
        ''' damit die Bindung EINE Eigenschaft ist und nicht drei, die auseinanderlaufen
        ''' koennen.</summary>
        Public Shared ReadOnly GridValuesProperty As StyledProperty(Of Double()) =
            AvaloniaProperty.Register(Of GridWarpOverlayControl, Double())(NameOf(GridValues), Nothing)

        Public Shared ReadOnly StrokeBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of GridWarpOverlayControl, IBrush)(NameOf(StrokeBrush),
                New SolidColorBrush(Color.FromRgb(240, 138, 26)))

        Shared Sub New()
            AffectsRender(Of GridWarpOverlayControl)(GridValuesProperty, StrokeBrushProperty)
        End Sub

        Public Property GridValues As Double()
            Get
                Return GetValue(GridValuesProperty)
            End Get
            Set(value As Double())
                SetValue(GridValuesProperty, value)
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
            Dim g = GridValues
            If g Is Nothing OrElse g.Length < 2 Then Return
            Dim columns = CInt(g(0)), rows = CInt(g(1))
            If columns < 1 OrElse rows < 1 Then Return
            Dim count = (columns + 1) * (rows + 1)
            If g.Length < 2 + count * 2 Then Return

            Dim punkt = Function(colIdx As Integer, rowIdx As Integer) As Point
                            Dim i = 2 + (rowIdx * (columns + 1) + colIdx) * 2
                            Return New Point(g(i), g(i + 1))
                        End Function

            ' Das Raster steht im Quellraum. Punkte, die im aktuellen Ausschnitt nicht vorkommen
            ' (weggeschnitten, leere Begradigungsecke), kommen als NaN an: sie werden weder
            ' gezeichnet noch als Linienende benutzt, sonst zoege die Linie ins Nirgendwo.
            Dim gilt = Function(p As Point) As Boolean
                           Return Not (Double.IsNaN(p.X) OrElse Double.IsNaN(p.Y))
                       End Function

            ' Zwei Stifte uebereinander, wie beim Verlaufs-Overlay: ein dunkler breiter darunter,
            ' damit das Raster auch auf hellem Bild sichtbar bleibt.
            Dim schatten = New Pen(New SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 2.6)
            Dim line = New Pen(StrokeBrush, 1.0)

            For Each stift In New Pen() {schatten, line}
                For rowIdx = 0 To rows
                    For colIdx = 0 To columns - 1
                        Dim a = punkt(colIdx, rowIdx), b = punkt(colIdx + 1, rowIdx)
                        If gilt(a) AndAlso gilt(b) Then context.DrawLine(stift, a, b)
                    Next
                Next
                For colIdx = 0 To columns
                    For rowIdx = 0 To rows - 1
                        Dim a = punkt(colIdx, rowIdx), b = punkt(colIdx, rowIdx + 1)
                        If gilt(a) AndAlso gilt(b) Then context.DrawLine(stift, a, b)
                    Next
                Next
            Next

            Dim fuellung = New SolidColorBrush(Color.FromArgb(230, 255, 255, 255))
            Dim border = New Pen(New SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1.0)
            For rowIdx = 0 To rows
                For colIdx = 0 To columns
                    Dim p = punkt(colIdx, rowIdx)
                    If gilt(p) Then context.DrawEllipse(fuellung, border, p, HandleRadius, HandleRadius)
                Next
            Next
        End Sub
    End Class

End Namespace
