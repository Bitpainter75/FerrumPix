Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Eine Stufe der Rechte je Ordner zur Auswahl. Der Name wird beim Lesen uebersetzt,
    ''' damit er einen Sprachwechsel mitmacht.</summary>
    Public NotInheritable Class FolderAccessOption
        Public Property Level As FolderAccessLevel

        Public ReadOnly Property Name As String
            Get
                Return FolderAccessName(Level)
            End Get
        End Property

        ''' <summary>Der Name einer Stufe, je ein ganzer Text fuer die Uebersetzung.</summary>
        Public Shared Function FolderAccessName(level As FolderAccessLevel) As String
            Select Case level
                Case FolderAccessLevel.View : Return LocalizationService.T("Ansehen")
                Case FolderAccessLevel.Contribute : Return LocalizationService.T("Mitarbeiten")
                Case FolderAccessLevel.Manage : Return LocalizationService.T("Verwalten")
                Case Else : Return LocalizationService.T("Kein Zugriff")
            End Select
        End Function

        Public Shared ReadOnly All As IReadOnlyList(Of FolderAccessOption) =
            {FolderAccessLevel.None, FolderAccessLevel.View, FolderAccessLevel.Contribute, FolderAccessLevel.Manage}.
                Select(Function(l) New FolderAccessOption With {.Level = l}).ToList()
    End Class

    ''' <summary>Eine Verschluesselung des Mailservers zur Auswahl.</summary>
    Public NotInheritable Class MailSecurityOption
        Public Property Security As MailSecurity

        Public ReadOnly Property Name As String
            Get
                Select Case Security
                    Case MailSecurity.Ssl : Return "SSL/TLS"
                    Case MailSecurity.None : Return LocalizationService.T("Ohne Verschlüsselung")
                    Case Else : Return "STARTTLS"
                End Select
            End Get
        End Property

        Public Shared ReadOnly All As IReadOnlyList(Of MailSecurityOption) =
            {MailSecurity.StartTls, MailSecurity.Ssl, MailSecurity.None}.Select(Function(s) New MailSecurityOption With {.Security = s}).ToList()
    End Class

    ''' <summary>Eine Sprache fuer einen Benutzer im Browser; "System" heisst automatisch.</summary>
    Public NotInheritable Class UserLanguageOption
        Public Property Key As String = "System"
        Public Property NativeName As String = ""

        Public ReadOnly Property Name As String
            Get
                Return If(Key = "System", LocalizationService.T("Automatisch (Sprache des Browsers)"), NativeName)
            End Get
        End Property

        Public Shared ReadOnly All As IReadOnlyList(Of UserLanguageOption) =
            LocalizationService.Languages.Select(Function(l) New UserLanguageOption With {.Key = l.Key, .NativeName = l.Name}).ToList()
    End Class

    ''' <summary>Ein Benutzer in der Liste der Verwaltung.</summary>
    Public NotInheritable Class UserAccountRow
        Public Property Account As UserAccount

        Public ReadOnly Property Name As String
            Get
                Return If(Account.Name.Length > 0, Account.Name, LocalizationService.T("Eigentümer"))
            End Get
        End Property

        Public ReadOnly Property Details As String
            Get
                Dim parts As New List(Of String)()
                If Account.IsOwner Then
                    parts.Add(LocalizationService.T("Eigentümer"))
                ElseIf Account.IsAdmin Then
                    parts.Add(LocalizationService.T("Administrator"))
                End If
                If Account.IsDisabled Then parts.Add(LocalizationService.T("Gesperrt"))
                If TwoFactorService.IsEnabled(Account.Id) Then parts.Add(LocalizationService.T("Zweiter Faktor"))
                If Account.Email.Length > 0 Then parts.Add(Account.Email)
                Return String.Join(", ", parts)
            End Get
        End Property
    End Class

    ''' <summary>Eine Regel eines Benutzers in der Verwaltung: Ordner, Stufe, Entfernen.</summary>
    Public NotInheritable Class UserFolderRuleRow
        Inherits ReactiveObject

        Private ReadOnly _changed As Action(Of UserFolderRuleRow)
        Private _selected As FolderAccessOption

        Public Sub New(folder As String, level As FolderAccessLevel, changed As Action(Of UserFolderRuleRow), remove As Action(Of UserFolderRuleRow))
            Me.Folder = folder
            _selected = FolderAccessOption.All.First(Function(o) o.Level = level)
            _changed = changed
            RemoveCommand = New DelegateCommand(Sub() remove(Me))
        End Sub

        Public ReadOnly Property Folder As String
        Public ReadOnly Property RemoveCommand As ICommand

        Public ReadOnly Property Options As IReadOnlyList(Of FolderAccessOption)
            Get
                Return FolderAccessOption.All
            End Get
        End Property

        Public Property SelectedLevel As FolderAccessOption
            Get
                Return _selected
            End Get
            Set(value As FolderAccessOption)
                If value Is Nothing OrElse value Is _selected Then Return
                Me.RaiseAndSetIfChanged(_selected, value)
                _changed(Me)
            End Set
        End Property
    End Class

    ''' <summary>
    ''' Der Abschnitt "Benutzer" der Einstellungen: weitere Benutzer fuer den Browser, ihre Rechte je
    ''' Ordner und der Mailversand fuer "Kennwort vergessen" (UserAccountService, MailService).
    '''
    ''' <para>SICHTBAR NUR FUER DEN ADMINISTRATOR: die Einstellungen gibt es nur in der vollen
    ''' Oberflaeche, und die bekommt im Browser nur ein Administrator. Am Rechner ohne Browser steht
    ''' der Abschnitt ebenfalls da, damit sich Benutzer vorbereiten lassen.</para>
    '''
    ''' <para>JEDE AENDERUNG WIRKT SOFORT, anders als "Zugang": Benutzer und Regeln stehen nicht in
    ''' "Anwenden", und "Auf Standard zuruecksetzen" nimmt sie nicht mit. Das Kennwort des
    ''' Eigentuemers aendert sich weiter nur unter "Zugang", mit dem aktuellen Kennwort.</para>
    ''' </summary>
    Partial Public Class SettingsViewModel

        Private ReadOnly _userRows As New ObservableCollection(Of UserAccountRow)()
        Private ReadOnly _userRuleRows As New ObservableCollection(Of UserFolderRuleRow)()
        Private _selectedUserRow As UserAccountRow
        Private _selectedUserEmail As String = ""
        Private _selectedUserName As String = ""
        Private _selectedUserNewPassword As String = ""
        Private _selectedUserNewPasswordRepeat As String = ""
        Private _newUserName As String = ""
        Private _newUserEmail As String = ""
        Private _newUserPassword As String = ""
        Private _newUserPasswordRepeat As String = ""
        Private _newUserIsAdmin As Boolean
        Private _usersStatusText As String = ""
        Private _usersStatusIsError As Boolean
        Private _usersBusy As Boolean
        Private _mailHost As String = ""
        Private _mailPort As String = "587"
        Private _mailSecurity As MailSecurityOption = MailSecurityOption.All(0)
        Private _mailUserName As String = ""
        Private _mailPassword As String = ""
        Private _mailSender As String = ""
        Private _publicAddress As String = ""
        Private _mailTestRecipient As String = ""
        Private _mailStatusText As String = ""
        Private _mailStatusIsError As Boolean
        Private _publicAddressStatusText As String = ""
        Private _publicAddressStatusIsError As Boolean
        Private _usersLoaded As Boolean
        Private _usersHooked As Boolean

        ''' <summary>Stand der Benutzerverwaltung (UserAccountService.Version), aus dem die Liste
        ''' zuletzt gebaut wurde. Eine Aenderung von hier baut sie selbst neu; die Meldung danach
        ''' findet dann denselben Stand und laesst die Liste stehen.</summary>
        Private _userRowsVersion As Integer = -1

        Public ReadOnly Property UserRows As ObservableCollection(Of UserAccountRow)
            Get
                EnsureUsersLoaded()
                Return _userRows
            End Get
        End Property

        Public ReadOnly Property UserRuleRows As ObservableCollection(Of UserFolderRuleRow)
            Get
                Return _userRuleRows
            End Get
        End Property

        Public ReadOnly Property MailSecurityOptions As IReadOnlyList(Of MailSecurityOption) = MailSecurityOption.All

        ''' <summary>Liest Benutzer und Mailversand beim ersten Zugriff des Abschnitts.</summary>
        Private Sub EnsureUsersLoaded()
            If _usersLoaded Then Return
            _usersLoaded = True
            ReloadUserRows(Nothing)
            HookUserAccounts()
            Dim settings = AppSettingsService.Load()
            Dim mail = MailSettings.FromAppSettings(settings)
            _mailHost = mail.Host
            _mailPort = mail.Port.ToString(CultureInfo.InvariantCulture)
            _mailSecurity = MailSecurityOption.All.First(Function(o) o.Security = mail.Security)
            _mailUserName = mail.UserName
            _mailPassword = mail.Password
            _mailSender = mail.Sender
            _publicAddress = If(settings.BrowserPublicAddress, "")
            _mailTestRecipient = If(settings.AppLockUserEmail, "")
        End Sub

        Private Sub ReloadUserRows(selectId As String)
            ' Der Stand VOR dem Lesen: eine Aenderung waehrenddessen meldet sich danach und wird
            ' nachgeholt, statt verloren zu gehen.
            _userRowsVersion = UserAccountService.Version
            _userRows.Clear()
            For Each account In UserAccountService.All()
                _userRows.Add(New UserAccountRow With {.Account = account})
            Next
            SelectedUserRow = _userRows.FirstOrDefault(Function(r) r.Account.Id = selectId)
        End Sub

        ''' <summary>Folgt jeder Aenderung an Benutzern und Regeln, auch von ausserhalb dieses
        ''' Abschnitts (mobile Seite, Kennwort vergessen, zweiter Faktor). Die Meldung kann von jedem
        ''' Faden kommen und wird auf den Anzeigefaden gereicht. Schwach gehalten wie die
        ''' Schriftbibliothek (<see cref="HookFontLibrary"/>): ist das Modell weg, meldet sich der
        ''' Empfaenger beim naechsten Mal selbst ab.</summary>
        Private Sub HookUserAccounts()
            If _usersHooked Then Return
            _usersHooked = True
            Dim weakSelf As New WeakReference(Of SettingsViewModel)(Me)
            Dim handler As EventHandler = Nothing
            handler = Sub(sender, e)
                          Dim alive As SettingsViewModel = Nothing
                          If Not weakSelf.TryGetTarget(alive) Then
                              RemoveHandler UserAccountService.Changed, handler
                              Return
                          End If
                          Try
                              Avalonia.Threading.Dispatcher.UIThread.Post(AddressOf alive.RefreshUserRowsFromService)
                          Catch ex As Exception
                              DiagnosticLogService.LogException("Settings.UserAccountsChanged", ex)
                          End Try
                      End Sub
            AddHandler UserAccountService.Changed, handler
        End Sub

        ''' <summary>Baut die Liste nach einer Aenderung von ausserhalb neu. Die Wahl bleibt; was im
        ''' gewaehlten Benutzer gerade getippt und noch nicht gespeichert ist (Name, E-Mail, neues
        ''' Kennwort), bleibt stehen, Unveraendertes folgt dem neuen Stand.</summary>
        Private Sub RefreshUserRowsFromService()
            If Not _usersLoaded OrElse UserAccountService.Version = _userRowsVersion Then Return
            Dim previous = _selectedUserRow
            Dim typedName = previous IsNot Nothing AndAlso _selectedUserName <> If(previous.Account.Name, "")
            Dim typedEmail = previous IsNot Nothing AndAlso _selectedUserEmail <> If(previous.Account.Email, "")
            Dim keepName = _selectedUserName
            Dim keepEmail = _selectedUserEmail
            Dim keepPassword = _selectedUserNewPassword
            Dim keepRepeat = _selectedUserNewPasswordRepeat
            ReloadUserRows(previous?.Account.Id)
            If previous Is Nothing OrElse _selectedUserRow Is Nothing Then Return
            If typedName Then SelectedUserName = keepName
            If typedEmail Then SelectedUserEmail = keepEmail
            SelectedUserNewPassword = keepPassword
            SelectedUserNewPasswordRepeat = keepRepeat
        End Sub

        Public Property SelectedUserRow As UserAccountRow
            Get
                Return _selectedUserRow
            End Get
            Set(value As UserAccountRow)
                _selectedUserRow = value
                Me.RaisePropertyChanged(NameOf(SelectedUserRow))
                _selectedUserEmail = If(value?.Account.Email, "")
                _selectedUserName = If(value?.Account.Name, "")
                _selectedUserNewPassword = ""
                _selectedUserNewPasswordRepeat = ""
                ReloadRuleRows()
                For Each name In {NameOf(HasSelectedUser), NameOf(SelectedUserIsOther), NameOf(SelectedUserEmail), NameOf(SelectedUserName),
                                  NameOf(SelectedUserIsAdmin), NameOf(SelectedUserIsDisabled), NameOf(SelectedUserNewPassword),
                                  NameOf(SelectedUserNewPasswordRepeat), NameOf(SelectedUserTitle), NameOf(UserRulesSummary),
                                  NameOf(SelectedUserNeedsRules), NameOf(SelectedUserLanguage),
                                  NameOf(SelectedUserHasTwoFactor), NameOf(CanResetSelectedUserTwoFactor)}
                    Me.RaisePropertyChanged(name)
                Next
            End Set
        End Property

        Public ReadOnly Property HasSelectedUser As Boolean
            Get
                Return _selectedUserRow IsNot Nothing
            End Get
        End Property

        ''' <summary>Ein weiterer Benutzer (nicht der Eigentuemer): nur ihn kann man sperren,
        ''' loeschen, herabstufen oder hier mit neuem Kennwort versehen.</summary>
        Public ReadOnly Property SelectedUserIsOther As Boolean
            Get
                Return _selectedUserRow IsNot Nothing AndAlso Not _selectedUserRow.Account.IsOwner
            End Get
        End Property

        ''' <summary>Regeln gelten nur fuer Benutzer ohne Administratorrolle.</summary>
        Public ReadOnly Property SelectedUserNeedsRules As Boolean
            Get
                Return SelectedUserIsOther AndAlso Not _selectedUserRow.Account.IsAdmin
            End Get
        End Property

        Public ReadOnly Property SelectedUserTitle As String
            Get
                Return If(_selectedUserRow?.Name, "")
            End Get
        End Property

        Public Property SelectedUserEmail As String
            Get
                Return _selectedUserEmail
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_selectedUserEmail, If(value, ""))
            End Set
        End Property

        ''' <summary>Der Benutzername des gewaehlten Benutzers zum Umbenennen, auch des Eigentuemers
        ''' (<see cref="RenameUserCommand"/>).</summary>
        Public Property SelectedUserName As String
            Get
                Return _selectedUserName
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_selectedUserName, If(value, ""))
            End Set
        End Property

        Public Property SelectedUserIsAdmin As Boolean
            Get
                Return _selectedUserRow IsNot Nothing AndAlso _selectedUserRow.Account.IsAdmin
            End Get
            Set(value As Boolean)
                If _selectedUserRow Is Nothing OrElse _selectedUserRow.Account.IsOwner OrElse value = _selectedUserRow.Account.IsAdmin Then Return
                ReportUsers(UserOutcomeText(UserAccountService.SetAdmin(_selectedUserRow.Account.Id, value)))
                ReloadUserRows(_selectedUserRow.Account.Id)
            End Set
        End Property

        Public ReadOnly Property UserLanguageOptions As IReadOnlyList(Of UserLanguageOption) = UserLanguageOption.All

        ''' <summary>Die Sprache des gewaehlten Benutzers im Browser (mobile Seite, Meldungen,
        ''' Nachrichten). Wirkt sofort; die volle Oberflaeche bleibt in der Sprache der Anwendung.</summary>
        Public Property SelectedUserLanguage As UserLanguageOption
            Get
                If _selectedUserRow Is Nothing Then Return Nothing
                Dim key = If(_selectedUserRow.Account.Language.Length = 0, "System", _selectedUserRow.Account.Language)
                Return UserLanguageOptions.FirstOrDefault(Function(o) o.Key = key)
            End Get
            Set(value As UserLanguageOption)
                If value Is Nothing OrElse _selectedUserRow Is Nothing Then Return
                Dim current = If(_selectedUserRow.Account.Language.Length = 0, "System", _selectedUserRow.Account.Language)
                If value.Key = current Then Return
                ReportUsers(UserOutcomeText(UserAccountService.SetLanguage(_selectedUserRow.Account.Id, value.Key)))
                ReloadUserRows(_selectedUserRow.Account.Id)
            End Set
        End Property

        Public Property SelectedUserIsDisabled As Boolean
            Get
                Return _selectedUserRow IsNot Nothing AndAlso _selectedUserRow.Account.IsDisabled
            End Get
            Set(value As Boolean)
                If _selectedUserRow Is Nothing OrElse _selectedUserRow.Account.IsOwner OrElse value = _selectedUserRow.Account.IsDisabled Then Return
                ReportUsers(UserOutcomeText(UserAccountService.SetDisabled(_selectedUserRow.Account.Id, value)))
                ReloadUserRows(_selectedUserRow.Account.Id)
            End Set
        End Property

        Public Property SelectedUserNewPassword As String
            Get
                Return _selectedUserNewPassword
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_selectedUserNewPassword, If(value, ""))
            End Set
        End Property

        Public Property SelectedUserNewPasswordRepeat As String
            Get
                Return _selectedUserNewPasswordRepeat
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_selectedUserNewPasswordRepeat, If(value, ""))
            End Set
        End Property

        Public Property NewUserName As String
            Get
                Return _newUserName
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_newUserName, If(value, ""))
            End Set
        End Property

        Public Property NewUserEmail As String
            Get
                Return _newUserEmail
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_newUserEmail, If(value, ""))
            End Set
        End Property

        Public Property NewUserPassword As String
            Get
                Return _newUserPassword
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_newUserPassword, If(value, ""))
            End Set
        End Property

        Public Property NewUserPasswordRepeat As String
            Get
                Return _newUserPasswordRepeat
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_newUserPasswordRepeat, If(value, ""))
            End Set
        End Property

        Public Property NewUserIsAdmin As Boolean
            Get
                Return _newUserIsAdmin
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_newUserIsAdmin, value)
            End Set
        End Property

        Public Property UsersStatusText As String
            Get
                Return _usersStatusText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_usersStatusText, If(value, ""))
            End Set
        End Property

        Public Property UsersStatusIsError As Boolean
            Get
                Return _usersStatusIsError
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_usersStatusIsError, value)
            End Set
        End Property

        Private Sub ReportUsers(text As String, Optional isError As Boolean? = Nothing)
            UsersStatusIsError = If(isError, text IsNot Nothing AndAlso text <> LocalizationService.T("Gespeichert."))
            UsersStatusText = text
        End Sub

        ''' <summary>Der Satz zu einem Ergebnis der Benutzerverwaltung, je ein ganzer Text.</summary>
        Friend Shared Function UserOutcomeText(outcome As UserChangeOutcome) As String
            Select Case outcome
                Case UserChangeOutcome.Done : Return LocalizationService.T("Gespeichert.")
                Case UserChangeOutcome.InvalidName : Return LocalizationService.T("Bitte einen Benutzernamen eingeben, höchstens 64 Zeichen.")
                Case UserChangeOutcome.NameTaken : Return LocalizationService.T("Diesen Benutzernamen gibt es schon.")
                Case UserChangeOutcome.InvalidEmail : Return LocalizationService.T("Die E-Mail-Adresse ist ungültig.")
                Case UserChangeOutcome.EmailTaken : Return LocalizationService.T("Diese E-Mail-Adresse gehört schon einem anderen Benutzer.")
                Case UserChangeOutcome.WeakPassword : Return LocalizationService.T("Das Kennwort erfüllt die Regeln nicht.")
                Case UserChangeOutcome.OwnerProtected : Return LocalizationService.T("Der Eigentümer bleibt immer Administrator und lässt sich weder sperren noch löschen.")
                Case Else : Return LocalizationService.T("Diesen Benutzer gibt es nicht mehr.")
            End Select
        End Function

        ' --- Regeln -------------------------------------------------------------------------------

        Private Sub ReloadRuleRows()
            _userRuleRows.Clear()
            If _selectedUserRow Is Nothing OrElse _selectedUserRow.Account.IsOwner Then Return
            For Each rule In UserAccountService.RulesFor(_selectedUserRow.Account.Id)
                _userRuleRows.Add(New UserFolderRuleRow(rule.Folder, rule.Level, AddressOf OnRuleLevelChanged, AddressOf OnRuleRemoved))
            Next
            Me.RaisePropertyChanged(NameOf(UserRulesSummary))
        End Sub

        Private Sub OnRuleLevelChanged(row As UserFolderRuleRow)
            If _selectedUserRow Is Nothing Then Return
            ReportUsers(UserOutcomeText(UserAccountService.SetRule(_selectedUserRow.Account.Id, row.Folder, row.SelectedLevel.Level)))
            Me.RaisePropertyChanged(NameOf(UserRulesSummary))
        End Sub

        Private Sub OnRuleRemoved(row As UserFolderRuleRow)
            If _selectedUserRow Is Nothing Then Return
            UserAccountService.RemoveRule(_selectedUserRow.Account.Id, row.Folder)
            ReloadRuleRows()
        End Sub

        ''' <summary>Eine neue Regel fuer den gewaehlten Benutzer, ab Werk "Ansehen". Aus dem
        ''' Ordnerwaehler der Seite (SettingsView).</summary>
        Public Sub AddUserRule(folder As String)
            If Not SelectedUserNeedsRules OrElse String.IsNullOrWhiteSpace(folder) Then Return
            ReportUsers(UserOutcomeText(UserAccountService.SetRule(_selectedUserRow.Account.Id, folder, FolderAccessLevel.View)))
            ReloadRuleRows()
        End Sub

        ''' <summary>Was aus den Regeln folgt: die Startordner der mobilen Seite und, je Regel, was
        ''' dort und darunter gilt. Ohne Regel kein Zugriff.</summary>
        Public ReadOnly Property UserRulesSummary As String
            Get
                If Not SelectedUserIsOther Then Return ""
                If _selectedUserRow.Account.IsAdmin Then Return LocalizationService.T("Administratoren sehen alles und brauchen keine Regeln.")
                Dim rules = UserAccountService.RulesFor(_selectedUserRow.Account.Id)
                If rules.Count = 0 Then Return LocalizationService.T("Ohne Regel sieht dieser Benutzer nichts.")
                Dim starts = UserAccountService.StartFolders(rules)
                Dim lines As New List(Of String)()
                If starts.Count = 0 Then
                    lines.Add(LocalizationService.T("Keine Regel gibt einen Ordner zum Ansehen frei."))
                Else
                    lines.Add(LocalizationService.T("Auf der mobilen Seite beginnt er hier:"))
                    lines.AddRange(starts.Select(Function(s) "  " & s))
                End If
                lines.Add(LocalizationService.T("Die nächstliegende Regel gewinnt, auch eine engere auf einem Unterordner."))
                Return String.Join(Environment.NewLine, lines)
            End Get
        End Property

        ' --- Befehle ------------------------------------------------------------------------------

        Private _createUserCommand As ICommand
        Private _saveUserEmailCommand As ICommand
        Private _setUserPasswordCommand As ICommand
        Private _deleteUserCommand As ICommand
        Private _saveMailCommand As ICommand
        Private _sendTestMailCommand As ICommand
        Private _applyPublicAddressCommand As ICommand

        Public ReadOnly Property CreateUserCommand As ICommand
            Get
                If _createUserCommand Is Nothing Then _createUserCommand = New DelegateCommand(Sub() RunUsersTask(AddressOf CreateUserAsync))
                Return _createUserCommand
            End Get
        End Property

        Public ReadOnly Property SaveUserEmailCommand As ICommand
            Get
                If _saveUserEmailCommand Is Nothing Then
                    _saveUserEmailCommand = New DelegateCommand(
                        Sub()
                            If _selectedUserRow Is Nothing Then Return
                            Dim id = _selectedUserRow.Account.Id
                            ReportUsers(UserOutcomeText(UserAccountService.SetEmail(id, _selectedUserEmail)))
                            ReloadUserRows(id)
                        End Sub)
                End If
                Return _saveUserEmailCommand
            End Get
        End Property

        Private _renameUserCommand As ICommand

        ''' <summary>Benennt den gewaehlten Benutzer um, den Eigentuemer eingeschlossen. Wirkt sofort;
        ''' offene Anmeldungen bleiben. Mit zweitem Faktor nennt die Meldung, dass die App des
        ''' Benutzers weiter den alten Namen zeigt.</summary>
        Public ReadOnly Property RenameUserCommand As ICommand
            Get
                If _renameUserCommand Is Nothing Then
                    _renameUserCommand = New DelegateCommand(
                        Sub()
                            If _selectedUserRow Is Nothing Then Return
                            Dim id = _selectedUserRow.Account.Id
                            Dim outcome = UserAccountService.RenameUser(id, _selectedUserName)
                            If outcome = UserChangeOutcome.Done AndAlso TwoFactorService.IsEnabled(id) Then
                                ReportUsers(LocalizationService.T("Gespeichert. Die Authenticator-App zeigt weiter den alten Namen; ihre Codes gelten trotzdem."), False)
                            Else
                                ReportUsers(UserOutcomeText(outcome))
                            End If
                            ' Abgewiesen bleibt der eingegebene Name im Feld stehen.
                            If outcome = UserChangeOutcome.Done Then ReloadUserRows(id)
                        End Sub)
                End If
                Return _renameUserCommand
            End Get
        End Property

        Public ReadOnly Property SetUserPasswordCommand As ICommand
            Get
                If _setUserPasswordCommand Is Nothing Then _setUserPasswordCommand = New DelegateCommand(Sub() RunUsersTask(AddressOf SetUserPasswordAsync))
                Return _setUserPasswordCommand
            End Get
        End Property

        Public ReadOnly Property DeleteUserCommand As ICommand
            Get
                If _deleteUserCommand Is Nothing Then _deleteUserCommand = New DelegateCommand(Sub() RunUsersTask(AddressOf DeleteUserAsync))
                Return _deleteUserCommand
            End Get
        End Property

        Public ReadOnly Property SaveMailCommand As ICommand
            Get
                If _saveMailCommand Is Nothing Then _saveMailCommand = New DelegateCommand(Sub() SaveMailSettings())
                Return _saveMailCommand
            End Get
        End Property

        Public ReadOnly Property ApplyPublicAddressCommand As ICommand
            Get
                If _applyPublicAddressCommand Is Nothing Then _applyPublicAddressCommand = New DelegateCommand(Sub() SavePublicAddress())
                Return _applyPublicAddressCommand
            End Get
        End Property

        Public ReadOnly Property SendTestMailCommand As ICommand
            Get
                If _sendTestMailCommand Is Nothing Then _sendTestMailCommand = New DelegateCommand(Sub() RunUsersTask(AddressOf SendTestMailAsync))
                Return _sendTestMailCommand
            End Get
        End Property

        ''' <summary>Fuehrt einen langsamen Schritt aus (Hash, Mailversand), einen zur Zeit.</summary>
        Private Async Sub RunUsersTask(work As Func(Of Task))
            If _usersBusy Then Return
            _usersBusy = True
            Try
                Await work()
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.Users", ex)
                ReportUsers(LocalizationService.T("Die Änderung ließ sich nicht speichern."), True)
            Finally
                _usersBusy = False
            End Try
        End Sub

        Private Async Function CreateUserAsync() As Task
            If _newUserPassword <> _newUserPasswordRepeat Then
                ReportUsers(LocalizationService.T("Die beiden neuen Kennwörter stimmen nicht überein."), True)
                Return
            End If
            Dim problems = AppLockService.PasswordProblems(_newUserPassword)
            If problems.Count > 0 Then
                ReportUsers(PasswordRuleText(problems(0)), True)
                Return
            End If
            Dim name = _newUserName, email = _newUserEmail, password = _newUserPassword, isAdmin = _newUserIsAdmin
            Dim createdId As String = Nothing
            Dim outcome = Await Task.Run(Function() UserAccountService.CreateUser(name, email, password, isAdmin, createdId))
            ReportUsers(UserOutcomeText(outcome))
            If outcome <> UserChangeOutcome.Done Then Return
            NewUserName = ""
            NewUserEmail = ""
            NewUserPassword = ""
            NewUserPasswordRepeat = ""
            NewUserIsAdmin = False
            ReloadUserRows(createdId)
        End Function

        Private Async Function SetUserPasswordAsync() As Task
            If Not SelectedUserIsOther Then Return
            If _selectedUserNewPassword <> _selectedUserNewPasswordRepeat Then
                ReportUsers(LocalizationService.T("Die beiden neuen Kennwörter stimmen nicht überein."), True)
                Return
            End If
            Dim problems = AppLockService.PasswordProblems(_selectedUserNewPassword)
            If problems.Count > 0 Then
                ReportUsers(PasswordRuleText(problems(0)), True)
                Return
            End If
            Dim id = _selectedUserRow.Account.Id
            Dim password = _selectedUserNewPassword
            Dim outcome = Await Task.Run(Function() UserAccountService.SetPassword(id, password))
            If outcome = UserChangeOutcome.Done Then SecurityLog.PasswordChanged(id, "admin")
            SelectedUserNewPassword = ""
            SelectedUserNewPasswordRepeat = ""
            ReportUsers(If(outcome = UserChangeOutcome.Done, LocalizationService.T("Das neue Kennwort gilt ab sofort. Offene Anmeldungen dieses Benutzers sind beendet."),
                           UserOutcomeText(outcome)), outcome <> UserChangeOutcome.Done)
        End Function

        Private Async Function DeleteUserAsync() As Task
            If Not SelectedUserIsOther OrElse _mainVm Is Nothing Then Return
            Dim account = _selectedUserRow.Account
            Dim confirmed = Await _mainVm.ShowConfirmAsync(
                LocalizationService.T("Benutzer löschen"),
                String.Format(LocalizationService.T("{0} kann sich danach nicht mehr anmelden, und seine Regeln für Ordner sind weg. Seine Fotos und Bewertungen bleiben."), account.Name),
                "Löschen", "Abbrechen")
            If Not confirmed Then Return
            ReportUsers(UserOutcomeText(UserAccountService.Delete(account.Id)))
            ReloadUserRows(Nothing)
        End Function

        ' --- Mailversand ---------------------------------------------------------------------------

        Public Property MailHost As String
            Get
                EnsureUsersLoaded()
                Return _mailHost
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_mailHost, If(value, ""))
            End Set
        End Property

        Public Property MailPort As String
            Get
                EnsureUsersLoaded()
                Return _mailPort
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_mailPort, If(value, ""))
            End Set
        End Property

        Public Property SelectedMailSecurity As MailSecurityOption
            Get
                EnsureUsersLoaded()
                Return _mailSecurity
            End Get
            Set(value As MailSecurityOption)
                If value Is Nothing Then Return
                Me.RaiseAndSetIfChanged(_mailSecurity, value)
            End Set
        End Property

        Public Property MailUserName As String
            Get
                EnsureUsersLoaded()
                Return _mailUserName
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_mailUserName, If(value, ""))
            End Set
        End Property

        Public Property MailPassword As String
            Get
                EnsureUsersLoaded()
                Return _mailPassword
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_mailPassword, If(value, ""))
            End Set
        End Property

        Public Property MailSender As String
            Get
                EnsureUsersLoaded()
                Return _mailSender
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_mailSender, If(value, ""))
            End Set
        End Property

        Public Property PublicAddress As String
            Get
                EnsureUsersLoaded()
                Return _publicAddress
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_publicAddress, If(value, ""))
            End Set
        End Property

        Public Property MailTestRecipient As String
            Get
                EnsureUsersLoaded()
                Return _mailTestRecipient
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_mailTestRecipient, If(value, ""))
            End Set
        End Property

        Public Property MailStatusText As String
            Get
                Return _mailStatusText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_mailStatusText, If(value, ""))
            End Set
        End Property

        Public Property MailStatusIsError As Boolean
            Get
                Return _mailStatusIsError
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_mailStatusIsError, value)
            End Set
        End Property

        Private Sub ReportMail(text As String, isError As Boolean)
            MailStatusIsError = isError
            MailStatusText = text
        End Sub

        Public Property PublicAddressStatusText As String
            Get
                Return _publicAddressStatusText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_publicAddressStatusText, If(value, ""))
            End Set
        End Property

        Public Property PublicAddressStatusIsError As Boolean
            Get
                Return _publicAddressStatusIsError
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_publicAddressStatusIsError, value)
            End Set
        End Property

        ''' <summary>Prueft und speichert die Angaben des Mailversands. False mit Meldung, wenn etwas
        ''' nicht stimmt; dann bleibt der alte Stand.
        '''
        ''' <para>Ohne Server ist der Mailversand aus: das speichert immer, auch wenn die anderen
        ''' Felder noch etwas tragen (so bleiben sie fuer ein spaeteres Einschalten erhalten), und
        ''' sagt dann nur dazu, dass keine Nachricht hinausgeht. Mit Server wird jede Angabe einzeln
        ''' geprueft und genau die genannt, die nicht passt.</para>
        '''
        ''' <para>Die Adresse von FerrumPix im Browser gehoert nicht hierher: sie gilt auch fuer die
        ''' Freigaben und wird ueber <see cref="SavePublicAddress"/> gespeichert, damit ein Fehler
        ''' in den Mailfeldern sie nicht aufhaelt. Fuer "Kennwort vergessen" ist sie aber Pflicht;
        ''' fehlt sie, sagt die Meldung das (gespeichert wird trotzdem).</para></summary>
        Private Function SaveMailSettings() As Boolean
            Dim port As Integer
            Dim host = _mailHost.Trim()
            Dim user = _mailUserName.Trim(), password = _mailPassword, sender = _mailSender.Trim()
            If host.Length > 0 Then
                If Not Integer.TryParse(_mailPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, port) OrElse port < 1 OrElse port > 65535 Then
                    ReportMail(LocalizationService.T("Der Port muss eine Zahl von 1 bis 65535 sein."), True)
                    Return False
                End If
                Dim senderAddress = "", senderName = ""
                If sender.Length = 0 Then
                    If user.Length = 0 OrElse Not UserAccountService.IsValidEmail(user) Then
                        ReportMail(LocalizationService.T("Bitte einen Absender eintragen. Der Benutzername des Mailkontos ist keine E-Mail-Adresse und kann ihn nicht ersetzen."), True)
                        Return False
                    End If
                ElseIf Not MailSettings.TryParseSender(sender, senderAddress, senderName) Then
                    ReportMail(String.Format(LocalizationService.T("Der Absender ""{0}"" ist keine gültige E-Mail-Adresse. Erlaubt sind fotos@example.org oder Name <fotos@example.org>."), sender), True)
                    Return False
                End If
            Else
                ' Ein Port, der nicht taugt, ist ohne Server ohne Belang; dann bleibt die Vorgabe.
                If Not Integer.TryParse(_mailPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, port) OrElse port < 1 OrElse port > 65535 Then port = 587
            End If
            Dim security = _mailSecurity.Security.ToString()
            AppSettingsService.Update(
                Sub(s)
                    s.MailSmtpHost = host
                    s.MailSmtpPort = port
                    s.MailSmtpSecurity = security
                    s.MailSmtpUserName = user
                    s.MailSmtpPassword = password
                    s.MailSenderAddress = sender
                End Sub)
            AppSettingsService.FlushNow()
            If host.Length = 0 AndAlso (user.Length > 0 OrElse password.Length > 0 OrElse sender.Length > 0) Then
                ReportMail(LocalizationService.T("Gespeichert. Ohne Server geht keine E-Mail hinaus."), False)
            ElseIf host.Length > 0 AndAlso BrowserBridge.ConfiguredPublicAddress() Is Nothing Then
                ' Der Verweis in der Mail beginnt nur mit der eingetragenen Adresse
                ' (BrowserBridge.CanSendResetMail); ohne sie bleibt "Kennwort vergessen" beim
                ' Hinweis auf den Administrator. Gespeichert ist trotzdem, die Test-E-Mail geht.
                ReportMail(LocalizationService.T("Gespeichert. Für ein vergessenes Kennwort fehlt noch die Adresse von FerrumPix im Browser; ohne sie geht keine Mail hinaus."), True)
            Else
                ReportMail(LocalizationService.T("Gespeichert."), False)
            End If
            Return True
        End Function

        ''' <summary>Prueft und speichert die Adresse von FerrumPix im Browser. Leerzeichen am Rand
        ''' und ein Schraegstrich am Ende fallen weg, Schema und Name werden klein geschrieben, ein
        ''' Standardport faellt weg. False mit Meldung, wenn sie nicht taugt.</summary>
        Private Function SavePublicAddress() As Boolean
            Dim address = _publicAddress.Trim().TrimEnd("/"c)
            If address.Length > 0 Then
                If Not BrowserBridge.IsUsablePublicAddress(address) Then
                    PublicAddressStatusIsError = True
                    PublicAddressStatusText = LocalizationService.T("Die Adresse braucht die Form https://fotos.example.org, ohne Pfad.")
                    Return False
                End If
                address = New Uri(address, UriKind.Absolute).GetLeftPart(UriPartial.Authority)
            End If
            AppSettingsService.Update(Sub(s) s.BrowserPublicAddress = address)
            AppSettingsService.FlushNow()
            PublicAddress = address
            PublicAddressStatusIsError = False
            PublicAddressStatusText = LocalizationService.T("Gespeichert.")
            Return True
        End Function

        Private Async Function SendTestMailAsync() As Task
            If Not SaveMailSettings() Then Return
            If _mailHost.Trim().Length = 0 Then
                ReportMail(LocalizationService.T("Bitte den Server für den Mailversand eintragen."), True)
                Return
            End If
            Dim recipient = _mailTestRecipient.Trim()
            If recipient.Length = 0 OrElse Not UserAccountService.IsValidEmail(recipient) Then
                ReportMail(LocalizationService.T("Bitte eine gültige Adresse für die Test-E-Mail eingeben."), True)
                Return
            End If
            ' Nach dem Speichern oben sind Server, Port und Absender geprueft; vollstaendig ist es
            ' damit immer.
            Dim settings = MailSettings.FromAppSettings(AppSettingsService.Load())
            ReportMail(LocalizationService.T("Wird gesendet ..."), False)
            Dim subject = LocalizationService.T("FerrumPix: Test-E-Mail")
            ' Dieselbe Vorlage wie "Kennwort vergessen"; der Knopf fuehrt zur Adresse von FerrumPix
            ' im Browser, wenn eine eingetragen ist.
            Dim address = If(AppSettingsService.Load().BrowserPublicAddress, "").Trim()
            Dim body As New MailContent With {
                .Title = LocalizationService.T("Der Mailversand funktioniert"),
                .ButtonText = LocalizationService.T("FerrumPix öffnen"),
                .Link = address,
                .FooterAddress = address,
                .Language = LocalizationService.HtmlLanguage(LocalizationService.TextCulture)}
            body.Paragraphs.Add(LocalizationService.T("Diese Nachricht kommt von FerrumPix. Der Mailversand für ein vergessenes Kennwort funktioniert."))
            Try
                Await MailService.SendAsync(settings, recipient, subject, body)
                ReportMail(String.Format(LocalizationService.T("Die Test-E-Mail an {0} ist versendet."), recipient), False)
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.TestMail", ex)
                ReportMail(String.Format(LocalizationService.T("Die Test-E-Mail ließ sich nicht senden: {0}"), ex.Message), True)
            End Try
        End Function

    End Class

End Namespace
