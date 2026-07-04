Imports Avalonia
Imports Avalonia.ReactiveUI
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
            LogToTrace()
    End Function
End Module
