Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Text.Json

Namespace Services

    ''' <summary>Wie es um den Katalog dieses Prozesses steht.</summary>
    Public Enum CatalogLockState
        ''' <summary>Dieser Rechner hat den Katalog.</summary>
        Owned
        ''' <summary>Ein anderer Rechner benutzt ihn; hier gilt ein leerer Katalog fuer die Sitzung.</summary>
        HeldElsewhere
        ''' <summary>Der gewaehlte Datenordner ist nicht erreichbar; ebenfalls ein leerer Katalog.</summary>
        Unavailable
    End Enum

    ''' <summary>Wer den Katalog haelt: eine Datei je Prozess im Ordner der Sperren.</summary>
    Public NotInheritable Class CatalogLockHolder
        Public Property Machine As String = ""
        ''' <summary>Die Kennung des Rechners (unter Linux /etc/machine-id), leer, wenn das System
        ''' keine liefert. Zwei Rechner mit demselben Namen (ab Werk "localhost", "raspberrypi")
        ''' unterscheiden sich so trotzdem.</summary>
        Public Property MachineId As String = ""
        Public Property ProcessId As Integer
        ''' <summary>Die Startzeit nach der Uhr DES Rechners, der die Datei schrieb. Nur zur Anzeige
        ''' und zur Reihenfolge der Nennung, nie zur Entscheidung (siehe CatalogLock).</summary>
        Public Property StartedUtc As DateTime
        Public Property AppVersion As String = ""
    End Class

    ''' <summary>Die Sperre des Katalogs gegen einen zweiten RECHNER.
    '''
    ''' WARUM: der Datenordner darf auf einer Freigabe im Netz liegen. SQLite verlaesst sich auf
    ''' Dateisperren, und die wirken ueber SMB und NFS nicht verlaesslich; zwei Rechner, die
    ''' dieselbe library.db zugleich beschreiben, beschaedigen sie. Mehrere Fenster auf DEMSELBEN
    ''' Rechner sind dagegen erlaubt und gewollt (siehe BackgroundRunLock): dort tragen die
    ''' Dateisperren.
    '''
    ''' WIE: jeder Prozess legt beim Start eine kleine Datei "&lt;rechner&gt;.&lt;prozess&gt;.json"
    ''' in den Ordner <see cref="FolderName"/> neben dem Katalog und nimmt sie beim Beenden wieder
    ''' weg. Eine Datei eines ANDEREN Rechners heisst: der Katalog ist belegt. Eigene Dateien eines
    ''' beendeten Prozesses (ein Absturz) raeumt der naechste Start auf diesem Rechner still weg;
    ''' ob ein Prozess auf einem anderen Rechner noch laeuft, laesst sich von hier nicht sagen,
    ''' deshalb fragt die Anwendung, bevor sie eine fremde Sperre uebernimmt
    ''' (<see cref="TakeOver"/>).
    '''
    ''' KEINE ENTSCHEIDUNG NACH DER UHR. Jede Startzeit in einer Sperrdatei stammt von der Uhr des
    ''' Rechners, der sie schrieb, und zwei Rechner an einer Freigabe gehen selten gleich. Haette der
    ''' fruehere Start gewonnen, bekaeme ein Rechner, dessen Uhr nachgeht, den Katalog, obwohl ein
    ''' anderer ihn laengst offen hat, und beide schrieben in dieselbe library.db. Deshalb gilt: erst
    ''' die eigene Datei schreiben, dann nachsehen, und JEDE Datei eines anderen Rechners heisst
    ''' belegt, gleich welche Zeit sie traegt. Starten zwei Rechner zugleich, sieht mindestens einer
    ''' den anderen; schlimmstenfalls ziehen beide zurueck und fragen nach der Uebernahme. Lieber
    ''' faelschlich belegt als faelschlich frei.
    '''
    ''' DERSELBE RECHNER heisst: gleicher Name UND, wo beide eine haben, gleiche Kennung
    ''' (<see cref="CatalogLockHolder.MachineId"/>). Nur dort sagt die Prozessnummer etwas, und nur
    ''' dort darf eine Datei als liegengeblieben gelten.
    '''
    ''' OHNE KATALOG heisst: ein leerer Katalog fuer diese Sitzung im temporaeren Ordner. Alles,
    ''' was ihn fragt, laeuft weiter, nur bleibt nichts davon. Der Katalogindex laeuft dann nicht,
    ''' und eine bereitgelegte Wiederherstellung wartet (CatalogBackupService).</summary>
    Public NotInheritable Class CatalogLock
        Private Sub New()
        End Sub

        ''' <summary>Der Ordner der Sperrdateien im Datenordner.</summary>
        Friend Const FolderName As String = "catalog-locks"

        ''' <summary>Fuer den Pruefstand: ob ein Prozess dieses Rechners noch laeuft.</summary>
        Friend Shared ProcessAliveProbe As Func(Of Integer, Boolean)
        ''' <summary>Fuer den Pruefstand: die eigene Prozessnummer.</summary>
        Friend Shared ProcessIdOverride As Integer = 0
        ''' <summary>Fuer den Pruefstand: die Uhr dieses Rechners, um eine nachgehende oder
        ''' vorgehende nachzustellen.</summary>
        Friend Shared UtcNowOverride As Func(Of DateTime)
        ''' <summary>Fuer den Pruefstand: die Kennung dieses Rechners. Nothing liest die echte.</summary>
        Friend Shared MachineIdOverride As String

        Private Shared _machineId As String

        Private Shared ReadOnly _gate As New Object()
        Private Shared _decided As Boolean
        Private Shared _state As CatalogLockState = CatalogLockState.Owned
        Private Shared _holder As CatalogLockHolder
        Private Shared _ownEntry As String
        Private Shared _sessionCatalog As String
        Private Shared _exitHooked As Boolean

        Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {.WriteIndented = True}

        ''' <summary>Wie es um den Katalog steht; entscheidet beim ersten Zugriff.</summary>
        Public Shared ReadOnly Property State As CatalogLockState
            Get
                EnsureDecided()
                Return _state
            End Get
        End Property

        ''' <summary>Hat dieser Prozess den Katalog im Datenordner?</summary>
        Public Shared ReadOnly Property HasCatalog As Boolean
            Get
                Return State = CatalogLockState.Owned
            End Get
        End Property

        ''' <summary>Wer ihn haelt, wenn ein anderer Rechner ihn hat.</summary>
        Public Shared ReadOnly Property Holder As CatalogLockHolder
            Get
                EnsureDecided()
                Return _holder
            End Get
        End Property

        ''' <summary>Die Datenbank, die dieser Prozess oeffnet.</summary>
        Public Shared ReadOnly Property DatabasePath As String
            Get
                If HasCatalog Then Return AppFolders.CatalogPath
                SyncLock _gate
                    If _sessionCatalog Is Nothing Then _sessionCatalog = CreateSessionCatalogPath()
                    Return _sessionCatalog
                End SyncLock
            End Get
        End Property

        Private Shared ReadOnly Property OwnProcessId As Integer
            Get
                Return If(ProcessIdOverride > 0, ProcessIdOverride, Environment.ProcessId)
            End Get
        End Property

        ''' <summary>Entscheidet fuer den Datenordner dieses Prozesses. Laeuft beim Start
        ''' (AppFolders.PrepareAtStartup), sonst beim ersten Zugriff.</summary>
        Public Shared Sub Acquire()
            EnsureDecided()
        End Sub

        Private Shared Sub EnsureDecided()
            If _decided Then Return
            SyncLock _gate
                If _decided Then Return
                If AppFolders.IsDataRootFallback Then
                    _state = CatalogLockState.Unavailable
                Else
                    Dim holder As CatalogLockHolder = Nothing
                    Dim entry As String = Nothing
                    If TryAcquireAt(AppFolders.DataRoot, holder, entry) Then
                        _state = CatalogLockState.Owned
                        _ownEntry = entry
                    Else
                        _state = CatalogLockState.HeldElsewhere
                        _holder = holder
                    End If
                End If
                HookExit()
                _decided = True
                If _state <> CatalogLockState.Owned Then
                    DiagnosticLogService.LogAlways("CatalogLock", $"no catalog for this session: {_state} {If(_holder?.Machine, "")}")
                End If
            End SyncLock
        End Sub

        Private Shared Sub HookExit()
            If _exitHooked Then Return
            _exitHooked = True
            AddHandler AppDomain.CurrentDomain.ProcessExit, Sub(sender As Object, e As EventArgs) Release()
        End Sub

        ''' <summary>Holt die Sperre in einem Datenordner. False: ein anderer Rechner hat sie, er
        ''' steht in <paramref name="holder"/>.</summary>
        Friend Shared Function TryAcquireAt(dataRoot As String, ByRef holder As CatalogLockHolder, ByRef ownEntry As String) As Boolean
            holder = Nothing
            ownEntry = Nothing
            Dim folder = Path.Combine(dataRoot, FolderName)
            Dim own As CatalogLockHolder
            Try
                Directory.CreateDirectory(folder)
                RemoveDeadOwnMachineEntries(folder)
                own = New CatalogLockHolder With {
                    .Machine = If(AppFolders.MachineNameOverride, Environment.MachineName),
                    .MachineId = OwnMachineId,
                    .ProcessId = OwnProcessId,
                    .StartedUtc = If(UtcNowOverride Is Nothing, DateTime.UtcNow, UtcNowOverride()),
                    .AppVersion = AppVersionText()}
                ownEntry = Path.Combine(folder, EntryName(own))
                Dim temp = ownEntry & ".tmp"
                File.WriteAllText(temp, JsonSerializer.Serialize(own, JsonOptions))
                File.Move(temp, ownEntry, overwrite:=True)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                ' Laesst sich keine Sperre anlegen (schreibgeschuetzter Datenordner), gibt es auch
                ' keinen zweiten Rechner, der dort schreibt. Der Katalog bleibt benutzbar.
                DiagnosticLogService.LogException("CatalogLock.Acquire", ex)
                ownEntry = Nothing
                Return True
            End Try
            ' Erst geschrieben, jetzt nachgesehen: jede Datei eines anderen Rechners heisst belegt,
            ' gleich welche Startzeit sie traegt (siehe oben, keine Entscheidung nach der Uhr). Die
            ' Zeit ordnet nur, wer in der Rueckfrage genannt wird.
            Dim foreign = ReadEntries(folder).
                Where(Function(e) Not IsOwnMachine(e.Holder)).
                OrderBy(Function(e) e.Holder.StartedUtc).ToList()
            If foreign.Count = 0 Then Return True
            holder = foreign(0).Holder
            Try
                File.Delete(ownEntry)
            Catch
            End Try
            ownEntry = Nothing
            Return False
        End Function

        ''' <summary>Haelt ein anderer Rechner den Katalog in diesem Datenordner, oder ein
        ''' anderes laufendes FerrumPix auf diesem? Fuer die Frage, ob der Datenordner umziehen
        ''' darf. Nothing heisst: niemand.</summary>
        Friend Shared Function ForeignHolderAt(dataRoot As String) As CatalogLockHolder
            Dim folder = Path.Combine(dataRoot, FolderName)
            For Each entry In ReadEntries(folder).OrderBy(Function(e) e.Holder.StartedUtc)
                If Not IsOwnMachine(entry.Holder) Then Return entry.Holder
                If entry.Holder.ProcessId <> OwnProcessId AndAlso IsAlive(entry.Holder.ProcessId) Then Return entry.Holder
            Next
            Return Nothing
        End Function

        ''' <summary>Uebernimmt die Sperre eines anderen Rechners, nach der Rueckfrage. Der leere
        ''' Katalog dieser Sitzung bleibt; geoeffnet wird der eigentliche beim naechsten Start.</summary>
        Public Shared Function TakeOver() As Boolean
            Return TakeOverAt(AppFolders.DataRoot)
        End Function

        Friend Shared Function TakeOverAt(dataRoot As String) As Boolean
            Dim folder = Path.Combine(dataRoot, FolderName)
            Try
                For Each entry In ReadEntries(folder)
                    If IsOwnMachine(entry.Holder) Then Continue For
                    File.Delete(entry.FilePath)
                    DiagnosticLogService.LogAlways("CatalogLock", $"taken over from {entry.Holder.Machine}")
                Next
                Return True
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                DiagnosticLogService.LogException("CatalogLock.TakeOver", ex)
                Return False
            End Try
        End Function

        ''' <summary>Gibt die Sperre frei und entfernt den leeren Katalog der Sitzung. Beim
        ''' Beenden des Prozesses.</summary>
        Public Shared Sub Release()
            SyncLock _gate
                Try
                    If _ownEntry IsNot Nothing AndAlso File.Exists(_ownEntry) Then File.Delete(_ownEntry)
                    Dim folder = If(_ownEntry Is Nothing, Nothing, Path.GetDirectoryName(_ownEntry))
                    If folder IsNot Nothing AndAlso FolderRelocation.IsEmptyDirectory(folder) Then Directory.Delete(folder)
                Catch
                End Try
                _ownEntry = Nothing
                If _sessionCatalog IsNot Nothing Then
                    For Each suffix In {"", "-wal", "-shm"}
                        Try
                            If File.Exists(_sessionCatalog & suffix) Then File.Delete(_sessionCatalog & suffix)
                        Catch
                        End Try
                    Next
                End If
            End SyncLock
        End Sub

        ''' <summary>Fuer den Pruefstand: vergessen, was entschieden war, und die eigene Sperre
        ''' freigeben.</summary>
        Friend Shared Sub ResetForTest()
            SyncLock _gate
                Release()
                _sessionCatalog = Nothing
                _holder = Nothing
                _state = CatalogLockState.Owned
                _decided = False
                _machineId = Nothing
            End SyncLock
        End Sub

        Private Shared Function EntryName(holder As CatalogLockHolder) As String
            Return AppFolders.SafeMachineName(holder.Machine) & "." &
                   holder.ProcessId.ToString(Globalization.CultureInfo.InvariantCulture) & ".json"
        End Function

        ''' <summary>Derselbe Rechner: gleicher Name, und tragen beide eine Kennung, auch dieselbe.
        ''' Fehlt eine (ein System ohne machine-id, eine Datei einer aelteren Fassung), entscheidet
        ''' der Name allein, wie bisher.</summary>
        Private Shared Function IsOwnMachine(holder As CatalogLockHolder) As Boolean
            If Not String.Equals(AppFolders.SafeMachineName(holder.Machine), AppFolders.MachineTag, StringComparison.OrdinalIgnoreCase) Then Return False
            Dim ownId = OwnMachineId
            Dim otherId = If(holder.MachineId, "")
            If ownId.Length = 0 OrElse otherId.Length = 0 Then Return True
            Return String.Equals(ownId, otherId, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Die Kennung dieses Rechners: unter Linux /etc/machine-id (auch im Flatpak
        ''' sichtbar), ersatzweise die von D-Bus; sonst leer.</summary>
        Private Shared ReadOnly Property OwnMachineId As String
            Get
                If MachineIdOverride IsNot Nothing Then Return MachineIdOverride
                Dim known = _machineId
                If known IsNot Nothing Then Return known
                Dim found = ""
                If OperatingSystem.IsLinux() Then
                    For Each candidate In {"/etc/machine-id", "/var/lib/dbus/machine-id"}
                        Try
                            If Not File.Exists(candidate) Then Continue For
                            Dim content = File.ReadAllText(candidate).Trim()
                            If content.Length > 0 Then
                                ' Nicht die Kennung selbst auf die Freigabe legen (machine-id(5)
                                ' raet davon ab), sondern einen davon abgeleiteten Wert.
                                Dim digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("FerrumPix catalog lock " & content))
                                found = Convert.ToHexString(digest, 0, 16)
                                Exit For
                            End If
                        Catch
                        End Try
                    Next
                End If
                _machineId = found
                Return found
            End Get
        End Property

        Private Shared Function IsAlive(processId As Integer) As Boolean
            If ProcessAliveProbe IsNot Nothing Then Return ProcessAliveProbe(processId)
            Try
                Using running = Diagnostics.Process.GetProcessById(processId)
                    Return Not running.HasExited
                End Using
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Eigene Dateien beendeter Prozesse: liegengeblieben nach einem Absturz.</summary>
        Private Shared Sub RemoveDeadOwnMachineEntries(folder As String)
            For Each entry In ReadEntries(folder)
                If Not IsOwnMachine(entry.Holder) Then Continue For
                If entry.Holder.ProcessId = OwnProcessId OrElse IsAlive(entry.Holder.ProcessId) Then Continue For
                Try
                    File.Delete(entry.FilePath)
                Catch
                End Try
            Next
        End Sub

        Private Structure LockEntry
            Public FilePath As String
            Public Holder As CatalogLockHolder
        End Structure

        Private Shared Function ReadEntries(folder As String) As List(Of LockEntry)
            Dim result As New List(Of LockEntry)()
            Try
                If Not Directory.Exists(folder) Then Return result
                For Each file In Directory.GetFiles(folder, "*.json")
                    Try
                        Dim holder = JsonSerializer.Deserialize(Of CatalogLockHolder)(IO.File.ReadAllText(file))
                        If holder Is Nothing OrElse String.IsNullOrWhiteSpace(holder.Machine) Then Continue For
                        result.Add(New LockEntry With {.FilePath = file, .Holder = holder})
                    Catch
                        ' Eine halb geschriebene oder fremde Datei zaehlt nicht.
                    End Try
                Next
            Catch
            End Try
            Return result
        End Function

        Private Shared Function CreateSessionCatalogPath() As String
            Dim folder = Path.Combine(Path.GetTempPath(), AppFolders.AppFolderName)
            Dim file = Path.Combine(folder, "session-catalog-" & OwnProcessId.ToString(Globalization.CultureInfo.InvariantCulture) & ".db")
            Try
                Directory.CreateDirectory(folder)
                For Each suffix In {"", "-wal", "-shm"}
                    If IO.File.Exists(file & suffix) Then IO.File.Delete(file & suffix)
                Next
            Catch
            End Try
            Return file
        End Function

        Private Shared Function AppVersionText() As String
            Try
                Return If(Reflection.Assembly.GetExecutingAssembly().
                          GetCustomAttributes(GetType(Reflection.AssemblyInformationalVersionAttribute), False).
                          OfType(Of Reflection.AssemblyInformationalVersionAttribute)().FirstOrDefault()?.InformationalVersion, "")
            Catch
                Return ""
            End Try
        End Function

    End Class

End Namespace
