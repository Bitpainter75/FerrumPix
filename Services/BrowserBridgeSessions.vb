Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading.Tasks
Imports Avalonia
Imports Avalonia.Media
Imports Avalonia.Platform
Imports Avalonia.Threading

Namespace Services

    ''' <summary>
    ''' Die Anmeldung im Browser: eine SITZUNG je Browser, aufgebaut ueber das Anmeldefenster der
    ''' Seite, mit demselben Kennwort wie das Anmeldefenster der Anwendung (AppLockService).
    '''
    ''' <para>EINE VERBINDUNG OHNE SITZUNG BEKOMMT NICHTS VON DER ANWENDUNG: keine Bilder, keine
    ''' Eingaben, kein Hochladen und Herunterladen. Sie sieht nur das Anmeldefenster als eigene
    ''' Seite (login.html), mit allem darin ausser dem einen Bild (Welcome.jpg, unter
    ''' <see cref="LoginImageRoute"/>). Die Anwendung hat nur EINE Oberflaeche; zeigte sie ihr eigenes
    ''' Anmeldefenster, saehe es jeder Verbundene, und nach der Anmeldung saehen alle die
    ''' Anwendung.</para>
    '''
    ''' <para>DAS COOKIE: 32 zufaellige Bytes, HttpOnly, SameSite=Strict, Path=/, Secure, wenn die
    ''' Seite ueber HTTPS kam (hinter dem Proxy). Der Server merkt sich nur den SHA-256 des Werts.
    ''' Neu laden, ein neuer Tab und eine abgerissene Verbindung brauchen keine neue Anmeldung,
    ''' solange die Sitzung gilt.</para>
    '''
    ''' <para>ABLAUF: nach der eingestellten Sperrzeit ohne Nutzung, ohne Sperrzeit nach 30 Tagen
    ''' ohne Nutzung. Das entscheidet allein der Server (LastUsedUtc); das Cookie lebt im Browser
    ''' immer 30 Tage, auch mit kurzer Sperrzeit. Mit einem Max-Age von der Laenge der Sperrzeit
    ''' liefe es sonst ab, waehrend ueber den WebSocket gearbeitet wird (der verlaengert nur beim
    ''' Server), und Hochladen, Herunterladen und Neuladen stuenden ploetzlich ohne Anmeldung da.
    ''' Sperren (von Hand, nach Inaktivitaet) beendet ALLE Sitzungen; "Abmelden" (POST /logout)
    ''' nur die eigene; ein neues Kennwort und das Abschalten des Kennworts alle ausser der des
    ''' verbundenen Browsers (<see cref="EndOtherSessions"/>).</para>
    '''
    ''' <para>DEM BROWSER WIRD NIE WWW-Authenticate GESCHICKT, damit sein eigener Dialog nicht mehr
    ''' erscheint. Werkzeuge (curl, der Probelauf des Abbilds) duerfen weiter selbst
    ''' "Authorization: Basic" mitschicken; das gilt dann fuer diese eine Anfrage und legt keine
    ''' Sitzung an.</para>
    '''
    ''' <para>DAS KENNWORT IST PFLICHT, in jeder Betriebsart der Bruecke. Ist noch keines
    ''' gespeichert, zeigt dieselbe Seite statt der Anmeldung die EINRICHTUNG (POST /setup): neues
    ''' Kennwort zweimal, danach ist der Browser angemeldet. Wer zuerst kommt, legt fest; ohne
    ''' Einmalcode und ohne Beschraenkung aufs Heimnetz, so entschieden. Bis dahin ist ausser der
    ''' Seite, ihrem Bild, den Symbolen und /health nichts erreichbar: kein WebSocket, kein
    ''' Hochladen und Herunterladen, keine Schnittstelle, keine Basic-Anmeldung (es gibt kein
    ''' Kennwort, gegen das sie gelingen koennte). Die Einrichtung gelingt genau einmal
    ''' (AppLockService.TrySetInitialPassword).</para>
    '''
    ''' <para>BENUTZER. Jede Sitzung traegt eine Benutzerkennung: den Eigentuemer
    ''' (AppLockService.OwnerUserId), den die Einrichtung festlegt, oder einen weiteren Benutzer
    ''' (UserAccountService). Wer die volle Oberflaeche betreten darf, steht an einer Stelle
    ''' (<see cref="CanUseDesktop"/>: nur Administratoren), und die mobile Schnittstelle holt ihren
    ''' Benutzer an einer Stelle (<see cref="SignedInUser"/>, darueber MobileContext). Ein
    ''' gesperrter oder geloeschter Benutzer hat keine gueltige Sitzung mehr, auch wenn sein Cookie
    ''' noch gilt.</para>
    '''
    ''' <para>KENNWORT VERGESSEN (POST /forgot, GET und POST /reset, PasswordResetService): ein
    ''' Verweis per E-Mail, wenn ein Mailserver eingerichtet ist; die Antwort ist fuer jedes Konto
    ''' dieselbe.</para>
    '''
    ''' <para>GEGEN FREMDE SEITEN: SameSite=Strict, dazu prueft POST /login und /logout die Herkunft
    ''' (Origin gegen Host) und verlangt JSON, was jeder fremden Seite eine Vorabfrage des Browsers
    ''' aufzwingt, die hier nie beantwortet wird. Gegen Raten zaehlt AppLockService Fehlversuche je
    ''' Browser und je Absenderadresse (<see cref="SenderKey"/>), einen gemeinsamen Zaehler gibt es
    ''' nicht.</para>
    ''' </summary>
    Partial Public NotInheritable Class BrowserBridge

        Friend Const SessionCookieName As String = "ferrumpix_session"

        ''' <summary>Kennung eines Browsers VOR der Anmeldung, nur fuer die Zaehlung seiner
        ''' Fehlversuche. Gibt keinen Zugang.</summary>
        Friend Const LoginCookieName As String = "ferrumpix_login"
        Friend Const LoginRoute As String = "/login"
        Friend Const SetupRoute As String = "/setup"
        Friend Const LogoutRoute As String = "/logout"
        Friend Const ForgotRoute As String = "/forgot"
        Friend Const ResetRoute As String = "/reset"
        Friend Const LoginImageRoute As String = "/login/welcome.jpg"

        Private Const LoginPageAsset As String = "avares://FerrumPix/Assets/BrowserBridge/login.html"
        Private Const LoginImageAsset As String = "avares://FerrumPix/Assets/Welcome.jpg"

        ''' <summary>Ohne eingestellte Sperrzeit endet eine Sitzung nach so langer Zeit ohne Nutzung.</summary>
        Friend Shared ReadOnly DefaultSessionLifetime As TimeSpan = TimeSpan.FromDays(30)

        ''' <summary>Hoechstens so viele Sitzungen je Benutzer; mit einer weiteren faellt seine am
        ''' laengsten unbenutzte heraus, nie die eines anderen.</summary>
        Friend Const MaxSessionsPerUser As Integer = 20

        ''' <summary>Hoechstens so viele Sitzungen zugleich, ueber alle Benutzer. Wird es voll, faellt
        ''' die am laengsten unbenutzte Sitzung des Benutzers mit den meisten Sitzungen heraus: wer
        ''' viele anlegt, verdraengt zuerst seine eigenen. Kein Const, damit der Pruefstand die
        ''' Grenze senken kann.</summary>
        Friend Shared MaxSessions As Integer = 512

        Private Const MaxLoginBodyBytes As Integer = 4096

        Private NotInheritable Class BrowserSession
            Public LastUsedUtc As DateTime
            Public Sender As String
            ''' <summary>Die Adresse bei der Anmeldung, wie sie im Protokoll steht (<see cref="LogAddress"/>).</summary>
            Public Address As String
            ''' <summary>Wem die Sitzung gehoert: AppLockService.OwnerUserId oder die Kennung eines
            ''' weiteren Benutzers (UserAccountService).</summary>
            Public UserId As String
        End Class

        ''' <summary>Die Sitzungen, nach dem SHA-256 des Cookie-Werts. Der Wert selbst steht nur im
        ''' Browser.</summary>
        Private Shared ReadOnly Sessions As New Dictionary(Of String, BrowserSession)(StringComparer.Ordinal)

        ''' <summary>Die Sitzung, mit der der verbundene WebSocket angemeldet ist, sonst leer.</summary>
        Private Shared _clientSessionKey As String = ""

        ''' <summary>Der zuletzt gelungene Basic-Kopf eines Werkzeugs (als Hash samt Marke des
        ''' Kennworts) und bis wann er ohne neue Pruefung gilt.</summary>
        Private Shared _basicHeaderKey As String = ""
        Private Shared _basicHeaderUser As String
        Private Shared _basicHeaderUntilUtc As DateTime

        Private Shared _loginPageText As String
        Private Shared _loginImage As Byte()
        Private Shared _lockHandler As EventHandler

        ''' <summary>Laeuft die Bruecke? Dann ist ein Kennwort Pflicht, in jeder Betriebsart, und die
        ''' Einstellungen lassen es nicht abschalten (aendern bleibt moeglich).</summary>
        Public Shared ReadOnly Property RequiresPassword As Boolean
            Get
                Return _listener IsNot Nothing
            End Get
        End Property

        ''' <summary>Ob ein Benutzer die volle Oberflaeche (<c>/</c>, den WebSocket, Hoch- und
        ''' Herunterladen der Desktop-Seite) betreten darf. Die eine Stelle fuer diese Entscheidung:
        ''' nur Administratoren, also der Eigentuemer und weitere Benutzer mit dieser Rolle. Die
        ''' volle Oberflaeche zeigt alles und fuehrt zu den Einstellungen; Rechte je Ordner gibt es
        ''' dort nicht.</summary>
        Friend Shared Function CanUseDesktop(userId As String) As Boolean
            Return Not String.IsNullOrEmpty(userId) AndAlso UserAccountService.IsAdmin(userId)
        End Function

        ''' <summary>Angemeldet und berechtigt fuer die volle Oberflaeche? Fuer die Wege der
        ''' Desktop-Seite (Hochladen, Herunterladen samt Ordner als ZIP).</summary>
        Private Shared Function IsSignedInForDesktop(context As HttpListenerContext) As Boolean
            Return CanUseDesktop(SignedInUser(context))
        End Function

        ''' <summary>Wie lange eine Sitzung ohne Nutzung gilt.</summary>
        Friend Shared Function SessionLifetime() As TimeSpan
            Dim minutes = AppLockService.IdleMinutes
            If minutes > 0 Then Return TimeSpan.FromMinutes(minutes)
            Return DefaultSessionLifetime
        End Function

        ''' <summary>Meldet die Bruecke bei der Sperre an: jede Sperre beendet alle Sitzungen.</summary>
        Private Shared Sub InstallSessionHooks()
            If _lockHandler IsNot Nothing Then Return
            _lockHandler = Sub(s, e)
                               If AppLockService.IsLocked Then EndAllSessions()
                           End Sub
            AddHandler AppLockService.LockStateChanged, _lockHandler
        End Sub

        Private Shared Sub RemoveSessionHooks()
            If _lockHandler Is Nothing Then Return
            RemoveHandler AppLockService.LockStateChanged, _lockHandler
            _lockHandler = Nothing
        End Sub

        Private Shared Function SessionKey(value As String) As String
            Return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value)))
        End Function

        Private Shared Function ReadSessionCookie(request As HttpListenerRequest) As String
            Return ReadHexCookie(request, SessionCookieName)
        End Function

        Private Shared Function ReadHexCookie(request As HttpListenerRequest, name As String) As String
            Dim cookie = request.Cookies(name)
            Dim value = cookie?.Value
            ' 32 Bytes als Hex; alles andere ist kein Wert von hier.
            If String.IsNullOrEmpty(value) OrElse value.Length <> 64 Then Return Nothing
            For Each c In value
                If Not Uri.IsHexDigit(c) Then Return Nothing
            Next
            Return value
        End Function

        ''' <summary>Der Schluessel einer gueltigen Sitzung dieser Anfrage, sonst Nothing. Eine
        ''' abgelaufene faellt dabei heraus; eine gueltige zaehlt als Nutzung.</summary>
        Private Shared Function ValidSessionKey(request As HttpListenerRequest) As String
            Dim value = ReadSessionCookie(request)
            If value Is Nothing Then Return Nothing
            Dim key = SessionKey(value)
            Dim now = AppLockService.Clock()
            Dim lifetime = SessionLifetime()
            SyncLock Sessions
                Dim session As BrowserSession = Nothing
                If Not Sessions.TryGetValue(key, session) Then Return Nothing
                If now - session.LastUsedUtc > lifetime Then
                    Sessions.Remove(key)
                    Return Nothing
                End If
                session.LastUsedUtc = now
            End SyncLock
            Return key
        End Function

        ''' <summary>Eine Nachricht des verbundenen Browsers zaehlt als Nutzung seiner Sitzung. False,
        ''' wenn sie inzwischen abgelaufen oder beendet ist; dann wird er getrennt.</summary>
        Private Shared Function TouchClientSession() As Boolean
            Dim key = _clientSessionKey
            If key.Length = 0 Then Return False
            Dim now = AppLockService.Clock()
            Dim lifetime = SessionLifetime()
            SyncLock Sessions
                Dim session As BrowserSession = Nothing
                If Sessions.TryGetValue(key, session) AndAlso now - session.LastUsedUtc <= lifetime Then
                    session.LastUsedUtc = now
                    Return True
                End If
                Sessions.Remove(key)
            End SyncLock
            DisconnectSignedInClient()
            Return False
        End Function

        ''' <summary>Legt eine Sitzung an und liefert den Wert fuer das Cookie.</summary>
        Private Shared Function CreateSession(request As HttpListenerRequest, userId As String) As String
            Dim value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            Dim now = AppLockService.Clock()
            Dim sender = SenderKey(request)
            Dim address = LogAddress(request)
            Dim lifetime = SessionLifetime()
            SyncLock Sessions
                ' Abgelaufene zuerst: sie belegen sonst Plaetze, bis jemand ihr Cookie vorzeigt.
                For Each stale In Sessions.Where(Function(pair) now - pair.Value.LastUsedUtc > lifetime).Select(Function(pair) pair.Key).ToList()
                    Sessions.Remove(stale)
                Next
                ' Je Benutzer eine Grenze: ein angemeldeter Benutzer, der sich immer wieder anmeldet,
                ' verdraengt nur seine eigenen Sitzungen, nicht die der anderen.
                Dim own = Sessions.Where(Function(pair) String.Equals(pair.Value.UserId, userId, StringComparison.Ordinal)).ToList()
                If own.Count >= MaxSessionsPerUser Then
                    RemoveSessionLocked(own.OrderBy(Function(pair) pair.Value.LastUsedUtc).First().Key)
                End If
                If Sessions.Count >= Math.Max(1, MaxSessions) Then
                    ' Voll ueber alle: die aelteste Sitzung des Benutzers mit den meisten Sitzungen
                    ' (bei Gleichstand der, dessen aelteste am laengsten unbenutzt ist).
                    Dim busiest = Sessions.GroupBy(Function(pair) If(pair.Value.UserId, "")).
                                           OrderByDescending(Function(g) g.Count()).
                                           ThenBy(Function(g) g.Min(Function(pair) pair.Value.LastUsedUtc)).First()
                    RemoveSessionLocked(busiest.OrderBy(Function(pair) pair.Value.LastUsedUtc).First().Key)
                End If
                Sessions(SessionKey(value)) = New BrowserSession With {.LastUsedUtc = now, .Sender = sender, .Address = address, .UserId = userId}
            End SyncLock
            Return value
        End Function

        ''' <summary>Entfernt eine Sitzung unter der Sperre von Sessions. War es die des verbundenen
        ''' Browsers, wird er danach getrennt (ausserhalb der Sperre, ueber den Thread-Pool).</summary>
        Private Shared Sub RemoveSessionLocked(key As String)
            Sessions.Remove(key)
            If String.Equals(key, _clientSessionKey, StringComparison.Ordinal) Then
                Dim ignored = Task.Run(Sub() DisconnectSignedInClient())
            End If
        End Sub

        ''' <summary>Wer an der vollen Oberflaeche sitzt, fuer das Protokoll (SecurityLog.DesktopActor):
        ''' Name und Adresse der Sitzung des verbundenen Browsers, ohne ihn zweimal "-".</summary>
        Friend Shared Function ConnectedActor() As (User As String, From As String)
            Dim key = _clientSessionKey
            If key.Length > 0 Then
                SyncLock Sessions
                    Dim session As BrowserSession = Nothing
                    If Sessions.TryGetValue(key, session) Then Return (SecurityLog.UserNameOf(session.UserId), session.Address)
                End SyncLock
            End If
            Return (SecurityLog.NoValue, SecurityLog.NoValue)
        End Function

        ''' <summary>Die Adresse einer Anfrage fuer das Protokoll (SecurityLog): dieselbe, die die
        ''' Bremse zaehlt (<see cref="CounterAddress"/>), hinter dem vertrauten Proxy also die
        ''' weitergereichte, aber voll und nicht als ihr /64. IPv4 in IPv6 erscheint als IPv4.</summary>
        Friend Shared Function LogAddress(request As HttpListenerRequest) As String
            Dim address As IPAddress
            Try
                address = CounterAddress(request)
            Catch ex As Exception When TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                ' Die Verbindung ist schon zu; die Zeile kommt trotzdem, ohne Adresse.
                Return SecurityLog.NoValue
            End Try
            If address Is Nothing Then Return SecurityLog.NoValue
            If address.IsIPv4MappedToIPv6 Then address = address.MapToIPv4()
            Return address.ToString()
        End Function

        ''' <summary>Die Wurzeln, relativ zu denen das Protokoll Pfade nennt: die Startordner der
        ''' vollen Oberflaeche (Fotoordner, sonst Benutzerordner und ueberwachte Ordner).</summary>
        Friend Shared Function LogRoots() As List(Of String)
            Return New MobileContext(AppLockService.OwnerUserId, "").BrowseRoots()
        End Function

        ''' <summary>Der Benutzer einer Sitzung, sonst Nothing.</summary>
        Private Shared Function SessionUser(key As String) As String
            If String.IsNullOrEmpty(key) Then Return Nothing
            SyncLock Sessions
                Dim session As BrowserSession = Nothing
                If Sessions.TryGetValue(key, session) Then Return session.UserId
            End SyncLock
            Return Nothing
        End Function

        ''' <summary>Wie viele Sitzungen gelten. Fuer den Pruefstand.</summary>
        Friend Shared ReadOnly Property SessionCount As Integer
            Get
                SyncLock Sessions
                    Return Sessions.Count
                End SyncLock
            End Get
        End Property

        ''' <summary>Beendet alle Sitzungen und trennt den verbundenen Browser; seine Seite laedt
        ''' neu und bekommt das Anmeldefenster.</summary>
        Friend Shared Sub EndAllSessions()
            SyncLock Sessions
                Sessions.Clear()
            End SyncLock
            DisconnectSignedInClient()
        End Sub

        ''' <summary>Nach einem neuen Kennwort oder dem Abschalten des Kennworts: alle Sitzungen enden
        ''' ausser der des verbundenen Browsers. Wer das Kennwort aendert, tut das in der Oberflaeche,
        ''' und die sieht im Browser nur der verbundene; ihn auszusperren hiesse, ihn mitten in den
        ''' Einstellungen zum Anmeldefenster zu schicken. Jeder andere Browser meldet sich mit dem
        ''' neuen Kennwort neu an. Beim Abschalten verhindert das, dass alte Sitzungen nach einem
        ''' spaeteren Einschalten wieder gelten. Ohne Sitzung (es gab vorher kein Kennwort) bleibt
        ''' keine; der verbundene Browser landet dann wie bisher beim Anmeldefenster.</summary>
        Friend Shared Sub EndOtherSessions()
            Dim keep = _clientSessionKey
            Dim ended As Integer
            SyncLock Sessions
                Dim kept As BrowserSession = Nothing
                If keep.Length = 0 OrElse Not Sessions.TryGetValue(keep, kept) Then kept = Nothing
                ended = Sessions.Count - If(kept Is Nothing, 0, 1)
                Sessions.Clear()
                If kept IsNot Nothing Then Sessions(keep) = kept
                _basicHeaderKey = ""
            End SyncLock
            If ended > 0 Then DiagnosticLogService.LogAlways("BrowserBridge", $"Password changed, ended {ended} other session(s).")
        End Sub

        ''' <summary>Beendet alle Sitzungen eines Benutzers, ausser <paramref name="keepKey"/>: nach
        ''' einem neuen Kennwort, dem Sperren, Loeschen oder Herabstufen (UserAccountService). Ist der
        ''' verbundene Browser darunter, wird er getrennt. Der kurz gemerkte Basic-Kopf eines
        ''' Werkzeugs gilt danach ebenfalls nicht mehr.</summary>
        Friend Shared Sub EndSessionsOfUser(userId As String, keepKey As String)
            If String.IsNullOrEmpty(userId) Then Return
            Dim ended As Integer
            Dim disconnect = False
            SyncLock Sessions
                Dim keys = Sessions.Where(Function(pair) String.Equals(pair.Value.UserId, userId, StringComparison.Ordinal) AndAlso
                                                         Not String.Equals(pair.Key, keepKey, StringComparison.Ordinal)).
                                    Select(Function(pair) pair.Key).ToList()
                For Each key In keys
                    Sessions.Remove(key)
                    If String.Equals(key, _clientSessionKey, StringComparison.Ordinal) Then disconnect = True
                Next
                ended = keys.Count
                _basicHeaderKey = ""
            End SyncLock
            If disconnect Then DisconnectSignedInClient()
            If ended > 0 Then DiagnosticLogService.LogAlways("BrowserBridge", $"Ended {ended} session(s) of {userId}.")
        End Sub

        ''' <summary>Wie viele Sitzungen ein Benutzer hat. Fuer den Pruefstand.</summary>
        Friend Shared Function SessionCountOf(userId As String) As Integer
            SyncLock Sessions
                Return Sessions.Values.Where(Function(s) String.Equals(s.UserId, userId, StringComparison.Ordinal)).Count()
            End SyncLock
        End Function

        Private Shared Sub EndSession(key As String)
            If String.IsNullOrEmpty(key) Then Return
            SyncLock Sessions
                Sessions.Remove(key)
            End SyncLock
            If String.Equals(key, _clientSessionKey, StringComparison.Ordinal) Then DisconnectSignedInClient()
        End Sub

        Private Shared Sub DisconnectSignedInClient()
            Dim socket = CurrentClient
            _clientSessionKey = ""
            If socket Is Nothing Then Return
            ClearInput()
            Dim ignored = Task.Run(
                Async Function()
                    Await SendRawAsync(socket, Encoding.UTF8.GetBytes("{""t"":""reload""}"), WebSockets.WebSocketMessageType.Text)
                    ReplaceClientIfCurrent(socket)
                End Function)
        End Sub

        Private Shared Sub ReplaceClientIfCurrent(socket As WebSockets.WebSocket)
            Dim current As Boolean
            SyncLock ClientLock
                current = _client Is socket
                If current Then _client = Nothing
            End SyncLock
            Try
                socket.Abort()
            Catch
            End Try
        End Sub

        ''' <summary>Ob die Anfrage angemeldet ist: mit gueltiger Sitzung oder (fuer Werkzeuge) mit
        ''' "Authorization: Basic". Ohne gesetztes Kennwort nie.</summary>
        Private Shared Function IsSignedIn(context As HttpListenerContext) As Boolean
            Return SignedInUser(context) IsNot Nothing
        End Function

        ''' <summary>Der angemeldete Benutzer einer Anfrage, sonst Nothing: aus der Sitzung, oder
        ''' fuer ein Werkzeug mit Basic der Benutzer zu Name und Kennwort im Kopf. Ein Benutzer, den
        ''' es nicht mehr gibt oder der gesperrt ist, gilt nicht, auch mit gueltigem Cookie.</summary>
        Private Shared Function SignedInUser(context As HttpListenerContext) As String
            If Not AppLockService.HasOwnerPassword Then Return Nothing
            Dim key = ValidSessionKey(context.Request)
            Dim user As String = Nothing
            If key IsNot Nothing Then
                user = SessionUser(key)
            Else
                user = BasicSignedInUserOnce(context.Request)
            End If
            If user Is Nothing OrElse Not UserAccountService.IsActive(user) Then Return Nothing
            Return user
        End Function

        ''' <summary>Das Ergebnis von <see cref="BasicSignedInUser"/> je Anfrage. Eine Anfrage fragt
        ''' ihren Benutzer an mehreren Stellen (Sprache, Pruefung der Schnittstelle, Kontext); ohne
        ''' dieses Merken zaehlte ein falscher Basic-Kopf eines Werkzeugs bis zu dreimal als
        ''' Fehlversuch.</summary>
        Private Shared ReadOnly BasicOutcomes As New System.Runtime.CompilerServices.ConditionalWeakTable(Of HttpListenerRequest, BasicOutcome)()

        Private NotInheritable Class BasicOutcome
            Public ReadOnly UserId As String
            Public Sub New(userId As String)
                Me.UserId = userId
            End Sub
        End Class

        ''' <summary><see cref="BasicSignedInUser"/>, je Anfrage hoechstens einmal gerechnet.</summary>
        Private Shared Function BasicSignedInUserOnce(request As HttpListenerRequest) As String
            Dim known As BasicOutcome = Nothing
            If BasicOutcomes.TryGetValue(request, known) Then Return known.UserId
            Dim user = BasicSignedInUser(request)
            BasicOutcomes.AddOrUpdate(request, New BasicOutcome(user))
            Return user
        End Function

        ''' <summary>Fuer die Schnittstelle der mobilen Seite: der angemeldete Benutzer dieser
        ''' Anfrage, sonst Nothing, und dann ist die Antwort schon geschrieben (401 mit JSON, nie
        ''' WWW-Authenticate). Die eine Stelle, an der eine Anfrage ihren Benutzer bekommt; heute
        ''' ist das immer der Eigentuemer.</summary>
        Friend Shared Async Function RequireSessionAsync(context As HttpListenerContext) As Task(Of String)
            Dim user = SignedInUser(context)
            If user IsNot Nothing Then Return user
            Dim answer As New Dictionary(Of String, Object) From {{"ok", False}, {"signIn", True}}
            If Not AppLockService.HasOwnerPassword Then answer("setup") = True
            Dim bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(answer))
            context.Response.StatusCode = 401
            context.Response.ContentType = "application/json; charset=utf-8"
            context.Response.Headers("Cache-Control") = "no-store"
            context.Response.ContentLength64 = bytes.Length
            Await context.Response.OutputStream.WriteAsync(bytes)
            context.Response.Close()
            Return Nothing
        End Function

        ''' <summary>Die Pruefung vor der mobilen Seite (BrowserBridgeMobile.vb), dieselbe wie vor
        ''' der vollen: ohne Kennwort nur die Einrichtung, ohne Sitzung nur die Anmeldung (mit Mobil
        ''' vorgewaehlt), und die Schnittstelle antwortet ohne Sitzung 401. Ohne Kennwort bekommt
        ''' auch die feste Huelle der Seite (Manifest, Skript, Stil, Symbole) nichts: bis zur
        ''' Einrichtung ist ausser ihr nichts erreichbar. Alles Uebrige entscheidet die Seite selbst
        ''' (HandleMobileAsync).</summary>
        Private Shared Async Function HandleMobileRouteAsync(context As HttpListenerContext, path As String) As Task
            ' Die mobile Seite spricht die Sprache ihres Benutzers (ohne Anmeldung die des Browsers),
            ' nur im Ablauf dieser Anfrage.
            ApplyRequestLanguage(context.Request, SignedInUser(context))
            Dim isPage = path = "/m" OrElse path = MobilePagePrefix OrElse path = MobilePagePrefix & "index.html"
            Dim isApi = path = "/api/m" OrElse path.StartsWith(MobileApiPrefix, StringComparison.Ordinal)
            If isApi Then
                If Await RequireSessionAsync(context) Is Nothing Then Return
                ' Wer auf der mobilen Seite arbeitet, ist aktiv: sonst sperrte die Zeit ohne Eingabe
                ' am Fenster die Anwendung, und das Sperren beendet ALLE Sitzungen, auch seine. Nur
                ' mit Sitzung (ein Werkzeug mit Basic haelt die Anwendung nicht offen). NoteActivity
                ' setzt nur eine Uhrzeit unter einer Sperre, ist threadsicher und braucht weder den
                ' Anzeigefaden noch eine Drossel.
                If ValidSessionKey(context.Request) IsNot Nothing Then AppLockService.NoteActivity()
            ElseIf isPage Then
                If Not IsSignedIn(context) Then
                    Await ServeLoginPageAsync(context, mobile:=True)
                    Return
                End If
            ElseIf Not AppLockService.HasOwnerPassword Then
                context.Response.StatusCode = 401
                context.Response.Close()
                Return
            End If
            Await HandleMobileAsync(context, path)
        End Function

        ''' <summary>Schickt den verbundenen Browser auf die andere Seite: <c>/m/</c> fuer die mobile,
        ''' <c>/</c> fuer die volle. Die Seite merkt sich die Wahl fuer die naechste Anmeldung.</summary>
        Public Shared Sub SwitchPageVariant(mobile As Boolean)
            Dim json = If(mobile, "{""t"":""variant"",""to"":""mobile""}", "{""t"":""variant"",""to"":""desktop""}")
            Dim ignored = SendTextAsync(json)
        End Sub

        ''' <summary>Die Anmeldung eines Werkzeugs, aus dem Kopf Authorization gelesen. Ein Browser
        ''' schickt den Kopf nur, wenn er vorher dazu aufgefordert wurde, und das geschieht nicht
        ''' mehr. Fehlversuche zaehlen wie im Anmeldefenster. Name und Kennwort gelten wie dort, fuer
        ''' jeden Benutzer; geliefert wird seine Kennung, sonst Nothing.</summary>
        Private Shared Function BasicSignedInUser(request As HttpListenerRequest) As String
            If Not AppLockService.HasOwnerPassword Then Return Nothing
            Dim header = request.Headers("Authorization")
            If String.IsNullOrEmpty(header) OrElse Not header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            ' Ein Werkzeug schickt den Kopf bei jeder Anfrage; die Pruefung ist mit Absicht langsam.
            ' Der zuletzt gelungene Kopf gilt deshalb eine Minute ohne neue Rechnung, solange
            ' Kennwoerter und Benutzer dieselben sind (die Marke zaehlt jede Aenderung mit).
            Dim headerKey = SessionKey(header) & "|" & AppLockService.PasswordStamp()
            Dim now = AppLockService.Clock()
            Dim cachedUser As String = Nothing
            SyncLock Sessions
                If String.Equals(headerKey, _basicHeaderKey, StringComparison.Ordinal) AndAlso now < _basicHeaderUntilUtc Then cachedUser = _basicHeaderUser
            End SyncLock
            ' Ein inzwischen eingerichteter zweiter Faktor gilt auch fuer den gemerkten Kopf.
            If cachedUser IsNot Nothing Then Return If(BasicAllowedFor(cachedUser), cachedUser, Nothing)
            Dim decoded As String
            Try
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6).Trim()))
            Catch ex As FormatException
                Return Nothing
            End Try
            Dim separator = decoded.IndexOf(":"c)
            If separator < 0 Then Return Nothing
            Dim result = AppLockService.CheckSignIn(decoded.Substring(0, separator), decoded.Substring(separator + 1), SenderKey(request))
            Select Case result.Outcome
                Case PasswordCheckOutcome.Accepted
                    ' Ein Konto mit zweitem Faktor kommt mit Basic nicht herein: der Kopf traegt
                    ' keinen Code (BrowserBridgeTwoFactor.vb). Werkzeuge brauchen ein Konto ohne.
                    If Not BasicAllowedFor(result.UserId) Then
                        SecurityLog.Write("signin.refused", SecurityLog.UserNameOf(result.UserId), LogAddress(request), "via", "basic", "reason", "twofactor")
                        Return Nothing
                    End If
                    SecurityLog.SignedIn(result.UserId, LogAddress(request), "basic")
                Case PasswordCheckOutcome.Throttled
                    SecurityLog.Throttled("basic", LogAddress(request), result.RetryAfter)
                    Return Nothing
                Case PasswordCheckOutcome.Rejected
                    SecurityLog.SignInFailed(LogAddress(request), "basic")
                    Return Nothing
                Case Else
                    Return Nothing
            End Select
            SyncLock Sessions
                _basicHeaderKey = headerKey
                _basicHeaderUser = result.UserId
                _basicHeaderUntilUtc = now.AddMinutes(1)
            End SyncLock
            Return result.UserId
        End Function

        ''' <summary>Der Zaehler der Fehlversuche fuer die Absenderadresse.
        '''
        ''' <para>HINTER DEM PROXY kaeme jede Verbindung von ihm, und alle Geraete im Internet teilten
        ''' sich einen Zaehler: ein Fremder hielte mit einem Fehlversuch je Wartezeit den Eigentuemer
        ''' draussen. Gezaehlt wird dort deshalb die Adresse, die der Proxy in X-Forwarded-For
        ''' meldet (<see cref="CounterAddress"/>).</para>
        '''
        ''' <para>IPv6 ZAEHLT JE /64: ein Anschluss bekommt mindestens ein ganzes /64 und kann darin
        ''' fuer jeden Versuch eine neue Adresse nehmen. Je voller Adresse liefe die Bremse damit ins
        ''' Leere, und die Versuche verdraengten nebenbei die gemerkten Zaehler anderer.</para>
        '''
        ''' <para>Grenze: im Behaelter mit Portweiterleitung und ohne Proxy traegt jede Verbindung die
        ''' Adresse des Docker-Gateways, dann teilen sich alle Geraete diesen Zaehler (mit
        ''' network_mode host nicht).</para></summary>
        Private Shared Function SenderKey(request As HttpListenerRequest) As String
            Return CounterKeyOf(CounterAddress(request))
        End Function

        ''' <summary>Der Zaehler zu einer Adresse: IPv4 voll, IPv6 als ihr /64.</summary>
        Friend Shared Function CounterKeyOf(address As IPAddress) As String
            If address Is Nothing Then Return "address unknown"
            If address.IsIPv4MappedToIPv6 Then address = address.MapToIPv4()
            If address.AddressFamily = System.Net.Sockets.AddressFamily.InterNetworkV6 AndAlso Not IPAddress.IsLoopback(address) Then
                Dim bytes = address.GetAddressBytes()
                For i = 8 To 15
                    bytes(i) = 0
                Next
                ' Ohne Zonenkennung: fe80::1%2 und fe80::1%3 sind derselbe Zaehler.
                Return "address " & New IPAddress(bytes).ToString() & "/64"
            End If
            Return "address " & address.ToString()
        End Function

        ''' <summary>Die Adresse, deren Fehlversuche zaehlen: hinter dem Proxy der Browser, wie der
        ''' Proxy ihn meldet, sonst der Absender der Verbindung.
        '''
        ''' <para>Dem Kopf wird nur geglaubt, wenn die Verbindung vom Proxy kommt
        ''' (<see cref="IsTrustedSender"/>), sonst koennte jeder sich mit einem erfundenen Kopf von
        ''' der Bremse loesen. Und es zaehlt nur der LETZTE Eintrag: den hat der Proxy selbst
        ''' angehaengt (nginx mit $proxy_add_x_forwarded_for, auch der Proxy von DSM), was davor
        ''' steht, kommt vom Browser und ist frei erfunden. Caddy (docker/Caddyfile) glaubt ohne
        ''' trusted_proxies keinem mitgeschickten Kopf und setzt nur die Adresse des Browsers; das
        ''' ist dann zugleich der letzte Eintrag. Fehlt der Kopf oder ist er unlesbar, zaehlt wie
        ''' bisher der Proxy.</para></summary>
        Private Shared Function CounterAddress(request As HttpListenerRequest) As IPAddress
            Dim connection = SenderAddress(request)
            If _tlsProxy AndAlso IsTrustedSender(connection, _trustEverySender, _proxyAddresses) Then
                Dim forwarded = LastForwardedAddress(request.Headers("X-Forwarded-For"))
                If forwarded IsNot Nothing Then Return forwarded
            End If
            Return connection
        End Function

        ''' <summary>Der letzte Eintrag von X-Forwarded-For als Adresse, sonst Nothing. Ein Port
        ''' dahinter ("203.0.113.7:51000", "[2001:db8::1]:443") faellt weg.</summary>
        Friend Shared Function LastForwardedAddress(header As String) As IPAddress
            If String.IsNullOrWhiteSpace(header) Then Return Nothing
            Dim entries = header.Split(","c)
            Dim last = entries(entries.Length - 1).Trim()
            Dim endPoint As IPEndPoint = Nothing
            If last.Length = 0 OrElse Not IPEndPoint.TryParse(last, endPoint) Then Return Nothing
            Return endPoint.Address
        End Function

        ''' <summary>Die Zaehler fuer einen Anmeldeversuch im Anmeldefenster: der dieses Browsers
        ''' (Cookie <see cref="LoginCookieName"/>, beim Ausliefern der Anmeldeseite gesetzt) und der
        ''' seiner Adresse. Gebremst wird so nur der Browser, in dem falsch getippt wurde, und wer
        ''' das Cookie wegwirft, bleibt ueber die Adresse gebremst; andere Adressen und das
        ''' Anmeldefenster am Rechner merken nichts (AppLockService.CheckPassword).</summary>
        Private Shared Function LoginCounterKeys(request As HttpListenerRequest) As String()
            Dim address = SenderKey(request)
            Dim value = ReadLoginCookie(request)
            If value Is Nothing Then Return {address}
            Return {AppLockService.BrowserCounterPrefix & SessionKey(value).Substring(0, 16), address}
        End Function

        ''' <summary>Der Schluessel, mit dem der Server die Kennungen der Anmeldeseite unterschreibt,
        ''' neu bei jedem Start (eine alte Kennung gilt danach nicht, die Seite gibt eine neue aus).</summary>
        Private Shared ReadOnly LoginCookieKey As Byte() = RandomNumberGenerator.GetBytes(32)

        ''' <summary>Eine neue Kennung fuer die Anmeldeseite: 16 Zufallsbytes und die ersten 16 Bytes
        ''' ihres HMAC-SHA256, zusammen 32 Bytes als Hex wie zuvor.</summary>
        Private Shared Function NewLoginCookieValue() As String
            Dim nonce = RandomNumberGenerator.GetBytes(16)
            Dim mac = HMACSHA256.HashData(LoginCookieKey, nonce)
            Return Convert.ToHexString(nonce) & Convert.ToHexString(mac, 0, 16)
        End Function

        ''' <summary>Die Kennung der Anmeldeseite dieser Anfrage, wenn der Server sie selbst
        ''' ausgegeben hat, sonst Nothing.
        '''
        ''' <para>NUR AUSGEGEBENE ZAEHLEN: frueher galt jeder Wert aus 64 Hexzeichen. Wer fuer jeden
        ''' Versuch einen neuen erfand, bekam je Versuch einen neuen Zaehler und schob damit die
        ''' gemerkten anderer aus der Liste. Ein ausgedachter Wert gilt jetzt wie keiner: gezaehlt
        ''' wird dann nur die Adresse.</para></summary>
        Private Shared Function ReadLoginCookie(request As HttpListenerRequest) As String
            Dim value = ReadHexCookie(request, LoginCookieName)
            If value Is Nothing Then Return Nothing
            Dim bytes = Convert.FromHexString(value)
            Dim nonce = bytes.Take(16).ToArray()
            Dim given = bytes.Skip(16).ToArray()
            Dim expected = HMACSHA256.HashData(LoginCookieKey, nonce).Take(16).ToArray()
            If Not CryptographicOperations.FixedTimeEquals(expected, given) Then Return Nothing
            Return value
        End Function

        ''' <summary>Kam die Seite ueber HTTPS? Hinter dem Proxy nimmt die Bruecke nur an, was er als
        ''' HTTPS kennzeichnet (IsAcceptedRoute); dann bekommt das Cookie "Secure".</summary>
        Private Shared Function IsSecureRequest(request As HttpListenerRequest) As Boolean
            Return request.IsSecureConnection OrElse
                   (_tlsProxy AndAlso IsForwardedAsHttps(request.Headers("X-Forwarded-Proto")))
        End Function

        Private Shared Function SessionCookie(value As String, maxAgeSeconds As Long, secure As Boolean) As String
            Return $"{SessionCookieName}={value}; Max-Age={maxAgeSeconds.ToString(CultureInfo.InvariantCulture)}; Path=/; HttpOnly; SameSite=Strict" &
                   If(secure, "; Secure", "")
        End Function

        ''' <summary>So lange lebt das Cookie im Browser, gleich welche Sperrzeit gilt. Ob die Sitzung
        ''' noch gilt, entscheidet der Server (<see cref="SessionLifetime"/>); das Cookie darf nur
        ''' nicht VOR ihr ablaufen.</summary>
        Friend Shared Function SessionCookieMaxAgeSeconds() As Long
            Return CLng(DefaultSessionLifetime.TotalSeconds)
        End Function

        ''' <summary>Setzt das Cookie einer gueltigen Sitzung neu, damit seine 30 Tage wieder vom
        ''' letzten Laden der Seite an zaehlen.</summary>
        Private Shared Sub RefreshSessionCookie(context As HttpListenerContext)
            Dim value = ReadSessionCookie(context.Request)
            If value Is Nothing Then Return
            context.Response.Headers.Add("Set-Cookie",
                SessionCookie(value, SessionCookieMaxAgeSeconds(), IsSecureRequest(context.Request)))
        End Sub

        ' --- Anmelden und Abmelden ---------------------------------------------------------------

        ''' <summary>Liest den Rumpf einer Anmeldung oder Einrichtung: nur POST, nur von der eigenen
        ''' Seite, nur JSON, hoechstens <see cref="MaxLoginBodyBytes"/>. Liefert die Felder als
        ''' Zeichenketten oder Nothing; dann ist die Antwort (405, 403, 415, 413, 400) geschrieben.</summary>
        Private Shared Async Function ReadSignInBodyAsync(context As HttpListenerContext) As Task(Of Dictionary(Of String, String))
            Dim request = context.Request
            Dim status = 0
            If Not String.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) Then
                status = 405
            ElseIf Not IsOwnOrigin(request) Then
                status = 403
            ElseIf Not If(request.ContentType, "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase) Then
                status = 415
            ElseIf request.ContentLength64 < 0 OrElse request.ContentLength64 > MaxLoginBodyBytes Then
                status = 413
            End If
            Dim fields As New Dictionary(Of String, String)(StringComparer.Ordinal)
            If status = 0 Then
                Try
                    Dim buffer(CInt(request.ContentLength64) - 1) As Byte
                    Dim read = 0
                    While read < buffer.Length
                        Dim count = Await request.InputStream.ReadAsync(buffer, read, buffer.Length - read)
                        If count <= 0 Then Exit While
                        read += count
                    End While
                    Using document = JsonDocument.Parse(New ReadOnlyMemory(Of Byte)(buffer, 0, read))
                        If document.RootElement.ValueKind = JsonValueKind.Object Then
                            For Each field In document.RootElement.EnumerateObject()
                                If field.Value.ValueKind = JsonValueKind.String Then fields(field.Name) = field.Value.GetString()
                            Next
                        End If
                    End Using
                Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is IOException
                    status = 400
                End Try
            End If
            If status = 0 Then Return fields
            context.Response.StatusCode = status
            context.Response.Close()
            Return Nothing
        End Function

        ''' <summary>POST /login mit {"user": "...", "password": "..."}. Bei Erfolg: Sitzung, Cookie
        ''' und die Anwendung ist entsperrt, fuer alle Browser mit Sitzung. Ein falscher Name zaehlt
        ''' wie ein falsches Kennwort, mit derselben Meldung (AppLockService.CheckSignIn). Ist noch
        ''' kein Name gespeichert, wird der eingegebene nach dem Erfolg der Name des Eigentuemers.
        ''' Ohne gesetztes Kennwort gibt es nichts anzumelden: 409, und die Seite laedt neu und
        ''' zeigt die Einrichtung.</summary>
        Private Shared Async Function HandleLoginAsync(context As HttpListenerContext) As Task
            Dim request = context.Request
            Dim fields = Await ReadSignInBodyAsync(context)
            If fields Is Nothing Then Return
            Dim password As String = Nothing
            Dim user As String = Nothing
            If Not fields.TryGetValue("password", password) Then password = ""
            If Not fields.TryGetValue("user", user) Then user = ""

            If Not AppLockService.HasOwnerPassword Then
                Await WriteLoginAnswerAsync(context, 409, "", 0, setup:=True)
                Return
            End If
            ' Der zweite Schritt (Code oder Wiederherstellungscode) geht an denselben Weg
            ' (BrowserBridgeTwoFactor.vb).
            If fields.ContainsKey("code") OrElse fields.ContainsKey("recovery") Then
                Await HandleSecondFactorAsync(context, fields)
                Return
            End If
            Dim from = LogAddress(request)
            Dim counters = LoginCounterKeys(request)
            Dim result = Await Task.Run(Function() AppLockService.CheckSignIn(user, password, counters))
            Select Case result.Outcome
                Case PasswordCheckOutcome.Accepted
                    Dim userId = If(result.UserId, AppLockService.OwnerUserId)
                    If userId = AppLockService.OwnerUserId AndAlso AppLockService.AdoptUserName(user) Then
                        SecurityLog.UserNameClaimed(SecurityLog.UserNameOf(userId), from)
                    End If
                    ' Mit zweitem Faktor noch keine Sitzung: erst der Code. Erst jetzt, wo das
                    ' Kennwort stimmt, erfaehrt die Seite, dass das Konto einen hat.
                    If TwoFactorService.IsEnabled(userId) Then
                        Await BeginSecondStepAsync(context, userId)
                        Return
                    End If
                    Await CompleteSignInAsync(context, userId)
                Case PasswordCheckOutcome.Throttled
                    Dim seconds = CInt(Math.Ceiling(result.RetryAfter.TotalSeconds))
                    context.Response.Headers("Retry-After") = seconds.ToString(CultureInfo.InvariantCulture)
                    SecurityLog.Throttled("signin", from, result.RetryAfter)
                    Dim text = Await Dispatcher.UIThread.InvokeAsync(Function() ViewModels.LockScreenViewModel.ThrottledText(result.RetryAfter))
                    Await WriteLoginAnswerAsync(context, 429, text, seconds)
                Case Else
                    If result.Outcome = PasswordCheckOutcome.Rejected Then SecurityLog.SignInFailed(from, "page")
                    Dim text = Await Dispatcher.UIThread.InvokeAsync(
                        Function() If(result.RetryAfter > TimeSpan.Zero,
                                      ViewModels.LockScreenViewModel.ThrottledText(result.RetryAfter),
                                      LocalizationService.T("Benutzername oder Kennwort ist falsch.")))
                    Await WriteLoginAnswerAsync(context, 401, text, 0)
            End Select
        End Function

        ''' <summary>Der Abschluss einer Anmeldung im Browser, mit oder nach dem zweiten Faktor:
        ''' Sitzung, Cookie, Protokoll, entsperrt fuer einen Administrator, Sprache, Ziel.</summary>
        Private Shared Async Function CompleteSignInAsync(context As HttpListenerContext, userId As String) As Task
            Dim request = context.Request
            Dim from = LogAddress(request)
            ' Eine Anmeldung macht einen offenen Verweis zum Zuruecksetzen ungueltig.
            PasswordResetService.Forget(userId)
            Dim value = CreateSession(request, userId)
            context.Response.Headers.Add("Set-Cookie", SessionCookie(value, SessionCookieMaxAgeSeconds(), IsSecureRequest(request)))
            SecurityLog.SignedIn(userId, from, "page")
            ' Entsperrt wird die Anwendung nur fuer einen Administrator: die Sperre gilt der
            ' vollen Oberflaeche, und die sieht nur er.
            If CanUseDesktop(userId) Then UnlockForApp(userId, from)
            RememberLanguage(context, userId)
            Await WriteLoginAnswerAsync(context, 200, Nothing, 0, target:=TargetFor(userId))
        End Function

        ''' <summary>POST /setup mit {"user": "...", "password": "...", "repeat": "..."}: die
        ''' Ersteinrichtung von Benutzername und Kennwort, solange kein Kennwort gesetzt ist. Dieselben Regeln wie in den Einstellungen,
        ''' dieselbe Herkunftspruefung und dieselbe Bremse wie beim Anmelden (ein abgewiesener
        ''' Versuch zaehlt als Fehlversuch). Bei Erfolg: Kennwort gespeichert, Sitzung des
        ''' Eigentuemers, Cookie, 200. Ist inzwischen eines gesetzt (auch von einem zweiten,
        ''' gleichzeitigen Versuch), 409: die Einrichtung geht nur einmal.</summary>
        Private Shared Async Function HandleSetupAsync(context As HttpListenerContext) As Task
            Dim request = context.Request
            Dim fields = Await ReadSignInBodyAsync(context)
            If fields Is Nothing Then Return
            If AppLockService.HasOwnerPassword Then
                Dim taken = Await Dispatcher.UIThread.InvokeAsync(Function() LocalizationService.T("Das Kennwort wurde inzwischen schon festgelegt. Bitte anmelden."))
                Await WriteLoginAnswerAsync(context, 409, taken, 0)
                Return
            End If
            Dim counters = LoginCounterKeys(request)
            Dim wait = AppLockService.RetryAfter(counters)
            If wait > TimeSpan.Zero Then
                Dim seconds = CInt(Math.Ceiling(wait.TotalSeconds))
                context.Response.Headers("Retry-After") = seconds.ToString(CultureInfo.InvariantCulture)
                SecurityLog.Throttled("setup", LogAddress(request), wait)
                Dim text = Await Dispatcher.UIThread.InvokeAsync(Function() ViewModels.LockScreenViewModel.ThrottledText(wait))
                Await WriteLoginAnswerAsync(context, 429, text, seconds)
                Return
            End If
            Dim password As String = Nothing
            Dim repeat As String = Nothing
            Dim user As String = Nothing
            Dim email As String = Nothing
            If Not fields.TryGetValue("password", password) Then password = ""
            If Not fields.TryGetValue("repeat", repeat) Then repeat = ""
            If Not fields.TryGetValue("user", user) Then user = ""
            If Not fields.TryGetValue("email", email) Then email = ""
            Dim problems = AppLockService.PasswordProblems(password)
            ' Nach dem Notweg fuer das Kennwort steht der Name schon fest; dann zaehlt der eingegebene nicht.
            Dim nameValid = AppLockService.StoredUserName.Length > 0 OrElse AppLockService.IsValidUserName(user)
            Dim emailValid = UserAccountService.IsValidEmail(email) AndAlso Not UserAccountService.EmailTaken(email, AppLockService.OwnerUserId)
            If Not nameValid OrElse Not emailValid OrElse problems.Count > 0 OrElse password <> repeat Then
                AppLockService.RegisterFailedAttempt(counters)
                Dim text = Await Dispatcher.UIThread.InvokeAsync(
                    Function() If(Not nameValid, LocalizationService.T("Bitte einen Benutzernamen eingeben, höchstens 64 Zeichen."),
                               If(Not emailValid, LocalizationService.T("Die E-Mail-Adresse ist ungültig."),
                               If(problems.Count > 0, ViewModels.SettingsViewModel.PasswordRuleText(problems(0)),
                                  LocalizationService.T("Die beiden neuen Kennwörter stimmen nicht überein.")))))
                Await WriteLoginAnswerAsync(context, 400, text, 0)
                Return
            End If
            Dim outcome = Await Task.Run(Function() AppLockService.TrySetInitialPassword(user, password, email))
            If outcome <> InitialPasswordOutcome.Created Then
                Dim taken = Await Dispatcher.UIThread.InvokeAsync(Function() LocalizationService.T("Das Kennwort wurde inzwischen schon festgelegt. Bitte anmelden."))
                Await WriteLoginAnswerAsync(context, 409, taken, 0)
                Return
            End If
            Dim from = LogAddress(request)
            ' Die Einrichtung legt den ersten Benutzer fest, und der ist der Eigentuemer.
            Dim value = CreateSession(request, AppLockService.OwnerUserId)
            context.Response.Headers.Add("Set-Cookie", SessionCookie(value, SessionCookieMaxAgeSeconds(), IsSecureRequest(request)))
            SecurityLog.SetupDone(SecurityLog.UserNameOf(AppLockService.OwnerUserId), from)
            UnlockForApp(AppLockService.OwnerUserId, from)
            Await WriteLoginAnswerAsync(context, 200, Nothing, 0, target:=TargetFor(AppLockService.OwnerUserId))
        End Function

        ' --- Sprache je Anfrage ------------------------------------------------------------------

        ''' <summary>Die zuletzt benutzte Sprache in diesem Browser, fuer die Anmeldeseite vor der
        ''' Anmeldung. Gibt keinen Zugang.</summary>
        Friend Const LanguageCookieName As String = "ferrumpix_lang"

        ''' <summary>Die Sprache einer Anfrage im Browser: mit Benutzer seine eingestellte, bei
        ''' "automatisch" die des Browsers (Accept-Language); ohne Benutzer die zuletzt in diesem
        ''' Browser benutzte (Cookie), sonst Accept-Language; passt nichts, die der Anwendung.</summary>
        Friend Shared Function RequestLanguageFor(request As HttpListenerRequest, userId As String) As CultureInfo
            Dim culture As CultureInfo = Nothing
            If Not String.IsNullOrEmpty(userId) Then
                culture = UserAccountService.CultureOf(userId)
            Else
                culture = LocalizationService.CultureForAcceptLanguage(request.Cookies(LanguageCookieName)?.Value)
            End If
            If culture Is Nothing Then culture = LocalizationService.CultureForAcceptLanguage(request.Headers("Accept-Language"))
            Return culture
        End Function

        ''' <summary>Setzt die Sprache fuer den Ablauf dieser Anfrage (LocalizationService.
        ''' SetRequestCulture), ohne die Sprache der Anwendung anzufassen. Nur in den Wegen der
        ''' Anmeldung und der mobilen Seite; die volle Oberflaeche bleibt in der Sprache der
        ''' Anwendung.</summary>
        Friend Shared Sub ApplyRequestLanguage(request As HttpListenerRequest, userId As String)
            LocalizationService.SetRequestCulture(RequestLanguageFor(request, userId))
        End Sub

        ''' <summary>Merkt die Sprache eines Benutzers in diesem Browser, damit die Anmeldeseite das
        ''' naechste Mal schon in ihr erscheint.</summary>
        Friend Shared Sub RememberLanguage(context As HttpListenerContext, userId As String)
            Dim culture = RequestLanguageFor(context.Request, userId)
            If culture Is Nothing Then Return
            Dim code = If(culture Is CultureInfo.InvariantCulture, "en", culture.Name)
            context.Response.Headers.Add("Set-Cookie",
                $"{LanguageCookieName}={code}; Max-Age=31536000; Path=/; HttpOnly; SameSite=Strict" & If(IsSecureRequest(context.Request), "; Secure", ""))
        End Sub

        ''' <summary>Schickt eine Arbeit an die Anwendung (Anzeigefaden) OHNE die Sprache der
        ''' Anfrage: der Dispatcher nimmt den Ablauf mit, und die Oberflaeche der Anwendung soll in
        ''' ihrer eigenen Sprache bleiben.</summary>
        Friend Shared Sub PostToApp(action As Action)
            Dispatcher.UIThread.Post(
                Sub()
                    LocalizationService.SetRequestCulture(Nothing)
                    action()
                End Sub)
        End Sub

        ''' <summary>Entsperrt die Anwendung aus einer Anfrage heraus, in der Sprache der Anwendung.</summary>
        Private Shared Sub UnlockForApp(userId As String, from As String)
            Using LocalizationService.UseRequestCulture(Nothing)
                AppLockService.UnlockBy("signin", SecurityLog.UserNameOf(userId), from)
            End Using
        End Sub

        ''' <summary>Wohin die Seite nach der Anmeldung geht: die volle Oberflaeche nur fuer einen
        ''' Administrator, sonst die mobile Seite. Entscheidet der Server; die Wahl im
        ''' Anmeldefenster ist nur ein Wunsch.</summary>
        Private Shared Function TargetFor(userId As String) As String
            Return If(CanUseDesktop(userId), "/", MobilePagePrefix)
        End Function

        ' --- Kennwort vergessen ----------------------------------------------------------------

        ''' <summary>POST /forgot mit {"who": Name oder E-Mail}. Ohne eingerichteten Mailserver oder
        ''' ohne eingetragene oeffentliche Adresse (<see cref="CanSendResetMail"/>) sagt die Antwort
        ''' nur, dass der Administrator hilft. Sonst ist sie fuer jedes Konto
        ''' dieselbe, in derselben Zeit (die Nachricht geht im Hintergrund hinaus): ob es das Konto
        ''' gibt und ob es eine Adresse hat, erfaehrt niemand. Gebremst wird je Browser und Adresse
        ''' (429) und je Konto (still), siehe PasswordResetService.</summary>
        Private Shared Async Function HandleForgotAsync(context As HttpListenerContext) As Task
            Dim request = context.Request
            Dim fields = Await ReadSignInBodyAsync(context)
            If fields Is Nothing Then Return
            If Not AppLockService.HasOwnerPassword Then
                Await WriteLoginAnswerAsync(context, 409, "", 0, setup:=True)
                Return
            End If
            Dim linkBase = ConfiguredPublicAddress()
            If linkBase Is Nothing OrElse Not MailService.IsConfigured Then
                SecurityLog.ResetRequested(LogAddress(request))
                Dim adminText = Await Dispatcher.UIThread.InvokeAsync(Function() LocalizationService.T("Bitte wenden Sie sich an den Administrator."))
                Await WriteLoginAnswerAsync(context, 200, Nothing, 0, message:=adminText)
                Return
            End If
            Dim who As String = Nothing
            If Not fields.TryGetValue("who", who) Then who = ""
            Dim counters = LoginCounterKeys(request)
            ' Die Texte entstehen erst, wenn das Konto feststeht: in seiner Sprache (T() ist
            ' threadsicher und fragt die Sprache des Ablaufs, LocalizationService.TextCulture).
            Dim texts As Func(Of PasswordResetService.MailTexts) =
                Function() New PasswordResetService.MailTexts With {
                    .Subject = LocalizationService.T("FerrumPix: neues Kennwort festlegen"),
                    .Title = LocalizationService.T("Neues Kennwort festlegen"),
                    .Greeting = LocalizationService.T("Hallo {0},"),
                    .Intro = LocalizationService.T("Für dein Konto bei FerrumPix wurde ein neues Kennwort angefordert. Über den Knopf legst du es fest."),
                    .ButtonText = LocalizationService.T("Neues Kennwort festlegen"),
                    .LinkHint = LocalizationService.T("Falls der Knopf nicht geht, kopiere diesen Link in den Browser:"),
                    .Validity = LocalizationService.T("Der Link gilt 30 Minuten und nur einmal."),
                    .Ignore = LocalizationService.T("Wenn du das nicht angefordert hast, kannst du diese Mail ignorieren.")}
            Dim accepted = PasswordResetService.Admit(counters)
            ' Nur der Absender: wer gemeint war und ob es das Konto gibt, steht nicht im Protokoll.
            If accepted Then SecurityLog.ResetRequested(LogAddress(request))
            If Not accepted Then
                Dim wait = TimeSpan.FromHours(1)
                SecurityLog.Throttled("forgot", LogAddress(request), wait)
                context.Response.Headers("Retry-After") = CInt(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture)
                Dim text = Await Dispatcher.UIThread.InvokeAsync(Function() LocalizationService.T("Zu viele Anfragen. Bitte später noch einmal versuchen."))
                Await WriteLoginAnswerAsync(context, 429, text, 0)
                Return
            End If
            Dim sent = Await Dispatcher.UIThread.InvokeAsync(Function() LocalizationService.T("Wenn es dazu ein Konto mit E-Mail-Adresse gibt, ist eine Nachricht unterwegs. Sonst wenden Sie sich bitte an den Administrator."))
            Await WriteLoginAnswerAsync(context, 200, Nothing, 0, message:=sent)
            ' Erst nach der Antwort: alles, was vom Konto abhaengt. So verraet die Antwortzeit
            ' nicht, ob es das Konto gibt.
            PasswordResetService.SendInBackground(who, linkBase, texts)
        End Function

        ''' <summary>Die eingetragene oeffentliche Adresse (AppSettings.BrowserPublicAddress) ohne
        ''' Schraegstrich am Ende, wenn sie taugt (<see cref="IsUsablePublicAddress"/>), sonst
        ''' Nothing.</summary>
        Friend Shared Function ConfiguredPublicAddress() As String
            Dim configured = If(AppSettingsService.Load().BrowserPublicAddress, "").Trim().TrimEnd("/"c)
            Return If(IsUsablePublicAddress(configured), configured, Nothing)
        End Function

        ''' <summary>Ob "Kennwort vergessen" eine Nachricht verschicken kann: Mailserver eingerichtet
        ''' UND oeffentliche Adresse eingetragen. Der Verweis in der Nachricht beginnt NUR mit dieser
        ''' Adresse, nie mit einem Namen aus der Anfrage: Host und X-Forwarded-Host schickt der
        ''' Absender von /forgot selbst, und ein Verweis auf seinen Rechner verriete ihm das Token,
        ''' sobald der Empfaenger klickt (Uebernahme des Kontos). Ohne Adresse antwortet /forgot wie
        ''' ohne Mailserver.</summary>
        Friend Shared Function CanSendResetMail() As Boolean
            Return ConfiguredPublicAddress() IsNot Nothing AndAlso MailService.IsConfigured
        End Function

        ''' <summary>Der Anfang eines Links, den der Browser dieser Anfrage selbst angezeigt bekommt
        ''' (Freigaben): die eingetragene oeffentliche Adresse, sonst die Adresse, ueber die der
        ''' Browser gerade kommt (Host bzw. hinter dem vertrauten Proxy X-Forwarded-Host, mit https
        ''' hinter dem Proxy). Diese Anfrage ist schon ueber einen zugesagten Weg gekommen
        ''' (IsAcceptedRoute), also ueber HTTPS oder erlaubtes HTTP.
        '''
        ''' <para>Ohne eingetragene Adresse stammt der Name aus Koepfen, die der Absender setzen
        ''' kann. Das ist hier hinnehmbar, weil der Link an genau diesen Absender zurueckgeht; fuer
        ''' einen Link an jemand anderen (die Mail zum Zuruecksetzen) taugt es nicht, siehe
        ''' <see cref="CanSendResetMail"/>.</para></summary>
        Friend Shared Function RequestLinkBase(request As HttpListenerRequest) As String
            Dim configured = ConfiguredPublicAddress()
            If configured IsNot Nothing Then Return configured
            Dim scheme = If(IsSecureRequest(request), "https", "http")
            Dim host = PublicHost(request)
            If String.IsNullOrEmpty(host) OrElse host.Any(Function(c) Char.IsWhiteSpace(c) OrElse c = "/"c OrElse c = "\"c OrElse c = "@"c) Then
                host = "127.0.0.1"
            End If
            Return scheme & "://" & host
        End Function

        ''' <summary>Taugt eine eingetragene Adresse fuer den Verweis? https immer, http nur, wenn
        ''' die Bruecke ohnehin unverschluesselt laeuft (Heimnetz oder eigener Rechner). Nur
        ''' Schema, Name und Port, kein Pfad, keine Anmeldedaten.</summary>
        Friend Shared Function IsUsablePublicAddress(address As String) As Boolean
            If String.IsNullOrWhiteSpace(address) Then Return False
            Dim parsed As Uri = Nothing
            If Not Uri.TryCreate(address, UriKind.Absolute, parsed) Then Return False
            If parsed.UserInfo.Length > 0 OrElse parsed.AbsolutePath <> "/" OrElse parsed.Query.Length > 0 OrElse parsed.Fragment.Length > 0 Then Return False
            If parsed.Scheme = Uri.UriSchemeHttps Then Return True
            Return parsed.Scheme = Uri.UriSchemeHttp AndAlso Not _tlsProxy
        End Function

        ''' <summary>GET /reset?t=: die Seite fuer das neue Kennwort, im Bild des Anmeldefensters.
        ''' Ein ungueltiger oder abgelaufener Verweis zeigt das gleich.</summary>
        Private Shared Async Function HandleResetPageAsync(context As HttpListenerContext) As Task
            Dim token = If(context.Request.QueryString("t"), "")
            Await ServeLoginPageAsync(context, mobile:=False, modeOverride:=If(PasswordResetService.IsValid(token), "reset", "resetInvalid"))
        End Function

        ''' <summary>POST /reset mit {"token", "password", "repeat"}: setzt das neue Kennwort mit den
        ''' Regeln wie immer. Danach enden alle Sitzungen dieses Benutzers, und dieser Browser ist
        ''' angemeldet. Ein ungueltiger Versuch zaehlt als Fehlversuch (dieselbe Bremse). Hat das
        ''' Konto einen zweiten Faktor, gehoert {"code"} oder {"recovery"} in dieselbe Anfrage; ohne
        ''' ihn antwortet der Weg nur mit "second" und schreibt nichts.</summary>
        Private Shared Async Function HandleResetAsync(context As HttpListenerContext) As Task
            Dim request = context.Request
            If String.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase) Then
                Await HandleResetPageAsync(context)
                Return
            End If
            Dim fields = Await ReadSignInBodyAsync(context)
            If fields Is Nothing Then Return
            If Not AppLockService.HasOwnerPassword Then
                Await WriteLoginAnswerAsync(context, 409, "", 0, setup:=True)
                Return
            End If
            Dim counters = LoginCounterKeys(request)
            Dim wait = AppLockService.RetryAfter(counters)
            If wait > TimeSpan.Zero Then
                Dim seconds = CInt(Math.Ceiling(wait.TotalSeconds))
                context.Response.Headers("Retry-After") = seconds.ToString(CultureInfo.InvariantCulture)
                SecurityLog.Throttled("reset", LogAddress(request), wait)
                Dim throttled = Await Dispatcher.UIThread.InvokeAsync(Function() ViewModels.LockScreenViewModel.ThrottledText(wait))
                Await WriteLoginAnswerAsync(context, 429, throttled, seconds)
                Return
            End If
            Dim token As String = Nothing
            Dim password As String = Nothing
            Dim repeat As String = Nothing
            If Not fields.TryGetValue("token", token) Then token = ""
            If Not fields.TryGetValue("password", password) Then password = ""
            If Not fields.TryGetValue("repeat", repeat) Then repeat = ""
            Dim problems = AppLockService.PasswordProblems(password)
            If problems.Count > 0 OrElse password <> repeat Then
                Dim text = Await Dispatcher.UIThread.InvokeAsync(
                    Function() If(problems.Count > 0, ViewModels.SettingsViewModel.PasswordRuleText(problems(0)),
                                  LocalizationService.T("Die beiden neuen Kennwörter stimmen nicht überein.")))
                Await WriteLoginAnswerAsync(context, 400, text, 0)
                Return
            End If
            ' Erst nachsehen, nicht verbrauchen: bei einem Konto mit zweitem Faktor bleibt der
            ' Verweis gueltig, bis auch der Code stimmt.
            Dim userId = PasswordResetService.Peek(token)
            If userId Is Nothing OrElse Not UserAccountService.IsActive(userId) Then
                AppLockService.RegisterFailedAttempt(counters)
                SecurityLog.Write("password.reset.invalid", SecurityLog.NoValue, LogAddress(request))
                Dim expired = Await Dispatcher.UIThread.InvokeAsync(Function() LocalizationService.T("Der Verweis ist abgelaufen oder wurde schon benutzt."))
                Await WriteLoginAnswerAsync(context, 400, expired, 0)
                Return
            End If
            Dim from = LogAddress(request)
            ' MIT ZWEITEM FAKTOR verlangt das neue Kennwort zugleich einen gueltigen Code (oder
            ' Wiederherstellungscode). Wer nur das Postfach hat, aendert so weder das Kennwort noch
            ' beendet er die Sitzungen des Inhabers. Ohne Code sagt die Antwort nur "second" und
            ' schreibt nichts; ein falscher Code zaehlt auf die Bremse der Codes.
            Dim codeCounters As String() = Nothing
            Dim usedRecovery = False
            If TwoFactorService.IsEnabled(userId) Then
                Dim code As String = Nothing
                Dim recoveryCode As String = Nothing
                If Not fields.TryGetValue("code", code) Then code = ""
                If Not fields.TryGetValue("recovery", recoveryCode) Then recoveryCode = ""
                If code.Trim().Length = 0 AndAlso recoveryCode.Trim().Length = 0 Then
                    Await WriteSecondFactorAnswerAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", False}, {"second", True}})
                    Return
                End If
                codeCounters = SecondFactorCounters(request, userId)
                Dim codeWait = AppLockService.RetryAfter(codeCounters)
                If codeWait > TimeSpan.Zero Then
                    SecurityLog.Throttled("twofactor", from, codeWait)
                    Await WriteThrottledSecondFactorAsync(context, codeWait)
                    Return
                End If
                usedRecovery = recoveryCode.Trim().Length > 0
                Dim checkedId = userId
                Dim accepted = Await Task.Run(Function() If(usedRecovery,
                                                          TwoFactorService.UseRecoveryCode(checkedId, recoveryCode),
                                                          TwoFactorService.VerifyCode(checkedId, code) = TwoFactorOutcome.Accepted))
                If Not accepted Then
                    AppLockService.RegisterFailedAttempt(codeCounters)
                    TwoFactorService.NoteFailed(userId, from, "reset")
                    Dim retry = AppLockService.RetryAfter(codeCounters)
                    Dim answer As New Dictionary(Of String, Object) From {
                        {"ok", False},
                        {"error", If(retry > TimeSpan.Zero, ViewModels.LockScreenViewModel.ThrottledText(retry), LocalizationService.T("Der Code ist falsch."))}}
                    If retry > TimeSpan.Zero Then answer("retry") = CInt(Math.Ceiling(retry.TotalSeconds))
                    Await WriteSecondFactorAnswerAsync(context, 401, answer)
                    Return
                End If
            End If
            ' Jetzt verbrauchen; ging der Verweis inzwischen anders weg, gilt er nicht.
            If Not String.Equals(PasswordResetService.Consume(token), userId, StringComparison.Ordinal) Then
                AppLockService.RegisterFailedAttempt(counters)
                SecurityLog.Write("password.reset.invalid", SecurityLog.NoValue, from)
                Dim expired = Await Dispatcher.UIThread.InvokeAsync(Function() LocalizationService.T("Der Verweis ist abgelaufen oder wurde schon benutzt."))
                Await WriteLoginAnswerAsync(context, 400, expired, 0)
                Return
            End If
            Dim outcome = Await Task.Run(Function() UserAccountService.SetPassword(userId, password))
            If outcome <> UserChangeOutcome.Done Then
                Await WriteLoginAnswerAsync(context, 400, "", 0)
                Return
            End If
            If codeCounters IsNot Nothing Then
                AppLockService.ClearFailedAttempts(codeCounters)
                If usedRecovery Then TwoFactorService.NoteRecoveryUsed(userId, from)
            End If
            Dim value = CreateSession(request, userId)
            context.Response.Headers.Add("Set-Cookie", SessionCookie(value, SessionCookieMaxAgeSeconds(), IsSecureRequest(request)))
            SecurityLog.ResetByLink(userId, from)
            If CanUseDesktop(userId) Then UnlockForApp(userId, from)
            RememberLanguage(context, userId)
            Await WriteLoginAnswerAsync(context, 200, Nothing, 0, target:=TargetFor(userId))
        End Function

        ''' <summary>POST /logout: beendet die Sitzung dieses Browsers, die anderen bleiben.</summary>
        Private Shared Async Function HandleLogoutAsync(context As HttpListenerContext) As Task
            Dim request = context.Request
            If Not String.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) OrElse
               Not IsOwnOrigin(request) Then
                context.Response.StatusCode = 403
                context.Response.Close()
                Return
            End If
            Dim value = ReadSessionCookie(request)
            If value IsNot Nothing Then
                Dim key = SessionKey(value)
                Dim userId = SessionUser(key)
                EndSession(key)
                If userId IsNot Nothing Then SecurityLog.SignedOut(userId, LogAddress(request))
            End If
            context.Response.Headers.Add("Set-Cookie", SessionCookie("", 0, IsSecureRequest(request)))
            Await WriteLoginAnswerAsync(context, 200, Nothing, 0)
        End Function

        ''' <summary>Die Antwort auf Anmelden, Einrichten und Abmelden als JSON. <paramref name="setup"/>
        ''' sagt der Seite, dass noch kein Kennwort gesetzt ist und sie die Einrichtung zeigen soll.
        ''' <paramref name="target"/> ist nach einer Anmeldung die Seite, auf die es geht (der Server
        ''' entscheidet, nicht die Wahl im Fenster), <paramref name="message"/> ein Satz fuer die
        ''' Seite ohne Fehler (Kennwort vergessen).</summary>
        Private Shared Async Function WriteLoginAnswerAsync(context As HttpListenerContext, status As Integer,
                                                           errorText As String, retrySeconds As Integer,
                                                           Optional setup As Boolean = False,
                                                           Optional target As String = Nothing,
                                                           Optional message As String = Nothing) As Task
            Dim answer As New Dictionary(Of String, Object) From {{"ok", errorText Is Nothing}}
            If Not String.IsNullOrEmpty(errorText) Then answer("error") = errorText
            If retrySeconds > 0 Then answer("retry") = retrySeconds
            If setup Then answer("setup") = True
            If Not String.IsNullOrEmpty(target) Then answer("target") = target
            If Not String.IsNullOrEmpty(message) Then answer("message") = message
            Dim bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(answer))
            context.Response.StatusCode = status
            context.Response.ContentType = "application/json; charset=utf-8"
            context.Response.Headers("Cache-Control") = "no-store"
            context.Response.ContentLength64 = bytes.Length
            Await context.Response.OutputStream.WriteAsync(bytes)
            context.Response.Close()
        End Function

        ' --- Das Anmeldefenster als Seite ----------------------------------------------------------

        ''' <summary>Liefert das Anmeldefenster, ohne gesetztes Kennwort die Einrichtung im selben
        ''' Bild. Status 401, aber OHNE WWW-Authenticate: ein Browser zeigt dann den Inhalt statt
        ''' seines Dialogs, ein Werkzeug sieht am Status, dass es nicht angemeldet ist.
        ''' <paramref name="mobile"/> waehlt die mobile Seite vor (Aufruf ueber /m/); sonst waehlt
        ''' die Seite selbst nach der letzten Wahl oder dem Geraet.</summary>
        Private Shared Async Function ServeLoginPageAsync(context As HttpListenerContext, mobile As Boolean,
                                                         Optional modeOverride As String = Nothing) As Task
            If _loginPageText Is Nothing Then _loginPageText = LoadAssetText(LoginPageAsset)
            Dim nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            Dim parts = Await Dispatcher.UIThread.InvokeAsync(Function() LoginPageParts())
            Dim mode = If(Not AppLockService.HasOwnerPassword, "setup", If(modeOverride, PageMode()))
            Dim page = _loginPageText.
                Replace("__FERRUMPIX_NONCE__", nonce, StringComparison.Ordinal).
                Replace("__FERRUMPIX_COLORS__", parts.Colors, StringComparison.Ordinal).
                Replace("__FERRUMPIX_LANG__", parts.Language, StringComparison.Ordinal).
                Replace("__FERRUMPIX_MODE__", mode, StringComparison.Ordinal).
                Replace("__FERRUMPIX_MAIL__", If(CanSendResetMail(), "1", ""), StringComparison.Ordinal).
                Replace("__FERRUMPIX_KEPT_USER__", JsonSerializer.Serialize(If(AppLockService.HasOwnerPassword, "", AppLockService.StoredUserName)), StringComparison.Ordinal).
                Replace("__FERRUMPIX_VARIANT__", If(mobile, "mobile", ""), StringComparison.Ordinal).
                Replace("__FERRUMPIX_LOGIN_TEXTS__", parts.Texts, StringComparison.Ordinal)
            Dim bytes = Encoding.UTF8.GetBytes(page)
            Dim response = context.Response
            response.StatusCode = 401
            response.ContentType = "text/html; charset=utf-8"
            response.Headers("Cache-Control") = "no-store"
            response.Headers("Content-Security-Policy") =
                $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'nonce-{nonce}'; img-src 'self'; connect-src 'self'; " &
                "form-action 'self'; base-uri 'none'; frame-ancestors 'none'"
            response.Headers("X-Content-Type-Options") = "nosniff"
            response.Headers("X-Frame-Options") = "DENY"
            response.Headers("Referrer-Policy") = "no-referrer"
            ' Die Kennung dieses Browsers fuer die Zaehlung seiner Fehlversuche (LoginCounterKeys).
            ' Nur der Server gibt sie aus (unterschrieben); eine fremde oder alte gilt wie keine.
            If ReadLoginCookie(context.Request) Is Nothing Then
                response.Headers.Add("Set-Cookie",
                    $"{LoginCookieName}={NewLoginCookieValue()}; Max-Age=86400; Path=/; HttpOnly; SameSite=Strict" &
                    If(IsSecureRequest(context.Request), "; Secure", ""))
            End If
            response.ContentLength64 = bytes.Length
            Await response.OutputStream.WriteAsync(bytes)
            response.Close()
        End Function

        ''' <summary>Was die Seite zeigt: "setup" ohne Kennwort (Einrichtung), "claim" mit Kennwort,
        ''' aber noch ohne Namen (Anmeldung, die den Namen einmal festlegt), sonst "login".</summary>
        Friend Shared Function PageMode() As String
            If Not AppLockService.HasOwnerPassword Then Return "setup"
            If AppLockService.StoredUserName.Length = 0 Then Return "claim"
            Return "login"
        End Function

        ''' <summary>Das Bild des Anmeldefensters. Das einzige, was es ohne Sitzung ausser der Seite
        ''' gibt; es liegt so schon in der Anwendung und zeigt nichts aus dem Bestand.</summary>
        Private Shared Async Function ServeLoginImageAsync(context As HttpListenerContext) As Task
            If _loginImage Is Nothing Then
                Using stream = AssetLoader.Open(New Uri(LoginImageAsset)), copy As New MemoryStream()
                    stream.CopyTo(copy)
                    _loginImage = copy.ToArray()
                End Using
            End If
            Dim response = context.Response
            response.StatusCode = 200
            response.ContentType = "image/jpeg"
            response.Headers("Cache-Control") = "public, max-age=86400"
            response.Headers("X-Content-Type-Options") = "nosniff"
            response.ContentLength64 = _loginImage.Length
            Await response.OutputStream.WriteAsync(_loginImage)
            response.Close()
        End Function

        Private Shared Function LoadAssetText(asset As String) As String
            Using stream = AssetLoader.Open(New Uri(asset)), reader As New StreamReader(stream, Encoding.UTF8)
                Return reader.ReadToEnd()
            End Using
        End Function

        Private NotInheritable Class LoginParts
            Public Colors As String
            Public Language As String
            Public Texts As String
        End Class

        ''' <summary>Texte und Farben des Anmeldefensters, aus der Sprache und dem Erscheinungsbild,
        ''' die die Anwendung gerade hat. Auf dem Anzeigefaden.</summary>
        Private Shared Function LoginPageParts() As LoginParts
            Dim texts As New Dictionary(Of String, String) From {
                {"title", LocalizationService.T("FerrumPix ist gesperrt.")},
                {"password", LocalizationService.T("Kennwort")},
                {"unlock", LocalizationService.T("Entsperren")},
                {"empty", LocalizationService.T("Bitte das Kennwort eingeben.")},
                {"failed", LocalizationService.T("Die Anmeldung ist fehlgeschlagen.")},
                {"user", LocalizationService.T("Benutzername")},
                {"userEmpty", LocalizationService.T("Bitte den Benutzernamen eingeben.")},
                {"claim", LocalizationService.T("Bitte einmal einen Benutzernamen festlegen. Er gilt ab jetzt zusammen mit dem Kennwort.")},
                {"setupTitle", LocalizationService.T("Willkommen bei FerrumPix. Bitte zuerst Benutzername und Kennwort festlegen.")},
                {"newPassword", LocalizationService.T("Neues Kennwort")},
                {"repeat", LocalizationService.T("Neues Kennwort wiederholen")},
                {"rules", LocalizationService.T("Mindestens 8 Zeichen mit Großbuchstaben, Kleinbuchstaben, Zahl und Sonderzeichen; ein Leerzeichen zählt als Sonderzeichen.")},
                {"setup", LocalizationService.T("Kennwort festlegen")},
                {"setupEmpty", LocalizationService.T("Bitte ein neues Kennwort eingeben.")},
                {"mismatch", LocalizationService.T("Die beiden neuen Kennwörter stimmen nicht überein.")},
                {"view", LocalizationService.T("Ansicht")},
                {"desktop", LocalizationService.T("Desktop")},
                {"desktopHint", LocalizationService.T("Volle Oberfläche")},
                {"mobile", LocalizationService.T("Mobil")},
                {"mobileHint", LocalizationService.T("Vereinfachte Seite")},
                {"email", LocalizationService.T("E-Mail-Adresse")},
                {"forgot", LocalizationService.T("Kennwort vergessen?")},
                {"forgotTitle", LocalizationService.T("Benutzername oder E-Mail-Adresse eingeben. Gibt es dazu ein Konto mit E-Mail-Adresse, kommt ein Verweis für ein neues Kennwort.")},
                {"forgotField", LocalizationService.T("Benutzername oder E-Mail-Adresse")},
                {"forgotSend", LocalizationService.T("Verweis senden")},
                {"admin", LocalizationService.T("Bitte wenden Sie sich an den Administrator.")},
                {"backToLogin", LocalizationService.T("Zurück zur Anmeldung")},
                {"resetTitle", LocalizationService.T("Neues Kennwort festlegen")},
                {"resetInvalid", LocalizationService.T("Der Verweis ist abgelaufen oder wurde schon benutzt.")},
                {"mobileOnly", LocalizationService.T("Die volle Oberfläche gibt es nur für Administratoren. Es geht weiter zur mobilen Seite.")},
                {"secondTitle", LocalizationService.T("Bitte den Code aus der Authenticator-App eingeben.")},
                {"code", LocalizationService.T("Code")},
                {"codeEmpty", LocalizationService.T("Bitte den Code eingeben.")},
                {"confirm", LocalizationService.T("Bestätigen")},
                {"useRecovery", LocalizationService.T("Wiederherstellungscode verwenden")},
                {"useCode", LocalizationService.T("Code aus der App verwenden")},
                {"recoveryCode", LocalizationService.T("Wiederherstellungscode")}}
            Dim colors As New StringBuilder()
            For Each entry In {("root", "FP.Bg.Root", "#0B0E11"), ("elevated", "FP.Bg.Elevated", "#192128"),
                               ("input", "FP.Bg.Input", "#10161B"), ("text", "FP.Text.Primary", "#E7ECF0"),
                               ("danger", "FP.Text.Danger", "#FF6B6B"), ("accent", "FP.Accent", "#F08A1A"),
                               ("border", "FP.Border.Strong", "#34414C"), ("border-normal", "FP.Border.Normal", "#26313B")}
                colors.Append("--").Append(entry.Item1).Append(":"c).Append(ThemeColor(entry.Item2, entry.Item3)).Append(";"c)
            Next
            Dim language = LocalizationService.HtmlLanguage(LocalizationService.TextCulture)
            Return New LoginParts With {
                .Colors = colors.ToString(),
                .Language = language,
                .Texts = JsonSerializer.Serialize(texts)}
        End Function

        ''' <summary>Eine Farbe des Erscheinungsbilds als #RRGGBB. Nur dieses Format kommt in die
        ''' Seite; fehlt die Farbe (Pruefstand ohne Anwendung), gilt die des dunklen Bilds.</summary>
        Private Shared Function ThemeColor(key As String, fallback As String) As String
            Try
                Dim resource As Object = Nothing
                Dim app = Application.Current
                If app IsNot Nothing AndAlso app.TryGetResource(key, app.ActualThemeVariant, resource) Then
                    Dim brush = TryCast(resource, ISolidColorBrush)
                    If brush IsNot Nothing Then
                        Dim c = brush.Color
                        Return $"#{c.R:X2}{c.G:X2}{c.B:X2}"
                    End If
                End If
            Catch
            End Try
            Return fallback
        End Function

        ''' <summary>Vergisst Sitzungen und Anmeldefenster. Aus Stop, fuer den Pruefstand.</summary>
        Private Shared Sub ResetSessions()
            SyncLock Sessions
                Sessions.Clear()
                _basicHeaderKey = ""
                _basicHeaderUser = Nothing
            End SyncLock
            _clientSessionKey = ""
            RemoveSessionHooks()
            PasswordResetService.ResetForTests()
            ForgetSecondSteps(Nothing)
        End Sub

    End Class

End Namespace
