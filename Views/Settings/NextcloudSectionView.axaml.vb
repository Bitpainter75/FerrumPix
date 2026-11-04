Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Nextcloud" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class NextcloudSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
