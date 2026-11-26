Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Abschnitt „Editor" der Einstellungen, siehe <see cref="SettingsSectionView"/>.</summary>
    Public Class EditorSectionView
        Inherits SettingsSectionView

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Owner?.OnLocalizedItemAttachedToVisualTree(sender, e)
        End Sub

        ''' <summary>Ordner waehlen fuer eine Zeile der Zielordner von Speichern unter: derselbe Weg
        ''' wie bei den Zielen des Imports, er arbeitet mit jeder ImportTargetRow.</summary>
        Private Sub OnBrowseSaveAsFolderClick(sender As Object, e As Avalonia.Interactivity.RoutedEventArgs)
            Owner?.OnBrowseImportTargetClick(sender, e)
        End Sub
    End Class

End Namespace
