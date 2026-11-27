Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia.Threading
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Die Bilder der mobilen Seite: Kacheln, Vorschauen fuer das Vollbild, Gesichter, Originale,
    ''' JPEG mit Bearbeitung, ZIP, dazu das Hochladen.
    '''
    ''' <para>NUR EIN DECODE GLEICHZEITIG (Regel des Projekts). Was dekodiert oder rechnet, geht
    ''' durch EINE Warteschlange der mobilen Seite (<see cref="RunMobileJobAsync"/>), der Reihe nach,
    ''' und darin durch die Schleuse der Anwendung (DecodeGate). Eine Kachel, die schon im
    ''' Vorschaubild-Speicher liegt, braucht keinen Platz in der Schlange; sie wird nur gelesen.</para>
    '''
    ''' <para>ABBRECHEN. Jede Bildanfrage traegt eine Kennung der Seite (r). Scrollt ein Bild aus
    ''' dem Blick, bricht die Seite ihre Anfrage ab und meldet die Kennung an /api/m/cancel; eine
    ''' Arbeit, die noch in der Schlange wartet, faellt dann heraus, bevor sie die Schleuse belegt.
    ''' Eine schon laufende Arbeit laeuft zu Ende (ein Decode laesst sich nicht mittendrin anhalten),
    ''' ihr Ergebnis bleibt im Zwischenspeicher. Den Abbruch der Verbindung selbst meldet der
    ''' Webserver von .NET nicht verlaesslich, deshalb die eigene Meldung.</para>
    ''' </summary>
    Partial Public NotInheritable Class BrowserBridge

        ''' <summary>Lange Kante der Vorschau im Vollbild: hoechstens so viel, auch auf einem
        ''' Geraet mit mehr Bildpunkten. Kleinere Stufen fuer kleinere Schirme.</summary>
        Private Shared ReadOnly MobilePreviewEdges As Integer() = {1024, 1536, 2048}

        ''' <summary>Eine Arbeit, die laenger wartet, ist von der Seite laengst vergessen.</summary>
        Private Const MobileJobMaxWaitMs As Long = 60_000

        Private Const MaxMobileCancelled As Integer = 4096
        Private Const MaxMobilePreviews As Integer = 24
        Private Const MaxMobileZipTickets As Integer = 32
        Private Shared ReadOnly MobileZipLifetime As TimeSpan = TimeSpan.FromMinutes(10)

        Private NotInheritable Class MobileJob
            ''' <summary>Sitzung und Kennung der Anfrage; leer heisst: nicht abbrechbar.</summary>
            Public Key As String
            Public Work As Func(Of Byte())
            Public QueuedAt As Long
            Public Cancelled As Boolean
            ''' <summary>Vorbereitung im Hintergrund (das naechste und vorige Bild im Vollbild): kommt
            ''' erst dran, wenn nichts anderes mehr wartet, also nie vor einer sichtbaren Kachel.</summary>
            Public Low As Boolean
            Public ReadOnly Done As New TaskCompletionSource(Of Byte())(TaskCreationOptions.RunContinuationsAsynchronously)
        End Class

        Private Shared ReadOnly MobileQueue As New LinkedList(Of MobileJob)()
        Private Shared _mobileWorkerActive As Boolean
        ''' <summary>Abbrueche, deren Anfrage (noch) nicht in der Schlange stand, mit der Zeit der
        ''' Meldung. Kommt die Anfrage danach, faellt sie gleich heraus.</summary>
        Private Shared ReadOnly MobileCancelled As New Dictionary(Of String, Long)(StringComparer.Ordinal)

        ''' <summary>Wie oft eine Arbeit der Schlange wirklich begonnen hat. Fuer den Pruefstand: eine
        ''' abgebrochene Anfrage darf diese Zahl nicht erhoehen.</summary>
        Friend Shared MobileWorkRuns As Integer

        ''' <summary>Die zuletzt gerechneten Vorschauen. Wer im Vollbild zurueckwischt, bekommt das
        ''' Bild ohne zweiten Decode.</summary>
        Private Shared ReadOnly MobilePreviews As New LinkedList(Of KeyValuePair(Of String, Byte()))()
        Private Shared ReadOnly MobileFaces As New Dictionary(Of String, Byte())(StringComparer.Ordinal)

        Private NotInheritable Class MobileZipTicket
            Public Paths As List(Of String)
            Public SessionKey As String
            Public Created As DateTime
        End Class

        Private Shared ReadOnly MobileZipTickets As New Dictionary(Of String, MobileZipTicket)(StringComparer.Ordinal)

        ' --- Warteschlange ----------------------------------------------------------------------

        ''' <summary>Die Kennung einer Anfrage, wie die Seite sie schickt: hoechstens 32 Zeichen aus
        ''' Buchstaben und Ziffern; sonst leer (nicht abbrechbar).</summary>
        Private Shared Function MobileJobKey(mobile As MobileContext, request As HttpListenerRequest) As String
            ' Die Seite schickt die Kennung als Kopf, damit die Adresse der Kachel gleich bleibt und
            ' der Browser sie aus seinem Zwischenspeicher nehmen kann; "r" in der Adresse fuer
            ' Werkzeuge und den Pruefstand.
            Dim id = If(request.Headers("X-FerrumPix-Request"), If(request.QueryString("r"), ""))
            If id.Length = 0 OrElse id.Length > 32 OrElse Not id.All(Function(c) Char.IsAsciiLetterOrDigit(c)) Then Return ""
            Return mobile.SessionKey & "|" & id
        End Function

        ''' <summary>Stellt eine Arbeit in die Schlange. Das Ergebnis ist Nothing, wenn sie
        ''' abgebrochen wurde, zu lange wartete oder nichts lieferte; <paramref name="cancelled"/>
        ''' unterscheidet den Abbruch.</summary>
        Private Shared Function RunMobileJobAsync(key As String, work As Func(Of Byte()), Optional low As Boolean = False) As Task(Of Byte())
            Dim job As New MobileJob With {.Key = If(key, ""), .Work = work, .QueuedAt = Environment.TickCount64, .Low = low}
            Dim start = False
            SyncLock MobileQueue
                If job.Key.Length > 0 AndAlso MobileCancelled.Remove(job.Key) Then
                    job.Cancelled = True
                    job.Done.TrySetResult(Nothing)
                    Return job.Done.Task
                End If
                MobileQueue.AddLast(job)
                If Not _mobileWorkerActive Then
                    _mobileWorkerActive = True
                    start = True
                End If
            End SyncLock
            If start Then
                Dim ignored = Task.Run(AddressOf MobileWorkerLoop)
            End If
            Return job.Done.Task
        End Function

        ''' <summary>Ob die Arbeit zu einer Kennung abgebrochen wurde. Nur fuer die Antwort.</summary>
        Private Shared Function WasMobileJobCancelled(key As String) As Boolean
            If String.IsNullOrEmpty(key) Then Return False
            SyncLock MobileQueue
                Return MobileCancelledKeys.Contains(key)
            End SyncLock
        End Function

        ''' <summary>Abgebrochene Arbeiten der letzten Zeit, damit ihre Antwort 409 statt 404 sagt.</summary>
        Private Shared ReadOnly MobileCancelledKeys As New HashSet(Of String)(StringComparer.Ordinal)

        ''' <summary>Der eine Arbeiter der Schlange: nimmt die aelteste Arbeit, laesst verfallene
        ''' fallen und endet, wenn nichts mehr wartet.</summary>
        Private Shared Sub MobileWorkerLoop()
            Do
                Dim job As MobileJob
                SyncLock MobileQueue
                    If MobileQueue.Count = 0 Then
                        _mobileWorkerActive = False
                        Return
                    End If
                    ' Die aelteste Arbeit, die nicht nur Vorbereitung ist; gibt es keine, die aelteste
                    ' Vorbereitung.
                    Dim node = MobileQueue.First
                    While node IsNot Nothing AndAlso node.Value.Low
                        node = node.Next
                    End While
                    If node Is Nothing Then node = MobileQueue.First
                    job = node.Value
                    MobileQueue.Remove(node)
                End SyncLock
                If Environment.TickCount64 - job.QueuedAt > MobileJobMaxWaitMs Then
                    job.Done.TrySetResult(Nothing)
                    Continue Do
                End If
                Interlocked.Increment(MobileWorkRuns)
                Try
                    job.Done.TrySetResult(job.Work())
                Catch ex As Exception
                    DiagnosticLogService.LogException("BrowserBridge.Mobile.Work", ex)
                    job.Done.TrySetResult(Nothing)
                End Try
            Loop
        End Sub

        ''' <summary>Nimmt wartende Arbeiten dieser Sitzung aus der Schlange. Steht eine noch nicht
        ''' darin (die Anfrage ist unterwegs), wird der Abbruch vorgemerkt. Liefert, wie viele
        ''' wartende Arbeiten herausfielen.</summary>
        Friend Shared Function CancelMobileJobs(sessionKey As String, ids As IEnumerable(Of String)) As Integer
            Dim removed = 0
            Dim now = Environment.TickCount64
            SyncLock MobileQueue
                ' Alte Vormerkungen fallen nach einer Minute weg: die Anfrage dazu kam nie oder ist
                ' laengst beantwortet.
                If MobileCancelled.Count > MaxMobileCancelled \ 2 Then
                    For Each stale In MobileCancelled.Where(Function(p) now - p.Value > MobileJobMaxWaitMs).Select(Function(p) p.Key).ToList()
                        MobileCancelled.Remove(stale)
                    Next
                End If
                If MobileCancelledKeys.Count > MaxMobileCancelled Then MobileCancelledKeys.Clear()
                For Each id In ids
                    If String.IsNullOrEmpty(id) OrElse id.Length > 32 OrElse Not id.All(Function(c) Char.IsAsciiLetterOrDigit(c)) Then Continue For
                    Dim key = sessionKey & "|" & id
                    MobileCancelledKeys.Add(key)
                    Dim node = MobileQueue.First
                    Dim found = False
                    While node IsNot Nothing
                        Dim following = node.Next
                        If node.Value.Key = key Then
                            MobileQueue.Remove(node)
                            node.Value.Cancelled = True
                            node.Value.Done.TrySetResult(Nothing)
                            removed += 1
                            found = True
                        End If
                        node = following
                    End While
                    If Not found AndAlso MobileCancelled.Count < MaxMobileCancelled Then MobileCancelled(key) = now
                Next
            End SyncLock
            Return removed
        End Function

        ''' <summary>POST /api/m/cancel mit {"r": ["kennung", ...]}.</summary>
        Private Shared Async Function HandleMobileCancelAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim body = Await ReadMobileJsonAsync(context.Request)
            Dim ids As New List(Of String)()
            Dim list As JsonElement
            If body.HasValue AndAlso body.Value.TryGetProperty("r", list) AndAlso list.ValueKind = JsonValueKind.Array Then
                For Each entry In list.EnumerateArray().Take(512)
                    If entry.ValueKind = JsonValueKind.String Then ids.Add(entry.GetString())
                Next
            End If
            Dim removed = CancelMobileJobs(mobile.SessionKey, ids)
            Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {{"ok", True}, {"cancelled", removed}})
        End Function

        ' --- Kacheln -------------------------------------------------------------------------------

        ''' <summary>GET /api/m/thumb?id=&amp;s=&amp;r=. Die Kachel aus dem Vorschaubild-Speicher der
        ''' Galerie; fehlt sie, legt die Schlange sie dort an (wie die Galerie es getan haette). Ist
        ''' der Speicher abgeschaltet, kommt eine kleine Vorschau.</summary>
        Private Shared Async Function HandleMobileThumbAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            ' Das Bild: eine Datei oder eine ihrer Varianten (eigene Kachel, eigenes Rezept).
            Dim key = mobile.ResolveImage(context.Request.QueryString("id"))
            If key Is Nothing Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If
            Dim watch = System.Diagnostics.Stopwatch.StartNew()
            Dim path = VariantKey.FileOf(key)
            If Not MobileContext.ImageExists(key) Then
                ' Ein Laufwerk, das gerade fehlt: die zuletzt abgelegte Kachel, wie die Galerie sie
                ' offline zeigt. Sonst ist die Datei weg (der Katalog kennt sie noch): die Seite
                ' zeigt dafuer ein eigenes Symbol statt einer grauen Flaeche.
                If OfflineVolumes.IsUnreachable(path) Then
                    Dim offline = ThumbnailCacheService.FindCachedTileOffline(key)
                    If offline.Length > 0 Then
                        Dim kept As Byte() = Nothing
                        Try
                            kept = Await File.ReadAllBytesAsync(offline)
                        Catch ex As IOException
                        End Try
                        If kept IsNot Nothing Then
                            MarkMobileThumb(context, MobileThumbKind.Reused, watch)
                            Await WriteMobileImageAsync(context, kept, "image/jpeg", """" & ShortStamp(IO.Path.GetFileName(offline)) & """")
                            Return
                        End If
                    End If
                End If
                WriteMobileThumbFailure(context, MobileThumbKind.Missing, watch)
                Return
            End If
            If Not mobile.CanDownload(path) Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If
            Dim info As New FileInfo(path)
            Dim stamp = info.LastWriteTimeUtc
            Dim size = info.Length
            ' Nennt das Rezept eine Datei, die dieser Benutzer nicht lesen darf, gilt der gemeinsame
            ' Kachelspeicher nicht: gerechnet wird ohne sie und ohne abzulegen (RecipeReferences).
            Dim reader = mobile.ReferenceReader
            Dim restricted = Not RecipeReferences.AllReadable(key, reader)
            ' Dieselben Werte wie die Galerie (ImageItem.EnsureFileInfoLoaded: Groesse und
            ' Aenderungszeit aus FileInfo, derselbe Ordnerbereich). Auch eine Kachel desselben
            ' Dateistands unter anderer Qualitaet oder Formatfassung zaehlt, wie in der Galerie;
            ' angelegt wird nur, wo es fuer diesen Stand noch gar keine gibt. Fuer eine Variante
            ' sucht und schreibt der Kachelspeicher unter ihrem Schluessel.
            Dim isExact As Boolean
            Dim tile = If(restricted, "", ThumbnailCacheService.FindReusableCachedThumbnail(key, stamp, size, isExact))
            If tile.Length > 0 Then
                Dim etag = """" & ShortStamp(IO.Path.GetFileName(tile)) & """"
                If IsNotModified(context.Request, etag) Then
                    MarkMobileThumb(context, MobileThumbKind.NotModified, watch)
                    WriteNotModified(context, etag)
                    Return
                End If
                Dim cached As Byte() = Nothing
                Try
                    cached = Await File.ReadAllBytesAsync(tile)
                Catch ex As IOException
                End Try
                If cached IsNot Nothing Then
                    MarkMobileThumb(context, If(isExact, MobileThumbKind.Hit, MobileThumbKind.Reused), watch)
                    Await WriteMobileImageAsync(context, cached, "image/jpeg", etag)
                    Return
                End If
            End If

            ' Ohne Kachelspeicher gilt der Stand der Datei als ETag; der Browser fragt dann nur nach.
            Dim plainTag = """" & ShortStamp(key & "|" & stamp.Ticks.ToString(CultureInfo.InvariantCulture) & "|" & size.ToString(CultureInfo.InvariantCulture) &
                                             "|" & MobileRecipeStamp(key) & If(restricted, "|r", "")) & """"
            If tile.Length = 0 AndAlso IsNotModified(context.Request, plainTag) Then
                MarkMobileThumb(context, MobileThumbKind.NotModified, watch)
                WriteNotModified(context, plainTag)
                Return
            End If
            Dim jobKey = MobileJobKey(mobile, context.Request)
            Dim fromCache = False
            Dim started = False
            Dim bytes = Await RunMobileJobAsync(jobKey,
                Function()
                    started = True
                    If restricted Then Return RenderMobilePreview(key, 480, reader)
                    ThumbnailCacheService.EnsureCached(key, stamp, size)
                    Dim written = ThumbnailCacheService.FindExactCachedThumbnail(key, stamp, size)
                    If written.Length > 0 Then
                        fromCache = True
                        Return File.ReadAllBytes(written)
                    End If
                    Return RenderMobileTile(key, reader)
                End Function)
            If bytes Is Nothing Then
                ' Abgebrochen (409): die Seite fragt wieder, wenn die Kachel wieder sichtbar ist.
                ' Nie begonnen, weil sie zu lange in der Schlange stand (503): ebenso. Gerechnet
                ' und nichts herausgekommen: das Format ist nicht lesbar, die Seite zeigt ein Symbol.
                If WasMobileJobCancelled(jobKey) Then
                    WriteMobileThumbFailure(context, MobileThumbKind.Cancelled, watch)
                ElseIf Not started Then
                    WriteMobileThumbFailure(context, MobileThumbKind.Busy, watch)
                Else
                    WriteMobileThumbFailure(context, MobileThumbKind.Unreadable, watch)
                End If
                Return
            End If
            ' Derselbe ETag wie beim naechsten Abruf aus dem Speicher: der Name der Kachel. Ohne
            ' Kachelspeicher der Stand der Datei.
            Dim created = If(restricted, "", ThumbnailCacheService.FindExactCachedThumbnail(key, stamp, size))
            Dim tag = If(created.Length > 0, """" & ShortStamp(IO.Path.GetFileName(created)) & """", plainTag)
            MarkMobileThumb(context, If(fromCache, MobileThumbKind.Built, MobileThumbKind.Preview), watch)
            Await WriteMobileImageAsync(context, bytes, "image/jpeg", tag)
        End Function

        ''' <summary>Der Stand der Begleitdatei, fuer den ETag einer Kachel ohne Kachelspeicher:
        ''' eine neue Bearbeitung aendert die Kachel, ohne die Datei anzufassen.</summary>
        Private Shared Function MobileRecipeStamp(key As String) As String
            Try
                Dim recipe = RawSidecarService.SidecarPathFor(VariantKey.FileOf(key))
                If File.Exists(recipe) Then Return File.GetLastWriteTimeUtc(recipe).Ticks.ToString(CultureInfo.InvariantCulture)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            Return ""
        End Function

        ''' <summary>Die Kachel, wenn der Kachelspeicher keine anlegt: SVG und ICO (nie abgelegt),
        ''' Speicher abgeschaltet, oder das Schreiben scheiterte. Erst der Weg der Galerie ohne
        ''' Speicher (ThumbnailCacheService.RenderTileWithoutCache), dann die Vorschau des
        ''' Vollbilds in Kachelgroesse. Nothing, wenn das Bild nicht lesbar ist.</summary>
        Private Shared Function RenderMobileTile(key As String, canRead As Func(Of String, Boolean)) As Byte()
            Dim direct = DecodeGate.Run(
                Function()
                    Try
                        Using guard = RecipeReferences.Restrict(canRead),
                              bitmap = ThumbnailCacheService.RenderTileWithoutCache(key)
                            If bitmap Is Nothing Then Return Nothing
                            ' Die Punkte direkt nach Skia, ohne Umweg ueber ein PNG.
                            Using converted = ImageOrientationService.ToSkBitmap(bitmap)
                                Return EncodeMobileJpeg(converted, 960, 85)
                            End Using
                        End Using
                    Catch ex As Exception When TypeOf ex IsNot OutOfMemoryException
                        DiagnosticLogService.LogException("BrowserBridge.MobileTile", ex)
                        Return Nothing
                    End Try
                End Function)
            If direct IsNot Nothing Then Return direct
            Return RenderMobilePreview(key, 480, canRead)
        End Function

        ''' <summary>Eine Kachel ohne Bild: Status und Kopf X-FerrumPix-Thumb sagen der Seite, was sie
        ''' statt dessen zeigt ("missing" Datei fehlt, "unreadable" Format nicht lesbar) oder ob sie
        ''' spaeter wieder fragt ("cancelled", "busy").</summary>
        Private Shared Sub WriteMobileThumbFailure(context As HttpListenerContext, kind As MobileThumbKind, watch As System.Diagnostics.Stopwatch)
            MarkMobileThumb(context, kind, watch)
            Try
                context.Response.StatusCode = MobileThumbStatus(kind)
                context.Response.Headers("Cache-Control") = "no-store"
                context.Response.Close()
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse
                                       TypeOf ex Is InvalidOperationException
            End Try
        End Sub

        ''' <summary>Der Status zu einer Kachel ohne Bild.</summary>
        Private Shared Function MobileThumbStatus(kind As MobileThumbKind) As Integer
            Select Case kind
                Case MobileThumbKind.Missing : Return 404
                Case MobileThumbKind.Unreadable : Return 422
                Case MobileThumbKind.Cancelled : Return 409
                Case MobileThumbKind.Busy : Return 503
                Case Else : Return 200
            End Select
        End Function

        ''' <summary>Woher eine Kachel der Seite kam. Steht als Kopf X-FerrumPix-Thumb an der
        ''' Antwort (die Diagnosezeile der Seite zaehlt ihn mit) und geht in die Sammelzeile des
        ''' Protokolls.</summary>
        Private Enum MobileThumbKind
            ''' <summary>"hit": genau die Kachel der Galerie aus dem Speicher.</summary>
            Hit
            ''' <summary>"reuse": eine Kachel desselben Dateistands unter aelterer Qualitaet oder
            ''' Formatfassung, wie sie die Galerie ebenfalls zeigt.</summary>
            Reused
            ''' <summary>"built": fehlte und wurde im Speicher angelegt.</summary>
            Built
            ''' <summary>"preview": kleine Vorschau ohne Speicher (abgeschaltet oder nicht moeglich).</summary>
            Preview
            ''' <summary>"304": der Browser hatte sie schon.</summary>
            NotModified
            ''' <summary>"missing" (404): die Datei gibt es nicht mehr, der Katalog kennt sie noch.</summary>
            Missing
            ''' <summary>"unreadable" (422): gerechnet, aber kein Bild herausgekommen.</summary>
            Unreadable
            ''' <summary>"cancelled" (409): die Seite hat die Anfrage abgebrochen.</summary>
            Cancelled
            ''' <summary>"busy" (503): stand zu lange in der Schlange und wurde nie begonnen.</summary>
            Busy
        End Enum

        Private Shared ReadOnly MobileThumbNames As String() = {"hit", "reuse", "built", "preview", "304", "missing", "unreadable", "cancelled", "busy"}
        Private Shared ReadOnly MobileThumbCounts(8) As Integer
        Private Shared _mobileThumbTotal As Integer
        Private Shared _mobileThumbTicks As Long
        Private Shared ReadOnly MobileThumbLock As New Object()

        ''' <summary>Setzt den Kopf X-FerrumPix-Thumb und zaehlt mit; bei aktivem Diagnoseprotokoll
        ''' je 50 Kacheln eine Sammelzeile "BrowserBridge.MobileThumbs".</summary>
        Private Shared Sub MarkMobileThumb(context As HttpListenerContext, kind As MobileThumbKind, watch As System.Diagnostics.Stopwatch)
            Try
                context.Response.Headers("X-FerrumPix-Thumb") = MobileThumbNames(kind)
            Catch ex As InvalidOperationException
            End Try
            Dim line As String = Nothing
            SyncLock MobileThumbLock
                MobileThumbCounts(kind) += 1
                _mobileThumbTotal += 1
                _mobileThumbTicks += watch.ElapsedTicks
                If _mobileThumbTotal >= 50 Then
                    line = String.Format(CultureInfo.InvariantCulture,
                        "kacheln={0} treffer={1} wiederverwendet={2} angelegt={3} vorschau={4} nicht_geaendert={5} " &
                        "datei_fehlt={7} unlesbar={8} abgebrochen={9} belegt={10} mittel_ms={6:0.0}",
                        _mobileThumbTotal, MobileThumbCounts(0), MobileThumbCounts(1), MobileThumbCounts(2), MobileThumbCounts(3), MobileThumbCounts(4),
                        _mobileThumbTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _mobileThumbTotal,
                        MobileThumbCounts(5), MobileThumbCounts(6), MobileThumbCounts(7), MobileThumbCounts(8))
                    Array.Clear(MobileThumbCounts)
                    _mobileThumbTotal = 0
                    _mobileThumbTicks = 0
                End If
            End SyncLock
            If line IsNot Nothing AndAlso DiagnosticLogService.IsVerboseEnabled Then
                DiagnosticLogService.LogAlways("BrowserBridge.MobileThumbs", line)
            End If
        End Sub

        ' --- Vorschau im Vollbild ------------------------------------------------------------------

        ''' <summary>GET /api/m/preview?id=&amp;w=&amp;r=. Das Bild in Bildschirmgroesse (lange Kante
        ''' hoechstens 2048), mit seiner Bearbeitung, wenn eine .fpxmp sie traegt.</summary>
        Private Shared Async Function HandleMobilePreviewAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            ' Datei oder Variante; die Vorschau einer Variante traegt deren Rezept.
            Dim path = mobile.ResolveImage(context.Request.QueryString("id"))
            If path Is Nothing OrElse Not MobileContext.ImageExists(path) OrElse Not mobile.CanDownload(VariantKey.FileOf(path)) Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If
            Dim wanted = ReadQueryInteger(context.Request, "w", 2048)
            Dim edge = MobilePreviewEdges.FirstOrDefault(Function(e) e >= wanted)
            If edge = 0 Then edge = MobilePreviewEdges(MobilePreviewEdges.Length - 1)
            Dim cacheKey = MobilePreviewKey(path, edge)
            ' Nennt das Rezept eine Datei, die dieser Benutzer nicht lesen darf, rechnet die
            ' Vorschau ohne sie; sie gilt dann nur fuer ihn und steht unter eigenem Schluessel
            ' (RecipeReferences).
            Dim reader = mobile.ReferenceReader
            If Not RecipeReferences.AllReadable(path, reader) Then cacheKey &= "|u:" & mobile.UserKey
            Dim etag = """" & ShortStamp(cacheKey) & """"
            If IsNotModified(context.Request, etag) Then
                WriteNotModified(context, etag)
                Return
            End If
            Dim held = TakeMobilePreview(cacheKey)
            If held Is Nothing Then
                Dim key = MobileJobKey(mobile, context.Request)
                ' Die Seite bereitet das naechste und vorige Bild vor und sagt das ueber einen Kopf
                ' (die Adresse bleibt dieselbe, damit der Browser das Ergebnis spaeter aus seinem
                ' Zwischenspeicher nimmt). Solche Arbeit kommt erst nach allem anderen dran.
                Dim low = String.Equals(context.Request.Headers("X-FerrumPix-Priority"), "low", StringComparison.OrdinalIgnoreCase)
                held = Await RunMobileJobAsync(key,
                    Function()
                        ' Hat eine fruehere Arbeit der Schlange dasselbe Bild schon gerechnet (die
                        ' Vorbereitung, bevor das Bild gezeigt wurde, oder umgekehrt), gilt deren
                        ' Ergebnis: dieselbe Vorschau wird nie zweimal entwickelt.
                        Dim ready = TakeMobilePreview(cacheKey)
                        If ready IsNot Nothing Then Return ready
                        Dim rendered = RenderMobilePreview(path, edge, reader)
                        If rendered IsNot Nothing Then KeepMobilePreview(cacheKey, rendered)
                        Return rendered
                    End Function, low)
                If held Is Nothing Then
                    context.Response.StatusCode = If(WasMobileJobCancelled(key), 409, 404)
                    context.Response.Close()
                    Return
                End If
            End If
            Await WriteMobileImageAsync(context, held, "image/jpeg", etag)
        End Function

        ''' <summary>Was eine Vorschau ausmacht: Datei, Stand, Rezept und Groesse.</summary>
        Private Shared Function MobilePreviewKey(path As String, edge As Integer) As String
            Dim parts As New StringBuilder(path)
            Try
                Dim info As New FileInfo(VariantKey.FileOf(path))
                parts.Append("|"c).Append(info.LastWriteTimeUtc.Ticks).Append("|"c).Append(info.Length)
                Dim recipe = RawSidecarService.SidecarPathFor(VariantKey.FileOf(path))
                If File.Exists(recipe) Then parts.Append("|"c).Append(File.GetLastWriteTimeUtc(recipe).Ticks)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            parts.Append("|"c).Append(edge)
            Return parts.ToString()
        End Function

        Private Shared Function TakeMobilePreview(key As String) As Byte()
            SyncLock MobilePreviews
                Dim node = MobilePreviews.First
                While node IsNot Nothing
                    If node.Value.Key = key Then
                        MobilePreviews.Remove(node)
                        MobilePreviews.AddFirst(node)
                        Return node.Value.Value
                    End If
                    node = node.Next
                End While
            End SyncLock
            Return Nothing
        End Function

        Private Shared Sub KeepMobilePreview(key As String, bytes As Byte())
            SyncLock MobilePreviews
                MobilePreviews.AddFirst(New KeyValuePair(Of String, Byte())(key, bytes))
                While MobilePreviews.Count > MaxMobilePreviews
                    MobilePreviews.RemoveLast()
                End While
            End SyncLock
        End Sub

        ''' <summary>Rechnet die Vorschau, durch die Schleuse der Anwendung. Ein bearbeitetes Bild
        ''' (.fpxmp mit Bearbeitung) geht durch die Kette wie die Kachel der Galerie; ein
        ''' unbearbeitetes kommt aus seiner Quelle. Eine RAW folgt den Regeln des Betrachters am
        ''' Desktop (<see cref="RenderMobileRawPreview"/>). Ein Video zeigt sein Standbild.
        ''' <paramref name="canRead"/>: die Leseregel fuer Dateien, die das Rezept nennt
        ''' (RecipeReferences.Restrict, MobileContext.ReferenceReader); Nothing heisst keine Grenze.</summary>
        Friend Shared Function RenderMobilePreview(path As String, edge As Integer, Optional canRead As Func(Of String, Boolean) = Nothing) As Byte()
            Return DecodeGate.Run(
                Function()
                    Using guard = RecipeReferences.Restrict(canRead)
                        Return RenderMobilePreviewCore(path, edge)
                    End Using
                End Function)
        End Function

        Private Shared Function RenderMobilePreviewCore(key As String, edge As Integer) As Byte()
            ' Der Schluessel einer Variante traegt deren Rezept; gelesen wird immer die Datei.
            Dim path = VariantKey.FileOf(key)
            If MediaFileTypes.IsVideo(path) Then
                Using still = VideoPreviewService.ExtractPreview(path, edge)
                    If still Is Nothing Then Return Nothing
                    Using frame = SKBitmap.Decode(still.ToArray())
                        Return EncodeMobileJpeg(frame, edge, 82)
                    End Using
                End Using
            End If
            Dim bitmap As SKBitmap = Nothing
            Try
                If RawPreviewService.IsSupportedRaw(path) Then
                    bitmap = RenderMobileRawPreview(key, edge)
                    If bitmap Is Nothing Then Return Nothing
                    Return EncodeMobileJpeg(bitmap, edge, 85)
                End If
                ' Jedes Rezept daneben geht durch die Kette in Vorschaugroesse, wie bei der Kachel der
                ' Galerie (ThumbnailCacheService.RendersRecipeTile), auch eines ohne Vermerk
                ' "bearbeitet": der volle Weg unten haette es ebenso angewandt, nur am ganzen Bild.
                Dim hasRecipe = False
                If Not FpxService.IsFpx(path) AndAlso RawSidecarService.HasRecipeBeside(path) Then
                    Dim adjustments = RawSidecarService.TryRead(path)
                    If adjustments IsNot Nothing Then
                        hasRecipe = True
                        ' Direkt als Bitmap: der Umweg ueber PNG-Kodieren und Skia-Lesen kostete
                        ' bei einer Vorschau mehr als das Rechnen.
                        bitmap = ImageProcessor.RenderPreviewBitmap(path, adjustments, edge)
                    End If
                End If
                ' Ohne Rezept gleich im Decoder verkleinert (JPEG, WebP), sonst Nothing und weiter
                ' unten der volle Weg. Drehung und Farbprofil wie dort.
                If bitmap Is Nothing AndAlso Not hasRecipe Then
                    Try
                        bitmap = ImageProcessor.DecodeOrientedReduced(path, edge)
                    Catch ex As Exception When TypeOf ex IsNot OutOfMemoryException
                        bitmap = Nothing
                    End Try
                End If
                If bitmap Is Nothing Then
                    Try
                        bitmap = ImageProcessor.DecodeDevelopedForOutput(path, developRaw:=False)
                    Catch ex As Exception When TypeOf ex IsNot OutOfMemoryException
                        bitmap = Nothing
                    End Try
                End If
                ' Formate, die nur der Weg der Kacheln liest (SVG, ICO und Verwandte): dessen Quelle.
                If bitmap Is Nothing Then
                    Try
                        Using source = ThumbnailCacheService.OpenThumbnailSource(path, edge)
                            If source IsNot Nothing Then bitmap = SKBitmap.Decode(source)
                        End Using
                    Catch ex As Exception When TypeOf ex IsNot OutOfMemoryException
                        bitmap = Nothing
                    End Try
                End If
                If bitmap Is Nothing Then Return Nothing
                Return EncodeMobileJpeg(bitmap, edge, 85)
            Finally
                bitmap?.Dispose()
            End Try
        End Function

        ''' <summary>Eine RAW nach denselben Regeln wie der Betrachter am Desktop
        ''' (ViewerViewModel.DecodeViewerBitmapWithState): entwickelt nur, wenn
        ''' <see cref="ViewModels.ViewerViewModel.ShouldDevelopRaw"/> es nach den Einstellungen des
        ''' Betrachters verlangt, und dann halb aufgeloest (die Vorschau hat hoechstens 2048 Punkte);
        ''' sonst, und wenn die Entwicklung ausfaellt, die eingebettete Vorschau der Kamera mit der
        ''' Drehung aus Container und Beistelldatei. Vorher ging eine RAW mit Beistelldatei, aber ohne
        ''' Bearbeitung, durch den vollen Decode in ganzer Aufloesung.</summary>
        Private Shared Function RenderMobileRawPreview(key As String, edge As Integer) As SKBitmap
            Dim path = VariantKey.FileOf(key)
            Dim settings = AppSettingsService.Load()
            Dim hasSidecar = RawSidecarService.Exists(path)
            ' Eine Variante wird immer entwickelt gezeigt, wie ihre Kachel: die Vorschau der Kamera
            ' saehe fuer alle Varianten gleich aus.
            Dim isVariant = VariantKey.IsVariant(key)
            If (isVariant AndAlso RawDecodeService.CanDevelop(path)) OrElse
               ViewModels.ViewerViewModel.ShouldDevelopRaw(False, RawDecodeService.CanDevelop(path), settings.DevelopRawInViewer,
                                                           hasSidecar, settings.DevelopRawInViewerWithoutRecipe) Then
                Dim adjustments = If(hasSidecar, RawSidecarService.TryRead(key), Nothing)
                If adjustments Is Nothing Then adjustments = ImageAdjustments.ForUneditedRaw(path)
                Try
                    Dim developed = ImageProcessor.RenderPreviewBitmap(path, adjustments, edge, preferReducedRawDecode:=True)
                    If developed IsNot Nothing Then Return developed
                Catch ex As Exception When TypeOf ex IsNot OperationCanceledException
                    ' Wie der Betrachter: lieber die eingebettete Vorschau als eine leere Flaeche.
                    DiagnosticLogService.LogException("BrowserBridge.MobileRawPreview", ex)
                End Try
            End If
            Using preview = RawPreviewService.ExtractPreviewWithFallback(path)
                If preview Is Nothing Then Return Nothing
                preview.Position = 0
                Using data = SKData.Create(preview)
                    If data Is Nothing Then Return Nothing
                    Using codec = SKCodec.Create(data)
                        If codec Is Nothing Then Return Nothing
                        Dim origin = ImageOrientationService.RawPreviewOrigin(path, codec.EncodedOrigin, codec.Info.Width, codec.Info.Height)
                        ' Die Vorschau der Kamera ist oft so gross wie das Foto (Canon R5 8192,
                        ' Nikon Z 8 8256 Punkte): gleich im Decoder auf die Kante verkleinert, sonst
                        ' wie bisher ganz. Drehung und Farbraum bleiben dieselben.
                        Dim decoded = If(ImageProcessor.DecodeCodecReduced(codec, edge), SKBitmap.Decode(codec))
                        If decoded Is Nothing Then Return Nothing
                        Dim corrected = ImageOrientationService.ApplyOrientation(decoded, origin)
                        If Not Object.ReferenceEquals(corrected, decoded) Then decoded.Dispose()
                        Dim rotated = ImageOrientationService.ApplyQuarterRotation(corrected, RawSidecarService.ReadRotationDegrees(path))
                        If Not Object.ReferenceEquals(rotated, corrected) Then corrected.Dispose()
                        Return rotated
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Verkleinert auf die lange Kante (nie vergroessert), bringt nach sRGB und
        ''' kodiert als JPEG. Durchsichtiges steht auf dem Hintergrund der Seite.</summary>
        Friend Shared Function EncodeMobileJpeg(source As SKBitmap, edge As Integer, quality As Integer) As Byte()
            If source Is Nothing OrElse source.Width <= 0 OrElse source.Height <= 0 Then Return Nothing
            Dim owned As New List(Of SKBitmap)()
            Try
                Dim current = source
                If current.ColorType <> SKColorType.Rgba8888 AndAlso current.ColorType <> SKColorType.Bgra8888 Then
                    current = current.Copy(SKColorType.Rgba8888)
                    If current Is Nothing Then Return Nothing
                    owned.Add(current)
                End If
                Dim longest = Math.Max(current.Width, current.Height)
                If longest > edge Then
                    Dim factor = edge / CDbl(longest)
                    Dim width = Math.Max(1, CInt(Math.Round(current.Width * factor)))
                    Dim height = Math.Max(1, CInt(Math.Round(current.Height * factor)))
                    Dim resized = ImageResampler.Resize(current, width, height, ImageResampler.Kernel.CatmullRom)
                    If resized Is Nothing Then
                        resized = current.Resize(New SKImageInfo(width, height, current.ColorType, current.AlphaType, current.ColorSpace),
                                                 New SKSamplingOptions(SKCubicResampler.Mitchell))
                    End If
                    If resized Is Nothing Then Return Nothing
                    owned.Add(resized)
                    current = resized
                End If
                If ColorManagementService.NeedsConversion(current.ColorSpace) Then
                    Dim managed = ColorManagementService.ToSrgb(current)
                    If managed IsNot Nothing AndAlso Not Object.ReferenceEquals(managed, current) Then
                        owned.Add(managed)
                        current = managed
                    End If
                End If
                Using surface = SKSurface.Create(New SKImageInfo(current.Width, current.Height, SKColorType.Rgba8888, SKAlphaType.Premul))
                    If surface Is Nothing Then Return Nothing
                    surface.Canvas.Clear(SKColor.Parse("#0B0E11"))
                    Using image = SKImage.FromBitmap(current)
                        surface.Canvas.DrawImage(image, 0, 0)
                    End Using
                    Using snapshot = surface.Snapshot(), data = snapshot.Encode(SKEncodedImageFormat.Jpeg, quality)
                        Return data?.ToArray()
                    End Using
                End Using
            Finally
                For Each bitmap In owned
                    bitmap.Dispose()
                Next
            End Try
        End Function

        ' --- Gesichter --------------------------------------------------------------------------------

        ''' <summary>GET /api/m/face?id=: das groesste Gesicht einer Person, ausgeschnitten wie in der
        ''' Personenliste der Anwendung, aus den Bildern, die dieser Benutzer sehen darf.</summary>
        Private Shared Async Function HandleMobileFaceAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim id = If(context.Request.QueryString("id"), "")
            If Not IsPersonId(id) Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If
            ' Das groesste Gesicht unter den Bildern, die dieser Benutzer sehen darf; aus einem Bild
            ' ausserhalb seiner Sicht kommt kein Ausschnitt.
            Dim cover = Await Task.Run(Function() LibraryService.Instance.GetMobileFaceRows(id).
                                                      FirstOrDefault(Function(f) mobile.CanDownload(f.FilePath)))
            If String.IsNullOrEmpty(cover.FilePath) OrElse Not mobile.CanDownload(cover.FilePath) Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If
            Dim cacheKey = id & "|" & cover.FilePath & "|" & cover.X.ToString(CultureInfo.InvariantCulture) & "|" & cover.Y.ToString(CultureInfo.InvariantCulture)
            Dim etag = """" & ShortStamp(cacheKey) & """"
            If IsNotModified(context.Request, etag) Then
                WriteNotModified(context, etag)
                Return
            End If
            Dim bytes As Byte() = Nothing
            SyncLock MobileFaces
                MobileFaces.TryGetValue(cacheKey, bytes)
            End SyncLock
            If bytes Is Nothing Then
                bytes = Await RunMobileJobAsync(MobileJobKey(mobile, context.Request),
                    Function()
                        Dim crop = DecodeGate.Run(Function() FacePanelService.CropFromFile(cover.FilePath, cover.X, cover.Y, cover.Width, cover.Height, Nothing))
                        If crop Is Nothing Then Return Nothing
                        Using crop
                            ' Geradliniges Alpha, wie PNG es speichert; gepackt ueber den
                            ' gemeinsamen Weg (PngEncoder), das Ergebnis bleibt im Speicher liegen.
                            Using converted = ImageOrientationService.ToSkBitmap(crop, straightAlpha:=True)
                                If converted Is Nothing Then Return Nothing
                                Return PngEncoder.EncodeToBytes(converted, PngPurpose.Stored)
                            End Using
                        End Using
                    End Function)
                If bytes Is Nothing Then
                    context.Response.StatusCode = 404
                    context.Response.Close()
                    Return
                End If
                SyncLock MobileFaces
                    If MobileFaces.Count > 512 Then MobileFaces.Clear()
                    MobileFaces(cacheKey) = bytes
                End SyncLock
            End If
            Await WriteMobileImageAsync(context, bytes, "image/png", etag)
        End Function

        ' --- Antworten mit Bildern --------------------------------------------------------------------

        Private Shared Function IsNotModified(request As HttpListenerRequest, etag As String) As Boolean
            Dim given = request.Headers("If-None-Match")
            Return Not String.IsNullOrEmpty(given) AndAlso given.Split(","c).Any(Function(g) g.Trim() = etag)
        End Function

        Private Shared Sub WriteNotModified(context As HttpListenerContext, etag As String)
            Dim response = context.Response
            response.StatusCode = 304
            response.Headers("ETag") = etag
            response.Headers("Cache-Control") = "private, max-age=86400"
            response.Close()
        End Sub

        ''' <summary>Ein Bild der Seite: privat zwischengespeichert, mit ETag. Die Adresse traegt den
        ''' Stand des Bildes (s), eine Aenderung holt es also neu.</summary>
        Private Shared Async Function WriteMobileImageAsync(context As HttpListenerContext, bytes As Byte(), contentType As String, etag As String) As Task
            Try
                Dim response = context.Response
                response.StatusCode = 200
                response.ContentType = contentType
                response.Headers("Cache-Control") = "private, max-age=86400"
                response.Headers("ETag") = etag
                response.Headers("X-Content-Type-Options") = "nosniff"
                response.ContentLength64 = bytes.Length
                If Not String.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase) Then
                    Await response.OutputStream.WriteAsync(bytes)
                End If
                response.Close()
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                Try
                    context.Response.Abort()
                Catch
                End Try
            End Try
        End Function

        ' --- Original und JPEG ------------------------------------------------------------------------

        Private Shared Function MobileContentType(path As String) As String
            Select Case IO.Path.GetExtension(path).ToLowerInvariant()
                Case ".jpg", ".jpeg" : Return "image/jpeg"
                Case ".png" : Return "image/png"
                Case ".webp" : Return "image/webp"
                Case ".gif" : Return "image/gif"
                Case ".heic", ".heif" : Return "image/heic"
                Case ".avif" : Return "image/avif"
                Case ".mp4", ".m4v" : Return "video/mp4"
                Case ".mov" : Return "video/quicktime"
                Case ".webm" : Return "video/webm"
                Case Else : Return "application/octet-stream"
            End Select
        End Function

        ''' <summary>GET /api/m/original?id=&amp;dl=1. Die Datei, wie sie ist: mit dl=1 zum Speichern,
        ''' sonst zum Abspielen (Video). Mit Bereichen (Range), damit ein Video springen kann.</summary>
        Private Shared Async Function HandleMobileOriginalAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            ' Bei einer Variante ihre Datei: das Original ist fuer alle Varianten dasselbe.
            Dim path = mobile.ResolveOriginal(context.Request.QueryString("id"))
            If path Is Nothing OrElse Not mobile.CanDownload(path) Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If
            ' RAW, HEIC, TIFF, PSD und die uebrigen Formate ausser JPEG, PNG und WebP gehen aus der
            ' mobilen Seite nie als Datei hinaus, nur als JPEG mit Bearbeitung (IsPortableImageFormat).
            ' Videos und alles, was keine Bilddatei ist, bleiben, wie sie sind.
            If MediaFileTypes.IsDisplayable(path) AndAlso Not MediaFileTypes.IsVideo(path) AndAlso Not IsPortableImageFormat(path) Then
                Await HandleMobileJpegAsync(context, mobile)
                Return
            End If
            Await SendFileRangeAsync(context, path, MobileContentType(path),
                                     If(context.Request.QueryString("dl") = "1", ContentDisposition(IO.Path.GetFileName(path)), "inline"))
        End Function

        ''' <summary>Eine Datei oder ein Ausschnitt daraus, mit Bereichen (Range), stroemend und ohne
        ''' Zwischendatei. Der gemeinsame Weg von Original, Freigabe und Bewegtbild.
        '''
        ''' <paramref name="segment"/> Nothing heisst: die ganze Datei. Sonst bestimmt die Funktion
        ''' am schon offenen Strom Versatz und Laenge des Ausschnitts (beim Motion Photo der Film
        ''' hinter dem Bild); so gilt die Lage fuer genau die Datei, die gesendet wird, auch wenn
        ''' jemand inzwischen Metadaten in ihren Kopf schrieb. Eine Laenge unter 1 heisst: keiner
        ''' da, Antwort 404. Bereiche und Content-Range zaehlen ab dem Anfang des Ausschnitts, der
        ''' Browser sieht nur ihn.</summary>
        Friend Shared Async Function SendFileRangeAsync(context As HttpListenerContext, filePath As String, contentType As String,
                                                        disposition As String,
                                                        Optional segment As Func(Of Stream, (Offset As Long, Length As Long)) = Nothing) As Task
            Dim response = context.Response
            Try
                Using stream As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous Or FileOptions.SequentialScan)
                    Dim start As Long = 0
                    Dim length = stream.Length
                    If segment IsNot Nothing Then
                        Dim found = segment(stream)
                        If found.Length < 1 OrElse found.Offset < 0 OrElse found.Offset > length - found.Length Then
                            response.StatusCode = 404
                            response.Close()
                            Return
                        End If
                        start = found.Offset
                        length = found.Length
                    End If
                    Dim first As Long = 0
                    Dim last As Long = length - 1
                    Dim isRange = False
                    Dim range = context.Request.Headers("Range")
                    If Not String.IsNullOrEmpty(range) AndAlso length > 0 Then
                        Select Case ParseRange(range, length, first, last)
                            Case RangeAnswer.Unsatisfiable
                                response.StatusCode = 416
                                response.Headers("Content-Range") = "bytes */" & length.ToString(CultureInfo.InvariantCulture)
                                response.Close()
                                Return
                            Case RangeAnswer.Satisfiable
                                isRange = True
                            Case Else
                                ' Nicht unterstuetzt oder ungueltig: uebergangen, die ganze Datei.
                                first = 0
                                last = length - 1
                        End Select
                    End If
                    response.StatusCode = If(isRange, 206, 200)
                    response.ContentType = contentType
                    response.Headers("Accept-Ranges") = "bytes"
                    response.Headers("Cache-Control") = "private, no-cache"
                    response.Headers("X-Content-Type-Options") = "nosniff"
                    response.Headers("Content-Disposition") = disposition
                    If isRange Then
                        response.Headers("Content-Range") = $"bytes {first.ToString(CultureInfo.InvariantCulture)}-{last.ToString(CultureInfo.InvariantCulture)}/{length.ToString(CultureInfo.InvariantCulture)}"
                    End If
                    Dim count = If(length = 0, 0L, last - first + 1)
                    response.ContentLength64 = count
                    If Not String.Equals(context.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase) AndAlso count > 0 Then
                        stream.Seek(start + first, SeekOrigin.Begin)
                        Dim buffer(1024 * 1024 - 1) As Byte
                        Dim remaining = count
                        While remaining > 0
                            Dim read = Await stream.ReadAsync(buffer, 0, CInt(Math.Min(buffer.Length, remaining)))
                            If read <= 0 Then Throw New IOException("The file became shorter while it was sent.")
                            Await response.OutputStream.WriteAsync(buffer, 0, read)
                            remaining -= read
                        End While
                    End If
                End Using
                response.Close()
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                       TypeOf ex Is InvalidOperationException
                Try
                    response.Abort()
                Catch
                End Try
            End Try
        End Function

        ' --- Bewegtbild ---------------------------------------------------------------------------

        ''' <summary>Woher der Film eines Bewegtbilds kommt: beim Motion Photo aus dem JPEG selbst
        ''' (Embedded), beim Live Photo die .mov daneben. Path Nothing: keiner.</summary>
        Friend Structure MobileMotionClip
            Public Path As String
            Public Embedded As Boolean
        End Structure

        ''' <summary>Der Film zu einem Bild, wie der Betrachter am Desktop ihn findet
        ''' (LivePhotoService.IdentityOf: der Katalog, wo seine Zeile zur Datei passt, sonst die
        ''' Datei; die .mov ueber FindPartnerPath mit derselben Kennung). Ob ein Motion Photo den
        ''' Film wirklich noch traegt, entscheidet erst das Senden an den Bytes. Liest Katalog,
        ''' Ordner und im Zweifel Dateien: im Hintergrund rufen. Wirft nie.</summary>
        Friend Shared Function MobileMotionClipOf(path As String) As MobileMotionClip
            Dim result As New MobileMotionClip()
            If String.IsNullOrEmpty(path) OrElse Not LivePhotoService.IsImageHalf(path) Then Return result
            Try
                Dim identity = LivePhotoService.IdentityOf(path)
                If identity.Kind = MotionKind.MotionPhoto Then
                    result.Path = path
                    result.Embedded = True
                ElseIf identity.Kind = MotionKind.LivePhotoImage Then
                    result.Path = LivePhotoService.FindPartnerPath(path)
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("BrowserBridge.MotionClip", ex)
                result.Path = Nothing
            End Try
            Return result
        End Function

        ''' <summary>Sendet den Film eines Bewegtbilds zum Abspielen, mit Bereichen: beim Motion
        ''' Photo genau die Bytes des MP4 aus dem JPEG (Versatz und Laenge an der offenen Datei
        ''' bestimmt, MotionPhotoService.Detect), sonst die .mov, wie sie ist.</summary>
        Friend Shared Function SendMotionClipAsync(context As HttpListenerContext, clip As MobileMotionClip) As Task
            If clip.Embedded Then
                Return SendFileRangeAsync(context, clip.Path, "video/mp4", "inline",
                    Function(stream)
                        Dim info = MotionPhotoService.Detect(stream)
                        If Not info.HasVideo Then Return (0L, -1L)
                        Return (info.VideoOffset, info.VideoLength)
                    End Function)
            End If
            Return SendFileRangeAsync(context, clip.Path, MobileContentType(clip.Path), "inline")
        End Function

        ''' <summary>GET /api/m/motion?id=. Der Film eines Motion Photos oder Live Photos zum
        ''' Abspielen, mit Bereichen. Rechte wie beim Original: das Bild herunterladen duerfen, beim
        ''' Live Photo auch die .mov. Ohne Film 404.</summary>
        Private Shared Async Function HandleMobileMotionAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim path = mobile.ResolveOriginal(context.Request.QueryString("id"))
            If path Is Nothing OrElse Not mobile.CanDownload(path) Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If
            Dim clip = Await Task.Run(Function() MobileMotionClipOf(path))
            If clip.Path Is Nothing OrElse Not mobile.CanDownload(clip.Path) Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If
            Await SendMotionClipAsync(context, clip)
        End Function

        ''' <summary>Was ein Range-Kopf verlangt (RFC 9110, 14.2).</summary>
        Friend Enum RangeAnswer
            ''' Kein unterstuetzter oder ein ungueltiger Kopf: uebergehen, 200 mit der ganzen Datei.
            WholeFile
            ''' Ein gueltiger Bereich, der in der Datei liegt: 206.
            Satisfiable
            ''' Ein gueltiger Bereich, der nicht in der Datei liegt: 416.
            Unsatisfiable
        End Enum

        ''' <summary>Ein Bereich "bytes=a-b", "bytes=a-" oder "bytes=-n". Nach RFC 9110 uebergeht der
        ''' Server, was er nicht unterstuetzt oder was ungueltig ist, und liefert die ganze Datei: eine
        ''' andere Einheit, mehrere Bereiche (fragt kein Video an), "a-b" mit b kleiner a, fehlende
        ''' oder nicht numerische Zahlen. 416 nur fuer einen gueltigen Einzelbereich, der nicht
        ''' erfuellbar ist: Anfang hinter dem Ende oder "bytes=-0".</summary>
        Friend Shared Function ParseRange(header As String, length As Long, ByRef first As Long, ByRef last As Long) As RangeAnswer
            If String.IsNullOrWhiteSpace(header) Then Return RangeAnswer.WholeFile
            Dim equals = header.IndexOf("="c)
            If equals < 0 OrElse Not String.Equals(header.Substring(0, equals).Trim(), "bytes", StringComparison.OrdinalIgnoreCase) Then Return RangeAnswer.WholeFile
            Dim spec = header.Substring(equals + 1).Trim()
            If spec.Contains(","c) Then Return RangeAnswer.WholeFile
            Dim dash = spec.IndexOf("-"c)
            If dash < 0 Then Return RangeAnswer.WholeFile
            Dim startText = spec.Substring(0, dash)
            Dim endText = spec.Substring(dash + 1)
            Dim startValue, endValue As Long
            If startText.Length = 0 Then
                If Not TryReadRangePosition(endText, endValue) Then Return RangeAnswer.WholeFile
                If endValue = 0 OrElse length <= 0 Then Return RangeAnswer.Unsatisfiable
                first = Math.Max(0, length - endValue)
                last = length - 1
                Return RangeAnswer.Satisfiable
            End If
            If Not TryReadRangePosition(startText, startValue) Then Return RangeAnswer.WholeFile
            If endText.Length > 0 Then
                If Not TryReadRangePosition(endText, endValue) OrElse endValue < startValue Then Return RangeAnswer.WholeFile
            End If
            If startValue >= length Then Return RangeAnswer.Unsatisfiable
            first = startValue
            last = If(endText.Length = 0, length - 1, Math.Min(endValue, length - 1))
            Return RangeAnswer.Satisfiable
        End Function

        ''' <summary>Eine Position im Range-Kopf: nur Ziffern. Eine Zahl jenseits von Long ist
        ''' gueltig und steht dann fuer "hinter jedem Ende".</summary>
        Private Shared Function TryReadRangePosition(text As String, ByRef value As Long) As Boolean
            If String.IsNullOrEmpty(text) Then Return False
            For Each character In text
                If character < "0"c OrElse character > "9"c Then Return False
            Next
            If Not Long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, value) Then value = Long.MaxValue
            Return True
        End Function

        ''' <summary>GET /api/m/jpeg?id=. Das Bild in voller Groesse als JPEG, mit seiner Bearbeitung
        ''' (Rezept der .fpxmp, eine RAW entwickelt), wie "Exportieren" am Desktop ohne Dialog.
        ''' Vermerkte, aber noch nicht gerechnete Vorgaenge (Objekt entfernen und Verwandte) kosten
        ''' Minuten und bleiben hier aussen vor, wie bei jeder Vorschau.</summary>
        Private Shared Async Function HandleMobileJpegAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim key = mobile.ResolveImage(context.Request.QueryString("id"))
            Dim path = mobile.ResolveOriginal(context.Request.QueryString("id"))
            If key Is Nothing OrElse path Is Nothing OrElse Not mobile.CanDownload(path) OrElse MediaFileTypes.IsVideo(path) Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If
            ' Ein unbearbeitetes RAW-Paar gibt das JPEG der Kamera (PairJpegFor): ohne eigene
            ' Bearbeitung die Datei selbst, sonst gerechnet wie jedes Bild.
            Dim pair = Await Task.Run(Function() PairJpegFor(key, AddressOf mobile.CanDownload))
            If pair IsNot Nothing Then
                If Not Await Task.Run(Function() RawSidecarService.IsEditedFile(pair)) Then
                    Await SendFileRangeAsync(context, pair, MobileContentType(pair), ContentDisposition(IO.Path.GetFileName(pair)))
                    Return
                End If
                key = pair
                path = pair
            End If
            ' Dateien, die das Rezept nennt, nur nach der Leseregel dieses Benutzers (RecipeReferences).
            Dim reader = mobile.ReferenceReader
            Dim bytes = Await RunMobileJobAsync(MobileJobKey(mobile, context.Request), Function() RenderFullMobileJpeg(key, reader))
            If bytes Is Nothing Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If
            Try
                Dim response = context.Response
                response.StatusCode = 200
                response.ContentType = "image/jpeg"
                response.Headers("Cache-Control") = "no-store"
                response.Headers("X-Content-Type-Options") = "nosniff"
                Dim variantName = If(VariantKey.IsVariant(key), RawSidecarService.VariantName(key), "")
                response.Headers("Content-Disposition") = ContentDisposition(IO.Path.GetFileNameWithoutExtension(path) &
                                                                             If(variantName.Length = 0, "", " (" & variantName & ")") & ".jpg")
                response.ContentLength64 = bytes.Length
                Await response.OutputStream.WriteAsync(bytes)
                response.Close()
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException
                Try
                    context.Response.Abort()
                Catch
                End Try
            End Try
        End Function

        ''' <summary>Das Bild in voller Groesse als JPEG mit Bearbeitung, ohne Metadaten (Skia
        ''' schreibt keine): eine Datei mit ihrem Rezept, eine Variante mit dem ihren. Durch die
        ''' Schleuse der Anwendung, Dateien des Rezepts nur nach <paramref name="canRead"/>
        ''' (RecipeReferences). Der eine Weg fuer das volle JPEG der mobilen Seite und fuer das
        ''' Herunterladen aus einer Freigabe. Nothing, wenn sich nichts rechnen laesst.</summary>
        Friend Shared Function RenderFullMobileJpeg(key As String, canRead As Func(Of String, Boolean)) As Byte()
            Dim path = VariantKey.FileOf(key)
            Return DecodeGate.Run(
                Function()
                    Using guard = RecipeReferences.Restrict(canRead)
                        If VariantKey.IsVariant(key) Then
                            ' Eine Variante mit ihrem eigenen Rezept, in voller Groesse.
                            Dim adjustments = RawSidecarService.TryRead(key)
                            If adjustments Is Nothing Then Return Nothing
                            Using developedVariant = ImageProcessor.RenderPreviewBitmap(path, adjustments, 0)
                                If developedVariant Is Nothing Then Return Nothing
                                Return EncodeMobileJpeg(developedVariant, Integer.MaxValue, 92)
                            End Using
                        End If
                        Using developed = ImageProcessor.DecodeDevelopedForOutput(path, developRaw:=True)
                            Return EncodeMobileJpeg(developed, Integer.MaxValue, 92)
                        End Using
                    End Using
                End Function)
        End Function

        ' --- Mehrere als ZIP ------------------------------------------------------------------------

        ''' <summary>POST /api/m/zip mit {"ids": [...]} legt einen Abholschein an und nennt seine
        ''' Adresse; GET /api/m/zip?t= liefert das ZIP ueber den Weg der Desktop-Seite
        ''' (PlanDownloadEntries, StreamingZipWriter): je Bild seine Beistelldateien, gleiche Namen
        ''' eindeutig. Zwei Schritte, weil ein Download ueber einen Verweis keinen Rumpf schicken kann.
        ''' Der Schein gilt zehn Minuten und nur fuer die Sitzung, die ihn bekam.</summary>
        Private Shared Async Function HandleMobileZipAsync(context As HttpListenerContext, mobile As MobileContext, isPost As Boolean) As Task
            If isPost Then
                Dim body = Await ReadMobileJsonAsync(context.Request)
                Dim paths As New List(Of String)()
                Dim list As JsonElement
                If body.HasValue AndAlso body.Value.TryGetProperty("ids", list) AndAlso list.ValueKind = JsonValueKind.Array Then
                    For Each entry In list.EnumerateArray().Take(MaxMobileList)
                        If entry.ValueKind <> JsonValueKind.String Then Continue For
                        Dim key = mobile.ResolveImage(entry.GetString())
                        If key Is Nothing OrElse Not MobileContext.ImageExists(key) Then Continue For
                        Dim path = VariantKey.FileOf(key)
                        If Not mobile.CanDownload(path) Then Continue For
                        ' Eine Variante kommt als JPEG mit ihrem eigenen Rezept, ohne Partner
                        ' (MobileZipEntries); ihre Datei nur, wenn sie selbst gewaehlt ist.
                        If VariantKey.IsVariant(key) Then
                            paths.Add(key)
                            Continue For
                        End If
                        ' Ein RAW-Paar gibt EIN JPEG: unbearbeitet das der Kamera (samt seinen
                        ' Beistelldateien), bearbeitet das gerechnete der RAW (PairJpegFor).
                        paths.Add(If(PairJpegFor(path, AddressOf mobile.CanDownload), path))
                    Next
                End If
                paths = paths.Distinct(PathIdentity.Comparer).ToList()
                If paths.Count = 0 Then
                    Await WriteMobileJsonAsync(context, 404, MobileProblem(LocalizationService.T("Das Bild konnte nicht geladen werden")))
                    Return
                End If
                Dim ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
                SyncLock MobileZipTickets
                    For Each stale In MobileZipTickets.Where(Function(t) DateTime.UtcNow - t.Value.Created > MobileZipLifetime).Select(Function(t) t.Key).ToList()
                        MobileZipTickets.Remove(stale)
                    Next
                    While MobileZipTickets.Count >= MaxMobileZipTickets
                        MobileZipTickets.Remove(MobileZipTickets.OrderBy(Function(t) t.Value.Created).First().Key)
                    End While
                    MobileZipTickets(ticket) = New MobileZipTicket With {.Paths = paths, .SessionKey = mobile.SessionKey, .Created = DateTime.UtcNow}
                End SyncLock
                Await WriteMobileJsonAsync(context, 200, New Dictionary(Of String, Object) From {
                    {"ok", True}, {"url", "/api/m/zip?t=" & ticket}, {"count", paths.Count}})
                Return
            End If

            Dim found As MobileZipTicket = Nothing
            SyncLock MobileZipTickets
                If Not MobileZipTickets.TryGetValue(If(context.Request.QueryString("t"), ""), found) OrElse
                   DateTime.UtcNow - found.Created > MobileZipLifetime OrElse
                   Not String.Equals(found.SessionKey, mobile.SessionKey, StringComparison.Ordinal) Then found = Nothing
            End SyncLock
            If found Is Nothing Then
                context.Response.StatusCode = 404
                context.Response.Close()
                Return
            End If
            ' Zwischen Schein und Abholung kann sich die Sicht geaendert haben; geprueft wird erneut.
            Dim files = found.Paths.Where(Function(p) mobile.CanDownload(VariantKey.FileOf(p))).ToList()
            Dim response = context.Response
            SecurityLog.ZipDownload(SecurityLog.UserNameOf(mobile.UserKey), LogAddress(context.Request), files.Count)
            Try
                Dim reader = mobile.ReferenceReader
                Dim entries = Await Task.Run(Function() MobileZipEntries(files, reader))
                Dim name = $"FerrumPix_{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}_{files.Count.ToString(CultureInfo.InvariantCulture)}.zip"
                response.StatusCode = 200
                response.ContentType = "application/zip"
                response.Headers("Cache-Control") = "no-store"
                response.Headers("X-Content-Type-Options") = "nosniff"
                response.Headers("Content-Disposition") = ContentDisposition(name)
                ' Ein gerechnetes Bild hat vorab keine Groesse: dann ohne Laenge, in Stuecken.
                If StreamingZipWriter.HasUnknownSizes(entries) Then
                    If context.Request.ProtocolVersion >= HttpVersion.Version11 Then response.SendChunked = True
                Else
                    response.ContentLength64 = StreamingZipWriter.TotalLength(entries)
                End If
                Await StreamingZipWriter.WriteAsync(response.OutputStream, entries)
                response.Close()
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is UnauthorizedAccessException OrElse
                                       TypeOf ex Is InvalidOperationException
                Try
                    response.Abort()
                Catch
                End Try
            End Try
        End Function

        ''' <summary>Die Eintraege des ZIP der mobilen Seite. JPEG, PNG, WebP, Videos und alles, was
        ''' keine Bilddatei ist, wie auf der Desktop-Seite (PlanDownloadEntries, mit Beistelldateien).
        ''' Jedes andere Bildformat (RAW, HEIC, TIFF, PSD, ...) und jede Variante als JPEG mit
        ''' Bearbeitung in voller Groesse (Name.jpg, ohne Beistelldateien): aus der mobilen Seite geht
        ''' keine solche Datei als Original hinaus (IsPortableImageFormat). Gerechnet wird erst beim
        ''' Schreiben, eines zur Zeit durch die Schleuse; misslingt es, fehlt nur dieses Bild.</summary>
        Friend Shared Function MobileZipEntries(keys As IList(Of String), canRead As Func(Of String, Boolean)) As List(Of ZipEntryPlan)
            Dim rendered = Function(key As String) VariantKey.IsVariant(key) OrElse
                                                   (MediaFileTypes.IsDisplayable(key) AndAlso Not MediaFileTypes.IsVideo(key) AndAlso Not IsPortableImageFormat(key))
            Dim result = PlanDownloadEntries(keys.Where(Function(k) Not rendered(k)).ToList())
            Dim used As New HashSet(Of String)(result.Select(Function(e) e.EntryName), StringComparer.OrdinalIgnoreCase)
            For Each key In keys.Where(rendered).Distinct(PathIdentity.Comparer)
                Dim filePath = VariantKey.FileOf(key)
                If Not File.Exists(filePath) Then Continue For
                Dim entryKey = key
                result.Add(New ZipEntryPlan With {
                    .SourcePath = filePath, .EntryName = UniqueEntryName(ShareRenderedName(key), used),
                    .Size = StreamingZipWriter.UnknownSize,
                    .Modified = File.GetLastWriteTime(filePath),
                    .Producer = Function() RenderFullMobileJpegSafe(entryKey, canRead)})
            Next
            Return result
        End Function

        ''' <summary>Wie <see cref="RenderFullMobileJpeg"/>, aber Nothing statt einer Ausnahme: im ZIP
        ''' soll ein Bild, das sich nicht rechnen laesst, fehlen, statt das Archiv abzubrechen.</summary>
        Friend Shared Function RenderFullMobileJpegSafe(key As String, canRead As Func(Of String, Boolean)) As Byte()
            Try
                Return RenderFullMobileJpeg(key, canRead)
            Catch ex As Exception When TypeOf ex IsNot OutOfMemoryException
                DiagnosticLogService.LogException("BrowserBridge.RenderFullJpeg", ex)
                Return Nothing
            End Try
        End Function

        ' --- Hochladen -----------------------------------------------------------------------------

        ''' <summary>POST /api/m/upload?folder=&amp;name=&amp;batch=. Eine Datei in einen Ordner, den
        ''' die Seite gewaehlt hat. Eine duenne Huelle um den Weg der Desktop-Seite: dieselbe
        ''' Bereinigung des Namens, dieselben Endungen und Grenzen, nie ueberschreiben, Teildatei bei
        ''' Abbruch weg, Beistelldateien folgen ihrem Bild. Anders ist nur das Ziel: es kommt vom
        ''' Kontext (MobileContext.CanUpload), nicht aus dem Fenster der Anwendung. Statt der Kennung
        ''' der Desktop-Seite verlangt der Weg den Kopf X-FerrumPix-Mobile, der jeder fremden Seite
        ''' eine Vorabfrage aufzwingt; die Herkunft ist schon geprueft.</summary>
        Private Shared Async Function HandleMobileUploadAsync(context As HttpListenerContext, mobile As MobileContext) As Task
            Dim request = context.Request
            context.Response.KeepAlive = False
            If request.Headers("X-FerrumPix-Mobile") <> "1" Then
                Await WriteResultAsync(context, 403, Nothing, LocalizationService.T("Die Seite ist veraltet. Bitte neu laden."))
                Return
            End If
            Dim folder = mobile.ResolveFolder(request.QueryString("folder"))
            If folder Is Nothing OrElse Not mobile.CanUpload(folder) Then
                Await WriteResultAsync(context, 403, Nothing, LocalizationService.T("Hier darf FerrumPix keine Dateien anlegen"))
                Return
            End If
            Dim fileName = SanitizeUploadName(request.QueryString("name"))
            If fileName Is Nothing Then
                Await WriteResultAsync(context, 400, Nothing, LocalizationService.T("Der Dateiname ist ungültig"))
                Return
            End If
            ' Beistelldateien und Projekte nur von einem Administrator (CarriesRecipe): sie nennen
            ' Dateien nach ihrem Pfad, auch aus Ordnern, die der Benutzer nicht sehen darf.
            If CarriesRecipe(fileName) AndAlso Not mobile.IsAdmin Then
                Await WriteResultAsync(context, 403, Nothing, LocalizationService.T("Bearbeitungen und Beistelldateien darf nur ein Administrator hochladen"))
                Return
            End If
            Dim given = If(request.QueryString("batch"), "")
            If given.Length > 32 Then given = given.Substring(0, 32)
            ' Eigene Stapel je Sitzung: zwei Telefone mit derselben Kennung stoeren sich nicht, und
            ' ein Stapel der Desktop-Seite ist nie gemeint.
            Dim batch = If(given.Length = 0, "", "m" & mobile.SessionKey.PadRight(8, "0"c).Substring(0, 8) & ":" & given)
            Dim isSidecar = IsSidecarName(fileName)
            Dim storedPath As String = Nothing
            ' Vor dem Empfang: nach der Antwort ist die Verbindung zu (KeepAlive aus), und ihre
            ' Adresse laesst sich nicht mehr lesen.
            Dim from = LogAddress(request)
            If Not isSidecar Then RememberBatchImage(batch, fileName, Nothing)
            ' Eine Zeile je Stapel, nicht je Datei (SecurityLog.NoteUpload); vermerkt, bevor die
            ' Antwort hinausgeht, damit der Stapel vollstaendig ist, wenn die Seite fertig meldet.
            Dim noteStored As Action =
                Sub() SecurityLog.NoteUpload(batch, SecurityLog.UserNameOf(mobile.UserKey), from,
                                             SecurityLog.ShownPath(folder, mobile.BrowseRoots()), "mobile")
            Try
                storedPath = Await ReceiveMobileUploadAsync(context, folder, fileName, batch, noteStored)
            Finally
                If Not isSidecar Then RememberBatchImage(batch, fileName, If(storedPath Is Nothing, Nothing, IO.Path.GetFileName(storedPath)))
            End Try
        End Function

        Private Shared Async Function ReceiveMobileUploadAsync(context As HttpListenerContext, folder As String, fileName As String, batch As String,
                                                               noteStored As Action) As Task(Of String)
            Dim request = context.Request
            Dim length = request.ContentLength64
            If length < 0 Then
                context.Response.StatusCode = 411
                context.Response.Close()
                Return Nothing
            End If
            If length = 0 Then
                Await WriteResultAsync(context, 400, Nothing, LocalizationService.T("Die Datei ist leer"))
                Return Nothing
            End If
            If length > MaxUploadBytes Then
                Await WriteResultAsync(context, 413, Nothing, MaxUploadText())
                Return Nothing
            End If
            If Not IsUploadableName(fileName) Then
                Await WriteResultAsync(context, 415, Nothing, LocalizationService.T("Diese Dateiart nimmt FerrumPix nicht an"))
                Return Nothing
            End If
            Dim sidecarImage As String = Nothing
            Dim sidecarImageStored As String = Nothing
            If IsSidecarName(fileName) Then
                Dim images = BatchImages(batch)
                sidecarImage = FindSidecarImage(fileName, images.Keys)
                If sidecarImage IsNot Nothing Then
                    sidecarImageStored = images(sidecarImage)
                    If sidecarImageStored Is Nothing Then
                        Await WriteResultAsync(context, 409, Nothing, LocalizationService.T("Das Bild zu dieser Beistelldatei ist nicht angekommen"))
                        Return Nothing
                    End If
                End If
            End If
            If FreeSpace(folder) < length + FreeSpaceReserveBytes Then
                Await WriteResultAsync(context, 507, Nothing, LocalizationService.T("Auf dem Laufwerk ist nicht genug Platz frei"))
                Return Nothing
            End If

            Dim partPath = IO.Path.Combine(folder, "." & fileName & "." & Guid.NewGuid().ToString("N").Substring(0, 12) & ".part")
            Dim finalPath As String = Nothing
            Try
                Dim complete = Await ReceiveBodyAsync(request.InputStream, partPath, length)
                If Not complete Then
                    Await WriteResultAsync(context, 400, Nothing, LocalizationService.T("Die Übertragung wurde unterbrochen"))
                    Return Nothing
                End If
                Dim preferred As List(Of String) = Nothing
                If sidecarImage IsNot Nothing Then
                    preferred = SidecarNamesFor(fileName, sidecarImage, IO.Path.Combine(folder, sidecarImageStored))
                End If
                finalPath = MovePartIntoPlace(partPath, folder, fileName, preferred)
            Finally
                If finalPath Is Nothing Then TryDelete(partPath)
            End Try

            ' Die Galerie liest den Ordner neu, falls sie ihn gerade zeigt; sonst aendert sich am
            ' Desktop nichts.
            Dim stored = UploadStored
            If stored IsNot Nothing Then PostToApp(Sub() stored(finalPath))
            If Not IsSidecarName(fileName) Then RememberBatchImage(batch, fileName, IO.Path.GetFileName(finalPath))
            ForgetMobileTimelines()
            noteStored?.Invoke()
            Await WriteResultAsync(context, 200, IO.Path.GetFileName(finalPath), Nothing)
            Return finalPath
        End Function

    End Class

End Namespace
