Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Views

    ''' <summary>Wasserzeichen-Formular (Vorlage, Anker, Breite) - geteilt zwischen dem Dialog
    ''' „Wasserzeichen anwenden" und der gleichnamigen Sektion im Export-Dialog. Reine
    ''' Bindungs-View, alle Logik liegt im MainWindowViewModel.</summary>
    Public Class WatermarkFormView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

    End Class

End Namespace
