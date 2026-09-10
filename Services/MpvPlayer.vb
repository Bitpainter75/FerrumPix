Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Runtime.InteropServices
Imports System.Threading

Namespace Services

    ''' <summary>Die Videowiedergabe über libmpv.
    '''
    ''' <para>KEIN libmpv-Aufruf läuft auf dem Anzeigefaden - alle laufen auf einem eigenen
    ''' Befehlsfaden, und die öffentlichen Methoden reihen nur ein. Das ist keine Kosmetik,
    ''' sondern der Riegel gegen ein Einfrieren der ganzen Anwendung unter macOS, und die Kette
    ''' dahinter steht so im Quelltext von mpv:</para>
    '''
    ''' <para><c>mpv_set_property_string</c> und <c>mpv_set_option_string</c> gehen in
    ''' <c>player/client.c</c> über <c>lock_core</c> und WARTEN auf den Kernfaden von mpv. Der
    ''' Kernfaden wartet beim Auf- und Abbau der Bildausgabe auf den Ausgabefaden. Und der
    ''' Ausgabefaden von macOS legt Fenster und Ansicht in <c>DispatchQueue.main.sync</c> an,
    ''' wartet also auf den Hauptfaden - denselben, der bei uns der Anzeigefaden ist. Wer von
    ''' dort eine Eigenschaft setzt, schließt den Ring: Anzeigefaden wartet auf Kern, Kern auf
    ''' Ausgabe, Ausgabe auf Anzeigefaden. Genau das war der Bericht "app freezes when
    ''' interacting with the app before the video has ended". Auf dem Befehlsfaden bleibt der
    ''' Hauptfaden frei, bedient die Warteschlange und alles läuft weiter.</para>
    '''
    ''' <para>Die Reihenfolge der Befehle bleibt dabei erhalten, weil es GENAU EINEN Befehlsfaden
    ''' mit einer Warteschlange gibt: Stop vor Laden vor Abspielen.</para>
    '''
    ''' <para>Zwei Ausgabewege: unter Windows und Linux hängt mpv sein Bild über die Option
    ''' <c>wid</c> in eine native Fläche der Anwendung. Unter macOS gibt es das nicht (siehe
    ''' <see cref="MpvSoftwareRenderer"/>), dort holt die Anwendung das Bild bei mpv ab.</para></summary>
    Public NotInheritable Class MpvPlayer
        Implements IDisposable

        Private Const PropTimePos As ULong = 1UL
        Private Const PropDuration As ULong = 2UL
        Private Const PropPause As ULong = 3UL
        Private Const PropMute As ULong = 4UL

        ''' <summary>Schützt den Zustand, der zwischen Anzeige-, Befehls- und Ereignisfaden geteilt
        ''' wird. Über einem libmpv-Aufruf darf diese Sperre NIE gehalten werden: sonst wartet der
        ''' Anzeigefaden doch wieder auf mpv, nur über eine Ecke mehr.</summary>
        Private ReadOnly _syncRoot As New Object()

        ''' <summary>Die Befehlswarteschlange. Dient zugleich als eigene Sperre.</summary>
        Private ReadOnly _queue As New Queue(Of Action)()

        Private ReadOnly _enableHardwareAcceleration As Boolean
        Private ReadOnly _usesRenderSurface As Boolean
        Private ReadOnly _renderer As MpvSoftwareRenderer

        Private _commandThread As Thread
        Private _queueClosed As Boolean = False

        ' Nur der Befehlsfaden fasst diese Felder an.
        Private _handle As IntPtr = IntPtr.Zero
        Private _eventThread As Thread
        Private _initialized As Boolean = False
        Private _initializationFailed As Boolean = False
        Private _windowHandle As IntPtr = IntPtr.Zero

        ''' <summary>Der zuletzt tatsächlich an mpv gegebene Pfad, und damit die EINZIGE Antwort auf
        ''' die Frage, welcher Film gerade laeuft.
        '''
        ''' GESCHRIEBEN NUR AUF DEM BEFEHLSFADEN, gelesen auch vom Anzeigefaden (siehe
        ''' <see cref="LoadedPath"/>). Eine Zuweisung an eine Verweisvariable ist unteilbar, mehr
        ''' braucht es hier nicht - gelesen wird ueber <c>Volatile</c>, damit der Leser nicht auf
        ''' einem Wert von vorhin sitzen bleibt.</summary>
        Private _loadedPath As String = Nothing

        Private _eventLoopStopping As Boolean = False

        ' Geteilter Zustand unter _syncRoot.
        Private _disposed As Boolean = False
        Private _pendingPath As String = Nothing
        Private _initializationError As Exception = Nothing
        Private _pendingPlay As Boolean = False
        Private _isPaused As Boolean = True
        Private _isMuted As Boolean = False

        Public Event TimeChanged(seconds As Double)
        Public Event DurationChanged(seconds As Double)
        Public Event PauseChanged(isPaused As Boolean)
        Public Event MuteChanged(isMuted As Boolean)
        Public Event EndReached(reason As Integer, [error] As Integer)

        ''' <summary>Ein Ladebefehl ist WIRKLICH an mpv gegangen. Feuert nicht, wenn der Riegel in
        ''' <see cref="LoadCore"/> ein zweites Laden desselben Films abgewiesen hat.
        '''
        ''' WOFUER: der Betrachter setzt daran seine Anzeige zurueck - Wiedergabestelle, Laufzeit,
        ''' Endemerker. Das darf nur geschehen, wenn auch wirklich ein anderer Film kommt; sonst
        ''' springt die Anzeige auf null, waehrend der Film unbeirrt weiterlaeuft.
        '''
        ''' DIE FRAGE IST HIER RICHTIG AUFGEHOBEN und auf der Anzeigeseite nicht. Dort wurde sie
        ''' einmal aus dem gemerkten Pfad beantwortet, und das ging schief: der Pfad wird auf dem
        ''' Befehlsfaden gefuehrt, gelesen wurde er auf dem Anzeigefaden. Wer von Video A auf ein
        ''' Bild und sofort zurueck auf A wechselte, las noch A - waehrend der Stop fuer A schon in
        ''' der Warteschlange stand und gleich danach lief. A blieb gestoppt, obwohl es wieder
        ''' ausgewaehlt war. Hier kann nichts veralten: die Meldung entsteht in demselben Faden, der
        ''' auch stoppt und laedt, und in derselben Reihenfolge.</summary>
        Public Event FileLoaded(path As String)
        Public Event InitializationFailed([error] As Exception)

        ''' <summary>mpv selbst hat sich beendet. Der Spieler ist danach unbrauchbar; wer ihn hält,
        ''' muss ihn wegwerfen und für das nächste Video einen neuen anlegen.</summary>
        Public Event PlaybackTerminated()

        ''' <summary>Ob auf DIESEM System das Bild bei mpv abgeholt wird. Steht schon fest, bevor
        ''' ein Spieler existiert: die Oberfläche muss vorher wissen, welche der beiden Flächen
        ''' zeigt.
        '''
        ''' <para>Die Umgebungsvariable <c>FERRUMPIX_VIDEO_RENDER_API</c> schaltet den abholenden
        ''' Weg auch dort ein, wo er nicht die Vorgabe ist. Sie ist nicht für Anwender gedacht,
        ''' sondern dafür, dass dieser Weg sich überhaupt anderswo als auf einem Mac ausprobieren
        ''' und im Prüfstand messen lässt.</para></summary>
        Public Shared ReadOnly Property UsesRenderSurfaceOnThisPlatform As Boolean
            Get
                If OperatingSystem.IsMacOS() Then Return True
                Return String.Equals(Environment.GetEnvironmentVariable("FERRUMPIX_VIDEO_RENDER_API"), "1", StringComparison.Ordinal)
            End Get
        End Property

        Public Sub New(enableHardwareAcceleration As Boolean)
            _enableHardwareAcceleration = enableHardwareAcceleration
            _usesRenderSurface = UsesRenderSurfaceOnThisPlatform
            If _usesRenderSurface Then _renderer = New MpvSoftwareRenderer()

            _commandThread = New Thread(AddressOf CommandLoop) With {
                .IsBackground = True,
                .Name = "libmpv-commands"
            }
            _commandThread.Start()
        End Sub

        ''' <summary>Startet den Aufbau. MUSS gerufen werden, und zwar ERST, nachdem der Aufrufer
        ''' seine Ereignisbehandlungen angehängt hat.
        '''
        ''' <para>Der Aufbau läuft auf dem Befehlsfaden und kann scheitern, bevor der Aufrufer die
        ''' nächste Zeile erreicht hat - im Konstruktor angestoßen, ginge
        ''' <see cref="InitializationFailed"/> dann an niemanden, und der Aufrufer hielte einen
        ''' bereits abgebauten Spieler für gesund. Wer trotzdem später fragen will, findet den
        ''' Fehler dauerhaft in <see cref="InitializationError"/>.</para>
        '''
        ''' <para>Auf dem Weg über <c>wid</c> ist das ein Leerlauf: dort steht das Ausgabeziel erst
        ''' mit <see cref="AttachWindow"/> fest, und der Aufbau hängt daran. Auf dem abholenden Weg
        ''' gibt es nichts abzuwarten, und der Zeichenkontext MUSS vor dem ersten Laden stehen -
        ''' ohne ihn fällt mpv bei <c>vo=libmpv</c> auf eine Ausgabe mit eigenem Fenster zurück.</para></summary>
        Public Sub Start()
            SyncLock _syncRoot
                If _disposed Then Return
            End SyncLock
            Enqueue(AddressOf InitializeCore)
        End Sub

        ''' <summary>True, wenn das Bild bei mpv abgeholt statt in eine native Fläche gezeichnet
        ''' wird. Entscheidet, welches Steuerelement die Anzeige übernimmt.</summary>
        Public ReadOnly Property UsesRenderSurface As Boolean
            Get
                Return _usesRenderSurface
            End Get
        End Property

        ''' <summary>Der Fehler, an dem der Aufbau gescheitert ist, sonst Nothing. Bleibt stehen,
        ''' damit ihn auch findet, wer erst nach dem Scheitern nachsieht.</summary>
        Public ReadOnly Property InitializationError As Exception
            Get
                SyncLock _syncRoot
                    Return _initializationError
                End SyncLock
            End Get
        End Property

        ''' <summary>Der Bildlieferant für den abholenden Weg, sonst Nothing.</summary>
        Public ReadOnly Property Renderer As MpvSoftwareRenderer
            Get
                Return _renderer
            End Get
        End Property

        Public Sub AttachWindow(windowHandle As IntPtr)
            If windowHandle = IntPtr.Zero OrElse _usesRenderSurface Then Return
            SyncLock _syncRoot
                If _disposed Then Return
            End SyncLock
            Enqueue(Sub() AttachWindowCore(windowHandle))
        End Sub

        Public Sub DetachWindow()
            If _usesRenderSurface Then Return
            Enqueue(AddressOf DetachWindowCore)
        End Sub

        ''' <summary>Meldet, dass GENAU DIESES Ausgabefenster abgeraeumt wurde. Anders als
        ''' <see cref="DetachWindow"/> wird mpv dabei nicht umgestellt - siehe
        ''' <c>ForgetWindowCore</c>.</summary>
        Public Sub ForgetWindow(windowHandle As IntPtr)
            If _usesRenderSurface Then Return
            Enqueue(Sub() ForgetWindowCore(windowHandle))
        End Sub

        ''' <summary>Welcher Film gerade geladen ist. Nothing heisst: keiner.
        '''
        ''' Fuer den Betrachter, der daran entscheidet, ob er seine Anzeige (Stelle, Laufzeit,
        ''' Endemerker) zuruecksetzt. Die Antwort kann im selben Augenblick veralten, in dem sie
        ''' gegeben wird - der Befehlsfaden laeuft weiter. Sie taugt deshalb NUR fuer Anzeigeleisten
        ''' und niemals als Riegel gegen ein zweites Laden: der steht in <see cref="LoadCore"/>, wo
        ''' der Wert nicht veralten kann.</summary>
        Public ReadOnly Property LoadedPath As String
            Get
                Return Volatile.Read(_loadedPath)
            End Get
        End Property

        ''' <summary><paramref name="force"/> laedt auch dann neu, wenn genau dieser Film schon
        ''' laeuft. Gebraucht fuer das WIEDERHOLEN nach dem Ende: dort ist das erneute Laden der
        ''' ganze Zweck.</summary>
        Public Sub Load(path As String, Optional force As Boolean = False)
            If String.IsNullOrWhiteSpace(path) Then Return
            SyncLock _syncRoot
                If _disposed Then Return
                _pendingPath = path
            End SyncLock
            Enqueue(Sub() LoadCore(path, force))
        End Sub

        ''' <summary>Lädt den zuletzt vorgemerkten Pfad. Gebraucht, wenn beim Vormerken noch keine
        ''' Ausgabefläche stand.</summary>
        Public Sub LoadPending()
            Enqueue(AddressOf LoadPendingCore)
        End Sub

        Public Sub Play()
            SyncLock _syncRoot
                If _disposed Then Return
                _pendingPlay = True
            End SyncLock
            Enqueue(Sub() SetPauseCore(False))
        End Sub

        Public Sub Pause()
            SyncLock _syncRoot
                If _disposed Then Return
                _pendingPlay = False
            End SyncLock
            Enqueue(Sub() SetPauseCore(True))
        End Sub

        Public Sub TogglePause()
            Enqueue(AddressOf TogglePauseCore)
        End Sub

        Public Sub [Stop]()
            SyncLock _syncRoot
                If _disposed Then Return
                _pendingPlay = False
            End SyncLock
            Enqueue(AddressOf StopCore)
        End Sub

        Public Sub Seek(seconds As Double)
            Dim target = Math.Max(0, seconds)
            Enqueue(Sub()
                        If Not ReadyForPlayback() Then Return
                        SetPropertyStringRaw("time-pos", target.ToString(CultureInfo.InvariantCulture))
                    End Sub)
        End Sub

        Public Sub SetMuted(value As Boolean)
            SyncLock _syncRoot
                If _disposed Then Return
                _isMuted = value
            End SyncLock
            Enqueue(Sub()
                        If Not ReadyForPlayback() Then Return
                        SetPropertyStringRaw("mute", If(value, "yes", "no"))
                    End Sub)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            SyncLock _syncRoot
                If _disposed Then Return
                _disposed = True
            End SyncLock

            ' Der Abbau läuft als LETZTER Auftrag auf dem Befehlsfaden. Er darf warten, solange er
            ' will: mpv_terminate_destroy kann auf den Abbau der Bildausgabe warten, und ein eigener
            ' Hintergrundfaden hält den Prozess beim Beenden nicht auf.
            EnqueueFinal(AddressOf ShutdownCore)
        End Sub

        ' ── Befehlsfaden ─────────────────────────────────────────────────────────

        Private Sub Enqueue(work As Action)
            SyncLock _queue
                If _queueClosed Then Return
                _queue.Enqueue(work)
                Monitor.Pulse(_queue)
            End SyncLock
        End Sub

        Private Sub EnqueueFinal(work As Action)
            SyncLock _queue
                If _queueClosed Then Return
                _queue.Enqueue(work)
                _queueClosed = True
                Monitor.Pulse(_queue)
            End SyncLock
        End Sub

        Private Sub CommandLoop()
            Do
                Dim work As Action = Nothing
                SyncLock _queue
                    Do While _queue.Count = 0
                        If _queueClosed Then Exit Do
                        Monitor.Wait(_queue)
                    Loop
                    If _queue.Count > 0 Then work = _queue.Dequeue()
                End SyncLock

                If work Is Nothing Then Exit Do

                Try
                    work()
                Catch ex As Exception
                    DiagnosticLogService.LogException("VideoPlayback.Command", ex)
                End Try
            Loop
        End Sub

        ''' <summary>True, wenn mpv steht und ein Ausgabeziel kennt.</summary>
        Private Function ReadyForPlayback() As Boolean
            If Not _initialized OrElse _initializationFailed OrElse _handle = IntPtr.Zero Then Return False
            Return _usesRenderSurface OrElse _windowHandle <> IntPtr.Zero
        End Function

        ''' <summary>Eine Zeile im Diagnoseprotokoll, nur bei eingeschaltetem Schalter. Der Weg zum
        ''' Bild geht ueber vier Fadenwechsel und haengt an der Reihenfolge; wenn er einmal nicht
        ''' zum Bild fuehrt, ist diese Abfolge das Einzige, woran sich das ablesen laesst.</summary>
        Private Sub LogState(stage As String)
            DiagnosticLogService.LogAlways("VideoPlayback.Weg",
                $"{stage} (Fenster={_windowHandle}, aufgebaut={_initialized}, geladen={If(_loadedPath, "-")})")
        End Sub

        Private Sub AttachWindowCore(windowHandle As IntPtr)
            If _initializationFailed Then Return
            If _windowHandle = windowHandle AndAlso _initialized Then
                LogState($"Fenster {windowHandle} steht schon")
                Return
            End If
            LogState($"Fenster {windowHandle} kommt an")

            _windowHandle = windowHandle
            If Not _initialized Then
                InitializeCore()
                Return
            End If

            If _handle = IntPtr.Zero Then Return
            Try
                SetOptionStringRaw("wid", WindowHandleToUnsignedString(windowHandle))
            Catch ex As Exception
                HandleInitializationFailure(ex)
                Return
            End Try

            ' EIN ANDERES FENSTER HEISST: DIE BILDAUSGABE MUSS NEU AUFGEBAUT WERDEN.
            '
            ' Die Option allein reicht dafuer nicht. mpv nimmt den neuen Wert an, aber die
            ' Bildausgabe, die es beim Laden aufgebaut hat, haengt weiter an dem Fenster von
            ' damals - und das ist entweder verschwunden oder wurde beim Wegschalten mit "wid=-1"
            ' aufgegeben. Der Ton lief in diesem Fall weiter, das Bild blieb weg (Nutzerbefund unter
            ' Linux: Video ansehen, auf ein Foto wechseln, zurueck auf dasselbe Video).
            '
            ' Neu aufbauen laesst sich die Ausgabe nur ueber ein neues Laden. Deshalb gilt hier,
            ' was geladen war, als NICHT mehr geladen. Der Film faengt dadurch von vorn an - denselben
            ' Preis zahlte die Anwendung frueher auch, nur unausgesprochen: dort lud sie nach jedem
            ' Anhaengen bedingungslos neu. Passiert nur bei einem WECHSEL des Fensters; steht dasselbe
            ' wie vorher, ist die Arbeit oben schon abgebrochen.
            _loadedPath = Nothing
            LoadPendingCore()
        End Sub

        ''' <summary>Das Ausgabefenster gibt es nicht mehr - die Oberflaeche hat es abgeraeumt.
        '''
        ''' Hier wird mpv NICHT umgestellt: <c>wid=-1</c> hiesse „mach dein eigenes Fenster auf",
        ''' und genau das soll es nie. Es wird nur gemerkt, dass kein Ausgabeziel mehr steht, damit
        ''' nichts mehr an ein Fenster geschickt wird, das es nicht mehr gibt. Der geladene Film
        ''' bleibt vermerkt: kommt gleich ein neues Fenster, laeuft er dort weiter, statt an den
        ''' Anfang zurueckzuspringen.
        '''
        ''' <para>VERGESSEN WIRD NUR DAS GEMELDETE FENSTER. Anbinden und Abraeumen laufen beide ueber
        ''' die Warteschlange, und die Oberflaeche kann das neue Fenster anbinden, bevor sie das alte
        ''' abraeumt. Ein Vergessen ohne Vergleich loeschte in dieser Reihenfolge das gerade erst
        ''' angebundene: jedes Laden waere danach wieder mangels Ausgabeziel verschoben worden, und
        ''' das Bild bliebe schwarz.</para></summary>
        Private Sub ForgetWindowCore(windowHandle As IntPtr)
            If _windowHandle <> windowHandle Then
                LogState($"Fenster {windowHandle} abgeraeumt, gilt aber schon ein anderes")
                Return
            End If
            _windowHandle = IntPtr.Zero
            LogState("Fenster abgeraeumt")
        End Sub

        Private Sub DetachWindowCore()
            _windowHandle = IntPtr.Zero
            _loadedPath = Nothing
            LogState("Fenster abgegeben")
            If _initializationFailed OrElse Not _initialized OrElse _handle = IntPtr.Zero Then Return
            Try
                SetOptionStringRaw("wid", "-1")
            Catch ex As Exception
                DiagnosticLogService.LogException("VideoPlayback.DetachWindow", ex)
            End Try
        End Sub

        Private Sub LoadCore(path As String, Optional force As Boolean = False)
            ' DASSELBE ZWEIMAL ZU LADEN IST EIN ABSTURZ, und der Riegel dagegen steht HIER.
            '
            ' Zwei "loadfile ... replace" kurz hintereinander bauen mpv um, waehrend der Zeichenfaden
            ' noch Einzelbilder der ersten Fassung holt; das Zuschneiden rechnet dann mit Massen, die
            ' nicht mehr gelten, und libmpv bricht mit einer Zusicherung ab ("mp_image_crop"). Der
            ' Prozess ist damit weg - aus nativem Code heraus, ohne Ausnahme, die sich fangen liesse.
            '
            ' ER STAND FRUEHER IM BETRACHTER und hat nicht gehalten: dort haengt er an einem eigenen
            ' Merker, und den leert jeder Weg ueber StopVideoPlayback - unter anderem der
            ' NICHT-Video-Zweig desselben Ladewegs. Ein Element aus Immich laeuft durch beide, weil
            ' es erst nach dem Herunterladen eine Datei ist. Gemeldet an 0.9.42 unter macOS, mit zwei
            ' gleichen "geladen"-Zeilen im Protokoll.
            '
            ' HIER KANN IHN NIEMAND UMGEHEN: jeder Weg zu mpv geht durch diese Stelle, und der Wert,
            ' an dem er haengt, ist derselbe, den mpv gerade wirklich spielt. Die drei Faelle, die
            ' nicht brechen duerfen, bleiben heil:
            '   - WIEDERHOLEN nach dem Ende laedt denselben Pfad mit Absicht: dafuer ist "force" da.
            '   - EIN FENSTERWECHSEL setzt _loadedPath selbst auf Nothing (AttachWindowCore), das
            '     Nachladen laeuft also durch.
            '   - EIN WEGGEWORFENER SPIELER bringt einen frischen Merker mit, weil der Merker jetzt
            '     IM Spieler sitzt. Genau das war am alten Ort die Sorge, und sie loest sich hier
            '     von selbst: ein neuer Spieler hat nichts geladen.
            If Not force AndAlso ReadyForPlayback() AndAlso
               String.Equals(path, _loadedPath, StringComparison.Ordinal) Then
                LogState("schon geladen, zweites Laden uebersprungen")
                Return
            End If

            ' Zuerst verwerfen, was mpv bisher hatte: gelingt das Laden nicht (etwa weil noch
            ' keine Ausgabefläche steht), muss LoadPending es später NACHHOLEN dürfen - auch dann,
            ' wenn es derselbe Pfad ist, der vorhin schon einmal lief.
            _loadedPath = Nothing
            If Not ReadyForPlayback() Then
                LogState("Laden verschoben, kein Ausgabeziel")
                Return
            End If
            SetPauseCore(True)
            If CommandAsyncRaw(_handle, "loadfile", path, "replace") < 0 Then
                LogState("Laden abgewiesen")
                Return
            End If
            _loadedPath = path
            ' VOR dem Abspielen melden: der Betrachter setzt daran seine Anzeige zurueck, und ein
            ' Zuruecksetzen NACH dem ersten Zeitbericht loeschte genau den wieder.
            RaiseEvent FileLoaded(path)
            If PendingPlay() Then SetPauseCore(False)
            LogState("geladen")
        End Sub

        Private Sub LoadPendingCore()
            Dim path As String
            SyncLock _syncRoot
                path = _pendingPath
            End SyncLock
            If String.IsNullOrWhiteSpace(path) Then Return
            ' Nur nachholen, was liegen geblieben ist. Laeuft derselbe Film schon, setzte ein
            ' zweites Laden ihn sichtbar auf den Anfang zurueck. Nach einem Fensterwechsel gilt
            ' er nicht mehr als geladen (siehe AttachWindowCore) - dort MUSS neu geladen werden.
            ' Die Regel dazu steht seit dem Riegel in LoadCore und wird hier nicht zweitgefasst:
            ' zwei Fassungen derselben Regel laufen auseinander.
            LoadCore(path)
        End Sub

        Private Sub TogglePauseCore()
            If _initializationFailed Then Return

            Dim paused As Boolean
            SyncLock _syncRoot
                If _disposed Then Return
                paused = _isPaused
                _pendingPlay = paused
            End SyncLock

            SetPauseCore(Not paused)
        End Sub

        Private Sub StopCore()
            _loadedPath = Nothing
            ' MITGESCHRIEBEN, weil der Stop den gemerkten Pfad loescht und damit den Riegel gegen das
            ' zweite Laden aufhebt. Faellt einer zwischen zwei Ladeauftraege, laden beide - und genau
            ' diese Abfolge ist die letzte offene Frage zum Absturz unter macOS. Ohne diese Zeile
            ' steht im Protokoll zweimal "geladen" und nichts dazwischen, und man kann nicht sagen,
            ' ob ein Stop dabei war.
            LogState("gestoppt")
            If Not ReadyForPlayback() Then Return
            CommandAsyncRaw(_handle, "stop")
        End Sub

        Private Function PendingPlay() As Boolean
            SyncLock _syncRoot
                Return _pendingPlay
            End SyncLock
        End Function

        Private Sub SetPauseCore(value As Boolean)
            ' Der vorgemerkte Zustand steht in _pendingPlay; _isPaused beschreibt, was mpv
            ' TATSÄCHLICH tut, und darf deshalb erst gesetzt werden, wenn der Befehl auch abgeht.
            If Not ReadyForPlayback() Then Return
            SyncLock _syncRoot
                _isPaused = value
            End SyncLock
            SetPropertyStringRaw("pause", If(value, "yes", "no"))
        End Sub

        Private Sub InitializeCore()
            If _initializationFailed OrElse _initialized Then Return
            If Not _usesRenderSurface AndAlso _windowHandle = IntPtr.Zero Then Return

            Try
                MpvInterop.EnsureResolver()
                _handle = MpvInterop.Create()
                If _handle = IntPtr.Zero Then Throw New InvalidOperationException("libmpv konnte nicht erstellt werden.")

                SetOptionStringRaw("terminal", "no")
                ' Siehe VideoPreviewService: FFmpeg-Demuxer-Hinweise nicht auf die Konsole durchlassen.
                SetOptionStringRaw("msg-level", "all=no")
                SetOptionStringRaw("config", "no")
                SetOptionStringRaw("input-default-bindings", "no")
                SetOptionStringRaw("osc", "no")
                SetOptionStringRaw("keep-open", "no")

                If _usesRenderSurface Then
                    ' Kein Fenster: mpv gibt das fertige Bild heraus. Die Bildbeschleunigung muss
                    ' dabei ZURÜCKKOPIEREN - ein Einzelbild, das auf der Grafikkarte liegen bleibt,
                    ' kann der Software-Zeichner nicht lesen.
                    SetOptionStringRaw("vo", "libmpv")
                    SetOptionStringRaw("hwdec", If(_enableHardwareAcceleration, "auto-copy", "no"))
                Else
                    SetOptionStringRaw("hwdec", If(_enableHardwareAcceleration, "auto-safe", "no"))
                    If OperatingSystem.IsLinux() Then
                        SetOptionStringRaw("vo", "gpu")
                        SetOptionStringRaw("gpu-context", "x11egl")
                    End If
                    SetOptionStringRaw("wid", WindowHandleToUnsignedString(_windowHandle))
                End If

                Dim result = MpvInterop.Initialize(_handle)
                If result < 0 Then Throw New InvalidOperationException($"libmpv konnte nicht initialisiert werden ({result}).")

                ObservePropertyRaw(PropTimePos, "time-pos", MpvInterop.MpvFormat.Double)
                ObservePropertyRaw(PropDuration, "duration", MpvInterop.MpvFormat.Double)
                ObservePropertyRaw(PropPause, "pause", MpvInterop.MpvFormat.Flag)
                ObservePropertyRaw(PropMute, "mute", MpvInterop.MpvFormat.Flag)

                Dim muted As Boolean
                SyncLock _syncRoot
                    muted = _isMuted
                End SyncLock
                SetPropertyStringRaw("mute", If(muted, "yes", "no"))

                If _usesRenderSurface Then
                    If Not _renderer.TryCreate(_handle) Then
                        Throw New InvalidOperationException("Der Zeichenkontext von libmpv konnte nicht angelegt werden.")
                    End If
                Else
                    ' Klick aufs Video toggelt Wiedergabe/Pause: Das Video sitzt in einem
                    ' NativeControlHost - Avalonia-Pointer-Events erreichen es NIE, das native
                    ' mpv-Fenster schluckt sie. Deshalb bindet mpv selbst die linke Maustaste; alle
                    ' uebrigen Default-Bindings bleiben aus (input-default-bindings=no). Der
                    ' Pause-Status fliesst ueber die bestehende pause-Observation zurueck in die
                    ' Oberflaeche (Play-Knopf folgt). Auf dem abholenden Weg braucht es das nicht:
                    ' dort liegt die Fläche in der Oberfläche und bekommt die Klicks selbst.
                    CommandRaw(_handle, "keybind", "MBTN_LEFT", "cycle pause")
                End If

                Dim handle = _handle
                _eventLoopStopping = False
                _eventThread = New Thread(Sub() EventLoop(handle)) With {
                    .IsBackground = True,
                    .Name = "libmpv-event-loop"
                }
                _initialized = True
                _eventThread.Start()
            Catch ex As Exception
                HandleInitializationFailure(ex)
                Return
            End Try

            ' DAS NACHHOLEN GEHOERT HIERHER und nicht an eine Wartezeit in der Oberflaeche.
            '
            ' Auf dem Weg ueber "wid" steht das Ausgabeziel erst, wenn die native Flaeche ihr
            ' Fenster hat - der Aufbau haengt also daran, und alles, was vorher an Laden und
            ' Abspielen kam, ist ins Leere gelaufen (ReadyForPlayback war falsch). Bis hierher
            ' verliess sich die Anwendung darauf, dass das Fenster binnen 180 ms da ist und danach
            ' noch einmal geladen wird. War es langsamer, wurde nie geladen: der Film blieb
            ' schwarz, ohne Fehler und ohne zweiten Versuch (Nutzerbefund, Linux). Jetzt holt der
            ' Aufbau selbst nach, was auf ihn gewartet hat - egal, wie lange es gedauert hat.
            '
            ' AUSSERHALB des Try oben: ein Fehler beim Laden ist kein gescheiterter Aufbau, und er
            ' darf den Spieler nicht wegwerfen.
            LoadPendingCore()
        End Sub

        Private Sub HandleInitializationFailure(ex As Exception)
            _initializationFailed = True
            _initialized = False
            _windowHandle = IntPtr.Zero
            SyncLock _syncRoot
                _disposed = True
                _initializationError = ex
            End SyncLock
            ' Nach einem gescheiterten Aufbau kommt nichts mehr: die Warteschlange wird
            ' geschlossen, damit der Befehlsfaden nicht bis zum Programmende wartet.
            SyncLock _queue
                _queueClosed = True
                Monitor.Pulse(_queue)
            End SyncLock

            ShutdownNative()
            RaiseEvent InitializationFailed(ex)
        End Sub

        Private Sub ShutdownCore()
            ShutdownNative()
        End Sub

        ''' <summary>Baut alles Native ab. Läuft ausschließlich auf dem Befehlsfaden.</summary>
        Private Sub ShutdownNative()
            _initialized = False

            If _renderer IsNot Nothing Then
                Try
                    _renderer.Shutdown()
                Catch ex As Exception
                    DiagnosticLogService.LogException("VideoPlayback.Dispose", ex)
                End Try
            End If

            Dim handle = _handle
            _handle = IntPtr.Zero
            If handle = IntPtr.Zero Then Return

            Try
                CommandAsyncRaw(handle, "quit")
            Catch
            End Try

            Dim eventThread = _eventThread
            _eventThread = Nothing
            Volatile.Write(_eventLoopStopping, True)

            Try
                If eventThread IsNot Nothing AndAlso eventThread.IsAlive AndAlso
                   Not Object.ReferenceEquals(Thread.CurrentThread, eventThread) Then
                    ' Erst kurz warten, damit der Normalfall (die Schleife endet nach "quit"
                    ' sofort) nichts protokolliert. Danach wird der Nachzügler GEMELDET, aber
                    ' nicht aufgegeben: den Handle einfach stehen zu lassen hieße, ihn samt
                    ' seiner nativen Puffer bis zum Programmende zu verlieren - bei wiederholtem
                    ' Öffnen und Schließen von Videos summiert sich das. Zerstören darf ihn nur,
                    ' wer sicher ist, dass die Ereignisschleife ihn nicht mehr anfasst.
                    If Not eventThread.Join(2000) Then
                        DiagnosticLogService.LogException("VideoPlayback.Dispose",
                                                          New TimeoutException("mpv event loop did not stop within two seconds; waiting on the command thread."))
                        eventThread.Join()
                    End If
                End If
                MpvInterop.TerminateDestroy(handle)
            Catch ex As Exception
                DiagnosticLogService.LogException("VideoPlayback.Dispose", ex)
            End Try
        End Sub

        ' ── Ereignisfaden ────────────────────────────────────────────────────────

        Private Sub EventLoop(handle As IntPtr)
            Do
                If Volatile.Read(_eventLoopStopping) Then Exit Do

                Dim eventPtr = MpvInterop.WaitEvent(handle, 0.2)
                If eventPtr = IntPtr.Zero Then Continue Do

                Dim ev = Marshal.PtrToStructure(Of MpvInterop.MpvEvent)(eventPtr)
                Select Case ev.EventId
                    Case MpvInterop.MpvEventId.None
                    Case MpvInterop.MpvEventId.PropertyChange
                        HandlePropertyChange(ev)
                    Case MpvInterop.MpvEventId.EndFile
                        Dim endData = Marshal.PtrToStructure(Of MpvInterop.MpvEventEndFile)(ev.Data)
                        RaiseEvent EndReached(CInt(endData.Reason), endData.Error)
                    Case MpvInterop.MpvEventId.Shutdown
                        ' mpv beendet sich selbst - etwa weil eine Ausgabe wegfiel. Der Handle ist
                        ' danach nur noch zum Wegwerfen gut, und wer das nicht erfährt, schickt
                        ' Befehle ins Leere und hält eine Oberfläche fest, die auf ein längst
                        ' totes Video zeigt.
                        RaiseEvent PlaybackTerminated()
                        Exit Do
                End Select
            Loop
        End Sub

        Private Sub HandlePropertyChange(ev As MpvInterop.MpvEvent)
            If ev.Data = IntPtr.Zero Then Return
            Dim prop = Marshal.PtrToStructure(Of MpvInterop.MpvEventProperty)(ev.Data)

            Select Case ev.ReplyUserData
                Case PropTimePos
                    If prop.Format <> MpvInterop.MpvFormat.Double OrElse prop.Data = IntPtr.Zero Then Return
                    RaiseEvent TimeChanged(Marshal.PtrToStructure(Of Double)(prop.Data))
                Case PropDuration
                    If prop.Format <> MpvInterop.MpvFormat.Double OrElse prop.Data = IntPtr.Zero Then Return
                    RaiseEvent DurationChanged(Marshal.PtrToStructure(Of Double)(prop.Data))
                Case PropPause
                    If prop.Format <> MpvInterop.MpvFormat.Flag OrElse prop.Data = IntPtr.Zero Then Return
                    Dim paused = Marshal.ReadInt32(prop.Data) <> 0
                    SyncLock _syncRoot
                        _isPaused = paused
                    End SyncLock
                    RaiseEvent PauseChanged(paused)
                Case PropMute
                    If prop.Format <> MpvInterop.MpvFormat.Flag OrElse prop.Data = IntPtr.Zero Then Return
                    Dim muted = Marshal.ReadInt32(prop.Data) <> 0
                    SyncLock _syncRoot
                        _isMuted = muted
                    End SyncLock
                    RaiseEvent MuteChanged(muted)
            End Select
        End Sub

        ' ── Native Aufrufe, ausschließlich vom Befehlsfaden ──────────────────────

        Private Sub ObservePropertyRaw(replyUserData As ULong, propertyName As String, fileFormat As MpvInterop.MpvFormat)
            Using namePtr = New Utf8String(propertyName)
                Dim result = MpvInterop.ObserveProperty(_handle, replyUserData, namePtr.Pointer, fileFormat)
                If result < 0 Then Throw New InvalidOperationException($"libmpv observe_property({propertyName}) fehlgeschlagen ({result}).")
            End Using
        End Sub

        Private Shared Function CommandRaw(handle As IntPtr, ParamArray args As String()) As Integer
            If handle = IntPtr.Zero Then Return -1

            Dim allocations As New List(Of Utf8String)()
            Dim ptrs As New List(Of IntPtr)()
            Try
                For Each arg In args
                    Dim utf8 = New Utf8String(arg)
                    allocations.Add(utf8)
                    ptrs.Add(utf8.Pointer)
                Next
                ptrs.Add(IntPtr.Zero)

                Dim arrayPtr = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Count)
                Try
                    For i = 0 To ptrs.Count - 1
                        Marshal.WriteIntPtr(arrayPtr, i * IntPtr.Size, ptrs(i))
                    Next
                    Return MpvInterop.Command(handle, arrayPtr)
                Finally
                    Marshal.FreeHGlobal(arrayPtr)
                End Try
            Finally
                For Each allocation In allocations
                    allocation.Dispose()
                Next
            End Try
        End Function

        Private Shared Function CommandAsyncRaw(handle As IntPtr, ParamArray args As String()) As Integer
            If handle = IntPtr.Zero Then Return -1

            Dim allocations As New List(Of Utf8String)()
            Dim ptrs As New List(Of IntPtr)()
            Try
                For Each arg In args
                    Dim utf8 = New Utf8String(arg)
                    allocations.Add(utf8)
                    ptrs.Add(utf8.Pointer)
                Next
                ptrs.Add(IntPtr.Zero)
                Dim arrayPtr = Marshal.AllocHGlobal(IntPtr.Size * ptrs.Count)
                Try
                    For i = 0 To ptrs.Count - 1
                        Marshal.WriteIntPtr(arrayPtr, i * IntPtr.Size, ptrs(i))
                    Next
                    Return MpvInterop.CommandAsync(handle, 0UL, arrayPtr)
                Finally
                    Marshal.FreeHGlobal(arrayPtr)
                End Try
            Finally
                For Each allocation In allocations
                    allocation.Dispose()
                Next
            End Try
        End Function

        Private Sub SetOptionStringRaw(name As String, value As String)
            Using namePtr = New Utf8String(name), valuePtr = New Utf8String(value)
                Dim result = MpvInterop.SetOptionString(_handle, namePtr.Pointer, valuePtr.Pointer)
                If result < 0 Then Throw New InvalidOperationException($"libmpv option {name} fehlgeschlagen ({result}).")
            End Using
        End Sub

        Private Sub SetPropertyStringRaw(name As String, value As String)
            Using namePtr = New Utf8String(name), valuePtr = New Utf8String(value)
                MpvInterop.SetPropertyString(_handle, namePtr.Pointer, valuePtr.Pointer)
            End Using
        End Sub

        Private Shared Function WindowHandleToUnsignedString(handle As IntPtr) As String
            If OperatingSystem.IsLinux() Then
                Return CUInt(handle.ToInt64() And &HFFFFFFFFL).ToString(CultureInfo.InvariantCulture)
            End If

            If IntPtr.Size <= 4 Then
                Return CUInt(handle.ToInt32()).ToString(CultureInfo.InvariantCulture)
            End If

            Dim bytes = BitConverter.GetBytes(handle.ToInt64())
            Return BitConverter.ToUInt64(bytes, 0).ToString(CultureInfo.InvariantCulture)
        End Function

        Private NotInheritable Class Utf8String
            Implements IDisposable

            Public ReadOnly Property Pointer As IntPtr

            Public Sub New(value As String)
                Dim bytes = System.Text.Encoding.UTF8.GetBytes(If(value, String.Empty) & ChrW(0))
                Pointer = Marshal.AllocHGlobal(bytes.Length)
                Marshal.Copy(bytes, 0, Pointer, bytes.Length)
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                If Pointer <> IntPtr.Zero Then Marshal.FreeHGlobal(Pointer)
            End Sub
        End Class
    End Class

End Namespace
