Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia.Media.Imaging
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>
    ''' Der zweite Faktor in der Benutzerverwaltung (TwoFactorService, BrowserBridgeTwoFactor.vb):
    ''' das eigene Konto einrichten und abschalten, und den eines anderen Benutzers zuruecksetzen.
    '''
    ''' <para>"DAS EIGENE KONTO" ist das, mit dem die volle Oberflaeche gerade bedient wird: im
    ''' Browser das des verbundenen Browsers, am Rechner der Eigentuemer
    ''' (BrowserBridge.DesktopUserId). Am Rechner laesst sich der zweite Faktor so fuer den Browser
    ''' vorbereiten; das Anmeldefenster am Rechner selbst fragt nie einen Code.</para>
    '''
    ''' <para>Wie auf der mobilen Seite: aktiv erst mit einem gueltigen Code, danach einmal die
    ''' zehn Wiederherstellungscodes (auch als Textdatei), Abschalten nur mit Kennwort UND Code.</para>
    ''' </summary>
    Partial Public Class SettingsViewModel

        Private _ownTwoFactorEnrolling As Boolean
        Private _ownTwoFactorQr As Bitmap
        Private _ownTwoFactorSecret As String = ""
        Private _ownTwoFactorCode As String = ""
        Private _ownTwoFactorPassword As String = ""
        Private _ownTwoFactorRecovery As List(Of String)
        Private _ownTwoFactorStatusText As String = ""
        Private _ownTwoFactorStatusIsError As Boolean
        Private _twoFactorBusy As Boolean

        ''' <summary>Die Kennung des eigenen Kontos, siehe oben.</summary>
        Private Shared Function OwnUserId() As String
            Return BrowserBridge.DesktopUserId()
        End Function

        ''' <summary>Ist fuer das eigene Konto ein zweiter Faktor eingerichtet?</summary>
        Public ReadOnly Property OwnTwoFactorEnabled As Boolean
            Get
                Return TwoFactorService.IsEnabled(OwnUserId())
            End Get
        End Property

        ''' <summary>Weder eingerichtet noch im Einrichten: der Knopf "Einrichten" steht da.</summary>
        Public ReadOnly Property OwnTwoFactorCanBegin As Boolean
            Get
                Return Not OwnTwoFactorEnabled AndAlso Not _ownTwoFactorEnrolling AndAlso Not HasOwnTwoFactorRecoveryCodes
            End Get
        End Property

        ''' <summary>Eingerichtet und keine Codes zu zeigen: die Felder zum Abschalten stehen da.</summary>
        Public ReadOnly Property OwnTwoFactorCanDisable As Boolean
            Get
                Return OwnTwoFactorEnabled AndAlso Not HasOwnTwoFactorRecoveryCodes
            End Get
        End Property

        ''' <summary>Der Satz zum Stand des eigenen Kontos, mit seinem Namen.</summary>
        Public ReadOnly Property OwnTwoFactorSummary As String
            Get
                Dim id = OwnUserId()
                Dim name = BrowserBridge.AccountNameOf(id)
                Dim state = If(TwoFactorService.IsEnabled(id),
                               String.Format(LocalizationService.T("Der zweite Faktor ist eingerichtet. Übrige Wiederherstellungscodes: {0}"), TwoFactorService.RecoveryCodesLeft(id)),
                               LocalizationService.T("Der zweite Faktor ist nicht eingerichtet."))
                Return String.Format(LocalizationService.T("Konto: {0}"), name) & Environment.NewLine & state
            End Get
        End Property

        Public ReadOnly Property OwnTwoFactorEnrolling As Boolean
            Get
                Return _ownTwoFactorEnrolling
            End Get
        End Property

        Public ReadOnly Property OwnTwoFactorQr As Bitmap
            Get
                Return _ownTwoFactorQr
            End Get
        End Property

        Public ReadOnly Property OwnTwoFactorSecret As String
            Get
                Return _ownTwoFactorSecret
            End Get
        End Property

        Public Property OwnTwoFactorCode As String
            Get
                Return _ownTwoFactorCode
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_ownTwoFactorCode, If(value, ""))
            End Set
        End Property

        Public Property OwnTwoFactorPassword As String
            Get
                Return _ownTwoFactorPassword
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_ownTwoFactorPassword, If(value, ""))
            End Set
        End Property

        ''' <summary>Die Wiederherstellungscodes direkt nach dem Einrichten, sonst leer. Danach gibt
        ''' es sie nirgends mehr im Klartext.</summary>
        Public ReadOnly Property OwnTwoFactorRecoveryText As String
            Get
                Return If(_ownTwoFactorRecovery Is Nothing, "", String.Join(Environment.NewLine, _ownTwoFactorRecovery))
            End Get
        End Property

        Public ReadOnly Property HasOwnTwoFactorRecoveryCodes As Boolean
            Get
                Return _ownTwoFactorRecovery IsNot Nothing AndAlso _ownTwoFactorRecovery.Count > 0
            End Get
        End Property

        Public Property OwnTwoFactorStatusText As String
            Get
                Return _ownTwoFactorStatusText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_ownTwoFactorStatusText, If(value, ""))
            End Set
        End Property

        Public Property OwnTwoFactorStatusIsError As Boolean
            Get
                Return _ownTwoFactorStatusIsError
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_ownTwoFactorStatusIsError, value)
            End Set
        End Property

        Private Sub ReportOwnTwoFactor(text As String, isError As Boolean)
            OwnTwoFactorStatusIsError = isError
            OwnTwoFactorStatusText = text
        End Sub

        Private Sub RaiseOwnTwoFactorChanged()
            For Each name In {NameOf(OwnTwoFactorEnabled), NameOf(OwnTwoFactorCanBegin), NameOf(OwnTwoFactorCanDisable),
                              NameOf(OwnTwoFactorSummary), NameOf(OwnTwoFactorEnrolling), NameOf(OwnTwoFactorQr),
                              NameOf(OwnTwoFactorSecret), NameOf(OwnTwoFactorRecoveryText), NameOf(HasOwnTwoFactorRecoveryCodes),
                              NameOf(SelectedUserHasTwoFactor), NameOf(CanResetSelectedUserTwoFactor)}
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        ' --- Befehle fuer das eigene Konto ----------------------------------------------------

        Private _beginOwnTwoFactorCommand As ICommand
        Private _confirmOwnTwoFactorCommand As ICommand
        Private _cancelOwnTwoFactorCommand As ICommand
        Private _disableOwnTwoFactorCommand As ICommand
        Private _closeOwnRecoveryCodesCommand As ICommand
        Private _resetSelectedUserTwoFactorCommand As ICommand

        Public ReadOnly Property BeginOwnTwoFactorCommand As ICommand
            Get
                If _beginOwnTwoFactorCommand Is Nothing Then _beginOwnTwoFactorCommand = New DelegateCommand(Sub() BeginOwnTwoFactor())
                Return _beginOwnTwoFactorCommand
            End Get
        End Property

        Public ReadOnly Property ConfirmOwnTwoFactorCommand As ICommand
            Get
                If _confirmOwnTwoFactorCommand Is Nothing Then _confirmOwnTwoFactorCommand = New DelegateCommand(Sub() ConfirmOwnTwoFactor())
                Return _confirmOwnTwoFactorCommand
            End Get
        End Property

        Public ReadOnly Property CancelOwnTwoFactorCommand As ICommand
            Get
                If _cancelOwnTwoFactorCommand Is Nothing Then
                    _cancelOwnTwoFactorCommand = New DelegateCommand(
                        Sub()
                            TwoFactorService.ForgetPending(OwnUserId())
                            EndOwnEnrollment()
                            ReportOwnTwoFactor("", False)
                        End Sub)
                End If
                Return _cancelOwnTwoFactorCommand
            End Get
        End Property

        Public ReadOnly Property DisableOwnTwoFactorCommand As ICommand
            Get
                If _disableOwnTwoFactorCommand Is Nothing Then _disableOwnTwoFactorCommand = New DelegateCommand(Sub() DisableOwnTwoFactorAsync())
                Return _disableOwnTwoFactorCommand
            End Get
        End Property

        ''' <summary>"Fertig" unter den Wiederherstellungscodes: sie verschwinden und kommen nicht
        ''' wieder.</summary>
        Public ReadOnly Property CloseOwnRecoveryCodesCommand As ICommand
            Get
                If _closeOwnRecoveryCodesCommand Is Nothing Then
                    _closeOwnRecoveryCodesCommand = New DelegateCommand(
                        Sub()
                            _ownTwoFactorRecovery = Nothing
                            RaiseOwnTwoFactorChanged()
                        End Sub)
                End If
                Return _closeOwnRecoveryCodesCommand
            End Get
        End Property

        Private Sub BeginOwnTwoFactor()
            Dim id = OwnUserId()
            Dim enrollment = TwoFactorService.BeginEnrollment(id, BrowserBridge.AccountNameOf(id))
            If enrollment Is Nothing Then
                ReportOwnTwoFactor(LocalizationService.T("Der zweite Faktor ist schon eingerichtet."), True)
                RaiseOwnTwoFactorChanged()
                Return
            End If
            Try
                Using stream As New MemoryStream(TwoFactorService.QrPng(enrollment.Uri, 6))
                    _ownTwoFactorQr = New Bitmap(stream)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.TwoFactor.Qr", ex)
                _ownTwoFactorQr = Nothing
            End Try
            _ownTwoFactorSecret = TwoFactorService.FormatSecret(enrollment.Secret)
            _ownTwoFactorEnrolling = True
            OwnTwoFactorCode = ""
            ReportOwnTwoFactor("", False)
            RaiseOwnTwoFactorChanged()
        End Sub

        Private Sub EndOwnEnrollment()
            _ownTwoFactorEnrolling = False
            _ownTwoFactorSecret = ""
            _ownTwoFactorQr?.Dispose()
            _ownTwoFactorQr = Nothing
            OwnTwoFactorCode = ""
            RaiseOwnTwoFactorChanged()
        End Sub

        Private Sub ConfirmOwnTwoFactor()
            Dim id = OwnUserId()
            If TwoFactorService.PendingEnrollment(id, "") Is Nothing Then
                EndOwnEnrollment()
                ReportOwnTwoFactor(LocalizationService.T("Das Einrichten ist abgelaufen. Bitte neu beginnen."), True)
                Return
            End If
            Dim codes As List(Of String) = Nothing
            If Not TwoFactorService.ConfirmEnrollment(id, _ownTwoFactorCode, codes) Then
                OwnTwoFactorCode = ""
                ReportOwnTwoFactor(LocalizationService.T("Der Code ist falsch."), True)
                Return
            End If
            Dim actor = SecurityLog.DesktopActor()
            TwoFactorService.NoteEnabled(id, actor.From, "settings")
            _ownTwoFactorRecovery = codes
            EndOwnEnrollment()
            ReloadUserRows(_selectedUserRow?.Account.Id)
            ReportOwnTwoFactor(LocalizationService.T("Der zweite Faktor ist eingerichtet."), False)
        End Sub

        Private Async Sub DisableOwnTwoFactorAsync()
            If _twoFactorBusy Then Return
            _twoFactorBusy = True
            Try
                Dim id = OwnUserId()
                Dim password = _ownTwoFactorPassword
                Dim code = _ownTwoFactorCode
                Dim actor = SecurityLog.DesktopActor()
                Dim counters = {AppLockService.LocalKey, "code account " & id}
                Dim problem = Await Task.Run(Function() BrowserBridge.DisableSecondFactor(id, password, code, counters, actor.From, "settings"))
                OwnTwoFactorPassword = ""
                OwnTwoFactorCode = ""
                If problem IsNot Nothing Then
                    ReportOwnTwoFactor(problem, True)
                Else
                    ReportOwnTwoFactor(LocalizationService.T("Der zweite Faktor ist abgeschaltet."), False)
                    ReloadUserRows(_selectedUserRow?.Account.Id)
                End If
                RaiseOwnTwoFactorChanged()
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.TwoFactor.Disable", ex)
                ReportOwnTwoFactor(LocalizationService.T("Die Änderung ließ sich nicht speichern."), True)
            Finally
                _twoFactorBusy = False
            End Try
        End Sub

        ''' <summary>Die Codes als Textdatei, Inhalt und Name, fuer "Als Textdatei speichern".</summary>
        Public Function OwnRecoveryCodesFileText() As String
            If _ownTwoFactorRecovery Is Nothing Then Return ""
            Return TwoFactorService.RecoveryCodesText(LocalizationService.T("Wiederherstellungscodes"),
                LocalizationService.T("Jeder Code gilt einmal, falls die App nicht zur Hand ist. Sie erscheinen nur jetzt; bitte sicher aufbewahren."),
                BrowserBridge.AccountNameOf(OwnUserId()), _ownTwoFactorRecovery)
        End Function

        Public Const RecoveryCodesFileName As String = "ferrumpix-recovery-codes.txt"

        ''' <summary>Im Browser: die Textdatei geht als Download an das Geraet (Ziel "Herunterladen"
        ''' der Bruecke). True, wenn das ging; sonst waehlt die Ansicht einen Ort.</summary>
        Public Function OfferOwnRecoveryCodesDownload() As Boolean
            If Not BrowserBridge.CanDownloadToDevice OrElse Not HasOwnTwoFactorRecoveryCodes Then Return False
            Dim folder = BrowserBridge.CreateDownloadFolder()
            If folder Is Nothing Then Return False
            Try
                Dim path = IO.Path.Combine(folder, RecoveryCodesFileName)
                File.WriteAllText(path, OwnRecoveryCodesFileText(), New System.Text.UTF8Encoding(False))
                Return BrowserBridge.OfferDownloadFolder(folder, {path}) IsNot Nothing
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                DiagnosticLogService.LogException("Settings.TwoFactor.Download", ex)
                BrowserBridge.DiscardDownloadFolder(folder)
                Return False
            End Try
        End Function

        ''' <summary>Schreibt die Textdatei an den gewaehlten Ort (am Rechner).</summary>
        Public Sub SaveOwnRecoveryCodes(path As String)
            Try
                File.WriteAllText(path, OwnRecoveryCodesFileText(), New System.Text.UTF8Encoding(False))
                ReportOwnTwoFactor(LocalizationService.T("Gespeichert."), False)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                DiagnosticLogService.LogException("Settings.TwoFactor.Save", ex)
                ReportOwnTwoFactor(LocalizationService.T("Die Änderung ließ sich nicht speichern."), True)
            End Try
        End Sub

        ' --- Ein anderer Benutzer ------------------------------------------------------------

        ''' <summary>Hat der gewaehlte Benutzer einen zweiten Faktor?</summary>
        Public ReadOnly Property SelectedUserHasTwoFactor As Boolean
            Get
                Return _selectedUserRow IsNot Nothing AndAlso TwoFactorService.IsEnabled(_selectedUserRow.Account.Id)
            End Get
        End Property

        ''' <summary>Zuruecksetzen geht fuer jeden anderen Benutzer mit zweitem Faktor, auch fuer den
        ''' Eigentuemer, wenn ein weiterer Administrator die Oberflaeche bedient; das eigene Konto
        ''' schaltet man oben mit Kennwort und Code ab.</summary>
        Public ReadOnly Property CanResetSelectedUserTwoFactor As Boolean
            Get
                Return SelectedUserHasTwoFactor AndAlso _selectedUserRow.Account.Id <> OwnUserId()
            End Get
        End Property

        Public ReadOnly Property ResetSelectedUserTwoFactorCommand As ICommand
            Get
                If _resetSelectedUserTwoFactorCommand Is Nothing Then _resetSelectedUserTwoFactorCommand = New DelegateCommand(Sub() RunUsersTask(AddressOf ResetSelectedUserTwoFactorAsync))
                Return _resetSelectedUserTwoFactorCommand
            End Get
        End Property

        ''' <summary>Setzt den zweiten Faktor des gewaehlten Benutzers zurueck, nach Rueckfrage.
        ''' Seine Sitzungen enden; er meldet sich danach nur mit dem Kennwort an.</summary>
        Private Async Function ResetSelectedUserTwoFactorAsync() As Task
            If Not CanResetSelectedUserTwoFactor OrElse _mainVm Is Nothing Then Return
            Dim account = _selectedUserRow.Account
            Dim name = If(account.Name.Length > 0, account.Name, LocalizationService.T("Eigentümer"))
            Dim confirmed = Await _mainVm.ShowConfirmAsync(
                LocalizationService.T("Zweiten Faktor zurücksetzen"),
                String.Format(LocalizationService.T("{0} meldet sich danach nur mit dem Kennwort an und kann den zweiten Faktor neu einrichten. Offene Anmeldungen dieses Benutzers enden."), name),
                "Zurücksetzen", "Abbrechen")
            If Not confirmed Then Return
            BrowserBridge.ResetSecondFactor(account.Id)
            ReportUsers(LocalizationService.T("Der zweite Faktor ist zurückgesetzt. Offene Anmeldungen dieses Benutzers sind beendet."), False)
            ReloadUserRows(account.Id)
            RaiseOwnTwoFactorChanged()
        End Function

    End Class

End Namespace
