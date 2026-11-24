Imports System.Collections.Generic
Imports System.Linq
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Das Fenster "Anpassungen verwalten" (AdjustmentPresetManagerViewModel). Die Ansicht
    ''' oeffnet nur die Dateiauswahl fuer Import und Export und reicht den Pfad an das ViewModel;
    ''' alles andere sind dessen Befehle.</summary>
    Public Class AdjustmentPresetManagerView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Shared Function PresetFileType() As FilePickerFileType
            Return New FilePickerFileType(LocalizationService.T("Anpassung") & " (*" & AdjustmentPresetStore.FileExtension & ")") With {
                .Patterns = New String() {"*" & AdjustmentPresetStore.FileExtension}
            }
        End Function

        ''' EINGABE fuehrt die vorgemerkte Aktion aus, ESC verwirft sie; beides bleibt im Feld,
        ''' sonst schloesse ESC das ganze Fenster.
        Private Sub OnPendingKeyDown(sender As Object, e As KeyEventArgs)
            Dim vm = TryCast(DataContext, AdjustmentPresetManagerViewModel)
            If vm Is Nothing Then Return
            If e.Key = Key.Enter Then
                e.Handled = True
                vm.ConfirmPendingCommand.Execute(Nothing)
            ElseIf e.Key = Key.Escape Then
                e.Handled = True
                vm.CancelPending()
            End If
        End Sub

        ' Async Sub: eine Ausnahme dahinter faenge niemand. Deshalb je ein eigenes Try.
        Private Async Sub OnImportFileClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = TryCast(DataContext, AdjustmentPresetManagerViewModel)
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If vm Is Nothing OrElse topLevel Is Nothing Then Return
                Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                    .Title = LocalizationService.T("Anpassungen importieren"),
                    .AllowMultiple = True,
                    .FileTypeFilter = New List(Of FilePickerFileType) From {PresetFileType()}
                })
                If files Is Nothing Then Return
                For Each file In files
                    Await vm.ImportAsync(file.Path.LocalPath)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.ImportFile", ex)
            End Try
        End Sub

        Private Async Sub OnImportFolderClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = TryCast(DataContext, AdjustmentPresetManagerViewModel)
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If vm Is Nothing OrElse topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Ordner mit Anpassungen importieren"),
                    .AllowMultiple = False
                })
                Dim folder = folders?.FirstOrDefault()
                If folder IsNot Nothing Then Await vm.ImportAsync(folder.Path.LocalPath)
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.ImportFolder", ex)
            End Try
        End Sub

        Private Async Sub OnExportPresetClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = TryCast(DataContext, AdjustmentPresetManagerViewModel)
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If vm Is Nothing OrElse topLevel Is Nothing OrElse Not vm.HasSelectedPreset Then Return
                Dim file = Await topLevel.StorageProvider.SaveFilePickerAsync(New FilePickerSaveOptions With {
                    .Title = LocalizationService.T("Anpassung exportieren"),
                    .SuggestedFileName = vm.SuggestedExportFileName,
                    .DefaultExtension = AdjustmentPresetStore.FileExtension.TrimStart("."c),
                    .FileTypeChoices = New List(Of FilePickerFileType) From {PresetFileType()}
                })
                Dim path = file?.Path.LocalPath
                If Not String.IsNullOrWhiteSpace(path) Then Await vm.ExportSelectedPresetAsync(path)
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.ExportPreset", ex)
            End Try
        End Sub

        Private Async Sub OnExportFolderClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = TryCast(DataContext, AdjustmentPresetManagerViewModel)
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If vm Is Nothing OrElse topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Zielordner wählen"),
                    .AllowMultiple = False
                })
                Dim folder = folders?.FirstOrDefault()
                If folder IsNot Nothing Then Await vm.ExportSelectedFolderAsync(folder.Path.LocalPath)
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresets.ExportFolder", ex)
            End Try
        End Sub

    End Class

End Namespace
