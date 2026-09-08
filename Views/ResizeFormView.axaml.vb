Imports Avalonia.Controls
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Threading
Imports FerrumPix.ViewModels

Namespace Views

    ''' <summary>Der FORMULARKERN von „Bildgröße ändern" (Breite/Höhe/Seitenverhältnis/
    ''' Neuberechnung + Vorgaben-Knöpfe). Herausgelöst, damit der „Exportieren nach"-Dialog
    ''' EXAKT dasselbe Formular zeigt wie der Einzeldialog - zwei Nachbauten drifteten
    ''' zwangsläufig auseinander. Bindet dieselben DialogBatchResize*-Eigenschaften des
    ''' MainWindowViewModel; es ist immer nur ein Dialog zugleich offen.</summary>
    Public Class ResizeFormView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
        End Sub

        ''' <summary>Beim Betreten eines Kantenfeldes haelt das ViewModel das Seitenverhaeltnis an,
        ''' das JETZT gilt - bis zum Verlassen bleibt die andere Kante stehen.
        '''
        ''' Ohne das zieht sie bei jedem Anschlag nach: wer in 1000x500 eine 1500 tippt, sieht die
        ''' Hoehe ueber 1, 5 und 50 wandern, und im Feld steht laufend etwas anderes, als er gerade
        ''' schreibt.</summary>
        Private Sub OnEdgeFieldGotFocus(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm Is Nothing Then Return
            vm.BeginBatchResizeEdgeEdit()
        End Sub

        Private Sub OnEdgeFieldLostFocus(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm Is Nothing Then Return
            vm.CommitBatchResizeEdgeEdit(IsWidthField(sender))
        End Sub

        ''' Die Eingabetaste bestaetigt den Dialog (Fenster-Tastenhandler) - die gekoppelte Kante
        ''' muss VORHER nachgezogen sein, sonst liefe der Stapel mit der alten Zahl. Dieser Handler
        ''' liegt am Feld und damit vor dem Fenster. Bleibt der Dialog wider Erwarten offen, faengt
        ''' das erneute Betreten die naechste Eingabe wieder ein.
        Private Sub OnEdgeFieldKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter AndAlso e.Key <> Key.Return Then Return
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm Is Nothing Then Return
            vm.CommitBatchResizeEdgeEdit(IsWidthField(sender))
            vm.BeginBatchResizeEdgeEdit()
        End Sub

        Private Shared Function IsWidthField(sender As Object) As Boolean
            Dim box = TryCast(sender, TextBox)
            Return box Is Nothing OrElse Not String.Equals(box.Name, "BatchResizeHeightTextBox", StringComparison.Ordinal)
        End Function

        Private Sub OnPresetClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            vm.SetDialogBatchResizePreset(If(button.Tag, "").ToString())
            e.Handled = True
        End Sub

        ''' "BatchResizeWidthTextBox" liegt in der NameScope DIESES Controls - der Fokus muss
        ''' über diese Methode gesetzt werden (siehe Hinweis im BatchResize-Dialog). Im
        ''' Lange-Kante-Modus ist das Breitenfeld ausgeblendet; dort gehört der Fokus in das eine
        ''' sichtbare Kantenfeld - sonst tippte der Nutzer ins Leere.
        Public Sub FocusWidthField()
            Dim widthBox = Me.FindControl(Of TextBox)("BatchResizeWidthTextBox")
            If widthBox Is Nothing OrElse Not widthBox.IsVisible Then
                widthBox = Me.FindControl(Of TextBox)("BatchResizeLongEdgeTextBox")
            End If
            If widthBox Is Nothing Then Return

            Dispatcher.UIThread.Post(
                Sub()
                    widthBox.Focus()
                    widthBox.SelectAll()
                End Sub,
                DispatcherPriority.Input)
            Dispatcher.UIThread.Post(
                Sub()
                    widthBox.Focus()
                    widthBox.SelectAll()
                End Sub,
                DispatcherPriority.Background)
        End Sub

    End Class

End Namespace
