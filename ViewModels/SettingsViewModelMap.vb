Imports System
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia.Threading
Imports ReactiveUI
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Die Einstellungen der Kartenansicht: der Schalter samt Rückfrage, die
    ''' Kacheladresse und das Leeren des Kartenspeichers.</summary>
    Partial Public Class SettingsViewModel

        Private _mapViewEnabled As Boolean = False
        Private _savedMapViewEnabled As Boolean = False
        Private _mapTileUrl As String = MapTileService.DefaultTileUrl
        Private _savedMapTileUrl As String = MapTileService.DefaultTileUrl
        Private _mapTileUrlError As String = ""
        Private _mapCacheMessage As String = ""
        Private _mapConsentPending As Boolean

        Public ReadOnly Property ResetMapTileUrlCommand As ICommand =
            ReactiveCommand.Create(Sub() MapTileUrl = MapTileService.DefaultTileUrl)

        Public ReadOnly Property ClearMapCacheCommand As ICommand =
            ReactiveCommand.CreateFromTask(AddressOf ClearMapCacheAsync)

        ''' <summary>Kartenansicht in der Galerie. EINSCHALTEN GEHT NUR ÜBER DIE RÜCKFRAGE: der
        ''' Schalter springt zurück, bis sie bestätigt ist. Ausschalten geht ohne.</summary>
        Public Property MapViewEnabled As Boolean
            Get
                Return _mapViewEnabled
            End Get
            Set(value As Boolean)
                If _mapViewEnabled = value Then Return
                If value Then
                    ' Der Schalter hat sich schon umgelegt; er muss zurück, und zwar erst nach
                    ' dem laufenden Bindungsdurchgang, sonst übergeht Avalonia die Meldung.
                    Dispatcher.UIThread.Post(Sub() Me.RaisePropertyChanged(NameOf(MapViewEnabled)))
                    Dim ignored = AskAndEnableMapViewAsync()
                    Return
                End If
                ApplyMapViewEnabled(False)
            End Set
        End Property

        Private Async Function AskAndEnableMapViewAsync() As Task
            If _mapConsentPending OrElse _mainVm Is Nothing Then Return
            _mapConsentPending = True
            Try
                Dim confirmed = Await _mainVm.ShowConfirmAsync(
                    LocalizationService.T("Kartenansicht einschalten?"),
                    LocalizationService.T("Die Karte lädt ihre Kacheln von einem Kachelserver im Internet, ab Werk von OpenStreetMap. Dabei erfährt der Server, welchen Ausschnitt Sie ansehen, und damit, wo Ihre Fotos entstanden sind, dazu Ihre IP-Adresse. Die Fotos selbst und ihre Koordinaten verlassen das Gerät nicht.") &
                        vbLf & vbLf &
                        LocalizationService.T("Einmal geladene Kacheln bleiben auf diesem Gerät gespeichert, bis Sie den Kartenspeicher leeren."),
                    "Einschalten", "Abbrechen")
                If confirmed Then ApplyMapViewEnabled(True)
            Catch ex As Exception
                DiagnosticLogService.LogException("Settings.MapViewConsent", ex)
            Finally
                _mapConsentPending = False
            End Try
        End Function

        ''' <summary>Setzt den Schalter ohne Rückfrage. Nur für Ausschalten, Zurücknehmen und
        ''' Werkseinstellung - und für das Ja der Rückfrage selbst.</summary>
        Private Sub ApplyMapViewEnabled(value As Boolean)
            If _mapViewEnabled = value Then Return
            Me.RaiseAndSetIfChanged(_mapViewEnabled, value, NameOf(MapViewEnabled))
            SaveMapSettings()
        End Sub

        ''' <summary>Die Kacheladresse. Gespeichert wird nur eine gültige; eine ungültige bleibt im
        ''' Feld stehen, darunter steht, was fehlt.</summary>
        Public Property MapTileUrl As String
            Get
                Return _mapTileUrl
            End Get
            Set(value As String)
                Dim text = If(value, "").Trim()
                If String.Equals(_mapTileUrl, text, StringComparison.Ordinal) Then Return
                Me.RaiseAndSetIfChanged(_mapTileUrl, text)
                If MapTileService.IsValidTemplate(text) Then
                    MapTileUrlError = ""
                    SaveMapSettings()
                Else
                    MapTileUrlError = LocalizationService.T("Die Adresse muss mit http:// oder https:// beginnen und {z}, {x} und {y} enthalten.")
                End If
            End Set
        End Property

        Public Property MapTileUrlError As String
            Get
                Return _mapTileUrlError
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_mapTileUrlError, value)
            End Set
        End Property

        ''' <summary>Wo die Kacheln liegen: ein eigener Ordner im Datenordner der Anwendung, unter
        ''' Linux ~/.local/share/FerrumPix/MapTileCache.</summary>
        Public ReadOnly Property MapCacheFolder As String
            Get
                Return MapTileService.DefaultCacheRoot
            End Get
        End Property

        Public Property MapCacheMessage As String
            Get
                Return _mapCacheMessage
            End Get
            Private Set(value As String)
                Me.RaiseAndSetIfChanged(_mapCacheMessage, value)
            End Set
        End Property

        Private Sub LoadMapSettings(settings As AppSettings)
            _mapViewEnabled = settings.MapViewEnabled
            _mapTileUrl = MapTileService.NormalizeTemplate(settings.MapTileUrl)
            _mapTileUrlError = ""
        End Sub

        Private Sub SnapshotMapSettings()
            _savedMapViewEnabled = _mapViewEnabled
            _savedMapTileUrl = _mapTileUrl
        End Sub

        Private Sub RestoreMapSettings()
            ApplyMapViewEnabled(_savedMapViewEnabled)
            MapTileUrl = _savedMapTileUrl
        End Sub

        Private Sub ResetMapSettings()
            ApplyMapViewEnabled(False)
            MapTileUrl = MapTileService.DefaultTileUrl
        End Sub

        Private Sub SaveMapSettings()
            Dim enabled = _mapViewEnabled
            Dim url = MapTileService.NormalizeTemplate(_mapTileUrl)
            AppSettingsService.Update(Sub(s)
                                          s.MapViewEnabled = enabled
                                          s.MapTileUrl = url
                                      End Sub)
            _mainVm?.Gallery?.RefreshMapSettings()
        End Sub

        Private Async Function ClearMapCacheAsync() As Task
            MapCacheMessage = LocalizationService.T("Wird geleert…")
            Dim removed = Await Task.Run(Function() MapTileService.Instance.ClearCache())
            MapCacheMessage = String.Format(LocalizationService.T("{0} Datei(en) entfernt"), removed)
        End Function

    End Class

End Namespace
