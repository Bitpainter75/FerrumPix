Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Controls.EditorPanels

    ''' <summary>Werkzeugpanel „Maske": Pinsel und die beiden gerechneten Verläufe. Reine
    ''' Bindungs-View - die Geometrie der Verläufe liegt im EditorViewModel.</summary>
    Public Class MaskPanel
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
