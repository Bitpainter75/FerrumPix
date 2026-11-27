Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json

Namespace Services

    ''' <summary>Was eine Freigabe zeigt: ein Bild, eine Auswahl, einen Ordner (mit oder ohne
    ''' Unterordner) oder ein Suchergebnis. Auswahl und Suchergebnis sind feste Listen zum
    ''' Zeitpunkt des Teilens; ein Ordner zeigt, was beim Aufruf darin liegt.</summary>
    Public Enum ShareKind
        Image
        Selection
        Folder
        Search
    End Enum

    ''' <summary>Eine Freigabe per Link, wie sie gespeichert ist. Der Link selbst steht nirgends,
    ''' nur der SHA-256 seines Tokens.</summary>
    Public Class ShareRecord
        ''' <summary>Kennung fuer die Verwaltung, 16 Zeichen Hex. Gibt keinen Zugang.</summary>
        Public Property Id As String = ""
        ''' <summary>SHA-256 des Tokens im Link, als Hex.</summary>
        Public Property TokenHash As String = ""
        ''' <summary>Wer geteilt hat (AppLockService.OwnerUserId oder ein weiterer Benutzer).</summary>
        Public Property OwnerUserId As String = ""
        Public Property Title As String = ""
        ''' <summary>Name einer <see cref="ShareKind"/>.</summary>
        Public Property Kind As String = NameOf(ShareKind.Selection)
        ''' <summary>Nur bei einem Ordner: der Ordner.</summary>
        Public Property Folder As String = ""
        Public Property IncludeSubfolders As Boolean
        ''' <summary>Bei Bild, Auswahl und Suchergebnis: die Dateien.</summary>
        Public Property Paths As List(Of String) = New List(Of String)()
        ''' <summary>Je Pfad aus <see cref="Paths"/> der Fingerabdruck der Datei beim Teilen
        ''' (<see cref="ShareStore.StampOf"/>). Liegt spaeter eine andere Datei unter demselben
        ''' Pfad, passt er nicht mehr, und das Bild gilt als fehlend.</summary>
        Public Property PathStamps As Dictionary(Of String, String) = New Dictionary(Of String, String)(StringComparer.Ordinal)
        Public Property CreatedUtc As String = ""
        ''' <summary>Ende der Freigabe, ISO 8601 in UTC; leer heisst nie.</summary>
        Public Property ExpiresUtc As String = ""
        ''' <summary>PBKDF2 wie das Kennwort der Anmeldung (AppLockService.HashPassword); leer
        ''' heisst ohne Kennwort.</summary>
        Public Property PasswordHash As String = ""
        Public Property AllowDownload As Boolean = True
        Public Property ShareLocation As Boolean
        Public Property SharePeople As Boolean
        Public Property ShareCamera As Boolean = True
        ''' <summary>Wie oft die Seite der Freigabe geoeffnet wurde.</summary>
        Public Property Views As Integer
        Public Property LastViewedUtc As String = ""

        ''' <summary>Die Art als Wert; Unbekanntes gilt als Auswahl.</summary>
        Public Function KindValue() As ShareKind
            Dim value As ShareKind
            If [Enum].TryParse(If(Kind, ""), ignoreCase:=True, result:=value) AndAlso [Enum].IsDefined(value) Then Return value
            Return ShareKind.Selection
        End Function

        ''' <summary>Eine tiefe Kopie: wer liest, bekommt nie das gemerkte Objekt.</summary>
        Public Function Clone() As ShareRecord
            Dim copy = DirectCast(MemberwiseClone(), ShareRecord)
            copy.Paths = If(Paths, New List(Of String)()).ToList()
            copy.PathStamps = New Dictionary(Of String, String)(If(PathStamps, New Dictionary(Of String, String)()), StringComparer.Ordinal)
            Return copy
        End Function
    End Class

    ''' <summary>Der Inhalt der Datei shares.json.</summary>
    Public Class ShareStoreFile
        Public Property FormatVersion As Integer = 1
        ''' <summary>Alle Freigaben angehalten: jeder Link antwortet wie ein unbekannter.</summary>
        Public Property Paused As Boolean
        Public Property Shares As List(Of ShareRecord) = New List(Of ShareRecord)()
    End Class

    ''' <summary>
    ''' Die Freigaben per Link (Plan BROWSER_MOBIL.md, "Gebaut: Teilen"). Hier stehen nur Ablage,
    ''' Token und Pflege; was ein Link zeigt und wer teilen darf, steht in BrowserBridgeShares.vb.
    '''
    ''' <para>DER LINK traegt ein zufaelliges Token aus 32 Bytes (256 Bit) in Base64url. Gespeichert
    ''' wird nur sein SHA-256; das Token selbst kommt genau einmal heraus, beim Anlegen oder beim
    ''' Erneuern (<see cref="Relink"/>). Wer die Datei liest, kann daraus keinen Link bauen.</para>
    '''
    ''' <para>ABLAGE im Datenordner (shares.json), nicht in den Einstellungen: Freigaben gehoeren zum
    ''' Bestand und muessen einen Umzug des Datenordners und einen neuen Behaelter ueberdauern (im
    ''' Behaelter liegt der Datenordner auf dem eingebundenen Band). In eine Sicherung kommen sie
    ''' nicht: die Datei traegt die Hashes der Kennwoerter der Freigaben, und eine Sicherung nimmt
    ''' Hashes nur mit ausdruecklicher Zustimmung mit (CatalogBackupService, Teil "Zugangsdaten").
    ''' Ein eigener Teil der Sicherung fuer Freigaben steht aus.</para>
    '''
    ''' <para>PFADE wandern mit: verschiebt oder benennt FerrumPix eine Datei oder einen Ordner um
    ''' (LibraryService.MoveEverythingForPath), zieht <see cref="MovePath"/> Liste und Ordner der
    ''' Freigaben nach. Eine Datei, die nicht mehr da ist, faellt beim Ausliefern still heraus.</para>
    '''
    ''' <para>FINGERABDRUCK je Pfad einer festen Liste (<see cref="StampOf"/>: Groesse und
    ''' Aenderungszeit): liegt unter dem Pfad spaeter eine andere Datei, zeigt die Freigabe sie
    ''' nicht, das Bild gilt als fehlend. Was FerrumPix selbst schreibt (Speichern einer
    ''' Bearbeitung, Stichwoerter, Ort, ueber ImageProcessor.WriteFileAtomic bzw.
    ''' CopyOverOriginal), fuehrt den Fingerabdruck nach (<see cref="NoteFileWritten"/>); ein
    ''' Verschieben in FerrumPix ebenso (<see cref="MovePath"/>). Loescht FerrumPix die Datei
    ''' (Papierkorb), faellt der Pfad aus der Liste (<see cref="ForgetPath"/>). Ordnerfreigaben
    ''' zeigen, was gerade darin liegt, und brauchen keinen.</para>
    '''
    ''' <para>AUFRUFE (<see cref="NoteView"/>) zaehlt die Ablage im Speicher und schreibt sie
    ''' gebuendelt, hoechstens alle 30 Sekunden und beim Beenden; ein Aufruf wartet nie auf die
    ''' Platte.</para>
    ''' </summary>
    Public NotInheritable Class ShareStore

        Private Sub New()
        End Sub

        Private Const FileName As String = "shares.json"

        ''' <summary>Laenge des Tokens in Bytes. 32 Bytes sind 256 Bit, weit ueber dem Mindestmass von
        ''' 128 Bit; Raten scheitert an der Zahl, nicht erst an der Bremse.</summary>
        Public Const TokenBytes As Integer = 32

        ''' <summary>So viele Zeichen hat ein Token in Base64url ohne Auffuellung.</summary>
        Public Const TokenLength As Integer = 43

        ''' <summary>Hoechstens so viele Freigaben; danach lehnt das Anlegen ab.</summary>
        Public Const MaxShares As Integer = 2000

        ''' <summary>Hoechstens so viele Freigaben je Benutzer; danach lehnt das Anlegen mit einer
        ''' Meldung ab. Ein einzelner Benutzer soll die Ablage nicht fuer alle anderen fuellen.</summary>
        Public Const MaxSharesPerUser As Integer = 100

        ''' <summary>Hoechstens so viele Bilder in einer Liste (Bild, Auswahl, Suchergebnis).</summary>
        Public Const MaxPathsPerShare As Integer = 2000

        ''' <summary>So lange sammeln sich Aufrufe im Speicher, bevor sie geschrieben werden.</summary>
        Private Const ViewFlushDelayMs As Integer = 30_000

        ''' <summary>Nur fuer den Pruefstand: eine andere Datei statt der im Datenordner.</summary>
        Friend Shared StorePathOverride As String

        Private Shared ReadOnly Gate As New Object()
        Private Shared _file As ShareStoreFile
        Private Shared _loadedFrom As String = ""
        Private Shared _version As Integer

        ''' <summary>Alle Pfade aller festen Listen, fuer die schnelle Frage "ist diese Datei
        ''' ueberhaupt geteilt?" beim Schreiben. Nothing heisst neu aufbauen. Nur unter
        ''' <see cref="Gate"/>.</summary>
        Private Shared _sharedPaths As HashSet(Of String)

        ''' <summary>Gezaehlte, noch nicht geschriebene Aufrufe je Freigabe. Eigene kleine Sperre,
        ''' damit ein Aufruf nie auf die der Ablage wartet.</summary>
        Private Shared ReadOnly ViewGate As New Object()
        Private Shared ReadOnly PendingViews As New Dictionary(Of String, (Count As Integer, LastUtc As String))(StringComparer.Ordinal)
        Private Shared _viewTimer As Threading.Timer

        ''' <summary>Warum ein Anlegen scheiterte.</summary>
        Public Enum CreateProblem
            None
            ''' <summary>Die Ablage hat schon <see cref="MaxShares"/> Freigaben.</summary>
            StoreFull
            ''' <summary>Der Benutzer hat schon <see cref="MaxSharesPerUser"/> Freigaben.</summary>
            UserLimit
            ''' <summary>Mehr als <see cref="MaxPathsPerShare"/> Bilder.</summary>
            TooManyPaths
            ''' <summary>Die Datei liess sich nicht schreiben.</summary>
            SaveFailed
        End Enum

        Shared Sub New()
            ' Gezaehlte Aufrufe gehen beim Beenden nicht verloren.
            AddHandler AppDomain.CurrentDomain.ProcessExit, Sub(sender As Object, e As EventArgs) FlushViews()
        End Sub

        ''' <summary>Freigaben haben sich geaendert. Kann von jedem Faden kommen.</summary>
        Public Shared Event Changed As EventHandler

        ''' <summary>Zaehlt jede Aenderung; Zwischenspeicher der Auslieferung nehmen ihn in ihren
        ''' Schluessel.</summary>
        Public Shared ReadOnly Property Version As Integer
            Get
                Return Threading.Volatile.Read(_version)
            End Get
        End Property

        Public Shared ReadOnly Property StorePath As String
            Get
                Dim forTests = StorePathOverride
                If Not String.IsNullOrEmpty(forTests) Then Return forTests
                Return AppFolders.InData(FileName)
            End Get
        End Property

        ' --- Lesen und Schreiben -------------------------------------------------------------

        ''' <summary>Der Stand im Speicher; gelesen beim ersten Zugriff und wenn sich der Ort
        ''' geaendert hat. Nur unter <see cref="Gate"/> rufen.</summary>
        Private Shared Function Current() As ShareStoreFile
            Dim target = StorePath
            If _file IsNot Nothing AndAlso String.Equals(_loadedFrom, target, StringComparison.Ordinal) Then Return _file
            Dim loaded As ShareStoreFile = Nothing
            Try
                If File.Exists(target) Then
                    loaded = JsonSerializer.Deserialize(Of ShareStoreFile)(File.ReadAllText(target, Encoding.UTF8))
                End If
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is JsonException
                DiagnosticLogService.LogException("Shares.Load", ex)
                JsonStoreService.BackupUnreadable(target, "Shares")
            End Try
            If loaded Is Nothing Then loaded = New ShareStoreFile()
            If loaded.Shares Is Nothing Then loaded.Shares = New List(Of ShareRecord)()
            loaded.Shares.RemoveAll(Function(s) s Is Nothing OrElse String.IsNullOrEmpty(s.Id) OrElse String.IsNullOrEmpty(s.TokenHash))
            Dim filledStamps = False
            For Each share In loaded.Shares
                If share.Paths Is Nothing Then share.Paths = New List(Of String)()
                If share.PathStamps Is Nothing Then share.PathStamps = New Dictionary(Of String, String)(StringComparer.Ordinal)
                ' Eine Freigabe aus der Zeit vor den Fingerabdruecken bekommt den Stand von jetzt:
                ' die Datei, die jetzt dort liegt, ist die, die bisher gezeigt wurde.
                For Each filePath In share.Paths
                    If Not share.PathStamps.ContainsKey(filePath) Then
                        ' Fehlt sie jetzt, ist eine spaetere Datei dort eine fremde: "-" passt auf keine.
                        Dim stamp = StampOf(filePath)
                        share.PathStamps(filePath) = If(stamp.Length > 0, stamp, "-")
                        filledStamps = True
                    End If
                Next
            Next
            _file = loaded
            _loadedFrom = target
            _sharedPaths = Nothing
            If filledStamps Then Save()
            Return _file
        End Function

        ''' <summary>Schreibt den Stand atomar. Nur unter <see cref="Gate"/> rufen.</summary>
        Private Shared Function Save() As Boolean
            Dim json = JsonSerializer.Serialize(_file, New JsonSerializerOptions With {.WriteIndented = True})
            Dim target = StorePath
            Try
                Dim folder = IO.Path.GetDirectoryName(target)
                If Not String.IsNullOrEmpty(folder) Then Directory.CreateDirectory(folder)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                DiagnosticLogService.LogException("Shares.Save", ex)
                Return False
            End Try
            Return JsonStoreService.WriteAtomic(target, json, "Shares")
        End Function

        Private Shared Sub NoteChange()
            Threading.Interlocked.Increment(_version)
            RaiseEvent Changed(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>Vergisst den Stand im Speicher. Fuer den Pruefstand, nach dem Umsetzen der Datei.</summary>
        Friend Shared Sub ResetForTests()
            SyncLock ViewGate
                PendingViews.Clear()
            End SyncLock
            SyncLock Gate
                _file = Nothing
                _loadedFrom = ""
                _sharedPaths = Nothing
            End SyncLock
            NoteChange()
        End Sub

        ' --- Fingerabdruck einer Datei ------------------------------------------------------------

        ''' <summary>Der Fingerabdruck einer Datei: Groesse und Aenderungszeit in UTC-Ticks, leer,
        ''' wenn es sie nicht gibt. Eine andere Datei unter demselben Pfad unterscheidet sich darin
        ''' praktisch immer. Die Kennung der Datei im Dateisystem (Inode) gehoert bewusst nicht
        ''' dazu: .NET liefert sie nicht, und jedes atomare Speichern (Nachbardatei, dann
        ''' umhaengen) gaebe ohnehin eine neue; sie unterschiede nichts, was Groesse und Zeit nicht
        ''' schon unterscheiden.</summary>
        Public Shared Function StampOf(filePath As String) As String
            If String.IsNullOrEmpty(filePath) Then Return ""
            Try
                Dim info As New FileInfo(filePath)
                If Not info.Exists Then Return ""
                Return info.Length.ToString(CultureInfo.InvariantCulture) & ":" &
                       info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                       TypeOf ex Is ArgumentException OrElse TypeOf ex Is NotSupportedException
                Return ""
            End Try
        End Function

        ''' <summary>Liegt unter dem Pfad noch die Datei, die geteilt wurde (oder die FerrumPix
        ''' seither selbst geschrieben hat)? Ohne gespeicherten Fingerabdruck gilt ja.</summary>
        Public Shared Function IsSameFile(record As ShareRecord, filePath As String) As Boolean
            If record Is Nothing OrElse String.IsNullOrEmpty(filePath) Then Return False
            Dim expected As String = Nothing
            If record.PathStamps Is Nothing OrElse Not record.PathStamps.TryGetValue(filePath, expected) Then Return True
            Dim actual = StampOf(filePath)
            Return actual.Length > 0 AndAlso String.Equals(actual, expected, StringComparison.Ordinal)
        End Function

        ''' <summary>Alle Pfade aller festen Listen. Nur unter <see cref="Gate"/>.</summary>
        Private Shared Function SharedPaths() As HashSet(Of String)
            If _sharedPaths IsNot Nothing Then Return _sharedPaths
            Dim result As New HashSet(Of String)(PathIdentity.Comparer)
            For Each share In Current().Shares
                For Each filePath In share.Paths
                    result.Add(filePath)
                Next
            Next
            _sharedPaths = result
            Return result
        End Function

        ' --- Token ------------------------------------------------------------------------------

        ''' <summary>Ein neues Token: 32 zufaellige Bytes in Base64url, 43 Zeichen.</summary>
        Friend Shared Function NewToken() As String
            Return Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).TrimEnd("="c).Replace("+"c, "-"c).Replace("/"c, "_"c)
        End Function

        ''' <summary>Sieht der Text aus wie ein Token von hier? Nur dann wird ueberhaupt gesucht.</summary>
        Public Shared Function IsWellFormedToken(token As String) As Boolean
            If String.IsNullOrEmpty(token) OrElse token.Length <> TokenLength Then Return False
            Return token.All(Function(c) Char.IsAsciiLetterOrDigit(c) OrElse c = "-"c OrElse c = "_"c)
        End Function

        ''' <summary>Der gespeicherte Wert zu einem Token: SHA-256 als Hex.</summary>
        Public Shared Function HashToken(token As String) As String
            Return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(If(token, ""))))
        End Function

        ' --- Abfragen ---------------------------------------------------------------------------

        ''' <summary>Alle Freigaben, als Kopien, die neuesten zuerst.</summary>
        Public Shared Function All() As List(Of ShareRecord)
            Dim copies As List(Of ShareRecord)
            SyncLock Gate
                copies = Current().Shares.Select(Function(s) s.Clone()).
                    OrderByDescending(Function(s) s.CreatedUtc, StringComparer.Ordinal).ToList()
            End SyncLock
            For Each copy In copies
                AddPendingViews(copy)
            Next
            Return copies
        End Function

        ''' <summary>Zaehlt in eine Kopie die Aufrufe, die noch nicht geschrieben sind.</summary>
        Private Shared Function AddPendingViews(copy As ShareRecord) As ShareRecord
            If copy Is Nothing Then Return Nothing
            SyncLock ViewGate
                Dim pending As (Count As Integer, LastUtc As String) = (0, "")
                If PendingViews.TryGetValue(copy.Id, pending) Then
                    copy.Views = CInt(Math.Min(CLng(copy.Views) + pending.Count, Integer.MaxValue))
                    copy.LastViewedUtc = pending.LastUtc
                End If
            End SyncLock
            Return copy
        End Function

        ''' <summary>Die Freigaben eines Benutzers, als Kopien, die neuesten zuerst.</summary>
        Public Shared Function OwnedBy(userId As String) As List(Of ShareRecord)
            Return All().Where(Function(s) String.Equals(s.OwnerUserId, userId, StringComparison.Ordinal)).ToList()
        End Function

        ''' <summary>Eine Freigabe nach ihrer Kennung, als Kopie, sonst Nothing.</summary>
        Public Shared Function Find(id As String) As ShareRecord
            If String.IsNullOrEmpty(id) Then Return Nothing
            Dim copy As ShareRecord
            SyncLock Gate
                copy = Current().Shares.FirstOrDefault(Function(s) String.Equals(s.Id, id, StringComparison.Ordinal))?.Clone()
            End SyncLock
            Return AddPendingViews(copy)
        End Function

        ''' <summary>Die Freigabe zu einem Token, als Kopie, sonst Nothing. Prueft nur, ob es sie
        ''' gibt; ob sie gilt (Ablauf, Pause, Rechte), entscheidet die Auslieferung.</summary>
        Public Shared Function FindByToken(token As String) As ShareRecord
            If Not IsWellFormedToken(token) Then Return Nothing
            Dim hash = HashToken(token)
            SyncLock Gate
                Return Current().Shares.FirstOrDefault(Function(s) String.Equals(s.TokenHash, hash, StringComparison.Ordinal))?.Clone()
            End SyncLock
        End Function

        ''' <summary>Sind alle Freigaben angehalten?</summary>
        Public Shared ReadOnly Property Paused As Boolean
            Get
                SyncLock Gate
                    Return Current().Paused
                End SyncLock
            End Get
        End Property

        ''' <summary>Ist die Freigabe abgelaufen? Ohne Ende nie; ein unlesbares Ende gilt als
        ''' abgelaufen.</summary>
        Public Shared Function IsExpired(record As ShareRecord, nowUtc As DateTime) As Boolean
            If record Is Nothing Then Return True
            Dim until = ExpiryOf(record)
            If Not until.HasValue Then Return Not String.IsNullOrWhiteSpace(record.ExpiresUtc)
            Return nowUtc >= until.Value
        End Function

        ''' <summary>Das Ende einer Freigabe in UTC, Nothing ohne (oder unlesbar).</summary>
        Public Shared Function ExpiryOf(record As ShareRecord) As DateTime?
            If record Is Nothing OrElse String.IsNullOrWhiteSpace(record.ExpiresUtc) Then Return Nothing
            Dim parsed As DateTime
            If Not DateTime.TryParse(record.ExpiresUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, parsed) Then Return Nothing
            Return If(parsed.Kind = DateTimeKind.Local, parsed.ToUniversalTime(), DateTime.SpecifyKind(parsed, DateTimeKind.Utc))
        End Function

        ''' <summary>Das Ende als gespeicherter Text: <paramref name="days"/> Tage ab jetzt, 0 oder
        ''' weniger heisst nie.</summary>
        Public Shared Function ExpiryText(days As Integer, nowUtc As DateTime) As String
            If days <= 0 Then Return ""
            Return nowUtc.AddDays(Math.Min(days, 3650)).ToString("o", CultureInfo.InvariantCulture)
        End Function

        ' --- Aendern ----------------------------------------------------------------------------

        ''' <summary>Legt eine Freigabe an und liefert das Token, genau dieses eine Mal. Kennung,
        ''' Hash des Tokens und Anlagezeit setzt die Ablage; der Rest kommt aus
        ''' <paramref name="record"/>. Nothing, wenn die Ablage voll ist oder nicht schreiben konnte.</summary>
        Public Shared Function Create(record As ShareRecord, ByRef token As String) As ShareRecord
            Dim problem As CreateProblem
            Return Create(record, token, problem)
        End Function

        ''' <summary>Wie oben, nennt aber den Grund, wenn es scheitert: Ablage voll, Grenze des
        ''' Benutzers, zu viele Bilder, nicht geschrieben.</summary>
        Public Shared Function Create(record As ShareRecord, ByRef token As String, ByRef problem As CreateProblem) As ShareRecord
            token = Nothing
            problem = CreateProblem.SaveFailed
            If record Is Nothing Then Return Nothing
            Dim fresh = record.Clone()
            If fresh.Paths.Count > MaxPathsPerShare Then
                problem = CreateProblem.TooManyPaths
                Return Nothing
            End If
            Dim generatedToken = NewToken()
            fresh.Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()
            fresh.TokenHash = HashToken(generatedToken)
            fresh.CreatedUtc = AppLockService.Clock().ToString("o", CultureInfo.InvariantCulture)
            fresh.Views = 0
            fresh.LastViewedUtc = ""
            ' Der Fingerabdruck jeder Datei, wie sie JETZT ist; ausserhalb der Sperre, er fragt die Platte.
            fresh.PathStamps = New Dictionary(Of String, String)(StringComparer.Ordinal)
            For Each filePath In fresh.Paths
                fresh.PathStamps(filePath) = StampOf(filePath)
            Next
            SyncLock Gate
                Dim store = Current()
                If store.Shares.Count >= MaxShares Then
                    problem = CreateProblem.StoreFull
                    Return Nothing
                End If
                If store.Shares.Where(Function(s) String.Equals(s.OwnerUserId, fresh.OwnerUserId, StringComparison.Ordinal)).Count() >= MaxSharesPerUser Then
                    problem = CreateProblem.UserLimit
                    Return Nothing
                End If
                store.Shares.Add(fresh)
                _sharedPaths = Nothing
                If Not Save() Then
                    store.Shares.Remove(fresh)
                    Return Nothing
                End If
            End SyncLock
            problem = CreateProblem.None
            DiagnosticLogService.LogAlways("Shares", $"Share {fresh.Id} created by {fresh.OwnerUserId}.")
            NoteChange()
            token = generatedToken
            Return fresh.Clone()
        End Function

        ''' <summary>Aendert eine Freigabe. Kennung, Token, Besitzer und Inhalt bleiben, was immer
        ''' <paramref name="change"/> damit tut.</summary>
        Public Shared Function Update(id As String, change As Action(Of ShareRecord)) As Boolean
            If String.IsNullOrEmpty(id) OrElse change Is Nothing Then Return False
            SyncLock Gate
                Dim store = Current()
                Dim index = store.Shares.FindIndex(Function(s) String.Equals(s.Id, id, StringComparison.Ordinal))
                If index < 0 Then Return False
                Dim original = store.Shares(index)
                Dim copy = original.Clone()
                change(copy)
                copy.Id = original.Id
                copy.TokenHash = original.TokenHash
                copy.OwnerUserId = original.OwnerUserId
                copy.Kind = original.Kind
                copy.Folder = original.Folder
                copy.Paths = original.Paths.ToList()
                copy.PathStamps = New Dictionary(Of String, String)(original.PathStamps, StringComparer.Ordinal)
                copy.Views = original.Views
                copy.LastViewedUtc = original.LastViewedUtc
                copy.CreatedUtc = original.CreatedUtc
                store.Shares(index) = copy
                If Not Save() Then
                    store.Shares(index) = original
                    Return False
                End If
            End SyncLock
            NoteChange()
            Return True
        End Function

        ''' <summary>Ein neues Token fuer eine Freigabe; das alte gilt sofort nicht mehr. Fuer
        ''' "Link kopieren" auf einem Geraet, das den Link nicht kennt: gespeichert ist nur der
        ''' Hash, den alten Link gibt es nirgends mehr.</summary>
        Public Shared Function Relink(id As String, ByRef token As String) As Boolean
            token = Nothing
            Dim generatedToken = NewToken()
            SyncLock Gate
                Dim store = Current()
                Dim found = store.Shares.FirstOrDefault(Function(s) String.Equals(s.Id, id, StringComparison.Ordinal))
                If found Is Nothing Then Return False
                Dim before = found.TokenHash
                found.TokenHash = HashToken(generatedToken)
                If Not Save() Then
                    found.TokenHash = before
                    Return False
                End If
            End SyncLock
            DiagnosticLogService.LogAlways("Shares", $"Share {id} got a new link.")
            NoteChange()
            token = generatedToken
            Return True
        End Function

        ''' <summary>Widerruft eine Freigabe: sie verschwindet ganz, ihr Link antwortet danach wie
        ''' ein unbekannter.</summary>
        Public Shared Function Revoke(id As String) As Boolean
            If String.IsNullOrEmpty(id) Then Return False
            SyncLock Gate
                Dim store = Current()
                Dim found = store.Shares.FirstOrDefault(Function(s) String.Equals(s.Id, id, StringComparison.Ordinal))
                If found Is Nothing Then Return False
                store.Shares.Remove(found)
                _sharedPaths = Nothing
                If Not Save() Then
                    store.Shares.Add(found)
                    Return False
                End If
            End SyncLock
            DiagnosticLogService.LogAlways("Shares", $"Share {id} revoked.")
            NoteChange()
            Return True
        End Function

        ''' <summary>Haelt alle Freigaben an oder gibt sie wieder frei.</summary>
        Public Shared Function SetPaused(pause As Boolean) As Boolean
            SyncLock Gate
                Dim store = Current()
                If store.Paused = pause Then Return True
                store.Paused = pause
                If Not Save() Then
                    store.Paused = Not pause
                    Return False
                End If
            End SyncLock
            DiagnosticLogService.LogAlways("Shares", If(pause, "All shares paused.", "Shares resumed."))
            NoteChange()
            Return True
        End Function

        ''' <summary>Zaehlt einen Aufruf der Seite einer Freigabe. Nur im Speicher: geschrieben wird
        ''' gebuendelt (<see cref="FlushViews"/>), hoechstens alle 30 Sekunden und beim Beenden.
        ''' Frueher schrieb jeder Aufruf die ganze Datei unter der Sperre der Ablage; ein oft
        ''' geoeffneter Link hielt so jede andere Anfrage an den Freigaben auf.</summary>
        Public Shared Sub NoteView(id As String)
            If String.IsNullOrEmpty(id) Then Return
            Dim stamp = AppLockService.Clock().ToString("o", CultureInfo.InvariantCulture)
            SyncLock ViewGate
                Dim pending As (Count As Integer, LastUtc As String) = (0, "")
                PendingViews.TryGetValue(id, pending)
                PendingViews(id) = (If(pending.Count < Integer.MaxValue, pending.Count + 1, pending.Count), stamp)
                If _viewTimer Is Nothing Then
                    _viewTimer = New Threading.Timer(Sub(state) FlushViews(), Nothing, ViewFlushDelayMs, Threading.Timeout.Infinite)
                End If
            End SyncLock
        End Sub

        ''' <summary>Schreibt die gezaehlten Aufrufe in die Ablage. Vom Zeitgeber, beim Beenden und
        ''' jederzeit zusaetzlich; ohne gezaehlte Aufrufe tut sie nichts.</summary>
        Public Shared Sub FlushViews()
            Dim taken As List(Of KeyValuePair(Of String, (Count As Integer, LastUtc As String)))
            SyncLock ViewGate
                _viewTimer?.Dispose()
                _viewTimer = Nothing
                If PendingViews.Count = 0 Then Return
                taken = PendingViews.ToList()
                PendingViews.Clear()
            End SyncLock
            Dim changed = False
            SyncLock Gate
                For Each item In taken
                    Dim found = Current().Shares.FirstOrDefault(Function(s) String.Equals(s.Id, item.Key, StringComparison.Ordinal))
                    If found Is Nothing Then Continue For
                    found.Views = CInt(Math.Min(CLng(found.Views) + item.Value.Count, Integer.MaxValue))
                    found.LastViewedUtc = item.Value.LastUtc
                    changed = True
                Next
                If changed Then Save()
            End SyncLock
        End Sub

        ''' <summary>FerrumPix hat diese Datei selbst geschrieben (Speichern einer Bearbeitung,
        ''' Stichwoerter, Ort): jede feste Liste, die sie enthaelt, nimmt den neuen Fingerabdruck,
        ''' die Freigabe zeigt also weiter dieses Bild. Billig, wenn die Datei in keiner Freigabe
        ''' steht (eine Menge im Speicher).</summary>
        Public Shared Sub NoteFileWritten(filePath As String)
            If String.IsNullOrEmpty(filePath) Then Return
            Dim changed = False
            SyncLock Gate
                If Not SharedPaths().Contains(filePath) Then Return
                Dim stamp = StampOf(filePath)
                For Each share In Current().Shares
                    For Each listed In share.Paths
                        If Not PathIdentity.Comparer.Equals(listed, filePath) Then Continue For
                        Dim before As String = Nothing
                        If share.PathStamps.TryGetValue(listed, before) AndAlso String.Equals(before, stamp, StringComparison.Ordinal) Then Continue For
                        share.PathStamps(listed) = stamp
                        changed = True
                    Next
                Next
                If changed Then Save()
            End SyncLock
            If changed Then NoteChange()
        End Sub

        ''' <summary>FerrumPix hat die Datei oder den Ordner geloescht (Papierkorb): der Pfad und
        ''' alles darunter fallen aus jeder festen Liste. Ein Bild, das spaeter wieder unter diesem
        ''' Pfad liegt, kommt so nicht zurueck in eine Freigabe. Ordnerfreigaben bleiben stehen
        ''' (ohne Ordner zeigen sie nichts).</summary>
        Public Shared Sub ForgetPath(path As String)
            If String.IsNullOrWhiteSpace(path) Then Return
            Dim changed = False
            SyncLock Gate
                For Each share In Current().Shares
                    Dim doomed = share.Paths.Where(Function(p) PathIdentity.IsAncestorOrSelf(path, p)).ToList()
                    If doomed.Count = 0 Then Continue For
                    For Each filePath In doomed
                        share.Paths.Remove(filePath)
                        share.PathStamps.Remove(filePath)
                    Next
                    changed = True
                Next
                If changed Then
                    _sharedPaths = Nothing
                    Save()
                End If
            End SyncLock
            If changed Then NoteChange()
        End Sub

        ''' <summary>Zieht die Pfade aller Freigaben nach, wenn FerrumPix eine Datei oder einen Ordner
        ''' verschoben oder umbenannt hat: der Pfad selbst und alles darunter. Aufgerufen von der
        ''' einen Stelle, ueber die jeder Verschiebeweg geht (LibraryService.MoveEverythingForPath).</summary>
        Public Shared Sub MovePath(oldPath As String, newPath As String)
            If String.IsNullOrWhiteSpace(oldPath) OrElse String.IsNullOrWhiteSpace(newPath) Then Return
            Dim changed = False
            SyncLock Gate
                For Each share In Current().Shares
                    Dim moved = MovedPath(share.Folder, oldPath, newPath)
                    If moved IsNot Nothing Then
                        share.Folder = moved
                        changed = True
                    End If
                    For i = 0 To share.Paths.Count - 1
                        Dim before = share.Paths(i)
                        moved = MovedPath(before, oldPath, newPath)
                        If moved IsNot Nothing Then
                            share.Paths(i) = moved
                            ' Verschoben hat FerrumPix selbst: der Fingerabdruck wandert mit und nimmt
                            ' den Stand am neuen Ort, falls das Verschieben ueber Laufwerke hinweg
                            ' (Kopieren, dann Loeschen) die Aenderungszeit nicht mitgenommen hat.
                            Dim stamp As String = Nothing
                            share.PathStamps.TryGetValue(before, stamp)
                            share.PathStamps.Remove(before)
                            Dim now = StampOf(moved)
                            If now.Length > 0 Then
                                share.PathStamps(moved) = now
                            ElseIf stamp IsNot Nothing Then
                                share.PathStamps(moved) = stamp
                            End If
                            changed = True
                        End If
                    Next
                Next
                If changed Then
                    _sharedPaths = Nothing
                    Save()
                End If
            End SyncLock
            If changed Then NoteChange()
        End Sub

        ''' <summary>Der neue Ort eines Pfads, wenn er selbst oder ein Ordner darueber verschoben
        ''' wurde; sonst Nothing.</summary>
        Friend Shared Function MovedPath(value As String, oldPath As String, newPath As String) As String
            If String.IsNullOrEmpty(value) OrElse Not PathIdentity.IsAncestorOrSelf(oldPath, value) Then Return Nothing
            Dim rest = value.Substring(Math.Min(value.Length, IO.Path.TrimEndingDirectorySeparator(oldPath).Length))
            Return IO.Path.TrimEndingDirectorySeparator(newPath) & rest
        End Function

    End Class

End Namespace
