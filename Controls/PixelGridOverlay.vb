Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media

Namespace Controls

    ''' Raster über dem Editorbild. Die Zellengröße wird in Bildpixeln angegeben (Einstellungen ->
    ''' Editor -> Rastergröße), das Raster wandert und skaliert deshalb mit Zoom und Schwenk mit.
    ''' Das Steuerelement deckt den gesamten Vorschau-Canvas ab und zeichnet nur innerhalb des Bildes.
    Public Class PixelGridOverlay
        Inherits Control

        ''' Canvas-Koordinate der linken oberen Bildecke.
        Public Shared ReadOnly ImageOriginProperty As StyledProperty(Of Point) =
            AvaloniaProperty.Register(Of PixelGridOverlay, Point)(NameOf(ImageOrigin))

        ''' Bildschirmpunkte pro Bildpixel (Zoomfaktor).
        Public Shared ReadOnly PixelsPerUnitProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of PixelGridOverlay, Double)(NameOf(PixelsPerUnit), 1.0)

        Public Shared ReadOnly ImageSizeProperty As StyledProperty(Of Size) =
            AvaloniaProperty.Register(Of PixelGridOverlay, Size)(NameOf(ImageSize))

        ''' Kantenlänge einer Zelle in Bildpixeln.
        Public Shared ReadOnly GridSizeProperty As StyledProperty(Of Double) =
            AvaloniaProperty.Register(Of PixelGridOverlay, Double)(NameOf(GridSize), 50.0)

        Public Shared ReadOnly LineBrushProperty As StyledProperty(Of IBrush) =
            AvaloniaProperty.Register(Of PixelGridOverlay, IBrush)(NameOf(LineBrush),
                                                                   New SolidColorBrush(Color.FromArgb(90, 255, 255, 255)))

        ''' Enger als das dürfen zwei Rasterlinien auf dem Schirm nicht liegen - sonst füllt das Raster
        ''' bei kleiner Zellengröße oder weit herausgezoomtem Bild die Fläche zu einem grauen Schleier.
        Private Const MinimumScreenSpacing As Double = 4.0

        Shared Sub New()
            AffectsRender(Of PixelGridOverlay)(ImageOriginProperty, PixelsPerUnitProperty, ImageSizeProperty,
                                               GridSizeProperty, LineBrushProperty)
        End Sub

        Public Property ImageOrigin As Point
            Get
                Return GetValue(ImageOriginProperty)
            End Get
            Set(value As Point)
                SetValue(ImageOriginProperty, value)
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

        Public Property ImageSize As Size
            Get
                Return GetValue(ImageSizeProperty)
            End Get
            Set(value As Size)
                SetValue(ImageSizeProperty, value)
            End Set
        End Property

        Public Property GridSize As Double
            Get
                Return GetValue(GridSizeProperty)
            End Get
            Set(value As Double)
                SetValue(GridSizeProperty, value)
            End Set
        End Property

        Public Property LineBrush As IBrush
            Get
                Return GetValue(LineBrushProperty)
            End Get
            Set(value As IBrush)
                SetValue(LineBrushProperty, value)
            End Set
        End Property

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)

            Dim w = Bounds.Width
            Dim h = Bounds.Height
            Dim scale = PixelsPerUnit
            Dim cell = GridSize
            If w <= 0 OrElse h <= 0 OrElse cell <= 0 Then Return
            If scale <= 0 OrElse Double.IsNaN(scale) OrElse Double.IsInfinity(scale) Then Return

            Dim spacing = cell * scale
            If spacing < MinimumScreenSpacing Then Return

            ' Sichtbarer Teil des Bildes: außerhalb davon hat das Raster keine Bedeutung.
            Dim imageRect = New Rect(ImageOrigin, New Size(ImageSize.Width * scale, ImageSize.Height * scale))
            Dim visible = imageRect.Intersect(New Rect(0, 0, w, h))
            If visible.Width <= 0 OrElse visible.Height <= 0 Then Return

            Dim pen = New Pen(LineBrush, 1)
            Dim clip = context.PushClip(visible)

            Dim firstColumn = CLng(Math.Ceiling((visible.Left - imageRect.Left) / spacing))
            Dim lastColumn = CLng(Math.Floor((visible.Right - imageRect.Left) / spacing))
            For column = firstColumn To lastColumn
                Dim x = Math.Floor(imageRect.Left + column * spacing) + 0.5
                context.DrawLine(pen, New Point(x, visible.Top), New Point(x, visible.Bottom))
            Next

            Dim firstRow = CLng(Math.Ceiling((visible.Top - imageRect.Top) / spacing))
            Dim lastRow = CLng(Math.Floor((visible.Bottom - imageRect.Top) / spacing))
            For row = firstRow To lastRow
                Dim y = Math.Floor(imageRect.Top + row * spacing) + 0.5
                context.DrawLine(pen, New Point(visible.Left, y), New Point(visible.Right, y))
            Next

            clip.Dispose()
        End Sub

    End Class

End Namespace
