Imports System.Collections.Generic
Imports System.Collections.Specialized
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Threading
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Eine Flaeche des Bildvergleichs, siehe ComparePaneView.axaml. Die Knoepfe auf der
    ''' Flaeche rufen den Betrachter mit dem Index der Flaeche; die Fokusfelder zeichnet sie
    ''' selbst auf ihre Ebene. Zeiger, Zoom und das Spiegeln des Ausschnitts liegen in ViewerView.</summary>
    Public Class ComparePaneView
        Inherits UserControl

        Private _pane As ComparePaneViewModel
        Private _layoutHooked As Boolean
        Private _rebuildQueued As Boolean
        Private ReadOnly _marks As New List(Of FocusMark)()

        ''' <summary>Ein Fokusfeld auf der Ebene: seine Lage in Anteilen des Bildes, die beiden
        ''' Linienzuege (dunkel unten, gruen oben) und das zuletzt gesetzte Rechteck.</summary>
        Private NotInheritable Class FocusMark
            Public Frame As FocusFrame
            Public Shadow As Shapes.Path
            Public Line As Shapes.Path
            Public Placed As New Rect(Double.NaN, Double.NaN, 0, 0)
        End Class

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            AddHandler DataContextChanged, AddressOf OnPaneChanged
        End Sub

        ''' <summary>Der Rollbereich der Flaeche; ViewerView haengt Zeiger und Spiegeln daran.</summary>
        Public ReadOnly Property Scroll As ScrollViewer
            Get
                Return Me.FindControl(Of ScrollViewer)("PaneScroll")
            End Get
        End Property

        ''' <summary>Das Bild der Flaeche; ViewerView gibt ihm die Masse zum Zoom.</summary>
        Public ReadOnly Property PaneImage As Image
            Get
                Return Me.FindControl(Of Image)("PaneImage")
            End Get
        End Property

        ''' <summary>Die Peaking-Ebene; sie bekommt dieselben Masse wie das Bild.</summary>
        Public ReadOnly Property PeakingImage As Image
            Get
                Return Me.FindControl(Of Image)("PanePeakingImage")
            End Get
        End Property

        ''' <summary>Die Flaeche, die dieses Steuerelement zeigt.</summary>
        Public ReadOnly Property Pane As ComparePaneViewModel
            Get
                Return TryCast(DataContext, ComparePaneViewModel)
            End Get
        End Property

        Private Sub OnPaneChanged(sender As Object, e As EventArgs)
            If _pane IsNot Nothing Then RemoveHandler _pane.FocusFrames.CollectionChanged, AddressOf OnFocusFramesChanged
            _pane = Pane
            If _pane IsNot Nothing Then AddHandler _pane.FocusFrames.CollectionChanged, AddressOf OnFocusFramesChanged
            RebuildFocusMarks()
        End Sub

        ' ── Fokusfelder ─────────────────────────────────────────────────────────
        '
        ' Wie in der Einzelansicht (ViewerView, Markierungen ueber dem Bild): aufgebaut wird nur,
        ' wenn sich die Liste aendert, und zwar gesammelt einen Takt spaeter; verschoben nach
        ' jedem Layoutlauf des Bildes. Bleibt das Rechteck gleich, wird nichts gesetzt.

        Private Sub OnFocusFramesChanged(sender As Object, e As NotifyCollectionChangedEventArgs)
            If _rebuildQueued Then Return
            _rebuildQueued = True
            Dispatcher.UIThread.Post(
                Sub()
                    _rebuildQueued = False
                    RebuildFocusMarks()
                End Sub, DispatcherPriority.Render)
        End Sub

        Private Sub RebuildFocusMarks()
            Dim canvas = Me.FindControl(Of Canvas)("PaneMarkCanvas")
            If canvas Is Nothing Then Return
            canvas.Children.Clear()
            _marks.Clear()
            If _pane Is Nothing Then Return
            If Not _layoutHooked Then
                Dim image = PaneImage
                If image IsNot Nothing Then
                    AddHandler image.LayoutUpdated, Sub(s, e) UpdateFocusMarkPositions()
                    _layoutHooked = True
                End If
            End If
            For Each frame In _pane.FocusFrames
                Dim shadow As New Shapes.Path With {.Stroke = ViewerView.FocusMarkShadowBrush, .StrokeThickness = 4, .IsHitTestVisible = False}
                Dim line As New Shapes.Path With {.Stroke = ViewerView.FocusMarkBrush, .StrokeThickness = 2, .IsHitTestVisible = False}
                canvas.Children.Add(shadow)
                canvas.Children.Add(line)
                _marks.Add(New FocusMark With {.Frame = frame, .Shadow = shadow, .Line = line})
            Next
            UpdateFocusMarkPositions()
        End Sub

        ''' <summary>Legt die Felder auf das Bild, wie es gerade gezoomt und verschoben ist.</summary>
        Private Sub UpdateFocusMarkPositions()
            If _marks.Count = 0 Then Return
            Dim image = PaneImage
            Dim layer = Me.FindControl(Of Canvas)("PaneMarkCanvas")
            If image Is Nothing OrElse layer Is Nothing Then Return
            Dim size = image.Bounds.Size
            If size.Width <= 0 OrElse size.Height <= 0 Then Return
            For Each mark In _marks
                Dim topLeft = image.TranslatePoint(New Point(mark.Frame.Left * size.Width, mark.Frame.Top * size.Height), layer)
                Dim bottomRight = image.TranslatePoint(New Point(mark.Frame.Right * size.Width, mark.Frame.Bottom * size.Height), layer)
                If Not topLeft.HasValue OrElse Not bottomRight.HasValue Then Continue For
                Dim area As New Rect(topLeft.Value, bottomRight.Value)
                If area = mark.Placed Then Continue For
                mark.Placed = area
                Dim geometry = ViewerView.FocusMarkGeometry(area, mark.Frame.HasSize)
                mark.Shadow.Data = geometry
                mark.Line.Data = geometry
            Next
        End Sub

        ' ── Marken ──────────────────────────────────────────────────────────────

        Private Shared Function NumberFrom(sender As Object, ByRef value As Integer) As Boolean
            Dim tag = TryCast(TryCast(sender, Control)?.Tag, String)
            Return Integer.TryParse(tag, Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture, value)
        End Function

        Private Sub OnStarClick(sender As Object, e As RoutedEventArgs)
            Dim stars As Integer
            Dim target = Pane
            If target IsNot Nothing AndAlso NumberFrom(sender, stars) Then target.Owner.SetCompareRating(target.Index, stars)
            e.Handled = True
        End Sub

        Private Sub OnFavoriteClick(sender As Object, e As RoutedEventArgs)
            Dim target = Pane
            If target IsNot Nothing Then target.Owner.ToggleCompareFavorite(target.Index)
            e.Handled = True
        End Sub

        ''' <summary>Behalten und Verwerfen; der Tag traegt den Zustand ("1", "-1").</summary>
        Private Sub OnPickClick(sender As Object, e As RoutedEventArgs)
            Dim state As Integer
            Dim target = Pane
            If target IsNot Nothing AndAlso NumberFrom(sender, state) Then target.Owner.ToggleComparePickState(target.Index, state)
            e.Handled = True
        End Sub

        Private Sub OnColorLabelClick(sender As Object, e As RoutedEventArgs)
            Dim target = Pane
            If target IsNot Nothing Then target.Owner.SetCompareColorLabel(target.Index, TryCast(TryCast(sender, Control)?.Tag, String))
            e.Handled = True
        End Sub

        Private Sub OnEditClick(sender As Object, e As RoutedEventArgs)
            Dim target = Pane
            If target IsNot Nothing Then target.Owner.OpenComparePaneInEditor(target.Index)
            e.Handled = True
        End Sub

        Private Sub OnDeleteClick(sender As Object, e As RoutedEventArgs)
            Dim target = Pane
            If target IsNot Nothing Then target.Owner.DeleteComparePane(target.Index)
            e.Handled = True
        End Sub

    End Class

End Namespace
