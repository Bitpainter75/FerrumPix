Imports System
Imports System.Linq
Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Die Dialoge "Sichern" und "Wiederherstellen". Die Logik steht in
    ''' MainWindowViewModelBackup; hier nur die Dateiwahl, je nach Richtung Speichern oder Oeffnen.</summary>
    Public Class BackupDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        ' Async Sub: eine Ausnahme dahinter faenge niemand. Deshalb das eigene Try.
        Private Async Sub OnBrowseClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = TryCast(DataContext, MainWindowViewModel)
                If vm Is Nothing Then Return
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim zipType As New FilePickerFileType(LocalizationService.T("FerrumPix-Sicherung")) With {.Patterns = {"*.zip"}}
                Dim startFolder = IO.Path.GetDirectoryName(vm.DialogBackupPath)
                Dim start = Await FolderPickerStart.ForPathAsync(topLevel, startFolder)
                If vm.DialogShowsBackup Then
                    Dim file = Await topLevel.StorageProvider.SaveFilePickerAsync(New FilePickerSaveOptions With {
                        .Title = LocalizationService.T("Sicherung speichern unter"),
                        .SuggestedFileName = IO.Path.GetFileName(vm.DialogBackupPath),
                        .DefaultExtension = "zip",
                        .FileTypeChoices = {zipType},
                        .SuggestedStartLocation = start
                    })
                    Dim path = file?.Path.LocalPath
                    If Not String.IsNullOrWhiteSpace(path) Then vm.DialogBackupPath = path
                Else
                    Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                        .Title = LocalizationService.T("Sicherung wählen"),
                        .AllowMultiple = False,
                        .FileTypeFilter = {zipType},
                        .SuggestedStartLocation = start
                    })
                    Dim path = files?.FirstOrDefault()?.Path.LocalPath
                    If Not String.IsNullOrWhiteSpace(path) Then vm.DialogBackupPath = path
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Backup.Browse", ex)
            End Try
        End Sub

    End Class

End Namespace
