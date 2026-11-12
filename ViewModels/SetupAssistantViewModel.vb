Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Linq
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Ein Punkt der Fortschrittsanzeige unten im Assistenten.</summary>
    Public Class SetupAssistantStep
        Inherits ReactiveObject

        Private _isCurrent As Boolean
        Public Property IsCurrent As Boolean
            Get
                Return _isCurrent
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isCurrent, value)
            End Set
        End Property

        Private _isDone As Boolean
        Public Property IsDone As Boolean
            Get
                Return _isDone
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isDone, value)
            End Set
        End Property
    End Class

    ''' <summary>
    ''' Der Einrichtungsassistent beim ersten Start: Willkommen und Sprache, Darstellung, Modelle,
    ''' Grafikkarte (nur wenn eine erkannt wird) und zum Schluss der Hinweis auf die Einstellungen.
    '''
    ''' Er hat KEINEN eigenen Zustand fuer die Einstellungen. Jede Seite bindet direkt an das
    ''' SettingsViewModel, dessen Setter sofort wirken und speichern; der Assistent ist nur eine
    ''' freundlichere Oberflaeche darueber. Deshalb gibt es auch nichts zurueckzunehmen, wenn man
    ''' ihn ueberspringt: was gewaehlt ist, gilt. Ob er erscheint, merkt sich
    ''' AppSettings.SetupAssistantDone (siehe MainWindowViewModel, ShowSetupAssistant).
    ''' </summary>
    Partial Public Class SetupAssistantViewModel
        Inherits ReactiveObject

        Public Const PageWelcome As String = "Welcome"
        Public Const PageAppearance As String = "Appearance"
        Public Const PageModels As String = "Models"
        Public Const PageGpu As String = "Gpu"
        Public Const PageDone As String = "Done"

        Private ReadOnly _main As MainWindowViewModel
        Private ReadOnly _pages As List(Of String)
        Private _index As Integer

        Public ReadOnly Property Steps As New ObservableCollection(Of SetupAssistantStep)()

        Public ReadOnly Property NextCommand As ICommand
        Public ReadOnly Property BackCommand As ICommand
        Public ReadOnly Property SkipCommand As ICommand
        Public ReadOnly Property FinishCommand As ICommand
        Public ReadOnly Property OpenSettingsCommand As ICommand

        Public Sub New(main As MainWindowViewModel)
            _main = main
            _pages = New List(Of String) From {PageWelcome, PageAppearance, PageModels}
            ' Die Seite zur Grafikkarte nur, wenn es eine geben kann: ohne Karte stuenden dort zwei
            ' Schalter, die nichts bewirken. Abgefragt wird hier aber nichts (HasGpu).
            If HasGpu Then _pages.Add(PageGpu)
            _pages.Add(PageDone)
            For i = 0 To _pages.Count - 1
                Steps.Add(New SetupAssistantStep())
            Next
            ' Weiter auf der Modellseite laedt erst das Angehakte und wechselt erst danach; als
            ' Befehl mit Task ist der Knopf waehrenddessen von selbst gesperrt.
            NextCommand = ReactiveCommand.CreateFromTask(AddressOf GoNextAsync)
            BackCommand = ReactiveCommand.Create(Sub() ShowPage(_index - 1))
            SkipCommand = ReactiveCommand.Create(Sub() _main.CloseSetupAssistant(openSettings:=False))
            FinishCommand = ReactiveCommand.Create(Sub() _main.CloseSetupAssistant(openSettings:=False))
            OpenSettingsCommand = ReactiveCommand.Create(Sub() _main.CloseSetupAssistant(openSettings:=True))
            CancelModelDownloadCommand = ReactiveCommand.Create(Sub() _modelDownloadCancel?.Cancel())
            BuildModelOptions()
            UpdateSteps()
        End Sub

        ''' <summary>Das Fenster selbst, fuer die Logos je Thema.</summary>
        Public ReadOnly Property Main As MainWindowViewModel
            Get
                Return _main
            End Get
        End Property

        Public ReadOnly Property Settings As SettingsViewModel
            Get
                Return _main.Settings
            End Get
        End Property

        ''' <summary>Kann es eine Karte geben? Der Assistent laeuft beim ersten Start, und die
        ''' Kartenabfrage laedt Laufzeit und Grafiktreiber; genau dort stuerzte FerrumPix unter
        ''' Windows 10 22H2 ab. Sind die Karten schon abgefragt, zaehlt das Ergebnis; sonst wird die
        ''' Seite gezeigt, und erst ihre Schalter fragen die Karte ab (beim Einschalten).</summary>
        Public ReadOnly Property HasGpu As Boolean
            Get
                If GpuAccelerationService.IsChecked Then Return GpuAccelerationService.Devices.Count > 0
                Return True
            End Get
        End Property

        ''' <summary>Die erkannten Karten in einer Zeile, fuer die Seite zur Grafikkarte. Leer, solange
        ''' sie nicht abgefragt sind; dann bleibt die Zeile weg (HasGpuNames).</summary>
        Public ReadOnly Property GpuNames As String
            Get
                If _main.Settings Is Nothing Then Return ""
                Return String.Join(", ", _main.Settings.GpuDeviceOptions.Where(Function(o) Not String.IsNullOrEmpty(o.Key)).Select(Function(o) o.Name))
            End Get
        End Property

        Public ReadOnly Property HasGpuNames As Boolean
            Get
                Return GpuNames.Length > 0
            End Get
        End Property

        Private ReadOnly Property CurrentPage As String
            Get
                Return _pages(_index)
            End Get
        End Property

        Public ReadOnly Property IsWelcomePage As Boolean
            Get
                Return CurrentPage = PageWelcome
            End Get
        End Property

        Public ReadOnly Property IsAppearancePage As Boolean
            Get
                Return CurrentPage = PageAppearance
            End Get
        End Property

        Public ReadOnly Property IsModelsPage As Boolean
            Get
                Return CurrentPage = PageModels
            End Get
        End Property

        Public ReadOnly Property IsGpuPage As Boolean
            Get
                Return CurrentPage = PageGpu
            End Get
        End Property

        Public ReadOnly Property IsDonePage As Boolean
            Get
                Return CurrentPage = PageDone
            End Get
        End Property

        Public ReadOnly Property CanGoBack As Boolean
            Get
                Return _index > 0
            End Get
        End Property

        ''' <summary>Weiter und Ueberspringen stehen auf jeder Seite ausser der letzten; dort
        ''' fuehren zwei eigene Knoepfe hinaus.</summary>
        Public ReadOnly Property ShowsNext As Boolean
            Get
                Return Not IsDonePage
            End Get
        End Property

        ''' <summary>Die erste Seite laedt ein, die anderen fuehren weiter.</summary>
        Public ReadOnly Property NextText As String
            Get
                Return If(IsWelcomePage, LocalizationService.T("Loslegen"), LocalizationService.T("Weiter"))
            End Get
        End Property

        Private Sub ShowPage(index As Integer)
            If index < 0 OrElse index >= _pages.Count OrElse index = _index Then Return
            _index = index
            UpdateSteps()
            For Each name In {NameOf(IsWelcomePage), NameOf(IsAppearancePage), NameOf(IsModelsPage),
                              NameOf(IsGpuPage), NameOf(IsDonePage), NameOf(CanGoBack), NameOf(ShowsNext),
                              NameOf(NextText), NameOf(GpuNames), NameOf(HasGpuNames)}
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        Private Sub UpdateSteps()
            For i = 0 To Steps.Count - 1
                Steps(i).IsCurrent = (i = _index)
                Steps(i).IsDone = (i < _index)
            Next
        End Sub

        ''' <summary>Nach einem Sprachwechsel: die Texte, die das ViewModel selbst liefert.</summary>
        Public Sub RefreshTexts()
            Me.RaisePropertyChanged(NameOf(NextText))
            RebuildModelOptionsKeepingSelection()
        End Sub
    End Class

End Namespace
