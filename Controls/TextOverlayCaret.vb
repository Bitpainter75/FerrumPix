Imports System
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media
Imports Avalonia.Threading
Imports FerrumPix.Services

Namespace Controls
    ''' <summary>Schreibmarke für den unsichtbaren Editor über einem gerenderten Textobjekt.
    '''
    ''' Lage und Größe kommen NICHT aus dem Layout der Textbox, sondern aus denselben Werten, mit
    ''' denen der Bildrenderer den Text zeichnet (<see cref="SetLayout"/>). Die Textbox liefert nur
    ''' Text und Schreibstelle. Ihr Layout taugt nicht als Maß: das Thema gibt ihr eine
    ''' Mindestbreite (bei schmalem Text stimmte die Mitte nicht mehr), die Layout-Rundung
    ''' verschiebt sie um Bruchteile eines Punktes, und während eines Griffzugs steht sie noch auf
    ''' dem Stand vor dem Zug.</summary>
    Public Class TextOverlayCaret
        Inherits Control

        Private ReadOnly _timer As New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(500)}
        Private _editor As TextBox
        Private _visible As Boolean = True
        Private _boxOrigin As Point
        Private _lineWidth As Double
        Private _fontFamily As String = ""
        Private _fontSize As Double
        Private _spacing As Double
        Private _bold As Boolean
        Private _italic As Boolean
        Private _alignment As String = "Left"

        Public Sub New()
            AddHandler _timer.Tick, Sub()
                                        _visible = Not _visible
                                        InvalidateVisual()
                                    End Sub
        End Sub

        Public Property Editor As TextBox
            Get
                Return _editor
            End Get
            Set(value As TextBox)
                If _editor Is value Then Return
                If _editor IsNot Nothing Then RemoveHandler _editor.PropertyChanged, AddressOf EditorChanged
                _editor = value
                If _editor IsNot Nothing Then AddHandler _editor.PropertyChanged, AddressOf EditorChanged
                InvalidateVisual()
            End Set
        End Property

        Public Property CaretColor As IBrush = Brushes.White

        ''' <summary>Der Satz, wie ihn der Renderer sieht, in Punkten dieses Steuerelements.</summary>
        ''' <param name="boxOrigin">Zeichnungsursprung des Textkastens; die erste Grundlinie liegt
        ''' eine Schriftgröße darunter.</param>
        ''' <param name="lineWidth">Breite des Kastens, an ihr hängt die Ausrichtung.</param>
        ''' <param name="spacing">Zeichenabstand in Punkten.</param>
        Public Sub SetLayout(boxOrigin As Point, lineWidth As Double, fontFamily As String, fontSize As Double,
                             spacing As Double, bold As Boolean, italic As Boolean, alignment As String)
            _boxOrigin = boxOrigin
            _lineWidth = lineWidth
            _fontFamily = If(fontFamily, "")
            _fontSize = fontSize
            _spacing = spacing
            _bold = bold
            _italic = italic
            _alignment = If(alignment, "Left")
            _visible = True
            InvalidateVisual()
        End Sub

        Protected Overrides Sub OnAttachedToVisualTree(e As VisualTreeAttachmentEventArgs)
            MyBase.OnAttachedToVisualTree(e)
            _timer.Start()
        End Sub

        Protected Overrides Sub OnDetachedFromVisualTree(e As VisualTreeAttachmentEventArgs)
            _timer.Stop()
            MyBase.OnDetachedFromVisualTree(e)
        End Sub

        Private Sub EditorChanged(sender As Object, e As AvaloniaPropertyChangedEventArgs)
            _visible = True
            InvalidateVisual()
        End Sub

        ''' <summary>Oberkante und Höhe der Marke: die Großbuchstabenhöhe statt der ganzen
        ''' Zeilenhöhe mit Ober- und Unterlängen, gemessen ab dem Zeichnungsursprung.</summary>
        Private Function CapInk() As SkiaSharp.SKRect
            Return ImageProcessor.MeasureAnnotationTextInk("H", _fontFamily, CSng(_fontSize), 0, _bold, _italic, 0)
        End Function

        Private Function CaretAt(text As String, index As Integer) As SkiaSharp.SKPoint
            Return ImageProcessor.MeasureAnnotationCaret(text, index, _fontFamily, CSng(_fontSize), CSng(_spacing),
                                                         _bold, _italic, _alignment, CSng(_lineWidth))
        End Function

        ''' <summary>Die Zeichenstelle, die einem Punkt dieses Steuerelements am nächsten liegt.</summary>
        Public Function IndexAt(point As Point) As Integer
            If _editor Is Nothing OrElse _fontSize <= 0 Then Return -1
            Dim text = If(_editor.Text, "")
            Dim ink = CapInk()
            Dim lineCenter = If(ink.IsEmpty, _fontSize / 2.0, ink.MidY)
            Dim best = 0
            Dim bestDistance = Double.PositiveInfinity
            For index = 0 To text.Length
                Dim hit = CaretAt(text, index)
                Dim dx = point.X - (_boxOrigin.X + hit.X)
                Dim dy = point.Y - (_boxOrigin.Y + hit.Y + lineCenter)
                Dim distance = dx * dx + dy * dy * 4.0
                If distance < bestDistance Then
                    best = index
                    bestDistance = distance
                End If
            Next
            Return best
        End Function

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)
            If Not _visible OrElse _editor Is Nothing OrElse Not _editor.IsVisible OrElse Not _editor.IsFocused Then Return
            If _fontSize <= 0 Then Return
            Dim ink = CapInk()
            If ink.IsEmpty Then Return
            Dim hit = CaretAt(_editor.Text, _editor.CaretIndex)
            Dim x = _boxOrigin.X + hit.X
            Dim top = _boxOrigin.Y + hit.Y + ink.Top
            context.DrawLine(New Pen(CaretColor, 1.0), New Point(x, top), New Point(x, top + ink.Height))
        End Sub
    End Class
End Namespace
