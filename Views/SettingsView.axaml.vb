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
        Implements IModeView

        ''' <summary>Beim Erscheinen den Fokus holen, sonst ist Escape tot - dieselbe Falle wie in
        ''' der Personenverwaltung: ein KeyDown an einer Ansicht laeuft nur, wenn der Fokus in ihrem
        ''' Teilbaum sitzt. Geoeffnet wird von einem Knopf ausserhalb, und dort blieb er auch.
        ''' Ueber den Dispatcher, weil beim Anhaengen das Layout noch nicht steht.
        '''
        ''' Nur wenn die Einstellungen wirklich offen sind: eine Seite, die gebaut wird, waehrend ein
        ''' anderer Modus vorn ist, darf ihm nicht die Tastatur wegnehmen.</summary>
        Private Sub OnAttachedFocus(sender As Object, e As EventArgs)
            Avalonia.Threading.Dispatcher.UIThread.Post(
                Sub()
                    Dim host = TryCast(TopLevel.GetTopLevel(Me)?.DataContext, MainWindowViewModel)
                    If host Is Nothing OrElse host.CurrentMode = AppMode.Settings Then Me.Focus()
                End Sub)
        End Sub

        ''' <summary>Jedes weitere Betreten, auch das erste nach dem Vorabbau.</summary>
        Public Sub OnModeEntered() Implements IModeView.OnModeEntered
            ResetSettingsScrollPosition()
            Avalonia.Threading.Dispatcher.UIThread.Post(Sub()
                                                            ResetSettingsScrollPosition()
                                                            Me.Focus()
                                                        End Sub,
                                                        Avalonia.Threading.DispatcherPriority.Loaded)
        End Sub

        ''' <summary>Die Ansicht bleibt beim Moduswechsel im Baum. Ohne ausdrueckliches Zuruecksetzen
        ''' oeffnet sie sich daher an der Stelle der letzten Sitzung statt bei „Allgemein“.</summary>
        Private Sub ResetSettingsScrollPosition()
            Dim scroll = Me.FindControl(Of ScrollViewer)("SettingsScrollViewer")
            If scroll IsNot Nothing Then scroll.Offset = New Avalonia.Vector(scroll.Offset.X, 0)
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
        Friend Sub OnLocalizedMenuFlyoutOpened(sender As Object, e As EventArgs)
            LocalizationService.ApplyToMenuFlyout(TryCast(sender, MenuFlyout))
        End Sub

        ''' <summary>Eine Listenzeile entsteht erst aus dem DataTemplate und meist erst NACH dem
        ''' Sprachdurchlauf ueber das Fenster. Jede neu materialisierte Zeile uebersetzt deshalb
        ''' ihren eigenen Teilbaum - ueber BEIDE Baeume, siehe
        ''' <see cref="LocalizationService.ApplyToMaterialized"/>.</summary>
        Friend Sub OnLocalizedItemAttachedToVisualTree(sender As Object, e As Avalonia.VisualTreeAttachmentEventArgs)
            Dim itemRoot = TryCast(sender, Avalonia.Visual)
            If itemRoot IsNot Nothing Then LocalizationService.ApplyToMaterialized(itemRoot)
        End Sub

        ' ── Die Abschnitte entstehen zur Laufzeit ──────────────────────────────────
        '
        ' Die Seite traegt nur die Rahmen der Abschnitte (siehe SettingsView.axaml). Ihr Inhalt ist
        ' je Abschnitt ein eigenes UserControl unter Views/Settings. "Allgemein" entsteht mit der
        ' Seite, die uebrigen danach von oben nach unten, je Abschnitt ein Schritt mit
        ' Hintergrundvorrang: Eingabe und Zeichnen gehen vor, die Seite bleibt bedienbar. Vorher war
        ' es eine XAML von ueber viertausend Zeilen, die beim ersten Oeffnen auf einmal entstand,
        ' rund eine Sekunde, und die das Fenster deshalb zehn Sekunden nach dem Start vorab baute.
        '
        ' Von oben nach unten heisst: die Seite waechst nur UNTERHALB dessen, was man sieht, nichts
        ' springt. Wer vorgreift, bekommt das Noetige sofort: ein Klick links baut bis zu seinem
        ' Abschnitt, Rollen baut, was in Sicht kommt, die Suche und ENDE bauen den Rest.

        ''' <summary>Name des Rahmens in SettingsView.axaml und wie sein Inhalt entsteht, in der
        ''' Reihenfolge der Seite.</summary>
        Private Shared ReadOnly SectionFactories As (Name As String, Create As Func(Of Control))() = {
            ("GeneralSection", Function() New Settings.GeneralSectionView()),
            ("AppearanceSection", Function() New Settings.AppearanceSectionView()),
            ("GallerySection", Function() New Settings.GallerySectionView()),
            ("ViewerSection", Function() New Settings.ViewerSectionView()),
            ("InfoPanelSection", Function() New Settings.InfoPanelSectionView()),
            ("FilmstripSection", Function() New Settings.FilmstripSectionView()),
            ("EditorSection", Function() New Settings.EditorSectionView()),
            ("RawSection", Function() New Settings.RawSectionView()),
            ("ImmichSection", Function() New Settings.ImmichSectionView()),
            ("NextcloudSection", Function() New Settings.NextcloudSectionView()),
            ("ExternalProgramsSection", Function() New Settings.ExternalProgramsSectionView()),
            ("ModelsSection", Function() New Settings.ModelsSectionView()),
            ("MapSection", Function() New Settings.MapSectionView()),
            ("PeopleSection", Function() New Settings.PeopleSectionView()),
            ("CatalogSection", Function() New Settings.CatalogSectionView()),
            ("PerformanceSection", Function() New Settings.PerformanceSectionView()),
            ("ShortcutsSection", Function() New Settings.ShortcutsSectionView()),
            ("TechnologySection", Function() New Settings.TechnologySectionView())
        }

        ''' <summary>Wie viele Abschnitte von oben schon stehen. Gebaut wird immer der naechste, so
        ''' bleibt die Reihenfolge auch beim Vorziehen erhalten.</summary>
        Private _builtSectionCount As Integer = 0
        Private _sectionStepPosted As Boolean = False

        ''' <summary>Sind alle Abschnitte da? Fuer die Diagnose und die Suche.</summary>
        Public ReadOnly Property AllSectionsBuilt As Boolean
            Get
                Return _builtSectionCount >= SectionFactories.Length
            End Get
        End Property

        ''' <summary>Bis zum Bau ausgeblendete Rahmen. Ausgeblendet mit Animationsvorrang, wie die
        ''' Suche es tut (SettingsSearch.Hide): das schlaegt die gebundene Sichtbarkeit von Modelle und
        ''' Personen, ohne sie zu ersetzen, und mit dem Freigeben gilt sie wieder. Nur unsichtbar
        ''' (Deckkraft 0) reichte nicht: ein leerer Rahmen behielt Innenabstand, Rand und den Abstand
        ''' der Liste, siebzehn davon ergaben eine lange leere Strecke zum Hineinrollen.</summary>
        Private ReadOnly _hiddenUntilBuilt As New Dictionary(Of String, IDisposable)(StringComparer.Ordinal)

        ''' <summary>Baut den naechsten Abschnitt: Inhalt erzeugen, in seinen Rahmen setzen,
        ''' einblenden, uebersetzen.</summary>
        Private Sub BuildNextSection()
            If AllSectionsBuilt Then Return
            Dim entry = SectionFactories(_builtSectionCount)
            _builtSectionCount += 1
            Dim frame = Me.FindControl(Of Border)(entry.Name)
            If frame Is Nothing Then
                DiagnosticLogService.LogAlways("Settings.Section", $"Rahmen fehlt: {entry.Name}")
                Return
            End If
            Dim content As Control = Nothing
            PerformanceTraceService.Measure("Einstellungen: Abschnitt bauen",
                Sub()
                    content = entry.Create()
                    frame.Child = content
                    Dim hidden As IDisposable = Nothing
                    If _hiddenUntilBuilt.TryGetValue(entry.Name, hidden) Then
                        hidden?.Dispose()
                        _hiddenUntilBuilt.Remove(entry.Name)
                    End If
                    LocalizationService.ApplyTo(content)
                End Sub)
            ' Was erst beim Layout aus Vorlagen entsteht, kennt der Durchlauf oben noch nicht. Danach
            ' die Mac-Schreibweise der Tastennamen, in derselben Reihenfolge wie im Fenster
            ' (MainWindow.ApplyLocalization): der dortige Durchlauf lief, bevor es diesen Abschnitt gab.
            Dim built = content
            Avalonia.Threading.Dispatcher.UIThread.Post(
                Sub()
                    LocalizationService.ApplyToVisualTree(built)
                    PlatformShortcutService.ApplyMacPresentation(built)
                End Sub,
                Avalonia.Threading.DispatcherPriority.Loaded)
        End Sub

        ''' <summary>Baut bis einschliesslich des genannten Abschnitts, sofort.</summary>
        Private Sub EnsureSectionsBuiltThrough(sectionName As String)
            Dim index = Array.FindIndex(SectionFactories, Function(f) String.Equals(f.Name, sectionName, StringComparison.Ordinal))
            If index < 0 Then Return
            While _builtSectionCount <= index
                BuildNextSection()
            End While
        End Sub

        ''' <summary>Baut alle noch fehlenden Abschnitte, sofort. Fuer die Suche, die ueber die ganze
        ''' Seite filtert, fuer ENDE und fuer die Diagnose.</summary>
        Public Sub EnsureAllSectionsBuilt()
            While Not AllSectionsBuilt
                BuildNextSection()
            End While
        End Sub

        ''' <summary>Stellt den naechsten Schritt des Aufbaus an, mit Hintergrundvorrang.</summary>
        Private Sub ScheduleNextSectionStep()
            If AllSectionsBuilt OrElse _sectionStepPosted Then Return
            _sectionStepPosted = True
            Avalonia.Threading.Dispatcher.UIThread.Post(
                Sub()
                    _sectionStepPosted = False
                    BuildNextSection()
                    ScheduleNextSectionStep()
                End Sub,
                Avalonia.Threading.DispatcherPriority.Background)
        End Sub

        ''' <summary>Rollt jemand schneller, als gebaut wird, kommt in Sicht, was noch fehlt: dann
        ''' sofort bauen, bis die Unterkante des zuletzt gebauten sichtbaren Abschnitts unter dem
        ''' Rand des Sichtfensters (plus einer halben Hoehe Vorlauf) liegt. Die ungebauten Rahmen
        ''' sind ausgeblendet und haben keine Lage; gemessen wird deshalb am Gebauten.</summary>
        Private Sub OnSettingsScrollChanged(sender As Object, e As ScrollChangedEventArgs)
            ' Das Vermessen unten meldet selbst wieder ein ScrollChanged.
            If AllSectionsBuilt OrElse _fillingViewport Then Return
            Dim sv = TryCast(sender, ScrollViewer)
            If sv Is Nothing OrElse sv.Viewport.Height <= 0 Then Return
            Dim limit = sv.Viewport.Height * 1.5
            _fillingViewport = True
            Try
                While Not AllSectionsBuilt
                    If BuiltContentBottom(sv) > limit Then Exit While
                    BuildNextSection()
                    sv.UpdateLayout()
                End While
            Finally
                _fillingViewport = False
            End Try
        End Sub

        Private _fillingViewport As Boolean = False

        ''' <summary>Unterkante des zuletzt gebauten SICHTBAREN Abschnitts im Sichtfenster. Ein
        ''' gebauter, aber per Bindung verborgener (Modelle, Personen ohne Laufzeit) zaehlt nicht,
        ''' er hat keine Lage. 0, wenn noch keiner steht.</summary>
        Private Function BuiltContentBottom(sv As ScrollViewer) As Double
            For i = _builtSectionCount - 1 To 0 Step -1
                Dim frame = Me.FindControl(Of Border)(SectionFactories(i).Name)
                If frame Is Nothing OrElse Not frame.IsVisible Then Continue For
                Dim pt = Avalonia.VisualExtensions.TranslatePoint(frame, New Avalonia.Point(0, 0), sv)
                If pt.HasValue Then Return pt.Value.Y + frame.Bounds.Height
            Next
            Return 0
        End Function

        Private Sub InitializeSections()
            For Each entry In SectionFactories
                Dim frame = Me.FindControl(Of Border)(entry.Name)
                If frame Is Nothing Then Continue For
                Dim handle = frame.SetValue(Avalonia.Visual.IsVisibleProperty, False, Avalonia.Data.BindingPriority.Animation)
                If handle IsNot Nothing Then _hiddenUntilBuilt(entry.Name) = handle
            Next
            ' Der erste Abschnitt gehoert zum ersten Bild der Seite.
            BuildNextSection()
            Dim sv = Me.FindControl(Of ScrollViewer)("SettingsScrollViewer")
            If sv IsNot Nothing Then AddHandler sv.ScrollChanged, AddressOf OnSettingsScrollChanged
            ' Der Rest beginnt, sobald die Seite im Fenster haengt.
            AddHandler Me.AttachedToVisualTree, Sub(s, e) ScheduleNextSectionStep()
        End Sub

        Public Sub New()
            AvaloniaXamlLoader.Load(Me)
            InitializeSections()
            AddHandler Me.AttachedToVisualTree, AddressOf OnAttachedFocus
            AddHandler Loaded, AddressOf HandleLoaded
            ' Die Bildschirme kennt erst das offene Fenster, und sie koennen wechseln, waehrend die
            ' Seite verborgen ist. Deshalb beim Zeigen neu, und wenn ein Bildschirm kommt oder geht.
            AddHandler Me.PropertyChanged,
                Sub(s, e)
                    If e.Property Is IsVisibleProperty AndAlso IsVisible Then TryCast(DataContext, SettingsViewModel)?.RefreshDisplayProfiles()
                End Sub
            AddHandler DisplayProfileService.Changed,
                Sub(s, e) TryCast(DataContext, SettingsViewModel)?.RefreshDisplayProfiles()
        End Sub

        ''' <summary>Ein ICC-Profil fuer einen Bildschirm waehlen. Der Auswahldialog haengt am
        ''' TopLevel, deshalb hier und nicht im ViewModel.</summary>
        Friend Async Sub OnChooseDisplayProfileClick(sender As Object, e As RoutedEventArgs)
            Dim row = TryCast(TryCast(sender, Control)?.DataContext, DisplayProfileRow)
            Dim vm = TryCast(DataContext, SettingsViewModel)
            If row Is Nothing OrElse vm Is Nothing Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim files = Await topLevel.StorageProvider.OpenFilePickerAsync(New FilePickerOpenOptions With {
                    .Title = LocalizationService.T("ICC-Profil des Bildschirms wählen"),
                    .AllowMultiple = False,
                    .FileTypeFilter = {New FilePickerFileType("ICC") With {.Patterns = {"*.icc", "*.icm", "*.ICC", "*.ICM"}}}
                })
                Dim path = files?.FirstOrDefault()?.Path?.LocalPath
                If String.IsNullOrEmpty(path) Then Return
                vm.SetDisplayProfile(row.ScreenKey, path)
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.DisplayProfile", ex)
            End Try
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
                ResetSettingsScrollPosition()
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
            ' Gefiltert wird Zeile fuer Zeile im gebauten Baum; ein noch leerer Rahmen fiele als
            ' "kein Treffer" weg. Mit dem ersten Zeichen deshalb den Rest bauen.
            If Not String.IsNullOrWhiteSpace(query) Then EnsureAllSectionsBuilt()
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
            ' Auch die Bildschirmprofile: die Seite entsteht beim ersten Betreten bereits sichtbar,
            ' der Wechsel von IsVisible oben kommt dann nie. Ohne diesen Aufruf stand dort "Keine
            ' Bildschirme erkannt", bis das Fenster einmal verschoben wurde.
            vm?.RefreshDisplayProfiles()

        End Sub

        Friend Async Sub OnBrowseGalleryStartupFolderClick(sender As Object, e As RoutedEventArgs)
            Dim vm = TryCast(DataContext, SettingsViewModel)
            If vm Is Nothing Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Startordner der Galerie wählen"),
                    .AllowMultiple = False,
                    .SuggestedStartLocation = Await FolderPickerStart.ForPathAsync(topLevel, vm.GalleryStartupCustomFolder)
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
        Friend Async Sub OnBrowseOpenWithProgramClick(sender As Object, e As RoutedEventArgs)
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

        ''' <summary>Setzt den gewaehlten Ordner als Vorlage; Platzhalter haengt man danach selbst an.</summary>
        Friend Async Sub OnBrowseImportTargetClick(sender As Object, e As RoutedEventArgs)
            Dim row = TryCast(TryCast(sender, Control)?.DataContext, ImportTargetRow)
            If row Is Nothing Then Return
            e.Handled = True
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Zielordner wählen"),
                    .AllowMultiple = False,
                    .SuggestedStartLocation = Await FolderPickerStart.ForPathAsync(topLevel, CardImportService.FixedRootOf(row.Template))
                })
                Dim folder = folders?.FirstOrDefault()
                If folder Is Nothing Then Return
                Dim path = folder.Path.LocalPath
                If Not String.IsNullOrWhiteSpace(path) Then row.Template = path
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.BrowseImportTarget", ex)
            End Try
        End Sub

        Friend Async Sub OnBrowseGmicClick(sender As Object, e As RoutedEventArgs)
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
        Friend Async Sub OnAddCatalogWatchFolderClick(sender As Object, e As RoutedEventArgs)
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
        Friend Async Sub OnRelocateCatalogFolderClick(sender As Object, e As RoutedEventArgs)
            e.Handled = True
            Dim vm = TryCast(DataContext, SettingsViewModel)
            Dim oldPath = TryCast(TryCast(sender, MenuItem)?.Tag, String)
            If vm Is Nothing OrElse String.IsNullOrWhiteSpace(oldPath) Then Return
            Try
                Dim topLevel As TopLevel = TopLevel.GetTopLevel(Me)
                If topLevel Is Nothing Then Return
                Dim folders = Await topLevel.StorageProvider.OpenFolderPickerAsync(New FolderPickerOpenOptions With {
                    .Title = LocalizationService.T("Neuer Ort dieses Ordners"),
                    .AllowMultiple = False,
                    .SuggestedStartLocation = Await FolderPickerStart.ForPathAsync(topLevel, oldPath)
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
            ' Bis zum Ziel bauen und vermessen, sonst stuende der Abschnitt als flacher Rahmen
            ' dort, wo er ohne die Abschnitte darueber liegt.
            EnsureSectionsBuiltThrough(targetName)
            Me.FindControl(Of ScrollViewer)("SettingsScrollViewer")?.UpdateLayout()
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
        Friend Sub OnManagePeopleClick(sender As Object, e As RoutedEventArgs)
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
                    ' Das Ende ist erst das Ende, wenn alles steht.
                    EnsureAllSectionsBuilt()
                    sv.UpdateLayout()
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
