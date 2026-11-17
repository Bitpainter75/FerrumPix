Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Linq
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Ein Bedienelement einer Erweiterung im Panel: Regler oder Schalter, mit dem Wert,
    ''' den das Bild gerade traegt. Jede Aenderung geht an das Panel der Erweiterung, das sie ins
    ''' Rezept schreibt.</summary>
    Public NotInheritable Class ExtensionControlViewModel
        Inherits ReactiveObject

        Private ReadOnly _owner As ExtensionPanelViewModel
        Private _value As Double

        Friend Sub New(owner As ExtensionPanelViewModel, control As ExtensionControl, value As Double)
            _owner = owner
            Me.Control = control
            _value = control.Clamp(value)
        End Sub

        Public ReadOnly Property Control As ExtensionControl

        Public ReadOnly Property Key As String
            Get
                Return Control.Key
            End Get
        End Property

        Public ReadOnly Property Label As String
            Get
                Return Control.Label
            End Get
        End Property

        Public ReadOnly Property Section As String
            Get
                Return Control.Section
            End Get
        End Property

        Public ReadOnly Property HasSection As Boolean
            Get
                Return Control.Section.Length > 0
            End Get
        End Property

        Public ReadOnly Property IsToggle As Boolean
            Get
                Return Control.IsToggle
            End Get
        End Property

        Public ReadOnly Property IsSlider As Boolean
            Get
                Return Not Control.IsToggle
            End Get
        End Property

        Public ReadOnly Property Minimum As Double
            Get
                Return Control.Minimum
            End Get
        End Property

        Public ReadOnly Property Maximum As Double
            Get
                Return Control.Maximum
            End Get
        End Property

        ''' <summary>Schrittweite des Zahlenfelds, nach den Nachkommastellen der Erweiterung.</summary>
        Public ReadOnly Property Increment As Decimal
            Get
                Select Case Control.Decimals
                    Case 1 : Return 0.1D
                    Case 2 : Return 0.01D
                    Case Else : Return 1D
                End Select
            End Get
        End Property

        Private _colorTrack As Avalonia.Media.IBrush
        Private _colorTrackBuilt As Boolean

        ''' <summary>Die Farbspur aus der Datei, oder Nothing fuer den gewoehnlichen Regler.</summary>
        Public ReadOnly Property ColorTrack As Avalonia.Media.IBrush
            Get
                If _colorTrackBuilt Then Return _colorTrack
                _colorTrackBuilt = True
                Select Case Control.Track
                    Case ExtensionTracks.Hue : _colorTrack = FerrumPix.Controls.SliderTracks.FullHue
                    Case ExtensionTracks.BlackWhite : _colorTrack = FerrumPix.Controls.SliderTracks.BlackToWhite
                    Case ExtensionTracks.Temperature : _colorTrack = FerrumPix.Controls.SliderTracks.Temperature
                    Case ExtensionTracks.Tint : _colorTrack = FerrumPix.Controls.SliderTracks.Tint
                    Case ExtensionTracks.Custom
                        _colorTrack = FerrumPix.Controls.SliderTracks.FromColors(
                            Control.TrackColors.Select(Function(c) Avalonia.Media.Color.Parse(c)))
                    Case ExtensionTracks.SaturationOf
                        _colorTrack = FerrumPix.Controls.SliderTracks.SaturationOf(SourceHue())
                    Case ExtensionTracks.LuminanceOf
                        _colorTrack = FerrumPix.Controls.SliderTracks.LuminanceOf(SourceHue())
                End Select
                Return _colorTrack
            End Get
        End Property

        ''' <summary>Der Regler, dem die Spur folgt, oder Nothing.</summary>
        Friend Property TrackSource As ExtensionControlViewModel

        Private Function SourceHue() As Double
            If TrackSource Is Nothing Then Return 0
            Return ((TrackSource.Value Mod 360.0) + 360.0) Mod 360.0
        End Function

        ''' <summary>Der Regler, dem die Spur folgt, hat sich bewegt: die Spur neu bauen.</summary>
        Friend Sub RefreshColorTrack()
            _colorTrackBuilt = False
            Me.RaisePropertyChanged(NameOf(ColorTrack))
        End Sub

        Public ReadOnly Property FormatString As String
            Get
                Return "F" & Control.Decimals.ToString(CultureInfo.InvariantCulture)
            End Get
        End Property

        Public Property Value As Double
            Get
                Return _value
            End Get
            Set(newValue As Double)
                Dim clamped = Control.Clamp(newValue)
                If clamped = _value Then Return
                _owner.BeforeChange(Me)
                _value = clamped
                Me.RaisePropertyChanged(NameOf(Value))
                Me.RaisePropertyChanged(NameOf(IsChecked))
                _owner.AfterChange()
            End Set
        End Property

        Public Property IsChecked As Boolean
            Get
                Return _value >= 0.5
            End Get
            Set(newValue As Boolean)
                Value = If(newValue, 1.0, 0.0)
            End Set
        End Property

        ''' <summary>Setzt den Wert, ohne ihn als Handgriff zu melden: beim Lesen eines Rezepts,
        ''' beim Rueckgaengig und beim Zuruecksetzen.</summary>
        Friend Sub SetSilently(newValue As Double)
            Dim clamped = Control.Clamp(newValue)
            If clamped = _value Then Return
            _value = clamped
            Me.RaisePropertyChanged(NameOf(Value))
            Me.RaisePropertyChanged(NameOf(IsChecked))
        End Sub

        Friend ReadOnly Property IsDefault As Boolean
            Get
                Return _value = Control.DefaultValue
            End Get
        End Property
    End Class

    ''' <summary>Eine Erweiterung als Gruppe im Werkzeug "Erweiterungen": ihr Ein-Schalter, ihre
    ''' Bedienelemente und Zuruecksetzen.</summary>
    Public NotInheritable Class ExtensionPanelViewModel
        Inherits ReactiveObject

        Private ReadOnly _onBeforeChange As Action(Of ExtensionPanelViewModel, String)
        Private ReadOnly _onAfterChange As Action
        Private _isEnabled As Boolean
        Private _savedVersion As Integer

        Friend Sub New(definition As ExtensionDefinition, entry As ExtensionEntry,
                       onBeforeChange As Action(Of ExtensionPanelViewModel, String), onAfterChange As Action)
            Me.Definition = definition
            _onBeforeChange = onBeforeChange
            _onAfterChange = onAfterChange
            _isEnabled = entry IsNot Nothing AndAlso entry.Enabled
            _savedVersion = If(entry Is Nothing, definition.Version, entry.Version)
            Controls = definition.Controls.Select(
                Function(control)
                    Dim value As Double
                    If entry Is Nothing OrElse Not entry.Values.TryGetValue(control.Key, value) Then value = control.DefaultValue
                    Return New ExtensionControlViewModel(Me, control, value)
                End Function).ToList()
            ' Spuren, die einem Farbtonregler folgen, an ihn haengen. Beide leben so lange wie die
            ' Gruppe, ein Abmelden braucht es nicht.
            For Each dependent In Controls.Where(Function(c) c.Control.TrackSource.Length > 0)
                Dim source = Controls.FirstOrDefault(Function(c) c.Key = dependent.Control.TrackSource)
                If source Is Nothing Then Continue For
                dependent.TrackSource = source
                Dim target = dependent
                AddHandler source.PropertyChanged,
                    Sub(sender, e)
                        If e.PropertyName = NameOf(ExtensionControlViewModel.Value) Then target.RefreshColorTrack()
                    End Sub
            Next
            ResetCommand = ReactiveCommand.Create(AddressOf Reset)
        End Sub

        Public ReadOnly Property Definition As ExtensionDefinition
        Public ReadOnly Property Controls As IReadOnlyList(Of ExtensionControlViewModel)
        Public ReadOnly Property ResetCommand As ICommand

        Public ReadOnly Property Name As String
            Get
                Return Definition.Name
            End Get
        End Property

        Public ReadOnly Property Description As String
            Get
                Return Definition.Description
            End Get
        End Property

        Public ReadOnly Property HasDescription As Boolean
            Get
                Return Definition.Description.Length > 0
            End Get
        End Property

        ''' <summary>Die festen Texte der Gruppe. Uebersetzt hier und nicht im XAML: die Gruppen
        ''' entstehen erst zur Laufzeit, der Baumdurchlauf der Uebersetzung erreicht sie nicht.</summary>
        Public ReadOnly Property EnabledLabel As String
            Get
                Return LocalizationService.T("Aktiv")
            End Get
        End Property

        Public ReadOnly Property ResetLabel As String
            Get
                Return LocalizationService.T("Zurücksetzen")
            End Get
        End Property

        ''' <summary>Rechnet die Erweiterung fuer dieses Bild?</summary>
        Public Property IsEnabled As Boolean
            Get
                Return _isEnabled
            End Get
            Set(value As Boolean)
                If value = _isEnabled Then Return
                _onBeforeChange(Me, "")
                _isEnabled = value
                Me.RaisePropertyChanged(NameOf(IsEnabled))
                _onAfterChange()
            End Set
        End Property

        ''' <summary>Das Bild wurde mit einer anderen Fassung dieser Erweiterung eingestellt, als
        ''' jetzt geladen ist. Die Rechnung kann sich geaendert haben.</summary>
        Public ReadOnly Property HasVersionNote As Boolean
            Get
                Return _savedVersion <> Definition.Version
            End Get
        End Property

        Public ReadOnly Property VersionNote As String
            Get
                If Not HasVersionNote Then Return ""
                Return String.Format(CultureInfo.CurrentCulture,
                                     LocalizationService.T("Dieses Bild wurde mit Fassung {0} eingestellt, geladen ist Fassung {1}."),
                                     _savedVersion, Definition.Version)
            End Get
        End Property

        Friend Sub BeforeChange(control As ExtensionControlViewModel)
            _onBeforeChange(Me, control.Key)
        End Sub

        Friend Sub AfterChange()
            ' Wer einen Regler bewegt, will die Wirkung sehen: die Erweiterung geht mit an.
            If Not _isEnabled Then
                _isEnabled = True
                Me.RaisePropertyChanged(NameOf(IsEnabled))
            End If
            _onAfterChange()
        End Sub

        Private Sub Reset()
            If Not _isEnabled AndAlso Controls.All(Function(c) c.IsDefault) Then Return
            _onBeforeChange(Me, "")
            ApplyEntry(Nothing)
            _onAfterChange()
        End Sub

        ''' <summary>Uebernimmt den Stand aus einem Rezept (Nothing: aus und alle Vorgaben), ohne
        ''' einen Handgriff zu melden.</summary>
        Friend Sub ApplyEntry(entry As ExtensionEntry)
            Dim enabled = entry IsNot Nothing AndAlso entry.Enabled
            If enabled <> _isEnabled Then
                _isEnabled = enabled
                Me.RaisePropertyChanged(NameOf(IsEnabled))
            End If
            Dim saved = If(entry Is Nothing, Definition.Version, entry.Version)
            If saved <> _savedVersion Then
                _savedVersion = saved
                Me.RaisePropertyChanged(NameOf(HasVersionNote))
                Me.RaisePropertyChanged(NameOf(VersionNote))
            End If
            For Each control In Controls
                Dim value As Double
                If entry Is Nothing OrElse Not entry.Values.TryGetValue(control.Key, value) Then value = control.Control.DefaultValue
                control.SetSilently(value)
            Next
        End Sub

        ''' <summary>Der Eintrag fuers Rezept, oder Nothing, wenn die Erweiterung aus ist und alle
        ''' Regler auf ihrer Vorgabe stehen: dann hat sie am Bild nichts verloren, und ein Bild, an
        ''' dem niemand etwas eingestellt hat, zaehlt nicht als bearbeitet.</summary>
        Friend Function ToEntry() As ExtensionEntry
            If Not _isEnabled AndAlso Controls.All(Function(c) c.IsDefault) Then Return Nothing
            Dim entry = New ExtensionEntry With {.Id = Definition.Id, .Version = Definition.Version, .Enabled = _isEnabled}
            For Each control In Controls
                entry.Values(control.Key) = control.Value
            Next
            Return entry
        End Function
    End Class

    ' Die Erweiterungen im Editor: das Werkzeug "Erweiterungen" zeigt je geladener Erweiterung eine
    ' Gruppe mit ihren Reglern. Der Zustand ist EINE Zeichenkette, dieselbe wie im Rezept
    ' (ImageAdjustments.ExtensionSettings); die Gruppen sind nur ihre Ansicht. So gehen
    ' Rueckgaengig, Uebertragen auf andere Bilder und das Speichern den Weg jeder anderen
    ' Anpassung, ohne dass einer davon die Erweiterungen kennen muss.
    Partial Public Class EditorViewModel

        Private _extensionSettings As String = ""
        Private _extensionPanelRevision As Long = -1
        Private _extensionServiceHooked As Boolean

        ''' <summary>Die Gruppen des Werkzeugs, eine je geladener Erweiterung.</summary>
        Public ReadOnly Property ExtensionPanels As New ObservableCollection(Of ExtensionPanelViewModel)()

        ''' <summary>Das Werkzeug steht in der Leiste, sobald eine Erweiterung geladen ist. Ohne
        ''' Erweiterung gaebe es dort nichts zu tun.</summary>
        Public ReadOnly Property ShowsToolExtensions As Boolean
            Get
                ' Die Leiste fragt das als Erstes, auch bevor ein Bild offen ist: ab hier folgt sie
                ' dem Bestand.
                EnsureExtensionServiceHooked()
                Return ExtensionService.All.Count > 0
            End Get
        End Property

        Public ReadOnly Property ShowExtensionAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Extensions
            End Get
        End Property

        ''' <summary>Traegt das Bild Einstellungen fuer Erweiterungen, die auf diesem Rechner
        ''' fehlen? Sie bleiben im Rezept, rechnen aber nicht.</summary>
        Public ReadOnly Property HasMissingExtensions As Boolean
            Get
                Return MissingExtensionIds().Count > 0
            End Get
        End Property

        Public ReadOnly Property MissingExtensionsText As String
            Get
                Dim missing = MissingExtensionIds()
                If missing.Count = 0 Then Return ""
                Return LocalizationService.T("Dieses Bild nutzt Erweiterungen, die hier nicht geladen sind. Ihre Einstellungen bleiben erhalten:") &
                       " " & String.Join(", ", missing)
            End Get
        End Property

        Private Function MissingExtensionIds() As List(Of String)
            Return ExtensionRecipe.Parse(_extensionSettings).
                Where(Function(e) ExtensionService.Find(e.Id) Is Nothing).Select(Function(e) e.Id).ToList()
        End Function

        ''' <summary>Haengt den Editor an den Bestand der Erweiterungen: wird eine geladen oder
        ''' entfernt (im Einstellungsdialog), folgen Werkzeugleiste, Gruppen und Vorschau.</summary>
        Private Sub EnsureExtensionServiceHooked()
            If _extensionServiceHooked Then Return
            _extensionServiceHooked = True
            ' Der Bestand lebt so lange wie das Programm, ein Editor nicht unbedingt: der Bestand
            ' haelt ihn deshalb nur schwach, und der Lauscher meldet sich ab, sobald er fort ist.
            Dim weak = New WeakReference(Of EditorViewModel)(Me)
            Dim handler As EventHandler = Nothing
            handler = Sub(sender, e)
                          Dim editor As EditorViewModel = Nothing
                          If Not weak.TryGetTarget(editor) Then
                              RemoveHandler ExtensionService.Changed, handler
                              Return
                          End If
                          Avalonia.Threading.Dispatcher.UIThread.Post(AddressOf editor.OnExtensionsChanged)
                      End Sub
            AddHandler ExtensionService.Changed, handler
        End Sub

        Private Sub OnExtensionsChanged()
            RefreshExtensionPanels()
            Me.RaisePropertyChanged(NameOf(ShowsToolExtensions))
            ' Wer im Werkzeug steht, waehrend die letzte Erweiterung geht, kaeme ueber die Leiste
            ' nicht mehr heraus.
            If _currentTool = EditorTool.Extensions AndAlso Not ShowsToolExtensions Then CurrentTool = EditorTool.Selection
            RaiseToolSwitcherChanged()
            ' Eine getauschte Datei rechnet anders: das Bild muss neu entstehen.
            If _extensionSettings.Length > 0 Then SchedulePreviewUpdate()
        End Sub

        ''' <summary>Bringt die Gruppen auf den Stand von Bestand und Rezept. Bleibt der Bestand
        ''' derselbe, behalten die Gruppen ihre Objekte und bekommen nur neue Werte: sonst klappte
        ''' bei jedem Rueckgaengig die offene Gruppe zu und ein laufender Reglerzug risse ab.</summary>
        Private Sub RefreshExtensionPanels()
            EnsureExtensionServiceHooked()
            Dim entries = ExtensionRecipe.Parse(_extensionSettings)
            Dim revision = ExtensionService.PanelRevision
            If revision <> _extensionPanelRevision Then
                _extensionPanelRevision = revision
                ExtensionPanels.Clear()
                For Each definition In ExtensionService.All
                    Dim entry = entries.FirstOrDefault(Function(e) String.Equals(e.Id, definition.Id, StringComparison.Ordinal))
                    ExtensionPanels.Add(New ExtensionPanelViewModel(definition, entry, AddressOf OnExtensionChanging, AddressOf OnExtensionChanged))
                Next
            Else
                For Each panel In ExtensionPanels
                    panel.ApplyEntry(entries.FirstOrDefault(Function(e) String.Equals(e.Id, panel.Definition.Id, StringComparison.Ordinal)))
                Next
            End If
            Me.RaisePropertyChanged(NameOf(HasMissingExtensions))
            Me.RaisePropertyChanged(NameOf(MissingExtensionsText))
        End Sub

        ''' <summary>Kennzeichen eines Handgriffs an einer Erweiterung fuer die Historie:
        ''' "Extension:Kennung:Schluessel", der Schluessel leer fuer Ein, Aus und Zuruecksetzen.</summary>
        Private Const ExtensionHistoryPrefix As String = "Extension:"

        Private Sub OnExtensionChanging(panel As ExtensionPanelViewModel, controlKey As String)
            CaptureUndoState(ExtensionHistoryPrefix & panel.Definition.Id & ":" & controlKey)
        End Sub

        Private Sub OnExtensionChanged()
            _extensionSettings = BuildExtensionSettings()
            RaiseResetButtonStateChanged()
            SchedulePreviewUpdate()
        End Sub

        ''' <summary>Die Einstellungen aus den Gruppen. Die Reihenfolge des Rezepts bleibt (sie ist
        ''' die Rechenfolge), neu eingeschaltete kommen hinten dran, und was auf diesem Rechner
        ''' fehlt, bleibt an seinem Platz stehen.</summary>
        Private Function BuildExtensionSettings() As String
            Dim result As New List(Of ExtensionEntry)()
            Dim written As New HashSet(Of String)(StringComparer.Ordinal)
            For Each existing In ExtensionRecipe.Parse(_extensionSettings)
                Dim panel = ExtensionPanels.FirstOrDefault(Function(p) String.Equals(p.Definition.Id, existing.Id, StringComparison.Ordinal))
                If panel Is Nothing Then
                    result.Add(existing)
                    Continue For
                End If
                written.Add(existing.Id)
                Dim entry = panel.ToEntry()
                If entry IsNot Nothing Then result.Add(entry)
            Next
            For Each panel In ExtensionPanels
                If written.Contains(panel.Definition.Id) Then Continue For
                Dim entry = panel.ToEntry()
                If entry IsNot Nothing Then result.Add(entry)
            Next
            Return ExtensionRecipe.Serialize(result)
        End Function

        Private Sub WriteExtensionsInto(adj As ImageAdjustments)
            adj.ExtensionSettings = _extensionSettings
        End Sub

        Private Sub ReadExtensionsFrom(adj As ImageAdjustments)
            _extensionSettings = ExtensionRecipe.Normalize(If(adj Is Nothing, "", adj.ExtensionSettings))
            RefreshExtensionPanels()
        End Sub

        ''' <summary>Alle Erweiterungen aus und auf Vorgabe. Auch die Eintraege fehlender
        ''' Erweiterungen gehen: wer das Bild zuruecksetzt, meint alles.</summary>
        Private Sub ResetExtensionValues()
            _extensionSettings = ""
            RefreshExtensionPanels()
        End Sub

        ''' <summary>Der Name eines Handgriffs an einer Erweiterung in der Historie, oder Nothing,
        ''' wenn das Kennzeichen keines ist.</summary>
        Private Function ExtensionHistoryLabel(propertyName As String) As String
            If propertyName Is Nothing OrElse Not propertyName.StartsWith(ExtensionHistoryPrefix, StringComparison.Ordinal) Then Return Nothing
            Dim rest = propertyName.Substring(ExtensionHistoryPrefix.Length)
            Dim cut = rest.LastIndexOf(":"c)
            Dim id = If(cut < 0, rest, rest.Substring(0, cut))
            Dim key = If(cut < 0, "", rest.Substring(cut + 1))
            Dim definition = ExtensionService.Find(id)
            If definition Is Nothing Then Return LocalizationService.T("Erweiterungen")
            Dim control = definition.FindControl(key)
            Return If(control Is Nothing, definition.Name, definition.Name & ": " & control.Label)
        End Function

    End Class

End Namespace
