Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.ApplicationLifetimes
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform
Imports FerrumPix.ViewModels
Imports FerrumPix.Views
Imports FerrumPix.Services

Public Class App
    Inherits Application

    Public Shared AppIcon As WindowIcon

    ''' Ob LibVLC erfolgreich initialisiert werden konnte - auf Linux ist libvlc eine vom Nutzer
    ''' separat zu installierende Systemabhängigkeit (kein Bundling wie unter Windows), daher muss
    ''' jede Video-Funktion (Thumbnails, Wiedergabe) hierauf prüfen und sauber degradieren statt
    ''' abzustürzen, wenn VLC nicht installiert ist. Lazy statt beim Start berechnet, damit
    ''' Core.Initialize() (misst spürbar CPU-Zeit) nur läuft, wenn tatsächlich ein Video-Thumbnail
    ''' oder eine Wiedergabe angefragt wird, statt bei jedem App-Start unabhängig vom Nutzer.
    ''' Lazy(Of Boolean) übernimmt Thread-Sicherheit, da der erste Zugriff auch von einem
    ''' Thumbnail-Hintergrund-Worker (VideoPreviewService) statt vom UI-Thread kommen kann.
    Private Shared ReadOnly _videoPlaybackAvailable As New Lazy(Of Boolean)(
        Function()
            Try
                LibVLCSharp.Shared.Core.Initialize()
                Return True
            Catch
                Return False
            End Try
        End Function, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication)

    Public Shared ReadOnly Property IsVideoPlaybackAvailable As Boolean
        Get
            Return _videoPlaybackAvailable.Value
        End Get
    End Property

    Public Overrides Sub Initialize()
        AvaloniaXamlLoader.Load(Me)
        AppIcon = New WindowIcon(AssetLoader.Open(New Uri("avares://FerrumPix/Assets/FerrumPix_Icon.ico")))
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

            ' VideoPreviewService (Video-Thumbnails) erzeugt bei Bedarf ein unsichtbares 1x1px-
            ' Hilfsfenster mit eigenem MediaPlayer, das nie explizit geschlossen wird - mit dem
            ' Default OnLastWindowClose würde die App dadurch nach Schließen des Hauptfensters
            ' unsichtbar im Hintergrund weiterlaufen (nie beendender Prozess), sobald einmal ein
            ' Video-Thumbnail erzeugt wurde. OnMainWindowClose beendet den Prozess unabhängig
            ' davon, sobald das eigentliche Hauptfenster schließt.
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose
        End If

        MyBase.OnFrameworkInitializationCompleted()
    End Sub

    Public Shared Sub ApplyIcon(win As Window)
        If AppIcon IsNot Nothing Then win.Icon = AppIcon
    End Sub
End Class
