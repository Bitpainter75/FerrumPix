Option Strict On
Option Explicit On

Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Media
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Controls

    ''' <summary>Die Griffe der Lichtquellen auf der Buehne des Editors (Werkzeug Beleuchtung).
    '''
    ''' Liegt ueber der ganzen Buehne; <see cref="ImageRect"/> sagt, wo das Bild darin steht. Alle
    ''' Werte der Lichter sind Prozent dieses Bildes, die Reichweite Prozent seiner kuerzeren Seite.
    ''' Je Art andere Griffe:
    '''   Mitte        verschiebt das Licht
    '''   Ring         Reichweite (Punktlicht, Strahlen)
    '''   Spitze       Richtung und Laenge (Spot, Streiflicht)
    '''   Kegelkanten  Oeffnungswinkel (Spot)
    '''   Achsenenden  Breite mit Drehung und Hoehe (Flaeche)
    ''' Ein Klick daneben ins Bild setzt ein neues Licht der gewaehlten Lichtart.</summary>
    Public Class LightSourceOverlay
        Inherits Control

        Public Property ImageRect As Rect

        Private Enum Handle
            None
            Center
            Radius
            Tip
            ConeA
            ConeB
            AxisX
            AxisY
        End Enum

        Private _dragId As String = ""
        Private _dragHandle As Handle = Handle.None
        Private _dragOffset As Vector
        ' Grosszuegig: wer knapp neben einen Griff klickt, meint den Griff und kein neues Licht.
        Private Const HitRadius As Double = 16.0

        Private ReadOnly Property Vm As EditorViewModel
            Get
                Return TryCast(DataContext, EditorViewModel)
            End Get
        End Property

        Private ReadOnly Property ShortSide As Double
            Get
                Return Math.Max(1.0, Math.Min(ImageRect.Width, ImageRect.Height))
            End Get
        End Property

        Private Function ToScreen(xPercent As Double, yPercent As Double) As Point
            Return New Point(ImageRect.X + ImageRect.Width * xPercent / 100.0, ImageRect.Y + ImageRect.Height * yPercent / 100.0)
        End Function

        Private Function ToPercent(p As Point) As (X As Double, Y As Double)
            Return ((p.X - ImageRect.X) / Math.Max(1.0, ImageRect.Width) * 100.0,
                    (p.Y - ImageRect.Y) / Math.Max(1.0, ImageRect.Height) * 100.0)
        End Function

        ''' <summary>Die Griffe eines Lichts auf dem Schirm.</summary>
        Private Function HandlesOf(l As LightSource) As List(Of (Kind As Handle, Pos As Point))
            Dim c = ToScreen(l.XPercent, l.YPercent)
            Dim r = l.RadiusPercent / 100.0 * ShortSide
            Dim a = l.AngleDegrees * Math.PI / 180.0
            Dim dir = New Vector(Math.Cos(a), Math.Sin(a))
            Dim list As New List(Of (Kind As Handle, Pos As Point)) From {(Handle.Center, c)}
            Select Case l.Kind
                Case LightSourceKinds.Spot
                    list.Add((Handle.Tip, c + dir * r))
                    Dim half = l.ConeDegrees * Math.PI / 360.0
                    list.Add((Handle.ConeA, c + New Vector(Math.Cos(a - half), Math.Sin(a - half)) * r))
                    list.Add((Handle.ConeB, c + New Vector(Math.Cos(a + half), Math.Sin(a + half)) * r))
                Case LightSourceKinds.Streak
                    list.Add((Handle.Tip, c + dir * r))
                Case LightSourceKinds.Area
                    Dim aspect = Math.Max(0.1, l.AspectPercent / 100.0)
                    Dim ex = r * Math.Sqrt(aspect), ey = r / Math.Sqrt(aspect)
                    list.Add((Handle.AxisX, c + dir * ex))
                    list.Add((Handle.AxisY, c + New Vector(-dir.Y, dir.X) * ey))
                Case Else
                    list.Add((Handle.Radius, c + New Vector(r, 0)))
            End Select
            Return list
        End Function

        Public Overrides Sub Render(context As DrawingContext)
            ' Durchsichtig gefuellt: ohne Flaeche kaemen Klicks ins Bild nur dort an, wo ein Griff
            ' gezeichnet ist, und ein neues Licht liesse sich nicht setzen.
            context.FillRectangle(Brushes.Transparent, New Rect(Bounds.Size))
            Dim vm = Me.Vm
            If vm Is Nothing OrElse ImageRect.Width <= 0 Then Return
            Dim selectedId = vm.SelectedLightId
            Dim accent = If(TryCast(Application.Current?.FindResource("FP.Accent"), IBrush), Brushes.Orange)
            Dim outline = New Pen(New SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), 3)
            Dim line = New Pen(Brushes.White, 1.5)
            Dim dashed = New Pen(Brushes.White, 1.2, New DashStyle({4.0, 4.0}, 0))
            For Each l In vm.LightSnapshot()
                Dim c = ToScreen(l.XPercent, l.YPercent)
                Dim swatch = New SolidColorBrush(EditorViewModel.LightSwatchColor(l))
                Dim selected = l.Id = selectedId
                If selected Then
                    Dim r = l.RadiusPercent / 100.0 * ShortSide
                    Dim a = l.AngleDegrees * Math.PI / 180.0
                    Dim dir = New Vector(Math.Cos(a), Math.Sin(a))
                    Select Case l.Kind
                        Case LightSourceKinds.Spot
                            Dim half = l.ConeDegrees * Math.PI / 360.0
                            Dim pa = c + New Vector(Math.Cos(a - half), Math.Sin(a - half)) * r
                            Dim pb = c + New Vector(Math.Cos(a + half), Math.Sin(a + half)) * r
                            For Each pen In {outline, line}
                                context.DrawLine(pen, c, pa)
                                context.DrawLine(pen, c, pb)
                            Next
                            context.DrawLine(dashed, c, c + dir * r)
                        Case LightSourceKinds.Streak
                            Dim n = New Vector(-dir.Y, dir.X) * ShortSide * 0.6
                            For Each pen In {outline, line}
                                context.DrawLine(pen, c - n, c + n)
                                context.DrawLine(pen, c, c + dir * r)
                            Next
                        Case LightSourceKinds.Area
                            Dim aspect = Math.Max(0.1, l.AspectPercent / 100.0)
                            Dim ex = r * Math.Sqrt(aspect), ey = r / Math.Sqrt(aspect)
                            Using context.PushTransform(Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateRotation(a) * Matrix.CreateTranslation(c.X, c.Y))
                                context.DrawEllipse(Nothing, outline, c, ex, ey)
                                context.DrawEllipse(Nothing, dashed, c, ex, ey)
                            End Using
                        Case Else
                            context.DrawEllipse(Nothing, outline, c, r, r)
                            context.DrawEllipse(Nothing, dashed, c, r, r)
                    End Select
                    For Each h In HandlesOf(l)
                        If h.Kind = Handle.Center Then Continue For
                        context.DrawEllipse(Brushes.White, New Pen(accent, 2.5), h.Pos, 7, 7)
                    Next
                End If
                ' Die Mitte in der Farbe des Lichts; ein Schatten als dunkler Kern.
                Dim core As IBrush = If(l.Intensity < 0, New SolidColorBrush(Color.FromRgb(40, 40, 48)), swatch)
                context.DrawEllipse(core, New Pen(If(selected, accent, Brushes.White), If(selected, 3, 2)), c, If(selected, 8, 6), If(selected, 8, 6))
                If Not l.Enabled Then context.DrawLine(New Pen(Brushes.White, 2), c + New Vector(-7, 7), c + New Vector(7, -7))
            Next
        End Sub

        Protected Overrides Sub OnPointerPressed(e As PointerPressedEventArgs)
            MyBase.OnPointerPressed(e)
            Dim vm = Me.Vm
            If vm Is Nothing OrElse Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            Dim p = e.GetPosition(Me)
            ' Erst die Griffe des markierten Lichts, dann die Mitten aller Lichter.
            Dim lights = vm.LightSnapshot()
            Dim hitId = ""
            Dim hitHandle = Handle.None
            Dim selected = lights.FirstOrDefault(Function(l) l.Id = vm.SelectedLightId)
            If selected IsNot Nothing Then
                For Each h In HandlesOf(selected)
                    If Distance(h.Pos, p) <= HitRadius Then hitId = selected.Id : hitHandle = h.Kind : Exit For
                Next
            End If
            If hitHandle = Handle.None Then
                For i = lights.Count - 1 To 0 Step -1
                    If Distance(ToScreen(lights(i).XPercent, lights(i).YPercent), p) <= HitRadius Then
                        hitId = lights(i).Id : hitHandle = Handle.Center : Exit For
                    End If
                Next
            End If
            If hitHandle = Handle.None AndAlso selected IsNot Nothing Then
                ' Ist ein Licht markiert, hebt ein Klick daneben nur die Markierung auf. Erst der
                ' naechste Klick ins Leere setzt ein neues Licht: knapp an einem Griff vorbei
                ' entstand sonst sofort eine zweite Lichtquelle.
                vm.SelectLight("")
                e.Handled = True
                InvalidateVisual()
                Return
            End If
            If hitHandle = Handle.None Then
                If Not ImageRect.Contains(p) Then Return
                Dim pct = ToPercent(p)
                Dim added = vm.AddLightAt(pct.X, pct.Y)
                hitId = added.Id : hitHandle = Handle.Center
                ' Das Anlegen ist schon ein Schritt; das Ziehen danach gehoert dazu.
                _dragId = hitId : _dragHandle = hitHandle : _dragOffset = New Vector(0, 0)
                vm.BeginLightEdit(hitId, captureUndo:=False)
            Else
                Dim light = lights.First(Function(l) l.Id = hitId)
                _dragOffset = If(hitHandle = Handle.Center, ToScreen(light.XPercent, light.YPercent) - p, New Vector(0, 0))
                _dragId = hitId : _dragHandle = hitHandle
                vm.BeginLightEdit(hitId)
            End If
            e.Pointer.Capture(Me)
            e.Handled = True
            InvalidateVisual()
        End Sub

        Protected Overrides Sub OnPointerMoved(e As PointerEventArgs)
            MyBase.OnPointerMoved(e)
            If _dragHandle = Handle.None Then
                Cursor = If(HitsAnything(e.GetPosition(Me)), New Cursor(StandardCursorType.Hand), New Cursor(StandardCursorType.Cross))
                Return
            End If
            Dim vm = Me.Vm
            Dim light = vm?.LightSnapshot().FirstOrDefault(Function(l) l.Id = _dragId)
            If light Is Nothing Then Return
            Dim p = e.GetPosition(Me)
            Dim c = ToScreen(light.XPercent, light.YPercent)
            Dim x = light.XPercent, y = light.YPercent, r = light.RadiusPercent
            Dim angle = light.AngleDegrees, cone = light.ConeDegrees, aspect = light.AspectPercent
            Dim v = p - c
            Dim len = Math.Sqrt(v.X * v.X + v.Y * v.Y)
            Dim pointerAngle = Math.Atan2(v.Y, v.X) * 180.0 / Math.PI
            Select Case _dragHandle
                Case Handle.Center
                    Dim pct = ToPercent(p + _dragOffset)
                    x = pct.X : y = pct.Y
                Case Handle.Radius
                    r = len / ShortSide * 100.0
                Case Handle.Tip
                    r = len / ShortSide * 100.0
                    angle = pointerAngle
                Case Handle.ConeA, Handle.ConeB
                    Dim diff = Math.Abs(NormalizeDegrees(pointerAngle - angle))
                    cone = diff * 2.0
                Case Handle.AxisX
                    angle = pointerAngle
                    Dim ey = (r / 100.0 * ShortSide) / Math.Sqrt(Math.Max(0.1, aspect / 100.0))
                    Dim ex = Math.Max(2.0, len)
                    r = Math.Sqrt(ex * ey) / ShortSide * 100.0
                    aspect = ex / Math.Max(1.0, ey) * 100.0
                Case Handle.AxisY
                    Dim ex = (r / 100.0 * ShortSide) * Math.Sqrt(Math.Max(0.1, aspect / 100.0))
                    Dim ey = Math.Max(2.0, len)
                    r = Math.Sqrt(ex * ey) / ShortSide * 100.0
                    aspect = ex / Math.Max(1.0, ey) * 100.0
            End Select
            vm.UpdateLightGeometry(_dragId, x, y, r, angle, cone, aspect)
            e.Handled = True
            InvalidateVisual()
        End Sub

        Protected Overrides Sub OnPointerReleased(e As PointerReleasedEventArgs)
            MyBase.OnPointerReleased(e)
            If _dragHandle = Handle.None Then Return
            _dragHandle = Handle.None
            _dragId = ""
            Vm?.EndLightEdit()
            e.Pointer.Capture(Nothing)
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerCaptureLost(e As PointerCaptureLostEventArgs)
            MyBase.OnPointerCaptureLost(e)
            If _dragHandle = Handle.None Then Return
            _dragHandle = Handle.None
            _dragId = ""
            Vm?.EndLightEdit()
        End Sub

        Private Function HitsAnything(p As Point) As Boolean
            Dim vm = Me.Vm
            If vm Is Nothing Then Return False
            For Each l In vm.LightSnapshot()
                Dim grips = If(l.Id = vm.SelectedLightId, HandlesOf(l), New List(Of (Kind As Handle, Pos As Point)) From {(Handle.Center, ToScreen(l.XPercent, l.YPercent))})
                If grips.Any(Function(h) Distance(h.Pos, p) <= HitRadius) Then Return True
            Next
            Return False
        End Function

        Private Shared Function Distance(a As Point, b As Point) As Double
            Dim dx = a.X - b.X, dy = a.Y - b.Y
            Return Math.Sqrt(dx * dx + dy * dy)
        End Function

        Private Shared Function NormalizeDegrees(v As Double) As Double
            Dim d = v Mod 360.0
            If d > 180 Then d -= 360
            If d < -180 Then d += 360
            Return d
        End Function
    End Class

End Namespace
