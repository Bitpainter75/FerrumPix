Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Net
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading.Tasks
Imports Avalonia.Threading

Namespace Services

    ''' <summary>
    ''' Der zweite Faktor im Browser (TwoFactorService): der zweite Schritt der Anmeldung und das
    ''' Einrichten auf der mobilen Seite.
    '''
    ''' <para>ZWEI SCHRITTE, EINE KARTE. Stimmen Name und Kennwort eines Kontos mit zweitem Faktor,
    ''' antwortet POST /login nicht mit einer Sitzung, sondern mit "second": die Seite fragt nach
    ''' dem Code. Bis dahin gibt es nur einen ZWISCHENSTAND: 32 Zufallsbytes im Cookie
    ''' <see cref="SecondStepCookieName"/> (HttpOnly, SameSite=Strict, Pfad /login, fuenf Minuten),
    ''' am Server nur ihr SHA-256, gebunden an diesen Browser (die Kennung seiner Anmeldeseite,
    ''' ferrumpix_login) und an den Stand der Kennwoerter. Der Code geht wieder an POST /login, mit
    ''' {"code"} oder {"recovery"}. Falsches Kennwort und "Code noetig" verraten nichts ueber das
    ''' Konto: die Frage nach dem Code kommt erst, wenn das Kennwort stimmt.</para>
    '''
    ''' <para>EIGENE BREMSE FUER CODES, je Konto ("code account ...") und je Absender ("code
    ''' address ..."), dazu die Zaehler der Anmeldung dieses Browsers: ein falscher Code zaehlt wie
    ''' ein falsches Kennwort (fuenf frei, dann 2, 4, 8 ... Sekunden). Nach zehn falschen Codes ist
    ''' der Zwischenstand verbraucht, dann beginnt die Anmeldung von vorn.</para>
    '''
    ''' <para>WERKZEUGE MIT BASIC kommen fuer ein Konto mit zweitem Faktor nicht durch: ein Kopf
    ''' traegt keinen Code. Sie brauchen ein Konto ohne zweiten Faktor.</para>
    ''' </summary>
    Partial Public NotInheritable Class BrowserBridge

        ''' <summary>Der Zwischenstand zwischen Kennwort und Code. Gibt keinen Zugang.</summary>
        Friend Const SecondStepCookieName As String = "ferrumpix_2fa"

        ''' <summary>So lange gilt der Zwischenstand.</summary>
        Friend Shared ReadOnly SecondStepLifetime As TimeSpan = TimeSpan.FromMinutes(5)

        Private Const MaxSecondSteps As Integer = 64
        Private Const MaxSecondStepAttempts As Integer = 10

        Private NotInheritable Class SecondStep
            Public UserId As String
            Public UntilUtc As DateTime
            ''' <summary>Der Zaehler der Anmeldeseite dieses Browsers beim ersten Schritt, sonst leer.</summary>
            Public BrowserKey As String
            ''' <summary>AppLockService.PasswordStamp beim ersten Schritt: ein neues Kennwort
            ''' dazwischen macht den Zwischenstand ungueltig.</summary>
            Public Stamp As String
            Public Attempts As Integer
        End Class

        ''' <summary>Die offenen Zwischenstaende nach dem SHA-256 des Cookie-Werts.</summary>
        Private Shared ReadOnly SecondSteps As New Dictionary(Of String, SecondStep)(StringComparer.Ordinal)

        ''' <summary>Die Zaehler fuer einen Code: die der Anmeldung dieses Browsers, dazu eigene je
        ''' Konto und je Absender.</summary>
        Private Shared Function SecondFactorCounters(request As HttpListenerRequest, userId As String) As String()
            Dim keys As New List(Of String)(LoginCounterKeys(request))
            keys.Add(AppLockService.AccountCounterPrefix & userId)
            keys.Add("code " & SenderKey(request))
            Return keys.ToArray()
        End Function

        ''' <summary>Die Kennung der Anmeldeseite dieses Browsers als Zaehler, sonst leer.</summary>
        Private Shared Function BrowserKeyOf(request As HttpListenerRequest) As String
            Dim value = ReadLoginCookie(request)
            Return If(value Is Nothing, "", SessionKey(value))
        End Function

        ''' <summary>Nach richtigem Namen und Kennwort eines Kontos mit zweitem Faktor (Anmeldung
        ''' oder neues Kennwort ueber den Verweis): legt den Zwischenstand an und sagt der Seite, dass
        ''' sie den Code fragen soll. Noch keine Sitzung, nichts entsperrt.</summary>
        Private Shared Async Function BeginSecondStepAsync(context As HttpListenerContext, userId As String) As Task
            Dim request = context.Request
            Dim value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            Dim now = AppLockService.Clock()
            SyncLock SecondSteps
                For Each stale In SecondSteps.Where(Function(p) p.Value.UntilUtc < now).Select(Function(p) p.Key).ToList()
                    SecondSteps.Remove(stale)
                Next
                If SecondSteps.Count >= MaxSecondSteps Then
                    SecondSteps.Remove(SecondSteps.OrderBy(Function(p) p.Value.UntilUtc).First().Key)
                End If
                SecondSteps(SessionKey(value)) = New SecondStep With {
                    .UserId = userId, .UntilUtc = now + SecondStepLifetime, .BrowserKey = BrowserKeyOf(request),
                    .Stamp = AppLockService.PasswordStamp()}
            End SyncLock
            context.Response.Headers.Add("Set-Cookie", SecondStepCookie(value, CLng(SecondStepLifetime.TotalSeconds), IsSecureRequest(request)))
            Await WriteSecondFactorAnswerAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", False}, {"second", True}})
        End Function

        Private Shared Function SecondStepCookie(value As String, maxAgeSeconds As Long, secure As Boolean) As String
            Return $"{SecondStepCookieName}={value}; Max-Age={maxAgeSeconds.ToString(CultureInfo.InvariantCulture)}; Path={LoginRoute}; HttpOnly; SameSite=Strict" &
                   If(secure, "; Secure", "")
        End Function

        ''' <summary>Der gueltige Zwischenstand dieser Anfrage samt seinem Schluessel, sonst Nothing.
        ''' Abgelaufen, aus einem anderen Browser oder nach einem neuen Kennwort gilt er nicht.</summary>
        Private Shared Function FindSecondStep(request As HttpListenerRequest, ByRef key As String) As SecondStep
            key = Nothing
            Dim value = ReadHexCookie(request, SecondStepCookieName)
            If value Is Nothing Then Return Nothing
            Dim found = SessionKey(value)
            Dim now = AppLockService.Clock()
            SyncLock SecondSteps
                Dim entry As SecondStep = Nothing
                If Not SecondSteps.TryGetValue(found, entry) Then Return Nothing
                Dim browser = BrowserKeyOf(request)
                Dim sameBrowser = CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(entry.BrowserKey), Encoding.ASCII.GetBytes(browser))
                If entry.UntilUtc < now OrElse Not sameBrowser OrElse entry.Stamp <> AppLockService.PasswordStamp() OrElse
                   Not UserAccountService.IsActive(entry.UserId) OrElse Not TwoFactorService.IsEnabled(entry.UserId) Then
                    SecondSteps.Remove(found)
                    Return Nothing
                End If
                key = found
                Return entry
            End SyncLock
        End Function

        ''' <summary>Vergisst die Zwischenstaende eines Benutzers (zweiter Faktor zurueckgesetzt) oder
        ''' mit Nothing alle (Anhalten der Bruecke).</summary>
        Friend Shared Sub ForgetSecondSteps(userId As String)
            SyncLock SecondSteps
                If userId Is Nothing Then
                    SecondSteps.Clear()
                    Return
                End If
                For Each key In SecondSteps.Where(Function(p) p.Value.UserId = userId).Select(Function(p) p.Key).ToList()
                    SecondSteps.Remove(key)
                Next
            End SyncLock
        End Sub

        ''' <summary>Wie viele Zwischenstaende offen sind. Fuer den Pruefstand.</summary>
        Friend Shared ReadOnly Property SecondStepCount As Integer
            Get
                SyncLock SecondSteps
                    Return SecondSteps.Count
                End SyncLock
            End Get
        End Property

        ''' <summary>POST /login mit {"code": "123456"} oder {"recovery": "xxxx-xxxx-xxxx"}: der
        ''' zweite Schritt. Ohne gueltigen Zwischenstand 401 mit "restart" (die Seite geht zum
        ''' ersten Schritt zurueck). Ein falscher Code zaehlt als Fehlversuch bei den Zaehlern von
        ''' <see cref="SecondFactorCounters"/>; gebremst 429. Richtig: der Zwischenstand ist
        ''' verbraucht, und es geht weiter wie nach einer Anmeldung ohne zweiten Faktor.
        '''
        ''' <para>OHNE ZWISCHENSTAND ZAEHLT NICHTS: geprueft wird dann kein Code, es gibt also nichts
        ''' zu raten. Frueher zaehlte ein solcher Versuch als Fehlversuch, ohne Kennwortrechnung und
        ''' ohne vorher nach der Bremse zu fragen; mit erfundenen Werten liess sich so billig die
        ''' Liste der Zaehler fuellen. Jetzt fragt der Weg zuerst die Bremse (gebremst 429) und legt
        ''' keinen Zaehler an.</para></summary>
        Private Shared Async Function HandleSecondFactorAsync(context As HttpListenerContext, fields As Dictionary(Of String, String)) As Task
            Dim request = context.Request
            Dim from = LogAddress(request)
            Dim stepKey As String = Nothing
            Dim pending = FindSecondStep(request, stepKey)
            If pending Is Nothing Then
                Dim blocked = AppLockService.RetryAfter(LoginCounterKeys(request))
                If blocked > TimeSpan.Zero Then
                    SecurityLog.Throttled("twofactor", from, blocked)
                    Await WriteThrottledSecondFactorAsync(context, blocked)
                    Return
                End If
                context.Response.Headers.Add("Set-Cookie", SecondStepCookie("", 0, IsSecureRequest(request)))
                Await WriteSecondFactorAnswerAsync(context, 401, New Dictionary(Of String, Object) From {
                    {"ok", False}, {"restart", True},
                    {"error", LocalizationService.T("Die Anmeldung ist abgelaufen. Bitte noch einmal anmelden.")}})
                Return
            End If
            Dim userId = pending.UserId
            Dim counters = SecondFactorCounters(request, userId)
            Dim wait = AppLockService.RetryAfter(counters)
            If wait > TimeSpan.Zero Then
                SecurityLog.Throttled("twofactor", from, wait)
                Await WriteThrottledSecondFactorAsync(context, wait)
                Return
            End If
            Dim code As String = Nothing
            Dim recovery As String = Nothing
            If Not fields.TryGetValue("code", code) Then code = ""
            If Not fields.TryGetValue("recovery", recovery) Then recovery = ""
            Dim useRecovery = recovery.Trim().Length > 0
            Dim accepted = Await Task.Run(Function() If(useRecovery,
                                                      TwoFactorService.UseRecoveryCode(userId, recovery),
                                                      TwoFactorService.VerifyCode(userId, code) = TwoFactorOutcome.Accepted))
            If Not accepted Then
                AppLockService.RegisterFailedAttempt(counters)
                TwoFactorService.NoteFailed(userId, from, "signin")
                Dim restart = False
                SyncLock SecondSteps
                    pending.Attempts += 1
                    If pending.Attempts >= MaxSecondStepAttempts Then
                        SecondSteps.Remove(stepKey)
                        restart = True
                    End If
                End SyncLock
                Dim retry = AppLockService.RetryAfter(counters)
                Dim text = If(restart, LocalizationService.T("Die Anmeldung ist abgelaufen. Bitte noch einmal anmelden."),
                              If(retry > TimeSpan.Zero, ViewModels.LockScreenViewModel.ThrottledText(retry),
                                 LocalizationService.T("Der Code ist falsch.")))
                Dim answer As New Dictionary(Of String, Object) From {{"ok", False}, {"error", text}}
                If restart Then answer("restart") = True
                If retry > TimeSpan.Zero Then answer("retry") = CInt(Math.Ceiling(retry.TotalSeconds))
                Await WriteSecondFactorAnswerAsync(context, 401, answer)
                Return
            End If
            SyncLock SecondSteps
                SecondSteps.Remove(stepKey)
            End SyncLock
            AppLockService.ClearFailedAttempts(counters)
            If useRecovery Then TwoFactorService.NoteRecoveryUsed(userId, from)
            context.Response.Headers.Add("Set-Cookie", SecondStepCookie("", 0, IsSecureRequest(request)))
            Await CompleteSignInAsync(context, userId)
        End Function

        Private Shared Async Function WriteThrottledSecondFactorAsync(context As HttpListenerContext, wait As TimeSpan) As Task
            Dim seconds = CInt(Math.Ceiling(wait.TotalSeconds))
            context.Response.Headers("Retry-After") = seconds.ToString(CultureInfo.InvariantCulture)
            Await WriteSecondFactorAnswerAsync(context, 429, New Dictionary(Of String, Object) From {
                {"ok", False}, {"retry", seconds}, {"error", ViewModels.LockScreenViewModel.ThrottledText(wait)}})
        End Function

        ''' <summary>Die Antwort des zweiten Schritts als JSON, nie zwischengespeichert.</summary>
        Private Shared Async Function WriteSecondFactorAnswerAsync(context As HttpListenerContext, status As Integer, answer As Dictionary(Of String, Object)) As Task
            Dim bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(answer))
            context.Response.StatusCode = status
            context.Response.ContentType = "application/json; charset=utf-8"
            context.Response.Headers("Cache-Control") = "no-store"
            context.Response.ContentLength64 = bytes.Length
            Await context.Response.OutputStream.WriteAsync(bytes)
            context.Response.Close()
        End Function

        ''' <summary>Werkzeuge mit Basic: ein Konto mit zweitem Faktor kommt so nicht herein, ein Kopf
        ''' traegt keinen Code. Das Kennwort stimmte; gezaehlt wird nichts, protokolliert je Konto
        ''' und Absender hoechstens einmal je Stunde (SecurityLog.SignedIn mit "basic" ebenso).</summary>
        Private Shared Function BasicAllowedFor(userId As String) As Boolean
            Return Not TwoFactorService.IsEnabled(userId)
        End Function

        ''' <summary>Setzt den zweiten Faktor eines Benutzers zurueck (Verwaltung) und beendet seine
        ''' Sitzungen und offenen Zwischenstaende. True, wenn einer eingerichtet war.</summary>
        Public Shared Function ResetSecondFactor(userId As String) As Boolean
            Dim had = TwoFactorService.Remove(userId)
            ForgetSecondSteps(userId)
            EndSessionsOfUser(userId, Nothing)
            If had Then TwoFactorService.NoteReset(userId, "admin")
            Return had
        End Function

        ' --- Mobile Seite: Mehr, Zweiter Faktor ------------------------------------------------

        ''' <summary>Die schreibenden Wege des zweiten Faktors (nur POST, eigene Herkunft).</summary>
        Friend Shared Function IsMobileTwoFactorWrite(route As String) As Boolean
            Return route = "twofactor/begin" OrElse route = "twofactor/confirm" OrElse
                   route = "twofactor/disable" OrElse route = "twofactor/cancel"
        End Function

        ''' <summary>Die Wege unter /api/m/twofactor fuer den angemeldeten Benutzer selbst. False,
        ''' wenn der Weg keiner von ihnen ist.
        ''' <list type="bullet">
        ''' <item>GET twofactor: eingerichtet ja/nein, uebrige Wiederherstellungscodes; mit
        ''' ?texts=1 die Texte der Seite.</item>
        ''' <item>POST twofactor/begin: neues Geheimnis (nur im Speicher), Schluessel zum
        ''' Abtippen.</item>
        ''' <item>GET twofactor/qr: der QR-Code dazu als SVG, nur mit Sitzung, no-store.</item>
        ''' <item>POST twofactor/confirm {"code"}: aktiv erst mit gueltigem Code, Antwort mit den
        ''' zehn Wiederherstellungscodes, die es danach nirgends mehr gibt.</item>
        ''' <item>POST twofactor/disable {"password", "code"}: nur mit beidem.</item>
        ''' <item>POST twofactor/cancel: verwirft ein begonnenes Einrichten.</item>
        ''' </list></summary>
        Private Shared Async Function HandleMobileTwoFactorApiAsync(context As HttpListenerContext, route As String, mobile As MobileContext) As Task(Of Boolean)
            Dim userId = mobile.UserKey
            Dim request = context.Request
            Select Case route
                Case "twofactor"
                    Dim answer As New Dictionary(Of String, Object) From {
                        {"ok", True}, {"enabled", TwoFactorService.IsEnabled(userId)},
                        {"recoveryLeft", TwoFactorService.RecoveryCodesLeft(userId)}}
                    If request.QueryString("texts") = "1" Then answer("texts") = MobileTwoFactorTexts()
                    Await WriteMobileJsonAsync(context, 200, answer)
                Case "twofactor/begin"
                    Dim enrollment = TwoFactorService.BeginEnrollment(userId, AccountNameOf(userId))
                    If enrollment Is Nothing Then
                        Await WriteMobileJsonAsync(context, 409, MobileProblem(LocalizationService.T("Der zweite Faktor ist schon eingerichtet.")))
                    Else
                        Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {
                            {"ok", True}, {"secret", TwoFactorService.FormatSecret(enrollment.Secret)}})
                    End If
                Case "twofactor/qr"
                    Dim enrollment = TwoFactorService.PendingEnrollment(userId, AccountNameOf(userId))
                    If enrollment Is Nothing Then
                        context.Response.StatusCode = 404
                        context.Response.Headers("Cache-Control") = "no-store"
                        context.Response.Close()
                    Else
                        Dim bytes = Encoding.UTF8.GetBytes(TwoFactorService.QrSvg(enrollment.Uri))
                        Dim response = context.Response
                        response.StatusCode = 200
                        response.ContentType = "image/svg+xml"
                        response.Headers("Cache-Control") = "no-store"
                        response.Headers("X-Content-Type-Options") = "nosniff"
                        response.Headers("Content-Security-Policy") = "default-src 'none'"
                        response.ContentLength64 = bytes.Length
                        Await response.OutputStream.WriteAsync(bytes)
                        response.Close()
                    End If
                Case "twofactor/confirm"
                    Dim body = Await ReadMobileJsonAsync(request)
                    Dim code = If(body.HasValue, ReadString(body.Value, "code"), "")
                    If TwoFactorService.PendingEnrollment(userId, "") Is Nothing Then
                        Await WriteMobileJsonAsync(context, 409, MobileProblem(LocalizationService.T("Das Einrichten ist abgelaufen. Bitte neu beginnen.")))
                        Return True
                    End If
                    Dim codes As List(Of String) = Nothing
                    If Not TwoFactorService.ConfirmEnrollment(userId, code, codes) Then
                        Await WriteMobileJsonAsync(context, 400, MobileProblem(LocalizationService.T("Der Code ist falsch.")))
                        Return True
                    End If
                    TwoFactorService.NoteEnabled(userId, LogAddress(request), "mobile")
                    Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}, {"recovery", codes}})
                Case "twofactor/disable"
                    Dim body = Await ReadMobileJsonAsync(request)
                    Dim password = If(body.HasValue, ReadString(body.Value, "password"), "")
                    Dim code = If(body.HasValue, ReadString(body.Value, "code"), "")
                    Dim problem = Await Task.Run(Function() DisableSecondFactor(userId, password, code, SecondFactorCounters(request, userId), LogAddress(request), "mobile"))
                    If problem Is Nothing Then
                        Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}})
                    Else
                        Await WriteMobileJsonAsync(context, 400, MobileProblem(problem))
                    End If
                Case "twofactor/cancel"
                    TwoFactorService.ForgetPending(userId)
                    Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}})
                Case Else
                    Return False
            End Select
            Return True
        End Function

        ''' <summary>Schaltet den eigenen zweiten Faktor ab, wenn Kennwort UND Code stimmen. Nothing
        ''' heisst abgeschaltet, sonst der Satz fuer die Meldung. Dieselbe Bremse wie der zweite
        ''' Schritt der Anmeldung; ein falsches Kennwort oder ein falscher Code zaehlt. Langsam
        ''' (Kennwort), nie auf dem Anzeigefaden. Auch fuer die Einstellungen.</summary>
        Friend Shared Function DisableSecondFactor(userId As String, password As String, code As String, counters As String(),
                                                   from As String, via As String) As String
            If Not TwoFactorService.IsEnabled(userId) Then Return Nothing
            Dim wait = AppLockService.RetryAfter(counters)
            If wait > TimeSpan.Zero Then Return ViewModels.LockScreenViewModel.ThrottledText(wait)
            Dim passwordResult = AppLockService.CheckUserPassword(userId, password, counters)
            If passwordResult.Outcome = PasswordCheckOutcome.Throttled Then Return ViewModels.LockScreenViewModel.ThrottledText(passwordResult.RetryAfter)
            ' Der Code wird auch bei falschem Kennwort geprueft: gleiche Zeit, und die Antwort sagt
            ' nicht, welches von beiden falsch war.
            Dim codeOutcome = TwoFactorService.VerifyCode(userId, code)
            If passwordResult.Outcome <> PasswordCheckOutcome.Accepted OrElse codeOutcome <> TwoFactorOutcome.Accepted Then
                If passwordResult.Outcome = PasswordCheckOutcome.Accepted Then AppLockService.RegisterFailedAttempt(counters)
                TwoFactorService.NoteFailed(userId, from, "disable")
                Return LocalizationService.T("Kennwort oder Code ist falsch.")
            End If
            TwoFactorService.Remove(userId)
            ForgetSecondSteps(userId)
            TwoFactorService.NoteDisabled(userId, from, via)
            Return Nothing
        End Function

        ''' <summary>Der Name eines Kontos fuer das Etikett in der App: sein Benutzername, ohne
        ''' Namen FerrumPix.</summary>
        Friend Shared Function AccountNameOf(userId As String) As String
            Dim name = UserAccountService.Find(userId)?.Name
            Return If(String.IsNullOrWhiteSpace(name), TwoFactorService.Issuer, name)
        End Function

        ''' <summary>Die Texte des zweiten Faktors auf der mobilen Seite, in der Sprache der Anfrage.</summary>
        Private Shared Function MobileTwoFactorTexts() As Dictionary(Of String, String)
            Return New Dictionary(Of String, String) From {
                {"title", LocalizationService.T("Zweiter Faktor")},
                {"intro", LocalizationService.T("Mit einem zweiten Faktor fragt die Anmeldung im Browser nach dem Kennwort zusätzlich einen Code aus einer Authenticator-App.")},
                {"statusOn", LocalizationService.T("Der zweite Faktor ist eingerichtet. Übrige Wiederherstellungscodes: {0}")},
                {"statusOff", LocalizationService.T("Der zweite Faktor ist nicht eingerichtet.")},
                {"setUp", LocalizationService.T("Einrichten")},
                {"scan", LocalizationService.T("Den QR-Code mit der Authenticator-App scannen oder den Schlüssel abtippen. Danach den Code aus der App eingeben.")},
                {"key", LocalizationService.T("Schlüssel")},
                {"code", LocalizationService.T("Code")},
                {"confirm", LocalizationService.T("Bestätigen")},
                {"cancel", LocalizationService.T("Abbrechen")},
                {"recoveryTitle", LocalizationService.T("Wiederherstellungscodes")},
                {"recoveryHint", LocalizationService.T("Jeder Code gilt einmal, falls die App nicht zur Hand ist. Sie erscheinen nur jetzt; bitte sicher aufbewahren.")},
                {"download", LocalizationService.T("Als Textdatei speichern")},
                {"done", LocalizationService.T("Fertig")},
                {"disable", LocalizationService.T("Abschalten")},
                {"disableHint", LocalizationService.T("Zum Abschalten das aktuelle Kennwort und einen Code aus der App eingeben.")},
                {"password", LocalizationService.T("Kennwort")},
                {"enabled", LocalizationService.T("Der zweite Faktor ist eingerichtet.")},
                {"disabled", LocalizationService.T("Der zweite Faktor ist abgeschaltet.")},
                {"empty", LocalizationService.T("Bitte den Code eingeben.")}}
        End Function

    End Class

End Namespace
