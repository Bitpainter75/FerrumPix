Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media
Imports Avalonia.Threading
Imports Avalonia.VisualTree

Namespace Controls

    Public Class SelectionOverlayControl
        Inherits Control

        Private Const DashLength As Double = 5.0
        Private Const DashGap As Double = 5.0

        Private ReadOnly _marchingTimer As DispatcherTimer
        Private _dashOffset As Double
        Private _isAttached As Boolean

        Public Shared ReadOnly ShapeModeProperty As StyledProperty(Of String) =
            AvaloniaProperty.Register(Of SelectionOverlayControl, String)(NameOf(ShapeMode), "Rectangle")

        Public Shared ReadOnly PointsProperty As StyledProperty(Of IList(Of Point)) =
            AvaloniaProperty.Register(Of SelectionOverlayControl, IList(Of Point))(NameOf(Points), Nothing)

        Public Shared ReadOnly EdgePointsProperty As StyledProperty(Of IList(Of Point)) =
            AvaloniaProperty.Register(Of SelectionOverlayControl, IList(Of Point))(NameOf(EdgePoints), Nothing)

        Public Shared ReadOnly CombineModeProperty As StyledProperty(Of String) =
            AvaloniaProperty.Register(Of SelectionOverlayControl, String)(NameOf(CombineMode), "New")

        Public Shared ReadOnly FillBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of SelectionOverlayControl, IBrush)(NameOf(FillBrush),
                New SolidColorBrush(Color.FromArgb(72, 255, 255, 255)))

        Public Shared ReadOnly StrokeBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of SelectionOverlayControl, IBrush)(NameOf(StrokeBrush),
                New SolidColorBrush(Color.FromRgb(240, 138, 26)))

        Shared Sub New()
            AffectsRender(Of SelectionOverlayControl)(ShapeModeProperty, PointsProperty, EdgePointsProperty, CombineModeProperty, FillBrushProperty, StrokeBrushProperty)
        End Sub

        Public Sub New()
            _marchingTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(80)}
            AddHandler _marchingTimer.Tick,
                Sub()
                    _dashOffset = (_dashOffset + 1.0) Mod (DashLength + DashGap)
                    InvalidateVisual()
                End Sub
        End Sub

        Protected Overrides Sub OnAttachedToVisualTree(e As VisualTreeAttachmentEventArgs)
            MyBase.OnAttachedToVisualTree(e)
            _isAttached = True
            UpdateTimerState()
        End Sub

        Protected Overrides Sub OnDetachedFromVisualTree(e As VisualTreeAttachmentEventArgs)
            _isAttached = False
            _marchingTimer.Stop()
            MyBase.OnDetachedFromVisualTree(e)
        End Sub

        Protected Overrides Sub OnPropertyChanged(change As AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property Is IsVisibleProperty OrElse
               change.Property Is BoundsProperty OrElse
               change.Property Is PointsProperty OrElse
               change.Property Is EdgePointsProperty OrElse
               change.Property Is CombineModeProperty Then
                UpdateTimerState()
            End If
        End Sub

        Private Sub UpdateTimerState()
            Dim hasGeometry = Bounds.Width > 0 AndAlso Bounds.Height > 0 AndAlso
                              ((Points IsNot Nothing AndAlso Points.Count > 0) OrElse
                               (EdgePoints IsNot Nothing AndAlso EdgePoints.Count > 0) OrElse
                               String.Equals(ShapeMode, "Rectangle", StringComparison.OrdinalIgnoreCase) OrElse
                               String.Equals(ShapeMode, "Ellipse", StringComparison.OrdinalIgnoreCase))

            If _isAttached AndAlso IsVisible AndAlso hasGeometry Then
                If Not _marchingTimer.IsEnabled Then _marchingTimer.Start()
            ElseIf _marchingTimer.IsEnabled Then
                _marchingTimer.Stop()
            End If
        End Sub

        Public Property ShapeMode As String
            Get
                Return GetValue(ShapeModeProperty)
            End Get
            Set(value As String)
                SetValue(ShapeModeProperty, If(String.IsNullOrWhiteSpace(value), "Rectangle", value))
            End Set
        End Property

        Public Property Points As IList(Of Point)
            Get
                Return GetValue(PointsProperty)
            End Get
            Set(value As IList(Of Point))
                SetValue(PointsProperty, value)
            End Set
        End Property

        Public Property EdgePoints As IList(Of Point)
            Get
                Return GetValue(EdgePointsProperty)
            End Get
            Set(value As IList(Of Point))
                SetValue(EdgePointsProperty, value)
            End Set
        End Property

        Public Property CombineMode As String
            Get
                Return GetValue(CombineModeProperty)
            End Get
            Set(value As String)
                SetValue(CombineModeProperty, If(String.IsNullOrWhiteSpace(value), "New", value))
            End Set
        End Property

        Public Property FillBrush As IBrush
            Get
                Return GetValue(FillBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(FillBrushProperty, value)
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
            MyBase.Render(context)
            Dim rect = New Rect(Bounds.Size)
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return

            Dim edgePts = EdgePoints
            If edgePts IsNot Nothing AndAlso edgePts.Count > 0 Then
                RenderEdgePoints(context, edgePts)
                Return
            End If

            Dim darkPen = New Pen(GetDarkDashBrush(), 2.6) With {
                .DashStyle = New DashStyle(New Double() {DashLength, DashGap}, _dashOffset),
                .LineJoin = PenLineJoin.Round
            }
            Dim lightPen = New Pen(Brushes.White, 1.4) With {
                .DashStyle = New DashStyle(New Double() {DashLength, DashGap}, _dashOffset + DashLength),
                .LineJoin = PenLineJoin.Round
            }
            Select Case If(ShapeMode, "Rectangle")
                Case "Ellipse"
                    context.DrawEllipse(Nothing, darkPen, rect.Center, rect.Width / 2.0, rect.Height / 2.0)
                    context.DrawEllipse(Nothing, lightPen, rect.Center, rect.Width / 2.0, rect.Height / 2.0)
                Case "Lasso", "MagicWand"
                    Dim geometry = BuildPolygonGeometry()
                    If geometry IsNot Nothing Then
                        context.DrawGeometry(Nothing, darkPen, geometry)
                        context.DrawGeometry(Nothing, lightPen, geometry)
                    Else
                        context.DrawRectangle(Nothing, darkPen, rect)
                        context.DrawRectangle(Nothing, lightPen, rect)
                    End If
                Case Else
                    context.DrawRectangle(Nothing, darkPen, rect)
                    context.DrawRectangle(Nothing, lightPen, rect)
            End Select
        End Sub

        Private Sub RenderEdgePoints(context As DrawingContext, edgePts As IList(Of Point))
            Dim darkBrush As IBrush = GetDarkDashBrush()
            Dim lightBrush As IBrush = Brushes.White
            Dim period = DashLength + DashGap
            Dim dotSize = Math.Max(1.25, Math.Min(2.2, Math.Max(Bounds.Width, Bounds.Height) / 360.0))

            For Each p In edgePts
                Dim phase = (p.X + p.Y + _dashOffset) Mod period
                Dim brush = If(phase < DashLength, lightBrush, darkBrush)
                context.DrawRectangle(brush, Nothing, New Rect(p.X - dotSize / 2.0, p.Y - dotSize / 2.0, dotSize, dotSize))

            Next
        End Sub

        Private Function GetDarkDashBrush() As IBrush
            Select Case If(CombineMode, "New").Trim()
                Case "Add"
                    Return New SolidColorBrush(Color.FromRgb(28, 132, 78))
                Case "Subtract"
                    Return New SolidColorBrush(Color.FromRgb(190, 64, 64))
                Case "Intersect"
                    Return New SolidColorBrush(Color.FromRgb(54, 104, 190))
                Case Else
                    Return New SolidColorBrush(Color.FromArgb(235, 0, 0, 0))
            End Select
        End Function

        Private Function BuildPolygonGeometry() As StreamGeometry
            Dim pts = Points
            If pts Is Nothing OrElse pts.Count < 3 Then Return Nothing

            Dim geometry As New StreamGeometry()
            Using ctx = geometry.Open()
                ctx.BeginFigure(pts(0), True)
                For i = 1 To pts.Count - 1
                    ctx.LineTo(pts(i))
                Next
                ctx.EndFigure(True)
            End Using
            Return geometry
        End Function
    End Class

End Namespace
