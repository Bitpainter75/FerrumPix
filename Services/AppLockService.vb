Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text

Namespace Services

    ''' <summary>Wie ein Versuch, das Kennwort zu pruefen, ausgegangen ist.</summary>
    Public Enum PasswordCheckOutcome
        ''' <summary>Das Kennwort stimmt.</summary>
        Accepted
        ''' <summary>Das Kennwort stimmt nicht.</summary>
        Rejected
        ''' <summary>Nach zu vielen Fehlversuchen wurde gar nicht erst geprueft.</summary>
        Throttled
        ''' <summary>Es ist kein Kennwort gesetzt.</summary>
        NotProtected
    End Enum

    ''' <summary>Eine Regel fuer ein neues Kennwort, die nicht erfuellt ist.</summary>
    Public Enum PasswordRule
        ''' <summary>Weniger als <see cref="AppLockService.MinimumPasswordLength"/> Zeichen.</summary>
        TooShort
        NoUppercase
        NoLowercase
        NoDigit
        ''' <summary>Kein Zeichen, das weder Buchstabe noch Ziffer ist; ein Leerzeichen zaehlt.</summary>
        NoSymbol
    End Enum

    ''' <summary>Wie die Ersteinrichtung des Kennworts ausgegangen ist.</summary>
    Public Enum InitialPasswordOutcome
        ''' <summary>Das Kennwort ist gesetzt.</summary>
        Created
        ''' <summary>Es gab schon eines; die Einrichtung geht nur einmal.</summary>
        AlreadySet
        ''' <summary>Das Kennwort erfuellt die Regeln nicht.</summary>
        Weak
        ''' <summary>Der Benutzername ist leer, zu lang oder enthaelt Steuerzeichen.</summary>
        InvalidName
        ''' <summary>Die E-Mail-Adresse hat nicht die Form name@domain.tld.</summary>
        InvalidEmail
    End Enum

    ''' <summary>Ergebnis einer Pruefung samt der Wartezeit, wenn zu oft falsch geraten wurde.</summary>
    Public NotInheritable Class PasswordCheckResult
        Public Sub New(outcome As PasswordCheckOutcome, retryAfter As TimeSpan, Optional userId As String = Nothing)
            Me.Outcome = outcome
            Me.RetryAfter = retryAfter
            Me.UserId = userId
        End Sub

        Public ReadOnly Property Outcome As PasswordCheckOutcome
        Public ReadOnly Property RetryAfter As TimeSpan
        ''' <summary>Wer sich angemeldet hat, nur bei <see cref="PasswordCheckOutcome.Accepted"/>:
        ''' der Eigentuemer (<see cref="AppLockService.OwnerUserId"/>) oder ein weiterer Benutzer.</summary>
        Public ReadOnly Property UserId As String
    End Class

    ''' <summary>
    ''' Das Anmeldefenster der Anwendung: ein Kennwort, die Sperre und die Zeit ohne Eingabe.
    '''
    ''' <para>ZWEI KENNWOERTER, NUR ALS HASH. Am Rechner das des Anmeldefensters
    ''' (AppSettings.AppLockPasswordHash, ein Schalter), im Browser das des Eigentuemers
    ''' (AppSettings.BrowserOwnerPasswordHash, Pflicht), getrennt, siehe <see cref="CurrentHash"/>.
    ''' Gespeichert wird PBKDF2 mit SHA-256, eigenem Salz und hoher Rundenzahl, verglichen in
    ''' fester Zeit. Argon2id
    ''' gaebe es nur mit einer neuen Abhaengigkeit; PBKDF2 bringt .NET selbst mit. Die Rundenzahl
    ''' steht im Hash, ein spaeter erhoehter Wert macht alte Hashes also nicht ungueltig.</para>
    '''
    ''' <para>DIE SPERRE SCHUETZT DIE LAUFENDE ANWENDUNG, nicht die Fotos: die Dateien liegen weiter
    ''' offen auf der Platte. Gesperrt wird beim Start, nach der eingestellten Zeit ohne Eingabe und
    ''' von Hand. Hintergrundarbeit laeuft dabei weiter.</para>
    '''
    ''' <para>SCHUTZ GEGEN RATEN: fuenf Fehlversuche frei, danach wachsende Wartezeit (2, 4, 8 ...
    ''' Sekunden, hoechstens fuenf Minuten), nie eine endgueltige Sperre. Gezaehlt wird je Zaehler,
    ''' nicht fuer die ganze Anwendung: am Rechner das Anmeldefenster dort (<see cref="LocalKey"/>),
    ''' im Browser die Anmeldung des Browsers und seine Absenderadresse (siehe
    ''' <see cref="CheckPassword"/>). Geprueft wird immer nur eines zur Zeit.</para>
    '''
    ''' <para>IM BROWSER (BrowserBridge) melden sich die Sitzungen mit den Benutzerkonten an, der
    ''' Eigentuemer mit seinem eigenen Kennwort; eine Sperre beendet sie alle
    ''' (<see cref="LockStateChanged"/>). Dort ist es Pflicht: ohne Kennwort des Eigentuemers legt
    ''' es der erste Browser fest (<see cref="TrySetInitialPassword"/>), genau einmal. Ein
    ''' Kennwort beim Start mitzugeben gibt es nicht mehr.</para>
    ''' </summary>
    Public NotInheritable Class AppLockService

        Private Sub New()
        End Sub

        Private Const HashScheme As String = "pbkdf2-sha256"

        ''' <summary>Runden fuer neue Hashes. 600 000 ist die Empfehlung fuer PBKDF2 mit SHA-256;
        ''' eine Pruefung kostet damit rund eine Viertelsekunde, und das ist gewollt.</summary>
        Public Const DefaultIterations As Integer = 600000

        Private Const SaltLength As Integer = 16
        Private Const HashLength As Integer = 32

        ''' <summary>Mindestlaenge eines neuen Kennworts, in Zeichen (Unicode-Codepunkten).</summary>
        Public Const MinimumPasswordLength As Integer = 8

        ''' <summary>Welche Regeln ein NEUES Kennwort verletzt; leer heisst in Ordnung. Gilt beim
        ''' Setzen in den Einstellungen und bei der Ersteinrichtung im Browser, nie beim Entsperren
        ''' (dort zaehlt nur der Hash, ein aelteres Kennwort bleibt gueltig).
        '''
        ''' <para>Mindestens acht Zeichen, je ein Gross- und Kleinbuchstabe, eine Ziffer und ein
        ''' Sonderzeichen. Buchstaben sind Unicode-Buchstaben (auch ä, ß, é): wer deutsch tippt,
        ''' soll nicht zu ASCII gezwungen werden. ß ist ein Kleinbuchstabe. Sonderzeichen ist alles,
        ''' was weder Buchstabe noch Ziffer ist, auch das Leerzeichen; Passphrasen aus mehreren
        ''' Woertern erfuellen die Regel damit von selbst. Gezaehlt wird in Codepunkten, ein
        ''' Emoji ist also ein Zeichen und nicht zwei.</para></summary>
        Public Shared Function PasswordProblems(password As String) As IReadOnlyList(Of PasswordRule)
            Dim problems As New List(Of PasswordRule)()
            Dim text = If(password, "")
            Dim length = 0
            Dim upper = False, lower = False, digit = False, symbol = False
            ' Der Name der Schleifenvariable darf nicht Rune heissen: VB unterscheidet keine Gross-
            ' und Kleinschreibung, und Rune.IsUpper waere dann die Variable.
            For Each codePoint In text.EnumerateRunes()
                length += 1
                If Rune.IsUpper(codePoint) Then
                    upper = True
                ElseIf Rune.IsLower(codePoint) Then
                    lower = True
                ElseIf Rune.IsDigit(codePoint) Then
                    digit = True
                ElseIf Not Rune.IsLetter(codePoint) Then
                    symbol = True
                End If
            Next
            If length < MinimumPasswordLength Then problems.Add(PasswordRule.TooShort)
            If Not upper Then problems.Add(PasswordRule.NoUppercase)
            If Not lower Then problems.Add(PasswordRule.NoLowercase)
            If Not digit Then problems.Add(PasswordRule.NoDigit)
            If Not symbol Then problems.Add(PasswordRule.NoSymbol)
            Return problems
        End Function

        ''' <summary>Die verletzten Regeln als englischer Satz fuer die Konsole (Start der Bruecke).</summary>
        Public Shared Function DescribeForConsole(problems As IEnumerable(Of PasswordRule)) As String
            Dim parts As New List(Of String)()
            For Each problem In problems
                Select Case problem
                    Case PasswordRule.TooShort : parts.Add($"at least {MinimumPasswordLength} characters")
                    Case PasswordRule.NoUppercase : parts.Add("an uppercase letter")
                    Case PasswordRule.NoLowercase : parts.Add("a lowercase letter")
                    Case PasswordRule.NoDigit : parts.Add("a digit")
                    Case PasswordRule.NoSymbol : parts.Add("a special character (a space counts)")
                End Select
            Next
            Return String.Join(", ", parts)
        End Function

        ''' <summary>Der Absender am Rechner selbst: das Anmeldefenster und die Einstellungen.</summary>
        Public Const LocalKey As String = "local"

        ''' <summary>So viele Fehlversuche je Zaehler bleiben ohne Wartezeit; ab dem naechsten
        ''' verdoppelt sie sich (2, 4, 8 ... Sekunden), hoechstens fuenf Minuten.</summary>
        Private Const FreeAttempts As Integer = 5

        Private Const MaxDelaySeconds As Integer = 300

        ''' <summary>Hoechstens so viele Zaehler je Browser (Kennung der Anmeldeseite) merkt sich die
        ''' Wartezeit. Laeuft die Liste ueber, faellt zuerst ein ungebremster heraus.</summary>
        Private Const MaxTrackedBrowsers As Integer = 256

        ''' <summary>Hoechstens so viele Zaehler je Adresse (und alle uebrigen ausser Konten,
        ''' Freigaben und dem Rechner selbst). Laeuft die Liste ueber, faellt nur ein UNGEBREMSTER
        ''' heraus, der am laengsten keinen Fehlversuch hatte; sind alle gebremst, wird ein neuer
        ''' Absender durchgelassen, ohne zu zaehlen (<see cref="RegisterFailure"/>).</summary>
        Private Const MaxTrackedAddresses As Integer = 1024

        ''' <summary>Hoechstens so viele Zaehler je Konto. Konten gibt es nur so viele, wie es Benutzer
        ''' gibt; die Grenze faengt nur einen Fehler ab, gemerkt wird wie bei den Adressen.</summary>
        Private Const MaxTrackedAccounts As Integer = 4096

        ''' <summary>Hoechstens so viele Zaehler fuer das Kennwort von Freigabelinks. Eigene Liste,
        ''' damit Raten an Freigaben die Anmeldung nicht verdraengt; gemerkt wird wie bei den
        ''' Adressen.</summary>
        Private Const MaxTrackedShares As Integer = 1024

        ''' <summary>So beginnt der Zaehler eines Browsers (BrowserBridge.LoginCounterKeys).</summary>
        Friend Const BrowserCounterPrefix As String = "login "

        ''' <summary>So beginnt der Zaehler eines Kontos (BrowserBridge.SecondFactorCounters).</summary>
        Friend Const AccountCounterPrefix As String = "code account "

        ''' <summary>So beginnt der Zaehler einer Freigabe (BrowserBridge, Kennwort und Raten von
        ''' Freigabelinks).</summary>
        Friend Const ShareCounterPrefix As String = "share "

        ''' <summary>Die Liste, in der ein Zaehler gefuehrt wird; jede hat ihre eigene Grenze.</summary>
        Private Enum CounterKind
            Local
            Browser
            Account
            Share
            Address
        End Enum

        ''' <summary>Die erlaubten Zeiten ohne Eingabe in Minuten; 0 heisst nie.</summary>
        Public Shared ReadOnly IdleChoices As Integer() = {0, 5, 15, 30, 60}

        ''' <summary>Die Uhr. Der Pruefstand stellt sie vor, statt Minuten zu warten.</summary>
        Friend Shared Clock As Func(Of DateTime) = Function() DateTime.UtcNow

        ''' <summary>Runden fuer neue Hashes. Nur der Pruefstand setzt weniger, damit er nicht
        ''' Sekunden je Pruefung rechnet; gelesen wird die Zahl immer aus dem Hash selbst.</summary>
        Friend Shared Iterations As Integer = DefaultIterations

        Private Shared ReadOnly StateLock As New Object()
        Private Shared ReadOnly VerifyGate As New Object()
        Private Shared ReadOnly SetupGate As New Object()
        Private Shared _isLocked As Boolean
        Private Shared _lastActivityUtc As DateTime = DateTime.UtcNow

        ''' <summary>Nur fuer den Pruefstand: ein Hash, der statt des gespeicherten gilt, ohne die
        ''' Einstellungen anzufassen (BrowserBridge.Start fuer ein Testfenster). Leer heisst: der
        ''' gespeicherte gilt.</summary>
        Friend Shared TestPasswordHash As String = ""

        ''' <summary>Nur fuer den Pruefstand, zusammen mit <see cref="TestPasswordHash"/>: der Name
        ''' des Eigentuemers fuer diesen Lauf.</summary>
        Friend Shared TestUserName As String = ""

        ''' <summary>Die Kennung des Eigentuemers. Heute gibt es nur ihn: die Ersteinrichtung legt
        ''' sein Kennwort fest, und jede Sitzung im Browser gehoert ihm. Sitzungen tragen die
        ''' Kennung trotzdem, damit spaeter weitere Benutzer dazukommen koennen.</summary>
        Public Const OwnerUserId As String = "owner"

        Private Shared ReadOnly Failures As New Dictionary(Of String, FailureRecord)(StringComparer.Ordinal)

        Private NotInheritable Class FailureRecord
            Public Count As Integer
            Public BlockedUntilUtc As DateTime
            Public LastUtc As DateTime
        End Class

        ''' <summary>Gesperrt oder entsperrt. Kann von einem Hintergrundfaden kommen (Anmeldung im
        ''' Browser); wer die Oberflaeche anfasst, wechselt selbst auf den Anzeigefaden.</summary>
        Public Shared Event LockStateChanged As EventHandler

        ''' <summary>Das Kennwort wurde gesetzt oder entfernt: in den Einstellungen
        ''' (<see cref="NotifyProtectionChanged"/>), bei der Ersteinrichtung im Browser oder ueber
        ''' den Notweg, die beiden letzten von einem Hintergrundfaden. Wer "Sperren" zeigt, sieht
        ''' danach nach.</summary>
        Public Shared Event ProtectionChanged As EventHandler

        ''' <summary>Meldet <see cref="ProtectionChanged"/>, nachdem die Einstellungen das Kennwort
        ''' gesetzt, geaendert oder entfernt haben.</summary>
        Friend Shared Sub NotifyProtectionChanged()
            RaiseEvent ProtectionChanged(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>Der Hash des Eigentuemers fuer den Browser (AppSettings.BrowserOwnerPasswordHash),
        ''' im Pruefstand ersetzbar (<see cref="TestPasswordHash"/>).</summary>
        Private Shared Function OwnerHash() As String
            Dim forTests = TestPasswordHash
            If Not String.IsNullOrEmpty(forTests) Then Return forTests
            Return If(AppSettingsService.Load().BrowserOwnerPasswordHash, "")
        End Function

        ''' <summary>Laeuft FerrumPix im Browser? Gestartet mit --browser (schon vor dem Webserver,
        ''' fuer die Sperre beim Start) oder mit laufendem Webserver (auch der des Pruefstands).</summary>
        Private Shared ReadOnly Property InBrowser As Boolean
            Get
                Return BrowserBridge.IsActive OrElse BrowserBridge.RequiresPassword
            End Get
        End Property

        ''' <summary>Der Hash der Sperre, die gerade gilt: im Browser das Kennwort des Eigentuemers,
        ''' am Rechner das des Anmeldefensters (AppSettings.AppLockPasswordHash).
        '''
        ''' <para>ZWEI KENNWOERTER, GETRENNT. Am Rechner ist die Sperre ein Schalter fuer das Fenster
        ''' dort; im Browser ist die Anmeldung Pflicht und gehoert zum Konto des Eigentuemers. Das
        ''' eine abzuschalten oder zu aendern ruehrt das andere nicht an: wer am Rechner die Sperre
        ''' abschaltet, oeffnet damit nicht die Einrichtung im Browser fuer den, der zuerst kommt,
        ''' und die Einrichtung im Browser sperrt nicht das Fenster am Rechner. Beide Laeufe teilen
        ''' sich dieselbe settings.json, deshalb zwei Felder.</para></summary>
        Private Shared Function CurrentHash() As String
            If InBrowser Then Return OwnerHash()
            Return If(AppSettingsService.Load().AppLockPasswordHash, "")
        End Function

        ''' <summary>Der Hash des Eigentuemers als Marke: aendert sich sein Kennwort, aendert sie sich
        ''' mit. Fuer Zwischenspeicher, die eine gelungene Pruefung kurz behalten (BrowserBridge).</summary>
        Friend Shared Function PasswordStamp() As String
            Return OwnerHash() & "|" & StoredUserName & "|" & UserAccountService.Version.ToString(CultureInfo.InvariantCulture)
        End Function

        ''' <summary>Gibt es die Sperre, die gerade gilt (<see cref="CurrentHash"/>)? Am Rechner: ist
        ''' das Kennwort des Anmeldefensters gesetzt; im Browser: hat der Eigentuemer ein Kennwort.
        ''' Nur dann gibt es Anmeldefenster, Sperre und "Sperren".</summary>
        Public Shared ReadOnly Property IsProtected As Boolean
            Get
                Return CurrentHash().Length > 0
            End Get
        End Property

        ''' <summary>Hat der Eigentuemer ein Kennwort fuer den Browser? Ohne zeigt der Browser die
        ''' Einrichtung. Haengt nie am Kennwort des Anmeldefensters am Rechner.</summary>
        Public Shared ReadOnly Property HasOwnerPassword As Boolean
            Get
                Return OwnerHash().Length > 0
            End Get
        End Property

        ''' <summary>Die einmalige Uebernahme beim Start (App und BrowserBridge.Start, wer zuerst
        ''' kommt): bis zur Trennung war das Kennwort des Anmeldefensters zugleich die Anmeldung im
        ''' Browser. Hat der Eigentuemer noch kein eigenes, bekommt er diesen Hash; das Kennwort am
        ''' Rechner bleibt dabei unveraendert. Ohne die Uebernahme stuende der Browser nach dem
        ''' Update offen fuer die Einrichtung durch den, der zuerst kommt.
        '''
        ''' <para>GENAU EINMAL (AppSettings.OwnerPasswordMigrated): spaeter gesetzte Kennwoerter am
        ''' Rechner werden nie mehr zur Anmeldung im Browser, und nach dem Notweg
        ''' (<see cref="ResetOwnerPassword"/>) kaeme sonst das alte Kennwort zurueck. True, wenn
        ''' dabei ein Hash uebernommen wurde.</para></summary>
        Friend Shared Function MigrateOwnerPassword() As Boolean
            Dim settings = AppSettingsService.Load()
            If settings.OwnerPasswordMigrated Then Return False
            Dim adopted = False
            SyncLock SetupGate
                AppSettingsService.Update(
                    Sub(s)
                        If s.OwnerPasswordMigrated Then Return
                        If String.IsNullOrEmpty(s.BrowserOwnerPasswordHash) AndAlso Not String.IsNullOrEmpty(s.AppLockPasswordHash) Then
                            s.BrowserOwnerPasswordHash = s.AppLockPasswordHash
                            adopted = True
                        End If
                        s.OwnerPasswordMigrated = True
                    End Sub)
                AppSettingsService.FlushNow()
            End SyncLock
            If adopted Then DiagnosticLogService.LogAlways("AppLock", "The lock screen password became the administrator password for the browser.")
            Return adopted
        End Function

        ''' <summary>Schreibt den Hash der Sperre, die gerade gilt (<see cref="CurrentHash"/>), in
        ''' die Einstellungen <paramref name="s"/>: im Browser den des Eigentuemers, am Rechner den
        ''' des Anmeldefensters. Fuer den Zugang in den Einstellungen.</summary>
        Friend Shared Sub StoreCurrentHash(s As AppSettings, hash As String)
            If InBrowser Then
                s.BrowserOwnerPasswordHash = If(hash, "")
            Else
                s.AppLockPasswordHash = If(hash, "")
            End If
        End Sub

        Public Shared ReadOnly Property IsLocked As Boolean
            Get
                SyncLock StateLock
                    Return _isLocked
                End SyncLock
            End Get
        End Property

        ''' <summary>Minuten ohne Eingabe bis zur Sperre, 0 heisst nie.</summary>
        Public Shared ReadOnly Property IdleMinutes As Integer
            Get
                Return NormalizeIdleMinutes(AppSettingsService.Load().AppLockIdleMinutes)
            End Get
        End Property

        Public Shared Function NormalizeIdleMinutes(value As Integer) As Integer
            Return If(IdleChoices.Contains(value), value, 0)
        End Function

        ''' <summary>Die Ersteinrichtung im Browser: setzt Benutzername und Kennwort des
        ''' Eigentuemers, wenn noch kein Kennwort gesetzt ist, sonst nichts. Wer zuerst kommt, legt fest; das ist so gewollt, der Browser-Modus
        ''' hat keinen anderen Weg zum ersten Kennwort. Intern ist das der erste Benutzer, und der
        ''' ist der Eigentuemer (<see cref="OwnerUserId"/>).
        '''
        ''' <para>GENAU EINMAL. Pruefen und Schreiben stehen unter einer Sperre: von zwei
        ''' gleichzeitigen Versuchen gelingt einer, der andere bekommt
        ''' <see cref="InitialPasswordOutcome.AlreadySet"/>. Der Hash entsteht vorher, weil er mit
        ''' Absicht langsam ist; er wird nur geschrieben, wenn noch keiner da ist. Geschrieben wird
        ''' sofort und nicht verzoegert: ging der Stand verloren, stuende die Einrichtung wieder
        ''' offen.</para>
        '''
        ''' <para>NACH DEM NOTWEG (<see cref="ResetOwnerPassword"/>) steht der Name des Eigentuemers
        ''' noch da: dann bleibt er, und der eingegebene zaehlt nicht. Ebenso bleibt eine schon
        ''' gespeicherte E-Mail-Adresse, wenn keine neue kommt.</para>
        '''
        ''' <para>Langsam (PBKDF2), deshalb nie auf dem Anzeigefaden rufen.</para></summary>
        Public Shared Function TrySetInitialPassword(userName As String, password As String, Optional email As String = "") As InitialPasswordOutcome
            Dim keptName = StoredUserName
            If keptName.Length = 0 AndAlso Not IsValidUserName(userName) Then Return InitialPasswordOutcome.InvalidName
            If Not UserAccountService.IsValidEmail(email) Then Return InitialPasswordOutcome.InvalidEmail
            If PasswordProblems(password).Count > 0 Then Return InitialPasswordOutcome.Weak
            If HasOwnerPassword Then Return InitialPasswordOutcome.AlreadySet
            Dim hash = HashPassword(password)
            Dim name = If(keptName.Length > 0, keptName, userName.Trim())
            Dim mail = If(email, "").Trim()
            SyncLock SetupGate
                If HasOwnerPassword Then Return InitialPasswordOutcome.AlreadySet
                If mail.Length > 0 AndAlso UserAccountService.EmailTaken(mail, OwnerUserId) Then Return InitialPasswordOutcome.InvalidEmail
                ' Nur das Konto des Eigentuemers; das Kennwort des Anmeldefensters am Rechner
                ' bleibt, wie es ist.
                AppSettingsService.Update(Sub(s)
                                              s.BrowserOwnerPasswordHash = hash
                                              s.AppLockUserName = name
                                              If mail.Length > 0 Then s.AppLockUserEmail = mail
                                          End Sub)
                AppSettingsService.FlushNow()
            End SyncLock
            DiagnosticLogService.LogAlways("AppLock", "Initial password set.")
            RaiseEvent ProtectionChanged(Nothing, EventArgs.Empty)
            Return InitialPasswordOutcome.Created
        End Function

        ''' <summary>Hoechstlaenge eines Benutzernamens, in Zeichen.</summary>
        Public Const MaxUserNameLength As Integer = 64

        ''' <summary>Ein Benutzername ist gueltig, wenn er nach dem Abschneiden der Leerzeichen am
        ''' Rand 1 bis <see cref="MaxUserNameLength"/> Zeichen hat und keine Steuerzeichen.</summary>
        Public Shared Function IsValidUserName(userName As String) As Boolean
            Dim name = If(userName, "").Trim()
            If name.Length = 0 OrElse name.Length > MaxUserNameLength Then Return False
            Return Not name.Any(Function(c) Char.IsControl(c))
        End Function

        ''' <summary>Der gespeicherte Benutzername des Eigentuemers, leer, wenn noch keiner
        ''' festgelegt ist.</summary>
        Public Shared ReadOnly Property StoredUserName As String
            Get
                If Not String.IsNullOrEmpty(TestPasswordHash) Then Return If(TestUserName, "").Trim()
                Return If(AppSettingsService.Load().AppLockUserName, "").Trim()
            End Get
        End Property

        ''' <summary>Nach einer gelungenen Anmeldung im Browser: ist noch kein Name gespeichert
        ''' (Kennwort aus der einmaligen Uebernahme oder aus den Einstellungen im Browser), wird der
        ''' eingegebene der Name des Eigentuemers. True,
        ''' wenn der Name dabei festgelegt wurde.</summary>
        Friend Shared Function AdoptUserName(userName As String) As Boolean
            If StoredUserName.Length > 0 OrElse Not IsValidUserName(userName) Then Return False
            If UserAccountService.NameTaken(userName, OwnerUserId) Then Return False
            ' Gilt gerade ein Kennwort nur fuer den Pruefstand, gehoert der Name nicht zu dem
            ' gespeicherten und bleibt draussen.
            If Not String.IsNullOrEmpty(TestPasswordHash) Then Return False
            Dim name = userName.Trim()
            AppSettingsService.Update(Sub(s) s.AppLockUserName = name)
            AppSettingsService.FlushNow()
            Return True
        End Function

        ''' <summary>Der Notweg, wenn der Eigentuemer sein Kennwort vergessen hat und keine Nachricht
        ''' ankommt: beim Start mit BrowserBridge.ResetAdminSwitch bzw. ResetAdminVariable. Loescht
        ''' NUR den Hash des Eigentuemers; Name, E-Mail, weitere Benutzer und ihre Regeln bleiben, und
        ''' die Seite zeigt wieder die Einrichtung fuer den bestehenden Namen.
        '''
        ''' <para>NUR EINMAL WIRKSAM: im Behaelter bleibt eine Umgebungsvariable bei jedem Neustart
        ''' gesetzt. Nach dem Loeschen steht deshalb ein Vermerk in den Einstellungen
        ''' (AdminPasswordResetConsumed); solange er steht, wirkt die Angabe nicht noch einmal, und
        ''' eine Zeile im Protokoll bittet, sie zu entfernen. Startet die Anwendung ohne die Angabe,
        ''' faellt der Vermerk weg. Wer den Behaelter starten kann, hat ohnehin Zugriff auf den
        ''' Rechner und seine Einstellungen; der Weg verschiebt also keine Grenze.</para>
        '''
        ''' <para>Liefert die Zeile fuer das Protokoll, Nothing, wenn nichts zu sagen ist.</para></summary>
        Friend Shared Function ResetOwnerPassword(requested As Boolean) As String
            Dim settings = AppSettingsService.Load()
            If Not requested Then
                If settings.AdminPasswordResetConsumed Then
                    AppSettingsService.Update(Sub(s) s.AdminPasswordResetConsumed = False)
                    AppSettingsService.FlushNow()
                End If
                Return Nothing
            End If
            If settings.AdminPasswordResetConsumed Then
                SecurityLog.AdminPasswordReset(applied:=False)
                Return "The administrator password was already reset once with this setting. Remove it from the start parameters or the environment; it has no effect until then."
            End If
            ' Der zweite Faktor des Eigentuemers geht mit: wer den Notweg nimmt, hat Zugriff auf den
            ' Rechner und hat womoeglich auch das Telefon mit der App verloren.
            Dim hadSecondFactor = TwoFactorService.IsEnabled(OwnerUserId)
            ' Nur das Konto des Eigentuemers; das Kennwort am Rechner bleibt. Die Uebernahme gilt
            ' als gelaufen, sonst kaeme mit ihr das alte Kennwort zurueck.
            AppSettingsService.Update(Sub(s)
                                          s.BrowserOwnerPasswordHash = ""
                                          s.OwnerPasswordMigrated = True
                                          s.AdminPasswordResetConsumed = True
                                          TwoFactorService.RemoveFrom(s, OwnerUserId)
                                      End Sub)
            AppSettingsService.FlushNow()
            RaiseEvent ProtectionChanged(Nothing, EventArgs.Empty)
            SecurityLog.AdminPasswordReset(applied:=True)
            If hadSecondFactor Then TwoFactorService.NoteReset(OwnerUserId, "start")
            Dim name = StoredUserName
            Return "The administrator password was reset" &
                   If(hadSecondFactor, " and the second factor of the administrator was removed", "") &
                   ". The first browser to open the page sets a new password" &
                   If(name.Length > 0, $" for the user ""{name}""", "") &
                   ". Other users and folder rules are unchanged. Remove the setting now; it works only once."
        End Function

        ''' <summary>Zaehlt einen misslungenen Versuch bei den genannten Zaehlern, ohne ein Kennwort
        ''' zu pruefen (eine abgewiesene Ersteinrichtung). Dieselbe Bremse wie beim Anmelden.</summary>
        Friend Shared Sub RegisterFailedAttempt(ParamArray counters As String())
            For Each key In If(counters, Array.Empty(Of String)())
                If Not String.IsNullOrEmpty(key) Then RegisterFailure(key)
            Next
        End Sub

        ''' <summary>Sperrt beim Start, wenn ein Kennwort gesetzt ist.</summary>
        Public Shared Sub LockAtStart()
            If IsProtected Then Lock("start", SecurityLog.NoValue, SecurityLog.NoValue)
        End Sub

        ''' <summary>Sperrt die Anwendung. Ohne Kennwort geschieht nichts. <paramref name="reason"/>
        ''' steht im Protokoll (SecurityLog): manual, idle, start. Ohne <paramref name="user"/> ist es,
        ''' wer an der vollen Oberflaeche sitzt.</summary>
        Public Shared Sub Lock(Optional reason As String = "manual", Optional user As String = Nothing, Optional from As String = Nothing)
            If Not IsProtected Then Return
            SyncLock StateLock
                If _isLocked Then Return
                _isLocked = True
            End SyncLock
            If user Is Nothing Then
                Dim actor = SecurityLog.DesktopActor()
                user = actor.User
                from = actor.From
            End If
            SecurityLog.Locked(reason, user, from)
            RaiseEvent LockStateChanged(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>Entsperrt ohne Pruefung nach dem Anmeldefenster der Anwendung, siehe
        ''' <see cref="UnlockBy"/>.</summary>
        Friend Shared Sub Unlock()
            UnlockBy("lockscreen")
        End Sub

        ''' <summary>Entsperrt ohne Pruefung. Nur nach einer gelungenen Pruefung und wenn der Schutz
        ''' abgeschaltet wird. <paramref name="via"/> steht im Protokoll: signin, lockscreen,
        ''' settings; ohne <paramref name="user"/> ist es, wer an der vollen Oberflaeche sitzt.</summary>
        Friend Shared Sub UnlockBy(via As String, Optional user As String = Nothing, Optional from As String = Nothing)
            SyncLock StateLock
                _lastActivityUtc = Clock()
                If Not _isLocked Then Return
                _isLocked = False
            End SyncLock
            If user Is Nothing Then
                Dim actor = SecurityLog.DesktopActor()
                user = actor.User
                from = actor.From
            End If
            SecurityLog.Unlocked(via, user, from)
            RaiseEvent LockStateChanged(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>Prueft das Kennwort und entsperrt bei Erfolg. Langsam (PBKDF2), deshalb nie auf
        ''' dem Anzeigefaden rufen.</summary>
        Public Shared Function TryUnlock(password As String, sender As String) As PasswordCheckResult
            Dim result = CheckPassword(password, sender)
            If result.Outcome = PasswordCheckOutcome.Accepted Then Unlock()
            Return result
        End Function

        ''' <summary>Prueft das Kennwort, ohne etwas zu entsperren (Einstellungen, Browser).
        '''
        ''' <para>GEZAEHLT WIRD JE ZAEHLER, ES GIBT KEINEN GEMEINSAMEN FUER DIE GANZE ANWENDUNG.
        ''' Am Rechner ist das <see cref="LocalKey"/>; im Browser die
        ''' Anmeldung dieses Browsers UND seine Absenderadresse (BrowserBridge.LoginCounterKeys):
        ''' gebremst wird, wo falsch getippt wurde, und wer das Cookie wegwirft, um von vorn zu
        ''' zaehlen, bleibt ueber die Adresse gebremst. Andere Browser an anderen Adressen und das
        ''' Anmeldefenster am Rechner merken davon nichts. Die Wartezeit ist die laengste der
        ''' genannten Zaehler; ein Fehlversuch zaehlt bei allen, ein Erfolg setzt alle zurueck.</para></summary>
        Public Shared Function CheckPassword(password As String, ParamArray counters As String()) As PasswordCheckResult
            Return Check(password, Nothing, counters)
        End Function

        ''' <summary>Wie <see cref="CheckPassword"/>, dazu der Benutzername (Anmeldung im Browser),
        ''' gegen alle Benutzer, die sich anmelden duerfen (UserAccountService.ResolveSignIn); wer es
        ''' war, steht im Ergebnis (<see cref="PasswordCheckResult.UserId"/>). Gross- und
        ''' Kleinschreibung zaehlen beim Namen nicht (Telefone schreiben das erste Zeichen gern
        ''' gross). Ein falscher Name zaehlt wie ein falsches Kennwort: dieselbe Antwort, dieselbe
        ''' Rechenzeit (das Kennwort wird immer geprueft), dieselbe Bremse, und kein Hinweis, ob es
        ''' den Namen gibt; ein gesperrter Benutzer ebenso. Ist fuer den Eigentuemer noch kein Name
        ''' gespeichert (Kennwort aus der einmaligen Uebernahme), passt ihm jeder gueltige, den kein anderer
        ''' traegt; die Anmeldung legt ihn dann fest (<see cref="AdoptUserName"/>).</summary>
        Public Shared Function CheckSignIn(userName As String, password As String, ParamArray counters As String()) As PasswordCheckResult
            Return Check(password, If(userName, ""), counters)
        End Function

        Private Shared Function Check(password As String, userName As String, counters As String()) As PasswordCheckResult
            ' Mit Namen ist es immer die Anmeldung im Browser und damit das Konto des Eigentuemers;
            ' ohne Namen die Sperre, die gerade gilt (am Rechner das Anmeldefenster).
            Dim hash = If(userName Is Nothing, CurrentHash(), OwnerHash())
            If hash.Length = 0 Then Return New PasswordCheckResult(PasswordCheckOutcome.NotProtected, TimeSpan.Zero)
            Dim keys = If(counters, Array.Empty(Of String)()).Where(Function(k) Not String.IsNullOrEmpty(k)).Distinct().ToArray()
            If keys.Length = 0 Then keys = {LocalKey}
            Dim wait = RetryAfter(keys)
            If wait > TimeSpan.Zero Then Return New PasswordCheckResult(PasswordCheckOutcome.Throttled, wait)
            Dim matches As Boolean
            Dim userId = OwnerUserId
            ' Eines zur Zeit: sonst liessen sich viele Versuche gleichzeitig starten, bevor der
            ' erste Fehlversuch gezaehlt ist.
            SyncLock VerifyGate
                wait = RetryAfter(keys)
                If wait > TimeSpan.Zero Then Return New PasswordCheckResult(PasswordCheckOutcome.Throttled, wait)
                If userName Is Nothing Then
                    matches = VerifyHash(If(password, ""), hash)
                Else
                    ' Mit Namen (Browser): der Benutzer dazu, Eigentuemer oder ein weiterer. Gibt es
                    ' ihn nicht, wird trotzdem gerechnet, gegen den Hash des Eigentuemers, und das
                    ' Ergebnis verworfen: dieselbe Zeit, dieselbe Antwort, kein Hinweis auf den Namen.
                    Dim candidate = UserAccountService.ResolveSignIn(userName, hash)
                    Dim verified = VerifyHash(If(password, ""), If(candidate.HasValue, candidate.Value.Hash, hash))
                    matches = verified AndAlso candidate.HasValue
                    If matches Then userId = candidate.Value.Id
                End If
                For Each key In keys
                    If matches Then
                        ClearFailures(key)
                    Else
                        RegisterFailure(key)
                    End If
                Next
            End SyncLock
            If matches Then Return New PasswordCheckResult(PasswordCheckOutcome.Accepted, TimeSpan.Zero, userId)
            DiagnosticLogService.LogAlways("AppLock", $"Wrong password from {String.Join(", ", keys)}.")
            Return New PasswordCheckResult(PasswordCheckOutcome.Rejected, RetryAfter(keys))
        End Function

        ''' <summary>Prueft das Kennwort eines bestimmten Benutzers (Eigentuemer oder weiterer),
        ''' etwa vor dem Abschalten seines zweiten Faktors. Dieselbe Bremse wie beim Anmelden, eines
        ''' zur Zeit, in fester Zeit; ohne Hash passt nichts. Langsam (PBKDF2).</summary>
        Friend Shared Function CheckUserPassword(userId As String, password As String, ParamArray counters As String()) As PasswordCheckResult
            Dim hash As String
            If userId = OwnerUserId Then
                ' Das Konto des Eigentuemers, nie das Anmeldefenster am Rechner.
                hash = OwnerHash()
            Else
                Dim stored As String = Nothing
                Dim hashes = AppSettingsService.Load().BrowserUserPasswordHashes
                hash = If(hashes IsNot Nothing AndAlso hashes.TryGetValue(If(userId, ""), stored), If(stored, ""), "")
            End If
            Dim keys = If(counters, Array.Empty(Of String)()).Where(Function(k) Not String.IsNullOrEmpty(k)).Distinct().ToArray()
            If keys.Length = 0 Then keys = {LocalKey}
            Dim matches As Boolean
            SyncLock VerifyGate
                Dim wait = RetryAfter(keys)
                If wait > TimeSpan.Zero Then Return New PasswordCheckResult(PasswordCheckOutcome.Throttled, wait)
                ' Auch ohne Hash wird gerechnet, gegen den des Eigentuemers: gleiche Zeit.
                Dim verified = VerifyHash(If(password, ""), If(hash.Length > 0, hash, OwnerHash()))
                matches = verified AndAlso hash.Length > 0
                For Each key In keys
                    If matches Then
                        ClearFailures(key)
                    Else
                        RegisterFailure(key)
                    End If
                Next
            End SyncLock
            If matches Then Return New PasswordCheckResult(PasswordCheckOutcome.Accepted, TimeSpan.Zero, userId)
            Return New PasswordCheckResult(PasswordCheckOutcome.Rejected, RetryAfter(keys))
        End Function

        ''' <summary>Vergisst die Fehlversuche der genannten Zaehler (ein gelungener zweiter Schritt
        ''' der Anmeldung).</summary>
        Friend Shared Sub ClearFailedAttempts(ParamArray counters As String())
            For Each key In If(counters, Array.Empty(Of String)())
                If Not String.IsNullOrEmpty(key) Then ClearFailures(key)
            Next
        End Sub

        ''' <summary>Wie lange noch gewartet werden muss: die laengste Wartezeit der Zaehler. Ein
        ''' Zaehler ohne Eintrag wartet nie, auch nicht bei voller Liste: sonst koennte, wer mit
        ''' vielen Adressen flutet, alle Anmeldungen warten lassen (<see cref="RegisterFailure"/>).</summary>
        Public Shared Function RetryAfter(ParamArray counters As String()) As TimeSpan
            Dim now = Clock()
            Dim until = DateTime.MinValue
            SyncLock StateLock
                For Each key In If(counters, Array.Empty(Of String)())
                    If key Is Nothing Then Continue For
                    Dim record As FailureRecord = Nothing
                    If Failures.TryGetValue(key, record) AndAlso record.BlockedUntilUtc > until Then until = record.BlockedUntilUtc
                Next
            End SyncLock
            Return If(until > now, until - now, TimeSpan.Zero)
        End Function

        ''' <summary>Die Liste, in der ein Zaehler gefuehrt wird.</summary>
        Private Shared Function KindOf(key As String) As CounterKind
            If String.Equals(key, LocalKey, StringComparison.Ordinal) Then Return CounterKind.Local
            If key.StartsWith(BrowserCounterPrefix, StringComparison.Ordinal) Then Return CounterKind.Browser
            If key.StartsWith(AccountCounterPrefix, StringComparison.Ordinal) Then Return CounterKind.Account
            If key.StartsWith(ShareCounterPrefix, StringComparison.Ordinal) Then Return CounterKind.Share
            Return CounterKind.Address
        End Function

        ''' <summary>Die Grenze einer Liste. Der Rechner selbst hat keine: sein Zaehler ist einer.</summary>
        Private Shared Function LimitOf(kind As CounterKind) As Integer
            Select Case kind
                Case CounterKind.Local : Return Integer.MaxValue
                Case CounterKind.Browser : Return MaxTrackedBrowsers
                Case CounterKind.Account : Return MaxTrackedAccounts
                Case CounterKind.Share : Return MaxTrackedShares
                Case Else : Return MaxTrackedAddresses
            End Select
        End Function

        ''' <summary>Zaehlt einen Fehlversuch.
        '''
        ''' <para>VIER LISTEN MIT EIGENER GRENZE (Browser, Konten, Freigaben, Adressen samt allem
        ''' Uebrigen), damit eine Flut der einen Art die andere nicht verdraengt. Der Zaehler des
        ''' Rechners (<see cref="LocalKey"/>) steht in keiner: er wird nie verdraengt und nie von
        ''' einer vollen Liste aufgehalten.</para>
        '''
        ''' <para>Laeuft eine Liste ueber, faellt der UNGEBREMSTE Eintrag heraus, der am laengsten
        ''' keinen Fehlversuch hatte; ein gebremster faellt nie heraus. Wer mit vielen neuen
        ''' Absendern flutet, verdraengt also nur ungebremste Zaehler und loest damit nicht seine
        ''' eigene Bremse. Sind alle gebremst, wird der neue Absender durchgelassen, ohne zu zaehlen:
        ''' liesse man ihn warten, koennte eine Flut aus vielen Adressen alle Anmeldungen aussperren.
        ''' Nur Browser duerfen auch gebremste verdraengen: ihr Zaehler steht nie allein, die Adresse
        ''' bremst weiter.</para></summary>
        Private Shared Sub RegisterFailure(key As String)
            Dim now = Clock()
            SyncLock StateLock
                Dim record As FailureRecord = Nothing
                If Not Failures.TryGetValue(key, record) Then
                    Dim kind = KindOf(key)
                    Dim same = If(kind = CounterKind.Local, Nothing, Failures.Where(Function(pair) KindOf(pair.Key) = kind).ToList())
                    If same IsNot Nothing AndAlso same.Count >= LimitOf(kind) Then
                        Dim free = same.Where(Function(pair) pair.Value.BlockedUntilUtc <= now).ToList()
                        If free.Count > 0 Then
                            Failures.Remove(free.OrderBy(Function(pair) pair.Value.LastUtc).First().Key)
                        ElseIf kind = CounterKind.Browser Then
                            Failures.Remove(same.OrderBy(Function(pair) pair.Value.LastUtc).First().Key)
                        Else
                            Return
                        End If
                    End If
                    record = New FailureRecord()
                    Failures(key) = record
                End If
                CountFailure(record, FreeAttempts, now)
            End SyncLock
        End Sub

        ''' <summary>Wie viele Fehlversuche ein Zaehler gerade hat, 0 ohne Eintrag. Fuer den
        ''' Pruefstand.</summary>
        Friend Shared Function FailureCount(key As String) As Integer
            SyncLock StateLock
                Dim record As FailureRecord = Nothing
                Return If(key IsNot Nothing AndAlso Failures.TryGetValue(key, record), record.Count, 0)
            End SyncLock
        End Function

        ''' <summary>Wie viele Zaehler es gerade gibt, deren Schluessel so beginnt. Fuer den
        ''' Pruefstand.</summary>
        Friend Shared Function CounterCount(prefix As String) As Integer
            SyncLock StateLock
                Return Failures.Keys.Where(Function(k) k.StartsWith(If(prefix, ""), StringComparison.Ordinal)).Count()
            End SyncLock
        End Function

        Private Shared Sub CountFailure(record As FailureRecord, free As Integer, now As DateTime)
            record.Count += 1
            record.LastUtc = now
            If record.Count <= free Then Return
            ' 2, 4, 8 ... Sekunden, hoechstens fuenf Minuten. Der Exponent ist begrenzt, damit die
            ' Rechnung nie ueberlaeuft (VB prueft Ganzzahlen).
            Dim exponent = Math.Min(record.Count - free, 9)
            Dim seconds = Math.Min(MaxDelaySeconds, 1 << exponent)
            record.BlockedUntilUtc = now.AddSeconds(seconds)
        End Sub

        Private Shared Sub ClearFailures(key As String)
            SyncLock StateLock
                Failures.Remove(key)
            End SyncLock
        End Sub

        ''' <summary>Vergisst alle Fehlversuche. Fuer den Pruefstand, wenn er die Bruecke anhaelt.</summary>
        Friend Shared Sub ClearAllFailures()
            SyncLock StateLock
                Failures.Clear()
            End SyncLock
        End Sub

        ''' <summary>Eine Eingabe am Fenster (Taste, Maus, Rad) oder eine Anfrage der mobilen Seite
        ''' mit Sitzung (BrowserBridge.HandleMobileRouteAsync). Setzt die Zeit bis zur Sperre
        ''' zurueck; kostet nur das Setzen einer Uhrzeit und geht von jedem Faden aus.</summary>
        Public Shared Sub NoteActivity()
            Dim now = Clock()
            SyncLock StateLock
                _lastActivityUtc = now
            End SyncLock
        End Sub

        ''' <summary>Sperrt, wenn die eingestellte Zeit ohne Eingabe vergangen ist. True, wenn
        ''' dabei gesperrt wurde. Das Fenster ruft das regelmaessig.</summary>
        Public Shared Function CheckIdle() As Boolean
            Dim minutes = IdleMinutes
            If minutes <= 0 OrElse Not IsProtected OrElse IsLocked Then Return False
            Dim last As DateTime
            SyncLock StateLock
                last = _lastActivityUtc
            End SyncLock
            If Clock() - last < TimeSpan.FromMinutes(minutes) Then Return False
            Lock("idle", SecurityLog.NoValue, SecurityLog.NoValue)
            Return True
        End Function

        ''' <summary>Der Hash zu einem Kennwort, als eine Zeile:
        ''' <c>pbkdf2-sha256$Runden$Salz$Ergebnis</c>, Salz und Ergebnis in Base64.</summary>
        Public Shared Function HashPassword(password As String) As String
            Dim salt = RandomNumberGenerator.GetBytes(SaltLength)
            Dim rounds = Math.Max(1, Iterations)
            Dim derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(If(password, "")), salt, rounds,
                                                     HashAlgorithmName.SHA256, HashLength)
            Return String.Join("$", HashScheme, rounds.ToString(CultureInfo.InvariantCulture),
                               Convert.ToBase64String(salt), Convert.ToBase64String(derived))
        End Function

        ''' <summary>Ob ein Kennwort zu einem Hash passt, verglichen in fester Zeit. Ein
        ''' unlesbarer Hash passt zu nichts.</summary>
        Public Shared Function VerifyHash(password As String, stored As String) As Boolean
            If String.IsNullOrEmpty(stored) Then Return False
            Dim parts = stored.Split("$"c)
            If parts.Length <> 4 OrElse parts(0) <> HashScheme Then Return False
            Dim rounds As Integer
            If Not Integer.TryParse(parts(1), NumberStyles.None, CultureInfo.InvariantCulture, rounds) OrElse rounds < 1 Then Return False
            Dim salt As Byte()
            Dim expected As Byte()
            Try
                salt = Convert.FromBase64String(parts(2))
                expected = Convert.FromBase64String(parts(3))
            Catch ex As FormatException
                Return False
            End Try
            If expected.Length = 0 Then Return False
            Dim actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(If(password, "")), salt, rounds,
                                                   HashAlgorithmName.SHA256, expected.Length)
            Return CryptographicOperations.FixedTimeEquals(actual, expected)
        End Function

        ''' <summary>Setzt Sperre, Fehlversuche, Uhr und Rundenzahl zurueck. Fuer den Pruefstand.</summary>
        Friend Shared Sub ResetForTests()
            SyncLock StateLock
                _isLocked = False
                _lastActivityUtc = DateTime.UtcNow
                Failures.Clear()
            End SyncLock
            Clock = Function() DateTime.UtcNow
            Iterations = DefaultIterations
            TestPasswordHash = ""
            TestUserName = ""
        End Sub

    End Class

End Namespace
