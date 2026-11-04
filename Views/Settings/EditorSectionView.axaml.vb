Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Editor" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class EditorSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Owner?.OnLocalizedItemAttachedToVisualTree(sender, e)
        End Sub
    End Class

End Namespace
