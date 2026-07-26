Imports Avalonia.Controls
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

        Private Sub OnPresetClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            vm.SetDialogBatchResizePreset(If(button.Tag, "").ToString())
            e.Handled = True
        End Sub

        ''' "BatchResizeWidthTextBox" liegt in der NameScope DIESES Controls - der Fokus muss
        ''' über diese Methode gesetzt werden (siehe Hinweis im BatchResize-Dialog).
        Public Sub FocusWidthField()
            Dim widthBox = Me.FindControl(Of TextBox)("BatchResizeWidthTextBox")
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
