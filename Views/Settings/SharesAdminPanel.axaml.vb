Imports Avalonia.Controls
Imports Avalonia.LogicalTree
Imports Avalonia.Markup.Xaml

Namespace Views.Settings

    ''' <summary>Der Bereich „Freigaben" der Einstellungen (SettingsViewModelShares.vb): alle Links
    ''' zum Teilen, Widerrufen, Pausieren. Kein eigener Abschnitt, sondern ein Baustein, den der
    ''' Abschnitt der Benutzerverwaltung einhaengt; die Bindungen gelten dem SettingsViewModel des
    ''' umgebenden Abschnitts.</summary>
    Public Class SharesAdminPanel
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Me.FindLogicalAncestorOfType(Of SettingsView)()?.OnLocalizedItemAttachedToVisualTree(sender, e)
        End Sub
    End Class

End Namespace
