Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices

Namespace Services

    ''' <summary>Laedt unter Windows die mitgelieferte Visual C++ Laufzeit, bevor ONNX Runtime
    ''' geladen wird.
    '''
    ''' DER ANLASS: ONNX Runtime ab 1.30 und ihr WebGPU-Plugin sind mit MSVC 14.44 gebaut und binden
    ''' die C++-Laufzeit dynamisch ein (MSVCP140.dll und Verwandte). Auf einem Windows, das nur eine
    ''' aeltere Fassung im Systemordner hat, stuerzt das Laden in std::mutex ab: Visual Studio 2022
    ''' 17.10 (14.40) hat dessen Aufbau geaendert. Der Absturz ist eine Zugriffsverletzung und laesst
    ''' sich in .NET nicht fangen.
    '''
    ''' DER WEG: Die Windows-Pakete legen die passende Laufzeit neben die Anwendung. Windows saehe sie
    ''' dort auch von selbst zuerst; hier wird sie trotzdem ueber den vollen Pfad geladen, damit
    ''' feststeht, welche Datei im Prozess ist, bevor ONNX Runtime nach ihr fragt. Hat schon etwas
    ''' anderes eine Laufzeit gleichen Namens geladen (ein Treiber, eine Shell-Erweiterung), bleibt
    ''' die im Prozess, denn Windows bindet jeden Import ueber den Namen an das bereits geladene
    ''' Modul. Ist diese aelter als 14.44, wird ONNX Runtime gar nicht erst geladen: die KI-Funktionen
    ''' fehlen dann, die Anwendung laeuft weiter.
    '''
    ''' Gerufen wird das an EINER Stelle, in <see cref="AiModelService"/> vor dem ersten Zugriff auf
    ''' die Laufzeit; jeder Weg zu einem Modell und zur Grafikbeschleunigung geht dort durch. Unter
    ''' Linux und macOS tut es nichts. Fehlen die Dateien neben der Anwendung (ein Entwicklerbau),
    ''' geht es ohne Vorladen weiter wie bisher.</summary>
    Public NotInheritable Class VcRuntimePreloader

        Private Sub New()
        End Sub

        ''' <summary>Die aelteste Laufzeit, mit der die mitgelieferte ONNX Runtime laedt: die Fassung
        ''' ihres Linkers.</summary>
        Public Shared ReadOnly MinimumVersion As New Version(14, 44)

        Private Shared ReadOnly _lock As New Object()
        Private Shared _done As Boolean
        Private Shared _usable As Boolean = True
        Private Shared _outcome As String = ""

        ''' <summary>Wurde der Vorlader in diesem Prozess schon gerufen? Fuer den Pruefstand: er
        ''' haelt fest, dass jeder Weg zur Laufzeit hier vorbeikommt.</summary>
        Public Shared ReadOnly Property WasCalled As Boolean
            Get
                SyncLock _lock
                    Return _done
                End SyncLock
            End Get
        End Property

        ''' <summary>Was der Vorlader getan oder gefunden hat, als kurzer Text fuer das Protokoll;
        ''' vor dem ersten Aufruf leer.</summary>
        Public Shared ReadOnly Property Outcome As String
            Get
                SyncLock _lock
                    Return _outcome
                End SyncLock
            End Get
        End Property

        ''' <summary>Die Dateien der Laufzeit in der Reihenfolge, in der sie geladen werden: jede
        ''' nach denen, die sie selbst importiert. VCRUNTIME140_1.dll gibt es nur fuer x64; die Datei
        ''' gleichen Namens fuer ARM64 ist ARM64EC und wird von keiner ARM64-Bibliothek verlangt.
        ''' Muss zu VC_RUNTIME_WIN und VC_RUNTIME_WIN_X64 in packaging/package.sh passen.</summary>
        Public Shared Function RuntimeFileNames(architecture As Architecture) As String()
            If architecture = Architecture.X64 Then
                Return New String() {"vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_atomic_wait.dll"}
            End If
            Return New String() {"vcruntime140.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_atomic_wait.dll"}
        End Function

        ''' <summary>Die erste bereits geladene Laufzeit, die zu alt ist, als Beschreibung; leer,
        ''' wenn keine zu alt ist. Ohne Systemaufrufe, damit der Pruefstand die Regel auch unter
        ''' Linux messen kann.</summary>
        ''' <param name="loaded">Dateiname, Pfad und Dateiversion jedes schon geladenen Moduls.</param>
        Public Shared Function FindTooOld(loaded As IEnumerable(Of (Name As String, Path As String, Version As Version))) As String
            For Each item In loaded
                If item.Version Is Nothing Then Continue For
                Dim majorMinor = New Version(item.Version.Major, item.Version.Minor)
                If majorMinor < MinimumVersion Then
                    Return $"{item.Name} {item.Version} ({item.Path})"
                End If
            Next
            Return ""
        End Function

        ''' <summary>Einmal je Prozess: die Laufzeit vorladen und sagen, ob ONNX Runtime geladen
        ''' werden darf. False nur, wenn schon eine zu alte Laufzeit im Prozess ist; der Grund steht
        ''' dann in <see cref="Outcome"/> und im Protokoll. Threadsicher.</summary>
        Public Shared Function EnsureLoaded() As Boolean
            SyncLock _lock
                If _done Then Return _usable
                _done = True
                If Not OperatingSystem.IsWindows() Then
                    _outcome = "not needed on this platform"
                    Return True
                End If
                Try
                    _usable = PreloadOnWindows(_outcome)
                Catch ex As Exception
                    ' Ein Fehler beim Vorladen selbst ist kein Grund, die Laufzeit zu sperren: dann
                    ' sucht Windows wie bisher.
                    _usable = True
                    _outcome = "preload failed: " & ex.Message
                End Try
                DiagnosticLogService.LogAlways("KiModell", "C++-Laufzeit: " & _outcome)
                Return _usable
            End SyncLock
        End Function

        Private Shared Function PreloadOnWindows(ByRef outcome As String) As Boolean
            Dim folder = AppContext.BaseDirectory
            Dim names = RuntimeFileNames(RuntimeInformation.ProcessArchitecture)

            ' Erst nachsehen, was schon im Prozess ist. Laedt man daneben eine zweite Datei gleichen
            ' Namens ueber den vollen Pfad, stuenden zwei Laufzeiten im Prozess, und ONNX Runtime
            ' bekaeme trotzdem die erste.
            Dim alreadyLoaded As New List(Of (Name As String, Path As String, Version As Version))()
            For Each name In names
                Dim existing = LoadedModulePath(name)
                If existing.Length > 0 Then alreadyLoaded.Add((name, existing, FileVersionOf(existing)))
            Next
            Dim tooOld = FindTooOld(alreadyLoaded)
            If tooOld.Length > 0 Then
                outcome = $"older runtime already in the process, model runtime not loaded: {tooOld}, needs {MinimumVersion}"
                Return False
            End If

            Dim loadedNow As New List(Of String)()
            Dim missing As New List(Of String)()
            For Each name In names
                If alreadyLoaded.Exists(Function(i) String.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)) Then Continue For
                Dim candidate = Path.Combine(folder, name)
                If Not File.Exists(candidate) Then
                    missing.Add(name)
                    Continue For
                End If
                Dim handle As IntPtr
                If NativeLibrary.TryLoad(candidate, handle) Then
                    loadedNow.Add(name)
                Else
                    missing.Add(name)
                End If
            Next

            Dim parts As New List(Of String)()
            If loadedNow.Count > 0 Then parts.Add("loaded from the program folder: " & String.Join(", ", loadedNow))
            If alreadyLoaded.Count > 0 Then
                parts.Add("already in the process: " & String.Join(", ", alreadyLoaded.ConvertAll(Function(i) $"{i.Name} {i.Version}")))
            End If
            If missing.Count > 0 Then parts.Add("not in the program folder, left to Windows: " & String.Join(", ", missing))
            outcome = String.Join("; ", parts)
            Return True
        End Function

        ''' <summary>Der Pfad des Moduls dieses Namens, wenn es schon im Prozess ist; sonst leer.</summary>
        Private Shared Function LoadedModulePath(name As String) As String
            Dim handle = GetModuleHandleW(name)
            If handle = IntPtr.Zero Then Return ""
            Dim buffer = New Char(1023) {}
            Dim length = GetModuleFileNameW(handle, buffer, buffer.Length)
            If length <= 0 Then Return name
            Return New String(buffer, 0, length)
        End Function

        Private Shared Function FileVersionOf(path As String) As Version
            Try
                Dim info = FileVersionInfo.GetVersionInfo(path)
                Return New Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart)
            Catch
                ' Unlesbar: dann laesst sich nichts belegen, und es bleibt beim Laden wie bisher.
                Return Nothing
            End Try
        End Function

        <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True)>
        Private Shared Function GetModuleHandleW(moduleName As String) As IntPtr
        End Function

        <DllImport("kernel32.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True)>
        Private Shared Function GetModuleFileNameW(moduleHandle As IntPtr, <Out> buffer As Char(), size As Integer) As Integer
        End Function

    End Class

End Namespace
