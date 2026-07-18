Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports FerrumPix.Services
Imports FerrumPix.ViewModels
Imports System.Linq

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
            FlyoutHelpers.CloseContainingFlyout(button)
            e.Handled = True
        End Sub

        Private Sub OnDialogSaveTargetClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            vm.SetDialogSaveAsTarget(TryCast(button.Tag, String))
            e.Handled = True
        End Sub

        ''' Einzeloption „Katalog-Metadaten übernehmen" umschalten (Akzent = aktiv).
        Private Sub OnDialogMetaCopyToggleClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            vm.ToggleDialogSaveAsMetaOption(TryCast(button.Tag, String))
            e.Handled = True
        End Sub

        Private Async Sub OnBrowseSaveTargetFolderClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm Is Nothing Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Zielordner wählen"),
                    .AllowMultiple = False
                })
                Dim folder = folders?.FirstOrDefault()
                If folder IsNot Nothing Then
                    Dim path = folder.Path.LocalPath
                    If Not String.IsNullOrWhiteSpace(path) Then vm.DialogSaveAsTargetFolder = path
                End If
            Catch
            End Try
            e.Handled = True
        End Sub

    End Class

End Namespace
