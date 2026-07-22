Imports System.Linq
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Threading
Imports Avalonia.VisualTree
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views

    Public Class LayersPanelView
        Inherits UserControl

        ' Ebene, die gerade inline umbenannt wird, plus ihr Name vor der Bearbeitung (für Esc = verwerfen).
        Private _renameRow As LayerPanelRow
        Private _renameOriginal As String = ""
        Private _suppressNextRenameLostFocus As Boolean = False

        ' Drag & Drop zum Umsortieren: Kandidat ab Mausdruck, echter Zug erst nach Bewegungsschwelle.
        Private _dragCandidate As LayerPanelRow
        Private _dragStartPoint As Point
        Private _dragPressArgs As PointerPressedEventArgs
        Private _draggedLayer As LayerPanelRow
        ' Zuletzt überfahrene Ziel-Ebene beim Ziehen und ob unter ihrer Mitte (= Einfügen darunter).
        Private _dropTarget As LayerPanelRow
        Private _dropBelow As Boolean
        Private Const DragThreshold As Double = 4.0
        Private Shared ReadOnly LayerDragFormat As DataFormat(Of String) =
            DataFormat.CreateStringApplicationFormat("FerrumPixLayer")

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        ' ── Umbenennen ────────────────────────────────────────────────────────

        ' Doppelklick auf die Beschriftung: Bearbeitung starten und das Eingabefeld direkt fokussieren.
        Private Sub OnLayerNameDoubleTapped(sender As Object, e As TappedEventArgs)
            Dim label = TryCast(sender, Control)
            Dim row = TryCast(label?.DataContext, LayerPanelRow)
            If row Is Nothing Then Return
            BeginRename(row, TryCast(label.Parent, Panel)?.Children.OfType(Of TextBox)().FirstOrDefault())
        End Sub

        ' Tastaturbedienung auf der markierten Ebene: F2 umbenennen, Entf löschen, Strg+D duplizieren.
        Private Sub OnLayerListKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key = Key.F2 Then
                e.Handled = True
                StartRenameSelectedLayer()
            ElseIf e.Key = Key.Delete Then
                e.Handled = True
                TryCast(DataContext, EditorViewModel)?.DeleteSelectedAnnotationCommand.Execute(Nothing)
            ElseIf e.Key = Key.D AndAlso e.KeyModifiers.HasFlag(KeyModifiers.Control) Then
                e.Handled = True
                TryCast(DataContext, EditorViewModel)?.DuplicateSelectedAnnotationCommand.Execute(Nothing)
            End If
        End Sub

        ' Umbenennen-Knopf in der unteren Werkzeugleiste.
        Private Sub OnRenameSelectedClick(sender As Object, e As RoutedEventArgs)
            StartRenameSelectedLayer()
        End Sub

        ' Startet das Umbenennen der aktuell markierten Ebene und fokussiert ihr Eingabefeld.
        Private Sub StartRenameSelectedLayer()
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim row = vm?.SelectedLayerRow
            If row Is Nothing OrElse row.IsRenaming Then Return
            Dim list = Me.FindControl(Of ListBox)("LayerListBox")
            Dim container = If(list IsNot Nothing AndAlso list.SelectedIndex >= 0,
                               TryCast(list.ContainerFromIndex(list.SelectedIndex), Control), Nothing)
            Dim box = container?.GetVisualDescendants().OfType(Of TextBox)().FirstOrDefault()
            BeginRename(row, box)
        End Sub

        Private Sub BeginRename(row As LayerPanelRow, box As TextBox)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If row Is Nothing OrElse vm Is Nothing Then Return
            _renameRow = row
            _renameOriginal = row.EditableName
            vm.BeginLayerRename(row)
            If box IsNot Nothing Then
                Dispatcher.UIThread.Post(Sub()
                                             box.Focus()
                                             box.SelectAll()
                                         End Sub, DispatcherPriority.Background)
            End If
        End Sub

        Private Sub OnLayerRenameKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key = Key.Enter Then
                e.Handled = True
                CommitRename(sender)
            ElseIf e.Key = Key.Escape Then
                e.Handled = True
                CancelRename()
            End If
        End Sub

        Private Sub OnLayerRenameLostFocus(sender As Object, e As RoutedEventArgs)
            If _suppressNextRenameLostFocus Then
                _suppressNextRenameLostFocus = False
                Return
            End If
            CommitRename(sender)
        End Sub

        ' Übernahme: Leerraum trimmen (nur Leerzeichen => automatische Beschriftung) und Bearbeitung beenden.
        Private Sub CommitRename(sender As Object)
            Dim row = TryCast(TryCast(sender, TextBox)?.DataContext, LayerPanelRow)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If row IsNot Nothing Then
                Dim oldName = If(_renameOriginal, "")
                Dim newName = If(row.EditableName, "").Trim()
                row.EditableName = newName
                If Not String.Equals(oldName, newName, StringComparison.Ordinal) Then vm?.MarkLayerMetadataChanged()
            End If
            vm?.EndLayerRename()
            _renameRow = Nothing
        End Sub

        ' Verwerfen: den Namen von vor der Bearbeitung wiederherstellen und Bearbeitung beenden.
        Private Sub CancelRename()
            _suppressNextRenameLostFocus = True
            If _renameRow IsNot Nothing Then _renameRow.EditableName = _renameOriginal
            TryCast(DataContext, EditorViewModel)?.EndLayerRename()
            _renameRow = Nothing
        End Sub

        ' ── Umsortieren per Drag & Drop ─────────────────────────────────────────

        Private Sub OnLayerRowPointerPressed(sender As Object, e As PointerPressedEventArgs)
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            ' Klicks auf Auge/Knöpfe/Eingabefeld nicht als Ziehstart werten.
            Dim src = TryCast(e.Source, Visual)
            If src IsNot Nothing AndAlso (src.FindAncestorOfType(Of Button)() IsNot Nothing OrElse
                                          src.FindAncestorOfType(Of ToggleButton)() IsNot Nothing OrElse
                                          src.FindAncestorOfType(Of TextBox)() IsNot Nothing) Then Return
            _dragCandidate = TryCast(TryCast(sender, Control)?.DataContext, LayerPanelRow)
            _dragStartPoint = e.GetPosition(Me)
            _dragPressArgs = e
        End Sub

        Private Async Sub OnLayerRowPointerMoved(sender As Object, e As PointerEventArgs)
            If _dragCandidate Is Nothing Then Return
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then
                _dragCandidate = Nothing
                Return
            End If
            Dim delta = e.GetPosition(Me) - _dragStartPoint
            If Math.Abs(delta.X) < DragThreshold AndAlso Math.Abs(delta.Y) < DragThreshold Then Return

            Dim dragged = _dragCandidate
            _dragCandidate = Nothing
            _draggedLayer = dragged
            Try
                ' Die eigentliche Ziehlast steht im Feld _draggedLayer (gleiche Steuerung); der Transfer trägt
                ' nur eine Markierung, damit DoDragDropAsync einen gültigen Datensatz bekommt.
                Dim data = New DataTransfer()
                data.Add(DataTransferItem.Create(LayerDragFormat, "1"))
                Await DragDrop.DoDragDropAsync(_dragPressArgs, data, DragDropEffects.Move)
            Finally
                _draggedLayer = Nothing
                _dropTarget = Nothing
                HideDropIndicator()
            End Try
        End Sub

        ' Über einer Zeile: Verschieben erlauben (Cursor), Einfüge-Position (über/unter Zeilenmitte) merken
        ' und die Hilfslinie an die passende Lücke schieben.
        Private Sub OnLayerRowDragOver(sender As Object, e As DragEventArgs)
            e.DragEffects = If(_draggedLayer IsNot Nothing, DragDropEffects.Move, DragDropEffects.None)
            e.Handled = True
            If _draggedLayer Is Nothing Then Return
            Dim row = TryCast(sender, Control)
            Dim layerRow = TryCast(row?.DataContext, LayerPanelRow)
            If row Is Nothing OrElse layerRow Is Nothing Then Return
            ' Objekt- und Korrekturebenen liegen in getrennten Rendergruppen und können nicht
            ' gruppenübergreifend verschoben werden.
            If _draggedLayer.IsAdjustmentLayer <> layerRow.IsAdjustmentLayer Then
                e.DragEffects = DragDropEffects.None
                HideDropIndicator()
                Return
            End If
            _dropTarget = layerRow
            _dropBelow = e.GetPosition(row).Y > row.Bounds.Height / 2
            ShowDropIndicator(row, _dropBelow)
        End Sub

        Private Sub OnLayerRowDrop(sender As Object, e As DragEventArgs)
            e.Handled = True
            PerformLayerDrop()
        End Sub

        ' Cursor auch über Lücken/Rand der Liste auf "Verschieben" halten (sonst zeigt der Zeiger "verboten").
        Private Sub OnLayerListDragOver(sender As Object, e As DragEventArgs)
            e.DragEffects = If(_draggedLayer IsNot Nothing, DragDropEffects.Move, DragDropEffects.None)
            e.Handled = True
        End Sub

        Private Sub OnLayerListDragLeave(sender As Object, e As DragEventArgs)
            HideDropIndicator()
        End Sub

        ' Fallenlassen außerhalb einer Zeile (z.B. unter der letzten): an der zuletzt gemerkten Position ablegen.
        Private Sub OnLayerListDrop(sender As Object, e As DragEventArgs)
            e.Handled = True
            PerformLayerDrop()
        End Sub

        Private Sub PerformLayerDrop()
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm IsNot Nothing AndAlso _draggedLayer IsNot Nothing AndAlso _dropTarget IsNot Nothing Then
                vm.ReorderLayerRelative(_draggedLayer, _dropTarget, _dropBelow)
            End If
            _draggedLayer = Nothing
            _dropTarget = Nothing
            HideDropIndicator()
        End Sub

        ' Schiebt die Hilfslinie an die obere bzw. untere Kante der überfahrenen Zeile (in Koordinaten des
        ' Listen-Overlays), sodass sie die Einfügeposition zwischen zwei Ebenen markiert.
        Private Sub ShowDropIndicator(row As Control, below As Boolean)
            Dim area = Me.FindControl(Of Grid)("LayerListArea")
            Dim indicator = Me.FindControl(Of Border)("DropIndicator")
            If area Is Nothing OrElse indicator Is Nothing OrElse row Is Nothing Then Return
            Dim p = row.TranslatePoint(New Point(0, If(below, row.Bounds.Height, 0.0)), area)
            If Not p.HasValue Then Return
            indicator.Margin = New Thickness(6, Math.Max(0, p.Value.Y - 1), 6, 0)
            indicator.IsVisible = True
        End Sub

        Private Sub HideDropIndicator()
            Dim indicator = Me.FindControl(Of Border)("DropIndicator")
            If indicator IsNot Nothing Then indicator.IsVisible = False
        End Sub
    End Class

End Namespace
