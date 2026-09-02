Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.ApplicationLifetimes
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform
Imports System.Threading.Tasks
Imports FerrumPix.ViewModels
Imports FerrumPix.Views
Imports FerrumPix.Services

Public Class App
    Inherits Application

    Public Shared AppIcon As WindowIcon

    Private Shared ReadOnly _mpvAvailable As New Lazy(Of Boolean)(
        Function()
            Try
                Return MpvInterop.IsAvailable()
            Catch
                Return False
            End Try
        End Function, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication)

    Public Shared ReadOnly Property IsInlineVideoPlaybackAvailable As Boolean
        Get
            Return _mpvAvailable.Value
        End Get
    End Property

    Public Shared ReadOnly Property IsVideoThumbnailAvailable As Boolean
        Get
            Return _mpvAvailable.Value
        End Get
    End Property

    Public Overrides Sub Initialize()
        AvaloniaXamlLoader.Load(Me)

        ' Microsoft.Data.Sqlite bringt den SQLitePCL-Provider mit, aktiviert ihn aber nicht
        ' automatisch in jeder Veröffentlichungsart. Ohne diese Initialisierung schlugen
        ' Katalog, Ortsnachträge und Hintergrundindex erst beim ersten Datenbankzugriff fehl.
        Try
            SQLitePCL.Batteries_V2.Init()
        Catch ex As Exception
            DiagnosticLogService.LogException("App.SQLiteInit", ex)
        End Try

        ' ERST HIER, nicht vor dem Aufbau: LogToTrace setzt den Empfaenger der Oberflaechenschicht
        ' beim Bauen und wuerde einen frueher gesetzten ersetzen. Mit eingeschaltetem Diagnoselog
        ' schreibt sie danach mit, was sie beim Ziehen und Ablegen tut - die einzige Stelle, die
        ' ueber ihr eigenes Warten Auskunft gibt.
        Try
            If AppSettingsService.Load().EnableDiagnosticLogging Then AvaloniaLogBridge.Install()
        Catch
        End Try
        AppIcon = New WindowIcon(AssetLoader.Open(New Uri("avares://FerrumPix/Assets/FerrumPix_Icon.ico")))

        ' Globales Sicherheitsnetz für Ausnahmen, die NICHT bereits lokal per Try/Catch abgefangen
        ' werden (würden sonst kommentarlos abstürzen bzw. spurlos verschwinden) - nur relevant für
        ' Diagnose, daher wie alle DiagnosticLogService-Aufrufe an den Einstellungen-Schalter
        ' gekoppelt. Verhindert den Absturz selbst NICHT (das ist hier auch nicht das Ziel), sichert
        ' aber den Stacktrace, bevor der Prozess endet.
        AddHandler AppDomain.CurrentDomain.UnhandledException,
            Sub(sender, e) DiagnosticLogService.LogException("UnhandledException", TryCast(e.ExceptionObject, Exception))
        AddHandler TaskScheduler.UnobservedTaskException,
            Sub(sender, e)
                DiagnosticLogService.LogException("UnobservedTaskException", e.Exception)
                e.SetObserved()
            End Sub
    End Sub

    ''' <summary>Ein Expander erzeugt seinen Inhalt erst beim Aufklappen. Der Durchlauf ueber das
    ''' Fenster hat ihn dann schon hinter sich, also wird hier nachgezogen - EIN Klassen-Handler fuer
    ''' alle Expander der Anwendung, statt eines Ereignisses je Panel.</summary>
    Private Shared Sub LokalisiereNachgeladenes()
        Avalonia.Controls.Expander.ExpandedEvent.AddClassHandler(Of Avalonia.Controls.Expander)(
            Sub(expander, e)
                Services.LocalizationService.ApplyToVisualTree(expander)
            End Sub)
    End Sub

    Public Overrides Sub OnFrameworkInitializationCompleted()
        LokalisiereNachgeladenes()
        If TypeOf ApplicationLifetime Is IClassicDesktopStyleApplicationLifetime Then
            Dim desktop = CType(ApplicationLifetime, IClassicDesktopStyleApplicationLifetime)

            Dim initialImagePath As String = Nothing
            Dim args = desktop.Args
            If args IsNot Nothing AndAlso args.Length > 0 Then
                If IO.File.Exists(args(0)) Then
                    initialImagePath = args(0)
                End If
            End If

            LocalizationService.LanguageMode = AppSettingsService.Load().LanguageMode
            ' Zeichentablett-Modus VOR dem ersten Fenster anhaengen: die Stile setzen ClickMode, und
            ' ein spaeter angehaengter Stil muesste alles noch einmal durchstilen.
            TabletInputService.Apply(AppSettingsService.Load().TabletMode)
            ' EINMAL beim Start nachsehen, welche gelernten Modelle vorliegen. Danach steht fuer
            ' diese Sitzung fest, welche Funktionen es gibt; was fehlt, blendet die Oberflaeche aus.
            ' Vor dem ViewModel, damit dessen Bindungen schon den richtigen Stand sehen.
            AiModelService.CheckInventory()
            Dim vm = New MainWindowViewModel(initialImagePath)
            Dim win = New MainWindow()
            win.DataContext = vm
            desktop.MainWindow = win

            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose
        End If

        MyBase.OnFrameworkInitializationCompleted()
    End Sub

    Public Shared Sub ApplyIcon(win As Window)
        If AppIcon IsNot Nothing Then win.Icon = AppIcon
    End Sub
End Class
