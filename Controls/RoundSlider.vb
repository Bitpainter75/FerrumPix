Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Data
Imports Avalonia.Input
Imports Avalonia.Media
Imports Avalonia.Rendering

Namespace Controls

    Public Class RoundSlider
        Inherits Control
        Implements ICustomHitTest

        Public Shared ReadOnly MinimumProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of RoundSlider, Double)(NameOf(Minimum), 0)

        Public Shared ReadOnly MaximumProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of RoundSlider, Double)(NameOf(Maximum), 100)

        Public Shared ReadOnly ValueProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of RoundSlider, Double)(NameOf(Value), 0, defaultBindingMode:=BindingMode.TwoWay)

        Public Shared ReadOnly DefaultValueProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of RoundSlider, Double)(NameOf(DefaultValue), 0)

        Public Shared ReadOnly TrackBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of RoundSlider, IBrush)(NameOf(TrackBrush), New SolidColorBrush(Color.Parse("#26313B")))

        Public Shared ReadOnly FillBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of RoundSlider, IBrush)(NameOf(FillBrush), New SolidColorBrush(Color.Parse("#F08A1A")))

        Public Shared ReadOnly ThumbBorderBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of RoundSlider, IBrush)(NameOf(ThumbBorderBrush), New SolidColorBrush(Color.Parse("#0B0E11")))

        Public Shared ReadOnly WheelIncrementProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of RoundSlider, Double)(NameOf(WheelIncrement), 0)

        Public Shared ReadOnly DragIncrementProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of RoundSlider, Double)(NameOf(DragIncrement), 0)

        Private _isDragging As Boolean
        Private _dragStartX As Double
        Private _dragStartValue As Double

        Shared Sub New()
            AffectsRender(Of RoundSlider)(MinimumProperty, MaximumProperty, ValueProperty, TrackBrushProperty, FillBrushProperty, ThumbBorderBrushProperty)
        End Sub

        Public Sub New()
            Cursor = New Cursor(StandardCursorType.Hand)
            MinHeight = 24
        End Sub

        ''' Garantiert unabhängig vom Theme-Style (FerrumPixTheme.axaml setzt Height=36, könnte aber
        ''' durch spätere Änderungen/spezifischere Selektoren überschrieben werden) eine Mindesthöhe,
        ''' die mindestens dem Thumb-Durchmesser (2*8=16px) entspricht.
        Protected Overrides Function MeasureOverride(availableSize As Size) As Size
            Dim thumbDiameter = 16.0
            Dim desired = MyBase.MeasureOverride(availableSize)
            Return New Size(desired.Width, Math.Max(desired.Height, thumbDiameter))
        End Function

        ''' Explizit statt auf das Standardverhalten zu vertrauen: der GESAMTE Bounds-Bereich (nicht
        ''' nur die 4px dünn gezeichnete Linie) muss als Klickfläche zählen - OnPointerPressed/
        ''' SetValueFromPoint werten ohnehin nur die X-Koordinate aus. ICustomHitTest erzwingt dies
        ''' eindeutig, unabhängig von sonstigem Geometrie-/Clip-bezogenem Hit-Testing-Verhalten.
        Public Function HitTest(point As Point) As Boolean Implements ICustomHitTest.HitTest
            Return New Rect(Bounds.Size).Contains(point)
        End Function

        Public Property Minimum As Double
            Get
                Return GetValue(MinimumProperty)
            End Get
            Set(value As Double)
                SetValue(MinimumProperty, value)
            End Set
        End Property

        Public Property Maximum As Double
            Get
                Return GetValue(MaximumProperty)
            End Get
            Set(value As Double)
                SetValue(MaximumProperty, value)
            End Set
        End Property

        Public Property Value As Double
            Get
                Return GetValue(ValueProperty)
            End Get
            Set(value As Double)
                SetValue(ValueProperty, ClampValue(value))
            End Set
        End Property

        Public Property DefaultValue As Double
            Get
                Return GetValue(DefaultValueProperty)
            End Get
            Set(value As Double)
                SetValue(DefaultValueProperty, value)
            End Set
        End Property

        Public Property TrackBrush As IBrush
            Get
                Return GetValue(TrackBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(TrackBrushProperty, value)
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

        Public Property ThumbBorderBrush As IBrush
            Get
                Return GetValue(ThumbBorderBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(ThumbBorderBrushProperty, value)
            End Set
        End Property

        Public Property WheelIncrement As Double
            Get
                Return GetValue(WheelIncrementProperty)
            End Get
            Set(value As Double)
                SetValue(WheelIncrementProperty, Math.Max(0, value))
            End Set
        End Property

        Public Property DragIncrement As Double
            Get
                Return GetValue(DragIncrementProperty)
            End Get
            Set(value As Double)
                SetValue(DragIncrementProperty, Math.Max(0, value))
            End Set
        End Property

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)

            Dim width = Bounds.Width
            Dim height = Bounds.Height
            If width <= 0 OrElse height <= 0 Then Return

            Dim thumbRadius = 8.0
            Dim trackStart = thumbRadius
            Dim trackEnd = Math.Max(trackStart, width - thumbRadius)
            Dim trackWidth = trackEnd - trackStart
            Dim centerY = height / 2.0
            Dim ratio = GetRatio()
            Dim thumbX = trackStart + trackWidth * ratio

            Dim trackPen = New Pen(TrackBrush, 4)
            Dim fillPen = New Pen(FillBrush, 4)
            context.DrawLine(trackPen, New Point(trackStart, centerY), New Point(trackEnd, centerY))
            context.DrawLine(fillPen, New Point(trackStart, centerY), New Point(thumbX, centerY))
            context.DrawEllipse(FillBrush, New Pen(ThumbBorderBrush, 2), New Point(thumbX, centerY), thumbRadius, thumbRadius)
        End Sub

        Protected Overrides Sub OnPointerPressed(e As PointerPressedEventArgs)
            MyBase.OnPointerPressed(e)
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            If e.ClickCount >= 2 Then
                Value = ClampValue(DefaultValue)
                e.Handled = True
                Return
            End If
            _isDragging = True
            _dragStartX = e.GetPosition(Me).X
            _dragStartValue = Value
            e.Pointer.Capture(Me)
            SetValueFromPoint(e.GetPosition(Me).X)
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerMoved(e As PointerEventArgs)
            MyBase.OnPointerMoved(e)
            If Not _isDragging Then Return
            If DragIncrement > 0 Then
                Dim deltaX = e.GetPosition(Me).X - _dragStartX
                Value = ClampValue(_dragStartValue + deltaX * DragIncrement)
            Else
                SetValueFromPoint(e.GetPosition(Me).X)
            End If
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerReleased(e As PointerReleasedEventArgs)
            MyBase.OnPointerReleased(e)
            _isDragging = False
            _dragStartX = 0
            _dragStartValue = 0
            e.Pointer.Capture(Nothing)
            e.Handled = True
        End Sub

        Protected Overrides Sub OnPointerWheelChanged(e As PointerWheelEventArgs)
            MyBase.OnPointerWheelChanged(e)
            Dim increment = If(WheelIncrement > 0, WheelIncrement, Math.Max(0.1, (Maximum - Minimum) * 0.0025))
            Value = ClampValue(Value + e.Delta.Y * increment)
            e.Handled = True
        End Sub

        Private Sub SetValueFromPoint(x As Double)
            Dim thumbRadius = 8.0
            Dim usableWidth = Math.Max(1, Bounds.Width - thumbRadius * 2)
            Dim ratio = Math.Max(0, Math.Min(1, (x - thumbRadius) / usableWidth))
            Value = Minimum + (Maximum - Minimum) * ratio
        End Sub

        Private Function GetRatio() As Double
            If Maximum <= Minimum Then Return 0
            Return Math.Max(0, Math.Min(1, (Value - Minimum) / (Maximum - Minimum)))
        End Function

        Private Function ClampValue(value As Double) As Double
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return Minimum
            Return Math.Max(Minimum, Math.Min(Maximum, value))
        End Function

    End Class

End Namespace
