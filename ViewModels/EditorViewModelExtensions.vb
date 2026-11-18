Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Linq
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Ein Bedienelement einer Erweiterung im Panel: Regler, Schalter, Farbe oder Kurve,
    ''' mit dem Wert, den das Bild gerade traegt. Jede Aenderung geht an das Panel der
    ''' Erweiterung, das sie ins Rezept schreibt.</summary>
    Public NotInheritable Class ExtensionControlViewModel
        Inherits ReactiveObject

        Private ReadOnly _owner As ExtensionPanelViewModel
        Private _value As Double
        Private _color As String
        Private _curve As List(Of (X As Double, Y As Double))
        ''' <summary>Die Punkte des Kurvenfelds, 0 bis 255 wie bei der Tonwertkurve.</summary>
        Private ReadOnly _curvePoints As New ObservableCollection(Of Avalonia.Point)()
        Private _loading As Boolean

        Friend Sub New(owner As ExtensionPanelViewModel, control As ExtensionControl, entry As ExtensionEntry)
            _owner = owner
            Me.Control = control
            LoadFrom(entry)
            AddHandler _curvePoints.CollectionChanged, AddressOf OnCurvePointsChanged
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
                Return Control.IsNumber AndAlso Not Control.IsToggle AndAlso Not Control.IsChoice
            End Get
        End Property

        Public ReadOnly Property IsColor As Boolean
            Get
                Return Control.IsColor
            End Get
        End Property

        Public ReadOnly Property IsChoice As Boolean
            Get
                Return Control.IsChoice
            End Get
        End Property

        ''' <summary>Die Beschriftungen der Auswahlliste, in der Reihenfolge der Datei.</summary>
        Public ReadOnly Property OptionLabels As IReadOnlyList(Of String)
            Get
                Return Control.Options.Select(Function(o) o.Label).ToList()
            End Get
        End Property

        ''' <summary>Die gewaehlte Moeglichkeit, ueber ihre Stelle in der Liste; dahinter steht ihr Wert.
        '''
        ''' UEBER DIE STELLE, NICHT UEBER DIE BESCHRIFTUNG: die Datei verlangt nur verschiedene
        ''' Werte, nicht verschiedene Beschriftungen, und zwei Moeglichkeiten koennen in einer Sprache
        ''' gleich heissen. Ueber die Beschriftung gebunden waehlte die Liste dann immer die erste,
        ''' und der Wert der zweiten war im Editor nicht zu erreichen.</summary>
        Public Property SelectedOptionIndex As Integer
            Get
                Dim options = Control.Options
                For i = 0 To options.Count - 1
                    If options(i).Value = _value Then Return i
                Next
                Return -1
            End Get
            Set(index As Integer)
                If index < 0 OrElse index >= Control.Options.Count Then Return
                Value = Control.Options(index).Value
            End Set
        End Property

        ''' <summary>Der Hilfetext als Tooltip; ohne Hilfetext die Beschriftung, damit eine
        ''' abgeschnittene Beschriftung lesbar bleibt.</summary>
        Public ReadOnly Property HintOrLabel As String
            Get
                Dim hint = Control.Hint
                Return If(hint.Length > 0, Label & vbLf & hint, Label)
            End Get
        End Property

        Public ReadOnly Property IsCurve As Boolean
            Get
                Return Control.IsCurve
            End Get
        End Property

        Public ReadOnly Property Minimum As Double
            Get
                Return Control.Minimum
            End Get
        End Property

        ''' <summary>Die Vorgabe des Reglers aus der Datei: Doppelklick setzt ihn darauf zurueck,
        ''' Rechtsklick vergleicht mit ihr, wie bei den eingebauten Reglern.</summary>
        Public ReadOnly Property DefaultValue As Double
            Get
                Return Control.DefaultValue
            End Get
        End Property

        Public ReadOnly Property ResetLabel As String
            Get
                Return LocalizationService.T("Zurücksetzen")
            End Get
        End Property

        ''' <summary>Setzt eine Farbe oder Kurve auf die Vorgabe der Datei, als ein Handgriff.</summary>
        Public ReadOnly Property ResetCommand As ICommand
            Get
                If _resetCommand Is Nothing Then _resetCommand = ReactiveCommand.Create(AddressOf ResetToDefault)
                Return _resetCommand
            End Get
        End Property
        Private _resetCommand As ICommand

        Private Sub ResetToDefault()
            If IsDefault Then Return
            _owner.BeforeChange(Me)
            LoadFrom(Nothing)
            _owner.AfterChange()
        End Sub

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

        ''' <summary>Schritt je Raste des Mausrads. Ohne Nachkommastellen 1 wie bei den eingebauten
        ''' Reglern. Mit Nachkommastellen ein Hundertstel des Bereichs, auf die Stellen der Datei
        ''' gerundet und nie kleiner als deren letzte Stelle: ohne eigene Angabe ging der Regler um 1,
        ''' auf 0 bis 1 also in zwei Rasten von einem Ende zum anderen, und bei 0 bis 10 Blenden in
        ''' Hundertsteln hiesse die letzte Stelle tausend Rasten.</summary>
        Public ReadOnly Property WheelIncrement As Double
            Get
                If Control.Decimals <= 0 Then Return 1.0
                Dim smallest = Math.Pow(10, -Control.Decimals)
                Dim rangeStep = Math.Round((Control.Maximum - Control.Minimum) / 100.0, Control.Decimals, MidpointRounding.AwayFromZero)
                Return Math.Max(smallest, rangeStep)
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
                Me.RaisePropertyChanged(NameOf(SelectedOptionIndex))
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

        ''' <summary>Die Farbe fuer den Farbwaehler.</summary>
        Public Property ColorValue As Avalonia.Media.Color
            Get
                Return Avalonia.Media.Color.Parse(_color)
            End Get
            Set(newValue As Avalonia.Media.Color)
                Dim text = String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", newValue.R, newValue.G, newValue.B)
                If String.Equals(text, _color, StringComparison.Ordinal) Then Return
                _owner.BeforeChange(Me)
                _color = text
                Me.RaisePropertyChanged(NameOf(ColorValue))
                _owner.AfterChange()
            End Set
        End Property

        ''' <summary>Die Punkte fuer das Kurvenfeld (0 bis 255). Das Feld aendert sie an Ort und
        ''' Stelle; OnCurvePointsChanged nimmt jede Aenderung als Handgriff.</summary>
        Public ReadOnly Property CurvePoints As ObservableCollection(Of Avalonia.Point)
            Get
                Return _curvePoints
            End Get
        End Property

        Private Sub OnCurvePointsChanged(sender As Object, e As System.Collections.Specialized.NotifyCollectionChangedEventArgs)
            If _loading Then Return
            ' Das Rezept traegt noch den alten Stand (erst AfterChange schreibt den neuen): der
            ' Rueckgaengig-Punkt, den BeforeChange anlegt, haelt also wirklich das Vorher fest.
            _owner.BeforeChange(Me)
            _curve = _curvePoints.Select(Function(p) (Math.Max(0.0, Math.Min(1.0, p.X / 255.0)),
                                                      Math.Max(0.0, Math.Min(1.0, p.Y / 255.0)))).
                                  OrderBy(Function(p) p.Item1).ToList()
            _owner.AfterChange()
        End Sub

        ''' <summary>Uebernimmt den Wert aus einem Rezept (Nothing: die Vorgabe), ohne ihn als
        ''' Handgriff zu melden: beim Lesen eines Rezepts, beim Rueckgaengig und beim Zuruecksetzen.</summary>
        Friend Sub LoadFrom(entry As ExtensionEntry)
            If Control.IsColor Then
                Dim text As String = Nothing
                If entry Is Nothing OrElse Not entry.Colors.TryGetValue(Control.Key, text) Then text = Control.DefaultColor
                If Not String.Equals(text, _color, StringComparison.Ordinal) Then
                    _color = text
                    Me.RaisePropertyChanged(NameOf(ColorValue))
                End If
            ElseIf Control.IsCurve Then
                Dim points As List(Of (X As Double, Y As Double)) = Nothing
                If entry Is Nothing OrElse Not entry.Curves.TryGetValue(Control.Key, points) Then points = Control.DefaultCurve
                If _curve IsNot Nothing AndAlso _curve.SequenceEqual(points) Then Return
                _curve = points.ToList()
                _loading = True
                Try
                    _curvePoints.Clear()
                    For Each p In _curve
                        _curvePoints.Add(New Avalonia.Point(p.X * 255.0, p.Y * 255.0))
                    Next
                Finally
                    _loading = False
                End Try
            Else
                Dim value As Double
                If entry Is Nothing OrElse Not entry.Values.TryGetValue(Control.Key, value) Then value = Control.DefaultValue
                Dim clamped = Control.Clamp(value)
                If clamped = _value Then Return
                _value = clamped
                Me.RaisePropertyChanged(NameOf(Value))
                Me.RaisePropertyChanged(NameOf(IsChecked))
                Me.RaisePropertyChanged(NameOf(SelectedOptionIndex))
            End If
        End Sub

        ''' <summary>Schreibt den Wert in einen Eintrag des Rezepts.</summary>
        Friend Sub WriteTo(entry As ExtensionEntry)
            If Control.IsColor Then
                entry.Colors(Control.Key) = _color
            ElseIf Control.IsCurve Then
                ' Auf vier Stellen gerundet: das Feld liefert krumme Punkte, und die feste Form des
                ' Rezepts sollte nicht von Gleitkommastaub abhaengen.
                entry.Curves(Control.Key) = _curve.Select(Function(p) (Math.Round(p.X, 4), Math.Round(p.Y, 4))).ToList()
            Else
                entry.Values(Control.Key) = _value
            End If
        End Sub

        Friend ReadOnly Property IsDefault As Boolean
            Get
                If Control.IsColor Then Return String.Equals(_color, Control.DefaultColor, StringComparison.Ordinal)
                If Control.IsCurve Then
                    Return _curve.Count = Control.DefaultCurve.Count AndAlso
                           _curve.Zip(Control.DefaultCurve).All(Function(z) Math.Abs(z.First.X - z.Second.X) < 0.0001 AndAlso
                                                                           Math.Abs(z.First.Y - z.Second.Y) < 0.0001)
                End If
                Return _value = Control.DefaultValue
            End Get
        End Property
    End Class

    ''' <summary>Eine Erweiterung als Gruppe im Werkzeug "Erweiterungen": ihr Ein-Schalter, ihre
    ''' Bedienelemente, ihre Voreinstellungen und Zuruecksetzen.</summary>
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
            Controls = definition.Controls.Select(Function(control) New ExtensionControlViewModel(Me, control, entry)).ToList()
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

        ''' <summary>Unter diesem Schluessel merkt sich die Gruppe, ob sie auf- oder zugeklappt ist
        ''' (ExpanderState), wie die eingebauten Gruppen. Die Kennung der Erweiterung, mit eigenem
        ''' Vorsatz, damit sie nie auf den Schluessel einer eingebauten Gruppe faellt.</summary>
        Public ReadOnly Property ExpanderKey As String
            Get
                Return "extension:" & Definition.Id
            End Get
        End Property

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

        ''' <summary>Der Schluessel im Gruppenvergleich, Parameter des Auges im Gruppenkopf.</summary>
        Public ReadOnly Property GroupKey As String
            Get
                Return EditorViewModel.ExtensionGroupKeyPrefix & Definition.Id
            End Get
        End Property

        Private _isComparing As Boolean

        ''' <summary>Steht die Erweiterung im Vergleich? Gesetzt vom Editor
        ''' (RefreshExtensionCompareFlags).</summary>
        Public Property IsComparing As Boolean
            Get
                Return _isComparing
            End Get
            Friend Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isComparing, value)
            End Set
        End Property

        Public ReadOnly Property CompareLabel As String
            Get
                Return LocalizationService.T("Gruppe vorübergehend ausblenden, um vorher und nachher zu vergleichen")
            End Get
        End Property

        Public ReadOnly Property PresetLabel As String
            Get
                Return LocalizationService.T("Voreinstellung")
            End Get
        End Property

        ' ── Voreinstellungen ─────────────────────────────────────────────────

        Public ReadOnly Property HasPresets As Boolean
            Get
                Return Definition.Presets.Count > 0
            End Get
        End Property

        Public ReadOnly Property PresetNames As IReadOnlyList(Of String)
            Get
                Return Definition.Presets.Select(Function(p) p.Name).ToList()
            End Get
        End Property

        ''' <summary>Die Auswahl der Voreinstellung. Gewaehlt wird einmal: der Wertesatz wird
        ''' uebernommen, danach steht die Auswahl wieder leer, denn wer einen Regler weiterbewegt,
        ''' hat die Voreinstellung schon verlassen.</summary>
        Public Property SelectedPresetName As String
            Get
                Return Nothing
            End Get
            Set(value As String)
                If String.IsNullOrEmpty(value) Then Return
                Dim preset = Definition.Presets.FirstOrDefault(Function(p) String.Equals(p.Name, value, StringComparison.Ordinal))
                If preset IsNot Nothing Then ApplyPreset(preset)
                Avalonia.Threading.Dispatcher.UIThread.Post(Sub() Me.RaisePropertyChanged(NameOf(SelectedPresetName)))
            End Set
        End Property

        ''' <summary>Setzt die Werte einer Voreinstellung als EIN Handgriff: was sie nennt, auf ihren
        ''' Wert, alles andere auf die Vorgabe, und schaltet die Erweiterung ein.</summary>
        Friend Sub ApplyPreset(preset As ExtensionPreset)
            _onBeforeChange(Me, "")
            Dim entry = preset.Entry.Clone()
            entry.Id = Definition.Id
            entry.Version = Definition.Version
            entry.Enabled = True
            ApplyEntry(entry)
            _onAfterChange()
        End Sub

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
                                     LocalizationService.T("Dieses Bild wurde mit Version {0} eingestellt, geladen ist Version {1}."),
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
                control.LoadFrom(entry)
            Next
        End Sub

        ''' <summary>Der Eintrag fuers Rezept, oder Nothing, wenn die Erweiterung aus ist und alle
        ''' Elemente auf ihrer Vorgabe stehen: dann hat sie am Bild nichts verloren, und ein Bild, an
        ''' dem niemand etwas eingestellt hat, zaehlt nicht als bearbeitet.</summary>
        Friend Function ToEntry() As ExtensionEntry
            If Not _isEnabled AndAlso Controls.All(Function(c) c.IsDefault) Then Return Nothing
            Dim entry = New ExtensionEntry With {.Id = Definition.Id, .Version = Definition.Version, .Enabled = _isEnabled}
            For Each control In Controls
                control.WriteTo(entry)
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
                ' dem Bestand. Im Kompaktmodus stehen die Erweiterungen als Block im Werkzeug
                ' "Anpassungen", und wer die Gruppe ausgeblendet hat, saehe ein leeres Werkzeug.
                EnsureExtensionServiceHooked()
                Return Not IsCompactAdjustments AndAlso ExtensionService.Shown.Count > 0 AndAlso HasVisibleExtensionGroup()
            End Get
        End Property

        Private Function HasVisibleExtensionGroup() As Boolean
            Return Not HiddenAdjustmentGroups.Split(","c).Any(
                Function(k) String.Equals(k.Trim(), "extensions", StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Das Panel der Erweiterungen steht im eigenen Werkzeug und, als Block, im
        ''' Kompaktwerkzeug.</summary>
        Public ReadOnly Property ShowExtensionAdjustments As Boolean
            Get
                Return _currentTool = EditorTool.Extensions OrElse _currentTool = EditorTool.AllAdjustments
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
                For Each definition In ExtensionService.Shown
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
            RefreshExtensionCompareFlags()
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

        ''' <summary>Der Schluessel einer Erweiterung im Gruppenvergleich (wie der Schluessel eines
        ''' Aufklappers bei den eingebauten Gruppen).</summary>
        Friend Const ExtensionGroupKeyPrefix As String = "ext:"

        ''' <summary>Der Gruppenvergleich einer Erweiterung: sie wird ausgeschaltet und beim zweiten
        ''' Klick zurueckgeholt. Alle Erweiterungen eines Bildes stehen in EINEM Feld des Rezepts;
        ''' aendert sich waehrenddessen eine andere, endet der Vergleich, wie bei den eingebauten
        ''' Gruppen, wenn ein Wert von anderer Seite kommt.</summary>
        Private Function ExtensionCompareStep(groupKey As String) As (Header As String, Neutralize As Action)
            Dim key = If(groupKey, "")
            If Not key.StartsWith(ExtensionGroupKeyPrefix, StringComparison.Ordinal) Then Return (Nothing, Nothing)
            Dim id = key.Substring(ExtensionGroupKeyPrefix.Length)
            Dim panel = ExtensionPanels.FirstOrDefault(Function(p) String.Equals(p.Definition.Id, id, StringComparison.Ordinal))
            If panel Is Nothing Then Return (Nothing, Nothing)
            Return (panel.Name, Sub() panel.IsEnabled = False)
        End Function

        ''' <summary>Die Augen der Gruppen nachziehen, nachdem sich die Liste der Vergleiche geaendert hat.</summary>
        Private Sub RefreshExtensionCompareFlags()
            Dim comparing = If(_comparingAdjustmentGroups, "").Split(","c)
            For Each panel In ExtensionPanels
                panel.IsComparing = comparing.Contains(panel.GroupKey)
            Next
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
