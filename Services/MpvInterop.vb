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
            If TryLoadPreferSystem(GetType(MpvInterop).Assembly, Nothing, handle) Then
                NativeLibrary.Free(handle)
                Return True
            End If
            Return False
        End Function

        Private Shared Function ResolveLibrary(libraryName As String, assembly As Assembly, searchPath As DllImportSearchPath?) As IntPtr
            If Not String.Equals(libraryName, "libmpv", StringComparison.Ordinal) Then Return IntPtr.Zero

            Dim handle As IntPtr
            If TryLoadPreferSystem(assembly, searchPath, handle) Then Return handle
            Return IntPtr.Zero
        End Function

        ''' <summary>Erst die Bibliothek des Systems, dann die mitgelieferte.
        '''
        ''' Die Reihenfolge ist Absicht: eine vom Paketverwalter gepflegte libmpv bekommt
        ''' Sicherheitsaktualisierungen und passt zu den Codecs, Treibern und Ausgabepfaden des
        ''' Systems. Die mitgelieferte Fassung ist der Rückfall für Umgebungen, die keine haben -
        ''' Windows und die portablen Pakete.</summary>
        Private Shared Function TryLoadPreferSystem(assembly As Assembly, searchPath As DllImportSearchPath?, ByRef handle As IntPtr) As Boolean
            For Each candidate In LibraryNames()
                If NativeLibrary.TryLoad(candidate, assembly, searchPath, handle) Then Return True
            Next

            ' Eine aus dem Finder gestartete .app erbt weder die Shell-Umgebung noch einen
            ' Homebrew-Pfad. Apple Silicon installiert Homebrew unter /opt/homebrew, Intel-Macs
            ' ueblicherweise unter /usr/local; beide liegen ausserhalb der Standard-Suchwege des
            ' .NET-Loaders. Die expliziten Pfade gehoeren noch zur Systembibliothek und haben
            ' deshalb Vorrang vor einer allenfalls mitgelieferten Runtime.
            For Each path In HomebrewLibraryCandidates()
                If NativeLibrary.TryLoad(path, handle) Then Return True
            Next

            Return TryLoadBundledLibrary(handle)
        End Function

        Private Shared Function LibraryNames() As String()
            If OperatingSystem.IsWindows() Then
                Return {"mpv-2.dll", "libmpv-2.dll", "mpv-1.dll", "libmpv.dll", "mpv.dll"}
            ElseIf OperatingSystem.IsMacOS() Then
                Return {"libmpv.2.dylib", "libmpv.dylib"}
            End If
            Return {"libmpv.so.2", "libmpv.so"}
        End Function

        Private Shared Iterator Function HomebrewLibraryCandidates() As IEnumerable(Of String)
            If Not OperatingSystem.IsMacOS() Then Return

            For Each prefix In {"/opt/homebrew/lib", "/usr/local/lib"}
                For Each name In LibraryNames()
                    Yield Path.Combine(prefix, name)
                Next
            Next
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
            Dim rid = GetCurrentRuntimeIdentifier()

            For Each name In LibraryNames()
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
        Friend Shared Function ObserveProperty(handle As IntPtr, replyUserData As ULong, name As IntPtr, fileFormat As MpvFormat) As Integer
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_command", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function Command(handle As IntPtr, args As IntPtr) As Integer
        End Function

        ''' <summary>Reiht einen Befehl bei mpv ein, ohne auf dessen Core-Thread zu warten.
        ''' Für Stop und Load ist das die richtige Form: der Aufrufer will nur, dass der Befehl
        ''' ankommt, und braucht das Ergebnis nicht.</summary>
        <DllImport("libmpv", EntryPoint:="mpv_command_async", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function CommandAsync(handle As IntPtr, replyUserData As ULong, args As IntPtr) As Integer
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_wait_event", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function WaitEvent(handle As IntPtr, timeout As Double) As IntPtr
        End Function

        ' ── Render-API ───────────────────────────────────────────────────────────
        '
        ' Der zweite Ausgabeweg von libmpv: statt in ein Fenster zu zeichnen, gibt mpv das
        ' fertige Bild an den Aufrufer. Das ist der EINZIGE Weg, ein Video unter macOS in die
        ' eigene Oberfläche zu bekommen - die Option "wid" kennt dort kein Ziel (die Anleitung
        ' nennt bei --wid nur X11, win32 und Android; im macOS-Teil von mpv wird sie nirgends
        ' ausgewertet), mpv macht dort immer ein eigenes Fenster auf.

        ''' <summary>Der Software-Renderer: mpv schreibt das Bild in einen Speicherbereich.</summary>
        Friend Const RenderApiTypeSoftware As String = "sw"

        ''' <summary>Vier Bytes je Bildpunkt in der Reihenfolge Blau, Grün, Rot, ungenutzt.
        ''' Deckt sich mit <c>PixelFormat.Bgra8888</c>; das ungenutzte Byte enthält Müll, deshalb
        ''' muss die Zielbitmap als undurchsichtig angelegt werden.</summary>
        Friend Const RenderFormatBgr0 As String = "bgr0"

        Friend Const RenderParamApiType As Integer = 1
        Friend Const RenderParamSoftwareSize As Integer = 17
        Friend Const RenderParamSoftwareFormat As Integer = 18
        Friend Const RenderParamSoftwareStride As Integer = 19
        Friend Const RenderParamSoftwarePointer As Integer = 20

        ''' <summary>Es liegt ein neues Einzelbild bereit.</summary>
        Friend Const RenderUpdateFrame As ULong = 1UL

        ''' <summary>Größe eines <c>mpv_render_param</c>: eine Kennzahl und ein Zeiger, beide auf
        ''' Zeigerbreite ausgerichtet.</summary>
        Friend Shared ReadOnly RenderParamSize As Integer = IntPtr.Size * 2

        ''' <summary>Schreibt einen Parameter an seinen Platz im Parameterblock.</summary>
        Friend Shared Sub WriteRenderParam(block As IntPtr, index As Integer, paramType As Integer, data As IntPtr)
            Dim offset = index * RenderParamSize
            Marshal.WriteInt32(block, offset, paramType)
            Marshal.WriteIntPtr(block, offset + IntPtr.Size, data)
        End Sub

        ''' <summary>Wird von mpv aus einem beliebigen Faden gerufen, sobald etwas zu zeichnen
        ''' ist. Darf selbst KEINE libmpv-Funktion rufen, nur wecken.</summary>
        <UnmanagedFunctionPointer(CallingConvention.Cdecl)>
        Friend Delegate Sub RenderUpdateCallback(callbackContext As IntPtr)

        <DllImport("libmpv", EntryPoint:="mpv_render_context_create", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function RenderContextCreate(ByRef context As IntPtr, handle As IntPtr, parameters As IntPtr) As Integer
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_render_context_set_update_callback", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Sub RenderContextSetUpdateCallback(context As IntPtr, callback As RenderUpdateCallback, callbackContext As IntPtr)
        End Sub

        <DllImport("libmpv", EntryPoint:="mpv_render_context_update", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function RenderContextUpdate(context As IntPtr) As ULong
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_render_context_render", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Function RenderContextRender(context As IntPtr, parameters As IntPtr) As Integer
        End Function

        <DllImport("libmpv", EntryPoint:="mpv_render_context_free", CallingConvention:=CallingConvention.Cdecl)>
        Friend Shared Sub RenderContextFree(context As IntPtr)
        End Sub
    End Class

End Namespace
