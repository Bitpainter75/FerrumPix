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
        Friend Const LogoutRoute As String = "/logout"
        Friend Const LoginImageRoute As String = "/login/welcome.jpg"

        Private Const LoginPageAsset As String = "avares://FerrumPix/Assets/BrowserBridge/login.html"
        Private Const LoginImageAsset As String = "avares://FerrumPix/Assets/Welcome.jpg"

        ''' <summary>Ohne eingestellte Sperrzeit endet eine Sitzung nach so langer Zeit ohne Nutzung.</summary>
        Friend Shared ReadOnly DefaultSessionLifetime As TimeSpan = TimeSpan.FromDays(30)

        ''' <summary>Hoechstens so viele Sitzungen zugleich; die am laengsten unbenutzte faellt heraus.</summary>
        Private Const MaxSessions As Integer = 64

        Private Const MaxLoginBodyBytes As Integer = 4096

        Private NotInheritable Class BrowserSession
            Public LastUsedUtc As DateTime
            Public Sender As String
        End Class

        ''' <summary>Die Sitzungen, nach dem SHA-256 des Cookie-Werts. Der Wert selbst steht nur im
        ''' Browser.</summary>
        Private Shared ReadOnly Sessions As New Dictionary(Of String, BrowserSession)(StringComparer.Ordinal)

        ''' <summary>Die Sitzung, mit der der verbundene WebSocket angemeldet ist, sonst leer.</summary>
        Private Shared _clientSessionKey As String = ""

        ''' <summary>Der zuletzt gelungene Basic-Kopf eines Werkzeugs (als Hash samt Marke des
        ''' Kennworts) und bis wann er ohne neue Pruefung gilt.</summary>
        Private Shared _basicHeaderKey As String = ""
        Private Shared _basicHeaderUntilUtc As DateTime

        Private Shared _loginPageText As String
        Private Shared _loginImage As Byte()
        Private Shared _lockHandler As EventHandler

        ''' <summary>Ob die Bruecke ueber den eigenen Rechner hinaus erreichbar ist (Proxy oder
        ''' Heimnetz). Dann ist ein Kennwort Pflicht, und die Einstellungen lassen es nicht
        ''' abschalten.</summary>
        Public Shared ReadOnly Property RequiresPassword As Boolean
            Get
                Return _listener IsNot Nothing AndAlso (_tlsProxy OrElse _allowHttp)
            End Get
        End Property

        ''' <summary>Braucht ein Browser eine Sitzung? Nur wenn ein Kennwort gesetzt ist; ohne
        ''' Kennwort ist die Bruecke nur am eigenen Rechner erreichbar (ReadOptions).</summary>
        Private Shared ReadOnly Property NeedsSignIn As Boolean
            Get
                Return AppLockService.IsProtected
            End Get
        End Property

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
            If key.Length = 0 Then Return Not NeedsSignIn
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
        Private Shared Function CreateSession(sender As String) As String
            Dim value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            Dim now = AppLockService.Clock()
            SyncLock Sessions
                If Sessions.Count >= MaxSessions Then
                    Dim oldest = Sessions.OrderBy(Function(pair) pair.Value.LastUsedUtc).First().Key
                    Sessions.Remove(oldest)
                End If
                Sessions(SessionKey(value)) = New BrowserSession With {.LastUsedUtc = now, .Sender = sender}
            End SyncLock
            Return value
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

        ''' <summary>Ob die Anfrage angemeldet ist: ohne Kennwort immer, sonst mit gueltiger
        ''' Sitzung oder (fuer Werkzeuge) mit "Authorization: Basic".</summary>
        Private Shared Function IsSignedIn(context As HttpListenerContext) As Boolean
            If Not NeedsSignIn Then Return True
            If ValidSessionKey(context.Request) IsNot Nothing Then Return True
            Return IsSignedInByBasic(context.Request)
        End Function

        ''' <summary>Die Anmeldung eines Werkzeugs, aus dem Kopf Authorization gelesen. Ein Browser
        ''' schickt den Kopf nur, wenn er vorher dazu aufgefordert wurde, und das geschieht nicht
        ''' mehr. Fehlversuche zaehlen wie im Anmeldefenster.</summary>
        Private Shared Function IsSignedInByBasic(request As HttpListenerRequest) As Boolean
            Dim header = request.Headers("Authorization")
            If String.IsNullOrEmpty(header) OrElse Not header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) Then Return False
            ' Ein Werkzeug schickt den Kopf bei jeder Anfrage; die Pruefung ist mit Absicht langsam.
            ' Der zuletzt gelungene Kopf gilt deshalb eine Minute ohne neue Rechnung, solange das
            ' Kennwort dasselbe ist.
            Dim headerKey = SessionKey(header) & "|" & AppLockService.PasswordStamp()
            Dim now = AppLockService.Clock()
            SyncLock Sessions
                If String.Equals(headerKey, _basicHeaderKey, StringComparison.Ordinal) AndAlso now < _basicHeaderUntilUtc Then Return True
            End SyncLock
            Dim decoded As String
            Try
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header.Substring(6).Trim()))
            Catch ex As FormatException
                Return False
            End Try
            Dim separator = decoded.IndexOf(":"c)
            If separator < 0 Then Return False
            If Not CredentialsMatch(decoded.Substring(0, separator), decoded.Substring(separator + 1), SenderKey(request)) Then Return False
            SyncLock Sessions
                _basicHeaderKey = headerKey
                _basicHeaderUntilUtc = now.AddMinutes(1)
            End SyncLock
            Return True
        End Function

        Friend Shared Function CredentialsMatch(user As String, password As String) As Boolean
            Return CredentialsMatch(user, password, AppLockService.LocalKey)
        End Function

        ''' <summary>Name und Kennwort eines Werkzeugs. Der Name zaehlt nur, wenn beim Start einer
        ''' gesetzt wurde; beide Vergleiche laufen immer.</summary>
        Private Shared Function CredentialsMatch(user As String, password As String, sender As String) As Boolean
            Dim result = AppLockService.CheckPassword(If(password, ""), sender)
            Dim passwordMatches = result.Outcome = PasswordCheckOutcome.Accepted
            Dim userMatches = _user.Length = 0 OrElse CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(If(user, "")), Encoding.UTF8.GetBytes(_user))
            Return passwordMatches And userMatches
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
            Dim value = ReadHexCookie(request, LoginCookieName)
            If value Is Nothing Then Return {address}
            Return {"login " & SessionKey(value).Substring(0, 16), address}
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

        ''' <summary>POST /login mit {"password": "..."}. Bei Erfolg: Sitzung, Cookie und die
        ''' Anwendung ist entsperrt, fuer alle Browser mit Sitzung.</summary>
        Private Shared Async Function HandleLoginAsync(context As HttpListenerContext) As Task
            Dim request = context.Request
            If Not String.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase) Then
                context.Response.StatusCode = 405
                context.Response.Close()
                Return
            End If
            If Not IsOwnOrigin(request) Then
                context.Response.StatusCode = 403
                context.Response.Close()
                Return
            End If
            Dim contentType = If(request.ContentType, "")
            If Not contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) Then
                context.Response.StatusCode = 415
                context.Response.Close()
                Return
            End If
            If request.ContentLength64 < 0 OrElse request.ContentLength64 > MaxLoginBodyBytes Then
                context.Response.StatusCode = 413
                context.Response.Close()
                Return
            End If
            Dim password As String = ""
            Try
                Dim buffer(CInt(request.ContentLength64) - 1) As Byte
                Dim read = 0
                While read < buffer.Length
                    Dim count = Await request.InputStream.ReadAsync(buffer, read, buffer.Length - read)
                    If count <= 0 Then Exit While
                    read += count
                End While
                Using document = JsonDocument.Parse(New ReadOnlyMemory(Of Byte)(buffer, 0, read))
                    Dim value As JsonElement
                    If document.RootElement.ValueKind = JsonValueKind.Object AndAlso
                       document.RootElement.TryGetProperty("password", value) AndAlso
                       value.ValueKind = JsonValueKind.String Then password = value.GetString()
                End Using
            Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is IOException
                context.Response.StatusCode = 400
                context.Response.Close()
                Return
            End Try

            If Not NeedsSignIn Then
                Await WriteLoginAnswerAsync(context, 200, Nothing, 0)
                Return
            End If
            Dim sender = SenderKey(request)
            Dim counters = LoginCounterKeys(request)
            Dim result = Await Task.Run(Function() AppLockService.CheckPassword(password, counters))
            Select Case result.Outcome
                Case PasswordCheckOutcome.Accepted, PasswordCheckOutcome.NotProtected
                    Dim value = CreateSession(sender)
                    context.Response.Headers.Add("Set-Cookie", SessionCookie(value, SessionCookieMaxAgeSeconds(), IsSecureRequest(request)))
                    AppLockService.Unlock()
                    DiagnosticLogService.LogAlways("BrowserBridge", $"Signed in from {sender}.")
                    Await WriteLoginAnswerAsync(context, 200, Nothing, 0)
                Case PasswordCheckOutcome.Throttled
                    Dim seconds = CInt(Math.Ceiling(result.RetryAfter.TotalSeconds))
                    context.Response.Headers("Retry-After") = seconds.ToString(CultureInfo.InvariantCulture)
                    Dim text = Await Dispatcher.UIThread.InvokeAsync(Function() ViewModels.LockScreenViewModel.ThrottledText(result.RetryAfter))
                    Await WriteLoginAnswerAsync(context, 429, text, seconds)
                Case Else
                    Dim text = Await Dispatcher.UIThread.InvokeAsync(
                        Function() If(result.RetryAfter > TimeSpan.Zero,
                                      ViewModels.LockScreenViewModel.ThrottledText(result.RetryAfter),
                                      LocalizationService.T("Das Kennwort ist falsch.")))
                    Await WriteLoginAnswerAsync(context, 401, text, 0)
            End Select
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
            If value IsNot Nothing Then EndSession(SessionKey(value))
            context.Response.Headers.Add("Set-Cookie", SessionCookie("", 0, IsSecureRequest(request)))
            Await WriteLoginAnswerAsync(context, 200, Nothing, 0)
        End Function

        Private Shared Async Function WriteLoginAnswerAsync(context As HttpListenerContext, status As Integer,
                                                           errorText As String, retrySeconds As Integer) As Task
            Dim answer As New Dictionary(Of String, Object) From {{"ok", errorText Is Nothing}}
            If errorText IsNot Nothing Then answer("error") = errorText
            If retrySeconds > 0 Then answer("retry") = retrySeconds
            Dim bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(answer))
            context.Response.StatusCode = status
            context.Response.ContentType = "application/json; charset=utf-8"
            context.Response.Headers("Cache-Control") = "no-store"
            context.Response.ContentLength64 = bytes.Length
            Await context.Response.OutputStream.WriteAsync(bytes)
            context.Response.Close()
        End Function

        ' --- Das Anmeldefenster als Seite ----------------------------------------------------------

        ''' <summary>Liefert das Anmeldefenster. Status 401, aber OHNE WWW-Authenticate: ein Browser
        ''' zeigt dann den Inhalt statt seines Dialogs, ein Werkzeug sieht am Status, dass es nicht
        ''' angemeldet ist.</summary>
        Private Shared Async Function ServeLoginPageAsync(context As HttpListenerContext) As Task
            If _loginPageText Is Nothing Then _loginPageText = LoadAssetText(LoginPageAsset)
            Dim nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            Dim parts = Await Dispatcher.UIThread.InvokeAsync(Function() LoginPageParts())
            Dim page = _loginPageText.
                Replace("__FERRUMPIX_NONCE__", nonce, StringComparison.Ordinal).
                Replace("__FERRUMPIX_COLORS__", parts.Colors, StringComparison.Ordinal).
                Replace("__FERRUMPIX_LANG__", parts.Language, StringComparison.Ordinal).
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
            If ReadHexCookie(context.Request, LoginCookieName) Is Nothing Then
                response.Headers.Add("Set-Cookie",
                    $"{LoginCookieName}={Convert.ToHexString(RandomNumberGenerator.GetBytes(32))}; Max-Age=86400; Path=/; HttpOnly; SameSite=Strict" &
                    If(IsSecureRequest(context.Request), "; Secure", ""))
            End If
            response.ContentLength64 = bytes.Length
            Await response.OutputStream.WriteAsync(bytes)
            response.Close()
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
                {"failed", LocalizationService.T("Die Anmeldung ist fehlgeschlagen.")}}
            Dim colors As New StringBuilder()
            For Each entry In {("root", "FP.Bg.Root", "#0B0E11"), ("elevated", "FP.Bg.Elevated", "#192128"),
                               ("input", "FP.Bg.Input", "#10161B"), ("text", "FP.Text.Primary", "#E7ECF0"),
                               ("danger", "FP.Text.Danger", "#FF6B6B"), ("accent", "FP.Accent", "#F08A1A"),
                               ("border", "FP.Border.Strong", "#34414C"), ("border-normal", "FP.Border.Normal", "#26313B")}
                colors.Append("--").Append(entry.Item1).Append(":"c).Append(ThemeColor(entry.Item2, entry.Item3)).Append(";"c)
            Next
            Dim language = Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            If language.Length = 0 OrElse Not language.All(Function(c) Char.IsAsciiLetterLower(c)) Then language = "en"
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
            End SyncLock
            _clientSessionKey = ""
            RemoveSessionHooks()
        End Sub

    End Class

End Namespace
