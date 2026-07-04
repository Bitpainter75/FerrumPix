Imports System.Collections.ObjectModel
Imports System.Globalization
Imports System.Linq
Imports System.Windows.Input
Imports Avalonia
Imports Avalonia.Media
Imports Avalonia.Styling
Imports ReactiveUI
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace ViewModels

    Public Class SettingsViewModel
        Inherits ViewModelBase

        Private ReadOnly _mainVm As MainWindowViewModel
        Private _selectedSection As SettingsSection = SettingsSection.Appearance
        Private ReadOnly _appSettings As AppSettings
        Private _themeMode As String = "Dark"
        Private _accentColor As String = "#F08A1A"
        Private _showBreadcrumbs As Boolean = True
        Private _thumbnailQuality As Integer = 82
        Private _jpgSaveQuality As Integer = 90
        Private _thumbnailCacheEnabled As Boolean = True
        Private _cacheSizeMb As Integer = 512
        Private _viewerOpenFitToWindow As Boolean = True
        Private _showHiddenFolders As Boolean = False
        Private _galleryShowFolders As Boolean = True
        Private _galleryShowParentFolder As Boolean = True
        Private _galleryViewMode As String = "Grid"
        Private _galleryStartupFolderMode As String = "Pictures"
        Private _viewerShowFilmstrip As Boolean = True
        Private _viewerSlideshowIntervalSeconds As Integer = 3
        Private _editorShowFilmstrip As Boolean = True
        Private _editorInfoSidebarExpanded As Boolean = True
        Private _viewerInfoSidebarExpanded As Boolean = True
        Private _startupImageMode As String = "Viewer"
        Private _languageMode As String = "System"
        Private _applicationScale As Double = 1.0
        Private _applicationScaleScreen As String = "HDMI-A-1"
        Private _runningApplicationScale As Double = 1.0
        Private _runningApplicationScaleScreen As String = "HDMI-A-1"
        Private ReadOnly _applicationScaleScreens As New ObservableCollection(Of String)()
        Private _cleanupResultMessage As String = ""
        Private _thumbnailCacheResultMessage As String = ""
        Private _videoHardwareAcceleration As Boolean = False
        Private _transparencyBackgroundMode As String = "Checkerboard"
        Private _transparencyBackgroundColor As String = "#FFFFFFFF"

        Private _savedThemeMode As String = "Dark"
        Private _savedAccentColor As String = "#F08A1A"
        Private _savedTabBar As Boolean = True
        Private _savedBreadcrumbs As Boolean = True
        Private _savedRounded As Boolean = False
        Private _savedDensity As String = "Komfortabel"
        Private _savedViewerOpenFitToWindow As Boolean = True
        Private _savedThumbnailQuality As Integer = 82
        Private _savedJpgSaveQuality As Integer = 90
        Private _savedThumbnailCacheEnabled As Boolean = True
        Private _savedShowHiddenFolders As Boolean = False
        Private _savedGalleryShowFolders As Boolean = True
        Private _savedGalleryShowParentFolder As Boolean = True
        Private _savedGalleryViewMode As String = "Grid"
        Private _savedGalleryStartupFolderMode As String = "Pictures"
        Private _savedViewerShowFilmstrip As Boolean = True
        Private _savedViewerSlideshowIntervalSeconds As Integer = 3
        Private _savedEditorShowFilmstrip As Boolean = True
        Private _savedEditorInfoSidebarExpanded As Boolean = True
        Private _savedViewerInfoSidebarExpanded As Boolean = True
        Private _savedStartupImageMode As String = "Viewer"
        Private _savedLanguageMode As String = "System"
        Private _savedApplicationScale As Double = 1.0
        Private _savedApplicationScaleScreen As String = "HDMI-A-1"
        Private _savedVideoHardwareAcceleration As Boolean = False
        Private _savedTransparencyBackgroundMode As String = "Checkerboard"
        Private _savedTransparencyBackgroundColor As String = "#FFFFFFFF"

        Public Property SelectedSection As SettingsSection
            Get
                Return _selectedSection
            End Get
            Set(value As SettingsSection)
                Me.RaiseAndSetIfChanged(_selectedSection, value)
            End Set
        End Property

        Public Property ThemeMode As String
            Get
                Return _themeMode
            End Get
            Set(value As String)
                value = AppSettingsService.NormalizeThemeMode(value)
                If _themeMode = value Then Return
                Me.RaiseAndSetIfChanged(_themeMode, value)
                RaiseThemeModeProperties()
                ApplyTheme(_themeMode, _accentColor)
                _mainVm?.RefreshThemeBindings()
                SaveAppearanceSettings()
            End Set
        End Property

        Public Property AccentColor As String
            Get
                Return _accentColor
            End Get
            Set(value As String)
                value = AppSettingsService.NormalizeAccentColor(value)
                If _accentColor = value Then Return
                Me.RaiseAndSetIfChanged(_accentColor, value)
                RaiseAccentProperties()
                ApplyTheme(_themeMode, _accentColor)
                SaveAppearanceSettings()
            End Set
        End Property

        Public ReadOnly Property IsDarkThemeMode As Boolean
            Get
                Return _themeMode = "Dark"
            End Get
        End Property

        Public ReadOnly Property IsLightThemeMode As Boolean
            Get
                Return _themeMode = "Light"
            End Get
        End Property

        Public ReadOnly Property IsGrayDarkThemeMode As Boolean
            Get
                Return _themeMode = "GrayDark"
            End Get
        End Property

        Public ReadOnly Property IsGrayLightThemeMode As Boolean
            Get
                Return _themeMode = "GrayLight"
            End Get
        End Property

        Public ReadOnly Property IsLightOrGrayLightThemeMode As Boolean
            Get
                Return _themeMode = "Light"
            End Get
        End Property

        Public ReadOnly Property IsDarkOrGrayDarkThemeMode As Boolean
            Get
                Return Not IsLightOrGrayLightThemeMode
            End Get
        End Property

        Public ReadOnly Property IsOrangeAccent As Boolean
            Get
                Return _accentColor = "#F08A1A"
            End Get
        End Property

        Public ReadOnly Property IsRedAccent As Boolean
            Get
                Return _accentColor = "#E74C3C"
            End Get
        End Property

        Public ReadOnly Property IsPinkAccent As Boolean
            Get
                Return _accentColor = "#F03B88"
            End Get
        End Property

        Public ReadOnly Property IsPurpleAccent As Boolean
            Get
                Return _accentColor = "#8B5CF6"
            End Get
        End Property

        Public ReadOnly Property IsBlueAccent As Boolean
            Get
                Return _accentColor = "#3B82F6"
            End Get
        End Property

        Public ReadOnly Property IsCyanAccent As Boolean
            Get
                Return _accentColor = "#0891B2"
            End Get
        End Property

        Public ReadOnly Property IsTealAccent As Boolean
            Get
                Return _accentColor = "#0F766E"
            End Get
        End Property

        Public ReadOnly Property IsGreenAccent As Boolean
            Get
                Return _accentColor = "#22C55E"
            End Get
        End Property

        Public ReadOnly Property IsYellowAccent As Boolean
            Get
                Return _accentColor = "#FACC15"
            End Get
        End Property

        Public Property ShowBreadcrumbs As Boolean
            Get
                Return _showBreadcrumbs
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_showBreadcrumbs, value)
            End Set
        End Property

        Public Property ThumbnailQuality As Integer
            Get
                Return _thumbnailQuality
            End Get
            Set(value As Integer)
                value = AppSettingsService.NormalizeThumbnailQuality(value)
                If _thumbnailQuality = value Then Return
                Me.RaiseAndSetIfChanged(_thumbnailQuality, value)
                SavePerformanceSettings()
            End Set
        End Property

        Public Property ThumbnailCacheEnabled As Boolean
            Get
                Return _thumbnailCacheEnabled
            End Get
            Set(value As Boolean)
                If _thumbnailCacheEnabled = value Then Return
                Me.RaiseAndSetIfChanged(_thumbnailCacheEnabled, value)
                SavePerformanceSettings()
            End Set
        End Property

        Public Property JpgSaveQuality As Integer
            Get
                Return _jpgSaveQuality
            End Get
            Set(value As Integer)
                value = AppSettingsService.NormalizeJpgSaveQuality(value)
                If _jpgSaveQuality = value Then Return
                Me.RaiseAndSetIfChanged(_jpgSaveQuality, value)
                SavePerformanceSettings()
            End Set
        End Property

        Public Property CacheSizeMb As Integer
            Get
                Return _cacheSizeMb
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_cacheSizeMb, value)
            End Set
        End Property

        Public Property ViewerOpenFitToWindow As Boolean
            Get
                Return _viewerOpenFitToWindow
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_viewerOpenFitToWindow, value)
            End Set
        End Property

        Public Property StartupImageMode As String
            Get
                Return _startupImageMode
            End Get
            Set(value As String)
                value = AppSettingsService.NormalizeStartupImageMode(value)
                If _startupImageMode = value Then Return
                Me.RaiseAndSetIfChanged(_startupImageMode, value)
                RaiseStartupImageModeProperties()
                SaveStartupSettings()
            End Set
        End Property

        Public Property LanguageMode As String
            Get
                Return _languageMode
            End Get
            Set(value As String)
                value = LocalizationService.NormalizeLanguageMode(value)
                If _languageMode = value Then Return
                Me.RaiseAndSetIfChanged(_languageMode, value)
                RaiseLanguageModeProperties()
                LocalizationService.LanguageMode = value
                SaveLanguageSettings()
                _mainVm?.RefreshLocalization()
            End Set
        End Property

        Public Property ApplicationScalePercent As Double
            Get
                Return _applicationScale * 100.0
            End Get
            Set(value As Double)
                Dim scale = AppSettingsService.NormalizeApplicationScale(value / 100.0)
                If Math.Abs(_applicationScale - scale) < 0.001 Then Return
                Me.RaiseAndSetIfChanged(_applicationScale, scale)
                Me.RaisePropertyChanged(NameOf(ApplicationScaleText))
                Me.RaisePropertyChanged(NameOf(IsApplicationScaleRestartRequired))
                SaveApplicationScaleSettings()
            End Set
        End Property

        Public Property ApplicationScaleScreen As String
            Get
                Return _applicationScaleScreen
            End Get
            Set(value As String)
                value = AppSettingsService.NormalizeApplicationScaleScreen(value)
                If _applicationScaleScreen = value Then Return
                Me.RaiseAndSetIfChanged(_applicationScaleScreen, value)
                Me.RaisePropertyChanged(NameOf(IsApplicationScaleRestartRequired))
                Me.RaisePropertyChanged(NameOf(IsApplicationScaleScreenKnown))
                Me.RaisePropertyChanged(NameOf(ApplicationScaleScreenStatusText))
                SaveApplicationScaleSettings()
            End Set
        End Property

        Public ReadOnly Property ApplicationScaleScreens As ObservableCollection(Of String)
            Get
                Return _applicationScaleScreens
            End Get
        End Property

        ' Die manuelle Skalierungs-Einstellung wirkt nur auf Avalonias X11-Backend (siehe
        ' AppSettingsService.ApplyApplicationScaleEnvironment) und ist unter Windows wirkungslos,
        ' da Windows nativ pro Monitor skaliert.
        Public ReadOnly Property IsApplicationScaleSupported As Boolean
            Get
                Return Not OperatingSystem.IsWindows()
            End Get
        End Property

        Public ReadOnly Property ApplicationScaleText As String
            Get
                Return $"{CInt(Math.Round(_applicationScale * 100.0))}%"
            End Get
        End Property

        Public ReadOnly Property IsApplicationScaleScreenKnown As Boolean
            Get
                Return _applicationScaleScreens IsNot Nothing AndAlso
                       _applicationScaleScreens.Contains(_applicationScaleScreen)
            End Get
        End Property

        Public ReadOnly Property ApplicationScaleScreenStatusText As String
            Get
                If _applicationScaleScreens Is Nothing OrElse _applicationScaleScreens.Count = 0 Then
                    Return "Keine Bildschirme erkannt."
                End If

                If IsApplicationScaleScreenKnown Then
                    Return $"Verfügbar: {_applicationScaleScreen}"
                End If

                Return $"Der gespeicherte Bildschirm '{_applicationScaleScreen}' wurde nicht gefunden."
            End Get
        End Property

        Public ReadOnly Property IsApplicationScaleRestartRequired As Boolean
            Get
                Return Math.Abs(_applicationScale - _runningApplicationScale) > 0.001 OrElse
                       Not String.Equals(_applicationScaleScreen, _runningApplicationScaleScreen, StringComparison.Ordinal)
            End Get
        End Property

        Public ReadOnly Property IsLanguageSystem As Boolean
            Get
                Return _languageMode = "System"
            End Get
        End Property

        Public ReadOnly Property IsLanguageGerman As Boolean
            Get
                Return _languageMode = "German"
            End Get
        End Property

        Public ReadOnly Property IsLanguageEnglish As Boolean
            Get
                Return _languageMode = "English"
            End Get
        End Property

        Public ReadOnly Property IsLanguageSpanish As Boolean
            Get
                Return _languageMode = "Spanish"
            End Get
        End Property

        Public ReadOnly Property IsLanguageFrench As Boolean
            Get
                Return _languageMode = "French"
            End Get
        End Property

        Public ReadOnly Property IsLanguageItalian As Boolean
            Get
                Return _languageMode = "Italian"
            End Get
        End Property

        Public ReadOnly Property IsStartupGalleryMode As Boolean
            Get
                Return _startupImageMode = "Gallery"
            End Get
        End Property

        Public ReadOnly Property IsStartupViewerMode As Boolean
            Get
                Return _startupImageMode = "Viewer"
            End Get
        End Property

        Public ReadOnly Property IsStartupEditorMode As Boolean
            Get
                Return _startupImageMode = "Editor"
            End Get
        End Property

        Public ReadOnly Property IsStartupFullscreenMode As Boolean
            Get
                Return _startupImageMode = "Fullscreen"
            End Get
        End Property

        Public Property GalleryStartupFolderMode As String
            Get
                Return _galleryStartupFolderMode
            End Get
            Set(value As String)
                value = AppSettingsService.NormalizeGalleryStartupFolderMode(value)
                If _galleryStartupFolderMode = value Then Return
                Me.RaiseAndSetIfChanged(_galleryStartupFolderMode, value)
                RaiseGalleryStartupFolderModeProperties()
                SaveFileBrowserSettings()
            End Set
        End Property

        Public ReadOnly Property IsGalleryStartupPicturesFolder As Boolean
            Get
                Return _galleryStartupFolderMode = "Pictures"
            End Get
        End Property

        Public ReadOnly Property IsGalleryStartupLastFolder As Boolean
            Get
                Return _galleryStartupFolderMode = "Last"
            End Get
        End Property

        Public Property ShowHiddenFolders As Boolean
            Get
                Return _showHiddenFolders
            End Get
            Set(value As Boolean)
                If _showHiddenFolders = value Then Return
                Me.RaiseAndSetIfChanged(_showHiddenFolders, value)
                FolderNode.ShowHiddenFolders = value
                SaveFileBrowserSettings()
                _mainVm?.Gallery?.LoadCurrentFolder()
            End Set
        End Property

        Public Property GalleryShowFolders As Boolean
            Get
                Return _galleryShowFolders
            End Get
            Set(value As Boolean)
                If _galleryShowFolders = value Then Return
                Me.RaiseAndSetIfChanged(_galleryShowFolders, value)
                If _mainVm?.Gallery IsNot Nothing Then
                    _mainVm.Gallery.ShowFolders = value
                End If
                SaveFileBrowserSettings()
            End Set
        End Property

        Public Property GalleryShowParentFolder As Boolean
            Get
                Return _galleryShowParentFolder
            End Get
            Set(value As Boolean)
                If _galleryShowParentFolder = value Then Return
                Me.RaiseAndSetIfChanged(_galleryShowParentFolder, value)
                If _mainVm?.Gallery IsNot Nothing Then
                    _mainVm.Gallery.ShowParentFolder = value
                End If
                SaveFileBrowserSettings()
            End Set
        End Property

        Public Property GalleryViewMode As String
            Get
                Return _galleryViewMode
            End Get
            Set(value As String)
                value = AppSettingsService.NormalizeGalleryViewMode(value)
                If _galleryViewMode = value Then Return
                Me.RaiseAndSetIfChanged(_galleryViewMode, value)
                RaiseGalleryViewModeProperties()
                If _mainVm?.Gallery IsNot Nothing Then
                    _mainVm.Gallery.ViewMode = value
                End If
                SaveFileBrowserSettings()
            End Set
        End Property

        Public ReadOnly Property IsGalleryViewModeGrid As Boolean
            Get
                Return _galleryViewMode = "Grid"
            End Get
        End Property

        Public ReadOnly Property IsGalleryViewModeList As Boolean
            Get
                Return _galleryViewMode = "List"
            End Get
        End Property

        ' Called by GalleryViewModel when the view mode changes from the Gallery toolbar itself,
        ' so the Settings dialog reflects it without pushing the value back and forth.
        Public Sub SyncGalleryViewMode(value As String)
            value = AppSettingsService.NormalizeGalleryViewMode(value)
            If _galleryViewMode = value Then Return
            Me.RaiseAndSetIfChanged(_galleryViewMode, value)
            RaiseGalleryViewModeProperties()
        End Sub

        Private Sub RaiseGalleryViewModeProperties()
            Me.RaisePropertyChanged(NameOf(IsGalleryViewModeGrid))
            Me.RaisePropertyChanged(NameOf(IsGalleryViewModeList))
        End Sub

        Public Property ViewerShowFilmstrip As Boolean
            Get
                Return _viewerShowFilmstrip
            End Get
            Set(value As Boolean)
                If _viewerShowFilmstrip = value Then Return
                Me.RaiseAndSetIfChanged(_viewerShowFilmstrip, value)
                _mainVm?.RefreshLayoutBindings()
                SaveLayoutSettings()
            End Set
        End Property

        Public Property ViewerSlideshowIntervalSeconds As Integer
            Get
                Return _viewerSlideshowIntervalSeconds
            End Get
            Set(value As Integer)
                value = AppSettingsService.NormalizeViewerSlideshowIntervalSeconds(value)
                If _viewerSlideshowIntervalSeconds = value Then Return
                Me.RaiseAndSetIfChanged(_viewerSlideshowIntervalSeconds, value)
                SaveLayoutSettings()
            End Set
        End Property

        Public Property EditorShowFilmstrip As Boolean
            Get
                Return _editorShowFilmstrip
            End Get
            Set(value As Boolean)
                If _editorShowFilmstrip = value Then Return
                Me.RaiseAndSetIfChanged(_editorShowFilmstrip, value)
                _mainVm?.RefreshLayoutBindings()
                SaveLayoutSettings()
            End Set
        End Property

        Public Property EditorInfoSidebarExpanded As Boolean
            Get
                Return _editorInfoSidebarExpanded
            End Get
            Set(value As Boolean)
                If _editorInfoSidebarExpanded = value Then Return
                Me.RaiseAndSetIfChanged(_editorInfoSidebarExpanded, value)
                _mainVm?.RefreshLayoutBindings()
                SaveLayoutSettings()
            End Set
        End Property

        Public Property ViewerInfoSidebarExpanded As Boolean
            Get
                Return _viewerInfoSidebarExpanded
            End Get
            Set(value As Boolean)
                If _viewerInfoSidebarExpanded = value Then Return
                Me.RaiseAndSetIfChanged(_viewerInfoSidebarExpanded, value)
                _mainVm?.RefreshLayoutBindings()
                SaveLayoutSettings()
            End Set
        End Property

        ''' Steuert, ob LibVLC bei der Videowiedergabe Hardware-Decoder (VDPAU/VAAPI/DXVA2) oder
        ''' erzwungenes Software-Decoding verwendet - siehe ViewerViewModel.EnsureMediaPlayer.
        Public Property VideoHardwareAcceleration As Boolean
            Get
                Return _videoHardwareAcceleration
            End Get
            Set(value As Boolean)
                If _videoHardwareAcceleration = value Then Return
                Me.RaiseAndSetIfChanged(_videoHardwareAcceleration, value)
                SavePlaybackSettings()
            End Set
        End Property

        ''' Bestimmt, wie der Bereich hinter transparenten Bildbereichen in Viewer und Editor
        ''' dargestellt wird: "Checkerboard" (Standard) zeigt das übliche Schachbrettmuster,
        ''' "None" zeigt gar keinen Hintergrund (echt durchsichtig, kein Muster), "Solid" eine per
        ''' Farbpicker gewählte Volltonfarbe (TransparencyBackgroundColor).
        Public Property TransparencyBackgroundMode As String
            Get
                Return _transparencyBackgroundMode
            End Get
            Set(value As String)
                value = AppSettingsService.NormalizeTransparencyBackgroundMode(value)
                If _transparencyBackgroundMode = value Then Return
                Me.RaiseAndSetIfChanged(_transparencyBackgroundMode, value)
                Me.RaisePropertyChanged(NameOf(IsTransparencyCheckerboardMode))
                Me.RaisePropertyChanged(NameOf(IsTransparencyNoneMode))
                Me.RaisePropertyChanged(NameOf(IsTransparencySolidMode))
                SaveDisplaySettings()
            End Set
        End Property

        Public ReadOnly Property IsTransparencyCheckerboardMode As Boolean
            Get
                Return _transparencyBackgroundMode = "Checkerboard"
            End Get
        End Property

        Public ReadOnly Property IsTransparencyNoneMode As Boolean
            Get
                Return _transparencyBackgroundMode = "None"
            End Get
        End Property

        Public ReadOnly Property IsTransparencySolidMode As Boolean
            Get
                Return _transparencyBackgroundMode = "Solid"
            End Get
        End Property

        Public Property TransparencyBackgroundColor As String
            Get
                Return _transparencyBackgroundColor
            End Get
            Set(value As String)
                value = AppSettingsService.NormalizeHexColor(value, "#FFFFFFFF")
                If _transparencyBackgroundColor = value Then Return
                Me.RaiseAndSetIfChanged(_transparencyBackgroundColor, value)
                Me.RaisePropertyChanged(NameOf(TransparencyBackgroundColorValue))
                Me.RaisePropertyChanged(NameOf(TransparencyBackgroundColorBrush))
                SaveDisplaySettings()
            End Set
        End Property

        Public Property TransparencyBackgroundColorValue As Avalonia.Media.Color
            Get
                Try
                    Return Avalonia.Media.Color.Parse(_transparencyBackgroundColor)
                Catch
                    Return Avalonia.Media.Colors.White
                End Try
            End Get
            Set(value As Avalonia.Media.Color)
                TransparencyBackgroundColor = value.ToString()
            End Set
        End Property

        Public ReadOnly Property TransparencyBackgroundColorBrush As Avalonia.Media.IBrush
            Get
                Return New Avalonia.Media.SolidColorBrush(TransparencyBackgroundColorValue)
            End Get
        End Property

        Public Property CleanupResultMessage As String
            Get
                Return _cleanupResultMessage
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_cleanupResultMessage, value)
            End Set
        End Property

        Public Property ThumbnailCacheResultMessage As String
            Get
                Return _thumbnailCacheResultMessage
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_thumbnailCacheResultMessage, value)
            End Set
        End Property

        Public ReadOnly Property NavigateSectionCommand As ICommand
        Public ReadOnly Property ResetCommand As ICommand
        Public ReadOnly Property ApplyCommand As ICommand
        Public ReadOnly Property CancelCommand As ICommand
        Public ReadOnly Property SetDensityCommand As ICommand
        Public ReadOnly Property SetThemeModeCommand As ICommand
        Public ReadOnly Property SetAccentColorCommand As ICommand
        Public ReadOnly Property SetStartupImageModeCommand As ICommand
        Public ReadOnly Property SetGalleryViewModeCommand As ICommand
        Public ReadOnly Property SetGalleryStartupFolderModeCommand As ICommand
        Public ReadOnly Property SetLanguageModeCommand As ICommand
        Public ReadOnly Property SetTransparencyBackgroundModeCommand As ICommand
        Public ReadOnly Property CleanupDatabaseCommand As ICommand
        Public ReadOnly Property RefreshThumbnailCacheCommand As ICommand
        Public ReadOnly Property DeleteThumbnailCacheFolderCommand As ICommand
        Public ReadOnly Property DeleteAllThumbnailCacheCommand As ICommand
        Public Property ThumbnailCacheFolders As ObservableCollection(Of ThumbnailCacheFolderInfo)

        Public ReadOnly Property ThumbnailCacheSummaryText As String
            Get
                If ThumbnailCacheFolders Is Nothing OrElse ThumbnailCacheFolders.Count = 0 Then Return "Kein Vorschaubild-Cache vorhanden."
                Dim count = ThumbnailCacheFolders.Sum(Function(i) i.ThumbnailCount)
                Dim size = ThumbnailCacheFolders.Sum(Function(i) i.SizeBytes)
                Return $"{ThumbnailCacheFolders.Count:N0} Ordner · {count:N0} Bilder · {FormatBytes(size)}"
            End Get
        End Property

        Public Sub New(mainVm As MainWindowViewModel)
            _mainVm = mainVm
            _appSettings = AppSettingsService.Load()
            ThumbnailCacheFolders = New ObservableCollection(Of ThumbnailCacheFolderInfo)()
            _themeMode = _appSettings.ThemeMode
            _accentColor = _appSettings.AccentColor
            _startupImageMode = _appSettings.StartupImageMode
            _languageMode = _appSettings.LanguageMode
            _applicationScale = _appSettings.ApplicationScale
            _applicationScaleScreen = _appSettings.ApplicationScaleScreen
            ParseRunningApplicationScale(_runningApplicationScale, _runningApplicationScaleScreen)
            _thumbnailCacheEnabled = _appSettings.ThumbnailCacheEnabled
            _thumbnailQuality = _appSettings.ThumbnailQuality
            _jpgSaveQuality = _appSettings.JpgSaveQuality
            _showHiddenFolders = _appSettings.ShowHiddenFolders
            _galleryShowFolders = _appSettings.GalleryShowFolders
            _galleryShowParentFolder = _appSettings.GalleryShowParentFolder
            _galleryViewMode = AppSettingsService.NormalizeGalleryViewMode(_appSettings.GalleryViewMode)
            _galleryStartupFolderMode = _appSettings.GalleryStartupFolderMode
            _viewerShowFilmstrip = _appSettings.ViewerShowFilmstrip
            _viewerSlideshowIntervalSeconds = _appSettings.ViewerSlideshowIntervalSeconds
            _editorShowFilmstrip = _appSettings.EditorShowFilmstrip
            _editorInfoSidebarExpanded = _appSettings.EditorInfoSidebarExpanded
            _viewerInfoSidebarExpanded = _appSettings.ViewerInfoSidebarExpanded
            _videoHardwareAcceleration = _appSettings.VideoHardwareAcceleration
            _transparencyBackgroundMode = AppSettingsService.NormalizeTransparencyBackgroundMode(_appSettings.TransparencyBackgroundMode)
            _transparencyBackgroundColor = AppSettingsService.NormalizeHexColor(_appSettings.TransparencyBackgroundColor, "#FFFFFFFF")
            FolderNode.ShowHiddenFolders = _showHiddenFolders
            NavigateSectionCommand = ReactiveCommand.Create(Of String)(Sub(s)
                                                                           Dim parsed As SettingsSection
                                                                           If [Enum].TryParse(s, parsed) Then
                                                                               SelectedSection = parsed
                                                                           End If
                                                                       End Sub)
            ResetCommand = ReactiveCommand.Create(Sub() ResetToDefaults())
            ApplyCommand = ReactiveCommand.Create(Sub()
                                                     SnapshotSettings()
                                                     _mainVm?.CloseSettings()
                                                 End Sub)
            CancelCommand = ReactiveCommand.Create(Sub()
                                                       RestoreSnapshot()
                                                       _mainVm?.CloseSettings()
                                                   End Sub)
            SetThemeModeCommand = ReactiveCommand.Create(Of String)(Sub(m) ThemeMode = m)
            SetAccentColorCommand = ReactiveCommand.Create(Of String)(Sub(c) AccentColor = c)
            SetStartupImageModeCommand = ReactiveCommand.Create(Of String)(Sub(m) StartupImageMode = m)
            SetGalleryViewModeCommand = ReactiveCommand.Create(Of String)(Sub(m) GalleryViewMode = m)
            SetGalleryStartupFolderModeCommand = ReactiveCommand.Create(Of String)(Sub(m) GalleryStartupFolderMode = m)
            SetLanguageModeCommand = ReactiveCommand.Create(Of String)(Sub(m) LanguageMode = m)
            SetTransparencyBackgroundModeCommand = ReactiveCommand.Create(Of String)(Sub(m) TransparencyBackgroundMode = m)
            CleanupDatabaseCommand = ReactiveCommand.Create(Sub()
                                                                Dim removed = Services.LibraryService.Instance.PurgeOrphanedRecords()
                                                                CleanupResultMessage = If(removed = 0,
                                                                    "Keine verwaisten Einträge gefunden.",
                                                                    $"{removed} verwaiste Einträge entfernt.")
                                                            End Sub)
            RefreshThumbnailCacheCommand = ReactiveCommand.Create(Sub() RefreshThumbnailCacheFolders())
            DeleteThumbnailCacheFolderCommand = ReactiveCommand.Create(Of ThumbnailCacheFolderInfo)(Sub(item)
                                                                                                       If item Is Nothing Then Return
                                                                                                       Dim removed = Services.ThumbnailCacheService.DeleteFolderCacheById(item.CacheId)
                                                                                                       ThumbnailCacheResultMessage = If(removed = 0,
                                                                                                           "Kein Cache für diesen Ordner gefunden.",
                                                                                                           $"{removed} Vorschaubilder aus dem Cache entfernt.")
                                                                                                       RefreshThumbnailCacheFolders()
                                                                                                       _mainVm?.Gallery?.LoadCurrentFolder()
                                                                                                   End Sub)
            DeleteAllThumbnailCacheCommand = ReactiveCommand.Create(Sub()
                                                                        Dim removed = Services.ThumbnailCacheService.DeleteAllCaches()
                                                                        ThumbnailCacheResultMessage = If(removed = 0,
                                                                            "Kein Cache vorhanden.",
                                                                            $"{removed} Vorschaubilder gelöscht.")
                                                                        RefreshThumbnailCacheFolders()
                                                                        _mainVm?.Gallery?.LoadCurrentFolder()
                                                                    End Sub)
            ApplyTheme(_themeMode, _accentColor)
            LocalizationService.LanguageMode = _languageMode
            SnapshotSettings()
        End Sub

        Public Sub New()
            Me.New(Nothing)
        End Sub

        Private Sub SnapshotSettings()
            _savedThemeMode = _themeMode
            _savedAccentColor = _accentColor
            _savedBreadcrumbs = _showBreadcrumbs
            _savedViewerOpenFitToWindow = _viewerOpenFitToWindow
            _savedThumbnailQuality = _thumbnailQuality
            _savedJpgSaveQuality = _jpgSaveQuality
            _savedThumbnailCacheEnabled = _thumbnailCacheEnabled
            _savedShowHiddenFolders = _showHiddenFolders
            _savedGalleryShowFolders = _galleryShowFolders
            _savedGalleryShowParentFolder = _galleryShowParentFolder
            _savedGalleryViewMode = _galleryViewMode
            _savedGalleryStartupFolderMode = _galleryStartupFolderMode
            _savedViewerShowFilmstrip = _viewerShowFilmstrip
            _savedViewerSlideshowIntervalSeconds = _viewerSlideshowIntervalSeconds
            _savedEditorShowFilmstrip = _editorShowFilmstrip
            _savedEditorInfoSidebarExpanded = _editorInfoSidebarExpanded
            _savedViewerInfoSidebarExpanded = _viewerInfoSidebarExpanded
            _savedStartupImageMode = _startupImageMode
            _savedLanguageMode = _languageMode
            _savedVideoHardwareAcceleration = _videoHardwareAcceleration
            _savedTransparencyBackgroundMode = _transparencyBackgroundMode
            _savedTransparencyBackgroundColor = _transparencyBackgroundColor
            _savedApplicationScale = _applicationScale
            _savedApplicationScaleScreen = _applicationScaleScreen
        End Sub

        Private Sub RestoreSnapshot()
            ThemeMode = _savedThemeMode
            AccentColor = _savedAccentColor
            ShowBreadcrumbs = _savedBreadcrumbs
            ViewerOpenFitToWindow = _savedViewerOpenFitToWindow
            ThumbnailQuality = _savedThumbnailQuality
            JpgSaveQuality = _savedJpgSaveQuality
            ThumbnailCacheEnabled = _savedThumbnailCacheEnabled
            ShowHiddenFolders = _savedShowHiddenFolders
            GalleryShowFolders = _savedGalleryShowFolders
            GalleryShowParentFolder = _savedGalleryShowParentFolder
            GalleryViewMode = _savedGalleryViewMode
            GalleryStartupFolderMode = _savedGalleryStartupFolderMode
            ViewerShowFilmstrip = _savedViewerShowFilmstrip
            ViewerSlideshowIntervalSeconds = _savedViewerSlideshowIntervalSeconds
            EditorShowFilmstrip = _savedEditorShowFilmstrip
            EditorInfoSidebarExpanded = _savedEditorInfoSidebarExpanded
            ViewerInfoSidebarExpanded = _savedViewerInfoSidebarExpanded
            StartupImageMode = _savedStartupImageMode
            LanguageMode = _savedLanguageMode
            VideoHardwareAcceleration = _savedVideoHardwareAcceleration
            TransparencyBackgroundMode = _savedTransparencyBackgroundMode
            TransparencyBackgroundColor = _savedTransparencyBackgroundColor
            ApplicationScalePercent = _savedApplicationScale * 100.0
            ApplicationScaleScreen = _savedApplicationScaleScreen
        End Sub

        Public Sub RefreshApplicationScaleScreens(screenNames As IEnumerable(Of String))
            _applicationScaleScreens.Clear()
            If screenNames IsNot Nothing Then
                For Each screenName In screenNames.
                    Where(Function(s) Not String.IsNullOrWhiteSpace(s)).
                    Distinct(StringComparer.OrdinalIgnoreCase).
                    OrderBy(Function(s) s, StringComparer.OrdinalIgnoreCase)
                    _applicationScaleScreens.Add(screenName)
                Next
            End If

            If Not String.IsNullOrWhiteSpace(_applicationScaleScreen) AndAlso
               Not _applicationScaleScreens.Contains(_applicationScaleScreen) Then
                _applicationScaleScreens.Insert(0, _applicationScaleScreen)
            End If

            Me.RaisePropertyChanged(NameOf(IsApplicationScaleScreenKnown))
            Me.RaisePropertyChanged(NameOf(ApplicationScaleScreenStatusText))
        End Sub

        Private Sub ResetToDefaults()
            ThemeMode = "Dark"
            AccentColor = "#F08A1A"
            ShowBreadcrumbs = True
            ViewerOpenFitToWindow = True
            StartupImageMode = "Viewer"
            ThumbnailCacheEnabled = True
            ThumbnailQuality = 82
            JpgSaveQuality = 90
            CacheSizeMb = 512
            ShowHiddenFolders = False
            GalleryShowFolders = True
            GalleryShowParentFolder = True
            GalleryViewMode = "Grid"
            GalleryStartupFolderMode = "Pictures"
            ViewerShowFilmstrip = True
            ViewerSlideshowIntervalSeconds = 3
            EditorShowFilmstrip = True
            EditorInfoSidebarExpanded = True
            ViewerInfoSidebarExpanded = True
            LanguageMode = "System"
            VideoHardwareAcceleration = False
            TransparencyBackgroundMode = "Checkerboard"
            TransparencyBackgroundColor = "#FFFFFFFF"
            ApplicationScalePercent = 100.0
            ApplicationScaleScreen = "HDMI-A-1"
        End Sub

        Private Sub SaveAppearanceSettings()
            Dim settings = AppSettingsService.Load()
            settings.ThemeMode = _themeMode
            settings.AccentColor = _accentColor
            AppSettingsService.Save(settings)
        End Sub

        Private Sub SaveLanguageSettings()
            Dim settings = AppSettingsService.Load()
            settings.LanguageMode = _languageMode
            AppSettingsService.Save(settings)
        End Sub

        Private Sub SavePlaybackSettings()
            Dim settings = AppSettingsService.Load()
            settings.VideoHardwareAcceleration = _videoHardwareAcceleration
            AppSettingsService.Save(settings)
        End Sub

        Private Sub SaveDisplaySettings()
            Dim settings = AppSettingsService.Load()
            settings.TransparencyBackgroundMode = _transparencyBackgroundMode
            settings.TransparencyBackgroundColor = _transparencyBackgroundColor
            AppSettingsService.Save(settings)
            _mainVm?.RefreshDisplayBindings()
        End Sub

        Private Sub SaveApplicationScaleSettings()
            Dim settings = AppSettingsService.Load()
            settings.ApplicationScale = _applicationScale
            settings.ApplicationScaleScreen = _applicationScaleScreen
            AppSettingsService.Save(settings)
        End Sub

        Private Sub SaveStartupSettings()
            Dim settings = AppSettingsService.Load()
            settings.StartupImageMode = _startupImageMode
            AppSettingsService.Save(settings)
        End Sub

        Private Sub SaveFileBrowserSettings()
            Dim settings = AppSettingsService.Load()
            settings.ShowHiddenFolders = _showHiddenFolders
            settings.GalleryShowFolders = _galleryShowFolders
            settings.GalleryShowParentFolder = _galleryShowParentFolder
            settings.GalleryViewMode = _galleryViewMode
            settings.GalleryStartupFolderMode = _galleryStartupFolderMode
            AppSettingsService.Save(settings)
        End Sub

        Private Sub SaveLayoutSettings()
            Dim settings = AppSettingsService.Load()
            settings.ViewerShowFilmstrip = _viewerShowFilmstrip
            settings.ViewerSlideshowIntervalSeconds = _viewerSlideshowIntervalSeconds
            settings.EditorShowFilmstrip = _editorShowFilmstrip
            settings.EditorInfoSidebarExpanded = _editorInfoSidebarExpanded
            settings.ViewerInfoSidebarExpanded = _viewerInfoSidebarExpanded
            AppSettingsService.Save(settings)
        End Sub

        Private Sub SavePerformanceSettings()
            Dim settings = AppSettingsService.Load()
            settings.ThumbnailCacheEnabled = _thumbnailCacheEnabled
            settings.ThumbnailQuality = _thumbnailQuality
            settings.JpgSaveQuality = _jpgSaveQuality
            AppSettingsService.Save(settings)
        End Sub

        Public Sub RefreshThumbnailCacheFolders()
            ThumbnailCacheFolders.Clear()
            For Each item In Services.ThumbnailCacheService.GetFolderCaches()
                ThumbnailCacheFolders.Add(item)
            Next
            Me.RaisePropertyChanged(NameOf(ThumbnailCacheSummaryText))
        End Sub

        Private Shared Function FormatBytes(bytes As Long) As String
            If bytes < 1024 Then Return $"{bytes:N0} B"
            Dim kb = bytes / 1024.0
            If kb < 1024 Then Return $"{kb:N1} KB"
            Dim mb = kb / 1024.0
            If mb < 1024 Then Return $"{mb:N1} MB"
            Return $"{mb / 1024.0:N1} GB"
        End Function

        Private Shared Sub ParseRunningApplicationScale(ByRef scale As Double, ByRef screen As String)
            scale = 1.0
            screen = "HDMI-A-1"

            Dim value = Environment.GetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS")
            If String.IsNullOrWhiteSpace(value) Then Return

            Dim firstEntry = value.Split(";"c).FirstOrDefault()
            If String.IsNullOrWhiteSpace(firstEntry) Then Return

            Dim parts = firstEntry.Split({"="c}, 2)
            If parts.Length <> 2 Then Return

            screen = AppSettingsService.NormalizeApplicationScaleScreen(parts(0))
            Dim parsed As Double
            If Double.TryParse(parts(1), NumberStyles.Float, CultureInfo.InvariantCulture, parsed) Then
                scale = AppSettingsService.NormalizeApplicationScale(parsed)
            End If
        End Sub

        Private Sub RaiseStartupImageModeProperties()
            Me.RaisePropertyChanged(NameOf(IsStartupGalleryMode))
            Me.RaisePropertyChanged(NameOf(IsStartupViewerMode))
            Me.RaisePropertyChanged(NameOf(IsStartupEditorMode))
            Me.RaisePropertyChanged(NameOf(IsStartupFullscreenMode))
        End Sub

        Private Sub RaiseGalleryStartupFolderModeProperties()
            Me.RaisePropertyChanged(NameOf(IsGalleryStartupPicturesFolder))
            Me.RaisePropertyChanged(NameOf(IsGalleryStartupLastFolder))
        End Sub

        Private Sub RaiseThemeModeProperties()
            Me.RaisePropertyChanged(NameOf(IsDarkThemeMode))
            Me.RaisePropertyChanged(NameOf(IsLightThemeMode))
            Me.RaisePropertyChanged(NameOf(IsGrayDarkThemeMode))
            Me.RaisePropertyChanged(NameOf(IsGrayLightThemeMode))
            Me.RaisePropertyChanged(NameOf(IsLightOrGrayLightThemeMode))
            Me.RaisePropertyChanged(NameOf(IsDarkOrGrayDarkThemeMode))
        End Sub

        Private Sub RaiseAccentProperties()
            Me.RaisePropertyChanged(NameOf(IsOrangeAccent))
            Me.RaisePropertyChanged(NameOf(IsRedAccent))
            Me.RaisePropertyChanged(NameOf(IsPinkAccent))
            Me.RaisePropertyChanged(NameOf(IsPurpleAccent))
            Me.RaisePropertyChanged(NameOf(IsBlueAccent))
            Me.RaisePropertyChanged(NameOf(IsCyanAccent))
            Me.RaisePropertyChanged(NameOf(IsTealAccent))
            Me.RaisePropertyChanged(NameOf(IsGreenAccent))
            Me.RaisePropertyChanged(NameOf(IsYellowAccent))
        End Sub

        Private Sub RaiseLanguageModeProperties()
            Me.RaisePropertyChanged(NameOf(IsLanguageSystem))
            Me.RaisePropertyChanged(NameOf(IsLanguageGerman))
            Me.RaisePropertyChanged(NameOf(IsLanguageEnglish))
            Me.RaisePropertyChanged(NameOf(IsLanguageSpanish))
            Me.RaisePropertyChanged(NameOf(IsLanguageFrench))
            Me.RaisePropertyChanged(NameOf(IsLanguageItalian))
        End Sub

        Public Sub RefreshLocalization()
            RaiseLanguageModeProperties()
        End Sub

        Private Shared Sub ApplyTheme(themeMode As String, accentColor As String)
            Dim app = Application.Current
            If app Is Nothing Then Return

            themeMode = AppSettingsService.NormalizeThemeMode(themeMode)
            accentColor = AppSettingsService.NormalizeAccentColor(accentColor)

            Select Case themeMode
                Case "Light"
                    app.RequestedThemeVariant = ThemeVariant.Light
                Case Else
                    app.RequestedThemeVariant = ThemeVariant.Dark
            End Select

            Dim isDark = themeMode <> "Light"

            If themeMode = "GrayDark" Then
                SetBrush("FP.Bg.Root", "#1B1F20")
                SetBrush("FP.Bg.Dark", "#1E2021")
                SetBrush("FP.Bg.Panel", "#232628")
                SetBrush("FP.Bg.Content", "#202426")
                SetBrush("FP.Bg.Elevated", "#2C3030")
                SetBrush("FP.Bg.Hover", "#323536")
                SetBrush("FP.Bg.Input", "#1F2122")
                SetBrush("FP.Bg.Active", "#323536")
                SetBrush("FP.Text.Primary", "#F1F2F2")
                SetBrush("FP.Text.Secondary", "#D0D3D4")
                SetBrush("FP.Text.Muted", "#9A9FA1")
                SetBrush("FP.Text.Accent", accentColor)
                SetBrush("FP.Border.Subtle", "#2A2D2F")
                SetBrush("FP.Border.Normal", "#3A3E40")
                SetBrush("FP.Border.Strong", "#555A5C")
                SetBrush("FP.Sel.Bg", "#323536")
                SetBrush("FP.Sel.Hover", "#3A3E40")
            ElseIf themeMode = "GrayLight" Then
                SetBrush("FP.Bg.Root", "#33383B")
                SetBrush("FP.Bg.Dark", "#1E2021")
                SetBrush("FP.Bg.Panel", "#4A5057")
                SetBrush("FP.Bg.Content", "#464B50")
                SetBrush("FP.Bg.Elevated", "#404649")
                SetBrush("FP.Bg.Hover", "#5A6065")
                SetBrush("FP.Bg.Input", "#3E4348")
                SetBrush("FP.Bg.Active", "#5A6065")
                SetBrush("FP.Text.Primary", "#F1F2F2")
                SetBrush("FP.Text.Secondary", "#D7D9DB")
                SetBrush("FP.Text.Muted", "#C7CACA")
                SetBrush("FP.Text.Accent", accentColor)
                SetBrush("FP.Border.Subtle", "#4E545A")
                SetBrush("FP.Border.Normal", "#5A5F63")
                SetBrush("FP.Border.Strong", "#747A7F")
                SetBrush("FP.Sel.Bg", "#5A6065")
                SetBrush("FP.Sel.Hover", "#62686D")
            ElseIf isDark Then
                SetBrush("FP.Bg.Root", "#0B0E11")
                SetBrush("FP.Bg.Dark", "#0E1216")
                SetBrush("FP.Bg.Panel", "#11161B")
                SetBrush("FP.Bg.Content", "#141A20")
                SetBrush("FP.Bg.Elevated", "#192128")
                SetBrush("FP.Bg.Hover", "#1F2830")
                SetBrush("FP.Bg.Input", "#10161B")
                SetBrush("FP.Bg.Active", "#24303A")
                SetBrush("FP.Text.Primary", "#E7ECF0")
                SetBrush("FP.Text.Secondary", "#A6AFB7")
                SetBrush("FP.Text.Muted", "#6C7780")
                SetBrush("FP.Text.Accent", accentColor)
                SetBrush("FP.Border.Subtle", "#182028")
                SetBrush("FP.Border.Normal", "#26313B")
                SetBrush("FP.Border.Strong", "#34414C")
                SetBrush("FP.Sel.Bg", "#24303A")
                SetBrush("FP.Sel.Hover", "#2A3742")
            Else
                SetBrush("FP.Bg.Root", "#FCFCFC")
                SetBrush("FP.Bg.Dark", "#F7F7F8")
                SetBrush("FP.Bg.Panel", "#FAFAFA")
                SetBrush("FP.Bg.Content", "#FFFFFF")
                SetBrush("FP.Bg.Elevated", "#F6F6F7")
                SetBrush("FP.Bg.Hover", "#F0F1F3")
                SetBrush("FP.Bg.Input", "#FFFFFF")
                SetBrush("FP.Bg.Active", "#E8EDF2")
                SetBrush("FP.Text.Primary", "#111827")
                SetBrush("FP.Text.Secondary", "#2F3A48")
                SetBrush("FP.Text.Muted", "#5F6B7A")
                SetBrush("FP.Text.Accent", accentColor)
                SetBrush("FP.Border.Subtle", "#EBEBED")
                SetBrush("FP.Border.Normal", "#DADDE2")
                SetBrush("FP.Border.Strong", "#A8B0BB")
                SetBrush("FP.Sel.Bg", "#E8EDF2")
                SetBrush("FP.Sel.Hover", "#DCE4EC")
            End If

            SetAccentBrushes(accentColor, isDark)
        End Sub

        Private Shared Sub SetBrush(key As String, hexColor As String)
            Dim app = Application.Current
            If app Is Nothing Then Return
            app.Resources(key) = New SolidColorBrush(Color.Parse(hexColor))
        End Sub

        Private Shared Sub SetAccentBrushes(accentColor As String, isDark As Boolean)
            Dim baseColor As Color = Color.Parse(accentColor)
            SetBrush("FP.Accent", accentColor)
            SetBrush("FP.Accent.Light", ToHex(Mix(baseColor, Colors.White, If(isDark, 0.18, 0.12))))
            SetBrush("FP.Accent.Dark", ToHex(Mix(baseColor, Colors.Black, If(isDark, 0.24, 0.18))))
            SetBrush("FP.Accent.Dim", ToHex(Mix(baseColor, If(isDark, Color.Parse("#0B0E11"), Color.Parse("#E9EDF2")), If(isDark, 0.78, 0.82))))
        End Sub

        Private Shared Function Mix(color As Color, target As Color, amount As Double) As Color
            amount = Math.Max(0, Math.Min(1, amount))
            Return Color.FromArgb(
                color.A,
                MixChannel(color.R, target.R, amount),
                MixChannel(color.G, target.G, amount),
                MixChannel(color.B, target.B, amount))
        End Function

        Private Shared Function MixChannel(value As Byte, target As Byte, amount As Double) As Byte
            Dim mixed = CDbl(value) + (CDbl(target) - CDbl(value)) * amount
            mixed = Math.Max(0, Math.Min(255, mixed))
            Return CByte(Math.Round(mixed))
        End Function

        Private Shared Function ToHex(color As Color) As String
            Return $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        End Function
    End Class

    Public Enum SettingsSection
        Appearance
        Display
        Thumbnails
        Viewer
        Editor
        FileAssociations
        Performance
        Advanced
        About
    End Enum

End Namespace
