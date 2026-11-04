Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Galerie" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class GallerySectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnBrowseGalleryStartupFolderClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnBrowseGalleryStartupFolderClick(sender, e)
        End Sub

        Private Sub OnBrowseImportTargetClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnBrowseImportTargetClick(sender, e)
        End Sub

        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Owner?.OnLocalizedItemAttachedToVisualTree(sender, e)
        End Sub
    End Class

End Namespace
