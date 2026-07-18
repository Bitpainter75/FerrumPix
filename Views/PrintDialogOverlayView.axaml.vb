Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Der Druckdialog. Anders als der Collage-Dialog hängt er am MainWindowViewModel und
    ''' liegt in MainWindow.axaml, weil er aus Galerie, Betrachter UND Editor geöffnet wird.</summary>
    Public Class PrintDialogOverlayView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnPrintClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            vm?.ConfirmPrint()
            e.Handled = True
        End Sub

        Private Sub OnCancelClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            vm?.ClosePrintDialog()
            e.Handled = True
        End Sub

        Private Sub OnOrientationClick(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If btn Is Nothing OrElse vm Is Nothing Then Return
            vm.PrintLandscape = String.Equals(TryCast(btn.Tag, String), "Landscape", StringComparison.OrdinalIgnoreCase)
            e.Handled = True
        End Sub

        Private Sub OnFitModeClick(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If btn Is Nothing OrElse vm Is Nothing Then Return
            ' Der Tag trägt den Logikschlüssel ("Fit"/"Fill"), nie den angezeigten Text - der ist
            ' übersetzt und würde den Vergleich in jeder anderen Sprache brechen.
            vm.PrintFitMode = TryCast(btn.Tag, String)
            e.Handled = True
        End Sub

        Private Sub OnImagesPerPageClick(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If btn Is Nothing OrElse vm Is Nothing Then Return
            Dim value As Integer
            If Integer.TryParse(TryCast(btn.Tag, String), value) Then vm.PrintImagesPerPage = value
            e.Handled = True
        End Sub

    End Class

End Namespace
