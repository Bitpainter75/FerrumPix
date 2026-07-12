Imports System
Imports System.ComponentModel
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Runtime.CompilerServices
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports FerrumPix.Services

Namespace Models

    Public Class ImageItem
        Implements INotifyPropertyChanged

        Public Event PropertyChanged As PropertyChangedEventHandler Implements INotifyPropertyChanged.PropertyChanged

        Protected Sub RaisePropertyChanged(<CallerMemberName> Optional name As String = "")
            RaiseEvent PropertyChanged(Me, New PropertyChangedEventArgs(name))
        End Sub

        Private Const BackgroundThumbnailPriority As Integer = 0
        Private Const ViewportThumbnailPriority As Integer = 100
        Private Const MaxParallelThumbnailJobs As Integer = 4
        ' Ein Worker, der gerade eine Hintergrund-Regenerierung (voller Decode+Resize+Encode)
        ' begonnen hat, prüft die Warteschlange erst wieder, wenn diese fertig ist - er kann also
        ' nicht "unterbrochen" werden, sobald neue Viewport-Anfragen eintreffen. Deshalb wird
        ' Hintergrundarbeit auf einen einzelnen Worker gedeckelt, damit immer genug freie Kapazität
        ' bleibt, um frisch sichtbare Bilder sofort (mit einem neuen Worker) zu bedienen, statt auf
        ' einen bereits blockierten Worker warten zu müssen, und damit die Whole-Folder-Vorladung
        ' nicht mehrere CPU-Kerne gleichzeitig auslastet.
        Private Const MaxConcurrentBackgroundJobs As Integer = 1
        Private Shared ReadOnly _thumbnailQueueLock As New Object()
        ' Separate queues: viewport items (high priority) and background items
        ' Workers always drain the viewport queue first (LIFO within each)
        Private Shared ReadOnly _viewportQueue As New List(Of ImageItem)()
        Private Shared ReadOnly _backgroundQueue As New List(Of ImageItem)()
        Private Shared _runningThumbnailWorkers As Integer = 0
        Private Shared _runningBackgroundWorkers As Integer = 0

        Public Property FilePath As String
        Public Property FileName As String
        Public Property FileSize As Long
        Public Property DateModified As DateTime
        Public Property IsFolder As Boolean
        Public Property IsParentFolderEntry As Boolean
        Public Property Tags As New List(Of String)()

        Public ReadOnly Property IsSelectableEntry As Boolean
            Get
                Return Not IsParentFolderEntry
            End Get
        End Property

        Public ReadOnly Property IsImage As Boolean
            Get
                Return Not IsFolder
            End Get
        End Property

        ''' <summary>True, wenn dieses Item ein Immich-Asset repräsentiert (kein lokaler Dateipfad).
        ''' FilePath trägt dann nur den Original-Dateinamen zur Endungserkennung.</summary>
        Public ReadOnly Property IsImmichAsset As Boolean
            Get
                Return _immichAssetId IsNot Nothing
            End Get
        End Property

        Public ReadOnly Property ImmichAssetId As String
            Get
                Return _immichAssetId
            End Get
        End Property

        Public ReadOnly Property ImmichOriginalFileName As String
            Get
                Return _immichOriginalFileName
            End Get
        End Property

        ''' <summary>Lokaler Temp-Pfad des zuletzt heruntergeladenen Originals (für Viewer/Editor).
        ''' Erlaubt der Galerie, das Item beim Rückweg aus dem Viewer wiederzufinden, obwohl FilePath
        ''' ein Immich-Pseudo-Pfad ist.</summary>
        Public Property ImmichLocalPath As String

        Private Shared ReadOnly _videoFormats As String() = {".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"}

        Public ReadOnly Property IsRawFile As Boolean
            Get
                Return Not IsFolder AndAlso RawPreviewService.IsSupportedRaw(FilePath)
            End Get
        End Property

        ''' SVG ist ein Vektorformat - Galerie/Viewer zeigen es (gerastert), der Pixel-Editor
        ''' (Belichtung/Kurven/Filter usw.) ergibt dafür konzeptionell keinen Sinn.
        Public ReadOnly Property IsVectorFile As Boolean
            Get
                Return Not IsFolder AndAlso SvgPreviewService.IsSupportedSvg(FilePath)
            End Get
        End Property

        Public ReadOnly Property IsVideoFile As Boolean
            Get
                Return Not IsFolder AndAlso _videoFormats.Contains(IO.Path.GetExtension(FilePath).ToLowerInvariant())
            End Get
        End Property

        Public ReadOnly Property CanEditFile As Boolean
            Get
                Return Not IsFolder AndAlso Not IsVectorFile AndAlso Not IsVideoFile
            End Get
        End Property

        Public ReadOnly Property CanRemoveMetadata As Boolean
            Get
                Return Not IsImmichAsset AndAlso CanEditFile
            End Get
        End Property

        Public ReadOnly Property CanFileOperationCopy As Boolean
            Get
                Return Not IsParentFolderEntry AndAlso FileOperationPolicy.CanCopy(FilePath)
            End Get
        End Property

        Public ReadOnly Property CanFileOperationRename As Boolean
            Get
                Return Not IsParentFolderEntry AndAlso FileOperationPolicy.CanRename(FilePath)
            End Get
        End Property

        Public ReadOnly Property CanFileOperationDelete As Boolean
            Get
                Return Not IsParentFolderEntry AndAlso FileOperationPolicy.CanDelete(FilePath)
            End Get
        End Property

        Public ReadOnly Property CanFileOperationPasteInto As Boolean
            Get
                Return If(IsFolder, FileOperationPolicy.CanPasteInto(FilePath), FileOperationPolicy.CanPasteInto(IO.Path.GetDirectoryName(FilePath)))
            End Get
        End Property

        Public ReadOnly Property SearchText As String
            Get
                Dim ratingToken = $"{Rating} Sterne {Rating} Stern rating:{Rating} bewertung:{Rating} {New String("★"c, Math.Max(0, Math.Min(5, Rating)))}"
                Dim favoriteToken = If(IsFavorite, "favorit favorite fav is:favorite", "")
                If Tags Is Nothing OrElse Tags.Count = 0 Then Return FileName & " " & ratingToken & " " & favoriteToken
                Return FileName & " " & String.Join(" ", Tags) & " " & ratingToken & " " & favoriteToken
            End Get
        End Property

        Private _rating As Integer
        Public Property Rating As Integer
            Get
                Return _rating
            End Get
            Set(value As Integer)
                If _rating = value Then Return
                _rating = value
                RaisePropertyChanged()
            End Set
        End Property

        Private _isFavorite As Boolean
        Public Property IsFavorite As Boolean
            Get
                Return _isFavorite
            End Get
            Set(value As Boolean)
                If _isFavorite = value Then Return
                _isFavorite = value
                RaisePropertyChanged()
            End Set
        End Property

        ' 0 = noch nicht geladen (Kachel zeigt in dem Fall nichts an, siehe DimensionsText)
        Private _imageWidth As Integer = 0
        Public Property ImageWidth As Integer
            Get
                Return _imageWidth
            End Get
            Set(value As Integer)
                If _imageWidth = value Then Return
                _imageWidth = value
                RaisePropertyChanged()
                RaisePropertyChanged(NameOf(DimensionsText))
            End Set
        End Property

        Private _imageHeight As Integer = 0
        Public Property ImageHeight As Integer
            Get
                Return _imageHeight
            End Get
            Set(value As Integer)
                If _imageHeight = value Then Return
                _imageHeight = value
                RaisePropertyChanged()
                RaisePropertyChanged(NameOf(DimensionsText))
            End Set
        End Property

        Public ReadOnly Property DimensionsText As String
            Get
                If _imageWidth <= 0 OrElse _imageHeight <= 0 Then Return ""
                Return $"{_imageWidth}×{_imageHeight}"
            End Get
        End Property

        Private _fileCreatedAt As DateTime = DateTime.MinValue
        Public Property FileCreatedAt As DateTime
            Get
                Return _fileCreatedAt
            End Get
            Set(value As DateTime)
                If _fileCreatedAt = value Then Return
                _fileCreatedAt = value
                RaisePropertyChanged()
                RaisePropertyChanged(NameOf(DateFileCreatedText))
            End Set
        End Property

        ' EXIF DateTimeOriginal - unterscheidet sich vom Dateisystem-Erstellungsdatum, da EXIF beim
        ' Kopieren/Synchronisieren der Datei erhalten bleibt, das Dateisystem-Datum aber nicht.
        Private _exifDateTaken As DateTime?
        Public Property ExifDateTaken As DateTime?
            Get
                Return _exifDateTaken
            End Get
            Set(value As DateTime?)
                If Nullable.Equals(_exifDateTaken, value) Then Return
                _exifDateTaken = value
                RaisePropertyChanged()
                RaisePropertyChanged(NameOf(DateExifTakenText))
            End Set
        End Property

        ' EXIF IFD0 TagDateTime - vom Aufnahmegerät/der Bearbeitungssoftware gesetztes "geändert"-Datum,
        ' unabhängig vom Dateisystem-Änderungsdatum.
        Private _exifDateModified As DateTime?
        Public Property ExifDateModified As DateTime?
            Get
                Return _exifDateModified
            End Get
            Set(value As DateTime?)
                If Nullable.Equals(_exifDateModified, value) Then Return
                _exifDateModified = value
                RaisePropertyChanged()
                RaisePropertyChanged(NameOf(DateExifModifiedText))
            End Set
        End Property

        ' Schlanke EXIF-Sortierfelder, aus dem Katalog gespiegelt (siehe GalleryViewModel.LoadFolderImages/
        ' QueueBackgroundMetaRefresh) - vermeidet DB-Zugriffe live während des Sortierens.
        Private _exifCamera As String = ""
        Public Property ExifCamera As String
            Get
                Return _exifCamera
            End Get
            Set(value As String)
                value = If(value, "")
                If _exifCamera = value Then Return
                _exifCamera = value
                RaisePropertyChanged()
            End Set
        End Property

        Private _exifIso As Integer?
        Public Property ExifIso As Integer?
            Get
                Return _exifIso
            End Get
            Set(value As Integer?)
                If Nullable.Equals(_exifIso, value) Then Return
                _exifIso = value
                RaisePropertyChanged()
            End Set
        End Property

        Private _exifAperture As Double?
        Public Property ExifAperture As Double?
            Get
                Return _exifAperture
            End Get
            Set(value As Double?)
                If Nullable.Equals(_exifAperture, value) Then Return
                _exifAperture = value
                RaisePropertyChanged()
            End Set
        End Property

        Private _hasExifMetadata As Boolean
        Public Property HasExifMetadata As Boolean
            Get
                Return _hasExifMetadata
            End Get
            Set(value As Boolean)
                If _hasExifMetadata = value Then Return
                _hasExifMetadata = value
                RaisePropertyChanged()
            End Set
        End Property

        Private _hasIptcMetadata As Boolean
        Public Property HasIptcMetadata As Boolean
            Get
                Return _hasIptcMetadata
            End Get
            Set(value As Boolean)
                If _hasIptcMetadata = value Then Return
                _hasIptcMetadata = value
                RaisePropertyChanged()
            End Set
        End Property

        Private _hasXmpMetadata As Boolean
        Public Property HasXmpMetadata As Boolean
            Get
                Return _hasXmpMetadata
            End Get
            Set(value As Boolean)
                If _hasXmpMetadata = value Then Return
                _hasXmpMetadata = value
                RaisePropertyChanged()
            End Set
        End Property

        Private _hasIccProfile As Boolean
        Public Property HasIccProfile As Boolean
            Get
                Return _hasIccProfile
            End Get
            Set(value As Boolean)
                If _hasIccProfile = value Then Return
                _hasIccProfile = value
                RaisePropertyChanged()
            End Set
        End Property

        Private _exifMetadataSummary As String = ""
        Public Property ExifMetadataSummary As String
            Get
                Return _exifMetadataSummary
            End Get
            Set(value As String)
                value = If(value, "")
                If _exifMetadataSummary = value Then Return
                _exifMetadataSummary = value
                RaisePropertyChanged()
                RaisePropertyChanged(NameOf(CurrentMetadataOverlayText))
            End Set
        End Property

        Private _iptcMetadataSummary As String = ""
        Public Property IptcMetadataSummary As String
            Get
                Return _iptcMetadataSummary
            End Get
            Set(value As String)
                value = If(value, "")
                If _iptcMetadataSummary = value Then Return
                _iptcMetadataSummary = value
                RaisePropertyChanged()
                RaisePropertyChanged(NameOf(CurrentMetadataOverlayText))
            End Set
        End Property

        Private _xmpMetadataSummary As String = ""
        Public Property XmpMetadataSummary As String
            Get
                Return _xmpMetadataSummary
            End Get
            Set(value As String)
                value = If(value, "")
                If _xmpMetadataSummary = value Then Return
                _xmpMetadataSummary = value
                RaisePropertyChanged()
                RaisePropertyChanged(NameOf(CurrentMetadataOverlayText))
            End Set
        End Property

        Private _iccMetadataSummary As String = ""
        Public Property IccMetadataSummary As String
            Get
                Return _iccMetadataSummary
            End Get
            Set(value As String)
                value = If(value, "")
                If _iccMetadataSummary = value Then Return
                _iccMetadataSummary = value
                RaisePropertyChanged()
                RaisePropertyChanged(NameOf(CurrentMetadataOverlayText))
            End Set
        End Property

        Private _hoveredMetadataKind As String = ""
        Public Property HoveredMetadataKind As String
            Get
                Return _hoveredMetadataKind
            End Get
            Set(value As String)
                value = If(value, "")
                If _hoveredMetadataKind = value Then Return
                _hoveredMetadataKind = value
                RaisePropertyChanged()
                RaisePropertyChanged(NameOf(CurrentMetadataOverlayTitle))
                RaisePropertyChanged(NameOf(CurrentMetadataOverlayText))
            End Set
        End Property

        Public ReadOnly Property CurrentMetadataOverlayTitle As String
            Get
                Select Case HoveredMetadataKind
                    Case "Exif" : Return "EXIF"
                    Case "Iptc" : Return "IPTC"
                    Case "Xmp" : Return "XMP"
                    Case "Icc" : Return "ICC"
                    Case Else : Return ""
                End Select
            End Get
        End Property

        Public ReadOnly Property CurrentMetadataOverlayText As String
            Get
                Select Case HoveredMetadataKind
                    Case "Exif" : Return ExifMetadataSummary
                    Case "Iptc" : Return IptcMetadataSummary
                    Case "Xmp" : Return XmpMetadataSummary
                    Case "Icc" : Return IccMetadataSummary
                    Case Else : Return ""
                End Select
            End Get
        End Property

        Private _isSelected As Boolean
        Private _isNavigationSelected As Boolean

        Public Property IsSelected As Boolean
            Get
                Return _isSelected
            End Get
            Set(value As Boolean)
                If _isSelected = value Then Return
                _isSelected = value
                RaisePropertyChanged()
            End Set
        End Property

        Public Property IsNavigationSelected As Boolean
            Get
                Return _isNavigationSelected
            End Get
            Set(value As Boolean)
                If _isNavigationSelected = value Then Return
                _isNavigationSelected = value
                RaisePropertyChanged()
            End Set
        End Property

        Private _thumbnail As Bitmap
        Private _thumbState As Integer = 0       ' 0=unloaded, 1=queued/loading, 2=done
        Private _inViewportQueue As Boolean = False
        Private _inBackgroundQueue As Boolean = False
        Private _isThumbnailLoading As Boolean = False
        Private _evictThumbnailAfterLoad As Boolean = False
        Private _thumbnailCancellationToken As CancellationToken = CancellationToken.None
        Private _thumbnailCacheScopeId As String = Nothing
        Private _thumbnailCacheScopeName As String = Nothing
        ' Gesetzt (Asset-UUID) nur für Immich-Items. Diese laden ihr Thumbnail über den Immich-Zweig
        ' (ImmichService, eigener Netz-/Diskcache) statt über den dateipfad-basierten
        ' ThumbnailCacheService - die gesamte übrige Warteschlangen-/LRU-/Dispose-Logik gilt unverändert.
        Private _immichAssetId As String = Nothing
        Private _immichOriginalFileName As String = Nothing
        Private _immichUpdatedAt As String = ""      ' Immichs updatedAt - Invalidierung des Metadaten-Index
        Private _immichDetailState As Integer = 0   ' 0=noch nicht, 1=läuft/erledigt (je Item einmal)
        Private _fileInfoLoaded As Boolean = False
        Private _residentLruNode As LinkedListNode(Of ImageItem)
        Private _isPinnedVisible As Boolean = False

        ' Ein bei ~480px Cache-Breite dekodiertes Thumbnail liegt typischerweise bei ca. 400-700KB
        ' (Bgra8888). Der Cap bindet die dauerhaft im Speicher gehaltenen, bereits gesehenen
        ' Thumbnails auf ca. 100-180MB bei Standardeinstellung (250), unabhängig davon, wie weit
        ' der Nutzer inzwischen weitergescrollt ist - deutlich mehr als der alte, an die
        ' Sichtfenster-Position gekoppelte Keep-Alive-Puffer, damit Hin- und Herscrollen im selben
        ' Bereich keinen Re-Decode mehr auslöst (siehe TouchResident). Über die Einstellungen
        ' (Vorschaubild-Speichercache-Regler) vom Nutzer einstellbar, siehe MaxResidentThumbnails.
        Private Const DefaultMaxResidentThumbnails As Integer = 250
        Private Shared _maxResidentThumbnails As Integer = DefaultMaxResidentThumbnails
        Private Shared ReadOnly _residentLru As New LinkedList(Of ImageItem)()

        ''' <summary>Von den Einstellungen aus gesetzte Obergrenze für dauerhaft im Speicher
        ''' gehaltene Thumbnails (siehe AppSettingsService.GalleryThumbnailMemoryCacheCapacity).
        ''' Ein Herabsetzen verdrängt sofort überzählige, nicht aktuell sichtbare Elemente, statt
        ''' erst beim nächsten TouchResident-Aufruf.</summary>
        Public Shared Property MaxResidentThumbnails As Integer
            Get
                Return Volatile.Read(_maxResidentThumbnails)
            End Get
            Set(value As Integer)
                Dim evicted As List(Of ImageItem) = Nothing
                SyncLock _thumbnailQueueLock
                    _maxResidentThumbnails = Math.Max(0, value)
                    evicted = EvictExcessLocked()
                End SyncLock
                DisposeEvictedThumbnails(evicted)
            End Set
        End Property

        ''' <summary>Als aktuell sichtbar markierte Elemente überleben die LRU-Verdrängung in
        ''' TouchResident immer, unabhängig von ihrer Position in der Warteschlange - sonst kann
        ''' eine parallel laufende Hintergrund-Vorladung des ganzen Ordners (QueueBackgroundThumbnails)
        ''' die gerade erst angezeigten Thumbnails wieder verdrängen, weil diese als erste "berührt"
        ''' wurden und damit die ältesten LRU-Einträge sind. Wird von ViewportThumbnailTracker anhand
        ''' des tatsächlich sichtbaren Bereichs gesetzt/gelöscht.</summary>
        Public Sub SetPinnedVisible(pinned As Boolean)
            SyncLock _thumbnailQueueLock
                _isPinnedVisible = pinned
            End SyncLock
        End Sub

        ''' <summary>Merkt ein bereits geladenes Thumbnail als zuletzt genutzt vor (MRU-Ende der
        ''' LRU-Liste) und verdrängt bei Überschreiten von MaxResidentThumbnails das am längsten
        ''' nicht mehr angefragte, nicht aktuell sichtbare Element. Muss unter _thumbnailQueueLock
        ''' aufgerufen werden (SyncLock ist pro Thread reentrant, daher auch von Aufrufern sicher,
        ''' die das Lock bereits halten).</summary>
        Private Shared Sub TouchResident(item As ImageItem)
            Dim evicted As List(Of ImageItem) = Nothing
            SyncLock _thumbnailQueueLock
                If item._residentLruNode IsNot Nothing Then _residentLru.Remove(item._residentLruNode)
                item._residentLruNode = _residentLru.AddLast(item)
                evicted = EvictExcessLocked()
            End SyncLock
            DisposeEvictedThumbnails(evicted)
        End Sub

        ''' <summary>Verdrängt vom ältesten Ende der LRU-Liste, bis MaxResidentThumbnails erreicht
        ''' ist oder keine weiteren nicht aktuell sichtbaren Kandidaten mehr existieren. Muss unter
        ''' _thumbnailQueueLock aufgerufen werden.</summary>
        Private Shared Function EvictExcessLocked() As List(Of ImageItem)
            Dim evicted As List(Of ImageItem) = Nothing
            Dim node = _residentLru.First
            While _residentLru.Count > _maxResidentThumbnails AndAlso node IsNot Nothing
                Dim nextNode = node.Next
                Dim candidate = node.Value
                If Not candidate._isPinnedVisible Then
                    _residentLru.Remove(node)
                    candidate._residentLruNode = Nothing
                    candidate._thumbState = 0
                    If evicted Is Nothing Then evicted = New List(Of ImageItem)()
                    evicted.Add(candidate)
                End If
                node = nextNode
            End While
            Return evicted
        End Function

        ''' <summary>Ein per Binding an ein Image.Source gebundenes Bitmap darf erst disposed
        ''' werden, NACHDEM die UI per PropertyChanged benachrichtigt wurde und die Bindung
        ''' entfernt hat - sonst kann ein noch laufender Avalonia-Layoutdurchlauf (z.B. beim
        ''' Scrollen) auf ein bereits disposed Bitmap zugreifen und mit einer
        ''' NullReferenceException abstürzen (Image.ArrangeOverride). Das Umhängen von
        ''' Source=Nothing und das Dispose müssen daher auf dem UI-Thread und in dieser
        ''' Reihenfolge erfolgen.</summary>
        Private Shared Sub DisposeEvictedThumbnails(evicted As List(Of ImageItem))
            If evicted IsNot Nothing Then
                For Each oldest In evicted
                    Dim bmp = oldest._thumbnail
                    oldest._thumbnail = Nothing
                    If bmp IsNot Nothing Then
                        Dispatcher.UIThread.Post(Sub()
                                                      oldest.RaisePropertyChanged(NameOf(Thumbnail))
                                                      bmp.Dispose()
                                                  End Sub, DispatcherPriority.Background)
                    End If
                Next
            End If
        End Sub

        Private Sub UntrackResident()
            SyncLock _thumbnailQueueLock
                If _residentLruNode IsNot Nothing Then
                    _residentLru.Remove(_residentLruNode)
                    _residentLruNode = Nothing
                End If
            End SyncLock
        End Sub

        Public Sub New(filePath As String)
            InitializePath(filePath, True)
        End Sub

        Private Sub InitializePath(filePath As String, loadFileInfo As Boolean)
            Me.FilePath = filePath
            FileName = Path.GetFileName(filePath)
            If loadFileInfo Then EnsureFileInfoLoaded()
        End Sub

        Private Sub EnsureFileInfoLoaded()
            If _fileInfoLoaded OrElse IsFolder Then Return
            Try
                Dim info = New FileInfo(filePath)
                FileSize = info.Length
                DateModified = info.LastWriteTime
                FileCreatedAt = info.CreationTime
            Catch
                FileSize = 0
                DateModified = DateTime.MinValue
                FileCreatedAt = DateTime.MinValue
            End Try
            _fileInfoLoaded = True
        End Sub

        Public Sub New(filePath As String, thumbnailCancellationToken As CancellationToken)
            Me.New(filePath)
            _thumbnailCancellationToken = thumbnailCancellationToken
        End Sub

        Public Sub New(filePath As String, thumbnailCancellationToken As CancellationToken, thumbnailCacheScopeId As String, thumbnailCacheScopeName As String)
            Me.New(filePath, thumbnailCancellationToken)
            _thumbnailCacheScopeId = thumbnailCacheScopeId
            _thumbnailCacheScopeName = thumbnailCacheScopeName
        End Sub

        ''' <summary>Wie der Konstruktor, nimmt Größe und Zeitstempel aber aus einem FileInfo, das der
        ''' Aufrufer bereits hat. DirectoryInfo.EnumerateFiles liefert diese Objekte fertig befüllt aus dem
        ''' Verzeichniseintrag - unter Windows direkt aus FindNextFile, unter Linux ohne zweite
        ''' Pfadauflösung. Der übliche Weg über New FileInfo(pfad) kostet dagegen einen zusätzlichen
        ''' Systemaufruf je Datei, und davon gibt es beim Öffnen eines Ordners so viele wie Bilder.</summary>
        Public Shared Function FromFileInfo(info As FileInfo, thumbnailCancellationToken As CancellationToken) As ImageItem
            Dim item = New ImageItem()
            item.InitializePath(info.FullName, False)
            Try
                item.FileSize = info.Length
                item.DateModified = info.LastWriteTime
                item.FileCreatedAt = info.CreationTime
            Catch
                item.FileSize = 0
                item.DateModified = DateTime.MinValue
                item.FileCreatedAt = DateTime.MinValue
            End Try
            item._fileInfoLoaded = True
            item._thumbnailCancellationToken = thumbnailCancellationToken
            Return item
        End Function

        Public Shared Function CreateLightweight(filePath As String,
                                                 Optional thumbnailCancellationToken As CancellationToken = Nothing,
                                                 Optional thumbnailCacheScopeId As String = Nothing,
                                                 Optional thumbnailCacheScopeName As String = Nothing) As ImageItem
            Dim item = New ImageItem()
            item.InitializePath(filePath, False)
            item._thumbnailCancellationToken = thumbnailCancellationToken
            item._thumbnailCacheScopeId = thumbnailCacheScopeId
            item._thumbnailCacheScopeName = thumbnailCacheScopeName
            Return item
        End Function

        ''' <summary>Baut ein Item für ein Immich-Asset. FilePath = Original-Dateiname (nur zur
        ''' endungsbasierten Format-/Editier-Erkennung, KEIN lokaler Pfad); die Metadaten kommen fertig
        ''' aus dem Immich-Album-Abruf, sodass keine FileInfo-/EXIF-Zugriffe auf Platte nötig sind.</summary>
        Public Shared Function CreateImmichItem(asset As ImmichAsset, Optional thumbnailCancellationToken As CancellationToken = Nothing) As ImageItem
            Dim item = New ImageItem()
            ' Eindeutiger Pseudo-Pfad: die Asset-UUID hält ihn kollisionsfrei (zwei Album-Bilder dürfen
            ' denselben Original-Namen tragen), während GetFileName/GetExtension weiterhin Anzeigename
            ' und Endung liefern - Letztere steuert Video-/RAW-/Editierbarkeits-Erkennung.
            Dim displayName = If(String.IsNullOrEmpty(asset.FileName), asset.Id, asset.FileName)
            item.InitializePath("immich://" & asset.Id & "/" & displayName, False)
            item.FileName = displayName
            item._immichAssetId = asset.Id
            item._immichOriginalFileName = asset.FileName
            item._immichUpdatedAt = asset.UpdatedAt
            item._thumbnailCancellationToken = thumbnailCancellationToken
            item._fileInfoLoaded = True
            item.IsFolder = False
            item.ImageWidth = asset.Width
            item.ImageHeight = asset.Height
            item.FileSize = asset.FileSizeBytes
            item.IsFavorite = asset.IsFavorite
            item.Rating = asset.Rating
            item.Tags = If(asset.Tags, New List(Of String)())
            item.ExifDateTaken = asset.ExifDateTaken
            item.ExifCamera = asset.Camera
            item.ExifIso = asset.Iso
            item.ExifAperture = asset.Aperture
            Dim created = If(asset.FileCreatedAt.HasValue, asset.FileCreatedAt.Value, DateTime.MinValue)
            item.FileCreatedAt = created
            item.DateModified = created
            Return item
        End Function

        ''' <summary>Ergänzt ein Immich-Item um die erst per Detail-Abruf verfügbaren Metadaten
        ''' (Dateigröße, Rating, Kamera/ISO/Blende, Aufnahmedatum, Stichwörter) und feuert die passenden
        ''' PropertyChanged, damit Kachel und Info-Leiste sich aktualisieren.</summary>
        Public Sub ApplyImmichDetail(fileSizeBytes As Long, rating As Integer, camera As String,
                                     iso As Integer?, aperture As Double?, dateTaken As DateTime?, tags As List(Of String))
            If Not IsImmichAsset Then Return
            FileSize = fileSizeBytes
            RaisePropertyChanged(NameOf(FileSizeText))
            Me.Rating = rating
            ExifCamera = camera
            ExifIso = iso
            ExifAperture = aperture
            If dateTaken.HasValue Then ExifDateTaken = dateTaken
            Me.Tags = If(tags, New List(Of String)())
            RaisePropertyChanged(NameOf(Tags))
            RaisePropertyChanged(NameOf(SearchText))
        End Sub

        Public Shared Function FromFolder(folderPath As String) As ImageItem
            Dim item = New ImageItem()
            item.FilePath = folderPath
            item.FileName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            If String.IsNullOrEmpty(item.FileName) Then item.FileName = folderPath
            item.IsFolder = True

            Try
                Dim info = New DirectoryInfo(folderPath)
                item.DateModified = info.LastWriteTime
            Catch
                item.DateModified = DateTime.MinValue
            End Try

            Return item
        End Function

        Public Shared Function CreateParentFolderEntry(folderPath As String) As ImageItem
            Dim item = FromFolder(folderPath)
            item.IsParentFolderEntry = True
            item.FileName = ".."
            Return item
        End Function

        Private Sub New()
        End Sub

        Public ReadOnly Property FileSizeText As String
            Get
                If IsFolder Then Return LocalizationService.T("Ordner")
                EnsureFileInfoLoaded()
                Dim kb = FileSize / 1024.0
                If FileSize < 1024 Then Return $"{FileSize} B"
                If kb < 1024 Then Return $"{kb:F0} KB"
                Return $"{kb / 1024:F1} MB"
            End Get
        End Property

        Public ReadOnly Property DateText As String
            Get
                EnsureFileInfoLoaded()
                Return DateModified.ToString("dd.MM.yyyy  HH:mm")
            End Get
        End Property

        Public ReadOnly Property DateFileCreatedText As String
            Get
                EnsureFileInfoLoaded()
                If FileCreatedAt = DateTime.MinValue Then Return ""
                Return FileCreatedAt.ToString("dd.MM.yyyy  HH:mm")
            End Get
        End Property

        Public ReadOnly Property DateExifTakenText As String
            Get
                If Not _exifDateTaken.HasValue Then Return ""
                Return _exifDateTaken.Value.ToString("dd.MM.yyyy  HH:mm")
            End Get
        End Property

        Public ReadOnly Property DateExifModifiedText As String
            Get
                If Not _exifDateModified.HasValue Then Return ""
                Return _exifDateModified.Value.ToString("dd.MM.yyyy  HH:mm")
            End Get
        End Property

        Public ReadOnly Property Thumbnail As Bitmap
            Get
                If IsFolder Then Return Nothing
                Return _thumbnail
            End Get
        End Property

        ''' <summary>Reiht das Element mit niedrigster Priorität ein - wird erst abgearbeitet, wenn
        ''' die Viewport-Warteschlange leer ist (d.h. im "Ruhezustand", nicht während aktivem
        ''' Scrollen). No-op, falls das Element bereits geladen/geladen wird/schon eingereiht ist.</summary>
        Public Sub RequestBackgroundThumbnail()
            If IsFolder Then Return
            QueueThumbnail(BackgroundThumbnailPriority)
        End Sub

        ''' <summary>Reiht alle Bilder eines Ordners/Filmstreifens mit Hintergrund-Priorität ein,
        ''' damit sie im Ruhezustand nach und nach Thumbnails bekommen - auch solche, die nie in
        ''' die Nähe des sichtbaren Bereichs gescrollt wurden.</summary>
        Public Shared Sub QueueBackgroundThumbnails(items As IEnumerable(Of ImageItem))
            If items Is Nothing Then Return
            ' Rückwärts einreihen: die Hintergrund-Warteschlange ist LIFO, damit Elemente, die
            ' zuletzt weggescrollt wurden, bevorzugt behandelt werden. Für den initialen Füllvorgang
            ' eines ganzen Ordners soll aber vorne im Ordner begonnen werden, daher umgekehrt.
            For Each item In items.Reverse()
                item?.RequestBackgroundThumbnail()
            Next
        End Sub

        Private Sub QueueThumbnail(priority As Integer)
            Dim state = Volatile.Read(_thumbState)
            If state = 2 Then
                TouchResident(Me)
                Return
            End If
            ' Already in queue at background priority and no upgrade requested — skip the lock
            If state = 1 AndAlso priority = BackgroundThumbnailPriority Then Return

            SyncLock _thumbnailQueueLock
                If _thumbState = 2 Then
                    TouchResident(Me)
                    Return
                End If
                _evictThumbnailAfterLoad = False
                If _thumbState = 0 Then
                    _thumbState = 1
                    If priority >= ViewportThumbnailPriority Then
                        _inViewportQueue = True
                        _inBackgroundQueue = False
                        _viewportQueue.Add(Me)
                    Else
                        _inViewportQueue = False
                        _inBackgroundQueue = True
                        _backgroundQueue.Add(Me)
                    End If
                ElseIf _thumbState = 1 AndAlso priority >= ViewportThumbnailPriority AndAlso _inBackgroundQueue Then
                    ' Upgrade from background queue to viewport queue
                    _backgroundQueue.Remove(Me)
                    _inViewportQueue = True
                    _inBackgroundQueue = False
                    _viewportQueue.Add(Me)
                ElseIf _thumbState = 1 AndAlso priority >= ViewportThumbnailPriority AndAlso _inViewportQueue Then
                    _viewportQueue.Remove(Me)
                    _viewportQueue.Add(Me)
                End If
                StartThumbnailWorkersLocked()
            End SyncLock
        End Sub

        Public Shared Sub SetViewportThumbnailRequests(items As IEnumerable(Of ImageItem))
            If items Is Nothing Then Return

            SyncLock _thumbnailQueueLock
                Dim requested = items.
                    Where(Function(i) i IsNot Nothing AndAlso i.IsImage).
                    Distinct().
                    ToList()
                Dim requestedSet = New HashSet(Of ImageItem)(requested)

                For Each queued In _viewportQueue.ToList()
                    If requestedSet.Contains(queued) Then Continue For
                    queued._inViewportQueue = False
                    If Not queued._inBackgroundQueue AndAlso Not queued._isThumbnailLoading AndAlso queued._thumbState = 1 Then
                        ' Nicht komplett aus jeder Warteschlange werfen (state=0) - das würde das
                        ' Element verwaisen lassen, bis es exakt wieder in den strikt sichtbaren
                        ' Bereich gescrollt wird. Stattdessen mit Hintergrund-Priorität weiterführen,
                        ' damit ein schnell wieder weggescrolltes Element garantiert irgendwann vom
                        ' Background-Worker abgearbeitet wird, statt dauerhaft eine Lücke zu bleiben.
                        queued._inBackgroundQueue = True
                        _backgroundQueue.Add(queued)
                    End If
                Next
                _viewportQueue.RemoveAll(Function(i) Not requestedSet.Contains(i))

                For Each item In requested
                    item._evictThumbnailAfterLoad = False
                    If item._thumbState = 2 OrElse item._isThumbnailLoading Then Continue For

                    If item._inBackgroundQueue Then
                        _backgroundQueue.Remove(item)
                        item._inBackgroundQueue = False
                    End If

                    If item._inViewportQueue Then _viewportQueue.Remove(item)

                    item._thumbState = 1
                    item._inViewportQueue = True
                    _viewportQueue.Add(item)
                Next

                ' Erst nach der obigen Schleife anfassen, damit ein durch das Anfassen eines
                ' Items ausgelöster Verdrängungs-Durchlauf (siehe TouchResident) nicht versehentlich
                ' ein noch nicht durchlaufenes Item aus genau diesem Batch trifft.
                For Each item In requested
                    If item._thumbState = 2 Then TouchResident(item)
                Next

                StartThumbnailWorkersLocked()
            End SyncLock
        End Sub

        Private Shared Sub StartThumbnailWorkersLocked()
            While _runningThumbnailWorkers < MaxParallelThumbnailJobs AndAlso
                  (_viewportQueue.Count > 0 OrElse _backgroundQueue.Count > 0)
                _runningThumbnailWorkers += 1
                Task.Run(AddressOf RunThumbnailWorkerAsync)
            End While
        End Sub

        Private Shared Async Function RunThumbnailWorkerAsync() As Task
            While True
                Dim item As ImageItem = Nothing
                Dim isBackgroundItem As Boolean = False
                SyncLock _thumbnailQueueLock
                    If _viewportQueue.Count > 0 Then
                        ' LIFO: most recently requested viewport item first
                        item = _viewportQueue(_viewportQueue.Count - 1)
                        _viewportQueue.RemoveAt(_viewportQueue.Count - 1)
                        item._inViewportQueue = False
                        item._isThumbnailLoading = True
                    ElseIf _backgroundQueue.Count > 0 AndAlso _runningBackgroundWorkers < MaxConcurrentBackgroundJobs Then
                        ' LIFO: most recently accessed background item first
                        item = _backgroundQueue(_backgroundQueue.Count - 1)
                        _backgroundQueue.RemoveAt(_backgroundQueue.Count - 1)
                        item._inBackgroundQueue = False
                        item._isThumbnailLoading = True
                        isBackgroundItem = True
                        _runningBackgroundWorkers += 1
                    Else
                        ' Entweder ist nichts mehr zu tun, oder die Hintergrund-Kapazität ist
                        ' bereits ausgeschöpft - dieser Worker beendet sich, damit er nicht
                        ' unnötig Kapazität blockiert. StartThumbnailWorkersLocked startet bei
                        ' Bedarf sofort einen neuen, sobald wieder Viewport-Kapazität frei wird.
                        _runningThumbnailWorkers -= 1
                        Return
                    End If
                End SyncLock

                Try
                    Await item.LoadThumbnailAsync()
                Catch
                End Try

                item.ApplyDeferredEviction()
                SyncLock _thumbnailQueueLock
                    item._isThumbnailLoading = False
                    If isBackgroundItem Then _runningBackgroundWorkers -= 1
                End SyncLock
            End While
        End Function

        ' Günstiger, unsynchronisierter Best-Effort-Check (kein Lock nötig - im schlimmsten Fall
        ' wird einmal unnötig weitergearbeitet, nie fälschlich abgebrochen). Wird von
        ' ThumbnailCacheService kurz vor dem teuren Resize/Encode abgefragt, damit ein Worker eine
        ' Regenerierung für ein längst weggescrolltes Element nicht bis zum Ende durchzieht.
        Private Function IsStillWantedForThumbnail() As Boolean
            Return Not _evictThumbnailAfterLoad
        End Function

        Private Async Function LoadThumbnailAsync() As Task
            If Volatile.Read(_thumbState) = 2 Then Return
            Dim token = _thumbnailCancellationToken
            If token.IsCancellationRequested Then
                _thumbState = 0
                Return
            End If

            ' Immich-Items umgehen den dateipfad-basierten Cache vollständig und holen ihr Thumbnail
            ' über den Immich-Netz-/Diskcache. State-, TouchResident- und UI-Benachrichtigungs-Ablauf
            ' sind identisch zum lokalen Zweig, damit LRU-Verdrängung und Dispose-Race-Schutz greifen.
            If _immichAssetId IsNot Nothing Then
                Await LoadImmichThumbnailAsync(token)
                Return
            End If

            Try
                Dim bmp As Bitmap = Nothing
                Dim cachedWasExact As Boolean = False
                Try
                    If token.IsCancellationRequested Then Return
                    EnsureFileInfoLoaded()
                    Dim isExact As Boolean = False
                    bmp = ThumbnailCacheService.LoadCached(FilePath, DateModified, FileSize, True, isExact, token, _thumbnailCacheScopeId, _thumbnailCacheScopeName)
                    cachedWasExact = isExact
                    If bmp Is Nothing Then
                        bmp = ThumbnailCacheService.CreateOrUpdate(FilePath, DateModified, FileSize, token, _thumbnailCacheScopeId, _thumbnailCacheScopeName, AddressOf IsStillWantedForThumbnail)
                        cachedWasExact = True
                    End If
                Catch ex As OperationCanceledException
                    Throw
                Catch
                End Try

                If token.IsCancellationRequested Then
                    bmp?.Dispose()
                    _thumbState = 0
                    Return
                End If

                _thumbnail = bmp
                _thumbState = 2
                If bmp IsNot Nothing Then TouchResident(Me)
                Await Dispatcher.UIThread.InvokeAsync(Sub() RaisePropertyChanged(NameOf(Thumbnail)), DispatcherPriority.Background)

                If bmp IsNot Nothing AndAlso Not cachedWasExact Then
                    Dim replacement As Bitmap = Nothing
                    Try
                        If token.IsCancellationRequested Then Return
                        replacement = ThumbnailCacheService.CreateOrUpdate(FilePath, DateModified, FileSize, token, _thumbnailCacheScopeId, _thumbnailCacheScopeName, AddressOf IsStillWantedForThumbnail)
                    Catch ex As OperationCanceledException
                        Throw
                    Catch
                    End Try

                    If replacement IsNot Nothing AndAlso Not token.IsCancellationRequested Then
                        Dim old = _thumbnail
                        _thumbnail = replacement
                        Await Dispatcher.UIThread.InvokeAsync(Sub() RaisePropertyChanged(NameOf(Thumbnail)), DispatcherPriority.Background)
                        If Not Object.ReferenceEquals(old, replacement) Then old?.Dispose()
                    Else
                        replacement?.Dispose()
                    End If
                End If
            Catch ex As OperationCanceledException
                _thumbState = 0
            Catch
                _thumbState = 2
            End Try
        End Function

        ''' <summary>Lädt das Thumbnail eines Immich-Assets. Netz-/Diskcache und Dekodierung erledigt
        ''' der ImmichService; hier läuft nur der zu <see cref="LoadThumbnailAsync"/> identische State-/
        ''' Benachrichtigungs-Ablauf, damit das Ergebnis genauso resident/verdrängbar ist wie ein
        ''' lokales Thumbnail. Ein Nothing-Ergebnis (Server nicht erreichbar) zählt bewusst als
        ''' "fertig" (State 2), um Wiederhol-Stürme gegen einen ausgefallenen Server zu vermeiden.</summary>
        Private Async Function LoadImmichThumbnailAsync(token As CancellationToken) As Task
            Try
                Dim bmp = Await ImmichService.LoadThumbnailBitmapAsync(_immichAssetId, ImmichService.ThumbnailSize, token)
                If token.IsCancellationRequested Then
                    bmp?.Dispose()
                    _thumbState = 0
                    Return
                End If
                _thumbnail = bmp
                _thumbState = 2
                If bmp IsNot Nothing Then TouchResident(Me)
                Await Dispatcher.UIThread.InvokeAsync(Sub() RaisePropertyChanged(NameOf(Thumbnail)), DispatcherPriority.Background)
            Catch ex As OperationCanceledException
                _thumbState = 0
            Catch
                _thumbState = 2
            End Try
            ' An den (viewport-priorisierten) Thumbnail-Ladeweg gekoppelt: sichtbare Bilder bekommen ihre
            ' Detaildaten (Dateigröße/Rating/Kamera/Tags) zuerst, statt eager 25.000 Assets auf einmal.
            RequestImmichDetailOnce()
        End Function

        ''' <summary>Holt einmalig die Detail-Metadaten des Immich-Assets nach (GET /api/assets/{id}) und
        ''' überträgt sie auf das Item. Nicht blockierend (eigener Task, damit der Thumbnail-Worker frei
        ''' bleibt) und je Item nur einmal (CompareExchange-Gate).</summary>
        Private Sub RequestImmichDetailOnce()
            If _immichAssetId Is Nothing Then Return
            If Interlocked.CompareExchange(_immichDetailState, 1, 0) <> 0 Then Return
            Dim token = _thumbnailCancellationToken
            Dim assetId = _immichAssetId
            Dim updatedAt = _immichUpdatedAt
            Dim ignored = Task.Run(Async Function()
                                       Try
                                           If token.IsCancellationRequested Then Return
                                           Dim detail = Await ImmichService.GetAssetDetailCachedAsync(assetId, updatedAt, token).ConfigureAwait(False)
                                           If detail Is Nothing OrElse token.IsCancellationRequested Then Return
                                           Await Dispatcher.UIThread.InvokeAsync(
                                               Sub() ApplyImmichDetail(detail.FileSizeBytes, detail.Rating, detail.Camera,
                                                                       detail.Iso, detail.Aperture, detail.ExifDateTaken, detail.Tags))
                                       Catch
                                       End Try
                                   End Function)
        End Sub

        ''' Ein Item, das während des Ladens den Sichtbereich verlassen hat (_evictThumbnailAfterLoad),
        ''' wird nach Abschluss nicht mehr verworfen, sondern - wie jedes andere fertig geladene
        ''' Thumbnail - einfach resident (siehe TouchResident/MaxResidentThumbnails). Die UI wurde
        ''' bereits beim Setzen von _thumbState=2 in LoadThumbnailAsync benachrichtigt, ein erneutes
        ''' RaisePropertyChanged ist hier daher nicht mehr nötig.
        Private Sub ApplyDeferredEviction()
            Dim becameResident As Boolean = False
            SyncLock _thumbnailQueueLock
                If _evictThumbnailAfterLoad AndAlso _thumbState = 2 Then becameResident = True
                _evictThumbnailAfterLoad = False
            End SyncLock
            If becameResident Then TouchResident(Me)
        End Sub

        ''' Storniert bei Verlassen des Sichtfensters nur noch angeforderte, aber noch nicht fertig
        ''' geladene Elemente - ein bereits geladenes Thumbnail (_thumbState=2) bleibt resident und
        ''' wird ausschließlich über den globalen LRU-Cache (TouchResident/MaxResidentThumbnails)
        ''' verdrängt, damit erneutes Scrollen zu bereits gesehenen Bildern keinen Re-Decode mehr
        ''' auslöst. Safe to call concurrently — does not touch viewport-queue items.
        Public Sub EvictThumbnail()
            SyncLock _thumbnailQueueLock
                If _thumbState = 1 AndAlso _inBackgroundQueue Then
                    _backgroundQueue.Remove(Me)
                    _inBackgroundQueue = False
                    _thumbState = 0
                ElseIf _thumbState = 1 AndAlso _inViewportQueue Then
                    _viewportQueue.Remove(Me)
                    _inViewportQueue = False
                    _thumbState = 0
                ElseIf _thumbState = 1 AndAlso _isThumbnailLoading Then
                    _evictThumbnailAfterLoad = True
                End If
            End SyncLock
        End Sub

        Public Sub ClearThumbnail()
            Dim bmp = _thumbnail
            _thumbnail = Nothing
            _inViewportQueue = False
            _inBackgroundQueue = False
            _isThumbnailLoading = False
            _evictThumbnailAfterLoad = False
            _thumbState = 0
            UntrackResident()
            ' Siehe TouchResident: Dispose erst nach der UI-Benachrichtigung, damit ein noch an
            ' dieses Bitmap gebundenes Image.Source nicht während eines Layoutdurchlaufs auf ein
            ' bereits disposed Bitmap zugreift.
            If bmp IsNot Nothing Then
                Dispatcher.UIThread.Post(Sub()
                                              RaisePropertyChanged(NameOf(Thumbnail))
                                              bmp.Dispose()
                                          End Sub, DispatcherPriority.Background)
            End If
        End Sub
    End Class

End Namespace
