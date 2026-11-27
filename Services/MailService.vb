Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Net.Security
Imports System.Net.Sockets
Imports System.Security.Authentication
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Wie die Verbindung zum Mailserver verschluesselt ist.</summary>
    Public Enum MailSecurity
        ''' <summary>Unverschluesselt verbinden, dann mit STARTTLS umschalten (meist Port 587).
        ''' Bietet der Server STARTTLS nicht an, wird nichts gesendet.</summary>
        StartTls
        ''' <summary>Von Anfang an verschluesselt (meist Port 465).</summary>
        Ssl
        ''' <summary>Ohne Verschluesselung. Nur fuer einen Server im eigenen Netz; Kennwort und
        ''' Nachricht sind auf der Leitung lesbar.</summary>
        None
    End Enum

    ''' <summary>Die Angaben fuer den Mailversand, aus den Einstellungen gelesen.</summary>
    Public NotInheritable Class MailSettings
        Public Property Host As String = ""
        Public Property Port As Integer = 587
        Public Property Security As MailSecurity = MailSecurity.StartTls
        Public Property UserName As String = ""
        Public Property Password As String = ""
        ''' <summary>Der Absender, wie er eingetragen ist: eine nackte Adresse
        ''' (fotos@example.org), eine mit Anzeigenamen (FerrumPix Fotos &lt;fotos@example.org&gt;)
        ''' oder leer. Leer gilt der Benutzername, wenn er eine E-Mail-Adresse ist.</summary>
        Public Property Sender As String = ""

        ''' <summary>Der Name im Kopf From, wenn der Absender keinen eigenen traegt.</summary>
        Public Const DefaultSenderName As String = "FerrumPix"

        ''' <summary>Die Adresse, von der gesendet wird (MAIL FROM und From), oder leer, wenn es
        ''' keine gibt: der Absender, ersatzweise der Benutzername des Mailkontos.</summary>
        Public ReadOnly Property SenderAddress As String
            Get
                Dim address = "", name = ""
                If Sender.Trim().Length > 0 Then
                    Return If(TryParseSender(Sender, address, name), address, "")
                End If
                Dim user = UserName.Trim()
                Return If(user.Length > 0 AndAlso UserAccountService.IsValidEmail(user), user, "")
            End Get
        End Property

        ''' <summary>Der Anzeigename im Kopf From: der des Absenders, sonst "FerrumPix".</summary>
        Public ReadOnly Property SenderName As String
            Get
                Dim address = "", name = ""
                If TryParseSender(Sender, address, name) AndAlso name.Length > 0 Then Return name
                Return DefaultSenderName
            End Get
        End Property

        ''' <summary>Genug angegeben, um zu senden: Server, Port und eine Absenderadresse.</summary>
        Public ReadOnly Property IsComplete As Boolean
            Get
                Return Host.Trim().Length > 0 AndAlso Port > 0 AndAlso Port <= 65535 AndAlso
                       SenderAddress.Length > 0
            End Get
        End Property

        ''' <summary>Zerlegt einen Absender in Adresse und Anzeigenamen. Angenommen werden
        ''' "fotos@example.org", "Name &lt;fotos@example.org&gt;" und "&quot;Name&quot;
        ''' &lt;fotos@example.org&gt;", mit Leerzeichen am Rand, beliebiger Gross- und
        ''' Kleinschreibung und Umlauten in der Domain. Steuerzeichen (ein Zeilenumbruch schriebe
        ''' sonst eigene Kopfzeilen in die Nachricht) und eine Adresse ohne Punkt in der Domain
        ''' werden abgelehnt. Der Name ist leer, wenn keiner angegeben ist.</summary>
        Public Shared Function TryParseSender(text As String, ByRef address As String, ByRef displayName As String) As Boolean
            address = ""
            displayName = ""
            Dim trimmed = If(text, "").Trim()
            If trimmed.Length = 0 OrElse trimmed.Length > 320 OrElse trimmed.Any(Function(c) Char.IsControl(c)) Then Return False
            Dim parsed As Net.Mail.MailAddress = Nothing
            If Not Net.Mail.MailAddress.TryCreate(trimmed, parsed) Then Return False
            If parsed.Address.Length = 0 OrElse Not UserAccountService.IsValidEmail(parsed.Address) Then Return False
            address = parsed.Address
            displayName = If(parsed.DisplayName, "").Trim()
            Return True
        End Function

        Public Shared Function FromAppSettings(settings As AppSettings) As MailSettings
            Dim security As MailSecurity
            If Not [Enum].TryParse(If(settings.MailSmtpSecurity, ""), ignoreCase:=True, result:=security) Then security = MailSecurity.StartTls
            Return New MailSettings With {
                .Host = If(settings.MailSmtpHost, "").Trim(), .Port = settings.MailSmtpPort, .Security = security,
                .UserName = If(settings.MailSmtpUserName, "").Trim(), .Password = If(settings.MailSmtpPassword, ""),
                .Sender = If(settings.MailSenderAddress, "").Trim()}
        End Function
    End Class

    ''' <summary>
    ''' Versendet einfache Nachrichten ueber einen SMTP-Server (Kennwort vergessen, Testnachricht).
    '''
    ''' <para>EIN EIGENER, KLEINER CLIENT statt System.Net.Mail.SmtpClient: der kann nur STARTTLS
    ''' und kein von Anfang an verschluesseltes SMTP (Port 465), und Microsoft raet in der Doku von
    ''' ihm ab. MailKit koennte beides, waere aber eine neue Abhaengigkeit fuer eine Handvoll
    ''' Befehle. Gebraucht werden nur EHLO, STARTTLS, AUTH PLAIN bzw. LOGIN, MAIL FROM, RCPT TO und
    ''' DATA; die Nachricht ist Text und HTML nach einer gemeinsamen Vorlage (MailTemplate) in
    ''' UTF-8 (Base64), der Betreff nach RFC 2047.</para>
    '''
    ''' <para>Das Zertifikat des Servers wird wie bei jeder TLS-Verbindung von .NET geprueft; ein
    ''' ungueltiges bricht ab. Ohne Verschluesselung meldet der Client sich nur an, wenn der Server
    ''' am eigenen Rechner oder im Heimnetz steht.</para>
    ''' </summary>
    Public NotInheritable Class MailService

        Private Sub New()
        End Sub

        Private Shared ReadOnly Timeout As TimeSpan = TimeSpan.FromSeconds(30)

        ''' <summary>Nur fuer den Pruefstand: zaehlt gesendete Nachrichten.</summary>
        Friend Shared SentCount As Integer

        ''' <summary>Ist ein Mailserver eingerichtet?</summary>
        Public Shared ReadOnly Property IsConfigured As Boolean
            Get
                Return MailSettings.FromAppSettings(AppSettingsService.Load()).IsComplete
            End Get
        End Property

        ''' <summary>Sendet eine Nachricht. Wirft bei jedem Fehler eine Ausnahme mit dem Grund (die
        ''' Antwort des Servers, wo es eine gibt).</summary>
        Public Shared Async Function SendAsync(settings As MailSettings, recipient As String, subject As String, body As MailContent,
                                               Optional cancellationToken As CancellationToken = Nothing) As Task
            If settings Is Nothing OrElse Not settings.IsComplete Then Throw New InvalidOperationException("The mail server is not set up.")
            If Not UserAccountService.IsValidEmail(recipient) OrElse String.IsNullOrWhiteSpace(recipient) Then Throw New ArgumentException("recipient")
            Using timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                timeoutSource.CancelAfter(Timeout)
                Dim token = timeoutSource.Token
                Using client As New TcpClient()
                    Await client.ConnectAsync(settings.Host, settings.Port, token)
                    Dim stream As Stream = client.GetStream()
                    Dim secure = False
                    If settings.Security = MailSecurity.Ssl Then
                        stream = Await StartTlsAsync(stream, settings.Host, token)
                        secure = True
                    End If
                    Dim session As New SmtpConversation(stream)
                    Await session.ExpectAsync(220, token)
                    Dim features = Await session.CommandAsync("EHLO " & LocalName(), 250, token)
                    If settings.Security = MailSecurity.StartTls Then
                        If Not features.Any(Function(l) l.StartsWith("STARTTLS", StringComparison.OrdinalIgnoreCase)) Then
                            Throw New IOException("The mail server does not offer STARTTLS.")
                        End If
                        Await session.CommandAsync("STARTTLS", 220, token)
                        stream = Await StartTlsAsync(stream, settings.Host, token)
                        secure = True
                        session = New SmtpConversation(stream)
                        features = Await session.CommandAsync("EHLO " & LocalName(), 250, token)
                    End If
                    If settings.UserName.Length > 0 Then
                        If Not secure AndAlso Not IsLocalHost(settings.Host) Then
                            Throw New IOException("Refusing to send the mail password unencrypted to a server outside the home network.")
                        End If
                        Dim auth = features.FirstOrDefault(Function(l) l.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase))
                        If auth IsNot Nothing AndAlso auth.IndexOf("PLAIN", StringComparison.OrdinalIgnoreCase) >= 0 Then
                            Dim plain = Convert.ToBase64String(Encoding.UTF8.GetBytes(ChrW(0) & settings.UserName & ChrW(0) & settings.Password))
                            Await session.CommandAsync("AUTH PLAIN " & plain, 235, token)
                        Else
                            Await session.CommandAsync("AUTH LOGIN", 334, token)
                            Await session.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.UserName)), 334, token)
                            Await session.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(settings.Password)), 235, token)
                        End If
                    End If
                    Dim fromAddress = AsciiDomain(settings.SenderAddress)
                    Dim toAddress = AsciiDomain(recipient.Trim())
                    Await session.CommandAsync($"MAIL FROM:<{fromAddress}>", 250, token)
                    Await session.CommandAsync($"RCPT TO:<{toAddress}>", 250, token)
                    Await session.CommandAsync("DATA", 354, token)
                    Await session.SendRawAsync(ComposeMessage(settings.SenderName, fromAddress, toAddress, subject, body), token)
                    Await session.ExpectAsync(250, token)
                    Try
                        Await session.CommandAsync("QUIT", 221, token)
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is OperationCanceledException
                        ' Die Nachricht ist angenommen; ein Abschied, der nicht klappt, aendert daran nichts.
                    End Try
                End Using
            End Using
            Interlocked.Increment(SentCount)
        End Function

        Private Shared Async Function StartTlsAsync(inner As Stream, host As String, token As CancellationToken) As Task(Of Stream)
            Dim tls As New SslStream(inner, leaveInnerStreamOpen:=False)
            Await tls.AuthenticateAsClientAsync(New SslClientAuthenticationOptions With {
                .TargetHost = host,
                .EnabledSslProtocols = SslProtocols.None}, token)
            Return tls
        End Function

        ''' <summary>Steht der Mailserver am eigenen Rechner oder im Heimnetz? Nur dorthin geht ein
        ''' Kennwort auch ohne Verschluesselung.</summary>
        Private Shared Function IsLocalHost(host As String) As Boolean
            If String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) Then Return True
            Dim address As Net.IPAddress = Nothing
            Return Net.IPAddress.TryParse(host, address) AndAlso BrowserBridge.IsPrivateAddress(address)
        End Function

        Private Shared Function LocalName() As String
            Dim name = Environment.MachineName
            If String.IsNullOrEmpty(name) OrElse Not name.All(Function(c) Char.IsAsciiLetterOrDigit(c) OrElse c = "-"c OrElse c = "."c) Then Return "ferrumpix"
            Return name
        End Function

        ''' <summary>Eine Adresse mit der Domain in ASCII (Punycode): ein Server ohne SMTPUTF8
        ''' nimmt Umlaute in der Domain sonst nicht an. Der Teil vor dem @ bleibt, wie er ist.</summary>
        Friend Shared Function AsciiDomain(address As String) As String
            Dim at = address.LastIndexOf("@"c)
            If at <= 0 OrElse address.Skip(at + 1).All(Function(c) AscW(c) < 128) Then Return address
            Try
                Return address.Substring(0, at + 1) & New IdnMapping().GetAscii(address.Substring(at + 1))
            Catch ex As ArgumentException
                Return address
            End Try
        End Function

        ''' <summary>Der Anzeigename fuer den Kopf From. Buchstaben, Ziffern und Leerzeichen in
        ''' ASCII stehen so da, anderes ASCII in Anfuehrungszeichen, alles mit Zeichen jenseits
        ''' von ASCII als kodierte Woerter nach RFC 2047 (UTF-8, Base64), je Wort hoechstens 45
        ''' Bytes, damit keines ueber 75 Zeichen kommt und kein Zeichen geteilt wird.</summary>
        Friend Shared Function EncodeDisplayName(name As String) As String
            Dim text = If(name, "").Trim()
            If text.Length = 0 Then text = MailSettings.DefaultSenderName
            If text.All(Function(c) Char.IsAsciiLetterOrDigit(c) OrElse c = " "c) Then Return text
            If text.All(Function(c) AscW(c) >= 32 AndAlso AscW(c) < 127) Then
                Return """" & text.Replace("\", "\\").Replace("""", "\""") & """"
            End If
            Return String.Join(" ", EncodeWords(text))
        End Function

        ''' <summary>Ein Text als kodierte Woerter nach RFC 2047 (UTF-8, Base64), je Wort hoechstens
        ''' 45 Bytes: so bleibt jedes unter 75 Zeichen, und kein Zeichen wird geteilt. Fuer den
        ''' Betreff und den Anzeigenamen; ein Leser setzt aufeinanderfolgende Woerter ohne den
        ''' Zwischenraum wieder zusammen.</summary>
        Friend Shared Function EncodeWords(text As String) As List(Of String)
            Dim words As New List(Of String)()
            If String.IsNullOrEmpty(text) Then
                words.Add("=?UTF-8?B??=")
                Return words
            End If
            Dim chunk As New StringBuilder()
            Dim i = 0
            While i < text.Length
                ' Ein Ersatzpaar ist EIN Zeichen und bleibt zusammen.
                Dim length = If(Char.IsSurrogatePair(text, i), 2, 1)
                Dim piece = text.Substring(i, length)
                If chunk.Length > 0 AndAlso Encoding.UTF8.GetByteCount(chunk.ToString() & piece) > 45 Then
                    words.Add("=?UTF-8?B?" & Convert.ToBase64String(Encoding.UTF8.GetBytes(chunk.ToString())) & "?=")
                    chunk.Clear()
                End If
                chunk.Append(piece)
                i += length
            End While
            If chunk.Length > 0 Then words.Add("=?UTF-8?B?" & Convert.ToBase64String(Encoding.UTF8.GetBytes(chunk.ToString())) & "?=")
            Return words
        End Function

        ''' <summary>Die Nachricht als Text fuer DATA: Kopfzeilen, Leerzeile, Teile, am Ende die
        ''' Zeile mit dem Punkt.
        '''
        ''' <para>AUFBAU: multipart/alternative mit text/plain als Rueckfall und text/html; hat das
        ''' Logo geladen werden koennen, steckt das HTML mit ihm in multipart/related und zeigt es
        ''' ueber seine Content-ID. Jeder Teil ist Base64 in Zeilen zu 76 Zeichen, damit bleibt jede
        ''' Zeile weit unter 998 Zeichen, ein Punkt am Zeilenanfang kommt nicht vor, und 8BITMIME
        ''' braucht es nicht. Die Message-ID traegt die Domain des Absenders.</para></summary>
        Friend Shared Function ComposeMessage(senderName As String, sender As String, recipient As String, subject As String, content As MailContent) As String
            content = If(content, New MailContent())
            Dim at = sender.LastIndexOf("@"c)
            Dim domain = If(at >= 0, sender.Substring(at + 1), "ferrumpix")
            Dim outer = "fp-alt-" & Guid.NewGuid().ToString("N")
            Dim related = "fp-rel-" & Guid.NewGuid().ToString("N")
            Dim logo = LogoPng()
            Dim logoId = "logo." & Guid.NewGuid().ToString("N") & "@" & domain

            Dim text As New StringBuilder()
            text.Append("From: ").Append(EncodeDisplayName(senderName)).Append(" <").Append(sender).Append(">"c).Append(vbCrLf)
            text.Append("To: <").Append(recipient).Append(">"c).Append(vbCrLf)
            text.Append("Subject: ").Append(String.Join(vbCrLf & " ", EncodeWords(If(subject, "")))).Append(vbCrLf)
            Dim now = DateTimeOffset.Now
            text.Append("Date: ").Append(now.ToString("ddd, dd MMM yyyy HH:mm:ss ", CultureInfo.InvariantCulture)).
                Append(now.ToString("zzz", CultureInfo.InvariantCulture).Replace(":", "")).Append(vbCrLf)
            text.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append("@"c).Append(domain).Append(">"c).Append(vbCrLf)
            text.Append("MIME-Version: 1.0").Append(vbCrLf)
            text.Append("Auto-Submitted: auto-generated").Append(vbCrLf)
            text.Append("Content-Type: multipart/alternative; boundary=""").Append(outer).Append(""""c).Append(vbCrLf)
            text.Append(vbCrLf)

            text.Append("--").Append(outer).Append(vbCrLf)
            AppendPart(text, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(MailTemplate.PlainText(content)))

            Dim html = Encoding.UTF8.GetBytes(MailTemplate.Html(content, If(logo Is Nothing, Nothing, logoId)))
            text.Append("--").Append(outer).Append(vbCrLf)
            If logo Is Nothing Then
                AppendPart(text, "text/html; charset=utf-8", html)
            Else
                text.Append("Content-Type: multipart/related; boundary=""").Append(related).Append(""""c).Append(vbCrLf)
                text.Append(vbCrLf)
                text.Append("--").Append(related).Append(vbCrLf)
                AppendPart(text, "text/html; charset=utf-8", html)
                text.Append("--").Append(related).Append(vbCrLf)
                AppendPart(text, "image/png", logo, "Content-ID: <" & logoId & ">" & vbCrLf &
                                                    "Content-Disposition: inline; filename=""ferrumpix.png""" & vbCrLf)
                text.Append("--").Append(related).Append("--").Append(vbCrLf)
            End If
            text.Append("--").Append(outer).Append("--").Append(vbCrLf)
            text.Append("."c).Append(vbCrLf)
            Return text.ToString()
        End Function

        ''' <summary>Ein Teil der Nachricht: Kopf, Leerzeile, Inhalt als Base64 in Zeilen zu 76
        ''' Zeichen.</summary>
        Private Shared Sub AppendPart(text As StringBuilder, contentType As String, bytes As Byte(), Optional extraHeaders As String = "")
            text.Append("Content-Type: ").Append(contentType).Append(vbCrLf)
            text.Append("Content-Transfer-Encoding: base64").Append(vbCrLf)
            text.Append(extraHeaders)
            text.Append(vbCrLf)
            Dim encoded = Convert.ToBase64String(bytes)
            For i = 0 To encoded.Length - 1 Step 76
                text.Append(encoded, i, Math.Min(76, encoded.Length - i)).Append(vbCrLf)
            Next
        End Sub

        Private Shared ReadOnly LogoGate As New Object()
        Private Shared _logoLoaded As Boolean
        Private Shared _logo As Byte()

        ''' <summary>Das Programmsymbol fuer den Kopf der Nachricht, 96 Punkte (angezeigt in 48, scharf
        ''' auf dichten Schirmen), einmal gerechnet und dann gehalten. Nothing, wenn es sich nicht
        ''' laden laesst (dann kommt die Nachricht ohne Bild).</summary>
        Friend Shared Function LogoPng() As Byte()
            SyncLock LogoGate
                If _logoLoaded Then Return _logo
                _logoLoaded = True
                Try
                    Dim source As Byte()
                    Using stream = Avalonia.Platform.AssetLoader.Open(New Uri("avares://FerrumPix/Assets/FerrumPix_Icon.png")), copy As New MemoryStream()
                        stream.CopyTo(copy)
                        source = copy.ToArray()
                    End Using
                    Using decoded = SkiaSharp.SKBitmap.Decode(source)
                        Using scaled = decoded.Resize(New SkiaSharp.SKImageInfo(96, 96, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Premul),
                                                      New SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear))
                            _logo = PngEncoder.EncodeToBytes(scaled, PngPurpose.Export)
                        End Using
                    End Using
                Catch ex As Exception
                    DiagnosticLogService.LogException("Mail.Logo", ex)
                    _logo = Nothing
                End Try
                Return _logo
            End SyncLock
        End Function

        ''' <summary>Ein Gespraech mit dem Server: Befehl schicken, Antwort lesen (auch mehrzeilig),
        ''' den erwarteten Code pruefen.</summary>
        Private NotInheritable Class SmtpConversation
            Private ReadOnly _stream As Stream
            Private ReadOnly _buffer As New List(Of Byte)()

            Public Sub New(stream As Stream)
                _stream = stream
            End Sub

            Public Async Function SendRawAsync(text As String, token As CancellationToken) As Task
                Dim bytes = Encoding.UTF8.GetBytes(text)
                Await _stream.WriteAsync(bytes, token)
                Await _stream.FlushAsync(token)
            End Function

            Public Async Function CommandAsync(command As String, expected As Integer, token As CancellationToken) As Task(Of List(Of String))
                Await SendRawAsync(command & vbCrLf, token)
                Return Await ExpectAsync(expected, token)
            End Function

            ''' <summary>Liest eine Antwort und liefert ihre Zeilen ohne Code; wirft, wenn der Code
            ''' nicht der erwartete ist.</summary>
            Public Async Function ExpectAsync(expected As Integer, token As CancellationToken) As Task(Of List(Of String))
                Dim lines As New List(Of String)()
                Dim code = 0
                Do
                    Dim line = Await ReadLineAsync(token)
                    If line Is Nothing Then Throw New IOException("The mail server closed the connection.")
                    If line.Length < 3 OrElse Not Integer.TryParse(line.Substring(0, 3), NumberStyles.None, CultureInfo.InvariantCulture, code) Then
                        Throw New IOException("Unexpected answer from the mail server: " & line)
                    End If
                    lines.Add(If(line.Length > 4, line.Substring(4), ""))
                    If line.Length < 4 OrElse line(3) <> "-"c Then Exit Do
                    If lines.Count > 100 Then Throw New IOException("The mail server answers too long.")
                Loop
                If code <> expected Then Throw New IOException($"The mail server answered {code}: {String.Join(" ", lines)}")
                Return lines
            End Function

            Private Async Function ReadLineAsync(token As CancellationToken) As Task(Of String)
                Do
                    Dim newline = _buffer.IndexOf(CByte(10))
                    If newline >= 0 Then
                        Dim line = Encoding.UTF8.GetString(_buffer.GetRange(0, newline).ToArray()).TrimEnd(ChrW(13))
                        _buffer.RemoveRange(0, newline + 1)
                        Return line
                    End If
                    If _buffer.Count > 4096 Then Throw New IOException("The mail server sent a line that is too long.")
                    Dim chunk(1023) As Byte
                    Dim read = Await _stream.ReadAsync(chunk.AsMemory(0, chunk.Length), token)
                    If read <= 0 Then Return Nothing
                    For i = 0 To read - 1
                        _buffer.Add(chunk(i))
                    Next
                Loop
            End Function
        End Class

    End Class

End Namespace
