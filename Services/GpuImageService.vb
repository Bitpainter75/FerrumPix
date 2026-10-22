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
        End Class

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
            Using done As New ManualResetEventSlim(False)
                Try
                    _queue.Add(New GpuWork With {
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
                                  End Sub})
                Catch ex As InvalidOperationException
                    ' Die Schlange ist geschlossen: der Faden ist nach einem Fehler beendet.
                    Return False
                End Try
                done.Wait()
            End Using
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
                started.Wait()
                started.Dispose()
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
                Volatile.Write(_state, 1)
                DiagnosticLogService.LogAlways("GpuImage",
                    $"bereit: {_renderer}; OpenGL {_context.Version.Major}.{_context.Version.Minor} ({_context.Version.Type}); " &
                    $"Plattformgrafik {graphics.GetType().Name}; {clock.ElapsedMilliseconds} ms")
            Catch ex As Exception
                Fail("Der GPU-Faden ist beim Anlegen des Kontexts gescheitert.", ex)
                Return
            Finally
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
                    item.Run()
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
