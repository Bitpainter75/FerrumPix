Imports System
Imports System.Windows.Input
Imports Avalonia.Threading
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ''' <summary>Das Anmeldefenster der Anwendung (AppLockService): ob gesperrt ist, das
    ''' ViewModel des Fensters und der Befehl "Sperren".</summary>
    Partial Public Class MainWindowViewModel

        Private _lockScreen As LockScreenViewModel

        ''' <summary>Das Anmeldefenster, solange gesperrt ist, sonst Nothing. Das Fenster baut seine
        ''' Ansicht beim ersten Sperren und behaelt sie danach.</summary>
        Public ReadOnly Property LockScreen As LockScreenViewModel
            Get
                Return _lockScreen
            End Get
        End Property

        Public ReadOnly Property IsAppLocked As Boolean
            Get
                Return _lockScreen IsNot Nothing
            End Get
        End Property

        ''' <summary>Der Titel des Fensters fuer Taskleiste und Fensterliste. Gesperrt nur der Name
        ''' der Anwendung: sonst stuende dort der Name des offenen Bildes.</summary>
        Public ReadOnly Property WindowTitle As String
            Get
                Return If(IsAppLocked, "FerrumPix", Title)
            End Get
        End Property

        ''' <summary>Gibt es ein Kennwort? Nur dann steht "Sperren" im Menue und wirkt die Taste.</summary>
        Public ReadOnly Property CanLockApp As Boolean
            Get
                Return AppLockService.IsProtected
            End Get
        End Property

        ''' <summary>Der Knopf "Sperren" der Fensterleiste: nur mit Kennwort, und gesperrt nicht.
        ''' Die Fensterleiste bleibt waehrend der Sperre sichtbar, bietet dann aber nur die
        ''' Fensterfunktionen an.</summary>
        Public ReadOnly Property IsWindowLockButtonVisible As Boolean
            Get
                Return CanLockApp AndAlso Not IsAppLocked
            End Get
        End Property

        ''' <summary>Wuerde das Schliessen des Fensters jetzt zurueckfragen? Im Editor bei
        ''' ungespeicherten Aenderungen, im Betrachter bei einer noch nicht gespeicherten Drehung.
        ''' Beide Rueckfragen nennen den Dateinamen; gesperrt wird deshalb nicht gefragt, sondern
        ''' das Schliessen abgelehnt (MainWindow.RefuseCloseWhileLocked).</summary>
        Friend ReadOnly Property ClosingNeedsQuestion As Boolean
            Get
                Select Case CurrentMode
                    Case AppMode.Editor : Return Editor IsNot Nothing AndAlso Editor.HasUnsavedChanges
                    Case AppMode.Viewer : Return Viewer IsNot Nothing AndAlso Viewer.HasPendingRotationQuestion
                    Case Else : Return False
                End Select
            End Get
        End Property

        Private _lockAppCommand As ICommand
        Public ReadOnly Property LockAppCommand As ICommand
            Get
                If _lockAppCommand Is Nothing Then _lockAppCommand = New DelegateCommand(Sub() LockApp())
                Return _lockAppCommand
            End Get
        End Property

        Private Sub InitializeAppLock()
            AddHandler AppLockService.LockStateChanged, AddressOf OnAppLockStateChanged
            ' Die Ersteinrichtung im Browser setzt das Kennwort von einem Hintergrundfaden aus.
            AddHandler AppLockService.ProtectionChanged,
                Sub(s, e) Dispatcher.UIThread.Post(AddressOf OnAppLockProtectionChanged)
            SyncLockScreen()
        End Sub

        ''' <summary>Sperrt die Anwendung (Menue, STRG+L). Ohne Kennwort geschieht nichts.</summary>
        Public Sub LockApp()
            AppLockService.Lock()
        End Sub

        ''' <summary>Der Schutz wurde in den Einstellungen ein- oder ausgeschaltet.</summary>
        Friend Sub OnAppLockProtectionChanged()
            Me.RaisePropertyChanged(NameOf(CanLockApp))
            Me.RaisePropertyChanged(NameOf(IsWindowLockButtonVisible))
        End Sub

        Private Sub OnAppLockStateChanged(sender As Object, e As EventArgs)
            ' Die Anmeldung im Browser entsperrt von einem Hintergrundfaden aus.
            If Dispatcher.UIThread.CheckAccess() Then
                SyncLockScreen()
            Else
                Dispatcher.UIThread.Post(AddressOf SyncLockScreen)
            End If
        End Sub

        Private Sub SyncLockScreen()
            Dim locked = AppLockService.IsLocked
            If locked = IsAppLocked Then Return
            If locked Then
                _lockScreen = New LockScreenViewModel()
                ' Ein laufendes Video spielte hinter der Sperre mit Ton weiter.
                Try
                    If Viewer IsNot Nothing AndAlso Viewer.IsVideoPlaying AndAlso
                       Viewer.PlayPauseVideoCommand IsNot Nothing AndAlso
                       Viewer.PlayPauseVideoCommand.CanExecute(Nothing) Then
                        Viewer.PlayPauseVideoCommand.Execute(Nothing)
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("MainWindowViewModel.LockPauseVideo", ex)
                End Try
            Else
                _lockScreen = Nothing
                ' Im Browser nach dem Entsperren noch einmal nach einer neueren Fassung fragen: die
                ' Anwendung laeuft dort oft wochenlang, die Abfrage beim Start ist dann alt. Einmal
                ' je Entsperren; weitere Browser, die sich anmelden, waehrend entsperrt ist,
                ' kommen hier nicht vorbei (der Zustand aendert sich nicht).
                If BrowserBridge.IsActive Then Settings?.RepeatUpdateCheck()
            End If
            Me.RaisePropertyChanged(NameOf(LockScreen))
            Me.RaisePropertyChanged(NameOf(IsAppLocked))
            Me.RaisePropertyChanged(NameOf(CanLockApp))
            Me.RaisePropertyChanged(NameOf(WindowTitle))
            ' Die Fensterleiste bleibt gesperrt sichtbar: ihr Titel, der Hinweis und "Sperren"
            ' folgen dem Zustand, die Maus erreicht sie auch mit einem Dialog darunter.
            Me.RaisePropertyChanged(NameOf(IsWindowLockButtonVisible))
            Me.RaisePropertyChanged(NameOf(IsUpdateNoticeVisible))
            Me.RaisePropertyChanged(NameOf(IsAppContentHitTestVisible))
            RefreshWindowTitle()
        End Sub

    End Class

End Namespace
