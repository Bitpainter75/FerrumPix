Imports System
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia.Media.Imaging
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    ' Das Standbild eines Films, bis er zum ersten Mal laeuft.
    '
    ' Ein Video wird angehalten an seinem Anfang geladen, und viele Filme beginnen mit einer
    ' Einblendung aus Schwarz. Bis zum ersten Abspielen stand deshalb eine schwarze Flaeche da,
    ' waehrend die Galerie fuer denselben Film ein Bild zeigt. Jetzt liegt bis dahin die Kachel der
    ' Galerie ueber der Videoflaeche (dieselbe Stelle bei 10 % der Laufzeit, VideoPreviewService).
    '
    ' NUR VOR DEM ERSTEN ABSPIELEN NACH DEM LADEN. Sobald der Film laeuft (eine Zeitmeldung ueber null
    ' bei aufgehobener Pause) oder jemand an eine Stelle springt, ist das Standbild weg und kommt fuer
    ' diesen Film nicht wieder: nicht beim Zurueckspulen, nicht am Ende, nicht beim Wiederholen. Neu
    ' beginnt es erst, wenn ein anderer Film gezeigt wird, oder derselbe nach einem Bild oder einer
    ' anderen Ansicht. Nur der gewoehnliche Videoweg; das Bewegtbild eines Fotos (ViewerViewModelMotion)
    ' hat sein Standbild schon im Foto selbst.
    '
    ' WARUM NICHT DAS UEBERGANGSBILD (TransitionImage): das ueberbrueckt die Entwicklung eines Fotos,
    ' endet mit dessen Laden, haengt an einer Einstellung, die ab Werk aus ist, und liegt unter der
    ' Videoflaeche. Hier endet das Bild mit dem Abspielen und muss UEBER der Flaeche liegen. Geteilt
    ' wird der Weg zur Kachel (ThumbnailCacheService.LoadCached mit dem Bereich der Galerie).
    '
    ' DIE NATIVE FLAECHE: unter Linux und Windows haengt mpv sein Bild in ein natives Fenster, und das
    ' liegt ueber allem, was Avalonia zeichnet. Solange das Standbild steht, schiebt die Ansicht die
    ' Flaeche deshalb aus dem sichtbaren Bereich (ViewerView.UpdateVideoPosterCover), statt sie
    ' auszublenden: eine unsichtbare Flaeche bekaeme keine Groesse, und der Spieler wird erst an eine
    ' Flaeche mit Groesse gehaengt.
    Partial Public Class ViewerViewModel

        Private _videoPosterImage As Bitmap
        ''' <summary>Der Film, fuer den das Standbild zuletzt begonnen hat. Bleibt nach dem ersten
        ''' Abspielen stehen: der Ladeweg des Betrachters laeuft fuer denselben Film mehrmals, und
        ''' das Standbild soll dabei nicht ueber einem laufenden Film wiederkommen.</summary>
        Private _videoPosterPath As String
        Private _awaitingFirstVideoPlayback As Boolean
        Private _videoPosterToken As Integer

        ''' <summary>Die Kachel des Films, solange er noch nicht gelaufen ist.</summary>
        Public ReadOnly Property VideoPosterImage As Bitmap
            Get
                Return _videoPosterImage
            End Get
        End Property

        ''' <summary>Liegt das Standbild gerade ueber der Videoflaeche? Daran haengen das Bild in
        ''' der Ansicht und das Wegschieben der nativen Flaeche.</summary>
        Public ReadOnly Property ShowVideoPoster As Boolean
            Get
                Return _awaitingFirstVideoPlayback AndAlso _videoPosterImage IsNot Nothing AndAlso
                       IsVideoFile AndAlso IsVideoPlaybackAvailable AndAlso Not _isVideoEnded
            End Get
        End Property

        ''' <summary>Ein Film wird gezeigt (LoadBitmap): bis er laeuft, steht seine Kachel.</summary>
        Private Sub BeginVideoPoster(path As String)
            ' Derselbe Film noch einmal (zweiter Durchlauf des Ladewegs): nichts neu beginnen.
            If String.Equals(path, _videoPosterPath, PathIdentity.Comparison) Then Return
            ClearVideoPoster()
            _videoPosterPath = path
            _awaitingFirstVideoPlayback = True
            Dim token = Interlocked.Increment(_videoPosterToken)
            LoadVideoPosterAsync(path, token)
        End Sub

        Private Async Sub LoadVideoPosterAsync(path As String, token As Integer)
            Dim scopeId = _thumbCacheScopeId
            Dim scopeName = _thumbCacheScopeName
            Dim tile As Bitmap = Nothing
            Try
                tile = Await Task.Run(Function() LoadVideoPosterTile(path, scopeId, scopeName))
            Catch ex As Exception
                DiagnosticLogService.LogException("Viewer.VideoPoster", ex)
                tile = Nothing
            End Try
            If tile Is Nothing Then Return
            ' Inzwischen ein anderer Film, oder dieser laeuft schon: dann braucht es kein Standbild mehr.
            If token <> Volatile.Read(_videoPosterToken) OrElse Not _awaitingFirstVideoPlayback Then
                tile.Dispose()
                Return
            End If
            SetVideoPosterImage(tile)
        End Sub

        ''' <summary>Die Kachel eines Films: die genaue aus dem Speicher der Galerie, sonst neu
        ''' gerechnet (das legt sie zugleich fuer die Galerie ab). Eine veraltete gilt nicht: das
        ''' koennte das schwarze erste Bild einer frueheren Fassung sein. Fasst die Platte an und
        ''' gehoert deshalb nicht auf den Oberflaechenfaden.</summary>
        Friend Shared Function LoadVideoPosterTile(path As String, scopeId As String, scopeName As String) As Bitmap
            If String.IsNullOrEmpty(path) OrElse Not VideoPreviewService.IsSupportedVideo(path) Then Return Nothing
            Dim info As FileInfo
            Try
                info = New FileInfo(path)
                If Not info.Exists Then Return Nothing
            Catch
                Return Nothing
            End Try
            Dim isExact As Boolean = False
            Dim tile = ThumbnailCacheService.LoadCached(path, info.LastWriteTimeUtc, info.Length,
                                                        False, isExact, Nothing, scopeId, scopeName)
            If tile IsNot Nothing AndAlso isExact Then Return tile
            tile?.Dispose()
            Return ThumbnailCacheService.CreateOrUpdate(path, info.LastWriteTimeUtc, info.Length,
                                                        Nothing, scopeId, scopeName)
        End Function

        Private Sub SetVideoPosterImage(value As Bitmap)
            Dim previous = _videoPosterImage
            If Object.ReferenceEquals(previous, value) Then Return
            _videoPosterImage = value
            Me.RaisePropertyChanged(NameOf(VideoPosterImage))
            Me.RaisePropertyChanged(NameOf(ShowVideoPoster))
            DisposeDeferred(previous)
        End Sub

        ''' <summary>Der Film laeuft (oder es wurde gesprungen): das Standbild geht und kommt fuer
        ''' diesen Film nicht wieder.</summary>
        Private Sub EndVideoPoster()
            If Not _awaitingFirstVideoPlayback AndAlso _videoPosterImage Is Nothing Then Return
            _awaitingFirstVideoPlayback = False
            Interlocked.Increment(_videoPosterToken)
            SetVideoPosterImage(Nothing)
        End Sub

        ''' <summary>Der Film verlaesst den Betrachter (ein Bild, eine andere Ansicht): beim
        ''' naechsten Zeigen beginnt das Standbild neu, auch fuer denselben Film.</summary>
        Private Sub ClearVideoPoster()
            _videoPosterPath = Nothing
            EndVideoPoster()
        End Sub

        ''' <summary>Eine Zeitmeldung des Spielers, die zum laufenden Film gehoert. Ueber null bei
        ''' aufgehobener Pause heisst: der Film laeuft, seine Bilder kommen. Null allein reicht nicht,
        ''' angehalten am Anfang meldet mpv genau das; und eine Stelle ueber null allein auch nicht,
        ''' ein Film, dessen erstes Bild spaeter beginnt, meldet sie schon angehalten.</summary>
        Friend Sub NoteVideoPlaybackProgress(seconds As Double, isPlaying As Boolean)
            If seconds > 0 AndAlso isPlaying Then EndVideoPoster()
        End Sub

    End Class

End Namespace
