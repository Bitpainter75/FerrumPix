Imports System
Imports System.ComponentModel
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia.Threading
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ' Bewegtbild im Betrachter: der kurze Film eines Live Photos oder eines Motion Photos.
    '
    ' DER BETRACHTER ZEIGT SO EIN BILD ALS STANDBILD, und ein Knopf in der Fussleiste spielt den Film
    ' EINMAL ab. Danach steht wieder das Standbild. Dafuer gibt es einen eigenen Zustand
    ' (IsMotionPlaying) UEBER dem Standbild: die Datei bleibt ein Bild (IsVideoFile ist und bleibt
    ' falsch, Bearbeiten, Bewerten, Drehen gelten weiter ihr), nur die Videoflaeche liegt solange
    ' darueber. Wer wissen will, ob die Videoflaeche zu sehen ist, fragt ShowsVideoStage; wer wissen
    ' will, ob die DATEI ein Film ist, weiter IsVideoFile.
    '
    ' WOHER DER FILM KOMMT:
    '   Live Photo    die .mov daneben (LivePhotoService.FindPartnerPath), als gewoehnliche Datei.
    '   Motion Photo  direkt aus dem JPEG, ueber eine Adresse, die dem Spieler den Ausschnitt nennt
    '                 (MotionPhotoService.SubfileAddress). Versatz und Laenge werden bei JEDEM
    '                 Abspielen neu bestimmt: jedes Schreiben von Metadaten verschiebt sie.
    '
    ' DER RUECKFALL: nicht jede Fassung des Spielers muss den Ausschnitt lesen koennen. Meldet er
    ' einen Fehler oder beginnt der Film nicht, wird der Filmteil in den Cache der Anwendung
    ' geschrieben (nie neben das Foto, eine Datei zur Zeit) und als Datei abgespielt. Aufgeraeumt
    ' wird beim Bildwechsel, nach dem Abspielen und beim Start der Anwendung.
    '
    ' IN DER DIASHOW UND IM VERGLEICH wird nicht abgespielt; der Knopf ist dort nicht da.
    ' Der Ton folgt dem Stummschalter der Videoleiste, wie bei jedem Film.
    Partial Public Class ViewerViewModel

        Private _motionKind As MotionKind = MotionKind.None
        ''' Live Photo: der Film neben dem Bild. Bei einem Motion Photo Nothing.
        Private _motionVideoPath As String
        Private _motionProbeToken As Integer
        Private _isMotionPlaying As Boolean
        ''' Zaehlt die Abspielvorgaenge: was nach einem Warten noch zu einem alten gehoert, verfaellt.
        Private _motionPlayToken As Integer
        ''' Was der Spieler gerade fuer das Bewegtbild bekommen hat: Adresse oder Dateipfad.
        Private _motionSource As String
        ''' Der Rueckfall in den Cache ist fuer diesen Abspielvorgang schon versucht (oder es gibt
        ''' keinen: ein Live Photo ist bereits eine Datei).
        Private _motionFallbackTried As Boolean
        ''' Im Cache liegt ein herausgeschriebener Film, der wieder weg muss.
        Private _motionClipWritten As Boolean
        Private _motionWired As Boolean
        Private _playMotionCommand As ICommand

        ''' <summary>So lange darf der Film brauchen, bis er seine Laufzeit meldet. Danach gilt der
        ''' Weg als gescheitert, und der Rueckfall ist dran. Eine Vorgabe fuer den Fall, dass der
        ''' Spieler gar nichts meldet; einen Fehler meldet er sofort.</summary>
        Friend Shared MotionStartTimeoutMilliseconds As Integer = 4000

        ''' <summary>Laeuft gerade der Film des gezeigten Bildes? Dann liegt die Videoflaeche ueber
        ''' dem Standbild.</summary>
        Public ReadOnly Property IsMotionPlaying As Boolean
            Get
                Return _isMotionPlaying
            End Get
        End Property

        ''' <summary>Ist die Videoflaeche zu sehen: weil die Datei ein Film ist, oder weil der Film
        ''' eines Bewegtbilds laeuft. Daran haengen die Flaeche, ihre Leiste und der Spieler.</summary>
        Public ReadOnly Property ShowsVideoStage As Boolean
            Get
                ' Wer hier fragt, will auch die Meldung: sie haengt an der von IsVideoFile.
                EnsureMotionWiring()
                Return IsVideoFile OrElse _isMotionPlaying
            End Get
        End Property

        ''' <summary>Gehoert zum gezeigten Bild ein Film, der sich abspielen laesst? Daran haengt der
        ''' Knopf in der Fussleiste. Nicht in der Diashow (auch nicht angehalten), nicht im
        ''' Vergleich, und nur, wo ueberhaupt abgespielt werden kann.</summary>
        Public ReadOnly Property HasMotionClip As Boolean
            Get
                Return _motionKind <> MotionKind.None AndAlso Not _slideshowSession AndAlso
                       Not _isCompareMode AndAlso IsVideoPlaybackAvailable
            End Get
        End Property

        ''' <summary>Der Hinweis am Knopf, je nachdem, ob der Film laeuft.</summary>
        Public ReadOnly Property MotionButtonTip As String
            Get
                Return If(_isMotionPlaying,
                          LocalizationService.T("Film anhalten und zum Bild zurück"),
                          LocalizationService.T("Film zu diesem Bild abspielen"))
            End Get
        End Property

        ''' <summary>Spielt den Film des gezeigten Bildes einmal ab; laeuft er schon, haelt der Knopf
        ''' ihn an und zeigt wieder das Bild.</summary>
        Public ReadOnly Property PlayMotionCommand As ICommand
            Get
                If _playMotionCommand Is Nothing Then _playMotionCommand = ReactiveCommand.Create(Sub() ToggleMotionPlayback())
                Return _playMotionCommand
            End Get
        End Property

        ''' <summary>Fuer die Pruefungen: was der Spieler zuletzt fuer das Bewegtbild bekam.</summary>
        Friend ReadOnly Property MotionSource As String
            Get
                Return _motionSource
            End Get
        End Property

        Private Sub RaiseMotionState()
            Me.RaisePropertyChanged(NameOf(IsMotionPlaying))
            Me.RaisePropertyChanged(NameOf(MotionButtonTip))
            Me.RaisePropertyChanged(NameOf(ShowVideoSurface))
            ' Zuletzt: daran haengt die Ansicht den Spieler an die Flaeche, und dann muss der
            ' uebrige Stand schon gelten.
            Me.RaisePropertyChanged(NameOf(ShowsVideoStage))
        End Sub

        ''' <summary>Diashow und Vergleich schalten den Knopf weg und beenden einen laufenden Film,
        ''' und jede Meldung von IsVideoFile geht als ShowsVideoStage weiter: die Ansicht haengt an
        ''' ShowsVideoStage, gemeldet wird beim Bildwechsel aber IsVideoFile. Angehaengt bei der
        ''' ersten Frage und beim ersten Bild statt im Konstruktor.</summary>
        Private Sub EnsureMotionWiring()
            If _motionWired Then Return
            _motionWired = True
            AddHandler Me.PropertyChanged, AddressOf OnOwnPropertyChangedForMotion
        End Sub

        Private Sub OnOwnPropertyChangedForMotion(sender As Object, e As PropertyChangedEventArgs)
            Select Case e.PropertyName
                Case NameOf(IsSlideshowPlaying), NameOf(IsSlideshowPaused), NameOf(IsCompareMode)
                    If _isMotionPlaying AndAlso (_slideshowSession OrElse _isCompareMode) Then EndMotionPlayback(stopPlayer:=True)
                    Me.RaisePropertyChanged(NameOf(HasMotionClip))
                Case NameOf(IsVideoPlaybackAvailable)
                    ' Der Aufbau des Spielers ist gescheitert: es laeuft nichts, also zurueck zum Bild.
                    If _isMotionPlaying AndAlso Not IsVideoPlaybackAvailable Then EndMotionPlayback(stopPlayer:=False)
                    Me.RaisePropertyChanged(NameOf(HasMotionClip))
                Case NameOf(ShowVideoSurface)
                    ' Der Spieler ist weg (mpv hat sich beendet, oder er kam gar nicht zustande):
                    ' ohne ihn bliebe die Videoflaeche schwarz ueber dem Standbild stehen.
                    If _isMotionPlaying AndAlso _mediaPlayer Is Nothing Then EndMotionPlayback(stopPlayer:=False)
                Case NameOf(IsVideoFile)
                    Me.RaisePropertyChanged(NameOf(ShowsVideoStage))
            End Select
        End Sub

        ''' <summary>Jeder Bildwechsel kommt hier vorbei (LoadBitmap): ein laufender Film gehoert
        ''' zum vorigen Bild und endet, ein herausgeschriebener Film wird weggeraeumt, und fuer das
        ''' neue Bild wird nachgesehen, ob es eines mit Film ist.</summary>
        Private Sub OnMotionImageChanged()
            EnsureMotionWiring()
            EndMotionPlayback(stopPlayer:=False)
            ClearMotionClip()
            ProbeMotionForCurrentImage()
        End Sub

        Private Sub SetMotionKind(kind As MotionKind, videoPath As String)
            If _motionKind = kind AndAlso String.Equals(_motionVideoPath, videoPath, StringComparison.Ordinal) Then Return
            _motionKind = kind
            _motionVideoPath = videoPath
            Me.RaisePropertyChanged(NameOf(HasMotionClip))
        End Sub

        ''' <summary>Fragt im Hintergrund, ob das gezeigte Bild ein Motion Photo ist oder das Bild
        ''' eines Live Photos, dessen Film daneben liegt. Der Katalog antwortet, wo seine Zeile zur
        ''' Datei passt, sonst die Datei (LivePhotoService.IdentityOf). Ein spaetes Ergebnis fuer
        ''' ein Bild, das nicht mehr gezeigt wird, verfaellt.</summary>
        Private Sub ProbeMotionForCurrentImage()
            Dim token = Interlocked.Increment(_motionProbeToken)
            Dim path = _currentImagePath
            SetMotionKind(MotionKind.None, Nothing)
            If String.IsNullOrEmpty(path) OrElse Not LivePhotoService.IsImageHalf(path) Then Return
            Task.Run(Sub()
                         Dim kind = MotionKind.None
                         Dim videoPath As String = Nothing
                         Try
                             Dim identity = LivePhotoService.IdentityOf(path)
                             If identity.Kind = MotionKind.MotionPhoto Then
                                 kind = MotionKind.MotionPhoto
                             ElseIf identity.Kind = MotionKind.LivePhotoImage Then
                                 videoPath = LivePhotoService.FindPartnerPath(path)
                                 If videoPath IsNot Nothing Then kind = MotionKind.LivePhotoImage
                             End If
                         Catch ex As Exception
                             DiagnosticLogService.LogException("Viewer.ProbeMotion", ex)
                         End Try
                         If kind = MotionKind.None Then Return
                         Dispatcher.UIThread.Post(Sub()
                                                      If token <> _motionProbeToken Then Return
                                                      If Not String.Equals(path, _currentImagePath, PathIdentity.Comparison) Then Return
                                                      SetMotionKind(kind, videoPath)
                                                  End Sub)
                     End Sub)
        End Sub

        Private Async Sub ToggleMotionPlayback()
            Try
                If _isMotionPlaying Then
                    EndMotionPlayback(stopPlayer:=True)
                    Return
                End If
                Await StartMotionPlaybackAsync()
            Catch ex As Exception
                ' Absicherung: eine Ausnahme in einem Async Sub landet sonst beim Dispatcher
                ' und beendet den Prozess.
                DiagnosticLogService.LogException("Viewer.ToggleMotionPlayback", ex)
            End Try
        End Sub

        ''' <summary>Bestimmt die Quelle des Films und gibt sie dem Spieler. Beim Motion Photo wird
        ''' die Lage des Films JETZT an der Datei bestimmt, nicht aus einem frueheren Blick
        ''' uebernommen. True, wenn der Film angefordert ist.</summary>
        Friend Async Function StartMotionPlaybackAsync() As Task(Of Boolean)
            If Not HasMotionClip OrElse _isMotionPlaying Then Return False
            Dim path = _currentImagePath
            Dim kind = _motionKind
            Dim videoPath = _motionVideoPath
            Dim token = Interlocked.Increment(_motionPlayToken)
            Dim source = Await Task.Run(
                Function() As String
                    Try
                        If kind = MotionKind.MotionPhoto Then
                            Return MotionPhotoService.SubfileAddress(path, MotionPhotoService.Detect(path))
                        End If
                        Return If(Not String.IsNullOrEmpty(videoPath) AndAlso File.Exists(videoPath), videoPath, Nothing)
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Viewer.MotionSource", ex)
                        Return Nothing
                    End Try
                End Function)
            ' Inzwischen ein anderes Bild, ein zweiter Druck, oder Diashow und Vergleich begonnen.
            If token <> _motionPlayToken OrElse Not String.Equals(path, _currentImagePath, PathIdentity.Comparison) Then Return False
            If Not HasMotionClip Then Return False
            If source Is Nothing Then
                ' Der Film ist seit dem Nachsehen verschwunden (geloescht, die Datei neu
                ' geschrieben): dann ist es ein gewoehnliches Bild, und der Knopf geht.
                SetMotionKind(MotionKind.None, Nothing)
                StatusInfo = LocalizationService.T("Der Film zu diesem Bild ist nicht mehr da")
                Return False
            End If
            ' Nur der Weg ueber den Ausschnitt hat einen Rueckfall; die .mov ist schon eine Datei.
            _motionFallbackTried = kind <> MotionKind.MotionPhoto
            BeginMotionSource(source, token)
            Return True
        End Function

        ''' <summary>Gibt dem Spieler eine Quelle und schaltet auf "Bewegtbild laeuft". Erst der
        ''' Spieler, dann die Meldung: an ihr haengt die Ansicht den Spieler an die Flaeche, und er
        ''' muss dann schon da sein (wie beim Wechsel auf eine Videodatei).</summary>
        Private Sub BeginMotionSource(source As String, token As Integer)
            _motionSource = source
            VideoPositionSeconds = 0
            VideoDurationSeconds = 0
            _isVideoEnded = False
            ' ERZWUNGEN: derselbe Film ein zweites Mal ist hier der gewoehnliche Fall.
            LoadVideo(source, force:=True)
            If _isMotionPlaying Then
                ' Der Rueckfall: die Flaeche steht schon, also selbst anstossen, was sonst die
                ' Ansicht nach dem Anhaengen tut.
                StartPendingVideoAutoplay()
            Else
                _isMotionPlaying = True
                RaiseMotionState()
            End If
            WatchMotionStart(token, source)
        End Sub

        ''' <summary>Meldet der Film in der Frist keine Laufzeit, hat der Spieler die Quelle nicht
        ''' angenommen, ohne es zu sagen.</summary>
        Private Async Sub WatchMotionStart(token As Integer, source As String)
            Try
                Await Task.Delay(MotionStartTimeoutMilliseconds)
                If token <> _motionPlayToken OrElse Not _isMotionPlaying Then Return
                If Not String.Equals(source, _motionSource, StringComparison.Ordinal) Then Return
                If _videoDurationSeconds > 0 Then Return
                OnMotionSourceFailed()
            Catch ex As Exception
                DiagnosticLogService.LogException("Viewer.WatchMotionStart", ex)
            End Try
        End Sub

        ''' <summary>Die Quelle liess sich nicht abspielen: einmal der Rueckfall in den Cache, danach
        ''' zurueck zum Standbild mit einem Satz in der Fusszeile.</summary>
        Private Sub OnMotionSourceFailed()
            If Not _isMotionPlaying Then Return
            If Not _motionFallbackTried Then
                _motionFallbackTried = True
                DiagnosticLogService.LogAlways("VideoPlayback", "Bewegtbild: der Ausschnitt liess sich nicht laden, Rueckfall in den Cache")
                StartMotionFallback()
                Return
            End If
            EndMotionPlayback(stopPlayer:=True)
            StatusInfo = LocalizationService.T("Der Film zu diesem Bild ließ sich nicht abspielen")
        End Sub

        ''' <summary>Schreibt den Filmteil in den Cache der Anwendung und spielt die Datei ab.</summary>
        Private Async Sub StartMotionFallback()
            Try
                Dim path = _currentImagePath
                Dim token = _motionPlayToken
                Dim clip = Await Task.Run(Function() MotionPhotoService.ExtractClip(path))
                If clip IsNot Nothing Then _motionClipWritten = True
                If token <> _motionPlayToken OrElse Not _isMotionPlaying OrElse
                   Not String.Equals(path, _currentImagePath, PathIdentity.Comparison) Then
                    ' Inzwischen beendet oder weitergeblaettert: was eben geschrieben wurde, geht wieder.
                    If Not _isMotionPlaying Then ClearMotionClip()
                    Return
                End If
                If clip Is Nothing Then
                    OnMotionSourceFailed()
                    Return
                End If
                BeginMotionSource(clip, token)
            Catch ex As Exception
                DiagnosticLogService.LogException("Viewer.StartMotionFallback", ex)
            End Try
        End Sub

        ''' <summary>Der Spieler meldet das Ende einer Datei, waehrend ein Bewegtbild laeuft. Am
        ''' natuerlichen Ende geht es zurueck zum Standbild (abgespielt wird EINMAL), bei einem
        ''' Fehler greift der Rueckfall. True, wenn die Meldung damit erledigt ist.</summary>
        Private Function HandleMotionEndFile(reason As Integer) As Boolean
            If Not _isMotionPlaying Then Return False
            If reason = CInt(MpvInterop.MpvEndFileReason.Eof) Then
                EndMotionPlayback(stopPlayer:=True)
                Return True
            End If
            If reason = CInt(MpvInterop.MpvEndFileReason.Error) Then
                OnMotionSourceFailed()
                Return True
            End If
            Return False
        End Function

        ''' <summary>Zurueck zum Standbild. <paramref name="stopPlayer"/> haelt auch den Spieler an;
        ''' ohne ihn nur der Zustand, wenn der Aufrufer den Spieler ohnehin anhaelt oder gleich neu
        ''' belegt (Bildwechsel).</summary>
        Private Sub EndMotionPlayback(stopPlayer As Boolean)
            If Not _isMotionPlaying Then Return
            Interlocked.Increment(_motionPlayToken)
            _isMotionPlaying = False
            _motionSource = Nothing
            _pendingVideoAutoplay = False
            If stopPlayer Then StopVideoPlayback()
            RaiseMotionState()
            ClearMotionClip()
        End Sub

        ''' <summary>Raeumt den herausgeschriebenen Film weg, wenn einer geschrieben wurde. Liegt er
        ''' danach noch da (der Spieler haelt ihn), nimmt ihn der naechste Bildwechsel.</summary>
        Private Sub ClearMotionClip()
            If Not _motionClipWritten Then Return
            MotionPhotoService.ClearClipCache()
            Try
                Dim folder = MotionPhotoService.ClipCacheFolder
                _motionClipWritten = Directory.Exists(folder) AndAlso Directory.GetFiles(folder).Length > 0
            Catch
                _motionClipWritten = True
            End Try
        End Sub

        ''' <summary>Die andere Haelfte eines Live Photos zum gezeigten Bild oder Film, oder
        ''' Nothing. Fuer Loeschen und Umbenennen aus dem Betrachter: die Haelfte geht dort mit
        ''' (MainWindowViewModel), und steht sie als eigener Eintrag im Streifen (Stapel aus), muss
        ''' der Streifen es erfahren. Liest den Ordner; nur auf einen Befehl des Nutzers rufen.</summary>
        Private Function LivePartnerOfCurrent() As String
            If _isImmichSession OrElse String.IsNullOrEmpty(_currentImagePath) Then Return Nothing
            Return LivePhotoService.FindPartnerPath(_currentImagePath)
        End Function

        ''' <summary>Nimmt die andere Haelfte aus dem Streifen, wenn sie mit geloescht wurde.</summary>
        Private Sub DropDeletedLivePartner(partnerPath As String)
            If String.IsNullOrEmpty(partnerPath) OrElse File.Exists(partnerPath) Then Return
            Dim removedBefore = 0
            For index = _folderPaths.Count - 1 To 0 Step -1
                If Not String.Equals(VariantKey.FileOf(_folderPaths(index)), partnerPath, PathIdentity.Comparison) Then Continue For
                _folderPaths.RemoveAt(index)
                If index < _currentIndex Then removedBefore += 1
            Next
            _currentIndex -= removedBefore
        End Sub

        ''' <summary>Fuehrt den Eintrag der anderen Haelfte nach, wenn sie mit umbenannt wurde.</summary>
        Private Sub FollowRenamedLivePartner(partnerPath As String, newPath As String)
            If String.IsNullOrEmpty(partnerPath) OrElse String.IsNullOrEmpty(newPath) Then Return
            Dim renamed = LivePhotoService.PartnerTargetFor(partnerPath, newPath)
            If Not File.Exists(renamed) Then Return
            For index = 0 To _folderPaths.Count - 1
                Dim entryId As String = Nothing
                If String.Equals(VariantKey.Split(_folderPaths(index), entryId), partnerPath, PathIdentity.Comparison) Then
                    _folderPaths(index) = VariantKey.Make(renamed, entryId)
                End If
            Next
        End Sub

    End Class

End Namespace
