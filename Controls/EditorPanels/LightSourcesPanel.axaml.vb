Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Controls.EditorPanels

    Public Class LightSourcesPanel
        Inherits UserControl

        ''' <summary>Die Zeilen der Lichterliste entstehen erst NACH dem Sprachdurchlauf ueber das
        ''' Fenster; jede uebersetzt deshalb ihren eigenen Teilbaum (die Tooltips der Knoepfe), siehe
        ''' <see cref="LocalizationService.ApplyToMaterialized"/>.</summary>
        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Dim itemRoot = TryCast(sender, Avalonia.Visual)
            If itemRoot IsNot Nothing Then FerrumPix.Services.LocalizationService.ApplyToMaterialized(itemRoot)
        End Sub

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
