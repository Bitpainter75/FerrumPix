Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Views

    ''' <summary>Dialogsektion „Exportieren nach" - reine Bindungs-View, alle Logik liegt im
    ''' MainWindowViewModel (DialogExport*-Eigenschaften).</summary>
    Public Class ExportToDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

    End Class

End Namespace
