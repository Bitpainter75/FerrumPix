Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Views.Settings

    ''' <summary>Abschnitt „Benutzer" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class UsersSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnAddUserRuleClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnAddUserRuleClick(sender, e)
        End Sub

        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Owner?.OnLocalizedItemAttachedToVisualTree(sender, e)
        End Sub

        ''' <summary>Die Wiederherstellungscodes als Textdatei: im Browser als Download an das
        ''' Geraet, am Rechner an einen gewaehlten Ort. Async Sub: das eigene Try faengt alles.</summary>
        Private Async Sub OnSaveRecoveryCodesClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Try
                Dim vm = TryCast(DataContext, SettingsViewModel)
                If vm Is Nothing OrElse Not vm.HasOwnTwoFactorRecoveryCodes Then Return
                If vm.OfferOwnRecoveryCodesDownload() Then Return
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim textType As New FilePickerFileType(LocalizationService.T("Textdatei")) With {.Patterns = {"*.txt"}}
                Dim file = Await FolderRootPickers.SaveFilePickerAsync(topLevel, New FilePickerSaveOptions With {
                    .Title = LocalizationService.T("Als Textdatei speichern"),
                    .SuggestedFileName = SettingsViewModel.RecoveryCodesFileName,
                    .DefaultExtension = "txt",
                    .FileTypeChoices = {textType}
                })
                Dim path = file?.Path.LocalPath
                If Not String.IsNullOrWhiteSpace(path) Then vm.SaveOwnRecoveryCodes(path)
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.TwoFactor.SaveCodes", ex)
            End Try
        End Sub
    End Class

End Namespace
