Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Das Entrauschen-Formular der Stapeldialoge, siehe DenoiseFormView.axaml.</summary>
    Public Class DenoiseFormView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnDenoiseModeClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            vm.SetDialogBatchDenoiseMode(TryCast(button.Tag, String))
            e.Handled = True
        End Sub

        Private Sub OnDenoiseModelClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            vm.SetDialogBatchDenoiseModel(TryCast(button.Tag, String))
            e.Handled = True
        End Sub

    End Class

End Namespace
