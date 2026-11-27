Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports QRCoder
Imports SkiaSharp

Namespace Services

    ''' <summary>Wie die Pruefung eines Codes aus der App ausgegangen ist.</summary>
    Public Enum TwoFactorOutcome
        ''' <summary>Der Code stimmt und war noch nicht benutzt.</summary>
        Accepted
        ''' <summary>Der Code stimmt nicht (auch: nicht sechs Ziffern).</summary>
        Rejected
        ''' <summary>Der Code stimmt, sein Zeitschritt ist aber schon benutzt worden.</summary>
        Reused
        ''' <summary>Fuer diesen Benutzer ist kein zweiter Faktor eingerichtet.</summary>
        NotEnabled
    End Enum

    ''' <summary>Ein begonnenes Einrichten: das neue Geheimnis und die Adresse fuer den QR-Code.
    ''' Gilt erst, wenn ein Code dazu bestaetigt ist.</summary>
    Public NotInheritable Class TwoFactorEnrollment
        ''' <summary>Das Geheimnis in Base32, ohne Auffuellung, in Grossbuchstaben.</summary>
        Public Property Secret As String = ""
        ''' <summary>otpauth://totp/... wie sie gaengige Authenticator-Apps lesen.</summary>
        Public Property Uri As String = ""
    End Class

    ''' <summary>
    ''' Der zweite Faktor fuer die Anmeldung im Browser: Codes nach RFC 6238 (TOTP, HMAC-SHA1,
    ''' sechs Ziffern, 30 Sekunden), wie sie gaengige Authenticator-Apps erzeugen.
    '''
    ''' <para>GESPEICHERT je Benutzer in den Einstellungen neben dem Kennwort-Hash
    ''' (AppSettings.TwoFactorSecrets, Schluessel ist die Benutzerkennung, auch
    ''' <see cref="AppLockService.OwnerUserId"/>). Das Geheimnis steht in der Datei verschluesselt wie
    ''' das Kennwort des Mailservers (AppSettingsService.SecretMapFieldNames), in einer Sicherung nur
    ''' mit dem Teil "Zugangsdaten". Die Wiederherstellungscodes stehen nur als SHA-256
    ''' (TwoFactorRecoveryHashes); sie tragen 60 Bit aus dem Zufall, ein schneller Hash genuegt.
    ''' Der zuletzt benutzte Zeitschritt (TwoFactorLastSteps) verhindert, dass derselbe Code
    ''' zweimal gilt.</para>
    '''
    ''' <para>EINRICHTEN IN ZWEI SCHRITTEN: <see cref="BeginEnrollment"/> legt ein Geheimnis nur im
    ''' Speicher an (zehn Minuten), erst <see cref="ConfirmEnrollment"/> mit einem gueltigen Code
    ''' dazu schreibt es und liefert die zehn Wiederherstellungscodes, die danach nirgends mehr im
    ''' Klartext stehen.</para>
    '''
    ''' <para>GEPRUEFT in fester Zeit: die drei Codes des Fensters (ein Zeitschritt davor und
    ''' danach) werden immer alle gerechnet und verglichen. Die Bremse gegen Raten liegt beim
    ''' Aufrufer (BrowserBridge), dieselbe wie beim Kennwort (AppLockService).</para>
    '''
    ''' <para>NUR IM BROWSER. Das Anmeldefenster der Anwendung am Rechner fragt keinen Code: wer am
    ''' Rechner sitzt, hat ihn ohnehin. Werkzeuge mit Basic werden fuer Konten mit zweitem Faktor
    ''' abgewiesen.</para>
    ''' </summary>
    Public NotInheritable Class TwoFactorService

        Private Sub New()
        End Sub

        Public Const Digits As Integer = 6
        Public Const PeriodSeconds As Integer = 30
        ''' <summary>160 Bit, die Laenge, die RFC 4226 fuer HMAC-SHA1 empfiehlt.</summary>
        Public Const SecretBytes As Integer = 20
        Public Const RecoveryCodeCount As Integer = 10
        Public Const Issuer As String = "FerrumPix"

        Private Const Base32Alphabet As String = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567"
        Private Const RecoveryAlphabet As String = "abcdefghijklmnopqrstuvwxyz234567"

        ''' <summary>So lange gilt ein begonnenes Einrichten ohne Bestaetigung.</summary>
        Friend Shared ReadOnly EnrollmentLifetime As TimeSpan = TimeSpan.FromMinutes(10)

        Private Shared ReadOnly UnixEpoch As New DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        Private Shared ReadOnly Gate As New Object()
        Private Shared ReadOnly Pending As New Dictionary(Of String, PendingSecret)(StringComparer.Ordinal)

        Private NotInheritable Class PendingSecret
            Public Secret As Byte()
            Public UntilUtc As DateTime
        End Class

        ' --- RFC 4226 und RFC 6238 ------------------------------------------------------------

        ''' <summary>Der Code zu einem Zeitschritt (HOTP nach RFC 4226 mit dem Zeitschritt als
        ''' Zaehler, HMAC-SHA1). <paramref name="digits"/> ist nur fuer die Testvektoren der RFC
        ''' anders als sechs.</summary>
        Public Shared Function ComputeCode(secret As Byte(), timeStep As Long, Optional digits As Integer = TwoFactorService.Digits) As String
            Dim message(7) As Byte
            Dim value = timeStep
            For i = 7 To 0 Step -1
                message(i) = CByte(value And &HFFL)
                value >>= 8
            Next
            Dim hash = HMACSHA1.HashData(secret, message)
            Dim offset = hash(hash.Length - 1) And &HF
            ' Erst nach Integer wandeln: ein Byte zu schieben waere in VB wirkungslos.
            Dim binary = ((CInt(hash(offset)) And &H7F) << 24) Or (CInt(hash(offset + 1)) << 16) Or
                         (CInt(hash(offset + 2)) << 8) Or CInt(hash(offset + 3))
            Dim modulo = 1
            For i = 1 To digits
                modulo *= 10
            Next
            Return (binary Mod modulo).ToString("D" & digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
        End Function

        ''' <summary>Der Zeitschritt zu einem Zeitpunkt: volle 30 Sekunden seit 1970 (UTC).</summary>
        Public Shared Function TimeStepAt(utc As DateTime) As Long
            Return CLng(Math.Floor((utc - UnixEpoch).TotalSeconds / PeriodSeconds))
        End Function

        ''' <summary>Base32 nach RFC 4648, Grossbuchstaben, ohne Auffuellung.</summary>
        Public Shared Function Base32Encode(data As Byte()) As String
            Dim text As New StringBuilder()
            Dim buffer = 0
            Dim bits = 0
            For Each value In If(data, Array.Empty(Of Byte)())
                buffer = (buffer << 8) Or CInt(value)
                bits += 8
                While bits >= 5
                    text.Append(Base32Alphabet((buffer >> (bits - 5)) And 31))
                    bits -= 5
                End While
                buffer = buffer And ((1 << bits) - 1)
            Next
            If bits > 0 Then text.Append(Base32Alphabet((buffer << (5 - bits)) And 31))
            Return text.ToString()
        End Function

        ''' <summary>Base32 zurueck; Gross- und Kleinschreibung, Leerzeichen, Bindestriche und
        ''' Auffuellung zaehlen nicht. Nothing bei einem fremden Zeichen.</summary>
        Public Shared Function Base32Decode(text As String) As Byte()
            Dim result As New List(Of Byte)()
            Dim buffer = 0
            Dim bits = 0
            For Each c In If(text, "").ToUpperInvariant()
                If c = " "c OrElse c = "-"c OrElse c = "="c Then Continue For
                Dim index = Base32Alphabet.IndexOf(c)
                If index < 0 Then Return Nothing
                buffer = (buffer << 5) Or index
                bits += 5
                If bits >= 8 Then
                    result.Add(CByte((buffer >> (bits - 8)) And &HFF))
                    bits -= 8
                End If
                buffer = buffer And ((1 << bits) - 1)
            Next
            Return result.ToArray()
        End Function

        ''' <summary>Das Geheimnis zum Abtippen: Base32 in Vierergruppen.</summary>
        Public Shared Function FormatSecret(secret As String) As String
            Dim clean = If(secret, "").Replace(" ", "")
            Dim groups As New List(Of String)()
            For i = 0 To clean.Length - 1 Step 4
                groups.Add(clean.Substring(i, Math.Min(4, clean.Length - i)))
            Next
            Return String.Join(" ", groups)
        End Function

        ''' <summary>Die Adresse fuer die App, nach dem Key-Uri-Format, das gaengige Authenticator-Apps
        ''' lesen: Aussteller und Name im Etikett, dazu Aussteller, Verfahren, Stellen und Takt.</summary>
        Public Shared Function OtpAuthUri(accountName As String, secret As String) As String
            Dim name = If(String.IsNullOrWhiteSpace(accountName), Issuer, accountName.Trim())
            Return "otpauth://totp/" & Uri.EscapeDataString(Issuer) & ":" & Uri.EscapeDataString(name) &
                   "?secret=" & secret & "&issuer=" & Uri.EscapeDataString(Issuer) &
                   "&algorithm=SHA1&digits=" & Digits.ToString(CultureInfo.InvariantCulture) &
                   "&period=" & PeriodSeconds.ToString(CultureInfo.InvariantCulture)
        End Function

        ' --- Gespeicherter Stand ------------------------------------------------------------------

        ''' <summary>Hat der Benutzer einen zweiten Faktor eingerichtet?</summary>
        Public Shared Function IsEnabled(userId As String) As Boolean
            Return StoredSecret(userId) IsNot Nothing
        End Function

        Private Shared Function StoredSecret(userId As String) As Byte()
            If String.IsNullOrEmpty(userId) Then Return Nothing
            Dim secrets = AppSettingsService.Load().TwoFactorSecrets
            Dim text As String = Nothing
            If secrets Is Nothing OrElse Not secrets.TryGetValue(userId, text) OrElse String.IsNullOrEmpty(text) Then Return Nothing
            ' Eine Datei, deren Geheimnis sich nicht entschluesseln liess (anderer Rechner), traegt
            ' noch die Kennung der Verschluesselung; das ist kein Base32 und gilt als eingerichtet,
            ' aber ohne passenden Code. So faellt ein Konto nie still auf "nur Kennwort" zurueck.
            Dim secret = Base32Decode(text)
            If secret Is Nothing OrElse secret.Length = 0 Then Return Array.Empty(Of Byte)()
            Return secret
        End Function

        Private Shared Function StoredRecoveryHashes(settings As AppSettings, userId As String) As List(Of String)
            Dim joined As String = Nothing
            If settings.TwoFactorRecoveryHashes Is Nothing OrElse Not settings.TwoFactorRecoveryHashes.TryGetValue(userId, joined) Then Return New List(Of String)()
            Return If(joined, "").Split(","c, StringSplitOptions.RemoveEmptyEntries).ToList()
        End Function

        ''' <summary>Wie viele Wiederherstellungscodes noch unbenutzt sind.</summary>
        Public Shared Function RecoveryCodesLeft(userId As String) As Integer
            If String.IsNullOrEmpty(userId) Then Return 0
            Return StoredRecoveryHashes(AppSettingsService.Load(), userId).Count
        End Function

        Private Shared Function LastStep(userId As String) As Long
            Dim steps = AppSettingsService.Load().TwoFactorLastSteps
            Dim value As Long
            If steps IsNot Nothing AndAlso steps.TryGetValue(userId, value) Then Return value
            Return Long.MinValue
        End Function

        ''' <summary>Nimmt den zweiten Faktor eines Benutzers aus den Einstellungen, innerhalb einer
        ''' laufenden Aenderung (AppSettingsService.Update): Loeschen eines Benutzers, Notweg.</summary>
        Friend Shared Sub RemoveFrom(settings As AppSettings, userId As String)
            settings.TwoFactorSecrets?.Remove(userId)
            settings.TwoFactorRecoveryHashes?.Remove(userId)
            settings.TwoFactorLastSteps?.Remove(userId)
            ForgetPending(userId)
        End Sub

        ''' <summary>Vergisst ein begonnenes, nicht bestaetigtes Einrichten.</summary>
        Public Shared Sub ForgetPending(userId As String)
            If String.IsNullOrEmpty(userId) Then Return
            SyncLock Gate
                Pending.Remove(userId)
            End SyncLock
        End Sub

        ' --- Einrichten und Abschalten ------------------------------------------------------------

        ''' <summary>Beginnt das Einrichten: ein neues Geheimnis aus 160 Bit Zufall, nur im Speicher.
        ''' Ein frueher begonnenes ersetzt es. Ist schon ein zweiter Faktor eingerichtet, Nothing:
        ''' erst abschalten.</summary>
        Public Shared Function BeginEnrollment(userId As String, accountName As String) As TwoFactorEnrollment
            If String.IsNullOrEmpty(userId) OrElse IsEnabled(userId) Then Return Nothing
            Dim secret = RandomNumberGenerator.GetBytes(SecretBytes)
            SyncLock Gate
                Pending(userId) = New PendingSecret With {.Secret = secret, .UntilUtc = AppLockService.Clock() + EnrollmentLifetime}
            End SyncLock
            Dim text = Base32Encode(secret)
            Return New TwoFactorEnrollment With {.Secret = text, .Uri = OtpAuthUri(accountName, text)}
        End Function

        ''' <summary>Das laufende Einrichten eines Benutzers (fuer den QR-Code), sonst Nothing.</summary>
        Public Shared Function PendingEnrollment(userId As String, accountName As String) As TwoFactorEnrollment
            Dim secret = PendingSecretOf(userId)
            If secret Is Nothing Then Return Nothing
            Dim text = Base32Encode(secret)
            Return New TwoFactorEnrollment With {.Secret = text, .Uri = OtpAuthUri(accountName, text)}
        End Function

        Private Shared Function PendingSecretOf(userId As String) As Byte()
            If String.IsNullOrEmpty(userId) Then Return Nothing
            SyncLock Gate
                Dim entry As PendingSecret = Nothing
                If Not Pending.TryGetValue(userId, entry) Then Return Nothing
                If AppLockService.Clock() > entry.UntilUtc Then
                    Pending.Remove(userId)
                    Return Nothing
                End If
                Return entry.Secret
            End SyncLock
        End Function

        ''' <summary>Schliesst das Einrichten ab, wenn der Code zum begonnenen Geheimnis passt
        ''' (ein Zeitschritt Toleranz). Dann ist der zweite Faktor gespeichert und aktiv, und
        ''' <paramref name="recoveryCodes"/> traegt die zehn Wiederherstellungscodes im Klartext,
        ''' zum einmaligen Zeigen. False ohne begonnenes Einrichten oder mit falschem Code.</summary>
        Public Shared Function ConfirmEnrollment(userId As String, code As String, ByRef recoveryCodes As List(Of String)) As Boolean
            recoveryCodes = Nothing
            Dim secret = PendingSecretOf(userId)
            If secret Is Nothing Then Return False
            Dim matched = MatchingStep(secret, code)
            If Not matched.HasValue Then Return False
            Dim codes = NewRecoveryCodes()
            Dim hashes = String.Join(",", codes.Select(AddressOf RecoveryHash))
            Dim text = Base32Encode(secret)
            SyncLock Gate
                ' Zwei gleichzeitige Bestaetigungen: nur die erste schreibt.
                Dim entry As PendingSecret = Nothing
                If Not Pending.TryGetValue(userId, entry) OrElse entry.Secret IsNot secret Then Return False
                Pending.Remove(userId)
                AppSettingsService.Update(
                    Sub(s)
                        s.TwoFactorSecrets(userId) = text
                        s.TwoFactorRecoveryHashes(userId) = hashes
                        s.TwoFactorLastSteps(userId) = matched.Value
                    End Sub)
                AppSettingsService.FlushNow()
            End SyncLock
            recoveryCodes = codes
            Return True
        End Function

        ''' <summary>Schaltet den zweiten Faktor ab bzw. setzt ihn zurueck (Administrator). Ob
        ''' Kennwort und Code dafuer stimmen, prueft der Aufrufer, ebenso schreibt er das Protokoll.
        ''' True, wenn einer eingerichtet war.</summary>
        Public Shared Function Remove(userId As String) As Boolean
            If String.IsNullOrEmpty(userId) Then Return False
            Dim had = IsEnabled(userId)
            SyncLock Gate
                AppSettingsService.Update(Sub(s) RemoveFrom(s, userId))
                AppSettingsService.FlushNow()
            End SyncLock
            Return had
        End Function

        ' --- Pruefen ---------------------------------------------------------------------------

        ''' <summary>Prueft einen Code aus der App: ein Zeitschritt Toleranz in beide Richtungen,
        ''' feste Vergleichszeit, und ein Zeitschritt gilt nur einmal (auch nicht ein frueherer
        ''' als der zuletzt benutzte). Ein gelungener Code merkt sich seinen Zeitschritt.</summary>
        Public Shared Function VerifyCode(userId As String, code As String) As TwoFactorOutcome
            Dim secret = StoredSecret(userId)
            If secret Is Nothing Then Return TwoFactorOutcome.NotEnabled
            SyncLock Gate
                Dim matched = MatchingStep(secret, code)
                If Not matched.HasValue Then Return TwoFactorOutcome.Rejected
                If matched.Value <= LastStep(userId) Then Return TwoFactorOutcome.Reused
                Dim value = matched.Value
                AppSettingsService.Update(Sub(s) s.TwoFactorLastSteps(userId) = value)
                AppSettingsService.FlushNow()
            End SyncLock
            Return TwoFactorOutcome.Accepted
        End Function

        ''' <summary>Der Zeitschritt, zu dem der Code passt (jetzt, davor, danach), sonst Nothing.
        ''' Es werden immer alle drei gerechnet und verglichen.</summary>
        Private Shared Function MatchingStep(secret As Byte(), code As String) As Long?
            Dim given = NormalizeCode(code)
            Dim givenBytes = Encoding.ASCII.GetBytes(given)
            Dim now = TimeStepAt(AppLockService.Clock())
            Dim matched As Long? = Nothing
            For offset = -1L To 1L
                Dim expected = Encoding.ASCII.GetBytes(ComputeCode(If(secret, Array.Empty(Of Byte)()), now + offset))
                Dim same = CryptographicOperations.FixedTimeEquals(givenBytes, expected)
                If same AndAlso given.Length = Digits AndAlso secret IsNot Nothing AndAlso secret.Length > 0 Then matched = now + offset
            Next
            Return matched
        End Function

        ''' <summary>Nur die Ziffern; mit anderer Laenge als sechs passt der Code zu nichts.</summary>
        Private Shared Function NormalizeCode(code As String) As String
            Dim text = New String(If(code, "").Where(Function(c) Not Char.IsWhiteSpace(c)).ToArray())
            If text.Length <> Digits OrElse Not text.All(Function(c) c >= "0"c AndAlso c <= "9"c) Then Return ""
            Return text
        End Function

        ''' <summary>Loest einen Wiederherstellungscode ein: passt er zu einem der gespeicherten,
        ''' ist er danach verbraucht. Verglichen wird mit jedem in fester Zeit.</summary>
        Public Shared Function UseRecoveryCode(userId As String, code As String) As Boolean
            If Not IsEnabled(userId) Then Return False
            Dim normalized = NormalizeRecovery(code)
            Dim given = Encoding.ASCII.GetBytes(RecoveryHash(normalized))
            Dim used As String = Nothing
            SyncLock Gate
                Dim hashes = StoredRecoveryHashes(AppSettingsService.Load(), userId)
                For Each stored In hashes
                    If CryptographicOperations.FixedTimeEquals(given, Encoding.ASCII.GetBytes(stored)) AndAlso normalized.Length > 0 Then used = stored
                Next
                If used Is Nothing Then Return False
                hashes.Remove(used)
                Dim rest = String.Join(",", hashes)
                AppSettingsService.Update(Sub(s) s.TwoFactorRecoveryHashes(userId) = rest)
                AppSettingsService.FlushNow()
            End SyncLock
            Return True
        End Function

        ''' <summary>Zehn neue Codes aus je 60 Bit, als xxxx-xxxx-xxxx.</summary>
        Private Shared Function NewRecoveryCodes() As List(Of String)
            Dim codes As New List(Of String)()
            While codes.Count < RecoveryCodeCount
                Dim text As New StringBuilder()
                For i = 0 To 11
                    If i > 0 AndAlso i Mod 4 = 0 Then text.Append("-"c)
                    text.Append(RecoveryAlphabet(RandomNumberGenerator.GetInt32(RecoveryAlphabet.Length)))
                Next
                Dim code = text.ToString()
                If Not codes.Contains(code) Then codes.Add(code)
            End While
            Return codes
        End Function

        Private Shared Function NormalizeRecovery(code As String) As String
            Return New String(If(code, "").ToLowerInvariant().Where(Function(c) RecoveryAlphabet.Contains(c)).ToArray())
        End Function

        Private Shared Function RecoveryHash(code As String) As String
            Return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(NormalizeRecovery(code)))).ToLowerInvariant()
        End Function

        ''' <summary>Die Wiederherstellungscodes als Textdatei: Titel, Hinweis, Konto, Datum und je
        ''' Code eine Zeile. Zeilenenden wie unter Windows, damit jeder Editor sie zeigt.</summary>
        Public Shared Function RecoveryCodesText(title As String, hint As String, accountName As String, codes As IEnumerable(Of String)) As String
            Dim text As New StringBuilder()
            text.Append(title).Append(vbCrLf)
            text.Append(hint).Append(vbCrLf).Append(vbCrLf)
            text.Append(Issuer).Append(": ").Append(accountName).Append(vbCrLf)
            text.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(vbCrLf).Append(vbCrLf)
            For Each code In codes
                text.Append(code).Append(vbCrLf)
            Next
            Return text.ToString()
        End Function

        ' --- QR-Code --------------------------------------------------------------------------

        ''' <summary>Die Module eines QR-Codes (Fehlerkorrektur M, Segmente und Version nach Inhalt)
        ''' samt Ruhezone von vier Modulen ringsum, Zeile zuerst. Erzeugt mit QRCoder, das die
        ''' Anwendung schon fuer QR-Objekte im Editor mitbringt; gezeichnet wird selbst.</summary>
        Public Shared Function QrModules(text As String) As Boolean(,)
            Using generator As New QRCodeGenerator()
                Using data = generator.CreateQrCode(If(text, ""), QRCodeGenerator.ECCLevel.M)
                    Dim rows = data.ModuleMatrix
                    Dim size = rows.Count
                    Dim result(size - 1, size - 1) As Boolean
                    For y = 0 To size - 1
                        For x = 0 To Math.Min(size, rows(y).Count) - 1
                            result(y, x) = rows(y)(x)
                        Next
                    Next
                    Return result
                End Using
            End Using
        End Function

        ''' <summary>Der QR-Code als SVG, schwarz auf weiss, ein Pfad fuer alle dunklen Module.</summary>
        Public Shared Function QrSvg(text As String) As String
            Dim modules = QrModules(text)
            Dim size = modules.GetLength(0)
            Dim path As New StringBuilder()
            For y = 0 To size - 1
                For x = 0 To size - 1
                    If modules(y, x) Then path.Append("M"c).Append(x).Append(" "c).Append(y).Append("h1v1h-1z")
                Next
            Next
            Dim edge = size.ToString(CultureInfo.InvariantCulture)
            Return "<svg xmlns=""http://www.w3.org/2000/svg"" viewBox=""0 0 " & edge & " " & edge & """ shape-rendering=""crispEdges"">" &
                   "<rect width=""" & edge & """ height=""" & edge & """ fill=""#ffffff""/>" &
                   "<path fill=""#000000"" d=""" & path.ToString() & """/></svg>"
        End Function

        ''' <summary>Der QR-Code als PNG, <paramref name="moduleSize"/> Pixel je Modul.</summary>
        Public Shared Function QrPng(text As String, moduleSize As Integer) As Byte()
            Dim modules = QrModules(text)
            Dim size = modules.GetLength(0)
            Dim scale = Math.Max(1, moduleSize)
            Using bitmap As New SKBitmap(size * scale, size * scale, SKColorType.Rgba8888, SKAlphaType.Opaque)
                Using canvas As New SKCanvas(bitmap)
                    canvas.Clear(SKColors.White)
                    Using paint As New SKPaint With {.Color = SKColors.Black, .Style = SKPaintStyle.Fill, .IsAntialias = False}
                        For y = 0 To size - 1
                            For x = 0 To size - 1
                                If modules(y, x) Then canvas.DrawRect(x * scale, y * scale, scale, scale, paint)
                            Next
                        Next
                    End Using
                End Using
                Return PngEncoder.EncodeToBytes(bitmap, PngPurpose.Stored)
            End Using
        End Function

        ' --- Protokoll --------------------------------------------------------------------------

        ' Die Ereignisse im Protokoll der Sicherheit (SecurityLog), nie mit Code, Geheimnis oder
        ' Wiederherstellungscode. Gescheiterte Codes nennen den Benutzer: das Kennwort stimmte da
        ' schon, der Name ist also kein Raten mehr.

        ''' <summary>Eingerichtet. <paramref name="via"/>: mobile (Seite im Browser) oder settings.</summary>
        Friend Shared Sub NoteEnabled(userId As String, from As String, via As String)
            SecurityLog.Write("twofactor.enabled", SecurityLog.UserNameOf(userId), from, "via", via)
        End Sub

        ''' <summary>Vom Benutzer selbst abgeschaltet, mit Kennwort und Code.</summary>
        Friend Shared Sub NoteDisabled(userId As String, from As String, via As String)
            SecurityLog.Write("twofactor.disabled", SecurityLog.UserNameOf(userId), from, "via", via)
        End Sub

        ''' <summary>Zurueckgesetzt: vom Administrator in der Verwaltung (admin) oder vom Notweg beim
        ''' Start (start).</summary>
        Friend Shared Sub NoteReset(userId As String, via As String)
            If via = "start" Then
                SecurityLog.Write("twofactor.reset", SecurityLog.UserNameOf(userId), "start", "target", SecurityLog.UserNameOf(userId), "via", via)
                Return
            End If
            Dim actor = SecurityLog.DesktopActor()
            SecurityLog.Write("twofactor.reset", actor.User, actor.From, "target", SecurityLog.UserNameOf(userId), "via", via)
        End Sub

        ''' <summary>Ein falscher Code (oder Wiederherstellungscode) bei der Anmeldung oder beim
        ''' Abschalten. <paramref name="at"/>: signin, disable.</summary>
        Friend Shared Sub NoteFailed(userId As String, from As String, at As String)
            SecurityLog.Write("twofactor.failed", SecurityLog.UserNameOf(userId), from, "at", at)
        End Sub

        ''' <summary>Ein Wiederherstellungscode ist eingeloest, mit der Zahl der uebrigen.</summary>
        Friend Shared Sub NoteRecoveryUsed(userId As String, from As String)
            SecurityLog.Write("twofactor.recovery.used", SecurityLog.UserNameOf(userId), from,
                              "left", RecoveryCodesLeft(userId).ToString(CultureInfo.InvariantCulture))
        End Sub

        ''' <summary>Vergisst begonnene Einrichtungen. Fuer den Pruefstand.</summary>
        Friend Shared Sub ResetForTests()
            SyncLock Gate
                Pending.Clear()
            End SyncLock
        End Sub

    End Class

End Namespace
