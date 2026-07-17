Imports System.Windows.Input
Imports System.Linq
Imports System.IO
Imports System.Threading.Tasks
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Text.RegularExpressions
Imports System.Globalization
Imports ReactiveUI
Imports Avalonia.Threading
Imports FerrumPix.Services

Namespace ViewModels

    Public Class MainWindowViewModel
        Inherits ViewModelBase

        Private _currentMode As AppMode
        Private _previousModeBeforeSettings As AppMode = AppMode.Gallery
        Private _previousModeBeforeFullscreen As AppMode = AppMode.Viewer
        Private _title As String = "FerrumPix"
        Private _isFullscreen As Boolean
        Private _dialogTitle As String = ""
        Private _dialogMessage As String = ""
        Private _dialogInputText As String = ""
        Private _dialogConfirmText As String = "OK"
        Private _dialogCancelText As String = "Abbrechen"
        Private _dialogConflictRenameText As String = ""
        Private _dialogKind As AppDialogKind = AppDialogKind.Message
        Private _dialogCompletion As TaskCompletionSource(Of String)
        Private _dialogSelectedFormat As String = "JPG"
        Private _dialogSaveAsTarget As String = "Local"
        Private _dialogSaveAsTargetFolder As String = ""
        Private _dialogJpgQuality As Integer = AppSettingsService.Load().JpgSaveQuality
        Private _dialogExistingFile As FileConflictInfo
        Private _dialogIncomingFile As FileConflictInfo
        Private _dialogBatchRenamePattern As String = "{name}_###"
        Private _dialogBatchRenameStart As Integer = 1
        Private _dialogBatchRenameStep As Integer = 1
        Private _dialogBatchRenamePaths As List(Of String) = New List(Of String)()
        Private ReadOnly _dialogBatchRenameExifCache As New Dictionary(Of String, ExifData)(StringComparer.OrdinalIgnoreCase)
        Private _dialogSearchName As String = ""
        Private _dialogSearchSource As String = "Local"
        Private _dialogSearchText As String = ""
        Private _dialogSearchRootFolder As String = ""
        Private _dialogSearchIncludeSubfolders As Boolean = True
        Private _dialogSearchFavoriteMode As String = "Any"
        Private _dialogSearchRatingMin As Integer = -1
        Private ReadOnly _dialogSearchRatings As New HashSet(Of Integer)()
        Private _dialogSearchConditionCombinator As String = "AND"
        Private _dialogBatchResizeWidthText As String = ""
        Private _dialogBatchResizeHeightText As String = ""
        Private _dialogBatchResizeLockAspect As Boolean = True
        Private _dialogBatchResizeInterpolation As ResizeInterpolationMode = ResizeInterpolationMode.Bilinear
        Private _dialogBatchResizeScalePercent As Integer = 0
        Private _dialogBatchResizeSourceWidth As Integer = 0
        Private _dialogBatchResizeSourceHeight As Integer = 0
        Private _dialogBatchResizeOverwrite As Boolean = True
        Private _dialogBatchResizeAppendSize As Boolean = True
        Private _dialogBatchWatermarkOverwrite As Boolean = True
        Private _dialogSelectedWatermarkPresetName As String = ""
        Private ReadOnly _dialogWatermarkPresets As New List(Of WatermarkPresetSettings)()

        Public Property Gallery As GalleryViewModel
        Public Property Viewer As ViewerViewModel
        Public Property Editor As EditorViewModel
        Public Property Settings As SettingsViewModel

        Public ReadOnly Property DialogFormatOptions As ObservableCollection(Of String) = New ObservableCollection(Of String) From {
            "JPG",
            "PNG",
            "WEBP"
        }
        Public ReadOnly Property DialogBatchRenamePreview As ObservableCollection(Of BatchRenamePreviewItem) = New ObservableCollection(Of BatchRenamePreviewItem)()
        Public ReadOnly Property DialogWatermarkPresetNames As ObservableCollection(Of String) = New ObservableCollection(Of String)()

        Public Property CurrentMode As AppMode
            Get
                Return _currentMode
            End Get
            Set(value As AppMode)
                Dim previousMode = _currentMode
                Me.RaiseAndSetIfChanged(_currentMode, value)
                Me.RaisePropertyChanged(NameOf(CurrentContent))
                Me.RaisePropertyChanged(NameOf(TitleSuffix))
                Me.RaisePropertyChanged(NameOf(IsFullscreenViewer))

                ' Beim Verlassen des Viewers (egal ob aus Fenster- oder Vollbildmodus heraus)
                ' läuft ein gerade abgespieltes Video sonst im Hintergrund weiter (Ton bleibt
                ' hörbar, obwohl nichts mehr zu sehen ist).
                If previousMode = AppMode.Viewer AndAlso value <> AppMode.Viewer Then
                    Viewer?.StopVideoPlayback()
                End If

                If previousMode <> AppMode.Editor AndAlso value = AppMode.Editor Then
                    Editor?.ActivateDefaultToolForModeEntry()
                    ' Herkunft merken: Wer aus der GALERIE in den Editor kam, soll beim Verlassen
                    ' wieder in der Galerie landen (auf dem Bild), nicht im Viewer (Nutzerwunsch
                    ' 2026-07-17).
                    Editor?.SetEntryMode(previousMode)
                End If
            End Set
        End Property

        Public Property Title As String
            Get
                Return _title
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_title, value)
            End Set
        End Property

        Public Property IsFullscreen As Boolean
            Get
                Return _isFullscreen
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isFullscreen, value)
                Me.RaisePropertyChanged(NameOf(IsWindowChromeVisible))
                Me.RaisePropertyChanged(NameOf(IsFullscreenViewer))
                If value Then
                    ' Entering fullscreen: delay showing the viewport overlay until after
                    ' the OS has actually expanded the window to avoid the image being
                    ' briefly visible clipped to the normal window bounds.
                    Dispatcher.UIThread.Post(Sub() Viewer?.RaiseFullscreenChanged(), DispatcherPriority.Background)
                Else
                    ' Exiting fullscreen: hide viewport immediately; the window shrink
                    ' is already delayed in ApplyFullscreenState (MainWindow code-behind).
                    Viewer?.RaiseFullscreenChanged()
                End If
            End Set
        End Property

        Public ReadOnly Property IsWindowChromeVisible As Boolean
            Get
                Return Not _isFullscreen
            End Get
        End Property

        Public ReadOnly Property IsFullscreenViewer As Boolean
            Get
                Return _isFullscreen AndAlso _currentMode = AppMode.Viewer
            End Get
        End Property

        Public ReadOnly Property TitleSuffix As String
            Get
                Select Case _currentMode
                    Case AppMode.Editor : Return " Editor"
                    Case AppMode.Viewer : Return " Viewer"
                    Case AppMode.Settings : Return " – " & LocalizationService.T("Einstellungen")
                    Case Else : Return ""
                End Select
            End Get
        End Property

        Public ReadOnly Property IsLightLogoVisible As Boolean
            Get
                Return Settings IsNot Nothing AndAlso Settings.ThemeMode = "Light"
            End Get
        End Property

        Public ReadOnly Property IsDarkLogoVisible As Boolean
            Get
                Return Not IsLightLogoVisible
            End Get
        End Property

        Public ReadOnly Property CurrentContent As ViewModelBase
            Get
                Select Case _currentMode
                    Case AppMode.Gallery : Return Gallery
                    Case AppMode.Viewer : Return Viewer
                    Case AppMode.Editor : Return Editor
                    Case AppMode.Settings : Return Settings
                    Case Else : Return Gallery
                End Select
            End Get
        End Property

        Public ReadOnly Property DialogConfirmCommand As ICommand
        Public ReadOnly Property DialogCancelCommand As ICommand
        Public ReadOnly Property DialogSkipCommand As ICommand
        Public ReadOnly Property DialogRenameCommand As ICommand
        Public ReadOnly Property DialogSkipAllCommand As ICommand
        Public ReadOnly Property DialogOverwriteAllCommand As ICommand

        Public Sub New(Optional initialImagePath As String = Nothing)
            Settings = New SettingsViewModel(Me)
            Gallery = New GalleryViewModel(Me)
            Viewer = New ViewerViewModel(Me)
            Editor = New EditorViewModel(Me)

            DialogConfirmCommand = ReactiveCommand.Create(Sub() ConfirmDialog())
            DialogCancelCommand = ReactiveCommand.Create(Sub() CancelDialog())
            DialogSkipCommand = ReactiveCommand.Create(Sub() SkipDialog())
            DialogRenameCommand = ReactiveCommand.Create(Sub() RenameConflictDialog())
            DialogSkipAllCommand = ReactiveCommand.Create(Sub() CompleteDialog("SkipAll"))
            DialogOverwriteAllCommand = ReactiveCommand.Create(Sub() CompleteDialog("OverwriteAll"))

            If Not String.IsNullOrEmpty(initialImagePath) Then
                OpenInitialImage(initialImagePath)
            Else
                OpenStartupGallery()
                CurrentMode = AppMode.Gallery
            End If
        End Sub

        Private Async Function ConfirmEditorLeaveAsync(actionDescription As String) As Task(Of Boolean)
            If Editor Is Nothing OrElse Not Editor.HasUnsavedChanges Then Return True
            Return Await Editor.ConfirmSaveBeforeLeavingAsync(actionDescription)
        End Function

        Private Async Function ConfirmViewerLeaveAsync(actionDescription As String) As Task(Of Boolean)
            If CurrentMode <> AppMode.Viewer OrElse Viewer Is Nothing Then Return True
            Return Await Viewer.ConfirmPendingRotationAsync(actionDescription)
        End Function

        Public Async Sub OpenImageInViewer(imagePath As String, Optional allPaths As System.Collections.Generic.List(Of String) = Nothing, Optional bypassEditorPrompt As Boolean = False, Optional cacheScopeId As String = Nothing, Optional cacheScopeName As String = Nothing)
            If CurrentMode = AppMode.Editor AndAlso Not bypassEditorPrompt Then
                If Not Await ConfirmEditorLeaveAsync("den Betrachter zu öffnen") Then Return
            End If
            If CurrentMode = AppMode.Viewer AndAlso
               Viewer IsNot Nothing AndAlso
               Not String.Equals(Viewer.CurrentImagePath, imagePath, StringComparison.OrdinalIgnoreCase) Then
                If Not Await ConfirmViewerLeaveAsync("ein anderes Bild öffnest") Then Return
            End If
            Viewer.OpenImage(imagePath, allPaths, cacheScopeId, cacheScopeName)
            CurrentMode = AppMode.Viewer
        End Sub

        ''' <summary>Öffnet eine Immich-Sitzung im Betrachter: der Filmstreifen zeigt das ganze Album
        ''' (Pseudo-Pfade), das aktuelle Bild wird on-demand heruntergeladen.</summary>
        Public Async Sub OpenImmichViewer(startPseudoPath As String, sessionItems As System.Collections.Generic.List(Of Models.ImageItem), Optional immichAlbumId As String = Nothing)
            If CurrentMode = AppMode.Editor Then
                If Not Await ConfirmEditorLeaveAsync("den Betrachter zu öffnen") Then Return
            End If
            Viewer.OpenImmichSession(startPseudoPath, sessionItems, immichAlbumId)
            CurrentMode = AppMode.Viewer
        End Sub

        Public Async Function OpenImageInEditor(path As String, Optional allPaths As System.Collections.Generic.List(Of String) = Nothing, Optional cacheScopeId As String = Nothing, Optional cacheScopeName As String = Nothing, Optional forceSaveAsOnly As Boolean = False, Optional immichAlbumId As String = Nothing) As Task
            If CurrentMode = AppMode.Editor AndAlso Not String.Equals(Editor?.CurrentImagePath, path, StringComparison.OrdinalIgnoreCase) Then
                If Not Await ConfirmEditorLeaveAsync("ein anderes Bild zu öffnen") Then Return
            End If
            If CurrentMode = AppMode.Viewer Then
                If Not Await ConfirmViewerLeaveAsync("den Editor öffnest") Then Return
            End If
            Dim opened = Await Editor.OpenImageAsync(path, allPaths, cacheScopeId, cacheScopeName, forceSaveAsOnly, immichAlbumId)
            If Not opened Then Return
            CurrentMode = AppMode.Editor
        End Function

        Public Async Sub OpenSettings()
            If CurrentMode = AppMode.Editor Then
                If Not Await ConfirmEditorLeaveAsync("die Einstellungen zu öffnen") Then Return
            End If
            If CurrentMode = AppMode.Viewer Then
                If Not Await ConfirmViewerLeaveAsync("die Einstellungen öffnest") Then Return
            End If
            If CurrentMode <> AppMode.Settings Then
                _previousModeBeforeSettings = CurrentMode
                Settings?.BeginEditSession()
            End If
            Settings?.RefreshThumbnailCacheFolders()
            CurrentMode = AppMode.Settings
        End Sub

        Public Sub CloseSettings()
            CurrentMode = _previousModeBeforeSettings
        End Sub

        Public Async Sub BackToGallery(Optional sourcePath As String = Nothing)
            If CurrentMode = AppMode.Editor Then
                If Not Await ConfirmEditorLeaveAsync("zur Galerie zu wechseln") Then Return
            End If
            If CurrentMode = AppMode.Viewer Then
                If Not Await ConfirmViewerLeaveAsync("zur Galerie wechselst") Then Return
            End If
            If String.IsNullOrEmpty(sourcePath) AndAlso Viewer IsNot Nothing Then
                sourcePath = Viewer.CurrentImagePath
            End If

            If Not String.IsNullOrEmpty(sourcePath) Then
                If Gallery IsNot Nothing AndAlso Gallery.IsVirtualFolder AndAlso Gallery.SelectImageInCurrentView(sourcePath) Then
                    CurrentMode = AppMode.Gallery
                    Return
                End If
                If IO.File.Exists(sourcePath) Then
                    Await Gallery.OpenFolderForImage(sourcePath)
                ElseIf IO.Directory.Exists(sourcePath) Then
                    Gallery.SetInitialFolderNodeForPath(sourcePath)
                    Gallery.NavigateToFolder(sourcePath)
                End If
            End If
            CurrentMode = AppMode.Gallery
        End Sub

        Public Async Sub EnterFullscreen()
            If CurrentMode = AppMode.Gallery AndAlso Gallery.SelectedItem IsNot Nothing AndAlso (Gallery.SelectedItem.IsImage OrElse Gallery.SelectedItem.IsVideoFile) Then
                _previousModeBeforeFullscreen = AppMode.Gallery
                OpenImageInViewer(Gallery.SelectedItem.FilePath, Gallery.Items.Where(Function(i) i.IsImage OrElse i.IsVideoFile).Select(Function(i) i.FilePath).ToList(),
                                  cacheScopeId:=Gallery.CurrentThumbnailCacheScopeId, cacheScopeName:=Gallery.CurrentThumbnailCacheScopeName)
            ElseIf CurrentMode = AppMode.Editor AndAlso Not String.IsNullOrEmpty(Editor.CurrentImagePath) Then
                If Not Await ConfirmEditorLeaveAsync("den Vollbildmodus zu öffnen") Then Return
                _previousModeBeforeFullscreen = AppMode.Editor
                Viewer.OpenImage(Editor.CurrentImagePath)
                CurrentMode = AppMode.Viewer
            ElseIf CurrentMode = AppMode.Viewer AndAlso String.IsNullOrEmpty(Viewer.CurrentImagePath) Then
                Return
            ElseIf CurrentMode = AppMode.Gallery Then
                Return
            Else
                _previousModeBeforeFullscreen = CurrentMode
            End If
            IsFullscreen = True
        End Sub

        Public Async Sub ExitFullscreen()
            If CurrentMode = AppMode.Viewer AndAlso _previousModeBeforeFullscreen <> AppMode.Viewer Then
                If Not Await ConfirmViewerLeaveAsync("den Vollbildmodus verlässt") Then Return
            End If
            IsFullscreen = False
            Viewer.StopSlideshow()
            CurrentMode = _previousModeBeforeFullscreen
        End Sub

        Private Sub OpenInitialImage(imagePath As String)
            Select Case AppSettingsService.Load().StartupImageMode
                Case "Gallery"
                    ' Beim Programmstart: das Laden läuft an, die Auswahl setzt sich, sobald es fertig ist.
                    Dim ignored = Gallery.OpenFolderForImage(imagePath)
                    CurrentMode = AppMode.Gallery
                Case "Editor"
                    If SvgPreviewService.IsSupportedSvg(imagePath) Then
                        Viewer.OpenImage(imagePath)
                        CurrentMode = AppMode.Viewer
                    Else
                        Editor.OpenImage(imagePath)
                        CurrentMode = AppMode.Editor
                    End If
                Case "Fullscreen"
                    _previousModeBeforeFullscreen = AppMode.Viewer
                    Viewer.OpenImage(imagePath)
                    CurrentMode = AppMode.Viewer
                    IsFullscreen = True
                Case Else
                    Viewer.OpenImage(imagePath)
                    CurrentMode = AppMode.Viewer
            End Select
        End Sub

        Private Sub OpenStartupGallery()
            Dim settings = AppSettingsService.Load()
            Dim targetFolder As String = Nothing

            Select Case AppSettingsService.NormalizeGalleryStartupFolderMode(settings.GalleryStartupFolderMode)
                Case "Last"
                    ' Auch ein Immich-Ziel (Alle Fotos/Album/Person/Ort) kann der zuletzt geöffnete
                    ' „Ordner" sein (Nutzerwunsch 2026-07-17). Erst den Bilder-Ordner als Basis
                    ' laden, dann asynchron das Immich-Ziel öffnen - bei ausgeschaltetem/nicht
                    ' erreichbarem Immich bleibt so der Bilder-Ordner als Fallback stehen.
                    If If(settings.LastGalleryFolder, "").StartsWith("immich://", StringComparison.OrdinalIgnoreCase) Then
                        If ImmichService.IsConfigured Then
                            Dim pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                            If Directory.Exists(pictures) Then
                                Gallery.SetInitialFolderNodeForPath(pictures)
                                Gallery.NavigateToFolder(pictures)
                            End If
                            Dim ignored = Gallery.OpenImmichStartupTargetAsync(settings.LastGalleryFolder)
                            Return
                        End If
                    ElseIf Directory.Exists(settings.LastGalleryFolder) Then
                        targetFolder = settings.LastGalleryFolder
                    End If
                Case "Custom"
                    If Directory.Exists(settings.GalleryStartupCustomFolder) Then
                        targetFolder = settings.GalleryStartupCustomFolder
                    End If
                Case "Immich"
                    ' Fester Start in Immich „Alle Fotos" (Einstellungsdialog, Nutzerwunsch
                    ' 2026-07-17) - gleiche Mechanik wie der Letzter-Ordner-Fall: Bilder-Ordner
                    ' als Basis laden, Immich-Ziel asynchron öffnen (Fallback bleibt so stehen).
                    If ImmichService.IsConfigured Then
                        Dim pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                        If Directory.Exists(pictures) Then
                            Gallery.SetInitialFolderNodeForPath(pictures)
                            Gallery.NavigateToFolder(pictures)
                        End If
                        Dim ignored = Gallery.OpenImmichStartupTargetAsync("immich://all")
                        Return
                    End If
            End Select

            If String.IsNullOrEmpty(targetFolder) OrElse Not Directory.Exists(targetFolder) Then
                targetFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            End If

            If String.IsNullOrEmpty(targetFolder) OrElse Not Directory.Exists(targetFolder) Then Return

            Gallery.SetInitialFolderNodeForPath(targetFolder)
            Gallery.NavigateToFolder(targetFolder)
        End Sub

        Public Sub RefreshThemeBindings()
            Me.RaisePropertyChanged(NameOf(IsLightLogoVisible))
            Me.RaisePropertyChanged(NameOf(IsDarkLogoVisible))
        End Sub

        Public Sub RefreshLayoutBindings()
            Viewer?.RaisePropertyChanged(NameOf(ViewerViewModel.ShowFilmstrip))
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.ShowFilmstrip))
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.IsInfoSidebarVisible))
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.IsLayersPanelVisible))
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.EditorGridSize))
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.EditorShowRulers))
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.EditorShowGrid))
        End Sub

        Public Sub RefreshDisplayBindings()
            Viewer?.RaisePropertyChanged(NameOf(ViewerViewModel.TransparencyBackgroundBrush))
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.TransparencyBackgroundBrush))
        End Sub

        Public Sub RefreshLocalization()
            Me.RaisePropertyChanged(NameOf(TitleSuffix))
            Gallery?.RefreshLocalization()
            Viewer?.RefreshLocalization()
            Editor?.RefreshLocalization()
            Settings?.RefreshLocalization()
        End Sub

        Public Property DialogTitle As String
            Get
                Return _dialogTitle
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogTitle, value)
            End Set
        End Property

        Public Property DialogMessage As String
            Get
                Return _dialogMessage
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogMessage, value)
            End Set
        End Property

        Public Property DialogInputText As String
            Get
                Return _dialogInputText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogInputText, value)
            End Set
        End Property

        Public Property DialogSelectedFormat As String
            Get
                Return _dialogSelectedFormat
            End Get
            Set(value As String)
                Dim normalized = NormalizeSaveAsFormat(value)
                If _dialogSelectedFormat = normalized Then Return
                Me.RaiseAndSetIfChanged(_dialogSelectedFormat, normalized)
                Me.RaisePropertyChanged(NameOf(IsDialogJpgQualityVisible))
                ' Verfügbarkeit des Immich-Ziels hängt vom Format ab (FPX: nur lokal); ein zuvor gewähltes
                ' Immich-Ziel auf Lokal zurücksetzen, damit keine ausgeblendete Option aktiv bleibt.
                Me.RaisePropertyChanged(NameOf(IsSaveAsImmichAvailable))
                If Not IsSaveAsImmichAvailable AndAlso String.Equals(_dialogSaveAsTarget, "Immich", StringComparison.OrdinalIgnoreCase) Then
                    DialogSaveAsTarget = "Local"
                End If
            End Set
        End Property

        Public Property DialogJpgQuality As Integer
            Get
                Return _dialogJpgQuality
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_dialogJpgQuality, AppSettingsService.NormalizeJpgSaveQuality(value))
            End Set
        End Property

        ''' <summary>Zielort im Speichern-unter-Dialog: "Local" oder "Immich" (nur wählbar, wenn konfiguriert).</summary>
        Public Property DialogSaveAsTarget As String
            Get
                Return _dialogSaveAsTarget
            End Get
            Set(value As String)
                Dim v = If(String.Equals(value, "Immich", StringComparison.OrdinalIgnoreCase) AndAlso IsSaveAsImmichAvailable, "Immich", "Local")
                If _dialogSaveAsTarget = v Then Return
                Me.RaiseAndSetIfChanged(_dialogSaveAsTarget, v)
                Me.RaisePropertyChanged(NameOf(IsSaveAsTargetLocal))
                Me.RaisePropertyChanged(NameOf(IsSaveAsTargetImmich))
                Me.RaisePropertyChanged(NameOf(IsSaveAsTargetFolderVisible))
            End Set
        End Property

        Public Property DialogSaveAsTargetFolder As String
            Get
                Return _dialogSaveAsTargetFolder
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogSaveAsTargetFolder, AppSettingsService.NormalizeFolderPath(value))
            End Set
        End Property

        Public Sub SetDialogSaveAsTarget(target As String)
            DialogSaveAsTarget = target
        End Sub

        Public ReadOnly Property IsSaveAsImmichAvailable As Boolean
            Get
                ' FPX ist ein lokales, nicht-destruktives Projektformat - ein Upload nach Immich (das Bilder
                ' als Assets führt) ergibt keinen Sinn, daher als Ziel ausschließen.
                Return ImmichService.IsConfigured AndAlso Not String.Equals(_dialogSelectedFormat, "FPX", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSaveAsTargetLocal As Boolean
            Get
                Return Not String.Equals(_dialogSaveAsTarget, "Immich", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSaveAsTargetImmich As Boolean
            Get
                Return String.Equals(_dialogSaveAsTarget, "Immich", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsSaveAsTargetFolderVisible As Boolean
            Get
                Return IsSaveAsTargetLocal
            End Get
        End Property

#Region "Dialog: Filter anwenden (Stapel)"

        Private _dialogFilterSourceKind As String = BatchFilterDialogResult.SourceFilter
        Private _dialogSelectedFilterChoice As String = ""
        Private _dialogFilterStrength As Integer = 100
        Private _dialogBatchFilterOverwrite As Boolean = False
        Private _dialogBatchFilterAppendName As Boolean = True
        ''' Anzeigename -> Dateipfad. Bei den eingebauten Filtern leer: sie stehen als Name in den
        ''' Anpassungen, nicht als Datei.
        Private ReadOnly _dialogFilterChoicePaths As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        ''' Die Auswahlliste zur aktuellen Quelle - eingebaute Filter, gespeicherte Lightroom-Presets oder
        ''' gespeicherte LUTs. Wird bei jedem Quellenwechsel neu aufgebaut.
        Public ReadOnly Property DialogFilterChoices As New ObservableCollection(Of String)()

        Public Property DialogFilterSourceKind As String
            Get
                Return _dialogFilterSourceKind
            End Get
            Set(value As String)
                Dim normalized = If(String.IsNullOrWhiteSpace(value), BatchFilterDialogResult.SourceFilter, value.Trim())
                If String.Equals(_dialogFilterSourceKind, normalized, StringComparison.OrdinalIgnoreCase) Then Return
                _dialogFilterSourceKind = normalized
                RebuildDialogFilterChoices()
                Me.RaisePropertyChanged(NameOf(DialogFilterSourceKind))
                Me.RaisePropertyChanged(NameOf(IsDialogFilterSourceFilter))
                Me.RaisePropertyChanged(NameOf(IsDialogFilterSourceLightroom))
                Me.RaisePropertyChanged(NameOf(IsDialogFilterSourceLut))
                Me.RaisePropertyChanged(NameOf(IsDialogFilterFileVisible))
                Me.RaisePropertyChanged(NameOf(IsDialogFilterStrengthVisible))
            End Set
        End Property

        Public Sub SetDialogFilterSourceKind(kind As String)
            DialogFilterSourceKind = kind
        End Sub

        Public ReadOnly Property IsDialogFilterSourceFilter As Boolean
            Get
                Return String.Equals(_dialogFilterSourceKind, BatchFilterDialogResult.SourceFilter, StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsDialogFilterSourceLightroom As Boolean
            Get
                Return String.Equals(_dialogFilterSourceKind, BatchFilterDialogResult.SourceLightroom, StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsDialogFilterSourceLut As Boolean
            Get
                Return String.Equals(_dialogFilterSourceKind, BatchFilterDialogResult.SourceLut, StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' Nur Presets aus Dateien lassen sich hinzuladen - die eingebauten Filter sind fest.
        Public ReadOnly Property IsDialogFilterFileVisible As Boolean
            Get
                Return Not IsDialogFilterSourceFilter
            End Get
        End Property

        ''' Ein Lightroom-Preset ist eine Sammlung einzelner Regler, kein Effekt mit einem Mischregler -
        ''' eine "Stärke" gäbe es dort nur als willkürliche Skalierung aller Werte.
        Public ReadOnly Property IsDialogFilterStrengthVisible As Boolean
            Get
                Return Not IsDialogFilterSourceLightroom
            End Get
        End Property

        Public Property DialogSelectedFilterChoice As String
            Get
                Return _dialogSelectedFilterChoice
            End Get
            Set(value As String)
                Dim normalized = If(value, "")
                If String.Equals(_dialogSelectedFilterChoice, normalized, StringComparison.Ordinal) Then Return
                _dialogSelectedFilterChoice = normalized
                ' Jeder eingebaute Filter bringt seine eigene Startstärke mit - genau die, mit der er auch
                ' im Editor anfängt (S/W und Sepia voll, alle anderen halb). Sonst sähe derselbe Filter im
                ' Stapel anders aus als in der Einzelbearbeitung.
                If IsDialogFilterSourceFilter AndAlso normalized.Length > 0 Then
                    DialogFilterStrength = CInt(ImageAdjustments.DefaultFilterStrength(normalized))
                End If
                Me.RaisePropertyChanged(NameOf(DialogSelectedFilterChoice))
            End Set
        End Property

        Public Property DialogFilterStrength As Integer
            Get
                Return _dialogFilterStrength
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_dialogFilterStrength, Math.Max(0, Math.Min(100, value)))
            End Set
        End Property

        ''' Überschreiben blendet Format, Ziel und Namenszusatz aus: die Datei behält Pfad und Endung.
        Public Property DialogBatchFilterOverwrite As Boolean
            Get
                Return _dialogBatchFilterOverwrite
            End Get
            Set(value As Boolean)
                If _dialogBatchFilterOverwrite = value Then Return
                _dialogBatchFilterOverwrite = value
                ' Die Wahl bleibt über Sitzungen erhalten (Nutzerwunsch 2026-07-17).
                AppSettingsService.SaveBatchFilterOverwriteOriginals(value)
                Me.RaisePropertyChanged(NameOf(DialogBatchFilterOverwrite))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsOptions))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsMetaOptions))
                Me.RaisePropertyChanged(NameOf(DialogWidth))
                Me.RaisePropertyChanged(NameOf(IsDialogJpgQualityVisible))
                Me.RaisePropertyChanged(NameOf(IsDialogFilterAppendNameVisible))
            End Set
        End Property

        Public Property DialogBatchFilterAppendName As Boolean
            Get
                Return _dialogBatchFilterAppendName
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogBatchFilterAppendName, value)
            End Set
        End Property

        Public ReadOnly Property IsDialogFilterAppendNameVisible As Boolean
            Get
                Return Not _dialogBatchFilterOverwrite
            End Get
        End Property

        Private Sub RebuildDialogFilterChoices()
            DialogFilterChoices.Clear()
            _dialogFilterChoicePaths.Clear()

            If IsDialogFilterSourceFilter Then
                ' "Keine" ist der neutrale Eintrag des Editors und im Stapel sinnlos.
                For Each name In ImageAdjustments.FilterPresetNames.Where(Function(n) Not String.Equals(n, "Keine", StringComparison.OrdinalIgnoreCase))
                    DialogFilterChoices.Add(name)
                Next
            Else
                Dim settings = AppSettingsService.Load()
                Dim entries = If(IsDialogFilterSourceLightroom,
                                 settings.LightroomPresets.Select(Function(p) (p.Name, p.Path)),
                                 settings.LutPresets.Select(Function(p) (p.Name, p.Path)))
                For Each entry In entries
                    If String.IsNullOrWhiteSpace(entry.Path) OrElse Not File.Exists(entry.Path) Then Continue For
                    Dim label = If(String.IsNullOrWhiteSpace(entry.Name), IO.Path.GetFileNameWithoutExtension(entry.Path), entry.Name)
                    If _dialogFilterChoicePaths.ContainsKey(label) Then Continue For
                    _dialogFilterChoicePaths(label) = entry.Path
                    DialogFilterChoices.Add(label)
                Next
            End If

            ' Eine LUT ist ein fertiger Look, kein Effekt mit halber Grundstärke: sie startet voll.
            If Not IsDialogFilterSourceFilter Then DialogFilterStrength = 100
            DialogSelectedFilterChoice = If(DialogFilterChoices.Count > 0, DialogFilterChoices(0), "")
        End Sub

        ''' Vom Dialog aufgerufen, wenn der Nutzer eine .xmp/.cube-Datei außerhalb der gespeicherten
        ''' Vorgaben wählt: der Eintrag kommt oben in die Liste und wird gleich ausgewählt.
        Public Sub AddDialogFilterFileChoice(path As String)
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return
            Dim label = IO.Path.GetFileNameWithoutExtension(path)
            If String.IsNullOrWhiteSpace(label) Then Return
            If Not _dialogFilterChoicePaths.ContainsKey(label) Then
                DialogFilterChoices.Insert(0, label)
            End If
            _dialogFilterChoicePaths(label) = path
            DialogSelectedFilterChoice = label
        End Sub

        ''' <param name="currentFolder">Der Ordner, in dem die Galerie gerade steht. Er ist die naheliegende
        ''' Vorgabe für neue Dateien - anders als beim Konvertieren, wo der zuletzt gewählte Exportordner
        ''' gemeint ist. Leer (z.B. in einer Suchliste oder in Immich) fällt es auf diesen zurück.</param>
        Public Async Function ShowBatchFilterAsync(fileCount As Integer, Optional currentFolder As String = "") As Task(Of BatchFilterDialogResult)
            _dialogFilterSourceKind = BatchFilterDialogResult.SourceFilter
            _dialogBatchFilterOverwrite = AppSettingsService.Load().BatchFilterOverwriteOriginals
            _dialogBatchFilterAppendName = True
            ResetDialogSaveAsMetaOptions()
            DialogSelectedFormat = NormalizeSaveAsFormat("JPG")
            DialogJpgQuality = 90
            DialogSaveAsTarget = "Local"
            DialogSaveAsTargetFolder = If(Not String.IsNullOrWhiteSpace(currentFolder) AndAlso Directory.Exists(currentFolder),
                                          currentFolder,
                                          ResolveDefaultSaveAsTargetFolder())
            ' Nach dem Neuaufbau steht der erste Filter in der Auswahl - dessen Setter setzt die Stärke.
            RebuildDialogFilterChoices()
            For Each name In {NameOf(DialogFilterSourceKind), NameOf(IsDialogFilterSourceFilter),
                              NameOf(IsDialogFilterSourceLightroom), NameOf(IsDialogFilterSourceLut),
                              NameOf(IsDialogFilterFileVisible), NameOf(IsDialogFilterStrengthVisible),
                              NameOf(DialogBatchFilterOverwrite), NameOf(DialogBatchFilterAppendName),
                              NameOf(IsDialogFilterAppendNameVisible), NameOf(DialogShowsSaveAsOptions),
                              NameOf(DialogShowsSaveAsMetaOptions), NameOf(DialogWidth),
                              NameOf(IsDialogJpgQualityVisible), NameOf(IsSaveAsImmichAvailable)}
                Me.RaisePropertyChanged(name)
            Next

            ' Titel vorab zusammensetzen: ShowDialogAsync übersetzt ihn zwar, aber ein interpolierter Text
            ' mit der Dateizahl darin hätte in keiner Sprache einen Schlüssel (siehe LocalizationService).
            Dim title = $"{LocalizationService.T("Filter anwenden")} ({fileCount} {LocalizationService.T("Dateien")})"
            Dim result = Await ShowDialogAsync(AppDialogKind.BatchFilter,
                                               title,
                                               "Wähle den Look und wohin die Bilder geschrieben werden.",
                                               "",
                                               "Anwenden",
                                               "Abbrechen")
            If result Is Nothing Then Return Nothing
            If String.IsNullOrWhiteSpace(_dialogSelectedFilterChoice) Then Return Nothing

            If Not _dialogBatchFilterOverwrite Then PersistDialogTargetFolderIfLocal()

            Dim path As String = Nothing
            _dialogFilterChoicePaths.TryGetValue(_dialogSelectedFilterChoice, path)
            Return New BatchFilterDialogResult With {
                .SourceKind = _dialogFilterSourceKind,
                .DisplayName = _dialogSelectedFilterChoice,
                .PresetPath = If(path, ""),
                .Strength = _dialogFilterStrength,
                .Overwrite = _dialogBatchFilterOverwrite,
                .AppendNameToFileName = _dialogBatchFilterAppendName,
                .Format = DialogSelectedFormat,
                .JpgQuality = DialogJpgQuality,
                .Target = DialogSaveAsTarget,
                .TargetFolder = DialogSaveAsTargetFolder,
                .CopyRating = _dialogSaveAsCopyRating,
                .CopyFavorite = _dialogSaveAsCopyFavorite,
                .CopyColorLabel = _dialogSaveAsCopyColorLabel,
                .CopyKeywords = _dialogSaveAsCopyKeywords
            }
        End Function

#End Region

        Public Property DialogConfirmText As String
            Get
                Return _dialogConfirmText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogConfirmText, value)
            End Set
        End Property

        Public Property DialogCancelText As String
            Get
                Return _dialogCancelText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogCancelText, value)
                Me.RaisePropertyChanged(NameOf(IsDialogCancelVisible))
            End Set
        End Property

        Public Property DialogConflictRenameText As String
            Get
                Return _dialogConflictRenameText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogConflictRenameText, value)
            End Set
        End Property

        Public Property DialogBatchRenamePattern As String
            Get
                Return _dialogBatchRenamePattern
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogBatchRenamePattern, If(value, ""))
                RebuildBatchRenamePreview()
            End Set
        End Property

        Public Property DialogBatchRenameStart As Integer
            Get
                Return _dialogBatchRenameStart
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_dialogBatchRenameStart, Math.Max(0, value))
                RebuildBatchRenamePreview()
            End Set
        End Property

        Public Property DialogBatchRenameStep As Integer
            Get
                Return _dialogBatchRenameStep
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_dialogBatchRenameStep, Math.Max(1, value))
                RebuildBatchRenamePreview()
            End Set
        End Property

        Public Property DialogSearchName As String
            Get
                Return _dialogSearchName
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogSearchName, If(value, ""))
            End Set
        End Property

        Public Property DialogSearchText As String
            Get
                Return _dialogSearchText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogSearchText, If(value, ""))
            End Set
        End Property

        Public Property DialogSearchRootFolder As String
            Get
                Return _dialogSearchRootFolder
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogSearchRootFolder, If(value, ""))
            End Set
        End Property

        Public Property DialogSearchIncludeSubfolders As Boolean
            Get
                Return _dialogSearchIncludeSubfolders
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogSearchIncludeSubfolders, value)
            End Set
        End Property

        ''' <summary>Suchquelle im Dialog: "Local" (Dateisystem) oder "Immich" (nur wählbar, wenn Immich
        ''' konfiguriert ist). Bei Immich gelten Struktur-/Ordnerbedingungen nicht.</summary>
        Public Property DialogSearchSource As String
            Get
                Return _dialogSearchSource
            End Get
            Set(value As String)
                Dim v = If(String.Equals(value, "Immich", StringComparison.OrdinalIgnoreCase) AndAlso IsDialogImmichAvailable, "Immich", "Local")
                If _dialogSearchSource = v Then Return
                Me.RaiseAndSetIfChanged(_dialogSearchSource, v)
                Me.RaisePropertyChanged(NameOf(IsDialogSourceLocal))
                Me.RaisePropertyChanged(NameOf(IsDialogSourceImmich))
            End Set
        End Property

        Public ReadOnly Property IsDialogImmichAvailable As Boolean
            Get
                Return ImmichService.IsConfigured
            End Get
        End Property

        Public ReadOnly Property IsDialogSourceLocal As Boolean
            Get
                Return Not String.Equals(_dialogSearchSource, "Immich", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsDialogSourceImmich As Boolean
            Get
                Return String.Equals(_dialogSearchSource, "Immich", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public Property DialogSearchFavoriteMode As String
            Get
                Return _dialogSearchFavoriteMode
            End Get
            Set(value As String)
                value = AppSettingsService.NormalizeSearchFavoriteMode(value)
                If _dialogSearchFavoriteMode = value Then Return
                Me.RaiseAndSetIfChanged(_dialogSearchFavoriteMode, value)
                RaiseDialogSearchFavoriteState()
            End Set
        End Property

        Public ReadOnly Property DialogSearchConditions As New ObservableCollection(Of SearchCondition)()

        Public ReadOnly Property DialogSearchConditionFieldOptions As New ObservableCollection(Of String)(SearchCondition.ValidFields)

        Public ReadOnly Property DialogSearchConditionOperatorOptions As New ObservableCollection(Of String)(SearchCondition.ValidOperators)

        Public Property DialogSearchConditionCombinator As String
            Get
                Return _dialogSearchConditionCombinator
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogSearchConditionCombinator, If(String.Equals(value, "OR", StringComparison.OrdinalIgnoreCase), "OR", "AND"))
                Me.RaisePropertyChanged(NameOf(IsDialogSearchConditionAnd))
                Me.RaisePropertyChanged(NameOf(IsDialogSearchConditionOr))
            End Set
        End Property

        Public ReadOnly Property IsDialogSearchConditionAnd As Boolean
            Get
                Return _dialogSearchConditionCombinator = "AND"
            End Get
        End Property

        Public ReadOnly Property IsDialogSearchConditionOr As Boolean
            Get
                Return _dialogSearchConditionCombinator = "OR"
            End Get
        End Property

        Public Sub AddDialogSearchCondition()
            DialogSearchConditions.Add(New SearchCondition With {.Field = "Width", .Operator = ">", .Value = ""})
        End Sub

        Public Sub RemoveDialogSearchCondition(condition As SearchCondition)
            If condition Is Nothing Then Return
            DialogSearchConditions.Remove(condition)
        End Sub

        Public Property DialogSearchRatingMin As Integer
            Get
                Return _dialogSearchRatingMin
            End Get
            Set(value As Integer)
                value = Math.Max(-1, Math.Min(5, value))
                If _dialogSearchRatingMin = value Then Return
                Me.RaiseAndSetIfChanged(_dialogSearchRatingMin, value)
                RaiseDialogSearchRatingState()
            End Set
        End Property

        Public Sub ToggleDialogSearchRating(valueText As String)
            Dim value As Integer
            If Not Integer.TryParse(valueText, value) Then Return
            If value < 0 Then
                If _dialogSearchRatings.Count = 0 Then Return
                _dialogSearchRatings.Clear()
            Else
                value = Math.Max(0, Math.Min(5, value))
                If _dialogSearchRatings.Contains(value) Then
                    _dialogSearchRatings.Remove(value)
                Else
                    _dialogSearchRatings.Add(value)
                End If
            End If
            RaiseDialogSearchRatingState()
        End Sub

        Public ReadOnly Property IsDialogSearchFavoriteOnly As Boolean
            Get
                Return _dialogSearchFavoriteMode = "Only"
            End Get
        End Property

        Public ReadOnly Property IsDialogSearchFavoriteNot As Boolean
            Get
                Return _dialogSearchFavoriteMode = "Not"
            End Get
        End Property

        Public ReadOnly Property DialogSearchRatingLabel As String
            Get
                If _dialogSearchRatings.Count = 0 Then Return "Alle"
                Return String.Join(", ", _dialogSearchRatings.OrderBy(Function(r) r).Select(Function(r)
                    If r = 0 Then Return "Nicht bewertet"
                    Return If(r = 1, "1 Stern", $"{r} Sterne")
                End Function))
            End Get
        End Property

        Public ReadOnly Property IsDialogSearchRatingAll As Boolean
            Get
                Return _dialogSearchRatings.Count = 0
            End Get
        End Property

        Public ReadOnly Property IsDialogSearchRatingUnrated As Boolean
            Get
                Return _dialogSearchRatings.Contains(0)
            End Get
        End Property

        Public ReadOnly Property IsDialogSearchRating1 As Boolean
            Get
                Return _dialogSearchRatings.Contains(1)
            End Get
        End Property

        Public ReadOnly Property IsDialogSearchRating2 As Boolean
            Get
                Return _dialogSearchRatings.Contains(2)
            End Get
        End Property

        Public ReadOnly Property IsDialogSearchRating3 As Boolean
            Get
                Return _dialogSearchRatings.Contains(3)
            End Get
        End Property

        Public ReadOnly Property IsDialogSearchRating4 As Boolean
            Get
                Return _dialogSearchRatings.Contains(4)
            End Get
        End Property

        Public ReadOnly Property IsDialogSearchRating5 As Boolean
            Get
                Return _dialogSearchRatings.Contains(5)
            End Get
        End Property

        Public Property DialogKind As AppDialogKind
            Get
                Return _dialogKind
            End Get
            Set(value As AppDialogKind)
                Me.RaiseAndSetIfChanged(_dialogKind, value)
                Me.RaisePropertyChanged(NameOf(DialogShowsInput))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsOptions))
                ' MUSS hier stehen: ResetDialogSaveAsMetaOptions läuft VOR dem Öffnen, also bevor
                ' _dialogKind gesetzt ist - die Zeile „Übernehmen" wurde dort mit der Art des VORIGEN
                ' Dialogs gemeldet und blieb je nach Vorgeschichte weg (Nutzerbericht „mal drin, mal
                ' draußen" 2026-07-17).
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsMetaOptions))
                Me.RaisePropertyChanged(NameOf(DialogShowsFileConflict))
                Me.RaisePropertyChanged(NameOf(DialogShowsStandardActions))
                Me.RaisePropertyChanged(NameOf(DialogShowsBatchRename))
                Me.RaisePropertyChanged(NameOf(DialogShowsSearch))
                Me.RaisePropertyChanged(NameOf(DialogShowsBatchResize))
                Me.RaisePropertyChanged(NameOf(DialogShowsBatchFilter))
                Me.RaisePropertyChanged(NameOf(DialogShowsWatermarkPreset))
                Me.RaisePropertyChanged(NameOf(DialogUsesWideLayout))
                Me.RaisePropertyChanged(NameOf(DialogWidth))
                Me.RaisePropertyChanged(NameOf(IsDialogJpgQualityVisible))
            End Set
        End Property

        Public ReadOnly Property IsDialogOpen As Boolean
            Get
                Return _dialogCompletion IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property IsAppContentHitTestVisible As Boolean
            Get
                Return Not IsDialogOpen
            End Get
        End Property

        Public ReadOnly Property DialogShowsInput As Boolean
            Get
                Return _dialogKind <> AppDialogKind.Message AndAlso
                       _dialogKind <> AppDialogKind.FileConflict AndAlso
                       _dialogKind <> AppDialogKind.BatchRename AndAlso
                       _dialogKind <> AppDialogKind.Search AndAlso
                       _dialogKind <> AppDialogKind.BatchConvert AndAlso
                       _dialogKind <> AppDialogKind.BatchResize AndAlso
                       _dialogKind <> AppDialogKind.BatchFilter AndAlso
                       _dialogKind <> AppDialogKind.WatermarkPreset
            End Get
        End Property

        ''' Zeigt den Format+Qualität-Block - sowohl für "Speichern unter" (mit Dateiname) als auch
        ''' für die Stapel-Konvertierung (ohne Dateiname, DialogShowsInput ist dafür oben ausgeschlossen).
        ''' Format, Qualität, Ziel und Zielordner. Beim Stapel-Filter nur dann, wenn NEUE Dateien
        ''' geschrieben werden - beim Überschreiben behält jede Datei ihren Pfad und ihre Endung.
        Public ReadOnly Property DialogShowsSaveAsOptions As Boolean
            Get
                Return _dialogKind = AppDialogKind.SaveAs OrElse
                       _dialogKind = AppDialogKind.BatchConvert OrElse
                       (_dialogKind = AppDialogKind.BatchFilter AndAlso Not _dialogBatchFilterOverwrite) OrElse
                       (_dialogKind = AppDialogKind.WatermarkPreset AndAlso Not _dialogBatchWatermarkOverwrite) OrElse
                       (_dialogKind = AppDialogKind.BatchResize AndAlso Not _dialogBatchResizeOverwrite)
            End Get
        End Property

        Public ReadOnly Property DialogShowsBatchFilter As Boolean
            Get
                Return _dialogKind = AppDialogKind.BatchFilter
            End Get
        End Property

        ''' Einzeloptionen „Katalog-Metadaten übernehmen" (Nutzerwunsch 2026-07-17) - überall dort, wo
        ''' aus EINER Quelldatei eine neue Datei entsteht: Speichern-unter, Konvertieren-nach sowie
        ''' Bildgröße-ändern und Filter-anwenden als Kopie. Beim Überschreiben behält die Datei ihren
        ''' Katalog-Eintrag, dort wäre die Zeile sinnlos.
        Public ReadOnly Property DialogShowsSaveAsMetaOptions As Boolean
            Get
                Return _dialogKind = AppDialogKind.SaveAs OrElse
                       _dialogKind = AppDialogKind.BatchConvert OrElse
                       (_dialogKind = AppDialogKind.BatchResize AndAlso Not _dialogBatchResizeOverwrite) OrElse
                       (_dialogKind = AppDialogKind.WatermarkPreset AndAlso Not _dialogBatchWatermarkOverwrite) OrElse
                       (_dialogKind = AppDialogKind.BatchFilter AndAlso Not _dialogBatchFilterOverwrite)
            End Get
        End Property

        Private _dialogSaveAsCopyRating As Boolean = True
        Private _dialogSaveAsCopyFavorite As Boolean = True
        Private _dialogSaveAsCopyColorLabel As Boolean = True
        Private _dialogSaveAsCopyKeywords As Boolean = True

        Public Property DialogSaveAsCopyRating As Boolean
            Get
                Return _dialogSaveAsCopyRating
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogSaveAsCopyRating, value)
            End Set
        End Property

        Public Property DialogSaveAsCopyFavorite As Boolean
            Get
                Return _dialogSaveAsCopyFavorite
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogSaveAsCopyFavorite, value)
            End Set
        End Property

        Public Property DialogSaveAsCopyColorLabel As Boolean
            Get
                Return _dialogSaveAsCopyColorLabel
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogSaveAsCopyColorLabel, value)
            End Set
        End Property

        Public Property DialogSaveAsCopyKeywords As Boolean
            Get
                Return _dialogSaveAsCopyKeywords
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogSaveAsCopyKeywords, value)
            End Set
        End Property

        ''' Vom Dialog aufgerufen (Klick auf einen der Übernehmen-Buttons): Option umschalten.
        Public Sub ToggleDialogSaveAsMetaOption(kind As String)
            Select Case If(kind, "")
                Case "Rating"
                    DialogSaveAsCopyRating = Not DialogSaveAsCopyRating
                Case "Favorite"
                    DialogSaveAsCopyFavorite = Not DialogSaveAsCopyFavorite
                Case "Label"
                    DialogSaveAsCopyColorLabel = Not DialogSaveAsCopyColorLabel
                Case "Keywords"
                    DialogSaveAsCopyKeywords = Not DialogSaveAsCopyKeywords
            End Select
        End Sub

        Private Sub ResetDialogSaveAsMetaOptions()
            _dialogSaveAsCopyRating = True
            _dialogSaveAsCopyFavorite = True
            _dialogSaveAsCopyColorLabel = True
            _dialogSaveAsCopyKeywords = True
            For Each name In {NameOf(DialogSaveAsCopyRating), NameOf(DialogSaveAsCopyFavorite),
                              NameOf(DialogSaveAsCopyColorLabel), NameOf(DialogSaveAsCopyKeywords),
                              NameOf(DialogShowsSaveAsMetaOptions)}
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        Public ReadOnly Property DialogShowsFileConflict As Boolean
            Get
                Return _dialogKind = AppDialogKind.FileConflict
            End Get
        End Property

        Public ReadOnly Property DialogShowsStandardActions As Boolean
            Get
                Return Not DialogShowsFileConflict
            End Get
        End Property

        Public ReadOnly Property DialogShowsBatchRename As Boolean
            Get
                Return _dialogKind = AppDialogKind.BatchRename
            End Get
        End Property

        Public ReadOnly Property DialogShowsSearch As Boolean
            Get
                Return _dialogKind = AppDialogKind.Search
            End Get
        End Property

        Public ReadOnly Property DialogShowsBatchResize As Boolean
            Get
                Return _dialogKind = AppDialogKind.BatchResize
            End Get
        End Property

        Public ReadOnly Property DialogShowsWatermarkPreset As Boolean
            Get
                Return _dialogKind = AppDialogKind.WatermarkPreset
            End Get
        End Property

        ''' Speichern-unter und Konvertieren-nach zählen mit dazu: die Zeile „Übernehmen" trägt vier
        ''' Umschalt-Buttons nebeneinander, die bei 440 px nicht mehr aufs Formular passen
        ''' (Nutzerwunsch 2026-07-17).
        Public ReadOnly Property DialogUsesWideLayout As Boolean
            Get
                Return DialogShowsFileConflict OrElse DialogShowsBatchRename OrElse DialogShowsSearch OrElse
                       DialogShowsBatchResize OrElse DialogShowsSaveAsMetaOptions
            End Get
        End Property

        Public ReadOnly Property DialogWidth As Double
            Get
                If DialogShowsFileConflict OrElse DialogShowsBatchRename OrElse DialogShowsSearch Then Return 820
                If DialogShowsBatchResize Then Return 780
                If _dialogKind = AppDialogKind.SaveAs OrElse
                   _dialogKind = AppDialogKind.BatchConvert OrElse
                   _dialogKind = AppDialogKind.BatchFilter Then Return 570
                If _dialogKind = AppDialogKind.WatermarkPreset AndAlso DialogShowsSaveAsOptions Then Return 570
                Return 440
            End Get
        End Property

        Public Property DialogExistingFile As FileConflictInfo
            Get
                Return _dialogExistingFile
            End Get
            Set(value As FileConflictInfo)
                Me.RaiseAndSetIfChanged(_dialogExistingFile, value)
            End Set
        End Property

        Public Property DialogIncomingFile As FileConflictInfo
            Get
                Return _dialogIncomingFile
            End Get
            Set(value As FileConflictInfo)
                Me.RaiseAndSetIfChanged(_dialogIncomingFile, value)
            End Set
        End Property

        Public ReadOnly Property IsDialogJpgQualityVisible As Boolean
            Get
                Return DialogShowsSaveAsOptions AndAlso String.Equals(_dialogSelectedFormat, "JPG", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' Überschreiben blendet Format, Ziel und Namenszusatz aus - wie beim Stapel-Filter.
        Public Property DialogBatchResizeOverwrite As Boolean
            Get
                Return _dialogBatchResizeOverwrite
            End Get
            Set(value As Boolean)
                If _dialogBatchResizeOverwrite = value Then Return
                _dialogBatchResizeOverwrite = value
                ' Die Wahl bleibt über Sitzungen erhalten (Nutzerwunsch 2026-07-17).
                AppSettingsService.SaveBatchResizeOverwriteOriginals(value)
                Me.RaisePropertyChanged(NameOf(DialogBatchResizeOverwrite))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsOptions))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsMetaOptions))
                Me.RaisePropertyChanged(NameOf(DialogWidth))
                Me.RaisePropertyChanged(NameOf(IsDialogJpgQualityVisible))
                Me.RaisePropertyChanged(NameOf(IsDialogResizeAppendSizeVisible))
            End Set
        End Property

        Public Property DialogBatchResizeAppendSize As Boolean
            Get
                Return _dialogBatchResizeAppendSize
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogBatchResizeAppendSize, value)
            End Set
        End Property

        Public ReadOnly Property IsDialogResizeAppendSizeVisible As Boolean
            Get
                Return Not _dialogBatchResizeOverwrite
            End Get
        End Property

        Public ReadOnly Property DialogBatchResizeInterpolationOptions As IReadOnlyList(Of String)
            Get
                Return New String() {"Nächstgelegen", "Bilinear", "Bikubisch"}
            End Get
        End Property

        Public Property DialogBatchResizeWidthText As String
            Get
                Return _dialogBatchResizeWidthText
            End Get
            Set(value As String)
                _dialogBatchResizeScalePercent = 0
                Dim normalized = NormalizeResizeDimensionText(value)
                If Me.RaiseAndSetIfChanged(_dialogBatchResizeWidthText, normalized) Then
                    If _dialogBatchResizeLockAspect Then UpdateDialogBatchResizeHeightFromWidth()
                End If
            End Set
        End Property

        Public Property DialogBatchResizeHeightText As String
            Get
                Return _dialogBatchResizeHeightText
            End Get
            Set(value As String)
                _dialogBatchResizeScalePercent = 0
                Dim normalized = NormalizeResizeDimensionText(value)
                If Me.RaiseAndSetIfChanged(_dialogBatchResizeHeightText, normalized) Then
                    If _dialogBatchResizeLockAspect Then UpdateDialogBatchResizeWidthFromHeight()
                End If
            End Set
        End Property

        Public Property DialogBatchResizeLockAspect As Boolean
            Get
                Return _dialogBatchResizeLockAspect
            End Get
            Set(value As Boolean)
                If Me.RaiseAndSetIfChanged(_dialogBatchResizeLockAspect, value) AndAlso value Then
                    If Not String.IsNullOrWhiteSpace(_dialogBatchResizeWidthText) Then
                        UpdateDialogBatchResizeHeightFromWidth()
                    ElseIf Not String.IsNullOrWhiteSpace(_dialogBatchResizeHeightText) Then
                        UpdateDialogBatchResizeWidthFromHeight()
                    End If
                End If
            End Set
        End Property

        Public Property DialogBatchResizeInterpolationLabel As String
            Get
                Select Case _dialogBatchResizeInterpolation
                    Case ResizeInterpolationMode.Nearest
                        Return "Nächstgelegen"
                    Case ResizeInterpolationMode.Bicubic
                        Return "Bikubisch"
                    Case Else
                        Return "Bilinear"
                End Select
            End Get
            Set(value As String)
                Select Case value
                    Case "Nächstgelegen"
                        _dialogBatchResizeInterpolation = ResizeInterpolationMode.Nearest
                    Case "Bikubisch"
                        _dialogBatchResizeInterpolation = ResizeInterpolationMode.Bicubic
                    Case Else
                        _dialogBatchResizeInterpolation = ResizeInterpolationMode.Bilinear
                End Select
                Me.RaisePropertyChanged(NameOf(DialogBatchResizeInterpolationLabel))
            End Set
        End Property

        Public Property DialogSelectedWatermarkPresetName As String
            Get
                Return _dialogSelectedWatermarkPresetName
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogSelectedWatermarkPresetName, If(value, "").Trim())
            End Set
        End Property

        Public Property DialogBatchWatermarkOverwrite As Boolean
            Get
                Return _dialogBatchWatermarkOverwrite
            End Get
            Set(value As Boolean)
                If _dialogBatchWatermarkOverwrite = value Then Return
                _dialogBatchWatermarkOverwrite = value
                AppSettingsService.SaveBatchWatermarkOverwriteOriginals(value)
                Me.RaisePropertyChanged(NameOf(DialogBatchWatermarkOverwrite))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsOptions))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsMetaOptions))
                Me.RaisePropertyChanged(NameOf(DialogWidth))
                Me.RaisePropertyChanged(NameOf(IsDialogJpgQualityVisible))
            End Set
        End Property

        Public Sub SetDialogBatchResizePreset(preset As String)
            _dialogBatchResizeScalePercent = 0
            Select Case If(preset, "")
                Case "Original"
                    If _dialogBatchResizeSourceWidth > 0 AndAlso _dialogBatchResizeSourceHeight > 0 Then
                        _dialogBatchResizeWidthText = _dialogBatchResizeSourceWidth.ToString(CultureInfo.InvariantCulture)
                        _dialogBatchResizeHeightText = _dialogBatchResizeSourceHeight.ToString(CultureInfo.InvariantCulture)
                    Else
                        _dialogBatchResizeWidthText = ""
                        _dialogBatchResizeHeightText = ""
                    End If
                Case "75%"
                    _dialogBatchResizeScalePercent = 75
                    SetDialogBatchResizeTextsFromScale(_dialogBatchResizeScalePercent)
                Case "50%"
                    _dialogBatchResizeScalePercent = 50
                    SetDialogBatchResizeTextsFromScale(_dialogBatchResizeScalePercent)
                Case "25%"
                    _dialogBatchResizeScalePercent = 25
                    SetDialogBatchResizeTextsFromScale(_dialogBatchResizeScalePercent)
                Case "UHD"
                    _dialogBatchResizeWidthText = "3840"
                    _dialogBatchResizeHeightText = "2160"
                Case "Full-HD"
                    _dialogBatchResizeWidthText = "1920"
                    _dialogBatchResizeHeightText = "1080"
                Case "SD"
                    _dialogBatchResizeWidthText = "1280"
                    _dialogBatchResizeHeightText = "720"
                Case Else
                    Dim edge As Integer
                    If Integer.TryParse(preset, edge) AndAlso edge > 0 Then
                        _dialogBatchResizeWidthText = edge.ToString(CultureInfo.InvariantCulture)
                        _dialogBatchResizeHeightText = ""
                        If _dialogBatchResizeLockAspect Then UpdateDialogBatchResizeHeightFromWidth()
                    End If
            End Select
            RaiseDialogBatchResizeProperties()
        End Sub

        Private Sub RaiseDialogBatchResizeProperties()
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeWidthText))
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeHeightText))
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeLockAspect))
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeInterpolationLabel))
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeOverwrite))
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeAppendSize))
            Me.RaisePropertyChanged(NameOf(IsDialogResizeAppendSizeVisible))
            Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsOptions))
            Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsMetaOptions))
            Me.RaisePropertyChanged(NameOf(DialogWidth))
            Me.RaisePropertyChanged(NameOf(IsDialogJpgQualityVisible))
            Me.RaisePropertyChanged(NameOf(IsSaveAsImmichAvailable))
        End Sub

        Private Shared Function NormalizeResizeDimensionText(value As String) As String
            If String.IsNullOrWhiteSpace(value) Then Return ""

            Dim parsed As Integer
            If Integer.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, parsed) OrElse
               Integer.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, parsed) Then
                Return Math.Max(0, Math.Min(50000, parsed)).ToString(CultureInfo.InvariantCulture)
            End If

            Return ""
        End Function

        Private Shared Function ParseResizeDimension(value As String) As Integer
            Dim parsed As Integer
            If Integer.TryParse(If(value, "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, parsed) OrElse
               Integer.TryParse(If(value, "").Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, parsed) Then
                Return Math.Max(0, Math.Min(50000, parsed))
            End If

            Return 0
        End Function

        Private Sub UpdateDialogBatchResizeHeightFromWidth()
            If _dialogBatchResizeSourceWidth <= 0 OrElse _dialogBatchResizeSourceHeight <= 0 Then Return

            Dim width = ParseResizeDimension(_dialogBatchResizeWidthText)
            If width <= 0 Then
                _dialogBatchResizeHeightText = ""
            Else
                Dim height = Math.Max(1, CInt(Math.Round(width * _dialogBatchResizeSourceHeight / CDbl(_dialogBatchResizeSourceWidth))))
                _dialogBatchResizeHeightText = height.ToString(CultureInfo.InvariantCulture)
            End If

            Me.RaisePropertyChanged(NameOf(DialogBatchResizeHeightText))
        End Sub

        Private Sub UpdateDialogBatchResizeWidthFromHeight()
            If _dialogBatchResizeSourceWidth <= 0 OrElse _dialogBatchResizeSourceHeight <= 0 Then Return

            Dim height = ParseResizeDimension(_dialogBatchResizeHeightText)
            If height <= 0 Then
                _dialogBatchResizeWidthText = ""
            Else
                Dim width = Math.Max(1, CInt(Math.Round(height * _dialogBatchResizeSourceWidth / CDbl(_dialogBatchResizeSourceHeight))))
                _dialogBatchResizeWidthText = width.ToString(CultureInfo.InvariantCulture)
            End If

            Me.RaisePropertyChanged(NameOf(DialogBatchResizeWidthText))
        End Sub

        Private Sub SetDialogBatchResizeTextsFromScale(scalePercent As Integer)
            If _dialogBatchResizeSourceWidth <= 0 OrElse _dialogBatchResizeSourceHeight <= 0 OrElse scalePercent <= 0 Then
                _dialogBatchResizeWidthText = ""
                _dialogBatchResizeHeightText = ""
                Return
            End If

            Dim width = Math.Max(1, CInt(Math.Round(_dialogBatchResizeSourceWidth * scalePercent / 100.0)))
            Dim height = Math.Max(1, CInt(Math.Round(_dialogBatchResizeSourceHeight * scalePercent / 100.0)))
            _dialogBatchResizeWidthText = width.ToString(CultureInfo.InvariantCulture)
            _dialogBatchResizeHeightText = height.ToString(CultureInfo.InvariantCulture)
        End Sub

        Public Async Function ShowMessageAsync(titleText As String, messageText As String, Optional confirmText As String = "OK") As Task
            Await ShowDialogAsync(AppDialogKind.Message, titleText, messageText, "", confirmText, "")
        End Function

        Public Async Function ShowConfirmAsync(titleText As String, messageText As String, Optional confirmText As String = "OK", Optional cancelText As String = "Abbrechen") As Task(Of Boolean)
            Dim result = Await ShowDialogAsync(AppDialogKind.Message, titleText, messageText, "", confirmText, cancelText)
            Return result IsNot Nothing
        End Function

        Public Function ShowInputAsync(kind As AppDialogKind, titleText As String, messageText As String, initialText As String, Optional confirmText As String = "OK", Optional cancelText As String = "Abbrechen") As Task(Of String)
            Return ShowDialogAsync(kind, titleText, messageText, initialText, confirmText, cancelText)
        End Function

        ''' <param name="currentFolder">Der Ordner, in dem die Galerie gerade steht - Vorgabe für den
        ''' Zielordner, wenn Kopien geschrieben werden (wie beim Stapel-Filter).</param>
        Public Async Function ShowBatchResizeAsync(Optional samplePath As String = Nothing, Optional currentFolder As String = "") As Task(Of BatchResizeResult)
            Dim settings = AppSettingsService.Load()
            _dialogBatchResizeWidthText = If(settings.LastBatchResizeWidth > 0, settings.LastBatchResizeWidth.ToString(CultureInfo.InvariantCulture), "")
            _dialogBatchResizeHeightText = If(settings.LastBatchResizeHeight > 0, settings.LastBatchResizeHeight.ToString(CultureInfo.InvariantCulture), "")
            _dialogBatchResizeLockAspect = settings.LastBatchResizeLockAspect
            _dialogBatchResizeInterpolation = ParseResizeInterpolationMode(settings.LastBatchResizeInterpolation)
            _dialogBatchResizeScalePercent = settings.LastBatchResizeScalePercent
            _dialogBatchResizeSourceWidth = 0
            _dialogBatchResizeSourceHeight = 0
            _dialogBatchResizeOverwrite = settings.BatchResizeOverwriteOriginals
            _dialogBatchResizeAppendSize = True
            ResetDialogSaveAsMetaOptions()
            DialogSelectedFormat = NormalizeSaveAsFormat("JPG")
            DialogJpgQuality = 90
            DialogSaveAsTarget = "Local"
            DialogSaveAsTargetFolder = If(Not String.IsNullOrWhiteSpace(currentFolder) AndAlso Directory.Exists(currentFolder),
                                          currentFolder,
                                          ResolveDefaultSaveAsTargetFolder())

            If Not String.IsNullOrWhiteSpace(samplePath) AndAlso File.Exists(samplePath) Then
                Try
                    Dim size = ImageProcessor.GetImageSize(samplePath)
                    _dialogBatchResizeSourceWidth = size.Width
                    _dialogBatchResizeSourceHeight = size.Height
                Catch
                End Try
            End If

            If _dialogBatchResizeScalePercent > 0 Then
                SetDialogBatchResizeTextsFromScale(_dialogBatchResizeScalePercent)
            ElseIf _dialogBatchResizeLockAspect AndAlso Not String.IsNullOrWhiteSpace(_dialogBatchResizeWidthText) Then
                UpdateDialogBatchResizeHeightFromWidth()
            ElseIf _dialogBatchResizeLockAspect AndAlso Not String.IsNullOrWhiteSpace(_dialogBatchResizeHeightText) Then
                UpdateDialogBatchResizeWidthFromHeight()
            End If

            RaiseDialogBatchResizeProperties()

            Dim result = Await ShowDialogAsync(AppDialogKind.BatchResize,
                                               "Bildgröße ändern",
                                               "Lege Zielgröße und Neuberechnung für die ausgewählten Bilder fest.",
                                               "",
                                               "Anwenden",
                                               "Abbrechen")
            If result Is Nothing Then Return Nothing
            Dim width = ParseResizeDimension(_dialogBatchResizeWidthText)
            Dim height = ParseResizeDimension(_dialogBatchResizeHeightText)

            If _dialogBatchResizeLockAspect AndAlso width > 0 AndAlso height <= 0 AndAlso _dialogBatchResizeSourceWidth > 0 AndAlso _dialogBatchResizeSourceHeight > 0 Then
                height = Math.Max(1, CInt(Math.Round(width * _dialogBatchResizeSourceHeight / CDbl(_dialogBatchResizeSourceWidth))))
            End If

            If _dialogBatchResizeLockAspect AndAlso height > 0 AndAlso width <= 0 AndAlso _dialogBatchResizeSourceWidth > 0 AndAlso _dialogBatchResizeSourceHeight > 0 Then
                width = Math.Max(1, CInt(Math.Round(height * _dialogBatchResizeSourceWidth / CDbl(_dialogBatchResizeSourceHeight))))
            End If

            If _dialogBatchResizeScalePercent <= 0 AndAlso width <= 0 AndAlso height <= 0 Then Return Nothing
            AppSettingsService.SaveLastBatchResizeSettings(width, height, _dialogBatchResizeScalePercent, _dialogBatchResizeLockAspect, _dialogBatchResizeInterpolation)
            If Not _dialogBatchResizeOverwrite Then PersistDialogTargetFolderIfLocal()
            Return New BatchResizeResult With {
                .Width = width,
                .Height = height,
                .ScalePercent = _dialogBatchResizeScalePercent,
                .LockAspect = _dialogBatchResizeLockAspect,
                .Interpolation = _dialogBatchResizeInterpolation,
                .Overwrite = _dialogBatchResizeOverwrite,
                .AppendSizeToFileName = _dialogBatchResizeAppendSize,
                .Format = DialogSelectedFormat,
                .JpgQuality = DialogJpgQuality,
                .Target = DialogSaveAsTarget,
                .TargetFolder = DialogSaveAsTargetFolder,
                .CopyRating = _dialogSaveAsCopyRating,
                .CopyFavorite = _dialogSaveAsCopyFavorite,
                .CopyColorLabel = _dialogSaveAsCopyColorLabel,
                .CopyKeywords = _dialogSaveAsCopyKeywords
            }
        End Function

        Public Async Function ShowWatermarkPresetDialogAsync() As Task(Of WatermarkPresetDialogResult)
            Dim settings = AppSettingsService.Load()
            SetDialogFormats(includeFpx:=False)
            _dialogWatermarkPresets.Clear()
            DialogWatermarkPresetNames.Clear()
            _dialogBatchWatermarkOverwrite = settings.BatchWatermarkOverwriteOriginals
            ResetDialogSaveAsMetaOptions()
            DialogSelectedFormat = NormalizeSaveAsFormat("JPG")
            DialogJpgQuality = 90
            DialogSaveAsTarget = "Local"
            DialogSaveAsTargetFolder = ResolveDefaultSaveAsTargetFolder()

            For Each preset In settings.WatermarkPresets
                _dialogWatermarkPresets.Add(preset)
                DialogWatermarkPresetNames.Add(preset.Name)
            Next

            If _dialogWatermarkPresets.Count = 0 Then
                Await ShowMessageAsync("Wasserzeichen anwenden", "Es ist kein gespeichertes Wasserzeichen vorhanden.")
                Return Nothing
            End If

            Dim lastName = AppSettingsService.NormalizePresetName(settings.LastWatermarkPresetName)
            Dim selectedPreset = _dialogWatermarkPresets.FirstOrDefault(Function(p) String.Equals(p.Name, lastName, StringComparison.OrdinalIgnoreCase))
            If selectedPreset Is Nothing Then selectedPreset = _dialogWatermarkPresets(0)
            DialogSelectedWatermarkPresetName = selectedPreset.Name
            For Each name In {NameOf(DialogBatchWatermarkOverwrite), NameOf(DialogShowsSaveAsOptions),
                              NameOf(DialogShowsSaveAsMetaOptions), NameOf(DialogWidth),
                              NameOf(IsDialogJpgQualityVisible), NameOf(IsSaveAsImmichAvailable)}
                Me.RaisePropertyChanged(name)
            Next

            Dim result = Await ShowDialogAsync(AppDialogKind.WatermarkPreset,
                                               "Wasserzeichen anwenden",
                                               "Wähle ein gespeichertes Wasserzeichen für die ausgewählten Bilder aus.",
                                               "",
                                               "Anwenden",
                                               "Abbrechen")
            If result Is Nothing Then Return Nothing

            selectedPreset = _dialogWatermarkPresets.FirstOrDefault(Function(p) String.Equals(p.Name, DialogSelectedWatermarkPresetName, StringComparison.OrdinalIgnoreCase))
            If selectedPreset Is Nothing Then Return Nothing

            AppSettingsService.SaveLastWatermarkPresetName(selectedPreset.Name)
            If Not _dialogBatchWatermarkOverwrite Then PersistDialogTargetFolderIfLocal()
            Return New WatermarkPresetDialogResult With {
                .Preset = selectedPreset,
                .Overwrite = _dialogBatchWatermarkOverwrite,
                .Format = DialogSelectedFormat,
                .JpgQuality = DialogJpgQuality,
                .Target = DialogSaveAsTarget,
                .TargetFolder = DialogSaveAsTargetFolder,
                .CopyRating = _dialogSaveAsCopyRating,
                .CopyFavorite = _dialogSaveAsCopyFavorite,
                .CopyColorLabel = _dialogSaveAsCopyColorLabel,
                .CopyKeywords = _dialogSaveAsCopyKeywords
            }
        End Function

        Private Shared Function ParseResizeInterpolationMode(value As String) As ResizeInterpolationMode
            Select Case AppSettingsService.NormalizeResizeInterpolationModeName(value)
                Case "Nearest"
                    Return ResizeInterpolationMode.Nearest
                Case "Bicubic"
                    Return ResizeInterpolationMode.Bicubic
                Case Else
                    Return ResizeInterpolationMode.Bilinear
            End Select
        End Function

        ''' prefill: Ist eine bestehende Suchliste angegeben, wird der Dialog mit deren Parametern
        ''' vorbelegt (Bearbeiten-Modus) statt mit den Standardwerten (Neuanlage).
        Public Async Function ShowSearchDialogAsync(initialText As String, Optional prefill As SearchListEntry = Nothing) As Task(Of SearchDialogResult)
            Dim isEdit = prefill IsNot Nothing
            _dialogSearchRatings.Clear()
            DialogSearchConditions.Clear()
            Me.RaisePropertyChanged(NameOf(IsDialogImmichAvailable))
            If isEdit Then
                DialogSearchName = If(prefill.Name, "")
                DialogSearchSource = If(prefill.Source, "Local")
                DialogSearchText = If(prefill.TextQuery, "").Trim()
                DialogSearchRootFolder = If(prefill.RootFolder, "")
                DialogSearchIncludeSubfolders = prefill.IncludeSubfolders
                DialogSearchFavoriteMode = If(prefill.FavoriteMode, "Any")
                DialogSearchRatingMin = -1
                For Each r In If(prefill.Ratings, New List(Of Integer)())
                    _dialogSearchRatings.Add(r)
                Next
                For Each c In If(prefill.Conditions, New List(Of SearchCondition)())
                    DialogSearchConditions.Add(New SearchCondition With {.Field = c.Field, .Operator = c.Operator, .Value = c.Value})
                Next
                DialogSearchConditionCombinator = If(prefill.ConditionCombinator, "AND")
            Else
                DialogSearchName = ""
                DialogSearchSource = "Local"
                DialogSearchText = If(initialText, "").Trim()
                DialogSearchRootFolder = ""
                DialogSearchIncludeSubfolders = True
                DialogSearchFavoriteMode = "Any"
                DialogSearchRatingMin = -1
                DialogSearchConditionCombinator = "AND"
            End If
            RaiseDialogSearchRatingState()

            Dim result = Await ShowDialogAsync(AppDialogKind.Search,
                                               If(isEdit, "Suche bearbeiten", "Suchen"),
                                               If(isEdit,
                                                  "Passe die Suchparameter an. Die Änderungen werden im Bereich Suchen gespeichert.",
                                                  "Lege Suchparameter fest. Die Suche wird im Bereich Suchen gespeichert."),
                                               "",
                                               If(isEdit, "Speichern", "Suchen"),
                                               "Abbrechen")
            If result Is Nothing Then Return Nothing

            Dim name = DialogSearchName.Trim()
            Dim textQuery = DialogSearchText.Trim()
            Dim rootFolder = DialogSearchRootFolder.Trim()
            If Not String.IsNullOrWhiteSpace(rootFolder) AndAlso Not Directory.Exists(rootFolder) Then
                Await ShowMessageAsync("Suche fehlgeschlagen", "Bitte wähle einen gültigen Startordner.")
                Return Nothing
            End If
            If String.IsNullOrWhiteSpace(name) Then
                name = BuildSearchDisplayName(textQuery, DialogSearchFavoriteMode, _dialogSearchRatings, rootFolder)
            End If
            If String.IsNullOrWhiteSpace(name) Then Return Nothing

            Return New SearchDialogResult With {
                .Name = name,
                .Source = DialogSearchSource,
                .TextQuery = textQuery,
                .RootFolder = rootFolder,
                .IncludeSubfolders = DialogSearchIncludeSubfolders,
                .FavoriteMode = DialogSearchFavoriteMode,
                .RatingMin = -1,
                .Ratings = _dialogSearchRatings.OrderBy(Function(r) r).ToList(),
                .Conditions = DialogSearchConditions.
                    Where(Function(c) Not String.IsNullOrWhiteSpace(c.Value)).
                    Select(Function(c) New SearchCondition With {.Field = c.Field, .Operator = c.Operator, .Value = c.Value.Trim()}).
                    ToList(),
                .ConditionCombinator = DialogSearchConditionCombinator
            }
        End Function

        Public Async Function ShowBatchRenameAsync(paths As IEnumerable(Of String)) As Task(Of BatchRenameResult)
            _dialogBatchRenamePaths = If(paths, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrEmpty(p) AndAlso (IO.File.Exists(p) OrElse IO.Directory.Exists(p))).
                ToList()
            If _dialogBatchRenamePaths.Count < 2 Then Return Nothing

            ' EXIF wird pro Datei nur einmal beim Öffnen gelesen und für die Dauer des Dialogs
            ' gecacht - RebuildBatchRenamePreview läuft sonst bei jedem Tastenanschlag im
            ' Muster-Textfeld erneut über alle Dateien.
            _dialogBatchRenameExifCache.Clear()
            Await Task.Run(Sub()
                For Each p In _dialogBatchRenamePaths
                    If Not IO.File.Exists(p) Then Continue For
                    Dim data = ExifService.ReadExif(p)
                    _dialogBatchRenameExifCache(p) = data
                    ' Dieser Lesevorgang war ohnehin fällig (Muster-Vorschau) - direkt mit dem
                    ' Katalog-Eintrag abgleichen, statt die Gelegenheit ungenutzt verstreichen zu lassen.
                    Dim searchFields = ExifService.ExtractSearchFields(data, p)
                    LibraryService.Instance.SyncExifData(p, searchFields, ExifService.BuildCatalogSummary(data, searchFields))
                Next
            End Sub)

            Dim settings = AppSettingsService.Load()
            DialogBatchRenamePattern = settings.LastBatchRenamePattern
            DialogBatchRenameStart = settings.LastBatchRenameStart
            DialogBatchRenameStep = settings.LastBatchRenameStep
            RebuildBatchRenamePreview()

            Dim result = Await ShowDialogAsync(AppDialogKind.BatchRename,
                                               $"Umbenennen ({_dialogBatchRenamePaths.Count} Dateien)",
                                               "Lege eine Namensvorlage fest und prüfe die Vorschau vor dem Umbenennen.",
                                               "",
                                               "Umbenennen",
                                               "Abbrechen")
            If result Is Nothing Then Return Nothing

            AppSettingsService.SaveLastBatchRenameSettings(DialogBatchRenamePattern, DialogBatchRenameStart, DialogBatchRenameStep)
            RebuildBatchRenamePreview()
            If DialogBatchRenamePreview.Any(Function(i) i.HasProblem) Then
                Await ShowMessageAsync("Stapel-Umbenennen fehlgeschlagen", "Bitte behebe Namenskonflikte oder ungültige Namen in der Vorschau.")
                Return Nothing
            End If

            Return New BatchRenameResult With {
                .Mappings = DialogBatchRenamePreview.
                    Where(Function(i) Not String.Equals(i.SourcePath, i.TargetPath, StringComparison.OrdinalIgnoreCase)).
                    Select(Function(i) New BatchRenameMapping With {.SourcePath = i.SourcePath, .TargetPath = i.TargetPath}).
                    ToList()
            }
        End Function

        Public Async Function ShowFileConflictAsync(existingPath As String, incomingPath As String) As Task(Of FileConflictDialogResult)
            DialogExistingFile = FileConflictInfo.FromPath(existingPath)
            DialogIncomingFile = FileConflictInfo.FromPath(incomingPath)
            DialogConflictRenameText = CreateUniqueConflictName(existingPath)
            Dim result = Await ShowDialogAsync(AppDialogKind.FileConflict,
                                               "Datei überschreiben?",
                                               "Eine Datei mit diesem Namen existiert bereits. Möchten Sie die bestehende Datei wirklich überschreiben?",
                                               "",
                                               "Überschreiben",
                                               "Abbrechen")
            Select Case result
                Case "Overwrite"
                    Return New FileConflictDialogResult With {.Choice = FileConflictChoice.Overwrite}
                Case "OverwriteAll"
                    Return New FileConflictDialogResult With {.Choice = FileConflictChoice.OverwriteAll}
                Case "Rename"
                    Return New FileConflictDialogResult With {.Choice = FileConflictChoice.Rename, .NewName = DialogConflictRenameText}
                Case "Skip"
                    Return New FileConflictDialogResult With {.Choice = FileConflictChoice.Skip}
                Case "SkipAll"
                    Return New FileConflictDialogResult With {.Choice = FileConflictChoice.SkipAll}
                Case Else
                    Return New FileConflictDialogResult With {.Choice = FileConflictChoice.Cancel}
            End Select
        End Function

        Public Async Function ShowSaveAsAsync(titleText As String,
                                             messageText As String,
                                             initialBaseName As String,
                                             initialFormat As String,
                                             Optional initialJpgQuality As Integer = 90,
                                             Optional confirmText As String = "Speichern",
                                             Optional cancelText As String = "Abbrechen") As Task(Of SaveAsDialogResult)
            SetDialogFormats(includeFpx:=True)
            DialogSelectedFormat = NormalizeSaveAsFormat(initialFormat)
            DialogJpgQuality = initialJpgQuality
            DialogSaveAsTarget = "Local"
            DialogSaveAsTargetFolder = ResolveDefaultSaveAsTargetFolder()
            ResetDialogSaveAsMetaOptions()
            Me.RaisePropertyChanged(NameOf(IsSaveAsImmichAvailable))

            Dim result = Await ShowDialogAsync(AppDialogKind.SaveAs, titleText, messageText, initialBaseName, confirmText, cancelText)
            If result Is Nothing Then Return Nothing

            PersistDialogTargetFolderIfLocal()
            Return New SaveAsDialogResult With {
                .BaseName = result.Trim(),
                .Format = DialogSelectedFormat,
                .JpgQuality = DialogJpgQuality,
                .Target = DialogSaveAsTarget,
                .TargetFolder = DialogSaveAsTargetFolder,
                .CopyRating = _dialogSaveAsCopyRating,
                .CopyFavorite = _dialogSaveAsCopyFavorite,
                .CopyColorLabel = _dialogSaveAsCopyColorLabel,
                .CopyKeywords = _dialogSaveAsCopyKeywords
            }
        End Function

        ''' Wiederverwendet denselben Format+Qualität-Block wie ShowSaveAsAsync, aber ohne
        ''' Dateinamen-Feld (BatchConvert lässt die Originalnamen unangetastet, ändert nur die Endung).
        Public Async Function ShowBatchConvertAsync(fileCount As Integer, initialFormat As String, Optional initialJpgQuality As Integer = 90) As Task(Of SaveAsDialogResult)
            SetDialogFormats(includeFpx:=False)
            DialogSelectedFormat = NormalizeSaveAsFormat(initialFormat)
            DialogJpgQuality = initialJpgQuality
            DialogSaveAsTarget = "Local"
            DialogSaveAsTargetFolder = ResolveDefaultSaveAsTargetFolder()
            ResetDialogSaveAsMetaOptions()
            Me.RaisePropertyChanged(NameOf(IsSaveAsImmichAvailable))

            Dim result = Await ShowDialogAsync(AppDialogKind.BatchConvert,
                                               $"In anderes Format konvertieren ({fileCount} Dateien)",
                                               "Wähle das Zielformat. Die Dateien werden mit neuer Endung im selben Ordner gespeichert.",
                                               "",
                                               "Konvertieren",
                                               "Abbrechen")
            If result Is Nothing Then Return Nothing

            PersistDialogTargetFolderIfLocal()
            Return New SaveAsDialogResult With {
                .Format = DialogSelectedFormat,
                .JpgQuality = DialogJpgQuality,
                .Target = DialogSaveAsTarget,
                .TargetFolder = DialogSaveAsTargetFolder,
                .CopyRating = _dialogSaveAsCopyRating,
                .CopyFavorite = _dialogSaveAsCopyFavorite,
                .CopyColorLabel = _dialogSaveAsCopyColorLabel,
                .CopyKeywords = _dialogSaveAsCopyKeywords
            }
        End Function

        Private Function ResolveDefaultSaveAsTargetFolder() As String
            Dim settings = AppSettingsService.Load()
            If Directory.Exists(settings.LastSaveAsTargetFolder) Then Return settings.LastSaveAsTargetFolder
            If Directory.Exists(settings.LastGalleryFolder) Then Return settings.LastGalleryFolder
            If Directory.Exists(settings.GalleryStartupCustomFolder) Then Return settings.GalleryStartupCustomFolder
            Return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        End Function

        Private Sub PersistDialogTargetFolderIfLocal()
            If Not IsSaveAsTargetLocal Then Return
            Dim folder = AppSettingsService.NormalizeFolderPath(DialogSaveAsTargetFolder)
            If String.IsNullOrWhiteSpace(folder) Then Return
            AppSettingsService.SaveLastSaveAsTargetFolder(folder)
        End Sub

        Private Function ShowDialogAsync(kind As AppDialogKind, titleText As String, messageText As String, initialText As String, confirmText As String, cancelText As String) As Task(Of String)
            If _dialogCompletion IsNot Nothing Then
                _dialogCompletion.TrySetResult(Nothing)
            End If

            _dialogCompletion = New TaskCompletionSource(Of String)()
            DialogKind = kind
            DialogTitle = LocalizationService.T(titleText)
            DialogMessage = LocalizationService.T(messageText)
            DialogInputText = initialText
            DialogConfirmText = LocalizationService.T(confirmText)
            DialogCancelText = LocalizationService.T(cancelText)
            Me.RaisePropertyChanged(NameOf(IsDialogOpen))
            Me.RaisePropertyChanged(NameOf(IsAppContentHitTestVisible))
            Me.RaisePropertyChanged(NameOf(IsDialogCancelVisible))
            Return _dialogCompletion.Task
        End Function

        Public Sub ConfirmDialog()
            If DialogShowsFileConflict Then
                CompleteDialog("Overwrite")
            ElseIf DialogShowsBatchRename Then
                CompleteDialog("BatchRename")
            ElseIf DialogShowsSearch Then
                CompleteDialog("Search")
            ElseIf DialogShowsBatchResize Then
                CompleteDialog("BatchResize")
            ElseIf DialogShowsWatermarkPreset Then
                CompleteDialog(DialogSelectedWatermarkPresetName)
            Else
                CompleteDialog(DialogInputText)
            End If
        End Sub

        Public Sub SetDialogSearchFavoriteMode(mode As String)
            DialogSearchFavoriteMode = mode
        End Sub

        Public Sub SetDialogSearchRatingMin(valueText As String)
            ToggleDialogSearchRating(valueText)
        End Sub

        Private Sub RaiseDialogSearchFavoriteState()
            Me.RaisePropertyChanged(NameOf(IsDialogSearchFavoriteOnly))
            Me.RaisePropertyChanged(NameOf(IsDialogSearchFavoriteNot))
        End Sub

        Private Sub RaiseDialogSearchRatingState()
            Me.RaisePropertyChanged(NameOf(DialogSearchRatingLabel))
            Me.RaisePropertyChanged(NameOf(IsDialogSearchRatingAll))
            Me.RaisePropertyChanged(NameOf(IsDialogSearchRatingUnrated))
            Me.RaisePropertyChanged(NameOf(IsDialogSearchRating1))
            Me.RaisePropertyChanged(NameOf(IsDialogSearchRating2))
            Me.RaisePropertyChanged(NameOf(IsDialogSearchRating3))
            Me.RaisePropertyChanged(NameOf(IsDialogSearchRating4))
            Me.RaisePropertyChanged(NameOf(IsDialogSearchRating5))
        End Sub

        Private Shared Function BuildSearchDisplayName(textQuery As String, favoriteMode As String, ratings As IEnumerable(Of Integer), rootFolder As String) As String
            Dim parts As New List(Of String)()
            textQuery = If(textQuery, "").Trim()
            If Not String.IsNullOrWhiteSpace(textQuery) Then parts.Add(textQuery)
            Select Case AppSettingsService.NormalizeSearchFavoriteMode(favoriteMode)
                Case "Only"
                    parts.Add("Favoriten")
                Case "Not"
                    parts.Add("Ohne Favoriten")
            End Select
            Dim ratingList = If(ratings, Enumerable.Empty(Of Integer)()).
                Select(Function(r) Math.Max(0, Math.Min(5, r))).
                Distinct().
                OrderBy(Function(r) r).
                ToList()
            If ratingList.Count > 0 Then
                parts.Add(String.Join(", ", ratingList.Select(Function(r)
                    If r = 0 Then Return "Nicht bewertet"
                    Return If(r = 1, "1 Stern", $"{r} Sterne")
                End Function)))
            End If
            If parts.Count = 0 AndAlso Not String.IsNullOrWhiteSpace(rootFolder) Then
                Dim folderName = Path.GetFileName(rootFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                parts.Add(If(String.IsNullOrWhiteSpace(folderName), rootFolder, folderName))
            End If
            If parts.Count = 0 Then parts.Add("Katalog")
            Return String.Join(" · ", parts)
        End Function

        Public Sub SkipDialog()
            CompleteDialog("Skip")
        End Sub

        Public Sub RenameConflictDialog()
            CompleteDialog("Rename")
        End Sub

        Public Sub CancelDialog()
            CompleteDialog(Nothing)
        End Sub

        Private Sub CompleteDialog(result As String)
            Dim completion = _dialogCompletion
            If completion Is Nothing Then Return
            _dialogCompletion = Nothing
            Me.RaisePropertyChanged(NameOf(IsDialogOpen))
            Me.RaisePropertyChanged(NameOf(IsAppContentHitTestVisible))
            Me.RaisePropertyChanged(NameOf(IsDialogCancelVisible))
            completion.TrySetResult(result)
        End Sub

        Public ReadOnly Property IsDialogCancelVisible As Boolean
            Get
                Return Not String.IsNullOrEmpty(_dialogCancelText)
            End Get
        End Property

        Private Sub RebuildBatchRenamePreview()
            If DialogBatchRenamePreview Is Nothing Then Return

            DialogBatchRenamePreview.Clear()
            If _dialogBatchRenamePaths Is Nothing OrElse _dialogBatchRenamePaths.Count = 0 Then Return

            Dim usedTargets As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim counter = _dialogBatchRenameStart

            For Each sourcePath In _dialogBatchRenamePaths
                Dim directoryPath = IO.Path.GetDirectoryName(sourcePath)
                Dim oldName = IO.Path.GetFileName(sourcePath)
                Dim newName = BuildBatchRenameName(sourcePath, counter)
                counter += _dialogBatchRenameStep

                Dim targetPath = If(String.IsNullOrEmpty(directoryPath), newName, IO.Path.Combine(directoryPath, newName))
                ''' Nothing statt "" - ToolTip.Tip zeigt sonst eine leere Blase an, auch wenn kein
                ''' Problem vorliegt (Avalonia unterscheidet zwischen "kein Tip" (Nothing) und "leerer Tip").
                Dim status As String = Nothing
                Dim hasProblem As Boolean = False

                If String.IsNullOrWhiteSpace(newName) OrElse HasInvalidFileNameChars(newName) Then
                    status = "Ungültiger Name"
                    hasProblem = True
                ElseIf Not usedTargets.Add(NormalizePath(targetPath)) Then
                    status = "Doppelter Zielname"
                    hasProblem = True
                ElseIf Not String.Equals(NormalizePath(sourcePath), NormalizePath(targetPath), StringComparison.OrdinalIgnoreCase) AndAlso
                       (IO.File.Exists(targetPath) OrElse IO.Directory.Exists(targetPath)) Then
                    status = "Existiert bereits"
                    hasProblem = True
                End If

                DialogBatchRenamePreview.Add(New BatchRenamePreviewItem With {
                    .SourcePath = sourcePath,
                    .TargetPath = targetPath,
                    .OldName = oldName,
                    .NewName = newName,
                    .DirectoryPath = If(directoryPath, ""),
                    .StatusText = status,
                    .HasProblem = hasProblem
                })
            Next
        End Sub

        ''' Extrahiert die erste Zahl (mit optionaler Nachkommastelle) aus einem formatierten
        ''' EXIF-Anzeigetext (z.B. "1920 pixels" -> "1920", "f/2.8" -> "2.8") - für Dateinamen
        ''' reicht die reine Zahl, die Einheiten/Symbole wären dort ohnehin unerwünscht.
        Private Shared Function ExtractLeadingNumberText(text As String) As String
            If String.IsNullOrWhiteSpace(text) Then Return ""
            Dim m = Regex.Match(text, "\d+(?:[.,]\d+)?")
            Return If(m.Success, m.Value, "")
        End Function

        Private Function BuildBatchRenameName(sourcePath As String, counter As Integer) As String
            Dim pattern = If(_dialogBatchRenamePattern, "").Trim()
            If String.IsNullOrEmpty(pattern) Then pattern = "{name}_###"

            Dim extension = IO.Path.GetExtension(sourcePath)
            Dim baseName = IO.Path.GetFileNameWithoutExtension(sourcePath)
            Dim modified = If(IO.File.Exists(sourcePath),
                              IO.File.GetLastWriteTime(sourcePath),
                              If(IO.Directory.Exists(sourcePath), IO.Directory.GetLastWriteTime(sourcePath), DateTime.Now))

            Dim exif As ExifData = Nothing
            _dialogBatchRenameExifCache.TryGetValue(sourcePath, exif)
            Dim camera = SanitizeForFileName(If(exif?.Camera, ""))
            Dim width = ExtractLeadingNumberText(exif?.ImageWidth)
            Dim height = ExtractLeadingNumberText(exif?.ImageHeight)
            Dim iso = ExtractLeadingNumberText(exif?.ISO)
            Dim aperture = ExtractLeadingNumberText(exif?.Aperture)
            ' Kleinbild-Äquivalent bevorzugen: "28" im Dateinamen sagt mehr als die "4" einer Handyoptik.
            Dim focal = ExtractLeadingNumberText(ExifService.GetComparableFocalLength(exif))
            Dim dateTakenRaw = If(exif?.DateTaken, "")
            Dim dateTaken = DateTime.MinValue
            Dim dateTakenParsed = Not String.IsNullOrWhiteSpace(dateTakenRaw) AndAlso
                DateTime.TryParseExact(dateTakenRaw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, dateTaken)

            Dim result = pattern.Replace("{name}", baseName).
                                 Replace("{filename}", baseName).
                                 Replace("{ext}", extension.TrimStart("."c)).
                                 Replace("{width}", width).
                                 Replace("{height}", height).
                                 Replace("{camera}", camera).
                                 Replace("{iso}", iso).
                                 Replace("{aperture}", aperture).
                                 Replace("{focal}", focal)
            result = Regex.Replace(result, "\{date:([^}]+)\}", Function(m)
                                                                   Try
                                                                       Return modified.ToString(m.Groups(1).Value)
                                                                   Catch
                                                                       Return ""
                                                                   End Try
                                                               End Function)
            result = Regex.Replace(result, "\{datetaken:([^}]+)\}", Function(m)
                                                                        If Not dateTakenParsed Then Return ""
                                                                        Try
                                                                            Return dateTaken.ToString(m.Groups(1).Value)
                                                                        Catch
                                                                            Return ""
                                                                        End Try
                                                                    End Function)

            ''' Kein automatisches Anhängen eines Zählers mehr, wenn das Muster kein #/### enthält -
            ''' der Nutzer kann bewusst ein rein datums-/EXIF-basiertes Muster ohne Zähler verwenden
            ''' (z.B. {datetaken:yyyyMMdd_HHmmss}), das pro Bild schon eindeutig genug ist. Echte
            ''' Namenskollisionen fängt die Duplikat-Prüfung in RebuildBatchRenamePreview ohnehin ab.
            Dim numberMatch = Regex.Match(result, "#+")
            If numberMatch.Success Then
                result = result.Remove(numberMatch.Index, numberMatch.Length).
                                Insert(numberMatch.Index, counter.ToString(New String("0"c, numberMatch.Length)))
            End If

            If String.IsNullOrEmpty(IO.Path.GetExtension(result)) AndAlso Not String.IsNullOrEmpty(extension) Then
                result &= extension
            End If
            Return result
        End Function

        Private Shared Function NormalizePath(path As String) As String
            If String.IsNullOrEmpty(path) Then Return ""
            Try
                Return IO.Path.GetFullPath(path).TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
            Catch
                Return path
            End Try
        End Function

        Private Shared Function NormalizeSaveAsFormat(value As String) As String
            Select Case If(value, "").Trim().ToUpperInvariant()
                Case "JPEG", "JPG"
                    Return "JPG"
                Case "PNG"
                    Return "PNG"
                Case "WEBP"
                    Return "WEBP"
                Case "FPX"
                    Return If(FpxService.Enabled, "FPX", "JPG")
                Case Else
                    Return "JPG"
            End Select
        End Function

        ''' <summary>Setzt die Formatliste des Speichern-Dialogs. FPX (nicht-destruktives Projektformat) nur beim
        ''' Editor-"Speichern unter" anbieten, nicht beim Stapel-Konvertieren (dort gibt es keinen Ebenenstand).</summary>
        Private Sub SetDialogFormats(includeFpx As Boolean)
            DialogFormatOptions.Clear()
            DialogFormatOptions.Add("JPG")
            DialogFormatOptions.Add("PNG")
            DialogFormatOptions.Add("WEBP")
            If includeFpx AndAlso FpxService.Enabled Then DialogFormatOptions.Add("FPX")
        End Sub

        Private Shared Function CreateUniqueConflictName(path As String) As String
            Dim dir = IO.Path.GetDirectoryName(path)
            Dim name = IO.Path.GetFileNameWithoutExtension(path)
            Dim ext = IO.Path.GetExtension(path)
            If IO.Directory.Exists(path) Then
                name = IO.Path.GetFileName(path)
                ext = ""
            End If
            If String.IsNullOrWhiteSpace(dir) OrElse String.IsNullOrWhiteSpace(name) Then Return IO.Path.GetFileName(path)

            Dim i = 1
            Dim candidate As String
            Do
                candidate = IO.Path.Combine(dir, $"{name} Kopie{If(i = 1, "", " " & i)}{ext}")
                i += 1
            Loop While IO.File.Exists(candidate) OrElse IO.Directory.Exists(candidate)

            Return IO.Path.GetFileName(candidate)
        End Function

        Public Async Sub RequestDeletePaths(paths As IEnumerable(Of String), Optional afterDelete As Action = Nothing)
            Dim pathList = paths.
                Where(Function(p) Not String.IsNullOrEmpty(p) AndAlso (IO.File.Exists(p) OrElse IO.Directory.Exists(p))).
                Where(Function(p) FileOperationPolicy.CanDelete(p)).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
            If pathList.Count = 0 Then Return

            Dim settings = AppSettingsService.Load()
            Dim useTrash = Not settings.DeleteSkipTrash
            Dim skipConfirmation = settings.DeleteSkipConfirmation

            If Not skipConfirmation Then
                Dim verb = If(useTrash, "in den Papierkorb verschieben", "endgültig löschen")
                Dim message = If(pathList.Count = 1,
                                 $"{IO.Path.GetFileName(pathList(0))} {verb}?",
                                 $"{pathList.Count} Elemente {verb}?")
                Dim confirmText = If(useTrash, "In den Papierkorb", "Löschen")
                If Not Await ShowConfirmAsync("Löschen", message, confirmText, "Abbrechen") Then Return
            End If

            Viewer.ReleaseCurrentImageIfAny(pathList)
            Editor.ReleaseCurrentImageIfAny(pathList)

            ' Pro Element abfangen, damit ein einzelner Fehler (z.B. Papierkorb nicht verfügbar) den Rest
            ' nicht abbricht und die Ansicht trotzdem aktualisiert wird.
            Dim failures As New List(Of String)()
            For Each itemPath In pathList
                Try
                    DeletePath(itemPath, useTrash)
                Catch ex As Exception
                    failures.Add($"{IO.Path.GetFileName(itemPath)}: {ex.Message}")
                End Try
            Next
            afterDelete?.Invoke()

            If failures.Count > 0 Then
                Dim title = If(useTrash, "In den Papierkorb fehlgeschlagen", "Löschen fehlgeschlagen")
                Await ShowMessageAsync(title, String.Join(Environment.NewLine, failures))
            End If
        End Sub

        Private Sub DeletePath(itemPath As String, useTrash As Boolean)
            If String.IsNullOrEmpty(itemPath) Then Return

            If useTrash Then
                ' Bewusst KEIN stiller Rückfall auf dauerhaftes Löschen, wenn der Papierkorb scheitert -
                ' das wäre die gefährliche Überraschung, die dieser Schalter gerade verhindern soll.
                If Not TrashService.MoveToTrash(itemPath) Then
                    Throw New IOException("Papierkorb nicht verfügbar")
                End If
                Return
            End If

            If IO.File.Exists(itemPath) Then
                IO.File.Delete(itemPath)
            ElseIf IO.Directory.Exists(itemPath) Then
                IO.Directory.Delete(itemPath, True)
            End If
        End Sub

        ''' Bei Dateien (nicht Ordnern) wird nur der Basisname ohne Endung im Eingabefeld angezeigt -
        ''' die Endung wird nach der Eingabe automatisch wieder angehängt, damit sie beim Umbenennen
        ''' nicht versehentlich mit überschrieben/entfernt werden kann.
        Public Async Sub RequestRenamePath(itemPath As String, Optional afterRename As Action(Of String) = Nothing)
            If String.IsNullOrEmpty(itemPath) OrElse Not (IO.File.Exists(itemPath) OrElse IO.Directory.Exists(itemPath)) Then Return
            If Not FileOperationPolicy.CanRename(itemPath) Then Return
            Dim oldName = IO.Path.GetFileName(itemPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
            Dim isDirectory = IO.Directory.Exists(itemPath)
            Dim extension = If(isDirectory, "", IO.Path.GetExtension(oldName))
            Dim baseName = If(isDirectory, oldName, IO.Path.GetFileNameWithoutExtension(oldName))
            Dim promptMessage = If(String.IsNullOrEmpty(extension), "Neuen Namen eingeben", $"Neuen Namen eingeben ({extension})")
            Dim newBaseName = Await ShowInputAsync(AppDialogKind.Rename, "Umbenennen", promptMessage, baseName, "Umbenennen", "Abbrechen")
            If String.IsNullOrWhiteSpace(newBaseName) OrElse String.Equals(newBaseName, baseName, StringComparison.Ordinal) Then Return
            Dim newName = newBaseName & extension

            Dim errorMessage As String = Nothing
            Try
                If HasInvalidFileNameChars(newName) Then Throw New IOException("Der Name enthält ungültige Zeichen.")
                Dim parent = IO.Path.GetDirectoryName(itemPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar))
                If String.IsNullOrEmpty(parent) Then Return
                Dim target = IO.Path.Combine(parent, newName)
                If IO.File.Exists(target) OrElse IO.Directory.Exists(target) Then Throw New IOException("Ein Element mit diesem Namen existiert bereits.")
                If IO.File.Exists(itemPath) Then
                    Viewer.ReleaseCurrentImageIfAny({itemPath})
                    Editor.ReleaseCurrentImageIfAny({itemPath})
                    IO.File.Move(itemPath, target)
                Else
                    IO.Directory.Move(itemPath, target)
                End If
                afterRename?.Invoke(target)
            Catch ex As Exception
                errorMessage = ex.Message
            End Try
            If errorMessage IsNot Nothing Then Await ShowMessageAsync("Umbenennen fehlgeschlagen", errorMessage)
        End Sub

        ''' Entfernt Zeichen aus einem eingesetzten Platzhalterwert (z.B. Kameramodell), die in
        ''' Dateinamen nicht erlaubt sind - der übrige Wert (Muster, Zähler, etc.) wird bereits
        ''' separat über HasInvalidFileNameChars in der Vorschau geprüft.
        Private Shared Function SanitizeForFileName(value As String) As String
            If String.IsNullOrEmpty(value) Then Return ""
            Dim invalidChars = IO.Path.GetInvalidFileNameChars()
            Dim builder As New Text.StringBuilder(value.Length)
            For Each c In value
                If Array.IndexOf(invalidChars, c) < 0 Then builder.Append(c)
            Next
            Return builder.ToString()
        End Function

        Private Shared Function HasInvalidFileNameChars(fileName As String) As Boolean
            If String.IsNullOrEmpty(fileName) Then Return True
            If fileName.IndexOf(IO.Path.DirectorySeparatorChar) >= 0 OrElse
               fileName.IndexOf(IO.Path.AltDirectorySeparatorChar) >= 0 Then Return True

            Dim invalidChars = IO.Path.GetInvalidFileNameChars()
            Return invalidChars IsNot Nothing AndAlso invalidChars.Length > 0 AndAlso fileName.IndexOfAny(invalidChars) >= 0
        End Function
    End Class

End Namespace
