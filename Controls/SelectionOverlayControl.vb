Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media

Namespace Controls

    Public Class SelectionOverlayControl
        Inherits Control

        Public Shared ReadOnly ShapeModeProperty As StyledProperty(Of String) =
            AvaloniaProperty.Register(Of SelectionOverlayControl, String)(NameOf(ShapeMode), "Rectangle")

        Public Shared ReadOnly PointsProperty As StyledProperty(Of IList(Of Point)) =
            AvaloniaProperty.Register(Of SelectionOverlayControl, IList(Of Point))(NameOf(Points), Nothing)

        Public Shared ReadOnly FillBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of SelectionOverlayControl, IBrush)(NameOf(FillBrush),
                New SolidColorBrush(Color.FromArgb(72, 255, 255, 255)))

        Public Shared ReadOnly StrokeBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of SelectionOverlayControl, IBrush)(NameOf(StrokeBrush),
                New SolidColorBrush(Color.FromRgb(240, 138, 26)))

        Shared Sub New()
            AffectsRender(Of SelectionOverlayControl)(ShapeModeProperty, PointsProperty, FillBrushProperty, StrokeBrushProperty)
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

            Dim shadowPen = New Pen(New SolidColorBrush(Color.FromArgb(220, 0, 0, 0)), 3.0) With {
                .DashStyle = New DashStyle(New Double() {4, 3}, 0),
                .LineJoin = PenLineJoin.Round
            }
            Dim highlightPen = New Pen(Brushes.White, 1.8) With {
                .DashStyle = New DashStyle(New Double() {4, 3}, 3.5),
                .LineJoin = PenLineJoin.Round
            }
            Dim pen = New Pen(New SolidColorBrush(Color.FromRgb(45, 140, 255)), 1.0) With {
                .DashStyle = New DashStyle(New Double() {1, 6}, 0),
                .LineJoin = PenLineJoin.Round
            }

            Select Case If(ShapeMode, "Rectangle")
                Case "Ellipse"
                    context.DrawEllipse(FillBrush, shadowPen, rect.Center, rect.Width / 2.0, rect.Height / 2.0)
                    context.DrawEllipse(Nothing, highlightPen, rect.Center, rect.Width / 2.0, rect.Height / 2.0)
                    context.DrawEllipse(Nothing, pen, rect.Center, rect.Width / 2.0, rect.Height / 2.0)
                Case "Lasso", "MagicWand"
                    Dim geometry = BuildPolygonGeometry()
                    If geometry IsNot Nothing Then
                        context.DrawGeometry(FillBrush, shadowPen, geometry)
                        context.DrawGeometry(Nothing, highlightPen, geometry)
                        context.DrawGeometry(Nothing, pen, geometry)
                    Else
                        context.DrawRectangle(FillBrush, shadowPen, rect)
                        context.DrawRectangle(Nothing, highlightPen, rect)
                        context.DrawRectangle(Nothing, pen, rect)
                    End If
                Case Else
                    context.DrawRectangle(FillBrush, shadowPen, rect)
                    context.DrawRectangle(Nothing, highlightPen, rect)
                    context.DrawRectangle(Nothing, pen, rect)
            End Select
        End Sub

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
