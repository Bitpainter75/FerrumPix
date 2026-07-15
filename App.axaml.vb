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

    Public Overrides Sub OnFrameworkInitializationCompleted()
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
