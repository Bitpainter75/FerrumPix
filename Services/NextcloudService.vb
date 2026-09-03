Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports System.Xml.Linq

Namespace Services

    ''' <summary>Anbindung an eine Nextcloud mit der Memories-App.
    '''
    ''' DER UNTERSCHIED ZU IMMICH, und er ist der Grund fuer diese zweite Quelle: Memories ist eine
    ''' ANSICHT auf den Dateibaum des Benutzers, keine eigene Ablage. Jedes Element traegt eine
    ''' Dateikennung UND einen Pfad, und der Pfad ist ueber WebDAV erreichbar. Damit ist auf diesem
    ''' Server moeglich, was Immich nicht kann: das Original an Ort und Stelle ersetzen und eine
    ''' .fpxmp danebenlegen. Eine RAW auf dem Server waere also zerstoerungsfrei bearbeitbar.
    ''' Wer hier etwas nach Immich-Vorbild baut ("nur Speichern unter", jedes Speichern legt ein
    ''' neues Asset an), verschenkt genau das.
    '''
    ''' STAND: Die Endpunkte stammen aus dem Routenverzeichnis der App (appinfo/routes.php), nicht
    ''' aus einer Messung an einem laufenden Server. Was hier NICHT belegt ist, steht als Frage in
    ''' <see cref="TestConnectionAsync"/> - der Verbindungstest ist bewusst mehr als ein Ja/Nein und
    ''' beantwortet sie beim ersten Lauf gegen eine echte Instanz. Erst danach werden aus den
    ''' Datentypen unten belastbare Typen; bis dahin sind sie nachsichtig gebaut
    ''' (siehe <see cref="FlexibleNumberConverter"/>).
    '''
    ''' OHNE MEMORIES traegt der Kern allein: Zeitachse, Alben, Stichwoerter und Vorschau kommen
    ''' dann aus der Photos-App und den WebDAV-Wegen des Kerns. Der Rueckfall steht in
    ''' NextcloudPhotosFallback.vb, dieselbe Klasse, und die oeffentlichen Funktionen hier waehlen
    ''' den Weg selbst aus - kein Aufrufer muss wissen, welche Apps auf dem Server liegen.</summary>
    Partial Public Class NextcloudService

        ''' <summary>Pfad-Schema der Elemente dieser Quelle, Gegenstueck zu immich://.
        ''' Die Dateikennung ist die Identitaet, der Name traegt nur die Endung fuer die
        ''' Formaterkennung.</summary>
        Public Const PseudoScheme As String = "nextcloud://"

        Private Shared _client As HttpClient
        Private Shared _clientSignature As String = ""
        Private Shared ReadOnly _clientLock As New Object()

        ''' <summary>Warum der letzte Abruf leer blieb; leer heisst "wirklich nichts da". Dieselbe
        ''' Regel wie bei Immich: ein Fehlschlag darf nicht stumm als "0 Bilder" erscheinen.</summary>
        Public Shared Property LastError As String = ""

        Public Shared ReadOnly Property IsConfigured As Boolean
            Get
                Dim s = AppSettingsService.Load()
                Return s.NextcloudEnabled AndAlso
                       Not String.IsNullOrWhiteSpace(s.NextcloudServerUrl) AndAlso
                       Not String.IsNullOrWhiteSpace(s.NextcloudUserName) AndAlso
                       Not String.IsNullOrWhiteSpace(s.NextcloudAppPassword)
            End Get
        End Property

        ''' <summary>Normalisiert die eingegebene Adresse auf die reine Basis. Abgeschnitten werden
        ''' der Schraegstrich am Ende und ein mitkopiertes "/index.php" oder "/apps/memories" - beim
        ''' Kopieren aus dem Browser haengt genau das dran, und der Rest wird hier selbst
        ''' angebaut.</summary>
        Public Shared Function NormalizeServerUrl(url As String) As String
            If String.IsNullOrWhiteSpace(url) Then Return ""
            Dim trimmed = url.Trim().TrimEnd("/"c)
            For Each suffix In {"/apps/memories", "/index.php"}
                If trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) Then
                    trimmed = trimmed.Substring(0, trimmed.Length - suffix.Length).TrimEnd("/"c)
                End If
            Next
            Return trimmed
        End Function

        ''' <summary>Basis der Memories-Endpunkte. Ueber index.php, weil das unabhaengig davon
        ''' funktioniert, ob der Server auf huebsche Adressen umgeschrieben ist.</summary>
        Private Shared Function ApiUrl(pathAndQuery As String) As String
            Dim baseUrl = NormalizeServerUrl(AppSettingsService.Load().NextcloudServerUrl)
            Return baseUrl & "/index.php/apps/memories/api/" & pathAndQuery.TrimStart("/"c)
        End Function

        ''' <summary>WebDAV-Wurzel des angemeldeten Benutzers. Ueber sie laeuft alles, was Memories
        ''' nicht kann: Original ersetzen, Begleitdatei ablegen, Favorit setzen.</summary>
        Private Shared Function DavUrl(relativePath As String) As String
            Dim s = AppSettingsService.Load()
            Dim baseUrl = NormalizeServerUrl(s.NextcloudServerUrl)
            Return baseUrl & "/remote.php/dav/files/" & Uri.EscapeDataString(If(s.NextcloudUserName, "").Trim()) &
                   "/" & If(relativePath, "").TrimStart("/"c)
        End Function

        Private Shared Function BuildClient(serverUrl As String, userName As String, appPassword As String, timeoutSeconds As Integer) As HttpClient
            Dim client = New HttpClient With {.Timeout = TimeSpan.FromSeconds(timeoutSeconds)}
            Dim raw = If(userName, "").Trim() & ":" & If(appPassword, "").Trim()
            client.DefaultRequestHeaders.Authorization =
                New AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)))
            ' Diesen Kopfeintrag schicken die Nextcloud-Clients mit. Er kostet nichts und ist auf
            ' manchen Aufbauten der Unterschied zwischen 200 und einer CSRF-Abweisung.
            client.DefaultRequestHeaders.Add("OCS-APIRequest", "true")
            client.DefaultRequestHeaders.Accept.Add(New MediaTypeWithQualityHeaderValue("application/json"))
            Return client
        End Function

        Private Shared Function GetClient() As HttpClient
            Dim s = AppSettingsService.Load()
            Dim signature = NormalizeServerUrl(s.NextcloudServerUrl) & "|" & If(s.NextcloudUserName, "") & "|" & If(s.NextcloudAppPassword, "")
            SyncLock _clientLock
                If _client IsNot Nothing AndAlso String.Equals(_clientSignature, signature, StringComparison.Ordinal) Then Return _client
                _client?.Dispose()
                _client = BuildClient(s.NextcloudServerUrl, s.NextcloudUserName, s.NextcloudAppPassword, 30)
                _clientSignature = signature
                Return _client
            End SyncLock
        End Function

        Private Shared _transferClient As HttpClient
        Private Shared _transferSignature As String = ""

        ''' <summary>EIGENER Client fuer Dateiuebertragungen, mit halbstuendiger Frist statt der
        ''' halben Minute des Abfrage-Clients.
        '''
        ''' Die 30 Sekunden sind fuer eine Antwort richtig und fuer eine Datei falsch: ein RAW von 60
        ''' MB oder ein Video ueber eine gewoehnliche Leitung braucht laenger, und der Abbruch kam
        ''' mitten im Hochladen - ohne dass jemand sagen konnte, ob die Datei nun ganz oben liegt
        ''' oder halb. Wer wirklich haengt, wird auch nach 30 Minuten abgebrochen.</summary>
        Private Shared Function GetTransferClient() As HttpClient
            Dim s = AppSettingsService.Load()
            Dim signature = NormalizeServerUrl(s.NextcloudServerUrl) & "|" & If(s.NextcloudUserName, "") & "|" & If(s.NextcloudAppPassword, "")
            SyncLock _clientLock
                If _transferClient IsNot Nothing AndAlso String.Equals(_transferSignature, signature, StringComparison.Ordinal) Then Return _transferClient
                _transferClient?.Dispose()
                _transferClient = BuildClient(s.NextcloudServerUrl, s.NextcloudUserName, s.NextcloudAppPassword, 30 * 60)
                _transferSignature = signature
                Return _transferClient
            End SyncLock
        End Function

        ' ── Pseudo-Pfade ────────────────────────────────────────────────────────

        ''' <summary>Ordner der geholten Originale. Eigener Ordner je Quelle, damit ein Aufraeumen
        ''' nie die Kopien der anderen erwischt.</summary>
        Private Shared ReadOnly TempFolderName As String = "ferrumpix-nextcloud"

        ''' <summary>Vorsatz der Kopien aus dem Papierkorb. Sie tragen im Namen KEINE Dateikennung -
        ''' siehe FileIdFromTempPath, das ihn deshalb ausnimmt.</summary>
        Private Const TrashCopyPrefix As String = "trash"

        ''' <summary>Liegt dieser Pfad in unserem Temp-Ordner, ist es also die Kopie eines Originals
        ''' und NICHT die Datei des Nutzers? Der Editor haengt daran seinen Speichern-unter-Zwang:
        ''' in eine Temp-Kopie zu speichern hiesse, die Bearbeitung beim naechsten Aufraeumen zu
        ''' verlieren.</summary>
        Public Shared Function IsNextcloudTempPath(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) Then Return False
            Dim ordner = IO.Path.GetDirectoryName(path)
            If String.IsNullOrEmpty(ordner) Then Return False
            Return String.Equals(IO.Path.GetFileName(ordner.TrimEnd(IO.Path.DirectorySeparatorChar)),
                                 TempFolderName, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Die Dateikennung aus dem Namen einer geholten Temp-Kopie. DownloadOriginalToTempAsync
        ''' setzt sie als "{fileid}_{name}" davor - sie ist damit der Rueckweg vom lokalen Pfad zum
        ''' Element in der Galerie, so wie bei Immich der Dateiname-Stamm die Asset-Kennung ist.
        '''
        ''' Leer, wenn der Pfad keine solche Kopie ist. Auch die Kopie aus dem Papierkorb faellt
        ''' heraus: sie heisst "trash_{name}" (DownloadTrashOriginalToTempAsync), und "trash" ist
        ''' keine Kennung. Ohne diese Ausnahme kaeme der Name als Kennung zurueck - er traefe zwar
        ''' kein Element, weil eine Kennung aus Ziffern besteht, aber die Funktion behauptete etwas,
        ''' das sie nicht weiss. Fuer den Papierkorb fuehrt der am Element gemerkte Pfad zurueck.</summary>
        Public Shared Function FileIdFromTempPath(path As String) As String
            If Not IsNextcloudTempPath(path) Then Return ""
            Dim name = IO.Path.GetFileName(path)
            Dim separator = name.IndexOf("_"c)
            If separator <= 0 Then Return ""
            Dim fileId = name.Substring(0, separator)
            If String.Equals(fileId, TrashCopyPrefix, StringComparison.OrdinalIgnoreCase) Then Return ""
            Return fileId
        End Function

        ''' <summary>Der NAME der Datei auf dem Server, gelesen aus dem Namen einer geholten
        ''' Temp-Kopie ("{fileid}_{name}", im Papierkorb "trash_{name}"). Gegenstueck zu
        ''' <see cref="FileIdFromTempPath"/>: dort steht der Teil VOR dem Trenner, hier der dahinter.
        '''
        ''' Gedacht fuer alles, was den Namen ANZEIGT - der Editor fuehrte eine solche Kopie sonst
        ''' unter "12345_Bild.jpg" in Fusszeile, Infopanel und im Vorschlag beim Speichern.
        ''' Der Papierkorb ist hier ausdruecklich eingeschlossen: sein Praefix ist keine Kennung,
        ''' der Name dahinter aber derselbe.
        '''
        ''' Leer, wenn der Pfad keine solche Kopie ist.</summary>
        Public Shared Function FileNameFromTempPath(path As String) As String
            If Not IsNextcloudTempPath(path) Then Return ""
            Dim name = IO.Path.GetFileName(path)
            Dim separator = name.IndexOf("_"c)
            If separator <= 0 OrElse separator >= name.Length - 1 Then Return ""
            Return name.Substring(separator + 1)
        End Function

        Public Shared Function IsNextcloudPseudoPath(path As String) As Boolean
            Return Not String.IsNullOrEmpty(path) AndAlso path.StartsWith(PseudoScheme, StringComparison.OrdinalIgnoreCase)
        End Function

        Public Shared Function MakePseudoPath(fileId As String, fileName As String) As String
            Return PseudoScheme & If(fileId, "") & "/" & If(fileName, "")
        End Function

        Public Shared Function TryParsePseudoPath(path As String, ByRef fileId As String, ByRef fileName As String) As Boolean
            fileId = Nothing : fileName = Nothing
            If Not IsNextcloudPseudoPath(path) Then Return False
            Dim rest = path.Substring(PseudoScheme.Length)
            Dim slash = rest.IndexOf("/"c)
            If slash <= 0 Then Return False
            fileId = rest.Substring(0, slash)
            fileName = rest.Substring(slash + 1)
            Return fileId.Length > 0
        End Function

        ' ── Verbindungstest ─────────────────────────────────────────────────────

        Public Class NextcloudConnectionResult
            Public Property Ok As Boolean
            Public Property Message As String = ""
            ''' <summary>Der ausfuehrliche Befund, Zeile fuer Zeile. Gedacht zum Weitergeben: er
            ''' beantwortet dieselben Fragen wie Diagnostics/nextcloud-probe.sh, nur aus der
            ''' Anwendung heraus und mit denselben Zugangsdaten.</summary>
            Public Property Report As New List(Of String)
        End Class

        ''' <summary>Prueft die Zugangsdaten und stellt dabei die vier Fragen, die vor dem weiteren
        ''' Ausbau offen sind. Wirft nie.
        '''
        ''' 1. Antwortet die Memories-App unter dieser Adresse? (/api/describe ist oeffentlich)
        ''' 2. Traegt die Anmeldung mit App-Passwort die uebrigen Routen, oder schlaegt die
        '''    CSRF-Pruefung zu? Von den Routen ist nur ein Teil davon ausgenommen - die Vorschau
        '''    ja, /api/days nicht. Daran haengt, ob ein Client ueberhaupt ohne Token-Tanz
        '''    auskommt, deshalb wird genau das hier gemessen und nicht geraten.
        ''' 3. Kommt die Zeitachse, und wie viele Aufnahmen liegen dahinter?
        ''' 4. Traegt WebDAV? Ohne das kein Ersetzen und keine Begleitdatei.</summary>
        Public Shared Async Function TestConnectionAsync(serverUrl As String, userName As String, appPassword As String,
                                                         Optional cancellationToken As CancellationToken = Nothing) As Task(Of NextcloudConnectionResult)
            Dim result = New NextcloudConnectionResult()
            Dim baseUrl = NormalizeServerUrl(serverUrl)
            If String.IsNullOrWhiteSpace(baseUrl) Then
                result.Message = LocalizationService.T("Keine Server-URL angegeben")
                Return result
            End If
            If String.IsNullOrWhiteSpace(userName) Then
                result.Message = LocalizationService.T("Kein Benutzername angegeben")
                Return result
            End If
            If String.IsNullOrWhiteSpace(appPassword) Then
                result.Message = LocalizationService.T("Kein App-Passwort angegeben")
                Return result
            End If

            Try
                Using client = BuildClient(baseUrl, userName, appPassword, 20)
                    Dim memoriesBase = baseUrl & "/index.php/apps/memories/api/"

                    ' 1. Ist die App da?
                    Dim describe = Await client.GetAsync(memoriesBase & "describe", cancellationToken).ConfigureAwait(False)
                    If Not describe.IsSuccessStatusCode Then
                        ' KEIN FEHLER, SONDERN DER ANDERE WEG. Ohne Memories tragen Photos-App und
                        ' Kern die Anbindung ebenfalls; geprueft wird das hier und nicht behauptet.
                        Return Await TestFallbackConnectionAsync(client, baseUrl, userName, result, cancellationToken).ConfigureAwait(False)
                    End If
                    Dim version = ""
                    Try
                        Using doc = JsonDocument.Parse(Await describe.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False))
                            Dim v As JsonElement
                            If doc.RootElement.TryGetProperty("version", v) Then version = v.ToString()
                        End Using
                    Catch
                        ' Der Befund haengt nicht daran - die App hat geantwortet, das genuegt.
                    End Try
                    result.Report.Add(LocalizationService.T("Memories erreichbar") & If(version = "", "", " " & version))

                    ' 2. und 3. Die eigentliche Frage.
                    Dim days = Await client.GetAsync(memoriesBase & "days", cancellationToken).ConfigureAwait(False)
                    If days.StatusCode = HttpStatusCode.Unauthorized Then
                        result.Message = LocalizationService.T("Server erreichbar, Anmeldung abgelehnt")
                        Return result
                    End If
                    If days.StatusCode = HttpStatusCode.PreconditionFailed Then
                        ' 412 ist die CSRF-Sperre. Sie ist kein Tippfehler des Benutzers, sondern der
                        ' Befund, auf den der weitere Ausbau wartet - deshalb wird sie benannt.
                        result.Message = LocalizationService.T("Angemeldet, aber der Server verlangt einen Sitzungsnachweis")
                        result.Report.Add(LocalizationService.T("Angemeldet, aber der Server verlangt einen Sitzungsnachweis"))
                        Return result
                    End If
                    If Not days.IsSuccessStatusCode Then
                        result.Message = String.Format(LocalizationService.T("Zeitachse antwortet mit {0}"), CInt(days.StatusCode))
                        Return result
                    End If

                    Dim total = 0, dayCount = 0
                    Try
                        Using doc = JsonDocument.Parse(Await days.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False))
                            If doc.RootElement.ValueKind = JsonValueKind.Array Then
                                For Each entry In doc.RootElement.EnumerateArray()
                                    dayCount += 1
                                    Dim c As JsonElement
                                    If entry.TryGetProperty("count", c) AndAlso c.ValueKind = JsonValueKind.Number Then
                                        total += CInt(Math.Round(c.GetDouble()))
                                    End If
                                Next
                            End If
                        End Using
                    Catch ex As Exception
                        ' Ein Aufbau, den wir nicht erwartet haben, ist selbst ein Befund.
                        result.Report.Add(LocalizationService.T("Zeitachse ist anders aufgebaut als erwartet") & ": " & ex.Message)
                    End Try
                    result.Report.Add(String.Format(LocalizationService.T("{0} Tage, {1} Aufnahmen"), dayCount, total))

                    ' 4. WAS DIE ZUSATZ-APPS SAGEN. Personen, Orte und Stichwoerter kommen aus
                    ' eigenen Apps des Servers, und wenn eine fehlt, bleibt ihr Zweig im Baum
                    ' einfach WEG - ein Knoten, unter dem nie etwas auftaucht, sieht aus wie ein
                    ' Fehler. Genau das laesst den Nutzer aber ratlos zurueck: "warum sehe ich keine
                    ' Personen?" Der Server beantwortet die Frage selbst (412 mit Begruendung),
                    ' und hier ist die Stelle, an der sie ankommt.
                    For Each backend In {("recognize", LocalizationService.T("Personen")),
                                         ("places", LocalizationService.T("Orte")),
                                         ("tags", LocalizationService.T("Stichwörter"))}
                        Dim antwort = Await client.GetAsync(memoriesBase & "clusters/" & backend.Item1, cancellationToken).ConfigureAwait(False)
                        Dim rumpf = Await antwort.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                        Dim grund = ServerMessage(rumpf)
                        If grund.Length > 0 Then
                            result.Report.Add(backend.Item2 & ": " & grund)
                        ElseIf Not antwort.IsSuccessStatusCode Then
                            result.Report.Add(backend.Item2 & ": " & String.Format(LocalizationService.T("Nextcloud antwortet mit {0}"), CInt(antwort.StatusCode)))
                        Else
                            Dim anzahl = 0
                            Try
                                Using doc = JsonDocument.Parse(rumpf)
                                    If doc.RootElement.ValueKind = JsonValueKind.Array Then anzahl = doc.RootElement.GetArrayLength()
                                End Using
                            Catch
                            End Try
                            ' Null ist die haeufigste Antwort und die missverstaendlichste: die App
                            ' laeuft, sie hat nur noch nichts gefunden.
                            result.Report.Add(backend.Item2 & ": " & If(anzahl = 0,
                                                                        LocalizationService.T("eingeschaltet, noch nichts erkannt"),
                                                                        anzahl.ToString(Globalization.CultureInfo.InvariantCulture)))
                        End If
                    Next

                    ' 5. Traegt WebDAV?
                    Dim davUrl = baseUrl & "/remote.php/dav/files/" & Uri.EscapeDataString(userName.Trim()) & "/"
                    Using request = New HttpRequestMessage(New HttpMethod("PROPFIND"), davUrl)
                        request.Headers.Add("Depth", "0")
                        Dim dav = Await client.SendAsync(request, cancellationToken).ConfigureAwait(False)
                        If CInt(dav.StatusCode) = 207 Then
                            result.Report.Add(LocalizationService.T("WebDAV trägt: Originale ersetzbar, Begleitdatei ablegbar"))
                        Else
                            result.Report.Add(String.Format(LocalizationService.T("WebDAV antwortet mit {0}"), CInt(dav.StatusCode)))
                        End If
                    End Using

                    result.Ok = True
                    result.Message = String.Join(" · ", result.Report)
                    Return result
                End Using
            Catch ex As Exception
                result.Message = ex.Message
                Return result
            End Try
        End Function

        ' ── Lesen ───────────────────────────────────────────────────────────────

        ''' <summary>Ein Tag der Zeitachse. GEMESSEN: die Liste bringt die Aufnahmen des jeweils
        ''' ersten Tages gleich mit (<c>detail</c>), bei den uebrigen fehlt das Feld. Ein zweiter
        ''' Abruf je Tag ist dafuer also nicht noetig, solange schon etwas da ist.</summary>
        Public Class NextcloudDay
            <JsonPropertyName("dayid")> Public Property DayId As Long
            <JsonPropertyName("count")> Public Property Count As Integer
            <JsonPropertyName("detail")> Public Property Detail As List(Of NextcloudPhoto)
        End Class

        ''' <summary>Eine Aufnahme. Die Feldnamen sind am Server GEMESSEN (Nextcloud 34.0.2,
        ''' Memories 8.1.0), nicht aus der Dokumentation genommen.
        '''
        ''' ZWEI FALLEN, die eine Vorlage nach Gefuehl nicht getroffen haette:
        ''' - Die Aufnahmezeit heisst je nach Endpunkt ANDERS. In der Tagesliste steht sie als
        '''   <c>epoch</c>, in den Einzelheiten als <c>datetaken</c>. Beide werden gelesen,
        '''   <see cref="TakenEpoch"/> waehlt aus - sonst haette die Galerie je nach Herkunft des
        '''   Elements mal ein Datum und mal 1970.
        ''' - <c>filename</c> gibt es NUR in den Einzelheiten, und es ist der Pfad im Dateibaum
        '''   (z.B. /Photos/Bild.jpg), nicht der blosse Name. Genau dieser Pfad wird gebraucht, um
        '''   eine Begleitdatei danebenzulegen oder das Original zu ersetzen.
        '''
        ''' Nicht gemessen und deshalb NICHT hier: ein Favoritenfeld. Die Tagesliste kennt keins;
        ''' der Favorit haengt an einer WebDAV-Eigenschaft.</summary>
        Public Class NextcloudPhoto
            <JsonPropertyName("fileid")> Public Property FileId As Long
            <JsonPropertyName("dayid")> Public Property DayId As Long
            <JsonPropertyName("etag")> Public Property ETag As String = ""
            <JsonPropertyName("auid")> Public Property Auid As String = ""
            <JsonPropertyName("basename")> Public Property BaseName As String = ""
            <JsonPropertyName("mimetype")> Public Property MimeType As String = ""
            <JsonPropertyName("w")> Public Property Width As Integer
            <JsonPropertyName("h")> Public Property Height As Integer
            <JsonPropertyName("epoch")> Public Property Epoch As Long
            <JsonPropertyName("datetaken")> Public Property DateTaken As Long
            ''' <summary>Nur aus den Einzelheiten: der PFAD im Dateibaum des Benutzers.</summary>
            <JsonPropertyName("filename")> Public Property FileName As String = ""
            <JsonPropertyName("size")> Public Property Size As Long
            <JsonPropertyName("mtime")> Public Property Mtime As Long
            ''' <summary>Rechte als Buchstaben, aus den Einzelheiten. C=Anlegen, R=Lesen,
            ''' U=Aendern, D=Loeschen, S=Teilen.</summary>
            <JsonPropertyName("permissions")> Public Property Permissions As String = ""
            ' Als JsonElement: gemessen kam "tags":[] - LEER, die Form ist damit unbelegt. Memories
            ' liefert je nach Fassung eine Liste ODER eine Zuordnung Kennung zu Name; auf eine Liste
            ' zu deserialisieren wuerde im zweiten Fall die ganze Antwort werfen.
            <JsonPropertyName("tags")> Public Property TagsRaw As JsonElement

            ''' <summary>Die Stichwoerter, gleich in welcher Form sie kamen.
            '''
            ''' JsonIgnore ist hier PFLICHT und nicht Kosmetik: der Serializer nimmt auch berechnete
            ''' Eigenschaften in sein Modell auf, und "Tags" stiess dort mit dem "tags" von
            ''' <see cref="TagsRaw"/> zusammen. Das wirft beim ERSTEN Deserialisieren des Typs, also
            ''' zur Laufzeit und nicht beim Bauen. Dasselbe gilt fuer jede weitere abgeleitete
            ''' Eigenschaft in diesen Datentypen.</summary>
            ''' <summary>Die Stichwoerter aus dem Index, wenn dieser Eintrag von dort kommt.
            '''
            ''' Sie MUESSEN neben TagsRaw stehen und duerfen es nicht ersetzen: ein JsonElement gilt
            ''' nur, solange sein JsonDocument lebt, und ein aus der Datenbank zusammengebautes waere
            ''' nach dem Lesen ungueltig. Nothing heisst "kein Indexeintrag", eine leere Liste heisst
            ''' "der Server hat keine Stichwoerter gemeldet" - das ist nicht dasselbe.</summary>
            <JsonIgnore>
            Private Property CachedTags As List(Of String)

            ''' <summary>Setzt die Stichwoerter aus dem Index. Nur fuer den Index gedacht.</summary>
            Public Sub SetCachedTags(tags As List(Of String))
                CachedTags = tags
            End Sub

            <JsonIgnore>
            Public ReadOnly Property Tags As List(Of String)
                Get
                    If CachedTags IsNot Nothing Then Return CachedTags
                    Dim result = New List(Of String)()
                    Select Case TagsRaw.ValueKind
                        Case JsonValueKind.Array
                            For Each entry In TagsRaw.EnumerateArray()
                                Dim text = If(entry.ValueKind = JsonValueKind.String, entry.GetString(), entry.ToString())
                                If Not String.IsNullOrWhiteSpace(text) Then result.Add(text)
                            Next
                        Case JsonValueKind.Object
                            For Each prop In TagsRaw.EnumerateObject()
                                Dim text = If(prop.Value.ValueKind = JsonValueKind.String, prop.Value.GetString(), prop.Value.ToString())
                                If Not String.IsNullOrWhiteSpace(text) Then result.Add(text)
                            Next
                    End Select
                    Return result
                End Get
            End Property

            ''' <summary>Die Aufnahmezeit, gleich welcher Endpunkt sie geliefert hat.</summary>
            <JsonIgnore>
            Public ReadOnly Property TakenEpoch As Long
                Get
                    Return If(DateTaken <> 0, DateTaken, Epoch)
                End Get
            End Property

            ''' <summary>Ob das Original an Ort und Stelle ersetzt werden darf. AM ELEMENT gelesen
            ''' und nicht angenommen: in einem geteilten Ordner fehlt das U, und ein Speichern
            ''' liefe dort ins Leere.</summary>
            <JsonIgnore>
            Public ReadOnly Property CanReplaceOriginal As Boolean
                Get
                    Return Not String.IsNullOrEmpty(Permissions) AndAlso Permissions.Contains("U"c)
                End Get
            End Property

            ''' <summary>Der Anzeigename. Memories liefert je nach Endpunkt basename oder filename;
            ''' fehlt beides, traegt die Dateikennung den Namen, damit die Endungserkennung nicht
            ''' auf einer leeren Zeichenkette steht.</summary>
            <JsonIgnore>
            Public ReadOnly Property DisplayName As String
                Get
                    If Not String.IsNullOrEmpty(BaseName) Then Return BaseName
                    If Not String.IsNullOrEmpty(FileName) Then Return IO.Path.GetFileName(FileName)
                    Return FileId.ToString(Globalization.CultureInfo.InvariantCulture)
                End Get
            End Property
        End Class

        ''' <summary>Ein Cluster: Album, Person, Ort oder Stichwort. Memories fuehrt alle vier ueber
        ''' EINEN Endpunkt mit verschiedenen Backends - das passt zu unserem virtuellen Baum besser
        ''' als Immichs vier getrennte Wege.
        '''
        ''' Zur KENNUNG, gemessen an einem Album: es traegt BEIDE Felder, <c>album_id</c> als Zahl
        ''' (1) und <c>cluster_id</c> als Text ("patrick/Nordsee"). Der Text ist der richtige, denn
        ''' er ist der, mit dem der Server anschliessend gefragt wird; die Zahl ist die interne
        ''' Zeilennummer. <see cref="Id"/> nimmt deshalb <c>cluster_id</c> zuerst.</summary>
        Public Class NextcloudCluster
            ' Als JsonElement und NICHT als String: album_id kommt als ZAHL (gemessen: 1), waehrend
            ' cluster_id Text ist. Eine Zahl in eine Zeichenkette zu deserialisieren wirft - und
            ' zwar die ganze Antwort, nicht nur dieses Feld. Genau diese Sorte Fehler hat bei Immich
            ' Zeit gekostet. JsonElement nimmt jede Form an, die Umwandlung in Text passiert erst
            ' beim Lesen.
            <JsonPropertyName("cluster_id")> Public Property ClusterIdRaw As JsonElement
            <JsonPropertyName("album_id")> Public Property AlbumIdRaw As JsonElement
            <JsonPropertyName("name")> Public Property Name As String = ""
            <JsonPropertyName("count")> Public Property Count As Integer
            <JsonPropertyName("location")> Public Property Location As String = ""
            ''' <summary>Der Benutzer, dem der Cluster gehoert. Bei einem Personencluster steht er
            ''' NEBEN der Kennung, waehrend ein Album ihn schon in seiner Kennung traegt
            ''' ("patrick/Nordsee") - und der Filter verlangt beides zusammen.</summary>
            <JsonPropertyName("user_id")> Public Property UserId As String = ""
            ''' <summary>Welches Backend geantwortet hat ("albums", "recognize", "places", "tags").
            ''' Der Server schickt es mit, und es entscheidet, wie die Kennung zu bilden ist.</summary>
            <JsonPropertyName("cluster_type")> Public Property ClusterType As String = ""

            Private Shared Function AsText(element As JsonElement) As String
                Select Case element.ValueKind
                    Case JsonValueKind.Undefined, JsonValueKind.Null : Return ""
                    Case JsonValueKind.String : Return If(element.GetString(), "")
                    Case Else : Return element.ToString()
                End Select
            End Function

            ''' <summary>Die Kennung, mit der der Server anschliessend gefragt wird.
            '''
            ''' EIN PERSONENCLUSTER BRAUCHT DEN BENUTZER DAVOR, und das ist am Server GEMESSEN
            ''' (2026-08-10, Recognize auf Nextcloud 34):
            '''
            '''   ?recognize=patrick/4   liefert genau die 7 Bilder des Clusters
            '''   ?recognize=4           wird abgewiesen: {"message":"Invalid face query"}
            '''   ?face=patrick/4        wird STILLSCHWEIGEND IGNORIERT - die Antwort kommt
            '''                          ungefiltert und sieht richtig aus
            '''
            ''' Ein Album traegt den Benutzer schon in seiner Kennung ("patrick/Nordsee"), dort darf
            ''' nichts davorgesetzt werden. Unterschieden wird deshalb am cluster_type, den der
            ''' Server selbst mitschickt - nicht daran, ob zufaellig ein Schraegstrich vorkommt.</summary>
            <JsonIgnore>
            Public ReadOnly Property Id As String
                Get
                    Dim cluster = AsText(ClusterIdRaw)
                    If cluster.Length = 0 Then cluster = AsText(AlbumIdRaw)
                    If cluster.Length = 0 Then Return ""
                    Dim typ = If(ClusterType, "").ToLowerInvariant()
                    If (typ = "recognize" OrElse typ = "facerecognition") AndAlso
                       Not String.IsNullOrEmpty(UserId) AndAlso Not cluster.Contains("/"c) Then
                        Return UserId & "/" & cluster
                    End If
                    Return cluster
                End Get
            End Property

            ''' <summary>Hat die Gruppe schon einen Namen? Eine frisch erkannte Personengruppe traegt
            ''' keinen - und "4" oder "12" in einer Auswahlliste ist kein Name, sondern Rauschen.
            ''' Benannt wird auf dem Server; hier stehen nur benannte Gruppen (dieselbe Regel wie bei
            ''' Immich).</summary>
            <JsonIgnore>
            Public ReadOnly Property IsNamed As Boolean
                Get
                    Return Not String.IsNullOrWhiteSpace(Name)
                End Get
            End Property
        End Class

        Private Shared ReadOnly _jsonOptions As New JsonSerializerOptions With {
            .PropertyNameCaseInsensitive = True,
            .NumberHandling = JsonNumberHandling.AllowReadingFromString Or JsonNumberHandling.AllowNamedFloatingPointLiterals
        }

        ''' <summary>Die eigene Begründung des Servers aus einem Antwortrumpf, sonst leer.
        '''
        ''' GEMESSEN und der Grund für diese Funktion: Memories legt seine Absage in einen
        ''' JSON-Rumpf und schickt dazu 412 - denselben Code, mit dem der Server auch die
        ''' CSRF-Sperre meldet. Nur am Status ist beides also NICHT zu unterscheiden, wohl aber am
        ''' Rumpf: die App schreibt {"message":"..."}, die Sperre kommt ohne. Ohne diesen Blick
        ''' stünde bei einer nicht eingeschalteten Zusatz-App "antwortet mit 412" - und der Nutzer
        ''' suchte den Fehler bei der Anmeldung.</summary>
        Private Shared Function ServerMessage(body As String) As String
            If String.IsNullOrWhiteSpace(body) Then Return ""
            Try
                Using doc = JsonDocument.Parse(body)
                    Dim message As JsonElement
                    If doc.RootElement.ValueKind = JsonValueKind.Object AndAlso
                       doc.RootElement.TryGetProperty("message", message) Then
                        Return If(message.GetString(), "")
                    End If
                End Using
            Catch
                ' Kein JSON (etwa eine Fehlerseite in HTML) - dann gibt es eben keine Begruendung.
            End Try
            Return ""
        End Function

        ''' <summary>Setzt <see cref="LastError"/> aus einer misslungenen Antwort: die Begründung des
        ''' Servers, wenn er eine mitschickt, sonst der nackte Status.</summary>
        Private Shared Async Function SetErrorFromResponse(response As HttpResponseMessage, cancellationToken As CancellationToken) As Task
            Dim body = ""
            Try
                body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
            Catch
            End Try
            Dim message = ServerMessage(body)
            LastError = If(message.Length > 0, message,
                           String.Format(LocalizationService.T("Nextcloud antwortet mit {0}"), CInt(response.StatusCode)))
        End Function

        Private Shared Async Function GetJsonAsync(Of T)(pathAndQuery As String, cancellationToken As CancellationToken) As Task(Of T)
            LastError = ""
            If Not IsConfigured Then
                LastError = LocalizationService.T("Nextcloud ist nicht eingerichtet")
                Return Nothing
            End If
            Try
                Dim response = Await GetClient().GetAsync(ApiUrl(pathAndQuery), cancellationToken).ConfigureAwait(False)
                If Not response.IsSuccessStatusCode Then
                    Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                    Return Nothing
                End If
                Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                Return JsonSerializer.Deserialize(Of T)(body, _jsonOptions)
            Catch ex As Exception
                LastError = ex.Message
                ' Unabhaengig vom Diagnose-Schalter festhalten: ein verschluckter Fehlschlag im
                ' Serverweg sieht in der Galerie wie "keine Bilder" aus. Genau das hat bei Immich
                ' viel Zeit gekostet.
                DiagnosticLogService.LogException("Nextcloud", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Der Filter auf ein Album, an Zeitachse UND Tagesabruf angehaengt.
        '''
        ''' GEMESSEN: der Parameter heisst <c>albums</c> und nimmt die <c>cluster_id</c>
        ''' ("patrick/Nordsee"). Ein naheliegendes <c>album=1</c> mit der Zahlenkennung wird
        ''' STILLSCHWEIGEND IGNORIERT - die Antwort kommt dann ungefiltert und sieht richtig aus.
        ''' Der Schraegstrich in der Kennung muss kodiert werden, sonst endet er als Pfadtrenner.</summary>
        ''' <summary>Der Filter auf einen Cluster. Der Parameter heisst wie das BACKEND
        ''' (<c>albums</c>, <c>recognize</c>, <c>places</c>, <c>tags</c>) und nimmt dessen Kennung.
        ''' Fuer <c>albums</c> ist das gemessen; fuer die uebrigen folgt es demselben Muster, ist
        ''' aber ungeprueft, weil auf der Messinstanz weder Orte eingeschaltet waren noch schon
        ''' Personengruppen bestanden.</summary>
        Private Shared Function ClusterFilter(backend As String, clusterId As String) As String
            If String.IsNullOrWhiteSpace(backend) OrElse String.IsNullOrWhiteSpace(clusterId) Then Return ""
            Return "?" & Uri.EscapeDataString(backend) & "=" & Uri.EscapeDataString(clusterId)
        End Function

        ''' <summary>Die Zeitachse: je Tag eine Kennung und die Anzahl.
        '''
        ''' OHNE MEMORIES baut sie der Rueckfall aus einer WebDAV-Suche selbst zusammen; der Aufrufer
        ''' merkt davon nichts. Dort haengen die Aufnahmen gleich an JEDEM Tag, waehrend Memories sie
        ''' nur beim ersten mitschickt - die vorhandene Schleife fragt dann von selbst nicht mehr
        ''' nach.</summary>
        Public Shared Async Function GetDaysAsync(Optional cancellationToken As CancellationToken = Nothing,
                                                  Optional albumId As String = Nothing,
                                                  Optional backend As String = "albums") As Task(Of List(Of NextcloudDay))
            LastError = ""
            If Not IsConfigured Then
                LastError = LocalizationService.T("Nextcloud ist nicht eingerichtet")
                Return New List(Of NextcloudDay)()
            End If
            If Await EnsureModeAsync(cancellationToken).ConfigureAwait(False) = ServerMode.Photos Then
                Return Await FallbackDaysAsync(backend, albumId, cancellationToken).ConfigureAwait(False)
            End If
            Return If(Await GetJsonAsync(Of List(Of NextcloudDay))("days" & ClusterFilter(backend, albumId), cancellationToken).ConfigureAwait(False),
                      New List(Of NextcloudDay)())
        End Function

        ''' <summary>Die Aufnahmen eines Tages.</summary>
        Public Shared Async Function GetDayAsync(dayId As Long, Optional cancellationToken As CancellationToken = Nothing,
                                                 Optional albumId As String = Nothing,
                                                 Optional backend As String = "albums") As Task(Of List(Of NextcloudPhoto))
            LastError = ""
            If Not IsConfigured Then
                LastError = LocalizationService.T("Nextcloud ist nicht eingerichtet")
                Return New List(Of NextcloudPhoto)()
            End If
            If Await EnsureModeAsync(cancellationToken).ConfigureAwait(False) = ServerMode.Photos Then
                Return Await FallbackDayAsync(dayId, backend, albumId, cancellationToken).ConfigureAwait(False)
            End If
            Return If(Await GetJsonAsync(Of List(Of NextcloudPhoto))(
                          "days/" & dayId.ToString(Globalization.CultureInfo.InvariantCulture) & ClusterFilter(backend, albumId),
                          cancellationToken).ConfigureAwait(False),
                      New List(Of NextcloudPhoto)())
        End Function

        ''' <summary>Alben, Personen, Orte oder Stichwoerter.
        '''
        ''' EIN NICHT EINGESCHALTETES BACKEND ANTWORTET MIT EINEM OBJEKT UND MIT 412. Gemessen:
        ''' ohne eingeschaltete Gesichtserkennung liefert "recognize"
        ''' {"message":"Recognize app not enabled or not the required version."} mit Status 412 -
        ''' demselben Code wie die CSRF-Sperre. Unterschieden wird deshalb am RUMPF, nicht am
        ''' Status (siehe <see cref="ServerMessage"/>).
        '''
        ''' Zusaetzlich wird die Form angesehen, bevor deserialisiert wird: ein Objekt direkt auf
        ''' eine Liste zu lesen wirft, und der Fehler saehe aus wie ein kaputter Server.
        '''
        ''' Ein leeres Ergebnis ist hier also kein Fehler; warum es leer ist, steht in
        ''' <see cref="LastError"/>.</summary>
        Public Shared Async Function GetClustersAsync(backend As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of List(Of NextcloudCluster))
            LastError = ""
            Dim leer = New List(Of NextcloudCluster)()
            If Not IsConfigured Then
                LastError = LocalizationService.T("Nextcloud ist nicht eingerichtet")
                Return leer
            End If
            ' OHNE MEMORIES gibt es zwei der vier Backends trotzdem: die Alben liegen in der
            ' Photos-App, die Stichwoerter im Kern. Personen und Orte gibt es NICHT, und die leere
            ' Liste ist dort die richtige Antwort - der Zweig bleibt dann ganz weg, statt leer im
            ' Baum zu stehen.
            If Await EnsureModeAsync(cancellationToken).ConfigureAwait(False) = ServerMode.Photos Then
                Select Case If(backend, "").ToLowerInvariant()
                    Case "albums" : Return Await FallbackAlbumsAsync(cancellationToken).ConfigureAwait(False)
                    Case "tags" : Return Await FallbackTagClustersAsync(cancellationToken).ConfigureAwait(False)
                    Case "places" : Return Await FallbackPlacesAsync(cancellationToken).ConfigureAwait(False)
                    Case Else
                        LastError = LocalizationService.T("Ohne die Memories-App kennt dieser Server keine Personen")
                        Return leer
                End Select
            End If
            Try
                Dim response = Await GetClient().GetAsync(ApiUrl("clusters/" & Uri.EscapeDataString(backend)), cancellationToken).ConfigureAwait(False)
                If Not response.IsSuccessStatusCode Then
                    Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                    Return leer
                End If
                Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                Using doc = JsonDocument.Parse(body)
                    If doc.RootElement.ValueKind <> JsonValueKind.Array Then
                        Dim message = ServerMessage(body)
                        LastError = If(message.Length > 0, message, LocalizationService.T("Nextcloud ist nicht eingerichtet"))
                        Return leer
                    End If
                End Using
                Return If(JsonSerializer.Deserialize(Of List(Of NextcloudCluster))(body, _jsonOptions), leer)
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
                Return leer
            End Try
        End Function

        ''' <summary>Vorschaubild. Diese Route ist von der CSRF-Pruefung ausgenommen, sie ist also
        ''' auch dann brauchbar, wenn sich die Frage oben ungünstig beantwortet.</summary>
        Public Shared Async Function GetPreviewBytesAsync(fileId As String, size As Integer,
                                                          Optional cancellationToken As CancellationToken = Nothing,
                                                          Optional etag As String = Nothing) As Task(Of Byte())
            LastError = ""
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(fileId) Then Return Nothing

            ' Ohne Etag wird NICHT zwischengespeichert: ein Eintrag ohne Etag liesse sich nie
            ' entwerten, und eine falsche Kachel ist schlimmer als eine langsame.
            Dim cachePath = If(String.IsNullOrEmpty(etag), Nothing, CacheFilePath(fileId, etag, size))
            If cachePath IsNot Nothing Then
                Try
                    If File.Exists(cachePath) Then Return Await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Throw
                Catch
                End Try
            End If

            Try
                ' OHNE MEMORIES rechnet der KERN die Vorschau vor. Beide Wege liefern ein fertiges
                ' Bild in der angefragten Groesse; nur die Adresse unterscheidet sich, der
                ' Zwischenspeicher darueber gilt fuer beide.
                Dim url As String
                If Await EnsureModeAsync(cancellationToken).ConfigureAwait(False) = ServerMode.Photos Then
                    ' Sagt der Server selbst, dass es keine Vorschau gibt (ab Werk gilt das fuer
                    ' Videos: der Kern rechnet sie nur mit eingeschaltetem Movie-Anbieter vor), wird
                    ' gar nicht erst gefragt. Sonst liefe je Video bei jedem Laden eine Anfrage, die
                    ' nur eine Absage holt.
                    If IsKnownWithoutPreview(fileId) Then Return Nothing
                    url = CorePreviewUrl(fileId, size)
                Else
                    url = ApiUrl($"image/preview/{Uri.EscapeDataString(fileId)}?x={size}&y={size}")
                End If
                Dim response = Await GetClient().GetAsync(url, cancellationToken).ConfigureAwait(False)
                If Not response.IsSuccessStatusCode Then
                    Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                    Return Nothing
                End If
                Dim bytes = Await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(False)
                If cachePath IsNot Nothing AndAlso bytes IsNot Nothing AndAlso bytes.Length > 0 Then
                    Try
                        Dim folder = IO.Path.GetDirectoryName(cachePath)
                        Directory.CreateDirectory(folder)
                        ' Erst daneben schreiben, dann umbenennen: ein abgebrochener Schreibvorgang
                        ' hinterlaesst sonst eine halbe Datei, die spaeter als gueltige Kachel gilt.
                        Dim temp = cachePath & ".part"
                        Await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(False)
                        File.Move(temp, cachePath, overwrite:=True)
                        DropOlderCacheEntries(folder, fileId, cachePath)
                    Catch ex As OperationCanceledException
                        Throw
                    Catch
                    End Try
                End If
                Return bytes
            Catch ex As Exception
                LastError = ex.Message
                Return Nothing
            End Try
        End Function

        ' ── Rueckweg: Begleitdatei neben das Original ───────────────────────────
        '
        ' DAS IST DER PUNKT, AN DEM NEXTCLOUD MEHR KANN ALS IMMICH. Weil die Fotos im Dateibaum des
        ' Nutzers liegen, gibt es einen Ordner, in den eine .fpxmp gehoert. Eine RAW auf dem Server
        ' ist damit zerstoerungsfrei bearbeitbar: das Original bleibt Byte fuer Byte liegen, daneben
        ' steht das Rezept, und beim naechsten Oeffnen wird es angewendet.
        '
        ' Am Server gemessen: der PUT antwortet mit 201, und Memories zaehlt die Begleitdatei NICHT
        ' als Foto (die Zeitachse blieb bei 173 Aufnahmen). Sie verstopft die Ansicht also nicht.

        Private Shared Function FileUrl(pathInTree As String) As String
            Dim s = AppSettingsService.Load()
            Return NormalizeServerUrl(s.NextcloudServerUrl) & "/remote.php/dav/files/" &
                   Uri.EscapeDataString(If(s.NextcloudUserName, "").Trim()) &
                   String.Join("/", If(pathInTree, "").Split("/"c).Select(Function(t) Uri.EscapeDataString(t)))
        End Function

        ''' <summary>Was ein PUT ergeben hat. <see cref="Conflict"/> heisst: der Server hat die
        ''' mitgeschickte BEDINGUNG abgelehnt (412) - dort lag etwas anderes, als der Aufrufer
        ''' erwartet hat. Das ist kein Fehler, sondern eine Antwort, und der Aufrufer entscheidet
        ''' selbst, was daraus folgt.</summary>
        Public Class NextcloudPutResult
            Public Property Ok As Boolean
            Public Property Conflict As Boolean
            ''' <summary>Der Etag der Datei NACH dem Schreiben, sofern der Server ihn mitschickt.
            ''' Wer gleich noch einmal schreiben will, braucht ihn - sonst weist die naechste
            ''' Bedingung die eigene Aenderung ab.</summary>
            Public Property ETag As String = ""
        End Class

        ''' <summary>Legt eine lokale Datei im Dateibaum ab. Mit <paramref name="onlyIfAbsent"/> nur,
        ''' wenn dort noch nichts liegt; mit <paramref name="ifMatchETag"/> nur, wenn dort noch genau
        ''' der erwartete Stand liegt.
        '''
        ''' BEIDE BEDINGUNGEN GEHEN AN DEN SERVER, nicht in eine Vorabprüfung: zwischen einem HEAD
        ''' und dem PUT liegt ein Zeitfenster, in dem ein Handy, ein Sync-Client oder die
        ''' Weboberflaeche dieselbe Datei anlegen oder aendern kann. Der Server ist die einzige
        ''' Stelle, die beides zusammen entscheiden kann.
        '''
        ''' Der Inhalt wird GESTROEMT, nicht in den Speicher gelesen: ein RAW oder ein Video sonst
        ''' vollstaendig, und bei mehreren Dateien hintereinander mehrfach.</summary>
        Public Shared Async Function PutFileAsync(localPath As String, targetPathInTree As String,
                                                  Optional cancellationToken As CancellationToken = Nothing,
                                                  Optional onlyIfAbsent As Boolean = False,
                                                  Optional ifMatchETag As String = Nothing) As Task(Of NextcloudPutResult)
            Dim result = New NextcloudPutResult()
            LastError = ""
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(localPath) OrElse Not File.Exists(localPath) Then Return result
            If String.IsNullOrWhiteSpace(targetPathInTree) Then Return result
            Try
                Using request = New HttpRequestMessage(HttpMethod.Put, FileUrl(TreePath(targetPathInTree)))
                    ' Der Stream gehoert dem Inhalt und wird mit dem Request geschlossen.
                    Dim sourceStream = New FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                      bufferSize:=81920, useAsync:=True)
                    request.Content = New StreamContent(sourceStream)
                    request.Content.Headers.ContentType = New MediaTypeHeaderValue("application/octet-stream")
                    If onlyIfAbsent Then request.Headers.TryAddWithoutValidation("If-None-Match", "*")
                    If Not String.IsNullOrEmpty(ifMatchETag) Then
                        request.Headers.TryAddWithoutValidation("If-Match", QuotedETag(ifMatchETag))
                    End If
                    Dim response = Await GetTransferClient().SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If response.StatusCode = HttpStatusCode.PreconditionFailed Then
                        result.Conflict = True
                        Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                        Return result
                    End If
                    If Not response.IsSuccessStatusCode Then
                        Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                        Return result
                    End If
                    result.Ok = True
                    result.ETag = If(response.Headers.ETag?.Tag, "").Trim(""""c)
                    DropTimelineCache()
                    Return result
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
                Return result
            End Try
        End Function

        ''' <summary>Der Etag in der Schreibweise, die ein Kopfeintrag verlangt: in Anfuehrungszeichen.
        ''' Memories liefert ihn nackt, WebDAV erwartet ihn zitiert - ohne das weist der Server jede
        ''' Bedingung ab, und ein Speichern schlaege immer fehl.</summary>
        Private Shared Function QuotedETag(etag As String) As String
            Dim wert = If(etag, "").Trim()
            If wert.Length = 0 Then Return ""
            If wert.StartsWith("W/", StringComparison.Ordinal) Then Return wert
            If wert.StartsWith("""", StringComparison.Ordinal) Then Return wert
            Return """" & wert & """"
        End Function

        ''' <summary>Legt eine lokale Datei im Dateibaum ab (ueberschreibt, falls vorhanden).</summary>
        Public Shared Async Function UploadFileAsync(localPath As String, targetPathInTree As String,
                                                     Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            Return (Await PutFileAsync(localPath, targetPathInTree, cancellationToken).ConfigureAwait(False)).Ok
        End Function

        ''' <summary>Holt die Begleitdatei zu einem Foto, wenn es eine gibt, und legt sie unter
        ''' <paramref name="localTargetPath"/> ab. False heisst "keine da" ODER "Fehler" - der
        ''' Unterschied steht in <see cref="LastError"/> und ist fuer den Aufrufer folgenlos: ohne
        ''' Begleitdatei wird eben nichts angewendet.</summary>
        Public Shared Async Function TryDownloadSidecarAsync(filePathInTree As String, localTargetPath As String,
                                                             Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            LastError = ""
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(filePathInTree) Then Return False
            Try
                Dim response = Await GetClient().GetAsync(FileUrl(filePathInTree & RawSidecarService.Extension),
                                                          cancellationToken).ConfigureAwait(False)
                If response.StatusCode = HttpStatusCode.NotFound Then Return False
                If Not response.IsSuccessStatusCode Then
                    Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                    Return False
                End If
                Dim bytes = Await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(False)
                If bytes Is Nothing OrElse bytes.Length = 0 Then Return False
                Await File.WriteAllBytesAsync(localTargetPath, bytes, cancellationToken).ConfigureAwait(False)
                Return True
            Catch ex As Exception
                LastError = ex.Message
                Return False
            End Try
        End Function

        ''' <summary>Loescht eine Datei im Dateibaum. Sie landet im Nextcloud-Papierkorb, ist also
        ''' vom Nutzer wiederherstellbar - anders als bei Immich braucht es dafuer keinen zweiten
        ''' Schalter. Die Begleitdatei wird MITGENOMMEN, sonst bliebe ein Rezept ohne Bild liegen.</summary>
        Public Shared Async Function DeleteFileAsync(filePathInTree As String,
                                                     Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(filePathInTree) Then Return False
            Dim ok = Await SendDavAsync("DELETE", FileUrl(filePathInTree), cancellationToken:=cancellationToken).ConfigureAwait(False)
            If ok Then
                ' Ein Fehlschlag hier ist folgenlos (meist gab es gar keine) und darf das Ergebnis
                ' nicht kippen.
                Try : Await DeleteSidecarAsync(filePathInTree, cancellationToken).ConfigureAwait(False) : Catch : End Try
            End If
            Return ok
        End Function

        ''' <summary>Loescht ENDGUELTIG, am Papierkorb vorbei.
        '''
        ''' Zwei Schritte, und das liegt am Server: ein WebDAV-DELETE legt die Datei immer in den
        ''' Papierkorb, einen Weg daran vorbei gibt es nicht. Endgueltig wird es erst, wenn dort auch
        ''' der Eintrag entfernt wird - und der heisst nicht wie die Datei, gesucht wird deshalb ueber
        ''' die Dateikennung, die vor dem Loeschen geholt wird.
        '''
        ''' Die Begleitdatei geht mit: sie traegt eine eigene Kennung, die wir nicht kennen, und wird
        ''' ueber ihren Namen samt Herkunftsordner gefunden. Bleibt sie liegen, steht im Papierkorb
        ''' ein Rezept ohne Bild.</summary>
        Public Shared Async Function DeleteFilePermanentlyAsync(filePathInTree As String,
                                                                Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(filePathInTree) Then Return False
            Dim fileId = Await GetFileIdAsync(filePathInTree, cancellationToken).ConfigureAwait(False)
            If Not Await DeleteFileAsync(filePathInTree, cancellationToken).ConfigureAwait(False) Then Return False

            Dim path = TreePath(filePathInTree)
            Dim folder = ""
            Dim separatorIndex = path.LastIndexOf("/"c)
            If separatorIndex > 0 Then folder = path.Substring(0, separatorIndex)
            Dim sidecarName = IO.Path.GetFileName(path) & RawSidecarService.Extension

            Try
                Dim trashEntries = Await GetTrashAsync(cancellationToken).ConfigureAwait(False)
                For Each entry In trashEntries
                    If entry Is Nothing OrElse String.IsNullOrEmpty(entry.Url) Then Continue For
                    Dim isTheFile = Not String.IsNullOrEmpty(fileId) AndAlso String.Equals(entry.FileId, fileId, StringComparison.Ordinal)
                    Dim isTheSidecar = String.Equals(entry.DisplayName, sidecarName, StringComparison.Ordinal) AndAlso
                                       (folder.Length = 0 OrElse
                                        String.Equals(If(entry.OriginalLocation, "").TrimStart("/"c),
                                                      (folder.TrimStart("/"c) & "/" & sidecarName).TrimStart("/"c),
                                                      StringComparison.Ordinal))
                    If isTheFile OrElse isTheSidecar Then
                        Await DeleteFromTrashAsync(entry.Url, cancellationToken).ConfigureAwait(False)
                    End If
                Next
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                ' Geloescht IST sie - dass der Papierkorbeintrag stehenbleibt, ist aergerlich, aber
                ' kein Grund, das Loeschen als gescheitert zu melden.
                DiagnosticLogService.LogException("Nextcloud", ex)
            End Try
            Return True
        End Function

        ''' <summary>Entfernt die Begleitdatei vom Server. Das Original bleibt.</summary>
        Public Shared Function DeleteSidecarAsync(filePathInTree As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(filePathInTree) Then Return Task.FromResult(False)
            Return SendDavAsync("DELETE", FileUrl(filePathInTree & RawSidecarService.Extension), cancellationToken:=cancellationToken)
        End Function

        ''' <summary>Legt die Begleitdatei neben das Original auf dem Server.</summary>
        Public Shared Function UploadSidecarAsync(localSidecarPath As String, filePathInTree As String,
                                                  Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(filePathInTree) Then Return Task.FromResult(False)
            Return UploadFileAsync(localSidecarPath, filePathInTree & RawSidecarService.Extension, cancellationToken)
        End Function

        ' ── Favorit ─────────────────────────────────────────────────────────────

        ''' <summary>Die Dateikennungen ALLER Favoriten des Benutzers, in EINEM Aufruf.
        '''
        ''' Gegenstueck zum Setzen - ohne das waere der Favorit nur schreibbar: ein auf dem Server
        ''' markiertes Foto zeigte hier ein leeres Herz, und ein hier gesetztes waere nach dem
        ''' Neuladen wieder weg. Der WebDAV-REPORT liefert sie gebuendelt, statt je Kachel zu fragen
        ''' (gemessen: ein Aufruf, 207).</summary>
        Public Shared Async Function GetFavoriteFileIdsAsync(Optional cancellationToken As CancellationToken = Nothing) As Task(Of HashSet(Of String))
            Dim result = New HashSet(Of String)(StringComparer.Ordinal)
            LastError = ""
            If Not IsConfigured Then Return result
            Try
                Dim s = AppSettingsService.Load()
                Dim url = NormalizeServerUrl(s.NextcloudServerUrl) & "/remote.php/dav/files/" &
                          Uri.EscapeDataString(If(s.NextcloudUserName, "").Trim()) & "/"
                Dim rumpf = "<?xml version=""1.0""?>" &
                            "<oc:filter-files xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"">" &
                            "<d:prop><oc:fileid/></d:prop>" &
                            "<oc:filter-rules><oc:favorite>1</oc:favorite></oc:filter-rules>" &
                            "</oc:filter-files>"
                Using request = New HttpRequestMessage(New HttpMethod("REPORT"), url)
                    request.Content = New StringContent(rumpf, Encoding.UTF8, "application/xml")
                    Dim response = Await GetClient().SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If Not response.IsSuccessStatusCode Then
                        Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                        Return result
                    End If
                    Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    For Each treffer As Text.RegularExpressions.Match In
                        Text.RegularExpressions.Regex.Matches(body, "<oc:fileid>(\d+)</oc:fileid>")
                        result.Add(treffer.Groups(1).Value)
                    Next
                End Using
            Catch ex As Exception
                LastError = ex.Message
            End Try
            Return result
        End Function

        ''' <summary>Setzt oder loescht den Favoriten. Das laeuft NICHT ueber Memories, sondern ueber
        ''' die WebDAV-Eigenschaft `oc:favorite` an der Datei - am Server gemessen (PROPPATCH gibt 207
        ''' mit einem 200 im Rumpf, das Zuruecklesen bestaetigt den Wert).
        '''
        ''' Sterne und Farbetikett gibt es auf der Gegenseite NICHT und sie bleiben deshalb lokal im
        ''' Katalog. Der Favorit ist die einzige dieser drei Angaben, die der Server kennt.</summary>
        Public Shared Async Function SetFavoriteAsync(filePathInTree As String, isFavorite As Boolean,
                                                      Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            LastError = ""
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(filePathInTree) Then Return False
            Try
                Dim s = AppSettingsService.Load()
                Dim url = NormalizeServerUrl(s.NextcloudServerUrl) & "/remote.php/dav/files/" &
                          Uri.EscapeDataString(If(s.NextcloudUserName, "").Trim()) &
                          String.Join("/", filePathInTree.Split("/"c).Select(Function(t) Uri.EscapeDataString(t)))
                Dim rumpf = "<?xml version=""1.0""?>" &
                            "<d:propertyupdate xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"">" &
                            "<d:set><d:prop><oc:favorite>" & If(isFavorite, "1", "0") & "</oc:favorite></d:prop></d:set>" &
                            "</d:propertyupdate>"
                Using request = New HttpRequestMessage(New HttpMethod("PROPPATCH"), url)
                    request.Content = New StringContent(rumpf, Encoding.UTF8, "application/xml")
                    Dim response = Await GetClient().SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If Not response.IsSuccessStatusCode Then
                        Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                        Return False
                    End If
                    ' 207 heisst nur "Mehrfachantwort" - ob die Eigenschaft wirklich gesetzt wurde,
                    ' steht IM Rumpf. Ein abgelehntes Setzen kaeme sonst als Erfolg durch.
                    Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    If body.IndexOf("200 OK", StringComparison.OrdinalIgnoreCase) < 0 Then
                        LastError = String.Format(LocalizationService.T("Nextcloud antwortet mit {0}"), CInt(response.StatusCode))
                        Return False
                    End If
                    Return True
                End Using
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
                Return False
            End Try
        End Function

        ' ── Hochladen in den Dateibaum ──────────────────────────────────────────
        '
        ' Bei Immich stellt sich die Frage nicht, weil es dort keine Ordner gibt. Hier ist genau sie
        ' das Einzige, was am Hochladen offen war: WOHIN. Beantwortet wird sie von der Einstellung
        ' "Zielordner fuer Uploads"; die Vorgabe /Photos ist der Ordner, den Nextcloud fuer die Fotos
        ' seiner eigenen Anwendungen anlegt.

        Public Const DefaultUploadFolder As String = "/Photos"

        ''' <summary>Der eingestellte Zielordner, immer mit fuehrendem und ohne abschliessenden
        ''' Schraegstrich. Leer heisst Vorgabe - ein Upload in die Wurzel des Dateibaums waere ein
        ''' Griff in fremde Ordnung.</summary>
        Public Shared ReadOnly Property UploadFolder As String
            Get
                Dim eingestellt = If(AppSettingsService.Load().NextcloudUploadFolder, "").Trim().Replace("\"c, "/"c)
                eingestellt = "/" & eingestellt.Trim("/"c)
                Return If(eingestellt = "/", DefaultUploadFolder, eingestellt)
            End Get
        End Property

        ''' <summary>Pfad im Dateibaum in der Schreibweise, die <see cref="FileUrl"/> erwartet: mit
        ''' fuehrendem Schraegstrich. Ohne ihn faellt beim Zusammenbauen der Adresse der Trenner
        ''' zwischen Benutzer und Pfad weg, und der PUT landet neben dem Benutzerordner.</summary>
        Private Shared Function TreePath(pathInTree As String) As String
            Return "/" & If(pathInTree, "").Replace("\"c, "/"c).TrimStart("/"c)
        End Function

        ''' <summary>Liegt unter diesem Pfad schon etwas? HEAD statt PROPFIND: gefragt ist nur "da
        ''' oder nicht da", und ein 404 ist hier die Antwort und kein Fehler.</summary>
        Private Shared Async Function ExistsAsync(pathInTree As String, cancellationToken As CancellationToken) As Task(Of Boolean)
            Try
                Using request = New HttpRequestMessage(HttpMethod.Head, FileUrl(TreePath(pathInTree)))
                    Dim response = Await GetClient().SendAsync(request, cancellationToken).ConfigureAwait(False)
                    Return response.IsSuccessStatusCode
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch
                ' Im Zweifel "da": ein nicht beantwortetes HEAD darf nicht dazu fuehren, dass der
                ' naechste Schritt eine fremde Datei ueberschreibt.
                Return True
            End Try
        End Function

        ''' <summary>Legt den Zielordner an, falls er fehlt - Segment fuer Segment, denn MKCOL legt
        ''' keine Zwischenstufen an. Ein 405 heisst "gibt es schon" und gilt deshalb als Erfolg.</summary>
        Public Shared Async Function EnsureFolderAsync(folderPathInTree As String,
                                                       Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            LastError = ""
            If Not IsConfigured Then
                LastError = LocalizationService.T("Nextcloud ist nicht eingerichtet")
                Return False
            End If
            Dim teile = If(folderPathInTree, "").Replace("\"c, "/"c).Split("/"c).Where(Function(t) t.Length > 0).ToArray()
            If teile.Length = 0 Then Return True
            Dim bisher = ""
            For Each segment In teile
                bisher &= "/" & segment
                Try
                    Using request = New HttpRequestMessage(New HttpMethod("MKCOL"), FileUrl(bisher))
                        Dim response = Await GetClient().SendAsync(request, cancellationToken).ConfigureAwait(False)
                        If Not response.IsSuccessStatusCode AndAlso response.StatusCode <> HttpStatusCode.MethodNotAllowed Then
                            Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                            Return False
                        End If
                    End Using
                Catch ex As OperationCanceledException
                    Throw
                Catch ex As Exception
                    LastError = ex.Message
                    DiagnosticLogService.LogException("Nextcloud", ex)
                    Return False
                End Try
            Next
            Return True
        End Function

        ''' <summary>Legt eine lokale Datei im Zielordner ab, OHNE etwas zu ueberschreiben: ist der
        ''' Name belegt, wird nummeriert. Liefert den Pfad im Dateibaum, sonst Nothing.
        '''
        ''' Der Unterschied zu <see cref="UploadFileAsync"/> ist Absicht. Dort IST das Ueberschreiben
        ''' der Zweck (Begleitdatei, Original ersetzen), hier waere es der Verlust einer fremden
        ''' Datei: zwei Kameras vergeben denselben Namen, und DSC_0001.JPG gibt es in jedem
        ''' Bestand mehrfach.</summary>
        Public Shared Async Function UploadNewFileAsync(localPath As String, folderPathInTree As String,
                                                        Optional cancellationToken As CancellationToken = Nothing) As Task(Of String)
            LastError = ""
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(localPath) OrElse Not File.Exists(localPath) Then Return Nothing
            Dim folder = TreePath(If(String.IsNullOrWhiteSpace(folderPathInTree), UploadFolder, folderPathInTree)).TrimEnd("/"c)
            If Not Await EnsureFolderAsync(folder, cancellationToken).ConfigureAwait(False) Then Return Nothing

            Dim baseName = Path.GetFileNameWithoutExtension(localPath)
            Dim extension = Path.GetExtension(localPath)
            Dim attempt = 1
            ' Das HEAD ist nur der schnelle Vorlauf - es spart einen Upload gegen einen Namen, der
            ' schon belegt IST. Entschieden wird am Server: der PUT traegt "If-None-Match: *" und
            ' wird abgewiesen, wenn zwischen Frage und Antwort jemand anderes die Datei angelegt
            ' hat. Ohne diese Bedingung wuerde sie ueberschrieben, und ein Upload darf nichts
            ' wegnehmen.
            While attempt <= 999
                Dim target = If(attempt = 1, folder & "/" & baseName & extension,
                                folder & "/" & baseName & "_" & attempt.ToString(Globalization.CultureInfo.InvariantCulture) & extension)
                If Await ExistsAsync(target, cancellationToken).ConfigureAwait(False) Then
                    attempt += 1
                    Continue While
                End If
                Dim result = Await PutFileAsync(localPath, target, cancellationToken, onlyIfAbsent:=True).ConfigureAwait(False)
                If result.Ok Then Return target
                If Not result.Conflict Then Return Nothing
                ' 412: jemand war schneller. Der naechste Name.
                attempt += 1
            End While
            LastError = LocalizationService.T("Der Zielordner enthält zu viele gleichnamige Dateien")
            Return Nothing
        End Function

        ''' <summary>Ersetzt das Original an Ort und Stelle. Das ist der eine Weg, den Immich NICHT
        ''' hat (dort entfiel der Ersetzen-Endpunkt mit v3), und er ist ein gewoehnlicher PUT auf
        ''' denselben Pfad: Dateikennung, Alben und Freigaben bleiben, weil die Datei dieselbe bleibt.
        '''
        ''' Der Aufrufer entscheidet, OB ersetzt wird - die Vorgabe der Anwendung ist die
        ''' Begleitdatei, und das Ersetzen haengt an einer eigenen Einstellung.
        '''
        ''' MIT DEM ETAG DES STANDES, den der Aufrufer geoeffnet hat: hat inzwischen jemand anderes
        ''' dieselbe Datei geaendert - ueber die Weboberflaeche, ein Handy oder einen Sync-Client -,
        ''' weist der Server den PUT ab (412), statt die fremde Aenderung lautlos zu ueberschreiben.
        ''' Ohne Etag bleibt es bei einem unbedingten Schreiben; das ist der Fall, in dem wir den
        ''' Ausgangsstand gar nicht kennen.</summary>
        Public Shared Function ReplaceOriginalAsync(localPath As String, filePathInTree As String,
                                                    Optional cancellationToken As CancellationToken = Nothing,
                                                    Optional ifMatchETag As String = Nothing) As Task(Of NextcloudPutResult)
            If String.IsNullOrWhiteSpace(filePathInTree) Then Return Task.FromResult(New NextcloudPutResult())
            Return PutFileAsync(localPath, filePathInTree, cancellationToken, ifMatchETag:=ifMatchETag)
        End Function

        ' ── Stichwoerter schreiben ──────────────────────────────────────────────
        '
        ' NICHT ueber Memories, sondern ueber die System-Tags des Kerns. Memories LIEST die
        ' Stichwoerter einer Aufnahme aus genau diesen Tags; sein eigener Schreibweg haengt dagegen an
        ' der Fassung der App. Der WebDAV-Weg des Kerns ist seit Jahren derselbe und traegt auch dort,
        ' wo Memories gar nicht eingeschaltet ist:
        '
        '   PROPFIND /remote.php/dav/systemtags/                     alle Tags mit Kennung
        '   POST     /remote.php/dav/systemtags/                     neuen Tag anlegen
        '   PUT      /remote.php/dav/systemtags-relations/files/{fileid}/{tagid}    zuweisen
        '   DELETE   /remote.php/dav/systemtags-relations/files/{fileid}/{tagid}    loesen

        Private Shared ReadOnly DavNs As XNamespace = "DAV:"
        Private Shared ReadOnly OcNs As XNamespace = "http://owncloud.org/ns"
        Private Shared ReadOnly NcNs As XNamespace = "http://nextcloud.org/ns"

        Private Shared Function DavRootUrl(relativePath As String) As String
            Return NormalizeServerUrl(AppSettingsService.Load().NextcloudServerUrl) &
                   "/remote.php/dav/" & If(relativePath, "").TrimStart("/"c)
        End Function

        ''' <summary>Ein Stichwort des Servers.</summary>
        Public Class NextcloudSystemTag
            Public Property Id As String = ""
            Public Property Name As String = ""
            ''' <summary>Ob der Nutzer es selbst vergeben darf. NEIN heisst: eine Zusatz-App hat es
            ''' gesetzt (gemessen: "Tagged by recognize v3.0.0"). Solche Marken gehoeren nicht in
            ''' eine Stichwortliste - sie sind Buchhaltung des Servers, kein Stichwort des Nutzers.</summary>
            Public Property IsAssignable As Boolean = True
        End Class

        ''' <summary>Alle Stichwoerter des Servers, samt Kennung und Zuweisbarkeit.</summary>
        Public Shared Async Function GetSystemTagListAsync(Optional cancellationToken As CancellationToken = Nothing) As Task(Of List(Of NextcloudSystemTag))
            Dim result = New List(Of NextcloudSystemTag)()
            LastError = ""
            If Not IsConfigured Then Return result
            Try
                Dim rumpf = "<?xml version=""1.0""?>" &
                            "<d:propfind xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"">" &
                            "<d:prop><oc:id/><oc:display-name/><oc:user-assignable/></d:prop></d:propfind>"
                Using request = New HttpRequestMessage(New HttpMethod("PROPFIND"), DavRootUrl("systemtags/"))
                    request.Headers.Add("Depth", "1")
                    request.Content = New StringContent(rumpf, Encoding.UTF8, "application/xml")
                    Dim response = Await GetClient().SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If Not response.IsSuccessStatusCode Then
                        Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                        Return result
                    End If
                    Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    For Each eintrag In XDocument.Parse(body).Descendants(DavNs + "response")
                        Dim id = eintrag.Descendants(OcNs + "id").Select(Function(e) e.Value).FirstOrDefault()
                        Dim name = eintrag.Descendants(OcNs + "display-name").Select(Function(e) e.Value).FirstOrDefault()
                        If String.IsNullOrWhiteSpace(id) OrElse String.IsNullOrWhiteSpace(name) Then Continue For
                        Dim assignable = eintrag.Descendants(OcNs + "user-assignable").Select(Function(e) e.Value).FirstOrDefault()
                        result.Add(New NextcloudSystemTag With {
                            .Id = id, .Name = name,
                            .IsAssignable = Not String.Equals(If(assignable, ""), "false", StringComparison.OrdinalIgnoreCase)})
                    Next
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
            End Try
            Return result
        End Function

        ''' <summary>Alle zuweisbaren Stichwoerter des Servers als Name-zu-Kennung. Gross- und
        ''' Kleinschreibung wird beim Nachschlagen ignoriert: der Server erlaubt "Urlaub" und "urlaub"
        ''' nebeneinander, doppelte Stichwoerter anzulegen waere aber genau das, was niemand will.</summary>
        Public Shared Async Function GetSystemTagsAsync(Optional cancellationToken As CancellationToken = Nothing) As Task(Of Dictionary(Of String, String))
            Dim result = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            LastError = ""
            If Not IsConfigured Then Return result
            Try
                Dim rumpf = "<?xml version=""1.0""?>" &
                            "<d:propfind xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"">" &
                            "<d:prop><oc:id/><oc:display-name/><oc:user-assignable/></d:prop></d:propfind>"
                Using request = New HttpRequestMessage(New HttpMethod("PROPFIND"), DavRootUrl("systemtags/"))
                    request.Headers.Add("Depth", "1")
                    request.Content = New StringContent(rumpf, Encoding.UTF8, "application/xml")
                    Dim response = Await GetClient().SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If Not response.IsSuccessStatusCode Then
                        Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                        Return result
                    End If
                    Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    For Each eintrag In XDocument.Parse(body).Descendants(DavNs + "response")
                        Dim id = eintrag.Descendants(OcNs + "id").Select(Function(e) e.Value).FirstOrDefault()
                        Dim name = eintrag.Descendants(OcNs + "display-name").Select(Function(e) e.Value).FirstOrDefault()
                        If String.IsNullOrWhiteSpace(id) OrElse String.IsNullOrWhiteSpace(name) Then Continue For
                        result(name) = id
                    Next
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
            End Try
            Return result
        End Function

        ''' <summary>Kennung eines Stichworts, notfalls neu angelegt. Der Server antwortet auf das
        ''' Anlegen mit 201 und der Adresse des neuen Tags im Kopfeintrag; die Kennung steht am Ende
        ''' dieser Adresse. Antwortet er mit 409, gibt es den Tag schon - dann wird die Liste noch
        ''' einmal gelesen statt einen Fehler zu melden.</summary>
        Private Shared Async Function EnsureSystemTagAsync(tagName As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim name = If(tagName, "").Trim()
            If name.Length = 0 Then Return Nothing
            Dim vorhanden = Await GetSystemTagsAsync(cancellationToken).ConfigureAwait(False)
            Dim id As String = Nothing
            If vorhanden.TryGetValue(name, id) Then Return id
            Try
                Dim rumpf = JsonSerializer.Serialize(New Dictionary(Of String, Object) From {
                    {"name", name}, {"userVisible", True}, {"userAssignable", True}})
                Using request = New HttpRequestMessage(HttpMethod.Post, DavRootUrl("systemtags/"))
                    request.Content = New StringContent(rumpf, Encoding.UTF8, "application/json")
                    Dim response = Await GetClient().SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If response.StatusCode = HttpStatusCode.Conflict Then
                        Dim erneut = Await GetSystemTagsAsync(cancellationToken).ConfigureAwait(False)
                        Return If(erneut.TryGetValue(name, id), id, Nothing)
                    End If
                    If Not response.IsSuccessStatusCode Then
                        Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                        Return Nothing
                    End If
                    Dim ort As IEnumerable(Of String) = Nothing
                    If response.Headers.TryGetValues("Content-Location", ort) Then
                        Dim adresse = If(ort.FirstOrDefault(), "").TrimEnd("/"c)
                        Dim separatorIndex = adresse.LastIndexOf("/"c)
                        If separatorIndex >= 0 Then Return adresse.Substring(separatorIndex + 1)
                    End If
                End Using
                ' Ohne den Kopfeintrag hilft nur nachsehen.
                Dim danach = Await GetSystemTagsAsync(cancellationToken).ConfigureAwait(False)
                Return If(danach.TryGetValue(name, id), id, Nothing)
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Haengt ein Stichwort an eine Datei; legt es an, wenn es den Server noch nicht
        ''' kennt.</summary>
        Public Shared Async Function AddTagAsync(fileId As String, tagName As String,
                                                 Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            LastError = ""
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(fileId) Then Return False
            Dim tagId = Await EnsureSystemTagAsync(tagName, cancellationToken).ConfigureAwait(False)
            If String.IsNullOrEmpty(tagId) Then Return False
            Dim ok = Await SendDavAsync("PUT", TagRelationUrl(fileId, tagId), cancellationToken:=cancellationToken).ConfigureAwait(False)
            ' Auch in den Index, wie bei Immich: eine Stichwortaenderung hebt den Etag der Datei
            ' NICHT an, der gespeicherte Eintrag gaelte sonst weiter mit der alten Liste.
            If ok Then NextcloudIndexService.Instance.UpdateTag(ServerKey, fileId, tagName, add:=True)
            Return ok
        End Function

        ''' <summary>Loest ein Stichwort von einer Datei. Der Tag selbst bleibt auf dem Server - ihn
        ''' zu loeschen wuerde ihn allen anderen Dateien wegnehmen.</summary>
        Public Shared Async Function RemoveTagAsync(fileId As String, tagName As String,
                                                    Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            LastError = ""
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(fileId) Then Return False
            Dim alle = Await GetSystemTagsAsync(cancellationToken).ConfigureAwait(False)
            Dim tagId As String = Nothing
            ' Kennt der Server das Stichwort gar nicht, ist es an der Datei auch nicht dran.
            If Not alle.TryGetValue(If(tagName, "").Trim(), tagId) Then Return True
            Dim ok = Await SendDavAsync("DELETE", TagRelationUrl(fileId, tagId), cancellationToken:=cancellationToken).ConfigureAwait(False)
            If ok Then NextcloudIndexService.Instance.UpdateTag(ServerKey, fileId, tagName, add:=False)
            Return ok
        End Function

        ''' <summary>Die Dateikennung zu einem Pfad im Dateibaum. Gebraucht dort, wo nur der Pfad
        ''' bekannt ist - im Editor etwa, der seine Herkunft als Pfad mitfuehrt, weil die
        ''' Begleitdatei danebengelegt wird.</summary>
        Public Shared Async Function GetFileIdAsync(filePathInTree As String,
                                                    Optional cancellationToken As CancellationToken = Nothing) As Task(Of String)
            LastError = ""
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(filePathInTree) Then Return Nothing
            Try
                Dim rumpf = "<?xml version=""1.0""?>" &
                            "<d:propfind xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"">" &
                            "<d:prop><oc:fileid/></d:prop></d:propfind>"
                Using request = New HttpRequestMessage(New HttpMethod("PROPFIND"), FileUrl(TreePath(filePathInTree)))
                    request.Headers.Add("Depth", "0")
                    request.Content = New StringContent(rumpf, Encoding.UTF8, "application/xml")
                    Dim response = Await GetClient().SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If Not response.IsSuccessStatusCode Then
                        Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                        Return Nothing
                    End If
                    Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    Return XDocument.Parse(body).Descendants(OcNs + "fileid").Select(Function(e) e.Value).FirstOrDefault()
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Stichwort setzen oder loesen, wenn nur der PFAD bekannt ist.</summary>
        Public Shared Async Function SetTagByPathAsync(filePathInTree As String, tagName As String, add As Boolean,
                                                       Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            Dim fileId = Await GetFileIdAsync(filePathInTree, cancellationToken).ConfigureAwait(False)
            If String.IsNullOrEmpty(fileId) Then Return False
            Return If(add,
                      Await AddTagAsync(fileId, tagName, cancellationToken).ConfigureAwait(False),
                      Await RemoveTagAsync(fileId, tagName, cancellationToken).ConfigureAwait(False))
        End Function

        Private Shared Function TagRelationUrl(fileId As String, tagId As String) As String
            Return DavRootUrl("systemtags-relations/files/" & Uri.EscapeDataString(fileId) & "/" & Uri.EscapeDataString(tagId))
        End Function

        ' ── Textsuche ───────────────────────────────────────────────────────────
        '
        ' DIE MEMORIES-API HAT KEINE. Gesucht wird deshalb ueber die gemeinsame Suche von Nextcloud
        ' selbst (OCS), und zwar beim Anbieter "files":
        '
        '   GET /ocs/v2.php/search/providers/files/search?term=…&limit=…&cursor=…
        '
        ' Sie liefert genau das, was eine Kachel braucht - GEMESSEN an einer laufenden Instanz:
        '
        '   "title": "Viewer.jpg",
        '   "attributes": { "fileId": "170", "path": "/Photos/Viewer.jpg" }
        '
        ' Damit entfaellt der zweite Weg ueber die Dateikennung: aus einem Treffer wird ohne weitere
        ' Anfrage ein Element. Gesucht wird im DATEINAMEN, nicht in Stichwoertern - dafuer gibt es
        ' den Cluster-Zweig.

        ''' <summary>Ein Suchtreffer im Dateibaum.</summary>
        Public Class NextcloudSearchHit
            Public Property FileId As String = ""
            Public Property FileName As String = ""
            ''' <summary>Der Pfad im Dateibaum - er ist der Rueckweg zum Original (Begleitdatei,
            ''' Ersetzen, Album).</summary>
            Public Property PathInTree As String = ""
        End Class

        ''' <summary>Sucht Dateien nach Namen. Geblaettert wird ueber den Cursor, den der Server
        ''' mitschickt; abgebrochen wird bei <paramref name="maxHits"/>, damit eine unglueckliche
        ''' Suche ("a") nicht den ganzen Bestand holt.</summary>
        Public Shared Async Function SearchFilesAsync(term As String, Optional maxHits As Integer = 500,
                                                      Optional cancellationToken As CancellationToken = Nothing) As Task(Of List(Of NextcloudSearchHit))
            Dim result = New List(Of NextcloudSearchHit)()
            LastError = ""
            If Not IsConfigured Then
                LastError = LocalizationService.T("Nextcloud ist nicht eingerichtet")
                Return result
            End If
            Dim suche = If(term, "").Trim()
            If suche.Length = 0 Then Return result

            Try
                Dim basis = NormalizeServerUrl(AppSettingsService.Load().NextcloudServerUrl) &
                            "/ocs/v2.php/search/providers/files/search?term=" & Uri.EscapeDataString(suche) & "&limit=50"
                Dim cursor As String = Nothing
                ' Der Cursor zaehlt die DURCHSUCHTEN Dateien, nicht die Treffer: eine Seite kann
                ' leer sein und die naechste wieder etwas bringen. Abgebrochen wird deshalb erst,
                ' wenn der Server das Blaettern beendet oder sich der Cursor nicht mehr bewegt.
                For seite = 1 To 20
                    Dim url = If(String.IsNullOrEmpty(cursor), basis, basis & "&cursor=" & Uri.EscapeDataString(cursor))
                    Dim response = Await GetClient().GetAsync(url, cancellationToken).ConfigureAwait(False)
                    If Not response.IsSuccessStatusCode Then
                        Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                        Exit For
                    End If
                    Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    Dim hasMore = False
                    Dim nextCursor As String = Nothing
                    Using doc = JsonDocument.Parse(body)
                        Dim data As JsonElement
                        If Not doc.RootElement.TryGetProperty("ocs", data) Then Exit For
                        If Not data.TryGetProperty("data", data) Then Exit For
                        Dim entries As JsonElement
                        If data.TryGetProperty("entries", entries) AndAlso entries.ValueKind = JsonValueKind.Array Then
                            For Each entry In entries.EnumerateArray()
                                Dim attributes As JsonElement
                                If Not entry.TryGetProperty("attributes", attributes) Then Continue For
                                Dim fileId = TextOf(attributes, "fileId")
                                Dim path = TextOf(attributes, "path")
                                If fileId.Length = 0 Then Continue For
                                Dim name = TextOf(entry, "title")
                                If name.Length = 0 Then name = IO.Path.GetFileName(path)
                                result.Add(New NextcloudSearchHit With {
                                    .FileId = fileId, .FileName = name, .PathInTree = path})
                                If result.Count >= maxHits Then Exit For
                            Next
                        End If
                        Dim paginated As JsonElement
                        hasMore = data.TryGetProperty("isPaginated", paginated) AndAlso
                                  paginated.ValueKind = JsonValueKind.True
                        nextCursor = TextOf(data, "cursor")
                    End Using
                    If result.Count >= maxHits Then Exit For
                    If Not hasMore OrElse String.IsNullOrEmpty(nextCursor) OrElse String.Equals(nextCursor, cursor, StringComparison.Ordinal) Then Exit For
                    cursor = nextCursor
                Next
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
            End Try
            Return result
        End Function

        ''' <summary>Ein Feld als Text, gleich ob der Server es als Zeichenkette oder als Zahl
        ''' schickt - die Dateikennung kommt hier als Text, an anderen Endpunkten als Zahl.</summary>
        Private Shared Function TextOf(element As JsonElement, name As String) As String
            Dim wert As JsonElement
            If Not element.TryGetProperty(name, wert) Then Return ""
            Select Case wert.ValueKind
                Case JsonValueKind.String : Return If(wert.GetString(), "")
                Case JsonValueKind.Number : Return wert.ToString()
                Case Else : Return ""
            End Select
        End Function

        ' ── Papierkorb ──────────────────────────────────────────────────────────
        '
        ' Geloescht wird bei uns immer in den Papierkorb (siehe DeleteFileAsync), und ohne Ansicht
        ' waere der Rueckweg allein die Weboberflaeche des Servers. Der Papierkorb ist ein EIGENER
        ' WebDAV-Baum, nicht Teil des Dateibaums:
        '
        '   PROPFIND /remote.php/dav/trashbin/{benutzer}/trash/    auflisten
        '   MOVE     Eintrag -> /remote.php/dav/trashbin/{benutzer}/restore/{name}    wiederherstellen
        '
        ' Der Eintrag heisst dort NICHT wie die Datei, sondern "Name.dZeitstempel" - der urspruengliche
        ' Name steht in einer eigenen Eigenschaft. Wer den Eintragsnamen anzeigt, zeigt dem Nutzer
        ' Bild.jpg.d1754812345.

        ''' <summary>Ein Eintrag im Papierkorb.</summary>
        Public Class NextcloudTrashEntry
            ''' <summary>Vollstaendige Adresse des Eintrags im Papierkorb-Baum. Sie ist die Identitaet:
            ''' das Wiederherstellen und das Holen der Datei laufen ueber sie.</summary>
            Public Property Url As String = ""
            Public Property FileId As String = ""
            ''' <summary>Der urspruengliche Dateiname, ohne den angehaengten Zeitstempel.</summary>
            Public Property DisplayName As String = ""
            ''' <summary>Wo die Datei vor dem Loeschen lag.</summary>
            Public Property OriginalLocation As String = ""
            ''' <summary>Zeitpunkt des Loeschens in Sekunden seit 1970.</summary>
            Public Property DeletedEpoch As Long
            Public Property ContentType As String = ""
            Public Property Size As Long
        End Class

        Private Shared Function TrashUrl(relativePath As String) As String
            Dim s = AppSettingsService.Load()
            Return NormalizeServerUrl(s.NextcloudServerUrl) & "/remote.php/dav/trashbin/" &
                   Uri.EscapeDataString(If(s.NextcloudUserName, "").Trim()) & "/" & If(relativePath, "").TrimStart("/"c)
        End Function

        ''' <summary>Macht aus der Adresse einer Serverantwort (sie kommt als Pfad, nicht als volle
        ''' Adresse) wieder eine abrufbare Adresse. Der Pfad enthaelt den Unterpfad einer Installation
        ''' bereits, angehaengt wird deshalb nur Schema und Host.</summary>
        Private Shared Function AbsoluteFromHref(href As String) As String
            Dim path = If(href, "").Trim()
            If path.Length = 0 Then Return ""
            If path.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then Return path
            Try
                Dim baseUri = New Uri(NormalizeServerUrl(AppSettingsService.Load().NextcloudServerUrl))
                Return baseUri.GetLeftPart(UriPartial.Authority) & "/" & path.TrimStart("/"c)
            Catch
                Return ""
            End Try
        End Function

        ''' <summary>Alles, was im Papierkorb liegt - Bilder wie anderes. Gefiltert wird beim
        ''' Aufrufer: was als Kachel taugt, entscheidet die Endungsliste der Galerie und nicht
        ''' dieser Dienst.</summary>
        Public Shared Async Function GetTrashAsync(Optional cancellationToken As CancellationToken = Nothing) As Task(Of List(Of NextcloudTrashEntry))
            Dim result = New List(Of NextcloudTrashEntry)()
            LastError = ""
            If Not IsConfigured Then
                LastError = LocalizationService.T("Nextcloud ist nicht eingerichtet")
                Return result
            End If
            Try
                Dim rumpf = "<?xml version=""1.0""?>" &
                            "<d:propfind xmlns:d=""DAV:"" xmlns:oc=""http://owncloud.org/ns"" xmlns:nc=""http://nextcloud.org/ns"">" &
                            "<d:prop><oc:fileid/><d:getcontenttype/><d:getcontentlength/><d:resourcetype/>" &
                            "<nc:trashbin-filename/><nc:trashbin-original-location/><nc:trashbin-deletion-time/>" &
                            "</d:prop></d:propfind>"
                Using request = New HttpRequestMessage(New HttpMethod("PROPFIND"), TrashUrl("trash/"))
                    request.Headers.Add("Depth", "1")
                    request.Content = New StringContent(rumpf, Encoding.UTF8, "application/xml")
                    Dim response = Await GetClient().SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If Not response.IsSuccessStatusCode Then
                        Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                        Return result
                    End If
                    Dim body = Await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    For Each eintrag In XDocument.Parse(body).Descendants(DavNs + "response")
                        Dim href = eintrag.Elements(DavNs + "href").Select(Function(e) e.Value).FirstOrDefault()
                        If String.IsNullOrWhiteSpace(href) Then Continue For
                        ' Der erste Eintrag einer Mehrfachantwort ist der Papierkorb SELBST, und
                        ' Ordner darin sind ganze geloeschte Verzeichnisse. Beides ist keine Aufnahme.
                        If eintrag.Descendants(DavNs + "collection").Any() Then Continue For
                        Dim name = eintrag.Descendants(NcNs + "trashbin-filename").Select(Function(e) e.Value).FirstOrDefault()
                        If String.IsNullOrWhiteSpace(name) Then
                            ' Ohne die Eigenschaft bleibt der Eintragsname - dann steht der
                            ' Zeitstempel im Namen, was immer noch besser ist als eine leere Zeile.
                            name = Uri.UnescapeDataString(href.TrimEnd("/"c).Substring(href.TrimEnd("/"c).LastIndexOf("/"c) + 1))
                        End If
                        Dim deletedEpoch As Long = 0
                        Long.TryParse(If(eintrag.Descendants(NcNs + "trashbin-deletion-time").Select(Function(e) e.Value).FirstOrDefault(), ""),
                                      Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture, deletedEpoch)
                        Dim size As Long = 0
                        Long.TryParse(If(eintrag.Descendants(DavNs + "getcontentlength").Select(Function(e) e.Value).FirstOrDefault(), ""),
                                      Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture, size)
                        result.Add(New NextcloudTrashEntry With {
                            .Url = AbsoluteFromHref(href),
                            .FileId = If(eintrag.Descendants(OcNs + "fileid").Select(Function(e) e.Value).FirstOrDefault(), ""),
                            .DisplayName = name,
                            .OriginalLocation = If(eintrag.Descendants(NcNs + "trashbin-original-location").Select(Function(e) e.Value).FirstOrDefault(), ""),
                            .DeletedEpoch = deletedEpoch,
                            .ContentType = If(eintrag.Descendants(DavNs + "getcontenttype").Select(Function(e) e.Value).FirstOrDefault(), ""),
                            .Size = size})
                    Next
                End Using
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
            End Try
            Return result
        End Function

        ''' <summary>Holt einen Eintrag aus dem Papierkorb zurueck. Der Server legt ihn dorthin, wo er
        ''' herkam - ein Zielpfad wird nicht angegeben und waere auch nicht vorgesehen.</summary>
        Public Shared Function RestoreFromTrashAsync(entryUrl As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(entryUrl) Then Return Task.FromResult(False)
            Dim name = entryUrl.TrimEnd("/"c)
            name = name.Substring(name.LastIndexOf("/"c) + 1)
            Return SendDavAsync("MOVE", entryUrl, TrashUrl("restore/" & name), cancellationToken)
        End Function

        ''' <summary>Leert den Papierkorb VOLLSTAENDIG. Ein DELETE auf die Sammlung selbst, nicht auf
        ''' die Eintraege einzeln - der Server raeumt sie in einem Schritt weg.
        '''
        ''' Danach ist nichts mehr wiederherstellbar, auch nicht das, was jemand vor Wochen geloescht
        ''' hat. Der Aufrufer fragt deshalb vorher nach.</summary>
        Public Shared Function EmptyTrashAsync(Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            Return SendDavAsync("DELETE", TrashUrl("trash"), cancellationToken:=cancellationToken)
        End Function

        ''' <summary>Loescht einen Eintrag ENDGUELTIG aus dem Papierkorb.
        '''
        ''' Die Anwendung bietet das NICHT an: der Papierkorb kennt dort genau eine Richtung, zurueck.
        ''' Gebraucht wird der Weg vom Pruefstand, der seine Probedateien wieder wegraeumen muss -
        ''' eine liegengebliebene Probe im Bestand des Nutzers waere Dreck.</summary>
        Public Shared Function DeleteFromTrashAsync(entryUrl As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(entryUrl) Then Return Task.FromResult(False)
            Return SendDavAsync("DELETE", entryUrl, cancellationToken:=cancellationToken)
        End Function

        ''' <summary>Vorschaubild eines geloeschten Fotos. Memories kennt es nicht mehr - die
        ''' Zeitachse enthaelt es ja nicht -, wohl aber die Papierkorb-App des Kerns. Ohne diesen
        ''' eigenen Weg bliebe die Papierkorbansicht eine Liste grauer Kacheln.</summary>
        Public Shared Async Function GetTrashPreviewBytesAsync(fileId As String, size As Integer,
                                                               Optional cancellationToken As CancellationToken = Nothing) As Task(Of Byte())
            LastError = ""
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(fileId) Then Return Nothing
            Try
                Dim url = NormalizeServerUrl(AppSettingsService.Load().NextcloudServerUrl) &
                          $"/index.php/apps/files_trashbin/preview?fileId={Uri.EscapeDataString(fileId)}&x={size}&y={size}"
                Dim response = Await GetClient().GetAsync(url, cancellationToken).ConfigureAwait(False)
                If Not response.IsSuccessStatusCode Then
                    Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                    Return Nothing
                End If
                Return Await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Throw
            Catch ex As Exception
                LastError = ex.Message
                Return Nothing
            End Try
        End Function

        ''' <summary>Vorschaubild eines geloeschten Fotos als fertiges Bitmap.</summary>
        Public Shared Async Function LoadTrashThumbnailBitmapAsync(fileId As String, size As Integer,
                                                                   Optional cancellationToken As CancellationToken = Nothing) As Task(Of Avalonia.Media.Imaging.Bitmap)
            Dim bytes = Await GetTrashPreviewBytesAsync(fileId, size, cancellationToken).ConfigureAwait(False)
            If bytes Is Nothing OrElse bytes.Length = 0 Then Return Nothing
            Try
                Using stream = New MemoryStream(bytes)
                    Return New Avalonia.Media.Imaging.Bitmap(stream)
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Holt ein geloeschtes Original in eine Temp-Datei. Der Weg ueber
        ''' <c>api/stream</c> gibt es dafuer nicht - Memories kennt die Datei nicht mehr -, wohl aber
        ''' ein gewoehnliches GET auf den Eintrag im Papierkorb.</summary>
        Public Shared Async Function DownloadTrashOriginalToTempAsync(entryUrl As String, fileName As String,
                                                                      Optional cancellationToken As CancellationToken = Nothing) As Task(Of String)
            LastError = ""
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(entryUrl) Then Return Nothing
            Try
                Dim response = Await GetTransferClient().GetAsync(entryUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(False)
                If Not response.IsSuccessStatusCode Then
                    Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                    Return Nothing
                End If
                Dim folder = Path.Combine(Path.GetTempPath(), TempFolderName)
                Directory.CreateDirectory(folder)
                Dim safeName = If(String.IsNullOrWhiteSpace(fileName), "papierkorb", fileName)
                For Each bad In Path.GetInvalidFileNameChars()
                    safeName = safeName.Replace(bad, "_"c)
                Next
                Dim target = Path.Combine(folder, TrashCopyPrefix & "_" & safeName)
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

        ' ── Alben: anlegen, umbenennen, loeschen, bestuecken ────────────────────
        '
        ' NICHT ueber Memories, sondern ueber die Photos-App. Die Alben liegen als WebDAV-Sammlungen
        ' unter /remote.php/dav/photos/{benutzer}/albums/; Memories liest sie nur mit. Alles hier ist
        ' am Server gemessen (2026-08-10, Nextcloud 34.0.2):
        '
        '   anlegen    MKCOL auf die Sammlung                      -> 201
        '   zuweisen   COPY der DATEI in die Sammlung              -> 201, Eintrag "{fileid}-{name}"
        '   loesen     DELETE des Eintrags IN der Sammlung         -> 204, Originaldatei unberuehrt
        '   loeschen   DELETE der Sammlung                         -> 204
        '
        ' ENTSCHEIDEND und deshalb gemessen statt angenommen: das COPY legt einen VERWEIS an, keine
        ' Kopie. Nach dem Zuweisen stand die Datei weiterhin genau einmal im Dateibaum, und der
        ' belegte Speicher war vor und nach dem Versuch auf das Byte gleich. Ein falsch geratener
        ' Aufruf haette hier den Bestand des Nutzers vervielfacht.

        ''' <summary>Der DAV-Pfad einer Albensammlung. Die Kennung eines Albums kommt als
        ''' "benutzer/name"; fuer den Pfad zaehlt der NAME, der Benutzer steckt schon in der Wurzel.
        ''' Fremde (geteilte) Alben liegen woanders und werden hier bewusst nicht angefasst.</summary>
        Private Shared Function AlbumUrl(albumIdOrName As String, Optional entry As String = "") As String
            Dim s = AppSettingsService.Load()
            Dim name = If(albumIdOrName, "")
            Dim slash = name.LastIndexOf("/"c)
            If slash >= 0 Then name = name.Substring(slash + 1)
            Dim url = NormalizeServerUrl(s.NextcloudServerUrl) & "/remote.php/dav/photos/" &
                      Uri.EscapeDataString(If(s.NextcloudUserName, "").Trim()) & "/albums/" & Uri.EscapeDataString(name) & "/"
            If Not String.IsNullOrEmpty(entry) Then url &= Uri.EscapeDataString(entry)
            Return url
        End Function

        Private Shared Async Function SendDavAsync(method As String, url As String,
                                                   Optional destination As String = Nothing,
                                                   Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            LastError = ""
            If Not IsConfigured Then
                LastError = LocalizationService.T("Nextcloud ist nicht eingerichtet")
                Return False
            End If
            Try
                Using request = New HttpRequestMessage(New HttpMethod(method), url)
                    If Not String.IsNullOrEmpty(destination) Then request.Headers.Add("Destination", destination)
                    Dim response = Await GetClient().SendAsync(request, cancellationToken).ConfigureAwait(False)
                    If response.IsSuccessStatusCode Then
                        ' Hier laufen ALLE schreibenden Wege durch (loeschen, verschieben, Album
                        ' bestuecken). Der Rueckfall haelt die Zeitachse kurz fest, damit der Abruf
                        ' je Tag nicht jedes Mal sucht - nach einer Aenderung waere sie von gestern.
                        DropTimelineCache()
                        Return True
                    End If
                    Await SetErrorFromResponse(response, cancellationToken).ConfigureAwait(False)
                    Return False
                End Using
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
                Return False
            End Try
        End Function

        ''' <summary>Legt ein Album an. Ein schon vorhandener Name schlaegt fehl (405).</summary>
        Public Shared Function CreateAlbumAsync(name As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(name) Then Return Task.FromResult(False)
            Return SendDavAsync("MKCOL", AlbumUrl(name.Trim()), cancellationToken:=cancellationToken)
        End Function

        Public Shared Function RenameAlbumAsync(albumId As String, newName As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(albumId) OrElse String.IsNullOrWhiteSpace(newName) Then Return Task.FromResult(False)
            Return SendDavAsync("MOVE", AlbumUrl(albumId), AlbumUrl(newName.Trim()), cancellationToken)
        End Function

        ''' <summary>Loescht das Album. Die Fotos bleiben, es verschwinden nur die Verweise.</summary>
        Public Shared Function DeleteAlbumAsync(albumId As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(albumId) Then Return Task.FromResult(False)
            Return SendDavAsync("DELETE", AlbumUrl(albumId), cancellationToken:=cancellationToken)
        End Function

        ''' <summary>Haengt ein Foto in ein Album. <paramref name="filePathInTree"/> ist der Pfad im
        ''' Dateibaum (Feld filename der Einzelheiten), nicht der Anzeigename.</summary>
        Public Shared Function AddToAlbumAsync(albumId As String, filePathInTree As String,
                                               Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(albumId) OrElse String.IsNullOrWhiteSpace(filePathInTree) Then Return Task.FromResult(False)
            Dim s = AppSettingsService.Load()
            Dim sourceUrl = NormalizeServerUrl(s.NextcloudServerUrl) & "/remote.php/dav/files/" &
                            Uri.EscapeDataString(If(s.NextcloudUserName, "").Trim()) &
                            String.Join("/", filePathInTree.Split("/"c).Select(Function(t) Uri.EscapeDataString(t)))
            Return SendDavAsync("COPY", sourceUrl, AlbumUrl(albumId, IO.Path.GetFileName(filePathInTree)), cancellationToken)
        End Function

        ''' <summary>Loest die Zuweisung. Der Eintrag im Album heisst "{fileid}-{name}" - das ist die
        ''' Schreibweise der Photos-App und nicht der blosse Dateiname.</summary>
        Public Shared Function RemoveFromAlbumAsync(albumId As String, fileId As String, fileName As String,
                                                    Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(albumId) OrElse String.IsNullOrWhiteSpace(fileId) Then Return Task.FromResult(False)
            Return SendDavAsync("DELETE", AlbumUrl(albumId, fileId & "-" & IO.Path.GetFileName(If(fileName, ""))),
                                cancellationToken:=cancellationToken)
        End Function

        ''' <summary>Einzelheiten zu einer Aufnahme: Groesse, Pfad im Dateibaum, Rechte, Stichwoerter.
        '''
        ''' Der EXIF-Block dieses Servers ist DUENN (gemessen kamen nur DateTimeEpoch und
        ''' Megapixels), weil Memories dafuer ein eingerichtetes exiftool braucht. Kamera, ISO und
        ''' Blende kommen deshalb NICHT von hier, sondern werden wie bei Immich aus der geholten
        ''' Originaldatei gelesen - das ist unabhaengig davon, wie gut der Server indiziert ist.</summary>
        Public Shared Async Function GetInfoAsync(fileId As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of NextcloudPhoto)
            If String.IsNullOrWhiteSpace(fileId) Then Return Nothing
            If Not IsConfigured Then
                LastError = LocalizationService.T("Nextcloud ist nicht eingerichtet")
                Return Nothing
            End If
            If Await EnsureModeAsync(cancellationToken).ConfigureAwait(False) = ServerMode.Photos Then
                Return Await FallbackInfoAsync(fileId, cancellationToken).ConfigureAwait(False)
            End If
            Return Await GetJsonAsync(Of NextcloudPhoto)("image/info/" & Uri.EscapeDataString(fileId) & "?tags=1",
                                                          cancellationToken).ConfigureAwait(False)
        End Function

        ''' <summary>Dieselben Einzelheiten, aber ZUERST aus dem lokalen Index (siehe
        ''' <see cref="NextcloudIndexService"/>). Das Gegenstueck zu
        ''' <c>ImmichService.GetAssetDetailCachedAsync</c>.
        '''
        ''' Ohne ihn holte jede Sitzung dieselben Angaben erneut, je Aufnahme eine Anfrage. Der Etag
        ''' entscheidet: passt er, gilt der gespeicherte Stand; sonst wird geholt und ueberschrieben.
        ''' OHNE Etag geht der Weg direkt zum Server - ein Eintrag, den nichts veralten laesst, waere
        ''' schlimmer als gar keiner.</summary>
        Public Shared Async Function GetInfoCachedAsync(fileId As String, etag As String,
                                                        Optional cancellationToken As CancellationToken = Nothing) As Task(Of NextcloudPhoto)
            If String.IsNullOrWhiteSpace(fileId) Then Return Nothing
            Dim key = ServerKey
            If Not String.IsNullOrEmpty(etag) Then
                Dim cached = NextcloudIndexService.Instance.TryGet(key, fileId, etag)
                If cached IsNot Nothing Then Return cached
            End If

            Dim info = Await GetInfoAsync(fileId, cancellationToken).ConfigureAwait(False)
            If info IsNot Nothing AndAlso Not String.IsNullOrEmpty(etag) Then
                NextcloudIndexService.Instance.Put(key, fileId, etag, info)
            End If
            Return info
        End Function

        ' ── Plattenzwischenspeicher der Vorschaubilder ──────────────────────────

        Private Shared ReadOnly CacheRoot As String =
            IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix", "NextcloudCache")

        ''' <summary>Kennung des Servers samt Benutzer. Getrennt abgelegt, damit ein Server- oder
        ''' Kontowechsel keine fremden Kacheln zeigt.</summary>
        Public Shared ReadOnly Property ServerKey As String
            Get
                Dim s = AppSettingsService.Load()
                Dim roh = NormalizeServerUrl(s.NextcloudServerUrl) & "|" & If(s.NextcloudUserName, "")
                ' Voll qualifiziert: "Imports System.Net" macht aus Security sonst System.Net.Security.
                Using sha = System.Security.Cryptography.SHA1.Create()
                    Dim hash = sha.ComputeHash(Encoding.UTF8.GetBytes(roh))
                    Return BitConverter.ToString(hash).Replace("-", "").Substring(0, 12).ToLowerInvariant()
                End Using
            End Get
        End Property

        ''' <summary>Ablage einer Kachel. Der ETAG steckt im Namen: aendert sich das Foto auf dem
        ''' Server, aendert sich sein Etag, und die alte Kachel wird nicht mehr gefunden. Damit
        ''' braucht es keine Gueltigkeitsdauer und keinen Abgleich - eine veraltete Kachel kann gar
        ''' nicht erst gezeigt werden.</summary>
        Private Shared Function CacheFilePath(fileId As String, etag As String, size As Integer) As String
            Dim stamm = New StringBuilder()
            For Each ch In fileId & "_" & If(etag, "")
                stamm.Append(If(Char.IsLetterOrDigit(ch), ch, "_"c))
            Next
            Return IO.Path.Combine(CacheRoot, ServerKey, $"{stamm}_{size}.img")
        End Function

        ''' <summary>Raeumt aeltere Staende DESSELBEN Fotos weg. Ohne das saemmelten sich bei jeder
        ''' Aenderung neue Dateien an, und niemand raeumte die alten je ab.</summary>
        Private Shared Sub DropOlderCacheEntries(folder As String, fileId As String, keep As String)
            Try
                If Not Directory.Exists(folder) Then Return
                For Each cached In Directory.EnumerateFiles(folder, fileId & "_*.img")
                    If Not String.Equals(cached, keep, StringComparison.Ordinal) Then File.Delete(cached)
                Next
            Catch
            End Try
        End Sub

        ''' <summary>Leert den Zwischenspeicher dieses Servers und liefert die Anzahl der entfernten
        ''' Dateien.</summary>
        Public Shared Function ClearCache() As Integer
            Try
                Dim folder = IO.Path.Combine(CacheRoot, ServerKey)
                If Not Directory.Exists(folder) Then Return 0
                Dim n = 0
                For Each cached In Directory.EnumerateFiles(folder, "*.img").ToList()
                    Try
                        File.Delete(cached)
                        n += 1
                    Catch
                    End Try
                Next
                Return n
            Catch
                Return 0
            End Try
        End Function

        ' "Cache leeren" laesst den Metadaten-Index BEWUSST stehen - dieselbe Entscheidung wie bei
        ' Immich: er ist teuer neu aufzubauen und ueber den Etag selbst-invalidierend. Ein
        ' Serverwechsel braucht kein Loeschen, weil der Serverschluessel Teil des Primaerschluessels
        ' ist und fremde Eintraege damit nie gelesen werden.

        ''' <summary>Kantenlaenge der Kachelvorschau. Memories rechnet die Vorschau selbst zurecht,
        ''' angefragt wird also gleich die Groesse, die die Kachel braucht.</summary>
        Public Const ThumbnailSize As Integer = 512

        ''' <summary>Vorschaubild als fertiges Bitmap fuer die Kachel. Liefert Nothing, wenn der
        ''' Server nicht antwortet - der Aufrufer wertet das als "fertig geladen, kein Bild" und
        ''' fragt nicht in einer Schleife nach.
        '''
        ''' NOCH OHNE PLATTENZWISCHENSPEICHER, anders als der Immich-Weg: der haengt dort an einem
        ''' eigenen Cache-Verzeichnis samt Aufraeumen. Solange die Anbindung im Aufbau ist, waere
        ''' ein zweiter Cache mit eigener Alterung mehr Risiko als Nutzen; Nextcloud liefert die
        ''' Vorschau ohnehin aus seinem eigenen vorgerechneten Bestand.</summary>
        Public Shared Async Function LoadThumbnailBitmapAsync(fileId As String, size As Integer,
                                                              Optional cancellationToken As CancellationToken = Nothing,
                                                              Optional etag As String = Nothing) As Task(Of Avalonia.Media.Imaging.Bitmap)
            Dim bytes = Await GetPreviewBytesAsync(fileId, size, cancellationToken, etag).ConfigureAwait(False)
            If bytes Is Nothing OrElse bytes.Length = 0 Then Return Nothing
            Try
                Using stream = New MemoryStream(bytes)
                    Return New Avalonia.Media.Imaging.Bitmap(stream)
                End Using
            Catch
                ' Kein Bild im Rumpf (etwa eine Fehlerseite) - wie ein fehlendes Vorschaubild
                ' behandeln, nicht als Absturz.
                Return Nothing
            End Try
        End Function

        ''' <summary>Holt das Original in eine Temp-Datei und liefert deren Pfad. Der Dateiname
        ''' behaelt seine Endung, sonst erkennt die Pipeline das Format nicht.</summary>
        Public Shared Async Function DownloadOriginalToTempAsync(fileId As String, fileName As String,
                                                                 Optional cancellationToken As CancellationToken = Nothing) As Task(Of String)
            LastError = ""
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(fileId) Then Return Nothing
            ' OHNE MEMORIES gibt es api/stream nicht - dann fuehrt der Pfad im Dateibaum zum
            ' Original, und der Weg dorthin ist ein gewoehnliches GET.
            If Await EnsureModeAsync(cancellationToken).ConfigureAwait(False) = ServerMode.Photos Then
                Return Await FallbackDownloadOriginalAsync(fileId, fileName, cancellationToken).ConfigureAwait(False)
            End If
            Try
                ' Ueber den Uebertragungs-Client: ein RAW oder ein Video ist in einer halben
                ' Minute nicht geholt, und der Abbruch kam bisher mitten im Laden.
                Dim response = Await GetTransferClient().GetAsync(ApiUrl("stream/" & Uri.EscapeDataString(fileId)),
                                                          HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(False)
                If Not response.IsSuccessStatusCode Then
                    LastError = String.Format(LocalizationService.T("Nextcloud antwortet mit {0}"), CInt(response.StatusCode))
                    Return Nothing
                End If
                Dim folder = Path.Combine(Path.GetTempPath(), TempFolderName)
                Directory.CreateDirectory(folder)
                Dim safeName = If(String.IsNullOrWhiteSpace(fileName), fileId, fileName)
                For Each bad In Path.GetInvalidFileNameChars()
                    safeName = safeName.Replace(bad, "_"c)
                Next
                Dim target = Path.Combine(folder, fileId & "_" & safeName)
                Using stream = Await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(False)
                    Using file = New FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None)
                        Await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(False)
                    End Using
                End Using
                Return target
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Nextcloud", ex)
                Return Nothing
            End Try
        End Function

    End Class

End Namespace
