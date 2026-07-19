Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Der Dialog „Neues Bild". Er hängt am EditorViewModel und liegt in EditorView.axaml,
    ''' weil er - anders als der Druckdialog - nur aus dem Editor heraus erreichbar ist. Vorbild ist
    ''' damit der Collage-Dialog am GalleryViewModel, nicht der Druckdialog am Fenster.</summary>
    Public Class NewDocumentDialogOverlayView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnCreateClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            vm?.ConfirmNewDocumentCommand.Execute(Nothing)
            e.Handled = True
        End Sub

        Private Sub OnCancelClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            vm?.CloseNewDocumentDialog()
            e.Handled = True
        End Sub

        ''' Alle Tags tragen den LOGIKSCHLÜSSEL, nie den angezeigten Text - der ist übersetzt und
        ''' würde den Vergleich in jeder anderen Sprache brechen (siehe PrintDialogOverlayView).
        Private Sub OnPresetClick(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If btn Is Nothing OrElse vm Is Nothing Then Return
            vm.ApplyNewDocPreset(TryCast(btn.Tag, String))
            e.Handled = True
        End Sub

        Private Sub OnOrientationClick(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If btn Is Nothing OrElse vm Is Nothing Then Return
            vm.NewDocIsLandscape = String.Equals(TryCast(btn.Tag, String), "Landscape", StringComparison.Ordinal)
            e.Handled = True
        End Sub

        Private Sub OnUnitClick(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If btn Is Nothing OrElse vm Is Nothing Then Return
            vm.NewDocUnit = TryCast(btn.Tag, String)
            e.Handled = True
        End Sub

        Private Sub OnBackgroundClick(sender As Object, e As RoutedEventArgs)
            Dim btn = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If btn Is Nothing OrElse vm Is Nothing Then Return
            vm.NewDocBackgroundMode = TryCast(btn.Tag, String)
            e.Handled = True
        End Sub

    End Class

End Namespace
