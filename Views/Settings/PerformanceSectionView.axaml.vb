Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Leistung" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class PerformanceSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
