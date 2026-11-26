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

    ''' <summary>Ergebnis einer Pruefung samt der Wartezeit, wenn zu oft falsch geraten wurde.</summary>
    Public NotInheritable Class PasswordCheckResult
        Public Sub New(outcome As PasswordCheckOutcome, retryAfter As TimeSpan)
            Me.Outcome = outcome
            Me.RetryAfter = retryAfter
        End Sub

        Public ReadOnly Property Outcome As PasswordCheckOutcome
        Public ReadOnly Property RetryAfter As TimeSpan
    End Class

    ''' <summary>
    ''' Das Anmeldefenster der Anwendung: ein Kennwort, die Sperre und die Zeit ohne Eingabe.
    '''
    ''' <para>EIN KENNWORT, NUR ALS HASH. Gespeichert wird PBKDF2 mit SHA-256, eigenem Salz und
    ''' hoher Rundenzahl in AppSettings.AppLockPasswordHash, verglichen in fester Zeit. Argon2id
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
    ''' <para>IM BROWSER (BrowserBridge) gilt dasselbe Kennwort fuer die Sitzungen; eine Sperre
    ''' beendet sie alle (<see cref="LockStateChanged"/>). Ein beim Start mitgegebenes Kennwort
    ''' (<see cref="SetStartPassword"/>) geht vor dem gespeicherten.</para>
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
        ''' Setzen in den Einstellungen und fuer das beim Start mitgegebene, nie beim Entsperren
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

        ''' <summary>Hoechstens so viele Absender merkt sich die Wartezeit.</summary>
        Private Const MaxTrackedSenders As Integer = 256

        ''' <summary>Die erlaubten Zeiten ohne Eingabe in Minuten; 0 heisst nie.</summary>
        Public Shared ReadOnly IdleChoices As Integer() = {0, 5, 15, 30, 60}

        ''' <summary>Die Uhr. Der Pruefstand stellt sie vor, statt Minuten zu warten.</summary>
        Friend Shared Clock As Func(Of DateTime) = Function() DateTime.UtcNow

        ''' <summary>Runden fuer neue Hashes. Nur der Pruefstand setzt weniger, damit er nicht
        ''' Sekunden je Pruefung rechnet; gelesen wird die Zahl immer aus dem Hash selbst.</summary>
        Friend Shared Iterations As Integer = DefaultIterations

        Private Shared ReadOnly StateLock As New Object()
        Private Shared ReadOnly VerifyGate As New Object()
        Private Shared _isLocked As Boolean
        Private Shared _lastActivityUtc As DateTime = DateTime.UtcNow
        Private Shared _startPasswordHash As String = ""
        Private Shared ReadOnly Failures As New Dictionary(Of String, FailureRecord)(StringComparer.Ordinal)

        Private NotInheritable Class FailureRecord
            Public Count As Integer
            Public BlockedUntilUtc As DateTime
            Public LastUtc As DateTime
        End Class

        ''' <summary>Gesperrt oder entsperrt. Kann von einem Hintergrundfaden kommen (Anmeldung im
        ''' Browser); wer die Oberflaeche anfasst, wechselt selbst auf den Anzeigefaden.</summary>
        Public Shared Event LockStateChanged As EventHandler

        ''' <summary>Der Hash, gegen den geprueft wird: der des beim Start mitgegebenen Kennworts,
        ''' sonst der gespeicherte.</summary>
        Private Shared Function CurrentHash() As String
            Dim fromStart = _startPasswordHash
            If Not String.IsNullOrEmpty(fromStart) Then Return fromStart
            Return If(AppSettingsService.Load().AppLockPasswordHash, "")
        End Function

        ''' <summary>Der geltende Hash als Marke: aendert sich das Kennwort, aendert sie sich mit.
        ''' Fuer Zwischenspeicher, die eine gelungene Pruefung kurz behalten (BrowserBridge).</summary>
        Friend Shared Function PasswordStamp() As String
            Return CurrentHash()
        End Function

        ''' <summary>Ist ein Kennwort gesetzt? Nur dann gibt es Anmeldefenster und Sperre.</summary>
        Public Shared ReadOnly Property IsProtected As Boolean
            Get
                Return CurrentHash().Length > 0
            End Get
        End Property

        ''' <summary>Kam das Kennwort beim Start mit (Behaelter)? Dann laesst es sich in den
        ''' Einstellungen weder aendern noch abschalten: beim naechsten Start gaelte wieder das
        ''' mitgegebene.</summary>
        Public Shared ReadOnly Property IsPasswordFromStart As Boolean
            Get
                Return Not String.IsNullOrEmpty(_startPasswordHash)
            End Get
        End Property

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

        ''' <summary>Das beim Start mitgegebene Kennwort (Browser, Behaelter). Es geht vor dem
        ''' gespeicherten und wird nur als Hash gehalten. Mit <paramref name="persist"/> wird es
        ''' auch in die Einstellungen uebernommen, damit der Behaelter geschuetzt bleibt, wenn die
        ''' Angabe beim naechsten Start fehlt; ein schon passender Hash bleibt dabei stehen.</summary>
        Friend Shared Sub SetStartPassword(password As String, persist As Boolean)
            If String.IsNullOrEmpty(password) Then
                _startPasswordHash = ""
                Return
            End If
            Dim stored = If(AppSettingsService.Load().AppLockPasswordHash, "")
            Dim hash As String
            If stored.Length > 0 AndAlso VerifyHash(password, stored) Then
                hash = stored
            Else
                hash = HashPassword(password)
                If persist Then AppSettingsService.Update(Sub(s) s.AppLockPasswordHash = hash)
            End If
            _startPasswordHash = hash
        End Sub

        ''' <summary>Vergisst das beim Start mitgegebene Kennwort. Fuer den Pruefstand.</summary>
        Friend Shared Sub ClearStartPassword()
            _startPasswordHash = ""
        End Sub

        ''' <summary>Sperrt beim Start, wenn ein Kennwort gesetzt ist.</summary>
        Public Shared Sub LockAtStart()
            If IsProtected Then Lock()
        End Sub

        ''' <summary>Sperrt die Anwendung. Ohne Kennwort geschieht nichts.</summary>
        Public Shared Sub Lock()
            If Not IsProtected Then Return
            SyncLock StateLock
                If _isLocked Then Return
                _isLocked = True
            End SyncLock
            DiagnosticLogService.LogAlways("AppLock", "Locked.")
            RaiseEvent LockStateChanged(Nothing, EventArgs.Empty)
        End Sub

        ''' <summary>Entsperrt ohne Pruefung. Nur nach einer gelungenen Pruefung und wenn der Schutz
        ''' abgeschaltet wird.</summary>
        Friend Shared Sub Unlock()
            SyncLock StateLock
                _lastActivityUtc = Clock()
                If Not _isLocked Then Return
                _isLocked = False
            End SyncLock
            DiagnosticLogService.LogAlways("AppLock", "Unlocked.")
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
            Dim hash = CurrentHash()
            If hash.Length = 0 Then Return New PasswordCheckResult(PasswordCheckOutcome.NotProtected, TimeSpan.Zero)
            Dim keys = If(counters, Array.Empty(Of String)()).Where(Function(k) Not String.IsNullOrEmpty(k)).Distinct().ToArray()
            If keys.Length = 0 Then keys = {LocalKey}
            Dim wait = RetryAfter(keys)
            If wait > TimeSpan.Zero Then Return New PasswordCheckResult(PasswordCheckOutcome.Throttled, wait)
            Dim matches As Boolean
            ' Eines zur Zeit: sonst liessen sich viele Versuche gleichzeitig starten, bevor der
            ' erste Fehlversuch gezaehlt ist.
            SyncLock VerifyGate
                wait = RetryAfter(keys)
                If wait > TimeSpan.Zero Then Return New PasswordCheckResult(PasswordCheckOutcome.Throttled, wait)
                matches = VerifyHash(If(password, ""), hash)
                For Each key In keys
                    If matches Then
                        ClearFailures(key)
                    Else
                        RegisterFailure(key)
                    End If
                Next
            End SyncLock
            If matches Then Return New PasswordCheckResult(PasswordCheckOutcome.Accepted, TimeSpan.Zero)
            DiagnosticLogService.LogAlways("AppLock", $"Wrong password from {String.Join(", ", keys)}.")
            Return New PasswordCheckResult(PasswordCheckOutcome.Rejected, RetryAfter(keys))
        End Function

        ''' <summary>Wie lange noch gewartet werden muss: die laengste Wartezeit der Zaehler.</summary>
        Public Shared Function RetryAfter(ParamArray counters As String()) As TimeSpan
            Dim now = Clock()
            Dim until = DateTime.MinValue
            SyncLock StateLock
                For Each key In If(counters, Array.Empty(Of String)())
                    Dim record As FailureRecord = Nothing
                    If key IsNot Nothing AndAlso Failures.TryGetValue(key, record) AndAlso record.BlockedUntilUtc > until Then
                        until = record.BlockedUntilUtc
                    End If
                Next
            End SyncLock
            Return If(until > now, until - now, TimeSpan.Zero)
        End Function

        Private Shared Sub RegisterFailure(key As String)
            Dim now = Clock()
            SyncLock StateLock
                Dim record As FailureRecord = Nothing
                If Not Failures.TryGetValue(key, record) Then
                    If Failures.Count >= MaxTrackedSenders Then
                        Dim oldest = Failures.OrderBy(Function(pair) pair.Value.LastUtc).First().Key
                        Failures.Remove(oldest)
                    End If
                    record = New FailureRecord()
                    Failures(key) = record
                End If
                CountFailure(record, FreeAttempts, now)
            End SyncLock
        End Sub

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

        ''' <summary>Eine Eingabe am Fenster (Taste, Maus, Rad). Setzt die Zeit bis zur Sperre
        ''' zurueck; kostet nur das Setzen einer Uhrzeit.</summary>
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
            Lock()
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
            _startPasswordHash = ""
        End Sub

    End Class

End Namespace
