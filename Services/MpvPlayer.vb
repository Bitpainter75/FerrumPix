Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Runtime.InteropServices
Imports System.Threading

Namespace Services

    Public NotInheritable Class MpvPlayer
        Implements IDisposable

        Private Const PropTimePos As ULong = 1UL
        Private Const PropDuration As ULong = 2UL
        Private Const PropPause As ULong = 3UL
        Private Const PropMute As ULong = 4UL

        Private ReadOnly _syncRoot As New Object()
        Private ReadOnly _enableHardwareAcceleration As Boolean
        ' Normale libmpv-Befehle laufen getrennt vom OpenGL-Renderthread. Das ist eine harte
        ' Anforderung der Render-API und verhindert gleichzeitig, dass Datei-/Seek-Befehle die
        ' Avalonia-Oberfläche blockieren.
        Private ReadOnly _commandQueue As New BlockingCollection(Of Action)()
        Private ReadOnly _commandThread As Thread
        Private ReadOnly _commandQueueLock As New Object()
        Private _disposeQueued As Boolean = False
        Private _disposeRequested As Boolean = False

        Private _handle As IntPtr = IntPtr.Zero
        Private _renderContext As IntPtr = IntPtr.Zero
        Private _getProcAddressCallback As MpvInterop.MpvOpenGlGetProcAddress
        Private _renderUpdateCallback As MpvInterop.MpvRenderUpdateCallback
        Private _requestRender As Action
        Private _eventThread As Thread
        Private _disposed As Boolean = False
        Private _initialized As Boolean = False
        Private _pendingPath As String = Nothing
        Private _pendingPlay As Boolean = False
        Private _isPaused As Boolean = True
        Private _isMuted As Boolean = False
        Private _initializationFailed As Boolean = False

        Public Event TimeChanged(seconds As Double)
        Public Event DurationChanged(seconds As Double)
        Public Event PauseChanged(isPaused As Boolean)
        Public Event MuteChanged(isMuted As Boolean)
        Public Event EndReached(reason As Integer, [error] As Integer)
        Public Event InitializationFailed([error] As Exception)

        Public Sub New(enableHardwareAcceleration As Boolean)
            _enableHardwareAcceleration = enableHardwareAcceleration
            _commandThread = New Thread(AddressOf CommandLoop) With {
                .IsBackground = True,
                .Name = "libmpv-command-loop"
            }
            _commandThread.Start()
        End Sub

        Friend ReadOnly Property IsDisposeRequested As Boolean
            Get
                SyncLock _syncRoot
                    Return _disposeRequested
                End SyncLock
            End Get
        End Property

        ''' <summary>Bindet libmpv an den aktuell gesetzten Avalonia-OpenGL-Kontext.
        ''' Muss ausschließlich aus OpenGlControlBase.OnOpenGl* aufgerufen werden.</summary>
        Friend Function AttachOpenGl(getProcAddress As Func(Of String, IntPtr), requestRender As Action) As Boolean
            If getProcAddress Is Nothing OrElse requestRender Is Nothing Then Return False

            Try
                SyncLock _syncRoot
                    If _disposed OrElse _disposeRequested OrElse _initializationFailed Then Return False
                    If _renderContext <> IntPtr.Zero Then Return True

                    InitializeCoreLocked()

                    _getProcAddressCallback =
                        Function(context As IntPtr, namePointer As IntPtr) As IntPtr
                            Try
                                Dim name = Marshal.PtrToStringUTF8(namePointer)
                                If String.IsNullOrEmpty(name) Then Return IntPtr.Zero
                                Return getProcAddress(name)
                            Catch
                                Return IntPtr.Zero
                            End Try
                        End Function

                    _renderUpdateCallback =
                        Sub(context As IntPtr)
                            Try
                                requestRender()
                            Catch
                            End Try
                        End Sub
                    _requestRender = requestRender

                    Using apiType As New Utf8String("opengl")
                        Dim initParams = New MpvInterop.MpvOpenGlInitParams With {
                            .GetProcAddress = _getProcAddressCallback,
                            .GetProcAddressContext = IntPtr.Zero
                        }
                        Dim initParamsPointer = Marshal.AllocHGlobal(Marshal.SizeOf(Of MpvInterop.MpvOpenGlInitParams)())
                        Try
                            Marshal.StructureToPtr(initParams, initParamsPointer, False)
                            Dim parameters = {
                                New MpvInterop.MpvRenderParam With {
                                    .Type = MpvInterop.MpvRenderParamType.ApiType,
                                    .Data = apiType.Pointer
                                },
                                New MpvInterop.MpvRenderParam With {
                                    .Type = MpvInterop.MpvRenderParamType.OpenGlInitParams,
                                    .Data = initParamsPointer
                                },
                                New MpvInterop.MpvRenderParam With {
                                    .Type = MpvInterop.MpvRenderParamType.Invalid,
                                    .Data = IntPtr.Zero
                                }
                            }
                            Dim context As IntPtr = IntPtr.Zero
                            Dim result = MpvInterop.RenderContextCreate(context, _handle, parameters)
                            If result < 0 OrElse context = IntPtr.Zero Then
                                Throw New InvalidOperationException($"libmpv OpenGL-Renderer konnte nicht initialisiert werden ({result}).")
                            End If
                            _renderContext = context
                        Finally
                            Marshal.FreeHGlobal(initParamsPointer)
                        End Try
                    End Using

                    MpvInterop.RenderContextSetUpdateCallback(_renderContext, _renderUpdateCallback, IntPtr.Zero)
                End SyncLock

                LoadPending()
                Return True
            Catch ex As Exception
                HandleInitializationFailure(ex)
                Return False
            End Try
        End Function

        ''' <summary>Löst den OpenGL-Renderer, solange Avalonia den zugehörigen Kontext current hält.</summary>
        Friend Sub DetachOpenGl()
            Dim context As IntPtr
            Dim shouldDispose As Boolean
            SyncLock _syncRoot
                context = _renderContext
                _renderContext = IntPtr.Zero
                _requestRender = Nothing
                shouldDispose = _disposeRequested
            End SyncLock

            If context <> IntPtr.Zero Then
                Try
                    MpvInterop.RenderContextSetUpdateCallback(context, Nothing, IntPtr.Zero)
                Catch
                End Try
                MpvInterop.RenderContextFree(context)
            End If

            _renderUpdateCallback = Nothing
            _getProcAddressCallback = Nothing
            If shouldDispose Then QueueDispose()
        End Sub

        ''' <summary>Zeichnet den nächsten Frame in Avalonias aktuellen Framebuffer.
        ''' Muss ausschließlich aus OpenGlControlBase.OnOpenGlRender aufgerufen werden.</summary>
        Friend Function RenderOpenGlFrame(framebuffer As Integer, width As Integer, height As Integer) As Boolean
            Dim context As IntPtr
            SyncLock _syncRoot
                context = _renderContext
            End SyncLock
            If context = IntPtr.Zero OrElse width <= 0 OrElse height <= 0 Then Return False

            MpvInterop.RenderContextUpdate(context)

            Dim fbo = New MpvInterop.MpvOpenGlFbo With {
                .Fbo = framebuffer,
                .Width = width,
                .Height = height,
                .InternalFormat = 0
            }
            Dim flipY As Integer = 1
            Dim fboPointer = Marshal.AllocHGlobal(Marshal.SizeOf(Of MpvInterop.MpvOpenGlFbo)())
            Dim flipPointer = Marshal.AllocHGlobal(Marshal.SizeOf(Of Integer)())
            Try
                Marshal.StructureToPtr(fbo, fboPointer, False)
                Marshal.WriteInt32(flipPointer, flipY)
                Dim parameters = {
                    New MpvInterop.MpvRenderParam With {
                        .Type = MpvInterop.MpvRenderParamType.OpenGlFbo,
                        .Data = fboPointer
                    },
                    New MpvInterop.MpvRenderParam With {
                        .Type = MpvInterop.MpvRenderParamType.FlipY,
                        .Data = flipPointer
                    },
                    New MpvInterop.MpvRenderParam With {
                        .Type = MpvInterop.MpvRenderParamType.Invalid,
                        .Data = IntPtr.Zero
                    }
                }
                Dim result = MpvInterop.RenderContextRender(context, parameters)
                If result >= 0 Then MpvInterop.RenderContextReportSwap(context)
                Return result >= 0
            Finally
                Marshal.FreeHGlobal(flipPointer)
                Marshal.FreeHGlobal(fboPointer)
            End Try
        End Function

        Public Sub Load(path As String)
            If String.IsNullOrWhiteSpace(path) Then Return
            EnqueueCommand(
                Sub()
                    SyncLock _syncRoot
                        _pendingPath = path
                        If _initializationFailed OrElse Not _initialized OrElse _renderContext = IntPtr.Zero Then Return
                        SetPauseLocked(True)
                        Dim result = CommandLocked("loadfile", path, "replace")
                        If result >= 0 AndAlso _pendingPlay Then SetPauseLocked(False)
                    End SyncLock
                End Sub)
        End Sub

        Public Sub LoadPending()
            EnqueueCommand(
                Sub()
                    SyncLock _syncRoot
                        If String.IsNullOrWhiteSpace(_pendingPath) Then Return
                        If _initializationFailed OrElse Not _initialized OrElse _renderContext = IntPtr.Zero Then Return
                        SetPauseLocked(True)
                        Dim result = CommandLocked("loadfile", _pendingPath, "replace")
                        If result >= 0 AndAlso _pendingPlay Then SetPauseLocked(False)
                    End SyncLock
                End Sub)
        End Sub

        Public Sub Play()
            EnqueueCommand(
                Sub()
                    SyncLock _syncRoot
                        _pendingPlay = True
                        If _initialized AndAlso Not _initializationFailed Then SetPauseLocked(False)
                    End SyncLock
                End Sub)
        End Sub

        Public Sub Pause()
            EnqueueCommand(
                Sub()
                    SyncLock _syncRoot
                        _pendingPlay = False
                        If _initialized AndAlso Not _initializationFailed Then SetPauseLocked(True)
                    End SyncLock
                End Sub)
        End Sub

        Public Sub [Stop]()
            EnqueueCommand(
                Sub()
                    SyncLock _syncRoot
                        _pendingPlay = False
                        If _initialized AndAlso Not _initializationFailed Then CommandLocked("stop")
                    End SyncLock
                End Sub)
        End Sub

        Public Sub Seek(seconds As Double)
            EnqueueCommand(
                Sub()
                    SyncLock _syncRoot
                        If _initializationFailed OrElse Not _initialized Then Return
                        SetPropertyStringLocked("time-pos", Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture))
                    End SyncLock
                End Sub)
        End Sub

        Public Sub SetMuted(value As Boolean)
            EnqueueCommand(
                Sub()
                    SyncLock _syncRoot
                        _isMuted = value
                        If _initialized AndAlso Not _initializationFailed Then SetPropertyStringLocked("mute", If(value, "yes", "no"))
                    End SyncLock
                End Sub)
        End Sub

        Private Sub EnqueueCommand(workItem As Action)
            If workItem Is Nothing Then Return
            SyncLock _commandQueueLock
                If _disposeRequested OrElse _disposeQueued OrElse _commandQueue.IsAddingCompleted Then Return
                _commandQueue.Add(workItem)
            End SyncLock
        End Sub

        Private Sub CommandLoop()
            For Each workItem In _commandQueue.GetConsumingEnumerable()
                Try
                    workItem()
                Catch ex As Exception
                    DiagnosticLogService.LogException("VideoPlayback.Command", ex)
                End Try
            Next
        End Sub

        Private Sub HandleInitializationFailure(ex As Exception)
            Dim handleToDestroy As IntPtr = IntPtr.Zero
            Dim eventThread As Thread = Nothing
            SyncLock _syncRoot
                _initializationFailed = True
                _disposed = True
                _initialized = False
                handleToDestroy = _handle
                eventThread = _eventThread
            End SyncLock

            If handleToDestroy <> IntPtr.Zero Then
                Try
                    CommandRaw(handleToDestroy, "quit")
                Catch
                End Try
            End If

            If eventThread IsNot Nothing AndAlso
               eventThread.IsAlive AndAlso
               Not Object.ReferenceEquals(Thread.CurrentThread, eventThread) Then
                eventThread.Join(2000)
            End If

            SyncLock _syncRoot
                _handle = IntPtr.Zero
                _eventThread = Nothing
            End SyncLock

            If handleToDestroy <> IntPtr.Zero AndAlso (eventThread Is Nothing OrElse Not eventThread.IsAlive) Then
                Try
                    MpvInterop.TerminateDestroy(handleToDestroy)
                Catch
                End Try
            End If

            RaiseEvent InitializationFailed(ex)
        End Sub

        Private Sub InitializeCoreLocked()
            If _initializationFailed OrElse _initialized Then Return

            MpvInterop.EnsureResolver()
            _handle = MpvInterop.Create()
            If _handle = IntPtr.Zero Then Throw New InvalidOperationException("libmpv konnte nicht erstellt werden.")

            SetOptionStringLocked("terminal", "no")
            ' Siehe VideoPreviewService: FFmpeg-Demuxer-Hinweise nicht auf die Konsole durchlassen.
            SetOptionStringLocked("msg-level", "all=no")
            SetOptionStringLocked("config", "no")
            SetOptionStringLocked("input-default-bindings", "no")
            ' Die gebündelte Minimal-Laufzeit enthält kein OSC-Skript. Das Abschalten ist optional:
            ' FerrumPix zeichnet seine eigenen Steuerelemente und braucht den mpv-OSC nicht.
            TrySetOptionStringLocked("osc", "no")
            SetOptionStringLocked("keep-open", "no")
            SetOptionStringLocked("vo", "libmpv")
            ' Wie "cover" bei Bildern: Seitenverhältnis bleibt erhalten, der Frame füllt aber
            ' die gesamte Ausgabefläche, statt links/rechts oder oben/unten schwarze Balken zu lassen.
            SetOptionStringLocked("panscan", "1.0")
            SetOptionStringLocked("hwdec", If(_enableHardwareAcceleration, "auto-safe", "no"))

            Dim result = MpvInterop.Initialize(_handle)
            If result < 0 Then Throw New InvalidOperationException($"libmpv konnte nicht initialisiert werden ({result}).")

            ObservePropertyLocked(PropTimePos, "time-pos", MpvInterop.MpvFormat.Double)
            ObservePropertyLocked(PropDuration, "duration", MpvInterop.MpvFormat.Double)
            ObservePropertyLocked(PropPause, "pause", MpvInterop.MpvFormat.Flag)
            ObservePropertyLocked(PropMute, "mute", MpvInterop.MpvFormat.Flag)
            SetPropertyStringLocked("mute", If(_isMuted, "yes", "no"))

            _eventThread = New Thread(AddressOf EventLoop) With {
                .IsBackground = True,
                .Name = "libmpv-event-loop"
            }
            _initialized = True
            _eventThread.Start()
        End Sub

        Private Sub EventLoop()
            Do
                Dim handleSnapshot As IntPtr
                SyncLock _syncRoot
                    If _disposed OrElse _handle = IntPtr.Zero Then Exit Do
                    handleSnapshot = _handle
                End SyncLock

                Dim eventPtr = MpvInterop.WaitEvent(handleSnapshot, 0.2)
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
                    _isPaused = Marshal.ReadInt32(prop.Data) <> 0
                    RaiseEvent PauseChanged(_isPaused)
                Case PropMute
                    If prop.Format <> MpvInterop.MpvFormat.Flag OrElse prop.Data = IntPtr.Zero Then Return
                    _isMuted = Marshal.ReadInt32(prop.Data) <> 0
                    RaiseEvent MuteChanged(_isMuted)
            End Select
        End Sub

        Private Sub ObservePropertyLocked(replyUserData As ULong, propertyName As String, format As MpvInterop.MpvFormat)
            Using namePtr = New Utf8String(propertyName)
                Dim result = MpvInterop.ObserveProperty(_handle, replyUserData, namePtr.Pointer, format)
                If result < 0 Then Throw New InvalidOperationException($"libmpv observe_property({propertyName}) fehlgeschlagen ({result}).")
            End Using
        End Sub

        Private Function CommandLocked(ParamArray args As String()) As Integer
            Return CommandRaw(_handle, args)
        End Function

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

        Private Sub SetOptionStringLocked(name As String, value As String)
            Using namePtr = New Utf8String(name), valuePtr = New Utf8String(value)
                Dim result = MpvInterop.SetOptionString(_handle, namePtr.Pointer, valuePtr.Pointer)
                If result < 0 Then Throw New InvalidOperationException($"libmpv option {name} fehlgeschlagen ({result}).")
            End Using
        End Sub

        Private Function TrySetOptionStringLocked(name As String, value As String) As Boolean
            Using namePtr = New Utf8String(name), valuePtr = New Utf8String(value)
                Return MpvInterop.SetOptionString(_handle, namePtr.Pointer, valuePtr.Pointer) >= 0
            End Using
        End Function

        Private Sub SetPropertyStringLocked(name As String, value As String)
            Using namePtr = New Utf8String(name), valuePtr = New Utf8String(value)
                MpvInterop.SetPropertyString(_handle, namePtr.Pointer, valuePtr.Pointer)
            End Using
        End Sub

        Private Sub SetPauseLocked(value As Boolean)
            _isPaused = value
            SetPropertyStringLocked("pause", If(value, "yes", "no"))
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Dim requestRender As Action
            Dim canDisposeNow As Boolean
            SyncLock _syncRoot
                If _disposeRequested Then Return
                _disposeRequested = True
                requestRender = _requestRender
                canDisposeNow = _renderContext = IntPtr.Zero
            End SyncLock

            If requestRender IsNot Nothing Then
                Try
                    requestRender()
                Catch
                End Try
            End If
            If canDisposeNow Then QueueDispose()
        End Sub

        Private Sub QueueDispose()
            SyncLock _commandQueueLock
                If _disposeQueued Then Return
                _disposeQueued = True
                _commandQueue.Add(AddressOf DisposeCore)
                _commandQueue.CompleteAdding()
            End SyncLock
        End Sub

        Private Sub DisposeCore()
            Dim handleToDestroy As IntPtr = IntPtr.Zero
            Dim eventThread As Thread = Nothing

            SyncLock _syncRoot
                If _disposed Then Return
                _disposed = True
                handleToDestroy = _handle
                eventThread = _eventThread
                _initialized = False
            End SyncLock

            If handleToDestroy <> IntPtr.Zero Then
                Try
                    CommandRaw(handleToDestroy, "quit")
                Catch
                End Try
            End If

            If eventThread IsNot Nothing AndAlso
               eventThread.IsAlive AndAlso
               Not Object.ReferenceEquals(Thread.CurrentThread, eventThread) Then
                eventThread.Join(2000)
            End If

            SyncLock _syncRoot
                _handle = IntPtr.Zero
                _eventThread = Nothing
            End SyncLock

            If handleToDestroy <> IntPtr.Zero AndAlso (eventThread Is Nothing OrElse Not eventThread.IsAlive) Then
                MpvInterop.TerminateDestroy(handleToDestroy)
            End If
        End Sub

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
