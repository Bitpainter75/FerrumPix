Imports System.Collections.ObjectModel
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Ein Ordner im Baum der Verwaltung: Pfad, Name und Einrueckung nach seiner Tiefe.</summary>
    Public Class AdjustmentPresetFolderNode
        ''' Der Pfad relativ zum Wurzelordner; leer ist der Wurzelordner.
        Public ReadOnly Property Path As String
        ''' Der Name des Ordners, beim Wurzelordner "Hauptordner".
        Public ReadOnly Property Name As String
        ''' Wie viele Anpassungen unmittelbar darin liegen.
        Public ReadOnly Property Count As Integer
        Public ReadOnly Property Indent As Avalonia.Thickness

        Public Sub New(path As String, count As Integer)
            Me.Path = AdjustmentPresetStore.NormalizeFolder(path)
            Me.Count = count
            Name = If(Me.Path.Length = 0, LocalizationService.T("Hauptordner"), AdjustmentPresetStore.FolderName(Me.Path))
            Dim depth = If(Me.Path.Length = 0, 0, Me.Path.Split("/"c).Length)
            Indent = New Avalonia.Thickness(depth * 16, 0, 0, 0)
        End Sub
    End Class

    ''' <summary>Eine Anpassung in der Liste der Verwaltung: ihr Name und was sie enthaelt.</summary>
    Public Class AdjustmentPresetRow
        Public ReadOnly Property Preset As AdjustmentPresetFile
        Public ReadOnly Property Name As String
        ''' Was die Anpassung enthaelt, in einer Zeile (AdjustmentEntries.Describe).
        Public ReadOnly Property Summary As String

        Public Sub New(preset As AdjustmentPresetFile)
            Me.Preset = preset
            Name = preset.Name
            Summary = AdjustmentEntries.Describe(preset.Fields).Replace(Environment.NewLine, "; ")
        End Sub
    End Class

    ''' <summary>Was die Verwaltung gerade fragt, bevor sie es tut.</summary>
    Public Enum AdjustmentPresetPendingKind
        None
        NewFolder
        RenameFolder
        RenamePreset
        MoveFolder
        MovePreset
        DeleteFolder
        DeletePreset
    End Enum

    ''' <summary>Der Inhalt des Fensters "Anpassungen verwalten": links der Ordnerbaum, rechts die
    ''' Anpassungen des gewaehlten Ordners mit dem, was sie enthalten. Ordner anlegen, Umbenennen,
    ''' Verschieben, Loeschen, Importieren, Exportieren, im Dateimanager zeigen.
    '''
    ''' NAMEN, ZIELE UND RUECKFRAGEN STEHEN IM FENSTER SELBST (<see cref="PendingKind"/>): die
    ''' Anwendung zeigt nur einen Dialog zur Zeit, eine zweite Frage wuerde dieses Fenster
    ''' ersetzen. Eine Aktion wird erst vorgemerkt und laeuft mit
    ''' <see cref="ConfirmPendingAsync"/>.
    '''
    ''' JEDER ZUGRIFF AUF DEN ORDNER LAEUFT IM HINTERGRUND; der Ordner kann in einer entfernten
    ''' Ablage liegen. Die Dateiauswahl fuer Import und Export oeffnet die Ansicht und reicht den
    ''' Pfad herein (<see cref="ImportAsync"/>, <see cref="ExportSelectedPresetAsync"/>,
    ''' <see cref="ExportSelectedFolderAsync"/>), so laesst sich alles ohne Fenster messen.</summary>
    Public Class AdjustmentPresetManagerViewModel
        Inherits ReactiveObject

        Private ReadOnly _fixedStore As AdjustmentPresetStore
        Private _listing As New AdjustmentPresetListing()
        Private _selectedFolder As AdjustmentPresetFolderNode
        Private _selectedPreset As AdjustmentPresetRow
        Private _statusText As String = ""
        Private _isReachable As Boolean = True
        Private _pendingKind As AdjustmentPresetPendingKind = AdjustmentPresetPendingKind.None
        Private _pendingText As String = ""
        Private _pendingPrompt As String = ""
        Private _pendingTarget As AdjustmentPresetFolderChoice
        Private _pendingFolderPath As String = ""
        Private _pendingPreset As AdjustmentPresetFile

        Public ReadOnly Property Folders As New ObservableCollection(Of AdjustmentPresetFolderNode)()
        Public ReadOnly Property Presets As New ObservableCollection(Of AdjustmentPresetRow)()
        ''' Die Ziele beim Verschieben.
        Public ReadOnly Property MoveTargets As New ObservableCollection(Of AdjustmentPresetFolderChoice)()

        Public ReadOnly Property NewFolderCommand As ICommand
        Public ReadOnly Property RenameFolderCommand As ICommand
        Public ReadOnly Property MoveFolderCommand As ICommand
        Public ReadOnly Property DeleteFolderCommand As ICommand
        Public ReadOnly Property RenamePresetCommand As ICommand
        Public ReadOnly Property MovePresetCommand As ICommand
        Public ReadOnly Property DeletePresetCommand As ICommand
        Public ReadOnly Property ConfirmPendingCommand As ICommand
        Public ReadOnly Property CancelPendingCommand As ICommand
        Public ReadOnly Property ShowInFileManagerCommand As ICommand

        ''' <param name="store">Ein fester Bestand (Pruefstand); ohne ihn gilt der Ordner aus den
        ''' Einstellungen.</param>
        Public Sub New(Optional store As AdjustmentPresetStore = Nothing)
            _fixedStore = store
            NewFolderCommand = ReactiveCommand.Create(Sub() BeginNewFolder())
            RenameFolderCommand = ReactiveCommand.Create(Sub() BeginRenameFolder())
            MoveFolderCommand = ReactiveCommand.Create(Sub() BeginMoveFolder())
            DeleteFolderCommand = ReactiveCommand.Create(Sub() BeginDeleteFolder())
            RenamePresetCommand = ReactiveCommand.Create(Sub() BeginRenamePreset())
            MovePresetCommand = ReactiveCommand.Create(Sub() BeginMovePreset())
            DeletePresetCommand = ReactiveCommand.Create(Sub() BeginDeletePreset())
            ConfirmPendingCommand = ReactiveCommand.CreateFromTask(Function() ConfirmPendingAsync())
            CancelPendingCommand = ReactiveCommand.Create(Sub() CancelPending())
            ShowInFileManagerCommand = ReactiveCommand.Create(Sub() ShowInFileManager())
        End Sub

        Private Function Store() As AdjustmentPresetStore
            Return If(_fixedStore, AdjustmentPresetStore.Current())
        End Function

        ' ── Stand ───────────────────────────────────────────────────────────────────────────

        ''' <summary>Der Ordner, in dem die Anpassungen liegen.</summary>
        Public ReadOnly Property RootPath As String
            Get
                Return _listing.Root
            End Get
        End Property

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

        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_statusText, If(value, ""))
            End Set
        End Property

        Public Property SelectedFolder As AdjustmentPresetFolderNode
            Get
                Return _selectedFolder
            End Get
            Set(value As AdjustmentPresetFolderNode)
                ' Die Liste meldet beim Neuaufbau kurz Nothing.
                If value Is Nothing OrElse value Is _selectedFolder Then Return
                Me.RaiseAndSetIfChanged(_selectedFolder, value)
                FillPresets(Nothing)
                RaiseSelectionState()
            End Set
        End Property

        Public Property SelectedPreset As AdjustmentPresetRow
            Get
                Return _selectedPreset
            End Get
            Set(value As AdjustmentPresetRow)
                Me.RaiseAndSetIfChanged(_selectedPreset, value)
                RaiseSelectionState()
            End Set
        End Property

        ''' Der gewaehlte Ordner relativ zum Wurzelordner.
        Public ReadOnly Property SelectedFolderPath As String
            Get
                Return If(_selectedFolder?.Path, "")
            End Get
        End Property

        ''' Der Wurzelordner laesst sich weder umbenennen noch verschieben noch loeschen.
        Public ReadOnly Property CanChangeFolder As Boolean
            Get
                Return _isReachable AndAlso SelectedFolderPath.Length > 0
            End Get
        End Property

        Public ReadOnly Property HasSelectedPreset As Boolean
            Get
                Return _isReachable AndAlso _selectedPreset IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property HasNoPresets As Boolean
            Get
                Return _isReachable AndAlso Presets.Count = 0
            End Get
        End Property

        Private Sub RaiseSelectionState()
            Me.RaisePropertyChanged(NameOf(SelectedFolderPath))
            Me.RaisePropertyChanged(NameOf(CanChangeFolder))
            Me.RaisePropertyChanged(NameOf(HasSelectedPreset))
            Me.RaisePropertyChanged(NameOf(HasNoPresets))
        End Sub

        ''' <summary>Liest den Ordner im Hintergrund neu und baut Baum und Liste auf. Der
        ''' gewaehlte Ordner und die gewaehlte Anpassung bleiben, soweit es sie noch gibt.</summary>
        ''' <param name="selectFolder">Der Ordner, der danach gewaehlt sein soll.</param>
        ''' <param name="selectPresetId">Die Anpassung, die danach gewaehlt sein soll.</param>
        Public Async Function ReloadAsync(Optional selectFolder As String = Nothing,
                                          Optional selectPresetId As String = Nothing) As Task
            Dim wantedFolder = If(selectFolder, SelectedFolderPath)
            Dim wantedPreset = If(selectPresetId, _selectedPreset?.Preset.Id)
            Dim fixedStore = _fixedStore
            Dim listing = Await Task.Run(Function() If(fixedStore IsNot Nothing, fixedStore.ReadAll(), AdjustmentPresetStore.LoadCurrent()))
            _listing = listing
            Me.RaisePropertyChanged(NameOf(RootPath))
            IsReachable = listing.Reachable

            Folders.Clear()
            If listing.Reachable Then
                Folders.Add(New AdjustmentPresetFolderNode("", listing.PresetsIn("").Count))
                For Each folder In listing.Folders.OrderBy(Function(f) f, StringComparer.OrdinalIgnoreCase)
                    Folders.Add(New AdjustmentPresetFolderNode(folder, listing.PresetsIn(folder).Count))
                Next
            End If
            Dim normalized = AdjustmentPresetStore.NormalizeFolder(wantedFolder)
            _selectedFolder = If(Folders.FirstOrDefault(Function(f) String.Equals(f.Path, normalized, StringComparison.Ordinal)),
                                 Folders.FirstOrDefault())
            Me.RaisePropertyChanged(NameOf(SelectedFolder))
            FillPresets(wantedPreset)
            RaiseSelectionState()
        End Function

        Private Sub FillPresets(selectPresetId As String)
            Presets.Clear()
            If _isReachable AndAlso _selectedFolder IsNot Nothing Then
                For Each preset In _listing.PresetsIn(_selectedFolder.Path)
                    Presets.Add(New AdjustmentPresetRow(preset))
                Next
            End If
            _selectedPreset = Presets.FirstOrDefault(Function(r) String.Equals(r.Preset.Id, selectPresetId, StringComparison.Ordinal))
            Me.RaisePropertyChanged(NameOf(SelectedPreset))
        End Sub

        ' ── Die vorgemerkte Aktion ──────────────────────────────────────────────────────────

        Public ReadOnly Property PendingKind As AdjustmentPresetPendingKind
            Get
                Return _pendingKind
            End Get
        End Property

        Public ReadOnly Property HasPending As Boolean
            Get
                Return _pendingKind <> AdjustmentPresetPendingKind.None
            End Get
        End Property

        ''' Anlegen und Umbenennen fragen nach einem Namen.
        Public ReadOnly Property IsPendingInput As Boolean
            Get
                Return _pendingKind = AdjustmentPresetPendingKind.NewFolder OrElse
                       _pendingKind = AdjustmentPresetPendingKind.RenameFolder OrElse
                       _pendingKind = AdjustmentPresetPendingKind.RenamePreset
            End Get
        End Property

        ''' Verschieben fragt nach dem Zielordner.
        Public ReadOnly Property IsPendingMove As Boolean
            Get
                Return _pendingKind = AdjustmentPresetPendingKind.MoveFolder OrElse
                       _pendingKind = AdjustmentPresetPendingKind.MovePreset
            End Get
        End Property

        ''' <summary>Was gefragt wird: die Zeile ueber Eingabefeld, Zielwahl oder Rueckfrage.</summary>
        Public ReadOnly Property PendingPrompt As String
            Get
                Return _pendingPrompt
            End Get
        End Property

        ''' <summary>Die Beschriftung des Knopfs, der die vorgemerkte Aktion ausfuehrt.</summary>
        Public ReadOnly Property PendingConfirmText As String
            Get
                Select Case _pendingKind
                    Case AdjustmentPresetPendingKind.NewFolder : Return LocalizationService.T("Anlegen")
                    Case AdjustmentPresetPendingKind.RenameFolder, AdjustmentPresetPendingKind.RenamePreset : Return LocalizationService.T("Umbenennen")
                    Case AdjustmentPresetPendingKind.MoveFolder, AdjustmentPresetPendingKind.MovePreset : Return LocalizationService.T("Verschieben")
                    Case Else : Return LocalizationService.T("Löschen")
                End Select
            End Get
        End Property

        Public Property PendingText As String
            Get
                Return _pendingText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_pendingText, If(value, ""))
            End Set
        End Property

        Public Property PendingTarget As AdjustmentPresetFolderChoice
            Get
                Return _pendingTarget
            End Get
            Set(value As AdjustmentPresetFolderChoice)
                If value Is Nothing Then Return
                Me.RaiseAndSetIfChanged(_pendingTarget, value)
            End Set
        End Property

        Private Sub SetPending(kind As AdjustmentPresetPendingKind, prompt As String, Optional text As String = "")
            _pendingKind = kind
            _pendingPrompt = If(prompt, "")
            _pendingFolderPath = SelectedFolderPath
            _pendingPreset = _selectedPreset?.Preset
            PendingText = text
            StatusText = ""
            For Each propertyName In {NameOf(PendingKind), NameOf(HasPending), NameOf(IsPendingInput),
                                      NameOf(IsPendingMove), NameOf(PendingPrompt), NameOf(PendingConfirmText)}
                Me.RaisePropertyChanged(propertyName)
            Next
        End Sub

        Public Sub CancelPending()
            SetPending(AdjustmentPresetPendingKind.None, "")
        End Sub

        Public Sub BeginNewFolder()
            If Not _isReachable Then Return
            SetPending(AdjustmentPresetPendingKind.NewFolder,
                       String.Format(LocalizationService.T("Neuer Ordner in {0}"), FolderLabel(SelectedFolderPath)))
        End Sub

        Public Sub BeginRenameFolder()
            If Not CanChangeFolder Then Return
            SetPending(AdjustmentPresetPendingKind.RenameFolder, LocalizationService.T("Neuer Name des Ordners"),
                       AdjustmentPresetStore.FolderName(SelectedFolderPath))
        End Sub

        Public Sub BeginRenamePreset()
            If Not HasSelectedPreset Then Return
            SetPending(AdjustmentPresetPendingKind.RenamePreset, LocalizationService.T("Neuer Name der Anpassung"),
                       _selectedPreset.Name)
        End Sub

        Public Sub BeginMoveFolder()
            If Not CanChangeFolder Then Return
            Dim source = SelectedFolderPath
            ' Nicht in sich selbst, nicht in einen eigenen Unterordner, nicht dorthin, wo er liegt.
            FillMoveTargets(Function(f) Not String.Equals(f, source, StringComparison.Ordinal) AndAlso
                                        Not f.StartsWith(source & "/", StringComparison.Ordinal) AndAlso
                                        Not String.Equals(f, AdjustmentPresetStore.ParentFolder(source), StringComparison.Ordinal))
            If MoveTargets.Count = 0 Then
                StatusText = LocalizationService.T("Es gibt keinen anderen Ordner als Ziel.")
                Return
            End If
            SetPending(AdjustmentPresetPendingKind.MoveFolder,
                       String.Format(LocalizationService.T("Ordner {0} verschieben nach"), FolderLabel(source)))
        End Sub

        Public Sub BeginMovePreset()
            If Not HasSelectedPreset Then Return
            Dim source = _selectedPreset.Preset.Folder
            FillMoveTargets(Function(f) Not String.Equals(f, source, StringComparison.Ordinal))
            If MoveTargets.Count = 0 Then
                StatusText = LocalizationService.T("Es gibt keinen anderen Ordner als Ziel.")
                Return
            End If
            SetPending(AdjustmentPresetPendingKind.MovePreset,
                       String.Format(LocalizationService.T("Anpassung {0} verschieben nach"), _selectedPreset.Name))
        End Sub

        Public Sub BeginDeleteFolder()
            If Not CanChangeFolder Then Return
            Dim folder = SelectedFolderPath
            Dim count = _listing.Presets.Where(
                Function(p) String.Equals(p.Folder, folder, StringComparison.Ordinal) OrElse
                            p.Folder.StartsWith(folder & "/", StringComparison.Ordinal)).Count()
            SetPending(AdjustmentPresetPendingKind.DeleteFolder,
                       String.Format(LocalizationService.T("Der Ordner {0} wird mit {1} Anpassung(en) gelöscht. Andere Dateien darin bleiben erhalten."),
                                     FolderLabel(folder), count))
        End Sub

        Public Sub BeginDeletePreset()
            If Not HasSelectedPreset Then Return
            SetPending(AdjustmentPresetPendingKind.DeletePreset,
                       String.Format(LocalizationService.T("Die Anpassung {0} wird gelöscht."), _selectedPreset.Name))
        End Sub

        Private Sub FillMoveTargets(allowed As Func(Of String, Boolean))
            MoveTargets.Clear()
            For Each choice In AdjustmentPresetFolderChoice.ListOf(_listing.Folders)
                If allowed(choice.Path) Then MoveTargets.Add(choice)
            Next
            _pendingTarget = MoveTargets.FirstOrDefault()
            Me.RaisePropertyChanged(NameOf(PendingTarget))
        End Sub

        Private Shared Function FolderLabel(folder As String) As String
            Return New AdjustmentPresetFolderChoice(folder).Label
        End Function

        ''' <summary>Fuehrt die vorgemerkte Aktion aus und liest den Ordner neu. Was nicht ging,
        ''' steht danach in <see cref="StatusText"/>, und die Frage bleibt stehen.</summary>
        Public Async Function ConfirmPendingAsync() As Task
            Dim kind = _pendingKind
            If kind = AdjustmentPresetPendingKind.None Then Return
            Dim store = Me.Store()
            Dim folder = _pendingFolderPath
            Dim preset = _pendingPreset
            Dim text = If(_pendingText, "").Trim()
            Dim target = If(_pendingTarget?.Path, "")
            Dim selectFolder As String = folder
            Dim selectPreset As String = preset?.Id
            Dim failure As String = Nothing

            Select Case kind
                Case AdjustmentPresetPendingKind.NewFolder
                    If text.Length = 0 Then
                        failure = LocalizationService.T("Bitte einen Namen eingeben.")
                    Else
                        Dim created = Await Task.Run(Function() store.CreateFolder(folder, text))
                        If created Is Nothing Then
                            failure = LocalizationService.T("Der Ordner ließ sich nicht anlegen. Gibt es ihn schon?")
                        Else
                            selectFolder = created
                        End If
                    End If
                Case AdjustmentPresetPendingKind.RenameFolder
                    If text.Length = 0 Then
                        failure = LocalizationService.T("Bitte einen Namen eingeben.")
                    Else
                        Dim renamed = Await Task.Run(Function() store.RenameFolder(folder, text))
                        If renamed Is Nothing Then
                            failure = LocalizationService.T("Der Ordner ließ sich nicht umbenennen. Gibt es den Namen schon?")
                        Else
                            selectFolder = renamed
                        End If
                    End If
                Case AdjustmentPresetPendingKind.RenamePreset
                    If text.Length = 0 Then
                        failure = LocalizationService.T("Bitte einen Namen eingeben.")
                    ElseIf preset Is Nothing OrElse Not Await Task.Run(Function() store.RenamePreset(preset, text)) Then
                        failure = LocalizationService.T("Die Anpassung ließ sich nicht umbenennen. Gibt es den Namen in diesem Ordner schon?")
                    End If
                Case AdjustmentPresetPendingKind.MoveFolder
                    Dim moved = Await Task.Run(Function() store.MoveFolder(folder, target))
                    If moved Is Nothing Then
                        failure = LocalizationService.T("Der Ordner ließ sich nicht verschieben. Gibt es dort schon einen gleichen Namens?")
                    Else
                        selectFolder = moved
                    End If
                Case AdjustmentPresetPendingKind.MovePreset
                    If preset Is Nothing OrElse Not Await Task.Run(Function() store.MovePreset(preset, target)) Then
                        failure = LocalizationService.T("Die Anpassung ließ sich nicht verschieben. Gibt es dort schon eine gleichen Namens?")
                    Else
                        selectFolder = target
                    End If
                Case AdjustmentPresetPendingKind.DeleteFolder
                    Dim deleted = Await Task.Run(Function() store.DeleteFolder(folder))
                    If deleted < 0 Then
                        failure = LocalizationService.T("Der Ordner ließ sich nicht löschen.")
                    Else
                        selectFolder = AdjustmentPresetStore.ParentFolder(folder)
                    End If
                Case AdjustmentPresetPendingKind.DeletePreset
                    If preset Is Nothing OrElse Not Await Task.Run(Function() store.DeletePreset(preset)) Then
                        failure = LocalizationService.T("Die Anpassung ließ sich nicht löschen.")
                    End If
            End Select

            If failure IsNot Nothing Then
                StatusText = failure
                Return
            End If
            CancelPending()
            Await ReloadAsync(selectFolder, selectPreset)
        End Function

        ' ── Importieren, Exportieren, Zeigen ────────────────────────────────────────────────

        ''' <summary>Nimmt eine Datei oder einen Ordner samt Unterordnern in den gewaehlten Ordner
        ''' auf und sagt danach, was geschah.</summary>
        Public Async Function ImportAsync(sourcePath As String) As Task
            If String.IsNullOrWhiteSpace(sourcePath) OrElse Not _isReachable Then Return
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

        ''' <summary>Schreibt die gewaehlte Anpassung als Datei an den gewaehlten Ort.</summary>
        Public Async Function ExportSelectedPresetAsync(targetFile As String) As Task
            Dim preset = _selectedPreset?.Preset
            If preset Is Nothing OrElse String.IsNullOrWhiteSpace(targetFile) Then Return
            Dim store = Me.Store()
            Dim written = Await Task.Run(Function() store.ExportPreset(preset, targetFile))
            StatusText = If(written,
                            String.Format(LocalizationService.T("Anpassung exportiert: {0}"), preset.Name),
                            LocalizationService.T("Die Anpassung ließ sich nicht exportieren."))
        End Function

        ''' <summary>Schreibt den gewaehlten Ordner samt Unterordnern in ein Verzeichnis.</summary>
        Public Async Function ExportSelectedFolderAsync(targetDirectory As String) As Task
            If String.IsNullOrWhiteSpace(targetDirectory) OrElse Not _isReachable Then Return
            Dim store = Me.Store()
            Dim folder = SelectedFolderPath
            Dim written = Await Task.Run(Function() store.ExportFolder(folder, targetDirectory))
            StatusText = String.Format(LocalizationService.T("{0} Anpassung(en) exportiert"), written)
        End Function

        ''' <summary>Der Dateiname, den der Export der gewaehlten Anpassung vorschlaegt.</summary>
        Public ReadOnly Property SuggestedExportFileName As String
            Get
                Dim preset = _selectedPreset?.Preset
                If preset Is Nothing Then Return ""
                Return AdjustmentPresetStore.SafeFileName(preset.Name) & AdjustmentPresetStore.FileExtension
            End Get
        End Property

        ''' <summary>Oeffnet den gewaehlten Ordner im Dateimanager.</summary>
        Public Sub ShowInFileManager()
            If Not _isReachable Then Return
            ShellOpenService.Open(Store().DirectoryOf(SelectedFolderPath), "AdjustmentPresets.Folder")
        End Sub

    End Class

End Namespace
