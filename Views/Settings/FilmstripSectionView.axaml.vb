Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Filmstreifen" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class FilmstripSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
