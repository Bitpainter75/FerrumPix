Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Runtime.InteropServices

Namespace Services

    Friend NotInheritable Class MpvInterop
        Private Sub New()
        End Sub

        Private Shared _resolverInstalled As Boolean = False
        Private Shared ReadOnly _bundledLoadLock As New Object()
        Private Shared ReadOnly _bundledDependencyHandles As New List(Of IntPtr)()
        Private Shared ReadOnly _preloadedDirectories As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

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
            If TryLoadBundledFirst(GetType(MpvInterop).Assembly, Nothing, handle) Then
                NativeLibrary.Free(handle)
                Return True
            End If
            Return False
        End Function

        Private Shared Function ResolveLibrary(libraryName As String, assembly As Assembly, searchPath As DllImportSearchPath?) As IntPtr
            If Not String.Equals(libraryName, "libmpv", StringComparison.Ordinal) Then Return IntPtr.Zero

            Dim handle As IntPtr
            If TryLoadBundledFirst(assembly, searchPath, handle) Then Return handle
            Return IntPtr.Zero
        End Function

        ''' <summary>Die mit FerrumPix veröffentlichte Bibliothek ist der primäre Laufzeitpfad.
        ''' Eine Systeminstallation bleibt nur als Rückfall für ältere/selbst gebaute Pakete.</summary>
        Private Shared Function TryLoadBundledFirst(assembly As Assembly, searchPath As DllImportSearchPath?, ByRef handle As IntPtr) As Boolean
            If TryLoadBundledLibrary(handle) Then Return True
            For Each candidate In LibraryNames()
                If NativeLibrary.TryLoad(candidate, assembly, searchPath, handle) Then Return True
            Next
            handle = IntPtr.Zero
            Return False
        End Function

        Private Shared Function LibraryNames() As String()
            If OperatingSystem.IsWindows() Then
                Return {"mpv-2.dll", "libmpv-2.dll", "mpv-1.dll", "libmpv.dll", "mpv.dll"}
            ElseIf OperatingSystem.IsMacOS() Then
                Return {"libmpv.2.dylib", "libmpv.dylib"}
            End If
            Return {"libmpv.so.2", "libmpv.so"}
        End Function

        Private Shared Function TryLoadBundledLibrary(ByRef handle As IntPtr) As Boolean
            SyncLock _bundledLoadLock
                For Each path In BundledLibraryCandidates()
                    If Not File.Exists(path) Then Continue For
                    PreloadBundledDependencies(IO.Path.GetDirectoryName(path), IO.Path.GetFileName(path))
                    If NativeLibrary.TryLoad(path, handle) Then Return True
                Next
                handle = IntPtr.Zero
                Return False
            End SyncLock
        End Function

        ''' <summary>media-kits portable macOS libraries refer to one another via @rpath. A .NET
        ''' application does not link against them at build time, so dyld has no matching rpath.
        ''' Loading the siblings by absolute path first makes their install names available before
        ''' libmpv itself is opened. Repeated passes resolve the dependency graph without baking a
        ''' fragile, hand-maintained load order into FerrumPix.</summary>
        Private Shared Sub PreloadBundledDependencies(directoryPath As String, mainLibraryName As String)
            If Not OperatingSystem.IsMacOS() OrElse String.IsNullOrWhiteSpace(directoryPath) Then Return
            If _preloadedDirectories.Contains(directoryPath) Then Return

            Dim pending As List(Of String)
            Try
                pending = Directory.EnumerateFiles(directoryPath, "*.dylib", SearchOption.TopDirectoryOnly).
                    Where(Function(path) Not String.Equals(IO.Path.GetFileName(path), mainLibraryName, StringComparison.OrdinalIgnoreCase)).
                    ToList()
            Catch
                Return
            End Try

            Dim madeProgress As Boolean
            Do
                madeProgress = False
                For index = pending.Count - 1 To 0 Step -1
                    Dim dependencyHandle As IntPtr
                    If NativeLibrary.TryLoad(pending(index), dependencyHandle) Then
                        _bundledDependencyHandles.Add(dependencyHandle)
                        pending.RemoveAt(index)
                        madeProgress = True
                    End If
                Next
            Loop While madeProgress AndAlso pending.Count > 0

            _preloadedDirectories.Add(directoryPath)
        End Sub

        Private Shared Iterator Function BundledLibraryCandidates() As IEnumerable(Of String)
            Dim baseDir = AppContext.BaseDirectory
            Dim rid = GetCurrentRuntimeIdentifier()

            For Each name In LibraryNames()
                Yield Path.Combine(baseDir, "libmpv", name)
                Yield Path.Combine(baseDir, name)
                If Not String.IsNullOrEmpty(rid) Then Yield Path.Combine(baseDir, "runtimes", rid, "native", name)
            Next
        End Function

        Private Shared Function GetCurrentRuntimeIdentifier() As String
            Dim archSuffix As String
            Select Case RuntimeInformation.ProcessArchitecture
                Case Architecture.Arm64
                    archSuffix = "arm64"
                Case Else
                    archSuffix = "x64"
            End Select

            If OperatingSystem.IsWindows() Then Return $"win-{archSuffix}"
            If OperatingSystem.IsLinux() Then Return $"linux-{archSuffix}"
            If OperatingSystem.IsMacOS() Then
                Return $"osx-{archSuffix}"
            End If
            Return ""
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

        Friend Enum MpvRenderParamType As Integer
            Invalid = 0
            ApiType = 1
            OpenGlInitParams = 2
            OpenGlFbo = 3
            FlipY = 4
        End Enum

        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Friend Delegate Function MpvOpenGlGetProcAddress(context As IntPtr, name As IntPtr) As IntPtr

        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Friend Delegate Sub MpvRenderUpdateCallback(context As IntPtr)

        <StructLayout(LayoutKind.Sequential)>
        Friend Structure MpvRenderParam
            Public Type As MpvRenderParamType
            Public Data As IntPtr
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Friend Structure MpvOpenGlInitParams
            Public GetProcAddress As MpvOpenGlGetProcAddress
            Public GetProcAddressContext As IntPtr
        End Structure

        <StructLayout(LayoutKind.Sequential)>
        Friend Structure MpvOpenGlFbo
            Public Fbo As Integer
            Public Width As Integer
            Public Height As Integer
            Public InternalFormat As Integer
        End Structure

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

        <DllImport("libmpv", EntryPoint:="mpv_render_context_create", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function RenderContextCreate(ByRef context As IntPtr,
                                                   handle As IntPtr,
                                                   <[In]> parameters As MpvRenderParam()) As Integer
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_render_context_set_update_callback", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Sub RenderContextSetUpdateCallback(context As IntPtr,
                                                         callback As MpvRenderUpdateCallback,
                                                         callbackContext As IntPtr)
        End Sub

        <DllImport("libmpv", EntryPoint:="mpv_render_context_update", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function RenderContextUpdate(context As IntPtr) As ULong
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_render_context_render", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function RenderContextRender(context As IntPtr,
                                                   <[In]> parameters As MpvRenderParam()) As Integer
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_render_context_report_swap", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Sub RenderContextReportSwap(context As IntPtr)
        End Sub

        <DllImport("libmpv", EntryPoint:="mpv_render_context_free", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Sub RenderContextFree(context As IntPtr)
        End Sub
    End Class

End Namespace
