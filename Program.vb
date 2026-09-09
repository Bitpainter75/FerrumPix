Imports Avalonia
Imports Avalonia.X11
Imports FerrumPix.Services
Imports ReactiveUI.Avalonia

Module Program
    ''' <summary>Startparameter, der das Protokoll fuer diesen Lauf einschaltet.</summary>
    Private Const DebugSwitch As String = "--debug"

    <STAThread>
    Function Main(args As String()) As Integer
        ' DER PROTOKOLLSCHALTER ZUERST, noch vor allem anderen. Er ist fuer den Fall gebaut, in dem
        ' die Anwendung ueberhaupt nicht hochkommt: das Protokoll liegt ab Werk aus und laesst sich
        ' nur IN der Anwendung einschalten - wer sie nicht starten kann, kaeme also nie an eines.
        ' Genau daran ist eine Fehlersuche unter Windows 10 schon einmal haengengeblieben.
        Dim debugRequested = args IsNot Nothing AndAlso
                             args.Any(Function(a) String.Equals(a, DebugSwitch, StringComparison.OrdinalIgnoreCase))
        If debugRequested Then DiagnosticLogService.ForceEnable()

        ' Der Riegel gegen die Telemetrie der Modelllaufzeit. Er wirkt nur, solange noch keine
        ' ORT-Umgebung entstanden ist, deshalb steht er hier und nicht dort, wo die Laufzeit zum
        ' ersten Mal gebraucht wird. Begruendung und Messung stehen bei
        ' AiModelService.SuppressRuntimeTelemetry.
        AiModelService.SuppressRuntimeTelemetry()
        AppSettingsService.ApplyApplicationScaleEnvironment()

        ' DER ABSTURZFANG GEHOERT HIERHER, nicht erst in die Anwendungsklasse. Dort wird er erst
        ' angemeldet, wenn Avalonia schon steht - ein Absturz beim Aufbau des Toolkits (fehlende
        ' Grafikbibliothek, fehlende Laufzeit) faellt vorher und hinterliesse keine Spur.
        AddHandler AppDomain.CurrentDomain.UnhandledException,
            Sub(sender, e) DiagnosticLogService.LogException("Start.UnhandledException",
                                                            TryCast(e.ExceptionObject, Exception))

        ' Build-Marker: beim Auswerten von Logs/Stacktraces muss zweifelsfrei erkennbar sein, WELCHER
        ' Build lief - mehrere Meldungen stammten unbemerkt aus einem veralteten Binary, und die
        ' Analyse jagte Geister.
        Try
            Dim asmPath = Reflection.Assembly.GetExecutingAssembly().Location
            Dim buildUtc = If(String.IsNullOrEmpty(asmPath), Date.MinValue, IO.File.GetLastWriteTimeUtc(asmPath))
            DiagnosticLogService.LogAlways("App.Start",
                $"buildUtc={buildUtc:yyyy-MM-dd HH:mm:ss}Z pid={Environment.ProcessId}")
            If debugRequested Then LogSystemInfo()
        Catch
        End Try

        Return BuildAvaloniaApp().StartWithClassicDesktopLifetime(ForwardedArguments(args))
    End Function

    ''' <summary>Die Argumente ohne den Protokollschalter.
    '''
    ''' Er darf NICHT durchgereicht werden: die Anwendung nimmt das erste Argument als zu oeffnendes
    ''' Bild bzw. als Ordner (siehe App.axaml.vb). Stuende der Schalter davor, waere er ein Pfad, den
    ''' es nicht gibt - und der Aufruf "FerrumPix.exe --debug Bild.jpg" oeffnete das Bild nicht mehr,
    ''' also genau in dem Moment, in dem jemand einen Fehler an einer bestimmten Datei sucht.</summary>
    Friend Function ForwardedArguments(args As String()) As String()
        If args Is Nothing Then Return Array.Empty(Of String)()
        Return args.Where(Function(a) Not String.Equals(a, DebugSwitch, StringComparison.OrdinalIgnoreCase)).ToArray()
    End Function

    ''' <summary>Was auf diesem Rechner laeuft - eine Zeile, die in einen Fehlerbericht passt.
    '''
    ''' OHNE Benutzer- und Rechnernamen und ohne den vollen Programmpfad: die Zeile wird in einen
    ''' oeffentlichen Fehlerbericht kopiert, und dort gehoert nichts hinein, was jemanden benennt.
    ''' Vom Ablageort bleibt deshalb nur, was diagnostisch zaehlt: die Art des Laufwerks. Ein
    ''' Programm auf einem Wechseldatentraeger kann von Windows wortlos am Start gehindert werden,
    ''' und das sieht von aussen aus wie ein Absturz.</summary>
    Private Sub LogSystemInfo()
        Try
            Dim asm = Reflection.Assembly.GetExecutingAssembly()
            Dim version = asm.GetName().Version?.ToString()
            Dim baseDir = AppContext.BaseDirectory
            Dim driveKind = "unbekannt"
            Try
                Dim root = IO.Path.GetPathRoot(baseDir)
                If Not String.IsNullOrEmpty(root) Then
                    Dim drive = New IO.DriveInfo(root)
                    driveKind = $"{drive.DriveType}/{drive.DriveFormat}"
                End If
            Catch
            End Try

            DiagnosticLogService.LogAlways("App.System",
                $"version={version} os={Runtime.InteropServices.RuntimeInformation.OSDescription} " &
                $"osArch={Runtime.InteropServices.RuntimeInformation.OSArchitecture} " &
                $"procArch={Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} " &
                $"framework={Runtime.InteropServices.RuntimeInformation.FrameworkDescription} " &
                $"cores={Environment.ProcessorCount} drive={driveKind} " &
                $"culture={Globalization.CultureInfo.CurrentUICulture.Name}")
        Catch ex As Exception
            DiagnosticLogService.LogException("App.System", ex)
        End Try
    End Sub

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
