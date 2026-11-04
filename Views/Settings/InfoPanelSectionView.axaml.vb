Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Info-Leiste" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class InfoPanelSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
