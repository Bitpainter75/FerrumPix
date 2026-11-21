Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Runtime.InteropServices

Namespace Services

    ''' <summary>Was eine Datei bei einem Vorgang gelesen hat.</summary>
    Friend Enum LibraryOutcome
        ''' <summary>Die mitgelieferte Bibliothek hat gelesen.</summary>
        Bundled = 1
        ''' <summary>Die mitgelieferte scheiterte, die des Systems hat gelesen.</summary>
        SystemLibrary = 2
        ''' <summary>Beide sind gescheitert, oder es gab keine des Systems.</summary>
        Failed = 3
    End Enum

    ''' <summary>Wie es um den Rueckfall einer Bibliothek steht, fuer die Seite Technik.</summary>
    Friend Enum SystemFallbackState
        ''' <summary>Auf dieser Plattform nicht vorgesehen: Windows, macOS, Flatpak.</summary>
        NotApplicable
        ''' <summary>Die mitgelieferte fehlt oder lud nicht; es laeuft schon die des Systems oder keine.</summary>
        PrimaryNotBundled
        ''' <summary>Keine Fassung des Systems gefunden.</summary>
        NotFound
        ''' <summary>Gefunden, noch nicht geladen, weil die mitgelieferte bisher alles las.</summary>
        Ready
        ''' <summary>Nachgeladen und benutzbar.</summary>
        Loaded
        ''' <summary>Gefunden, aber das Nachladen scheiterte (zu alt, Export fehlt, Laden misslang).</summary>
        LoadFailed
    End Enum

    ''' <summary>Was ein einzelner Versuch ueber sein Scheitern zu sagen hat. Der Versuch setzt
    ''' <see cref="Failed"/> nur, wenn die Bibliothek die Datei wirklich nicht lesen konnte; "die
    ''' Datei traegt kein Vorschaubild" ist kein Scheitern.</summary>
    Friend NotInheritable Class FallbackAttempt
        ''' <summary>Die Bibliothek konnte die Datei nicht lesen.</summary>
        Public Failed As Boolean
        ''' <summary>Kurze Fehlerart, nach der die Protokollzeilen gedeckelt werden (etwa "2/101").</summary>
        Public Kind As String = ""
        ''' <summary>Der Fehlertext der Bibliothek, so wie er ins Protokoll geht.</summary>
        Public Message As String = ""

        Public Sub Fail(kind As String, message As String)
            Failed = True
            Me.Kind = If(kind, "")
            Me.Message = If(message, "")
        End Sub
    End Class

    ''' <summary>
    ''' Der Rueckfall auf die Fassung des Systems, wenn die mitgelieferte Bibliothek an einer
    ''' bestimmten Datei scheitert. Gemeinsam fuer libheif, libjxl und LibRaw; je Bibliothek eine
    ''' Instanz, die der jeweilige Dienst haelt.
    '''
    ''' WANN. Die mitgelieferte geht ueberall vor (<see cref="BundledLibraryOrder"/>). Scheitert sie an
    ''' einer Datei, wird die des Systems EINMAL je Programmlauf nachgeladen und dieselbe Datei damit
    ''' versucht. Das gibt es nur unter Linux ausserhalb des Flatpak (deb, rpm, AUR, AppImage, ZIP),
    ''' und nur, wenn die laufende Bibliothek wirklich die mitgelieferte ist: lud schon die des
    ''' Systems, gaebe es nichts Zweites. Unter Windows gibt es keine des Systems, der Flatpak hat
    ''' keine Systembibliotheken, und unter macOS liest HEIF zuerst ImageIO.
    '''
    ''' WIE GELADEN WIRD. Die Fassung des Systems kommt mit vollem Pfad aus den Orten, die auch der
    ''' Lader des Systems durchsucht (<see cref="SearchDirectories"/>), und in einen EIGENEN
    ''' Namensraum des Laders (dlmopen mit LM_ID_NEWLM), den sich alle Rueckfaelle teilen. Ein
    ''' schlichtes dlopen reichte nicht: die mitgelieferte libheif traegt denselben Soname
    ''' libheif.so.1, und eine Bibliothek, die die des Systems nachlaedt und libheif.so.1 verlangt
    ''' (unter Debian und Ubuntu liegen die Dekoder als Erweiterungen daneben, etwa
    ''' libheif-libde265.so), bekaeme vom Lader die schon geladene mitgelieferte zugeteilt. Ihre
    ''' Bilder liefen dann durch zwei verschiedene libheif. Im eigenen Namensraum sieht die des Systems
    ''' nur sich und ihre eigenen Abhaengigkeiten, die mitgelieferte bleibt unberuehrt, und umgekehrt.
    ''' Die mitgelieferten verbergen ihre inneren Bibliotheken ohnehin (--exclude-libs,ALL bzw. eine
    ''' Versionsliste beim Binden), und beide werden nur ueber ihren eigenen Handle angesprochen.
    '''
    ''' JE DATEI GEMERKT wird in <see cref="Memory"/>, wer gelesen hat: hat die des Systems gelesen,
    ''' geht der naechste Aufruf gleich dorthin; sind beide gescheitert, wird keine mehr gefragt.
    ''' Der Schluessel ist Pfad, Groesse und Aenderungszeit samt Vorgang, eine veraenderte Datei
    ''' wird also neu versucht.
    '''
    ''' NUR EIN DECODE ZUR ZEIT: <see cref="Run"/> rechnet beide Versuche nacheinander auf dem Faden
    ''' des Aufrufers. Schleuse und Schloss nimmt der Aufrufer wie bisher VOR dem Aufruf.
    '''
    ''' PROTOKOLL. Jeder Rueckfall steht in errors.log, auch ohne eingeschaltetes Diagnoseprotokoll
    ''' (<see cref="DiagnosticLogService.LogNotice"/>): Datei, Fehler der mitgelieferten, Fassung der
    ''' des Systems, Ergebnis. Je Bibliothek und Fehlerart die ersten <see cref="LiteralLogLines"/>
    ''' woertlich, danach nur noch eine Zaehlung bei 10, 100, 1000 und so fort. Ein Ordner mit
    ''' tausend Aufnahmen derselben Kamera schreibt so eine Handvoll Zeilen statt tausend, und die
    ''' Zahl bleibt trotzdem ablesbar.
    ''' </summary>
    Friend NotInheritable Class SystemLibraryFallback

        ''' <summary>So viele Rueckfaelle je Bibliothek und Fehlerart stehen woertlich im Protokoll.</summary>
        Friend Const LiteralLogLines As Integer = 3

        ''' <summary>So viele Eintraege haelt die Merkliste je Bibliothek. Ein Eintrag ist ein kurzer
        ''' Text und eine Zahl; viertausend reichen fuer einen grossen Ordner und bleiben unter einem
        ''' Megabyte.</summary>
        Friend Const DefaultMemoryCapacity As Integer = 4096

        ' Gemeinsamer Teil: Plattform, Suche, Laden in den Namensraum der Rueckfaelle

        Private Delegate Function DlmopenFn(lmid As IntPtr, file As IntPtr, mode As Integer) As IntPtr
        Private Delegate Function DlinfoFn(handle As IntPtr, request As Integer, ByRef info As IntPtr) As Integer
        Private Delegate Function DlerrorFn() As IntPtr

        Private Const RtldNow As Integer = 2
        Private Const RtldDiLmid As Integer = 1

        Private Shared ReadOnly _namespaceLock As New Object()
        Private Shared _dlmopen As DlmopenFn
        Private Shared _dlinfo As DlinfoFn
        Private Shared _dlerror As DlerrorFn
        Private Shared _loaderResolved As Boolean
        ''' <summary>Der Namensraum, den sich alle Rueckfaelle teilen, oder Zero, solange keiner
        ''' angelegt ist. Ein eigener je Bibliothek waere moeglich, aber glibc haelt ab Werk nur
        ''' Platz fuer drei zusaetzliche vor.</summary>
        Private Shared _namespace As IntPtr

        ''' <summary>Gibt es auf dieser Plattform ueberhaupt eine Fassung des Systems als Rueckfall?
        ''' Nur Linux ausserhalb des Flatpak.</summary>
        Friend Shared ReadOnly Property IsPlatformSupported As Boolean
            Get
                Return OperatingSystem.IsLinux() AndAlso Not IsFlatpak
            End Get
        End Property

        ''' <summary>Laeuft die Anwendung im Flatpak? Dort gibt es keine Systembibliotheken, nur die
        ''' der Laufzeit, und die sind schon die geladenen.</summary>
        Friend Shared ReadOnly Property IsFlatpak As Boolean
            Get
                Return Not String.IsNullOrEmpty(Environment.GetEnvironmentVariable("FLATPAK_ID")) OrElse
                       File.Exists("/.flatpak-info")
            End Get
        End Property

        ''' <summary>Die Ordner, in denen eine Fassung des Systems gesucht wird, in der Folge des
        ''' Laders: LD_LIBRARY_PATH, die Eintraege aus /etc/ld.so.conf (dort steht unter Debian und
        ''' Ubuntu /usr/local/lib), dann die festen Orte. Ausgelassen wird alles unter dem
        ''' Verzeichnis der Anwendung und unter APPDIR der AppImage: das sind die mitgelieferten.</summary>
        Friend Shared Function SearchDirectories() As IReadOnlyList(Of String)
            Dim result As New List(Of String)()
            Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
            Dim add = Sub(folder As String)
                          If String.IsNullOrWhiteSpace(folder) OrElse Not Path.IsPathRooted(folder) Then Return
                          Dim full As String
                          Try
                              full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder))
                          Catch
                              Return
                          End Try
                          If IsInsideApplication(full & Path.DirectorySeparatorChar) Then Return
                          If Not Directory.Exists(full) Then Return
                          If seen.Add(full) Then result.Add(full)
                      End Sub

            For Each folder In If(Environment.GetEnvironmentVariable("LD_LIBRARY_PATH"), "").Split(":"c)
                add(folder)
            Next
            For Each folder In ReadLdSoConf("/etc/ld.so.conf", 0)
                add(folder)
            Next
            Dim triplet = If(RuntimeInformation.ProcessArchitecture = Architecture.Arm64, "aarch64-linux-gnu", "x86_64-linux-gnu")
            For Each folder In {$"/usr/local/lib/{triplet}", "/usr/local/lib64", "/usr/local/lib",
                             $"/lib/{triplet}", $"/usr/lib/{triplet}", "/lib64", "/usr/lib64", "/lib", "/usr/lib"}
                add(folder)
            Next
            Return result
        End Function

        ''' <summary>Die Ordner aus einer ld.so.conf samt ihrer include-Zeilen, in Dateireihenfolge.</summary>
        Private Shared Iterator Function ReadLdSoConf(file As String, depth As Integer) As IEnumerable(Of String)
            If depth > 4 Then Return
            Dim lines As String()
            Try
                If Not IO.File.Exists(file) Then Return
                lines = IO.File.ReadAllLines(file)
            Catch
                Return
            End Try
            For Each raw In lines
                Dim line = raw
                Dim hash = line.IndexOf("#"c)
                If hash >= 0 Then line = line.Substring(0, hash)
                line = line.Trim()
                If line.Length = 0 Then Continue For
                If line.StartsWith("include ", StringComparison.Ordinal) Then
                    Dim pattern = line.Substring(8).Trim()
                    If Not Path.IsPathRooted(pattern) Then pattern = Path.Combine(Path.GetDirectoryName(file), pattern)
                    Dim folder = Path.GetDirectoryName(pattern)
                    Dim mask = Path.GetFileName(pattern)
                    Dim included As String() = Array.Empty(Of String)()
                    Try
                        If Directory.Exists(folder) Then included = Directory.GetFiles(folder, mask)
                    Catch
                    End Try
                    Array.Sort(included, StringComparer.Ordinal)
                    For Each child In included
                        For Each entry In ReadLdSoConf(child, depth + 1)
                            Yield entry
                        Next
                    Next
                Else
                    Yield line
                End If
            Next
        End Function

        ''' <summary>Liegt der Pfad unter dem Verzeichnis der Anwendung oder der AppImage?</summary>
        Private Shared Function IsInsideApplication(fullPath As String) As Boolean
            If BundledLibraryOrder.IsBundledPath(fullPath, AppContext.BaseDirectory) Then Return True
            Dim appDir = Environment.GetEnvironmentVariable("APPDIR")
            Return Not String.IsNullOrEmpty(appDir) AndAlso Path.IsPathRooted(appDir) AndAlso
                   BundledLibraryOrder.IsBundledPath(fullPath, appDir)
        End Function

        ''' <summary>Die erste Datei aus <paramref name="fileNames"/> in <see cref="SearchDirectories"/>,
        ''' als voller Pfad, oder Nothing. Je Name alle Ordner, dann der naechste Name: der Soname
        ''' geht dem nackten Namen des Entwicklerpakets vor, wo immer er liegt. Nur Dateien, die zur
        ''' laufenden Architektur passen (unter Arch liegt /usr/lib32 im Suchpfad).</summary>
        Friend Shared Function FindFile(fileNames As IEnumerable(Of String)) As String
            Dim dirs = SearchDirectories()
            For Each name In fileNames
                For Each folder In dirs
                    Dim candidate = Path.Combine(folder, name)
                    Try
                        If File.Exists(candidate) AndAlso IsMatchingElf(candidate) Then Return candidate
                    Catch
                    End Try
                Next
            Next
            Return Nothing
        End Function

        ''' <summary>Passt die ELF-Datei zum laufenden Prozess (64 Bit, gleiche Maschine)?</summary>
        Private Shared Function IsMatchingElf(file As String) As Boolean
            Dim head(19) As Byte
            Using stream = IO.File.OpenRead(file)
                If stream.Read(head, 0, head.Length) < head.Length Then Return False
            End Using
            If head(0) <> &H7F OrElse head(1) <> AscW("E"c) OrElse head(2) <> AscW("L"c) OrElse head(3) <> AscW("F"c) Then Return False
            If head(4) <> 2 OrElse head(5) <> 1 Then Return False   ' 64 Bit, kleines Ende zuerst
            Dim machine = CInt(head(18)) Or (CInt(head(19)) << 8)
            Dim expected = If(RuntimeInformation.ProcessArchitecture = Architecture.Arm64, 183, 62)
            Return machine = expected
        End Function

        ''' <summary>Die Datei hinter allen symbolischen Verweisen, oder der Pfad selbst.</summary>
        Friend Shared Function ResolveFinalPath(file As String) As String
            If String.IsNullOrEmpty(file) Then Return file
            Try
                Dim target = New FileInfo(file).ResolveLinkTarget(returnFinalTarget:=True)
                If target IsNot Nothing Then Return target.FullName
                Return Path.GetFullPath(file)
            Catch
                Return file
            End Try
        End Function

        ''' <summary>Die Fassung aus dem Dateinamen hinter den Verweisen, ohne die Datei zu laden:
        ''' /usr/lib/libheif.so.1 zeigt auf libheif.so.1.23.7, daraus wird "1.23.7". Leer, wenn der
        ''' Name keine Fassung traegt.</summary>
        Friend Shared Function VersionFromFileName(file As String) As String
            Dim name = Path.GetFileName(ResolveFinalPath(file))
            Dim marker = name.IndexOf(".so.", StringComparison.Ordinal)
            If marker < 0 Then Return ""
            Return name.Substring(marker + 4)
        End Function

        ''' <summary>Laedt eine Bibliothek mit vollem Pfad in den Namensraum der Rueckfaelle. Der
        ''' erste Aufruf legt ihn an, jeder weitere laedt in denselben. Bei Misserfolg steht der Grund
        ''' in <paramref name="failure"/>, etwa die Meldung des Laders.</summary>
        Friend Shared Function LoadIsolated(fullPath As String, ByRef handle As IntPtr, ByRef failure As String) As Boolean
            handle = IntPtr.Zero
            failure = ""
            If Not OperatingSystem.IsLinux() Then
                failure = "nur unter Linux"
                Return False
            End If
            SyncLock _namespaceLock
                If Not ResolveLoader() Then
                    failure = "dlmopen fehlt in dieser C-Bibliothek"
                    Return False
                End If
                Dim pathPtr = Marshal.StringToCoTaskMemUTF8(fullPath)
                Try
                    ' LM_ID_NEWLM ist -1: ein neuer Namensraum. Danach immer derselbe.
                    Dim lmid = If(_namespace = IntPtr.Zero, New IntPtr(-1), _namespace)
                    handle = _dlmopen(lmid, pathPtr, RtldNow)
                    If handle = IntPtr.Zero Then
                        Dim message = _dlerror()
                        failure = If(message = IntPtr.Zero, "dlmopen scheiterte", Marshal.PtrToStringUTF8(message))
                        Return False
                    End If
                    If _namespace = IntPtr.Zero Then
                        Dim created As IntPtr
                        If _dlinfo IsNot Nothing AndAlso _dlinfo(handle, RtldDiLmid, created) = 0 Then _namespace = created
                    End If
                    Return True
                Finally
                    Marshal.FreeCoTaskMem(pathPtr)
                End Try
            End SyncLock
        End Function

        ''' <summary>dlmopen und seine Begleiter aus der C-Bibliothek. Seit glibc 2.34 stehen sie in
        ''' libc.so.6, davor in libdl.so.2.</summary>
        Private Shared Function ResolveLoader() As Boolean
            If _loaderResolved Then Return _dlmopen IsNot Nothing
            _loaderResolved = True
            For Each name In {"libc.so.6", "libdl.so.2"}
                Dim library As IntPtr
                If Not NativeLibrary.TryLoad(name, library) Then Continue For
                Dim open = TryGetExport(Of DlmopenFn)(library, "dlmopen")
                Dim info = TryGetExport(Of DlinfoFn)(library, "dlinfo")
                Dim errorFn = TryGetExport(Of DlerrorFn)(library, "dlerror")
                If open IsNot Nothing AndAlso errorFn IsNot Nothing Then
                    _dlmopen = open
                    _dlinfo = info
                    _dlerror = errorFn
                    Return True
                End If
            Next
            Return False
        End Function

        ''' <summary>Ein Export als Delegat, oder Nothing, wenn es ihn nicht gibt.</summary>
        Friend Shared Function TryGetExport(Of T As Class)(handle As IntPtr, name As String) As T
            Dim address As IntPtr
            If handle = IntPtr.Zero OrElse Not NativeLibrary.TryGetExport(handle, name, address) Then Return Nothing
            Return Marshal.GetDelegateForFunctionPointer(Of T)(address)
        End Function

        ' Je Bibliothek

        Private ReadOnly _lock As New Object()
        Private ReadOnly _fileNames As String()
        Private _searched As Boolean
        Private _candidatePath As String
        Private _loadTried As Boolean
        Private _handle As IntPtr
        Private _loadedPath As String
        Private _version As String = ""
        Private _loadFailure As String = ""
        Private _loadAttempts As Integer
        Private _systemReads As Integer
        Private ReadOnly _logCounts As New Dictionary(Of String, (Count As Integer, Read As Integer))(StringComparer.Ordinal)

        ''' <param name="libraryName">Der Name fuer Protokoll und Anzeige, etwa "libheif".</param>
        ''' <param name="fileNames">Die Dateinamen der Fassung des Systems, der bevorzugte zuerst.</param>
        Friend Sub New(libraryName As String, fileNames As IEnumerable(Of String),
                       Optional memoryCapacity As Integer = DefaultMemoryCapacity)
            Me.LibraryName = libraryName
            _fileNames = New List(Of String)(fileNames).ToArray()
            Memory = New FileOutcomeMemory(memoryCapacity)
        End Sub

        Friend ReadOnly Property LibraryName As String

        ''' <summary>Je Datei, wer gelesen hat.</summary>
        Friend ReadOnly Property Memory As FileOutcomeMemory

        ''' <summary>Der volle Pfad der laufenden, also der zuerst geladenen Bibliothek. Setzt der
        ''' Dienst nach seinem eigenen Laden; leer, wenn keine lud.</summary>
        Friend Property PrimaryPath As String = ""

        ''' <summary>Die Fassung der laufenden Bibliothek, fuer das Protokoll.</summary>
        Friend Property PrimaryVersion As String = ""

        ''' <summary>Nur fuer den Pruefstand: an genau dieser Datei scheitert die mitgelieferte, als
        ''' haette sie sie nicht lesen koennen. Ein Pfad statt eines Schalters, damit gleichzeitig
        ''' laufende Pruefungen derselben Bibliothek davon nichts merken.</summary>
        Friend Property SimulatedBundledFailurePath As String

        ''' <summary>Wie oft die mitgelieferte wegen <see cref="SimulatedBundledFailurePath"/>
        ''' scheiterte. Daran sieht der Pruefstand, ob ein zweiter Aufruf sie erneut fragt.</summary>
        Friend ReadOnly Property SimulatedFailures As Integer
            Get
                Return Threading.Volatile.Read(_simulatedFailures)
            End Get
        End Property
        Private _simulatedFailures As Integer

        ''' <summary>Ist der Rueckfall fuer diese Bibliothek vorgesehen? Linux ausserhalb des
        ''' Flatpak, und die laufende ist die mitgelieferte.</summary>
        Friend ReadOnly Property IsOffered As Boolean
            Get
                Return IsPlatformSupported AndAlso Not String.IsNullOrEmpty(PrimaryPath) AndAlso
                       IsInsideApplication(PrimaryPath)
            End Get
        End Property

        ''' <summary>Die gefundene Fassung des Systems als voller Pfad, ohne sie zu laden, oder
        ''' Nothing. Einmal gesucht und gemerkt.</summary>
        Friend ReadOnly Property CandidatePath As String
            Get
                SyncLock _lock
                    If Not _searched Then
                        _searched = True
                        If IsPlatformSupported Then
                            Dim found = FindFile(_fileNames)
                            ' Dieselbe Datei wie die laufende waere kein Rueckfall.
                            If found IsNot Nothing AndAlso Not String.IsNullOrEmpty(PrimaryPath) AndAlso
                               String.Equals(ResolveFinalPath(found), ResolveFinalPath(PrimaryPath), StringComparison.Ordinal) Then
                                found = Nothing
                            End If
                            _candidatePath = found
                        End If
                    End If
                    Return _candidatePath
                End SyncLock
            End Get
        End Property

        Friend ReadOnly Property IsLoaded As Boolean
            Get
                SyncLock _lock
                    Return _handle <> IntPtr.Zero
                End SyncLock
            End Get
        End Property

        ''' <summary>Der Handle der nachgeladenen Fassung des Systems, oder Zero.</summary>
        Friend ReadOnly Property Handle As IntPtr
            Get
                SyncLock _lock
                    Return _handle
                End SyncLock
            End Get
        End Property

        Friend ReadOnly Property LoadedPath As String
            Get
                SyncLock _lock
                    Return If(_loadedPath, "")
                End SyncLock
            End Get
        End Property

        ''' <summary>Die Fassung: nach dem Laden die der Bibliothek selbst, davor die aus dem Dateinamen.</summary>
        Friend ReadOnly Property Version As String
            Get
                SyncLock _lock
                    If _version.Length > 0 Then Return _version
                End SyncLock
                Dim candidate = CandidatePath
                Return If(candidate Is Nothing, "", VersionFromFileName(candidate))
            End Get
        End Property

        ''' <summary>Warum das Nachladen scheiterte, oder leer.</summary>
        Friend ReadOnly Property LoadFailure As String
            Get
                SyncLock _lock
                    Return _loadFailure
                End SyncLock
            End Get
        End Property

        ''' <summary>Wie oft ein Nachladen versucht wurde (0 oder 1). Der Pruefstand sieht daran,
        ''' dass ohne Scheitern nie nachgeladen wird.</summary>
        Friend ReadOnly Property LoadAttempts As Integer
            Get
                SyncLock _lock
                    Return _loadAttempts
                End SyncLock
            End Get
        End Property

        ''' <summary>Wie viele Dateien die Fassung des Systems in diesem Lauf gelesen hat.</summary>
        Friend ReadOnly Property SystemReads As Integer
            Get
                Return Threading.Volatile.Read(_systemReads)
            End Get
        End Property

        ''' <summary>Laedt die Fassung des Systems, einmal je Programmlauf. <paramref name="bind"/>
        ''' bekommt Handle und vollen Pfad, holt seine Einstiege und gibt die Fassung als Text zurueck,
        ''' oder Nothing, wenn die Bibliothek nicht taugt (zu alt, Pflichtexport fehlt); den Grund
        ''' schreibt es in den zweiten Parameter. Dann bleibt der Rueckfall fuer diesen Lauf aus.
        ''' Weitere Dateien derselben Bibliothek (etwa libjxl_threads) laedt bind selbst ueber
        ''' <see cref="LoadIsolated"/>, sie landen im selben Namensraum.</summary>
        Friend Function EnsureLoaded(bind As Func(Of IntPtr, String, Text.StringBuilder, String)) As Boolean
            SyncLock _lock
                If _loadTried Then Return _handle <> IntPtr.Zero
            End SyncLock
            Dim candidate = CandidatePath
            SyncLock _lock
                If _loadTried Then Return _handle <> IntPtr.Zero
                _loadTried = True
                If Not IsOffered Then
                    _loadFailure = "nicht vorgesehen"
                    Return False
                End If
                If candidate Is Nothing Then
                    _loadFailure = "keine Fassung des Systems gefunden"
                    Return False
                End If
                _loadAttempts += 1
                Dim handle As IntPtr
                Dim failure As String = ""
                If Not LoadIsolated(candidate, handle, failure) Then
                    _loadFailure = failure
                    DiagnosticLogService.LogNotice(LibraryName, $"{LibraryName} des Systems ({candidate}) laesst sich nicht laden: {failure}")
                    Return False
                End If
                Dim reason As New Text.StringBuilder()
                Dim version As String = Nothing
                Try
                    version = bind(handle, candidate, reason)
                Catch ex As Exception
                    reason.Append(ex.Message)
                End Try
                If version Is Nothing Then
                    _loadFailure = If(reason.Length > 0, reason.ToString(), "nicht benutzbar")
                    DiagnosticLogService.LogNotice(LibraryName, $"{LibraryName} des Systems ({candidate}) wird nicht benutzt: {_loadFailure}")
                    ' Nicht freigeben: was bind schon gebunden hat, koennte noch zeigen; ein Handle
                    ' im eigenen Namensraum kostet nichts.
                    Return False
                End If
                _handle = handle
                _loadedPath = ResolveFinalPath(candidate)
                _version = version
                DiagnosticLogService.LogAlways(LibraryName, $"{LibraryName} des Systems als Rueckfall geladen: {_version} ({_loadedPath})")
                Return True
            End SyncLock
        End Function

        ''' <summary>
        ''' Ein Vorgang an einer Datei mit Rueckfall. Erst die mitgelieferte
        ''' (<paramref name="tryBundled"/>); setzt sie <see cref="FallbackAttempt.Failed"/>, wird die
        ''' Fassung des Systems geladen (<paramref name="bind"/>, einmal je Lauf) und
        ''' <paramref name="trySystem"/> gerufen. Die Merkliste kuerzt ab: hat bei dieser Datei und
        ''' diesem Vorgang schon die des Systems gelesen, geht es gleich dorthin; sind beide
        ''' gescheitert, kommt sofort der leere Wert.
        '''
        ''' Ohne Rueckfall (andere Plattform, laufende ist nicht die mitgelieferte, Datei gibt es
        ''' nicht) ist das genau ein Aufruf von <paramref name="tryBundled"/>.
        '''
        ''' Liefert der gescheiterte Versuch trotzdem ein Objekt und liest die des Systems, wird es
        ''' freigegeben, wenn es IDisposable ist.
        ''' </summary>
        ''' <param name="operation">Kurzer Name des Vorgangs fuer Merkliste und Protokoll, etwa "Bild".</param>
        Friend Function Run(Of T)(filePath As String, operation As String,
                                  tryBundled As Func(Of FallbackAttempt, T),
                                  bind As Func(Of IntPtr, String, Text.StringBuilder, String),
                                  trySystem As Func(Of FallbackAttempt, T)) As T
            If Not IsOffered OrElse String.IsNullOrEmpty(filePath) Then Return tryBundled(New FallbackAttempt())
            Dim key = FileOutcomeMemory.MakeKey(filePath, operation)
            If key Is Nothing Then Return tryBundled(New FallbackAttempt())

            Dim remembered As LibraryOutcome
            If Memory.TryGet(key, remembered) Then
                If remembered = LibraryOutcome.Failed Then Return Nothing
                If remembered = LibraryOutcome.SystemLibrary AndAlso EnsureLoaded(bind) Then
                    Dim again As New FallbackAttempt()
                    Dim value = SafeRun(trySystem, again)
                    If again.Failed Then
                        Memory.Remember(key, LibraryOutcome.Failed)
                        DisposeIfNeeded(value)
                        Return Nothing
                    End If
                    Return value
                End If
            End If

            Dim first As New FallbackAttempt()
            Dim result As T
            Dim simulated = SimulatedBundledFailurePath
            If simulated IsNot Nothing AndAlso String.Equals(simulated, filePath, StringComparison.Ordinal) Then
                Threading.Interlocked.Increment(_simulatedFailures)
                first.Fail("simuliert", "fuer den Pruefstand gezielt gescheitert")
                result = Nothing
            Else
                result = SafeRun(tryBundled, first)
            End If
            If Not first.Failed Then
                Memory.Remember(key, LibraryOutcome.Bundled)
                Return result
            End If

            If Not EnsureLoaded(bind) Then
                Memory.Remember(key, LibraryOutcome.Failed)
                Report(filePath, operation, first, Nothing)
                Return result
            End If

            Dim second As New FallbackAttempt()
            Dim fallback = SafeRun(trySystem, second)
            Report(filePath, operation, first, second)
            If second.Failed Then
                Memory.Remember(key, LibraryOutcome.Failed)
                DisposeIfNeeded(fallback)
                Return result
            End If
            Threading.Interlocked.Increment(_systemReads)
            Memory.Remember(key, LibraryOutcome.SystemLibrary)
            DisposeIfNeeded(result)
            Return fallback
        End Function

        Private Shared Function SafeRun(Of T)(work As Func(Of FallbackAttempt, T), attempt As FallbackAttempt) As T
            Try
                Return work(attempt)
            Catch ex As Exception
                attempt.Fail(ex.GetType().Name, ex.Message)
                Return Nothing
            End Try
        End Function

        Private Shared Sub DisposeIfNeeded(Of T)(value As T)
            Dim disposable = TryCast(value, IDisposable)
            disposable?.Dispose()
        End Sub

        ''' <summary>Eine Zeile ins Protokoll, gedeckelt je Fehlerart (siehe Klassenkommentar).
        ''' <paramref name="second"/> ist Nothing, wenn es keine Fassung des Systems gab.</summary>
        Private Sub Report(filePath As String, operation As String, first As FallbackAttempt, second As FallbackAttempt)
            Dim read = second IsNot Nothing AndAlso Not second.Failed
            Dim kind = If(first.Kind.Length > 0, first.Kind, first.Message)
            Dim count As Integer
            Dim readCount As Integer
            SyncLock _logCounts
                Dim entry As (Count As Integer, Read As Integer) = Nothing
                _logCounts.TryGetValue(kind, entry)
                entry = (entry.Count + 1, entry.Read + If(read, 1, 0))
                _logCounts(kind) = entry
                count = entry.Count
                readCount = entry.Read
            End SyncLock

            Dim bundled = $"mitgelieferte {LibraryName} {PrimaryVersion}".TrimEnd()
            If count <= LiteralLogLines Then
                Dim outcome As String
                If second Is Nothing Then
                    outcome = $"keine Fassung des Systems ({LoadFailure})"
                ElseIf read Then
                    outcome = $"{LibraryName} des Systems {Version} ({LoadedPath}) hat gelesen"
                Else
                    outcome = $"{LibraryName} des Systems {Version} ({LoadedPath}) scheiterte ebenfalls: {second.Message}"
                End If
                DiagnosticLogService.LogNotice(LibraryName,
                    $"Rueckfall bei {filePath} ({operation}): {bundled} scheiterte: {first.Message}; {outcome}")
                If count = LiteralLogLines Then
                    DiagnosticLogService.LogNotice(LibraryName,
                        $"weitere Rueckfaelle mit diesem Fehler ({kind}) werden nur noch gezaehlt")
                End If
            ElseIf IsPowerOfTen(count) Then
                DiagnosticLogService.LogNotice(LibraryName,
                    $"{count} Rueckfaelle mit dem Fehler {kind} der {bundled} in diesem Lauf, davon {readCount} von der Fassung des Systems gelesen")
            End If
        End Sub

        Private Shared Function IsPowerOfTen(value As Integer) As Boolean
            If value < 10 Then Return False
            While value Mod 10 = 0
                value \= 10
            End While
            Return value = 1
        End Function

        ''' <summary>Der Stand fuer die Seite Technik.</summary>
        Friend ReadOnly Property State As SystemFallbackState
            Get
                If Not IsPlatformSupported Then Return SystemFallbackState.NotApplicable
                If Not IsOffered Then Return SystemFallbackState.PrimaryNotBundled
                If IsLoaded Then Return SystemFallbackState.Loaded
                If CandidatePath Is Nothing Then Return SystemFallbackState.NotFound
                SyncLock _lock
                    If _loadTried Then Return SystemFallbackState.LoadFailed
                End SyncLock
                Return SystemFallbackState.Ready
            End Get
        End Property

    End Class

    ''' <summary>
    ''' Je Datei und Vorgang, welche Bibliothek gelesen hat. Der Schluessel ist voller Pfad, Groesse
    ''' und Aenderungszeit samt Vorgang: eine geaenderte Datei hat einen neuen Schluessel und wird
    ''' neu versucht. Begrenzt auf eine feste Zahl; ist sie erreicht, faellt der aelteste Eintrag
    ''' heraus. Threadsicher.
    ''' </summary>
    Friend NotInheritable Class FileOutcomeMemory
        Private ReadOnly _lock As New Object()
        Private ReadOnly _entries As New Dictionary(Of String, LibraryOutcome)(StringComparer.Ordinal)
        Private ReadOnly _order As New Queue(Of String)()

        Friend Sub New(capacity As Integer)
            Me.Capacity = Math.Max(1, capacity)
        End Sub

        Friend ReadOnly Property Capacity As Integer

        Friend ReadOnly Property Count As Integer
            Get
                SyncLock _lock
                    Return _entries.Count
                End SyncLock
            End Get
        End Property

        ''' <summary>Der Schluessel fuer eine Datei, oder Nothing, wenn es sie nicht gibt.</summary>
        Friend Shared Function MakeKey(filePath As String, operation As String) As String
            Try
                Dim info = New FileInfo(filePath)
                If Not info.Exists Then Return Nothing
                Return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{operation}"
            Catch
                Return Nothing
            End Try
        End Function

        Friend Function TryGet(key As String, ByRef outcome As LibraryOutcome) As Boolean
            If key Is Nothing Then Return False
            SyncLock _lock
                Return _entries.TryGetValue(key, outcome)
            End SyncLock
        End Function

        ''' <summary>Fuer Pruefstand und Aufrufer: was fuer diese Datei und diesen Vorgang gilt.</summary>
        Friend Function Lookup(filePath As String, operation As String) As LibraryOutcome?
            Dim outcome As LibraryOutcome
            If TryGet(MakeKey(filePath, operation), outcome) Then Return outcome
            Return Nothing
        End Function

        Friend Sub Remember(key As String, outcome As LibraryOutcome)
            If key Is Nothing Then Return
            SyncLock _lock
                If Not _entries.ContainsKey(key) Then
                    _order.Enqueue(key)
                    While _order.Count > Capacity
                        _entries.Remove(_order.Dequeue())
                    End While
                End If
                _entries(key) = outcome
            End SyncLock
        End Sub

        Friend Sub Clear()
            SyncLock _lock
                _entries.Clear()
                _order.Clear()
            End SyncLock
        End Sub
    End Class

End Namespace
