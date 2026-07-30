Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media

Namespace Controls

    ''' <summary>Das Verzerrungsviereck mit seinen vier Eck-Anfassern.
    '''
    ''' Gezeichnet wird, wohin die Bildecken GEWANDERT sind - nicht, wo sie ohne Verzerrung laegen.
    ''' Man soll sehen, was man gerade formt. Bei unbenutztem Werkzeug liegt das Viereck deshalb
    ''' genau auf dem Bildrand, und die Anfasser sitzen in den Bildecken.</summary>
    Public Class PerspectiveOverlayControl
        Inherits Control

        Private Const HandleRadius As Double = 7.0

        ''' <summary>Die vier Ecken in EIGENEN Koordinaten (Pixel), im Uhrzeigersinn ab links oben:
        ''' [x0, y0, x1, y1, x2, y2, x3, y3]. Ein einziges Feld, damit die Bindung EINE Eigenschaft
        ''' ist und nicht acht, die auseinanderlaufen koennen.</summary>
        Public Shared ReadOnly CornerValuesProperty As StyledProperty(Of Double()) =
            AvaloniaProperty.Register(Of PerspectiveOverlayControl, Double())(NameOf(CornerValues), Nothing)

        Public Shared ReadOnly StrokeBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of PerspectiveOverlayControl, IBrush)(NameOf(StrokeBrush),
                New SolidColorBrush(Color.FromRgb(240, 138, 26)))

        Shared Sub New()
            AffectsRender(Of PerspectiveOverlayControl)(CornerValuesProperty, StrokeBrushProperty)
        End Sub

        Public Property CornerValues As Double()
            Get
                Return GetValue(CornerValuesProperty)
            End Get
            Set(value As Double())
                SetValue(CornerValuesProperty, value)
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
            Dim c = CornerValues
            If c Is Nothing OrElse c.Length < 8 Then Return
            For Each v In c
                If Double.IsNaN(v) OrElse Double.IsInfinity(v) Then Return
            Next

            Dim p = New Point() {New Point(c(0), c(1)), New Point(c(2), c(3)),
                                 New Point(c(4), c(5)), New Point(c(6), c(7))}

            ' Zwei Stifte uebereinander, wie beim Verlaufs-Overlay: ein dunkler breiter darunter,
            ' damit die Linie auch auf hellem Bild sichtbar bleibt.
            Dim schatten = New Pen(New SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 3.0)
            Dim line = New Pen(StrokeBrush, 1.4)
            For Each stift In New Pen() {schatten, line}
                For i = 0 To 3
                    context.DrawLine(stift, p(i), p((i + 1) Mod 4))
                Next
            Next

            ' Die Diagonalen als duenne Hilfslinien: sie zeigen die Fluchtung und machen sichtbar,
            ' wann das Viereck entartet - eine gekreuzte Diagonale ist keine gueltige Perspektive.
            Dim hilfe = New Pen(New SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1.0)
            context.DrawLine(hilfe, p(0), p(2))
            context.DrawLine(hilfe, p(1), p(3))

            Dim fuellung = New SolidColorBrush(Color.FromArgb(235, 255, 255, 255))
            Dim border = New Pen(New SolidColorBrush(Color.FromArgb(210, 0, 0, 0)), 1.2)
            For i = 0 To 3
                context.DrawEllipse(fuellung, border, p(i), HandleRadius, HandleRadius)
            Next
        End Sub
    End Class

End Namespace
