Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Threading
Imports Avalonia.VisualTree
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Das Fenster "Anpassungen verwalten" (AdjustmentPresetManagerViewModel). Der Ansicht
    ''' gehoert nur, was ein Fenster braucht: der Fokus im Eingabefeld einer Zeile, das Menue an "Verschieben", die
    ''' Zeigergeste beim Ziehen einer Anpassung oder eines Ordners auf einen Ordner und die
    ''' Meldung, ueber welcher Zeile der Zeiger steht. Was dabei geschieht, entscheidet
    ''' das ViewModel; die Tasten ESC, EINGABE, F2 und ENTF reicht das Hauptfenster dorthin
    ''' (MainWindow.OnWindowKeyDown, AdjustmentPresetManagerViewModel.HandleKey).
    '''
    ''' DAS ZIEHEN IST EINE EIGENE ZEIGERGESTE, kein Zug des Fenstersystems, wie in der Galerie:
    ''' der Zeigergriff geht an die Ansicht, das Ziel kommt aus dem Treffer unter dem Zeiger, die
    ''' Hervorhebung ist nur Farbe. Der Zug endet ohne Ablegen bei ESC, beim Verlust des Griffs
    ''' und bei der ersten Bewegung ohne gedrueckte linke Taste.</summary>
    Public Class AdjustmentPresetManagerView
        Inherits UserControl

        ''' Ab dieser Strecke ist ein Druck auf eine Zeile ein Zug und kein Klick.
        Private Const DragThreshold As Double = 6

        Private _manager As AdjustmentPresetManagerViewModel
        Private _pressedRow As AdjustmentPresetRow
        Private _pressedFolder As AdjustmentPresetFolderNode
        Private _pressPoint As Point
        Private _dragActive As Boolean

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            AddHandler DataContextChanged, AddressOf OnManagerChanged
            ' Ein Klick irgendwo nimmt ein "Löschen?" am Papierkorb zurueck. Im Tunnel, also vor
            ' dem Knopf: der Klick auf den Papierkorb einer ANDEREN Zeile nimmt erst zurueck und
            ' laesst dann diese fragen.
            Me.AddHandler(InputElement.PointerPressedEvent, AddressOf OnAnyPointerPressed, RoutingStrategies.Tunnel, True)
            ' Nach dem Griff kommen Bewegung und Loslassen an der Ansicht an, nicht mehr an der Zeile.
            Me.AddHandler(InputElement.PointerMovedEvent, AddressOf OnDragPointerMoved, RoutingStrategies.Bubble, True)
            Me.AddHandler(InputElement.PointerReleasedEvent, AddressOf OnDragPointerReleased, RoutingStrategies.Bubble, True)
            AddHandler PointerCaptureLost, AddressOf OnDragPointerCaptureLost
        End Sub

        Private Function Manager() As AdjustmentPresetManagerViewModel
            Return TryCast(DataContext, AdjustmentPresetManagerViewModel)
        End Function

        Private Sub OnManagerChanged(sender As Object, e As EventArgs)
            If _manager IsNot Nothing Then RemoveHandler _manager.EditStarted, AddressOf OnEditStarted
            _manager = Manager()
            If _manager IsNot Nothing Then AddHandler _manager.EditStarted, AddressOf OnEditStarted
            EndDragGesture(Nothing)
        End Sub

        ''' <summary>Die Zeilen entstehen erst aus ihrer Vorlage, nach dem einmaligen Durchlauf der
        ''' Uebersetzung; ihre Kurzhinweise und Platzhalter blieben sonst in der
        ''' Ausgangssprache.</summary>
        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As VisualTreeAttachmentEventArgs)
            Dim itemRoot = TryCast(sender, Visual)
            If itemRoot IsNot Nothing Then LocalizationService.ApplyToMaterialized(itemRoot)
        End Sub

        Private Shared Function FolderOf(sender As Object) As AdjustmentPresetFolderNode
            Return TryCast(TryCast(sender, Control)?.DataContext, AdjustmentPresetFolderNode)
        End Function

        Private Shared Function PresetOf(sender As Object) As AdjustmentPresetRow
            Return TryCast(TryCast(sender, Control)?.DataContext, AdjustmentPresetRow)
        End Function

        ' ── Umbenennen in der Zeile ─────────────────────────────────────────────────────────

        ''' <summary>Eine Zeile ist ins Umbenennen gegangen: der Fokus geht in ihr Eingabefeld,
        ''' der bisherige Name ist markiert. Nachgestellt, weil die Zeile eines neuen Ordners
        ''' erst mit dem naechsten Layout entsteht.</summary>
        Private Sub OnEditStarted(item As Object)
            Dim list = Me.FindControl(Of ListBox)(If(TypeOf item Is AdjustmentPresetFolderNode, "FolderList", "PresetList"))
            If list Is Nothing Then Return
            Dispatcher.UIThread.Post(
                Sub()
                    list.ScrollIntoView(item)
                    Dim container = TryCast(list.ContainerFromItem(item), Control)
                    Dim box = container?.GetVisualDescendants().OfType(Of TextBox)().FirstOrDefault()
                    If box Is Nothing Then Return
                    box.Focus()
                    box.SelectAll()
                End Sub, DispatcherPriority.Background)
        End Sub

        ''' Das Verlassen des Felds uebernimmt. Ist die Zeile nicht mehr im Bearbeiten (ESC,
        ''' schon uebernommen), geschieht im ViewModel nichts.
        Private Sub OnEditLostFocus(sender As Object, e As RoutedEventArgs)
            Dim item = TryCast(sender, Control)?.DataContext
            If item IsNot Nothing Then Manager()?.CommitEditInBackground(item)
        End Sub

        Private Sub OnFolderRenameClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Manager()?.BeginRenameFolder(FolderOf(sender))
        End Sub

        Private Sub OnPresetRenameClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Manager()?.BeginRenamePreset(PresetOf(sender))
        End Sub

        ' ── Baum, Anwenden ──────────────────────────────────────────────────────────────────

        Private Sub OnFolderToggleClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Manager()?.ToggleFolder(FolderOf(sender))
        End Sub

        Private Sub OnPresetApplyClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Manager()?.ApplyPreset(PresetOf(sender))
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.Apply", ex)
            End Try
        End Sub

        ' ── Loeschen mit Rueckfrage an der Zeile ────────────────────────────────────────────

        Private Sub OnFolderDeleteClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Manager()?.BeginDeleteFolder(FolderOf(sender))
        End Sub

        ' Der Papierkorb einer Anpassung: der erste Klick fragt, der zweite auf dieselbe Stelle
        ' loescht. Async Sub: eine Ausnahme dahinter finge niemand.
        Private Async Sub OnPresetDeleteClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = Manager()
                If vm IsNot Nothing Then Await vm.ClickDeletePresetAsync(PresetOf(sender))
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.Delete", ex)
            End Try
        End Sub

        ''' <summary>Ein Druck irgendwo im Fenster nimmt das "Löschen?" am Papierkorb zurueck, ausser
        ''' er trifft genau diesen Papierkorb: dort ist er der zweite Klick.</summary>
        Private Sub OnAnyPointerPressed(sender As Object, e As PointerPressedEventArgs)
            Dim vm = Manager()
            If vm Is Nothing OrElse Not vm.IsConfirmingDelete Then Return
            Dim current = TryCast(e.Source, Visual)
            Dim depth = 0
            While current IsNot Nothing AndAlso current IsNot Me AndAlso depth < 64
                Dim button = TryCast(current, Button)
                If button IsNot Nothing AndAlso button.Classes.Contains("preset-delete") Then
                    Dim row = TryCast(button.DataContext, AdjustmentPresetRow)
                    If row IsNot Nothing AndAlso row.IsConfirmingDelete Then Return
                    Exit While
                End If
                current = current.GetVisualParent()
                depth += 1
            End While
            vm.CancelPresetDeleteConfirm()
        End Sub

        ' ── Der Zeiger ueber einer Zeile ────────────────────────────────────────────────────

        ''' Der Zeiger steht auf der Zeile: das grosse Bild im Editor zeigt die Anpassung vorab.
        ''' Die Wartezeit davor liegt im Editor.
        Private Sub OnPresetRowPointerEntered(sender As Object, e As PointerEventArgs)
            If _dragActive Then Return
            Manager()?.HoverPreset(PresetOf(sender))
        End Sub

        ''' Der Zeiger verlaesst die Zeile: die Vorschau endet, ein "Löschen?" ist zurueckgenommen.
        Private Sub OnPresetRowPointerExited(sender As Object, e As PointerEventArgs)
            Manager()?.LeavePresetRow(PresetOf(sender))
        End Sub

        ''' <summary>Die vorlaeufige Zeile "Neuer Ordner" ist kein Ordner, der sich waehlen laesst:
        ''' das ViewModel nimmt sie nicht an, die Liste zeigte sie aber weiter als gewaehlt. Hier
        ''' bekommt die Liste wieder, was das ViewModel haelt.</summary>
        Private Sub OnFolderSelectionChanged(sender As Object, e As SelectionChangedEventArgs)
            Dim list = TryCast(sender, ListBox)
            Dim vm = Manager()
            If list Is Nothing OrElse vm Is Nothing Then Return
            Dim shown = TryCast(list.SelectedItem, AdjustmentPresetFolderNode)
            If shown Is Nothing OrElse Not shown.IsNew Then Return
            Dim held = vm.SelectedFolder
            list.SelectedItem = If(held IsNot Nothing AndAlso vm.Folders.Contains(held), held, Nothing)
        End Sub

        ' Async Sub: eine Ausnahme dahinter faenge niemand. Deshalb je ein eigenes Try.
        Private Async Sub OnConfirmDeleteClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = Manager()
                If vm IsNot Nothing Then Await vm.ConfirmDeleteAsync()
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.Delete", ex)
            End Try
        End Sub

        Private Sub OnCancelDeleteClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Manager()?.CancelDelete()
        End Sub

        ' ── Verschieben ueber die Ordnerliste ───────────────────────────────────────────────

        ''' <summary>Der Knopf "Verschieben" klappt die Ordner auf, in die die Anpassung kann; ihr
        ''' eigener fehlt darin. Die Liste entsteht VOR dem Oeffnen, weil das Aufklappfenster
        ''' seine Breite beim Oeffnen misst. Die Eintraege sind schon uebersetzt oder Namen des
        ''' Nutzers.</summary>
        Private Sub OnPresetMoveClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Dim vm = Manager()
            Dim row = PresetOf(sender)
            Dim button = TryCast(sender, Control)
            If vm Is Nothing OrElse row Is Nothing OrElse button Is Nothing Then Return
            Dim targets = vm.MoveTargetsFor(row)
            If targets.Count = 0 Then Return
            Dim flyout As New MenuFlyout()
            For Each target In targets
                Dim folder = target.Path
                Dim entry As New MenuItem With {.Header = target.Label}
                entry.Classes.Add("no-translate")
                AddHandler entry.Click, Sub() MovePresetFromMenu(row, folder)
                flyout.Items.Add(entry)
            Next
            flyout.ShowAt(button)
        End Sub

        Private Async Sub MovePresetFromMenu(row As AdjustmentPresetRow, folder As String)
            Try
                Dim vm = Manager()
                If vm IsNot Nothing Then Await vm.MovePresetAsync(row, folder)
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.Move", ex)
            End Try
        End Sub

        ' ── Verschieben durch Ziehen auf einen Ordner ───────────────────────────────────────

        ''' Ein Druck auf die Zeile merkt nur den Ort. Knoepfe und das Eingabefeld verbrauchen
        ''' ihren Druck selbst, von dort beginnt also kein Zug.
        Private Sub OnPresetRowPointerPressed(sender As Object, e As PointerPressedEventArgs)
            _pressedRow = Nothing
            _pressedFolder = Nothing
            If _dragActive OrElse Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            Dim row = PresetOf(sender)
            If row Is Nothing OrElse Not row.ShowsName Then Return
            _pressedRow = row
            _pressPoint = e.GetPosition(Me)
        End Sub

        Private Sub OnPresetRowPointerMoved(sender As Object, e As PointerEventArgs)
            If _dragActive OrElse _pressedRow Is Nothing Then Return
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then
                _pressedRow = Nothing
                Return
            End If
            Dim delta = e.GetPosition(Me) - _pressPoint
            If Math.Abs(delta.X) < DragThreshold AndAlso Math.Abs(delta.Y) < DragThreshold Then Return
            Dim row = _pressedRow
            _pressedRow = Nothing
            Dim vm = Manager()
            If vm Is Nothing OrElse Not vm.BeginPresetDrag(row) Then Return
            _dragActive = True
            e.Pointer.Capture(Me)
            UpdateDropTarget(e.GetPosition(Me))
            e.Handled = True
        End Sub

        ''' Ein Druck auf eine Ordnerzeile merkt nur den Ort, wie bei einer Anpassung. Die Wurzel
        ''' und die vorlaeufige Zeile eines neuen Ordners lassen sich nicht ziehen.
        Private Sub OnFolderRowPointerPressed(sender As Object, e As PointerPressedEventArgs)
            _pressedRow = Nothing
            _pressedFolder = Nothing
            If _dragActive OrElse Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            Dim node = FolderOf(sender)
            If node Is Nothing OrElse Not node.CanChange OrElse Not node.ShowsName Then Return
            _pressedFolder = node
            _pressPoint = e.GetPosition(Me)
        End Sub

        Private Sub OnFolderRowPointerMoved(sender As Object, e As PointerEventArgs)
            If _dragActive OrElse _pressedFolder Is Nothing Then Return
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then
                _pressedFolder = Nothing
                Return
            End If
            Dim delta = e.GetPosition(Me) - _pressPoint
            If Math.Abs(delta.X) < DragThreshold AndAlso Math.Abs(delta.Y) < DragThreshold Then Return
            Dim node = _pressedFolder
            _pressedFolder = Nothing
            Dim vm = Manager()
            If vm Is Nothing OrElse Not vm.BeginFolderDrag(node) Then Return
            _dragActive = True
            e.Pointer.Capture(Me)
            UpdateDropTarget(e.GetPosition(Me))
            e.Handled = True
        End Sub

        Private Sub OnDragPointerMoved(sender As Object, e As PointerEventArgs)
            If Not _dragActive Then Return
            Dim vm = Manager()
            ' Ohne gedrueckte Taste gibt es keinen Zug mehr (das Loslassen kam woanders an), und
            ' ESC hat ihn im ViewModel schon beendet.
            If vm Is Nothing OrElse Not vm.IsDragging OrElse
               Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then
                EndDragGesture(e.Pointer)
                Return
            End If
            UpdateDropTarget(e.GetPosition(Me))
            e.Handled = True
        End Sub

        ' Async Sub: eine Ausnahme aus dem Verschieben beendete sonst die Anwendung.
        Private Async Sub OnDragPointerReleased(sender As Object, e As PointerReleasedEventArgs)
            If Not _dragActive Then
                _pressedRow = Nothing
                _pressedFolder = Nothing
                Return
            End If
            e.Handled = True
            Dim vm = Manager()
            Dim drop = vm IsNot Nothing AndAlso vm.IsDragging
            _dragActive = False
            e.Pointer.Capture(Nothing)
            If Not drop Then Return
            Try
                Await vm.CompleteDragAsync()
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.Drop", ex)
            End Try
        End Sub

        ''' Verliert die Ansicht den Zeigergriff, ist der Zug vorbei, ohne abzulegen. Das eigene
        ''' Freigeben beim Loslassen landet ebenfalls hier, dann ist der Zug aber schon beendet.
        Private Sub OnDragPointerCaptureLost(sender As Object, e As PointerCaptureLostEventArgs)
            If _dragActive Then EndDragGesture(Nothing)
        End Sub

        Private Sub EndDragGesture(pointer As IPointer)
            _pressedRow = Nothing
            _pressedFolder = Nothing
            If Not _dragActive Then Return
            _dragActive = False
            pointer?.Capture(Nothing)
            Manager()?.CancelDrag()
        End Sub

        ''' <summary>Sucht die Ordnerzeile unter dem Zeiger und meldet sie dem ViewModel; das
        ''' entscheidet, ob sie ein Ziel ist. Der Weg nach oben hat eine harte Grenze.</summary>
        Private Sub UpdateDropTarget(point As Point)
            Dim vm = Manager()
            If vm Is Nothing Then Return
            Dim node As AdjustmentPresetFolderNode = Nothing
            Dim current = TryCast(Me.InputHitTest(point), Visual)
            Dim depth = 0
            While current IsNot Nothing AndAlso current IsNot Me AndAlso depth < 64
                node = TryCast(TryCast(current, Control)?.DataContext, AdjustmentPresetFolderNode)
                If node IsNot Nothing Then Exit While
                current = current.GetVisualParent()
                depth += 1
            End While
            vm.HoverDropTarget(node)
        End Sub

    End Class

End Namespace
