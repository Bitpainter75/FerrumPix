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
        Private _isDragSource As Boolean

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

        ''' Der Ordner wird gerade gezogen.
        Public Property IsDragSource As Boolean
            Get
                Return _isDragSource
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isDragSource, value)
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

    ''' <summary>Eine Anpassung in der Liste der Verwaltung. Wie die Ordnerzeile traegt sie ihren
    ''' Zustand selbst: im Umbenennen, vor dem Loeschen, mit ihrem Vorschaubild.</summary>
    Public Class AdjustmentPresetRow
        Inherits ReactiveObject

        Private _isEditing As Boolean
        Private _editText As String = ""
        Private _rowMessage As String = ""
        Private _isConfirmingDelete As Boolean
        Private _isDragSource As Boolean
        Private _canApply As Boolean
        Private _showsThumbnail As Boolean
        Private _thumbnail As Avalonia.Media.Imaging.Bitmap

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
                       Optional canMove As Boolean = True, Optional canApply As Boolean = False,
                       Optional showsThumbnail As Boolean = False)
            Me.Preset = preset
            Name = preset.Name
            Summary = AdjustmentEntries.Describe(preset.Fields).Replace(Environment.NewLine, "; ")
            FolderLabel = New AdjustmentPresetFolderChoice(preset.Folder).Label
            SecondLine = If(showFolder, FolderLabel, Summary)
            Me.CanMove = canMove
            _canApply = canApply
            _showsThumbnail = showsThumbnail
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

        ''' <summary>Der Papierkorb ist einmal geklickt und steht als "Löschen?" da; der zweite
        ''' Klick auf dieselbe Stelle loescht. Die Zeile sieht sonst aus wie immer.</summary>
        Public Property IsConfirmingDelete As Boolean
            Get
                Return _isConfirmingDelete
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isConfirmingDelete, value)
                Me.RaisePropertyChanged(NameOf(ShowsDeleteIcon))
            End Set
        End Property

        ''' Der Papierkorb zeigt sein Symbol, solange er nicht fragt.
        Public ReadOnly Property ShowsDeleteIcon As Boolean
            Get
                Return Not _isConfirmingDelete
            End Get
        End Property

        ''' Name, zweite Zeile und Knoepfe: der gewoehnliche Zustand der Zeile. Er gilt auch,
        ''' waehrend der Papierkorb fragt.
        Public ReadOnly Property ShowsName As Boolean
            Get
                Return Not _isEditing
            End Get
        End Property

        ''' <summary>Hat die Zeile links einen Platz fuer ihr Vorschaubild? Nur mit einem
        ''' geoeffneten Bild; ohne gibt es weder Bild noch leeren Platz.</summary>
        Public Property ShowsThumbnail As Boolean
            Get
                Return _showsThumbnail
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_showsThumbnail, value)
            End Set
        End Property

        ''' <summary>Das geoeffnete Foto klein, mit dieser Anpassung. Nothing, solange es rechnet:
        ''' dann steht der ruhige Platzhalter da. Das Bild gehoert der Verwaltung, sie gibt es
        ''' frei (AdjustmentPresetManagerViewModel.Release).</summary>
        Public Property Thumbnail As Avalonia.Media.Imaging.Bitmap
            Get
                Return _thumbnail
            End Get
            Friend Set(value As Avalonia.Media.Imaging.Bitmap)
                Me.RaiseAndSetIfChanged(_thumbnail, value)
                Me.RaisePropertyChanged(NameOf(HasThumbnail))
            End Set
        End Property

        Public ReadOnly Property HasThumbnail As Boolean
            Get
                Return _thumbnail IsNot Nothing
            End Get
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

        ' Die Aufschriften kommen uebersetzt aus der Zeile: sie entsteht aus einer Vorlage, nach
        ' dem Durchlauf der Uebersetzung.
        Public ReadOnly Property ApplyText As String
            Get
                Return LocalizationService.T("Anwenden")
            End Get
        End Property

        ''' Die Aufschrift des Papierkorbs nach dem ersten Klick.
        Public ReadOnly Property DeletePrompt As String
            Get
                Return LocalizationService.T("Löschen?")
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
    ''' Ordner und Suche, links der Ordnerbaum, rechts die Anpassungen.
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
    ''' Pfade und Zeilen herein; so laesst sich alles ohne Fenster messen.
    '''
    ''' WAS DAS GEOEFFNETE BILD BRAUCHT, KOMMT VOM EDITOR ALS WEG HEREIN und fehlt ohne Bild:
    ''' Anwenden (<see cref="ApplyHandler"/>), das Vorschaubild je Zeile
    ''' (<see cref="ThumbnailHandler"/>) und die Vorschau im grossen Bild beim Ueberfahren
    ''' (<see cref="HoverPreviewHandler"/>). Die Verwaltung selbst kennt weder Bild noch
    ''' Editor. Wer das Fenster zeigt, ruft danach <see cref="Release"/>.</summary>
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
        Private _draggedPreset As AdjustmentPresetRow
        Private _draggedFolder As AdjustmentPresetFolderNode
        Private _dropTarget As AdjustmentPresetFolderNode
        ''' Welche Seite zuletzt gewaehlt wurde: an sie gehen F2 und ENTF.
        Private _presetIsActive As Boolean
        Private _committing As Boolean
        Private _applyHandler As Func(Of AdjustmentPresetFile, Boolean)
        Private _thumbnailHandler As Func(Of IReadOnlyList(Of AdjustmentPresetFile), Action(Of AdjustmentPresetFile, Avalonia.Media.Imaging.Bitmap), Threading.CancellationToken, Task)
        Private _thumbnailCts As Threading.CancellationTokenSource
        Private _hoverPreviewHandler As Action(Of AdjustmentPresetFile)
        Private _hoveredPreset As AdjustmentPresetRow
        Private _released As Boolean
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

        ''' <summary>Der Hinweis, wenn der Ordner der Anpassungen fehlt (er liegt im Datenordner,
        ''' und der ist gerade nicht erreichbar). Derselbe Wortlaut im Editor und in der Verwaltung.</summary>
        Public Shared Function UnreachableMessage(root As String) As String
            Return String.Format(LocalizationService.T("Der Ordner für Anpassungen ist nicht erreichbar: {0}."), If(root, ""))
        End Function

        ''' Was Verschieben getan hat; eine Zeile unter den Listen.
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
        ''' gewaehlte Ordner, die gewaehlte Anpassung und eine offene Rueckfrage bleiben, soweit es ihre Zeilen noch gibt.</summary>
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
            Me.RaisePropertyChanged(NameOf(ShowsThumbnails))

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
            Return AdjustmentPresetStore.OrderFoldersAsTree(folders)
        End Function

        Private Sub FillPresets(selectPresetId As String)
            ' Die Zeilen entstehen neu: eine Bearbeitung, Rueckfrage oder ein Zug an einer alten
            ' Zeile gilt nicht mehr. Bliebe die Rueckfrage stehen, fragte eine Zeile, die niemand
            ' mehr sieht.
            _editingPreset = Nothing
            _confirmPreset = Nothing
            CancelDrag()
            ' Die Vorschau im grossen Bild gehoerte einer Zeile, die es gleich nicht mehr gibt.
            EndHoverPreview()
            ' Die Vorschaubilder der bisherigen Zeilen: was unveraendert wieder erscheint, behaelt
            ' seines, alles andere wird unten freigegeben. Ein laufender Durchgang bricht ab.
            StopThumbnails()
            Dim kept As New Dictionary(Of String, Avalonia.Media.Imaging.Bitmap)(StringComparer.Ordinal)
            For Each old In Presets
                Dim image = old.Thumbnail
                If image Is Nothing Then Continue For
                old.Thumbnail = Nothing
                Dim key = ThumbnailKey(old.Preset)
                If kept.ContainsKey(key) Then image.Dispose() Else kept(key) = image
            Next
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
                Dim showsThumbnails = Me.ShowsThumbnails
                For Each preset In source.OrderBy(Function(p) p.Name, StringComparer.CurrentCultureIgnoreCase).
                                          ThenBy(Function(p) p.Folder, StringComparer.OrdinalIgnoreCase)
                    Dim row As New AdjustmentPresetRow(preset, showFolder, canMove, canApply, showsThumbnails)
                    Dim image As Avalonia.Media.Imaging.Bitmap = Nothing
                    If showsThumbnails AndAlso kept.TryGetValue(ThumbnailKey(preset), image) Then
                        kept.Remove(ThumbnailKey(preset))
                        row.Thumbnail = image
                    End If
                    Presets.Add(row)
                Next
            End If
            For Each leftover In kept.Values
                leftover.Dispose()
            Next
            StartThumbnails()
            _selectedPreset = Presets.FirstOrDefault(Function(r) String.Equals(r.Preset.Id, selectPresetId, StringComparison.Ordinal))
            Me.RaisePropertyChanged(NameOf(SelectedPreset))
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
            _draggedFolder = Nothing
            _dropTarget = Nothing
        End Sub

        ''' <summary>Beendet jede offene Bearbeitung und Rueckfrage, ohne etwas zu aendern, und
        ''' nimmt die Meldungen von den Zeilen. Vor jeder neuen Aktion: hoechstens eine Zeile ist
        ''' in einem besonderen Zustand.</summary>
        Private Sub ClearRowStates()
            CancelEdit()
            CancelDelete()
            CancelDrag()
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

        ''' <summary>Der erste Klick auf den Papierkorb einer Anpassung (und ENTF): der Papierkorb
        ''' wird zu "Löschen?". Geloescht ist damit nichts; das tut erst der zweite Klick auf
        ''' dieselbe Stelle (<see cref="ClickDeletePresetAsync"/>). Ein Klick woanders, ESC und das
        ''' Verlassen der Zeile nehmen es zurueck.</summary>
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

        ''' <summary>Der Klick auf den Papierkorb einer Anpassung. Der erste macht ihn zu
        ''' "Löschen?", der zweite auf dieselbe Zeile loescht. Fragt gerade eine ANDERE Zeile,
        ''' ist das dort zurueckgenommen und diese fragt.</summary>
        ''' <returns>Ob geloescht wurde.</returns>
        Public Async Function ClickDeletePresetAsync(row As AdjustmentPresetRow) As Task(Of Boolean)
            If Not _isReachable OrElse row Is Nothing OrElse Not Presets.Contains(row) Then Return False
            If _confirmPreset Is row Then Return Await ConfirmDeleteAsync()
            BeginDeletePreset(row)
            Return False
        End Function

        ''' <summary>Nimmt das "Löschen?" am Papierkorb einer Anpassung zurueck: ein Klick
        ''' woanders. Die ausfuehrliche Rueckfrage eines ORDNERS bleibt stehen, sie hat ihren
        ''' eigenen Knopf Abbrechen.</summary>
        Public Sub CancelPresetDeleteConfirm()
            If _confirmPreset Is Nothing Then Return
            _confirmPreset.IsConfirmingDelete = False
            _confirmPreset = Nothing
        End Sub

        ' ── Der Zeiger ueber einer Zeile: Vorschau im grossen Bild ──────────────────────────

        ''' <summary>Der Weg, auf dem das grosse Bild im Editor eine Anpassung vorab zeigt; Nothing
        ''' als Anpassung beendet die Vorschau. Der Editor setzt ihn, wenn ein Bild offen ist. Am
        ''' Rezept aendert die Vorschau nichts.</summary>
        Public Property HoverPreviewHandler As Action(Of AdjustmentPresetFile)
            Get
                Return _hoverPreviewHandler
            End Get
            Set(value As Action(Of AdjustmentPresetFile))
                EndHoverPreview()
                _hoverPreviewHandler = value
            End Set
        End Property

        ''' Die Zeile, deren Anpassung gerade vorab im grossen Bild steht.
        Public ReadOnly Property HoveredPreset As AdjustmentPresetRow
            Get
                Return _hoveredPreset
            End Get
        End Property

        ''' <summary>Der Zeiger steht auf der Zeile einer Anpassung: das grosse Bild zeigt sie
        ''' vorab. Nicht waehrend eines Zugs und nicht an einer Zeile, die umbenannt wird oder
        ''' fragt.</summary>
        Public Sub HoverPreset(row As AdjustmentPresetRow)
            If _hoverPreviewHandler Is Nothing OrElse _released Then Return
            If row Is Nothing OrElse Not Presets.Contains(row) OrElse Not row.ShowsName OrElse IsDragging Then
                EndHoverPreview()
                Return
            End If
            If row Is _hoveredPreset Then Return
            _hoveredPreset = row
            _hoverPreviewHandler(row.Preset)
        End Sub

        ''' <summary>Der Zeiger hat die Zeile verlassen: die Vorschau im grossen Bild endet, und
        ''' ein "Löschen?" an ihrem Papierkorb ist zurueckgenommen.</summary>
        Public Sub LeavePresetRow(row As AdjustmentPresetRow)
            If row Is Nothing Then Return
            If _confirmPreset Is row Then CancelPresetDeleteConfirm()
            If _hoveredPreset Is row Then EndHoverPreview()
        End Sub

        ''' <summary>Beendet die Vorschau im grossen Bild, falls eine steht.</summary>
        Public Sub EndHoverPreview()
            If _hoveredPreset Is Nothing Then Return
            _hoveredPreset = Nothing
            _hoverPreviewHandler?.Invoke(Nothing)
        End Sub

        ' ── Vorschaubild je Zeile ───────────────────────────────────────────────────────────

        ''' <summary>Der Weg, auf dem die Vorschaubilder entstehen: er bekommt die Anpassungen der
        ''' Liste, liefert je Anpassung ein Bild auf dem Anzeigefaden ab und hoert auf, wenn die
        ''' Marke abbricht. Der Editor setzt ihn, wenn ein Bild offen ist; ohne ihn haben die
        ''' Zeilen weder Bild noch Platz dafuer.</summary>
        Public Property ThumbnailHandler As Func(Of IReadOnlyList(Of AdjustmentPresetFile), Action(Of AdjustmentPresetFile, Avalonia.Media.Imaging.Bitmap), Threading.CancellationToken, Task)
            Get
                Return _thumbnailHandler
            End Get
            Set(value As Func(Of IReadOnlyList(Of AdjustmentPresetFile), Action(Of AdjustmentPresetFile, Avalonia.Media.Imaging.Bitmap), Threading.CancellationToken, Task))
                StopThumbnails()
                _thumbnailHandler = value
                Me.RaisePropertyChanged(NameOf(ShowsThumbnails))
                Dim shows = ShowsThumbnails
                For Each row In Presets
                    row.ShowsThumbnail = shows
                    If Not shows Then ReleaseThumbnail(row)
                Next
                StartThumbnails()
            End Set
        End Property

        Public ReadOnly Property ShowsThumbnails As Boolean
            Get
                Return _isReachable AndAlso _thumbnailHandler IsNot Nothing AndAlso Not _released
            End Get
        End Property

        ''' Woran ein Vorschaubild haengt: an der Anpassung und an dem, was sie einstellt.
        Private Shared Function ThumbnailKey(preset As AdjustmentPresetFile) As String
            Return String.Concat(preset.Id, vbLf, If(preset.Fields Is Nothing, "*", String.Join(",", preset.Fields)), vbLf, preset.RecipeJson)
        End Function

        Private Shared Sub ReleaseThumbnail(row As AdjustmentPresetRow)
            Dim image = row.Thumbnail
            If image Is Nothing Then Return
            row.Thumbnail = Nothing
            image.Dispose()
        End Sub

        ''' <summary>Bricht einen laufenden Durchgang ab. Die schon gezeigten Bilder bleiben.</summary>
        Private Sub StopThumbnails()
            Dim cts = _thumbnailCts
            _thumbnailCts = Nothing
            If cts Is Nothing Then Return
            Try
                cts.Cancel()
            Catch ex As ObjectDisposedException
            End Try
        End Sub

        ''' <summary>Laesst die Vorschaubilder der Zeilen rechnen, die noch keines haben: genau die
        ''' Zeilen der Liste, also der gewaehlte Ordner oder die Suchtreffer. Ein Durchgang zur
        ''' Zeit; jeder Wechsel der Liste bricht den laufenden ab.</summary>
        Private Sub StartThumbnails()
            StopThumbnails()
            If Not ShowsThumbnails Then Return
            Dim missing = Presets.Where(Function(r) r.Thumbnail Is Nothing).Select(Function(r) r.Preset).ToList()
            If missing.Count = 0 Then Return
            Dim cts As New Threading.CancellationTokenSource()
            _thumbnailCts = cts
            Dim token = cts.Token
            Dim handler = _thumbnailHandler
            Dim deliver As Action(Of AdjustmentPresetFile, Avalonia.Media.Imaging.Bitmap) =
                Sub(preset, image)
                    If image Is Nothing Then Return
                    Dim row = If(token.IsCancellationRequested, Nothing,
                                 Presets.FirstOrDefault(Function(r) r.Preset Is preset))
                    If row Is Nothing Then
                        ' Die Liste hat gewechselt: das Bild gehoert niemandem mehr.
                        image.Dispose()
                        Return
                    End If
                    Dim old = row.Thumbnail
                    row.Thumbnail = image
                    old?.Dispose()
                End Sub
            RunThumbnails(handler, missing, deliver, token)
        End Sub

        ' Async Sub: auf die Vorschaubilder wartet niemand, eine Ausnahme dahinter finge sonst
        ' niemand.
        Private Async Sub RunThumbnails(handler As Func(Of IReadOnlyList(Of AdjustmentPresetFile), Action(Of AdjustmentPresetFile, Avalonia.Media.Imaging.Bitmap), Threading.CancellationToken, Task),
                                        presets As List(Of AdjustmentPresetFile),
                                        deliver As Action(Of AdjustmentPresetFile, Avalonia.Media.Imaging.Bitmap),
                                        token As Threading.CancellationToken)
            Try
                Await handler(presets, deliver, token)
            Catch ex As OperationCanceledException
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.Thumbnails", ex)
            End Try
        End Sub

        ''' <summary>Verwirft alle Vorschaubilder und laesst sie neu rechnen: das Rezept des
        ''' geoeffneten Bildes hat sich geaendert (Anwenden), jedes Bild zeigte den Stand davor.</summary>
        Private Sub RefreshThumbnails()
            StopThumbnails()
            For Each row In Presets
                ReleaseThumbnail(row)
            Next
            StartThumbnails()
        End Sub

        ''' <summary>Das Fenster schliesst: ein laufender Durchgang bricht ab, die Vorschaubilder
        ''' werden freigegeben, und das grosse Bild zeigt wieder das Rezept. Danach rechnet und
        ''' zeigt die Verwaltung nichts mehr vorab.</summary>
        Public Sub Release()
            _released = True
            StopThumbnails()
            EndHoverPreview()
            CancelDrag()
            For Each row In Presets
                ReleaseThumbnail(row)
                row.ShowsThumbnail = False
            Next
            Me.RaisePropertyChanged(NameOf(ShowsThumbnails))
        End Sub

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

        ''' Der Ordner, der gerade gezogen wird.
        Public ReadOnly Property DraggedFolder As AdjustmentPresetFolderNode
            Get
                Return _draggedFolder
            End Get
        End Property

        ''' Laeuft gerade ein Zug, mit einer Anpassung oder einem Ordner?
        Public ReadOnly Property IsDragging As Boolean
            Get
                Return _draggedPreset IsNot Nothing OrElse _draggedFolder IsNot Nothing
            End Get
        End Property

        ''' <summary>Ein Zug mit einer Anpassung beginnt. Nicht aus einer Zeile, die gerade
        ''' umbenannt wird oder fragt, ob sie ueberschrieben werden soll.</summary>
        Public Function BeginPresetDrag(row As AdjustmentPresetRow) As Boolean
            If Not _isReachable OrElse row Is Nothing OrElse Not Presets.Contains(row) OrElse
               Not row.ShowsName OrElse row.IsConfirmingDelete Then Return False
            ClearRowStates()
            EndHoverPreview()
            _draggedPreset = row
            row.IsDragSource = True
            Return True
        End Function

        ''' <summary>Ein Zug mit einem Ordner beginnt. Nicht mit der Wurzel, nicht mit der
        ''' vorlaeufigen Zeile eines neuen Ordners und nicht aus einer Zeile, die gerade umbenannt
        ''' wird oder fragt.</summary>
        Public Function BeginFolderDrag(node As AdjustmentPresetFolderNode) As Boolean
            If Not _isReachable OrElse node Is Nothing OrElse Not node.CanChange OrElse
               Not Folders.Contains(node) OrElse Not node.ShowsName Then Return False
            ClearRowStates()
            EndHoverPreview()
            _draggedFolder = node
            node.IsDragSource = True
            Return True
        End Function

        ''' <summary>Kann, was gerade gezogen wird, auf diesem Ordner abgelegt werden? Eine
        ''' Anpassung nicht auf dem Ordner, in dem sie liegt. Ein Ordner nicht auf sich selbst,
        ''' nicht auf einem seiner Unterordner und nicht auf dem Ordner, in dem er schon liegt.</summary>
        Public Function CanDropOn(node As AdjustmentPresetFolderNode) As Boolean
            If node Is Nothing OrElse node.IsNew Then Return False
            If _draggedPreset IsNot Nothing Then
                Return Not String.Equals(node.Path, AdjustmentPresetStore.NormalizeFolder(_draggedPreset.Preset.Folder), StringComparison.Ordinal)
            End If
            If _draggedFolder IsNot Nothing Then Return CanMoveFolderTo(_draggedFolder.Path, node.Path)
            Return False
        End Function

        ''' <summary>Laesst sich der Ordner <paramref name="folder"/> unter
        ''' <paramref name="targetParent"/> haengen? Nicht in sich selbst, nicht in einen eigenen
        ''' Unterordner, und dorthin, wo er schon liegt, waere es kein Verschieben.</summary>
        Public Shared Function CanMoveFolderTo(folder As String, targetParent As String) As Boolean
            Dim source = AdjustmentPresetStore.NormalizeFolder(folder)
            Dim target = AdjustmentPresetStore.NormalizeFolder(targetParent)
            If source.Length = 0 Then Return False
            If String.Equals(target, source, StringComparison.Ordinal) Then Return False
            If target.StartsWith(source & "/", StringComparison.Ordinal) Then Return False
            Return Not String.Equals(target, AdjustmentPresetStore.ParentFolder(source), StringComparison.Ordinal)
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
        Public Sub CancelDrag()
            If _dropTarget IsNot Nothing Then _dropTarget.IsDropTarget = False
            If _draggedPreset IsNot Nothing Then _draggedPreset.IsDragSource = False
            If _draggedFolder IsNot Nothing Then _draggedFolder.IsDragSource = False
            _dropTarget = Nothing
            _draggedPreset = Nothing
            _draggedFolder = Nothing
        End Sub

        ''' <summary>Der Zug endet mit dem Loslassen: steht der Zeiger ueber einem erlaubten
        ''' Ordner, wird die Anpassung oder der Ordner dorthin verschoben, sonst geschieht
        ''' nichts.</summary>
        ''' <returns>Ob verschoben wurde.</returns>
        Public Async Function CompleteDragAsync() As Task(Of Boolean)
            Dim row = _draggedPreset
            Dim folder = _draggedFolder
            Dim target = _dropTarget
            CancelDrag()
            If target Is Nothing Then Return False
            If row IsNot Nothing Then Return Await MovePresetAsync(row, target.Path)
            If folder IsNot Nothing Then Return Await MoveFolderAsync(folder, target.Path)
            Return False
        End Function

        ''' <summary>Haengt einen Ordner samt Inhalt unter einen anderen. Nicht in sich selbst
        ''' oder einen eigenen Unterordner, und dorthin, wo er schon liegt, geschieht nichts. Liegt
        ''' im Ziel schon ein Ordner gleichen Namens, steht die Meldung an der Zeile und nichts
        ''' wird bewegt. Die Anpassungen darin behalten ihre Kennungen; war der Ordner oder einer
        ''' darin gewaehlt, ist er es danach an seinem neuen Platz.</summary>
        ''' <returns>Ob verschoben wurde.</returns>
        Public Async Function MoveFolderAsync(node As AdjustmentPresetFolderNode, targetParent As String) As Task(Of Boolean)
            If Not _isReachable OrElse node Is Nothing OrElse Not node.CanChange OrElse Not Folders.Contains(node) Then Return False
            Dim source = node.Path
            Dim target = AdjustmentPresetStore.NormalizeFolder(targetParent)
            If Not CanMoveFolderTo(source, target) Then Return False
            ClearRowStates()
            Dim store = Me.Store()
            Dim destination = AdjustmentPresetStore.CombineFolder(target, AdjustmentPresetStore.FolderName(source))
            Dim moved As String = Nothing
            If Not _listing.Folders.Any(Function(f) String.Equals(f, destination, StringComparison.OrdinalIgnoreCase)) Then
                moved = Await Task.Run(Function() store.MoveFolder(source, target))
            End If
            If moved Is Nothing Then
                node.RowMessage = LocalizationService.T("Der Ordner ließ sich nicht verschieben. Gibt es dort schon einen gleichen Namens?")
                Return False
            End If
            ' Was zugeklappt war, bleibt es an seinem neuen Platz.
            Dim prefix = source & "/"
            For Each collapsed In _collapsed.Where(Function(c) String.Equals(c, source, StringComparison.Ordinal) OrElse
                                                                c.StartsWith(prefix, StringComparison.Ordinal)).ToList()
                _collapsed.Remove(collapsed)
                _collapsed.Add(moved & collapsed.Substring(source.Length))
            Next
            ' Der gewaehlte Ordner kann der verschobene sein oder darin liegen.
            Dim selected = SelectedFolderPath
            Dim selectFolder = selected
            If String.Equals(selected, source, StringComparison.Ordinal) OrElse selected.StartsWith(prefix, StringComparison.Ordinal) Then
                selectFolder = moved & selected.Substring(source.Length)
            End If
            Await ReloadAsync(selectFolder)
            StatusText = String.Format(LocalizationService.T("Ordner verschoben: {0}"), New AdjustmentPresetFolderChoice(moved).Label)
            Return True
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
            ' Die Vorschau im grossen Bild endet: ab hier steht die Anpassung wirklich im Rezept.
            EndHoverPreview()
            Dim applied = _applyHandler(row.Preset)
            row.RowMessage = If(applied, LocalizationService.T("Angewendet"),
                                LocalizationService.T("Die Anpassung konnte nicht gelesen werden."))
            ' Jedes Vorschaubild zeigte die Anpassung auf dem Rezept von eben.
            If applied Then RefreshThumbnails()
            Return applied
        End Function

        ' ── Zeigen, Ordner aendern ──────────────────────────────────────────────────────────
        ' Einen Import und einen Export gibt es im Fenster nicht: Dateien legt man ueber den
        ' Dateimanager in die Ablage und holt sie dort heraus.

        ''' <summary>Oeffnet die Ablage im Dateimanager.</summary>
        Public Sub ShowInFileManager()
            If Not _isReachable Then Return
            ShellOpenService.Open(Store().DirectoryOf(""), "AdjustmentPresets.Folder")
        End Sub

        ''' <summary>True, nachdem "Einstellungen öffnen" (bei nicht erreichbarem Ordner) gewaehlt wurde: wer
        ''' das Fenster gezeigt hat, wechselt nach dem Schliessen in die Einstellungen.</summary>
        Public ReadOnly Property SettingsRequested As Boolean
            Get
                Return _settingsRequested
            End Get
        End Property

        ''' <summary>"Einstellungen öffnen" beim nicht erreichbaren Ordner: das Fenster schliesst, danach geht es in die Einstellungen,
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
                    If IsDragging Then
                        CancelDrag()
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
