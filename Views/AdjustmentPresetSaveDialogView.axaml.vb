Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Views

    ''' <summary>Der Dialog "Anpassung speichern": Name und Auswahl der Regler, die die Vorlage
    ''' enthaelt (AdjustmentPresetSaveDialogViewModel).</summary>
    Public Class AdjustmentPresetSaveDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        ''' <summary>Die Zeilen der Gruppen entstehen erst aus ihrem Template, nach dem einmaligen
        ''' Durchlauf der Uebersetzung; der Kurzhinweis am Pfeil bliebe sonst in der
        ''' Ausgangssprache.</summary>
        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Dim itemRoot = TryCast(sender, Avalonia.Visual)
            If itemRoot IsNot Nothing Then FerrumPix.Services.LocalizationService.ApplyToMaterialized(itemRoot)
        End Sub

        ''' <summary>"Durchsuchen" bei XMP und LUT: der Dateidialog im Ordner der vorgeschlagenen
        ''' Datei. Er fragt selbst nach einer vorhandenen Datei; das Speichern fragt dann nicht noch
        ''' einmal. Async Sub: eine Ausnahme dahinter faenge niemand, deshalb das eigene Try.</summary>
        Private Async Sub OnBrowseTargetFileClick(sender As Object, e As Avalonia.Interactivity.RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = TryCast(DataContext, FerrumPix.ViewModels.AdjustmentPresetSaveDialogViewModel)
                If vm Is Nothing Then Return
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim isLut = vm.Target = FerrumPix.ViewModels.AdjustmentSaveTarget.Lut
                Dim extension = If(isLut, "cube", "xmp")
                Dim fileType As New Avalonia.Platform.Storage.FilePickerFileType(
                    If(isLut, FerrumPix.Services.LocalizationService.T("LUT (.cube)"),
                              FerrumPix.Services.LocalizationService.T("XMP-Preset (.xmp)"))) With {.Patterns = {"*." & extension}}
                Dim file = Await FolderRootPickers.SaveFilePickerAsync(topLevel,New Avalonia.Platform.Storage.FilePickerSaveOptions With {
                    .Title = FerrumPix.Services.LocalizationService.T("Anpassung speichern als"),
                    .SuggestedFileName = IO.Path.GetFileName(vm.TargetFilePath),
                    .DefaultExtension = extension,
                    .FileTypeChoices = {fileType},
                    .SuggestedStartLocation = Await FolderPickerStart.ForPathAsync(topLevel, vm.TargetFolder)
                })
                Dim path = file?.Path.LocalPath
                If Not String.IsNullOrWhiteSpace(path) Then vm.ChooseFile(path)
            Catch ex As Exception
                FerrumPix.Services.DiagnosticLogService.LogException("AdjustmentPresetSave.Browse", ex)
            End Try
        End Sub

    End Class

End Namespace
