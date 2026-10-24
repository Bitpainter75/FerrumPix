Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports FerrumPix.Services

Namespace Controls.EditorPanels

    Public Class FillPanel
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        ''' <summary>Entfernt den markierten Farbstopp. Die Markierung lebt in der Verlaufsleiste,
        ''' nicht im ViewModel, deshalb laeuft der Knopf hierueber statt ueber einen Befehl.</summary>
        Private Sub OnRemoveStopClick(sender As Object, e As RoutedEventArgs)
            Me.FindControl(Of GradientStopEditor)("GradientStops")?.RemoveSelectedStop()
        End Sub

        ''' <summary>Nach dem Speichern das Flyout schliessen; gespeichert hat der Befehl.
        '''
        ''' NACHGESTELLT, nicht sofort: Button.OnClick loest erst Click aus und liest DANACH sein
        ''' Kommando. Schloss das Flyout hier sofort, hing sein Inhalt nicht mehr am Baum, die
        ''' Bindung an SaveGradientPresetCommand fiel weg, und die Vorlage wurde nie gespeichert.</summary>
        Private Sub OnSavePresetClick(sender As Object, e As RoutedEventArgs)
            Avalonia.Threading.Dispatcher.UIThread.Post(
                Sub() Me.FindControl(Of Button)("SavePresetButton")?.Flyout?.Hide())
        End Sub

        ''' <summary>Ein Flyout entsteht erst beim Oeffnen und haengt nicht im logischen Baum: ohne
        ''' diesen Einstieg bliebe sein Inhalt in der Ausgangssprache.</summary>
        Private Sub OnLocalizedFlyoutOpened(sender As Object, e As EventArgs)
            Dim content = TryCast(TryCast(sender, Flyout)?.Content, Avalonia.LogicalTree.ILogical)
            If content IsNot Nothing Then LocalizationService.ApplyTo(content)
        End Sub
    End Class

End Namespace
