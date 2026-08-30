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

        Public Async Sub OnLoadXmpPresetClick(sender As Object, e As RoutedEventArgs)
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
                vm.SaveXmpPresetToSettings(file.Path.LocalPath)
                vm.ApplyXmpPreset(file.Path.LocalPath)
            Catch
            End Try
        End Sub

        Public Async Sub OnLoadXmpFolderClick(sender As Object, e As RoutedEventArgs)
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
                vm.ImportXmpPresetsFromFolder(folder.Path.LocalPath)
            Catch
            End Try
        End Sub

        ''' Über ApplySavedXmpPresetAsync statt direkt über ApplyXmpPreset: nur hier steht ein
        ''' LISTENEINTRAG hinter dem Klick, und nur hier lässt sich deshalb anbieten, ihn zu entfernen,
        ''' wenn die Datei nicht mehr da ist.
        Public Async Sub OnApplySavedXmpPresetClick(sender As Object, e As RoutedEventArgs)
            Try
                Dim vm = TryCast(DataContext, EditorViewModel)
                Dim preset = TryCast(TryCast(sender, Control)?.DataContext, XmpPresetSettings)
                If vm Is Nothing OrElse preset Is Nothing Then Return
                Await vm.ApplySavedXmpPresetAsync(preset.Path)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("XmpPresetPanel.OnApplySavedXmpPresetClick", ex)
            End Try
        End Sub

        Public Async Sub OnRemoveSavedXmpPresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            Dim preset = TryCast(TryCast(sender, Control)?.DataContext, XmpPresetSettings)
            If vm Is Nothing OrElse preset Is Nothing Then Return
            e.Handled = True
            Await vm.ConfirmRemoveSavedPresetAsync(preset.Path, isLut:=False)
        End Sub
    End Class

End Namespace
