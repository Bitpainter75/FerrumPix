Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Ein Regler im Dialog "Anpassung speichern": Beschriftung und Haken.</summary>
    Public Class AdjustmentPresetEntryChoice
        Inherits ReactiveObject

        Private ReadOnly _group As AdjustmentPresetGroupChoice
        Private _isChecked As Boolean
        Private _blockReason As String

        Public ReadOnly Property Entry As AdjustmentEntry
        ''' Die Beschriftung in der Sprache der Oberflaeche.
        Public ReadOnly Property Label As String

        Friend Sub New(group As AdjustmentPresetGroupChoice, entry As AdjustmentEntry, isChecked As Boolean)
            _group = group
            Me.Entry = entry
            Label = LocalizationService.T(entry.Label)
            _isChecked = isChecked
        End Sub

        ''' <summary>Der Haken, wie gewaehlt. Er bleibt stehen, solange das Ziel den Regler nicht
        ''' tragen kann; zurueck beim Ziel, das ihn traegt, gilt er wieder.</summary>
        Public Property IsChecked As Boolean
            Get
                Return _isChecked
            End Get
            Set(value As Boolean)
                If _isChecked = value Then Return
                Me.RaiseAndSetIfChanged(_isChecked, value)
                Me.RaisePropertyChanged(NameOf(ShownChecked))
                _group.OnEntryChanged()
            End Set
        End Property

        ''' Setzt den Haken ohne Rueckmeldung an die Gruppe; sie meldet danach einmal fuer alle.
        Friend Sub SetCheckedQuietly(value As Boolean)
            If _isChecked = value Then Return
            Me.RaiseAndSetIfChanged(_isChecked, value, NameOf(IsChecked))
            Me.RaisePropertyChanged(NameOf(ShownChecked))
        End Sub

        ''' <summary>Kann das gewaehlte Ziel diesen Regler tragen? Sonst ist er ausgegraut, und
        ''' <see cref="BlockReason"/> sagt warum.</summary>
        Public ReadOnly Property IsAvailable As Boolean
            Get
                Return _blockReason Is Nothing
            End Get
        End Property

        ''' Der Satz, warum das Ziel den Regler nicht tragen kann, in der Sprache der Oberflaeche.
        Public ReadOnly Property BlockReason As String
            Get
                Return _blockReason
            End Get
        End Property

        ''' <summary>Der Haken, wie ihn das Kaestchen zeigt: ein ausgegrauter Regler ist nie
        ''' enthalten und zeigt keinen.</summary>
        Public Property ShownChecked As Boolean
            Get
                Return _isChecked AndAlso IsAvailable
            End Get
            Set(value As Boolean)
                If Not IsAvailable Then
                    Me.RaisePropertyChanged(NameOf(ShownChecked))
                    Return
                End If
                IsChecked = value
            End Set
        End Property

        ''' Setzt, ob und warum das Ziel den Regler nicht tragen kann; Nothing heisst, es kann.
        Friend Sub SetBlockReason(reason As String)
            If String.Equals(_blockReason, reason, StringComparison.Ordinal) Then Return
            _blockReason = reason
            Me.RaisePropertyChanged(NameOf(IsAvailable))
            Me.RaisePropertyChanged(NameOf(BlockReason))
            Me.RaisePropertyChanged(NameOf(ShownChecked))
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
                ' Gezaehlt wird, was das Ziel tragen kann; ausgegraute Regler zaehlen nicht.
                Dim offered = Entries.Where(Function(e) e.IsAvailable).ToList()
                Dim chosen = offered.Where(Function(e) e.IsChecked).Count()
                If chosen = 0 Then Return False
                If chosen = offered.Count Then Return True
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

        ''' Waehlt alle Regler der Gruppe oder keinen; ausgegraute bleiben, wie sie sind.
        Friend Sub SetAllEntries(value As Boolean)
            For Each entry In Entries
                If entry.IsAvailable Then entry.SetCheckedQuietly(value)
            Next
            OnEntryChanged()
        End Sub

        ''' Kann das Ziel wenigstens einen Regler der Gruppe tragen? Sonst ist ihr Haken ausgegraut.
        Public ReadOnly Property IsAvailable As Boolean
            Get
                Return Entries.Any(Function(e) e.IsAvailable)
            End Get
        End Property

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

        ''' Wie viele ihrer Regler gewaehlt sind, als "2 / 7"; gezaehlt nur, was das Ziel tragen kann.
        Public ReadOnly Property Summary As String
            Get
                Dim offered = Entries.Where(Function(e) e.IsAvailable).ToList()
                Return String.Format("{0} / {1}", offered.Where(Function(e) e.IsChecked).Count(), offered.Count)
            End Get
        End Property

        Friend Sub OnEntryChanged()
            Me.RaisePropertyChanged(NameOf(IsChecked))
            Me.RaisePropertyChanged(NameOf(Summary))
            Me.RaisePropertyChanged(NameOf(IsAvailable))
            _owner.OnSelectionChanged()
        End Sub
    End Class

    ''' <summary>Ein Ordner der Anpassungen in einer Auswahlliste: der Pfad relativ zum Wurzelordner
    ''' und seine Beschriftung. Der Wurzelordner heisst "Hauptordner". Die aufgeklappte Liste
    ''' zeigt die Ordner als eingerueckten Baum (<see cref="Name"/> mit <see cref="Indent"/>), das
    ''' geschlossene Feld und Menues den ganzen Pfad (<see cref="Label"/>).</summary>
    Public Class AdjustmentPresetFolderChoice
        Public ReadOnly Property Path As String
        ''' Der ganze Pfad: "Ordner / Unterordner".
        Public ReadOnly Property Label As String
        ''' Nur der Name des Ordners, fuer die Zeile im eingerueckten Baum.
        Public ReadOnly Property Name As String
        ''' Die Stufe im Baum: der Hauptordner und die Ordner darin stehen am Rand (0).
        Public ReadOnly Property Depth As Integer
        Public ReadOnly Property Indent As Avalonia.Thickness

        Public Sub New(path As String)
            Me.Path = AdjustmentPresetStore.NormalizeFolder(path)
            Label = If(Me.Path.Length = 0, LocalizationService.T("Hauptordner"), Me.Path.Replace("/", " / "))
            Name = If(Me.Path.Length = 0, Label, AdjustmentPresetStore.FolderName(Me.Path))
            Depth = If(Me.Path.Length = 0, 0, Me.Path.Split("/"c).Length - 1)
            Indent = New Avalonia.Thickness(Depth * AdjustmentPresetChoiceRow.IndentStep, 0, 0, 0)
        End Sub

        Public Overrides Function ToString() As String
            Return Label
        End Function

        ''' <summary>Die Auswahlliste zu einer Ordnerliste: der Wurzelordner zuerst, danach die
        ''' Ordner in der Reihenfolge des Baums, jeder vor seinen Unterordnern.</summary>
        Public Shared Function ListOf(folders As IEnumerable(Of String)) As List(Of AdjustmentPresetFolderChoice)
            Dim result As New List(Of AdjustmentPresetFolderChoice) From {New AdjustmentPresetFolderChoice("")}
            Dim known = If(folders, Enumerable.Empty(Of String)()).
                Select(Function(f) AdjustmentPresetStore.NormalizeFolder(f)).
                Where(Function(f) f.Length > 0)
            For Each folder In AdjustmentPresetStore.OrderFoldersAsTree(known)
                result.Add(New AdjustmentPresetFolderChoice(folder))
            Next
            Return result
        End Function
    End Class

    ''' <summary>Wohin "Anpassung speichern" schreibt: die eigene Anpassung im Ordner der
    ''' Anpassungen, ein XMP-Preset oder eine LUT an einem frei gewaehlten Ort.</summary>
    Public Enum AdjustmentSaveTarget
        Preset = 0
        Xmp = 1
        Lut = 2
    End Enum

    ''' <summary>Ein Ziel in der Auswahlliste "Speichern als", fertig beschriftet.</summary>
    Public Class AdjustmentSaveTargetChoice
        Public ReadOnly Property Target As AdjustmentSaveTarget
        Public ReadOnly Property Label As String

        Public Sub New(target As AdjustmentSaveTarget, label As String)
            Me.Target = target
            Me.Label = label
        End Sub

        Public Overrides Function ToString() As String
            Return Label
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
    ''' messen.
    '''
    ''' DAS ZIEL KOMMT ZULETZT ("Speichern als"): die eigene Anpassung, ein XMP-Preset oder eine
    ''' LUT. Was ein Ziel nicht tragen kann, ist ausgegraut und sagt im Kurzhinweis warum
    ''' (XmpPresetWriter.BlockReason, CubeLutWriter.BlockReason); der Haken darunter bleibt stehen
    ''' und gilt wieder, sobald das Ziel ihn traegt. XMP und LUT gehen in eine Datei an einem frei
    ''' gewaehlten Ort, vorgeschlagen im zuletzt benutzten Ordner.</summary>
    Public Class AdjustmentPresetSaveDialogViewModel
        Inherits ReactiveObject

        Private _name As String = ""
        Private _selectedFolder As AdjustmentPresetFolderChoice
        Private _selectedTarget As AdjustmentSaveTargetChoice
        Private _selectedGridSize As Integer = CubeLutWriter.DefaultGridSize
        Private ReadOnly _isRaw As Boolean
        Private ReadOnly _recipe As ImageAdjustments
        Private ReadOnly _exportFolder As String
        Private _chosenFile As String
        Private _fileConfirmedByPicker As Boolean

        Public ReadOnly Property Groups As New List(Of AdjustmentPresetGroupChoice)()
        Public ReadOnly Property SelectAllCommand As ICommand
        Public ReadOnly Property SelectNoneCommand As ICommand
        ''' Die Ordner, in die gespeichert werden kann; der Wurzelordner zuerst.
        Public ReadOnly Property Folders As List(Of AdjustmentPresetFolderChoice)
        ''' Einfuegen mit Auswahl statt Speichern: kein Name, kein Ordner.
        Public ReadOnly Property IsPaste As Boolean
        ''' Die Ziele der Liste "Speichern als".
        Public ReadOnly Property Targets As List(Of AdjustmentSaveTargetChoice)
        ''' Die waehlbaren Gitter einer LUT, Punkte je Achse.
        Public ReadOnly Property LutGridSizes As List(Of Integer) = CubeLutWriter.GridSizes.ToList()

        ''' <param name="name">Der vorgeschlagene Name.</param>
        ''' <param name="preselected">Die vorbelegten Eintraege.</param>
        ''' <param name="includeFrame">Traegt das Bild einen uebertragbaren Rahmen? Ohne ihn fehlt
        ''' die Gruppe Rahmen: es gaebe nichts zu speichern.</param>
        ''' <param name="folders">Die vorhandenen Ordner relativ zum Wurzelordner.</param>
        ''' <param name="selectedFolder">Der vorbelegte Ordner.</param>
        ''' <param name="isPaste">Der Dialog waehlt, was aus der Ablage eingefuegt wird.</param>
        ''' <param name="isRaw">Kommt das Bild aus einer RAW-Datei? Dann fehlen Belichtung und
        ''' Weissabgleich in einer LUT.</param>
        ''' <param name="recipe">Das Rezept des Bildes, fuer Eintraege, die je nach Wert in eine LUT
        ''' passen (der weiche Filter).</param>
        ''' <param name="exportFolder">Der Ordner, in dem XMP und LUT vorgeschlagen werden; leer
        ''' heisst der Bilderordner.</param>
        Public Sub New(name As String, preselected As IEnumerable(Of AdjustmentEntry), includeFrame As Boolean,
                       Optional folders As IEnumerable(Of String) = Nothing,
                       Optional selectedFolder As String = "",
                       Optional isPaste As Boolean = False,
                       Optional isRaw As Boolean = False,
                       Optional recipe As ImageAdjustments = Nothing,
                       Optional exportFolder As String = "")
            _isRaw = isRaw
            _recipe = recipe
            _exportFolder = DefaultExportFolder(exportFolder)
            Targets = New List(Of AdjustmentSaveTargetChoice) From {
                New AdjustmentSaveTargetChoice(AdjustmentSaveTarget.Preset, LocalizationService.T("FerrumPix-Anpassung")),
                New AdjustmentSaveTargetChoice(AdjustmentSaveTarget.Xmp, LocalizationService.T("XMP-Preset (.xmp)")),
                New AdjustmentSaveTargetChoice(AdjustmentSaveTarget.Lut, LocalizationService.T("LUT (.cube)"))}
            _selectedTarget = Targets(0)
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
                ' Der vorgeschlagene Dateiname folgt dem Namen, bis eine Datei gewaehlt ist.
                If String.IsNullOrWhiteSpace(_chosenFile) Then Me.RaisePropertyChanged(NameOf(TargetFilePath))
            End Set
        End Property

        ''' <summary>Die gewaehlten Eintraege in der Reihenfolge der Tabelle, ohne die, die das Ziel
        ''' nicht tragen kann.</summary>
        Public Function SelectedEntries() As List(Of AdjustmentEntry)
            Return Groups.SelectMany(Function(g) g.Entries).Where(Function(e) e.IsChecked AndAlso e.IsAvailable).
                Select(Function(e) e.Entry).ToList()
        End Function

        ''' <summary>Ist jeder angebotene Regler gewaehlt? Dann traegt die Vorlage keine Liste und
        ''' enthaelt alles, wie eine Vorlage von frueher.</summary>
        Public ReadOnly Property IsEverythingSelected As Boolean
            Get
                Return Groups.SelectMany(Function(g) g.Entries).Where(Function(e) e.IsAvailable).All(Function(e) e.IsChecked)
            End Get
        End Property

        Public ReadOnly Property HasSelection As Boolean
            Get
                Return Groups.SelectMany(Function(g) g.Entries).Any(Function(e) e.IsChecked AndAlso e.IsAvailable)
            End Get
        End Property

        ''' <summary>Das gewaehlte Ziel in der Liste "Speichern als". Ein Wechsel graut aus, was das
        ''' neue Ziel nicht tragen kann.</summary>
        Public Property SelectedTarget As AdjustmentSaveTargetChoice
            Get
                Return _selectedTarget
            End Get
            Set(value As AdjustmentSaveTargetChoice)
                ' Die Liste meldet beim Neuaufbau kurz Nothing; das Ziel bleibt dann stehen.
                If value Is Nothing OrElse value Is _selectedTarget Then Return
                Me.RaiseAndSetIfChanged(_selectedTarget, value)
                ApplyTargetToEntries()
                For Each propertyName In {NameOf(Target), NameOf(ShowsFolder), NameOf(ShowsFile), NameOf(ShowsGridSize),
                                          NameOf(TargetHint), NameOf(ShowsTargetHint), NameOf(TargetFilePath), NameOf(CanConfirm)}
                    Me.RaisePropertyChanged(propertyName)
                Next
            End Set
        End Property

        Public ReadOnly Property Target As AdjustmentSaveTarget
            Get
                Return If(_selectedTarget?.Target, AdjustmentSaveTarget.Preset)
            End Get
        End Property

        ''' <summary>Waehlt das Ziel ohne die Liste, fuer den Pruefstand und fuer Aufrufer ohne
        ''' Fenster.</summary>
        Public Sub SelectTarget(wanted As AdjustmentSaveTarget)
            SelectedTarget = Targets.First(Function(t) t.Target = wanted)
        End Sub

        ''' <summary>Setzt je Regler, ob und warum das Ziel ihn nicht tragen kann.</summary>
        Private Sub ApplyTargetToEntries()
            For Each group In Groups
                For Each choice In group.Entries
                    Dim reason As String = Nothing
                    Select Case Target
                        Case AdjustmentSaveTarget.Xmp
                            reason = XmpPresetWriter.BlockReason(choice.Entry)
                        Case AdjustmentSaveTarget.Lut
                            reason = CubeLutWriter.BlockReason(choice.Entry, _isRaw, _recipe)
                    End Select
                    choice.SetBlockReason(reason)
                Next
                group.OnEntryChanged()
            Next
        End Sub

        ''' Die Liste der Ziele gibt es nur beim Speichern, nicht beim Einfuegen.
        Public ReadOnly Property ShowsTargetChoice As Boolean
            Get
                Return Not IsPaste
            End Get
        End Property

        ''' XMP und LUT gehen in eine Datei an einem frei gewaehlten Ort.
        Public ReadOnly Property ShowsFile As Boolean
            Get
                Return Not IsPaste AndAlso Target <> AdjustmentSaveTarget.Preset
            End Get
        End Property

        Public ReadOnly Property ShowsGridSize As Boolean
            Get
                Return Not IsPaste AndAlso Target = AdjustmentSaveTarget.Lut
            End Get
        End Property

        ''' <summary>Die Gitterpunkte je Achse der LUT; 33 ab Werk.</summary>
        Public Property SelectedGridSize As Integer
            Get
                Return _selectedGridSize
            End Get
            Set(value As Integer)
                ' Die Liste meldet beim Neuaufbau kurz 0; das Gitter bleibt dann stehen.
                If Not CubeLutWriter.GridSizes.Contains(value) Then Return
                Me.RaiseAndSetIfChanged(_selectedGridSize, value)
            End Set
        End Property

        ''' <summary>Was das Ziel kann und was nicht, als kurzer Hinweis unter der Liste.</summary>
        Public ReadOnly Property TargetHint As String
            Get
                Select Case Target
                    Case AdjustmentSaveTarget.Xmp
                        Return LocalizationService.T("Das XMP-Preset lesen auch andere Entwickler. Regler ohne Gegenstück stehen darin in einem eigenen Bereich, den nur FerrumPix liest.")
                    Case AdjustmentSaveTarget.Lut
                        Dim hint = LocalizationService.T("Eine LUT trägt nur Regler, die jeden Bildpunkt für sich ändern. Lichter, Tiefen und Weiß richten sich nach der Helligkeit dieses Bildes, die LUT passt deshalb am besten zu ähnlichen Bildern.")
                        If _isRaw Then hint &= " " & LocalizationService.T("Belichtung und Weißabgleich wirken bei einer RAW beim Entwickeln und fehlen in der LUT.")
                        Return hint
                End Select
                Return ""
            End Get
        End Property

        Public ReadOnly Property ShowsTargetHint As Boolean
            Get
                Return Not IsPaste AndAlso Not String.IsNullOrEmpty(TargetHint)
            End Get
        End Property

        ''' <summary>Die Datei fuer XMP und LUT. Bis eine gewaehlt oder eingetippt ist, der Name im
        ''' zuletzt benutzten Ordner; die Endung folgt dem Ziel.</summary>
        Public Property TargetFilePath As String
            Get
                Dim extension = If(Target = AdjustmentSaveTarget.Lut, CubeLutWriter.FileExtension, XmpPresetWriter.FileExtension)
                If Not String.IsNullOrWhiteSpace(_chosenFile) Then
                    Return System.IO.Path.ChangeExtension(_chosenFile.Trim(), extension)
                End If
                Return System.IO.Path.Combine(_exportFolder, SafeFileName(_name) & extension)
            End Get
            Set(value As String)
                If String.Equals(value, TargetFilePath, StringComparison.Ordinal) Then Return
                _chosenFile = value
                _fileConfirmedByPicker = False
                Me.RaisePropertyChanged(NameOf(TargetFilePath))
                Me.RaisePropertyChanged(NameOf(CanConfirm))
            End Set
        End Property

        ''' <summary>Hat der Dateidialog die Datei bestaetigt, samt der Frage nach einer
        ''' vorhandenen? Dann fragt das Speichern nicht noch einmal.</summary>
        Public ReadOnly Property FileConfirmedByPicker As Boolean
            Get
                Return _fileConfirmedByPicker AndAlso Not String.IsNullOrWhiteSpace(_chosenFile)
            End Get
        End Property

        ''' <summary>Die Datei aus dem Dateidialog.</summary>
        Public Sub ChooseFile(path As String)
            If String.IsNullOrWhiteSpace(path) Then Return
            _chosenFile = path
            _fileConfirmedByPicker = True
            Me.RaisePropertyChanged(NameOf(TargetFilePath))
            Me.RaisePropertyChanged(NameOf(CanConfirm))
        End Sub

        ''' Der Ordner, in dem der Dateidialog beginnt.
        Public ReadOnly Property TargetFolder As String
            Get
                Dim folder = System.IO.Path.GetDirectoryName(TargetFilePath)
                Return If(String.IsNullOrWhiteSpace(folder), _exportFolder, folder)
            End Get
        End Property

        ''' <summary>Der Ordner fuer XMP und LUT: der zuletzt benutzte, sonst der Bilderordner,
        ''' sonst der Benutzerordner.</summary>
        Private Shared Function DefaultExportFolder(lastFolder As String) As String
            If Not String.IsNullOrWhiteSpace(lastFolder) Then Return lastFolder
            Dim pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            If Not String.IsNullOrWhiteSpace(pictures) Then Return pictures
            Return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        End Function

        ''' <summary>Der Name als Dateiname: Zeichen, die kein Dateisystem traegt, werden zum
        ''' Unterstrich; leer heisst "FerrumPix".</summary>
        Friend Shared Function SafeFileName(name As String) As String
            Dim invalid = System.IO.Path.GetInvalidFileNameChars().Concat({"/"c, "\"c, ":"c, "*"c, "?"c, """"c, "<"c, ">"c, "|"c}).ToHashSet()
            Dim cleaned = New String(If(name, "").Trim().Select(Function(c) If(invalid.Contains(c), "_"c, c)).ToArray()).Trim("."c, " "c)
            Return If(cleaned.Length = 0, "FerrumPix", cleaned)
        End Function

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

        ''' Die Ordnerwahl erscheint erst, wenn es ausser dem Wurzelordner einen gibt, und nur fuer
        ''' die eigene Anpassung.
        Public ReadOnly Property ShowsFolder As Boolean
            Get
                Return Not IsPaste AndAlso Target = AdjustmentSaveTarget.Preset AndAlso Folders.Count > 1
            End Get
        End Property

        ''' Ohne Namen oder ohne einen einzigen Haken gibt es nichts zu speichern; beim Einfuegen
        ''' genuegt ein Haken. XMP und LUT brauchen dazu eine Datei.
        Public ReadOnly Property CanConfirm As Boolean
            Get
                Dim needsFile = Not IsPaste AndAlso Target <> AdjustmentSaveTarget.Preset
                Return (IsPaste OrElse Not String.IsNullOrWhiteSpace(_name)) AndAlso HasSelection AndAlso
                       (Not needsFile OrElse Not String.IsNullOrWhiteSpace(TargetFilePath))
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
