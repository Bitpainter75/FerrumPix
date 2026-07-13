Imports System
Imports System.Collections.Generic
Imports System.ComponentModel
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

    Public Class WatermarkPresetSettings
        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        Public Property Text As String = ""
        Public Property ImagePath As String = ""
        Public Property OffsetXPixels As Double = 24
        Public Property OffsetYPixels As Double = 24
        Public Property WidthPixels As Double = 480
        Public Property HeightPixels As Double = 180
        Public Property Anchor As String = "BottomRight"
        Public Property RotationDegrees As Double = 0
        Public Property Opacity As Double = 100
        Public Property FontFamily As String = "Arial"
        Public Property FontSizePixels As Double = 48
        Public Property FillColor As String = "#FFFFFFFF"
    End Class

    Public Class LightroomPresetSettings
        Implements INotifyPropertyChanged

        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        Public Property Path As String = ""

        Private _isLastApplied As Boolean
        Public Property IsLastApplied As Boolean
            Get
                Return _isLastApplied
            End Get
            Set(value As Boolean)
                If _isLastApplied = value Then Return
                _isLastApplied = value
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(IsLastApplied)))
            End Set
        End Property

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
    End Class

    Public Class LutPresetSettings
        Implements INotifyPropertyChanged

        Public Property Id As String = Guid.NewGuid().ToString("N")
        Public Property Name As String = ""
        Public Property Path As String = ""

        Private _isLastApplied As Boolean
        Public Property IsLastApplied As Boolean
            Get
                Return _isLastApplied
            End Get
            Set(value As Boolean)
                If _isLastApplied = value Then Return
                _isLastApplied = value
                RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(NameOf(IsLastApplied)))
            End Set
        End Property

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged
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
        Public Property GalleryStartupCustomFolder As String = ""
        Public Property LastGalleryFolder As String = ""
        Public Property LastSaveAsTargetFolder As String = ""
        Public Property ViewerShowFilmstrip As Boolean = True
        Public Property ViewerSlideshowIntervalSeconds As Integer = 3
        Public Property ViewerOpenFitToWindow As Boolean = True
        ''' "Always" (immer einpassen, auch kleinere Bilder hochskalieren) oder "OnlyWhenLarger"
        ''' (nur einpassen, wenn das Bild größer als die Darstellungsfläche ist, sonst 100%).
        Public Property ViewerFitBehavior As String = "Always"
        Public Property EditorShowFilmstrip As Boolean = True
        ''' Kantenlänge einer Rasterzelle im Editor, in Bildpixeln.
        Public Property EditorGridSize As Integer = 50
        Public Property EditorShowRulers As Boolean = False
        Public Property EditorShowGrid As Boolean = False
        Public Property ShowHiddenFolders As Boolean = False
        ' Löschen: standardmäßig in den Papierkorb und mit Sicherheitsabfrage. Beide Schalter können das
        ' einzeln abschalten (True = überspringen).
        Public Property DeleteSkipTrash As Boolean = False
        Public Property DeleteSkipConfirmation As Boolean = False
        Public Property ThemeMode As String = "Dark"
        Public Property AccentColor As String = "#F08A1A"
        Public Property StartupImageMode As String = "Viewer"
        Public Property LanguageMode As String = "System"
        Public Property ThumbnailCacheEnabled As Boolean = True
        Public Property ThumbnailQuality As Integer = 82
        Public Property GalleryThumbnailMemoryCacheCapacity As Integer = 250
        Public Property JpgSaveQuality As Integer = 90
        Public Property PreserveMetadataOnSave As Boolean = True
        Public Property EditorInfoSidebarExpanded As Boolean = True
        ''' Ob der Vorher/Nachher-Vergleich im Editor zuletzt eingeschaltet war - gemerkter Bedienzustand
        ''' (wie die Info-Leiste), kein Schalter in den Einstellungen.
        Public Property EditorShowComparison As Boolean = True
        Public Property ViewerInfoSidebarExpanded As Boolean = True
        ''' Ganzzahliger Versatz auf alle Text-Schriftgrößen (siehe FontScaleService). 0 = Auslieferung.
        Public Property FontSizeOffset As Integer = 0
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
        Public Property LastBatchRenameStart As Integer = 1
        Public Property LastBatchRenameStep As Integer = 1
        Public Property LastBatchResizeWidth As Integer = 0
        Public Property LastBatchResizeHeight As Integer = 0
        Public Property LastBatchResizeScalePercent As Integer = 0
        Public Property LastBatchResizeLockAspect As Boolean = True
        Public Property LastBatchResizeInterpolation As String = "Bilinear"
        Public Property LastWatermarkPresetName As String = ""
        Public Property EnableDiagnosticLogging As Boolean = False
        Public Property WatermarkPresets As New List(Of WatermarkPresetSettings)()
        Public Property LightroomPresets As New List(Of LightroomPresetSettings)()
        Public Property LutPresets As New List(Of LutPresetSettings)()

        ' Immich-Anbindung (self-hosted Foto-Server). Der Baum blendet den Immich-Zweig nur ein,
        ' wenn Enabled=True und eine Server-URL hinterlegt ist. Der API-Key wird - wie bei den meisten
        ' self-hosted-Tools üblich - im Klartext in settings.json gehalten; die Datei liegt im
        ' Benutzerprofil (AppData/.config). Wer strengere Geheimnisverwaltung braucht, kann später auf
        ' einen plattformspezifischen Tresor umstellen (siehe ImmichService).
        Public Property ImmichEnabled As Boolean = False
        Public Property ImmichServerUrl As String = ""
        Public Property ImmichApiKey As String = ""
        Public Property ImmichStoreRatingInDescription As Boolean = False
        Public Property ImmichStoreTagsInDescription As Boolean = False
        ' Bearbeitete Immich-Bilder: Standard ist, ein neues Asset anzulegen (das Original bleibt
        ' unangetastet). Mit ImmichUpdateExistingAssets=True ersetzt eine Bearbeitung stattdessen das
        ' Quell-Asset - erst dann ist im Editor auch „Speichern" (statt nur „Speichern unter") möglich.
        Public Property ImmichUpdateExistingAssets As Boolean = False
        ' Löschen wirkt standardmäßig NICHT auf den Server: ein versehentliches Entf in der Galerie soll
        ' keine Bilder aus Immich entfernen. ImmichDeletePermanently umgeht zusätzlich den Immich-Papierkorb.
        Public Property ImmichAllowDelete As Boolean = False
        Public Property ImmichDeletePermanently As Boolean = False
    End Class

    Public NotInheritable Class AppSettingsService
        Private Sub New()
        End Sub

        Private Shared ReadOnly SettingsDirectory As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FerrumPix")

        Private Shared ReadOnly SettingsPath As String =
            Path.Combine(SettingsDirectory, "settings.json")

        ''' Der zuletzt bekannte, bereits normalisierte Stand als JSON. Load() liest daraus statt von der
        ''' Platte: die Einstellungen werden an 34 Stellen abgefragt, unter anderem aus Vorschaubild-Threads.
        Private Shared ReadOnly _cacheLock As New Object()
        Private Shared _cachedJson As String = Nothing

        ''' Geschrieben wird verzögert und zusammengefasst. Sonst löste jedes Häkchen im Dialog ein
        ''' vollständiges Serialisieren samt Temporärdatei und Umbenennen aus. Flush() erzwingt das Schreiben -
        ''' beim Programmende und immer dann, wenn ein Verlust der letzten Sekunde nicht hinnehmbar wäre.
        Private Const WriteDebounceMs As Integer = 1500
        Private Shared ReadOnly _writeLock As New Object()
        Private Shared _pendingJson As String = Nothing
        Private Shared _flushTimer As Timers.Timer = Nothing

        ''' Der zuletzt geöffnete Galerie-Ordner wird beim Navigieren nur gemerkt, nicht geschrieben. Ein
        ''' Ordnerklick soll gar nichts serialisieren; persistiert wird der Ordner gesammelt beim nächsten
        ''' Flush (Programmende oder ohnehin fälliger Schreibvorgang). Gelesen wird er nur beim Start.
        Private Shared ReadOnly _pendingFolderLock As New Object()
        Private Shared _pendingLastGalleryFolder As String = Nothing

        Shared Sub New()
            AddHandler AppDomain.CurrentDomain.ProcessExit, Sub(sender As Object, e As EventArgs) Flush()
        End Sub

        Public Shared Function Load() As AppSettings
            Try
                Dim json As String
                Dim readError As Exception = Nothing
                SyncLock _cacheLock
                    If _cachedJson Is Nothing Then _cachedJson = ReadSettingsJson(readError)
                    json = _cachedJson
                End SyncLock

                ' Erst protokollieren, wenn _cachedJson steht: DiagnosticLogService.LogException fragt
                ' seinerseits Load() nach EnableDiagnosticLogging. Aus ReadSettingsJson heraus zu
                ' protokollieren liefe deshalb in eine Endlosrekursion - SyncLock ist reentrant und
                ' hielte sie nicht auf.
                If readError IsNot Nothing Then DiagnosticLogService.LogException("Settings.Read", readError)

                If String.IsNullOrEmpty(json) Then Return New AppSettings()

                Dim settings = JsonSerializer.Deserialize(Of AppSettings)(json)
                If settings Is Nothing Then Return New AppSettings()

                settings.GalleryThumbnailSize = NormalizeThumbnailSize(settings.GalleryThumbnailSize)
                settings.GalleryViewMode = NormalizeGalleryViewMode(settings.GalleryViewMode)
                settings.GallerySortMode = NormalizeGallerySortMode(settings.GallerySortMode)
                settings.GalleryStartupFolderMode = NormalizeGalleryStartupFolderMode(settings.GalleryStartupFolderMode)
                settings.GalleryStartupCustomFolder = NormalizeFolderPath(settings.GalleryStartupCustomFolder)
                settings.LastGalleryFolder = NormalizeFolderPath(settings.LastGalleryFolder)
                settings.LastSaveAsTargetFolder = NormalizeFolderPath(settings.LastSaveAsTargetFolder)
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
                settings.EditorGridSize = NormalizeEditorGridSize(settings.EditorGridSize)
                settings.ViewerFitBehavior = NormalizeViewerFitBehavior(settings.ViewerFitBehavior)
                settings.MainWindowWidth = NormalizeWindowDimension(settings.MainWindowWidth, 1536)
                settings.MainWindowHeight = NormalizeWindowDimension(settings.MainWindowHeight, 1024)
                settings.FontSizeOffset = NormalizeFontSizeOffset(settings.FontSizeOffset)
                settings.ApplicationScale = NormalizeApplicationScale(settings.ApplicationScale)
                settings.ApplicationScaleScreen = NormalizeApplicationScaleScreen(settings.ApplicationScaleScreen)
                settings.SavedSearches = NormalizeSavedSearches(settings.SavedSearches)
                settings.TransparencyBackgroundMode = NormalizeTransparencyBackgroundMode(settings.TransparencyBackgroundMode)
                settings.TransparencyBackgroundColor = NormalizeHexColor(settings.TransparencyBackgroundColor, "#FFFFFFFF")
                settings.LastBatchRenamePattern = NormalizeBatchRenamePattern(settings.LastBatchRenamePattern)
                settings.LastBatchRenameStart = NormalizeBatchRenameStart(settings.LastBatchRenameStart)
                settings.LastBatchRenameStep = NormalizeBatchRenameStep(settings.LastBatchRenameStep)
                settings.LastBatchResizeWidth = NormalizeBatchResizeDimension(settings.LastBatchResizeWidth)
                settings.LastBatchResizeHeight = NormalizeBatchResizeDimension(settings.LastBatchResizeHeight)
                settings.LastBatchResizeScalePercent = NormalizeBatchResizeScalePercent(settings.LastBatchResizeScalePercent)
                settings.LastBatchResizeInterpolation = NormalizeResizeInterpolationModeName(settings.LastBatchResizeInterpolation)
                settings.LastWatermarkPresetName = NormalizePresetName(settings.LastWatermarkPresetName)
                settings.WatermarkPresets = NormalizeWatermarkPresets(settings.WatermarkPresets)
                settings.LightroomPresets = NormalizeLightroomPresets(settings.LightroomPresets)
                settings.LutPresets = NormalizeLutPresets(settings.LutPresets)
                Return settings
            Catch ex As JsonException
                ' Kaputte Datei: der nächste Save() überschriebe sie mit Standardwerten. Vorher zur
                ' Seite legen, damit Presets und gespeicherte Suchen von Hand zu retten sind.
                BackupUnreadableSettings()
                SyncLock _cacheLock
                    _cachedJson = ""
                End SyncLock
                Return New AppSettings()
            Catch
                Return New AppSettings()
            End Try
        End Function

        ''' Liest die Datei roh. Protokolliert selbst nichts - siehe Load().
        Private Shared Function ReadSettingsJson(ByRef readError As Exception) As String
            Try
                If Not File.Exists(SettingsPath) Then Return ""
                Return File.ReadAllText(SettingsPath)
            Catch ex As Exception
                readError = ex
                Return ""
            End Try
        End Function

        Private Shared Sub BackupUnreadableSettings()
            Try
                If Not File.Exists(SettingsPath) Then Return
                File.Move(SettingsPath, SettingsPath & ".corrupt", overwrite:=True)
            Catch
            End Try
        End Sub

        Public Shared Sub Save(settings As AppSettings)
            Try
                Directory.CreateDirectory(SettingsDirectory)
                settings.GalleryThumbnailSize = NormalizeThumbnailSize(settings.GalleryThumbnailSize)
                settings.GalleryViewMode = NormalizeGalleryViewMode(settings.GalleryViewMode)
                settings.GallerySortMode = NormalizeGallerySortMode(settings.GallerySortMode)
                settings.GalleryStartupFolderMode = NormalizeGalleryStartupFolderMode(settings.GalleryStartupFolderMode)
                settings.GalleryStartupCustomFolder = NormalizeFolderPath(settings.GalleryStartupCustomFolder)
                settings.LastGalleryFolder = NormalizeFolderPath(settings.LastGalleryFolder)
                settings.LastSaveAsTargetFolder = NormalizeFolderPath(settings.LastSaveAsTargetFolder)
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
                settings.EditorGridSize = NormalizeEditorGridSize(settings.EditorGridSize)
                settings.ViewerFitBehavior = NormalizeViewerFitBehavior(settings.ViewerFitBehavior)
                settings.MainWindowWidth = NormalizeWindowDimension(settings.MainWindowWidth, 1536)
                settings.MainWindowHeight = NormalizeWindowDimension(settings.MainWindowHeight, 1024)
                settings.FontSizeOffset = NormalizeFontSizeOffset(settings.FontSizeOffset)
                settings.ApplicationScale = NormalizeApplicationScale(settings.ApplicationScale)
                settings.ApplicationScaleScreen = NormalizeApplicationScaleScreen(settings.ApplicationScaleScreen)
                settings.SavedSearches = NormalizeSavedSearches(settings.SavedSearches)
                settings.TransparencyBackgroundMode = NormalizeTransparencyBackgroundMode(settings.TransparencyBackgroundMode)
                settings.TransparencyBackgroundColor = NormalizeHexColor(settings.TransparencyBackgroundColor, "#FFFFFFFF")
                settings.LastBatchRenamePattern = NormalizeBatchRenamePattern(settings.LastBatchRenamePattern)
                settings.LastBatchRenameStart = NormalizeBatchRenameStart(settings.LastBatchRenameStart)
                settings.LastBatchRenameStep = NormalizeBatchRenameStep(settings.LastBatchRenameStep)
                settings.LastBatchResizeWidth = NormalizeBatchResizeDimension(settings.LastBatchResizeWidth)
                settings.LastBatchResizeHeight = NormalizeBatchResizeDimension(settings.LastBatchResizeHeight)
                settings.LastBatchResizeScalePercent = NormalizeBatchResizeScalePercent(settings.LastBatchResizeScalePercent)
                settings.LastBatchResizeInterpolation = NormalizeResizeInterpolationModeName(settings.LastBatchResizeInterpolation)
                settings.LastWatermarkPresetName = NormalizePresetName(settings.LastWatermarkPresetName)
                settings.WatermarkPresets = NormalizeWatermarkPresets(settings.WatermarkPresets)
                settings.LightroomPresets = NormalizeLightroomPresets(settings.LightroomPresets)
                settings.LutPresets = NormalizeLutPresets(settings.LutPresets)
                Dim json = JsonSerializer.Serialize(settings, New JsonSerializerOptions With {.WriteIndented = True})

                ' Der neue Stand gilt ab sofort für alle Leser; auf die Platte geht er gesammelt.
                SyncLock _cacheLock
                    _cachedJson = json
                End SyncLock
                ThumbnailCacheService.InvalidateSettingsCache()
                ScheduleWrite(json)
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.Save", ex)
            End Try
        End Sub

        Private Shared Sub ScheduleWrite(json As String)
            SyncLock _writeLock
                _pendingJson = json
                If _flushTimer Is Nothing Then
                    _flushTimer = New Timers.Timer(WriteDebounceMs) With {.AutoReset = False}
                    AddHandler _flushTimer.Elapsed, Sub(sender As Object, e As Timers.ElapsedEventArgs) Flush()
                End If
                _flushTimer.Stop()
                _flushTimer.Start()
            End SyncLock
        End Sub

        ''' <summary>Schreibt einen ausstehenden Stand sofort auf die Platte. Wird beim Programmende gerufen
        ''' (ProcessExit) und darf jederzeit zusätzlich aufgerufen werden - ohne ausstehende Änderung tut sie
        ''' nichts.</summary>
        Public Shared Sub Flush()
            ' Erst den gemerkten Ordner in einen ausstehenden Stand überführen, dann diesen wegschreiben.
            CommitPendingLastGalleryFolder()

            Dim json As String
            SyncLock _writeLock
                json = _pendingJson
                _pendingJson = Nothing
                _flushTimer?.Stop()
            End SyncLock
            If json Is Nothing Then Return

            Try
                Directory.CreateDirectory(SettingsDirectory)
                ' Nicht direkt in settings.json schreiben: ein Absturz mitten im Schreiben hinterließe eine
                ' abgeschnittene Datei. Load() würde die beim nächsten Start als unlesbar verwerfen - samt
                ' Wasserzeichen-Presets, gespeicherten Suchen und Theme. Erst vollständig danebenschreiben,
                ' dann ersetzen.
                Dim tempPath = SettingsPath & ".tmp"
                File.WriteAllText(tempPath, json)
                File.Move(tempPath, SettingsPath, overwrite:=True)
            Catch ex As Exception
                ' Volle Platte, fehlende Rechte: früher fiel das lautlos unter den Tisch und der Nutzer
                ' glaubte, seine Einstellung sei gespeichert.
                DiagnosticLogService.LogException("Settings.Flush", ex)
            End Try
        End Sub

        ''' <summary>Ändert genau ein paar Felder und speichert. Ersetzt das fünfzehnmal kopierte
        ''' Load()-ändern-Save()-Muster.</summary>
        Public Shared Sub Update(mutate As Action(Of AppSettings))
            If mutate Is Nothing Then Return
            Dim settings = Load()
            mutate(settings)
            Save(settings)
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

        Public Shared Function NormalizeBatchRenamePattern(value As String) As String
            If String.IsNullOrWhiteSpace(value) Then Return "{name}_###"
            Return value.Trim()
        End Function

        Public Shared Function NormalizeBatchRenameStart(value As Integer) As Integer
            Return Math.Max(0, Math.Min(999999, value))
        End Function

        Public Shared Function NormalizeBatchRenameStep(value As Integer) As Integer
            Return Math.Max(1, Math.Min(999999, value))
        End Function

        Public Shared Function NormalizeBatchResizeDimension(value As Integer) As Integer
            Return Math.Max(0, Math.Min(100000, value))
        End Function

        Public Shared Function NormalizeBatchResizeScalePercent(value As Integer) As Integer
            Return Math.Max(0, Math.Min(1000, value))
        End Function

        Public Shared Function NormalizeResizeInterpolationModeName(value As String) As String
            Select Case If(value, "").Trim()
                Case "Nearest", "Bicubic"
                    Return value.Trim()
                Case Else
                    Return "Bilinear"
            End Select
        End Function

        Public Shared Function NormalizePresetName(value As String) As String
            Return If(value, "").Trim()
        End Function

        Public Shared Function NormalizeViewerSlideshowIntervalSeconds(value As Integer) As Integer
            Return Math.Max(1, Math.Min(30, value))
        End Function

        Public Shared Function NormalizeEditorGridSize(value As Integer) As Integer
            Return Math.Max(2, Math.Min(1000, value))
        End Function

        Public Shared Function NormalizeViewerFitBehavior(value As String) As String
            Select Case If(value, "").Trim()
                Case "OnlyWhenLarger"
                    Return "OnlyWhenLarger"
                Case Else
                    Return "Always"
            End Select
        End Function

        ' Untergrenze -1: die kleinste Schrift der Oberfläche ist 9px (FP.Font.Label), bei -2 wäre sie
        ' 7px und damit unlesbar. Nach oben ist reichlich Luft, die Layouts sind flexibel.
        Public Shared Function NormalizeFontSizeOffset(value As Integer) As Integer
            Return Math.Max(-1, Math.Min(6, value))
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
                Case "Pictures", "Last", "Custom"
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

        Public Shared Function NormalizeWatermarkPresets(value As List(Of WatermarkPresetSettings)) As List(Of WatermarkPresetSettings)
            Dim result As New List(Of WatermarkPresetSettings)()
            For Each preset In If(value, New List(Of WatermarkPresetSettings)())
                If preset Is Nothing Then Continue For
                Dim name = If(preset.Name, "").Trim()
                If String.IsNullOrWhiteSpace(name) Then Continue For
                result.Add(New WatermarkPresetSettings With {
                    .Id = If(String.IsNullOrWhiteSpace(preset.Id), Guid.NewGuid().ToString("N"), preset.Id),
                    .Name = name,
                    .Text = If(preset.Text, "").Trim(),
                    .ImagePath = NormalizeFolderPath(preset.ImagePath),
                    .OffsetXPixels = Math.Max(-100000, Math.Min(100000, preset.OffsetXPixels)),
                    .OffsetYPixels = Math.Max(-100000, Math.Min(100000, preset.OffsetYPixels)),
                    .WidthPixels = Math.Max(1, Math.Min(100000, preset.WidthPixels)),
                    .HeightPixels = Math.Max(1, Math.Min(100000, preset.HeightPixels)),
                    .Anchor = NormalizeAnnotationAnchorName(preset.Anchor),
                    .RotationDegrees = Math.Max(-180, Math.Min(180, preset.RotationDegrees)),
                    .Opacity = Math.Max(0, Math.Min(100, preset.Opacity)),
                    .FontFamily = If(preset.FontFamily, "Arial").Trim(),
                    .FontSizePixels = Math.Max(8, Math.Min(5000, preset.FontSizePixels)),
                    .FillColor = NormalizeHexColor(preset.FillColor, "#FFFFFFFF")
                })
            Next
            Return result.OrderBy(Function(p) p.Name, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        Public Shared Function NormalizeAnnotationAnchorName(value As String) As String
            Select Case If(value, "").Trim()
                Case "TopLeft", "Top", "TopRight", "Left", "Center", "Right", "BottomLeft", "Bottom", "BottomRight"
                    Return value.Trim()
                Case Else
                    Return "BottomRight"
            End Select
        End Function

        Public Shared Function NormalizeLightroomPresets(value As List(Of LightroomPresetSettings)) As List(Of LightroomPresetSettings)
            Dim result As New List(Of LightroomPresetSettings)()
            Dim seenPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each preset In If(value, New List(Of LightroomPresetSettings)())
                If preset Is Nothing Then Continue For
                Dim presetPath = NormalizeFolderPath(preset.Path)
                If String.IsNullOrWhiteSpace(presetPath) OrElse Not seenPaths.Add(presetPath) Then Continue For
                Dim name = If(preset.Name, "").Trim()
                If String.IsNullOrWhiteSpace(name) Then name = IO.Path.GetFileNameWithoutExtension(presetPath)
                result.Add(New LightroomPresetSettings With {
                    .Id = If(String.IsNullOrWhiteSpace(preset.Id), Guid.NewGuid().ToString("N"), preset.Id),
                    .Name = name,
                    .Path = presetPath
                })
            Next
            Return result.OrderBy(Function(p) p.Name, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        Public Shared Function NormalizeLutPresets(value As List(Of LutPresetSettings)) As List(Of LutPresetSettings)
            Dim result As New List(Of LutPresetSettings)()
            Dim seenPaths As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each preset In If(value, New List(Of LutPresetSettings)())
                If preset Is Nothing Then Continue For
                Dim presetPath = NormalizeFolderPath(preset.Path)
                If String.IsNullOrWhiteSpace(presetPath) OrElse Not seenPaths.Add(presetPath) Then Continue For
                Dim name = If(preset.Name, "").Trim()
                If String.IsNullOrWhiteSpace(name) Then name = IO.Path.GetFileNameWithoutExtension(presetPath)
                result.Add(New LutPresetSettings With {
                    .Id = If(String.IsNullOrWhiteSpace(preset.Id), Guid.NewGuid().ToString("N"), preset.Id),
                    .Name = name,
                    .Path = presetPath
                })
            Next
            Return result.OrderBy(Function(p) p.Name, StringComparer.OrdinalIgnoreCase).ToList()
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
            Update(Sub(s) s.GalleryThumbnailSize = value)
        End Sub

        Public Shared Sub SaveGalleryViewMode(value As String)
            Update(Sub(s) s.GalleryViewMode = NormalizeGalleryViewMode(value))
        End Sub

        Public Shared Sub SaveGallerySort(sortMode As String, sortAscending As Boolean)
            Update(Sub(s)
                       s.GallerySortMode = sortMode
                       s.GallerySortAscending = sortAscending
                   End Sub)
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
            Update(Sub(s)
                       s.GalleryFilterFavorite = NormalizeGalleryFilterFavorite(favorite)
                       s.GalleryFilterRatings = NormalizeGalleryFilterRatings(If(ratings, Enumerable.Empty(Of Integer)()).ToList())
                       s.GalleryFilterFileType = NormalizeGalleryFilterFileType(fileType)
                   End Sub)
        End Sub

        Public Shared Sub SaveGalleryStartupFolderMode(mode As String)
            Update(Sub(s) s.GalleryStartupFolderMode = mode)
        End Sub

        ''' Merkt sich den zuletzt geöffneten Galerie-Ordner nur im Speicher (kein Serialisieren, kein
        ''' Schreiben). Persistiert wird er beim nächsten Flush - siehe CommitPendingLastGalleryFolder.
        Public Shared Sub RememberLastGalleryFolder(folderPath As String)
            SyncLock _pendingFolderLock
                _pendingLastGalleryFolder = folderPath
            End SyncLock
        End Sub

        ''' Überführt den gemerkten Ordner in einen ausstehenden Schreibvorgang, falls er sich vom
        ''' gespeicherten Stand unterscheidet. Läuft vor jedem Flush; ohne gemerkten Ordner ein No-Op.
        Private Shared Sub CommitPendingLastGalleryFolder()
            Dim folder As String
            SyncLock _pendingFolderLock
                folder = _pendingLastGalleryFolder
                _pendingLastGalleryFolder = Nothing
            End SyncLock
            If folder Is Nothing Then Return

            If String.Equals(Load().LastGalleryFolder, NormalizeFolderPath(folder), StringComparison.Ordinal) Then Return
            Update(Sub(s) s.LastGalleryFolder = folder)
        End Sub

        Public Shared Sub SaveJpgSaveQuality(value As Integer)
            Update(Sub(s) s.JpgSaveQuality = value)
        End Sub

        Public Shared Sub SaveLastSaveAsTargetFolder(folderPath As String)
            Update(Sub(s) s.LastSaveAsTargetFolder = NormalizeFolderPath(folderPath))
        End Sub

        Public Shared Sub SaveLastBatchRenameSettings(pattern As String, start As Integer, stepValue As Integer)
            Update(Sub(s)
                       s.LastBatchRenamePattern = NormalizeBatchRenamePattern(pattern)
                       s.LastBatchRenameStart = NormalizeBatchRenameStart(start)
                       s.LastBatchRenameStep = NormalizeBatchRenameStep(stepValue)
                   End Sub)
        End Sub

        Public Shared Sub SaveLastBatchRenamePattern(value As String)
            Dim settings = Load()
            SaveLastBatchRenameSettings(value, settings.LastBatchRenameStart, settings.LastBatchRenameStep)
        End Sub

        Public Shared Sub SaveLastBatchResizeSettings(width As Integer, height As Integer, scalePercent As Integer, lockAspect As Boolean, interpolation As ResizeInterpolationMode)
            Update(Sub(s)
                       s.LastBatchResizeWidth = NormalizeBatchResizeDimension(width)
                       s.LastBatchResizeHeight = NormalizeBatchResizeDimension(height)
                       s.LastBatchResizeScalePercent = NormalizeBatchResizeScalePercent(scalePercent)
                       s.LastBatchResizeLockAspect = lockAspect
                       s.LastBatchResizeInterpolation = interpolation.ToString()
                   End Sub)
        End Sub

        Public Shared Sub SaveLastWatermarkPresetName(value As String)
            Update(Sub(s) s.LastWatermarkPresetName = NormalizePresetName(value))
        End Sub

        Public Shared Sub SaveViewerSlideshowIntervalSeconds(value As Integer)
            Update(Sub(s) s.ViewerSlideshowIntervalSeconds = NormalizeViewerSlideshowIntervalSeconds(value))
        End Sub

        Public Shared Sub SaveEditorInfoSidebarExpanded(value As Boolean)
            Update(Sub(s) s.EditorInfoSidebarExpanded = value)
        End Sub

        Public Shared Sub SaveEditorShowComparison(value As Boolean)
            Update(Sub(s) s.EditorShowComparison = value)
        End Sub

        Public Shared Sub SaveEditorShowRulers(value As Boolean)
            Update(Sub(s) s.EditorShowRulers = value)
        End Sub

        Public Shared Sub SaveEditorShowGrid(value As Boolean)
            Update(Sub(s) s.EditorShowGrid = value)
        End Sub

        Public Shared Sub SaveMainWindowPlacement(left As Integer, top As Integer, width As Double, height As Double)
            Update(Sub(s)
                       s.MainWindowLeft = left
                       s.MainWindowTop = top
                       s.MainWindowWidth = NormalizeWindowDimension(width, s.MainWindowWidth)
                       s.MainWindowHeight = NormalizeWindowDimension(height, s.MainWindowHeight)
                   End Sub)
        End Sub
    End Class

End Namespace
