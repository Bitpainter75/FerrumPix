Imports Avalonia
Imports Avalonia.ReactiveUI
Imports Avalonia.X11
Imports FerrumPix.Services

Module Program
    <STAThread>
    Function Main(args As String()) As Integer
        AppSettingsService.ApplyApplicationScaleEnvironment()
        Return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)
    End Function

    Function BuildAvaloniaApp() As AppBuilder
        Return AppBuilder.Configure(Of App)().
            UsePlatformDetect().
            UseReactiveUI().
            LogToTrace().
            With(New X11PlatformOptions With {.UseDBusMenu = False})
    End Function
End Module
