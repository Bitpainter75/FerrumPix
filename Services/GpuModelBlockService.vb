Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Runtime.CompilerServices
Imports System.Text.Json
Imports Microsoft.ML.OnnxRuntime

Namespace Services

    ''' <summary>Welche Modelle auf welcher Karte NICHT mehr laufen sollen, ueber einen Neustart
    ''' hinaus.
    '''
    ''' DER ANLASS: ein Grafiktreiber, der waehrend Sitzungsaufbau oder Rechnung eines Modells den
    ''' Prozess beendet (Restormer mit "double free or corruption"). Das laesst sich nicht fangen.
    ''' Ohne Spur startete die Anwendung danach normal, und beim naechsten Gebrauch lief dasselbe
    ''' Modell wieder auf derselben Karte, und der Prozess starb wieder.
    '''
    ''' DIE LAUFMARKE: vor Sitzungsaufbau und jedem Lauf eines Modells AUF DER KARTE wird im Ordner
    ''' des Startschutzes (<see cref="NativeStartGuard.GuardDirectory"/>) eine Marke
    ''' <c>modell-&lt;datei&gt;-&lt;karte&gt;.&lt;pid&gt;.marke</c> angelegt und danach geloescht. Sie
    ''' traegt Programmfassung, Prozessnummer, Modelldatei, Kartenschluessel, Kartenname,
    ''' Ausfuehrungsweg und Treiberfassung. Bleibt sie liegen und lebt ihr Prozess nicht mehr, ist er
    ''' genau dort gestorben. Wie beim Startschutz hat jeder Prozess seine eigenen Marken; laufen in
    ''' einem Prozess zwei Laeufe desselben Paares gleichzeitig, steht die Marke bis zum letzten.
    '''
    ''' DIE SPERRDATEI <c>karte-gesperrt.json</c> liegt neben den Einstellungen (wie die Marke der
    ''' Bildkette, <c>gpu-busy.marker</c>). Beim ersten Blick in die Sperren eines Programmlaufs
    ''' werden liegengebliebene Laufmarken dorthin uebernommen (Grund "crash"), und auch ein
    ''' gefangener Fehler im Aufbau landet dort (Grund "error", ueber
    ''' <see cref="AiModelService.NoteGpuFailedModel"/>). Ein gesperrtes Paar rechnet auf dem
    ''' Prozessor; einen zweiten Weg auf die Karte gibt es nicht.
    '''
    ''' VERFALL: eine Sperre gilt fuer den Bau der Anwendung (<see cref="NativeStartGuard.CurrentStamp"/>)
    ''' und die Treiberfassung, unter denen sie entstand. Ein neuer Bau verwirft sie beim Laden, ein
    ''' neuer Treiber beim naechsten Versuch, das Modell auf dieser Karte zu bauen (erst dann wird
    ''' Vulkan gefragt; die Anzeige in den Einstellungen laedt keinen Treiber). Ist die Treiberfassung
    ''' unbekannt, verfaellt die Sperre nur mit dem Bau. Von Hand hebt sie der Knopf in den
    ''' Einstellungen auf. Ein bestaetigter Aufbau auf der Karte (Probe, Modellaufbau) hebt nur eine
    ''' Sperre aus einem Fehler auf; eine aus einem Absturz nicht, denn der kann erst im Lauf kommen.
    '''
    ''' ZWEI PROZESSE teilen sich die Datei. Jede Aenderung liest sie unter einem benannten Mutex
    ''' frisch, traegt nur das eigene Paar ein oder aus und schreibt atomar zurueck (Mutate); der
    ''' Zwischenspeicher wird neu gelesen, sobald Schreibzeit oder Laenge der Datei abweichen. So
    ''' ueberschreibt kein Prozess eine Sperre, die der andere inzwischen eingetragen hat.
    '''
    ''' EINE KAPUTTE DATEI ist keine Sperre: sie wird ignoriert und beim naechsten Schreiben ersetzt.</summary>
    Public NotInheritable Class GpuModelBlockService

        Private Sub New()
        End Sub

        ''' <summary>Der Prozess starb waehrend Sitzungsaufbau oder Lauf (liegengebliebene Marke).</summary>
        Public Const ReasonCrash As String = "crash"
        ''' <summary>Der Aufbau auf der Karte warf eine gefangene Ausnahme.</summary>
        Public Const ReasonError As String = "error"

        Private Const MarkerPrefix As String = "modell-"
        Private Const BlockFileName As String = "karte-gesperrt.json"

        ''' <summary>Eine Sperre: dieses Modell auf dieser Karte.</summary>
        Public NotInheritable Class BlockEntry
            Public Property Model As String = ""
            Public Property Device As String = ""
            Public Property DeviceLabel As String = ""
            Public Property ExecutionPath As String = ""
            Public Property Driver As String = ""
            Public Property Build As String = ""
            Public Property Reason As String = ""
            Public Property BlockedAtUtc As DateTime

            Friend Function Copy() As BlockEntry
                Return DirectCast(MemberwiseClone(), BlockEntry)
            End Function

            Friend Function Matches(fileName As String, deviceKey As String) As Boolean
                Return String.Equals(Model, fileName, StringComparison.OrdinalIgnoreCase) AndAlso
                       String.Equals(Device, deviceKey, StringComparison.Ordinal)
            End Function
        End Class

        ''' <summary>Der Aufbau der Datei. Oeffentlich, damit der JSON-Leser ihn ohne Umweg baut.</summary>
        Public NotInheritable Class BlockFile
            Public Property Entries As List(Of BlockEntry) = New List(Of BlockEntry)()
        End Class

        ''' <summary>Fuer den Pruefstand: eine andere Sperrdatei.</summary>
        Friend Shared FileOverride As String = Nothing
        ''' <summary>Fuer den Pruefstand: die Treiberfassung je Kartenschluessel, ohne Vulkan.</summary>
        Friend Shared DriverOverride As Func(Of String, String) = Nothing

        Private Shared ReadOnly _lock As New Object()
        ''' Der zuletzt gelesene oder geschriebene Stand, nur die Sperren dieses Baus. Nothing heisst:
        ''' noch nicht gelesen. Gilt nur, solange die Datei Zeit und Laenge von _fileStamp traegt.
        Private Shared _entries As List(Of BlockEntry) = Nothing
        ''' Schreibzeit und Laenge der Datei, als _entries entstand; ein anderer Prozess, der seither
        ''' geschrieben hat, aendert sie, und der naechste Blick liest neu.
        Private Shared _fileStamp As (Time As DateTime, Length As Long)
        ''' Ob in diesem Programmlauf die liegengebliebenen Laufmarken schon uebernommen sind.
        Private Shared _adopted As Boolean
        ''' Wie lange ein Schreiber auf einen anderen Prozess wartet, der gerade dieselbe Datei
        ''' schreibt. Danach wird trotzdem geschrieben: eine Sperre, die verloren geht, ist
        ''' schlimmer als ein Wettlauf, der sie vielleicht verliert. Ein Schreiben der kleinen
        ''' JSON-Datei dauert Millisekunden; 30 Sekunden Warten erreicht nur ein Prozess, der mit der
        ''' Sperre haengt. Erst dann bleibt der Restfall, dass sein spaeteres Schreiben einen in der
        ''' Zwischenzeit geschriebenen Eintrag ueberdeckt.
        Private Const CrossProcessLockTimeoutMs As Integer = 30000
        ''' Die offenen Laeufe dieses Prozesses je Paar, fuer die eine Marke liegt.
        Private Shared ReadOnly _running As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
        ''' Zu jeder Sitzung auf der Karte: Modelldatei und Kartenschluessel.
        Private Shared ReadOnly _sessions As New ConditionalWeakTable(Of InferenceSession, Tuple(Of String, String))()

        ''' <summary>Eine Sperre kam dazu oder ging weg. Kann aus jedem Faden kommen.</summary>
        Public Shared Event Changed As EventHandler

        Private Shared ReadOnly Property BlockFilePath As String
            Get
                If Not String.IsNullOrEmpty(FileOverride) Then Return FileOverride
                Return Path.Combine(AppSettingsService.Folder, BlockFileName)
            End Get
        End Property

        ''' <summary>Der Ausfuehrungsweg auf die Karte, fuer Marke und Sperre. Es gibt nur einen:
        ''' den WebGPU-Anbieter der Laufzeit, darunter das Grafiksystem der Plattform.</summary>
        Public Shared ReadOnly Property ExecutionPathName As String
            Get
                Return "WebGPU (" & GpuAccelerationService.BackendName & ")"
            End Get
        End Property

        ''' <summary>Die Treiberfassung der Karte als Text, oder leer. Unter macOS steht der Treiber
        ''' im System, dort zaehlt dessen Fassung.</summary>
        Friend Shared Function CurrentDriver(deviceKey As String) As String
            Dim override = DriverOverride
            If override IsNot Nothing Then Return If(override(deviceKey), "")
            If String.IsNullOrEmpty(deviceKey) Then Return ""
            Try
                If OperatingSystem.IsMacOS() Then Return "macos " & Environment.OSVersion.Version.ToString()
                Return VulkanMemoryService.DriverVersionFor(deviceKey)
            Catch
                Return ""
            End Try
        End Function

        ' ---------------------------------------------------------------------------------------
        ' Laufmarken

        ''' <summary>Der Name der Marke eines Paares, ohne Prozessnummer: nur Buchstaben, Ziffern und
        ''' Bindestrich, weil Kartenschluessel Doppelpunkte tragen.</summary>
        Private Shared Function MarkerBaseName(fileName As String, deviceKey As String) As String
            Return MarkerPrefix & Sanitize(fileName) & "-" & Sanitize(deviceKey)
        End Function

        Private Shared Function Sanitize(text As String) As String
            Dim chars = If(text, "").Select(Function(c) If(Char.IsAsciiLetterOrDigit(c) OrElse c = "-"c, c, "_"c)).ToArray()
            Return New String(chars)
        End Function

        ''' <summary>Vor Sitzungsaufbau oder Lauf auf der Karte. Zurueck kommt der Bereich, dessen
        ''' Ende die Marke wegnimmt; Nothing bei leerem Kartenschluessel (Prozessor). Kann die Marke
        ''' nicht geschrieben werden, wird trotzdem gerechnet; der Schutz fehlt dann, die Funktion nicht.</summary>
        Public Shared Function EnterRun(fileName As String, deviceKey As String) As IDisposable
            If String.IsNullOrEmpty(fileName) OrElse String.IsNullOrEmpty(deviceKey) Then Return Nothing
            Dim baseName = MarkerBaseName(fileName, deviceKey)
            Dim first As Boolean
            SyncLock _running
                Dim count = 0
                _running.TryGetValue(baseName, count)
                _running(baseName) = count + 1
                first = (count = 0)
            End SyncLock
            If first Then
                Try
                    ' Treiber und Kartenname VOR dem Lauf: nach dem Absturz und einem Treiberwechsel
                    ' soll die Sperre den ALTEN Treiber tragen und damit gleich verfallen.
                    Dim lines = {NativeStartGuard.CurrentStamp(),
                                 Environment.ProcessId.ToString(Globalization.CultureInfo.InvariantCulture),
                                 fileName, deviceKey, GpuAccelerationService.DeviceLabelFor(deviceKey),
                                 ExecutionPathName, CurrentDriver(deviceKey)}
                    Directory.CreateDirectory(NativeStartGuard.GuardDirectory)
                    File.WriteAllLines(OwnMarkerPath(baseName), lines)
                Catch ex As Exception
                    DiagnosticLogService.LogAlways("Startschutz", $"{baseName}: Laufmarke nicht nutzbar - {ex.Message}")
                End Try
            End If
            Return New RunScope(baseName)
        End Function

        Private Shared Function OwnMarkerPath(baseName As String) As String
            Return Path.Combine(NativeStartGuard.GuardDirectory,
                                baseName & "." & Environment.ProcessId.ToString(Globalization.CultureInfo.InvariantCulture) & ".marke")
        End Function

        Private Shared Sub LeaveRun(baseName As String)
            Dim last As Boolean
            SyncLock _running
                Dim count = 0
                _running.TryGetValue(baseName, count)
                If count <= 1 Then
                    _running.Remove(baseName)
                    last = True
                Else
                    _running(baseName) = count - 1
                End If
            End SyncLock
            If Not last Then Return
            Try
                File.Delete(OwnMarkerPath(baseName))
            Catch
            End Try
        End Sub

        Private NotInheritable Class RunScope
            Implements IDisposable
            Private ReadOnly _baseName As String
            Private _done As Integer

            Public Sub New(baseName As String)
                _baseName = baseName
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                If Threading.Interlocked.Exchange(_done, 1) = 1 Then Return
                LeaveRun(_baseName)
            End Sub
        End Class

        ''' <summary>Eine Sitzung, die auf der Karte gebaut wurde, mit ihrem Paar vermerken, damit
        ''' jeder Lauf darauf seine Marke bekommt (<see cref="GuardRun"/>).</summary>
        Friend Shared Sub RegisterSession(session As InferenceSession, fileName As String, deviceKey As String)
            If session Is Nothing OrElse String.IsNullOrEmpty(deviceKey) Then Return
            _sessions.AddOrUpdate(session, Tuple.Create(fileName, deviceKey))
        End Sub

        ''' <summary>Um jeden Lauf einer Sitzung: auf der Karte die Laufmarke, auf dem Prozessor
        ''' nichts (Nothing; ein Using darf Nothing tragen).</summary>
        Public Shared Function GuardRun(session As InferenceSession) As IDisposable
            If session Is Nothing Then Return Nothing
            Dim pair As Tuple(Of String, String) = Nothing
            If Not _sessions.TryGetValue(session, pair) Then Return Nothing
            Return EnterRun(pair.Item1, pair.Item2)
        End Function

        ' ---------------------------------------------------------------------------------------
        ' Sperrdatei

        ''' <summary>Den Zwischenspeicher bereitstellen. Einmal je Programmlauf werden dabei
        ''' liegengebliebene Marken uebernommen und Sperren eines anderen Baus verworfen; danach wird
        ''' nur neu gelesen, wenn ein anderer Prozess die Datei geaendert hat (Zeit oder Laenge).
        ''' Unter _lock zu rufen.</summary>
        Private Shared Sub EnsureLoaded()
            If Not _adopted Then
                _adopted = True
                Mutate(Function(list)
                           Dim changed = AdoptLeftoverMarkers(list)
                           Dim stamp = NativeStartGuard.CurrentStamp()
                           Dim before = list.Count
                           list.RemoveAll(Function(e) Not String.Equals(e.Build, stamp, StringComparison.Ordinal))
                           If list.Count <> before Then
                               changed = True
                               DiagnosticLogService.LogAlways("Startschutz",
                                   $"{before - list.Count} Modellsperre(n) einer anderen Programmfassung verworfen, die Karte wird wieder versucht")
                           End If
                           Return changed
                       End Function)
                Return
            End If
            If _entries IsNot Nothing AndAlso CurrentFileStamp().Equals(_fileStamp) Then Return
            ' Die Marke VOR dem Lesen: schreibt jemand dazwischen, weicht sie beim naechsten Blick
            ' ab, und es wird noch einmal gelesen.
            _fileStamp = CurrentFileStamp()
            _entries = OwnBuildOnly(ReadFile())
        End Sub

        ''' <summary>Eine Aenderung an der Sperrdatei, die neben einem zweiten FerrumPix bestehen
        ''' muss. Unter einer Sperre ueber Prozesse hinweg wird die Datei FRISCH gelesen,
        ''' <paramref name="change"/> fuegt darin einzelne Eintraege hinzu oder nimmt sie weg, und
        ''' nur bei einer Aenderung wird atomar zurueckgeschrieben. Frueher schrieb jeder Prozess
        ''' seinen einmal gelesenen Stand zurueck, und die Sperre, die ein anderer inzwischen
        ''' eingetragen hatte, war wieder weg. Liefert, ob <paramref name="change"/> etwas geaendert
        ''' hat. Unter _lock zu rufen.</summary>
        Private Shared Function Mutate(change As Func(Of List(Of BlockEntry), Boolean)) As Boolean
            Using crossLock = LockAcrossProcesses()
                Dim list = ReadFile()
                Dim changed = change(list)
                If changed Then
                    Dim json = JsonSerializer.Serialize(New BlockFile With {.Entries = list},
                                                        New JsonSerializerOptions With {.WriteIndented = True})
                    JsonStoreService.WriteAtomic(BlockFilePath, json, "GpuModelBlock")
                End If
                _fileStamp = CurrentFileStamp()
                _entries = OwnBuildOnly(list)
                Return changed
            End Using
        End Function

        ''' <summary>Nur die Sperren dieses Baus gelten; die eines anderen, gleichzeitig laufenden
        ''' Baus bleiben in der Datei, sperren hier aber nichts.</summary>
        Private Shared Function OwnBuildOnly(list As List(Of BlockEntry)) As List(Of BlockEntry)
            Dim stamp = NativeStartGuard.CurrentStamp()
            Return list.Where(Function(e) String.Equals(e.Build, stamp, StringComparison.Ordinal)).ToList()
        End Function

        Private Shared Function CurrentFileStamp() As (Time As DateTime, Length As Long)
            Try
                Dim info As New FileInfo(BlockFilePath)
                If Not info.Exists Then Return (DateTime.MinValue, -1)
                Return (info.LastWriteTimeUtc, info.Length)
            Catch
                Return (DateTime.MinValue, -2)
            End Try
        End Function

        ''' <summary>Die Sperre ueber Prozesse hinweg: ein benannter Mutex je Sperrdatei, wie bei den
        ''' Beistelldateien (RawSidecarService.LockAcrossProcesses). Ist er nicht zu haben oder
        ''' laeuft die Wartezeit ab, wird ohne ihn weitergemacht; das Protokoll haelt es fest.</summary>
        Private Shared Function LockAcrossProcesses() As IDisposable
            Dim key As String
            Try
                key = Path.GetFullPath(BlockFilePath)
            Catch
                key = BlockFilePath
            End Try
            If PathIdentity.IgnoresCase Then key = key.ToUpperInvariant()
            Dim name = "FerrumPix.GpuModelBlock." &
                       Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key))).Substring(0, 40)
            Dim mutex As Threading.Mutex
            Try
                mutex = New Threading.Mutex(False, name)
            Catch ex As Exception
                DiagnosticLogService.LogException("GpuModelBlock.CrossProcessLock", ex)
                Return Nothing
            End Try
            Try
                If Not mutex.WaitOne(CrossProcessLockTimeoutMs) Then
                    mutex.Dispose()
                    DiagnosticLogService.LogAlways("GpuModelBlock.CrossProcessLock", "belegt, ohne Sperre geschrieben")
                    Return Nothing
                End If
            Catch ex As Threading.AbandonedMutexException
                ' Ein anderer Prozess endete, waehrend er sie hielt: sie gehoert jetzt uns, und die
                ' Datei ist dank des atomaren Schreibens trotzdem ganz.
            End Try
            Return New HeldMutex(mutex)
        End Function

        Private NotInheritable Class HeldMutex
            Implements IDisposable
            Private _mutex As Threading.Mutex

            Public Sub New(mutex As Threading.Mutex)
                _mutex = mutex
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                Dim held = _mutex
                _mutex = Nothing
                If held Is Nothing Then Return
                Try
                    held.ReleaseMutex()
                Catch ex As Exception
                    DiagnosticLogService.LogException("GpuModelBlock.CrossProcessLock", ex)
                End Try
                held.Dispose()
            End Sub
        End Class

        Private Shared Function ReadFile() As List(Of BlockEntry)
            Dim result As New List(Of BlockEntry)()
            Try
                Dim filePath = BlockFilePath
                If Not File.Exists(filePath) Then Return result
                Dim parsed = JsonSerializer.Deserialize(Of BlockFile)(File.ReadAllText(filePath))
                If parsed?.Entries Is Nothing Then Return result
                For Each entry In parsed.Entries
                    If entry Is Nothing OrElse String.IsNullOrWhiteSpace(entry.Model) OrElse
                       String.IsNullOrWhiteSpace(entry.Device) Then Continue For
                    entry.Model = entry.Model.Trim()
                    entry.Device = entry.Device.Trim()
                    entry.DeviceLabel = If(entry.DeviceLabel, "")
                    entry.ExecutionPath = If(entry.ExecutionPath, "")
                    entry.Driver = If(entry.Driver, "")
                    entry.Build = If(entry.Build, "")
                    entry.Reason = If(entry.Reason, ReasonError)
                    If result.Any(Function(e) e.Matches(entry.Model, entry.Device)) Then Continue For
                    result.Add(entry)
                Next
            Catch ex As Exception
                ' Eine kaputte Datei sperrt nichts. Sie bleibt liegen, bis neu geschrieben wird.
                DiagnosticLogService.LogAlways("Startschutz", $"{BlockFileName} nicht lesbar, wird ignoriert: {ex.GetType().Name}")
                result.Clear()
            End Try
            Return result
        End Function

        ''' <summary>Liegengebliebene Laufmarken gestorbener Prozesse werden zu Sperren. Marken
        ''' dieses Prozesses und eines noch laufenden zweiten FerrumPix zaehlen nicht; Marken einer
        ''' anderen Programmfassung sind erledigt und werden weggeraeumt.</summary>
        Private Shared Function AdoptLeftoverMarkers(list As List(Of BlockEntry)) As Boolean
            Dim changed = False
            Try
                Dim folder = NativeStartGuard.GuardDirectory
                If Not Directory.Exists(folder) Then Return False
                Dim stamp = NativeStartGuard.CurrentStamp()
                For Each marker In Directory.GetFiles(folder, MarkerPrefix & "*.marke")
                    Dim lines As String()
                    Try
                        lines = File.ReadAllLines(marker)
                    Catch
                        Continue For
                    End Try
                    Dim markedPid = -1
                    If lines.Length > 1 Then Integer.TryParse(lines(1), markedPid)
                    If markedPid = Environment.ProcessId OrElse NativeStartGuard.IsOtherRunningInstance(markedPid) Then Continue For
                    Dim markedStamp = If(lines.Length > 0, lines(0), "")
                    If lines.Length >= 4 AndAlso String.Equals(markedStamp, stamp, StringComparison.Ordinal) AndAlso
                       lines(2).Length > 0 AndAlso lines(3).Length > 0 Then
                        Dim fileName = lines(2)
                        Dim deviceKey = lines(3)
                        list.RemoveAll(Function(e) e.Matches(fileName, deviceKey))
                        list.Add(New BlockEntry With {
                            .Model = fileName, .Device = deviceKey,
                            .DeviceLabel = If(lines.Length > 4, lines(4), ""),
                            .ExecutionPath = If(lines.Length > 5, lines(5), ""),
                            .Driver = If(lines.Length > 6, lines(6), ""),
                            .Build = stamp, .Reason = ReasonCrash, .BlockedAtUtc = DateTime.UtcNow})
                        changed = True
                        DiagnosticLogService.LogAlways("Startschutz",
                            $"{fileName} auf {deviceKey}: beim letzten Lauf abgestuerzt, rechnet dort auf dem Prozessor")
                    End If
                    Try
                        File.Delete(marker)
                    Catch
                    End Try
                Next
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Startschutz", $"Laufmarken nicht lesbar: {ex.Message}")
            End Try
            Return changed
        End Function

        ''' <summary>Ist dieses Modell auf dieser Karte gesperrt? Hier verfaellt eine Sperre mit
        ''' neuem Treiber: gefragt wird nur, wenn fuer das Paar eine Sperre besteht.</summary>
        Public Shared Function IsBlocked(fileName As String, deviceKey As String) As Boolean
            If String.IsNullOrEmpty(fileName) OrElse String.IsNullOrEmpty(deviceKey) Then Return False
            Dim recorded As String
            SyncLock _lock
                EnsureLoaded()
                Dim entry = _entries.FirstOrDefault(Function(e) e.Matches(fileName, deviceKey))
                If entry Is Nothing Then Return False
                If entry.Driver.Length = 0 Then Return True
                recorded = entry.Driver
            End SyncLock
            ' Den Treiber AUSSERHALB des Schlosses fragen: das kann Vulkan laden.
            Dim current = CurrentDriver(deviceKey)
            If current.Length = 0 OrElse String.Equals(current, recorded, StringComparison.Ordinal) Then Return True
            Dim removed = False
            SyncLock _lock
                removed = Mutate(Function(list) list.RemoveAll(Function(e) e.Matches(fileName, deviceKey) AndAlso
                                                                           String.Equals(e.Driver, recorded, StringComparison.Ordinal)) > 0)
            End SyncLock
            If removed Then
                DiagnosticLogService.LogAlways("Startschutz",
                    $"{fileName} auf {deviceKey}: neuer Treiber ({recorded} statt {current}), wird wieder versucht")
                RaiseEvent Changed(Nothing, EventArgs.Empty)
            End If
            Return Not removed AndAlso IsBlocked(fileName, deviceKey)
        End Function

        ''' <summary>Gesperrt, und zwar nach einem Absturz? Die Probe uebergeht nur solche Paare.</summary>
        Public Shared Function IsCrashBlocked(fileName As String, deviceKey As String) As Boolean
            If Not IsBlocked(fileName, deviceKey) Then Return False
            SyncLock _lock
                Return _entries.Any(Function(e) e.Matches(fileName, deviceKey) AndAlso e.Reason = ReasonCrash)
            End SyncLock
        End Function

        ''' <summary>Eine Sperre setzen (oder ersetzen). Treiber und Kartenname werden jetzt gelesen;
        ''' das nimmt das Schloss der Grafikbeschleunigung, also nicht unter dem Schloss von
        ''' <see cref="AiModelService"/> rufen (dafuer die Fassung mit beidem schon gelesen).</summary>
        Public Shared Sub Note(fileName As String, deviceKey As String, reason As String)
            If String.IsNullOrEmpty(fileName) OrElse String.IsNullOrEmpty(deviceKey) Then Return
            Note(fileName, deviceKey, reason, GpuAccelerationService.DeviceLabelFor(deviceKey), CurrentDriver(deviceKey))
        End Sub

        ''' <summary>Eine Sperre setzen, Kartenname und Treiber vom Aufrufer gelesen. Nimmt nur das
        ''' eigene Schloss und ist damit auch unter fremden Schloessern sicher.</summary>
        Public Shared Sub Note(fileName As String, deviceKey As String, reason As String, deviceLabel As String, driver As String)
            If String.IsNullOrEmpty(fileName) OrElse String.IsNullOrEmpty(deviceKey) Then Return
            Dim entry = New BlockEntry With {
                .Model = fileName, .Device = deviceKey,
                .DeviceLabel = If(deviceLabel, ""),
                .ExecutionPath = ExecutionPathName, .Driver = If(driver, ""),
                .Build = NativeStartGuard.CurrentStamp(), .Reason = If(reason, ReasonError),
                .BlockedAtUtc = DateTime.UtcNow}
            Dim noted As Boolean
            SyncLock _lock
                EnsureLoaded()
                noted = Mutate(Function(list)
                                   Dim existing = list.FirstOrDefault(Function(e) e.Matches(fileName, deviceKey))
                                   ' Ein Absturz bleibt ein Absturz, auch wenn danach ein Fehler gemeldet
                                   ' wird; auch einer, den ein anderer Prozess eingetragen hat.
                                   If existing IsNot Nothing AndAlso existing.Reason = ReasonCrash AndAlso
                                      String.Equals(existing.Build, entry.Build, StringComparison.Ordinal) AndAlso
                                      entry.Reason = ReasonError Then Return False
                                   list.RemoveAll(Function(e) e.Matches(fileName, deviceKey))
                                   list.Add(entry)
                                   Return True
                               End Function)
            End SyncLock
            If noted Then RaiseEvent Changed(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>Ein Aufbau auf der Karte ist gelungen: eine Sperre aus einem FEHLER faellt. Eine
        ''' aus einem Absturz bleibt; der kann erst im Lauf kommen.</summary>
        Public Shared Sub ClearError(fileName As String, deviceKey As String)
            If String.IsNullOrEmpty(fileName) OrElse String.IsNullOrEmpty(deviceKey) Then Return
            Dim removed As Boolean
            SyncLock _lock
                EnsureLoaded()
                removed = Mutate(Function(list) list.RemoveAll(Function(e) e.Matches(fileName, deviceKey) AndAlso e.Reason <> ReasonCrash) > 0)
            End SyncLock
            If removed Then RaiseEvent Changed(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>Alle Sperren, als Kopien. Laedt keinen Treiber.</summary>
        Public Shared Function Entries() As IReadOnlyList(Of BlockEntry)
            SyncLock _lock
                EnsureLoaded()
                Return _entries.Select(Function(e) e.Copy()).ToList()
            End SyncLock
        End Function

        ''' <summary>Alle Sperren aufheben (Knopf in den Einstellungen).</summary>
        Public Shared Sub ClearAll()
            Dim removed As Boolean
            SyncLock _lock
                EnsureLoaded()
                ' Alles, was JETZT in der Datei steht, auch die Sperren eines anderen Prozesses:
                ' der Knopf meint alle Sperren.
                removed = Mutate(Function(list)
                                     Dim any = list.Count > 0
                                     list.Clear()
                                     Return any
                                 End Function)
            End SyncLock
            If removed Then
                DiagnosticLogService.LogAlways("Startschutz", "Modellsperren von Hand aufgehoben")
                RaiseEvent Changed(Nothing, EventArgs.Empty)
            End If
        End Sub

        ''' <summary>Fuer "Abbrechen" in den Einstellungen: die Sperren eines frueheren Stands, die
        ''' inzwischen fehlen, wieder eintragen. Was seither dazukam, bleibt.</summary>
        Public Shared Sub RestoreMissing(saved As IEnumerable(Of BlockEntry))
            If saved Is Nothing Then Return
            Dim added = False
            SyncLock _lock
                EnsureLoaded()
                Dim stamp = NativeStartGuard.CurrentStamp()
                Dim wanted = saved.Where(Function(e) e IsNot Nothing AndAlso String.Equals(e.Build, stamp, StringComparison.Ordinal)).ToList()
                added = Mutate(Function(list)
                                   Dim any = False
                                   For Each entry In wanted
                                       If list.Any(Function(e) e.Matches(entry.Model, entry.Device)) Then Continue For
                                       list.Add(entry.Copy())
                                       any = True
                                   Next
                                   Return any
                               End Function)
            End SyncLock
            If added Then RaiseEvent Changed(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>Fuer den Pruefstand: beim naechsten Blick neu lesen wie nach einem Neustart.</summary>
        Friend Shared Sub Reload()
            SyncLock _lock
                _entries = Nothing
                _adopted = False
            End SyncLock
        End Sub
    End Class
End Namespace
