Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Threading
Imports FerrumPix.ViewModels

Namespace Views

    Public Class BatchResizeDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            AddHandler Me.Loaded, AddressOf OnDialogLoaded
        End Sub

        Private Sub OnPresetClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            vm.SetDialogBatchResizePreset(If(button.Tag, "").ToString())
            e.Handled = True
        End Sub

        Private Sub OnDialogLoaded(sender As Object, e As RoutedEventArgs)
            Dim widthBox = Me.FindControl(Of TextBox)("BatchResizeWidthTextBox")
            If widthBox Is Nothing Then Return

            Dispatcher.UIThread.Post(
                Sub()
                    widthBox.Focus()
                    widthBox.SelectAll()
                End Sub,
                DispatcherPriority.Input)
            Dispatcher.UIThread.Post(
                Sub()
                    widthBox.Focus()
                    widthBox.SelectAll()
                End Sub,
                DispatcherPriority.Background)
        End Sub

    End Class

End Namespace
