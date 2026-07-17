Imports Avalonia
Imports Avalonia.X11
Imports FerrumPix.Services
Imports ReactiveUI.Avalonia

Module Program
    <STAThread>
    Function Main(args As String()) As Integer
        AppSettingsService.ApplyApplicationScaleEnvironment()
        ' Build-Marker (ungated): beim Auswerten von Logs/Stacktraces muss zweifelsfrei erkennbar
        ' sein, WELCHER Build lief - mehrere Befunde am 2026-07-16 stammten unbemerkt aus einem
        ' veralteten Binary, und die Analyse jagte Geister.
        Try
            Dim asmPath = Reflection.Assembly.GetExecutingAssembly().Location
            Dim buildUtc = If(String.IsNullOrEmpty(asmPath), Date.MinValue, IO.File.GetLastWriteTimeUtc(asmPath))
            DiagnosticLogService.LogAlways("App.Start",
                $"buildUtc={buildUtc:yyyy-MM-dd HH:mm:ss}Z pid={Environment.ProcessId}")
        Catch
        End Try
        Return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)
    End Function

    Function BuildAvaloniaApp() As AppBuilder
        Return AppBuilder.Configure(Of App)().
            UsePlatformDetect().
            UseReactiveUI(AddressOf ConfigureReactiveUI).
            LogToTrace().
            With(New X11PlatformOptions With {.UseDBusMenu = False})
    End Function

    ''' Seit Avalonia 12 kommt die ReactiveUI-Anbindung aus dem Paket ReactiveUI.Avalonia, und
    ''' UseReactiveUI verlangt einen Rückruf zum Einrichten des ReactiveUI-Builders. FerrumPix nutzt
    ''' nur ReactiveObject/ReactiveCommand und braucht dort nichts zu konfigurieren.
    Private Sub ConfigureReactiveUI(rxBuilder As ReactiveUI.Builder.ReactiveUIBuilder)
    End Sub
End Module
