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
                        cmd.CommandText = "DELETE FROM PhotoMeta WHERE ServerKey=$s AND FileId=$f"
                        cmd.Parameters.AddWithValue("$s", serverKey)
                        cmd.Parameters.AddWithValue("$f", fileId)
                        cmd.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("NextcloudIndex.Remove", ex)
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
                        cmd.CommandText = "DELETE FROM PhotoMeta"
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
