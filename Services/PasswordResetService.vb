Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>
    ''' "Kennwort vergessen" im Browser: ein Verweis per E-Mail, der genau einmal ein neues Kennwort
    ''' setzen laesst.
    '''
    ''' <para>DER VERWEIS traegt 32 zufaellige Bytes. Gemerkt wird nur ihr SHA-256, im Speicher und
    ''' nirgends sonst: nach einem Neustart gilt kein Verweis mehr, und in keiner Datei und keiner
    ''' Sicherung steht etwas, das sich benutzen liesse. Er gilt 30 Minuten, nur einmal, und jede
    ''' Anmeldung des Benutzers, jedes neue Kennwort, Sperren und Loeschen machen ihn ungueltig
    ''' (<see cref="Forget"/>). Ein neuer Verweis ersetzt den alten.</para>
    '''
    ''' <para>DIE ANTWORT VERRAET NICHTS: ob es das Konto gibt, ob es eine E-Mail-Adresse hat und ob
    ''' die Bremse je Konto gegriffen hat, sieht der Anfragende nicht. Vor der Antwort laeuft nur
    ''' die Bremse je Absender (<see cref="Admit"/>); alles, was vom Konto abhaengt (Suche,
    ''' Verweis, Texte, Protokoll, Versand), laeuft erst danach im Hintergrund
    ''' (<see cref="SendInBackground"/>). Die Antwort braucht deshalb fuer jedes Konto dieselbe
    ''' Zeit.</para>
    '''
    ''' <para>ZWEI BREMSEN: je Browser und je Absenderadresse hoechstens
    ''' <see cref="MaxRequestsPerSender"/> Anfragen in der Stunde (dann 429, unabhaengig vom Konto),
    ''' und je Konto hoechstens <see cref="MaxMailsPerAccount"/> Nachrichten in der Stunde (dann
    ''' still keine weitere), damit niemand ein Postfach mit Nachrichten fuellt.</para>
    ''' </summary>
    Public NotInheritable Class PasswordResetService

        Private Sub New()
        End Sub

        Public Shared ReadOnly TokenLifetime As TimeSpan = TimeSpan.FromMinutes(30)
        Public Const MaxMailsPerAccount As Integer = 3
        Public Const MaxRequestsPerSender As Integer = 10
        Private Shared ReadOnly Window As TimeSpan = TimeSpan.FromHours(1)
        Private Const MaxTrackedKeys As Integer = 512

        Private NotInheritable Class PendingReset
            Public UserId As String
            Public ExpiresUtc As DateTime
        End Class

        Private Shared ReadOnly Gate As New Object()
        ''' <summary>Offene Verweise nach dem SHA-256 ihres Tokens.</summary>
        Private Shared ReadOnly Pending As New Dictionary(Of String, PendingReset)(StringComparer.Ordinal)
        Private Shared ReadOnly MailsByAccount As New Dictionary(Of String, List(Of DateTime))(StringComparer.Ordinal)
        Private Shared ReadOnly RequestsBySender As New Dictionary(Of String, List(Of DateTime))(StringComparer.Ordinal)

        ''' <summary>Was die Nachricht sagt, in der Sprache des Kontos (die Bruecke baut die Texte,
        ''' Request ruft sie in dessen Sprache). In <see cref="Greeting"/> ist {0} der Name; der
        ''' Verweis kommt als Knopf und als Text dazu (MailTemplate).</summary>
        Public NotInheritable Class MailTexts
            Public Subject As String
            Public Title As String
            Public Greeting As String
            Public Intro As String
            Public ButtonText As String
            Public LinkHint As String
            Public Validity As String
            Public Ignore As String
        End Class

        ''' <summary>Nur fuer den Pruefstand: die letzte gesendete Nachricht ging an diese Adresse
        ''' mit diesem Verweis. So kann er ihn benutzen, ohne den Mailserver auszulesen.</summary>
        Friend Shared LastLinkForTests As String = ""

        ''' <summary>Nur fuer den Pruefstand: die letzte Aufgabe, die eine Nachricht sendet.</summary>
        Friend Shared LastSendTask As Task = Task.CompletedTask

        Private Shared Function HashToken(token As String) As String
            Return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(If(token, ""))))
        End Function

        ''' <summary>Ein Token, wie der Verweis es traegt: 64 Zeichen Hex.</summary>
        Private Shared Function IsTokenShape(token As String) As Boolean
            Return Not String.IsNullOrEmpty(token) AndAlso token.Length = 64 AndAlso token.All(Function(c) Uri.IsHexDigit(c))
        End Function

        ''' <summary>Zaehlt einen Zeitpunkt in einer Liste der letzten Stunde und sagt, ob die Liste
        ''' davor schon voll war.</summary>
        Private Shared Function CountWithin(map As Dictionary(Of String, List(Of DateTime)), key As String, limit As Integer,
                                            now As DateTime, record As Boolean) As Boolean
            Dim times As List(Of DateTime) = Nothing
            If Not map.TryGetValue(key, times) Then
                If map.Count >= MaxTrackedKeys Then map.Remove(map.OrderBy(Function(p) If(p.Value.Count = 0, DateTime.MinValue, p.Value.Max())).First().Key)
                times = New List(Of DateTime)()
                map(key) = times
            End If
            times.RemoveAll(Function(t) now - t > Window)
            If times.Count >= limit Then Return False
            If record Then times.Add(now)
            Return True
        End Function

        ''' <summary>Der erste Teil einer Anfrage "Kennwort vergessen": die Bremse je Browser und
        ''' Adresse. False, wenn dieser Browser oder diese Adresse zu oft gefragt hat; sonst ist die
        ''' Anfrage gezaehlt. Haengt an keinem Konto, kostet also fuer jedes dieselbe Zeit.</summary>
        Friend Shared Function Admit(counters As IEnumerable(Of String)) As Boolean
            Dim now = AppLockService.Clock()
            Dim keys = If(counters, Array.Empty(Of String)()).Where(Function(k) Not String.IsNullOrEmpty(k)).Distinct().ToList()
            SyncLock Gate
                For Each key In keys
                    If Not CountWithin(RequestsBySender, key, MaxRequestsPerSender, now, record:=False) Then Return False
                Next
                For Each key In keys
                    CountWithin(RequestsBySender, key, MaxRequestsPerSender, now, record:=True)
                Next
            End SyncLock
            Return True
        End Function

        ''' <summary>Der zweite Teil, NACH der Antwort und im Hintergrund: Konto suchen, Verweis
        ''' anlegen, Nachricht bauen, Protokoll, Versand. Die Bruecke schreibt die Antwort, bevor sie
        ''' das hier startet: so haengt ihre Zeit an nichts, was vom Konto abhaengt (frueher liefen
        ''' Suche, Verweis, Texte und eine Zeile im Diagnoseprotokoll fuer ein bekanntes Konto im
        ''' Vordergrund, und die Antwortzeit verriet, ob es das Konto gibt).
        ''' <paramref name="linkBase"/> ist der Anfang des Verweises ohne Schraegstrich am Ende,
        ''' <paramref name="buildTexts"/> baut die Texte in der Sprache des Kontos.</summary>
        Friend Shared Sub SendInBackground(nameOrEmail As String, linkBase As String, buildTexts As Func(Of MailTexts))
            LastSendTask = Task.Run(Function() SendForAsync(nameOrEmail, linkBase, buildTexts))
        End Sub

        Private Shared Async Function SendForAsync(nameOrEmail As String, linkBase As String, buildTexts As Func(Of MailTexts)) As Task
            Try
                Dim body As MailContent = Nothing
                Dim subject As String = Nothing
                Dim recipient As String = Nothing
                Dim settings As MailSettings = Nothing
                Dim accountId = Prepare(nameOrEmail, linkBase, buildTexts, body, subject, recipient, settings)
                If accountId Is Nothing Then Return
                Try
                    Await MailService.SendAsync(settings, recipient, subject, body)
                    SecurityLog.ResetMailed(accountId, sent:=True)
                Catch ex As Exception
                    DiagnosticLogService.LogException("PasswordReset.Send", ex)
                    SecurityLog.ResetMailed(accountId, sent:=False)
                End Try
            Catch ex As Exception
                DiagnosticLogService.LogException("PasswordReset.Prepare", ex)
            End Try
        End Function

        ''' <summary>Sucht das Konto und legt den Verweis an. Die Kennung des Kontos, wenn eine
        ''' Nachricht hinausgehen soll, sonst Nothing (kein Konto, keine Adresse, kein Mailserver,
        ''' Bremse je Konto).</summary>
        Private Shared Function Prepare(nameOrEmail As String, linkBase As String, buildTexts As Func(Of MailTexts),
                                        ByRef body As MailContent, ByRef subject As String, ByRef recipient As String,
                                        ByRef settings As MailSettings) As String
            Dim now = AppLockService.Clock()
            Dim account = UserAccountService.FindForRecovery(nameOrEmail)
            If account Is Nothing OrElse account.Email.Length = 0 Then Return Nothing
            settings = MailSettings.FromAppSettings(AppSettingsService.Load())
            If Not settings.IsComplete Then Return Nothing
            Dim token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
            SyncLock Gate
                If Not CountWithin(MailsByAccount, account.Id, MaxMailsPerAccount, now, record:=True) Then Return Nothing
                ' Ein neuer Verweis ersetzt den alten.
                For Each stale In Pending.Where(Function(p) p.Value.UserId = account.Id OrElse p.Value.ExpiresUtc < now).Select(Function(p) p.Key).ToList()
                    Pending.Remove(stale)
                Next
                Pending(HashToken(token)) = New PendingReset With {.UserId = account.Id, .ExpiresUtc = now + TokenLifetime}
            End SyncLock
            Dim link = linkBase.TrimEnd("/"c) & BrowserBridge.ResetRoute & "?t=" & token
            LastLinkForTests = link
            recipient = account.Email
            ' Die Nachricht in der Sprache des Benutzers; bei "automatisch" in der dieser Anfrage
            ' (Accept-Language des Browsers).
            Dim texts As MailTexts
            Dim language As String
            Dim userCulture = UserAccountService.CultureOf(account.Id)
            Using LocalizationService.UseRequestCulture(If(userCulture, If(LocalizationService.HasRequestCulture, LocalizationService.TextCulture, Nothing)))
                texts = buildTexts?.Invoke()
                language = LocalizationService.HtmlLanguage(LocalizationService.TextCulture)
            End Using
            texts = If(texts, New MailTexts())
            subject = If(texts.Subject, "FerrumPix")
            body = New MailContent With {
                .Title = If(texts.Title, ""),
                .Greeting = If(texts.Greeting Is Nothing, "", String.Format(texts.Greeting, account.Name)),
                .ButtonText = If(texts.ButtonText, ""),
                .Link = link,
                .LinkHint = If(texts.LinkHint, ""),
                .FooterAddress = linkBase.TrimEnd("/"c),
                .Language = language}
            If Not String.IsNullOrEmpty(texts.Intro) Then body.Paragraphs.Add(texts.Intro)
            If Not String.IsNullOrEmpty(texts.Validity) Then body.Notes.Add(texts.Validity)
            If Not String.IsNullOrEmpty(texts.Ignore) Then body.Notes.Add(texts.Ignore)
            DiagnosticLogService.LogAlways("PasswordReset", $"Reset link requested for {account.Id}.")
            Return account.Id
        End Function

        ''' <summary>Gilt dieser Verweis noch? Fuer die Seite, die das neue Kennwort fragt.</summary>
        Friend Shared Function IsValid(token As String) As Boolean
            If Not IsTokenShape(token) Then Return False
            Dim now = AppLockService.Clock()
            SyncLock Gate
                Dim found As PendingReset = Nothing
                Return Pending.TryGetValue(HashToken(token), found) AndAlso found.ExpiresUtc >= now
            End SyncLock
        End Function

        ''' <summary>Der Benutzer zu einem gueltigen Verweis, ohne ihn zu verbrauchen; sonst Nothing.
        ''' Fuer ein Konto mit zweitem Faktor: der Verweis bleibt, bis auch der Code stimmt.</summary>
        Friend Shared Function Peek(token As String) As String
            If Not IsTokenShape(token) Then Return Nothing
            Dim now = AppLockService.Clock()
            SyncLock Gate
                Dim found As PendingReset = Nothing
                If Not Pending.TryGetValue(HashToken(token), found) OrElse found.ExpiresUtc < now Then Return Nothing
                Return found.UserId
            End SyncLock
        End Function

        ''' <summary>Loest einen Verweis ein: der Benutzer dazu, und der Verweis ist verbraucht. Ein
        ''' abgelaufener, unbekannter oder schon benutzter liefert Nothing.</summary>
        Friend Shared Function Consume(token As String) As String
            If Not IsTokenShape(token) Then Return Nothing
            Dim now = AppLockService.Clock()
            Dim key = HashToken(token)
            SyncLock Gate
                Dim found As PendingReset = Nothing
                If Not Pending.TryGetValue(key, found) Then Return Nothing
                Pending.Remove(key)
                If found.ExpiresUtc < now Then Return Nothing
                Return found.UserId
            End SyncLock
        End Function

        ''' <summary>Macht jeden offenen Verweis eines Benutzers ungueltig: nach einer Anmeldung, einem
        ''' neuen Kennwort, dem Sperren oder Loeschen.</summary>
        Public Shared Sub Forget(userId As String)
            If String.IsNullOrEmpty(userId) Then Return
            SyncLock Gate
                For Each stale In Pending.Where(Function(p) p.Value.UserId = userId).Select(Function(p) p.Key).ToList()
                    Pending.Remove(stale)
                Next
            End SyncLock
        End Sub

        ''' <summary>Vergisst alles. Fuer den Pruefstand.</summary>
        Friend Shared Sub ResetForTests()
            SyncLock Gate
                Pending.Clear()
                MailsByAccount.Clear()
                RequestsBySender.Clear()
            End SyncLock
            LastLinkForTests = ""
            LastSendTask = Task.CompletedTask
        End Sub

    End Class

End Namespace
