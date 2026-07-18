Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports FerrumPix.Services
Imports System.Diagnostics
Imports System.Linq
Imports FerrumPix.ViewModels

Namespace Views

    Public Class SettingsView
        Inherits UserControl

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            AddHandler Loaded, AddressOf HandleLoaded
        End Sub

        Private Sub HandleLoaded(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, SettingsViewModel)
            Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
            Dim screens As New List(Of String)()
            If topLevel IsNot Nothing AndAlso topLevel.Screens IsNot Nothing AndAlso topLevel.Screens.All IsNot Nothing Then
                For Each screen As Avalonia.Platform.Screen In topLevel.Screens.All
                    Dim name = If(screen Is Nothing, "", screen.DisplayName)
                    If String.IsNullOrWhiteSpace(name) Then Continue For
                    If screens.Contains(name) Then Continue For
                    screens.Add(name)
                Next
            End If
            vm?.RefreshApplicationScaleScreens(screens)

        End Sub

        Private Sub OnScaleScreenOptionClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim vm = TryCast(DataContext, SettingsViewModel)
            If button Is Nothing OrElse vm Is Nothing Then Return
            Dim selected = TryCast(button.DataContext, String)
            If Not String.IsNullOrEmpty(selected) Then
                vm.ApplicationScaleScreen = selected
            End If
            FlyoutHelpers.CloseContainingFlyout(button)
            e.Handled = True
        End Sub

        Private Async Sub OnBrowseGalleryStartupFolderClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, SettingsViewModel)
            If vm Is Nothing Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Startordner der Galerie wählen"),
                    .AllowMultiple = False
                })
                Dim folder = folders?.FirstOrDefault()
                If folder IsNot Nothing Then
                    Dim path = folder.Path.LocalPath
                    If Not String.IsNullOrWhiteSpace(path) Then
                        vm.GalleryStartupCustomFolder = path
                        vm.GalleryStartupFolderMode = "Custom"
                    End If
                End If
            Catch
            End Try
            e.Handled = True
        End Sub

        Public Sub OnSectionNavClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim targetName = TryCast(button?.Tag, String)
            If String.IsNullOrWhiteSpace(targetName) Then Return

            Dim sv = Me.FindControl(Of ScrollViewer)("SettingsScrollViewer")
            Dim target = Me.FindControl(Of Control)(targetName)
            If sv Is Nothing OrElse target Is Nothing Then Return

            Dim pt = Avalonia.VisualExtensions.TranslatePoint(target, New Avalonia.Point(0, 0), sv)
            If pt.HasValue Then
                sv.Offset = New Avalonia.Vector(0, Math.Max(0, sv.Offset.Y + pt.Value.Y))
            End If
            e.Handled = True
        End Sub

        Public Sub OnHomepageClick(sender As Object, e As RoutedEventArgs)
            OpenExternalUrl("https://ferrumpix.app")
            e.Handled = True
        End Sub

        Public Sub OnGitHubClick(sender As Object, e As RoutedEventArgs)
            OpenExternalUrl("https://github.com/Bitpainter75/FerrumPix")
            e.Handled = True
        End Sub

        Private Shared Sub OpenExternalUrl(url As String)
            Try
                Process.Start(New ProcessStartInfo With {
                    .FileName = url,
                    .UseShellExecute = True
                })
            Catch
            End Try
        End Sub

        Public Shadows Sub OnKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key <> Key.Escape Then Return

            Dim vm = TryCast(DataContext, SettingsViewModel)
            vm?.CancelCommand.Execute(Nothing)
            e.Handled = True
        End Sub
    End Class

End Namespace
