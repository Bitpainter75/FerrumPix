Imports System
Imports System.Collections.Concurrent
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Reflection
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Welche Kachel sich geändert hat - für die Karte, die sie dann neu zeichnet.</summary>
    Public NotInheritable Class MapTileUpdatedEventArgs
        Inherits EventArgs

        Public Sub New(template As String, zoom As Integer, x As Integer, y As Integer)
            Me.Template = template
            Me.Zoom = zoom
            Me.X = x
            Me.Y = y
        End Sub

        Public ReadOnly Property Template As String
        Public ReadOnly Property Zoom As Integer
        Public ReadOnly Property X As Integer
        Public ReadOnly Property Y As Integer
    End Class

    ''' <summary>Holt Kartenkacheln von einem Kachelserver und hält sie auf der Platte.
    '''
    ''' <para>DIE REGELN KOMMEN AUS DER KACHELRICHTLINIE DER OSM-STIFTUNG, und sie gelten für jeden
    ''' Server, der hier eingetragen wird: eine eigene Kennung im User-Agent, die die Anwendung
    ''' benennt (nie die Vorgabe der Bibliothek, nie ein Browser); die Kopfzeilen zum
    ''' Zwischenspeichern werden befolgt, ersatzweise sieben Tage; eine abgelaufene Kachel wird
    ''' BEDINGT nachgefragt (If-None-Match, If-Modified-Since) und nicht neu geladen; kein
    ''' Vorabladen, geholt wird nur, was die Karte gerade zeigt. Dazu höchstens zwei Verbindungen
    ''' gleichzeitig.</para>
    '''
    ''' <para>EINMAL GEHOLT, BLEIBT DIE KACHEL. Eine abgelaufene Kachel wird trotzdem sofort
    ''' gezeigt; die Nachfrage läuft dahinter und ersetzt sie nur, wenn der Server wirklich eine
    ''' neue schickt. Meist antwortet er mit 304 ohne Bilddaten. Ohne Netz bleibt es beim
    ''' gespeicherten Stand. Neu geladen wird ein Gebiet nur, wenn es noch nie angesehen wurde
    ''' oder der Speicher geleert ist. Nachgefragt wird je Kachel höchstens einmal je Sitzung,
    ''' auch wenn ein Server gar keine Frist nennt.</para>
    '''
    ''' <para>Der Speicher liegt im Anwendungsordner, nie im Fotobestand, und ist nach Server
    ''' getrennt: wer die Adresse wechselt, bekommt nicht die Kacheln des alten Servers
    ''' untergemischt. Wird er größer als die Grenze, fallen die am längsten nicht angesehenen
    ''' Kacheln heraus.</para></summary>
    Public NotInheritable Class MapTileService

        Public Const DefaultTileUrl As String = "https://tile.openstreetmap.org/{z}/{x}/{y}.png"

        ''' <summary>Die höchste Zoomstufe, die angefragt wird. Der OSM-Server liefert bis 19.</summary>
        Public Const MaxZoom As Integer = 19

        Public Const DefaultCacheLimitBytes As Long = 1024L * 1024L * 1024L

        ''' <summary>Wenn der Server keine Frist nennt: so lange gilt eine Kachel, bevor nachgefragt
        ''' wird. Die Richtlinie nennt sieben Tage als Mindestmaß.</summary>
        Public Shared ReadOnly FallbackLifetime As TimeSpan = TimeSpan.FromDays(7)

        Private Const MaxParallelDownloads As Integer = 2
        Private Const ProjectUrl As String = "https://github.com/Bitpainter75/FerrumPix"
        Private Const TileExtension As String = ".tile"
        Private Const MetaExtension As String = ".json"

        Public Shared ReadOnly DefaultCacheRoot As String =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix", "MapTileCache")

        Private Shared ReadOnly _instance As New Lazy(Of MapTileService)(
            Function() New MapTileService(DefaultCacheRoot, Nothing, DefaultCacheLimitBytes))

        Public Shared ReadOnly Property Instance As MapTileService
            Get
                Return _instance.Value
            End Get
        End Property

        Private ReadOnly _root As String
        Private ReadOnly _client As HttpClient
        Private ReadOnly _limitBytes As Long
        Private ReadOnly _downloadGate As New SemaphoreSlim(MaxParallelDownloads)
        Private ReadOnly _inFlight As New ConcurrentDictionary(Of String, Lazy(Of Task(Of Byte())))(StringComparer.Ordinal)
        Private ReadOnly _revalidated As New ConcurrentDictionary(Of String, Byte)(StringComparer.Ordinal)
        Private ReadOnly _loggedFailures As New ConcurrentDictionary(Of String, Byte)(StringComparer.Ordinal)
        Private ReadOnly _sizeLock As New Object()
        Private _knownSizeBytes As Long = -1
        Private _trimRunning As Integer

        ''' <summary>Die Uhr, an der die Fristen gemessen werden. Der Prüfstand stellt sie vor.</summary>
        Public Property Clock As Func(Of DateTime) = Function() DateTime.UtcNow

        ''' <summary>Eine gezeigte Kachel ist ersetzt worden. Kommt aus einem Hintergrundfaden.</summary>
        Public Event TileUpdated As EventHandler(Of MapTileUpdatedEventArgs)

        ''' <param name="handler">Nur für den Prüfstand: ein Ersatz für das Netz. Nothing heißt
        ''' echtes HTTP.</param>
        Public Sub New(cacheRoot As String, handler As HttpMessageHandler, limitBytes As Long)
            _root = cacheRoot
            _limitBytes = Math.Max(1L, limitBytes)
            _client = If(handler Is Nothing, New HttpClient(), New HttpClient(handler, disposeHandler:=False))
            _client.Timeout = TimeSpan.FromSeconds(30)
            _client.DefaultRequestHeaders.UserAgent.Add(New ProductInfoHeaderValue("FerrumPix", AppVersionToken()))
            _client.DefaultRequestHeaders.UserAgent.Add(New ProductInfoHeaderValue("(+" & ProjectUrl & ")"))
        End Sub

        ''' <summary>Die Kennung, mit der jede Anfrage hinausgeht.</summary>
        Public ReadOnly Property UserAgent As String
            Get
                Return _client.DefaultRequestHeaders.UserAgent.ToString()
            End Get
        End Property

        Private Shared Function AppVersionToken() As String
            Dim asm = Assembly.GetExecutingAssembly()
            Dim informational = asm.GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()?.InformationalVersion
            Dim version = If(String.IsNullOrWhiteSpace(informational), asm.GetName().Version?.ToString(3), informational)
            If String.IsNullOrWhiteSpace(version) Then Return "0"
            Dim plus = version.IndexOf("+"c)
            If plus >= 0 Then version = version.Substring(0, plus)
            ' Nur Zeichen, die in einem Produkt-Token erlaubt sind.
            Dim clean = New String(version.Where(Function(c) Char.IsLetterOrDigit(c) OrElse c = "."c OrElse c = "-"c).ToArray())
            Return If(clean.Length = 0, "0", clean)
        End Function

        ' ------------------------------------------------------------------------------------
        ' Adresse
        ' ------------------------------------------------------------------------------------

        ''' <summary>Eine Adresse taugt, wenn sie {z}, {x} und {y} trägt und nach dem Einsetzen
        ''' eine vollständige http- oder https-Adresse ergibt.</summary>
        Public Shared Function IsValidTemplate(template As String) As Boolean
            If String.IsNullOrWhiteSpace(template) Then Return False
            Dim trimmed = template.Trim()
            If trimmed.IndexOf("{z}", StringComparison.Ordinal) < 0 OrElse
               trimmed.IndexOf("{x}", StringComparison.Ordinal) < 0 OrElse
               trimmed.IndexOf("{y}", StringComparison.Ordinal) < 0 Then Return False
            Dim uri As Uri = Nothing
            If Not Uri.TryCreate(BuildUrl(trimmed, 1, 0, 0), UriKind.Absolute, uri) Then Return False
            Return uri.Scheme = Uri.UriSchemeHttps OrElse uri.Scheme = Uri.UriSchemeHttp
        End Function

        Public Shared Function NormalizeTemplate(template As String) As String
            Return If(IsValidTemplate(template), template.Trim(), DefaultTileUrl)
        End Function

        Public Shared Function BuildUrl(template As String, zoom As Integer, x As Integer, y As Integer) As String
            Return template.
                Replace("{z}", zoom.ToString(CultureInfo.InvariantCulture)).
                Replace("{x}", x.ToString(CultureInfo.InvariantCulture)).
                Replace("{y}", y.ToString(CultureInfo.InvariantCulture))
        End Function

        ' ------------------------------------------------------------------------------------
        ' Holen
        ' ------------------------------------------------------------------------------------

        ''' <summary>Die Bilddaten einer Kachel, aus dem Speicher oder vom Server. Nothing, wenn es
        ''' sie weder hier noch dort gibt. Gleichzeitige Anfragen nach derselben Kachel teilen sich
        ''' einen Weg.</summary>
        Public Function GetTileAsync(template As String, zoom As Integer, x As Integer, y As Integer,
                                     cancellationToken As CancellationToken) As Task(Of Byte())
            Dim key = TileKey(template, zoom, x, y)
            Dim created = New Lazy(Of Task(Of Byte()))(
                Function() LoadAsync(template, zoom, x, y, cancellationToken))
            Dim entry = _inFlight.GetOrAdd(key, created)
            ' Ein FERTIGER Eintrag wird ersetzt, nicht geteilt. Er fliegt erst kurz nach seinem Ende
            ' aus der Liste; wer in dieser Lücke fragt, bekaeme sonst das alte Ergebnis, und die
            ' Pruefung auf Ablauf fiele aus.
            If entry IsNot created AndAlso entry.IsValueCreated AndAlso entry.Value.IsCompleted AndAlso
               _inFlight.TryUpdate(key, created, entry) Then
                entry = created
            End If
            Dim task = entry.Value
            If entry Is created Then
                task.ContinueWith(Sub(t) _inFlight.TryRemove(New KeyValuePair(Of String, Lazy(Of Task(Of Byte())))(key, created)),
                                  TaskScheduler.Default)
            End If
            Return task
        End Function

        Private Async Function LoadAsync(template As String, zoom As Integer, x As Integer, y As Integer,
                                         cancellationToken As CancellationToken) As Task(Of Byte())
            Dim tilePath = TilePathOf(template, zoom, x, y)
            Dim cached As Byte() = Nothing
            Try
                ' ConfigureAwait(False) an jedem Await dieses Dienstes: er hat keine Oberflaeche, und wer
                ' synchron auf ihn wartet, darf nicht darauf warten muessen, dass der wartende Faden
                ' frei wird.
                If File.Exists(tilePath) Then cached = Await File.ReadAllBytesAsync(tilePath, cancellationToken).ConfigureAwait(False)
            Catch ex As IOException
                cached = Nothing
            Catch ex As UnauthorizedAccessException
                cached = Nothing
            End Try

            If cached IsNot Nothing AndAlso cached.Length > 0 Then
                Dim meta = ReadMeta(MetaPathOf(tilePath))
                TouchForEviction(tilePath)
                If meta Is Nothing OrElse meta.ExpiresUtc <= Clock.Invoke() Then
                    QueueRevalidation(template, zoom, x, y, meta)
                End If
                Return cached
            End If

            Dim outcome = Await FetchAsync(template, zoom, x, y, Nothing, cancellationToken).ConfigureAwait(False)
            Return outcome.Bytes
        End Function

        ''' <summary>Die abgelaufene Kachel ist schon gezeigt; hier fragt der Speicher nach, ob es
        ''' eine neuere gibt. Je Kachel einmal je Sitzung.</summary>
        Private Sub QueueRevalidation(template As String, zoom As Integer, x As Integer, y As Integer, meta As TileMeta)
            If Not _revalidated.TryAdd(TileKey(template, zoom, x, y), 0) Then Return
            Dim ignored = Task.Run(
                Async Function()
                    Try
                        Dim outcome = Await FetchAsync(template, zoom, x, y, If(meta, New TileMeta()), CancellationToken.None).ConfigureAwait(False)
                        If outcome.Status = FetchStatus.Replaced Then
                            RaiseEvent TileUpdated(Me, New MapTileUpdatedEventArgs(template, zoom, x, y))
                        End If
                    Catch ex As Exception
                        DiagnosticLogService.LogException("MapTile.Revalidate", ex)
                    End Try
                End Function)
        End Sub

        Private Enum FetchStatus
            Replaced
            NotModified
            Failed
        End Enum

        Private Structure FetchOutcome
            Public Status As FetchStatus
            Public Bytes As Byte()
        End Structure

        ''' <summary>Eine Anfrage an den Server. Mit <paramref name="meta"/> ist sie bedingt: dann
        ''' heißt 304, dass die gespeicherte Kachel weiter gilt, und nur ihre Frist wird
        ''' erneuert.</summary>
        Private Async Function FetchAsync(template As String, zoom As Integer, x As Integer, y As Integer,
                                          meta As TileMeta, cancellationToken As CancellationToken) As Task(Of FetchOutcome)
            Dim tilePath = TilePathOf(template, zoom, x, y)
            Dim metaPath = MetaPathOf(tilePath)
            Await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(False)
            Try
                Using request = New HttpRequestMessage(HttpMethod.Get, BuildUrl(template, zoom, x, y))
                    If meta IsNot Nothing Then
                        If Not String.IsNullOrEmpty(meta.ETag) Then request.Headers.TryAddWithoutValidation("If-None-Match", meta.ETag)
                        If Not String.IsNullOrEmpty(meta.LastModified) Then request.Headers.TryAddWithoutValidation("If-Modified-Since", meta.LastModified)
                    End If

                    Using response = Await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(False)
                        If response.StatusCode = HttpStatusCode.NotModified AndAlso meta IsNot Nothing Then
                            meta.ExpiresUtc = ExpiryOf(response)
                            If response.Headers.ETag IsNot Nothing Then meta.ETag = response.Headers.ETag.ToString()
                            WriteMeta(metaPath, meta)
                            Return New FetchOutcome With {.Status = FetchStatus.NotModified}
                        End If

                        If Not response.IsSuccessStatusCode Then
                            LogFailureOnce("HTTP " & CInt(response.StatusCode).ToString(CultureInfo.InvariantCulture))
                            Return New FetchOutcome With {.Status = FetchStatus.Failed}
                        End If

                        Dim bytes = Await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(False)
                        If bytes Is Nothing OrElse bytes.Length = 0 Then Return New FetchOutcome With {.Status = FetchStatus.Failed}

                        Dim lastModified = response.Content.Headers.LastModified
                        Dim fresh As New TileMeta With {
                            .ETag = If(response.Headers.ETag?.ToString(), ""),
                            .LastModified = If(lastModified.HasValue, lastModified.Value.ToString("R", CultureInfo.InvariantCulture), ""),
                            .ExpiresUtc = ExpiryOf(response)
                        }
                        StoreTile(tilePath, metaPath, bytes, fresh)
                        Return New FetchOutcome With {.Status = FetchStatus.Replaced, .Bytes = bytes}
                    End Using
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As HttpRequestException
                LogFailureOnce(ex.Message)
                Return New FetchOutcome With {.Status = FetchStatus.Failed}
            Catch ex As OperationCanceledException
                ' Die Zeitgrenze des Clients, nicht der Aufrufer.
                LogFailureOnce("Zeitgrenze")
                Return New FetchOutcome With {.Status = FetchStatus.Failed}
            Catch ex As IOException
                DiagnosticLogService.LogException("MapTile.Store", ex)
                Return New FetchOutcome With {.Status = FetchStatus.Failed}
            Finally
                _downloadGate.Release()
            End Try
        End Function

        ''' <summary>Bis wann die Kachel ohne Nachfrage gilt: Cache-Control vor Expires, ohne
        ''' beides sieben Tage. no-cache und no-store heißen "jedes Mal fragen" - und das heißt
        ''' hier: einmal je Sitzung, denn gezeigt wird die gespeicherte Kachel trotzdem.</summary>
        Private Function ExpiryOf(response As HttpResponseMessage) As DateTime
            Dim now = Clock.Invoke()
            Dim cacheControl = response.Headers.CacheControl
            If cacheControl IsNot Nothing Then
                If cacheControl.NoCache OrElse cacheControl.NoStore Then Return now
                If cacheControl.MaxAge.HasValue Then
                    Dim age = If(response.Headers.Age, TimeSpan.Zero)
                    Dim remaining = cacheControl.MaxAge.Value - age
                    Return If(remaining > TimeSpan.Zero, now + remaining, now)
                End If
            End If
            Dim expires = response.Content.Headers.Expires
            If expires.HasValue Then Return expires.Value.UtcDateTime
            Return now + FallbackLifetime
        End Function

        Private Sub LogFailureOnce(reason As String)
            If _loggedFailures.TryAdd(reason, 0) Then
                DiagnosticLogService.LogAlways("MapTile", "Kachel nicht geladen: " & reason)
            End If
        End Sub

        ' ------------------------------------------------------------------------------------
        ' Ablage
        ' ------------------------------------------------------------------------------------

        Private NotInheritable Class TileMeta
            Public Property ETag As String = ""
            Public Property LastModified As String = ""
            Public Property ExpiresUtc As DateTime
        End Class

        Private Shared Function TileKey(template As String, zoom As Integer, x As Integer, y As Integer) As String
            Return String.Concat(template, "|", zoom.ToString(CultureInfo.InvariantCulture), "/",
                                 x.ToString(CultureInfo.InvariantCulture), "/", y.ToString(CultureInfo.InvariantCulture))
        End Function

        ''' <summary>Je Server ein eigener Ordner, benannt nach einer Prüfsumme der Adresse.</summary>
        Private Function ServerFolderOf(template As String) As String
            Dim hash = SHA1.HashData(Encoding.UTF8.GetBytes(template))
            Return Path.Combine(_root, Convert.ToHexString(hash, 0, 8).ToLowerInvariant())
        End Function

        Private Function TilePathOf(template As String, zoom As Integer, x As Integer, y As Integer) As String
            Return Path.Combine(ServerFolderOf(template),
                                zoom.ToString(CultureInfo.InvariantCulture),
                                x.ToString(CultureInfo.InvariantCulture),
                                y.ToString(CultureInfo.InvariantCulture) & TileExtension)
        End Function

        Private Shared Function MetaPathOf(tilePath As String) As String
            Return Path.ChangeExtension(tilePath, MetaExtension)
        End Function

        Private Shared Function ReadMeta(metaPath As String) As TileMeta
            Try
                If Not File.Exists(metaPath) Then Return Nothing
                Return JsonSerializer.Deserialize(Of TileMeta)(File.ReadAllText(metaPath))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Nothing
            End Try
        End Function

        Private Shared Sub WriteMeta(metaPath As String, meta As TileMeta)
            Try
                WriteAtomic(metaPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta)))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                DiagnosticLogService.LogException("MapTile.WriteMeta", ex)
            End Try
        End Sub

        ''' <summary>Erst in eine Nachbardatei, dann umbenennen: eine halb geschriebene Kachel darf
        ''' nie als gültige gelesen werden.</summary>
        Private Shared Sub WriteAtomic(targetPath As String, bytes As Byte())
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath))
            Dim temporary = targetPath & "." & Guid.NewGuid().ToString("N") & ".tmp"
            File.WriteAllBytes(temporary, bytes)
            File.Move(temporary, targetPath, overwrite:=True)
        End Sub

        Private Sub StoreTile(tilePath As String, metaPath As String, bytes As Byte(), meta As TileMeta)
            Dim previous = 0L
            Try
                If File.Exists(tilePath) Then previous = New FileInfo(tilePath).Length
            Catch ex As IOException
                previous = 0
            End Try
            WriteAtomic(tilePath, bytes)
            WriteMeta(metaPath, meta)
            AccountAndTrim(bytes.Length - previous)
        End Sub

        ''' <summary>Das Änderungsdatum der Datei ist die Reihenfolge für das Ausräumen. Es wird
        ''' beim Ansehen höchstens einmal am Tag nachgezogen, damit nicht jedes Zeichnen schreibt.</summary>
        Private Sub TouchForEviction(tilePath As String)
            Try
                Dim now = Clock.Invoke()
                If now - File.GetLastWriteTimeUtc(tilePath) > TimeSpan.FromDays(1) Then
                    File.SetLastWriteTimeUtc(tilePath, now)
                End If
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                ' Nur die Reihenfolge des Ausräumens leidet.
            End Try
        End Sub

        ' ------------------------------------------------------------------------------------
        ' Größe und Leeren
        ' ------------------------------------------------------------------------------------

        Private Sub AccountAndTrim(deltaBytes As Long)
            Dim needsMeasure As Boolean
            Dim overLimit As Boolean
            SyncLock _sizeLock
                If _knownSizeBytes < 0 Then
                    needsMeasure = True
                Else
                    _knownSizeBytes += deltaBytes
                    overLimit = _knownSizeBytes > _limitBytes
                End If
            End SyncLock
            If needsMeasure OrElse overLimit Then StartTrim()
        End Sub

        Private Sub StartTrim()
            If Interlocked.Exchange(_trimRunning, 1) = 1 Then Return
            Dim ignored = Task.Run(
                Sub()
                    Try
                        TrimToLimit()
                    Catch ex As Exception
                        DiagnosticLogService.LogException("MapTile.Trim", ex)
                    Finally
                        Interlocked.Exchange(_trimRunning, 0)
                    End Try
                End Sub)
        End Sub

        ''' <summary>Misst den Speicher und räumt, wenn er über der Grenze liegt, die ältesten
        ''' Kacheln aus, bis er wieder bei vier Fünfteln steht - sonst ginge es mit jeder neuen
        ''' Kachel von vorn los.</summary>
        Public Sub TrimToLimit()
            Dim tiles = EnumerateTiles().ToList()
            Dim total = tiles.Sum(Function(t) t.Length)
            If total > _limitBytes Then
                Dim target = _limitBytes * 4 \ 5
                For Each tile In tiles.OrderBy(Function(t) t.LastWriteTimeUtc)
                    If total <= target Then Exit For
                    Try
                        Dim length = tile.Length
                        tile.Delete()
                        File.Delete(MetaPathOf(tile.FullName))
                        total -= length
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                        ' Liegt gerade in Arbeit - beim nächsten Durchgang.
                    End Try
                Next
            End If
            SyncLock _sizeLock
                _knownSizeBytes = total
            End SyncLock
        End Sub

        Private Function EnumerateTiles() As IEnumerable(Of FileInfo)
            If Not Directory.Exists(_root) Then Return Enumerable.Empty(Of FileInfo)()
            Try
                Return New DirectoryInfo(_root).EnumerateFiles("*" & TileExtension, SearchOption.AllDirectories).ToList()
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return Enumerable.Empty(Of FileInfo)()
            End Try
        End Function

        ''' <summary>Belegter Platz in Bytes, gemessen an den Kacheln selbst.</summary>
        Public Function CacheSizeBytes() As Long
            Return EnumerateTiles().Sum(Function(t) t.Length)
        End Function

        ''' <summary>Leert den ganzen Speicher, über alle Server. Gibt die Zahl der entfernten
        ''' Kacheln zurück. Danach wird jedes Gebiet beim nächsten Ansehen neu geholt.</summary>
        Public Function ClearCache() As Integer
            Dim count = EnumerateTiles().Count()
            Try
                If Directory.Exists(_root) Then Directory.Delete(_root, recursive:=True)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                DiagnosticLogService.LogException("MapTile.ClearCache", ex)
            End Try
            _revalidated.Clear()
            SyncLock _sizeLock
                _knownSizeBytes = 0
            End SyncLock
            Return count
        End Function

    End Class

End Namespace
