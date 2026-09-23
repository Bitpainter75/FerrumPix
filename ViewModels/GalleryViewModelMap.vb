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

        ''' <summary>Die Bilder des angeklickten Haeufchens, im Streifen unter der Karte. Es sind
        ''' dieselben Elemente wie in der Galerie: Auswahl, Infopanel, Kontextmenue und Ziehen
        ''' laufen deshalb ueber die Wege der Kacheln.</summary>
        Public ReadOnly Property MapSelection As New BulkObservableCollection(Of ImageItem)()

        Private _mapStripNavDebouncer As FilmstripNavigationDebouncer

        ''' <summary>Das ausgewaehlte Bild im Streifen, -1 wenn keines darin liegt. Der Streifen
        ''' markiert es und rollt es ins Bild, wie der Filmstreifen das aktuelle Bild.</summary>
        Public ReadOnly Property MapStripIndex As Integer
            Get
                If _selectedItem Is Nothing Then Return -1
                Return MapSelection.IndexOf(_selectedItem)
            End Get
        End Property

        ''' <summary>Das Mausrad ueber dem Streifen blaettert wie im Filmstreifen von Betrachter und
        ''' Editor: es waehlt das naechste oder vorige Bild aus, und das Infopanel folgt.</summary>
        Public Sub NavigateMapStripByWheel(deltaY As Double)
            If MapSelection.Count = 0 OrElse deltaY = 0 Then Return
            ' Anders als im Betrachter gibt es hier nicht immer ein aktuelles Bild. Dann beginnt
            ' das Rad am Anfang (nach unten) oder am Ende (nach oben), statt eines zu ueberspringen.
            If MapStripIndex < 0 Then
                SelectOnly(If(deltaY < 0, MapSelection(0), MapSelection(MapSelection.Count - 1)))
                Return
            End If
            If _mapStripNavDebouncer Is Nothing Then
                _mapStripNavDebouncer = New FilmstripNavigationDebouncer(
                    wrapAround:=True,
                    getCurrentIndex:=Function() MapStripIndex,
                    getCount:=Function() MapSelection.Count,
                    commit:=Function(idx)
                                If idx >= 0 AndAlso idx < MapSelection.Count Then SelectOnly(MapSelection(idx))
                                Return Task.CompletedTask
                            End Function)
            End If
            _mapStripNavDebouncer.QueueWheelDelta(deltaY)
        End Sub

        ' Dasselbe Aussehen wie der Filmstreifen in Betrachter und Editor, aus denselben
        ' Einstellungen. MainWindowViewModel meldet sie bei einer Aenderung neu.

        Public ReadOnly Property ShowFilmstripItemBadges As Boolean
            Get
                Return _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing AndAlso
                       _mainVm.Settings.FilmstripItemBadgesVisible
            End Get
        End Property

        Public ReadOnly Property FilmstripTilesAreFlat As Boolean
            Get
                Return _mainVm IsNot Nothing AndAlso _mainVm.Settings IsNot Nothing AndAlso
                       Not _mainVm.Settings.FilmstripTileFrame
            End Get
        End Property

        Public ReadOnly Property FilmstripImageCornerRadius As Avalonia.CornerRadius
            Get
                Return If(FilmstripTilesAreFlat, New Avalonia.CornerRadius(0), New Avalonia.CornerRadius(6))
            End Get
        End Property

        Public ReadOnly Property ShowMapClusterCommand As ICommand =
            ReactiveCommand.Create(Of IReadOnlyList(Of ImageItem))(AddressOf ShowMapCluster)

        Public ReadOnly Property CloseMapSelectionCommand As ICommand =
            ReactiveCommand.Create(AddressOf CloseMapSelection)

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
                ' Solange der Metadatenlauf arbeitet, stehen die Aufnahmeorte noch nicht im Katalog.
                ' Ohne diesen Satz sah man lange eine leere Weltkarte und wusste nicht, dass gerade
                ' eingelesen wird.
                If _mapIndexTotal > 0 Then
                    Return String.Format(LocalizationService.T("Aufnahmeorte werden eingelesen: {0} von {1} Bildern, {2} bisher auf der Karte"),
                                         _mapIndexDone, _mapIndexTotal, _mapPoints.Count)
                End If
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

        ' Der Fortschritt des Metadatenlaufs, wie ihn die Karte zuletzt gesehen hat.
        Private _mapIndexTotal As Integer
        Private _mapIndexDone As Integer
        Private _mapIndexRefreshAt As DateTime = DateTime.MinValue

        ''' <summary>Wie oft die Karte neu rechnet, solange eingelesen wird. Jede Runde fragt den
        ''' Katalog nach allen Bildern der Liste; oefter als das waere Last ohne sichtbaren Gewinn.</summary>
        Private Shared ReadOnly MapIndexRefreshInterval As TimeSpan = TimeSpan.FromSeconds(3)

        ''' <summary>Vom Zeitgeber der Fusszeile gerufen, mit dem Stand des Metadatenlaufs. Die
        ''' Karte liest ihre Koordinaten aus dem Katalog, und der fuellt sich erst durch diesen Lauf.
        ''' Neu gerechnet wurde vorher nur, wenn sich die Bilderliste aenderte, also meist erst am
        ''' Ende: die Punkte kamen spaet und auf einen Schlag. Jetzt kommen sie in Abstaenden nach,
        ''' und am Ende einmal vollstaendig.</summary>
        Private Sub UpdateMapIndexProgress(total As Integer, done As Integer)
            Dim wasRunning = _mapIndexTotal > 0
            If total = _mapIndexTotal AndAlso done = _mapIndexDone Then Return
            _mapIndexTotal = total
            _mapIndexDone = done
            Me.RaisePropertyChanged(NameOf(MapSummaryText))
            If Not IsMapView Then Return
            If total = 0 Then
                If wasRunning Then RefreshMapPoints()
                Return
            End If
            Dim now = DateTime.UtcNow
            If now - _mapIndexRefreshAt < MapIndexRefreshInterval Then Return
            _mapIndexRefreshAt = now
            RefreshMapPoints()
        End Sub

        ''' <summary>Der Streifen haelt Bilder der Galerie. Aendert sich deren Liste, fliegt
        ''' heraus, was nicht mehr darin steht; nach einem Ordnerwechsel ist das alles, und der
        ''' Streifen schliesst sich. Vorher blieben die Bilder des vorigen Ordners als leere Kacheln
        ''' stehen.</summary>
        Private Sub PruneMapSelection()
            If MapSelection.Count = 0 Then Return
            Dim present As New HashSet(Of ImageItem)(Items.Where(Function(i) i IsNot Nothing))
            Dim keep = MapSelection.Where(Function(i) present.Contains(i)).ToList()
            If keep.Count = MapSelection.Count Then Return
            If keep.Count = 0 Then
                CloseMapSelection()
                Return
            End If
            MapSelection.ReplaceAll(keep)
            Me.RaisePropertyChanged(NameOf(MapStripIndex))
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
            Me.RaisePropertyChanged(NameOf(MapStripIndex))
            ' Die Vorschaubilder fragt der Streifen selbst fuer sein Sichtfenster an, ueber
            ' denselben Weg wie der Filmstreifen (FilmstripInteractionController).
        End Sub

        Private Sub CloseMapSelection()
            If MapSelection.Count = 0 Then Return
            MapSelection.ReplaceAll(Array.Empty(Of ImageItem)())
            Me.RaisePropertyChanged(NameOf(HasMapSelection))
            Me.RaisePropertyChanged(NameOf(MapStripIndex))
        End Sub

        ''' <summary>Öffnet das Bild im Betrachter; geblättert wird durch die Bilder des
        ''' angeklickten Häufchens. Der Doppelklick im Streifen kommt hier an.</summary>
        Public Sub OpenMapItem(item As ImageItem)
            If item Is Nothing OrElse String.IsNullOrEmpty(item.FilePath) Then Return
            Dim paths = MapSelection.Select(Function(i) i.FilePath).ToList()
            If Not paths.Contains(item.FilePath) Then paths.Insert(0, item.FilePath)
            _mainVm?.OpenImageInViewer(item.FilePath, paths,
                                       cacheScopeId:=CurrentThumbnailCacheScopeId,
                                       cacheScopeName:=CurrentThumbnailCacheScopeName)
        End Sub

    End Class

End Namespace
