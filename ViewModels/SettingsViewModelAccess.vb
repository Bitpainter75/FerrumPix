Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Eine Stufe der Sperre nach Inaktivitaet. Der Name wird beim Lesen uebersetzt,
    ''' damit er einen Sprachwechsel mitmacht.</summary>
    Public NotInheritable Class AppLockIdleOption
        Public Property Minutes As Integer

        Public ReadOnly Property Name As String
            Get
                If Minutes <= 0 Then Return LocalizationService.T("Nie")
                Return String.Format(LocalizationService.T("Nach {0} Minuten"), Minutes.ToString(CultureInfo.CurrentCulture))
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Der Teil "Zugang" der Einstellungen: Schutz mit Kennwort, Kennwort setzen oder aendern,
    ''' Sperre nach Inaktivitaet.
    '''
    ''' <para>NICHTS WIRKT SOFORT. Anders als die uebrigen Einstellungen gilt eine Aenderung erst mit
    ''' "Anwenden", und ist ein Kennwort gesetzt, nur mit diesem: wer an einem entsperrten Rechner
    ''' vorbeikommt, soll den Schutz nicht abschalten oder das Kennwort tauschen koennen. Deshalb
    ''' nimmt auch "Auf Standard zuruecksetzen" diesen Teil nicht mit.</para>
    '''
    ''' <para>KEIN HINTERTUERCHEN. Ein vergessenes Kennwort laesst sich in der Anwendung nicht
    ''' zurueckholen; der Weg steht im Hinweis darunter (Eintrag in settings.json loeschen, solange
    ''' die Anwendung nicht laeuft).</para>
    ''' </summary>
    Partial Public Class SettingsViewModel

        Private _accessProtectionWanted As Boolean = AppLockService.IsProtected
        Private _accessCurrentPassword As String = ""
        Private _accessNewPassword As String = ""
        Private _accessNewPasswordRepeat As String = ""
        Private _accessStatusText As String = ""
        Private _accessStatusIsError As Boolean
        Private _accessIdleMinutes As Integer = AppLockService.IdleMinutes
        Private _applyAccessCommand As ICommand
        Private _lockNowCommand As ICommand

        Public ReadOnly Property AccessIdleOptions As IReadOnlyList(Of AppLockIdleOption) =
            AppLockService.IdleChoices.Select(Function(m) New AppLockIdleOption With {.Minutes = m}).ToList()

        ''' <summary>Ist die Anwendung jetzt mit einem Kennwort geschuetzt?</summary>
        Public ReadOnly Property AccessIsProtected As Boolean
            Get
                Return AppLockService.IsProtected
            End Get
        End Property

        ''' <summary>Kam das Kennwort beim Start mit (Behaelter)? Dann laesst es sich hier weder
        ''' aendern noch abschalten.</summary>
        Public ReadOnly Property AccessPasswordFromStart As Boolean
            Get
                Return AppLockService.IsPasswordFromStart
            End Get
        End Property

        ''' <summary>Der Schalter "Mit Kennwort schützen". Vorgemerkt bis "Anwenden".</summary>
        Public Property AccessProtectionWanted As Boolean
            Get
                Return _accessProtectionWanted
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_accessProtectionWanted, value)
                Me.RaisePropertyChanged(NameOf(AccessShowsNewPassword))
                Me.RaisePropertyChanged(NameOf(AccessShowsIdle))
                RaiseCanApplyAccess()
            End Set
        End Property

        ''' <summary>Was dem neuen Kennwort noch fehlt, eine Regel je Zeile, waehrend getippt wird.
        ''' Leer, solange nichts eingegeben ist oder alles stimmt.</summary>
        Public ReadOnly Property AccessPasswordHints As String
            Get
                If _accessNewPassword.Length = 0 AndAlso _accessNewPasswordRepeat.Length = 0 Then Return ""
                Dim lines As New List(Of String)()
                For Each problem In AppLockService.PasswordProblems(_accessNewPassword)
                    lines.Add(PasswordRuleText(problem))
                Next
                If _accessNewPassword <> _accessNewPasswordRepeat Then
                    lines.Add(LocalizationService.T("Die Wiederholung stimmt noch nicht mit dem neuen Kennwort überein."))
                End If
                Return String.Join(Environment.NewLine, lines)
            End Get
        End Property

        Public ReadOnly Property AccessHasPasswordHints As Boolean
            Get
                Return AccessPasswordHints.Length > 0
            End Get
        End Property

        ''' <summary>Der Satz zu einer fehlenden Regel, je ein ganzer Satz fuer die Uebersetzung.</summary>
        Friend Shared Function PasswordRuleText(problem As PasswordRule) As String
            Select Case problem
                Case PasswordRule.TooShort
                    Return String.Format(LocalizationService.T("Noch nötig: mindestens {0} Zeichen."),
                                         AppLockService.MinimumPasswordLength.ToString(CultureInfo.CurrentCulture))
                Case PasswordRule.NoUppercase
                    Return LocalizationService.T("Noch nötig: ein Großbuchstabe.")
                Case PasswordRule.NoLowercase
                    Return LocalizationService.T("Noch nötig: ein Kleinbuchstabe.")
                Case PasswordRule.NoDigit
                    Return LocalizationService.T("Noch nötig: eine Zahl.")
                Case Else
                    Return LocalizationService.T("Noch nötig: ein Sonderzeichen, auch ein Leerzeichen zählt.")
            End Select
        End Function

        ''' <summary>Ob "Anwenden" gerade etwas Gueltiges uebernehmen kann: beim Abschalten immer
        ''' (das aktuelle Kennwort prueft der Schritt selbst), mit neuem Kennwort erst, wenn alle
        ''' Regeln erfuellt sind und die Wiederholung passt, ohne neues Kennwort nur, wenn schon eines
        ''' gesetzt ist (dann aendert sich nur die Sperrzeit).</summary>
        Public ReadOnly Property CanApplyAccess As Boolean
            Get
                If Not _accessProtectionWanted Then Return True
                If _accessNewPassword.Length = 0 AndAlso _accessNewPasswordRepeat.Length = 0 Then Return AccessIsProtected
                Return AppLockService.PasswordProblems(_accessNewPassword).Count = 0 AndAlso
                       _accessNewPassword = _accessNewPasswordRepeat
            End Get
        End Property

        ''' <summary>Waehrend "Anwenden" laeuft (die Pruefung ist mit Absicht langsam), ist der
        ''' Knopf gesperrt.</summary>
        Private _applyingAccess As Boolean

        Private Sub RaiseAccessPasswordHints()
            Me.RaisePropertyChanged(NameOf(AccessPasswordHints))
            Me.RaisePropertyChanged(NameOf(AccessHasPasswordHints))
            RaiseCanApplyAccess()
        End Sub

        Private Sub RaiseCanApplyAccess()
            Me.RaisePropertyChanged(NameOf(CanApplyAccess))
            TryCast(_applyAccessCommand, DelegateCommand)?.RaiseCanExecuteChanged()
        End Sub

        Private Async Sub ExecuteApplyAccess(parameter As Object)
            If _applyingAccess Then Return
            _applyingAccess = True
            RaiseCanApplyAccess()
            Try
                Await ApplyAccessAsync()
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.ApplyAccessCommand", ex)
            Finally
                _applyingAccess = False
                RaiseCanApplyAccess()
            End Try
        End Sub

        Public ReadOnly Property AccessShowsCurrentPassword As Boolean
            Get
                Return AccessIsProtected
            End Get
        End Property

        Public ReadOnly Property AccessShowsNewPassword As Boolean
            Get
                Return _accessProtectionWanted AndAlso Not AccessPasswordFromStart
            End Get
        End Property

        Public ReadOnly Property AccessShowsIdle As Boolean
            Get
                Return _accessProtectionWanted
            End Get
        End Property

        Public Property AccessCurrentPassword As String
            Get
                Return _accessCurrentPassword
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_accessCurrentPassword, If(value, ""))
            End Set
        End Property

        Public Property AccessNewPassword As String
            Get
                Return _accessNewPassword
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_accessNewPassword, If(value, ""))
                RaiseAccessPasswordHints()
            End Set
        End Property

        Public Property AccessNewPasswordRepeat As String
            Get
                Return _accessNewPasswordRepeat
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_accessNewPasswordRepeat, If(value, ""))
                RaiseAccessPasswordHints()
            End Set
        End Property

        ''' <summary>Die gewaehlte Sperre nach Inaktivitaet. Vorgemerkt bis "Anwenden".</summary>
        Public Property SelectedAccessIdle As AppLockIdleOption
            Get
                Return AccessIdleOptions.FirstOrDefault(Function(o) o.Minutes = _accessIdleMinutes)
            End Get
            Set(value As AppLockIdleOption)
                If value Is Nothing OrElse value.Minutes = _accessIdleMinutes Then Return
                _accessIdleMinutes = value.Minutes
                Me.RaisePropertyChanged(NameOf(SelectedAccessIdle))
            End Set
        End Property

        Public Property AccessStatusText As String
            Get
                Return _accessStatusText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_accessStatusText, If(value, ""))
            End Set
        End Property

        Public Property AccessStatusIsError As Boolean
            Get
                Return _accessStatusIsError
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_accessStatusIsError, value)
            End Set
        End Property

        Public ReadOnly Property ApplyAccessCommand As ICommand
            Get
                ' Erst aktiv, wenn alles erfuellt ist (CanApplyAccess); die Zeilen unter dem Feld
                ' sagen, was fehlt.
                If _applyAccessCommand Is Nothing Then
                    _applyAccessCommand = New DelegateCommand(AddressOf ExecuteApplyAccess,
                                                              Function() CanApplyAccess AndAlso Not _applyingAccess)
                End If
                Return _applyAccessCommand
            End Get
        End Property

        Public ReadOnly Property LockNowCommand As ICommand
            Get
                If _lockNowCommand Is Nothing Then _lockNowCommand = New DelegateCommand(Sub() _mainVm?.LockApp())
                Return _lockNowCommand
            End Get
        End Property

        Private Sub ReportAccess(text As String, isError As Boolean)
            AccessStatusIsError = isError
            AccessStatusText = text
        End Sub

        ''' <summary>Uebernimmt Schalter, neues Kennwort und Sperrzeit. Ist ein Kennwort gesetzt,
        ''' nur mit diesem. Gibt True zurueck, wenn etwas gespeichert wurde.</summary>
        Public Async Function ApplyAccessAsync() As Task(Of Boolean)
            Try
                Dim wasProtected = AccessIsProtected
                If wasProtected Then
                    If AccessCurrentPassword.Length = 0 Then
                        ReportAccess(LocalizationService.T("Bitte das aktuelle Kennwort eingeben."), True)
                        Return False
                    End If
                    Dim given = AccessCurrentPassword
                    Dim result = Await Task.Run(Function() AppLockService.CheckPassword(given, AppLockService.LocalKey))
                    AccessCurrentPassword = ""
                    Select Case result.Outcome
                        Case PasswordCheckOutcome.Accepted
                        Case PasswordCheckOutcome.Throttled
                            ReportAccess(LockScreenViewModel.ThrottledText(result.RetryAfter), True)
                            Return False
                        Case Else
                            ReportAccess(LocalizationService.T("Das aktuelle Kennwort ist falsch."), True)
                            Return False
                    End Select
                End If

                Dim wantsNewPassword = AccessNewPassword.Length > 0 OrElse AccessNewPasswordRepeat.Length > 0
                If (Not _accessProtectionWanted OrElse wantsNewPassword) AndAlso AccessPasswordFromStart Then
                    ReportAccess(LocalizationService.T("Das Kennwort kommt aus der Angabe beim Start und lässt sich nur dort ändern."), True)
                    Return False
                End If

                If Not _accessProtectionWanted Then
                    If Not wasProtected Then
                        ReportAccess("", False)
                        Return False
                    End If
                    If BrowserBridge.IsActive AndAlso BrowserBridge.RequiresPassword Then
                        ReportAccess(LocalizationService.T("Über das Netz braucht FerrumPix ein Kennwort. Der Schutz bleibt eingeschaltet."), True)
                        Return False
                    End If
                    AppSettingsService.Update(Sub(s) s.AppLockPasswordHash = "")
                    ' Sonst gaelten die alten Sitzungen nach einem spaeteren Einschalten wieder.
                    BrowserBridge.EndOtherSessions()
                    AppLockService.Unlock()
                    ClearAccessFields()
                    ReportAccess(LocalizationService.T("Der Schutz mit Kennwort ist ausgeschaltet."), False)
                    AfterAccessChanged()
                    Return True
                End If

                Dim newHash As String = Nothing
                If wantsNewPassword Then
                    If AccessNewPassword <> AccessNewPasswordRepeat Then
                        ReportAccess(LocalizationService.T("Die beiden neuen Kennwörter stimmen nicht überein."), True)
                        Return False
                    End If
                    Dim problems = AppLockService.PasswordProblems(AccessNewPassword)
                    If problems.Count > 0 Then
                        ReportAccess(PasswordRuleText(problems(0)), True)
                        Return False
                    End If
                    Dim chosen = AccessNewPassword
                    newHash = Await Task.Run(Function() AppLockService.HashPassword(chosen))
                ElseIf Not wasProtected Then
                    ReportAccess(LocalizationService.T("Bitte ein neues Kennwort eingeben."), True)
                    Return False
                End If

                Dim minutes = AppLockService.NormalizeIdleMinutes(_accessIdleMinutes)
                AppSettingsService.Update(
                    Sub(s)
                        If newHash IsNot Nothing Then s.AppLockPasswordHash = newHash
                        s.AppLockIdleMinutes = minutes
                    End Sub)
                ' Ein neues Kennwort beendet jede Sitzung, die mit dem alten entstand, bis auf die
                ' des Browsers, in dem es eben geaendert wurde.
                If newHash IsNot Nothing Then BrowserBridge.EndOtherSessions()
                AppLockService.NoteActivity()
                ClearAccessFields()
                ReportAccess(If(newHash Is Nothing, LocalizationService.T("Gespeichert."),
                                If(wasProtected, LocalizationService.T("Das neue Kennwort gilt ab sofort."),
                                                 LocalizationService.T("Die Anwendung ist jetzt mit einem Kennwort geschützt."))), False)
                AfterAccessChanged()
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.ApplyAccess", ex)
                ReportAccess(LocalizationService.T("Die Änderung ließ sich nicht speichern."), True)
                Return False
            End Try
        End Function

        Private Sub ClearAccessFields()
            AccessCurrentPassword = ""
            AccessNewPassword = ""
            AccessNewPasswordRepeat = ""
        End Sub

        Private Sub AfterAccessChanged()
            _accessProtectionWanted = AccessIsProtected
            _accessIdleMinutes = AppLockService.IdleMinutes
            Me.RaisePropertyChanged(NameOf(AccessProtectionWanted))
            Me.RaisePropertyChanged(NameOf(AccessIsProtected))
            Me.RaisePropertyChanged(NameOf(AccessShowsCurrentPassword))
            Me.RaisePropertyChanged(NameOf(AccessShowsNewPassword))
            Me.RaisePropertyChanged(NameOf(AccessShowsIdle))
            Me.RaisePropertyChanged(NameOf(SelectedAccessIdle))
            RaiseCanApplyAccess()
            _mainVm?.OnAppLockProtectionChanged()
        End Sub

    End Class

End Namespace
