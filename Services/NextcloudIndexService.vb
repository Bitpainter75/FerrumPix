Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports Microsoft.Data.Sqlite

Namespace Services

    ''' <summary>
    ''' Lokaler Metadaten-Index für Nextcloud-Aufnahmen, in einer eigenen SQLite-Datei. Das
    ''' Gegenstück zu <see cref="ImmichIndexService"/> und aus demselben Grund getrennt vom lokalen
    ''' Bild-Katalog: ein Server- oder Kontowechsel darf nichts vermischen.
    '''
    ''' WOFÜR: Die Einzelheiten einer Aufnahme (Stichwörter, Größe, Pfad, Rechte, Aufnahmezeit) holt
    ''' <see cref="NextcloudService.GetInfoAsync"/> je Datei einzeln vom Server. Bisher geschah das
    ''' in JEDER Sitzung neu - für Immich lag dasselbe längst auf der Platte, für Nextcloud gab es
    ''' überhaupt keinen Index. Damit stehen Stichwörter und Aufnahmezeit beim Öffnen sofort, ohne
    ''' eine einzige Anfrage.
    '''
    ''' DAS SCHEMA IST DAS DES IMMICH-INDEX, auch wo dieser Server weniger füllt. Kamera, ISO,
    ''' Blende, Objektiv und Brennweite bleiben hier meist leer: der EXIF-Block von Memories ist je
    ''' nach Einrichtung fast leer (er braucht ein eingerichtetes exiftool), und die Aufnahmedaten
    ''' liest FerrumPix deshalb aus der geholten Originaldatei. Die Spalten stehen trotzdem, damit
    ''' beide Server dasselbe Modell füllen und ein später nachgereichter Wert keinen Umbau braucht.
    '''
    ''' INVALIDIERT WIRD ÜBER DEN ETAG, nicht über die Änderungszeit: der Etag ist der Wert, den die
    ''' Listenabfrage ohnehin mitbringt, und derselbe, der schon den Namen der abgelegten Kachel
    ''' bestimmt (siehe <c>ImageItem</c>). Zwei Invalidierungsschlüssel für dieselbe Datei liefen
    ''' garantiert auseinander.
    ''' </summary>
    Public NotInheritable Class NextcloudIndexService

        Private Shared _instance As NextcloudIndexService
        Private ReadOnly _connectionString As String
        Private ReadOnly _dbPath As String

        Public Shared ReadOnly Property Instance As NextcloudIndexService
            Get
                If _instance Is Nothing Then _instance = New NextcloudIndexService()
                Return _instance
            End Get
        End Property

        Private Sub New()
            Dim dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix")
            Directory.CreateDirectory(dir)
            _dbPath = Path.Combine(dir, "nextcloud-index.db")
            _connectionString = $"Data Source={_dbPath}"
            Try
                InitDb()
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.Init", ex)
            End Try
        End Sub

        Private Sub InitDb()
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using pragmaCmd = conn.CreateCommand()
                    pragmaCmd.CommandText = "PRAGMA journal_mode=WAL"
                    pragmaCmd.ExecuteNonQuery()
                End Using
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "CREATE TABLE IF NOT EXISTS PhotoMeta (" &
                        "  ServerKey  TEXT NOT NULL," &
                        "  FileId     TEXT NOT NULL," &
                        "  Etag       TEXT NOT NULL DEFAULT ''," &
                        "  FileSize   INTEGER NOT NULL DEFAULT 0," &
                        "  FilePath   TEXT," &
                        "  Permissions TEXT," &
                        "  Tags       TEXT NOT NULL DEFAULT ''," &
                        "  DateTaken  TEXT," &
                        "  Camera     TEXT," &
                        "  Iso        INTEGER," &
                        "  Aperture   REAL," &
                        "  Lens       TEXT," &
                        "  FocalLengthMm REAL," &
                        "  ShutterSpeed TEXT," &
                        "  GpsLatitude REAL," &
                        "  GpsLongitude REAL," &
                        "  Width      INTEGER NOT NULL DEFAULT 0," &
                        "  Height     INTEGER NOT NULL DEFAULT 0," &
                        "  PRIMARY KEY (ServerKey, FileId)" &
                        ")"
                    cmd.ExecuteNonQuery()
                End Using
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "CREATE TABLE IF NOT EXISTS AiPhotoScan (ServerKey TEXT NOT NULL, FileId TEXT NOT NULL, SourceVersion TEXT NOT NULL DEFAULT '', ModelKey TEXT NOT NULL DEFAULT '', ModelVersion TEXT NOT NULL DEFAULT '', PRIMARY KEY(ServerKey,FileId))"
                    cmd.ExecuteNonQuery()
                End Using
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "CREATE TABLE IF NOT EXISTS XmpSidecarSync (ServerKey TEXT NOT NULL, FileId TEXT NOT NULL, SourceSignature TEXT NOT NULL DEFAULT '', PRIMARY KEY(ServerKey,FileId))"
                    cmd.ExecuteNonQuery()
                End Using
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "CREATE TABLE IF NOT EXISTS AiPhotoTag (" &
                        " ServerKey TEXT NOT NULL, FileId TEXT NOT NULL, Canonical TEXT NOT NULL COLLATE NOCASE," &
                        " Score REAL NOT NULL, ModelKey TEXT NOT NULL DEFAULT '', ModelVersion TEXT NOT NULL DEFAULT ''," &
                        " SourceVersion TEXT NOT NULL DEFAULT '', PRIMARY KEY(ServerKey,FileId,Canonical))"
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>Der gespeicherte Stand, wenn er zum Etag des Servers passt - sonst Nothing.
        ''' Der Aufrufer holt dann neu und überschreibt den Eintrag.</summary>
        Public Function TryGet(serverKey As String, fileId As String, etag As String) As NextcloudService.NextcloudPhoto
            If String.IsNullOrEmpty(serverKey) OrElse String.IsNullOrEmpty(fileId) Then Return Nothing
            ' OHNE ETAG KEIN TREFFER. Ein leerer Vergleichswert würde auf einen leer gespeicherten
            ' Etag passen, und der Eintrag gälte für immer als frisch - genau die Falle, die ein
            ' Zwischenspeicher ohne Invalidierung ist.
            If String.IsNullOrEmpty(etag) Then Return Nothing
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT Etag,FileSize,FilePath,Permissions,Tags,DateTaken " &
                                          "FROM PhotoMeta WHERE ServerKey=$s AND FileId=$f"
                        cmd.Parameters.AddWithValue("$s", serverKey)
                        cmd.Parameters.AddWithValue("$f", fileId)
                        Using r = cmd.ExecuteReader()
                            If Not r.Read() Then Return Nothing
                            Dim storedEtag = If(r.IsDBNull(0), "", r.GetString(0))
                            If Not String.Equals(storedEtag, etag, StringComparison.Ordinal) Then Return Nothing
                            Dim photo As New NextcloudService.NextcloudPhoto()
                            photo.FileId = ParseFileId(fileId)
                            photo.Size = If(r.IsDBNull(1), 0L, r.GetInt64(1))
                            photo.FileName = If(r.IsDBNull(2), "", r.GetString(2))
                            photo.Permissions = If(r.IsDBNull(3), "", r.GetString(3))
                            photo.SetCachedTags(SplitTags(If(r.IsDBNull(4), "", r.GetString(4))))
                            Dim taken = If(r.IsDBNull(5), "", r.GetString(5))
                            Dim epoch As Long
                            If Long.TryParse(taken, NumberStyles.Integer, CultureInfo.InvariantCulture, epoch) Then
                                photo.DateTaken = epoch
                            End If
                            Return photo
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.TryGet", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Der lokal gespeicherte Nextcloud-Photos-Katalog. Anders als Memories liefert
        ''' Photos keine Cluster für Filter; diese Datenbank ist deshalb die maßgebliche Quelle für
        ''' die Galerie-Suche, auch wenn Memories deaktiviert ist.</summary>
        Public Function GetCachedMetas(serverKey As String) As List(Of LibraryImageMeta)
            Dim result As New List(Of LibraryImageMeta)()
            If String.IsNullOrWhiteSpace(serverKey) Then Return result
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT FileId,FilePath,Tags,DateTaken,Camera,Iso,Aperture,Lens,FocalLengthMm,ShutterSpeed,GpsLatitude,GpsLongitude,Width,Height " &
                                          "FROM PhotoMeta WHERE ServerKey=$s"
                        cmd.Parameters.AddWithValue("$s", serverKey)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                If reader.IsDBNull(0) Then Continue While
                                Dim fileId = reader.GetString(0)
                                Dim filePath = If(reader.IsDBNull(1), "", reader.GetString(1))
                                Dim fileName = Path.GetFileName(filePath)
                                If String.IsNullOrWhiteSpace(fileName) Then fileName = fileId
                                ' CTYPE BEI JEDER NULLSPALTE. Ohne die ausdrueckliche Umwandlung
                                ' bestimmt der andere Zweig den Typ, und Nothing wird zur 0: ein Bild
                                ' ohne Koordinate bekaeme den Ort 0/0, ein Bild ohne ISO die ISO 0,
                                ' und jede Suchbedingung ueber diese Felder traefe Zeilen, die den
                                ' Wert gar nicht haben.
                                result.Add(New LibraryImageMeta With {
                                    .FilePath = NextcloudService.MakePseudoPath(fileId, fileName),
                                    .Tags = SplitTags(If(reader.IsDBNull(2), "", reader.GetString(2))),
                                    .DateTaken = If(reader.IsDBNull(3), "", reader.GetString(3)),
                                    .Camera = If(reader.IsDBNull(4), "", reader.GetString(4)),
                                    .Iso = If(reader.IsDBNull(5), CType(Nothing, Integer?), reader.GetInt32(5)),
                                    .Aperture = If(reader.IsDBNull(6), CType(Nothing, Double?), reader.GetDouble(6)),
                                    .Lens = If(reader.IsDBNull(7), "", reader.GetString(7)),
                                    .FocalLengthMm = If(reader.IsDBNull(8), CType(Nothing, Double?), reader.GetDouble(8)),
                                    .ShutterSpeed = If(reader.IsDBNull(9), "", reader.GetString(9)),
                                    .GpsLatitude = If(reader.IsDBNull(10), CType(Nothing, Double?), reader.GetDouble(10)),
                                    .GpsLongitude = If(reader.IsDBNull(11), CType(Nothing, Double?), reader.GetDouble(11)),
                                    .ImageWidth = If(reader.IsDBNull(12), CType(Nothing, Integer?), reader.GetInt32(12)),
                                    .ImageHeight = If(reader.IsDBNull(13), CType(Nothing, Integer?), reader.GetInt32(13))
                                })
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.GetCachedMetas", ex)
                result.Clear()
            End Try
            Return result
        End Function

        ''' <summary>Speichert die Einzelheiten einer Aufnahme unter ihrem Etag.</summary>
        Public Sub Put(serverKey As String, fileId As String, etag As String, photo As NextcloudService.NextcloudPhoto)
            If String.IsNullOrEmpty(serverKey) OrElse String.IsNullOrEmpty(fileId) OrElse photo Is Nothing Then Return
            ' Ohne Etag wird NICHT abgelegt: der Eintrag wäre nie wieder als veraltet zu erkennen.
            If String.IsNullOrEmpty(etag) Then Return
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText =
                            "INSERT INTO PhotoMeta(ServerKey,FileId,Etag,FileSize,FilePath,Permissions,Tags,DateTaken) " &
                            "VALUES($s,$f,$e,$sz,$p,$perm,$tags,$dt) " &
                            "ON CONFLICT(ServerKey,FileId) DO UPDATE SET " &
                            "Etag=$e,FileSize=$sz,FilePath=$p,Permissions=$perm,Tags=$tags,DateTaken=$dt"
                        cmd.Parameters.AddWithValue("$s", serverKey)
                        cmd.Parameters.AddWithValue("$f", fileId)
                        cmd.Parameters.AddWithValue("$e", etag)
                        cmd.Parameters.AddWithValue("$sz", photo.Size)
                        cmd.Parameters.AddWithValue("$p", If(CObj(photo.FileName), DBNull.Value))
                        cmd.Parameters.AddWithValue("$perm", If(CObj(photo.Permissions), DBNull.Value))
                        cmd.Parameters.AddWithValue("$tags", JoinTags(photo.Tags))
                        cmd.Parameters.AddWithValue("$dt", If(photo.TakenEpoch > 0,
                                                              CObj(photo.TakenEpoch.ToString(CultureInfo.InvariantCulture)),
                                                              DBNull.Value))
                        cmd.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.Put", ex)
            End Try
        End Sub

        ''' <summary>Ergänzt oder entfernt ein Stichwort im gespeicherten Eintrag. Ohne Eintrag
        ''' passiert nichts - dieselbe Regel wie beim Immich-Index: ein Stichwort ist kein Grund,
        ''' einen halben Eintrag ohne Etag anzulegen.</summary>
        Public Sub UpdateTag(serverKey As String, fileId As String, tag As String, add As Boolean)
            If String.IsNullOrEmpty(serverKey) OrElse String.IsNullOrEmpty(fileId) OrElse String.IsNullOrWhiteSpace(tag) Then Return
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Dim current As String
                    Using sel = conn.CreateCommand()
                        sel.CommandText = "SELECT Tags FROM PhotoMeta WHERE ServerKey=$s AND FileId=$f"
                        sel.Parameters.AddWithValue("$s", serverKey)
                        sel.Parameters.AddWithValue("$f", fileId)
                        Dim res = sel.ExecuteScalar()
                        If res Is Nothing OrElse res Is DBNull.Value Then Return
                        current = Convert.ToString(res)
                    End Using
                    Dim tags = SplitTags(current)
                    If add Then
                        If Not tags.Any(Function(t) String.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) Then tags.Add(tag)
                    Else
                        tags.RemoveAll(Function(t) String.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                    End If
                    Using upd = conn.CreateCommand()
                        upd.CommandText = "UPDATE PhotoMeta SET Tags=$v WHERE ServerKey=$s AND FileId=$f"
                        upd.Parameters.AddWithValue("$v", JoinTags(tags))
                        upd.Parameters.AddWithValue("$s", serverKey)
                        upd.Parameters.AddWithValue("$f", fileId)
                        upd.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.UpdateTag", ex)
            End Try
        End Sub

        Public Sub Remove(serverKey As String, fileId As String)
            If String.IsNullOrEmpty(serverKey) OrElse String.IsNullOrEmpty(fileId) Then Return
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "DELETE FROM PhotoMeta WHERE ServerKey=$s AND FileId=$f; DELETE FROM AiPhotoTag WHERE ServerKey=$s AND FileId=$f; DELETE FROM AiPhotoScan WHERE ServerKey=$s AND FileId=$f; DELETE FROM XmpSidecarSync WHERE ServerKey=$s AND FileId=$f"
                        cmd.Parameters.AddWithValue("$s", serverKey)
                        cmd.Parameters.AddWithValue("$f", fileId)
                        cmd.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.Remove", ex)
            End Try
        End Sub

        Public Function GetAiTags(serverKey As String, fileId As String) As List(Of AiImageTag)
            Dim result As New List(Of AiImageTag)()
            If String.IsNullOrWhiteSpace(serverKey) OrElse String.IsNullOrWhiteSpace(fileId) Then Return result
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT Canonical,Score,ModelKey,ModelVersion FROM AiPhotoTag WHERE ServerKey=$s AND FileId=$f ORDER BY Score DESC,Canonical"
                        cmd.Parameters.AddWithValue("$s", serverKey) : cmd.Parameters.AddWithValue("$f", fileId)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                result.Add(New AiImageTag With {.Canonical = reader.GetString(0), .Score = CSng(reader.GetDouble(1)),
                                                               .ModelKey = reader.GetString(2), .ModelVersion = reader.GetString(3)})
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.GetAiTags", ex)
            End Try
            Return result
        End Function

        Public Function GetTagCounts() As List(Of (Name As String, Count As Integer))
            Dim counts As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT Tags FROM PhotoMeta WHERE ServerKey=$s AND Tags<>''"
                        cmd.Parameters.AddWithValue("$s", NextcloudService.ServerKey)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                If reader.IsDBNull(0) Then Continue While
                                For Each tag In reader.GetString(0).Split(vbLf).Where(Function(t) Not String.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase)
                                    counts(tag) = If(counts.ContainsKey(tag), counts(tag) + 1, 1)
                                Next
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.StichwortZahlen", ex)
            End Try
            Return counts.OrderBy(Function(x) x.Key, StringComparer.OrdinalIgnoreCase).Select(Function(x) (x.Key, x.Value)).ToList()
        End Function

        Public Function GetAiTagCounts() As List(Of (Canonical As String, Count As Integer))
            Dim result As New List(Of (Canonical As String, Count As Integer))()
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT Canonical,COUNT(DISTINCT FileId) FROM AiPhotoTag WHERE ServerKey=$s GROUP BY Canonical"
                        cmd.Parameters.AddWithValue("$s", NextcloudService.ServerKey)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read() : result.Add((reader.GetString(0), reader.GetInt32(1))) : End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.KIStichwortZahlen", ex)
            End Try
            Return result
        End Function

        ''' <summary>Ermittelt die Dateien, die mindestens eines der gewählten KI-Stichwörter
        ''' tragen. KI-Stichwörter sind lokal erkannt und daher nicht als Memories-Cluster beim
        ''' Nextcloud-Server verfügbar.</summary>
        Public Function GetFileIdsForAiTags(tags As IEnumerable(Of String)) As HashSet(Of String)
            Dim wanted = If(tags, Enumerable.Empty(Of String)()).
                Where(Function(t) Not String.IsNullOrWhiteSpace(t)).
                Select(Function(t) t.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            Dim result As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            If wanted.Count = 0 Then Return result
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        Dim parameters As New List(Of String)()
                        For index = 0 To wanted.Count - 1
                            Dim parameter = "$t" & index.ToString(CultureInfo.InvariantCulture)
                            parameters.Add(parameter)
                            cmd.Parameters.AddWithValue(parameter, wanted(index))
                        Next
                        cmd.CommandText = "SELECT DISTINCT FileId FROM AiPhotoTag WHERE ServerKey=$s AND Canonical IN (" & String.Join(",", parameters) & ")"
                        cmd.Parameters.AddWithValue("$s", NextcloudService.ServerKey)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                If Not reader.IsDBNull(0) Then result.Add(reader.GetString(0))
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.FindAiTags", ex)
            End Try
            Return result
        End Function

        Public Sub ReplaceAiTags(serverKey As String, fileId As String, sourceVersion As String, tags As IEnumerable(Of AiImageTag))
            If String.IsNullOrWhiteSpace(serverKey) OrElse String.IsNullOrWhiteSpace(fileId) Then Return
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using tx = conn.BeginTransaction()
                        Using deleteCmd = conn.CreateCommand()
                            deleteCmd.Transaction = tx
                            deleteCmd.CommandText = "DELETE FROM AiPhotoTag WHERE ServerKey=$s AND FileId=$f"
                            deleteCmd.Parameters.AddWithValue("$s", serverKey) : deleteCmd.Parameters.AddWithValue("$f", fileId)
                            deleteCmd.ExecuteNonQuery()
                        End Using
                        For Each tag In If(tags, Enumerable.Empty(Of AiImageTag)()).Where(Function(t) t IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(t.Canonical))
                            Using insert = conn.CreateCommand()
                                insert.Transaction = tx
                                insert.CommandText = "INSERT INTO AiPhotoTag(ServerKey,FileId,Canonical,Score,ModelKey,ModelVersion,SourceVersion) VALUES($s,$f,$c,$score,$k,$v,$source)"
                                insert.Parameters.AddWithValue("$s", serverKey) : insert.Parameters.AddWithValue("$f", fileId)
                                insert.Parameters.AddWithValue("$c", tag.Canonical.Trim()) : insert.Parameters.AddWithValue("$score", tag.Score)
                                insert.Parameters.AddWithValue("$k", If(tag.ModelKey, "")) : insert.Parameters.AddWithValue("$v", If(tag.ModelVersion, ""))
                                insert.Parameters.AddWithValue("$source", If(sourceVersion, ""))
                                insert.ExecuteNonQuery()
                            End Using
                        Next
                        Using scan = conn.CreateCommand()
                            scan.Transaction = tx
                            scan.CommandText = "INSERT INTO AiPhotoScan(ServerKey,FileId,SourceVersion,ModelKey,ModelVersion) VALUES($s,$f,$source,$k,$v) ON CONFLICT(ServerKey,FileId) DO UPDATE SET SourceVersion=$source,ModelKey=$k,ModelVersion=$v"
                            scan.Parameters.AddWithValue("$s", serverKey) : scan.Parameters.AddWithValue("$f", fileId)
                            scan.Parameters.AddWithValue("$source", If(sourceVersion, ""))
                            scan.Parameters.AddWithValue("$k", ImageTaggingService.ModelKey) : scan.Parameters.AddWithValue("$v", ImageTaggingService.AnalysisVersion)
                            scan.ExecuteNonQuery()
                        End Using
                        tx.Commit()
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.ReplaceAiTags", ex)
            End Try
        End Sub

        Public Function AiTagsNeedRefresh(serverKey As String, fileId As String, sourceVersion As String) As Boolean
            If String.IsNullOrWhiteSpace(serverKey) OrElse String.IsNullOrWhiteSpace(fileId) Then Return False
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT SourceVersion,ModelKey,ModelVersion FROM AiPhotoScan WHERE ServerKey=$s AND FileId=$f"
                        cmd.Parameters.AddWithValue("$s", serverKey) : cmd.Parameters.AddWithValue("$f", fileId)
                        Using r = cmd.ExecuteReader()
                            Return Not r.Read() OrElse Not String.Equals(If(r.IsDBNull(0), "", r.GetString(0)), If(sourceVersion, ""), StringComparison.Ordinal) OrElse Not String.Equals(If(r.IsDBNull(1), "", r.GetString(1)), ImageTaggingService.ModelKey, StringComparison.Ordinal) OrElse Not String.Equals(If(r.IsDBNull(2), "", r.GetString(2)), ImageTaggingService.AnalysisVersion, StringComparison.Ordinal)
                        End Using
                    End Using
                End Using
            Catch
                Return True
            End Try
        End Function

        Public Function XmpSidecarNeedsSync(serverKey As String, fileId As String, sourceSignature As String) As Boolean
            If String.IsNullOrWhiteSpace(serverKey) OrElse String.IsNullOrWhiteSpace(fileId) Then Return False
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT SourceSignature FROM XmpSidecarSync WHERE ServerKey=$s AND FileId=$f"
                        cmd.Parameters.AddWithValue("$s", serverKey) : cmd.Parameters.AddWithValue("$f", fileId)
                        Dim value = cmd.ExecuteScalar()
                        Return value Is Nothing OrElse value Is DBNull.Value OrElse Not String.Equals(Convert.ToString(value, CultureInfo.InvariantCulture), If(sourceSignature, ""), StringComparison.Ordinal)
                    End Using
                End Using
            Catch
                Return True
            End Try
        End Function

        Public Sub MarkXmpSidecarSynced(serverKey As String, fileId As String, sourceSignature As String)
            If String.IsNullOrWhiteSpace(serverKey) OrElse String.IsNullOrWhiteSpace(fileId) Then Return
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "INSERT INTO XmpSidecarSync(ServerKey,FileId,SourceSignature) VALUES($s,$f,$v) ON CONFLICT(ServerKey,FileId) DO UPDATE SET SourceSignature=$v"
                        cmd.Parameters.AddWithValue("$s", serverKey) : cmd.Parameters.AddWithValue("$f", fileId) : cmd.Parameters.AddWithValue("$v", If(sourceSignature, ""))
                        cmd.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.XmpSync", ex)
            End Try
        End Sub

        ''' <summary>Anzahl der Einträge und ungefähre Größe der Datei - für die Einstellungen.</summary>
        Public Function GetInfo() As (Count As Integer, SizeBytes As Long)
            Dim count = 0
            Dim size = 0L
            Try
                If File.Exists(_dbPath) Then size = New FileInfo(_dbPath).Length
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT COUNT(*) FROM PhotoMeta"
                        count = Convert.ToInt32(cmd.ExecuteScalar())
                    End Using
                End Using
            Catch
            End Try
            Return (count, size)
        End Function

        ''' <summary>Leert den Index. Gibt die Zahl der entfernten Einträge zurück.</summary>
        Public Function Clear() As Integer
            Try
                Dim removed = 0
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using countCmd = conn.CreateCommand()
                        countCmd.CommandText = "SELECT COUNT(*) FROM PhotoMeta"
                        removed = Convert.ToInt32(countCmd.ExecuteScalar())
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "DELETE FROM PhotoMeta; DELETE FROM AiPhotoTag; DELETE FROM AiPhotoScan; DELETE FROM XmpSidecarSync"
                        cmd.ExecuteNonQuery()
                    End Using
                End Using
                Return removed
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.Clear", ex)
                Return 0
            End Try
        End Function

        Private Shared Function ParseFileId(value As String) As Long
            Dim id As Long
            If Long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, id) Then Return id
            Return 0
        End Function

        ''' Komma trennt, wie im lokalen Katalog und im Immich-Index.
        Private Shared Function JoinTags(tags As List(Of String)) As String
            If tags Is Nothing Then Return ""
            Return String.Join(",", tags.Where(Function(t) Not String.IsNullOrWhiteSpace(t)).Select(Function(t) t.Trim()))
        End Function

        Private Shared Function SplitTags(value As String) As List(Of String)
            If String.IsNullOrWhiteSpace(value) Then Return New List(Of String)()
            Return value.Split(","c, StringSplitOptions.RemoveEmptyEntries).
                Select(Function(t) t.Trim()).
                Where(Function(t) Not String.IsNullOrWhiteSpace(t)).
                ToList()
        End Function

    End Class

End Namespace
