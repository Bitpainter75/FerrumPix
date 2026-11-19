Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ''' <summary>Ein Eintrag im Index der Erweiterungen (docs/extensions/index.json).</summary>
    Public NotInheritable Class ExtensionCatalogEntry
        Public Property Id As String = ""
        ''' <summary>Der Dateiname unter docs/extensions, ohne Ordner.</summary>
        Public Property File As String = ""
        Public Property Version As Integer = 1
        Public Property Author As String = ""
        Public Property Names As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Public Property Descriptions As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Der Pull Request, mit dem sie kam: dort stehen Beschreibung und Bilder.
        ''' Leer bei den Beispielen der Anleitung.</summary>
        Public Property PullRequest As String = ""
        ''' <summary>SHA-256 der Datei, klein geschrieben. Der Download gilt nur, wenn er stimmt.</summary>
        Public Property Sha256 As String = ""
        ''' <summary>Ein Beispielbild unter docs/extensions/images, oft ein Ausschnitt aus dem Bild im
        ''' Pull Request; leer, wenn es keines gibt. Gilt nur mit <see cref="ImageSha256"/>.</summary>
        Public Property Image As String = ""
        Public Property ImageSha256 As String = ""

        Public ReadOnly Property HasImage As Boolean
            Get
                Return Image.Length > 0
            End Get
        End Property

        Public ReadOnly Property Name As String
            Get
                Return ExtensionService.Localized(Names, Id)
            End Get
        End Property

        Public ReadOnly Property Description As String
            Get
                Return ExtensionService.Localized(Descriptions, "")
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Der Weg vom Index der Erweiterungen im Repository zur Datei im Ordner des Nutzers.
    '''
    ''' DER INDEX IST DIE LISTE DESSEN, WAS ANGEBOTEN WIRD. Eine Erweiterung kommt als Pull Request
    ''' mit ihrer Datei und einer Beschreibung; nach dem Mergen bekommt sie einen Eintrag im Index
    ''' mit Kennung, Datei, Fassung, kurzer Beschreibung, dem Link zum Pull Request und der
    ''' Pruefsumme. Mitgeliefert wird keine; der Dialog "Erweiterungen herunterladen" liest den Index
    ''' und holt, was der Nutzer waehlt.
    '''
    ''' Gelesen wird der Hauptzweig, wie bei der Versionsabfrage (UpdateCheckService): dieselben
    ''' Dateien, die im Repository unter docs/extensions liegen, ohne eigenen Veroeffentlichungsweg.
    ''' Abgerufen wird nur auf die ausdrueckliche Geste hin, mitgeschickt wird nichts.
    '''
    ''' EINE DATEI GILT NUR MIT DER PRUEFSUMME AUS DEM INDEX, mit der Kennung aus dem Index und nach
    ''' derselben Pruefung wie beim Laden von Hand (ExtensionService.InstallDownloaded). Index und
    ''' Dateien liegen im selben Zweig; die Pruefsumme haelt beide zusammen, so dass eine spaeter
    ''' geaenderte Datei ohne neuen Eintrag nicht still eine andere Rechnung bringt. Der Pruefstand
    ''' haelt Index und Dateien im Repository deckungsgleich.
    ''' </summary>
    Public NotInheritable Class ExtensionCatalogService

        Private Sub New()
        End Sub

        Public Const BaseAddress As String = "https://raw.githubusercontent.com/Bitpainter75/FerrumPix/main/docs/extensions/"
        Public Const IndexFileName As String = "index.json"
        ''' <summary>Fassung des Index-Formats, die diese Anwendung liest.</summary>
        Public Const IndexFormat As Integer = 1
        Private Const MaxIndexBytes As Integer = 512 * 1024
        Private Const MaxEntries As Integer = 500

        Private Shared ReadOnly FileNamePattern As New Regex("^[a-z0-9]+([.-][a-z0-9]+)+\.json$", RegexOptions.CultureInvariant)
        Private Shared ReadOnly ShaPattern As New Regex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
        Private Shared ReadOnly ImagePattern As New Regex("^images/[a-z0-9]+([.-][a-z0-9]+)*\.(jpg|png)$", RegexOptions.CultureInvariant)
        Private Const MaxImageBytes As Integer = 1024 * 1024
        Private Shared ReadOnly PullRequestPattern As New Regex("^https://github\.com/Bitpainter75/FerrumPix/pull/[0-9]+$", RegexOptions.CultureInvariant)

        ''' <summary>Fuer den Pruefstand: liefert die Bytes zu einer Adresse statt des Netzes.</summary>
        Friend Shared Property FetchOverride As Func(Of String, Byte())

        Private Shared ReadOnly _client As New Lazy(Of HttpClient)(
            Function()
                Dim client = New HttpClient()
                client.Timeout = TimeSpan.FromSeconds(30)
                client.DefaultRequestHeaders.UserAgent.ParseAdd("FerrumPix")
                ' Nach dem Mergen soll der neue Eintrag gleich da sein, nicht erst, wenn ein
                ' Zwischenspeicher auf dem Weg ihn freigibt.
                client.DefaultRequestHeaders.CacheControl = New Headers.CacheControlHeaderValue With {.NoCache = True}
                Return client
            End Function)

        Private Shared Async Function FetchAsync(address As String, maxBytes As Integer, cancel As CancellationToken) As Task(Of Byte())
            Dim override = FetchOverride
            If override IsNot Nothing Then Return override(address)
            Using response = Await _client.Value.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancel)
                response.EnsureSuccessStatusCode()
                Dim length = response.Content.Headers.ContentLength
                If length.HasValue AndAlso length.Value > maxBytes Then Throw New InvalidOperationException("the file is too large")
                ' Die Angabe der Laenge kann fehlen oder falsch sein: gelesen wird deshalb der
                ' Datenstrom selbst, und zwar nur bis zur Grenze.
                Using body = Await response.Content.ReadAsStreamAsync(cancel)
                    Return Await ReadLimitedAsync(body, maxBytes, cancel)
                End Using
            End Using
        End Function

        ''' <summary>Liest einen Datenstrom bis zu seinem Ende, aber hoechstens <paramref name="maxBytes"/>
        ''' Bytes. Kommt mehr, wirft es, sobald die Grenze ueberschritten ist, statt den Rest erst
        ''' in den Speicher zu holen: ein Server ohne oder mit falscher Laengenangabe koennte sonst
        ''' beliebig viel puffern lassen.</summary>
        Friend Shared Async Function ReadLimitedAsync(body As IO.Stream, maxBytes As Integer, cancel As CancellationToken) As Task(Of Byte())
            Dim buffer(81919) As Byte
            Using collected As New IO.MemoryStream()
                Do
                    ' Ohne Rueckkehr in den Kontext des Aufrufers: der Rest der Schleife braucht ihn
                    ' nicht, und ein Aufrufer, der blockierend wartet, verklemmte sich sonst.
                    Dim read = Await body.ReadAsync(buffer.AsMemory(0, buffer.Length), cancel).ConfigureAwait(False)
                    If read <= 0 Then Exit Do
                    If collected.Length + read > maxBytes Then Throw New InvalidOperationException("the file is too large")
                    collected.Write(buffer, 0, read)
                Loop
                Return collected.ToArray()
            End Using
        End Function

        ''' <summary>Holt und liest den Index. Wirft bei einem Netz- oder Formatfehler; die Meldung
        ''' zeigt der Dialog.</summary>
        Public Shared Async Function FetchIndexAsync(Optional cancel As CancellationToken = Nothing) As Task(Of List(Of ExtensionCatalogEntry))
            Dim bytes = Await FetchAsync(BaseAddress & IndexFileName, MaxIndexBytes, cancel)
            Dim entries As List(Of ExtensionCatalogEntry) = Nothing
            Dim problem = ParseIndex(Encoding.UTF8.GetString(bytes), entries)
            If problem.Length > 0 Then Throw New IO.InvalidDataException(problem)
            Return entries
        End Function

        ''' <summary>Holt eine Erweiterung, prueft Pruefsumme, Kennung und Inhalt und nimmt sie in
        ''' den Ordner des Nutzers auf.</summary>
        Public Shared Async Function DownloadAsync(entry As ExtensionCatalogEntry, Optional cancel As CancellationToken = Nothing) As Task(Of ExtensionLoadResult)
            If entry Is Nothing Then Return New ExtensionLoadResult With {.ErrorText = "no entry"}
            Dim bytes As Byte()
            Try
                bytes = Await FetchAsync(BaseAddress & entry.File, ExtensionService.MaxFileSize, cancel)
            Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                Return New ExtensionLoadResult With {.FilePath = entry.File, .ErrorText = ex.Message}
            End Try
            If Not String.Equals(Sha256Of(bytes), entry.Sha256, StringComparison.Ordinal) Then
                Return New ExtensionLoadResult With {.FilePath = entry.File, .ErrorText = "the file does not match the checksum in the index"}
            End If
            Dim result = ExtensionService.InstallDownloaded(Encoding.UTF8.GetString(bytes), entry.Id)
            If result.FilePath.Length = 0 Then result.FilePath = entry.File
            Return result
        End Function

        ''' <summary>Holt das Beispielbild eines Eintrags. Nothing, wenn es keines gibt, der Abruf
        ''' scheitert oder die Pruefsumme nicht stimmt: ein fehlendes Bild ist kein Fehler, der Eintrag
        ''' steht dann ohne.</summary>
        Public Shared Async Function FetchImageAsync(entry As ExtensionCatalogEntry, Optional cancel As CancellationToken = Nothing) As Task(Of Byte())
            If entry Is Nothing OrElse Not entry.HasImage Then Return Nothing
            Try
                Dim bytes = Await FetchAsync(BaseAddress & entry.Image, MaxImageBytes, cancel)
                If String.Equals(Sha256Of(bytes), entry.ImageSha256, StringComparison.Ordinal) Then Return bytes
                DiagnosticLogService.LogAlways("Extensions.Image", entry.Id & ": checksum does not match")
            Catch ex As Exception When Not TypeOf ex Is OperationCanceledException
                DiagnosticLogService.LogAlways("Extensions.Image", entry.Id & ": " & ex.Message)
            End Try
            Return Nothing
        End Function

        Friend Shared Function Sha256Of(bytes As Byte()) As String
            Return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()
        End Function

        ''' <summary>Was der Dialog zu einem Eintrag sagt: nicht da, da, oder eine neuere Fassung im
        ''' Index. Eine Erweiterung, die neben dem Programm liegt, zaehlt als vorhanden.</summary>
        Public Enum InstallState
            NotInstalled
            Installed
            UpdateAvailable
        End Enum

        Public Shared Function StateOf(entry As ExtensionCatalogEntry) As InstallState
            Dim installed = ExtensionService.Find(entry.Id)
            If installed Is Nothing Then Return InstallState.NotInstalled
            Return If(installed.Version < entry.Version, InstallState.UpdateAvailable, InstallState.Installed)
        End Function

        ''' <summary>Liest den Index. Ein Fehler im Aufbau macht den ganzen Index unbrauchbar, ein
        ''' Eintrag mit Fehler nicht: er faellt weg, damit ein einzelner Fehler nicht alle anderen
        ''' Erweiterungen sperrt. Der Pruefstand verlangt einen fehlerfreien Index.</summary>
        ''' <returns>Leer, oder der Grund, warum der Index nicht taugt.</returns>
        Public Shared Function ParseIndex(json As String, ByRef entries As List(Of ExtensionCatalogEntry),
                                          Optional skipped As List(Of String) = Nothing) As String
            entries = New List(Of ExtensionCatalogEntry)()
            Try
                Using document = JsonDocument.Parse(If(json, ""), New JsonDocumentOptions With {.MaxDepth = 16})
                    Dim root = document.RootElement
                    If root.ValueKind <> JsonValueKind.Object Then Return "the index is not an object"
                    Dim format As JsonElement
                    If Not root.TryGetProperty("format", format) OrElse format.ValueKind <> JsonValueKind.Number OrElse
                       Not format.TryGetInt32(Nothing) Then Return "the index has no format"
                    If format.GetInt32() > IndexFormat Then Return $"the index has format {format.GetInt32()}, this version reads {IndexFormat}"
                    Dim list As JsonElement
                    If Not root.TryGetProperty("extensions", list) OrElse list.ValueKind <> JsonValueKind.Array Then Return "the index has no list of extensions"
                    For Each item In list.EnumerateArray().Take(MaxEntries)
                        Dim entry As ExtensionCatalogEntry = Nothing
                        Dim problem = ParseEntry(item, entry)
                        If problem.Length > 0 Then
                            skipped?.Add(problem)
                            Continue For
                        End If
                        If entries.Any(Function(e) String.Equals(e.Id, entry.Id, StringComparison.Ordinal)) Then
                            skipped?.Add(entry.Id & ": listed twice")
                            Continue For
                        End If
                        entries.Add(entry)
                    Next
                End Using
            Catch ex As JsonException
                Return "the index is not valid JSON: " & ex.Message
            End Try
            Return ""
        End Function

        Private Shared Function ParseEntry(item As JsonElement, ByRef entry As ExtensionCatalogEntry) As String
            entry = Nothing
            If item.ValueKind <> JsonValueKind.Object Then Return "an entry is not an object"
            Dim result As New ExtensionCatalogEntry With {
                .Id = Text(item, "id"), .File = Text(item, "file"), .Author = Text(item, "author"),
                .PullRequest = Text(item, "pullRequest"), .Sha256 = Text(item, "sha256").ToLowerInvariant()}
            If Not ExtensionService.IsValidId(result.Id) Then Return $"invalid id ""{result.Id}"""
            If Not FileNamePattern.IsMatch(result.File) OrElse
               String.Equals(result.File, IndexFileName, StringComparison.Ordinal) Then Return result.Id & ": invalid file name"
            If Not ShaPattern.IsMatch(result.Sha256) Then Return result.Id & ": invalid sha256"
            result.Image = Text(item, "image")
            result.ImageSha256 = Text(item, "imageSha256").ToLowerInvariant()
            If result.Image.Length > 0 AndAlso (Not ImagePattern.IsMatch(result.Image) OrElse Not ShaPattern.IsMatch(result.ImageSha256)) Then
                Return result.Id & ": invalid image"
            End If
            If result.PullRequest.Length > 0 AndAlso Not PullRequestPattern.IsMatch(result.PullRequest) Then
                Return result.Id & ": the pull request is not a pull request of FerrumPix"
            End If
            Dim version As JsonElement
            If Not item.TryGetProperty("version", version) OrElse version.ValueKind <> JsonValueKind.Number OrElse
               Not version.TryGetInt32(result.Version) OrElse result.Version < 1 Then Return result.Id & ": invalid version"
            If Not ReadTexts(item, "name", result.Names) OrElse result.Names.Count = 0 Then Return result.Id & ": no name"
            If Not ReadTexts(item, "description", result.Descriptions) Then Return result.Id & ": invalid description"
            entry = result
            Return ""
        End Function

        Private Shared Function Text(parent As JsonElement, name As String) As String
            Dim element As JsonElement
            If parent.TryGetProperty(name, element) AndAlso element.ValueKind = JsonValueKind.String Then
                Return If(element.GetString(), "").Trim()
            End If
            Return ""
        End Function

        ''' <summary>Ein Text je Sprache, wie in der Erweiterungsdatei selbst: {"en": "...", "de": "..."}.
        ''' Fehlt das Feld, bleibt die Liste leer.</summary>
        Private Shared Function ReadTexts(parent As JsonElement, name As String, target As Dictionary(Of String, String)) As Boolean
            Dim element As JsonElement
            If Not parent.TryGetProperty(name, element) Then Return True
            If element.ValueKind <> JsonValueKind.Object Then Return False
            For Each pair In element.EnumerateObject()
                If pair.Value.ValueKind <> JsonValueKind.String Then Return False
                Dim value = If(pair.Value.GetString(), "").Trim()
                If value.Length > 0 AndAlso value.Length <= 1000 Then target(pair.Name) = value
            Next
            Return True
        End Function

    End Class

End Namespace
