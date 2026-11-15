Imports System
Imports System.Collections.Generic
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.VisualTree
Imports FerrumPix.Models
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Der Dialog zum Ordnen der Stichwörter. Er hängt an seinem eigenen ViewModel
    ''' (<see cref="KeywordManagerViewModel"/>) und liegt in der Galerie, weil die Ordnung nur den
    ''' lokalen Katalog betrifft und von dort aus geöffnet wird.
    '''
    ''' DAS ZIEHEN IST EIN EIGENER ZEIGERZUG, kein Ziehen des Systems: derselbe Grund wie in der
    ''' Galerie, wo ein Zug innerhalb der Anwendung unter X11 trotz angenommenem Ziel oft den
    ''' Verbotszeiger zeigte. Der Dialog nimmt den Zeigergriff, sobald der Zeiger sich mit
    ''' gedrückter Taste weit genug von einer Zeile wegbewegt hat, sucht die Zeile unter dem
    ''' Zeiger selbst und legt beim Loslassen ab. Ein Klick ohne Bewegung bleibt ein Klick.</summary>
    Public Class KeywordManagerOverlayView
        Inherits UserControl

        ''' <summary>Ab dieser Strecke gilt ein gedrückter Zeiger als Zug und nicht mehr als Klick.</summary>
        Private Const DragThreshold As Double = 6

        Private _pressedRow As TagFilterOption
        Private _pressPoint As Point
        Private _dragActive As Boolean

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            ' Der Druck wird beim Hinuntergehen gemerkt und NICHT als behandelt markiert: der Knopf
            ' der Zeile soll ihn weiter bekommen, sonst gäbe es keinen Klick mehr. Bewegung und
            ' Loslassen kommen auch dann an, wenn der Knopf sie schon behandelt hat.
            Me.AddHandler(InputElement.PointerPressedEvent, AddressOf OnOverlayPointerPressed, RoutingStrategies.Tunnel)
            Me.AddHandler(InputElement.PointerMovedEvent, AddressOf OnOverlayPointerMoved, RoutingStrategies.Bubble, True)
            Me.AddHandler(InputElement.PointerReleasedEvent, AddressOf OnOverlayPointerReleased, RoutingStrategies.Bubble, True)
            AddHandler PointerCaptureLost, AddressOf OnOverlayPointerCaptureLost
        End Sub

        Private Function Vm() As KeywordManagerViewModel
            Return TryCast(DataContext, KeywordManagerViewModel)
        End Function

        ''' <summary>Über den Datenkontext der Schaltfläche und nicht über eine Bindung: die Zeile
        ''' kennt nur sich selbst, nicht den Dialog.</summary>
        Private Sub OnRowClick(sender As Object, e As RoutedEventArgs)
            Vm()?.SelectRow(TryCast(TryCast(sender, Button)?.DataContext, TagFilterOption))
            e.Handled = True
        End Sub

        Private Sub OnExpandClick(sender As Object, e As RoutedEventArgs)
            Vm()?.ToggleExpanded(TryCast(TryCast(sender, Button)?.DataContext, TagFilterOption))
            e.Handled = True
        End Sub

        ''' <summary>ENTER im Feld "Steht unter" ordnet ein wie der Knopf daneben.</summary>
        Private Sub OnParentKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter Then Return
            Vm()?.ApplyParent()
            e.Handled = True
        End Sub

        ''' <summary>ENTER im Feld für das neue Stichwort legt es an wie der Knopf daneben.</summary>
        Private Sub OnNewChildKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter Then Return
            Vm()?.AddChild()
            e.Handled = True
        End Sub

        ' ---------------------------------------------------------------- Ziehen

        ''' <summary>Die Zeile des Baums, zu der ein Steuerelement gehört: es selbst oder eines
        ''' darüber muss ein Zeilenknopf sein. Der Pfeil davor zählt nicht, er klappt nur auf.</summary>
        Private Shared Function RowOf(start As Control) As TagFilterOption
            Dim current = start
            Dim steps = 0
            While current IsNot Nothing AndAlso steps < 48
                Dim button = TryCast(current, Button)
                If button IsNot Nothing Then
                    Return If(button.Classes.Contains("kw-row"), TryCast(button.DataContext, TagFilterOption), Nothing)
                End If
                current = TryCast(current.GetVisualParent(), Control)
                steps += 1
            End While
            Return Nothing
        End Function

        Private Function IsInside(start As Control, name As String) As Boolean
            Dim target = Me.FindControl(Of Control)(name)
            Dim current = start
            Dim steps = 0
            While current IsNot Nothing AndAlso steps < 48
                If current Is target Then Return True
                current = TryCast(current.GetVisualParent(), Control)
                steps += 1
            End While
            Return False
        End Function

        Private Sub OnOverlayPointerPressed(sender As Object, e As PointerPressedEventArgs)
            _pressedRow = Nothing
            If _dragActive Then Return
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            _pressedRow = RowOf(TryCast(e.Source, Control))
            _pressPoint = e.GetPosition(Me)
        End Sub

        Private Sub OnOverlayPointerMoved(sender As Object, e As PointerEventArgs)
            Dim model = Vm()
            If model Is Nothing Then Return
            Dim point = e.GetPosition(Me)
            If Not _dragActive Then
                If _pressedRow Is Nothing Then Return
                If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then
                    _pressedRow = Nothing
                    Return
                End If
                If Math.Abs(point.X - _pressPoint.X) < DragThreshold AndAlso
                   Math.Abs(point.Y - _pressPoint.Y) < DragThreshold Then Return
                ' Ab hier ist es ein Zug. Der Griff geht vom Knopf der Zeile an den Dialog: der
                ' Knopf verliert damit seinen gedrückten Zustand und meldet beim Loslassen keinen
                ' Klick mehr.
                _dragActive = True
                model.BeginDrag(_pressedRow)
                e.Pointer.Capture(Me)
            End If
            UpdateDrag(point)
            e.Handled = True
        End Sub

        ''' <summary>Schild an den Zeiger, Ziel unter dem Zeiger suchen, am Rand der Liste rollen.</summary>
        Private Sub UpdateDrag(point As Point)
            Dim model = Vm()
            If model Is Nothing Then Return
            Dim badge = Me.FindControl(Of Border)("DragBadge")
            If badge IsNot Nothing Then
                Canvas.SetLeft(badge, point.X + 14)
                Canvas.SetTop(badge, point.Y + 12)
            End If

            Dim hit = TryCast(Me.InputHitTest(point), Control)
            If IsInside(hit, "TopLevelDrop") Then
                model.SetDropTarget(Nothing, True)
            Else
                model.SetDropTarget(RowOf(hit), False)
            End If

            ' Rollen, solange der Zeiger nah am oberen oder unteren Rand der Liste steht. Ohne das
            ' liesse sich in einer langen Liste nur ablegen, was gerade sichtbar ist.
            Dim scroll = Me.FindControl(Of ScrollViewer)("RowsScroll")
            If scroll Is Nothing Then Return
            Dim top = scroll.TranslatePoint(New Point(0, 0), Me)
            If Not top.HasValue Then Return
            Dim inside = point.X >= top.Value.X AndAlso point.X <= top.Value.X + scroll.Bounds.Width
            If Not inside Then Return
            Dim fromTop = point.Y - top.Value.Y
            Dim fromBottom = top.Value.Y + scroll.Bounds.Height - point.Y
            Dim stepSize = 0.0
            If fromTop >= 0 AndAlso fromTop < 28 Then stepSize = -14
            If fromBottom >= 0 AndAlso fromBottom < 28 Then stepSize = 14
            If stepSize = 0 Then Return
            Dim maxOffset = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)
            scroll.Offset = New Vector(scroll.Offset.X, Math.Max(0, Math.Min(maxOffset, scroll.Offset.Y + stepSize)))
        End Sub

        Private Sub OnOverlayPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            _pressedRow = Nothing
            If Not _dragActive Then Return
            _dragActive = False
            Vm()?.EndDrag(drop:=True)
            e.Pointer.Capture(Nothing)
            e.Handled = True
        End Sub

        ''' <summary>Verliert der Dialog den Zeigergriff, ist der Zug vorbei, ohne abzulegen. Das
        ''' eigene Freigeben nach dem Ablegen landet ebenfalls hier, dann ist der Zug schon
        ''' beendet.</summary>
        Private Sub OnOverlayPointerCaptureLost(sender As Object, e As PointerCaptureLostEventArgs)
            _pressedRow = Nothing
            If Not _dragActive Then Return
            _dragActive = False
            Vm()?.EndDrag(drop:=False)
        End Sub

    End Class

End Namespace
