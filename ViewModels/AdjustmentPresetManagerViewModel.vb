Imports System.Collections.ObjectModel
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Eine Zeile im Ordnerbaum der Verwaltung. Die Zeile traegt ihren ganzen Zustand
    ''' selbst: aufgeklappt, im Umbenennen, in der Rueckfrage zum Loeschen, als Ziel eines
    ''' Zugs hervorgehoben.</summary>
    Public Class AdjustmentPresetFolderNode
        Inherits ReactiveObject

        Private _isExpanded As Boolean = True
        Private _isEditing As Boolean
        Private _editText As String = ""
        Private _rowMessage As String = ""
        Private _isConfirmingDelete As Boolean
        Private _deletePrompt As String = ""
        Private _isDropTarget As Boolean

        ''' Der Pfad relativ zum Wurzelordner; leer ist die Wurzel "Alle Anpassungen".
        Public ReadOnly Property Path As String
        ''' Der Name des Ordners, bei der Wurzel "Alle Anpassungen".
        Public ReadOnly Property Name As String
        ''' Wie viele Anpassungen die Liste zeigt, wenn diese Zeile gewaehlt ist: beim Ordner die
        ''' unmittelbar darin, bei der Wurzel alle.
        Public ReadOnly Property Count As Integer
        ''' Die Ebene im Baum: 0 ist die Wurzel.
        Public ReadOnly Property Depth As Integer
        Public ReadOnly Property Indent As Avalonia.Thickness
        Public ReadOnly Property HasChildren As Boolean
        ''' Die vorlaeufige Zeile von "Neuer Ordner": es gibt den Ordner noch nicht.
        Public ReadOnly Property IsNew As Boolean
        ''' Der Ordner, in dem die Zeile liegt.
        Public ReadOnly Property ParentPath As String

        Public Sub New(path As String, count As Integer, hasChildren As Boolean, isExpanded As Boolean)
            Me.Path = AdjustmentPresetStore.NormalizeFolder(path)
            Me.Count = count
            Me.HasChildren = hasChildren
            _isExpanded = isExpanded
            Name = If(Me.Path.Length = 0, LocalizationService.T("Alle Anpassungen"), AdjustmentPresetStore.FolderName(Me.Path))
            ParentPath = AdjustmentPresetStore.ParentFolder(Me.Path)
            Depth = If(Me.Path.Length = 0, 0, Me.Path.Split("/"c).Length)
            Indent = New Avalonia.Thickness(Depth * 16, 0, 0, 0)
        End Sub

        ''' <summary>Die vorlaeufige Zeile eines neuen Ordners unter <paramref name="parentPath"/>.</summary>
        Public Shared Function CreateNew(parentPath As String) As AdjustmentPresetFolderNode
            Return New AdjustmentPresetFolderNode(AdjustmentPresetStore.NormalizeFolder(parentPath), True)
        End Function

        Private Sub New(parentPath As String, isNew As Boolean)
            Me.IsNew = isNew
            Path = ""
            Name = ""
            Me.ParentPath = parentPath
            Depth = If(parentPath.Length = 0, 1, parentPath.Split("/"c).Length + 1)
            Indent = New Avalonia.Thickness(Depth * 16, 0, 0, 0)
        End Sub

        Public ReadOnly Property IsRoot As Boolean
            Get
                Return Path.Length = 0 AndAlso Not IsNew
            End Get
        End Property

        ''' Die Wurzel laesst sich weder umbenennen noch loeschen.
        Public ReadOnly Property CanChange As Boolean
            Get
                Return Not IsRoot AndAlso Not IsNew
            End Get
        End Property

        Public Property IsExpanded As Boolean
            Get
                Return _isExpanded
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isExpanded, value)
                Me.RaisePropertyChanged(NameOf(ShowsCollapsed))
                Me.RaisePropertyChanged(NameOf(ShowsExpanded))
            End Set
        End Property

        Public ReadOnly Property ShowsCollapsed As Boolean
            Get
                Return HasChildren AndAlso Not _isExpanded
            End Get
        End Property

        Public ReadOnly Property ShowsExpanded As Boolean
            Get
                Return HasChildren AndAlso _isExpanded
            End Get
        End Property

        Public Property IsEditing As Boolean
            Get
                Return _isEditing
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isEditing, value)
                Me.RaisePropertyChanged(NameOf(ShowsName))
            End Set
        End Property

        ''' Der Text im Eingabefeld der Zeile.
        Public Property EditText As String
            Get
                Return _editText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_editText, If(value, ""))
            End Set
        End Property

        ''' Was an der Zeile gemeldet wird: ein abgelehnter Name, ein Fehlschlag.
        Public Property RowMessage As String
            Get
                Return _rowMessage
            End Get
            Friend Set(value As String)
                Me.RaiseAndSetIfChanged(_rowMessage, If(value, ""))
                Me.RaisePropertyChanged(NameOf(HasRowMessage))
            End Set
        End Property

        Public ReadOnly Property HasRowMessage As Boolean
            Get
                Return _rowMessage.Length > 0
            End Get
        End Property

        Public Property IsConfirmingDelete As Boolean
            Get
                Return _isConfirmingDelete
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isConfirmingDelete, value)
                Me.RaisePropertyChanged(NameOf(ShowsName))
            End Set
        End Property

        ''' Die Rueckfrage an der Zeile: wie viele Anpassungen und Unterordner mitgehen.
        Public Property DeletePrompt As String
            Get
                Return _deletePrompt
            End Get
            Friend Set(value As String)
                Me.RaiseAndSetIfChanged(_deletePrompt, If(value, ""))
            End Set
        End Property

        ''' Name, Zaehler und Symbolknoepfe: der gewoehnliche Zustand der Zeile.
        Public ReadOnly Property ShowsName As Boolean
            Get
                Return Not _isEditing AndAlso Not _isConfirmingDelete
            End Get
        End Property

        ''' Der Ordner unter dem Zeiger, auf den eine gezogene Anpassung fallen wuerde.
        Public Property IsDropTarget As Boolean
            Get
                Return _isDropTarget
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isDropTarget, value)
            End Set
        End Property

        ' Die Aufschriften der Rueckfrage kommen uebersetzt aus der Zeile: sie entsteht aus einer
        ' Vorlage, nach dem Durchlauf der Uebersetzung.
        Public ReadOnly Property DeleteText As String
            Get
                Return LocalizationService.T("Löschen")
            End Get
        End Property

        Public ReadOnly Property CancelText As String
            Get
                Return LocalizationService.T("Abbrechen")
            End Get
        End Property
    End Class

    ''' <summary>Ein Regler im aufgeklappten Inhalt einer Anpassung: Beschriftung und Wert.</summary>
    Public Class AdjustmentPresetDetailLine
        Public ReadOnly Property Label As String
        ''' Der Wert aus dem Rezept; bei Eintraegen aus mehreren Feldern "enthalten".
        Public ReadOnly Property Value As String

        Public Sub New(label As String, value As String)
            Me.Label = label
            Me.Value = value
        End Sub
    End Class

    ''' <summary>Eine Gruppe im aufgeklappten Inhalt einer Anpassung mit ihren Reglern.</summary>
    Public Class AdjustmentPresetDetailGroup
        ''' Der Schluessel der Gruppe wie in AdjustmentEntries.
        Public ReadOnly Property Key As String
        Public ReadOnly Property Title As String
        Public ReadOnly Property Lines As New List(Of AdjustmentPresetDetailLine)()

        Public Sub New(key As String, title As String)
            Me.Key = key
            Me.Title = title
        End Sub
    End Class

    ''' <summary>Eine Anpassung in der Liste der Verwaltung. Wie die Ordnerzeile traegt sie ihren
    ''' Zustand selbst: im Umbenennen, in der Rueckfrage, aufgeklappt mit ihrem Inhalt.</summary>
    Public Class AdjustmentPresetRow
        Inherits ReactiveObject

        Private _isEditing As Boolean
        Private _editText As String = ""
        Private _rowMessage As String = ""
        Private _isConfirmingDelete As Boolean
        Private _isExpanded As Boolean
        Private _isDragSource As Boolean
        Private _canApply As Boolean
        Private _details As List(Of AdjustmentPresetDetailGroup)
        Private _detailNote As String = ""

        Public ReadOnly Property Preset As AdjustmentPresetFile
        Public ReadOnly Property Name As String
        ''' Was die Anpassung enthaelt, in einer Zeile (AdjustmentEntries.Describe).
        Public ReadOnly Property Summary As String
        ''' Der Ordner der Anpassung, wie ihn die Auswahllisten nennen.
        Public ReadOnly Property FolderLabel As String
        ''' Die zweite Zeile: der Inhalt, oder der Ordnerpfad, wenn die Liste Anpassungen aus
        ''' mehreren Ordnern zeigt (Suche, Wurzel).
        Public ReadOnly Property SecondLine As String
        ''' Gibt es einen anderen Ordner, in den sie verschoben werden kann?
        Public ReadOnly Property CanMove As Boolean

        Public Sub New(preset As AdjustmentPresetFile, Optional showFolder As Boolean = False,
                       Optional canMove As Boolean = True, Optional canApply As Boolean = False)
            Me.Preset = preset
            Name = preset.Name
            Summary = AdjustmentEntries.Describe(preset.Fields).Replace(Environment.NewLine, "; ")
            FolderLabel = New AdjustmentPresetFolderChoice(preset.Folder).Label
            SecondLine = If(showFolder, FolderLabel, Summary)
            Me.CanMove = canMove
            _canApply = canApply
        End Sub

        Public Property IsEditing As Boolean
            Get
                Return _isEditing
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isEditing, value)
                Me.RaisePropertyChanged(NameOf(ShowsName))
            End Set
        End Property

        Public Property EditText As String
            Get
                Return _editText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_editText, If(value, ""))
            End Set
        End Property

        ''' Was an der Zeile gemeldet wird: ein abgelehnter Name, ein Fehlschlag, "Angewendet".
        Public Property RowMessage As String
            Get
                Return _rowMessage
            End Get
            Friend Set(value As String)
                Me.RaiseAndSetIfChanged(_rowMessage, If(value, ""))
                Me.RaisePropertyChanged(NameOf(HasRowMessage))
            End Set
        End Property

        Public ReadOnly Property HasRowMessage As Boolean
            Get
                Return _rowMessage.Length > 0
            End Get
        End Property

        Public Property IsConfirmingDelete As Boolean
            Get
                Return _isConfirmingDelete
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isConfirmingDelete, value)
                Me.RaisePropertyChanged(NameOf(ShowsName))
            End Set
        End Property

        ''' Name, zweite Zeile und Knoepfe: der gewoehnliche Zustand der Zeile.
        Public ReadOnly Property ShowsName As Boolean
            Get
                Return Not _isEditing AndAlso Not _isConfirmingDelete
            End Get
        End Property

        ''' Der Inhalt steht aufgeklappt unter der Zeile.
        Public Property IsExpanded As Boolean
            Get
                Return _isExpanded
            End Get
            Friend Set(value As Boolean)
                If value AndAlso _details Is Nothing Then
                    BuildDetails()
                    Me.RaisePropertyChanged(NameOf(Details))
                    Me.RaisePropertyChanged(NameOf(DetailNote))
                    Me.RaisePropertyChanged(NameOf(HasDetailNote))
                End If
                Me.RaiseAndSetIfChanged(_isExpanded, value)
            End Set
        End Property

        ''' Die Zeile wird gerade gezogen.
        Public Property IsDragSource As Boolean
            Get
                Return _isDragSource
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isDragSource, value)
            End Set
        End Property

        ''' "Anwenden" gibt es nur, wenn das Fenster aus dem Editor mit einem geoeffneten Bild kommt.
        Public Property CanApply As Boolean
            Get
                Return _canApply
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_canApply, value)
            End Set
        End Property

        ''' <summary>Welche Regler die Anpassung mit welchen Werten enthaelt, nach Gruppen. Entsteht
        ''' beim ersten Aufklappen und ist bis dahin Nothing: das Rezept jeder Zeile schon beim
        ''' Aufbau der Liste zu lesen, waere Arbeit fuer nichts.</summary>
        Public ReadOnly Property Details As List(Of AdjustmentPresetDetailGroup)
            Get
                Return _details
            End Get
        End Property

        ''' Der Satz unter den Gruppen einer Anpassung ohne Feldliste; sonst leer.
        Public ReadOnly Property DetailNote As String
            Get
                Return _detailNote
            End Get
        End Property

        Public ReadOnly Property HasDetailNote As Boolean
            Get
                Return DetailNote.Length > 0
            End Get
        End Property

        ''' <summary>Eine Anpassung MIT Feldliste zeigt jeden enthaltenen Regler, auch den auf dem
        ''' Neutralwert: er ersetzt beim Anwenden. Eine OHNE Liste ersetzt alle Regler; gezeigt
        ''' werden die, die vom Neutralwert abweichen, und ein Satz sagt, was mit den uebrigen
        ''' geschieht. Sonst stuenden dort alle Regler der Anwendung.</summary>
        Private Sub BuildDetails()
            Dim groups As New List(Of AdjustmentPresetDetailGroup)()
            Dim note = ""
            Dim recipe As ImageAdjustments = Nothing
            Try
                recipe = FpxService.DeserializeAdjustments(Preset.RecipeJson)
            Catch
                recipe = Nothing
            End Try
            If recipe Is Nothing Then
                note = LocalizationService.T("Die Anpassung konnte nicht gelesen werden.")
            Else
                Dim entries = If(Preset.Fields Is Nothing,
                                 AdjustmentEntries.NonNeutralEntries(recipe),
                                 AdjustmentEntries.EntriesForFields(Preset.Fields))
                For Each groupKey In AdjustmentEntries.GroupKeys()
                    Dim members = entries.Where(Function(e) String.Equals(e.GroupKey, groupKey, StringComparison.Ordinal)).ToList()
                    If members.Count = 0 Then Continue For
                    Dim group As New AdjustmentPresetDetailGroup(groupKey, LocalizationService.T(AdjustmentEntries.GroupLabel(groupKey)))
                    For Each entry In members
                        group.Lines.Add(New AdjustmentPresetDetailLine(
                            LocalizationService.T(entry.Label),
                            If(AdjustmentEntries.ValueText(entry, recipe), LocalizationService.T("enthalten"))))
                    Next
                    groups.Add(group)
                Next
                If Preset.Fields Is Nothing Then note = LocalizationService.T("Alle übrigen Regler werden auf den Neutralwert gesetzt.")
            End If
            _details = groups
            _detailNote = note
        End Sub

        ' Die Aufschriften kommen uebersetzt aus der Zeile: sie entsteht aus einer Vorlage, nach
        ' dem Durchlauf der Uebersetzung.
        Public ReadOnly Property ApplyText As String
            Get
                Return LocalizationService.T("Anwenden")
            End Get
        End Property

        Public ReadOnly Property DeletePrompt As String
            Get
                Return LocalizationService.T("Löschen?")
            End Get
        End Property

        Public ReadOnly Property DeleteText As String
            Get
                Return LocalizationService.T("Löschen")
            End Get
        End Property

        Public ReadOnly Property CancelText As String
            Get
                Return LocalizationService.T("Abbrechen")
            End Get
        End Property
    End Class

    ''' <summary>Was die Verwaltung mit einer Taste getan hat.</summary>
    Public Enum AdjustmentPresetKeyResult
        ''' Nichts: die Taste gehoert dem Dialog (ESC schliesst, EINGABE schliesst).
        NotHandled
        ''' Verbraucht: die Verwaltung hat etwas getan, oder die Taste soll nichts tun.
        Handled
        ''' Die Taste gehoert dem Bedienelement mit dem Fokus (ENTF im Textfeld, EINGABE am Knopf).
        PassThrough
    End Enum

    ''' <summary>Der Inhalt des Fensters "Anpassungen verwalten": oben die Ablage, darunter Neuer
    ''' Ordner, Suche und Importieren, links der Ordnerbaum, rechts die Anpassungen.
    '''
    ''' JEDE ZEILE TRAEGT IHRE AKTIONEN UND IHREN ZUSTAND: umbenannt wird in der Zeile
    ''' (<see cref="BeginRenamePreset"/>, <see cref="CommitEditAsync"/>), die Rueckfrage zum
    ''' Loeschen steht an der Zeile (<see cref="BeginDeletePreset"/>,
    ''' <see cref="ConfirmDeleteAsync"/>), Meldungen ebenso. Es gibt keinen zweiten Dialog: die
    ''' Anwendung zeigt nur einen zur Zeit. Hoechstens EINE Zeile ist im Umbenennen oder in der
    ''' Rueckfrage.
    '''
    ''' DER BAUM IST EINE LISTE DER SICHTBAREN ZEILEN (<see cref="Folders"/>): die Wurzel "Alle
    ''' Anpassungen" und darunter jeder Ordner, dessen Vorfahren aufgeklappt sind. Die Wurzel
    ''' zeigt rechts alle Anpassungen, ein Ordner die unmittelbar darin; der Zaehler der Zeile
    ''' ist die Zahl, die rechts erscheint.
    '''
    ''' JEDER ZUGRIFF AUF DEN ORDNER LAEUFT IM HINTERGRUND; der Ordner kann in einer entfernten
    ''' Ablage liegen. Dateiauswahl, Fokus und Zeigergesten gehoeren der Ansicht, sie reicht nur
    ''' Pfade und Zeilen herein; so laesst sich alles ohne Fenster messen.</summary>
    Public Class AdjustmentPresetManagerViewModel
        Inherits ReactiveObject

        Private ReadOnly _fixedStore As AdjustmentPresetStore
        Private _listing As New AdjustmentPresetListing()
        Private _selectedFolder As AdjustmentPresetFolderNode
        Private _selectedPreset As AdjustmentPresetRow
        Private _statusText As String = ""
        Private _isReachable As Boolean = True
        Private _searchText As String = ""
        ''' Die zugeklappten Ordner; alles andere ist aufgeklappt.
        Private ReadOnly _collapsed As New HashSet(Of String)(StringComparer.Ordinal)
        Private _editingFolder As AdjustmentPresetFolderNode
        Private _editingPreset As AdjustmentPresetRow
        Private _confirmFolder As AdjustmentPresetFolderNode
        Private _confirmPreset As AdjustmentPresetRow
        Private _expandedPresetId As String
        Private _draggedPreset As AdjustmentPresetRow
        Private _dropTarget As AdjustmentPresetFolderNode
        ''' Welche Seite zuletzt gewaehlt wurde: an sie gehen F2 und ENTF.
        Private _presetIsActive As Boolean
        Private _committing As Boolean
        Private _applyHandler As Func(Of AdjustmentPresetFile, Boolean)
        Private _settingsRequested As Boolean

        ''' Die sichtbaren Zeilen des Baums, die Wurzel zuerst.
        Public ReadOnly Property Folders As New ObservableCollection(Of AdjustmentPresetFolderNode)()
        Public ReadOnly Property Presets As New ObservableCollection(Of AdjustmentPresetRow)()

        Public ReadOnly Property NewFolderCommand As ICommand
        Public ReadOnly Property ShowInFileManagerCommand As ICommand
        Public ReadOnly Property ChangeFolderCommand As ICommand

        ''' <summary>Eine Zeile ist ins Umbenennen gegangen: die Ansicht setzt den Fokus in ihr
        ''' Eingabefeld.</summary>
        Public Event EditStarted As Action(Of Object)

        ''' <summary>Das Fenster soll schliessen (der Weg zur Einstellung).</summary>
        Public Event CloseRequested As Action

        ''' <param name="store">Ein fester Bestand (Pruefstand); ohne ihn gilt der Ordner aus den
        ''' Einstellungen.</param>
        Public Sub New(Optional store As AdjustmentPresetStore = Nothing)
            _fixedStore = store
            NewFolderCommand = ReactiveCommand.Create(Sub() BeginNewFolder())
            ShowInFileManagerCommand = ReactiveCommand.Create(Sub() ShowInFileManager())
            ChangeFolderCommand = ReactiveCommand.Create(Sub() RequestChangeFolder())
        End Sub

        Private Function Store() As AdjustmentPresetStore
            Return If(_fixedStore, AdjustmentPresetStore.Current())
        End Function

        ' ── Stand ───────────────────────────────────────────────────────────────────────────

        ''' <summary>Der Ordner, in dem die Anpassungen liegen, ungekuerzt.</summary>
        Public ReadOnly Property RootPath As String
            Get
                Return _listing.Root
            End Get
        End Property

        ''' <summary>Der Ordner fuer die Kopfzeile: der Heimatordner als ~, ein langer Pfad in
        ''' der Mitte gekuerzt. Der volle Pfad steht im Kurzhinweis.</summary>
        Public ReadOnly Property RootDisplayPath As String
            Get
                Return ShortenPath(_listing.Root)
            End Get
        End Property

        ''' <summary>Kuerzt einen Pfad fuer die Anzeige: der Heimatordner wird zu ~, und was
        ''' danach laenger ist als <paramref name="maxLength"/>, verliert seine Mitte.</summary>
        Public Shared Function ShortenPath(path As String, Optional maxLength As Integer = 60) As String
            Dim text = If(path, "").Trim()
            Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            If home.Length > 1 Then
                home = home.TrimEnd(IO.Path.DirectorySeparatorChar)
                If String.Equals(text, home, StringComparison.Ordinal) Then
                    text = "~"
                ElseIf text.StartsWith(home & IO.Path.DirectorySeparatorChar, StringComparison.Ordinal) Then
                    text = "~" & text.Substring(home.Length)
                End If
            End If
            If maxLength < 8 OrElse text.Length <= maxLength Then Return text
            Dim keep = maxLength - 3
            Dim head = keep \ 2
            Return text.Substring(0, head) & "..." & text.Substring(text.Length - (keep - head))
        End Function

        ''' <summary>False, wenn der Ordner nicht erreichbar ist. Dann sind Baum und Liste leer,
        ''' und <see cref="UnreachableText"/> sagt, wo er festgelegt wird.</summary>
        Public Property IsReachable As Boolean
            Get
                Return _isReachable
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isReachable, value)
                Me.RaisePropertyChanged(NameOf(IsUnreachable))
                Me.RaisePropertyChanged(NameOf(UnreachableText))
            End Set
        End Property

        Public ReadOnly Property IsUnreachable As Boolean
            Get
                Return Not _isReachable
            End Get
        End Property

        Public ReadOnly Property UnreachableText As String
            Get
                Return UnreachableMessage(_listing.Root)
            End Get
        End Property

        ''' <summary>Der Hinweis, wenn der Ordner der Anpassungen fehlt, mit dem Weg zur
        ''' Einstellung. Derselbe Wortlaut im Editor und in der Verwaltung.</summary>
        Public Shared Function UnreachableMessage(root As String) As String
            Return String.Format(LocalizationService.T("Der Ordner für Anpassungen ist nicht erreichbar: {0}. Er wird in den Einstellungen unter Editor festgelegt."), If(root, ""))
        End Function

        ''' Was Importieren, Exportieren und Verschieben getan haben; eine Zeile unter den Listen.
        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_statusText, If(value, ""))
                Me.RaisePropertyChanged(NameOf(HasStatusText))
            End Set
        End Property

        Public ReadOnly Property HasStatusText As Boolean
            Get
                Return _statusText.Length > 0
            End Get
        End Property

        Public Property SelectedFolder As AdjustmentPresetFolderNode
            Get
                Return _selectedFolder
            End Get
            Set(value As AdjustmentPresetFolderNode)
                ' Die Liste meldet beim Neuaufbau kurz Nothing; die vorlaeufige Zeile eines neuen
                ' Ordners ist kein Ordner, der sich waehlen laesst.
                If value Is Nothing OrElse value Is _selectedFolder OrElse value.IsNew Then Return
                Me.RaiseAndSetIfChanged(_selectedFolder, value)
                _presetIsActive = False
                FillPresets(Nothing)
                RaiseListState()
            End Set
        End Property

        Public Property SelectedPreset As AdjustmentPresetRow
            Get
                Return _selectedPreset
            End Get
            Set(value As AdjustmentPresetRow)
                Me.RaiseAndSetIfChanged(_selectedPreset, value)
                If value IsNot Nothing Then _presetIsActive = True
            End Set
        End Property

        ''' Der gewaehlte Ordner relativ zum Wurzelordner; leer bei der Wurzel.
        Public ReadOnly Property SelectedFolderPath As String
            Get
                Return If(_selectedFolder?.Path, "")
            End Get
        End Property

        ' ── Suche und Ueberschrift der Liste ────────────────────────────────────────────────

        ''' <summary>Der Suchbegriff. Solange einer steht, zeigt die Liste die Anpassungen aus
        ''' ALLEN Ordnern, deren Name ihn enthaelt, ohne Gross und Klein.</summary>
        Public Property SearchText As String
            Get
                Return _searchText
            End Get
            Set(value As String)
                Dim text = If(value, "")
                If String.Equals(text, _searchText, StringComparison.Ordinal) Then Return
                Dim keep = _selectedPreset?.Preset.Id
                Me.RaiseAndSetIfChanged(_searchText, text)
                Me.RaisePropertyChanged(NameOf(IsSearching))
                ClearRowStates()
                FillPresets(keep)
                RaiseListState()
            End Set
        End Property

        Public ReadOnly Property IsSearching As Boolean
            Get
                Return _searchText.Trim().Length > 0
            End Get
        End Property

        ''' <summary>Die Ueberschrift der Liste: der Ordner, "Alle Anpassungen" oder die Zahl der
        ''' Treffer.</summary>
        Public ReadOnly Property ListHeader As String
            Get
                If Not _isReachable Then Return ""
                If IsSearching Then Return String.Format(LocalizationService.T("{0} Treffer"), Presets.Count)
                If _selectedFolder Is Nothing Then Return LocalizationService.T("Anpassungen")
                If _selectedFolder.IsRoot Then Return LocalizationService.T("Alle Anpassungen")
                Return String.Format(LocalizationService.T("Anpassungen in {0}"), _selectedFolder.Name)
            End Get
        End Property

        ''' Die Zahl hinter der Ueberschrift; in der Suche nennt die Ueberschrift sie selbst.
        Public ReadOnly Property ListCountText As String
            Get
                If Not _isReachable OrElse IsSearching OrElse _selectedFolder Is Nothing Then Return ""
                Return "(" & Presets.Count.ToString(Globalization.CultureInfo.CurrentCulture) & ")"
            End Get
        End Property

        ''' <summary>Was die leere Liste erklaert: kein Ordner gewaehlt, Ordner leer, keine
        ''' Treffer. Leer, solange die Liste etwas zeigt.</summary>
        Public ReadOnly Property EmptyText As String
            Get
                If Not _isReachable OrElse Presets.Count > 0 Then Return ""
                If IsSearching Then Return LocalizationService.T("Keine Anpassung mit diesem Namen gefunden.")
                If _selectedFolder Is Nothing Then Return LocalizationService.T("Links einen Ordner wählen.")
                Return LocalizationService.T("In diesem Ordner liegt keine Anpassung. Gespeichert wird im Editor unter Gespeicherte Anpassungen.")
            End Get
        End Property

        Public ReadOnly Property HasEmptyText As Boolean
            Get
                Return EmptyText.Length > 0
            End Get
        End Property

        Private Sub RaiseListState()
            For Each propertyName In {NameOf(SelectedFolderPath), NameOf(ListHeader), NameOf(ListCountText),
                                      NameOf(EmptyText), NameOf(HasEmptyText)}
                Me.RaisePropertyChanged(propertyName)
            Next
        End Sub

        ' ── Lesen und Aufbauen ──────────────────────────────────────────────────────────────

        ''' <summary>Liest den Ordner im Hintergrund neu und baut Baum und Liste auf. Der
        ''' gewaehlte Ordner, die gewaehlte Anpassung, der aufgeklappte Inhalt und eine offene
        ''' Rueckfrage bleiben, soweit es ihre Zeilen noch gibt.</summary>
        ''' <param name="selectFolder">Der Ordner, der danach gewaehlt sein soll.</param>
        ''' <param name="selectPresetId">Die Anpassung, die danach gewaehlt sein soll.</param>
        Public Async Function ReloadAsync(Optional selectFolder As String = Nothing,
                                          Optional selectPresetId As String = Nothing) As Task
            Dim wantedFolder = AdjustmentPresetStore.NormalizeFolder(If(selectFolder, SelectedFolderPath))
            Dim wantedPreset = If(selectPresetId, _selectedPreset?.Preset.Id)
            Dim confirmFolderPath = _confirmFolder?.Path
            Dim confirmPresetId = _confirmPreset?.Preset.Id
            Dim fixedStore = _fixedStore
            Dim listing = Await Task.Run(Function() If(fixedStore IsNot Nothing, fixedStore.ReadAll(), AdjustmentPresetStore.LoadCurrent()))
            _listing = listing
            Me.RaisePropertyChanged(NameOf(RootPath))
            Me.RaisePropertyChanged(NameOf(RootDisplayPath))
            IsReachable = listing.Reachable
            Me.RaisePropertyChanged(NameOf(CanApply))

            ForgetRowStates()
            ' Der gewuenschte Ordner soll sichtbar sein: seine Vorfahren klappen auf.
            Dim ancestor = AdjustmentPresetStore.ParentFolder(wantedFolder)
            While ancestor.Length > 0
                _collapsed.Remove(ancestor)
                ancestor = AdjustmentPresetStore.ParentFolder(ancestor)
            End While
            BuildFolderRows(wantedFolder)
            FillPresets(wantedPreset)
            RaiseListState()

            If confirmFolderPath IsNot Nothing Then
                Dim node = Folders.FirstOrDefault(Function(f) f.CanChange AndAlso String.Equals(f.Path, confirmFolderPath, StringComparison.Ordinal))
                If node IsNot Nothing Then BeginDeleteFolder(node)
            ElseIf confirmPresetId IsNot Nothing Then
                Dim row = Presets.FirstOrDefault(Function(r) String.Equals(r.Preset.Id, confirmPresetId, StringComparison.Ordinal))
                If row IsNot Nothing Then BeginDeletePreset(row)
            End If
        End Function

        Private Function IsFolderVisible(folder As String) As Boolean
            Dim ancestor = AdjustmentPresetStore.ParentFolder(folder)
            While ancestor.Length > 0
                If _collapsed.Contains(ancestor) Then Return False
                ancestor = AdjustmentPresetStore.ParentFolder(ancestor)
            End While
            Return Not _collapsed.Contains("")
        End Function

        ''' <summary>Baut die sichtbaren Zeilen aus dem gelesenen Stand und dem, was zugeklappt
        ''' ist. Gewaehlt ist danach <paramref name="selectPath"/>; liegt der Ordner in einem
        ''' zugeklappten Zweig oder gibt es ihn nicht mehr, sein naechster sichtbarer Vorfahr.</summary>
        Private Sub BuildFolderRows(selectPath As String)
            Folders.Clear()
            _selectedFolder = Nothing
            If _listing.Reachable Then
                Dim all = _listing.Folders.OrderBy(Function(f) f, StringComparer.OrdinalIgnoreCase).ToList()
                Folders.Add(New AdjustmentPresetFolderNode("", _listing.Presets.Count, all.Count > 0, Not _collapsed.Contains("")))
                For Each folder In OrderAsTree(all)
                    If Not IsFolderVisible(folder) Then Continue For
                    Dim prefix = folder & "/"
                    Dim hasChildren = all.Any(Function(f) f.StartsWith(prefix, StringComparison.Ordinal))
                    Folders.Add(New AdjustmentPresetFolderNode(folder, _listing.PresetsIn(folder).Count, hasChildren,
                                                               Not _collapsed.Contains(folder)))
                Next
                Dim wanted = AdjustmentPresetStore.NormalizeFolder(selectPath)
                Do
                    Dim path = wanted
                    _selectedFolder = Folders.FirstOrDefault(Function(f) String.Equals(f.Path, path, StringComparison.Ordinal))
                    If _selectedFolder IsNot Nothing OrElse wanted.Length = 0 Then Exit Do
                    wanted = AdjustmentPresetStore.ParentFolder(wanted)
                Loop
                If _selectedFolder Is Nothing Then _selectedFolder = Folders.FirstOrDefault()
            End If
            Me.RaisePropertyChanged(NameOf(SelectedFolder))
        End Sub

        ''' <summary>Die Ordner in der Reihenfolge des Baums: jeder Ordner unmittelbar vor seinen
        ''' Unterordnern, Geschwister nach Namen. Eine blosse Sortierung der Pfade reicht dafuer
        ''' nicht: "A B" stuende zwischen "A" und "A/Unter".</summary>
        Private Shared Function OrderAsTree(folders As List(Of String)) As List(Of String)
            Dim result As New List(Of String)()
            AppendChildren("", folders, result)
            Return result
        End Function

        Private Shared Sub AppendChildren(parent As String, folders As List(Of String), result As List(Of String))
            For Each child In folders.Where(Function(f) String.Equals(AdjustmentPresetStore.ParentFolder(f), parent, StringComparison.Ordinal)).
                                      OrderBy(Function(f) AdjustmentPresetStore.FolderName(f), StringComparer.OrdinalIgnoreCase).ToList()
                result.Add(child)
                AppendChildren(child, folders, result)
            Next
        End Sub

        Private Sub FillPresets(selectPresetId As String)
            ' Die Zeilen entstehen neu: eine Bearbeitung, Rueckfrage oder ein Zug an einer alten
            ' Zeile gilt nicht mehr. Bliebe die Rueckfrage stehen, fragte eine Zeile, die niemand
            ' mehr sieht.
            _editingPreset = Nothing
            _confirmPreset = Nothing
            CancelPresetDrag()
            Presets.Clear()
            If _isReachable AndAlso _selectedFolder IsNot Nothing Then
                Dim source As IEnumerable(Of AdjustmentPresetFile)
                Dim showFolder As Boolean
                If IsSearching Then
                    Dim term = _searchText.Trim()
                    source = _listing.Presets.Where(Function(p) p.Name.IndexOf(term, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    showFolder = True
                ElseIf _selectedFolder.IsRoot Then
                    source = _listing.Presets
                    showFolder = True
                Else
                    source = _listing.PresetsIn(_selectedFolder.Path)
                    showFolder = False
                End If
                Dim canMove = _listing.Folders.Count > 0
                Dim canApply = Me.CanApply
                For Each preset In source.OrderBy(Function(p) p.Name, StringComparer.CurrentCultureIgnoreCase).
                                          ThenBy(Function(p) p.Folder, StringComparer.OrdinalIgnoreCase)
                    Presets.Add(New AdjustmentPresetRow(preset, showFolder, canMove, canApply))
                Next
            End If
            _selectedPreset = Presets.FirstOrDefault(Function(r) String.Equals(r.Preset.Id, selectPresetId, StringComparison.Ordinal))
            Me.RaisePropertyChanged(NameOf(SelectedPreset))
            Dim expanded = Presets.FirstOrDefault(Function(r) String.Equals(r.Preset.Id, _expandedPresetId, StringComparison.Ordinal))
            If expanded IsNot Nothing Then expanded.IsExpanded = True
        End Sub

        ' ── Auf- und Zuklappen ──────────────────────────────────────────────────────────────

        ''' <summary>Klappt einen Ordner auf oder zu. Liegt der gewaehlte Ordner danach in einem
        ''' zugeklappten Zweig, ist der zugeklappte Ordner gewaehlt.</summary>
        Public Sub ToggleFolder(node As AdjustmentPresetFolderNode)
            If node Is Nothing OrElse node.IsNew OrElse Not node.HasChildren Then Return
            ClearRowStates()
            If node.IsExpanded Then
                _collapsed.Add(node.Path)
            Else
                _collapsed.Remove(node.Path)
            End If
            Dim before = SelectedFolderPath
            BuildFolderRows(before)
            If Not String.Equals(SelectedFolderPath, before, StringComparison.Ordinal) Then FillPresets(Nothing)
            RaiseListState()
        End Sub

        ''' <summary>Den aufgeklappten Inhalt einer Anpassung zeigen oder schliessen. Es ist
        ''' immer nur eine aufgeklappt.</summary>
        Public Sub ToggleDetails(row As AdjustmentPresetRow)
            If row Is Nothing OrElse Not Presets.Contains(row) Then Return
            Dim open = Not row.IsExpanded
            For Each other In Presets
                If other IsNot row AndAlso other.IsExpanded Then other.IsExpanded = False
            Next
            row.IsExpanded = open
            _expandedPresetId = If(open, row.Preset.Id, Nothing)
        End Sub

        ' ── Zustand der Zeilen ──────────────────────────────────────────────────────────────

        ''' Ist gerade eine Zeile im Umbenennen?
        Public ReadOnly Property IsEditing As Boolean
            Get
                Return _editingFolder IsNot Nothing OrElse _editingPreset IsNot Nothing
            End Get
        End Property

        ''' Steht gerade eine Rueckfrage zum Loeschen an einer Zeile?
        Public ReadOnly Property IsConfirmingDelete As Boolean
            Get
                Return _confirmFolder IsNot Nothing OrElse _confirmPreset IsNot Nothing
            End Get
        End Property

        ''' <summary>Vergisst die Zustaende, ohne die Zeilen anzufassen: sie werden gleich neu
        ''' gebaut.</summary>
        Private Sub ForgetRowStates()
            _editingFolder = Nothing
            _editingPreset = Nothing
            _confirmFolder = Nothing
            _confirmPreset = Nothing
            _draggedPreset = Nothing
            _dropTarget = Nothing
        End Sub

        ''' <summary>Beendet jede offene Bearbeitung und Rueckfrage, ohne etwas zu aendern, und
        ''' nimmt die Meldungen von den Zeilen. Vor jeder neuen Aktion: hoechstens eine Zeile ist
        ''' in einem besonderen Zustand.</summary>
        Private Sub ClearRowStates()
            CancelEdit()
            CancelDelete()
            CancelPresetDrag()
            For Each node In Folders
                node.RowMessage = ""
            Next
            For Each row In Presets
                row.RowMessage = ""
            Next
            StatusText = ""
        End Sub

        ' ── Umbenennen in der Zeile ─────────────────────────────────────────────────────────

        ''' <summary>"Neuer Ordner": eine vorlaeufige Zeile unter dem gewaehlten Ordner, gleich im
        ''' Bearbeiten. Angelegt wird erst mit <see cref="CommitEditAsync"/>.</summary>
        Public Sub BeginNewFolder()
            If Not _isReachable OrElse _selectedFolder Is Nothing Then Return
            ClearRowStates()
            Dim parent = _selectedFolder
            If _collapsed.Remove(parent.Path) Then
                BuildFolderRows(parent.Path)
                parent = _selectedFolder
            End If
            Dim node = AdjustmentPresetFolderNode.CreateNew(parent.Path)
            Folders.Insert(Folders.IndexOf(parent) + 1, node)
            node.IsEditing = True
            _editingFolder = node
            RaiseEvent EditStarted(node)
        End Sub

        ''' <summary>Das Umbenennen eines Ordners beginnt in seiner Zeile. Die Wurzel hat keines.</summary>
        Public Sub BeginRenameFolder(node As AdjustmentPresetFolderNode)
            If Not _isReachable OrElse node Is Nothing OrElse Not node.CanChange OrElse Not Folders.Contains(node) Then Return
            ClearRowStates()
            node.EditText = node.Name
            node.IsEditing = True
            _editingFolder = node
            RaiseEvent EditStarted(node)
        End Sub

        ''' <summary>Das Umbenennen einer Anpassung beginnt in ihrer Zeile.</summary>
        Public Sub BeginRenamePreset(row As AdjustmentPresetRow)
            If Not _isReachable OrElse row Is Nothing OrElse Not Presets.Contains(row) Then Return
            ClearRowStates()
            row.EditText = row.Name
            row.IsEditing = True
            _editingPreset = row
            RaiseEvent EditStarted(row)
        End Sub

        ''' <summary>Verwirft die offene Bearbeitung. Die vorlaeufige Zeile eines neuen Ordners
        ''' verschwindet.</summary>
        Public Sub CancelEdit()
            Dim node = _editingFolder
            Dim row = _editingPreset
            _editingFolder = Nothing
            _editingPreset = Nothing
            If node IsNot Nothing Then
                node.IsEditing = False
                node.RowMessage = ""
                If node.IsNew Then Folders.Remove(node)
            End If
            If row IsNot Nothing Then
                row.IsEditing = False
                row.RowMessage = ""
            End If
        End Sub

        ''' <summary>Uebernimmt die offene Bearbeitung: EINGABE im Feld und das Verlassen des
        ''' Felds. Ein leerer oder vergebener Name wird an der Zeile gemeldet, und das Feld bleibt
        ''' offen. Ein unveraenderter Name schliesst das Feld ohne Plattenzugriff.</summary>
        ''' <param name="item">Die Zeile, deren Feld verlassen wurde; ist sie nicht die, die
        ''' gerade bearbeitet wird, geschieht nichts.</param>
        ''' <returns>Ob die Bearbeitung beendet ist.</returns>
        Public Async Function CommitEditAsync(Optional item As Object = Nothing) As Task(Of Boolean)
            Dim node = _editingFolder
            Dim row = _editingPreset
            If node Is Nothing AndAlso row Is Nothing Then Return False
            If item IsNot Nothing AndAlso item IsNot node AndAlso item IsNot row Then Return False
            If _committing Then Return False
            _committing = True
            Try
                Dim store = Me.Store()
                If node IsNot Nothing Then
                    Dim text = node.EditText.Trim()
                    If text.Length = 0 Then
                        node.RowMessage = LocalizationService.T("Bitte einen Namen eingeben.")
                        Return False
                    End If
                    Dim selectFolder As String
                    If node.IsNew Then
                        Dim parent = node.ParentPath
                        Dim created = Await Task.Run(Function() store.CreateFolder(parent, text))
                        If created Is Nothing Then
                            node.RowMessage = LocalizationService.T("Der Ordner ließ sich nicht anlegen. Gibt es ihn schon?")
                            Return False
                        End If
                        selectFolder = created
                    ElseIf String.Equals(text, node.Name, StringComparison.Ordinal) Then
                        CancelEdit()
                        Return True
                    Else
                        Dim path = node.Path
                        Dim renamed = Await Task.Run(Function() store.RenameFolder(path, text))
                        If renamed Is Nothing Then
                            node.RowMessage = LocalizationService.T("Der Ordner ließ sich nicht umbenennen. Gibt es den Namen schon?")
                            Return False
                        End If
                        ' Der gewaehlte Ordner kann der umbenannte sein oder darin liegen.
                        Dim selected = SelectedFolderPath
                        If String.Equals(selected, path, StringComparison.Ordinal) Then
                            selectFolder = renamed
                        ElseIf selected.StartsWith(path & "/", StringComparison.Ordinal) Then
                            selectFolder = renamed & selected.Substring(path.Length)
                        Else
                            selectFolder = selected
                        End If
                        If _collapsed.Remove(path) Then _collapsed.Add(renamed)
                    End If
                    _editingFolder = Nothing
                    Await ReloadAsync(selectFolder)
                    Return True
                End If

                Dim newName = row.EditText.Trim()
                If newName.Length = 0 Then
                    row.RowMessage = LocalizationService.T("Bitte einen Namen eingeben.")
                    Return False
                End If
                If String.Equals(newName, row.Name, StringComparison.Ordinal) Then
                    CancelEdit()
                    Return True
                End If
                Dim preset = row.Preset
                If Not Await Task.Run(Function() store.RenamePreset(preset, newName)) Then
                    row.RowMessage = LocalizationService.T("Die Anpassung ließ sich nicht umbenennen. Gibt es den Namen in diesem Ordner schon?")
                    Return False
                End If
                _editingPreset = Nothing
                Await ReloadAsync(Nothing, preset.Id)
                Return True
            Finally
                _committing = False
            End Try
        End Function

        ' ── Loeschen mit Rueckfrage an der Zeile ────────────────────────────────────────────

        ''' <summary>Die Zeile des Ordners wechselt zur Rueckfrage und nennt, wie viele
        ''' Anpassungen und Unterordner mitgehen. Geloescht ist damit nichts.</summary>
        Public Sub BeginDeleteFolder(node As AdjustmentPresetFolderNode)
            If Not _isReachable OrElse node Is Nothing OrElse Not node.CanChange OrElse Not Folders.Contains(node) Then Return
            ClearRowStates()
            Dim folder = node.Path
            Dim prefix = folder & "/"
            Dim presets = _listing.Presets.Where(
                Function(p) String.Equals(p.Folder, folder, StringComparison.Ordinal) OrElse
                            p.Folder.StartsWith(prefix, StringComparison.Ordinal)).Count()
            Dim subfolders = _listing.Folders.Where(Function(f) f.StartsWith(prefix, StringComparison.Ordinal)).Count()
            node.DeletePrompt = String.Format(LocalizationService.T("Löschen? {0} Anpassung(en) und {1} Unterordner gehen mit."), presets, subfolders)
            node.IsConfirmingDelete = True
            _confirmFolder = node
        End Sub

        ''' <summary>Die Zeile der Anpassung wechselt zur Rueckfrage. Geloescht ist damit nichts.</summary>
        Public Sub BeginDeletePreset(row As AdjustmentPresetRow)
            If Not _isReachable OrElse row Is Nothing OrElse Not Presets.Contains(row) Then Return
            ClearRowStates()
            row.IsConfirmingDelete = True
            _confirmPreset = row
        End Sub

        Public Sub CancelDelete()
            If _confirmFolder IsNot Nothing Then _confirmFolder.IsConfirmingDelete = False
            If _confirmPreset IsNot Nothing Then _confirmPreset.IsConfirmingDelete = False
            _confirmFolder = Nothing
            _confirmPreset = Nothing
        End Sub

        ''' <summary>Loescht, wonach die Zeile fragt. Ohne offene Rueckfrage geschieht nichts.</summary>
        Public Async Function ConfirmDeleteAsync() As Task(Of Boolean)
            Dim node = _confirmFolder
            Dim row = _confirmPreset
            If node Is Nothing AndAlso row Is Nothing Then Return False
            Dim store = Me.Store()
            If node IsNot Nothing Then
                Dim folder = node.Path
                Dim deleted = Await Task.Run(Function() store.DeleteFolder(folder))
                CancelDelete()
                If deleted < 0 Then
                    node.RowMessage = LocalizationService.T("Der Ordner ließ sich nicht löschen.")
                    Return False
                End If
                ' Lag der gewaehlte Ordner darin, ist danach der Ordner darueber gewaehlt.
                Dim selected = SelectedFolderPath
                Dim inside = String.Equals(selected, folder, StringComparison.Ordinal) OrElse
                             selected.StartsWith(folder & "/", StringComparison.Ordinal)
                Await ReloadAsync(If(inside, AdjustmentPresetStore.ParentFolder(folder), selected))
                Return True
            End If
            Dim preset = row.Preset
            Dim removed = Await Task.Run(Function() store.DeletePreset(preset))
            CancelDelete()
            If Not removed Then
                row.RowMessage = LocalizationService.T("Die Anpassung ließ sich nicht löschen.")
                Return False
            End If
            Await ReloadAsync()
            Return True
        End Function

        ' ── Verschieben: ueber die Ordnerliste und ueber das Ablegen im Baum ────────────────

        ''' <summary>Die Ordner, in die sich eine Anpassung verschieben laesst: alle ausser dem,
        ''' in dem sie liegt. Der Wurzelordner heisst darin "Hauptordner".</summary>
        Public Function MoveTargetsFor(row As AdjustmentPresetRow) As List(Of AdjustmentPresetFolderChoice)
            If row Is Nothing Then Return New List(Of AdjustmentPresetFolderChoice)()
            Dim source = AdjustmentPresetStore.NormalizeFolder(row.Preset.Folder)
            Return AdjustmentPresetFolderChoice.ListOf(_listing.Folders).
                Where(Function(c) Not String.Equals(c.Path, source, StringComparison.Ordinal)).ToList()
        End Function

        ''' <summary>Verschiebt eine Anpassung in einen Ordner. In den eigenen Ordner geschieht
        ''' nichts; liegt im Ziel schon eine gleichen Namens, steht die Meldung an der Zeile und
        ''' nichts wird bewegt. Der gewaehlte Ordner bleibt gewaehlt.</summary>
        ''' <returns>Ob verschoben wurde.</returns>
        Public Async Function MovePresetAsync(row As AdjustmentPresetRow, targetFolder As String) As Task(Of Boolean)
            If Not _isReachable OrElse row Is Nothing OrElse Not Presets.Contains(row) Then Return False
            Dim preset = row.Preset
            Dim target = AdjustmentPresetStore.NormalizeFolder(targetFolder)
            If String.Equals(target, AdjustmentPresetStore.NormalizeFolder(preset.Folder), StringComparison.Ordinal) Then Return False
            ClearRowStates()
            Dim store = Me.Store()
            Dim name = preset.Name
            If _listing.PresetsIn(target).Any(Function(p) String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) OrElse
               Not Await Task.Run(Function() store.MovePreset(preset, target)) Then
                row.RowMessage = LocalizationService.T("Die Anpassung ließ sich nicht verschieben. Gibt es dort schon eine gleichen Namens?")
                Return False
            End If
            Await ReloadAsync(Nothing, preset.Id)
            StatusText = String.Format(LocalizationService.T("Anpassung verschoben: {0}"),
                                       New AdjustmentPresetFolderChoice(target).Label & " / " & name)
            Return True
        End Function

        ''' Die Anpassung, die gerade gezogen wird.
        Public ReadOnly Property DraggedPreset As AdjustmentPresetRow
            Get
                Return _draggedPreset
            End Get
        End Property

        ''' Der Ordner, auf den die gezogene Anpassung fallen wuerde.
        Public ReadOnly Property DropTarget As AdjustmentPresetFolderNode
            Get
                Return _dropTarget
            End Get
        End Property

        ''' <summary>Ein Zug mit einer Anpassung beginnt. Nicht aus einer Zeile, die gerade
        ''' umbenannt wird oder fragt.</summary>
        Public Function BeginPresetDrag(row As AdjustmentPresetRow) As Boolean
            If Not _isReachable OrElse row Is Nothing OrElse Not Presets.Contains(row) OrElse Not row.ShowsName Then Return False
            ClearRowStates()
            _draggedPreset = row
            row.IsDragSource = True
            Return True
        End Function

        ''' <summary>Kann die gezogene Anpassung auf diesem Ordner abgelegt werden? Nicht auf dem,
        ''' in dem sie liegt.</summary>
        Public Function CanDropOn(node As AdjustmentPresetFolderNode) As Boolean
            If _draggedPreset Is Nothing OrElse node Is Nothing OrElse node.IsNew Then Return False
            Return Not String.Equals(node.Path, AdjustmentPresetStore.NormalizeFolder(_draggedPreset.Preset.Folder), StringComparison.Ordinal)
        End Function

        ''' <summary>Der Zeiger steht waehrend des Zugs ueber dieser Ordnerzeile (oder ueber
        ''' keiner): ein erlaubtes Ziel wird hervorgehoben, jedes andere nicht.</summary>
        Public Sub HoverDropTarget(node As AdjustmentPresetFolderNode)
            Dim target = If(CanDropOn(node), node, Nothing)
            If target Is _dropTarget Then Return
            If _dropTarget IsNot Nothing Then _dropTarget.IsDropTarget = False
            _dropTarget = target
            If _dropTarget IsNot Nothing Then _dropTarget.IsDropTarget = True
        End Sub

        ''' <summary>Der Zug endet ohne Ablegen: ESC, verlorener Zeigergriff.</summary>
        Public Sub CancelPresetDrag()
            If _dropTarget IsNot Nothing Then _dropTarget.IsDropTarget = False
            If _draggedPreset IsNot Nothing Then _draggedPreset.IsDragSource = False
            _dropTarget = Nothing
            _draggedPreset = Nothing
        End Sub

        ''' <summary>Der Zug endet mit dem Loslassen: steht der Zeiger ueber einem erlaubten
        ''' Ordner, wird dorthin verschoben, sonst geschieht nichts.</summary>
        ''' <returns>Ob verschoben wurde.</returns>
        Public Async Function CompletePresetDragAsync() As Task(Of Boolean)
            Dim row = _draggedPreset
            Dim target = _dropTarget
            CancelPresetDrag()
            If row Is Nothing OrElse target Is Nothing Then Return False
            Return Await MovePresetAsync(row, target.Path)
        End Function

        ' ── Anwenden ────────────────────────────────────────────────────────────────────────

        ''' <summary>Der Weg, auf dem "Anwenden" die Anpassung auf das geoeffnete Bild setzt. Der
        ''' Editor setzt ihn, wenn ein Bild offen ist; ohne ihn gibt es den Knopf nicht.</summary>
        Public Property ApplyHandler As Func(Of AdjustmentPresetFile, Boolean)
            Get
                Return _applyHandler
            End Get
            Set(value As Func(Of AdjustmentPresetFile, Boolean))
                _applyHandler = value
                Me.RaisePropertyChanged(NameOf(CanApply))
                For Each row In Presets
                    row.CanApply = CanApply
                Next
            End Set
        End Property

        Public ReadOnly Property CanApply As Boolean
            Get
                Return _isReachable AndAlso _applyHandler IsNot Nothing
            End Get
        End Property

        ''' <summary>Wendet die Anpassung der Zeile auf das im Editor geoeffnete Bild an. Das
        ''' Fenster bleibt offen, die Zeile bestaetigt es.</summary>
        Public Function ApplyPreset(row As AdjustmentPresetRow) As Boolean
            If Not CanApply OrElse row Is Nothing OrElse Not Presets.Contains(row) Then Return False
            ClearRowStates()
            Dim applied = _applyHandler(row.Preset)
            row.RowMessage = If(applied, LocalizationService.T("Angewendet"),
                                LocalizationService.T("Die Anpassung konnte nicht gelesen werden."))
            Return applied
        End Function

        ' ── Importieren, Exportieren, Zeigen, Ordner aendern ────────────────────────────────

        ''' <summary>Nimmt eine Datei oder einen Ordner samt Unterordnern in den gewaehlten Ordner
        ''' auf und sagt danach, was geschah.</summary>
        Public Async Function ImportAsync(sourcePath As String) As Task
            If String.IsNullOrWhiteSpace(sourcePath) OrElse Not _isReachable Then Return
            ClearRowStates()
            Dim store = Me.Store()
            Dim folder = SelectedFolderPath
            Dim result = Await Task.Run(Function() store.Import(sourcePath, folder))
            Await ReloadAsync(folder)
            Dim parts As New List(Of String) From {
                String.Format(LocalizationService.T("{0} Anpassung(en) importiert"), result.Imported)}
            If result.AlreadyPresent > 0 Then parts.Add(String.Format(LocalizationService.T("{0} schon vorhanden"), result.AlreadyPresent))
            If result.Skipped > 0 Then parts.Add(String.Format(LocalizationService.T("{0} Datei(en) nicht lesbar"), result.Skipped))
            StatusText = String.Join(", ", parts)
        End Function

        ''' <summary>Schreibt die Anpassung einer Zeile als Datei an den gewaehlten Ort.</summary>
        Public Async Function ExportPresetAsync(row As AdjustmentPresetRow, targetFile As String) As Task
            Dim preset = row?.Preset
            If preset Is Nothing OrElse String.IsNullOrWhiteSpace(targetFile) Then Return
            ClearRowStates()
            Dim store = Me.Store()
            Dim written = Await Task.Run(Function() store.ExportPreset(preset, targetFile))
            StatusText = If(written,
                            String.Format(LocalizationService.T("Anpassung exportiert: {0}"), preset.Name),
                            LocalizationService.T("Die Anpassung ließ sich nicht exportieren."))
        End Function

        ''' <summary>Schreibt den Ordner einer Zeile samt Unterordnern in ein Verzeichnis; die
        ''' Wurzel schreibt den ganzen Bestand.</summary>
        Public Async Function ExportFolderAsync(node As AdjustmentPresetFolderNode, targetDirectory As String) As Task
            If node Is Nothing OrElse node.IsNew OrElse String.IsNullOrWhiteSpace(targetDirectory) OrElse Not _isReachable Then Return
            ClearRowStates()
            Dim store = Me.Store()
            Dim folder = node.Path
            Dim written = Await Task.Run(Function() store.ExportFolder(folder, targetDirectory))
            StatusText = String.Format(LocalizationService.T("{0} Anpassung(en) exportiert"), written)
        End Function

        ''' <summary>Der Dateiname, den der Export einer Anpassung vorschlaegt.</summary>
        Public Shared Function SuggestedExportFileName(row As AdjustmentPresetRow) As String
            If row Is Nothing Then Return ""
            Return AdjustmentPresetStore.SafeFileName(row.Preset.Name) & AdjustmentPresetStore.FileExtension
        End Function

        ''' <summary>Oeffnet die Ablage im Dateimanager.</summary>
        Public Sub ShowInFileManager()
            If Not _isReachable Then Return
            ShellOpenService.Open(Store().DirectoryOf(""), "AdjustmentPresets.Folder")
        End Sub

        ''' <summary>True, nachdem "Ordner ändern" oder "Einstellungen öffnen" gewaehlt wurde: wer
        ''' das Fenster gezeigt hat, wechselt nach dem Schliessen in die Einstellungen.</summary>
        Public ReadOnly Property SettingsRequested As Boolean
            Get
                Return _settingsRequested
            End Get
        End Property

        ''' <summary>"Ordner ändern": das Fenster schliesst, danach geht es in die Einstellungen,
        ''' wo der Ordner fuer Anpassungen unter Editor festgelegt wird.</summary>
        Public Sub RequestChangeFolder()
            ClearRowStates()
            _settingsRequested = True
            RaiseEvent CloseRequested()
        End Sub

        ' ── Tastatur ────────────────────────────────────────────────────────────────────────

        ''' <summary>Was eine Taste im Fenster tut. ESC schliesst erst eine offene Bearbeitung
        ''' oder Rueckfrage und erst danach das Fenster; F2 benennt die gewaehlte Zeile um, ENTF
        ''' fragt nach dem Loeschen; EINGABE im Eingabefeld uebernimmt. Gewaehlt ist die Zeile
        ''' der Seite, auf der zuletzt gewaehlt wurde.</summary>
        ''' <param name="sourceIsTextInput">Der Fokus steht in einem Textfeld.</param>
        ''' <param name="sourceIsButton">Der Fokus steht auf einem Knopf.</param>
        Public Function HandleKey(key As Avalonia.Input.Key, sourceIsTextInput As Boolean,
                                  sourceIsButton As Boolean) As AdjustmentPresetKeyResult
            Select Case key
                Case Avalonia.Input.Key.Escape
                    If _draggedPreset IsNot Nothing Then
                        CancelPresetDrag()
                        Return AdjustmentPresetKeyResult.Handled
                    End If
                    If IsEditing Then
                        CancelEdit()
                        Return AdjustmentPresetKeyResult.Handled
                    End If
                    If IsConfirmingDelete Then
                        CancelDelete()
                        Return AdjustmentPresetKeyResult.Handled
                    End If
                    Return AdjustmentPresetKeyResult.NotHandled
                Case Avalonia.Input.Key.Enter
                    If sourceIsTextInput Then
                        ' Im Suchfeld tut EINGABE nichts; das Fenster soll davon nicht schliessen.
                        If IsEditing Then CommitEditInBackground(Nothing)
                        Return AdjustmentPresetKeyResult.Handled
                    End If
                    If sourceIsButton Then Return AdjustmentPresetKeyResult.PassThrough
                    If IsEditing OrElse IsConfirmingDelete Then Return AdjustmentPresetKeyResult.Handled
                    Return AdjustmentPresetKeyResult.NotHandled
                Case Avalonia.Input.Key.F2
                    If sourceIsTextInput Then Return AdjustmentPresetKeyResult.PassThrough
                    If _presetIsActive AndAlso _selectedPreset IsNot Nothing Then
                        BeginRenamePreset(_selectedPreset)
                    Else
                        BeginRenameFolder(_selectedFolder)
                    End If
                    Return AdjustmentPresetKeyResult.Handled
                Case Avalonia.Input.Key.Delete
                    If sourceIsTextInput Then Return AdjustmentPresetKeyResult.PassThrough
                    If _presetIsActive AndAlso _selectedPreset IsNot Nothing Then
                        BeginDeletePreset(_selectedPreset)
                    Else
                        BeginDeleteFolder(_selectedFolder)
                    End If
                    Return AdjustmentPresetKeyResult.Handled
            End Select
            Return AdjustmentPresetKeyResult.NotHandled
        End Function

        ''' <summary>Uebernimmt die Bearbeitung, ohne dass jemand darauf wartet (Taste, Verlassen
        ''' des Felds). Eine Ausnahme dahinter finge sonst niemand.</summary>
        Public Async Sub CommitEditInBackground(item As Object)
            Try
                Await CommitEditAsync(item)
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.CommitEdit", ex)
            End Try
        End Sub

    End Class

End Namespace
