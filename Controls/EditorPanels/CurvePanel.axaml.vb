Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.VisualTree
Imports FerrumPix.ViewModels

Namespace Controls.EditorPanels

    Public Class CurvePanel
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            ' Rechtsklick auf einen Kanalschalter: Einzelvergleich dieses Kanals, wie am Regler.
            ' Im TUNNEL, damit der Schalter ihn nicht vorher als eigenen Druck verbucht. In eckigen
            ' Klammern, weil AddHandler in VB ein Schluesselwort ist.
            Me.[AddHandler](PointerPressedEvent, New EventHandler(Of PointerPressedEventArgs)(AddressOf OnChannelRightClick), RoutingStrategies.Tunnel)
        End Sub

        Private Sub OnChannelRightClick(sender As Object, e As PointerPressedEventArgs)
            If Not e.GetCurrentPoint(Me).Properties.IsRightButtonPressed Then Return
            Dim button = TryCast(e.Source, Visual)?.FindAncestorOfType(Of Button)(includeSelf:=True)
            If button Is Nothing OrElse Not button.Classes.Contains("channel-tab") Then Return
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            vm.ToggleCurveCompare(TryCast(button.CommandParameter, String))
            e.Handled = True
        End Sub
    End Class

End Namespace
