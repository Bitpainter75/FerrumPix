Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Views

    ''' <summary>Der Dialog "Aufnahmeort aus Aufzeichnung": Zeitversatz und zugelassener Abstand,
    ''' darunter die mitzaehlende Vorschau, wie viele Bilder damit einen Ort bekaemen.</summary>
    Public Class GpxTrackDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        ''' Der Fokus gehoert auf den Zeitversatz: das Feld mit dem Abstand steht meist schon
        ''' richtig, der Versatz ist der, an dem gedreht wird.
        Public Sub FocusOffsetField()
            Dim box = Me.FindControl(Of TextBox)("GpxOffsetTextBox")
            If box Is Nothing Then Return
            box.Focus()
            box.SelectAll()
        End Sub

    End Class

End Namespace
