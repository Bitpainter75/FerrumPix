Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Controls.EditorPanels

    ''' <summary>Werkzeugpanel „Pfad": Punkte setzen, abschließen, nachziehen. Reine Bindungs-View -
    ''' die Stützpunkte liegen am Objekt, das Zeichnen im EditorViewModel.</summary>
    Public Class PathPanel
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub
    End Class

End Namespace
