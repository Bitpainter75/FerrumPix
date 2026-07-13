Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks
Imports System.Timers
Imports System.Windows.Input
Imports ReactiveUI
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports FerrumPix.Models
Imports FerrumPix.Services
Imports LibVLCSharp.Shared

Namespace ViewModels

    Public Class ViewerViewModel
        Inherits ViewModelBase

        Private ReadOnly _mainVm As MainWindowViewModel
        Private _currentImagePath As String = ""
        ' Immich-Sitzung: _folderPaths enthält dann die Immich-Pseudo-Pfade (immich://{assetId}/{name})
        ' für Filmstreifen/Zähler, während _currentImagePath weiterhin der reale (heruntergeladene)
        ' Temp-Pfad des aktuell angezeigten Bildes ist - so bleibt der ganze Datei-/Anzeigecode gleich.
        Private _isImmichSession As Boolean = False
        Private _currentImmichAssetId As String = Nothing

        ''' <summary>True, solange der Viewer eine Immich-Album-Sitzung zeigt (Filmstreifen = Album).
        ''' Der Editor braucht das, um beim Zurückschalten die noch lebende Sitzung nicht durch eine
        ''' Ein-Bild-Sitzung des lokalen Temp-Pfads zu ersetzen.</summary>
        Public ReadOnly Property IsImmichSession As Boolean
            Get
                Return _isImmichSession
            End Get
        End Property
        Private _immichSourceAlbumId As String = Nothing
        Private _immichNavToken As Integer = 0
        ' Metadaten (Favorit/Rating/Stichwörter) je Album-Position - aus den Galerie-Items durchgereicht,
        ' da die reinen Pseudo-Pfade sie nicht tragen.
        Private _immichSessionItems As New List(Of ImageItem)()
        Private _currentImage As Bitmap
        Private _zoomLevel As Double = 1.0
        Private _zoomText As String = "100%"
        Private _currentIndex As Integer = -1
        Private _statusInfo As String = ""
        Private _mousePositionText As String = ""
        Private _imageWidth As Integer
        Private _imageHeight As Integer
        Private _currentFileName As String = ""
        Private _selectedInfoTab As InfoSidebarTab = InfoSidebarTab.General
        Private _exifInfo As ExifData
        Private _histogramImage As Bitmap
        Private _newTagText As String = ""
        Private _rotationAngle As Double = 0
        Private _scaleX As Double = 1.0
        Private _rating As Integer = 0
        Private _isFavorite As Boolean = False
        Private _isSlideshowPlaying As Boolean = False
        Private _slideshowTimer As Timer
        Private _slideshowIntervalMs As Double = 3000
        Private _folderPaths As New List(Of String)()
        ' Cache-Scope für die Filmstreifen-Thumbnails (bei Suchlisten die Suchlisten-Scope, sonst Nothing),
        ' damit nicht je Ursprungsordner der Treffer ein eigener Cache-Ordner entsteht.
        Private _thumbCacheScopeId As String = Nothing
        Private _thumbCacheScopeName As String = Nothing
        Private ReadOnly _navDebouncer As FilmstripNavigationDebouncer
        Private _isFitToWindow As Boolean = True
        Private _activeZoomPreset As ZoomPresetMode = ZoomPresetMode.Fit
        Private _imageViewportWidth As Double
        Private _imageViewportHeight As Double

        Private _libVlc As LibVLC
        Private _mediaPlayer As MediaPlayer
        Private _isVideoPlaying As Boolean = False
        Private _videoPositionSeconds As Double = 0
        Private _videoDurationSeconds As Double = 0
        Private _isVideoMuted As Boolean = False
        Private _isSeekingVideo As Boolean = False

        Public Property FilmstripItems As BulkObservableCollection(Of ImageItem)
        Public Property Tags As ObservableCollection(Of String)
        Public Property TagSuggestions As ObservableCollection(Of String)

        Public ReadOnly Property IsInfoSidebarVisible As Boolean
            Get
                Return _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing AndAlso _mainVm.Settings.ViewerInfoSidebarExpanded
            End Get
        End Property

        Public ReadOnly Property IsFullscreenMode As Boolean
            Get
                Return _mainVm IsNot Nothing AndAlso _mainVm.IsFullscreen
            End Get
        End Property

        Public Property CurrentImagePath As String
            Get
                Return _currentImagePath
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_currentImagePath, value)
                Me.RaisePropertyChanged(NameOf(TransparencyBackgroundBrush))
            End Set
        End Property

        ''' Hintergrund hinter transparenten Bildbereichen (Schachbrettmuster oder Volltonfarbe je
        ''' nach Einstellung) - wird bei Änderung in den Settings über
        ''' MainWindowViewModel.RefreshDisplayBindings aktualisiert.
        Public ReadOnly Property TransparencyBackgroundBrush As Avalonia.Media.IBrush
            Get
                ' Formate ohne Alphakanal-Unterstützung (z.B. JPEG) können strukturell nie
                ' transparente Bereiche haben - Schachbrett/Volltonfarbe wäre dort nur an
                ' Letterbox-/Rundungsrändern fälschlich sichtbar, nie inhaltlich sinnvoll.
                If Not TransparencyBrushService.HasVisibleTransparency(_currentImagePath) Then
                    Return Avalonia.Media.Brushes.Transparent
                End If
                ' Im Vollbildmodus soll die tatsächliche Transparenz durchscheinen statt des
                ' Schachbrett-/Volltonfarbe-Hintergrunds, der im Fenstermodus als Bearbeitungshilfe
                ' dient - im Vollbild geht es um ungestörtes Betrachten, nicht um Transparenz-Analyse.
                If IsFullscreenMode Then Return Avalonia.Media.Brushes.Transparent
                Dim settings = AppSettingsService.Load()
                Return TransparencyBrushService.GetBrush(settings.TransparencyBackgroundMode, settings.TransparencyBackgroundColor)
            End Get
        End Property

        ''' Löst das alte Bitmap erst einen Dispatcher-Tick später auf (statt im selben Aufruf) -
        ''' MainImage/FullscreenImage (siehe ViewerView.axaml) könnten die alte Quelle sonst noch
        ''' kurz zum Kompositieren/Rendern brauchen, obwohl die Bindung bereits auf das neue Bild
        ''' umgestellt wurde.
        Private Shared Sub DisposeDeferred(bitmap As Bitmap)
            If bitmap Is Nothing Then Return
            Dispatcher.UIThread.Post(Sub() bitmap.Dispose(), DispatcherPriority.Background)
        End Sub

        Public Property CurrentImage As Bitmap
            Get
                Return _currentImage
            End Get
            Set(value As Bitmap)
                Dim previous = _currentImage
                Me.RaiseAndSetIfChanged(_currentImage, value)
                Me.RaisePropertyChanged(NameOf(HasNoMedia))
                If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, value) Then DisposeDeferred(previous)
            End Set
        End Property

        ''' True nur, wenn weder ein Bild noch ein Video geladen ist - steuert den
        ''' "Kein Bild geöffnet"-Leerzustand im Viewer (der bei Videos nicht erscheinen darf,
        ''' obwohl CurrentImage dort bewusst Nothing bleibt).
        Public ReadOnly Property HasNoMedia As Boolean
            Get
                Return _currentImage Is Nothing AndAlso Not IsVideoFile
            End Get
        End Property

        Public Property ZoomLevel As Double
            Get
                Return _zoomLevel
            End Get
            Set(value As Double)
                Dim clamped = Math.Max(0.05, Math.Min(20.0, value))
                Me.RaiseAndSetIfChanged(_zoomLevel, clamped)
                ZoomText = $"{CInt(clamped * 100)}%"
                Me.RaisePropertyChanged(NameOf(ZoomSliderValue))
            End Set
        End Property

        Private Const ZoomSliderMinPercent As Double = 5.0
        Private Const ZoomSliderMaxPercent As Double = 2000.0

        ''' <summary>Rundregler-Wert (0-100, log-skaliert) für den Zoom-Regler in der Topbar -
        ''' bildet denselben 0-100-Bereich wie der Editor-Zoom-Regler auf den (breiteren) Viewer-
        ''' Zoombereich von 5%-2000% ab (ZoomLevel-Setter clamped bereits auf 0.05-20.0).</summary>
        Public Property ZoomSliderValue As Double
            Get
                Dim pct = Math.Max(ZoomSliderMinPercent, Math.Min(ZoomSliderMaxPercent, ZoomLevel * 100.0))
                Return Math.Max(0, Math.Min(100, Math.Log(pct / ZoomSliderMinPercent) / Math.Log(ZoomSliderMaxPercent / ZoomSliderMinPercent) * 100.0))
            End Get
            Set(value As Double)
                Dim clampedSlider = Math.Max(0, Math.Min(100, value))
                Dim pct = ZoomSliderMinPercent * Math.Pow(ZoomSliderMaxPercent / ZoomSliderMinPercent, clampedSlider / 100.0)
                ActiveZoomPreset = ZoomPresetMode.Manual
                IsFitToWindow = False
                ZoomLevel = pct / 100.0
            End Set
        End Property

        Public Property IsFitToWindow As Boolean
            Get
                Return _isFitToWindow
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isFitToWindow, value)
                ZoomText = $"{CInt(_zoomLevel * 100)}%"
            End Set
        End Property

        ''' <summary>Zuletzt bewusst gewählter Zoom-Modus (Fit/Actual/Manual) - bleibt über einen
        ''' Bildwechsel hinweg erhalten (siehe LoadPathAt), nur eine manuelle Zoomänderung setzt ihn
        ''' auf Manual zurück. Dient außerdem den Classes.active-Bindings der Fit/100%-Buttons.</summary>
        Public Property ActiveZoomPreset As ZoomPresetMode
            Get
                Return _activeZoomPreset
            End Get
            Set(value As ZoomPresetMode)
                Me.RaiseAndSetIfChanged(_activeZoomPreset, value)
                Me.RaisePropertyChanged(NameOf(IsZoomFitActive))
                Me.RaisePropertyChanged(NameOf(IsZoomActualActive))
            End Set
        End Property

        Public ReadOnly Property IsZoomFitActive As Boolean
            Get
                Return _activeZoomPreset = ZoomPresetMode.Fit
            End Get
        End Property

        Public ReadOnly Property IsZoomActualActive As Boolean
            Get
                Return _activeZoomPreset = ZoomPresetMode.Actual
            End Get
        End Property

        Public Property ZoomText As String
            Get
                Return _zoomText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_zoomText, value)
            End Set
        End Property

        Public Property CurrentIndex As Integer
            Get
                Return _currentIndex
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_currentIndex, value)
                Me.RaisePropertyChanged(NameOf(PositionText))
                Me.RaisePropertyChanged(NameOf(CurrentFilmstripIndex))
                MarkCurrentFilmstripItem()
            End Set
        End Property

        Public Property StatusInfo As String
            Get
                Return _statusInfo
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_statusInfo, value)
            End Set
        End Property

        ''' <summary>Bildpixel-Koordinate der Maus über dem Bild, für die Fußleiste - leer, wenn die
        ''' Maus das Bild nicht berührt.</summary>
        Public Property MousePositionText As String
            Get
                Return _mousePositionText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_mousePositionText, value)
            End Set
        End Property

        Public Property ImageWidth As Integer
            Get
                Return _imageWidth
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_imageWidth, value)
            End Set
        End Property

        Public Property ImageHeight As Integer
            Get
                Return _imageHeight
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_imageHeight, value)
            End Set
        End Property

        Public Property CurrentFileName As String
            Get
                Return _currentFileName
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_currentFileName, value)
            End Set
        End Property

        Public Property HistogramImage As Bitmap
            Get
                Return _histogramImage
            End Get
            Set(value As Bitmap)
                Dim previous = _histogramImage
                Me.RaiseAndSetIfChanged(_histogramImage, value)
                If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, value) Then DisposeDeferred(previous)
            End Set
        End Property

        Public Property ExifInfo As ExifData
            Get
                Return _exifInfo
            End Get
            Set(value As ExifData)
                Me.RaiseAndSetIfChanged(_exifInfo, value)
            End Set
        End Property

        Public Property NewTagText As String
            Get
                Return _newTagText
            End Get
            Set(value As String)
                Me.RaiseAndSetIfChanged(_newTagText, value)
            End Set
        End Property

        Public Property SelectedInfoTab As InfoSidebarTab
            Get
                Return _selectedInfoTab
            End Get
            Set(value As InfoSidebarTab)
                If _selectedInfoTab = value Then Return
                Me.RaiseAndSetIfChanged(_selectedInfoTab, value)
                RaiseInfoTabStateChanged()
            End Set
        End Property

        Public ReadOnly Property IsInfoTabGeneral As Boolean
            Get
                Return _selectedInfoTab = InfoSidebarTab.General
            End Get
        End Property

        Public ReadOnly Property IsInfoTabExif As Boolean
            Get
                Return _selectedInfoTab = InfoSidebarTab.Exif
            End Get
        End Property

        Public ReadOnly Property IsInfoTabIptc As Boolean
            Get
                Return _selectedInfoTab = InfoSidebarTab.Iptc
            End Get
        End Property

        Public ReadOnly Property IsInfoTabXmp As Boolean
            Get
                Return _selectedInfoTab = InfoSidebarTab.Xmp
            End Get
        End Property

        Public ReadOnly Property IsInfoTabIcc As Boolean
            Get
                Return _selectedInfoTab = InfoSidebarTab.Icc
            End Get
        End Property

        Public Property RotationAngle As Double
            Get
                Return _rotationAngle
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_rotationAngle, value)
                If _isFitToWindow Then
                    UpdateFitZoom()
                End If
            End Set
        End Property

        Public Property ScaleX As Double
            Get
                Return _scaleX
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_scaleX, value)
            End Set
        End Property

        Public Property Rating As Integer
            Get
                Return _rating
            End Get
            Set(value As Integer)
                Me.RaiseAndSetIfChanged(_rating, value)
                Me.RaisePropertyChanged(NameOf(RatingText))
                If _isImmichSession AndAlso Not String.IsNullOrEmpty(_currentImmichAssetId) Then
                    Dim ignored = ImmichService.SetRatingAsync(_currentImmichAssetId, value)
                ElseIf Not String.IsNullOrEmpty(_currentImagePath) Then
                    LibraryService.Instance.SetRating(_currentImagePath, value, syncToXmp:=True)
                End If
            End Set
        End Property

        Public ReadOnly Property RatingText As String
            Get
                Return New String("★"c, Math.Max(0, Math.Min(5, _rating))) &
                       New String("☆"c, 5 - Math.Max(0, Math.Min(5, _rating)))
            End Get
        End Property

        Public Property IsFavorite As Boolean
            Get
                Return _isFavorite
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isFavorite, value)
                If _isImmichSession AndAlso Not String.IsNullOrEmpty(_currentImmichAssetId) Then
                    Dim ignored = ImmichService.SetFavoriteAsync(_currentImmichAssetId, value)
                ElseIf Not String.IsNullOrEmpty(_currentImagePath) Then
                    LibraryService.Instance.SetFavorite(_currentImagePath, value)
                End If
            End Set
        End Property

        Public Property IsSlideshowPlaying As Boolean
            Get
                Return _isSlideshowPlaying
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isSlideshowPlaying, value)
                Me.RaisePropertyChanged(NameOf(SlideshowButtonText))
            End Set
        End Property

        Public ReadOnly Property SlideshowButtonText As String
            Get
                Return If(_isSlideshowPlaying, "Stop", LocalizationService.T("Diashow"))
            End Get
        End Property

        Public ReadOnly Property IsRawFile As Boolean
            Get
                Return Not String.IsNullOrEmpty(_currentImagePath) AndAlso
                       RawPreviewService.IsSupportedRaw(_currentImagePath)
            End Get
        End Property

        Public ReadOnly Property IsVideoFile As Boolean
            Get
                Return Not String.IsNullOrEmpty(_currentImagePath) AndAlso
                       VideoPreviewService.IsSupportedVideo(_currentImagePath)
            End Get
        End Property

        ''' Ob LibVLC beim App-Start erfolgreich initialisiert wurde. Unter Linux ist libvlc eine
        ''' vom Nutzer separat zu installierende Systemabhängigkeit - fehlt sie, zeigt der Viewer
        ''' statt eines leeren/kaputten Wiedergabebereichs einen erklärenden Hinweis
        ''' (siehe "VLC nicht installiert"-Zustand in ViewerView.axaml).
        Public ReadOnly Property IsVideoPlaybackAvailable As Boolean
            Get
                Return App.IsVideoPlaybackAvailable
            End Get
        End Property

        Public ReadOnly Property ShowVideoUnavailableNotice As Boolean
            Get
                Return IsVideoFile AndAlso Not IsVideoPlaybackAvailable
            End Get
        End Property

        Private _isVideoEnded As Boolean = False

        ''' Nach dem Videoende wird die native Ausgabefläche ausgeblendet. Sie behält sonst den zuletzt
        ''' gezeichneten Frame - ein X11-Fenster wird nicht von selbst geleert -, und da keine neuen
        ''' Frames mehr kommen, steht dieser Frame nach einem Vollbild-Wechsel in der alten Skalierung
        ''' im Bild. Ausgeblendet zerstört Avalonia das Kindfenster, und der schwarze Hintergrund des
        ''' VideoOverlay-Grids bleibt in der richtigen Größe zurück.
        Public ReadOnly Property ShowVideoSurface As Boolean
            Get
                Return IsVideoPlaybackAvailable AndAlso Not _isVideoEnded
            End Get
        End Property

        Public ReadOnly Property CanEdit As Boolean
            Get
                Return Not IsVideoFile AndAlso
                       Not SvgPreviewService.IsSupportedSvg(_currentImagePath)
            End Get
        End Property

        Public Property IsVideoPlaying As Boolean
            Get
                Return _isVideoPlaying
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isVideoPlaying, value)
            End Set
        End Property

        Public Property VideoPositionSeconds As Double
            Get
                Return _videoPositionSeconds
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_videoPositionSeconds, value)
                Me.RaisePropertyChanged(NameOf(VideoTimeText))
            End Set
        End Property

        Public Property VideoDurationSeconds As Double
            Get
                Return _videoDurationSeconds
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_videoDurationSeconds, value)
                Me.RaisePropertyChanged(NameOf(VideoTimeText))
            End Set
        End Property

        Public Property IsVideoMuted As Boolean
            Get
                Return _isVideoMuted
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isVideoMuted, value)
                If _mediaPlayer IsNot Nothing Then _mediaPlayer.Mute = value
            End Set
        End Property

        Public ReadOnly Property VideoTimeText As String
            Get
                Return $"{FormatSeconds(_videoPositionSeconds)} / {FormatSeconds(_videoDurationSeconds)}"
            End Get
        End Property

        Private Shared Function FormatSeconds(totalSeconds As Double) As String
            If totalSeconds < 0 OrElse Double.IsNaN(totalSeconds) OrElse Double.IsInfinity(totalSeconds) Then totalSeconds = 0
            Dim t = TimeSpan.FromSeconds(totalSeconds)
            If t.TotalHours >= 1 Then Return t.ToString("h\:mm\:ss")
            Return t.ToString("m\:ss")
        End Function

        Public ReadOnly Property PositionText As String
            Get
                If _folderPaths.Count = 0 Then Return ""
                Return $"{_currentIndex + 1} / {_folderPaths.Count}"
            End Get
        End Property

        Public ReadOnly Property CurrentFilmstripIndex As Integer
            Get
                Return _currentIndex
            End Get
        End Property

        Public ReadOnly Property ShowFilmstrip As Boolean
            Get
                Return _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing AndAlso _mainVm.Settings.ViewerShowFilmstrip
            End Get
        End Property

        ' Commands
        Public ReadOnly Property PreviousCommand As ICommand
        Public ReadOnly Property NextCommand As ICommand
        Public ReadOnly Property ZoomInCommand As ICommand
        Public ReadOnly Property ZoomOutCommand As ICommand
        Public ReadOnly Property ZoomFitCommand As ICommand
        Public ReadOnly Property ZoomActualCommand As ICommand
        Public ReadOnly Property EditCommand As ICommand
        Public ReadOnly Property ToggleInfoSidebarCommand As ICommand
        Public ReadOnly Property SetInfoTabCommand As ICommand
        Public ReadOnly Property AddTagCommand As ICommand
        Public ReadOnly Property RemoveTagCommand As ICommand
        Public ReadOnly Property RotateLeftCommand As ICommand
        Public ReadOnly Property RotateRightCommand As ICommand
        Public ReadOnly Property FlipHorizontalCommand As ICommand
        Public ReadOnly Property BackToGalleryCommand As ICommand
        Public ReadOnly Property DeleteCurrentCommand As ICommand
        Public ReadOnly Property RenameCurrentCommand As ICommand
        Public ReadOnly Property CopyPathCommand As ICommand
        Public ReadOnly Property OpenFileManagerCommand As ICommand
        Public ReadOnly Property SetRatingCommand As ICommand
        Public ReadOnly Property ToggleFavoriteCommand As ICommand
        Public ReadOnly Property ToggleSlideshowCommand As ICommand
        Public ReadOnly Property PlayPauseVideoCommand As ICommand
        Public ReadOnly Property SeekVideoCommand As ICommand
        Public ReadOnly Property ToggleVideoMuteCommand As ICommand

        Public Sub New(mainVm As MainWindowViewModel)
            _mainVm = mainVm
            FilmstripItems = New BulkObservableCollection(Of ImageItem)()
            Tags = New ObservableCollection(Of String)()
            TagSuggestions = New ObservableCollection(Of String)(LibraryService.Instance.GetAllTags())

            _navDebouncer = New FilmstripNavigationDebouncer(wrapAround:=True,
                                                               getCurrentIndex:=Function() _currentIndex,
                                                               getCount:=Function() _folderPaths.Count,
                                                               commit:=AddressOf CommitNavigateAsync)

            PreviousCommand = ReactiveCommand.Create(Sub() NavigatePrevious())
            NextCommand = ReactiveCommand.Create(Sub() NavigateNext())
            ZoomInCommand = ReactiveCommand.Create(Sub() ZoomIn())
            ZoomOutCommand = ReactiveCommand.Create(Sub() ZoomOut())
            ZoomFitCommand = ReactiveCommand.Create(Sub()
                                                        ActiveZoomPreset = ZoomPresetMode.Fit
                                                        IsFitToWindow = True
                                                        UpdateFitZoom()
                                                    End Sub)
            ZoomActualCommand = ReactiveCommand.Create(Sub()
                                                           ActiveZoomPreset = ZoomPresetMode.Actual
                                                           IsFitToWindow = False
                                                           ZoomLevel = 1.0
                                                       End Sub)
            EditCommand = ReactiveCommand.CreateFromTask(Async Function()
                                                             If Not String.IsNullOrEmpty(_currentImagePath) Then
                                                                 Await _mainVm.OpenImageInEditor(_currentImagePath, EditorFilmstripPaths(), _thumbCacheScopeId, _thumbCacheScopeName, forceSaveAsOnly:=_isImmichSession, immichAlbumId:=_immichSourceAlbumId)
                                                             End If
                                                         End Function)
            ToggleInfoSidebarCommand = ReactiveCommand.Create(Sub()
                                                                   If _mainVm Is Nothing OrElse _mainVm.Settings Is Nothing Then Return
                                                                   _mainVm.Settings.ViewerInfoSidebarExpanded = Not _mainVm.Settings.ViewerInfoSidebarExpanded
                                                                   Me.RaisePropertyChanged(NameOf(IsInfoSidebarVisible))
                                                                   If IsInfoSidebarVisible Then EnsureHistogramLoaded()
                                                               End Sub)
            SetInfoTabCommand = ReactiveCommand.Create(Of String)(Sub(tabName) SetInfoTab(tabName))
            AddTagCommand = ReactiveCommand.Create(Sub()
                                                       Dim tag = NewTagText.Trim().ToLowerInvariant()
                                                       If String.IsNullOrEmpty(tag) OrElse Tags.Contains(tag) Then Return
                                                       Tags.Add(tag)
                                                       NewTagText = ""
                                                       If _isImmichSession AndAlso Not String.IsNullOrEmpty(_currentImmichAssetId) Then
                                                           Dim ignored = ImmichService.AddTagToAssetAsync(_currentImmichAssetId, tag)
                                                       ElseIf Not String.IsNullOrEmpty(_currentImagePath) Then
                                                           LibraryService.Instance.SetTags(_currentImagePath, Tags)
                                                       End If
                                                       RefreshTagSuggestions()
                                                   End Sub)
            RemoveTagCommand = ReactiveCommand.Create(Of String)(Sub(tag)
                                                                     If Not Tags.Remove(tag) Then Return
                                                                     If _isImmichSession AndAlso Not String.IsNullOrEmpty(_currentImmichAssetId) Then
                                                                         Dim ignored = ImmichService.RemoveTagFromAssetAsync(_currentImmichAssetId, tag)
                                                                     ElseIf Not String.IsNullOrEmpty(_currentImagePath) Then
                                                                         LibraryService.Instance.SetTags(_currentImagePath, Tags)
                                                                     End If
                                                                 End Sub)
            RotateLeftCommand = ReactiveCommand.Create(Sub() RotationAngle = ((RotationAngle - 90) Mod 360 + 360) Mod 360)
            RotateRightCommand = ReactiveCommand.Create(Sub() RotationAngle = (RotationAngle + 90) Mod 360)
            FlipHorizontalCommand = ReactiveCommand.Create(Sub() ScaleX = ScaleX * -1)
            BackToGalleryCommand = ReactiveCommand.Create(Sub() _mainVm.BackToGallery(_currentImagePath))
            DeleteCurrentCommand = ReactiveCommand.Create(Sub() DeleteCurrent())
            RenameCurrentCommand = ReactiveCommand.Create(Sub() RenameCurrent())
            CopyPathCommand = ReactiveCommand.Create(Sub() CopyToClipboard())
            OpenFileManagerCommand = ReactiveCommand.Create(Sub() OpenInFileManager())
            SetRatingCommand = ReactiveCommand.Create(Of String)(Sub(r)
                                                                     Dim v As Integer
                                                                     If Integer.TryParse(r, v) Then Rating = If(_rating = v, 0, v)
                                                                 End Sub)
            ToggleFavoriteCommand = ReactiveCommand.Create(Sub() IsFavorite = Not IsFavorite)
            ToggleSlideshowCommand = ReactiveCommand.Create(Sub()
                                                                If _isSlideshowPlaying Then
                                                                    StopSlideshow()
                                                                Else
                                                                    StartSlideshow()
                                                                End If
                                                            End Sub)
            PlayPauseVideoCommand = ReactiveCommand.Create(Sub() ToggleVideoPlayPause())
            SeekVideoCommand = ReactiveCommand.Create(Of Double)(Sub(seconds) SeekVideo(seconds))
            ToggleVideoMuteCommand = ReactiveCommand.Create(Sub() IsVideoMuted = Not IsVideoMuted)
        End Sub

        ''' <summary>Öffnet eine Immich-Sitzung: der Filmstreifen zeigt das ganze Album (Pseudo-Pfade),
        ''' das jeweils angezeigte Original wird on-demand heruntergeladen. Reibt sich nicht mit dem
        ''' lokalen Pfad-Fluss (alles Immich-spezifische ist über _isImmichSession gekapselt).</summary>
        Public Sub OpenImmichSession(startPseudoPath As String, sessionItems As List(Of ImageItem), Optional immichAlbumId As String = Nothing)
            If sessionItems Is Nothing OrElse sessionItems.Count = 0 Then Return
            _isImmichSession = True
            _immichSourceAlbumId = immichAlbumId
            _thumbCacheScopeId = Nothing
            _thumbCacheScopeName = Nothing
            StopVideoPlayback()
            _immichSessionItems = sessionItems.Where(Function(i) i IsNot Nothing AndAlso i.IsImmichAsset).ToList()
            _folderPaths = _immichSessionItems.Select(Function(i) i.FilePath).ToList()
            _currentIndex = _folderPaths.FindIndex(Function(p) String.Equals(p, startPseudoPath, StringComparison.OrdinalIgnoreCase))
            If _currentIndex < 0 Then _currentIndex = 0
            LoadFilmstrip()
            LoadImmichAt(_currentIndex)
        End Sub

        ''' <summary>Lädt das Immich-Bild an Position idx: Original in Temp holen, dann anzeigen. Der
        ''' Navigations-Token verwirft ein spät eintreffendes Download-Ergebnis, falls der Nutzer
        ''' inzwischen weitergeblättert hat.</summary>
        Private Async Sub LoadImmichAt(idx As Integer)
            If idx < 0 OrElse idx >= _folderPaths.Count Then Return
            Dim pseudo = _folderPaths(idx)
            Dim assetId As String = Nothing, fileName As String = Nothing
            If Not ImmichService.TryParsePseudoPath(pseudo, assetId, fileName) Then Return

            Dim token = System.Threading.Interlocked.Increment(_immichNavToken)
            _currentIndex = idx
            CurrentIndex = idx
            MarkCurrentFilmstripItem()
            CurrentFileName = fileName
            _currentImmichAssetId = assetId
            StatusInfo = LocalizationService.T("Lade…")

            ' Favorit/Rating/Stichwörter aus dem durchgereichten Galerie-Item übernehmen - Felder direkt
            ' setzen, damit die Property-Setter nicht sofort wieder an den Server zurückschreiben.
            If idx < _immichSessionItems.Count Then
                Dim meta = _immichSessionItems(idx)
                _isFavorite = meta.IsFavorite
                Me.RaisePropertyChanged(NameOf(IsFavorite))
                _rating = meta.Rating
                Me.RaisePropertyChanged(NameOf(Rating))
                Me.RaisePropertyChanged(NameOf(RatingText))
                Tags.Clear()
                If meta.Tags IsNot Nothing Then
                    For Each t In meta.Tags
                        Tags.Add(t)
                    Next
                End If
            End If

            Dim localPath = Await ImmichService.DownloadOriginalToTempAsync(assetId, fileName)
            ' Zwischenzeitlich weitergeblättert oder Sitzung verlassen? Dann Ergebnis verwerfen.
            If token <> System.Threading.Volatile.Read(_immichNavToken) OrElse Not _isImmichSession Then Return
            If String.IsNullOrEmpty(localPath) Then
                StatusInfo = LocalizationService.T("Bild konnte nicht aus Immich geladen werden")
                Return
            End If

            _currentImagePath = localPath
            CurrentImagePath = localPath
            RotationAngle = 0
            ScaleX = 1.0
            Select Case _activeZoomPreset
                Case ZoomPresetMode.Fit : IsFitToWindow = True
                Case ZoomPresetMode.Actual
                    IsFitToWindow = False
                    ZoomLevel = 1.0
                Case Else : IsFitToWindow = False
            End Select
            LoadBitmap()
            If _isFitToWindow Then UpdateFitZoom()
            UpdateStatus()
            ' Die heruntergeladene Temp-Kopie ist das Original - EXIF/IPTC/XMP direkt daraus lesen.
            LoadInfoPanelData(_currentImagePath, preserveExistingTags:=True)
            Me.RaisePropertyChanged(NameOf(IsRawFile))
            Me.RaisePropertyChanged(NameOf(IsVideoFile))
            Me.RaisePropertyChanged(NameOf(ShowVideoUnavailableNotice))
            Me.RaisePropertyChanged(NameOf(HasNoMedia))
            Me.RaisePropertyChanged(NameOf(CanEdit))
        End Sub

        Public Sub OpenImage(imagePath As String, Optional allPaths As List(Of String) = Nothing, Optional cacheScopeId As String = Nothing, Optional cacheScopeName As String = Nothing)
            _isImmichSession = False
            _immichSourceAlbumId = Nothing
            If Not File.Exists(imagePath) Then Return

            ' Scope nur wirksam, wenn eine explizite Pfadliste (z.B. Suchliste) übergeben wurde; beim
            ' Öffnen aus einem echten Ordner (allPaths=Nothing) gilt der normale ordnerbasierte Cache.
            _thumbCacheScopeId = If(allPaths IsNot Nothing, cacheScopeId, Nothing)
            _thumbCacheScopeName = If(allPaths IsNot Nothing, cacheScopeName, Nothing)
            _currentImagePath = imagePath
            CurrentImagePath = imagePath
            CurrentFileName = IO.Path.GetFileName(imagePath)
            RotationAngle = 0
            ScaleX = 1.0
            IsFitToWindow = If(_mainVm?.Settings IsNot Nothing, _mainVm.Settings.ViewerOpenFitToWindow, True)
            ActiveZoomPreset = If(_isFitToWindow, ZoomPresetMode.Fit, ZoomPresetMode.Actual)

            If allPaths IsNot Nothing Then
                _folderPaths = allPaths.
                    Where(Function(p) Not String.IsNullOrEmpty(p)).
                    Distinct(StringComparer.OrdinalIgnoreCase).
                    ToList()
                _currentIndex = _folderPaths.FindIndex(Function(p) String.Equals(p, imagePath, StringComparison.OrdinalIgnoreCase))
                LoadFilmstrip()
            Else
                Dim folder = IO.Path.GetDirectoryName(imagePath)
                LoadFolderContext(folder, imagePath)
            End If

            LoadBitmap()
            If _isFitToWindow Then UpdateFitZoom()
            UpdateStatus()
            LoadInfoPanelData(imagePath)

            _isFavorite = LibraryService.Instance.GetFavorite(imagePath)
            Me.RaisePropertyChanged(NameOf(IsFavorite))
            _rating = LibraryService.Instance.GetRating(imagePath)
            Me.RaisePropertyChanged(NameOf(Rating))
            Me.RaisePropertyChanged(NameOf(RatingText))
            Me.RaisePropertyChanged(NameOf(IsRawFile))
            Me.RaisePropertyChanged(NameOf(IsVideoFile))
            Me.RaisePropertyChanged(NameOf(ShowVideoUnavailableNotice))
            Me.RaisePropertyChanged(NameOf(HasNoMedia))
            Me.RaisePropertyChanged(NameOf(CanEdit))

            CurrentIndex = _currentIndex
        End Sub

        ' Öffnet das Bild im Editor mit aktivem Zuschneiden-Werkzeug und übernimmt den im
        ' Viewer per Ziehgeste ausgewählten Bildausschnitt als Vorschlag.
        ''' <summary>Filmstreifen-Pfade für den Editor: in einer Immich-Sitzung nur das aktuelle
        ''' (heruntergeladene) Bild, da _folderPaths dort Pseudo-Pfade enthält, die der Editor nicht laden kann.</summary>
        Private Function EditorFilmstripPaths() As List(Of String)
            If _isImmichSession Then Return New List(Of String) From {_currentImagePath}
            Return _folderPaths.ToList()
        End Function

        Public Async Sub OpenCropInEditor(cropLeft As Double, cropTop As Double, cropRight As Double, cropBottom As Double)
            If String.IsNullOrEmpty(_currentImagePath) OrElse _mainVm Is Nothing Then Return
            Await _mainVm.OpenImageInEditor(_currentImagePath, EditorFilmstripPaths(), _thumbCacheScopeId, _thumbCacheScopeName, forceSaveAsOnly:=_isImmichSession, immichAlbumId:=_immichSourceAlbumId)
            If _mainVm.Editor Is Nothing OrElse Not String.Equals(_mainVm.Editor.CurrentImagePath, _currentImagePath, StringComparison.OrdinalIgnoreCase) Then Return
            _mainVm.Editor.CurrentTool = EditorTool.Crop
            _mainVm.Editor.SetCropPercentages(cropLeft, cropTop, cropRight, cropBottom)
        End Sub

        Private Sub LoadBitmap()
            If VideoPreviewService.IsSupportedVideo(_currentImagePath) Then
                CurrentImage = Nothing
                ImageWidth = 0
                ImageHeight = 0
                LoadVideo(_currentImagePath)
                Return
            End If

            StopVideoPlayback()

            Try
                Dim bmp As Bitmap
                If RawPreviewService.IsSupportedRaw(_currentImagePath) Then
                    Using preview = RawPreviewService.ExtractPreview(_currentImagePath)
                        bmp = If(preview IsNot Nothing, ImageOrientationService.LoadOrientedAvaloniaBitmap(preview), Nothing)
                    End Using
                ElseIf SvgPreviewService.IsSupportedSvg(_currentImagePath) Then
                    Using preview = SvgPreviewService.ExtractPreview(_currentImagePath)
                        bmp = If(preview IsNot Nothing, New Bitmap(preview), Nothing)
                    End Using
                ElseIf IcoPreviewService.IsSupportedIco(_currentImagePath) Then
                    Using preview = IcoPreviewService.ExtractPreview(_currentImagePath)
                        bmp = If(preview IsNot Nothing, New Bitmap(preview), Nothing)
                    End Using
                Else
                    bmp = ImageOrientationService.LoadOrientedAvaloniaBitmap(_currentImagePath)
                End If
                If bmp Is Nothing Then Throw New Exception("Keine Vorschau extrahierbar")
                CurrentImage = bmp
                ImageWidth = CInt(bmp.Size.Width)
                ImageHeight = CInt(bmp.Size.Height)
            Catch
                CurrentImage = Nothing
                ImageWidth = 0
                ImageHeight = 0
            End Try
        End Sub

        Private Sub EnsureMediaPlayer()
            If _mediaPlayer IsNot Nothing Then Return
            If Not App.IsVideoPlaybackAvailable Then Return
            Try
                ' "--avcodec-hw=none" erzwingt Software-Decoding: Hardware-beschleunigte Pfade
                ' (VDPAU/VAAPI/DXVA2) verursachen beim Rendern in ein in eine fremde Toolkit-
                ' Oberfläche eingebettetes natives Fenster (wie hier über VideoView) auf Linux
                ' häufig sichtbare Bildartefakte/Kompositions-Fehler - daher ist Software-Decoding
                ' der Standard, Hardware-Beschleunigung lässt sich in den Einstellungen aktivieren
                ' (Settings.VideoHardwareAcceleration), falls das auf dem jeweiligen System nicht auftritt.
                Dim hwAccelArg = If(AppSettingsService.Load().VideoHardwareAcceleration, "--avcodec-hw=any", "--avcodec-hw=none")
                _libVlc = New LibVLC("--quiet", hwAccelArg)
                _mediaPlayer = New MediaPlayer(_libVlc)
                ' Sonst wählt das Ausgabefenster von VLC selbst ButtonPress/PointerMotion/KeyPress aus
                ' und verschluckt als oberstes natives Kindfenster jede Maus- und Tasteneingabe über dem
                ' Video. Im Vollbild deckt es das gesamte Fenster ab - die Oberfläche wirkt dann tot und
                ' lässt sich nur noch über den Fenstermanager (Alt+F4) beenden.
                _mediaPlayer.EnableMouseInput = False
                _mediaPlayer.EnableKeyInput = False
                _mediaPlayer.Mute = _isVideoMuted
                AddHandler _mediaPlayer.TimeChanged, AddressOf OnVideoTimeChanged
                AddHandler _mediaPlayer.LengthChanged, AddressOf OnVideoLengthChanged
                AddHandler _mediaPlayer.EndReached, AddressOf OnVideoEndReached
                AddHandler _mediaPlayer.Playing, AddressOf OnVideoPlayingChanged
                AddHandler _mediaPlayer.Paused, AddressOf OnVideoPausedChanged
                AddHandler _mediaPlayer.Stopped, AddressOf OnVideoPausedChanged
            Catch ex As Exception
                DiagnosticLogService.LogException("VideoPlayback.EnsureMediaPlayer", ex)
                _mediaPlayer?.Dispose()
                _mediaPlayer = Nothing
                _libVlc?.Dispose()
                _libVlc = Nothing
            End Try
        End Sub

        ''' Der von der View gemeinsam genutzte MediaPlayer für Fenster- und Vollbild-VideoView -
        ''' beide Controls sind fest verankert, es wird jeweils nur zugewiesen, welches gerade die
        ''' .MediaPlayer-Property gesetzt bekommt (siehe ViewerView.axaml.vb, UpdateActiveVideoView).
        Public ReadOnly Property VideoMediaPlayer As MediaPlayer
            Get
                Return _mediaPlayer
            End Get
        End Property

        Private _pendingVideoAutoplay As Boolean = False

        ' Play() darf erst aufgerufen werden, NACHDEM das VideoView der View sein natives
        ' Fenster-Handle an diesen MediaPlayer gebunden hat (UpdateActiveVideoView in
        ' ViewerView.axaml.vb, dort per StartPendingVideoAutoplay() ausgelöst) - andernfalls
        ' beginnt die Wiedergabe/Dekodierung ohne Ausgabeziel, und LibVLC erzeugt kurzzeitig ein
        ' eigenes, sichtbares Fenster, bis die eigentliche Zuweisung nachzieht.
        Private Sub LoadVideo(path As String)
            EnsureMediaPlayer()
            If _mediaPlayer Is Nothing Then Return
            Try
                _mediaPlayer.Stop()
                VideoPositionSeconds = 0
                VideoDurationSeconds = 0
                IsVideoPlaying = False
                _isVideoEnded = False
                Me.RaisePropertyChanged(NameOf(ShowVideoSurface))
                Using media As New Media(_libVlc, path, FromType.FromPath)
                    _mediaPlayer.Media = media
                End Using
                _pendingVideoAutoplay = True
            Catch ex As Exception
                DiagnosticLogService.LogException("VideoPlayback.LoadVideo", ex)
            End Try
        End Sub

        Public Sub StartPendingVideoAutoplay()
            If Not _pendingVideoAutoplay Then Return
            _pendingVideoAutoplay = False
            Try
                _mediaPlayer?.Play()
            Catch ex As Exception
                DiagnosticLogService.LogException("VideoPlayback.StartPendingVideoAutoplay", ex)
            End Try
        End Sub

        Public Sub StopVideoPlayback()
            If _mediaPlayer Is Nothing Then Return
            Try
                _mediaPlayer.Stop()
            Catch ex As Exception
                DiagnosticLogService.LogException("VideoPlayback.StopVideoPlayback", ex)
            End Try
            IsVideoPlaying = False
        End Sub

        Public Sub ShutdownVideo()
            If _mediaPlayer IsNot Nothing Then
                RemoveHandler _mediaPlayer.TimeChanged, AddressOf OnVideoTimeChanged
                RemoveHandler _mediaPlayer.LengthChanged, AddressOf OnVideoLengthChanged
                RemoveHandler _mediaPlayer.EndReached, AddressOf OnVideoEndReached
                RemoveHandler _mediaPlayer.Playing, AddressOf OnVideoPlayingChanged
                RemoveHandler _mediaPlayer.Paused, AddressOf OnVideoPausedChanged
                RemoveHandler _mediaPlayer.Stopped, AddressOf OnVideoPausedChanged
                Try
                    _mediaPlayer.Stop()
                Catch
                End Try
                _mediaPlayer.Dispose()
                _mediaPlayer = Nothing
            End If
            _libVlc?.Dispose()
            _libVlc = Nothing
        End Sub

        Private Sub ToggleVideoPlayPause()
            If _mediaPlayer Is Nothing Then Return

            ' Nach dem Ende ist die Ausgabefläche ausgeblendet (siehe ShowVideoSurface). Play() erst,
            ' wenn sie wieder da ist und ihr Handle am Player hängt - das erledigt die View über
            ' AttachVideoPlayer/StartPendingVideoAutoplay, sobald sie das Layout durchlaufen hat.
            If _isVideoEnded Then
                _isVideoEnded = False
                _pendingVideoAutoplay = True
                Me.RaisePropertyChanged(NameOf(ShowVideoSurface))
                Return
            End If

            If _mediaPlayer.IsPlaying Then
                _mediaPlayer.Pause()
            Else
                _mediaPlayer.Play()
            End If
        End Sub

        Private Sub SeekVideo(seconds As Double)
            If _mediaPlayer Is Nothing Then Return
            Try
                _isSeekingVideo = True
                _mediaPlayer.Time = CLng(Math.Max(0, seconds) * 1000)
                VideoPositionSeconds = seconds
            Finally
                _isSeekingVideo = False
            End Try
        End Sub

        Private Sub OnVideoTimeChanged(sender As Object, e As MediaPlayerTimeChangedEventArgs)
            If _isSeekingVideo Then Return
            Dispatcher.UIThread.Post(Sub() VideoPositionSeconds = e.Time / 1000.0)
        End Sub

        Private Sub OnVideoLengthChanged(sender As Object, e As MediaPlayerLengthChangedEventArgs)
            Dispatcher.UIThread.Post(Sub() VideoDurationSeconds = Math.Max(0, e.Length / 1000.0))
        End Sub

        ''' Am Ende bleibt LibVLC im Zustand "Ended" stehen: Play() ist von dort aus wirkungslos, das
        ''' Video ließe sich nicht erneut starten. Und weil keine Frames mehr kommen, behält das
        ''' Ausgabefenster den zuletzt gezeichneten Frame samt der Skalierung, die beim Zeichnen galt -
        ''' nach einem Vollbild-Wechsel steht das Bild danach verzerrt/beschnitten da.
        '''
        ''' Stop() räumt beides ab: der Zustand wird "Stopped" (Play() spielt von vorn), und das
        ''' Ausgabefenster verschwindet. Es MUSS außerhalb des LibVLC-Ereignisfadens laufen, sonst
        ''' verklemmt sich LibVLC - daher der Umweg über den UI-Faden.
        Private Sub OnVideoEndReached(sender As Object, e As EventArgs)
            Dispatcher.UIThread.Post(Sub()
                                          Try
                                              _mediaPlayer?.Stop()
                                          Catch ex As Exception
                                              DiagnosticLogService.LogException("VideoPlayback.OnVideoEndReached", ex)
                                          End Try
                                          _isVideoEnded = True
                                          Me.RaisePropertyChanged(NameOf(ShowVideoSurface))
                                          IsVideoPlaying = False
                                          ' Die letzte TimeChanged-Position vor EndReached entspricht dem letzten
                                          ' dekodierten Frame, nicht exakt dem Ende - der Regler blieb dadurch
                                          ' sichtbar vor 100% stehen, obwohl die (auf ganze Sekunden gerundete)
                                          ' Zeit-Anzeige schon "Ende/Ende" zeigte.
                                          VideoPositionSeconds = VideoDurationSeconds
                                      End Sub)
        End Sub

        Private Sub OnVideoPlayingChanged(sender As Object, e As EventArgs)
            Dispatcher.UIThread.Post(Sub() IsVideoPlaying = True)
        End Sub

        Private Sub OnVideoPausedChanged(sender As Object, e As EventArgs)
            Dispatcher.UIThread.Post(Sub() IsVideoPlaying = False)
        End Sub

        Private Sub LoadFolderContext(folder As String, currentPath As String)
            Dim exts = {".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".heic", ".avif", ".ico", ".svg", ".cr2", ".cr3", ".nef", ".arw", ".dng", ".pef", ".rw2", ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"}
            Try
                _folderPaths = Directory.GetFiles(folder).
                    Where(Function(f) exts.Contains(IO.Path.GetExtension(f).ToLowerInvariant())).
                    OrderBy(Function(f) IO.Path.GetFileName(f)).
                    ToList()
                _currentIndex = _folderPaths.FindIndex(Function(p) String.Equals(p, currentPath, StringComparison.OrdinalIgnoreCase))
                If _currentIndex < 0 Then _currentIndex = 0
                LoadFilmstrip()
            Catch
                _folderPaths = New List(Of String)()
                _currentIndex = 0
            End Try
        End Sub

        Private Sub LoadFilmstrip()
            For Each filmItem In FilmstripItems
                filmItem?.EvictThumbnail()
            Next
            FilmstripItems.ReplaceAll(_folderPaths.
                Where(Function(p) Not String.IsNullOrEmpty(p)).
                Select(AddressOf CreateFilmstripItem))
            MarkCurrentFilmstripItem()
            Dim itemsSnapshot = FilmstripItems.ToList()
            Dispatcher.UIThread.Post(Sub() ImageItem.QueueBackgroundThumbnails(itemsSnapshot), DispatcherPriority.Background)
        End Sub

        ''' <summary>Baut einen Filmstreifen-Eintrag: für Immich-Pseudo-Pfade ein Immich-Item (Thumbnail
        ''' aus dem Immich-Cache), sonst ein normales lokales Lightweight-Item.</summary>
        Private Function CreateFilmstripItem(pseudoOrPath As String) As ImageItem
            Dim assetId As String = Nothing, fileName As String = Nothing
            If ImmichService.TryParsePseudoPath(pseudoOrPath, assetId, fileName) Then
                Return ImageItem.CreateImmichItem(New ImmichAsset With {.Id = assetId, .FileName = fileName}, Nothing)
            End If
            Return ImageItem.CreateLightweight(pseudoOrPath, Nothing, _thumbCacheScopeId, _thumbCacheScopeName)
        End Function

        Private Sub UpdateStatus()
            Try
                Dim info = New FileInfo(_currentImagePath)
                Dim kb = info.Length / 1024.0
                Dim sizeStr = If(kb < 1024, $"{kb:F0} KB", $"{kb / 1024:F1} MB")
                StatusInfo = $"{ImageWidth} × {ImageHeight}  •  {sizeStr}"
            Catch
                StatusInfo = ""
            End Try
        End Sub

        Private Sub SetInfoTab(tabName As String)
            Select Case If(tabName, "").Trim().ToLowerInvariant()
                Case "exif"
                    SelectedInfoTab = InfoSidebarTab.Exif
                Case "iptc"
                    SelectedInfoTab = InfoSidebarTab.Iptc
                Case "xmp"
                    SelectedInfoTab = InfoSidebarTab.Xmp
                Case "icc"
                    SelectedInfoTab = InfoSidebarTab.Icc
                Case Else
                    SelectedInfoTab = InfoSidebarTab.General
            End Select
        End Sub

        Private Sub RaiseInfoTabStateChanged()
            Me.RaisePropertyChanged(NameOf(IsInfoTabGeneral))
            Me.RaisePropertyChanged(NameOf(IsInfoTabExif))
            Me.RaisePropertyChanged(NameOf(IsInfoTabIptc))
            Me.RaisePropertyChanged(NameOf(IsInfoTabXmp))
            Me.RaisePropertyChanged(NameOf(IsInfoTabIcc))
        End Sub

        Private Shared Function BuildImageInfo(imagePath As String, imageWidth As Integer, imageHeight As Integer) As ExifData
            Dim data = ExifService.ReadExif(imagePath)

            If imageWidth > 0 AndAlso imageHeight > 0 Then
                If String.IsNullOrWhiteSpace(data.ImageWidth) Then data.ImageWidth = imageWidth.ToString()
                If String.IsNullOrWhiteSpace(data.ImageHeight) Then data.ImageHeight = imageHeight.ToString()

                Dim mp = imageWidth * imageHeight / 1_000_000.0
                data.Megapixels = $"{mp:F1} MP"
                data.AspectRatio = FormatAspectRatio(imageWidth, imageHeight)
            End If

            If String.IsNullOrWhiteSpace(data.FileType) Then
                data.FileType = IO.Path.GetExtension(imagePath).TrimStart("."c).ToUpperInvariant()
            End If

            If String.IsNullOrWhiteSpace(data.ColorSpace) Then data.ColorSpace = "Unbekannt"

            Return data
        End Function

        Private Shared Function FormatAspectRatio(width As Integer, height As Integer) As String
            If width <= 0 OrElse height <= 0 Then Return ""

            Dim divisor = GreatestCommonDivisor(width, height)
            Return $"{width \ divisor}:{height \ divisor}"
        End Function

        Private Shared Function GreatestCommonDivisor(a As Integer, b As Integer) As Integer
            a = Math.Abs(a)
            b = Math.Abs(b)

            While b <> 0
                Dim remainder = a Mod b
                a = b
                b = remainder
            End While

            Return Math.Max(1, a)
        End Function

        ' Erhöht sich bei jedem LoadInfoPanelData-Aufruf - läuft der Nutzer währenddessen zum
        ' nächsten Bild weiter, verwirft der Dispatcher.UIThread.Post-Rücksprung unten das dann
        ' veraltete Ergebnis, statt EXIF/Histogramm eines längst verlassenen Bildes anzuzeigen.
        Private _infoPanelLoadToken As Integer = 0

        ' Pfad, für den HistogramImage zuletzt tatsächlich berechnet wurde - erlaubt
        ' EnsureHistogramLoaded, beim Einblenden der Info-Leiste zu erkennen, ob für das aktuelle
        ' Bild noch nachgeladen werden muss (siehe unten).
        Private _histogramLoadedForPath As String = ""

        ' EXIF-Lesen und v.a. die Histogramm-Berechnung (kompletter Re-Decode des Bildes von der
        ' Platte, siehe ImageProcessor.BuildHistogramImage) sind zu teuer, um sie wie zuvor
        ' synchron im UI-Thread bei jedem Bildwechsel auszuführen - das ließ den Viewer bei jeder
        ' Navigation kurz einfrieren. Läuft jetzt komplett im Hintergrund; nur die Zuweisung der
        ' fertigen Ergebnisse an die gebundenen Properties passiert per Dispatcher wieder im UI-Thread.
        ' Das Histogramm wird zusätzlich nur berechnet, wenn die Info-Leiste gerade sichtbar ist -
        ' andernfalls wäre die Arbeit für ein unsichtbares Panel verschwendet (siehe EnsureHistogramLoaded,
        ' das beim Einblenden für das dann aktuelle Bild nachlädt).
        Private Sub LoadInfoPanelData(imagePath As String, Optional preserveExistingTags As Boolean = False)
            Dim token = System.Threading.Interlocked.Increment(_infoPanelLoadToken)
            Dim capturedWidth = _imageWidth
            Dim capturedHeight = _imageHeight
            Dim loadHistogram = IsInfoSidebarVisible

            If Not preserveExistingTags Then
                Tags.Clear()
                For Each tag In LibraryService.Instance.GetTags(imagePath)
                    Tags.Add(tag)
                Next
            End If
            RefreshTagSuggestions()

            If Not loadHistogram Then HistogramImage = Nothing

            Task.Run(Sub()
                         Dim info = BuildImageInfo(imagePath, capturedWidth, capturedHeight)
                         Dim histogram As Bitmap = Nothing
                         If loadHistogram Then histogram = ImageProcessor.BuildHistogramImage(imagePath, 240, 120)
                         Dim exifForSearch = ExifService.ExtractSearchFields(info, imagePath)
                         LibraryService.Instance.SyncExifData(imagePath, exifForSearch, ExifService.BuildCatalogSummary(info, exifForSearch))

                         Dispatcher.UIThread.Post(Sub()
                                                       If token <> _infoPanelLoadToken Then Return
                                                       ExifInfo = info
                                                       If loadHistogram Then
                                                           HistogramImage = histogram
                                                           _histogramLoadedForPath = imagePath
                                                       End If
                                                   End Sub)
                     End Sub)
        End Sub

        Private Sub RefreshTagSuggestions()
            TagSuggestions.Clear()
            For Each tag In LibraryService.Instance.GetAllTags()
                TagSuggestions.Add(tag)
            Next
        End Sub

        ''' Lädt das Histogramm für das aktuell offene Bild nach, falls es (weil die Info-Leiste
        ''' beim letzten LoadInfoPanelData-Aufruf ausgeblendet war) noch nicht berechnet wurde -
        ''' aufgerufen von ToggleInfoSidebarCommand beim Einblenden.
        Private Sub EnsureHistogramLoaded()
            If String.IsNullOrEmpty(_currentImagePath) Then Return
            If String.Equals(_histogramLoadedForPath, _currentImagePath, StringComparison.OrdinalIgnoreCase) Then Return

            Dim imagePath = _currentImagePath
            Task.Run(Sub()
                         Dim histogram = ImageProcessor.BuildHistogramImage(imagePath, 240, 120)
                         Dispatcher.UIThread.Post(Sub()
                                                       If Not String.Equals(_currentImagePath, imagePath, StringComparison.OrdinalIgnoreCase) Then Return
                                                       HistogramImage = histogram
                                                       _histogramLoadedForPath = imagePath
                                                   End Sub)
                     End Sub)
        End Sub

        Public Sub RefreshLocalization()
            Me.RaisePropertyChanged(NameOf(SlideshowButtonText))
            Me.RaisePropertyChanged(NameOf(PositionText))
        End Sub

        Public Sub NavigatePrevious()
            _navDebouncer.QueuePrevious()
        End Sub

        Public Sub NavigateNext()
            _navDebouncer.QueueNext()
        End Sub

        ''' Für Mausrad-Navigation im Filmstrip/Viewer - normalisiert per Delta-Magnitude statt
        ''' pro Event einen vollen Schritt auszulösen (siehe FilmstripNavigationDebouncer.QueueWheelDelta).
        Public Sub NavigateByWheel(deltaY As Double)
            _navDebouncer.QueueWheelDelta(deltaY)
        End Sub

        Private Function CommitNavigateAsync(idx As Integer) As Task
            LoadPathAt(idx)
            Return Task.CompletedTask
        End Function

        Private Sub LoadPathAt(idx As Integer)
            If idx < 0 OrElse idx >= _folderPaths.Count Then Return
            If _isImmichSession Then
                LoadImmichAt(idx)
                Return
            End If
            Dim nextPath = _folderPaths(idx)
            If String.IsNullOrEmpty(nextPath) OrElse Not File.Exists(nextPath) Then
                _folderPaths.RemoveAll(Function(p) String.Equals(p, nextPath, StringComparison.OrdinalIgnoreCase))
                If _folderPaths.Count = 0 Then
                    CurrentImage = Nothing
                    CurrentImagePath = ""
                    CurrentFileName = ""
                    Return
                End If
                If idx >= _folderPaths.Count Then idx = _folderPaths.Count - 1
                nextPath = _folderPaths(idx)
                If String.IsNullOrEmpty(nextPath) OrElse Not File.Exists(nextPath) Then Return
            End If

            _currentImagePath = nextPath
            CurrentImagePath = _currentImagePath
            CurrentFileName = IO.Path.GetFileName(_currentImagePath)
            RotationAngle = 0
            ScaleX = 1.0
            ' Anders als OpenImage (frischer Start) NICHT mehr aus den Settings neu initialisieren -
            ' der zuletzt vom Nutzer gewählte Zoom-Modus soll über einen Bildwechsel hinweg erhalten
            ' bleiben (siehe ActiveZoomPreset), nur bei Manual bleibt der bisherige ZoomLevel stehen.
            Select Case _activeZoomPreset
                Case ZoomPresetMode.Fit
                    IsFitToWindow = True
                Case ZoomPresetMode.Actual
                    IsFitToWindow = False
                    ZoomLevel = 1.0
                Case Else
                    IsFitToWindow = False
            End Select
            LoadBitmap()
            If _isFitToWindow Then UpdateFitZoom()
            UpdateStatus()
            LoadInfoPanelData(_currentImagePath)
            _isFavorite = LibraryService.Instance.GetFavorite(_currentImagePath)
            Me.RaisePropertyChanged(NameOf(IsFavorite))
            _rating = LibraryService.Instance.GetRating(_currentImagePath)
            Me.RaisePropertyChanged(NameOf(Rating))
            Me.RaisePropertyChanged(NameOf(RatingText))
            Me.RaisePropertyChanged(NameOf(IsRawFile))
            Me.RaisePropertyChanged(NameOf(IsVideoFile))
            Me.RaisePropertyChanged(NameOf(ShowVideoUnavailableNotice))
            Me.RaisePropertyChanged(NameOf(HasNoMedia))
            Me.RaisePropertyChanged(NameOf(CanEdit))
            CurrentIndex = idx
        End Sub

        Public Sub NavigateToItem(item As ImageItem)
            If item Is Nothing Then Return
            Dim idx = _folderPaths.FindIndex(Function(p) String.Equals(p, item.FilePath, StringComparison.OrdinalIgnoreCase))
            If idx >= 0 Then LoadPathAt(idx)
        End Sub

        Private Sub DeleteCurrent()
            ' In einer Immich-Sitzung ist _currentImagePath nur die heruntergeladene Temp-Kopie - die zu
            ' löschen wäre wirkungslos (sie wäre beim nächsten Blättern wieder da). Gemeint ist das Asset.
            If _isImmichSession Then
                Dim ignored = DeleteCurrentImmichAssetAsync()
                Return
            End If
            If String.IsNullOrEmpty(_currentImagePath) Then Return
            Dim deletedPath = _currentImagePath
            _mainVm.RequestDeletePaths({deletedPath}, Sub()
                                                           _folderPaths.Remove(deletedPath)
                                                           If _currentIndex >= _folderPaths.Count Then _currentIndex = _folderPaths.Count - 1
                                                           If _currentIndex >= 0 Then
                                                               LoadFilmstrip()
                                                               LoadPathAt(_currentIndex)
                                                           Else
                                                               CurrentImage = Nothing
                                                               CurrentImagePath = ""
                                                               CurrentFileName = ""
                                                               _mainVm.BackToGallery(IO.Path.GetDirectoryName(deletedPath))
                                                           End If
                                                       End Sub)
        End Sub

        ''' <summary>Löscht das gerade gezeigte Immich-Asset auf dem Server. Erfordert die Einstellung
        ''' "Löschen in Immich erlauben"; "Endgültig löschen" umgeht den Immich-Papierkorb. Danach rückt der
        ''' Betrachter im Album weiter (bzw. zurück in die Galerie, wenn nichts mehr übrig ist).</summary>
        Private Async Function DeleteCurrentImmichAssetAsync() As Task
            Dim assetId = _currentImmichAssetId
            If String.IsNullOrEmpty(assetId) OrElse _currentIndex < 0 OrElse _currentIndex >= _folderPaths.Count Then Return

            Dim settings = AppSettingsService.Load()
            If Not settings.ImmichAllowDelete Then
                StatusInfo = LocalizationService.T("Löschen in Immich ist in den Einstellungen nicht erlaubt")
                Return
            End If

            Dim permanent = settings.ImmichDeletePermanently
            If Not settings.DeleteSkipConfirmation Then
                Dim verb = If(permanent,
                              LocalizationService.T("endgültig aus Immich löschen"),
                              LocalizationService.T("in den Immich-Papierkorb verschieben"))
                Dim confirmText = If(permanent, LocalizationService.T("Löschen"), LocalizationService.T("In den Papierkorb"))
                If Not Await _mainVm.ShowConfirmAsync(LocalizationService.T("Aus Immich löschen"),
                                                      $"{CurrentFileName} {verb}?",
                                                      confirmText,
                                                      LocalizationService.T("Abbrechen")) Then Return
            End If

            If Not Await ImmichService.DeleteAssetsAsync({assetId}, force:=permanent) Then
                StatusInfo = LocalizationService.T("Löschen in Immich fehlgeschlagen")
                Return
            End If

            Dim idx = _currentIndex
            _folderPaths.RemoveAt(idx)
            If idx < _immichSessionItems.Count Then _immichSessionItems.RemoveAt(idx)
            _mainVm.Gallery?.RemoveImmichItems({assetId})

            If _folderPaths.Count = 0 Then
                CurrentImage = Nothing
                CurrentImagePath = ""
                CurrentFileName = ""
                _mainVm.CurrentMode = AppMode.Gallery
                Return
            End If

            If idx >= _folderPaths.Count Then idx = _folderPaths.Count - 1
            LoadFilmstrip()
            LoadImmichAt(idx)
        End Function

        Private Sub RenameCurrent()
            If String.IsNullOrEmpty(_currentImagePath) Then Return
            Dim oldPath = _currentImagePath
            _mainVm.RequestRenamePath(oldPath, Sub(newPath)
                                                   Dim idx = _folderPaths.FindIndex(Function(p) String.Equals(p, oldPath, StringComparison.OrdinalIgnoreCase))
                                                   If idx >= 0 Then _folderPaths(idx) = newPath
                                                   LoadFilmstrip()
                                                   OpenImage(newPath, _folderPaths)
                                               End Sub)
        End Sub

        Public Sub ReleaseCurrentImageIfAny(paths As IEnumerable(Of String))
            If String.IsNullOrEmpty(_currentImagePath) OrElse paths Is Nothing Then Return
            If paths.Any(Function(p) String.Equals(p, _currentImagePath, StringComparison.OrdinalIgnoreCase)) Then
                CurrentImage = Nothing
            End If
        End Sub

        Private Sub CopyToClipboard()
            ' Clipboard-Zugriff erfolgt über TopLevel in der View (ViewModel hat keinen UI-Zugriff)
        End Sub

        Private Sub OpenInFileManager()
            If String.IsNullOrEmpty(_currentImagePath) Then Return
            Try
                Dim folder = IO.Path.GetDirectoryName(_currentImagePath)
                Diagnostics.Process.Start(New Diagnostics.ProcessStartInfo() With {
                    .FileName = folder,
                    .UseShellExecute = True
                })
            Catch
            End Try
        End Sub

        Public Sub ZoomIn()
            ActiveZoomPreset = ZoomPresetMode.Manual
            IsFitToWindow = False
            ZoomLevel = ZoomLevel * 1.25
        End Sub

        Public Sub ZoomOut()
            ActiveZoomPreset = ZoomPresetMode.Manual
            IsFitToWindow = False
            ZoomLevel = ZoomLevel / 1.25
        End Sub

        Public Sub SetImageViewportSize(width As Double, height As Double)
            _imageViewportWidth = Math.Max(0, width)
            _imageViewportHeight = Math.Max(0, height)
            If _isFitToWindow Then
                UpdateFitZoom()
            End If
        End Sub

        Private Sub UpdateFitZoom()
            If Not _isFitToWindow Then Return

            Dim fitZoom = CalculateFitZoom()
            ZoomLevel = fitZoom
        End Sub

        Private Function CalculateFitZoom() As Double
            If CurrentImage Is Nothing OrElse _imageViewportWidth <= 0 OrElse _imageViewportHeight <= 0 Then
                Return 1.0
            End If

            Dim imageWidth = CurrentImage.Size.Width
            Dim imageHeight = CurrentImage.Size.Height
            Dim angle = CInt(Math.Round(RotationAngle))
            angle = ((angle Mod 360) + 360) Mod 360
            If angle = 90 OrElse angle = 270 Then
                Dim tmp = imageWidth
                imageWidth = imageHeight
                imageHeight = tmp
            End If

            If imageWidth <= 0 OrElse imageHeight <= 0 Then
                Return 1.0
            End If

            Dim scaleX = _imageViewportWidth / imageWidth
            Dim scaleY = _imageViewportHeight / imageHeight
            Dim fitScale = Math.Max(0.05, Math.Min(scaleX, scaleY))

            ' "Nur wenn größer": kleinere Bilder nicht auf die Darstellungsfläche hochskalieren,
            ' sondern in Originalgröße (100%) zeigen - "Immer einpassen" (Default) skaliert dagegen
            ' auch kleinere Bilder auf die volle Fläche.
            If String.Equals(_mainVm?.Settings?.ViewerFitBehavior, "OnlyWhenLarger", StringComparison.OrdinalIgnoreCase) Then
                Return Math.Min(fitScale, 1.0)
            End If
            Return fitScale
        End Function

        Public Sub RaiseFullscreenChanged()
            Me.RaisePropertyChanged(NameOf(IsFullscreenMode))
            Me.RaisePropertyChanged(NameOf(TransparencyBackgroundBrush))
        End Sub

        Private Sub MarkCurrentFilmstripItem()
            If FilmstripItems Is Nothing Then Return
            For i = 0 To FilmstripItems.Count - 1
                FilmstripItems(i).IsSelected = (i = _currentIndex)
            Next
        End Sub

        Private Sub StartSlideshow()
            If _folderPaths.Count < 2 Then Return
            _mainVm.EnterFullscreen()
            IsSlideshowPlaying = True
            Dim intervalSeconds = If(_mainVm?.Settings IsNot Nothing, _mainVm.Settings.ViewerSlideshowIntervalSeconds, 3)
            _slideshowIntervalMs = Math.Max(1, intervalSeconds) * 1000.0
            _slideshowTimer = New Timer(_slideshowIntervalMs)
            AddHandler _slideshowTimer.Elapsed, AddressOf OnSlideshowTick
            _slideshowTimer.AutoReset = True
            _slideshowTimer.Start()
        End Sub

        Public Sub StopSlideshow()
            IsSlideshowPlaying = False
            If _slideshowTimer IsNot Nothing Then
                _slideshowTimer.Stop()
                RemoveHandler _slideshowTimer.Elapsed, AddressOf OnSlideshowTick
                _slideshowTimer.Dispose()
                _slideshowTimer = Nothing
            End If
        End Sub

        Private Sub OnSlideshowTick(sender As Object, e As ElapsedEventArgs)
            Dispatcher.UIThread.InvokeAsync(Sub()
                If _folderPaths.Count > 0 Then NavigateNext()
            End Sub)
        End Sub
    End Class

End Namespace
