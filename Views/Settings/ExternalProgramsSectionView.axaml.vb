Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Externe Programme" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class ExternalProgramsSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnBrowseGmicClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnBrowseGmicClick(sender, e)
        End Sub

        Private Sub OnBrowseOpenWithProgramClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnBrowseOpenWithProgramClick(sender, e)
        End Sub

        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Owner?.OnLocalizedItemAttachedToVisualTree(sender, e)
        End Sub
    End Class

End Namespace
