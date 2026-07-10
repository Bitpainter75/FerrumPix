Imports System.Globalization
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media

Namespace Controls

    ''' Lineal am Rand der Editor-Vorschau. Die Skala zählt Bildpixel, nicht Bildschirmpixel:
    ''' Origin ist die Bildschirmkoordinate von Bildpixel 0, PixelsPerUnit der Zoomfaktor.
    ''' Das Steuerelement liegt in derselben Grid-Spalte (waagerecht) bzw. -Zeile (senkrecht) wie
    ''' der Vorschau-Canvas, seine lokalen Koordinaten entlang der Skala entsprechen deshalb
    ''' unverändert denen des Canvas - EditorView reicht Canvas-Koordinaten direkt durch.
    Public Class RulerControl
        Inherits Control

        Public Shared ReadOnly IsHorizontalProperty As StyledProperty(Of Boolean) =
            AvaloniaProperty.Register(Of RulerControl, Boolean)(NameOf(IsHorizontal), True)

        Public Shared ReadOnly OriginProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of RulerControl, Double)(NameOf(Origin))

        Public Shared ReadOnly PixelsPerUnitProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of RulerControl, Double)(NameOf(PixelsPerUnit), 1.0)

        Public Shared ReadOnly ImageLengthProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of RulerControl, Double)(NameOf(ImageLength))

        ''' Lokale Koordinate des Mauszeigers entlang der Skala; NaN blendet die Markierung aus.
        Public Shared ReadOnly PointerOffsetProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of RulerControl, Double)(NameOf(PointerOffset), Double.NaN)

        Public Shared ReadOnly RulerBackgroundProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of RulerControl, IBrush)(NameOf(RulerBackground), Brushes.Transparent)

        ''' Hinterlegt den Abschnitt der Skala, der tatsächlich über dem Bild liegt.
        Public Shared ReadOnly ImageAreaBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of RulerControl, IBrush)(NameOf(ImageAreaBrush), Brushes.Transparent)

        Public Shared ReadOnly TickBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of RulerControl, IBrush)(NameOf(TickBrush), Brushes.Gray)

        Public Shared ReadOnly LabelBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of RulerControl, IBrush)(NameOf(LabelBrush), Brushes.Gray)

        Public Shared ReadOnly MarkerBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of RulerControl, IBrush)(NameOf(MarkerBrush), Brushes.OrangeRed)

        Private Const LabelFontSize As Double = 9.0

        Shared Sub New()
            AffectsRender(Of RulerControl)(IsHorizontalProperty, OriginProperty, PixelsPerUnitProperty,
                                           ImageLengthProperty, PointerOffsetProperty, RulerBackgroundProperty,
                                           ImageAreaBrushProperty, TickBrushProperty, LabelBrushProperty,
                                           MarkerBrushProperty)
        End Sub

        Public Property IsHorizontal As Boolean
            Get
                Return GetValue(IsHorizontalProperty)
            End Get
            Set(value As Boolean)
                SetValue(IsHorizontalProperty, value)
            End Set
        End Property

        Public Property Origin As Double
            Get
                Return GetValue(OriginProperty)
            End Get
            Set(value As Double)
                SetValue(OriginProperty, value)
            End Set
        End Property

        Public Property PixelsPerUnit As Double
            Get
                Return GetValue(PixelsPerUnitProperty)
            End Get
            Set(value As Double)
                SetValue(PixelsPerUnitProperty, value)
            End Set
        End Property

        Public Property ImageLength As Double
            Get
                Return GetValue(ImageLengthProperty)
            End Get
            Set(value As Double)
                SetValue(ImageLengthProperty, value)
            End Set
        End Property

        Public Property PointerOffset As Double
            Get
                Return GetValue(PointerOffsetProperty)
            End Get
            Set(value As Double)
                SetValue(PointerOffsetProperty, value)
            End Set
        End Property

        Public Property RulerBackground As IBrush
            Get
                Return GetValue(RulerBackgroundProperty)
            End Get
            Set(value As IBrush)
                SetValue(RulerBackgroundProperty, value)
            End Set
        End Property

        Public Property ImageAreaBrush As IBrush
            Get
                Return GetValue(ImageAreaBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(ImageAreaBrushProperty, value)
            End Set
        End Property

        Public Property TickBrush As IBrush
            Get
                Return GetValue(TickBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(TickBrushProperty, value)
            End Set
        End Property

        Public Property LabelBrush As IBrush
            Get
                Return GetValue(LabelBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(LabelBrushProperty, value)
            End Set
        End Property

        Public Property MarkerBrush As IBrush
            Get
                Return GetValue(MarkerBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(MarkerBrushProperty, value)
            End Set
        End Property

        ''' Kleinster Wert aus 1/2/5·10^k, dessen Abstand auf dem Schirm noch mindestens minSpacing
        ''' Punkte beträgt - damit die Beschriftungen bei jedem Zoom lesbar bleiben und nie kollidieren.
        Private Shared Function ChooseMajorStep(scale As Double, minSpacing As Double) As Double
            Dim magnitude = 1.0
            For k = 0 To 9
                For Each c In {1.0, 2.0, 5.0}
                    Dim candidate = c * magnitude
                    If candidate * scale >= minSpacing Then Return candidate
                Next
                magnitude *= 10.0
            Next
            Return magnitude
        End Function

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)

            Dim w = Bounds.Width
            Dim h = Bounds.Height
            If w <= 0 OrElse h <= 0 Then Return

            Dim horizontal = IsHorizontal
            Dim axisLength = If(horizontal, w, h)
            Dim thickness = If(horizontal, h, w)

            context.FillRectangle(RulerBackground, New Rect(0, 0, w, h))

            Dim scale = PixelsPerUnit
            If scale <= 0 OrElse Double.IsNaN(scale) OrElse Double.IsInfinity(scale) Then Return

            Dim imageStart = Math.Max(0.0, Origin)
            Dim imageEnd = Math.Min(axisLength, Origin + ImageLength * scale)
            If imageEnd > imageStart Then
                context.FillRectangle(ImageAreaBrush,
                                      If(horizontal,
                                         New Rect(imageStart, 0, imageEnd - imageStart, h),
                                         New Rect(0, imageStart, w, imageEnd - imageStart)))
            End If

            Dim majorStep = ChooseMajorStep(scale, If(horizontal, 62.0, 48.0))
            Dim minorStep = Math.Max(1.0, majorStep / 5.0)
            If minorStep * scale < 4.0 Then minorStep = majorStep

            Dim tickPen = New Pen(TickBrush, 1)
            Dim typeface = New Typeface(FontFamily.Default)

            ' Ganzzahliger Schrittzähler statt aufaddiertem Double: sonst driftet der Wert über die
            ' vielen kleinen Schritte hinweg und die Beschriftungen landen neben ihren Strichen.
            Dim firstIndex = CLng(Math.Floor((0.0 - Origin) / scale / minorStep))
            Dim lastIndex = CLng(Math.Ceiling((axisLength - Origin) / scale / minorStep))
            If lastIndex - firstIndex > 5000 Then Return

            For i = firstIndex To lastIndex
                Dim value = i * minorStep
                ' Halbe Pixel: sonst verschmiert eine 1px-Linie auf zwei Gerätepixel.
                Dim pos = Math.Floor(Origin + value * scale) + 0.5
                If pos < -1 OrElse pos > axisLength + 1 Then Continue For

                Dim majorRatio = value / majorStep
                Dim isMajor = Math.Abs(majorRatio - Math.Round(majorRatio)) < 0.0001
                Dim tickLength = If(isMajor, thickness * 0.6, thickness * 0.3)

                If horizontal Then
                    context.DrawLine(tickPen, New Point(pos, h - tickLength), New Point(pos, h))
                Else
                    context.DrawLine(tickPen, New Point(w - tickLength, pos), New Point(w, pos))
                End If

                If Not isMajor Then Continue For

                Dim label = New FormattedText(CLng(Math.Round(value)).ToString(CultureInfo.CurrentCulture),
                                              CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                              typeface, LabelFontSize, LabelBrush)
                If horizontal Then
                    context.DrawText(label, New Point(pos + 3, 1))
                Else
                    ' Um -90° gedreht liest sich die Zahl von unten nach oben und endet knapp über
                    ' ihrem Strich. Lokales (u,v) landet nach der Drehung auf (v, -u), der Ursprung
                    ' der Translation ist also die untere linke Ecke des Textes.
                    Dim rotated = context.PushTransform(Matrix.CreateRotation(-Math.PI / 2.0) *
                                                       Matrix.CreateTranslation(1, pos - 3))
                    context.DrawText(label, New Point(0, 0))
                    rotated.Dispose()
                End If
            Next

            Dim edgePen = New Pen(TickBrush, 1)
            If horizontal Then
                context.DrawLine(edgePen, New Point(0, h - 0.5), New Point(w, h - 0.5))
            Else
                context.DrawLine(edgePen, New Point(w - 0.5, 0), New Point(w - 0.5, h))
            End If

            Dim marker = PointerOffset
            If Not Double.IsNaN(marker) AndAlso marker >= 0 AndAlso marker <= axisLength Then
                Dim markerPen = New Pen(MarkerBrush, 1)
                Dim m = Math.Floor(marker) + 0.5
                If horizontal Then
                    context.DrawLine(markerPen, New Point(m, 0), New Point(m, h))
                Else
                    context.DrawLine(markerPen, New Point(0, m), New Point(w, m))
                End If
            End If
        End Sub

    End Class

End Namespace
