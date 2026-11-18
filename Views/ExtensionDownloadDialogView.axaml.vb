Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Views

    ''' <summary>Der Dialog "Erweiterungen herunterladen". Die Logik steht in
    ''' MainWindowViewModelExtensionDownload.</summary>
    Public Class ExtensionDownloadDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        ''' <summary>Die Zeilen entstehen erst mit dem Dialog, nach dem Durchlauf der Uebersetzung
        ''' ueber das Fenster; jede uebersetzt deshalb ihren eigenen Teilbaum.</summary>
        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Dim itemRoot = TryCast(sender, Avalonia.Visual)
            If itemRoot IsNot Nothing Then FerrumPix.Services.LocalizationService.ApplyToMaterialized(itemRoot)
        End Sub

    End Class

End Namespace
