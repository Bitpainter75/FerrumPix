Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Erweiterungen" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class ExtensionsSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnInstallExtensionClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnInstallExtensionClick(sender, e)
        End Sub

        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Owner?.OnLocalizedItemAttachedToVisualTree(sender, e)
        End Sub
    End Class

End Namespace
