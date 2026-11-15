Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Linq
Imports System.Windows.Input
Imports ReactiveUI
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Der Dialog, in dem Stichwörter untereinander geordnet werden.
    '''
    ''' Links stehen alle Stichwörter des lokalen Katalogs als Baum, rechts das gewählte Wort mit
    ''' dem, was sich daran ändern lässt: worunter es steht, ein neues Wort darunter, heraus aus
    ''' der Ordnung. JEDE ÄNDERUNG GILT SOFORT, es gibt kein Übernehmen und kein Verwerfen: sie
    ''' schreibt eine Zeile in die Ordnung und an keinem Bild. Deshalb auch nur "Schließen".
    '''
    ''' Die Zeilen sind dieselben wie in der Stichwortauswahl der Galerie
    ''' (<see cref="TagFilterOption"/>), und gebaut wird der Baum mit derselben Funktion
    ''' (<see cref="GalleryViewModel.ArrangeTagTree"/>): zwei Bäume aus zwei Rechnungen zeigten
    ''' früher oder später Verschiedenes.</summary>
    Public Class KeywordManagerViewModel
        Inherits ReactiveObject

        Private ReadOnly _collapsed As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Private _allRows As New List(Of TagFilterOption)()
        Private _isTree As Boolean
        Private _isOpen As Boolean
        Private _searchText As String = ""
        Private _selected As TagFilterOption
        Private _parentText As String = ""
        Private _newChildText As String = ""
        Private _statusText As String = ""

        ''' <summary>Gerufen nach jeder Änderung an der Ordnung, damit die Galerie ihre
        ''' Stichwortauswahl und eine laufende Auswahl nachzieht.</summary>
        Public Property OrderChanged As Action

        Public Sub New()
            ApplyParentCommand = ReactiveCommand.Create(AddressOf ApplyParent)
            MoveToTopCommand = ReactiveCommand.Create(AddressOf MoveToTop)
            AddChildCommand = ReactiveCommand.Create(AddressOf AddChild)
            RemoveCommand = ReactiveCommand.Create(AddressOf RemoveSelected)
            CloseCommand = ReactiveCommand.Create(AddressOf Close)
            ClearSelectionCommand = ReactiveCommand.Create(AddressOf ClearSelection)
        End Sub

        Public ReadOnly Property ApplyParentCommand As ICommand
        Public ReadOnly Property MoveToTopCommand As ICommand
        Public ReadOnly Property AddChildCommand As ICommand
        Public ReadOnly Property RemoveCommand As ICommand
        Public ReadOnly Property CloseCommand As ICommand
        Public ReadOnly Property ClearSelectionCommand As ICommand

        ''' <summary>Die angezeigten Zeilen: der Baum ohne das Zugeklappte, beim Suchen die
        ''' Treffer flach.</summary>
        Public ReadOnly Property Rows As New ObservableCollection(Of TagFilterOption)()

        ''' <summary>Die Wörter, unter die sich das gewählte hängen lässt: alle außer ihm selbst
        ''' und dem, was schon unter ihm hängt.</summary>
        Public ReadOnly Property ParentCandidates As New ObservableCollection(Of String)()

        Public Property IsOpen As Boolean
            Get
                Return _isOpen
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isOpen, value)
            End Set
        End Property

        Public Property SearchText As String
            Get
                Return _searchText
            End Get
            Set(value As String)
                If String.Equals(_searchText, value, StringComparison.Ordinal) Then Return
                Me.RaiseAndSetIfChanged(_searchText, If(value, ""))
                ShowRows()
            End Set
        End Property

        ''' <summary>Das Feld "Steht unter". Ein Wort, ein Weg ("Reisen > Italien") oder leer für
        ''' die oberste Ebene; ein unbekanntes Wort wird dabei angelegt.</summary>
        Public Property ParentText As String
            Get
                Return _parentText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_parentText, If(value, ""))
            End Set
        End Property

        Public Property NewChildText As String
            Get
                Return _newChildText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_newChildText, If(value, ""))
            End Set
        End Property

        ''' <summary>Warum die letzte Änderung nicht ging; leer, wenn sie ging.</summary>
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_statusText, If(value, ""))
                Me.RaisePropertyChanged(NameOf(HasStatus))
            End Set
        End Property

        Public ReadOnly Property HasStatus As Boolean
            Get
                Return _statusText.Length > 0
            End Get
        End Property

        Public ReadOnly Property HasSelection As Boolean
            Get
                Return _selected IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property HasNoSelection As Boolean
            Get
                Return _selected Is Nothing
            End Get
        End Property

        Public ReadOnly Property SelectedName As String
            Get
                Return If(_selected?.Tag, "")
            End Get
        End Property

        ''' <summary>Steht das gewählte Wort unter einem anderen? Nur dann hat "Auf die oberste
        ''' Ebene" etwas zu tun.</summary>
        Public ReadOnly Property SelectedHasParent As Boolean
            Get
                Return _selected IsNot Nothing AndAlso _selected.ParentOption IsNot Nothing
            End Get
        End Property

        ''' <summary>Lässt sich das gewählte Wort ganz entfernen? Nur, wenn es an keinem Bild
        ''' steht: dann ist es eine reine Kategorie, und mit der Zeile in der Ordnung ist es weg.
        ''' Ein Wort mit Bildern bliebe als Stichwort stehen, "Entfernen" hielte also nicht, was es
        ''' sagt; dafür gibt es "Auf die oberste Ebene".</summary>
        Public ReadOnly Property CanRemoveSelected As Boolean
            Get
                Return _selected IsNot Nothing AndAlso Not _usedNames.Contains(_selected.Tag)
            End Get
        End Property

        Public ReadOnly Property HasNoRows As Boolean
            Get
                Return Rows.Count = 0
            End Get
        End Property

        Private _usedNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        Public Sub Open()
            _searchText = ""
            Me.RaisePropertyChanged(NameOf(SearchText))
            StatusText = ""
            Reload(Nothing)
            IsOpen = True
        End Sub

        Public Sub Close()
            IsOpen = False
        End Sub

        ''' <summary>Liest Stichwörter und Ordnung neu und stellt die Auswahl wieder her.</summary>
        Public Sub Reload(selectName As String)
            Dim library = LibraryService.Instance
            Dim entries As New List(Of TagFilterOption)()
            _usedNames = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Try
                For Each entry In library.GetTagCounts()
                    If String.IsNullOrWhiteSpace(entry.Tag) OrElse Not _usedNames.Add(entry.Tag) Then Continue For
                    entries.Add(New TagFilterOption(entry.Tag, entry.Count, False))
                Next
                ' Wörter, die nur die Ordnung kennt: eine Kategorie ohne eigenes Bild.
                For Each name In library.GetKeywordTreeNames()
                    If _usedNames.Contains(name) OrElse entries.Any(Function(o) String.Equals(o.Tag, name, StringComparison.OrdinalIgnoreCase)) Then Continue For
                    entries.Add(New TagFilterOption(name, 0, False))
                Next
                Dim tree = library.GetKeywordTree()
                Dim arranged = GalleryViewModel.ArrangeTagTree(entries, tree, Nothing, _collapsed, Function(name) False)
                _isTree = Not tree.IsEmpty
                _allRows = If(_isTree, arranged, entries.OrderBy(Function(o) o.DisplayTag, StringComparer.OrdinalIgnoreCase).ToList())
            Catch ex As Exception
                DiagnosticLogService.LogException("KeywordManager.Reload", ex)
                _allRows = entries
                _isTree = False
            End Try
            Dim wanted = If(selectName, If(_selected?.Tag, ""))
            _selected = _allRows.FirstOrDefault(Function(o) String.Equals(o.Tag, wanted, StringComparison.OrdinalIgnoreCase))
            ShowRows()
            RefreshSelection()
        End Sub

        Private Sub ShowRows()
            Dim search = If(_searchText, "").Trim()
            Dim showsTree = _isTree AndAlso search.Length = 0
            Rows.Clear()
            For Each row In _allRows
                row.ShowsTree = showsTree
                row.IsSelected = row Is _selected
                If search.Length > 0 AndAlso row.DisplayTag.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0 Then Continue For
                If showsTree AndAlso row.IsHiddenByCollapse Then Continue For
                Rows.Add(row)
            Next
            Me.RaisePropertyChanged(NameOf(HasNoRows))
        End Sub

        ''' <summary>Wählt eine Zeile. Ein zweiter Klick auf die gewählte Zeile hebt die Auswahl
        ''' wieder auf: nur ohne Auswahl legt "Anlegen" auf der obersten Ebene an, und ohne diesen
        ''' Rückweg käme man nach dem ersten Klick nie mehr dorthin.</summary>
        Public Sub SelectRow(row As TagFilterOption)
            If row Is Nothing Then Return
            If row Is _selected Then
                ClearSelection()
                Return
            End If
            _selected = row
            StatusText = ""
            For Each entry In _allRows
                entry.IsSelected = entry Is row
            Next
            RefreshSelection()
        End Sub

        ' ---------------------------------------------------------------- Ziehen

        Private _dragged As TagFilterOption
        Private _isDropOnTop As Boolean

        ''' <summary>Wird gerade ein Stichwort gezogen? Dann zeigt der Dialog über der Liste die
        ''' Ablage für die oberste Ebene.</summary>
        Public ReadOnly Property IsDragging As Boolean
            Get
                Return _dragged IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property DraggedName As String
            Get
                Return If(_dragged?.Tag, "")
            End Get
        End Property

        ''' <summary>Liegt der Zeiger gerade über der Ablage für die oberste Ebene?</summary>
        Public Property IsDropOnTop As Boolean
            Get
                Return _isDropOnTop
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isDropOnTop, value)
            End Set
        End Property

        Public Sub BeginDrag(row As TagFilterOption)
            If row Is Nothing Then Return
            _dragged = row
            StatusText = ""
            Me.RaisePropertyChanged(NameOf(IsDragging))
            Me.RaisePropertyChanged(NameOf(DraggedName))
        End Sub

        ''' <summary>Ließe sich das gezogene Wort unter <paramref name="target"/> hängen? Nicht
        ''' unter sich selbst, nicht unter etwas, das schon unter ihm hängt, und nicht dorthin, wo
        ''' es ohnehin steht.</summary>
        Public Function CanDropOn(target As TagFilterOption) As Boolean
            If _dragged Is Nothing OrElse target Is Nothing OrElse target Is _dragged Then Return False
            If target Is _dragged.ParentOption Then Return False
            Return Not LibraryService.Instance.GetKeywordTree().WouldCycle(_dragged.Tag, target.Tag)
        End Function

        ''' <summary>Zieht die Markierung des Ziels nach: genau eine Zeile oder die Ablage für die
        ''' oberste Ebene, oder nichts.</summary>
        Public Sub SetDropTarget(target As TagFilterOption, onTop As Boolean)
            Dim allowed = If(onTop, Nothing, If(CanDropOn(target), target, Nothing))
            For Each entry In _allRows
                entry.IsDropTarget = entry Is allowed
            Next
            IsDropOnTop = onTop AndAlso _dragged IsNot Nothing AndAlso _dragged.ParentOption IsNot Nothing
        End Sub

        ''' <summary>Beendet den Zug. Mit <paramref name="drop"/> hängt das gezogene Wort unter die
        ''' markierte Zeile oder kommt auf die oberste Ebene; ohne bleibt alles, wie es war.</summary>
        Public Sub EndDrag(drop As Boolean)
            Dim dragged = _dragged
            Dim target = _allRows.FirstOrDefault(Function(entry) entry.IsDropTarget)
            Dim onTop = _isDropOnTop
            _dragged = Nothing
            For Each entry In _allRows
                entry.IsDropTarget = False
            Next
            IsDropOnTop = False
            Me.RaisePropertyChanged(NameOf(IsDragging))
            Me.RaisePropertyChanged(NameOf(DraggedName))
            If Not drop OrElse dragged Is Nothing Then Return
            Dim library = LibraryService.Instance
            If target IsNot Nothing Then
                If Not library.SetKeywordParent(dragged.Tag, target.Tag) Then
                    StatusText = LocalizationService.T("Ein Stichwort kann nicht unter sich selbst stehen")
                    Return
                End If
                _collapsed.Remove(target.Tag)
                AfterChange(dragged.Tag)
            ElseIf onTop Then
                If library.SetKeywordParent(dragged.Tag, "") Then AfterChange(dragged.Tag)
            End If
        End Sub

        ''' <summary>Hebt die Auswahl auf; danach legt "Anlegen" auf der obersten Ebene an.</summary>
        Public Sub ClearSelection()
            _selected = Nothing
            StatusText = ""
            For Each entry In _allRows
                entry.IsSelected = False
            Next
            RefreshSelection()
        End Sub

        Public Sub ToggleExpanded(row As TagFilterOption)
            If row Is Nothing OrElse Not row.HasChildren Then Return
            row.IsExpanded = Not row.IsExpanded
            If row.IsExpanded Then _collapsed.Remove(row.Tag) Else _collapsed.Add(row.Tag)
            ShowRows()
        End Sub

        ''' <summary>Zieht nach, was von der Auswahl abhängt: Name, heutige Stelle, die Wörter, die
        ''' als übergeordnetes in Frage kommen.</summary>
        Private Sub RefreshSelection()
            ParentCandidates.Clear()
            If _selected Is Nothing Then
                ParentText = ""
            Else
                Dim tree = LibraryService.Instance.GetKeywordTree()
                Dim above = tree.PathOf(_selected.Tag)
                If above.Count > 0 Then above.RemoveAt(above.Count - 1)
                ParentText = String.Join(" > ", above)
                Dim blocked As New HashSet(Of String)(tree.DescendantsOf(_selected.Tag), StringComparer.OrdinalIgnoreCase) From {_selected.Tag}
                For Each name In _allRows.Select(Function(o) o.Tag).
                                 Where(Function(t) Not blocked.Contains(t)).
                                 OrderBy(Function(t) t, StringComparer.OrdinalIgnoreCase)
                    ParentCandidates.Add(name)
                Next
            End If
            NewChildText = ""
            Me.RaisePropertyChanged(NameOf(HasSelection))
            Me.RaisePropertyChanged(NameOf(HasNoSelection))
            Me.RaisePropertyChanged(NameOf(SelectedName))
            Me.RaisePropertyChanged(NameOf(SelectedHasParent))
            Me.RaisePropertyChanged(NameOf(CanRemoveSelected))
        End Sub

        ''' <summary>Hängt das gewählte Wort unter das, was im Feld steht.</summary>
        Public Sub ApplyParent()
            If _selected Is Nothing Then Return
            Dim name = _selected.Tag
            If Not GalleryViewModel.ArrangeTag(name, ParentText) Then
                StatusText = LocalizationService.T("Ein Stichwort kann nicht unter sich selbst stehen")
                Return
            End If
            AfterChange(name)
        End Sub

        Public Sub MoveToTop()
            If _selected Is Nothing Then Return
            Dim name = _selected.Tag
            If LibraryService.Instance.SetKeywordParent(name, "") Then AfterChange(name)
        End Sub

        ''' <summary>Legt ein Wort unter dem gewählten an, oder auf der obersten Ebene, wenn
        ''' nichts gewählt ist. Das Wort muss an keinem Bild stehen: so entsteht eine Kategorie,
        ''' bevor das erste Bild sie trägt. Ein Wort, das es schon gibt, wird umgehängt.</summary>
        Public Sub AddChild()
            Dim steps = KeywordTree.SplitPath(NewChildText)
            If steps.Count = 0 Then Return
            Dim above = If(_selected?.Tag, "")
            If above.Length > 0 Then steps.Insert(0, above)
            Dim library = LibraryService.Instance
            Dim tree = library.GetKeywordTree()
            For index = 1 To steps.Count - 1
                If tree.WouldCycle(steps(index), steps(index - 1)) Then
                    StatusText = LocalizationService.T("Ein Stichwort kann nicht unter sich selbst stehen")
                    Return
                End If
            Next
            If steps.Count = 1 Then
                ' Ein einzelnes neues Wort ohne Auswahl: als Kategorie auf die oberste Ebene.
                If Not library.GetKeywordTreeNames().Contains(steps(0), StringComparer.OrdinalIgnoreCase) Then
                    library.SetKeywordParent(steps(0), "")
                End If
            Else
                library.SetKeywordPath(steps)
            End If
            If above.Length > 0 Then _collapsed.Remove(above)
            AfterChange(If(above.Length > 0, above, steps.Last()))
        End Sub

        Public Sub RemoveSelected()
            If Not CanRemoveSelected Then Return
            If LibraryService.Instance.RemoveKeywordFromTree(_selected.Tag) Then
                _selected = Nothing
                AfterChange(Nothing)
            End If
        End Sub

        Private Sub AfterChange(selectName As String)
            StatusText = ""
            Reload(selectName)
            Try
                OrderChanged?.Invoke()
            Catch ex As Exception
                DiagnosticLogService.LogException("KeywordManager.Changed", ex)
            End Try
        End Sub

    End Class

End Namespace
