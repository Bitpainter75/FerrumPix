Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media

Namespace Controls

    ''' <summary>Griffe und Achse einer Verlaufsmaske.
    '''
    ''' Die DECKUNG selbst zeigt das rote Masken-Overlay (dasselbe Bild wie beim Masken-Pinsel) -
    ''' dieses Steuerelement zeichnet nur, was man ANFASSEN kann. Deshalb bewusst zurückhaltend:
    ''' eine durchgehende Achse zwischen zwei runden Griffen, dazu eine dünne Begrenzung des
    ''' Übergangsbereichs. Keine gestrichelten Linienbündel - die Deckung ist schon farbig zu sehen,
    ''' und doppelte Information macht das Bild nur unruhig.</summary>
    Public Class GradientMaskOverlayControl
        Inherits Control

        Private Const HandleRadius As Double = 7.0

        ''' <summary>Achse in EIGENEN Koordinaten (Pixel): Start X/Y, Ende X/Y, dann Stauchung,
        ''' Übergang in Prozent, 1 = radial, 1 = umgekehrt. Genau der Aufbau, den
        ''' EditorViewModel.GradientGeometry liefert - nur von Prozent auf Pixel gerechnet.</summary>
        Public Shared ReadOnly GeometryValuesProperty As StyledProperty(Of Double()) =
            AvaloniaProperty.Register(Of GradientMaskOverlayControl, Double())(NameOf(GeometryValues), Nothing)

        Public Shared ReadOnly StrokeBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of GradientMaskOverlayControl, IBrush)(NameOf(StrokeBrush),
                New SolidColorBrush(Color.FromRgb(240, 138, 26)))

        Shared Sub New()
            AffectsRender(Of GradientMaskOverlayControl)(GeometryValuesProperty, StrokeBrushProperty)
        End Sub

        Public Property GeometryValues As Double()
            Get
                Return GetValue(GeometryValuesProperty)
            End Get
            Set(value As Double())
                SetValue(GeometryValuesProperty, value)
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
            Dim g = GeometryValues
            If g Is Nothing OrElse g.Length < 8 Then Return
            Dim a = New Point(g(0), g(1))
            Dim b = New Point(g(2), g(3))
            Dim ratio = g(4)
            Dim uebergang = Math.Max(0.0, Math.Min(100.0, g(5))) / 100.0
            Dim istRadial = g(6) > 0.5

            ' Zwei Stifte übereinander: ein dunkler, breiter darunter, damit die Achse auch auf
            ' hellem Himmel sichtbar bleibt (dieselbe Überlegung wie bei den Laufameisen).
            Dim schatten = New Pen(New SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 3.0)
            Dim linie = New Pen(StrokeBrush, 1.4)
            Dim zart = New Pen(New SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 1.0)

            Dim dx = b.X - a.X, dy = b.Y - a.Y
            Dim laenge = Math.Sqrt(dx * dx + dy * dy)
            If laenge < 0.5 Then Return
            Dim ex = dx / laenge, ey = dy / laenge

            If istRadial Then
                ' Aussenkante der Ellipse und die innere Grenze des Übergangs.
                Dim r2 = Math.Max(0.05, ratio) * laenge
                Dim innen = Math.Max(0.0, 1.0 - uebergang)
                ZeichneEllipse(context, schatten, a, laenge, r2, ex, ey)
                ZeichneEllipse(context, linie, a, laenge, r2, ex, ey)
                If innen > 0.02 Then ZeichneEllipse(context, zart, a, laenge * innen, r2 * innen, ex, ey)
            Else
                context.DrawLine(schatten, a, b)
                context.DrawLine(linie, a, b)
                ' Grenzen des Übergangs: zwei kurze Querstriche senkrecht zur Achse. Sie machen
                ' sichtbar, was der Regler "Übergang" tut, ohne die ganze Bildbreite zu queren.
                Dim halb = laenge * uebergang / 2.0
                Dim mitte = New Point((a.X + b.X) / 2.0, (a.Y + b.Y) / 2.0)
                Dim quer = 26.0
                For Each s In New Double() {-halb, halb}
                    Dim p = New Point(mitte.X + ex * s, mitte.Y + ey * s)
                    Dim p1 = New Point(p.X - ey * quer, p.Y + ex * quer)
                    Dim p2 = New Point(p.X + ey * quer, p.Y - ex * quer)
                    context.DrawLine(zart, p1, p2)
                Next
            End If

            ZeichneGriff(context, a, False)
            ZeichneGriff(context, b, True)
            ' Beim radialen Verlauf zusaetzlich ein Griff QUER zur Achse: dort endet die zweite
            ' Halbachse, und daran laesst sich die Stauchung ziehen. Ohne ihn waere sie das einzige
            ' Mass der Ellipse, das nur der Regler kann.
            If istRadial Then
                Dim r2 = Math.Max(0.05, ratio) * laenge
                ZeichneGriff(context, New Point(a.X - ey * r2, a.Y + ex * r2), True)
            End If
        End Sub

        Private Shared Sub ZeichneEllipse(context As DrawingContext, stift As Pen, mitte As Point,
                                          r1 As Double, r2 As Double, ex As Double, ey As Double)
            ' Avalonia kennt keine gedrehte Ellipse als Grundform - der Umriss wird deshalb aus
            ' Stützpunkten gezogen. 72 Schritte sind auch bei Vollbild-Ellipsen glatt.
            Dim geo As New StreamGeometry()
            Using ctx = geo.Open()
                For i = 0 To 72
                    Dim w = i / 72.0 * 2.0 * Math.PI
                    Dim lx = Math.Cos(w) * r1, ly = Math.Sin(w) * r2
                    Dim p = New Point(mitte.X + lx * ex - ly * ey, mitte.Y + lx * ey + ly * ex)
                    If i = 0 Then ctx.BeginFigure(p, False) Else ctx.LineTo(p)
                Next
                ctx.EndFigure(True)
            End Using
            context.DrawGeometry(Nothing, stift, geo)
        End Sub

        ''' <summary>Der Endpunkt ist gefüllt, der Startpunkt hohl - so sieht man auch bei
        ''' gedrehtem Verlauf sofort, welches Ende die volle Wirkung trägt.</summary>
        Private Sub ZeichneGriff(context As DrawingContext, p As Point, gefuellt As Boolean)
            Dim rand = New Pen(New SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), 2.5)
            Dim stift = New Pen(StrokeBrush, 1.6)
            context.DrawEllipse(Nothing, rand, p, HandleRadius, HandleRadius)
            context.DrawEllipse(If(gefuellt, StrokeBrush, New SolidColorBrush(Color.FromArgb(70, 255, 255, 255))),
                                stift, p, HandleRadius, HandleRadius)
        End Sub

    End Class

End Namespace
