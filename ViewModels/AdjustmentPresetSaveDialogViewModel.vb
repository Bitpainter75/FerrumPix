Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Ein Regler im Dialog "Anpassung speichern": Beschriftung und Haken.</summary>
    Public Class AdjustmentPresetEntryChoice
        Inherits ReactiveObject

        Private ReadOnly _group As AdjustmentPresetGroupChoice
        Private _isChecked As Boolean

        Public ReadOnly Property Entry As AdjustmentEntry
        ''' Die Beschriftung in der Sprache der Oberflaeche.
        Public ReadOnly Property Label As String

        Friend Sub New(group As AdjustmentPresetGroupChoice, entry As AdjustmentEntry, isChecked As Boolean)
            _group = group
            Me.Entry = entry
            Label = LocalizationService.T(entry.Label)
            _isChecked = isChecked
        End Sub

        Public Property IsChecked As Boolean
            Get
                Return _isChecked
            End Get
            Set(value As Boolean)
                If _isChecked = value Then Return
                Me.RaiseAndSetIfChanged(_isChecked, value)
                _group.OnEntryChanged()
            End Set
        End Property

        ''' Setzt den Haken ohne Rueckmeldung an die Gruppe; sie meldet danach einmal fuer alle.
        Friend Sub SetCheckedQuietly(value As Boolean)
            If _isChecked = value Then Return
            Me.RaiseAndSetIfChanged(_isChecked, value, NameOf(IsChecked))
        End Sub
    End Class

    ''' <summary>Eine Reglergruppe im Dialog "Anpassung speichern": zum Aufklappen, mit einem Haken
    ''' fuer alle ihre Regler.</summary>
    Public Class AdjustmentPresetGroupChoice
        Inherits ReactiveObject

        Private ReadOnly _owner As AdjustmentPresetSaveDialogViewModel
        Private _isExpanded As Boolean

        Public ReadOnly Property Key As String
        ''' Der Name der Gruppe in der Sprache der Oberflaeche.
        Public ReadOnly Property Label As String
        Public ReadOnly Property Entries As New List(Of AdjustmentPresetEntryChoice)()

        ''' Klappt die Regler der Gruppe auf oder zu.
        Public ReadOnly Property ToggleExpandedCommand As ICommand

        Friend Sub New(owner As AdjustmentPresetSaveDialogViewModel, key As String)
            _owner = owner
            Me.Key = key
            Label = LocalizationService.T(AdjustmentEntries.GroupLabel(key))
            ToggleExpandedCommand = ReactiveCommand.Create(Sub() IsExpanded = Not IsExpanded)
        End Sub

        ''' <summary>Der Haken der Gruppe: True, wenn alle ihre Regler gewaehlt sind, False bei
        ''' keinem, Nothing als Zwischenstand bei einem Teil. Setzen waehlt alle oder keinen.
        '''
        ''' AUS DEM ZWISCHENSTAND GEHT ES ZU "ALLE": das Kaestchen selbst wuerde beim Klick auf den
        ''' Zwischenstand False melden; wer eine halb gewaehlte Gruppe anklickt, will sie ganz.</summary>
        Public Property IsChecked As Boolean?
            Get
                Dim chosen = Entries.Where(Function(e) e.IsChecked).Count()
                If chosen = 0 Then Return False
                If chosen = Entries.Count Then Return True
                Return Nothing
            End Get
            Set(value As Boolean?)
                Dim current = IsChecked
                Dim target = value.GetValueOrDefault(True)
                If Not current.HasValue Then target = True
                SetAllEntries(target)
                ' Weicht der Stand von dem ab, was das Kaestchen gemeldet hat, liest es ihn nach
                ' seinem eigenen Schreiben noch einmal.
                If Not value.HasValue OrElse value.Value <> target Then
                    Avalonia.Threading.Dispatcher.UIThread.Post(Sub() Me.RaisePropertyChanged(NameOf(IsChecked)))
                End If
            End Set
        End Property

        ''' Waehlt alle Regler der Gruppe oder keinen.
        Friend Sub SetAllEntries(value As Boolean)
            For Each entry In Entries
                entry.SetCheckedQuietly(value)
            Next
            OnEntryChanged()
        End Sub

        ''' Aufgeklappt zeigt die Gruppe ihre Regler.
        Public Property IsExpanded As Boolean
            Get
                Return _isExpanded
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isExpanded, value)
            End Set
        End Property

        ''' Hat die Gruppe mehr als einen Regler? Sonst gibt es nichts aufzuklappen.
        Public ReadOnly Property CanExpand As Boolean
            Get
                Return Entries.Count > 1
            End Get
        End Property

        ''' Wie viele ihrer Regler gewaehlt sind, als "2 / 7".
        Public ReadOnly Property Summary As String
            Get
                Return String.Format("{0} / {1}", Entries.Where(Function(e) e.IsChecked).Count(), Entries.Count)
            End Get
        End Property

        Friend Sub OnEntryChanged()
            Me.RaisePropertyChanged(NameOf(IsChecked))
            Me.RaisePropertyChanged(NameOf(Summary))
            _owner.OnSelectionChanged()
        End Sub
    End Class

    ''' <summary>Ein Ordner der Anpassungen in einer Auswahlliste: der Pfad relativ zum Wurzelordner
    ''' und seine Beschriftung. Der Wurzelordner heisst "Hauptordner".</summary>
    Public Class AdjustmentPresetFolderChoice
        Public ReadOnly Property Path As String
        Public ReadOnly Property Label As String

        Public Sub New(path As String)
            Me.Path = AdjustmentPresetStore.NormalizeFolder(path)
            Label = If(Me.Path.Length = 0, LocalizationService.T("Hauptordner"), Me.Path.Replace("/", " / "))
        End Sub

        Public Overrides Function ToString() As String
            Return Label
        End Function

        ''' <summary>Die Auswahlliste zu einer Ordnerliste: der Wurzelordner zuerst.</summary>
        Public Shared Function ListOf(folders As IEnumerable(Of String)) As List(Of AdjustmentPresetFolderChoice)
            Dim result As New List(Of AdjustmentPresetFolderChoice) From {New AdjustmentPresetFolderChoice("")}
            For Each folder In If(folders, Enumerable.Empty(Of String)()).
                Select(Function(f) AdjustmentPresetStore.NormalizeFolder(f)).
                Where(Function(f) f.Length > 0).Distinct(StringComparer.Ordinal).
                OrderBy(Function(f) f, StringComparer.OrdinalIgnoreCase)
                result.Add(New AdjustmentPresetFolderChoice(folder))
            Next
            Return result
        End Function
    End Class

    ''' <summary>Der Inhalt des Dialogs "Anpassung speichern": der Name, der Ordner und je
    ''' Reglergruppe des Editors ein Haken, darunter aufklappbar jeder Regler einzeln
    ''' (AdjustmentEntries). Vorbelegt sind die Regler, die im Bild vom Neutralwert abweichen.
    '''
    ''' DERSELBE DIALOG FUER "EINFUEGEN MIT AUSWAHL" (<see cref="IsPaste"/>): dann ohne Name und
    ''' Ordner, vorbelegt mit dem, was in der Ablage vom Neutralwert abweicht.
    '''
    ''' Der Dialog kennt weder das Bild noch den Bestand: der Editor baut ihn aus dem Rezept
    ''' (EditorViewModel.CreateAdjustmentPresetSaveDialog) und speichert nach dem Bestaetigen, was
    ''' hier gewaehlt ist (SaveAdjustmentPresetFromDialogAsync). So laesst sich beides ohne Fenster
    ''' messen.</summary>
    Public Class AdjustmentPresetSaveDialogViewModel
        Inherits ReactiveObject

        Private _name As String = ""
        Private _selectedFolder As AdjustmentPresetFolderChoice

        Public ReadOnly Property Groups As New List(Of AdjustmentPresetGroupChoice)()
        Public ReadOnly Property SelectAllCommand As ICommand
        Public ReadOnly Property SelectNoneCommand As ICommand
        ''' Die Ordner, in die gespeichert werden kann; der Wurzelordner zuerst.
        Public ReadOnly Property Folders As List(Of AdjustmentPresetFolderChoice)
        ''' Einfuegen mit Auswahl statt Speichern: kein Name, kein Ordner.
        Public ReadOnly Property IsPaste As Boolean

        ''' <param name="name">Der vorgeschlagene Name.</param>
        ''' <param name="preselected">Die vorbelegten Eintraege.</param>
        ''' <param name="includeFrame">Traegt das Bild einen uebertragbaren Rahmen? Ohne ihn fehlt
        ''' die Gruppe Rahmen: es gaebe nichts zu speichern.</param>
        ''' <param name="folders">Die vorhandenen Ordner relativ zum Wurzelordner.</param>
        ''' <param name="selectedFolder">Der vorbelegte Ordner.</param>
        ''' <param name="isPaste">Der Dialog waehlt, was aus der Ablage eingefuegt wird.</param>
        Public Sub New(name As String, preselected As IEnumerable(Of AdjustmentEntry), includeFrame As Boolean,
                       Optional folders As IEnumerable(Of String) = Nothing,
                       Optional selectedFolder As String = "",
                       Optional isPaste As Boolean = False)
            Me.IsPaste = isPaste
            Me.Folders = AdjustmentPresetFolderChoice.ListOf(folders)
            Dim wantedFolder = AdjustmentPresetStore.NormalizeFolder(selectedFolder)
            _selectedFolder = If(Me.Folders.FirstOrDefault(Function(f) String.Equals(f.Path, wantedFolder, StringComparison.Ordinal)),
                                 Me.Folders(0))
            _name = If(name, "")
            Dim chosen As New HashSet(Of String)(
                If(preselected, Enumerable.Empty(Of AdjustmentEntry)()).Select(Function(e) e.Key), StringComparer.Ordinal)
            For Each groupKey In AdjustmentEntries.GroupKeys()
                Dim group As New AdjustmentPresetGroupChoice(Me, groupKey)
                For Each entry In AdjustmentEntries.EntriesOfGroup(groupKey)
                    If entry.IsFrame AndAlso Not includeFrame Then Continue For
                    group.Entries.Add(New AdjustmentPresetEntryChoice(group, entry, chosen.Contains(entry.Key)))
                Next
                If group.Entries.Count > 0 Then Groups.Add(group)
            Next
            SelectAllCommand = ReactiveCommand.Create(Sub() SetAll(True))
            SelectNoneCommand = ReactiveCommand.Create(Sub() SetAll(False))
        End Sub

        Public Property Name As String
            Get
                Return _name
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_name, If(value, ""))
                Me.RaisePropertyChanged(NameOf(CanConfirm))
            End Set
        End Property

        ''' <summary>Die gewaehlten Eintraege in der Reihenfolge der Tabelle.</summary>
        Public Function SelectedEntries() As List(Of AdjustmentEntry)
            Return Groups.SelectMany(Function(g) g.Entries).Where(Function(e) e.IsChecked).Select(Function(e) e.Entry).ToList()
        End Function

        ''' <summary>Ist jeder angebotene Regler gewaehlt? Dann traegt die Vorlage keine Liste und
        ''' enthaelt alles, wie eine Vorlage von frueher.</summary>
        Public ReadOnly Property IsEverythingSelected As Boolean
            Get
                Return Groups.SelectMany(Function(g) g.Entries).All(Function(e) e.IsChecked)
            End Get
        End Property

        Public ReadOnly Property HasSelection As Boolean
            Get
                Return Groups.SelectMany(Function(g) g.Entries).Any(Function(e) e.IsChecked)
            End Get
        End Property

        ''' <summary>Der Ordner, in den gespeichert wird.</summary>
        Public Property SelectedFolder As AdjustmentPresetFolderChoice
            Get
                Return _selectedFolder
            End Get
            Set(value As AdjustmentPresetFolderChoice)
                ' Die Liste meldet beim Neuaufbau kurz Nothing; der Ordner bleibt dann stehen.
                If value Is Nothing Then Return
                Me.RaiseAndSetIfChanged(_selectedFolder, value)
            End Set
        End Property

        ''' <summary>Der gewaehlte Ordner relativ zum Wurzelordner; leer ist der Wurzelordner.</summary>
        Public ReadOnly Property FolderPath As String
            Get
                Return If(_selectedFolder?.Path, "")
            End Get
        End Property

        ''' Name und Ordner gibt es nur beim Speichern.
        Public ReadOnly Property ShowsName As Boolean
            Get
                Return Not IsPaste
            End Get
        End Property

        ''' Die Ordnerwahl erscheint erst, wenn es ausser dem Wurzelordner einen gibt.
        Public ReadOnly Property ShowsFolder As Boolean
            Get
                Return Not IsPaste AndAlso Folders.Count > 1
            End Get
        End Property

        ''' Ohne Namen oder ohne einen einzigen Haken gibt es nichts zu speichern; beim Einfuegen
        ''' genuegt ein Haken.
        Public ReadOnly Property CanConfirm As Boolean
            Get
                Return (IsPaste OrElse Not String.IsNullOrWhiteSpace(_name)) AndAlso HasSelection
            End Get
        End Property

        Public Sub SetAll(value As Boolean)
            For Each group In Groups
                group.SetAllEntries(value)
            Next
        End Sub

        Friend Sub OnSelectionChanged()
            Me.RaisePropertyChanged(NameOf(HasSelection))
            Me.RaisePropertyChanged(NameOf(IsEverythingSelected))
            Me.RaisePropertyChanged(NameOf(CanConfirm))
        End Sub

    End Class

End Namespace
