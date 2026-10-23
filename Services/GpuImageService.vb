Imports System
Imports System.Collections.Concurrent
Imports System.Diagnostics
Imports System.Reflection
Imports System.Threading
Imports Avalonia
Imports Avalonia.OpenGL
Imports Avalonia.Platform
Imports SkiaSharp

Namespace Services

    ''' <summary>Die Grafikkarte fuer die Bildbearbeitung: EIN eigener Faden mit EINEM eigenen
    ''' OpenGL-Kontext und einem Skia-GRContext darauf.
    '''
    ''' WARUM EIN EIGENER KONTEXT: Avalonia gibt seinen Skia-Kontext nur waehrend des Zeichnens im
    ''' Fenster heraus. Export und Stapel laufen aber im Hintergrund. Avalonias Plattformgrafik
    ''' erzeugt auf Zuruf einen eigenen Kontext (<c>IPlatformGraphics.CreateContext</c>, unter X11
    ''' GLX), unabhaengig vom Fenster. Ein OpenGL-Kontext gehoert dem Faden, der ihn aktuell macht -
    ''' darum laeuft alles, was ihn braucht, auf genau diesem einen Faden (<see cref="TryRun"/>).
    ''' Probe 2026-10-22 an einer RTX 5060: Kontext 30 ms, ein Shader ueber 33 MP 1 bis 2 ms.
    '''
    ''' WANN ER NICHT GEHT, faellt der Aufrufer auf den Prozessor zurueck: keine Plattformgrafik
    ''' (Kopflos, Software), kein OpenGL-Kontext (etwa Metal unter macOS), kein GRContext, ein
    ''' verlorenes Geraet. Der Grund steht in <see cref="FailureReason"/>, und ein Fehlschlag geht
    ''' IMMER in die Fehlerdatei (LogException) - auch ohne eingeschaltete Diagnose, denn genau dann
    ''' sucht jemand, warum sein Schalter nichts tut. Ablauf und Zeiten stehen nur im
    ''' Diagnoseprotokoll (Bereich "GpuImage").</summary>
    Public NotInheritable Class GpuImageService

        ''' <summary>Ein Fehlschlag des GPU-Wegs, mit dem Grund im Text - so steht er in der
        ''' Fehlerdatei, wo jemand ihn sucht.</summary>
        Public NotInheritable Class GpuUnavailableException
            Inherits Exception
            Public Sub New(message As String, Optional inner As Exception = Nothing)
                MyBase.New(message, inner)
            End Sub
        End Class

        Private Shared ReadOnly _gate As New Object()
        Private Shared _thread As Thread
        ''' <summary>Ein Auftrag fuer den GPU-Faden: die Arbeit, und was geschieht, wenn der Faden
        ''' sie nicht mehr ausfuehrt (Karte verloren, Selbstpruefung durchgefallen). Ohne den
        ''' Abbruchweg blieben eingereihte Auftraege beim Abschalten liegen, und ihre Aufrufer
        ''' warteten in TryRun fuer immer.</summary>
        Private NotInheritable Class GpuWork
            Public Run As Action
            Public Cancel As Action
            ''' <summary>Vom GPU-Faden gesetzt, sobald er den Auftrag beginnt; ab dann laeuft die
            ''' Frist <see cref="JobTimeout"/>. Die Zeit in der Schlange zaehlt nicht mit.</summary>
            Public Started As Integer
        End Class

        ''' <summary>So lange darf ein begonnener Auftrag dauern. Gemessen braucht die schwerste
        ''' Stufe (Zurueckrechnen am 20-MP-Foto) an einer RTX 5060 rund 0,4 s; ein Treiber, der
        ''' haengt, kommt dagegen nie zurueck. Ohne Frist stuende dann ein Export fuer immer.</summary>
        Private Shared ReadOnly JobTimeout As TimeSpan = TimeSpan.FromSeconds(60)

        ''' <summary>So lange darf das Anlegen des Kontexts dauern (gemessen 30 ms).</summary>
        Private Shared ReadOnly StartTimeout As TimeSpan = TimeSpan.FromSeconds(30)

        ''' <summary>Renderer, die das Bild in Software auf dem Prozessor rasterisieren. Sie rechnen
        ''' richtig, die Selbstpruefung besteht also, aber Shader laufen dort viel langsamer als die
        ''' eigene Prozessorkette. Anderswo gemessen: llvmpipe 6,3 s gegen 2,3 s fuer dasselbe Bild,
        ''' auf vier Kernen Minuten. Typisch in virtuellen Maschinen und ohne Grafiktreiber.</summary>
        Private Shared ReadOnly SoftwareRendererNames As String() = {
            "llvmpipe", "softpipe", "swrast", "lavapipe", "SwiftShader",
            "Microsoft Basic Render", "GDI Generic", "Apple Software Renderer"}

        ''' <summary>Die Marke liegt neben den Einstellungen, solange die Karte arbeitet (Anlegen
        ''' des Kontexts und jeder Auftrag). Stuerzt der Treiber ab, faengt kein Try das: der Prozess
        ''' endet, und mit eingeschaltetem Schalter stuerzte er beim naechsten Bild wieder ab. Findet
        ''' der naechste Start die Marke noch, schaltet er die Karte deshalb ab
        ''' (<see cref="StartFromSettings"/>). Ein normales Ende raeumt sie weg (ProcessExit).</summary>
        Private Shared ReadOnly MarkerPath As String = IO.Path.Combine(AppSettingsService.Folder, "gpu-busy.marker")
        Private Shared _shuttingDown As Boolean
        ''' <summary>Gesetzt, wenn die Karte haengt: die Marke bleibt dann auch beim normalen Ende
        ''' liegen, damit der naechste Start den Schalter abschaltet.</summary>
        Private Shared _markerSticky As Boolean
        Private Shared _exitHooked As Integer

        Private Shared _queue As BlockingCollection(Of GpuWork)
        Private Shared _context As IGlContext
        Private Shared _currentScope As IDisposable
        Private Shared _gr As GRContext
        Private Shared _state As Integer ' 0 unbekannt, 1 bereit, 2 gescheitert
        Private Shared _failureReason As String = ""
        Private Shared _renderer As String = ""

        Private Shared _enabled As Boolean
        Private Shared _completedJobs As Long

        Private Sub New()
        End Sub

        ''' <summary>Die Einstellung "Grafikkarte fuer die Bildbearbeitung nutzen" (ab Werk aus).
        ''' Gesetzt beim Start und beim Umlegen des Schalters; ohne sie fasst niemand die Karte an,
        ''' auch der Faden startet dann nicht.</summary>
        Public Shared Property Enabled As Boolean
            Get
                Return Volatile.Read(_enabled)
            End Get
            Set(value As Boolean)
                Volatile.Write(_enabled, value)
            End Set
        End Property

        ''' <summary>Steht die Grafikkarte bereit? Startet den Faden beim ersten Aufruf.</summary>
        Public Shared ReadOnly Property IsAvailable As Boolean
            Get
                EnsureStarted()
                Return Volatile.Read(_state) = 1
            End Get
        End Property

        ''' <summary>Warum die Grafikkarte nicht bereitsteht; leer, solange nichts schiefging.</summary>
        Public Shared ReadOnly Property FailureReason As String
            Get
                Return _failureReason
            End Get
        End Property

        ''' <summary>Die Karte, die der Kontext meldet (GL_RENDERER).</summary>
        Public Shared ReadOnly Property Renderer As String
            Get
                Return _renderer
            End Get
        End Property

        ''' <summary>True, wenn dieser Start den Schalter ausgeschaltet hat, weil die letzte
        ''' Sitzung mitten in der Arbeit der Karte endete (Marke gefunden). Die Einstellungen sagen
        ''' das, bis der Schalter wieder eingeschaltet wird.</summary>
        Public Shared Property SwitchedOffAfterCrash As Boolean

        ''' <summary>Beim Start der Anwendung: liegt die Marke noch, endete die letzte Sitzung im
        ''' Treiber. Dann geht der Schalter aus, und der Grund steht in der Fehlerdatei. Setzt
        ''' danach <see cref="Enabled"/> aus der Einstellung.</summary>
        Public Shared Sub StartFromSettings()
            Dim leftover As String = Nothing
            Try
                If IO.File.Exists(MarkerPath) Then
                    leftover = IO.File.ReadAllText(MarkerPath)
                    IO.File.Delete(MarkerPath)
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("GpuImage", ex)
            End Try
            If leftover IsNot Nothing AndAlso AppSettingsService.Load().GpuImageProcessing Then
                AppSettingsService.Update(Sub(s) s.GpuImageProcessing = False)
                SwitchedOffAfterCrash = True
                DiagnosticLogService.LogException("GpuImage", New GpuUnavailableException(
                    $"Die letzte Sitzung endete mitten in der Arbeit der Grafikkarte ({leftover.Trim()}). " &
                    "Der Schalter ist deshalb ausgeschaltet, die Bildbearbeitung rechnet auf dem Prozessor."))
            End If
            Enabled = AppSettingsService.Load().GpuImageProcessing
        End Sub

        ''' <summary>Erkennt einen Software-Renderer an seinem Namen (GL_RENDERER). Nothing, wenn
        ''' es eine echte Karte ist.</summary>
        Friend Shared Function SoftwareRendererName(renderer As String) As String
            If String.IsNullOrEmpty(renderer) Then Return Nothing
            For Each name In SoftwareRendererNames
                If renderer.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 Then Return name
            Next
            Return Nothing
        End Function

        Private Shared Sub ArmMarker(what As String)
            If Volatile.Read(_shuttingDown) Then Return
            If Interlocked.Exchange(_exitHooked, 1) = 0 Then
                AddHandler AppDomain.CurrentDomain.ProcessExit, Sub(sender As Object, e As EventArgs)
                                                                     Volatile.Write(_shuttingDown, True)
                                                                     DisarmMarker()
                                                                 End Sub
            End If
            Try
                IO.Directory.CreateDirectory(AppSettingsService.Folder)
                IO.File.WriteAllText(MarkerPath, $"{what}; {If(_renderer, "")}; {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            Catch
                ' Ohne Marke fehlt nur der Schutz beim naechsten Start; die Arbeit selbst geht.
            End Try
        End Sub

        ''' <summary>Die Karte haengt im Treiber: Marke setzen und liegen lassen.</summary>
        Private Shared Sub KeepMarkerForNextStart(what As String)
            ArmMarker(what)
            Volatile.Write(_markerSticky, True)
        End Sub

        Private Shared Sub DisarmMarker()
            If Volatile.Read(_markerSticky) Then Return
            Try
                IO.File.Delete(MarkerPath)
            Catch
            End Try
        End Sub

        ''' <summary>Wie viele Auftraege die Karte seit dem Start ohne Fehler erledigt hat. Belegt
        ''' im Pruefstand, dass ein Vergleich wirklich auf der Karte lief: faellt die Arbeit still
        ''' auf den Prozessor zurueck, sind beide Ergebnisse gleich und der Vergleich waere grundlos
        ''' gruen.</summary>
        Public Shared ReadOnly Property CompletedJobs As Long
            Get
                Return Interlocked.Read(_completedJobs)
            End Get
        End Property

        ''' <summary>Fuehrt <paramref name="work"/> auf dem GPU-Faden aus und wartet darauf. False,
        ''' wenn die Grafikkarte nicht bereitsteht oder die Arbeit scheitert - dann rechnet der
        ''' Aufrufer auf dem Prozessor. Ein Fehler in der Arbeit geht in die Fehlerdatei; ist das
        ''' Geraet dabei verloren gegangen, bleibt der Weg fuer diese Sitzung zu.</summary>
        Public Shared Function TryRun(Of T)(work As Func(Of GRContext, T), ByRef result As T) As Boolean
            result = Nothing
            If work Is Nothing OrElse Not IsAvailable Then Return False
            Dim value As T = Nothing
            Dim failure As Exception = Nothing
            Dim cancelled = False
            ' Das Ereignis wird bewusst nicht freigegeben: kehrt dieser Aufruf nach Ablauf der Frist
            ' zurueck, setzt der GPU-Faden es vielleicht noch spaeter, und ein freigegebenes
            ' Ereignis wuerde dort werfen und den Faden beenden.
            Dim done As New ManualResetEventSlim(False)
            Dim item As New GpuWork With {
                .Run = Sub()
                           Try
                               value = work(_gr)
                           Catch ex As Exception
                               failure = ex
                           Finally
                               done.Set()
                           End Try
                       End Sub,
                .Cancel = Sub()
                              cancelled = True
                              done.Set()
                          End Sub}
            Try
                _queue.Add(item)
            Catch ex As InvalidOperationException
                ' Die Schlange ist geschlossen: der Faden ist nach einem Fehler beendet.
                Return False
            End Try
            Dim startedAt As Long = 0
            While Not done.Wait(500)
                If Volatile.Read(item.Started) = 0 Then
                    ' Noch in der Schlange. Ist der Weg inzwischen zu, weil ein anderer Auftrag im
                    ' Treiber haengt, erreicht der Faden diesen nie mehr.
                    If Volatile.Read(_state) = 2 Then Return False
                    Continue While
                End If
                If startedAt = 0 Then startedAt = Stopwatch.GetTimestamp()
                If Stopwatch.GetElapsedTime(startedAt) > JobTimeout Then
                    KeepMarkerForNextStart("Auftrag haengt")
                    Fail($"Ein Auftrag auf der Grafikkarte kam nach {JobTimeout.TotalSeconds:0} Sekunden nicht zurueck; der Treiber haengt.", Nothing)
                    Return False
                End If
            End While
            done.Dispose()
            ' Der Faden hat abgeschaltet, bevor er diesen Auftrag erreichte: ohne Karte weiter, der
            ' Grund steht schon in der Fehlerdatei.
            If cancelled Then Return False
            If failure IsNot Nothing Then
                DiagnosticLogService.LogException("GpuImage", New GpuUnavailableException(
                    "Die Arbeit auf der Grafikkarte ist gescheitert, dieser Durchgang rechnet auf dem Prozessor.", failure))
                If _gr Is Nothing OrElse _gr.IsAbandoned Then
                    Fail("Das Grafikgeraet ist verloren gegangen.", failure)
                End If
                Return False
            End If
            result = value
            Interlocked.Increment(_completedJobs)
            Return True
        End Function

        ''' <summary>Sperrt den Weg fuer diese Sitzung, weil die Karte zwar laeuft, aber nicht
        ''' brauchbar rechnet (die Selbstpruefung weicht ab). Der Grund geht wie jeder Fehlschlag in
        ''' die Fehlerdatei.</summary>
        Public Shared Sub MarkUnusable(reason As String)
            Fail(reason, Nothing)
        End Sub

        Private Shared Sub EnsureStarted()
            If Volatile.Read(_state) <> 0 Then Return
            Dim started As ManualResetEventSlim = Nothing
            SyncLock _gate
                If Volatile.Read(_state) <> 0 OrElse _thread IsNot Nothing Then
                    ' Ein anderer Aufrufer startet gerade; unten auf sein Ergebnis warten.
                Else
                    _queue = New BlockingCollection(Of GpuWork)()
                    started = New ManualResetEventSlim(False)
                    Dim signal = started
                    _thread = New Thread(Sub() RunWorker(signal)) With {.IsBackground = True, .Name = "FerrumPix GPU"}
                    _thread.Start()
                End If
            End SyncLock
            If started IsNot Nothing Then
                ' Nicht freigeben: kommt das Anlegen erst nach der Frist zurueck, setzt der Faden
                ' das Ereignis noch.
                If Not started.Wait(StartTimeout) Then
                    KeepMarkerForNextStart("Anlegen haengt")
                    Fail($"Das Anlegen des Grafikkontexts kam nach {StartTimeout.TotalSeconds:0} Sekunden nicht zurueck; der Treiber haengt.", Nothing)
                End If
            Else
                ' Warten, bis der startende Aufrufer fertig ist (der Zustand verlaesst die 0).
                Dim spin As New SpinWait()
                While Volatile.Read(_state) = 0
                    spin.SpinOnce()
                End While
            End If
        End Sub

        Private Shared Sub RunWorker(started As ManualResetEventSlim)
            Dim clock = Stopwatch.StartNew()
            ArmMarker("Kontext anlegen")
            Try
                Dim graphics = ResolvePlatformGraphics()
                If graphics Is Nothing Then
                    Fail("Keine Plattformgrafik: Avalonia zeichnet ohne Grafikkarte (Software oder kopflos).", Nothing)
                    Return
                End If
                Dim created As IPlatformGraphicsContext
                Try
                    created = graphics.CreateContext()
                Catch ex As Exception
                    Fail($"Die Plattformgrafik ({graphics.GetType().Name}) gibt keinen eigenen Kontext heraus.", ex)
                    Return
                End Try
                _context = TryCast(created, IGlContext)
                If _context Is Nothing Then
                    created?.Dispose()
                    Fail($"Kein OpenGL-Kontext: die Plattformgrafik {graphics.GetType().Name} liefert {If(created?.GetType().Name, "nichts")}.", Nothing)
                    Return
                End If
                _currentScope = _context.MakeCurrent()
                Dim glInterface = GRGlInterface.Create(Function(name) _context.GlInterface.GetProcAddress(name))
                If glInterface Is Nothing OrElse Not glInterface.Validate() Then
                    Fail("Skia kann die OpenGL-Funktionen des Kontexts nicht binden.", Nothing)
                    Return
                End If
                _gr = GRContext.CreateGl(glInterface)
                If _gr Is Nothing Then
                    Fail("Skia legt auf dem OpenGL-Kontext keinen GRContext an.", Nothing)
                    Return
                End If
                Try
                    _renderer = _context.GlInterface.GetString(&H1F01)
                Catch
                    _renderer = ""
                End Try
                Dim software = SoftwareRendererName(_renderer)
                If software IsNot Nothing Then
                    Fail($"{_renderer} zeichnet in Software auf dem Prozessor; die eigene Prozessorkette ist dort schneller.", Nothing)
                    Return
                End If
                ' Fail kann waehrend des Anlegens schon gesetzt sein (Frist abgelaufen); dann bleibt es dabei.
                If Interlocked.CompareExchange(_state, 1, 0) <> 0 Then Return
                DiagnosticLogService.LogAlways("GpuImage",
                    $"bereit: {_renderer}; OpenGL {_context.Version.Major}.{_context.Version.Minor} ({_context.Version.Type}); " &
                    $"Plattformgrafik {graphics.GetType().Name}; {clock.ElapsedMilliseconds} ms")
            Catch ex As Exception
                Fail("Der GPU-Faden ist beim Anlegen des Kontexts gescheitert.", ex)
                Return
            Finally
                DisarmMarker()
                started.Set()
            End Try

            ' Abgeschaltet wird nicht durch Abbrechen der Schleife: Fail schliesst die Schlange
            ' (CompleteAdding), und die Schleife laeuft weiter, bis sie leer ist. Was nach dem
            ' Abschalten noch darin liegt, wird nicht mehr gerechnet, sondern freigegeben, sonst
            ' warteten seine Aufrufer in TryRun fuer immer.
            For Each item In _queue.GetConsumingEnumerable()
                If Volatile.Read(_state) = 2 Then
                    item.Cancel()
                Else
                    Volatile.Write(item.Started, 1)
                    ArmMarker("Auftrag")
                    Try
                        item.Run()
                    Finally
                        DisarmMarker()
                    End Try
                End If
            Next
        End Sub

        Private Shared Sub Fail(reason As String, inner As Exception)
            _failureReason = reason
            Volatile.Write(_state, 2)
            DiagnosticLogService.LogException("GpuImage", New GpuUnavailableException(
                reason & " Die Bildbearbeitung rechnet auf dem Prozessor.", inner))
            Try
                _queue?.CompleteAdding()
            Catch
            End Try
        End Sub

        ''' <summary>Avalonias Plattformgrafik. <c>AvaloniaLocator.Current</c> steht im
        ''' Referenzpaket nicht zur Verfuegung, nur zur Laufzeit - daher per Reflexion.</summary>
        Private Shared Function ResolvePlatformGraphics() As IPlatformGraphics
            Dim currentProperty = GetType(AvaloniaLocator).GetProperty("Current", BindingFlags.Public Or BindingFlags.Static)
            Dim locator = currentProperty?.GetValue(Nothing)
            If locator Is Nothing Then Return Nothing
            Dim getService = locator.GetType().GetMethod("GetService", New Type() {GetType(Type)})
            Return TryCast(getService?.Invoke(locator, New Object() {GetType(IPlatformGraphics)}), IPlatformGraphics)
        End Function

    End Class

End Namespace
