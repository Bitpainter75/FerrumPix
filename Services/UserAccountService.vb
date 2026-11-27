Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading

Namespace Services

    ''' <summary>Was ein Benutzer in einem Ordner darf, aufsteigend: jede Stufe schliesst die
    ''' darunter ein. Der Zahlenwert ist die Reihenfolge, gespeichert wird der Name.</summary>
    Public Enum FolderAccessLevel
        ''' <summary>Kein Zugriff: der Ordner und alles darunter fehlen in jeder Antwort.</summary>
        None = 0
        ''' <summary>Sehen und herunterladen.</summary>
        View = 1
        ''' <summary>Dazu bewerten, markieren und hochladen.</summary>
        Contribute = 2
        ''' <summary>Dazu umbenennen, verschieben und loeschen.</summary>
        Manage = 3
    End Enum

    ''' <summary>Ein weiterer Benutzer des Browsers, wie er in den Einstellungen steht. Der Hash
    ''' seines Kennworts steht getrennt (AppSettings.BrowserUserPasswordHashes).</summary>
    Public Class BrowserUserSettings
        ''' <summary>Feste Kennung, 16 Zeichen Hex; bleibt bei jeder Umbenennung gleich.</summary>
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property Email As String = ""
        ''' <summary>Ein Administrator darf wie der Eigentuemer in die volle Oberflaeche und sieht
        ''' alles.</summary>
        Public Property IsAdmin As Boolean
        ''' <summary>Gesperrt: keine Anmeldung, bestehende Sitzungen sind beendet.</summary>
        Public Property IsDisabled As Boolean
        ''' <summary>Wann angelegt, ISO 8601 in UTC.</summary>
        Public Property CreatedUtc As String = ""
        ''' <summary>Die Sprache im Browser, ein Schluessel aus LocalizationService.Languages; leer
        ''' heisst automatisch (Accept-Language des Browsers).</summary>
        Public Property Language As String = ""
    End Class

    ''' <summary>Eine Regel: was ein Benutzer im Ordner und darunter darf.</summary>
    Public Class BrowserFolderRuleSettings
        Public Property UserId As String = ""
        Public Property Folder As String = ""
        ''' <summary>Name einer <see cref="FolderAccessLevel"/>.</summary>
        Public Property Level As String = NameOf(FolderAccessLevel.View)
    End Class

    ''' <summary>Ein Benutzer, wie ihn der Rest der Anwendung sieht: der Eigentuemer oder ein
    ''' weiterer. Ein Abzug, aendern geht nur ueber <see cref="UserAccountService"/>.</summary>
    Public NotInheritable Class UserAccount
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property Email As String = ""
        Public Property IsAdmin As Boolean
        Public Property IsDisabled As Boolean
        ''' <summary>Der Eigentuemer aus der Ersteinrichtung: immer Administrator, nie gesperrt
        ''' oder geloescht.</summary>
        Public Property IsOwner As Boolean
        Public Property CreatedUtc As String = ""
        ''' <summary>Sprache im Browser, leer heisst automatisch.</summary>
        Public Property Language As String = ""
    End Class

    ''' <summary>Wie eine Aenderung an den Benutzern ausgegangen ist.</summary>
    Public Enum UserChangeOutcome
        Done
        InvalidName
        NameTaken
        InvalidEmail
        EmailTaken
        WeakPassword
        NotFound
        ''' <summary>Den Eigentuemer kann man weder sperren noch loeschen noch herabstufen.</summary>
        OwnerProtected
    End Enum

    ''' <summary>
    ''' Die Benutzer des Browsers und ihre Rechte je Ordner.
    '''
    ''' <para>DER EIGENTUEMER bleibt, wo er war: Name, Hash und E-Mail in AppLockUserName,
    ''' AppLockPasswordHash und AppLockUserEmail, Kennung <see cref="AppLockService.OwnerUserId"/>.
    ''' Eine bestehende Installation wird so ohne jeden Umbau zu einer mit einem Administrator. Er
    ''' ist immer Administrator, laesst sich nicht sperren, loeschen oder herabstufen; das
    ''' Anmeldefenster am Rechner und die Sperre der Anwendung gehoeren weiter ihm.</para>
    '''
    ''' <para>WEITERE BENUTZER stehen in den Einstellungen dieses Rechners
    ''' (AppSettings.BrowserUsers), ihre Hashes getrennt (BrowserUserPasswordHashes), die Regeln
    ''' flach je Benutzer und Ordner (BrowserFolderRules). Bewusst nicht im Katalog: der Katalog
    ''' laesst sich als fremder Katalog oeffnen und wandert mit dem Datenordner, die Anmeldung
    ''' gehoert aber zu der Anwendung, die lauscht, wie das Kennwort des Eigentuemers. In einer
    ''' Sicherung stehen Benutzer und Regeln bei den Einstellungen, die Hashes nur mit den
    ''' Zugangsdaten (CatalogBackupService).</para>
    '''
    ''' <para>RECHTE IN DREI STUFEN je Ordner, vererbt auf Unterordner: die naechstliegende Regel
    ''' gewinnt, auch eine engere auf einem Unterordner und auch "kein Zugriff"
    ''' (<see cref="LevelFor"/>). Ohne Regel gibt es keinen Zugriff. Administratoren brauchen
    ''' keine Regeln, sie sehen alles.</para>
    '''
    ''' <para>Bewertungen, Etiketten und Markierungen sind fuer alle gemeinsam, wie im Katalog und
    ''' in der Beistelldatei: wer mitarbeiten darf, aendert sie fuer alle.</para>
    ''' </summary>
    Public NotInheritable Class UserAccountService

        Private Sub New()
        End Sub

        Private Shared ReadOnly ChangeGate As New Object()
        Private Shared _version As Integer

        ''' <summary>Zaehlt jede Aenderung an Benutzern, Kennwoertern und Regeln. Zwischenspeicher,
        ''' die je Sicht gelten (die Zeitleiste der mobilen Seite, die kurz gemerkte Anmeldung eines
        ''' Werkzeugs), nehmen ihn in ihren Schluessel.</summary>
        Public Shared ReadOnly Property Version As Integer
            Get
                Return Volatile.Read(_version)
            End Get
        End Property

        ''' <summary>Benutzer oder Regeln haben sich geaendert. Kann von jedem Faden kommen.</summary>
        Public Shared Event Changed As EventHandler

        Private Shared Sub NoteChange()
            Interlocked.Increment(_version)
            RaiseEvent Changed(Nothing, EventArgs.Empty)
        End Sub

        ' --- Lesen -------------------------------------------------------------------------------

        Private Shared Function OwnerAccount(settings As AppSettings) As UserAccount
            Return New UserAccount With {
                .Id = AppLockService.OwnerUserId, .Name = AppLockService.StoredUserName,
                .Email = If(settings.AppLockUserEmail, "").Trim(), .IsAdmin = True, .IsOwner = True,
                .Language = If(settings.AppLockUserLanguage, "")}
        End Function

        Private Shared Function ToAccount(user As BrowserUserSettings) As UserAccount
            Return New UserAccount With {
                .Id = user.Id, .Name = If(user.Name, ""), .Email = If(user.Email, ""), .IsAdmin = user.IsAdmin,
                .IsDisabled = user.IsDisabled, .CreatedUtc = If(user.CreatedUtc, ""), .Language = If(user.Language, "")}
        End Function

        ''' <summary>Die Kultur eines Benutzers im Browser; Nothing heisst automatisch (dann gilt der
        ''' Browser bzw. die Sprache der Anwendung).</summary>
        Public Shared Function CultureOf(userId As String) As CultureInfo
            Dim account = Find(userId)
            If account Is Nothing Then Return Nothing
            Return LocalizationService.CultureForLanguage(account.Language)
        End Function

        ''' <summary>Setzt die Sprache eines Benutzers im Browser; "System" oder leer heisst
        ''' automatisch. Der Benutzer selbst (mobile Seite) oder der Administrator.</summary>
        Public Shared Function SetLanguage(userId As String, language As String) As UserChangeOutcome
            Dim normalized = LocalizationService.NormalizeLanguageMode(language)
            Dim stored = If(normalized = "System", "", normalized)
            SyncLock ChangeGate
                If userId = AppLockService.OwnerUserId Then
                    AppSettingsService.Update(Sub(s) s.AppLockUserLanguage = stored)
                    AppSettingsService.FlushNow()
                ElseIf Not ReplaceUser(userId, Sub(u) u.Language = stored) Then
                    Return UserChangeOutcome.NotFound
                End If
            End SyncLock
            NoteChange()
            Return UserChangeOutcome.Done
        End Function

        ''' <summary>Alle Benutzer, der Eigentuemer zuerst, dann die weiteren nach Namen.</summary>
        Public Shared Function All() As List(Of UserAccount)
            Dim settings = AppSettingsService.Load()
            Dim result As New List(Of UserAccount) From {OwnerAccount(settings)}
            result.AddRange(If(settings.BrowserUsers, New List(Of BrowserUserSettings)()).
                                Where(Function(u) u IsNot Nothing AndAlso Not String.IsNullOrEmpty(u.Id)).
                                OrderBy(Function(u) u.Name, StringComparer.CurrentCultureIgnoreCase).
                                Select(AddressOf ToAccount))
            Return result
        End Function

        ''' <summary>Ein Benutzer nach Kennung, sonst Nothing.</summary>
        Public Shared Function Find(userId As String) As UserAccount
            If String.IsNullOrEmpty(userId) Then Return Nothing
            Return All().FirstOrDefault(Function(u) String.Equals(u.Id, userId, StringComparison.Ordinal))
        End Function

        ''' <summary>Gibt es den Benutzer, und darf er sich anmelden? Der Eigentuemer immer.</summary>
        Public Shared Function IsActive(userId As String) As Boolean
            Dim account = Find(userId)
            Return account IsNot Nothing AndAlso Not account.IsDisabled
        End Function

        ''' <summary>Darf der Benutzer in die volle Oberflaeche, in die Einstellungen und an die
        ''' Benutzerverwaltung, und sieht er alles? Der Eigentuemer immer, ein weiterer nur als
        ''' Administrator und nicht gesperrt.</summary>
        Public Shared Function IsAdmin(userId As String) As Boolean
            Dim account = Find(userId)
            Return account IsNot Nothing AndAlso account.IsAdmin AndAlso Not account.IsDisabled
        End Function

        ''' <summary>Der Hash eines weiteren Benutzers, leer ohne. Der des Eigentuemers kommt aus
        ''' AppLockService.</summary>
        Private Shared Function StoredHash(settings As AppSettings, userId As String) As String
            Dim hash As String = Nothing
            If settings.BrowserUserPasswordHashes IsNot Nothing AndAlso
               settings.BrowserUserPasswordHashes.TryGetValue(userId, hash) Then Return If(hash, "")
            Return ""
        End Function

        ''' <summary>Fuer die Anmeldung: Kennung und Hash des Benutzers mit diesem Namen, sonst
        ''' Nothing. Gross- und Kleinschreibung zaehlen nicht. Verglichen wird jeder Name in fester
        ''' Zeit, auch nach einem Treffer, damit die Laufzeit nicht verraet, ob und wo es den Namen
        ''' gibt. Ein gesperrter Benutzer oder einer ohne Kennwort hat keinen Treffer.
        '''
        ''' <para>Hat der Eigentuemer noch keinen Namen (Kennwort aus den Einstellungen am Rechner),
        ''' gehoert ihm jeder gueltige Name, den kein weiterer Benutzer traegt; die Anmeldung legt
        ''' ihn dann fest (AppLockService.AdoptUserName).</para></summary>
        Friend Shared Function ResolveSignIn(userName As String, ownerHash As String) As (Id As String, Hash As String)?
            Dim settings = AppSettingsService.Load()
            Dim given = Encoding.UTF8.GetBytes(If(userName, "").Trim().ToUpperInvariant())
            Dim found As (Id As String, Hash As String)? = Nothing
            Dim ownerName = AppLockService.StoredUserName
            If CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(ownerName.ToUpperInvariant())) AndAlso ownerName.Length > 0 Then
                found = (AppLockService.OwnerUserId, ownerHash)
            End If
            For Each user In If(settings.BrowserUsers, New List(Of BrowserUserSettings)())
                If user Is Nothing Then Continue For
                Dim same = CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(If(user.Name, "").Trim().ToUpperInvariant()))
                If same AndAlso Not found.HasValue AndAlso Not user.IsDisabled Then
                    Dim hash = StoredHash(settings, user.Id)
                    If hash.Length > 0 Then found = (user.Id, hash)
                End If
            Next
            If Not found.HasValue AndAlso ownerName.Length = 0 AndAlso AppLockService.IsValidUserName(userName) AndAlso
               Not NameTaken(userName, Nothing) Then
                found = (AppLockService.OwnerUserId, ownerHash)
            End If
            Return found
        End Function

        ''' <summary>Der Benutzer zu einem Namen oder einer E-Mail-Adresse (ohne Gross- und
        ''' Kleinschreibung), wenn er sich anmelden darf; sonst Nothing. Fuer "Kennwort vergessen".</summary>
        Friend Shared Function FindForRecovery(nameOrEmail As String) As UserAccount
            Dim given = If(nameOrEmail, "").Trim()
            If given.Length = 0 Then Return Nothing
            For Each account In All()
                If account.IsDisabled Then Continue For
                If String.Equals(account.Name, given, StringComparison.OrdinalIgnoreCase) OrElse
                   (account.Email.Length > 0 AndAlso String.Equals(account.Email, given, StringComparison.OrdinalIgnoreCase)) Then Return account
            Next
            Return Nothing
        End Function

        ''' <summary>Traegt schon ein anderer Benutzer diesen Namen (auch der Eigentuemer)?</summary>
        Public Shared Function NameTaken(name As String, exceptId As String) As Boolean
            Dim wanted = If(name, "").Trim()
            Return All().Any(Function(u) Not String.Equals(u.Id, exceptId, StringComparison.Ordinal) AndAlso
                                         String.Equals(u.Name.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Traegt schon ein anderer Benutzer diese E-Mail-Adresse?</summary>
        Public Shared Function EmailTaken(email As String, exceptId As String) As Boolean
            Dim wanted = If(email, "").Trim()
            If wanted.Length = 0 Then Return False
            Return All().Any(Function(u) Not String.Equals(u.Id, exceptId, StringComparison.Ordinal) AndAlso
                                         String.Equals(u.Email, wanted, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Eine E-Mail-Adresse in der einfachen Form name@domain.tld: ohne Anzeigenamen,
        ''' ohne Leerzeichen und Steuerzeichen, hoechstens 254 Zeichen, mit Punkt im Teil nach dem
        ''' @. Leer ist erlaubt (dann gibt es keine).</summary>
        Public Shared Function IsValidEmail(email As String) As Boolean
            Dim text = If(email, "").Trim()
            If text.Length = 0 Then Return True
            If text.Length > 254 OrElse text.Any(Function(c) Char.IsWhiteSpace(c) OrElse Char.IsControl(c)) Then Return False
            Dim parsed As Net.Mail.MailAddress = Nothing
            If Not Net.Mail.MailAddress.TryCreate(text, parsed) Then Return False
            If Not String.Equals(parsed.Address, text, StringComparison.Ordinal) Then Return False
            Dim at = text.LastIndexOf("@"c)
            Dim domain = text.Substring(at + 1)
            Return at > 0 AndAlso domain.Contains("."c) AndAlso Not domain.StartsWith("."c) AndAlso Not domain.EndsWith("."c)
        End Function

        ' --- Rechte je Ordner --------------------------------------------------------------------

        ''' <summary>Die Stufe aus ihrem gespeicherten Namen; Unbekanntes ist "kein Zugriff".</summary>
        Public Shared Function ParseLevel(text As String) As FolderAccessLevel
            Dim level As FolderAccessLevel
            If [Enum].TryParse(If(text, ""), ignoreCase:=True, result:=level) AndAlso [Enum].IsDefined(level) Then Return level
            Return FolderAccessLevel.None
        End Function

        ''' <summary>Die Regeln eines Benutzers, als Ordner und Stufe.</summary>
        Public Shared Function RulesFor(userId As String) As List(Of (Folder As String, Level As FolderAccessLevel))
            Dim settings = AppSettingsService.Load()
            Return If(settings.BrowserFolderRules, New List(Of BrowserFolderRuleSettings)()).
                Where(Function(r) r IsNot Nothing AndAlso String.Equals(r.UserId, userId, StringComparison.Ordinal) AndAlso
                                  Not String.IsNullOrWhiteSpace(r.Folder)).
                Select(Function(r) (r.Folder, ParseLevel(r.Level))).
                OrderBy(Function(r) r.Folder, StringComparer.CurrentCultureIgnoreCase).ToList()
        End Function

        ''' <summary>Was die Regeln fuer einen Pfad ergeben: die Stufe der naechstliegenden Regel
        ''' auf dem Pfad selbst oder einem seiner Ordner darueber, ohne Regel
        ''' <see cref="FolderAccessLevel.None"/>. Reine Zeichenkettenarbeit, sie laeuft ueber jede
        ''' Zeile des Katalogs.</summary>
        Public Shared Function LevelFor(rules As IReadOnlyList(Of (Folder As String, Level As FolderAccessLevel)), path As String) As FolderAccessLevel
            If rules Is Nothing OrElse String.IsNullOrEmpty(path) Then Return FolderAccessLevel.None
            Dim best = FolderAccessLevel.None
            Dim bestLength = -1
            For Each rule In rules
                If rule.Folder.Length <= bestLength Then Continue For
                If PathIdentity.IsAncestorOrSelf(rule.Folder, path) Then
                    best = rule.Level
                    bestLength = rule.Folder.Length
                End If
            Next
            Return best
        End Function

        ''' <summary>Die Startordner, die die Regeln ergeben: jeder Ordner einer Regel mit mindestens
        ''' "Ansehen", ausser denen, die man von einem anderen Startordner aus blaetternd erreicht
        ''' (keine Regel unter "Ansehen" dazwischen). <paramref name="usable"/> filtert, was es
        ''' gibt und sichtbar ist (die mobile Seite fragt die Platte, die Verwaltung nicht).</summary>
        Public Shared Function StartFolders(rules As IReadOnlyList(Of (Folder As String, Level As FolderAccessLevel)),
                                            Optional usable As Func(Of String, Boolean) = Nothing) As List(Of String)
            Dim own = rules.Where(Function(r) r.Level >= FolderAccessLevel.View AndAlso (usable Is Nothing OrElse usable(r.Folder))).
                            Select(Function(r) r.Folder).ToList()
            Return own.Where(Function(lower) Not own.Any(Function(upper) Not PathIdentity.AreSame(upper, lower) AndAlso
                                                                         PathIdentity.IsAncestorOrSelf(upper, lower) AndAlso
                                                                         IsReachable(rules, upper, lower))).ToList()
        End Function

        ''' <summary>Kommt man von <paramref name="upper"/> aus blaetternd bis <paramref name="lower"/>?
        ''' Ja, wenn keine Regel unter "Ansehen" zwischen beiden liegt.</summary>
        Private Shared Function IsReachable(rules As IReadOnlyList(Of (Folder As String, Level As FolderAccessLevel)),
                                            upper As String, lower As String) As Boolean
            Dim parent = IO.Path.GetDirectoryName(lower)
            If String.IsNullOrEmpty(parent) Then Return False
            Return Not rules.Any(Function(r) r.Level < FolderAccessLevel.View AndAlso
                                             Not PathIdentity.AreSame(r.Folder, upper) AndAlso
                                             PathIdentity.IsAncestorOrSelf(upper, r.Folder) AndAlso
                                             PathIdentity.IsAncestorOrSelf(r.Folder, parent))
        End Function

        ' --- Aendern ----------------------------------------------------------------------------

        ''' <summary>Legt einen Benutzer an. Langsam (der Hash), deshalb nie auf dem Anzeigefaden.</summary>
        Public Shared Function CreateUser(name As String, email As String, password As String, isAdmin As Boolean,
                                          ByRef createdId As String) As UserChangeOutcome
            createdId = Nothing
            Dim cleanName = If(name, "").Trim()
            Dim cleanEmail = If(email, "").Trim()
            If Not AppLockService.IsValidUserName(cleanName) Then Return UserChangeOutcome.InvalidName
            If Not IsValidEmail(cleanEmail) Then Return UserChangeOutcome.InvalidEmail
            If AppLockService.PasswordProblems(password).Count > 0 Then Return UserChangeOutcome.WeakPassword
            Dim hash = AppLockService.HashPassword(password)
            Dim id = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()
            SyncLock ChangeGate
                If NameTaken(cleanName, Nothing) Then Return UserChangeOutcome.NameTaken
                If EmailTaken(cleanEmail, Nothing) Then Return UserChangeOutcome.EmailTaken
                AppSettingsService.Update(
                    Sub(s)
                        s.BrowserUsers.Add(New BrowserUserSettings With {
                            .Id = id, .Name = cleanName, .Email = cleanEmail, .IsAdmin = isAdmin,
                            .CreatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)})
                        s.BrowserUserPasswordHashes(id) = hash
                    End Sub)
                AppSettingsService.FlushNow()
            End SyncLock
            createdId = id
            SecurityLog.UserEvent("user.created", cleanName, "admin", If(isAdmin, "yes", "no"))
            NoteChange()
            Return UserChangeOutcome.Done
        End Function

        ''' <summary>Ersetzt den Eintrag eines weiteren Benutzers durch eine geaenderte Kopie. Die
        ''' Elemente der Liste werden beim Kopieren der Einstellungen nur flach kopiert; geaendert
        ''' wird deshalb nie das gemerkte Objekt selbst.</summary>
        Private Shared Function ReplaceUser(userId As String, change As Action(Of BrowserUserSettings)) As Boolean
            Dim found = False
            AppSettingsService.Update(
                Sub(s)
                    For i = 0 To s.BrowserUsers.Count - 1
                        Dim user = s.BrowserUsers(i)
                        If user Is Nothing OrElse Not String.Equals(user.Id, userId, StringComparison.Ordinal) Then Continue For
                        Dim copy As New BrowserUserSettings With {
                            .Id = user.Id, .Name = user.Name, .Email = user.Email, .IsAdmin = user.IsAdmin,
                            .IsDisabled = user.IsDisabled, .CreatedUtc = user.CreatedUtc, .Language = user.Language}
                        change(copy)
                        s.BrowserUsers(i) = copy
                        found = True
                    Next
                End Sub)
            AppSettingsService.FlushNow()
            Return found
        End Function

        ''' <summary>Setzt ein neues Kennwort fuer einen weiteren Benutzer (Verwaltung) oder fuer
        ''' jeden (Kennwort vergessen). Beendet alle Sitzungen des Benutzers ausser
        ''' <paramref name="keepSessionKey"/> und jeden offenen Verweis zum Zuruecksetzen. Langsam.</summary>
        Public Shared Function SetPassword(userId As String, password As String, Optional keepSessionKey As String = Nothing) As UserChangeOutcome
            If AppLockService.PasswordProblems(password).Count > 0 Then Return UserChangeOutcome.WeakPassword
            Dim account = Find(userId)
            If account Is Nothing Then Return UserChangeOutcome.NotFound
            Dim hash = AppLockService.HashPassword(password)
            SyncLock ChangeGate
                AppSettingsService.Update(
                    Sub(s)
                        If account.IsOwner Then
                            s.AppLockPasswordHash = hash
                        Else
                            s.BrowserUserPasswordHashes(userId) = hash
                        End If
                    End Sub)
                AppSettingsService.FlushNow()
            End SyncLock
            DiagnosticLogService.LogAlways("Users", $"Password of {userId} changed.")
            NoteChange()
            BrowserBridge.EndSessionsOfUser(userId, keepSessionKey)
            PasswordResetService.Forget(userId)
            Return UserChangeOutcome.Done
        End Function

        ''' <summary>Sperrt oder entsperrt einen weiteren Benutzer; Sperren beendet seine Sitzungen.</summary>
        Public Shared Function SetDisabled(userId As String, disabled As Boolean) As UserChangeOutcome
            If userId = AppLockService.OwnerUserId Then Return UserChangeOutcome.OwnerProtected
            Dim found As Boolean
            SyncLock ChangeGate
                found = ReplaceUser(userId, Sub(u) u.IsDisabled = disabled)
            End SyncLock
            If Not found Then Return UserChangeOutcome.NotFound
            SecurityLog.UserEvent(If(disabled, "user.disabled", "user.enabled"), SecurityLog.UserNameOf(userId))
            NoteChange()
            If disabled Then
                BrowserBridge.EndSessionsOfUser(userId, Nothing)
                PasswordResetService.Forget(userId)
            End If
            Return UserChangeOutcome.Done
        End Function

        ''' <summary>Macht einen weiteren Benutzer zum Administrator oder nimmt es zurueck. Ein
        ''' herabgestufter verliert seine Sitzungen, damit keine mehr die volle Oberflaeche traegt.</summary>
        Public Shared Function SetAdmin(userId As String, isAdmin As Boolean) As UserChangeOutcome
            If userId = AppLockService.OwnerUserId Then Return UserChangeOutcome.OwnerProtected
            Dim found As Boolean
            SyncLock ChangeGate
                found = ReplaceUser(userId, Sub(u) u.IsAdmin = isAdmin)
            End SyncLock
            If Not found Then Return UserChangeOutcome.NotFound
            SecurityLog.UserEvent("user.admin", SecurityLog.UserNameOf(userId), "admin", If(isAdmin, "yes", "no"))
            NoteChange()
            If Not isAdmin Then BrowserBridge.EndSessionsOfUser(userId, Nothing)
            Return UserChangeOutcome.Done
        End Function

        ''' <summary>Benennt einen Benutzer um, auch den Eigentuemer. Regeln wie beim Anlegen und bei
        ''' der Einrichtung: 1 bis 64 Zeichen ohne Steuerzeichen, eindeutig ohne Gross- und
        ''' Kleinschreibung gegen alle Benutzer samt Eigentuemer; nur die Schreibweise des eigenen
        ''' Namens zu aendern geht.
        '''
        ''' <para>ALLES ANDERE HAENGT AN DER KENNUNG und bleibt: Sitzungen (der Benutzer bleibt
        ''' angemeldet), Regeln, Freigaben, zweiter Faktor, Sprache, Zaehler der Bremse und offene
        ''' Verweise fuer ein vergessenes Kennwort. Die Anmeldung gilt ab sofort nur mit dem neuen
        ''' Namen; der alte antwortet wie ein unbekannter. Der eine Minute gemerkte Kopf eines
        ''' Werkzeugs (Basic) verfaellt mit <see cref="Version"/>.</para>
        '''
        ''' <para><paramref name="actorUser"/> und <paramref name="actorFrom"/> fuer das Protokoll,
        ''' wenn nicht die volle Oberflaeche handelt (die mobile Seite fuer das eigene Konto).</para></summary>
        Public Shared Function RenameUser(userId As String, newName As String,
                                          Optional actorUser As String = Nothing, Optional actorFrom As String = Nothing) As UserChangeOutcome
            Dim clean = If(newName, "").Trim()
            If Not AppLockService.IsValidUserName(clean) Then Return UserChangeOutcome.InvalidName
            Dim oldName As String
            SyncLock ChangeGate
                Dim account = Find(userId)
                If account Is Nothing Then Return UserChangeOutcome.NotFound
                oldName = account.Name
                If String.Equals(oldName, clean, StringComparison.Ordinal) Then Return UserChangeOutcome.Done
                If NameTaken(clean, userId) Then Return UserChangeOutcome.NameTaken
                If account.IsOwner Then
                    If Not String.IsNullOrEmpty(AppLockService.TestPasswordHash) Then
                        ' Gilt gerade ein Kennwort nur fuer den Pruefstand, gilt auch sein Name nur
                        ' fuer diesen Lauf (wie bei AdoptUserName).
                        AppLockService.TestUserName = clean
                    Else
                        AppSettingsService.Update(Sub(s) s.AppLockUserName = clean)
                        AppSettingsService.FlushNow()
                    End If
                ElseIf Not ReplaceUser(userId, Sub(u) u.Name = clean) Then
                    Return UserChangeOutcome.NotFound
                End If
            End SyncLock
            SecurityLog.UserRenamed(oldName, clean, actorUser, actorFrom)
            NoteChange()
            Return UserChangeOutcome.Done
        End Function

        ''' <summary>Setzt die E-Mail-Adresse, auch die des Eigentuemers. Leer loescht sie.</summary>
        Public Shared Function SetEmail(userId As String, email As String) As UserChangeOutcome
            Dim clean = If(email, "").Trim()
            If Not IsValidEmail(clean) Then Return UserChangeOutcome.InvalidEmail
            SyncLock ChangeGate
                If EmailTaken(clean, userId) Then Return UserChangeOutcome.EmailTaken
                If userId = AppLockService.OwnerUserId Then
                    AppSettingsService.Update(Sub(s) s.AppLockUserEmail = clean)
                    AppSettingsService.FlushNow()
                ElseIf Not ReplaceUser(userId, Sub(u) u.Email = clean) Then
                    Return UserChangeOutcome.NotFound
                End If
            End SyncLock
            ' Ohne die Adresse selbst: nur, dass sie sich geaendert hat.
            SecurityLog.UserEvent("user.changed", SecurityLog.UserNameOf(userId), "field", "email")
            NoteChange()
            Return UserChangeOutcome.Done
        End Function

        ''' <summary>Loescht einen weiteren Benutzer samt Kennwort und Regeln und beendet seine
        ''' Sitzungen. Den Eigentuemer nie.</summary>
        Public Shared Function Delete(userId As String) As UserChangeOutcome
            If userId = AppLockService.OwnerUserId Then Return UserChangeOutcome.OwnerProtected
            Dim found = False
            Dim name = SecurityLog.UserNameOf(userId)
            SyncLock ChangeGate
                AppSettingsService.Update(
                    Sub(s)
                        found = s.BrowserUsers.RemoveAll(Function(u) u IsNot Nothing AndAlso String.Equals(u.Id, userId, StringComparison.Ordinal)) > 0
                        s.BrowserFolderRules.RemoveAll(Function(r) r Is Nothing OrElse String.Equals(r.UserId, userId, StringComparison.Ordinal))
                        s.BrowserUserPasswordHashes.Remove(userId)
                        ' Der zweite Faktor gehoert zum Konto und geht mit.
                        TwoFactorService.RemoveFrom(s, userId)
                    End Sub)
                AppSettingsService.FlushNow()
            End SyncLock
            If Not found Then Return UserChangeOutcome.NotFound
            SecurityLog.UserEvent("user.deleted", name)
            NoteChange()
            BrowserBridge.EndSessionsOfUser(userId, Nothing)
            PasswordResetService.Forget(userId)
            Return UserChangeOutcome.Done
        End Function

        ''' <summary>Setzt die Regel eines Benutzers fuer einen Ordner; eine fuer denselben Ordner
        ''' wird ersetzt.</summary>
        Public Shared Function SetRule(userId As String, folder As String, level As FolderAccessLevel) As UserChangeOutcome
            If String.IsNullOrWhiteSpace(folder) OrElse Not IO.Path.IsPathRooted(folder) Then Return UserChangeOutcome.NotFound
            If userId = AppLockService.OwnerUserId Then Return UserChangeOutcome.OwnerProtected
            If Find(userId) Is Nothing Then Return UserChangeOutcome.NotFound
            Dim clean = IO.Path.TrimEndingDirectorySeparator(IO.Path.GetFullPath(folder))
            SyncLock ChangeGate
                AppSettingsService.Update(
                    Sub(s)
                        s.BrowserFolderRules.RemoveAll(Function(r) r Is Nothing OrElse
                                                                    (String.Equals(r.UserId, userId, StringComparison.Ordinal) AndAlso
                                                                     PathIdentity.AreSame(r.Folder, clean)))
                        s.BrowserFolderRules.Add(New BrowserFolderRuleSettings With {.UserId = userId, .Folder = clean, .Level = level.ToString()})
                    End Sub)
                AppSettingsService.FlushNow()
            End SyncLock
            SecurityLog.RuleChanged(userId, clean, level.ToString().ToLowerInvariant())
            NoteChange()
            Return UserChangeOutcome.Done
        End Function

        ''' <summary>Entfernt die Regel eines Benutzers fuer einen Ordner.</summary>
        Public Shared Sub RemoveRule(userId As String, folder As String)
            SyncLock ChangeGate
                AppSettingsService.Update(
                    Sub(s) s.BrowserFolderRules.RemoveAll(Function(r) r Is Nothing OrElse
                                                                       (String.Equals(r.UserId, userId, StringComparison.Ordinal) AndAlso
                                                                        PathIdentity.AreSame(r.Folder, folder))))
                AppSettingsService.FlushNow()
            End SyncLock
            SecurityLog.RuleChanged(userId, folder, "removed")
            NoteChange()
        End Sub

    End Class

End Namespace
