Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml

Namespace Views

    Public Class BatchRenameDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        ''' Fügt den Platzhalter des geklickten Chips an der Cursorposition der Namensvorlage ein
        ''' (Nutzerwunsch 2026-07-17: Klick-Auswahl statt Hilfetext) und setzt den Cursor dahinter.
        Private Sub OnInsertPlaceholder(sender As Object, e As RoutedEventArgs)
            Dim chip = TryCast(sender, Button)
            Dim box = Me.FindControl(Of TextBox)("PatternTextBox")
            If chip Is Nothing OrElse box Is Nothing Then Return
            Dim placeholder = TryCast(chip.Tag, String)
            If String.IsNullOrEmpty(placeholder) Then Return

            Dim text = If(box.Text, "")
            Dim caret = Math.Max(0, Math.Min(box.CaretIndex, text.Length))
            box.Text = text.Substring(0, caret) & placeholder & text.Substring(caret)
            box.CaretIndex = caret + placeholder.Length
            box.Focus()
        End Sub
    End Class

End Namespace
