Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Katalog" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class CatalogSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnAddCatalogWatchFolderClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnAddCatalogWatchFolderClick(sender, e)
        End Sub

        Private Sub OnBrowseForeignCatalogClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnBrowseForeignCatalogClick(sender, e)
        End Sub

        Private Sub OnRelocateCatalogFolderClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnRelocateCatalogFolderClick(sender, e)
        End Sub

        Private Sub OnLocalizedMenuFlyoutOpened(sender As Object, e As System.EventArgs)
            Owner?.OnLocalizedMenuFlyoutOpened(sender, e)
        End Sub

        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Owner?.OnLocalizedItemAttachedToVisualTree(sender, e)
        End Sub
    End Class

End Namespace
