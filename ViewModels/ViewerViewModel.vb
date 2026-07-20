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

Namespace ViewModels

    Public Class ViewerViewModel
        Inherits ViewModelBase

        ''' Weniger Schalter als im Editor - „Diashow starten" und „Anpassen" oben,
        ''' „Einpassen" in der Fußzeile.
        Protected Overrides ReadOnly Property ToolbarLabelWidthThreshold As Double
            Get
                Return 1150
            End Get
        End Property

        Private ReadOnly _mainVm As MainWindowViewModel
        Private _currentImagePath As String = ""
        Private _bitmapLoadToken As Integer = 0
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
        Private _hasPendingRotationSave As Boolean = False
        Private _suppressRotationDirty As Boolean = False
        Private _scaleX As Double = 1.0
        Private _rating As Integer = 0
        Private _isFavorite As Boolean = False
        Private _colorLabel As String = ""
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

        Private _mediaPlayer As MpvPlayer
        Private _isVideoPlaying As Boolean = False
        Private _videoPositionSeconds As Double = 0
        Private _videoDurationSeconds As Double = 0
        Private _isVideoMuted As Boolean = False
        Private _isSeekingVideo As Boolean = False
        Private _ignoreVideoTimeUpdatesUntilUtc As DateTime = DateTime.MinValue
        Private _videoPlaybackRuntimeFailed As Boolean = False
        Private _slideshowVideoEndSequence As Integer = 0

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
                ' Der Alpha-Scan läuft im HINTERGRUND (früher: Volldekode im Binding-Getter =
                ' UI-Hänger bei grossen PNGs); solange unbekannt, erst mal kein Schachbrett -
                ' der Callback zieht den Brush nach, sobald das Ergebnis vorliegt.
                Dim hasTransparency As Boolean = False
                If Not TransparencyBrushService.TryGetTransparency(_currentImagePath, hasTransparency,
                        Sub() Me.RaisePropertyChanged(NameOf(TransparencyBackgroundBrush))) Then
                    Return Avalonia.Media.Brushes.Transparent
                End If
                If Not hasTransparency Then
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
                Dim normalized = NormalizeRotationAngle(value)
                If Me.RaiseAndSetIfChanged(_rotationAngle, normalized) AndAlso
                   Not _suppressRotationDirty AndAlso
                   Not _isImmichSession AndAlso
                   Not String.IsNullOrEmpty(_currentImagePath) AndAlso
                   File.Exists(_currentImagePath) Then
                    _hasPendingRotationSave = normalized <> 0
                End If
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

        ''' Farbetikett (Hex der Akzentfarben-Palette, "" = keins) - lokal in der Bibliotheks-DB;
        ''' bei Immich-Sitzungen unter dem Pseudo-Pfad des Assets, damit die Galerie-Kachel den
        ''' gleichen Eintrag sieht.
        Public Property ColorLabel As String
            Get
                Return _colorLabel
            End Get
            Set(value As String)
                Dim normalized = If(value, "")
                If String.Equals(_colorLabel, normalized, StringComparison.OrdinalIgnoreCase) Then Return
                _colorLabel = normalized
                RaiseColorLabelProperties()
                If _isImmichSession Then
                    If _currentIndex >= 0 AndAlso _currentIndex < _immichSessionItems.Count Then
                        Dim meta = _immichSessionItems(_currentIndex)
                        meta.ColorLabel = normalized
                        LibraryService.Instance.SetColorLabelForMany({meta.FilePath}, normalized)
                    End If
                ElseIf Not String.IsNullOrEmpty(_currentImagePath) Then
                    LibraryService.Instance.SetColorLabelForMany({_currentImagePath}, normalized)
                End If
            End Set
        End Property

        Private Sub RaiseColorLabelProperties()
            Me.RaisePropertyChanged(NameOf(ColorLabel))
            Me.RaisePropertyChanged(NameOf(IsColorLabelOrange))
            Me.RaisePropertyChanged(NameOf(IsColorLabelRed))
            Me.RaisePropertyChanged(NameOf(IsColorLabelPink))
            Me.RaisePropertyChanged(NameOf(IsColorLabelPurple))
            Me.RaisePropertyChanged(NameOf(IsColorLabelBlue))
            Me.RaisePropertyChanged(NameOf(IsColorLabelCyan))
            Me.RaisePropertyChanged(NameOf(IsColorLabelTeal))
            Me.RaisePropertyChanged(NameOf(IsColorLabelGreen))
            Me.RaisePropertyChanged(NameOf(HasColorLabel))
            Me.RaisePropertyChanged(NameOf(ColorLabelBrush))
        End Sub

        Public ReadOnly Property HasColorLabel As Boolean
            Get
                Return Not String.IsNullOrEmpty(_colorLabel)
            End Get
        End Property

        ''' Punkt in der Fussleiste vor dem Dateinamen (gleiche Darstellung wie die Galerie-Kachel).
        Public ReadOnly Property ColorLabelBrush As Avalonia.Media.IBrush
            Get
                If String.IsNullOrEmpty(_colorLabel) Then Return Avalonia.Media.Brushes.Transparent
                Try
                    Return New Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(_colorLabel))
                Catch
                    Return Avalonia.Media.Brushes.Transparent
                End Try
            End Get
        End Property

        Private Function IsColorLabelValue(hex As String) As Boolean
            Return String.Equals(_colorLabel, hex, StringComparison.OrdinalIgnoreCase)
        End Function

        Public ReadOnly Property IsColorLabelOrange As Boolean
            Get
                Return IsColorLabelValue("#F08A1A")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelRed As Boolean
            Get
                Return IsColorLabelValue("#E74C3C")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelPink As Boolean
            Get
                Return IsColorLabelValue("#F03B88")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelPurple As Boolean
            Get
                Return IsColorLabelValue("#8B5CF6")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelBlue As Boolean
            Get
                Return IsColorLabelValue("#3B82F6")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelCyan As Boolean
            Get
                Return IsColorLabelValue("#0891B2")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelTeal As Boolean
            Get
                Return IsColorLabelValue("#0F766E")
            End Get
        End Property
        Public ReadOnly Property IsColorLabelGreen As Boolean
            Get
                Return IsColorLabelValue("#22C55E")
            End Get
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

        ''' Ob die Inline-Videowiedergabe im Viewer verfügbar ist. Für den Viewer wird libmpv
        ''' verwendet; fehlt die Bibliothek, zeigt die View stattdessen einen Hinweis.
        Public ReadOnly Property IsVideoPlaybackAvailable As Boolean
            Get
                Return App.IsInlineVideoPlaybackAvailable AndAlso Not _videoPlaybackRuntimeFailed
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
                If _mediaPlayer IsNot Nothing Then _mediaPlayer.SetMuted(value)
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
        Public ReadOnly Property PrintCommand As ICommand
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
        Public ReadOnly Property SetColorLabelCommand As ICommand
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
            ' Parameterlos: an ein ReactiveCommand.Create(Of T) gebundene Tastenkürzel wären mit
            ' Execute(Nothing) ein stiller No-Op.
            PrintCommand = ReactiveCommand.Create(Sub() PrintCurrent())
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
            RotateLeftCommand = ReactiveCommand.Create(Sub() RotationAngle = RotationAngle - 90)
            RotateRightCommand = ReactiveCommand.Create(Sub() RotationAngle = RotationAngle + 90)
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
            ' Gleiche Farbe erneut = Etikett entfernen (wie im Galerie-Kontextmenü).
            SetColorLabelCommand = ReactiveCommand.Create(Of String)(
                Sub(hex) ColorLabel = If(String.Equals(_colorLabel, If(hex, ""), StringComparison.OrdinalIgnoreCase), "", If(hex, "")))
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

            ' Infopanel SOFORT auf das neue Asset umschalten (Minimalstand): während des
            ' Original-Downloads (Sekunden) stand sonst das komplette Panel des vorherigen
            ' Bildes da (Nutzer-Befund 17.07., Filmstrip-Wechsel). Der volle EXIF-Stand kommt
            ' nach dem Download über LoadInfoPanelData mit der Temp-Kopie.
            BeginInfoPanelSwitch(pseudo, New ExifData With {
                .FileName = If(fileName, ""),
                .FileType = IO.Path.GetExtension(If(fileName, "")).TrimStart("."c).ToUpperInvariant()
            })

            ' Favorit/Rating/Stichwörter aus dem durchgereichten Galerie-Item übernehmen - Felder direkt
            ' setzen, damit die Property-Setter nicht sofort wieder an den Server zurückschreiben.
            If idx < _immichSessionItems.Count Then
                Dim meta = _immichSessionItems(idx)
                _isFavorite = meta.IsFavorite
                Me.RaisePropertyChanged(NameOf(IsFavorite))
                _rating = meta.Rating
                Me.RaisePropertyChanged(NameOf(Rating))
                Me.RaisePropertyChanged(NameOf(RatingText))
                ' Etikett ist lokal (Bibliotheks-DB, Pseudo-Pfad) - das Galerie-Item traegt es schon.
                _colorLabel = If(meta.ColorLabel, "")
                RaiseColorLabelProperties()
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
            ResetViewerRotation()
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
            ResetViewerRotation()
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
            _colorLabel = LibraryService.Instance.GetColorLabel(imagePath)
            RaiseColorLabelProperties()
            Me.RaisePropertyChanged(NameOf(IsRawFile))
            Me.RaisePropertyChanged(NameOf(IsVideoFile))
            Me.RaisePropertyChanged(NameOf(ShowVideoUnavailableNotice))
            Me.RaisePropertyChanged(NameOf(HasNoMedia))
            Me.RaisePropertyChanged(NameOf(CanEdit))

            CurrentIndex = _currentIndex
        End Sub

        Public Sub ReloadCurrentImageFromDisk(Optional evictCurrentThumbnail As Boolean = True)
            If String.IsNullOrEmpty(_currentImagePath) OrElse Not File.Exists(_currentImagePath) Then Return

            If evictCurrentThumbnail Then
                For Each filmItem In FilmstripItems.Where(Function(i) i IsNot Nothing AndAlso String.Equals(i.FilePath, _currentImagePath, StringComparison.OrdinalIgnoreCase))
                    filmItem.RefreshFileInfo()
                    filmItem.ClearThumbnail()
                Next
            End If

            LoadBitmap()
            If _isFitToWindow Then UpdateFitZoom()
            UpdateStatus()
            LoadInfoPanelData(_currentImagePath)
            Me.RaisePropertyChanged(NameOf(IsRawFile))
            Me.RaisePropertyChanged(NameOf(IsVideoFile))
            Me.RaisePropertyChanged(NameOf(ShowVideoUnavailableNotice))
            Me.RaisePropertyChanged(NameOf(HasNoMedia))
            Me.RaisePropertyChanged(NameOf(CanEdit))
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

        ''' <summary>Startet das Laden des aktuellen Bildes. Der DECODE laeuft im HINTERGRUND
        ''' (Analyse 2026-07-16: vorher synchron auf dem UI-Thread - jeder Bildwechsel fror den
        ''' Viewer fuer die Dekodier-Dauer ein, bei grossen JPEGs/RAWs deutlich spuerbar). Das
        ''' bisherige Bild bleibt sichtbar, bis das neue fertig ist; ueberholte Ergebnisse
        ''' verwirft der Lade-Token (schnelles Blaettern startet mehrere Loads, nur der juengste
        ''' gewinnt). Nach der Uebernahme werden Fit-Zoom und Statuszeile NACHGEZOGEN - die
        ''' Aufrufer haben sie direkt nach LoadBitmap() nur fuer das noch angezeigte alte Bild
        ''' aktualisiert.</summary>
        Private Sub LoadBitmap()
            If VideoPreviewService.IsSupportedVideo(_currentImagePath) Then
                ' Laufende Bild-Loads (inkl. FPX-Vollaufloesung) verwerfen - sonst wuerde ein spaet
                ' eintreffendes Bitmap das Video-Layout ueberschreiben.
                InvalidatePendingBitmapLoad()
                CurrentImage = Nothing
                ImageWidth = 0
                ImageHeight = 0
                LoadVideo(_currentImagePath)
                Return
            End If

            StopVideoPlayback()
            Dim token = System.Threading.Interlocked.Increment(_bitmapLoadToken)
            Dim path = _currentImagePath
            RunBitmapLoad(path, token, FpxService.IsFpx(path))
        End Sub

        ''' Verwirft ein eventuell laufendes asynchrones Bild-Laden (Bildwechsel auf Video,
        ''' Loeschen/Freigeben der Datei) - ohne das koennte ein spaetes Decode-Ergebnis ein
        ''' bewusst geleertes CurrentImage wieder "auferstehen" lassen.
        Private Sub InvalidatePendingBitmapLoad()
            System.Threading.Interlocked.Increment(_bitmapLoadToken)
        End Sub

        Private Async Sub RunBitmapLoad(path As String, token As Integer, isFpx As Boolean)
            Dim bmp As Bitmap = Nothing
            Try
                bmp = Await Task.Run(Function() DecodeViewerBitmap(path))
            Catch
                bmp = Nothing
            End Try

            If Not ApplyLoadedBitmap(token, bmp) Then Return
            ' FPX: das schnelle Komposit steht - die Vollaufloesung zieht mit demselben Token nach.
            If isFpx AndAlso bmp IsNot Nothing Then LoadFpxFullResolutionBitmapAsync(path, token)
        End Sub

        ''' <summary>Uebernimmt ein fertig dekodiertes Bitmap, WENN der Token noch aktuell ist -
        ''' sonst wird es verworfen (False). Zieht Fit-Zoom und Status nach.</summary>
        Private Function ApplyLoadedBitmap(token As Integer, bmp As Bitmap) As Boolean
            If token <> System.Threading.Volatile.Read(_bitmapLoadToken) Then
                bmp?.Dispose()
                Return False
            End If

            If bmp Is Nothing Then
                CurrentImage = Nothing
                ImageWidth = 0
                ImageHeight = 0
            Else
                CurrentImage = bmp
                ImageWidth = CInt(bmp.Size.Width)
                ImageHeight = CInt(bmp.Size.Height)
            End If
            If _isFitToWindow Then UpdateFitZoom()
            UpdateStatus()
            Return True
        End Function

        ''' Reiner Decode ohne ViewModel-Zustand - laeuft im Task.Run-Worker.
        Private Shared Function DecodeViewerBitmap(path As String) As Bitmap
            If RawPreviewService.IsSupportedRaw(path) Then
                Using preview = RawPreviewService.ExtractPreviewWithFallback(path)
                    ' Gedrehte RAWs tragen ihre Drehung im Sidecar (die Datei selbst wird nie
                    ' neu geschrieben) - beim Anzeigen also wieder drauflegen.
                    Return If(preview IsNot Nothing,
                              ImageOrientationService.LoadOrientedAvaloniaBitmap(preview, RawSidecarService.ReadRotationDegrees(path)),
                              Nothing)
                End Using
            End If
            If SvgPreviewService.IsSupportedSvg(path) Then
                Using preview = SvgPreviewService.ExtractPreview(path)
                    Return If(preview IsNot Nothing, New Bitmap(preview), Nothing)
                End Using
            End If
            If IcoPreviewService.IsSupportedIco(path) Then
                Using preview = IcoPreviewService.ExtractPreview(path)
                    Return If(preview IsNot Nothing, New Bitmap(preview), Nothing)
                End Using
            End If
            If PsdPreviewService.IsSupportedPsd(path) Then
                Using preview = PsdPreviewService.ExtractPreview(path)
                    Return If(preview IsNot Nothing, New Bitmap(preview), Nothing)
                End Using
            End If
            If FpxService.IsFpx(path) Then
                Using preview = FpxService.ExtractComposite(path)
                    Return If(preview IsNot Nothing, New Bitmap(preview), Nothing)
                End Using
            End If
            Return ImageOrientationService.LoadOrientedAvaloniaBitmap(path)
        End Function

        Private Async Sub LoadFpxFullResolutionBitmapAsync(path As String, token As Integer)
            If String.IsNullOrWhiteSpace(path) Then Return
            Try
                Dim full = Await Task.Run(Function() ImageProcessor.RenderFpxFullResolutionBitmap(path))
                If token <> System.Threading.Volatile.Read(_bitmapLoadToken) Then
                    full?.Dispose()
                    Return
                End If
                If Not String.Equals(path, _currentImagePath, StringComparison.OrdinalIgnoreCase) Then
                    full?.Dispose()
                    Return
                End If
                If full Is Nothing Then Return
                CurrentImage = full
                ImageWidth = CInt(full.Size.Width)
                ImageHeight = CInt(full.Size.Height)
                If _isFitToWindow Then UpdateFitZoom()
                UpdateStatus()
            Catch
            End Try
        End Sub

        Private Sub EnsureMediaPlayer()
            If _mediaPlayer IsNot Nothing Then Return
            If Not IsVideoPlaybackAvailable Then Return
            Try
                _mediaPlayer = New MpvPlayer(AppSettingsService.Load().VideoHardwareAcceleration)
                _mediaPlayer.SetMuted(_isVideoMuted)
                AddHandler _mediaPlayer.TimeChanged, AddressOf OnVideoTimeChanged
                AddHandler _mediaPlayer.DurationChanged, AddressOf OnVideoLengthChanged
                AddHandler _mediaPlayer.EndReached, AddressOf OnVideoEndReached
                AddHandler _mediaPlayer.PauseChanged, AddressOf OnVideoPauseChanged
                AddHandler _mediaPlayer.MuteChanged, AddressOf OnVideoMuteChanged
                AddHandler _mediaPlayer.InitializationFailed, AddressOf OnVideoInitializationFailed
            Catch ex As Exception
                DiagnosticLogService.LogException("VideoPlayback.EnsureMediaPlayer", ex)
                _mediaPlayer = Nothing
            End Try
        End Sub

        ''' Der von der View gemeinsam genutzte libmpv-Player für Fenster- und Vollbild-VideoView -
        ''' beide Controls sind fest verankert, es wird jeweils nur zugewiesen, welches gerade die
        ''' Player-Property gesetzt bekommt (siehe ViewerView.axaml.vb, UpdateActiveVideoView).
        Public ReadOnly Property VideoMediaPlayer As MpvPlayer
            Get
                Return _mediaPlayer
            End Get
        End Property

        Private _pendingVideoAutoplay As Boolean = False

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
                _mediaPlayer.Load(path)
                _pendingVideoAutoplay = True
            Catch ex As Exception
                DiagnosticLogService.LogException("VideoPlayback.LoadVideo", ex)
            End Try
        End Sub

        Public Sub StartPendingVideoAutoplay()
            If Not _pendingVideoAutoplay Then Return
            _pendingVideoAutoplay = False
            Try
                _mediaPlayer?.LoadPending()
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
                DetachMediaPlayerHandlers(_mediaPlayer)
                _mediaPlayer.Dispose()
                _mediaPlayer = Nothing
            End If
        End Sub

        Private Sub DetachMediaPlayerHandlers(player As MpvPlayer)
            If player Is Nothing Then Return
            RemoveHandler player.TimeChanged, AddressOf OnVideoTimeChanged
            RemoveHandler player.DurationChanged, AddressOf OnVideoLengthChanged
            RemoveHandler player.EndReached, AddressOf OnVideoEndReached
            RemoveHandler player.PauseChanged, AddressOf OnVideoPauseChanged
            RemoveHandler player.MuteChanged, AddressOf OnVideoMuteChanged
            RemoveHandler player.InitializationFailed, AddressOf OnVideoInitializationFailed
        End Sub

        Private Sub ToggleVideoPlayPause()
            If _mediaPlayer Is Nothing Then Return

            If _isVideoEnded Then
                If String.IsNullOrEmpty(_currentImagePath) Then Return
                LoadVideo(_currentImagePath)
                Return
            End If

            _mediaPlayer.TogglePause()
        End Sub

        Private Sub SeekVideo(seconds As Double)
            If _mediaPlayer Is Nothing Then Return
            Try
                _isSeekingVideo = True
                _ignoreVideoTimeUpdatesUntilUtc = DateTime.UtcNow.AddMilliseconds(250)
                _mediaPlayer.Seek(seconds)
                VideoPositionSeconds = seconds
            Finally
                _isSeekingVideo = False
            End Try
        End Sub

        Private Sub OnVideoTimeChanged(seconds As Double)
            If _isSeekingVideo Then Return
            If DateTime.UtcNow < _ignoreVideoTimeUpdatesUntilUtc Then Return
            Dispatcher.UIThread.Post(Sub() VideoPositionSeconds = seconds)
        End Sub

        Private Sub OnVideoInitializationFailed(ex As Exception)
            Dispatcher.UIThread.Post(Sub()
                                          DiagnosticLogService.LogException("VideoPlayback.Initialize", ex)
                                          _videoPlaybackRuntimeFailed = True
                                          _pendingVideoAutoplay = False
                                          IsVideoPlaying = False
                                          _isVideoEnded = False

                                          Dim failedPlayer = _mediaPlayer
                                          If failedPlayer IsNot Nothing Then
                                              DetachMediaPlayerHandlers(failedPlayer)
                                              failedPlayer.Dispose()
                                              If Object.ReferenceEquals(_mediaPlayer, failedPlayer) Then _mediaPlayer = Nothing
                                          End If

                                          Me.RaisePropertyChanged(NameOf(IsVideoPlaybackAvailable))
                                          Me.RaisePropertyChanged(NameOf(ShowVideoUnavailableNotice))
                                          Me.RaisePropertyChanged(NameOf(ShowVideoSurface))
                                      End Sub)
        End Sub

        Private Sub OnVideoLengthChanged(seconds As Double)
            Dispatcher.UIThread.Post(Sub() VideoDurationSeconds = Math.Max(0, seconds))
        End Sub

        Private Sub OnVideoEndReached(reason As Integer, [error] As Integer)
            Dispatcher.UIThread.Post(Sub()
                                          If reason <> CInt(MpvInterop.MpvEndFileReason.Eof) Then
                                              IsVideoPlaying = False
                                              Return
                                          End If
                                          If _isSlideshowPlaying Then
                                              IsVideoPlaying = False
                                              VideoPositionSeconds = VideoDurationSeconds
                                              _slideshowVideoEndSequence += 1
                                              ContinueSlideshowAfterVideoEndAsync(_slideshowVideoEndSequence)
                                              Return
                                          End If
                                          _isVideoEnded = True
                                          Me.RaisePropertyChanged(NameOf(ShowVideoSurface))
                                          IsVideoPlaying = False
                                          VideoPositionSeconds = VideoDurationSeconds
                                      End Sub)
        End Sub

        Private Sub OnVideoPauseChanged(isPaused As Boolean)
            Dispatcher.UIThread.Post(Sub() IsVideoPlaying = Not isPaused AndAlso Not _isVideoEnded)
        End Sub

        Private Sub OnVideoMuteChanged(isMuted As Boolean)
            Dispatcher.UIThread.Post(Sub() IsVideoMuted = isMuted)
        End Sub

        Private Async Sub ContinueSlideshowAfterVideoEndAsync(sequence As Integer)
            Await Task.Delay(1000)
            If Not _isSlideshowPlaying OrElse sequence <> _slideshowVideoEndSequence Then Return
            If Not IsVideoFile Then Return
            NavigateNext()
        End Sub

        Private Sub LoadFolderContext(folder As String, currentPath As String)
            ' ".fpx" gehört dazu: Projekte blättern im Viewer/Vollbild mit (Anzeige aus dem Composite).
            ' Feste Formate plus die kanonischen RAW-Endungen (RawPreviewService.SupportedExtensions).
            Dim exts = {
                ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp", ".heic", ".avif",
                ".ico", ".svg", ".fpx", ".psd", ".psb",
                ".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"
            }.Concat(RawPreviewService.SupportedExtensions).ToArray()
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

        ' Pfad, dessen Daten das Infopanel gerade zeigt (auch provisorisch) - verhindert beim
        ' Neuladen DESSELBEN Bildes (Tag-Edit, Sidebar-Toggle) das kurze Zurückfallen auf den
        ' provisorischen Katalog-Stand.
        Private _infoPanelShownForPath As String = ""

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

            If Not loadHistogram Then
                _histogramLoadedForPath = ""
                HistogramImage = Nothing
            End If

            ' NUTZER-BEFUND (17.07., 2. Runde): Beim schnellen Blättern blieb das KOMPLETTE Panel
            ' auf dem vorherigen Bild stehen - ExifInfo wurde erst nach der gesamten Hintergrund-
            ' Arbeit ersetzt, und dazu gehörte auch der Histogramm-Volldecode. Deshalb jetzt
            ' dreistufig: (1) SOFORT auf einen Stand des NEUEN Bildes wechseln - aus dem Katalog,
            ' wenn er das Bild schon kennt (Nutzervorschlag), sonst leeren; (2) das EXIF-Ergebnis
            ' posten, SOBALD es gelesen ist; (3) das Histogramm separat nachschieben, ohne das
            ' EXIF-Update dahinter aufzuhalten.
            If Not String.Equals(_infoPanelShownForPath, imagePath, StringComparison.OrdinalIgnoreCase) Then
                SetProvisionalInfoPanelForPath(imagePath)
            End If

            Task.Run(Sub()
                         ' Maße aus dem DATEI-Header statt aus dem VM-Zustand: seit der Viewer
                         ' asynchron lädt, hielt _imageWidth beim Aufruf noch das VORHERIGE Bild -
                         ' MP/Seitenverhältnis/Maße im Infopanel blieben bei schnellem Blättern
                         ' auf dem alten Stand (Nutzer-Befund 2026-07-17).
                         Dim headerSize = ImageProcessor.GetOrientedImageSize(imagePath)
                         Dim infoWidth = If(headerSize.Width > 0, headerSize.Width, capturedWidth)
                         Dim infoHeight = If(headerSize.Height > 0, headerSize.Height, capturedHeight)
                         Dim info = BuildImageInfo(imagePath, infoWidth, infoHeight)
                         Dispatcher.UIThread.Post(Sub()
                                                       If token <> _infoPanelLoadToken Then Return
                                                       ExifInfo = info
                                                       _infoPanelShownForPath = imagePath
                                                   End Sub)

                         Dim exifForSearch = ExifService.ExtractSearchFields(info, imagePath)
                         LibraryService.Instance.SyncExifData(imagePath, exifForSearch, ExifService.BuildCatalogSummary(info, exifForSearch))

                         If loadHistogram Then
                             Dim histogram = ImageProcessor.BuildHistogramImage(imagePath, 240, 120)
                             Dispatcher.UIThread.Post(Sub()
                                                           If token <> _infoPanelLoadToken Then
                                                               histogram?.Dispose()
                                                               Return
                                                           End If
                                                           HistogramImage = histogram
                                                           _histogramLoadedForPath = imagePath
                                                       End Sub)
                         End If
                     End Sub)
        End Sub

        ''' <summary>Startet einen Bildwechsel fürs Infopanel sofort: alte Hintergrund-Posts werden per
        ''' Token ungültig, das Panel bekommt ein neues ExifData-Objekt und das alte Histogramm wird
        ''' entfernt. Das passiert bewusst VOR dem eigentlichen Bitmap-/EXIF-Decode, damit schnelle
        ''' Filmstrip-Klicks nie sichtbare Daten des vorherigen Bildes stehen lassen.</summary>
        Private Function BeginInfoPanelSwitch(imagePath As String, Optional provisionalInfo As ExifData = Nothing) As Integer
            Dim token = System.Threading.Interlocked.Increment(_infoPanelLoadToken)
            SetProvisionalInfoPanelForPath(imagePath, provisionalInfo)
            Return token
        End Function

        Private Sub SetProvisionalInfoPanelForPath(imagePath As String, Optional provisionalInfo As ExifData = Nothing)
            ExifInfo = If(provisionalInfo, BuildProvisionalInfoFromCatalog(imagePath))
            _infoPanelShownForPath = imagePath
            ' Das Histogramm des alten Bildes ebenfalls sofort raus - es käme sonst als letztes
            ' Relikt des vorherigen Bildes erst mit dem Nachschub-Post weg.
            _histogramLoadedForPath = ""
            HistogramImage = Nothing
        End Sub

        ''' <summary>Provisorischer Infopanel-Stand aus dem Katalog (SQLite, ein Zeilen-Lookup) -
        ''' zeigt beim Bildwechsel sofort die Daten des RICHTIGEN Bildes, bis der vollständige
        ''' EXIF-Read sie ersetzt. Kennt der Katalog das Bild nicht, kommt ein MINIMAL-Objekt
        ''' (Dateiname/Typ, Rest leer) zurück - NIE Nothing: Bindings wie „ExifInfo.Camera"
        ''' aktualisieren bei Nothing nicht auf leer, sondern behalten stumpf den letzten Wert -
        ''' genau so blieb das Panel beim Filmstrip-Wechsel auf dem Vorgängerbild stehen
        ''' (Nutzer-Befund 17.07., 3. Runde).</summary>
        Private Shared Function BuildProvisionalInfoFromCatalog(imagePath As String) As ExifData
            Try
                Dim meta = LibraryService.Instance.GetMetaForPaths({imagePath}).Values.FirstOrDefault()
                If meta Is Nothing Then
                    Return New ExifData With {
                        .FileName = IO.Path.GetFileName(imagePath),
                        .FileType = IO.Path.GetExtension(imagePath).TrimStart("."c).ToUpperInvariant()
                    }
                End If

                Dim data As New ExifData With {
                    .FileName = IO.Path.GetFileName(imagePath),
                    .FileType = IO.Path.GetExtension(imagePath).TrimStart("."c).ToUpperInvariant(),
                    .DateTaken = If(meta.DateTaken, ""),
                    .DateModifiedExif = If(meta.DateModifiedExif, ""),
                    .Camera = If(meta.Camera, ""),
                    .Lens = If(meta.Lens, ""),
                    .ShutterSpeed = If(meta.ShutterSpeed, "")
                }
                If meta.Aperture.HasValue Then data.Aperture = "f/" & meta.Aperture.Value.ToString("0.#", Globalization.CultureInfo.InvariantCulture)
                If meta.FocalLengthMm.HasValue Then data.FocalLength = meta.FocalLengthMm.Value.ToString("0.#", Globalization.CultureInfo.InvariantCulture) & " mm"
                If meta.Iso.HasValue Then data.ISO = meta.Iso.Value.ToString(Globalization.CultureInfo.InvariantCulture)
                If meta.ImageWidth.GetValueOrDefault() > 0 AndAlso meta.ImageHeight.GetValueOrDefault() > 0 Then
                    Dim w = meta.ImageWidth.Value
                    Dim h = meta.ImageHeight.Value
                    data.ImageWidth = w.ToString(Globalization.CultureInfo.InvariantCulture)
                    data.ImageHeight = h.ToString(Globalization.CultureInfo.InvariantCulture)
                    data.Megapixels = $"{w * h / 1_000_000.0:F1} MP"
                    data.AspectRatio = FormatAspectRatio(w, h)
                End If
                Return data
            Catch
                ' Auch im Fehlerfall nie Nothing (siehe Methodenkommentar - Bindings blieben sonst
                ' auf dem Vorgängerbild stehen).
                Return New ExifData With {.FileName = IO.Path.GetFileName(If(imagePath, ""))}
            End Try
        End Function

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
            ' Während eines Immich-Wechsels zeigt das Infopanel bereits den neuen Pseudo-Pfad,
            ' _currentImagePath verweist aber bis zum Downloadende noch auf die alte Temp-Datei.
            ' In dieser Zwischenzeit kein Histogramm nachladen, sonst erscheint wieder das alte Bild.
            If Not String.Equals(_infoPanelShownForPath, _currentImagePath, StringComparison.OrdinalIgnoreCase) Then Return
            If String.Equals(_histogramLoadedForPath, _currentImagePath, StringComparison.OrdinalIgnoreCase) Then Return

            Dim imagePath = _currentImagePath
            Task.Run(Sub()
                         Dim histogram = ImageProcessor.BuildHistogramImage(imagePath, 240, 120)
                         Dispatcher.UIThread.Post(Sub()
                                                       If Not String.Equals(_currentImagePath, imagePath, StringComparison.OrdinalIgnoreCase) OrElse
                                                          Not String.Equals(_infoPanelShownForPath, imagePath, StringComparison.OrdinalIgnoreCase) Then
                                                           histogram?.Dispose()
                                                           Return
                                                       End If
                                                       HistogramImage = histogram
                                                       _histogramLoadedForPath = imagePath
                                                   End Sub)
                     End Sub)
        End Sub

        Public Sub RefreshLocalization()
            Me.RaisePropertyChanged(NameOf(SlideshowButtonText))
            Me.RaisePropertyChanged(NameOf(PositionText))
        End Sub

        Private Shared Function NormalizeRotationAngle(value As Double) As Double
            Dim rounded = CInt(Math.Round(value / 90.0)) * 90
            Return ((rounded Mod 360) + 360) Mod 360
        End Function

        Private Sub ResetViewerRotation()
            _suppressRotationDirty = True
            Try
                RotationAngle = 0
            Finally
                _suppressRotationDirty = False
            End Try
            _hasPendingRotationSave = False
        End Sub

        ''' <summary>Formate, in die der Viewer eine Drehung GEBACKEN zurückschreiben darf. Bewusst
        ''' eine Whitelist (wie GalleryViewModel.BatchImageEditWritableExtensions): die Speicherroutine
        ''' kann nur JPEG/PNG/WebP erzeugen, und ein Ziel mit fremder Endung bekäme still ein JPEG
        ''' untergeschoben - bei RAW/PSD/SVG/.fpx wäre das Original damit vernichtet.</summary>
        Private Shared ReadOnly RotationBakeableExtensions As String() = {".jpg", ".jpeg", ".png", ".webp"}

        Private Shared Function CanBakeRotation(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Return RotationBakeableExtensions.Contains(IO.Path.GetExtension(path).ToLowerInvariant())
        End Function

        ''' <summary>Kann die Drehung dieses Bildes überhaupt dauerhaft werden - gebacken oder als
        ''' Rezept neben der Datei?</summary>
        Private Shared Function CanPersistRotation(path As String) As Boolean
            Return CanBakeRotation(path) OrElse RawPreviewService.IsSupportedRaw(path)
        End Function

        Public Async Function ConfirmPendingRotationAsync(actionDescription As String) As Task(Of Boolean)
            If Not _hasPendingRotationSave OrElse NormalizeRotationAngle(_rotationAngle) = 0 Then Return True
            If String.IsNullOrEmpty(_currentImagePath) OrElse Not File.Exists(_currentImagePath) Then
                ResetViewerRotation()
                Return True
            End If

            ' Nur-lesbare Formate (PSD, SVG, ICO, Video, .fpx): gar nicht erst zum Speichern
            ' auffordern - die Drehung bleibt reine Ansicht und wird beim Wechsel verworfen.
            If Not CanPersistRotation(_currentImagePath) Then
                ResetViewerRotation()
                Return True
            End If

            Dim save = Await _mainVm.ShowConfirmAsync(
                LocalizationService.T("Drehung speichern?"),
                String.Format(
                    LocalizationService.T("Soll die Drehung von {0} gespeichert werden, bevor du {1}?"),
                    IO.Path.GetFileName(_currentImagePath),
                    LocalizationService.T(actionDescription)),
                LocalizationService.T("Speichern"),
                LocalizationService.T("Verwerfen"))
            If save Then
                If Not Await SavePendingRotationAsync() Then Return False
            Else
                ResetViewerRotation()
            End If

            Return True
        End Function

        Private Async Function SavePendingRotationAsync() As Task(Of Boolean)
            Dim angle = CInt(NormalizeRotationAngle(_rotationAngle))
            If angle = 0 Then
                ResetViewerRotation()
                Return True
            End If

            Dim source = _currentImagePath

            ' RAW wird NIE neu geschrieben - wir könnten das Format gar nicht erzeugen und würden
            ' die Rohdaten durch ihre eigene eingebettete JPEG-Vorschau ersetzen. Die Drehung geht
            ' stattdessen nicht-destruktiv in die Rezept-Begleitdatei (foto.cr2.fpxmp), genau wie
            ' beim Editor. Viewer, Filmstreifen und Kacheln lesen sie beim Anzeigen wieder aus.
            If RawPreviewService.IsSupportedRaw(source) Then Return Await SaveRotationToSidecarAsync(source, angle)

            Dim ext = IO.Path.GetExtension(source)
            Dim temp = IO.Path.Combine(IO.Path.GetDirectoryName(source), $".{IO.Path.GetFileNameWithoutExtension(source)}.ferrumpix-rotate-{Guid.NewGuid():N}{ext}")
            Dim preserveMetadata = If(_mainVm?.Settings IsNot Nothing, _mainVm.Settings.PreserveMetadataOnSave, AppSettingsService.Load().PreserveMetadataOnSave)
            Dim ok = False
            Dim errorMessage As String = Nothing

            Try
                ok = Await Task.Run(Function()
                                        Dim adj = New ImageAdjustments With {.RotationDegrees = angle}
                                        Return ImageProcessor.SaveImage(source, temp, adj, 95, preserveMetadata)
                                    End Function)
                If ok AndAlso File.Exists(temp) Then
                    File.Copy(temp, source, True)
                    ExifService.Invalidate(source)
                    LoadBitmap()
                    ResetViewerRotation()
                    UpdateStatus()
                    Return True
                End If
                errorMessage = LocalizationService.T("Bild konnte nicht gespeichert werden")
            Catch ex As Exception
                errorMessage = ex.Message
            Finally
                Try
                    If File.Exists(temp) Then File.Delete(temp)
                Catch
                End Try
            End Try

            Await _mainVm.ShowMessageAsync(LocalizationService.T("Drehung speichern"), If(errorMessage, LocalizationService.T("Bild konnte nicht gespeichert werden")))
            Return False
        End Function

        ''' <summary>Legt die Drehung einer RAW-Datei im Rezept-Sidecar ab (foto.cr2.fpxmp) statt sie
        ''' in Pixel zu backen. Ein schon vorhandenes Rezept (aus dem Editor) bleibt vollständig
        ''' erhalten - nur RotationDegrees wird um den neuen Winkel weitergedreht.</summary>
        Private Async Function SaveRotationToSidecarAsync(source As String, angle As Integer) As Task(Of Boolean)
            Dim ok = False
            Dim errorMessage As String = Nothing
            Try
                ok = Await Task.Run(Function()
                                        Dim adj = If(RawSidecarService.TryRead(source), New ImageAdjustments())
                                        adj.RotationDegrees = CInt(NormalizeRotationAngle(adj.RotationDegrees + angle))
                                        Return RawSidecarService.TryWrite(source, adj)
                                    End Function)
                If ok Then
                    ' Das Original ist unverändert - nur der Sidecar ist neu. Der Disk-Cache merkt das
                    ' von selbst (ThumbnailCacheService zieht die Sidecar-Zeit in den Dateinamen), aber
                    ' die bereits geladenen Bitmaps im Speicher muessen weg, sonst bleibt die Kachel
                    ' im Filmstreifen und in der Gallery ungedreht stehen.
                    For Each filmItem In FilmstripItems.Where(Function(i) i IsNot Nothing AndAlso PathIdentity.AreSame(i.FilePath, source))
                        filmItem.EvictThumbnail()
                    Next
                    _mainVm?.Gallery?.RefreshThumbnailFor(source)
                    LoadBitmap()
                    ResetViewerRotation()
                    UpdateStatus()
                    Return True
                End If
                errorMessage = LocalizationService.T("Drehung konnte nicht gespeichert werden")
            Catch ex As Exception
                errorMessage = ex.Message
            End Try

            Await _mainVm.ShowMessageAsync(LocalizationService.T("Drehung speichern"), If(errorMessage, LocalizationService.T("Drehung konnte nicht gespeichert werden")))
            Return False
        End Function

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

        Private Async Function CommitNavigateAsync(idx As Integer) As Task
            If Not Await ConfirmPendingRotationAsync("zu einem anderen Bild wechselst") Then Return
            LoadPathAt(idx)
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
                    InvalidatePendingBitmapLoad() : CurrentImage = Nothing
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
            CurrentIndex = idx
            BeginInfoPanelSwitch(_currentImagePath)
            ResetViewerRotation()
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
            _colorLabel = LibraryService.Instance.GetColorLabel(_currentImagePath)
            RaiseColorLabelProperties()
            Me.RaisePropertyChanged(NameOf(IsRawFile))
            Me.RaisePropertyChanged(NameOf(IsVideoFile))
            Me.RaisePropertyChanged(NameOf(ShowVideoUnavailableNotice))
            Me.RaisePropertyChanged(NameOf(HasNoMedia))
            Me.RaisePropertyChanged(NameOf(CanEdit))
        End Sub

        Public Async Sub NavigateToItem(item As ImageItem)
            If item Is Nothing Then Return
            Dim idx = _folderPaths.FindIndex(Function(p) String.Equals(p, item.FilePath, StringComparison.OrdinalIgnoreCase))
            If idx >= 0 Then Await CommitNavigateAsync(idx)
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
                                                               InvalidatePendingBitmapLoad() : CurrentImage = Nothing
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
                InvalidatePendingBitmapLoad() : CurrentImage = Nothing
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
                InvalidatePendingBitmapLoad()
                CurrentImage = Nothing
            End If
        End Sub

        Private Sub CopyToClipboard()
            ' Clipboard-Zugriff erfolgt über TopLevel in der View (ViewModel hat keinen UI-Zugriff)
        End Sub

        ''' <summary>Druckt das gerade angezeigte Bild. Der Betrachter arbeitet auf Pfaden - bei
        ''' einem Immich-Asset ist _currentImagePath bereits die lokale Temp-Kopie, es braucht also
        ''' keine gesonderte Auflösung wie in der Galerie.</summary>
        Private Sub PrintCurrent()
            If String.IsNullOrEmpty(_currentImagePath) OrElse Not IO.File.Exists(_currentImagePath) Then Return
            _mainVm?.ShowPrintDialog(New List(Of String) From {_currentImagePath})
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
            _slideshowVideoEndSequence += 1
            If _slideshowTimer IsNot Nothing Then
                _slideshowTimer.Stop()
                RemoveHandler _slideshowTimer.Elapsed, AddressOf OnSlideshowTick
                _slideshowTimer.Dispose()
                _slideshowTimer = Nothing
            End If
        End Sub

        Private Sub OnSlideshowTick(sender As Object, e As ElapsedEventArgs)
            Dispatcher.UIThread.InvokeAsync(Sub()
                If _folderPaths.Count = 0 Then Return
                If IsVideoFile Then
                    If Not IsVideoPlaying Then StartPendingVideoAutoplay()
                    Return
                End If
                NavigateNext()
            End Sub)
        End Sub
    End Class

End Namespace
