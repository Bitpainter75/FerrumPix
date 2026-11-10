Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Markup.Xaml
Imports FerrumPix.ViewModels

Namespace Controls.EditorPanels

    ''' <summary>Filter-Panel: Knöpfe mit Namen oder Kacheln mit Vorschau. Der Code hier meldet dem
    ''' ViewModel nur, ob die Gruppe aufgeklappt ist; zugeklappt rechnet es keine Kacheln. Den Zustand
    ''' selbst merkt ExpanderState, deshalb keine Bindung an IsExpanded: ein von dort gesetzter Wert
    ''' löste sie ab.</summary>
    Public Class FilterPanel
        Inherits UserControl

        Private ReadOnly _expander As Expander

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            _expander = Me.FindControl(Of Expander)("FilterExpander")
            If _expander IsNot Nothing Then
                AddHandler _expander.PropertyChanged,
                    Sub(sender, e)
                        If e.Property Is Expander.IsExpandedProperty Then ReportExpanded()
                    End Sub
            End If
        End Sub

        Protected Overrides Sub OnDataContextChanged(e As EventArgs)
            MyBase.OnDataContextChanged(e)
            ReportExpanded()
        End Sub

        Private Sub ReportExpanded()
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing OrElse _expander Is Nothing Then Return
            vm.FilterPanelExpanded = _expander.IsExpanded
        End Sub
    End Class

End Namespace
