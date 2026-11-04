Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Kartenansicht" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class MapSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
