Imports System.Collections.Generic
Imports System.Linq
Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Controls.EditorPanels

    Public Class XmpPresetPanel
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
                    .Title = LocalizationService.T("XMP-Preset laden"),
                    .AllowMultiple = False,
                    .FileTypeFilter = New List(Of FilePickerFileType) From {
                        New FilePickerFileType("XMP-Preset") With {
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

        Public Async Sub OnLoadLightroomFolderClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Ordner mit XMP-Presets wählen"),
                    .AllowMultiple = False
                })
                Dim folder = folders?.FirstOrDefault()
                If folder Is Nothing Then Return
                vm.ImportLightroomPresetsFromFolder(folder.Path.LocalPath)
            Catch
            End Try
        End Sub

        ''' Über ApplySavedLightroomPresetAsync statt direkt über ApplyLightroomPreset: nur hier steht ein
        ''' LISTENEINTRAG hinter dem Klick, und nur hier lässt sich deshalb anbieten, ihn zu entfernen,
        ''' wenn die Datei nicht mehr da ist.
        Public Async Sub OnApplySavedLightroomPresetClick(sender As Object, e As RoutedEventArgs)
            Try
                Dim vm = TryCast(DataContext, EditorViewModel)
                Dim preset = TryCast(TryCast(sender, Control)?.DataContext, LightroomPresetSettings)
                If vm Is Nothing OrElse preset Is Nothing Then Return
                Await vm.ApplySavedLightroomPresetAsync(preset.Path)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("XmpPresetPanel.OnApplySavedLightroomPresetClick", ex)
            End Try
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
