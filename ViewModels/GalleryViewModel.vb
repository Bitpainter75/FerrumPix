Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Linq
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia.Threading
Imports ReactiveUI
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace ViewModels

    Public Class GalleryViewModel
        Inherits ViewModelBase

        ''' Betrifft den Brotkrümelpfad, der mit langen Ordnernamen als Erstes in die
        ''' Suchleiste läuft.
        Protected Overrides ReadOnly Property ToolbarLabelWidthThreshold As Double
            Get
                Return 1050
            End Get
        End Property

        ''' <summary>Personen, Orte und Stichwörter tragen ihren Namen neben dem Symbol. Der Platz
        ''' dafür kommt aus dem Suchfeld, das bis zu seiner Mindestbreite mitschrumpft; erst danach
        ''' weichen die drei Namen. Die Schwelle liegt deutlich über der für die übrigen
        ''' Beschriftungen: die Namen sollen gehen, SOLANGE das Suchfeld noch brauchbar breit ist,
        ''' und nicht erst, wenn es schon an seiner Mindestbreite klebt.</summary>
        Protected Overrides ReadOnly Property FilterLabelWidthThreshold As Double
            Get
                Return 1550
            End Get
        End Property

        Private ReadOnly _mainVm As MainWindowViewModel
        Private _currentFolder As String = Nothing
        Private _selectedItem As ImageItem
        ' Die aktive Stichwortauswahl. Sie ueberlebt keinen Wechsel im Baum - siehe ClearTagFilter.
        Private _activeTagFilters As New List(Of String)()
        ''' <summary>Wohin "Auswahl aufheben" zurueckkehrt. Bei einem echten Ordner der Pfad,
        ''' bei einer virtuellen Ansicht (Immich-Album, Person, Ort, Suchliste) der Knoten - sonst
        ''' bliebe die Trefferliste stehen, obwohl nichts mehr ausgewaehlt ist.</summary>
        Private _nodeBeforeTagFilter As VirtualNavigationNode
        Private _folderBeforeTagFilter As String = ""
        Private _thumbnailSize As Double = 260
        Private _statusText As String = LocalizationService.T("Willkommen bei FerrumPix")
        Private _hoveredMetadataTitle As String = ""
        Private _hoveredMetadataText As String = ""
        Private _searchText As String = ""
        Private _sortMode As String = AppSettingsService.DefaultGallerySortMode
        Private _sortAscending As Boolean = AppSettingsService.DefaultGallerySortAscending
        Private _groupDateStep As String = AppSettingsService.DefaultGalleryGroupDateStep
        Private _showFolders As Boolean = True
        Private _showParentFolder As Boolean = True
        Private _ratingBadgesAlwaysVisible As Boolean = False
        Private _favoriteBadgeAlwaysVisible As Boolean = True
        Private _metadataBadgesAlwaysVisible As Boolean = True
        Private _viewMode As String = "Grid"
        Private _isLoading As Boolean
        Private _storageFreeText As String = ""
        Private _storageFillPercent As Double = 0
        Private _selectedFolderNode As FolderNode
        Private _selectedSearchNode As VirtualNavigationNode
        Private _selectedImmichNode As VirtualNavigationNode
        Private _selectedNextcloudNode As VirtualNavigationNode
        Private _clipboardPaths As New List(Of String)()
        Private _clipboardCut As Boolean
        Private ReadOnly _historyBack As New Stack(Of String)()
        Private ReadOnly _historyForward As New Stack(Of String)()
        Private _watcher As FileSystemWatcher
        Private _isVirtualFolder As Boolean
        Private _virtualFolderName As String = ""
        Private _pendingReload As Boolean = False
        ' Jede Dateisystem-Aenderung kann waehrend eines schon laufenden Abgleichs eintreffen.
        ' Die Marke trennt diese Laeufe: nur das zuletzt begonnene Ergebnis darf die gebundenen
        ' Collections anfassen. Ein Abbruch des Dateisystems selbst ist nicht immer moeglich
        ' (ein Netzwerk-Listing kann bereits im Kernel warten), aber sein Ergebnis wird dann
        ' wenigstens nicht mehr sichtbar.
        Private _folderSyncGeneration As Integer
        ' Nach dem Umbenennen soll das Ziel markiert sein. WELCHER Abgleichslauf das erledigt, steht
        ' aber nicht fest: das Umbenennen loest selbst Watcher-Ereignisse aus, und die
        ' Generationsmarke laesst nur den zuletzt begonnenen Lauf die Anzeige anfassen. Wer auf
        ' "seinen" Lauf wartet, wartet deshalb womoeglich auf einen, der stillschweigend abbricht -
        ' und liest danach die alte Liste. Der Wunsch wird darum hier hinterlegt und von dem Lauf
        ' eingeloest, der tatsaechlich uebernimmt.
        Private _pendingSelectionPaths As HashSet(Of String)
        Private _filterFavorite As String = "All"
        Private ReadOnly _filterRatings As New HashSet(Of Integer)()
        ''' Farbetikett-Filter (Mehrfachauswahl). Bewusst NICHT persistiert: Etiketten sind
        ''' Arbeits-Markierungen - ein vergessener, mitgespeicherter Filter würde Wochen später
        ''' still Bilder verstecken.
        Private ReadOnly _filterColorLabels As New HashSet(Of String)(StringComparer.Ordinal)
        Private _filterFileType As String = "All"
        Private _isCollageDialogOpen As Boolean
        Private _collageBaseName As String = "Collage"
        Private _collageFormat As String = "JPG"
        Private _collageWidth As Integer = 2400
        Private _collageColumns As Integer = 3
        Private _collageGap As Integer = 24
        Private _collageMargin As Integer = 48
        Private _collageBackgroundColor As String = "#FFFFFFFF"
        Private _collageQuality As Integer = 90
        Private _collageLayoutMode As String = "Grid"
        Private _collageHeroIndex As Integer = 0
        Private _collageHeroPosition As String = "Left"
        Private _collageRandomSeed As Integer = 0
        Private _collageOrderSeed As Integer? = Nothing
        Private _collagePreviewZoom As Double = 1.0
        Private _collagePreviewImage As Avalonia.Media.Imaging.Bitmap
        Private _collagePreviewRequestId As Integer
        Private ReadOnly _collagePreviewTimer As DispatcherTimer
        Private ReadOnly _searchDebounceTimer As DispatcherTimer
        Private ReadOnly _backgroundWorkTimer As DispatcherTimer
        Private _thumbnailLoadCts As New CancellationTokenSource()
        Private _activeSearchCts As CancellationTokenSource
        Private ReadOnly _virtualPathSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _savedSearches As New List(Of SearchListEntry)()
        ' Kanonische Liste, siehe RawPreviewService.SupportedExtensions.
        Private ReadOnly _rawExtensions As String() = RawPreviewService.SupportedExtensions

        Public Event RequestScrollToItem As EventHandler

        Public Property FolderTree As ObservableCollection(Of FolderNode)
        ''' <summary>Suchlisten des Ordner-Tabs (Source="Local") - inklusive "Neue Suche".</summary>
        Public Property SearchTree As ObservableCollection(Of VirtualNavigationNode)
        ''' <summary>Suchlisten des Immich-Tabs (Source="Immich"). Die Suchen sind nach Quelle
        ''' getrennt, weil sie im jeweiligen Tab neben ihrem Baum stehen sollen - eine
        ''' Dateisystem-Suche gehoert nicht neben die Immich-Alben und umgekehrt.</summary>
        Public Property ImmichSearchTree As ObservableCollection(Of VirtualNavigationNode)
        ''' <summary>Suchlisten des Nextcloud-Tabs (Source="Nextcloud").</summary>
        Public Property NextcloudSearchTree As ObservableCollection(Of VirtualNavigationNode)
        ''' <summary>Favoriten-Tab: frei zusammengestellte Verweise auf Ordner, Immich-Knoten und
        ''' Suchlisten (siehe FavoritesService). Die Knoten sind vollwertige Navigationsknoten -
        ''' ein Klick tut genau dasselbe wie im Herkunfts-Tab.</summary>
        Public Property FavoritesTree As ObservableCollection(Of VirtualNavigationNode)
        ''' <summary>Eigener Immich-Bereich im Navigationsbereich (getrennt von der Suche): der
        ''' „Alle Fotos"-Knoten plus je ein Knoten pro Album.</summary>
        Public Property ImmichTree As ObservableCollection(Of VirtualNavigationNode)
        Public Property NextcloudTree As ObservableCollection(Of VirtualNavigationNode)
        Public Property Items As BulkObservableCollection(Of ImageItem)
        Public Property DisplayItems As BulkObservableCollection(Of ImageItem)
        Public Property SelectedItems As ObservableCollection(Of ImageItem)
        Public ReadOnly Property CollageFormatOptions As ObservableCollection(Of String) = New ObservableCollection(Of String) From {"JPG", "PNG", "WEBP", "PDF", "FPX"}

        Public Property CurrentFolder As String
            Get
                Return _currentFolder
            End Get
            Set(value As String)
                If String.Equals(_currentFolder, value, StringComparison.OrdinalIgnoreCase) Then Return
                Me.RaiseAndSetIfChanged(_currentFolder, value)
                Me.RaisePropertyChanged(NameOf(CurrentFolderName))
                Me.RaisePropertyChanged(NameOf(BreadcrumbParent))
                Me.RaisePropertyChanged(NameOf(HasBreadcrumbParent))
                If Not String.IsNullOrWhiteSpace(value) AndAlso Directory.Exists(value) Then
                    ' Nur merken; geschrieben wird der Ordner gesammelt beim Schließen der App.
                    AppSettingsService.RememberLastGalleryFolder(value)
                End If
            End Set
        End Property

        Public ReadOnly Property CurrentFolderName As String
            Get
                If _isVirtualFolder Then Return _virtualFolderName
                If String.IsNullOrEmpty(_currentFolder) Then Return "—"
                Dim name = IO.Path.GetFileName(_currentFolder.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
                If String.IsNullOrEmpty(name) Then Return _currentFolder
                Return name
            End Get
        End Property

        Public ReadOnly Property BreadcrumbParent As String
            Get
                If _isVirtualFolder Then Return ""
                If String.IsNullOrEmpty(_currentFolder) Then Return ""
                Dim parent = IO.Path.GetDirectoryName(_currentFolder)
                If String.IsNullOrEmpty(parent) Then Return ""
                Return IO.Path.GetFileName(parent)
            End Get
        End Property

        Public ReadOnly Property HasBreadcrumbParent As Boolean
            Get
                Return Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(BreadcrumbParent)
            End Get
        End Property

        Public ReadOnly Property IsVirtualFolder As Boolean
            Get
                Return _isVirtualFolder
            End Get
        End Property

        Public Property SelectedItem As ImageItem
            Get
                Return _selectedItem
            End Get
            Set(value As ImageItem)
                Dim oldSelected = _selectedItem
                If oldSelected IsNot Nothing Then
                    oldSelected.IsNavigationSelected = False
                    RemoveHandler oldSelected.PropertyChanged, AddressOf OnSelectedItemPropertyChanged
                End If
                Me.RaiseAndSetIfChanged(_selectedItem, value)
                If _selectedItem IsNot Nothing Then
                    AddHandler _selectedItem.PropertyChanged, AddressOf OnSelectedItemPropertyChanged
                End If
                Me.RaisePropertyChanged(NameOf(SelectionText))
                Me.RaisePropertyChanged(NameOf(FooterStatusText))
                Me.RaisePropertyChanged(NameOf(SelectedRating))
                Me.RaisePropertyChanged(NameOf(HasSelectedImage))
                Me.RaisePropertyChanged(NameOf(HasSelection))
                Me.RaisePropertyChanged(NameOf(SelectedIsFavorite))
                UpdateInfoPanelTarget()
            End Set
        End Property

        ''' <summary>Das Infopanel folgt der Auswahl: ein Bild zeigt seine Aufnahmedaten, mehrere
        ''' eine Uebersicht. Ordner und die Zeile zum uebergeordneten Ordner haben keine Angaben -
        ''' dann steht nur ein Hinweis in der Mitte.</summary>
        Public ReadOnly Property IsInfoPanelActive As Boolean
            Get
                Return InfoPanelTargets().Count > 0
            End Get
        End Property

        Private Function InfoPanelTargets() As IList(Of ImageItem)
            If SelectedItems Is Nothing Then Return New List(Of ImageItem)()
            Return SelectedItems.Where(Function(i) i IsNot Nothing AndAlso
                                                   Not i.IsFolder AndAlso Not i.IsParentFolderEntry).ToList()
        End Function

        Private Sub UpdateInfoPanelTarget()
            Dim targets = InfoPanelTargets()
            ' EIN Satz fuer beide Faelle - keine Auswahl oder nur Ordner. Er trifft auf beide zu,
            ' und ein Hinweisfeld ist kein Ort fuer Erklaerungen.
            If targets.Count = 0 Then
                InfoPanel.InfoPlaceholderText = LocalizationService.T("Kein einzelnes Bild ausgewählt")
            End If
            InfoPanel.ShowItems(targets)
            Me.RaisePropertyChanged(NameOf(IsInfoPanelActive))
        End Sub

        ''' <summary>Das Infopanel der Galerie. Eigener Zustand, damit es beim Blaettern nicht die
        ''' Daten des vorherigen Bildes stehen laesst.</summary>
        Public ReadOnly Property InfoPanel As New InfoPanelViewModel()

        ''' <summary>Ob die Info-Leiste offen ist. Wie in Betrachter und Editor liegt der Zustand in
        ''' den Einstellungen - er ueberlebt damit den Programmstart und laesst sich dort setzen.</summary>
        Public ReadOnly Property IsInfoSidebarVisible As Boolean
            Get
                Return _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing AndAlso
                       _mainVm.Settings.GalleryInfoSidebarExpanded
            End Get
        End Property

        ''' <summary>Ob die Fusszeile sichtbar ist. Aus den Einstellungen, je Bereich getrennt.
        ''' Ausgeblendet gewinnt die Galerie deren Hoehe fuer die Kacheln; die Aktionen der Zeile
        ''' stehen ohnehin auch im Kontextmenue eines Bildes.</summary>
        Public ReadOnly Property ShowFooter As Boolean
            Get
                Return _mainVm Is Nothing OrElse _mainVm.Settings Is Nothing OrElse
                       _mainVm.Settings.GalleryShowFooter
            End Get
        End Property

        Public Sub ToggleInfoSidebar()
            If _mainVm Is Nothing OrElse _mainVm.Settings Is Nothing Then Return
            _mainVm.Settings.GalleryInfoSidebarExpanded = Not _mainVm.Settings.GalleryInfoSidebarExpanded
            RefreshInfoSidebarState()
        End Sub

        ''' <summary>Nach einer Aenderung in den Einstellungen den Stand nachziehen.</summary>
        Public Sub RefreshInfoSidebarState()
            Me.RaisePropertyChanged(NameOf(IsInfoSidebarVisible))
            InfoPanel.IsInfoSidebarVisible = IsInfoSidebarVisible
        End Sub

        ''' <summary>Der Ort des Analysebildes wurde umgestellt. Die Galerie kennt nur die Leiste:
        ''' entweder nachrechnen oder das vorhandene Bild loswerden.</summary>
        Friend Sub RefreshScopeAfterPlacementChange()
            If ScopeSelectionViewModel.ShowInInfoSidebar Then
                InfoPanel.Refresh()
            Else
                InfoPanel.ScopeImage = Nothing
            End If
        End Sub

        ''' <summary>Meldet den Wechsel der Ansicht. Steht die Galerie im Hintergrund, weil
        ''' Betrachter oder Editor vorne sind, faellt jede teure Arbeit der Leiste aus; das Panel
        ''' holt sie beim Zurueckkommen selbst nach.</summary>
        Friend Sub SetViewActive(active As Boolean)
            InfoPanel.IsOwnerViewActive = active
        End Sub

        Private Sub OnSelectedItemPropertyChanged(sender As Object, e As ComponentModel.PropertyChangedEventArgs)
            If e.PropertyName = NameOf(ImageItem.Rating) Then
                Me.RaisePropertyChanged(NameOf(SelectedRating))
            ElseIf e.PropertyName = NameOf(ImageItem.IsFavorite) Then
                Me.RaisePropertyChanged(NameOf(SelectedIsFavorite))
            End If
        End Sub

        Public Property ThumbnailSize As Double
            Get
                Return _thumbnailSize
            End Get
            Set(value As Double)
                value = AppSettingsService.NormalizeThumbnailSize(value)
                If Math.Abs(_thumbnailSize - value) < 0.01 Then Return
                Me.RaiseAndSetIfChanged(_thumbnailSize, value)
                Me.RaisePropertyChanged(NameOf(ThumbnailImageHeight))
                Me.RaisePropertyChanged(NameOf(GridItemSlotHeight))
                Me.RaisePropertyChanged(NameOf(GridColumnPitch))
                ' Die Kopfzeile der Gruppenansicht ist genau eine Kachelzeile breit. Bleibt die
                ' Spaltenzahl gleich und aendert sich nur die Kachelgroesse, meldet das sonst niemand.
                Me.RaisePropertyChanged(NameOf(GroupHeaderWidth))
                Me.RaisePropertyChanged(NameOf(TileHasRoomForDetails))
                AppSettingsService.SaveGalleryThumbnailSize(value)
            End Set
        End Property

        ''' <summary>Ab welcher Kachelbreite Metadaten-Abzeichen und Dateidatum noch sinnvoll
        ''' hineinpassen. Darunter überlagern die 32-px-Abzeichen das halbe Bild und das Datum wird auf
        ''' wenige Zeichen abgeschnitten - dann bleiben beide weg.
        ''' Der Regler geht von 140 bis 520; 200 liegt knapp über den kleinsten Stufen.</summary>
        Public Const TileDetailsMinWidth As Double = 200

        Public ReadOnly Property TileHasRoomForDetails As Boolean
            Get
                Return _thumbnailSize >= TileDetailsMinWidth
            End Get
        End Property

        Public ReadOnly Property ThumbnailImageHeight As Double
            Get
                Return Math.Max(104, _thumbnailSize * 0.74)
            End Get
        End Property

        ' Zusätzliche Höhe pro Grid-Zelle über dem reinen Thumbnail-Bild hinaus: die feste
        ' Label-Zeile (RowDefinitions="Auto,68" in GalleryView.axaml), die Rahmenstärke der Karte
        ' (Border.thumb-card: BorderThickness="2" in FerrumPixTheme.axaml, oben+unten = 4) und das
        ' WrapPanel-Margin der Karte (Margin="5", oben+unten = 10). Muss mit dem tatsächlichen XAML
        ' übereinstimmen - sonst driftet die virtualisierte Scroll-Berechnung mit der Scrolltiefe
        ' immer weiter auseinander (einzige Quelle für beide, damit sie nicht wieder auseinanderlaufen).
        ' SCHÄTZWERT für die virtualisierte Scroll-Rechnung. Die Kachel selbst misst ihre
        ' Beschriftungszeile inzwischen selbst (RowDefinitions="Auto,Auto"), damit unter dem Text kein
        ' Leerstreifen bleibt und größere Schrift nicht klemmt. Der Wert hier bildet denselben Inhalt
        ' nach: 2x9 Innenabstand + Name (FP.Font.ItemTitle 13) + 7 Abstand + Detailzeile
        ' (FP.Font.Body 12), Zeilenhöhe rund das 1,35-fache der Schriftgröße.
        ' Stand hier vorübergehend 92, während das XAML bei 68 blieb: die Karte endete dadurch 24 px
        ' über der Unterkante ihres Slots - sichtbar als großer Abstand unter jeder Kachel, und die
        ' virtualisierte Scroll-Rechnung driftete mit der Scrolltiefe.
        Private Const GridItemLabelRowHeight As Double = 59
        Private Const GridItemCardBorderHeight As Double = 4
        Private Const GridItemCardMarginHeight As Double = 10

        Public ReadOnly Property GridItemSlotHeight As Double
            Get
                Return ThumbnailImageHeight + GridItemLabelRowHeight + GridItemCardBorderHeight + GridItemCardMarginHeight
            End Get
        End Property

        ' Zusätzliche Breite pro Spalte über die reine Thumbnail-Breite hinaus: nur das
        ' WrapPanel-Margin (links+rechts = 10) - die Rahmenstärke wird INNERHALB der explizit
        ' gesetzten Breite gezeichnet (Border.Width ist gebunden), kommt also nicht zusätzlich dazu.
        Private Const GridColumnMarginWidth As Double = 10

        Public ReadOnly Property GridColumnPitch As Double
            Get
                Return ThumbnailSize + GridColumnMarginWidth
            End Get
        End Property

        Public Property StatusText As String
            Get
                Return _statusText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_statusText, value)
                ' Ein Suchlauf schreibt seinen Stand ohnehin hierher ("Suche läuft... 1.234 Bilder").
                ' Der Balken oben zeigt denselben Satz, statt ihn an einem Dutzend Stellen im
                ' Suchlauf ein zweites Mal zu setzen.
                If _searchRunRow IsNot Nothing Then _searchRunRow.Text = value
            End Set
        End Property

        ''' Für das Gallery-weite Metadaten-Hover-Overlay (rechts oben im Gallery-Fenster, nicht am
        ''' Thumbnail) - wird von OnMetadataBadgePointerEntered/-Exited in GalleryView.axaml.vb gesetzt.
        Public Property HoveredMetadataTitle As String
            Get
                Return _hoveredMetadataTitle
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_hoveredMetadataTitle, value)
            End Set
        End Property

        Public Property HoveredMetadataText As String
            Get
                Return _hoveredMetadataText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_hoveredMetadataText, value)
            End Set
        End Property

        Public Property SearchText As String
            Get
                Return _searchText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_searchText, value)
                ' Entprellt: siehe _searchDebounceTimer. Das Leeren des Feldes (Abbrechen-Knopf, Ordnerwechsel)
                ' filtert sofort - dort wartet niemand auf weitere Tastendrücke, und eine sichtbare
                ' Verzögerung beim Zurücksetzen wirkt wie ein Hänger.
                _searchDebounceTimer.Stop()
                If String.IsNullOrEmpty(_searchText) Then
                    FilterAndSort()
                Else
                    _searchDebounceTimer.Start()
                End If
            End Set
        End Property

        Public Property SortMode As String
            Get
                Return _sortMode
            End Get
            Set(value As String)
                value = AppSettingsService.NormalizeGallerySortMode(value)
                If _sortMode = value Then Return
                Me.RaiseAndSetIfChanged(_sortMode, value)
                Me.RaisePropertyChanged(NameOf(SortLabel))
                Me.RaisePropertyChanged(NameOf(IsGroupDateStepVisible))
                RaiseSortStateChanged()
                ' Die Gruppen entstehen aus der Sortierung; eine andere Sortierung heisst andere Gruppen,
                ' auch wenn dieselben Bilder in derselben Reihenfolge stehen bleiben.
                InvalidateGroupLayout()
                FilterAndSort()
                AppSettingsService.SaveGallerySort(_sortMode, _sortAscending)
            End Set
        End Property

        Public Property SortAscending As Boolean
            Get
                Return _sortAscending
            End Get
            Set(value As Boolean)
                If _sortAscending = value Then Return
                Me.RaiseAndSetIfChanged(_sortAscending, value)
                Me.RaisePropertyChanged(NameOf(SortLabel))
                RaiseSortStateChanged()
                FilterAndSort()
                AppSettingsService.SaveGallerySort(_sortMode, _sortAscending)
            End Set
        End Property

        ''' <summary>Wonach sortiert wird - OHNE die Richtung. Die steht als Pfeil in der
        ''' Akzentfarbe daneben im Knopf: sie ist damit auf einen Blick da statt am Ende eines
        ''' Textes, der bei den laengeren Sortierarten ohnehin abgeschnitten wurde.</summary>
        Public ReadOnly Property SortLabel As String
            Get
                Dim modeLabel As String
                Select Case _sortMode
                    Case "FileCreatedAt" : modeLabel = LocalizationService.T("Erstellt (Datei)")
                    Case "FileModifiedAt" : modeLabel = LocalizationService.T("Geändert (Datei)")
                    Case "ExifDateTaken" : modeLabel = LocalizationService.T("Aufgenommen (EXIF)")
                    Case "ExifDateModified" : modeLabel = LocalizationService.T("Geändert (EXIF)")
                    Case "Width" : modeLabel = LocalizationService.T("Bildbreite")
                    Case "Height" : modeLabel = LocalizationService.T("Bildhöhe")
                    Case "Camera" : modeLabel = LocalizationService.T("Kamera")
                    Case "Iso" : modeLabel = LocalizationService.T("ISO")
                    Case "Aperture" : modeLabel = LocalizationService.T("Blende")
                    Case "Size" : modeLabel = LocalizationService.T("Größe")
                    Case "Type" : modeLabel = LocalizationService.T("Typ")
                    Case "Rating" : modeLabel = LocalizationService.T("Bewertung")
                    Case "Favorite" : modeLabel = LocalizationService.T("Favorit")
                    Case Else : modeLabel = LocalizationService.T("Name")
                End Select

                Return modeLabel
            End Get
        End Property

        Public ReadOnly Property IsSortName As Boolean
            Get
                Return String.Equals(_sortMode, "Name", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortFileCreatedAt As Boolean
            Get
                Return String.Equals(_sortMode, "FileCreatedAt", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortFileModifiedAt As Boolean
            Get
                Return String.Equals(_sortMode, "FileModifiedAt", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortExifDateTaken As Boolean
            Get
                Return String.Equals(_sortMode, "ExifDateTaken", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortExifDateModified As Boolean
            Get
                Return String.Equals(_sortMode, "ExifDateModified", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortWidth As Boolean
            Get
                Return String.Equals(_sortMode, "Width", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortHeight As Boolean
            Get
                Return String.Equals(_sortMode, "Height", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortCamera As Boolean
            Get
                Return String.Equals(_sortMode, "Camera", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortIso As Boolean
            Get
                Return String.Equals(_sortMode, "Iso", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortAperture As Boolean
            Get
                Return String.Equals(_sortMode, "Aperture", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortSize As Boolean
            Get
                Return String.Equals(_sortMode, "Size", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortType As Boolean
            Get
                Return String.Equals(_sortMode, "Type", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortRating As Boolean
            Get
                Return String.Equals(_sortMode, "Rating", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortFavorite As Boolean
            Get
                Return String.Equals(_sortMode, "Favorite", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSortAscending As Boolean
            Get
                Return _sortAscending
            End Get
        End Property

        Public ReadOnly Property IsSortDescending As Boolean
            Get
                Return Not _sortAscending
            End Get
        End Property

        Private Sub RaiseSortStateChanged()
            For Each propertyName In {
                NameOf(IsSortName),
                NameOf(IsSortFileCreatedAt),
                NameOf(IsSortFileModifiedAt),
                NameOf(IsSortExifDateTaken),
                NameOf(IsSortExifDateModified),
                NameOf(IsSortWidth),
                NameOf(IsSortHeight),
                NameOf(IsSortCamera),
                NameOf(IsSortIso),
                NameOf(IsSortAperture),
                NameOf(IsSortSize),
                NameOf(IsSortType),
                NameOf(IsSortRating),
                NameOf(IsSortFavorite),
                NameOf(IsSortAscending),
                NameOf(IsSortDescending)
            }
                Me.RaisePropertyChanged(propertyName)
            Next
        End Sub

        Public Property FilterFavorite As String
            Get
                Return _filterFavorite
            End Get
            Set(value As String)
                If _filterFavorite = value Then Return
                Me.RaiseAndSetIfChanged(_filterFavorite, value)
                Me.RaisePropertyChanged(NameOf(IsFilterFavoriteAll))
                Me.RaisePropertyChanged(NameOf(IsFilterFavoriteOnly))
                Me.RaisePropertyChanged(NameOf(HasActiveFilter))
                Me.RaisePropertyChanged(NameOf(FilterLabel))
                FilterAndSort()
                SaveGalleryFilters()
            End Set
        End Property

        Private Sub ToggleFilterRating(value As Integer)
            If value < 0 Then
                If _filterRatings.Count = 0 Then Return
                _filterRatings.Clear()
            Else
                value = Math.Max(0, Math.Min(5, value))
                If _filterRatings.Contains(value) Then
                    _filterRatings.Remove(value)
                Else
                    _filterRatings.Add(value)
                End If
            End If
            RaiseFilterRatingStateChanged()
            FilterAndSort()
            SaveGalleryFilters()
        End Sub

        Private Sub SaveGalleryFilters()
            AppSettingsService.SaveGalleryFilters(_filterFavorite, _filterRatings, _filterFileType)
        End Sub

        Private Sub ToggleFilterColorLabel(value As String)
            If String.IsNullOrEmpty(value) Then
                If _filterColorLabels.Count = 0 Then Return
                _filterColorLabels.Clear()
            Else
                If Not _filterColorLabels.Add(value) Then _filterColorLabels.Remove(value)
            End If
            RaiseFilterColorLabelStateChanged()
            FilterAndSort()
        End Sub

        Private Sub RaiseFilterColorLabelStateChanged()
            For Each name In {
                NameOf(IsFilterLabelAll),
                NameOf(IsFilterLabelOrange),
                NameOf(IsFilterLabelRed),
                NameOf(IsFilterLabelPink),
                NameOf(IsFilterLabelPurple),
                NameOf(IsFilterLabelBlue),
                NameOf(IsFilterLabelCyan),
                NameOf(IsFilterLabelTeal),
                NameOf(IsFilterLabelGreen),
                NameOf(IsFilterLabelYellow),
                NameOf(HasActiveFilter),
                NameOf(FilterLabel)
            }
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        ' Die Etikett-Werte sind die HEX-Farben der Akzentfarben-Palette aus den Einstellungen
        ' (SettingsView-Swatches) - eine Palette, eine Wahrheit.
        Public ReadOnly Property IsFilterLabelAll As Boolean
            Get
                Return _filterColorLabels.Count = 0
            End Get
        End Property
        Public ReadOnly Property IsFilterLabelOrange As Boolean
            Get
                Return _filterColorLabels.Contains("#F08A1A")
            End Get
        End Property
        Public ReadOnly Property IsFilterLabelRed As Boolean
            Get
                Return _filterColorLabels.Contains("#E74C3C")
            End Get
        End Property
        Public ReadOnly Property IsFilterLabelPink As Boolean
            Get
                Return _filterColorLabels.Contains("#F03B88")
            End Get
        End Property
        Public ReadOnly Property IsFilterLabelPurple As Boolean
            Get
                Return _filterColorLabels.Contains("#8B5CF6")
            End Get
        End Property
        Public ReadOnly Property IsFilterLabelBlue As Boolean
            Get
                Return _filterColorLabels.Contains("#3B82F6")
            End Get
        End Property
        Public ReadOnly Property IsFilterLabelCyan As Boolean
            Get
                Return _filterColorLabels.Contains("#0891B2")
            End Get
        End Property
        Public ReadOnly Property IsFilterLabelTeal As Boolean
            Get
                Return _filterColorLabels.Contains("#0F766E")
            End Get
        End Property
        Public ReadOnly Property IsFilterLabelGreen As Boolean
            Get
                Return _filterColorLabels.Contains("#22C55E")
            End Get
        End Property
        ''' Gelb kam mit dem XMP-Sidecar-Import dazu: xmp:Label="Yellow" ist eines der fünf Etiketten,
        ''' die andere Programme vergeben. Ohne diesen Filter landeten importierte Fotos in einer Markierung,
        ''' nach der man nicht filtern kann.
        Public ReadOnly Property IsFilterLabelYellow As Boolean
            Get
                Return _filterColorLabels.Contains("#FACC15")
            End Get
        End Property

        Private Sub RaiseFilterRatingStateChanged()
            For Each name In {
                NameOf(IsFilterRatingAll),
                NameOf(IsFilterRatingUnrated),
                NameOf(IsFilterRating1Plus),
                NameOf(IsFilterRating2Plus),
                NameOf(IsFilterRating3Plus),
                NameOf(IsFilterRating4Plus),
                NameOf(IsFilterRating5),
                NameOf(HasActiveFilter),
                NameOf(FilterLabel)
            }
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        Public Property FilterFileType As String
            Get
                Return _filterFileType
            End Get
            Set(value As String)
                If _filterFileType = value Then Return
                Me.RaiseAndSetIfChanged(_filterFileType, value)
                Me.RaisePropertyChanged(NameOf(IsFilterTypeAll))
                Me.RaisePropertyChanged(NameOf(IsFilterTypeRaw))
                Me.RaisePropertyChanged(NameOf(IsFilterTypeNonRaw))
                Me.RaisePropertyChanged(NameOf(HasActiveFilter))
                Me.RaisePropertyChanged(NameOf(FilterLabel))
                FilterAndSort()
                SaveGalleryFilters()
            End Set
        End Property

        Public ReadOnly Property IsFilterFavoriteAll As Boolean
            Get
                Return _filterFavorite = "All"
            End Get
        End Property
        Public ReadOnly Property IsFilterFavoriteOnly As Boolean
            Get
                Return _filterFavorite = "Only"
            End Get
        End Property
        Public ReadOnly Property IsFilterRatingAll As Boolean
            Get
                Return _filterRatings.Count = 0
            End Get
        End Property
        Public ReadOnly Property IsFilterRatingUnrated As Boolean
            Get
                Return _filterRatings.Contains(0)
            End Get
        End Property
        Public ReadOnly Property IsFilterRating1Plus As Boolean
            Get
                Return _filterRatings.Contains(1)
            End Get
        End Property
        Public ReadOnly Property IsFilterRating2Plus As Boolean
            Get
                Return _filterRatings.Contains(2)
            End Get
        End Property
        Public ReadOnly Property IsFilterRating3Plus As Boolean
            Get
                Return _filterRatings.Contains(3)
            End Get
        End Property
        Public ReadOnly Property IsFilterRating4Plus As Boolean
            Get
                Return _filterRatings.Contains(4)
            End Get
        End Property
        Public ReadOnly Property IsFilterRating5 As Boolean
            Get
                Return _filterRatings.Contains(5)
            End Get
        End Property
        Public ReadOnly Property IsFilterTypeAll As Boolean
            Get
                Return _filterFileType = "All"
            End Get
        End Property
        Public ReadOnly Property IsFilterTypeRaw As Boolean
            Get
                Return _filterFileType = "Raw"
            End Get
        End Property
        Public ReadOnly Property IsFilterTypeNonRaw As Boolean
            Get
                Return _filterFileType = "NonRaw"
            End Get
        End Property

        Public ReadOnly Property HasActiveFilter As Boolean
            Get
                Return _filterFavorite <> "All" OrElse _filterRatings.Count > 0 OrElse
                       _filterFileType <> "All" OrElse _filterColorLabels.Count > 0
            End Get
        End Property

        Public ReadOnly Property FilterLabel As String
            Get
                Return "Filter" & If(HasActiveFilter, " •", "")
            End Get
        End Property

        ''' <summary>Favorit, Bewertung, Etikett und Dateityp zurueck auf "alles zeigen".
        '''
        ''' Eine eigene Methode und nicht nur der Rumpf des Befehls: sie haengt am Eintrag im Menue
        ''' UND am Mausradklick auf den Knopf, und zwei Wege sollen nicht zwei Fassungen bedeuten.</summary>
        Public Sub ClearFilters()
            _filterFavorite = "All"
            _filterRatings.Clear()
            _filterFileType = "All"
            _filterColorLabels.Clear()
            For Each name In {NameOf(IsFilterFavoriteAll), NameOf(IsFilterFavoriteOnly),
                              NameOf(IsFilterRatingAll), NameOf(IsFilterRatingUnrated),
                              NameOf(IsFilterRating1Plus), NameOf(IsFilterRating2Plus),
                              NameOf(IsFilterRating3Plus), NameOf(IsFilterRating4Plus),
                              NameOf(IsFilterRating5),
                              NameOf(IsFilterTypeAll), NameOf(IsFilterTypeRaw), NameOf(IsFilterTypeNonRaw),
                              NameOf(IsFilterLabelAll), NameOf(IsFilterLabelOrange), NameOf(IsFilterLabelRed),
                              NameOf(IsFilterLabelPink), NameOf(IsFilterLabelPurple), NameOf(IsFilterLabelBlue),
                              NameOf(IsFilterLabelCyan), NameOf(IsFilterLabelTeal), NameOf(IsFilterLabelGreen),
                              NameOf(IsFilterLabelYellow),
                              NameOf(HasActiveFilter), NameOf(FilterLabel)}
                Me.RaisePropertyChanged(name)
            Next
            FilterAndSort()
            SaveGalleryFilters()
        End Sub

        ''' <summary>Sortierung zurueck auf den Standard: Name, aufsteigend.
        '''
        ''' Steht der Standard schon, passiert nichts - beide Eigenschaften vergleichen vor dem
        ''' Setzen, es faellt also weder ein Neusortieren noch ein Schreiben der Einstellung an.</summary>
        Public Sub ResetSort()
            SortMode = AppSettingsService.DefaultGallerySortMode
            SortAscending = AppSettingsService.DefaultGallerySortAscending
        End Sub

        Public Property IsCollageDialogOpen As Boolean
            Get
                Return _isCollageDialogOpen
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isCollageDialogOpen, value)
            End Set
        End Property

        Public Property CollageBaseName As String
            Get
                Return _collageBaseName
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_collageBaseName, If(value, "Collage"))
            End Set
        End Property

        Public Property CollageFormat As String
            Get
                Return _collageFormat
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_collageFormat, If(String.IsNullOrWhiteSpace(value), "JPG", value.ToUpperInvariant()))
                Me.RaisePropertyChanged(NameOf(IsCollageJpgQualityVisible))
            End Set
        End Property

        Public Property CollageWidth As Integer
            Get
                Return _collageWidth
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_collageWidth, Math.Max(640, Math.Min(12000, value)))
                ScheduleCollagePreviewUpdate()
            End Set
        End Property

        Public Property CollageColumns As Integer
            Get
                Return _collageColumns
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_collageColumns, Math.Max(1, Math.Min(12, value)))
                ScheduleCollagePreviewUpdate()
            End Set
        End Property

        Public Property CollageGap As Integer
            Get
                Return _collageGap
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_collageGap, Math.Max(0, Math.Min(400, value)))
                ScheduleCollagePreviewUpdate()
            End Set
        End Property

        Public Property CollageMargin As Integer
            Get
                Return _collageMargin
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_collageMargin, Math.Max(0, Math.Min(800, value)))
                ScheduleCollagePreviewUpdate()
            End Set
        End Property

        Public Property CollageBackgroundColor As String
            Get
                Return _collageBackgroundColor
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_collageBackgroundColor, AppSettingsService.NormalizeHexColor(value, "#FFFFFFFF"))
                Me.RaisePropertyChanged(NameOf(CollageBackgroundColorValue))
                Me.RaisePropertyChanged(NameOf(CollageBackgroundBrush))
                ScheduleCollagePreviewUpdate()
            End Set
        End Property

        Public Property CollageBackgroundColorValue As Avalonia.Media.Color
            Get
                Try
                    Return Avalonia.Media.Color.Parse(_collageBackgroundColor)
                Catch
                    Return Avalonia.Media.Colors.White
                End Try
            End Get
            Set(value As Avalonia.Media.Color)
                CollageBackgroundColor = value.ToString()
            End Set
        End Property

        Public ReadOnly Property CollageBackgroundBrush As Avalonia.Media.IBrush
            Get
                Return New Avalonia.Media.SolidColorBrush(CollageBackgroundColorValue)
            End Get
        End Property

        Public Property CollageQuality As Integer
            Get
                Return _collageQuality
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_collageQuality, Math.Max(1, Math.Min(100, value)))
            End Set
        End Property

        Public Property CollageLayoutMode As String
            Get
                Return _collageLayoutMode
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_collageLayoutMode, If(String.IsNullOrWhiteSpace(value), "Grid", value))
                Me.RaisePropertyChanged(NameOf(IsCollageRandomMode))
                ScheduleCollagePreviewUpdate()
            End Set
        End Property

        Public ReadOnly Property IsCollageGridMode As Boolean
            Get
                Return String.Equals(_collageLayoutMode, "Grid", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsCollageHeroMode As Boolean
            Get
                Return String.Equals(_collageLayoutMode, "Hero", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsCollageRandomMode As Boolean
            Get
                Return String.Equals(_collageLayoutMode, "Random", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public Property CollageHeroIndex As Integer
            Get
                Return _collageHeroIndex
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_collageHeroIndex, Math.Max(0, value))
                ScheduleCollagePreviewUpdate()
            End Set
        End Property

        ''' Nimmt dieselben 9 Anker-Namen wie der Leinwandgröße-Positionswähler im Editor entgegen
        ''' (TopLeft/Top/TopRight/Left/Center/Right/BottomLeft/Bottom/BottomRight), da dieselbe
        ''' anchor-dot-Optik wiederverwendet wird - die 4 Ecken kennt Hero nicht direkt und bildet
        ''' sie auf die naheliegendste Seite ab; Center ist eine eigene, echte Layout-Variante
        ''' (Hero mittig, Rest ringsherum verteilt) und zugleich der Standard für den Hero-Modus.
        Private Shared Function NormalizeHeroPosition(value As String) As String
            Select Case If(value, "").Trim().ToUpperInvariant()
                Case "TOP", "TOPLEFT", "TOPRIGHT" : Return "Top"
                Case "BOTTOM", "BOTTOMLEFT", "BOTTOMRIGHT" : Return "Bottom"
                Case "RIGHT" : Return "Right"
                Case "LEFT" : Return "Left"
                Case Else : Return "Center"
            End Select
        End Function

        Public Property CollageHeroPosition As String
            Get
                Return _collageHeroPosition
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_collageHeroPosition, NormalizeHeroPosition(value))
                ScheduleCollagePreviewUpdate()
            End Set
        End Property

        Public Property CollageRandomSeed As Integer
            Get
                Return _collageRandomSeed
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_collageRandomSeed, value)
                ScheduleCollagePreviewUpdate()
            End Set
        End Property

        Public Property CollageOrderSeed As Integer?
            Get
                Return _collageOrderSeed
            End Get
            Set(value As Integer?)
                Me.RaiseAndSetIfChanged(_collageOrderSeed, value)
                ScheduleCollagePreviewUpdate()
            End Set
        End Property

        Public Property CollagePreviewZoom As Double
            Get
                Return _collagePreviewZoom
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_collagePreviewZoom, Math.Max(0.25, Math.Min(4.0, value)))
            End Set
        End Property

        Public Property CollagePreviewImage As Avalonia.Media.Imaging.Bitmap
            Get
                Return _collagePreviewImage
            End Get
            Set(value As Avalonia.Media.Imaging.Bitmap)
                Me.RaiseAndSetIfChanged(_collagePreviewImage, value)
            End Set
        End Property

        Public ReadOnly Property IsCollageJpgQualityVisible As Boolean
            Get
                Return String.Equals(_collageFormat, "JPG", StringComparison.OrdinalIgnoreCase) OrElse
                       String.Equals(_collageFormat, "WEBP", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public Property ShowFolders As Boolean
            Get
                Return _showFolders
            End Get
            Set(value As Boolean)
                If _showFolders = value Then Return
                Me.RaiseAndSetIfChanged(_showFolders, value)
                If _isVirtualFolder Then
                    FilterAndSort()
                    SaveFileBrowserSettings()
                    Return
                End If
                ' Ordner ein-/ausblenden ändert nur, welche Einträge dazugehören - kein Grund, die Liste
                ' und damit die Bildlaufposition neu aufzubauen.
                SyncFolderItems()
                SaveFileBrowserSettings()
            End Set
        End Property

        ''' <summary>Galerie-Kachel-Badges: True = immer sichtbar, False = erst beim Mouseover. Von den
        ''' Kachel-Vorlagen per RelativeSource gelesen (Classes.badge-always).</summary>
        Public Property RatingBadgesAlwaysVisible As Boolean
            Get
                Return _ratingBadgesAlwaysVisible
            End Get
            Set(value As Boolean)
                If _ratingBadgesAlwaysVisible = value Then Return
                Me.RaiseAndSetIfChanged(_ratingBadgesAlwaysVisible, value)
                SaveFileBrowserSettings()
            End Set
        End Property

        Public Property FavoriteBadgeAlwaysVisible As Boolean
            Get
                Return _favoriteBadgeAlwaysVisible
            End Get
            Set(value As Boolean)
                If _favoriteBadgeAlwaysVisible = value Then Return
                Me.RaiseAndSetIfChanged(_favoriteBadgeAlwaysVisible, value)
                SaveFileBrowserSettings()
            End Set
        End Property

        Public Property MetadataBadgesAlwaysVisible As Boolean
            Get
                Return _metadataBadgesAlwaysVisible
            End Get
            Set(value As Boolean)
                If _metadataBadgesAlwaysVisible = value Then Return
                Me.RaiseAndSetIfChanged(_metadataBadgesAlwaysVisible, value)
                SaveFileBrowserSettings()
            End Set
        End Property


        Public Property ShowParentFolder As Boolean
            Get
                Return _showParentFolder
            End Get
            Set(value As Boolean)
                If _showParentFolder = value Then Return
                Me.RaiseAndSetIfChanged(_showParentFolder, value)
                If _isVirtualFolder Then
                    FilterAndSort()
                    SaveFileBrowserSettings()
                    Return
                End If
                ' Ordner ein-/ausblenden ändert nur, welche Einträge dazugehören - kein Grund, die Liste
                ' und damit die Bildlaufposition neu aufzubauen.
                SyncFolderItems()
                SaveFileBrowserSettings()
            End Set
        End Property

        Public Property ViewMode As String
            Get
                Return _viewMode
            End Get
            Set(value As String)
                value = AppSettingsService.NormalizeGalleryViewMode(value)
                If _viewMode = value Then Return
                Me.RaiseAndSetIfChanged(_viewMode, value)
                Me.RaisePropertyChanged(NameOf(IsGridView))
                Me.RaisePropertyChanged(NameOf(IsListView))
                Me.RaisePropertyChanged(NameOf(IsGroupView))
                Me.RaisePropertyChanged(NameOf(IsTileView))
                Me.RaisePropertyChanged(NameOf(IsGroupDateStepVisible))
                AppSettingsService.SaveGalleryViewMode(value)
                _mainVm?.Settings?.SyncGalleryViewMode(value)
                ' Das Anzeigefenster zaehlt in der Gruppenansicht Layout-Eintraege (Kopfzeilen
                ' eingerechnet), in den anderen beiden Bilder. Ohne diesen Neuaufbau stuenden nach dem
                ' Umschalten die Grenzen der alten Zaehlung im Fenster.
                InvalidateGroupLayout()
                _displayWindowFirst = -1
                _displayWindowLast = -1
                RefreshDisplayWindow()
            End Set
        End Property

        Public ReadOnly Property IsGridView As Boolean
            Get
                Return _viewMode = "Grid"
            End Get
        End Property

        Public ReadOnly Property IsListView As Boolean
            Get
                Return _viewMode = "List"
            End Get
        End Property

        ''' <summary>Dritte Ansicht neben Raster und Liste: dasselbe Kachelraster, aber mit einer
        ''' Kopfzeile je Gruppe. Woraus die Gruppen entstehen, sagt die aktuelle Sortierung.</summary>
        Public ReadOnly Property IsGroupView As Boolean
            Get
                Return _viewMode = "Group"
            End Get
        End Property

        ''' <summary>Raster oder Gruppenansicht - beide zeigen Kacheln und teilen sich dieselbe
        ''' Flaeche in der Ansicht.</summary>
        Public ReadOnly Property IsTileView As Boolean
            Get
                Return IsGridView OrElse IsGroupView
            End Get
        End Property

        ''' <summary>Feinheit der Datumsgruppen: "Day", "Month" oder "Year". Wirkt nur, solange nach
        ''' einem Datum sortiert wird.</summary>
        Public Property GroupDateStep As String
            Get
                Return _groupDateStep
            End Get
            Set(value As String)
                value = AppSettingsService.NormalizeGalleryGroupDateStep(value)
                If _groupDateStep = value Then Return
                Me.RaiseAndSetIfChanged(_groupDateStep, value)
                Me.RaisePropertyChanged(NameOf(IsGroupStepDay))
                Me.RaisePropertyChanged(NameOf(IsGroupStepMonth))
                Me.RaisePropertyChanged(NameOf(IsGroupStepYear))
                AppSettingsService.SaveGalleryGroupDateStep(value)
                InvalidateGroupLayout()
                RefreshDisplayWindow()
            End Set
        End Property

        Public ReadOnly Property IsGroupStepDay As Boolean
            Get
                Return _groupDateStep = "Day"
            End Get
        End Property

        Public ReadOnly Property IsGroupStepMonth As Boolean
            Get
                Return _groupDateStep = "Month"
            End Get
        End Property

        Public ReadOnly Property IsGroupStepYear As Boolean
            Get
                Return _groupDateStep = "Year"
            End Get
        End Property

        ''' <summary>Ob die Wahl der Feinheit gerade etwas bewirkt: nur in der Gruppenansicht und nur bei
        ''' einer Datumssortierung. Bei Name, Kamera oder Bewertung gibt es keinen Tag, den man
        ''' vergroebern koennte.</summary>
        Public ReadOnly Property IsGroupDateStepVisible As Boolean
            Get
                If Not IsGroupView Then Return False
                Select Case _sortMode
                    Case "FileModifiedAt", "FileCreatedAt", "ExifDateTaken", "ExifDateModified"
                        Return True
                    Case Else
                        Return False
                End Select
            End Get
        End Property

        Public Property IsLoading As Boolean
            Get
                Return _isLoading
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isLoading, value)
            End Set
        End Property

        Public Property StorageFreeText As String
            Get
                Return _storageFreeText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_storageFreeText, value)
            End Set
        End Property

        Public Property StorageFillPercent As Double
            Get
                Return _storageFillPercent
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_storageFillPercent, value)
            End Set
        End Property

        Public ReadOnly Property RefreshCommand As ICommand
        Public ReadOnly Property ClearSearchCommand As ICommand
        Public ReadOnly Property NavigateForwardCommand As ICommand
        Public ReadOnly Property NavigateUpCommand As ICommand
        Public ReadOnly Property NavigateParentCommand As ICommand
        Public ReadOnly Property NavigatePicturesCommand As ICommand
        Public ReadOnly Property SetSortCommand As ICommand
        Public ReadOnly Property SetSortDirectionCommand As ICommand
        Public ReadOnly Property SetViewModeCommand As ICommand
        Public ReadOnly Property SetGroupDateStepCommand As ICommand
        Public ReadOnly Property DeleteSelectedCommand As ICommand
        Public ReadOnly Property SelectAllCommand As ICommand
        Public ReadOnly Property ClearSelectionCommand As ICommand
        Public ReadOnly Property OpenFileManagerCommand As ICommand
        Public ReadOnly Property CopyPathCommand As ICommand
        Public ReadOnly Property ToggleFavoriteCommand As ICommand
        Public ReadOnly Property ToggleSelectedFavoriteCommand As ICommand
        Public ReadOnly Property ToggleInfoSidebarCommand As ICommand
        Public ReadOnly Property ToggleTagFilterCommand As ICommand
        Public ReadOnly Property ClearTagFilterCommand As ICommand
        Public ReadOnly Property ClearPersonFilterCommand As ICommand
        Public ReadOnly Property ClearPlaceFilterCommand As ICommand
        Public ReadOnly Property ScanFacesCommand As ICommand
        Public ReadOnly Property SetSelectedRatingCommand As ICommand
        Public ReadOnly Property SetSelectedColorLabelCommand As ICommand
        Public ReadOnly Property RenameSelectedCommand As ICommand
        Public ReadOnly Property DuplicateSelectedCommand As ICommand
        Public ReadOnly Property ResizeSelectedCommand As ICommand
        Public ReadOnly Property ApplyWatermarkSelectedCommand As ICommand
        Public ReadOnly Property PrintSelectedCommand As ICommand
        Public ReadOnly Property BatchConvertSelectedCommand As ICommand
        ''' <summary>Wo das Kontextmenue geoeffnet wurde und welche Elemente es meint. Beides
        ''' setzt die View, bevor sie oeffnet.</summary>
        Public Property ContextSite As MenuSite = MenuSite.GalleryTile
        Public Property ContextItems As IList(Of ImageItem) = New List(Of ImageItem)()

        ''' <summary>Die Kommandos fuer das Kontextmenue. Gefuellt von der VIEW, weil ein grosser
        ''' Teil der Galerie-Aktionen dort liegt: Zwischenablage, Ordner anlegen, Vergleichen und
        ''' das Farbetikett arbeiten mit dem Fensterrahmen. Sie hier nachzubauen hiesse, sie in das
        ''' ViewModel zu verschieben, wo sie nicht hingehoeren.</summary>
        Public Property ContextCommands As MenuCommands

        ''' <summary>Die Eintraege des Kontextmenues, gebaut aus Aufrufort und Auswahl. Die Regeln
        ''' stehen in Audits/KONTEXTMENUE.md und gelten fuer alle Bereiche gleich - die Galerie ist
        ''' dabei die fuehrende Vorlage.</summary>
        Public ReadOnly Property ContextActions As IReadOnlyList(Of Object)
            Get
                Return ContextMenuBuilder.Build(ContextSite, ContextItems,
                                                isVirtual:=IsVirtualFolder,
                                                canPaste:=CanPasteIntoFolder(CurrentFolder),
                                                commands:=ContextCommands)
            End Get
        End Property

        Public Sub RefreshContextActions()
            Me.RaisePropertyChanged(NameOf(ContextActions))
        End Sub

        Public ReadOnly Property ToggleFullscreenCommand As ICommand
        Public ReadOnly Property NewDocumentCommand As ICommand
        Public ReadOnly Property ExportSelectedCommand As ICommand
        Public ReadOnly Property ApplyFilterSelectedCommand As ICommand
        Public ReadOnly Property RemoveMetadataSelectedCommand As ICommand
        Public ReadOnly Property IncreaseThumbnailSizeCommand As ICommand
        Public ReadOnly Property DecreaseThumbnailSizeCommand As ICommand
        Public ReadOnly Property SetFilterFavoriteCommand As ICommand
        Public ReadOnly Property SetFilterColorLabelCommand As ICommand
        Public ReadOnly Property SetFilterRatingCommand As ICommand
        Public ReadOnly Property SetFilterTypeCommand As ICommand
        Public ReadOnly Property ClearFiltersCommand As ICommand

        Public ReadOnly Property CanNavigateBack As Boolean
            Get
                Return _historyBack.Count > 0
            End Get
        End Property

        Public ReadOnly Property CanNavigateForward As Boolean
            Get
                Return _historyForward.Count > 0
            End Get
        End Property

        Public ReadOnly Property SelectedRating As Integer
            Get
                Dim images = GetSelectedImageItems()
                If images.Count = 0 Then Return 0
                Dim firstRating = images(0).Rating
                If images.Any(Function(i) i.Rating <> firstRating) Then Return 0
                Return firstRating
            End Get
        End Property

        ''' Gefüllt nur, wenn ALLE ausgewählten Bilder Favoriten sind - bei gemischter Auswahl zeigt die
        ''' Fußleiste das leere Herz, und der nächste Klick macht alle zu Favoriten.
        Public ReadOnly Property SelectedIsFavorite As Boolean
            Get
                Dim images = GetSelectedImageItems()
                Return images.Count > 0 AndAlso images.All(Function(i) i.IsFavorite)
            End Get
        End Property

        Private Sub RaiseSelectionMetadataChanged()
            Me.RaisePropertyChanged(NameOf(SelectedRating))
            Me.RaisePropertyChanged(NameOf(SelectedIsFavorite))
            ' Hier und nicht an jeder Auswahlstelle einzeln: alle Wege, die die Auswahl aendern,
            ' laufen ueber diese Meldung. Der Setter von SelectedItem allein genuegt nicht - beim
            ' Abwaehlen auf ein einzelnes Bild bleibt er unveraendert.
            UpdateInfoPanelTarget()
            RefreshGroupHeaderSelection()
        End Sub

        Public ReadOnly Property HasSelection As Boolean
            Get
                Return SelectedItems IsNot Nothing AndAlso SelectedItems.Count > 0
            End Get
        End Property

        Public ReadOnly Property HasSelectedImage As Boolean
            Get
                Return GetSelectedImageItems().Count > 0
            End Get
        End Property

        Public Function CanRenamePath(path As String) As Boolean
            If IsVirtualFolderPath(path) Then Return False
            Return FileOperationPolicy.CanRename(path)
        End Function

        Public Function CanDeletePath(path As String) As Boolean
            Return FileOperationPolicy.CanDelete(path)
        End Function

        Public Function CanCopyPath(path As String) As Boolean
            Return FileOperationPolicy.CanCopy(path)
        End Function

        Public Function CanCutPath(path As String) As Boolean
            If IsVirtualFolderPath(path) Then Return False
            Return FileOperationPolicy.CanRename(path)
        End Function

        Public Function CanPasteIntoFolder(folderPath As String) As Boolean
            If IsVirtualFolderPath(folderPath) Then Return False
            Return FileOperationPolicy.CanPasteInto(folderPath)
        End Function

        Public Function CanMovePathsToFolder(paths As IEnumerable(Of String), targetFolder As String) As Boolean
            If paths Is Nothing Then Return False
            Dim list = paths.Where(Function(p) Not String.IsNullOrEmpty(p)).ToList()
            Return list.Count > 0 AndAlso list.All(Function(p) FileOperationPolicy.CanMove(p, targetFolder))
        End Function

        Public Property SelectedFolderNode As FolderNode
            Get
                Return _selectedFolderNode
            End Get
            Set(value As FolderNode)
                Me.RaiseAndSetIfChanged(_selectedFolderNode, value)
            End Set
        End Property

        Public Property SelectedSearchNode As VirtualNavigationNode
            Get
                Return _selectedSearchNode
            End Get
            Set(value As VirtualNavigationNode)
                Me.RaiseAndSetIfChanged(_selectedSearchNode, value)
            End Set
        End Property

        ''' <summary>Der aktuell geöffnete Immich-Knoten (Album bzw. „Alle Fotos"). Analog zu
        ''' SelectedSearchNode, damit die GalleryView nach Neuinstanziierung (Moduswechsel) im Immich-
        ''' Ordner bleibt statt in den Startordner zu navigieren.</summary>
        Public Property SelectedImmichNode As VirtualNavigationNode
            Get
                Return _selectedImmichNode
            End Get
            Set(value As VirtualNavigationNode)
                Me.RaiseAndSetIfChanged(_selectedImmichNode, value)
            End Set
        End Property

        Public Property SelectedNextcloudNode As VirtualNavigationNode
            Get
                Return _selectedNextcloudNode
            End Get
            Set(value As VirtualNavigationNode)
                Me.RaiseAndSetIfChanged(_selectedNextcloudNode, value)
            End Set
        End Property

        Private _allItems As New List(Of ImageItem)()
        Private _lastWindowFirst As Integer = -1
        Private _lastWindowLast As Integer = -1
        Private _lastWindowSlotHeight As Double = 0
        Private _lastWindowColumns As Integer = 0
        Private _displayWindowFirst As Integer = -1
        Private _displayWindowLast As Integer = -1

        ' Gruppenansicht: die Anzeigereihenfolge einschliesslich der Kopfzeilen, je Eintrag der Index in
        ' Items (-1 bei einer Kopfzeile), der Rueckweg von Items in diese Liste und die Zeilentabelle.
        ' In der Gruppenansicht zaehlen _displayWindowFirst/-Last Eintraege dieser Liste, in Raster und
        ' Liste Elemente aus Items - die Ansichten laufen nie gleichzeitig.
        Private ReadOnly _groupLayout As New List(Of ImageItem)()
        Private ReadOnly _groupLayoutItemIndex As New List(Of Integer)()
        Private ReadOnly _groupRows As New List(Of GroupLayoutRow)()
        Private _itemToGroupEntry As Integer() = Array.Empty(Of Integer)()
        Private _groupLayoutColumns As Integer = 0
        Private _groupLayoutSlotHeight As Double = 0
        Private _groupContentHeight As Double = 0
        Private _groupEntriesDirty As Boolean = True
        Private _lastGroupOffsetY As Double = 0
        Private _lastGroupViewportHeight As Double = 0

        Private _topSpacerHeight As Double
        Private _bottomSpacerHeight As Double
        Private _contentHeight As Double

        Public Property TopSpacerHeight As Double
            Get
                Return _topSpacerHeight
            End Get
            Private Set(value As Double)
                If Math.Abs(_topSpacerHeight - value) < 0.1 Then Return
                Me.RaiseAndSetIfChanged(_topSpacerHeight, value)
                Me.RaisePropertyChanged(NameOf(DisplayWindowHeight))
            End Set
        End Property

        Public Property BottomSpacerHeight As Double
            Get
                Return _bottomSpacerHeight
            End Get
            Private Set(value As Double)
                If Math.Abs(_bottomSpacerHeight - value) < 0.1 Then Return
                Me.RaiseAndSetIfChanged(_bottomSpacerHeight, value)
                Me.RaisePropertyChanged(NameOf(DisplayWindowHeight))
            End Set
        End Property

        Public Property ContentHeight As Double
            Get
                Return _contentHeight
            End Get
            Private Set(value As Double)
                If Math.Abs(_contentHeight - value) < 0.1 Then Return
                Me.RaiseAndSetIfChanged(_contentHeight, value)
                Me.RaisePropertyChanged(NameOf(DisplayWindowHeight))
            End Set
        End Property

        ''' <summary>Die Hoehe, die das Anzeigefenster selbst einnimmt: Gesamthoehe abzueglich der
        ''' beiden Platzhalter.
        '''
        ''' <para>DIE REPEATER BINDEN SIE ALS MinHeight, und das ist keine Kosmetik. Ein
        ''' ItemsRepeater realisiert nur, was in sein Sichtfenster faellt, und dieses Fenster ist
        ''' seine EIGENE sichtbare Flaeche (EffectiveViewportChanged, ausgewertet im
        ''' ViewportManager). Ein Repeater, der leer in eine Ansicht geht, ist null hoch; damit ist
        ''' sein Sichtfenster leer, er realisiert nichts - und bleibt null hoch. Aus dieser Klemme
        ''' kommt er nicht von selbst heraus.
        '''
        ''' <para>Sichtbar wurde das ueberall dort, wo der Inhalt ERST NACH dem Aufbau der Ansicht
        ''' eintrifft: ein Ordner ohne Katalogdaten, Suchlisten, Immich und Nextcloud blieben leer,
        ''' bis ein Ansichts- oder Ordnerwechsel die Flaeche neu aufbaute (Nutzerbefund
        ''' 2026-08-28). Nur wo der Katalog vorfuellte, stand schon Inhalt da, als der Repeater das
        ''' erste Mal vermessen wurde - deshalb fiel es dort nicht auf.</para>
        '''
        ''' <para>MinHeight und nicht Height: braucht der Repeater mehr, soll er sich nehmen, was
        ''' er braucht.</para></summary>
        Public ReadOnly Property DisplayWindowHeight As Double
            Get
                Return Math.Max(0, _contentHeight - _topSpacerHeight - _bottomSpacerHeight)
            End Get
        End Property

        Public Sub New(mainVm As MainWindowViewModel)
            _mainVm = mainVm
            Dim settings = AppSettingsService.Load()
            _thumbnailSize = settings.GalleryThumbnailSize
            _viewMode = AppSettingsService.NormalizeGalleryViewMode(settings.GalleryViewMode)
            _sortMode = settings.GallerySortMode
            _sortAscending = settings.GallerySortAscending
            _groupDateStep = AppSettingsService.NormalizeGalleryGroupDateStep(settings.GalleryGroupDateStep)
            _showFolders = settings.GalleryShowFolders
            _showParentFolder = settings.GalleryShowParentFolder
            _ratingBadgesAlwaysVisible = settings.GalleryRatingBadgesAlwaysVisible
            _favoriteBadgeAlwaysVisible = settings.GalleryFavoriteBadgeAlwaysVisible
            _metadataBadgesAlwaysVisible = settings.GalleryMetadataBadgesAlwaysVisible
            _filterFavorite = settings.GalleryFilterFavorite
            _filterRatings.UnionWith(settings.GalleryFilterRatings)
            _filterFileType = settings.GalleryFilterFileType
            Items = New BulkObservableCollection(Of ImageItem)()
            ' Die Gruppen haengen an Items. Statt an jedem der vielen Wege, die die Liste anfassen, einen
            ' Aufruf nachzutragen (einer wird immer vergessen), horcht die Gruppenansicht an der Sammlung
            ' selbst. Der Neuaufbau passiert erst beim naechsten Zeichnen, das Ereignis kostet nichts.
            AddHandler Items.CollectionChanged, Sub(sender As Object, e As Specialized.NotifyCollectionChangedEventArgs) InvalidateGroupLayout()
            DisplayItems = New BulkObservableCollection(Of ImageItem)()
            WatchBackgroundRuns()
            SelectedItems = New ObservableCollection(Of ImageItem)()
            FolderTree = New ObservableCollection(Of FolderNode)()
            SetSidebarTabCommand = ReactiveCommand.Create(Of String)(Sub(tab) SidebarTab = tab)
            SearchTree = New ObservableCollection(Of VirtualNavigationNode)()
            ImmichSearchTree = New ObservableCollection(Of VirtualNavigationNode)()
            NextcloudSearchTree = New ObservableCollection(Of VirtualNavigationNode)()
            FavoritesTree = New ObservableCollection(Of VirtualNavigationNode)()
            ImmichTree = New ObservableCollection(Of VirtualNavigationNode)()
            NextcloudTree = New ObservableCollection(Of VirtualNavigationNode)()

            _collagePreviewTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(350)}
            AddHandler _collagePreviewTimer.Tick, Sub()
                                                       _collagePreviewTimer.Stop()
                                                       RefreshCollagePreviewAsync()
                                                   End Sub

            ' Jeder Tastendruck in der Suche filterte bisher sofort den GANZEN Ordner neu, sortierte ihn und
            ' schob ihn in die gebundene Sammlung. Bei ein paar tausend Fotos ist jeder Buchstabe ein
            ' vollständiger Neuaufbau der Ansicht. 150 ms sind kurz genug, um beim Tippen nicht zu stören,
            ' und lang genug, damit eine zügig getippte Eingabe nur EINEN Filterlauf auslöst.
            _searchDebounceTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(150)}
            AddHandler _searchDebounceTimer.Tick, Sub()
                                                       _searchDebounceTimer.Stop()
                                                       FilterAndSort()
                                                   End Sub

            ' Die Fusszeile fragt nach, statt sich melden zu lassen: das Erzeugen der Vorschaubilder
            ' meldete sonst je Bild, und das waeren bei einem grossen Ordner tausende Meldungen fuer
            ' eine Zeile Text. Dreimal je Sekunde reicht dem Auge, und gemeldet wird nur, wenn der
            ' Satz sich wirklich geaendert hat.
            _backgroundWorkTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(350)}
            AddHandler _backgroundWorkTimer.Tick, Sub() RefreshBackgroundWorkText()
            _backgroundWorkTimer.Start()

            RefreshCommand = ReactiveCommand.Create(Sub() LoadCurrentFolder())
            ClearSearchCommand = ReactiveCommand.Create(Sub() SearchText = "")
            NavigateForwardCommand = ReactiveCommand.Create(Sub() NavigateForward())
            NavigateUpCommand = ReactiveCommand.Create(Sub() NavigateBack())
            NavigateParentCommand = ReactiveCommand.Create(Sub() NavigateToParent())
            NavigatePicturesCommand = ReactiveCommand.Create(Sub() NavigateToPicturesFolder())
            SetSortCommand = ReactiveCommand.Create(Of String)(Sub(m) SortMode = m)
            SetSortDirectionCommand = ReactiveCommand.Create(Of String)(Sub(direction)
                                                                            SortAscending = Not String.Equals(direction, "Descending", StringComparison.OrdinalIgnoreCase)
                                                                        End Sub)
            SetViewModeCommand = ReactiveCommand.Create(Of String)(Sub(m) ViewMode = m)
            SetGroupDateStepCommand = ReactiveCommand.Create(Of String)(Sub(dateStep) GroupDateStep = dateStep)
            DeleteSelectedCommand = ReactiveCommand.Create(Sub() DeleteSelected())
            SelectAllCommand = ReactiveCommand.Create(Sub() SelectAllVisible())
            ClearSelectionCommand = ReactiveCommand.Create(Sub() ClearSelection())
            OpenFileManagerCommand = ReactiveCommand.Create(Sub() OpenInFileManager())
            CopyPathCommand = ReactiveCommand.Create(Sub() CopySelectedPath())
            ToggleFavoriteCommand = ReactiveCommand.Create(Of ImageItem)(Sub(item) DoToggleFavorite(item))
            ToggleSelectedFavoriteCommand = ReactiveCommand.Create(Sub() ToggleSelectedFavorite())
            ToggleInfoSidebarCommand = ReactiveCommand.Create(Sub() ToggleInfoSidebar())
            ToggleTagFilterCommand = ReactiveCommand.Create(Of String)(Sub(tag) ToggleTagFilter(tag))
            ClearTagFilterCommand = ReactiveCommand.Create(Sub() ClearTagFilter())
            ClearPersonFilterCommand = ReactiveCommand.Create(Sub() ClearPersonFilter())
            ClearPlaceFilterCommand = ReactiveCommand.Create(Sub() ClearPlaceFilter())
            ScanFacesCommand = ReactiveCommand.CreateFromTask(Function() ScanFacesAsync())
            InfoPanel.OpenTagSearch = Sub(tag) OpenTagSearch(tag)
            ' Das Panel kennt nur ImageItem und koennte lokale Bilder nicht von Immich-Assets
            ' trennen. Die Wege stellt deshalb die Galerie - dieselbe Teilung wie im Sternemenue.
            InfoPanel.PersistRating = Sub(items, value) ApplyRatingTo(items, value)
            InfoPanel.PersistFavorite = Sub(items, value) ApplyFavoriteTo(items, value)
            InfoPanel.PersistColorLabel = Sub(items, value) ApplyColorLabelTo(items, value)
            InfoPanel.PersistTag = Sub(items, tag, add) ApplyTagTo(items, tag, add)
            CopyPlaceCommand = ReactiveCommand.Create(Sub() CopyPlaceFromSelected())
            OpenPlaceInOsmCommand = ReactiveCommand.Create(Sub() OpenPlaceInOsmFromSelected())
            PastePlaceCommand = ReactiveCommand.Create(Sub() PastePlaceToSelected())
            SetPlaceCommand = ReactiveCommand.CreateFromTask(Function() SetPlaceForSelectedAsync())
            SetCopyrightCommand = ReactiveCommand.CreateFromTask(Function() SetCopyrightForSelectedAsync())
            SetCaptureDateCommand = ReactiveCommand.CreateFromTask(Function() SetCaptureDateForSelectedAsync())
            RemovePlaceCommand = ReactiveCommand.CreateFromTask(Function() RemovePlaceFromSelectedAsync())
            RenameSelectedCommand = ReactiveCommand.Create(Sub() RenameSelected())
            DuplicateSelectedCommand = ReactiveCommand.CreateFromTask(Function() DuplicateSelectedAsync())
            ' CreateFromTask: der Befehl bleibt bis zum Ende des Laufs gesperrt (siehe ResizeSelectedAsync).
            ResizeSelectedCommand = ReactiveCommand.CreateFromTask(Function() ResizeSelectedAsync())
            ApplyWatermarkSelectedCommand = ReactiveCommand.Create(Sub() ApplyWatermarkSelected())
            BatchConvertSelectedCommand = ReactiveCommand.Create(Sub() BatchConvertSelected())
            ToggleFullscreenCommand = ReactiveCommand.Create(Sub() _mainVm?.ToggleFullscreen())
            NewDocumentCommand = ReactiveCommand.Create(Sub() _mainVm?.ShowNewDocumentDialog())
            ExportSelectedCommand = ReactiveCommand.Create(Sub() ExportSelected())
            PrintSelectedCommand = ReactiveCommand.CreateFromTask(Function() PrintSelectedAsync())
            ApplyFilterSelectedCommand = ReactiveCommand.Create(Sub() ApplyFilterSelected())
            RemoveMetadataSelectedCommand = ReactiveCommand.CreateFromTask(Function() RemoveMetadataSelectedAsync())
            IncreaseThumbnailSizeCommand = ReactiveCommand.Create(Sub() ThumbnailSize += 24)
            DecreaseThumbnailSizeCommand = ReactiveCommand.Create(Sub() ThumbnailSize -= 24)
            SetSelectedRatingCommand = ReactiveCommand.Create(Of String)(Sub(r) SetSelectedRating(r))
            SetSelectedColorLabelCommand = ReactiveCommand.Create(Of String)(Sub(hex) SetSelectedColorLabel(hex))
            SetFilterFavoriteCommand = ReactiveCommand.Create(Of String)(Sub(v) FilterFavorite = v)
            SetFilterRatingCommand = ReactiveCommand.Create(Of String)(Sub(v)
                Dim r As Integer
                If Integer.TryParse(v, r) Then ToggleFilterRating(r)
            End Sub)
            SetFilterTypeCommand = ReactiveCommand.Create(Of String)(Sub(v) FilterFileType = v)
            SetFilterColorLabelCommand = ReactiveCommand.Create(Of String)(Sub(v) ToggleFilterColorLabel(v))
            ClearFiltersCommand = ReactiveCommand.Create(Sub() ClearFilters())

            InitializeFolderTree()
            InitializeVirtualNavigation()
            InitializeImmich()
            InitializeNextcloud()
            FillMissingPlacesInBackground()
        End Sub

        Public Sub ReplaceSelection(selected As IEnumerable(Of ImageItem))
            For Each existing In SelectedItems
                existing.IsSelected = False
                existing.IsNavigationSelected = False
            Next
            SelectedItems.Clear()
            ' Die Kopfzeile einer Gruppe kann nicht ausgewaehlt werden - sie hat keine Datei, und jede
            ' Stapelaktion liefe auf ihr ins Leere. Die Sperre steht hier, wo alle Wege durchkommen.
            For Each item In selected.Where(Function(i) i IsNot Nothing AndAlso Not i.IsGroupHeader)
                item.IsSelected = True
                item.IsNavigationSelected = False
                SelectedItems.Add(item)
            Next
            If SelectedItems.Count > 0 Then SelectedItem = SelectedItems(SelectedItems.Count - 1)
            If SelectedItems.Count = 0 Then SelectedItem = Nothing
            Me.RaisePropertyChanged(NameOf(SelectionText))
            Me.RaisePropertyChanged(NameOf(FooterStatusText))
            Me.RaisePropertyChanged(NameOf(HasSelection))
            Me.RaisePropertyChanged(NameOf(HasSelectedImage))
            RaiseSelectionMetadataChanged()
        End Sub

        Private Sub SetNavigationOnlySelection(item As ImageItem)
            For Each existing In SelectedItems
                existing.IsSelected = False
                existing.IsNavigationSelected = False
            Next
            SelectedItems.Clear()
            If item IsNot Nothing Then item.IsNavigationSelected = True
            SelectedItem = item
            Me.RaisePropertyChanged(NameOf(SelectionText))
            Me.RaisePropertyChanged(NameOf(FooterStatusText))
            Me.RaisePropertyChanged(NameOf(HasSelection))
            Me.RaisePropertyChanged(NameOf(HasSelectedImage))
            RaiseSelectionMetadataChanged()
        End Sub

        Public Sub SelectOnly(item As ImageItem)
            If item Is Nothing Then Return
            If item.IsParentFolderEntry Then
                SetNavigationOnlySelection(item)
                Return
            End If
            ReplaceSelection({item})
        End Sub

        ''' <summary>Markiert das Element mit diesem Pfad, falls es im aktuellen Ordner sichtbar
        ''' ist - für die Rückkehr aus dem Editor auf das zuletzt bearbeitete Bild.</summary>
        Public Sub SelectItemByPath(path As String)
            If String.IsNullOrEmpty(path) Then Return
            Dim item = Items.FirstOrDefault(Function(i) i IsNot Nothing AndAlso
                                                String.Equals(i.FilePath, path, StringComparison.OrdinalIgnoreCase))
            If item IsNot Nothing Then SelectOnly(item)
        End Sub

        Public Sub SelectAllVisible()
            ReplaceSelection(Items.Where(Function(i) i IsNot Nothing AndAlso i.IsSelectableEntry))
        End Sub

        Public Sub ToggleSelection(item As ImageItem)
            If item Is Nothing OrElse item.IsParentFolderEntry OrElse item.IsGroupHeader Then Return
            If SelectedItems.Contains(item) Then
                item.IsSelected = False
                item.IsNavigationSelected = False
                SelectedItems.Remove(item)
                SelectedItem = SelectedItems.LastOrDefault()
            Else
                item.IsSelected = True
                item.IsNavigationSelected = False
                SelectedItems.Add(item)
                SelectedItem = item
            End If
            Me.RaisePropertyChanged(NameOf(SelectionText))
            Me.RaisePropertyChanged(NameOf(FooterStatusText))
            Me.RaisePropertyChanged(NameOf(HasSelection))
            Me.RaisePropertyChanged(NameOf(HasSelectedImage))
            RaiseSelectionMetadataChanged()
        End Sub

        Public ReadOnly Property CopyPlaceCommand As ICommand
        Public ReadOnly Property PastePlaceCommand As ICommand
        Public ReadOnly Property SetPlaceCommand As ICommand
        Public ReadOnly Property SetCopyrightCommand As ICommand
        Public ReadOnly Property SetCaptureDateCommand As ICommand
        Public ReadOnly Property RemovePlaceCommand As ICommand
        Public ReadOnly Property OpenPlaceInOsmCommand As ICommand

        ''' <summary>Loescht den Aufnahmeort - nach Rueckfrage.
        '''
        ''' Die Rueckfrage steht hier, weil der Vorgang die BILDDATEIEN aendert und sich nicht
        ''' zurueckdrehen laesst: die Koordinate ist danach weg, nicht versteckt. Der Text sagt
        ''' deshalb beides - wie viele Bilder es trifft und dass es endgueltig ist.</summary>
        Private Async Function RemovePlaceFromSelectedAsync(Optional preset As IList(Of ImageItem) = Nothing) As Task
            Try
                Dim images = GetPlaceTargets(preset)
                If images.Count = 0 OrElse _mainVm Is Nothing Then Return

                Dim message = String.Format(
                    LocalizationService.T("Der Aufnahmeort wird aus {0} Bildern entfernt - aus der Bilddatei, aus einer Beistelldatei daneben und aus dem Katalog. Das lässt sich nicht rückgängig machen."),
                    images.Count)
                If Not Await _mainVm.ShowConfirmAsync(LocalizationService.T("Aufnahmeort löschen"), message,
                                                      LocalizationService.T("Löschen"),
                                                      LocalizationService.T("Abbrechen")) Then Return

                Dim batch = LibraryService.Instance.ClearGpsCoordinatesForMany(images.Select(Function(i) i.FilePath).ToList())
                StatusText = String.Format(LocalizationService.T("Aufnahmeort aus {0} Bildern entfernt"), batch.SucceededCount)
                UpdateInfoPanelTarget()
                InfoPanel.RefreshPlace()
                RefreshContextActions()
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.RemovePlace", ex)
            End Try
        End Function

        ''' <summary>Fragt den Aufnahmeort im Dialog ab und setzt ihn auf die ganze Auswahl.</summary>
        Private Async Function SetPlaceForSelectedAsync(Optional preset As IList(Of ImageItem) = Nothing) As Task
            Try
                Dim images = GetPlaceTargets(preset)
                If images.Count = 0 OrElse _mainVm Is Nothing Then Return

                ' Bei EINEM Bild, das schon einen Ort hat, steht der beim Oeffnen im Feld: dann
                ' sieht man, was gilt, und kann ihn korrigieren statt ihn neu zu tippen. Bei
                ' mehreren gibt es den einen Stand nicht, dort bleibt das Feld leer.
                Dim initialQuery = ""
                If images.Count = 1 Then
                    Dim stored = LibraryService.Instance.GetGpsCoordinates(images(0).FilePath)
                    If stored.Latitude.HasValue AndAlso stored.Longitude.HasValue Then
                        initialQuery = stored.Latitude.Value.ToString("0.000000", Globalization.CultureInfo.InvariantCulture) & ", " &
                                       stored.Longitude.Value.ToString("0.000000", Globalization.CultureInfo.InvariantCulture)
                    End If
                End If

                Dim chosen = Await _mainVm.ShowSetPlaceAsync(images.Select(Function(i) i.FilePath).ToList(), initialQuery)
                If chosen Is Nothing Then Return
                ApplyPlaceToSelection(chosen.Latitude, chosen.Longitude, chosen.Label, images)

                ' Wer einen Ort gesetzt hat, will ihn meist auf mehrere Reihen anwenden. Er landet
                ' deshalb gleich im Merker und steht beim naechsten Rechtsklick zum Einfuegen bereit.
                GeotagClipboard.Remember(chosen.Latitude, chosen.Longitude, chosen.Label)
                RefreshContextActions()
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.SetPlace", ex)
            End Try
        End Function

        ''' <summary>Merkt sich den Aufnahmeort des angeklickten Bildes.
        '''
        ''' Die Beschriftung des Einfuegen-Eintrags soll den ORT nennen und nicht die Koordinate,
        ''' solange einer bekannt ist - "Aufnahmeort einfügen: München" sagt mehr als eine Zahl.</summary>
        Private Sub CopyPlaceFromSelected(Optional preset As IList(Of ImageItem) = Nothing)
            Dim images = GetPlaceTargets(preset)
            If images.Count <> 1 Then Return
            Dim path = images(0).FilePath
            Dim stored = LibraryService.Instance.GetGpsCoordinates(path)
            If Not stored.Latitude.HasValue OrElse Not stored.Longitude.HasValue Then
                StatusText = LocalizationService.T("Dieses Bild hat keinen Aufnahmeort")
                Return
            End If

            Dim place = LibraryService.Instance.GetPlace(path)
            Dim label = place.City
            If label.Length > 0 AndAlso place.Country.Length > 0 Then
                label &= ", " & PlaceLookupService.LocalizedCountry(place.CountryCode, place.Country)
            End If
            GeotagClipboard.Remember(stored.Latitude.Value, stored.Longitude.Value, label)
            StatusText = LocalizationService.T("Aufnahmeort gemerkt") & ": " & GeotagClipboard.Label
            RefreshContextActions()
        End Sub

        ''' <summary>Zeigt den Aufnahmeort des markierten Bildes auf der Karte von OpenStreetMap.
        ''' Der Ort steht im Katalog, gebraucht wird nur die Adresse - es geht nichts an einen
        ''' Dienst, ausser dem Aufruf, den der Nutzer mit dem Klick selbst auslöst.</summary>
        Private Sub OpenPlaceInOsmFromSelected(Optional preset As IList(Of ImageItem) = Nothing)
            Dim images = GetPlaceTargets(preset)
            If images.Count <> 1 Then Return
            Dim stored = LibraryService.Instance.GetGpsCoordinates(images(0).FilePath)
            If Not stored.Latitude.HasValue OrElse Not stored.Longitude.HasValue Then
                StatusText = LocalizationService.T("Dieses Bild hat keinen Aufnahmeort")
                Return
            End If
            Dim url = GeotagService.BuildOpenStreetMapUrl(stored.Latitude.Value, stored.Longitude.Value)
            If Not ShellOpenService.Open(url, "Gallery.OpenPlaceInOsm") Then
                StatusText = LocalizationService.T("Die Karte konnte nicht geöffnet werden")
            End If
        End Sub

        ''' <summary>Setzt den gemerkten Aufnahmeort auf die ganze Auswahl. Serverbilder bleiben
        ''' aussen vor: sie haben keine Datei, die den Ort tragen koennte.</summary>
        Private Sub PastePlaceToSelected(Optional preset As IList(Of ImageItem) = Nothing)
            Dim latitude As Double, longitude As Double
            If Not GeotagClipboard.TryGet(latitude, longitude) Then Return
            ApplyPlaceToSelection(latitude, longitude, GeotagClipboard.Label, preset)
        End Sub

        ''' <summary>Die Bilder, auf die eine Ortsaktion wirkt: die VORGEGEBENE Liste, sonst die
        ''' Auswahl der Galerie. Betrachter und Editor geben ihr angezeigtes Bild vor - dieselbe
        ''' Umsetzung, nur ein anderer Ausgangspunkt (wie bei ResizeImageItemsAsync).
        '''
        ''' Serverbilder fallen in beiden Faellen weg: sie haben keine Datei, die den Ort tragen
        ''' koennte, und ihr Katalogeintrag haengt an einem Pseudo-Pfad.</summary>
        Private Function GetPlaceTargets(preset As IList(Of ImageItem)) As List(Of ImageItem)
            Dim source = If(preset IsNot Nothing AndAlso preset.Count > 0,
                            preset.AsEnumerable(),
                            GetSelectedImageItems().AsEnumerable())
            Return source.Where(Function(i) i IsNot Nothing AndAlso i.IsImage AndAlso
                                            Not i.IsRemoteAsset AndAlso
                                            Not String.IsNullOrEmpty(i.FilePath)).ToList()
        End Function

        ''' <summary>Der gemeinsame Weg von "einfügen" und "setzen": schreiben, melden, Infoleiste
        ''' nachziehen. Gemeldet wird getrennt nach Ziel - "steht in der Datei" und "steht daneben"
        ''' ist fuer den Nutzer nicht dasselbe.</summary>
        Friend Sub ApplyPlaceToSelection(latitude As Double, longitude As Double, label As String,
                                         Optional preset As IList(Of ImageItem) = Nothing)
            Dim images = GetPlaceTargets(preset)
            If images.Count = 0 Then Return

            Dim batch = LibraryService.Instance.SetGpsCoordinatesForMany(
                images.Select(Function(i) i.FilePath).ToList(), latitude, longitude)

            Dim ort = If(String.IsNullOrWhiteSpace(label), GeotagService.FormatCoordinates(latitude, longitude), label.Trim())
            If batch.Failed.Count = 0 Then
                StatusText = String.Format(LocalizationService.T("Aufnahmeort gesetzt für {0} Bilder: {1}"),
                                           batch.SucceededCount, ort)
            Else
                StatusText = String.Format(LocalizationService.T("Aufnahmeort gesetzt für {0} von {1} Bildern"),
                                           batch.SucceededCount, batch.Total)
            End If

            ' Die Infoleiste zeigt Ort und Land - sie steht sonst beim alten Stand. RefreshPlace
            ' gehoert dazu: UpdateInfoPanelTarget reicht dieselbe Auswahl noch einmal ein, und das
            ' Panel steigt bei unveraendertem Pfad sofort wieder aus.
            UpdateInfoPanelTarget()
            InfoPanel.RefreshPlace()
            RefreshContextActions()
        End Sub

        ''' <summary>Die Ortsaktionen fuer eine VORGEGEBENE Liste. Betrachter und Editor loesen damit
        ''' fuer ihr angezeigtes Bild dasselbe aus, was die Galerie fuer ihre Auswahl tut.</summary>
        Public Sub CopyPlaceFromImageItems(items As IList(Of ImageItem))
            If items Is Nothing OrElse items.Count = 0 Then Return
            CopyPlaceFromSelected(items)
        End Sub

        Public Sub OpenPlaceInOsmForImageItems(items As IList(Of ImageItem))
            If items Is Nothing OrElse items.Count = 0 Then Return
            OpenPlaceInOsmFromSelected(items)
        End Sub

        Public Sub PastePlaceToImageItems(items As IList(Of ImageItem))
            If items Is Nothing OrElse items.Count = 0 Then Return
            PastePlaceToSelected(items)
        End Sub

        Public Async Function SetPlaceForImageItemsAsync(items As IList(Of ImageItem)) As Task
            If items Is Nothing OrElse items.Count = 0 Then Return
            Await SetPlaceForSelectedAsync(items)
        End Function

        ''' <summary>Derselbe Weg fuer den Urheberrechtshinweis: Betrachter und Editor loesen damit
        ''' fuer ihr angezeigtes Bild aus, was die Galerie fuer ihre Auswahl tut.</summary>
        Public Async Function SetCopyrightForImageItemsAsync(items As IList(Of ImageItem)) As Task
            If items Is Nothing OrElse items.Count = 0 Then Return
            Await SetCopyrightForSelectedAsync(items)
        End Function

        ''' <summary>Der Weg fuer Betrachter und Editor: dort steht immer genau EIN Bild vorn, die
        ''' Arbeit macht aber dieselbe Stelle wie in der Galerie - eine zweite Fassung hiesse zwei
        ''' Regeln fuer dieselbe Frage.</summary>
        Public Async Function SetCaptureDateForImageItemsAsync(items As IList(Of ImageItem)) As Task
            If items Is Nothing OrElse items.Count = 0 Then Return
            Await SetCaptureDateForSelectedAsync(items)
        End Function

        ''' <summary>Setzt eine Aufnahmezeit für die Auswahl - als festen Wert oder als Verschiebung
        ''' einer vorhandenen (siehe <see cref="CaptureDateDialogResult"/>). Beim festen Wert ordnet
        ''' das Inkrement eine Serie; beim Verschieben behält jedes Bild seinen Abstand zum nächsten.
        ''' Das Original einer RAW-Datei bleibt in beiden Fällen unberührt - dort steht die Zeit in
        ''' der Beistelldatei, und von dort liest FerrumPix sie auch wieder.</summary>
        Private Async Function SetCaptureDateForSelectedAsync(Optional preset As IList(Of ImageItem) = Nothing) As Task
            Try
                ' IN DER REIHENFOLGE DER ANZEIGE, nicht in der des Anklickens: das Inkrement ordnet
                ' eine Serie zeitlich, und "das naechste Bild" ist fuer den Nutzer das rechts
                ' daneben. Wer die Reihe per Strg-Klick von hinten nach vorn aufsammelt, bekaeme
                ' sonst die Sekunden verkehrt herum verteilt.
                Dim images = SortBySelectionDisplayOrder(GetPlaceTargets(preset))
                If images.Count = 0 OrElse _mainVm Is Nothing Then Return
                Dim chosen = Await _mainVm.ShowCaptureDateAsync(If(images.Count = 1, images(0).ExifDateTaken, Nothing))
                If chosen Is Nothing Then Return
                Dim succeeded = 0
                Dim withoutDate = 0
                For index = 0 To images.Count - 1
                    Dim path = images(index).FilePath
                    Dim value As DateTime?
                    If chosen.UsesShift Then
                        ' Die GELTENDE Zeit aus der Datei, nicht die des Katalogeintrags: der kann
                        ' aelter sein als die Datei, und verschoben wird, was jetzt drinsteht.
                        Dim current = CaptureDateService.ReadCaptureDate(path)
                        If Not current.HasValue Then
                            withoutDate += 1
                            Continue For
                        End If
                        value = CaptureDateService.Shift(current.Value, chosen.Offset)
                    Else
                        ' In Sekunden und in einem Long: das Inkrement mal der laufenden Nummer
                        ' kann sonst schon beim Bilden der Spanne ueberlaufen.
                        value = CaptureDateService.Shift(chosen.CapturedAt,
                                                        CLng(index) * chosen.IncrementSeconds)
                    End If
                    If Not value.HasValue Then Continue For
                    If CaptureDateService.Write(path, value.Value) Then
                        Dim exif = ExifService.ReadExif(path)
                        Dim fields = ExifService.ExtractSearchFields(exif, path)
                        LibraryService.Instance.SyncExifData(path, fields,
                                                             ExifService.BuildCatalogSummary(exif, fields))
                        ' ALLE DREI Zeiten an der Kachel nachziehen, nicht nur die Aufnahme:
                        ' geschrieben werden drei (siehe CaptureDateService), und die Galerie kann
                        ' nach jeder davon sortieren und gruppieren. Zoege nur eine nach, zeigte die
                        ' Kachel bei "Geaendert (Datei)" oder "Geaendert (EXIF)" weiter den alten
                        ' Wert - bis der Ordner irgendwann neu gelesen wird.
                        images(index).ExifDateTaken = value
                        images(index).ExifDateModified = ExifService.ParseExifDateTime(exif.DateModifiedExif)
                        ' Das Dateidatum vom DATEISYSTEM holen statt es anzunehmen. Das Nachziehen
                        ' darf scheitern (schreibgeschuetzte Freigabe, Netzlaufwerk), und
                        ' CaptureDateService laesst den ganzen Vorgang bewusst nicht daran
                        ' scheitern - die Kachel zeigte dann eine Zeit, die die Datei nicht traegt,
                        ' und sortierte auch noch danach.
                        Try
                            images(index).DateModified = IO.File.GetLastWriteTime(path)
                        Catch ex As Exception
                            ' Keine Auskunft ueber die Datei: lieber den bisherigen Wert stehen
                            ' lassen als einen erfundenen setzen.
                            DiagnosticLogService.LogException("Gallery.SetCaptureDate.FileModified", ex)
                        End Try
                        succeeded += 1
                    End If
                Next
                ' Bilder OHNE Zeit sind beim Verschieben kein Fehler, sondern der Normalfall einer
                ' gemischten Auswahl - sie werden aber eigens genannt, sonst sucht der Nutzer den
                ' Grund an der falschen Stelle.
                If withoutDate > 0 Then
                    StatusText = String.Format(LocalizationService.T("Aufnahmedatum gesetzt für {0} von {1} Bildern, {2} ohne vorhandene Zeit"),
                                               succeeded, images.Count, withoutDate)
                Else
                    StatusText = String.Format(LocalizationService.T("Aufnahmedatum gesetzt für {0} von {1} Bildern"),
                                               succeeded, images.Count)
                End If
                UpdateInfoPanelTarget()
                InfoPanel.Refresh()
                RefreshContextActions()
                ' Haengt die Sortierung an einer der drei geschriebenen Zeiten, stehen die Kacheln
                ' jetzt an der falschen Stelle - und in der Gruppenansicht auch unter der falschen
                ' Ueberschrift. Nur dann neu ordnen: bei jeder anderen Sortierung waere es ein
                ' Neuaufbau der ganzen Liste fuer nichts.
                If succeeded > 0 AndAlso IsCaptureDateSortAffected() Then FilterAndSort()
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.SetCaptureDate", ex)
            End Try
        End Function

        ''' <summary>Ordnet die aktuelle Sortierung nach einer der Zeiten, die das Setzen des
        ''' Aufnahmedatums aendert? Aufnahme, "geaendert am" im Bild und "geaendert am" der Datei
        ''' bekommen alle drei denselben neuen Wert.</summary>
        Private Function IsCaptureDateSortAffected() As Boolean
            Return String.Equals(_sortMode, "ExifDateTaken", StringComparison.OrdinalIgnoreCase) OrElse
                   String.Equals(_sortMode, "ExifDateModified", StringComparison.OrdinalIgnoreCase) OrElse
                   String.Equals(_sortMode, "FileModifiedAt", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Bringt eine Auswahl in die Reihenfolge, in der die Bilder auf dem Schirm
        ''' stehen. <see cref="Items"/> traegt die fertig sortierte Liste; wer dort nicht steht
        ''' (Suchtreffer aus einem anderen Ordner), kommt hinten an, in sich nach Dateiname.</summary>
        Private Function SortBySelectionDisplayOrder(images As List(Of ImageItem)) As List(Of ImageItem)
            If images Is Nothing OrElse images.Count <= 1 Then Return If(images, New List(Of ImageItem)())
            Dim order As New Dictionary(Of ImageItem, Integer)()
            For index = 0 To Items.Count - 1
                Dim item = Items(index)
                If item IsNot Nothing AndAlso Not order.ContainsKey(item) Then order(item) = index
            Next
            Return images.
                OrderBy(Function(i) If(order.ContainsKey(i), order(i), Integer.MaxValue)).
                ThenBy(Function(i) i.FileName, StringComparer.CurrentCultureIgnoreCase).
                ToList()
        End Function

        ''' <summary>Fragt den Urheberrechtshinweis ab und schreibt ihn an die Auswahl.
        '''
        ''' Bei EINEM Bild, an dem schon einer steht, steht er beim Oeffnen im Feld - dann sieht man,
        ''' was gilt, und kann ihn aendern statt ihn neu zu tippen. Bei mehreren Bildern nur dann,
        ''' wenn alle denselben tragen; sonst bliebe unklar, wessen Hinweis da vorgeschlagen wird.
        '''
        ''' Ein LEER bestaetigtes Feld tut nichts. Zum Entfernen gibt es den eigenen Eintrag - sonst
        ''' loeschte ein versehentlich geleertes Feld den Hinweis einer ganzen Auswahl.</summary>
        Private Async Function SetCopyrightForSelectedAsync(Optional preset As IList(Of ImageItem) = Nothing) As Task
            Try
                Dim images = GetPlaceTargets(preset)
                If images.Count = 0 OrElse _mainVm Is Nothing Then Return

                Dim initial = CopyrightService.ReadCopyright(images(0).FilePath)
                If initial.Length > 0 Then
                    For Each item In images
                        If Not String.Equals(CopyrightService.ReadCopyright(item.FilePath), initial, StringComparison.Ordinal) Then
                            initial = ""
                            Exit For
                        End If
                    Next
                End If

                Dim entered = Await _mainVm.ShowInputAsync(AppDialogKind.Input,
                                                           LocalizationService.T("Copyright setzen"),
                                                           LocalizationService.T("Urheberrechtshinweis für die Auswahl:"),
                                                           initial,
                                                           "Setzen")
                If entered Is Nothing Then Return
                ApplyCopyrightToSelection(entered, images)
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.SetCopyright", ex)
            End Try
        End Function

        ''' <summary>Schreibt den Hinweis an die Bilder und meldet, was daraus geworden ist. Getrennt
        ''' gemeldet wird, was in die DATEI ging und was in eine Beistelldatei daneben - das ist der
        ''' Unterschied, der zaehlt, wenn man die Datei weitergibt.</summary>
        Friend Sub ApplyCopyrightToSelection(copyrightText As String, Optional preset As IList(Of ImageItem) = Nothing)
            Dim images = GetPlaceTargets(preset)
            If images.Count = 0 Then Return
            Dim text = CopyrightService.NormalizeText(copyrightText)
            If text.Length = 0 Then Return

            Dim inFile = 0, besideFile = 0, failed = 0
            For Each item In images
                Dim result = CopyrightService.WriteCopyright(item.FilePath, text)
                If Not result.Success Then
                    failed += 1
                ElseIf result.Target = CopyrightTarget.EmbeddedExif Then
                    inFile += 1
                Else
                    besideFile += 1
                End If
            Next

            If failed = 0 Then
                StatusText = String.Format(LocalizationService.T("Copyright gesetzt: {0} in der Datei, {1} in einer Beistelldatei"),
                                           inFile, besideFile)
            Else
                StatusText = String.Format(LocalizationService.T("Copyright gesetzt für {0} von {1} Bildern"),
                                           inFile + besideFile, images.Count)
            End If

            ' Das Infopanel zeigt den Hinweis in der Allgemein-Ansicht - es stuende sonst beim alten
            ' Stand. Refresh MUSS dabei sein: der Pfad hat sich nicht geaendert, nur sein Inhalt,
            ' und bei gleichem Pfad steigt das Panel sofort wieder aus, ohne neu zu lesen.
            UpdateInfoPanelTarget()
            InfoPanel.Refresh()
            RefreshContextActions()
        End Sub

        Public Async Function RemovePlaceFromImageItemsAsync(items As IList(Of ImageItem)) As Task
            If items Is Nothing OrElse items.Count = 0 Then Return
            Await RemovePlaceFromSelectedAsync(items)
        End Function

        Private Function GetSelectedImageItems() As List(Of ImageItem)
            Dim selected = If(SelectedItems IsNot Nothing AndAlso SelectedItems.Count > 0,
                              SelectedItems.AsEnumerable(),
                              If(_selectedItem Is Nothing, Enumerable.Empty(Of ImageItem)(), {_selectedItem}))
            Return selected.Where(Function(i) i IsNot Nothing AndAlso i.IsImage).ToList()
        End Function

        ''' <summary>Persistiert eine Bewertung ans passende Backend: Immich-Items an den Server
        ''' (Rückrichtung), lokale Dateien in den SQLite-Katalog samt XMP-Sidecar.</summary>
        Private Sub PersistRating(item As ImageItem, rating As Integer, before As Integer)
            If item Is Nothing Then Return
            If item.IsImmichAsset Then
                WriteToImmich(Function() ImmichService.SetRatingAsync(item.ImmichAssetId, rating),
                                   Sub()
                                       item.Rating = before
                                       Me.RaisePropertyChanged(NameOf(SelectedRating))
                                   End Sub)
            Else
                LibraryService.Instance.SetRating(item.FilePath, rating, syncToXmp:=True)
            End If
        End Sub

        ''' <summary>Die Schreibwege des Infopanels. Lokale Bilder gebuendelt in den Katalog,
        ''' Immich-Elemente einzeln an den Server - mit Ruecknahme, falls der ablehnt. Ohne diese
        ''' Teilung landete eine Bewertung unter dem Pseudo-Pfad "immich://..." im Katalog, der
        ''' Server saehe sie nie, und der naechste Abgleich raeumte sie wieder weg.</summary>
        Friend Sub ApplyRatingTo(items As IList(Of ImageItem), rating As Integer)
            If items Is Nothing OrElse items.Count = 0 Then Return
            ' Alte Werte VOR dem Setzen sichern - nur damit kann ein abgelehnter Immich-Schreibvorgang
            ' die Kachel wieder auf ihren echten Stand zurueckdrehen.
            Dim beforePerItem = items.ToDictionary(Function(i) i, Function(i) i.Rating)
            For Each item In items
                item.Rating = rating
            Next
            Dim localPaths = items.Where(Function(i) Not i.IsImmichAsset).Select(Function(i) i.FilePath).ToList()
            If localPaths.Count > 0 Then LibraryService.Instance.SetRatingForMany(localPaths, rating, syncToXmp:=True)
            For Each item In items.Where(Function(i) i.IsImmichAsset)
                PersistRating(item, rating, beforePerItem(item))
            Next
            Me.RaisePropertyChanged(NameOf(SelectedRating))
            If _sortMode = "Rating" Then FilterAndSort()
        End Sub

        Friend Sub ApplyFavoriteTo(items As IList(Of ImageItem), value As Boolean)
            If items Is Nothing OrElse items.Count = 0 Then Return
            For Each item In items
                Dim before = item.IsFavorite
                item.IsFavorite = value
                PersistFavorite(item, value, before)
            Next
            Me.RaisePropertyChanged(NameOf(SelectedIsFavorite))
            If _sortMode = "Favorite" Then FilterAndSort()
        End Sub

        ''' <summary>Das Farbetikett ist bewusst rein lokal und wandert nicht zum Server; der
        ''' Pseudo-Pfad ist dafuer ein stabiler Schluessel in der Bibliothek.</summary>
        Friend Sub ApplyColorLabelTo(items As IList(Of ImageItem), colorLabel As String)
            If items Is Nothing OrElse items.Count = 0 Then Return
            LibraryService.Instance.SetColorLabelForMany(items.Select(Function(i) i.FilePath), If(colorLabel, ""), syncToXmp:=True)
            If _filterColorLabels.Count > 0 Then FilterAndSort()
        End Sub

        Friend Sub ApplyTagTo(items As IList(Of ImageItem), tag As String, add As Boolean)
            If items Is Nothing OrElse items.Count = 0 OrElse String.IsNullOrEmpty(tag) Then Return
            For Each item In items
                If item.IsImmichAsset Then
                    If add Then
                        Dim ignored = ImmichService.AddTagToAssetAsync(item.ImmichAssetId, tag)
                    Else
                        Dim ignored = ImmichService.RemoveTagFromAssetAsync(item.ImmichAssetId, tag)
                    End If
                ElseIf item.IsNextcloudAsset Then
                    ' Auf diesem Server sind Stichwoerter System-Tags des Kerns, dieselben, die
                    ' Memories liest. Geschrieben wird ueber die Dateikennung - sie steht am Element,
                    ' anders als der Pfad im Dateibaum braucht sie kein Nachladen.
                    Dim ignored = WriteNextcloudTagAsync(item, tag, add)
                Else
                    LibraryService.Instance.SetTags(item.FilePath, If(item.Tags, New List(Of String)()), syncToXmp:=True)
                End If
            Next
            RefreshTagFilterOptions()
        End Sub

        ''' <summary>Schreibt ein Stichwort an eine Nextcloud-Aufnahme oder loest es.
        '''
        ''' Ein Fehlschlag bleibt nicht stumm: die Kachel zeigte das Stichwort sonst als gesetzt an,
        ''' waehrend der Server es abgelehnt hat.</summary>
        Private Async Function WriteNextcloudTagAsync(item As ImageItem, tag As String, add As Boolean) As Task
            Try
                Dim ok = If(add,
                            Await NextcloudService.AddTagAsync(item.NextcloudFileId, tag),
                            Await NextcloudService.RemoveTagAsync(item.NextcloudFileId, tag))
                If Not ok Then
                    StatusText = If(String.IsNullOrEmpty(NextcloudService.LastError),
                                    LocalizationService.T("Stichwort konnte nicht geschrieben werden"), NextcloudService.LastError)
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Nextcloud.WriteTag", ex)
            End Try
        End Function

        ''' <summary>Persistiert den Favoriten-Status ans passende Backend.
        '''
        ''' Nextcloud kennt den Favoriten als WebDAV-Eigenschaft an der Datei, also geht er dorthin
        ''' und nicht in den lokalen Katalog. Sterne und Farbetikett dagegen kennt dieser Server
        ''' NICHT - die bleiben bewusst lokal (siehe PersistRating und PersistColorLabel).</summary>
        Private Sub PersistFavorite(item As ImageItem, value As Boolean, before As Boolean)
            If item Is Nothing Then Return
            If item.IsImmichAsset Then
                WriteToImmich(Function() ImmichService.SetFavoriteAsync(item.ImmichAssetId, value),
                                   Sub()
                                       item.IsFavorite = before
                                       Me.RaisePropertyChanged(NameOf(SelectedIsFavorite))
                                   End Sub)
            ElseIf item.IsNextcloudAsset Then
                WriteToImmich(Function() SetNextcloudFavoriteAsync(item, value),
                                   Sub()
                                       item.IsFavorite = before
                                       Me.RaisePropertyChanged(NameOf(SelectedIsFavorite))
                                   End Sub)
            Else
                LibraryService.Instance.SetFavorite(item.FilePath, value)
            End If
        End Sub

        ''' <summary>Der Favorit braucht den Pfad im Dateibaum. Steht der am Element noch nicht (die
        ''' Einzelheiten kommen erst mit der sichtbaren Kachel), wird er hier nachgeholt.</summary>
        Private Async Function SetNextcloudFavoriteAsync(item As ImageItem, value As Boolean) As Task(Of Boolean)
            Dim pathInTree = item.NextcloudPath
            If String.IsNullOrEmpty(pathInTree) Then
                Dim info = Await NextcloudService.GetInfoAsync(item.NextcloudFileId)
                If info Is Nothing Then Return False
                item.ApplyNextcloudMetadata(info)
                pathInTree = item.NextcloudPath
            End If
            If String.IsNullOrEmpty(pathInTree) Then Return False
            Return Await NextcloudService.SetFavoriteAsync(pathInTree, value)
        End Function

        ''' <summary>
        ''' Schreibt eine Änderung an Immich und macht sie in der Anzeige RÜCKGÄNGIG, wenn der Server
        ''' sie nicht angenommen hat.
        '''
        ''' Vorher liefen diese Aufrufe als "Dim ignored = ..." ins Leere: die Kachel zeigte Sterne oder
        ''' Herz als gespeichert an, während der Server 403 oder 500 gemeldet hatte - beim Stapelsetzen
        ''' gleich für viele Fotos. Der Dienst selbst wirft nicht (er fängt intern und liefert
        ''' False); das Try/Catch hier deckt nur den Rest ab.
        ''' </summary>
        Private Async Sub WriteToImmich(vorgang As Func(Of Task(Of Boolean)), zuruecknehmen As Action)
            Dim ok As Boolean = False
            Try
                ok = Await vorgang()
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.ImmichWrite", ex)
            End Try
            If ok Then Return
            Try
                zuruecknehmen?.Invoke()
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.ImmichRevert", ex)
            End Try
            StatusText = LocalizationService.T("Änderung konnte nicht an Immich übertragen werden")
        End Sub

        Private Sub SetSelectedRating(ratingText As String)
            Dim rating As Integer
            If Not Integer.TryParse(ratingText, rating) Then Return
            Dim images = GetSelectedImageItems()
            If images.Count = 0 Then Return

            Dim currentRating = SelectedRating
            Dim targetRating = If(currentRating = rating, 0, rating)
            ' Alte Werte VOR dem Setzen sichern - nur damit kann ein abgelehnter Immich-Schreibvorgang
            ' die Kachel wieder auf ihren echten Stand zurückdrehen.
            Dim vorherProItem = images.ToDictionary(Function(i) i, Function(i) i.Rating)
            For Each item In images
                item.Rating = targetRating
            Next
            ' Lokale gebündelt (ein DB-/XMP-Durchlauf), Immich-Items einzeln an den Server.
            Dim localPaths = images.Where(Function(i) Not i.IsImmichAsset).Select(Function(i) i.FilePath).ToList()
            If localPaths.Count > 0 Then LibraryService.Instance.SetRatingForMany(localPaths, targetRating, syncToXmp:=True)
            For Each im In images.Where(Function(i) i.IsImmichAsset)
                PersistRating(im, targetRating, vorherProItem(im))
            Next

            Me.RaisePropertyChanged(NameOf(SelectedRating))
            If _sortMode = "Rating" Then FilterAndSort()
        End Sub

        ''' <summary>Setzt das Farbetikett auf die GANZE Auswahl - der Weg der Tastenkürzel ALT+1
        ''' bis ALT+9. Ein leerer Wert (ALT+0) nimmt das Etikett weg, dieselbe Farbe noch einmal
        ''' ebenfalls (Toggle wie bei den Sternen).
        '''
        ''' Verglichen wird gegen das Etikett der AUSWAHL, nicht gegen das eines einzelnen Bildes:
        ''' tragen die markierten Bilder verschiedene Farben, gilt das als gemischt - dann setzt die
        ''' Taste die Farbe, statt sie wegzunehmen. Sonst hinge das Ergebnis daran, welches Bild
        ''' zufällig zuerst in der Auswahl steht.</summary>
        Private Sub SetSelectedColorLabel(colorLabel As String)
            Dim images = GetSelectedImageItems()
            If images.Count = 0 Then Return

            Dim value = If(colorLabel, "")
            Dim common = If(images(0).ColorLabel, "")
            If images.Any(Function(i) Not String.Equals(If(i.ColorLabel, ""), common, StringComparison.OrdinalIgnoreCase)) Then common = ""
            Dim target = If(value.Length > 0 AndAlso String.Equals(common, value, StringComparison.OrdinalIgnoreCase), "", value)

            For Each item In images
                item.ColorLabel = target
            Next
            LibraryService.Instance.SetColorLabelForMany(images.Select(Function(i) i.FilePath), target, syncToXmp:=True)
            If _filterColorLabels.Count > 0 Then FilterAndSort()
        End Sub

        ''' <summary>Setzt das Farbetikett. Ist das Bild Teil der aktuellen Auswahl, bekommt die GANZE
        ''' Auswahl das Etikett (ein Rechtsklick auf ein markiertes Bild meint die Markierung);
        ''' erneutes Setzen derselben Farbe entfernt sie (Toggle wie bei den Sternen). Immich-Items
        ''' funktionieren mit: der Pseudo-Pfad ist ein stabiler library.db-Schlüssel - das Etikett
        ''' ist aber rein lokal und wandert nicht zum Server.</summary>
        Public Sub SetItemColorLabel(item As ImageItem, colorLabel As String)
            If item Is Nothing OrElse Not item.IsImage Then Return
            Dim value = If(colorLabel, "")
            Dim target = If(String.Equals(item.ColorLabel, value, StringComparison.Ordinal), "", value)

            Dim targets As New List(Of ImageItem)()
            If SelectedItems IsNot Nothing AndAlso SelectedItems.Contains(item) Then
                targets.AddRange(SelectedItems.Where(Function(i) i IsNot Nothing AndAlso i.IsImage))
            End If
            If targets.Count = 0 Then targets.Add(item)

            For Each t In targets
                t.ColorLabel = target
            Next
            LibraryService.Instance.SetColorLabelForMany(targets.Select(Function(t) t.FilePath), target, syncToXmp:=True)
            If _filterColorLabels.Count > 0 Then FilterAndSort()
        End Sub

        Public Sub SetItemRating(item As ImageItem, rating As Integer)
            If item Is Nothing OrElse Not item.IsImage Then Return
            Dim targetRating = If(item.Rating = rating, 0, rating)
            Dim before = item.Rating
            item.Rating = targetRating
            PersistRating(item, targetRating, before)

            If Object.ReferenceEquals(item, _selectedItem) OrElse (SelectedItems IsNot Nothing AndAlso SelectedItems.Contains(item)) Then
                Me.RaisePropertyChanged(NameOf(SelectedRating))
            End If

            If _sortMode = "Rating" Then FilterAndSort()
        End Sub

        Public Sub SelectRange(anchor As ImageItem, target As ImageItem)
            If anchor Is Nothing OrElse target Is Nothing Then
                SelectOnly(target)
                Return
            End If
            If anchor.IsParentFolderEntry Then
                If target.IsParentFolderEntry Then
                    SetNavigationOnlySelection(target)
                Else
                    SelectOnly(target)
                End If
                Return
            End If
            If target.IsParentFolderEntry Then
                SetNavigationOnlySelection(target)
                Return
            End If
            Dim startIndex = Items.IndexOf(anchor)
            Dim endIndex = Items.IndexOf(target)
            If startIndex < 0 OrElse endIndex < 0 Then
                SelectOnly(target)
                Return
            End If
            If startIndex > endIndex Then
                Dim tmp = startIndex
                startIndex = endIndex
                endIndex = tmp
            End If
            ReplaceSelection(Items.Skip(startIndex).Take(endIndex - startIndex + 1))
        End Sub

        Public Function GetFirstNavigableIndex() As Integer
            If Items.Count = 0 Then Return -1
            Return 0
        End Function

        Public Function FindNavigableIndex(startIndex As Integer, offset As Integer) As Integer
            If Items.Count = 0 Then Return -1
            Dim idx = Math.Max(0, Math.Min(Items.Count - 1, startIndex))
            Dim direction = If(offset >= 0, 1, -1)
            Dim remaining = Math.Abs(offset)

            Do
                idx += direction
                If idx < 0 OrElse idx >= Items.Count Then Exit Do
                remaining -= 1
                If remaining = 0 Then Return idx
            Loop

            Return -1
        End Function

        Private _initialFolderNode As FolderNode

        Public ReadOnly Property InitialFolderNode As FolderNode
            Get
                Return _initialFolderNode
            End Get
        End Property

        Private Sub InitializeFolderTree()
            Dim homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            Dim homeNode As FolderNode = Nothing
            If Directory.Exists(homePath) Then
                homeNode = New FolderNode(LocalizationService.T("Persönlicher Ordner"), homePath)
                FolderTree.Add(homeNode)
                homeNode.EnsureChildrenLoaded()
                homeNode.IsExpanded = True
            End If

            If OperatingSystem.IsWindows() Then
                For Each drive In DriveInfo.GetDrives().Where(Function(d) d.IsReady)
                    Dim label = If(String.IsNullOrWhiteSpace(drive.VolumeLabel), drive.Name, $"{drive.VolumeLabel} ({drive.Name})")
                    FolderTree.Add(New FolderNode(label, drive.RootDirectory.FullName))
                Next
            Else
                Dim rootPath = IO.Path.GetPathRoot(homePath)
                If String.IsNullOrEmpty(rootPath) Then rootPath = IO.Path.DirectorySeparatorChar.ToString()
                If Directory.Exists(rootPath) Then FolderTree.Add(New FolderNode(LocalizationService.T("Root"), rootPath))
            End If

            Dim picPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            If Directory.Exists(picPath) Then _initialFolderNode = FindFolderNode(FolderTree, picPath)

            If _initialFolderNode Is Nothing Then
                _initialFolderNode = If(homeNode, FolderTree.FirstOrDefault())
            End If
        End Sub

        Private Sub InitializeVirtualNavigation()
            SearchTree.Clear()
            ImmichSearchTree.Clear()
            NextcloudSearchTree.Clear()
            ' "Neue Suche" steht in JEDEM Tab, und der Knoten traegt die Quelle: der Dialog fragt sie
            ' nicht mehr ab, sondern uebernimmt den Bereich, in dem geklickt wurde.
            SearchTree.Add(New VirtualNavigationNode(LocalizationService.T("Neue Suche"), "NewSearch") With {.Source = "Local"})
            ImmichSearchTree.Add(New VirtualNavigationNode(LocalizationService.T("Neue Suche"), "NewSearch") With {.Source = "Immich"})
            NextcloudSearchTree.Add(New VirtualNavigationNode(LocalizationService.T("Neue Suche"), "NewSearch") With {.Source = "Nextcloud"})
            _savedSearches.Clear()
            _savedSearches.AddRange(SearchListService.Load())
            For Each search In _savedSearches
                SearchTreeForSource(search.Source).Add(CreateSavedSearchNode(search))
            Next
            RefreshFavorites()
        End Sub

        ''' <summary>Der Suchbaum, in den eine Suche dieser Quelle gehoert.</summary>
        Private Function SearchTreeForSource(source As String) As ObservableCollection(Of VirtualNavigationNode)
            Select Case SearchListService.NormalizeSource(source)
                Case "Immich" : Return ImmichSearchTree
                Case "Nextcloud" : Return NextcloudSearchTree
                Case Else : Return SearchTree
            End Select
        End Function

        ' ── Favoriten ────────────────────────────────────────────────────────────

        ''' <summary>Baut den Favoriten-Tab aus der gespeicherten Liste neu auf. Ziele, die es nicht
        ''' mehr gibt (geloeschte Suche, entfernter Ordner), werden als "fehlt" markiert statt still
        ''' zu verschwinden - sonst wundert man sich, wo der Favorit hin ist.</summary>
        Public Sub RefreshFavorites()
            FavoritesTree.Clear()
            ' Nach Namen sortiert - kulturabhaengig, damit Umlaute dort
            ' stehen, wo man sie sucht. Die gespeicherte Reihenfolge bleibt davon unberuehrt.
            For Each fav In FavoritesService.Load().
                    OrderBy(Function(f) If(f.Name, ""), StringComparer.CurrentCultureIgnoreCase)
                Dim node = CreateFavoriteNode(fav)
                If node IsNot Nothing Then FavoritesTree.Add(node)
            Next
            ' Der Baum wurde neu aufgebaut - die gemerkte Auswahl zeigt sonst auf ein weggeworfenes
            ' Objekt, und die Wiederherstellung nach einem Moduswechsel liefe ins Leere (kein Rahmen).
            ' Ueber den stabilen Schluessel wieder auf den NEUEN Knoten zeigen.
            If SelectedFavoriteNode IsNot Nothing Then
                Dim key = SelectedFavoriteNode.FavoriteKey
                SelectedFavoriteNode = FavoritesTree.FirstOrDefault(
                    Function(n) String.Equals(n.FavoriteKey, key, StringComparison.Ordinal))
            End If
            Me.RaisePropertyChanged(NameOf(HasFavorites))
        End Sub

        ' ── Seitenleisten-Tabs ───────────────────────────────────────────────────

        Private _sidebarTab As String = "Folders"

        ''' <summary>Aktiver Tab der Navigations-Seitenleiste: "Folders", "Immich" oder "Favorites".
        ''' Der Immich-Tab existiert nur, wenn Immich hinterlegt ist (HasImmich) - faellt die
        ''' Konfiguration weg, waehrend er offen ist, springt die Auswahl auf Ordner zurueck.</summary>
        Public Property SidebarTab As String
            Get
                Return _sidebarTab
            End Get
            Set(value As String)
                Dim normalized = If(value, "Folders").Trim()
                Select Case normalized.ToLowerInvariant()
                    Case "immich" : normalized = "Immich"
                    Case "nextcloud" : normalized = "Nextcloud"
                    Case "favorites" : normalized = "Favorites"
                    Case Else : normalized = "Folders"
                End Select
                If normalized = "Immich" AndAlso Not HasImmich Then normalized = "Folders"
                If normalized = "Nextcloud" AndAlso Not HasNextcloud Then normalized = "Folders"
                If String.Equals(_sidebarTab, normalized, StringComparison.Ordinal) Then Return
                _sidebarTab = normalized
                RaiseSidebarTabProperties()
            End Set
        End Property

        Public ReadOnly Property IsFoldersTab As Boolean
            Get
                Return String.Equals(_sidebarTab, "Folders", StringComparison.Ordinal)
            End Get
        End Property

        Public ReadOnly Property IsImmichTab As Boolean
            Get
                Return String.Equals(_sidebarTab, "Immich", StringComparison.Ordinal) AndAlso HasImmich
            End Get
        End Property

        Public ReadOnly Property IsNextcloudTab As Boolean
            Get
                Return String.Equals(_sidebarTab, "Nextcloud", StringComparison.Ordinal) AndAlso HasNextcloud
            End Get
        End Property

        Public ReadOnly Property IsFavoritesTab As Boolean
            Get
                Return String.Equals(_sidebarTab, "Favorites", StringComparison.Ordinal)
            End Get
        End Property

        Public ReadOnly Property SetSidebarTabCommand As ICommand

        Private Sub RaiseSidebarTabProperties()
            Me.RaisePropertyChanged(NameOf(SidebarTab))
            Me.RaisePropertyChanged(NameOf(IsFoldersTab))
            Me.RaisePropertyChanged(NameOf(IsImmichTab))
            Me.RaisePropertyChanged(NameOf(IsNextcloudTab))
            Me.RaisePropertyChanged(NameOf(IsFavoritesTab))
        End Sub

        Public ReadOnly Property HasFavorites As Boolean
            Get
                Return FavoritesTree IsNot Nothing AndAlso FavoritesTree.Count > 0
            End Get
        End Property

        ''' <summary>Ordner-Favorit oeffnen: Ansicht wechseln UND den Ordnerbaum nachziehen, damit
        ''' der Ordner-Tab denselben Stand zeigt (sonst steht dort noch die alte Markierung).</summary>
        ''' <summary>Wechsel in einen echten Ordner: Tab auf Ordner ziehen und die
        ''' Favoriten-Markierung loesen - sonst zeigte die Rueckkehr aus dem Viewer den
        ''' Favoriten-Tab, obwohl laengst im Ordnerbaum navigiert wurde.</summary>
        Friend Sub NoteFolderNavigation()
            SelectedFavoriteNode = Nothing
            SidebarTab = "Folders"
        End Sub

        Private Sub OpenFolderFromFavorite(folderPath As String)
            If String.IsNullOrWhiteSpace(folderPath) OrElse Not IO.Directory.Exists(folderPath) Then
                StatusText = LocalizationService.T("Das Ziel dieses Favoriten existiert nicht mehr")
                Return
            End If
            ' Der Tab bleibt auf Favoriten: die daraus folgende Baummarkierung laeuft in der View
            ' unter _restoringFolderTreeSelection und meldet deshalb keine Nutzer-Navigation.
            CurrentFolder = folderPath
            LoadFolderImages(folderPath)
            SelectFolderInTreeByPath(folderPath)
        End Sub

        ''' <summary>In welchem Tab ein Knoten zu Hause ist. Favoriten gewinnen: ein aus dem
        ''' Favoriten-Tab geoeffnetes Immich-Album soll beim Zurueckkommen wieder DORT stehen.</summary>
        Private Function TabForNode(node As VirtualNavigationNode) As String
            If node Is Nothing Then Return SidebarTab
            If node.IsFavoriteNode Then Return "Favorites"
            Select Case node.Kind
                Case "ImmichAll", "ImmichAlbum", "ImmichPerson", "ImmichPlace", "ImmichPeopleRoot", "ImmichPlacesRoot",
                     "ImmichTrash"
                    Return "Immich"
                Case "NextcloudAll", "NextcloudAlbum", "NextcloudPerson", "NextcloudPlace",
                     "NextcloudPeopleRoot", "NextcloudPlacesRoot", "NextcloudTag", "NextcloudTagsRoot",
                     "NextcloudTrash"
                    Return "Nextcloud"
                Case "SavedSearch", "NewSearch"
                    Select Case SearchListService.NormalizeSource(node.Source)
                        Case "Immich" : Return "Immich"
                        Case "Nextcloud" : Return "Nextcloud"
                        Case Else : Return "Folders"
                    End Select
                Case Else
                    Return SidebarTab
            End Select
        End Function

        ''' <summary>Zuletzt aus dem Favoriten-Tab geoeffneter Knoten - die GalleryView stellt damit
        ''' nach einem Moduswechsel die Markierung im Favoriten-Baum wieder her (Gegenstueck zu
        ''' SelectedSearchNode/SelectedImmichNode fuer die anderen Baeume).</summary>
        Public Property SelectedFavoriteNode As VirtualNavigationNode

        ''' <summary>Name des TreeView, in dem nach einer Neuinstanziierung der GalleryView die
        ''' Markierung wiederherzustellen ist - passend zum aktiven Tab. Nothing, wenn gerade ein
        ''' echter Ordner aktiv ist (dann uebernimmt der Ordnerbaum).</summary>
        Public ReadOnly Property NavigationRestoreTreeName As String
            Get
                If SelectedFavoriteNode IsNot Nothing AndAlso IsFavoritesTab Then Return "FavoritesTreeView"
                If SelectedImmichNode IsNot Nothing Then Return "ImmichTreeView"
                If SelectedNextcloudNode IsNot Nothing Then Return "NextcloudTreeView"
                If SelectedSearchNode IsNot Nothing Then
                    Select Case SearchListService.NormalizeSource(SelectedSearchNode.Source)
                        Case "Immich" : Return "ImmichSearchTreeView"
                        Case "Nextcloud" : Return "NextcloudSearchTreeView"
                        Case Else : Return "SearchTreeView"
                    End Select
                End If
                Return Nothing
            End Get
        End Property

        ''' <summary>Knoten, der zu NavigationRestoreTreeName gehoert.</summary>
        Public ReadOnly Property NavigationRestoreNode As VirtualNavigationNode
            Get
                If SelectedFavoriteNode IsNot Nothing AndAlso IsFavoritesTab Then Return SelectedFavoriteNode
                Return If(SelectedImmichNode, If(SelectedNextcloudNode, SelectedSearchNode))
            End Get
        End Property

        Private Function CreateFavoriteNode(fav As FavoriteEntry) As VirtualNavigationNode
            If fav Is Nothing Then Return Nothing
            Select Case fav.Kind
                Case "Search"
                    Dim search = _savedSearches.FirstOrDefault(Function(s) String.Equals(s.Id, fav.SearchId, StringComparison.OrdinalIgnoreCase))
                    If search Is Nothing Then
                        Return New VirtualNavigationNode(fav.Name, "FavoriteMissing") With {.FavoriteKey = fav.Key, .IsRemovable = True}
                    End If
                    Dim node = CreateSavedSearchNode(search)
                    node.Name = If(String.IsNullOrWhiteSpace(fav.Name), search.Name, fav.Name)
                    node.FavoriteKey = fav.Key
                    Return node
                Case "Immich"
                    If Not ImmichService.IsConfigured Then Return Nothing
                    Return New VirtualNavigationNode(fav.Name, fav.NodeKind) With {
                        .Id = fav.NodeId,
                        .Query = fav.NodeId,
                        .FavoriteKey = fav.Key
                    }
                Case "Nextcloud"
                    ' Ohne eingerichteten Server bleibt der Favorit unsichtbar statt als tote Zeile
                    ' stehen - genau wie bei Immich.
                    If Not NextcloudService.IsConfigured Then Return Nothing
                    Return New VirtualNavigationNode(fav.Name, fav.NodeKind) With {
                        .Id = fav.NodeId,
                        .Query = fav.NodeId,
                        .FavoriteKey = fav.Key
                    }
                Case Else
                    If Not IO.Directory.Exists(fav.Path) Then
                        Return New VirtualNavigationNode(fav.Name, "FavoriteMissing") With {.FavoriteKey = fav.Key, .IsRemovable = True}
                    End If
                    Return New VirtualNavigationNode(fav.Name, "FavoriteFolder") With {
                        .Query = fav.Path,
                        .RootFolder = fav.Path,
                        .FavoriteKey = fav.Key
                    }
            End Select
        End Function

        ''' <summary>Legt einen Ordner als Favorit an (Kontextmenue im Ordnerbaum).</summary>
        Public Sub AddFolderFavorite(folderPath As String)
            If String.IsNullOrWhiteSpace(folderPath) OrElse Not IO.Directory.Exists(folderPath) Then Return
            Dim name = IO.Path.GetFileName(folderPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
            If String.IsNullOrWhiteSpace(name) Then name = folderPath
            AnnounceFavorite(FavoritesService.Add(New FavoriteEntry With {.Kind = "Folder", .Name = name, .Path = folderPath}), name)
        End Sub

        ''' <summary>Legt einen Immich-Knoten oder eine gespeicherte Suche als Favorit an
        ''' (Kontextmenue im jeweiligen Baum).</summary>
        Public Sub AddNodeFavorite(node As VirtualNavigationNode)
            If node Is Nothing Then Return
            Dim entry As FavoriteEntry = Nothing
            Select Case node.Kind
                Case "SavedSearch"
                    If String.IsNullOrWhiteSpace(node.Id) Then Return
                    entry = New FavoriteEntry With {.Kind = "Search", .Name = node.Name, .SearchId = node.Id}
                Case "ImmichAll", "ImmichAlbum", "ImmichPerson", "ImmichPlace"
                    entry = New FavoriteEntry With {
                        .Kind = "Immich", .Name = node.Name, .NodeKind = node.Kind,
                        .NodeId = If(String.IsNullOrWhiteSpace(node.Id), If(node.Query, ""), node.Id)}
                Case "NextcloudAll", "NextcloudAlbum", "NextcloudPerson", "NextcloudPlace", "NextcloudTag"
                    ' Ohne diesen Zweig blieb "Zu Favoriten hinzufügen" auf einem Nextcloud-Knoten
                    ' WIRKUNGSLOS: der Eintrag war sichtbar, es entstand nur kein Favorit
                    ' (Nutzerbefund 2026-08-10).
                    entry = New FavoriteEntry With {
                        .Kind = "Nextcloud", .Name = node.Name, .NodeKind = node.Kind,
                        .NodeId = If(String.IsNullOrWhiteSpace(node.Id), If(node.Query, ""), node.Id)}
                Case "FavoriteFolder"
                    AddFolderFavorite(node.RootFolder)
                    Return
            End Select
            If entry Is Nothing Then Return
            AnnounceFavorite(FavoritesService.Add(entry), node.Name)
        End Sub

        Private Sub AnnounceFavorite(added As Boolean, name As String)
            If added Then
                RefreshFavorites()
                StatusText = String.Format(LocalizationService.T("{0} zu den Favoriten hinzugefügt"), name)
            Else
                StatusText = String.Format(LocalizationService.T("{0} ist bereits ein Favorit"), name)
            End If
        End Sub

        Public Sub RemoveFavorite(node As VirtualNavigationNode)
            If node Is Nothing OrElse String.IsNullOrWhiteSpace(node.FavoriteKey) Then Return
            If FavoritesService.Remove(node.FavoriteKey) Then
                RefreshFavorites()
                StatusText = String.Format(LocalizationService.T("{0} aus den Favoriten entfernt"), node.Name)
            End If
        End Sub


        ''' <summary>Gibt False zurück, wenn "Neue Suche" per Dialog-Abbruch verworfen wurde - der
        ''' Aufrufer (GalleryView) nutzt das, um die sichtbare Baumauswahl in dem Fall wieder auf
        ''' den tatsächlich aktiven Ordner statt auf den "Neue Suche"-Eintrag zurückzusetzen.</summary>
        Public Async Function OpenVirtualNavigationNode(node As VirtualNavigationNode) As Task(Of Boolean)
            If node Is Nothing Then Return True
            ' Der Tab folgt dem geoeffneten Ziel: kommt man aus Viewer/Editor in die Galerie
            ' zurueck, steht wieder derselbe Tab offen wie beim Verlassen (die GalleryView wird
            ' beim Moduswechsel neu gebaut, das ViewModel ueberlebt - siehe RestoreNavigationTab).
            SidebarTab = TabForNode(node)
            SelectedFavoriteNode = If(node.IsFavoriteNode, node, Nothing)

            ' EIN WECHSEL IM BAUM BEENDET JEDE KNOPFAUSWAHL. Fuer den Ordnerbaum steht das in
            ' NavigateToFolderAsync; die virtuellen Ziele (Favoriten, Immich, Nextcloud, gespeicherte
            ' Suchen) liefen bisher daran vorbei - dort blieb ein Personen-, Orts- oder
            ' Stichwortfilter stehen und siebte still die Liste des neuen Ziels, waehrend die drei
            ' Knoepfe ihre Akzentfarbe behielten (Nutzerbefund 2026-08-16). Die reinen Klappknoten
            ' oeffnen gar keine Ansicht und lassen die Auswahl deshalb, wie sie ist.
            Select Case node.Kind
                Case "ImmichPeopleRoot", "ImmichPlacesRoot",
                     "NextcloudPeopleRoot", "NextcloudPlacesRoot", "NextcloudTagsRoot",
                     "FavoriteMissing"
                    ' Klappknoten und tote Favoriten: kein Zielwechsel.
                Case Else
                    ClearButtonFiltersSilently()
            End Select

            Select Case node.Kind
                Case "NewSearch"
                    Return Await OpenSearchDialog(node.Source)
                Case "SavedSearch"
                    OpenSavedSearch(node)
                Case "ImmichAlbum"
                    Await OpenImmichAlbumAsync(node)
                Case "ImmichAll"
                    Await OpenImmichAllAsync(node)
                Case "ImmichPerson"
                    Await OpenImmichPersonAsync(node)
                Case "ImmichPlace"
                    Await OpenImmichPlaceAsync(node)
                Case "ImmichPeopleRoot", "ImmichPlacesRoot"
                    ' Elternknoten: nur auf-/zuklappen, keine Ansicht öffnen.
                    node.IsExpanded = Not node.IsExpanded
                Case "ImmichTrash"
                    Await OpenImmichTrashAsync(node)
                Case "NextcloudAll"
                    Await OpenNextcloudAllAsync(node)
                Case "NextcloudAlbum"
                    Await OpenNextcloudAlbumAsync(node)
                Case "NextcloudPerson"
                    Await OpenNextcloudClusterAsync(node, "recognize")
                Case "NextcloudPlace"
                    Await OpenNextcloudClusterAsync(node, "places")
                Case "NextcloudTag"
                    Await OpenNextcloudClusterAsync(node, "tags")
                Case "NextcloudTrash"
                    Await OpenNextcloudTrashAsync(node)
                Case "NextcloudPeopleRoot", "NextcloudPlacesRoot", "NextcloudTagsRoot"
                    ' Klappknoten wie im Immich-Baum: nur auf- und zuklappen.
                    node.IsExpanded = Not node.IsExpanded
                Case "FavoriteFolder"
                    ' Favorit auf einen Ordner: exakt derselbe Weg wie ein Klick im Ordnerbaum.
                    OpenFolderFromFavorite(node.RootFolder)
                Case "FavoriteMissing"
                    StatusText = LocalizationService.T("Das Ziel dieses Favoriten existiert nicht mehr")
            End Select
            Return True
        End Function

        Public ReadOnly Property HasImmich As Boolean
            Get
                Return ImmichTree IsNot Nothing AndAlso ImmichTree.Count > 0
            End Get
        End Property

        Public ReadOnly Property HasNextcloud As Boolean
            Get
                Return NextcloudTree IsNot Nothing AndAlso NextcloudTree.Count > 0
            End Get
        End Property

        ''' <summary>Baut den Nextcloud-Bereich auf: die Zeitachse und die Alben. Die Alben kommen
        ''' im Hintergrund nach, damit der Baum nicht auf den Server wartet.</summary>
        Private Sub InitializeNextcloud()
            NextcloudTree.Clear()
            SyncSidebarTabWithNextcloud()
            If Not NextcloudService.IsConfigured Then
                ' Wie bei Immich: mit der Quelle verschwinden auch ihre Einträge aus den
                ' Filterknöpfen.
                _nextcloudPeople = New List(Of NextcloudService.NextcloudCluster)()
                _nextcloudPlaces = New List(Of NextcloudService.NextcloudCluster)()
                _nextcloudTags = New List(Of NextcloudService.NextcloudCluster)()
                RefreshPersonFilterOptions()
                RefreshPlaceFilterOptions()
                RefreshTagFilterOptions()
                Return
            End If
            NextcloudTree.Add(New VirtualNavigationNode(LocalizationService.T("Alle Fotos"), "NextcloudAll"))
            SyncSidebarTabWithNextcloud()
            RefreshNextcloudAlbumsAsync()
        End Sub

        ''' <summary>Nach einer Änderung in den Einstellungen neu aufbauen.</summary>
        Public Sub ReinitializeNextcloud()
            InitializeNextcloud()
        End Sub

        ''' <summary>Wie <see cref="SyncSidebarTabWithImmich"/>, nur für den Nextcloud-Reiter:
        ''' verschwindet die Quelle, während ihr Reiter offen ist, fällt die Auswahl auf Ordner
        ''' zurück - sonst zeigt die Seitenleiste einen Reiter, den es nicht mehr gibt.</summary>
        Private Sub SyncSidebarTabWithNextcloud()
            Me.RaisePropertyChanged(NameOf(HasNextcloud))
            If Not HasNextcloud AndAlso String.Equals(_sidebarTab, "Nextcloud", StringComparison.Ordinal) Then
                _sidebarTab = "Folders"
            End If
            RaiseSidebarTabProperties()
        End Sub

        ''' <summary>Holt die Alben und hängt sie unter die Zeitachse. Ein Fehlschlag bleibt still im
        ''' Baum (die Zeitachse steht ja), meldet sich aber in der Statuszeile - ein leerer Baum ohne
        ''' Begründung sähe aus wie "keine Alben vorhanden".</summary>
        Private Sub RefreshNextcloudAlbumsAsync()
            Dim ignored = Task.Run(Async Function()
                                       Try
                                           Dim alben = Await NextcloudService.GetClustersAsync("albums").ConfigureAwait(False)
                                           Dim albumError = NextcloudService.LastError
                                           ' Personen kommen je nach Installation aus "recognize" ODER
                                           ' "facerecognition"; welche da ist, steht nicht fest. Das
                                           ' NICHT eingeschaltete Backend antwortet mit einer
                                           ' Begruendung, nicht mit einer Liste - deshalb einfach
                                           ' beide fragen und nehmen, was etwas liefert.
                                           Dim personen = Await NextcloudService.GetClustersAsync("recognize").ConfigureAwait(False)
                                           If personen.Count = 0 Then personen = Await NextcloudService.GetClustersAsync("facerecognition").ConfigureAwait(False)
                                           Dim orte = Await NextcloudService.GetClustersAsync("places").ConfigureAwait(False)
                                           ' Stichwoerter sind auf diesem Server ein Cluster wie Alben und Orte - anders
                                           ' als bei Immich, wo die Suche keinen Stichwortfilter kennt. Sie koennen
                                           ' deshalb sowohl in den Baum als auch in den Filterknopf.
                                           Dim stichworte = Await NextcloudService.GetClustersAsync("tags").ConfigureAwait(False)
                                           Await Dispatcher.UIThread.InvokeAsync(
                                               Sub()
                                                   For i = NextcloudTree.Count - 1 To 0 Step -1
                                                       Select Case NextcloudTree(i).Kind
                                                           Case "NextcloudAlbum", "NextcloudPeopleRoot", "NextcloudPlacesRoot",
                                                                "NextcloudTagsRoot", "NextcloudTrash"
                                                               NextcloudTree.RemoveAt(i)
                                                       End Select
                                                   Next
                                                   For Each album In alben
                                                       If String.IsNullOrEmpty(album.Id) Then Continue For
                                                       NextcloudTree.Add(New VirtualNavigationNode(album.Name, "NextcloudAlbum") With {.Id = album.Id})
                                                   Next
                                                   AddNextcloudClusterBranch(LocalizationService.T("Personen"), "NextcloudPeopleRoot", "NextcloudPerson", personen)
                                                   AddNextcloudClusterBranch(LocalizationService.T("Orte"), "NextcloudPlacesRoot", "NextcloudPlace", orte)
                                                   AddNextcloudClusterBranch(LocalizationService.T("Stichwörter"), "NextcloudTagsRoot", "NextcloudTag", stichworte)
                                                   ' Der Papierkorb steht ganz unten, wie in jedem Dateiverwalter.
                                                   NextcloudTree.Add(New VirtualNavigationNode(LocalizationService.T("Papierkorb"), "NextcloudTrash"))
                                                   ' Dieselben Listen speisen die FILTERKNOEPFE. Sie werden beim Oeffnen
                                                   ' des Menues synchron gelesen und koennen deshalb nicht selbst auf den
                                                   ' Server warten - der Abruf hier ist ohnehin faellig.
                                                   _nextcloudPeople = personen
                                                   _nextcloudPlaces = orte
                                                   _nextcloudTags = stichworte
                                                   RefreshPersonFilterOptions()
                                                   RefreshPlaceFilterOptions()
                                                   RefreshTagFilterOptions()
                                                   If alben.Count = 0 AndAlso Not String.IsNullOrEmpty(albumError) Then StatusText = albumError
                                                   SyncSidebarTabWithNextcloud()
                                               End Sub)
                                       Catch
                                       End Try
                                   End Function)
        End Sub

        ''' <summary>Haengt einen Klappzweig mit Personen bzw. Orten an. Ist nichts da - Zusatz-App
        ''' nicht eingeschaltet oder noch nichts erkannt -, entsteht auch KEIN leerer Zweig: ein
        ''' Knoten, unter dem nie etwas auftaucht, sieht aus wie ein Fehler.</summary>
        Private Sub AddNextcloudClusterBranch(name As String, rootKind As String, childKind As String,
                                              cluster As List(Of NextcloudService.NextcloudCluster))
            If cluster Is Nothing OrElse cluster.Count = 0 Then Return
            Dim root = New VirtualNavigationNode(name, rootKind)
            For Each entry In cluster
                If String.IsNullOrEmpty(entry.Id) Then Continue For
                ' NUR BENANNTE PERSONEN. Eine frisch erkannte Gruppe hat keinen Namen, und ihre
                ' Kennung ist eine Zahl - "4" und "12" untereinander im Baum sind kein Angebot,
                ' sondern Rauschen. Benannt wird auf dem Server; dieselbe Regel gilt bei Immich.
                If String.Equals(childKind, "NextcloudPerson", StringComparison.Ordinal) AndAlso Not entry.IsNamed Then Continue For
                Dim displayName = If(String.IsNullOrWhiteSpace(entry.Name), entry.Id, entry.Name)
                root.Children.Add(New VirtualNavigationNode(displayName, childKind) With {.Id = entry.Id})
            Next
            If root.Children.Count = 0 Then Return
            NextcloudTree.Add(root)
        End Sub

        ''' <summary>Oeffnet eine Personengruppe oder einen Ort. Beide laufen ueber denselben
        ''' Cluster-Filter der Zeitachse, nur mit anderem Backend-Namen.</summary>
        Private Async Function OpenNextcloudClusterAsync(node As VirtualNavigationNode, backend As String) As Task
            If node Is Nothing OrElse String.IsNullOrWhiteSpace(node.Id) Then Return
            SelectedNextcloudNode = node
            Await LoadNextcloudVirtualFolderAsync(node.Name, Nothing, backend, node.Id)
        End Function

        ''' <summary>Legt ein Nextcloud-Album an. Die Alben gehören der Photos-App und liegen als
        ''' WebDAV-Sammlungen; Memories liest sie nur mit (siehe `NextcloudService`).</summary>
        Public Async Sub CreateNextcloudAlbum()
            Try
                If Not NextcloudService.IsConfigured Then Return
                Dim name = Await _mainVm.ShowInputAsync(AppDialogKind.Input, LocalizationService.T("Neues Album…"),
                                                        LocalizationService.T("Name des Albums:"), "")
                If String.IsNullOrWhiteSpace(name) Then Return
                If Not Await NextcloudService.CreateAlbumAsync(name.Trim()) Then
                    StatusText = If(String.IsNullOrEmpty(NextcloudService.LastError),
                                    LocalizationService.T("Album konnte nicht angelegt werden"), NextcloudService.LastError)
                    Return
                End If
                RefreshNextcloudAlbumsAsync()
                StatusText = String.Format(LocalizationService.T("Album {0} angelegt"), name.Trim())
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryViewModel.CreateNextcloudAlbum", ex)
            End Try
        End Sub

        Public Async Sub RenameNextcloudAlbum(node As VirtualNavigationNode)
            Try
                If node Is Nothing OrElse Not node.IsNextcloudAlbumNode Then Return
                Dim name = Await _mainVm.ShowInputAsync(AppDialogKind.Input, LocalizationService.T("Album umbenennen"),
                                                        LocalizationService.T("Name des Albums:"), node.Name)
                If String.IsNullOrWhiteSpace(name) OrElse String.Equals(name.Trim(), node.Name, StringComparison.Ordinal) Then Return
                If Not Await NextcloudService.RenameAlbumAsync(node.Id, name.Trim()) Then
                    StatusText = NextcloudService.LastError
                    Return
                End If
                RefreshNextcloudAlbumsAsync()
                StatusText = String.Format(LocalizationService.T("Album umbenannt: {0}"), name.Trim())
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryViewModel.RenameNextcloudAlbum", ex)
            End Try
        End Sub

        ''' <summary>Löscht das Album. Die FOTOS bleiben - es verschwinden nur die Verweise, das ist
        ''' am Server gemessen. Deshalb genügt hier eine schlichte Rückfrage.</summary>
        Public Async Sub DeleteNextcloudAlbum(node As VirtualNavigationNode)
            Try
                If node Is Nothing OrElse Not node.IsNextcloudAlbumNode Then Return
                Dim ok = Await _mainVm.ShowConfirmAsync(LocalizationService.T("Album löschen"),
                            String.Format(LocalizationService.T("Album {0} löschen? Die Fotos bleiben erhalten."), node.Name))
                If Not ok Then Return
                If Not Await NextcloudService.DeleteAlbumAsync(node.Id) Then
                    StatusText = NextcloudService.LastError
                    Return
                End If
                Dim warOffen = SelectedNextcloudNode IsNot Nothing AndAlso String.Equals(SelectedNextcloudNode.Id, node.Id, StringComparison.Ordinal)
                RefreshNextcloudAlbumsAsync()
                If warOffen Then
                    Dim alle = NextcloudTree.FirstOrDefault(Function(n) String.Equals(n.Kind, "NextcloudAll", StringComparison.Ordinal))
                    If alle IsNot Nothing Then Await OpenNextcloudAllAsync(alle)
                End If
                StatusText = LocalizationService.T("Album gelöscht")
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryViewModel.DeleteNextcloudAlbum", ex)
            End Try
        End Sub

        ''' <summary>Hängt die ausgewählten Nextcloud-Bilder in ein Album. Braucht den Pfad im
        ''' Dateibaum; steht der am Element noch nicht (die Einzelheiten kommen erst mit der
        ''' sichtbaren Kachel), wird er hier nachgeholt.</summary>
        Public Async Function AddSelectedToNextcloudAlbumAsync(node As VirtualNavigationNode) As Task
            If node Is Nothing OrElse Not node.IsNextcloudAlbumNode Then Return
            Dim items = GetSelectedImageItems().Where(Function(i) i.IsNextcloudAsset).ToList()
            If items.Count = 0 Then Return
            Dim assigned = 0
            For Each item In items
                Dim pathInTree = item.NextcloudPath
                If String.IsNullOrEmpty(pathInTree) Then
                    Dim info = Await NextcloudService.GetInfoAsync(item.NextcloudFileId)
                    If info Is Nothing Then Continue For
                    item.ApplyNextcloudMetadata(info)
                    pathInTree = item.NextcloudPath
                End If
                If String.IsNullOrEmpty(pathInTree) Then Continue For
                If Await NextcloudService.AddToAlbumAsync(node.Id, pathInTree) Then assigned += 1
            Next
            StatusText = If(assigned = 0,
                            If(String.IsNullOrEmpty(NextcloudService.LastError), LocalizationService.T("Kein Element ausgewählt"), NextcloudService.LastError),
                            String.Format(LocalizationService.T("{0} von {1} zugewiesen"), assigned, items.Count))
        End Function

        ''' <summary>Hängt Serverbilder in ein Album ihrer EIGENEN Quelle - aus dem Ablegen auf einem
        ''' Albumknoten oder aus dem Einfügen. Die Bilder werden dabei nicht kopiert, es entsteht nur
        ''' eine Zuordnung.
        '''
        ''' Quellenfremdes bleibt draußen: ein Immich-Bild in ein Nextcloud-Album zu hängen hieße,
        ''' es erst herunter- und wieder hochzuladen. Das ist ein Umzug und keine Zuordnung, und er
        ''' gehört nicht hinter eine Ablegegeste.</summary>
        Public Async Function AddRemotePathsToAlbumAsync(node As VirtualNavigationNode, paths As List(Of String)) As Task
            If node Is Nothing OrElse paths Is Nothing OrElse paths.Count = 0 Then Return
            ' Anfang und Ende im Protokoll: laeuft die Zuordnung noch, waehrend jemand den naechsten
            ' Zug beginnt, steht es damit im Log statt in der Vermutung.
            DiagnosticLogService.LogAlways("Drag", $"zuordnen beginnt ziel={node.Kind} pfade={paths.Count}")
            Dim assigned = 0

            If node.IsImmichAlbumNode Then
                Dim ids As New List(Of String)()
                For Each path In paths
                    Dim assetId As String = Nothing, fileName As String = Nothing
                    If ImmichService.TryParsePseudoPath(path, assetId, fileName) Then ids.Add(assetId)
                Next
                If ids.Count = 0 Then Return
                If Await ImmichService.AddAssetsToAlbumAsync(node.Id, ids) Then assigned = ids.Count

            ElseIf node.IsNextcloudAlbumNode Then
                For Each path In paths
                    Dim fileId As String = Nothing, fileName As String = Nothing
                    If Not NextcloudService.TryParsePseudoPath(path, fileId, fileName) Then Continue For
                    ' Das Zuweisen braucht den Pfad im Dateibaum; der steht am Element erst nach den
                    ' Einzelheiten, und beim Ablegen haben wir nur den Pseudo-Pfad.
                    Dim info = Await NextcloudService.GetInfoAsync(fileId)
                    If info Is Nothing OrElse String.IsNullOrEmpty(info.FileName) Then Continue For
                    If Await NextcloudService.AddToAlbumAsync(node.Id, info.FileName) Then assigned += 1
                Next
            Else
                Return
            End If

            StatusText = If(assigned = 0,
                            If(String.IsNullOrEmpty(NextcloudService.LastError), LocalizationService.T("Kein Element ausgewählt"), NextcloudService.LastError),
                            String.Format(LocalizationService.T("{0} von {1} zugewiesen"), assigned, paths.Count))
            DiagnosticLogService.LogAlways("Drag", $"zuordnen fertig {assigned} von {paths.Count}")
        End Function

        ''' <summary>Lädt lokale Dateien in den Nextcloud-Dateibaum und ordnet sie - auf einem
        ''' Albumknoten - zusätzlich dem Album zu.
        '''
        ''' WOHIN, war hier die einzige offene Frage; bei Immich stellt sie sich nicht, weil es dort
        ''' keine Ordner gibt. Sie beantwortet die Einstellung „Zielordner für Uploads" (Vorgabe
        ''' /Photos). Ein vorhandener Name wird NICHT überschrieben, sondern nummeriert: DSC_0001.JPG
        ''' gibt es in jedem Bestand mehrfach, und ein Upload darf nichts wegnehmen.</summary>
        Public Async Sub UploadToNextcloud(node As VirtualNavigationNode, filePaths As IEnumerable(Of String))
            If Not NextcloudService.IsConfigured Then Return
            Dim albumId = If(node IsNot Nothing AndAlso node.IsNextcloudAlbumNode, node.Id, Nothing)
            Dim localPaths = If(filePaths, Enumerable.Empty(Of String)()).
                             Where(Function(p) Not String.IsNullOrWhiteSpace(p) AndAlso File.Exists(p)).ToList()
            If localPaths.Count = 0 Then Return

            IsLoading = True
            Dim uploadedPaths As New List(Of String)()
            Try
                Dim done = 0
                For Each localPath In localPaths
                    done += 1
                    StatusText = String.Format(LocalizationService.T("Lade nach Nextcloud hoch… ({0}/{1})"), done, localPaths.Count)
                    Dim target = Await NextcloudService.UploadNewFileAsync(localPath, NextcloudService.UploadFolder)
                    If Not String.IsNullOrEmpty(target) Then uploadedPaths.Add(target)
                Next
                If Not String.IsNullOrEmpty(albumId) Then
                    For Each target In uploadedPaths
                        Await NextcloudService.AddToAlbumAsync(albumId, target)
                    Next
                End If
                StatusText = If(uploadedPaths.Count = 0 AndAlso Not String.IsNullOrEmpty(NextcloudService.LastError),
                                NextcloudService.LastError,
                                String.Format(LocalizationService.T("{0} von {1} nach Nextcloud hochgeladen"), uploadedPaths.Count, localPaths.Count))
            Catch ex As Exception
                DiagnosticLogService.LogException("Nextcloud.UploadFlow", ex)
                StatusText = LocalizationService.T("Upload fehlgeschlagen")
            Finally
                IsLoading = False
            End Try

            If uploadedPaths.Count = 0 Then Return
            RefreshNextcloudAlbumsAsync()
            ' Die offene Ansicht neu laden, damit die neuen Bilder erscheinen. Der Server braucht
            ' einen Augenblick, bis eine frisch abgelegte Datei in der Zeitachse steht - anders als
            ' bei Immich gibt es dafuer keine Bereitschaftsmeldung, also wird schlicht neu geladen.
            If _isVirtualFolder AndAlso SelectedNextcloudNode IsNot Nothing Then
                Await OpenVirtualNavigationNode(SelectedNextcloudNode)
            End If
        End Sub

        ''' <summary>Öffnet die gesamte Zeitachse als virtuellen Ordner.</summary>
        Private Async Function OpenNextcloudAllAsync(node As VirtualNavigationNode) As Task
            SelectedNextcloudNode = node
            AppSettingsService.RememberLastGalleryFolder("nextcloud://all")
            Await LoadNextcloudVirtualFolderAsync(If(node?.Name, LocalizationService.T("Alle Fotos")), Nothing)
        End Function

        ''' <summary>Öffnet ein Nextcloud-Album als virtuellen Ordner.</summary>
        Private Async Function OpenNextcloudAlbumAsync(node As VirtualNavigationNode) As Task
            If node Is Nothing OrElse String.IsNullOrWhiteSpace(node.Id) Then Return
            SelectedNextcloudNode = node
            AppSettingsService.RememberLastGalleryFolder($"nextcloud://album/{node.Id}/{node.Name}")
            Await LoadNextcloudVirtualFolderAsync(node.Name, node.Id)
        End Function

        ''' <summary>Startziel „zuletzt: Nextcloud": öffnet Zeitachse oder Album wieder. Ist die
        ''' Quelle aus oder nicht erreichbar, bleibt still der schon geladene Ordner stehen.</summary>
        Public Async Function OpenNextcloudStartupTargetAsync(token As String) As Task
            Try
                If Not NextcloudService.IsConfigured OrElse String.IsNullOrWhiteSpace(token) Then Return
                Dim rest = token.Substring("nextcloud://".Length)
                Dim node As VirtualNavigationNode = Nothing
                If String.Equals(rest, "all", StringComparison.OrdinalIgnoreCase) Then
                    node = New VirtualNavigationNode(LocalizationService.T("Alle Fotos"), "NextcloudAll")
                ElseIf rest.StartsWith("album/", StringComparison.OrdinalIgnoreCase) Then
                    Dim parts = rest.Substring(6).Split("/"c, 2)
                    node = New VirtualNavigationNode(If(parts.Length > 1, parts(1), "Album"), "NextcloudAlbum") With {.Id = parts(0)}
                End If
                If node Is Nothing Then Return
                SidebarTab = "Nextcloud"
                Await OpenVirtualNavigationNode(node)
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryViewModel.OpenNextcloudStartupTarget", ex)
            End Try
        End Function

        ''' <summary>Lädt die Aufnahmen der Zeitachse oder eines Albums in den virtuellen Ordner.
        '''
        ''' DIE TAGESLISTE BRINGT DIE AUFNAHMEN NICHT MIT: gemessen trägt nur der erste Tag ein
        ''' `detail`, die übrigen nur ihre Anzahl. Deshalb wird je Tag nachgefragt, und zwar von der
        ''' jüngsten Aufnahme rückwärts - so steht das Neueste sofort da, statt dass der Nutzer auf
        ''' den ganzen Bestand wartet.</summary>
        Private Async Function LoadNextcloudVirtualFolderAsync(name As String, albumId As String,
                                                               Optional backend As String = "albums",
                                                               Optional clusterId As String = Nothing) As Task
            Dim filterId = If(String.IsNullOrEmpty(clusterId), albumId, clusterId)
            Dim thumbnailToken = StartEmptyVirtualFolder(name)
            SelectedSearchNode = Nothing
            SelectedImmichNode = Nothing
            IsLoading = True
            StatusText = LocalizationService.T("Lade Nextcloud-Fotos…")
            Dim total As Integer = 0
            Try
                Dim days = Await NextcloudService.GetDaysAsync(thumbnailToken, filterId, backend)
                If thumbnailToken.IsCancellationRequested Then Return
                If days.Count = 0 Then
                    ' Leer ist nicht gleich kaputt: liegt eine Begruendung vor, gehoert SIE dorthin,
                    ' sonst sieht ein Serverfehler aus wie ein leeres Album.
                    Dim serverError = NextcloudService.LastError
                    StatusText = If(String.IsNullOrEmpty(serverError), $"0 {LocalizationService.T("Bilder")}  •  {name}", serverError)
                    Return
                End If
                ' Die Gesamtzahl steht schon in der Tagesliste - damit kann der Fortschritt sagen,
                ' wie weit es ist, statt nur hochzuzaehlen.
                Dim expectedTotal = days.Sum(Function(t) t.Count)

                ' ERST SAMMELN, DANN EINMAL ZEIGEN - und das ist eine Nutzerbeobachtung, keine
                ' Vorliebe: geladen wird nach Tagen von neu nach alt, sortiert aber nach der
                ' EINSTELLUNG der Galerie. Steht die auf "Name aufsteigend", hat die erste Ladung
                ' mit dem, was am Ende oben steht, nichts zu tun. Wer klickte, sah erst fremde
                ' Bilder und kurz darauf die richtigen. Ein Zwischenstand, der gleich wieder
                ' umspringt, ist schlechter als ein Augenblick Geduld.
                '
                ' Der Preis: bei einem sehr grossen Bestand dauert es, bis die erste Kachel steht.
                ' Faellt das auf, ist die Antwort ein sortierungsbewusstes Nachladen (nur zeigen,
                ' solange die Ladereihenfolge der Sortierung entspricht), nicht das Zurueckdrehen
                ' auf den flackernden Zustand.
                Dim collected As New List(Of ImageItem)()
                ' NICHT "day" als Schleifenvariable: Day ist eine VB-Funktion.
                For Each dayEntry In days.OrderByDescending(Function(t) t.DayId)
                    If thumbnailToken.IsCancellationRequested Then Return
                    ' Die Aufnahmen des ersten Tages liegen schon bei; nur fuer die uebrigen fragen.
                    Dim photos = If(dayEntry.Detail IsNot Nothing AndAlso dayEntry.Detail.Count > 0,
                                    dayEntry.Detail,
                                    Await NextcloudService.GetDayAsync(dayEntry.DayId, thumbnailToken, filterId, backend))
                    If thumbnailToken.IsCancellationRequested Then Return
                    If photos Is Nothing OrElse photos.Count = 0 Then Continue For

                    collected.AddRange(photos.Select(Function(p) ImageItem.CreateNextcloudItem(p, thumbnailToken)))
                    total = collected.Count
                    StatusText = String.Format(LocalizationService.T("{0} von {1} geladen…"), total, expectedTotal)
                Next
                If thumbnailToken.IsCancellationRequested Then Return
                AddPrebuiltItemsToVirtualFolder(collected, sortNow:=False)

                ' Die Favoriten EINMAL fuer die ganze Ansicht holen, nicht je Kachel. Ohne diesen
                ' Schritt waere der Favorit nur schreibbar: auf dem Server markierte Fotos zeigten
                ' ein leeres Herz.
                Dim favoriteIds = Await NextcloudService.GetFavoriteFileIdsAsync(thumbnailToken)
                If thumbnailToken.IsCancellationRequested Then Return
                If favoriteIds.Count > 0 Then
                    For Each item In _allItems
                        If item Is Nothing OrElse Not item.IsNextcloudAsset Then Continue For
                        ' Direkt die Eigenschaft: der Setter meldet nur die Anzeige. Zurueck auf den
                        ' Server schreibt allein PersistFavorite, ausgeloest vom Herz-Klick.
                        If favoriteIds.Contains(item.NextcloudFileId) Then item.IsFavorite = True
                    Next
                End If

                FilterAndSort()
                StatusText = $"{total} {LocalizationService.T("Bilder")}  •  {name}"
            Catch ex As Exception
                StatusText = ex.Message
            Finally
                IsLoading = False
            End Try
        End Function

        ''' <summary>Nach jeder Aenderung an HasImmich aufrufen: verschwindet Immich, waehrend sein
        ''' Tab offen ist, faellt die Auswahl auf Ordner zurueck (sonst zeigt die Seitenleiste einen
        ''' Tab, den es nicht mehr gibt).</summary>
        Private Sub SyncSidebarTabWithImmich()
            Me.RaisePropertyChanged(NameOf(HasImmich))
            If Not HasImmich AndAlso String.Equals(_sidebarTab, "Immich", StringComparison.Ordinal) Then
                _sidebarTab = "Folders"
            End If
            RaiseSidebarTabProperties()
        End Sub

        ''' <summary>Baut den eigenen Immich-Bereich auf, sofern konfiguriert: „Alle Fotos" plus die
        ''' Alben (im Hintergrund nachgeladen). No-op, wenn Immich deaktiviert/unkonfiguriert ist.</summary>
        Private Sub InitializeImmich()
            ImmichTree.Clear()
            SyncSidebarTabWithImmich()
            If Not ImmichService.IsConfigured Then
                ' Auch die FILTERLISTEN leeren. Ohne das zeigten die Knöpfe weiter Server-Einträge,
                ' und ein Klick öffnete eine Server-Ansicht gegen einen Server, den es nicht mehr
                ' gibt.
                _immichPeople = New List(Of ImmichPerson)()
                _immichPlaces = New List(Of String)()
                RefreshPersonFilterOptions()
                RefreshPlaceFilterOptions()
                Return
            End If
            ImmichTree.Add(New VirtualNavigationNode(LocalizationService.T("Alle Fotos"), "ImmichAll"))
            SyncSidebarTabWithImmich()
            RefreshImmichAlbumsAsync()
        End Sub

        ''' <summary>Baut den Immich-Bereich nach einer Konfigurationsänderung (Einstellungen) neu auf.</summary>
        Public Sub ReinitializeImmich()
            InitializeImmich()
        End Sub

        ''' <summary>Legt ein neues Immich-Album an (nach Namenseingabe) und aktualisiert den Baum.</summary>
        Public Async Sub CreateImmichAlbum()
            Try
                If Not ImmichService.IsConfigured Then Return
                Dim name = Await _mainVm.ShowInputAsync(AppDialogKind.Input, LocalizationService.T("Neues Immich-Album"), LocalizationService.T("Name des Albums:"), "")
                If String.IsNullOrWhiteSpace(name) Then Return
                Dim id = Await ImmichService.CreateAlbumAsync(name)
                If String.IsNullOrEmpty(id) Then
                    StatusText = LocalizationService.T("Album konnte nicht angelegt werden")
                    Return
                End If
                RefreshImmichAlbumsAsync()
                StatusText = String.Format(LocalizationService.T("Album {0} angelegt"), name.Trim())
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryViewModel.CreateImmichAlbum", ex)
                StatusText = LocalizationService.T("Aktion fehlgeschlagen")
            End Try
        End Sub

        ''' <summary>Benennt ein Immich-Album um (nach Namenseingabe) und aktualisiert den Baum.</summary>
        Public Async Sub RenameImmichAlbum(node As VirtualNavigationNode)
            Try
                If node Is Nothing OrElse Not String.Equals(node.Kind, "ImmichAlbum", StringComparison.Ordinal) OrElse String.IsNullOrWhiteSpace(node.Id) Then Return
                Dim name = Await _mainVm.ShowInputAsync(AppDialogKind.Rename, LocalizationService.T("Album umbenennen"), LocalizationService.T("Neuer Name:"), node.Name)
                If String.IsNullOrWhiteSpace(name) OrElse String.Equals(name.Trim(), node.Name, StringComparison.Ordinal) Then Return
                Dim ok = Await ImmichService.RenameAlbumAsync(node.Id, name)
                If Not ok Then
                    StatusText = LocalizationService.T("Umbenennen fehlgeschlagen")
                    Return
                End If
                RefreshImmichAlbumsAsync()
                StatusText = String.Format(LocalizationService.T("Album umbenannt: {0}"), name.Trim())
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryViewModel.RenameImmichAlbum", ex)
                StatusText = LocalizationService.T("Aktion fehlgeschlagen")
            End Try
        End Sub

        ''' <summary>Löscht ein Immich-Album - nur die Zusammenstellung, die Fotos bleiben in Immich. Hängt am
        ''' selben Schalter wie das Löschen von Fotos („Löschen in Immich erlauben"), weil auch das auf dem
        ''' Server wirkt. Steht das gelöschte Album gerade offen, fällt die Ansicht auf „Alle Fotos" zurück.</summary>
        Public Async Sub DeleteImmichAlbum(node As VirtualNavigationNode)
            Try
                If node Is Nothing OrElse Not node.IsImmichAlbumNode OrElse String.IsNullOrWhiteSpace(node.Id) Then Return
                Dim settings = AppSettingsService.Load()
                If Not settings.ImmichAllowDelete Then
                    StatusText = LocalizationService.T("Löschen in Immich ist in den Einstellungen nicht erlaubt")
                    Return
                End If

                If Not settings.DeleteSkipConfirmation Then
                    Dim message = String.Format(LocalizationService.T("Album {0} löschen? Die Fotos darin bleiben in Immich erhalten."), node.Name)
                    If Not Await _mainVm.ShowConfirmAsync(LocalizationService.T("Album löschen"), message,
                                                          LocalizationService.T("Löschen"), LocalizationService.T("Abbrechen")) Then Return
                End If

                If Not Await ImmichService.DeleteAlbumAsync(node.Id) Then
                    StatusText = LocalizationService.T("Album konnte nicht gelöscht werden")
                    Return
                End If

                Dim wasOpen = SelectedImmichNode IsNot Nothing AndAlso String.Equals(SelectedImmichNode.Id, node.Id, StringComparison.Ordinal)
                RefreshImmichAlbumsAsync()
                If wasOpen Then
                    Dim allNode = ImmichTree.FirstOrDefault(Function(n) String.Equals(n.Kind, "ImmichAll", StringComparison.Ordinal))
                    If allNode IsNot Nothing Then Await OpenVirtualNavigationNode(allNode)
                End If
                StatusText = String.Format(LocalizationService.T("Album gelöscht: {0}"), node.Name)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryViewModel.DeleteImmichAlbum", ex)
                StatusText = LocalizationService.T("Aktion fehlgeschlagen")
            End Try
        End Sub

        ''' <summary>Lädt lokale Dateien nach Immich hoch und ordnet sie - falls ein Album-Knoten übergeben
        ''' wurde - diesem zu. Aktualisiert danach Baum und (falls betroffen) die offene Ansicht.</summary>
        Public Async Sub UploadToImmich(node As VirtualNavigationNode, filePaths As IEnumerable(Of String))
            If Not ImmichService.IsConfigured Then Return
            Dim albumId = If(node IsNot Nothing AndAlso String.Equals(node.Kind, "ImmichAlbum", StringComparison.Ordinal), node.Id, Nothing)
            Dim paths = If(filePaths, Enumerable.Empty(Of String)()).Where(Function(p) Not String.IsNullOrWhiteSpace(p) AndAlso File.Exists(p)).ToList()
            If paths.Count = 0 Then Return

            IsLoading = True
            Dim uploaded As New List(Of String)()
            Try
                Dim done = 0
                For Each p In paths
                    done += 1
                    StatusText = String.Format(LocalizationService.T("Lade nach Immich hoch… ({0}/{1})"), done, paths.Count)
                    Dim id = Await ImmichService.UploadAssetAsync(p)
                    If Not String.IsNullOrEmpty(id) Then uploaded.Add(id)
                Next
                If Not String.IsNullOrEmpty(albumId) AndAlso uploaded.Count > 0 Then
                    Await ImmichService.AddAssetsToAlbumAsync(albumId, uploaded)
                End If
                StatusText = String.Format(LocalizationService.T("{0} von {1} nach Immich hochgeladen"), uploaded.Count, paths.Count)
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.UploadFlow", ex)
                StatusText = LocalizationService.T("Upload fehlgeschlagen")
            Finally
                IsLoading = False
            End Try

            RefreshImmichAlbumsAsync()
            ' Offene Immich-Ansicht (dasselbe Album oder „Alle Fotos") neu laden, damit die neuen Bilder erscheinen.
            If uploaded.Count > 0 AndAlso _isVirtualFolder AndAlso SelectedImmichNode IsNot Nothing Then
                Dim reopen = SelectedImmichNode
                If String.Equals(reopen.Kind, "ImmichAll", StringComparison.Ordinal) OrElse
                   (String.Equals(reopen.Kind, "ImmichAlbum", StringComparison.Ordinal) AndAlso String.Equals(reopen.Id, albumId, StringComparison.Ordinal)) Then
                    ' Immich erzeugt die Thumbnails asynchron nach dem Upload. Vor dem Neuladen darauf warten,
                    ' sonst zeigt die Ansicht für die neuen Assets leere Kacheln (siehe SaveImageAsync-Upload).
                    StatusText = LocalizationService.T("Warte auf Immich-Thumbnails…")
                    Await Task.WhenAll(uploaded.Select(Function(id) ImmichService.WaitForThumbnailReadyAsync(id)))
                    Await OpenVirtualNavigationNode(reopen)
                End If
            End If
        End Sub

        ''' <summary>Lädt die Albenliste im Hintergrund und hängt sie (unter „Alle Fotos") in den
        ''' Immich-Bereich. Ein nicht erreichbarer Server hinterlässt einfach nur „Alle Fotos".</summary>
        Private Sub RefreshImmichAlbumsAsync()
            Dim ignored = Task.Run(Async Function()
                                       Dim albums = Await ImmichService.GetAlbumsAsync()
                                       ' Personen (serverseitige Gesichtserkennung) und Orte (Städte) kommen als
                                       ' EINKLAPPBARE Elternknoten dazu - eingeklappt, damit der (gedeckelte)
                                       ' Immich-Bereich der Sidebar übersichtlich bleibt.
                                       Dim people = Await ImmichService.GetPeopleAsync()
                                       Dim places = Await ImmichService.GetPlacesAsync()
                                       Await Dispatcher.UIThread.InvokeAsync(
                                           Sub()
                                               ' Nur die eigenen Knoten ersetzen, „Alle Fotos" bleibt stehen.
                                               For i = ImmichTree.Count - 1 To 0 Step -1
                                                   Select Case ImmichTree(i).Kind
                                                       Case "ImmichAlbum", "ImmichPeopleRoot", "ImmichPlacesRoot", "ImmichTrash"
                                                           ImmichTree.RemoveAt(i)
                                                   End Select
                                               Next
                                               For Each album In albums
                                                   ImmichTree.Add(New VirtualNavigationNode(album.Name, "ImmichAlbum") With {
                                                       .Id = album.Id,
                                                       .IsRemovable = False
                                                   })
                                               Next
                                               If people.Count > 0 Then
                                                   Dim peopleRoot = New VirtualNavigationNode(LocalizationService.T("Personen"), "ImmichPeopleRoot") With {
                                                       .Children = New ObservableCollection(Of VirtualNavigationNode)(),
                                                       .IsExpanded = False
                                                   }
                                                   For Each person In people
                                                       peopleRoot.Children.Add(New VirtualNavigationNode(person.Name, "ImmichPerson") With {
                                                           .Id = person.Id,
                                                           .IsRemovable = False
                                                       })
                                                   Next
                                                   ImmichTree.Add(peopleRoot)
                                               End If
                                               If places.Count > 0 Then
                                                   Dim placesRoot = New VirtualNavigationNode(LocalizationService.T("Orte"), "ImmichPlacesRoot") With {
                                                       .Children = New ObservableCollection(Of VirtualNavigationNode)(),
                                                       .IsExpanded = False
                                                   }
                                                   For Each place In places
                                                       placesRoot.Children.Add(New VirtualNavigationNode(place, "ImmichPlace") With {
                                                           .Id = place,
                                                           .IsRemovable = False
                                                       })
                                                   Next
                                                   ImmichTree.Add(placesRoot)
                                               End If
                                               ' Der Papierkorb steht ganz unten, wie in jedem
                                               ' Dateiverwalter - und auf beiden Serverquellen gleich.
                                               ImmichTree.Add(New VirtualNavigationNode(LocalizationService.T("Papierkorb"), "ImmichTrash"))
                                               ' Dieselben Listen speisen die FILTERKNOEPFE. Sie
                                               ' werden synchron beim Oeffnen des Menues gelesen,
                                               ' koennen also nicht selbst auf den Server warten -
                                               ' der Abruf hier ist ohnehin faellig, und jeder
                                               ' zweite waere nur Wartezeit.
                                               _immichPeople = people
                                               _immichPlaces = places
                                               RefreshPersonFilterOptions()
                                               RefreshPlaceFilterOptions()
                                               SyncSidebarTabWithImmich()
                                           End Sub)
                                   End Function)
        End Sub

        ''' <summary>Personen und Staedte des Immich-Servers, wie sie beim Aufbau der Seitenleiste
        ''' geholt wurden. Die Filterlisten lesen sie von hier; ohne Server bleiben sie leer, und die
        ''' Listen sehen aus wie bisher.</summary>
        Private _immichPeople As New List(Of ImmichPerson)()
        Private _immichPlaces As New List(Of String)()

        ''' <summary>Dasselbe fuer die zweite Serverquelle, beim Aufbau des Nextcloud-Baums geholt.
        ''' Sie kommen als CLUSTER (dieselbe Sorte Eintrag wie ein Album), Stichwoerter eingeschlossen -
        ''' anders als bei Immich, wo die Suche keinen Stichwortfilter kennt.</summary>
        Private _nextcloudPeople As New List(Of NextcloudService.NextcloudCluster)()
        Private _nextcloudPlaces As New List(Of NextcloudService.NextcloudCluster)()
        Private _nextcloudTags As New List(Of NextcloudService.NextcloudCluster)()

        ''' <summary>Name der Quelle in den Zwischenueberschriften der Filterlisten. Servernamen
        ''' werden NICHT uebersetzt - sie sind Eigennamen.</summary>
        Private Const ImmichSourceName As String = "Immich"
        Private Const NextcloudSourceName As String = "Nextcloud"

        ''' <summary>Öffnet „Alle Fotos" (Timeline ohne Album) als virtuellen Ordner.</summary>
        Private Async Function OpenImmichAllAsync(node As VirtualNavigationNode) As Task
            SelectedImmichNode = node
            AppSettingsService.RememberLastGalleryFolder("immich://all")
            Await LoadImmichVirtualFolderAsync(If(node?.Name, LocalizationService.T("Alle Fotos")), Nothing)
        End Function

        ''' <summary>Öffnet die Bilder einer Immich-Person (serverseitige Gesichtserkennung).</summary>
        Private Async Function OpenImmichPersonAsync(node As VirtualNavigationNode) As Task
            If node Is Nothing OrElse String.IsNullOrWhiteSpace(node.Id) Then Return
            SelectedImmichNode = node
            AppSettingsService.RememberLastGalleryFolder($"immich://person/{node.Id}/{node.Name}")
            Await LoadImmichVirtualFolderAsync(node.Name, Nothing, personId:=node.Id)
        End Function

        ''' <summary>Öffnet die Bilder eines Immich-Orts (Stadt aus den EXIF-Daten).</summary>
        Private Async Function OpenImmichPlaceAsync(node As VirtualNavigationNode) As Task
            If node Is Nothing OrElse String.IsNullOrWhiteSpace(node.Id) Then Return
            SelectedImmichNode = node
            AppSettingsService.RememberLastGalleryFolder($"immich://place/{node.Id}")
            Await LoadImmichVirtualFolderAsync(node.Name, Nothing, city:=node.Id)
        End Function

        ''' <summary>Öffnet ein Immich-Album als virtuellen Ordner.</summary>
        Private Async Function OpenImmichAlbumAsync(node As VirtualNavigationNode) As Task
            If node Is Nothing OrElse String.IsNullOrWhiteSpace(node.Id) Then Return
            SelectedImmichNode = node
            AppSettingsService.RememberLastGalleryFolder($"immich://album/{node.Id}/{node.Name}")
            Await LoadImmichVirtualFolderAsync(node.Name, node.Id)
        End Function

        ''' <summary>Startziel „zuletzt: Immich" (immich://…-Token aus LastGalleryFolder): öffnet
        ''' Alle Fotos, Album, Person oder Ort wieder. Bei ausgeschaltetem/nicht erreichbarem
        ''' Immich bleibt still der bereits geladene Ordner stehen (Fallback Bilder-Ordner).</summary>
        Public Async Function OpenImmichStartupTargetAsync(token As String) As Task
            Try
                If Not ImmichService.IsConfigured OrElse String.IsNullOrWhiteSpace(token) Then Return
                Dim remainder = token.Substring("immich://".Length)
                Dim node As VirtualNavigationNode = Nothing
                If String.Equals(remainder, "all", StringComparison.OrdinalIgnoreCase) Then
                    node = New VirtualNavigationNode(LocalizationService.T("Alle Fotos"), "ImmichAll")
                ElseIf remainder.StartsWith("album/", StringComparison.OrdinalIgnoreCase) Then
                    Dim parts = remainder.Substring(6).Split("/"c, 2)
                    node = New VirtualNavigationNode(If(parts.Length > 1, parts(1), "Album"), "ImmichAlbum") With {.Id = parts(0)}
                ElseIf remainder.StartsWith("person/", StringComparison.OrdinalIgnoreCase) Then
                    Dim parts = remainder.Substring(7).Split("/"c, 2)
                    node = New VirtualNavigationNode(If(parts.Length > 1, parts(1), "Person"), "ImmichPerson") With {.Id = parts(0)}
                ElseIf remainder.StartsWith("place/", StringComparison.OrdinalIgnoreCase) Then
                    Dim placeName = remainder.Substring(6)
                    node = New VirtualNavigationNode(placeName, "ImmichPlace") With {.Id = placeName}
                End If
                If node Is Nothing OrElse String.IsNullOrWhiteSpace(node.Id) AndAlso Not String.Equals(remainder, "all", StringComparison.OrdinalIgnoreCase) Then Return
                Await OpenVirtualNavigationNode(node)
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.ImmichStartup", ex)
            End Try
        End Function

        ''' <summary>Gemeinsamer Lade-Pfad für „Alle Fotos" (albumId = Nothing) und Alben (albumId gesetzt):
        ''' streamt die Assets seitenweise über die Metadaten-Suche und zeigt bereits geladene sofort an.
        ''' v3 liefert Album-Assets NICHT über /api/albums/{id}, daher der einheitliche Suche-mit-albumIds-Weg.</summary>
        Private Async Function LoadImmichVirtualFolderAsync(name As String, albumId As String,
                                                            Optional personId As String = Nothing,
                                                            Optional city As String = Nothing) As Task
            Dim thumbnailToken = StartEmptyVirtualFolder(name)
            SelectedSearchNode = Nothing
            IsLoading = True
            StatusText = LocalizationService.T("Lade Immich-Fotos…")
            ' Sicherheitsnetz gegen eine versehentlich riesige Bibliothek.
            Const SafetyCap As Integer = 100000
            Dim total As Integer = 0

            ' LOKALER KATALOG (30k-Bibliothek): "Alle Fotos" zeigte bei
            ' jedem Öffnen erst nach dem kompletten Server-Streaming etwas an. Jetzt kommt SOFORT
            ' der zuletzt gespeicherte Katalog aus der Index-DB; der Server-Abgleich läuft danach
            ' im Hintergrund weiter (neue Assets kommen dazu - Dedup über die Pseudo-Pfade -,
            ' verschwundene werden am Ende ausgetragen, Favoriten-Änderungen nachgezogen).
            ' Nur für die ungefilterte Timeline - Alben/Personen/Orte sind klein genug.
            Dim useCatalog = String.IsNullOrEmpty(albumId) AndAlso String.IsNullOrEmpty(personId) AndAlso String.IsNullOrEmpty(city)
            Dim serverKey = ImmichService.ServerKey
            Dim catalogShown = False
            Dim itemsByAssetId As Dictionary(Of String, ImageItem) = Nothing
            If useCatalog Then
                Dim cached = Await Task.Run(Function() ImmichIndexService.Instance.GetAssetList(serverKey))
                If thumbnailToken.IsCancellationRequested Then Return
                If cached.Count > 0 Then
                    Dim cachedItems = cached.Select(Function(a) ImageItem.CreateImmichItem(a, thumbnailToken)).ToList()
                    AddPrebuiltItemsToVirtualFolder(cachedItems, sortNow:=False)
                    FilterAndSort()
                    catalogShown = True
                    itemsByAssetId = cachedItems.
                        Where(Function(i) Not String.IsNullOrEmpty(i.ImmichAssetId)).
                        ToDictionary(Function(i) i.ImmichAssetId, StringComparer.Ordinal)
                    StatusText = LocalizationService.T("Wird mit Immich abgeglichen…")
                End If
            End If
            Dim serverAssets As List(Of ImmichAsset) = If(useCatalog, New List(Of ImmichAsset)(), Nothing)

            Try
                Dim page As Integer = 1
                ' FilterAndSort ist O(n log n) über ALLE bisher geladenen Items und läuft auf dem UI-Thread.
                ' Bei zehntausenden Fotos (viele Seiten) darf es NICHT pro Seite laufen (sonst O(n²) und
                ' träge Bedienung). Erste Charge sofort anzeigen, danach höchstens alle ~600ms neu sortieren,
                ' plus ein abschließender Durchlauf. Detaildaten (Dateigröße/Rating/…) lädt jedes Item
                ' viewport-priorisiert selbst nach, gekoppelt an seinen Thumbnail-Ladeweg.
                Dim lastSortTick = Environment.TickCount64
                Do
                    Dim result = Await ImmichService.GetAssetsPageAsync(page, albumId, thumbnailToken, personId, city)
                    If thumbnailToken.IsCancellationRequested Then Return
                    If result.Items.Count > 0 Then
                        Dim isFirstBatch = (total = 0)
                        If serverAssets IsNot Nothing Then serverAssets.AddRange(result.Items)
                        ' Bereits aus dem Katalog angezeigte Assets vollständig nachziehen. Ältere
                        ' Katalogversionen enthielten weder Dateigröße noch EXIF-/Änderungszeiten;
                        ' nur den Favoriten zu aktualisieren ließ diese Einträge trotz vollständiger
                        ' Serverantwort dauerhaft bei 0 B bzw. „Ohne Datum".
                        If itemsByAssetId IsNot Nothing Then
                            For Each asset In result.Items
                                Dim known As ImageItem = Nothing
                                If itemsByAssetId.TryGetValue(asset.Id, known) Then known.ApplyImmichMetadata(asset, replaceMissingDates:=True)
                            Next
                        End If
                        Dim items = result.Items.Select(Function(a) ImageItem.CreateImmichItem(a, thumbnailToken)).ToList()
                        AddPrebuiltItemsToVirtualFolder(items, sortNow:=False)
                        total += items.Count
                        ' Zwischensortierungen bewusst selten (das Neuaufbauen der Liste läuft auf dem
                        ' UI-Thread und konkurriert sonst mit den Viewport-Thumbnail-Benachrichtigungen).
                        ' Mit angezeigtem Katalog gar nicht: die Liste steht ja schon - erst der
                        ' Abschluss sortiert Neuzugänge ein.
                        If Not catalogShown AndAlso (isFirstBatch OrElse Environment.TickCount64 - lastSortTick > 1500) Then
                            FilterAndSort()
                            lastSortTick = Environment.TickCount64
                        End If
                    End If
                    If result.NextPage <= 0 OrElse total >= SafetyCap Then Exit Do
                    page = result.NextPage
                Loop
                FilterAndSort()
                If total = 0 AndAlso Not String.IsNullOrEmpty(ImmichService.LastError) Then
                    StatusText = LocalizationService.T("Immich-Fehler: ") & ImmichService.LastError
                End If

                ' Abgleich abschliessen - NUR wenn das Streaming vollstaendig und fehlerfrei war
                ' (ein Teilstand wuerde Bilder verstecken bzw. beim naechsten Start fehlen lassen).
                If useCatalog AndAlso String.IsNullOrEmpty(ImmichService.LastError) AndAlso
                   Not thumbnailToken.IsCancellationRequested AndAlso total < SafetyCap Then
                    If itemsByAssetId IsNot Nothing Then
                        Dim serverIds = New HashSet(Of String)(serverAssets.Select(Function(a) a.Id), StringComparer.Ordinal)
                        Dim removed = itemsByAssetId.Keys.Where(Function(id) Not serverIds.Contains(id)).ToList()
                        If removed.Count > 0 Then RemoveImmichItems(removed)
                    End If
                    Dim toStore = serverAssets
                    Await Task.Run(Sub() ImmichIndexService.Instance.ReplaceAssetList(serverKey, toStore))
                    StatusText = LocalizationService.T("Vorschau bereit")
                End If
            Catch ex As OperationCanceledException
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.LoadVirtualFolder", ex)
                StatusText = LocalizationService.T("Immich-Fotos konnten nicht geladen werden")
            Finally
                If Not thumbnailToken.IsCancellationRequested Then IsLoading = False
            End Try
        End Function

        ' ── Papierkorb beider Serverquellen ─────────────────────────────────────
        '
        ' Geloescht wird auf beiden Servern in den Papierkorb (ohne "endgueltig loeschen" bei Immich,
        ' bei Nextcloud grundsaetzlich). Ohne diese Ansicht waere der Rueckweg allein die
        ' Weboberflaeche des Servers - und wer versehentlich Entf gedrueckt hat, sucht ihn hier.
        '
        ' Die Ansicht ist eine EINBAHNSTRASSE: es gibt genau eine Geste, das Wiederherstellen.
        ' Endgueltiges Loeschen gehoert nicht hinter dieselbe Taste wie das Loeschen in der Galerie.

        Private _isTrashView As Boolean

        ''' <summary>Steht gerade ein Papierkorb offen? Daran haengt der Menueeintrag zum
        ''' Wiederherstellen - er soll nirgends sonst auftauchen.</summary>
        Public ReadOnly Property IsTrashView As Boolean
            Get
                Return _isTrashView
            End Get
        End Property

        Private Sub SetTrashView(value As Boolean)
            If _isTrashView = value Then Return
            _isTrashView = value
            Me.RaisePropertyChanged(NameOf(IsTrashView))
        End Sub

        ''' <summary>Oeffnet den Immich-Papierkorb. Die Assets kommen ueber dieselbe Metadaten-Suche
        ''' wie alles andere, nur mit dem Zeitfilter - die Kacheln, Vorschauen und Einzelheiten
        ''' funktionieren deshalb unveraendert.</summary>
        Private Async Function OpenImmichTrashAsync(node As VirtualNavigationNode) As Task
            SelectedImmichNode = node
            SelectedNextcloudNode = Nothing
            Dim name = If(node?.Name, LocalizationService.T("Papierkorb"))
            Dim thumbnailToken = StartEmptyVirtualFolder(name)
            SetTrashView(True)
            SelectedSearchNode = Nothing
            IsLoading = True
            StatusText = LocalizationService.T("Lade Papierkorb…")
            Dim total = 0
            Try
                Dim expectedTotal = Await ImmichService.GetTrashedCountAsync(thumbnailToken)
                Dim page = 1
                Do
                    Dim result = Await ImmichService.GetTrashedAssetsPageAsync(page, thumbnailToken)
                    If thumbnailToken.IsCancellationRequested Then Return
                    If result.Items.Count > 0 Then
                        Dim items = result.Items.Select(Function(a)
                                                            Dim item = ImageItem.CreateImmichItem(a, thumbnailToken)
                                                            item.IsTrashed = True
                                                            Return item
                                                        End Function).ToList()
                        AddPrebuiltItemsToVirtualFolder(items, sortNow:=False)
                        total += items.Count
                        StatusText = If(expectedTotal > 0,
                                        String.Format(LocalizationService.T("{0} von {1} geladen…"), total, expectedTotal),
                                        String.Format(LocalizationService.T("{0} geladen…"), total))
                    End If
                    If result.NextPage <= 0 Then Exit Do
                    page = result.NextPage
                Loop
                FilterAndSort()
                StatusText = TrashStatusText(total)
            Catch ex As OperationCanceledException
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.ImmichTrash", ex)
                StatusText = LocalizationService.T("Der Papierkorb konnte nicht geladen werden")
            Finally
                If Not thumbnailToken.IsCancellationRequested Then IsLoading = False
            End Try
        End Function

        ''' <summary>Oeffnet den Nextcloud-Papierkorb. Er ist ein EIGENER WebDAV-Baum: die Aufnahmen
        ''' stehen nicht mehr in der Zeitachse, und Memories kennt sie nicht - Vorschau und Original
        ''' laufen deshalb ueber eigene Wege (siehe NextcloudService).</summary>
        Private Async Function OpenNextcloudTrashAsync(node As VirtualNavigationNode) As Task
            SelectedNextcloudNode = node
            SelectedImmichNode = Nothing
            Dim name = If(node?.Name, LocalizationService.T("Papierkorb"))
            Dim thumbnailToken = StartEmptyVirtualFolder(name)
            SetTrashView(True)
            SelectedSearchNode = Nothing
            IsLoading = True
            StatusText = LocalizationService.T("Lade Papierkorb…")
            Try
                Dim entries = Await NextcloudService.GetTrashAsync(thumbnailToken)
                If thumbnailToken.IsCancellationRequested Then Return
                ' Im Papierkorb liegt alles, was der Nutzer je geloescht hat - Textdateien wie Fotos.
                ' Gefiltert wird nach der Endungsliste der Galerie, damit hier keine Kachel steht,
                ' die nie ein Bild zeigen kann.
                Dim images = entries.Where(Function(e) e IsNot Nothing AndAlso
                                               _imageExtensions.Contains(IO.Path.GetExtension(If(e.DisplayName, "")).ToLowerInvariant())).ToList()
                If images.Count = 0 Then
                    Dim serverError = NextcloudService.LastError
                    StatusText = If(String.IsNullOrEmpty(serverError), TrashStatusText(0), serverError)
                    Return
                End If
                AddPrebuiltItemsToVirtualFolder(images.Select(Function(e) ImageItem.CreateNextcloudTrashItem(e, thumbnailToken)).ToList(),
                                                sortNow:=False)
                FilterAndSort()
                StatusText = TrashStatusText(images.Count)
            Catch ex As OperationCanceledException
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.NextcloudTrash", ex)
                StatusText = LocalizationService.T("Der Papierkorb konnte nicht geladen werden")
            Finally
                If Not thumbnailToken.IsCancellationRequested Then IsLoading = False
            End Try
        End Function

        ''' <summary>Leert den Papierkorb der Quelle, zu der der Knoten gehoert.
        '''
        ''' MIT RUECKFRAGE, und zwar unabhaengig von "Nachfrage beim Loeschen ueberspringen": das
        ''' hier trifft nicht die Auswahl, sondern ALLES, was der Nutzer je geloescht hat - auch das
        ''' von vor Wochen, an das er gerade nicht denkt. Danach gibt es keinen Rueckweg mehr.</summary>
        Public Async Function EmptyTrashAsync(node As VirtualNavigationNode) As Task
            If node Is Nothing OrElse Not node.IsTrashNode Then Return
            Dim istNextcloud = String.Equals(node.Kind, "NextcloudTrash", StringComparison.Ordinal)
            Try
                Dim ok = Await _mainVm.ShowConfirmAsync(LocalizationService.T("Papierkorb leeren"),
                            LocalizationService.T("Alles im Papierkorb des Servers endgültig löschen? Das lässt sich nicht rückgängig machen."),
                            LocalizationService.T("Papierkorb leeren"),
                            LocalizationService.T("Abbrechen"))
                If Not ok Then Return

                IsLoading = True
                Dim geleert = If(istNextcloud,
                                 Await NextcloudService.EmptyTrashAsync(),
                                 Await ImmichService.EmptyTrashAsync())
                If Not geleert Then
                    Dim serverError = If(istNextcloud, NextcloudService.LastError, ImmichService.LastError)
                    StatusText = If(String.IsNullOrEmpty(serverError), LocalizationService.T("Löschen fehlgeschlagen"), serverError)
                    Return
                End If
                StatusText = LocalizationService.T("Der Papierkorb ist leer")
                ' Steht der Papierkorb gerade offen, zeigt er jetzt etwas, das es nicht mehr gibt.
                Dim openNode = If(SelectedImmichNode, SelectedNextcloudNode)
                If _isTrashView AndAlso openNode IsNot Nothing Then Await OpenVirtualNavigationNode(openNode)
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.EmptyTrash", ex)
                StatusText = LocalizationService.T("Löschen fehlgeschlagen")
            Finally
                IsLoading = False
            End Try
        End Function

        Private Shared Function TrashStatusText(count As Integer) As String
            If count = 0 Then Return LocalizationService.T("Der Papierkorb ist leer")
            Return String.Format(LocalizationService.T("{0} im Papierkorb"), count)
        End Function

        ''' <summary>Holt die markierten Aufnahmen aus dem Papierkorb zurueck. Der Server legt sie
        ''' dorthin, wo sie herkamen; ein Zielordner wird nicht gefragt und waere auch nicht
        ''' vorgesehen. Danach ist die Ansicht nicht mehr aktuell - sie wird neu geladen, denn eine
        ''' Kachel, die es hier nicht mehr gibt, waere ein Klick ins Leere.</summary>
        Public Async Function RestoreSelectedFromTrashAsync() As Task
            Dim items = GetSelectedImageItems().Where(Function(i) i IsNot Nothing AndAlso i.IsTrashed).ToList()
            If items.Count = 0 Then
                StatusText = LocalizationService.T("Kein Element ausgewählt")
                Return
            End If
            Dim restored = 0
            Try
                Dim immichIds = items.Where(Function(i) i.IsImmichAsset).Select(Function(i) i.ImmichAssetId).ToList()
                If immichIds.Count > 0 AndAlso Await ImmichService.RestoreAssetsAsync(immichIds) Then restored += immichIds.Count
                For Each item In items.Where(Function(i) i.IsNextcloudAsset AndAlso Not String.IsNullOrEmpty(i.NextcloudTrashUrl))
                    If Await NextcloudService.RestoreFromTrashAsync(item.NextcloudTrashUrl) Then restored += 1
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.RestoreFromTrash", ex)
            End Try

            If restored = 0 Then
                StatusText = LocalizationService.T("Wiederherstellen fehlgeschlagen")
                Return
            End If
            StatusText = String.Format(LocalizationService.T("{0} von {1} wiederhergestellt"), restored, items.Count)
            Dim openNode = If(SelectedImmichNode, SelectedNextcloudNode)
            If openNode IsNot Nothing Then Await OpenVirtualNavigationNode(openNode)
        End Function

        Public Sub RemoveVirtualSearchNode(node As VirtualNavigationNode)
            If node Is Nothing OrElse Not node.IsRemovable OrElse String.IsNullOrWhiteSpace(node.Id) Then Return
            Dim existing = _savedSearches.FirstOrDefault(Function(s) String.Equals(s.Id, node.Id, StringComparison.OrdinalIgnoreCase))
            If existing IsNot Nothing Then _savedSearches.Remove(existing)
            ' Aus dem Baum der eigenen Quelle - vorher wurde nur der Ordner-Tab durchsucht, eine
            ' Immich-Suche blieb nach dem Entfernen sichtbar stehen.
            Dim tree = SearchTreeForSource(If(existing?.Source, node.Source))
            Dim treeNode = tree.FirstOrDefault(Function(n) String.Equals(n.Id, node.Id, StringComparison.OrdinalIgnoreCase))
            If treeNode IsNot Nothing Then tree.Remove(treeNode)
            SaveSearches()
            ThumbnailCacheService.DeleteSearchListCache(node.Id)
            If _isVirtualFolder AndAlso String.Equals(_virtualFolderName, node.Name, StringComparison.OrdinalIgnoreCase) Then
                Items.Clear()
                DisplayItems.Clear()
                _allItems.Clear()
                StatusText = LocalizationService.T("Suche entfernt")
            End If
        End Sub

        Public Sub SetInitialFolderNodeForPath(folderPath As String)
            If String.IsNullOrEmpty(folderPath) OrElse Not Directory.Exists(folderPath) Then Return
            Dim node = FindFolderNode(FolderTree, folderPath)
            If node Is Nothing Then Return
            _initialFolderNode = node
            SelectedFolderNode = node
            Me.RaisePropertyChanged(NameOf(InitialFolderNode))
        End Sub

        Private Function FindFolderNode(nodes As IEnumerable(Of FolderNode), folderPath As String) As FolderNode
            If nodes Is Nothing OrElse String.IsNullOrEmpty(folderPath) Then Return Nothing

            For Each node In nodes
                If node Is Nothing OrElse String.IsNullOrEmpty(node.FullPath) Then Continue For
                If String.Equals(NormalizePath(node.FullPath), NormalizePath(folderPath), StringComparison.OrdinalIgnoreCase) Then
                    Return node
                End If

                If IsAncestorOrSelf(node.FullPath, folderPath) Then
                    node.EnsureChildrenLoaded()
                    Dim child = FindFolderNode(node.Children, folderPath)
                    If child IsNot Nothing Then
                        node.IsExpanded = True
                        Return child
                    End If
                End If
            Next

            Return Nothing
        End Function

        ' Wie FindFolderNode, aber ohne Seiteneffekte (kein EnsureChildrenLoaded/IsExpanded) -
        ' zum Nachschlagen eines bereits geladenen Knotens, z.B. beim FileSystemWatcher-Callback.
        Private Function FindLoadedFolderNode(nodes As IEnumerable(Of FolderNode), folderPath As String) As FolderNode
            If nodes Is Nothing OrElse String.IsNullOrEmpty(folderPath) Then Return Nothing

            For Each node In nodes
                If node Is Nothing OrElse String.IsNullOrEmpty(node.FullPath) Then Continue For
                If String.Equals(NormalizePath(node.FullPath), NormalizePath(folderPath), StringComparison.OrdinalIgnoreCase) Then
                    Return node
                End If

                If IsAncestorOrSelf(node.FullPath, folderPath) Then
                    Dim child = FindLoadedFolderNode(node.Children, folderPath)
                    If child IsNot Nothing Then Return child
                End If
            Next

            Return Nothing
        End Function

        Public Sub NavigateToFolder(folderPath As String)
            Dim ignored = NavigateToFolderAsync(folderPath)
        End Sub

        Public Function NavigateToFolderAsync(folderPath As String) As Task
            CancelActiveSearch()
            ' Ein Wechsel im Baum beendet JEDE Knopfauswahl - die Knoepfe verlieren die
            ' Akzentfarbe, sonst behaupteten sie einen Filter, der gar nicht mehr gilt.
            ClearButtonFiltersSilently()
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) AndAlso _currentFolder <> folderPath Then
                _historyBack.Push(_currentFolder)
                _historyForward.Clear()
                Me.RaisePropertyChanged(NameOf(CanNavigateBack))
                Me.RaisePropertyChanged(NameOf(CanNavigateForward))
            End If
            ClearVirtualFolderState()
            CurrentFolder = folderPath
            Return LoadFolderImagesAsync(folderPath)
        End Function

        Public Sub LoadCurrentFolder()
            If _isVirtualFolder Then
                FilterAndSort()
            Else
                LoadFolderImages(_currentFolder)
            End If
        End Sub

        ''' Muss das Laden abwarten: die Auswahl greift auf Items zu, die erst danach stehen.
        Public Async Function OpenFolderForImage(imagePath As String) As Task
            If String.IsNullOrEmpty(imagePath) OrElse Not File.Exists(imagePath) Then Return
            Dim folder = IO.Path.GetDirectoryName(imagePath)
            If String.IsNullOrEmpty(folder) Then Return

            SetInitialFolderNodeForPath(folder)
            Await NavigateToFolderAsync(folder)
            Dim item = Items.FirstOrDefault(Function(i) String.Equals(i.FilePath, imagePath, StringComparison.OrdinalIgnoreCase))
            If item IsNot Nothing Then
                SelectOnly(item)
            Else
                SelectedItem = Nothing
            End If
            If SelectedItem IsNot Nothing Then RaiseEvent RequestScrollToItem(Me, EventArgs.Empty)
        End Function

        Public Function SelectImageInCurrentView(imagePath As String) As Boolean
            If String.IsNullOrEmpty(imagePath) Then Return False
            ' Aus dem Viewer/Editor kommt bei BEIDEN Servern der Temp-Pfad der geholten Kopie zurück,
            ' nie der Pseudo-Pfad. Der Name der Kopie trägt die Kennung des Elements: bei Immich als
            ' Dateiname-Stamm (die Asset-UUID), bei Nextcloud als Teil vor dem ersten Unterstrich.
            ' Damit lässt sich das Element wiederfinden, auch wenn RemoteLocalPath nicht gesetzt wurde -
            ' die Navigation im Filmstreifen setzt es nicht.
            '
            ' NUTZERBEFUND: fehlte der Nextcloud-Zweig, fand BackToGallery hier nichts und öffnete
            ' statt dessen den ORDNER der Temp-Datei - der Nutzer stand plötzlich im Temp-Ordner der
            ' geholten Originale statt wieder in seiner Zeitachse.
            Dim immichStem = If(ImmichService.IsImmichTempPath(imagePath), IO.Path.GetFileNameWithoutExtension(imagePath), Nothing)
            Dim nextcloudId = NextcloudService.FileIdFromTempPath(imagePath)
            Dim item = Items.FirstOrDefault(Function(i) i.IsImage AndAlso (
                String.Equals(i.FilePath, imagePath, StringComparison.OrdinalIgnoreCase) OrElse
                (i.IsRemoteAsset AndAlso Not String.IsNullOrEmpty(i.RemoteLocalPath) AndAlso String.Equals(i.RemoteLocalPath, imagePath, StringComparison.OrdinalIgnoreCase)) OrElse
                (i.IsImmichAsset AndAlso immichStem IsNot Nothing AndAlso String.Equals(i.ImmichAssetId, immichStem, StringComparison.OrdinalIgnoreCase)) OrElse
                (i.IsNextcloudAsset AndAlso nextcloudId.Length > 0 AndAlso String.Equals(i.NextcloudFileId, nextcloudId, StringComparison.OrdinalIgnoreCase))))
            If item Is Nothing Then Return False
            SelectOnly(item)
            RaiseEvent RequestScrollToItem(Me, EventArgs.Empty)
            Return True
        End Function

        Private Sub NavigateBack()
            If _historyBack.Count = 0 Then Return
            _historyForward.Push(_currentFolder)
            Dim prev = _historyBack.Pop()
            ClearVirtualFolderState()
            CurrentFolder = prev
            LoadFolderImages(prev)
            Me.RaisePropertyChanged(NameOf(CanNavigateBack))
            Me.RaisePropertyChanged(NameOf(CanNavigateForward))
        End Sub

        Private Sub NavigateForward()
            If _historyForward.Count = 0 Then Return
            _historyBack.Push(_currentFolder)
            Dim nextFolder = _historyForward.Pop()
            ClearVirtualFolderState()
            CurrentFolder = nextFolder
            LoadFolderImages(nextFolder)
            Me.RaisePropertyChanged(NameOf(CanNavigateBack))
            Me.RaisePropertyChanged(NameOf(CanNavigateForward))
        End Sub

        Public Sub NavigateToParent()
            If String.IsNullOrEmpty(_currentFolder) Then Return
            Dim parent = IO.Path.GetDirectoryName(_currentFolder)
            If parent IsNot Nothing Then NavigateToFolder(parent)
        End Sub

        Private Sub NavigateToPicturesFolder()
            Dim picturesPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            If String.IsNullOrEmpty(picturesPath) OrElse Not Directory.Exists(picturesPath) Then Return

            Dim node = FindFolderNode(FolderTree, picturesPath)
            If node IsNot Nothing Then SelectedFolderNode = node

            NavigateToFolder(picturesPath)
        End Sub

        ''' <param name="source">Bereich, aus dem "Neue Suche" angeklickt wurde. Er bestimmt die
        ''' Quelle der Suche und den Baum, in dem sie landet.</param>
        Private Async Function OpenSearchDialog(source As String) As Task(Of Boolean)
            Dim result = Await _mainVm.ShowSearchDialogAsync(_searchText, Nothing, SearchListService.NormalizeSource(source))
            If result Is Nothing Then Return False

            Dim saved = New SearchListEntry With {
                .Id = Guid.NewGuid().ToString("N"),
                .Name = result.Name,
                .Source = SearchListService.NormalizeSource(result.Source),
                .TextQuery = result.TextQuery,
                .RootFolder = result.RootFolder,
                .IncludeSubfolders = result.IncludeSubfolders,
                .FavoriteMode = result.FavoriteMode,
                .RatingMin = result.RatingMin,
                .Ratings = If(result.Ratings, New List(Of Integer)()),
                .Conditions = If(result.Conditions, New List(Of SearchCondition)()),
                .ConditionCombinator = If(result.ConditionCombinator, "AND")
            }
            Dim treeNode = CreateSavedSearchNode(saved)
            _savedSearches.Add(saved)
            ' In den Baum der eigenen Quelle - vorher landete auch eine Immich-Suche im Ordner-Tab
            ' und tauchte erst nach einem Neustart an ihrem Platz auf.
            SearchTreeForSource(saved.Source).Add(treeNode)
            SaveSearches()
            OpenSavedSearch(treeNode)
            Return True
        End Function

        ''' Öffnet den Such-Overlay-Dialog vorbelegt mit den Parametern einer bereits gespeicherten
        ''' Suchliste, übernimmt die Änderungen auf denselben Eintrag (gleiche Id) und startet die
        ''' Suche neu. Aufgerufen aus dem Kontextmenü der Sidebar-Suchliste.
        Public Async Sub EditVirtualSearchNode(node As VirtualNavigationNode)
            Try
                If node Is Nothing OrElse Not String.Equals(node.Kind, "SavedSearch", StringComparison.Ordinal) Then Return
                Dim existing = _savedSearches.FirstOrDefault(Function(s) String.Equals(s.Id, node.Id, StringComparison.OrdinalIgnoreCase))
                If existing Is Nothing Then Return

                Dim result = Await _mainVm.ShowSearchDialogAsync(existing.TextQuery, existing)
                If result Is Nothing Then Return

                existing.Name = result.Name
                existing.Source = SearchListService.NormalizeSource(result.Source)
                existing.TextQuery = result.TextQuery
                existing.RootFolder = result.RootFolder
                existing.IncludeSubfolders = result.IncludeSubfolders
                existing.FavoriteMode = result.FavoriteMode
                existing.RatingMin = result.RatingMin
                existing.Ratings = If(result.Ratings, New List(Of Integer)())
                existing.Conditions = If(result.Conditions, New List(Of SearchCondition)())
                existing.ConditionCombinator = If(result.ConditionCombinator, "AND")
                ' Zwischengespeicherte Treffer verwerfen - sie können durch die geänderten Parameter veraltet sein.
                existing.Results = New List(Of String)()
                ThumbnailCacheService.DeleteSearchListCache(existing.Id)
                SaveSearches()

                ' VirtualNavigationNode hat kein INotifyPropertyChanged - den Baumknoten daher ersetzen,
                ' damit u.a. der geänderte Name in der Sidebar erscheint.
                Dim newNode = CreateSavedSearchNode(existing)
                Dim tree = SearchTreeForSource(existing.Source)
                Dim index = tree.IndexOf(node)
                If index >= 0 Then
                    tree(index) = newNode
                Else
                    tree.Add(newNode)
                End If
                OpenSavedSearch(newNode)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryViewModel.EditVirtualSearchNode", ex)
                StatusText = LocalizationService.T("Aktion fehlgeschlagen")
            End Try
        End Sub

        ''' <summary>Alle Bilder mit diesem Stichwort zeigen.
        '''
        ''' Der Klick auf ein Stichwort im Infopanel landet hier. Es entsteht eine Suche wie jede
        ''' andere, nur wird sie NICHT gespeichert - sie ist ein Sprung, keine Liste. Ohne Startordner
        ''' laeuft sie ueber den ganzen Bestand, denn wer auf ein Stichwort klickt, sucht nicht im
        ''' gerade offenen Ordner.</summary>
        Public Sub OpenTagSearch(tag As String)
            Dim wanted = If(tag, "").Trim()
            If String.IsNullOrEmpty(wanted) Then Return
            SetTagFilter({wanted})
        End Sub

        ''' <summary>Alle Bilder zeigen, die MINDESTENS eines dieser Stichwoerter tragen. Leere
        ''' Liste hebt die Auswahl auf und laesst den zuletzt offenen Ordner stehen.</summary>
        Public Sub SetTagFilter(tags As IEnumerable(Of String))
            Dim wanted = If(tags, Enumerable.Empty(Of String)()).
                         Where(Function(t) Not String.IsNullOrWhiteSpace(t)).
                         Select(Function(t) t.Trim()).
                         Distinct(StringComparer.OrdinalIgnoreCase).ToList()

            ' Den Ordner merken, aus dem heraus die Auswahl begonnen hat - "Auswahl aufheben"
            ' kehrt dorthin zurueck, statt die Trefferliste stehen zu lassen.
            If wanted.Count > 0 AndAlso _activeTagFilters.Count = 0 Then
                If _isVirtualFolder Then
                    _nodeBeforeTagFilter = If(SelectedImmichNode, SelectedSearchNode)
                    _folderBeforeTagFilter = ""
                Else
                    _folderBeforeTagFilter = If(_currentFolder, "")
                    _nodeBeforeTagFilter = Nothing
                End If
            End If

            _activeTagFilters = wanted
            RefreshTagFilterState()
            ApplyButtonFilters()
        End Sub

        ''' <summary>Baut aus ALLEN Knopfauswahlen EINEN Suchknoten.
        '''
        ''' UND, nicht ODER: "diese Person, an diesem Ort, mit diesem Stichwort" ist eine einzige
        ''' Frage mit drei Teilen. Vorher baute jeder Knopf seinen eigenen Knoten und ersetzte damit
        ''' den vorherigen - die Auswahlen loeschten sich gegenseitig aus, statt sich zu schneiden.
        '''
        ''' Innerhalb einer Sorte gilt, was zu ihr passt: Stichwoerter ODER (mindestens eines),
        ''' Personen UND (gemeinsam auf einem Bild), Orte ODER (ein Bild hat genau einen Ort).
        ''' Deshalb drei eigene Listen am Knoten und keine gemeinsame Bedingungsliste - die traegt
        ''' nur EINE Verknuepfung fuer alles.
        '''
        ''' Ist nichts mehr ausgewaehlt, kehrt die Ansicht dorthin zurueck, wo die Auswahl begonnen
        ''' hat.</summary>
        Private Sub ApplyButtonFilters()
            Dim tags = _activeTagFilters.ToList()
            Dim places = _activePlaceFilters.ToList()
            Dim people = PersonFilterOptions.
                         Where(Function(o) _activePersonFilters.Contains(o.Id) AndAlso o.IsNamed).
                         Select(Function(o) o.Name).
                         Distinct(StringComparer.OrdinalIgnoreCase).ToList()

            If tags.Count = 0 AndAlso places.Count = 0 AndAlso people.Count = 0 Then
                ReturnToFilterOrigin()
                Return
            End If

            Dim title = String.Join(", ", people.Concat(places).Concat(tags))
            Dim node As New VirtualNavigationNode(title, "SavedSearch") With {
                .Source = "Local",
                .TagQueries = tags,
                .PersonQueries = people,
                .PlaceQueries = places,
                .RootFolder = "",
                .IncludeSubfolders = True,
                .IsRemovable = False
            }
            SidebarTab = TabForNode(node)
            OpenSavedSearch(node)
        End Sub

        ''' <summary>Die Stichwortauswahl aufheben. Der Knopf verliert die Akzentfarbe, und die
        ''' Ansicht kehrt in den Ordner zurueck, aus dem die Auswahl begonnen hat - sonst bliebe die
        ''' Trefferliste stehen, obwohl nichts mehr ausgewaehlt ist.</summary>
        ''' <param name="returnToFolder">Aus, wenn der Aufruf SELBST aus einer Navigation kommt.
        ''' Sonst riefe das Zurueckkehren die Navigation erneut auf.</param>
        Public Sub ClearTagFilter(Optional returnToFolder As Boolean = True)
            If _activeTagFilters.Count = 0 Then Return
            _activeTagFilters = New List(Of String)()
            RefreshTagFilterState()
            ' Die uebrigen Knopfauswahlen bleiben und werden neu angewandt - erst wenn gar nichts
            ' mehr ausgewaehlt ist, kehrt die Ansicht zurueck.
            If returnToFolder Then ApplyButtonFilters() Else ReturnToFilterOrigin(navigate:=False)
        End Sub

        ''' <summary>Alle drei Knopfauswahlen zuruecknehmen, ohne irgendwohin zu navigieren.
        '''
        ''' Fuer den Ordnerwechsel: danach gilt keiner der Filter mehr, und ein Knopf, der noch die
        ''' Akzentfarbe traegt, behauptet etwas Falsches.</summary>
        Private Sub ClearButtonFiltersSilently()
            _activeTagFilters = New List(Of String)()
            _activePersonFilters = New List(Of String)()
            _activePlaceFilters = New List(Of String)()
            RefreshTagFilterState()
            RefreshPersonFilterState()
            RefreshPlaceFilterState()
            ReturnToFilterOrigin(navigate:=False)
        End Sub

        ''' <summary>Zurueck dorthin, wo die Auswahl begonnen hat - in den Ordner oder auf den Knoten.
        '''
        ''' EIGENE METHODE, weil sie drei Filtern gehoert. Personen und Orte riefen dafuer
        ''' ClearTagFilter, und das kehrt gleich in der ersten Zeile um, wenn kein Stichwort
        ''' ausgewaehlt ist: das Aufheben nahm die Akzentfarbe vom Knopf und liess die Trefferliste
        ''' stehen.</summary>
        ''' <param name="navigate">Aus, wenn der Aufruf SELBST aus einer Navigation kommt - sonst
        ''' riefe das Zurueckkehren die Navigation erneut auf. Der gemerkte Ausgangspunkt wird
        ''' trotzdem verworfen, er ist dann ueberholt.</param>
        Private Sub ReturnToFilterOrigin(Optional navigate As Boolean = True)
            Dim back = _folderBeforeTagFilter
            Dim backNode = _nodeBeforeTagFilter
            _folderBeforeTagFilter = ""
            _nodeBeforeTagFilter = Nothing
            If Not navigate OrElse Not _isVirtualFolder Then Return
            If backNode IsNot Nothing Then
                Dim ignored = OpenVirtualNavigationNode(backNode)
            ElseIf Not String.IsNullOrEmpty(back) AndAlso Directory.Exists(back) Then
                NavigateToFolder(back)
            End If
        End Sub

        ''' <summary>Ein Stichwort aus der Liste dazunehmen, abwaehlen - oder, wenn es vom Server
        ''' kommt, dessen Ansicht oeffnen.
        '''
        ''' Die Herkunft kommt vom ANGEKLICKTEN Eintrag, nicht aus einer Namenssuche: dasselbe
        ''' Stichwort kann lokal UND auf dem Server stehen, und eine Suche ueber alle Optionen liefe
        ''' beim lokalen Eintrag faelschlich auf den Server (derselbe Befund wie bei den Orten).</summary>
        Public Sub ToggleTagFilter(entry As TagFilterOption)
            If entry Is Nothing Then Return
            If entry.IsFromServer Then
                ClearButtonFiltersSilently()
                Dim ignored = OpenNextcloudClusterAsync(New VirtualNavigationNode(entry.Tag, "NextcloudTag") With {
                    .Id = entry.ServerId,
                    .IsRemovable = False
                }, "tags")
                Return
            End If
            ToggleTagFilter(entry.Tag)
        End Sub

        ''' <summary>Ein Stichwort dazunehmen oder abwaehlen.</summary>
        Public Sub ToggleTagFilter(tag As String)
            Dim wanted = If(tag, "").Trim()
            If String.IsNullOrEmpty(wanted) Then Return
            Dim next_ = _activeTagFilters.ToList()
            Dim existing = next_.FirstOrDefault(Function(t) String.Equals(t, wanted, StringComparison.OrdinalIgnoreCase))
            If existing IsNot Nothing Then
                next_.Remove(existing)
            Else
                next_.Add(wanted)
            End If
            SetTagFilter(next_)
        End Sub

        ''' <summary>Die Liste fuer das Aufklappmenue: jedes benutzte Stichwort mit der Anzahl
        ''' Bilder. Sie entsteht beim Oeffnen neu, damit frisch vergebene Stichwoerter sofort
        ''' dabei sind.</summary>
        Private _tagFilterSearch As String = ""

        ''' <summary>Suchfeld im Stichwortmenue - dieselbe Bedienung wie bei Personen und Orten.
        '''
        ''' Ein gewachsener Bestand hat schnell dreistellig viele Stichwoerter; ohne Suche waere die
        ''' Liste nur noch scrollbar, nicht benutzbar. Gefiltert wird die ANZEIGE, nicht der Filter:
        ''' die Auswahl bleibt beim Tippen bestehen.</summary>
        Public Property TagFilterSearch As String
            Get
                Return _tagFilterSearch
            End Get
            Set(value As String)
                If String.Equals(_tagFilterSearch, value, StringComparison.Ordinal) Then Return
                Me.RaiseAndSetIfChanged(_tagFilterSearch, value)
                RefreshTagFilterOptions()
            End Set
        End Property

        Public Sub RefreshTagFilterOptions()
            TagFilterOptions.Clear()
            Try
                Dim search = If(_tagFilterSearch, "").Trim()
                For Each entry In LibraryService.Instance.GetTagCounts()
                    ' Ein ausgewaehltes Stichwort bleibt IMMER sichtbar, auch wenn es nicht zur
                    ' Suche passt - sonst verschwindet es beim Tippen und laesst sich nicht mehr
                    ' abwaehlen.
                    Dim matches = search.Length = 0 OrElse
                                  entry.Tag.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                  IsTagFilterSelected(entry.Tag)
                    If Not matches Then Continue For
                    TagFilterOptions.Add(New TagFilterOption(entry.Tag, entry.Count,
                                                             IsTagFilterSelected(entry.Tag)))
                Next
                ' Und die Stichwoerter des Nextcloud-Servers, hinter den lokalen und unter eigener
                ' Ueberschrift. NUR diese Serverquelle steht hier: Nextcloud fuehrt Stichwoerter als
                ' Cluster und kann danach filtern, Immichs Suche kennt keinen Stichwortfilter - ein
                ' Eintrag dafuer waere ein Knopf, der nichts findet.
                Dim firstNextcloudTag = True
                For Each stichwort In _nextcloudTags
                    If stichwort Is Nothing OrElse String.IsNullOrEmpty(stichwort.Id) Then Continue For
                    Dim anzeige = If(String.IsNullOrWhiteSpace(stichwort.Name), stichwort.Id, stichwort.Name)
                    If search.Length > 0 AndAlso anzeige.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 Then Continue For
                    TagFilterOptions.Add(New TagFilterOption(anzeige, stichwort.Count, False,
                                                             serverSource:=NextcloudSourceName,
                                                             serverId:=stichwort.Id) With {
                        .ShowsServerHeader = firstNextcloudTag
                    })
                    firstNextcloudTag = False
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.RefreshTagFilterOptions", ex)
            End Try
        End Sub

        Public Function IsTagFilterSelected(tag As String) As Boolean
            Return _activeTagFilters.Any(Function(t) String.Equals(t, If(tag, ""), StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Traegt der Knopf die Akzentfarbe? Genau dann, wenn eine Stichwortauswahl
        ''' aktiv ist.</summary>
        Public ReadOnly Property HasTagFilter As Boolean
            Get
                Return _activeTagFilters.Count > 0
            End Get
        End Property

        Public ReadOnly Property TagFilterOptions As New ObservableCollection(Of TagFilterOption)()

        ' ── Personen ─────────────────────────────────────────────────────────────

        Private _activePersonFilters As New List(Of String)()

        Public ReadOnly Property PersonFilterOptions As New ObservableCollection(Of PersonFilterOption)()

        ''' <summary>Steht der Knopf ueberhaupt zur Verfuegung? Nur mit eingeschalteter Erkennung UND
        ''' vorhandenen Modellen. Fehlt eines, ist der Knopf ganz WEG und nicht ausgegraut - so haelt
        ''' es die Anwendung bei allen Modellfunktionen, und ein toter Knopf ist schlimmer als
        ''' keiner.</summary>
        Public ReadOnly Property HasPersonFeature As Boolean
            Get
                ' ODER die Personen eines Servers: wer die serverseitige Erkennung nutzt und die
                ' lokale abgeschaltet laesst, sah den Knopf sonst gar nicht - also genau die
                ' Nutzergruppe nicht, fuer die die Servereintraege gedacht sind.
                Return FaceDetectionService.Enabled OrElse _immichPeople.Count > 0 OrElse _nextcloudPeople.Count > 0
            End Get
        End Property

        ''' <summary>Traegt der Knopf die Akzentfarbe? Genau dann, wenn eine Personenauswahl aktiv
        ''' ist - gleiche Regel wie bei den Stichwoertern.</summary>
        Public ReadOnly Property HasPersonFilter As Boolean
            Get
                Return _activePersonFilters.Count > 0
            End Get
        End Property

        ''' <summary>Baut die Liste beim Oeffnen neu auf, damit frisch benannte Personen dabei sind.
        '''
        ''' NUR BENANNTE GRUPPEN. Eine namenlose Gruppe sagt in einer Filterliste nichts - "Ohne
        ''' Namen (17)" dreimal untereinander ist keine Auswahl, sondern Rauschen, und bei einem
        ''' gewachsenen Bestand stehen dort schnell dutzende davon. Gesehen und benannt werden sie am
        ''' Bild im Infopanel, wo der Ausschnitt danebensteht.</summary>
        ''' <summary>Suchfeld im Personen-Menue. Bei einem gewachsenen Bestand hat man schnell
        ''' dreistellig viele Gruppen; ohne Suche waere die Liste dann nur noch scrollbar, nicht
        ''' benutzbar. Die Auswahl bleibt beim Tippen bestehen - gefiltert wird die ANZEIGE, nicht
        ''' der Filter.</summary>
        Private _personFilterSearch As String = ""

        Public Property PersonFilterSearch As String
            Get
                Return _personFilterSearch
            End Get
            Set(value As String)
                If String.Equals(_personFilterSearch, value, StringComparison.Ordinal) Then Return
                Me.RaiseAndSetIfChanged(_personFilterSearch, value)
                RefreshPersonFilterOptions()
            End Set
        End Property

        Public Sub RefreshPersonFilterOptions()
            PersonFilterOptions.Clear()
            Try
                Dim search = If(_personFilterSearch, "").Trim()
                For Each entry In LibraryService.Instance.GetPeople()
                    If entry.ImageCount <= 0 Then Continue For
                    If Not entry.IsNamed Then Continue For
                    ' Eine ausgewaehlte Person bleibt IMMER sichtbar, auch wenn sie nicht zur Suche
                    ' passt - sonst verschwindet sie beim Tippen und man kann sie nicht abwaehlen.
                    Dim matches = search.Length = 0 OrElse
                                  entry.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                  IsPersonFilterSelected(entry.Id)
                    If Not matches Then Continue For
                    PersonFilterOptions.Add(New PersonFilterOption(entry.Id, entry.Name, entry.ImageCount,
                                                                   IsPersonFilterSelected(entry.Id)))
                Next
                ' Und die Personen des Immich-Servers. Sie stehen HINTER den lokalen und tragen den
                ' Vermerk ihrer Herkunft: verunden lassen sich die beiden Beststaende nicht (der
                ' Server filtert nach genau einer Person, und ein Immich-Element steht in keiner
                ' lokalen Tabelle), und eine Liste, die das verschweigt, verspricht etwas Falsches.
                Dim firstImmichPerson = True
                For Each person In _immichPeople
                    If person Is Nothing OrElse String.IsNullOrWhiteSpace(person.Name) Then Continue For
                    Dim matchesImmich = search.Length = 0 OrElse
                                        person.Name.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                    If Not matchesImmich Then Continue For
                    PersonFilterOptions.Add(New PersonFilterOption(person.Id, person.Name, 0, False,
                                                                   serverSource:=ImmichSourceName) With {
                        .ShowsServerHeader = firstImmichPerson
                    })
                    firstImmichPerson = False
                Next
                ' Und dieselbe Reihe fuer die zweite Serverquelle. Der Knopf baute seine Suche bisher
                ' allein aus dem lokalen Katalog - und zu einem Nextcloud-Element steht dort nichts,
                ' der Knopf lief also ins Leere, waehrend dieselben Personen als Zweig in der
                ' Seitenleiste standen.
                Dim firstNextcloudPerson = True
                For Each person In _nextcloudPeople
                    If person Is Nothing OrElse String.IsNullOrEmpty(person.Id) Then Continue For
                    ' Nur benannte Gruppen, wie im Baum und wie bei Immich.
                    If Not person.IsNamed Then Continue For
                    Dim anzeige = If(String.IsNullOrWhiteSpace(person.Name), person.Id, person.Name)
                    If search.Length > 0 AndAlso anzeige.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 Then Continue For
                    PersonFilterOptions.Add(New PersonFilterOption(person.Id, anzeige, person.Count, False,
                                                                   serverSource:=NextcloudSourceName) With {
                        .ShowsServerHeader = firstNextcloudPerson
                    })
                    firstNextcloudPerson = False
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.RefreshPersonFilterOptions", ex)
            End Try
            Me.RaisePropertyChanged(NameOf(HasPersonFeature))
        End Sub

        Public Function IsPersonFilterSelected(personId As String) As Boolean
            Return _activePersonFilters.Any(Function(p) String.Equals(p, If(personId, ""), StringComparison.Ordinal))
        End Function

        ''' <summary>Setzt die Personenauswahl und zeigt die Treffer.
        '''
        ''' Gebaut wie <see cref="SetTagFilter"/>, und das ist Absicht: der Filter legt KEINEN
        ''' eigenen Zustand an, sondern erzeugt einen Suchknoten mit Kriterien. Nur so lassen sich
        ''' die Filter spaeter verunden - vier Filter mit je eigenem Zustand wuerden sich gegenseitig
        ''' ersetzen statt zu schneiden, und "diese Person, dieses Stichwort, fuenf Sterne" waere
        ''' nicht darstellbar.
        '''
        ''' Die Pfade kommen aus der Bibliothek und sind bereits verundet: mehrere Personen heisst
        ''' "wer gemeinsam auf einem Bild steht", nicht "wer auf irgendeinem"
        ''' (LibraryService.GetPathsForPeople).</summary>
        Public Sub SetPersonFilter(personIds As IEnumerable(Of String))
            Dim wanted = If(personIds, Enumerable.Empty(Of String)()).
                         Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
                         Select(Function(p) p.Trim()).
                         Distinct(StringComparer.Ordinal).ToList()

            ' Den Ausgangspunkt merken, damit "Auswahl aufheben" dorthin zurueckkehrt - dieselbe
            ' Ueberlegung wie bei den Stichwoertern.
            If wanted.Count > 0 AndAlso _activePersonFilters.Count = 0 Then
                If _isVirtualFolder Then
                    _nodeBeforeTagFilter = If(SelectedImmichNode, SelectedSearchNode)
                    _folderBeforeTagFilter = ""
                Else
                    _folderBeforeTagFilter = If(_currentFolder, "")
                    _nodeBeforeTagFilter = Nothing
                End If
            End If

            _activePersonFilters = wanted
            RefreshPersonFilterState()
            ' Der Knoten traegt die Auswahl als NAMENSLISTE, keine fertige Trefferliste. Mit Results
            ' allein passierte beim Klick nichts - der Durchlauf wertet sie nur bei einer
            ' gespeicherten Suche mit Id aus. Und er entsteht gemeinsam mit Ort und Stichwort, damit
            ' sich die Knoepfe verunden statt sich zu ersetzen.
            ApplyButtonFilters()
        End Sub

        ''' <summary>Eine Person dazunehmen oder abwaehlen. Verglichen wird ueber die ID, nicht ueber
        ''' den Namen: eine Umbenennung darf einen laufenden Filter nicht ins Leere laufen lassen.</summary>
        Public Sub TogglePersonFilter(personId As String)
            Dim wanted = If(personId, "").Trim()
            If wanted.Length = 0 Then Return
            ' Eine Person des SERVERS ist allein waehlbar: die Abfrage kennt genau eine Person, und
            ' ein Immich-Element steht in keiner lokalen Tabelle - eine Verundung mit lokalen
            ' Stichworten oder Orten gaebe es nirgends zu rechnen. Der Klick oeffnet deshalb direkt
            ' die Server-Ansicht, wie der gleichnamige Knoten in der Seitenleiste.
            Dim vomServer = PersonFilterOptions.FirstOrDefault(Function(o) o IsNot Nothing AndAlso o.IsFromServer AndAlso
                                                                  String.Equals(o.Id, wanted, StringComparison.Ordinal))
            If vomServer IsNot Nothing Then
                ClearButtonFiltersSilently()
                If String.Equals(vomServer.ServerSource, NextcloudSourceName, StringComparison.Ordinal) Then
                    Dim ignoredNextcloud = OpenNextcloudClusterAsync(New VirtualNavigationNode(vomServer.Name, "NextcloudPerson") With {
                        .Id = vomServer.Id,
                        .IsRemovable = False
                    }, "recognize")
                Else
                    Dim ignored = OpenImmichPersonAsync(New VirtualNavigationNode(vomServer.Name, "ImmichPerson") With {
                        .Id = vomServer.Id,
                        .IsRemovable = False
                    })
                End If
                Return
            End If
            Dim next_ = _activePersonFilters.ToList()
            If next_.Contains(wanted) Then
                next_.Remove(wanted)
            Else
                next_.Add(wanted)
            End If
            SetPersonFilter(next_)
        End Sub

        Public Sub ClearPersonFilter()
            If _activePersonFilters.Count = 0 Then Return
            SetPersonFilter(New List(Of String)())
        End Sub

        ' ── Orte ─────────────────────────────────────────────────────────────────

        Private _activePlaceFilters As New List(Of String)()

        Public ReadOnly Property PlaceFilterOptions As New ObservableCollection(Of PlaceFilterOption)()

        ''' <summary>Steht der Knopf zur Verfuegung? Nur mit vorhandener UND eingeschalteter
        ''' Ortstabelle - dieselbe Regel wie beim Personenknopf, und aus demselben Grund ist er sonst
        ''' ganz weg statt ausgegraut.</summary>
        Public ReadOnly Property HasPlaceFeature As Boolean
            Get
                ' Wie beim Personenknopf: die Orte eines Servers halten ihn offen, auch wenn die
                ' lokale Ortstabelle aus ist.
                Return PlaceLookupService.Enabled OrElse _immichPlaces.Count > 0 OrElse _nextcloudPlaces.Count > 0
            End Get
        End Property

        Public ReadOnly Property HasPlaceFilter As Boolean
            Get
                Return _activePlaceFilters.Count > 0
            End Get
        End Property

        Private _placeFilterSearch As String = ""

        ''' <summary>Suchfeld im Ortsmenue. Ein gewachsener Bestand hat schnell hunderte Orte.</summary>
        Public Property PlaceFilterSearch As String
            Get
                Return _placeFilterSearch
            End Get
            Set(value As String)
                If String.Equals(_placeFilterSearch, value, StringComparison.Ordinal) Then Return
                Me.RaiseAndSetIfChanged(_placeFilterSearch, value)
                RefreshPlaceFilterOptions()
            End Set
        End Property

        ''' <summary>Baut die Ortsliste beim Oeffnen neu auf.
        '''
        ''' Sie kommt aus dem Katalog, nicht aus den Dateien: der Ortsname entsteht beim Einlesen aus
        ''' den Koordinaten. Was vor der Ortstabelle eingelesen wurde, traegt ihn noch nicht -
        ''' deshalb zieht <see cref="FillMissingPlacesInBackground"/> ihn einmalig nach, sonst
        ''' bliebe diese Liste auf einem gewachsenen Bestand dauerhaft leer.</summary>
        Public Sub RefreshPlaceFilterOptions()
            PlaceFilterOptions.Clear()
            Try
                Dim search = If(_placeFilterSearch, "").Trim()
                For Each entry In LibraryService.Instance.GetPlaceCounts()
                    ' Ein ausgewaehlter Ort bleibt sichtbar, auch wenn er nicht zur Suche passt -
                    ' sonst verschwindet er beim Tippen und laesst sich nicht mehr abwaehlen.
                    ' Gesucht wird auch im UEBERSETZTEN Landesnamen: wer "Deutschland" tippt, meint
                    ' das, was er in der Liste liest - in der Tabelle steht "Germany".
                    Dim land = PlaceLookupService.LocalizedCountry(entry.CountryCode, entry.Country)
                    Dim matches = search.Length = 0 OrElse
                                  entry.City.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                  entry.Country.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                  land.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                  IsPlaceFilterSelected(entry.City)
                    If Not matches Then Continue For
                    PlaceFilterOptions.Add(New PlaceFilterOption(entry.City, entry.Country, entry.Count,
                                                                 IsPlaceFilterSelected(entry.City),
                                                                 entry.CountryCode))
                Next
                ' Und die Staedte des Immich-Servers, hinter den lokalen und mit dem Vermerk ihrer
                ' Herkunft - dieselbe Regel wie bei den Personen. Der Server kennt nur die STADT,
                ' kein Land: das Feld bleibt leer, und die Beschriftung faellt entsprechend kuerzer aus.
                Dim firstImmichPlace = True
                For Each city In _immichPlaces
                    If String.IsNullOrWhiteSpace(city) Then Continue For
                    Dim matchesImmich = search.Length = 0 OrElse
                                        city.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                    If Not matchesImmich Then Continue For
                    PlaceFilterOptions.Add(New PlaceFilterOption(city, "", 0, False, "", serverSource:=ImmichSourceName) With {
                        .ShowsServerHeader = firstImmichPlace
                    })
                    firstImmichPlace = False
                Next
                ' Die Orte der zweiten Serverquelle. Sie kommen als Cluster und tragen - anders als
                ' die Staedte von Immich - eine Anzahl.
                Dim firstNextcloudPlace = True
                For Each ort In _nextcloudPlaces
                    If ort Is Nothing OrElse String.IsNullOrEmpty(ort.Id) Then Continue For
                    Dim anzeige = If(String.IsNullOrWhiteSpace(ort.Name), ort.Id, ort.Name)
                    If search.Length > 0 AndAlso anzeige.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 Then Continue For
                    PlaceFilterOptions.Add(New PlaceFilterOption(anzeige, "", ort.Count, False, "",
                                                                 serverSource:=NextcloudSourceName,
                                                                 serverId:=ort.Id) With {
                        .ShowsServerHeader = firstNextcloudPlace
                    })
                    firstNextcloudPlace = False
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.RefreshPlaceFilterOptions", ex)
            End Try
            Me.RaisePropertyChanged(NameOf(HasPlaceFeature))
        End Sub

        Public Function IsPlaceFilterSelected(city As String) As Boolean
            Return _activePlaceFilters.Any(Function(p) String.Equals(p, If(city, ""), StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Setzt die Ortsauswahl und zeigt die Treffer.
        '''
        ''' Gebaut wie der Personenfilter - ein Suchknoten mit Bedingungen, kein eigener Zustand.
        '''
        ''' ODER statt UND, und das ist der Unterschied zu Personen und Stichwoertern: ein Bild hat
        ''' GENAU EINEN Aufnahmeort. Zwei Orte mit UND ergaeben immer eine leere Liste; gemeint ist
        ''' "von hier oder von dort".</summary>
        Public Sub SetPlaceFilter(cities As IEnumerable(Of String))
            Dim wanted = If(cities, Enumerable.Empty(Of String)()).
                         Where(Function(c) Not String.IsNullOrWhiteSpace(c)).
                         Select(Function(c) c.Trim()).
                         Distinct(StringComparer.OrdinalIgnoreCase).ToList()

            If wanted.Count > 0 AndAlso _activePlaceFilters.Count = 0 Then
                If _isVirtualFolder Then
                    _nodeBeforeTagFilter = If(SelectedImmichNode, SelectedSearchNode)
                    _folderBeforeTagFilter = ""
                Else
                    _folderBeforeTagFilter = If(_currentFolder, "")
                    _nodeBeforeTagFilter = Nothing
                End If
            End If

            _activePlaceFilters = wanted
            RefreshPlaceFilterState()
            ApplyButtonFilters()
        End Sub

        Public Sub TogglePlaceFilter(entry As PlaceFilterOption)
            If entry Is Nothing Then Return
            Dim wanted = If(entry.City, "").Trim()
            If wanted.Length = 0 Then Return
            ' Ein Ort des SERVERS ist allein waehlbar - dieselbe Regel wie bei den Personen. Die
            ' Herkunft kommt vom ANGEKLICKTEN Eintrag selbst: dieselbe Stadt kann lokal UND auf dem
            ' Server stehen, und eine Namenssuche ueber alle Optionen liefe beim lokalen Eintrag
            ' faelschlich auf den Server.
            If entry.IsFromServer Then
                ClearButtonFiltersSilently()
                If String.Equals(entry.ServerSource, NextcloudSourceName, StringComparison.Ordinal) Then
                    Dim ignoredNextcloud = OpenNextcloudClusterAsync(New VirtualNavigationNode(entry.City, "NextcloudPlace") With {
                        .Id = entry.ServerId,
                        .IsRemovable = False
                    }, "places")
                Else
                    Dim ignored = OpenImmichPlaceAsync(New VirtualNavigationNode(entry.City, "ImmichPlace") With {
                        .Id = entry.ServerId,
                        .IsRemovable = False
                    })
                End If
                Return
            End If
            Dim next_ = _activePlaceFilters.ToList()
            Dim existing = next_.FirstOrDefault(Function(c) String.Equals(c, wanted, StringComparison.OrdinalIgnoreCase))
            If existing IsNot Nothing Then
                next_.Remove(existing)
            Else
                next_.Add(wanted)
            End If
            SetPlaceFilter(next_)
        End Sub

        Public Sub ClearPlaceFilter()
            If _activePlaceFilters.Count = 0 Then Return
            SetPlaceFilter(New List(Of String)())
        End Sub

        Private Sub RefreshPlaceFilterState()
            Me.RaisePropertyChanged(NameOf(HasPlaceFilter))
            For Each entry In PlaceFilterOptions
                entry.IsSelected = IsPlaceFilterSelected(entry.City)
            Next
        End Sub

        ''' <summary>Zieht Ortsnamen fuer aeltere Bibliothekseintraege nach, einmal je Sitzung.
        '''
        ''' Die Spalten Ort und Land kamen spaeter dazu und werden nur beim Einlesen gefuellt. Auf
        ''' einem gewachsenen Bestand ist deshalb JEDER Eintrag ohne Ortsnamen - gemessen 13333
        ''' Eintraege, davon 4471 mit Koordinaten und kein einziger mit Namen. Ohne diesen Lauf
        ''' bliebe der Ortsfilter leer, und genau so sah es aus.
        '''
        ''' Im Hintergrund und ohne Meldung: es ist Nacharbeit an vorhandenen Daten, kein Auftrag des
        ''' Benutzers. Steht nichts mehr aus, kostet der Lauf eine Abfrage.</summary>
        Private Sub FillMissingPlacesInBackground()
            If Not PlaceLookupService.Enabled Then Return
            Task.Run(Sub()
                         Try
                             Dim filled = LibraryService.Instance.FillMissingPlaces()
                             If filled <= 0 Then Return
                             Dispatcher.UIThread.Post(Sub() RefreshPlaceFilterOptions())
                         Catch ex As Exception
                             DiagnosticLogService.LogException("Gallery.FillMissingPlaces", ex)
                         End Try
                     End Sub)
        End Sub

        ''' <summary>Sucht Gesichter in dem, was gerade zu sehen ist.
        '''
        ''' AUSWAHL SCHLAEGT ANSICHT: sind Bilder markiert, gilt die Auswahl, sonst die ganze
        ''' Ansicht - dieselbe Regel wie bei den uebrigen Stapelaktionen. Nicht rekursiv: "diese
        ''' Ansicht" ist, was man sieht, und Unterordner stillschweigend mitzunehmen waere eine
        ''' Ueberraschung.
        '''
        ''' JEDES Bild wird durchsucht, auch ein schon gescanntes. Die Buchfuehrung merkt sich nur,
        ''' DASS ein Bild dran war, nicht WOMIT - ein Bestand bliebe sonst fuer immer auf dem Stand
        ''' der Fassung, mit der er einmal durchlief, und eine verbesserte Erkennung kaeme dort nie
        ''' an. Von Hand gesetzte Zuordnungen bleiben stehen: was der Benutzer eingetragen oder
        ''' geloest hat, ist seine Entscheidung und nicht die des Modells.</summary>
        Public Async Function ScanFacesAsync() As Task
            If Not FaceDetectionService.Enabled Then Return
            If _faceScanRunning Then Return

            Dim source = If(SelectedItems IsNot Nothing AndAlso SelectedItems.Count > 0,
                            SelectedItems.Cast(Of ImageItem)().ToList(),
                            Items.ToList())
            Dim paths = source.Where(Function(i) i IsNot Nothing AndAlso Not i.IsFolder).
                               Select(Function(i) i.FilePath).
                               Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
                               Distinct(StringComparer.Ordinal).ToList()
            If paths.Count = 0 Then Return

            _faceScanRunning = True
            _faceScanCancellation = New CancellationTokenSource()
            ' Der Fortschritt steht im Balken oben, NICHT in der Statuszeile: ein Lauf ueber einen
            ' grossen Ordner dauert Minuten, und eine Zeile am unteren Rand ist dafuer zu leise -
            ' man haelt das Programm sonst fuer haengengeblieben.
            Dim runRow = BeginGalleryRun(AddressOf CancelFaceScan)
            ' EINE Schablone statt zusammengesetzter Bruchstuecke: in anderen Sprachen steht die Zahl
            ' woanders im Satz, und aus aneinandergehaengten Teilen laesst sich das nicht bauen.
            runRow.Text = String.Format(LocalizationService.T("Gesichter werden gesucht: {0} von {1}"),
                                        0, paths.Count)
            runRow.Percent = 0
            runRow.HasProgress = True
            Try
                Dim reporter = New Progress(Of (Done As Integer, Total As Integer, File As String))(
                    Sub(p)
                        runRow.Text = String.Format(LocalizationService.T("Gesichter werden gesucht: {0} von {1}"),
                                                    p.Done, p.Total)
                        runRow.Percent = If(p.Total > 0, Math.Min(100.0, p.Done * 100.0 / p.Total), 0.0)
                        runRow.HasProgress = p.Total > 0
                    End Sub)
                Dim result = Await FaceScanRunner.RunAsync(paths, reporter, _faceScanCancellation.Token,
                                                           force:=True).ConfigureAwait(True)
                ' Ein abgebrochener Lauf hat trotzdem etwas gefunden, und das bleibt auch gespeichert -
                ' die Zahl unter den Tisch fallen zu lassen saehe aus, als waere alles umsonst gewesen.
                '
                ' Gar nicht erst gelaufen ist etwas anderes als "nichts gefunden": ohne diesen Zweig
                ' stuende hier "0 Gesichter gefunden", und der Nutzer haette den Eindruck, auf seinen
                ' Bildern sei niemand zu sehen.
                If result.BlockedByOtherWindow Then
                    StatusText = LocalizationService.T("Ein anderes Fenster arbeitet gerade am Katalog")
                ElseIf result.NotStarted Then
                    StatusText = LocalizationService.T("Es läuft bereits eine Suche")
                Else
                    StatusText = If(result.Cancelled,
                                    String.Format(LocalizationService.T("Suche abgebrochen, {0} Gesichter gefunden"),
                                                  result.FacesFound),
                                    String.Format(LocalizationService.T("{0} Gesichter gefunden"), result.FacesFound))
                End If
                RefreshPersonFilterOptions()
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.ScanFaces", ex)
            Finally
                _faceScanRunning = False
                _faceScanCancellation?.Dispose()
                _faceScanCancellation = Nothing
                EndGalleryRun(runRow)
            End Try
        End Function

        ''' <summary>Haelt den laufenden Durchlauf an. Der Knopf dazu steht neben dem Balken.
        '''
        ''' Was bis dahin gefunden wurde, BLEIBT in der Bibliothek: ein Abbruch ist "hoer auf", nicht
        ''' "mach es rueckgaengig". Der Lauf endet nach dem Bild, an dem er gerade sitzt.</summary>
        Public Sub CancelFaceScan()
            Try
                _faceScanCancellation?.Cancel()
            Catch ex As ObjectDisposedException
                ' Der Lauf war in derselben Sekunde von selbst fertig - dann gibt es nichts zu tun.
            End Try
        End Sub

        Private _faceScanRunning As Boolean
        Private _faceScanCancellation As CancellationTokenSource

        Private Sub RefreshPersonFilterState()
            Me.RaisePropertyChanged(NameOf(HasPersonFilter))
            ' "Option" ist in VB ein Schluesselwort (Option Strict) und taugt nicht als Name.
            For Each entry In PersonFilterOptions
                entry.IsSelected = IsPersonFilterSelected(entry.Id)
            Next
        End Sub

        Private Sub RefreshTagFilterState()
            Me.RaisePropertyChanged(NameOf(HasTagFilter))
            ' NICHT "option" als Schleifenvariable: Option ist ein VB-Schluesselwort.
            For Each entry In TagFilterOptions
                entry.IsSelected = IsTagFilterSelected(entry.Tag)
            Next
        End Sub

        Private Sub OpenSavedSearch(node As VirtualNavigationNode)
            If node Is Nothing Then Return
            ' Beide Server laufen denselben Weg: Kandidaten vom Server, gefiltert und ergaenzt
            ' ueber den eigenen Katalog.
            If String.Equals(node.Source, "Immich", StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(node.Source, "Nextcloud", StringComparison.OrdinalIgnoreCase) Then
                SelectedSearchNode = node
                StartServerSearch(node)
                Return
            End If
            If Not String.IsNullOrWhiteSpace(node.RootFolder) AndAlso Not Directory.Exists(node.RootFolder) Then
                StatusText = LocalizationService.T("Startordner nicht gefunden")
                Return
            End If
            SelectedSearchNode = node
            StartIncrementalSavedSearch(node)
        End Sub

        ''' <summary>Führt eine Suchliste auf einem der beiden Server aus - Immich und Nextcloud
        ''' laufen denselben Weg.
        '''
        ''' ZWEI QUELLEN, ein Ergebnis. Der Server liefert Kandidaten, so weit seine Suche reicht:
        ''' Immich sucht semantisch und filtert Favorit und Bewertung gleich mit, Nextcloud sucht im
        ''' Dateinamen und kann sonst nichts. Alles Weitere beantwortet der eigene Katalog - er hält
        ''' Bewertung, Favorit, Stichwörter, Personen und Orte zu jedem Bild, das schon einmal
        ''' geöffnet oder bewertet wurde, auch zu Serverbildern (sie stehen dort unter ihrem
        ''' Pseudo-Pfad). Deshalb kommt der Katalog zusätzlich als eigene Trefferquelle dazu: ein
        ''' Bild, dessen STICHWORT passt, fände der Server nie.
        '''
        ''' Was weder Server noch Katalog wissen, bleibt offen. Eine Bedingung wie „Bildhöhe > 500"
        ''' kann bei einem nie geöffneten Serverbild niemand beantworten, ohne es zu holen - solche
        ''' Bilder fallen heraus, und die Statuszeile sagt, wie viele es waren. Bei einer Ordnersuche
        ''' ist das anders: dort liest der Suchlauf notfalls die Datei selbst.</summary>
        Private Async Sub StartServerSearch(node As VirtualNavigationNode)
            Dim isImmich = String.Equals(node.Source, "Immich", StringComparison.OrdinalIgnoreCase)
            Dim pathPrefix = If(isImmich, "immich://", "nextcloud://")
            Dim textQuery = If(node.TextQuery, "").Trim()
            Dim favoriteMode = AppSettingsService.NormalizeSearchFavoriteMode(node.FavoriteMode)
            Dim ratings = NormalizeRatings(node.Ratings)
            Dim conditions = If(node.Conditions, New List(Of SearchCondition)())
            ' Immich filtert auf genau eine Bewertung - bei mehreren nehmen wir die höchste.
            Dim rating = If(ratings.Count > 0, ratings.Max(), 0)
            Dim favoriteOnly = String.Equals(favoriteMode, "Only", StringComparison.OrdinalIgnoreCase)

            ' DER SERVER ZUERST. Personen, Orte und Stichwörter kennen beide Server selbst; was hier
            ' herausgezogen wird, beantwortet die API und NICHT der Katalog. Übrig bleiben die
            ' Bedingungen, die kein Server führt (Kamera, ISO, Maße, Datum).
            Dim personNames = WantedNames(node, "Person", node.PersonQueries)
            Dim placeNames = WantedNames(node, "Place", node.PlaceQueries)
            Dim tagNames = If(node.TagQueries, New List(Of String)()).
                           Where(Function(t) Not String.IsNullOrWhiteSpace(t)).ToList()
            Dim catalogConditions = conditions.Where(
                Function(c) Not String.Equals(c.Field, "Person", StringComparison.OrdinalIgnoreCase) AndAlso
                            Not String.Equals(c.Field, "Place", StringComparison.OrdinalIgnoreCase)).ToList()
            ' Fragt überhaupt etwas den Server? Wenn nicht (also nur Bewertung, Etikett oder
            ' Aufnahmedaten), trägt der Katalog die Treffer - er ist dann die einzige Quelle, die
            ' diese Angaben hat, und damit vollständig.
            Dim hasServerCriterion = textQuery.Length > 0 OrElse personNames.Count > 0 OrElse
                                     placeNames.Count > 0 OrElse tagNames.Count > 0 OrElse
                                     Not String.Equals(favoriteMode, "Any", StringComparison.OrdinalIgnoreCase) OrElse
                                     ratings.Count > 0

            Dim thumbnailToken = StartEmptyVirtualFolder(node.Name)
            _activeSearchCts = New CancellationTokenSource()
            Dim token = _activeSearchCts.Token
            SelectedImmichNode = Nothing
            SelectedNextcloudNode = Nothing
            IsLoading = True
            Dim runRow = BeginSearchRun()
            StatusText = LocalizationService.T("Suche auf dem Server…")

            Const SafetyCap As Integer = 5000
            Dim seenPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim totalPublished As Integer = 0
            Dim skippedUnanswerable As Integer = 0
            Dim lastSortTick = Environment.TickCount64

            ' Spielt eine Portion Kandidaten ein: erst durch den Katalogfilter, dann in die Ansicht.
            Dim publishBatch =
                Async Function(candidates As List(Of ImageItem), serverMeta As Dictionary(Of String, LibraryImageMeta)) As Task
                    Dim fresh = candidates.Where(Function(i) i IsNot Nothing AndAlso
                                                     Not String.IsNullOrEmpty(i.FilePath) AndAlso
                                                     seenPaths.Add(i.FilePath)).ToList()
                    If fresh.Count = 0 Then Return
                    Dim catalogMeta = LibraryService.Instance.GetMetaForPaths(fresh.Select(Function(i) i.FilePath))
                    Dim kept As New List(Of ImageItem)()
                    For Each item In fresh
                        Dim fromServer As LibraryImageMeta = Nothing
                        serverMeta.TryGetValue(item.FilePath, fromServer)
                        Dim fromCatalog As LibraryImageMeta = Nothing
                        catalogMeta.TryGetValue(item.FilePath, fromCatalog)
                        Dim meta = MergeServerMeta(item.FilePath, fromServer, fromCatalog)
                        If ServerConditionUnanswerable(meta, catalogConditions) Then
                            skippedUnanswerable += 1
                            Continue For
                        End If
                        ' Suchtext, Personen, Orte, Stichwörter und Favoriten hat der Server schon
                        ' beantwortet (bei Immich der Text sogar semantisch, also OHNE Namensbezug).
                        ' Hier bleibt, was nur der Katalog weiß.
                        If Not Await MatchesServerCriteriaAsync(meta, catalogConditions, node.ConditionCombinator,
                                                                "", "Any", ratings) Then Continue For
                        kept.Add(item)
                    Next
                    If kept.Count = 0 Then Return
                    Dim isFirstBatch = (totalPublished = 0)
                    AddPrebuiltItemsToVirtualFolder(kept, sortNow:=False)
                    totalPublished += kept.Count
                    ' Zwischensortierungen bewusst selten (das Neuaufbauen der Liste läuft auf dem
                    ' UI-Thread und konkurriert sonst mit den Viewport-Benachrichtigungen).
                    If isFirstBatch OrElse Environment.TickCount64 - lastSortTick > 1500 Then
                        FilterAndSort()
                        lastSortTick = Environment.TickCount64
                    End If
                End Function

            Try
                ' ── 1. Kandidaten vom Server ─────────────────────────────────────────────
                If isImmich Then
                    Dim serverKey = ImmichService.ServerKey
                    ' Personen kennt NUR der Server: die Assetliste im Index traegt keine Gesichter.
                    ' Ohne diesen Abruf blieb eine Suche nach einer Person bei Immich wirkungslos.
                    Dim allowedAssetIds = Await ResolveImmichPersonAssetIdsAsync(personNames, thumbnailToken)
                    Dim page As Integer = 1
                    Do
                        Dim result = Await ImmichService.SearchAsync(textQuery, favoriteOnly, rating, page, thumbnailToken)
                        If token.IsCancellationRequested OrElse thumbnailToken.IsCancellationRequested Then Return
                        If result.Items.Count > 0 Then
                            Dim serverMeta As New Dictionary(Of String, LibraryImageMeta)(StringComparer.OrdinalIgnoreCase)
                            Dim items As New List(Of ImageItem)()
                            For Each asset In result.Items
                                If allowedAssetIds IsNot Nothing AndAlso Not allowedAssetIds.Contains(asset.Id) Then Continue For
                                Dim item = ImageItem.CreateImmichItem(asset, thumbnailToken)
                                items.Add(item)
                                ' Immichs eigener Index trägt Kamera, ISO, Blende, Ort und
                                ' Stichwörter - dieselbe Quelle wie in der Ordnersuche.
                                serverMeta(item.FilePath) = If(BuildSearchMetaFromImmichAsset(serverKey, asset),
                                                               MetaFromImmichAsset(item.FilePath, asset))
                            Next
                            Await publishBatch(items, serverMeta)
                        End If
                        If result.NextPage <= 0 OrElse totalPublished >= SafetyCap Then Exit Do
                        page = result.NextPage
                    Loop

                    ' Der lokale Immich-Index als zweiter Durchgang. Er liegt ohnehin auf der Platte,
                    ' kostet keine Anfrage und traegt Stichwoerter, Ort, Kamera und Bewertung zu
                    ' JEDEM Asset - auch zu denen, die Immichs Suche nicht ausgeworfen hat. Ohne ihn
                    ' fand eine Suche nach einem STICHWORT nichts: die semantische Suche sucht im
                    ' Bildinhalt, nicht in den eigenen Stichwoertern.
                    If totalPublished < SafetyCap Then
                        Dim indexedAssets = Await Task.Run(Of List(Of ImmichAsset))(
                            Function() ImmichIndexService.Instance.GetAssetList(serverKey))
                        If token.IsCancellationRequested OrElse thumbnailToken.IsCancellationRequested Then Return
                        Dim indexMeta As New Dictionary(Of String, LibraryImageMeta)(StringComparer.OrdinalIgnoreCase)
                        Dim indexItems As New List(Of ImageItem)()
                        For Each asset In If(indexedAssets, New List(Of ImmichAsset)())
                            token.ThrowIfCancellationRequested()
                            Dim meta = BuildSearchMetaFromImmichAsset(serverKey, asset)
                            If meta Is Nothing Then Continue For
                            If seenPaths.Contains(meta.FilePath) Then Continue For
                            ' Hier gilt der Suchtext wieder: dieser Durchgang sucht in Name und
                            ' Stichwoertern, waehrend die Serversuche den Bildinhalt abgedeckt hat.
                            If allowedAssetIds IsNot Nothing AndAlso Not allowedAssetIds.Contains(asset.Id) Then Continue For
                            If Not MatchesSavedSearchText(meta.FilePath, meta.Tags, textQuery) Then Continue For
                            If Not MatchesTagQuery(meta.Tags, node.TagQueries) Then Continue For
                            If Not MatchesPlaceQuery(meta, placeNames) Then Continue For
                            ' In diesem Durchgang hat KEIN Server vorgefiltert - Favorit und
                            ' Bewertung gelten deshalb hier.
                            If favoriteMode = "Only" AndAlso Not meta.IsFavorite Then Continue For
                            If favoriteMode = "Not" AndAlso meta.IsFavorite Then Continue For
                            If ratings.Count > 0 AndAlso Not ratings.Contains(meta.Rating) Then Continue For
                            Dim item = ImageItem.CreateImmichItem(asset, thumbnailToken)
                            If item Is Nothing OrElse String.IsNullOrEmpty(item.FilePath) Then Continue For
                            indexItems.Add(item)
                            indexMeta(item.FilePath) = meta
                        Next
                        If indexItems.Count > 0 Then Await publishBatch(indexItems, indexMeta)
                    End If
                Else
                    ' NEXTCLOUD. Der Server beantwortet, was er kann, und zwar ALLES davon: den
                    ' Dateinamen über seine Suche, Personen, Orte und Stichwörter über ihre Cluster,
                    ' Favoriten über die WebDAV-Eigenschaft. Mehrere Kriterien wirken als UND,
                    ' deshalb werden die Kennungsmengen geschnitten. Der Katalog kommt erst danach
                    ' und nur für das, was der Server nicht führt: Bewertung, Farbetikett und die
                    ' Aufnahmedaten.
                    Dim allowedFileIds As HashSet(Of String) = Nothing
                    Dim intersectWith = Sub(more As HashSet(Of String))
                                            If more Is Nothing Then Return
                                            If allowedFileIds Is Nothing Then
                                                allowedFileIds = more
                                            Else
                                                allowedFileIds.IntersectWith(more)
                                            End If
                                        End Sub

                    Dim hits As List(Of NextcloudService.NextcloudSearchHit) = Nothing
                    If textQuery.Length > 0 Then
                        ' Der Suchtext meint Dateiname ODER Stichwort - genau wie bei einer
                        ' Ordnersuche, wo beides im selben Feld gesucht wird. Die Dateisuche des
                        ' Servers kennt nur den Namen, die Stichwoerter stehen in seinen System-Tags:
                        ' beide Mengen werden VEREINIGT, nicht geschnitten.
                        hits = Await NextcloudService.SearchFilesAsync(textQuery, cancellationToken:=thumbnailToken)
                        Dim byName As New HashSet(Of String)(hits.Select(Function(h) h.FileId), StringComparer.OrdinalIgnoreCase)
                        Dim byTag = Await NextcloudClusterFileIdsAsync("tags", {textQuery}, thumbnailToken)
                        If byTag IsNot Nothing Then byName.UnionWith(byTag)
                        intersectWith(byName)
                    End If
                    intersectWith(Await NextcloudClusterFileIdsAsync("recognize", personNames, thumbnailToken))
                    intersectWith(Await NextcloudClusterFileIdsAsync("places", placeNames, thumbnailToken))
                    intersectWith(Await NextcloudClusterFileIdsAsync("tags", tagNames, thumbnailToken))
                    If String.Equals(favoriteMode, "Only", StringComparison.OrdinalIgnoreCase) Then
                        intersectWith(Await NextcloudService.GetFavoriteFileIdsAsync(thumbnailToken))
                    End If
                    If token.IsCancellationRequested OrElse thumbnailToken.IsCancellationRequested Then Return

                    If allowedFileIds IsNot Nothing Then
                        ' Der Suchtreffer trägt Name und Pfad gleich mit; kam die Kennung aus einem
                        ' Cluster, reicht sie allein - das Übrige kommt mit den Einzelheiten nach.
                        Dim hitById = If(hits, New List(Of NextcloudService.NextcloudSearchHit)()).
                                      Where(Function(h) h IsNot Nothing AndAlso Not String.IsNullOrEmpty(h.FileId)).
                                      GroupBy(Function(h) h.FileId, StringComparer.OrdinalIgnoreCase).
                                      ToDictionary(Function(g) g.Key, Function(g) g.First(), StringComparer.OrdinalIgnoreCase)
                        Dim serverMeta As New Dictionary(Of String, LibraryImageMeta)(StringComparer.OrdinalIgnoreCase)
                        Dim items As New List(Of ImageItem)()
                        For Each id In allowedFileIds
                            Dim hit As NextcloudService.NextcloudSearchHit = Nothing
                            If Not hitById.TryGetValue(id, hit) Then
                                hit = New NextcloudService.NextcloudSearchHit With {.FileId = id}
                            End If
                            ' Die Suche kennt alle Dateien, nicht nur Bilder. Ein Name ohne
                            ' Bildendung fliegt raus; eine Kennung ohne Namen bleibt drin, denn
                            ' aus einem Cluster kommen ohnehin nur Aufnahmen.
                            If Not String.IsNullOrEmpty(hit.FileName) AndAlso
                               Not _imageExtensions.Contains(IO.Path.GetExtension(hit.FileName).ToLowerInvariant()) Then Continue For
                            Dim item = ImageItem.CreateNextcloudSearchItem(hit, thumbnailToken)
                            If String.IsNullOrEmpty(item.FilePath) Then Continue For
                            items.Add(item)
                            serverMeta(item.FilePath) = New LibraryImageMeta With {.FilePath = item.FilePath}
                        Next
                        Await publishBatch(items, serverMeta)
                    End If
                End If

                ' ── 2. Der Katalog als RÜCKFALL ─────────────────────────────────────────
                ' Nur wenn den Server nichts gefragt hat. Eine Suche allein nach Bewertung oder
                ' Farbetikett kann er nicht beantworten - diese Angaben stehen ausschliesslich im
                ' Katalog, und dort ist die Menge dann auch vollstaendig: was keine Bewertung hat,
                ' steht auch in keiner Suche danach.
                If Not hasServerCriterion Then
                    Dim catalogHits = Await Task.Run(Of List(Of LibraryImageMeta))(
                        Function() LibraryService.Instance.GetImagesWithPathPrefix(pathPrefix))
                    If token.IsCancellationRequested OrElse thumbnailToken.IsCancellationRequested Then Return
                    Dim catalogItems As New List(Of ImageItem)()
                    Dim catalogItemMeta As New Dictionary(Of String, LibraryImageMeta)(StringComparer.OrdinalIgnoreCase)
                    For Each meta In catalogHits
                        If meta Is Nothing OrElse String.IsNullOrEmpty(meta.FilePath) Then Continue For
                        If seenPaths.Contains(meta.FilePath) Then Continue For
                        If ServerConditionUnanswerable(meta, catalogConditions) Then
                            skippedUnanswerable += 1
                            Continue For
                        End If
                        If Not Await MatchesServerCriteriaAsync(meta, catalogConditions, node.ConditionCombinator,
                                                                textQuery, favoriteMode, ratings) Then Continue For
                        Dim item = CreateServerItemFromPseudoPath(meta.FilePath, isImmich, thumbnailToken)
                        If item Is Nothing Then Continue For
                        catalogItems.Add(item)
                        catalogItemMeta(item.FilePath) = meta
                    Next
                    If catalogItems.Count > 0 Then Await publishBatch(catalogItems, catalogItemMeta)
                End If

                FilterAndSort()
                If totalPublished = 0 Then
                    Dim serverError = If(isImmich, ImmichService.LastError, NextcloudService.LastError)
                    StatusText = If(Not String.IsNullOrEmpty(serverError), serverError, LocalizationService.T("Keine Treffer"))
                ElseIf skippedUnanswerable > 0 Then
                    ' Ehrlich sagen, was nicht geprüft werden konnte - sonst sieht die Liste
                    ' vollständig aus, obwohl Bilder ohne Katalogeintrag herausgefallen sind.
                    StatusText = String.Format(LocalizationService.T("{0} Treffer, {1} ohne Angaben im Katalog übergangen"),
                                               totalPublished, skippedUnanswerable)
                Else
                    StatusText = String.Format(LocalizationService.T("{0} Treffer"), totalPublished)
                End If
            Catch ex As OperationCanceledException
            Catch ex As Exception
                DiagnosticLogService.LogException(If(isImmich, "Immich.Search", "Nextcloud.Search"), ex)
                StatusText = LocalizationService.T("Die Suche ist fehlgeschlagen")
            Finally
                EndSearchRun(runRow)
                If Not thumbnailToken.IsCancellationRequested Then IsLoading = False
            End Try
        End Sub

        ''' <summary>Prüft die Kriterien gegen die Metadaten EINES Serverbildes. Übergeben wird nur,
        ''' was der Server nicht schon beantwortet hat - Personen, Orte und Stichwörter kennt er
        ''' selbst, sie kommen hier nicht mehr an.</summary>
        Private Shared Function MatchesServerCriteriaAsync(meta As LibraryImageMeta,
                                                           conditions As List(Of SearchCondition),
                                                           combinator As String,
                                                           textQuery As String,
                                                           favoriteMode As String,
                                                           selectedRatings As HashSet(Of Integer)) As Task(Of Boolean)
            If meta Is Nothing Then Return Task.FromResult(False)
            If Not MatchesSavedSearchText(meta.FilePath, meta.Tags, textQuery) Then Return Task.FromResult(False)
            If favoriteMode = "Only" AndAlso Not meta.IsFavorite Then Return Task.FromResult(False)
            If favoriteMode = "Not" AndAlso meta.IsFavorite Then Return Task.FromResult(False)
            ' Die Bewertung führt KEIN Server: Immich kennt zwar Sterne, aber das Etikett und die
            ' Feinheiten kommen aus dem Katalog, und Nextcloud hat gar keine. Deshalb wird sie hier
            ' geprüft, auch wenn die Immich-Suche schon vorgefiltert hat.
            If selectedRatings IsNot Nothing AndAlso selectedRatings.Count > 0 Then
                If Not selectedRatings.Contains(meta.Rating) Then Return Task.FromResult(False)
            End If
            If conditions Is Nothing OrElse conditions.Count = 0 Then Return Task.FromResult(True)
            Dim isAnd = Not String.Equals(combinator, "OR", StringComparison.OrdinalIgnoreCase)
            For Each condition In conditions
                Dim isMatch = EvaluateSingleCondition(meta, condition)
                If isAnd AndAlso Not isMatch Then Return Task.FromResult(False)
                If Not isAnd AndAlso isMatch Then Return Task.FromResult(True)
            Next
            Return Task.FromResult(isAnd)
        End Function

        ''' <summary>Die Namen, die eine Suchliste für ein Bedingungsfeld verlangt - aus den
        ''' Bedingungen und (beim Sprung aus dem Infopanel) aus den Direktabfragen des Knotens.</summary>
        Private Shared Function WantedNames(node As VirtualNavigationNode, field As String,
                                            directQueries As IList(Of String)) As List(Of String)
            Dim names As New List(Of String)()
            names.AddRange(If(directQueries, New List(Of String)()).Where(Function(n) Not String.IsNullOrWhiteSpace(n)))
            For Each c In If(node.Conditions, New List(Of SearchCondition)())
                If String.Equals(c.Field, field, StringComparison.OrdinalIgnoreCase) AndAlso
                   Not String.IsNullOrWhiteSpace(c.Value) Then names.Add(c.Value.Trim())
            Next
            Return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ''' <summary>Die Dateikennungen aller Aufnahmen eines Nextcloud-Clusters (Person, Ort,
        ''' Stichwort). Der Weg führt über die Zeitachse mit Clusterfilter - Memories hat keinen
        ''' Endpunkt, der die Dateien eines Clusters in einem Zug liefert.</summary>
        Private Shared Async Function NextcloudClusterFileIdsAsync(backend As String,
                                                                   names As IList(Of String),
                                                                   token As CancellationToken) As Task(Of HashSet(Of String))
            If names Is Nothing OrElse names.Count = 0 Then Return Nothing
            Dim cluster = Await NextcloudService.GetClustersAsync(backend, token)
            Dim matched As HashSet(Of String) = Nothing
            ' Mehrere Namen wirken als UND - wer zwei Personen sucht, meint beide auf einem Bild.
            For Each name In names
                token.ThrowIfCancellationRequested()
                Dim matching = cluster.Where(Function(c) c IsNot Nothing AndAlso
                                                (String.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase) OrElse
                                                 c.Name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)).ToList()
                Dim forThisName As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                For Each c In matching
                    ' NICHT "day" als Schleifenvariable: Day ist eine VB-Funktion (Day(DateValue)).
                    For Each dayEntry In Await NextcloudService.GetDaysAsync(token, c.Id, backend)
                        token.ThrowIfCancellationRequested()
                        For Each photo In Await NextcloudService.GetDayAsync(dayEntry.DayId, token, c.Id, backend)
                            forThisName.Add(photo.FileId.ToString(Globalization.CultureInfo.InvariantCulture))
                        Next
                    Next
                Next
                If matched Is Nothing Then
                    matched = forThisName
                Else
                    matched.IntersectWith(forThisName)
                End If
                If matched.Count = 0 Then Exit For
            Next
            Return matched
        End Function

        ''' <summary>True, wenn eine der Bedingungen ein Feld braucht, das zu diesem Serverbild
        ''' niemand kennt. Bei der Ordnersuche wird die Datei dann gelesen; hier läge sie auf dem
        ''' Server, und ein Download je Kandidat wäre nicht vertretbar.</summary>
        Private Shared Function ServerConditionUnanswerable(meta As LibraryImageMeta,
                                                            conditions As List(Of SearchCondition)) As Boolean
            If conditions Is Nothing OrElse conditions.Count = 0 Then Return False
            Return conditions.Any(Function(c) Not MetaHasField(meta, c.Field))
        End Function

        ''' <summary>Legt die Angaben des Servers und die des Katalogs übereinander. Der KATALOG hat
        ''' Vorrang: was dort steht, hat jemand bewusst gesetzt.</summary>
        Private Shared Function MergeServerMeta(filePath As String,
                                                vomServer As LibraryImageMeta,
                                                ausKatalog As LibraryImageMeta) As LibraryImageMeta
            If ausKatalog Is Nothing Then
                Return If(vomServer, New LibraryImageMeta With {.FilePath = filePath})
            End If
            If vomServer Is Nothing Then Return ausKatalog
            If Not ausKatalog.ImageWidth.HasValue Then ausKatalog.ImageWidth = vomServer.ImageWidth
            If Not ausKatalog.ImageHeight.HasValue Then ausKatalog.ImageHeight = vomServer.ImageHeight
            If Not ausKatalog.Iso.HasValue Then ausKatalog.Iso = vomServer.Iso
            If Not ausKatalog.Aperture.HasValue Then ausKatalog.Aperture = vomServer.Aperture
            If String.IsNullOrWhiteSpace(ausKatalog.Camera) Then ausKatalog.Camera = vomServer.Camera
            If String.IsNullOrWhiteSpace(ausKatalog.DateTaken) Then ausKatalog.DateTaken = vomServer.DateTaken
            If String.IsNullOrWhiteSpace(ausKatalog.City) Then ausKatalog.City = vomServer.City
            If String.IsNullOrWhiteSpace(ausKatalog.Country) Then ausKatalog.Country = vomServer.Country
            If (ausKatalog.Tags Is Nothing OrElse ausKatalog.Tags.Count = 0) AndAlso vomServer.Tags IsNot Nothing Then
                ausKatalog.Tags = vomServer.Tags
            End If
            Return ausKatalog
        End Function

        ''' <summary>Was Immich selbst über ein Bild weiß, in der Form des Katalogs. Immich führt
        ''' einen eigenen EXIF-Index; damit lassen sich Bedingungen wie Kamera, ISO oder Bildgröße
        ''' auch für Bilder beantworten, die FerrumPix noch nie geöffnet hat.</summary>
        Private Shared Function MetaFromImmichAsset(filePath As String, asset As ImmichAsset) As LibraryImageMeta
            Dim meta As New LibraryImageMeta With {.FilePath = filePath}
            If asset Is Nothing Then Return meta
            If asset.Width > 0 Then meta.ImageWidth = asset.Width
            If asset.Height > 0 Then meta.ImageHeight = asset.Height
            meta.Camera = If(asset.Camera, "")
            meta.Iso = asset.Iso
            meta.Aperture = asset.Aperture
            meta.IsFavorite = asset.IsFavorite
            meta.Rating = asset.Rating
            meta.City = If(asset.City, "")
            meta.Country = If(asset.Country, "")
            If asset.Tags IsNot Nothing Then meta.Tags = asset.Tags
            If asset.ExifDateTaken.HasValue Then
                meta.DateTaken = asset.ExifDateTaken.Value.ToString("yyyy-MM-dd HH:mm:ss", Globalization.CultureInfo.InvariantCulture)
            ElseIf asset.FileCreatedAt.HasValue Then
                meta.DateTaken = asset.FileCreatedAt.Value.ToString("yyyy-MM-dd HH:mm:ss", Globalization.CultureInfo.InvariantCulture)
            End If
            Return meta
        End Function

        ''' <summary>Baut aus dem Pseudo-Pfad eines Katalogeintrags wieder ein Serverelement. Kennung
        ''' und Name stecken im Pfad; Größe, Aufnahmezeit und - bei Nextcloud - der Pfad im
        ''' Dateibaum kommen nach, sobald die Kachel sichtbar wird.</summary>
        Private Shared Function CreateServerItemFromPseudoPath(pseudoPath As String,
                                                               istImmich As Boolean,
                                                               thumbnailToken As CancellationToken) As ImageItem
            Dim id As String = Nothing
            Dim name As String = Nothing
            If istImmich Then
                If Not ImmichService.TryParsePseudoPath(pseudoPath, id, name) Then Return Nothing
                Return ImageItem.CreateImmichItem(New ImmichAsset With {.Id = id, .FileName = name},
                                                  thumbnailToken)
            End If
            If Not NextcloudService.TryParsePseudoPath(pseudoPath, id, name) Then Return Nothing
            Return ImageItem.CreateNextcloudSearchItem(New NextcloudService.NextcloudSearchHit With {
                                                           .FileId = id, .FileName = name}, thumbnailToken)
        End Function

        Private Shared Function CreateSavedSearchNode(search As SearchListEntry) As VirtualNavigationNode
            Return New VirtualNavigationNode(search.Name, "SavedSearch") With {
                .Id = search.Id,
                .Source = SearchListService.NormalizeSource(search.Source),
                .TextQuery = search.TextQuery,
                .RootFolder = search.RootFolder,
                .IncludeSubfolders = search.IncludeSubfolders,
                .FavoriteMode = search.FavoriteMode,
                .RatingMin = search.RatingMin,
                .Ratings = If(search.Ratings, New List(Of Integer)()),
                .Results = If(search.Results, New List(Of String)()),
                .Conditions = If(search.Conditions, New List(Of SearchCondition)()),
                .ConditionCombinator = If(search.ConditionCombinator, "AND"),
                .IsRemovable = True
            }
        End Function

        Private Shared Function GetSearchListCacheScopeId(searchListId As String) As String
            If String.IsNullOrWhiteSpace(searchListId) Then Return Nothing
            Return "searchlist_" & searchListId
        End Function

        Private Async Sub StartIncrementalSavedSearch(node As VirtualNavigationNode)
            Dim cacheScopeId = GetSearchListCacheScopeId(node.Id)
            Dim cacheScopeName = "Suchliste: " & node.Name
            ' Erst die Ansicht leeren (bricht einen laufenden Suchlauf ab), dann den eigenen Token
            ' anlegen - umgekehrt wuerde StartEmptyVirtualFolder die gerade begonnene Suche abwuergen.
            Dim thumbnailToken = StartEmptyVirtualFolder(node.Name)
            _activeSearchCts = New CancellationTokenSource()
            Dim searchCts = _activeSearchCts
            Dim token = _activeSearchCts.Token
            ' Die gemerkten Treffer werden unten in einem Hintergrundlauf geladen - auf dem
            ' UI-Thread wird nichts von der Platte gelesen.
            Dim savedPaths = If(node.Results, New List(Of String)())
            Dim textQuery = If(node.TextQuery, "").Trim()
            Dim rootFolder = If(node.RootFolder, "").Trim()
            Dim favoriteMode = AppSettingsService.NormalizeSearchFavoriteMode(node.FavoriteMode)
            Dim ratingMin = node.RatingMin
            Dim selectedRatings = NormalizeRatings(node.Ratings)
            Dim foundCount = 0
            Dim scannedCount = 0
            Dim foundThisRun As New List(Of String)()

            IsLoading = True
            Dim runRow = BeginSearchRun()
            StatusText = $"Suche läuft... 0 {LocalizationService.T("Bilder")}"

            Try
                Await Task.Run(Async Function()
                                   ' Was Stufe eins bereits abgehandelt hat. Stufe zwei laesst diese
                                   ' Pfade aus: sie findet beim Gang ueber den Ordnerbaum dieselben
                                   ' Dateien noch einmal, holte ihre Katalogzeilen ein ZWEITES Mal
                                   ' und prueste jede Bedingung ein zweites Mal - um sie am Ende als
                                   ' Dublette wegzuwerfen. Bei 7500 gemerkten Treffern war das je
                                   ' Oeffnen die doppelte Katalogabfrage und die doppelte Pruefung.
                                   '
                                   ' Damit das TRAGFAEHIG ist, prueft Stufe eins jetzt selbst (siehe
                                   ' unten): sonst bliebe ein gemerkter Treffer, der die Bedingung
                                   ' laengst nicht mehr erfuellt, ungeprueft stehen. Genau diese
                                   ' Nachpruefung war bisher die Aufgabe von Stufe zwei.
                                   Dim restored As New HashSet(Of String)(PathIdentity.Comparer)

                                   ' Erste Stufe: die zuletzt gefundenen KATALOG-Treffer sofort
                                   ' wiederherstellen. File.Exists gehört bewusst NICHT hierher:
                                   ' auf großen oder schlafenden Laufwerken würde schon diese
                                   ' Prüfung den ersten sichtbaren Inhalt wieder ausbremsen. Die
                                   ' anschließende Dateisystem-Suche prüft und bereinigt den Bestand.
                                   If savedPaths.Count > 0 Then
                                       Dim seenSaved As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                                       Dim published = 0
                                       For Each pathBatch In savedPaths.Chunk(180)
                                           token.ThrowIfCancellationRequested()
                                           ' Papierkorb- und Ordnergrenze sind reine Pfadregeln
                                           ' und damit ohne Dateisystemzugriff sicher prüfbar.
                                           Dim valid = pathBatch.
                                               Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
                                               Where(Function(p) seenSaved.Add(p)).
                                               Where(Function(p) Not IsTrashedLocalPath(p)).
                                               Where(Function(p) _imageExtensions.Contains(IO.Path.GetExtension(p).ToLowerInvariant())).
                                               Where(Function(p) IsPathInSearchRoot(p, rootFolder, node.IncludeSubfolders)).
                                               ToList()
                                           If valid.Count = 0 Then Continue For

                                           Dim metaByPath = LibraryService.Instance.GetMetaForPaths(valid)
                                           Dim catalogPaths = valid.Where(Function(path) metaByPath.ContainsKey(path)).ToList()
                                           Dim matched As New List(Of LibraryImageMeta)()
                                           For Each path In valid
                                               token.ThrowIfCancellationRequested()
                                               Dim m As LibraryImageMeta = Nothing
                                               ' Ohne Katalogzeile gibt es absichtlich keinen
                                               ' Ersatz-Lookup auf der Platte. Der zweite Suchlauf
                                               ' findet diese Datei später und ergänzt sie normal.
                                               If Not metaByPath.TryGetValue(path, m) OrElse m Is Nothing Then Continue For
                                               If Not Await MatchesSavedSearchAsync(node, m, textQuery, favoriteMode, ratingMin, selectedRatings) Then Continue For
                                               matched.Add(m)
                                           Next

                                           ' ERLEDIGT ist auch, was durchgefallen ist: Stufe zwei kaeme
                                           ' ueber dieselbe Zeile zum selben Urteil.
                                           For Each path In catalogPaths
                                               restored.Add(path)
                                           Next
                                           If matched.Count = 0 Then Continue For

                                           Dim prebuilt = matched.
                                               Select(Function(m)
                                                          Dim item = ImageItem.CreateLightweight(m.FilePath, thumbnailToken, cacheScopeId, cacheScopeName)
                                                          item.IsFavorite = m.IsFavorite
                                                          item.Rating = m.Rating
                                                          item.ColorLabel = m.ColorLabel
                                                          item.Tags = If(m.Tags, New List(Of String)())
                                                          item.ImageWidth = If(m.ImageWidth, 0)
                                                          item.ImageHeight = If(m.ImageHeight, 0)
                                                          ' Ohne die Dateidaten aus dem Katalog stuende die Zeitleiste
                                                          ' auf "Ohne Datum" und die Sortierung nach Erstellungs- oder
                                                          ' Aenderungsdatum sortierte nach nichts.
                                                          item.ApplyCatalogFileDates(m.FileCreatedAt, m.ScannedSourceModifiedAt)
                                                          Return item
                                                      End Function).ToList()

                                           ' Die Treffer dieser Stufe zaehlen mit: die Bereinigung am
                                           ' Ende setzt die gemerkte Liste auf das, was DIESER Lauf
                                           ' gefunden hat, und ohne sie fiele alles wieder heraus.
                                           foundThisRun.AddRange(matched.Select(Function(m) m.FilePath))
                                           foundCount += matched.Count

                                           Await Dispatcher.UIThread.InvokeAsync(Sub()
                                               If Not SearchMayPublish(node, token) Then Return
                                               AddPrebuiltItemsToVirtualFolder(prebuilt)
                                           End Sub, DispatcherPriority.Background)

                                           published += prebuilt.Count
                                           Dim localPublished = published
                                           Await Dispatcher.UIThread.InvokeAsync(Sub()
                                               If Not SearchMayPublish(node, token) Then Return
                                               StatusText = $"{localPublished:N0} gespeicherte {LocalizationService.T("Bilder")}  •  Suche läuft..."
                                           End Sub, DispatcherPriority.Background)
                                       Next
                                   End If

                                   If Not String.IsNullOrWhiteSpace(rootFolder) Then
                                       Dim pending As New List(Of String)()
                                       For Each file In EnumerateSearchFilesLazy(rootFolder, node.IncludeSubfolders, textQuery, token)
                                           token.ThrowIfCancellationRequested()
                                           scannedCount += 1
                                           ' DIE ANZEIGE HAENGT AN DIESER STELLE, NICHT AM BLOCK.
                                           ' Gemeldet wurde frueher nur, wenn ein Block von 120
                                           ' zusammenkam - und seit Stufe zwei die gemerkten Treffer
                                           ' auslaesst, wird der bei einer eingelaufenen Suchliste nie
                                           ' mehr voll. Der Text blieb auf dem Stand von Stufe eins
                                           ' stehen, waehrend der Ordnerbaum still weiterlief: es sah
                                           ' aus, als haenge die Suche (Nutzerbefund).
                                           If scannedCount Mod 200 = 0 Then
                                               Dim scannedFound = foundCount
                                               Dim scannedSoFar = scannedCount
                                               Await Dispatcher.UIThread.InvokeAsync(Sub()
                                                   If Not SearchMayPublish(node, token) Then Return
                                                   StatusText = String.Format(
                                                       LocalizationService.T("Suche läuft... {0} Bilder, {1} geprüft"),
                                                       scannedFound.ToString("N0"), scannedSoFar.ToString("N0"))
                                               End Sub, DispatcherPriority.Background)
                                           End If
                                           ' Stufe eins hatte ihn schon - weder Katalogzeile noch
                                           ' Bedingung ein zweites Mal.
                                           If restored.Contains(file) Then Continue For
                                           pending.Add(file)
                                           If pending.Count >= 120 Then
                                               Dim added = Await PublishSearchBatchAsync(node, pending, textQuery, favoriteMode, ratingMin, selectedRatings, thumbnailToken, cacheScopeId, cacheScopeName, token)
                                               foundThisRun.AddRange(added)
                                               foundCount += added.Count
                                               pending.Clear()
                                               Dim localFound = foundCount
                                               Await Dispatcher.UIThread.InvokeAsync(Sub()
                                                   If Not SearchMayPublish(node, token) Then Return
                                                   StatusText = $"Suche läuft... {localFound:N0} {LocalizationService.T("Bilder")}"
                                               End Sub)
                                           End If
                                       Next
                                       If pending.Count > 0 Then
                                           Dim added = Await PublishSearchBatchAsync(node, pending, textQuery, favoriteMode, ratingMin, selectedRatings, thumbnailToken, cacheScopeId, cacheScopeName, token)
                                           foundThisRun.AddRange(added)
                                           foundCount += added.Count
                                       End If
                                   Else
                                       Dim pending As New List(Of LibraryImageMeta)()
                                       For Each meta In EnumerateCatalogSearchMetasLazy("", node.IncludeSubfolders, token, node)
                                           token.ThrowIfCancellationRequested()
                                           scannedCount += 1
                                           ' Melden am Fortschritt, nicht am Block - siehe oben.
                                           If scannedCount Mod 200 = 0 Then
                                               Dim scannedFound = foundCount
                                               Dim scannedSoFar = scannedCount
                                               Await Dispatcher.UIThread.InvokeAsync(Sub()
                                                   If Not SearchMayPublish(node, token) Then Return
                                                   StatusText = String.Format(
                                                       LocalizationService.T("Suche läuft... {0} Bilder, {1} geprüft"),
                                                       scannedFound.ToString("N0"), scannedSoFar.ToString("N0"))
                                               End Sub, DispatcherPriority.Background)
                                           End If
                                           ' Siehe oben: was Stufe eins abgehandelt hat, bleibt aus.
                                           If restored.Contains(meta.FilePath) Then Continue For
                                           pending.Add(meta)
                                           If pending.Count >= 120 Then
                                               Dim added = Await PublishSearchMetaBatchAsync(node, pending, textQuery, favoriteMode, ratingMin, selectedRatings, thumbnailToken, cacheScopeId, cacheScopeName, token)
                                               foundThisRun.AddRange(added)
                                               foundCount += added.Count
                                               pending.Clear()
                                               Dim localFound = foundCount
                                               Await Dispatcher.UIThread.InvokeAsync(Sub()
                                                   If Not SearchMayPublish(node, token) Then Return
                                                   StatusText = $"Suche läuft... {localFound:N0} {LocalizationService.T("Bilder")}"
                                               End Sub)
                                           End If
                                       Next
                                       If pending.Count > 0 Then
                                           Dim added = Await PublishSearchMetaBatchAsync(node, pending, textQuery, favoriteMode, ratingMin, selectedRatings, thumbnailToken, cacheScopeId, cacheScopeName, token)
                                           foundThisRun.AddRange(added)
                                           foundCount += added.Count
                                       End If

                                       ' Dritte Stufe: die Bilder eines Immich-Servers. Sie stehen in
                                       ' keinem Katalog, also findet die Stufe darüber sie nie.
                                       ' Gesucht wird über den LOKALEN Index, ohne eine einzige
                                       ' Serveranfrage. Nur ohne Suchordner: eine Suche, die
                                       ' ausdrücklich einen Ordner auf der Platte meint, meint keinen
                                       ' Server.
                                       Dim serverKey = ImmichService.ServerKey
                                       If ImmichService.IsConfigured AndAlso Not String.IsNullOrEmpty(serverKey) Then
                                           ' Die Timeline im lokalen Index kennt keine Gesichter. Fuer
                                           ' einen Personenfilter holen wir deshalb nur die IDs der
                                           ' betroffenen Personen vom Server und schneiden damit die
                                           ' Indexmenge - ohne Filter bleibt die Suche weiterhin rein
                                           ' lokal und verursacht keine Netzabfrage.
                                           Dim allowedImmichPersonAssets = Await ResolveImmichPersonAssetIdsAsync(node.PersonQueries, token)
                                           If allowedImmichPersonAssets Is Nothing OrElse allowedImmichPersonAssets.Count > 0 Then
                                               Dim immichPending As New List(Of ImmichAsset)()
                                               For Each asset In ImmichIndexService.Instance.GetAssetList(serverKey)
                                                   token.ThrowIfCancellationRequested()
                                                   If asset Is Nothing OrElse asset.IsVideo Then Continue For
                                                   If allowedImmichPersonAssets IsNot Nothing AndAlso Not allowedImmichPersonAssets.Contains(asset.Id) Then Continue For
                                                   scannedCount += 1
                                                   immichPending.Add(asset)
                                                   If immichPending.Count < 120 Then Continue For
                                                   Dim addedImmich = Await PublishImmichSearchBatchAsync(node, immichPending, serverKey, textQuery, favoriteMode, ratingMin, selectedRatings, thumbnailToken, token, skipPersonQuery:=allowedImmichPersonAssets IsNot Nothing)
                                                   foundCount += addedImmich.Count
                                                   immichPending.Clear()
                                                   Dim localFoundImmich = foundCount
                                                   Await Dispatcher.UIThread.InvokeAsync(Sub()
                                                       If Not SearchMayPublish(node, token) Then Return
                                                       StatusText = $"Suche läuft... {localFoundImmich:N0} {LocalizationService.T("Bilder")}"
                                                   End Sub)
                                               Next
                                               If immichPending.Count > 0 Then
                                                   Dim addedImmich = Await PublishImmichSearchBatchAsync(node, immichPending, serverKey, textQuery, favoriteMode, ratingMin, selectedRatings, thumbnailToken, token, skipPersonQuery:=allowedImmichPersonAssets IsNot Nothing)
                                                   foundCount += addedImmich.Count
                                               End If
                                           End If
                                       End If
                                   End If
                               End Function, token)

                If SearchMayPublish(node, token) Then
                    CleanupSearchListResults(node, foundThisRun)
                    StatusText = $"{foundCount:N0} {LocalizationService.T("Bilder")}  •  {CurrentFolderName}"
                End If
            Catch ex As OperationCanceledException
            Catch ex As Exception
                If Not token.IsCancellationRequested Then StatusText = LocalizationService.T("Suche fehlgeschlagen")
            Finally
                ' Die eigene Zeile immer abraeumen, auch nach einem Abbruch: sie gehoert diesem Lauf
                ' allein, ein zweiter Aufruf laeuft ins Leere.
                EndSearchRun(runRow)
                If Object.ReferenceEquals(_activeSearchCts, searchCts) Then
                    _activeSearchCts.Dispose()
                    _activeSearchCts = Nothing
                    IsLoading = False
                End If
            End Try
        End Sub

        Private Iterator Function EnumerateCatalogSearchMetasLazy(rootFolder As String, includeSubfolders As Boolean,
                                                                 token As CancellationToken,
                                                                 Optional node As VirtualNavigationNode = Nothing) As IEnumerable(Of LibraryImageMeta)
            Dim root = If(rootFolder, "").Trim()
            ' Vor dem Durchlauf leeren: eine gerade vergebene Benennung soll sofort greifen.
            ResetPersonNameCache()

            ' Laesst sich die Menge vorab eingrenzen, dann nur diese Eintraege - sonst alle.
            Dim narrowed = NarrowSearchMetas(node)
            Dim source As IEnumerable(Of LibraryImageMeta) =
                If(narrowed, DirectCast(LibraryService.Instance.GetAllImages(), IEnumerable(Of LibraryImageMeta)))

            ' DIESELBE REGEL WIE IM ORDNERDURCHLAUF (siehe EnumerateImageFilesForSearch). Der
            ' Durchlauf ueber die Ordner steigt nicht in Papierkorb und versteckte Ordner - der
            ' Durchlauf ueber den KATALOG tat es weiterhin, und der Katalog traegt solche Eintraege:
            ' aus Durchlaeufen von frueher und von jedem Ordner, den jemand mit eingeschalteten
            ' versteckten Ordnern besucht hat. Ueber Person, Ort, Stichwort oder Bewertung stand ein
            ' weggeworfenes Bild damit wieder in der Trefferliste, und loeschen liess es sich nicht
            ' (die Regel weist versteckte Pfade ab, siehe Bildschirmfoto Nutzerbefund 2026-08-11).
            ' Beide Bedingungen VOR File.Exists: Zeichenkettenarbeit ist billiger als ein Zugriff.
            Dim showHidden = FolderNode.ShowHiddenFolders
            For Each meta In source
                token.ThrowIfCancellationRequested()
                If meta Is Nothing OrElse String.IsNullOrWhiteSpace(meta.FilePath) Then Continue For
                ' Der Papierkorb IMMER nicht - auch mit eingeschalteten versteckten Ordnern.
                If FileOperationPolicy.IsTrashFolder(meta.FilePath) Then Continue For
                If Not showHidden AndAlso FileOperationPolicy.IsInHiddenFolder(meta.FilePath) Then Continue For
                If Not File.Exists(meta.FilePath) Then Continue For
                If Not _imageExtensions.Contains(IO.Path.GetExtension(meta.FilePath).ToLowerInvariant()) Then Continue For
                If Not IsPathInSearchRoot(meta.FilePath, root, includeSubfolders) Then Continue For
                Yield meta
            Next
        End Function

        ''' <summary>Die Eintraege, die ueberhaupt in Frage kommen - oder Nothing, wenn sich das
        ''' nicht sagen laesst.
        '''
        ''' WARUM: ohne Startordner geht der Durchlauf ueber JEDEN Katalogeintrag und fragt fuer
        ''' jeden die Datei ab. Bei 13000 Eintraegen und einem Ort mit dreissig Bildern stand die
        ''' Galerie lange leer, weil die Treffer irgendwo weit hinten lagen. Person und Ort liegen
        ''' aber in der Datenbank und lassen sich dort in EINER Abfrage einschraenken.
        '''
        ''' NUR EINE VORAUSWAHL: die Bedingungen werden danach ganz normal ausgewertet, ebenso Text,
        ''' Favorit und Sterne. Eingegrenzt wird deshalb nur, wo die Abfrage GENAU dasselbe meint wie
        ''' die Bedingung - Gleichheit, und bei mehreren Bedingungen dieselbe Verknuepfung, die auch
        ''' die Abfrage kennt: Orte ODER, Personen UND. Alles andere gibt Nothing zurueck und laeuft
        ''' wie bisher.</summary>
        Private Shared Function NarrowSearchMetas(node As VirtualNavigationNode) As List(Of LibraryImageMeta)
            If node Is Nothing Then Return Nothing
            Try
                ' Die Knopfauswahl zuerst: sie ist der haeufige Fall und laesst sich sauber
                ' schneiden - Personen ueber ihre Namen, Orte ueber Ort und Land.
                Dim hasPeople = node.PersonQueries IsNot Nothing AndAlso node.PersonQueries.Count > 0
                Dim hasPlaces = node.PlaceQueries IsNot Nothing AndAlso node.PlaceQueries.Count > 0
                If hasPeople OrElse hasPlaces Then
                    Dim narrowedPaths As HashSet(Of String) = Nothing
                    If hasPeople Then
                        narrowedPaths = New HashSet(Of String)(
                            LibraryService.Instance.GetPathsForPersonNames(node.PersonQueries.ToList()),
                            StringComparer.Ordinal)
                    End If
                    If hasPlaces Then
                        Dim placePaths = LibraryService.Instance.GetPathsForPlaces(node.PlaceQueries.ToList())
                        If narrowedPaths Is Nothing Then
                            narrowedPaths = New HashSet(Of String)(placePaths, StringComparer.Ordinal)
                        Else
                            narrowedPaths.IntersectWith(placePaths)
                        End If
                    End If
                    If narrowedPaths.Count = 0 Then Return New List(Of LibraryImageMeta)()
                    Return LibraryService.Instance.GetMetaForPaths(narrowedPaths).Values.ToList()
                End If

                If node.Conditions Is Nothing OrElse node.Conditions.Count = 0 Then Return Nothing
                Dim conditions = node.Conditions.Where(Function(c) c IsNot Nothing).ToList()
                If conditions.Count = 0 Then Return Nothing
                If Not conditions.All(Function(c) String.Equals(c.Operator, "=", StringComparison.Ordinal)) Then Return Nothing
                If conditions.Any(Function(c) String.IsNullOrWhiteSpace(c.Value)) Then Return Nothing
                Dim isOr = String.Equals(node.ConditionCombinator, "OR", StringComparison.OrdinalIgnoreCase)

                Dim paths As List(Of String) = Nothing
                If conditions.All(Function(c) String.Equals(c.Field, "Place", StringComparison.Ordinal)) Then
                    ' Mehrere Orte gehen nur mit ODER: ein Bild hat genau einen Aufnahmeort.
                    If conditions.Count > 1 AndAlso Not isOr Then Return Nothing
                    paths = LibraryService.Instance.GetPathsForPlaces(conditions.Select(Function(c) c.Value.Trim()).ToList())
                ElseIf conditions.All(Function(c) String.Equals(c.Field, "Person", StringComparison.Ordinal)) Then
                    ' Mehrere Personen heisst "gemeinsam auf einem Bild", also UND.
                    If conditions.Count > 1 AndAlso isOr Then Return Nothing
                    paths = LibraryService.Instance.GetPathsForPersonNames(conditions.Select(Function(c) c.Value.Trim()).ToList())
                Else
                    Return Nothing
                End If

                If paths Is Nothing Then Return Nothing
                If paths.Count = 0 Then Return New List(Of LibraryImageMeta)()
                Dim byPath = LibraryService.Instance.GetMetaForPaths(paths)
                Return byPath.Values.ToList()
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.NarrowSearchMetas", ex)
                Return Nothing
            End Try
        End Function

        Private Shared Function IsPathInSearchRoot(filePath As String, rootFolder As String, includeSubfolders As Boolean) As Boolean
            If String.IsNullOrWhiteSpace(rootFolder) Then Return True
            Try
                Dim fullPath = IO.Path.GetFullPath(filePath)
                Dim root = IO.Path.GetFullPath(rootFolder).TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
                If includeSubfolders Then
                    Dim prefix = root & IO.Path.DirectorySeparatorChar
                    Return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                End If

                Dim parent = IO.Path.GetDirectoryName(fullPath)
                Return Not String.IsNullOrEmpty(parent) AndAlso String.Equals(parent, root, StringComparison.OrdinalIgnoreCase)
            Catch
                Return False
            End Try
        End Function

        Private Async Function PublishSearchMetaBatchAsync(node As VirtualNavigationNode,
                                                           metas As List(Of LibraryImageMeta),
                                                           textQuery As String,
                                                           favoriteMode As String,
                                                           ratingMin As Integer,
                                                           selectedRatings As HashSet(Of Integer),
                                                           thumbnailToken As CancellationToken,
                                                           cacheScopeId As String,
                                                           cacheScopeName As String,
                                                           searchToken As CancellationToken) As Task(Of List(Of String))
            Dim matches As New List(Of LibraryImageMeta)()

            For Each meta In metas
                searchToken.ThrowIfCancellationRequested()
                If Not Await MatchesSavedSearchAsync(node, meta, textQuery, favoriteMode, ratingMin, selectedRatings) Then Continue For
                matches.Add(meta)
            Next

            If matches.Count = 0 Then Return New List(Of String)()
            Dim matchedPaths = matches.Select(Function(m) m.FilePath).ToList()

            Await Dispatcher.UIThread.InvokeAsync(Sub()
                If Not SearchMayPublish(node, searchToken) Then Return
                AddMetasToVirtualFolder(matches, thumbnailToken, cacheScopeId, cacheScopeName)
                AppendSearchListResults(node, matchedPaths)
            End Sub)

            Return matchedPaths
        End Function

        ''' <summary>Passt diese Zeile auf die gespeicherte Suche? EINE Stelle für alle drei Quellen
        ''' (Ordner, Katalog, Immich-Index) - drei Kopien derselben Kette wären genau die Bauart, bei
        ''' der eine neue Bedingung in zweien landet und in der dritten fehlt.</summary>
        Private Async Function MatchesSavedSearchAsync(node As VirtualNavigationNode,
                                                       meta As LibraryImageMeta,
                                                       textQuery As String,
                                                       favoriteMode As String,
                                                       ratingMin As Integer,
                                                       selectedRatings As HashSet(Of Integer),
                                                       Optional skipPersonQuery As Boolean = False) As Task(Of Boolean)
            If meta Is Nothing Then Return False
            If Not MatchesSavedSearchText(meta.FilePath, meta.Tags, textQuery) Then Return False
            If Not MatchesTagQuery(meta.Tags, node.TagQueries) Then Return False
            If Not skipPersonQuery AndAlso Not MatchesPersonQuery(meta.FilePath, node.PersonQueries) Then Return False
            If Not MatchesPlaceQuery(meta, node.PlaceQueries) Then Return False
            If favoriteMode = "Only" AndAlso Not meta.IsFavorite Then Return False
            If favoriteMode = "Not" AndAlso meta.IsFavorite Then Return False
            If selectedRatings IsNot Nothing AndAlso selectedRatings.Count > 0 Then
                If Not selectedRatings.Contains(meta.Rating) Then Return False
            Else
                If ratingMin = 0 AndAlso meta.Rating <> 0 Then Return False
                If ratingMin > 0 AndAlso meta.Rating < ratingMin Then Return False
            End If
            Return Await EvaluateConditionsAsync(meta, node.Conditions, node.ConditionCombinator)
        End Function

        ''' <summary>Die Asset-IDs der Immich-Personen, die fuer einen gespeicherten
        ''' Personenfilter gemeinsam gelten. Die Assetliste im lokalen Index traegt bewusst keine
        ''' Gesichter; erst der gezielte Serverabruf pro ausgewaehlter Person liefert diese
        ''' Zuordnung. Ohne Personenfilter gibt Nothing zurueck, damit der normale Suchlauf keine
        ''' Netzverbindung benoetigt.</summary>
        Private Async Function ResolveImmichPersonAssetIdsAsync(personNames As IList(Of String),
                                                                 token As CancellationToken) As Task(Of HashSet(Of String))
            Dim wanted = If(personNames, New List(Of String)()).
                Where(Function(n) Not String.IsNullOrWhiteSpace(n)).
                Select(Function(n) n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            If wanted.Count = 0 Then Return Nothing

            Dim people = _immichPeople.Where(Function(p) p IsNot Nothing AndAlso
                                                           Not String.IsNullOrWhiteSpace(p.Id) AndAlso
                                                           Not String.IsNullOrWhiteSpace(p.Name)).ToList()
            If people.Count = 0 Then people = Await ImmichService.GetPeopleAsync(token)

            Dim result As HashSet(Of String) = Nothing
            For Each name In wanted
                token.ThrowIfCancellationRequested()
                Dim ids = people.Where(Function(p) String.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).
                                 Select(Function(p) p.Id).Distinct(StringComparer.Ordinal).ToList()
                ' Die Filter wirken als UND. Ist eine Person auf dem Server unbekannt, kann kein
                ' Asset die gesamte Bedingung erfuellen.
                If ids.Count = 0 Then Return New HashSet(Of String)(StringComparer.Ordinal)

                Dim assetsForName As New HashSet(Of String)(StringComparer.Ordinal)
                For Each personId In ids
                    Dim page = 1
                    Do
                        token.ThrowIfCancellationRequested()
                        Dim response = Await ImmichService.GetAssetsPageAsync(page, Nothing, token, personId)
                        For Each asset In response.Items
                            If asset IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(asset.Id) Then assetsForName.Add(asset.Id)
                        Next
                        If response.NextPage <= 0 Then Exit Do
                        page = response.NextPage
                    Loop
                Next

                If result Is Nothing Then
                    result = assetsForName
                Else
                    result.IntersectWith(assetsForName)
                End If
                If result.Count = 0 Then Return result
            Next
            Return If(result, New HashSet(Of String)(StringComparer.Ordinal))
        End Function

        ''' <summary>Der dritte Zweig einer Suchliste: die Bilder eines Immich-Servers, gesucht über
        ''' den LOKALEN Index. Sie tragen einen Pseudo-Pfad und stehen deshalb in keinem Katalog -
        ''' ohne diesen Zweig fand eine gespeicherte Suche sie nie.</summary>
        Private Async Function PublishImmichSearchBatchAsync(node As VirtualNavigationNode,
                                                             assets As List(Of ImmichAsset),
                                                             serverKey As String,
                                                             textQuery As String,
                                                             favoriteMode As String,
                                                             ratingMin As Integer,
                                                             selectedRatings As HashSet(Of Integer),
                                                             thumbnailToken As CancellationToken,
                                                             searchToken As CancellationToken,
                                                             Optional skipPersonQuery As Boolean = False) As Task(Of List(Of String))
            Dim matched As New List(Of ImmichAsset)()
            For Each asset In assets
                searchToken.ThrowIfCancellationRequested()
                Dim meta = BuildSearchMetaFromImmichAsset(serverKey, asset)
                If meta Is Nothing Then Continue For
                If Not Await MatchesSavedSearchAsync(node, meta, textQuery, favoriteMode, ratingMin, selectedRatings, skipPersonQuery) Then Continue For
                matched.Add(asset)
            Next

            If matched.Count = 0 Then Return New List(Of String)()
            Dim matchedPaths = matched.Select(Function(a) ImmichService.MakePseudoPath(a.Id, a.FileName)).ToList()

            Await Dispatcher.UIThread.InvokeAsync(Sub()
                If Not SearchMayPublish(node, searchToken) Then Return
                AddPrebuiltItemsToVirtualFolder(matched.Select(Function(a) ImageItem.CreateImmichItem(a, thumbnailToken)).ToList())
                ' Die Treffer bewusst NICHT in node.Results merken: beim nächsten Öffnen prüft die
                ' erste Stufe jeden gemerkten Pfad mit File.Exists, und ein Pseudo-Pfad fällt dort
                ' immer durch. Der Immich-Zweig läuft ohnehin bei jedem Öffnen neu und ist billig.
            End Sub)

            Return matchedPaths
        End Function

        Private Async Function PublishSearchBatchAsync(node As VirtualNavigationNode,
                                                       files As List(Of String),
                                                       textQuery As String,
                                                       favoriteMode As String,
                                                       ratingMin As Integer,
                                                       selectedRatings As HashSet(Of Integer),
                                                       thumbnailToken As CancellationToken,
                                                       cacheScopeId As String,
                                                       cacheScopeName As String,
                                                       searchToken As CancellationToken) As Task(Of List(Of String))
            Dim metaByPath = LibraryService.Instance.GetMetaForPaths(files)
            Dim matches As New List(Of LibraryImageMeta)()

            For Each file In files
                searchToken.ThrowIfCancellationRequested()
                Dim meta As LibraryImageMeta = Nothing
                If Not metaByPath.TryGetValue(file, meta) Then
                    meta = New LibraryImageMeta With {
                        .FilePath = file,
                        .IsFavorite = False,
                        .Rating = 0,
                        .Tags = New List(Of String)()
                    }
                    ' DEN KATALOG KENNT DIESE DATEI NOCH NICHT - dann kommen ihre Dateidaten direkt
                    ' von der Platte. Ohne sie stuende das Element ohne Datum da, und die Zeitleiste
                    ' meldete fuer den ganzen Bereich "Ohne Datum" (Nutzerbefund 2026-08-28: in einer
                    ' Suche ueber 7500 Bilder war der Katalog noch nicht vollstaendig).
                    '
                    ' HIER und nicht beim Anzeigen: dieser Lauf ist der Hintergrundfaden. Auf einer
                    ' Netzwerkfreigabe ist jeder Dateizugriff ein Gang ueber die Leitung, und auf dem
                    ' Anzeigefaden waere das genau die Stockung, die niemand haben will.
                    Try
                        Dim info As New FileInfo(file)
                        If info.Exists Then
                            meta.FileCreatedAt = info.CreationTime.ToString("o")
                            meta.ScannedSourceModifiedAt = info.LastWriteTime.ToString("o")
                        End If
                    Catch
                        ' Nicht lesbar: dann bleibt das Element ohne Datum, wie bisher.
                    End Try
                End If

                If Not Await MatchesSavedSearchAsync(node, meta, textQuery, favoriteMode, ratingMin, selectedRatings) Then Continue For
                matches.Add(meta)
            Next

            If matches.Count = 0 Then Return New List(Of String)()
            Dim matchedPaths = matches.Select(Function(m) m.FilePath).ToList()

            Await Dispatcher.UIThread.InvokeAsync(Sub()
                If Not SearchMayPublish(node, searchToken) Then Return
                AddMetasToVirtualFolder(matches, thumbnailToken, cacheScopeId, cacheScopeName)
                AppendSearchListResults(node, matchedPaths)
            End Sub)

            Return matchedPaths
        End Function

        ''' Wertet die strukturierten Bedingungen (Breite/Höhe/EXIF) für eine Datei aus. Fehlen
        ''' referenzierte Werte noch in der DB (Bild wurde nie im Viewer/Editor geöffnet), werden sie
        ''' hier einmalig live nachgeladen (EXIF lesen + Bildmaße per Header) und zurückgeschrieben,
        ''' damit der nächste Suchlauf über dieselben Bilder schnell ist.
        Private Async Function EvaluateConditionsAsync(meta As LibraryImageMeta, conditions As List(Of SearchCondition), combinator As String) As Task(Of Boolean)
            If conditions Is Nothing OrElse conditions.Count = 0 Then Return True

            If conditions.Any(Function(c) Not MetaHasField(meta, c.Field)) OrElse IsMetaStale(meta) Then
                Await Task.Run(Sub() ResolveMissingMetaFields(meta))
            End If

            Dim isAnd = Not String.Equals(combinator, "OR", StringComparison.OrdinalIgnoreCase)
            For Each condition In conditions
                Dim isMatch = EvaluateSingleCondition(meta, condition)
                If isAnd AndAlso Not isMatch Then Return False
                If Not isAnd AndAlso isMatch Then Return True
            Next
            Return isAnd
        End Function

        Private Shared Function MetaHasField(meta As LibraryImageMeta, field As String) As Boolean
            Select Case field
                Case "Width" : Return meta.ImageWidth.HasValue
                Case "Height" : Return meta.ImageHeight.HasValue
                Case "Camera" : Return Not String.IsNullOrWhiteSpace(meta.Camera)
                Case "Iso" : Return meta.Iso.HasValue
                Case "Aperture" : Return meta.Aperture.HasValue
                Case "FocalLength" : Return meta.FocalLengthMm.HasValue
                Case "DateTaken" : Return Not String.IsNullOrWhiteSpace(meta.DateTaken)
                Case Else : Return True
            End Select
        End Function

        ''' <summary>Vergleicht den beim letzten EXIF-Scan festgehaltenen Dateisystem-Zeitstempel mit dem
        ''' aktuellen - True, wenn beide übereinstimmen (Katalogeintrag ist noch gültig).</summary>
        ''' <paramref name="scannedSidecarModifiedAt"/> und <paramref name="currentSidecar"/> tragen
        ''' dasselbe für die XMP-Beistelldatei. Sie muss getrennt geprüft werden: ein Fremdprogramm
        ''' ändert Bewertung oder Stichworte in "foto.cr2.xmp", ohne die Bilddatei anzufassen - ohne
        ''' diesen zweiten Vergleich bliebe der Eintrag "frisch" und der Import liefe nie an.
        Private Shared Function IsScannedSnapshotFresh(scannedSourceModifiedAt As String, currentModified As DateTime,
                                                       scannedSidecarModifiedAt As String, currentSidecar As String) As Boolean
            If String.IsNullOrWhiteSpace(scannedSourceModifiedAt) Then Return False
            Dim scanned As DateTime
            If Not DateTime.TryParse(scannedSourceModifiedAt, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind, scanned) Then Return False
            If scanned <> currentModified Then Return False
            Return String.Equals(If(scannedSidecarModifiedAt, ""), If(currentSidecar, ""), StringComparison.Ordinal)
        End Function

        ''' <summary>Snapshot-Vergleich: Der SQLite-Katalog invalidiert sich sonst nie automatisch bei
        ''' Dateiänderungen (weder der FileSystemWatcher noch MetaHasField erkennen das) - ohne diesen
        ''' Check würden nach dem ersten erfolgreichen Lesen dauerhaft veraltete EXIF-Werte geliefert.</summary>
        Private Shared Function IsMetaStale(meta As LibraryImageMeta) As Boolean
            Try
                Return Not IsScannedSnapshotFresh(meta.ScannedSourceModifiedAt, File.GetLastWriteTime(meta.FilePath),
                                                  meta.ScannedSidecarModifiedAt, LibraryService.SidecarStamp(meta.FilePath))
            Catch
                Return True
            End Try
        End Function

        ''' <summary>Übernimmt Bewertung, Farbetikett und Stichworte aus einer XMP-Beistelldatei (und die
        ''' Bewertung aus eingebettetem XMP) in die Bibliothek. Liefert zurück, was sich geändert hat,
        ''' damit der Aufrufer die Anzeige nachziehen kann - Nothing/leer heißt „nichts angefasst".
        '''
        ''' REGEL: Es wird nur gefüllt, was in FerrumPix leer ist. Eine selbst vergebene Bewertung oder
        ''' Markierung darf ein Scan niemals überschreiben - genau das tat der Import des eingebetteten
        ''' Ratings vorher bedingungslos, sodass eigene Bewertungen bei jedem erneuten Einlesen verloren
        ''' gingen. Stichworte sind der Sonderfall: dort ist Vereinigen richtig, nicht Ersetzen, weil
        ''' beide Seiten unabhängig voneinander sinnvolle Einträge haben können.</summary>
        Private Shared Function ImportSidecarCatalogData(filePath As String,
                                                        embeddedRating As Integer?,
                                                        currentRating As Integer,
                                                        currentColorLabel As String,
                                                        currentTags As List(Of String)) _
                                                        As (Rating As Integer?, Favorite As Boolean?, ColorLabel As String, HasColorLabel As Boolean, Tags As List(Of String))
            Dim result As (Rating As Integer?, Favorite As Boolean?, ColorLabel As String, HasColorLabel As Boolean, Tags As List(Of String)) =
                (Nothing, Nothing, "", False, Nothing)
            Try
                ' .fpxmp ist fuer RAW/PSD die primaere portable Katalogquelle. Vor dem XMP-Fallback
                ' exakt nach SQLite uebernehmen; explizite Leerwerte (0/False/keine Tags/kein Label)
                ' duerfen dabei nicht wieder von einer aelteren XMP-Sidecar aufgefuellt werden.
                Dim fpxmpCatalog = LibraryService.Instance.ImportFpxmpCatalogData(filePath)
                If fpxmpCatalog IsNot Nothing Then
                    If fpxmpCatalog.Rating.HasValue Then result.Rating = fpxmpCatalog.Rating
                    If fpxmpCatalog.IsFavorite.HasValue Then result.Favorite = fpxmpCatalog.IsFavorite
                    If fpxmpCatalog.ColorLabel IsNot Nothing Then
                        result.ColorLabel = fpxmpCatalog.ColorLabel
                        result.HasColorLabel = True
                    End If
                    If fpxmpCatalog.HasKeywords Then result.Tags = New List(Of String)(fpxmpCatalog.Keywords)
                End If

                Dim sidecar As XmpSidecarService.XmpSidecarData = Nothing
                Dim sidecarPath = XmpSidecarService.FindSidecar(filePath)
                If Not String.IsNullOrEmpty(sidecarPath) Then sidecar = XmpSidecarService.ReadSidecar(sidecarPath)

                ' Trägt dieselbe Sidecar auch Entwicklungseinstellungen (crs:), werden sie hier einmalig
                ' in eine .fpxmp übersetzt - der EINE Ort, an dem das passiert. Editor und Viewer kennen
                ' XMP dadurch gar nicht und arbeiten weiter allein auf dem eigenen Rezeptformat.
                RawSidecarService.TryImportFromXmpSidecar(filePath)

                ' Die Beistelldatei ist die speziellere Quelle und schlägt eingebettetes XMP.
                Dim ratingSource = If(fpxmpCatalog?.Rating.HasValue, CType(Nothing, Integer?), If(sidecar?.Rating, embeddedRating))
                If ratingSource.HasValue AndAlso ratingSource.Value > 0 AndAlso currentRating = 0 Then
                    LibraryService.Instance.SetRating(filePath, ratingSource.Value)
                    result.Rating = ratingSource.Value
                End If

                If sidecar IsNot Nothing Then
                    If (fpxmpCatalog Is Nothing OrElse fpxmpCatalog.ColorLabel Is Nothing) AndAlso
                       Not String.IsNullOrEmpty(sidecar.ColorLabel) AndAlso String.IsNullOrEmpty(currentColorLabel) Then
                        LibraryService.Instance.SetColorLabelForMany({filePath}, sidecar.ColorLabel)
                        result.ColorLabel = sidecar.ColorLabel
                        result.HasColorLabel = True
                    End If

                    If (fpxmpCatalog Is Nothing OrElse Not fpxmpCatalog.HasKeywords) AndAlso sidecar.Keywords.Count > 0 Then
                        Dim merged As New List(Of String)(If(currentTags, New List(Of String)()))
                        Dim added = False
                        For Each keyword In sidecar.Keywords
                            If Not merged.Any(Function(t) String.Equals(t, keyword, StringComparison.OrdinalIgnoreCase)) Then
                                merged.Add(keyword)
                                added = True
                            End If
                        Next
                        If added Then
                            LibraryService.Instance.SetTags(filePath, merged)
                            result.Tags = merged
                        End If
                    End If

                    ' Und die Gesichtsregionen: sie stehen in derselben Beistelldatei und tragen,
                    ' WER und WO. Ohne diesen Weg käme eine Zuordnung, die in einem anderen Programm
                    ' entstanden ist, nie an - und wer FerrumPix neu aufsetzt, finge bei null an,
                    ' obwohl die Namen neben den Fotos liegen.
                    If Not String.IsNullOrEmpty(sidecarPath) Then
                        LibraryService.Instance.ImportFaceRegionsFromXmp(filePath, sidecarPath)
                    End If
                End If
            Catch
            End Try
            Return result
        End Function

        Private Shared Sub ResolveMissingMetaFields(meta As LibraryImageMeta)
            ' Ein Immich-Asset liegt in keinem Dateisystem: sein Pfad ist eine Kennung, kein Ort.
            ' Alles, was es an Metadaten gibt, steht schon im Immich-Index (siehe
            ' BuildSearchMetaFromImmichAsset) - hier gaebe es nur eine Ausnahme je Bild und einen
            ' Katalogschreiber, der einen Pseudo-Pfad einträgt.
            If ImmichService.IsImmichPseudoPath(meta.FilePath) Then Return
            Try
                Dim data = ExifService.ReadExif(meta.FilePath)
                Dim fields = ExifService.ExtractSearchFields(data, meta.FilePath)
                Dim xmpRating = ExifService.GetXmpRating(data)
                Dim catalogSummary = ExifService.BuildCatalogSummary(data, fields)
                ' Erst importieren, dann den Schnappschuss schreiben - siehe QueueBackgroundMetaRefresh.
                Dim imported = ImportSidecarCatalogData(meta.FilePath, xmpRating, meta.Rating, meta.ColorLabel, meta.Tags)
                LibraryService.Instance.SyncExifData(meta.FilePath, fields, catalogSummary)
                If imported.Rating.HasValue Then meta.Rating = imported.Rating.Value
                If imported.Favorite.HasValue Then meta.IsFavorite = imported.Favorite.Value
                If imported.HasColorLabel Then meta.ColorLabel = imported.ColorLabel
                If imported.Tags IsNot Nothing Then meta.Tags = imported.Tags
                meta.DateTaken = fields.DateTaken
                meta.DateModifiedExif = fields.DateModifiedExif
                meta.Camera = fields.Camera
                meta.Lens = fields.Lens
                meta.Aperture = fields.Aperture
                meta.FocalLengthMm = fields.FocalLengthMm
                meta.Iso = fields.Iso
                meta.ShutterSpeed = fields.ShutterSpeed
                meta.GpsLatitude = fields.GpsLatitude
                meta.GpsLongitude = fields.GpsLongitude
                meta.ImageWidth = fields.ImageWidth
                meta.ImageHeight = fields.ImageHeight
                meta.ExifSummary = catalogSummary.ExifSummary
                meta.IptcSummary = catalogSummary.IptcSummary
                meta.XmpSummary = catalogSummary.XmpSummary
                meta.IccSummary = catalogSummary.IccSummary
                meta.SummaryFormat = catalogSummary.SummaryFormat
                meta.ScannedSourceModifiedAt = File.GetLastWriteTime(meta.FilePath).ToString("o")
            Catch
            End Try
        End Sub

        Private Shared Function EvaluateSingleCondition(meta As LibraryImageMeta, condition As SearchCondition) As Boolean
            Select Case condition.Field
                Case "Width"
                    Return CompareNumericCondition(If(meta.ImageWidth.HasValue, CDbl(meta.ImageWidth.Value), CType(Nothing, Double?)), condition.Operator, condition.Value)
                Case "Height"
                    Return CompareNumericCondition(If(meta.ImageHeight.HasValue, CDbl(meta.ImageHeight.Value), CType(Nothing, Double?)), condition.Operator, condition.Value)
                Case "Iso"
                    Return CompareNumericCondition(If(meta.Iso.HasValue, CDbl(meta.Iso.Value), CType(Nothing, Double?)), condition.Operator, condition.Value)
                Case "Aperture"
                    Return CompareNumericCondition(meta.Aperture, condition.Operator, condition.Value)
                Case "FocalLength"
                    Return CompareNumericCondition(meta.FocalLengthMm, condition.Operator, condition.Value)
                Case "Camera"
                    Return CompareTextCondition(meta.Camera, condition.Operator, condition.Value)
                Case "DateTaken"
                    Return CompareTextCondition(meta.DateTaken, condition.Operator, condition.Value)
                Case "Place"
                    ' Ort und Land gelten beide: wer "Borkum" sucht, meint den Ort, wer "Germany"
                    ' sucht, das Land - eine getrennte Bedingung je Ebene waere nur Ballast.
                    Return CompareTextCondition(meta.City, condition.Operator, condition.Value) OrElse
                           CompareTextCondition(meta.Country, condition.Operator, condition.Value)
                Case "Person"
                    Return MatchesPersonCondition(meta.FilePath, condition)
                Case Else
                    Return True
            End Select
        End Function

        ''' <summary>Personen liegen NICHT in der Bildzeile, sondern in eigenen Tabellen - ein Bild
        ''' traegt mehrere. Verglichen wird deshalb gegen die Namen, die zu diesem Pfad gehoeren.
        '''
        ''' Die Zuordnung wird je Durchlauf EINMAL geholt und gemerkt: ohne das faellt je Bild eine
        ''' eigene Datenbankabfrage an, und bei einem Bestand mit zehntausenden Fotos steht die
        ''' Oberflaeche. Der Zwischenspeicher gilt fuer einen Durchlauf, nicht laenger - danach kann
        ''' eine Benennung ihn ueberholt haben.</summary>
        Private Shared _personNamesByPath As Dictionary(Of String, List(Of String))

        Private Shared Function MatchesPersonCondition(filePath As String, condition As SearchCondition) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Try
                If _personNamesByPath Is Nothing Then
                    _personNamesByPath = LibraryService.Instance.GetPersonNamesByPath()
                End If
                Dim names As List(Of String) = Nothing
                If Not _personNamesByPath.TryGetValue(filePath, names) OrElse names Is Nothing Then Return False
                Return names.Any(Function(n) CompareTextCondition(n, condition.Operator, condition.Value))
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.MatchesPersonCondition", ex)
                Return False
            End Try
        End Function

        ''' <summary>Vor jedem Durchlauf leeren, damit frisch benannte Personen sofort greifen.</summary>
        Private Shared Sub ResetPersonNameCache()
            _personNamesByPath = Nothing
        End Sub

        ''' <summary>Ein Immich-Asset als Suchzeile, damit eine Suchliste dieselben Bedingungen
        ''' darauf anwenden kann wie auf ein lokales Bild.
        '''
        ''' Gesucht wird über den LOKALEN Index, nicht über den Server. Das ist der Punkt: die
        ''' Asset-Liste liegt ohnehin auf der Platte, eine Suche darüber kostet keine einzige
        ''' Anfrage - eine Suche, die einen Server mit zehntausenden Fotos Bild für Bild befragt,
        ''' wäre etwas ganz anderes und ist ausdrücklich nicht gemeint.
        '''
        ''' Die Liste trägt nur die Grunddaten; Kamera, ISO, Blende, Bewertung und Stichwörter
        ''' stehen im Metadaten-Zwischenspeicher und werden übernommen, wo er zu diesem Stand des
        ''' Assets passt. Fehlen sie, bleibt das Feld leer - nachgeladen wird NICHT, das wäre je
        ''' Bild eine Serveranfrage mitten im Suchlauf.</summary>
        Private Shared Function BuildSearchMetaFromImmichAsset(serverKey As String, asset As ImmichAsset) As LibraryImageMeta
            If asset Is Nothing OrElse String.IsNullOrEmpty(asset.Id) Then Return Nothing
            Dim detail = ImmichIndexService.Instance.TryGet(serverKey, asset.Id, asset.UpdatedAt)
            Dim taken = If(asset.ExifDateTaken, asset.FileCreatedAt)
            If detail IsNot Nothing Then taken = If(detail.ExifDateTaken, taken)
            Return New LibraryImageMeta With {
                .FilePath = ImmichService.MakePseudoPath(asset.Id, asset.FileName),
                .IsFavorite = If(detail IsNot Nothing, detail.IsFavorite, asset.IsFavorite),
                .Rating = If(detail IsNot Nothing, detail.Rating, asset.Rating),
                .Tags = If(detail IsNot Nothing AndAlso detail.Tags IsNot Nothing, detail.Tags, New List(Of String)()),
                .DateTaken = If(taken.HasValue, taken.Value.ToString("yyyy-MM-dd HH:mm:ss"), ""),
                .Camera = If(detail IsNot Nothing, detail.Camera, ""),
                .Iso = If(detail IsNot Nothing, detail.Iso, Nothing),
                .Aperture = If(detail IsNot Nothing, detail.Aperture, Nothing),
                .ImageWidth = If(asset.Width > 0, asset.Width, If(detail Is Nothing, 0, detail.Width)),
                .ImageHeight = If(asset.Height > 0, asset.Height, If(detail Is Nothing, 0, detail.Height)),
                .City = If(Not String.IsNullOrWhiteSpace(asset.City), asset.City, If(detail Is Nothing, "", detail.City)),
                .Country = If(Not String.IsNullOrWhiteSpace(asset.Country), asset.Country, If(detail Is Nothing, "", detail.Country))
            }
        End Function

        Private Shared Function CompareNumericCondition(actual As Double?, op As String, valueText As String) As Boolean
            If Not actual.HasValue Then Return False
            Dim target As Double
            If Not Double.TryParse(If(valueText, "").Replace(","c, "."c), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, target) Then Return False
            Select Case op
                Case ">" : Return actual.Value > target
                Case "<" : Return actual.Value < target
                Case ">=" : Return actual.Value >= target
                Case "<=" : Return actual.Value <= target
                Case "=" : Return Math.Abs(actual.Value - target) < 0.0001
                Case "Contains" : Return actual.Value.ToString(Globalization.CultureInfo.InvariantCulture).Contains(valueText, StringComparison.OrdinalIgnoreCase)
                Case Else : Return False
            End Select
        End Function

        Private Shared Function CompareTextCondition(actual As String, op As String, valueText As String) As Boolean
            If String.IsNullOrWhiteSpace(actual) Then Return False
            Select Case op
                Case "Contains" : Return actual.Contains(valueText, StringComparison.OrdinalIgnoreCase)
                Case "=" : Return String.Equals(actual, valueText, StringComparison.OrdinalIgnoreCase)
                Case ">" : Return String.Compare(actual, valueText, StringComparison.OrdinalIgnoreCase) > 0
                Case "<" : Return String.Compare(actual, valueText, StringComparison.OrdinalIgnoreCase) < 0
                Case ">=" : Return String.Compare(actual, valueText, StringComparison.OrdinalIgnoreCase) >= 0
                Case "<=" : Return String.Compare(actual, valueText, StringComparison.OrdinalIgnoreCase) <= 0
                Case Else : Return False
            End Select
        End Function

        Private Iterator Function EnumerateSearchFilesLazy(rootFolder As String, includeSubfolders As Boolean, textQuery As String, token As CancellationToken) As IEnumerable(Of String)
            If String.IsNullOrWhiteSpace(rootFolder) OrElse Not Directory.Exists(rootFolder) Then Return
            ' Der Startordner SELBST, nicht nur die Unterordner: gefiltert wurde bisher erst beim
            ' Absteigen, ein Startpunkt im Papierkorb lieferte seinen ganzen Inhalt aus.
            If FileOperationPolicy.IsTrashFolder(rootFolder) Then Return
            Dim filePatterns = GetFileEnumerationPatterns(textQuery)
            Dim pendingFolders As New Stack(Of String)()
            pendingFolders.Push(rootFolder)

            While pendingFolders.Count > 0
                token.ThrowIfCancellationRequested()
                Dim folder = pendingFolders.Pop()

                If filePatterns.Count = 0 Then
                    Dim files As IEnumerable(Of String) = Enumerable.Empty(Of String)()
                    Try
                        files = Directory.EnumerateFiles(folder)
                    Catch ex As UnauthorizedAccessException
                    Catch ex As IOException
                    End Try

                    For Each file In files
                        token.ThrowIfCancellationRequested()
                        If _imageExtensions.Contains(IO.Path.GetExtension(file).ToLowerInvariant()) Then Yield file
                    Next
                Else
                    For Each pattern In filePatterns
                        Dim files As IEnumerable(Of String) = Enumerable.Empty(Of String)()
                        Try
                            files = Directory.EnumerateFiles(folder, pattern)
                        Catch ex As UnauthorizedAccessException
                        Catch ex As IOException
                        End Try

                        For Each file In files
                            token.ThrowIfCancellationRequested()
                            If _imageExtensions.Contains(IO.Path.GetExtension(file).ToLowerInvariant()) Then Yield file
                        Next
                    Next
                End If

                If includeSubfolders Then
                    Dim children As IEnumerable(Of String) = Enumerable.Empty(Of String)()
                    Try
                        children = Directory.EnumerateDirectories(folder)
                    Catch ex As UnauthorizedAccessException
                    Catch ex As IOException
                    End Try
                    For Each child In children
                        ' VERSTECKTE ORDNER wie im Ordnerbaum und in der Ordneransicht: sie zeigt
                        ' nur, wer sie eingeschaltet hat. Der Suchlauf war die einzige Stelle ohne
                        ' diese Bedingung - und stieg damit in den Systempapierkorb (".Trash-1000").
                        ' Geloeschte Bilder standen in der Trefferliste, liessen sich aber nicht
                        ' loeschen: die Regel weist versteckte Pfade ab, und es passierte wortlos
                        ' nichts (Nutzerbefund 2026-08-10).
                        ' DER PAPIERKORB IMMER NICHT - auch mit eingeschalteten versteckten
                        ' Ordnern. Was dort liegt, ist weggeworfen.
                        If FileOperationPolicy.IsTrashFolder(child) Then Continue For
                        If Not FolderNode.ShowHiddenFolders AndAlso
                           IO.Path.GetFileName(child).StartsWith(".", StringComparison.Ordinal) Then Continue For
                        pendingFolders.Push(child)
                    Next
                End If
            End While
        End Function

        Private Function GetFileEnumerationPatterns(textQuery As String) As List(Of String)
            textQuery = If(textQuery, "").Trim()
            If textQuery.IndexOf(IO.Path.DirectorySeparatorChar) >= 0 OrElse
               textQuery.IndexOf(IO.Path.AltDirectorySeparatorChar) >= 0 Then
                Return New List(Of String)()
            End If

            If textQuery.IndexOf("*"c) >= 0 OrElse textQuery.IndexOf("?"c) >= 0 Then
                Return GetImageWildcardEnumerationPatterns(textQuery)
            End If

            Return New List(Of String)()
        End Function

        Private Function GetImageWildcardEnumerationPatterns(pattern As String) As List(Of String)
            Dim extension = IO.Path.GetExtension(pattern)
            If String.IsNullOrWhiteSpace(extension) Then Return New List(Of String)()
            If extension.IndexOf("*"c) >= 0 OrElse extension.IndexOf("?"c) >= 0 Then Return New List(Of String)()
            If Not _imageExtensions.Contains(extension.ToLowerInvariant()) Then Return New List(Of String)()

            Dim prefix = pattern.Substring(0, pattern.Length - extension.Length)
            Return GetExtensionCaseVariants(extension).
                Select(Function(ext) prefix & ext).
                Distinct(StringComparer.Ordinal).
                ToList()
        End Function

        Private Shared Function GetExtensionCaseVariants(extension As String) As IEnumerable(Of String)
            If String.IsNullOrEmpty(extension) Then Return Enumerable.Empty(Of String)()
            Dim variants As New List(Of String) From {""}
            For Each ch In extension
                Dim nextVariants As New List(Of String)()
                If Char.IsLetter(ch) Then
                    For Each existing In variants
                        nextVariants.Add(existing & Char.ToLowerInvariant(ch))
                        nextVariants.Add(existing & Char.ToUpperInvariant(ch))
                    Next
                Else
                    For Each existing In variants
                        nextVariants.Add(existing & ch)
                    Next
                End If
                variants = nextVariants
            Next
            Return variants
        End Function

        ''' Unterstützt AND/OR-Verknüpfung mehrerer Suchbegriffe: " OR "/" ODER " (Groß-/Kleinschreibung
        ''' egal) trennt Begriffs-GRUPPEN (mind. eine muss zutreffen), innerhalb einer Gruppe müssen
        ''' alle durch Leerzeichen getrennten Begriffe zutreffen (AND) - Anführungszeichen erlauben
        ''' Begriffe mit Leerzeichen. Wildcard (*/?) funktioniert weiterhin pro Einzelbegriff.
        ''' <summary>Traegt das Bild mindestens eines dieser Stichwoerter? Leere Liste heisst: keine
        ''' Einschraenkung. Verglichen wird das ganze Stichwort, nicht ein Teil davon - "urlaub"
        ''' soll nicht auch "urlaub2019" mitbringen.</summary>
        Private Shared Function MatchesTagQuery(tags As IEnumerable(Of String), tagQueries As IList(Of String)) As Boolean
            If tagQueries Is Nothing OrElse tagQueries.Count = 0 Then Return True
            If tags Is Nothing Then Return False
            Dim own = tags.Where(Function(t) Not String.IsNullOrWhiteSpace(t)).
                           Select(Function(t) t.Trim()).ToList()
            Return tagQueries.Any(Function(wanted) own.Any(
                Function(t) String.Equals(t, wanted.Trim(), StringComparison.OrdinalIgnoreCase)))
        End Function

        ''' <summary>Stehen ALLE diese Personen auf dem Bild? Mehrere wirken als UND - wer zwei Namen
        ''' anklickt, sucht die beiden gemeinsam.
        '''
        ''' Die Zuordnung kommt aus demselben Zwischenspeicher wie die Suchbedingung: sie liegt nicht
        ''' in der Bildzeile, weil ein Bild mehrere Personen traegt, und eine eigene Abfrage je Bild
        ''' liess bei grossen Bestaenden die Oberflaeche stehen.</summary>
        Private Shared Function MatchesPersonQuery(filePath As String, wanted As IList(Of String)) As Boolean
            If wanted Is Nothing OrElse wanted.Count = 0 Then Return True
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Try
                If _personNamesByPath Is Nothing Then
                    _personNamesByPath = LibraryService.Instance.GetPersonNamesByPath()
                End If
                Dim names As List(Of String) = Nothing
                If Not _personNamesByPath.TryGetValue(filePath, names) OrElse names Is Nothing Then Return False
                Return wanted.All(Function(w) names.Any(
                    Function(n) String.Equals(n, w, StringComparison.OrdinalIgnoreCase)))
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.MatchesPersonQuery", ex)
                Return False
            End Try
        End Function

        ''' <summary>Kommt das Bild von einem dieser Orte? Mehrere wirken als ODER, denn ein Bild hat
        ''' genau einen Aufnahmeort. Verglichen wird gegen Ort UND Land - wer "Germany" waehlt, meint
        ''' das Land.</summary>
        Private Shared Function MatchesPlaceQuery(meta As LibraryImageMeta, wanted As IList(Of String)) As Boolean
            If wanted Is Nothing OrElse wanted.Count = 0 Then Return True
            If meta Is Nothing Then Return False
            Return wanted.Any(Function(w) String.Equals(meta.City, w, StringComparison.OrdinalIgnoreCase) OrElse
                                          String.Equals(meta.Country, w, StringComparison.OrdinalIgnoreCase))
        End Function

        Private Shared Function MatchesSavedSearchText(filePath As String, tags As IEnumerable(Of String), textQuery As String) As Boolean
            If String.IsNullOrWhiteSpace(textQuery) Then Return True
            Dim fileName = IO.Path.GetFileName(filePath)
            Dim haystack = fileName & " " & String.Join(" ", If(tags, Enumerable.Empty(Of String)()))

            Dim groups = Regex.Split(textQuery.Trim(), "\s+(?:OR|ODER)\s+", RegexOptions.IgnoreCase)
            For Each group In groups
                If MatchesAllSearchTerms(group, fileName, haystack) Then Return True
            Next
            Return False
        End Function

        Private Shared Function MatchesAllSearchTerms(group As String, fileName As String, haystack As String) As Boolean
            Dim terms = Regex.Matches(group.Trim(), """[^""]*""|\S+").
                Cast(Of Match)().
                Select(Function(m) m.Value.Trim(""""c)).
                Where(Function(t) Not String.IsNullOrWhiteSpace(t)).
                ToList()
            If terms.Count = 0 Then Return True

            For Each term In terms
                If Not MatchesSingleSearchTerm(term, fileName, haystack) Then Return False
            Next
            Return True
        End Function

        Private Shared Function MatchesSingleSearchTerm(term As String, fileName As String, haystack As String) As Boolean
            If term.IndexOf("*"c) >= 0 OrElse term.IndexOf("?"c) >= 0 Then
                Dim pattern = "^" & Regex.Escape(term).
                    Replace("\*", ".*").
                    Replace("\?", ".") & "$"
                Return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase Or RegexOptions.CultureInvariant)
            End If
            Return haystack.Contains(term, StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function NormalizeRatings(ratings As IEnumerable(Of Integer)) As HashSet(Of Integer)
            Return New HashSet(Of Integer)(If(ratings, Enumerable.Empty(Of Integer)()).
                Select(Function(r) Math.Max(0, Math.Min(5, r))))
        End Function

        Private Sub SaveSearches()
            SearchListService.Save(_savedSearches)
        End Sub

        ''' Die Schreibvorgaenge der Suchlisten laufen NACHEINANDER. Waehrend einer Suche kommen die
        ''' Treffer schubweise, und jeder Schub stiess bisher einen eigenen Hintergrundlauf an. Zwei
        ''' davon haben ohne diese Kette keine Reihenfolge: der aeltere kann nach dem juengeren
        ''' fertig werden und dessen Treffer wieder wegschreiben.
        Private _searchListSaveChain As Task = Task.CompletedTask
        Private ReadOnly _searchListSaveLock As New Object()

        ''' <summary>Schreibt eine MOMENTAUFNAHME der Suchlisten im Hintergrund weg.
        '''
        ''' Die Aufnahme muss der Aufrufer machen, und zwar tief (SearchListService.Normalize baut
        ''' neue Eintraege samt neuer Trefferliste). Eine flache Kopie teilt die Trefferlisten mit
        ''' der Oberflaeche, die waehrend der Suche weiter anhaengt - der Serialisierer laeuft dann
        ''' ueber eine Liste, die sich unter ihm aendert, und wirft.</summary>
        Private Sub QueueSearchListSave(snapshot As List(Of SearchListEntry))
            SyncLock _searchListSaveLock
                _searchListSaveChain = _searchListSaveChain.ContinueWith(
                    Sub()
                        Try
                            SearchListService.Save(snapshot)
                        Catch ex As Exception
                            ' Ein verworfener Task verschluckt seine Ausnahme sonst spurlos.
                            DiagnosticLogService.LogException("Gallery.SearchListSave", ex)
                        End Try
                    End Sub, TaskScheduler.Default)
            End SyncLock
        End Sub

        ''' <summary>Leert die Ansicht fuer einen virtuellen Ordner (Suchliste, Immich-Album, ...). Bricht
        ''' dabei einen noch laufenden Suchlauf ab: JEDER Ansichtswechsel geht hier durch, deshalb ist das
        ''' die Stelle, an der eine Suche stirbt - sonst schuettet ein Wechsel im Baum (Album, Person, Ort)
        ''' die weiterlaufenden Treffer in die neue Ansicht. Die Starter legen ihren Abbruch-Token deshalb
        ''' erst NACH diesem Aufruf an.</summary>
        Private Function StartEmptyVirtualFolder(name As String) As CancellationToken
            CancelActiveSearch()
            Dim thumbnailToken = BeginNewFolderThumbnailScope()
            ClearSelection()
            ' Ein hinterlegter Auswahlwunsch gehoert zur verlassenen Ansicht und darf in der
            ' naechsten nicht nachtraeglich zuschlagen.
            _pendingSelectionPaths = Nothing
            _allItems.Clear()
            Items.Clear()
            DisplayItems.Clear()
            _virtualPathSet.Clear()
            SetupWatcher(Nothing)

            _isVirtualFolder = True
            ' Jede neue Ansicht faengt als gewoehnliche an; die beiden Papierkorb-Wege setzen das
            ' Kennzeichen gleich danach selbst. So kann es nirgends stehenbleiben.
            SetTrashView(False)
            _virtualFolderName = If(String.IsNullOrWhiteSpace(name), "Virtueller Ordner", name)
            CurrentFolder = "virtual://" & _virtualFolderName
            _historyBack.Clear()
            _historyForward.Clear()
            StorageFreeText = ""
            StorageFillPercent = 0
            SelectedFolderNode = Nothing

            FilterAndSort()
            Me.RaisePropertyChanged(NameOf(IsVirtualFolder))
            Me.RaisePropertyChanged(NameOf(CurrentFolderName))
            Me.RaisePropertyChanged(NameOf(BreadcrumbParent))
            Me.RaisePropertyChanged(NameOf(HasBreadcrumbParent))
            Me.RaisePropertyChanged(NameOf(CanNavigateBack))
            Me.RaisePropertyChanged(NameOf(CanNavigateForward))
            Return thumbnailToken
        End Function

        ''' <summary>Ein weggeworfenes Bild - gehoert in keine Trefferliste.
        '''
        ''' Geprueft wird an den beiden Stellen, an denen ein Pfad in eine SUCHANSICHT eintritt, und
        ''' nicht in den einzelnen Suchlaeufen: die Wege dorthin sind vier (Ordnerdurchlauf,
        ''' Katalogdurchlauf, gemerkte Treffer einer Suchliste, Immich-Index), und die Regel an jedem
        ''' einzeln haette bei der naechsten Quelle wieder gefehlt. Genau so ist es gekommen - der
        ''' Ordner- und der Katalogdurchlauf hatten den Riegel bereits, die gemerkten Treffer einer
        ''' Suchliste nicht, und weggeworfene Bilder standen beim Oeffnen wieder da (Nutzerbefund).
        '''
        ''' SERVERBILDER BLEIBEN AUSSEN VOR: ihr Pseudo-Pfad ist kein Ordnerpfad, und die Ansichten
        ''' "Papierkorb" von Immich und Nextcloud sollen ihren Inhalt gerade zeigen.</summary>
        Private Shared Function IsTrashedLocalPath(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            If LibraryService.IsServerPseudoPath(filePath) Then Return False
            Return FileOperationPolicy.IsTrashFolder(filePath)
        End Function

        Private Sub AddMetasToVirtualFolder(metas As IEnumerable(Of LibraryImageMeta),
                                            thumbnailToken As CancellationToken,
                                            Optional cacheScopeId As String = Nothing,
                                            Optional cacheScopeName As String = Nothing)
            ' Ein Nachzuegler aus einem abgebrochenen Suchlauf darf niemals in eine echte Ordner-
            ' ansicht rieseln - geprueft wird am ZIEL, nicht am Abbruch-Token des Aufrufers.
            If Not _isVirtualFolder Then Return
            Dim added = False
            For Each meta In If(metas, Enumerable.Empty(Of LibraryImageMeta)())
                If meta Is Nothing OrElse String.IsNullOrWhiteSpace(meta.FilePath) Then Continue For
                ' Vor File.Exists: Zeichenkettenarbeit ist billiger als ein Plattenzugriff.
                If IsTrashedLocalPath(meta.FilePath) Then Continue For
                If Not File.Exists(meta.FilePath) Then Continue For
                If Not _imageExtensions.Contains(IO.Path.GetExtension(meta.FilePath).ToLowerInvariant()) Then Continue For
                If Not _virtualPathSet.Add(meta.FilePath) Then Continue For

                Dim neu = New ImageItem(meta.FilePath, thumbnailToken, cacheScopeId, cacheScopeName) With {
                    .IsFavorite = meta.IsFavorite,
                    .Rating = meta.Rating,
                    .ColorLabel = meta.ColorLabel,
                    .Tags = If(meta.Tags, New List(Of String)()),
                    .ImageWidth = If(meta.ImageWidth, 0),
                    .ImageHeight = If(meta.ImageHeight, 0)
                }
                ' Siehe den anderen Weg oben: ohne die Dateidaten aus dem Katalog kennt weder die
                ' Zeitleiste noch die Sortierung ein Datum.
                neu.ApplyCatalogFileDates(meta.FileCreatedAt, meta.ScannedSourceModifiedAt)
                _allItems.Add(neu)
                added = True
            Next
            If added Then FilterAndSort()
        End Sub

        ''' Nimmt fertig gebaute Elemente - im Hintergrund erzeugt - in den virtuellen Ordner auf,
        ''' ohne einen einzigen Dateizugriff. Hier wird nur auf Dubletten geprueft und die Liste
        ''' geaendert.
        Private Sub AddPrebuiltItemsToVirtualFolder(items As List(Of ImageItem), Optional sortNow As Boolean = True)
            ' Siehe AddMetasToVirtualFolder: Zielpruefung, kein Vertrauen auf den Aufrufer.
            If Not _isVirtualFolder Then Return
            ApplyLocalColorLabelsToImmichItems(items)
            Dim added = False
            For Each item In If(items, New List(Of ImageItem)())
                If item Is Nothing Then Continue For
                If IsTrashedLocalPath(item.FilePath) Then Continue For
                If Not _virtualPathSet.Add(item.FilePath) Then Continue For
                _allItems.Add(item)
                added = True
            Next
            If added AndAlso sortNow Then FilterAndSort()
        End Sub

        ''' Farbetiketten sind rein lokal (Bibliotheks-DB) - Immich-Items kommen vom Server bzw. aus
        ''' dem Katalog und tragen sie deshalb nicht. Hier werden sie unter dem Pseudo-Pfad nachgeladen;
        ''' die Gesamtmenge der vergebenen Etiketten ist klein, daher EIN Abruf statt Pfad-Abfragen.
        Private Sub ApplyLocalColorLabelsToImmichItems(items As List(Of ImageItem))
            If items Is Nothing OrElse items.Count = 0 Then Return
            If Not items.Any(Function(i) i IsNot Nothing AndAlso ImmichService.IsImmichPseudoPath(i.FilePath)) Then Return
            Try
                Dim labels = LibraryService.Instance.GetAllColorLabels()
                If labels.Count = 0 Then Return
                For Each item In items
                    If item Is Nothing OrElse Not ImmichService.IsImmichPseudoPath(item.FilePath) Then Continue For
                    Dim label As String = Nothing
                    If labels.TryGetValue(item.FilePath, label) Then item.ColorLabel = label
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.ImmichColorLabels", ex)
            End Try
        End Sub

        Private Sub AppendSearchListResults(node As VirtualNavigationNode, paths As IEnumerable(Of String))
            If node Is Nothing OrElse paths Is Nothing Then Return
            Dim target = _savedSearches.FirstOrDefault(Function(s) String.Equals(s.Id, node.Id, StringComparison.OrdinalIgnoreCase))
            If target Is Nothing Then Return
            If target.Results Is Nothing Then target.Results = New List(Of String)()
            Dim changed = False
            For Each path In paths
                If String.IsNullOrWhiteSpace(path) Then Continue For
                If target.Results.Any(Function(p) String.Equals(p, path, StringComparison.OrdinalIgnoreCase)) Then Continue For
                target.Results.Add(path)
                changed = True
            Next
            If changed Then
                node.Results = target.Results.ToList()
                ' Abseits des UI-Threads schreiben, damit der Plattenzugriff die Oberflaeche
                ' waehrend der Suche nicht anhaelt. Die Momentaufnahme entsteht dafuer HIER, auf
                ' diesem Faden, und TIEF: _savedSearches.ToList() allein kopiert nur die aeussere
                ' Liste, die Trefferlisten darin blieben dieselben Objekte, an die der naechste
                ' Fund gleich wieder anhaengt.
                QueueSearchListSave(SearchListService.Normalize(_savedSearches.ToList()))
            End If
        End Sub

        Private Sub CleanupSearchListResults(node As VirtualNavigationNode, Optional currentRunResults As IEnumerable(Of String) = Nothing)
            If node Is Nothing Then Return
            Dim target = _savedSearches.FirstOrDefault(Function(s) String.Equals(s.Id, node.Id, StringComparison.OrdinalIgnoreCase))
            If target Is Nothing OrElse target.Results Is Nothing Then Return
            Dim source = If(currentRunResults, target.Results)
            ' Der Papierkorb faellt hier ENDGUELTIG heraus und nicht nur aus der Anzeige: die
            ' gemerkten Treffer stehen auf der Platte (searchlists.json), und was dort bleibt, kommt
            ' bei jedem Oeffnen wieder. File.Exists allein raeumt es nicht weg - ein weggeworfenes
            ' Bild ist ja noch da.
            Dim cleaned = source.
                Where(Function(p) Not String.IsNullOrWhiteSpace(p) AndAlso
                                  Not IsTrashedLocalPath(p) AndAlso File.Exists(p)).
                Distinct(PathIdentity.Comparer).
                ToList()
            Dim changed = cleaned.Count <> target.Results.Count
            If Not changed Then
                For i = 0 To cleaned.Count - 1
                    If Not String.Equals(cleaned(i), target.Results(i), StringComparison.OrdinalIgnoreCase) Then
                        changed = True
                        Exit For
                    End If
                Next
            End If
            If changed Then
                target.Results = cleaned
                node.Results = cleaned.ToList()
                ' Die Ansicht nur aufraeumen, solange sie diesem Suchlauf gehoert - sonst wuerde das
                ' Aufraeumen die Bilder eines inzwischen geoeffneten Ordners wegwerfen.
                If currentRunResults IsNot Nothing AndAlso _isVirtualFolder Then
                    Dim keep = cleaned.ToHashSet(PathIdentity.Comparer)
                    ' Immich-Treffer sind absichtlich nicht in Results: ein Pseudo-Pfad besteht
                    ' nicht im Dateisystem und wuerde beim naechsten Oeffnen als "verwaist"
                    ' entfernt. Die Bereinigung darf deshalb ausschliesslich die persistenten,
                    ' lokalen Treffer ausraeumen. Vorher verschwand mit jeder bereinigten lokalen
                    ' Datei die gesamte Immich-Teilmenge der gerade angezeigten Suche.
                    _allItems.RemoveAll(Function(i) i IsNot Nothing AndAlso i.IsImage AndAlso
                                         Not ImmichService.IsImmichPseudoPath(i.FilePath) AndAlso
                                         Not keep.Contains(i.FilePath))
                    FilterAndSort()
                End If
                SaveSearches()
            End If
        End Sub


        ''' <summary>Darf dieser Suchlauf noch in die Ansicht schreiben? Nur wenn er nicht abgebrochen ist,
        ''' die Ansicht ueberhaupt ein virtueller Ordner ist UND genau diese Suchliste offen steht. Der
        ''' Abbruch-Token allein reicht nicht: zwischen dem Abbruch und dem naechsten Ausfuehren einer
        ''' bereits eingereihten UI-Rueckmeldung liegt eine Luecke, und ein Wechsel im Baum (Ordner,
        ''' Album, andere Suchliste) darf die Treffer nicht in die neue Ansicht kippen.</summary>
        Private Function SearchMayPublish(node As VirtualNavigationNode, searchToken As CancellationToken) As Boolean
            If searchToken.IsCancellationRequested Then Return False
            If Not _isVirtualFolder Then Return False
            Return node IsNot Nothing AndAlso Object.ReferenceEquals(_selectedSearchNode, node)
        End Function

        Private Sub CancelActiveSearch()
            ' Die Anzeige zuerst: der Suchlauf merkt den Abbruch erst an seiner naechsten Pruefstelle,
            ' bei einer Serverabfrage kann das eine Sekunde dauern. Solange bliebe der Balken stehen,
            ' obwohl der Knopf schon gedrueckt wurde.
            EndSearchRun(_searchRunRow)
            If _activeSearchCts Is Nothing Then Return
            Try
                _activeSearchCts.Cancel()
            Catch
            End Try
            _activeSearchCts.Dispose()
            _activeSearchCts = Nothing
            IsLoading = False
        End Sub

        ''' <summary>Die Zeile des laufenden Suchlaufs in der Werkzeugleiste. Nothing, wenn gerade
        ''' keine Suchliste laeuft.</summary>
        Private _searchRunRow As BackgroundRunRow

        ''' <summary>Zeigt den Suchlauf oben an, mit Balken und Abbruchknopf. Eine Suchliste ueber
        ''' einen grossen Bestand laeuft Minuten - die Statuszeile allein ist dafuer zu leise, und
        ''' ohne Knopf bliebe nur, den Baum zu wechseln.</summary>
        Private Function BeginSearchRun() As BackgroundRunRow
            Dim row = BeginGalleryRun(Sub() CancelActiveSearch())
            ' Unbestimmter Balken: wie viele Bilder zu pruefen sind, weiss der Lauf erst, wenn er
            ' durch ist.
            row.Text = StatusText
            _searchRunRow = row
            Return row
        End Function

        Private Sub EndSearchRun(row As BackgroundRunRow)
            If row Is Nothing Then Return
            If Object.ReferenceEquals(_searchRunRow, row) Then _searchRunRow = Nothing
            EndGalleryRun(row)
        End Sub

        Private Sub ClearVirtualFolderState()
            CancelActiveSearch()
            If Not _isVirtualFolder Then Return
            _isVirtualFolder = False
            SetTrashView(False)
            _virtualFolderName = ""
            _virtualPathSet.Clear()
            Me.RaisePropertyChanged(NameOf(IsVirtualFolder))
            Me.RaisePropertyChanged(NameOf(CurrentFolderName))
            Me.RaisePropertyChanged(NameOf(BreadcrumbParent))
            Me.RaisePropertyChanged(NameOf(HasBreadcrumbParent))
        End Sub

        ' Anzeigbare Medien. Die Liste steht in MediaFileTypes, weil der Katalogindex dieselben
        ' Ordner durchgeht und dieselbe Definition braucht.
        Private ReadOnly _imageExtensions As String() = MediaFileTypes.Displayable

        ''' <summary>Der Katalogindex. Die Fusszeile zeigt seinen Fortschritt und laesst ihn
        ''' anhalten; der Zustand gehoert dem Fenster, damit Einstellungen und Galerie dasselbe
        ''' sehen.</summary>
        Public ReadOnly Property CatalogIndex As CatalogIndexViewModel
            Get
                Return _mainVm?.CatalogIndex
            End Get
        End Property

        ''' <summary>Die Gesichtssuche ueber die ueberwachten Ordner. Sie laeuft Stunden und muss
        ''' deshalb auch dann sichtbar sein, wenn die Einstellungen zu sind.</summary>
        Public ReadOnly Property FaceIndex As FaceIndexViewModel
            Get
                Return _mainVm?.FaceIndex
            End Get
        End Property

        ' --- Laufende Vorgaenge in der Werkzeugleiste -------------------------------------------
        '
        ' EINE Stelle fuer ALLE Laeufe, an der Stelle des Suchfelds: der Katalogindex, die
        ' Gesichtssuche ueber die ueberwachten Ordner, die Gesichtssuche ueber die angezeigten
        ' Bilder und eine laufende Suchliste. Vorher zeigten die ersten beiden hier und die dritte
        ' unten in der Fusszeile, die Suchliste gar nicht - drei Orte fuer dieselbe Aussage, und der
        ' leiseste davon war ausgerechnet der fuer den laengsten Lauf.
        '
        ' Laufen mehrere gleichzeitig, stehen sie NEBENEINANDER und teilen sich die Breite; keiner
        ' verdeckt den anderen, und jeder behaelt seinen eigenen Knopf zum Anhalten. Die Anzeige
        ' selbst ist eine Liste von <see cref="BackgroundRunRow"/> - jede Zeile weiss nur, was sie
        ' zeigt, nicht wer sie fuellt.

        Private ReadOnly _backgroundRuns As New ObservableCollection(Of BackgroundRunRow)()

        ''' <summary>Die Zeilen der Anzeige, in der Reihenfolge ihres Starts.</summary>
        Public ReadOnly Property BackgroundRuns As ObservableCollection(Of BackgroundRunRow)
            Get
                Return _backgroundRuns
            End Get
        End Property

        ''' <summary>Laeuft ueberhaupt etwas? Traegt die Anzeige und blendet solange das Suchfeld
        ''' aus.</summary>
        Public ReadOnly Property HasBackgroundRuns As Boolean
            Get
                Return _backgroundRuns.Count > 0
            End Get
        End Property

        ''' <summary>Die Zeile zu einem der beiden Hintergrundlaeufe, solange er laeuft.</summary>
        Private ReadOnly _backgroundRunRows As New Dictionary(Of BackgroundRunViewModel, BackgroundRunRow)()

        Private Sub AddBackgroundRun(row As BackgroundRunRow)
            If row Is Nothing OrElse _backgroundRuns.Contains(row) Then Return
            _backgroundRuns.Add(row)
            Me.RaisePropertyChanged(NameOf(HasBackgroundRuns))
        End Sub

        Private Sub RemoveBackgroundRun(row As BackgroundRunRow)
            If row Is Nothing OrElse Not _backgroundRuns.Remove(row) Then Return
            Me.RaisePropertyChanged(NameOf(HasBackgroundRuns))
        End Sub

        ''' <summary>Meldet einen Lauf an, den die GALERIE selbst fuehrt (die Gesichtssuche ueber die
        ''' Ansicht, eine Suchliste). Die Zeile gehoert dem Aufrufer: er schreibt seinen Fortschritt
        ''' hinein und gibt sie mit <see cref="EndGalleryRun"/> wieder ab.
        '''
        ''' Je Lauf eine EIGENE Zeile, kein gemeinsamer Platz: so kann ein zweiter Lauf dem ersten
        ''' seine Anzeige nicht wegnehmen, und ein spaet eintreffendes Ende raeumt nur die eigene
        ''' Zeile ab.</summary>
        Private Function BeginGalleryRun(stopAction As Action) As BackgroundRunRow
            Dim row As New BackgroundRunRow(New DelegateCommand(stopAction))
            AddBackgroundRun(row)
            Return row
        End Function

        Private Sub EndGalleryRun(row As BackgroundRunRow)
            RemoveBackgroundRun(row)
        End Sub

        ''' <summary>Haengt die Anzeige an BEIDE Hintergrundlaeufe. Ohne das bliebe sie stehen, wo
        ''' sie beim Aufbau der Galerie zufaellig stand: die Werte gehoeren fremden Objekten, und
        ''' eine Bindung darauf erfaehrt von deren Aenderungen nichts.</summary>
        Private Sub WatchBackgroundRuns()
            For Each run In New BackgroundRunViewModel() {CatalogIndex, FaceIndex}
                If run Is Nothing Then Continue For
                AddHandler run.PropertyChanged, AddressOf OnBackgroundRunChanged
                SyncBackgroundRunRow(run)
            Next
        End Sub

        Private Sub OnBackgroundRunChanged(sender As Object, e As ComponentModel.PropertyChangedEventArgs)
            SyncBackgroundRunRow(TryCast(sender, BackgroundRunViewModel))
        End Sub

        ''' <summary>Bringt die Zeile eines Hintergrundlaufs auf seinen Stand: legt sie an, wenn er
        ''' anlaeuft, raeumt sie ab, wenn er fertig ist, und schreibt sonst Text und Balken nach.
        '''
        ''' Alles auf einmal statt je gemeldeter Eigenschaft: mit demselben Ereignis kann sich der
        ''' Text aendern, obwohl nur IsRunning gemeldet wurde.</summary>
        Private Sub SyncBackgroundRunRow(run As BackgroundRunViewModel)
            If run Is Nothing Then Return
            Dim row As BackgroundRunRow = Nothing
            _backgroundRunRows.TryGetValue(run, row)

            If Not run.IsRunning Then
                If row Is Nothing Then Return
                _backgroundRunRows.Remove(run)
                RemoveBackgroundRun(row)
                Return
            End If

            If row Is Nothing Then
                row = New BackgroundRunRow(run.StopCommand)
                _backgroundRunRows(run) = row
                AddBackgroundRun(row)
            End If
            row.Text = run.StatusText
            row.Percent = run.ProgressPercent
            row.HasProgress = run.HasProgress
        End Sub

        ''' <summary>
        ''' Freier Speicherplatz des Laufwerks, auf dem der aktuelle Ordner liegt. Läuft im Hintergrund:
        ''' DriveInfo.GetDrives zählt unter Linux jeden Mountpoint auf, und ein toter NFS-Mount blockiert
        ''' bereits in IsReady. Aufgerufen wird die Methode nicht nur beim Ordnerwechsel, sondern über
        ''' SyncFolderItems nach jeder Dateioperation und jedem Watcher-Ereignis.
        ''' </summary>
        Private Sub UpdateStorageInfo()
            If _isVirtualFolder OrElse String.IsNullOrEmpty(_currentFolder) Then
                StorageFreeText = ""
                StorageFillPercent = 0
                Return
            End If

            Dim folder = _currentFolder
            Dim ignored = Task.Run(Sub()
                                       Dim info = ReadStorageInfo(folder)
                                       Dispatcher.UIThread.Post(Sub()
                                                                    ' Inzwischen woanders? Dann gehört die Zahl nicht mehr hierher.
                                                                    If Not String.Equals(NormalizePath(folder), NormalizePath(_currentFolder), StringComparison.OrdinalIgnoreCase) Then Return
                                                                    StorageFreeText = info.FreeText
                                                                    StorageFillPercent = info.FillPercent
                                                                End Sub, DispatcherPriority.Background)
                                   End Sub)
        End Sub

        Private Shared Function ReadStorageInfo(folderPath As String) As (FreeText As String, FillPercent As Double)
            Try
                Dim drive = GetCachedDrives().
                    Where(Function(d) d.IsReady AndAlso
                          folderPath.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase)).
                    OrderByDescending(Function(d) d.RootDirectory.FullName.Length).
                    FirstOrDefault()

                If drive Is Nothing Then Return ("", 0)

                Dim freeGb = drive.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0)
                Dim totalGb = drive.TotalSize / (1024.0 * 1024.0 * 1024.0)
                Dim de = New System.Globalization.CultureInfo("de-DE")

                Dim freeStr As String
                Dim totalStr As String

                If totalGb >= 1024 Then
                    freeStr = (freeGb / 1024.0).ToString("F1", de) & " TB"
                    totalStr = CInt(Math.Round(totalGb / 1024.0)).ToString() & " TB"
                Else
                    freeStr = CInt(Math.Round(freeGb)).ToString() & " GB"
                    totalStr = CInt(Math.Round(totalGb)).ToString() & " GB"
                End If

                Return (String.Format(LocalizationService.T("{0} von {1} frei"), freeStr, totalStr),
                        Math.Max(0, Math.Min(100, (drive.TotalSize - drive.AvailableFreeSpace) / CDbl(drive.TotalSize) * 100)))
            Catch
                Return ("", 0)
            End Try
        End Function

        ''' Die Liste der Laufwerke ändert sich selten, ihre Ermittlung ist teuer (jeder Mountpoint wird
        ''' angefasst). Die Kennzahlen je Laufwerk - freier Platz, Gesamtgröße - liest DriveInfo dagegen bei
        ''' jedem Zugriff frisch, der Cache macht die Anzeige also nicht veraltet.
        Private Shared _cachedDrives As DriveInfo() = Nothing
        Private Shared _cachedDrivesAt As Long = 0
        Private Const DriveCacheLifetimeMs As Long = 30_000

        Private Shared Function GetCachedDrives() As DriveInfo()
            Dim now = Environment.TickCount64
            Dim cached = _cachedDrives
            If cached IsNot Nothing AndAlso now - Volatile.Read(_cachedDrivesAt) < DriveCacheLifetimeMs Then Return cached

            Dim drives = DriveInfo.GetDrives()
            _cachedDrives = drives
            Volatile.Write(_cachedDrivesAt, now)
            Return drives
        End Function

        ''' <summary>Das Herz auf der KACHEL. Ein eigener Weg neben dem der Fusszeile, weil er genau
        ''' ein Bild umschaltet statt der ganzen Auswahl.</summary>
        Private Sub DoToggleFavorite(item As ImageItem)
            If item Is Nothing OrElse item.IsFolder Then Return
            Dim newVal = Not item.IsFavorite
            item.IsFavorite = newVal
            PersistFavorite(item, newVal, Not newVal)
            If Object.ReferenceEquals(item, _selectedItem) OrElse (SelectedItems IsNot Nothing AndAlso SelectedItems.Contains(item)) Then
                Me.RaisePropertyChanged(NameOf(SelectedIsFavorite))
            End If
            If _sortMode = "Favorite" Then FilterAndSort()
        End Sub

        ''' Herz in der Fußleiste: setzt die gesamte Auswahl auf denselben Zustand, statt jedes Bild
        ''' einzeln umzuschalten - bei gemischter Auswahl werden also erst alle zu Favoriten.
        Private Sub ToggleSelectedFavorite()
            Dim images = GetSelectedImageItems()
            If images.Count = 0 Then Return

            Dim target = Not SelectedIsFavorite
            For Each item In images
                Dim before = item.IsFavorite
                item.IsFavorite = target
                PersistFavorite(item, target, before)
            Next

            Me.RaisePropertyChanged(NameOf(SelectedIsFavorite))
            If _sortMode = "Favorite" Then FilterAndSort()
        End Sub

        Private Sub SetupWatcher(folderPath As String)
            If _watcher IsNot Nothing Then
                _watcher.EnableRaisingEvents = False
                RemoveHandler _watcher.Created, AddressOf OnFileSystemChanged
                RemoveHandler _watcher.Deleted, AddressOf OnFileSystemChanged
                RemoveHandler _watcher.Renamed, AddressOf OnFileSystemChanged
                _watcher.Dispose()
                _watcher = Nothing
            End If
            If String.IsNullOrEmpty(folderPath) OrElse Not Directory.Exists(folderPath) Then Return
            Try
                ' Erst die Handler, dann scharfstellen: mit EnableRaisingEvents im Initialisierer gingen
                ' alle Ereignisse verloren, die zwischen Konstruktor und AddHandler eintrafen.
                _watcher = New FileSystemWatcher(folderPath) With {
                    .NotifyFilter = NotifyFilters.FileName Or NotifyFilters.DirectoryName
                }
                AddHandler _watcher.Created, AddressOf OnFileSystemChanged
                AddHandler _watcher.Deleted, AddressOf OnFileSystemChanged
                AddHandler _watcher.Renamed, AddressOf OnFileSystemChanged
                _watcher.EnableRaisingEvents = True
            Catch ex As Exception
                ' Scheitert typischerweise am inotify-Limit unter Linux. Die Galerie funktioniert dann
                ' weiter, bekommt externe Änderungen aber nicht mehr mit - das gehört ins Diagnoseprotokoll,
                ' statt still verschluckt zu werden.
                DiagnosticLogService.LogException("Gallery.SetupWatcher", ex)
                _watcher?.Dispose()
                _watcher = Nothing
            End Try
        End Sub

        Private Sub OnFileSystemChanged(sender As Object, e As FileSystemEventArgs)
            ' Läuft auf einem Threadpool-Thread des Watchers, nicht auf dem UI-Thread.
            If _pendingReload Then Return
            _pendingReload = True
            Dispatcher.UIThread.Post(Sub()
                _pendingReload = False
                ' Abgleichen statt neu laden - auch eine Änderung, die wir selbst ausgelöst haben, meldet
                ' der Watcher noch einmal. Ein Neuaufbau würde dabei jedes Mal die Bildlaufposition
                ' verwerfen und alle Vorschaubilder neu erzeugen.
                SyncFolderItems()
                ' Externe Änderungen (z.B. anderer Dateimanager) betreffen nur die aktuell
                ' beobachtete Ordner-Ebene - den zugehörigen Baum-Knoten mit aktualisieren,
                ' damit dessen Unterordnerliste im TreeView nicht veraltet bleibt.
                Dim node = FindLoadedFolderNode(FolderTree, _currentFolder)
                If node IsNot Nothing Then node.ReloadChildren()
            End Sub, DispatcherPriority.Background)
        End Sub

        ''' Startet das Laden und kehrt zurück. Kein Async Sub: eine Ausnahme aus einem Async Sub erreicht
        ''' keinen Aufrufer und beendet den Prozess - LoadFolderImagesAsync fängt deshalb selbst alles ab.
        Private Sub LoadFolderImages(folderPath As String)
            Dim ignored = LoadFolderImagesAsync(folderPath)
        End Sub

        ''' <summary>
        ''' Liest einen Ordner ein. Alles, was sofort sichtbar sein muss - Suche abbrechen, Sammlungen
        ''' leeren, Beobachter umhängen - passiert synchron auf dem UI-Thread; das Aufzählen des
        ''' Verzeichnisses, die Katalogabfrage und der Aufbau der ImageItem-Objekte laufen im Hintergrund.
        ''' Vorher stand das Fenster still, bis alles fertig war: auf einer Netzwerkfreigabe oder einem
        ''' schlafenden USB-Laufwerk sekundenlang.
        '''
        ''' Der Abbruch-Token stammt aus BeginNewFolderThumbnailScope und wird vom nächsten Ordner-Laden
        ''' storniert. Zwei schnelle Klicks im Baum dürfen sonst dazu führen, dass die langsamere Antwort
        ''' die schnellere überschreibt - deshalb wird beim Eintreffen sowohl der Token als auch der
        ''' inzwischen aktuelle Ordner geprüft.
        ''' </summary>
        Public Async Function LoadFolderImagesAsync(folderPath As String) As Task
            CancelActiveSearch()
            ClearVirtualFolderState()
            Dim thumbnailToken = BeginNewFolderThumbnailScope()
            ClearSelection()
            ' Ein hinterlegter Auswahlwunsch gehoert zur verlassenen Ansicht und darf in der
            ' naechsten nicht nachtraeglich zuschlagen.
            _pendingSelectionPaths = Nothing
            _allItems.Clear()
            Items.Clear()
            DisplayItems.Clear()
            UpdateStorageInfo()

            If String.IsNullOrEmpty(folderPath) Then
                StatusText = LocalizationService.T("Kein Ordner gewählt")
                SetupWatcher(Nothing)
                Return
            End If

            ' Auch diese kleine Pruefung kann auf einer getrennten Netzwerkfreigabe mehrere
            ' Sekunden dauern. Sie gehoert daher zum Hintergrundteil des Ordnerwechsels und nicht
            ' vor das erste Await auf dem Anzeigefaden.
            Dim folderExists As Boolean
            Try
                folderExists = Await Task.Run(Function() Directory.Exists(folderPath), thumbnailToken)
            Catch ex As OperationCanceledException
                Return
            Catch ex As Exception
                If Not IsCurrentFolderLoad(folderPath, thumbnailToken) Then Return
                DiagnosticLogService.LogException("Gallery.LoadFolder.Exists", ex)
                StatusText = LocalizationService.T("Fehler beim Laden")
                SetupWatcher(Nothing)
                Return
            End Try

            If Not IsCurrentFolderLoad(folderPath, thumbnailToken) Then Return
            If Not folderExists Then
                StatusText = LocalizationService.T("Kein Ordner gewählt")
                SetupWatcher(Nothing)
                Return
            End If

            SetupWatcher(folderPath)
            StatusText = LocalizationService.T("Ordner wird gelesen...")

            ' ZUERST den Katalog anzeigen. Der Dateisystem-Abgleich darf nicht gleichzeitig seinen
            ' Parallel.For starten: er kann sonst sämtliche ThreadPool-Worker belegen, sodass die
            ' an sich schnelle Katalogabfrage mehrere Sekunden auf einen freien Worker wartet.
            Dim catalogTask = Task.Run(Function() BuildCatalogSnapshot(folderPath, thumbnailToken), thumbnailToken)

            ' Die Katalogzeilen werden gleich ein zweites Mal gebraucht - der Dateisystemlauf
            ' vergleicht sie mit dem, was wirklich auf der Platte liegt. Sie hier festzuhalten
            ' spart die zweite Abfrage ueber denselben Ordner; sie kostete dasselbe wie die erste.
            Dim catalogMetaByPath As Dictionary(Of String, LibraryImageMeta) = Nothing
            Try
                Dim catalog = Await catalogTask
                catalogMetaByPath = catalog.MetaByPath
                If IsCurrentFolderLoad(folderPath, thumbnailToken) AndAlso catalog.Items.Count > 0 Then
                    PerformanceTraceService.Measure("Ordner: Katalogbestand anzeigen",
                        Sub()
                            _allItems.AddRange(catalog.Items)
                            FilterAndSort()
                        End Sub)
                End If
            Catch ex As OperationCanceledException
                Return
            Catch ex As Exception
                ' Der Katalog ist ein Beschleuniger, nicht die Voraussetzung zum Öffnen eines Ordners.
                DiagnosticLogService.LogException("Gallery.LoadFolder.Catalog", ex)
            End Try

            ' Das Einfüllen der Collections plant Layout, Zeichnen und das Anfordern der sichtbaren
            ' Vorschaubilder nur EIN. Ohne diesen Yield startet der nachfolgende Scan seine Worker,
            ' bevor davon irgendetwas gelaufen ist.
            '
            ' DIE PRIORITAET IST DER GANZE PUNKT, und sie war zuerst falsch herum. In Avalonia ist
            ' ein GROESSERER Wert die hoehere Prioritaet, und die Reihenfolge lautet
            ' Render > Loaded > Default > Input > Background (nachgeschlagen in
            ' Avalonia.Threading.DispatcherPriority). Die Kette nach dem Fuellen haengt aber an
            ' Loaded: das Reset von DisplayItems stellt InvalidateGalleryItemsLayout auf Loaded, und
            ' erst DIESE Aufgabe stellt das Anfordern der Vorschaubilder nach. Ein Yield auf Render
            ' laeuft damit VOR beidem - er wartete also auf gar nichts, und die Kacheln blieben leer,
            ' bis der Scan durch war (Nutzerbefund 2026-08-28: "erst wenn die Meldung weg ist sieht
            ' man die ersten Bilder").
            '
            ' Background liegt unter Loaded UND unter Render und kommt deshalb zuverlaessig als
            ' Letzter dran - auch nach der von Loaded aus nachgestellten Render-Aufgabe.
            If _allItems.Count > 0 Then
                Await Dispatcher.UIThread.InvokeAsync(Sub()
                                                      End Sub, DispatcherPriority.Background)
            End If

            ' Erst nachdem die Katalog-Collection gebunden wurde, den teuren Abgleich anstoßen.
            ' Das nächste Await gibt den UI-Faden frei, damit die Kacheln vor dessen Abschluss
            ' tatsächlich gemessen und gezeichnet werden können.
            Dim scanTask = Task.Run(Function() ScanFolder(folderPath, thumbnailToken, catalogMetaByPath), thumbnailToken)

            Dim scan As FolderScanResult
            Try
                scan = Await scanTask
            Catch ex As OperationCanceledException
                Return
            Catch ex As UnauthorizedAccessException
                If Not IsCurrentFolderLoad(folderPath, thumbnailToken) Then Return
                StatusText = LocalizationService.T("Zugriff verweigert")
                Return
            Catch ex As IOException
                If Not IsCurrentFolderLoad(folderPath, thumbnailToken) Then Return
                StatusText = LocalizationService.T("Fehler beim Laden")
                Return
            Catch ex As Exception
                If Not IsCurrentFolderLoad(folderPath, thumbnailToken) Then Return
                DiagnosticLogService.LogException("Gallery.LoadFolder", ex)
                StatusText = LocalizationService.T("Fehler beim Laden")
                Return
            End Try

            ' Ein zwischenzeitlicher Ordnerwechsel macht dieses Ergebnis wertlos.
            If Not IsCurrentFolderLoad(folderPath, thumbnailToken) Then Return

            ' Der EINZIGE Teil des Ordnerwechsels, der auf dem Anzeigefaden liegt: einfuellen,
            ' filtern, sortieren, Anzeigefenster stellen. Alles davor lief im Hintergrund. Ein
            ' eigener Messpunkt, weil nur diese Zeitspanne das Fenster wirklich stehen laesst -
            ' die Zeiten der Hintergrundschritte kosten Wartezeit, aber keine Stockung.
            '
            ' UEBERNEHMEN, NICHT ANHAENGEN UND NICHT ERSETZEN. Der Katalogbestand steht bereits in
            ' _allItems, und das Dateisystem liefert dieselben Bilder noch einmal - diesmal als
            ' Wahrheit. Beide naheliegenden Wege waren falsch:
            '
            ' ANHAENGEN legte jedes Bild doppelt in die Liste. Die pfadbasierte Sicherung in
            ' FilterAndSort verbarg das in der Anzeige, behielt aber das KATALOG-Element - die
            ' Kacheln bekamen nie die Dateiangaben, und der Nachlauf ueber NeedsMetaRefresh schrieb
            ' in Objekte, die an keiner Kachel haengen. Eine extern geaenderte Bewertung blieb
            ' unsichtbar.
            '
            ' ERSETZEN tauschte JEDES Objekt aus, auch die unveraenderten. Die Anzeige sah lauter
            ' neue Elemente und baute saemtliche Kacheln neu auf - sichtbar als Flackern kurz nach
            ' dem ersten Bild, obwohl sich nichts geaendert hatte (Nutzerbefund 2026-08-28).
            '
            ' Also behaelt jedes bereits gezeigte Element seine Identitaet und bekommt nur die
            ' geprueften Werte (ImageItem.AdoptScannedState). Danach stehen in Items und
            ' DisplayItems dieselben Objekte in derselben Reihenfolge, ApplyDisplayWindow sieht
            ' ueber SequenceEqual keinen Unterschied - und die Anzeige ruehrt sich nicht.
            Dim adopted = MergeScanIntoExistingItems(scan.Items)
            PerformanceTraceService.Measure("Ordner: einfuellen und sortieren",
                Sub()
                    _allItems.Clear()
                    _allItems.AddRange(adopted)
                    FilterAndSort()
                End Sub)

            ' Der Nachlauf muss die ANGEZEIGTEN Objekte treffen, nicht die frisch gebauten: nach der
            ' Uebernahme sind das zwei verschiedene. Der Pfad ist das Bindeglied.
            If scan.NeedsMetaRefresh.Count > 0 Then
                Dim nachzulesen = scan.NeedsMetaRefresh.
                    Where(Function(i) i IsNot Nothing).
                    Select(Function(i) i.FilePath).
                    ToHashSet(PathIdentity.Comparer)
                Dim ziele = _allItems.Where(Function(i) i IsNot Nothing AndAlso nachzulesen.Contains(i.FilePath)).ToList()
                If ziele.Count > 0 Then QueueBackgroundMetaRefresh(ziele, thumbnailToken)
            End If

            ' Im Ruhezustand (Viewport-Warteschlange leer) füllt sich der Rest des Ordners
            ' nach und nach mit Thumbnails auf, auch für Bilder, die nie in die Nähe des
            ' sichtbaren Bereichs gescrollt werden. Mit niedrigerer Priorität eingereiht, damit
            ' die erste Viewport-Anfrage (Input-Priorität) beim Öffnen des Ordners nicht durch
            ' hunderte Hintergrund-Jobs ausgebremst wird. Zusätzlich verzögert gestartet (statt
            ' sofort), damit der anfängliche CPU-Ausschlag nicht mit dem ersten Aufbau/Rendern
            ' der Gallery und dem Laden der sichtbaren Viewport-Thumbnails zusammenfällt - deren
            ' Vorrang bleibt davon unberührt, da sie über eine separate, garantiert freie
            ' Worker-Kapazität laufen (siehe ImageItem.MaxConcurrentBackgroundJobs).
            Const BackgroundThumbnailStartupDelayMs As Integer = 1500
            Dim itemsSnapshot = _allItems.ToList()
            Dim ignoredPreload = Task.Run(Async Function()
                                              Try
                                                  Await Task.Delay(BackgroundThumbnailStartupDelayMs, thumbnailToken)
                                              Catch ex As OperationCanceledException
                                                  Return
                                              End Try
                                              Dispatcher.UIThread.Post(Sub() ImageItem.QueueBackgroundThumbnails(itemsSnapshot), DispatcherPriority.Background)
                                          End Function)
        End Function

        ''' <summary>Legt das Ergebnis des Dateisystemlaufs auf den bereits gezeigten Sofortbestand:
        ''' Reihenfolge und Zusammensetzung kommen vom Lauf, die OBJEKTE aber von der Anzeige,
        ''' soweit es sie dort schon gibt. Siehe <see cref="ImageItem.AdoptScannedState"/>.
        '''
        ''' <para>Laeuft auf dem Anzeigefaden - AdoptScannedState meldet Aenderungen an gebundene
        ''' Kacheln.</para></summary>
        Private Function MergeScanIntoExistingItems(scanItems As List(Of ImageItem)) As List(Of ImageItem)
            If scanItems Is Nothing Then Return New List(Of ImageItem)()
            If _allItems.Count = 0 Then Return scanItems

            Dim vorhanden As New Dictionary(Of String, ImageItem)(PathIdentity.Comparer)
            For Each item In _allItems
                If item Is Nothing OrElse String.IsNullOrEmpty(item.FilePath) Then Continue For
                If Not vorhanden.ContainsKey(item.FilePath) Then vorhanden(item.FilePath) = item
            Next

            Dim merged As New List(Of ImageItem)(scanItems.Count)
            For Each frisch In scanItems
                Dim alt As ImageItem = Nothing
                ' Gleicher Pfad reicht NICHT: aus einem Ordner kann eine Datei geworden sein und
                ' umgekehrt. Dann ist es trotz gleichen Namens ein anderes Element.
                If frisch IsNot Nothing AndAlso Not String.IsNullOrEmpty(frisch.FilePath) AndAlso
                   vorhanden.TryGetValue(frisch.FilePath, alt) AndAlso alt IsNot Nothing AndAlso
                   alt.IsFolder = frisch.IsFolder AndAlso
                   alt.IsParentFolderEntry = frisch.IsParentFolderEntry Then
                    ' Ordnerkacheln tragen keine Bild- oder Katalogwerte; sie bleiben, wie sie sind.
                    If Not frisch.IsFolder Then alt.AdoptScannedState(frisch)
                    merged.Add(alt)
                Else
                    merged.Add(frisch)
                End If
            Next
            Return merged
        End Function

        ''' Das Ergebnis eines Ordner-Durchlaufs, fertig zum Einfüllen auf dem UI-Thread.
        Private Structure FolderScanResult
            Public Items As List(Of ImageItem)
            Public NeedsMetaRefresh As List(Of ImageItem)
        End Structure

        ''' Der Sofortbestand eines Ordners: die fertigen Kacheln UND die Katalogzeilen, aus denen
        ''' sie stammen. Die Zeilen gehen weiter an den Dateisystemlauf, der sonst dieselbe Abfrage
        ''' ein zweites Mal stellen wuerde.
        Private Structure FolderCatalogSnapshot
            Public Items As List(Of ImageItem)
            Public MetaByPath As Dictionary(Of String, LibraryImageMeta)
        End Structure

        ''' <summary>Liest die Katalogzeilen der Ordner-Ebene und baut daraus den Sofortbestand.
        ''' Laeuft vollstaendig im Hintergrund; keine gebundene Collection wird beruehrt.</summary>
        Private Function BuildCatalogSnapshot(folderPath As String, thumbnailToken As CancellationToken) As FolderCatalogSnapshot
            Dim rows = LibraryService.Instance.GetImagesInFolder(folderPath)
            Dim byPath As New Dictionary(Of String, LibraryImageMeta)(PathIdentity.Comparer)
            For Each row In rows
                If row Is Nothing OrElse String.IsNullOrWhiteSpace(row.FilePath) Then Continue For
                byPath(row.FilePath) = row
            Next

            ' "..", die Unterordner UND die Katalogbilder - der Sofortbestand zeigt dieselbe
            ' Zusammensetzung wie der spaetere Dateisystemlauf. Alles andere liesse die Galerie
            ' nachtraeglich springen (siehe BuildFolderEntries).
            Dim items As New List(Of ImageItem)()
            Try
                items.AddRange(BuildFolderEntries(folderPath))
            Catch ex As Exception
                ' Ein nicht lesbarer Ordner ist kein Grund, den Katalogbestand fallenzulassen -
                ' der Dateisystemlauf meldet den Fehler gleich darauf mit seinem eigenen Status.
                DiagnosticLogService.LogException("Gallery.CatalogSnapshot.Folders", ex)
            End Try
            items.AddRange(BuildCatalogItems(rows, thumbnailToken))

            Return New FolderCatalogSnapshot With {
                .Items = items,
                .MetaByPath = byPath
            }
        End Function

        ''' <summary>Die Ordner-Eintraege einer Ebene: erst "..", dann die Unterordner.
        '''
        ''' <para>BEIDE Wege in den Ordner bauen sie ueber DIESE Stelle - der Sofortbestand aus dem
        ''' Katalog und der Dateisystemlauf danach. Fehlten sie im Sofortbestand, schoebe der Lauf
        ''' sie kurz darauf VOR den Bildern ein, und die ganze Galerie ruckte um eine Zeile weiter
        ''' (Nutzerbefund 2026-08-28: "ansonsten springt die Gallery danach"). Ein
        ''' Verzeichnis-Listing zweimal zu machen kostet weniger als dieser Sprung; das zweite
        ''' beantwortet ohnehin das Betriebssystem aus seinem Zwischenspeicher.</para>
        '''
        ''' <para>Laeuft im Hintergrund und beruehrt keine gebundene Collection.</para></summary>
        Private Function BuildFolderEntries(folderPath As String) As List(Of ImageItem)
            Dim entries As New List(Of ImageItem)()
            If Not _showFolders Then Return entries

            If _showParentFolder Then
                Dim parentPath = IO.Path.GetDirectoryName(folderPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
                If Not String.IsNullOrEmpty(parentPath) AndAlso Not IsAncestorOrSelf(folderPath, parentPath) AndAlso Directory.Exists(parentPath) Then
                    entries.Add(ImageItem.CreateParentFolderEntry(parentPath))
                End If
            End If

            For Each folder In Directory.GetDirectories(folderPath).
                Where(Function(d) FolderNode.ShowHiddenFolders OrElse Not IO.Path.GetFileName(d).StartsWith(".")).
                OrderBy(Function(d) IO.Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase)

                entries.Add(ImageItem.FromFolder(folder))
            Next
            Return entries
        End Function

        ''' <summary>Baut sofort darstellbare Einträge ausschließlich aus dem Katalog. Dateigröße und
        ''' Änderungszeit werden bei Bedarf später durch den Hintergrund-Abgleich beziehungsweise die
        ''' sichtbaren Bindungen ergänzt; die Kachel-, Sortier- und Filterdaten liegen bereits vor.
        '''
        ''' <para>Diese Elemente sind eine VORSCHAU, kein Endstand: sie uebernehmen die Katalogwerte
        ''' ungeprueft, also auch dann, wenn die Datei seit dem letzten Scan geaendert wurde. Sobald
        ''' der Dateisystemlauf fertig ist, ersetzt er sie (siehe LoadFolderImagesAsync).</para></summary>
        Private Function BuildCatalogItems(metaItems As IEnumerable(Of LibraryImageMeta), thumbnailToken As CancellationToken) As List(Of ImageItem)
            Dim result As New List(Of ImageItem)()
            For Each meta In metaItems
                If meta Is Nothing OrElse String.IsNullOrWhiteSpace(meta.FilePath) Then Continue For
                Dim item = ImageItem.CreateLightweight(meta.FilePath, thumbnailToken)
                item.IsFavorite = meta.IsFavorite
                item.Rating = meta.Rating
                item.ColorLabel = meta.ColorLabel
                item.Tags = If(meta.Tags, New List(Of String)())
                item.ImageWidth = meta.ImageWidth.GetValueOrDefault()
                item.ImageHeight = meta.ImageHeight.GetValueOrDefault()
                item.ExifDateTaken = ExifService.ParseExifDateTime(meta.DateTaken)
                item.ExifDateModified = ExifService.ParseExifDateTime(meta.DateModifiedExif)
                item.ExifCamera = meta.Camera
                item.ExifIso = meta.Iso
                item.ExifAperture = meta.Aperture
                item.HasExifMetadata = meta.HasExifMetadata
                item.HasIptcMetadata = meta.HasIptcMetadata
                item.HasXmpMetadata = meta.HasXmpMetadata
                item.HasIccProfile = meta.HasIccProfile
                item.ExifMetadataSummary = meta.ExifSummary
                item.IptcMetadataSummary = meta.IptcSummary
                item.XmpMetadataSummary = meta.XmpSummary
                item.IccMetadataSummary = meta.IccSummary
                ' Die Dateidaten gehören ebenfalls zur Sortierung (nicht nur EXIF). Ohne sie
                ' stünden Katalogelemente bei "geändert" bzw. "erstellt" am Rand, bis der
                ' Hintergrundscan sie ersetzt.
                item.ApplyCatalogFileDates(meta.FileCreatedAt, meta.ScannedSourceModifiedAt)
                result.Add(item)
            Next
            Return result
        End Function

        ''' Läuft im Hintergrund. Berührt nur lesend Felder des ViewModels und keine gebundene Collection.
        ''' <param name="catalogMetaByPath">Die Katalogzeilen des Ordners, falls der Sofortbestand sie
        ''' schon geholt hat. Nothing bedeutet nur "noch nicht da" - dann fragt der Lauf selbst.</param>
        Private Function ScanFolder(folderPath As String, thumbnailToken As CancellationToken,
                                    Optional catalogMetaByPath As Dictionary(Of String, LibraryImageMeta) = Nothing) As FolderScanResult
            Dim items As New List(Of ImageItem)()

            PerformanceTraceService.Measure("Ordner: Unterordner auflisten",
                Sub() items.AddRange(BuildFolderEntries(folderPath)))

            thumbnailToken.ThrowIfCancellationRequested()

            Dim needsMetaRefresh As New List(Of ImageItem)()
            Dim files = PerformanceTraceService.Measure("Ordner: Dateien auflisten",
                                                        Function() EnumerateImageFiles(folderPath))
            items.AddRange(BuildFileItems(files, folderPath, thumbnailToken, needsMetaRefresh, catalogMetaByPath))

            Return New FolderScanResult With {.Items = items, .NeedsMetaRefresh = needsMetaRefresh}
        End Function

        ''' <summary>Lädt Breite/Höhe/EXIF-Daten für Bilder nach, die beim Ordner-Scan noch fehlten oder
        ''' seit dem letzten Scan geändert wurden - mit begrenzter Parallelität und niedriger Priorität,
        ''' analog zur bestehenden Hintergrund-Thumbnail-Vorladung. Reihenfolge/Filter werden erst einmal
        ''' am Ende neu berechnet (kein Re-Sort pro Einzel-Item), damit die Kacheln beim Scrollen nicht
        ''' springen, während die Daten nach und nach eintreffen.</summary>
        Private Sub QueueBackgroundMetaRefresh(items As List(Of ImageItem), cancellationToken As CancellationToken)
            If items Is Nothing OrElse items.Count = 0 Then Return
            Const MetaRefreshStartupDelayMs As Integer = 250
            ' Die halbe Kernzahl ist die Obergrenze, nicht die Antwort: auf einer drehenden Platte
            ' und auf einem knappen Rechner sind zwoelf gleichzeitige Leser schaedlich (siehe
            ' IoConcurrencyService).
            Dim folderForDisk = items.Select(Function(i) i?.FilePath).FirstOrDefault(Function(p) Not String.IsNullOrWhiteSpace(p))
            Dim degreeOfParallelism = IoConcurrencyService.RecommendedReaders(
                folderForDisk, Math.Max(1, Environment.ProcessorCount \ 2))

            Task.Run(Async Function()
                         Try
                             Await Task.Delay(MetaRefreshStartupDelayMs, cancellationToken)
                         Catch ex As OperationCanceledException
                             Return
                         End Try

                         ' Fuer die Fusszeile: wie viele Bilder dieser Lauf vor sich hat und wie
                         ' viele davon schon durch sind. Ohne das ist ein laufender Metadatenlauf
                         ' unsichtbar - man merkt ihn nur daran, dass die Anwendung beschaeftigt wirkt.
                         ' Die eigene Laufnummer ziehen und die Zähler übernehmen - UNTER EINER
                         ' SPERRE. Beides einzeln zu tun ließe ein Fenster zwischen Nummer und
                         ' Zählern: startet dort ein neuer Lauf, überschreibt der ältere dessen
                         ' frisch gesetzte Zahlen und die Fußzeile zählt den falschen Scan.
                         Dim meinLauf As Integer
                         SyncLock _metaRefreshLock
                             meinLauf = _metaRefreshRun + 1
                             _metaRefreshRun = meinLauf
                             Threading.Volatile.Write(_metaRefreshTotal, items.Count)
                             Threading.Volatile.Write(_metaRefreshDone, 0)
                         End SyncLock

                         Dim nextIndex = -1
                         Dim workers As New List(Of Task)()
                         For w = 1 To degreeOfParallelism
                             workers.Add(Task.Run(Async Function()
                                                       Do
                                                           Dim i = Interlocked.Increment(nextIndex)
                                                           If i >= items.Count Then Exit Do
                                                           If cancellationToken.IsCancellationRequested Then Exit Do
                                                           Dim item = items(i)
                                                           Try
                                                               Dim data = ExifService.ReadExif(item.FilePath)
                                                               Dim fields = ExifService.ExtractSearchFields(data, item.FilePath)
                                                               Dim xmpRating = ExifService.GetXmpRating(data)
                                                               Dim catalogSummary = ExifService.BuildCatalogSummary(data, fields)
                                                               Dim hasExif = catalogSummary.HasExifMetadata
                                                               Dim hasIptc = catalogSummary.HasIptcMetadata
                                                               Dim hasXmp = catalogSummary.HasXmpMetadata
                                                               Dim hasIcc = catalogSummary.HasIccProfile
                                                               Dim exifSummary = catalogSummary.ExifSummary
                                                               Dim iptcSummary = catalogSummary.IptcSummary
                                                               Dim xmpSummary = catalogSummary.XmpSummary
                                                               Dim iccSummary = catalogSummary.IccSummary
                                                               ' ImportSidecarCatalogData legt ggf. die .fpxmp an, und deren
                                                               ' Vorhandensein steckt im Stempel, den SyncExifData schreibt -
                                                               ' deshalb ZUERST importieren. Andersherum traege der Stempel den
                                                               ' Zustand von davor und der Ordner gaelte beim naechsten Wechsel
                                                               ' erneut als veraltet.
                                                               Dim imported = ImportSidecarCatalogData(item.FilePath, xmpRating,
                                                                                                       item.Rating, item.ColorLabel, item.Tags)
                                                               LibraryService.Instance.SyncExifData(item.FilePath, fields, catalogSummary)
                                                               Dim width = If(fields.ImageWidth, 0)
                                                               Dim height = If(fields.ImageHeight, 0)
                                                               Dim exifTaken = ExifService.ParseExifDateTime(fields.DateTaken)
                                                               Dim exifModified = ExifService.ParseExifDateTime(fields.DateModifiedExif)
                                                               Dim camera = fields.Camera
                                                               Dim iso = fields.Iso
                                                               Dim aperture = fields.Aperture
                                                               Await Dispatcher.UIThread.InvokeAsync(Sub()
                                                                                                          If cancellationToken.IsCancellationRequested Then Return
                                                                                                          item.ImageWidth = width
                                                                                                          item.ImageHeight = height
                                                                                                          If imported.Rating.HasValue Then item.Rating = imported.Rating.Value
                                                                                                          If imported.Favorite.HasValue Then item.IsFavorite = imported.Favorite.Value
                                                                                                          If imported.HasColorLabel Then item.ColorLabel = imported.ColorLabel
                                                                                                          ' Der Tags-Setter verwirft den Suchtext-Cache selbst, die
                                                                                                          ' importierten Stichworte sind also sofort auffindbar.
                                                                                                          If imported.Tags IsNot Nothing Then item.Tags = imported.Tags
                                                                                                          item.ExifDateTaken = exifTaken
                                                                                                          item.ExifDateModified = exifModified
                                                                                                          item.ExifCamera = camera
                                                                                                          item.ExifIso = iso
                                                                                                          item.ExifAperture = aperture
                                                                                                          item.HasExifMetadata = hasExif
                                                                                                          item.HasIptcMetadata = hasIptc
                                                                                                          item.HasXmpMetadata = hasXmp
                                                                                                          item.HasIccProfile = hasIcc
                                                                                                          item.ExifMetadataSummary = exifSummary
                                                                                                          item.IptcMetadataSummary = iptcSummary
                                                                                                          item.XmpMetadataSummary = xmpSummary
                                                                                                          item.IccMetadataSummary = iccSummary
                                                                                                      End Sub)
                                                           Catch
                                                           End Try
                                                           ' Nur der jüngste Lauf zählt mit - ein
                                                           ' überholter würde den Fortschritt des
                                                           ' neuen verfälschen.
                                                           If Threading.Volatile.Read(_metaRefreshRun) = meinLauf Then
                                                               Threading.Interlocked.Increment(_metaRefreshDone)
                                                           End If
                                                       Loop
                                                   End Function))
                         Next
                         Await Task.WhenAll(workers)
                         ' Fertig heisst: die Fusszeile soll nichts mehr melden. Auch bei Abbruch -
                         ' deshalb VOR der Abbruchpruefung. ABER nur, wenn inzwischen kein neuer Lauf
                         ' begonnen hat: sonst raeumt der ueberholte Lauf die Zaehler des laufenden
                         ' ab, und die Fusszeile verschweigt einen Scan, der noch arbeitet.
                         SyncLock _metaRefreshLock
                             If _metaRefreshRun = meinLauf Then
                                 Threading.Volatile.Write(_metaRefreshTotal, 0)
                                 Threading.Volatile.Write(_metaRefreshDone, 0)
                             End If
                         End SyncLock

                         If cancellationToken.IsCancellationRequested Then Return
                         Await Dispatcher.UIThread.InvokeAsync(Sub()
                                                                    If cancellationToken.IsCancellationRequested Then Return
                                                                    FilterAndSort()
                                                                End Sub)
                     End Function)
        End Sub

        ' ── Was im Hintergrund noch laeuft ──────────────────────────────────────
        '
        ' Zwei Laeufe arbeiten der Galerie zu, ohne sich bisher zu zeigen: das Erzeugen der
        ' Vorschaubilder und das Nachlesen der Metadaten. Beide waren nur daran zu erkennen, dass
        ' nach und nach etwas erschien (Patrick, 2026-08-27: "man sollte auch erkennen koennen,
        ' wenn im Hintergrund noch weitere Threads Thumbnails erstellen und Metadaten einlesen").
        Private Shared _metaRefreshTotal As Integer = 0
        Private Shared _metaRefreshDone As Integer = 0

        ''' <summary>Die Nummer des jüngsten Metadatenlaufs.
        '''
        ''' <para>Die Zähler darüber sind gemeinsam, die Läufe aber nicht: wer schnell den Ordner
        ''' wechselt, hat zwei davon gleichzeitig unterwegs. Ohne diese Nummer räumte der ältere,
        ''' langsamere Lauf beim Fertigwerden die Zähler ab - und die Fußzeile verschwieg den
        ''' Fortschritt des NEUEN, noch laufenden Scans. Jeder Lauf zieht deshalb beim Start eine
        ''' Nummer und schreibt nur weiter, solange sie noch die aktuelle ist.</para></summary>
        Private Shared _metaRefreshRun As Integer = 0
        Private Shared ReadOnly _metaRefreshLock As New Object()

        Private _backgroundWorkText As String = ""

        ''' <summary>Was gerade im Hintergrund laeuft, als Satz fuer die Fusszeile - leer, wenn
        ''' nichts laeuft. Der Text wird von einem Zeitgeber nachgezogen und nur gemeldet, wenn er
        ''' sich wirklich geaendert hat: eine Meldung je Vorschaubild waere selbst eine Last.</summary>
        Public ReadOnly Property BackgroundWorkText As String
            Get
                Return _backgroundWorkText
            End Get
        End Property

        Public ReadOnly Property HasBackgroundWork As Boolean
            Get
                Return Not String.IsNullOrEmpty(_backgroundWorkText)
            End Get
        End Property

        ''' <summary>Baut den Satz neu und meldet ihn, falls er sich geaendert hat.</summary>
        Private Sub RefreshBackgroundWorkText()
            Dim teile As New List(Of String)()

            Dim thumbs = ImageItem.PendingThumbnails
            If thumbs > 0 Then teile.Add($"{thumbs:N0} {LocalizationService.T("Vorschaubilder")}")

            Dim total = Threading.Volatile.Read(_metaRefreshTotal)
            If total > 0 Then
                Dim done = Math.Min(total, Threading.Volatile.Read(_metaRefreshDone))
                teile.Add(String.Format(LocalizationService.T("Metadaten {0} von {1}"),
                                        done.ToString("N0"), total.ToString("N0")))
            End If

            Dim text = If(teile.Count = 0, "", String.Join("  ·  ", teile))
            If String.Equals(text, _backgroundWorkText, StringComparison.Ordinal) Then Return
            _backgroundWorkText = text
            Me.RaisePropertyChanged(NameOf(BackgroundWorkText))
            Me.RaisePropertyChanged(NameOf(HasBackgroundWork))
        End Sub


        ''' <summary>
        ''' Gleicht die Einträge des aktuellen Ordners mit dem Dateisystem ab, statt sie neu aufzubauen:
        ''' vorhandene Elemente behalten ihre Instanz und damit ihr Vorschaubild, ihre Metadaten und ihre
        ''' Auswahl, verschwundene fliegen raus, neue kommen dazu. Anders als LoadFolderImages werden Items
        ''' und DisplayItems nicht geleert - die Bildlaufposition bleibt deshalb erhalten.
        '''
        ''' Gedacht für alles, was den Ordnerinhalt verändert, ohne ihn zu wechseln: Löschen, Umbenennen,
        ''' Einfügen, Verschieben, Duplizieren, Konvertieren - und den FileSystemWatcher, der externe
        ''' Änderungen meldet. Existiert der Ordner nicht mehr, fällt die Methode auf den vollen Neuaufbau
        ''' zurück, der den leeren Zustand samt Statusmeldung herstellt.
        ''' </summary>
        Private Sub SyncFolderItems()
            ' Absichtlich ohne Warten: die meisten Aufrufer sind Ereignisse. Der Teil NACH dem
            ' ersten Await liegt aber auf dem Anzeigefaden und kann werfen (PruneSelection,
            ' FilterAndSort, UpdateStorageInfo). Ohne diesen Anhang landete das in einer Task, die
            ' niemand liest, und verschwaende still - solange die Methode ein Sub war, war eine
            ' solche Ausnahme wenigstens zu sehen.
            SyncFolderItemsAsync().ContinueWith(
                Sub(lauf) DiagnosticLogService.LogException("GalleryViewModel.SyncFolderItems", lauf.Exception),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default)
        End Sub

        ''' <summary>Gleicht den aktuellen Ordner im Hintergrund mit dem Dateisystem ab.
        '''
        ''' <para>Der Aufrufer darf sofort weiterarbeiten: Aufzaehlen, Katalogabfrage,
        ''' Beistelldateien und der Aufbau neuer <see cref="ImageItem"/>s laufen ausserhalb des
        ''' Anzeigefadens. Nur das Uebernehmen der fertig vorbereiteten Liste bleibt auf ihm. Das
        ''' ist wichtig, weil diese Methode auch vom FileSystemWatcher und nach Dateioperationen
        ''' gerufen wird - ein grosser Ordner durfte die Bedienung dabei nicht anhalten.</para></summary>
        Private Async Function SyncFolderItemsAsync() As Task
            If _isVirtualFolder Then Return
            If String.IsNullOrEmpty(_currentFolder) Then Return

            Dim folderPath = _currentFolder
            Dim generation = Interlocked.Increment(_folderSyncGeneration)
            ' Den laufenden Thumbnail-Scope weiterbenutzen: BeginNewFolderThumbnailScope würde die
            ' Vorschaubilder der erhalten gebliebenen Elemente verwerfen.
            Dim thumbnailToken = _thumbnailLoadCts.Token

            Dim folderExists As Boolean
            Try
                folderExists = Await Task.Run(Function() Directory.Exists(folderPath), thumbnailToken)
            Catch ex As OperationCanceledException
                Return
            Catch ex As UnauthorizedAccessException
                If Not IsCurrentFolderSync(generation, folderPath, thumbnailToken) Then Return
                StatusText = LocalizationService.T("Zugriff verweigert")
                Return
            Catch ex As IOException
                If Not IsCurrentFolderSync(generation, folderPath, thumbnailToken) Then Return
                StatusText = LocalizationService.T("Fehler beim Laden")
                Return
            Catch ex As Exception
                If Not IsCurrentFolderSync(generation, folderPath, thumbnailToken) Then Return
                DiagnosticLogService.LogException("GalleryViewModel.SyncFolderItems.Exists", ex)
                StatusText = LocalizationService.T("Fehler beim Laden")
                Return
            End Try

            If Not IsCurrentFolderSync(generation, folderPath, thumbnailToken) Then Return
            If Not folderExists Then
                LoadFolderImages(folderPath)
                Return
            End If

            Dim existing = New Dictionary(Of String, ImageItem)(StringComparer.OrdinalIgnoreCase)
            For Each item In _allItems
                If item IsNot Nothing AndAlso Not item.IsParentFolderEntry AndAlso Not existing.ContainsKey(item.FilePath) Then
                    existing(item.FilePath) = item
                End If
            Next

            Dim keepParent = _allItems.FirstOrDefault(Function(i) i IsNot Nothing AndAlso i.IsParentFolderEntry)
            Dim showFolders = _showFolders
            Dim showParentFolder = _showParentFolder
            Dim syncResult As FolderSyncResult
            Try
                syncResult = Await Task.Run(Function() BuildFolderSyncResult(folderPath, thumbnailToken,
                                                                              existing, keepParent,
                                                                              showFolders, showParentFolder))
            Catch ex As UnauthorizedAccessException
                If Not IsCurrentFolderSync(generation, folderPath, thumbnailToken) Then Return
                StatusText = LocalizationService.T("Zugriff verweigert")
                Return
            Catch ex As IOException
                If Not IsCurrentFolderSync(generation, folderPath, thumbnailToken) Then Return
                StatusText = LocalizationService.T("Fehler beim Laden")
                Return
            Catch ex As Exception
                If Not IsCurrentFolderSync(generation, folderPath, thumbnailToken) Then Return
                ' SyncFolderItems wird auch als Fire-and-forget vom Watcher aufgerufen. Eine
                ' unerwartete Katalog- oder Metadaten-Ausnahme darf deshalb weder den Dispatcher
                ' noch die virtuelle Ansicht spaeter beeinflussen.
                DiagnosticLogService.LogException("GalleryViewModel.SyncFolderItems", ex)
                StatusText = LocalizationService.T("Fehler beim Laden")
                Return
            End Try

            ' Zwischenzeitlich kann ein weiterer Watcher-Tick, ein Ordnerwechsel oder eine
            ' Dateioperation gelaufen sein. Dann gehoert dieses Ergebnis nicht mehr zur Ansicht.
            If Not IsCurrentFolderSync(generation, folderPath, thumbnailToken) Then Return

            PruneSelection(New HashSet(Of ImageItem)(syncResult.Rebuilt))
            _allItems.Clear()
            _allItems.AddRange(syncResult.Rebuilt)
            FilterAndSort()
            UpdateStorageInfo()
            ApplyPendingSelection()

            If syncResult.ItemsNeedingMetaRefresh.Count > 0 Then
                QueueBackgroundMetaRefresh(syncResult.ItemsNeedingMetaRefresh, thumbnailToken)
            End If
            If syncResult.NewItems.Count > 0 Then ImageItem.QueueBackgroundThumbnails(syncResult.NewItems)
        End Function

        ''' <summary>Hinterlegt, welche Pfade nach dem naechsten uebernommenen Abgleich markiert sein
        ''' sollen. Siehe <see cref="_pendingSelectionPaths"/>.</summary>
        Private Sub RequestSelectionAfterSync(paths As IEnumerable(Of String))
            Dim wanted = If(paths, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrEmpty(p)).
                ToHashSet(PathIdentity.Comparer)
            _pendingSelectionPaths = If(wanted.Count > 0, wanted, Nothing)
        End Sub

        ''' <summary>Loest einen hinterlegten Auswahlwunsch ein. Findet sich kein einziger Pfad
        ''' wieder, bleibt die Auswahl wie sie ist: das Umbenennen kann fehlgeschlagen sein, und
        ''' eine leergeraeumte Auswahl waere dann die schlechtere Antwort.</summary>
        Private Sub ApplyPendingSelection()
            Dim wanted = _pendingSelectionPaths
            If wanted Is Nothing Then Return
            _pendingSelectionPaths = Nothing
            Dim treffer = Items.Where(Function(i) i IsNot Nothing AndAlso Not i.IsGroupHeader AndAlso
                                                  wanted.Contains(If(i.FilePath, ""))).ToList()
            If treffer.Count = 0 Then Return
            ReplaceSelection(treffer)
        End Sub

        ''' <summary>Der Ordner kann waehrend selbst einer einfachen Existenzpruefung gewechselt
        ''' werden. Das Ergebnis darf dann weder die neue Ansicht leeren noch ihren Status aendern.</summary>
        Private Function IsCurrentFolderLoad(folderPath As String, thumbnailToken As CancellationToken) As Boolean
            Return Not thumbnailToken.IsCancellationRequested AndAlso
                   Not _isVirtualFolder AndAlso
                   String.Equals(NormalizePath(folderPath), NormalizePath(_currentFolder), StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Prueft auch im Fehlerfall, ob ein Hintergrundlauf noch zur sichtbaren
        ''' lokalen Ansicht gehoert. So kann ein spaeter Fehler niemals den Status einer
        ''' inzwischen geoeffneten Server- oder Suchansicht ueberschreiben.</summary>
        Private Function IsCurrentFolderSync(generation As Integer, folderPath As String,
                                             thumbnailToken As CancellationToken) As Boolean
            Return generation = Volatile.Read(_folderSyncGeneration) AndAlso
                   Not thumbnailToken.IsCancellationRequested AndAlso
                   Not _isVirtualFolder AndAlso
                   String.Equals(folderPath, _currentFolder, StringComparison.OrdinalIgnoreCase)
        End Function

        Private Structure FolderSyncResult
            Public Rebuilt As List(Of ImageItem)
            Public NewItems As List(Of ImageItem)
            Public ItemsNeedingMetaRefresh As List(Of ImageItem)
        End Structure

        ''' <summary>Der reine Hintergrundteil von <see cref="SyncFolderItemsAsync"/>. Die Methode
        ''' beruehrt keine ObservableCollection und keine an die Ansicht gebundenen Eigenschaften.</summary>
        Private Function BuildFolderSyncResult(folderPath As String,
                                               thumbnailToken As CancellationToken,
                                               existing As Dictionary(Of String, ImageItem),
                                               keepParent As ImageItem,
                                               showFolders As Boolean,
                                               showParentFolder As Boolean) As FolderSyncResult
            Dim rebuilt As New List(Of ImageItem)()
            If showFolders Then
                If showParentFolder Then
                    Dim parentPath = IO.Path.GetDirectoryName(folderPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
                    If Not String.IsNullOrEmpty(parentPath) AndAlso Not IsAncestorOrSelf(folderPath, parentPath) AndAlso Directory.Exists(parentPath) Then
                        rebuilt.Add(If(keepParent, ImageItem.CreateParentFolderEntry(parentPath)))
                    End If
                End If

                For Each folder In Directory.GetDirectories(folderPath).
                    Where(Function(d) FolderNode.ShowHiddenFolders OrElse Not IO.Path.GetFileName(d).StartsWith(".")).
                    OrderBy(Function(d) IO.Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase)

                    Dim keptFolder As ImageItem = Nothing
                    rebuilt.Add(If(existing.TryGetValue(folder, keptFolder) AndAlso keptFolder.IsFolder, keptFolder, ImageItem.FromFolder(folder)))
                Next
            End If

            Dim files = EnumerateImageFiles(folderPath)
            Dim newFiles = files.Where(Function(f) Not existing.ContainsKey(f.FullName)).ToArray()
            Dim itemsNeedingMetaRefresh As New List(Of ImageItem)()
            Dim newItems = BuildFileItems(newFiles, folderPath, thumbnailToken, itemsNeedingMetaRefresh)
            Dim newItemsByPath = newItems.ToDictionary(Function(i) i.FilePath, StringComparer.OrdinalIgnoreCase)

            For Each file In files
                Dim keptFile As ImageItem = Nothing
                If existing.TryGetValue(file.FullName, keptFile) Then
                    rebuilt.Add(keptFile)
                Else
                    Dim added As ImageItem = Nothing
                    If newItemsByPath.TryGetValue(file.FullName, added) Then rebuilt.Add(added)
                End If
            Next

            Return New FolderSyncResult With {
                .Rebuilt = rebuilt,
                .NewItems = newItems,
                .ItemsNeedingMetaRefresh = itemsNeedingMetaRefresh
            }
        End Function

        Public Sub RefreshChangedFiles(paths As IEnumerable(Of String))
            Dim changed = If(paths, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
                ToHashSet(PathIdentity.Comparer)
            If changed.Count = 0 Then Return

            For Each item In _allItems.Where(Function(i) i IsNot Nothing AndAlso changed.Contains(i.FilePath))
                item.RefreshFileInfo()
                item.ClearThumbnail()
            Next

            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then SyncFolderItems()
        End Sub

        ''' Entfernt Elemente aus der Auswahl, die es nach dem Abgleich nicht mehr gibt.
        Private Sub PruneSelection(survivors As HashSet(Of ImageItem))
            If SelectedItems IsNot Nothing Then
                For i = SelectedItems.Count - 1 To 0 Step -1
                    If Not survivors.Contains(SelectedItems(i)) Then SelectedItems.RemoveAt(i)
                Next
            End If
            If SelectedItem IsNot Nothing AndAlso Not survivors.Contains(SelectedItem) Then SelectedItem = Nothing
        End Sub

        ''' <summary>Zählt die Bilddateien eines Ordners auf. DirectoryInfo statt Directory: die gelieferten
        ''' FileInfo-Objekte tragen Größe und Zeitstempel bereits aus dem Verzeichniseintrag, sodass für sie
        ''' kein eigener Stat-Aufruf mehr nötig ist (siehe ImageItem.FromFileInfo).</summary>
        Private Function EnumerateImageFiles(folderPath As String) As FileInfo()
            Return New DirectoryInfo(folderPath).
                EnumerateFiles().
                Where(Function(f) _imageExtensions.Contains(f.Extension.ToLowerInvariant())).
                ToArray()
        End Function

        ''' <summary>Die Bilddateien eines Ordners, nach Namen sortiert - für den Start ohne Bilddatei
        ''' im Betrachter (siehe MainWindowViewModel). Nutzt dieselbe Endungsliste wie die Galerie,
        ''' damit der Filmstreifen des Betrachters genau die Dateien zeigt, die auch die Galerie
        ''' zeigen würde. Leere Liste bei nicht lesbarem Ordner - der Aufrufer weicht dann aus.</summary>
        Public Function GetFolderImagePaths(folderPath As String) As List(Of String)
            If String.IsNullOrWhiteSpace(folderPath) OrElse Not Directory.Exists(folderPath) Then Return New List(Of String)()
            Try
                Return EnumerateImageFiles(folderPath).
                    Select(Function(f) f.FullName).
                    OrderBy(Function(p) p, StringComparer.OrdinalIgnoreCase).
                    ToList()
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.GetFolderImagePaths", ex)
                Return New List(Of String)()
            End Try
        End Function

        ''' <summary>Erzeugt die ImageItem-Objekte für eine Dateiliste und übernimmt die im Katalog
        ''' gespeicherten Metadaten. Trägt Elemente, deren Katalogeintrag fehlt oder veraltet ist, in
        ''' <paramref name="itemsNeedingMetaRefresh"/> ein.</summary>
        ''' Schlägt beide Namensformen in der einmalig eingelesenen Ordnerliste nach - "foto.cr2.xmp"
        ''' (angehängt) und "foto.xmp" (ersetzt). Leer, wenn es keine Beistelldatei gibt.
        Private Shared Function LookupSidecarStamp(stamps As Dictionary(Of String, String),
                                                   eigeneRezepte As Dictionary(Of String, String),
                                                   imagePath As String) As String
            Dim xmpStamp = ""
            For Each candidate In XmpSidecarService.SidecarCandidates(imagePath)
                Dim stamp As String = Nothing
                If stamps.TryGetValue(candidate, stamp) Then
                    xmpStamp = stamp
                    Exit For
                End If
            Next
            Dim fpxmpStamp = ""
            eigeneRezepte.TryGetValue(RawSidecarService.SidecarPathFor(imagePath), fpxmpStamp)
            If String.IsNullOrEmpty(xmpStamp) AndAlso String.IsNullOrEmpty(fpxmpStamp) Then Return ""
            ' Muss zeichengleich zu LibraryService.SidecarStamp sein - das ist die Gegenseite
            ' desselben Vergleichs, und ein Auseinanderlaufen liesse den Ordner bei JEDEM
            ' Wechsel komplett neu einlesen, ohne dass etwas darauf hindeutet.
            Return xmpStamp & If(String.IsNullOrEmpty(fpxmpStamp), "|-", "|fpxmp:" & fpxmpStamp)
        End Function

        ''' Nur der Ordner selbst, und case-insensitiv: unter Linux matcht das Suchmuster sonst
        ''' case-sensitiv, ".XMP" käme nicht vor (kommt bei Exporten aus Windows-Programmen aber vor).
        Private Shared ReadOnly SidecarSearchOptions As New EnumerationOptions With {
            .RecurseSubdirectories = False,
            .MatchCasing = MatchCasing.CaseInsensitive
        }

        ''' <param name="vorhandeneKatalogzeilen">Bereits geholte Katalogzeilen derselben Ordner-Ebene.
        ''' Der Ordnerwechsel gibt hier den Sofortbestand weiter; alle anderen Aufrufer lassen den
        ''' Wert offen und fragen selbst.</param>
        Private Function BuildFileItems(files As FileInfo(),
                                        folderPath As String,
                                        thumbnailToken As CancellationToken,
                                        itemsNeedingMetaRefresh As List(Of ImageItem),
                                        Optional vorhandeneKatalogzeilen As Dictionary(Of String, LibraryImageMeta) = Nothing) As List(Of ImageItem)
            If files Is Nothing OrElse files.Length = 0 Then Return New List(Of ImageItem)()
            ' Frueh aussteigen, wenn der Ordner schon gewechselt hat: was hier folgt, sind zwei
            ' Verzeichnis-Listings und der Aufbau aller Kachelobjekte - alles fuer die Katz.
            thumbnailToken.ThrowIfCancellationRequested()

            ' Nach den DATEIEN fragen, nicht nach dem Ordner. Eine Ordnerabfrage ueber
            ' "FilePath LIKE 'ordner/%'" holt auch alles aus den Unterordnern - beim Oeffnen eines
            ' reinen Elternordners waren das an Patricks Bestand 17178 Katalogzeilen zu
            ' zweiunddreissig Spalten fuer null angezeigte Bilder. Hier steht die Dateiliste schon
            ' fertig da, und ueber sie geht die Abfrage auf den Primaerschluessel.
            '
            ' Beim Ordnerwechsel entfaellt die Abfrage ganz: der Sofortbestand hat dieselbe
            ' Ordner-Ebene gerade gelesen (EnumerateImageFiles geht nicht in Unterordner, deckt
            ' sich also mit GetImagesInFolder), und zweimal dasselbe zu fragen kostete zweimal.
            Dim meta = If(vorhandeneKatalogzeilen,
                          PerformanceTraceService.Measure("Ordner: Katalogabfrage",
                              Function() LibraryService.Instance.GetMetaForPaths(files.Select(Function(f) f.FullName))))

            ''' Die XMP-Beistelldateien EINMAL für den ganzen Ordner auflisten statt je Bild zu prüfen:
            ''' die Frische-Erkennung unten braucht das Änderungsdatum der Sidecar, und zwei zusätzliche
            ''' Dateisystem-Zugriffe pro Bild summieren sich bei großen Ordnern und auf Netzwerkfreigaben
            ''' spürbar. Ein Verzeichnis-Listing kostet dagegen einmalig.
            Dim sidecarStamps As New Dictionary(Of String, String)(PathIdentity.Comparer)
            Dim eigeneRezepte As New Dictionary(Of String, String)(PathIdentity.Comparer)
            PerformanceTraceService.Measure("Ordner: Beistelldateien auflisten",
                Sub()
                    Try
                        For Each sidecar In Directory.EnumerateFiles(folderPath, "*.xmp", SidecarSearchOptions)
                            ' ".fpxmp" endet nicht auf ".xmp" und faellt hier nicht mit hinein - der Vergleich
                            ' steht trotzdem da, weil ein Treffer den Stempel still verfaelschen wuerde.
                            If sidecar.EndsWith(RawSidecarService.Extension, StringComparison.OrdinalIgnoreCase) Then Continue For
                            sidecarStamps(sidecar) = File.GetLastWriteTime(sidecar).ToString("o")
                        Next
                        ' Zweites Listing fuer die eigenen Rezepte: Vorhandensein UND Aenderungszeit gehoeren
                        ' in den Stempel. So werden extern geaenderte Katalogwerte aus .fpxmp ebenso erkannt
                        ' wie das Loeschen einer Sidecar.
                        For Each rezept In Directory.EnumerateFiles(folderPath, "*" & RawSidecarService.Extension, SidecarSearchOptions)
                            eigeneRezepte(rezept) = File.GetLastWriteTime(rezept).ToString("o")
                        Next
                    Catch
                    End Try
                End Sub)

                ''' Die FileInfo-Objekte kommen fertig befüllt aus DirectoryInfo.EnumerateFiles (siehe
                ''' ImageItem.FromFileInfo) - der frühere Weg über New ImageItem(pfad) stieß je Datei einen
                ''' zusätzlichen Stat-Aufruf an, was sich bei Ordnern mit vielen Bildern und erst recht auf
                ''' Netzwerk-/USB-Freigaben summierte. Da jedes Element hier unabhängig von den anderen ist
                ''' und noch an keine UI-gebundene Collection angehängt wurde, läuft der Aufbau parallel;
                ''' erst der abschließende Durchlauf unten geht wieder sequenziell in fester Reihenfolge.
                Dim results = New ImageItem(files.Length - 1) {}
                Dim needsRefreshFlags = New Boolean(files.Length - 1) {}
                Dim bauUhr = If(PerformanceTraceService.IsActive, Diagnostics.Stopwatch.StartNew(), Nothing)
                ' MIT DECKEL, sonst nimmt dieser Lauf dem Vorschaubild-Lader die Faeden weg. Ein
                ' Parallel.For ohne Grenze belegt alle ThreadPool-Worker, und die Lader stehen als
                ' Task.Run in derselben Schlange (ImageItem.StartThumbnailWorkersLocked). Der
                ' ThreadPool legt danach nur ein bis zwei Faeden je Sekunde nach - deshalb konnte
                ' schon die schnelle Katalogabfrage Sekunden auf einen freien Worker warten, und
                ' beim Ordnerwechsel blieben die Kacheln leer, bis dieser Lauf durch war
                ' (Nutzerbefund 2026-08-28).
                '
                ' EIN VIERTEL BLEIBT FREI, nicht eine feste Zahl: fest reserviert (vier Leser plus
                ' ein Hintergrundlader) waere ein Vierkerner auf einen einzigen Faden zurueckgefallen
                ' und haette den Ordnerwechsel selbst ausgebremst. So bleibt auf jeder Maschine
                ' etwas frei und der Lauf trotzdem parallel.
                '
                ' UND MIT ABBRUCHMARKE. Ohne sie lief dieser Lauf nach einem Ordnerwechsel BIS ZUM
                ' ENDE weiter, obwohl sein Ergebnis schon niemanden mehr interessierte - der
                ' Ordnerwechsel prueft die Marke erst hinterher. Wer aus einem grossen Ordner in
                ' einen anderen wechselte, wartete deshalb darauf, dass der ALTE Ordner fertig
                ' gebaut wird, bevor die Katalogabfrage des neuen ueberhaupt einen freien Worker
                ' bekam (Nutzerbefund 2026-08-28: "gefuehlt mehrere Sekunden, bis die ersten Bilder
                ' sichtbar werden"). Parallel.For wirft dann OperationCanceledException, und die
                ' faengt der Ordnerwechsel bereits ab.
                Dim reservedForThumbnails = Math.Max(1, Environment.ProcessorCount \ 4)
                Dim buildOptions As New ParallelOptions With {
                    .MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - reservedForThumbnails),
                    .CancellationToken = thumbnailToken
                }
                Parallel.For(0, files.Length, buildOptions,
                    Sub(i As Integer)
                        Dim file = files(i).FullName
                        Dim item = ImageItem.FromFileInfo(files(i), thumbnailToken)
                        Dim needsRefresh = False
                        Dim m As LibraryImageMeta = Nothing
                        If meta.TryGetValue(file, m) Then
                            item.IsFavorite = m.IsFavorite
                            item.Rating = m.Rating
                            item.ColorLabel = m.ColorLabel
                            item.Tags = If(m.Tags, New List(Of String)())
                            ' Nur übernehmen, wenn die Datei sich seit dem letzten EXIF-Scan nicht geändert hat -
                            ' sonst würde man dauerhaft veraltete Breite/Höhe/EXIF-Daten anzeigen (siehe IsMetaStale).
                            If m.ImageWidth.HasValue AndAlso m.ImageHeight.HasValue AndAlso
                               IsScannedSnapshotFresh(m.ScannedSourceModifiedAt, item.DateModified,
                                                      m.ScannedSidecarModifiedAt,
                                                      LookupSidecarStamp(sidecarStamps, eigeneRezepte, file)) Then
                                Dim needsMetadataFlagBackfill =
                                    Not m.HasExifMetadata AndAlso
                                    Not m.HasIptcMetadata AndAlso
                                    Not m.HasXmpMetadata AndAlso
                                    (Not String.IsNullOrWhiteSpace(m.DateTaken) OrElse
                                     Not String.IsNullOrWhiteSpace(m.DateModifiedExif) OrElse
                                     Not String.IsNullOrWhiteSpace(m.Camera) OrElse
                                     Not String.IsNullOrWhiteSpace(m.Lens) OrElse
                                     m.Aperture.HasValue OrElse
                                     m.FocalLengthMm.HasValue OrElse
                                     m.Iso.HasValue OrElse
                                     Not String.IsNullOrWhiteSpace(m.ShutterSpeed) OrElse
                                     m.GpsLatitude.HasValue OrElse
                                     m.GpsLongitude.HasValue)

                                ''' Einmalige Selbstheilung für Katalog-Einträge, deren Zusammenfassungstexte
                                ''' fehlen (Version vor den Summary-Spalten) oder aus einem älteren Format bzw.
                                ''' einer anderen Anzeigesprache stammen (SummaryFormat-Stempel). Unveränderte
                                ''' Dateien werden sonst nie wieder eingelesen - die alten Texte blieben also
                                ''' dauerhaft im Overlay stehen.
                                Dim needsSummaryBackfill =
                                    (m.HasExifMetadata AndAlso String.IsNullOrEmpty(m.ExifSummary)) OrElse
                                    (m.HasIptcMetadata AndAlso String.IsNullOrEmpty(m.IptcSummary)) OrElse
                                    (m.HasXmpMetadata AndAlso String.IsNullOrEmpty(m.XmpSummary)) OrElse
                                    (m.HasIccProfile AndAlso String.IsNullOrEmpty(m.IccSummary)) OrElse
                                    Not String.Equals(m.SummaryFormat, ExifService.CurrentSummaryFormat, StringComparison.Ordinal)

                                item.ImageWidth = m.ImageWidth.Value
                                item.ImageHeight = m.ImageHeight.Value
                                item.ExifDateTaken = ExifService.ParseExifDateTime(m.DateTaken)
                                item.ExifDateModified = ExifService.ParseExifDateTime(m.DateModifiedExif)
                                item.ExifCamera = m.Camera
                                item.ExifIso = m.Iso
                                item.ExifAperture = m.Aperture
                                item.HasExifMetadata = m.HasExifMetadata
                                item.HasIptcMetadata = m.HasIptcMetadata
                                item.HasXmpMetadata = m.HasXmpMetadata
                                item.HasIccProfile = m.HasIccProfile
                                item.ExifMetadataSummary = m.ExifSummary
                                item.IptcMetadataSummary = m.IptcSummary
                                item.XmpMetadataSummary = m.XmpSummary
                                item.IccMetadataSummary = m.IccSummary
                                needsRefresh = needsMetadataFlagBackfill OrElse needsSummaryBackfill
                            Else
                                needsRefresh = True
                            End If
                        Else
                            needsRefresh = True
                        End If
                        results(i) = item
                        needsRefreshFlags(i) = needsRefresh
                    End Sub)
                If bauUhr IsNot Nothing Then
                    PerformanceTraceService.Record("Ordner: Kachelobjekte bauen", bauUhr.Elapsed.TotalMilliseconds)
                    PerformanceTraceService.Record("Ordner: Bilder im Ordner", files.Length)
                End If

            Dim built As New List(Of ImageItem)(results.Length)
            For i = 0 To results.Length - 1
                built.Add(results(i))
                If needsRefreshFlags(i) Then itemsNeedingMetaRefresh.Add(results(i))
            Next
            Return built
        End Function

        Private Function BeginNewFolderThumbnailScope() As CancellationToken
            _thumbnailLoadCts.Cancel()
            ClearLoadedThumbnails()
            _thumbnailLoadCts.Dispose()
            _thumbnailLoadCts = New CancellationTokenSource()
            Return _thumbnailLoadCts.Token
        End Function

        Private Sub ClearLoadedThumbnails()
            Dim seen As New HashSet(Of ImageItem)()
            For Each item In Items.Concat(DisplayItems).Concat(_allItems)
                If item Is Nothing OrElse Not seen.Add(item) Then Continue For
                item.ClearThumbnail()
            Next
        End Sub

        ''' <summary>Baut die Segmente der Zeitleiste am rechten Galerierand passend zur AKTUELLEN
        ''' Sortierung aus Items: Datums-Sortierungen liefern Monats-Segmente (Grob-Marker nur beim
        ''' Jahreswechsel), Namens-Sortierung Anfangsbuchstaben. Nothing bei Sortierungen ohne
        ''' sinnvolle Achse (Größe, ISO, ...) - die View blendet die Leiste dann aus. Ein Durchlauf
        ''' über die Liste (O(n), auch bei 30k Immich-Assets unkritisch); Ordner-Einträge am
        ''' Listenanfang zählen für die Positionen mit, bekommen aber kein Segment.</summary>
        Public Function BuildTimelineSegments() As List(Of Controls.GalleryTimelineSegment)
            If Items Is Nothing OrElse Items.Count = 0 Then Return Nothing

            Dim dateSelector As Func(Of ImageItem, DateTime?) = Nothing
            Dim byName = False
            Select Case _sortMode
                Case "FileModifiedAt" : dateSelector = Function(i) i.DateModified
                Case "FileCreatedAt" : dateSelector = Function(i) i.FileCreatedAt
                Case "ExifDateTaken" : dateSelector = Function(i) i.ExifDateTaken
                Case "ExifDateModified" : dateSelector = Function(i) i.ExifDateModified
                Case "Name" : byName = True
                Case Else
                    Return Nothing
            End Select

            Dim segments As New List(Of Controls.GalleryTimelineSegment)()
            Dim lastMonthKey As Integer = Integer.MinValue
            Dim lastYear As Integer = Integer.MinValue
            Dim lastLetter As String = Nothing
            For i = 0 To Items.Count - 1
                Dim item = Items(i)
                If item Is Nothing OrElse item.IsFolder OrElse item.IsParentFolderEntry Then Continue For

                If byName Then
                    Dim name = If(item.FileName, "")
                    Dim letter = If(name.Length > 0 AndAlso Char.IsLetter(name(0)),
                                    name.Substring(0, 1).ToUpperInvariant(), "#")
                    If Not String.Equals(letter, lastLetter, StringComparison.Ordinal) Then
                        lastLetter = letter
                        segments.Add(New Controls.GalleryTimelineSegment With {
                            .Label = letter, .DetailLabel = letter, .StartIndex = i})
                    End If
                Else
                    Dim value = dateSelector(item)
                    If Not value.HasValue OrElse value.Value = DateTime.MinValue Then
                        If lastMonthKey <> -1 Then
                            lastMonthKey = -1
                            segments.Add(New Controls.GalleryTimelineSegment With {
                                .Label = "", .DetailLabel = LocalizationService.T("Ohne Datum"), .StartIndex = i})
                        End If
                        Continue For
                    End If
                    Dim monthKey = value.Value.Year * 12 + value.Value.Month
                    If monthKey <> lastMonthKey Then
                        lastMonthKey = monthKey
                        Dim label = If(value.Value.Year <> lastYear, value.Value.Year.ToString(Globalization.CultureInfo.CurrentUICulture), "")
                        lastYear = value.Value.Year
                        segments.Add(New Controls.GalleryTimelineSegment With {
                            .Label = label,
                            .DetailLabel = value.Value.ToString("MMMM yyyy", Globalization.CultureInfo.CurrentUICulture),
                            .StartIndex = i})
                    End If
                End If
            Next
            Return If(segments.Count > 0, segments, Nothing)
        End Function

        Public Sub SetDisplayWindow(firstIndex As Integer, lastIndex As Integer, itemSlotHeight As Double, columns As Integer)
            If Items Is Nothing OrElse Items.Count = 0 Then
                DisplayItems.Clear()
                _displayWindowFirst = -1
                _displayWindowLast = -1
                TopSpacerHeight = 0
                BottomSpacerHeight = 0
                ContentHeight = 0
                Return
            End If

            columns = Math.Max(1, columns)
            itemSlotHeight = Math.Max(1, itemSlotHeight)

            firstIndex = Math.Max(0, Math.Min(firstIndex, Items.Count - 1))
            lastIndex = Math.Max(firstIndex, Math.Min(lastIndex, Items.Count - 1))

            ' Die zuletzt von der Ansicht gemeldete Fenstergeometrie merken, damit das ViewModel das
            ' Fenster nach einer Aenderung der Liste selbst nachziehen kann (siehe RefreshDisplayWindow).
            _lastWindowFirst = firstIndex
            _lastWindowLast = lastIndex
            _lastWindowSlotHeight = itemSlotHeight
            _lastWindowColumns = columns

            If columns > 1 Then
                Dim firstRow = firstIndex \ columns
                Dim lastRow = lastIndex \ columns
                firstIndex = firstRow * columns
                lastIndex = Math.Min(Items.Count - 1, ((lastRow + 1) * columns) - 1)
            End If

            Dim topRows = firstIndex \ columns
            Dim totalRows = CInt(Math.Ceiling(Items.Count / CDbl(columns)))
            Dim remainingItems = Math.Max(0, Items.Count - lastIndex - 1)
            Dim bottomRows = CInt(Math.Ceiling(remainingItems / CDbl(columns)))
            TopSpacerHeight = topRows * itemSlotHeight
            BottomSpacerHeight = bottomRows * itemSlotHeight
            ContentHeight = totalRows * itemSlotHeight

            ' Messpunkt (nur bei eingeschaltetem Diagnoselog): der Fensterwechsel ist die DATENSEITE
            ' des Rollens. Was danach noch kostet, ist der Aufbau der Kacheln - der steht im
            ' Waechter ueber dem Anzeigefaden.
            If PerformanceTraceService.IsActive Then
                PerformanceTraceService.Measure("Galerie Fensterwechsel",
                                                Sub() ApplyDisplayWindow(Items, firstIndex, lastIndex))
            Else
                ApplyDisplayWindow(Items, firstIndex, lastIndex)
            End If
        End Sub

        ''' <summary>Das Anzeigefenster auf einen Ausschnitt der Quelle stellen. Quelle ist in Raster und
        ''' Liste die Elementliste, in der Gruppenansicht die Anzeigereihenfolge mit den Kopfzeilen.</summary>
        Private Sub ApplyDisplayWindow(source As IList(Of ImageItem), firstIndex As Integer, lastIndex As Integer)
            If _displayWindowFirst = firstIndex AndAlso _displayWindowLast = lastIndex Then Return

            ' ZWEI WEGE - UND WELCHER BILLIGER IST, HAENGT DARAN, WIE WEIT DAS FENSTER RUECKT.
            '
            ' Am Rand tauschen, wenn sich das Fenster nur verschiebt: beim Rollen um wenige Zeilen
            ' bleiben die allermeisten Elemente dieselben, sie stehen nur an anderer Stelle. Dann
            ' werden vorn und hinten so viele Plaetze entfernt und angehaengt, wie das Fenster
            ' wirklich weitergerueckt ist - alles dazwischen behaelt seine fertig aufgebaute
            ' Kachel. Beim Rollen um eine Zeile sind das fuenf Aenderungen statt sechzig
            ' Neuaufbauten (Nutzerbefund 2026-08-28: Position fuer Position zu ersetzen brachte das
            ' Rollen "mehr ins Stocken").
            '
            ' Ganz zuruecksetzen, wenn das neue Fenster mit dem alten NICHTS zu tun hat - beim
            ' Sprung am Regler. Dann fuehrt kein Weg an einem Neuaufbau vorbei, und EIN
            ' Zuruecksetzen ist dafuer der billigste.
            '
            ' WAS DAMIT NICHT GELOEST IST: jede einzelne Aenderung an DisplayItems wird fuer sich
            ' gemeldet, und das nicht virtualisierende WrapPanel des Rasters wiederholt daraufhin
            ' sein Layout ueber alle Kacheln. Ein Rollschritt ueber zwei Zeilen bei sechs Spalten
            ' sind vierundzwanzig Meldungen und damit vierundzwanzig Layoutlaeufe; am echten
            ' Fenster gemessen bis zu 350 ms (Nutzerprotokoll 2026-08-27, 1000 Bilder). Die
            ' LISTENansicht hat das seit der Umstellung auf den ItemsRepeater nicht mehr - der
            ' verarbeitet einzelne Aenderungen guenstig. Fuer das Raster bleibt es offen; ein
            ' Buendeln zu einer einzigen Meldung hilft dort nicht, weil eine Ruecksetzung genau den
            ' Neuaufbau ausloest, den der Randtausch vermeiden soll. Der Weg waere, den reinen
            ' Rasterpfad von der Gruppenansicht zu trennen und ihn ueber einen ItemsRepeater mit
            ' UniformGridLayout laufen zu lassen - ohne Kopfzeilen sind die Kacheln gleich gross.
            ' Das gehoert am echten Fenster gemessen, bevor es umgebaut wird.
            Dim hasOverlap = _displayWindowFirst >= 0 AndAlso firstIndex <= _displayWindowLast AndAlso lastIndex >= _displayWindowFirst
            ' Messpunkt: die beiden Wege bleiben getrennt zu sehen, damit sich am Protokoll ablesen
            ' lässt, welcher wie oft läuft und was er kostet.
            Dim messpunkt = If(hasOverlap, "Fenster: Kacheln tauschen", "Fenster: ganz ersetzen")
            Dim messuhr = If(PerformanceTraceService.IsActive, Diagnostics.Stopwatch.StartNew(), Nothing)
            ' Die ALTEN Grenzen werden unten noch gebraucht: der Randtausch rechnet aus, wie weit das
            ' Fenster gerückt ist. Gesetzt werden sie deshalb erst am Ende.
            Dim slice = source.Skip(firstIndex).Take(lastIndex - firstIndex + 1).ToList()
            Dim overlaps = hasOverlap AndAlso DisplayItems.Count = _displayWindowLast - _displayWindowFirst + 1
            Dim geaendert = 0
            If overlaps Then
                While _displayWindowFirst < firstIndex
                    DisplayItems.RemoveAt(0)
                    _displayWindowFirst += 1
                    geaendert += 1
                End While
                While _displayWindowLast > lastIndex
                    DisplayItems.RemoveAt(DisplayItems.Count - 1)
                    _displayWindowLast -= 1
                    geaendert += 1
                End While
                Dim insertAt = 0
                For i = firstIndex To _displayWindowFirst - 1
                    DisplayItems.Insert(insertAt, source(i))
                    insertAt += 1
                    geaendert += 1
                Next
                For i = _displayWindowLast + 1 To lastIndex
                    DisplayItems.Add(source(i))
                    geaendert += 1
                Next
            ElseIf Not DisplayItems.SequenceEqual(slice) Then
                DisplayItems.ReplaceAll(slice)
                geaendert = slice.Count
            End If
            _displayWindowFirst = firstIndex
            _displayWindowLast = lastIndex
            If messuhr IsNot Nothing Then
                PerformanceTraceService.Record("Fenster: geaenderte Plaetze", geaendert)
                PerformanceTraceService.Record("Fenster: Plaetze insgesamt", slice.Count)
            End If
            If messuhr IsNot Nothing Then PerformanceTraceService.Record(messpunkt, messuhr.Elapsed.TotalMilliseconds)
        End Sub

        ''' <summary>Baut das angezeigte Fenster mit der zuletzt gemeldeten Geometrie neu auf. Noetig nach
        ''' jeder Aenderung an <see cref="Items"/>: die Kacheln binden an DisplayItems, und das fuellt sonst
        ''' erst das naechste Viewport-Ereignis der Ansicht nach.</summary>
        Private Sub RefreshDisplayWindow()
            If Items Is Nothing OrElse DisplayItems Is Nothing Then Return

            ' DAS RASTER HAT KEIN ANZEIGEFENSTER MEHR. Dort haengt der Repeater direkt an Items und
            ' virtualisiert selbst (siehe GalleryView.axaml). DisplayItems bleibt hier leer - es zu
            ' fuellen kostete bei jedem Filter- und Sortierlauf, ohne dass es je jemand ansieht.
            ' Liste und Gruppenansicht brauchen das Fenster weiterhin.
            If IsGridView Then
                If DisplayItems.Count > 0 Then DisplayItems.Clear()
                _displayWindowFirst = -1
                _displayWindowLast = -1
                Return
            End If

            If Items.Count = 0 Then
                If DisplayItems.Count > 0 Then DisplayItems.Clear()
                Return
            End If

            If IsGroupView Then
                ' Vor der ersten Messung steht keine Fenstergeometrie zur Verfuegung; dann gilt dasselbe
                ' wie im Raster - das Anfangsfenster stellen und auf die erste Meldung der Ansicht warten.
                If _lastGroupViewportHeight <= 0 OrElse _groupLayoutColumns <= 0 Then
                    ResetDisplayWindow()
                Else
                    SetGroupDisplayWindow(_lastGroupOffsetY, _lastGroupViewportHeight, _groupLayoutSlotHeight, _groupLayoutColumns)
                End If
                Return
            End If
            ' Vor dem ersten Layout gibt es keine Fenstergeometrie - dann steht im Anzeigefenster das
            ' Anfangsfenster (ResetDisplayWindow), und genau das muss hier nachgezogen werden. Sonst
            ' bliebe eine geloeschte Kachel stehen, solange die Ansicht noch nie gescrollt wurde.
            If _lastWindowColumns <= 0 OrElse _lastWindowSlotHeight <= 0 Then
                Dim initial = Items.Take(Math.Min(120, Items.Count)).ToList()
                If Not DisplayItems.SequenceEqual(initial) Then ResetDisplayWindow()
                Return
            End If
            SetDisplayWindow(_lastWindowFirst, _lastWindowLast, _lastWindowSlotHeight, _lastWindowColumns)
        End Sub

        Private Sub ResetDisplayWindow()
            _displayWindowFirst = -1
            _displayWindowLast = -1
            TopSpacerHeight = 0
            BottomSpacerHeight = 0
            ContentHeight = 0
            If Items Is Nothing OrElse DisplayItems Is Nothing Then Return

            ' Auch hier: das Raster hat kein Anzeigefenster mehr (siehe RefreshDisplayWindow). Der
            ' Riegel muss an BEIDEN Stellen stehen - FilterAndSort ruft bei leerem Fenster nicht
            ' RefreshDisplayWindow, sondern genau diese Methode, und die haette es sofort wieder
            ' gefuellt.
            If IsGridView Then
                If DisplayItems.Count > 0 Then DisplayItems.Clear()
                Return
            End If

            If IsGroupView Then
                EnsureGroupEntries()
                DisplayItems.ReplaceAll(_groupLayout.Take(Math.Min(120, _groupLayout.Count)))
                If DisplayItems.Count > 0 Then
                    _displayWindowFirst = 0
                    _displayWindowLast = DisplayItems.Count - 1
                End If
                Return
            End If

            DisplayItems.ReplaceAll(Items.Take(Math.Min(120, Items.Count)))

            ' Das gefüllte Fenster mitführen. Sonst hält der nächste SetDisplayWindow-Aufruf (er kommt
            ' vom ersten Layout-Durchlauf der Ansicht) die Grenzen für unbekannt und ersetzt DisplayItems
            ' per ReplaceAll durch exakt dieselben Elemente. Das Reset-Ereignis baut alle Kacheln neu auf,
            ' und der ItemsControl bleibt danach mit Höhe 0 stehen - die Galerie wirkt leer, bis ein
            ' Ordnerwechsel sie neu aufbaut.
            If DisplayItems.Count > 0 Then
                _displayWindowFirst = 0
                _displayWindowLast = DisplayItems.Count - 1
            End If
        End Sub

        Private Sub FilterAndSort()
            Dim filtered = _allItems.AsEnumerable()

            ' Mehrere asynchrone Quellen dürfen denselben Treffer melden (Katalog-Wiederherstellung
            ' und nachfolgender Dateisystemlauf einer Suchliste). Die Anzeige besitzt deshalb eine
            ' letzte, pfadbasierte Sicherung; sie hält auch dann genau eine Kachel, wenn die beiden
            ' Quellen unterschiedliche Groß-/Kleinschreibung des gleichen Pfads liefern.
            '
            ' Ein HashSet statt GroupBy: FilterAndSort laeuft bei jeder Filter- und
            ' Sortieraenderung ueber den ganzen Bestand, und GroupBy legt dafuer je Pfad eine
            ' eigene Liste an - bei 30000 Bildern also 30000 Listen fuer eine Frage, die ein
            ' Merkposten beantwortet. Das ToList macht den Durchlauf einmalig; ohne es traege die
            ' Kette einen Merkposten mit sich, der beim zweiten Durchlaufen alles verwuerfe.
            Dim gesehenePfade As New HashSet(Of String)(PathIdentity.Comparer)
            filtered = filtered.
                Where(Function(i) i IsNot Nothing AndAlso gesehenePfade.Add(If(i.FilePath, ""))).
                ToList()

            If Not String.IsNullOrEmpty(_searchText) Then
                filtered = filtered.Where(Function(i) i.SearchText.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            End If

            If _filterFavorite = "Only" Then
                filtered = filtered.Where(Function(i) i.IsFolder OrElse i.IsFavorite)
            End If

            If _filterColorLabels.Count > 0 Then
                filtered = filtered.Where(Function(i) i.IsFolder OrElse _filterColorLabels.Contains(i.ColorLabel))
            End If
            If _filterRatings.Count > 0 Then
                filtered = filtered.Where(Function(i) i.IsFolder OrElse _filterRatings.Contains(i.Rating))
            End If

            If _filterFileType = "Raw" Then
                filtered = filtered.Where(Function(i) i.IsFolder OrElse _rawExtensions.Contains(i.ExtensionLower))
            ElseIf _filterFileType = "NonRaw" Then
                filtered = filtered.Where(Function(i) i.IsFolder OrElse Not _rawExtensions.Contains(i.ExtensionLower))
            End If

            Items.ReplaceAll(SortItems(filtered))
            If DisplayItems.Count = 0 Then
                ResetDisplayWindow()
            Else
                _displayWindowFirst = -1
                _displayWindowLast = -1
                ' Die Kacheln haengen an DisplayItems, nicht an Items: ohne diesen Neuaufbau blieb ein
                ' geloeschtes Bild stehen, bis das naechste Viewport-Ereignis (Scrollen, Groessenaenderung)
                ' SetDisplayWindow rief. Das Fenster wird nur ersetzt, wenn sich sein Inhalt wirklich
                ' geaendert hat - waehrend einer laufenden Suche kaeme sonst pro Stapel ein voller
                ' Neuaufbau der sichtbaren Kacheln.
                RefreshDisplayWindow()
            End If
            Me.RaisePropertyChanged(NameOf(FooterStatusText))

            ' Ein Durchlauf für beide Zahlen statt zweier - die Liste ist hier bereits gefiltert und
            ' sortiert, sie noch zweimal komplett abzugehen ist reine Zugabe.
            Dim imageCount = 0
            Dim folderCount = 0
            For Each item In Items
                If item.IsImage Then
                    imageCount += 1
                ElseIf item.IsFolder AndAlso Not item.IsParentFolderEntry Then
                    folderCount += 1
                End If
            Next
            If _isVirtualFolder Then
                StatusText = $"{imageCount} {LocalizationService.T("Bilder")}  •  {CurrentFolderName}"
            Else
                StatusText = $"{imageCount} {LocalizationService.T("Bilder")}  •  {folderCount} {LocalizationService.T("Ordner")}  •  {CurrentFolderName}"
            End If
        End Sub

        Private Function SortItems(items As IEnumerable(Of ImageItem)) As IEnumerable(Of ImageItem)
            Dim parent = items.Where(Function(i) i.IsParentFolderEntry).ToList()
            Dim folders = items.
                Where(Function(i) Not i.IsParentFolderEntry AndAlso i.IsFolder).
                OrderBy(Function(i) i.FileName, StringComparer.CurrentCultureIgnoreCase)
            Dim contentItems = items.Where(Function(i) Not i.IsParentFolderEntry AndAlso Not i.IsFolder)

            Select Case _sortMode
                Case "FileModifiedAt"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.DateModified).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.DateModified).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case "FileCreatedAt"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.FileCreatedAt).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.FileCreatedAt).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case "ExifDateTaken"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.ExifDateTaken.GetValueOrDefault(DateTime.MinValue)).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.ExifDateTaken.GetValueOrDefault(DateTime.MinValue)).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case "ExifDateModified"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.ExifDateModified.GetValueOrDefault(DateTime.MinValue)).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.ExifDateModified.GetValueOrDefault(DateTime.MinValue)).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case "Width"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.ImageWidth).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.ImageWidth).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case "Height"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.ImageHeight).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.ImageHeight).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case "Camera"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.ExifCamera).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.ExifCamera).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case "Iso"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.ExifIso.GetValueOrDefault(0)).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.ExifIso.GetValueOrDefault(0)).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case "Aperture"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.ExifAperture.GetValueOrDefault(0.0)).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.ExifAperture.GetValueOrDefault(0.0)).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case "Size"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.FileSize).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.FileSize).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case "Type"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) IO.Path.GetExtension(i.FilePath)).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) IO.Path.GetExtension(i.FilePath)).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case "Rating"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.Rating).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.Rating).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case "Favorite"
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.IsFavorite).ThenBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.IsFavorite).ThenBy(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
                Case Else
                    Dim sorted = If(_sortAscending,
                                    contentItems.OrderBy(Function(i) i.FileName),
                                    contentItems.OrderByDescending(Function(i) i.FileName))
                    Return parent.Concat(folders).Concat(sorted)
            End Select
        End Function

        ' ---------------------------------------------------------------------------------------------
        ' Gruppenansicht
        '
        ' Raster und Liste rechnen mit EINER festen Zeilenhoehe: Zeile = Index geteilt durch Spaltenzahl,
        ' Gesamthoehe = Zeilen mal Slothoehe. Mit Kopfzeilen dazwischen stimmt diese Formel nicht mehr,
        ' deshalb fuehrt die Gruppenansicht eine Zeilentabelle: je Zeile ihre Lage und ihre Hoehe. Die
        ' Ansicht meldet nur noch Scrollversatz und Sichthoehe, das Uebersetzen in ein Anzeigefenster
        ' passiert hier.
        '
        ' Items bleibt dabei unberuehrt und enthaelt weiterhin ausschliesslich echte Eintraege. Die
        ' Kopfzeilen stehen nur in _groupLayout und damit im Anzeigefenster; alles, was mit Auswahl,
        ' Loeschen oder dem Betrachter zu tun hat, sieht sie nie.
        ' ---------------------------------------------------------------------------------------------

        ''' <summary>Eine Zeile der Gruppenansicht: entweder eine Kopfzeile oder eine Zeile Kacheln.
        ''' First und Count zeigen in _groupLayout, Top und Height sind Bildpunkte im Inhalt.</summary>
        Private Structure GroupLayoutRow
            Public First As Integer
            Public Count As Integer
            Public Top As Double
            Public Height As Double
        End Structure

        ''' <summary>Hoehe einer Kopfzeile. Muss mit der Hoehe im XAML uebereinstimmen (Height am Border
        ''' der Kopfzeile, ohne senkrechten Rand) - sonst driftet die Scrollrechnung mit der Scrolltiefe,
        ''' genau wie es bei der Slothoehe der Kacheln schon einmal passiert ist.</summary>
        Public Const GroupHeaderRowHeight As Double = 48

        ''' <summary>Breite einer Kopfzeile: genau eine volle Kachelzeile. Damit belegt sie im WrapPanel
        ''' immer eine eigene Zeile, ohne dass die Ansicht ihre Breite zurueckmelden muss.</summary>
        Public ReadOnly Property GroupHeaderWidth As Double
            Get
                Return Math.Max(1, _groupLayoutColumns) * GridColumnPitch
            End Get
        End Property

        ''' <summary>Merkt vor, dass Gruppen und Zeilentabelle neu gebaut werden muessen. Wird bei jeder
        ''' Aenderung an Items gerufen (ueber das Ereignis der Sammlung), bei einem Sortierwechsel und
        ''' beim Umstellen der Feinheit.</summary>
        Public Sub InvalidateGroupLayout()
            _groupEntriesDirty = True
            _groupRows.Clear()
            ' Das Anzeigefenster zeigt mit Zahlen in die Layoutliste. Wird die neu gebaut, stehen an
            ' denselben Stellen ANDERE Eintraege - das naechste Fenster darf dann nicht ueber die
            ' Delta-Fassung entstehen, die nur Raender abschneidet und anfuegt. Sonst blieben
            ' Kopfzeilen von vorhin stehen (gemessen beim Umstellen von Tag auf Monat: die Ansicht
            ' zeigte weiter zwei Kopfzeilen statt einer). Raster und Liste bleiben aussen vor: dort
            ' haelt FilterAndSort dieselbe Regel schon selbst, und ein Ruecksetzen bei JEDER Aenderung
            ' an Items wuerde beim Einlesen eines Ordners jeden Stapel zu einem vollen Neuaufbau der
            ' sichtbaren Kacheln machen.
            If IsGroupView Then
                _displayWindowFirst = -1
                _displayWindowLast = -1
            End If
        End Sub

        ''' <summary>True, wenn die aktuelle Sortierung sinnvolle Gruppen hergibt. Bei Groesse, ISO,
        ''' Blende und den Abmessungen zeigt die Gruppenansicht ein durchgehendes Raster ohne
        ''' Kopfzeilen - Gruppen ueber Zahlenwerte waeren Rauschen.</summary>
        Public Function GroupingIsAvailable() As Boolean
            Select Case _sortMode
                Case "FileModifiedAt", "FileCreatedAt", "ExifDateTaken", "ExifDateModified",
                     "Name", "Camera", "Type", "Rating", "Favorite"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        ''' <summary>Der Gruppenschluessel eines Eintrags. Ordner bilden immer eine eigene erste Gruppe:
        ''' sie stehen in jeder Sortierung vorn und haben mit dem Sortierkriterium der Bilder nichts zu
        ''' tun.</summary>
        Private Function GroupKeyFor(item As ImageItem) As String
            If item Is Nothing Then Return "?"
            If item.IsFolder Then Return "folders"

            Select Case _sortMode
                Case "FileModifiedAt" : Return DateGroupKey(item.DateModified)
                Case "FileCreatedAt" : Return DateGroupKey(item.FileCreatedAt)
                Case "ExifDateTaken" : Return DateGroupKey(item.ExifDateTaken)
                Case "ExifDateModified" : Return DateGroupKey(item.ExifDateModified)
                Case "Name"
                    Dim name = If(item.FileName, "")
                    If name.Length = 0 OrElse Not Char.IsLetter(name(0)) Then Return "name:#"
                    Return "name:" & name.Substring(0, 1).ToUpperInvariant()
                Case "Camera"
                    Return "cam:" & If(item.ExifCamera, "").Trim().ToUpperInvariant()
                Case "Type"
                    Return "type:" & If(item.ExtensionLower, "")
                Case "Rating"
                    Return "rating:" & item.Rating.ToString(Globalization.CultureInfo.InvariantCulture)
                Case "Favorite"
                    Return If(item.IsFavorite, "fav:1", "fav:0")
                Case Else
                    Return "all"
            End Select
        End Function

        Private Function DateGroupKey(value As DateTime?) As String
            If Not value.HasValue OrElse value.Value = DateTime.MinValue Then Return "date:none"
            Select Case _groupDateStep
                Case "Month" : Return "date:" & value.Value.ToString("yyyyMM", Globalization.CultureInfo.InvariantCulture)
                Case "Year" : Return "date:" & value.Value.ToString("yyyy", Globalization.CultureInfo.InvariantCulture)
                Case Else : Return "date:" & value.Value.ToString("yyyyMMdd", Globalization.CultureInfo.InvariantCulture)
            End Select
        End Function

        ''' <summary>Die Beschriftung einer Gruppe, gebildet aus ihrem ersten Eintrag. Sie wird nur
        ''' einmal je Gruppe gebraucht, deshalb steht sie nicht im Schluessel.</summary>
        Private Function GroupTitleFor(item As ImageItem) As String
            If item Is Nothing Then Return ""
            If item.IsFolder Then Return LocalizationService.T("Ordner")

            Select Case _sortMode
                Case "FileModifiedAt" : Return DateGroupTitle(item.DateModified)
                Case "FileCreatedAt" : Return DateGroupTitle(item.FileCreatedAt)
                Case "ExifDateTaken" : Return DateGroupTitle(item.ExifDateTaken)
                Case "ExifDateModified" : Return DateGroupTitle(item.ExifDateModified)
                Case "Name"
                    Dim name = If(item.FileName, "")
                    If name.Length = 0 OrElse Not Char.IsLetter(name(0)) Then Return "#"
                    Return name.Substring(0, 1).ToUpperInvariant()
                Case "Camera"
                    Dim camera = If(item.ExifCamera, "").Trim()
                    Return If(camera.Length > 0, camera, LocalizationService.T("Ohne Kameraangabe"))
                Case "Type"
                    Dim ext = If(item.ExtensionLower, "").TrimStart("."c)
                    Return If(ext.Length > 0, ext.ToUpperInvariant(), LocalizationService.T("Ohne Dateiendung"))
                Case "Rating"
                    If item.Rating <= 0 Then Return LocalizationService.T("Ohne Bewertung")
                    If item.Rating = 1 Then Return "1 " & LocalizationService.T("Stern")
                    Return item.Rating.ToString(Globalization.CultureInfo.CurrentUICulture) & " " & LocalizationService.T("Sterne")
                Case "Favorite"
                    Return If(item.IsFavorite, LocalizationService.T("Favoriten"), LocalizationService.T("Kein Favorit"))
                Case Else
                    Return ""
            End Select
        End Function

        Private Function DateGroupTitle(value As DateTime?) As String
            If Not value.HasValue OrElse value.Value = DateTime.MinValue Then Return LocalizationService.T("Ohne Datum")
            Dim culture = Globalization.CultureInfo.CurrentUICulture
            Select Case _groupDateStep
                Case "Month" : Return value.Value.ToString("MMMM yyyy", culture)
                Case "Year" : Return value.Value.ToString("yyyy", culture)
                Case Else : Return value.Value.ToString("D", culture)
            End Select
        End Function

        ''' <summary>Baut Kopfzeilen und Elemente in Anzeigereihenfolge auf. Ein Durchlauf ueber Items;
        ''' die Gruppen sind zusammenhaengend, weil sie aus derselben Sortierung stammen, nach der die
        ''' Liste bereits geordnet ist.</summary>
        Private Sub EnsureGroupEntries()
            If Not _groupEntriesDirty Then Return
            _groupEntriesDirty = False
            _groupLayout.Clear()
            _groupLayoutItemIndex.Clear()
            _itemToGroupEntry = Array.Empty(Of Integer)()
            If Items Is Nothing OrElse Items.Count = 0 Then Return

            Dim entryOfItem(Items.Count - 1) As Integer

            If Not GroupingIsAvailable() Then
                For i = 0 To Items.Count - 1
                    entryOfItem(i) = _groupLayout.Count
                    _groupLayout.Add(Items(i))
                    _groupLayoutItemIndex.Add(i)
                Next
                _itemToGroupEntry = entryOfItem
                Return
            End If

            Dim keys(Items.Count - 1) As String
            For i = 0 To Items.Count - 1
                keys(i) = GroupKeyFor(Items(i))
            Next

            Dim groupStart = 0
            While groupStart < Items.Count
                Dim groupEnd = groupStart
                While groupEnd + 1 < Items.Count AndAlso String.Equals(keys(groupEnd + 1), keys(groupStart), StringComparison.Ordinal)
                    groupEnd += 1
                End While

                ' Der Eintrag zum uebergeordneten Ordner zaehlt nicht mit: er ist ein Weg nach oben,
                ' kein Ordner dieser Ansicht.
                Dim countable = 0
                For i = groupStart To groupEnd
                    If Items(i).IsSelectableEntry Then countable += 1
                Next

                _groupLayout.Add(ImageItem.CreateGroupHeader(GroupTitleFor(Items(groupStart)),
                                                             GroupCountText(Items(groupStart).IsFolder, countable)))
                _groupLayoutItemIndex.Add(-1)
                For i = groupStart To groupEnd
                    entryOfItem(i) = _groupLayout.Count
                    _groupLayout.Add(Items(i))
                    _groupLayoutItemIndex.Add(i)
                Next
                groupStart = groupEnd + 1
            End While
            _itemToGroupEntry = entryOfItem
            ' Die Kopfzeilen sind frisch und wissen nichts von der bestehenden Auswahl - sie ueberlebt
            ' zum Beispiel einen Filterwechsel.
            RefreshGroupHeaderSelection()
        End Sub

        Private Function GroupCountText(isFolderGroup As Boolean, count As Integer) As String
            ' Kein "0 Ordner": in einer Gruppe, die nur den Weg nach oben enthaelt, saehe das nach einem
            ' Fehler aus. Dann bleibt die Zeile rechts einfach leer.
            If count <= 0 Then Return ""
            If isFolderGroup Then Return count & " " & LocalizationService.T("Ordner")
            If count = 1 Then Return "1 " & LocalizationService.T("Bild")
            Return count & " " & LocalizationService.T("Bilder")
        End Function

        ''' <summary>Baut die Zeilentabelle zur gemeldeten Spaltenzahl und Slothoehe. Die Spaltenzahl
        ''' aendert sich mit der Fensterbreite und mit der Kachelgroesse, deshalb haengt die Tabelle an
        ''' beiden und wird bei einer Aenderung neu aufgebaut.</summary>
        Private Sub EnsureGroupRows(columns As Integer, itemSlotHeight As Double)
            columns = Math.Max(1, columns)
            itemSlotHeight = Math.Max(1, itemSlotHeight)
            EnsureGroupEntries()

            If _groupRows.Count > 0 AndAlso _groupLayoutColumns = columns AndAlso
               Math.Abs(_groupLayoutSlotHeight - itemSlotHeight) < 0.01 Then
                ' Die Gesamthoehe gehoert zur Tabelle und wird hier mitgefuehrt. Sie steht sonst noch
                ' auf dem Wert, den das Anfangsfenster gesetzt hat (0) - der untere Platzhalter
                ' rechnete damit gegen eine leere Flaeche.
                ContentHeight = _groupContentHeight
                Return
            End If

            Dim columnsChanged = _groupLayoutColumns <> columns
            _groupLayoutColumns = columns
            _groupLayoutSlotHeight = itemSlotHeight
            _groupRows.Clear()
            If columnsChanged Then Me.RaisePropertyChanged(NameOf(GroupHeaderWidth))
            If _groupLayout.Count = 0 Then
                _groupContentHeight = 0
                ContentHeight = 0
                Return
            End If

            Dim top = 0.0
            Dim i = 0
            While i < _groupLayout.Count
                If _groupLayout(i).IsGroupHeader Then
                    _groupRows.Add(New GroupLayoutRow With {.First = i, .Count = 1, .Top = top, .Height = GroupHeaderRowHeight})
                    top += GroupHeaderRowHeight
                    i += 1
                Else
                    Dim runEnd = i
                    While runEnd < _groupLayout.Count AndAlso Not _groupLayout(runEnd).IsGroupHeader
                        runEnd += 1
                    End While
                    While i < runEnd
                        Dim count = Math.Min(columns, runEnd - i)
                        _groupRows.Add(New GroupLayoutRow With {.First = i, .Count = count, .Top = top, .Height = itemSlotHeight})
                        top += itemSlotHeight
                        i += count
                    End While
                End If
            End While
            _groupContentHeight = top
            ContentHeight = top
        End Sub

        ''' <summary>Die Zeile, in der ein Bildpunkt des Inhalts liegt. Binaersuche, damit auch 30000
        ''' Elemente je Scroll-Tick nichts kosten.</summary>
        Private Function FindGroupRowAt(contentY As Double) As Integer
            If _groupRows.Count = 0 Then Return 0
            If contentY <= 0 Then Return 0
            Dim low = 0
            Dim high = _groupRows.Count - 1
            While low < high
                Dim middle = (low + high + 1) \ 2
                If _groupRows(middle).Top <= contentY Then
                    low = middle
                Else
                    high = middle - 1
                End If
            End While
            Return low
        End Function

        ''' <summary>Anzeigefenster der Gruppenansicht setzen. Anders als <see cref="SetDisplayWindow"/>
        ''' bekommt es den Scrollversatz statt fertiger Grenzen: welche Zeilen dort stehen, weiss nur die
        ''' Zeilentabelle.</summary>
        Public Sub SetGroupDisplayWindow(contentOffsetY As Double, viewportHeight As Double, itemSlotHeight As Double, columns As Integer)
            EnsureGroupRows(columns, itemSlotHeight)
            _lastGroupOffsetY = contentOffsetY
            _lastGroupViewportHeight = viewportHeight

            If _groupRows.Count = 0 Then
                If DisplayItems.Count > 0 Then DisplayItems.Clear()
                _displayWindowFirst = -1
                _displayWindowLast = -1
                TopSpacerHeight = 0
                BottomSpacerHeight = 0
                ContentHeight = 0
                Return
            End If

            Dim firstRow = FindGroupRowAt(contentOffsetY)
            Dim lastRow = FindGroupRowAt(contentOffsetY + Math.Max(1.0, viewportHeight))
            ' Vorhaltepuffer wie im Raster: das Doppelte des Sichtbereichs nach oben und unten, damit
            ' gewoehnliches Scrollen keine Kachel erst im Moment des Erscheinens bauen muss.
            Dim visibleRows = Math.Max(1, lastRow - firstRow + 1)
            firstRow = Math.Max(0, firstRow - visibleRows * 2)
            lastRow = Math.Min(_groupRows.Count - 1, lastRow + visibleRows * 2)

            TopSpacerHeight = _groupRows(firstRow).Top
            BottomSpacerHeight = Math.Max(0.0, ContentHeight - (_groupRows(lastRow).Top + _groupRows(lastRow).Height))
            ApplyDisplayWindow(_groupLayout, _groupRows(firstRow).First,
                               _groupRows(lastRow).First + _groupRows(lastRow).Count - 1)
        End Sub

        ''' <summary>Der Bereich in ITEMS, der im Sichtbereich steht - die Ansicht fordert damit ihre
        ''' Vorschaubilder an. Die Gruppen sind zusammenhaengend, der Bereich ist es damit auch.</summary>
        Public Sub GetGroupVisibleItemRange(contentOffsetY As Double, viewportHeight As Double,
                                            ByRef firstItem As Integer, ByRef lastItem As Integer)
            firstItem = -1
            lastItem = -1
            If _groupRows.Count = 0 Then Return

            Dim firstRow = FindGroupRowAt(contentOffsetY)
            Dim lastRow = FindGroupRowAt(contentOffsetY + Math.Max(1.0, viewportHeight))
            Dim firstEntry = _groupRows(firstRow).First
            Dim lastEntry = Math.Min(_groupLayout.Count - 1, _groupRows(lastRow).First + _groupRows(lastRow).Count - 1)
            For i = firstEntry To lastEntry
                Dim itemIndex = _groupLayoutItemIndex(i)
                If itemIndex < 0 Then Continue For
                If firstItem < 0 Then firstItem = itemIndex
                lastItem = itemIndex
            Next
        End Sub

        ''' <summary>Die Lage eines Elements in der Gruppenansicht: Oberkante seiner Zeile und deren
        ''' Hoehe. Damit holt die Ansicht ein Element in den Blick, ohne selbst zu rechnen.</summary>
        Public Function TryGetGroupItemPosition(itemIndex As Integer, columns As Integer, itemSlotHeight As Double,
                                                ByRef rowTop As Double, ByRef rowHeight As Double) As Boolean
            rowTop = 0
            rowHeight = itemSlotHeight
            EnsureGroupRows(columns, itemSlotHeight)
            Dim entry = GroupEntryForItem(itemIndex)
            If entry < 0 Then Return False
            Dim row = FindGroupRowForEntry(entry)
            If row < 0 Then Return False
            rowTop = _groupRows(row).Top
            rowHeight = _groupRows(row).Height
            Return True
        End Function

        ''' <summary>Fenster um eine Zeile herum aufziehen, bevor der Scrollversatz gesetzt wird - sonst
        ''' klemmt der ScrollViewer den Versatz gegen seine noch veraltete Gesamthoehe. Dieselbe Regel
        ''' gilt im Raster, nur mit einer Formel statt der Tabelle.</summary>
        Public Sub SetGroupDisplayWindowAround(contentTop As Double, viewportHeight As Double,
                                               itemSlotHeight As Double, columns As Integer)
            SetGroupDisplayWindow(Math.Max(0.0, contentTop - viewportHeight), viewportHeight * 3, itemSlotHeight, columns)
        End Sub

        Private Function GroupEntryForItem(itemIndex As Integer) As Integer
            If itemIndex < 0 OrElse itemIndex >= _itemToGroupEntry.Length Then Return -1
            Return _itemToGroupEntry(itemIndex)
        End Function

        Private Function FindGroupRowForEntry(entry As Integer) As Integer
            If _groupRows.Count = 0 Then Return -1
            Dim low = 0
            Dim high = _groupRows.Count - 1
            While low < high
                Dim middle = (low + high + 1) \ 2
                If _groupRows(middle).First <= entry Then
                    low = middle
                Else
                    high = middle - 1
                End If
            End While
            Return low
        End Function

        ''' <summary>Die Elemente einer Gruppe, gefunden ueber die Kopfzeile selbst. Bewusst ueber den
        ''' Verweis und nicht ueber gemerkte Indizes: die Layoutliste wird bei jeder Aenderung an Items
        ''' neu gebaut, gemerkte Zahlen waeren danach still veraltet.</summary>
        Private Function GroupItemsFor(header As ImageItem) As List(Of ImageItem)
            Dim result As New List(Of ImageItem)()
            If header Is Nothing OrElse Not header.IsGroupHeader Then Return result
            Dim start = _groupLayout.IndexOf(header)
            If start < 0 Then Return result
            For i = start + 1 To _groupLayout.Count - 1
                If _groupLayout(i).IsGroupHeader Then Exit For
                If _groupLayout(i).IsSelectableEntry Then result.Add(_groupLayout(i))
            Next
            Return result
        End Function

        ''' <summary>Der Kreis an der Kopfzeile: ist die ganze Gruppe markiert, hebt der Klick sie auf,
        ''' sonst nimmt er sie dazu. Er ERSETZT die Auswahl nicht - genau wie der Kreis auf der Kachel
        ''' laesst er stehen, was anderswo markiert ist.</summary>
        Public Sub ToggleGroupSelection(header As ImageItem)
            Dim members = GroupItemsFor(header)
            If members.Count = 0 Then Return

            Dim allSelected = members.All(Function(i) SelectedItems.Contains(i))
            For Each item In members
                If allSelected Then
                    If SelectedItems.Remove(item) Then
                        item.IsSelected = False
                        item.IsNavigationSelected = False
                    End If
                ElseIf Not SelectedItems.Contains(item) Then
                    item.IsSelected = True
                    item.IsNavigationSelected = False
                    SelectedItems.Add(item)
                End If
            Next

            SelectedItem = If(allSelected, SelectedItems.LastOrDefault(), members(members.Count - 1))
            Me.RaisePropertyChanged(NameOf(SelectionText))
            Me.RaisePropertyChanged(NameOf(FooterStatusText))
            Me.RaisePropertyChanged(NameOf(HasSelection))
            Me.RaisePropertyChanged(NameOf(HasSelectedImage))
            RaiseSelectionMetadataChanged()
        End Sub

        ''' <summary>Zeichnet die Kreise der Kopfzeilen nach: eine Kopfzeile gilt als markiert, wenn
        ''' JEDES Element ihrer Gruppe markiert ist. Haengt an <see cref="RaiseSelectionMetadataChanged"/>
        ''' und damit an jedem Weg, der die Auswahl aendert - auch an kuenftigen. Eine Liste einzelner
        ''' Anstoss-Stellen vergisst erfahrungsgemaess einen davon.</summary>
        Private Sub RefreshGroupHeaderSelection()
            ' Ausserhalb der Gruppenansicht gibt es keine Kopfzeilen zu zeichnen, und solange die
            ' Layoutliste als veraltet vermerkt ist, stuenden dort ohnehin die Eintraege von vorhin.
            ' Nach einem Neuaufbau holt EnsureGroupEntries den Durchlauf selbst nach.
            If Not IsGroupView OrElse _groupEntriesDirty OrElse _groupLayout.Count = 0 Then Return
            Dim header As ImageItem = Nothing
            Dim members = 0
            Dim selected = 0
            For i = 0 To _groupLayout.Count
                Dim entry = If(i < _groupLayout.Count, _groupLayout(i), Nothing)
                If entry Is Nothing OrElse entry.IsGroupHeader Then
                    If header IsNot Nothing Then header.IsSelected = members > 0 AndAlso selected = members
                    header = entry
                    members = 0
                    selected = 0
                ElseIf entry.IsSelectableEntry Then
                    members += 1
                    If entry.IsSelected Then selected += 1
                End If
            Next
        End Sub

        ''' <summary>Versatz in ITEMS-Indizes fuer eine Bewegung um ganze Zeilen. Am Gruppenende ist das
        ''' NICHT die Spaltenzahl: die letzte Zeile einer Gruppe ist meist nur teilweise gefuellt, und
        ''' dazwischen liegt eine Kopfzeile, auf der nichts stehen kann.</summary>
        Public Function GroupRowNavigationOffset(currentItemIndex As Integer, rowDelta As Integer,
                                                 columns As Integer, itemSlotHeight As Double) As Integer
            If rowDelta = 0 Then Return 0
            EnsureGroupRows(columns, itemSlotHeight)
            Dim entry = GroupEntryForItem(currentItemIndex)
            If entry < 0 Then Return rowDelta
            Dim row = FindGroupRowForEntry(entry)
            If row < 0 Then Return rowDelta
            Dim column = entry - _groupRows(row).First

            Dim direction = Math.Sign(rowDelta)
            Dim remaining = Math.Abs(rowDelta)
            Dim targetRow = row
            While remaining > 0
                Dim nextRow = targetRow + direction
                If nextRow < 0 OrElse nextRow > _groupRows.Count - 1 Then Exit While
                targetRow = nextRow
                ' Eine Kopfzeile ueberspringen, ohne sie als Zeile zu zaehlen: die Bewegung soll von
                ' Bild zu Bild gehen, nicht auf einer Beschriftung landen.
                If Not _groupLayout(_groupRows(targetRow).First).IsGroupHeader Then remaining -= 1
            End While
            While targetRow >= 0 AndAlso targetRow <= _groupRows.Count - 1 AndAlso
                  _groupLayout(_groupRows(targetRow).First).IsGroupHeader
                targetRow += direction
            End While
            If targetRow < 0 OrElse targetRow > _groupRows.Count - 1 Then Return rowDelta

            Dim targetEntry = _groupRows(targetRow).First + Math.Min(column, _groupRows(targetRow).Count - 1)
            Dim targetItem = _groupLayoutItemIndex(targetEntry)
            If targetItem < 0 Then Return rowDelta
            Return targetItem - currentItemIndex
        End Function

        ''' Cache-Scope der aktuell angezeigten Ansicht - bei Suchlisten die Suchlisten-Scope, damit
        ''' Viewer/Editor (Filmstreifen) die Thumbnails im selben Suchlisten-Cache ablegen statt neue
        ''' Cache-Ordner je Ursprungsordner der Treffer anzulegen. Bei normalen Ordnern Nothing.
        Public ReadOnly Property CurrentThumbnailCacheScopeId As String
            Get
                If _isVirtualFolder AndAlso _selectedSearchNode IsNot Nothing Then Return GetSearchListCacheScopeId(_selectedSearchNode.Id)
                Return Nothing
            End Get
        End Property

        Public ReadOnly Property CurrentThumbnailCacheScopeName As String
            Get
                If _isVirtualFolder AndAlso _selectedSearchNode IsNot Nothing Then Return "Suchliste: " & _selectedSearchNode.Name
                Return Nothing
            End Get
        End Property

        ''' <summary>Zwei markierte Bilder nebeneinander oeffnen. Uebergibt DIESELBE Pfadliste und
        ''' denselben Cache-Skopus wie das Einzeloeffnen - sonst baut der Betrachter den Ordner aus
        ''' dem linken Bild neu auf, und aus einer Suchliste heraus stuende ploetzlich ein ganz
        ''' anderer Filmstreifen da.</summary>
        Public Sub CompareSelectedInViewer()
            Try
                Dim gewaehlt = Items.Where(Function(i) i IsNot Nothing AndAlso i.IsImage AndAlso
                                                       i.IsSelected AndAlso Not String.IsNullOrEmpty(i.FilePath)).ToList()
                If gewaehlt.Count <> 2 Then Return
                ' Der Vergleich laeuft ueber DATEIPFADE; ein Pseudo-Pfad gleich welcher Serverquelle
                ' scheitert dort. Das Menue blendet den Eintrag entsprechend aus, hier steht dieselbe
                ' Bedingung noch einmal - der Weg wird auch ueber Tastenkuerzel erreicht.
                If gewaehlt.Any(Function(i) i.IsRemoteAsset) Then Return
                _mainVm.OpenCompareInViewer(gewaehlt(0).FilePath, gewaehlt(1).FilePath,
                                            Items.Where(Function(i) i.IsImage OrElse i.IsVideoFile).Select(Function(i) i.FilePath).ToList(),
                                            cacheScopeId:=CurrentThumbnailCacheScopeId,
                                            cacheScopeName:=CurrentThumbnailCacheScopeName)
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryViewModel.CompareSelectedInViewer", ex)
            End Try
        End Sub

        Public Async Sub OpenSelectedInViewer()
            Try
                Dim selectedMedia = Items.Where(Function(i) i IsNot Nothing AndAlso (i.IsImage OrElse i.IsVideoFile) AndAlso i.IsSelected).ToList()
                If selectedMedia.Count > 0 Then
                    Dim first = selectedMedia(0)
                    ' Jedes Serverbild geht ueber die Sitzung, nicht nur ein Immich-Asset: ein
                    ' Pseudo-Pfad im lokalen Weg wuerde zu "Datei nicht gefunden" fuehren.
                    If first.IsRemoteAsset Then
                        Await OpenRemoteItemInViewerAsync(first)
                        Return
                    End If
                    _mainVm.OpenImageInViewer(first.FilePath, Items.Where(Function(i) i.IsImage OrElse i.IsVideoFile).Select(Function(i) i.FilePath).ToList(),
                                              cacheScopeId:=CurrentThumbnailCacheScopeId, cacheScopeName:=CurrentThumbnailCacheScopeName)
                ElseIf SelectedItem IsNot Nothing AndAlso (SelectedItem.IsImage OrElse SelectedItem.IsVideoFile) Then
                    _mainVm.OpenImageInViewer(SelectedItem.FilePath, Items.Where(Function(i) i.IsImage OrElse i.IsVideoFile).Select(Function(i) i.FilePath).ToList(),
                                              cacheScopeId:=CurrentThumbnailCacheScopeId, cacheScopeName:=CurrentThumbnailCacheScopeName)
                ElseIf SelectedItem IsNot Nothing AndAlso SelectedItem.IsParentFolderEntry Then
                    NavigateToParent()
                End If
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryViewModel.OpenSelectedInViewer", ex)
                StatusText = LocalizationService.T("Aktion fehlgeschlagen")
            End Try
        End Sub

        Public Async Sub OpenSelectedInEditor()
            Try
                Dim image = GetSelectedImageItems().FirstOrDefault(Function(i) i.CanEditFile)
                If image Is Nothing Then Return
                If image.IsRemoteAsset Then
                    Await OpenRemoteItemInEditorAsync(image)
                    Return
                End If
                Await _mainVm.OpenImageInEditor(image.FilePath, Items.Where(Function(i) i.IsImage AndAlso i.CanEditFile).Select(Function(i) i.FilePath).ToList(),
                                                cacheScopeId:=CurrentThumbnailCacheScopeId, cacheScopeName:=CurrentThumbnailCacheScopeName)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                ' LogException (nicht LogAlways): nur DIESER Weg schreibt unabhängig vom
                ' Diagnose-Schalter in die Fehlerdatei. „Aktion fehlgeschlagen" ist die einzige Spur,
                ' die der Nutzer sieht - ohne den Eintrag wäre der Grund nicht mehr zu ermitteln.
                DiagnosticLogService.LogException("GalleryViewModel.OpenSelectedInEditor", ex)
                StatusText = LocalizationService.T("Aktion fehlgeschlagen")
            End Try
        End Sub

        ''' <summary>Lädt das Original einer Serverquelle in eine Temp-Kopie und öffnet es im Editor
        ''' mit Speichern-unter-Zwang - die Temp-Kopie wird nie in-place überschrieben, das Ergebnis
        ''' landet als neue Datei.
        '''
        ''' Für Immich ist das ein Upload als neues Asset, sofern die Einstellung es erlaubt. Für
        ''' Nextcloud gibt es noch keinen Rückweg; dort bleibt es beim Speichern auf die Platte,
        ''' obwohl der Server ein Ersetzen erlauben würde (siehe `OFFENE_PUNKTE.md`).</summary>
        Private Async Function OpenRemoteItemInEditorAsync(item As ImageItem) As Task
            IsLoading = True
            StatusText = LocalizationService.T("Lade Bild aus Immich…")
            Try
                Dim localPath = Await item.EnsureLocalOriginalAsync()
                If String.IsNullOrEmpty(localPath) Then
                    StatusText = LocalizationService.T("Bild konnte nicht aus Immich geladen werden")
                    Return
                End If
                ' Bei Nextcloud die Herkunft mitgeben: daran haengt, wohin die Begleitdatei kommt.
                ' Steht der Pfad noch nicht am Element, jetzt holen - ohne ihn gaebe es keinen
                ' Rueckweg und der Editor bliebe stumm bei "Speichern unter".
                If item.IsNextcloudAsset AndAlso String.IsNullOrEmpty(item.NextcloudPath) Then
                    Dim info = Await NextcloudService.GetInfoAsync(item.NextcloudFileId)
                    If info IsNot Nothing Then item.ApplyNextcloudMetadata(info)
                End If
                ' NICHT "nextcloudOrigin": VB unterscheidet keine Gross-/Kleinschreibung, der Name
                ' verdeckte damit den TYP NextcloudOrigin.
                Dim itemOrigin = NextcloudOrigin.FromItem(item)
                ' Stammt das Bild aus einem geöffneten Immich-Album, den bearbeiteten Upload gleich dorthin.
                Dim sourceAlbumId = If(SelectedImmichNode IsNot Nothing AndAlso String.Equals(SelectedImmichNode.Kind, "ImmichAlbum", StringComparison.Ordinal), SelectedImmichNode.Id, Nothing)
                ' forceSaveAsOnly bleibt fuer Immich; bei Nextcloud entscheidet der Editor selbst,
                ' denn dort ist die Begleitdatei ein echter Speicherweg.
                ' Den Namen vom Server mitgeben: die Temp-Kopie heisst nach der Kennung, und ohne
                ' ihn stuende sie so in der Fusszeile und im Vorschlag beim Speichern.
                Await _mainVm.OpenImageInEditor(localPath, New List(Of String) From {localPath},
                                                forceSaveAsOnly:=(itemOrigin Is Nothing OrElse Not itemOrigin.IsKnown),
                                                immichAlbumId:=sourceAlbumId,
                                                nextcloudSource:=itemOrigin,
                                                displayFileName:=item.FileName)
                StatusText = ""
            Finally
                IsLoading = False
            End Try
        End Function

        ''' <summary>Öffnet ein Serverbild im Betrachter als Sitzung: der Filmstreifen zeigt alle
        ''' Serverbilder der aktuellen Ansicht (Pseudo-Pfade), das Original wird on-demand geladen.</summary>
        Private Function OpenRemoteItemInViewerAsync(item As ImageItem) As Task
            Dim sessionItems = Items.Where(Function(i) i.IsImage AndAlso i.IsRemoteAsset).ToList()
            Dim sourceAlbumId = If(SelectedImmichNode IsNot Nothing AndAlso String.Equals(SelectedImmichNode.Kind, "ImmichAlbum", StringComparison.Ordinal), SelectedImmichNode.Id, Nothing)
            _mainVm.OpenImmichViewer(item.FilePath, sessionItems, sourceAlbumId)
            Return Task.CompletedTask
        End Function

        Public Sub DeleteSelected()
            If _isVirtualFolder Then
                ' Immich-Items haben keinen Dateipfad (Pseudo-Pfad) - sie werden auf dem Server gelöscht,
                ' alles andere in der virtuellen Ansicht (Suchliste) wie gewohnt lokal.
                Dim immichItems = GetSelectedImageItems().Where(Function(i) i.IsImmichAsset).ToList()
                If immichItems.Count > 0 Then
                    Dim ignored = DeleteImmichAssetsAsync(immichItems)
                End If
                Dim nextcloudItems = GetSelectedImageItems().Where(Function(i) i.IsNextcloudAsset).ToList()
                If nextcloudItems.Count > 0 Then
                    Dim ignored2 = DeleteNextcloudFilesAsync(nextcloudItems)
                End If
                Dim virtualTargets = GetSelectedPaths().Where(Function(p) File.Exists(p)).ToList()
                DeletePaths(virtualTargets)
                Return
            End If
            Dim targets = GetSelectedPaths()
            If targets.Count = 0 AndAlso SelectedFolderNode IsNot Nothing AndAlso SelectedItem Is Nothing Then
                targets.Add(SelectedFolderNode.FullPath)
            End If
            DeletePaths(targets)
        End Sub

        ''' <summary>Löscht Dateien auf dem Nextcloud-Server. Ohne die Einstellung „Löschen in
        ''' Nextcloud erlauben" wirkungslos - niemand soll aus Versehen Bilder vom Server werfen.
        ''' Gelöscht wird in den Nextcloud-Papierkorb, es braucht also keinen zweiten Schalter für
        ''' „endgültig". Die Rückfrage folgt derselben Einstellung wie beim lokalen Löschen.</summary>
        Private Async Function DeleteNextcloudFilesAsync(items As List(Of ImageItem)) As Task
            If items Is Nothing OrElse items.Count = 0 Then Return
            If Not AppSettingsService.Load().NextcloudAllowDelete Then
                StatusText = LocalizationService.T("Löschen in Nextcloud ist nicht erlaubt")
                Return
            End If
            ' Endgueltig heisst endgueltig: dann steht es AUCH in der Rueckfrage, sonst klickt
            ' jemand denselben Knopf wie sonst und wundert sich, dass der Papierkorb leer bleibt.
            Dim permanent = AppSettingsService.Load().NextcloudDeletePermanently
            If Not AppSettingsService.Load().DeleteSkipConfirmation Then
                Dim question = If(permanent,
                                  String.Format(LocalizationService.T("{0} Bild(er) endgültig vom Server löschen? Der Papierkorb hilft danach nicht mehr."), items.Count),
                                  String.Format(LocalizationService.T("{0} Bild(er) auf dem Server löschen?"), items.Count))
                If Not Await _mainVm.ShowConfirmAsync(LocalizationService.T("Löschen"), question) Then Return
            End If

            Dim deletedCount = 0
            For Each item In items
                Dim pathInTree = item.NextcloudPath
                If String.IsNullOrEmpty(pathInTree) Then
                    Dim info = Await NextcloudService.GetInfoAsync(item.NextcloudFileId)
                    If info Is Nothing Then Continue For
                    item.ApplyNextcloudMetadata(info)
                    pathInTree = item.NextcloudPath
                End If
                If String.IsNullOrEmpty(pathInTree) Then Continue For
                Dim ok = If(permanent,
                            Await NextcloudService.DeleteFilePermanentlyAsync(pathInTree),
                            Await NextcloudService.DeleteFileAsync(pathInTree))
                If ok Then
                    deletedCount += 1
                    RemovePathsFromVirtualFolder({item.FilePath})
                End If
            Next
            FilterAndSort()
            StatusText = If(deletedCount = 0,
                            If(String.IsNullOrEmpty(NextcloudService.LastError), LocalizationService.T("Kein Element ausgewählt"), NextcloudService.LastError),
                            String.Format(LocalizationService.T("{0} Bild(er) gelöscht"), deletedCount))
        End Function

        ''' <summary>Löscht Assets auf dem Immich-Server. Standardmäßig abgeschaltet: ohne die Einstellung
        ''' "Löschen in Immich erlauben" bleibt ein Entf in der Galerie bei Immich-Bildern wirkungslos -
        ''' niemand soll aus Versehen Bilder vom Server werfen. "Endgültig löschen" umgeht zusätzlich den
        ''' Immich-Papierkorb; die Rückfrage folgt derselben Einstellung wie beim lokalen Löschen.</summary>
        Private Async Function DeleteImmichAssetsAsync(items As List(Of ImageItem)) As Task
            Dim settings = AppSettingsService.Load()
            If Not settings.ImmichAllowDelete Then
                StatusText = LocalizationService.T("Löschen in Immich ist in den Einstellungen nicht erlaubt")
                Return
            End If

            Dim assetIds = items.Select(Function(i) i.ImmichAssetId).
                Where(Function(id) Not String.IsNullOrWhiteSpace(id)).
                Distinct(StringComparer.Ordinal).
                ToList()
            If assetIds.Count = 0 Then Return

            Dim permanent = settings.ImmichDeletePermanently
            If Not settings.DeleteSkipConfirmation Then
                Dim verb = If(permanent,
                              LocalizationService.T("endgültig aus Immich löschen"),
                              LocalizationService.T("in den Immich-Papierkorb verschieben"))
                Dim message = If(items.Count = 1,
                                 $"{items(0).FileName} {verb}?",
                                 $"{items.Count} {LocalizationService.T("Elemente")} {verb}?")
                Dim confirmText = If(permanent, LocalizationService.T("Löschen"), LocalizationService.T("In den Papierkorb"))
                If Not Await _mainVm.ShowConfirmAsync(LocalizationService.T("Aus Immich löschen"), message, confirmText, LocalizationService.T("Abbrechen")) Then Return
            End If

            IsLoading = True
            Try
                Dim ok = Await ImmichService.DeleteAssetsAsync(assetIds, force:=permanent)
                If Not ok Then
                    StatusText = LocalizationService.T("Löschen in Immich fehlgeschlagen")
                    Return
                End If

                Dim deletionAnchor = CaptureDeletionAnchor(items.Select(Function(i) i.FilePath))
                ClearSelection()
                RemoveImmichItems(assetIds)
                SelectAfterDeletion(deletionAnchor)
                RefreshImmichAlbumsAsync()
                StatusText = String.Format(LocalizationService.T("{0} aus Immich gelöscht"), assetIds.Count)
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.DeleteFlow", ex)
                StatusText = LocalizationService.T("Löschen in Immich fehlgeschlagen")
            Finally
                IsLoading = False
            End Try
        End Function

        ''' <summary>Die Einstellung „Löschen in Immich erlauben" entscheidet, ob Kontextmenü und Kachel-Knopf
        ''' bei Immich-Bildern ein Löschen anbieten (ImageItem.CanFileOperationDelete) und ob der Baum „Album
        ''' löschen" zeigt (VirtualNavigationNode.CanDeleteImmichAlbum) - wird sie umgelegt, während die
        ''' Galerie offen ist, müssen beide das mitbekommen. Die Album-Knoten baut RefreshImmichAlbumsAsync
        ''' dafür neu auf (der Knoten hat keine Benachrichtigung).</summary>
        Public Sub RefreshImmichDeletePermission()
            For Each item In _allItems.Where(Function(i) i IsNot Nothing AndAlso i.IsImmichAsset)
                item.RefreshFileOperationFlags()
            Next
            If HasImmich Then RefreshImmichAlbumsAsync()
        End Sub

        ''' <summary>Nimmt gelöschte Immich-Assets aus der Ansicht - auch aus der Dedup-Menge der virtuellen
        ''' Ansicht, sonst bliebe deren Pseudo-Pfad belegt und dasselbe Bild käme nach einem erneuten Upload
        ''' nicht mehr in die Liste. Wird auch vom Betrachter gerufen, der auf demselben Bestand arbeitet.</summary>
        Public Sub RemoveImmichItems(assetIds As IEnumerable(Of String))
            Dim gone = If(assetIds, Enumerable.Empty(Of String)()).Where(Function(id) Not String.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal)
            If gone.Count = 0 Then Return

            For Each item In _allItems.Where(Function(i) i IsNot Nothing AndAlso i.IsImmichAsset AndAlso gone.Contains(i.ImmichAssetId)).ToList()
                _virtualPathSet.Remove(item.FilePath)
            Next
            _allItems.RemoveAll(Function(i) i IsNot Nothing AndAlso i.IsImmichAsset AndAlso gone.Contains(i.ImmichAssetId))
            FilterAndSort()
        End Sub

        Public Sub DeletePaths(paths As IEnumerable(Of String))
            Dim targets = If(paths, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrEmpty(p)).
                Distinct(PathIdentity.Comparer).
                ToList()
            targets = targets.Where(Function(p) FileOperationPolicy.CanDelete(p)).ToList()
            If targets.Count = 0 Then Return
            Dim deletionAnchor = CaptureDeletionAnchor(targets)
            If _isVirtualFolder Then
                Dim deletedSet = targets.ToHashSet(PathIdentity.Comparer)
                _mainVm.RequestDeletePaths(targets, Nothing,
                                           Sub()
                                               ClearSelection()
                                               _allItems.RemoveAll(Function(i) i IsNot Nothing AndAlso deletedSet.Contains(i.FilePath))
                                               FilterAndSort()
                                               SelectAfterDeletion(deletionAnchor)
                                           End Sub)
                Return
            End If
            Dim currentFolderWasDeleted = Not String.IsNullOrEmpty(_currentFolder) AndAlso
                                          targets.Any(Function(p) Directory.Exists(p) AndAlso String.Equals(NormalizePath(p), NormalizePath(_currentFolder), StringComparison.OrdinalIgnoreCase))
            Dim fallbackFolder = If(currentFolderWasDeleted, IO.Path.GetDirectoryName(_currentFolder), Nothing)
            ' Vor dem Löschen feststellen: danach sagt Directory.Exists nichts mehr. Nur wenn ein Ordner
            ' dabei war, muss der Baum neu aufgebaut werden.
            Dim anyFolderDeleted = targets.Any(Function(p) Directory.Exists(p))
            Dim deletedPaths = targets.ToHashSet(PathIdentity.Comparer)
            _mainVm.RequestDeletePaths(targets,
                                       Sub()
                                           ' Nachlauf: Verzeichnis abgleichen (holt zurueck, was doch nicht
                                           ' geloescht werden konnte) und den Baum nachziehen.
                                           If currentFolderWasDeleted AndAlso Not String.IsNullOrEmpty(fallbackFolder) AndAlso Directory.Exists(fallbackFolder) Then
                                               CurrentFolder = fallbackFolder
                                               LoadFolderImages(fallbackFolder)
                                               RefreshTree()
                                               SelectFolderInTreeByPath(fallbackFolder)
                                           Else
                                               SyncFolderItems()
                                               If anyFolderDeleted Then RefreshTree()
                                           End If
                                       End Sub,
                                       Sub()
                                           ' Sofort ausblenden, noch VOR dem Papierkorb-Aufruf: das eigentliche
                                           ' Loeschen laeuft im Hintergrund, und SyncFolderItems zaehlt danach
                                           ' das ganze Verzeichnis neu auf - beides war frueher zu sehen.
                                           ClearSelection()
                                           _allItems.RemoveAll(Function(i) i IsNot Nothing AndAlso deletedPaths.Contains(i.FilePath))
                                           FilterAndSort()
                                           If Not currentFolderWasDeleted Then SelectAfterDeletion(deletionAnchor)
                                       End Sub)
        End Sub

        ''' <summary>Merkt sich vor dem Löschen, an welcher Stelle der Liste das erste betroffene Element
        ''' steht. Danach rücken die überlebenden Elemente in die Lücke nach, sodass genau dieser Index
        ''' auf den Nachfolger zeigt - beim letzten Bild auf das neue letzte. Ohne diesen Anker bliebe die
        ''' Auswahl leer und die nächste Pfeiltaste sprang an den Listenanfang.</summary>
        Private Function CaptureDeletionAnchor(deletedPaths As IEnumerable(Of String)) As Integer
            Dim gone = If(deletedPaths, Enumerable.Empty(Of String)()).ToHashSet(PathIdentity.Comparer)
            If gone.Count = 0 Then Return -1
            For i = 0 To Items.Count - 1
                Dim item = Items(i)
                If item IsNot Nothing AndAlso item.IsSelectableEntry AndAlso gone.Contains(item.FilePath) Then Return i
            Next
            Return -1
        End Function

        ''' <summary>Setzt die Auswahl nach dem Löschen auf das nachgerückte Element. Greift nur, wenn
        ''' nichts von der alten Auswahl übrig ist - sonst bliebe eine bewusst erhaltene Auswahl auf der
        ''' Strecke.</summary>
        Private Sub SelectAfterDeletion(anchor As Integer)
            If anchor < 0 OrElse Items.Count = 0 Then Return
            If SelectedItem IsNot Nothing OrElse (SelectedItems IsNot Nothing AndAlso SelectedItems.Count > 0) Then Return

            Dim start = Math.Min(anchor, Items.Count - 1)
            For i = start To Items.Count - 1
                If Items(i) IsNot Nothing AndAlso Items(i).IsSelectableEntry Then
                    SelectOnly(Items(i))
                    Return
                End If
            Next
            For i = start - 1 To 0 Step -1
                If Items(i) IsNot Nothing AndAlso Items(i).IsSelectableEntry Then
                    SelectOnly(Items(i))
                    Return
                End If
            Next
        End Sub

        Private Sub OpenInFileManager()
            If _isVirtualFolder Then
                Dim selectedPath = GetSelectedPaths().FirstOrDefault()
                If String.IsNullOrEmpty(selectedPath) Then Return
                Dim folder = IO.Path.GetDirectoryName(selectedPath)
                If String.IsNullOrEmpty(folder) Then Return
                ShellOpenService.Open(folder, "Gallery.OpenInFileManager")
                Return
            End If
            If String.IsNullOrEmpty(_currentFolder) Then Return
            ShellOpenService.Open(_currentFolder, "Gallery.OpenInFileManager")
        End Sub

        Private Sub CopySelectedPath()
            ' Clipboard-Zugriff erfolgt in der View
        End Sub

        Public ReadOnly Property SelectionText As String
            Get
                If SelectedItems Is Nothing OrElse SelectedItems.Count = 0 Then Return LocalizationService.T("Kein Element ausgewählt")
                If SelectedItems.Count = 1 Then Return LocalizationService.T("1 Element ausgewählt")
                Return String.Format(LocalizationService.T("{0} Elemente ausgewählt"), SelectedItems.Count)
            End Get
        End Property

        Public ReadOnly Property FooterStatusText As String
            Get
                Dim itemCount = If(Items Is Nothing, 0, Items.Count)
                Dim itemText = If(itemCount = 1,
                                  $"1 {LocalizationService.T("Element")}",
                                  $"{itemCount:N0} {LocalizationService.T("Elemente")}")
                Return $"{SelectionText} · {itemText}"
            End Get
        End Property

        Public Sub RenameSelected()
            If _isVirtualFolder Then Return
            If SelectedItems IsNot Nothing AndAlso SelectedItems.Count > 1 Then
                BatchRenameSelected()
                Return
            End If

            Dim target As String = Nothing
            If SelectedItems.Count = 1 Then
                If Not SelectedItems(0).IsParentFolderEntry Then
                    target = SelectedItems(0).FilePath
                End If
            ElseIf SelectedItems.Count = 0 AndAlso SelectedFolderNode IsNot Nothing Then
                target = SelectedFolderNode.FullPath
            End If
            If String.IsNullOrEmpty(target) Then Return
            RenamePath(target)
        End Sub

        Public Sub RenamePath(target As String)
            If String.IsNullOrEmpty(target) Then Return
            If Not FileOperationPolicy.CanRename(target) Then Return

            _mainVm.RequestRenamePath(target, Sub(newPath)
                                                  RefreshTree()
                                                  If Directory.Exists(newPath) AndAlso String.Equals(NormalizePath(target), NormalizePath(_currentFolder), StringComparison.OrdinalIgnoreCase) Then
                                                      CurrentFolder = newPath
                                                      LoadFolderImages(newPath)
                                                      RestoreCurrentFolderTreeSelection()
                                                  Else
                                                      ' Der Dialog erwartet, dass das Ziel danach markiert ist. Der
                                                      ' Wunsch wird hinterlegt, statt auf einen bestimmten Lauf zu
                                                      ' warten - siehe RequestSelectionAfterSync.
                                                      '
                                                      ' Markiert heisst AUSGEWAEHLT, nicht nur "aktuelles Element":
                                                      ' bisher stand hier ein blosses SelectedItem, und damit blieb
                                                      ' SelectedItems leer - ein zweites Umbenennen hintereinander
                                                      ' nahm dann den Ordner als Ziel statt die eben umbenannte Datei.
                                                      RequestSelectionAfterSync({newPath})
                                                      SyncFolderItems()
                                                  End If
                                              End Sub)
        End Sub

        Private Async Sub BatchRenameSelected()
            Dim paths = GetSelectedPaths().
                Where(Function(p) FileOperationPolicy.CanRename(p)).
                ToList()
            If paths.Count < 2 Then Return

            Dim result = Await _mainVm.ShowBatchRenameAsync(paths)
            If result Is Nothing OrElse result.Mappings Is Nothing OrElse result.Mappings.Count = 0 Then Return

            Dim errorMessage As String = Nothing
            Try
                Dim sources = result.Mappings.Select(Function(m) m.SourcePath).ToList()
                _mainVm.Viewer.ReleaseCurrentImageIfAny(sources)
                _mainVm.Editor.ReleaseCurrentImageIfAny(sources)

                For Each mapping In result.Mappings
                    If String.IsNullOrEmpty(mapping.SourcePath) OrElse String.IsNullOrEmpty(mapping.TargetPath) Then Continue For
                    ' Reines Aendern der Gross-/Kleinschreibung ist auf Linux eine ECHTE Umbenennung
                ' (RAW.jpg und raw.jpg sind zwei Dateien) und darf nicht uebersprungen werden.
                If String.Equals(NormalizePath(mapping.SourcePath), NormalizePath(mapping.TargetPath), PathIdentity.Comparison) Then Continue For
                    If File.Exists(mapping.TargetPath) OrElse Directory.Exists(mapping.TargetPath) Then Throw New IOException("Ein Zielname existiert bereits.")

                    If File.Exists(mapping.SourcePath) Then
                        File.Move(mapping.SourcePath, mapping.TargetPath)
                        RawSidecarService.AccompanyMove(mapping.SourcePath, mapping.TargetPath)
                    ElseIf Directory.Exists(mapping.SourcePath) Then
                        Directory.Move(mapping.SourcePath, mapping.TargetPath)
                    End If
                Next

                ClearSelection()
                ' Erst den Wunsch hinterlegen, dann abgleichen: das Umbenennen hat den Watcher schon
                ' geweckt, und dessen Lauf darf den hier begonnenen ueberholen, ohne dass die
                ' Auswahl verlorengeht (siehe RequestSelectionAfterSync).
                RequestSelectionAfterSync(result.Mappings.Select(Function(m) m.TargetPath))
                Await SyncFolderItemsAsync()
                RefreshTree()
            Catch ex As Exception
                errorMessage = ex.Message
            End Try

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Stapel-Umbenennen fehlgeschlagen"), errorMessage)
        End Sub

        Public Async Sub CreateFolderIn(folderPath As String)
            If IsVirtualFolderPath(folderPath) Then Return
            If String.IsNullOrEmpty(folderPath) OrElse Not FileOperationPolicy.CanPasteInto(folderPath) Then Return

            Dim folderName = Await _mainVm.ShowInputAsync(AppDialogKind.Input, "Neuer Ordner", "Ordnernamen eingeben", "Neuer Ordner", "Erstellen", "Abbrechen")
            If String.IsNullOrWhiteSpace(folderName) Then Return
            folderName = folderName.Trim()

            Dim errorMessage As String = Nothing
            Try
                If HasInvalidFileNameChars(folderName) Then Throw New IOException("Der Name enthält ungültige Zeichen.")
                Dim target = IO.Path.Combine(folderPath, folderName)
                If FileOperationPolicy.IsHiddenPath(target) Then Throw New IOException("Versteckte Ordner können hier nicht erstellt werden.")
                If IO.File.Exists(target) OrElse IO.Directory.Exists(target) Then Throw New IOException("Ein Element mit diesem Namen existiert bereits.")

                IO.Directory.CreateDirectory(target)
                RefreshTree()
                ExpandFolderInTreeByPath(folderPath)
                If Not _isVirtualFolder AndAlso String.Equals(NormalizePath(folderPath), NormalizePath(_currentFolder), StringComparison.OrdinalIgnoreCase) Then
                    SyncFolderItems()
                End If
            Catch ex As Exception
                errorMessage = ex.Message
            End Try

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Ordner erstellen fehlgeschlagen"), errorMessage)
        End Sub

        Public Function GetSelectedPaths() As List(Of String)
            Dim selected = If(SelectedItems Is Nothing OrElse SelectedItems.Count = 0,
                              If(SelectedItem Is Nothing, Enumerable.Empty(Of ImageItem)(), {SelectedItem}),
                              SelectedItems)
            Return selected.
                Where(Function(i) i IsNot Nothing AndAlso Not i.IsParentFolderEntry).
                Select(Function(i) i.FilePath).
                Where(Function(p) Not String.IsNullOrEmpty(p)).
                Distinct(PathIdentity.Comparer).
                ToList()
        End Function

        ''' <summary>Öffnet den Druckdialog für die Auswahl. Immich-Assets tragen in FilePath nur
        ''' einen Pseudopfad - für sie muss erst das Original lokal vorliegen, sonst fände der
        ''' PDF-Renderer keine Datei. Videos scheiden aus (nichts zu drucken).</summary>
        Private Async Function PrintSelectedAsync() As Task
            Dim items = If(SelectedItems Is Nothing OrElse SelectedItems.Count = 0,
                           If(SelectedItem Is Nothing, Enumerable.Empty(Of ImageItem)(), {SelectedItem}),
                           SelectedItems.AsEnumerable()).
                Where(Function(i) i IsNot Nothing AndAlso i.IsImage AndAlso Not i.IsVideoFile).
                ToList()

            If items.Count = 0 Then
                StatusText = LocalizationService.T("Es sind keine druckbaren Bilder ausgewählt.")
                Return
            End If

            Dim beschafft = Await ResolveLocalPathsAsync(items)
            Dim paths = beschafft.Paths
            Dim skipped = beschafft.Skipped

            If paths.Count = 0 Then
                StatusText = LocalizationService.T("Es sind keine druckbaren Bilder ausgewählt.")
                Return
            End If

            StatusText = If(skipped > 0, LocalizationService.T("Einige Bilder konnten nicht geladen werden."), "")
            _mainVm?.ShowPrintDialog(paths)
        End Function

        ''' <summary>Bilder auf den Datentraeger holen, damit eine Ausgabe sie lesen kann.
        '''
        ''' Ein Immich-Asset traegt nur einen Pseudopfad; das Original wird bei Bedarf in den
        ''' Temp-Ordner geladen und am Element gemerkt, damit ein zweiter Aufruf es wiederverwendet.
        ''' Steht an EINER Stelle, weil Drucken und Collage dieselbe Beschaffung brauchen - vorher
        ''' hatte nur das Drucken sie, und die Collage lief bei Immich ins Leere.</summary>
        Private Async Function ResolveLocalPathsAsync(items As IList(Of ImageItem)) As Task(Of (Paths As List(Of String), Skipped As Integer))
            Dim paths As New List(Of String)()
            Dim skipped = 0
            Try
                For Each item In items
                    ' JEDE Serverquelle, nicht nur Immich: ein Nextcloud-Element fiel hier auf
                    ' File.Exists("nextcloud://…") und wurde uebersprungen - beim Drucken fehlte es,
                    ' und eine reine Nextcloud-Auswahl brachte gar keine Collage zustande.
                    ' EnsureLocalOriginalAsync ist der EINE Weg dafuer: es weiss selbst, von welchem
                    ' Server es holt, und benutzt eine schon geholte Kopie wieder.
                    If item.IsRemoteAsset Then
                        Dim localPath = item.RemoteLocalPath
                        If String.IsNullOrEmpty(localPath) OrElse Not File.Exists(localPath) Then
                            StatusText = LocalizationService.T(If(items.Count > 1,
                                                                  "Lade Bilder vom Server…",
                                                                  "Lade Bild vom Server…"))
                            IsLoading = True
                            localPath = Await item.EnsureLocalOriginalAsync()
                        End If
                        If Not String.IsNullOrEmpty(localPath) AndAlso File.Exists(localPath) Then
                            paths.Add(localPath)
                        Else
                            skipped += 1
                        End If
                    ElseIf File.Exists(item.FilePath) Then
                        paths.Add(item.FilePath)
                    Else
                        skipped += 1
                    End If
                Next
            Finally
                IsLoading = False
            End Try
            Return (paths, skipped)
        End Function

        Public Async Sub OpenCollageDialog()
            Try
                Await OpenCollageDialogAsync()
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("Gallery.OpenCollageDialog", ex)
            End Try
        End Sub

        Private Async Function OpenCollageDialogAsync() As Task
            Dim items = If(SelectedItems, Enumerable.Empty(Of ImageItem)()).
                Where(Function(i) i IsNot Nothing AndAlso i.IsImage AndAlso Not i.IsVideoFile).
                ToList()

            Dim beschafft = Await ResolveLocalPathsAsync(items)
            Dim paths = beschafft.Paths.Where(AddressOf IsCollageSourcePath).ToList()
            If paths.Count < 2 Then
                StatusText = LocalizationService.T("Für eine Collage müssen mindestens zwei Bilder ausgewählt sein.")
                Return
            End If

            CollageBaseName = $"Collage_{DateTime.Now:yyyyMMdd_HHmmss}"
            CollageColumns = Math.Max(2, CInt(Math.Ceiling(Math.Sqrt(paths.Count))))
            CollageWidth = 2400
            CollageGap = 24
            CollageMargin = 48
            CollageFormat = "JPG"
            CollageQuality = If(_mainVm?.Settings IsNot Nothing, _mainVm.Settings.JpgSaveQuality, 90)
            CollageLayoutMode = "Grid"
            CollageHeroIndex = 0
            CollageHeroPosition = "Center"
            CollageRandomSeed = Environment.TickCount
            CollageOrderSeed = Nothing
            CollagePreviewZoom = 1.0
            IsCollageDialogOpen = True
        End Function

        ''' "Neu mischen" - würfelt sowohl die Bild-REIHENFOLGE (in allen Layouts, im Hero-Layout
        ''' bleibt nur das gewählte Hero-Bild selbst unberührt) als auch, nur im Zufallsmodus
        ''' sichtbar, Größe/Rotation jedes Bilds neu.
        Public Sub ReshuffleCollageRandom()
            CollageRandomSeed = Environment.TickCount
            CollageOrderSeed = Environment.TickCount + 1
        End Sub

        ''' Bestimmt anhand der zuletzt gerenderten Vorschau-Slots (CollageService.LastPreviewSlots),
        ''' welches Bild an einer Klickposition (in Pixeln, im selben Koordinatenraum wie die Slots -
        ''' das Code-Behind rechnet Zoom bereits heraus) liegt, und setzt es als neues Hero-Bild.
        Public Sub SetCollageHeroFromPreviewClick(pixelX As Double, pixelY As Double)
            If Not IsCollageHeroMode Then Return
            Dim slots = CollageService.LastPreviewSlots
            If slots Is Nothing OrElse slots.Count = 0 Then Return

            Dim hit = slots.FirstOrDefault(Function(s) pixelX >= s.X AndAlso pixelX <= s.X + s.Width AndAlso
                                                        pixelY >= s.Y AndAlso pixelY <= s.Y + s.Height)
            If hit IsNot Nothing Then CollageHeroIndex = hit.SourceIndex
        End Sub

        Public Sub CloseCollageDialog()
            IsCollageDialogOpen = False
            _collagePreviewTimer.Stop()
            CollagePreviewImage = Nothing
        End Sub

        Private Sub ScheduleCollagePreviewUpdate()
            _collagePreviewTimer.Stop()
            _collagePreviewTimer.Start()
        End Sub

        Private Async Sub RefreshCollagePreviewAsync()
            Try
                Dim requestId = Interlocked.Increment(_collagePreviewRequestId)
                If Not IsCollageDialogOpen Then Return

                Dim paths = CollageSourcePaths()
                If paths.Count < 2 Then
                    CollagePreviewImage = Nothing
                    Return
                End If

                Dim options = New CollageOptions With {
                    .Width = CollageWidth,
                    .Columns = CollageColumns,
                    .Gap = CollageGap,
                    .Margin = CollageMargin,
                    .BackgroundColor = CollageBackgroundColor,
                    .LayoutMode = CollageLayoutMode,
                    .HeroIndex = CollageHeroIndex,
                    .HeroPosition = CollageHeroPosition,
                    .RandomSeed = CollageRandomSeed,
                    .OrderSeed = CollageOrderSeed
                }

                Dim preview = Await Task.Run(Function() CollageService.RenderPreview(paths, options, 900))
                If requestId <> _collagePreviewRequestId OrElse Not IsCollageDialogOpen Then Return
                CollagePreviewImage = preview
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryViewModel.RefreshCollagePreviewAsync", ex)
                StatusText = LocalizationService.T("Aktion fehlgeschlagen")
            End Try
        End Sub

        Public Async Sub CreateCollage()
            Try
                Dim paths = CollageSourcePaths()
                If paths.Count < 2 Then
                    StatusText = LocalizationService.T("Für eine Collage müssen mindestens zwei Bilder ausgewählt sein.")
                    Return
                End If
                If String.IsNullOrWhiteSpace(CurrentFolder) OrElse Not Directory.Exists(CurrentFolder) Then Return

                Dim baseName = IO.Path.GetFileNameWithoutExtension(If(String.IsNullOrWhiteSpace(CollageBaseName), "Collage", CollageBaseName.Trim()))
                If String.IsNullOrWhiteSpace(baseName) Then baseName = "Collage"
                Dim ext = If(String.Equals(CollageFormat, "PNG", StringComparison.OrdinalIgnoreCase), ".png",
                          If(String.Equals(CollageFormat, "WEBP", StringComparison.OrdinalIgnoreCase), ".webp",
                          If(String.Equals(CollageFormat, "PDF", StringComparison.OrdinalIgnoreCase), ".pdf",
                          If(String.Equals(CollageFormat, "FPX", StringComparison.OrdinalIgnoreCase), ".fpx", ".jpg"))))
                Dim target = MakeUniquePath(IO.Path.Combine(CurrentFolder, baseName & ext))
                Dim options = New CollageOptions With {
                    .OutputPath = target,
                    .Width = CollageWidth,
                    .Columns = CollageColumns,
                    .Gap = CollageGap,
                    .Margin = CollageMargin,
                    .BackgroundColor = CollageBackgroundColor,
                    .Format = CollageFormat,
                    .Quality = CollageQuality,
                    .LayoutMode = CollageLayoutMode,
                    .HeroIndex = CollageHeroIndex,
                    .HeroPosition = CollageHeroPosition,
                    .RandomSeed = CollageRandomSeed,
                    .OrderSeed = CollageOrderSeed
                }

                ' Dieselbe Frage wie vor dem Drucken, aus demselben Grund: was hier gerechnet wird,
                ' laeuft ohne Rueckfrage durch, und Minuten je Bild will niemand ungefragt.
                options.ApplyPendingBakedOperations = Await _mainVm.AskApplyPendingBakedAsync(paths)

                IsCollageDialogOpen = False
                _collagePreviewTimer.Stop()
                CollagePreviewImage = Nothing
                StatusText = LocalizationService.T("Collage wird erstellt...")
                Dim ok = Await Task.Run(Function() CollageService.SaveCollage(paths, options))
                StatusText = If(ok, $"Collage gespeichert: {IO.Path.GetFileName(target)}", "Collage konnte nicht erstellt werden")
                If ok Then SyncFolderItems()
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("GalleryViewModel.CreateCollage", ex)
                StatusText = LocalizationService.T("Aktion fehlgeschlagen")
            End Try
        End Sub

        Private Shared Function MakeUniquePath(path As String) As String
            If Not File.Exists(path) Then Return path
            Dim dir = IO.Path.GetDirectoryName(path)
            Dim name = IO.Path.GetFileNameWithoutExtension(path)
            Dim ext = IO.Path.GetExtension(path)
            Dim index = 2
            Do
                Dim candidate = IO.Path.Combine(dir, $"{name}_{index}{ext}")
                If Not File.Exists(candidate) Then Return candidate
                index += 1
            Loop
        End Function

        ''' <summary>Quellen, aus denen eine Collage Bildinhalt beziehen kann. Gefragt wird DIESELBE
        ''' Funktion wie bei den übrigen Stapelaktionen (IsBatchImageEditReadable) - die eigene
        ''' Endungsliste hier kannte weder RAW noch .fpx, obwohl das Menü sie anbot: die Auswahl
        ''' schrumpfte still auf null und der Dialog verlangte "mindestens zwei Bilder". ICO steht
        ''' zusätzlich drin, weil der Renderer es lesen kann und die Collage es bisher annahm.</summary>
        ''' <summary>Die Quellpfade der Collage aus der Auswahl, OHNE zu laden.
        '''
        ''' Fuer ein Serverelement steht hier die Kopie, die beim Oeffnen des Dialogs in den
        ''' Temp-Ordner geholt wurde - gleich von welchem Server. Vorschau und Speichern laufen nach dem Oeffnen, das Laden ist
        ''' also schon passiert - sie duerfen es nur nicht erneut anstossen, sonst laedt jede
        ''' Vorschau-Aktualisierung von vorn.</summary>
        Private Function CollageSourcePaths() As List(Of String)
            Return If(SelectedItems, Enumerable.Empty(Of ImageItem)()).
                   Where(Function(i) i IsNot Nothing AndAlso i.IsImage AndAlso Not i.IsVideoFile).
                   Select(Function(i) If(i.IsRemoteAsset, If(i.RemoteLocalPath, ""), If(i.FilePath, ""))).
                   Where(Function(p) Not String.IsNullOrEmpty(p) AndAlso File.Exists(p) AndAlso IsCollageSourcePath(p)).
                   ToList()
        End Function

        Private Shared Function IsCollageSourcePath(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            If IO.Path.GetExtension(path).ToLowerInvariant() = ".ico" Then Return True
            Return IsBatchImageEditReadable(path)
        End Function

        Public Sub StoreClipboard(cut As Boolean)
            Dim paths = GetSelectedPaths()
            paths = paths.Where(Function(p) If(cut, FileOperationPolicy.CanRename(p), FileOperationPolicy.CanCopy(p))).ToList()
            If paths.Count = 0 Then
                StatusText = LocalizationService.T("Kein Element ausgewählt")
                Return
            End If
            _clipboardPaths = paths
            _clipboardCut = cut
            StatusText = If(_clipboardPaths.Count = 1,
                            LocalizationService.T("1 Element in der Zwischenablage"),
                            String.Format(LocalizationService.T("{0} Elemente in der Zwischenablage"), _clipboardPaths.Count))
        End Sub

        Public Sub StoreClipboardPaths(paths As IEnumerable(Of String), cut As Boolean)
            Dim validPaths = If(paths, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrEmpty(p)).
                Where(Function(p) If(cut, FileOperationPolicy.CanRename(p), FileOperationPolicy.CanCopy(p))).
                Distinct(PathIdentity.Comparer).
                ToList()
            If validPaths.Count = 0 Then
                StatusText = LocalizationService.T("Kein Element ausgewählt")
                Return
            End If
            _clipboardPaths = validPaths
            _clipboardCut = cut
            StatusText = If(_clipboardPaths.Count = 1,
                            LocalizationService.T("1 Element in der Zwischenablage"),
                            String.Format(LocalizationService.T("{0} Elemente in der Zwischenablage"), _clipboardPaths.Count))
        End Sub

        Public Async Function PasteIntoFolderAsync(targetFolder As String) As Task
            If IsVirtualFolderPath(targetFolder) Then Return
            If String.IsNullOrEmpty(targetFolder) OrElse Not Directory.Exists(targetFolder) OrElse _clipboardPaths.Count = 0 Then Return
            If Not FileOperationPolicy.CanPasteInto(targetFolder) Then Return
            Await PastePathsIntoFolderAsync(_clipboardPaths.ToList(), targetFolder, _clipboardCut)
            If _clipboardCut Then _clipboardPaths.Clear()
        End Function

        Public Sub PastePathsIntoFolder(paths As IEnumerable(Of String), targetFolder As String, Optional cut As Boolean = False)
            Dim ignored = PastePathsIntoFolderAsync(paths, targetFolder, cut)
        End Sub

        Public Async Function PastePathsIntoFolderAsync(paths As IEnumerable(Of String), targetFolder As String, Optional cut As Boolean = False) As Task
            If IsVirtualFolderPath(targetFolder) Then Return
            If paths Is Nothing OrElse String.IsNullOrEmpty(targetFolder) OrElse Not Directory.Exists(targetFolder) Then Return
            If Not FileOperationPolicy.CanPasteInto(targetFolder) Then Return
            ' Serverelemente (Pseudo-Pfade) werden nicht dateikopiert, sondern als Originale in den
            ' Zielordner heruntergeladen - beide Server. Die restliche (lokale) Kopierlogik ignoriert
            ' Pseudo-Pfade ohnehin (File.Exists).
            Dim serverPseudo = paths.Where(Function(p) LibraryService.IsServerPseudoPath(p)).ToList()
            If serverPseudo.Count > 0 Then Await DownloadServerAssetsToFolderAsync(serverPseudo, targetFolder)
            _conflictBatchDecision = Nothing
            Dim errorMessage As String = Nothing
            Dim sourcePaths As List(Of String) = Nothing
            Dim completedSources As New List(Of String)()
            Try
                sourcePaths = paths.
                    Where(Function(p) Not String.IsNullOrEmpty(p) AndAlso (File.Exists(p) OrElse Directory.Exists(p))).
                    Distinct(PathIdentity.Comparer).
                    Where(Function(p) Not String.Equals(NormalizePath(p), NormalizePath(targetFolder), PathIdentity.Comparison)).
                    Where(Function(p) If(cut, FileOperationPolicy.CanMove(p, targetFolder), FileOperationPolicy.CanCopy(p))).
                    ToList()

                For Each source In sourcePaths
                    If Await CopyOrMovePathAsync(source, targetFolder, cut) Then
                        completedSources.Add(source)
                    End If
                Next
                ClearSelection()
                If _isVirtualFolder Then
                    If cut Then RemovePathsFromVirtualFolder(completedSources)
                    FilterAndSort()
                Else
                    SyncFolderItems()
                End If
                RefreshTree()
            Catch ex As Exception
                errorMessage = ex.Message
            End Try
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Einfügen fehlgeschlagen"), errorMessage)
        End Function

        ''' <summary>Lädt Serveroriginale (Pseudo-Pfade) in einen lokalen Zielordner herunter - der
        ''' Server→lokal-Zweig von Einfügen und Ziehen. Kollidierende Namen werden nummeriert.
        '''
        ''' Beide Server, EIN Weg: das Element weiß über <c>EnsureLocalOriginalAsync</c> selbst, wo
        ''' es herholt (Zeitachse wie Papierkorb). Vorher stand hier der Immich-Abruf fest verdrahtet,
        ''' und ein Nextcloud-Bild ließ sich gar nicht erst in einen Ordner ziehen.</summary>
        Private Async Function DownloadServerAssetsToFolderAsync(pseudoPaths As List(Of String), targetFolder As String) As Task
            Dim total = pseudoPaths.Count
            Dim done = 0
            Dim saved = 0
            For Each pseudo In pseudoPaths
                Dim istImmich = ImmichService.IsImmichPseudoPath(pseudo)
                Dim item = CreateServerItemFromPseudoPath(pseudo, istImmich, CancellationToken.None)
                If item Is Nothing Then Continue For
                done += 1
                StatusText = String.Format(LocalizationService.T("Lade vom Server… ({0}/{1})"), done, total)
                Dim temp = Await item.EnsureLocalOriginalAsync()
                If String.IsNullOrEmpty(temp) OrElse Not File.Exists(temp) Then Continue For
                Try
                    Dim fileName = If(String.IsNullOrEmpty(item.FileName), IO.Path.GetFileName(temp), item.FileName)
                    Dim dest = MakeUniqueFilePath(IO.Path.Combine(targetFolder, fileName))
                    File.Copy(temp, dest, False)
                    saved += 1
                Catch ex As Exception
                    DiagnosticLogService.LogException("Server.DownloadToFolder", ex)
                End Try
            Next
            If Not _isVirtualFolder AndAlso String.Equals(NormalizePath(targetFolder), NormalizePath(_currentFolder), StringComparison.OrdinalIgnoreCase) Then
                SyncFolderItems()
            End If
            RefreshTree()
            StatusText = String.Format(LocalizationService.T("{0} Bilder vom Server gespeichert"), saved)
        End Function

        Private Shared Function MakeUniqueFilePath(path As String) As String
            If Not File.Exists(path) Then Return path
            Dim dir = IO.Path.GetDirectoryName(path)
            Dim stem = IO.Path.GetFileNameWithoutExtension(path)
            Dim ext = IO.Path.GetExtension(path)
            Dim i = 1
            Dim candidate As String
            Do
                candidate = IO.Path.Combine(dir, $"{stem} ({i}){ext}")
                i += 1
            Loop While File.Exists(candidate)
            Return candidate
        End Function

        Public Async Function DuplicateSelectedAsync() As Task
            If _isVirtualFolder Then Return
            Dim targets = GetSelectedPaths()
            If targets.Count = 0 OrElse String.IsNullOrEmpty(_currentFolder) OrElse Not FileOperationPolicy.CanPasteInto(_currentFolder) Then Return
            Dim errorMessage As String = Nothing
            Try
                For Each source In targets
                    If Not FileOperationPolicy.CanDuplicate(source, _currentFolder) Then Continue For
                    Await CopyOrMovePathAsync(source, _currentFolder, False, True)
                Next
                SyncFolderItems()
                RefreshTree()
            Catch ex As Exception
                errorMessage = ex.Message
            End Try
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Duplizieren fehlgeschlagen"), errorMessage)
        End Function

        ''' <summary>Der Ausgangspunkt einer Stapel-Bearbeitung fuer EINE Quelle.
        '''
        ''' Liegt neben einer RAW- oder PSD-Datei ein .fpxmp-Rezept, IST das die Grundlage: der
        ''' Stapel setzt dann auf der Bearbeitung auf, die im Editor entstanden ist, statt sie
        ''' stillschweigend zu verwerfen. Ein entwickeltes RAW kam sonst flach aus dem Stapel
        ''' zurueck, obwohl es im Editor und in der Galerie entwickelt aussieht.
        '''
        ''' .fpx bleibt aussen vor: dort rendert SaveImage das Buendel bereits aus Basisbild und
        ''' Rezept - ein zweites Mal angewandt kaeme die Bearbeitung doppelt.</summary>
        Private Shared Function BatchBaseAdjustments(sourcePath As String) As ImageAdjustments
            If RawSidecarService.IsSidecarFormat(sourcePath) AndAlso RawSidecarService.Exists(sourcePath) Then
                Dim rezept = RawSidecarService.TryRead(sourcePath)
                If rezept IsNot Nothing Then Return rezept
            End If
            Return New ImageAdjustments()
        End Function

        ''' <summary>Darf diese Quelle im Stapel voll entwickelt werden? Mit Rezept IMMER - die
        ''' eingebettete Vorschau waere dort schlicht das falsche Bild. Ohne Rezept entscheidet die
        ''' Einstellung; da geht es um Geschwindigkeit gegen Aufloesung.</summary>
        Private Shared Function BatchDevelopsRaw(sourcePath As String) As Boolean
            If Not RawPreviewService.IsSupportedRaw(sourcePath) Then Return True
            If RawSidecarService.Exists(sourcePath) Then Return True
            Return AppSettingsService.Load().DevelopRawInBatch
        End Function

        Private Shared ReadOnly BatchConvertExcludedExtensions As String() = {".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".svg"}
        ''' <summary>Kann die Stapel-Bildbearbeitung (Groesse/Wasserzeichen/Filter) diese Datei
        ''' schreiben? Die Menuesichtbarkeit MUSS dieselbe Frage stellen wie
        ''' GetSelectedBatchEditableImageItems - sonst stehen Eintraege da, die beim Klick still
        ''' nichts tun (.tif/.bmp/.heic waren sichtbar, der Dialog erschien nie).</summary>
        Public Shared Function IsBatchImageEditReadable(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            If BatchImageEditReadableExtensions.Contains(IO.Path.GetExtension(path).ToLowerInvariant()) Then Return True
            ' RAW, PSD und .fpx sind LESBARE Quellen wie jede andere - SaveImage rendert sie
            ' (RAW ueber die eingebettete Vorschau, PSD ueber das Gesamtbild, .fpx aus Basisbild +
            ' Rezept). Nur ZURUECKschreiben kann man sie nicht, und genau das entscheidet
            ' IsBatchImageEditWritable weiter unten - dort wird "Originale ueberschreiben"
            ' gesperrt. Die Endungen kommen aus den zustaendigen Diensten statt aus einer zweiten
            ' Liste hier: eine neu unterstuetzte RAW-Endung soll nicht an zwei Stellen gepflegt
            ' werden muessen.
            Return RawPreviewService.IsSupportedRaw(path) OrElse
                   PsdPreviewService.IsSupportedPsd(path) OrElse
                   FpxService.IsFpx(path)
        End Function

        Public Shared Function IsBatchImageEditWritable(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Return BatchImageEditWritableExtensions.Contains(IO.Path.GetExtension(path).ToLowerInvariant())
        End Function

        ''' <summary>Kann "Exportieren nach"/"Konvertieren nach" diese Datei lesen? (Videos und SVG
        ''' nicht - deren Eintraege blieben sonst wirkungslos sichtbar.)</summary>
        Public Shared Function IsBatchExportable(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Return Not BatchConvertExcludedExtensions.Contains(IO.Path.GetExtension(path).ToLowerInvariant())
        End Function

        ''' <summary>Formate, die die Stapel-Bildbearbeitung als QUELLE lesen kann. BMP/GIF (Skia)
        ''' und HEIC/HEIF/AVIF (libheif) sind dabei, obwohl sie sich nicht zurueckschreiben lassen -
        ''' dafuer entstehen NEUE Dateien im gewaehlten Zielformat. GIF liefert das erste Einzelbild.
        ''' HEIC steht hier ohne Verfuegbarkeitspruefung: ohne libheif scheitert der Decode sichtbar,
        ''' statt dass der Menuepunkt je nach System verschwindet.</summary>
        Private Shared ReadOnly BatchImageEditReadableExtensions As String() = {".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
                                                                                ".heic", ".heif", ".hif", ".avif",
                                                                                ".tif", ".tiff"}

        ''' <summary>Formate, die sich AN ORT UND STELLE ueberschreiben lassen - dafuer braucht es
        ''' einen Encoder fuer genau dieses Format (siehe ImageProcessor.CanEncodeToTargetExtension).
        ''' Skia hat keinen BMP-/GIF-Encoder, deshalb ist "Originale ueberschreiben" fuer solche
        ''' Auswahlen gesperrt statt still wirkungslos.</summary>
        Private Shared ReadOnly BatchImageEditWritableExtensions As String() = {".jpg", ".jpeg", ".png", ".webp"}

        ''' <summary>Function statt Sub, damit der Befehl als CreateFromTask darauf warten kann: er
        ''' bleibt dadurch bis zum Ende gesperrt. Vorher war er nach dem Klick sofort wieder
        ''' bedienbar, und ein zweiter Aufruf mitten im Schreiblauf öffnete einen zweiten Dialog und
        ''' schrieb ein zweites Mal über dieselben Dateien.</summary>
        Private Async Function ResizeSelectedAsync() As Task
            Try
                Await ResizeImageItemsAsync(GetSelectedBatchEditableImageItems())
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryViewModel.ResizeSelected", ex)
                StatusText = LocalizationService.T("Aktion fehlgeschlagen")
            End Try
        End Function

        ''' <summary>Der komplette "Bildgröße ändern"-Ablauf für eine Liste von Bildern - Dialog,
        ''' Überschreiben oder Kopie, lokal oder nach Immich. Öffentlich, weil der Betrachter mit
        ''' Strg+R denselben Ablauf für das gerade angezeigte Bild auslöst: eine Umsetzung, nicht zwei.</summary>
        ''' <summary>Die drei Stapel-Ablaeufe fuer eine VORGEGEBENE Liste. Der Betrachter loest damit
        ''' dasselbe fuer das angezeigte Bild aus, was die Galerie fuer ihre Auswahl tut - dieselbe
        ''' Umsetzung, nur ein anderer Ausgangspunkt (wie bei ResizeImageItemsAsync).</summary>
        Public Sub ConvertImageItems(items As IList(Of ImageItem))
            If items Is Nothing OrElse items.Count = 0 Then Return
            BatchConvertSelected(items)
        End Sub

        Public Sub ExportImageItems(items As IList(Of ImageItem))
            If items Is Nothing OrElse items.Count = 0 Then Return
            ExportSelected(items)
        End Sub

        Public Sub ApplyWatermarkToImageItems(items As IList(Of ImageItem))
            If items Is Nothing OrElse items.Count = 0 Then Return
            ApplyWatermarkSelected(items)
        End Sub

        Public Sub ApplyFilterToImageItems(items As IList(Of ImageItem))
            If items Is Nothing OrElse items.Count = 0 Then Return
            ApplyFilterSelected(items)
        End Sub

        ''' <summary>Der Mantel um den eigentlichen Ablauf: er raeumt die Abbruch-Marke des Stapels
        ''' auf JEDEM Weg wieder ab. Ohne ihn bliebe sie nach einem fruehen Ausstieg stehen und
        ''' zeigte auf einen Lauf, den es nicht mehr gibt.</summary>
        Public Async Function ResizeImageItemsAsync(items As IList(Of ImageItem)) As Task
            Try
                Await ResizeImageItemsCoreAsync(items)
            Finally
                EndBatchRun()
            End Try
        End Function

        Private Async Function ResizeImageItemsCoreAsync(items As IList(Of ImageItem)) As Task
            Dim targetItems = If(items, New List(Of ImageItem)()).Where(Function(i) i IsNot Nothing).ToList()
            If targetItems.Count = 0 Then Return

            Dim samplePath = Await EnsureLocalPathForBatchAsync(targetItems(0))
            Dim folderHint = BatchFolderHint(targetItems)
            ' Ueberschreiben nur anbieten, wenn JEDE Quelle ihr eigenes Format auch schreiben kann
            ' (BMP/GIF koennen es nicht - dort entstehen neue Dateien).
            Dim ueberschreibbar = targetItems.All(Function(i) IsBatchImageEditWritable(i.FilePath))
            Await _mainVm.PreparePendingBakedOptionAsync(targetItems.Select(Function(i) i.FilePath))
            Dim resize = Await _mainVm.ShowBatchResizeAsync(samplePath, folderHint, ueberschreibbar,
                                                           singleImage:=targetItems.Count = 1,
                                                           sourcesIncludeJpg:=BatchIncludesJpg(targetItems))
            If resize Is Nothing Then Return
            ' Der Haken wird JETZT festgehalten: der Schreiblauf liest ihn im Hintergrund, und bis
            ' dahin kann der naechste Dialog ihn laengst zurueckgesetzt haben.
            Dim applyPendingBaked = _mainVm.DialogApplyPendingBaked

            StatusText = LocalizationService.T("Ändere Bildgröße...")
            ' Der Knopf "EXIF" im Uebernehmen-Bereich des Dialogs entscheidet je Lauf; die
            ' Einstellung ist nur noch die Vorbelegung.
            Dim preserveMetadata = resize.PreserveMetadata
            ' Auch beim Ueberschreiben zaehlt der Regler des Dialogs (Vorbelegung 95) - vorher
            ' wurde hier still mit fester Qualitaet gespeichert.
            Dim jpgQuality = resize.JpgQuality
            ' Die Marke fuer den ganzen Lauf. Sie geht in jedes SaveImage und von dort in die
            ' Modelldienste: das Hochskalieren steigt an der naechsten Kachelgrenze aus, und das
            ' angefangene Bild wird NICHT geschrieben.
            Dim cancel = BeginBatchRun(targetItems.Count)
            Dim writer = Function(source As String, target As String)
                             ' Prozent NICHT vorab aus der Datei schaetzen: SKCodec kennt die Masse
                             ' von RAW/PSD/.fpx nicht (das ergab stumm die Originalgroesse, und ohne
                             ' Null-Wache sogar ein 1x1-Bild) und liegt bei EXIF-gedrehten JPEGs um
                             ' die Drehung daneben. Die Engine rechnet es auf dem fertig dekodierten
                             ' Bild.
                             ' Auf dem .fpxmp-Rezept aufsetzen, falls es eines gibt - die
                             ' Groessenfelder gehoeren dem Stapel und ueberschreiben es dort.
                             Dim adj = BatchBaseAdjustments(source)
                             adj.ResizeWidth = resize.Width
                             adj.ResizeHeight = resize.Height
                             adj.ResizeScalePercent = resize.ScalePercent
                             adj.ResizeFitInsideBox = True
                             adj.LockResizeAspect = resize.LockAspect
                             adj.NoResizeUpscale = resize.NoUpscale
                             adj.ResizeInterpolation = resize.Interpolation
                             ' Das Vergroessern laeuft im Speicherweg VOR der Reglerkette, also vor
                             ' den Groessenfeldern darueber. Beides zusammen ist damit sinnvoll:
                             ' erst vierfach durch das Modell, dann auf das eingetragene Zielmass
                             ' herunter.
                             adj.UpscaleModel = resize.UpscaleModel
                             Return ImageProcessor.SaveImage(source, target, adj, jpgQuality, preserveMetadata,
                                                             developRaw:=BatchDevelopsRaw(source),
                                                             applyPendingBaked:=applyPendingBaked,
                                                             copyrightText:=resize.Copyright,
                                                             cancel:=cancel)
                         End Function

            ' LOKAL heisst "hat eine Datei auf dieser Platte" - nicht "ist kein Immich". Ein
            ' Nextcloud-Element fiel vorher in diesen Topf und wurde mit seinem Pseudo-Pfad wie eine
            ' Datei behandelt.
            Dim localItems = targetItems.Where(Function(i) Not i.IsRemoteAsset).ToList()
            ' Alle Serverelemente. Der Export in einen ORDNER kann sie alle (die Quelle wird geholt);
            ' das Zurueckschreiben nach Immich ueberspringt fremde Quellen von selbst.
            Dim immichItems = targetItems.Where(Function(i) i.IsRemoteAsset).ToList()
            Dim uploadedAssetIds As New List(Of String)()
            Dim changedCount = 0
            Dim uploadedCount = 0

            If resize.Overwrite Then
                Dim localTargets = localItems.Where(Function(i) File.Exists(i.FilePath)).Select(Function(i) i.FilePath).ToList()
                changedCount = Await RewriteImagesInPlaceAsync(localTargets, writer)
                ' In Immich gibt es kein Überschreiben an Ort und Stelle - dort entsteht wie bei den
                ' übrigen Stapelaktionen ein neues Asset.
                uploadedCount = Await ProcessImmichBatchItemsAsync(immichItems, writer,
                                                                   Function(source) IO.Path.GetExtension(source),
                                                                   uploadedAssetIds).ConfigureAwait(True)
                StatusText = BatchResultText(LocalizationService.T("{0} von {1} Datei(en) geändert"), changedCount + uploadedCount, targetItems.Count)
                RefreshAfterBatchFileRewrite(localTargets)
                If uploadedCount > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
                Return
            End If

            ' Als Kopie mit Formatauswahl - gleicher Ablauf wie beim
            ' Stapel-Filter: neue Dateien in den Zielordner oder als neues Asset nach Immich.
            If String.Equals(resize.Target, "Immich", StringComparison.OrdinalIgnoreCase) AndAlso ImmichService.IsConfigured Then
                changedCount = Await ProcessLocalBatchItemsToImmichAsync(localItems, writer,
                                                                         Function(source) resize.Extension,
                                                                         uploadedAssetIds, "", skipSameExtension:=False).ConfigureAwait(True)
                uploadedCount = Await ProcessImmichBatchItemsAsync(immichItems, writer,
                                                                   Function(source) resize.Extension,
                                                                   uploadedAssetIds, "").ConfigureAwait(True)
                StatusText = BatchResultText(LocalizationService.T("{0} von {1} Datei(en) geändert"), changedCount + uploadedCount, targetItems.Count)
                If uploadedAssetIds.Count > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
                Return
            End If

            Dim targetFolder = If(resize.TargetFolder, "").Trim()
            If String.IsNullOrWhiteSpace(targetFolder) Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Bildgröße ändern"), LocalizationService.T("Kein Zielordner angegeben."))
                Return
            End If
            Dim createFolderError As String = Nothing
            Try
                Directory.CreateDirectory(targetFolder)
            Catch ex As Exception
                createFolderError = ex.Message
            End Try
            If createFolderError IsNot Nothing Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Bildgröße ändern"), createFolderError)
                Return
            End If

            Dim nameBuilder = CreateNameBuilder(resize.NamePattern)
            changedCount = Await ProcessLocalBatchItemsToFolderAsync(localItems, targetFolder, writer,
                                                                     Function(source) resize.Extension,
                                                                     "", skipSameExtension:=False,
                                                                     metaCopy:=resize.MetaCopy,
                                                                     nameBuilder:=nameBuilder).ConfigureAwait(True)
            uploadedCount = Await ProcessImmichBatchItemsToFolderAsync(immichItems, targetFolder, writer,
                                                                       Function(source) resize.Extension,
                                                                       "", nameBuilder).ConfigureAwait(True)

            StatusText = BatchResultText(LocalizationService.T("{0} von {1} Datei(en) geändert"), changedCount + uploadedCount, targetItems.Count)
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then SyncFolderItems()
        End Function

        ''' <summary>Stapel: einen eingebauten Filter, ein XMP-Preset (.xmp) oder eine LUT (.cube) auf
        ''' die Auswahl anwenden - entweder in die Originale hinein oder in neue Dateien (mit dem Namen der
        ''' Vorgabe im Dateinamen).</summary>
        ''' <param name="vorgabe">Siehe BatchConvertSelected.</param>
        ''' <summary>Der Ordner fuer den Knopf "Aktueller Ordner" eines Stapeldialogs: der
        ''' gemeinsame Ordner der Dateien selbst - erst wenn die Dateien verstreut liegen, der
        ''' offene Galerie-Ordner. Aus Viewer und Editor kommen die Dialoge mit dem EINEN aktuellen
        ''' Bild, waehrend die Galerie in einer Suchliste oder in Immich stehen kann: der Knopf
        ''' fehlte dort, obwohl das Bild einen eindeutigen Ordner hat. Immich-Assets und ihre
        ''' Tempkopien bleiben aussen vor - ein Temp-Ordner ist kein sinnvolles Ziel.</summary>
        Private Function BatchFolderHint(targetItems As IEnumerable(Of ImageItem)) As String
            Dim gemeinsam As String = Nothing
            For Each item In If(targetItems, Enumerable.Empty(Of ImageItem)())
                If item Is Nothing OrElse String.IsNullOrWhiteSpace(item.FilePath) Then Continue For
                ' Serverelemente haben keinen sinnvollen Ordner, und ihre Temp-Kopie erst recht
                ' nicht - dann greift die Vorgabe des Dialogs statt eines Temp-Pfads. Die Frage geht
                ' an BEIDE Server: aus Betrachter und Editor kommen die Elemente mit dem Pfad ihrer
                ' Kopie, IsRemoteAsset ist daran False.
                If item.IsRemoteAsset OrElse LibraryService.IsServerTempPath(item.FilePath) Then
                    gemeinsam = Nothing
                    Exit For
                End If
                Dim ordner = IO.Path.GetDirectoryName(item.FilePath)
                If String.IsNullOrWhiteSpace(ordner) Then Continue For
                If gemeinsam Is Nothing Then
                    gemeinsam = ordner
                ElseIf Not PathIdentity.AreSame(gemeinsam, ordner) Then
                    gemeinsam = Nothing
                    Exit For
                End If
            Next
            If Not String.IsNullOrWhiteSpace(gemeinsam) Then Return gemeinsam
            ' In einer Suchliste oder in Immich gibt es keinen echten Ordner - dann greift die
            ' Vorgabe des Dialogs (zuletzt genutzter Exportordner).
            Return If(_isVirtualFolder, "", If(_currentFolder, ""))
        End Function

        ''' <summary>Sind JPG-Dateien im Stapel? Dann zeigt der Dialog den Qualitaetsregler auch
        ''' beim Ueberschreiben - die Dateien werden dabei neu encodiert.</summary>
        Private Shared Function BatchIncludesJpg(targetItems As IEnumerable(Of ImageItem)) As Boolean
            Return If(targetItems, Enumerable.Empty(Of ImageItem)()).Any(
                Function(i)
                    If i Is Nothing Then Return False
                    Dim ext = IO.Path.GetExtension(If(i.FilePath, "")).ToLowerInvariant()
                    Return ext = ".jpg" OrElse ext = ".jpeg"
                End Function)
        End Function

        ''' <summary>Mantel um den Ablauf, siehe <see cref="ResizeImageItemsAsync"/>: er raeumt die
        ''' Abbruch-Marke des Stapels auf jedem Weg wieder ab.</summary>
        Private Async Sub ApplyFilterSelected(Optional vorgabe As IList(Of ImageItem) = Nothing)
            Try
                Await ApplyFilterSelectedCoreAsync(vorgabe)
            Catch ex As Exception
                ' Eigenes Try, weil es ein Async Sub ist: eine Ausnahme darin landet sonst beim
                ' Dispatcher und beendet den Prozess.
                DiagnosticLogService.LogException("Gallery.ApplyFilter", ex)
            Finally
                EndBatchRun()
            End Try
        End Sub

        Private Async Function ApplyFilterSelectedCoreAsync(vorgabe As IList(Of ImageItem)) As Task
            Dim targetItems = If(vorgabe IsNot Nothing, vorgabe.ToList(), GetSelectedBatchEditableImageItems())
            If targetItems.Count = 0 Then Return

            Dim folderHint = BatchFolderHint(targetItems)
            Dim ueberschreibbar = targetItems.All(Function(i) IsBatchImageEditWritable(i.FilePath))
            Await _mainVm.PreparePendingBakedOptionAsync(targetItems.Select(Function(i) i.FilePath))
            Dim result = Await _mainVm.ShowBatchFilterAsync(targetItems.Count, folderHint, ueberschreibbar,
                                                            sourcesIncludeJpg:=BatchIncludesJpg(targetItems))
            If result Is Nothing Then Return
            Dim applyPendingBaked = _mainVm.DialogApplyPendingBaked

            Dim adjustmentsTemplate = BuildBatchFilterAdjustments(result)
            If adjustmentsTemplate Is Nothing Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Filter anwenden"), LocalizationService.T("Die gewählte Vorgabe konnte nicht gelesen werden."))
                Return
            End If

            StatusText = LocalizationService.T("Wende Filter an...")
            ' Der Knopf "EXIF" im Uebernehmen-Bereich des Dialogs entscheidet je Lauf; die
            ' Einstellung ist nur noch die Vorbelegung.
            Dim preserveMetadata = result.PreserveMetadata
            ' Jedes Bild bekommt seinen eigenen Klon: ApplyAdjustments schreibt Quellmaße hinein, ein
            ' geteiltes Objekt würde sie über die Dateien hinweg vermischen. Bei der automatischen
            ' Bildverbesserung wird zusätzlich PRO BILD gemessen - eine gemeinsame Vorlage gäbe allen
            ' Bildern die Korrektur des ersten.
            Dim isAutoEnhance = String.Equals(result.SourceKind, BatchFilterDialogResult.SourceAuto, StringComparison.OrdinalIgnoreCase)
            Dim cancel = BeginBatchRun(targetItems.Count)
            Dim writer = Function(source As String, target As String)
                             ' Rezept als Grundlage, der Look kommt oben drauf - und zwar nur mit
                             ' den Reglern, die er wirklich setzt (siehe MergeNonDefault...).
                             Dim adj = BatchBaseAdjustments(source)
                             adj.MergeNonDefaultPixelAdjustmentsFrom(adjustmentsTemplate)
                             If isAutoEnhance Then ImageProcessor.ApplyAutoAdjustmentsTo(adj, source)
                             Return ImageProcessor.SaveImage(source, target, adj, result.JpgQuality, preserveMetadata,
                                                             developRaw:=BatchDevelopsRaw(source),
                                                             applyPendingBaked:=applyPendingBaked,
                                                             copyrightText:=result.Copyright,
                                                             cancel:=cancel)
                         End Function

            ' LOKAL heisst "hat eine Datei auf dieser Platte" - nicht "ist kein Immich". Ein
            ' Nextcloud-Element fiel vorher in diesen Topf und wurde mit seinem Pseudo-Pfad wie eine
            ' Datei behandelt.
            Dim localItems = targetItems.Where(Function(i) Not i.IsRemoteAsset).ToList()
            ' Alle Serverelemente. Der Export in einen ORDNER kann sie alle (die Quelle wird geholt);
            ' das Zurueckschreiben nach Immich ueberspringt fremde Quellen von selbst.
            Dim immichItems = targetItems.Where(Function(i) i.IsRemoteAsset).ToList()
            Dim uploadedAssetIds As New List(Of String)()
            Dim changedCount = 0
            Dim uploadedCount = 0

            If result.Overwrite Then
                Dim localPaths = localItems.Where(Function(i) File.Exists(i.FilePath)).Select(Function(i) i.FilePath).ToList()
                changedCount = Await RewriteImagesInPlaceAsync(localPaths, writer)
                ' In Immich gibt es kein Überschreiben an Ort und Stelle - dort entsteht wie bei den
                ' übrigen Stapelaktionen ein neues Asset.
                uploadedCount = Await ProcessImmichBatchItemsAsync(immichItems, writer,
                                                                   Function(source) IO.Path.GetExtension(source),
                                                                   uploadedAssetIds).ConfigureAwait(True)
                StatusText = BatchResultText(LocalizationService.T("{0} von {1} Datei(en) gefiltert"), changedCount + uploadedCount, targetItems.Count)
                RefreshAfterBatchFileRewrite(localPaths)
                If uploadedCount > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
                Return
            End If

            Dim suffix = result.FileNameSuffix
            If String.Equals(result.Target, "Immich", StringComparison.OrdinalIgnoreCase) AndAlso ImmichService.IsConfigured Then
                changedCount = Await ProcessLocalBatchItemsToImmichAsync(localItems, writer,
                                                                         Function(source) result.Extension,
                                                                         uploadedAssetIds, suffix, skipSameExtension:=False).ConfigureAwait(True)
                uploadedCount = Await ProcessImmichBatchItemsAsync(immichItems, writer,
                                                                   Function(source) result.Extension,
                                                                   uploadedAssetIds, suffix).ConfigureAwait(True)
                StatusText = BatchResultText(LocalizationService.T("{0} von {1} Datei(en) gefiltert"), changedCount + uploadedCount, targetItems.Count)
                If uploadedAssetIds.Count > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
                Return
            End If

            Dim targetFolder = If(result.TargetFolder, "").Trim()
            If String.IsNullOrWhiteSpace(targetFolder) Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Filter anwenden"), LocalizationService.T("Kein Zielordner angegeben."))
                Return
            End If
            Dim createFolderError As String = Nothing
            Try
                Directory.CreateDirectory(targetFolder)
            Catch ex As Exception
                createFolderError = ex.Message
            End Try
            If createFolderError IsNot Nothing Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Filter anwenden"), createFolderError)
                Return
            End If

            Dim nameBuilder = CreateNameBuilder(result.NamePattern)
            changedCount = Await ProcessLocalBatchItemsToFolderAsync(localItems, targetFolder, writer,
                                                                     Function(source) result.Extension,
                                                                     suffix, skipSameExtension:=False,
                                                                     metaCopy:=result.MetaCopy,
                                                                     nameBuilder:=nameBuilder).ConfigureAwait(True)
            uploadedCount = Await ProcessImmichBatchItemsToFolderAsync(immichItems, targetFolder, writer,
                                                                       Function(source) result.Extension,
                                                                       suffix, nameBuilder).ConfigureAwait(True)

            StatusText = BatchResultText(LocalizationService.T("{0} von {1} Datei(en) gefiltert"), changedCount + uploadedCount, targetItems.Count)
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then SyncFolderItems()
        End Function

        ''' <summary>Übersetzt die Dialogauswahl in Anpassungen. XMP-Presets laufen durch denselben
        ''' XmpPresetService wie der Editor - es gibt nur eine Abbildung der crs:-Schlüssel.
        ''' Nothing, wenn die Preset-Datei fehlt oder nichts Verwertbares enthält.</summary>
        Private Shared Function BuildBatchFilterAdjustments(result As BatchFilterDialogResult) As ImageAdjustments
            Select Case result.SourceKind
                Case BatchFilterDialogResult.SourceXmpPreset
                    Return XmpPresetService.LoadLook(result.PresetPath)

                Case BatchFilterDialogResult.SourceAdjustmentPreset
                    Return LoadAdjustmentPresetLook(result.DisplayName)

                Case BatchFilterDialogResult.SourceLut
                    If String.IsNullOrWhiteSpace(result.PresetPath) OrElse Not File.Exists(result.PresetPath) Then Return Nothing
                    Return New ImageAdjustments With {
                        .LutPath = result.PresetPath,
                        .LutStrength = result.Strength
                    }

                Case BatchFilterDialogResult.SourceAuto
                    ' Neutraler Startzustand: die eigentlichen Reglerwerte misst der Writer PRO BILD
                    ' (ImageProcessor.ApplyAutoAdjustmentsTo) - eine gemeinsame Vorlage gibt es nicht.
                    Return New ImageAdjustments()

                Case Else
                    If String.IsNullOrWhiteSpace(result.DisplayName) Then Return Nothing
                    Return New ImageAdjustments With {
                        .FilterPreset = result.DisplayName,
                        .FilterStrength = result.Strength
                    }
            End Select
        End Function

        ''' <summary>Holt eine im Anpassen-Werkzeug gespeicherte Vorlage über ihren Namen aus den
        ''' Einstellungen. Das Rezept enthält nur die Pixel-Anpassungen - genau das, was der Stapel
        ''' über die vorhandene Bearbeitung legt. Nothing, wenn es die Vorlage nicht mehr gibt oder
        ''' ihr Rezept unlesbar ist (von Hand bearbeitete Einstellungsdatei).</summary>
        Private Shared Function LoadAdjustmentPresetLook(name As String) As ImageAdjustments
            Dim trimmedName = If(name, "").Trim()
            If trimmedName.Length = 0 Then Return Nothing
            Dim preset = AppSettingsService.Load().AdjustmentPresets.
                FirstOrDefault(Function(p) String.Equals(p.Name, trimmedName, StringComparison.OrdinalIgnoreCase))
            If preset Is Nothing Then Return Nothing
            Try
                Return FpxService.DeserializeAdjustments(preset.RecipeJson)
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Function statt Sub, damit der Befehl als CreateFromTask darauf warten kann und
        ''' bis zum Ende gesperrt bleibt - sonst startet ein zweiter Klick mitten im Schreiblauf
        ''' einen zweiten Durchgang ueber dieselben Dateien (siehe ResizeSelectedAsync).</summary>
        ''' <returns>Wie viele Dateien tatsaechlich neu geschrieben wurden. Null heisst auch:
        ''' abgebrochen - der Betrachter laedt sein Bild dann nicht ohne Grund neu.</returns>
        Private Async Function RemoveMetadataSelectedAsync(Optional preset As IList(Of ImageItem) = Nothing) As Task(Of Integer)
            Try
                Dim targets = GetMetadataTargetPaths(preset)
                If targets.Count = 0 Then
                    StatusText = LocalizationService.T("Für diese Dateien lassen sich keine Metadaten entfernen.")
                    Return 0
                End If

                ' Rueckfrage: die Dateien werden AN ORT UND STELLE neu geschrieben, EXIF, XMP und
                ' ICC sind danach weg. Das laesst sich nicht zuruecknehmen.
                Dim frage = String.Format(
                    LocalizationService.T("Aus {0} Datei(en) werden Aufnahmedaten, XMP und Farbprofil entfernt. Die Dateien werden dabei neu geschrieben, das lässt sich nicht rückgängig machen."),
                    targets.Count)
                If Not Await _mainVm.ShowConfirmAsync(LocalizationService.T("Metadaten entfernen"), frage,
                                                      LocalizationService.T("Entfernen"),
                                                      LocalizationService.T("Abbrechen")) Then Return 0

                StatusText = LocalizationService.T("Entferne Metadaten...")
                ' Auch dieser Weg schreibt eine ganze Liste an Ort und Stelle und braucht deshalb
                ' seinen Zaehler; ohne Anmeldung stuende in der Anzeige "1 von 0".
                Dim cancel = BeginBatchRun(targets.Count)
                Dim changedCount = Await RewriteImagesInPlaceAsync(targets,
                    Function(source, temp) ImageProcessor.SaveImage(source, temp, New ImageAdjustments(), 95,
                                                                    preserveMetadata:=False, cancel:=cancel))

                StatusText = BatchResultText(LocalizationService.T("{0} von {1} Datei(en) bereinigt"), changedCount, targets.Count)
                RefreshAfterBatchFileRewrite(targets)
                Return changedCount
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryViewModel.RemoveMetadataSelected", ex)
                StatusText = LocalizationService.T("Aktion fehlgeschlagen")
                Return 0
            Finally
                EndBatchRun()
            End Try
        End Function

        ''' <summary>"Metadaten entfernen" fuer eine VORGEGEBENE Liste. Betrachter und Editor loesen
        ''' damit fuer ihr angezeigtes Bild dasselbe aus, was die Galerie fuer ihre Auswahl tut.</summary>
        Public Async Function RemoveMetadataForImageItemsAsync(items As IList(Of ImageItem)) As Task(Of Integer)
            If items Is Nothing OrElse items.Count = 0 Then Return 0
            Return Await RemoveMetadataSelectedAsync(items)
        End Function

        ''' <summary>Die Dateien, aus denen Metadaten entfernt werden: die VORGEGEBENE Liste, sonst
        ''' die Auswahl.
        '''
        ''' Massgeblich ist IsBatchImageEditWritable, nicht ...Readable: lesen koennen wir auch TIFF,
        ''' HEIC, BMP und GIF, schreiben nicht. Mit der Lese-Pruefung landete eine solche Datei in
        ''' der Liste und wurde im falschen Format zurueckgeschrieben.</summary>
        Private Function GetMetadataTargetPaths(preset As IList(Of ImageItem)) As List(Of String)
            Dim paths = If(preset IsNot Nothing AndAlso preset.Count > 0,
                           preset.Where(Function(i) i IsNot Nothing).Select(Function(i) i.FilePath),
                           GetSelectedPaths().AsEnumerable())
            Return paths.Where(Function(p) Not String.IsNullOrEmpty(p) AndAlso File.Exists(p) AndAlso
                                           IsBatchImageEditWritable(p)).ToList()
        End Function

        ''' <param name="vorgabe">Siehe BatchConvertSelected.</param>
        ''' <summary>Mantel um den Ablauf, siehe <see cref="ResizeImageItemsAsync"/>.</summary>
        Private Async Sub ApplyWatermarkSelected(Optional vorgabe As IList(Of ImageItem) = Nothing)
            Try
                Await ApplyWatermarkSelectedCoreAsync(vorgabe)
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.ApplyWatermark", ex)
            Finally
                EndBatchRun()
            End Try
        End Sub

        Private Async Function ApplyWatermarkSelectedCoreAsync(vorgabe As IList(Of ImageItem)) As Task
            Dim targetItems = If(vorgabe IsNot Nothing, vorgabe.ToList(), GetSelectedBatchEditableImageItems())
            If targetItems.Count = 0 Then Return

            Dim ueberschreibbar = targetItems.All(Function(i) IsBatchImageEditWritable(i.FilePath))
            Await _mainVm.PreparePendingBakedOptionAsync(targetItems.Select(Function(i) i.FilePath))
            Dim result = Await _mainVm.ShowWatermarkPresetDialogAsync(ueberschreibbar,
                                                                      currentFolder:=BatchFolderHint(targetItems),
                                                                      sourcesIncludeJpg:=BatchIncludesJpg(targetItems))
            If result Is Nothing OrElse result.Preset Is Nothing Then Return
            Dim applyPendingBaked = _mainVm.DialogApplyPendingBaked

            Dim annotation = CreateWatermarkAnnotation(result.Preset)
            If annotation Is Nothing Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Wasserzeichen anwenden"), LocalizationService.T("Das ausgewählte Wasserzeichen enthält keinen Text und kein Bild."))
                Return
            End If

            StatusText = LocalizationService.T("Wende Wasserzeichen an...")
            Dim nameBuilder = CreateNameBuilder(result.NamePattern)
            ' Der Knopf "EXIF" im Uebernehmen-Bereich des Dialogs entscheidet je Lauf; die
            ' Einstellung ist nur noch die Vorbelegung.
            Dim preserveMetadata = result.PreserveMetadata
            Dim cancel = BeginBatchRun(targetItems.Count)
            Dim writer = Function(source As String, target As String)
                             ' Das Wasserzeichen kommt ZUSAETZLICH auf die vorhandene Bearbeitung -
                             ' Objekte aus dem Rezept bleiben stehen.
                             Dim adj = BatchBaseAdjustments(source)
                             adj.Annotations.Add(annotation.Clone())
                             Return ImageProcessor.SaveImage(source, target, adj, result.JpgQuality, preserveMetadata,
                                                             developRaw:=BatchDevelopsRaw(source),
                                                             applyPendingBaked:=applyPendingBaked,
                                                             copyrightText:=result.Copyright,
                                                             cancel:=cancel)
                         End Function
            ' LOKAL heisst "hat eine Datei auf dieser Platte" - nicht "ist kein Immich". Ein
            ' Nextcloud-Element fiel vorher in diesen Topf und wurde mit seinem Pseudo-Pfad wie eine
            ' Datei behandelt.
            Dim localItems = targetItems.Where(Function(i) Not i.IsRemoteAsset).ToList()
            ' Alle Serverelemente. Der Export in einen ORDNER kann sie alle (die Quelle wird geholt);
            ' das Zurueckschreiben nach Immich ueberspringt fremde Quellen von selbst.
            Dim immichItems = targetItems.Where(Function(i) i.IsRemoteAsset).ToList()
            Dim uploadedAssetIds As New List(Of String)()
            Dim changedCount = 0
            Dim uploadedCount = 0

            If result.Overwrite Then
                Dim localTargets = localItems.Where(Function(i) File.Exists(i.FilePath)).Select(Function(i) i.FilePath).ToList()
                changedCount = Await RewriteImagesInPlaceAsync(localTargets, writer)
                uploadedCount = Await ProcessImmichBatchItemsAsync(immichItems, writer,
                                                                   Function(source) IO.Path.GetExtension(source),
                                                                   uploadedAssetIds).ConfigureAwait(True)
                StatusText = BatchResultText(LocalizationService.T("{0} von {1} Datei(en) mit Wasserzeichen versehen"), changedCount + uploadedCount, targetItems.Count)
                RefreshAfterBatchFileRewrite(localTargets)
                If uploadedCount > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
                Return
            End If

            If String.Equals(result.Target, "Immich", StringComparison.OrdinalIgnoreCase) AndAlso ImmichService.IsConfigured Then
                changedCount = Await ProcessLocalBatchItemsToImmichAsync(localItems, writer,
                                                                         Function(source) result.Extension,
                                                                         uploadedAssetIds, "", skipSameExtension:=False,
                                                                         nameBuilder:=nameBuilder).ConfigureAwait(True)
                uploadedCount = Await ProcessImmichBatchItemsAsync(immichItems, writer,
                                                                   Function(source) result.Extension,
                                                                   uploadedAssetIds, nameBuilder:=nameBuilder).ConfigureAwait(True)
                StatusText = BatchResultText(LocalizationService.T("{0} von {1} Datei(en) mit Wasserzeichen versehen"), changedCount + uploadedCount, targetItems.Count)
                If uploadedAssetIds.Count > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
                Return
            End If

            Dim targetFolder = If(result.TargetFolder, "").Trim()
            If String.IsNullOrWhiteSpace(targetFolder) Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Wasserzeichen anwenden"), LocalizationService.T("Kein Zielordner angegeben."))
                Return
            End If
            Dim createFolderError As String = Nothing
            Try
                Directory.CreateDirectory(targetFolder)
            Catch ex As Exception
                createFolderError = ex.Message
            End Try
            If createFolderError IsNot Nothing Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Wasserzeichen anwenden"), createFolderError)
                Return
            End If

            changedCount = Await ProcessLocalBatchItemsToFolderAsync(localItems, targetFolder, writer,
                                                                     Function(source) result.Extension,
                                                                     "", skipSameExtension:=False,
                                                                     metaCopy:=result.MetaCopy,
                                                                     nameBuilder:=nameBuilder).ConfigureAwait(True)
            uploadedCount = Await ProcessImmichBatchItemsToFolderAsync(immichItems, targetFolder, writer,
                                                                       Function(source) result.Extension,
                                                                       nameBuilder:=nameBuilder).ConfigureAwait(True)

            StatusText = BatchResultText(LocalizationService.T("{0} von {1} Datei(en) mit Wasserzeichen versehen"), changedCount + uploadedCount, targetItems.Count)
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then SyncFolderItems()
        End Function

        Private Shared Function CreateWatermarkAnnotation(preset As WatermarkPresetSettings) As ImageAnnotation
            If preset Is Nothing Then Return Nothing
            Dim text = If(preset.Text, "").Trim()
            Dim imagePath = If(preset.ImagePath, "").Trim()
            If String.IsNullOrWhiteSpace(text) AndAlso String.IsNullOrWhiteSpace(imagePath) Then Return Nothing

            ' LockAspect steuert nicht nur das Ziehen, sondern auch das ZEICHNEN: ohne Sperre
            ' wird das Bild auf die Box gestreckt statt uniform eingepasst.
            Return New ImageAnnotation With {
                .Kind = "Watermark",
                .Text = If(String.IsNullOrWhiteSpace(text), "FerrumPix", text),
                .ImagePath = imagePath,
                .XPixels = CSng(Math.Max(-100000, Math.Min(100000, preset.OffsetXPixels))),
                .YPixels = CSng(Math.Max(-100000, Math.Min(100000, preset.OffsetYPixels))),
                .WidthPixels = CSng(Math.Max(1, Math.Min(100000, preset.WidthPixels))),
                .HeightPixels = CSng(Math.Max(1, Math.Min(100000, preset.HeightPixels))),
                .FillColor = AppSettingsService.NormalizeHexColor(preset.FillColor, "#FFFFFFFF"),
                .StrokeColor = AppSettingsService.NormalizeHexColor(preset.StrokeColor, "#FF000000"),
                .StrokeWidth = CSng(Math.Max(0, Math.Min(200, preset.StrokeWidth))),
                .FontSizePixels = CSng(Math.Max(8, Math.Min(5000, preset.FontSizePixels))),
                .FontFamily = If(String.IsNullOrWhiteSpace(preset.FontFamily), "Arial", preset.FontFamily),
                .Opacity = CSng(Math.Max(0, Math.Min(100, preset.Opacity))),
                .BlendMode = If(String.IsNullOrWhiteSpace(preset.BlendMode), "Normal", preset.BlendMode),
                .BlendIncludesStroke = preset.BlendIncludesStroke,
                .RotationDegrees = CSng(Math.Max(-180, Math.Min(180, preset.RotationDegrees))),
                .FlipHorizontal = preset.FlipHorizontal,
                .FlipVertical = preset.FlipVertical,
                .Anchor = AppSettingsService.NormalizeAnnotationAnchorName(preset.Anchor),
                .LockAspect = preset.LockAspect,
                .IsVisible = True,
                .FillKind = preset.FillKind,
                .FillColor2 = AppSettingsService.NormalizeHexColor(preset.FillColor2, "#FFFFFFFF"),
                .GradientAngleDegrees = CSng(preset.GradientAngleDegrees),
                .GradientInverted = preset.GradientInverted,
                .LetterSpacingPercent = CSng(preset.LetterSpacingPercent),
                .Bold = preset.Bold,
                .Italic = preset.Italic,
                .ShadowEnabled = preset.ShadowEnabled,
                .ShadowOffsetXPercent = CSng(preset.ShadowOffsetXPercent),
                .ShadowOffsetYPercent = CSng(preset.ShadowOffsetYPercent),
                .ShadowBlur = CSng(preset.ShadowBlur),
                .ShadowStrength = CSng(preset.ShadowStrength),
                .ShadowColor = AppSettingsService.NormalizeHexColor(preset.ShadowColor, "#80000000"),
                .ShadowRounded = preset.ShadowRounded,
                .ShadowCornerRadiusPercent = CSng(preset.ShadowCornerRadiusPercent),
                .ShadowSizePercent = CSng(preset.ShadowSizePercent),
                .GlowEnabled = preset.GlowEnabled,
                .GlowBlur = CSng(preset.GlowBlur),
                .GlowStrength = CSng(preset.GlowStrength),
                .GlowColor = AppSettingsService.NormalizeHexColor(preset.GlowColor, "#FFFFFF00")
            }
        End Function

        Private Function GetSelectedBatchEditableImageItems() As List(Of ImageItem)
            Return GetSelectedImageItems().
                Where(Function(i) i IsNot Nothing AndAlso i.CanEditFile).
                Where(Function(i) IsBatchImageEditReadable(i.FilePath)).
                ToList()
        End Function

        ' ── Stapel: Abbruch und Fortschritt ─────────────────────────────────────
        '
        ' EIN Lauf, EINE Marke. Die Schreibschleifen eines Stapels laufen nacheinander (erst die
        ' lokalen Dateien, dann die Serverbilder), und das X soll den GANZEN Lauf anhalten, nicht
        ' nur den Abschnitt, der gerade dran ist. Deshalb liegt die Marke hier und nicht in den
        ' Schleifen.
        '
        ' Warum es das braucht: ein Bild vierfach zu vergroessern kostet auf dem Prozessor
        ' achteinhalb Minuten, und bis hierher stand dazu nur "x von y Dateien" - von einem Haenger
        ' nicht zu unterscheiden, und ohne Rueckweg, wenn man sich in der Auswahl vertan hat.
        Private _batchCancellation As Threading.CancellationToken
        Private _batchFilesDone As Integer
        Private _batchFilesTotal As Integer
        Private _batchTilesDone As Integer
        Private _batchTilesTotal As Integer

        ''' <summary>Beginn eines Stapellaufs. Zurueck kommt die Marke; sie geht in die
        ''' Schreibschleifen und von dort ueber <c>SaveImage</c> bis in die Modelldienste.</summary>
        Private Function BeginBatchRun(total As Integer) As Threading.CancellationToken
            _batchFilesDone = 0
            _batchFilesTotal = Math.Max(0, total)
            _batchTilesDone = 0
            _batchTilesTotal = 0
            _batchCancellation = _mainVm.BeginBusyOverlayCancellation()
            ' Die beiden Modelldienste melden je Kachel - im Stapel hoerte dem bisher niemand zu.
            UpscaleModelService.Progress = AddressOf ReportBatchTiles
            DenoiseModelService.Progress = AddressOf ReportBatchTiles
            Return _batchCancellation
        End Function

        ''' <summary>Ende, egal ob fertig, abgebrochen oder fehlgeschlagen. Darf mehrfach und auch
        ''' ohne vorherigen Beginn laufen - die Stapelwege haben mehrere Ausgaenge.</summary>
        Private Sub EndBatchRun()
            UpscaleModelService.Progress = Nothing
            DenoiseModelService.Progress = Nothing
            _mainVm.EndBusyOverlayCancellation()
            _batchCancellation = Nothing
            _batchTilesTotal = 0
        End Sub

        ''' <summary>Hat der Nutzer den laufenden Stapel angehalten?</summary>
        Private Function BatchWasCancelled() As Boolean
            Return _batchCancellation.IsCancellationRequested
        End Function

        ''' <summary>Kachelmeldung eines Modelldienstes. Kommt aus dem HINTERGRUND, die Anzeige
        ''' gehoert aber dem UI-Faden.</summary>
        Private Sub ReportBatchTiles(done As Integer, total As Integer)
            _batchTilesDone = done
            _batchTilesTotal = total
            PostBatchProgress()
        End Sub

        ''' <summary>Ein Bild ist durch. Die Kachelzahl faellt damit weg - das naechste Bild faengt
        ''' wieder bei null an, und eine stehengebliebene Zahl waere eine Falschaussage.</summary>
        Private Sub ReportBatchFileDone()
            _batchFilesDone += 1
            _batchTilesTotal = 0
            PostBatchProgress()
        End Sub

        Private Sub PostBatchProgress()
            Dispatcher.UIThread.Post(
                Sub()
                    ' Nach dem Druck aufs X steht dort "Wird abgebrochen…" - diese Auskunft ist die
                    ' wichtigere und darf nicht von der naechsten Kachel ueberschrieben werden.
                    If BatchWasCancelled() Then Return
                    _mainVm.UpdateBusyOverlay(BatchProgressText())
                End Sub)
        End Sub

        ''' <summary>Der Text in der Warteanzeige. Gezaehlt wird das Bild, an dem gerade gerechnet
        ''' wird, nicht das letzte fertige - sonst stuende beim ersten Bild minutenlang eine
        ''' Null.</summary>
        Private Function BatchProgressText() As String
            Dim current = Math.Min(_batchFilesDone + 1, Math.Max(1, _batchFilesTotal))
            If _batchTilesTotal > 0 Then
                Return String.Format(LocalizationService.T("Bilder werden geschrieben: {0} von {1}, Kachel {2} von {3}"),
                                     current, _batchFilesTotal, _batchTilesDone, _batchTilesTotal)
            End If
            Return String.Format(LocalizationService.T("Bilder werden geschrieben: {0} von {1}"),
                                 current, _batchFilesTotal)
        End Function

        ''' <summary>Die Schlussmeldung eines Stapels. Nach einem Abbruch sagt sie das auch: eine
        ''' blosse Zahl liest sich dort wie ein Fehlschlag.</summary>
        Private Function BatchResultText(template As String, done As Integer, total As Integer) As String
            If BatchWasCancelled() Then
                Return String.Format(LocalizationService.T("Stapel abgebrochen: {0} von {1} Datei(en) geschrieben"), done, total)
            End If
            Return String.Format(template, done, total)
        End Function

        Private Async Function RewriteImagesInPlaceAsync(targets As List(Of String), writer As Func(Of String, String, Boolean)) As Task(Of Integer)
            Dim changedCount = 0
            Dim errorMessage As String = Nothing
            Try
                _mainVm.BeginBusyOverlay(BatchProgressText())
                Await Task.Run(Sub()
                    For Each source In targets
                        ' Abbruch VOR dem naechsten Bild: das laufende steigt selbst an der
                        ' naechsten Kachelgrenze aus (siehe SaveImage).
                        If BatchWasCancelled() Then Exit For
                        Dim ext = IO.Path.GetExtension(source)
                        Dim temp = IO.Path.Combine(IO.Path.GetDirectoryName(source), $".{IO.Path.GetFileNameWithoutExtension(source)}.ferrumpix-{Guid.NewGuid():N}{ext}")
                        Try
                            If writer(source, temp) Then
                                File.Copy(temp, source, True)
                                changedCount += 1
                                ExifService.Invalidate(source)
                            End If
                        Finally
                            Try
                                If File.Exists(temp) Then File.Delete(temp)
                            Catch
                            End Try
                        End Try
                        ReportBatchFileDone()
                    Next
                End Sub)
            Catch ex As Exception
                errorMessage = ex.Message
            Finally
                _mainVm.EndBusyOverlay()
            End Try
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Bildverarbeitung fehlgeschlagen"), errorMessage)
            Return changedCount
        End Function

        ''' <summary>Die Quelldatei eines Elements für den Stapel: lokal der eigene Pfad, bei einem
        ''' Serverelement eine Temp-Kopie des Originals. Welcher Server, entscheidet das Element
        ''' selbst - hier steht bewusst kein Zweig je Quelle mehr.</summary>
        Private Async Function EnsureLocalPathForBatchAsync(item As ImageItem) As Task(Of String)
            If item Is Nothing Then Return Nothing
            Return Await item.EnsureLocalOriginalAsync()
        End Function

        Private Function CurrentImmichAlbumIdForUpload() As String
            If SelectedImmichNode IsNot Nothing AndAlso String.Equals(SelectedImmichNode.Kind, "ImmichAlbum", StringComparison.Ordinal) Then
                Return SelectedImmichNode.Id
            End If
            Return Nothing
        End Function

        Private Shared Function CreateImmichBatchOutputPath(sourcePath As String, requestedExtension As String,
                                                            Optional nameSuffix As String = "",
                                                            Optional nameBuilder As Func(Of String, String) = Nothing) As String
            Dim ext = If(String.IsNullOrWhiteSpace(requestedExtension), IO.Path.GetExtension(sourcePath), requestedExtension)
            If String.IsNullOrWhiteSpace(ext) Then ext = ".jpg"
            If Not ext.StartsWith(".", StringComparison.Ordinal) Then ext = "." & ext

            ' MIT Namensmuster: der gewuenschte Name muss EXAKT so hochgeladen werden - Immich
            ' uebernimmt den Dateinamen als Assetnamen. Die Eindeutigkeit der Temp-Datei kommt
            ' dann aus einem eigenen Unterordner statt aus einem Anhang im Namen (wie beim
            ' Ersetzen, siehe CreateImmichReplaceOutputPath). Ohne Muster bleibt alles wie bisher.
            Dim gebauterName As String = Nothing
            If nameBuilder IsNot Nothing Then gebauterName = nameBuilder(sourcePath)
            If Not String.IsNullOrWhiteSpace(gebauterName) Then
                Dim ownFolder = IO.Path.Combine(IO.Path.GetTempPath(), "FerrumPix", "ImmichBatch", Guid.NewGuid().ToString("N"))
                Directory.CreateDirectory(ownFolder)
                Return IO.Path.Combine(ownFolder, gebauterName & ext)
            End If

            Dim dir = IO.Path.Combine(IO.Path.GetTempPath(), "FerrumPix", "ImmichBatch")
            Directory.CreateDirectory(dir)
            Dim stem = If(String.IsNullOrWhiteSpace(IO.Path.GetFileNameWithoutExtension(sourcePath)), "immich-export", IO.Path.GetFileNameWithoutExtension(sourcePath))
            Return IO.Path.Combine(dir, $"{stem}{If(nameSuffix, "")}-ferrumpix-{Guid.NewGuid():N}{ext}")
        End Function

        ''' <summary>Zielpfad für ein Asset, das ERSETZT wird: Immich übernimmt den Dateinamen des Uploads als
        ''' Originalnamen des Assets, also muss er der alte bleiben - weder der Guid-Name aus
        ''' CreateImmichBatchOutputPath noch ein Filtersuffix haben in einer aktualisierten Bibliothek etwas
        ''' verloren. Eindeutigkeit stellt stattdessen ein eigener Unterordner je Bild her.</summary>
        ''' <summary>Raeumt den je Datei angelegten Temp-Unterordner weg - aber nur den, nicht den
        ''' gemeinsamen "ImmichBatch"-Ordner (dort koennen parallele Laeufe noch Dateien haben).</summary>
        Private Shared Sub DeleteEmptyTempFolder(folder As String)
            If String.IsNullOrEmpty(folder) OrElse Not Directory.Exists(folder) Then Return
            If String.Equals(IO.Path.GetFileName(folder), "ImmichBatch", StringComparison.OrdinalIgnoreCase) Then Return
            If Directory.EnumerateFileSystemEntries(folder).Any() Then Return
            Try
                Directory.Delete(folder)
            Catch
            End Try
        End Sub

        Private Shared Function CreateImmichReplaceOutputPath(item As ImageItem, requestedExtension As String) As String
            Dim originalName = If(String.IsNullOrWhiteSpace(item.ImmichOriginalFileName), item.ImmichAssetId, item.ImmichOriginalFileName)
            Dim ext = If(String.IsNullOrWhiteSpace(requestedExtension), IO.Path.GetExtension(originalName), requestedExtension)
            If String.IsNullOrWhiteSpace(ext) Then ext = ".jpg"
            If Not ext.StartsWith(".", StringComparison.Ordinal) Then ext = "." & ext

            Dim dir = IO.Path.Combine(IO.Path.GetTempPath(), "FerrumPix", "ImmichBatch", Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(dir)
            Dim stem = If(String.IsNullOrWhiteSpace(IO.Path.GetFileNameWithoutExtension(originalName)), "immich-export", IO.Path.GetFileNameWithoutExtension(originalName))
            Return IO.Path.Combine(dir, stem & ext)
        End Function

        ''' <param name="nameSuffix">Wird an den Dateinamen angehängt ("foto" + "_Vintage" -> "foto_Vintage").
        ''' Leer lassen, wenn der Name unverändert bleiben soll.</param>
        ''' <summary>Baut aus dem Dialog-Muster den Namensstamm-Erzeuger fuer die Stapel-Schleifen
        ''' (Nothing bei leerem Muster - dann bleibt der Originalname). Der Zaehler laeuft ueber den
        ''' ganzen Stapel; ungueltige Zeichen aus Platzhaltern (Kameranamen) werden entfernt.</summary>
        Private Function CreateNameBuilder(namePattern As String) As Func(Of String, String)
            If String.IsNullOrWhiteSpace(namePattern) Then Return Nothing
            Dim counter = 0
            Dim invalid = IO.Path.GetInvalidFileNameChars()
            Return Function(sourcePath)
                       counter += 1
                       Dim name = _mainVm.ExpandTargetNamePattern(namePattern, sourcePath, counter)
                       Return New String(name.Where(Function(c) Array.IndexOf(invalid, c) < 0).ToArray())
                   End Function
        End Function

        ''' <param name="nameBuilder">Liefert den kompletten Namensstamm fuer eine Quelle (Muster
        ''' aus dem Dialog, siehe ExpandTargetNamePattern); Nothing = Originalname + Suffix.</param>
        ''' <param name="makeUnique">True = einer vorhandenen Datei ausweichen (foto_1.jpg). False =
        ''' den Namen so lassen, wie er sich ergibt; dann entscheidet der Konflikt-Dialog, was mit
        ''' einer vorhandenen Datei geschieht. Der Stapel geht den zweiten Weg: still auszuweichen
        ''' hiess, dass am Ende Dateien im Ordner lagen, nach deren Namen niemand gefragt hatte.</param>
        Private Shared Function CreateBatchTargetFolderPath(sourcePath As String, targetFolder As String, requestedExtension As String,
                                                            Optional nameSuffix As String = "",
                                                            Optional nameBuilder As Func(Of String, String) = Nothing,
                                                            Optional makeUnique As Boolean = True) As String
            Dim ext = If(String.IsNullOrWhiteSpace(requestedExtension), IO.Path.GetExtension(sourcePath), requestedExtension)
            If String.IsNullOrWhiteSpace(ext) Then ext = ".jpg"
            If Not ext.StartsWith(".", StringComparison.Ordinal) Then ext = "." & ext

            Dim stem As String = Nothing
            If nameBuilder IsNot Nothing Then stem = nameBuilder(sourcePath)
            If String.IsNullOrWhiteSpace(stem) Then
                stem = If(String.IsNullOrWhiteSpace(IO.Path.GetFileNameWithoutExtension(sourcePath)), "ferrumpix-export", IO.Path.GetFileNameWithoutExtension(sourcePath)) & If(nameSuffix, "")
            End If
            Dim combined = IO.Path.Combine(targetFolder, stem & ext)
            Return If(makeUnique, MakeUniqueFilePath(combined), combined)
        End Function

        ''' <summary>Die Ziele eines Stapels VOR dem Schreiben festlegen und dabei jeden Konflikt
        ''' klären.
        '''
        ''' Warum vorher und nicht mittendrin: der Schreiblauf selbst läuft im Hintergrund, und von
        ''' dort lässt sich kein Dialog öffnen. Für die Bedienung ist es ohnehin das bessere von
        ''' beidem - alle Fragen am Stück, danach läuft der Stapel durch, statt alle paar Sekunden
        ''' nach einer Antwort zu verlangen.
        '''
        ''' Zurück kommen nur die Paare, die wirklich geschrieben werden sollen: was der Nutzer
        ''' übersprungen hat, fehlt hier. „Alle überschreiben" und „Alle überspringen" gelten für
        ''' den Rest des Laufs, deshalb wird die Merkentscheidung am Anfang zurückgesetzt.</summary>
        Private Async Function ResolveBatchTargetsAsync(sources As IEnumerable(Of String),
                                                        targetFolder As String,
                                                        outputExtension As Func(Of String, String),
                                                        nameSuffix As String,
                                                        nameBuilder As Func(Of String, String)) As Task(Of List(Of KeyValuePair(Of String, String)))
            Dim pairs As New List(Of KeyValuePair(Of String, String))()
            _conflictBatchDecision = Nothing
            ' Zwei Quellen können auf denselben Namen fallen (gleicher Stamm, verschiedene Ordner).
            ' Ohne dieses Gedächtnis wäre der zweite kein Konflikt - die Datei liegt ja noch nicht -
            ' und überschriebe still das Ergebnis des ersten.
            Dim claimed As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each source In If(sources, Enumerable.Empty(Of String)())
                If String.IsNullOrEmpty(source) Then Continue For
                Dim target = CreateBatchTargetFolderPath(source, targetFolder, outputExtension(source),
                                                         nameSuffix, nameBuilder, makeUnique:=False)
                If File.Exists(target) OrElse claimed.Contains(target) Then
                    target = Await ResolveConflictTargetAsync(target, source, deleteOnOverwrite:=False)
                    If String.IsNullOrEmpty(target) Then Continue For   ' übersprungen
                End If
                claimed.Add(target)
                pairs.Add(New KeyValuePair(Of String, String)(source, target))
            Next
            Return pairs
        End Function

        ''' <param name="uploadedAssetIds">Sammelt die IDs der Assets, die danach in der Ansicht stehen sollen -
        ''' im Update-Modus sind das die ERSETZTEN (ab Immich v3 mit neuer ID), sonst die neu angelegten.</param>
        Private Async Function ProcessImmichBatchItemsAsync(items As IEnumerable(Of ImageItem),
                                                            writer As Func(Of String, String, Boolean),
                                                            outputExtension As Func(Of String, String),
                                                            Optional uploadedAssetIds As List(Of String) = Nothing,
                                                            Optional nameSuffix As String = "",
                                                            Optional nameBuilder As Func(Of String, String) = Nothing) As Task(Of Integer)
            Dim uploadedCount = 0
            Dim errorMessage As String = Nothing
            Dim albumId = CurrentImmichAlbumIdForUpload()
            ' Update-Modus: die Stapelverarbeitung ersetzt die bearbeiteten Assets, statt neben jedes
            ' Original eine bearbeitete Kopie zu legen (siehe Einstellung "Vorhandene Assets aktualisieren").
            Dim updateExisting = AppSettingsService.Load().ImmichUpdateExistingAssets

            ' Elemente einer ANDEREN Serverquelle koennen hier nicht landen - dieser Weg laedt nach
            ' Immich hoch. Sie werden uebersprungen, aber NICHT stillschweigend: ein Stapel, der
            ' die Haelfte der Auswahl wortlos auslaesst, sieht aus wie ein Fehlschlag ohne Grund.
            Dim skipped = If(items, Enumerable.Empty(Of ImageItem)()).
                          Count(Function(i) i IsNot Nothing AndAlso i.IsRemoteAsset AndAlso Not i.IsImmichAsset)

            Try
                _mainVm.BeginBusyOverlay(BatchProgressText())
                For Each item In If(items, Enumerable.Empty(Of ImageItem)())
                    If BatchWasCancelled() Then Exit For
                    If item Is Nothing OrElse Not item.IsImmichAsset Then Continue For
                    Dim source = Await EnsureLocalPathForBatchAsync(item)
                    If String.IsNullOrEmpty(source) OrElse Not File.Exists(source) Then Continue For

                    Dim outputPath = If(updateExisting,
                                        CreateImmichReplaceOutputPath(item, outputExtension(source)),
                                        CreateImmichBatchOutputPath(source, outputExtension(source), nameSuffix, nameBuilder))
                    Try
                        Dim ok = Await Task.Run(Function() writer(source, outputPath))
                        If Not ok OrElse Not File.Exists(outputPath) Then Continue For

                        Dim newAssetId As String
                        If updateExisting Then
                            newAssetId = Await ImmichService.ReplaceAssetAsync(item.ImmichAssetId, outputPath)
                        Else
                            newAssetId = Await ImmichService.UploadAssetAsync(outputPath)
                        End If
                        If String.IsNullOrEmpty(newAssetId) Then Continue For
                        uploadedAssetIds?.Add(newAssetId)
                        ' Beim Ersetzen bringt PUT /assets/copy die Albenzugehörigkeit selbst mit.
                        If Not updateExisting AndAlso Not String.IsNullOrEmpty(albumId) Then Await ImmichService.AddAssetsToAlbumAsync(albumId, {newAssetId})
                        Await ImmichService.WaitForThumbnailReadyAsync(newAssetId)
                        uploadedCount += 1
                    Finally
                        Try
                            If File.Exists(outputPath) Then File.Delete(outputPath)
                            ' Ersetzte Assets UND Uploads mit Namensmuster bekommen einen eigenen
                            ' Unterordner (Namensgleichheit), der mit weg muss.
                            DeleteEmptyTempFolder(IO.Path.GetDirectoryName(outputPath))
                        Catch
                        End Try
                    End Try
                    ReportBatchFileDone()
                Next
            Catch ex As Exception
                errorMessage = ex.Message
            Finally
                _mainVm.EndBusyOverlay()
            End Try

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Immich-Upload fehlgeschlagen"), errorMessage)
            If skipped > 0 Then
                Await _mainVm.ShowMessageAsync(
                    LocalizationService.T("Immich-Upload fehlgeschlagen"),
                    String.Format(LocalizationService.T("{0} Bild(er) liegen auf einer anderen Serverquelle und wurden ausgelassen. Dorthin zurückschreiben kann die Anwendung noch nicht; wähle als Ziel einen Ordner."), skipped))
            End If
            Return uploadedCount
        End Function

        Private Async Function ProcessImmichBatchItemsToFolderAsync(items As IEnumerable(Of ImageItem),
                                                                    targetFolder As String,
                                                                    writer As Func(Of String, String, Boolean),
                                                                    outputExtension As Func(Of String, String),
                                                                    Optional nameSuffix As String = "",
                                                                    Optional nameBuilder As Func(Of String, String) = Nothing) As Task(Of Integer)
            Dim savedCount = 0
            Dim errorMessage As String = Nothing

            Try
                ' Erst alle Quellen holen, dann die Ziele samt Konfliktfragen, dann schreiben -
                ' dieselbe Ordnung wie beim lokalen Stapel, damit die Fragen am Stück kommen.
                Dim sources As New List(Of String)()
                For Each item In If(items, Enumerable.Empty(Of ImageItem)())
                    ' Gilt fuer JEDE Serverquelle: das Ziel ist ein lokaler Ordner, die Quelle wird
                    ' dafuer geholt. Nur das Zurueckschreiben ist quellenabhaengig, und das
                    ' passiert hier nicht.
                    If item Is Nothing OrElse Not item.IsRemoteAsset Then Continue For
                    Dim source = Await EnsureLocalPathForBatchAsync(item)
                    If String.IsNullOrEmpty(source) OrElse Not File.Exists(source) Then Continue For
                    sources.Add(source)
                Next
                Dim pairs = Await ResolveBatchTargetsAsync(sources, targetFolder, outputExtension, nameSuffix, nameBuilder)
                ' Erst JETZT die Anzeige: bis hierher standen die Rueckfragen zu vorhandenen
                ' Dateien an, und eine Warteanzeige dahinter waere eine Falschaussage gewesen.
                _mainVm.BeginBusyOverlay(BatchProgressText())
                For Each pair In pairs
                    If BatchWasCancelled() Then Exit For
                    Dim sourcePath = pair.Key
                    Dim target = pair.Value
                    Dim ok = Await Task.Run(Function() writer(sourcePath, target))
                    If ok AndAlso File.Exists(target) Then savedCount += 1
                    ReportBatchFileDone()
                Next
            Catch ex As Exception
                errorMessage = ex.Message
            Finally
                _mainVm.EndBusyOverlay()
            End Try

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Immich-Export fehlgeschlagen"), errorMessage)
            Return savedCount
        End Function

        ''' <param name="skipSameExtension">Beim Konvertieren ist eine Datei, die schon im Zielformat
        ''' vorliegt, nichts zu tun. Beim Anwenden eines Filters dagegen schon - dort MUSS auch ein JPG
        ''' nach JPG geschrieben werden.</param>
        ''' <param name="metaCopy">Einzeloptionen aus dem Konvertieren-Dialog, welche Katalog-Metadaten
        ''' zur Kopie wandern; Nothing = alles (Filter/Bildgröße haben keine Einzeloptionen).</param>
        Private Async Function ProcessLocalBatchItemsToFolderAsync(items As IEnumerable(Of ImageItem),
                                                                   targetFolder As String,
                                                                   writer As Func(Of String, String, Boolean),
                                                                   outputExtension As Func(Of String, String),
                                                                   Optional nameSuffix As String = "",
                                                                   Optional skipSameExtension As Boolean = True,
                                                                   Optional metaCopy As CatalogMetaCopyOptions = Nothing,
                                                                   Optional nameBuilder As Func(Of String, String) = Nothing) As Task(Of Integer)
            Dim savedCount = 0
            Dim errorMessage As String = Nothing

            Try
                ' Erst die Kandidaten, dann die Ziele samt Konfliktfragen, DANN erst schreiben. Die
                ' Fragen müssen vor den Hintergrundfaden - von dort ginge kein Dialog auf.
                Dim candidates As New List(Of String)()
                For Each item In If(items, Enumerable.Empty(Of ImageItem)())
                    If item Is Nothing OrElse item.IsImmichAsset OrElse Not File.Exists(item.FilePath) Then Continue For
                    Dim sourceExt = IO.Path.GetExtension(item.FilePath)
                    Dim targetExt = outputExtension(item.FilePath)
                    If skipSameExtension AndAlso String.Equals(sourceExt, targetExt, StringComparison.OrdinalIgnoreCase) Then Continue For
                    candidates.Add(item.FilePath)
                Next
                Dim pairs = Await ResolveBatchTargetsAsync(candidates, targetFolder, outputExtension, nameSuffix, nameBuilder)
                If pairs.Count = 0 Then Return 0

                ' ES DAUERT, UND ZWAR SICHTBAR. Ein Stapel mit Modell-Hochskalierung rechnet je Bild
                ' Minuten; ohne Anzeige sitzt die Oberflaeche still da und ist von einem Haenger
                ' nicht zu unterscheiden (Nutzerbefund 2026-08-08). Die Anzeige zaehlt seither
                ' Bilder UND Kacheln mit und traegt das X zum Abbrechen.
                _mainVm.BeginBusyOverlay(BatchProgressText())
                Await Task.Run(Sub()
                    For Each pair In pairs
                        If BatchWasCancelled() Then Exit For
                        Dim sourcePath = pair.Key
                        Dim target = pair.Value
                        If writer(sourcePath, target) AndAlso File.Exists(target) Then
                            savedCount += 1
                            ' Katalog-Metadaten (Bewertung/Favorit/Etikett/Stichworte) wandern zur
                            ' Kopie mit - das Original behält seine.
                            LibraryService.Instance.CopyEntryMeta(sourcePath, target,
                                                                  If(metaCopy Is Nothing, True, metaCopy.CopyRating),
                                                                  If(metaCopy Is Nothing, True, metaCopy.CopyFavorite),
                                                                  If(metaCopy Is Nothing, True, metaCopy.CopyColorLabel),
                                                                  If(metaCopy Is Nothing, True, metaCopy.CopyKeywords))
                        End If
                        ReportBatchFileDone()
                    Next
                End Sub)
            Catch ex As Exception
                errorMessage = ex.Message
            Finally
                _mainVm.EndBusyOverlay()
            End Try

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Konvertierung fehlgeschlagen"), errorMessage)
            Return savedCount
        End Function

        Private Async Function ProcessLocalBatchItemsToImmichAsync(items As IEnumerable(Of ImageItem),
                                                                   writer As Func(Of String, String, Boolean),
                                                                   outputExtension As Func(Of String, String),
                                                                   Optional uploadedAssetIds As List(Of String) = Nothing,
                                                                   Optional nameSuffix As String = "",
                                                                   Optional skipSameExtension As Boolean = True,
                                                                   Optional nameBuilder As Func(Of String, String) = Nothing) As Task(Of Integer)
            Dim uploadedCount = 0
            Dim errorMessage As String = Nothing
            Dim albumId = CurrentImmichAlbumIdForUpload()

            Try
                _mainVm.BeginBusyOverlay(BatchProgressText())
                For Each item In If(items, Enumerable.Empty(Of ImageItem)())
                    If BatchWasCancelled() Then Exit For
                    If item Is Nothing OrElse item.IsImmichAsset OrElse Not File.Exists(item.FilePath) Then Continue For
                    Dim sourceExt = IO.Path.GetExtension(item.FilePath)
                    Dim targetExt = outputExtension(item.FilePath)
                    If skipSameExtension AndAlso String.Equals(sourceExt, targetExt, StringComparison.OrdinalIgnoreCase) Then Continue For

                    Dim outputPath = CreateImmichBatchOutputPath(item.FilePath, targetExt, nameSuffix, nameBuilder)
                    Try
                        Dim ok = Await Task.Run(Function() writer(item.FilePath, outputPath))
                        If Not ok OrElse Not File.Exists(outputPath) Then Continue For

                        Dim newAssetId = Await ImmichService.UploadAssetAsync(outputPath)
                        If String.IsNullOrEmpty(newAssetId) Then Continue For
                        uploadedAssetIds?.Add(newAssetId)
                        If Not String.IsNullOrEmpty(albumId) Then Await ImmichService.AddAssetsToAlbumAsync(albumId, {newAssetId})
                        Await ImmichService.WaitForThumbnailReadyAsync(newAssetId)
                        uploadedCount += 1
                    Finally
                        Try
                            If File.Exists(outputPath) Then File.Delete(outputPath)
                            DeleteEmptyTempFolder(IO.Path.GetDirectoryName(outputPath))
                        Catch
                        End Try
                    End Try
                    ReportBatchFileDone()
                Next
            Catch ex As Exception
                errorMessage = ex.Message
            Finally
                _mainVm.EndBusyOverlay()
            End Try

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Immich-Upload fehlgeschlagen"), errorMessage)
            Return uploadedCount
        End Function

        ''' <summary>Lädt die gerade offene Immich-Ansicht neu (z.B. nachdem der Editor ein Asset ersetzt
        ''' hat - die Kachel zeigt sonst das Bild von vorher oder ein Asset, das es nicht mehr gibt).</summary>
        Public Async Function RefreshImmichViewAsync() As Task
            If Not _isVirtualFolder OrElse SelectedImmichNode Is Nothing Then Return
            Await RefreshAfterImmichBatchUploadAsync()
        End Function

        ''' <summary>Die offene Nextcloud-Ansicht neu laden - nach einem Upload oder nachdem ein
        ''' Original auf dem Server ersetzt wurde. Sonst zeigte die Kachel weiter das alte
        ''' Vorschaubild; dass sie es tut, entscheidet der Etag im Namen des Zwischenspeichers, und
        ''' der aendert sich mit dem neuen Inhalt ohnehin.</summary>
        Public Async Function RefreshNextcloudViewAsync() As Task
            If Not _isVirtualFolder OrElse SelectedNextcloudNode Is Nothing Then Return
            Await OpenVirtualNavigationNode(SelectedNextcloudNode)
        End Function

        Private Async Function RefreshAfterImmichBatchUploadAsync(Optional uploadedAssetIds As IEnumerable(Of String) = Nothing) As Task
            RefreshImmichAlbumsAsync()
            If SelectedImmichNode Is Nothing Then Return

            Dim reopen = SelectedImmichNode
            If String.Equals(reopen.Kind, "ImmichAll", StringComparison.Ordinal) Then
                Await OpenImmichAllAsync(reopen)
            ElseIf String.Equals(reopen.Kind, "ImmichAlbum", StringComparison.Ordinal) Then
                Await OpenImmichAlbumAsync(reopen)
            End If
            Await EnsureUploadedImmichAssetsVisibleAsync(uploadedAssetIds)
        End Function

        Private Async Function EnsureUploadedImmichAssetsVisibleAsync(assetIds As IEnumerable(Of String)) As Task
            Dim ids = If(assetIds, Enumerable.Empty(Of String)()).
                Where(Function(id) Not String.IsNullOrWhiteSpace(id)).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
            If ids.Count = 0 Then Return

            Dim missingItems As New List(Of ImageItem)()
            For Each id In ids
                Dim pseudoPrefix = "immich://" & id & "/"
                If _virtualPathSet.Any(Function(p) p.StartsWith(pseudoPrefix, StringComparison.OrdinalIgnoreCase)) Then Continue For
                Dim detail = Await ImmichService.GetAssetDetailAsync(id)
                If detail Is Nothing Then Continue For
                missingItems.Add(ImageItem.CreateImmichItem(detail))
            Next

            If missingItems.Count > 0 Then AddPrebuiltItemsToVirtualFolder(missingItems)
        End Function

        ''' <summary>Laedt das Vorschaubild EINER Datei neu - fuer Aenderungen, die die Datei selbst
        ''' nicht anfassen und deshalb auch keinen SyncFolderItems-Durchlauf brauchen (Drehung einer
        ''' RAW/PSD: die landet im Sidecar, siehe RawSidecarService).</summary>
        Public Sub RefreshThumbnailFor(path As String)
            If String.IsNullOrWhiteSpace(path) Then Return
            For Each item In Items.Where(Function(i) i IsNot Nothing AndAlso PathIdentity.AreSame(i.FilePath, path))
                item.ReloadThumbnail()
            Next
        End Sub

        Private Sub RefreshAfterBatchFileRewrite(paths As IEnumerable(Of String))
            For Each item In Items.Where(Function(i) i IsNot Nothing AndAlso paths.Contains(i.FilePath, StringComparer.OrdinalIgnoreCase))
                item.EvictThumbnail()
            Next
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then SyncFolderItems()
        End Sub

        ''' <summary>„Exportieren nach": der Sammel-Export der Galerie. Wie „Konvertieren nach",
        ''' aber mit Namensmuster, Look/Auto-Verbesserung, Wasserzeichen, Bildgröße und
        ''' Metadaten-Wahl in einem Durchgang. Funktioniert auch auf RAW/PSD-Quellen (der Decode
        ''' läuft über DecodeOriented); Originale werden NIE überschrieben - Ziel ist immer eine
        ''' neue Datei, vorhandene Namen bekommen automatisch einen Zähler.</summary>
        ''' Async Sub: eine durchgereichte Ausnahme landet beim Dispatcher und beendet den Prozess
        ''' (siehe ResizeSelected/RemoveMetadataSelected) - deshalb der aeussere Schutz.
        Private Async Sub ExportSelected(Optional vorgabe As IList(Of ImageItem) = Nothing)
            ' Await darf in VB nicht im Catch stehen - die Meldung deshalb danach.
            Dim fehler As String = Nothing
            Try
                Await ExportSelectedAsync(vorgabe)
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Gallery.ExportTo", "failed: " & ex.ToString())
                fehler = ex.Message
            End Try
            If fehler IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Exportieren nach"), fehler)
        End Sub

        ''' <param name="vorgabe">Siehe BatchConvertSelected.</param>
        ''' <summary>Mantel um den Ablauf, siehe <see cref="ResizeImageItemsAsync"/>.</summary>
        Private Async Function ExportSelectedAsync(Optional vorgabe As IList(Of ImageItem) = Nothing) As Task
            Try
                Await ExportSelectedCoreAsync(vorgabe)
            Finally
                EndBatchRun()
            End Try
        End Function

        Private Async Function ExportSelectedCoreAsync(vorgabe As IList(Of ImageItem)) As Task
            Dim targetItems = If(vorgabe, GetSelectedImageItems()).
                Where(Function(i) i IsNot Nothing AndAlso Not i.IsFolder).
                Where(Function(i) Not BatchConvertExcludedExtensions.Contains(IO.Path.GetExtension(i.FilePath).ToLowerInvariant())).
                ToList()
            DiagnosticLogService.LogAlways("Gallery.ExportTo", $"selected={GetSelectedImageItems().Count} exportable={targetItems.Count}")
            If targetItems.Count = 0 Then Return

            Dim folderHint = BatchFolderHint(targetItems)
            Dim samplePath = targetItems.Select(Function(i) i.FilePath).
                FirstOrDefault(Function(pth) Not String.IsNullOrEmpty(pth) AndAlso File.Exists(pth))
            Await _mainVm.PreparePendingBakedOptionAsync(targetItems.Select(Function(i) i.FilePath))
            Dim result = Await _mainVm.ShowExportToAsync(targetItems.Count, folderHint, samplePath)
            If result Is Nothing Then Return
            Dim applyPendingBaked = _mainVm.DialogApplyPendingBaked

            ' Die Vorlage trägt alles Bild-UNabhängige (Look, Größe, Wasserzeichen); die
            ' automatische Bildverbesserung misst dagegen PRO BILD im Writer.
            Dim template As ImageAdjustments
            Select Case result.LookKind
                Case BatchFilterDialogResult.SourceXmpPreset
                    template = XmpPresetService.LoadLook(result.LookPath)
                Case BatchFilterDialogResult.SourceAdjustmentPreset
                    ' Kein Pfad: die Vorlage steht unter ihrem Namen in den Einstellungen.
                    template = LoadAdjustmentPresetLook(result.LookName)
                Case BatchFilterDialogResult.SourceLut
                    template = If(File.Exists(result.LookPath),
                                 New ImageAdjustments With {.LutPath = result.LookPath, .LutStrength = result.LookStrength},
                                 Nothing)
                Case BatchFilterDialogResult.SourceFilter
                    template = New ImageAdjustments With {.FilterPreset = result.LookName, .FilterStrength = result.LookStrength}
                Case BatchFilterDialogResult.SourceAuto
                    ' Die Auto-Verbesserung kommt als result.AutoEnhance herein (der Dialog setzt
                    ' dann KEIN LookKind) - dieser Zweig ist nur das Sicherheitsnetz, falls sich
                    ' die Zuordnung im Dialog je aendert.
                    template = New ImageAdjustments()
                Case Else
                    template = New ImageAdjustments()
            End Select
            If template Is Nothing Then
                Await _mainVm.ShowMessageAsync(LocalizationService.T("Exportieren nach"), LocalizationService.T("Die gewählte Vorgabe konnte nicht gelesen werden."))
                Return
            End If
            If result.ResizeScalePercent > 0 Then
                ' Prozentuale Skalierung: die Zielmasse haengen am EINZELNEN Bild, deshalb erst im
                ' Writer (unten) je Quelle ausgerechnet.
                template.LockResizeAspect = result.LockAspect
                template.NoResizeUpscale = result.NoUpscale
                template.ResizeInterpolation = result.ResizeInterpolation
            ElseIf result.ResizeWidth > 0 OrElse result.ResizeHeight > 0 Then
                template.ResizeWidth = result.ResizeWidth
                template.ResizeHeight = result.ResizeHeight
                template.LockResizeAspect = result.LockAspect
                template.NoResizeUpscale = result.NoUpscale
                template.ResizeInterpolation = result.ResizeInterpolation
                ' KASTEN-Modus wie in der Bildgroessen-Stapelaktion: EIN Wert begrenzt die laengste
                ' Kante, zwei Werte sind der Kasten, in den eingepasst wird. Ohne ihn galten die
                ' Werte exakt - ein Stapel aus Quer- und Hochformaten wurde damit gestreckt, und die
                ' "Lange Kante" haette hier gar nicht gewirkt.
                template.ResizeFitInsideBox = True
            End If
            ' Das Vergroessern mit Modell steht AUSSERHALB der beiden Zweige darueber: es gilt auch
            ' ohne Zielmasse. Es laeuft im Speicherweg vor der Reglerkette - wer also vierfach
            ' vergroessert UND eine Zielbreite eintraegt, bekommt genau die, gerechnet vom
            ' vergroesserten Bild herunter.
            template.UpscaleModel = If(result.UpscaleModel, "")
            If Not String.IsNullOrEmpty(result.WatermarkPresetName) Then
                ' Die Lauf-Kopie aus dem Dialog traegt Anker und Breite dieses Laufs; nur wenn sie
                ' fehlt (aelteres Ergebnis), wird die gespeicherte Vorlage nachgeschlagen.
                Dim preset = If(result.WatermarkPreset,
                                AppSettingsService.Load().WatermarkPresets.
                                    FirstOrDefault(Function(pr) String.Equals(pr.Name, result.WatermarkPresetName, StringComparison.OrdinalIgnoreCase)))
                ' Eine Vorgabe OHNE Text und ohne Bild ist speicherbar (nur der Name ist Pflicht) -
                ' CreateWatermarkAnnotation liefert dafuer Nothing, und ein Nothing in Annotations
                ' liess Clone je Bild mit NullReference auffliegen: kein einziges Bild kam an.
                Dim annotation = If(preset Is Nothing, Nothing, CreateWatermarkAnnotation(preset))
                If annotation Is Nothing Then
                    Await _mainVm.ShowMessageAsync(LocalizationService.T("Exportieren nach"),
                                                   LocalizationService.T("Die gewählte Wasserzeichen-Vorlage enthält weder Text noch Bild."))
                    Return
                End If
                ' „Nicht mitskalieren": die Maße der Vorlage gelten dann fuer das FERTIGE Bild -
                ' das Wasserzeichen kommt also erst nach dem Verkleinern in seiner eingestellten
                ' Groesse darauf und ist in jeder Ausgabegroesse gleich gross.
                annotation.ScaleWithImage = Not result.WatermarkKeepSize
                template.Annotations.Add(annotation)
            End If

            StatusText = LocalizationService.T("Exportiere…")
            Dim cancel = BeginBatchRun(targetItems.Count)
            Dim writer = Function(source As String, target As String)
                             ' Wie beim Filter: Rezept als Grundlage, die Export-Vorlage darueber.
                             ' Groesse und Objekte gehoeren dem Export und werden gesetzt statt
                             ' zusammengefuehrt.
                             Dim adj = BatchBaseAdjustments(source)
                             adj.MergeNonDefaultPixelAdjustmentsFrom(template)
                             adj.ResizeWidth = template.ResizeWidth
                             adj.ResizeHeight = template.ResizeHeight
                             adj.ResizeScalePercent = template.ResizeScalePercent
                             adj.ResizeFitInsideBox = template.ResizeFitInsideBox
                             adj.LockResizeAspect = template.LockResizeAspect
                             adj.NoResizeUpscale = template.NoResizeUpscale
                             adj.ResizeInterpolation = template.ResizeInterpolation
                             ' Gehoert zur GROESSE und wird deshalb gesetzt wie die Felder darueber,
                             ' nicht zusammengefuehrt. Frueher kam es allein ueber
                             ' MergeNonDefaultPixelAdjustmentsFrom an - das war ein Zufall und haette
                             ' beim Einordnen als Strukturfeld still aufgehoert zu wirken.
                             adj.UpscaleModel = template.UpscaleModel
                             If template.Annotations IsNot Nothing Then
                                 For Each item In template.Annotations
                                     If item IsNot Nothing Then adj.Annotations.Add(item.Clone())
                                 Next
                             End If
                             If result.AutoEnhance Then ImageProcessor.ApplyAutoAdjustmentsTo(adj, source)
                             If result.ResizeScalePercent > 0 Then
                                 Dim size = ImageProcessor.GetImageSize(source)
                                 If size.Width > 0 AndAlso size.Height > 0 Then
                                     adj.ResizeWidth = Math.Max(1, CInt(Math.Round(size.Width * result.ResizeScalePercent / 100.0)))
                                     adj.ResizeHeight = Math.Max(1, CInt(Math.Round(size.Height * result.ResizeScalePercent / 100.0)))
                                 End If
                             End If
                             Return ImageProcessor.SaveImage(source, target, adj, result.JpgQuality, result.PreserveMetadata,
                                                             developRaw:=BatchDevelopsRaw(source),
                                                             applyPendingBaked:=applyPendingBaked,
                                                             copyrightText:=result.Copyright,
                                                             cancel:=cancel)
                         End Function
            Dim nameBuilder = CreateNameBuilder(result.NamePattern)

            ' LOKAL heisst "hat eine Datei auf dieser Platte" - nicht "ist kein Immich". Ein
            ' Nextcloud-Element fiel vorher in diesen Topf und wurde mit seinem Pseudo-Pfad wie eine
            ' Datei behandelt.
            Dim localItems = targetItems.Where(Function(i) Not i.IsRemoteAsset).ToList()
            ' Alle Serverelemente. Der Export in einen ORDNER kann sie alle (die Quelle wird geholt);
            ' das Zurueckschreiben nach Immich ueberspringt fremde Quellen von selbst.
            Dim immichItems = targetItems.Where(Function(i) i.IsRemoteAsset).ToList()
            Dim exportedCount = 0
            Dim uploadedCount = 0
            Dim uploadedAssetIds As New List(Of String)()
            ' Ein Projektbuendel gehoert nicht nach Immich - dort liegen Bild-Assets, keine
            ' Dokumente (dieselbe Regel wie beim Speichern unter).
            Dim saveToImmich = String.Equals(result.Target, "Immich", StringComparison.OrdinalIgnoreCase) AndAlso
                               ImmichService.IsConfigured AndAlso Not result.IsFpx

            If saveToImmich Then
                exportedCount = Await ProcessLocalBatchItemsToImmichAsync(localItems, writer,
                    Function(source) result.Extension, uploadedAssetIds, skipSameExtension:=False,
                    nameBuilder:=nameBuilder).ConfigureAwait(True)
                uploadedCount = Await ProcessImmichBatchItemsAsync(immichItems, writer,
                    Function(source) result.Extension, uploadedAssetIds, nameBuilder:=nameBuilder).ConfigureAwait(True)
            Else
                Dim targetFolder = If(result.TargetFolder, "").Trim()
                If String.IsNullOrWhiteSpace(targetFolder) Then
                    Await _mainVm.ShowMessageAsync(LocalizationService.T("Exportieren nach"), LocalizationService.T("Kein Zielordner angegeben."))
                    Return
                End If
                Dim createFolderError As String = Nothing
                Try
                    Directory.CreateDirectory(targetFolder)
                Catch ex As Exception
                    createFolderError = ex.Message
                End Try
                If createFolderError IsNot Nothing Then
                    Await _mainVm.ShowMessageAsync(LocalizationService.T("Exportieren nach"), createFolderError)
                    Return
                End If

                exportedCount = Await ProcessLocalBatchItemsToFolderAsync(localItems, targetFolder, writer,
                                                                          Function(source) result.Extension,
                                                                          "", skipSameExtension:=False,
                                                                          metaCopy:=result.MetaCopy,
                                                                          nameBuilder:=nameBuilder).ConfigureAwait(True)
                uploadedCount = Await ProcessImmichBatchItemsToFolderAsync(immichItems, targetFolder, writer,
                                                                           Function(source) result.Extension,
                                                                           "", nameBuilder).ConfigureAwait(True)
            End If

            If saveToImmich AndAlso uploadedAssetIds.Count > 0 Then Await RefreshImmichViewAsync()
            ' EINE Schablone statt dreier Bruchstuecke: in anderen Sprachen steht die Zahl woanders
            ' im Satz, und aus aneinandergehaengten Teilen laesst sich das nicht bauen.
            StatusText = BatchResultText(LocalizationService.T("{0} von {1} Datei(en) exportiert"), exportedCount + uploadedCount, targetItems.Count)
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then SyncFolderItems()
        End Function

        ''' <param name="vorgabe">Wenn gesetzt, gilt DIESE Liste statt der Galerie-Auswahl. So loest
        ''' der Betrachter denselben Ablauf fuer das angezeigte Bild aus - eine Umsetzung, nicht zwei.</param>
        ''' <summary>Mantel um den Ablauf, siehe <see cref="ResizeImageItemsAsync"/>.</summary>
        Private Async Sub BatchConvertSelected(Optional vorgabe As IList(Of ImageItem) = Nothing)
            Try
                Await BatchConvertSelectedCoreAsync(vorgabe)
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.BatchConvert", ex)
            Finally
                EndBatchRun()
            End Try
        End Sub

        Private Async Function BatchConvertSelectedCoreAsync(vorgabe As IList(Of ImageItem)) As Task
            Dim targetItems = If(vorgabe, GetSelectedImageItems()).
                Where(Function(i) i IsNot Nothing AndAlso Not i.IsFolder).
                Where(Function(i) Not BatchConvertExcludedExtensions.Contains(IO.Path.GetExtension(i.FilePath).ToLowerInvariant())).
                ToList()
            DiagnosticLogService.LogAlways("Gallery.BatchConvert", $"selected={GetSelectedImageItems().Count} convertible={targetItems.Count}")
            If targetItems.Count = 0 Then Return

            Await _mainVm.PreparePendingBakedOptionAsync(targetItems.Select(Function(i) i.FilePath))
            Dim result = Await _mainVm.ShowBatchConvertAsync(targetItems.Count, MainWindowViewModel.DefaultSaveFormat(),
                                                             currentFolder:=BatchFolderHint(targetItems))
            If result Is Nothing Then Return
            ' Der Haken wird JETZT festgehalten: der Schreiblauf liest ihn im Hintergrund, und bis
            ' dahin kann der naechste Dialog ihn laengst zurueckgesetzt haben.
            Dim applyPendingBaked = _mainVm.DialogApplyPendingBaked

            StatusText = LocalizationService.T("Konvertiere…")
            Dim convertedCount = 0
            ' Der Knopf "EXIF" im Uebernehmen-Bereich des Dialogs entscheidet je Lauf; die
            ' Einstellung ist nur noch die Vorbelegung.
            Dim preserveMetadata = result.PreserveMetadata
            Dim uploadedCount = 0

            ' KONVERTIEREN RENDERT WIE EXPORTIEREN. Vorher stand hier ein blankes
            ' New ImageAdjustments() und die Vorgabe developRaw:=True, und zwar an allen vier
            ' Schreibstellen. Beides war falsch: eine vorhandene Bearbeitung (.fpxmp) blieb liegen,
            ' und der Schalter "RAWs im Stapel entwickeln" galt hier als einziger Stapelweg nicht.
            ' Wer ein bearbeitetes RAW konvertierte, bekam es unbearbeitet zurueck - und wer die
            ' Entwicklung abgeschaltet hatte, bekam trotzdem die volle Entwicklung statt des Bildes,
            ' das ihm die Uebersicht zeigt. Gemeldet ueber Reddit am 2026-08-17.
            Dim cancel = BeginBatchRun(targetItems.Count)
            Dim writer = Function(source As String, target As String)
                             Dim adj = BatchBaseAdjustments(source)
                             Return ImageProcessor.SaveImage(source, target, adj, result.JpgQuality, preserveMetadata,
                                                             developRaw:=BatchDevelopsRaw(source),
                                                             applyPendingBaked:=applyPendingBaked,
                                                             copyrightText:=result.Copyright,
                                                             cancel:=cancel)
                         End Function
            ' Fuer die Serverwege dieselbe Kette, nur mit der Bedingung davor, die es dort seit jeher
            ' gibt: liegt die Quelle schon im Zielformat vor, gibt es nichts zu konvertieren.
            Dim serverWriter = Function(source As String, target As String)
                                   If String.Equals(IO.Path.GetExtension(source), result.Extension, StringComparison.OrdinalIgnoreCase) Then Return False
                                   Return writer(source, target)
                               End Function
            ' LOKAL heisst "hat eine Datei auf dieser Platte" - nicht "ist kein Immich". Ein
            ' Nextcloud-Element fiel vorher in diesen Topf und wurde mit seinem Pseudo-Pfad wie eine
            ' Datei behandelt.
            Dim localItems = targetItems.Where(Function(i) Not i.IsRemoteAsset).ToList()
            ' Alle Serverelemente. Der Export in einen ORDNER kann sie alle (die Quelle wird geholt);
            ' das Zurueckschreiben nach Immich ueberspringt fremde Quellen von selbst.
            Dim immichItems = targetItems.Where(Function(i) i.IsRemoteAsset).ToList()
            Dim saveToImmich = String.Equals(result.Target, "Immich", StringComparison.OrdinalIgnoreCase) AndAlso ImmichService.IsConfigured
            Dim uploadedAssetIds As New List(Of String)()

            If saveToImmich Then
                convertedCount = Await ProcessLocalBatchItemsToImmichAsync(localItems,
                    writer,
                    Function(source) result.Extension,
                    uploadedAssetIds).ConfigureAwait(True)

                uploadedCount = Await ProcessImmichBatchItemsAsync(immichItems,
                    serverWriter,
                    Function(source) result.Extension,
                    uploadedAssetIds).ConfigureAwait(True)
            Else
                Dim targetFolder = If(result.TargetFolder, "").Trim()
                If String.IsNullOrWhiteSpace(targetFolder) Then
                    Await _mainVm.ShowMessageAsync(LocalizationService.T("Konvertierung fehlgeschlagen"), LocalizationService.T("Kein Zielordner angegeben."))
                    Return
                End If
                Dim createFolderError As String = Nothing
                Try
                    Directory.CreateDirectory(targetFolder)
                Catch ex As Exception
                    createFolderError = ex.Message
                End Try
                If createFolderError IsNot Nothing Then
                    Await _mainVm.ShowMessageAsync(LocalizationService.T("Konvertierung fehlgeschlagen"), createFolderError)
                    Return
                End If

                Dim nameBuilder = CreateNameBuilder(result.NamePattern)
                convertedCount = Await ProcessLocalBatchItemsToFolderAsync(localItems,
                    targetFolder,
                    writer,
                    Function(source) result.Extension,
                    metaCopy:=result.MetaCopy,
                    nameBuilder:=nameBuilder).ConfigureAwait(True)

                uploadedCount = Await ProcessImmichBatchItemsToFolderAsync(immichItems,
                    targetFolder,
                    serverWriter,
                    Function(source) result.Extension).ConfigureAwait(True)
            End If

            StatusText = BatchResultText(LocalizationService.T("{0} von {1} Datei(en) konvertiert"), convertedCount + uploadedCount, targetItems.Count)
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then SyncFolderItems()
            If saveToImmich AndAlso uploadedAssetIds.Count > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
        End Function

        Public Async Function MovePathsToFolderAsync(paths As IEnumerable(Of String), targetFolder As String) As Task
            If IsVirtualFolderPath(targetFolder) Then Return
            If paths Is Nothing OrElse String.IsNullOrEmpty(targetFolder) OrElse Not Directory.Exists(targetFolder) Then Return
            If Not FileOperationPolicy.CanPasteInto(targetFolder) Then Return
            _conflictBatchDecision = Nothing
            Dim errorMessage As String = Nothing
            Dim sourcePaths As List(Of String) = Nothing
            Dim completedSources As New List(Of String)()
            Try
                sourcePaths = paths.
                    Where(Function(p) Not String.IsNullOrEmpty(p)).
                    Distinct(PathIdentity.Comparer).
                    Where(Function(p) Not String.Equals(NormalizePath(p), NormalizePath(targetFolder), PathIdentity.Comparison)).
                    Where(Function(p) FileOperationPolicy.CanMove(p, targetFolder)).
                    ToList()
                If sourcePaths.Count = 0 Then Return

                For Each source In sourcePaths
                    If Await CopyOrMovePathAsync(source, targetFolder, True) Then
                        completedSources.Add(source)
                    End If
                Next
                ClearSelection()
                If _isVirtualFolder Then
                    RemovePathsFromVirtualFolder(completedSources)
                    FilterAndSort()
                Else
                    SyncFolderItems()
                End If
                RefreshTree()
            Catch ex As Exception
                errorMessage = ex.Message
            End Try
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync(LocalizationService.T("Verschieben fehlgeschlagen"), errorMessage)
        End Function

        Public Sub ClearSelection()
            ReplaceSelection(Enumerable.Empty(Of ImageItem)())
        End Sub

        Public Sub RefreshLocalization()
            Me.RaisePropertyChanged(NameOf(SortLabel))
            Me.RaisePropertyChanged(NameOf(SelectionText))
            Me.RaisePropertyChanged(NameOf(FooterStatusText))
            Me.RaisePropertyChanged(NameOf(CurrentFolderName))
            FilterAndSort()
        End Sub

        Private Sub SaveFileBrowserSettings()
            AppSettingsService.Update(Sub(s)
                                          s.GalleryShowFolders = _showFolders
                                          s.GalleryShowParentFolder = _showParentFolder
                                          s.GalleryRatingBadgesAlwaysVisible = _ratingBadgesAlwaysVisible
                                          s.GalleryFavoriteBadgeAlwaysVisible = _favoriteBadgeAlwaysVisible
                                          s.GalleryMetadataBadgesAlwaysVisible = _metadataBadgesAlwaysVisible
                                      End Sub)
        End Sub

        Private Sub RemovePathsFromVirtualFolder(paths As IEnumerable(Of String))
            If paths Is Nothing Then Return
            Dim moved = paths.ToHashSet(PathIdentity.Comparer)
            _allItems.RemoveAll(Function(i) i IsNot Nothing AndAlso moved.Contains(i.FilePath))
        End Sub

        ''' <summary>Nimmt Elemente aus der offenen Ansicht, ohne den Server zu fragen - für den
        ''' Betrachter, der auf dem Server bereits gelöscht hat. Gegenstück zu
        ''' <see cref="RemoveImmichItems"/>, nur über den Pfad statt über die Asset-Kennung: ein
        ''' Nextcloud-Element hat keine.</summary>
        Public Sub RemovePathsFromCurrentView(paths As IEnumerable(Of String))
            If paths Is Nothing Then Return
            RemovePathsFromVirtualFolder(paths)
            FilterAndSort()
        End Sub

        Private Shared Function IsVirtualFolderPath(path As String) As Boolean
            Return Not String.IsNullOrEmpty(path) AndAlso path.StartsWith("virtual://", StringComparison.OrdinalIgnoreCase)
        End Function

        Private Async Function CopyOrMovePathAsync(source As String, targetFolder As String, movePath As Boolean, Optional duplicate As Boolean = False) As Task(Of Boolean)
            If Not File.Exists(source) AndAlso Not Directory.Exists(source) Then Return False
            If duplicate Then
                If Not FileOperationPolicy.CanDuplicate(source, targetFolder) Then Return False
            ElseIf movePath Then
                If Not FileOperationPolicy.CanMove(source, targetFolder) Then Return False
            Else
                If Not FileOperationPolicy.CanCopy(source) OrElse Not FileOperationPolicy.CanPasteInto(targetFolder) Then Return False
            End If
            If Directory.Exists(source) AndAlso IsAncestorOrSelf(source, targetFolder) Then Throw New IOException("Ein Ordner kann nicht in sich selbst verschoben werden.")

            Dim target = Await ResolveCopyTargetAsync(source, targetFolder, movePath, duplicate)
            If String.IsNullOrEmpty(target) Then Return False

            ' Kopieren kann bei großen Dateien und Ordnern Sekunden dauern - nicht auf dem UI-Thread.
            Await Task.Run(Sub()
                               If File.Exists(source) Then
                                   If movePath Then
                                       File.Move(source, target)
                                       RawSidecarService.AccompanyMove(source, target)
                                   Else
                                       File.Copy(source, target)
                                       RawSidecarService.AccompanyCopy(source, target)
                                   End If
                               Else
                                   If movePath Then
                                       Directory.Move(source, target)
                                   Else
                                       CopyDirectory(source, target)
                                   End If
                               End If
                           End Sub)
            Return True
        End Function

        Private Async Function ResolveCopyTargetAsync(source As String, targetFolder As String, movePath As Boolean, duplicate As Boolean) As Task(Of String)
            Dim name = IO.Path.GetFileName(source.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
            If String.IsNullOrWhiteSpace(name) Then Return Nothing

            Dim target = IO.Path.Combine(targetFolder, name)
            Dim normalizedSource = NormalizePath(source)
            Dim normalizedTarget = NormalizePath(target)

            If String.Equals(normalizedSource, normalizedTarget, PathIdentity.Comparison) Then
                If movePath Then Return Nothing
                Return CreateUniquePath(target)
            End If

            If duplicate Then
                Return CreateUniquePath(target)
            End If

            If Not movePath Then
                If Not File.Exists(target) AndAlso Not Directory.Exists(target) Then Return target
                Return Await ResolveConflictTargetAsync(target, source)
            End If

            If Not File.Exists(target) AndAlso Not Directory.Exists(target) Then Return target

            Return Await ResolveConflictTargetAsync(target, source)
        End Function

        ' "Alle überschreiben"/"Alle überspringen" gilt für den Rest des laufenden Stapels. Wird zu Beginn
        ' jedes konfliktbehafteten Stapels (Einfügen/Verschieben) zurückgesetzt.
        Private _conflictBatchDecision As FileConflictChoice? = Nothing

        ''' <param name="deleteOnOverwrite">Beim Einfügen und Verschieben wird die vorhandene Datei
        ''' sofort gelöscht, weil die Kopie unmittelbar folgt. Der STAPEL klärt seine Ziele dagegen
        ''' lange vor dem Schreiben - dort darf nichts vorab gelöscht werden, sonst wäre die alte
        ''' Datei weg, falls der Lauf danach scheitert. Der Schreiber überschreibt selbst.</param>
        Private Async Function ResolveConflictTargetAsync(conflictingTarget As String, source As String,
                                                          Optional deleteOnOverwrite As Boolean = True) As Task(Of String)
            If _conflictBatchDecision.HasValue Then
                If _conflictBatchDecision.Value = FileConflictChoice.OverwriteAll Then
                    If deleteOnOverwrite Then DeleteTargetForOverwrite(conflictingTarget)
                    Return conflictingTarget
                End If
                Return Nothing   ' SkipAll
            End If

            Do
                ' incomingIsPlanned: der Stapel SCHREIBT die Zieldatei erst, sie existiert noch
                ' nicht. Ohne die Angabe zeigte der Dialog die Werte der QUELLE als die der neuen
                ' Datei - beim Verkleinern also weiter die Masse des Originals.
                Dim result = Await _mainVm.ShowFileConflictAsync(conflictingTarget, source, incomingIsPlanned:=True)
                If result Is Nothing Then Return Nothing

                Select Case result.Choice
                    Case FileConflictChoice.OverwriteAll
                        _conflictBatchDecision = FileConflictChoice.OverwriteAll
                        If deleteOnOverwrite Then DeleteTargetForOverwrite(conflictingTarget)
                        Return conflictingTarget
                    Case FileConflictChoice.SkipAll
                        _conflictBatchDecision = FileConflictChoice.SkipAll
                        Return Nothing
                    Case FileConflictChoice.Overwrite
                        If deleteOnOverwrite Then DeleteTargetForOverwrite(conflictingTarget)
                        Return conflictingTarget
                    Case FileConflictChoice.Rename
                        Dim newName = If(result.NewName, "").Trim()
                        If String.IsNullOrWhiteSpace(newName) Then Return Nothing
                        If HasInvalidFileNameChars(newName) Then
                            Await _mainVm.ShowMessageAsync(LocalizationService.T("Umbenennen fehlgeschlagen"), LocalizationService.T("Der Name enthält ungültige Zeichen."))
                            Continue Do
                        End If

                        Dim targetFolder = IO.Path.GetDirectoryName(conflictingTarget)
                        If String.IsNullOrEmpty(targetFolder) Then Return Nothing
                        Dim renamedTarget = IO.Path.Combine(targetFolder, newName)
                        If File.Exists(renamedTarget) OrElse Directory.Exists(renamedTarget) Then
                            Await _mainVm.ShowMessageAsync(LocalizationService.T("Umbenennen fehlgeschlagen"), LocalizationService.T("Ein Element mit diesem Namen existiert bereits."))
                            Continue Do
                        End If

                        Return renamedTarget
                    Case Else
                        Return Nothing
                End Select
            Loop
        End Function

        Private Shared Sub DeleteTargetForOverwrite(target As String)
            If File.Exists(target) Then
                File.Delete(target)
            ElseIf Directory.Exists(target) Then
                Directory.Delete(target, True)
            End If
        End Sub

        Private Shared Function CreateUniquePath(path As String) As String
            Dim dir = IO.Path.GetDirectoryName(path)
            Dim name = IO.Path.GetFileNameWithoutExtension(path)
            Dim ext = IO.Path.GetExtension(path)
            If Directory.Exists(path) Then
                name = IO.Path.GetFileName(path)
                ext = ""
            End If
            If String.IsNullOrWhiteSpace(dir) OrElse String.IsNullOrWhiteSpace(name) Then Return path
            Dim i = 1
            Dim candidate As String
            Do
                candidate = IO.Path.Combine(dir, $"{name} Kopie{If(i = 1, "", " " & i)}{ext}")
                i += 1
            Loop While File.Exists(candidate) OrElse Directory.Exists(candidate)
            Return candidate
        End Function

        Private Shared Function HasInvalidFileNameChars(fileName As String) As Boolean
            If String.IsNullOrEmpty(fileName) Then Return True
            If fileName.IndexOf(IO.Path.DirectorySeparatorChar) >= 0 OrElse
               fileName.IndexOf(IO.Path.AltDirectorySeparatorChar) >= 0 Then Return True

            Dim invalidChars = IO.Path.GetInvalidFileNameChars()
            Return invalidChars IsNot Nothing AndAlso invalidChars.Length > 0 AndAlso fileName.IndexOfAny(invalidChars) >= 0
        End Function

        Private Shared Sub CopyDirectory(source As String, target As String)
            Directory.CreateDirectory(target)
            For Each filePath In Directory.GetFiles(source)
                IO.File.Copy(filePath, IO.Path.Combine(target, IO.Path.GetFileName(filePath)))
            Next
            For Each directoryPath In Directory.GetDirectories(source)
                CopyDirectory(directoryPath, IO.Path.Combine(target, IO.Path.GetFileName(directoryPath)))
            Next
        End Sub

        Private Shared Function IsAncestorOrSelf(parentPath As String, childPath As String) As Boolean
            Dim parent = NormalizePath(parentPath)
            Dim child = NormalizePath(childPath)
            If String.IsNullOrEmpty(parent) OrElse String.IsNullOrEmpty(child) Then Return False
            Return child.Equals(parent, PathIdentity.Comparison) OrElse
                   child.StartsWith(AppendDirectorySeparator(parent), PathIdentity.Comparison)
        End Function

        Private Shared Function NormalizePath(path As String) As String
            If String.IsNullOrEmpty(path) Then Return ""
            Dim fullPath = IO.Path.GetFullPath(path)
            Dim root = IO.Path.GetPathRoot(fullPath)
            If String.Equals(fullPath, root, PathIdentity.Comparison) Then Return fullPath
            Return fullPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
        End Function

        Private Shared Function AppendDirectorySeparator(path As String) As String
            If path.EndsWith(IO.Path.DirectorySeparatorChar) OrElse path.EndsWith(IO.Path.AltDirectorySeparatorChar) Then Return path
            Return path & IO.Path.DirectorySeparatorChar
        End Function

        Private Sub RefreshTree()
            For Each node In FolderTree
                ResetNode(node)
            Next
            RestoreCurrentFolderTreeSelection()
        End Sub

        Private Sub ResetNode(node As FolderNode)
            node.ReloadChildren()
            For Each child In node.Children
                child.ReloadChildren()
            Next
        End Sub

        Private Sub RestoreCurrentFolderTreeSelection()
            If String.IsNullOrEmpty(_currentFolder) Then Return
            SelectFolderInTreeByPath(_currentFolder)
        End Sub

        Private Sub SelectFolderInTreeByPath(folderPath As String)
            If String.IsNullOrEmpty(folderPath) Then Return
            Dim node = FindFolderNode(FolderTree, folderPath)
            If node Is Nothing Then Return
            SelectedFolderNode = node
            _initialFolderNode = node
            node.EnsureChildrenLoaded()
            node.IsExpanded = True
            Me.RaisePropertyChanged(NameOf(InitialFolderNode))
        End Sub

        Private Sub ExpandFolderInTreeByPath(folderPath As String)
            If String.IsNullOrEmpty(folderPath) Then Return
            Dim node = FindFolderNode(FolderTree, folderPath)
            If node Is Nothing Then Return
            node.EnsureChildrenLoaded()
            node.IsExpanded = True
        End Sub

        Public Sub SelectByOffset(offset As Integer)
            If Items.Count = 0 Then Return
            If SelectedItem Is Nothing Then
                Dim first = GetFirstNavigableIndex()
                If first >= 0 Then SelectOnly(Items(first))
                Return
            End If
            Dim idx = Items.IndexOf(SelectedItem)
            Dim nextIndex = FindNavigableIndex(idx, offset)
            If nextIndex >= 0 Then
                SelectOnly(Items(nextIndex))
            End If
        End Sub

        Public Function MoveCurrentByOffset(offset As Integer) As ImageItem
            If Items.Count = 0 Then Return Nothing
            If SelectedItem Is Nothing Then
                Dim first = GetFirstNavigableIndex()
                If first < 0 Then Return Nothing
                SelectedItem = Items(first)
                Return SelectedItem
            End If
            Dim idx = Items.IndexOf(SelectedItem)
            If idx < 0 Then idx = 0
            Dim nextIndex = FindNavigableIndex(idx, offset)
            If nextIndex < 0 Then Return SelectedItem
            SelectedItem = Items(nextIndex)
            Return SelectedItem
        End Function

        Public Sub ExtendSelectionByOffset(anchor As ImageItem, offset As Integer)
            Dim target = MoveCurrentByOffset(offset)
            If target IsNot Nothing Then SelectRange(anchor, target)
        End Sub

        Public Function MoveCurrentToFirst() As ImageItem
            Dim first = GetFirstNavigableIndex()
            If first < 0 Then Return Nothing
            SelectedItem = Items(first)
            Return SelectedItem
        End Function

        Public Function MoveCurrentToLast() As ImageItem
            If Items.Count = 0 Then Return Nothing
            SelectedItem = Items(Items.Count - 1)
            Return SelectedItem
        End Function

        Public Sub SelectFirst()
            Dim first = GetFirstNavigableIndex()
            If first >= 0 Then SelectOnly(Items(first))
        End Sub

        Public Sub SelectLast()
            If Items.Count > 0 Then SelectOnly(Items(Items.Count - 1))
        End Sub

        Public Sub ExtendSelectionToFirst(anchor As ImageItem)
            Dim target = MoveCurrentToFirst()
            If target IsNot Nothing Then SelectRange(anchor, target)
        End Sub

        Public Sub ExtendSelectionToLast(anchor As ImageItem)
            Dim target = MoveCurrentToLast()
            If target IsNot Nothing Then SelectRange(anchor, target)
        End Sub
    End Class

End Namespace
