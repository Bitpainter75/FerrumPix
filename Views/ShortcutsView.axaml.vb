Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Views

    ''' <summary>Die Liste der Tasten- und Mausbelegung, geteilt zwischen dem Abschnitt
    ''' „Tastenkürzel" der Einstellungen und dem Dialog hinter F1. Reine Anzeige ohne Bindungen.</summary>
    Public Class ShortcutsView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

    End Class

End Namespace
