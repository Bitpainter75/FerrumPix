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

    End Class

End Namespace
