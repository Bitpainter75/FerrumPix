Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading.Tasks
Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports Avalonia.VisualTree
Imports FerrumPix.Services
Imports FerrumPix.ViewModels

Namespace Controls.EditorPanels

    Public Class AnnotationPropertiesPanel
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
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
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            Dim path = Await PickSingleImagePathAsync("Bild auswählen")
            If Not String.IsNullOrWhiteSpace(path) Then vm.AddImageAnnotationAtCurrentPosition(path)
        End Sub

        Public Async Sub OnWatermarkChooseImageClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, EditorViewModel)
            If vm Is Nothing Then Return
            Dim path = Await PickSingleImagePathAsync("Wasserzeichen-Bild auswählen")
            If Not String.IsNullOrWhiteSpace(path) Then vm.SetWatermarkImagePath(path)
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
