Imports Avalonia.Controls
Imports Avalonia.Controls.Primitives
Imports Avalonia.Input
Imports Avalonia.Interactivity
Imports Avalonia.Markup.Xaml
Imports Avalonia.Platform.Storage
Imports FerrumPix.Models
Imports FerrumPix.Services
Imports System.Diagnostics
Imports System.Linq
Imports FerrumPix.ViewModels

Namespace Views

    Public Class SettingsView
        Inherits UserControl

        ''' <summary>Beim Erscheinen den Fokus holen, sonst ist Escape tot - dieselbe Falle wie in
        ''' der Personenverwaltung: ein KeyDown an einer Ansicht laeuft nur, wenn der Fokus in ihrem
        ''' Teilbaum sitzt. Geoeffnet wird von einem Knopf ausserhalb, und dort blieb er auch.
        ''' Ueber den Dispatcher, weil beim Anhaengen das Layout noch nicht steht.</summary>
        Private Sub OnAttachedFocus(sender As Object, e As EventArgs)
            Avalonia.Threading.Dispatcher.UIThread.Post(Sub() Me.Focus())
        End Sub

        ''' <summary>Ein Flyout entsteht erst beim Oeffnen aus seinem Template und ist beim
        ''' einmaligen Durchlauf ueber das Fenster noch nicht da. Ohne diesen Einstieg bleibt sein
        ''' Inhalt in der Ausgangssprache stehen, waehrend die uebrige Oberflaeche umgeschaltet
        ''' hat.</summary>
        Private Sub OnLocalizedFlyoutOpened(sender As Object, e As EventArgs)
            Dim content = TryCast(TryCast(sender, Flyout)?.Content, Avalonia.LogicalTree.ILogical)
            If content IsNot Nothing Then LocalizationService.ApplyTo(content)
        End Sub

        ''' <summary>Dasselbe fuer ein MenuFlyout, das seine Eintraege NICHT als Content traegt,
        ''' sondern als Items - der Weg oben findet dort nichts und liess beide Aktionsmenues des
        ''' Katalogbereichs in der Ausgangssprache stehen (Nutzerbefund). Die Mechanik steht in
        ''' <see cref="LocalizationService.ApplyToMenuFlyout"/>, damit die Diagnose sie messen
        ''' kann.</summary>
        Private Sub OnLocalizedMenuFlyoutOpened(sender As Object, e As EventArgs)
            LocalizationService.ApplyToMenuFlyout(TryCast(sender, MenuFlyout))
        End Sub

        ''' <summary>Eine Listenzeile entsteht erst aus dem DataTemplate und meist erst NACH dem
        ''' Sprachdurchlauf ueber das Fenster. Jede neu materialisierte Zeile uebersetzt deshalb
        ''' ihren eigenen Teilbaum - ueber BEIDE Baeume, siehe
        ''' <see cref="LocalizationService.ApplyToMaterialized"/>.</summary>
        Private Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Dim itemRoot = TryCast(sender, Avalonia.Visual)
            If itemRoot IsNot Nothing Then LocalizationService.ApplyToMaterialized(itemRoot)
        End Sub

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            AddHandler Me.AttachedToVisualTree, AddressOf OnAttachedFocus
            AddHandler Loaded, AddressOf HandleLoaded
        End Sub

        Private ReadOnly _search As New SettingsSearch()

        ''' <summary>Die Ansicht wird nur ein- und ausgeblendet, nicht neu gebaut. Eine Suche vom
        ''' letzten Mal stuende sonst beim naechsten Oeffnen noch da, und die Seite saehe halb leer
        ''' aus, ohne dass man wuesste, warum.</summary>
        Protected Overrides Sub OnPropertyChanged(change As Avalonia.AvaloniaPropertyChangedEventArgs)
            MyBase.OnPropertyChanged(change)
            If change.Property Is IsVisibleProperty AndAlso IsVisible Then
                Dim box = Me.FindControl(Of TextBox)("SettingsSearchBox")
                If box IsNot Nothing AndAlso Not String.IsNullOrEmpty(box.Text) Then box.Text = ""
            End If
        End Sub

        ''' <summary>Das X im Suchfeld: leert es und gibt den Fokus zurueck, damit gleich neu
        ''' getippt werden kann.</summary>
        Public Sub OnSettingsSearchClearClick(sender As Object, e As RoutedEventArgs)
            Dim box = Me.FindControl(Of TextBox)("SettingsSearchBox")
            If box Is Nothing Then Return
            box.Text = ""
            box.Focus()
            e.Handled = True
        End Sub

        Public Sub OnSettingsSearchChanged(sender As Object, e As TextChangedEventArgs)
            ApplySettingsSearch(TryCast(sender, TextBox)?.Text)
        End Sub

        ''' <summary>Filtert die Seite (siehe <see cref="SettingsSearch"/>) und rollt nach oben,
        ''' damit der erste Treffer zu sehen ist.</summary>
        Private Sub ApplySettingsSearch(query As String)
            Dim sectionsPanel = Me.FindControl(Of StackPanel)("SettingsSectionsPanel")
            Dim navPanel = Me.FindControl(Of StackPanel)("SettingsNavPanel")
            If sectionsPanel Is Nothing Then Return
            Dim sections = sectionsPanel.Children.OfType(Of Border)().Where(Function(b) b.Classes.Contains("section")).ToList()
            Dim navigation = Function(section As Border) As Control
                                 If navPanel Is Nothing Then Return Nothing
                                 Return navPanel.Children.OfType(Of Button)().FirstOrDefault(
                                     Function(b) String.Equals(TryCast(b.Tag, String), section.Name, StringComparison.Ordinal))
                             End Function
            Dim shown = _search.Apply(sections, navigation, query)
            Dim empty = Me.FindControl(Of TextBlock)("SettingsSearchEmpty")
            If empty IsNot Nothing Then empty.IsVisible = (shown = 0)
            Dim sv = Me.FindControl(Of ScrollViewer)("SettingsScrollViewer")
            If sv IsNot Nothing Then sv.Offset = New Avalonia.Vector(sv.Offset.X, 0)
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

        ''' <summary>Das Programm einer Zeile von "Öffnen mit" auswählen. Ein leerer Name wird aus dem
        ''' Dateinamen gefuellt, damit im Menue gleich etwas Lesbares steht.</summary>
        Private Async Sub OnBrowseOpenWithProgramClick(sender As Object, e As RoutedEventArgs)
            Dim row = TryCast(TryCast(sender, Control)?.DataContext, OpenWithProgramRow)
            If row Is Nothing Then Return
            e.Handled = True
            Try
                Dim path = Await PickProgramFileAsync(LocalizationService.T("Programm wählen"))
                If String.IsNullOrWhiteSpace(path) Then Return
                row.ProgramPath = path
                If String.IsNullOrWhiteSpace(row.Name) Then
                    row.Name = OpenWithService.DisplayName(New OpenWithProgramSettings With {.ProgramPath = path})
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.BrowseOpenWithProgram", ex)
            End Try
        End Sub

        Private Async Sub OnBrowseGmicClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, SettingsViewModel)
            If vm Is Nothing Then Return
            e.Handled = True
            Try
                Dim path = Await PickProgramFileAsync(LocalizationService.T("gmic_qt wählen"))
                If Not String.IsNullOrWhiteSpace(path) Then vm.GmicQtPath = path
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.BrowseGmic", ex)
            End Try
        End Sub

        ''' <summary>Der Dateiwaehler haengt am TopLevel und ist nur von der View aus erreichbar.</summary>
        Private Async Function PickProgramFileAsync(title As String) As Task(Of String)
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return Nothing
                Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                    .Title = title,
                    .AllowMultiple = False
                })
                Return files?.FirstOrDefault()?.Path?.LocalPath
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.PickProgram", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Einen Ordner in die Liste des Katalogindex aufnehmen. Der Auswahldialog haengt
        ''' am TopLevel und ist nur von der View aus erreichbar - deshalb hier und nicht im
        ''' ViewModel, genau wie beim Startordner der Galerie darueber.</summary>
        Private Async Sub OnAddCatalogWatchFolderClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, SettingsViewModel)
            If vm Is Nothing Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Ordner zum Indizieren wählen"),
                    .AllowMultiple = True
                })
                If folders Is Nothing Then Return
                For Each folder In folders
                    Dim path = folder?.Path?.LocalPath
                    If Not String.IsNullOrWhiteSpace(path) Then vm.AddCatalogWatchFolder(path)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.AddCatalogWatchFolder", ex)
            End Try
            e.Handled = True
        End Sub

        ''' <summary>Trägt einen Ordner samt allem, was FerrumPix über ihn weiß, auf einen neuen Ort
        ''' um - für den Fall, dass er AUSSERHALB der Anwendung umbenannt oder verschoben wurde.
        ''' Der Ordnerwähler hängt am TopLevel, deshalb hier und nicht im ViewModel.</summary>
        Private Async Sub OnRelocateCatalogFolderClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Dim vm = TryCast(DataContext, SettingsViewModel)
            Dim oldPath = TryCast(TryCast(sender, MenuItem)?.Tag, String)
            If vm Is Nothing OrElse String.IsNullOrWhiteSpace(oldPath) Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Neuer Ort dieses Ordners"),
                    .AllowMultiple = False
                })
                Dim newPath = folders?.FirstOrDefault()?.Path?.LocalPath
                If String.IsNullOrWhiteSpace(newPath) Then Return
                Dim moved = vm.RelocateCatalogFolder(oldPath, newPath)
                ' Gesagt wird, WAS umgezogen ist. "Fertig" allein liesse offen, ob der gewaehlte
                ' Ordner ueberhaupt der richtige war - bei null Zeilen war er es meistens nicht.
                vm.CleanupResultMessage = String.Format(
                    LocalizationService.T("{0} Katalogeinträge und {1} Vorschaubilder umgezogen"),
                    moved.CatalogRows, moved.Thumbnails)
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.RelocateCatalogFolder", ex)
            End Try
        End Sub

        Public Sub OnSectionNavClick(sender As Object, e As RoutedEventArgs)
            Dim button = TryCast(sender, Button)
            Dim targetName = TryCast(button?.Tag, String)
            If String.IsNullOrWhiteSpace(targetName) Then Return
            ScrollSectionToTop(targetName)
            e.Handled = True
        End Sub

        ''' <summary>Rollt einen Abschnitt an den oberen Rand.
        '''
        ''' Nicht nur fuer die Navigation links: wer in der Personenwand weit unten eine Kachel
        ''' anklickt, bekommt danach eine viel KUERZERE Ansicht - die Rollposition bliebe stehen, und
        ''' auf einmal steht dort der naechste Abschnitt. Deshalb rollt jeder Wechsel innerhalb der
        ''' Personenverwaltung ebenfalls hierher.</summary>
        Private Sub ScrollSectionToTop(sectionName As String)
            Dim sv = Me.FindControl(Of ScrollViewer)("SettingsScrollViewer")
            Dim target = Me.FindControl(Of Control)(sectionName)
            If sv Is Nothing OrElse target Is Nothing Then Return

            Dim pt = Avalonia.VisualExtensions.TranslatePoint(target, New Avalonia.Point(0, 0), sv)
            If pt.HasValue Then
                sv.Offset = New Avalonia.Vector(0, Math.Max(0, sv.Offset.Y + pt.Value.Y))
            End If
        End Sub

        ''' <summary>Fuehrt in die Personenverwaltung. Die liegt nicht mehr hier: in den
        ''' Einstellungen legt man fest, WIE das Programm arbeitet, dort arbeitet man am Bestand.
        ''' Der Knopf steht trotzdem hier, damit niemand suchen muss.</summary>
        Private Sub OnManagePeopleClick(sender As Object, e As RoutedEventArgs)
            TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)?.OpenPeople()
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

        ''' <summary>Der Hinweis neben der Versionsangabe führt zur zuletzt veröffentlichten Fassung.
        ''' Die Adresse steht im Dienst, der auch die Nummer holt - eine zweite Stelle mit derselben
        ''' Adresse liefe irgendwann auseinander.</summary>
        Public Sub OnReleasePageClick(sender As Object, e As RoutedEventArgs)
            OpenExternalUrl(UpdateCheckService.ReleasesAddress)
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
            ShellOpenService.Open(url, "Settings.OpenExternalUrl")
        End Sub

        Public Shadows Sub OnKeyDown(sender As Object, e As KeyEventArgs)
            If e.Key = Key.Escape Then
                ' Erst die Suche leeren, erst der zweite ESC schliesst die Einstellungen.
                Dim box = Me.FindControl(Of TextBox)("SettingsSearchBox")
                If box IsNot Nothing AndAlso Not String.IsNullOrEmpty(box.Text) Then
                    box.Text = ""
                    e.Handled = True
                    Return
                End If
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
                ' Die Pfeiltasten schieben zeilenweise. Der Fokus sitzt auf der Ansicht OBERHALB des
                ' ScrollViewers, dessen eigene Tastenbehandlung erreicht ein Tastendruck also nie.
                ' Ein Textfeld bleibt aussen vor: es markiert Hoch/Runter nur als behandelt, wenn
                ' sich der Cursor bewegt, in einer einzeiligen Eingabe also nie - ohne die Ausnahme
                ' liefe die Seite beim Tippen in der Suche davon.
                Case Key.Down, Key.Up
                    If TypeOf e.Source Is TextBox Then Return
                    ScrollSettingsBy(sv, If(e.Key = Key.Down, 50.0, -50.0))
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
