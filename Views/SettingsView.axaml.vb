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

        ''' <summary>Ein Flyout entsteht erst beim Oeffnen aus seinem Template und ist beim
        ''' einmaligen Durchlauf ueber das Fenster noch nicht da. Ohne diesen Einstieg bleibt sein
        ''' Inhalt in der Ausgangssprache stehen, waehrend die uebrige Oberflaeche umgeschaltet
        ''' hat.</summary>
        Private Sub OnLocalizedFlyoutOpened(sender As Object, e As EventArgs)
            Dim inhalt = TryCast(TryCast(sender, Flyout)?.Content, Avalonia.LogicalTree.ILogical)
            If inhalt IsNot Nothing Then LocalizationService.ApplyTo(inhalt)
        End Sub

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            ' Ausklappliste nie schmaler als der Knopf (siehe FlyoutHelpers).
            MatchFlyoutWidthToButton(Me.FindControl(Of Button)("ScaleScreenDropDownButton"))
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

        ''' <summary>Öffnet den Lizenztext des angeklickten Bestandteils. Die Adresse steht im
        ''' Tag der Schaltfläche, damit die Liste im XAML gepflegt werden kann, ohne hier für
        ''' jeden Eintrag eine eigene Behandlung anzulegen.</summary>
        Public Sub OnLicenseLinkClick(sender As Object, e As RoutedEventArgs)
            Dim url = TryCast(TryCast(sender, Control)?.Tag, String)
            If Not String.IsNullOrWhiteSpace(url) Then OpenExternalUrl(url)
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
            If e.Key = Key.Escape Then
                Dim vm = TryCast(DataContext, SettingsViewModel)
                vm?.CancelCommand.Execute(Nothing)
                e.Handled = True
                Return
            End If

            If e.KeyModifiers <> KeyModifiers.None Then Return
            Dim sv = Me.FindControl(Of ScrollViewer)("SettingsScrollViewer")
            If sv Is Nothing OrElse sv.Viewport.Height <= 0 Then Return

            ' Bild auf/ab und Pos1/Ende blättern durch die Einstellungsliste. Bedienelemente, die diese
            ' Tasten selbst brauchen (Textfeld, Regler, Auswahlliste), markieren sie als behandelt - das
            ' XAML-Ereignis kommt dann gar nicht erst hier an, der Cursor im Textfeld bleibt also heil.
            ' Eine Bildschirmhöhe minus einer Zeile Überlappung, damit beim Blättern nichts überspringt.
            Dim page = Math.Max(40.0, sv.Viewport.Height - 40.0)
            Select Case e.Key
                Case Key.PageDown
                    ScrollSettingsBy(sv, page)
                Case Key.PageUp
                    ScrollSettingsBy(sv, -page)
                Case Key.Home
                    sv.Offset = New Avalonia.Vector(sv.Offset.X, 0)
                Case Key.End
                    sv.Offset = New Avalonia.Vector(sv.Offset.X, Math.Max(0, sv.Extent.Height - sv.Viewport.Height))
                Case Else
                    Return
            End Select
            e.Handled = True
        End Sub

        Private Shared Sub ScrollSettingsBy(sv As ScrollViewer, delta As Double)
            Dim maxOffset = Math.Max(0, sv.Extent.Height - sv.Viewport.Height)
            Dim target = Math.Max(0, Math.Min(maxOffset, sv.Offset.Y + delta))
            sv.Offset = New Avalonia.Vector(sv.Offset.X, target)
        End Sub
    End Class

End Namespace
