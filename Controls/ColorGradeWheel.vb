Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Data
Imports Avalonia.Input
Imports Avalonia.Media
Imports Avalonia.Rendering

Namespace Controls

    ''' <summary>
    ''' Farbrad für die Farbgradierung: eine gefüllte Scheibe, auf der der WINKEL den Farbton und der
    ''' RADIUS die Sättigung bestimmt. Mitte = ungetönt.
    '''
    ''' Bewusst NICHT <see cref="ColorWheel"/> mitbenutzt, obwohl beide rund sind: jenes ist ein
    ''' Farbwähler (Farbtonring plus Sättigung/Helligkeit-Feld) und liefert eine fertige Farbe. Hier
    ''' werden zwei unabhängige Reglerwerte gesetzt, und eine Helligkeitsachse gibt es nicht - die
    ''' Luminanz ist ein eigener Regler neben dem Rad, weil sie in der Pipeline an anderer Stelle
    ''' wirkt als die Tönung.
    '''
    ''' Der Farbton wird als eigenes Feld gehalten: bei Sättigung 0 hat die Position keinen Farbton
    ''' mehr, der Zeiger spränge sonst bei jedem Nulldurchgang auf Rot zurück (dieselbe Falle wie im
    ''' ColorWheel).
    ''' </summary>
    Public Class ColorGradeWheel
        Inherits Control
        Implements ICustomHitTest

        Public Shared ReadOnly HueProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of ColorGradeWheel, Double)(NameOf(Hue), 0.0, defaultBindingMode:=BindingMode.TwoWay)

        Public Shared ReadOnly SaturationProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of ColorGradeWheel, Double)(NameOf(Saturation), 0.0, defaultBindingMode:=BindingMode.TwoWay)

        Private _dragging As Boolean = False

        Shared Sub New()
            AffectsRender(Of ColorGradeWheel)(HueProperty, SaturationProperty)
        End Sub

        Public Sub New()
            Cursor = New Cursor(StandardCursorType.Cross)
            MinWidth = 76
            MinHeight = 76
        End Sub

        ''' <summary>Farbton in Grad (0-360).</summary>
        Public Property Hue As Double
            Get
                Return GetValue(HueProperty)
            End Get
            Set(value As Double)
                SetValue(HueProperty, value)
            End Set
        End Property

        ''' <summary>Sättigung in Prozent (0-100) - zugleich der Abstand vom Mittelpunkt.</summary>
        Public Property Saturation As Double
            Get
                Return GetValue(SaturationProperty)
            End Get
            Set(value As Double)
                SetValue(SaturationProperty, value)
            End Set
        End Property

        Public Function HitTest(point As Point) As Boolean Implements ICustomHitTest.HitTest
            Return New Rect(Bounds.Size).Contains(point)
        End Function

        ' ---- Geometrie ----

        Private ReadOnly Property Center As Point
            Get
                Return New Point(Bounds.Width / 2.0, Bounds.Height / 2.0)
            End Get
        End Property

        Private ReadOnly Property Radius As Double
            Get
                Return Math.Min(Bounds.Width, Bounds.Height) / 2.0 - 1
            End Get
        End Property

        ' ---- Zeichnen ----

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)
            If Radius <= 0 Then Return

            DrawDisc(context)
            DrawMarker(context)
        End Sub

        ''' Die Scheibe entsteht aus Keilen wie der Ring im ColorWheel; der Sättigungsverlauf nach
        ''' innen kommt anschließend als ein einziger radialer Weiß-Verlauf darüber. Ein Verlauf je
        ''' Keil wäre sichtbar gebändert und deutlich teurer.
        Private Sub DrawDisc(context As DrawingContext)
            Const stepDeg As Double = 3.0
            Dim segments = CInt(360.0 / stepDeg)
            For i = 0 To segments - 1
                Dim startDeg = i * stepDeg
                Dim hue = (startDeg + stepDeg / 2.0 + 360.0) Mod 360.0
                Dim segmentColor = New HsvColor(1.0, hue, 1.0, 1.0).ToRgb()
                ' Die halbe Grad Überlappung verhindert Haarrisse zwischen den Keilen.
                Dim geometry = BuildWedge(Center, Radius, startDeg - 0.5, startDeg + stepDeg + 0.5)
                context.DrawGeometry(New SolidColorBrush(segmentColor), Nothing, geometry)
            Next

            Dim toCenter As New RadialGradientBrush With {
                .Center = New RelativePoint(0.5, 0.5, RelativeUnit.Relative),
                .GradientOrigin = New RelativePoint(0.5, 0.5, RelativeUnit.Relative)
            }
            toCenter.GradientStops.Add(New GradientStop(Colors.White, 0))
            toCenter.GradientStops.Add(New GradientStop(Avalonia.Media.Color.FromArgb(0, 255, 255, 255), 1))
            context.DrawEllipse(toCenter, Nothing, Center, Radius, Radius)

            ' Dünner Rand, damit die Scheibe sich vom Panelhintergrund abhebt.
            context.DrawEllipse(Nothing, New Pen(New SolidColorBrush(Avalonia.Media.Color.FromArgb(60, 0, 0, 0)), 1),
                                Center, Radius, Radius)
        End Sub

        ''' <summary>Mittelpunkt des Selektionskreises (Marker) für aktuelle Hue/Saturation. Bei
        ''' Sättigung 0 liegt er genau im Zentrum. Von DrawMarker UND dem Doppelklick-Reset genutzt.</summary>
        Private Function MarkerPoint() As Point
            Dim sat = Math.Max(0.0, Math.Min(1.0, Saturation / 100.0))
            Dim rad = Hue * Math.PI / 180.0
            Return New Point(Center.X + Radius * sat * Math.Cos(rad), Center.Y + Radius * sat * Math.Sin(rad))
        End Function

        ' Trefferradius des Selektionskreises (5-px-Ring, für den Doppelklick etwas großzügiger).
        Private Const MarkerHitRadius As Double = 10.0

        Private Function IsOnMarker(p As Point) As Boolean
            Dim m = MarkerPoint()
            Dim dx = p.X - m.X, dy = p.Y - m.Y
            Return dx * dx + dy * dy <= MarkerHitRadius * MarkerHitRadius
        End Function

        Private Sub DrawMarker(context As DrawingContext)
            Dim p = MarkerPoint()
            context.DrawEllipse(Nothing, New Pen(New SolidColorBrush(Colors.Black), 3), p, 5, 5)
            context.DrawEllipse(Nothing, New Pen(New SolidColorBrush(Colors.White), 1.6), p, 5, 5)
        End Sub

        Private Shared Function BuildWedge(center As Point, radius As Double, startDeg As Double, endDeg As Double) As StreamGeometry
            Dim geometry = New StreamGeometry()
            Using ctx = geometry.Open()
                Dim startRad = startDeg * Math.PI / 180.0
                Dim endRad = endDeg * Math.PI / 180.0
                Dim outerStart = New Point(center.X + radius * Math.Cos(startRad), center.Y + radius * Math.Sin(startRad))
                Dim outerEnd = New Point(center.X + radius * Math.Cos(endRad), center.Y + radius * Math.Sin(endRad))

                ctx.BeginFigure(center, True)
                ctx.LineTo(outerStart)
                ctx.ArcTo(outerEnd, New Size(radius, radius), 0, False, SweepDirection.Clockwise)
                ctx.EndFigure(True)
            End Using
            Return geometry
        End Function

        ' ---- Bedienung ----

        Protected Overrides Sub OnPointerPressed(e As PointerPressedEventArgs)
            MyBase.OnPointerPressed(e)
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            Dim pos = e.GetPosition(Me)
            Dim onMarker = IsOnMarker(pos)

            ' Doppelklick auf den Selektionskreis setzt genau DIESES Farbrad zurück (Hue/Sat = 0). Weil
            ' die Bindungen TwoWay auf die Zonen-Property zeigen, landet der Reset in der richtigen Zone
            ' und ist über deren SetUndoableDouble sogar rückgängig machbar.
            If e.ClickCount >= 2 AndAlso onMarker Then
                Hue = 0
                Saturation = 0
                InvalidateVisual()
                e.Handled = True
                Return
            End If

            _dragging = True
            e.Pointer.Capture(Me)
            ' Ein einfacher Klick auf den Marker zieht ihn NICHT weg (sonst wäre der Doppelklick-Reset
            ' unmöglich, weil der erste Klick den Kreis unter den Zeiger holte). Ziehen wirkt trotzdem:
            ' OnPointerMoved wendet den Punkt an, sobald sich der Zeiger bewegt.
            If Not onMarker Then ApplyPoint(pos, e.KeyModifiers)
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerMoved(e As PointerEventArgs)
            MyBase.OnPointerMoved(e)
            If Not _dragging OrElse e.Pointer.Captured IsNot Me Then Return
            ApplyPoint(e.GetPosition(Me), e.KeyModifiers)
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerReleased(e As PointerReleasedEventArgs)
            MyBase.OnPointerReleased(e)
            _dragging = False
            If e.Pointer.Captured Is Me Then e.Pointer.Capture(Nothing)
        End Sub

        ''' <summary>Mit gedrückter Umschalttaste bleibt der Farbton stehen und nur die Sättigung folgt -
        ''' eine gefundene Farbstimmung lässt sich so verstärken oder zurücknehmen, ohne sie zu
        ''' verlieren. Auf dem freien Rad ist das ohne Hilfe kaum zu treffen.</summary>
        Private Sub ApplyPoint(p As Point, modifiers As KeyModifiers)
            If Radius <= 0 Then Return
            Dim dx = p.X - Center.X
            Dim dy = p.Y - Center.Y

            Dim distance = Math.Sqrt(dx * dx + dy * dy)
            Saturation = Math.Max(0.0, Math.Min(100.0, distance / Radius * 100.0))

            If (modifiers And KeyModifiers.Shift) = 0 Then
                ' Bei Sättigung praktisch 0 liegt der Zeiger im Mittelpunkt und hat keine Richtung -
                ' den zuletzt gewählten Farbton dann behalten statt auf 0 (Rot) zu springen.
                If distance > 0.001 Then
                    Dim angle = Math.Atan2(dy, dx) * 180.0 / Math.PI
                    Hue = (angle + 360.0) Mod 360.0
                End If
            End If

            InvalidateVisual()
        End Sub

    End Class

End Namespace
