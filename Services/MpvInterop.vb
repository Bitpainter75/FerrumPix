Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Reflection
Imports System.Runtime.InteropServices

Namespace Services

    Friend NotInheritable Class MpvInterop
        Private Sub New()
        End Sub

        Private Shared _resolverInstalled As Boolean = False

        Shared Sub New()
            EnsureResolver()
        End Sub

        Public Shared Sub EnsureResolver()
            If _resolverInstalled Then Return
            NativeLibrary.SetDllImportResolver(GetType(MpvInterop).Assembly, AddressOf ResolveLibrary)
            _resolverInstalled = True
        End Sub

        Public Shared Function IsAvailable() As Boolean
            EnsureResolver()
            Dim handle As IntPtr
            If TryLoadBundledLibrary(handle) OrElse NativeLibrary.TryLoad("libmpv", GetType(MpvInterop).Assembly, Nothing, handle) Then
                NativeLibrary.Free(handle)
                Return True
            End If
            Return False
        End Function

        Private Shared Function ResolveLibrary(libraryName As String, assembly As Assembly, searchPath As DllImportSearchPath?) As IntPtr
            If Not String.Equals(libraryName, "libmpv", StringComparison.Ordinal) Then Return IntPtr.Zero

            Dim candidates As String()
            If OperatingSystem.IsWindows() Then
                candidates = {"mpv-2.dll", "libmpv-2.dll", "mpv-1.dll", "libmpv.dll", "mpv.dll"}
            ElseIf OperatingSystem.IsMacOS() Then
                candidates = {"libmpv.2.dylib", "libmpv.dylib"}
            Else
                candidates = {"libmpv.so.2", "libmpv.so"}
            End If

            Dim handle As IntPtr
            If TryLoadBundledLibrary(handle) Then Return handle

            For Each candidate In candidates
                If NativeLibrary.TryLoad(candidate, assembly, searchPath, handle) Then Return handle
            Next

            Return IntPtr.Zero
        End Function

        Private Shared Function TryLoadBundledLibrary(ByRef handle As IntPtr) As Boolean
            For Each path In BundledLibraryCandidates()
                If NativeLibrary.TryLoad(path, handle) Then Return True
            Next
            handle = IntPtr.Zero
            Return False
        End Function

        Private Shared Iterator Function BundledLibraryCandidates() As IEnumerable(Of String)
            Dim baseDir = AppContext.BaseDirectory
            Dim rid = If(OperatingSystem.IsWindows(), "win-x64", If(OperatingSystem.IsLinux(), "linux-x64", If(OperatingSystem.IsMacOS(), "osx-x64", "")))

            Dim names As String()
            If OperatingSystem.IsWindows() Then
                names = {"mpv-2.dll", "libmpv-2.dll", "mpv-1.dll", "libmpv.dll", "mpv.dll"}
            ElseIf OperatingSystem.IsMacOS() Then
                names = {"libmpv.2.dylib", "libmpv.dylib"}
            Else
                names = {"libmpv.so.2", "libmpv.so"}
            End If

            For Each name In names
                Yield Path.Combine(baseDir, name)
                If Not String.IsNullOrEmpty(rid) Then Yield Path.Combine(baseDir, "runtimes", rid, "native", name)
            Next
        End Function

        Friend Enum MpvFormat As Integer
            None = 0
            [String] = 1
            OsdString = 2
            Flag = 3
            Int64 = 4
            [Double] = 5
        End Enum

        Friend Enum MpvEventId As Integer
            None = 0
            Shutdown = 1
            LogMessage = 2
            GetPropertyReply = 3
            SetPropertyReply = 4
            CommandReply = 5
            StartFile = 6
            EndFile = 7
            FileLoaded = 8
            Idle = 11
            Seek = 20
            PlaybackRestart = 21
            PropertyChange = 22
            QueueOverflow = 24
        End Enum

        Friend Enum MpvEndFileReason As Integer
            Eof = 0
            [Stop] = 2
            Quit = 3
            [Error] = 4
            Redirect = 5
        End Enum

        <StructLayout(LayoutKind.Sequential)>
        Friend Structure MpvEvent
            Public EventId As MpvEventId
            Public [Error] As Integer
            Public ReplyUserData As ULong
            Public Data As IntPtr
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Friend Structure MpvEventProperty
            Public Name As IntPtr
            Public Format As MpvFormat
            Public Data As IntPtr
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Friend Structure MpvEventEndFile
            Public Reason As MpvEndFileReason
            Public [Error] As Integer
            Public PlaylistEntryId As Long
            Public PlaylistInsertId As Long
            Public PlaylistInsertNumEntries As Integer
        End Structure

        <DllImport("libmpv", EntryPoint:="mpv_create", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function Create() As IntPtr
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_initialize", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function Initialize(handle As IntPtr) As Integer
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_terminate_destroy", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Sub TerminateDestroy(handle As IntPtr)
        End Sub

        <DllImport("libmpv", EntryPoint:="mpv_set_option_string", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function SetOptionString(handle As IntPtr, name As IntPtr, value As IntPtr) As Integer
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_set_property_string", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function SetPropertyString(handle As IntPtr, name As IntPtr, value As IntPtr) As Integer
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_observe_property", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function ObserveProperty(handle As IntPtr, replyUserData As ULong, name As IntPtr, format As MpvFormat) As Integer
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_command", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function Command(handle As IntPtr, args As IntPtr) As Integer
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_wait_event", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function WaitEvent(handle As IntPtr, timeout As Double) As IntPtr
        End Function
    End Class

End Namespace
