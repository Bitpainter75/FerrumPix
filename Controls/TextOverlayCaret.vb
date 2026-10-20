Imports System
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Media
Imports Avalonia.Threading
Imports FerrumPix.Services

Namespace Controls
    ''' <summary>Schreibmarke für den unsichtbaren Editor über einem gerenderten Textobjekt.</summary>
    Public Class TextOverlayCaret
        Inherits Control

        Private ReadOnly _timer As New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(500)}
        Private _editor As TextBox
        Private _visible As Boolean = True

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

        Public Overrides Sub Render(context As DrawingContext)
            MyBase.Render(context)
            If Not _visible OrElse _editor Is Nothing OrElse Not _editor.IsVisible OrElse Not _editor.IsFocused Then Return
            Dim origin = _editor.TranslatePoint(New Point(0, 0), Me)
            If Not origin.HasValue Then Return
            ' Großbuchstabenhöhe statt der gesamten Zeilenhöhe mit Ober- und Unterlängen.
            Dim ink = ImageProcessor.MeasureAnnotationTextInk("H", _editor.FontFamily.Name, CSng(_editor.FontSize), 0,
                                                               _editor.FontWeight = FontWeight.Bold, _editor.FontStyle = FontStyle.Italic, 0)
            If ink.IsEmpty Then Return
            Dim offset = ImageProcessor.GetBakedTextTopOffset(_editor.FontFamily.Name, CSng(_editor.FontSize),
                                                               _editor.Text, _editor.FontWeight = FontWeight.Bold,
                                                               _editor.FontStyle = FontStyle.Italic)
            Dim hit = ImageProcessor.MeasureAnnotationCaret(_editor.Text, _editor.CaretIndex, _editor.FontFamily.Name,
                                                            CSng(_editor.FontSize), CSng(_editor.LetterSpacing),
                                                            _editor.FontWeight = FontWeight.Bold, _editor.FontStyle = FontStyle.Italic,
                                                            _editor.TextAlignment.ToString(), CSng(_editor.Bounds.Width))
            Dim x = origin.Value.X + hit.X
            Dim top = origin.Value.Y - offset + hit.Y + ink.Top
            context.DrawLine(New Pen(CaretColor, 1.0), New Point(x, top), New Point(x, top + ink.Height))
        End Sub
    End Class
End Namespace
