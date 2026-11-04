Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Technologie" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class TechnologySectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnLicenseLinkClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnLicenseLinkClick(sender, e)
        End Sub
    End Class

End Namespace
