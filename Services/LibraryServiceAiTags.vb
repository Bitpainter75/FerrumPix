Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports Microsoft.Data.Sqlite

Namespace Services

    ''' <summary>Ein von einem lokalen Modell erkannter Bildinhalt. Er ist bewusst KEIN Eintrag in
    ''' ImageMeta.Tags: manuelle/importierte Stichwörter und Modelltreffer haben eine andere Herkunft,
    ''' andere Bearbeitungsregeln und werden im Infopanel getrennt gezeigt.</summary>
    Public Class AiImageTag
        Public Property Canonical As String = ""
        Public Property Score As Single
        Public Property ModelKey As String = ""
        Public Property ModelVersion As String = ""

        Public ReadOnly Property DisplayText As String
            Get
                Return AiTagLocalizationService.Display(Canonical)
            End Get
        End Property

        Public ReadOnly Property ScoreText As String
            Get
                Return Score.ToString("P0", CultureInfo.CurrentCulture)
            End Get
        End Property
    End Class

    Partial Public Class LibraryService

        ''' <summary>Gibt es ueberhaupt erkannte Stichwoerter? Gemerkt, weil zwei heisse Pfade sonst
        ''' je Datei - beim Stichwortimport sogar je Stichwort - eine Abfrage stellten, und zwar
        ''' auch dann, wenn das Modell nie installiert und die Funktion nie eingeschaltet war. Bei
        ''' leerer Tabelle ist die Antwort immer dieselbe, und dann kostet der Weg gar nichts.</summary>
        Private Shared _anyAiTags As Boolean?
        Private Shared ReadOnly _anyAiTagsLock As New Object()

        Private Function HasAnyAiTags() As Boolean
            SyncLock _anyAiTagsLock
                If _anyAiTags.HasValue Then Return _anyAiTags.Value
            End SyncLock
            Dim found As Boolean
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT 1 FROM AiImageTag LIMIT 1"
                        found = cmd.ExecuteScalar() IsNot Nothing
                    End Using
                End Using
            Catch
                ' Im Zweifel den teuren Weg gehen: lieber eine Abfrage zu viel als ein Stichwort,
                ' das faelschlich als Handarbeit im Katalog landet.
                Return True
            End Try
            SyncLock _anyAiTagsLock
                _anyAiTags = found
            End SyncLock
            Return found
        End Function

        ''' <summary>Nach jedem Schreiben: der gemerkte Stand kann nicht mehr stimmen.</summary>
        Private Shared Sub InvalidateAiTagPresence()
            SyncLock _anyAiTagsLock
                _anyAiTags = Nothing
            End SyncLock
        End Sub

        ''' <summary>Eine eigene Tabelle statt einer JSON-Spalte: die Suche braucht kanonische
        ''' Begriffe, die Anzeige Sprache und Score. Beides waere in ImageMeta.Tags nicht mehr
        ''' unterscheidbar und würde beim XMP-Import als Handarbeit zurückkehren.</summary>
        Private Shared Sub EnsureAiTagTables(conn As SqliteConnection)
            Using cmd = conn.CreateCommand()
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS AiImageTag (" &
                    "  FilePath TEXT NOT NULL," &
                    "  Canonical TEXT NOT NULL COLLATE NOCASE," &
                    "  Score REAL NOT NULL," &
                    "  ModelKey TEXT NOT NULL DEFAULT ''," &
                    "  ModelVersion TEXT NOT NULL DEFAULT ''," &
                    "  SourceModifiedAt TEXT NOT NULL DEFAULT ''," &
                    "  GeneratedAt TEXT NOT NULL DEFAULT ''," &
                    "  PRIMARY KEY(FilePath, Canonical)" &
                    ");" &
                    "CREATE INDEX IF NOT EXISTS IX_AiImageTag_Canonical ON AiImageTag(Canonical);" &
                    "CREATE TABLE IF NOT EXISTS AiTagScan (" &
                    "  FilePath TEXT PRIMARY KEY," &
                    "  ModelKey TEXT NOT NULL DEFAULT ''," &
                    "  ModelVersion TEXT NOT NULL DEFAULT ''," &
                    "  SourceModifiedAt TEXT NOT NULL DEFAULT ''," &
                    "  GeneratedAt TEXT NOT NULL DEFAULT ''" &
                    ");"
                cmd.ExecuteNonQuery()
            End Using
        End Sub

        ''' <summary>Ersetzt den Modellstand eines Bildes atomar. Treffer, die beim neuen Lauf nicht
        ''' mehr vorkommen, bleiben damit nicht als Geisterstichwörter zurück.</summary>
        Public Sub ReplaceAiTags(filePath As String, tags As IEnumerable(Of AiImageTag),
                                 sourceModifiedAt As String, modelKey As String, modelVersion As String)
            If Not IsCatalogWritable(filePath) Then Return
            Dim clean = If(tags, Enumerable.Empty(Of AiImageTag)()).
                Where(Function(t) t IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(t.Canonical)).
                GroupBy(Function(t) t.Canonical.Trim(), StringComparer.OrdinalIgnoreCase).
                Select(Function(g) g.OrderByDescending(Function(t) t.Score).First()).ToList()
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using tx = conn.BeginTransaction()
                        Using remove = conn.CreateCommand()
                            remove.Transaction = tx
                            remove.CommandText = "DELETE FROM AiImageTag WHERE FilePath=$p"
                            remove.Parameters.AddWithValue("$p", filePath)
                            remove.ExecuteNonQuery()
                        End Using
                        For Each tag In clean
                            Using insert = conn.CreateCommand()
                                insert.Transaction = tx
                                insert.CommandText =
                                    "INSERT INTO AiImageTag(FilePath,Canonical,Score,ModelKey,ModelVersion,SourceModifiedAt,GeneratedAt) " &
                                    "VALUES($p,$c,$s,$k,$v,$m,$g)"
                                insert.Parameters.AddWithValue("$p", filePath)
                                insert.Parameters.AddWithValue("$c", tag.Canonical.Trim())
                                insert.Parameters.AddWithValue("$s", tag.Score)
                                insert.Parameters.AddWithValue("$k", If(modelKey, ""))
                                insert.Parameters.AddWithValue("$v", If(modelVersion, ""))
                                insert.Parameters.AddWithValue("$m", If(sourceModifiedAt, ""))
                                insert.Parameters.AddWithValue("$g", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                                insert.ExecuteNonQuery()
                            End Using
                        Next
                        Using scan = conn.CreateCommand()
                            scan.Transaction = tx
                            scan.CommandText =
                                "INSERT INTO AiTagScan(FilePath,ModelKey,ModelVersion,SourceModifiedAt,GeneratedAt) " &
                                "VALUES($p,$k,$v,$m,$g) ON CONFLICT(FilePath) DO UPDATE SET " &
                                "ModelKey=$k,ModelVersion=$v,SourceModifiedAt=$m,GeneratedAt=$g"
                            scan.Parameters.AddWithValue("$p", filePath)
                            scan.Parameters.AddWithValue("$k", If(modelKey, ""))
                            scan.Parameters.AddWithValue("$v", If(modelVersion, ""))
                            scan.Parameters.AddWithValue("$m", If(sourceModifiedAt, ""))
                            scan.Parameters.AddWithValue("$g", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
                            scan.ExecuteNonQuery()
                        End Using
                        tx.Commit()
                    End Using
                End Using
                InvalidateAiTagPresence()
            Catch ex As Exception
                DiagnosticLogService.LogException("Bibliothek.KIStichwörter", ex)
            End Try
        End Sub

        Public Function GetAiTags(filePath As String) As List(Of AiImageTag)
            Dim result As New List(Of AiImageTag)()
            If String.IsNullOrWhiteSpace(filePath) Then Return result
            Dim assetId As String = Nothing, fileName As String = Nothing
            If ImmichService.TryParsePseudoPath(filePath, assetId, fileName) Then
                Return ImmichIndexService.Instance.GetAiTags(ImmichService.ServerKey, assetId)
            End If
            If NextcloudService.TryParsePseudoPath(filePath, assetId, fileName) Then
                Return NextcloudIndexService.Instance.GetAiTags(NextcloudService.ServerKey, assetId)
            End If
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText =
                            "SELECT Canonical,Score,ModelKey,ModelVersion FROM AiImageTag WHERE FilePath=$p " &
                            "ORDER BY Score DESC, Canonical COLLATE NOCASE"
                        cmd.Parameters.AddWithValue("$p", filePath)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                result.Add(New AiImageTag With {
                                    .Canonical = reader.GetString(0), .Score = CSng(reader.GetDouble(1)),
                                    .ModelKey = reader.GetString(2), .ModelVersion = reader.GetString(3)})
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Bibliothek.KIStichwörterLesen", ex)
            End Try
            Return result
        End Function

        Public Function GetAiTagCounts() As List(Of (Canonical As String, Count As Integer))
            Dim result As New List(Of (Canonical As String, Count As Integer))()
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT Canonical,COUNT(DISTINCT FilePath) FROM AiImageTag GROUP BY Canonical ORDER BY Canonical COLLATE NOCASE"
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                result.Add((reader.GetString(0), reader.GetInt32(1)))
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Bibliothek.KIStichwortZahlen", ex)
            End Try
            For Each serverCount In ImmichIndexService.Instance.GetAiTagCounts().Concat(NextcloudIndexService.Instance.GetAiTagCounts())
                Dim existing = result.FindIndex(Function(x) String.Equals(x.Canonical, serverCount.Canonical, StringComparison.OrdinalIgnoreCase))
                If existing >= 0 Then result(existing) = (result(existing).Canonical, result(existing).Count + serverCount.Count) Else result.Add(serverCount)
            Next
            Return result.OrderBy(Function(x) x.Canonical, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ''' <summary>Traegt dieses Bild einen erkannten Begriff, den der Filter meint? Laeuft je Bild
        ''' des durchsuchten Bestands, deshalb EINE indizierte Abfrage mit LIMIT 1 statt aller
        ''' Treffer der Datei - und bei leerer Tabelle gar keine.</summary>
        Public Function MatchesAiTagQuery(filePath As String, queries As IEnumerable(Of String)) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Dim wanted = If(queries, Enumerable.Empty(Of String)()).
                SelectMany(Function(query) AiTagLocalizationService.CanonicalsForQuery(query)).
                Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            If wanted.Count = 0 Then Return False
            ' Serverpfade liegen in getrennten SQLite-Indizes. Der zentrale Leser verteilt nach
            ' Pseudo-Schema und vermeidet damit eine Datenbankabfrage gegen den falschen Katalog.
            Dim remoteId As String = Nothing, remoteName As String = Nothing
            If ImmichService.TryParsePseudoPath(filePath, remoteId, remoteName) OrElse NextcloudService.TryParsePseudoPath(filePath, remoteId, remoteName) Then
                Return GetAiTags(filePath).Any(Function(tag) wanted.Contains(tag.Canonical, StringComparer.OrdinalIgnoreCase))
            End If
            If Not HasAnyAiTags() Then Return False
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        Dim slots As New List(Of String)()
                        For index = 0 To wanted.Count - 1
                            Dim parameter = "$c" & index.ToString(CultureInfo.InvariantCulture)
                            slots.Add(parameter)
                            cmd.Parameters.AddWithValue(parameter, wanted(index))
                        Next
                        cmd.CommandText = "SELECT 1 FROM AiImageTag WHERE FilePath=$p AND Canonical IN (" &
                                          String.Join(",", slots) & ") LIMIT 1"
                        cmd.Parameters.AddWithValue("$p", filePath)
                        Return cmd.ExecuteScalar() IsNot Nothing
                    End Using
                End Using
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Nimmt die Stichwoerter heraus, die auf diesem Bild von uns selbst stammen.
        '''
        ''' Ein von uns nach XMP geschriebener KI-Begriff kommt beim naechsten Metadatenimport
        ''' zurueck; ohne diesen Filter kippte er in die manuelle Tags-Spalte, und der abgegrenzte
        ''' Bereich im Infopanel waere nach einem Neustart nur noch Schein.
        '''
        ''' EINE Abfrage je Datei, nicht je Stichwort: der Import ist ein heisser Pfad des
        ''' Katalogindex. Bei leerer Tabelle gar keine - dann kann es nichts herauszunehmen geben.
        '''
        ''' Wer ein Wort von Hand vergibt, das die Erkennung auf demselben Bild ebenfalls gefunden
        ''' hat, findet es danach im KI-Bereich statt bei seinen eigenen. In XMP sind die beiden
        ''' nicht auseinanderzuhalten; auffindbar bleibt das Bild ueber dasselbe Wort so oder so.</summary>
        Public Function WithoutOwnAiTags(filePath As String, keywords As List(Of String)) As List(Of String)
            If keywords Is Nothing OrElse keywords.Count = 0 Then Return keywords
            If String.IsNullOrWhiteSpace(filePath) OrElse Not HasAnyAiTags() Then Return keywords
            Dim known As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT Canonical FROM AiImageTag WHERE FilePath=$p"
                        cmd.Parameters.AddWithValue("$p", filePath)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                known.Add(reader.GetString(0))
                            End While
                        End Using
                    End Using
                End Using
            Catch
                Return keywords
            End Try
            If known.Count = 0 Then Return keywords
            Return keywords.Where(Function(k) Not known.Contains(If(k, "").Trim())).ToList()
        End Function

        ''' <summary>Schreibt die kanonischen KI-Begriffe nur auf ausdrücklichen Wunsch in XMP. Sie
        ''' bleiben intern dennoch in AiImageTag getrennt, damit Infopanel und Filter ihre Herkunft
        ''' nicht verlieren. Eine fehlende .xmp wird nur angelegt, wenn die bereits vorhandene
        ''' Katalog-XMP-Option dies ebenfalls erlaubt.</summary>
        Public Function SyncAiTagsToXmp(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Try
                Dim settings = AppSettingsService.Load()
                If Not settings.WriteAiTagsToXmp Then Return False
                Dim tags = GetTags(filePath).Concat(GetAiTags(filePath).Select(Function(t) t.Canonical)).
                    Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                Dim labelWord = XmpSidecarService.LabelToXmpWord(GetColorLabel(filePath))
                Return ExifService.WriteXmpCatalogSidecar(filePath, GetRating(filePath), labelWord, tags,
                                                          settings.CreateXmpSidecarIfMissing)
            Catch ex As Exception
                DiagnosticLogService.LogException("Bibliothek.KIStichwörterXmp", ex)
                Return False
            End Try
        End Function

        ''' <summary>Holt das optionale XMP-Schreiben für bereits analysierte lokale Bilder nach.
        ''' Die Analyse bleibt dabei unangetastet: es werden nur die bereits gespeicherten
        ''' kanonischen Begriffe in vorhandene bzw. ausdrücklich erlaubte Sidecars übertragen.</summary>
        Public Function SyncAllAiTagsToXmp(Optional cancel As Threading.CancellationToken = Nothing) As Integer
            If Not AppSettingsService.Load().WriteAiTagsToXmp Then Return 0
            Dim paths As New List(Of String)()
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT DISTINCT FilePath FROM AiImageTag"
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                paths.Add(reader.GetString(0))
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Bibliothek.KIStichwörterXmpAlleLesen", ex)
                Return 0
            End Try

            Dim synced = 0
            For Each filePath In paths
                cancel.ThrowIfCancellationRequested()
                If IsServerPseudoPath(filePath) OrElse Not File.Exists(filePath) Then Continue For
                If SyncAiTagsToXmp(filePath) Then synced += 1
            Next
            Return synced
        End Function

        Public Function AiTagsNeedRefresh(filePath As String, sourceModifiedAt As String,
                                          modelKey As String, modelVersion As String) As Boolean
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText =
                            "SELECT SourceModifiedAt,ModelKey,ModelVersion FROM AiTagScan WHERE FilePath=$p LIMIT 1"
                        cmd.Parameters.AddWithValue("$p", filePath)
                        Using reader = cmd.ExecuteReader()
                            If Not reader.Read() Then Return True
                            Return Not String.Equals(reader.GetString(0), If(sourceModifiedAt, ""), StringComparison.Ordinal) OrElse
                                   Not String.Equals(reader.GetString(1), If(modelKey, ""), StringComparison.Ordinal) OrElse
                                   Not String.Equals(reader.GetString(2), If(modelVersion, ""), StringComparison.Ordinal)
                        End Using
                    End Using
                End Using
            Catch
                Return True
            End Try
        End Function

    End Class

End Namespace
