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
        Private Const GriffRadius As Double = 7.0

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
            Dim spalten = CInt(g(0)), zeilen = CInt(g(1))
            If spalten < 1 OrElse zeilen < 1 Then Return
            Dim anzahl = (spalten + 1) * (zeilen + 1)
            If g.Length < 2 + anzahl * 2 Then Return

            Dim punkt = Function(si As Integer, zi As Integer) As Point
                            Dim i = 2 + (zi * (spalten + 1) + si) * 2
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
            Dim linie = New Pen(StrokeBrush, 1.0)

            For Each stift In New Pen() {schatten, linie}
                For zi = 0 To zeilen
                    For si = 0 To spalten - 1
                        Dim a = punkt(si, zi), b = punkt(si + 1, zi)
                        If gilt(a) AndAlso gilt(b) Then context.DrawLine(stift, a, b)
                    Next
                Next
                For si = 0 To spalten
                    For zi = 0 To zeilen - 1
                        Dim a = punkt(si, zi), b = punkt(si, zi + 1)
                        If gilt(a) AndAlso gilt(b) Then context.DrawLine(stift, a, b)
                    Next
                Next
            Next

            Dim fuellung = New SolidColorBrush(Color.FromArgb(230, 255, 255, 255))
            Dim rand = New Pen(New SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1.0)
            For zi = 0 To zeilen
                For si = 0 To spalten
                    Dim p = punkt(si, zi)
                    If gilt(p) Then context.DrawEllipse(fuellung, rand, p, GriffRadius, GriffRadius)
                Next
            Next
        End Sub
    End Class

End Namespace
