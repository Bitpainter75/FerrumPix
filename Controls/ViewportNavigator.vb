Imports System.Windows.Input
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Media
Imports Avalonia.Rendering

Namespace Controls

    ''' <summary>Der Navigator: ein Rechteck ueber einem kleinen Bild, das zeigt, welcher Ausschnitt
    ''' gerade gross zu sehen ist, und das sich ziehen laesst. Liegt im Betrachter auf der aktiven
    ''' Kachel des Filmstreifens und im Editor unten im Ebenenpanel.
    '''
    ''' Das Steuerelement zeichnet nur das Rechteck und die Abdunklung darum; das Bild darunter
    ''' bringt der Aufrufer mit. Damit beides deckungsgleich ist, rechnet es die Bildflaeche aus
    ''' seiner eigenen Groesse, dem Seitenverhaeltnis des Bildes und der Streckung nach, mit der das
    ''' Bild darunter steht (<see cref="Fill"/>: wie UniformToFill beschnitten, sonst wie Uniform).
    '''
    ''' Alles ist auf das Bild bezogen, 0 bis 1: <see cref="ViewRect"/> ist der sichtbare Teil, und
    ''' gezogen meldet es die gewuenschte MITTE ueber <see cref="PanCommand"/>. Ohne Ausschnitt (das
    ''' ganze Bild ist zu sehen) zeichnet es nichts und faengt keinen Klick ab, die Kachel darunter
    ''' bleibt also klickbar wie immer.</summary>
    Public Class ViewportNavigator
        Inherits Control
        Implements ICustomHitTest

        Public Shared ReadOnly ViewRectProperty As StyledProperty(Of Rect) =
            AvaloniaProperty.Register(Of ViewportNavigator, Rect)(NameOf(ViewRect))

        Public Shared ReadOnly ImageAspectProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of ViewportNavigator, Double)(NameOf(ImageAspect), 1.5)

        Public Shared ReadOnly FillProperty As StyledProperty(Of Boolean) =
            AvaloniaProperty.Register(Of ViewportNavigator, Boolean)(NameOf(Fill))

        Public Shared ReadOnly PanCommandProperty As StyledProperty(Of ICommand) =
            AvaloniaProperty.Register(Of ViewportNavigator, ICommand)(NameOf(PanCommand))

        Public Shared ReadOnly FrameBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of ViewportNavigator, IBrush)(NameOf(FrameBrush), Brushes.White)

        Shared Sub New()
            AffectsRender(Of ViewportNavigator)(ViewRectProperty, ImageAspectProperty, FillProperty, FrameBrushProperty)
        End Sub

        Public Property ViewRect As Rect
            Get
                Return GetValue(ViewRectProperty)
            End Get
            Set(value As Rect)
                SetValue(ViewRectProperty, value)
            End Set
        End Property

        ''' <summary>Breite durch Hoehe des Bildes, wie es gezeigt wird.</summary>
        Public Property ImageAspect As Double
            Get
                Return GetValue(ImageAspectProperty)
            End Get
            Set(value As Double)
                SetValue(ImageAspectProperty, value)
            End Set
        End Property

        ''' <summary>True: das Bild darunter fuellt die Flaeche und ist beschnitten (UniformToFill,
        ''' die Kachel im Filmstreifen). False: es steht ganz darin (Uniform).</summary>
        Public Property Fill As Boolean
            Get
                Return GetValue(FillProperty)
            End Get
            Set(value As Boolean)
                SetValue(FillProperty, value)
            End Set
        End Property

        ''' <summary>Bekommt beim Ziehen die gewuenschte Mitte des Ausschnitts als Point, 0 bis 1.</summary>
        Public Property PanCommand As ICommand
            Get
                Return GetValue(PanCommandProperty)
            End Get
            Set(value As ICommand)
                SetValue(PanCommandProperty, value)
            End Set
        End Property

        Public Property FrameBrush As IBrush
            Get
                Return GetValue(FrameBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(FrameBrushProperty, value)
            End Set
        End Property

        ''' <summary>Gilt ein Ausschnitt? Ist er (fast) das ganze Bild, gibt es nichts zu zeigen.</summary>
        Public Shared Function HasViewport(rect As Rect) As Boolean
            Return rect.Width > 0 AndAlso rect.Height > 0 AndAlso (rect.Width < 0.999 OrElse rect.Height < 0.999)
        End Function

        ''' <summary>Der sichtbare Teil eines Inhalts der Groesse <paramref name="extent"/> in einem
        ''' Fenster <paramref name="viewport"/>, um <paramref name="offset"/> gerollt, auf den Inhalt
        ''' bezogen (0 bis 1). Passt der Inhalt in einer Richtung ganz hinein, reicht der Ausschnitt
        ''' dort von 0 bis 1.</summary>
        Public Shared Function VisibleRect(offsetX As Double, offsetY As Double,
                                           viewportWidth As Double, viewportHeight As Double,
                                           extentWidth As Double, extentHeight As Double) As Rect
            If extentWidth <= 0 OrElse extentHeight <= 0 OrElse viewportWidth <= 0 OrElse viewportHeight <= 0 Then Return New Rect()
            Dim x0 = Math.Max(0, Math.Min(1, offsetX / extentWidth))
            Dim y0 = Math.Max(0, Math.Min(1, offsetY / extentHeight))
            Dim x1 = Math.Max(x0, Math.Min(1, (offsetX + viewportWidth) / extentWidth))
            Dim y1 = Math.Max(y0, Math.Min(1, (offsetY + viewportHeight) / extentHeight))
            Return New Rect(x0, y0, x1 - x0, y1 - y0)
        End Function

        ''' <summary>Dasselbe fuer ein Bild, das an (<paramref name="imageX"/>, <paramref name="imageY"/>)
        ''' in der Groesse <paramref name="imageWidth"/> mal <paramref name="imageHeight"/> auf einer
        ''' Flaeche liegt (die Buehne des Editors): der Teil, der in die Flaeche faellt.</summary>
        Public Shared Function VisibleRectOnStage(imageX As Double, imageY As Double,
                                                  imageWidth As Double, imageHeight As Double,
                                                  stageWidth As Double, stageHeight As Double) As Rect
            Return VisibleRect(-imageX, -imageY, stageWidth, stageHeight, imageWidth, imageHeight)
        End Function

        ''' <summary>Wo das Bild in einer Flaeche der Groesse <paramref name="box"/> steht, beschnitten
        ''' (<paramref name="fill"/>) oder ganz. Die Flaeche kann bei <paramref name="fill"/> ueber die
        ''' Box hinausreichen.</summary>
        Public Shared Function ImageArea(box As Size, aspect As Double, fill As Boolean) As Rect
            If box.Width <= 0 OrElse box.Height <= 0 OrElse aspect <= 0 OrElse Double.IsNaN(aspect) Then Return New Rect(box)
            Dim boxAspect = box.Width / box.Height
            Dim width, height As Double
            If (aspect > boxAspect) Xor fill Then
                width = box.Width
                height = box.Width / aspect
            Else
                height = box.Height
                width = box.Height * aspect
            End If
            Return New Rect((box.Width - width) / 2, (box.Height - height) / 2, width, height)
        End Function

        ''' <summary>Die Mitte, die ein Ausschnitt der Groesse <paramref name="viewSize"/> haben darf,
        ''' damit er ganz im Bild bleibt.</summary>
        Public Shared Function ClampCenter(center As Point, viewSize As Size) As Point
            Dim halfW = Math.Min(0.5, viewSize.Width / 2)
            Dim halfH = Math.Min(0.5, viewSize.Height / 2)
            Return New Point(Math.Max(halfW, Math.Min(1 - halfW, center.X)),
                             Math.Max(halfH, Math.Min(1 - halfH, center.Y)))
        End Function

        Private _dragging As Boolean
        Private _grab As Vector

        Private Function CurrentImageArea() As Rect
            Return ImageArea(Bounds.Size, ImageAspect, Fill)
        End Function

        Private Function ToImage(p As Point, area As Rect) As Point
            Return New Point((p.X - area.X) / Math.Max(1, area.Width), (p.Y - area.Y) / Math.Max(1, area.Height))
        End Function

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)
            Dim view = ViewRect
            If Not HasViewport(view) Then Return
            Dim area = CurrentImageArea()
            Dim r = New Rect(area.X + view.X * area.Width, area.Y + view.Y * area.Height,
                             view.Width * area.Width, view.Height * area.Height)
            ' Abdunkeln, was ausserhalb liegt, damit das Rechteck auch auf hellem Bild zu sehen ist.
            Dim shade = New SolidColorBrush(Color.FromArgb(110, 0, 0, 0))
            Dim full = New Rect(Bounds.Size)
            Using context.PushClip(full)
                context.DrawRectangle(shade, Nothing, New Rect(full.X, full.Y, full.Width, Math.Max(0, r.Y - full.Y)))
                context.DrawRectangle(shade, Nothing, New Rect(full.X, r.Bottom, full.Width, Math.Max(0, full.Bottom - r.Bottom)))
                context.DrawRectangle(shade, Nothing, New Rect(full.X, r.Y, Math.Max(0, r.X - full.X), r.Height))
                context.DrawRectangle(shade, Nothing, New Rect(r.Right, r.Y, Math.Max(0, full.Right - r.Right), r.Height))
                ' Dunkler Rand aussen, heller innen: sichtbar auf jedem Bild.
                context.DrawRectangle(Nothing, New Pen(New SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 3), r)
                context.DrawRectangle(Nothing, New Pen(FrameBrush, 1.5), r)
            End Using
        End Sub

        Private Function HitTest(point As Point) As Boolean Implements ICustomHitTest.HitTest
            Return HasViewport(ViewRect) AndAlso New Rect(Bounds.Size).Contains(point)
        End Function

        Protected Overrides Sub OnPointerPressed(e As PointerPressedEventArgs)
            MyBase.OnPointerPressed(e)
            If Not HasViewport(ViewRect) OrElse Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            Dim area = CurrentImageArea()
            Dim p = ToImage(e.GetPosition(Me), area)
            Dim view = ViewRect
            ' Im Rechteck gegriffen: es bleibt unter dem Zeiger, wo es gegriffen wurde. Daneben
            ' geklickt: die Mitte springt auf die Stelle.
            _grab = If(view.Contains(p), p - view.Center, New Vector(0, 0))
            _dragging = True
            e.Pointer.Capture(Me)
            PanTo(p)
            ' Die Kachel darunter soll weder waehlen noch einen Zug beginnen.
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerMoved(e As PointerEventArgs)
            MyBase.OnPointerMoved(e)
            If Not _dragging Then Return
            PanTo(ToImage(e.GetPosition(Me), CurrentImageArea()))
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerReleased(e As PointerReleasedEventArgs)
            MyBase.OnPointerReleased(e)
            If Not _dragging Then Return
            _dragging = False
            e.Pointer.Capture(Nothing)
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerCaptureLost(e As PointerCaptureLostEventArgs)
            MyBase.OnPointerCaptureLost(e)
            _dragging = False
        End Sub

        Private Sub PanTo(pointer As Point)
            Dim center = ClampCenter(pointer - _grab, ViewRect.Size)
            Dim command = PanCommand
            If command IsNot Nothing AndAlso command.CanExecute(center) Then command.Execute(center)
        End Sub

    End Class

End Namespace
