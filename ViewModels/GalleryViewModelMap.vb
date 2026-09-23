Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia.Threading
Imports ReactiveUI
Imports FerrumPix.Models
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Die Kartenansicht der Galerie: dieselben Bilder wie in Raster und Liste, aber nach
    ''' ihrem Aufnahmeort. Es gibt sie nur, wenn sie in den Einstellungen freigegeben ist (siehe
    ''' AppSettingsService.MapViewEnabled); die Kacheln holt MapTileService.
    '''
    ''' Die Koordinaten stehen nicht im ImageItem, sondern im Katalog. Gezeigt wird deshalb, was
    ''' der Katalog zu den Bildern der aktuellen Liste kennt; Serverbilder haben dort keinen
    ''' Eintrag und fehlen auf der Karte.</summary>
    Partial Public Class GalleryViewModel

        Public Const MapViewMode As String = "Map"

        ''' <summary>Wie lange nach der letzten Änderung der Liste gewartet wird, bevor die Karte
        ''' neu rechnet. Ein Ordner kommt in vielen kleinen Schüben an.</summary>
        Private Shared ReadOnly MapRefreshDelay As TimeSpan = TimeSpan.FromMilliseconds(300)

        Private _mapViewAvailable As Boolean = AppSettingsService.Load().MapViewEnabled
        Private _mapTileUrl As String = MapTileService.NormalizeTemplate(AppSettingsService.Load().MapTileUrl)
        Private _viewModeBeforeMap As String = "Grid"
        Private _mapPoints As IReadOnlyList(Of MapPhotoPoint) = Array.Empty(Of MapPhotoPoint)()
        Private _mapCandidateCount As Integer
        Private _mapRefreshVersion As Integer
        Private _mapRefreshTimer As DispatcherTimer
        Private _mapZoom As Double = 2

        Public ReadOnly Property MapSelection As New BulkObservableCollection(Of ImageItem)()

        Public ReadOnly Property ShowMapClusterCommand As ICommand =
            ReactiveCommand.Create(Of IReadOnlyList(Of ImageItem))(AddressOf ShowMapCluster)

        Public ReadOnly Property CloseMapSelectionCommand As ICommand =
            ReactiveCommand.Create(AddressOf CloseMapSelection)

        Public ReadOnly Property OpenMapItemCommand As ICommand =
            ReactiveCommand.Create(Of ImageItem)(AddressOf OpenMapItem)

        ''' <summary>Die Zoomstufe der Karte, gebunden an die Karte und an den Regler in der
        ''' Fußleiste, der in der Kartenansicht an die Stelle der Vorschaugröße tritt.</summary>
        Public Property MapZoom As Double
            Get
                Return _mapZoom
            End Get
            Set(value As Double)
                Me.RaiseAndSetIfChanged(_mapZoom, Math.Max(Controls.PhotoMapControl.MinZoom, Math.Min(MapTileService.MaxZoom, value)))
            End Set
        End Property

        Public ReadOnly Property MapZoomInCommand As ICommand =
            ReactiveCommand.Create(Sub() MapZoom = Math.Round(MapZoom) + 1)

        Public ReadOnly Property MapZoomOutCommand As ICommand =
            ReactiveCommand.Create(Sub() MapZoom = Math.Round(MapZoom) - 1)

        Public ReadOnly Property IsMapView As Boolean
            Get
                Return _viewMode = MapViewMode
            End Get
        End Property

        ''' <summary>Ob der Knopf für die Karte in der Leiste steht.</summary>
        Public ReadOnly Property IsMapViewAvailable As Boolean
            Get
                Return _mapViewAvailable
            End Get
        End Property

        Public ReadOnly Property MapTileUrl As String
            Get
                Return _mapTileUrl
            End Get
        End Property

        Public Property MapPoints As IReadOnlyList(Of MapPhotoPoint)
            Get
                Return _mapPoints
            End Get
            Private Set(value As IReadOnlyList(Of MapPhotoPoint))
                Me.RaiseAndSetIfChanged(_mapPoints, value)
                Me.RaisePropertyChanged(NameOf(MapSummaryText))
            End Set
        End Property

        ''' <summary>Wie viele der Bilder einen Aufnahmeort haben - damit niemand die übrigen
        ''' auf der Karte sucht.</summary>
        Public ReadOnly Property MapSummaryText As String
            Get
                If _mapCandidateCount = 0 Then Return ""
                If _mapPoints.Count = 0 Then Return LocalizationService.T("Kein Bild in dieser Ansicht hat einen Aufnahmeort.")
                Return String.Format(LocalizationService.T("{0} von {1} Bildern haben einen Aufnahmeort"),
                                     _mapPoints.Count, _mapCandidateCount)
            End Get
        End Property

        Public ReadOnly Property MapFailureText As String
            Get
                Return LocalizationService.T("Kartenkacheln konnten nicht geladen werden.")
            End Get
        End Property

        Public ReadOnly Property HasMapSelection As Boolean
            Get
                Return MapSelection.Count > 0
            End Get
        End Property

        ''' <summary>Nach einer Änderung in den Einstellungen: Freigabe und Adresse neu lesen. Wird
        ''' die Karte abgeschaltet, während sie offen ist, kehrt die Galerie zur vorigen Ansicht
        ''' zurück.</summary>
        Public Sub RefreshMapSettings()
            Dim settings = AppSettingsService.Load()
            Dim available = settings.MapViewEnabled
            Dim url = MapTileService.NormalizeTemplate(settings.MapTileUrl)
            If _mapViewAvailable <> available Then
                _mapViewAvailable = available
                Me.RaisePropertyChanged(NameOf(IsMapViewAvailable))
            End If
            If Not String.Equals(_mapTileUrl, url, StringComparison.Ordinal) Then
                _mapTileUrl = url
                Me.RaisePropertyChanged(NameOf(MapTileUrl))
            End If
            If Not available AndAlso IsMapView Then ViewMode = _viewModeBeforeMap
        End Sub

        Private Sub ScheduleMapRefresh()
            If Not IsMapView Then Return
            If _mapRefreshTimer Is Nothing Then
                _mapRefreshTimer = New DispatcherTimer With {.Interval = MapRefreshDelay}
                AddHandler _mapRefreshTimer.Tick,
                    Sub(sender As Object, e As EventArgs)
                        _mapRefreshTimer.Stop()
                        RefreshMapPoints()
                    End Sub
            End If
            _mapRefreshTimer.Stop()
            _mapRefreshTimer.Start()
        End Sub

        ''' <summary>Liest die Koordinaten der aktuellen Liste aus dem Katalog. Läuft im
        ''' Hintergrund; überholt ein neuerer Aufruf, wird das Ergebnis verworfen.</summary>
        Private Async Sub RefreshMapPoints()
            If Not IsMapView Then Return
            Dim version = Interlocked.Increment(_mapRefreshVersion)
            Dim candidates = Items.
                Where(Function(i) i IsNot Nothing AndAlso Not i.IsFolder AndAlso Not i.IsParentFolderEntry AndAlso
                                  Not i.IsGroupHeader AndAlso Not i.IsRemoteAsset AndAlso (i.IsImage OrElse i.IsVideoFile)).
                ToList()
            Try
                Dim points = Await Task.Run(Function() BuildMapPoints(candidates))
                If version <> Volatile.Read(_mapRefreshVersion) Then Return
                _mapCandidateCount = candidates.Count
                MapPoints = points
            Catch ex As Exception
                DiagnosticLogService.LogException("GalleryViewModel.RefreshMapPoints", ex)
            End Try
        End Sub

        Private Shared Function BuildMapPoints(candidates As List(Of ImageItem)) As IReadOnlyList(Of MapPhotoPoint)
            If candidates.Count = 0 Then Return Array.Empty(Of MapPhotoPoint)()
            Dim metaByPath = LibraryService.Instance.GetMetaForPaths(candidates.Select(Function(i) i.FilePath))
            Dim points As New List(Of MapPhotoPoint)()
            For Each item In candidates
                Dim meta As LibraryImageMeta = Nothing
                If Not metaByPath.TryGetValue(item.FilePath, meta) Then Continue For
                If Not meta.GpsLatitude.HasValue OrElse Not meta.GpsLongitude.HasValue Then Continue For
                Dim latitude = meta.GpsLatitude.Value
                Dim longitude = meta.GpsLongitude.Value
                If Double.IsNaN(latitude) OrElse Double.IsNaN(longitude) Then Continue For
                If Math.Abs(latitude) > 90 OrElse Math.Abs(longitude) > 180 Then Continue For
                ' Null/Null ist der Golf von Guinea und fast immer ein leeres Feld, das eine
                ' Kamera als Koordinate geschrieben hat.
                If latitude = 0 AndAlso longitude = 0 Then Continue For
                points.Add(New MapPhotoPoint(item, latitude, longitude))
            Next
            Return points
        End Function

        Private Sub ShowMapCluster(items As IReadOnlyList(Of ImageItem))
            If items Is Nothing OrElse items.Count = 0 Then Return
            MapSelection.ReplaceAll(items)
            Me.RaisePropertyChanged(NameOf(HasMapSelection))
            ' Die Galerie fragt Vorschaubilder nur für ihr eigenes Sichtfenster an; der Streifen
            ' ist keines davon.
            ImageItem.SetViewportThumbnailRequests(items.Take(40))
            ImageItem.QueueBackgroundThumbnails(items)
        End Sub

        Private Sub CloseMapSelection()
            If MapSelection.Count = 0 Then Return
            MapSelection.ReplaceAll(Array.Empty(Of ImageItem)())
            Me.RaisePropertyChanged(NameOf(HasMapSelection))
        End Sub

        ''' <summary>Öffnet das Bild im Betrachter; geblättert wird durch die Bilder des
        ''' angeklickten Häufchens.</summary>
        Private Sub OpenMapItem(item As ImageItem)
            If item Is Nothing OrElse String.IsNullOrEmpty(item.FilePath) Then Return
            Dim paths = MapSelection.Select(Function(i) i.FilePath).ToList()
            If Not paths.Contains(item.FilePath) Then paths.Insert(0, item.FilePath)
            _mainVm?.OpenImageInViewer(item.FilePath, paths,
                                       cacheScopeId:=CurrentThumbnailCacheScopeId,
                                       cacheScopeName:=CurrentThumbnailCacheScopeName)
        End Sub

    End Class

End Namespace
