Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports System.Threading
Imports System.Threading.Tasks
Imports Avalonia.Media.Imaging

Namespace Services

    ''' <summary>Ein Immich-Album, wie es der Baum/die Galerie anzeigt.</summary>
    Public Class ImmichAlbum
        Public Property Id As String = ""
        Public Property Name As String = ""
        Public Property AssetCount As Integer
        Public Property ThumbnailAssetId As String = ""
    End Class

    ''' <summary>Ein einzelnes Immich-Asset (Bild/Video) mit den Feldern, die Galerie/Info-Leiste
    ''' ohne zusätzlichen Netzabruf anzeigen können. Bewusst schlank gehalten - Details holt der
    ''' Viewer bei Bedarf nach.</summary>
    Public Class ImmichAsset
        Public Property Id As String = ""
        Public Property FileName As String = ""
        Public Property IsVideo As Boolean
        Public Property FileCreatedAt As DateTime?
        Public Property ExifDateTaken As DateTime?
        Public Property Width As Integer
        Public Property Height As Integer
        Public Property Camera As String = ""
        Public Property Description As String = ""
        Public Property Iso As Integer?
        Public Property Aperture As Double?
        Public Property IsFavorite As Boolean
        Public Property FileSizeBytes As Long
        ''' Immichs Änderungszeitstempel - Invalidierungsschlüssel für den lokalen Metadaten-Index.
        Public Property UpdatedAt As String = ""
        ''' 0 = unbewertet (FerrumPix-Konvention); Immich liefert 1-5 bzw. null/-1 für unbewertet.
        Public Property Rating As Integer
        ''' Flache Stichwörter (Immich-Tag-Pfade, z.B. "Reise/Italien").
        Public Property Tags As New List(Of String)()
    End Class

    ''' <summary>Eine benannte Person aus der serverseitigen Gesichtserkennung von Immich.</summary>
    Public Class ImmichPerson
        Public Property Id As String = ""
        Public Property Name As String = ""
    End Class

    ''' <summary>
    ''' Ein Ergebnis eines Verbindungstests: <see cref="Ok"/> plus eine anzeigbare Meldung
    ''' (Benutzername bei Erfolg, Fehlertext sonst).
    ''' </summary>
    Public Structure ImmichConnectionResult
        Public Property Ok As Boolean
        Public Property Message As String
    End Structure

    ''' <summary>
    ''' Gekapselter Zugriff auf einen self-hosted Immich-Server. Einziger Ort in FerrumPix, der die
    ''' Immich-REST-API kennt (HTTP, Auth, Endpunkte, JSON-DTOs) - siehe die bewusste Entscheidung,
    ''' Immich direkt statt über eine allgemeine Quellen-Abstraktion anzubinden. Alles ist async und
    ''' abbruchbar; Fehler werden geschluckt (und optional protokolliert) statt geworfen, damit ein
    ''' unerreichbarer Server nie die UI blockiert.
    '''
    ''' WICHTIG (Catch-Filter "When cancellationToken.IsCancellationRequested"): Nur ein echter
    ''' Abbruch durch den AUFRUFER wird weitergeworfen. Ein HttpClient-TIMEOUT ist ebenfalls eine
    ''' OperationCanceledException - ungefiltert flog sie durch Async-Sub-Aufrufer (z.B. das
    ''' Blaettern im Viewer) und riss die ganze App ab. Timeouts landen deshalb im normalen
    ''' Fehlerpfad (Log + Nothing/False).
    '''
    ''' Thumbnails werden lokal auf Platte zwischengespeichert (eigener Cache-Zweig, unabhängig vom
    ''' dateipfad-basierten <see cref="ThumbnailCacheService"/>), damit erneutes Scrollen keine
    ''' weiteren Netzabrufe auslöst.
    ''' </summary>
    Public NotInheritable Class ImmichService
        Private Sub New()
        End Sub

        Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {
            .PropertyNameCaseInsensitive = True,
            .NumberHandling = JsonNumberHandling.AllowReadingFromString
        }

        ' Immich liefert Thumbnails in zwei Größen: "thumbnail" (klein, für die Galerie-Kacheln) und
        ' "preview" (größer, für Viewer/Filmstreifen). Beide werden getrennt gecacht.
        Public Const ThumbnailSize As String = "thumbnail"
        Public Const PreviewSize As String = "preview"

        Private Shared ReadOnly CacheRoot As String =
            IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix", "ImmichCache")

        ''' Ordner, in den Originale für Viewer/Editor heruntergeladen werden.
        Private Shared ReadOnly ImmichTempDir As String =
            IO.Path.Combine(IO.Path.GetTempPath(), "FerrumPix", "Immich")

        ''' <summary>True, wenn der Pfad eine aus Immich heruntergeladene Temp-Kopie ist. Der Editor
        ''' erzwingt dafür „Speichern unter" - unabhängig davon, ob das Bild aus Galerie oder Viewer
        ''' geöffnet wurde.</summary>
        Public Shared Function IsImmichTempPath(path As String) As Boolean
            If String.IsNullOrEmpty(path) Then Return False
            Try
                Return IO.Path.GetFullPath(path).StartsWith(IO.Path.GetFullPath(ImmichTempDir), StringComparison.OrdinalIgnoreCase)
            Catch
                Return False
            End Try
        End Function

        ''' <summary>True für einen Immich-Pseudo-Pfad (immich://{assetId}/{name}).</summary>
        Public Shared Function IsImmichPseudoPath(path As String) As Boolean
            Return Not String.IsNullOrEmpty(path) AndAlso path.StartsWith("immich://", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Baut den Immich-Pseudo-Pfad (immich://{assetId}/{name}) - MUSS mit
        ''' ImageItem.CreateImmichItem identisch bleiben, denn lokale Metadaten (Farbetikett)
        ''' werden in der Bibliotheks-DB unter genau diesem Pfad abgelegt.</summary>
        Public Shared Function MakePseudoPath(assetId As String, fileName As String) As String
            Dim displayName = If(String.IsNullOrEmpty(fileName), assetId, fileName)
            Return "immich://" & assetId & "/" & displayName
        End Function

        ''' <summary>Zerlegt einen Immich-Pseudo-Pfad in Asset-ID und Original-Dateinamen.</summary>
        Public Shared Function TryParsePseudoPath(path As String, ByRef assetId As String, ByRef fileName As String) As Boolean
            assetId = Nothing : fileName = Nothing
            If Not IsImmichPseudoPath(path) Then Return False
            Dim rest = path.Substring("immich://".Length)
            Dim slash = rest.IndexOf("/"c)
            If slash <= 0 Then
                assetId = rest
                fileName = rest
            Else
                assetId = rest.Substring(0, slash)
                fileName = rest.Substring(slash + 1)
            End If
            Return Not String.IsNullOrEmpty(assetId)
        End Function

        ' Ein einziger HttpClient wird wiederverwendet und nur neu gebaut, wenn sich Server-URL oder
        ' API-Key ändern (Socket-Erschöpfung vermeiden - siehe HttpClient-Guidance).
        Private Shared ReadOnly _clientLock As New Object()
        Private Shared _client As HttpClient
        Private Shared _clientSignature As String = Nothing

        ''' <summary>Letzte Fehlermeldung eines Album-/Asset-Abrufs (für die Statusanzeige, damit ein
        ''' leeres Ergebnis nicht stumm als „0 Bilder" erscheint). Nothing bei Erfolg.</summary>
        Public Shared Property LastError As String

        Public Shared ReadOnly Property IsConfigured As Boolean
            Get
                Dim s = AppSettingsService.Load()
                Return s.ImmichEnabled AndAlso
                       Not String.IsNullOrWhiteSpace(s.ImmichServerUrl) AndAlso
                       Not String.IsNullOrWhiteSpace(s.ImmichApiKey)
            End Get
        End Property

        ''' <summary>Normalisiert die vom Benutzer eingegebene Server-URL auf die reine Basis ohne
        ''' abschließenden Schrägstrich und ohne angehängtes "/api" - beides wird beim Bau der
        ''' Endpunkt-URLs selbst ergänzt.</summary>
        Public Shared Function NormalizeServerUrl(url As String) As String
            If String.IsNullOrWhiteSpace(url) Then Return ""
            Dim trimmed = url.Trim().TrimEnd("/"c)
            If trimmed.EndsWith("/api", StringComparison.OrdinalIgnoreCase) Then
                trimmed = trimmed.Substring(0, trimmed.Length - 4).TrimEnd("/"c)
            End If
            Return trimmed
        End Function

        Private Shared Function GetClient() As HttpClient
            Dim s = AppSettingsService.Load()
            Dim baseUrl = NormalizeServerUrl(s.ImmichServerUrl)
            Dim apiKey = If(s.ImmichApiKey, "").Trim()
            Dim signature = baseUrl & "|" & apiKey

            SyncLock _clientLock
                If _client IsNot Nothing AndAlso String.Equals(_clientSignature, signature, StringComparison.Ordinal) Then
                    Return _client
                End If

                _client?.Dispose()
                Dim client = New HttpClient With {.Timeout = TimeSpan.FromSeconds(30)}
                If Not String.IsNullOrEmpty(apiKey) Then client.DefaultRequestHeaders.Add("x-api-key", apiKey)
                client.DefaultRequestHeaders.Accept.Add(New MediaTypeWithQualityHeaderValue("application/json"))
                _client = client
                _clientSignature = signature
                Return _client
            End SyncLock
        End Function

        Private Shared Function ApiUrl(pathAndQuery As String) As String
            Dim baseUrl = NormalizeServerUrl(AppSettingsService.Load().ImmichServerUrl)
            Return baseUrl & "/api/" & pathAndQuery.TrimStart("/"c)
        End Function

        ''' <summary>Prüft URL + API-Key gegen /api/users/me. Erfolg heißt: Server erreichbar UND Key
        ''' gültig. Wirft nie - liefert das Ergebnis als <see cref="ImmichConnectionResult"/>.</summary>
        Public Shared Async Function TestConnectionAsync(serverUrl As String, apiKey As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of ImmichConnectionResult)
            Dim baseUrl = NormalizeServerUrl(serverUrl)
            If String.IsNullOrWhiteSpace(baseUrl) Then Return New ImmichConnectionResult With {.Ok = False, .Message = LocalizationService.T("Keine Server-URL angegeben")}
            If String.IsNullOrWhiteSpace(apiKey) Then Return New ImmichConnectionResult With {.Ok = False, .Message = LocalizationService.T("Kein API-Key angegeben")}

            Try
                Using client = New HttpClient With {.Timeout = TimeSpan.FromSeconds(15)}
                    client.DefaultRequestHeaders.Add("x-api-key", apiKey.Trim())
                    Using req = New HttpRequestMessage(HttpMethod.Get, baseUrl & "/api/users/me")
                        Using resp = Await client.SendAsync(req, cancellationToken).ConfigureAwait(False)
                            If Not resp.IsSuccessStatusCode Then
                                Return New ImmichConnectionResult With {.Ok = False, .Message = $"HTTP {CInt(resp.StatusCode)} {resp.ReasonPhrase}"}
                            End If
                            Dim body = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                            Dim me_ = JsonSerializer.Deserialize(Of ImmichUserDto)(body, JsonOptions)
                            Dim who = If(me_?.Name, If(me_?.Email, "OK"))
                            Return New ImmichConnectionResult With {.Ok = True, .Message = LocalizationService.T("Verbunden als") & " " & who}
                        End Using
                    End Using
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.TestConnection", ex)
                Return New ImmichConnectionResult With {.Ok = False, .Message = ex.Message}
            End Try
        End Function

        ''' <summary>Legt ein neues Album an und gibt dessen ID zurück (Nothing bei Fehler).</summary>
        Public Shared Async Function CreateAlbumAsync(albumName As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of String)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(albumName) Then Return Nothing
            Try
                Dim client = GetClient()
                Dim body = "{""albumName"":" & JsonSerializer.Serialize(albumName.Trim()) & "}"
                Using content = New StringContent(body, Encoding.UTF8, "application/json")
                    Using resp = Await client.PostAsync(ApiUrl("albums"), content, cancellationToken).ConfigureAwait(False)
                        Dim respBody = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                        If Not resp.IsSuccessStatusCode Then
                            DiagnosticLogService.LogAlways("Immich.CreateAlbum", $"HTTP {CInt(resp.StatusCode)} {respBody.Substring(0, Math.Min(300, respBody.Length))}")
                            Return Nothing
                        End If
                        Dim dto = JsonSerializer.Deserialize(Of ImmichAlbumDto)(respBody, JsonOptions)
                        Return dto?.Id
                    End Using
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.CreateAlbum", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Benennt ein Album um.</summary>
        Public Shared Async Function RenameAlbumAsync(albumId As String, newName As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(albumId) OrElse String.IsNullOrWhiteSpace(newName) Then Return False
            Try
                Dim client = GetClient()
                Dim body = "{""albumName"":" & JsonSerializer.Serialize(newName.Trim()) & "}"
                Using req = New HttpRequestMessage(New HttpMethod("PATCH"), ApiUrl("albums/" & Uri.EscapeDataString(albumId)))
                    req.Content = New StringContent(body, Encoding.UTF8, "application/json")
                    Using resp = Await client.SendAsync(req, cancellationToken).ConfigureAwait(False)
                        If Not resp.IsSuccessStatusCode Then
                            DiagnosticLogService.LogAlways("Immich.RenameAlbum", $"HTTP {CInt(resp.StatusCode)} album={albumId}")
                        End If
                        Return resp.IsSuccessStatusCode
                    End Using
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.RenameAlbum", ex)
                Return False
            End Try
        End Function

        ''' <summary>Löscht ein Album (DELETE /albums/{id}). Die Fotos darin bleiben in Immich erhalten -
        ''' ein Album ist nur eine Zusammenstellung, kein Ablageort.</summary>
        Public Shared Async Function DeleteAlbumAsync(albumId As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(albumId) Then Return False
            Try
                Dim client = GetClient()
                Using resp = Await client.DeleteAsync(ApiUrl("albums/" & Uri.EscapeDataString(albumId)), cancellationToken).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then
                        Dim err = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                        DiagnosticLogService.LogAlways("Immich.DeleteAlbum", $"HTTP {CInt(resp.StatusCode)} album={albumId} {err.Substring(0, Math.Min(300, err.Length))}")
                    End If
                    Return resp.IsSuccessStatusCode
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.DeleteAlbum", ex)
                Return False
            End Try
        End Function

        ''' <summary>Lädt eine lokale Datei als neues Asset hoch und gibt dessen Asset-ID zurück (auch bei
        ''' „duplicate" - Immich dedupliziert serverseitig per Prüfsumme). Nothing bei Fehler.</summary>
        ''' <param name="fileCreatedAtIso">Aufnahmedatum, das Immich dem Asset geben soll. Beim ERSETZEN eines
        ''' Assets zwingend das des Originals: der Zeitstempel der frisch gerenderten Temp-Datei wäre „jetzt",
        ''' und ohne EXIF-Aufnahmedatum im Bild (z.B. PNG) rutschte das Foto damit in der Zeitleiste auf heute.</param>
        Public Shared Async Function UploadAssetAsync(filePath As String,
                                                      Optional cancellationToken As CancellationToken = Nothing,
                                                      Optional fileCreatedAtIso As String = Nothing) As Task(Of String)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(filePath) OrElse Not File.Exists(filePath) Then Return Nothing
            Try
                Dim client = GetClient()
                Dim info = New FileInfo(filePath)
                Dim createdIso = If(String.IsNullOrWhiteSpace(fileCreatedAtIso),
                                    info.CreationTimeUtc.ToString("o", Globalization.CultureInfo.InvariantCulture),
                                    fileCreatedAtIso)
                Dim modifiedIso = info.LastWriteTimeUtc.ToString("o", Globalization.CultureInfo.InvariantCulture)
                Using form = New MultipartFormDataContent()
                    form.Add(New StringContent(createdIso), "fileCreatedAt")
                    form.Add(New StringContent(modifiedIso), "fileModifiedAt")
                    form.Add(New StringContent(IO.Path.GetFileName(filePath)), "filename")
                    Using fileStream = File.OpenRead(filePath)
                        Dim fileContent = New StreamContent(fileStream)
                        fileContent.Headers.ContentType = New MediaTypeHeaderValue(GuessMimeType(filePath))
                        form.Add(fileContent, "assetData", IO.Path.GetFileName(filePath))
                        Using resp = Await client.PostAsync(ApiUrl("assets"), form, cancellationToken).ConfigureAwait(False)
                            Dim respBody = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                            If Not resp.IsSuccessStatusCode Then
                                DiagnosticLogService.LogAlways("Immich.Upload", $"HTTP {CInt(resp.StatusCode)} {IO.Path.GetFileName(filePath)} {respBody.Substring(0, Math.Min(300, respBody.Length))}")
                                Return Nothing
                            End If
                            Dim dto = JsonSerializer.Deserialize(Of ImmichUploadResponseDto)(respBody, JsonOptions)
                            Return dto?.Id
                        End Using
                    End Using
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.Upload", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Ordnet bereits hochgeladene Assets einem Album zu.</summary>
        Public Shared Async Function AddAssetsToAlbumAsync(albumId As String, assetIds As IEnumerable(Of String), Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(albumId) Then Return False
            Dim ids = If(assetIds, Enumerable.Empty(Of String)()).Where(Function(s) Not String.IsNullOrWhiteSpace(s)).ToList()
            If ids.Count = 0 Then Return False
            Try
                Dim client = GetClient()
                Dim body = "{""ids"":[" & String.Join(",", ids.Select(Function(i) JsonSerializer.Serialize(i))) & "]}"
                Using content = New StringContent(body, Encoding.UTF8, "application/json")
                    Using resp = Await client.PutAsync(ApiUrl("albums/" & Uri.EscapeDataString(albumId) & "/assets"), content, cancellationToken).ConfigureAwait(False)
                        If Not resp.IsSuccessStatusCode Then
                            DiagnosticLogService.LogAlways("Immich.AddToAlbum", $"HTTP {CInt(resp.StatusCode)} album={albumId}")
                        End If
                        Return resp.IsSuccessStatusCode
                    End Using
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.AddToAlbum", ex)
                Return False
            End Try
        End Function

        ''' <summary>Ersetzt das Original eines vorhandenen Assets durch eine lokale Datei und liefert die
        ''' Asset-ID des Ergebnisses (Nothing bei Fehler).
        '''
        ''' Bis Immich v2 erledigt das ein Aufruf (PUT /assets/{id}/original), die Asset-ID bleibt. In v3
        ''' ist dieser Endpunkt entfallen; der von Immich vorgesehene Weg ist: neu hochladen, die
        ''' Verknüpfungen (Alben, Favorit, Stack, geteilte Links, Sidecar) per PUT /assets/copy übernehmen,
        ''' Beschreibung/Bewertung/Stichwörter nachziehen und das alte Asset in den Papierkorb legen. Die
        ''' Asset-ID ändert sich dabei zwangsläufig - Aufrufer müssen mit der zurückgegebenen ID
        ''' weiterarbeiten und dürfen die übergebene nicht weiterverwenden.</summary>
        Public Shared Async Function ReplaceAssetAsync(assetId As String, filePath As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of String)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(assetId) OrElse String.IsNullOrWhiteSpace(filePath) OrElse Not File.Exists(filePath) Then Return Nothing

            ' Unbekannte Version (0) wie eine alte behandeln: der Legacy-Aufruf beantwortet die Frage selbst
            ' - auf einem v3-Server läuft er ins Leere und wir nehmen den Weg darunter.
            Dim major = Await GetServerMajorVersionAsync(cancellationToken).ConfigureAwait(False)
            If major < 3 Then
                If Await ReplaceOriginalLegacyAsync(assetId, filePath, cancellationToken).ConfigureAwait(False) Then
                    InvalidateAssetCaches(assetId)
                    Await RefreshAssetDetailCacheAsync(assetId, "nach ReplaceAsset (bis v2)", cancellationToken).ConfigureAwait(False)
                    Return assetId
                End If
            End If

            ' Den Serverzustand des Originals EINMAL holen: er liefert das Aufnahmedatum für den Upload und
            ' gleich darauf Beschreibung/Bewertung/Stichwörter für das neue Asset.
            Dim source = Await GetAssetDetailRawAsync(assetId, cancellationToken).ConfigureAwait(False)

            Dim newAssetId = Await UploadAssetAsync(filePath, cancellationToken, fileCreatedAtIso:=source?.FileCreatedAt).ConfigureAwait(False)
            If String.IsNullOrEmpty(newAssetId) Then Return Nothing
            ' Immich dedupliziert per Prüfsumme: ist die "neue" Datei byteweise die alte, kommt dieselbe
            ' ID zurück. Dann gibt es nichts zu kopieren und erst recht nichts zu löschen.
            If String.Equals(newAssetId, assetId, StringComparison.Ordinal) Then Return assetId

            Await CopyAssetLinksAsync(assetId, newAssetId, cancellationToken).ConfigureAwait(False)
            Await CopyAssetMetadataAsync(source, newAssetId, cancellationToken).ConfigureAwait(False)
            ' Immer in den Immich-Papierkorb (force=False), nie endgültig: das Original einer Bearbeitung
            ' unwiederbringlich zu löschen wäre eine Falle, die niemand erwartet.
            If Not Await DeleteAssetsAsync({assetId}, force:=False, cancellationToken:=cancellationToken).ConfigureAwait(False) Then
                DiagnosticLogService.LogAlways("Immich.ReplaceAsset", $"Neues Asset {newAssetId} liegt, altes {assetId} ließ sich nicht löschen")
            End If
            Return newAssetId
        End Function

        ''' <summary>Der Ein-Aufruf-Weg bis Immich v2. False, wenn der Server den Endpunkt nicht (mehr) kennt.</summary>
        Private Shared Async Function ReplaceOriginalLegacyAsync(assetId As String, filePath As String, cancellationToken As CancellationToken) As Task(Of Boolean)
            Try
                Dim client = GetClient()
                Dim info = New FileInfo(filePath)
                Using form = New MultipartFormDataContent()
                    form.Add(New StringContent(info.CreationTimeUtc.ToString("o", Globalization.CultureInfo.InvariantCulture)), "fileCreatedAt")
                    form.Add(New StringContent(info.LastWriteTimeUtc.ToString("o", Globalization.CultureInfo.InvariantCulture)), "fileModifiedAt")
                    form.Add(New StringContent(IO.Path.GetFileName(filePath)), "filename")
                    Using fileStream = File.OpenRead(filePath)
                        Dim fileContent = New StreamContent(fileStream)
                        fileContent.Headers.ContentType = New MediaTypeHeaderValue(GuessMimeType(filePath))
                        form.Add(fileContent, "assetData", IO.Path.GetFileName(filePath))
                        Using req = New HttpRequestMessage(HttpMethod.Put, ApiUrl($"assets/{Uri.EscapeDataString(assetId)}/original"))
                            req.Content = form
                            Using resp = Await client.SendAsync(req, cancellationToken).ConfigureAwait(False)
                                If resp.IsSuccessStatusCode Then Return True
                                Dim body = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                                DiagnosticLogService.LogAlways("Immich.ReplaceAsset", $"HTTP {CInt(resp.StatusCode)} asset={assetId} {body.Substring(0, Math.Min(300, body.Length))}")
                                Return False
                            End Using
                        End Using
                    End Using
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.ReplaceAsset", ex)
                Return False
            End Try
        End Function

        ''' <summary>Überträgt Alben, Favorit, Stack, geteilte Links und Sidecar vom Quell- auf das
        ''' Zielasset (PUT /assets/copy, ab Immich v3).</summary>
        Private Shared Async Function CopyAssetLinksAsync(sourceId As String, targetId As String, cancellationToken As CancellationToken) As Task(Of Boolean)
            Try
                Dim client = GetClient()
                Dim body = "{""sourceId"":" & JsonSerializer.Serialize(sourceId) & ",""targetId"":" & JsonSerializer.Serialize(targetId) & "}"
                Using content = New StringContent(body, Encoding.UTF8, "application/json")
                    Using resp = Await client.PutAsync(ApiUrl("assets/copy"), content, cancellationToken).ConfigureAwait(False)
                        If Not resp.IsSuccessStatusCode Then
                            DiagnosticLogService.LogAlways("Immich.CopyAsset", $"HTTP {CInt(resp.StatusCode)} {sourceId} → {targetId}")
                        End If
                        Return resp.IsSuccessStatusCode
                    End Using
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.CopyAsset", ex)
                Return False
            End Try
        End Function

        ''' <summary>Zieht Beschreibung, Bewertung und Stichwörter des Quellassets auf das Zielasset nach -
        ''' PUT /assets/copy deckt die nicht ab. Bewusst der ROHE Serverzustand: liegen Bewertung und
        ''' Stichwörter (je nach Einstellung) im [FerrumPix]-Block der Beschreibung, kommen sie mit der
        ''' Beschreibung mit, ohne dass hier zwischen beiden Ablagearten unterschieden werden muss.</summary>
        Private Shared Async Function CopyAssetMetadataAsync(raw As ImmichAssetDto, targetId As String, cancellationToken As CancellationToken) As Task(Of Boolean)
            Try
                If raw Is Nothing Then Return False

                Dim fields As New List(Of String)()
                Dim description = If(raw.ExifInfo?.Description, "")
                If Not String.IsNullOrEmpty(description) Then fields.Add("""description"":" & JsonSerializer.Serialize(description))
                Dim rating = If(raw.ExifInfo?.Rating.HasValue, CInt(Math.Round(raw.ExifInfo.Rating.Value)), 0)
                If rating >= 1 AndAlso rating <= 5 Then fields.Add("""rating"":" & rating.ToString(Globalization.CultureInfo.InvariantCulture))
                If fields.Count > 0 Then
                    Await UpdateAssetAsync(targetId, "{" & String.Join(",", fields) & "}", cancellationToken).ConfigureAwait(False)
                End If

                For Each tag In If(raw.Tags, New List(Of ImmichTagDto)())
                    If tag Is Nothing OrElse String.IsNullOrEmpty(tag.Id) Then Continue For
                    Await TagAssetsAsync(tag.Id, targetId, add:=True, cancellationToken:=cancellationToken).ConfigureAwait(False)
                Next

                Await RefreshAssetDetailCacheAsync(targetId, $"nach CopyAssetMetadata von {raw.Id}", cancellationToken).ConfigureAwait(False)
                Return True
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.CopyAssetMetadata", ex)
                Return False
            End Try
        End Function

        ''' <summary>Löscht Assets auf dem Server. force=False legt sie in den Immich-Papierkorb (dort
        ''' wiederherstellbar), force=True löscht sie endgültig.</summary>
        Public Shared Async Function DeleteAssetsAsync(assetIds As IEnumerable(Of String), force As Boolean, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If Not IsConfigured Then Return False
            Dim ids = If(assetIds, Enumerable.Empty(Of String)()).Where(Function(s) Not String.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal).ToList()
            If ids.Count = 0 Then Return False
            Try
                Dim client = GetClient()
                Dim body = "{""force"":" & If(force, "true", "false") & ",""ids"":[" & String.Join(",", ids.Select(Function(i) JsonSerializer.Serialize(i))) & "]}"
                Using req = New HttpRequestMessage(HttpMethod.Delete, ApiUrl("assets"))
                    req.Content = New StringContent(body, Encoding.UTF8, "application/json")
                    Using resp = Await client.SendAsync(req, cancellationToken).ConfigureAwait(False)
                        If Not resp.IsSuccessStatusCode Then
                            Dim err = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                            DiagnosticLogService.LogAlways("Immich.DeleteAssets", $"HTTP {CInt(resp.StatusCode)} force={force} n={ids.Count} {err.Substring(0, Math.Min(300, err.Length))}")
                            Return False
                        End If
                    End Using
                End Using
                For Each id In ids
                    InvalidateAssetCaches(id)
                Next
                Return True
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.DeleteAssets", ex)
                Return False
            End Try
        End Function

        ' Die Hauptversion des Servers entscheidet, wie ein vorhandenes Asset ersetzt wird (siehe
        ' ReplaceAssetAsync). Sie ändert sich nur bei einem Server-Update, daher je Server einmal abfragen.
        Private Shared _serverMajorVersion As Integer = -1
        Private Shared _serverVersionKey As String = Nothing

        ''' <summary>Hauptversion des Immich-Servers (GET /server/version), 0 wenn nicht ermittelbar.</summary>
        Public Shared Async Function GetServerMajorVersionAsync(Optional cancellationToken As CancellationToken = Nothing) As Task(Of Integer)
            If Not IsConfigured Then Return 0
            Dim key = NormalizeServerUrl(AppSettingsService.Load().ImmichServerUrl)
            If _serverMajorVersion >= 0 AndAlso String.Equals(_serverVersionKey, key, StringComparison.Ordinal) Then Return _serverMajorVersion
            Try
                Dim client = GetClient()
                Using resp = Await client.GetAsync(ApiUrl("server/version"), cancellationToken).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then Return 0
                    Dim body = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    Dim dto = JsonSerializer.Deserialize(Of ImmichVersionDto)(body, JsonOptions)
                    Dim major = If(dto Is Nothing, 0, dto.Major)
                    _serverMajorVersion = major
                    _serverVersionKey = key
                    Return major
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.ServerVersion", ex)
                Return 0
            End Try
        End Function

        ''' <summary>Wirft alles lokal Zwischengespeicherte zu einem Asset weg: die Thumbnail-Dateien beider
        ''' Größen, die heruntergeladene Temp-Kopie des Originals und den Metadaten-Index-Eintrag. Nach
        ''' Ersetzen oder Löschen zwingend - sonst zeigt die Galerie das Bild von vorher weiter.</summary>
        Public Shared Sub InvalidateAssetCaches(assetId As String)
            If String.IsNullOrWhiteSpace(assetId) Then Return
            Try
                For Each sizeKey In {ThumbnailSize, PreviewSize}
                    Dim cachePath = GetCacheFilePath(assetId, sizeKey)
                    If File.Exists(cachePath) Then File.Delete(cachePath)
                Next
            Catch
            End Try
            Try
                If Directory.Exists(ImmichTempDir) Then
                    For Each temp In Directory.GetFiles(ImmichTempDir, SafeFileStem(assetId) & ".*")
                        Try : File.Delete(temp) : Catch : End Try
                    Next
                End If
            Catch
            End Try
            ImmichIndexService.Instance.Remove(ServerKey, assetId)
        End Sub

        Private Shared Function GuessMimeType(filePath As String) As String
            Select Case IO.Path.GetExtension(filePath).ToLowerInvariant()
                Case ".jpg", ".jpeg" : Return "image/jpeg"
                Case ".png" : Return "image/png"
                Case ".gif" : Return "image/gif"
                Case ".webp" : Return "image/webp"
                Case ".bmp" : Return "image/bmp"
                Case ".tif", ".tiff" : Return "image/tiff"
                Case ".heic" : Return "image/heic"
                Case ".heif" : Return "image/heif"
                Case ".avif" : Return "image/avif"
                Case ".mp4" : Return "video/mp4"
                Case ".mov" : Return "video/quicktime"
                Case ".mkv" : Return "video/x-matroska"
                Case ".avi" : Return "video/x-msvideo"
                Case ".webm" : Return "video/webm"
                Case Else : Return "application/octet-stream"
            End Select
        End Function

        ''' <summary>Alle Alben des Servers, alphabetisch. Leere Liste bei Fehler/keiner Konfiguration.</summary>
        Public Shared Async Function GetAlbumsAsync(Optional cancellationToken As CancellationToken = Nothing) As Task(Of List(Of ImmichAlbum))
            If Not IsConfigured Then Return New List(Of ImmichAlbum)()
            Try
                Dim client = GetClient()
                Using resp = Await client.GetAsync(ApiUrl("albums"), cancellationToken).ConfigureAwait(False)
                    resp.EnsureSuccessStatusCode()
                    Dim body = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    Dim dtos = JsonSerializer.Deserialize(Of List(Of ImmichAlbumDto))(body, JsonOptions)
                    If dtos Is Nothing Then Return New List(Of ImmichAlbum)()
                    Return dtos.
                        Where(Function(d) d IsNot Nothing AndAlso Not String.IsNullOrEmpty(d.Id)).
                        Select(Function(d) New ImmichAlbum With {
                            .Id = d.Id,
                            .Name = If(d.AlbumName, "(ohne Namen)"),
                            .AssetCount = d.AssetCount,
                            .ThumbnailAssetId = If(d.AlbumThumbnailAssetId, "")
                        }).
                        OrderBy(Function(a) a.Name, StringComparer.CurrentCultureIgnoreCase).
                        ToList()
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.GetAlbums", ex)
                Return New List(Of ImmichAlbum)()
            End Try
        End Function

        Public Const AssetPageSize As Integer = 250

        ''' <summary>Eine Seite der Timeline-Suche: die Assets plus die Nummer der nächsten Seite
        ''' (0 = keine weitere). Erlaubt dem Aufrufer, seitenweise zu laden und schon geladene Fotos
        ''' sofort anzuzeigen, statt auf die ganze (u.U. riesige) Bibliothek zu warten.</summary>
        Public Class ImmichAssetPage
            Public Property Items As New List(Of ImmichAsset)()
            Public Property NextPage As Integer
        End Class

        ''' <summary>Sucht Fotos auf dem Server (für Immich-Suchlisten): bei vorhandenem Suchtext bevorzugt
        ''' über die semantische Suche (<c>/search/smart</c>, CLIP). Ist Smart Search serverseitig
        ''' deaktiviert, fällt die Suche auf Metadaten zurück: passende Tags werden über ihre Tag-IDs
        ''' gesucht, sonst der Dateiname. Ohne Suchtext wird direkt die Metadaten-Suche genutzt.</summary>
        Public Shared Async Function SearchAsync(query As String, favoriteOnly As Boolean, rating As Integer, page As Integer, Optional cancellationToken As CancellationToken = Nothing) As Task(Of ImmichAssetPage)
            If Not IsConfigured Then Return New ImmichAssetPage()
            Dim useSmart = Not String.IsNullOrWhiteSpace(query)
            Try
                If useSmart Then
                    Dim smartBody = BuildSearchBody(query.Trim(), favoriteOnly, rating, page, Nothing, useSmartQuery:=True)
                    Dim smartResult = Await ExecuteSearchRequestAsync("search/smart", smartBody, cancellationToken).ConfigureAwait(False)
                    If smartResult.Ok Then Return smartResult.Page

                    If smartResult.StatusCode <> 400 OrElse smartResult.ErrorBody.IndexOf("Smart search is not enabled", StringComparison.OrdinalIgnoreCase) < 0 Then
                        LastError = $"HTTP {smartResult.StatusCode}"
                        Return smartResult.Page
                    End If

                    DiagnosticLogService.LogAlways("Immich.Search", "Smart Search deaktiviert - fallback auf search/metadata")
                End If

                Return Await SearchMetadataFallbackAsync(query, favoriteOnly, rating, page, cancellationToken).ConfigureAwait(False)
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Immich.Search", ex)
            End Try
            Return New ImmichAssetPage()
        End Function

        Private Shared Function BuildSearchBody(query As String,
                                                favoriteOnly As Boolean,
                                                rating As Integer,
                                                page As Integer,
                                                tagIds As List(Of String),
                                                useSmartQuery As Boolean,
                                                Optional includeRatingFilter As Boolean = True,
                                                Optional includeExif As Boolean = False) As String
            Dim fields As New List(Of String) From {$"""page"":{Math.Max(1, page)}", $"""size"":{AssetPageSize}"}
            If useSmartQuery AndAlso Not String.IsNullOrWhiteSpace(query) Then fields.Add("""query"":" & JsonSerializer.Serialize(query.Trim()))
            If Not useSmartQuery AndAlso Not String.IsNullOrWhiteSpace(query) Then fields.Add("""description"":" & JsonSerializer.Serialize(query.Trim()))
            If tagIds IsNot Nothing AndAlso tagIds.Count > 0 Then
                fields.Add("""tagIds"":[" & String.Join(",", tagIds.Select(Function(id) JsonSerializer.Serialize(id))) & "]")
            End If
            If favoriteOnly Then fields.Add("""isFavorite"":true")
            If includeRatingFilter AndAlso rating >= 1 AndAlso rating <= 5 Then fields.Add($"""rating"":{rating}")
            If includeExif Then fields.Add("""withExif"":true")
            Return "{" & String.Join(",", fields) & "}"
        End Function

        Private Structure ImmichSearchRequestResult
            Public Property Ok As Boolean
            Public Property StatusCode As Integer
            Public Property ErrorBody As String
            Public Property Page As ImmichAssetPage
        End Structure

        Private Shared Async Function ExecuteSearchRequestAsync(endpoint As String, requestBody As String, cancellationToken As CancellationToken) As Task(Of ImmichSearchRequestResult)
            Dim result As New ImmichSearchRequestResult With {.Page = New ImmichAssetPage(), .ErrorBody = ""}
            Dim client = GetClient()
            Using content = New StringContent(requestBody, Encoding.UTF8, "application/json")
                Using resp = Await client.PostAsync(ApiUrl(endpoint), content, cancellationToken).ConfigureAwait(False)
                    result.StatusCode = CInt(resp.StatusCode)
                    Dim body = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then
                        result.ErrorBody = body
                        DiagnosticLogService.LogAlways("Immich.Search", $"{endpoint} → HTTP {result.StatusCode} {body.Substring(0, Math.Min(300, body.Length))}")
                        Return result
                    End If

                    Dim dto = JsonSerializer.Deserialize(Of ImmichSearchResponseDto)(body, JsonOptions)
                    Dim items = dto?.Assets?.Items
                    If items IsNot Nothing Then
                        For Each a In items
                            If a Is Nothing OrElse String.IsNullOrEmpty(a.Id) Then Continue For
                            If Not IsBrowsableAsset(a.Visibility) Then Continue For
                            result.Page.Items.Add(MapAsset(a))
                        Next
                    End If

                    Dim parsedPage As Integer
                    If Not String.IsNullOrEmpty(dto?.Assets?.NextPage) AndAlso Integer.TryParse(dto.Assets.NextPage, parsedPage) Then result.Page.NextPage = parsedPage
                    result.Ok = True
                    LastError = Nothing
                    Return result
                End Using
            End Using
        End Function

        Private Shared Async Function SearchMetadataFallbackAsync(query As String, favoriteOnly As Boolean, rating As Integer, page As Integer, cancellationToken As CancellationToken) As Task(Of ImmichAssetPage)
            Dim settings = AppSettingsService.Load()
            Dim serverCanFilterRating = Not settings.ImmichStoreRatingInDescription

            Dim yearBody = BuildYearSearchBody(query, favoriteOnly, rating, page, serverCanFilterRating)
            If yearBody IsNot Nothing Then
                Dim yearResult = Await ExecuteSearchRequestAsync("search/metadata", yearBody, cancellationToken).ConfigureAwait(False)
                If yearResult.Ok Then Return yearResult.Page
            End If

            Dim tagIds = Await FindTagIdsForSearchAsync(query, cancellationToken).ConfigureAwait(False)
            Dim filenameQuery = If(tagIds.Count > 0, Nothing, query)
            Dim body = BuildSearchBody(filenameQuery, favoriteOnly, rating, page, tagIds, useSmartQuery:=False, includeRatingFilter:=serverCanFilterRating, includeExif:=True)
            Dim metadataResult = Await ExecuteSearchRequestAsync("search/metadata", body, cancellationToken).ConfigureAwait(False)
            If metadataResult.Ok AndAlso (String.IsNullOrWhiteSpace(query) OrElse metadataResult.Page.Items.Count > 0) AndAlso (serverCanFilterRating OrElse rating < 1 OrElse rating > 5) Then Return metadataResult.Page

            Dim clientFiltered = Await SearchMetadataClientFilteredAsync(query, favoriteOnly, rating, Math.Max(1, page), cancellationToken).ConfigureAwait(False)
            If clientFiltered IsNot Nothing Then Return clientFiltered

            LastError = $"HTTP {metadataResult.StatusCode}"
            Return metadataResult.Page
        End Function

        Private Shared Function BuildYearSearchBody(query As String,
                                                    favoriteOnly As Boolean,
                                                    rating As Integer,
                                                    page As Integer,
                                                    includeRatingFilter As Boolean) As String
            Dim text = If(query, "").Trim()
            If text.Length <> 4 Then Return Nothing
            Dim year As Integer
            If Not Integer.TryParse(text, year) OrElse year < 1900 OrElse year > 2200 Then Return Nothing

            Dim fields As New List(Of String) From {
                $"""page"":{Math.Max(1, page)}",
                $"""size"":{AssetPageSize}",
                """withExif"":true",
                """takenAfter"":" & JsonSerializer.Serialize($"{year:0000}-01-01T00:00:00.000Z"),
                """takenBefore"":" & JsonSerializer.Serialize($"{year + 1:0000}-01-01T00:00:00.000Z")
            }
            If favoriteOnly Then fields.Add("""isFavorite"":true")
            If includeRatingFilter AndAlso rating >= 1 AndAlso rating <= 5 Then fields.Add($"""rating"":{rating}")
            Return "{" & String.Join(",", fields) & "}"
        End Function

        Private Shared Async Function SearchMetadataClientFilteredAsync(query As String,
                                                                        favoriteOnly As Boolean,
                                                                        rating As Integer,
                                                                        startPage As Integer,
                                                                        cancellationToken As CancellationToken) As Task(Of ImmichAssetPage)
            Dim result As New ImmichAssetPage()
            Dim queryText = If(query, "").Trim()
            Dim page = Math.Max(1, startPage)
            Dim scannedPages = 0
            Const MaxScanPagesPerCall As Integer = 20

            Do
                cancellationToken.ThrowIfCancellationRequested()
                Dim body = BuildSearchBody(Nothing, favoriteOnly, 0, page, Nothing, useSmartQuery:=False, includeRatingFilter:=False, includeExif:=True)
                Dim serverPage = Await ExecuteSearchRequestAsync("search/metadata", body, cancellationToken).ConfigureAwait(False)
                If Not serverPage.Ok Then
                    LastError = $"HTTP {serverPage.StatusCode}"
                    Return result
                End If

                For Each item In serverPage.Page.Items
                    If MatchesMetadataFallback(item, queryText, rating) Then result.Items.Add(item)
                    If result.Items.Count >= AssetPageSize Then
                        result.NextPage = If(serverPage.Page.NextPage > 0, serverPage.Page.NextPage, 0)
                        LastError = Nothing
                        Return result
                    End If
                Next

                scannedPages += 1
                If serverPage.Page.NextPage <= 0 Then Exit Do
                page = serverPage.Page.NextPage
            Loop While scannedPages < MaxScanPagesPerCall

            result.NextPage = If(result.Items.Count > 0, page, 0)
            LastError = Nothing
            Return result
        End Function

        Private Shared Function MatchesMetadataFallback(item As ImmichAsset, query As String, rating As Integer) As Boolean
            If item Is Nothing Then Return False
            If rating >= 1 AndAlso rating <= 5 AndAlso item.Rating <> rating Then Return False
            If String.IsNullOrWhiteSpace(query) Then Return True
            If If(item.FileName, "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 Then Return True
            If If(item.Description, "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 Then Return True
            If item.Tags IsNot Nothing AndAlso item.Tags.Any(Function(t) If(t, "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) Then Return True
            Return False
        End Function

        Private Shared Async Function FindTagIdsForSearchAsync(query As String, cancellationToken As CancellationToken) As Task(Of List(Of String))
            Dim trimmed = If(query, "").Trim()
            If String.IsNullOrWhiteSpace(trimmed) Then Return New List(Of String)()
            Try
                Dim client = GetClient()
                Using resp = Await client.GetAsync(ApiUrl("tags"), cancellationToken).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then Return New List(Of String)()
                    Dim json = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    Dim tags = JsonSerializer.Deserialize(Of List(Of ImmichTagDto))(json, JsonOptions)
                    If tags Is Nothing Then Return New List(Of String)()
                    Return tags.
                        Where(Function(t) t IsNot Nothing AndAlso Not String.IsNullOrEmpty(t.Id) AndAlso
                                           ((If(t.Value, "").IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0) OrElse
                                            (If(t.Name, "").IndexOf(trimmed, StringComparison.OrdinalIgnoreCase) >= 0))).
                        Select(Function(t) t.Id).
                        Distinct(StringComparer.OrdinalIgnoreCase).
                        ToList()
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.SearchTags", ex)
                Return New List(Of String)()
            End Try
        End Function

        ''' <summary>Holt eine Seite von Fotos über die Metadaten-Suche - optional auf ein Album
        ''' gefiltert (v3 liefert Album-Assets NICHT über /api/albums/{id}, sondern nur so). Neueste zuerst.</summary>
        Public Shared Async Function GetAssetsPageAsync(page As Integer, Optional albumId As String = Nothing,
                                                        Optional cancellationToken As CancellationToken = Nothing,
                                                        Optional personId As String = Nothing,
                                                        Optional city As String = Nothing) As Task(Of ImmichAssetPage)
            Dim result As New ImmichAssetPage()
            If Not IsConfigured Then Return result
            Try
                Dim client = GetClient()
                ' Ein gemeinsamer Fetcher fuer "Alle Fotos", Alben, Personen und Orte - search/metadata
                ' filtert wahlweise per albumIds, personIds (Gesichtserkennung des Servers) oder city.
                Dim filters As New List(Of String)()
                If Not String.IsNullOrWhiteSpace(albumId) Then filters.Add($"""albumIds"":[""{albumId}""]")
                If Not String.IsNullOrWhiteSpace(personId) Then filters.Add($"""personIds"":[""{personId}""]")
                If Not String.IsNullOrWhiteSpace(city) Then filters.Add($"""city"":{JsonSerializer.Serialize(city)}")
                Dim filterPrefix = If(filters.Count > 0, String.Join(",", filters) & ",", "")
                Dim requestBody = $"{{{filterPrefix}""page"":{Math.Max(1, page)},""size"":{AssetPageSize}}}"
                Using content = New StringContent(requestBody, Encoding.UTF8, "application/json")
                    Using resp = Await client.PostAsync(ApiUrl("search/metadata"), content, cancellationToken).ConfigureAwait(False)
                        resp.EnsureSuccessStatusCode()
                        Dim body = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                        Dim dto = JsonSerializer.Deserialize(Of ImmichSearchResponseDto)(body, JsonOptions)
                        Dim items = dto?.Assets?.Items
                        If items IsNot Nothing Then
                            For Each a In items
                                If a Is Nothing OrElse String.IsNullOrEmpty(a.Id) Then Continue For
                                If Not IsBrowsableAsset(a.Visibility) Then Continue For
                                result.Items.Add(MapAsset(a))
                            Next
                        End If
                        Dim parsedPage As Integer
                        If Not String.IsNullOrEmpty(dto?.Assets?.NextPage) AndAlso Integer.TryParse(dto.Assets.NextPage, parsedPage) Then
                            result.NextPage = parsedPage
                        End If
                    End Using
                End Using
                LastError = Nothing
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Immich.GetAssetsPage", ex)
            End Try
            Return result
        End Function

        ''' <summary>Benannte Personen der serverseitigen Gesichtserkennung (GET /api/people).
        ''' Unbenannte und versteckte Gesichter bleiben draußen - sie wären als Sidebar-Knoten
        ''' ohne Namen wertlos. Leere Liste bei Fehler/unkonfiguriert (wirft nie).</summary>
        Public Shared Async Function GetPeopleAsync(Optional cancellationToken As CancellationToken = Nothing) As Task(Of List(Of ImmichPerson))
            Dim result As New List(Of ImmichPerson)()
            If Not IsConfigured Then Return result
            Try
                Dim client = GetClient()
                Using resp = Await client.GetAsync(ApiUrl("people?withHidden=false"), cancellationToken).ConfigureAwait(False)
                    resp.EnsureSuccessStatusCode()
                    Dim body = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    Dim dto = JsonSerializer.Deserialize(Of ImmichPeopleResponseDto)(body, JsonOptions)
                    If dto?.People IsNot Nothing Then
                        For Each p In dto.People
                            If p Is Nothing OrElse String.IsNullOrEmpty(p.Id) OrElse String.IsNullOrWhiteSpace(p.Name) Then Continue For
                            If p.IsHidden Then Continue For
                            result.Add(New ImmichPerson With {.Id = p.Id, .Name = p.Name.Trim()})
                        Next
                    End If
                End Using
                result.Sort(Function(a, b) String.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase))
                LastError = Nothing
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Immich.GetPeople", ex)
            End Try
            Return result
        End Function

        ''' <summary>Orte (Städte) der Bibliothek. Primär über GET /api/search/cities - das liefert
        ''' ALLE Städte (ein Beispiel-Asset je Stadt, exifInfo.city). GET /api/search/explore wäre
        ''' naheliegender, gibt aber nur eine BEGRENZTE kuratierte Auswahl zurück (Befund:
        ''' Liste endete alphabetisch bei "B") - es bleibt nur als Fallback für ältere Server.
        ''' Leere Liste bei Fehler (wirft nie).</summary>
        Public Shared Async Function GetPlacesAsync(Optional cancellationToken As CancellationToken = Nothing) As Task(Of List(Of String))
            Dim result As New List(Of String)()
            If Not IsConfigured Then Return result
            Try
                Dim client = GetClient()
                Using resp = Await client.GetAsync(ApiUrl("search/cities"), cancellationToken).ConfigureAwait(False)
                    If resp.IsSuccessStatusCode Then
                        Dim body = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                        Dim assets = JsonSerializer.Deserialize(Of List(Of ImmichCityAssetDto))(body, JsonOptions)
                        If assets IsNot Nothing Then
                            For Each asset In assets
                                Dim value = asset?.ExifInfo?.City
                                If Not String.IsNullOrWhiteSpace(value) AndAlso Not result.Contains(value, StringComparer.CurrentCultureIgnoreCase) Then
                                    result.Add(value.Trim())
                                End If
                            Next
                        End If
                    End If
                End Using

                If result.Count = 0 Then
                    ' Fallback: search/explore (begrenzte Auswahl, besser als nichts).
                    Using resp = Await client.GetAsync(ApiUrl("search/explore"), cancellationToken).ConfigureAwait(False)
                        resp.EnsureSuccessStatusCode()
                        Dim body = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                        Dim fields = JsonSerializer.Deserialize(Of List(Of ImmichExploreFieldDto))(body, JsonOptions)
                        If fields IsNot Nothing Then
                            For Each field In fields
                                If field Is Nothing OrElse Not String.Equals(field.FieldName, "exifInfo.city", StringComparison.OrdinalIgnoreCase) Then Continue For
                                If field.Items Is Nothing Then Continue For
                                For Each item In field.Items
                                    Dim value = item?.Value
                                    If Not String.IsNullOrWhiteSpace(value) AndAlso Not result.Contains(value, StringComparer.CurrentCultureIgnoreCase) Then
                                        result.Add(value.Trim())
                                    End If
                                Next
                            Next
                        End If
                    End Using
                End If
                result.Sort(StringComparer.CurrentCultureIgnoreCase)
                LastError = Nothing
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                LastError = ex.Message
                DiagnosticLogService.LogException("Immich.GetPlaces", ex)
            End Try
            Return result
        End Function

        ''' <summary>Gehört ein Asset in die Galerie? Immich führt neben den normalen Assets auch
        ''' „versteckte" (visibility=hidden): vor allem die Videospur von Bewegtfotos (Motion Photos,
        ''' „MVIMG_…mp4"/„…-MP.mp4"), die zum Standbild gehört. Für die gibt es serverseitig KEIN
        ''' Vorschaubild - der Thumbnail-Endpunkt antwortet mit 404 „Asset media not found" -, und Immichs
        ''' eigene Oberfläche zeigt sie gar nicht erst an. Ungefiltert landeten sie bei uns als Kacheln
        ''' ohne Bild in der Galerie. „locked" (Sicherer Ordner) bleibt aus demselben Grund draußen.
        ''' Archivierte Assets sind ausdrücklich KEIN Sonderfall - die haben ein Vorschaubild und dürfen
        ''' angezeigt werden.</summary>
        Private Shared Function IsBrowsableAsset(visibility As String) As Boolean
            If String.IsNullOrWhiteSpace(visibility) Then Return True   ' älterer Server ohne das Feld
            Return Not (String.Equals(visibility, "hidden", StringComparison.OrdinalIgnoreCase) OrElse
                        String.Equals(visibility, "locked", StringComparison.OrdinalIgnoreCase))
        End Function

        Private Shared Function MapAsset(a As ImmichAssetDto) As ImmichAsset
            Dim item = New ImmichAsset With {
                .Id = a.Id,
                .FileName = If(a.OriginalFileName, a.Id),
                .IsVideo = String.Equals(a.Type, "VIDEO", StringComparison.OrdinalIgnoreCase),
                .FileCreatedAt = ParseDate(a.FileCreatedAt),
                .IsFavorite = a.IsFavorite,
                .UpdatedAt = If(a.UpdatedAt, ""),
                .Description = If(a.ExifInfo?.Description, "")
            }
            ' Maße bevorzugt aus den oberste-Ebene-Feldern (immer da), sonst aus exifInfo (Detail-Abruf).
            item.Width = CInt(Math.Round(If(a.Width, If(a.ExifInfo?.ExifImageWidth, 0))))
            item.Height = CInt(Math.Round(If(a.Height, If(a.ExifInfo?.ExifImageHeight, 0))))
            If a.ExifInfo IsNot Nothing Then
                item.FileSizeBytes = If(a.ExifInfo.FileSizeInByte.HasValue, CLng(a.ExifInfo.FileSizeInByte.Value), 0L)
                item.ExifDateTaken = ParseDate(a.ExifInfo.DateTimeOriginal)
                item.Iso = If(a.ExifInfo.Iso.HasValue, CInt(Math.Round(a.ExifInfo.Iso.Value)), CType(Nothing, Integer?))
                item.Aperture = a.ExifInfo.FNumber
                ' Immich (v3): 1-5 gültig, null/-1 = unbewertet. Auf FerrumPix 0-5 abbilden.
                item.Rating = If(a.ExifInfo.Rating.HasValue AndAlso a.ExifInfo.Rating.Value >= 1, CInt(Math.Round(a.ExifInfo.Rating.Value)), 0)
                Dim make = If(a.ExifInfo.Make, "").Trim()
                Dim model = If(a.ExifInfo.Model, "").Trim()
                item.Camera = String.Join(" ", {make, model}.Where(Function(s) Not String.IsNullOrEmpty(s))).Trim()
            End If
            If a.Tags IsNot Nothing Then
                item.Tags = a.Tags.
                    Where(Function(t) t IsNot Nothing).
                    Select(Function(t) If(String.IsNullOrEmpty(t.Value), t.Name, t.Value)).
                    Where(Function(s) Not String.IsNullOrWhiteSpace(s)).
                    ToList()
            End If
            ApplyFerrumPixDescriptionMeta(item)
            Return item
        End Function

        Private Const FerrumPixMetaStart As String = "[FerrumPix]"
        Private Const FerrumPixMetaEnd As String = "[/FerrumPix]"

        Private Class FerrumPixDescriptionMeta
            Public Property Favorite As Boolean?
            Public Property Rating As Integer?
            Public Property Tags As List(Of String)
        End Class

        Private Shared Function TryReadFerrumPixMeta(description As String, ByRef baseDescription As String, ByRef meta As FerrumPixDescriptionMeta) As Boolean
            baseDescription = If(description, "")
            meta = Nothing
            Dim startIdx = baseDescription.IndexOf(FerrumPixMetaStart, StringComparison.Ordinal)
            If startIdx < 0 Then Return False
            Dim jsonStart = startIdx + FerrumPixMetaStart.Length
            Dim endIdx = baseDescription.IndexOf(FerrumPixMetaEnd, jsonStart, StringComparison.Ordinal)
            If endIdx < 0 Then Return False

            Dim json = baseDescription.Substring(jsonStart, endIdx - jsonStart).Trim()
            Try
                meta = JsonSerializer.Deserialize(Of FerrumPixDescriptionMeta)(json, JsonOptions)
            Catch
                meta = Nothing
            End Try
            baseDescription = (baseDescription.Substring(0, startIdx) &
                               baseDescription.Substring(endIdx + FerrumPixMetaEnd.Length)).Trim()
            Return meta IsNot Nothing
        End Function

        Private Shared Function BuildDescriptionWithFerrumPixMeta(currentDescription As String, meta As FerrumPixDescriptionMeta) As String
            Dim baseDescription As String = Nothing
            Dim ignored As FerrumPixDescriptionMeta = Nothing
            TryReadFerrumPixMeta(currentDescription, baseDescription, ignored)
            Dim json = JsonSerializer.Serialize(meta, JsonOptions)
            Dim block = FerrumPixMetaStart & Environment.NewLine & json & Environment.NewLine & FerrumPixMetaEnd
            If String.IsNullOrWhiteSpace(baseDescription) Then Return block
            Return baseDescription.TrimEnd() & Environment.NewLine & Environment.NewLine & block
        End Function

        Private Shared Sub ApplyFerrumPixDescriptionMeta(asset As ImmichAsset)
            If asset Is Nothing Then Return
            Dim settings = AppSettingsService.Load()
            Dim baseDescription As String = Nothing
            Dim meta As FerrumPixDescriptionMeta = Nothing
            If Not TryReadFerrumPixMeta(asset.Description, baseDescription, meta) Then Return
            asset.Description = baseDescription

            ' Vorhandene FerrumPix-Blöcke auch dann lesen, wenn der Nutzer inzwischen wieder native
            ' Immich-Felder bevorzugt: native Werte gewinnen, Beschreibung dient als Fallback.
            If meta.Rating.HasValue AndAlso (settings.ImmichStoreRatingInDescription OrElse asset.Rating <= 0) Then
                asset.Rating = Math.Max(0, Math.Min(5, meta.Rating.Value))
            End If
            If meta.Tags IsNot Nothing AndAlso (settings.ImmichStoreTagsInDescription OrElse asset.Tags Is Nothing OrElse asset.Tags.Count = 0) Then
                asset.Tags = meta.Tags.
                    Where(Function(t) Not String.IsNullOrWhiteSpace(t)).
                    Distinct(StringComparer.OrdinalIgnoreCase).
                    ToList()
            End If
        End Sub

        Private Shared Function ParseDate(value As String) As DateTime?
            If String.IsNullOrWhiteSpace(value) Then Return Nothing
            Dim parsed As DateTime
            If DateTime.TryParse(value, Nothing, Globalization.DateTimeStyles.AdjustToUniversal Or Globalization.DateTimeStyles.AssumeUniversal, parsed) Then
                Return parsed.ToLocalTime()
            End If
            Return Nothing
        End Function

        ''' <summary>Volle Metadaten eines Assets über GET /api/assets/{id} - anders als die Metadaten-Suche
        ''' enthält die Detailantwort exifInfo (Dateigröße, Kamera, ISO, Blende, Rating) und tags. Wird im
        ''' Hintergrund nachgeladen, um die aus der Suche gelieferten Grunddaten zu ergänzen. Nothing bei Fehler.</summary>
        ' Detail-Abrufe werden je Item viewport-gekoppelt ausgelöst und teilen sich den HttpClient mit den
        ' (wichtigeren) Thumbnail-Abrufen. Deshalb streng gedeckelt - der Index-Lookup UND der Netzabruf
        ' laufen innerhalb dieses Kontingents, damit weder Threadpool noch Netz die Thumbnails aushungern.
        Private Shared ReadOnly _detailSemaphore As New System.Threading.SemaphoreSlim(2)

        ''' <summary>Detaildaten mit lokalem Index als Read-Through-Cache: liegt ein Eintrag mit passendem
        ''' <paramref name="updatedAt"/> im <see cref="ImmichIndexService"/> vor, wird er ohne Netz genutzt;
        ''' sonst wird über das Netz geholt und der Index aktualisiert. Über Sitzungen hinweg entfällt so
        ''' das wiederholte Abrufen zehntausender Assets.</summary>
        Public Shared Async Function GetAssetDetailCachedAsync(assetId As String, updatedAt As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of ImmichAsset)
            If String.IsNullOrWhiteSpace(assetId) Then Return Nothing
            Await _detailSemaphore.WaitAsync(cancellationToken).ConfigureAwait(False)
            Try
                Dim srvKey = ServerKey
                Dim cached = ImmichIndexService.Instance.TryGet(srvKey, assetId, updatedAt)
                If cached IsNot Nothing Then Return cached

                Dim detail = Await GetAssetDetailAsync(assetId, cancellationToken).ConfigureAwait(False)
                If detail IsNot Nothing Then
                    If String.IsNullOrEmpty(detail.UpdatedAt) Then detail.UpdatedAt = If(updatedAt, "")
                    ImmichIndexService.Instance.Put(srvKey, detail)
                End If
                Return detail
            Finally
                _detailSemaphore.Release()
            End Try
        End Function

        ''' <summary>Roher Detail-Abruf ohne Cache/Drosselung. Aufrufer sollten den gedrosselten
        ''' <see cref="GetAssetDetailCachedAsync"/> nutzen; dieser hier wird von dort aus verwendet.</summary>
        Public Shared Async Function GetAssetDetailAsync(assetId As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of ImmichAsset)
            Dim dto = Await GetAssetDetailRawAsync(assetId, cancellationToken).ConfigureAwait(False)
            If dto Is Nothing Then Return Nothing
            Return MapAsset(dto)
        End Function

        Private Shared Async Function GetAssetDetailRawAsync(assetId As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of ImmichAssetDto)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(assetId) Then Return Nothing
            Try
                Dim client = GetClient()
                Using resp = Await client.GetAsync(ApiUrl("assets/" & Uri.EscapeDataString(assetId)), cancellationToken).ConfigureAwait(False)
                    resp.EnsureSuccessStatusCode()
                    Dim body = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    Dim dto = JsonSerializer.Deserialize(Of ImmichAssetDto)(body, JsonOptions)
                    If dto Is Nothing OrElse String.IsNullOrEmpty(dto.Id) Then Return Nothing
                    Return dto
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.GetAssetDetail", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Lädt ein Thumbnail als Avalonia-Bitmap - zuerst aus dem lokalen Diskcache, sonst
        ''' per HTTP mit anschließendem Cachen. Liefert Nothing bei Abbruch/Fehler.</summary>
        Public Shared Async Function LoadThumbnailBitmapAsync(assetId As String, size As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Bitmap)
            Dim bytes = Await GetThumbnailBytesAsync(assetId, size, cancellationToken).ConfigureAwait(False)
            If bytes Is Nothing OrElse bytes.Length = 0 Then Return Nothing
            cancellationToken.ThrowIfCancellationRequested()
            Try
                Using ms = New MemoryStream(bytes, writable:=False)
                    Return New Bitmap(ms)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.DecodeThumbnail", ex)
                Return Nothing
            End Try
        End Function

        Private Shared Async Function GetThumbnailBytesAsync(assetId As String, size As String, cancellationToken As CancellationToken) As Task(Of Byte())
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(assetId) Then Return Nothing
            Dim sizeKey = If(String.Equals(size, PreviewSize, StringComparison.OrdinalIgnoreCase), PreviewSize, ThumbnailSize)

            Dim cachePath = GetCacheFilePath(assetId, sizeKey)
            Try
                If File.Exists(cachePath) Then
                    Return Await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(False)
                End If
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch
            End Try

            Try
                Dim client = GetClient()
                Dim url = ApiUrl($"assets/{Uri.EscapeDataString(assetId)}/thumbnail?size={sizeKey}")
                Using resp = Await client.GetAsync(url, cancellationToken).ConfigureAwait(False)
                    ' Fehlschläge NICHT verschlucken: eine leere Kachel sieht sonst genauso aus wie ein Bild,
                    ' das der Server (noch) nicht hat, und man rät, statt den Statuscode zu lesen.
                    If Not resp.IsSuccessStatusCode Then
                        DiagnosticLogService.LogAlways("Immich.Thumbnail", $"HTTP {CInt(resp.StatusCode)} size={sizeKey} asset={assetId}")
                        Return Nothing
                    End If
                    Dim bytes = Await resp.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(False)
                    If bytes Is Nothing OrElse bytes.Length = 0 Then
                        DiagnosticLogService.LogAlways("Immich.Thumbnail", $"leere Antwort size={sizeKey} asset={assetId}")
                        Return Nothing
                    End If
                    Await TryWriteCacheAsync(cachePath, bytes, cancellationToken).ConfigureAwait(False)
                    Return bytes
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.GetThumbnailBytes", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Wartet nach einem Upload kurz darauf, dass Immich das asynchron erzeugte Thumbnail
        ''' ausliefert. Das verhindert leere Kacheln, wenn die Galerie direkt nach dem Upload neu lädt.</summary>
        Public Shared Async Function WaitForThumbnailReadyAsync(assetId As String,
                                                                Optional timeoutMs As Integer = 15000,
                                                                Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(assetId) Then Return False

            Dim started = DateTime.UtcNow
            Do
                cancellationToken.ThrowIfCancellationRequested()
                Dim bytes = Await GetThumbnailBytesAsync(assetId, ThumbnailSize, cancellationToken).ConfigureAwait(False)
                If bytes IsNot Nothing AndAlso bytes.Length > 0 Then Return True
                If (DateTime.UtcNow - started).TotalMilliseconds >= Math.Max(1000, timeoutMs) Then Exit Do
                Await Task.Delay(750, cancellationToken).ConfigureAwait(False)
            Loop

            DiagnosticLogService.LogAlways("Immich.ThumbnailWait", $"Timeout asset={assetId}")
            Return False
        End Function

        ''' <summary>Lädt das Originalbild eines Assets in eine lokale Temp-Datei und gibt deren Pfad
        ''' zurück (für Viewer/Editor, die mit lokalen Pfaden arbeiten). Der Dateiname behält die
        ''' Original-Endung, damit Format-Erkennung/Editor-Speichern korrekt greifen. Nothing bei Fehler.</summary>
        Public Shared Async Function DownloadOriginalToTempAsync(assetId As String, originalFileName As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of String)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(assetId) Then Return Nothing
            Try
                Directory.CreateDirectory(ImmichTempDir)
                Dim ext = IO.Path.GetExtension(If(originalFileName, "")).ToLowerInvariant()
                Dim safeName = SafeFileStem(assetId) & ext
                Dim targetPath = IO.Path.Combine(ImmichTempDir, safeName)
                If File.Exists(targetPath) Then Return targetPath

                Dim client = GetClient()
                Using resp = Await client.GetAsync(ApiUrl($"assets/{Uri.EscapeDataString(assetId)}/original"), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then Return Nothing
                    ' Die Zwischendatei je Abruf eindeutig benennen: laden zwei Stellen dasselbe Asset
                    ' gleichzeitig (Viewer und Filmstreifen, Stapelverarbeitung), griffen sie sonst nach
                    ' derselben „{assetId}.part" und eine von beiden stürbe an „file in use".
                    Dim tempPath = $"{targetPath}.{Guid.NewGuid():N}.part"
                    Try
                        Using fs = File.Create(tempPath)
                            Await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(False)
                        End Using
                        File.Move(tempPath, targetPath, overwrite:=True)
                    Finally
                        Try
                            If File.Exists(tempPath) Then File.Delete(tempPath)
                        Catch
                        End Try
                    End Try
                    Return targetPath
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.DownloadOriginal", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Setzt den Favoriten-Status eines Assets auf dem Server (Rückrichtung) und hält den
        ''' lokalen Index konsistent.</summary>
        Public Shared Async Function SetFavoriteAsync(assetId As String, isFavorite As Boolean, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            Dim ok = Await UpdateAssetAsync(assetId, $"{{""isFavorite"":{If(isFavorite, "true", "false")}}}", cancellationToken).ConfigureAwait(False)
            If ok Then
                ImmichIndexService.Instance.UpdateFavorite(ServerKey, assetId, isFavorite)
                Await RefreshAssetDetailCacheAsync(assetId, $"nach SetFavorite={isFavorite} ok={ok}", cancellationToken).ConfigureAwait(False)
            End If
            Return ok
        End Function

        ''' <summary>Setzt die Sternebewertung eines Assets (Rückrichtung). FerrumPix-0 (unbewertet) wird
        ''' zu Immichs null; gültig sind 1-5 (v3 akzeptiert 0 nicht mehr).</summary>
        Public Shared Async Function SetRatingAsync(assetId As String, rating As Integer, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            Dim safeRating = Math.Max(0, Math.Min(5, rating))
            If Not AppSettingsService.Load().ImmichStoreRatingInDescription Then
                Dim ratingJson = If(safeRating >= 1 AndAlso safeRating <= 5, safeRating.ToString(Globalization.CultureInfo.InvariantCulture), "null")
                Dim directOk = Await UpdateAssetAsync(assetId, $"{{""rating"":{ratingJson}}}", cancellationToken).ConfigureAwait(False)
                If directOk Then ImmichIndexService.Instance.UpdateRating(ServerKey, assetId, safeRating)
                Await RefreshAssetDetailCacheAsync(assetId, $"nach SetRating={safeRating} ok={directOk}", cancellationToken).ConfigureAwait(False)
                Return directOk
            End If

            Dim ok = Await UpdateFerrumPixDescriptionMetaAsync(assetId,
                Sub(meta)
                    meta.Rating = safeRating
                End Sub,
                $"nach SetRating={safeRating}",
                cancellationToken).ConfigureAwait(False)
            Return ok
        End Function

        Private Shared Async Function UpdateFerrumPixDescriptionMetaAsync(assetId As String,
                                                                          mutate As Action(Of FerrumPixDescriptionMeta),
                                                                          context As String,
                                                                          cancellationToken As CancellationToken) As Task(Of Boolean)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(assetId) Then Return False
            Try
                Dim raw = Await GetAssetDetailRawAsync(assetId, cancellationToken).ConfigureAwait(False)
                If raw Is Nothing Then Return False

                Dim currentDescription = If(raw.ExifInfo?.Description, "")
                Dim baseDescription As String = Nothing
                Dim meta As FerrumPixDescriptionMeta = Nothing
                If Not TryReadFerrumPixMeta(currentDescription, baseDescription, meta) OrElse meta Is Nothing Then
                    meta = New FerrumPixDescriptionMeta With {
                        .Rating = If(raw.ExifInfo?.Rating.HasValue, CInt(Math.Round(raw.ExifInfo.Rating.Value)), CType(Nothing, Integer?)),
                        .Tags = If(raw.Tags, New List(Of ImmichTagDto)()).
                            Where(Function(t) t IsNot Nothing).
                            Select(Function(t) If(String.IsNullOrEmpty(t.Value), t.Name, t.Value)).
                            Where(Function(s) Not String.IsNullOrWhiteSpace(s)).
                            Distinct(StringComparer.OrdinalIgnoreCase).
                            ToList()
                    }
                End If
                If meta.Tags Is Nothing Then meta.Tags = New List(Of String)()
                mutate(meta)

                Dim newDescription = BuildDescriptionWithFerrumPixMeta(currentDescription, meta)
                Dim ok = Await UpdateAssetAsync(assetId, "{""description"":" & JsonSerializer.Serialize(newDescription) & "}", cancellationToken).ConfigureAwait(False)
                Await RefreshAssetDetailCacheAsync(assetId, $"{context} ok={ok}", cancellationToken).ConfigureAwait(False)
                Return ok
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.UpdateFerrumPixDescriptionMeta", ex)
                Return False
            End Try
        End Function

        ''' <summary>Holt nach einem Schreibvorgang den echten Serverzustand, protokolliert ihn und
        ''' schreibt ihn in den lokalen Index. Wichtig: Immich aktualisiert asset.updatedAt bei Tag-
        ''' Änderungen nicht zuverlässig; punktuelle Cache-Patches würden dann später veraltete Details
        ''' liefern.</summary>
        Private Shared Async Function RefreshAssetDetailCacheAsync(assetId As String, context As String, cancellationToken As CancellationToken) As Task(Of ImmichAsset)
            Try
                Dim client = GetClient()
                Using resp = Await client.GetAsync(ApiUrl("assets/" & Uri.EscapeDataString(assetId)), cancellationToken).ConfigureAwait(False)
                    Dim body = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    DiagnosticLogService.LogAlways("Immich.AssetState", $"{context} | HTTP {CInt(resp.StatusCode)} | {body.Substring(0, Math.Min(2200, body.Length))}")
                    If Not resp.IsSuccessStatusCode Then Return Nothing

                    Dim dto = JsonSerializer.Deserialize(Of ImmichAssetDto)(body, JsonOptions)
                    If dto Is Nothing OrElse String.IsNullOrEmpty(dto.Id) Then Return Nothing
                    Dim detail = MapAsset(dto)
                    ImmichIndexService.Instance.Put(ServerKey, detail)
                    Return detail
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.RefreshAssetDetailCache", ex)
                Return Nothing
            End Try
        End Function

        Private Shared Async Function UpdateAssetAsync(assetId As String, jsonBody As String, cancellationToken As CancellationToken) As Task(Of Boolean)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(assetId) Then Return False
            Try
                Dim client = GetClient()
                Using content = New StringContent(jsonBody, Encoding.UTF8, "application/json")
                    Using resp = Await client.PutAsync(ApiUrl("assets/" & Uri.EscapeDataString(assetId)), content, cancellationToken).ConfigureAwait(False)
                        If Not resp.IsSuccessStatusCode Then
                            DiagnosticLogService.LogAlways("Immich.UpdateAsset", $"HTTP {CInt(resp.StatusCode)} für {jsonBody} (asset {assetId})")
                        End If
                        Return resp.IsSuccessStatusCode
                    End Using
                End Using
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.UpdateAsset", ex)
                Return False
            End Try
        End Function

        ''' <summary>Fügt einem Asset ein Stichwort hinzu (Rückrichtung). Immich-Tags sind eigene
        ''' Entitäten: erst anlegen/finden (PUT /api/tags), dann dem Asset zuordnen. „/" im Namen bildet
        ''' in Immich eine Hierarchie.</summary>
        Public Shared Async Function AddTagToAssetAsync(assetId As String, tagName As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(assetId) OrElse String.IsNullOrWhiteSpace(tagName) Then Return False
            Try
                Dim normalizedTag = tagName.Trim()
                If Not AppSettingsService.Load().ImmichStoreTagsInDescription Then
                    Dim tagId = Await UpsertTagIdAsync(normalizedTag, cancellationToken).ConfigureAwait(False)
                    If String.IsNullOrEmpty(tagId) Then
                        DiagnosticLogService.LogAlways("Immich.AddTag", $"Tag-Upsert lieferte keine ID für '{normalizedTag}'")
                        Return False
                    End If
                    Dim directOk = Await TagAssetsAsync(tagId, assetId, add:=True, cancellationToken:=cancellationToken).ConfigureAwait(False)
                    DiagnosticLogService.LogAlways("Immich.AddTag", $"'{normalizedTag}' (tagId={tagId}) → asset {assetId}: {If(directOk, "OK", "FEHLGESCHLAGEN")}")
                    Await RefreshAssetDetailCacheAsync(assetId, $"nach AddTag '{normalizedTag}' ok={directOk}", cancellationToken).ConfigureAwait(False)
                    Return directOk
                End If

                Dim ok = Await UpdateFerrumPixDescriptionMetaAsync(assetId,
                    Sub(meta)
                        If meta.Tags Is Nothing Then meta.Tags = New List(Of String)()
                        If Not meta.Tags.Any(Function(t) String.Equals(t, normalizedTag, StringComparison.OrdinalIgnoreCase)) Then meta.Tags.Add(normalizedTag)
                    End Sub,
                    $"nach AddTag '{normalizedTag}'",
                    cancellationToken).ConfigureAwait(False)
                DiagnosticLogService.LogAlways("Immich.AddTag", $"'{normalizedTag}' → description meta asset {assetId}: {If(ok, "OK", "FEHLGESCHLAGEN")}")
                Return ok
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.AddTag", ex)
                Return False
            End Try
        End Function

        ''' <summary>Entfernt ein Stichwort von einem Asset (Rückrichtung). Findet die Tag-ID und löst
        ''' die Zuordnung; existiert der Tag nicht, ist nichts zu tun.</summary>
        Public Shared Async Function RemoveTagFromAssetAsync(assetId As String, tagName As String, Optional cancellationToken As CancellationToken = Nothing) As Task(Of Boolean)
            If Not IsConfigured OrElse String.IsNullOrWhiteSpace(assetId) OrElse String.IsNullOrWhiteSpace(tagName) Then Return False
            Try
                Dim normalizedTag = tagName.Trim()
                If Not AppSettingsService.Load().ImmichStoreTagsInDescription Then
                    Dim tagId = Await FindTagIdAsync(normalizedTag, cancellationToken).ConfigureAwait(False)
                    If String.IsNullOrEmpty(tagId) Then Return True
                    Dim directOk = Await TagAssetsAsync(tagId, assetId, add:=False, cancellationToken:=cancellationToken).ConfigureAwait(False)
                    DiagnosticLogService.LogAlways("Immich.RemoveTag", $"'{normalizedTag}' (tagId={tagId}) ✗ asset {assetId}: {If(directOk, "OK", "FEHLGESCHLAGEN")}")
                    Await RefreshAssetDetailCacheAsync(assetId, $"nach RemoveTag '{normalizedTag}' ok={directOk}", cancellationToken).ConfigureAwait(False)
                    Return directOk
                End If

                Dim ok = Await UpdateFerrumPixDescriptionMetaAsync(assetId,
                    Sub(meta)
                        If meta.Tags Is Nothing Then meta.Tags = New List(Of String)()
                        meta.Tags.RemoveAll(Function(t) String.Equals(t, normalizedTag, StringComparison.OrdinalIgnoreCase))
                    End Sub,
                    $"nach RemoveTag '{normalizedTag}'",
                    cancellationToken).ConfigureAwait(False)
                DiagnosticLogService.LogAlways("Immich.RemoveTag", $"'{normalizedTag}' → description meta asset {assetId}: {If(ok, "OK", "FEHLGESCHLAGEN")}")
                Return ok
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch ex As Exception
                DiagnosticLogService.LogException("Immich.RemoveTag", ex)
                Return False
            End Try
        End Function

        Private Shared Function MatchesTag(t As ImmichTagDto, tagName As String) As Boolean
            Return t IsNot Nothing AndAlso Not String.IsNullOrEmpty(t.Id) AndAlso (
                String.Equals(t.Value, tagName, StringComparison.OrdinalIgnoreCase) OrElse
                String.Equals(t.Name, tagName, StringComparison.OrdinalIgnoreCase))
        End Function

        Private Shared Async Function UpsertTagIdAsync(tagName As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim client = GetClient()
            Dim body = "{""tags"":[" & JsonSerializer.Serialize(tagName) & "]}"
            Using content = New StringContent(body, Encoding.UTF8, "application/json")
                Using resp = Await client.PutAsync(ApiUrl("tags"), content, cancellationToken).ConfigureAwait(False)
                    resp.EnsureSuccessStatusCode()
                    Dim json = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                    Dim tags = JsonSerializer.Deserialize(Of List(Of ImmichTagDto))(json, JsonOptions)
                    If tags Is Nothing OrElse tags.Count = 0 Then Return Nothing
                    Dim match = tags.FirstOrDefault(Function(t) MatchesTag(t, tagName))
                    Return If(match, tags.First()).Id
                End Using
            End Using
        End Function

        Private Shared Async Function FindTagIdAsync(tagName As String, cancellationToken As CancellationToken) As Task(Of String)
            Dim client = GetClient()
            Using resp = Await client.GetAsync(ApiUrl("tags"), cancellationToken).ConfigureAwait(False)
                resp.EnsureSuccessStatusCode()
                Dim json = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                Dim tags = JsonSerializer.Deserialize(Of List(Of ImmichTagDto))(json, JsonOptions)
                Return tags?.FirstOrDefault(Function(t) MatchesTag(t, tagName))?.Id
            End Using
        End Function

        Private Shared Async Function TagAssetsAsync(tagId As String, assetId As String, add As Boolean, cancellationToken As CancellationToken) As Task(Of Boolean)
            Dim client = GetClient()
            Dim url = ApiUrl("tags/" & Uri.EscapeDataString(tagId) & "/assets")
            Dim body = "{""ids"":[" & JsonSerializer.Serialize(assetId) & "]}"
            Using req = New HttpRequestMessage(If(add, HttpMethod.Put, HttpMethod.Delete), url)
                req.Content = New StringContent(body, Encoding.UTF8, "application/json")
                Using resp = Await client.SendAsync(req, cancellationToken).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then
                        Dim errBody = Await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(False)
                        DiagnosticLogService.LogAlways("Immich.TagAssets", $"{If(add, "PUT", "DELETE")} {url} → HTTP {CInt(resp.StatusCode)} {errBody.Substring(0, Math.Min(300, errBody.Length))}")
                    End If
                    Return resp.IsSuccessStatusCode
                End Using
            End Using
        End Function

        Private Shared Async Function TryWriteCacheAsync(cachePath As String, bytes As Byte(), cancellationToken As CancellationToken) As Task
            Try
                Directory.CreateDirectory(IO.Path.GetDirectoryName(cachePath))
                Dim tempPath = cachePath & ".part"
                Await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(False)
                File.Move(tempPath, cachePath, overwrite:=True)
            Catch ex As OperationCanceledException When cancellationToken.IsCancellationRequested
                Throw
            Catch
            End Try
        End Function

        ''' <summary>Löscht den gesamten lokalen Immich-Thumbnail-Cache (Dateien). Gibt die Zahl der
        ''' gelöschten Dateien zurück.</summary>
        Public Shared Function ClearCache() As Integer
            If Not Directory.Exists(CacheRoot) Then Return 0
            Try
                Dim count = Directory.GetFiles(CacheRoot, "*", SearchOption.AllDirectories).Length
                Directory.Delete(CacheRoot, True)
                Return count
            Catch
                Return 0
            End Try
        End Function

        ''' <summary>Leert Thumbnail-Cache UND Metadaten-Index. Liefert (gelöschte Thumbnail-Dateien,
        ''' gelöschte Index-Einträge) für die Rückmeldung in den Einstellungen.</summary>
        Public Shared Function ClearAllCaches() As (Thumbnails As Integer, MetaEntries As Integer)
            Dim thumbs = ClearCache()
            Dim meta = ImmichIndexService.Instance.Clear()
            Return (thumbs, meta)
        End Function

        ''' <summary>Stabiler Kurzschlüssel des aktuellen Servers (aus der URL) - trennt Thumbnail-Cache
        ''' und Metadaten-Index nach Server, damit ein Serverwechsel nichts vermischt.</summary>
        Public Shared ReadOnly Property ServerKey As String
            Get
                Return ShortHash(NormalizeServerUrl(AppSettingsService.Load().ImmichServerUrl))
            End Get
        End Property

        Private Shared Function GetCacheFilePath(assetId As String, sizeKey As String) As String
            ' Nach Server getrennt ablegen, damit ein Serverwechsel keine fremden Thumbnails zeigt.
            Return IO.Path.Combine(CacheRoot, ServerKey, $"{SafeFileStem(assetId)}_{sizeKey}.img")
        End Function

        Private Shared Function SafeFileStem(value As String) As String
            ' Asset-IDs sind UUIDs (dateisystemsicher); zur Sicherheit dennoch säubern.
            Dim sb = New StringBuilder(value.Length)
            For Each ch In value
                sb.Append(If(Char.IsLetterOrDigit(ch) OrElse ch = "-"c, ch, "_"c))
            Next
            Return sb.ToString()
        End Function

        Private Shared Function ShortHash(value As String) As String
            Using sha = SHA1.Create()
                Dim bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(If(value, "")))
                Return String.Concat(bytes.Take(8).Select(Function(b) b.ToString("x2")))
            End Using
        End Function

        ' ---- Interne JSON-DTOs (Immich-Feldnamen, camelCase per PropertyNameCaseInsensitive) ----

        Private Class ImmichUserDto
            Public Property Name As String
            Public Property Email As String
        End Class

        Private Class ImmichAlbumDto
            Public Property Id As String
            Public Property AlbumName As String
            Public Property AssetCount As Integer
            Public Property AlbumThumbnailAssetId As String
        End Class

        Private Class ImmichUploadResponseDto
            Public Property Id As String
            ''' "created" oder "duplicate".
            Public Property Status As String
        End Class

        Private Class ImmichVersionDto
            Public Property Major As Integer
            Public Property Minor As Integer
            Public Property Patch As Integer
        End Class

        Private Class ImmichAlbumDetailDto
            Public Property Id As String
            Public Property AlbumName As String
            Public Property Assets As List(Of ImmichAssetDto)
        End Class

        Private Class ImmichPersonDto
            Public Property Id As String
            Public Property Name As String
            Public Property IsHidden As Boolean
        End Class

        Private Class ImmichPeopleResponseDto
            Public Property People As List(Of ImmichPersonDto)
            Public Property Total As Integer
        End Class

        Private Class ImmichExploreItemDto
            Public Property Value As String
        End Class

        Private Class ImmichCityAssetDto
            Public Property ExifInfo As ImmichCityExifDto
        End Class

        Private Class ImmichCityExifDto
            Public Property City As String
        End Class

        Private Class ImmichExploreFieldDto
            Public Property FieldName As String
            Public Property Items As List(Of ImmichExploreItemDto)
        End Class

        Private Class ImmichAssetDto
            Public Property Id As String
            Public Property Type As String
            ''' "timeline" | "archive" | "hidden" | "locked" (siehe IsBrowsableAsset).
            Public Property Visibility As String
            Public Property OriginalFileName As String
            Public Property FileCreatedAt As String
            Public Property UpdatedAt As String
            Public Property IsFavorite As Boolean
            ' Maße/Dauer liegen bei der Metadaten-Suche auf oberster Ebene (kein exifInfo), beim
            ' Detail-Abruf (GET /api/assets/{id}) zusätzlich in exifInfo.
            Public Property Width As Double?
            Public Property Height As Double?
            Public Property ExifInfo As ImmichExifDto
            Public Property Tags As List(Of ImmichTagDto)
        End Class

        ' Alle Zahlenfelder bewusst als Double? (nicht Int?): so liest der Deserializer sowohl
        ' Ganzzahlen als auch Fließkommazahlen, ohne bei einem einzelnen abweichenden Feld die
        ' gesamte Asset-Liste mit einer Exception zu verwerfen (Immich v3 hat Zahlentypen geändert).
        Private Class ImmichExifDto
            Public Property ExifImageWidth As Double?
            Public Property ExifImageHeight As Double?
            Public Property Make As String
            Public Property Model As String
            Public Property Iso As Double?
            Public Property FNumber As Double?
            Public Property DateTimeOriginal As String
            Public Property Description As String
            Public Property Rating As Double?
            Public Property FileSizeInByte As Double?
        End Class

        Private Class ImmichSearchResponseDto
            Public Property Assets As ImmichSearchAssetsDto
        End Class

        Private Class ImmichSearchAssetsDto
            Public Property Total As Integer
            Public Property Count As Integer
            Public Property Items As List(Of ImmichAssetDto)
            ''' Immich liefert die nächste Seite als String ("2") oder null, wenn keine folgt.
            Public Property NextPage As String
        End Class

        Private Class ImmichTagDto
            Public Property Id As String
            ''' Blattname des Tags (z.B. "Italien").
            Public Property Name As String
            ''' Vollständiger, hierarchischer Pfad (z.B. "Reise/Italien").
            Public Property Value As String
        End Class

    End Class

End Namespace
