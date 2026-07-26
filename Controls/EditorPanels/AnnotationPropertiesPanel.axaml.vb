Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports Avalonia.VisualTree
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Controls.EditorPanels

    Public Class AnnotationPropertiesPanel
        Inherits UserControl


        ''' <summary>Neue Zeile NUR per Strg+Enter oder Umschalt+Enter (
        ''' Umschalt-Variante nachgereicht): der Renderer bricht
        ''' Text-Objekte nie automatisch um, ein unbedachtes Enter erzeugte sonst Zeilen, deren Box
        ''' beim Verschieben Geist-Fragmente hinterliess. Sitzt als TUNNEL direkt auf der TextBox -
        ''' er feuert damit vor deren eigener Enter-Behandlung, ein Bubble-Handler kam nie an.</summary>
        Private Sub OnAnnotationTextKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Enter Then Return
            Dim box = TryCast(sender, TextBox)
            If box Is Nothing Then Return
            If e.KeyModifiers.HasFlag(KeyModifiers.Control) OrElse e.KeyModifiers.HasFlag(KeyModifiers.Shift) Then
                Dim text = If(box.Text, "")
                Dim selStart = Math.Min(box.SelectionStart, box.SelectionEnd)
                Dim selEnd = Math.Max(box.SelectionStart, box.SelectionEnd)
                Dim caret = If(selEnd > selStart, selStart, Math.Max(0, Math.Min(box.CaretIndex, text.Length)))
                If selEnd > selStart Then text = text.Remove(selStart, selEnd - selStart)
                box.Text = text.Insert(caret, vbLf)
                box.SelectionStart = caret + 1
                box.SelectionEnd = caret + 1
                box.CaretIndex = caret + 1
            End If
            ' In BEIDEN Faellen erledigt: Enter allein darf keine Zeile einfuegen (AcceptsReturn
            ' ist nur fuer die mehrzeilige ANZEIGE an), Strg+Enter hat sie schon eingefuegt.
            e.Handled = True
        End Sub

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            ' Tunnel statt XAML-KeyDown: siehe OnAnnotationTextKeyDown.
            Dim textBox = Me.FindControl(Of TextBox)("AnnotationTextBox")
            textBox?.AddHandler(KeyDownEvent, AddressOf OnAnnotationTextKeyDown, RoutingStrategies.Tunnel)
        End Sub

        Private Async Function PickSingleImagePathAsync(title As String) As Task(Of String)
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return Nothing
                Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                    .Title = title,
                    .AllowMultiple = False,
                    .FileTypeFilter = New List(Of FilePickerFileType) From {
                        New FilePickerFileType(LocalizationService.T("Bilder")) With {
                            .Patterns = New String() {"*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp", "*.tif", "*.tiff", "*.avif", "*.ico"}
                        }
                    }
                })
                Return files?.FirstOrDefault()?.Path.LocalPath
            Catch
                Return Nothing
            End Try
        End Function

        Public Async Sub OnInsertImageClick(sender As Object, e As RoutedEventArgs)
            Try
                Dim vm = TryCast(DataContext, EditorViewModel)
                If vm Is Nothing Then Return
                Dim path = Await PickSingleImagePathAsync("Bild auswählen")
                If Not String.IsNullOrWhiteSpace(path) Then vm.AddImageAnnotationAtCurrentPosition(path)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("AnnotationPropertiesPanel.OnInsertImageClick", ex)
            End Try
        End Sub

        Public Async Sub OnWatermarkChooseImageClick(sender As Object, e As RoutedEventArgs)
            Try
                Dim vm = TryCast(DataContext, EditorViewModel)
                If vm Is Nothing Then Return
                Dim path = Await PickSingleImagePathAsync("Wasserzeichen-Bild auswählen")
                If Not String.IsNullOrWhiteSpace(path) Then vm.SetWatermarkImagePath(path)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("AnnotationPropertiesPanel.OnWatermarkChooseImageClick", ex)
            End Try
        End Sub

        Public Sub OnWatermarkClearImageClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            vm.ClearWatermarkImagePath()
        End Sub

        Public Sub OnWatermarkSavePresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            vm.SaveCurrentWatermarkPreset()
        End Sub

        Public Sub OnWatermarkDeletePresetClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            vm.DeleteCurrentWatermarkPreset()
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
