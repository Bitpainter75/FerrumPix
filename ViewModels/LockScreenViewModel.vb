Imports System
Imports System.Globalization
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Das Anmeldefenster ueber der gesperrten Anwendung: ein Kennwortfeld, Entsperren
    ''' und die Meldung nach einem Fehlversuch. Geprueft wird ueber <see cref="AppLockService"/>,
    ''' auf einem Hintergrundfaden, weil der Hash mit Absicht langsam ist.</summary>
    Public Class LockScreenViewModel
        Inherits ReactiveObject

        Public Sub New()
            UnlockCommand = ReactiveCommand.CreateFromTask(AddressOf UnlockAsync)
        End Sub

        ''' <summary>Entsperren. Als Befehl mit Task ist der Knopf waehrend der Pruefung gesperrt.</summary>
        Public ReadOnly Property UnlockCommand As ICommand

        Private _password As String = ""
        Public Property Password As String
            Get
                Return _password
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_password, If(value, ""))
            End Set
        End Property

        Private _errorText As String = ""
        ''' <summary>Die Meldung nach einem Fehlversuch, sonst leer.</summary>
        Public Property ErrorText As String
            Get
                Return _errorText
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_errorText, If(value, ""))
                Me.RaisePropertyChanged(NameOf(HasError))
            End Set
        End Property

        Public ReadOnly Property HasError As Boolean
            Get
                Return _errorText.Length > 0
            End Get
        End Property

        Private _isChecking As Boolean
        Public Property IsChecking As Boolean
            Get
                Return _isChecking
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isChecking, value)
            End Set
        End Property

        ''' <summary>Das Feld wurde geleert und soll den Fokus zurueck. Die Ansicht hoert darauf.</summary>
        Public Event PasswordCleared As EventHandler

        ''' <summary>Prueft das Kennwort im Feld. Nach jedem Versuch ist das Feld leer; wie lang das
        ''' Kennwort ist oder wie nah man war, sagt die Meldung nicht.</summary>
        Public Async Function UnlockAsync() As Task
            If IsChecking Then Return
            Dim given = Password
            If given.Length = 0 Then
                ErrorText = LocalizationService.T("Bitte das Kennwort eingeben.")
                Return
            End If
            IsChecking = True
            Try
                Dim result = Await Task.Run(Function() AppLockService.TryUnlock(given, AppLockService.LocalKey))
                Password = ""
                Select Case result.Outcome
                    Case PasswordCheckOutcome.Accepted, PasswordCheckOutcome.NotProtected
                        ErrorText = ""
                        AppLockService.Unlock()
                    Case PasswordCheckOutcome.Throttled
                        ErrorText = ThrottledText(result.RetryAfter)
                    Case Else
                        ErrorText = If(result.RetryAfter > TimeSpan.Zero,
                                       ThrottledText(result.RetryAfter),
                                       LocalizationService.T("Das Kennwort ist falsch."))
                End Select
            Catch ex As Exception
                DiagnosticLogService.LogException("LockScreen.Unlock", ex)
            Finally
                IsChecking = False
            End Try
            RaiseEvent PasswordCleared(Me, EventArgs.Empty)
        End Function

        ''' <summary>Die Meldung bei Wartezeit, auf ganze Sekunden aufgerundet.</summary>
        Friend Shared Function ThrottledText(wait As TimeSpan) As String
            Dim seconds = CInt(Math.Ceiling(Math.Max(1.0, wait.TotalSeconds)))
            Return String.Format(LocalizationService.T("Zu viele Fehlversuche. Bitte in {0} Sekunden erneut versuchen."),
                                 seconds.ToString(CultureInfo.CurrentCulture))
        End Function

        ''' <summary>Leert Feld und Meldung, wenn das Fenster erneut erscheint.</summary>
        Public Sub Reset()
            Password = ""
            ErrorText = ""
        End Sub

    End Class

End Namespace
