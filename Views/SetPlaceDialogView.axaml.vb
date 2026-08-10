Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml

Namespace Views

    ''' <summary>Der Dialog "Aufnahmeort setzen": ein Feld für Koordinate ODER Ortsname, darunter
    ''' die Treffer der lokalen Ortstabelle und die Vorschau dessen, was geschrieben wird.</summary>
    Public Class SetPlaceDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        ''' Der Fokus gehört ins Eingabefeld: der Dialog hat genau eines, und wer ihn öffnet, will
        ''' tippen oder einfügen.
        Public Sub FocusQueryField()
            Dim box = Me.FindControl(Of TextBox)("SetPlaceQueryTextBox")
            If box Is Nothing Then Return
            box.Focus()
            box.SelectAll()
        End Sub

    End Class

End Namespace
