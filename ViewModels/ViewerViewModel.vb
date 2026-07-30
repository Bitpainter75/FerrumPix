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

        ' Bewusst die schmale Sicht (siehe IViewerHost) und nicht das MainWindowViewModel: nur so
        ' laesst sich der Betrachter ohne den Anwendungsrumpf bauen und der Bildvergleich messen.
        Private ReadOnly _mainVm As IViewerHost
        Private _currentImagePath As String = ""
        Private _bitmapLoadToken As Integer = 0
        Private _isBitmapLoading As Boolean = False
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
                Me.RaisePropertyChanged(NameOf(CanPinForCompare))
                ' Der Ladehinweis hängt auch vom Dateityp ab. Beim Wechsel darf der alte
                ' Zustand nicht kurz für eine XMP/FPXMP-Begleitdatei weitergelten.
                Me.RaisePropertyChanged(NameOf(ShowBitmapLoading))
                Me.RaisePropertyChanged(NameOf(HasNoMedia))
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
                Me.RaisePropertyChanged(NameOf(IsSingleImageVisible))
                Me.RaisePropertyChanged(NameOf(ShowBitmapLoading))
                If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, value) Then DisposeDeferred(previous)
            End Set
        End Property

        ''' True nur, wenn weder ein Bild noch ein Video geladen ist - steuert den
        ''' "Kein Bild geöffnet"-Leerzustand im Viewer (der bei Videos nicht erscheinen darf,
        ''' obwohl CurrentImage dort bewusst Nothing bleibt).
        Public ReadOnly Property HasNoMedia As Boolean
            Get
                ' Im Vergleich zeigen die beiden Flaechen die Bilder, das versteckte Einzelbild ist
                ' dabei belanglos. Ohne diese Bedingung erschien "Kein Bild geoeffnet" mitten in der
                ' laufenden Vergleichsansicht - ein Platzhalter, der dem widerspricht, was zu sehen
                ' ist.
                If _isCompareMode Then Return False
                Return _currentImage Is Nothing AndAlso Not IsVideoFile AndAlso Not _isBitmapLoading
            End Get
        End Property

        ''' <summary>Zeigt den korrekten Zwischenzustand, solange ein Bild erstmals dekodiert oder
        ''' ein RAW-/FPX-Ergebnis gerendert wird. Ein bereits sichtbares altes Vorschaubild bleibt
        ''' beim Blaettern wie bisher stehen und braucht keinen zusaetzlichen Platzhalter.</summary>
        Public ReadOnly Property ShowBitmapLoading As Boolean
            Get
                Return _isBitmapLoading AndAlso _currentImage Is Nothing AndAlso
                       IsRenderableImagePath(_currentImagePath) AndAlso Not IsVideoFile
            End Get
        End Property

        ''' <summary>Der Render-Ladezustand ist nur bei Formaten sichtbar, deren Anzeige wirklich
        ''' rendern/entwickeln kann (RAW, PSD, FPX), sowie bei Bildern mit einem Sidecar. Normale
        ''' JPG/PNG/GIF-Bilder erscheinen direkt und sollen beim Blättern nicht flackern.</summary>
        Private Shared Function IsRenderableImagePath(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Dim extension = IO.Path.GetExtension(path)
            If String.Equals(extension, ".xmp", StringComparison.OrdinalIgnoreCase) OrElse
               String.Equals(extension, RawSidecarService.Extension, StringComparison.OrdinalIgnoreCase) Then
                Return False
            End If

            If RawPreviewService.IsSupportedRaw(path) OrElse
               PsdPreviewService.IsSupportedPsd(path) OrElse
               HeifDecodeService.IsSupportedHeif(path) OrElse
               TiffPreviewService.IsSupportedTiff(path) OrElse
               FpxService.IsFpx(path) Then
                Return True
            End If

            ' XMP wird in beiden verbreiteten Namensformen gesucht (foto.xmp und foto.jpg.xmp).
            Return XmpSidecarService.FindSidecar(path) IsNot Nothing OrElse
                   RawSidecarService.Exists(path)
        End Function

        Private Sub SetBitmapLoading(value As Boolean)
            If _isBitmapLoading = value Then Return
            _isBitmapLoading = value
            Me.RaisePropertyChanged(NameOf(ShowBitmapLoading))
            Me.RaisePropertyChanged(NameOf(HasNoMedia))
        End Sub

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

        ''' <summary>Die Info-Leiste zeigt hier immer etwas: der Betrachter hat stets ein Bild auf
        ''' der Buehne. Die beiden Eigenschaften gibt es nur, weil sich die Leiste die Galerie teilt,
        ''' wo bei mehreren markierten Bildern oder einem Ordner nichts anzuzeigen ist.</summary>
        Public ReadOnly Property HasInfoContent As Boolean
            Get
                Return True
            End Get
        End Property

        Public ReadOnly Property InfoPlaceholderText As String
            Get
                Return ""
            End Get
        End Property

        ''' <summary>Der Betrachter zeigt immer genau EIN Bild - die Uebersicht ueber mehrere gibt
        ''' es nur in der Galerie.</summary>
        Public ReadOnly Property IsSummary As Boolean
            Get
                Return False
            End Get
        End Property

        Public ReadOnly Property IsSingleImage As Boolean
            Get
                Return True
            End Get
        End Property

        Public ReadOnly Property Name As String
            Get
                Return If(_exifInfo?.FileName, "")
            End Get
        End Property

        ''' <summary>Bleibt leer: die Uebersicht ueber mehrere Bilder gibt es nur in der Galerie.
        ''' Die Info-Leiste ist dieselbe, also muss die Eigenschaft da sein.</summary>
        Public ReadOnly Property SummaryFacts As New ObservableCollection(Of ExifTag)()

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
                ' RaiseAndSetIfChanged liefert den WERT, nicht "hat sich geaendert" - als Bedingung
                ' gelesen war der Zweig bei 0 Grad (=False) tot und warf bei anderen Typen sogar
                ' (siehe Bildgroessen-Dialog). Deshalb explizit vergleichen.
                Dim geaendert = _rotationAngle <> normalized
                Me.RaiseAndSetIfChanged(_rotationAngle, normalized)
                If geaendert AndAlso
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
                    ' Auch das Sitzungs-Item mitschreiben (wie der ColorLabel-Setter), sonst liest
                    ' LoadImmichAt beim Zurücknavigieren den alten Wert und die Bewertung springt zurück.
                    If _currentIndex >= 0 AndAlso _currentIndex < _immichSessionItems.Count Then
                        _immichSessionItems(_currentIndex).Rating = value
                    End If
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
                    ' Auch das Sitzungs-Item mitschreiben (wie der ColorLabel-Setter), sonst springt der
                    ' Favorit beim Zurücknavigieren zurück, weil LoadImmichAt aus dem Meta neu liest.
                    If _currentIndex >= 0 AndAlso _currentIndex < _immichSessionItems.Count Then
                        _immichSessionItems(_currentIndex).IsFavorite = value
                    End If
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
                    LibraryService.Instance.SetColorLabelForMany({_currentImagePath}, normalized, syncToXmp:=True)
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
            Me.RaisePropertyChanged(NameOf(IsColorLabelYellow))
            Me.RaisePropertyChanged(NameOf(HasColorLabel))
            _mainVm?.RefreshWindowTitle()
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
        ''' Gelb kam mit dem XMP-Sidecar-Import dazu (xmp:Label="Yellow").
        Public ReadOnly Property IsColorLabelYellow As Boolean
            Get
                Return IsColorLabelValue("#FACC15")
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
        Public ReadOnly Property OpenTagSearchCommand As ICommand
        Public ReadOnly Property RotateLeftCommand As ICommand
        Public ReadOnly Property RotateRightCommand As ICommand
        Public ReadOnly Property FlipHorizontalCommand As ICommand
        Public ReadOnly Property BackToGalleryCommand As ICommand
        Public ReadOnly Property DeleteCurrentCommand As ICommand
        Public ReadOnly Property ResizeCurrentCommand As ICommand
        Public ReadOnly Property ToggleFullscreenCommand As ICommand
        Public ReadOnly Property TogglePinCommand As ICommand
        Public ReadOnly Property ApplyWatermarkCurrentCommand As ICommand

        ''' <summary>Wo das Kontextmenue zuletzt geoeffnet wurde und welche Elemente es meint.
        ''' Beides setzt die View, bevor sie oeffnet.</summary>
        Public Property ContextSite As MenuSite = MenuSite.ViewerFooter
        Public Property ContextItems As IList(Of ImageItem) = New List(Of ImageItem)()

        ''' <summary>Die Eintraege des Kontextmenues. Wird bei jedem Oeffnen neu gelesen, damit
        ''' Aufrufort, Auswahl und Sprache ankommen - der Aufbau ist billig.</summary>
        Public ReadOnly Property ContextActions As IReadOnlyList(Of Object)
            Get
                Return ContextMenuBuilder.Build(ContextSite, ContextItems,
                                                isVirtual:=False, canPaste:=False,
                                                commands:=New MenuCommands With {
                                                    .NewImage = NewDocumentCommand,
                                                    .Fullscreen = ToggleFullscreenCommand,
                                                    .Adjust = EditCommand,
                                                    .PinImage = TogglePinCommand,
                                                    .Rename = RenameCurrentCommand,
                                                    .ResizeImage = ResizeCurrentCommand,
                                                    .ApplyWatermark = ApplyWatermarkCurrentCommand,
                                                    .ApplyFilter = ApplyFilterCurrentCommand,
                                                    .ConvertTo = ConvertCurrentCommand,
                                                    .ExportTo = ExportCurrentCommand,
                                                    .Print = PrintCommand,
                                                    .RotateLeft = RotateLeftCommand,
                                                    .RotateRight = RotateRightCommand,
                                                    .Favorite = ToggleFavoriteCommand,
                                                    .Rating = SetRatingCommand,
                                                    .ColorLabel = SetColorLabelCommand,
                                                    .CopyPath = CopyPathCommand,
                                                    .ShowInFileManager = OpenFileManagerCommand,
                                                    .Delete = DeleteCurrentCommand})
            End Get
        End Property

        Public Sub RefreshContextActions()
            Me.RaisePropertyChanged(NameOf(ContextActions))
        End Sub
        Public ReadOnly Property NewDocumentCommand As ICommand
        Public ReadOnly Property ConvertCurrentCommand As ICommand
        Public ReadOnly Property ExportCurrentCommand As ICommand
        Public ReadOnly Property ApplyFilterCurrentCommand As ICommand
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

        Public Sub New(mainVm As IViewerHost)
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
                                                       ' Die Schreibweise bleibt, wie sie getippt wurde: aus einer
                                                       ' Beistelldatei kommen Stichwoerter ebenfalls mit Grossbuchstaben,
                                                       ' und Immich behaelt sie auch. Verglichen wird dafuer ohne
                                                       ' Ruecksicht darauf - sonst stuende "Berlin" zweimal da.
                                                       Dim tag = NewTagText.Trim()
                                                       If String.IsNullOrEmpty(tag) OrElse
                                                          Tags.Any(Function(vorhanden) String.Equals(vorhanden, tag, StringComparison.OrdinalIgnoreCase)) Then Return
                                                       Tags.Add(tag)
                                                       NewTagText = ""
                                                       If _isImmichSession AndAlso Not String.IsNullOrEmpty(_currentImmichAssetId) Then
                                                           Dim ignored = ImmichService.AddTagToAssetAsync(_currentImmichAssetId, tag)
                                                       ElseIf Not String.IsNullOrEmpty(_currentImagePath) Then
                                                           LibraryService.Instance.SetTags(_currentImagePath, Tags, syncToXmp:=True)
                                                       End If
                                                       RefreshTagSuggestions()
                                                   End Sub)
            RemoveTagCommand = ReactiveCommand.Create(Of String)(Sub(tag)
                                                                     If Not Tags.Remove(tag) Then Return
                                                                     If _isImmichSession AndAlso Not String.IsNullOrEmpty(_currentImmichAssetId) Then
                                                                         Dim ignored = ImmichService.RemoveTagFromAssetAsync(_currentImmichAssetId, tag)
                                                                     ElseIf Not String.IsNullOrEmpty(_currentImagePath) Then
                                                                         LibraryService.Instance.SetTags(_currentImagePath, Tags, syncToXmp:=True)
                                                                     End If
                                                                 End Sub)
            OpenTagSearchCommand = ReactiveCommand.Create(Of String)(Sub(tag) _mainVm?.OpenTagSearchInGallery(tag))
            RotateLeftCommand = ReactiveCommand.Create(Sub() RotationAngle = RotationAngle - 90)
            RotateRightCommand = ReactiveCommand.Create(Sub() RotationAngle = RotationAngle + 90)
            FlipHorizontalCommand = ReactiveCommand.Create(Sub() ScaleX = ScaleX * -1)
            BackToGalleryCommand = ReactiveCommand.Create(Sub() _mainVm.BackToGallery(_currentImagePath))
            DeleteCurrentCommand = ReactiveCommand.Create(Sub() DeleteCurrent())
            ResizeCurrentCommand = ReactiveCommand.Create(Sub() ResizeCurrent())
            ToggleFullscreenCommand = ReactiveCommand.Create(Sub() _mainVm?.ToggleFullscreen())
            TogglePinCommand = ReactiveCommand.Create(Sub() TogglePin())
            ApplyWatermarkCurrentCommand = ReactiveCommand.Create(Sub() WithCurrentImage(Sub(g, i) g.ApplyWatermarkToImageItems(i)))
            NewDocumentCommand = ReactiveCommand.Create(Sub() _mainVm?.ShowNewDocumentDialog())
            ConvertCurrentCommand = ReactiveCommand.Create(Sub() WithCurrentImage(Sub(g, i) g.ConvertImageItems(i)))
            ExportCurrentCommand = ReactiveCommand.Create(Sub() WithCurrentImage(Sub(g, i) g.ExportImageItems(i)))
            ApplyFilterCurrentCommand = ReactiveCommand.Create(Sub() WithCurrentImage(Sub(g, i) g.ApplyFilterToImageItems(i)))
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
            Try
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
                ' Bildes da (Filmstrip-Wechsel). Der volle EXIF-Stand kommt
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
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("ViewerViewModel.LoadImmichAt", ex)
            End Try
        End Sub

        ' ── Anheften: erst ein Bild festhalten, das naechste kommt daneben ───────

        Private _pinnedPath As String = ""

        ''' <summary>Ist ein Bild angeheftet? Solange das gilt, oeffnet das NAECHSTE angesteuerte Bild
        ''' den Vergleich - angeheftet links, neu rechts. Der bequeme Weg neben dem Kontextmenue der
        ''' Galerie: man sieht ein Bild, haelt es fest und blaettert weiter.</summary>
        Public ReadOnly Property IsImagePinned As Boolean
            Get
                Return Not String.IsNullOrEmpty(_pinnedPath)
            End Get
        End Property

        ''' <summary>Anheften geht nur bei ECHTEN Dateien. Immich-Elemente sind Pseudo-Pfade und
        ''' laden ueber einen eigenen Weg - der Vergleich kann sie (noch) nicht, also darf der Knopf
        ''' auch nicht so tun. Ein Klick, der nichts tut, ist schlimmer als ein grauer Knopf.</summary>
        Public ReadOnly Property CanPinForCompare As Boolean
            Get
                ' Nicht nur das Sitzungsmerkmal pruefen: in einer GEMISCHTEN Suchliste (lokale und
                ' Immich-Elemente nebeneinander) steht es auf lokal, waehrend gerade ein
                ' Immich-Element angezeigt wird. Die Existenz der Datei ist das ehrliche Kriterium -
                ' der Vergleich laedt genau darueber.
                If _isImmichSession Then Return False
                If String.IsNullOrEmpty(_currentImagePath) Then Return False
                Return File.Exists(_currentImagePath)
            End Get
        End Property

        Public ReadOnly Property PinnedFileName As String
            Get
                Return IO.Path.GetFileName(If(_pinnedPath, ""))
            End Get
        End Property

        ''' <summary>Anheften ein- und ausschalten. Ausschalten beendet einen laufenden Vergleich -
        ''' sonst bliebe eine Buehne stehen, deren Bezugsbild niemand mehr festhaelt.</summary>
        Public Sub TogglePin()
            If IsImagePinned Then
                ' ExitCompare loest die Heftung mit; laeuft ausnahmsweise kein Vergleich (etwa nach
                ' einem Fehlschlag beim Laden), bleibt sie sonst haengen - deshalb beides.
                _pinnedPath = ""
                ExitCompare()
            Else
                Dim path = If(_isCompareMode, _compareLeftPath, _currentImagePath)
                If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return
                ' Sofort in die geteilte Ansicht, zunaechst mit demselben Bild auf beiden Seiten:
                ' das Anheften wird damit unmittelbar sichtbar, statt erst beim naechsten Blaettern.
                ActivateCompare(path, path)
                Return
            End If
            Me.RaisePropertyChanged(NameOf(IsImagePinned))
            Me.RaisePropertyChanged(NameOf(PinnedFileName))
        End Sub

        ' ── Vergleichsmodus ──────────────────────────────────────────────────────

        Private _isCompareMode As Boolean
        Private _compareLeftImage As Bitmap
        Private _compareRightImage As Bitmap
        Private _compareLeftPath As String = ""
        Private _compareRightPath As String = ""
        Private _focusedComparePane As Integer

        ''' <summary>Zwei Bilder nebeneinander mit GETEILTEM Zoom und gespiegeltem Ausschnitt.
        ''' Die beiden Flaechen haengen an eigenen Eigenschaften (links/rechts) und NICHT an
        ''' CurrentImage: sonst wuerde ein Fokuswechsel die Bilder vertauschen, statt nur die Daten
        ''' im Infopanel umzuschalten. Der Filmstreifen bleibt bedienbar: ein Klick darauf setzt die
        ''' RECHTE Flaeche, so haelt man links eine Referenz fest und blaettert rechts durch die
        ''' Kandidaten.</summary>
        Public Property IsCompareMode As Boolean
            Get
                Return _isCompareMode
            End Get
            Private Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isCompareMode, value)
                Me.RaisePropertyChanged(NameOf(IsSingleImageMode))
                Me.RaisePropertyChanged(NameOf(IsSingleImageVisible))
            End Set
        End Property

        ''' <summary>True im normalen Einzelbild-Betrieb - die vorhandene, einzelne Bildflaeche
        ''' haengt daran.</summary>
        Public ReadOnly Property IsSingleImageMode As Boolean
            Get
                Return Not _isCompareMode
            End Get
        End Property

        ''' <summary>Sichtbarkeit der EINZELNEN Bildflaeche: nur ohne Vergleich und nur mit Bild.
        ''' Als eigene Eigenschaft, weil eine Bindung zwei Bedingungen nicht ohne Umweg verknuepft.</summary>
        Public ReadOnly Property IsSingleImageVisible As Boolean
            Get
                Return Not _isCompareMode AndAlso _currentImage IsNot Nothing
            End Get
        End Property

        Public Property CompareLeftImage As Bitmap
            Get
                Return _compareLeftImage
            End Get
            Private Set(value As Bitmap)
                Dim alt = _compareLeftImage
                Me.RaiseAndSetIfChanged(_compareLeftImage, value)
                If alt IsNot Nothing AndAlso Not ReferenceEquals(alt, value) Then alt.Dispose()
            End Set
        End Property

        Public Property CompareRightImage As Bitmap
            Get
                Return _compareRightImage
            End Get
            Private Set(value As Bitmap)
                Dim alt = _compareRightImage
                Me.RaiseAndSetIfChanged(_compareRightImage, value)
                If alt IsNot Nothing AndAlso Not ReferenceEquals(alt, value) Then alt.Dispose()
            End Set
        End Property

        Public ReadOnly Property CompareLeftFileName As String
            Get
                Return IO.Path.GetFileName(If(_compareLeftPath, ""))
            End Get
        End Property

        Public ReadOnly Property CompareRightFileName As String
            Get
                Return IO.Path.GetFileName(If(_compareRightPath, ""))
            End Get
        End Property

        Private _isCompareViewportLinked As Boolean = True

        ''' <summary>Gekoppelt (Vorgabe) spiegelt die eine Flaeche ihren Ausschnitt in die andere -
        ''' dafuer ist der Vergleich bei zwei Aufnahmen DERSELBEN Szene da. Entkoppelt scrollt jede
        ''' fuer sich, was bei unterschiedlich gerahmten Aufnahmen der einzig brauchbare Weg ist.
        '''
        ''' Bewusst ein EIGENER Schalter und keine dritte Stufe am Anheften: das Anheften beantwortet
        ''' "welches Bild halte ich fest", der hier "wie bewegen sich die Flaechen". Zwei Fragen an
        ''' einem Knopf waeren nicht ablesbar.
        '''
        ''' Der ZOOM bleibt in beiden Faellen gemeinsam - sonst vergliche man zwei Vergroesserungen
        ''' statt zweier Bilder.</summary>
        Public Property IsCompareViewportLinked As Boolean
            Get
                Return _isCompareViewportLinked
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isCompareViewportLinked, value)
            End Set
        End Property

        ''' <summary>Welche Flaeche den Fokus hat (0 = links, 1 = rechts). Der Rahmen haengt daran,
        ''' und das Infopanel zeigt die Daten GENAU dieser Flaeche.</summary>
        Public Property FocusedComparePane As Integer
            Get
                Return _focusedComparePane
            End Get
            Set(value As Integer)
                Dim v = If(value = 1, 1, 0)
                If _focusedComparePane = v Then Return
                Me.RaiseAndSetIfChanged(_focusedComparePane, v)
                Me.RaisePropertyChanged(NameOf(IsCompareLeftFocused))
                Me.RaisePropertyChanged(NameOf(IsCompareRightFocused))
                ShowDataOfFocusedPane()
            End Set
        End Property

        Public ReadOnly Property IsCompareLeftFocused As Boolean
            Get
                Return _focusedComparePane = 0
            End Get
        End Property

        Public ReadOnly Property IsCompareRightFocused As Boolean
            Get
                Return _focusedComparePane = 1
            End Get
        End Property

        ''' <summary>Infopanel, Dateiname und Pfad auf die fokussierte Flaeche umstellen - ohne die
        ''' Flaechen selbst anzufassen. BeginInfoPanelSwitch ist dafuer die einzige Stelle; wer hier
        ''' stattdessen das Bild neu laedt, vertauscht die Flaechen.</summary>
        Private Sub ShowDataOfFocusedPane()
            Dim path = If(_focusedComparePane = 1, _compareRightPath, _compareLeftPath)
            If String.IsNullOrEmpty(path) Then Return
            _currentImagePath = path
            CurrentImagePath = path
            CurrentFileName = IO.Path.GetFileName(path)
            BeginInfoPanelSwitch(path)
            ' Der Dateiname allein reicht nicht: Sterne, Herz und Farbetikett in der Fusszeile
            ' gehoeren zum SELBEN Bild wie der Name daneben. Ohne das zeigte die Fusszeile die
            ' Bewertung der zuletzt in der Einzelansicht geoeffneten Datei, waehrend links der
            ' Name der fokussierten Flaeche stand - und ein Klick auf einen Stern haette sie dem
            ' falschen Bild gegeben.
            UebernehmeKatalogAttribute(path)
            LoadInfoPanelData(path)
            Me.RaisePropertyChanged(NameOf(CanEdit))
        End Sub

        ''' <summary>Bewertung, Favorit und Farbetikett des Bildes aus dem Katalog in die
        ''' Fusszeilen-Felder uebernehmen. Die EINE Stelle dafuer: sie lag vorher zweimal wortgleich
        ''' im Oeffnen- und im Blaettern-Weg, und der Vergleich hatte sie gar nicht.</summary>
        Private Sub UebernehmeKatalogAttribute(imagePath As String)
            _isFavorite = LibraryService.Instance.GetFavorite(imagePath)
            Me.RaisePropertyChanged(NameOf(IsFavorite))
            _rating = LibraryService.Instance.GetRating(imagePath)
            Me.RaisePropertyChanged(NameOf(Rating))
            Me.RaisePropertyChanged(NameOf(RatingText))
            _colorLabel = LibraryService.Instance.GetColorLabel(imagePath)
            RaiseColorLabelProperties()
        End Sub

        ''' <summary>Doppelklick auf eine Vergleichsflaeche: erst den Fokus dorthin, dann das Bild
        ''' DIESER Flaeche im Editor oeffnen - genauso, wie ein Doppelklick in der Einzelansicht das
        ''' angezeigte Bild oeffnet. Der Fokuswechsel zieht Pfad und Fusszeile mit, deshalb trifft
        ''' der Editor danach das richtige Bild.</summary>
        Public Sub OpenComparePaneInEditor(pane As Integer)
            If Not _isCompareMode Then Return
            FocusedComparePane = pane
            Dim path = If(_focusedComparePane = 1, _compareRightPath, _compareLeftPath)
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return
            If Not CanEdit Then Return
            EditCommand.Execute(Nothing)
        End Sub

        ''' <summary>Zwei Bilder zum Vergleich oeffnen. Das LINKE gilt als das aktuelle - Ordner-
        ''' kontext und Infopanel bauen darauf auf wie beim normalen Oeffnen.</summary>
        Public Sub OpenCompare(leftPath As String, rightPath As String,
                               Optional allPaths As List(Of String) = Nothing,
                               Optional cacheScopeId As String = Nothing,
                               Optional cacheScopeName As String = Nothing)
            If String.IsNullOrWhiteSpace(leftPath) OrElse String.IsNullOrWhiteSpace(rightPath) Then Return
            If Not File.Exists(leftPath) OrElse Not File.Exists(rightPath) Then Return

            OpenImage(leftPath, allPaths, cacheScopeId, cacheScopeName)
            ActivateCompare(leftPath, rightPath)
        End Sub

        ''' <summary>Den Vergleich einschalten, ohne das Bild neu zu oeffnen. Das linke Bild gilt dabei
        ''' als angeheftet - egal ob der Vergleich ueber den Knopf oder ueber die Galerie begann. So
        ''' ist der Knopf der EINE sichtbare Zustand fuer "Vergleich laeuft", und Loesen fuehrt in
        ''' beiden Faellen zurueck zur Einzelansicht.</summary>
        Private Sub ActivateCompare(leftPath As String, rightPath As String)
            _compareLeftPath = leftPath
            _compareRightPath = rightPath
            _focusedComparePane = 0
            _pinnedPath = leftPath
            IsCompareMode = True
            LoadCompareMarkers()
            For Each n In {NameOf(CompareLeftFileName), NameOf(CompareRightFileName),
                           NameOf(IsCompareLeftFocused), NameOf(IsCompareRightFocused),
                           NameOf(IsImagePinned), NameOf(PinnedFileName),
                           NameOf(CanDeleteCurrent), NameOf(CanSwapComparePanes),
                           NameOf(HasNoMedia)}
                Me.RaisePropertyChanged(n)
            Next
            LoadCompareImages()
        End Sub

        ' ── Marken auf den Vergleichsflaechen ───────────────────────────────────
        '
        ' Wie auf der Galerie-Kachel: Sterne, Herz, Anpassen und Loeschen liegen AUF dem Bild, zu
        ' dem sie gehoeren. Das ist auch der Grund, warum sie hier ueberhaupt sind - der Knopf in
        ' der Werkzeugleiste kann im Vergleich nicht sagen, welches der beiden Bilder er meint, und
        ' bei einer nicht umkehrbaren Aktion ist das zu wenig.

        Private _compareLeftRating As Integer
        Private _compareRightRating As Integer
        Private _compareLeftFavorite As Boolean
        Private _compareRightFavorite As Boolean

        Public ReadOnly Property CompareLeftRating As Integer
            Get
                Return _compareLeftRating
            End Get
        End Property

        Public ReadOnly Property CompareRightRating As Integer
            Get
                Return _compareRightRating
            End Get
        End Property

        Public ReadOnly Property CompareLeftIsFavorite As Boolean
            Get
                Return _compareLeftFavorite
            End Get
        End Property

        Public ReadOnly Property CompareRightIsFavorite As Boolean
            Get
                Return _compareRightFavorite
            End Get
        End Property

        ''' <summary>Bewertung und Favorit beider Flaechen aus dem Katalog nachziehen. Jede Stelle,
        ''' die einen der beiden Pfade aendert, ruft das - sonst zeigen die Marken die Werte des
        ''' vorherigen Bildes.</summary>
        Private Sub LoadCompareMarkers()
            _compareLeftRating = If(String.IsNullOrEmpty(_compareLeftPath), 0, LibraryService.Instance.GetRating(_compareLeftPath))
            _compareRightRating = If(String.IsNullOrEmpty(_compareRightPath), 0, LibraryService.Instance.GetRating(_compareRightPath))
            _compareLeftFavorite = Not String.IsNullOrEmpty(_compareLeftPath) AndAlso LibraryService.Instance.GetFavorite(_compareLeftPath)
            _compareRightFavorite = Not String.IsNullOrEmpty(_compareRightPath) AndAlso LibraryService.Instance.GetFavorite(_compareRightPath)
            For Each n In {NameOf(CompareLeftRating), NameOf(CompareRightRating),
                           NameOf(CompareLeftIsFavorite), NameOf(CompareRightIsFavorite)}
                Me.RaisePropertyChanged(n)
            Next
        End Sub

        Private Function PathOfPane(pane As Integer) As String
            Return If(pane = 1, _compareRightPath, _compareLeftPath)
        End Function

        ''' <summary>Sterne setzen. Derselbe Stern nochmal loescht die Bewertung - genauso wie auf
        ''' der Galerie-Kachel.</summary>
        Public Sub SetCompareRating(pane As Integer, sterne As Integer)
            Dim path = PathOfPane(pane)
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return
            Dim bisher = If(pane = 1, _compareRightRating, _compareLeftRating)
            Dim newValue = If(bisher = sterne, 0, Math.Max(0, Math.Min(5, sterne)))
            LibraryService.Instance.SetRating(path, newValue)
            LoadCompareMarkers()
            ' Die Fusszeile zeigt die fokussierte Flaeche - sie muss mit, wenn genau die gemeint war.
            If pane = _focusedComparePane Then UebernehmeKatalogAttribute(path)
        End Sub

        Public Sub ToggleCompareFavorite(pane As Integer)
            Dim path = PathOfPane(pane)
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return
            Dim bisher = If(pane = 1, _compareRightFavorite, _compareLeftFavorite)
            LibraryService.Instance.SetFavorite(path, Not bisher)
            LoadCompareMarkers()
            If pane = _focusedComparePane Then UebernehmeKatalogAttribute(path)
        End Sub

        ''' <summary>Eine Flaeche loeschen. Danach rueckt nach: bei der RECHTEN kommt das naechste
        ''' Bild dorthin, bei der LINKEN wandert die rechte nach links (samt ihrer bereits geladenen
        ''' Bitmap) und rechts kommt das naechste. So bleibt immer ein Bezugsbild stehen und eine
        ''' Serie laesst sich in einem Zug durchsehen.</summary>
        Public Sub DeleteComparePane(pane As Integer)
            If Not _isCompareMode Then Return
            Dim path = PathOfPane(pane)
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return
            _mainVm.RequestDeletePaths({path}, Sub() AfterDeleteInCompare(pane, path))
        End Sub

        Private Sub AfterDeleteInCompare(pane As Integer, geloescht As String)
            ' Die Stelle in der Liste MERKEN, bevor der Pfad herausfaellt - "das naechste Bild" ist
            ' genau das, was danach an dieser Stelle steht.
            Dim stelle = _folderPaths.FindIndex(Function(p) String.Equals(p, geloescht, StringComparison.OrdinalIgnoreCase))
            _folderPaths.RemoveAll(Function(p) String.Equals(p, geloescht, StringComparison.OrdinalIgnoreCase))
            LoadFilmstrip()

            ' Beide Flaechen zeigten dasselbe Bild (direkt nach dem Anheften) - dann ist nach dem
            ' Loeschen nichts mehr zu vergleichen.
            Dim beideGleich = String.Equals(_compareLeftPath, _compareRightPath, StringComparison.OrdinalIgnoreCase)

            If _folderPaths.Count = 0 Then
                ExitCompare(zurueckZumFokus:=False)
                InvalidatePendingBitmapLoad()
                CurrentImage = Nothing
                CurrentImagePath = ""
                CurrentFileName = ""
                _mainVm.BackToGallery(IO.Path.GetDirectoryName(geloescht))
                Return
            End If

            If beideGleich Then
                ' Angeheftet, aber noch nicht weitergeblaettert: beide Flaechen zeigten dieselbe
                ' Datei. Mit ihr faellt das Bezugsbild weg, also gibt es nichts mehr zu vergleichen.
                ' Frueher blieb der geloeschte Pfad links stehen und die Flaeche zeigte eine Datei,
                ' die es nicht mehr gibt.
                Dim weiter = NextPathFrom(stelle, "")
                ExitCompare(zurueckZumFokus:=False)
                Dim wIdx = _folderPaths.FindIndex(Function(p) String.Equals(p, weiter, StringComparison.OrdinalIgnoreCase))
                If wIdx >= 0 Then LoadPathAt(wIdx)
                Return
            End If

            If pane = 0 Then
                ' Die rechte Bitmap wandert MIT nach links, sie ist bereits geladen - ein zweiter
                ' Decode fuer ein Bild, das schon da ist, waere bei RAW Sekunden.
                _compareLeftPath = _compareRightPath
                Dim wegwerfen = CompareLeftImage
                SetCompareBitmaps(CompareRightImage, Nothing)
                ReleaseCompareBitmap(wegwerfen)
                _pinnedPath = _compareLeftPath
                _vergleichLadeZaehler += 1
            End If

            ' Das Bild, das jetzt LINKS steht, darf nicht als "das naechste" rechts landen - beim
            ' Loeschen der linken Flaeche ist die rechte gerade dorthin gewandert und stuende sonst
            ' auf beiden Seiten.
            Dim naechster = NextPathFrom(stelle, _compareLeftPath)
            If String.IsNullOrEmpty(naechster) Then
                ' Nur noch EIN Bild uebrig - ein Vergleich mit sich selbst hat keinen Sinn.
                Dim bleibt = If(String.IsNullOrEmpty(_compareLeftPath), naechster, _compareLeftPath)
                ExitCompare(zurueckZumFokus:=False)
                Dim idx = _folderPaths.FindIndex(Function(p) String.Equals(p, bleibt, StringComparison.OrdinalIgnoreCase))
                If idx >= 0 Then LoadPathAt(idx)
                Return
            End If

            _compareRightPath = naechster
            _currentIndex = _folderPaths.FindIndex(Function(p) String.Equals(p, naechster, StringComparison.OrdinalIgnoreCase))
            _focusedComparePane = 0
            LoadCompareImages(nurRechts:=True)
            LoadCompareMarkers()
            ShowDataOfFocusedPane()
            For Each n In {NameOf(CompareLeftFileName), NameOf(CompareRightFileName),
                           NameOf(IsCompareLeftFocused), NameOf(IsCompareRightFocused),
                           NameOf(IsImagePinned), NameOf(PinnedFileName),
                           NameOf(CanSwapComparePanes), NameOf(PositionText),
                           NameOf(CurrentFilmstripIndex)}
                Me.RaisePropertyChanged(n)
            Next
            MarkCurrentFilmstripItem()
        End Sub

        ''' <summary>Das Bild, das nach dem Loeschen an dieser Stelle steht. Ist die Liste dort zu
        ''' Ende, wird vorne weitergesucht - sonst endete eine Serie am letzten Bild in einer leeren
        ''' Flaeche.</summary>
        Private Function NextPathFrom(stelle As Integer, ausser As String) As String
            If _folderPaths.Count = 0 Then Return ""
            Dim start = Math.Max(0, Math.Min(stelle, _folderPaths.Count - 1))
            For i = 0 To _folderPaths.Count - 1
                Dim k = (start + i) Mod _folderPaths.Count
                If Not String.Equals(_folderPaths(k), ausser, StringComparison.OrdinalIgnoreCase) Then Return _folderPaths(k)
            Next
            Return ""
        End Function

        ''' <summary>Beide Vergleichs-Bitmaps setzen, OHNE eines freizugeben.
        '''
        ''' Die Setter geben das abgeloeste Bild frei - richtig, solange ein Bild wirklich ersetzt
        ''' wird. Beim Tauschen und beim Nachruecken wandert es dagegen nur auf die ANDERE SEITE,
        ''' und dann zieht der Setter ihm den Boden weg: der erste Zuweisung gibt das Bitmap frei,
        ''' das die zweite gerade hinlegen will. Die Anzeige liest danach dessen Groesse und der
        ''' Prozess faellt. Deshalb hier direkt auf die Felder und die Meldung von Hand - und wer
        ''' wirklich etwas wegwerfen will, tut das ausdruecklich ueber
        ''' <see cref="GibVergleichsBitmapFrei"/>.</summary>
        Private Sub SetCompareBitmaps(left As Bitmap, right As Bitmap)
            _compareLeftImage = left
            _compareRightImage = right
            Me.RaisePropertyChanged(NameOf(CompareLeftImage))
            Me.RaisePropertyChanged(NameOf(CompareRightImage))
        End Sub

        ''' <summary>Ein abgelegtes Vergleichsbild freigeben - erst NACH der Benachrichtigung, damit
        ''' kein laufender Layoutdurchlauf mehr darauf zugreift (dieselbe Regel wie bei den
        ''' Vorschaubildern).</summary>
        Private Shared Sub ReleaseCompareBitmap(bmp As Bitmap)
            If bmp Is Nothing Then Return
            Dispatcher.UIThread.Post(Sub() bmp.Dispose(), DispatcherPriority.Background)
        End Sub

        ''' <summary>Tauschbar, sobald zwei VERSCHIEDENE Bilder anliegen. Direkt nach dem Anheften
        ''' steht auf beiden Seiten dasselbe; ein Tausch waere dort ein Knopf, der nichts tut.</summary>
        Public ReadOnly Property CanSwapComparePanes As Boolean
            Get
                Return _isCompareMode AndAlso
                       Not String.IsNullOrEmpty(_compareLeftPath) AndAlso
                       Not String.IsNullOrEmpty(_compareRightPath) AndAlso
                       Not String.Equals(_compareLeftPath, _compareRightPath, StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' <summary>Seiten tauschen: das rechte Bild wird das neue angeheftete und wandert nach
        ''' links, das bisher angeheftete nach rechts. Der Weg, wenn man beim Blaettern ein besseres
        ''' Bezugsbild findet.
        '''
        ''' Die Bitmaps wandern MIT, es wird nichts neu geladen - bei zwei entwickelten RAWs waeren
        ''' das sonst zwei volle Decodes fuer einen Tausch. Der Ladezaehler muss trotzdem hoch:
        ''' laeuft gerade ein Ladevorgang, faellt sein Ergebnis sonst in die inzwischen getauschte
        ''' Flaeche.</summary>
        Public Sub SwapComparePanes()
            If Not CanSwapComparePanes Then Return
            Dim alterLinker = _compareLeftPath
            _compareLeftPath = _compareRightPath
            _compareRightPath = alterLinker

            SetCompareBitmaps(CompareRightImage, CompareLeftImage)

            _vergleichLadeZaehler += 1
            _pinnedPath = _compareLeftPath
            _focusedComparePane = 0
            LoadCompareMarkers()

            ' Das Weiterblaettern haengt am Index der RECHTEN Flaeche - er muss dem getauschten
            ' Bild folgen, sonst springt der naechste Tastendruck an eine fremde Stelle im Ordner.
            Dim idx = _folderPaths.FindIndex(Function(p) String.Equals(p, _compareRightPath, StringComparison.OrdinalIgnoreCase))
            If idx >= 0 Then _currentIndex = idx

            ShowDataOfFocusedPane()
            For Each n In {NameOf(CompareLeftFileName), NameOf(CompareRightFileName),
                           NameOf(IsCompareLeftFocused), NameOf(IsCompareRightFocused),
                           NameOf(IsImagePinned), NameOf(PinnedFileName),
                           NameOf(CanSwapComparePanes), NameOf(PositionText),
                           NameOf(CurrentFilmstripIndex)}
                Me.RaisePropertyChanged(n)
            Next
            MarkCurrentFilmstripItem()
        End Sub

        ''' <summary>Die rechte Flaeche auf ein anderes Bild setzen - der Weg des Filmstreifens.
        ''' Die linke bleibt stehen, damit sie als Bezug taugt.</summary>
        Public Sub SetCompareRight(path As String)
            If Not _isCompareMode OrElse String.IsNullOrWhiteSpace(path) Then Return
            If Not File.Exists(path) Then Return
            If String.Equals(path, _compareRightPath, StringComparison.OrdinalIgnoreCase) Then Return
            _compareRightPath = path
            Me.RaisePropertyChanged(NameOf(CompareRightFileName))
            Me.RaisePropertyChanged(NameOf(CanSwapComparePanes))
            LoadCompareMarkers()
            LoadCompareImages(nurRechts:=True)
            ' Steht der Fokus rechts, muss auch das Infopanel mit.
            If _focusedComparePane = 1 Then ShowDataOfFocusedPane()
        End Sub

        ''' <summary>Vergleich beenden. Standardmaessig springt die Einzelansicht auf das Bild, das
        ''' gerade den Fokus hatte - man schaute es an, also soll es stehen bleiben. Beim normalen
        ''' Oeffnen eines anderen Bildes entfaellt das, sonst laedt der Betrachter zwei nacheinander.</summary>
        Public Sub ExitCompare(Optional zurueckZumFokus As Boolean = True)
            If Not _isCompareMode Then Return
            Dim focusPath = If(_focusedComparePane = 1, _compareRightPath, _compareLeftPath)
            IsCompareMode = False
            CompareLeftImage = Nothing
            CompareRightImage = Nothing
            _compareRightPath = ""
            _focusedComparePane = 0
            ' Die Heftung MUSS hier mit fallen: sie ist derselbe Zustand wie der Vergleich, nur
            ' sichtbar gemacht. Blieb sie stehen, sprang der naechste Bildwechsel unvermittelt
            ' zurueck in den Vergleich - und zwar mit dem Ordner des alten angehefteten Bildes,
            ' weil dessen Neuoeffnen den Filmstreifen mitzieht.
            _pinnedPath = ""
            For Each n In {NameOf(IsCompareLeftFocused), NameOf(IsCompareRightFocused),
                           NameOf(IsImagePinned), NameOf(PinnedFileName),
                           NameOf(CanDeleteCurrent), NameOf(CanSwapComparePanes),
                           NameOf(HasNoMedia)}
                Me.RaisePropertyChanged(n)
            Next
            If Not zurueckZumFokus OrElse String.IsNullOrEmpty(focusPath) Then Return
            Dim idx = _folderPaths.FindIndex(Function(pf) String.Equals(pf, focusPath, StringComparison.OrdinalIgnoreCase))
            If idx >= 0 Then LoadPathAt(idx)
        End Sub

        ''' <summary>Zaehler gegen ueberholende Ladevorgaenge: blaettert man schnell, laufen mehrere
        ''' Decodes gleichzeitig, und ein langsamer frueherer wuerde sonst das neuere Bild
        ''' ueberschreiben.</summary>
        Private _vergleichLadeZaehler As Integer

        ''' <summary>Die Vergleichsbilder laden. <paramref name="nurRechts"/> beim Weiterblaettern:
        ''' das linke ist die festgehaltene Referenz und aendert sich dabei NICHT - es mitzuladen
        ''' hiess, bei jedem Tastendruck ein RAW ein zweites Mal zu entwickeln.</summary>
        Private Async Sub LoadCompareImages(Optional nurRechts As Boolean = False)
            _vergleichLadeZaehler += 1
            Dim token = _vergleichLadeZaehler
            Dim left = _compareLeftPath, right = _compareRightPath
            Try
                ' "nurRechts" heisst: das festgehaltene linke Bild NICHT neu laden - es ist die
                ' Referenz und ein zweiter Decode waere bei RAW Sekunden. Es heisst aber nicht
                ' "ohne linkes Bild weitermachen": blaettert man direkt nach dem Anheften weiter,
                ' ueberholt diese Anforderung den noch laufenden ersten Ladevorgang, dessen Ergebnis
                ' dann verworfen wird - die linke Flaeche blieb dauerhaft leer.
                If Not nurRechts OrElse _compareLeftImage Is Nothing Then
                    Dim a = Await Task.Run(Function() DecodeViewerBitmap(left, alwaysDevelop:=True))
                    If Not _isCompareMode OrElse token <> _vergleichLadeZaehler Then
                        a?.Dispose()
                        Return
                    End If
                    CompareLeftImage = a
                    If _isFitToWindow Then UpdateFitZoom()
                End If
                Dim b = Await Task.Run(Function() DecodeViewerBitmap(right, alwaysDevelop:=True))
                If Not _isCompareMode OrElse token <> _vergleichLadeZaehler Then
                    b?.Dispose()
                    Return
                End If
                CompareRightImage = b
                If _isFitToWindow AndAlso CompareLeftImage Is Nothing Then UpdateFitZoom()
            Catch ex As Exception
                DiagnosticLogService.LogException("Viewer.Vergleich", ex)
            End Try
        End Sub

        Public Sub OpenImage(imagePath As String, Optional allPaths As List(Of String) = Nothing, Optional cacheScopeId As String = Nothing, Optional cacheScopeName As String = Nothing)
            _isImmichSession = False
            _immichSourceAlbumId = Nothing
            If Not File.Exists(imagePath) Then Return
            ' Ein normales Oeffnen beendet einen laufenden Vergleich - sonst zeigt der Betrachter
            ' beim naechsten Bild aus der Galerie weiter die alten zwei Flaechen. OpenCompare ruft
            ' diese Methode zuerst und setzt den Modus danach wieder.
            ExitCompare(zurueckZumFokus:=False)

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

            UebernehmeKatalogAttribute(imagePath)
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
            Try
                If String.IsNullOrEmpty(_currentImagePath) OrElse _mainVm Is Nothing Then Return
                Await _mainVm.OpenImageInEditor(_currentImagePath, EditorFilmstripPaths(), _thumbCacheScopeId, _thumbCacheScopeName, forceSaveAsOnly:=_isImmichSession, immichAlbumId:=_immichSourceAlbumId)
                If _mainVm.Editor Is Nothing OrElse Not String.Equals(_mainVm.Editor.CurrentImagePath, _currentImagePath, StringComparison.OrdinalIgnoreCase) Then Return
                _mainVm.Editor.CurrentTool = EditorTool.Crop
                _mainVm.Editor.SetCropPercentages(cropLeft, cropTop, cropRight, cropBottom)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("ViewerViewModel.OpenCropInEditor", ex)
            End Try
        End Sub

        ''' <summary>Startet das Laden des aktuellen Bildes. Der DECODE laeuft im HINTERGRUND
        ''' (Analyse: vorher synchron auf dem UI-Thread - jeder Bildwechsel fror den
        ''' Viewer fuer die Dekodier-Dauer ein, bei grossen JPEGs/RAWs deutlich spuerbar). Das
        ''' bisherige Bild wird beim Start des neuen Loads entfernt, damit waehrend des
        ''' Dekodierens/Renderns nicht kurz der vorherige Inhalt als aktuelles Bild erscheint;
        ''' ueberholte Ergebnisse verwirft der Lade-Token (schnelles Blaettern startet mehrere Loads, nur der juengste
        ''' gewinnt). Nach der Uebernahme werden Fit-Zoom und Statuszeile NACHGEZOGEN - die
        ''' Aufrufer haben sie direkt nach LoadBitmap nur fuer das noch angezeigte alte Bild
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
            SetBitmapLoading(True)
            ' Bildwechsel atomar anzeigen: das alte Bitmap darf nicht waehrend des neuen
            ' Decode-/Render-Vorgangs als scheinbar aktuelles Bild stehen bleiben. Der Ladezustand
            ' wird erst sichtbar, wenn CurrentImage Nothing ist (ShowBitmapLoading).
            CurrentImage = Nothing
            ImageWidth = 0
            ImageHeight = 0
            RunBitmapLoad(path, token, FpxService.IsFpx(path))
        End Sub

        ''' Verwirft ein eventuell laufendes asynchrones Bild-Laden (Bildwechsel auf Video,
        ''' Loeschen/Freigeben der Datei) - ohne das koennte ein spaetes Decode-Ergebnis ein
        ''' bewusst geleertes CurrentImage wieder "auferstehen" lassen.
        Private Sub InvalidatePendingBitmapLoad()
            System.Threading.Interlocked.Increment(_bitmapLoadToken)
            SetBitmapLoading(False)
        End Sub

        Private Async Sub RunBitmapLoad(path As String, token As Integer, isFpx As Boolean)
            Dim bmp As Bitmap = Nothing
            Try
                bmp = Await Task.Run(Function() DecodeViewerBitmap(path))
            Catch
                bmp = Nothing
            End Try

            If Not ApplyLoadedBitmap(token, bmp) Then Return
            SetBitmapLoading(False)
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
        ''' <summary>Die Entscheidung, ob ein RAW entwickelt oder als eingebettete Kamera-Vorschau
        ''' gezeigt wird - bewusst als parameterlose reine Funktion und nicht als Eigenschaft, damit
        ''' sie ohne Anwendung, ohne Einstellungen und ohne LibRaw geprueft werden kann.
        '''
        ''' Im Vergleich gilt sie IMMER: dort ist die Kamera-Vorschau die falsche Quelle. Sonst nur
        ''' bei eingeschalteter Einstellung UND vorhandener Begleitdatei - ohne Rezept lohnt der
        ''' teure Decode in der Einzelansicht nicht.</summary>
        Friend Shared Function SollRawEntwickeln(alwaysDevelop As Boolean, libRawVorhanden As Boolean,
                                                 einstellungAn As Boolean, begleitdateiDa As Boolean) As Boolean
            If Not libRawVorhanden Then Return False
            If alwaysDevelop Then Return True
            Return einstellungAn AndAlso begleitdateiDa
        End Function

        ''' <param name="alwaysDevelop">Im Bildvergleich gesetzt. Dort ist die eingebettete
        ''' Kamera-Vorschau die falsche Quelle: sie zeigt die Wiedergabe der KAMERA, nicht unsere -
        ''' zwei RAWs nebeneinander waeren damit nach der Kamera beurteilt statt nach der eigenen
        ''' Entwicklung, und ein RAW neben einem JPEG vergliche zwei verschiedene Pipelines. Kostet
        ''' den vollen Decode je Bild; das linke Bild bleibt deshalb stehen und wird beim
        ''' Weiterblaettern nicht neu geladen (siehe LadeVergleichsbilder).</param>
        Private Shared Function DecodeViewerBitmap(path As String, Optional alwaysDevelop As Boolean = False) As Bitmap
            If RawPreviewService.IsSupportedRaw(path) Then
                ' Entwickelte Vorschau statt der schnellen eingebetteten: im Vergleich immer, sonst
                ' nur bei aktiver Einstellung und vorhandener .fpxmp. ApplyAdjustments liefert
                ' bereits orientiert + mit Rezept-Geometrie.
                If SollRawEntwickeln(alwaysDevelop, RawDecodeService.IsAvailable,
                                     AppSettingsService.Load().DevelopRawInViewer,
                                     RawSidecarService.Exists(path)) Then
                    ' Ohne Begleitdatei die Vorgabewerte: entwickelt wird trotzdem, nur eben ohne
                    ' Rezept. Genau das ist im Vergleich gewollt.
                    Dim adj = If(RawSidecarService.Exists(path), RawSidecarService.TryRead(path), Nothing)
                    If adj Is Nothing Then adj = New ImageAdjustments()
                    Try
                        Dim developed = ImageProcessor.ApplyAdjustments(path, adj)
                        If developed IsNot Nothing Then Return developed
                    Catch
                        ' Faellt die Entwicklung aus (fehlendes LibRaw, defekte Datei), lieber die
                        ' eingebettete Vorschau als eine leere Flaeche.
                    End Try
                End If
                Using preview = RawPreviewService.ExtractPreviewWithFallback(path)
                    ' Gedrehte RAWs tragen ihre Drehung im Sidecar (die Datei selbst wird nie
                    ' neu geschrieben) - beim Anzeigen also wieder drauflegen.
                    Return If(preview IsNot Nothing,
                              ImageOrientationService.LoadOrientedAvaloniaBitmap(preview, RawSidecarService.ReadRotationDegrees(path), rawContainerPath:=path),
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
                    ' Wie RAW: eine gedrehte PSD traegt ihre Drehung im Sidecar, nicht in den Pixeln.
                    Return If(preview IsNot Nothing,
                              ImageOrientationService.LoadOrientedAvaloniaBitmap(preview, RawSidecarService.ReadRotationDegrees(path)),
                              Nothing)
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
            Try
                Await Task.Delay(1000)
                If Not _isSlideshowPlaying OrElse sequence <> _slideshowVideoEndSequence Then Return
                If Not IsVideoFile Then Return
                NavigateNext()
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("ViewerViewModel.ContinueSlideshowAfterVideoEndAsync", ex)
            End Try
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

            ' Eine vorhandene .fpxmp ist fuer RAW/PSD die portable Katalogquelle. Vor dem Laden der
            ' UI-Felder importieren, damit auch ein direkt im Viewer geoeffnetes Bild (ohne vorherigen
            ' Galerie-Scan) sofort Bewertung, Favorit, Etikett und Stichwoerter zeigt.
            If Not _isImmichSession Then LibraryService.Instance.ImportFpxmpCatalogData(imagePath)

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

            ' NUTZER-BEFUND (2. Runde): Beim schnellen Blättern blieb das KOMPLETTE Panel
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
                         ' auf dem alten Stand.
                         Dim headerSize = ImageProcessor.GetOrientedImageSize(imagePath)
                         Dim infoWidth = If(headerSize.Width > 0, headerSize.Width, capturedWidth)
                         Dim infoHeight = If(headerSize.Height > 0, headerSize.Height, capturedHeight)
                         Dim info = ImageInfoService.BuildImageInfo(imagePath, infoWidth, infoHeight)
                         ' In einer Immich-Sitzung sind Größe und Zeitstempel die der Temp-Kopie -
                         ' keines davon beschreibt das Asset, das der Nutzer sieht.
                         If _isImmichSession Then
                             info.FileCreated = ""
                             info.FileModified = ""
                         End If
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
            ExifInfo = If(provisionalInfo, ImageInfoService.BuildProvisionalFromCatalog(imagePath))
            _infoPanelShownForPath = imagePath
            ' Das Histogramm des alten Bildes ebenfalls sofort raus - es käme sonst als letztes
            ' Relikt des vorherigen Bildes erst mit dem Nachschub-Post weg.
            _histogramLoadedForPath = ""
            HistogramImage = Nothing
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
            Return CanBakeRotation(path) OrElse RawSidecarService.IsSidecarFormat(path)
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

            ' RAW und PSD werden NIE neu geschrieben - wir könnten die Formate gar nicht erzeugen
            ' und würden die Datei durch ihre eigene eingebettete Vorschau ersetzen. Die Drehung geht
            ' stattdessen nicht-destruktiv in die Rezept-Begleitdatei (foto.cr2.fpxmp), genau wie
            ' beim Editor. Viewer, Filmstreifen und Kacheln lesen sie beim Anzeigen wieder aus.
            If RawSidecarService.IsSidecarFormat(source) Then Return Await SaveRotationToSidecarAsync(source, angle)

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
                    ' von selbst (der Drehwinkel steckt im Cache-Dateinamen), aber die bereits
                    ' geladenen Bitmaps im Speicher muessen neu geladen werden, sonst bleibt die
                    ' Kachel im Filmstreifen und in der Gallery ungedreht stehen.
                    _mainVm?.ReloadThumbnailsForFile(source)
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
            ' Im Vergleich blaettert JEDE Navigation die rechte Flaeche weiter - Pfeiltasten, Mausrad
            ' und Filmstreifen laufen alle hier durch. Ohne diese Weiche wechselte nur das versteckte
            ' Einzelbild samt Infopanel, waehrend die beiden sichtbaren Flaechen stehen blieben.
            ' Angeheftet, aber noch kein Vergleich: das naechste Bild oeffnet ihn - angeheftet links,
            ' neu rechts. Dasselbe Bild nochmal anzusteuern tut nichts.
            If Not _isCompareMode AndAlso IsImagePinned AndAlso
               Not String.Equals(_folderPaths(idx), _pinnedPath, StringComparison.OrdinalIgnoreCase) Then
                OpenCompare(_pinnedPath, _folderPaths(idx))
                Return
            End If
            If _isCompareMode Then
                SetCompareRight(_folderPaths(idx))
                _currentIndex = idx
                Me.RaisePropertyChanged(NameOf(PositionText))
                Me.RaisePropertyChanged(NameOf(CurrentFilmstripIndex))
                Return
            End If
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
            UebernehmeKatalogAttribute(_currentImagePath)
            Me.RaisePropertyChanged(NameOf(IsRawFile))
            Me.RaisePropertyChanged(NameOf(IsVideoFile))
            Me.RaisePropertyChanged(NameOf(ShowVideoUnavailableNotice))
            Me.RaisePropertyChanged(NameOf(HasNoMedia))
            Me.RaisePropertyChanged(NameOf(CanEdit))
        End Sub

        Public Async Sub NavigateToItem(item As ImageItem)
            Try
                If item Is Nothing Then Return
                Dim idx = _folderPaths.FindIndex(Function(p) String.Equals(p, item.FilePath, StringComparison.OrdinalIgnoreCase))
                If idx >= 0 Then Await CommitNavigateAsync(idx)
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("ViewerViewModel.NavigateToItem", ex)
            End Try
        End Sub

        ''' <summary>Loeschen ist im Vergleich gesperrt. Dort stehen ZWEI Bilder nebeneinander, und
        ''' welches der Knopf traefe, waere nicht ablesbar - der Fokus ist eine feine Markierung, kein
        ''' Sicherheitsmerkmal fuer eine nicht umkehrbare Aktion. Wer loeschen will, verlaesst den
        ''' Vergleich; dann ist eindeutig, was gemeint ist.</summary>
        Public ReadOnly Property CanDeleteCurrent As Boolean
            Get
                Return Not _isCompareMode AndAlso Not String.IsNullOrEmpty(_currentImagePath)
            End Get
        End Property

        Private Sub DeleteCurrent()
            If Not CanDeleteCurrent Then Return
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

        ''' <summary>Strg+R im Betrachter: derselbe "Bildgröße ändern"-Ablauf wie in der Galerie, nur für
        ''' das angezeigte Bild. Bewusst über die Galerie-Umsetzung, damit Dialog, Überschreiben/Kopie und
        ''' der Immich-Weg an EINER Stelle stehen. Danach wird das Bild neu geladen - beim Überschreiben
        ''' liegt auf dem Pfad jetzt eine andere Datei.</summary>
        ''' <summary>Reicht das ANGEZEIGTE Bild an einen Stapel-Ablauf der Galerie weiter.
        ''' Konvertieren, Exportieren und Filter unterscheiden sich nur in der Zeile, die sie
        ''' aufrufen - der Weg zum Element ist derselbe wie bei ResizeCurrent.</summary>
        ''' <summary>Auf WELCHES Bild sich die naechste Menueaktion bezieht. Leer heisst: das
        ''' auf der Buehne. Gesetzt wird es beim Oeffnen des Kontextmenues, wenn der Klick auf
        ''' einer Filmstreifen-Kachel lag - dann meint der Nutzer diese und nicht die Buehne.</summary>
        Public Property ContextTargetPath As String = ""

        ''' <summary>Der Pfad, auf den sich eine Menueaktion bezieht.</summary>
        ''' <summary>Ist das Ziel ein Video? Dann faellt alles weg, was auf Bildpixeln arbeitet.
        ''' Gefragt wird ueber ImageItem, damit dieselbe Erkennung gilt wie in der Galerie.</summary>
        Private Function TargetIsVideo() As Boolean
            Dim path = TargetPath()
            If String.IsNullOrWhiteSpace(path) Then Return False
            Return New ImageItem(path).IsVideoFile
        End Function

        Private Function TargetPath() As String
            Dim target = If(ContextTargetPath, "")
            If target <> "" AndAlso IO.File.Exists(target) Then Return target
            Return _currentImagePath
        End Function

        Private Sub WithCurrentImage(ablauf As Action(Of GalleryViewModel, IList(Of ImageItem)))
            Try
                Dim path = TargetPath()
                If String.IsNullOrWhiteSpace(path) Then Return
                Dim gallery = _mainVm?.Gallery
                If gallery Is Nothing Then Return

                Dim item = FilmstripItems.FirstOrDefault(Function(i) i IsNot Nothing AndAlso
                                                             PathIdentity.AreSame(i.FilePath, path))
                If item Is Nothing Then
                    If Not File.Exists(path) Then Return
                    item = New ImageItem(path)
                End If
                If Not item.IsImage Then Return

                ablauf(gallery, New List(Of ImageItem) From {item})
            Catch ex As Exception
                DiagnosticLogService.LogException("Viewer.StapelAblauf", ex)
            End Try
        End Sub

        Private Async Sub ResizeCurrent()
            Try
                If String.IsNullOrWhiteSpace(_currentImagePath) Then Return
                Dim gallery = _mainVm?.Gallery
                If gallery Is Nothing Then Return

                Dim item = FilmstripItems.FirstOrDefault(Function(i) i IsNot Nothing AndAlso
                                                             PathIdentity.AreSame(i.FilePath, _currentImagePath))
                If item Is Nothing Then
                    If Not File.Exists(_currentImagePath) Then Return
                    item = New ImageItem(_currentImagePath)
                End If
                If Not item.IsImage Then Return

                Await gallery.ResizeImageItemsAsync(New List(Of ImageItem) From {item})

                If File.Exists(_currentImagePath) Then
                    item.EvictThumbnail()
                    OpenImage(_currentImagePath, _folderPaths, _thumbCacheScopeId, _thumbCacheScopeName)
                End If
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("ViewerViewModel.ResizeCurrent", ex)
            End Try
        End Sub

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

        ''' <summary>Die Zwischenablage haengt am TopLevel und ist nur von der View aus
        ''' erreichbar. Frueher stand hier ein LEERER Rumpf mit genau diesem Hinweis - das Kommando
        ''' gab es, es tat nur nichts, und ein Menueeintrag darauf waere still wirkungslos gewesen.
        ''' Die View setzt den Haken beim Anhaengen.</summary>
        Public Property CopyPathToClipboard As Action(Of String)

        Private Sub CopyToClipboard()
            Dim path = TargetPath()
            If String.IsNullOrEmpty(path) Then Return
            CopyPathToClipboard?.Invoke(path)
        End Sub

        ''' <summary>Druckt das gerade angezeigte Bild. Der Betrachter arbeitet auf Pfaden - bei
        ''' einem Immich-Asset ist _currentImagePath bereits die lokale Temp-Kopie, es braucht also
        ''' keine gesonderte Auflösung wie in der Galerie.</summary>
        Private Sub PrintCurrent()
            If String.IsNullOrEmpty(_currentImagePath) OrElse Not IO.File.Exists(_currentImagePath) Then Return
            _mainVm?.ShowPrintDialog(New List(Of String) From {_currentImagePath})
        End Sub

        Private Sub OpenInFileManager()
            Dim path = TargetPath()
            If String.IsNullOrEmpty(path) Then Return
            Try
                Dim folder = IO.Path.GetDirectoryName(path)
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

        Private _compareViewportWidth As Double
        Private _compareViewportHeight As Double

        ''' <summary>Die Groesse EINER Vergleichsflaeche. Der Vergleich braucht eine eigene, weil die
        ''' einzelne Bildflaeche waehrenddessen ausgeblendet ist und damit Groesse null meldet - das
        ''' Einpassen rechnete sonst mit einer Flaeche von null und lieferte stumpf 100 %.</summary>
        Public Sub SetCompareViewportSize(width As Double, height As Double)
            _compareViewportWidth = Math.Max(0, width)
            _compareViewportHeight = Math.Max(0, height)
            If _isCompareMode AndAlso _isFitToWindow Then UpdateFitZoom()
        End Sub

        Private Function CalculateFitZoom() As Double
            ' Im Vergleich zaehlt das Bild IN DER FLAECHE, nicht das versteckte Einzelbild. Bei RAW
            ' sind das zwei verschiedene Groessen: die Flaechen zeigen die volle Entwicklung, das
            ' Einzelbild die kleine eingebettete Vorschau. Mit deren Groesse gerechnet fiel das
            ' Einpassen um den Faktor zwischen beiden daneben.
            If _isCompareMode Then
                Dim source = If(CompareLeftImage, CompareRightImage)
                If source Is Nothing OrElse _compareViewportWidth <= 0 OrElse _compareViewportHeight <= 0 Then Return 1.0
                Return FitFactor(source.Size.Width, source.Size.Height,
                                       _compareViewportWidth, _compareViewportHeight)
            End If

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

            Return FitFactor(imageWidth, imageHeight, _imageViewportWidth, _imageViewportHeight)
        End Function

        ''' <summary>Die EINE Einpassen-Rechnung fuer Einzelbild und Vergleich. Vorher stand sie nur
        ''' im Einzelbild-Weg; der Vergleich hatte gar keine.</summary>
        Private Function FitFactor(imageWidth As Double, imageHeight As Double,
                                         flaecheBreite As Double, flaecheHoehe As Double) As Double
            If imageWidth <= 0 OrElse imageHeight <= 0 OrElse flaecheBreite <= 0 OrElse flaecheHoehe <= 0 Then Return 1.0
            Dim fitScale = Math.Max(0.05, Math.Min(flaecheBreite / imageWidth, flaecheHoehe / imageHeight))

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
