Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Views

    ''' <summary>Der Einrichtungsassistent beim ersten Start (SetupAssistantViewModel). Die
    ''' Logik steht im ViewModel; das Fenster baut diese Ansicht erst, wenn der Assistent zum
    ''' ersten Mal erscheint, und uebersetzt sie dann (MainWindow, EnsureSetupAssistantView).</summary>
    Public Class SetupAssistantView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

    End Class

End Namespace
