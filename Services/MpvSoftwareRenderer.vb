Imports System
Imports System.Runtime.InteropServices
Imports System.Threading
Imports Avalonia
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform
Imports Avalonia.Threading

Namespace Services

    ''' <summary>Holt das Videobild aus libmpv ab, statt mpv in ein Fenster zeichnen zu lassen.
    '''
    ''' Gebraucht wird das unter macOS, und dort zwingend: die Option <c>wid</c>, mit der das Video
    ''' unter Windows und Linux in eine Fläche der Anwendung eingehängt wird, hat auf dem Mac kein
    ''' Ziel. Die mpv-Anleitung nennt bei <c>--wid</c> nur X11, win32 und Android, und im macOS-Teil
    ''' von mpv wird die Option nirgends ausgewertet - mpv macht dort immer ein EIGENES Fenster auf.
    ''' Genau das war der Bericht eines Anwenders: das Video erschien in einem separaten Fenster,
    ''' das sich vor und hinter das Hauptfenster schob und dessen rotes Schließkreuz die Anwendung
    ''' mitriss.
    '''
    ''' <para>Der Weg hier ist der zweite Ausgabeweg von libmpv: mpv zeichnet das fertige Bild in
    ''' einen Speicherbereich, den wir stellen, und wir zeigen es als gewöhnliche Bitmap an. Damit
    ''' gibt es kein natives Fenster mehr, die Bedienleiste liegt sichtbar darüber, und Mausklicks
    ''' erreichen die Oberfläche.</para>
    '''
    ''' <para>DER PREIS ist Rechenzeit: Farbwandlung und Skalierung laufen auf dem Prozessor, in
    ''' einem Faden. Deshalb ist die Zielfläche begrenzt (siehe <see cref="MaxSurfaceWidth"/> beim
    ''' aufrufenden Steuerelement) - ein bildschirmfüllendes Fenster auf einem hochauflösenden
    ''' Bildschirm würde sonst mehr Bildpunkte umrechnen lassen, als ein Video je braucht.</para>
    '''
    ''' <para>FADENREGELN von libmpv, die hier eingehalten werden müssen: die
    ''' <c>mpv_render_*</c>-Funktionen dürfen nie gleichzeitig laufen, nie aus dem Rückruf heraus,
    ''' und der Zeichenfaden darf auf keinen Faden warten, der gerade in einer anderen
    ''' libmpv-Funktion steht. Deshalb hat dieser Renderer einen EIGENEN Faden, der außer den
    ''' Render-Funktionen nichts von libmpv anfasst, und teilt keine Sperre mit dem Befehlsfaden
    ''' des Spielers.</para></summary>
    Public NotInheritable Class MpvSoftwareRenderer
        Implements IDisposable

        ''' <summary>Bildzeilen werden auf 64 Bytes ausgerichtet: libmpv arbeitet dann mit den
        ''' schnellen Kopierbefehlen des Prozessors statt mit dem Rückfallpfad.</summary>
        Private Const StrideAlignment As Integer = 64

        ''' <summary>So oft darf das Zeichnen scheitern, bevor der Faden aufgibt. Ohne Grenze
        ''' würde ein dauerhafter Fehler die Protokolldatei fluten.</summary>
        Private Const MaxRenderFailures As Integer = 5

        Private ReadOnly _sync As New Object()
        Private ReadOnly _wake As New AutoResetEvent(False)

        Private _context As IntPtr = IntPtr.Zero
        Private _thread As Thread
        Private _stopping As Boolean = False
        Private _updateCallback As MpvInterop.RenderUpdateCallback

        Private _requestedWidth As Integer = 0
        Private _requestedHeight As Integer = 0

        ' Nur der Zeichenfaden fasst diese Felder an (und Shutdown, nachdem er beendet ist).
        Private _frame As Byte()
        Private _frameHandle As GCHandle
        Private _frameWidth As Integer = 0
        Private _frameHeight As Integer = 0
        Private _frameStride As Integer = 0

        Private _bitmap As WriteableBitmap

        ''' <summary>Ein neues Bild steht in <see cref="CurrentFrame"/>. Wird aus dem Zeichenfaden
        ''' ausgelöst, nicht aus dem Anzeigefaden.</summary>
        Public Event FrameReady()

        ''' <summary>Das zuletzt fertig gezeichnete Bild, oder Nothing, solange keines vorliegt.</summary>
        Public ReadOnly Property CurrentFrame As WriteableBitmap
            Get
                SyncLock _sync
                    Return _bitmap
                End SyncLock
            End Get
        End Property

        ''' <summary>Die gewünschte Größe der Zielfläche in Bildpunkten. Setzt das Steuerelement,
        ''' sobald es seine Größe kennt; der Zeichenfaden zieht beim nächsten Bild nach.</summary>
        Public Sub SetSurfaceSize(width As Integer, height As Integer)
            Volatile.Write(_requestedWidth, Math.Max(0, width))
            Volatile.Write(_requestedHeight, Math.Max(0, height))
            _wake.Set()
        End Sub

        ''' <summary>Legt den Zeichenkontext an und startet den Zeichenfaden. Läuft auf dem
        ''' Befehlsfaden des Spielers, NICHT auf dem Anzeigefaden.</summary>
        Friend Function TryCreate(handle As IntPtr) As Boolean
            If handle = IntPtr.Zero Then Return False

            Dim block = Marshal.AllocHGlobal(MpvInterop.RenderParamSize * 2)
            Dim apiType = Marshal.StringToHGlobalAnsi(MpvInterop.RenderApiTypeSoftware)
            Try
                MpvInterop.WriteRenderParam(block, 0, MpvInterop.RenderParamApiType, apiType)
                MpvInterop.WriteRenderParam(block, 1, 0, IntPtr.Zero)

                Dim context As IntPtr = IntPtr.Zero
                Dim result = MpvInterop.RenderContextCreate(context, handle, block)
                If result < 0 OrElse context = IntPtr.Zero Then
                    DiagnosticLogService.LogAlways("VideoPlayback.Render",
                                                   $"mpv_render_context_create fehlgeschlagen ({result})")
                    Return False
                End If

                _context = context
                ' Der Rückruf muss verankert bleiben, solange mpv ihn kennt: ein nur lokal
                ' gehaltener Delegat wird eingesammelt, und der Rücksprung landet im Nichts.
                _updateCallback = AddressOf OnRenderUpdate
                MpvInterop.RenderContextSetUpdateCallback(context, _updateCallback, IntPtr.Zero)

                _thread = New Thread(AddressOf RenderLoop) With {
                    .IsBackground = True,
                    .Name = "libmpv-render"
                }
                _thread.Start()
                Return True
            Finally
                Marshal.FreeHGlobal(block)
                Marshal.FreeHGlobal(apiType)
            End Try
        End Function

        ''' <summary>Beendet den Zeichenfaden und gibt den Kontext frei. Läuft auf dem Befehlsfaden
        ''' des Spielers: <c>mpv_render_context_free</c> darf warten, bis mpv die Videokette
        ''' abgebaut hat, und das darf den Anzeigefaden nicht treffen.</summary>
        Friend Sub Shutdown()
            Dim worker As Thread
            SyncLock _sync
                If _stopping Then Return
                _stopping = True
                worker = _thread
                _thread = Nothing
            End SyncLock

            _wake.Set()

            If worker IsNot Nothing AndAlso worker.IsAlive AndAlso
               Not Object.ReferenceEquals(Thread.CurrentThread, worker) Then
                ' Der Faden steht im Normalfall höchstens die Anzeigedauer eines Bildes im
                ' Zeichnen. Kommt er nicht zurück, wird das GEMELDET und trotzdem gewartet: den
                ' Kontext freizugeben, während er noch darin zeichnet, stürzt ab.
                If Not worker.Join(2000) Then
                    DiagnosticLogService.LogException("VideoPlayback.Render",
                                                      New TimeoutException("mpv render loop did not stop within two seconds; waiting."))
                    worker.Join()
                End If
            End If

            Dim context = Interlocked.Exchange(_context, IntPtr.Zero)
            If context <> IntPtr.Zero Then
                Try
                    MpvInterop.RenderContextSetUpdateCallback(context, Nothing, IntPtr.Zero)
                    MpvInterop.RenderContextFree(context)
                Catch ex As Exception
                    DiagnosticLogService.LogException("VideoPlayback.Render", ex)
                End Try
            End If

            _updateCallback = Nothing
            FreeSurface()

            ' Der Zeichenfaden steht hier bereits. Erst herausnehmen, dann freigeben: eine Anzeige,
            ' die gleich noch einmal laeuft, bekommt so Nothing und nicht eine Bitmap, die gerade
            ' verschwindet.
            Dim last As WriteableBitmap
            SyncLock _sync
                last = _bitmap
                _bitmap = Nothing
            End SyncLock
            RetireBitmap(last)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Shutdown()
        End Sub

        ''' <summary>Der Rückruf von mpv. Er darf nichts tun als wecken - jede libmpv-Funktion
        ''' wäre hier ein Verstoß gegen die Fadenregeln der Render-API.</summary>
        Private Sub OnRenderUpdate(callbackContext As IntPtr)
            Try
                _wake.Set()
            Catch
            End Try
        End Sub

        Private Sub RenderLoop()
            Dim failures = 0

            Do
                _wake.WaitOne(100)
                If Volatile.Read(_stopping) Then Exit Do

                Dim context = _context
                If context = IntPtr.Zero Then Continue Do

                Dim flags = MpvInterop.RenderContextUpdate(context)

                Dim width = Volatile.Read(_requestedWidth)
                Dim height = Volatile.Read(_requestedHeight)
                If width < 16 OrElse height < 16 Then Continue Do

                ' Auch ohne neues Einzelbild muss gezeichnet werden, wenn sich die Fläche geändert
                ' hat: mpv wiederholt dann das letzte Bild in der neuen Größe.
                Dim sizeChanged = width <> _frameWidth OrElse height <> _frameHeight
                If (flags And MpvInterop.RenderUpdateFrame) = 0UL AndAlso Not sizeChanged Then Continue Do

                Try
                    EnsureSurface(width, height)
                    If RenderFrame(context) Then
                        PublishFrame()
                        failures = 0
                    End If
                Catch ex As Exception
                    failures += 1
                    DiagnosticLogService.LogException("VideoPlayback.Render", ex)
                    If failures >= MaxRenderFailures Then Exit Do
                End Try
            Loop
        End Sub

        ''' <summary>Legt Zwischenspeicher und Zielbitmap in der geforderten Größe an.</summary>
        Private Sub EnsureSurface(width As Integer, height As Integer)
            If width = _frameWidth AndAlso height = _frameHeight AndAlso _frame IsNot Nothing Then Return

            FreeSurface()

            ' Die Klammern um die ganzzahlige Division sind PFLICHT: in VB bindet "*" stärker als
            ' "\", ohne sie stünde hier eine Division durch 64 mal 64.
            Dim stride = (((width * 4) + StrideAlignment - 1) \ StrideAlignment) * StrideAlignment
            _frame = New Byte(stride * height - 1) {}
            ' mpv schreibt direkt in dieses Feld; es muss dafür festgenagelt sein.
            _frameHandle = GCHandle.Alloc(_frame, GCHandleType.Pinned)
            _frameWidth = width
            _frameHeight = height
            _frameStride = stride

            ' Undurchsichtig, weil das vierte Byte je Bildpunkt bei "bgr0" nicht gesetzt wird:
            ' als Alphakanal gelesen wäre das Video durchsichtig.
            Dim bitmap = New WriteableBitmap(New PixelSize(width, height),
                                             New Vector(96, 96),
                                             PixelFormat.Bgra8888,
                                             AlphaFormat.Opaque)
            Dim previous As WriteableBitmap
            SyncLock _sync
                previous = _bitmap
                _bitmap = bitmap
            End SyncLock
            RetireBitmap(previous)
        End Sub

        ''' <summary>Gibt eine abgeloeste Bitmap frei, und zwar AUF DEM ANZEIGEFADEN.
        '''
        ''' WARUM UEBERHAUPT: eine WriteableBitmap ist verwaltet nur eine Handvoll Bytes und haelt
        ''' dabei die ganze Bildflaeche in nativem Speicher. Der Sammler sieht davon nichts und
        ''' laesst sich Zeit; bei jeder Groessenaenderung und jedem Wechsel in den Vollbildmodus
        ''' entsteht aber eine neue. Liegengelassen wachsen sie sich zu einem spuerbaren Betrag aus,
        ''' obwohl die Anwendung nach dem Zaehler des Sammlers nichts verbraucht.
        '''
        ''' WARUM DAS SICHER IST: der Zeichenbaum haelt seine EIGENE Zaehlmarke auf die
        ''' Bildflaeche - <c>DrawImage</c> vervielfaeltigt sie beim Aufzeichnen (in Avalonia
        ''' nachgesehen). Das Freigeben nimmt also nur unsere Marke zurueck; die Flaeche selbst
        ''' faellt erst, wenn auch der Zeichenbaum sie hergibt.
        '''
        ''' WARUM AUF DEM ANZEIGEFADEN: dort und nur dort laeuft <c>Render</c>. Wer die Bitmap vom
        ''' Zeichenfaden aus freigaebe, koennte das mitten in einem Anzeigedurchlauf tun, der sie
        ''' gerade aus <see cref="CurrentFrame"/> geholt, aber noch nicht aufgezeichnet hat. Auf dem
        ''' Anzeigefaden kann sich beides nicht verschraenken. Kommt die Nachricht nicht mehr an -
        ''' beim Beenden ist die Warteschlange schon zu -, bleibt es beim alten Verhalten und der
        ''' Sammler raeumt auf.</summary>
        Private Shared Sub RetireBitmap(bitmap As WriteableBitmap)
            If bitmap Is Nothing Then Return
            Try
                If Dispatcher.UIThread.CheckAccess() Then
                    bitmap.Dispose()
                Else
                    Dispatcher.UIThread.Post(Sub() bitmap.Dispose(), DispatcherPriority.Background)
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("VideoPlayback.Render", ex)
            End Try
        End Sub

        ''' <summary>Nur der Zwischenspeicher, NICHT die Bitmap: beim Groessenwechsel muss die alte
        ''' noch anzeigbar sein, bis die neue steht. Sie wird an ihren zwei Stellen einzeln
        ''' abgeloest (siehe <see cref="RetireBitmap"/>).</summary>
        Private Sub FreeSurface()
            If _frameHandle.IsAllocated Then _frameHandle.Free()
            _frame = Nothing
            _frameWidth = 0
            _frameHeight = 0
            _frameStride = 0
        End Sub

        Private Function RenderFrame(context As IntPtr) As Boolean
            If _frame Is Nothing OrElse Not _frameHandle.IsAllocated Then Return False

            Dim block = Marshal.AllocHGlobal(MpvInterop.RenderParamSize * 5)
            Dim sizePtr = Marshal.AllocHGlobal(8)
            Dim stridePtr = Marshal.AllocHGlobal(IntPtr.Size)
            Dim formatPtr = Marshal.StringToHGlobalAnsi(MpvInterop.RenderFormatBgr0)
            Try
                Marshal.WriteInt32(sizePtr, 0, _frameWidth)
                Marshal.WriteInt32(sizePtr, 4, _frameHeight)
                Marshal.WriteIntPtr(stridePtr, New IntPtr(_frameStride))

                MpvInterop.WriteRenderParam(block, 0, MpvInterop.RenderParamSoftwareSize, sizePtr)
                MpvInterop.WriteRenderParam(block, 1, MpvInterop.RenderParamSoftwareFormat, formatPtr)
                MpvInterop.WriteRenderParam(block, 2, MpvInterop.RenderParamSoftwareStride, stridePtr)
                MpvInterop.WriteRenderParam(block, 3, MpvInterop.RenderParamSoftwarePointer, _frameHandle.AddrOfPinnedObject())
                MpvInterop.WriteRenderParam(block, 4, 0, IntPtr.Zero)

                Return MpvInterop.RenderContextRender(context, block) >= 0
            Finally
                Marshal.FreeHGlobal(block)
                Marshal.FreeHGlobal(sizePtr)
                Marshal.FreeHGlobal(stridePtr)
                Marshal.FreeHGlobal(formatPtr)
            End Try
        End Function

        ''' <summary>Kopiert das fertige Bild in die Bitmap. Die Sperre der Bitmap wird NUR für das
        ''' Kopieren gehalten - sie ist dieselbe, die der Zeichenbaum beim Anzeigen nimmt, und darf
        ''' nicht über das (wartende) Zeichnen von mpv gehalten werden.</summary>
        Private Sub PublishFrame()
            Dim bitmap As WriteableBitmap
            SyncLock _sync
                bitmap = _bitmap
            End SyncLock
            If bitmap Is Nothing Then Return

            Using buffer = bitmap.Lock()
                If buffer.RowBytes = _frameStride Then
                    Marshal.Copy(_frame, 0, buffer.Address, _frameStride * _frameHeight)
                Else
                    Dim rowBytes = Math.Min(buffer.RowBytes, _frameStride)
                    For y = 0 To _frameHeight - 1
                        Marshal.Copy(_frame, y * _frameStride, IntPtr.Add(buffer.Address, y * buffer.RowBytes), rowBytes)
                    Next
                End If
            End Using

            RaiseEvent FrameReady()
        End Sub
    End Class

End Namespace
