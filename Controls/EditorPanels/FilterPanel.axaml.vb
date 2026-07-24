Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Controls.EditorPanels

    ''' <summary>Filter-Panel. Reine Auswahl aus Knöpfen - der frühere Auswahllisten-Handler
    ''' (Popup-Breite an die ComboBox angleichen) ist mit der Liste entfallen.</summary>
    Public Class FilterPanel
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
