Imports System.Collections.ObjectModel
Imports System.Collections.Specialized
Imports System.Linq
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Data
Imports Avalonia.Input
Imports Avalonia.Media

Namespace Controls

    ' Interaktiver Tonwertkurven-Editor: Stützpunkte im Bereich 0..255 (X = Eingabe, Y = Ausgabe),
    ' per Maus verschiebbar, per Linksklick auf leere Fläche neue Punkte, per Rechtsklick Punkt entfernen
    ' (Endpunkte bei X=0 und X=255 bleiben erhalten und nur vertikal verschiebbar).
    Public Class CurveEditor
        Inherits Control

        Public Shared ReadOnly PointsProperty As StyledProperty(Of ObservableCollection(Of Point)) =
            AvaloniaProperty.Register(Of CurveEditor, ObservableCollection(Of Point))(NameOf(Points), defaultBindingMode:=BindingMode.TwoWay)

        Public Shared ReadOnly HistogramCountsProperty As StyledProperty(Of Integer()) =
            AvaloniaProperty.Register(Of CurveEditor, Integer())(NameOf(HistogramCounts))

        Public Shared ReadOnly CurveBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of CurveEditor, IBrush)(NameOf(CurveBrush), New SolidColorBrush(Color.Parse("#F08A1A")))

        Public Shared ReadOnly HistogramBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of CurveEditor, IBrush)(NameOf(HistogramBrush), New SolidColorBrush(Color.FromArgb(140, 205, 213, 224)))

        Private Const PointHitRadius As Double = 11
        Private Const PointVisualRadius As Double = 5

        Private _draggingIndex As Integer = -1
        Private _subscribedPoints As ObservableCollection(Of Point)

        Shared Sub New()
            AffectsRender(Of CurveEditor)(PointsProperty, HistogramCountsProperty, CurveBrushProperty, HistogramBrushProperty)
        End Sub

        Public Sub New()
            MinHeight = 160
            ClipToBounds = True
        End Sub

        Public Property Points As ObservableCollection(Of Point)
            Get
                Return GetValue(PointsProperty)
            End Get
            Set(value As ObservableCollection(Of Point))
                SetValue(PointsProperty, value)
            End Set
        End Property

        Public Property HistogramCounts As Integer()
            Get
                Return GetValue(HistogramCountsProperty)
            End Get
            Set(value As Integer())
                SetValue(HistogramCountsProperty, value)
            End Set
        End Property

        Public Property CurveBrush As IBrush
            Get
                Return GetValue(CurveBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(CurveBrushProperty, value)
            End Set
        End Property

        Public Property HistogramBrush As IBrush
            Get
                Return GetValue(HistogramBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(HistogramBrushProperty, value)
            End Set
        End Property

        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property = PointsProperty Then
                If _subscribedPoints IsNot Nothing Then
                    RemoveHandler _subscribedPoints.CollectionChanged, AddressOf OnPointsCollectionChanged
                End If
                _subscribedPoints = TryCast(change.NewValue, ObservableCollection(Of Point))
                If _subscribedPoints IsNot Nothing Then
                    AddHandler _subscribedPoints.CollectionChanged, AddressOf OnPointsCollectionChanged
                End If
                InvalidateVisual()
            End If
        End Sub

        Private Sub OnPointsCollectionChanged(sender As Object, e As NotifyCollectionChangedEventArgs)
            InvalidateVisual()
        End Sub

        Private Function ToScreen(p As Point) As Point
            Dim w = Bounds.Width
            Dim h = Bounds.Height
            Return New Point(p.X / 255.0 * w, h - p.Y / 255.0 * h)
        End Function

        Private Function ToDomain(p As Point) As Point
            Dim w = Math.Max(1.0, Bounds.Width)
            Dim h = Math.Max(1.0, Bounds.Height)
            Dim x = Math.Max(0.0, Math.Min(255.0, p.X / w * 255.0))
            Dim y = Math.Max(0.0, Math.Min(255.0, 255.0 - p.Y / h * 255.0))
            Return New Point(x, y)
        End Function

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)
            Dim w = Bounds.Width
            Dim h = Bounds.Height
            If w <= 0 OrElse h <= 0 Then Return

            context.FillRectangle(New SolidColorBrush(Color.Parse("#12161B")), New Rect(0, 0, w, h))

            Dim gridPen = New Pen(New SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), 1)
            For i = 1 To 3
                Dim x = w * i / 4.0
                Dim y = h * i / 4.0
                context.DrawLine(gridPen, New Point(x, 0), New Point(x, h))
                context.DrawLine(gridPen, New Point(0, y), New Point(w, y))
            Next

            Dim counts = HistogramCounts
            If counts IsNot Nothing AndAlso counts.Length = 256 Then
                Dim maxBin = Math.Max(1, counts.Max())
                Dim histPaint = New Pen(HistogramBrush, Math.Max(1.0, w / 256.0))
                For i = 0 To 255
                    If counts(i) <= 0 Then Continue For
                    Dim x = CSng(i / 255.0 * w)
                    Dim bar = CSng(Math.Pow(counts(i) / CDbl(maxBin), 0.45) * h)
                    context.DrawLine(histPaint, New Point(x, h), New Point(x, h - bar))
                Next
            End If

            Dim diagonalPen = New Pen(New SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1, dashStyle:=DashStyle.Dash)
            context.DrawLine(diagonalPen, New Point(0, h), New Point(w, 0))

            Dim pts = Points
            If pts Is Nothing OrElse pts.Count = 0 Then Return
            Dim ordered = pts.OrderBy(Function(p) p.X).ToList()

            Dim curvePen = New Pen(CurveBrush, 2, lineCap:=PenLineCap.Round)
            Dim geometry = New StreamGeometry()
            Using ctx = geometry.Open()
                Dim first = True
                Dim stepCount = Math.Max(32, CInt(w))
                For i = 0 To stepCount
                    Dim x = i / CDbl(stepCount) * 255.0
                    Dim y = EvaluateCurve(ordered, x)
                    Dim sp = ToScreen(New Point(x, y))
                    If first Then
                        ctx.BeginFigure(sp, False)
                        first = False
                    Else
                        ctx.LineTo(sp)
                    End If
                Next
            End Using
            context.DrawGeometry(Nothing, curvePen, geometry)

            Dim handleFill = New SolidColorBrush(Color.Parse("#0B0E11"))
            Dim handlePen = New Pen(CurveBrush, 2)
            For Each p In ordered
                Dim sp = ToScreen(p)
                context.DrawEllipse(handleFill, handlePen, sp, PointVisualRadius, PointVisualRadius)
            Next
        End Sub

        Private Shared Function EvaluateCurve(points As List(Of Point), x As Double) As Double
            Dim n = points.Count
            If n = 0 Then Return x
            If n = 1 Then Return points(0).Y
            If x <= points(0).X Then Return points(0).Y
            If x >= points(n - 1).X Then Return points(n - 1).Y

            Dim segIndex = 0
            For i = 0 To n - 2
                If x >= points(i).X AndAlso x <= points(i + 1).X Then
                    segIndex = i
                    Exit For
                End If
            Next

            Dim p0 = If(segIndex > 0, points(segIndex - 1), points(segIndex))
            Dim p1 = points(segIndex)
            Dim p2 = points(segIndex + 1)
            Dim p3 = If(segIndex + 2 < n, points(segIndex + 2), points(segIndex + 1))

            Dim span = p2.X - p1.X
            If span <= 0.0001 Then Return p1.Y
            Dim t = (x - p1.X) / span
            Dim t2 = t * t
            Dim t3 = t2 * t

            Dim y = 0.5 * ((2 * p1.Y) +
                           (-p0.Y + p2.Y) * t +
                           (2 * p0.Y - 5 * p1.Y + 4 * p2.Y - p3.Y) * t2 +
                           (-p0.Y + 3 * p1.Y - 3 * p2.Y + p3.Y) * t3)
            Return Math.Max(0.0, Math.Min(255.0, y))
        End Function

        Private Function FindNearestPointIndex(screenPos As Point) As Integer
            Dim pts = Points
            If pts Is Nothing Then Return -1
            Dim bestIndex = -1
            Dim bestDist = Double.MaxValue
            For i = 0 To pts.Count - 1
                Dim sp = ToScreen(pts(i))
                Dim dx = sp.X - screenPos.X
                Dim dy = sp.Y - screenPos.Y
                Dim dist = Math.Sqrt(dx * dx + dy * dy)
                If dist < bestDist Then
                    bestDist = dist
                    bestIndex = i
                End If
            Next
            If bestDist <= PointHitRadius Then Return bestIndex
            Return -1
        End Function

        Protected Overrides Sub OnPointerPressed(e As PointerPressedEventArgs)
            MyBase.OnPointerPressed(e)
            Dim pts = Points
            If pts Is Nothing Then Return
            Dim pos = e.GetPosition(Me)
            Dim props = e.GetCurrentPoint(Me).Properties

            If props.IsRightButtonPressed Then
                Dim idx = FindNearestPointIndex(pos)
                If idx > 0 AndAlso idx < pts.Count - 1 Then
                    pts.RemoveAt(idx)
                    InvalidateVisual()
                End If
                e.Handled = True
                Return
            End If

            If Not props.IsLeftButtonPressed Then Return

            Dim existingIndex = FindNearestPointIndex(pos)
            If existingIndex >= 0 Then
                _draggingIndex = existingIndex
            Else
                Dim domainPoint = ToDomain(pos)
                Dim insertAt = 0
                While insertAt < pts.Count AndAlso pts(insertAt).X < domainPoint.X
                    insertAt += 1
                End While
                pts.Insert(insertAt, domainPoint)
                _draggingIndex = insertAt
            End If

            e.Pointer.Capture(Me)
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerMoved(e As PointerEventArgs)
            MyBase.OnPointerMoved(e)
            If _draggingIndex < 0 Then Return
            Dim pts = Points
            If pts Is Nothing OrElse _draggingIndex >= pts.Count Then Return

            Dim domainPoint = ToDomain(e.GetPosition(Me))
            Dim minX As Double = 0
            Dim maxX As Double = 255

            If _draggingIndex = 0 Then
                minX = 0 : maxX = 0
            ElseIf _draggingIndex = pts.Count - 1 Then
                minX = 255 : maxX = 255
            Else
                minX = pts(_draggingIndex - 1).X + 1
                maxX = pts(_draggingIndex + 1).X - 1
                If maxX < minX Then maxX = minX
            End If

            Dim clampedX = Math.Max(minX, Math.Min(maxX, domainPoint.X))
            pts(_draggingIndex) = New Point(clampedX, domainPoint.Y)
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerReleased(e As PointerReleasedEventArgs)
            MyBase.OnPointerReleased(e)
            _draggingIndex = -1
            e.Pointer.Capture(Nothing)
        End Sub

    End Class

End Namespace
