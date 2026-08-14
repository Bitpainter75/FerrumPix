Imports System.Globalization
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports System.Xml.Linq

Namespace Services

    ''' <summary>Der Rueckfall auf Nextcloud Photos und den Kern, wenn Memories nicht installiert
    ''' ist.
    '''
    ''' WARUM DAS UEBERHAUPT GEHT: Memories ist eine ANSICHT auf den Dateibaum, keine eigene Ablage
    ''' (siehe NextcloudService.vb). Alles, was die Anbindung braucht, liegt also auch ohne die App
    ''' da - es steht nur woanders. Der weitaus groessere Teil der Anbindung laeuft ohnehin schon am
    ''' Kern und nicht an Memories: Favorit, Stichwoerter schreiben, Papierkorb, Textsuche,
    ''' Hochladen, Original ersetzen, Begleitdatei und die Alben SELBST. Was fehlte, waren fuenf
    ''' Wege, und die stehen hier:
    '''
    ''' | Memories | Rueckfall |
    ''' |---|---|
    ''' | GET api/days, api/days/{id} | SEARCH ueber den Dateibaum, Tage selbst gebildet |
    ''' | GET api/clusters/albums | PROPFIND remote.php/dav/photos/{benutzer}/albums/ |
    ''' | GET api/clusters/tags | PROPFIND remote.php/dav/systemtags/ |
    ''' | GET api/image/preview/{id} | GET index.php/core/preview?fileId= |
    ''' | GET api/image/info/{id} | PROPFIND auf den Pfad plus systemtags-relations |
    ''' | GET api/stream/{id} | GET auf den Pfad im Dateibaum |
    '''
    ''' WAS DER RUECKFALL NICHT KANN, und das ist kein Versehen: Personen und Orte sind bei Memories
    ''' Cluster einer Zusatz-App. Ohne Memories gibt es dafuer keinen Weg, der auf jeder Installation
    ''' da waere - die beiden Zweige bleiben deshalb WEG, statt leer im Baum zu stehen. Das ist
    ''' dieselbe Regel wie bei einer nicht eingeschalteten Zusatz-App (siehe
    ''' AddNextcloudClusterBranch): ein Knoten, unter dem nie etwas auftaucht, sieht aus wie ein
    ''' Fehler.</summary>
    Partial Public Class NextcloudService

        ''' <summary>Welchen Weg dieser Server traegt.</summary>
        Public Enum ServerMode
            ''' <summary>Noch nicht gefragt.</summary>
            Unknown = 0
            ''' <summary>Memories ist da - der volle Weg.</summary>
            Memories = 1
            ''' <summary>Kein Memories - Photos-App und Kern.</summary>
            Photos = 2
        End Enum

        Private Shared _mode As ServerMode = ServerMode.Unknown
        Private Shared _modeSignature As String = ""
        Private Shared _modeChecked As DateTime = DateTime.MinValue
        Private Shared ReadOnly _modeLock As New Object()

        ''' <summary>Wie lange die erkannte Betriebsart gilt.
        '''
        ''' NICHT FUER IMMER, und der Grund ist die Gegenrichtung: wer Memories waehrend der Sitzung
        ''' nachinstalliert, saehe sonst bis zum Neustart weiter den Rueckfall - und wem die App
        ''' ausfaellt, dem blieben bis dahin nur Fehlermeldungen. Eine Viertelstunde ist eine Anfrage
        ''' und beantwortet beides von selbst.</summary>
        Private Shared ReadOnly ModeLifetime As TimeSpan = TimeSpan.FromMinutes(15)

        ''' <summary>Der zuletzt erkannte Weg. Fuer Anzeige und Pruefstand; die Abrufe fragen
        ''' <see cref="EnsureModeAsync"/>, das notfalls selbst nachsieht.</summary>
        Public Shared ReadOnly Property CurrentMode As ServerMode
            Get
                SyncLock _modeLock
                    Return If(String.Equals(_modeSignature, ConnectionSignature(), StringComparison.Ordinal),
                              _mode, ServerMode.Unknown)
                End SyncLock
            End Get
        End Property

        ''' <summary>Server, Benutzer und Passwort als eine Zeichenkette. Sie ist der Schluessel, an
        ''' dem Client UND erkannter Weg haengen: nach einem Kontowechsel gilt beides nicht mehr.</summary>
        Private Shared Function ConnectionSignature() As String
            Dim s = AppSettingsService.Load()
            Return NormalizeServerUrl(s.NextcloudServerUrl) & "|" & If(s.NextcloudUserName, "") & "|" & If(s.NextcloudAppPassword, "")
        End Function

        ''' <summary>Erkennt EINMAL je Server und Konto, ob Memories antwortet.
        '''
        ''' Gefragt wird die Zeitachse selbst und nicht /api/describe: describe ist oeffentlich und
        ''' antwortet auch dann, wenn die App zwar liegt, aber fuer diesen Benutzer nichts liefert.
        ''' Entschieden wird an der FORM der Antwort - eine Liste ist die Zeitachse, ein Objekt ist
        ''' die Absage der App (siehe ServerMessage).
        '''
        ''' KEINE ANTWORT WIRD NICHT GEMERKT. Ein Netzaussetzer darf einen Server mit Memories nicht
        ''' dauerhaft auf den Rueckfall festlegen; beim naechsten Abruf wird deshalb addedNow gefragt.</summary>
        ''' <summary>Zwingt den Rueckfall, auch wenn Memories antwortet.
        '''
        ''' Zwei Zwecke, und beide sind echt: der Pruefstand misst damit den zweiten Weg auf einem
        ''' Server, auf dem die App liegt (sonst waere er nur dort pruefbar, wo sie fehlt), und wer
        ''' eine kaputte oder sehr langsame Memories-Installation hat, kommt daran vorbei, ohne sie
        ''' abschalten zu muessen.</summary>
        Public Shared Property ForceFallback As Boolean = False

        Friend Shared Async Function EnsureModeAsync(cancellationToken As CancellationToken) As Task(Of ServerMode)
            If ForceFallback Then Return ServerMode.Photos
            Dim signature = ConnectionSignature()
            SyncLock _modeLock
                If _mode <> ServerMode.Unknown AndAlso String.Equals(_modeSignature, signature, StringComparison.Ordinal) AndAlso
                   DateTime.UtcNow - _modeChecked < ModeLifetime Then
                    Return _mode
                End If
            End SyncLock

            Dim detected As ServerMode
            Try
                Dim response = Await GetClient().GetAsync(ApiUrl("days"), cancellationToken).ConfigureAwait(False)
                If response.StatusCode = HttpStatusCode.Unauthorized Then
                    ' Die Anmeldung stimmt nicht. Das ist keine Aussage ueber Memories, und ein
                    ' gemerkter Rueckfall waere hier nur falsch.
                    Return ServerMode.Photos
                End If
                If Not response.IsSuccessStatusCode Then
                    detected = ServerMode.Photos
                Else
                    Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    detected = ServerMode.Photos
                    Try
                        Using doc = JsonDocument.Parse(body)
                            If doc.RootElement.ValueKind = JsonValueKind.Array Then detected = ServerMode.Memories
                        End Using
                    Catch
                        ' Kein JSON: dann hat hier etwas anderes geantwortet als die App.
                    End Try
                End If
            Catch ex As OperationCanceledException
                Throw
            Catch
                Return ServerMode.Photos
            End Try

            SyncLock _modeLock
                ' Wechselt die Betriebsart, ist die gemerkte Zeitachse von der anderen Seite.
                If _mode <> detected Then DropTimelineCache()
                _mode = detected
                _modeSignature = signature
                _modeChecked = DateTime.UtcNow
            End SyncLock
            Return detected
        End Function

        ''' <summary>Welchen Weg dieser Server traegt, notfalls beim Server nachgefragt. Fuer den
        ''' Pruefstand und fuer jeden, der die Frage beantworten muss, bevor er etwas behauptet.</summary>
        Public Shared Function GetServerModeAsync(Optional cancellationToken As CancellationToken = Nothing) As Task(Of ServerMode)
            If Not IsConfigured Then Return Task.FromResult(ServerMode.Unknown)
            Return EnsureModeAsync(cancellationToken)
        End Function

        ''' <summary>Vergisst den erkannten Weg. Gebraucht, wenn der Nutzer die Zugangsdaten aendert
        ''' oder der Pruefstand beide Wege nacheinander messen will.</summary>
        Public Shared Sub ResetMode()
            SyncLock _modeLock
                _mode = ServerMode.Unknown
                _modeSignature = ""
                _modeChecked = DateTime.MinValue
            End SyncLock
            DropTimelineCache()
        End Sub

        ''' <summary>Der Verbindungstest fuer einen Server OHNE Memories.
        '''
        ''' Er beantwortet dieselben Fragen wie der Test daneben, nur an den anderen Wegen: traegt
        ''' WebDAV, findet die Suche Bilder, gibt es Alben und Stichwoerter. UND ER SAGT ES: dass
        ''' Memories fehlt, ist kein Fehler, aber der Nutzer soll wissen, warum es keine Personen
        ''' gibt - sonst sucht er den Grund bei sich.
        '''
        ''' Gerechnet wird mit dem MITGEGEBENEN Client: der Test laeuft, bevor die Zugangsdaten
        ''' gespeichert sind, und darf deshalb nicht ueber die Einstellungen gehen.</summary>
        Private Shared Async Function TestFallbackConnectionAsync(client As HttpClient, baseUrl As String, userName As String,
                                                                   result As NextcloudConnectionResult,
                                                                   cancellationToken As CancellationToken) As Task(Of NextcloudConnectionResult)
            Dim davFiles = baseUrl & "/remote.php/dav/files/" & Uri.EscapeDataString(userName.Trim()) & "/"

            ' 1. Traegt WebDAV? Ohne das gibt es hier gar nichts.
            Using request = New HttpRequestMessage(New HttpMethod("PROPFIND"), davFiles)
                request.Headers.Add("Depth", "0")
                Dim response = Await client.SendAsync(request, cancellationToken).ConfigureAwait(False)
                If response.StatusCode = HttpStatusCode.Unauthorized Then
                    result.Message = LocalizationService.T("Server erreichbar, Anmeldung abgelehnt")
                    Return result
                End If
                If CInt(response.StatusCode) <> 207 Then
                    result.Message = String.Format(LocalizationService.T("WebDAV antwortet mit {0}"), CInt(response.StatusCode))
                    Return result
                End If
            End Using
            result.Report.Add(LocalizationService.T("Ohne Memories: Fotos kommen aus der Photos-App"))

            ' 2. Die Zeitachse. Sie ist hier eine Suche, und ob der Server sie traegt, ist die eine
            ' Frage, an der der ganze Rueckfall haengt.
            Dim searchBody = "<?xml version=""1.0"" encoding=""UTF-8""?>" &
                        "<d:searchrequest xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"" xmlns:nc=""http://nextcloud.org/ns"">" &
                        "<d:basicsearch><d:select><d:prop><oc:fileid/></d:prop></d:select>" &
                        "<d:from><d:scope><d:href>/files/" & XmlText(userName.Trim()) & "</d:href><d:depth>infinity</d:depth></d:scope></d:from>" &
                        "<d:where><d:like><d:prop><d:getcontenttype/></d:prop><d:literal>image/%</d:literal></d:like></d:where>" &
                        "<d:limit><d:nresults>10000</d:nresults></d:limit>" &
                        "</d:basicsearch></d:searchrequest>"
            Using request = New HttpRequestMessage(New HttpMethod("SEARCH"), baseUrl & "/remote.php/dav/")
                request.Headers.TryAddWithoutValidation("Depth", "0")
                request.Content = New StringContent(searchBody, Encoding.UTF8, "application/xml")
                Dim response = Await client.SendAsync(request, cancellationToken).ConfigureAwait(False)
                If CInt(response.StatusCode) <> 207 Then
                    result.Message = String.Format(LocalizationService.T("Die Suche nach Fotos antwortet mit {0}"), CInt(response.StatusCode))
                    result.Report.Add(result.Message)
                    Return result
                End If
                Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                Dim found = Text.RegularExpressions.Regex.Matches(body, "<oc:fileid>").Count
                result.Report.Add(String.Format(LocalizationService.T("{0} Aufnahmen gefunden"), found))
            End Using

            ' 3. Alben und Stichwoerter. Beide gibt es auch ohne Memories; fehlt eins, bleibt sein
            ' Zweig im Baum weg, und der Nutzer soll das hier lesen koennen.
            Await CountFallbackBranchAsync(client, baseUrl & "/remote.php/dav/photos/" & Uri.EscapeDataString(userName.Trim()) & "/albums/",
                                           LocalizationService.T("Alben"), result, cancellationToken).ConfigureAwait(False)
            Await CountFallbackBranchAsync(client, baseUrl & "/remote.php/dav/systemtags/",
                                           LocalizationService.T("Stichwörter"), result, cancellationToken).ConfigureAwait(False)
            result.Report.Add(LocalizationService.T("Ohne die Memories-App kennt dieser Server keine Personen"))

            result.Ok = True
            result.Message = String.Join(" · ", result.Report)
            Return result
        End Function

        ''' <summary>Zaehlt die Eintraege einer WebDAV-Sammlung fuer den Befund. Ein Fehlschlag ist
        ''' hier kein Abbruch: der Zweig fehlt dann eben, die Verbindung steht trotzdem.</summary>
        Private Shared Async Function CountFallbackBranchAsync(client As HttpClient, url As String, name As String,
                                                                result As NextcloudConnectionResult,
                                                                cancellationToken As CancellationToken) As Task
            Try
                Using request = New HttpRequestMessage(New HttpMethod("PROPFIND"), url)
                    request.Headers.Add("Depth", "1")
                    Dim response = Await client.SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If CInt(response.StatusCode) <> 207 Then
                        result.Report.Add(name & ": " & String.Format(LocalizationService.T("Nextcloud antwortet mit {0}"), CInt(response.StatusCode)))
                        Return
                    End If
                    Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    ' Der erste Eintrag ist die Sammlung selbst und zaehlt nicht mit.
                    Dim found = Math.Max(0, Text.RegularExpressions.Regex.Matches(body, "<d:response>").Count - 1)
                    result.Report.Add(name & ": " & found.ToString(CultureInfo.InvariantCulture))
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                result.Report.Add(name & ": " & ex.Message)
            End Try
        End Function

        ' ── Zeitachse aus einer WebDAV-Suche ────────────────────────────────────
        '
        ' Ohne Memories gibt es keine fertige Tagesliste. Sie entsteht hier aus EINER Suche ueber den
        ' Dateibaum: der Server liefert die Bilder samt Aufnahmezeit, gruppiert wird selbst. Das ist
        ' derselbe Weg, den die Photos-App fuer ihre Zeitachse geht.
        '
        ' Die Tageskennung wird GENAUSO gebildet wie bei Memories (Tage seit 1970, UTC), damit alles
        ' dahinter unveraendert weiterlaeuft - Sortierung, Fortschritt, der Abruf je Tag.

        ''' <summary>Wie viele Aufnahmen der Rueckfall hoechstens holt. Eine Suche ohne Deckel kann
        ''' auf einem grossen Bestand minutenlang laufen; erreicht sie den Deckel, SAGT sie es (siehe
        ''' <see cref="LastError"/>), statt stillschweigend einen Ausschnitt fuer den Bestand
        ''' auszugeben.
        '''
        ''' Veraenderbar, weil ein Deckel sonst nur dort messbar waere, wo jemand 20000 Aufnahmen
        ''' liegen hat: der Pruefstand setzt ihn klein und zaehlt nach. Genau an dieser Stelle ist ein
        ''' Fehler um eine Seite besonders leicht und besonders unauffaellig.</summary>
        Public Shared Property FallbackTimelineLimit As Integer = 20000

        ''' <summary>Groesse einer Seite der Suche. Der Server beantwortet sie in einem Zug; kleinere
        ''' Seiten kosten Anfragen, groessere lassen den Server lange rechnen, bevor irgendetwas
        ''' zurueckkommt.</summary>
        Private Const FallbackPageSize As Integer = 1000

        Private Class TimelineCacheEntry
            Public Property Key As String = ""
            Public Property Photos As List(Of NextcloudPhoto)
            Public Property Fetched As DateTime
        End Class

        Private Shared _timelineCache As TimelineCacheEntry
        Private Shared ReadOnly _timelineLock As New Object()

        ''' <summary>Wie lange die geholte Liste fuer den Abruf JE TAG weiterverwendet wird. Die
        ''' Galerie holt erst die Tage und danach jeden Tag einzeln; ohne diesen kurzen Speicher
        ''' liefe je Tag eine ganze Suche ueber den Dateibaum.</summary>
        Private Shared ReadOnly TimelineCacheLifetime As TimeSpan = TimeSpan.FromMinutes(2)

        Private Shared Function CachedTimeline(key As String) As List(Of NextcloudPhoto)
            SyncLock _timelineLock
                If _timelineCache Is Nothing Then Return Nothing
                If Not String.Equals(_timelineCache.Key, key, StringComparison.Ordinal) Then Return Nothing
                If DateTime.UtcNow - _timelineCache.Fetched > TimelineCacheLifetime Then Return Nothing
                Return _timelineCache.Photos
            End SyncLock
        End Function

        Private Shared Sub RememberTimeline(key As String, photos As List(Of NextcloudPhoto))
            SyncLock _timelineLock
                _timelineCache = New TimelineCacheEntry With {.Key = key, .Photos = photos, .Fetched = DateTime.UtcNow}
            End SyncLock
        End Sub

        ''' <summary>Vergisst die zwischengespeicherte Zeitachse. Nach jedem Schreiben faellig, sonst
        ''' zeigt die naechste Ansicht ein geloeschtes Bild noch einmal.</summary>
        Friend Shared Sub DropTimelineCache()
            SyncLock _timelineLock
                _timelineCache = Nothing
            End SyncLock
        End Sub

        ''' <summary>Der Schluessel einer Ansicht: dieselbe Ansicht, dieselbe Liste.</summary>
        Private Shared Function TimelineKey(backend As String, clusterId As String) As String
            Return ConnectionSignature() & "|" & If(backend, "") & "|" & If(clusterId, "")
        End Function

        ''' <summary>Die Aufnahmen einer Ansicht im Rueckfall: ganze Zeitachse, ein Album oder ein
        ''' Stichwort. Sortiert von addedNow nach alt, wie die Tagesliste von Memories.</summary>
        Friend Shared Async Function FallbackPhotosAsync(backend As String, clusterId As String,
                                                          cancellationToken As CancellationToken) As Task(Of List(Of NextcloudPhoto))
            Dim key = TimelineKey(backend, clusterId)
            Dim cached = CachedTimeline(key)
            If cached IsNot Nothing Then Return cached

            Dim photos As List(Of NextcloudPhoto)
            If Not String.IsNullOrEmpty(clusterId) AndAlso
               String.Equals(backend, "albums", StringComparison.OrdinalIgnoreCase) Then
                photos = Await FallbackAlbumPhotosAsync(clusterId, cancellationToken).ConfigureAwait(False)
            ElseIf Not String.IsNullOrEmpty(clusterId) AndAlso
                   String.Equals(backend, "tags", StringComparison.OrdinalIgnoreCase) Then
                photos = Await FallbackTagPhotosAsync(clusterId, cancellationToken).ConfigureAwait(False)
            ElseIf Not String.IsNullOrEmpty(clusterId) AndAlso
                   String.Equals(backend, "places", StringComparison.OrdinalIgnoreCase) Then
                photos = Await FallbackCollectionPhotosAsync(PhotosUrl("places/" & Uri.EscapeDataString(clusterId) & "/"),
                                                             cancellationToken).ConfigureAwait(False)
            ElseIf Not String.IsNullOrEmpty(clusterId) Then
                ' Personen kennt der Rueckfall nicht (siehe Klassenkopf). Eine leere Liste ist hier
                ' die richtige Antwort - der Zweig steht im Baum gar nicht erst.
                LastError = LocalizationService.T("Ohne die Memories-App kennt dieser Server keine Personen")
                Return New List(Of NextcloudPhoto)()
            Else
                photos = Await FallbackTimelineAsync(cancellationToken).ConfigureAwait(False)
            End If

            photos.Sort(Function(a, b) b.TakenEpoch.CompareTo(a.TakenEpoch))
            ' EIN LEERES ERGEBNIS WIRD NICHT GEMERKT. Es kann auch heissen, dass der Server gerade
            ' nicht geantwortet hat - und dann klebte die leere Ansicht zwei Minuten lang, auch wenn
            ' der Nutzer sofort addedNow laedt.
            If photos.Count > 0 Then RememberTimeline(key, photos)
            Return photos
        End Function

        ''' <summary>Die Tagesliste des Rueckfalls, samt Aufnahmen. Anders als Memories, das nur beim
        ''' ersten Tag ein <c>detail</c> mitschickt, haengen hier ALLE Tage voll dran: die Suche hat
        ''' sie ohnehin schon geholt, und ein zweiter Abruf je Tag waere eine zweite Suche.</summary>
        Friend Shared Async Function FallbackDaysAsync(backend As String, clusterId As String,
                                                        cancellationToken As CancellationToken) As Task(Of List(Of NextcloudDay))
            Dim photos = Await FallbackPhotosAsync(backend, clusterId, cancellationToken).ConfigureAwait(False)
            Dim days = New List(Of NextcloudDay)()
            For Each group In photos.GroupBy(Function(p) p.DayId).OrderByDescending(Function(g) g.Key)
                days.Add(New NextcloudDay With {
                    .DayId = group.Key,
                    .Count = group.Count(),
                    .Detail = group.ToList()})
            Next
            Return days
        End Function

        Friend Shared Async Function FallbackDayAsync(dayId As Long, backend As String, clusterId As String,
                                                       cancellationToken As CancellationToken) As Task(Of List(Of NextcloudPhoto))
            Dim photos = Await FallbackPhotosAsync(backend, clusterId, cancellationToken).ConfigureAwait(False)
            Return photos.Where(Function(p) p.DayId = dayId).ToList()
        End Function

        ''' <summary>Die ganze Zeitachse: eine WebDAV-Suche ueber den Dateibaum des Benutzers, nach
        ''' Aufnahmezeit absteigend.
        '''
        ''' GEBLAETTERT WIRD UEBER DIE ZEIT, nicht ueber einen Zaehler: eine zweite Seite fragt nach
        ''' allem, was AELTER ist als das Aelteste der ersten. Ein Versatz waere bei einem Bestand,
        ''' der sich waehrend des Blaetterns aendert, nicht stabil - und die Suche kennt ohnehin
        ''' keinen.
        '''
        ''' Bringt eine Seite nichts NEUES, wird abgebrochen: haben mehr Aufnahmen als eine Seite
        ''' fasst dieselbe Sekunde, drehte sich das Blaettern sonst im Kreis.</summary>
        Private Shared Async Function FallbackTimelineAsync(cancellationToken As CancellationToken) As Task(Of List(Of NextcloudPhoto))
            Dim result = New List(Of NextcloudPhoto)()
            Dim seen = New HashSet(Of Long)()
            Dim before As Long = 0
            Dim limitReached = False
            ' Der Zaehler ist nur die Reissleine gegen eine Schleife, die sich nicht selbst beendet;
            ' abgebrochen wird ueber den Deckel, und der wird BEIM AUFNEHMEN gezogen, nicht am
            ' Seitenende. Sonst kaeme bei 20500 Aufnahmen alles zurueck und die Statuszeile behauptete
            ' trotzdem, es seien die neuesten 20000.
            For round = 1 To (FallbackTimelineLimit \ FallbackPageSize) + 2
                Dim page = Await SearchTimelinePageAsync(before, FallbackPageSize, cancellationToken).ConfigureAwait(False)
                If page Is Nothing Then Exit For
                Dim addedNow = 0
                For Each photo In page
                    If Not seen.Add(photo.FileId) Then Continue For
                    If result.Count >= FallbackTimelineLimit Then
                        limitReached = True
                        Exit For
                    End If
                    result.Add(photo)
                    addedNow += 1
                Next
                If limitReached Then
                    ' Sortiert wird von addedNow nach alt, es sind also wirklich die NEUESTEN.
                    LastError = String.Format(
                        LocalizationService.T("Es werden die neuesten {0} Aufnahmen gezeigt"), FallbackTimelineLimit)
                    Exit For
                End If
                If addedNow = 0 OrElse page.Count < FallbackPageSize Then Exit For
                ' Die aelteste Aufnahme dieser Seite ist die Grenze der naechsten - und zwar
                ' EINSCHLIESSLICH ihrer Sekunde (deshalb die Eins darauf). Genau auf der Grenze zu
                ' schneiden verloere jede weitere Aufnahme aus derselben Sekunde, und eine
                ' Serienaufnahme liegt genau so. Die Wiederholungen faengt der Doppelfilter oben ab;
                ' bringt eine Seite nur noch Bekanntes, endet die Schleife von selbst.
                Dim oldestTaken = page.Min(Function(p) p.TakenEpoch)
                If oldestTaken <= 0 Then Exit For
                before = oldestTaken + 1
            Next
            Return result
        End Function

        ''' <summary>Eine Seite der Suche. <paramref name="olderThanEpoch"/> = 0 heisst "von vorn".
        ''' Nothing heisst, dass der Server nicht geantwortet hat - das ist etwas anderes als eine
        ''' leere Seite und beendet das Blaettern.</summary>
        Private Shared Async Function SearchTimelinePageAsync(olderThanEpoch As Long, limit As Integer,
                                                               cancellationToken As CancellationToken) As Task(Of List(Of NextcloudPhoto))
            Dim s = AppSettingsService.Load()
            Dim user = If(s.NextcloudUserName, "").Trim()
            Dim body = New StringBuilder()
            body.Append("<?xml version=""1.0"" encoding=""UTF-8""?>")
            body.Append("<d:searchrequest xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"" xmlns:nc=""http://nextcloud.org/ns"">")
            body.Append("<d:basicsearch><d:select><d:prop>")
            body.Append("<d:getcontenttype/><d:getcontentlength/><d:getlastmodified/><d:getetag/>")
            body.Append("<oc:fileid/><oc:permissions/><oc:favorite/>")
            body.Append("<nc:has-preview/><nc:metadata-photos-original_date_time/><nc:metadata-photos-size/>")
            body.Append("</d:prop></d:select>")
            body.Append("<d:from><d:scope><d:href>/files/").Append(XmlText(user)).Append("</d:href><d:depth>infinity</d:depth></d:scope></d:from>")
            body.Append("<d:where><d:and>")
            ' Bilder UND Videos: die Galerie zeigt beides, und Memories tut es auch.
            body.Append("<d:or>")
            body.Append("<d:like><d:prop><d:getcontenttype/></d:prop><d:literal>image/%</d:literal></d:like>")
            body.Append("<d:like><d:prop><d:getcontenttype/></d:prop><d:literal>video/%</d:literal></d:like>")
            body.Append("</d:or>")
            If olderThanEpoch > 0 Then
                body.Append("<d:lt><d:prop><nc:metadata-photos-original_date_time/></d:prop><d:literal>")
                body.Append(olderThanEpoch.ToString(CultureInfo.InvariantCulture))
                body.Append("</d:literal></d:lt>")
            End If
            body.Append("</d:and></d:where>")
            body.Append("<d:orderby><d:order><d:prop><nc:metadata-photos-original_date_time/></d:prop><d:descending/></d:order></d:orderby>")
            body.Append("<d:limit><d:nresults>").Append(limit.ToString(CultureInfo.InvariantCulture)).Append("</d:nresults></d:limit>")
            body.Append("</d:basicsearch></d:searchrequest>")

            Dim xml = Await SendDavBodyAsync("SEARCH", DavRootUrl(""), body.ToString(), "0", cancellationToken).ConfigureAwait(False)
            If xml Is Nothing Then Return Nothing
            Return ParseFileResponses(xml, user)
        End Function

        ' ── Alben ohne Memories ─────────────────────────────────────────────────
        '
        ' Sie liegen ohnehin schon in der Photos-App (siehe AlbumUrl weiter oben) - gelesen wurden
        ' sie bisher nur ueber Memories mit. Hier steht der eigene Weg dorthin.

        ''' <summary>Die Alben des Benutzers. Die Kennung wird in DERSELBEN Form gebildet, die
        ''' Memories liefert ("benutzer/name") - damit tragen Baum, Menue und Schreibwege
        ''' unveraendert.</summary>
        Friend Shared Function FallbackAlbumsAsync(cancellationToken As CancellationToken) As Task(Of List(Of NextcloudCluster))
            Dim user = If(AppSettingsService.Load().NextcloudUserName, "").Trim()
            ' Die Kennung traegt den Benutzer VOR dem Namen, weil Memories sie so liefert und alle
            ' Schreibwege (AlbumUrl) genau diese Form erwarten.
            Return FallbackCollectionsAsync("albums", "albums", Function(name) user & "/" & name, cancellationToken)
        End Function

        ''' <summary>Die Orte der Photos-App.
        '''
        ''' UNGEPRUEFT, und das steht hier statt in einer Zusicherung: auf der Messinstanz gibt es die
        ''' Sammlung (PROPFIND antwortet 207), sie ist aber LEER - ohne die Erkennungs-App entstehen
        ''' keine Orte. Der Aufbau eines Eintrags ist deshalb aus dem der Alben uebernommen. Bleibt
        ''' die Liste leer, faellt der Zweig im Baum von selbst weg; kommt etwas anderes als
        ''' erwartet, faellt es beim ersten Server mit Orten auf und nicht stillschweigend hier.</summary>
        Friend Shared Function FallbackPlacesAsync(cancellationToken As CancellationToken) As Task(Of List(Of NextcloudCluster))
            Return FallbackCollectionsAsync("places", "places", Function(name) name, cancellationToken)
        End Function

        ''' <summary>Die Sammlungen unter einem Zweig der Photos-App. Der NAME steht in der Adresse,
        ''' nicht in einer Eigenschaft: <c>d:displayname</c> beantwortet der Server hier mit 404
        ''' (gemessen), die Anzahl dagegen liefert er als <c>nc:nbItems</c>.</summary>
        Private Shared Async Function FallbackCollectionsAsync(zweig As String, clusterType As String,
                                                                makeId As Func(Of String, String),
                                                                cancellationToken As CancellationToken) As Task(Of List(Of NextcloudCluster))
            Dim result = New List(Of NextcloudCluster)()
            Dim user = If(AppSettingsService.Load().NextcloudUserName, "").Trim()
            Dim propfind = "<?xml version=""1.0""?>" &
                           "<d:propfind xmlns:d=""DAV:"" xmlns:nc=""http://nextcloud.org/ns"">" &
                           "<d:prop><nc:nbItems/></d:prop></d:propfind>"
            Dim xml = Await SendDavBodyAsync("PROPFIND", PhotosUrl(zweig & "/"), propfind, "1", cancellationToken).ConfigureAwait(False)
            If xml Is Nothing Then Return result

            For Each response In xml.Descendants(DavNs + "response")
                Dim href = HrefOf(response)
                If href.Length = 0 Then Continue For
                Dim name = LastSegment(href)
                ' Der erste Eintrag einer Mehrfachantwort ist die Sammlung SELBST.
                If name.Length = 0 OrElse String.Equals(name, zweig, StringComparison.Ordinal) Then Continue For
                Dim count = 0
                Integer.TryParse(ValueOf(OkProps(response), NcNs + "nbItems"), NumberStyles.Integer, CultureInfo.InvariantCulture, count)
                result.Add(New NextcloudCluster With {
                    .ClusterIdRaw = JsonText(makeId(name)),
                    .Name = name,
                    .Count = count,
                    .UserId = user,
                    .ClusterType = clusterType})
            Next
            Return result
        End Function

        ''' <summary>Die Aufnahmen eines Albums. Der Eintrag in der Sammlung heisst "{fileid}-{name}"
        ''' und ist ein VERWEIS auf die Datei; seine Eigenschaften sind die der Datei, der PFAD im
        ''' Dateibaum steht aber nicht dabei. Er wird deshalb aus der Zeitachse nachgeschlagen, und
        ''' wo das nicht traegt, ueber die Dateikennung gesucht - ohne ihn gaebe es weder
        ''' Begleitdatei noch Ersetzen.</summary>
        Private Shared Function FallbackAlbumPhotosAsync(albumId As String,
                                                         cancellationToken As CancellationToken) As Task(Of List(Of NextcloudPhoto))
            Return FallbackCollectionPhotosAsync(AlbumUrl(albumId), cancellationToken)
        End Function

        ''' <summary>Die Aufnahmen einer Sammlung der Photos-App (Album oder Ort).</summary>
        Private Shared Async Function FallbackCollectionPhotosAsync(url As String,
                                                                     cancellationToken As CancellationToken) As Task(Of List(Of NextcloudPhoto))
            Dim result = New List(Of NextcloudPhoto)()
            Dim propfind = "<?xml version=""1.0""?>" &
                           "<d:propfind xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"" xmlns:nc=""http://nextcloud.org/ns"">" &
                           "<d:prop><d:getcontenttype/><d:getcontentlength/><d:getlastmodified/><d:getetag/>" &
                           "<oc:fileid/><oc:permissions/><oc:favorite/>" &
                           "<nc:has-preview/><nc:metadata-photos-original_date_time/><nc:metadata-photos-size/>" &
                           "</d:prop></d:propfind>"
            Dim xml = Await SendDavBodyAsync("PROPFIND", url, propfind, "1", cancellationToken).ConfigureAwait(False)
            If xml Is Nothing Then Return result

            For Each response In xml.Descendants(DavNs + "response")
                Dim href = HrefOf(response)
                If href.Length = 0 OrElse href.EndsWith("/", StringComparison.Ordinal) Then Continue For
                If response.Descendants(DavNs + "collection").Any() Then Continue For
                ' DER PFAD FEHLT HIER, und zwar am Server: nc:realpath beantwortet er in der
                ' Sammlung mit 404 (gemessen). Der Eintrag ist ein Verweis, kein Ort. Bekannt ist er
                ' aus der Zeitachse, sonst holt ihn ResolvePathAsync bei Bedarf nach - hier gleich
                ' fuer alle zu suchen waere eine Anfrage je Bild.
                Dim photo = PhotoFromProps(OkProps(response), NameFromAlbumEntry(LastSegment(href)), KnownPathOf(FileIdOf(response)))
                If photo Is Nothing Then Continue For
                result.Add(photo)
            Next
            Return result
        End Function

        Private Shared Function FileIdOf(response As XElement) As Long
            Dim id As Long = 0
            Long.TryParse(ValueOf(OkProps(response), OcNs + "fileid"), NumberStyles.Integer, CultureInfo.InvariantCulture, id)
            Return id
        End Function

        ''' <summary>Aus "1234-Bild.jpg" wird "Bild.jpg". Der Bindestrich davor ist die Schreibweise
        ''' der Photos-App und gehoert nicht zum Namen; wer ihn stehen laesst, zeigt dem Nutzer die
        ''' Dateikennung im Kachelnamen.</summary>
        Private Shared Function NameFromAlbumEntry(entry As String) As String
            Dim text = If(entry, "")
            Dim separator = text.IndexOf("-"c)
            If separator <= 0 Then Return text
            Dim head = text.Substring(0, separator)
            If head.Length > 0 AndAlso head.All(AddressOf Char.IsDigit) Then Return text.Substring(separator + 1)
            Return text
        End Function

        ' ── Stichwoerter ohne Memories ──────────────────────────────────────────
        '
        ' Geschrieben wurden sie schon immer ueber die System-Tags des Kerns (siehe oben). Gelesen
        ' wurden sie ueber Memories; hier steht der Weg zurueck: die Liste der Tags kennt der Kern
        ' selbst, und die Dateien zu EINEM Tag holt ein REPORT in einem Zug.

        ''' <summary>Die Stichwoerter als Cluster. Die Kennung ist der NAME - genau wie bei Memories,
        ''' wo der Filter <c>?tags=Architecture</c> heisst.</summary>
        Friend Shared Async Function FallbackTagClustersAsync(cancellationToken As CancellationToken) As Task(Of List(Of NextcloudCluster))
            Dim result = New List(Of NextcloudCluster)()
            For Each tag In Await GetSystemTagListAsync(cancellationToken).ConfigureAwait(False)
                ' Nur, was der Nutzer selbst vergeben kann. Sonst stuende "Tagged by recognize
                ' v3.0.0" als Stichwort im Baum - gemessen, das legt die Erkennung selbst an.
                If Not tag.IsAssignable Then Continue For
                result.Add(New NextcloudCluster With {
                    .ClusterIdRaw = JsonText(tag.Name),
                    .Name = tag.Name,
                    .ClusterType = "tags"})
            Next
            result.Sort(Function(a, b) String.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase))
            Return result
        End Function

        ''' <summary>Die Dateien zu einem Stichwort. EIN Aufruf fuer alle - der REPORT des Kerns
        ''' filtert serverseitig; die Alternative waere, den ganzen Bestand zu holen und selbst zu
        ''' vergleichen.</summary>
        Private Shared Async Function FallbackTagPhotosAsync(tagName As String,
                                                              cancellationToken As CancellationToken) As Task(Of List(Of NextcloudPhoto))
            Dim result = New List(Of NextcloudPhoto)()
            Dim tags = Await GetSystemTagsAsync(cancellationToken).ConfigureAwait(False)
            Dim tagId As String = Nothing
            If Not tags.TryGetValue(If(tagName, "").Trim(), tagId) Then Return result

            Dim s = AppSettingsService.Load()
            Dim user = If(s.NextcloudUserName, "").Trim()
            Dim report = "<?xml version=""1.0""?>" &
                         "<oc:filter-files xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"" xmlns:nc=""http://nextcloud.org/ns"">" &
                         "<d:prop><d:getcontenttype/><d:getcontentlength/><d:getlastmodified/><d:getetag/>" &
                         "<oc:fileid/><oc:permissions/><nc:has-preview/><nc:metadata-photos-original_date_time/><nc:metadata-photos-size/></d:prop>" &
                         "<oc:filter-rules><oc:systemtag>" & XmlText(tagId) & "</oc:systemtag></oc:filter-rules>" &
                         "</oc:filter-files>"
            Dim xml = Await SendDavBodyAsync("REPORT", DavUrl(""), report, "1", cancellationToken).ConfigureAwait(False)
            If xml Is Nothing Then Return result
            Return ParseFileResponses(xml, user)
        End Function

        ' ── Einzelheiten, Vorschau und Original ohne Memories ───────────────────

        ''' <summary>Der Pfad im Dateibaum zu einer Dateikennung, soweit er schon einmal vorkam.
        '''
        ''' DAS IST DER PUNKT, AN DEM DER RUECKFALL HAENGT: unsere Elemente tragen die Kennung als
        ''' Identitaet (nextcloud://{fileid}/{name}), der Server braucht fuer Original, Begleitdatei
        ''' und Ersetzen aber den PFAD. Memories liefert ihn in den Einzelheiten mit; hier kommt er
        ''' aus der Suche, die ihn ohnehin gebracht hat, und wird gemerkt.</summary>
        Private Shared ReadOnly _pathById As New Concurrent.ConcurrentDictionary(Of Long, String)()

        ''' <summary>Wovon der Server selbst sagt, dass er dafuer keine Vorschau hat.
        '''
        ''' GEMESSEN und der Grund fuer diese Liste: der Kern rechnet Vorschauen nur fuer die
        ''' Formate vor, fuer die ein Anbieter eingeschaltet ist. Fuer ein Video ist das ab Werk
        ''' KEINER (es braucht ffmpeg und den Movie-Anbieter in der Serverkonfiguration), und
        ''' /core/preview antwortet dann mit 404 - Memories bringt dafuer einen eigenen Weg mit.
        ''' Der Server sagt es aber vorher: <c>nc:has-preview</c> steht in derselben Antwort, aus der
        ''' die Kachel entsteht. Ohne diese Liste liefe je Video bei jedem Laden eine Anfrage, die
        ''' nur eine Absage holt.</summary>
        Private Shared ReadOnly _withoutPreview As New Concurrent.ConcurrentDictionary(Of String, Boolean)(StringComparer.Ordinal)

        Private Shared _mapsSignature As String = ""
        Private Shared ReadOnly _mapsLock As New Object()

        ''' <summary>Wirft beide Merklisten weg, sobald Server oder Konto wechseln.
        '''
        ''' DAS IST KEINE HYGIENE, SONDERN EINE SICHERUNG. Eine Dateikennung ist NUR auf ihrem Server
        ''' eindeutig; zwei Nextclouds vergeben munter dieselbe 153. Bliebe der gemerkte Pfad stehen,
        ''' zeigte er nach einem Wechsel auf eine Datei des vorigen Servers - und weil an diesem Pfad
        ''' nicht nur das Anzeigen haengt, sondern auch Begleitdatei und "Original ersetzen", waere
        ''' der schlimmste Fall ein PUT auf eine fremde Datei. Deshalb wird an JEDEM Zugriff verglichen
        ''' und nicht darauf vertraut, dass irgendwer beim Umschalten aufraeumt.</summary>
        Private Shared Sub EnsureCurrentConnectionMaps()
            Dim signature = ConnectionSignature()
            SyncLock _mapsLock
                If String.Equals(_mapsSignature, signature, StringComparison.Ordinal) Then Return
                _pathById.Clear()
                _withoutPreview.Clear()
                _mapsSignature = signature
            End SyncLock
        End Sub

        Private Shared Sub RememberPath(fileId As Long, pathInTree As String)
            If fileId <= 0 OrElse String.IsNullOrWhiteSpace(pathInTree) Then Return
            EnsureCurrentConnectionMaps()
            _pathById(fileId) = pathInTree
        End Sub

        Private Shared Sub RememberWithoutPreview(fileId As Long)
            If fileId <= 0 Then Return
            EnsureCurrentConnectionMaps()
            _withoutPreview(fileId.ToString(CultureInfo.InvariantCulture)) = True
        End Sub

        ''' <summary>Weiss der Server, dass es hierzu keine Vorschau gibt?</summary>
        Friend Shared Function IsKnownWithoutPreview(fileId As String) As Boolean
            If String.IsNullOrEmpty(fileId) Then Return False
            EnsureCurrentConnectionMaps()
            Return _withoutPreview.ContainsKey(fileId)
        End Function

        Private Shared Function KnownPathOf(fileId As Long) As String
            EnsureCurrentConnectionMaps()
            Dim path As String = Nothing
            Return If(_pathById.TryGetValue(fileId, path), path, "")
        End Function

        ''' <summary>Der Pfad zu einer Kennung, notfalls beim Server erfragt. Die Suche nach der
        ''' Kennung ist der einzige Weg, der ohne Memories vom Ausweis zum Ort fuehrt.</summary>
        Private Shared Async Function ResolvePathAsync(fileId As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim id As Long = 0
            If Not Long.TryParse(fileId, NumberStyles.Integer, CultureInfo.InvariantCulture, id) OrElse id <= 0 Then Return ""
            Dim known = KnownPathOf(id)
            If known.Length > 0 Then Return known

            Dim s = AppSettingsService.Load()
            Dim user = If(s.NextcloudUserName, "").Trim()
            Dim body = "<?xml version=""1.0"" encoding=""UTF-8""?>" &
                       "<d:searchrequest xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"" xmlns:nc=""http://nextcloud.org/ns"">" &
                       "<d:basicsearch><d:select><d:prop>" &
                       "<d:getcontenttype/><d:getcontentlength/><d:getlastmodified/><d:getetag/>" &
                       "<oc:fileid/><oc:permissions/><nc:metadata-photos-original_date_time/><nc:metadata-photos-size/>" &
                       "</d:prop></d:select>" &
                       "<d:from><d:scope><d:href>/files/" & XmlText(user) & "</d:href><d:depth>infinity</d:depth></d:scope></d:from>" &
                       "<d:where><d:eq><d:prop><oc:fileid/></d:prop><d:literal>" &
                       id.ToString(CultureInfo.InvariantCulture) & "</d:literal></d:eq></d:where>" &
                       "<d:limit><d:nresults>1</d:nresults></d:limit>" &
                       "</d:basicsearch></d:searchrequest>"
            Dim xml = Await SendDavBodyAsync("SEARCH", DavRootUrl(""), body, "0", cancellationToken).ConfigureAwait(False)
            If xml Is Nothing Then Return ""
            Dim hits = ParseFileResponses(xml, user)
            If hits.Count = 0 Then Return ""
            Return hits(0).FileName
        End Function

        ''' <summary>Einzelheiten zu einer Aufnahme ohne Memories: die Eigenschaften der Datei plus
        ''' ihre Stichwoerter. Beides sind zwei Aufrufe, weil der Kern sie getrennt fuehrt.</summary>
        Friend Shared Async Function FallbackInfoAsync(fileId As String, cancellationToken As CancellationToken) As Task(Of NextcloudPhoto)
            Dim path = Await ResolvePathAsync(fileId, cancellationToken).ConfigureAwait(False)
            If path.Length = 0 Then
                If String.IsNullOrEmpty(LastError) Then LastError = LocalizationService.T("Die Datei ist auf dem Server nicht auffindbar")
                Return Nothing
            End If

            Dim propfind = "<?xml version=""1.0""?>" &
                           "<d:propfind xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"" xmlns:nc=""http://nextcloud.org/ns"">" &
                           "<d:prop><d:getcontenttype/><d:getcontentlength/><d:getlastmodified/><d:getetag/>" &
                           "<oc:fileid/><oc:permissions/><oc:favorite/>" &
                           "<nc:has-preview/><nc:metadata-photos-original_date_time/><nc:metadata-photos-size/></d:prop></d:propfind>"
            Dim xml = Await SendDavBodyAsync("PROPFIND", FileUrl(TreePath(path)), propfind, "0", cancellationToken).ConfigureAwait(False)
            If xml Is Nothing Then Return Nothing
            Dim response = xml.Descendants(DavNs + "response").FirstOrDefault()
            If response Is Nothing Then Return Nothing
            Dim photo = PhotoFromProps(OkProps(response), IO.Path.GetFileName(path), path)
            If photo Is Nothing Then Return Nothing

            Dim tags = Await FallbackTagsOfFileAsync(photo.FileId, cancellationToken).ConfigureAwait(False)
            If tags.Count > 0 Then photo.TagsRaw = JsonArray(tags)
            Return photo
        End Function

        ''' <summary>Die Stichwoerter EINER Datei. Sie haengen im Kern als eigene Sammlung an der
        ''' Datei; ohne diesen Aufruf blieben sie im Infopanel leer, obwohl der Server sie kennt.</summary>
        Private Shared Async Function FallbackTagsOfFileAsync(fileId As Long, cancellationToken As CancellationToken) As Task(Of List(Of String))
            Dim result = New List(Of String)()
            If fileId <= 0 Then Return result
            Dim propfind = "<?xml version=""1.0""?>" &
                           "<d:propfind xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"">" &
                           "<d:prop><oc:id/><oc:display-name/><oc:user-assignable/></d:prop></d:propfind>"
            Dim url = DavRootUrl("systemtags-relations/files/" & fileId.ToString(CultureInfo.InvariantCulture) & "/")
            Dim xml = Await SendDavBodyAsync("PROPFIND", url, propfind, "1", cancellationToken).ConfigureAwait(False)
            If xml Is Nothing Then Return result
            For Each response In xml.Descendants(DavNs + "response")
                Dim props = OkProps(response)
                Dim name = ValueOf(props, OcNs + "display-name")
                If String.IsNullOrWhiteSpace(name) Then Continue For
                ' Dieselbe Auslese wie im Baum: was der Nutzer nicht vergeben kann, ist kein
                ' Stichwort von ihm und gehoert nicht ins Infopanel.
                If String.Equals(ValueOf(props, OcNs + "user-assignable"), "false", StringComparison.OrdinalIgnoreCase) Then Continue For
                result.Add(name)
            Next
            Return result
        End Function

        ''' <summary>Die Adresse des Vorschaubildes ohne Memories. Der Kern rechnet Vorschauen selbst
        ''' vor; <c>a=1</c> haelt das Seitenverhaeltnis, sonst kaeme ein beschnittenes Quadrat.</summary>
        Private Shared Function CorePreviewUrl(fileId As String, size As Integer) As String
            Return NormalizeServerUrl(AppSettingsService.Load().NextcloudServerUrl) &
                   "/index.php/core/preview?fileId=" & Uri.EscapeDataString(fileId) &
                   "&x=" & size.ToString(CultureInfo.InvariantCulture) &
                   "&y=" & size.ToString(CultureInfo.InvariantCulture) & "&a=1"
        End Function

        ''' <summary>Holt das Original ohne Memories: ein gewoehnliches GET auf den Pfad im
        ''' Dateibaum. Die Temp-Kopie heisst wie im Memories-Weg "{fileid}_{name}" - daran haengt der
        ''' Rueckweg von der Datei zum Element in der Galerie (siehe FileIdFromTempPath).</summary>
        Friend Shared Async Function FallbackDownloadOriginalAsync(fileId As String, fileName As String,
                                                                    cancellationToken As CancellationToken) As Task(Of String)
            Dim path = Await ResolvePathAsync(fileId, cancellationToken).ConfigureAwait(False)
            If path.Length = 0 Then
                If String.IsNullOrEmpty(LastError) Then LastError = LocalizationService.T("Die Datei ist auf dem Server nicht auffindbar")
                Return Nothing
            End If
            Try
                Dim response = Await GetTransferClient().GetAsync(FileUrl(TreePath(path)),
                                                                  HttpCompletionOption.ResponseHeadersRead,
                                                                  cancellationToken).ConfigureAwait(False)
                If Not response.IsSuccessStatusCode Then
                    Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                    Return Nothing
                End If
                Dim folder = IO.Path.Combine(IO.Path.GetTempPath(), TempFolderName)
                Directory.CreateDirectory(folder)
                Dim safeName = If(String.IsNullOrWhiteSpace(fileName), IO.Path.GetFileName(path), fileName)
                For Each bad In IO.Path.GetInvalidFileNameChars()
                    safeName = safeName.Replace(bad, "_"c)
                Next
                Dim target = IO.Path.Combine(folder, fileId & "_" & safeName)
                Using stream = Await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(False)
                    Using file = New FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None)
                        Await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(False)
                    End Using
                End Using
                Return target
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
                Return Nothing
            End Try
        End Function

        ' ── Werkzeug fuer die WebDAV-Antworten ──────────────────────────────────

        Private Shared Function PhotosUrl(relativePath As String) As String
            Dim s = AppSettingsService.Load()
            Return NormalizeServerUrl(s.NextcloudServerUrl) & "/remote.php/dav/photos/" &
                   Uri.EscapeDataString(If(s.NextcloudUserName, "").Trim()) & "/" & If(relativePath, "").TrimStart("/"c)
        End Function

        ''' <summary>Schickt eine WebDAV-Anfrage mit Rumpf und gibt die Antwort als Baum zurueck.
        ''' Nothing heisst "hat nicht geklappt" - der Grund steht in <see cref="LastError"/>.</summary>
        Private Shared Async Function SendDavBodyAsync(method As String, url As String, requestBody As String,
                                                        depth As String, cancellationToken As CancellationToken) As Task(Of XDocument)
            LastError = ""
            If Not IsConfigured Then
                LastError = LocalizationService.T("Nextcloud ist nicht eingerichtet")
                Return Nothing
            End If
            Try
                Using request = New HttpRequestMessage(New HttpMethod(method), url)
                    If Not String.IsNullOrEmpty(depth) Then request.Headers.TryAddWithoutValidation("Depth", depth)
                    request.Content = New StringContent(requestBody, Encoding.UTF8, "application/xml")
                    Dim response = Await GetClient().SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If Not response.IsSuccessStatusCode Then
                        Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                        Return Nothing
                    End If
                    Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    If String.IsNullOrWhiteSpace(body) Then Return Nothing
                    Return XDocument.Parse(body)
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Die Eigenschaften einer Antwort, die der Server WIRKLICH geliefert hat.
        '''
        ''' Eine Mehrfachantwort traegt zwei Bloecke: einen mit 200 und den gefundenen Werten, einen
        ''' mit 404 und den Namen, die der Server nicht kennt. Wer beide zusammen liest, haelt eine
        ''' fehlende Eigenschaft fuer vorhanden und liest sie als leer - der Unterschied zwischen
        ''' "kennt der Server nicht" und "ist leer" ginge dabei verloren.</summary>
        Private Shared Function OkProps(response As XElement) As XElement
            For Each propstat In response.Elements(DavNs + "propstat")
                Dim status = If(propstat.Element(DavNs + "status")?.Value, "")
                If status.IndexOf("200", StringComparison.Ordinal) >= 0 Then Return propstat.Element(DavNs + "prop")
            Next
            Return Nothing
        End Function

        Private Shared Function ValueOf(props As XElement, name As XName) As String
            If props Is Nothing Then Return ""
            Return If(props.Element(name)?.Value, "")
        End Function

        Private Shared Function HrefOf(response As XElement) As String
            Dim href = If(response.Element(DavNs + "href")?.Value, "")
            If href.Length = 0 Then Return ""
            Try
                Return Uri.UnescapeDataString(href)
            Catch
                Return href
            End Try
        End Function

        Private Shared Function LastSegment(href As String) As String
            Dim text = If(href, "").TrimEnd("/"c)
            Dim separator = text.LastIndexOf("/"c)
            Return If(separator < 0, text, text.Substring(separator + 1))
        End Function

        ''' <summary>Macht aus den Antworten einer Suche oder eines REPORTs Aufnahmen. Der PFAD kommt
        ''' aus der Adresse der Antwort: sie lautet "/remote.php/dav/files/{benutzer}/Ordner/Bild.jpg",
        ''' und alles hinter dem Benutzer ist der Pfad im Dateibaum.</summary>
        Private Shared Function ParseFileResponses(xml As XDocument, user As String) As List(Of NextcloudPhoto)
            Dim result = New List(Of NextcloudPhoto)()
            Dim prefix = "/remote.php/dav/files/" & user
            For Each response In xml.Descendants(DavNs + "response")
                Dim href = HrefOf(response)
                If href.Length = 0 Then Continue For
                If response.Descendants(DavNs + "collection").Any() Then Continue For
                Dim path = href
                Dim at = path.IndexOf(prefix, StringComparison.OrdinalIgnoreCase)
                If at >= 0 Then path = path.Substring(at + prefix.Length)
                If path.Length = 0 OrElse path.EndsWith("/", StringComparison.Ordinal) Then Continue For
                Dim photo = PhotoFromProps(OkProps(response), IO.Path.GetFileName(path), path)
                If photo Is Nothing Then Continue For
                result.Add(photo)
            Next
            Return result
        End Function

        ''' <summary>Baut aus WebDAV-Eigenschaften eine Aufnahme in DERSELBEN Form, die Memories
        ''' liefert. Nur so bleibt alles dahinter - Kachel, Infopanel, Editor - unveraendert.
        '''
        ''' OHNE AUFNAHMEZEIT WIRD DIE AENDERUNGSZEIT GENOMMEN. Ein Bild ohne Datum faellt sonst auf
        ''' den 1.1.1970 und steht in jeder Zeitachse ganz unten; die Aenderungszeit ist naeher an
        ''' der Wahrheit als das.</summary>
        Private Shared Function PhotoFromProps(props As XElement, displayName As String, pathInTree As String) As NextcloudPhoto
            If props Is Nothing Then Return Nothing
            Dim fileId As Long = 0
            Long.TryParse(ValueOf(props, OcNs + "fileid"), NumberStyles.Integer, CultureInfo.InvariantCulture, fileId)
            If fileId <= 0 Then Return Nothing

            Dim photo = New NextcloudPhoto With {
                .FileId = fileId,
                .BaseName = If(displayName, ""),
                .FileName = If(pathInTree, ""),
                .MimeType = ValueOf(props, DavNs + "getcontenttype"),
                .Permissions = MemoriesPermissions(ValueOf(props, OcNs + "permissions")),
                .ETag = ValueOf(props, DavNs + "getetag").Trim(""""c)}

            Dim size As Long = 0
            Long.TryParse(ValueOf(props, DavNs + "getcontentlength"), NumberStyles.Integer, CultureInfo.InvariantCulture, size)
            photo.Size = size

            Dim modified As DateTimeOffset
            If DateTimeOffset.TryParse(ValueOf(props, DavNs + "getlastmodified"), CultureInfo.InvariantCulture,
                                       DateTimeStyles.AllowWhiteSpaces, modified) Then
                photo.Mtime = modified.ToUnixTimeSeconds()
            End If

            Dim taken As Long = 0
            Long.TryParse(ValueOf(props, NcNs + "metadata-photos-original_date_time"),
                          NumberStyles.Integer, CultureInfo.InvariantCulture, taken)
            photo.DateTaken = If(taken > 0, taken, photo.Mtime)
            ' Die Tageskennung ist bei Memories die Zahl der Tage seit 1970 in UTC. Genauso hier,
            ' sonst passte die Gruppierung nicht zu dem, was die Galerie erwartet.
            photo.DayId = If(photo.TakenEpoch > 0, photo.TakenEpoch \ 86400L, 0L)

            Dim width = 0, height = 0
            If ParseImageSize(If(props Is Nothing, Nothing, props.Element(NcNs + "metadata-photos-size")), width, height) Then
                photo.Width = width
                photo.Height = height
            End If

            RememberPath(fileId, photo.FileName)
            ' Nur das ausdrueckliche "false" zaehlt: fehlt die Eigenschaft (etwa in einer aelteren
            ' Fassung), wird gefragt statt angenommen.
            If String.Equals(ValueOf(props, NcNs + "has-preview"), "false", StringComparison.OrdinalIgnoreCase) Then
                RememberWithoutPreview(fileId)
            End If
            Return photo
        End Function

        ''' <summary>Breite und Hoehe aus der Masseigenschaft.
        '''
        ''' GEMESSEN (2026-08-14, Nextcloud 34.0.2): sie kommt als VERSCHACHTELTES XML, nicht als
        ''' Text - <c>&lt;nc:metadata-photos-size&gt;&lt;width&gt;4096&lt;/width&gt;
        ''' &lt;height&gt;3072&lt;/height&gt;&lt;/nc:metadata-photos-size&gt;</c>, und die beiden
        ''' Kinder tragen KEINEN Namensraum. Wer den Wert als Text liest, bekommt "40963072" und
        ''' damit gar nichts. Die zweite Form ("4096x3072") und JSON werden mitgenommen, weil eine
        ''' aeltere Fassung sie so geschickt haben kann und der Unterschied nicht auffiele: das Bild
        ''' saehe richtig aus, nur die Masse im Infopanel blieben 0.</summary>
        Private Shared Function ParseImageSize(element As XElement, ByRef width As Integer, ByRef height As Integer) As Boolean
            width = 0 : height = 0
            If element Is Nothing Then Return False

            Dim w = element.Element("width")
            Dim h = element.Element("height")
            If w IsNot Nothing AndAlso h IsNot Nothing Then
                Integer.TryParse(w.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, width)
                Integer.TryParse(h.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, height)
                If width > 0 AndAlso height > 0 Then Return True
                width = 0 : height = 0
            End If

            Dim value = element.Value.Trim()
            If value.Length = 0 Then Return False
            If value.StartsWith("{", StringComparison.Ordinal) Then
                Try
                    Using doc = JsonDocument.Parse(value)
                        Dim jw As JsonElement, jh As JsonElement
                        If doc.RootElement.TryGetProperty("width", jw) AndAlso doc.RootElement.TryGetProperty("height", jh) Then
                            width = If(jw.ValueKind = JsonValueKind.Number, jw.GetInt32(), 0)
                            height = If(jh.ValueKind = JsonValueKind.Number, jh.GetInt32(), 0)
                            Return width > 0 AndAlso height > 0
                        End If
                    End Using
                Catch
                End Try
                Return False
            End If
            Dim parts = value.Split("x"c, "X"c)
            If parts.Length <> 2 Then Return False
            Return Integer.TryParse(parts(0).Trim(), width) AndAlso Integer.TryParse(parts(1).Trim(), height) AndAlso
                   width > 0 AndAlso height > 0
        End Function

        ''' <summary>Die Rechte in DER Schreibweise, die <see cref="NextcloudPhoto.CanReplaceOriginal"/>
        ''' liest.
        '''
        ''' DIE BEIDEN QUELLEN BENUTZEN VERSCHIEDENE BUCHSTABEN FUER DASSELBE, und das haette still
        ''' Schaden angerichtet: Memories meldet Aendern als "U" (gemessen: "RUDS"), WebDAV als "W"
        ''' (gemessen: "RGDNVW" fuer eine eigene Datei, "GDNVW" fuer eine im Album). Ungeuebersetzt
        ''' faende <c>Contains("U")</c> nie ein U - der Speichern-Knopf bliebe ohne Memories bei
        ''' JEDER Datei zu, obwohl das Schreibrecht da ist, und niemand saehe einen Grund dafuer.
        ''' Uebersetzt wird nur, was wir auch lesen: Aendern und Loeschen.</summary>
        Private Shared Function MemoriesPermissions(davPermissions As String) As String
            Dim value = If(davPermissions, "")
            If value.Length = 0 Then Return ""
            Dim result = New StringBuilder("R")
            If value.IndexOf("W"c) >= 0 Then result.Append("U"c)
            If value.IndexOf("D"c) >= 0 Then result.Append("D"c)
            ' Das R der WebDAV-Schreibweise heisst "darf geteilt werden", nicht "darf gelesen werden".
            If value.IndexOf("R"c) >= 0 Then result.Append("S"c)
            Return result.ToString()
        End Function

        ''' <summary>Text als JsonElement. Die Datentypen tragen ihre Kennungen als JsonElement,
        ''' damit eine Zahl aus der Antwort nicht die ganze Deserialisierung wirft (siehe
        ''' NextcloudCluster); der Rueckfall baut sie selbst und braucht denselben Typ. Clone ist
        ''' Pflicht - ohne ihn stirbt der Wert mit dem JsonDocument.</summary>
        Private Shared Function JsonText(value As String) As JsonElement
            Using doc = JsonDocument.Parse(JsonSerializer.Serialize(If(value, "")))
                Return doc.RootElement.Clone()
            End Using
        End Function

        Private Shared Function JsonArray(values As List(Of String)) As JsonElement
            Using doc = JsonDocument.Parse(JsonSerializer.Serialize(values))
                Return doc.RootElement.Clone()
            End Using
        End Function

        ''' <summary>Text fuer einen XML-Rumpf. Ein Album- oder Stichwortname mit einem kaufmaennischen
        ''' Und zerlegte sonst die Anfrage, und der Server antwortete mit einem Fehler, der wie ein
        ''' Serverproblem aussieht.</summary>
        Private Shared Function XmlText(value As String) As String
            Return New XText(If(value, "")).ToString()
        End Function

    End Class

End Namespace
