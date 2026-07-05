Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text.Json

Namespace Services

    Public Class SavedSearchSettings
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        Public Property TextQuery As String = ""
        Public Property RootFolder As String = ""
        Public Property IncludeSubfolders As Boolean = True
        Public Property FavoriteMode As String = "Any"
        Public Property RatingMin As Integer = -1
    End Class

    Public Class AppSettings
        Public Property GalleryThumbnailSize As Double = 260
        Public Property GalleryViewMode As String = "Grid"
        Public Property GallerySortMode As String = "Name"
        Public Property GallerySortAscending As Boolean = True
        Public Property GalleryShowFolders As Boolean = True
        Public Property GalleryShowParentFolder As Boolean = True
        Public Property GalleryFilterFavorite As String = "All"
        Public Property GalleryFilterRatings As New List(Of Integer)()
        Public Property GalleryFilterFileType As String = "All"
        Public Property GalleryStartupFolderMode As String = "Pictures"
        Public Property LastGalleryFolder As String = ""
        Public Property ViewerShowFilmstrip As Boolean = True
        Public Property ViewerSlideshowIntervalSeconds As Integer = 3
        Public Property ViewerOpenFitToWindow As Boolean = True
        ''' "Always" (immer einpassen, auch kleinere Bilder hochskalieren) oder "OnlyWhenLarger"
        ''' (nur einpassen, wenn das Bild größer als die Darstellungsfläche ist, sonst 100%).
        Public Property ViewerFitBehavior As String = "Always"
        Public Property EditorShowFilmstrip As Boolean = True
        Public Property ShowHiddenFolders As Boolean = False
        Public Property ThemeMode As String = "Dark"
        Public Property AccentColor As String = "#F08A1A"
        Public Property StartupImageMode As String = "Viewer"
        Public Property LanguageMode As String = "System"
        Public Property ThumbnailCacheEnabled As Boolean = True
        Public Property ThumbnailQuality As Integer = 82
        Public Property GalleryThumbnailMemoryCacheCapacity As Integer = 250
        Public Property JpgSaveQuality As Integer = 90
        Public Property EditorInfoSidebarExpanded As Boolean = True
        Public Property ViewerInfoSidebarExpanded As Boolean = True
        Public Property ApplicationScale As Double = 1.0
        Public Property ApplicationScaleScreen As String = "HDMI-A-1"
        Public Property MainWindowLeft As Integer = -1
        Public Property MainWindowTop As Integer = -1
        Public Property MainWindowWidth As Double = 1536
        Public Property MainWindowHeight As Double = 1024
        Public Property SavedSearches As New List(Of SavedSearchSettings)()
        Public Property VideoHardwareAcceleration As Boolean = False
        Public Property TransparencyBackgroundMode As String = "Checkerboard"
        Public Property TransparencyBackgroundColor As String = "#FFFFFFFF"
        Public Property LastBatchRenamePattern As String = "{name}_###"
        Public Property EnableDiagnosticLogging As Boolean = False
    End Class

    Public NotInheritable Class AppSettingsService
        Private Sub New()
        End Sub

        Private Shared ReadOnly SettingsDirectory As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FerrumPix")

        Private Shared ReadOnly SettingsPath As String =
            Path.Combine(SettingsDirectory, "settings.json")

        Public Shared Function Load() As AppSettings
            Try
                If Not File.Exists(SettingsPath) Then Return New AppSettings()

                Dim json = File.ReadAllText(SettingsPath)
                Dim settings = JsonSerializer.Deserialize(Of AppSettings)(json)
                If settings Is Nothing Then Return New AppSettings()

                settings.GalleryThumbnailSize = NormalizeThumbnailSize(settings.GalleryThumbnailSize)
                settings.GalleryViewMode = NormalizeGalleryViewMode(settings.GalleryViewMode)
                settings.GallerySortMode = NormalizeGallerySortMode(settings.GallerySortMode)
                settings.GalleryStartupFolderMode = NormalizeGalleryStartupFolderMode(settings.GalleryStartupFolderMode)
                settings.LastGalleryFolder = NormalizeFolderPath(settings.LastGalleryFolder)
                settings.GalleryFilterFavorite = NormalizeGalleryFilterFavorite(settings.GalleryFilterFavorite)
                settings.GalleryFilterRatings = NormalizeGalleryFilterRatings(settings.GalleryFilterRatings)
                settings.GalleryFilterFileType = NormalizeGalleryFilterFileType(settings.GalleryFilterFileType)
                settings.ThemeMode = NormalizeThemeMode(settings.ThemeMode)
                settings.AccentColor = NormalizeAccentColor(settings.AccentColor)
                settings.StartupImageMode = NormalizeStartupImageMode(settings.StartupImageMode)
                settings.LanguageMode = LocalizationService.NormalizeLanguageMode(settings.LanguageMode)
                settings.ThumbnailQuality = NormalizeThumbnailQuality(settings.ThumbnailQuality)
                settings.GalleryThumbnailMemoryCacheCapacity = NormalizeGalleryThumbnailMemoryCacheCapacity(settings.GalleryThumbnailMemoryCacheCapacity)
                settings.JpgSaveQuality = NormalizeJpgSaveQuality(settings.JpgSaveQuality)
                settings.ViewerSlideshowIntervalSeconds = NormalizeViewerSlideshowIntervalSeconds(settings.ViewerSlideshowIntervalSeconds)
                settings.ViewerFitBehavior = NormalizeViewerFitBehavior(settings.ViewerFitBehavior)
                settings.MainWindowWidth = NormalizeWindowDimension(settings.MainWindowWidth, 1536)
                settings.MainWindowHeight = NormalizeWindowDimension(settings.MainWindowHeight, 1024)
                settings.ApplicationScale = NormalizeApplicationScale(settings.ApplicationScale)
                settings.ApplicationScaleScreen = NormalizeApplicationScaleScreen(settings.ApplicationScaleScreen)
                settings.SavedSearches = NormalizeSavedSearches(settings.SavedSearches)
                settings.TransparencyBackgroundMode = NormalizeTransparencyBackgroundMode(settings.TransparencyBackgroundMode)
                settings.TransparencyBackgroundColor = NormalizeHexColor(settings.TransparencyBackgroundColor, "#FFFFFFFF")
                Return settings
            Catch
                Return New AppSettings()
            End Try
        End Function

        Public Shared Sub Save(settings As AppSettings)
            Try
                Directory.CreateDirectory(SettingsDirectory)
                settings.GalleryThumbnailSize = NormalizeThumbnailSize(settings.GalleryThumbnailSize)
                settings.GalleryViewMode = NormalizeGalleryViewMode(settings.GalleryViewMode)
                settings.GallerySortMode = NormalizeGallerySortMode(settings.GallerySortMode)
                settings.GalleryStartupFolderMode = NormalizeGalleryStartupFolderMode(settings.GalleryStartupFolderMode)
                settings.LastGalleryFolder = NormalizeFolderPath(settings.LastGalleryFolder)
                settings.GalleryFilterFavorite = NormalizeGalleryFilterFavorite(settings.GalleryFilterFavorite)
                settings.GalleryFilterRatings = NormalizeGalleryFilterRatings(settings.GalleryFilterRatings)
                settings.GalleryFilterFileType = NormalizeGalleryFilterFileType(settings.GalleryFilterFileType)
                settings.ThemeMode = NormalizeThemeMode(settings.ThemeMode)
                settings.AccentColor = NormalizeAccentColor(settings.AccentColor)
                settings.StartupImageMode = NormalizeStartupImageMode(settings.StartupImageMode)
                settings.LanguageMode = LocalizationService.NormalizeLanguageMode(settings.LanguageMode)
                settings.ThumbnailQuality = NormalizeThumbnailQuality(settings.ThumbnailQuality)
                settings.GalleryThumbnailMemoryCacheCapacity = NormalizeGalleryThumbnailMemoryCacheCapacity(settings.GalleryThumbnailMemoryCacheCapacity)
                settings.JpgSaveQuality = NormalizeJpgSaveQuality(settings.JpgSaveQuality)
                settings.ViewerSlideshowIntervalSeconds = NormalizeViewerSlideshowIntervalSeconds(settings.ViewerSlideshowIntervalSeconds)
                settings.ViewerFitBehavior = NormalizeViewerFitBehavior(settings.ViewerFitBehavior)
                settings.MainWindowWidth = NormalizeWindowDimension(settings.MainWindowWidth, 1536)
                settings.MainWindowHeight = NormalizeWindowDimension(settings.MainWindowHeight, 1024)
                settings.ApplicationScale = NormalizeApplicationScale(settings.ApplicationScale)
                settings.ApplicationScaleScreen = NormalizeApplicationScaleScreen(settings.ApplicationScaleScreen)
                settings.SavedSearches = NormalizeSavedSearches(settings.SavedSearches)
                settings.TransparencyBackgroundMode = NormalizeTransparencyBackgroundMode(settings.TransparencyBackgroundMode)
                settings.TransparencyBackgroundColor = NormalizeHexColor(settings.TransparencyBackgroundColor, "#FFFFFFFF")
                Dim json = JsonSerializer.Serialize(settings, New JsonSerializerOptions With {.WriteIndented = True})
                File.WriteAllText(SettingsPath, json)
                ThumbnailCacheService.InvalidateSettingsCache()
            Catch
            End Try
        End Sub

        Public Shared Function NormalizeThumbnailSize(value As Double) As Double
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return 260
            Return Math.Max(140, Math.Min(480, value))
        End Function

        Public Shared Function NormalizeThumbnailQuality(value As Integer) As Integer
            Return Math.Max(45, Math.Min(95, value))
        End Function

        Public Shared Function NormalizeGalleryThumbnailMemoryCacheCapacity(value As Integer) As Integer
            Return Math.Max(50, Math.Min(5000, value))
        End Function

        Public Shared Function NormalizeJpgSaveQuality(value As Integer) As Integer
            Return Math.Max(1, Math.Min(100, value))
        End Function

        Public Shared Function NormalizeViewerSlideshowIntervalSeconds(value As Integer) As Integer
            Return Math.Max(1, Math.Min(30, value))
        End Function

        Public Shared Function NormalizeViewerFitBehavior(value As String) As String
            Select Case If(value, "").Trim()
                Case "OnlyWhenLarger"
                    Return "OnlyWhenLarger"
                Case Else
                    Return "Always"
            End Select
        End Function

        Public Shared Function NormalizeApplicationScale(value As Double) As Double
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return 1.0
            Return Math.Max(1.0, Math.Min(2.5, Math.Round(value, 2)))
        End Function

        Public Shared Function NormalizeApplicationScaleScreen(value As String) As String
            value = If(value, "").Trim()
            If String.IsNullOrWhiteSpace(value) Then Return "HDMI-A-1"
            Return value.Replace(";"c, "_"c).Replace("="c, "_"c)
        End Function

        Public Shared Sub ApplyApplicationScaleEnvironment()
            ' AVALONIA_SCREEN_SCALE_FACTORS wirkt nur auf Avalonias X11-Backend. Unter Windows
            ' skaliert Avalonia bereits nativ pro Monitor über DWM, die Einstellung wäre dort wirkungslos.
            If OperatingSystem.IsWindows() Then Return

            Dim settings = Load()
            Dim scale = NormalizeApplicationScale(settings.ApplicationScale)
            If scale <= 1.0001 Then
                Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", Nothing)
                Return
            End If

            Dim screen = NormalizeApplicationScaleScreen(settings.ApplicationScaleScreen)
            Dim scaleText = scale.ToString("0.##", CultureInfo.InvariantCulture)
            Environment.SetEnvironmentVariable("AVALONIA_SCREEN_SCALE_FACTORS", $"{screen}={scaleText}")
        End Sub

        Public Shared Function NormalizeGalleryViewMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "List"
                    Return "List"
                Case Else
                    Return "Grid"
            End Select
        End Function

        Public Shared Function NormalizeGallerySortMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Size", "Type", "Rating", "Favorite",
                     "Width", "Height", "FileCreatedAt", "FileModifiedAt", "ExifDateTaken", "ExifDateModified",
                     "Camera", "Iso", "Aperture"
                    Return value.Trim()
                Case "Date" ' Alte Sortiereinstellung aus Versionen vor 0.4.0 - Bestandsnutzer nicht stillschweigend auf "Name" zurückfallen lassen.
                    Return "FileModifiedAt"
                Case Else
                    Return "Name"
            End Select
        End Function

        Public Shared Function NormalizeThemeMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Light", "GrayDark", "GrayLight"
                    Return value.Trim()
                Case "System", "Gray"
                    Return "GrayDark"
                Case Else
                    Return "Dark"
            End Select
        End Function

        Public Shared Function NormalizeAccentColor(value As String) As String
            Select Case If(value, "").Trim().ToUpperInvariant()
                Case "#D97706"
                    Return "#FACC15"
                Case "#F08A1A", "#E74C3C", "#F03B88", "#8B5CF6", "#3B82F6", "#0891B2", "#0F766E", "#22C55E", "#FACC15"
                    Return value.Trim().ToUpperInvariant()
                Case Else
                    Return "#F08A1A"
            End Select
        End Function

        Public Shared Function NormalizeTransparencyBackgroundMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Solid"
                    Return "Solid"
                Case "None"
                    Return "None"
                Case Else
                    Return "Checkerboard"
            End Select
        End Function

        Public Shared Function NormalizeHexColor(value As String, fallback As String) As String
            If String.IsNullOrWhiteSpace(value) Then Return fallback
            Dim trimmed = value.Trim()
            If Not System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$") Then Return fallback
            Return trimmed.ToUpperInvariant()
        End Function

        Public Shared Function NormalizeStartupImageMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Gallery", "Editor", "Fullscreen"
                    Return value.Trim()
                Case Else
                    Return "Viewer"
            End Select
        End Function

        Public Shared Function NormalizeGalleryStartupFolderMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Pictures", "Last"
                    Return value.Trim()
                Case Else
                    Return "Pictures"
            End Select
        End Function

        Public Shared Function NormalizeFolderPath(value As String) As String
            Return If(value, "").Trim()
        End Function

        Public Shared Function NormalizeWindowDimension(value As Double, fallback As Double) As Double
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) OrElse value < 200 OrElse value > 10000 Then
                Return fallback
            End If
            Return Math.Round(value, 1)
        End Function

        Public Shared Function NormalizeSavedSearches(value As List(Of SavedSearchSettings)) As List(Of SavedSearchSettings)
            Dim result As New List(Of SavedSearchSettings)()
            For Each search In If(value, New List(Of SavedSearchSettings)())
                If search Is Nothing Then Continue For
                Dim name = If(search.Name, "").Trim()
                Dim textQuery = If(search.TextQuery, "").Trim()
                Dim rootFolder = NormalizeFolderPath(search.RootFolder)
                Dim favoriteMode = NormalizeSearchFavoriteMode(search.FavoriteMode)
                Dim ratingMin = Math.Max(-1, Math.Min(5, search.RatingMin))
                If String.IsNullOrWhiteSpace(name) Then
                    If Not String.IsNullOrWhiteSpace(textQuery) Then
                        name = textQuery
                    ElseIf favoriteMode = "Only" Then
                        name = "Favoriten"
                    ElseIf ratingMin >= 0 Then
                        name = If(ratingMin = 0, "Nicht bewertet", $"{ratingMin}+ Sterne")
                    Else
                        Continue For
                    End If
                End If
                result.Add(New SavedSearchSettings With {
                    .Id = If(String.IsNullOrWhiteSpace(search.Id), Guid.NewGuid().ToString("N"), search.Id),
                    .Name = name,
                    .TextQuery = textQuery,
                    .RootFolder = rootFolder,
                    .IncludeSubfolders = search.IncludeSubfolders,
                    .FavoriteMode = favoriteMode,
                    .RatingMin = ratingMin
                })
            Next
            Return result
        End Function

        Public Shared Function NormalizeSearchFavoriteMode(value As String) As String
            Select Case If(value, "").Trim()
                Case "Only", "Not"
                    Return value.Trim()
                Case Else
                    Return "Any"
            End Select
        End Function

        Public Shared Sub SaveGalleryThumbnailSize(value As Double)
            Dim settings = Load()
            settings.GalleryThumbnailSize = value
            Save(settings)
        End Sub

        Public Shared Sub SaveGalleryViewMode(value As String)
            Dim settings = Load()
            settings.GalleryViewMode = NormalizeGalleryViewMode(value)
            Save(settings)
        End Sub

        Public Shared Sub SaveGallerySort(sortMode As String, sortAscending As Boolean)
            Dim settings = Load()
            settings.GallerySortMode = sortMode
            settings.GallerySortAscending = sortAscending
            Save(settings)
        End Sub

        Public Shared Function NormalizeGalleryFilterFavorite(value As String) As String
            If String.Equals(value, "Only", StringComparison.OrdinalIgnoreCase) Then Return "Only"
            Return "All"
        End Function

        Public Shared Function NormalizeGalleryFilterFileType(value As String) As String
            If String.Equals(value, "Raw", StringComparison.OrdinalIgnoreCase) Then Return "Raw"
            If String.Equals(value, "NonRaw", StringComparison.OrdinalIgnoreCase) Then Return "NonRaw"
            Return "All"
        End Function

        Public Shared Function NormalizeGalleryFilterRatings(value As List(Of Integer)) As List(Of Integer)
            If value Is Nothing Then Return New List(Of Integer)()
            Return value.Where(Function(r) r >= 0 AndAlso r <= 5).Distinct().OrderBy(Function(r) r).ToList()
        End Function

        Public Shared Sub SaveGalleryFilters(favorite As String, ratings As IEnumerable(Of Integer), fileType As String)
            Dim settings = Load()
            settings.GalleryFilterFavorite = NormalizeGalleryFilterFavorite(favorite)
            settings.GalleryFilterRatings = NormalizeGalleryFilterRatings(If(ratings, Enumerable.Empty(Of Integer)()).ToList())
            settings.GalleryFilterFileType = NormalizeGalleryFilterFileType(fileType)
            Save(settings)
        End Sub

        Public Shared Sub SaveGalleryStartupFolderMode(mode As String)
            Dim settings = Load()
            settings.GalleryStartupFolderMode = mode
            Save(settings)
        End Sub

        Public Shared Sub SaveLastGalleryFolder(folderPath As String)
            Dim settings = Load()
            settings.LastGalleryFolder = folderPath
            Save(settings)
        End Sub

        Public Shared Sub SaveJpgSaveQuality(value As Integer)
            Dim settings = Load()
            settings.JpgSaveQuality = value
            Save(settings)
        End Sub

        Public Shared Sub SaveLastBatchRenamePattern(value As String)
            Dim settings = Load()
            settings.LastBatchRenamePattern = If(String.IsNullOrWhiteSpace(value), "{name}_###", value)
            Save(settings)
        End Sub

        Public Shared Sub SaveViewerSlideshowIntervalSeconds(value As Integer)
            Dim settings = Load()
            settings.ViewerSlideshowIntervalSeconds = NormalizeViewerSlideshowIntervalSeconds(value)
            Save(settings)
        End Sub

        Public Shared Sub SaveEditorInfoSidebarExpanded(value As Boolean)
            Dim settings = Load()
            settings.EditorInfoSidebarExpanded = value
            Save(settings)
        End Sub

        Public Shared Sub SaveMainWindowPlacement(left As Integer, top As Integer, width As Double, height As Double)
            Dim settings = Load()
            settings.MainWindowLeft = left
            settings.MainWindowTop = top
            settings.MainWindowWidth = NormalizeWindowDimension(width, settings.MainWindowWidth)
            settings.MainWindowHeight = NormalizeWindowDimension(height, settings.MainWindowHeight)
            Save(settings)
        End Sub
    End Class

End Namespace
