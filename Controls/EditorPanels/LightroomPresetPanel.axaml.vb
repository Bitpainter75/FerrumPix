Imports System.Collections.Generic
Imports System.Linq
Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Controls.EditorPanels

    Public Class LightroomPresetPanel
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Public Async Sub OnLoadLightroomPresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                    .Title = "Lightroom-Preset laden",
                    .AllowMultiple = False,
                    .FileTypeFilter = New List(Of FilePickerFileType) From {
                        New FilePickerFileType("Lightroom XMP") With {
                            .Patterns = New String() {"*.xmp"}
                        }
                    }
                })
                Dim file = files?.FirstOrDefault()
                If file Is Nothing Then Return
                vm.SaveLightroomPresetToSettings(file.Path.LocalPath)
                vm.ApplyLightroomPreset(file.Path.LocalPath)
            Catch
            End Try
        End Sub

        Public Sub OnApplySavedLightroomPresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim preset = TryCast(TryCast(sender, Control)?.DataContext, LightroomPresetSettings)
            If vm Is Nothing OrElse preset Is Nothing Then Return
            vm.ApplyLightroomPreset(preset.Path)
        End Sub

        Public Sub OnRemoveSavedLightroomPresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim preset = TryCast(TryCast(sender, Control)?.DataContext, LightroomPresetSettings)
            If vm Is Nothing OrElse preset Is Nothing Then Return
            vm.RemoveLightroomPresetFromSettings(preset.Path)
            e.Handled = True
        End Sub
    End Class

End Namespace
