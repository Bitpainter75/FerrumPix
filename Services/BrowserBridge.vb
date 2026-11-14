Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Net.WebSockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Controls.ApplicationLifetimes
Imports Avalonia.Platform
Imports Avalonia.Threading

Namespace Services

    ''' <summary>
    ''' Die Anwendung im Browser, ohne X-Server und ohne fremde Uebertragungsschicht: sie zeichnet
    ''' ohne Bildschirm, und diese Klasse bringt die fertigen Bilder ueber einen WebSocket in ein
    ''' Canvas und die Eingaben von dort zurueck.
    '''
    ''' <para>DREI TEILE, je eine Teildatei: hier der Webserver, die Anmeldung und die Verbindung,
    ''' <c>BrowserBridgeFrames.vb</c> holt die Bilder der Fenster ab, vergleicht sie mit dem zuletzt
    ''' gesendeten Stand und kodiert, was sich geaendert hat, <c>BrowserBridgeInput.vb</c> setzt
    ''' Maus und Tastatur des Browsers in Eingaben der Fenster um.</para>
    '''
    ''' <para>EIN BROWSER ZUR ZEIT. Verbindet sich ein zweiter, loest er den ersten ab: es gibt eine
    ''' Sitzung mit einer Fenstergroesse, und zwei Tabs wuerden sich darum streiten.</para>
    '''
    ''' <para>ZUGANG IN DREI STUFEN. Erstens die Adresse: ab Werk nur der eigene Rechner
    ''' (127.0.0.1). Die Bruecke spricht nur HTTP, unverschluesselt; ueber den eigenen Rechner
    ''' hinaus gibt es sie deshalb nur hinter einem Reverse Proxy, der verschluesselt, und nur auf
    ''' ausdrueckliche Zusage (<see cref="ReadOptions"/>). Zweitens die Anmeldung: ist ein Kennwort
    ''' gesetzt, fragt der Browser mit seinem eigenen Dialog nach Benutzername und Kennwort, bevor
    ''' er die Seite bekommt (HTTP Basic); hinter dem Proxy ist das Pflicht. Drittens die Verbindung: jede ausgelieferte
    ''' Seite traegt eine einmalige Kennung, und der WebSocket nimmt nur eine Verbindung an, die
    ''' diese Kennung vorzeigt und deren Herkunft die eigene Seite ist. Ohne die dritte Stufe
    ''' koennte jede fremde Webseite im selben Browser die Anwendung fernbedienen.</para>
    '''
    ''' <para>WARUM DER DIALOG DES BROWSERS und kein eigenes Formular: er ist in jeder Sprache schon
    ''' da, der Browser merkt sich die Angaben fuer die Sitzung, und die Seite selbst braucht
    ''' keinen einzigen Text.</para>
    ''' </summary>
    Partial Public NotInheritable Class BrowserBridge

        ''' <summary>Startparameter, der die Anwendung ohne Bildschirm startet und die Bruecke
        ''' einschaltet.</summary>
        Public Const StartSwitch As String = "--browser"

        ''' <summary>Startparameter fuer Adresse und Port, etwa <c>--browser-bind=0.0.0.0:14500</c>.</summary>
        Public Const BindSwitch As String = "--browser-bind="

        ''' <summary>Startparameter fuer den Benutzernamen der Anmeldung.</summary>
        Public Const UserSwitch As String = "--browser-user="

        ''' <summary>Startparameter fuer das Kennwort der Anmeldung. Ein Kennwort auf der
        ''' Befehlszeile ist fuer jeden Nutzer des Rechners in der Prozessliste zu sehen; wo das
        ''' stoert, steht es besser in der Umgebung (<see cref="PasswordVariable"/>).</summary>
        Public Const PasswordSwitch As String = "--browser-password="

        ''' <summary>Adresse und Port, etwa <c>0.0.0.0:14500</c>.</summary>
        Public Const BindVariable As String = "FERRUMPIX_BROWSER_BIND"

        Public Const UserVariable As String = "FERRUMPIX_BROWSER_USER"

        Public Const PasswordVariable As String = "FERRUMPIX_BROWSER_PASSWORD"

        ''' <summary>Auf "1" gesetzt, darf die Bruecke an allen Adressen lauschen, mit oder ohne
        ''' Kennwort. Das ist fuer den Behaelter gedacht: darin muss sie an allen Adressen lauschen,
        ''' damit die Portweiterleitung sie erreicht, und nach aussen gibt erst die Weiterleitung
        ''' den Port frei. Angenommen werden dann nur Anfragen, die der Browser an den eigenen
        ''' Rechner gerichtet hat (127.0.0.1 oder localhost), siehe <see cref="IsLoopbackHost"/>.</summary>
        Public Const LocalForwardVariable As String = "FERRUMPIX_BROWSER_LOCAL_FORWARD"

        ''' <summary>Startparameter: vor der Bruecke steht ein Reverse Proxy, der verschluesselt.
        ''' Ohne diese Zusage (oder <see cref="TlsProxyVariable"/> auf "1") lauscht die Bruecke an
        ''' keiner Adresse ausser der eigenen, siehe <see cref="ReadOptions"/>.</summary>
        Public Const TlsProxySwitch As String = "--browser-tls-proxy"

        Public Const TlsProxyVariable As String = "FERRUMPIX_BROWSER_TLS_PROXY"

        ''' <summary>Startparameter: die Adressen, von denen aus der Proxy die Bruecke anspricht,
        ''' mit Komma getrennt. Noetig, wenn der Proxy auf einem ANDEREN Rechner steht; einer auf
        ''' demselben Rechner kommt ueber 127.0.0.1 und gilt ohne Angabe. Siehe
        ''' <see cref="IsTrustedSender"/>.</summary>
        Public Const ProxyAddressSwitch As String = "--browser-proxy-address="

        Public Const ProxyAddressVariable As String = "FERRUMPIX_BROWSER_PROXY_ADDRESS"

        Private Const DefaultBind As String = "127.0.0.1:14500"

        Private Const PageAsset As String = "avares://FerrumPix/Assets/BrowserBridge/index.html"

        ''' <summary>Die Stelle in der Seite, an der die Kennung der Verbindung eingesetzt wird.</summary>
        Private Const TokenPlaceholder As String = "__FERRUMPIX_TOKEN__"

        Private Const Realm As String = "FerrumPix"

        ''' <summary>So viele Kennungen bleiben gueltig. Jedes Laden der Seite erzeugt eine neue;
        ''' die aelteste faellt dann heraus.</summary>
        Private Const MaxTokens As Integer = 16

        ''' <summary>Eine Nachricht des Browsers ist ein kurzer JSON-Text. Was groesser ist, ist
        ''' keine Eingabe.</summary>
        Private Const MaxMessageBytes As Integer = 64 * 1024

        Private Shared _listener As HttpListener
        Private Shared _client As WebSocket
        Private Shared ReadOnly ClientLock As New Object()
        Private Shared ReadOnly SendGate As New SemaphoreSlim(1, 1)
        Private Shared _pageText As String
        Private Shared _user As String = ""
        Private Shared _password As String = ""
        Private Shared _loopbackHostOnly As Boolean
        Private Shared _tlsProxy As Boolean
        Private Shared _trustEverySender As Boolean
        Private Shared _proxyAddresses As IReadOnlyList(Of IPAddress) = Array.Empty(Of IPAddress)()
        Private Shared ReadOnly Tokens As New List(Of String)()

        ''' <summary>Woher die Liste der offenen Fenster kommt. Ab Werk aus der Anwendung; der
        ''' Pruefstand setzt ein eigenes Fenster ein, weil er keine Anwendung startet.</summary>
        Friend Shared WindowSource As Func(Of IReadOnlyList(Of Window)) = AddressOf ApplicationWindows

        Private Sub New()
        End Sub

        ''' <summary>Laeuft die Anwendung im Browser? Dann fuellt das Hauptfenster den Tab und
        ''' gehoert dem Browser: Fensterknoepfe, Verschieben, Groesse ziehen und der Doppelklick
        ''' auf die Kopfleiste haben dort nichts zu bedeuten, und Lage und Groesse des Fensters
        ''' werden nicht in die Einstellungen geschrieben, sonst kaeme das Fenster beim naechsten
        ''' gewoehnlichen Start in der Groesse des Tabs wieder.
        '''
        ''' Gesetzt nur beim Start ueber den Schalter, nicht vom Pruefstand, der die Bruecke an
        ''' einem Testfenster betreibt.</summary>
        Public Shared Property IsActive As Boolean

        Public Shared Function IsRequested(args As String()) As Boolean
            Return args IsNot Nothing AndAlso
                   args.Any(Function(a) String.Equals(a, StartSwitch, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Ob ein Startparameter der Bruecke gehoert. Die Anwendung selbst bekommt diese
        ''' nicht zu sehen, sie hielte den ersten sonst fuer eine Datei.</summary>
        Public Shared Function IsOwnArgument(argument As String) As Boolean
            If argument Is Nothing Then Return False
            Return String.Equals(argument, StartSwitch, StringComparison.OrdinalIgnoreCase) OrElse
                   argument.StartsWith(StartSwitch & "-", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Startet Webserver und Bildabholung. Adresse, Benutzername und Kennwort kommen
        ''' aus den Startparametern, ersatzweise aus der Umgebung. Gerufen, sobald das Toolkit steht.</summary>
        Public Shared Sub Start(args As String())
            Dim problem As String = Nothing
            Dim options = ReadOptions(args, problem)
            If options Is Nothing Then Throw New InvalidOperationException(problem)
            IsActive = True
            Start(options)
        End Sub

        ''' <summary>Prueft die Angaben fuer die Bruecke, bevor irgendetwas startet. Nothing heisst
        ''' in Ordnung, sonst steht hier der Satz fuer die Konsole. Main ruft das vor dem Aufbau
        ''' des Toolkits: eine falsche Adresse soll mit einer Zeile enden und nicht mit einer
        ''' Aufrufkette mitten im Start.</summary>
        Public Shared Function CheckArguments(args As String()) As String
            Dim problem As String = Nothing
            ReadOptions(args, problem)
            Return problem
        End Function

        Private Shared Function Setting(args As String(), prefix As String, variable As String) As String
            Dim argument = args?.FirstOrDefault(Function(a) a IsNot Nothing AndAlso a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            If argument IsNot Nothing Then Return argument.Substring(prefix.Length)
            Return Environment.GetEnvironmentVariable(variable)
        End Function

        ''' <summary>Was fuer einen Lauf der Bruecke feststeht: wo sie lauscht, wer sich anmelden
        ''' darf und unter welcher Zusage sie ueber den eigenen Rechner hinaus erreichbar ist.</summary>
        Friend NotInheritable Class BridgeOptions
            Public Host As String = "127.0.0.1"
            Public Port As Integer = 14500
            Public User As String = ""
            Public Password As String = ""
            Public LocalForward As Boolean
            Public TlsProxy As Boolean
            Public ProxyAddresses As New List(Of IPAddress)()

            Public ReadOnly Property IsLoopback As Boolean
                Get
                    Return Host = "127.0.0.1" OrElse String.Equals(Host, "localhost", StringComparison.OrdinalIgnoreCase)
                End Get
            End Property
        End Class

        ''' <summary>Liest Adresse, Anmeldung und Zusagen aus Startparametern und Umgebung und
        ''' prueft sie. Liefert Nothing und den Grund in <paramref name="problem"/>, wenn etwas
        ''' nicht stimmt.
        '''
        ''' <para>DIE VERBINDUNG IST UNVERSCHLUESSELT. Die Bruecke spricht nur HTTP, und die
        ''' Anmeldung des Browsers schickt Name und Kennwort dabei lesbar mit, bei jeder Anfrage.
        ''' Im eigenen Rechner bleibt das unter sich. Ueber ein Netz kann es jeder mitlesen, der an
        ''' der Leitung sitzt, samt allem, was ueber die Verbindung geht. Eine Adresse ausser der
        ''' eigenen gibt es deshalb nur unter einer von zwei Zusagen:</para>
        '''
        ''' <para><see cref="LocalForwardVariable"/>: der Port ist nur am eigenen Rechner
        ''' freigegeben (Behaelter). Angenommen wird dann nur, was der Browser an 127.0.0.1 oder
        ''' localhost gerichtet hat.</para>
        '''
        ''' <para><see cref="TlsProxySwitch"/>: davor steht ein Reverse Proxy, der verschluesselt.
        ''' Angenommen wird dann nur, was VOM PROXY KOMMT (<see cref="IsTrustedSender"/>: vom
        ''' eigenen Rechner oder von einer mit <see cref="ProxyAddressSwitch"/> genannten Adresse)
        ''' und was er als verschluesselt angenommen kennzeichnet (X-Forwarded-Proto: https). Wer
        ''' den Port am Proxy vorbei aufruft, wird abgewiesen, BEVOR der Browser nach Name und
        ''' Kennwort fragt. Ein Kennwort ist hier Pflicht. Lauscht die Bruecke dabei an einer
        ''' Adresse im Netz, muss die Adresse des Proxys genannt sein.</para></summary>
        Friend Shared Function ReadOptions(args As String(), ByRef problem As String) As BridgeOptions
            problem = Nothing
            Dim options As New BridgeOptions With {
                .User = If(Setting(args, UserSwitch, UserVariable), ""),
                .Password = If(Setting(args, PasswordSwitch, PasswordVariable), ""),
                .LocalForward = Environment.GetEnvironmentVariable(LocalForwardVariable) = "1",
                .TlsProxy = Environment.GetEnvironmentVariable(TlsProxyVariable) = "1" OrElse
                            (args IsNot Nothing AndAlso args.Any(Function(a) String.Equals(a, TlsProxySwitch, StringComparison.OrdinalIgnoreCase)))}

            Dim bind = Setting(args, BindSwitch, BindVariable)
            If String.IsNullOrWhiteSpace(bind) Then bind = DefaultBind
            bind = bind.Trim()
            Dim separator = bind.LastIndexOf(":"c)
            If separator <= 0 OrElse separator = bind.Length - 1 Then
                problem = $"The address ""{bind}"" needs a host and a port, for example 127.0.0.1:14500 ({BindSwitch} or {BindVariable})."
                Return Nothing
            End If
            options.Host = bind.Substring(0, separator)
            Dim portText = bind.Substring(separator + 1)
            If Not Integer.TryParse(portText, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture, options.Port) OrElse
               options.Port < 1 OrElse options.Port > 65535 Then
                problem = $"""{portText}"" is not a port. It has to be a number from 1 to 65535 ({BindSwitch} or {BindVariable})."
                Return Nothing
            End If

            If options.User.Length > 0 AndAlso options.Password.Length = 0 Then
                problem = $"A user name for the browser needs a password as well ({PasswordSwitch} or {PasswordVariable})."
                Return Nothing
            End If
            Dim proxyAddresses = Setting(args, ProxyAddressSwitch, ProxyAddressVariable)
            If Not String.IsNullOrWhiteSpace(proxyAddresses) Then
                For Each part In proxyAddresses.Split(","c, StringSplitOptions.RemoveEmptyEntries Or StringSplitOptions.TrimEntries)
                    Dim address As IPAddress = Nothing
                    If Not IPAddress.TryParse(part, address) Then
                        problem = $"""{part}"" is not an IP address ({ProxyAddressSwitch} or {ProxyAddressVariable})."
                        Return Nothing
                    End If
                    options.ProxyAddresses.Add(address)
                Next
                If Not options.TlsProxy Then
                    problem = $"The address of a proxy only means something together with {TlsProxySwitch}."
                    Return Nothing
                End If
            End If

            ' Hinter einem Proxy ist die Bruecke von aussen erreichbar, gleich an welcher Adresse
            ' sie selbst lauscht: steht der Proxy auf demselben Rechner, ist das 127.0.0.1.
            If options.TlsProxy Then
                If options.Password.Length = 0 Then
                    problem = $"Behind a reverse proxy FerrumPix can be reached from other computers and needs a password ({PasswordSwitch} or {PasswordVariable})."
                    Return Nothing
                End If
                ' An einer Adresse im Netz erreicht JEDER den Port, nicht nur der Proxy. Die Bruecke
                ' muss dann wissen, welcher Absender der Proxy ist; ohne das wiese sie alle ab.
                If Not options.IsLoopback AndAlso Not options.LocalForward AndAlso options.ProxyAddresses.Count = 0 Then
                    problem = $"The address {bind} can be reached by every computer in the network, not only by the proxy. " &
                              $"Name the proxy with {ProxyAddressSwitch}, or use 127.0.0.1 when the proxy runs on this computer."
                    Return Nothing
                End If
                Return options
            End If
            If options.LocalForward Then Return options
            If options.IsLoopback Then Return options
            problem = $"The address {bind} can be reached from other computers, and the connection is not encrypted: " &
                      "user name, password and everything shown would be readable on the network. " &
                      $"Put a reverse proxy with TLS in front and start with {TlsProxySwitch}, or stay on 127.0.0.1."
            Return Nothing
        End Function

        ''' <summary>Fuer den Pruefstand: am eigenen Rechner, mit Anmeldung.</summary>
        Friend Shared Sub Start(bind As String, user As String, password As String)
            Dim separator = bind.LastIndexOf(":"c)
            Start(New BridgeOptions With {
                .Host = bind.Substring(0, separator),
                .Port = Integer.Parse(bind.Substring(separator + 1), Globalization.CultureInfo.InvariantCulture),
                .User = If(user, ""), .Password = If(password, "")})
        End Sub

        Private Shared Sub Start(options As BridgeOptions)
            If _listener IsNot Nothing Then Return
            _user = options.User
            _password = options.Password
            _tlsProxy = options.TlsProxy
            _loopbackHostOnly = options.LocalForward AndAlso Not options.TlsProxy AndAlso Not options.IsLoopback
            ' Im Behaelter kommt jede Anfrage von der Adresse der Portweiterleitung, gleich wer
            ' sie geschickt hat: der Absender sagt dort nichts. Die Zusage "nur am eigenen Rechner
            ' freigegeben" tritt an seine Stelle.
            _trustEverySender = options.LocalForward
            _proxyAddresses = options.ProxyAddresses.ToArray()
            Dim host = options.Host
            Dim port = options.Port
            Dim local = options.IsLoopback

            _pageText = LoadPage()
            Dim listener As New HttpListener()
            If local Then
                ' Beide Schreibweisen: der Listener ordnet eine Anfrage ueber ihren Host-Kopf zu,
                ' und der Browser schickt, was in der Adresszeile steht.
                listener.Prefixes.Add($"http://127.0.0.1:{port}/")
                listener.Prefixes.Add($"http://localhost:{port}/")
            ElseIf host = "0.0.0.0" OrElse host = "*" OrElse host = "+" Then
                listener.Prefixes.Add($"http://+:{port}/")
            Else
                listener.Prefixes.Add($"http://{host}:{port}/")
            End If
            ' Die Anmeldung gilt der SEITE. Der WebSocket bleibt davon ausgenommen: nicht jeder
            ' Browser schickt die Anmeldung dort mit. Er weist sich ueber die Kennung aus, die nur
            ' eine angemeldet geladene Seite bekommt.
            listener.Realm = Realm
            '
            ' Eine Anfrage, die gar nicht angenommen wird (am Proxy vorbei, an einen fremden Namen
            ' gerichtet), bekommt KEINE Aufforderung zur Anmeldung: der Browser schickte Name und
            ' Kennwort sonst genau ueber den Weg, der sie nicht sehen soll.
            listener.AuthenticationSchemeSelectorDelegate =
                Function(request)
                    If _password.Length > 0 AndAlso request.Url.AbsolutePath <> "/ws" AndAlso
                       IsAcceptedRoute(request) Then Return AuthenticationSchemes.Basic
                    Return AuthenticationSchemes.Anonymous
                End Function
            listener.Start()
            _listener = listener

            Console.Out.WriteLine($"FerrumPix in the browser: http://{If(local, "127.0.0.1", host)}:{port}")
            Dim ignored = Task.Run(AddressOf AcceptLoopAsync)
            Dispatcher.UIThread.Post(AddressOf StartFramePump)
        End Sub

        ''' <summary>Haelt Webserver und Bildabholung an. Fuer den Pruefstand, die Anwendung endet
        ''' mit dem Prozess.</summary>
        Friend Shared Sub [Stop]()
            StopFramePump()
            Dim listener = _listener
            _listener = Nothing
            Try
                listener?.Close()
            Catch
            End Try
            ReplaceClient(Nothing)
            SyncLock Tokens
                Tokens.Clear()
            End SyncLock
            WindowSource = AddressOf ApplicationWindows
        End Sub

        Private Shared Function LoadPage() As String
            Using stream = AssetLoader.Open(New Uri(PageAsset)), reader As New StreamReader(stream, Encoding.UTF8)
                Return reader.ReadToEnd()
            End Using
        End Function

        Private Shared Function ApplicationWindows() As IReadOnlyList(Of Window)
            Dim desktop = TryCast(Application.Current?.ApplicationLifetime, IClassicDesktopStyleApplicationLifetime)
            If desktop Is Nothing Then Return Array.Empty(Of Window)()
            Return desktop.Windows
        End Function

        Private Shared Async Function AcceptLoopAsync() As Task
            Dim listener = _listener
            While listener IsNot Nothing AndAlso listener.IsListening
                Dim context As HttpListenerContext = Nothing
                Try
                    context = Await listener.GetContextAsync()
                Catch
                    ' Der Listener wurde geschlossen.
                    Return
                End Try
                Dim ignored = HandleContextAsync(context)
            End While
        End Function

        Private Shared Async Function HandleContextAsync(context As HttpListenerContext) As Task
            Try
                Dim path = context.Request.Url.AbsolutePath
                If Not IsAcceptedRoute(context.Request) Then
                    Dim hint = Encoding.UTF8.GetBytes(
                        If(_tlsProxy, "FerrumPix is only available through its encrypted address.",
                                      "FerrumPix is only available at 127.0.0.1 or localhost."))
                    context.Response.StatusCode = 403
                    context.Response.ContentType = "text/plain; charset=utf-8"
                    context.Response.ContentLength64 = hint.Length
                    Await context.Response.OutputStream.WriteAsync(hint)
                    context.Response.Close()
                    Return
                End If
                If context.Request.IsWebSocketRequest AndAlso path = "/ws" Then
                    If Not IsOwnOrigin(context.Request) Then
                        context.Response.StatusCode = 403
                        context.Response.Close()
                        Return
                    End If
                    Dim socketContext = Await context.AcceptWebSocketAsync(Nothing)
                    Await RunClientAsync(socketContext.WebSocket)
                    Return
                End If

                If path <> "/" AndAlso path <> "/index.html" Then
                    context.Response.StatusCode = 404
                    context.Response.Close()
                    Return
                End If

                If Not IsSignedIn(context) Then
                    ' Eine falsche Angabe kostet eine Sekunde, damit sich Kennwoerter nicht
                    ' durchprobieren lassen. Danach fragt der Browser erneut.
                    Await Task.Delay(1000)
                    context.Response.StatusCode = 401
                    context.Response.Headers("WWW-Authenticate") = $"Basic realm=""{Realm}"", charset=""UTF-8"""
                    context.Response.Close()
                    Return
                End If

                Dim page = Encoding.UTF8.GetBytes(_pageText.Replace(TokenPlaceholder, IssueToken(), StringComparison.Ordinal))
                context.Response.ContentType = "text/html; charset=utf-8"
                context.Response.Headers("Cache-Control") = "no-store"
                context.Response.ContentLength64 = page.Length
                Await context.Response.OutputStream.WriteAsync(page)
                context.Response.Close()
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.Request", ex)
            End Try
        End Function

        ''' <summary>Ob die Anfrage nach der Seite angemeldet ist. Ohne gesetztes Kennwort gibt es
        ''' keine Anmeldung. Ohne gesetzten Benutzernamen zaehlt nur das Kennwort; der Dialog des
        ''' Browsers fragt trotzdem nach einem Namen, und der ist dann beliebig.</summary>
        Private Shared Function IsSignedIn(context As HttpListenerContext) As Boolean
            If _password.Length = 0 Then Return True
            Dim identity = TryCast(context.User?.Identity, HttpListenerBasicIdentity)
            If identity Is Nothing Then Return False
            Return CredentialsMatch(identity.Name, identity.Password)
        End Function

        Friend Shared Function CredentialsMatch(user As String, password As String) As Boolean
            ' Beide Vergleiche laufen immer und in fester Zeit, damit die Dauer der Antwort nicht
            ' verraet, welche Haelfte stimmte.
            Dim passwordMatches = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(If(password, "")), Encoding.UTF8.GetBytes(_password))
            Dim userMatches = _user.Length = 0 OrElse CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(If(user, "")), Encoding.UTF8.GetBytes(_user))
            Return passwordMatches And userMatches
        End Function

        ''' <summary>Eine neue Kennung fuer eine ausgelieferte Seite: 32 zufaellige Bytes.</summary>
        Private Shared Function IssueToken() As String
            Dim token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            SyncLock Tokens
                Tokens.Add(token)
                If Tokens.Count > MaxTokens Then Tokens.RemoveAt(0)
            End SyncLock
            Return token
        End Function

        Private Shared Function IsKnownToken(token As String) As Boolean
            If String.IsNullOrEmpty(token) Then Return False
            Dim given = Encoding.ASCII.GetBytes(token)
            Dim known = False
            SyncLock Tokens
                For Each candidate In Tokens
                    If CryptographicOperations.FixedTimeEquals(given, Encoding.ASCII.GetBytes(candidate)) Then known = True
                Next
            End SyncLock
            Return known
        End Function

        ''' <summary>Die Herkunft der Seite, die den WebSocket oeffnet, muss dieser Server selbst
        ''' sein. Ein Browser schickt den Kopf immer mit und laesst ihn sich von keiner Seite
        ''' faelschen; eine fremde Seite scheitert deshalb hier.</summary>
        Friend Shared Function IsOwnOrigin(request As HttpListenerRequest) As Boolean
            Return IsOwnOrigin(request.Headers("Origin"), PublicHost(request))
        End Function

        ''' <summary>Kommt die Anfrage ueber den Weg, der zugesagt ist? Am eigenen Rechner immer.
        ''' Mit der Zusage "nur am eigenen Rechner freigegeben" nur, wenn der Browser sie an
        ''' 127.0.0.1 oder localhost gerichtet hat. Hinter einem Proxy nur, wenn der sie als
        ''' verschluesselt angenommen kennzeichnet.</summary>
        Private Shared Function IsAcceptedRoute(request As HttpListenerRequest) As Boolean
            If _loopbackHostOnly Then Return IsLoopbackHost(request.Headers("Host"))
            If _tlsProxy Then
                Return IsTrustedSender(request.RemoteEndPoint?.Address, _trustEverySender, _proxyAddresses) AndAlso
                       IsForwardedAsHttps(request.Headers("X-Forwarded-Proto"))
            End If
            Return True
        End Function

        ''' <summary>Kommt die Anfrage vom Proxy? Entschieden wird das an der ABSENDERADRESSE der
        ''' Verbindung, nicht an einem Kopf.
        '''
        ''' <para>X-Forwarded-Proto und X-Forwarded-Host kann jeder setzen, der den Port erreicht.
        ''' Sie sind eine Auskunft des Proxys und kein Nachweis, dass er es war: wer die Bruecke
        ''' direkt anspricht und die Koepfe selbst mitschickt, saehe sonst aus wie der Proxy. Die
        ''' Absenderadresse einer TCP-Verbindung laesst sich so nicht vortaeuschen. Den Koepfen
        ''' wird deshalb nur geglaubt, wenn sie von dort kommen, wo der Proxy steht: vom eigenen
        ''' Rechner, oder von einer der genannten Adressen.</para>
        '''
        ''' <para>Im Behaelter (<paramref name="trustEverySender"/>) traegt jede Verbindung die
        ''' Adresse der Portweiterleitung. Dort schuetzt allein, dass der Port nur am eigenen
        ''' Rechner freigegeben ist.</para></summary>
        Friend Shared Function IsTrustedSender(sender As IPAddress, trustEverySender As Boolean,
                                               proxyAddresses As IReadOnlyList(Of IPAddress)) As Boolean
            If trustEverySender Then Return True
            If sender Is Nothing Then Return False
            ' Eine IPv4-Adresse kann als IPv6-Schreibweise ankommen (::ffff:192.168.1.2).
            Dim address = If(sender.IsIPv4MappedToIPv6, sender.MapToIPv4(), sender)
            If IPAddress.IsLoopback(address) Then Return True
            If proxyAddresses Is Nothing Then Return False
            For Each proxy In proxyAddresses
                Dim known = If(proxy.IsIPv4MappedToIPv6, proxy.MapToIPv4(), proxy)
                If known.Equals(address) Then Return True
            Next
            Return False
        End Function

        Friend Shared Function IsForwardedAsHttps(forwardedProto As String) As Boolean
            If String.IsNullOrEmpty(forwardedProto) Then Return False
            ' Bei mehreren Proxys hintereinander steht je einer in der Liste; der erste Eintrag ist
            ' der Weg des Browsers.
            Dim first = forwardedProto.Split(","c)(0).Trim()
            Return String.Equals(first, "https", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Der Name, unter dem der BROWSER die Seite kennt. Hinter einem Proxy steht er in
        ''' X-Forwarded-Host, wenn der Proxy den Host-Kopf auf die innere Adresse umschreibt; die
        ''' Herkunft der Seite wird gegen diesen Namen geprueft.</summary>
        Private Shared Function PublicHost(request As HttpListenerRequest) As String
            If _tlsProxy Then
                Dim forwarded = request.Headers("X-Forwarded-Host")
                If Not String.IsNullOrEmpty(forwarded) Then Return forwarded.Split(","c)(0).Trim()
            End If
            Return request.Headers("Host")
        End Function

        ''' <summary>Ob der Browser die Anfrage an den eigenen Rechner gerichtet hat. Ohne Kennwort
        ''' ist das ein Teil des Schutzes: eine fremde Seite kann ihren eigenen Namen auf
        ''' 127.0.0.1 zeigen lassen, dann stimmen Herkunft und Host ueberein, aber der Host ist ihr
        ''' Name und nicht der des eigenen Rechners.</summary>
        Friend Shared Function IsLoopbackHost(host As String) As Boolean
            If String.IsNullOrEmpty(host) Then Return False
            Dim name = host
            Dim portSeparator = name.LastIndexOf(":"c)
            If portSeparator > 0 AndAlso Not name.EndsWith("]", StringComparison.Ordinal) Then name = name.Substring(0, portSeparator)
            Return name = "127.0.0.1" OrElse name = "[::1]" OrElse
                   String.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
        End Function

        Friend Shared Function IsOwnOrigin(origin As String, host As String) As Boolean
            If String.IsNullOrEmpty(origin) OrElse String.IsNullOrEmpty(host) Then Return False
            Dim parsed As Uri = Nothing
            If Not Uri.TryCreate(origin, UriKind.Absolute, parsed) Then Return False
            Return String.Equals(parsed.Authority, host, StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Async Function RunClientAsync(socket As WebSocket) As Task
            Dim accepted = False
            Try
                While socket.State = WebSocketState.Open
                    Dim text = Await ReceiveTextAsync(socket)
                    If text Is Nothing Then Exit While

                    Dim message As JsonElement
                    Using document = JsonDocument.Parse(text)
                        message = document.RootElement.Clone()
                    End Using

                    If Not accepted Then
                        If ReadString(message, "t") <> "hello" OrElse Not IsKnownToken(ReadString(message, "token")) Then
                            ' Die Seite ist aelter als dieser Lauf der Anwendung oder gar nicht
                            ' von hier. Eine echte Seite laedt sich daraufhin neu und bekommt, nach
                            ' der Anmeldung, eine frische Kennung.
                            Await SendRawAsync(socket, Encoding.UTF8.GetBytes("{""t"":""reload""}"), WebSocketMessageType.Text)
                            Exit While
                        End If
                        accepted = True
                        ReplaceClient(socket)
                        ' Was vom abgeloesten Browser noch wartet, gehoert nicht in diese Sitzung.
                        ClearInput()
                        Dispatcher.UIThread.Post(Sub() OnClientConnected(message))
                    Else
                        EnqueueInput(message)
                    End If
                End While
            Catch ex As Exception When TypeOf ex Is WebSocketException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is JsonException OrElse TypeOf ex Is OperationCanceledException
                ' Die Verbindung ist weg oder hat Unlesbares geschickt. Beides beendet sie.
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.Client", ex)
            Finally
                SyncLock ClientLock
                    If _client Is socket Then _client = Nothing
                End SyncLock
                Try
                    socket.Abort()
                    socket.Dispose()
                Catch
                End Try
            End Try
        End Function

        Private Shared Async Function ReceiveTextAsync(socket As WebSocket) As Task(Of String)
            Dim buffer(8191) As Byte
            Using collected As New MemoryStream()
                Do
                    Dim result = Await socket.ReceiveAsync(New ArraySegment(Of Byte)(buffer), CancellationToken.None)
                    If result.MessageType = WebSocketMessageType.Close Then Return Nothing
                    collected.Write(buffer, 0, result.Count)
                    If collected.Length > MaxMessageBytes Then Return Nothing
                    If result.EndOfMessage Then
                        If result.MessageType <> WebSocketMessageType.Text Then Return Nothing
                        Return Encoding.UTF8.GetString(collected.GetBuffer(), 0, CInt(collected.Length))
                    End If
                Loop
            End Using
        End Function

        Private Shared Sub ReplaceClient(socket As WebSocket)
            Dim previous As WebSocket
            SyncLock ClientLock
                previous = _client
                _client = socket
            End SyncLock
            If previous IsNot Nothing AndAlso previous IsNot socket Then
                Try
                    previous.Abort()
                Catch
                End Try
            End If
        End Sub

        Private Shared ReadOnly Property CurrentClient As WebSocket
            Get
                SyncLock ClientLock
                    Return _client
                End SyncLock
            End Get
        End Property

        ''' <summary>Schickt eine Nachricht an den verbundenen Browser. Ein WebSocket vertraegt nur
        ''' einen Sender zur Zeit, deshalb das Tor.</summary>
        Private Shared Async Function SendAsync(payload As Byte(), kind As WebSocketMessageType) As Task
            Dim socket = CurrentClient
            If socket Is Nothing Then Return
            Await SendRawAsync(socket, payload, kind)
        End Function

        Private Shared Async Function SendRawAsync(socket As WebSocket, payload As Byte(), kind As WebSocketMessageType) As Task
            Await SendGate.WaitAsync()
            Try
                If socket.State = WebSocketState.Open Then
                    Await socket.SendAsync(New ArraySegment(Of Byte)(payload), kind, True, CancellationToken.None)
                End If
            Catch ex As Exception When TypeOf ex Is WebSocketException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                ' Der Browser ist weg; die Empfangsschleife raeumt auf.
            Finally
                SendGate.Release()
            End Try
        End Function

        Private Shared Function SendTextAsync(json As String) As Task
            Return SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text)
        End Function

        Private Shared Function ReadString(message As JsonElement, name As String) As String
            Dim value As JsonElement
            If message.ValueKind = JsonValueKind.Object AndAlso message.TryGetProperty(name, value) AndAlso
               value.ValueKind = JsonValueKind.String Then
                Return value.GetString()
            End If
            Return ""
        End Function

        Private Shared Function ReadNumber(message As JsonElement, name As String, fallback As Double) As Double
            Dim value As JsonElement
            Dim number As Double
            If message.ValueKind = JsonValueKind.Object AndAlso message.TryGetProperty(name, value) AndAlso
               value.ValueKind = JsonValueKind.Number AndAlso value.TryGetDouble(number) AndAlso
               Not Double.IsNaN(number) AndAlso Not Double.IsInfinity(number) Then
                Return number
            End If
            Return fallback
        End Function
    End Class
End Namespace
