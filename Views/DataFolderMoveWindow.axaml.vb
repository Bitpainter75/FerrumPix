Imports System
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Markup.Xaml
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Das Fenster beim Umzug des Datenordners. Die Regeln (wann es erscheint, wann der
    ''' Start weitergeht) stehen in StartupMoveCoordinator; hier nur, dass es sich nicht schliessen
    ''' laesst, solange der Umzug laeuft.</summary>
    Public Class DataFolderMoveWindow
        Inherits Window

        ''' <summary>Laeuft, wenn das Fenster nach dem Ende des Umzugs zugegangen ist.</summary>
        Public Event ClosedAfterMove As EventHandler

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            LocalizationService.ApplyTo(Me)
            App.ApplyIcon(Me)
            AddHandler Closing, AddressOf OnWindowClosing
            AddHandler Closed, Sub(s, e) RaiseEvent ClosedAfterMove(Me, EventArgs.Empty)
        End Sub

        ''' <summary>Kreuz, ALT+F4 und CMD+Q gehen hier durch. Solange der Umzug laeuft, bleibt
        ''' das Fenster stehen; auch ein Beenden des Systems wird abgewiesen, damit der Umzug nicht
        ''' mitten in einer Datei endet (er setzte zwar beim naechsten Start fort, aber dann
        ''' wieder hinter diesem Fenster).</summary>
        Private Sub OnWindowClosing(sender As Object, e As WindowClosingEventArgs)
            Dim vm = TryCast(DataContext, DataFolderMoveViewModel)
            If vm IsNot Nothing AndAlso Not vm.CanCloseWindow() Then e.Cancel = True
        End Sub

        Private Sub OnBackgroundPointerPressed(sender As Object, e As PointerPressedEventArgs)
            If e.GetCurrentPoint(Me).Properties.IsLeftButtonPressed Then
                Try
                    BeginMoveDrag(e)
                Catch
                End Try
            End If
        End Sub

    End Class

End Namespace
