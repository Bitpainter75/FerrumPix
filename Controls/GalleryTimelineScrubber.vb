Imports System.Globalization
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Media

Namespace Controls

    ''' <summary>Ein Segment der Galerie-Zeitleiste: Startindex in der (sortierten) Item-Liste plus
    ''' die Beschriftungen. <see cref="Label"/> ist der Grob-Marker an der Leiste (Jahr bzw.
    ''' Anfangsbuchstabe, leer bei "kein neuer Marker"), <see cref="DetailLabel"/> der Text der
    ''' Sprechblase beim Zeigen/Ziehen (z.B. "März 2024").</summary>
    Public Class GalleryTimelineSegment
        Public Property Label As String = ""
        Public Property DetailLabel As String = ""
        Public Property StartIndex As Integer
    End Class

    ''' <summary>Zeitleiste am rechten Galerierand (wie in Immich): zeigt die Grob-Marker der
    ''' aktuellen Sortierung (Jahre bzw. Anfangsbuchstaben), ein Positions-Band für den sichtbaren
    ''' Ausschnitt und beim Zeigen/Ziehen eine Sprechblase mit dem Feineintrag. Klick/Ziehen meldet
    ''' die gewünschte Scroll-Position über <see cref="ScrubRequested"/> (0..1 der Scroll-Strecke).
    ''' Das Control hält keinerlei ViewModel-Bezug: die View füttert es über SetData/SetScrollState.</summary>
    Public Class GalleryTimelineScrubber
        Inherits Control

        Public Shared ReadOnly LabelBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of GalleryTimelineScrubber, IBrush)(NameOf(LabelBrush), Brushes.Gray)
        Public Shared ReadOnly AccentBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of GalleryTimelineScrubber, IBrush)(NameOf(AccentBrush), Brushes.SteelBlue)
        Public Shared ReadOnly BubbleBackgroundProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of GalleryTimelineScrubber, IBrush)(NameOf(BubbleBackground), Brushes.Black)
        Public Shared ReadOnly BubbleForegroundProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of GalleryTimelineScrubber, IBrush)(NameOf(BubbleForeground), Brushes.White)

        Public Property LabelBrush As IBrush
            Get
                Return GetValue(LabelBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(LabelBrushProperty, value)
            End Set
        End Property

        Public Property AccentBrush As IBrush
            Get
                Return GetValue(AccentBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(AccentBrushProperty, value)
            End Set
        End Property

        Public Property BubbleBackground As IBrush
            Get
                Return GetValue(BubbleBackgroundProperty)
            End Get
            Set(value As IBrush)
                SetValue(BubbleBackgroundProperty, value)
            End Set
        End Property

        Public Property BubbleForeground As IBrush
            Get
                Return GetValue(BubbleForegroundProperty)
            End Get
            Set(value As IBrush)
                SetValue(BubbleForegroundProperty, value)
            End Set
        End Property

        ''' Gewünschte Scroll-Position als Anteil 0..1 der Scroll-Strecke (Offset / (Extent - Viewport)).
        Public Event ScrubRequested As EventHandler(Of Double)

        Private _segments As IReadOnlyList(Of GalleryTimelineSegment)
        Private _totalCount As Integer
        Private _offsetFraction As Double
        Private _viewportFraction As Double = 1.0
        Private _pointerFraction As Double = -1.0 ' -1 = Zeiger nicht über der Leiste
        Private _isDragging As Boolean

        Public Sub SetData(segments As IReadOnlyList(Of GalleryTimelineSegment), totalCount As Integer)
            _segments = segments
            _totalCount = Math.Max(0, totalCount)
            InvalidateVisual()
        End Sub

        ''' offsetFraction: Offset / (Extent - Viewport), 0..1. viewportFraction: Viewport / Extent, 0..1.
        Public Sub SetScrollState(offsetFraction As Double, viewportFraction As Double)
            _offsetFraction = Math.Max(0.0, Math.Min(1.0, offsetFraction))
            _viewportFraction = Math.Max(0.001, Math.Min(1.0, viewportFraction))
            InvalidateVisual()
        End Sub

        Protected Overrides Sub OnPointerPressed(e As PointerPressedEventArgs)
            MyBase.OnPointerPressed(e)
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            _isDragging = True
            e.Pointer.Capture(Me)
            UpdatePointer(e)
            RaiseScrub()
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerMoved(e As PointerEventArgs)
            MyBase.OnPointerMoved(e)
            UpdatePointer(e)
            If _isDragging Then
                RaiseScrub()
                e.Handled = True
            End If
        End Sub

        Protected Overrides Sub OnPointerReleased(e As PointerReleasedEventArgs)
            MyBase.OnPointerReleased(e)
            If _isDragging Then
                _isDragging = False
                e.Pointer.Capture(Nothing)
                e.Handled = True
            End If
        End Sub

        Protected Overrides Sub OnPointerExited(e As PointerEventArgs)
            MyBase.OnPointerExited(e)
            If Not _isDragging Then
                _pointerFraction = -1.0
                InvalidateVisual()
            End If
        End Sub

        Private Sub UpdatePointer(e As PointerEventArgs)
            Dim h = Bounds.Height
            If h <= 0 Then Return
            _pointerFraction = Math.Max(0.0, Math.Min(1.0, e.GetPosition(Me).Y / h))
            InvalidateVisual()
        End Sub

        ''' Der Zeiger steht auf einer INHALTS-Position (0..1 der Leiste = der Item-Liste); für den
        ''' ScrollViewer wird daraus die Offset-Position, bei der dieser Inhalt mittig im Sichtfenster
        ''' liegt (an den Rändern geklemmt).
        Private Sub RaiseScrub()
            If _pointerFraction < 0 Then Return
            Dim range = Math.Max(0.0001, 1.0 - _viewportFraction)
            Dim offsetFraction = Math.Max(0.0, Math.Min(1.0, (_pointerFraction - _viewportFraction / 2.0) / range))
            RaiseEvent ScrubRequested(Me, offsetFraction)
        End Sub

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)
            ' Unsichtbarer Treffbereich über die volle Breite - ohne Füllung gäbe es keine Pointer-Events.
            context.FillRectangle(Brushes.Transparent, New Rect(Bounds.Size))

            If _segments Is Nothing OrElse _segments.Count = 0 OrElse _totalCount <= 0 Then Return
            Dim w = Bounds.Width
            Dim h = Bounds.Height
            If w <= 0 OrElse h <= 0 Then Return

            Dim typeface = New Typeface(FontFamily.Default)

            ' Grob-Marker (Jahre/Buchstaben) mit Mindestabstand, damit dichte Jahre nicht überlappen.
            Dim lastLabelY As Double = Double.MinValue
            Dim tickPen = New Pen(LabelBrush, 1.0)
            For Each segment In _segments
                If String.IsNullOrEmpty(segment.Label) Then Continue For
                Dim y = segment.StartIndex / CDbl(_totalCount) * h
                If y - lastLabelY < 16.0 Then Continue For
                lastLabelY = y
                Dim label = New FormattedText(segment.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                              typeface, 10.0, LabelBrush)
                context.DrawText(label, New Point(w - label.Width - 12.0, Math.Min(h - label.Height, y)))
                context.DrawLine(tickPen, New Point(w - 8.0, y), New Point(w - 3.0, y))
            Next

            ' Positions-Band des sichtbaren Ausschnitts (Mini-Scrollbalken in Akzentfarbe).
            Dim thumbH = Math.Max(24.0, _viewportFraction * h)
            Dim thumbY = _offsetFraction * (h - thumbH)
            context.DrawRectangle(AccentBrush, Nothing, New RoundedRect(New Rect(w - 3.0, thumbY, 3.0, thumbH), 1.5))

            ' Sprechblase am Zeiger: Feineintrag des Segments an dieser Inhalts-Position.
            If _pointerFraction >= 0 Then
                Dim index = CInt(Math.Floor(_pointerFraction * (_totalCount - 1)))
                Dim detail As String = Nothing
                For i = _segments.Count - 1 To 0 Step -1
                    If _segments(i).StartIndex <= index Then
                        detail = _segments(i).DetailLabel
                        Exit For
                    End If
                Next
                If Not String.IsNullOrEmpty(detail) Then
                    Dim text = New FormattedText(detail, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                                 typeface, 12.0, BubbleForeground)
                    Dim padX = 10.0
                    Dim padY = 5.0
                    Dim bubbleW = text.Width + padX * 2
                    Dim bubbleH = text.Height + padY * 2
                    Dim y = Math.Max(0.0, Math.Min(h - bubbleH, _pointerFraction * h - bubbleH / 2.0))
                    Dim rect = New Rect(w - bubbleW - 14.0, y, bubbleW, bubbleH)
                    context.DrawRectangle(BubbleBackground, New Pen(AccentBrush, 1.0), New RoundedRect(rect, 6.0))
                    context.DrawText(text, New Point(rect.X + padX, rect.Y + padY))
                End If
            End If
        End Sub
    End Class

End Namespace
