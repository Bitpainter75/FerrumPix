Imports Avalonia.Controls
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports FerrumPix.ViewModels
Imports FerrumPix.Services
Imports System.Linq

Namespace Views

    Public Class SearchDialogView
        Inherits UserControl

        ''' <summary>Ein Flyout entsteht erst beim Oeffnen aus seinem Template und ist beim
        ''' einmaligen Durchlauf ueber das Fenster noch nicht da. Ohne diesen Einstieg bleibt sein
        ''' Inhalt in der Ausgangssprache stehen, waehrend die uebrige Oberflaeche umgeschaltet
        ''' hat.</summary>
        Private Sub OnLocalizedFlyoutOpened(sender As Object, e As EventArgs)
            Dim content = TryCast(TryCast(sender, Flyout)?.Content, Avalonia.LogicalTree.ILogical)
            If content IsNot Nothing Then LocalizationService.ApplyTo(content)
        End Sub

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            ' Ausklappliste nie schmaler als der Knopf (siehe FlyoutHelpers).
            MatchFlyoutWidthToButton(Me.FindControl(Of Button)("SearchRatingDropDownButton"))
        End Sub

        Private Sub OnFavoriteClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            vm.SetDialogSearchFavoriteMode(TryCast(button.Tag, String))
            e.Handled = True
        End Sub

        Private Sub OnRatingClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            vm.SetDialogSearchRatingMin(If(button.Tag, "").ToString())
            FlyoutHelpers.CloseContainingFlyout(button)
            e.Handled = True
        End Sub

        Private Async Sub OnBrowseFolderClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If vm Is Nothing Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Startordner wählen"),
                    .AllowMultiple = False
                })
                Dim folder = folders?.FirstOrDefault()
                If folder IsNot Nothing Then
                    Dim path = folder.Path.LocalPath
                    If Not String.IsNullOrWhiteSpace(path) Then vm.DialogSearchRootFolder = path
                End If
            Catch
            End Try
            e.Handled = True
        End Sub

        Private Sub OnAddConditionClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            vm?.AddDialogSearchCondition()
            e.Handled = True
        End Sub

        Private Sub OnRemoveConditionClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            Dim condition = TryCast(button?.DataContext, SearchCondition)
            If vm Is Nothing OrElse condition Is Nothing Then Return
            vm.RemoveDialogSearchCondition(condition)
            e.Handled = True
        End Sub

        Private Sub OnConditionCombinatorClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, MainWindowViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            vm.DialogSearchConditionCombinator = TryCast(button.Tag, String)
            e.Handled = True
        End Sub
    End Class

End Namespace
