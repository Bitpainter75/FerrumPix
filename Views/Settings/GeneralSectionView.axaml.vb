Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Allgemein" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class GeneralSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnHomepageClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnHomepageClick(sender, e)
        End Sub

        Private Sub OnGitHubClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnGitHubClick(sender, e)
        End Sub

        Private Sub OnReleasePageClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnReleasePageClick(sender, e)
        End Sub

        Private Sub OnLicenseLinkClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnLicenseLinkClick(sender, e)
        End Sub
    End Class

End Namespace
