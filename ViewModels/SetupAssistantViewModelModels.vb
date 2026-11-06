Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Eine Funktion auf der Modellseite des Assistenten: Kaestchen, Name und ein Satz.
    ''' Bereits vorhandene sind angehakt und gesperrt, ohne eigenen Hinweis.</summary>
    Public Class SetupModelOption
        Inherits ReactiveObject

        Private ReadOnly _onChanged As Action

        Public Sub New(group As SettingsViewModel.ModelGroup, onChanged As Action)
            Me.Group = group
            _onChanged = onChanged
            _isSelected = IsInstalled
        End Sub

        Public ReadOnly Property Group As SettingsViewModel.ModelGroup

        Public ReadOnly Property Name As String
            Get
                Return Group.Name
            End Get
        End Property

        Public ReadOnly Property Description As String
            Get
                Return Group.Description
            End Get
        End Property

        ''' <summary>Ein kurzer Satz fuer die Karte im Assistenten: nur, was die Funktion tut. Die
        ''' Beschreibungen der Einstellungen nennen dazu Modellzahl, Groesse und Herkunft; hier
        ''' liessen sie die Karten ungleich hoch werden und sagen dem Einsteiger nichts.</summary>
        Public ReadOnly Property ShortDescription As String
            Get
                Select Case If(Group.Files.FirstOrDefault()?.Group, "")
                    Case "Objektauswahl" : Return LocalizationService.T("Ein Objekt anklicken, die Maske entsteht von selbst.")
                    Case "Tiefe" : Return LocalizationService.T("Masken nach Entfernung und Unschärfe nach Tiefe.")
                    Case "Objekt entfernen" : Return LocalizationService.T("Störendes entfernen, der Hintergrund wird ergänzt.")
                    Case "Entrauschen" : Return LocalizationService.T("Rauschen aus Fotos mit hoher ISO entfernen.")
                    Case "Hochskalieren" : Return LocalizationService.T("Fotos zwei- oder vierfach vergrößern.")
                    Case "Automatische Stichwörter" : Return LocalizationService.T("Bildinhalte erkennen und als Stichwörter ablegen.")
                    Case "Orte" : Return LocalizationService.T("Ortsnamen zu den Koordinaten im Foto.")
                    Case Else : Return Group.Description
                End Select
            End Get
        End Property

        ''' <summary>Das Symbol der Karte, nach der festen Gruppe der Dateien (nicht nach dem
        ''' uebersetzten Namen).</summary>
        Public ReadOnly Property IconSource As String
            Get
                Dim key = If(Group.Files.FirstOrDefault()?.Group, "")
                Dim icon As String
                Select Case key
                    Case "Objektauswahl" : icon = "cursor-click"
                    Case "Tiefe" : icon = "mountains"
                    Case "Objekt entfernen" : icon = "eraser"
                    Case "Entrauschen" : icon = "sparkle"
                    Case "Hochskalieren" : icon = "arrows-out"
                    Case "Automatische Stichwörter" : icon = "tag"
                    Case "Orte" : icon = "map-pin"
                    Case Else : icon = "magic-wand"
                End Select
                Return "avares://FerrumPix/Assets/Icons/phosphor/regular/" & icon & ".svg"
            End Get
        End Property

        Public ReadOnly Property IsInstalled As Boolean
            Get
                Return Group.IsComplete
            End Get
        End Property

        Public ReadOnly Property CanChoose As Boolean
            Get
                Return Not IsInstalled
            End Get
        End Property

        ''' <summary>Die Dateien der Funktion, die noch fehlen; nur sie werden geladen und gezaehlt.</summary>
        Public ReadOnly Property MissingFiles As List(Of AiModelService.ModelEntry)
            Get
                Return Group.Files.Where(Function(d) String.IsNullOrEmpty(AiModelService.BestFile(d.Key))).ToList()
            End Get
        End Property

        Private _isSelected As Boolean
        Public Property IsSelected As Boolean
            Get
                Return _isSelected
            End Get
            Set(value As Boolean)
                If IsInstalled Then value = True
                If _isSelected = value Then Return
                Me.RaiseAndSetIfChanged(_isSelected, value)
                _onChanged?.Invoke()
            End Set
        End Property

        ''' <summary>Nach einem Download: vorhanden ist jetzt angehakt und gesperrt.</summary>
        Public Sub RefreshState()
            If IsInstalled Then _isSelected = True
            Me.RaisePropertyChanged(NameOf(IsInstalled))
            Me.RaisePropertyChanged(NameOf(CanChoose))
            Me.RaisePropertyChanged(NameOf(IsSelected))
        End Sub
    End Class

    ''' <summary>Die Modellseite des Assistenten: eine Liste mit Kaestchen, darunter die
    ''' Gesamtgroesse der angehakten und fehlenden Dateien, EIN Knopf und EIN Fortschritt fuer
    ''' alle. Geladen wird ueber SettingsViewModel.DownloadModelEntriesAsync, derselbe Weg wie der
    ''' Dialog "Modell herunterladen".</summary>
    Partial Public Class SetupAssistantViewModel

        Public ReadOnly Property ModelOptions As New ObservableCollection(Of SetupModelOption)()

        Public ReadOnly Property CancelModelDownloadCommand As ICommand

        Private _modelDownloadCancel As CancellationTokenSource
        Private _modelDownloadTask As Task

        Private Sub BuildModelOptions()
            ModelOptions.Clear()
            _peopleGroup = Nothing
            If _main.Settings Is Nothing Then Return
            For Each group In _main.Settings.ModelGroups
                ' Die Gruppe mit fremder Bedingung (Personen, ArcFace) steht NICHT in der Liste,
                ' sondern als eigener Schalter darunter: dort kommt die Rueckfrage beim Einschalten
                ' und nennt das Modell, um das es geht.
                If group.Files.Any(Function(d) d.NonCommercialOnly) Then
                    _peopleGroup = group
                    Continue For
                End If
                ModelOptions.Add(New SetupModelOption(group, AddressOf RaiseModelSelectionChanged))
            Next
            Me.RaisePropertyChanged(NameOf(HasPeopleOption))
        End Sub

        ' ---------------------------------------------------------------- Personen

        Private _peopleGroup As SettingsViewModel.ModelGroup
        ''' <summary>Der Schalter steht an: Personen sollen erkannt werden. Fehlt das Modell, laedt
        ''' Weiter es mit; danach wird die Erkennung eingeschaltet.</summary>
        Private _peopleChosen As Boolean
        Private _peopleChosenInitialized As Boolean

        Public ReadOnly Property HasPeopleOption As Boolean
            Get
                Return _peopleGroup IsNot Nothing
            End Get
        End Property

        Private ReadOnly Property IsPeopleModelInstalled As Boolean
            Get
                Return _peopleGroup IsNot Nothing AndAlso _peopleGroup.IsComplete
            End Get
        End Property

        ''' <summary>Der Schalter "Personen erkennen". Einschalten OHNE vorhandenes Modell fragt
        ''' sofort nach der Bedingung des Modells (nur fuer nicht-kommerzielle Forschung) und merkt
        ''' es nur bei Zustimmung fuer den Download vor; der Schalter springt sonst zurueck. Mit
        ''' vorhandenem Modell schaltet er die Erkennung direkt in den Einstellungen.</summary>
        Public Property PeopleEnabled As Boolean
            Get
                If Not _peopleChosenInitialized Then
                    _peopleChosen = IsPeopleModelInstalled AndAlso _main.Settings IsNot Nothing AndAlso _main.Settings.FaceRecognitionEnabled
                    _peopleChosenInitialized = True
                End If
                ' Waehrend die Rueckfrage offen ist, steht der Schalter so, wie er geklickt wurde.
                Return _peopleChosen OrElse _peopleAsking
            End Get
            Set(value As Boolean)
                If _peopleAsking Then Return
                If PeopleEnabled = value Then Return
                If Not value Then
                    _peopleChosen = False
                    Me.RaisePropertyChanged(NameOf(PeopleEnabled))
                    If _main.Settings IsNot Nothing AndAlso _main.Settings.FaceRecognitionEnabled Then _main.Settings.FaceRecognitionEnabled = False
                    RaiseModelSelectionChanged()
                    Return
                End If
                If IsPeopleModelInstalled Then
                    _peopleChosen = True
                    Me.RaisePropertyChanged(NameOf(PeopleEnabled))
                    If _main.Settings IsNot Nothing Then _main.Settings.FaceRecognitionEnabled = True
                    Return
                End If
                ' Der Schalter hat sich schon umgelegt. Er BLEIBT so, solange die Rueckfrage offen ist
                ' (_peopleAsking), und geht erst bei Ablehnung zurueck. Ihn sofort zurueckzumelden
                ' half nicht: die Bindung hatte zuletzt "aus" gelesen, sah in einem erneuten "aus"
                ' keine Aenderung und liess den Schalter stehen. Erst ein echter Wechsel von "an"
                ' nach "aus" stellt ihn zurueck.
                _peopleAsking = True
                Dim ignored = AskAndChoosePeopleAsync()
            End Set
        End Property

        Private Async Function AskAndChoosePeopleAsync() As Task
            Try
                Dim goAhead = Await _main.ShowConfirmAsync(
                    LocalizationService.T("Nur für nicht-kommerzielle Forschung"),
                    LocalizationService.T("ArcFace ResNet100 wird aus dem ONNX Model Zoo auf Hugging Face heruntergeladen. Die vortrainierten Gewichte stammen laut InsightFace aus Daten, die nur für nicht-kommerzielle Forschung freigegeben sind. Bei kommerzieller Nutzung von FerrumPix darf das Modell nicht heruntergeladen oder verwendet werden; die übrigen Funktionen bleiben verfügbar."),
                    "Zustimmen", "Abbrechen")
                _peopleChosen = goAhead
            Catch ex As Exception
                DiagnosticLogService.LogException("SetupAssistant.ChoosePeople", ex)
                _peopleChosen = False
            Finally
                ' Erst jetzt endet das Fragen: bei Ablehnung wechselt der gemeldete Wert von "an"
                ' nach "aus", und genau dieser Wechsel stellt den Schalter zurueck.
                _peopleAsking = False
                Me.RaisePropertyChanged(NameOf(PeopleEnabled))
                RaiseModelSelectionChanged()
            End Try
        End Function

        ''' <summary>Die Rueckfrage zum Personenmodell ist gerade offen.</summary>
        Private _peopleAsking As Boolean

        ''' <summary>Nach einem gelungenen Download: ist der Schalter an und das Modell jetzt da,
        ''' die Erkennung einschalten. Erst jetzt, sonst oeffnete der Setter der Einstellungen
        ''' seinen eigenen Download-Dialog.</summary>
        Private Sub EnablePeopleIfReady()
            If Not _peopleChosen OrElse Not IsPeopleModelInstalled OrElse _main.Settings Is Nothing Then Return
            _main.Settings.FaceRecognitionEnabled = True
        End Sub

        ''' <summary>Nach einem Sprachwechsel: die Einstellungen haben ihre Gruppen neu gebaut, mit
        ''' Namen in der neuen Sprache. Die Haken bleiben, wiedererkannt an den Dateien.</summary>
        Private Sub RebuildModelOptionsKeepingSelection()
            Dim chosen As New HashSet(Of String)(
                ModelOptions.Where(Function(o) o.IsSelected).SelectMany(Function(o) o.Group.Files.Select(Function(d) d.Key)))
            BuildModelOptions()
            For Each item In ModelOptions
                If item.Group.Files.Any(Function(d) chosen.Contains(d.Key)) Then item.IsSelected = True
            Next
            RaiseModelSelectionChanged()
        End Sub

        Private Function SelectedMissingFiles() As List(Of AiModelService.ModelEntry)
            Dim files = ModelOptions.Where(Function(o) o.IsSelected AndAlso Not o.IsInstalled).
                SelectMany(Function(o) o.MissingFiles).ToList()
            ' Das Personenmodell ueber seinen eigenen Schalter.
            If _peopleChosen AndAlso _peopleGroup IsNot Nothing AndAlso Not _peopleGroup.IsComplete Then
                files.AddRange(_peopleGroup.Files.Where(Function(d) String.IsNullOrEmpty(AiModelService.BestFile(d.Key))))
            End If
            Return files.GroupBy(Function(d) d.Key).Select(Function(g) g.First()).ToList()
        End Function

        ''' <summary>Was der Download fuer die angehakten Funktionen umfasst, als ein Text.</summary>
        Public ReadOnly Property ModelDownloadSizeText As String
            Get
                Dim files = SelectedMissingFiles()
                If files.Count = 0 Then Return LocalizationService.T("Nichts ausgewählt")
                Return String.Format(LocalizationService.T("Download insgesamt: {0}"),
                                     CardImportService.FormatBytes(files.Sum(Function(d) d.Bytes)))
            End Get
        End Property

        Private _isDownloadingModels As Boolean
        Public Property IsDownloadingModels As Boolean
            Get
                Return _isDownloadingModels
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isDownloadingModels, value)
            End Set
        End Property

        Private _modelDownloadProgress As Double
        Public Property ModelDownloadProgress As Double
            Get
                Return _modelDownloadProgress
            End Get
            Private Set(value As Double)
                Me.RaiseAndSetIfChanged(_modelDownloadProgress, value)
                Me.RaisePropertyChanged(NameOf(ModelDownloadPercentText))
            End Set
        End Property

        Public ReadOnly Property ModelDownloadPercentText As String
            Get
                Return String.Format(CultureInfo.CurrentCulture, "{0:0} %", _modelDownloadProgress * 100)
            End Get
        End Property

        Private _modelDownloadMessage As String = ""
        ''' <summary>Ergebnis des letzten Downloads: fertig oder der Grund, warum nicht.</summary>
        Public Property ModelDownloadMessage As String
            Get
                Return _modelDownloadMessage
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_modelDownloadMessage, If(value, ""))
                Me.RaisePropertyChanged(NameOf(HasModelDownloadMessage))
            End Set
        End Property

        Public ReadOnly Property HasModelDownloadMessage As Boolean
            Get
                Return _modelDownloadMessage.Length > 0
            End Get
        End Property

        Private Sub RaiseModelSelectionChanged()
            Me.RaisePropertyChanged(NameOf(ModelDownloadSizeText))
            ModelDownloadMessage = ""
        End Sub

        ''' <summary>Weiter: auf der Modellseite erst das Angehakte laden und nur bei Erfolg die Seite
        ''' wechseln; scheitert der Download oder wird er abgebrochen, bleibt die Seite mit dem
        ''' Grund stehen. Ohne Auswahl geht es gleich weiter.</summary>
        Private Async Function GoNextAsync() As Task
            Try
                If IsModelsPage AndAlso SelectedMissingFiles().Count > 0 Then
                    Dim download = RunModelDownloadAsync()
                    _modelDownloadTask = download
                    If Not Await download Then Return
                End If
                ShowPage(_index + 1)
            Catch ex As Exception
                DiagnosticLogService.LogException("SetupAssistant.Next", ex)
            End Try
        End Function

        ''' <returns>True, wenn alles Angehakte jetzt da ist.</returns>
        Private Async Function RunModelDownloadAsync() As Task(Of Boolean)
            Dim files = SelectedMissingFiles()
            If files.Count = 0 Then Return True
            If _isDownloadingModels OrElse _main.Settings Is Nothing Then Return False

            ' Keine Rueckfrage hier: die Bedingung des Personenmodells ist schon beim Einschalten
            ' seines Schalters bestaetigt (PeopleEnabled), und nur darueber kommt es in die Auswahl.

            ModelDownloadMessage = ""
            ModelDownloadProgress = 0
            IsDownloadingModels = True
            _modelDownloadCancel = New CancellationTokenSource()
            Dim succeeded = False
            Try
                Dim failure = Await _main.Settings.DownloadModelEntriesAsync(
                    files, Sub(p) ModelDownloadProgress = p, _modelDownloadCancel.Token)
                succeeded = failure Is Nothing
                If succeeded Then
                    EnablePeopleIfReady()
                    ' Galerie und Werkzeuge erfahren, dass neue Modelle da sind.
                    _main.Gallery?.RefreshModelFeatures()
                End If
                ModelDownloadMessage = If(failure, LocalizationService.T("Alle ausgewählten Modelle sind bereit."))
            Catch ex As Exception
                DiagnosticLogService.LogException("SetupAssistant.DownloadModels", ex)
                ModelDownloadMessage = LocalizationService.T("Das Herunterladen ist fehlgeschlagen")
            Finally
                _modelDownloadCancel.Dispose()
                _modelDownloadCancel = Nothing
                IsDownloadingModels = False
                For Each item In ModelOptions
                    item.RefreshState()
                Next
                ' Die Meldung ueberlebt das Nachziehen der Auswahl, das sie sonst leerte.
                Dim message = ModelDownloadMessage
                RaiseModelSelectionChanged()
                ModelDownloadMessage = message
            End Try
            Return succeeded
        End Function

        ''' <summary>Fuer das Schliessen des Assistenten mitten im Download: bricht ab und wartet,
        ''' bis der Lauf wirklich steht.</summary>
        Public Async Function CancelModelDownloadAndWaitAsync() As Task
            _modelDownloadCancel?.Cancel()
            Dim running = _modelDownloadTask
            If running IsNot Nothing Then
                Try
                    Await running
                Catch
                End Try
            End If
        End Function

    End Class

End Namespace
