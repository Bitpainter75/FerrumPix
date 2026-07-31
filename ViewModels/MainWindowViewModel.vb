Imports System.Windows.Input
Imports System.Linq
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.Text.RegularExpressions
Imports System.Globalization
Imports ReactiveUI
Imports Avalonia.Threading
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace ViewModels

    Public Enum SaveChangesDialogResult
        Save
        Discard
        Cancelled
    End Enum

    Public Class MainWindowViewModel
        Inherits ViewModelBase
        ' Betrachter und Editor kennen nur noch je eine schmale Sicht auf den Rahmen (siehe
        ' IViewerHost und IEditorHost) - nicht mehr die ganze Klasse. Die Implements-Klauseln unten
        ' sind die Liste dessen, was sie anfassen duerfen.
        Implements IViewerHost, IEditorHost

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
        Private _dialogSecondaryText As String = ""
        Private _dialogConflictRenameText As String = ""
        Private _dialogKind As AppDialogKind = AppDialogKind.Message
        Private _dialogCompletion As TaskCompletionSource(Of String)
        Private _dialogSelectedFormat As String = "JPG"
        Private _dialogSaveAsTarget As String = "Local"
        Private _dialogSaveAsTargetFolder As String = ""
        Private _dialogFolderChoiceCurrent As String = ""
        Private _dialogFolderChoiceLastSaved As String = ""
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
        Private _dialogBatchResizeNoUpscale As Boolean
        Private _dialogBatchResizeLongEdge As Boolean
        Private _dialogBatchWatermarkOverwrite As Boolean = True
        Private _dialogSelectedWatermarkPresetName As String = ""
        Private ReadOnly _dialogWatermarkPresets As New List(Of WatermarkPresetSettings)()

        Public Property Gallery As GalleryViewModel Implements IViewerHost.Gallery, IEditorHost.Gallery
        Public Property Viewer As ViewerViewModel Implements IEditorHost.Viewer
        Public Property Editor As EditorViewModel Implements IViewerHost.Editor
        Public Property Settings As SettingsViewModel Implements IViewerHost.Settings, IEditorHost.Settings

        ''' <summary>Meldet die Fensterbreite an alle Leisten. Wird von MainWindow bei jeder
        ''' Größenänderung gerufen; meldet nur, wenn sich an mindestens einer Schwelle etwas
        ''' ändert - sonst liefe bei jedem Pixel Ziehen eine Runde Bindungsaktualisierungen.</summary>
        Public Sub UpdateWindowWidth(width As Double)
            If Double.IsNaN(width) OrElse Double.IsInfinity(width) Then Return

            Dim targets = {CType(Me, ViewModelBase), Gallery, Viewer, Editor, Settings}
            Dim before = targets.Select(Function(vm) vm IsNot Nothing AndAlso vm.AreToolbarLabelsVisible).ToArray()
            ViewModelBase.SetWindowWidth(width)

            For index = 0 To targets.Length - 1
                Dim target = targets(index)
                If target Is Nothing Then Continue For
                If target.AreToolbarLabelsVisible <> before(index) Then target.RaiseToolbarLabelsChanged()
            Next
        End Sub

        Public ReadOnly Property DialogFormatOptions As ObservableCollection(Of String) = New ObservableCollection(Of String) From {
            "JPG",
            "PNG",
            "WEBP"
        }
        Public ReadOnly Property DialogBatchRenamePreview As ObservableCollection(Of BatchRenamePreviewItem) = New ObservableCollection(Of BatchRenamePreviewItem)()
        Public ReadOnly Property DialogWatermarkPresetNames As ObservableCollection(Of String) = New ObservableCollection(Of String)()

        Public Property CurrentMode As AppMode Implements IViewerHost.CurrentMode, IEditorHost.CurrentMode
            Get
                Return _currentMode
            End Get
            Set(value As AppMode)
                Dim previousMode = _currentMode
                Me.RaiseAndSetIfChanged(_currentMode, value)
                Me.RaisePropertyChanged(NameOf(CurrentContent))
                Me.RaisePropertyChanged(NameOf(TitleSuffix))
                RefreshWindowTitle()
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
                    ' wieder in der Galerie landen (auf dem Bild), nicht im Viewer
                    '.
                    Editor?.SetEntryMode(previousMode)
                End If
            End Set
        End Property

        ''' <summary>Dateiname und Farbetikett des gerade offenen Bildes, fuer die Mitte der
        ''' Fensterleiste. Der Rahmen liest sie aus dem AKTIVEN Modus - Betrachter und Editor
        ''' bieten dieselben Eigenschaften, die Galerie hat kein einzelnes Bild.</summary>
        Public ReadOnly Property WindowTitleFileName As String
            Get
                Select Case CurrentMode
                    Case AppMode.Viewer : Return If(Viewer?.CurrentFileName, "")
                    Case AppMode.Editor : Return If(Editor?.CurrentFileName, "")
                    Case Else : Return ""
                End Select
            End Get
        End Property

        Public ReadOnly Property HasWindowTitleColorLabel As Boolean
            Get
                Select Case CurrentMode
                    Case AppMode.Viewer : Return Viewer IsNot Nothing AndAlso Viewer.HasColorLabel
                    Case AppMode.Editor : Return Editor IsNot Nothing AndAlso Editor.HasColorLabel
                    Case Else : Return False
                End Select
            End Get
        End Property

        Public ReadOnly Property WindowTitleColorLabelBrush As Object
            Get
                Select Case CurrentMode
                    Case AppMode.Viewer : Return Viewer?.ColorLabelBrush
                    Case AppMode.Editor : Return Editor?.ColorLabelBrush
                    Case Else : Return Nothing
                End Select
            End Get
        End Property

        ''' <summary>Meldet die drei Titel-Eigenschaften neu. Wird beim Moduswechsel und bei jedem
        ''' Bildwechsel gerufen - sie leiten sich ab und melden sonst nie von selbst.</summary>
        Public Sub RefreshWindowTitle() Implements IViewerHost.RefreshWindowTitle, IEditorHost.RefreshWindowTitle
            Me.RaisePropertyChanged(NameOf(WindowTitleFileName))
            Me.RaisePropertyChanged(NameOf(HasWindowTitleColorLabel))
            Me.RaisePropertyChanged(NameOf(WindowTitleColorLabelBrush))
        End Sub

        Public Property Title As String
            Get
                Return _title
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_title, value)
            End Set
        End Property

        Public Property IsFullscreen As Boolean Implements IViewerHost.IsFullscreen
            Get
                Return _isFullscreen
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isFullscreen, value)
                Me.RaisePropertyChanged(NameOf(IsWindowChromeVisible))
                Me.RaisePropertyChanged(NameOf(IsFullscreenViewer))
                If value Then
                    ' Ins Vollbild: die Vollbildflaeche erst zeigen, wenn das Betriebssystem das
                    ' Fenster wirklich aufgezogen hat. Sonst ist das Bild kurz auf die normale
                    ' Fenstergroesse beschnitten zu sehen.
                    Dispatcher.UIThread.Post(Sub() Viewer?.RaiseFullscreenChanged(), DispatcherPriority.Background)
                Else
                    ' Aus dem Vollbild: die Flaeche sofort ausblenden. Das Verkleinern des Fensters
                    ' ist bereits in ApplyFullscreenState verzoegert.
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
        Public ReadOnly Property DialogSecondaryCommand As ICommand
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
            DialogSecondaryCommand = ReactiveCommand.Create(Sub() CompleteDialog("Secondary"))
            DialogSkipCommand = ReactiveCommand.Create(Sub() SkipDialog())
            DialogRenameCommand = ReactiveCommand.Create(Sub() RenameConflictDialog())
            SetDialogWatermarkAnchorCommand = ReactiveCommand.Create(Of String)(Sub(anchor) DialogWatermarkAnchor = anchor)
            DialogSkipAllCommand = ReactiveCommand.Create(Sub() CompleteDialog("SkipAll"))
            DialogOverwriteAllCommand = ReactiveCommand.Create(Sub() CompleteDialog("OverwriteAll"))

            If Not String.IsNullOrEmpty(initialImagePath) Then
                OpenInitialImage(initialImagePath)
            Else
                OpenStartupWithoutImage()
            End If
        End Sub

        ''' <summary>Start ohne Bildparameter - Einstellung „Start ohne Bilddatei".
        ''' Die Galerie wird IMMER aufgebaut, auch wenn Betrachter oder Editor nach vorn kommen:
        ''' sonst führte „Zurück zur Galerie" in eine leere Ansicht.</summary>
        Private Sub OpenStartupWithoutImage()
            OpenStartupGallery()
            CurrentMode = AppMode.Gallery

            Select Case AppSettingsService.NormalizeStartupNoImageMode(AppSettingsService.Load().StartupNoImageMode)
                Case "Viewer"
                    ' Ein Betrachter ohne Bild wäre eine leere Fläche - deshalb das erste Bild des
                    ' Startordners öffnen. Ist dort keins (oder zeigt der Start auf Immich), bleibt
                    ' es bei der Galerie.
                    Dim resolved = ResolveStartupFolder()
                    If Not String.IsNullOrEmpty(resolved.ImmichTarget) Then Return
                    Dim paths = Gallery.GetFolderImagePaths(resolved.LocalFolder)
                    If paths.Count = 0 Then Return
                    Viewer.OpenImage(paths(0), paths)
                    CurrentMode = AppMode.Viewer

                Case "Editor"
                    ' Kein OpenImageAsync: der Editor zeigt seinen Platzhalter (HasDocument = False),
                    ' und der Neu-Dialog legt sich darüber. Der Dialog wird nachgelagert geöffnet,
                    ' weil die EditorView im Konstruktor noch nicht realisiert ist.
                    CurrentMode = AppMode.Editor
                    Dispatcher.UIThread.Post(Sub() Editor?.ShowNewDocumentDialog(), DispatcherPriority.Background)
            End Select
        End Sub

        Private Async Function ConfirmEditorLeaveAsync(actionDescription As String) As Task(Of Boolean)
            If Editor Is Nothing OrElse Not Editor.HasUnsavedChanges Then Return True
            Return Await Editor.ConfirmSaveBeforeLeavingAsync(actionDescription)
        End Function

        Private Async Function ConfirmViewerLeaveAsync(actionDescription As String) As Task(Of Boolean)
            If CurrentMode <> AppMode.Viewer OrElse Viewer Is Nothing Then Return True
            Return Await Viewer.ConfirmPendingRotationAsync(actionDescription)
        End Function

        ''' <summary>Zwei Bilder im Betrachter nebeneinander vergleichen. Derselbe Weg wie beim
        ''' normalen Oeffnen, nur mit zwei Pfaden - inklusive der Rueckfragen, die ein ungespeichertes
        ''' Rezept oder eine offene Drehung sonst still verwerfen wuerden.</summary>
        Public Async Sub OpenCompareInViewer(leftPath As String, rightPath As String,
                                             Optional allPaths As System.Collections.Generic.List(Of String) = Nothing,
                                             Optional cacheScopeId As String = Nothing,
                                             Optional cacheScopeName As String = Nothing)
            Try
                If String.IsNullOrWhiteSpace(leftPath) OrElse String.IsNullOrWhiteSpace(rightPath) Then Return
                If CurrentMode = AppMode.Editor Then
                    If Not Await ConfirmEditorLeaveAsync("den Betrachter zu öffnen") Then Return
                End If
                If CurrentMode = AppMode.Viewer AndAlso Viewer IsNot Nothing AndAlso
                   Not String.Equals(Viewer.CurrentImagePath, leftPath, StringComparison.OrdinalIgnoreCase) Then
                    If Not Await ConfirmViewerLeaveAsync("ein anderes Bild öffnest") Then Return
                End If
                Viewer.OpenCompare(leftPath, rightPath, allPaths, cacheScopeId, cacheScopeName)
                CurrentMode = AppMode.Viewer
            Catch ex As Exception
                DiagnosticLogService.LogException("MainWindowViewModel.OpenCompareInViewer", ex)
            End Try
        End Sub

        Public Async Sub OpenImageInViewer(imagePath As String, Optional allPaths As System.Collections.Generic.List(Of String) = Nothing, Optional bypassEditorPrompt As Boolean = False, Optional cacheScopeId As String = Nothing, Optional cacheScopeName As String = Nothing)
            Try
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
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("MainWindowViewModel.OpenImageInViewer", ex)
            End Try
        End Sub

        ''' <summary>Öffnet eine Immich-Sitzung im Betrachter: der Filmstreifen zeigt das ganze Album
        ''' (Pseudo-Pfade), das aktuelle Bild wird on-demand heruntergeladen.</summary>
        Public Async Sub OpenImmichViewer(startPseudoPath As String, sessionItems As System.Collections.Generic.List(Of Models.ImageItem), Optional immichAlbumId As String = Nothing)
            Try
                If CurrentMode = AppMode.Editor Then
                    If Not Await ConfirmEditorLeaveAsync("den Betrachter zu öffnen") Then Return
                End If
                Viewer.OpenImmichSession(startPseudoPath, sessionItems, immichAlbumId)
                CurrentMode = AppMode.Viewer
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("MainWindowViewModel.OpenImmichViewer", ex)
            End Try
        End Sub

        Public Async Function OpenImageInEditor(path As String, Optional allPaths As System.Collections.Generic.List(Of String) = Nothing, Optional cacheScopeId As String = Nothing, Optional cacheScopeName As String = Nothing, Optional forceSaveAsOnly As Boolean = False, Optional immichAlbumId As String = Nothing) As Task Implements IViewerHost.OpenImageInEditor
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
            Try
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
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("MainWindowViewModel.OpenSettings", ex)
            End Try
        End Sub

        Public Sub CloseSettings()
            CurrentMode = _previousModeBeforeSettings
        End Sub

        ''' <summary>Aus Betrachter oder Editor zur Galerie wechseln und dort alle Bilder mit diesem
        ''' Stichwort zeigen. Bewusst OHNE den Umweg ueber den Ordner des aktuellen Bildes: das Ziel
        ''' ist der ganze Bestand, nicht der Ort, an dem man gerade stand.</summary>
        Public Async Sub OpenTagSearchInGallery(tag As String) Implements IViewerHost.OpenTagSearchInGallery, IEditorHost.OpenTagSearchInGallery
            Try
                If String.IsNullOrWhiteSpace(tag) OrElse Gallery Is Nothing Then Return
                If CurrentMode = AppMode.Editor Then
                    If Not Await ConfirmEditorLeaveAsync("zur Galerie zu wechseln") Then Return
                End If
                If CurrentMode = AppMode.Viewer Then
                    If Not Await ConfirmViewerLeaveAsync("zur Galerie wechselst") Then Return
                End If
                CurrentMode = AppMode.Gallery
                Gallery.OpenTagSearch(tag)
            Catch ex As Exception
                DiagnosticLogService.LogException("MainWindowViewModel.OpenTagSearchInGallery", ex)
            End Try
        End Sub

        Public Async Sub BackToGallery(Optional sourcePath As String = Nothing) Implements IViewerHost.BackToGallery
            Try
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
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("MainWindowViewModel.BackToGallery", ex)
            End Try
        End Sub

        ''' <summary>Vollbild umschalten. Dieselbe Wirkung wie F11, aber aus einem Menue heraus
        ''' aufrufbar - Galerie, Betrachter und Editor teilen sich diesen einen Weg.</summary>
        Public Sub ToggleFullscreen() Implements IViewerHost.ToggleFullscreen, IEditorHost.ToggleFullscreen
            If IsFullscreen Then ExitFullscreen() Else EnterFullscreen()
        End Sub

        ''' <summary>In den Editor wechseln und dort den Dialog fuer ein neues Bild oeffnen.
        ''' Aus Galerie und Betrachter gab es dafuer bisher keinen Weg.</summary>
        Public Sub ShowNewDocumentDialog() Implements IViewerHost.ShowNewDocumentDialog, IEditorHost.ShowNewDocumentDialog
            CurrentMode = AppMode.Editor
            Editor?.ShowNewDocumentDialogCommand.Execute(Nothing)
        End Sub

        Public Async Sub EnterFullscreen() Implements IViewerHost.EnterFullscreen
            Try
                If CurrentMode = AppMode.Gallery AndAlso Gallery.SelectedItem IsNot Nothing AndAlso (Gallery.SelectedItem.IsImage OrElse Gallery.SelectedItem.IsVideoFile) Then
                    _previousModeBeforeFullscreen = AppMode.Gallery
                    OpenImageInViewer(Gallery.SelectedItem.FilePath, Gallery.Items.Where(Function(i) i.IsImage OrElse i.IsVideoFile).Select(Function(i) i.FilePath).ToList(),
                                      cacheScopeId:=Gallery.CurrentThumbnailCacheScopeId, cacheScopeName:=Gallery.CurrentThumbnailCacheScopeName)
                ElseIf CurrentMode = AppMode.Editor AndAlso Editor.IsNewDocument Then
                    ' Ein nie gespeichertes neues Bild hat auf der Platte nur seine LEERE Temp-Datei -
                    ' Vollbild zeigte also eine leere Fläche und zöge den Betrachter in den Temp-Ordner.
                    ' Wie beim bildlosen Betrachter unten: nichts tun.
                    Return
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
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("MainWindowViewModel.EnterFullscreen", ex)
            End Try
        End Sub

        Public Async Sub ExitFullscreen()
            Try
                If CurrentMode = AppMode.Viewer AndAlso _previousModeBeforeFullscreen <> AppMode.Viewer Then
                    If Not Await ConfirmViewerLeaveAsync("den Vollbildmodus verlässt") Then Return
                End If
                IsFullscreen = False
                Viewer.StopSlideshow()
                CurrentMode = _previousModeBeforeFullscreen
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("MainWindowViewModel.ExitFullscreen", ex)
            End Try
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

        ''' <summary>Sorgt dafür, dass die Galerie auf einem ECHTEN Ordner steht, und zeigt sie an.
        ''' Gebraucht beim Verwerfen eines nie gespeicherten neuen Bildes: dessen Pfad zeigt in einen
        ''' Temp-Ordner, der als Ziel nicht taugt. Ein bereits geöffneter Ordner bleibt stehen - nur
        ''' wenn gar keiner da ist (Start MIT Bildparameter baut die Galerie nie auf), wird der
        ''' Startordner nachgeladen.</summary>
        Public Sub ShowGalleryAtRealFolder() Implements IEditorHost.ShowGalleryAtRealFolder
            Dim current = Gallery?.CurrentFolder
            If String.IsNullOrEmpty(current) OrElse
               current.StartsWith("immich://", StringComparison.OrdinalIgnoreCase) OrElse
               Not Directory.Exists(current) Then
                OpenStartupGallery()
            End If
            CurrentMode = AppMode.Gallery
        End Sub

        ''' <summary>Wohin die Galerie beim Start zeigt - nur ermittelt, nichts navigiert.
        ''' Herausgezogen, damit „Start ohne Bilddatei = Betrachter" denselben Ordner benutzt, statt
        ''' die Leiter (Letzter/Benutzerdefiniert/Immich/Bilder) ein zweites Mal nachzubauen.
        ''' Ein Immich-Ziel kommt zusätzlich zum lokalen Ordner zurück: der lokale Ordner ist dann
        ''' die Grundlage, die stehen bleibt, falls Immich nicht erreichbar ist.</summary>
        Private Function ResolveStartupFolder() As (LocalFolder As String, ImmichTarget As String)
            Dim settings = AppSettingsService.Load()
            Dim targetFolder As String = Nothing
            Dim pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)

            Select Case AppSettingsService.NormalizeGalleryStartupFolderMode(settings.GalleryStartupFolderMode)
                Case "Last"
                    ' Auch ein Immich-Ziel (Alle Fotos/Album/Person/Ort) kann der zuletzt geöffnete
                    ' „Ordner" sein. Erst den Bilder-Ordner als Basis
                    ' laden, dann asynchron das Immich-Ziel öffnen - bei ausgeschaltetem/nicht
                    ' erreichbarem Immich bleibt so der Bilder-Ordner als Fallback stehen.
                    If If(settings.LastGalleryFolder, "").StartsWith("immich://", StringComparison.OrdinalIgnoreCase) Then
                        If ImmichService.IsConfigured Then Return (pictures, settings.LastGalleryFolder)
                    ElseIf Directory.Exists(settings.LastGalleryFolder) Then
                        targetFolder = settings.LastGalleryFolder
                    End If
                Case "Custom"
                    If Directory.Exists(settings.GalleryStartupCustomFolder) Then
                        targetFolder = settings.GalleryStartupCustomFolder
                    End If
                Case "Immich"
                    ' Fester Start in Immich „Alle Fotos" (Einstellungsdialog)
                    ' - gleiche Mechanik wie der Letzter-Ordner-Fall.
                    If ImmichService.IsConfigured Then Return (pictures, "immich://all")
            End Select

            If String.IsNullOrEmpty(targetFolder) OrElse Not Directory.Exists(targetFolder) Then
                targetFolder = pictures
            End If
            Return (targetFolder, Nothing)
        End Function

        Private Sub OpenStartupGallery()
            Dim resolved = ResolveStartupFolder()

            If Not String.IsNullOrEmpty(resolved.LocalFolder) AndAlso Directory.Exists(resolved.LocalFolder) Then
                Gallery.SetInitialFolderNodeForPath(resolved.LocalFolder)
                Gallery.NavigateToFolder(resolved.LocalFolder)
            End If

            If Not String.IsNullOrEmpty(resolved.ImmichTarget) Then
                Dim ignored = Gallery.OpenImmichStartupTargetAsync(resolved.ImmichTarget)
            End If
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
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.IsToolSidebarCollapsed))
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.AreToolSidebarLabelsVisible))
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.ToolSidebarWidth))
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.EditorGridSize))
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.EditorShowRulers))
            Editor?.RaisePropertyChanged(NameOf(EditorViewModel.EditorShowGrid))
            Gallery?.RefreshInfoSidebarState()
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
                Me.RaisePropertyChanged(NameOf(IsDialogOverwriteJpgQualityVisible))
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
                Me.RaisePropertyChanged(NameOf(IsSaveAsTargetRowVisible))
                Me.RaisePropertyChanged(NameOf(IsDialogFolderChoiceCurrentActive))
                Me.RaisePropertyChanged(NameOf(IsDialogFolderChoiceLastSavedActive))
            End Set
        End Property

        Public Property DialogSaveAsTargetFolder As String
            Get
                Return _dialogSaveAsTargetFolder
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogSaveAsTargetFolder, AppSettingsService.NormalizeFolderPath(value))
                ' Aktiv-Zustand und Bedienbarkeit haengen am Feldinhalt - deshalb hier und nicht
                ' nur beim Oeffnen melden.
                Me.RaisePropertyChanged(NameOf(IsDialogFolderChoiceCurrentEnabled))
                Me.RaisePropertyChanged(NameOf(IsDialogFolderChoiceLastSavedEnabled))
                Me.RaisePropertyChanged(NameOf(IsDialogFolderChoiceCurrentActive))
                Me.RaisePropertyChanged(NameOf(IsDialogFolderChoiceLastSavedActive))
            End Set
        End Property

        ''' <summary>Die zwei Ordner, zwischen denen ein Overlay-Dialog umschalten laesst: der
        ''' Ordner, in dem der Nutzer gerade steht, und der zuletzt als Ziel benutzte. Vorbelegt
        ''' bleibt der aktuelle - wer regelmaessig in denselben Ausgabeordner schreibt, kommt mit
        ''' einem Klick dorthin, statt den Pfad jedes Mal neu zu suchen.</summary>
        Public ReadOnly Property DialogFolderChoiceCurrent As String
            Get
                Return _dialogFolderChoiceCurrent
            End Get
        End Property

        Public ReadOnly Property DialogFolderChoiceLastSaved As String
            Get
                Return _dialogFolderChoiceLastSaved
            End Get
        End Property

        ''' <summary>Bedienbar, sobald es den Ordner gibt. Die Knoepfe sind IMMER sichtbar und
        ''' werden ohne Ordner nur ausgegraut - eine Zeile, in der Knoepfe je nach Aufrufweg
        ''' auftauchen und verschwinden, ist schwerer zu lesen als eine stabile (Nutzerwunsch).
        ''' Frueher versteckte die Regel die Schaltflaeche komplett, und aus Viewer und Editor
        ''' fehlte der Knopf "Aktueller Ordner" dadurch ganz.</summary>
        Public ReadOnly Property IsDialogFolderChoiceCurrentEnabled As Boolean
            Get
                Return _dialogFolderChoiceCurrent <> ""
            End Get
        End Property

        Public ReadOnly Property IsDialogFolderChoiceLastSavedEnabled As Boolean
            Get
                Return _dialogFolderChoiceLastSaved <> ""
            End Get
        End Property

        Public ReadOnly Property IsDialogFolderChoiceCurrentActive As Boolean
            Get
                Return IsSaveAsTargetLocal AndAlso _dialogFolderChoiceCurrent <> "" AndAlso
                       PathIdentity.AreSame(_dialogFolderChoiceCurrent, _dialogSaveAsTargetFolder)
            End Get
        End Property

        Public ReadOnly Property IsDialogFolderChoiceLastSavedActive As Boolean
            Get
                Return IsSaveAsTargetLocal AndAlso _dialogFolderChoiceLastSaved <> "" AndAlso
                       PathIdentity.AreSame(_dialogFolderChoiceLastSaved, _dialogSaveAsTargetFolder)
            End Get
        End Property

        ''' <summary>Belegt den Zielordner vor und merkt sich beide Wahlmoeglichkeiten. Alle
        ''' Dialoge, die einen Zielordner zeigen, gehen hier durch - sonst haetten die einen die
        ''' Schnellwahl und die anderen nicht.</summary>
        Private Sub InitDialogTargetFolder(currentFolder As String)
            Dim zuletzt = AppSettingsService.Load().LastSaveAsTargetFolder
            ' Die Haelfte der Dialoge bekommt gar keinen Ordner uebergeben - dort fehlte der Knopf
            ' "Aktueller Ordner" deshalb ganz. Der Ordner, in dem der Nutzer steht, ist aber immer
            ' bekannt: er steht in der Galerie. In einer Suchliste oder in Immich ist er leer, dann
            ' bleibt es beim Ausblenden, denn dort gibt es keinen echten Ordner.
            Dim folder = currentFolder
            If String.IsNullOrWhiteSpace(folder) Then folder = If(Gallery?.CurrentFolder, "")
            _dialogFolderChoiceCurrent = If(Not String.IsNullOrWhiteSpace(folder) AndAlso Directory.Exists(folder),
                                            folder, "")
            _dialogFolderChoiceLastSaved = If(Not String.IsNullOrWhiteSpace(zuletzt) AndAlso Directory.Exists(zuletzt),
                                              zuletzt, "")
            ' Beim ersten Mal gibt es noch keinen zuletzt benutzten Ordner. Dann traegt der Knopf
            ' denselben wie der aktuelle, statt zu fehlen: eine Zeile mit wechselnder Knopfzahl ist
            ' schwerer zu lesen als eine, in der einmal beide dasselbe meinen.
            If _dialogFolderChoiceLastSaved = "" Then _dialogFolderChoiceLastSaved = _dialogFolderChoiceCurrent
            DialogSaveAsTargetFolder = If(_dialogFolderChoiceCurrent <> "",
                                          _dialogFolderChoiceCurrent,
                                          ResolveDefaultSaveAsTargetFolder())
            Me.RaisePropertyChanged(NameOf(DialogFolderChoiceCurrent))
            Me.RaisePropertyChanged(NameOf(DialogFolderChoiceLastSaved))
            Me.RaisePropertyChanged(NameOf(IsDialogFolderChoiceCurrentEnabled))
            Me.RaisePropertyChanged(NameOf(IsDialogFolderChoiceLastSavedEnabled))
            Me.RaisePropertyChanged(NameOf(IsSaveAsTargetRowVisible))
        End Sub

        Public Sub SetDialogTargetFolderChoice(welche As String)
            If String.Equals(welche, "Current", StringComparison.Ordinal) Then
                If _dialogFolderChoiceCurrent <> "" Then DialogSaveAsTargetFolder = _dialogFolderChoiceCurrent
            ElseIf String.Equals(welche, "LastSaved", StringComparison.Ordinal) Then
                If _dialogFolderChoiceLastSaved <> "" Then DialogSaveAsTargetFolder = _dialogFolderChoiceLastSaved
            End If
        End Sub

        ''' <summary>Ein Klick in der Ziel-Zeile. "Immich" schaltet das Ziel um, die beiden
        ''' Ordner-Knoepfe setzen das Ziel auf lokal UND tragen ihren Ordner ein - sonst muesste
        ''' der Nutzer zwei Dinge anklicken, um dasselbe zu meinen.</summary>
        Public Sub SetDialogSaveAsTarget(target As String)
            Select Case target
                Case "Current", "LastSaved"
                    DialogSaveAsTarget = "Local"
                    SetDialogTargetFolderChoice(target)
                Case Else
                    DialogSaveAsTarget = target
            End Select
            Me.RaisePropertyChanged(NameOf(IsDialogFolderChoiceCurrentActive))
            Me.RaisePropertyChanged(NameOf(IsDialogFolderChoiceLastSavedActive))
        End Sub

        Public ReadOnly Property IsSaveAsImmichAvailable As Boolean
            Get
                ' FPX ist ein lokales, nicht-destruktives Projektformat - ein Upload nach Immich (das Bilder
                ' als Assets führt) ergibt keinen Sinn, daher als Ziel ausschließen. PDF genauso:
                ' Immich verwaltet Bild-Assets, keine Dokumente.
                Return ImmichService.IsConfigured AndAlso
                       Not String.Equals(_dialogSelectedFormat, "FPX", StringComparison.OrdinalIgnoreCase) AndAlso
                       Not String.Equals(_dialogSelectedFormat, "PDF", StringComparison.OrdinalIgnoreCase)
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

        ''' <summary>Das Ordner-FELD. Bei Ziel Immich wird kein Ordner gebraucht, es faellt weg.</summary>
        Public ReadOnly Property IsSaveAsTargetFolderVisible As Boolean
            Get
                Return IsSaveAsTargetLocal
            End Get
        End Property

        ''' <summary>Die ZIEL-ZEILE mit ihren Knoepfen. Sie haengt ausdruecklich NICHT am gewaehlten
        ''' Ziel: an die Sichtbarkeit des Ordner-Feldes gebunden verschwand sie bei Immich mitsamt
        ''' dem Immich-Knopf, und es gab keinen Weg zurueck zu einem Ordner. Seit die Ordner-Knoepfe
        ''' immer sichtbar sind (ohne Ordner nur ausgegraut), ist die Zeile schlicht immer da.</summary>
        Public ReadOnly Property IsSaveAsTargetRowVisible As Boolean
            Get
                Return True
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

        ''' Die Auswahlliste zur aktuellen Quelle - eingebaute Filter, gespeicherte XMP-Presets oder
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
                Me.RaisePropertyChanged(NameOf(IsDialogFilterSourceXmpPreset))
                Me.RaisePropertyChanged(NameOf(IsDialogFilterSourceLut))
                Me.RaisePropertyChanged(NameOf(IsDialogFilterSourceAuto))
                Me.RaisePropertyChanged(NameOf(IsDialogFilterFileVisible))
                Me.RaisePropertyChanged(NameOf(IsDialogFilterStrengthVisible))
                Me.RaisePropertyChanged(NameOf(IsDialogFilterChoiceVisible))
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

        Public ReadOnly Property IsDialogFilterSourceXmpPreset As Boolean
            Get
                Return String.Equals(_dialogFilterSourceKind, BatchFilterDialogResult.SourceXmpPreset, StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsDialogFilterSourceLut As Boolean
            Get
                Return String.Equals(_dialogFilterSourceKind, BatchFilterDialogResult.SourceLut, StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsDialogFilterSourceAuto As Boolean
            Get
                Return String.Equals(_dialogFilterSourceKind, BatchFilterDialogResult.SourceAuto, StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' Nur Presets aus Dateien lassen sich hinzuladen - die eingebauten Filter sind fest,
        ''' die automatische Bildverbesserung misst jedes Bild selbst.
        Public ReadOnly Property IsDialogFilterFileVisible As Boolean
            Get
                Return Not IsDialogFilterSourceFilter AndAlso Not IsDialogFilterSourceAuto
            End Get
        End Property

        ''' Ein XMP-Preset ist eine Sammlung einzelner Regler, kein Effekt mit einem Mischregler -
        ''' eine "Stärke" gäbe es dort nur als willkürliche Skalierung aller Werte. Die automatische
        ''' Bildverbesserung setzt gemessene Absolutwerte - auch dort wäre eine Stärke willkürlich.
        Public ReadOnly Property IsDialogFilterStrengthVisible As Boolean
            Get
                Return Not IsDialogFilterSourceXmpPreset AndAlso Not IsDialogFilterSourceAuto
            End Get
        End Property

        ''' Bei der automatischen Bildverbesserung gibt es keine Vorgabenliste - die Zeile entfällt.
        Public ReadOnly Property IsDialogFilterChoiceVisible As Boolean
            Get
                Return Not IsDialogFilterSourceAuto
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
                ' Die Wahl bleibt über Sitzungen erhalten.
                AppSettingsService.SaveBatchFilterOverwriteOriginals(value)
                ' Beim Ueberschreiben startet die Qualitaet auf 95 (dem frueheren festen Wert),
                ' bei Kopien gilt wieder die Einstellung.
                DialogJpgQuality = If(value, 95, DefaultJpgQuality())
                Me.RaisePropertyChanged(NameOf(DialogBatchFilterOverwrite))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsOptions))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsMetaOptions))
                Me.RaisePropertyChanged(NameOf(DialogWidth))
                Me.RaisePropertyChanged(NameOf(IsDialogJpgQualityVisible))
                Me.RaisePropertyChanged(NameOf(IsDialogOverwriteJpgQualityVisible))
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
            ElseIf IsDialogFilterSourceAuto Then
                ' Genau ein fester Eintrag: er hält die Auswahl nicht-leer (der Bestätigen-Wächter
                ' prüft darauf), sichtbar ist die Zeile in diesem Modus ohnehin nicht.
                DialogFilterChoices.Add(LocalizationService.T("Automatische Bildverbesserung"))
            Else
                Dim settings = AppSettingsService.Load()
                Dim entries = If(IsDialogFilterSourceXmpPreset,
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

        ' ── „Exportieren nach" (Galerie): Sammel-Export ─────────────────────────
        ' Ein Look aus EINER Liste (eingebaute Filter + gespeicherte XMP-/LUT-Vorgaben),
        ' dazu Auto-Verbesserung, Wasserzeichen-Vorgabe, Bildgröße und Metadaten. Der Dialog
        ' nutzt den gemeinsamen SaveAs-Block (Format/Qualität/Ziel/Muster/Übernehmen).

        Private _dialogExportUseFilter As Boolean
        Private _dialogExportUseWatermark As Boolean
        Private _dialogExportUseResize As Boolean
        Private _dialogWatermarkKeepSize As Boolean
        Private _dialogExportPreserveMetadata As Boolean = True
        Private _dialogSaveAsPreserveExif As Boolean = True

        ''' Die Sektionen des Sammel-Exports. Ist eine aus, bleibt ihr Formular verborgen UND ihre
        ''' Einstellungen wirken nicht - der Nutzer sieht genau das, was angewendet wird.
        Public Property DialogExportUseFilter As Boolean
            Get
                Return _dialogExportUseFilter
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogExportUseFilter, value)
            End Set
        End Property

        Public Property DialogExportUseWatermark As Boolean
            Get
                Return _dialogExportUseWatermark
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogExportUseWatermark, value)
            End Set
        End Property

        Public Property DialogExportUseResize As Boolean
            Get
                Return _dialogExportUseResize
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogExportUseResize, value)
                ' Ohne Größenänderung gibt es nichts, wovon das Wasserzeichen sich abkoppeln könnte.
                Me.RaisePropertyChanged(NameOf(IsDialogWatermarkKeepSizeVisible))
            End Set
        End Property

        ''' <summary>„Wasserzeichen nicht mitskalieren": normalerweise gelten Größe und Randabstand
        ''' der Vorlage für das ORIGINALBILD und schrumpfen mit der Verkleinerung mit. Ist der
        ''' Schalter an, gelten sie für das FERTIGE Bild - das Wasserzeichen wird also erst nach dem
        ''' Verkleinern in seiner eingestellten Größe aufgebracht und ist auf jeder Ausgabegröße
        ''' gleich groß.</summary>
        Public Property DialogWatermarkKeepSize As Boolean
            Get
                Return _dialogWatermarkKeepSize
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogWatermarkKeepSize, value)
            End Set
        End Property

        ''' Nur im Sammel-Export und nur bei aktiver Größenänderung sinnvoll - das Wasserzeichen-
        ''' Formular teilen sich beide Dialoge, im Einzeldialog wird nichts skaliert.
        Public ReadOnly Property IsDialogWatermarkKeepSizeVisible As Boolean
            Get
                Return _dialogKind = AppDialogKind.ExportTo AndAlso _dialogExportUseResize
            End Get
        End Property


        ''' <summary>Bereitet die eingebetteten Formulare vor: sie binden DIESELBEN Eigenschaften
        ''' wie die Einzeldialoge, also muessen deren Listen/Startwerte hier genauso gefuellt werden.</summary>
        Private Sub PrepareDialogExportForms(settings As AppSettings, samplePath As String)
            ' Filter-Formular (Quelle/Vorgabe/Staerke)
            _dialogFilterSourceKind = BatchFilterDialogResult.SourceFilter
            _dialogFilterStrength = 100
            RebuildDialogFilterChoices()

            ' Wasserzeichen-Liste
            _dialogWatermarkPresets.Clear()
            DialogWatermarkPresetNames.Clear()
            For Each preset In settings.WatermarkPresets
                _dialogWatermarkPresets.Add(preset)
                DialogWatermarkPresetNames.Add(preset.Name)
            Next
            Dim lastName = AppSettingsService.NormalizePresetName(settings.LastWatermarkPresetName)
            Dim selectedPreset = _dialogWatermarkPresets.FirstOrDefault(Function(p) String.Equals(p.Name, lastName, StringComparison.OrdinalIgnoreCase))
            If selectedPreset Is Nothing Then selectedPreset = _dialogWatermarkPresets.FirstOrDefault()
            DialogSelectedWatermarkPresetName = If(selectedPreset?.Name, "")
            LoadWatermarkPresetIntoDialogFields(selectedPreset)

            ' Groessen-Formular - wie in ShowBatchResizeAsync, inklusive Beispielmasse fuer die
            ' Seitenverhaeltnis-Rechnung.
            _dialogBatchResizeWidthText = If(settings.LastBatchResizeWidth > 0, settings.LastBatchResizeWidth.ToString(CultureInfo.InvariantCulture), "")
            _dialogBatchResizeHeightText = If(settings.LastBatchResizeHeight > 0, settings.LastBatchResizeHeight.ToString(CultureInfo.InvariantCulture), "")
            _dialogBatchResizeLockAspect = settings.LastBatchResizeLockAspect
            _dialogBatchResizeNoUpscale = settings.LastBatchResizeNoUpscale
            _dialogBatchResizeLongEdge = settings.LastBatchResizeLongEdge
            If _dialogBatchResizeLongEdge Then _dialogBatchResizeLockAspect = True
            _dialogBatchResizeInterpolation = ParseResizeInterpolationMode(settings.LastBatchResizeInterpolation)
            _dialogBatchResizeScalePercent = settings.LastBatchResizeScalePercent
            _dialogBatchResizeSourceWidth = 0
            _dialogBatchResizeSourceHeight = 0
            If Not String.IsNullOrWhiteSpace(samplePath) AndAlso File.Exists(samplePath) Then
                Try
                    Dim size = ImageProcessor.GetImageSize(samplePath)
                    _dialogBatchResizeSourceWidth = size.Width
                    _dialogBatchResizeSourceHeight = size.Height
                Catch
                End Try
            End If
            If _dialogBatchResizeScalePercent > 0 Then SetDialogBatchResizeTextsFromScale(_dialogBatchResizeScalePercent)
            If _dialogBatchResizeLongEdge Then CollapseBatchResizeToLongEdge()
            RaiseDialogBatchResizeProperties()

            For Each name In {NameOf(DialogFilterSourceKind), NameOf(IsDialogFilterSourceFilter),
                              NameOf(IsDialogFilterSourceXmpPreset), NameOf(IsDialogFilterSourceLut),
                              NameOf(IsDialogFilterSourceAuto),
                              NameOf(IsDialogFilterFileVisible), NameOf(IsDialogFilterStrengthVisible),
                              NameOf(IsDialogFilterChoiceVisible), NameOf(DialogSelectedWatermarkPresetName)}
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        Public Async Function ShowExportToAsync(fileCount As Integer, Optional currentFolder As String = "",
                                                Optional samplePath As String = Nothing) As Task(Of ExportToDialogResult)
            Dim settings = AppSettingsService.Load()
            ' Zuletzt benutzte Zusammenstellung wiederherstellen - ein Sammel-Export wird meist mit
            ' denselben Bausteinen wiederholt.
            _dialogBatchOverwriteAvailable = True
            DialogExportUseFilter = settings.ExportToUseFilter
            DialogExportUseWatermark = settings.ExportToUseWatermark
            DialogExportUseResize = settings.ExportToUseResize
            DialogWatermarkKeepSize = settings.ExportToWatermarkKeepSize
            DialogTargetNamePattern = AppSettingsService.Load().LastTargetNamePattern
            ' Aufnahmedaten fuer die Muster-Platzhalter nicht ueber Dialogoeffnungen hinweg
            ' mitschleppen: sonst stehen geaenderte Dateien mit alten Werten im Namen.
            _dialogBatchRenameExifCache.Clear()
            ResetDialogSaveAsMetaOptions()
            ' FPX auch hier: ein Sammel-Export darf weiterbearbeitbare Projekte liefern,
            ' nicht nur fertige Bilder (SaveImage schreibt das Buendel).
            SetDialogFormats(includeFpx:=True)
            DialogSelectedFormat = NormalizeSaveAsFormat(DefaultSaveFormat(allowFpx:=True))
            DialogJpgQuality = DefaultJpgQuality()
            DialogSaveAsTarget = "Local"
            InitDialogTargetFolder(currentFolder)
            PrepareDialogExportForms(settings, samplePath)
            Me.RaisePropertyChanged(NameOf(IsSaveAsImmichAvailable))

            Dim title = $"{LocalizationService.T("Exportieren nach")} ({fileCount} {LocalizationService.T("Dateien")})"
            Dim result = Await ShowDialogAsync(AppDialogKind.ExportTo,
                                               title,
                                               "Stelle den Export zusammen - die Originale bleiben unangetastet.",
                                               "",
                                               "Exportieren",
                                               "Abbrechen")
            If result Is Nothing Then Return Nothing

            PersistDialogTargetFolderIfLocal()
            PersistDialogTargetNamePattern()
            AppSettingsService.SaveExportToSections(_dialogExportUseFilter, _dialogExportUseWatermark, _dialogExportUseResize,
                                                    _dialogWatermarkKeepSize)

            ' Nur AKTIVE Sektionen fliessen ins Ergebnis - was der Dialog verbirgt, wirkt auch nicht.
            ' Die automatische Bildverbesserung ist die vierte QUELLE des Filter-Formulars, kein
            ' eigener Schalter - so steht sie im Dialog nur einmal.
            Dim lookKind = ""
            Dim lookName = ""
            Dim lookPath = ""
            Dim autoEnhance = False
            If _dialogExportUseFilter Then
                If String.Equals(_dialogFilterSourceKind, BatchFilterDialogResult.SourceAuto, StringComparison.OrdinalIgnoreCase) Then
                    autoEnhance = True
                Else
                    lookKind = _dialogFilterSourceKind
                    lookName = If(_dialogSelectedFilterChoice, "")
                    _dialogFilterChoicePaths.TryGetValue(lookName, lookPath)
                    If String.IsNullOrWhiteSpace(lookName) Then lookKind = ""
                End If
            End If

            Dim width = 0
            Dim height = 0
            Dim skalierung = 0
            If _dialogExportUseResize Then
                width = ParseResizeDimension(_dialogBatchResizeWidthText)
                height = ParseResizeDimension(_dialogBatchResizeHeightText)
                skalierung = _dialogBatchResizeScalePercent
                If width > 0 OrElse height > 0 Then
                    AppSettingsService.SaveLastBatchResizeSettings(width, height, skalierung,
                                                                   _dialogBatchResizeLockAspect, _dialogBatchResizeInterpolation,
                                                                   _dialogBatchResizeNoUpscale, _dialogBatchResizeLongEdge)
                End If
            End If

            ' Ohne Größenänderung gibt es nichts zu skalieren - der Wasserzeichen-Schalter darf dann
            ' auch nichts bedeuten, sonst wirkte eine unsichtbare Einstellung.
            Dim keepWatermarkSize = _dialogExportUseResize AndAlso _dialogWatermarkKeepSize

            Return New ExportToDialogResult With {
                .AutoEnhance = autoEnhance,
                .LookKind = lookKind,
                .LookName = lookName,
                .LookPath = If(lookPath, ""),
                .LookStrength = _dialogFilterStrength,
                .WatermarkPresetName = If(_dialogExportUseWatermark, If(DialogSelectedWatermarkPresetName, ""), ""),
                .WatermarkPreset = If(_dialogExportUseWatermark,
                                      BuildWatermarkPresetForRun(_dialogWatermarkPresets.FirstOrDefault(
                                          Function(pr) String.Equals(pr.Name, DialogSelectedWatermarkPresetName, StringComparison.OrdinalIgnoreCase))),
                                      Nothing),
                .WatermarkKeepSize = keepWatermarkSize,
                .ResizeWidth = width,
                .ResizeHeight = height,
                .ResizeScalePercent = skalierung,
                .LockAspect = _dialogBatchResizeLockAspect,
                .NoUpscale = _dialogBatchResizeNoUpscale,
                .ResizeInterpolation = _dialogBatchResizeInterpolation,
                .PreserveMetadata = _dialogSaveAsPreserveExif,
                .NamePattern = If(_dialogTargetNamePattern, "").Trim(),
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
        Public Async Function ShowBatchFilterAsync(fileCount As Integer, Optional currentFolder As String = "",
                                                   Optional allowOverwrite As Boolean = True,
                                                   Optional sourcesIncludeJpg As Boolean = False) As Task(Of BatchFilterDialogResult)
            _dialogFilterSourceKind = BatchFilterDialogResult.SourceFilter
            _dialogBatchOverwriteAvailable = allowOverwrite
            _dialogBatchFilterOverwrite = allowOverwrite AndAlso AppSettingsService.Load().BatchFilterOverwriteOriginals
            _dialogBatchSourcesIncludeJpg = sourcesIncludeJpg
            _dialogBatchFilterAppendName = True
            ResetDialogSaveAsMetaOptions()
            DialogTargetNamePattern = AppSettingsService.Load().LastTargetNamePattern
            DialogSelectedFormat = NormalizeSaveAsFormat(DefaultSaveFormat())
            DialogJpgQuality = If(_dialogBatchFilterOverwrite, 95, DefaultJpgQuality())
            DialogSaveAsTarget = "Local"
            InitDialogTargetFolder(currentFolder)
            ' Nach dem Neuaufbau steht der erste Filter in der Auswahl - dessen Setter setzt die Stärke.
            RebuildDialogFilterChoices()
            ' IsDialogFilterSourceAuto/IsDialogFilterChoiceVisible MUESSEN mitgemeldet werden: das
            ' Feld wird oben am Setter vorbei zurueckgesetzt. Ohne die Meldung blieb nach einer
            ' Auto-Auswahl die Vorgabenliste unsichtbar und der Auto-Knopf aktiv - der Dialog war
            ' fuer Filter unbedienbar.
            For Each name In {NameOf(DialogFilterSourceKind), NameOf(IsDialogFilterSourceFilter),
                              NameOf(IsDialogFilterSourceXmpPreset), NameOf(IsDialogFilterSourceLut),
                              NameOf(IsDialogFilterSourceAuto), NameOf(IsDialogFilterChoiceVisible),
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
            PersistDialogTargetNamePattern()

            Dim path As String = Nothing
            _dialogFilterChoicePaths.TryGetValue(_dialogSelectedFilterChoice, path)
            Return New BatchFilterDialogResult With {
                .SourceKind = _dialogFilterSourceKind,
                .DisplayName = If(IsDialogFilterSourceAuto, "Auto", _dialogSelectedFilterChoice),
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
                .CopyKeywords = _dialogSaveAsCopyKeywords,
                .NamePattern = If(_dialogTargetNamePattern, "").Trim(),
                .PreserveMetadata = _dialogSaveAsPreserveExif
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

        Public Property DialogSecondaryText As String
            Get
                Return _dialogSecondaryText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogSecondaryText, value)
                Me.RaisePropertyChanged(NameOf(IsDialogSecondaryVisible))
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
                If _dialogSearchRatings.Count = 0 Then Return LocalizationService.T("Alle")
                Return String.Join(", ", _dialogSearchRatings.OrderBy(Function(r) r).Select(Function(r)
                    If r = 0 Then Return LocalizationService.T("Nicht bewertet")
                    Return If(r = 1, LocalizationService.T("1 Stern"), $"{r} {LocalizationService.T("Sterne")}")
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
                ' Dialogs gemeldet und blieb je nach Vorgeschichte weg („mal drin, mal
                ' draußen").
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsMetaOptions))
                Me.RaisePropertyChanged(NameOf(DialogShowsFileConflict))
                Me.RaisePropertyChanged(NameOf(DialogShowsStandardActions))
                Me.RaisePropertyChanged(NameOf(DialogShowsBatchRename))
                Me.RaisePropertyChanged(NameOf(DialogShowsSearch))
                Me.RaisePropertyChanged(NameOf(DialogShowsBatchResize))
                Me.RaisePropertyChanged(NameOf(DialogShowsBatchFilter))
                Me.RaisePropertyChanged(NameOf(DialogShowsWatermarkPreset))
                Me.RaisePropertyChanged(NameOf(DialogShowsExportTo))
                Me.RaisePropertyChanged(NameOf(IsDialogBatchOverwriteAvailable))
                Me.RaisePropertyChanged(NameOf(IsDialogNamePatternVisible))
                Me.RaisePropertyChanged(NameOf(DialogUsesWideLayout))
                Me.RaisePropertyChanged(NameOf(DialogWidth))
                Me.RaisePropertyChanged(NameOf(IsDialogJpgQualityVisible))
                Me.RaisePropertyChanged(NameOf(IsDialogOverwriteJpgQualityVisible))
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
                       _dialogKind <> AppDialogKind.WatermarkPreset AndAlso
                       _dialogKind <> AppDialogKind.ExportTo
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
                       (_dialogKind = AppDialogKind.BatchResize AndAlso Not _dialogBatchResizeOverwrite) OrElse
                       _dialogKind = AppDialogKind.ExportTo
            End Get
        End Property

        Public ReadOnly Property DialogShowsBatchFilter As Boolean
            Get
                Return _dialogKind = AppDialogKind.BatchFilter
            End Get
        End Property

        ''' Einzeloptionen „Katalog-Metadaten übernehmen" - überall dort, wo
        ''' aus EINER Quelldatei eine neue Datei entsteht: Speichern-unter, Konvertieren-nach sowie
        ''' Bildgröße-ändern und Filter-anwenden als Kopie. Beim Überschreiben behält die Datei ihren
        ''' Katalog-Eintrag, dort wäre die Zeile sinnlos.
        Public ReadOnly Property DialogShowsSaveAsMetaOptions As Boolean
            Get
                Return _dialogKind = AppDialogKind.SaveAs OrElse
                       _dialogKind = AppDialogKind.BatchConvert OrElse
                       (_dialogKind = AppDialogKind.BatchResize AndAlso Not _dialogBatchResizeOverwrite) OrElse
                       (_dialogKind = AppDialogKind.WatermarkPreset AndAlso Not _dialogBatchWatermarkOverwrite) OrElse
                       (_dialogKind = AppDialogKind.BatchFilter AndAlso Not _dialogBatchFilterOverwrite) OrElse
                       _dialogKind = AppDialogKind.ExportTo
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
                Case "Exif"
                    DialogSaveAsPreserveExif = Not DialogSaveAsPreserveExif
            End Select
        End Sub

        ''' <summary>Datei-Metadaten (EXIF/XMP) in die Zieldatei uebernehmen. Steht bewusst im
        ''' selben "Uebernehmen"-Bereich wie Bewertung/Favorit/Etikett/Stichworte: fuer den Nutzer
        ''' ist das dieselbe Frage - was von der Quelle mitkommt. Vorher war es im Export eine
        ''' einzelne Checkbox weiter oben und in den uebrigen Stapelfunktionen gar nicht sichtbar.
        ''' Vorbelegt aus der Einstellung "Metadaten beim Speichern erhalten".</summary>
        Public Property DialogSaveAsPreserveExif As Boolean
            Get
                Return _dialogSaveAsPreserveExif
            End Get
            Set(value As Boolean)
                If _dialogSaveAsPreserveExif = value Then Return
                _dialogSaveAsPreserveExif = value
                Me.RaisePropertyChanged(NameOf(DialogSaveAsPreserveExif))
            End Set
        End Property

        Private Sub ResetDialogSaveAsMetaOptions()
            _dialogSaveAsCopyRating = True
            _dialogSaveAsCopyFavorite = True
            _dialogSaveAsCopyColorLabel = True
            _dialogSaveAsCopyKeywords = True
            _dialogSaveAsPreserveExif = AppSettingsService.Load().PreserveMetadataOnSave
            For Each name In {NameOf(DialogSaveAsCopyRating), NameOf(DialogSaveAsCopyFavorite),
                              NameOf(DialogSaveAsCopyColorLabel), NameOf(DialogSaveAsCopyKeywords),
                              NameOf(DialogSaveAsPreserveExif),
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

        Private _dialogBatchOverwriteAvailable As Boolean = True

        ''' <summary>Ob "Originale ueberschreiben" in den Stapel-Dialogen angeboten wird. Falsch,
        ''' sobald die Auswahl ein Format enthaelt, fuer das es keinen Encoder gibt (BMP/GIF) -
        ''' der Schreibversuch waere dort still wirkungslos (ImageProcessor.CanEncodeToTargetExtension
        ''' weist das Ziel ab). Dann entstehen ausschliesslich neue Dateien.</summary>
        Public ReadOnly Property IsDialogBatchOverwriteAvailable As Boolean
            Get
                Return _dialogBatchOverwriteAvailable
            End Get
        End Property

        ''' Hinweis unter dem ausgeblendeten Haken - sonst raetselt der Nutzer, warum die Option fehlt.
        Public ReadOnly Property DialogBatchOverwriteUnavailableHint As String
            Get
                Return LocalizationService.T("Diese Auswahl lässt sich nicht zurückschreiben - es entstehen neue Dateien.")
            End Get
        End Property

        Public ReadOnly Property DialogShowsExportTo As Boolean
            Get
                Return _dialogKind = AppDialogKind.ExportTo
            End Get
        End Property

        ''' Das Zieldateinamen-Muster gibt es nur bei Stapel-Zielen mit NEUEN Dateien - der
        ''' Einzel-Speichern-Dialog hat sein eigenes Namensfeld, beim Ueberschreiben behaelt
        ''' jede Datei ihren Namen (dann ist der SaveAs-Block ohnehin ausgeblendet).
        Public ReadOnly Property IsDialogNamePatternVisible As Boolean
            Get
                Return DialogShowsSaveAsOptions AndAlso _dialogKind <> AppDialogKind.SaveAs
            End Get
        End Property

        Private _dialogTargetNamePattern As String = ""

        ''' <summary>Dateinamen-Muster fuer die ZIELDATEIEN der Stapel-Funktionen (leer = Originalname
        ''' behalten). Gleiche Platzhalter wie beim Stapel-Umbenennen (ExpandTargetNamePattern).</summary>
        Public Property DialogTargetNamePattern As String
            Get
                Return _dialogTargetNamePattern
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_dialogTargetNamePattern, value)
            End Set
        End Property

        ''' Speichern-unter und Konvertieren-nach zählen mit dazu: die Zeile „Übernehmen" trägt vier
        ''' Umschalt-Buttons nebeneinander, die bei 440 px nicht mehr aufs Formular passen
        '''.
        Public ReadOnly Property DialogUsesWideLayout As Boolean
            Get
                Return DialogShowsFileConflict OrElse DialogShowsBatchRename OrElse DialogShowsSearch OrElse
                       DialogShowsBatchResize OrElse DialogShowsSaveAsMetaOptions
            End Get
        End Property

        Public ReadOnly Property DialogWidth As Double
            Get
                If DialogShowsFileConflict OrElse DialogShowsBatchRename OrElse DialogShowsSearch Then Return 820
                If DialogShowsBatchResize OrElse DialogShowsExportTo Then Return 780
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

        ''' <summary>Startwert des Qualitätsreglers: die in den Einstellungen gepflegte
        ''' JPEG-Qualität. Die Stapel- und Export-Dialoge starteten mit einer fest verdrahteten 90
        ''' und gingen damit ausgerechnet an der Einstellung vorbei, die dafür da ist.</summary>
        Private Shared Function DefaultJpgQuality() As Integer
            Return AppSettingsService.NormalizeJpgSaveQuality(AppSettingsService.Load().JpgSaveQuality)
        End Function

        ''' <summary>Vorgewähltes Zielformat aus den Einstellungen. FPX gibt es NUR beim Speichern
        ''' unter; wo es nicht in der Auswahlliste steht (Stapel, Export), fällt die Vorgabe auf JPG
        ''' zurück - sonst stünde in der Liste eine Auswahl, die es dort gar nicht gibt.</summary>
        Public Shared Function DefaultSaveFormat(Optional allowFpx As Boolean = False) As String
            Dim value = AppSettingsService.NormalizeDefaultSaveFormat(AppSettingsService.Load().DefaultSaveFormat)
            If String.Equals(value, "FPX", StringComparison.OrdinalIgnoreCase) AndAlso
               (Not allowFpx OrElse Not FpxService.Enabled) Then Return "JPG"
            Return value
        End Function

        Public ReadOnly Property IsDialogJpgQualityVisible As Boolean
            Get
                Return DialogShowsSaveAsOptions AndAlso String.Equals(_dialogSelectedFormat, "JPG", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' <summary>Die EIGENE Qualitaetszeile fuer das UEBERSCHREIBEN: der Speichern-unter-Block
        ''' (Format, Ziel, Qualitaet) ist dann komplett ausgeblendet, neu encodiert wird die Datei
        ''' trotzdem. Vorher wurde still mit einem festen bzw. dem Vorgabewert gespeichert, ohne
        ''' dass man es sehen oder aendern konnte (Nutzerwunsch 2026-07-31). Sichtbar nur, wenn
        ''' JPG-Quellen im Stapel sind - bei reinen PNG-Stapeln gibt es nichts einzustellen.</summary>
        Public ReadOnly Property IsDialogOverwriteJpgQualityVisible As Boolean
            Get
                Return DialogOverwriteActive AndAlso _dialogBatchSourcesIncludeJpg
            End Get
        End Property

        ''' <summary>Ueberschreibt der GERADE OFFENE Stapeldialog die Originale? Die drei Haken
        ''' leben in getrennten Feldern, die nur ihr eigener Dialog beim Oeffnen zuruecksetzt -
        ''' ohne die Weiche ueber die Dialogart zoege ein stehengebliebener Haken eines anderen
        ''' Dialogs den Qualitaetsregler mit herein.</summary>
        Private ReadOnly Property DialogOverwriteActive As Boolean
            Get
                Return (_dialogKind = AppDialogKind.BatchResize AndAlso _dialogBatchResizeOverwrite) OrElse
                       (_dialogKind = AppDialogKind.BatchFilter AndAlso _dialogBatchFilterOverwrite) OrElse
                       (_dialogKind = AppDialogKind.WatermarkPreset AndAlso _dialogBatchWatermarkOverwrite)
            End Get
        End Property

        ''' <summary>Sind JPG-Dateien im Stapel des offenen Dialogs? Entscheidet zusammen mit dem
        ''' Ueberschreiben-Haken ueber den Qualitaetsregler.</summary>
        Private _dialogBatchSourcesIncludeJpg As Boolean = False

        ''' Überschreiben blendet Format, Ziel und Namenszusatz aus - wie beim Stapel-Filter.
        Public Property DialogBatchResizeOverwrite As Boolean
            Get
                Return _dialogBatchResizeOverwrite
            End Get
            Set(value As Boolean)
                If _dialogBatchResizeOverwrite = value Then Return
                _dialogBatchResizeOverwrite = value
                ' Die Wahl bleibt über Sitzungen erhalten.
                AppSettingsService.SaveBatchResizeOverwriteOriginals(value)
                ' Beim Ueberschreiben startet die Qualitaet auf 95 (dem frueheren festen Wert),
                ' bei Kopien gilt wieder die Einstellung.
                DialogJpgQuality = If(value, 95, DefaultJpgQuality())
                Me.RaisePropertyChanged(NameOf(DialogBatchResizeOverwrite))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsOptions))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsMetaOptions))
                Me.RaisePropertyChanged(NameOf(DialogWidth))
                Me.RaisePropertyChanged(NameOf(IsDialogJpgQualityVisible))
                Me.RaisePropertyChanged(NameOf(IsDialogOverwriteJpgQualityVisible))
            End Set
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
                ' Das Verhaeltnis VOR der Aenderung merken - danach ist es ja schon verstellt.
                Dim oldWidth = ParseBatchResizeDimension(_dialogBatchResizeWidthText)
                Dim oldHeight = ParseBatchResizeDimension(_dialogBatchResizeHeightText)
                Me.RaiseAndSetIfChanged(_dialogBatchResizeWidthText, normalized)
                CoupleBatchResizeEdge(vonBreite:=True, oldWidth:=oldWidth, oldHeight:=oldHeight)
            End Set
        End Property

        Public Property DialogBatchResizeHeightText As String
            Get
                Return _dialogBatchResizeHeightText
            End Get
            Set(value As String)
                _dialogBatchResizeScalePercent = 0
                Dim normalized = NormalizeResizeDimensionText(value)
                Dim oldWidth = ParseBatchResizeDimension(_dialogBatchResizeWidthText)
                Dim oldHeight = ParseBatchResizeDimension(_dialogBatchResizeHeightText)
                Me.RaiseAndSetIfChanged(_dialogBatchResizeHeightText, normalized)
                CoupleBatchResizeEdge(vonBreite:=False, oldWidth:=oldWidth, oldHeight:=oldHeight)
            End Set
        End Property

        ''' Laeuft gerade eine gekoppelte Aenderung? Sonst riefe der eine Setter den anderen und der
        ''' wieder den ersten.
        Private _dialogBatchResizeSyncing As Boolean = False

        ''' <summary>Zieht bei gesperrtem Seitenverhaeltnis die jeweils andere Kante mit - im
        ''' Verhaeltnis der BEIDEN EINGETRAGENEN WERTE.
        '''
        ''' Wichtig ist, WORAUS das Verhaeltnis kommt. Eine frühere Fassung nahm es aus einem
        ''' Beispielbild des Stapels; bei einem quadratischen Beispiel liessen sich dann nur noch
        ''' identische Werte eintragen, und einem gemischten Stapel war das Format dieses einen
        ''' Bildes aufgezwungen. Das Verhaeltnis der beiden Felder ist dagegen genau das, was der
        ''' Nutzer selbst hingeschrieben hat.
        '''
        ''' Gekoppelt wird nur, wenn BEIDE Felder gefuellt sind: ein einzelner Wert bedeutet
        ''' weiterhin "laengste Kante" und haette gar kein Verhaeltnis, aus dem sich etwas ableiten
        ''' liesse.</summary>
        Private Sub CoupleBatchResizeEdge(vonBreite As Boolean, oldWidth As Integer, oldHeight As Integer)
            If _dialogBatchResizeSyncing OrElse Not _dialogBatchResizeLockAspect Then Return
            If oldWidth <= 0 OrElse oldHeight <= 0 Then Return
            Dim verhaeltnis = oldWidth / CDbl(oldHeight)
            If verhaeltnis <= 0.0001 Then Return

            _dialogBatchResizeSyncing = True
            Try
                If vonBreite Then
                    Dim width = ParseBatchResizeDimension(_dialogBatchResizeWidthText)
                    If width <= 0 Then Return
                    Dim height = Math.Max(1, CInt(Math.Round(width / verhaeltnis)))
                    _dialogBatchResizeHeightText = height.ToString(CultureInfo.InvariantCulture)
                    Me.RaisePropertyChanged(NameOf(DialogBatchResizeHeightText))
                Else
                    Dim height = ParseBatchResizeDimension(_dialogBatchResizeHeightText)
                    If height <= 0 Then Return
                    Dim width = Math.Max(1, CInt(Math.Round(height * verhaeltnis)))
                    _dialogBatchResizeWidthText = width.ToString(CultureInfo.InvariantCulture)
                    Me.RaisePropertyChanged(NameOf(DialogBatchResizeWidthText))
                End If
            Finally
                _dialogBatchResizeSyncing = False
            End Try
        End Sub

        Private Shared Function ParseBatchResizeDimension(text As String) As Integer
            Dim value As Integer
            If Not Integer.TryParse(If(text, "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, value) Then Return 0
            Return Math.Max(0, value)
        End Function

        Public Property DialogBatchResizeLockAspect As Boolean
            Get
                Return _dialogBatchResizeLockAspect
            End Get
            Set(value As Boolean)
                ' Umschalten rechnet nichts um: die eingetragenen Werte bleiben stehen und
                ' bedeuten je nach Schalter Zielkasten (an) oder exakte Zielmasse (aus).
                Me.RaiseAndSetIfChanged(_dialogBatchResizeLockAspect, value)
            End Set
        End Property

        ''' <summary>„Lange Kante": EIN Wert statt Breite und Höhe. Er begrenzt bei jedem Bild die
        ''' längere Kante - ein Stapel aus Quer- und Hochformaten kommt so einheitlich heraus, ohne
        ''' dass man für jede Ausrichtung getrennt rechnen müsste. Technisch ist das genau der schon
        ''' vorhandene Fall „nur eine Kante gesetzt" (siehe ImageProcessor.ApplyResize); der Schalter
        ''' macht ihn bedienbar, statt ihn im leeren Höhenfeld zu verstecken. Das Seitenverhältnis
        ''' ist dabei zwingend gehalten - ohne zweite Kante gäbe es sonst nichts zu strecken.</summary>
        Public Property DialogBatchResizeLongEdge As Boolean
            Get
                Return _dialogBatchResizeLongEdge
            End Get
            Set(value As Boolean)
                If _dialogBatchResizeLongEdge = value Then Return
                _dialogBatchResizeLongEdge = value
                If value Then
                    _dialogBatchResizeLockAspect = True
                    CollapseBatchResizeToLongEdge()
                End If
                Me.RaisePropertyChanged(NameOf(DialogBatchResizeLongEdge))
                Me.RaisePropertyChanged(NameOf(DialogBatchResizeLockAspect))
                Me.RaisePropertyChanged(NameOf(IsDialogBatchResizeLockAspectEnabled))
                Me.RaisePropertyChanged(NameOf(IsDialogBatchResizeEdgeFieldsVisible))
                Me.RaisePropertyChanged(NameOf(DialogBatchResizeWidthText))
                Me.RaisePropertyChanged(NameOf(DialogBatchResizeHeightText))
            End Set
        End Property

        ''' Im Lange-Kante-Modus ist das Seitenverhältnis zwingend gehalten - der Haken bleibt
        ''' sichtbar und gesetzt, damit man sieht was gilt, ist aber nicht abwählbar.
        Public ReadOnly Property IsDialogBatchResizeLockAspectEnabled As Boolean
            Get
                Return Not _dialogBatchResizeLongEdge
            End Get
        End Property

        ''' Breite und Höhe weichen im Lange-Kante-Modus dem einen Kantenfeld.
        Public ReadOnly Property IsDialogBatchResizeEdgeFieldsVisible As Boolean
            Get
                Return Not _dialogBatchResizeLongEdge
            End Get
        End Property

        ''' <summary>Führt zwei eingetragene Kanten auf die längere zusammen: sie wird zur Kantenlänge,
        ''' die zweite bleibt leer. Nur so bedeutet der Wert für ApplyResize „längste Kante".</summary>
        Private Sub CollapseBatchResizeToLongEdge()
            Dim width = ParseBatchResizeDimension(_dialogBatchResizeWidthText)
            Dim height = ParseBatchResizeDimension(_dialogBatchResizeHeightText)
            Dim edge = Math.Max(width, height)
            _dialogBatchResizeWidthText = If(edge > 0, edge.ToString(CultureInfo.InvariantCulture), "")
            _dialogBatchResizeHeightText = ""
        End Sub

        ''' <summary>"Nicht vergroessern": Bilder unterhalb der Zielgroesse bleiben unveraendert
        ''' (Gegenstueck zu "Don't Enlarge" in gaengigen Export-Dialogen).</summary>
        Public Property DialogBatchResizeNoUpscale As Boolean
            Get
                Return _dialogBatchResizeNoUpscale
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_dialogBatchResizeNoUpscale, value)
            End Set
        End Property

        Public Property DialogBatchResizeInterpolationLabel As String
            Get
                Select Case _dialogBatchResizeInterpolation
                    Case ResizeInterpolationMode.Nearest
                        Return LocalizationService.T("Nächstgelegen")
                    Case ResizeInterpolationMode.Bicubic
                        Return LocalizationService.T("Bikubisch")
                    Case Else
                        Return LocalizationService.T("Bilinear")
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
                ' Eine andere Vorlage bringt eigene Lage und Groesse mit - die Felder muessen ihr
                ' folgen, sonst stuenden die Werte der vorigen darin.
                LoadWatermarkPresetIntoDialogFields(
                    _dialogWatermarkPresets.FirstOrDefault(
                        Function(pr) String.Equals(pr.Name, _dialogSelectedWatermarkPresetName, StringComparison.OrdinalIgnoreCase)))
            End Set
        End Property

        ' --- Wasserzeichen im Stapel: Lage und Groesse -------------------------------------
        ' Die Vorlage bringt Anker und Groesse mit, aber ein Stapel braucht sie oft anders als der
        ' Einzelfall (anderes Zielformat, kleinere Bilder). Beides ist hier fuer diesen Lauf
        ' aenderbar, OHNE die gespeicherte Vorlage zu veraendern - der Dialog gibt eine Kopie
        ' zurueck (siehe ShowWatermarkPresetDialogAsync).

        Private _dialogWatermarkAnchor As String = "BottomRight"
        Private _dialogWatermarkWidthPixels As Integer = 480
        Private _dialogWatermarkHeightPixels As Integer = 180
        Private _dialogWatermarkWidthText As String = "480"
        ''' Traegt die gewaehlte Vorlage eine Bilddatei? Nur dann hat die Breite eine eindeutige
        ''' Bedeutung (siehe IsDialogWatermarkWidthVisible).
        Private _dialogWatermarkHasImage As Boolean = False
        Private _dialogWatermarkLockAspect As Boolean = True
        ''' Breite/Hoehe der gewaehlten Vorlage. Bezugsverhaeltnis fuer die gekoppelte Hoehe:
        ''' bei einem BILD-Wasserzeichen hat der Editor die Box bereits auf das Bildformat
        ''' geschnappt, bei einem TEXT-Wasserzeichen ist es das gewaehlte Kaestchen. In beiden
        ''' Faellen ist es genau das Verhaeltnis, das der Nutzer in der Vorlage gesehen hat -
        ''' die Bilddatei dafuer zu oeffnen waere teurer und koennte gar nicht mehr da sein.
        Private _dialogWatermarkAspect As Double = 480.0 / 180.0

        Public Property DialogWatermarkAnchor As String
            Get
                Return _dialogWatermarkAnchor
            End Get
            Set(value As String)
                Dim normalized = AppSettingsService.NormalizeAnnotationAnchorName(value)
                If String.Equals(_dialogWatermarkAnchor, normalized, StringComparison.Ordinal) Then Return
                _dialogWatermarkAnchor = normalized
                Me.RaisePropertyChanged(NameOf(DialogWatermarkAnchor))
            End Set
        End Property

        ''' <summary>Breite als TEXT, nicht als Zahl - wie in den uebrigen Stapel-Formularen.
        ''' Ein NumericUpDown liefert beim Leeren Nothing und warf beim Binden auf ein Integer eine
        ''' InvalidCastException mitten im Dialog; ausserdem bringt es sein eigenes Aussehen mit,
        ''' das neben den Feldern der Nachbarzeilen fremd wirkt. Ein leeres Feld zaehlt hier als
        ''' "unveraendert" und faellt auf den Wert der Vorlage zurueck.</summary>
        Public Property DialogWatermarkWidthText As String
            Get
                Return _dialogWatermarkWidthText
            End Get
            Set(value As String)
                Dim normalized = NormalizeWatermarkSizeText(value)
                Me.RaiseAndSetIfChanged(_dialogWatermarkWidthText, normalized)
                Dim widthValue = ParseWatermarkSize(normalized, 0)
                If widthValue <= 0 Then Return
                _dialogWatermarkWidthPixels = widthValue
                ' Gesperrtes Verhaeltnis: die Hoehe folgt der Breite. Der umgekehrte Weg ist
                ' gesperrt (Feld deaktiviert), sonst zoegen sich beide gegenseitig.
                If _dialogWatermarkLockAspect AndAlso _dialogWatermarkAspect > 0.0001 Then
                    _dialogWatermarkHeightPixels = Math.Max(1, Math.Min(100000, CInt(Math.Round(widthValue / _dialogWatermarkAspect))))
                    Me.RaisePropertyChanged(NameOf(DialogWatermarkHeightText))
                End If
            End Set
        End Property

        ''' Nur Ziffern durchlassen, Laenge begrenzen - dieselbe Haltung wie bei der Bildgroesse:
        ''' das Feld wehrt Unsinn ab, statt ihn erst beim Bestaetigen zu melden.
        Private Shared Function NormalizeWatermarkSizeText(value As String) As String
            Dim digits = New String(If(value, "").Where(AddressOf Char.IsDigit).ToArray())
            If digits.Length > 6 Then digits = digits.Substring(0, 6)
            Return digits.TrimStart("0"c)
        End Function

        Private Shared Function ParseWatermarkSize(value As String, fallback As Integer) As Integer
            Dim parsed As Integer
            If Not Integer.TryParse(If(value, "").Trim(), Globalization.NumberStyles.Integer,
                                    Globalization.CultureInfo.InvariantCulture, parsed) Then Return fallback
            Return Math.Max(0, Math.Min(100000, parsed))
        End Function


        ''' <summary>Die mitlaufende Hoehe - NUR zum Anzeigen. Sie ist kein zweiter Regler, sondern
        ''' das Ergebnis aus Breite und Seitenverhaeltnis der Vorlage; sichtbar, damit man beim
        ''' Eintippen einer Breite sofort sieht, wie gross das Wasserzeichen wirklich wird, statt es
        ''' im Kopf auszurechnen.</summary>
        Public ReadOnly Property DialogWatermarkHeightText As String
            Get
                Return _dialogWatermarkHeightPixels.ToString(Globalization.CultureInfo.InvariantCulture)
            End Get
        End Property

        ''' <summary>Die Breite ist nur dort einstellbar, wo sie EINDEUTIG ist: bei einem
        ''' BILD-Wasserzeichen mit gesperrtem Seitenverhaeltnis folgt die Hoehe zwingend. Bei einem
        ''' Textwasserzeichen bestimmt die Schriftgroesse die Ausdehnung, und ohne Sperre waeren es
        ''' zwei unabhaengige Werte - beides gehoert in die Vorlage, nicht in den Stapeldialog.
        ''' Ein zweiter Sperrschalter hier waere eine zweite Wahrheit ueber dieselbe Sache.</summary>
        Public ReadOnly Property IsDialogWatermarkWidthVisible As Boolean
            Get
                Return _dialogWatermarkHasImage AndAlso _dialogWatermarkLockAspect
            End Get
        End Property

        Public ReadOnly Property SetDialogWatermarkAnchorCommand As ICommand

        ''' Uebernimmt Anker, Groesse und Sperre der gewaehlten Vorlage in die Dialogfelder.
        Private Sub LoadWatermarkPresetIntoDialogFields(preset As WatermarkPresetSettings)
            If preset Is Nothing Then Return
            _dialogWatermarkAnchor = AppSettingsService.NormalizeAnnotationAnchorName(preset.Anchor)
            _dialogWatermarkWidthPixels = Math.Max(1, Math.Min(100000, CInt(Math.Round(preset.WidthPixels))))
            _dialogWatermarkHeightPixels = Math.Max(1, Math.Min(100000, CInt(Math.Round(preset.HeightPixels))))
            _dialogWatermarkLockAspect = preset.LockAspect
            _dialogWatermarkHasImage = Not String.IsNullOrWhiteSpace(preset.ImagePath)
            _dialogWatermarkAspect = If(_dialogWatermarkHeightPixels > 0,
                                        _dialogWatermarkWidthPixels / CDbl(_dialogWatermarkHeightPixels), 1.0)
            _dialogWatermarkWidthText = _dialogWatermarkWidthPixels.ToString(Globalization.CultureInfo.InvariantCulture)
            For Each name In {NameOf(DialogWatermarkAnchor), NameOf(DialogWatermarkWidthText),
                              NameOf(DialogWatermarkHeightText), NameOf(IsDialogWatermarkWidthVisible)}
                Me.RaisePropertyChanged(name)
            Next
        End Sub

        Public Property DialogBatchWatermarkOverwrite As Boolean
            Get
                Return _dialogBatchWatermarkOverwrite
            End Get
            Set(value As Boolean)
                If _dialogBatchWatermarkOverwrite = value Then Return
                _dialogBatchWatermarkOverwrite = value
                AppSettingsService.SaveBatchWatermarkOverwriteOriginals(value)
                ' Beim Ueberschreiben startet die Qualitaet auf 95 (dem frueheren festen Wert),
                ' bei Kopien gilt wieder die Einstellung.
                DialogJpgQuality = If(value, 95, DefaultJpgQuality())
                Me.RaisePropertyChanged(NameOf(DialogBatchWatermarkOverwrite))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsOptions))
                Me.RaisePropertyChanged(NameOf(DialogShowsSaveAsMetaOptions))
                Me.RaisePropertyChanged(NameOf(DialogWidth))
                Me.RaisePropertyChanged(NameOf(IsDialogJpgQualityVisible))
                Me.RaisePropertyChanged(NameOf(IsDialogOverwriteJpgQualityVisible))
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
                    ' Reine Kantenlaenge: EIN Wert, der bei gehaltenem Seitenverhaeltnis die
                    ' laengste Kante jedes Bildes begrenzt (siehe ImageProcessor.ApplyResize).
                    Dim edge As Integer
                    If Integer.TryParse(preset, edge) AndAlso edge > 0 Then
                        _dialogBatchResizeWidthText = edge.ToString(CultureInfo.InvariantCulture)
                        _dialogBatchResizeHeightText = ""
                    End If
            End Select
            ' Die Kasten-Vorgaben (UHD, Full-HD, SD) und die Prozentwerte tragen zwei Kanten ein -
            ' im Lange-Kante-Modus zählt davon nur die längere.
            If _dialogBatchResizeLongEdge Then CollapseBatchResizeToLongEdge()
            RaiseDialogBatchResizeProperties()
        End Sub

        Private Sub RaiseDialogBatchResizeProperties()
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeWidthText))
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeHeightText))
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeLockAspect))
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeLongEdge))
            Me.RaisePropertyChanged(NameOf(IsDialogBatchResizeLockAspectEnabled))
            Me.RaisePropertyChanged(NameOf(IsDialogBatchResizeEdgeFieldsVisible))
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeNoUpscale))
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeInterpolationLabel))
            Me.RaisePropertyChanged(NameOf(DialogBatchResizeOverwrite))
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

        ' Die Knopfbeschriftungen sind Optional-Vorgaben und müssen deshalb Konstanten bleiben
        ' (VB verlangt konstante Ausdrücke) - übersetzt wird daher IM RUMPF, nicht in der Signatur.
        ' Genau daran lag es, dass „OK"/„Abbrechen" in jeder Sprache deutsch blieben.
        Public Async Function ShowMessageAsync(titleText As String, messageText As String, Optional confirmText As String = "OK") As Task Implements IViewerHost.ShowMessageAsync, IEditorHost.ShowMessageAsync
            Await ShowDialogAsync(AppDialogKind.Message, titleText, messageText, "", LocalizationService.T(confirmText), "")
        End Function

        Public Async Function ShowConfirmAsync(titleText As String, messageText As String, Optional confirmText As String = "OK", Optional cancelText As String = "Abbrechen") As Task(Of Boolean) Implements IViewerHost.ShowConfirmAsync, IEditorHost.ShowConfirmAsync
            Dim result = Await ShowDialogAsync(AppDialogKind.Message, titleText, messageText, "",
                                               LocalizationService.T(confirmText), LocalizationService.T(cancelText))
            Return result IsNot Nothing
        End Function

        Public Async Function ShowSaveChangesAsync(titleText As String, messageText As String) As Task(Of SaveChangesDialogResult) Implements IEditorHost.ShowSaveChangesAsync
            Dim result = Await ShowDialogAsync(AppDialogKind.Message, titleText, messageText, "",
                                               LocalizationService.T("Speichern"), LocalizationService.T("Abbrechen"),
                                               LocalizationService.T("Nicht speichern"))
            If result Is Nothing Then Return SaveChangesDialogResult.Cancelled
            If String.Equals(result, "Secondary", StringComparison.Ordinal) Then Return SaveChangesDialogResult.Discard
            Return SaveChangesDialogResult.Save
        End Function

        Public Function ShowInputAsync(kind As AppDialogKind, titleText As String, messageText As String, initialText As String, Optional confirmText As String = "OK", Optional cancelText As String = "Abbrechen") As Task(Of String)
            Return ShowDialogAsync(kind, titleText, messageText, initialText,
                                   LocalizationService.T(confirmText), LocalizationService.T(cancelText))
        End Function

        ''' <param name="currentFolder">Der Ordner, in dem die Galerie gerade steht - Vorgabe für den
        ''' Zielordner, wenn Kopien geschrieben werden (wie beim Stapel-Filter).</param>
        ''' <param name="singleImage">Genau EIN Bild. Dann tragen Breite und Höhe seine tatsächliche
        ''' Größe, nicht die zuletzt benutzten Werte: bei einem einzelnen Bild ist die aktuelle Größe
        ''' der Ausgangspunkt, den man ändern will. Bei einem Stapel gibt es die eine Größe nicht,
        ''' dort bleiben die zuletzt benutzten Werte richtig.</param>
        Public Async Function ShowBatchResizeAsync(Optional samplePath As String = Nothing, Optional currentFolder As String = "",
                                                   Optional allowOverwrite As Boolean = True,
                                                   Optional singleImage As Boolean = False,
                                                   Optional sourcesIncludeJpg As Boolean = False) As Task(Of BatchResizeResult)
            Dim settings = AppSettingsService.Load()
            _dialogBatchOverwriteAvailable = allowOverwrite
            _dialogBatchSourcesIncludeJpg = sourcesIncludeJpg
            _dialogBatchResizeWidthText = If(settings.LastBatchResizeWidth > 0, settings.LastBatchResizeWidth.ToString(CultureInfo.InvariantCulture), "")
            _dialogBatchResizeHeightText = If(settings.LastBatchResizeHeight > 0, settings.LastBatchResizeHeight.ToString(CultureInfo.InvariantCulture), "")
            _dialogBatchResizeLockAspect = settings.LastBatchResizeLockAspect
            _dialogBatchResizeNoUpscale = settings.LastBatchResizeNoUpscale
            _dialogBatchResizeLongEdge = settings.LastBatchResizeLongEdge
            If _dialogBatchResizeLongEdge Then _dialogBatchResizeLockAspect = True
            _dialogBatchResizeInterpolation = ParseResizeInterpolationMode(settings.LastBatchResizeInterpolation)
            _dialogBatchResizeScalePercent = settings.LastBatchResizeScalePercent
            _dialogBatchResizeSourceWidth = 0
            _dialogBatchResizeSourceHeight = 0
            _dialogBatchResizeOverwrite = allowOverwrite AndAlso settings.BatchResizeOverwriteOriginals
            ResetDialogSaveAsMetaOptions()
            DialogTargetNamePattern = AppSettingsService.Load().LastTargetNamePattern
            DialogSelectedFormat = NormalizeSaveAsFormat(DefaultSaveFormat())
            DialogJpgQuality = If(_dialogBatchResizeOverwrite, 95, DefaultJpgQuality())
            DialogSaveAsTarget = "Local"
            InitDialogTargetFolder(currentFolder)

            If Not String.IsNullOrWhiteSpace(samplePath) AndAlso File.Exists(samplePath) Then
                Try
                    Dim size = ImageProcessor.GetImageSize(samplePath)
                    _dialogBatchResizeSourceWidth = size.Width
                    _dialogBatchResizeSourceHeight = size.Height
                Catch
                End Try
            End If

            ' Einzelbild: die Felder zeigen seine tatsaechliche Groesse. Der gemerkte
            ' Prozentsatz wird dabei zurueckgesetzt - sonst rechnete er sofort wieder darueber
            ' hinweg, und der Nutzer saehe erneut nicht, wie gross das Bild ueberhaupt ist.
            If singleImage AndAlso _dialogBatchResizeSourceWidth > 0 AndAlso _dialogBatchResizeSourceHeight > 0 Then
                _dialogBatchResizeWidthText = _dialogBatchResizeSourceWidth.ToString(CultureInfo.InvariantCulture)
                _dialogBatchResizeHeightText = _dialogBatchResizeSourceHeight.ToString(CultureInfo.InvariantCulture)
                _dialogBatchResizeScalePercent = 0
            End If

            If _dialogBatchResizeScalePercent > 0 Then SetDialogBatchResizeTextsFromScale(_dialogBatchResizeScalePercent)
            If _dialogBatchResizeLongEdge Then CollapseBatchResizeToLongEdge()

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

            ' Bewusst NICHT die fehlende Kante aus dem Beispielbild ergaenzen: ein einzeln
            ' eingetragener Wert bedeutet bei gehaltenem Seitenverhaeltnis die LAENGSTE KANTE
            ' (ApplyResize entscheidet das pro Bild). Die Ergaenzung aus EINEM Beispielbild
            ' haette einem gemischten Stapel dessen Ausrichtung aufgezwungen.

            If _dialogBatchResizeScalePercent <= 0 AndAlso width <= 0 AndAlso height <= 0 Then Return Nothing
            AppSettingsService.SaveLastBatchResizeSettings(width, height, _dialogBatchResizeScalePercent, _dialogBatchResizeLockAspect, _dialogBatchResizeInterpolation, _dialogBatchResizeNoUpscale,
                                                           _dialogBatchResizeLongEdge)
            If Not _dialogBatchResizeOverwrite Then PersistDialogTargetFolderIfLocal()
            PersistDialogTargetNamePattern()
            Return New BatchResizeResult With {
                .Width = width,
                .Height = height,
                .ScalePercent = _dialogBatchResizeScalePercent,
                .LockAspect = _dialogBatchResizeLockAspect,
                .NoUpscale = _dialogBatchResizeNoUpscale,
                .Interpolation = _dialogBatchResizeInterpolation,
                .Overwrite = _dialogBatchResizeOverwrite,
                .Format = DialogSelectedFormat,
                .JpgQuality = DialogJpgQuality,
                .Target = DialogSaveAsTarget,
                .TargetFolder = DialogSaveAsTargetFolder,
                .CopyRating = _dialogSaveAsCopyRating,
                .CopyFavorite = _dialogSaveAsCopyFavorite,
                .CopyColorLabel = _dialogSaveAsCopyColorLabel,
                .CopyKeywords = _dialogSaveAsCopyKeywords,
                .NamePattern = If(_dialogTargetNamePattern, "").Trim(),
                .PreserveMetadata = _dialogSaveAsPreserveExif
            }
        End Function

        Public Async Function ShowWatermarkPresetDialogAsync(Optional allowOverwrite As Boolean = True,
                                                             Optional currentFolder As String = "",
                                                             Optional sourcesIncludeJpg As Boolean = False) As Task(Of WatermarkPresetDialogResult)
            Dim settings = AppSettingsService.Load()
            SetDialogFormats(includeFpx:=False)
            _dialogWatermarkPresets.Clear()
            DialogWatermarkPresetNames.Clear()
            _dialogBatchOverwriteAvailable = allowOverwrite
            _dialogBatchWatermarkOverwrite = allowOverwrite AndAlso settings.BatchWatermarkOverwriteOriginals
            _dialogBatchSourcesIncludeJpg = sourcesIncludeJpg
            ResetDialogSaveAsMetaOptions()
            DialogSelectedFormat = NormalizeSaveAsFormat(DefaultSaveFormat())
            DialogJpgQuality = If(_dialogBatchWatermarkOverwrite, 95, DefaultJpgQuality())
            DialogSaveAsTarget = "Local"
            InitDialogTargetFolder(currentFolder)

            For Each preset In settings.WatermarkPresets
                _dialogWatermarkPresets.Add(preset)
                DialogWatermarkPresetNames.Add(preset.Name)
            Next

            If _dialogWatermarkPresets.Count = 0 Then
                Await ShowMessageAsync(LocalizationService.T("Wasserzeichen anwenden"), LocalizationService.T("Es ist kein gespeichertes Wasserzeichen vorhanden."))
                Return Nothing
            End If

            Dim lastName = AppSettingsService.NormalizePresetName(settings.LastWatermarkPresetName)
            Dim selectedPreset = _dialogWatermarkPresets.FirstOrDefault(Function(p) String.Equals(p.Name, lastName, StringComparison.OrdinalIgnoreCase))
            If selectedPreset Is Nothing Then selectedPreset = _dialogWatermarkPresets(0)
            DialogSelectedWatermarkPresetName = selectedPreset.Name
            LoadWatermarkPresetIntoDialogFields(selectedPreset)
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
            PersistDialogTargetNamePattern()
            Return New WatermarkPresetDialogResult With {
                .PreserveMetadata = _dialogSaveAsPreserveExif,
                .Preset = BuildWatermarkPresetForRun(selectedPreset),
                .Overwrite = _dialogBatchWatermarkOverwrite,
                .NamePattern = If(_dialogTargetNamePattern, "").Trim(),
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
                Await ShowMessageAsync(LocalizationService.T("Suche fehlgeschlagen"), LocalizationService.T("Bitte wähle einen gültigen Startordner."))
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
                Await ShowMessageAsync(LocalizationService.T("Stapel-Umbenennen fehlgeschlagen"), LocalizationService.T("Bitte behebe Namenskonflikte oder ungültige Namen in der Vorschau."))
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
                                             Optional initialJpgQuality As Integer = 0,
                                             Optional confirmText As String = "Speichern",
                                             Optional cancelText As String = "Abbrechen") As Task(Of SaveAsDialogResult) Implements IEditorHost.ShowSaveAsAsync
            SetDialogFormats(includeFpx:=True)
            DialogSelectedFormat = NormalizeSaveAsFormat(initialFormat)
            ' 0 = kein eigener Startwert, dann gilt die Einstellung.
            DialogJpgQuality = If(initialJpgQuality > 0, initialJpgQuality, DefaultJpgQuality())
            DialogSaveAsTarget = "Local"
            InitDialogTargetFolder("")
            ResetDialogSaveAsMetaOptions()
            Me.RaisePropertyChanged(NameOf(IsSaveAsImmichAvailable))

            Dim result = Await ShowDialogAsync(AppDialogKind.SaveAs, titleText, messageText, initialBaseName, confirmText, cancelText)
            If result Is Nothing Then Return Nothing

            PersistDialogTargetFolderIfLocal()
            PersistDialogTargetNamePattern()
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
        Public Async Function ShowBatchConvertAsync(fileCount As Integer, initialFormat As String, Optional initialJpgQuality As Integer = 0,
                                                    Optional currentFolder As String = "") As Task(Of SaveAsDialogResult)
            SetDialogFormats(includeFpx:=False)
            DialogSelectedFormat = NormalizeSaveAsFormat(initialFormat)
            ' 0 = kein eigener Startwert, dann gilt die Einstellung.
            DialogJpgQuality = If(initialJpgQuality > 0, initialJpgQuality, DefaultJpgQuality())
            DialogSaveAsTarget = "Local"
            DialogTargetNamePattern = AppSettingsService.Load().LastTargetNamePattern
            InitDialogTargetFolder(currentFolder)
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
            PersistDialogTargetNamePattern()
            Return New SaveAsDialogResult With {
                .Format = DialogSelectedFormat,
                .JpgQuality = DialogJpgQuality,
                .Target = DialogSaveAsTarget,
                .TargetFolder = DialogSaveAsTargetFolder,
                .CopyRating = _dialogSaveAsCopyRating,
                .CopyFavorite = _dialogSaveAsCopyFavorite,
                .CopyColorLabel = _dialogSaveAsCopyColorLabel,
                .CopyKeywords = _dialogSaveAsCopyKeywords,
                .NamePattern = If(_dialogTargetNamePattern, "").Trim(),
                .PreserveMetadata = _dialogSaveAsPreserveExif
            }
        End Function

        Private Function ResolveDefaultSaveAsTargetFolder() As String
            Dim settings = AppSettingsService.Load()
            If Directory.Exists(settings.LastSaveAsTargetFolder) Then Return settings.LastSaveAsTargetFolder
            If Directory.Exists(settings.LastGalleryFolder) Then Return settings.LastGalleryFolder
            If Directory.Exists(settings.GalleryStartupCustomFolder) Then Return settings.GalleryStartupCustomFolder
            Return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        End Function

        ''' Zieldateinamen-Muster merken. Bewusst NUR beim Bestaetigen eines Dialogs und nicht im
        ''' Setter: der feuert bei jedem Tastendruck, und jedes Speichern schreibt die
        ''' Einstellungsdatei.
        ''' <summary>Die gewaehlte Vorlage MIT den Dialogwerten (Anker, Breite/Hoehe) - bewusst als
        ''' KOPIE. Direkt in die gespeicherte Vorlage zu schreiben veraenderte sie als Nebenwirkung
        ''' eines einzelnen Stapellaufs.</summary>
        Private Function BuildWatermarkPresetForRun(vorlage As WatermarkPresetSettings) As WatermarkPresetSettings
            If vorlage Is Nothing Then Return Nothing
            Return New WatermarkPresetSettings With {
                .Id = vorlage.Id,
                .Name = vorlage.Name,
                .Text = vorlage.Text,
                .ImagePath = vorlage.ImagePath,
                .OffsetXPixels = vorlage.OffsetXPixels,
                .OffsetYPixels = vorlage.OffsetYPixels,
                .WidthPixels = _dialogWatermarkWidthPixels,
                .HeightPixels = _dialogWatermarkHeightPixels,
                .Anchor = _dialogWatermarkAnchor,
                .LockAspect = vorlage.LockAspect,
                .RotationDegrees = vorlage.RotationDegrees,
                .Opacity = vorlage.Opacity,
                .FontFamily = vorlage.FontFamily,
                .FontSizePixels = vorlage.FontSizePixels,
                .FillColor = vorlage.FillColor
            }
        End Function

        Private Sub PersistDialogTargetNamePattern()
            AppSettingsService.SaveLastTargetNamePattern(DialogTargetNamePattern)
        End Sub

        Private Sub PersistDialogTargetFolderIfLocal()
            If Not IsSaveAsTargetLocal Then Return
            Dim folder = AppSettingsService.NormalizeFolderPath(DialogSaveAsTargetFolder)
            If String.IsNullOrWhiteSpace(folder) Then Return
            AppSettingsService.SaveLastSaveAsTargetFolder(folder)
        End Sub

        Private Function ShowDialogAsync(kind As AppDialogKind, titleText As String, messageText As String, initialText As String, confirmText As String, cancelText As String, Optional secondaryText As String = "") As Task(Of String)
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
            DialogSecondaryText = LocalizationService.T(secondaryText)
            Me.RaisePropertyChanged(NameOf(IsDialogOpen))
            Me.RaisePropertyChanged(NameOf(IsAppContentHitTestVisible))
            Me.RaisePropertyChanged(NameOf(IsDialogCancelVisible))
            Me.RaisePropertyChanged(NameOf(IsDialogSecondaryVisible))
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
            Me.RaisePropertyChanged(NameOf(IsDialogSecondaryVisible))
            completion.TrySetResult(result)
        End Sub

        Public ReadOnly Property IsDialogCancelVisible As Boolean
            Get
                Return Not String.IsNullOrEmpty(_dialogCancelText)
            End Get
        End Property

        Public ReadOnly Property IsDialogSecondaryVisible As Boolean
            Get
                Return Not String.IsNullOrEmpty(_dialogSecondaryText)
            End Get
        End Property

        Private Sub RebuildBatchRenamePreview()
            If DialogBatchRenamePreview Is Nothing Then Return

            DialogBatchRenamePreview.Clear()
            If _dialogBatchRenamePaths Is Nothing OrElse _dialogBatchRenamePaths.Count = 0 Then Return

            Dim usedTargets As New HashSet(Of String)(PathIdentity.Comparer)
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
                    status = LocalizationService.T("Ungültiger Name")
                    hasProblem = True
                ElseIf Not usedTargets.Add(NormalizePath(targetPath)) Then
                    status = LocalizationService.T("Doppelter Zielname")
                    hasProblem = True
                ElseIf Not String.Equals(NormalizePath(sourcePath), NormalizePath(targetPath), PathIdentity.Comparison) AndAlso
                       (IO.File.Exists(targetPath) OrElse IO.Directory.Exists(targetPath)) Then
                    status = LocalizationService.T("Existiert bereits")
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
            Return ExpandTargetNamePattern(pattern, sourcePath, counter, appendSourceExtension:=True)
        End Function

        ''' <summary>Expandiert ein Dateinamen-Muster ({name}, {ext}, {camera}, {iso}, {aperture},
        ''' {focal}, {width}, {height}, {date:...}, {datetaken:...}, #/### als Zähler) für eine
        ''' Quelldatei. Öffentlich, weil neben dem Stapel-Umbenennen auch die Zieldateinamen der
        ''' Stapel-Funktionen (Konvertieren/Größe/Filter/Exportieren) dasselbe Muster sprechen -
        ''' zwei Muster-Dialekte wären eine Falle. EXIF kommt aus dem Dialog-Cache; fehlt der
        ''' Eintrag (Stapellauf ohne Umbenennen-Dialog), wird er nachgelesen.</summary>
        Public Function ExpandTargetNamePattern(pattern As String, sourcePath As String, counter As Integer,
                                                Optional appendSourceExtension As Boolean = False) As String
            Dim extension = IO.Path.GetExtension(sourcePath)
            Dim baseName = IO.Path.GetFileNameWithoutExtension(sourcePath)
            Dim modified = If(IO.File.Exists(sourcePath),
                              IO.File.GetLastWriteTime(sourcePath),
                              If(IO.Directory.Exists(sourcePath), IO.Directory.GetLastWriteTime(sourcePath), DateTime.Now))

            ' NUR fuer echte EXIF-Platzhalter lesen: "{name}_###" braucht keine Aufnahmedaten,
            ' bei RAW kostet ein ExifService.ReadExif je Datei sonst spuerbar Zeit.
            Static exifPlatzhalter As String() = {"{camera}", "{iso}", "{aperture}", "{focal}",
                                                  "{width}", "{height}", "{datetaken:"}
            Dim brauchtExif = exifPlatzhalter.Any(Function(ph) pattern.IndexOf(ph, StringComparison.OrdinalIgnoreCase) >= 0)
            Dim exif As ExifData = Nothing
            If brauchtExif AndAlso Not _dialogBatchRenameExifCache.TryGetValue(sourcePath, exif) AndAlso
               IO.File.Exists(sourcePath) Then
                Try
                    exif = ExifService.ReadExif(sourcePath)
                    _dialogBatchRenameExifCache(sourcePath) = exif
                Catch
                End Try
            End If
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

            If appendSourceExtension AndAlso
               String.IsNullOrEmpty(IO.Path.GetExtension(result)) AndAlso Not String.IsNullOrEmpty(extension) Then
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
                Case "PDF"
                    Return "PDF"
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
            ' PDF in BEIDEN Dialogen: einzeln als druckfertige Datei speichern und stapelweise
            ' konvertieren (dort entsteht wie bei allen Formaten eine Zieldatei je Bild).
            DialogFormatOptions.Add("PDF")
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

        ''' <param name="beforeDelete">Läuft direkt nach der Rückfrage und VOR dem eigentlichen Löschen -
        ''' die Ansicht blendet die Elemente damit sofort aus, statt auf den Papierkorb zu warten. Ein
        ''' fehlgeschlagenes Löschen holt sie über den Abgleich in <paramref name="afterDelete"/> zurück.</param>
        Public Async Sub RequestDeletePaths(paths As IEnumerable(Of String), Optional afterDelete As Action = Nothing,
                                            Optional beforeDelete As Action = Nothing) Implements IViewerHost.RequestDeletePaths, IEditorHost.RequestDeletePaths
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
                Dim verb = If(useTrash, LocalizationService.T("in den Papierkorb verschieben"),
                                        LocalizationService.T("endgültig löschen"))
                Dim message = If(pathList.Count = 1,
                                 $"{IO.Path.GetFileName(pathList(0))} {verb}?",
                                 String.Format(LocalizationService.T("{0} Elemente {1}?"), pathList.Count, verb))
                Dim confirmText = If(useTrash, "In den Papierkorb", "Löschen")
                If Not Await ShowConfirmAsync("Löschen", message, confirmText, "Abbrechen") Then Return
            End If

            Viewer.ReleaseCurrentImageIfAny(pathList)
            Editor.ReleaseCurrentImageIfAny(pathList)

            beforeDelete?.Invoke()

            ' Pro Element abfangen, damit ein einzelner Fehler (z.B. Papierkorb nicht verfügbar) den Rest
            ' nicht abbricht und die Ansicht trotzdem aktualisiert wird.
            ' Im Hintergrund: "gio trash" ist ein eigener Prozess je Datei - auf dem UI-Thread stand das
            ' Fenster so lange still, und die Ansicht konnte das Ausblenden gar nicht erst zeigen.
            Dim failures = Await Task.Run(Function()
                                              Dim errors As New List(Of String)()
                                              For Each itemPath In pathList
                                                  Try
                                                      DeletePath(itemPath, useTrash)
                                                  Catch ex As Exception
                                                      errors.Add($"{IO.Path.GetFileName(itemPath)}: {ex.Message}")
                                                  End Try
                                              Next
                                              Return errors
                                          End Function)
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
                RawSidecarService.AccompanyDelete(itemPath, useTrash:=True)
                Return
            End If

            If IO.File.Exists(itemPath) Then
                IO.File.Delete(itemPath)
                RawSidecarService.AccompanyDelete(itemPath, useTrash:=False)
            ElseIf IO.Directory.Exists(itemPath) Then
                IO.Directory.Delete(itemPath, True)
            End If
        End Sub

        ''' <summary>Laedt die Vorschaubilder EINER Datei in allen drei Ansichten neu: Galerie-Kacheln
        ''' und die Filmstreifen von Viewer und Editor.
        '''
        ''' Gebraucht, wenn sich das ANGEZEIGTE Bild aendert, ohne dass die Datei angefasst wird -
        ''' bislang genau ein Fall: die Drehung einer RAW/PSD landet im .fpxmp-Sidecar. Bewusst hier
        ''' und nicht je Ansicht: Viewer und Editor koennen beide einen Sidecar schreiben, und die
        ''' erste Fassung hatte in genau dieser Doppelung den Editor-Fall vergessen
        ''' ("auch bei Thumbnails wird nicht korrekt gedreht").</summary>
        Public Sub ReloadThumbnailsForFile(path As String) Implements IViewerHost.ReloadThumbnailsForFile, IEditorHost.ReloadThumbnailsForFile
            If String.IsNullOrWhiteSpace(path) Then Return
            Gallery?.RefreshThumbnailFor(path)
            ImageItem.ReloadThumbnailsFor(Viewer?.FilmstripItems, path)
            ImageItem.ReloadThumbnailsFor(Editor?.FilmstripItems, path)
        End Sub

        ''' Bei Dateien (nicht Ordnern) wird nur der Basisname ohne Endung im Eingabefeld angezeigt -
        ''' die Endung wird nach der Eingabe automatisch wieder angehängt, damit sie beim Umbenennen
        ''' nicht versehentlich mit überschrieben/entfernt werden kann.
        Public Async Sub RequestRenamePath(itemPath As String, Optional afterRename As Action(Of String) = Nothing) Implements IViewerHost.RequestRenamePath, IEditorHost.RequestRenamePath
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
                    RawSidecarService.AccompanyMove(itemPath, target)
                Else
                    IO.Directory.Move(itemPath, target)
                End If
                afterRename?.Invoke(target)
            Catch ex As Exception
                errorMessage = ex.Message
            End Try
            If errorMessage IsNot Nothing Then Await ShowMessageAsync(LocalizationService.T("Umbenennen fehlgeschlagen"), errorMessage)
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

#Region "Drucken"

        ' Der Druckdialog hängt - anders als der Collage-Dialog - am MainWindowViewModel, weil er
        ' aus allen drei Modi (Galerie, Betrachter, Editor) geöffnet wird. Aufbau ansonsten genau
        ' wie der Collage-Dialog: Overlay-View mit Live-Vorschau, entprellt und mit Request-ID
        ' gegen veraltete Ergebnisse abgesichert.

        Private _isPrintDialogOpen As Boolean
        Private _printPaths As New List(Of String)()
        Private _printTitle As String = ""
        Private _printPreviewImage As Avalonia.Media.Imaging.Bitmap
        Private _printPreviewRequestId As Integer
        Private _printPreviewTimer As DispatcherTimer
        ''' Temporär gerenderte Datei (Editor-Bearbeitungsstand), die nach dem Dialog wieder weg muss.
        Private _printTempFile As String

        Private _printPageSize As String = "A4"
        Private _printLandscape As Boolean
        Private _printMarginMm As Double = 10
        Private _printFitMode As String = "Fit"
        Private _printImagesPerPage As Integer = 1
        Private _printShowCaption As Boolean
        Private _printBorderless As Boolean
        Private _printCopies As Integer = 1

        ''' Logikschlüssel, NIE der übersetzte Anzeigetext - sonst bricht die Auswahl in jeder
        ''' anderen Sprache. Die View zeigt sie über PrintPageSizeDisplay/... übersetzt an.
        Public ReadOnly Property PrintPageSizeOptions As New ObservableCollection(Of String) From {"A4", "A3", "A5", "Letter", "Legal"}

        Public Property IsPrintDialogOpen As Boolean
            Get
                Return _isPrintDialogOpen
            End Get
            Set
                Me.RaiseAndSetIfChanged(_isPrintDialogOpen, value)
            End Set
        End Property

        Public Property PrintPreviewImage As Avalonia.Media.Imaging.Bitmap
            Get
                Return _printPreviewImage
            End Get
            Set
                Me.RaiseAndSetIfChanged(_printPreviewImage, value)
            End Set
        End Property

        Public Property PrintPageSize As String
            Get
                Return _printPageSize
            End Get
            Set
                Me.RaiseAndSetIfChanged(_printPageSize, If(value, "A4"))
                SchedulePrintPreviewUpdate()
            End Set
        End Property

        Public Property PrintLandscape As Boolean
            Get
                Return _printLandscape
            End Get
            Set
                Me.RaiseAndSetIfChanged(_printLandscape, value)
                Me.RaisePropertyChanged(NameOf(IsPrintPortrait))
                SchedulePrintPreviewUpdate()
            End Set
        End Property

        ''' Für die Segment-Schalter „Hoch"/„Quer" in der View.
        Public ReadOnly Property IsPrintPortrait As Boolean
            Get
                Return Not _printLandscape
            End Get
        End Property

        Public Property PrintMarginMm As Double
            Get
                Return _printMarginMm
            End Get
            Set
                Me.RaiseAndSetIfChanged(_printMarginMm, Math.Max(0, Math.Min(50, value)))
                SchedulePrintPreviewUpdate()
            End Set
        End Property

        Public Property PrintFitMode As String
            Get
                Return _printFitMode
            End Get
            Set
                Dim v = If(String.Equals(value, "Fill", StringComparison.OrdinalIgnoreCase), "Fill", "Fit")
                Me.RaiseAndSetIfChanged(_printFitMode, v)
                Me.RaisePropertyChanged(NameOf(IsPrintFitModeFit))
                Me.RaisePropertyChanged(NameOf(IsPrintFitModeFill))
                SchedulePrintPreviewUpdate()
            End Set
        End Property

        Public ReadOnly Property IsPrintFitModeFit As Boolean
            Get
                Return String.Equals(_printFitMode, "Fit", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public ReadOnly Property IsPrintFitModeFill As Boolean
            Get
                Return String.Equals(_printFitMode, "Fill", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public Property PrintImagesPerPage As Integer
            Get
                Return _printImagesPerPage
            End Get
            Set
                Me.RaiseAndSetIfChanged(_printImagesPerPage, value)
                RaisePrintPerPageChanged()
                SchedulePrintPreviewUpdate()
            End Set
        End Property

        ''' Die Bildunterschrift ergibt nur im Kontaktabzug Sinn - bei einem Bild pro Seite
        ''' verdeckt sie nur das Motiv.
        Public ReadOnly Property IsPrintContactSheet As Boolean
            Get
                Return _printImagesPerPage > 1
            End Get
        End Property

        ' Aktiv-Zustand der Rasterschalter. Einzelne Boolesche statt eines Converters - dasselbe
        ' Vorgehen wie IsCollageGridMode/IsCollageHeroMode im Collage-Dialog.
        Public ReadOnly Property IsPrintPerPage1 As Boolean
            Get
                Return _printImagesPerPage <= 1
            End Get
        End Property

        Public ReadOnly Property IsPrintPerPage4 As Boolean
            Get
                Return _printImagesPerPage = 4
            End Get
        End Property

        Public ReadOnly Property IsPrintPerPage9 As Boolean
            Get
                Return _printImagesPerPage = 9
            End Get
        End Property

        Public ReadOnly Property IsPrintPerPage16 As Boolean
            Get
                Return _printImagesPerPage = 16
            End Get
        End Property

        Public Property PrintShowCaption As Boolean
            Get
                Return _printShowCaption
            End Get
            Set
                Me.RaiseAndSetIfChanged(_printShowCaption, value)
                SchedulePrintPreviewUpdate()
            End Set
        End Property

        ''' <summary>Randlos drucken: Rand 0 und Seite füllen. Die zuletzt gewählten Werte für Rand
        ''' und Skalierung bleiben im ViewModel stehen (PrintOptions entscheidet erst beim Zeichnen) -
        ''' beim Abschalten steht wieder das da, was der Nutzer vorher eingestellt hatte.</summary>
        Public Property PrintBorderless As Boolean
            Get
                Return _printBorderless
            End Get
            Set
                Me.RaiseAndSetIfChanged(_printBorderless, value)
                Me.RaisePropertyChanged(NameOf(CanEditPrintPageFit))
                SchedulePrintPreviewUpdate()
            End Set
        End Property

        ''' <summary>Rand und Skalierung sind bei „randlos" wirkungslos - die Bedienelemente werden
        ''' deshalb ausgegraut statt still ignoriert zu werden.</summary>
        Public ReadOnly Property CanEditPrintPageFit As Boolean
            Get
                Return Not _printBorderless
            End Get
        End Property

        ''' <summary>Wie oft dasselbe Bild gedruckt wird. Zusammen mit „Bilder pro Seite" ergibt das
        ''' den Wunschfall „ein Bild 4x auf einer Seite". Nur bei Einzelauswahl sichtbar - bei mehreren
        ''' Bildern wäre unklar, ob sich die Zahl auf die Auswahl oder auf jedes Bild bezieht.</summary>
        Public Property PrintCopies As Integer
            Get
                Return _printCopies
            End Get
            Set
                Me.RaiseAndSetIfChanged(_printCopies, Math.Max(1, Math.Min(PrintService.MaxCopies, value)))
                SchedulePrintPreviewUpdate()
            End Set
        End Property

        Public ReadOnly Property PrintMaxCopies As Integer
            Get
                Return PrintService.MaxCopies
            End Get
        End Property

        ''' <summary>Nur bei genau einem ausgewählten Bild ergibt die Wiederholung Sinn.</summary>
        Public ReadOnly Property IsPrintSingleImage As Boolean
            Get
                Return _printPaths IsNot Nothing AndAlso _printPaths.Count = 1
            End Get
        End Property

        Public ReadOnly Property PrintDialogTitle As String
            Get
                Return If(String.IsNullOrWhiteSpace(_printTitle), LocalizationService.T("Drucken"), _printTitle)
            End Get
        End Property

        Public ReadOnly Property PrintSummaryText As String
            Get
                Dim options = BuildPrintOptions()
                Dim pages = PrintService.GetPageCount(_printPaths, options)
                ' Die Wiederholungen mitzählen, sonst behauptet die Zeile "1 Bild · 1 Seite",
                ' während vier Abzüge auf der Seite liegen.
                Dim prints = _printPaths.Count * Math.Max(1, options.Copies)
                Return $"{prints} {LocalizationService.T("Bilder")} · {pages} {LocalizationService.T("Seiten")}"
            End Get
        End Property

        Private Function BuildPrintOptions() As PrintOptions
            Return New PrintOptions With {
                .PageSize = _printPageSize,
                .Landscape = _printLandscape,
                .MarginMm = _printMarginMm,
                .FitMode = _printFitMode,
                .ImagesPerPage = _printImagesPerPage,
                .ShowCaption = _printShowCaption,
                .Borderless = _printBorderless,
                .Copies = If(IsPrintSingleImage, _printCopies, 1)
            }
        End Function

        ''' <summary>Öffnet den Druckdialog für die übergebenen Bilder. tempFile wird nach dem
        ''' Schließen gelöscht - der Editor rendert seinen Bearbeitungsstand dorthin.</summary>
        Public Sub ShowPrintDialog(imagePaths As IEnumerable(Of String),
                                   Optional title As String = Nothing,
                                   Optional tempFile As String = Nothing) Implements IViewerHost.ShowPrintDialog, IEditorHost.ShowPrintDialog
            Dim paths = If(imagePaths, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrWhiteSpace(p) AndAlso File.Exists(p)).
                ToList()
            If paths.Count = 0 Then
                ' Ohne diesen Zweig entstünde ein Dialog mit leerer Vorschau, dessen „Drucken"
                ' wortlos nichts tut.
                DeletePrintTempFile(tempFile)
                Dim unused = ShowMessageAsync(LocalizationService.T("Drucken"),
                                              LocalizationService.T("Es sind keine druckbaren Bilder ausgewählt."))
                Return
            End If

            _printPaths = paths
            _printTitle = title
            _printTempFile = tempFile

            ' Zuletzt bestätigte Optionen vorbelegen.
            Dim settings = AppSettingsService.Load()
            _printPageSize = If(settings.PrintPageSize, "A4")
            _printLandscape = settings.PrintLandscape
            _printMarginMm = settings.PrintMarginMm
            _printFitMode = If(String.Equals(settings.PrintFitMode, "Fill", StringComparison.OrdinalIgnoreCase), "Fill", "Fit")
            _printImagesPerPage = settings.PrintImagesPerPage
            _printShowCaption = settings.PrintShowCaption
            _printBorderless = settings.PrintBorderless
            ' Wiederholungen sind eine bewusste Einzelentscheidung, kein Dauerzustand - sonst
            ' druckt der naechste Auftrag ungefragt wieder alles vierfach.
            _printCopies = 1
            RaisePrintOptionChanged()

            IsPrintDialogOpen = True
            RefreshPrintPreviewAsync()
        End Sub

        Private Sub RaisePrintPerPageChanged()
            Me.RaisePropertyChanged(NameOf(IsPrintContactSheet))
            Me.RaisePropertyChanged(NameOf(IsPrintPerPage1))
            Me.RaisePropertyChanged(NameOf(IsPrintPerPage4))
            Me.RaisePropertyChanged(NameOf(IsPrintPerPage9))
            Me.RaisePropertyChanged(NameOf(IsPrintPerPage16))
        End Sub

        Private Sub RaisePrintOptionChanged()
            Me.RaisePropertyChanged(NameOf(PrintPageSize))
            Me.RaisePropertyChanged(NameOf(PrintLandscape))
            Me.RaisePropertyChanged(NameOf(IsPrintPortrait))
            Me.RaisePropertyChanged(NameOf(PrintMarginMm))
            Me.RaisePropertyChanged(NameOf(PrintFitMode))
            Me.RaisePropertyChanged(NameOf(IsPrintFitModeFit))
            Me.RaisePropertyChanged(NameOf(IsPrintFitModeFill))
            Me.RaisePropertyChanged(NameOf(PrintImagesPerPage))
            RaisePrintPerPageChanged()
            Me.RaisePropertyChanged(NameOf(PrintShowCaption))
            Me.RaisePropertyChanged(NameOf(PrintBorderless))
            Me.RaisePropertyChanged(NameOf(CanEditPrintPageFit))
            Me.RaisePropertyChanged(NameOf(PrintCopies))
            Me.RaisePropertyChanged(NameOf(IsPrintSingleImage))
            Me.RaisePropertyChanged(NameOf(PrintDialogTitle))
            Me.RaisePropertyChanged(NameOf(PrintSummaryText))
        End Sub

        Public Sub ClosePrintDialog()
            IsPrintDialogOpen = False
            _printPreviewTimer?.Stop()
            PrintPreviewImage = Nothing
            DeletePrintTempFile(_printTempFile)
            _printTempFile = Nothing
            _printPaths = New List(Of String)()
        End Sub

        ''' <summary>Die Temp-Datei des Editor-Bearbeitungsstands wegräumen. Fehler sind hier
        ''' bedeutungslos - im schlimmsten Fall bleibt eine Datei im Temp-Ordner liegen.</summary>
        Private Shared Sub DeletePrintTempFile(path As String)
            If String.IsNullOrWhiteSpace(path) Then Return
            Try
                If File.Exists(path) Then File.Delete(path)
            Catch
            End Try
        End Sub

        Private Sub SchedulePrintPreviewUpdate()
            Me.RaisePropertyChanged(NameOf(PrintSummaryText))
            If Not IsPrintDialogOpen Then Return
            If _printPreviewTimer Is Nothing Then
                _printPreviewTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(180)}
                AddHandler _printPreviewTimer.Tick, Sub()
                                                        _printPreviewTimer.Stop()
                                                        RefreshPrintPreviewAsync()
                                                    End Sub
            End If
            _printPreviewTimer.Stop()
            _printPreviewTimer.Start()
        End Sub

        ''' <summary>Rendert die Vorschau der ersten Seite im Hintergrund. Die Request-ID verwirft
        ''' Ergebnisse, die während des Renderns von einer neueren Einstellung überholt wurden -
        ''' sonst würde beim schnellen Reglerdrehen ein veraltetes Bild „gewinnen".</summary>
        Private Async Sub RefreshPrintPreviewAsync()
            Try
                Dim requestId = Interlocked.Increment(_printPreviewRequestId)
                If Not IsPrintDialogOpen Then Return

                Dim paths = _printPaths.ToList()
                Dim options = BuildPrintOptions()
                Dim preview = Await Task.Run(Function() PrintService.RenderPreview(paths, options, 900))
                If requestId <> _printPreviewRequestId OrElse Not IsPrintDialogOpen Then Return
                PrintPreviewImage = preview
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("MainWindowViewModel.RefreshPrintPreviewAsync", ex)
            End Try
        End Sub

        ''' <summary>Erzeugt das PDF und übergibt es dem System. Der Dialog schließt vorher, damit
        ''' der Systemviewer nicht hinter dem Overlay auftaucht.</summary>
        Public Async Sub ConfirmPrint()
            Try
                Dim paths = _printPaths.ToList()
                Dim options = BuildPrintOptions()
                Dim tempFile = _printTempFile

                AppSettingsService.SavePrintOptions(options)

                IsPrintDialogOpen = False
                _printPreviewTimer?.Stop()
                PrintPreviewImage = Nothing
                ' Die Temp-Datei erst NACH dem Rendern löschen - sie ist die Druckquelle.
                _printTempFile = Nothing

                Dim ok = Await PrintService.PrintAsync(paths, options)
                DeletePrintTempFile(tempFile)
                _printPaths = New List(Of String)()

                If Not ok Then
                    Await ShowMessageAsync(LocalizationService.T("Drucken fehlgeschlagen"),
                                           LocalizationService.T("Das PDF konnte nicht erzeugt oder geöffnet werden."))
                End If
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("MainWindowViewModel.ConfirmPrint", ex)
            End Try
        End Sub

#End Region

    End Class

End Namespace
