Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Personen" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class PeopleSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnManagePeopleClick(sender As Object, e As RoutedEventArgs)
            Owner?.OnManagePeopleClick(sender, e)
        End Sub
    End Class

End Namespace
