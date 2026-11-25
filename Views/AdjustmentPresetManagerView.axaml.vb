Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports Avalonia.Threading
Imports Avalonia.VisualTree
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Das Fenster "Anpassungen verwalten" (AdjustmentPresetManagerViewModel). Der Ansicht
    ''' gehoert nur, was ein Fenster braucht: die Dateiauswahl fuer Import und Export, der Fokus
    ''' im Eingabefeld einer Zeile, die Ordnerliste am Knopf "Verschieben" und die Zeigergeste
    ''' beim Ziehen einer Anpassung auf einen Ordner. Was dabei geschieht, entscheidet das
    ''' ViewModel; die Tasten ESC, EINGABE, F2 und ENTF reicht das Hauptfenster dorthin
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
        Private _pressPoint As Point
        Private _dragActive As Boolean

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            AddHandler DataContextChanged, AddressOf OnManagerChanged
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

        Private Sub OnImportMenuOpened(sender As Object, e As EventArgs)
            LocalizationService.ApplyToMenuFlyout(TryCast(sender, MenuFlyout))
        End Sub

        Private Shared Function PresetFileType() As FilePickerFileType
            Return New FilePickerFileType(LocalizationService.T("Anpassung") & " (*" & AdjustmentPresetStore.FileExtension & ")") With {
                .Patterns = New String() {"*" & AdjustmentPresetStore.FileExtension}
            }
        End Function

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

        ' ── Baum, Inhalt, Anwenden ──────────────────────────────────────────────────────────

        Private Sub OnFolderToggleClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Manager()?.ToggleFolder(FolderOf(sender))
        End Sub

        ''' Ein Klick auf den Namen klappt auf, was die Anpassung enthaelt.
        Private Sub OnPresetNameTapped(sender As Object, e As TappedEventArgs)
            Manager()?.ToggleDetails(PresetOf(sender))
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

        Private Sub OnPresetDeleteClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Manager()?.BeginDeletePreset(PresetOf(sender))
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

        Private Sub OnDragPointerMoved(sender As Object, e As PointerEventArgs)
            If Not _dragActive Then Return
            Dim vm = Manager()
            ' Ohne gedrueckte Taste gibt es keinen Zug mehr (das Loslassen kam woanders an), und
            ' ESC hat ihn im ViewModel schon beendet.
            If vm Is Nothing OrElse vm.DraggedPreset Is Nothing OrElse
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
                Return
            End If
            e.Handled = True
            Dim vm = Manager()
            Dim drop = vm IsNot Nothing AndAlso vm.DraggedPreset IsNot Nothing
            _dragActive = False
            e.Pointer.Capture(Nothing)
            If Not drop Then Return
            Try
                Await vm.CompletePresetDragAsync()
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
            If Not _dragActive Then Return
            _dragActive = False
            pointer?.Capture(Nothing)
            Manager()?.CancelPresetDrag()
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

        ' ── Importieren und Exportieren ─────────────────────────────────────────────────────

        Private Async Sub OnImportFileClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = Manager()
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If vm Is Nothing OrElse topLevel Is Nothing Then Return
                Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                    .Title = LocalizationService.T("Anpassungen importieren"),
                    .AllowMultiple = True,
                    .FileTypeFilter = New List(Of FilePickerFileType) From {PresetFileType()}
                })
                If files Is Nothing Then Return
                For Each file In files
                    Await vm.ImportAsync(file.Path.LocalPath)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.ImportFile", ex)
            End Try
        End Sub

        Private Async Sub OnImportFolderClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = Manager()
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If vm Is Nothing OrElse topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Ordner mit Anpassungen importieren"),
                    .AllowMultiple = False
                })
                Dim folder = folders?.FirstOrDefault()
                If folder IsNot Nothing Then Await vm.ImportAsync(folder.Path.LocalPath)
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.ImportFolder", ex)
            End Try
        End Sub

        Private Async Sub OnPresetExportClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = Manager()
                Dim row = PresetOf(sender)
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If vm Is Nothing OrElse row Is Nothing OrElse topLevel Is Nothing Then Return
                Dim file = Await topLevel.StorageProvider.SaveFilePickerAsync(New FilePickerSaveOptions With {
                    .Title = LocalizationService.T("Anpassung exportieren"),
                    .SuggestedFileName = AdjustmentPresetManagerViewModel.SuggestedExportFileName(row),
                    .DefaultExtension = AdjustmentPresetStore.FileExtension.TrimStart("."c),
                    .FileTypeChoices = New List(Of FilePickerFileType) From {PresetFileType()}
                })
                Dim path = file?.Path.LocalPath
                If Not String.IsNullOrWhiteSpace(path) Then Await vm.ExportPresetAsync(row, path)
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.ExportPreset", ex)
            End Try
        End Sub

        Private Async Sub OnFolderExportClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = Manager()
                Dim node = FolderOf(sender)
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If vm Is Nothing OrElse node Is Nothing OrElse topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Zielordner wählen"),
                    .AllowMultiple = False
                })
                Dim folder = folders?.FirstOrDefault()
                If folder IsNot Nothing Then Await vm.ExportFolderAsync(node, folder.Path.LocalPath)
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.ExportFolder", ex)
            End Try
        End Sub

    End Class

End Namespace
