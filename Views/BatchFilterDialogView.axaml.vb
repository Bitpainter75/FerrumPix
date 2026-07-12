Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports FerrumPix.Services
Imports FerrumPix.ViewModels
Imports System.Linq

Namespace Views

    Public Class BatchFilterDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnFilterSourceClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            vm.SetDialogFilterSourceKind(TryCast(button.Tag, String))
            e.Handled = True
        End Sub

        ''' Lädt eine Preset-Datei außerhalb der im Editor gespeicherten Vorgaben. Der Filter-Typ hat
        ''' keinen Datei-Button (siehe IsDialogFilterFileVisible) - die eingebauten Filter sind fest.
        Private Async Sub OnBrowseFilterFileClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm Is Nothing Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return

                Dim isLut = vm.IsDialogFilterSourceLut
                Dim fileType = New FilePickerFileType(If(isLut, "LUT (*.cube)", "Lightroom-Preset (*.xmp)")) With {
                    .Patterns = If(isLut, New String() {"*.cube"}, New String() {"*.xmp"})
                }
                Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                    .Title = LocalizationService.T(If(isLut, "LUT wählen", "Lightroom-Preset wählen")),
                    .AllowMultiple = False,
                    .FileTypeFilter = New List(Of FilePickerFileType) From {fileType}
                })
                Dim file = files?.FirstOrDefault()
                If file IsNot Nothing Then vm.AddDialogFilterFileChoice(file.Path.LocalPath)
            Catch
            End Try
            e.Handled = True
        End Sub

    End Class

End Namespace
