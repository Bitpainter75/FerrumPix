Imports System
Imports System.Linq
Imports System.Threading.Tasks
Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Der Dialog "Importieren": Quelle, Aufnahmetage, Ziel und Sicherung.
    ''' Die Logik steht in MainWindowViewModelImport; hier nur die drei Ordnerwahlen und das Neusuchen
    ''' der Datentraeger.</summary>
    Public Class ImportDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnRefreshSourcesClick(sender As Object, e As RoutedEventArgs)
            TryCast(DataContext, MainWindowViewModel)?.RefreshDialogImportSources()
            e.Handled = True
        End Sub

        ' Async Sub: eine Ausnahme dahinter faenge niemand, sie beendete die Anwendung. Deshalb hat
        ' jeder der drei Wege sein eigenes Try, auch wenn PickFolderAsync selbst schon faengt.
        Private Async Sub OnBrowseSourceClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = TryCast(DataContext, MainWindowViewModel)
                If vm Is Nothing Then Return
                Dim path = Await PickFolderAsync(LocalizationService.T("Quelle wählen"), vm.DialogImportSourceFolder)
                If path IsNot Nothing Then vm.DialogImportSourceFolder = path
            Catch ex As Exception
                DiagnosticLogService.LogException("Import.BrowseSource", ex)
            End Try
        End Sub

        Private Async Sub OnBrowseTargetClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = TryCast(DataContext, MainWindowViewModel)
                If vm Is Nothing Then Return
                Dim path = Await PickFolderAsync(LocalizationService.T("Zielordner wählen"), vm.DialogImportTargetFolder)
                If path IsNot Nothing Then vm.DialogImportTargetFolder = path
            Catch ex As Exception
                DiagnosticLogService.LogException("Import.BrowseTarget", ex)
            End Try
        End Sub

        Private Async Sub OnBrowseBackupClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = TryCast(DataContext, MainWindowViewModel)
                If vm Is Nothing Then Return
                Dim path = Await PickFolderAsync(LocalizationService.T("Zielordner wählen"), vm.DialogImportBackupFolder)
                If path IsNot Nothing Then vm.DialogImportBackupFolder = path
            Catch ex As Exception
                DiagnosticLogService.LogException("Import.BrowseBackup", ex)
            End Try
        End Sub

        ''' <summary>Der Ordnerdialog des Systems; Nothing, wenn nichts gewaehlt wurde.</summary>
        Private Async Function PickFolderAsync(title As String, startPath As String) As Task(Of String)
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return Nothing
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = title,
                    .AllowMultiple = False,
                    .SuggestedStartLocation = Await FolderPickerStart.ForPathAsync(topLevel, startPath)
                })
                Dim folder = folders?.FirstOrDefault()
                If folder Is Nothing Then Return Nothing
                Dim path = folder.Path.LocalPath
                Return If(String.IsNullOrWhiteSpace(path), Nothing, path)
            Catch
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
