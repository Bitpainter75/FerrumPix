Imports System
Imports System.Linq
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Markup.Xaml
Imports Avalonia.VisualTree

Namespace Controls.EditorPanels

    Public Class ResizePanel
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        Public Sub OnMatchWidthDropDownOpened(sender As Object, e As EventArgs)
            Dim comboBox = TryCast(sender, ComboBox)
            If comboBox Is Nothing Then Return
            Dim popup = comboBox.GetVisualDescendants().OfType(Of Popup)().FirstOrDefault()
            If popup IsNot Nothing Then
                popup.Width = comboBox.Bounds.Width
            End If
        End Sub
    End Class

End Namespace
