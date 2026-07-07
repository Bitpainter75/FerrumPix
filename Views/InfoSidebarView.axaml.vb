Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Markup.Xaml
Imports Avalonia.Interactivity

Namespace Views

    Public Class InfoSidebarView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            Me.AddHandler(InputElement.PointerWheelChangedEvent, AddressOf HandlePointerWheelChanged, RoutingStrategies.Bubble)
        End Sub

        Private Sub HandlePointerWheelChanged(sender As Object, e As PointerWheelEventArgs)
            e.Handled = True
        End Sub
    End Class

End Namespace
