Imports System.Collections.Generic
Imports System.Linq
Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Controls.EditorPanels

    Public Class LutPresetPanel
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Public Async Sub OnLoadLutPresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                    .Title = "LUT laden",
                    .AllowMultiple = False,
                    .FileTypeFilter = New List(Of FilePickerFileType) From {
                        New FilePickerFileType("3D-LUT (.cube)") With {
                            .Patterns = New String() {"*.cube"}
                        }
                    }
                })
                Dim file = files?.FirstOrDefault()
                If file Is Nothing Then Return
                vm.SaveLutPresetToSettings(file.Path.LocalPath)
                vm.ApplyLutPreset(file.Path.LocalPath)
            Catch
            End Try
        End Sub

        Public Async Sub OnLoadLutFolderClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = "Ordner mit LUTs wählen",
                    .AllowMultiple = False
                })
                Dim folder = folders?.FirstOrDefault()
                If folder Is Nothing Then Return
                vm.ImportLutPresetsFromFolder(folder.Path.LocalPath)
            Catch
            End Try
        End Sub

        Public Sub OnApplySavedLutPresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim preset = TryCast(TryCast(sender, Control)?.DataContext, LutPresetSettings)
            If vm Is Nothing OrElse preset Is Nothing Then Return
            vm.ApplyLutPreset(preset.Path)
        End Sub

        Public Sub OnRemoveSavedLutPresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim preset = TryCast(TryCast(sender, Control)?.DataContext, LutPresetSettings)
            If vm Is Nothing OrElse preset Is Nothing Then Return
            vm.RemoveLutPresetFromSettings(preset.Path)
            e.Handled = True
        End Sub
    End Class

End Namespace
