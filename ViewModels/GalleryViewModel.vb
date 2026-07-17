Imports System
Imports System.Collections.Generic
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

        Private ReadOnly _mainVm As MainWindowViewModel
        Private _currentFolder As String = Nothing
        Private _selectedItem As ImageItem
        Private _thumbnailSize As Double = 260
        Private _statusText As String = LocalizationService.T("Willkommen bei FerrumPix")
        Private _hoveredMetadataTitle As String = ""
        Private _hoveredMetadataText As String = ""
        Private _searchText As String = ""
        Private _sortMode As String = "Name"
        Private _sortAscending As Boolean = True
        Private _showFolders As Boolean = True
        Private _showParentFolder As Boolean = True
        Private _viewMode As String = "Grid"
        Private _isLoading As Boolean
        Private _storageFreeText As String = ""
        Private _storageFillPercent As Double = 0
        Private _selectedFolderNode As FolderNode
        Private _selectedSearchNode As VirtualNavigationNode
        Private _selectedImmichNode As VirtualNavigationNode
        Private _clipboardPaths As New List(Of String)()
        Private _clipboardCut As Boolean
        Private ReadOnly _historyBack As New Stack(Of String)()
        Private ReadOnly _historyForward As New Stack(Of String)()
        Private _watcher As FileSystemWatcher
        Private _isVirtualFolder As Boolean
        Private _virtualFolderName As String = ""
        Private _pendingReload As Boolean = False
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
        Private _thumbnailLoadCts As New CancellationTokenSource()
        Private _activeSearchCts As CancellationTokenSource
        Private ReadOnly _virtualPathSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _savedSearches As New List(Of SearchListEntry)()
        Private ReadOnly _rawExtensions As String() = {".cr2", ".cr3", ".nef", ".arw", ".dng", ".pef", ".rw2"}

        Public Event RequestScrollToItem As EventHandler

        Public Property FolderTree As ObservableCollection(Of FolderNode)
        Public Property SearchTree As ObservableCollection(Of VirtualNavigationNode)
        ''' <summary>Eigener Immich-Bereich im Navigationsbereich (getrennt von der Suche): der
        ''' „Alle Fotos"-Knoten plus je ein Knoten pro Album.</summary>
        Public Property ImmichTree As ObservableCollection(Of VirtualNavigationNode)
        Public Property Items As BulkObservableCollection(Of ImageItem)
        Public Property DisplayItems As BulkObservableCollection(Of ImageItem)
        Public Property SelectedItems As ObservableCollection(Of ImageItem)
        Public ReadOnly Property CollageFormatOptions As ObservableCollection(Of String) = New ObservableCollection(Of String) From {"JPG", "PNG", "WEBP"}

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
            End Set
        End Property

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
                AppSettingsService.SaveGalleryThumbnailSize(value)
            End Set
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
        ' Muss mit der Grid-Zeilenhöhe "RowDefinitions=Auto,92" in GalleryView.axaml übereinstimmen
        ' (92 statt vormals 68, seit die Kachel zusätzlich eine DimensionsText-Zeile zeigt).
        Private Const GridItemLabelRowHeight As Double = 92
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
                RaiseSortStateChanged()
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
                    Case Else : modeLabel = "Name"
                End Select

                Return $"{modeLabel} {If(_sortAscending, LocalizationService.T("aufsteigend"), LocalizationService.T("absteigend"))}"
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
                AppSettingsService.SaveGalleryViewMode(value)
                _mainVm?.Settings?.SyncGalleryViewMode(value)
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
        Public ReadOnly Property DeleteSelectedCommand As ICommand
        Public ReadOnly Property SelectAllCommand As ICommand
        Public ReadOnly Property ClearSelectionCommand As ICommand
        Public ReadOnly Property OpenFileManagerCommand As ICommand
        Public ReadOnly Property CopyPathCommand As ICommand
        Public ReadOnly Property ToggleFavoriteCommand As ICommand
        Public ReadOnly Property ToggleSelectedFavoriteCommand As ICommand
        Public ReadOnly Property SetSelectedRatingCommand As ICommand
        Public ReadOnly Property RenameSelectedCommand As ICommand
        Public ReadOnly Property DuplicateSelectedCommand As ICommand
        Public ReadOnly Property ResizeSelectedCommand As ICommand
        Public ReadOnly Property ApplyWatermarkSelectedCommand As ICommand
        Public ReadOnly Property BatchConvertSelectedCommand As ICommand
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

        Private _allItems As New List(Of ImageItem)()
        Private _displayWindowFirst As Integer = -1
        Private _displayWindowLast As Integer = -1
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
            End Set
        End Property

        Public Property BottomSpacerHeight As Double
            Get
                Return _bottomSpacerHeight
            End Get
            Private Set(value As Double)
                If Math.Abs(_bottomSpacerHeight - value) < 0.1 Then Return
                Me.RaiseAndSetIfChanged(_bottomSpacerHeight, value)
            End Set
        End Property

        Public Property ContentHeight As Double
            Get
                Return _contentHeight
            End Get
            Private Set(value As Double)
                If Math.Abs(_contentHeight - value) < 0.1 Then Return
                Me.RaiseAndSetIfChanged(_contentHeight, value)
            End Set
        End Property

        Public Sub New(mainVm As MainWindowViewModel)
            _mainVm = mainVm
            Dim settings = AppSettingsService.Load()
            _thumbnailSize = settings.GalleryThumbnailSize
            _viewMode = AppSettingsService.NormalizeGalleryViewMode(settings.GalleryViewMode)
            _sortMode = settings.GallerySortMode
            _sortAscending = settings.GallerySortAscending
            _showFolders = settings.GalleryShowFolders
            _showParentFolder = settings.GalleryShowParentFolder
            _filterFavorite = settings.GalleryFilterFavorite
            _filterRatings.UnionWith(settings.GalleryFilterRatings)
            _filterFileType = settings.GalleryFilterFileType
            Items = New BulkObservableCollection(Of ImageItem)()
            DisplayItems = New BulkObservableCollection(Of ImageItem)()
            SelectedItems = New ObservableCollection(Of ImageItem)()
            FolderTree = New ObservableCollection(Of FolderNode)()
            SearchTree = New ObservableCollection(Of VirtualNavigationNode)()
            ImmichTree = New ObservableCollection(Of VirtualNavigationNode)()

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
            DeleteSelectedCommand = ReactiveCommand.Create(Sub() DeleteSelected())
            SelectAllCommand = ReactiveCommand.Create(Sub() SelectAllVisible())
            ClearSelectionCommand = ReactiveCommand.Create(Sub() ClearSelection())
            OpenFileManagerCommand = ReactiveCommand.Create(Sub() OpenInFileManager())
            CopyPathCommand = ReactiveCommand.Create(Sub() CopySelectedPath())
            ToggleFavoriteCommand = ReactiveCommand.Create(Of ImageItem)(Sub(item) DoToggleFavorite(item))
            ToggleSelectedFavoriteCommand = ReactiveCommand.Create(Sub() ToggleSelectedFavorite())
            RenameSelectedCommand = ReactiveCommand.Create(Sub() RenameSelected())
            DuplicateSelectedCommand = ReactiveCommand.CreateFromTask(Function() DuplicateSelectedAsync())
            ResizeSelectedCommand = ReactiveCommand.Create(Sub() ResizeSelected())
            ApplyWatermarkSelectedCommand = ReactiveCommand.Create(Sub() ApplyWatermarkSelected())
            BatchConvertSelectedCommand = ReactiveCommand.Create(Sub() BatchConvertSelected())
            ApplyFilterSelectedCommand = ReactiveCommand.Create(Sub() ApplyFilterSelected())
            RemoveMetadataSelectedCommand = ReactiveCommand.Create(Sub() RemoveMetadataSelected())
            IncreaseThumbnailSizeCommand = ReactiveCommand.Create(Sub() ThumbnailSize += 24)
            DecreaseThumbnailSizeCommand = ReactiveCommand.Create(Sub() ThumbnailSize -= 24)
            SetSelectedRatingCommand = ReactiveCommand.Create(Of String)(Sub(r) SetSelectedRating(r))
            SetFilterFavoriteCommand = ReactiveCommand.Create(Of String)(Sub(v) FilterFavorite = v)
            SetFilterRatingCommand = ReactiveCommand.Create(Of String)(Sub(v)
                Dim r As Integer
                If Integer.TryParse(v, r) Then ToggleFilterRating(r)
            End Sub)
            SetFilterTypeCommand = ReactiveCommand.Create(Of String)(Sub(v) FilterFileType = v)
            SetFilterColorLabelCommand = ReactiveCommand.Create(Of String)(Sub(v) ToggleFilterColorLabel(v))
            ClearFiltersCommand = ReactiveCommand.Create(Sub()
                _filterFavorite = "All"
                _filterRatings.Clear()
                _filterFileType = "All"
                _filterColorLabels.Clear()
                For Each n In {NameOf(IsFilterFavoriteAll), NameOf(IsFilterFavoriteOnly),
                                NameOf(IsFilterRatingAll), NameOf(IsFilterRatingUnrated),
                                NameOf(IsFilterRating1Plus), NameOf(IsFilterRating2Plus),
                                NameOf(IsFilterRating3Plus), NameOf(IsFilterRating4Plus),
                                NameOf(IsFilterRating5),
                                NameOf(IsFilterTypeAll), NameOf(IsFilterTypeRaw), NameOf(IsFilterTypeNonRaw),
                                NameOf(IsFilterLabelAll), NameOf(IsFilterLabelOrange), NameOf(IsFilterLabelRed),
                                NameOf(IsFilterLabelPink), NameOf(IsFilterLabelPurple), NameOf(IsFilterLabelBlue),
                                NameOf(IsFilterLabelCyan), NameOf(IsFilterLabelTeal), NameOf(IsFilterLabelGreen),
                                NameOf(HasActiveFilter), NameOf(FilterLabel)}
                    Me.RaisePropertyChanged(n)
                Next
                FilterAndSort()
                SaveGalleryFilters()
            End Sub)

            InitializeFolderTree()
            InitializeVirtualNavigation()
            InitializeImmich()
        End Sub

        Public Sub ReplaceSelection(selected As IEnumerable(Of ImageItem))
            For Each existing In SelectedItems
                existing.IsSelected = False
                existing.IsNavigationSelected = False
            Next
            SelectedItems.Clear()
            For Each item In selected.Where(Function(i) i IsNot Nothing)
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
            If item Is Nothing OrElse item.IsParentFolderEntry Then Return
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

        Private Function GetSelectedImageItems() As List(Of ImageItem)
            Dim selected = If(SelectedItems IsNot Nothing AndAlso SelectedItems.Count > 0,
                              SelectedItems.AsEnumerable(),
                              If(_selectedItem Is Nothing, Enumerable.Empty(Of ImageItem)(), {_selectedItem}))
            Return selected.Where(Function(i) i IsNot Nothing AndAlso i.IsImage).ToList()
        End Function

        ''' <summary>Persistiert eine Bewertung ans passende Backend: Immich-Items an den Server
        ''' (Rückrichtung), lokale Dateien in den SQLite-Katalog samt XMP-Sidecar.</summary>
        Private Shared Sub PersistRating(item As ImageItem, rating As Integer)
            If item Is Nothing Then Return
            If item.IsImmichAsset Then
                Dim ignored = ImmichService.SetRatingAsync(item.ImmichAssetId, rating)
            Else
                LibraryService.Instance.SetRating(item.FilePath, rating, syncToXmp:=True)
            End If
        End Sub

        ''' <summary>Persistiert den Favoriten-Status ans passende Backend (Immich-Server bzw. Katalog).</summary>
        Private Shared Sub PersistFavorite(item As ImageItem, value As Boolean)
            If item Is Nothing Then Return
            If item.IsImmichAsset Then
                Dim ignored = ImmichService.SetFavoriteAsync(item.ImmichAssetId, value)
            Else
                LibraryService.Instance.SetFavorite(item.FilePath, value)
            End If
        End Sub

        Private Sub SetSelectedRating(ratingText As String)
            Dim rating As Integer
            If Not Integer.TryParse(ratingText, rating) Then Return
            Dim images = GetSelectedImageItems()
            If images.Count = 0 Then Return

            Dim currentRating = SelectedRating
            Dim targetRating = If(currentRating = rating, 0, rating)
            For Each item In images
                item.Rating = targetRating
            Next
            ' Lokale gebündelt (ein DB-/XMP-Durchlauf), Immich-Items einzeln an den Server.
            Dim localPaths = images.Where(Function(i) Not i.IsImmichAsset).Select(Function(i) i.FilePath).ToList()
            If localPaths.Count > 0 Then LibraryService.Instance.SetRatingForMany(localPaths, targetRating, syncToXmp:=True)
            For Each im In images.Where(Function(i) i.IsImmichAsset)
                Dim ignored = ImmichService.SetRatingAsync(im.ImmichAssetId, targetRating)
            Next

            Me.RaisePropertyChanged(NameOf(SelectedRating))
            If _sortMode = "Rating" Then FilterAndSort()
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
            LibraryService.Instance.SetColorLabelForMany(targets.Select(Function(t) t.FilePath), target)
            If _filterColorLabels.Count > 0 Then FilterAndSort()
        End Sub

        Public Sub SetItemRating(item As ImageItem, rating As Integer)
            If item Is Nothing OrElse Not item.IsImage Then Return
            Dim targetRating = If(item.Rating = rating, 0, rating)
            item.Rating = targetRating
            PersistRating(item, targetRating)

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
                homeNode = New FolderNode("Persönlicher Ordner", homePath)
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
                If Directory.Exists(rootPath) Then FolderTree.Add(New FolderNode("Root", rootPath))
            End If

            Dim picPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            If Directory.Exists(picPath) Then _initialFolderNode = FindFolderNode(FolderTree, picPath)

            If _initialFolderNode Is Nothing Then
                _initialFolderNode = If(homeNode, FolderTree.FirstOrDefault())
            End If
        End Sub

        Private Sub InitializeVirtualNavigation()
            SearchTree.Clear()
            SearchTree.Add(New VirtualNavigationNode("Neue Suche", "NewSearch"))
            _savedSearches.Clear()
            _savedSearches.AddRange(SearchListService.Load())
            For Each search In _savedSearches
                SearchTree.Add(CreateSavedSearchNode(search))
            Next
        End Sub

        ''' <summary>Gibt False zurück, wenn "Neue Suche" per Dialog-Abbruch verworfen wurde - der
        ''' Aufrufer (GalleryView) nutzt das, um die sichtbare Baumauswahl in dem Fall wieder auf
        ''' den tatsächlich aktiven Ordner statt auf den "Neue Suche"-Eintrag zurückzusetzen.</summary>
        Public Async Function OpenVirtualNavigationNode(node As VirtualNavigationNode) As Task(Of Boolean)
            If node Is Nothing Then Return True
            Select Case node.Kind
                Case "NewSearch"
                    Return Await OpenSearchDialog()
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
            End Select
            Return True
        End Function

        Public ReadOnly Property HasImmich As Boolean
            Get
                Return ImmichTree IsNot Nothing AndAlso ImmichTree.Count > 0
            End Get
        End Property

        ''' <summary>Baut den eigenen Immich-Bereich auf, sofern konfiguriert: „Alle Fotos" plus die
        ''' Alben (im Hintergrund nachgeladen). No-op, wenn Immich deaktiviert/unkonfiguriert ist.</summary>
        Private Sub InitializeImmich()
            ImmichTree.Clear()
            Me.RaisePropertyChanged(NameOf(HasImmich))
            If Not ImmichService.IsConfigured Then Return
            ImmichTree.Add(New VirtualNavigationNode(LocalizationService.T("Alle Fotos"), "ImmichAll"))
            Me.RaisePropertyChanged(NameOf(HasImmich))
            RefreshImmichAlbumsAsync()
        End Sub

        ''' <summary>Baut den Immich-Bereich nach einer Konfigurationsänderung (Einstellungen) neu auf.</summary>
        Public Sub ReinitializeImmich()
            InitializeImmich()
        End Sub

        ''' <summary>Legt ein neues Immich-Album an (nach Namenseingabe) und aktualisiert den Baum.</summary>
        Public Async Sub CreateImmichAlbum()
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
        End Sub

        ''' <summary>Benennt ein Immich-Album um (nach Namenseingabe) und aktualisiert den Baum.</summary>
        Public Async Sub RenameImmichAlbum(node As VirtualNavigationNode)
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
        End Sub

        ''' <summary>Löscht ein Immich-Album - nur die Zusammenstellung, die Fotos bleiben in Immich. Hängt am
        ''' selben Schalter wie das Löschen von Fotos („Löschen in Immich erlauben"), weil auch das auf dem
        ''' Server wirkt. Steht das gelöschte Album gerade offen, fällt die Ansicht auf „Alle Fotos" zurück.</summary>
        Public Async Sub DeleteImmichAlbum(node As VirtualNavigationNode)
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
                                                       Case "ImmichAlbum", "ImmichPeopleRoot", "ImmichPlacesRoot"
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
                                               Me.RaisePropertyChanged(NameOf(HasImmich))
                                           End Sub)
                                   End Function)
        End Sub

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
                Dim rest = token.Substring("immich://".Length)
                Dim node As VirtualNavigationNode = Nothing
                If String.Equals(rest, "all", StringComparison.OrdinalIgnoreCase) Then
                    node = New VirtualNavigationNode(LocalizationService.T("Alle Fotos"), "ImmichAll")
                ElseIf rest.StartsWith("album/", StringComparison.OrdinalIgnoreCase) Then
                    Dim parts = rest.Substring(6).Split("/"c, 2)
                    node = New VirtualNavigationNode(If(parts.Length > 1, parts(1), "Album"), "ImmichAlbum") With {.Id = parts(0)}
                ElseIf rest.StartsWith("person/", StringComparison.OrdinalIgnoreCase) Then
                    Dim parts = rest.Substring(7).Split("/"c, 2)
                    node = New VirtualNavigationNode(If(parts.Length > 1, parts(1), "Person"), "ImmichPerson") With {.Id = parts(0)}
                ElseIf rest.StartsWith("place/", StringComparison.OrdinalIgnoreCase) Then
                    Dim placeName = rest.Substring(6)
                    node = New VirtualNavigationNode(placeName, "ImmichPlace") With {.Id = placeName}
                End If
                If node Is Nothing OrElse String.IsNullOrWhiteSpace(node.Id) AndAlso Not String.Equals(rest, "all", StringComparison.OrdinalIgnoreCase) Then Return
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

            ' LOKALER KATALOG (Nutzerwunsch 2026-07-16, 30k-Bibliothek): "Alle Fotos" zeigte bei
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
                        ' Bereits aus dem Katalog angezeigte Assets: nur Kernfelder nachziehen, die
                        ' die Timeline/Kacheln sofort betreffen (Favorit) - der Rest heilt sich
                        ' viewport-gekoppelt ueber updatedAt selbst.
                        If itemsByAssetId IsNot Nothing Then
                            For Each asset In result.Items
                                Dim known As ImageItem = Nothing
                                If itemsByAssetId.TryGetValue(asset.Id, known) AndAlso known.IsFavorite <> asset.IsFavorite Then
                                    known.IsFavorite = asset.IsFavorite
                                End If
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

        Public Sub RemoveVirtualSearchNode(node As VirtualNavigationNode)
            If node Is Nothing OrElse Not node.IsRemovable OrElse String.IsNullOrWhiteSpace(node.Id) Then Return
            Dim existing = _savedSearches.FirstOrDefault(Function(s) String.Equals(s.Id, node.Id, StringComparison.OrdinalIgnoreCase))
            If existing IsNot Nothing Then _savedSearches.Remove(existing)
            Dim treeNode = SearchTree.FirstOrDefault(Function(n) String.Equals(n.Id, node.Id, StringComparison.OrdinalIgnoreCase))
            If treeNode IsNot Nothing Then SearchTree.Remove(treeNode)
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
            ' Aus dem Viewer/Editor kommt bei Immich der Temp-Pfad zurück; dessen Dateiname-Stamm ist die
            ' Asset-UUID (siehe DownloadOriginalToTempAsync), womit sich das Album-Item wiederfinden lässt -
            ' unabhängig davon, ob ImmichLocalPath am Item gesetzt wurde (Filmstreifen-Navigation setzt es nicht).
            Dim immichStem = If(ImmichService.IsImmichTempPath(imagePath), IO.Path.GetFileNameWithoutExtension(imagePath), Nothing)
            Dim item = Items.FirstOrDefault(Function(i) i.IsImage AndAlso (
                String.Equals(i.FilePath, imagePath, StringComparison.OrdinalIgnoreCase) OrElse
                (i.IsImmichAsset AndAlso Not String.IsNullOrEmpty(i.ImmichLocalPath) AndAlso String.Equals(i.ImmichLocalPath, imagePath, StringComparison.OrdinalIgnoreCase)) OrElse
                (i.IsImmichAsset AndAlso immichStem IsNot Nothing AndAlso String.Equals(i.ImmichAssetId, immichStem, StringComparison.OrdinalIgnoreCase))))
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

        Private Async Function OpenSearchDialog() As Task(Of Boolean)
            Dim result = Await _mainVm.ShowSearchDialogAsync(_searchText)
            If result Is Nothing Then Return False

            Dim saved = New SearchListEntry With {
                .Id = Guid.NewGuid().ToString("N"),
                .Name = result.Name,
                .Source = If(String.Equals(result.Source, "Immich", StringComparison.OrdinalIgnoreCase), "Immich", "Local"),
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
            SearchTree.Add(treeNode)
            SaveSearches()
            OpenSavedSearch(treeNode)
            Return True
        End Function

        ''' Öffnet den Such-Overlay-Dialog vorbelegt mit den Parametern einer bereits gespeicherten
        ''' Suchliste, übernimmt die Änderungen auf denselben Eintrag (gleiche Id) und startet die
        ''' Suche neu. Aufgerufen aus dem Kontextmenü der Sidebar-Suchliste.
        Public Async Sub EditVirtualSearchNode(node As VirtualNavigationNode)
            If node Is Nothing OrElse Not String.Equals(node.Kind, "SavedSearch", StringComparison.Ordinal) Then Return
            Dim existing = _savedSearches.FirstOrDefault(Function(s) String.Equals(s.Id, node.Id, StringComparison.OrdinalIgnoreCase))
            If existing Is Nothing Then Return

            Dim result = Await _mainVm.ShowSearchDialogAsync(existing.TextQuery, existing)
            If result Is Nothing Then Return

            existing.Name = result.Name
            existing.Source = If(String.Equals(result.Source, "Immich", StringComparison.OrdinalIgnoreCase), "Immich", "Local")
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
            Dim index = SearchTree.IndexOf(node)
            If index >= 0 Then
                SearchTree(index) = newNode
            Else
                SearchTree.Add(newNode)
            End If
            OpenSavedSearch(newNode)
        End Sub

        Private Sub OpenSavedSearch(node As VirtualNavigationNode)
            If node Is Nothing Then Return
            If String.Equals(node.Source, "Immich", StringComparison.OrdinalIgnoreCase) Then
                SelectedSearchNode = node
                StartImmichSearch(node)
                Return
            End If
            If Not String.IsNullOrWhiteSpace(node.RootFolder) AndAlso Not Directory.Exists(node.RootFolder) Then
                StatusText = LocalizationService.T("Startordner nicht gefunden")
                Return
            End If
            SelectedSearchNode = node
            StartIncrementalSavedSearch(node)
        End Sub

        ''' <summary>Führt eine Immich-Suchliste aus: fragt Immichs Such-API (semantisch bei Suchtext, sonst
        ''' Metadaten) mit Favorit-/Bewertungsfilter ab und spielt die Treffer als virtuellen Ordner ein.
        ''' Strukturbedingungen (Breite/ISO/…) gelten hier nicht - Immich kennt diese Felder in der Suche nicht.</summary>
        Private Async Sub StartImmichSearch(node As VirtualNavigationNode)
            CancelActiveSearch()
            _activeSearchCts = New CancellationTokenSource()
            Dim token = _activeSearchCts.Token
            Dim thumbnailToken = StartEmptyVirtualFolder(node.Name)
            SelectedImmichNode = Nothing
            Dim favoriteOnly = String.Equals(AppSettingsService.NormalizeSearchFavoriteMode(node.FavoriteMode), "Only", StringComparison.OrdinalIgnoreCase)
            Dim ratings = NormalizeRatings(node.Ratings)
            ' Immich filtert auf genau eine Bewertung - bei mehreren nehmen wir die höchste, bei keiner keine.
            Dim rating = If(ratings.Count > 0, ratings.Max(), 0)
            Dim query = If(node.TextQuery, "").Trim()
            IsLoading = True
            StatusText = LocalizationService.T("Immich-Suche läuft…")
            Const SafetyCap As Integer = 5000
            Dim total As Integer = 0
            Try
                Dim page As Integer = 1
                Dim lastSortTick = Environment.TickCount64
                Do
                    Dim result = Await ImmichService.SearchAsync(query, favoriteOnly, rating, page, thumbnailToken)
                    If token.IsCancellationRequested OrElse thumbnailToken.IsCancellationRequested Then Return
                    If result.Items.Count > 0 Then
                        Dim isFirstBatch = (total = 0)
                        Dim items = result.Items.Select(Function(a) ImageItem.CreateImmichItem(a, thumbnailToken)).ToList()
                        AddPrebuiltItemsToVirtualFolder(items, sortNow:=False)
                        total += items.Count
                        ' Zwischensortierungen bewusst selten (das Neuaufbauen der Liste läuft auf dem
                        ' UI-Thread und konkurriert sonst mit den Viewport-Thumbnail-Benachrichtigungen).
                        If isFirstBatch OrElse Environment.TickCount64 - lastSortTick > 1500 Then
                            FilterAndSort()
                            lastSortTick = Environment.TickCount64
                        End If
                    End If
                    If result.NextPage <= 0 OrElse total >= SafetyCap Then Exit Do
                    page = result.NextPage
                Loop
                FilterAndSort()
                If total = 0 Then
                    StatusText = If(Not String.IsNullOrEmpty(ImmichService.LastError),
                                    LocalizationService.T("Immich-Fehler: ") & ImmichService.LastError,
                                    LocalizationService.T("Keine Treffer"))
                End If
            Catch ex As OperationCanceledException
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.Search", ex)
                StatusText = LocalizationService.T("Immich-Suche fehlgeschlagen")
            Finally
                If Not thumbnailToken.IsCancellationRequested Then IsLoading = False
            End Try
        End Sub

        Private Shared Function CreateSavedSearchNode(search As SearchListEntry) As VirtualNavigationNode
            Return New VirtualNavigationNode(search.Name, "SavedSearch") With {
                .Id = search.Id,
                .Source = If(String.Equals(search.Source, "Immich", StringComparison.OrdinalIgnoreCase), "Immich", "Local"),
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
            CancelActiveSearch()
            _activeSearchCts = New CancellationTokenSource()
            Dim searchCts = _activeSearchCts
            Dim token = _activeSearchCts.Token
            Dim cacheScopeId = GetSearchListCacheScopeId(node.Id)
            Dim cacheScopeName = "Suchliste: " & node.Name
            Dim thumbnailToken = StartEmptyVirtualFolder(node.Name)
            ' Saved results are loaded inside Task.Run below — no synchronous I/O on UI thread
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
            StatusText = $"Suche läuft... 0 {LocalizationService.T("Bilder")}"

            Try
                Await Task.Run(Async Function()
                                   ' Phase 0: restore previously found paths — all I/O on background thread
                                   If savedPaths.Count > 0 Then
                                       Dim seenSaved As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
                                       Dim published = 0
                                       For Each pathBatch In savedPaths.Chunk(180)
                                           token.ThrowIfCancellationRequested()
                                           Dim valid = pathBatch.
                                               Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
                                               Where(Function(p) seenSaved.Add(p)).
                                               Where(Function(p) _imageExtensions.Contains(IO.Path.GetExtension(p).ToLowerInvariant())).
                                               Where(Function(p) File.Exists(p)).
                                               ToList()
                                           If valid.Count = 0 Then Continue For

                                           Dim metaByPath = LibraryService.Instance.GetMetaForPaths(valid)
                                           Dim prebuilt = valid.
                                               Select(Function(path)
                                                          Dim m As LibraryImageMeta = Nothing
                                                          metaByPath.TryGetValue(path, m)
                                                          Dim item = ImageItem.CreateLightweight(path, thumbnailToken, cacheScopeId, cacheScopeName)
                                                          item.IsFavorite = If(m IsNot Nothing, m.IsFavorite, False)
                                                          item.Rating = If(m IsNot Nothing, m.Rating, 0)
                                                          item.ColorLabel = If(m IsNot Nothing, m.ColorLabel, "")
                                                          item.Tags = If(m IsNot Nothing AndAlso m.Tags IsNot Nothing, m.Tags, New List(Of String)())
                                                          If m IsNot Nothing Then
                                                              item.ImageWidth = If(m.ImageWidth, 0)
                                                              item.ImageHeight = If(m.ImageHeight, 0)
                                                          End If
                                                          Return item
                                                      End Function).ToList()

                                           Await Dispatcher.UIThread.InvokeAsync(Sub()
                                               If token.IsCancellationRequested Then Return
                                               AddPrebuiltItemsToVirtualFolder(prebuilt)
                                           End Sub, DispatcherPriority.Background)

                                           published += prebuilt.Count
                                           Dim localPublished = published
                                           Await Dispatcher.UIThread.InvokeAsync(Sub()
                                               If token.IsCancellationRequested Then Return
                                               StatusText = $"{localPublished:N0} gespeicherte {LocalizationService.T("Bilder")}  •  Suche läuft..."
                                           End Sub, DispatcherPriority.Background)
                                       Next
                                   End If

                                   If Not String.IsNullOrWhiteSpace(rootFolder) Then
                                       Dim pending As New List(Of String)()
                                       For Each file In EnumerateSearchFilesLazy(rootFolder, node.IncludeSubfolders, textQuery, token)
                                           token.ThrowIfCancellationRequested()
                                           scannedCount += 1
                                           pending.Add(file)
                                           If pending.Count >= 120 Then
                                               Dim added = Await PublishSearchBatchAsync(node, pending, textQuery, favoriteMode, ratingMin, selectedRatings, thumbnailToken, cacheScopeId, cacheScopeName, token)
                                               foundThisRun.AddRange(added)
                                               foundCount += added.Count
                                               pending.Clear()
                                               Dim localFound = foundCount
                                               Await Dispatcher.UIThread.InvokeAsync(Sub()
                                                   If token.IsCancellationRequested Then Return
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
                                       For Each meta In EnumerateCatalogSearchMetasLazy("", node.IncludeSubfolders, token)
                                           token.ThrowIfCancellationRequested()
                                           scannedCount += 1
                                           pending.Add(meta)
                                           If pending.Count >= 120 Then
                                               Dim added = Await PublishSearchMetaBatchAsync(node, pending, textQuery, favoriteMode, ratingMin, selectedRatings, thumbnailToken, cacheScopeId, cacheScopeName, token)
                                               foundThisRun.AddRange(added)
                                               foundCount += added.Count
                                               pending.Clear()
                                               Dim localFound = foundCount
                                               Await Dispatcher.UIThread.InvokeAsync(Sub()
                                                   If token.IsCancellationRequested Then Return
                                                   StatusText = $"Suche läuft... {localFound:N0} {LocalizationService.T("Bilder")}"
                                               End Sub)
                                           End If
                                       Next
                                       If pending.Count > 0 Then
                                           Dim added = Await PublishSearchMetaBatchAsync(node, pending, textQuery, favoriteMode, ratingMin, selectedRatings, thumbnailToken, cacheScopeId, cacheScopeName, token)
                                           foundThisRun.AddRange(added)
                                           foundCount += added.Count
                                       End If
                                   End If
                               End Function, token)

                If Not token.IsCancellationRequested Then
                    CleanupSearchListResults(node, foundThisRun)
                    StatusText = $"{foundCount:N0} {LocalizationService.T("Bilder")}  •  {CurrentFolderName}"
                End If
            Catch ex As OperationCanceledException
            Catch ex As Exception
                If Not token.IsCancellationRequested Then StatusText = LocalizationService.T("Suche fehlgeschlagen")
            Finally
                If Object.ReferenceEquals(_activeSearchCts, searchCts) Then
                    _activeSearchCts.Dispose()
                    _activeSearchCts = Nothing
                    IsLoading = False
                End If
            End Try
        End Sub

        Private Iterator Function EnumerateCatalogSearchMetasLazy(rootFolder As String, includeSubfolders As Boolean, token As CancellationToken) As IEnumerable(Of LibraryImageMeta)
            Dim root = If(rootFolder, "").Trim()
            For Each meta In LibraryService.Instance.GetAllImages()
                token.ThrowIfCancellationRequested()
                If meta Is Nothing OrElse String.IsNullOrWhiteSpace(meta.FilePath) Then Continue For
                If Not File.Exists(meta.FilePath) Then Continue For
                If Not _imageExtensions.Contains(IO.Path.GetExtension(meta.FilePath).ToLowerInvariant()) Then Continue For
                If Not IsPathInSearchRoot(meta.FilePath, root, includeSubfolders) Then Continue For
                Yield meta
            Next
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
                If meta Is Nothing Then Continue For
                If Not MatchesSavedSearchText(meta.FilePath, meta.Tags, textQuery) Then Continue For
                If favoriteMode = "Only" AndAlso Not meta.IsFavorite Then Continue For
                If favoriteMode = "Not" AndAlso meta.IsFavorite Then Continue For
                If selectedRatings IsNot Nothing AndAlso selectedRatings.Count > 0 Then
                    If Not selectedRatings.Contains(meta.Rating) Then Continue For
                Else
                    If ratingMin = 0 AndAlso meta.Rating <> 0 Then Continue For
                    If ratingMin > 0 AndAlso meta.Rating < ratingMin Then Continue For
                End If
                If Not Await EvaluateConditionsAsync(meta, node.Conditions, node.ConditionCombinator) Then Continue For
                matches.Add(meta)
            Next

            If matches.Count = 0 Then Return New List(Of String)()
            Dim matchedPaths = matches.Select(Function(m) m.FilePath).ToList()

            Await Dispatcher.UIThread.InvokeAsync(Sub()
                If searchToken.IsCancellationRequested Then Return
                AddMetasToVirtualFolder(matches, thumbnailToken, cacheScopeId, cacheScopeName)
                AppendSearchListResults(node, matchedPaths)
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
                End If

                If Not MatchesSavedSearchText(file, meta.Tags, textQuery) Then Continue For
                If favoriteMode = "Only" AndAlso Not meta.IsFavorite Then Continue For
                If favoriteMode = "Not" AndAlso meta.IsFavorite Then Continue For
                If selectedRatings IsNot Nothing AndAlso selectedRatings.Count > 0 Then
                    If Not selectedRatings.Contains(meta.Rating) Then Continue For
                Else
                    If ratingMin = 0 AndAlso meta.Rating <> 0 Then Continue For
                    If ratingMin > 0 AndAlso meta.Rating < ratingMin Then Continue For
                End If
                If Not Await EvaluateConditionsAsync(meta, node.Conditions, node.ConditionCombinator) Then Continue For
                matches.Add(meta)
            Next

            If matches.Count = 0 Then Return New List(Of String)()
            Dim matchedPaths = matches.Select(Function(m) m.FilePath).ToList()

            Await Dispatcher.UIThread.InvokeAsync(Sub()
                If searchToken.IsCancellationRequested Then Return
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
        Private Shared Function IsScannedSnapshotFresh(scannedSourceModifiedAt As String, currentModified As DateTime) As Boolean
            If String.IsNullOrWhiteSpace(scannedSourceModifiedAt) Then Return False
            Dim scanned As DateTime
            If Not DateTime.TryParse(scannedSourceModifiedAt, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.RoundtripKind, scanned) Then Return False
            Return scanned = currentModified
        End Function

        ''' <summary>Snapshot-Vergleich: Der SQLite-Katalog invalidiert sich sonst nie automatisch bei
        ''' Dateiänderungen (weder der FileSystemWatcher noch MetaHasField erkennen das) - ohne diesen
        ''' Check würden nach dem ersten erfolgreichen Lesen dauerhaft veraltete EXIF-Werte geliefert.</summary>
        Private Shared Function IsMetaStale(meta As LibraryImageMeta) As Boolean
            Try
                Return Not IsScannedSnapshotFresh(meta.ScannedSourceModifiedAt, File.GetLastWriteTime(meta.FilePath))
            Catch
                Return True
            End Try
        End Function

        Private Shared Sub ResolveMissingMetaFields(meta As LibraryImageMeta)
            Try
                Dim data = ExifService.ReadExif(meta.FilePath)
                Dim fields = ExifService.ExtractSearchFields(data, meta.FilePath)
                Dim xmpRating = ExifService.GetXmpRating(data)
                Dim catalogSummary = ExifService.BuildCatalogSummary(data, fields)
                LibraryService.Instance.SyncExifData(meta.FilePath, fields, catalogSummary)
                If xmpRating.HasValue Then
                    LibraryService.Instance.SetRating(meta.FilePath, xmpRating.Value)
                End If
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
                Case Else
                    Return True
            End Select
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

        Private Function StartEmptyVirtualFolder(name As String) As CancellationToken
            Dim thumbnailToken = BeginNewFolderThumbnailScope()
            ClearSelection()
            _allItems.Clear()
            Items.Clear()
            DisplayItems.Clear()
            _virtualPathSet.Clear()
            SetupWatcher(Nothing)

            _isVirtualFolder = True
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

        Private Sub AddMetasToVirtualFolder(metas As IEnumerable(Of LibraryImageMeta),
                                            thumbnailToken As CancellationToken,
                                            Optional cacheScopeId As String = Nothing,
                                            Optional cacheScopeName As String = Nothing)
            Dim added = False
            For Each meta In If(metas, Enumerable.Empty(Of LibraryImageMeta)())
                If meta Is Nothing OrElse String.IsNullOrWhiteSpace(meta.FilePath) Then Continue For
                If Not File.Exists(meta.FilePath) Then Continue For
                If Not _imageExtensions.Contains(IO.Path.GetExtension(meta.FilePath).ToLowerInvariant()) Then Continue For
                If Not _virtualPathSet.Add(meta.FilePath) Then Continue For

                _allItems.Add(New ImageItem(meta.FilePath, thumbnailToken, cacheScopeId, cacheScopeName) With {
                    .IsFavorite = meta.IsFavorite,
                    .Rating = meta.Rating,
                    .ColorLabel = meta.ColorLabel,
                    .Tags = If(meta.Tags, New List(Of String)()),
                    .ImageWidth = If(meta.ImageWidth, 0),
                    .ImageHeight = If(meta.ImageHeight, 0)
                })
                added = True
            Next
            If added Then FilterAndSort()
        End Sub

        ''' Adds pre-built ImageItem objects (constructed on a background thread) to the virtual
        ''' folder without any filesystem I/O — only dedup check and collection mutation.
        Private Sub AddPrebuiltItemsToVirtualFolder(items As List(Of ImageItem), Optional sortNow As Boolean = True)
            ApplyLocalColorLabelsToImmichItems(items)
            Dim added = False
            For Each item In If(items, New List(Of ImageItem)())
                If item Is Nothing Then Continue For
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
                ' Write off the UI thread so disk I/O doesn't stall the UI during search
                Dim snapshot = _savedSearches.ToList()
                Task.Run(Sub() SearchListService.Save(snapshot))
            End If
        End Sub

        Private Sub CleanupSearchListResults(node As VirtualNavigationNode, Optional currentRunResults As IEnumerable(Of String) = Nothing)
            If node Is Nothing Then Return
            Dim target = _savedSearches.FirstOrDefault(Function(s) String.Equals(s.Id, node.Id, StringComparison.OrdinalIgnoreCase))
            If target Is Nothing OrElse target.Results Is Nothing Then Return
            Dim source = If(currentRunResults, target.Results)
            Dim cleaned = source.
                Where(Function(p) Not String.IsNullOrWhiteSpace(p) AndAlso File.Exists(p)).
                Distinct(StringComparer.OrdinalIgnoreCase).
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
                If currentRunResults IsNot Nothing Then
                    Dim keep = cleaned.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    _allItems.RemoveAll(Function(i) i IsNot Nothing AndAlso i.IsImage AndAlso Not keep.Contains(i.FilePath))
                    FilterAndSort()
                End If
                SaveSearches()
            End If
        End Sub


        Private Sub CancelActiveSearch()
            If _activeSearchCts Is Nothing Then Return
            Try
                _activeSearchCts.Cancel()
            Catch
            End Try
            _activeSearchCts.Dispose()
            _activeSearchCts = Nothing
            IsLoading = False
        End Sub

        Private Sub ClearVirtualFolderState()
            CancelActiveSearch()
            If Not _isVirtualFolder Then Return
            _isVirtualFolder = False
            _virtualFolderName = ""
            _virtualPathSet.Clear()
            Me.RaisePropertyChanged(NameOf(IsVirtualFolder))
            Me.RaisePropertyChanged(NameOf(CurrentFolderName))
            Me.RaisePropertyChanged(NameOf(BreadcrumbParent))
            Me.RaisePropertyChanged(NameOf(HasBreadcrumbParent))
        End Sub

        ' ".fpx" gehört dazu: FerrumPix-Projekte erscheinen wie Bilder in Galerie und Filmstreifen
        ' (Thumbnail aus dem eingebetteten Composite, siehe ThumbnailCacheService).
        Private ReadOnly _imageExtensions As String() = {".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".heic", ".avif", ".ico", ".svg", ".fpx", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".pef", ".rw2", ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"}

        ''' <summary>
        ''' Freier Speicherplatz des Laufwerks, auf dem der aktuelle Ordner liegt. Läuft im Hintergrund:
        ''' DriveInfo.GetDrives() zählt unter Linux jeden Mountpoint auf, und ein toter NFS-Mount blockiert
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

                Return ($"{freeStr} von {totalStr} frei",
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

        Private Sub DoToggleFavorite(item As ImageItem)
            If item Is Nothing OrElse item.IsFolder Then Return
            Dim newVal = Not item.IsFavorite
            item.IsFavorite = newVal
            PersistFavorite(item, newVal)
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
                item.IsFavorite = target
                PersistFavorite(item, target)
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
            _allItems.Clear()
            Items.Clear()
            DisplayItems.Clear()
            UpdateStorageInfo()

            If String.IsNullOrEmpty(folderPath) OrElse Not Directory.Exists(folderPath) Then
                StatusText = LocalizationService.T("Kein Ordner gewählt")
                SetupWatcher(Nothing)
                Return
            End If

            SetupWatcher(folderPath)
            StatusText = LocalizationService.T("Ordner wird gelesen...")

            Dim scan As FolderScanResult
            Try
                scan = Await Task.Run(Function() ScanFolder(folderPath, thumbnailToken), thumbnailToken)
            Catch ex As OperationCanceledException
                Return
            Catch ex As UnauthorizedAccessException
                StatusText = LocalizationService.T("Zugriff verweigert")
                Return
            Catch ex As IOException
                StatusText = LocalizationService.T("Fehler beim Laden")
                Return
            Catch ex As Exception
                DiagnosticLogService.LogException("Gallery.LoadFolder", ex)
                StatusText = LocalizationService.T("Fehler beim Laden")
                Return
            End Try

            ' Ein zwischenzeitlicher Ordnerwechsel macht dieses Ergebnis wertlos.
            If thumbnailToken.IsCancellationRequested Then Return
            If Not String.Equals(NormalizePath(folderPath), NormalizePath(_currentFolder), StringComparison.OrdinalIgnoreCase) Then Return

            _allItems.AddRange(scan.Items)
            FilterAndSort()

            If scan.NeedsMetaRefresh.Count > 0 Then
                QueueBackgroundMetaRefresh(scan.NeedsMetaRefresh, thumbnailToken)
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

        ''' Das Ergebnis eines Ordner-Durchlaufs, fertig zum Einfüllen auf dem UI-Thread.
        Private Structure FolderScanResult
            Public Items As List(Of ImageItem)
            Public NeedsMetaRefresh As List(Of ImageItem)
        End Structure

        ''' Läuft im Hintergrund. Berührt nur lesend Felder des ViewModels und keine gebundene Collection.
        Private Function ScanFolder(folderPath As String, thumbnailToken As CancellationToken) As FolderScanResult
            Dim items As New List(Of ImageItem)()

            If _showFolders Then
                If _showParentFolder Then
                    Dim parentPath = IO.Path.GetDirectoryName(folderPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
                    If Not String.IsNullOrEmpty(parentPath) AndAlso Not IsAncestorOrSelf(folderPath, parentPath) AndAlso Directory.Exists(parentPath) Then
                        items.Add(ImageItem.CreateParentFolderEntry(parentPath))
                    End If
                End If

                For Each folder In Directory.GetDirectories(folderPath).
                    Where(Function(d) FolderNode.ShowHiddenFolders OrElse Not IO.Path.GetFileName(d).StartsWith(".")).
                    OrderBy(Function(d) IO.Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase)

                    items.Add(ImageItem.FromFolder(folder))
                Next
            End If

            thumbnailToken.ThrowIfCancellationRequested()

            Dim needsMetaRefresh As New List(Of ImageItem)()
            items.AddRange(BuildFileItems(EnumerateImageFiles(folderPath), folderPath, thumbnailToken, needsMetaRefresh))

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
            Dim degreeOfParallelism = Math.Max(1, Environment.ProcessorCount \ 2)

            Task.Run(Async Function()
                         Try
                             Await Task.Delay(MetaRefreshStartupDelayMs, cancellationToken)
                         Catch ex As OperationCanceledException
                             Return
                         End Try

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
                                                               LibraryService.Instance.SyncExifData(item.FilePath, fields, catalogSummary)
                                                               If xmpRating.HasValue Then
                                                                   LibraryService.Instance.SetRating(item.FilePath, xmpRating.Value)
                                                               End If
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
                                                                                                          If xmpRating.HasValue Then item.Rating = xmpRating.Value
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
                                                       Loop
                                                   End Function))
                         Next
                         Await Task.WhenAll(workers)

                         If cancellationToken.IsCancellationRequested Then Return
                         Await Dispatcher.UIThread.InvokeAsync(Sub()
                                                                    If cancellationToken.IsCancellationRequested Then Return
                                                                    FilterAndSort()
                                                                End Sub)
                     End Function)
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
            If _isVirtualFolder Then Return
            If String.IsNullOrEmpty(_currentFolder) Then Return
            If Not Directory.Exists(_currentFolder) Then
                LoadFolderImages(_currentFolder)
                Return
            End If

            Dim folderPath = _currentFolder
            ' Den laufenden Thumbnail-Scope weiterbenutzen: BeginNewFolderThumbnailScope würde die
            ' Vorschaubilder der erhalten gebliebenen Elemente verwerfen.
            Dim thumbnailToken = _thumbnailLoadCts.Token
            Dim existing = New Dictionary(Of String, ImageItem)(StringComparer.OrdinalIgnoreCase)
            For Each item In _allItems
                If item IsNot Nothing AndAlso Not item.IsParentFolderEntry AndAlso Not existing.ContainsKey(item.FilePath) Then
                    existing(item.FilePath) = item
                End If
            Next

            Dim rebuilt As New List(Of ImageItem)()
            Try
                If _showFolders Then
                    If _showParentFolder Then
                        Dim parentPath = IO.Path.GetDirectoryName(folderPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
                        If Not String.IsNullOrEmpty(parentPath) AndAlso Not IsAncestorOrSelf(folderPath, parentPath) AndAlso Directory.Exists(parentPath) Then
                            Dim keptParent = _allItems.FirstOrDefault(Function(i) i IsNot Nothing AndAlso i.IsParentFolderEntry)
                            rebuilt.Add(If(keptParent, ImageItem.CreateParentFolderEntry(parentPath)))
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

                ' Nur für neue Dateien ein ImageItem bauen.
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

                PruneSelection(New HashSet(Of ImageItem)(rebuilt))
                _allItems.Clear()
                _allItems.AddRange(rebuilt)
                FilterAndSort()
                UpdateStorageInfo()

                If itemsNeedingMetaRefresh.Count > 0 Then QueueBackgroundMetaRefresh(itemsNeedingMetaRefresh, thumbnailToken)
                If newItems.Count > 0 Then ImageItem.QueueBackgroundThumbnails(newItems)
            Catch ex As UnauthorizedAccessException
                StatusText = LocalizationService.T("Zugriff verweigert")
            Catch ex As IOException
                StatusText = LocalizationService.T("Fehler beim Laden")
            End Try
        End Sub

        Public Sub RefreshChangedFiles(paths As IEnumerable(Of String))
            Dim changed = If(paths, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
                ToHashSet(StringComparer.OrdinalIgnoreCase)
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

        ''' <summary>Erzeugt die ImageItem-Objekte für eine Dateiliste und übernimmt die im Katalog
        ''' gespeicherten Metadaten. Trägt Elemente, deren Katalogeintrag fehlt oder veraltet ist, in
        ''' <paramref name="itemsNeedingMetaRefresh"/> ein.</summary>
        Private Function BuildFileItems(files As FileInfo(),
                                        folderPath As String,
                                        thumbnailToken As CancellationToken,
                                        itemsNeedingMetaRefresh As List(Of ImageItem)) As List(Of ImageItem)
            If files Is Nothing OrElse files.Length = 0 Then Return New List(Of ImageItem)()

            Dim meta = LibraryService.Instance.GetFolderMeta(folderPath)

                ''' Die FileInfo-Objekte kommen fertig befüllt aus DirectoryInfo.EnumerateFiles (siehe
                ''' ImageItem.FromFileInfo) - der frühere Weg über New ImageItem(pfad) stieß je Datei einen
                ''' zusätzlichen Stat-Aufruf an, was sich bei Ordnern mit vielen Bildern und erst recht auf
                ''' Netzwerk-/USB-Freigaben summierte. Da jedes Element hier unabhängig von den anderen ist
                ''' und noch an keine UI-gebundene Collection angehängt wurde, läuft der Aufbau parallel;
                ''' erst der abschließende Durchlauf unten geht wieder sequenziell in fester Reihenfolge.
                Dim results = New ImageItem(files.Length - 1) {}
                Dim needsRefreshFlags = New Boolean(files.Length - 1) {}
                Parallel.For(0, files.Length,
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
                               IsScannedSnapshotFresh(m.ScannedSourceModifiedAt, item.DateModified) Then
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

            If _displayWindowFirst = firstIndex AndAlso _displayWindowLast = lastIndex Then Return

            ' Delta-Update statt vollem Reset: bei überlappenden Fenstern (normales Scrollen um
            ' wenige Zeilen, der Regelfall) werden nur die Elemente entfernt/hinzugefügt, die das
            ' Fenster tatsächlich verlassen bzw. neu betreten, statt bei jedem Scroll-Tick das
            ' komplette (durch den Keep-Alive-Puffer ohnehin schon große) Fenster per ReplaceAll
            ' zurückzusetzen - ein Reset zwingt das nicht-virtualisierende WrapPanel, ausnahmslos
            ' alle Item-Controls (inkl. der pro Kachel eager erzeugten Kontextmenüs) neu zu
            ' erzeugen und zu layouten, was sich als spürbares Ruckeln bemerkbar macht.
            Dim hasOverlap = _displayWindowFirst >= 0 AndAlso firstIndex <= _displayWindowLast AndAlso lastIndex >= _displayWindowFirst
            If hasOverlap AndAlso DisplayItems.Count = _displayWindowLast - _displayWindowFirst + 1 Then
                While _displayWindowFirst < firstIndex
                    DisplayItems.RemoveAt(0)
                    _displayWindowFirst += 1
                End While
                While _displayWindowLast > lastIndex
                    DisplayItems.RemoveAt(DisplayItems.Count - 1)
                    _displayWindowLast -= 1
                End While
                Dim insertAt = 0
                For i = firstIndex To _displayWindowFirst - 1
                    DisplayItems.Insert(insertAt, Items(i))
                    insertAt += 1
                Next
                For i = _displayWindowLast + 1 To lastIndex
                    DisplayItems.Add(Items(i))
                Next
                _displayWindowFirst = firstIndex
                _displayWindowLast = lastIndex
            Else
                _displayWindowFirst = firstIndex
                _displayWindowLast = lastIndex
                DisplayItems.ReplaceAll(Items.Skip(firstIndex).Take(lastIndex - firstIndex + 1))
            End If
        End Sub

        Private Sub ResetDisplayWindow()
            _displayWindowFirst = -1
            _displayWindowLast = -1
            TopSpacerHeight = 0
            BottomSpacerHeight = 0
            ContentHeight = 0
            If Items Is Nothing OrElse DisplayItems Is Nothing Then Return
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

        Public Async Sub OpenSelectedInViewer()
            Dim selectedMedia = Items.Where(Function(i) i IsNot Nothing AndAlso (i.IsImage OrElse i.IsVideoFile) AndAlso i.IsSelected).ToList()
            If selectedMedia.Count > 0 Then
                Dim first = selectedMedia(0)
                If first.IsImmichAsset Then
                    Await OpenImmichItemInViewerAsync(first)
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
        End Sub

        Public Async Sub OpenSelectedInEditor()
            Dim image = GetSelectedImageItems().FirstOrDefault(Function(i) i.CanEditFile)
            If image Is Nothing Then Return
            If image.IsImmichAsset Then
                Await OpenImmichItemInEditorAsync(image)
                Return
            End If
            Await _mainVm.OpenImageInEditor(image.FilePath, Items.Where(Function(i) i.IsImage AndAlso i.CanEditFile).Select(Function(i) i.FilePath).ToList(),
                                            cacheScopeId:=CurrentThumbnailCacheScopeId, cacheScopeName:=CurrentThumbnailCacheScopeName)
        End Sub

        ''' <summary>Lädt das Immich-Original in eine Temp-Kopie und öffnet es im Editor mit
        ''' Speichern-unter-Zwang - die Temp-Kopie wird nie in-place überschrieben, das Ergebnis landet
        ''' als neue Datei im Bilder-Ordner. (Rückschreiben nach Immich als Upload wäre ein späterer Schritt.)</summary>
        Private Async Function OpenImmichItemInEditorAsync(item As ImageItem) As Task
            IsLoading = True
            StatusText = LocalizationService.T("Lade Bild aus Immich…")
            Try
                Dim localPath = Await ImmichService.DownloadOriginalToTempAsync(item.ImmichAssetId, item.ImmichOriginalFileName)
                If String.IsNullOrEmpty(localPath) Then
                    StatusText = LocalizationService.T("Bild konnte nicht aus Immich geladen werden")
                    Return
                End If
                item.ImmichLocalPath = localPath
                ' Stammt das Bild aus einem geöffneten Immich-Album, den bearbeiteten Upload gleich dorthin.
                Dim sourceAlbumId = If(SelectedImmichNode IsNot Nothing AndAlso String.Equals(SelectedImmichNode.Kind, "ImmichAlbum", StringComparison.Ordinal), SelectedImmichNode.Id, Nothing)
                Await _mainVm.OpenImageInEditor(localPath, New List(Of String) From {localPath}, forceSaveAsOnly:=True, immichAlbumId:=sourceAlbumId)
                StatusText = ""
            Finally
                IsLoading = False
            End Try
        End Function

        ''' <summary>Öffnet ein Immich-Bild im Betrachter als Album-Sitzung: der Filmstreifen zeigt alle
        ''' Immich-Bilder der aktuellen Ansicht (Pseudo-Pfade), das Original wird im Viewer on-demand geladen.</summary>
        Private Function OpenImmichItemInViewerAsync(item As ImageItem) As Task
            Dim sessionItems = Items.Where(Function(i) i.IsImage AndAlso i.IsImmichAsset).ToList()
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

                ClearSelection()
                RemoveImmichItems(assetIds)
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
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
            targets = targets.Where(Function(p) FileOperationPolicy.CanDelete(p)).ToList()
            If targets.Count = 0 Then Return
            If _isVirtualFolder Then
                Dim deletedSet = targets.ToHashSet(StringComparer.OrdinalIgnoreCase)
                _mainVm.RequestDeletePaths(targets, Sub()
                                                        ClearSelection()
                                                        _allItems.RemoveAll(Function(i) i IsNot Nothing AndAlso deletedSet.Contains(i.FilePath))
                                                        FilterAndSort()
                                                    End Sub)
                Return
            End If
            Dim currentFolderWasDeleted = Not String.IsNullOrEmpty(_currentFolder) AndAlso
                                          targets.Any(Function(p) Directory.Exists(p) AndAlso String.Equals(NormalizePath(p), NormalizePath(_currentFolder), StringComparison.OrdinalIgnoreCase))
            Dim fallbackFolder = If(currentFolderWasDeleted, IO.Path.GetDirectoryName(_currentFolder), Nothing)
            ' Vor dem Löschen feststellen: danach sagt Directory.Exists nichts mehr. Nur wenn ein Ordner
            ' dabei war, muss der Baum neu aufgebaut werden.
            Dim anyFolderDeleted = targets.Any(Function(p) Directory.Exists(p))
            _mainVm.RequestDeletePaths(targets, Sub()
                                                    ClearSelection()
                                                    If currentFolderWasDeleted AndAlso Not String.IsNullOrEmpty(fallbackFolder) AndAlso Directory.Exists(fallbackFolder) Then
                                                        CurrentFolder = fallbackFolder
                                                        LoadFolderImages(fallbackFolder)
                                                        RefreshTree()
                                                        SelectFolderInTreeByPath(fallbackFolder)
                                                    Else
                                                        SyncFolderItems()
                                                        If anyFolderDeleted Then RefreshTree()
                                                    End If
                                                End Sub)
        End Sub

        Private Sub OpenInFileManager()
            If _isVirtualFolder Then
                Dim selectedPath = GetSelectedPaths().FirstOrDefault()
                If String.IsNullOrEmpty(selectedPath) Then Return
                Try
                    Dim folder = IO.Path.GetDirectoryName(selectedPath)
                    If String.IsNullOrEmpty(folder) Then Return
                    Diagnostics.Process.Start(New Diagnostics.ProcessStartInfo() With {
                        .FileName = folder,
                        .UseShellExecute = True
                    })
                Catch
                End Try
                Return
            End If
            If String.IsNullOrEmpty(_currentFolder) Then Return
            Try
                Diagnostics.Process.Start(New Diagnostics.ProcessStartInfo() With {
                    .FileName = _currentFolder,
                    .UseShellExecute = True
                })
            Catch
            End Try
        End Sub

        Private Sub CopySelectedPath()
            ' Clipboard-Zugriff erfolgt in der View
        End Sub

        Public ReadOnly Property SelectionText As String
            Get
                If SelectedItems Is Nothing OrElse SelectedItems.Count = 0 Then Return LocalizationService.T("Kein Element ausgewählt")
                If SelectedItems.Count = 1 Then Return LocalizationService.T("1 Element ausgewählt")
                Return $"{SelectedItems.Count} {LocalizationService.T("Elemente ausgewählt")}"
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
                                                      SyncFolderItems()
                                                      SelectedItem = Items.FirstOrDefault(Function(i) String.Equals(i.FilePath, newPath, StringComparison.OrdinalIgnoreCase))
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
                    If String.Equals(NormalizePath(mapping.SourcePath), NormalizePath(mapping.TargetPath), StringComparison.OrdinalIgnoreCase) Then Continue For
                    If File.Exists(mapping.TargetPath) OrElse Directory.Exists(mapping.TargetPath) Then Throw New IOException("Ein Zielname existiert bereits.")

                    If File.Exists(mapping.SourcePath) Then
                        File.Move(mapping.SourcePath, mapping.TargetPath)
                    ElseIf Directory.Exists(mapping.SourcePath) Then
                        Directory.Move(mapping.SourcePath, mapping.TargetPath)
                    End If
                Next

                ClearSelection()
                SyncFolderItems()
                RefreshTree()
                Dim renamedPaths = result.Mappings.Select(Function(m) m.TargetPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
                ReplaceSelection(Items.Where(Function(i) i IsNot Nothing AndAlso renamedPaths.Contains(i.FilePath)))
            Catch ex As Exception
                errorMessage = ex.Message
            End Try

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Stapel-Umbenennen fehlgeschlagen", errorMessage)
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

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Ordner erstellen fehlgeschlagen", errorMessage)
        End Sub

        Public Function GetSelectedPaths() As List(Of String)
            Dim selected = If(SelectedItems Is Nothing OrElse SelectedItems.Count = 0,
                              If(SelectedItem Is Nothing, Enumerable.Empty(Of ImageItem)(), {SelectedItem}),
                              SelectedItems)
            Return selected.
                Where(Function(i) i IsNot Nothing AndAlso Not i.IsParentFolderEntry).
                Select(Function(i) i.FilePath).
                Where(Function(p) Not String.IsNullOrEmpty(p)).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
        End Function

        Public Sub OpenCollageDialog()
            Dim paths = GetSelectedPaths().
                Where(Function(p) File.Exists(p) AndAlso IsImagePath(p)).
                ToList()
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
        End Sub

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
            Dim requestId = Interlocked.Increment(_collagePreviewRequestId)
            If Not IsCollageDialogOpen Then Return

            Dim paths = GetSelectedPaths().
                Where(Function(p) File.Exists(p) AndAlso IsImagePath(p)).
                ToList()
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
        End Sub

        Public Async Sub CreateCollage()
            Dim paths = GetSelectedPaths().
                Where(Function(p) File.Exists(p) AndAlso IsImagePath(p)).
                ToList()
            If paths.Count < 2 Then
                StatusText = LocalizationService.T("Für eine Collage müssen mindestens zwei Bilder ausgewählt sein.")
                Return
            End If
            If String.IsNullOrWhiteSpace(CurrentFolder) OrElse Not Directory.Exists(CurrentFolder) Then Return

            Dim baseName = IO.Path.GetFileNameWithoutExtension(If(String.IsNullOrWhiteSpace(CollageBaseName), "Collage", CollageBaseName.Trim()))
            If String.IsNullOrWhiteSpace(baseName) Then baseName = "Collage"
            Dim ext = If(String.Equals(CollageFormat, "PNG", StringComparison.OrdinalIgnoreCase), ".png",
                      If(String.Equals(CollageFormat, "WEBP", StringComparison.OrdinalIgnoreCase), ".webp", ".jpg"))
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

            IsCollageDialogOpen = False
            _collagePreviewTimer.Stop()
            CollagePreviewImage = Nothing
            StatusText = LocalizationService.T("Collage wird erstellt...")
            Dim ok = Await Task.Run(Function() CollageService.SaveCollage(paths, options))
            StatusText = If(ok, $"Collage gespeichert: {IO.Path.GetFileName(target)}", "Collage konnte nicht erstellt werden")
            If ok Then SyncFolderItems()
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

        Private Shared Function IsImagePath(path As String) As Boolean
            Dim ext = IO.Path.GetExtension(path).ToLowerInvariant()
            Return {".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".heic", ".avif", ".ico"}.Contains(ext)
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
            StatusText = If(_clipboardPaths.Count = 1, LocalizationService.T("1 Element in der Zwischenablage"), $"{_clipboardPaths.Count} {LocalizationService.T("Elemente")} {LocalizationService.T("in der Zwischenablage")}")
        End Sub

        Public Sub StoreClipboardPaths(paths As IEnumerable(Of String), cut As Boolean)
            Dim validPaths = If(paths, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrEmpty(p)).
                Where(Function(p) If(cut, FileOperationPolicy.CanRename(p), FileOperationPolicy.CanCopy(p))).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
            If validPaths.Count = 0 Then
                StatusText = LocalizationService.T("Kein Element ausgewählt")
                Return
            End If
            _clipboardPaths = validPaths
            _clipboardCut = cut
            StatusText = If(_clipboardPaths.Count = 1, LocalizationService.T("1 Element in der Zwischenablage"), $"{_clipboardPaths.Count} {LocalizationService.T("Elemente")} {LocalizationService.T("in der Zwischenablage")}")
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
            ' Immich-Items (Pseudo-Pfade) werden nicht dateikopiert, sondern als Originale in den Zielordner
            ' heruntergeladen. Die restliche (lokale) Kopierlogik ignoriert Pseudo-Pfade ohnehin (File.Exists).
            Dim immichPseudo = paths.Where(Function(p) ImmichService.IsImmichPseudoPath(p)).ToList()
            If immichPseudo.Count > 0 Then Await DownloadImmichToFolderAsync(immichPseudo, targetFolder)
            _conflictBatchDecision = Nothing
            Dim errorMessage As String = Nothing
            Dim sourcePaths As List(Of String) = Nothing
            Dim completedSources As New List(Of String)()
            Try
                sourcePaths = paths.
                    Where(Function(p) Not String.IsNullOrEmpty(p) AndAlso (File.Exists(p) OrElse Directory.Exists(p))).
                    Distinct(StringComparer.OrdinalIgnoreCase).
                    Where(Function(p) Not String.Equals(NormalizePath(p), NormalizePath(targetFolder), StringComparison.OrdinalIgnoreCase)).
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
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Einfügen fehlgeschlagen", errorMessage)
        End Function

        ''' <summary>Lädt Immich-Originale (Pseudo-Pfade) in einen lokalen Zielordner herunter - der
        ''' Immich→lokal-Zweig von Einfügen/Drag&Drop. Kollidierende Namen werden nummeriert.</summary>
        Private Async Function DownloadImmichToFolderAsync(pseudoPaths As List(Of String), targetFolder As String) As Task
            Dim total = pseudoPaths.Count
            Dim done = 0
            Dim saved = 0
            For Each pseudo In pseudoPaths
                Dim assetId As String = Nothing, fileName As String = Nothing
                If Not ImmichService.TryParsePseudoPath(pseudo, assetId, fileName) Then Continue For
                done += 1
                StatusText = String.Format(LocalizationService.T("Lade aus Immich… ({0}/{1})"), done, total)
                Dim temp = Await ImmichService.DownloadOriginalToTempAsync(assetId, fileName)
                If String.IsNullOrEmpty(temp) OrElse Not File.Exists(temp) Then Continue For
                Try
                    Dim dest = MakeUniqueFilePath(IO.Path.Combine(targetFolder, If(String.IsNullOrEmpty(fileName), assetId, fileName)))
                    File.Copy(temp, dest, False)
                    saved += 1
                Catch ex As Exception
                    DiagnosticLogService.LogException("Immich.DownloadToFolder", ex)
                End Try
            Next
            If Not _isVirtualFolder AndAlso String.Equals(NormalizePath(targetFolder), NormalizePath(_currentFolder), StringComparison.OrdinalIgnoreCase) Then
                SyncFolderItems()
            End If
            RefreshTree()
            StatusText = String.Format(LocalizationService.T("{0} Bilder aus Immich gespeichert"), saved)
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
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Duplizieren fehlgeschlagen", errorMessage)
        End Function

        Private Shared ReadOnly BatchConvertExcludedExtensions As String() = {".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".svg"}
        Private Shared ReadOnly BatchImageEditWritableExtensions As String() = {".jpg", ".jpeg", ".png", ".webp"}

        Private Async Sub ResizeSelected()
            Dim targetItems = GetSelectedBatchEditableImageItems()
            If targetItems.Count = 0 Then Return

            Dim samplePath = Await EnsureLocalPathForBatchAsync(targetItems(0))
            ' In einer Suchliste oder in Immich gibt es keinen echten Ordner - dann greift die Vorgabe des
            ' Dialogs (zuletzt genutzter Exportordner).
            Dim folderHint = If(_isVirtualFolder, "", If(_currentFolder, ""))
            Dim resize = Await _mainVm.ShowBatchResizeAsync(samplePath, folderHint)
            If resize Is Nothing Then Return

            StatusText = LocalizationService.T("Ändere Bildgröße...")
            Dim preserveMetadata = If(_mainVm?.Settings IsNot Nothing, _mainVm.Settings.PreserveMetadataOnSave, AppSettingsService.Load().PreserveMetadataOnSave)
            ' Beim Überschreiben behält die Datei ihr Format - dort bleibt die bisherige feste Qualität;
            ' bei Kopien zählt die Formatauswahl des Dialogs.
            Dim jpgQuality = If(resize.Overwrite, 95, resize.JpgQuality)
            Dim writer = Function(source As String, target As String)
                             Dim width = resize.Width
                             Dim height = resize.Height
                             If resize.ScalePercent > 0 Then
                                 Dim size = ImageProcessor.GetImageSize(source)
                                 width = Math.Max(1, CInt(Math.Round(size.Width * resize.ScalePercent / 100.0)))
                                 height = Math.Max(1, CInt(Math.Round(size.Height * resize.ScalePercent / 100.0)))
                             End If
                             Dim adj = New ImageAdjustments With {
                                 .ResizeWidth = width,
                                 .ResizeHeight = height,
                                 .LockResizeAspect = resize.LockAspect,
                                 .ResizeInterpolation = resize.Interpolation
                             }
                             Return ImageProcessor.SaveImage(source, target, adj, jpgQuality, preserveMetadata)
                         End Function

            Dim localItems = targetItems.Where(Function(i) Not i.IsImmichAsset).ToList()
            Dim immichItems = targetItems.Where(Function(i) i.IsImmichAsset).ToList()
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
                StatusText = $"{changedCount + uploadedCount} von {targetItems.Count} Datei(en) geändert"
                RefreshAfterBatchFileRewrite(localTargets)
                If uploadedCount > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
                Return
            End If

            ' Als Kopie mit Formatauswahl (Nutzerwunsch 2026-07-17) - gleicher Ablauf wie beim
            ' Stapel-Filter: neue Dateien in den Zielordner oder als neues Asset nach Immich.
            Dim suffix = resize.FileNameSuffix
            If String.Equals(resize.Target, "Immich", StringComparison.OrdinalIgnoreCase) AndAlso ImmichService.IsConfigured Then
                changedCount = Await ProcessLocalBatchItemsToImmichAsync(localItems, writer,
                                                                         Function(source) resize.Extension,
                                                                         uploadedAssetIds, suffix, skipSameExtension:=False).ConfigureAwait(True)
                uploadedCount = Await ProcessImmichBatchItemsAsync(immichItems, writer,
                                                                   Function(source) resize.Extension,
                                                                   uploadedAssetIds, suffix).ConfigureAwait(True)
                StatusText = $"{changedCount + uploadedCount} von {targetItems.Count} Datei(en) geändert"
                If uploadedAssetIds.Count > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
                Return
            End If

            Dim targetFolder = If(resize.TargetFolder, "").Trim()
            If String.IsNullOrWhiteSpace(targetFolder) Then
                Await _mainVm.ShowMessageAsync("Bildgröße ändern", "Kein Zielordner angegeben.")
                Return
            End If
            Dim createFolderError As String = Nothing
            Try
                Directory.CreateDirectory(targetFolder)
            Catch ex As Exception
                createFolderError = ex.Message
            End Try
            If createFolderError IsNot Nothing Then
                Await _mainVm.ShowMessageAsync("Bildgröße ändern", createFolderError)
                Return
            End If

            changedCount = Await ProcessLocalBatchItemsToFolderAsync(localItems, targetFolder, writer,
                                                                     Function(source) resize.Extension,
                                                                     suffix, skipSameExtension:=False,
                                                                     metaCopy:=resize.MetaCopy).ConfigureAwait(True)
            uploadedCount = Await ProcessImmichBatchItemsToFolderAsync(immichItems, targetFolder, writer,
                                                                       Function(source) resize.Extension,
                                                                       suffix).ConfigureAwait(True)

            StatusText = $"{changedCount + uploadedCount} von {targetItems.Count} Datei(en) geändert"
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then SyncFolderItems()
        End Sub

        ''' <summary>Stapel: einen eingebauten Filter, ein Lightroom-Preset (.xmp) oder eine LUT (.cube) auf
        ''' die Auswahl anwenden - entweder in die Originale hinein oder in neue Dateien (mit dem Namen der
        ''' Vorgabe im Dateinamen).</summary>
        Private Async Sub ApplyFilterSelected()
            Dim targetItems = GetSelectedBatchEditableImageItems()
            If targetItems.Count = 0 Then Return

            ' In einer Suchliste oder in Immich gibt es keinen echten Ordner - dann greift die Vorgabe des
            ' Dialogs (zuletzt genutzter Exportordner).
            Dim folderHint = If(_isVirtualFolder, "", If(_currentFolder, ""))
            Dim result = Await _mainVm.ShowBatchFilterAsync(targetItems.Count, folderHint)
            If result Is Nothing Then Return

            Dim adjustmentsTemplate = BuildBatchFilterAdjustments(result)
            If adjustmentsTemplate Is Nothing Then
                Await _mainVm.ShowMessageAsync("Filter anwenden", "Die gewählte Vorgabe konnte nicht gelesen werden.")
                Return
            End If

            StatusText = LocalizationService.T("Wende Filter an...")
            Dim preserveMetadata = If(_mainVm?.Settings IsNot Nothing, _mainVm.Settings.PreserveMetadataOnSave, AppSettingsService.Load().PreserveMetadataOnSave)
            ' Jedes Bild bekommt seinen eigenen Klon: ApplyAdjustments schreibt Quellmaße hinein, ein
            ' geteiltes Objekt würde sie über die Dateien hinweg vermischen.
            Dim writer = Function(source As String, target As String) ImageProcessor.SaveImage(source, target, adjustmentsTemplate.Clone(), result.JpgQuality, preserveMetadata)

            Dim localItems = targetItems.Where(Function(i) Not i.IsImmichAsset).ToList()
            Dim immichItems = targetItems.Where(Function(i) i.IsImmichAsset).ToList()
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
                StatusText = $"{changedCount + uploadedCount} von {targetItems.Count} Datei(en) gefiltert"
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
                StatusText = $"{changedCount + uploadedCount} von {targetItems.Count} Datei(en) gefiltert"
                If uploadedAssetIds.Count > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
                Return
            End If

            Dim targetFolder = If(result.TargetFolder, "").Trim()
            If String.IsNullOrWhiteSpace(targetFolder) Then
                Await _mainVm.ShowMessageAsync("Filter anwenden", "Kein Zielordner angegeben.")
                Return
            End If
            Dim createFolderError As String = Nothing
            Try
                Directory.CreateDirectory(targetFolder)
            Catch ex As Exception
                createFolderError = ex.Message
            End Try
            If createFolderError IsNot Nothing Then
                Await _mainVm.ShowMessageAsync("Filter anwenden", createFolderError)
                Return
            End If

            changedCount = Await ProcessLocalBatchItemsToFolderAsync(localItems, targetFolder, writer,
                                                                     Function(source) result.Extension,
                                                                     suffix, skipSameExtension:=False,
                                                                     metaCopy:=result.MetaCopy).ConfigureAwait(True)
            uploadedCount = Await ProcessImmichBatchItemsToFolderAsync(immichItems, targetFolder, writer,
                                                                       Function(source) result.Extension,
                                                                       suffix).ConfigureAwait(True)

            StatusText = $"{changedCount + uploadedCount} von {targetItems.Count} Datei(en) gefiltert"
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then SyncFolderItems()
        End Sub

        ''' <summary>Übersetzt die Dialogauswahl in Anpassungen. Lightroom-Presets laufen durch denselben
        ''' LightroomPresetService wie der Editor - es gibt nur eine Abbildung der crs:-Schlüssel.
        ''' Nothing, wenn die Preset-Datei fehlt oder nichts Verwertbares enthält.</summary>
        Private Shared Function BuildBatchFilterAdjustments(result As BatchFilterDialogResult) As ImageAdjustments
            Select Case result.SourceKind
                Case BatchFilterDialogResult.SourceLightroom
                    Return LightroomPresetService.LoadLook(result.PresetPath)

                Case BatchFilterDialogResult.SourceLut
                    If String.IsNullOrWhiteSpace(result.PresetPath) OrElse Not File.Exists(result.PresetPath) Then Return Nothing
                    Return New ImageAdjustments With {
                        .LutPath = result.PresetPath,
                        .LutStrength = result.Strength
                    }

                Case Else
                    If String.IsNullOrWhiteSpace(result.DisplayName) Then Return Nothing
                    Return New ImageAdjustments With {
                        .FilterPreset = result.DisplayName,
                        .FilterStrength = result.Strength
                    }
            End Select
        End Function

        Private Async Sub RemoveMetadataSelected()
            Dim targets = GetSelectedEditableImagePaths()
            If targets.Count = 0 Then Return

            StatusText = LocalizationService.T("Entferne Metadaten...")
            Dim changedCount = Await RewriteImagesInPlaceAsync(targets,
                Function(source, temp) ImageProcessor.SaveImage(source, temp, New ImageAdjustments(), 95, preserveMetadata:=False))

            StatusText = $"{changedCount} von {targets.Count} Datei(en) bereinigt"
            RefreshAfterBatchFileRewrite(targets)
        End Sub

        Private Async Sub ApplyWatermarkSelected()
            Dim targetItems = GetSelectedBatchEditableImageItems()
            If targetItems.Count = 0 Then Return

            Dim result = Await _mainVm.ShowWatermarkPresetDialogAsync()
            If result Is Nothing OrElse result.Preset Is Nothing Then Return

            Dim annotation = CreateWatermarkAnnotation(result.Preset)
            If annotation Is Nothing Then
                Await _mainVm.ShowMessageAsync("Wasserzeichen anwenden", "Das ausgewählte Wasserzeichen enthält keinen Text und kein Bild.")
                Return
            End If

            StatusText = LocalizationService.T("Wende Wasserzeichen an...")
            Dim preserveMetadata = If(_mainVm?.Settings IsNot Nothing, _mainVm.Settings.PreserveMetadataOnSave, AppSettingsService.Load().PreserveMetadataOnSave)
            Dim writer = Function(source As String, target As String)
                             Dim adj = New ImageAdjustments()
                             adj.Annotations.Add(annotation.Clone())
                             Return ImageProcessor.SaveImage(source, target, adj, result.JpgQuality, preserveMetadata)
                         End Function
            Dim localItems = targetItems.Where(Function(i) Not i.IsImmichAsset).ToList()
            Dim immichItems = targetItems.Where(Function(i) i.IsImmichAsset).ToList()
            Dim uploadedAssetIds As New List(Of String)()
            Dim changedCount = 0
            Dim uploadedCount = 0

            If result.Overwrite Then
                Dim localTargets = localItems.Where(Function(i) File.Exists(i.FilePath)).Select(Function(i) i.FilePath).ToList()
                changedCount = Await RewriteImagesInPlaceAsync(localTargets, writer)
                uploadedCount = Await ProcessImmichBatchItemsAsync(immichItems, writer,
                                                                   Function(source) IO.Path.GetExtension(source),
                                                                   uploadedAssetIds).ConfigureAwait(True)
                StatusText = $"{changedCount + uploadedCount} von {targetItems.Count} Datei(en) mit Wasserzeichen versehen"
                RefreshAfterBatchFileRewrite(localTargets)
                If uploadedCount > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
                Return
            End If

            If String.Equals(result.Target, "Immich", StringComparison.OrdinalIgnoreCase) AndAlso ImmichService.IsConfigured Then
                changedCount = Await ProcessLocalBatchItemsToImmichAsync(localItems, writer,
                                                                         Function(source) result.Extension,
                                                                         uploadedAssetIds, "", skipSameExtension:=False).ConfigureAwait(True)
                uploadedCount = Await ProcessImmichBatchItemsAsync(immichItems, writer,
                                                                   Function(source) result.Extension,
                                                                   uploadedAssetIds).ConfigureAwait(True)
                StatusText = $"{changedCount + uploadedCount} von {targetItems.Count} Datei(en) mit Wasserzeichen versehen"
                If uploadedAssetIds.Count > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
                Return
            End If

            Dim targetFolder = If(result.TargetFolder, "").Trim()
            If String.IsNullOrWhiteSpace(targetFolder) Then
                Await _mainVm.ShowMessageAsync("Wasserzeichen anwenden", "Kein Zielordner angegeben.")
                Return
            End If
            Dim createFolderError As String = Nothing
            Try
                Directory.CreateDirectory(targetFolder)
            Catch ex As Exception
                createFolderError = ex.Message
            End Try
            If createFolderError IsNot Nothing Then
                Await _mainVm.ShowMessageAsync("Wasserzeichen anwenden", createFolderError)
                Return
            End If

            changedCount = Await ProcessLocalBatchItemsToFolderAsync(localItems, targetFolder, writer,
                                                                     Function(source) result.Extension,
                                                                     "", skipSameExtension:=False,
                                                                     metaCopy:=result.MetaCopy).ConfigureAwait(True)
            uploadedCount = Await ProcessImmichBatchItemsToFolderAsync(immichItems, targetFolder, writer,
                                                                       Function(source) result.Extension).ConfigureAwait(True)

            StatusText = $"{changedCount + uploadedCount} von {targetItems.Count} Datei(en) mit Wasserzeichen versehen"
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then SyncFolderItems()
        End Sub

        Private Shared Function CreateWatermarkAnnotation(preset As WatermarkPresetSettings) As ImageAnnotation
            If preset Is Nothing Then Return Nothing
            Dim text = If(preset.Text, "").Trim()
            Dim imagePath = If(preset.ImagePath, "").Trim()
            If String.IsNullOrWhiteSpace(text) AndAlso String.IsNullOrWhiteSpace(imagePath) Then Return Nothing

            Return New ImageAnnotation With {
                .Kind = "Watermark",
                .Text = If(String.IsNullOrWhiteSpace(text), "FerrumPix", text),
                .ImagePath = imagePath,
                .XPixels = CSng(Math.Max(-100000, Math.Min(100000, preset.OffsetXPixels))),
                .YPixels = CSng(Math.Max(-100000, Math.Min(100000, preset.OffsetYPixels))),
                .WidthPixels = CSng(Math.Max(1, Math.Min(100000, preset.WidthPixels))),
                .HeightPixels = CSng(Math.Max(1, Math.Min(100000, preset.HeightPixels))),
                .FillColor = AppSettingsService.NormalizeHexColor(preset.FillColor, "#FFFFFFFF"),
                .StrokeColor = "#FF000000",
                .StrokeWidth = 0,
                .FontSizePixels = CSng(Math.Max(8, Math.Min(5000, preset.FontSizePixels))),
                .FontFamily = If(String.IsNullOrWhiteSpace(preset.FontFamily), "Arial", preset.FontFamily),
                .Opacity = CSng(Math.Max(0, Math.Min(100, preset.Opacity))),
                .RotationDegrees = CSng(Math.Max(-180, Math.Min(180, preset.RotationDegrees))),
                .Anchor = AppSettingsService.NormalizeAnnotationAnchorName(preset.Anchor),
                .IsVisible = True,
                .FillKind = "Solid",
                .FillColor2 = AppSettingsService.NormalizeHexColor(preset.FillColor, "#FFFFFFFF")
            }
        End Function

        Private Function GetSelectedEditableImagePaths() As List(Of String)
            Return GetSelectedPaths().
                Where(Function(p) File.Exists(p) AndAlso BatchImageEditWritableExtensions.Contains(IO.Path.GetExtension(p).ToLowerInvariant())).
                ToList()
        End Function

        Private Function GetSelectedBatchEditableImageItems() As List(Of ImageItem)
            Return GetSelectedImageItems().
                Where(Function(i) i IsNot Nothing AndAlso i.CanEditFile).
                Where(Function(i) BatchImageEditWritableExtensions.Contains(IO.Path.GetExtension(i.FilePath).ToLowerInvariant())).
                ToList()
        End Function

        Private Async Function RewriteImagesInPlaceAsync(targets As List(Of String), writer As Func(Of String, String, Boolean)) As Task(Of Integer)
            Dim changedCount = 0
            Dim errorMessage As String = Nothing
            Try
                Await Task.Run(Sub()
                    For Each source In targets
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
                    Next
                End Sub)
            Catch ex As Exception
                errorMessage = ex.Message
            End Try
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Bildverarbeitung fehlgeschlagen", errorMessage)
            Return changedCount
        End Function

        Private Async Function EnsureLocalPathForBatchAsync(item As ImageItem) As Task(Of String)
            If item Is Nothing Then Return Nothing
            If Not item.IsImmichAsset Then Return item.FilePath
            Dim localPath = Await ImmichService.DownloadOriginalToTempAsync(item.ImmichAssetId, item.ImmichOriginalFileName)
            If Not String.IsNullOrEmpty(localPath) Then item.ImmichLocalPath = localPath
            Return localPath
        End Function

        Private Function CurrentImmichAlbumIdForUpload() As String
            If SelectedImmichNode IsNot Nothing AndAlso String.Equals(SelectedImmichNode.Kind, "ImmichAlbum", StringComparison.Ordinal) Then
                Return SelectedImmichNode.Id
            End If
            Return Nothing
        End Function

        Private Shared Function CreateImmichBatchOutputPath(sourcePath As String, requestedExtension As String,
                                                            Optional nameSuffix As String = "") As String
            Dim ext = If(String.IsNullOrWhiteSpace(requestedExtension), IO.Path.GetExtension(sourcePath), requestedExtension)
            If String.IsNullOrWhiteSpace(ext) Then ext = ".jpg"
            If Not ext.StartsWith(".", StringComparison.Ordinal) Then ext = "." & ext

            Dim dir = IO.Path.Combine(IO.Path.GetTempPath(), "FerrumPix", "ImmichBatch")
            Directory.CreateDirectory(dir)
            Dim stem = If(String.IsNullOrWhiteSpace(IO.Path.GetFileNameWithoutExtension(sourcePath)), "immich-export", IO.Path.GetFileNameWithoutExtension(sourcePath))
            Return IO.Path.Combine(dir, $"{stem}{If(nameSuffix, "")}-ferrumpix-{Guid.NewGuid():N}{ext}")
        End Function

        ''' <summary>Zielpfad für ein Asset, das ERSETZT wird: Immich übernimmt den Dateinamen des Uploads als
        ''' Originalnamen des Assets, also muss er der alte bleiben - weder der Guid-Name aus
        ''' CreateImmichBatchOutputPath noch ein Filtersuffix haben in einer aktualisierten Bibliothek etwas
        ''' verloren. Eindeutigkeit stellt stattdessen ein eigener Unterordner je Bild her.</summary>
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
        Private Shared Function CreateBatchTargetFolderPath(sourcePath As String, targetFolder As String, requestedExtension As String,
                                                            Optional nameSuffix As String = "") As String
            Dim ext = If(String.IsNullOrWhiteSpace(requestedExtension), IO.Path.GetExtension(sourcePath), requestedExtension)
            If String.IsNullOrWhiteSpace(ext) Then ext = ".jpg"
            If Not ext.StartsWith(".", StringComparison.Ordinal) Then ext = "." & ext

            Dim stem = If(String.IsNullOrWhiteSpace(IO.Path.GetFileNameWithoutExtension(sourcePath)), "ferrumpix-export", IO.Path.GetFileNameWithoutExtension(sourcePath))
            Return MakeUniqueFilePath(IO.Path.Combine(targetFolder, stem & If(nameSuffix, "") & ext))
        End Function

        ''' <param name="uploadedAssetIds">Sammelt die IDs der Assets, die danach in der Ansicht stehen sollen -
        ''' im Update-Modus sind das die ERSETZTEN (ab Immich v3 mit neuer ID), sonst die neu angelegten.</param>
        Private Async Function ProcessImmichBatchItemsAsync(items As IEnumerable(Of ImageItem),
                                                            writer As Func(Of String, String, Boolean),
                                                            outputExtension As Func(Of String, String),
                                                            Optional uploadedAssetIds As List(Of String) = Nothing,
                                                            Optional nameSuffix As String = "") As Task(Of Integer)
            Dim uploadedCount = 0
            Dim errorMessage As String = Nothing
            Dim albumId = CurrentImmichAlbumIdForUpload()
            ' Update-Modus: die Stapelverarbeitung ersetzt die bearbeiteten Assets, statt neben jedes
            ' Original eine bearbeitete Kopie zu legen (siehe Einstellung "Vorhandene Assets aktualisieren").
            Dim updateExisting = AppSettingsService.Load().ImmichUpdateExistingAssets

            Try
                For Each item In If(items, Enumerable.Empty(Of ImageItem)())
                    If item Is Nothing OrElse Not item.IsImmichAsset Then Continue For
                    Dim source = Await EnsureLocalPathForBatchAsync(item)
                    If String.IsNullOrEmpty(source) OrElse Not File.Exists(source) Then Continue For

                    Dim outputPath = If(updateExisting,
                                        CreateImmichReplaceOutputPath(item, outputExtension(source)),
                                        CreateImmichBatchOutputPath(source, outputExtension(source), nameSuffix))
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
                            ' Ersetzte Assets bekommen einen eigenen Unterordner (Namensgleichheit), der mit weg muss.
                            Dim outputDir = IO.Path.GetDirectoryName(outputPath)
                            If updateExisting AndAlso Directory.Exists(outputDir) AndAlso Not Directory.EnumerateFileSystemEntries(outputDir).Any() Then Directory.Delete(outputDir)
                        Catch
                        End Try
                    End Try
                Next
            Catch ex As Exception
                errorMessage = ex.Message
            End Try

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Immich-Upload fehlgeschlagen", errorMessage)
            Return uploadedCount
        End Function

        Private Async Function ProcessImmichBatchItemsToFolderAsync(items As IEnumerable(Of ImageItem),
                                                                    targetFolder As String,
                                                                    writer As Func(Of String, String, Boolean),
                                                                    outputExtension As Func(Of String, String),
                                                                    Optional nameSuffix As String = "") As Task(Of Integer)
            Dim savedCount = 0
            Dim errorMessage As String = Nothing

            Try
                For Each item In If(items, Enumerable.Empty(Of ImageItem)())
                    If item Is Nothing OrElse Not item.IsImmichAsset Then Continue For
                    Dim source = Await EnsureLocalPathForBatchAsync(item)
                    If String.IsNullOrEmpty(source) OrElse Not File.Exists(source) Then Continue For

                    Dim target = CreateBatchTargetFolderPath(source, targetFolder, outputExtension(source), nameSuffix)
                    Dim ok = Await Task.Run(Function() writer(source, target))
                    If ok AndAlso File.Exists(target) Then savedCount += 1
                Next
            Catch ex As Exception
                errorMessage = ex.Message
            End Try

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Immich-Export fehlgeschlagen", errorMessage)
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
                                                                   Optional metaCopy As CatalogMetaCopyOptions = Nothing) As Task(Of Integer)
            Dim savedCount = 0
            Dim errorMessage As String = Nothing

            Try
                Await Task.Run(Sub()
                    For Each item In If(items, Enumerable.Empty(Of ImageItem)())
                        If item Is Nothing OrElse item.IsImmichAsset OrElse Not File.Exists(item.FilePath) Then Continue For
                        Dim sourceExt = IO.Path.GetExtension(item.FilePath)
                        Dim targetExt = outputExtension(item.FilePath)
                        If skipSameExtension AndAlso String.Equals(sourceExt, targetExt, StringComparison.OrdinalIgnoreCase) Then Continue For

                        Dim target = CreateBatchTargetFolderPath(item.FilePath, targetFolder, targetExt, nameSuffix)
                        If writer(item.FilePath, target) AndAlso File.Exists(target) Then
                            savedCount += 1
                            ' Katalog-Metadaten (Bewertung/Favorit/Etikett/Stichworte) wandern zur
                            ' Kopie mit - das Original behält seine (Nutzerwunsch 2026-07-17).
                            LibraryService.Instance.CopyEntryMeta(item.FilePath, target,
                                                                  If(metaCopy Is Nothing, True, metaCopy.CopyRating),
                                                                  If(metaCopy Is Nothing, True, metaCopy.CopyFavorite),
                                                                  If(metaCopy Is Nothing, True, metaCopy.CopyColorLabel),
                                                                  If(metaCopy Is Nothing, True, metaCopy.CopyKeywords))
                        End If
                    Next
                End Sub)
            Catch ex As Exception
                errorMessage = ex.Message
            End Try

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Konvertierung fehlgeschlagen", errorMessage)
            Return savedCount
        End Function

        Private Async Function ProcessLocalBatchItemsToImmichAsync(items As IEnumerable(Of ImageItem),
                                                                   writer As Func(Of String, String, Boolean),
                                                                   outputExtension As Func(Of String, String),
                                                                   Optional uploadedAssetIds As List(Of String) = Nothing,
                                                                   Optional nameSuffix As String = "",
                                                                   Optional skipSameExtension As Boolean = True) As Task(Of Integer)
            Dim uploadedCount = 0
            Dim errorMessage As String = Nothing
            Dim albumId = CurrentImmichAlbumIdForUpload()

            Try
                For Each item In If(items, Enumerable.Empty(Of ImageItem)())
                    If item Is Nothing OrElse item.IsImmichAsset OrElse Not File.Exists(item.FilePath) Then Continue For
                    Dim sourceExt = IO.Path.GetExtension(item.FilePath)
                    Dim targetExt = outputExtension(item.FilePath)
                    If skipSameExtension AndAlso String.Equals(sourceExt, targetExt, StringComparison.OrdinalIgnoreCase) Then Continue For

                    Dim outputPath = CreateImmichBatchOutputPath(item.FilePath, targetExt, nameSuffix)
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
                        Catch
                        End Try
                    End Try
                Next
            Catch ex As Exception
                errorMessage = ex.Message
            End Try

            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Immich-Upload fehlgeschlagen", errorMessage)
            Return uploadedCount
        End Function

        ''' <summary>Lädt die gerade offene Immich-Ansicht neu (z.B. nachdem der Editor ein Asset ersetzt
        ''' hat - die Kachel zeigt sonst das Bild von vorher oder ein Asset, das es nicht mehr gibt).</summary>
        Public Async Function RefreshImmichViewAsync() As Task
            If Not _isVirtualFolder OrElse SelectedImmichNode Is Nothing Then Return
            Await RefreshAfterImmichBatchUploadAsync()
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

        Private Sub RefreshAfterBatchFileRewrite(paths As IEnumerable(Of String))
            For Each item In Items.Where(Function(i) i IsNot Nothing AndAlso paths.Contains(i.FilePath, StringComparer.OrdinalIgnoreCase))
                item.EvictThumbnail()
            Next
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then SyncFolderItems()
        End Sub

        Private Async Sub BatchConvertSelected()
            Dim targetItems = GetSelectedImageItems().
                Where(Function(i) i IsNot Nothing AndAlso Not i.IsFolder).
                Where(Function(i) Not BatchConvertExcludedExtensions.Contains(IO.Path.GetExtension(i.FilePath).ToLowerInvariant())).
                ToList()
            DiagnosticLogService.LogAlways("Gallery.BatchConvert", $"selected={GetSelectedImageItems().Count} convertible={targetItems.Count}")
            If targetItems.Count = 0 Then Return

            Dim result = Await _mainVm.ShowBatchConvertAsync(targetItems.Count, "JPG")
            If result Is Nothing Then Return

            StatusText = LocalizationService.T("Konvertiere…")
            Dim convertedCount = 0
            Dim preserveMetadata = If(_mainVm?.Settings IsNot Nothing, _mainVm.Settings.PreserveMetadataOnSave, AppSettingsService.Load().PreserveMetadataOnSave)
            Dim uploadedCount = 0
            Dim localItems = targetItems.Where(Function(i) Not i.IsImmichAsset).ToList()
            Dim immichItems = targetItems.Where(Function(i) i.IsImmichAsset).ToList()
            Dim saveToImmich = String.Equals(result.Target, "Immich", StringComparison.OrdinalIgnoreCase) AndAlso ImmichService.IsConfigured
            Dim uploadedAssetIds As New List(Of String)()

            If saveToImmich Then
                convertedCount = Await ProcessLocalBatchItemsToImmichAsync(localItems,
                    Function(source, target)
                        Return ImageProcessor.SaveImage(source, target, New ImageAdjustments(), result.JpgQuality, preserveMetadata)
                    End Function,
                    Function(source) result.Extension,
                    uploadedAssetIds).ConfigureAwait(True)

                uploadedCount = Await ProcessImmichBatchItemsAsync(immichItems,
                    Function(source, target)
                        If String.Equals(IO.Path.GetExtension(source), result.Extension, StringComparison.OrdinalIgnoreCase) Then Return False
                        Return ImageProcessor.SaveImage(source, target, New ImageAdjustments(), result.JpgQuality, preserveMetadata)
                    End Function,
                    Function(source) result.Extension,
                    uploadedAssetIds).ConfigureAwait(True)
            Else
                Dim targetFolder = If(result.TargetFolder, "").Trim()
                If String.IsNullOrWhiteSpace(targetFolder) Then
                    Await _mainVm.ShowMessageAsync("Konvertierung fehlgeschlagen", "Kein Zielordner angegeben.")
                    Return
                End If
                Dim createFolderError As String = Nothing
                Try
                    Directory.CreateDirectory(targetFolder)
                Catch ex As Exception
                    createFolderError = ex.Message
                End Try
                If createFolderError IsNot Nothing Then
                    Await _mainVm.ShowMessageAsync("Konvertierung fehlgeschlagen", createFolderError)
                    Return
                End If

                convertedCount = Await ProcessLocalBatchItemsToFolderAsync(localItems,
                    targetFolder,
                    Function(source, target)
                        Return ImageProcessor.SaveImage(source, target, New ImageAdjustments(), result.JpgQuality, preserveMetadata)
                    End Function,
                    Function(source) result.Extension,
                    metaCopy:=result.MetaCopy).ConfigureAwait(True)

                uploadedCount = Await ProcessImmichBatchItemsToFolderAsync(immichItems,
                    targetFolder,
                    Function(source, target)
                        If String.Equals(IO.Path.GetExtension(source), result.Extension, StringComparison.OrdinalIgnoreCase) Then Return False
                        Return ImageProcessor.SaveImage(source, target, New ImageAdjustments(), result.JpgQuality, preserveMetadata)
                    End Function,
                    Function(source) result.Extension).ConfigureAwait(True)
            End If

            StatusText = $"{convertedCount + uploadedCount} von {targetItems.Count} Datei(en) konvertiert"
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then SyncFolderItems()
            If saveToImmich AndAlso uploadedAssetIds.Count > 0 Then Await RefreshAfterImmichBatchUploadAsync(uploadedAssetIds)
        End Sub

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
                    Distinct(StringComparer.OrdinalIgnoreCase).
                    Where(Function(p) Not String.Equals(NormalizePath(p), NormalizePath(targetFolder), StringComparison.OrdinalIgnoreCase)).
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
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Verschieben fehlgeschlagen", errorMessage)
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
            Dim settings = AppSettingsService.Load()
            settings.GalleryShowFolders = _showFolders
            settings.GalleryShowParentFolder = _showParentFolder
            AppSettingsService.Save(settings)
        End Sub

        Private Sub RemovePathsFromVirtualFolder(paths As IEnumerable(Of String))
            If paths Is Nothing Then Return
            Dim moved = paths.ToHashSet(StringComparer.OrdinalIgnoreCase)
            _allItems.RemoveAll(Function(i) i IsNot Nothing AndAlso moved.Contains(i.FilePath))
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
                                   Else
                                       File.Copy(source, target)
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

            If String.Equals(normalizedSource, normalizedTarget, StringComparison.OrdinalIgnoreCase) Then
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

        Private Async Function ResolveConflictTargetAsync(conflictingTarget As String, source As String) As Task(Of String)
            If _conflictBatchDecision.HasValue Then
                If _conflictBatchDecision.Value = FileConflictChoice.OverwriteAll Then
                    DeleteTargetForOverwrite(conflictingTarget)
                    Return conflictingTarget
                End If
                Return Nothing   ' SkipAll
            End If

            Do
                Dim result = Await _mainVm.ShowFileConflictAsync(conflictingTarget, source)
                If result Is Nothing Then Return Nothing

                Select Case result.Choice
                    Case FileConflictChoice.OverwriteAll
                        _conflictBatchDecision = FileConflictChoice.OverwriteAll
                        DeleteTargetForOverwrite(conflictingTarget)
                        Return conflictingTarget
                    Case FileConflictChoice.SkipAll
                        _conflictBatchDecision = FileConflictChoice.SkipAll
                        Return Nothing
                    Case FileConflictChoice.Overwrite
                        DeleteTargetForOverwrite(conflictingTarget)
                        Return conflictingTarget
                    Case FileConflictChoice.Rename
                        Dim newName = If(result.NewName, "").Trim()
                        If String.IsNullOrWhiteSpace(newName) Then Return Nothing
                        If HasInvalidFileNameChars(newName) Then
                            Await _mainVm.ShowMessageAsync("Umbenennen fehlgeschlagen", "Der Name enthält ungültige Zeichen.")
                            Continue Do
                        End If

                        Dim targetFolder = IO.Path.GetDirectoryName(conflictingTarget)
                        If String.IsNullOrEmpty(targetFolder) Then Return Nothing
                        Dim renamedTarget = IO.Path.Combine(targetFolder, newName)
                        If File.Exists(renamedTarget) OrElse Directory.Exists(renamedTarget) Then
                            Await _mainVm.ShowMessageAsync("Umbenennen fehlgeschlagen", "Ein Element mit diesem Namen existiert bereits.")
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
            Return child.Equals(parent, StringComparison.OrdinalIgnoreCase) OrElse
                   child.StartsWith(AppendDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function NormalizePath(path As String) As String
            If String.IsNullOrEmpty(path) Then Return ""
            Dim fullPath = IO.Path.GetFullPath(path)
            Dim root = IO.Path.GetPathRoot(fullPath)
            If String.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) Then Return fullPath
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
