Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media

Namespace Controls

    ''' <summary>Stützpunkte und Griffe eines freien Pfades.
    '''
    ''' Gezeichnet wird NUR, was man anfassen kann, plus eine dünne Vorschau der Kurve selbst. Der
    ''' fertige Pfad steht ja schon im Bild; ein zweites, kräftiges Liniengerüst darüber machte
    ''' beides schlechter lesbar. Während ein Entwurf läuft, ist die Vorschau dagegen das einzige,
    ''' was es zu sehen gibt - deshalb wird sie immer gezeichnet.</summary>
    Public Class PathOverlayControl
        Inherits Control

        Private Const AnchorRadius As Double = 4.5
        Private Const HandleRadius As Double = 3.5

        ''' <summary>Anzahl, geschlossen-Kennzeichen, Entwurf-Kennzeichen, dann je Punkt sechs
        ''' Zahlen in PROZENT des Steuerelements: Stützpunkt, eingehender Griff, ausgehender Griff.
        ''' Genau der Aufbau, den EditorViewModel.PathOverlayValues liefert.</summary>
        Public Shared ReadOnly NodeValuesProperty As StyledProperty(Of Double()) =
            AvaloniaProperty.Register(Of PathOverlayControl, Double())(NameOf(NodeValues), Nothing)

        Public Shared ReadOnly StrokeBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of PathOverlayControl, IBrush)(NameOf(StrokeBrush),
                New SolidColorBrush(Color.FromRgb(240, 138, 26)))

        Shared Sub New()
            AffectsRender(Of PathOverlayControl)(NodeValuesProperty, StrokeBrushProperty)
        End Sub

        Public Property NodeValues As Double()
            Get
                Return GetValue(NodeValuesProperty)
            End Get
            Set(value As Double())
                SetValue(NodeValuesProperty, value)
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

        ''' <summary>Steht der Griff sichtbar vom Stützpunkt ab? Bei einer Ecke liegt er darauf, und
        ''' ein Stiel der Länge null wäre nur ein Punkt zu viel.</summary>
        Private Shared Function IsHandleVisible(anchor As Point, handle As Point) As Boolean
            Dim dx = handle.X - anchor.X, dy = handle.Y - anchor.Y
            Return dx * dx + dy * dy > 0.25
        End Function

        Private Function ToPixels(xPercent As Double, yPercent As Double) As Point
            Return New Point(xPercent / 100.0 * Bounds.Width, yPercent / 100.0 * Bounds.Height)
        End Function

        Public Overrides Sub Render(context As DrawingContext)
            Dim v = NodeValues
            If v Is Nothing OrElse v.Length < 9 Then Return
            Dim count = CInt(v(0))
            If count < 1 OrElse v.Length < 3 + count * 6 Then Return
            Dim closed = v(1) > 0.5
            Dim draft = v(2) > 0.5

            Dim anchors(count - 1) As Point
            Dim handlesIn(count - 1) As Point
            Dim handlesOut(count - 1) As Point
            For i = 0 To count - 1
                Dim o = 3 + i * 6
                anchors(i) = ToPixels(v(o), v(o + 1))
                handlesIn(i) = ToPixels(v(o + 2), v(o + 3))
                handlesOut(i) = ToPixels(v(o + 4), v(o + 5))
            Next

            ' Zwei Stifte übereinander, wie beim Verlaufs-Overlay: ein dunkler, breiter darunter,
            ' damit die Linie auch auf hellem Bild stehen bleibt.
            Dim shadowPen = New Pen(New SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), 3.0)
            Dim pen = New Pen(StrokeBrush, 1.4)
            Dim handlePen = New Pen(New SolidColorBrush(Color.FromArgb(190, 255, 255, 255)), 1.0)

            ' Die Kurve selbst.
            If count >= 2 Then
                Dim geometry = New StreamGeometry()
                Using ctx = geometry.Open()
                    ctx.BeginFigure(anchors(0), False)
                    For i = 0 To count - 2
                        ctx.CubicBezierTo(handlesOut(i), handlesIn(i + 1), anchors(i + 1))
                    Next
                    If closed Then ctx.CubicBezierTo(handlesOut(count - 1), handlesIn(0), anchors(0))
                    ctx.EndFigure(closed)
                End Using
                context.DrawGeometry(Nothing, shadowPen, geometry)
                context.DrawGeometry(Nothing, pen, geometry)
            End If

            For i = 0 To count - 1
                ' Griffstiele nur, wo ein Griff wirklich absteht - bei einer Ecke liegen beide auf
                ' dem Stützpunkt, und ein Stiel der Länge null wäre nur ein Punkt zu viel.
                If IsHandleVisible(anchors(i), handlesIn(i)) Then
                    context.DrawLine(handlePen, anchors(i), handlesIn(i))
                    context.DrawEllipse(StrokeBrush, handlePen, handlesIn(i), HandleRadius, HandleRadius)
                End If
                If IsHandleVisible(anchors(i), handlesOut(i)) Then
                    context.DrawLine(handlePen, anchors(i), handlesOut(i))
                    context.DrawEllipse(StrokeBrush, handlePen, handlesOut(i), HandleRadius, HandleRadius)
                End If
            Next

            ' Stützpunkte zuletzt, damit sie über den Stielen liegen. Der ERSTE bekommt WÄHREND DES
            ' ENTWURFS einen Ring: auf ihn klickt man, um den Pfad zu schließen, und das muss man
            ' sehen können. An einem FERTIGEN offenen Pfad zieht ein Klick dort dagegen nur den
            ' Anker - der Ring wäre ein falsches Versprechen und bleibt deshalb weg.
            For i = 0 To count - 1
                Dim rect = New Rect(anchors(i).X - AnchorRadius, anchors(i).Y - AnchorRadius,
                                    AnchorRadius * 2, AnchorRadius * 2)
                context.DrawRectangle(Brushes.White, pen, rect)
            Next
            If draft AndAlso count >= 2 AndAlso Not closed Then
                context.DrawEllipse(Nothing, pen, anchors(0), AnchorRadius + 3.0, AnchorRadius + 3.0)
            End If
        End Sub
    End Class

End Namespace
