Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Threading

Namespace Services

    ''' <summary>Das Protokoll der Sicherheitsereignisse: Anmelden, Sperren, Kennwoerter, Benutzer,
    ''' Regeln, Freigaben, Loeschen, Umbenennen, Hoch- und Herunterladen.
    '''
    ''' <para>EINE ZEILE JE EREIGNIS, leicht auszuwerten:
    ''' <c>FerrumPix security: 2026-11-27T15:04:05Z event=signin.failed user=- from=192.168.1.20</c>.
    ''' Die Zeit steht immer in UTC. <c>user</c> ist der Name des Benutzers, der handelt, sonst "-";
    ''' <c>from</c> die Adresse, die auch die Bremse zaehlt (hinter dem vertrauten Proxy die
    ''' weitergereichte, BrowserBridge.LogAddress), am Rechner ohne Browser "local". Danach knappe
    ''' Felder <c>schluessel=wert</c>; ein Wert mit Leerzeichen, Anfuehrungszeichen oder
    ''' Gleichheitszeichen steht in Anfuehrungszeichen.</para>
    '''
    ''' <para>NIE IM PROTOKOLL: Kennwoerter, Tokens, Codes, Cookies, Inhalte, vollstaendige
    ''' Kennungen und die Adressen von Empfaengern einer Nachricht. Eine Freigabe erscheint mit den
    ''' ersten acht Zeichen ihrer Kennung (nicht dem Token des Links). Ein gescheitertes Anmelden
    ''' nennt keinen Namen: falscher Name und falsches Kennwort sehen gleich aus, und ein Kennwort,
    ''' das versehentlich ins Namensfeld geriet, landet so nicht im Protokoll.</para>
    '''
    ''' <para>WOHIN: mit laufender Bruecke auf die Standardausgabe (im Behaelter docker logs), immer
    ''' zusaetzlich mit dem Marker "Security" ins Diagnoseprotokoll im Datenordner (das schreibt
    ''' nur, wenn das Diagnoseprotokoll eingeschaltet ist). Am Rechner ohne Browser gibt es die
    ''' meisten Ereignisse nicht; Sperren und Entsperren gehen dort nur in die Datei, die
    ''' Standardausgabe einer Fensteranwendung liest niemand.</para>
    '''
    ''' <para>FELDER SIND ENTSCHAERFT (<see cref="Field"/>): Steuerzeichen, Zeilenumbrueche und
    ''' unsichtbare Formatzeichen in Namen und Dateinamen werden zu "?", sonst liesse sich mit einem
    ''' Benutzernamen eine erfundene Zeile ins Protokoll schreiben.</para>
    '''
    ''' <para>KEINE FLUT: keine Zeile je Kachel, Vorschau oder Anfrage. Eine geoeffnete Freigabe
    ''' steht je Freigabe und Absender hoechstens einmal je Stunde da, eine greifende Bremse je
    ''' Absender hoechstens einmal je Minute, ein Hochladen als eine Zeile je Stapel
    ''' (<see cref="NoteUpload"/>).</para></summary>
    Public NotInheritable Class SecurityLog

        ''' <summary>Der Anfang jeder Zeile auf der Standardausgabe; danach filtert
        ''' <c>docker logs ferrumpix | grep "FerrumPix security"</c>.</summary>
        Public Const LinePrefix As String = "FerrumPix security: "

        ''' <summary>Der Marker im Diagnoseprotokoll.</summary>
        Private Const LogArea As String = "Security"

        ''' <summary>Ein Feld ohne Wert.</summary>
        Friend Const NoValue As String = "-"

        ''' <summary>Die Absenderangabe fuer eine Handlung am Rechner ohne Browser.</summary>
        Friend Const LocalSender As String = "local"

        ''' <summary>Hoechstlaenge eines Werts in Zeichen; laengere enden mit drei Punkten.</summary>
        Private Const MaxValueLength As Integer = 160

        ''' <summary>So viele Pfade nennt eine Zeile zum Loeschen hoechstens.</summary>
        Private Const MaxListedPaths As Integer = 10

        ''' <summary>Wie lange ein Stapel beim Hochladen ruhen muss, bis seine Zeile geschrieben wird.
        ''' Die Seite schickt jede Datei einzeln; ein Ende des Stapels meldet sie nicht.</summary>
        Friend Shared UploadQuiet As TimeSpan = TimeSpan.FromSeconds(10)

        Private Shared ReadOnly WriteGate As New Object()

        ''' <summary>Wann ein Ereignis mit Schluessel zuletzt geschrieben wurde (<see cref="FirstWithin"/>).</summary>
        Private Shared ReadOnly LastWritten As New Dictionary(Of String, DateTime)(StringComparer.Ordinal)
        Private Const MaxRemembered As Integer = 4096

        ''' <summary>Ein Stapel beim Hochladen, bis er ruht.</summary>
        Private NotInheritable Class UploadTally
            Public User As String
            Public From As String
            Public Folder As String
            Public Via As String
            Public Count As Integer
            Public LastUtc As DateTime
        End Class

        Private Shared ReadOnly Uploads As New Dictionary(Of String, UploadTally)(StringComparer.Ordinal)
        Private Shared _uploadTimer As System.Threading.Timer

        Private Sub New()
        End Sub

        ' --- Schreiben ------------------------------------------------------------------------------

        ''' <summary>Schreibt die Standardausgabe mit? Nur mit laufender Bruecke (auch im Pruefstand,
        ''' der sie ohne den Startschalter betreibt).</summary>
        Private Shared ReadOnly Property WritesToConsole As Boolean
            Get
                Return BrowserBridge.IsActive OrElse BrowserBridge.RequiresPassword
            End Get
        End Property

        ''' <summary>Schreibt ein Ereignis. <paramref name="fields"/> sind Paare aus Schluessel und
        ''' Wert; ein Schluessel ist ein fester Name aus dem Quelltext, nur der Wert wird entschaerft.</summary>
        Friend Shared Sub Write(eventName As String, user As String, from As String, ParamArray fields As String())
            Dim text As New StringBuilder(96)
            text.Append("event=").Append(eventName)
            text.Append(" user=").Append(Field(user))
            text.Append(" from=").Append(Field(from))
            If fields IsNot Nothing Then
                For i = 0 To fields.Length - 2 Step 2
                    text.Append(" "c).Append(fields(i)).Append("="c).Append(Field(fields(i + 1)))
                Next
            End If
            Dim body = text.ToString()
            If WritesToConsole Then
                Dim stamp = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                SyncLock WriteGate
                    Try
                        Console.Out.WriteLine(LinePrefix & stamp & " " & body)
                        Console.Out.Flush()
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException
                        ' Keine Standardausgabe (Pipe zu); die Datei bekommt die Zeile trotzdem.
                    End Try
                End SyncLock
            End If
            DiagnosticLogService.LogAlways(LogArea, body)
        End Sub

        ''' <summary>Ein Wert fuer die Zeile: leer wird "-", Steuer-, Zeilen- und Formatzeichen werden
        ''' "?", hoechstens <see cref="MaxValueLength"/> Zeichen. Enthaelt er Leerraum,
        ''' Anfuehrungszeichen, Gleichheitszeichen oder Rueckstrich, steht er in Anfuehrungszeichen,
        ''' Rueckstrich und Anfuehrungszeichen darin mit Rueckstrich davor.</summary>
        Friend Shared Function Field(value As String) As String
            If String.IsNullOrEmpty(value) Then Return NoValue
            Dim clean As New StringBuilder(Math.Min(value.Length, MaxValueLength) + 3)
            Dim quote = False
            For Each c In value
                If clean.Length >= MaxValueLength Then
                    clean.Append("...")
                    Exit For
                End If
                Select Case Char.GetUnicodeCategory(c)
                    Case UnicodeCategory.Control, UnicodeCategory.Format, UnicodeCategory.LineSeparator,
                         UnicodeCategory.ParagraphSeparator, UnicodeCategory.PrivateUse
                        clean.Append("?"c)
                        Continue For
                End Select
                If Char.IsWhiteSpace(c) OrElse c = """"c OrElse c = "="c OrElse c = "\"c Then quote = True
                clean.Append(c)
            Next
            If Not quote Then Return clean.ToString()
            Return """" & clean.ToString().Replace("\", "\\").Replace("""", "\""") & """"
        End Function

        ''' <summary>True, wenn zu <paramref name="key"/> innerhalb von <paramref name="interval"/>
        ''' noch nichts geschrieben wurde; merkt sich dann den Zeitpunkt. Die Uhr ist die der Sperre
        ''' (AppLockService.Clock), damit der Pruefstand die Stunde vorstellen kann.</summary>
        Private Shared Function FirstWithin(key As String, interval As TimeSpan) As Boolean
            Dim now = AppLockService.Clock()
            SyncLock LastWritten
                Dim last As DateTime
                If LastWritten.TryGetValue(key, last) AndAlso now - last < interval AndAlso now >= last Then Return False
                If LastWritten.Count >= MaxRemembered Then
                    For Each stale In LastWritten.Where(Function(p) now - p.Value > TimeSpan.FromHours(1)).Select(Function(p) p.Key).ToList()
                        LastWritten.Remove(stale)
                    Next
                    If LastWritten.Count >= MaxRemembered Then LastWritten.Clear()
                End If
                LastWritten(key) = now
                Return True
            End SyncLock
        End Function

        ''' <summary>Fuer den Pruefstand: vergisst, was schon geschrieben wurde, und schreibt offene
        ''' Stapel nicht mehr.</summary>
        Friend Shared Sub ResetForTests()
            SyncLock LastWritten
                LastWritten.Clear()
            End SyncLock
            SyncLock Uploads
                Uploads.Clear()
                _uploadTimer?.Dispose()
                _uploadTimer = Nothing
            End SyncLock
        End Sub

        ' --- Namen und Pfade -----------------------------------------------------------------------

        ''' <summary>Der Name eines Benutzers nach Kennung, sonst "-".</summary>
        Friend Shared Function UserNameOf(userId As String) As String
            If String.IsNullOrEmpty(userId) Then Return NoValue
            Dim name = UserAccountService.Find(userId)?.Name
            Return If(String.IsNullOrEmpty(name), NoValue, name)
        End Function

        ''' <summary>Die ersten acht Zeichen der Kennung einer Freigabe. Nie das Token.</summary>
        Friend Shared Function ShortShareId(shareId As String) As String
            If String.IsNullOrEmpty(shareId) Then Return NoValue
            Return If(shareId.Length > 8, shareId.Substring(0, 8), shareId)
        End Function

        ''' <summary>Ein Pfad fuer das Protokoll: relativ zur Wurzel, unter der er liegt, mit dem Namen
        ''' der Wurzel vorn und Schraegstrichen ("photos/2024/bild.jpg"); liegt er unter keiner, nur
        ''' der Dateiname. Nie der volle Pfad des Rechners.</summary>
        Friend Shared Function ShownPath(path As String, roots As IEnumerable(Of String)) As String
            If String.IsNullOrEmpty(path) Then Return NoValue
            Dim best As String = Nothing
            For Each root In If(roots, Enumerable.Empty(Of String)())
                If String.IsNullOrEmpty(root) OrElse Not PathIdentity.IsAncestorOrSelf(root, path) Then Continue For
                If best Is Nothing OrElse root.Length > best.Length Then best = root
            Next
            If best Is Nothing Then
                Dim leaf = IO.Path.GetFileName(IO.Path.TrimEndingDirectorySeparator(path))
                Return If(leaf.Length = 0, NoValue, leaf)
            End If
            Dim rootName = IO.Path.GetFileName(IO.Path.TrimEndingDirectorySeparator(best))
            Dim relative = IO.Path.GetRelativePath(best, path).Replace(IO.Path.DirectorySeparatorChar, "/"c)
            If relative = "." Then Return If(rootName.Length = 0, "/", rootName)
            Return If(rootName.Length = 0, relative, rootName & "/" & relative)
        End Function

        ''' <summary>Wer an der vollen Oberflaeche handelt (Einstellungen, Benutzerverwaltung,
        ''' Sperren in der Galerie): mit laufender Bruecke der Benutzer und die Adresse des
        ''' verbundenen Browsers, ohne Browser der Eigentuemer am Rechner.</summary>
        Friend Shared Function DesktopActor() As (User As String, From As String)
            If WritesToConsole Then Return BrowserBridge.ConnectedActor()
            Return (UserNameOf(AppLockService.OwnerUserId), LocalSender)
        End Function

        ' --- Anmelden und Sperren ------------------------------------------------------------------

        ''' <summary>Gelungene Anmeldung. <paramref name="via"/>: "page" (Anmeldefenster) oder
        ''' "basic" (Werkzeug; je Benutzer und Absender hoechstens einmal je Stunde, ein Werkzeug
        ''' meldet sich bei jeder Anfrage an).</summary>
        Friend Shared Sub SignedIn(userId As String, from As String, via As String)
            If via = "basic" AndAlso Not FirstWithin("signin.basic|" & userId & "|" & from, TimeSpan.FromHours(1)) Then Return
            Write("signin.success", UserNameOf(userId), from, "via", via)
        End Sub

        ''' <summary>Gescheiterte Anmeldung, ohne Namen: falscher Name und falsches Kennwort sehen
        ''' gleich aus.</summary>
        Friend Shared Sub SignInFailed(from As String, via As String)
            Write("signin.failed", NoValue, from, "via", via)
        End Sub

        ''' <summary>Die Bremse greift (429). <paramref name="what"/> sagt, wo: signin, setup, reset,
        ''' forgot, share. Je Ort und Absender hoechstens einmal je Minute.</summary>
        Friend Shared Sub Throttled(what As String, from As String, retryAfter As TimeSpan, Optional shareId As String = Nothing)
            If Not FirstWithin("throttled|" & what & "|" & from & "|" & shareId, TimeSpan.FromMinutes(1)) Then Return
            Dim seconds = CLng(Math.Ceiling(Math.Max(0, retryAfter.TotalSeconds))).ToString(CultureInfo.InvariantCulture)
            If shareId Is Nothing Then
                Write("throttled", NoValue, from, "at", what, "retry", seconds)
            Else
                Write("throttled", NoValue, from, "at", what, "share", ShortShareId(shareId), "retry", seconds)
            End If
        End Sub

        ''' <summary>Abmelden ueber /logout.</summary>
        Friend Shared Sub SignedOut(userId As String, from As String)
            Write("signout", UserNameOf(userId), from)
        End Sub

        ''' <summary>Die Anwendung ist gesperrt. <paramref name="reason"/>: manual, idle, start.</summary>
        Friend Shared Sub Locked(reason As String, user As String, from As String)
            Write("app.locked", user, from, "reason", reason)
        End Sub

        ''' <summary>Die Anwendung ist entsperrt. <paramref name="via"/>: signin (Anmeldung eines
        ''' Administrators im Browser), lockscreen (Anmeldefenster am Rechner), settings (Schutz
        ''' abgeschaltet).</summary>
        Friend Shared Sub Unlocked(via As String, user As String, from As String)
            Write("app.unlocked", user, from, "via", via)
        End Sub

        ''' <summary>Die Ersteinrichtung im Browser hat Name und Kennwort festgelegt.</summary>
        Friend Shared Sub SetupDone(userName As String, from As String)
            Write("setup.done", userName, from)
        End Sub

        ''' <summary>Die erste Anmeldung im Browser hat den Namen des Eigentuemers festgelegt.</summary>
        Friend Shared Sub UserNameClaimed(userName As String, from As String)
            Write("user.claimed", userName, from)
        End Sub

        ''' <summary>Der Notweg fuer das Kennwort des Administrators beim Start. <paramref name="applied"/>:
        ''' geloescht, oder die Angabe stand noch und wirkte nicht noch einmal.</summary>
        Friend Shared Sub AdminPasswordReset(applied As Boolean)
            Write("admin.reset", UserNameOf(AppLockService.OwnerUserId), "start", "result", If(applied, "applied", "ignored"))
        End Sub

        ' --- Kennwoerter --------------------------------------------------------------------------

        ''' <summary>"Kennwort vergessen" angefordert: nur der Absender, nie wer gemeint war, und
        ''' nicht, ob es das Konto gibt.</summary>
        Friend Shared Sub ResetRequested(from As String)
            Write("password.reset.requested", NoValue, from)
        End Sub

        ''' <summary>Die Nachricht mit dem Verweis ging hinaus (oder nicht), ohne Empfaengeradresse.</summary>
        Friend Shared Sub ResetMailed(userId As String, sent As Boolean)
            Write(If(sent, "password.reset.mailed", "password.reset.mailfailed"), UserNameOf(userId), NoValue)
        End Sub

        ''' <summary>Ein neues Kennwort ueber den Verweis aus der Nachricht.</summary>
        Friend Shared Sub ResetByLink(userId As String, from As String)
            Write("password.reset.done", UserNameOf(userId), from)
        End Sub

        ''' <summary>Ein Kennwort geaendert, an der vollen Oberflaeche. <paramref name="via"/>:
        ''' settings (das eigene des Eigentuemers) oder admin (Benutzerverwaltung).</summary>
        Friend Shared Sub PasswordChanged(targetUserId As String, via As String)
            Dim actor = DesktopActor()
            Write("password.changed", actor.User, actor.From, "target", UserNameOf(targetUserId), "via", via)
        End Sub

        ''' <summary>Der Schutz mit Kennwort ist abgeschaltet (nur am Rechner moeglich).</summary>
        Friend Shared Sub PasswordRemoved()
            Dim actor = DesktopActor()
            Write("password.removed", actor.User, actor.From)
        End Sub

        ' --- Benutzer und Regeln ------------------------------------------------------------------

        ''' <summary>Eine Aenderung an einem Benutzer in der Verwaltung: user.created, user.changed,
        ''' user.disabled, user.enabled, user.admin, user.deleted. <paramref name="targetName"/> ist
        ''' der Name des betroffenen Benutzers; beim Loeschen ist er schon weg und kommt vom Aufrufer.</summary>
        Friend Shared Sub UserEvent(eventName As String, targetName As String, ParamArray fields As String())
            Dim actor = DesktopActor()
            Dim all As New List(Of String) From {"target", If(String.IsNullOrEmpty(targetName), NoValue, targetName)}
            If fields IsNot Nothing Then all.AddRange(fields)
            Write(eventName, actor.User, actor.From, all.ToArray())
        End Sub

        ''' <summary>Ein Benutzer umbenannt: <c>user.renamed</c> mit altem und neuem Namen. Handelnd
        ''' ist, wer an der vollen Oberflaeche sitzt, oder der Benutzer selbst auf der mobilen Seite
        ''' (<paramref name="user"/>, <paramref name="from"/>).</summary>
        Friend Shared Sub UserRenamed(oldName As String, newName As String, Optional user As String = Nothing, Optional from As String = Nothing)
            If user Is Nothing Then
                Dim actor = DesktopActor()
                user = actor.User
                from = actor.From
            End If
            Write("user.renamed", user, If(from, NoValue), "old", If(String.IsNullOrEmpty(oldName), NoValue, oldName),
                  "new", If(String.IsNullOrEmpty(newName), NoValue, newName))
        End Sub

        ''' <summary>Eine Ordnerregel gesetzt oder entfernt (<paramref name="level"/> "removed").</summary>
        Friend Shared Sub RuleChanged(targetUserId As String, folder As String, level As String)
            Dim actor = DesktopActor()
            Write("rule.changed", actor.User, actor.From, "target", UserNameOf(targetUserId),
                  "folder", ShownPath(folder, BrowserBridge.LogRoots()), "level", level)
        End Sub

        ' --- Freigaben ------------------------------------------------------------------------------

        ''' <summary>Eine Aenderung an einer Freigabe: share.created, share.changed, share.relinked,
        ''' share.revoked.</summary>
        Friend Shared Sub ShareEvent(eventName As String, user As String, from As String, shareId As String, ParamArray fields As String())
            Dim all As New List(Of String) From {"share", ShortShareId(shareId)}
            If fields IsNot Nothing Then all.AddRange(fields)
            Write(eventName, user, from, all.ToArray())
        End Sub

        ''' <summary>Alle Freigaben angehalten oder wieder frei.</summary>
        Friend Shared Sub SharesPaused(paused As Boolean, user As String, from As String)
            Write(If(paused, "shares.paused", "shares.resumed"), user, from)
        End Sub

        ''' <summary>Die Seite einer Freigabe wurde geoeffnet. Je Freigabe und Absender hoechstens
        ''' einmal je Stunde.</summary>
        Friend Shared Sub ShareOpened(shareId As String, from As String)
            If Not FirstWithin("share.opened|" & shareId & "|" & from, TimeSpan.FromHours(1)) Then Return
            Write("share.opened", NoValue, from, "share", ShortShareId(shareId))
        End Sub

        ''' <summary>Ein falsches Kennwort an einer Freigabe.</summary>
        Friend Shared Sub ShareWrongPassword(shareId As String, from As String)
            Write("share.password.failed", NoValue, from, "share", ShortShareId(shareId))
        End Sub

        ' --- Dateien -------------------------------------------------------------------------------

        ''' <summary>Ein ZIP geht hinaus: aus einer Freigabe (<paramref name="shareId"/>) oder von der
        ''' mobilen Seite, mit der Zahl der Dateien darin.</summary>
        Friend Shared Sub ZipDownload(user As String, from As String, files As Integer, Optional shareId As String = Nothing)
            Dim count = files.ToString(CultureInfo.InvariantCulture)
            If shareId Is Nothing Then
                Write("download.zip", user, from, "files", count)
            Else
                Write("download.zip", user, from, "share", ShortShareId(shareId), "files", count)
            End If
        End Sub

        ''' <summary>Loeschen ueber die mobile Seite: Zahl, ob in den Papierkorb, und die ersten Pfade
        ''' relativ zur Wurzel.</summary>
        Friend Shared Sub FilesDeleted(user As String, from As String, paths As IReadOnlyList(Of String), roots As IEnumerable(Of String), trash As Boolean)
            Dim rootList = If(roots, Enumerable.Empty(Of String)()).ToList()
            Dim shown = paths.Take(MaxListedPaths).Select(Function(p) ShownPath(p, rootList)).ToList()
            If paths.Count > MaxListedPaths Then shown.Add("...")
            Write("file.deleted", user, from, "count", paths.Count.ToString(CultureInfo.InvariantCulture),
                  "trash", If(trash, "yes", "no"), "paths", String.Join("|", shown))
        End Sub

        ''' <summary>Umbenennen ueber die mobile Seite: alter Pfad relativ zur Wurzel, neuer Name.</summary>
        Friend Shared Sub FileRenamed(user As String, from As String, oldPath As String, newPath As String, roots As IEnumerable(Of String))
            Write("file.renamed", user, from, "path", ShownPath(oldPath, roots), "name", IO.Path.GetFileName(newPath))
        End Sub

        ''' <summary>Eine angekommene Datei beim Hochladen. Geschrieben wird je Stapel EINE Zeile mit
        ''' der Zahl und dem Zielordner, sobald der Stapel <see cref="UploadQuiet"/> ruht.
        ''' <paramref name="batch"/> ist die Kennung des Ablegens; ohne sie bilden Benutzer, Absender
        ''' und Ordner den Stapel.</summary>
        Friend Shared Sub NoteUpload(batch As String, user As String, from As String, folderShown As String, via As String)
            Dim key = If(batch, "") & "|" & user & "|" & from & "|" & folderShown & "|" & via
            SyncLock Uploads
                Dim tally As UploadTally = Nothing
                If Not Uploads.TryGetValue(key, tally) Then
                    tally = New UploadTally With {.User = user, .From = from, .Folder = folderShown, .Via = via}
                    Uploads(key) = tally
                End If
                tally.Count += 1
                tally.LastUtc = DateTime.UtcNow
                If _uploadTimer Is Nothing Then
                    _uploadTimer = New System.Threading.Timer(Sub(state) FlushUploads(False), Nothing, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2))
                End If
            End SyncLock
        End Sub

        ''' <summary>Schreibt die Stapel, die ruhen, oder mit <paramref name="all"/> jeden (beim
        ''' Anhalten der Bruecke und im Pruefstand).</summary>
        Friend Shared Sub FlushUploads(all As Boolean)
            Dim ready As List(Of UploadTally)
            Dim now = DateTime.UtcNow
            SyncLock Uploads
                Dim keys = Uploads.Where(Function(p) all OrElse now - p.Value.LastUtc >= UploadQuiet).Select(Function(p) p.Key).ToList()
                ready = keys.Select(Function(k) Uploads(k)).ToList()
                For Each key In keys
                    Uploads.Remove(key)
                Next
                If Uploads.Count = 0 AndAlso _uploadTimer IsNot Nothing Then
                    _uploadTimer.Dispose()
                    _uploadTimer = Nothing
                End If
            End SyncLock
            For Each tally In ready
                Write("upload", tally.User, tally.From, "files", tally.Count.ToString(CultureInfo.InvariantCulture),
                      "folder", tally.Folder, "via", tally.Via)
            Next
        End Sub

        ''' <summary>Eine Anfrage ueber einen Weg, der nicht zugesagt ist (BrowserBridge.LogRefusal),
        ''' je Absender einmal.</summary>
        Friend Shared Sub Refused(from As String, host As String, proto As String)
            Write("request.refused", NoValue, from, "host", host, "proto", proto)
        End Sub

    End Class

End Namespace
