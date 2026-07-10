Imports Avalonia
Imports Avalonia.X11
Imports FerrumPix.Services
Imports ReactiveUI.Avalonia

Module Program
    <STAThread>
    Function Main(args As String()) As Integer
        AppSettingsService.ApplyApplicationScaleEnvironment()
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
