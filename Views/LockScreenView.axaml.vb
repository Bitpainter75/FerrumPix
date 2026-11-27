Imports System
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Threading
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Das Anmeldefenster ueber der gesperrten Anwendung. Die Regeln (was gesperrt ist,
    ''' welche Tasten durchkommen) stehen in MainWindow; hier nur Feld, Knopf und Fokus.</summary>
    Public Class LockScreenView
        Inherits UserControl

        Private _viewModel As LockScreenViewModel

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            AddHandler DataContextChanged, AddressOf OnDataContextChangedInternal
            ' Beim ersten Einhaengen steht das Feld erst jetzt im Baum.
            AddHandler AttachedToVisualTree, Sub(s, e) FocusPassword()
            ' Im Tunnel: das Textfeld koennte ENTER sonst selbst verbrauchen.
            Me.FindControl(Of TextBox)("PasswordBox")?.AddHandler(InputElement.KeyDownEvent,
                AddressOf OnPasswordKeyDown, RoutingStrategies.Tunnel)
        End Sub

        Private Sub OnDataContextChangedInternal(sender As Object, e As EventArgs)
            If _viewModel IsNot Nothing Then RemoveHandler _viewModel.PasswordCleared, AddressOf OnPasswordCleared
            _viewModel = TryCast(DataContext, LockScreenViewModel)
            If _viewModel IsNot Nothing Then AddHandler _viewModel.PasswordCleared, AddressOf OnPasswordCleared
        End Sub

        ''' <summary>Holt den Fokus ins Kennwortfeld. Das Fenster ruft das bei jedem Sperren.</summary>
        Public Sub FocusPassword()
            ' Sofort, wenn das Feld schon angehaengt und sichtbar ist; sonst ginge der Fokus ins
            ' Leere (Avalonia nimmt ihn einem unsichtbaren Element gleich wieder). Dazu noch einmal
            ' nach dem Vermessen und nach allem, was mit Hintergrundvorrang schon wartet: eine
            ' Ansicht holt sich nach dem Betreten so den Fokus (OnModeEntered). Was danach noch
            ' zugreift, faengt MainWindow.OnFocusWhileLocked ab.
            TryFocusPassword()
            Dispatcher.UIThread.Post(AddressOf TryFocusPassword, DispatcherPriority.Loaded)
            Dispatcher.UIThread.Post(AddressOf TryFocusPassword, DispatcherPriority.Background)
        End Sub

        Private Sub TryFocusPassword()
            Dim box = Me.FindControl(Of TextBox)("PasswordBox")
            If box Is Nothing OrElse TopLevel.GetTopLevel(box) Is Nothing OrElse Not box.IsEffectivelyVisible Then Return
            If Not box.IsFocused Then box.Focus()
        End Sub

        Private Sub OnPasswordCleared(sender As Object, e As EventArgs)
            FocusPassword()
        End Sub

        Private Sub OnPasswordKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter AndAlso e.Key <> Key.Return Then Return
            e.Handled = True
            Dim command = _viewModel?.UnlockCommand
            If command IsNot Nothing AndAlso command.CanExecute(Nothing) Then command.Execute(Nothing)
        End Sub

        ''' <summary>Ein Druck auf die Karte gehoert ihr und verschiebt nichts.</summary>
        Private Sub OnCardPointerPressed(sender As Object, e As PointerPressedEventArgs)
            e.Handled = True
        End Sub

        ''' <summary>Ein Druck auf den freien Grund verschiebt das Fenster, wie die Fensterleiste
        ''' darueber. Im Browser gehoert das Fenster dem Tab und bleibt stehen.</summary>
        Private Sub OnBackgroundPointerPressed(sender As Object, e As PointerPressedEventArgs)
            e.Handled = True
            If BrowserBridge.IsActive Then Return
            If Not e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then Return
            Dim window = TryCast(TopLevel.GetTopLevel(Me), Window)
            If window Is Nothing OrElse window.WindowState = WindowState.FullScreen Then Return
            Try
                window.BeginMoveDrag(e)
            Catch ex As Exception
                DiagnosticLogService.LogException("LockScreen.MoveWindow", ex)
            End Try
        End Sub

    End Class

End Namespace
