Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports FerrumPix.ViewModels

Namespace Views

    Public Class DialogOverlayView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnDialogFormatOptionClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            Dim selected = TryCast(button.DataContext, String)
            If Not String.IsNullOrEmpty(selected) Then
                vm.DialogSelectedFormat = selected
            End If
            e.Handled = True
        End Sub

    End Class

End Namespace
