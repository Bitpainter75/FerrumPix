Imports System.Linq
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Threading
Imports Avalonia.VisualTree
Imports FerrumPix.Controls
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
            ' Der Klick in den LEEREN Bereich unter den Ebenen muss VOR der Liste ankommen. Die
            ' ListBox behandelt PointerPressed selbst; ein gewoehnlicher Handler auf der Flaeche
            ' darunter bekam das Ereignis deshalb nie zu sehen, und eine markierte Maskenebene liess
            ' sich so nicht abwaehlen. Mit dem Tunnel laeuft er davor - Klicks auf echte Zeilen
            ' erkennt er an ihrem ListBoxItem und laesst sie durch.
            Dim area = Me.FindControl(Of Grid)("LayerListArea")
            If area IsNot Nothing Then
                area.AddHandler(InputElement.PointerPressedEvent,
                                   New EventHandler(Of PointerPressedEventArgs)(AddressOf OnLayerListAreaPointerPressed),
                                   RoutingStrategies.Tunnel)
            End If
        End Sub

        ' ── Umbenennen ────────────────────────────────────────────────────────

        ' Doppelklick auf die Beschriftung: Bearbeitung starten und das Eingabefeld direkt fokussieren.
        Private Sub OnLayerNameDoubleTapped(sender As Object, e As TappedEventArgs)
            Dim label = TryCast(sender, Control)
            Dim row = TryCast(label?.DataContext, LayerPanelRow)
            If row Is Nothing Then Return
            BeginRename(row, TryCast(label.Parent, Panel)?.Children.OfType(Of TextBox)().FirstOrDefault())
        End Sub

        Private Sub OnGlobalAdjustmentsPointerPressed(sender As Object, e As PointerPressedEventArgs)
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            ' Das Auge bleibt eine reine Sichtbarkeitsaktion und darf beim Auf-/Zuklappen nicht
            ' zusätzlich das Reglerziel wechseln.
            Dim source = TryCast(e.Source, Visual)
            If source IsNot Nothing AndAlso source.FindAncestorOfType(Of ToggleButton)() IsNot Nothing Then Return
            TryCast(DataContext, EditorViewModel)?.SelectGlobalAdjustmentsTarget()
            e.Handled = True
        End Sub

        Private Sub OnFixedLayerPointerPressed(sender As Object, e As PointerPressedEventArgs)
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            ' Das Auge blendet die feste Ebene nur ein/aus; ein Klick auf den übrigen Bereich
            ' hebt dagegen die lokale Ebenenauswahl auf und setzt das Bild als Reglerziel.
            Dim source = TryCast(e.Source, Visual)
            If source IsNot Nothing AndAlso source.FindAncestorOfType(Of ToggleButton)() IsNot Nothing Then Return
            TryCast(DataContext, EditorViewModel)?.SelectGlobalAdjustmentsTarget()
            e.Handled = True
        End Sub

        Private Sub OnLayerListAreaPointerPressed(sender As Object, e As PointerPressedEventArgs)
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            ' Klicks auf echte ListBox-Zeilen werden von der ListBox selbst verarbeitet. Nur der
            ' freie Bereich (auch bei leerem Stapel) ist der bequeme Weg zurück zum Bildziel.
            Dim source = TryCast(e.Source, Visual)
            If source IsNot Nothing AndAlso source.FindAncestorOfType(Of ListBoxItem)() IsNot Nothing Then Return
            ' Bildlaufleiste und Knoepfe im freien Bereich sind KEIN Klick ins Leere - sonst
            ' verlaere man beim Scrollen die Markierung.
            If source IsNot Nothing AndAlso
               (source.FindAncestorOfType(Of ScrollBar)() IsNot Nothing OrElse
                source.FindAncestorOfType(Of Button)() IsNot Nothing) Then Return
            TryCast(DataContext, EditorViewModel)?.SelectGlobalAdjustmentsTarget()
            ' NICHT als behandelt markieren: der Tunnel laeuft vor der Liste, und ein behandeltes
            ' Ereignis naehme ihr das Scrollen und das Ausklappen von Gruppen.
            e.Handled = False
        End Sub

        ' Tastaturbedienung auf der markierten Ebene: F2 umbenennen, Entf löschen, Strg+D duplizieren.
        Private Sub OnLayerListKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key = Key.F2 Then
                e.Handled = True
                StartRenameSelectedLayer()
            ElseIf e.Key = Key.Delete Then
                e.Handled = True
                TryCast(DataContext, EditorViewModel)?.DeleteSelectedAnnotationCommand.Execute(Nothing)
            ElseIf e.Key = Key.D AndAlso PlatformShortcutService.HasPrimaryModifier(e.KeyModifiers) Then
                e.Handled = True
                TryCast(DataContext, EditorViewModel)?.DuplicateSelectedAnnotationCommand.Execute(Nothing)
            End If
        End Sub

        ' Umbenennen-Knopf in der unteren Werkzeugleiste.
        ''' Schloss in der Zeile: entsperrt genau DIESE Zeile (sichtbar ist es nur bei gesperrten).
        Private Sub OnToggleRowLockClick(sender As Object, e As RoutedEventArgs)
            Dim row = TryCast(TryCast(sender, Control)?.DataContext, LayerPanelRow)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If row Is Nothing OrElse vm Is Nothing Then Return
            vm.SelectedLayerRow = row
            vm.ToggleSelectionLocked()
            e.Handled = True
        End Sub

        ''' Knopf in der Fußzeile: sperrt bzw. entsperrt die aktuelle Auswahl.
        Private Sub OnToggleSelectionLockClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            vm?.ToggleSelectionLocked()
        End Sub

        Private Sub OnRenameSelectedClick(sender As Object, e As RoutedEventArgs)
            StartRenameSelectedLayer()
        End Sub

        ''' Knopf in der Fußzeile: legt die Ebenenmaske an bzw. öffnet sie zum Bearbeiten.
        Private Sub OnAnnotationMaskFooterClick(sender As Object, e As RoutedEventArgs)
            TryCast(DataContext, EditorViewModel)?.UseAnnotationMask()
        End Sub

        ''' <summary>Klick auf das Maskensymbol EINER ZEILE: erst diese Zeile markieren, dann ihre
        ''' Maske öffnen. Die Reihenfolge ist der Punkt - die Befehle des ViewModels arbeiten auf der
        ''' markierten Ebene, und angeklickt wurde vielleicht eine andere.</summary>
        Private Sub OnLayerMaskIconClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim row = TryCast(TryCast(sender, Control)?.DataContext, LayerPanelRow)
            If vm Is Nothing OrElse row Is Nothing Then Return
            If Not Object.ReferenceEquals(row, vm.SelectedLayerRow) Then vm.SelectedLayerRow = row
            vm.UseAnnotationMask()
            e.Handled = True
        End Sub

        ' Rechtsklick auf eine Ebene: dieselben Aktionen wie im Footer, aber als Kontextmenü direkt an der
        ' Zeile. Der Klick wählt die Zeile zuerst aus, damit die Selected*-Kommandos auf sie wirken; kind-
        ' abhängige Einträge (Maske/Rastern) erscheinen nur, wo sie gelten. Programmatisch aufgebaut, weil
        ' ein Popup die VM-Kommandos nicht über AncestorType aus dem UserControl-DataContext erreicht.
        '
        ' Der Handler hängt an der LISTE, nicht an der Zeile, und sucht die gemeinte Zeile über einen
        ' Treffertest (RowUnderPointer). Zwei Gründe: der Rechtsklick zwischen zwei Zeilen oder in den
        ' leeren Bereich traf sonst gar keinen Handler und blubberte bis zum Wurzelraster des Editors,
        ' wo das Menü der Fußzeile hängt - im Ebenenpanel hat es nichts zu suchen. Und der Zeilenstapel
        ' kann sich zwischen Drücken und Loslassen neu aufbauen; die Zeile aus dem DataContext eines
        ' alten Behälters wäre dann ein Überbleibsel, das es in der Liste nicht mehr gibt.
        Private Sub OnLayerRowContextRequested(sender As Object, e As ContextRequestedEventArgs)
            Dim list = TryCast(sender, Control)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If list Is Nothing OrElse vm Is Nothing Then Return
            ' Ganz vorn und ohne Bedingung: im Ebenenpanel gibt es NUR dieses Menü. Auch wenn keine
            ' Zeile getroffen ist, darf das Ereignis nicht weiterlaufen.
            e.Handled = True
            Dim row = RowUnderPointer(e, list)
            If row Is Nothing Then Return
            ' Eine bestehende MEHRFACHauswahl darf der Rechtsklick nicht zerstören - sonst wäre
            ' „Gruppieren" über das Kontextmenü nie erreichbar (der Klick hätte die Auswahl schon auf
            ' eine Zeile eingedampft). Nur wenn die angeklickte Zeile nicht dazugehört, wird gewechselt.
            Dim rowIsInSelection = (row.Annotation IsNot Nothing AndAlso vm.IsAnnotationSelected(row.Annotation)) OrElse
                                   (row.AdjustmentLayer IsNot Nothing AndAlso vm.IsAdjustmentLayerSelected(row.AdjustmentLayer)) OrElse
                                   (row.IsGroupHeader AndAlso Object.ReferenceEquals(row, vm.SelectedLayerRow))
            If Not rowIsInSelection Then vm.SelectedLayerRow = row

            Dim items As New List(Of Control)()
            ' DIESELBE Quelle wie die Fusszeile (IsMultiLayerSelection). Vorher rechnete jede Stelle
            ' ihre eigene Bedingung, und die beiden liefen auseinander: das Menue liess "nach vorne"
            ' und "umbenennen" bei mehreren richtigerweise weg, die Fusszeile bot sie weiter an.
            Dim mehrere = vm.IsMultiLayerSelection
            If vm.CanGroupSelectedAnnotations Then
                Dim text = LocalizationService.T("Objekte gruppieren (Strg+G)")
                items.Add(MakeLayerMenuItem(
                    PlatformShortcutService.FormatShortcutInLabel(
                        text, PlatformShortcutService.FormatPrimaryShortcut("G")),
                    "folder", vm.GroupSelectedAnnotationsCommand))
            End If
            If vm.CanUngroupSelectedAnnotations Then
                Dim text = LocalizationService.T("Gruppierung aufheben (Strg+Umschalt+G)")
                items.Add(MakeLayerMenuItem(
                    PlatformShortcutService.FormatShortcutInLabel(
                        text, PlatformShortcutService.FormatPrimaryShortcut("G", includeShift:=True)),
                    "folder-x", vm.UngroupSelectedAnnotationsCommand))
            End If
            If items.Count > 0 Then items.Add(New Separator())
            If Not mehrere Then
                items.Add(MakeLayerMenuItem(LocalizationService.T("Ebene nach vorne"), "arrow-up", vm.MoveSelectedAnnotationUpCommand))
                items.Add(MakeLayerMenuItem(LocalizationService.T("Ebene nach hinten"), "arrow-down", vm.MoveSelectedAnnotationDownCommand))
            End If
            If mehrere Then
                items.Add(MakeLayerMenuItem(LocalizationService.T("Objekte duplizieren"), "copy", vm.DuplicateSelectedAnnotationsCommand))
            Else
                Dim text = LocalizationService.T("Ebene duplizieren (Strg+D)")
                items.Add(MakeLayerMenuItem(
                    PlatformShortcutService.FormatShortcutInLabel(
                        text, PlatformShortcutService.FormatPrimaryShortcut("D")),
                    "copy", vm.DuplicateSelectedAnnotationCommand))
            End If
            If vm.HasSelectedAdjustmentLayer Then
                items.Add(MakeLayerMenuItem(LocalizationService.T("Neue Korrektur mit derselben Maske"), "adjustments-plus", vm.AddAdjustmentWithSameMaskCommand))
            End If
            ' Ebenenmaske und Schnittmaske gelten fuer GENAU EIN Objekt - bei einer Mehrfachauswahl
            ' sagen die Eigenschaften des ViewModels schon Nein, hier bleiben die Eintraege dann weg.
            If vm.CanAddAnnotationMask Then
                items.Add(MakeLayerMenuItem(LocalizationService.T("Ebenenmaske hinzufügen"), "mask", vm.AddAnnotationMaskCommand))
            End If
            If vm.SelectedAnnotationHasMask Then
                items.Add(MakeLayerMenuItem(LocalizationService.T("Ebenenmaske bearbeiten"), "brush", vm.EditAnnotationMaskCommand))
                items.Add(MakeLayerMenuItem(LocalizationService.T("Ebenenmaske entfernen"), "circle-x", vm.RemoveAnnotationMaskCommand))
            End If
            If vm.CanClipSelectedAnnotation Then
                Dim clipText = If(vm.SelectedAnnotationIsClipped,
                                  LocalizationService.T("Beschränkung auf Ebene darunter aufheben"),
                                  LocalizationService.T("Auf Ebene darunter beschränken"))
                items.Add(MakeLayerMenuItem(clipText, "layers-intersect", vm.ToggleClipToLayerBelowCommand))
            End If
            If vm.CanMergeSelectedAnnotations Then
                items.Add(MakeLayerMenuItem(LocalizationService.T("Ebenen zusammenlegen"), "layers-subtract", vm.MergeSelectedAnnotationsCommand))
            End If
            If vm.CanRasterizeSelectedAnnotation Then
                items.Add(MakeLayerMenuItem(LocalizationService.T("Ebene rastern"), "layers-union", vm.RasterizeSelectedAnnotationCommand))
            End If
            ' Sperren gilt für alles Markierte - bei einer Gruppen-Kopfzeile also für die ganze Gruppe.
            Dim lockEntry = MakeLayerMenuItem(vm.SelectionLockLabel,
                                                 If(vm.IsSelectionGeometryLocked, "lock-open", "lock"), Nothing)
            AddHandler lockEntry.Click, Sub(s2, e2) vm.ToggleSelectionLocked()
            items.Add(lockEntry)
            ' Umbenennen gilt für die angeklickte ZEILE - eine Gruppen-Kopfzeile also auch, obwohl mit
            ' ihr alle Mitglieder markiert sind (sonst wäre eine Gruppe nur per F2 umbenennbar).
            If row.IsGroupHeader Then
                Dim renameGroupItem = MakeLayerMenuItem(LocalizationService.T("Gruppe umbenennen (F2)"), "edit", Nothing)
                AddHandler renameGroupItem.Click, Sub(s2, e2) StartRenameSelectedLayer()
                items.Add(renameGroupItem)
            ElseIf Not mehrere Then
                Dim renameItem = MakeLayerMenuItem(LocalizationService.T("Ebene umbenennen (F2)"), "edit", Nothing)
                AddHandler renameItem.Click, Sub(s2, e2) StartRenameSelectedLayer()
                items.Add(renameItem)
            End If
            items.Add(New Separator())
            If mehrere Then
                items.Add(MakeLayerMenuItem(LocalizationService.T("Objekte löschen"), "trash", vm.DeleteSelectedAnnotationsCommand))
            Else
                items.Add(MakeLayerMenuItem(LocalizationService.T("Ebene löschen (Entf)"), "trash", vm.DeleteSelectedAnnotationCommand))
            End If

            Dim menu As New ContextMenu()
            menu.ItemsSource = items
            list.ContextMenu = menu
            menu.Open(list)
        End Sub

        ''' <summary>Die Ebenenzeile unter dem Zeiger. Treffertest an der Zeigerposition und dann durch
        ''' den SICHTBAREN Baum nach oben - der logische Elternteil ist in einer virtualisierten Liste
        ''' nicht gesetzt, und e.Source ist beim Handler an der Liste nicht die Zeile.</summary>
        Private Shared Function RowUnderPointer(e As ContextRequestedEventArgs, list As Control) As LayerPanelRow
            If e Is Nothing OrElse list Is Nothing OrElse Not list.IsVisible Then Return Nothing
            Dim pos As Point
            ' Über die Tastatur aufgerufen gibt es keine Position - dann ist keine Zeile gemeint.
            If Not e.TryGetPosition(list, pos) Then Return Nothing
            If pos.X < 0 OrElse pos.Y < 0 OrElse
               pos.X > list.Bounds.Width OrElse pos.Y > list.Bounds.Height Then Return Nothing
            Dim target = TryCast(list.InputHitTest(pos), Visual)
            While target IsNot Nothing
                Dim ctrl = TryCast(target, Control)
                If ctrl IsNot Nothing Then
                    Dim row = TryCast(ctrl.DataContext, LayerPanelRow)
                    If row IsNot Nothing Then Return row
                End If
                target = TryCast(target.GetVisualParent(), Visual)
            End While
            Return Nothing
        End Function

        Private Function MakeLayerMenuItem(header As String, iconName As String, command As System.Windows.Input.ICommand) As MenuItem
            Dim mi As New MenuItem With {.Header = header}
            If command IsNot Nothing Then mi.Command = command
            If Not String.IsNullOrEmpty(iconName) Then
                mi.Icon = New SvgIcon With {
                    .Source = $"avares://FerrumPix/Assets/Icons/outline/{iconName}.svg",
                    .Width = 15, .Height = 15
                }
            End If
            Return mi
        End Function

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
            If e.GetCurrentPoint(Me).Properties.IsRightButtonPressed Then
                ' Rechtsklick auf eine bereits markierte Zeile darf die Mehrfachauswahl nicht
                ' eindampfen - die ListBox setzt ihr SelectedItem auch mit der rechten Taste.
                Dim vmRight = TryCast(DataContext, EditorViewModel)
                Dim rightRow = TryCast(TryCast(sender, Control)?.DataContext, LayerPanelRow)
                If vmRight IsNot Nothing AndAlso rightRow IsNot Nothing Then
                    vmRight.PreserveMultiSelectionOnNextRowChange =
                        (rightRow.Annotation IsNot Nothing AndAlso vmRight.IsAnnotationSelected(rightRow.Annotation)) OrElse
                        (rightRow.AdjustmentLayer IsNot Nothing AndAlso vmRight.IsAdjustmentLayerSelected(rightRow.AdjustmentLayer))
                End If
                Return
            End If
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            ' Klicks auf Auge/Knöpfe/Eingabefeld nicht als Ziehstart werten.
            Dim src = TryCast(e.Source, Visual)
            If src IsNot Nothing AndAlso (src.FindAncestorOfType(Of Button)() IsNot Nothing OrElse
                                          src.FindAncestorOfType(Of ToggleButton)() IsNot Nothing OrElse
                                          src.FindAncestorOfType(Of TextBox)() IsNot Nothing) Then Return
            Dim row = TryCast(TryCast(sender, Control)?.DataContext, LayerPanelRow)
            ' Strg+Klick nimmt eine Objektebene zur Auswahl hinzu bzw. heraus. Das Ereignis wird dabei
            ' verbraucht: die ListBox ist einfach-auswählend und würde die Menge sonst sofort wieder
            ' auf diese eine Zeile eindampfen.
            Dim mehrfach = PlatformShortcutService.HasSelectionModifier(e.KeyModifiers) OrElse
                           e.KeyModifiers.HasFlag(KeyModifiers.Shift)
            If mehrfach AndAlso row IsNot Nothing AndAlso (row.Annotation IsNot Nothing OrElse row.AdjustmentLayer IsNot Nothing) Then
                Dim vm = TryCast(DataContext, EditorViewModel)
                If vm IsNot Nothing Then
                    Dim bereich = e.KeyModifiers.HasFlag(KeyModifiers.Shift)
                    If row.Annotation IsNot Nothing Then
                        Dim index = vm.TextAnnotations.IndexOf(row.Annotation)
                        If bereich Then vm.SelectAnnotationRangeTo(index) Else vm.ToggleAnnotationInSelection(index)
                    Else
                        If bereich Then vm.SelectAdjustmentLayerRangeTo(row.AdjustmentLayer) Else vm.ToggleAdjustmentLayerInSelection(row.AdjustmentLayer)
                    End If
                    e.Handled = True
                    Return
                End If
            End If
            ' Ein Klick auf die BEREITS markierte Zeile meldet der Liste keinen Wechsel, der Setter
            ' von SelectedLayerRow laeuft also nie. Genau dann fehlte das rote Overlay: es wird nach
            ' dem ersten Reglerdreh und in jedem verdeckenden Werkzeug mit Absicht ausgeblendet, und
            ' es gab keine Geste, die es zurueckholt - nur der Umweg ueber eine andere Zeile.
            If Not mehrfach AndAlso row IsNot Nothing AndAlso row.AdjustmentLayer IsNot Nothing Then
                Dim vmSame = TryCast(DataContext, EditorViewModel)
                If vmSame IsNot Nothing AndAlso Object.ReferenceEquals(row, vmSame.SelectedLayerRow) Then
                    vmSame.ReapplySelectedLayerPresentation()
                End If
            End If
            _dragCandidate = row
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
            ' gruppenübergreifend verschoben werden. Eine Gruppen-Kopfzeile zählt dabei als die Art
            ' ihrer Mitglieder (die Entscheidung trifft das ViewModel).
            Dim vmDrag = TryCast(DataContext, EditorViewModel)
            If vmDrag Is Nothing OrElse Not vmDrag.CanDropLayerOn(_draggedLayer, layerRow) Then
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
