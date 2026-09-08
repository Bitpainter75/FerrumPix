Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Controls.EditorPanels

    ''' <summary>Werkzeugpanel „Maske": Pinsel und die beiden gerechneten Verläufe. Reine
    ''' Bindungs-View - die Geometrie der Verläufe liegt im EditorViewModel.</summary>
    Public Class MaskPanel
        Inherits UserControl

        ''' <summary>Inhalte aus einem DataTemplate haengen nicht im logischen Baum und entstehen
        ''' oft erst NACH dem Sprachdurchlauf ueber das Fenster. Jedes neu materialisierte Element
        ''' uebersetzt deshalb seinen eigenen Teilbaum.</summary>
        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Dim itemRoot = TryCast(sender, Avalonia.Visual)
            If itemRoot IsNot Nothing Then FerrumPix.Services.LocalizationService.ApplyToVisualTree(itemRoot)
        End Sub

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
