Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Tastenkürzel" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class ShortcutsSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
