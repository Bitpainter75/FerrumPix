Imports Avalonia
Imports Avalonia.X11
Imports FerrumPix.Services
Imports ReactiveUI.Avalonia

Module Program
    ''' <summary>Startparameter, der das Protokoll fuer diesen Lauf einschaltet.</summary>
    Private Const DebugSwitch As String = "--debug"

    ''' <summary>Die Konsole des AUFRUFENDEN Prozesses, nicht die einer bestimmten Kennung.</summary>
    Private Const AttachParentProcess As UInteger = &HFFFFFFFFUI

    ' Als Declare und nicht ueber DllImport: die Bibliothek wird damit erst beim AUFRUF gesucht,
    ' und der steht hinter der Windows-Abfrage in AttachDebugConsole. Unter Linux und macOS wird
    ' kernel32 deshalb nie angefasst.
    Private Declare Function AttachConsole Lib "kernel32.dll" (processId As UInteger) As Boolean
    Private Declare Function AllocConsole Lib "kernel32.dll" () As Boolean

    ''' <summary>Die Konsole haben WIR geoeffnet, sie gehoert keinem Aufrufer. Entscheidet allein
    ''' darueber, ob am Ende auf eine Taste gewartet wird.</summary>
    Private _ownsConsole As Boolean

    <STAThread>
    Function Main(args As String()) As Integer
        ' DER PROTOKOLLSCHALTER ZUERST, noch vor allem anderen. Er ist fuer den Fall gebaut, in dem
        ' die Anwendung ueberhaupt nicht hochkommt: das Protokoll liegt ab Werk aus und laesst sich
        ' nur IN der Anwendung einschalten - wer sie nicht starten kann, kaeme also nie an eines.
        ' Genau daran ist eine Fehlersuche unter Windows 10 schon einmal haengengeblieben.
        Dim debugRequested = args IsNot Nothing AndAlso
                             args.Any(Function(a) String.Equals(a, DebugSwitch, StringComparison.OrdinalIgnoreCase))
        If debugRequested Then
            DiagnosticLogService.ForceEnable()
            AttachDebugConsole()
            EchoStartupBanner()
        End If

        ' Der Riegel gegen die Telemetrie der Modelllaufzeit. Er wirkt nur, solange noch keine
        ' ORT-Umgebung entstanden ist, deshalb steht er hier und nicht dort, wo die Laufzeit zum
        ' ersten Mal gebraucht wird. Begruendung und Messung stehen bei
        ' AiModelService.SuppressRuntimeTelemetry.
        AiModelService.SuppressRuntimeTelemetry()
        AppSettingsService.ApplyApplicationScaleEnvironment()

        ' DIE BEIDEN SICHERHEITSNETZE GEHOEREN HIERHER, nicht in die Anwendungsklasse. Dort werden
        ' sie erst angemeldet, wenn Avalonia schon steht - ein Absturz beim Aufbau des Toolkits
        ' (fehlende Grafikbibliothek, fehlende Laufzeit) faellt vorher und hinterliesse keine Spur.
        '
        ' UND NUR HIER: eine zweite Anmeldung in der Anwendungsklasse schriebe jede Ausnahme, die
        ' nach dem Hochlauf faellt, doppelt in errors.log. Sie fangen den Absturz nicht ab, das ist
        ' nicht ihr Zweck - sie sichern den Stacktrace, bevor der Prozess endet.
        AddHandler AppDomain.CurrentDomain.UnhandledException, AddressOf OnUnhandledException
        AddHandler TaskScheduler.UnobservedTaskException,
            Sub(sender, e)
                DiagnosticLogService.LogException("UnobservedTaskException", e.Exception)
                e.SetObserved()
            End Sub

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

        Dim exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(ForwardedArguments(args))
        ' DIE LETZTE ZEILE IST SELBST EIN BEFUND: kommt sie, hat der Aufbau des Toolkits gehalten
        ' und die Anwendung ist geordnet zu Ende gegangen. Bleibt sie aus, endete der Prozess
        ' vorher - und der Unterschied ist genau der, den ein Fehlerbericht sonst nicht hergibt.
        EchoLine($"FerrumPix exited with code {exitCode}.")
        WaitBeforeClosingOwnConsole()
        Return exitCode
    End Function

    ''' <summary>Das Sicherheitsnetz fuer alles, was niemand gefangen hat.
    '''
    ''' EINE BENANNTE METHODE UND KEIN LAMBDA, damit sie messbar ist: der Prueffall ruft sie mit
    ''' einer Ausnahme und <c>isTerminating</c> auf und sieht nach, ob gewartet wird. Als Lambda in
    ''' Main waere genau der Fehlerfall der einzige, den keine Pruefung je betritt.
    '''
    ''' <para>DAS WARTEN STEHT HIER, nicht nur am Ende von Main: bei einem Absturz endet der Prozess
    ''' direkt nach diesem Handler, der Stapel wird nicht abgewickelt, und kein Finally laeuft mehr.
    ''' Eine selbst geoeffnete Konsole verschwaende sonst in dem Augenblick, fuer den sie gebaut
    ''' ist.</para>
    '''
    ''' <para>Nur bei <c>IsTerminating</c>. Eine Ausnahme, nach der die Laufzeit weiterlaeuft, darf
    ''' den Faden nicht an einer Eingabeaufforderung festhalten.</para></summary>
    Friend Sub OnUnhandledException(sender As Object, e As UnhandledExceptionEventArgs)
        DiagnosticLogService.LogException("UnhandledException", TryCast(e.ExceptionObject, Exception))
        If e IsNot Nothing AndAlso e.IsTerminating Then WaitBeforeClosingOwnConsole()
    End Sub

    ''' <summary>Haelt eine selbst geoeffnete Konsole offen, bis jemand die Eingabetaste drueckt.
    '''
    ''' NUR fuer die eigene: eine uebernommene gehoert der PowerShell, und die dort auf eine Taste
    ''' warten zu lassen waere eine Zumutung fuer den, der weiterarbeiten will.
    '''
    ''' GENAU EINMAL: der Merker faellt vor dem Warten. Ein Absturz waehrend des Beendens liefe sonst
    ''' durch den Handler UND durch das Ende von Main, und die zweite Aufforderung stuende da, ohne
    ''' dass jemand wuesste, worauf sie wartet.</summary>
    Friend Sub WaitBeforeClosingOwnConsole()
        If Not _ownsConsole Then Return
        _ownsConsole = False
        Try
            Console.Out.WriteLine("Press Enter to close this window.")
            Console.In.ReadLine()
        Catch
        End Try
    End Sub

    ''' <summary>Windows: eine Konsole beschaffen, damit der Protokollschalter etwas ZEIGT.
    '''
    ''' Die Anwendung ist eine Fensteranwendung (WinExe) und haengt deshalb an keiner Konsole: aus
    ''' PowerShell gestartet kehrt der Aufruf sofort zurueck, ohne eine Zeile zu hinterlassen. Genau
    ''' das kam in einem Fehlerbericht als "es passiert gar nichts" an, waehrend niemand sagen
    ''' konnte, ob das Programm angelaufen war.
    '''
    ''' ZWEI WEGE, in dieser Reihenfolge: hat der Aufrufer eine Konsole (PowerShell, cmd), wird sie
    ''' uebernommen und die Ausgabe steht in demselben Fenster, in dem der Befehl steht. Sonst
    ''' (Doppelklick, Verknuepfung) wird eine eigene geoeffnet - ohne sie saehe wieder niemand etwas.
    '''
    ''' Danach MUSS die Standardausgabe neu gebunden werden: die Laufzeit merkt sich beim ersten
    ''' Zugriff, wohin sie schreibt, und das war bis hierher das Nichts.
    '''
    ''' Nur unter Windows. Linux und macOS geben die Ausgabe des Terminals ohnehin weiter.</summary>
    Private Sub AttachDebugConsole()
        If Not OperatingSystem.IsWindows() Then Return
        Try
            If Not AttachConsole(AttachParentProcess) Then
                ' Eine SELBST geoeffnete Konsole gehoert uns, und sie verschwindet mit dem Prozess.
                ' Wer per Doppelklick startet, saehe die Ausgabe sonst fuer den Bruchteil einer
                ' Sekunde - also fuer denselben Nutzer nichts, fuer den sie gebaut ist.
                '
                ' DER MERKER KOMMT AUS DEM RUECKGABEWERT und wird nicht daneben gesetzt: auch
                ' AllocConsole kann scheitern (der Prozess haengt schon an einer Konsole, das System
                ' gibt keine her). Dann gehoert uns keine - und am Ende auf eine Eingabe zu warten
                ' hiesse, ein fremdes Fenster festzuhalten oder auf eine Taste zu warten, die
                ' niemand sieht.
                _ownsConsole = AllocConsole()
            End If
            Dim writer = New IO.StreamWriter(Console.OpenStandardOutput())
            writer.AutoFlush = True
            Console.SetOut(writer)
            ' Ohne das stehen Umlaute aus dem Protokoll in der Windows-Konsole als Fragezeichen.
            Try
                Console.OutputEncoding = Text.Encoding.UTF8
            Catch
            End Try
        Catch
            ' Keine Konsole zu bekommen ist kein Grund, den Start abzubrechen - das Protokoll
            ' schreibt weiterhin in seine Datei.
        End Try
    End Sub

    ''' <summary>Was am Anfang auf der Konsole steht: die Fassung und WO die Protokolldatei liegt.
    '''
    ''' Der Pfad gehoert hierher, weil er unter Windows in einem versteckten Ordner endet. Im
    ''' Fehlerbericht, der diesen Schalter ausgeloest hat, suchte der Melder ihn vergeblich im
    ''' Benutzerordner - er war da, nur unsichtbar.</summary>
    Private Sub EchoStartupBanner()
        Try
            Dim version = Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString()
            EchoLine($"FerrumPix {version} starting with {DebugSwitch}.")
            EchoLine($"log file: {IO.Path.Combine(DiagnosticLogService.LogFolder, "diagnostics.log")}")
        Catch
        End Try
    End Sub

    ''' <summary>Eine Zeile auf die Konsole, aber nur beim erzwungenen Protokoll. Dieselbe Regel wie
    ''' bei der Spiegelung der Protokollzeilen: ohne den Schalter schreibt die Anwendung nichts.</summary>
    Private Sub EchoLine(text As String)
        If Not DiagnosticLogService.IsForcedOn Then Return
        Try
            Console.Out.WriteLine(text)
        Catch
        End Try
    End Sub

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
