Imports Avalonia
Imports Avalonia.X11
Imports FerrumPix.Services
Imports ReactiveUI.Avalonia

Module Program
    <STAThread>
    Function Main(args As String()) As Integer
        ' GANZ ZUERST, vor allem anderen: der Riegel gegen die Telemetrie der Modelllaufzeit. Er
        ' wirkt nur, solange noch keine ORT-Umgebung entstanden ist, deshalb steht er hier und
        ' nicht dort, wo die Laufzeit zum ersten Mal gebraucht wird. Begruendung und Messung
        ' stehen bei AiModelService.SuppressRuntimeTelemetry.
        AiModelService.SuppressRuntimeTelemetry()
        AppSettingsService.ApplyApplicationScaleEnvironment()
        ' Build-Marker (ungated): beim Auswerten von Logs/Stacktraces muss zweifelsfrei erkennbar
        ' sein, WELCHER Build lief - mehrere Meldungen stammten unbemerkt aus einem
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
        Dim builder = AppBuilder.Configure(Of App)().
            UsePlatformDetect().
            UseReactiveUI(AddressOf ConfigureReactiveUI).
            LogToTrace().
            With(New X11PlatformOptions With {.UseDBusMenu = False})

        ' Nur macOS, und nur wenn jemand ausdrücklich einen Weg wählt: die Wahl gilt VOR dem Aufbau
        ' des Toolkits, deshalb steht sie hier und nicht in den Einstellungen. Was sie soll, steht
        ' an AppSettings.MacRenderingMode.
        Dim renderingMode = MacRenderingMode()
        If renderingMode IsNot Nothing Then
            builder = builder.With(New AvaloniaNativePlatformOptions With {.RenderingMode = renderingMode})
        End If
        Return builder
    End Function

    ''' <summary>Die gewählte Reihenfolge der Zeichenwege, oder Nothing für die des Toolkits.
    '''
    ''' Der gewählte Weg steht VORN, die übrigen bleiben als Rückfall dahinter: eine Wahl, die
    ''' auf dem Gerät nicht trägt, darf ein schwarzes Fenster nicht zur Folge haben. Bei "Software"
    ''' gibt es nichts darunter, das ist der Boden.</summary>
    Private Function MacRenderingMode() As IReadOnlyList(Of AvaloniaNativeRenderingMode)
        If Not OperatingSystem.IsMacOS() Then Return Nothing
        Try
            Select Case AppSettingsService.NormalizeMacRenderingMode(AppSettingsService.Load().MacRenderingMode)
                Case "Metal"
                    Return {AvaloniaNativeRenderingMode.Metal,
                            AvaloniaNativeRenderingMode.OpenGl,
                            AvaloniaNativeRenderingMode.Software}
                Case "OpenGl"
                    Return {AvaloniaNativeRenderingMode.OpenGl,
                            AvaloniaNativeRenderingMode.Software}
                Case "Software"
                    Return {AvaloniaNativeRenderingMode.Software}
                Case Else
                    Return Nothing
            End Select
        Catch ex As Exception
            DiagnosticLogService.LogException("App.Start", ex)
            Return Nothing
        End Try
    End Function

    ''' Seit Avalonia 12 kommt die ReactiveUI-Anbindung aus dem Paket ReactiveUI.Avalonia, und
    ''' UseReactiveUI verlangt einen Rückruf zum Einrichten des ReactiveUI-Builders. FerrumPix nutzt
    ''' nur ReactiveObject/ReactiveCommand und braucht dort nichts zu konfigurieren.
    Private Sub ConfigureReactiveUI(rxBuilder As ReactiveUI.Builder.ReactiveUIBuilder)
    End Sub
End Module
