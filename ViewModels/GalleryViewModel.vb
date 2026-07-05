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
        Private _thumbnailLoadCts As New CancellationTokenSource()
        Private _activeSearchCts As CancellationTokenSource
        Private ReadOnly _virtualPathSet As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _savedSearches As New List(Of SearchListEntry)()
        Private ReadOnly _rawExtensions As String() = {".cr2", ".cr3", ".nef", ".arw", ".dng", ".pef", ".rw2"}

        Public Event RequestScrollToItem As EventHandler

        Public Property FolderTree As ObservableCollection(Of FolderNode)
        Public Property SearchTree As ObservableCollection(Of VirtualNavigationNode)
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
                    AppSettingsService.SaveLastGalleryFolder(value)
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
                Me.RaisePropertyChanged(NameOf(HasSelectedItem))
                Me.RaisePropertyChanged(NameOf(HasSelectedImage))
                Me.RaisePropertyChanged(NameOf(HasSelection))
            End Set
        End Property

        Private Sub OnSelectedItemPropertyChanged(sender As Object, e As ComponentModel.PropertyChangedEventArgs)
            If e.PropertyName = NameOf(ImageItem.Rating) Then
                Me.RaisePropertyChanged(NameOf(SelectedRating))
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

        Public Property SearchText As String
            Get
                Return _searchText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_searchText, value)
                FilterAndSort()
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
                Return _filterFavorite <> "All" OrElse _filterRatings.Count > 0 OrElse _filterFileType <> "All"
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
                Me.RaisePropertyChanged(NameOf(IsCollageHeroMode))
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
                Me.RaisePropertyChanged(NameOf(IsCollageHeroLeft))
                Me.RaisePropertyChanged(NameOf(IsCollageHeroRight))
                Me.RaisePropertyChanged(NameOf(IsCollageHeroTop))
                Me.RaisePropertyChanged(NameOf(IsCollageHeroBottom))
                ScheduleCollagePreviewUpdate()
            End Set
        End Property

        Public ReadOnly Property IsCollageHeroLeft As Boolean
            Get
                Return String.Equals(_collageHeroPosition, "Left", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsCollageHeroRight As Boolean
            Get
                Return String.Equals(_collageHeroPosition, "Right", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsCollageHeroTop As Boolean
            Get
                Return String.Equals(_collageHeroPosition, "Top", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsCollageHeroBottom As Boolean
            Get
                Return String.Equals(_collageHeroPosition, "Bottom", StringComparison.OrdinalIgnoreCase)
            End Get
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
                LoadFolderImages(_currentFolder)
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
                LoadFolderImages(_currentFolder)
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

        Public ReadOnly Property OpenInViewerCommand As ICommand
        Public ReadOnly Property OpenInEditorCommand As ICommand
        Public ReadOnly Property RefreshCommand As ICommand
        Public ReadOnly Property ClearSearchCommand As ICommand
        Public ReadOnly Property NavigateForwardCommand As ICommand
        Public ReadOnly Property NavigateUpCommand As ICommand
        Public ReadOnly Property NavigateParentCommand As ICommand
        Public ReadOnly Property NavigatePicturesCommand As ICommand
        Public ReadOnly Property SetSortCommand As ICommand
        Public ReadOnly Property SetSortDirectionCommand As ICommand
        Public ReadOnly Property SetViewModeCommand As ICommand
        Public ReadOnly Property OpenSettingsCommand As ICommand
        Public ReadOnly Property DeleteSelectedCommand As ICommand
        Public ReadOnly Property SelectAllCommand As ICommand
        Public ReadOnly Property ClearSelectionCommand As ICommand
        Public ReadOnly Property OpenFileManagerCommand As ICommand
        Public ReadOnly Property CopyPathCommand As ICommand
        Public ReadOnly Property ToggleFavoriteCommand As ICommand
        Public ReadOnly Property SetSelectedRatingCommand As ICommand
        Public ReadOnly Property RenameSelectedCommand As ICommand
        Public ReadOnly Property CreateFolderCommand As ICommand
        Public ReadOnly Property CopySelectedCommand As ICommand
        Public ReadOnly Property CutSelectedCommand As ICommand
        Public ReadOnly Property PasteCommand As ICommand
        Public ReadOnly Property DuplicateSelectedCommand As ICommand
        Public ReadOnly Property ExportSelectedCommand As ICommand
        Public ReadOnly Property BatchConvertSelectedCommand As ICommand
        Public ReadOnly Property SetFilterFavoriteCommand As ICommand
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

        Public ReadOnly Property HasSelectedItem As Boolean
            Get
                Return _selectedItem IsNot Nothing
            End Get
        End Property

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

        Private _allItems As New List(Of ImageItem)()
        Private _displayWindowFirst As Integer = -1
        Private _displayWindowLast As Integer = -1
        Private _topSpacerHeight As Double
        Private _bottomSpacerHeight As Double

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

            _collagePreviewTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(350)}
            AddHandler _collagePreviewTimer.Tick, Sub()
                                                       _collagePreviewTimer.Stop()
                                                       RefreshCollagePreviewAsync()
                                                   End Sub

            OpenInViewerCommand = ReactiveCommand.Create(Sub() OpenSelectedInViewer())
            OpenInEditorCommand = ReactiveCommand.Create(Sub() OpenSelectedInEditor())
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
            OpenSettingsCommand = ReactiveCommand.Create(Sub() _mainVm.OpenSettings())
            DeleteSelectedCommand = ReactiveCommand.Create(Sub() DeleteSelected())
            SelectAllCommand = ReactiveCommand.Create(Sub() SelectAllVisible())
            ClearSelectionCommand = ReactiveCommand.Create(Sub() ClearSelection())
            OpenFileManagerCommand = ReactiveCommand.Create(Sub() OpenInFileManager())
            CopyPathCommand = ReactiveCommand.Create(Sub() CopySelectedPath())
            ToggleFavoriteCommand = ReactiveCommand.Create(Of ImageItem)(Sub(item) DoToggleFavorite(item))
            RenameSelectedCommand = ReactiveCommand.Create(Sub() RenameSelected())
            CreateFolderCommand = ReactiveCommand.Create(Sub() CreateFolderIn(If(SelectedFolderNode?.FullPath, _currentFolder)))
            CopySelectedCommand = ReactiveCommand.Create(Sub() StoreClipboard(False))
            CutSelectedCommand = ReactiveCommand.Create(Sub() StoreClipboard(True))
            PasteCommand = ReactiveCommand.Create(Sub() PasteIntoFolder(_currentFolder))
            DuplicateSelectedCommand = ReactiveCommand.Create(Sub() DuplicateSelected())
            ExportSelectedCommand = ReactiveCommand.Create(Sub() ExportSelected())
            BatchConvertSelectedCommand = ReactiveCommand.Create(Sub() BatchConvertSelected())
            SetSelectedRatingCommand = ReactiveCommand.Create(Of String)(Sub(r) SetSelectedRating(r))
            SetFilterFavoriteCommand = ReactiveCommand.Create(Of String)(Sub(v) FilterFavorite = v)
            SetFilterRatingCommand = ReactiveCommand.Create(Of String)(Sub(v)
                Dim r As Integer
                If Integer.TryParse(v, r) Then ToggleFilterRating(r)
            End Sub)
            SetFilterTypeCommand = ReactiveCommand.Create(Of String)(Sub(v) FilterFileType = v)
            ClearFiltersCommand = ReactiveCommand.Create(Sub()
                _filterFavorite = "All"
                _filterRatings.Clear()
                _filterFileType = "All"
                For Each n In {NameOf(IsFilterFavoriteAll), NameOf(IsFilterFavoriteOnly),
                                NameOf(IsFilterRatingAll), NameOf(IsFilterRatingUnrated),
                                NameOf(IsFilterRating1Plus), NameOf(IsFilterRating2Plus),
                                NameOf(IsFilterRating3Plus), NameOf(IsFilterRating4Plus),
                                NameOf(IsFilterRating5),
                                NameOf(IsFilterTypeAll), NameOf(IsFilterTypeRaw), NameOf(IsFilterTypeNonRaw),
                                NameOf(HasActiveFilter), NameOf(FilterLabel)}
                    Me.RaisePropertyChanged(n)
                Next
                FilterAndSort()
                SaveGalleryFilters()
            End Sub)

            InitializeFolderTree()
            InitializeVirtualNavigation()
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
            Me.RaisePropertyChanged(NameOf(SelectedRating))
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
            Me.RaisePropertyChanged(NameOf(SelectedRating))
        End Sub

        Public Sub SelectOnly(item As ImageItem)
            If item Is Nothing Then Return
            If item.IsParentFolderEntry Then
                SetNavigationOnlySelection(item)
                Return
            End If
            ReplaceSelection({item})
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
            Me.RaisePropertyChanged(NameOf(SelectedRating))
        End Sub

        Private Function GetSelectedImageItems() As List(Of ImageItem)
            Dim selected = If(SelectedItems IsNot Nothing AndAlso SelectedItems.Count > 0,
                              SelectedItems.AsEnumerable(),
                              If(_selectedItem Is Nothing, Enumerable.Empty(Of ImageItem)(), {_selectedItem}))
            Return selected.Where(Function(i) i IsNot Nothing AndAlso i.IsImage).ToList()
        End Function

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
            LibraryService.Instance.SetRatingForMany(images.Select(Function(i) i.FilePath), targetRating)

            Me.RaisePropertyChanged(NameOf(SelectedRating))
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

        Public Function GetFirstSelectableIndex() As Integer
            For i = 0 To Items.Count - 1
                If Items(i).IsSelectableEntry Then Return i
            Next
            Return -1
        End Function

        Public Function GetFirstNavigableIndex() As Integer
            If Items.Count = 0 Then Return -1
            Return 0
        End Function

        Public Function FindSelectableIndex(startIndex As Integer, offset As Integer) As Integer
            If Items.Count = 0 Then Return -1
            Dim idx = Math.Max(0, Math.Min(Items.Count - 1, startIndex))
            Dim direction = If(offset >= 0, 1, -1)
            Dim remaining = Math.Abs(offset)

            Do
                idx += direction
                If idx < 0 OrElse idx >= Items.Count Then Exit Do
                If Items(idx).IsSelectableEntry Then
                    remaining -= 1
                    If remaining = 0 Then Return idx
                End If
            Loop

            Return -1
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
            End Select
            Return True
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
            CancelActiveSearch()
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) AndAlso _currentFolder <> folderPath Then
                _historyBack.Push(_currentFolder)
                _historyForward.Clear()
                Me.RaisePropertyChanged(NameOf(CanNavigateBack))
                Me.RaisePropertyChanged(NameOf(CanNavigateForward))
            End If
            ClearVirtualFolderState()
            CurrentFolder = folderPath
            LoadFolderImages(folderPath)
        End Sub

        Public Sub LoadCurrentFolder()
            If _isVirtualFolder Then
                FilterAndSort()
            Else
                LoadFolderImages(_currentFolder)
            End If
        End Sub

        Public Sub OpenFolderForImage(imagePath As String)
            If String.IsNullOrEmpty(imagePath) OrElse Not File.Exists(imagePath) Then Return
            Dim folder = IO.Path.GetDirectoryName(imagePath)
            If String.IsNullOrEmpty(folder) Then Return

            SetInitialFolderNodeForPath(folder)
            NavigateToFolder(folder)
            Dim item = Items.FirstOrDefault(Function(i) String.Equals(i.FilePath, imagePath, StringComparison.OrdinalIgnoreCase))
            If item IsNot Nothing Then
                SelectOnly(item)
            Else
                SelectedItem = Nothing
            End If
            If SelectedItem IsNot Nothing Then RaiseEvent RequestScrollToItem(Me, EventArgs.Empty)
        End Sub

        Public Function SelectImageInCurrentView(imagePath As String) As Boolean
            If String.IsNullOrEmpty(imagePath) Then Return False
            Dim item = Items.FirstOrDefault(Function(i) i.IsImage AndAlso String.Equals(i.FilePath, imagePath, StringComparison.OrdinalIgnoreCase))
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

        Private Sub OpenSavedSearch(node As VirtualNavigationNode)
            If node Is Nothing Then Return
            If Not String.IsNullOrWhiteSpace(node.RootFolder) AndAlso Not Directory.Exists(node.RootFolder) Then
                StatusText = LocalizationService.T("Startordner nicht gefunden")
                Return
            End If
            SelectedSearchNode = node
            StartIncrementalSavedSearch(node)
        End Sub

        Private Shared Function CreateSavedSearchNode(search As SearchListEntry) As VirtualNavigationNode
            Return New VirtualNavigationNode(search.Name, "SavedSearch") With {
                .Id = search.Id,
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
                                                          item.Tags = If(m IsNot Nothing AndAlso m.Tags IsNot Nothing, m.Tags, New List(Of String)())
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
                LibraryService.Instance.SetExifData(meta.FilePath, fields)
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

        Private Shared Function BuildSavedSearchQuery(textQuery As String, favoriteMode As String, ratingMin As Integer) As String
            Dim parts As New List(Of String)()
            textQuery = If(textQuery, "").Trim()
            If Not String.IsNullOrWhiteSpace(textQuery) Then parts.Add(textQuery)
            Select Case AppSettingsService.NormalizeSearchFavoriteMode(favoriteMode)
                Case "Only"
                    parts.Add("favorit")
                Case "Not"
                    parts.Add("kein favorit")
            End Select
            If ratingMin = 0 Then
                parts.Add("0 Sterne")
            ElseIf ratingMin > 0 Then
                parts.Add($">={ratingMin} Sterne")
            End If
            Return String.Join(" ", parts)
        End Function

        Private Shared Function NormalizeRatings(ratings As IEnumerable(Of Integer)) As HashSet(Of Integer)
            Return New HashSet(Of Integer)(If(ratings, Enumerable.Empty(Of Integer)()).
                Select(Function(r) Math.Max(0, Math.Min(5, r))))
        End Function

        Private Sub SaveSearches()
            SearchListService.Save(_savedSearches)
        End Sub

        Private Sub LoadVirtualFolder(name As String, metas As IEnumerable(Of LibraryImageMeta))
            CancelActiveSearch()
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

            For Each meta In If(metas, Enumerable.Empty(Of LibraryImageMeta)())
                If meta Is Nothing OrElse String.IsNullOrWhiteSpace(meta.FilePath) Then Continue For
                If Not File.Exists(meta.FilePath) Then Continue For
                If Not _imageExtensions.Contains(IO.Path.GetExtension(meta.FilePath).ToLowerInvariant()) Then Continue For
                If Not _virtualPathSet.Add(meta.FilePath) Then Continue For

                Dim item = New ImageItem(meta.FilePath, thumbnailToken) With {
                    .IsFavorite = meta.IsFavorite,
                    .Rating = meta.Rating,
                    .Tags = If(meta.Tags, New List(Of String)())
                }
                _allItems.Add(item)
            Next

            FilterAndSort()
            Me.RaisePropertyChanged(NameOf(IsVirtualFolder))
            Me.RaisePropertyChanged(NameOf(CurrentFolderName))
            Me.RaisePropertyChanged(NameOf(BreadcrumbParent))
            Me.RaisePropertyChanged(NameOf(HasBreadcrumbParent))
            Me.RaisePropertyChanged(NameOf(CanNavigateBack))
            Me.RaisePropertyChanged(NameOf(CanNavigateForward))
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
                    .Tags = If(meta.Tags, New List(Of String)())
                })
                added = True
            Next
            If added Then FilterAndSort()
        End Sub

        ''' Adds pre-built ImageItem objects (constructed on a background thread) to the virtual
        ''' folder without any filesystem I/O — only dedup check and collection mutation.
        Private Sub AddPrebuiltItemsToVirtualFolder(items As List(Of ImageItem))
            Dim added = False
            For Each item In If(items, New List(Of ImageItem)())
                If item Is Nothing Then Continue For
                If Not _virtualPathSet.Add(item.FilePath) Then Continue For
                _allItems.Add(item)
                added = True
            Next
            If added Then FilterAndSort()
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

        Private ReadOnly _imageExtensions As String() = {".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".heic", ".avif", ".ico", ".svg", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".pef", ".rw2", ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"}

        Private Sub UpdateStorageInfo()
            If _isVirtualFolder OrElse String.IsNullOrEmpty(_currentFolder) Then
                StorageFreeText = ""
                StorageFillPercent = 0
                Return
            End If

            Try
                Dim drive = DriveInfo.GetDrives().
                    Where(Function(d) d.IsReady AndAlso
                          _currentFolder.StartsWith(d.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase)).
                    OrderByDescending(Function(d) d.RootDirectory.FullName.Length).
                    FirstOrDefault()

                If drive Is Nothing Then
                    StorageFreeText = ""
                    StorageFillPercent = 0
                    Return
                End If

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

                StorageFreeText = $"{freeStr} von {totalStr} frei"
                StorageFillPercent = Math.Max(0, Math.Min(100,
                    (drive.TotalSize - drive.AvailableFreeSpace) / CDbl(drive.TotalSize) * 100))
            Catch
                StorageFreeText = ""
                StorageFillPercent = 0
            End Try
        End Sub

        Private Sub DoToggleFavorite(item As ImageItem)
            If item Is Nothing OrElse item.IsFolder Then Return
            Dim newVal = Not item.IsFavorite
            item.IsFavorite = newVal
            LibraryService.Instance.SetFavorite(item.FilePath, newVal)
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
                _watcher = New FileSystemWatcher(folderPath) With {
                    .NotifyFilter = NotifyFilters.FileName Or NotifyFilters.DirectoryName,
                    .EnableRaisingEvents = True
                }
                AddHandler _watcher.Created, AddressOf OnFileSystemChanged
                AddHandler _watcher.Deleted, AddressOf OnFileSystemChanged
                AddHandler _watcher.Renamed, AddressOf OnFileSystemChanged
            Catch
            End Try
        End Sub

        Private Sub OnFileSystemChanged(sender As Object, e As FileSystemEventArgs)
            If _pendingReload Then Return
            _pendingReload = True
            Dispatcher.UIThread.Post(Sub()
                _pendingReload = False
                LoadFolderImages(_currentFolder)
                ' Externe Änderungen (z.B. anderer Dateimanager) betreffen nur die aktuell
                ' beobachtete Ordner-Ebene - den zugehörigen Baum-Knoten mit aktualisieren,
                ' damit dessen Unterordnerliste im TreeView nicht veraltet bleibt.
                Dim node = FindLoadedFolderNode(FolderTree, _currentFolder)
                If node IsNot Nothing Then node.ReloadChildren()
            End Sub, DispatcherPriority.Background)
        End Sub

        Private Sub LoadFolderImages(folderPath As String)
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

            Try
                If _showFolders Then
                    If _showParentFolder Then
                        Dim parentPath = IO.Path.GetDirectoryName(folderPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
                        If Not String.IsNullOrEmpty(parentPath) AndAlso Not IsAncestorOrSelf(folderPath, parentPath) AndAlso Directory.Exists(parentPath) Then
                            _allItems.Add(ImageItem.CreateParentFolderEntry(parentPath))
                        End If
                    End If

                    Dim directories = Directory.GetDirectories(folderPath).
                        Where(Function(d) FolderNode.ShowHiddenFolders OrElse Not IO.Path.GetFileName(d).StartsWith(".")).
                        OrderBy(Function(d) IO.Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase).
                        ToArray()

                    For Each folder In directories
                        _allItems.Add(ImageItem.FromFolder(folder))
                    Next
                End If

                Dim files = Directory.GetFiles(folderPath).
                    Where(Function(f) _imageExtensions.Contains(IO.Path.GetExtension(f).ToLowerInvariant())).
                    ToArray()

                Dim meta = LibraryService.Instance.GetFolderMeta(folderPath)
                Dim itemsNeedingMetaRefresh As New List(Of ImageItem)()
                For Each file In files
                    Dim item = New ImageItem(file, thumbnailToken)
                    Dim m As LibraryImageMeta = Nothing
                    If meta.TryGetValue(file, m) Then
                        item.IsFavorite = m.IsFavorite
                        item.Rating = m.Rating
                        item.Tags = If(m.Tags, New List(Of String)())
                        ' Nur übernehmen, wenn die Datei sich seit dem letzten EXIF-Scan nicht geändert hat -
                        ' sonst würde man dauerhaft veraltete Breite/Höhe/EXIF-Daten anzeigen (siehe IsMetaStale).
                        If m.ImageWidth.HasValue AndAlso m.ImageHeight.HasValue AndAlso
                           IsScannedSnapshotFresh(m.ScannedSourceModifiedAt, item.DateModified) Then
                            item.ImageWidth = m.ImageWidth.Value
                            item.ImageHeight = m.ImageHeight.Value
                            item.ExifDateTaken = ExifService.ParseExifDateTime(m.DateTaken)
                            item.ExifDateModified = ExifService.ParseExifDateTime(m.DateModifiedExif)
                            item.ExifCamera = m.Camera
                            item.ExifIso = m.Iso
                            item.ExifAperture = m.Aperture
                        Else
                            itemsNeedingMetaRefresh.Add(item)
                        End If
                    Else
                        itemsNeedingMetaRefresh.Add(item)
                    End If
                    _allItems.Add(item)
                Next

                FilterAndSort()

                If itemsNeedingMetaRefresh.Count > 0 Then
                    QueueBackgroundMetaRefresh(itemsNeedingMetaRefresh, thumbnailToken)
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
                Task.Run(Async Function()
                             Try
                                 Await Task.Delay(BackgroundThumbnailStartupDelayMs, thumbnailToken)
                             Catch ex As OperationCanceledException
                                 Return
                             End Try
                             Dispatcher.UIThread.Post(Sub() ImageItem.QueueBackgroundThumbnails(itemsSnapshot), DispatcherPriority.Background)
                         End Function)
            Catch ex As UnauthorizedAccessException
                StatusText = LocalizationService.T("Zugriff verweigert")
            Catch ex As IOException
                StatusText = LocalizationService.T("Fehler beim Laden")
            End Try
        End Sub

        ''' <summary>Lädt Breite/Höhe/EXIF-Daten für Bilder nach, die beim Ordner-Scan noch fehlten oder
        ''' seit dem letzten Scan geändert wurden - mit begrenzter Parallelität und niedriger Priorität,
        ''' analog zur bestehenden Hintergrund-Thumbnail-Vorladung. Reihenfolge/Filter werden erst einmal
        ''' am Ende neu berechnet (kein Re-Sort pro Einzel-Item), damit die Kacheln beim Scrollen nicht
        ''' springen, während die Daten nach und nach eintreffen.</summary>
        Private Sub QueueBackgroundMetaRefresh(items As List(Of ImageItem), cancellationToken As CancellationToken)
            If items Is Nothing OrElse items.Count = 0 Then Return
            Const MetaRefreshStartupDelayMs As Integer = 1500
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
                                                               LibraryService.Instance.SetExifData(item.FilePath, fields)
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
                                                                                                          item.ExifDateTaken = exifTaken
                                                                                                          item.ExifDateModified = exifModified
                                                                                                          item.ExifCamera = camera
                                                                                                          item.ExifIso = iso
                                                                                                          item.ExifAperture = aperture
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

        Public Sub SetDisplayWindow(firstIndex As Integer, lastIndex As Integer, itemSlotHeight As Double, columns As Integer)
            If Items Is Nothing OrElse Items.Count = 0 Then
                DisplayItems.Clear()
                _displayWindowFirst = -1
                _displayWindowLast = -1
                TopSpacerHeight = 0
                BottomSpacerHeight = 0
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
            Dim remainingItems = Math.Max(0, Items.Count - lastIndex - 1)
            Dim bottomRows = CInt(Math.Ceiling(remainingItems / CDbl(columns)))
            TopSpacerHeight = topRows * itemSlotHeight
            BottomSpacerHeight = bottomRows * itemSlotHeight

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
            If Items Is Nothing OrElse DisplayItems Is Nothing Then Return
            DisplayItems.ReplaceAll(Items.Take(Math.Min(120, Items.Count)))
        End Sub

        Private Sub FilterAndSort()
            Dim filtered = _allItems.AsEnumerable()

            If Not String.IsNullOrEmpty(_searchText) Then
                filtered = filtered.Where(Function(i) i.SearchText.Contains(_searchText, StringComparison.OrdinalIgnoreCase))
            End If

            If _filterFavorite = "Only" Then
                filtered = filtered.Where(Function(i) i.IsFolder OrElse i.IsFavorite)
            End If

            If _filterRatings.Count > 0 Then
                filtered = filtered.Where(Function(i) i.IsFolder OrElse _filterRatings.Contains(i.Rating))
            End If

            If _filterFileType = "Raw" Then
                filtered = filtered.Where(Function(i) i.IsFolder OrElse _rawExtensions.Contains(IO.Path.GetExtension(i.FilePath).ToLowerInvariant()))
            ElseIf _filterFileType = "NonRaw" Then
                filtered = filtered.Where(Function(i) i.IsFolder OrElse Not _rawExtensions.Contains(IO.Path.GetExtension(i.FilePath).ToLowerInvariant()))
            End If

            Items.ReplaceAll(SortItems(filtered))
            If DisplayItems.Count = 0 Then
                ResetDisplayWindow()
            Else
                _displayWindowFirst = -1
                _displayWindowLast = -1
            End If
            Me.RaisePropertyChanged(NameOf(FooterStatusText))

            Dim imageCount = Items.Where(Function(i) i.IsImage).Count()
            Dim folderCount = Items.Where(Function(i) i.IsFolder AndAlso Not i.IsParentFolderEntry).Count()
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

        Public Sub OpenSelectedInViewer()
            Dim images = GetSelectedImageItems()
            If images.Count > 0 Then
                _mainVm.OpenImageInViewer(images(0).FilePath, Items.Where(Function(i) i.IsImage).Select(Function(i) i.FilePath).ToList())
            ElseIf SelectedItem IsNot Nothing AndAlso SelectedItem.IsParentFolderEntry Then
                NavigateToParent()
            End If
        End Sub

        Public Sub OpenSelectedInEditor()
            Dim image = GetSelectedImageItems().FirstOrDefault(Function(i) i.CanEditFile)
            If image IsNot Nothing Then
                _mainVm.OpenImageInEditor(image.FilePath, Items.Where(Function(i) i.IsImage AndAlso i.CanEditFile).Select(Function(i) i.FilePath).ToList())
            End If
        End Sub

        Public Sub DeleteSelected()
            If _isVirtualFolder Then
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
            _mainVm.RequestDeletePaths(targets, Sub()
                                                    ClearSelection()
                                                    If currentFolderWasDeleted AndAlso Not String.IsNullOrEmpty(fallbackFolder) AndAlso Directory.Exists(fallbackFolder) Then
                                                        CurrentFolder = fallbackFolder
                                                        LoadFolderImages(fallbackFolder)
                                                        RefreshTree()
                                                        SelectFolderInTreeByPath(fallbackFolder)
                                                    Else
                                                        LoadFolderImages(_currentFolder)
                                                        RefreshTree()
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

        Public ReadOnly Property SelectedPath As String
            Get
                If SelectedItem Is Nothing OrElse SelectedItem.IsParentFolderEntry Then Return ""
                Return If(SelectedItem?.FilePath, "")
            End Get
        End Property

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
                                                      LoadFolderImages(_currentFolder)
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
                LoadFolderImages(_currentFolder)
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
                    LoadFolderImages(_currentFolder)
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
            If ok Then LoadFolderImages(CurrentFolder)
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

        Public Sub PasteIntoFolder(targetFolder As String)
            Dim ignored = PasteIntoFolderAsync(targetFolder)
        End Sub

        Public Sub PastePathsIntoFolder(paths As IEnumerable(Of String), targetFolder As String, Optional cut As Boolean = False)
            Dim ignored = PastePathsIntoFolderAsync(paths, targetFolder, cut)
        End Sub

        Public Async Function PastePathsIntoFolderAsync(paths As IEnumerable(Of String), targetFolder As String, Optional cut As Boolean = False) As Task
            If IsVirtualFolderPath(targetFolder) Then Return
            If paths Is Nothing OrElse String.IsNullOrEmpty(targetFolder) OrElse Not Directory.Exists(targetFolder) Then Return
            If Not FileOperationPolicy.CanPasteInto(targetFolder) Then Return
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
                    LoadFolderImages(_currentFolder)
                End If
                RefreshTree()
            Catch ex As Exception
                errorMessage = ex.Message
            End Try
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Einfügen fehlgeschlagen", errorMessage)
        End Function

        Public Sub DuplicateSelected()
            If _isVirtualFolder Then Return
            Dim targets = GetSelectedPaths()
            If targets.Count = 0 OrElse String.IsNullOrEmpty(_currentFolder) OrElse Not FileOperationPolicy.CanPasteInto(_currentFolder) Then Return
            Try
                For Each source In targets
                    If Not FileOperationPolicy.CanDuplicate(source, _currentFolder) Then Continue For
                    CopyOrMovePath(source, _currentFolder, False, True)
                Next
                LoadFolderImages(_currentFolder)
                RefreshTree()
            Catch ex As Exception
                Dim ignored = _mainVm.ShowMessageAsync("Duplizieren fehlgeschlagen", ex.Message)
            End Try
        End Sub

        Private Async Sub ExportSelected()
            Dim targets = GetSelectedPaths().Where(Function(p) File.Exists(p)).ToList()
            If targets.Count = 0 OrElse _isVirtualFolder OrElse String.IsNullOrEmpty(_currentFolder) Then Return

            Dim defaultFolderName = $"Export_{DateTime.Now:yyyyMMdd_HHmmss}"
            Dim folderName = Await _mainVm.ShowInputAsync(AppDialogKind.Input, "Exportieren", "Ordnernamen eingeben", defaultFolderName, "Exportieren", "Abbrechen")
            If String.IsNullOrWhiteSpace(folderName) Then Return
            folderName = folderName.Trim()
            If HasInvalidFileNameChars(folderName) Then
                Await _mainVm.ShowMessageAsync("Export fehlgeschlagen", "Der Ordnername enthält ungültige Zeichen.")
                Return
            End If

            Dim exportFolder = IO.Path.Combine(_currentFolder, folderName)
            Dim errorMessage As String = Nothing
            Try
                If Not Directory.Exists(exportFolder) Then Directory.CreateDirectory(exportFolder)
                PastePathsIntoFolder(targets, exportFolder, False)
            Catch ex As Exception
                errorMessage = ex.Message
            End Try
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Export fehlgeschlagen", errorMessage)
        End Sub

        Private Shared ReadOnly BatchConvertExcludedExtensions As String() = {".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v", ".svg"}

        Private Async Sub BatchConvertSelected()
            Dim targets = GetSelectedPaths().
                Where(Function(p) File.Exists(p) AndAlso Not BatchConvertExcludedExtensions.Contains(IO.Path.GetExtension(p).ToLowerInvariant())).
                ToList()
            If targets.Count = 0 Then Return

            Dim result = Await _mainVm.ShowBatchConvertAsync(targets.Count, "JPG")
            If result Is Nothing Then Return

            StatusText = LocalizationService.T("Konvertiere…")
            Dim convertedCount = 0
            Dim errorMessage As String = Nothing
            Try
                Await Task.Run(Sub()
                    For Each source In targets
                        Dim sourceExt = IO.Path.GetExtension(source)
                        If String.Equals(sourceExt, result.Extension, StringComparison.OrdinalIgnoreCase) Then Continue For

                        Dim target = IO.Path.ChangeExtension(source, result.Extension)
                        Dim suffix = 1
                        While File.Exists(target)
                            target = IO.Path.Combine(IO.Path.GetDirectoryName(source), $"{IO.Path.GetFileNameWithoutExtension(source)}_{suffix}{result.Extension}")
                            suffix += 1
                        End While

                        If ImageProcessor.SaveImage(source, target, New ImageAdjustments(), result.JpgQuality) Then
                            convertedCount += 1
                        End If
                    Next
                End Sub)
            Catch ex As Exception
                errorMessage = ex.Message
            End Try

            StatusText = $"{convertedCount} von {targets.Count} Datei(en) konvertiert"
            If errorMessage IsNot Nothing Then Await _mainVm.ShowMessageAsync("Konvertierung fehlgeschlagen", errorMessage)
            If Not _isVirtualFolder AndAlso Not String.IsNullOrEmpty(_currentFolder) Then LoadFolderImages(_currentFolder)
        End Sub

        Public Sub MovePathsToFolder(paths As IEnumerable(Of String), targetFolder As String)
            Dim ignored = MovePathsToFolderAsync(paths, targetFolder)
        End Sub

        Public Async Function MovePathsToFolderAsync(paths As IEnumerable(Of String), targetFolder As String) As Task
            If IsVirtualFolderPath(targetFolder) Then Return
            If paths Is Nothing OrElse String.IsNullOrEmpty(targetFolder) OrElse Not Directory.Exists(targetFolder) Then Return
            If Not FileOperationPolicy.CanPasteInto(targetFolder) Then Return
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
                    LoadFolderImages(_currentFolder)
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

        Private Sub CopyOrMovePath(source As String, targetFolder As String, movePath As Boolean, Optional duplicate As Boolean = False)
            CopyOrMovePathAsync(source, targetFolder, movePath, duplicate).GetAwaiter().GetResult()
        End Sub

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

        Private Async Function ResolveConflictTargetAsync(conflictingTarget As String, source As String) As Task(Of String)
            Do
                Dim result = Await _mainVm.ShowFileConflictAsync(conflictingTarget, source)
                If result Is Nothing Then Return Nothing

                Select Case result.Choice
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

        Public Sub SelectNext()
            SelectByOffset(1)
        End Sub

        Public Sub SelectPrevious()
            SelectByOffset(-1)
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
