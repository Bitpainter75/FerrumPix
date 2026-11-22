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
        ''' Nothing heisst: in diesem Programmlauf noch nicht gelesen.
        Private Shared _entries As List(Of BlockEntry) = Nothing
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

        ''' <summary>Einmal je Programmlauf: Datei lesen, liegengebliebene Marken uebernehmen,
        ''' Sperren eines anderen Baus verwerfen. Unter _lock zu rufen.</summary>
        Private Shared Sub EnsureLoaded()
            If _entries IsNot Nothing Then Return
            Dim list = ReadFile()
            Dim changed = AdoptLeftoverMarkers(list)
            Dim stamp = NativeStartGuard.CurrentStamp()
            Dim before = list.Count
            list.RemoveAll(Function(e) Not String.Equals(e.Build, stamp, StringComparison.Ordinal))
            If list.Count <> before Then
                changed = True
                DiagnosticLogService.LogAlways("Startschutz",
                    $"{before - list.Count} Modellsperre(n) einer anderen Programmfassung verworfen, die Karte wird wieder versucht")
            End If
            _entries = list
            If changed Then WriteFile()
        End Sub

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

        Private Shared Sub WriteFile()
            Dim json = JsonSerializer.Serialize(New BlockFile With {.Entries = _entries},
                                                New JsonSerializerOptions With {.WriteIndented = True})
            JsonStoreService.WriteAtomic(BlockFilePath, json, "GpuModelBlock")
        End Sub

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
                removed = _entries.RemoveAll(Function(e) e.Matches(fileName, deviceKey) AndAlso
                                                         String.Equals(e.Driver, recorded, StringComparison.Ordinal)) > 0
                If removed Then WriteFile()
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
            SyncLock _lock
                EnsureLoaded()
                Dim existing = _entries.FirstOrDefault(Function(e) e.Matches(fileName, deviceKey))
                ' Ein Absturz bleibt ein Absturz, auch wenn danach ein Fehler gemeldet wird.
                If existing IsNot Nothing AndAlso existing.Reason = ReasonCrash AndAlso entry.Reason = ReasonError Then Return
                _entries.RemoveAll(Function(e) e.Matches(fileName, deviceKey))
                _entries.Add(entry)
                WriteFile()
            End SyncLock
            RaiseEvent Changed(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>Ein Aufbau auf der Karte ist gelungen: eine Sperre aus einem FEHLER faellt. Eine
        ''' aus einem Absturz bleibt; der kann erst im Lauf kommen.</summary>
        Public Shared Sub ClearError(fileName As String, deviceKey As String)
            If String.IsNullOrEmpty(fileName) OrElse String.IsNullOrEmpty(deviceKey) Then Return
            Dim removed As Boolean
            SyncLock _lock
                EnsureLoaded()
                removed = _entries.RemoveAll(Function(e) e.Matches(fileName, deviceKey) AndAlso e.Reason <> ReasonCrash) > 0
                If removed Then WriteFile()
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
                removed = _entries.Count > 0
                _entries.Clear()
                If removed Then WriteFile()
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
                For Each entry In saved
                    If entry Is Nothing OrElse Not String.Equals(entry.Build, stamp, StringComparison.Ordinal) Then Continue For
                    If _entries.Any(Function(e) e.Matches(entry.Model, entry.Device)) Then Continue For
                    _entries.Add(entry.Copy())
                    added = True
                Next
                If added Then WriteFile()
            End SyncLock
            If added Then RaiseEvent Changed(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>Fuer den Pruefstand: beim naechsten Blick neu lesen wie nach einem Neustart.</summary>
        Friend Shared Sub Reload()
            SyncLock _lock
                _entries = Nothing
            End SyncLock
        End Sub
    End Class
End Namespace
