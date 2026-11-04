Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „RAW-Entwicklung" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class RawSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
