Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Threading
Imports FerrumPix.ViewModels

Namespace Views

    Public Class BatchResizeDialogView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        ''' Delegiert in die eingebettete ResizeFormView - das Breitenfeld liegt in DEREN NameScope.
        Public Sub FocusWidthField()
            Me.FindControl(Of ResizeFormView)("ResizeForm")?.FocusWidthField()
        End Sub

    End Class

End Namespace
