Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Data
Imports Avalonia.Input
Imports Avalonia.Media
Imports Avalonia.Rendering

Namespace Controls

    ''' <summary>
    ''' Selbstgezeichneter Farbwähler: außen ein durchgehender Farbtonring, innen ein Feld für
    ''' Sättigung (waagerecht) und Helligkeit (senkrecht). Gezogen wird in beiden Bereichen; welcher
    ''' gerade bedient wird, entscheidet sich beim Aufsetzen des Zeigers.
    '''
    ''' Der Farbton wird zusätzlich als eigenes Feld gehalten. Ohne das ginge er verloren, sobald die
    ''' Sättigung Null erreicht (Grau hat keinen Farbton) - der Ringzeiger spränge dann auf Rot.
    ''' </summary>
    Public Class ColorWheel
        Inherits Control
        Implements ICustomHitTest

        Public Shared ReadOnly ColorProperty As StyledProperty(Of Color) =
            AvaloniaProperty.Register(Of ColorWheel, Color)(NameOf(Color), Colors.White, defaultBindingMode:=BindingMode.TwoWay)

        Private Enum DragTarget
            None
            Ring
            Field
        End Enum

        Private _hue As Double = 0
        Private _saturation As Double = 0
        Private _value As Double = 1
        Private _dragTarget As DragTarget = DragTarget.None
        Private _suppressColorSync As Boolean

        Shared Sub New()
            AffectsRender(Of ColorWheel)(ColorProperty)
        End Sub

        Public Sub New()
            Cursor = New Cursor(StandardCursorType.Cross)
            MinWidth = 140
            MinHeight = 140
        End Sub

        Public Property Color As Color
            Get
                Return GetValue(ColorProperty)
            End Get
            Set(value As Color)
                SetValue(ColorProperty, value)
            End Set
        End Property

        Public Function HitTest(point As Point) As Boolean Implements ICustomHitTest.HitTest
            Return New Rect(Bounds.Size).Contains(point)
        End Function

        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property IsNot ColorProperty OrElse _suppressColorSync Then Return

            ' Von außen gesetzt: HSV nachziehen, aber den Farbton bei Grautönen behalten.
            Dim hsv = Color.ToHsv()
            If hsv.S > 0.0001 Then _hue = hsv.H
            _saturation = hsv.S
            _value = hsv.V
        End Sub

        ' ---- Geometrie ----

        Private ReadOnly Property WheelSize As Double
            Get
                Return Math.Min(Bounds.Width, Bounds.Height)
            End Get
        End Property

        Private ReadOnly Property Center As Point
            Get
                Return New Point(Bounds.Width / 2.0, Bounds.Height / 2.0)
            End Get
        End Property

        Private ReadOnly Property OuterRadius As Double
            Get
                Return WheelSize / 2.0 - 1
            End Get
        End Property

        Private ReadOnly Property InnerRadius As Double
            Get
                Return OuterRadius - Math.Max(10.0, WheelSize * 0.13)
            End Get
        End Property

        ''' Das größte achsenparallele Quadrat, das in den Innenkreis passt, minus etwas Luft.
        Private ReadOnly Property FieldRect As Rect
            Get
                Dim side = (InnerRadius - 4.0) * Math.Sqrt(2.0)
                If side <= 0 Then Return New Rect()
                Return New Rect(Center.X - side / 2.0, Center.Y - side / 2.0, side, side)
            End Get
        End Property

        ' ---- Zeichnen ----

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)
            If WheelSize <= 0 OrElse InnerRadius <= 0 Then Return

            DrawHueRing(context)
            DrawSaturationValueField(context)
            DrawRingMarker(context)
            DrawFieldMarker(context)
        End Sub

        ''' Der Ring entsteht aus 120 Keilen zu je 3 Grad. Die halbe Grad Überlappung verhindert die
        ''' Haarrisse, die sonst zwischen den Segmenten durchscheinen.
        Private Sub DrawHueRing(context As DrawingContext)
            Const stepDeg As Double = 3.0
            Dim segments = CInt(360.0 / stepDeg)
            For i = 0 To segments - 1
                Dim startDeg = i * stepDeg
                Dim hue = (startDeg + stepDeg / 2.0 + 360.0) Mod 360.0
                Dim segmentColor = New HsvColor(1.0, hue, 1.0, 1.0).ToRgb()
                Dim geometry = BuildWedgeGeometry(Center, InnerRadius, OuterRadius, startDeg - 0.5, startDeg + stepDeg + 0.5)
                context.DrawGeometry(New SolidColorBrush(segmentColor), Nothing, geometry)
            Next
        End Sub

        ''' Volltonfarbe des aktuellen Farbtons, darüber ein Weiß-Verlauf nach links (Sättigung) und
        ''' ein Schwarz-Verlauf nach unten (Helligkeit) - das übliche Sättigungs-/Helligkeitsfeld.
        Private Sub DrawSaturationValueField(context As DrawingContext)
            Dim rect = FieldRect
            If rect.Width <= 0 Then Return

            Dim pureHue = New HsvColor(1.0, _hue, 1.0, 1.0).ToRgb()
            context.FillRectangle(New SolidColorBrush(pureHue), rect, 3)

            Dim toWhite As New LinearGradientBrush With {
                .StartPoint = New RelativePoint(0, 0, RelativeUnit.Relative),
                .EndPoint = New RelativePoint(1, 0, RelativeUnit.Relative)
            }
            toWhite.GradientStops.Add(New GradientStop(Colors.White, 0))
            toWhite.GradientStops.Add(New GradientStop(Avalonia.Media.Color.FromArgb(0, 255, 255, 255), 1))
            context.FillRectangle(toWhite, rect, 3)

            Dim toBlack As New LinearGradientBrush With {
                .StartPoint = New RelativePoint(0, 0, RelativeUnit.Relative),
                .EndPoint = New RelativePoint(0, 1, RelativeUnit.Relative)
            }
            toBlack.GradientStops.Add(New GradientStop(Avalonia.Media.Color.FromArgb(0, 0, 0, 0), 0))
            toBlack.GradientStops.Add(New GradientStop(Colors.Black, 1))
            context.FillRectangle(toBlack, rect, 3)
        End Sub

        Private Sub DrawRingMarker(context As DrawingContext)
            Dim radius = (InnerRadius + OuterRadius) / 2.0
            Dim rad = _hue * Math.PI / 180.0
            Dim p = New Point(Center.X + radius * Math.Cos(rad), Center.Y + radius * Math.Sin(rad))
            Dim markerRadius = Math.Max(4.0, (OuterRadius - InnerRadius) / 2.0 - 2.0)
            context.DrawEllipse(Nothing, New Pen(New SolidColorBrush(Colors.Black), 3), p, markerRadius, markerRadius)
            context.DrawEllipse(Nothing, New Pen(New SolidColorBrush(Colors.White), 1.6), p, markerRadius, markerRadius)
        End Sub

        Private Sub DrawFieldMarker(context As DrawingContext)
            Dim rect = FieldRect
            If rect.Width <= 0 Then Return
            Dim p = New Point(rect.X + _saturation * rect.Width, rect.Y + (1.0 - _value) * rect.Height)
            context.DrawEllipse(Nothing, New Pen(New SolidColorBrush(Colors.Black), 3), p, 6, 6)
            context.DrawEllipse(Nothing, New Pen(New SolidColorBrush(Colors.White), 1.6), p, 6, 6)
        End Sub

        Private Shared Function BuildWedgeGeometry(center As Point, innerRadius As Double, outerRadius As Double, startDeg As Double, endDeg As Double) As StreamGeometry
            Dim geometry = New StreamGeometry()
            Using ctx = geometry.Open()
                Dim startRad = startDeg * Math.PI / 180.0
                Dim endRad = endDeg * Math.PI / 180.0

                Dim outerStart = New Point(center.X + outerRadius * Math.Cos(startRad), center.Y + outerRadius * Math.Sin(startRad))
                Dim outerEnd = New Point(center.X + outerRadius * Math.Cos(endRad), center.Y + outerRadius * Math.Sin(endRad))
                Dim innerStart = New Point(center.X + innerRadius * Math.Cos(startRad), center.Y + innerRadius * Math.Sin(startRad))
                Dim innerEnd = New Point(center.X + innerRadius * Math.Cos(endRad), center.Y + innerRadius * Math.Sin(endRad))

                ctx.BeginFigure(innerStart, True)
                ctx.LineTo(outerStart)
                ctx.ArcTo(outerEnd, New Size(outerRadius, outerRadius), 0, False, SweepDirection.Clockwise)
                ctx.LineTo(innerEnd)
                ctx.ArcTo(innerStart, New Size(innerRadius, innerRadius), 0, False, SweepDirection.CounterClockwise)
                ctx.EndFigure(True)
            End Using
            Return geometry
        End Function

        ' ---- Bedienung ----

        Protected Overrides Sub OnPointerPressed(e As PointerPressedEventArgs)
            MyBase.OnPointerPressed(e)
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return

            Dim p = e.GetPosition(Me)
            Dim radiusFromCenter = DistanceBetween(p, Center)

            If radiusFromCenter >= InnerRadius AndAlso radiusFromCenter <= OuterRadius + 2 Then
                _dragTarget = DragTarget.Ring
            ElseIf FieldRect.Contains(p) Then
                _dragTarget = DragTarget.Field
            Else
                Return
            End If

            e.Pointer.Capture(Me)
            ApplyPoint(p)
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerMoved(e As PointerEventArgs)
            MyBase.OnPointerMoved(e)
            If _dragTarget = DragTarget.None OrElse e.Pointer.Captured IsNot Me Then Return
            ApplyPoint(e.GetPosition(Me))
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerReleased(e As PointerReleasedEventArgs)
            MyBase.OnPointerReleased(e)
            _dragTarget = DragTarget.None
            If e.Pointer.Captured Is Me Then e.Pointer.Capture(Nothing)
        End Sub

        ''' Der einmal begonnene Zug bleibt in seinem Bereich, auch wenn der Zeiger ihn verlässt -
        ''' sonst spränge beim Ziehen über die Ringkante hinaus die Sättigung mit.
        Private Sub ApplyPoint(p As Point)
            Select Case _dragTarget
                Case DragTarget.Ring
                    Dim angle = Math.Atan2(p.Y - Center.Y, p.X - Center.X) * 180.0 / Math.PI
                    _hue = (angle + 360.0) Mod 360.0
                Case DragTarget.Field
                    Dim rect = FieldRect
                    If rect.Width <= 0 OrElse rect.Height <= 0 Then Return
                    _saturation = Clamp01((p.X - rect.X) / rect.Width)
                    _value = Clamp01(1.0 - (p.Y - rect.Y) / rect.Height)
                Case Else
                    Return
            End Select

            PushColor()
        End Sub

        ''' Die Deckkraft gehört nicht zum Rad - sie bleibt unverändert, damit ein halbtransparentes
        ''' Wasserzeichen beim Umfärben nicht unbemerkt deckend wird.
        Private Sub PushColor()
            Dim rgb = New HsvColor(1.0, _hue, _saturation, _value).ToRgb()
            Dim withAlpha = Avalonia.Media.Color.FromArgb(Color.A, rgb.R, rgb.G, rgb.B)

            _suppressColorSync = True
            Try
                Color = withAlpha
            Finally
                _suppressColorSync = False
            End Try
            InvalidateVisual()
        End Sub

        Private Shared Function DistanceBetween(a As Point, b As Point) As Double
            Return Math.Sqrt((a.X - b.X) ^ 2 + (a.Y - b.Y) ^ 2)
        End Function

        Private Shared Function Clamp01(v As Double) As Double
            Return Math.Max(0.0, Math.Min(1.0, v))
        End Function

    End Class

End Namespace
