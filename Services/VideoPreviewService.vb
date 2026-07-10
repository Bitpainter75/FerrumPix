Imports System
Imports System.IO
Imports System.Linq
Imports System.Threading
Imports Avalonia
Imports Avalonia.Controls
Imports Avalonia.Threading
Imports LibVLCSharp.Shared
Imports SkiaSharp

Namespace Services

    ''' Extrahiert ein Standbild aus Videodateien für die Thumbnail-Pipeline, analog zu
    ''' RawPreviewService/SvgPreviewService. Anders als dort ist die Extraktion über LibVLC
    ''' event-getrieben (ein kurzlebiger, stummgeschalteter Headless-Player muss erst zu spielen
    ''' beginnen, bevor ein Frame für einen Snapshot verfügbar ist) statt rein synchron - daher
    ''' blockiert ExtractPreview den aufrufenden Thread bis zu timeoutMs. Das ist unbedenklich,
    ''' da diese Methode ausschließlich von den Hintergrund-Thumbnail-Workern aus ImageItem
    ''' aufgerufen wird, nie vom UI-Thread.
    Public Class VideoPreviewService

        Private Shared ReadOnly _videoExtensions As String() = {".mp4", ".mov", ".mkv", ".avi", ".webm", ".m4v"}

        Private Shared ReadOnly _libVlcLock As New Object()
        Private Shared _libVlc As LibVLC

        Public Shared Function IsSupportedVideo(filePath As String) As Boolean
            If String.IsNullOrEmpty(filePath) Then Return False
            Return _videoExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant())
        End Function

        Private Shared Function GetSharedLibVlc() As LibVLC
            SyncLock _libVlcLock
                If _libVlc Is Nothing Then _libVlc = New LibVLC("--quiet", "--no-audio", "--avcodec-hw=none")
                Return _libVlc
            End SyncLock
        End Function

        Public Shared Function ExtractPreview(filePath As String, Optional maxDimension As Integer = 480, Optional timeoutMs As Integer = 4000) As MemoryStream
            If Not App.IsVideoPlaybackAvailable Then Return Nothing
            If Not IsSupportedVideo(filePath) OrElse Not File.Exists(filePath) Then Return Nothing

            Dim surface = HeadlessVideoSurface.Acquire(GetSharedLibVlc(), timeoutMs)
            If surface Is Nothing Then Return Nothing

            Dim tempPath = Path.Combine(Path.GetTempPath(), $"ferrumpix_vthumb_{Guid.NewGuid():N}.png")
            Dim media As Media = Nothing
            Dim playingSignal As New ManualResetEventSlim(False)
            Dim playingHandler As EventHandler(Of EventArgs) = Nothing
            Dim errorHandler As EventHandler(Of EventArgs) = Nothing
            Dim player = surface.MediaPlayer

            Try
                media = New Media(GetSharedLibVlc(), filePath, FromType.FromPath, {":avcodec-hw=none"})
                player.Mute = True

                playingHandler = Sub(s As Object, e As EventArgs) playingSignal.Set()
                errorHandler = Sub(s As Object, e As EventArgs) playingSignal.Set()
                AddHandler player.Playing, playingHandler
                AddHandler player.EncounteredError, errorHandler

                If Not player.Play(media) Then Return Nothing
                If Not playingSignal.Wait(timeoutMs) Then Return Nothing
                If Not player.IsPlaying Then Return Nothing

                ' Etwas in die Datei hineinseeken, damit nicht zufällig ein schwarzes
                ' Startbild als Thumbnail landet, danach kurz warten, bis der Frame an der
                ' neuen Position tatsächlich dekodiert wurde.
                Dim length = player.Length
                If length > 2000 Then
                    player.Time = Math.Min(1000L, length \ 10L)
                    Thread.Sleep(300)
                Else
                    Thread.Sleep(150)
                End If

                Dim deadline = Environment.TickCount64 + timeoutMs
                Do
                    Try
                        If player.TakeSnapshot(0, tempPath, CUInt(Math.Max(1, maxDimension)), 0UI) AndAlso
                           File.Exists(tempPath) AndAlso New FileInfo(tempPath).Length > 0 Then
                            Return ApplyVideoOrientation(File.ReadAllBytes(tempPath), GetVideoOrientation(media))
                        End If
                    Catch
                    End Try
                    Thread.Sleep(150)
                Loop While Environment.TickCount64 < deadline

                Return Nothing
            Catch
                Return Nothing
            Finally
                If playingHandler IsNot Nothing Then RemoveHandler player.Playing, playingHandler
                If errorHandler IsNot Nothing Then RemoveHandler player.EncounteredError, errorHandler
                Try
                    player.Stop()
                Catch
                End Try
                media?.Dispose()
                Try
                    If File.Exists(tempPath) Then File.Delete(tempPath)
                Catch
                End Try
                surface.Release()
            End Try
        End Function

        ''' Der Headless-Snapshot (--vout=dummy) wendet die Rotations-Metadaten des Videos
        ''' (z.B. hochkant mit dem Handy gefilmt) anders als eine normale Wiedergabe nicht
        ''' automatisch an - deshalb wird sie hier separat aus den Video-Track-Metadaten gelesen
        ''' und manuell korrigiert.
        Private Shared Function GetVideoOrientation(media As Media) As SKEncodedOrigin
            Try
                Dim tracks = media.Tracks
                If tracks Is Nothing Then Return SKEncodedOrigin.TopLeft
                For Each track In tracks
                    If track.TrackType = TrackType.Video Then
                        Return MapVideoOrientation(track.Data.Video.Orientation)
                    End If
                Next
            Catch
            End Try
            Return SKEncodedOrigin.TopLeft
        End Function

        ''' VideoOrientation (LibVLC) und SKEncodedOrigin (EXIF-Konvention) verwenden dieselben
        ''' acht Positionsnamen (TopLeft/TopRight/.../LeftBottom) für dieselbe Bedeutung - direkte
        ''' 1:1-Zuordnung nach Name statt nach den (in den jeweiligen Docs uneinheitlich
        ''' formulierten) Rotationsgrad-Beschreibungen.
        Private Shared Function MapVideoOrientation(orientation As VideoOrientation) As SKEncodedOrigin
            Select Case orientation
                Case VideoOrientation.TopLeft : Return SKEncodedOrigin.TopLeft
                Case VideoOrientation.TopRight : Return SKEncodedOrigin.TopRight
                Case VideoOrientation.BottomRight : Return SKEncodedOrigin.BottomRight
                Case VideoOrientation.BottomLeft : Return SKEncodedOrigin.BottomLeft
                Case VideoOrientation.LeftTop : Return SKEncodedOrigin.LeftTop
                Case VideoOrientation.RightTop : Return SKEncodedOrigin.RightTop
                Case VideoOrientation.RightBottom : Return SKEncodedOrigin.RightBottom
                Case VideoOrientation.LeftBottom : Return SKEncodedOrigin.LeftBottom
                Case Else : Return SKEncodedOrigin.TopLeft
            End Select
        End Function

        Private Shared Function ApplyVideoOrientation(pngBytes As Byte(), origin As SKEncodedOrigin) As MemoryStream
            If origin = SKEncodedOrigin.TopLeft Then Return New MemoryStream(pngBytes)
            Try
                Using original = SKBitmap.Decode(pngBytes)
                    If original Is Nothing Then Return New MemoryStream(pngBytes)
                    Dim corrected = ImageOrientationService.ApplyOrientation(original, origin)
                    Try
                        Dim outStream As New MemoryStream()
                        Using img = SKImage.FromBitmap(corrected)
                            Using data = img.Encode(SKEncodedImageFormat.Png, 100)
                                data.SaveTo(outStream)
                            End Using
                        End Using
                        outStream.Position = 0
                        Return outStream
                    Finally
                        If Not Object.ReferenceEquals(corrected, original) Then corrected.Dispose()
                    End Try
                End Using
            Catch
                Return New MemoryStream(pngBytes)
            End Try
        End Function

    End Class

    ''' Stellt einen einzigen, dauerhaft laufenden, für den Nutzer unsichtbaren MediaPlayer für
    ''' die Standbild-Extraktion bereit. "--vout=dummy" allein verhindert kein sichtbares
    ''' Fenster: LibVLC wählt die Fenster-BEREITSTELLUNG ("vout window", unter Linux z.B.
    ''' "xcb_window") unabhängig von der Render-Implementierung ("vout display", korrekt
    ''' "dummy") - per Log-Diagnose bestätigt, erzeugt LibVLC ohne selbst bereitgestelltes
    ''' Fenster-Handle trotzdem ein eigenes, kurz sichtbares natives Fenster. Die einzig
    ''' zuverlässige Lösung: dem MediaPlayer ein echtes, aber weit außerhalb des sichtbaren
    ''' Bildschirmbereichs positioniertes 1x1px-Fenster als Ziel geben, statt LibVLC eines
    ''' erzeugen zu lassen. Der Player wird seriell (ein Aufruf zur Zeit, siehe Acquire/Release)
    ''' für alle Video-Thumbnails wiederverwendet statt pro Anfrage neu erzeugt zu werden, da das
    ''' Erzeugen des Fensters selbst den UI-Thread braucht und nicht pro Thumbnail wiederholt
    ''' werden soll.
    Friend NotInheritable Class HeadlessVideoSurface
        Private Shared ReadOnly _gate As New SemaphoreSlim(1, 1)
        Private Shared ReadOnly _createLock As New Object()
        Private Shared _instance As HeadlessVideoSurface

        Public ReadOnly Property MediaPlayer As MediaPlayer

        Private Sub New(player As MediaPlayer)
            MediaPlayer = player
        End Sub

        ''' Blockiert, bis die einzige Instanz frei ist (max. eine Video-Thumbnail-Extraktion
        ''' gleichzeitig), und liefert sie zusammen mit dem exklusiven Zugriff. Aufrufer MUSS
        ''' Release() aufrufen (Try/Finally), sonst bleibt der Zugriff dauerhaft blockiert.
        Public Shared Function Acquire(libVlc As LibVLC, timeoutMs As Integer) As HeadlessVideoSurface
            If Not _gate.Wait(Math.Max(timeoutMs, 4000)) Then Return Nothing
            Try
                Return GetOrCreate(libVlc)
            Catch
                _gate.Release()
                Return Nothing
            End Try
        End Function

        Public Sub Release()
            _gate.Release()
        End Sub

        Private Shared Function GetOrCreate(libVlc As LibVLC) As HeadlessVideoSurface
            SyncLock _createLock
                If _instance IsNot Nothing Then Return _instance
            End SyncLock

            Dim created As HeadlessVideoSurface = Nothing
            Dispatcher.UIThread.Invoke(Sub()
                                           SyncLock _createLock
                                               If _instance IsNot Nothing Then
                                                   created = _instance
                                                   Return
                                               End If
                                               Dim videoView As New FerrumPix.Controls.VideoView()
                                               ' KEINE extreme Off-Screen-Position verwenden (z.B. -32000,-32000):
                                               ' manche Fenstermanager (u.a. KWin) respektieren das nicht und
                                               ' klemmen das Fenster stattdessen sichtbar in eine Bildschirmecke -
                                               ' zusätzlich kann das transiente PositionChanged-Ereignisse
                                               ' auslösen, die versehentlich als Hauptfenster-Position gespeichert
                                               ' werden. Stattdessen eine normale, immer gültige On-Screen-Position
                                               ' (0,0) mit Opacity 0 und 1x1px - unsichtbar unabhängig vom
                                               ' Fenstermanager-Verhalten.
                                               Dim window As New Window() With {
                                                   .Width = 1,
                                                   .Height = 1,
                                                   .Opacity = 0,
                                                   .SystemDecorations = SystemDecorations.None,
                                                   .ShowInTaskbar = False,
                                                   .CanResize = False,
                                                   .ShowActivated = False,
                                                   .Topmost = False,
                                                   .WindowStartupLocation = WindowStartupLocation.Manual,
                                                   .Position = New PixelPoint(0, 0),
                                                   .Content = videoView
                                               }
                                               Dim player As New MediaPlayer(libVlc)
                                               videoView.MediaPlayer = player
                                               window.Show()
                                               created = New HeadlessVideoSurface(player)
                                               _instance = created
                                           End SyncLock
                                       End Sub, DispatcherPriority.Send)
            Return created
        End Function

    End Class

End Namespace
