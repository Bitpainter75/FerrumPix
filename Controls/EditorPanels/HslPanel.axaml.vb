Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Markup.Xaml

Namespace Controls.EditorPanels

    Public Class HslPanel
        Inherits UserControl

        ''' <summary>Inhalte aus einem DataTemplate entstehen oft erst NACH dem Sprachdurchlauf
        ''' ueber das Fenster. Jedes neu materialisierte Element uebersetzt deshalb seinen eigenen
        ''' Teilbaum - ueber BEIDE Baeume, siehe
        ''' <see cref="LocalizationService.ApplyToMaterialized"/>.</summary>
        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Dim itemRoot = TryCast(sender, Avalonia.Visual)
            If itemRoot IsNot Nothing Then FerrumPix.Services.LocalizationService.ApplyToMaterialized(itemRoot)
        End Sub

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        ''' <summary>Mittlere Maustaste auf einem Farbkreis: dieses Band auf den Standard
        ''' zuruecksetzen.
        '''
        ''' Dieselbe Geste raeumt in der Galerie einen Filter weg, ist hier also nichts Neues. Der
        ''' Doppelklick bleibt den REGLERN vorbehalten - dort setzt er genau einen von drei Werten
        ''' zurueck, und beides auf dieselbe Geste zu legen hiesse, dass ein Doppelklick je nach
        ''' Trefferpunkt einen Wert oder alle drei loescht.
        '''
        ''' UEBER DEN DATENKONTEXT DES KNOPFES und nicht ueber eine Bindung mit Parameter: die
        ''' Zeile ist eine ItemsControl-Vorlage, jeder Knopf traegt sein eigenes Band als
        ''' Datenkontext, und ein Zeigerereignis kennt keinen Kommandoparameter.</summary>
        Private Sub OnBandPointerPressed(sender As Object, e As PointerPressedEventArgs)
            Dim button = TryCast(sender, Button)
            If button Is Nothing Then Return
            If Not e.GetCurrentPoint(button).Properties.IsMiddleButtonPressed Then Return
            Dim chip = TryCast(button.DataContext, ViewModels.EditorViewModel.HslBandChip)
            Dim editor = TryCast(Me.DataContext, ViewModels.EditorViewModel)
            If chip Is Nothing OrElse editor Is Nothing Then Return
            If editor.ResetHslBandCommand IsNot Nothing AndAlso
               editor.ResetHslBandCommand.CanExecute(chip.Key) Then
                editor.ResetHslBandCommand.Execute(chip.Key)
            End If
            ' Sonst nimmt der Knopf die mittlere Taste als gewoehnlichen Druck und waehlt das Band
            ' zusaetzlich aus.
            e.Handled = True
        End Sub
    End Class

End Namespace
