Imports System
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

        Private _handle As IntPtr = IntPtr.Zero
        Private _eventThread As Thread
        Private _disposed As Boolean = False
        Private _windowHandle As IntPtr = IntPtr.Zero
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
        End Sub

        Public Sub AttachWindow(windowHandle As IntPtr)
            If windowHandle = IntPtr.Zero Then Return

            Try
                SyncLock _syncRoot
                    If _disposed OrElse _initializationFailed Then Return
                    If _windowHandle = windowHandle AndAlso _initialized Then Return
                    _windowHandle = windowHandle
                    If Not _initialized Then
                        InitializeLocked()
                    ElseIf _handle <> IntPtr.Zero Then
                        SetOptionStringLocked("wid", WindowHandleToUnsignedString(_windowHandle))
                    End If
                End SyncLock
            Catch ex As Exception
                HandleInitializationFailure(ex)
            End Try
        End Sub

        Public Sub DetachWindow()
            SyncLock _syncRoot
                _windowHandle = IntPtr.Zero
                If _initializationFailed OrElse Not _initialized OrElse _handle = IntPtr.Zero Then Return
                SetOptionStringLocked("wid", "-1")
            End SyncLock
        End Sub

        Public Sub Load(path As String)
            If String.IsNullOrWhiteSpace(path) Then Return

            SyncLock _syncRoot
                _pendingPath = path
                If _initializationFailed OrElse Not _initialized OrElse _windowHandle = IntPtr.Zero Then Return
                SetPauseLocked(True)
                Dim result = CommandLocked("loadfile", path, "replace")
                If result >= 0 AndAlso _pendingPlay Then SetPauseLocked(False)
            End SyncLock
        End Sub

        Public Sub LoadPending()
            SyncLock _syncRoot
                If String.IsNullOrWhiteSpace(_pendingPath) Then Return
                If _initializationFailed OrElse Not _initialized OrElse _windowHandle = IntPtr.Zero Then Return
                SetPauseLocked(True)
                Dim result = CommandLocked("loadfile", _pendingPath, "replace")
                If result >= 0 AndAlso _pendingPlay Then SetPauseLocked(False)
            End SyncLock
        End Sub

        Public Sub Play()
            SyncLock _syncRoot
                _pendingPlay = True
                If _initialized AndAlso Not _initializationFailed Then SetPauseLocked(False)
            End SyncLock
        End Sub

        Public Sub Pause()
            SyncLock _syncRoot
                _pendingPlay = False
                If _initialized AndAlso Not _initializationFailed Then SetPauseLocked(True)
            End SyncLock
        End Sub

        Public Sub TogglePause()
            SyncLock _syncRoot
                If _initializationFailed Then Return
                If _isPaused Then
                    _pendingPlay = True
                    If _initialized Then SetPauseLocked(False)
                Else
                    _pendingPlay = False
                    If _initialized Then SetPauseLocked(True)
                End If
            End SyncLock
        End Sub

        Public Sub [Stop]()
            SyncLock _syncRoot
                _pendingPlay = False
                If _initialized AndAlso Not _initializationFailed Then CommandLocked("stop")
            End SyncLock
        End Sub

        Public Sub Seek(seconds As Double)
            SyncLock _syncRoot
                If _initializationFailed OrElse Not _initialized Then Return
                SetPropertyStringLocked("time-pos", Math.Max(0, seconds).ToString(CultureInfo.InvariantCulture))
            End SyncLock
        End Sub

        Public Sub SetMuted(value As Boolean)
            SyncLock _syncRoot
                _isMuted = value
                If _initialized AndAlso Not _initializationFailed Then SetPropertyStringLocked("mute", If(value, "yes", "no"))
            End SyncLock
        End Sub

        Private Sub HandleInitializationFailure(ex As Exception)
            Dim handleToDestroy As IntPtr = IntPtr.Zero
            Dim eventThread As Thread = Nothing
            SyncLock _syncRoot
                _initializationFailed = True
                _disposed = True
                _initialized = False
                _windowHandle = IntPtr.Zero
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

        Private Sub InitializeLocked()
            If _initializationFailed OrElse _initialized OrElse _windowHandle = IntPtr.Zero Then Return

            MpvInterop.EnsureResolver()
            _handle = MpvInterop.Create()
            If _handle = IntPtr.Zero Then Throw New InvalidOperationException("libmpv konnte nicht erstellt werden.")

            SetOptionStringLocked("terminal", "no")
            ' Siehe VideoPreviewService: FFmpeg-Demuxer-Hinweise nicht auf die Konsole durchlassen.
            SetOptionStringLocked("msg-level", "all=no")
            SetOptionStringLocked("config", "no")
            SetOptionStringLocked("input-default-bindings", "no")
            SetOptionStringLocked("osc", "no")
            SetOptionStringLocked("keep-open", "no")
            SetOptionStringLocked("hwdec", If(_enableHardwareAcceleration, "auto-safe", "no"))
            If OperatingSystem.IsLinux() Then
                SetOptionStringLocked("vo", "gpu")
                SetOptionStringLocked("gpu-context", "x11egl")
            End If
            SetOptionStringLocked("wid", WindowHandleToUnsignedString(_windowHandle))

            Dim result = MpvInterop.Initialize(_handle)
            If result < 0 Then Throw New InvalidOperationException($"libmpv konnte nicht initialisiert werden ({result}).")

            ObservePropertyLocked(PropTimePos, "time-pos", MpvInterop.MpvFormat.Double)
            ObservePropertyLocked(PropDuration, "duration", MpvInterop.MpvFormat.Double)
            ObservePropertyLocked(PropPause, "pause", MpvInterop.MpvFormat.Flag)
            ObservePropertyLocked(PropMute, "mute", MpvInterop.MpvFormat.Flag)
            SetPropertyStringLocked("mute", If(_isMuted, "yes", "no"))

            ' Klick aufs Video toggelt Wiedergabe/Pause (Nutzerwunsch 2026-07-16): Das Video sitzt in
            ' einem NativeControlHost - Avalonia-Pointer-Events erreichen es NIE, das native
            ' mpv-Fenster schluckt sie. Deshalb bindet mpv selbst die linke Maustaste; alle uebrigen
            ' Default-Bindings bleiben aus (input-default-bindings=no). Der Pause-Status fliesst
            ' ueber die bestehende pause-Observation zurueck in die Oberflaeche (Play-Knopf folgt).
            CommandLocked("keybind", "MBTN_LEFT", "cycle pause")

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

        Private Sub SetPropertyStringLocked(name As String, value As String)
            Using namePtr = New Utf8String(name), valuePtr = New Utf8String(value)
                MpvInterop.SetPropertyString(_handle, namePtr.Pointer, valuePtr.Pointer)
            End Using
        End Sub

        Private Sub SetPauseLocked(value As Boolean)
            _isPaused = value
            SetPropertyStringLocked("pause", If(value, "yes", "no"))
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

        Public Sub Dispose() Implements IDisposable.Dispose
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
