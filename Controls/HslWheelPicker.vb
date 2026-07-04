Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Data
Imports Avalonia.Input
Imports Avalonia.Media
Imports Avalonia.Rendering

Namespace Controls

    ''' Farbrad zur Auswahl eines der 8 HSL-Bänder (Phase 1 "Farbmischer neu denken", siehe Roadmap) -
    ''' ersetzt die vormals 8 gestapelten Ton/Sättigungs-Reglerpaare durch ein Rad aus 8 Segmenten;
    ''' das ausgewählte Segment bestimmt, welches Band die (weiterhin unveränderten) Editor-Regler
    ''' ActiveHslHue/ActiveHslSaturation gerade lesen/schreiben. Reine Auswahl-UI - das Rad selbst
    ''' trägt keine Farbwerte ein, es schaltet nur um.
    Public Class HslWheelPicker
        Inherits Control
        Implements ICustomHitTest

        Public Shared ReadOnly SelectedBandProperty As StyledProperty(Of String) =
            AvaloniaProperty.Register(Of HslWheelPicker, String)(NameOf(SelectedBand), "Red", defaultBindingMode:=BindingMode.TwoWay)

        Private Shared ReadOnly BandOrder As String() = {"Red", "Orange", "Yellow", "Green", "Aqua", "Blue", "Purple", "Magenta"}

        ' Repräsentative Anzeigefarbe je Band (echter Hue-Winkel, nicht der gleichmäßig verteilte
        ' Segment-Winkel unten) - so sieht das Rad wie ein normaler Farbkreis aus.
        Private Shared ReadOnly BandDisplayHue As Double() = {0, 30, 60, 120, 180, 240, 275, 315}

        Shared Sub New()
            AffectsRender(Of HslWheelPicker)(SelectedBandProperty)
        End Sub

        Public Sub New()
            Cursor = New Cursor(StandardCursorType.Hand)
            MinWidth = 120
            MinHeight = 120
        End Sub

        Public Property SelectedBand As String
            Get
                Return GetValue(SelectedBandProperty)
            End Get
            Set(value As String)
                SetValue(SelectedBandProperty, If(String.IsNullOrWhiteSpace(value), "Red", value))
            End Set
        End Property

        Public Function HitTest(point As Point) As Boolean Implements ICustomHitTest.HitTest
            Return New Rect(Bounds.Size).Contains(point)
        End Function

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)

            Dim size = Math.Min(Bounds.Width, Bounds.Height)
            If size <= 0 Then Return

            Dim center = New Point(Bounds.Width / 2.0, Bounds.Height / 2.0)
            Dim outerRadius = size / 2.0 - 2
            Dim innerRadius = outerRadius * 0.55
            Dim selectedIndex = Array.IndexOf(BandOrder, SelectedBand)
            If selectedIndex < 0 Then selectedIndex = 0

            For i = 0 To BandOrder.Length - 1
                Dim segmentStartDeg = i * 45.0 - 22.5
                Dim segmentEndDeg = segmentStartDeg + 45.0
                Dim isSelected = i = selectedIndex
                Dim bandColor = New HsvColor(1.0, BandDisplayHue(i), 0.78, If(isSelected, 0.85, 0.55)).ToRgb()

                Dim geometry = BuildWedgeGeometry(center, innerRadius, outerRadius, segmentStartDeg, segmentEndDeg)
                Dim fillBrush = New SolidColorBrush(bandColor)
                Dim pen = If(isSelected, New Pen(New SolidColorBrush(Colors.White), 2), Nothing)
                context.DrawGeometry(fillBrush, pen, geometry)
            Next
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

        Protected Overrides Sub OnPointerPressed(e As PointerPressedEventArgs)
            MyBase.OnPointerPressed(e)
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            SelectBandFromPoint(e.GetPosition(Me))
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerMoved(e As PointerEventArgs)
            MyBase.OnPointerMoved(e)
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            SelectBandFromPoint(e.GetPosition(Me))
            e.Handled = True
        End Sub

        Private Sub SelectBandFromPoint(point As Point)
            Dim size = Math.Min(Bounds.Width, Bounds.Height)
            If size <= 0 Then Return
            Dim center = New Point(Bounds.Width / 2.0, Bounds.Height / 2.0)
            Dim dx = point.X - center.X
            Dim dy = point.Y - center.Y
            Dim angleDeg = Math.Atan2(dy, dx) * 180.0 / Math.PI
            Dim normalized = ((angleDeg + 22.5) Mod 360 + 360) Mod 360
            Dim index = CInt(Math.Floor(normalized / 45.0))
            If index < 0 Then index = 0
            If index > 7 Then index = 7
            SelectedBand = BandOrder(index)
        End Sub

    End Class

End Namespace
