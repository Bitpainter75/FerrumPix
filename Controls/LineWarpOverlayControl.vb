Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media

Namespace Controls

    ''' <summary>Die Linien der Linienverzerrung.
    '''
    ''' Jede Linie steht zweimal auf dem Bild: gestrichelt und blass dort, wo sie im Bild LIEGT, und
    ''' durchgezogen mit Griffen dort, wohin sie GEZOGEN wurde. Dazwischen eine duenne Verbindung.
    ''' Ohne diese Doppelung sieht man beim Ziehen zwar, wo die Linie jetzt ist, aber nicht mehr,
    ''' worauf sie ursprunglich lag - und genau das ist die Angabe, die die Verzerrung bestimmt.
    '''
    ''' Solange eine Linie nicht bewegt wurde, liegen beide aufeinander. Dann wird nur die
    ''' durchgezogene gezeichnet, sonst saehe man an jeder frisch gelegten Linie ein Doppelbild.</summary>
    Public Class LineWarpOverlayControl
        Inherits Control

        ''' <summary>Sichtbarer Radius der Griffe an den Enden. Er soll zur Greifweite in der Ansicht
        ''' passen - ein Punkt, der kleiner aussieht als sein Fangbereich, laesst einen
        ''' danebenzielen.</summary>
        Private Const HandleRadius As Double = 7.0

        ''' <summary>Die Linien in EIGENEN Koordinaten (Pixel): [Anzahl, dann je Linie QuelleAx,
        ''' QuelleAy, QuelleBx, QuelleBy, ZielAx, ZielAy, ZielBx, ZielBy]. Ein einziges Feld, damit
        ''' die Bindung EINE Eigenschaft ist und nicht mehrere, die auseinanderlaufen koennen.</summary>
        Public Shared ReadOnly LineValuesProperty As StyledProperty(Of Double()) =
            AvaloniaProperty.Register(Of LineWarpOverlayControl, Double())(NameOf(LineValues), Nothing)

        Public Shared ReadOnly StrokeBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of LineWarpOverlayControl, IBrush)(NameOf(StrokeBrush),
                New SolidColorBrush(Color.FromRgb(240, 138, 26)))

        Shared Sub New()
            AffectsRender(Of LineWarpOverlayControl)(LineValuesProperty, StrokeBrushProperty)
        End Sub

        Public Property LineValues As Double()
            Get
                Return GetValue(LineValuesProperty)
            End Get
            Set(value As Double())
                SetValue(LineValuesProperty, value)
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
            Dim g = LineValues
            If g Is Nothing OrElse g.Length < 1 Then Return
            Dim count = CInt(g(0))
            If count < 1 OrElse g.Length < 1 + count * 8 Then Return

            ' Punkte ohne Anzeigeort (weggeschnitten, leere Begradigungsecke) kommen als NaN an: eine
            ' Linie mit einem solchen Ende wird gar nicht gezeichnet, sonst zoege sie ins Nirgendwo.
            Dim gilt = Function(p As Point) As Boolean
                           Return Not (Double.IsNaN(p.X) OrElse Double.IsNaN(p.Y))
                       End Function

            ' Zwei Stifte uebereinander, wie beim Raster: ein dunkler breiter darunter, damit die
            ' Linien auch auf hellem Bild sichtbar bleiben.
            Dim schatten = New Pen(New SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 3.4)
            Dim targetLine = New Pen(StrokeBrush, 1.6)
            ' Der Strichabstand ist ein Vielfaches der Stiftbreite - bei einem duenneren Stift
            ' ergaebe dieselbe Zahl ein anderes Muster.
            Dim sourceLine = New Pen(New SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1.0,
                                     New DashStyle(New Double() {4, 4}, 0))
            Dim verbindung = New Pen(New SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1.0,
                                     New DashStyle(New Double() {2, 3}, 0))

            Dim fuellung = New SolidColorBrush(Color.FromArgb(230, 255, 255, 255))
            Dim border = New Pen(New SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 1.0)

            For i = 0 To count - 1
                Dim b = 1 + i * 8
                Dim qa = New Point(g(b), g(b + 1))
                Dim qb = New Point(g(b + 2), g(b + 3))
                Dim za = New Point(g(b + 4), g(b + 5))
                Dim zb = New Point(g(b + 6), g(b + 7))
                If Not (gilt(qa) AndAlso gilt(qb) AndAlso gilt(za) AndAlso gilt(zb)) Then Continue For

                Dim moved = Math.Abs(za.X - qa.X) > 0.5 OrElse Math.Abs(za.Y - qa.Y) > 0.5 OrElse
                             Math.Abs(zb.X - qb.X) > 0.5 OrElse Math.Abs(zb.Y - qb.Y) > 0.5

                If moved Then
                    ' Erst die Herkunft, dann die Verbindung, dann das Ziel - in dieser Reihenfolge,
                    ' damit das Ziel oben liegt und beim Ziehen nicht verdeckt wird.
                    context.DrawLine(sourceLine, qa, qb)
                    context.DrawLine(verbindung, qa, za)
                    context.DrawLine(verbindung, qb, zb)
                End If

                context.DrawLine(schatten, za, zb)
                context.DrawLine(targetLine, za, zb)
                context.DrawEllipse(fuellung, border, za, HandleRadius, HandleRadius)
                context.DrawEllipse(fuellung, border, zb, HandleRadius, HandleRadius)
            Next
        End Sub
    End Class

End Namespace
