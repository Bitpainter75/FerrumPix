Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports Microsoft.Data.Sqlite

Namespace Services

    ''' <summary>
    ''' Lokaler Metadaten-Index für Immich-Assets in einer eigenen SQLite-Datei (getrennt vom lokalen
    ''' Bild-Katalog, damit ein Serverwechsel/Reset nichts vermischt). Die Metadaten-Suche liefert nur
    ''' Grunddaten; Dateigröße/Rating/Kamera/Stichwörter stehen nur im Detail-Endpunkt. Diese werden hier
    ''' je (Server, Asset-ID) zwischengespeichert, damit sie über Sitzungen hinweg nicht immer wieder
    ''' neu über das Netz geholt werden müssen. Invalidiert wird über Immichs <c>updatedAt</c>: ändert
    ''' es sich, gilt der Eintrag als veraltet und wird neu geholt.
    ''' </summary>
    Public NotInheritable Class ImmichIndexService

        Private Shared _instance As ImmichIndexService
        Private ReadOnly _connectionString As String
        Private ReadOnly _dbPath As String

        Public Shared ReadOnly Property Instance As ImmichIndexService
            Get
                If _instance Is Nothing Then _instance = New ImmichIndexService()
                Return _instance
            End Get
        End Property

        Private Sub New()
            Dim dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix")
            Directory.CreateDirectory(dir)
            _dbPath = Path.Combine(dir, "immich-index.db")
            _connectionString = $"Data Source={_dbPath}"
            Try
                InitDb()
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.Init", ex)
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
                        "CREATE TABLE IF NOT EXISTS AssetMeta (" &
                        "  ServerKey  TEXT NOT NULL," &
                        "  AssetId    TEXT NOT NULL," &
                        "  UpdatedAt  TEXT NOT NULL DEFAULT ''," &
                        "  FileSize   INTEGER NOT NULL DEFAULT 0," &
                        "  Rating     INTEGER NOT NULL DEFAULT 0," &
                        "  Camera     TEXT," &
                        "  Iso        INTEGER," &
                        "  Aperture   REAL," &
                        "  FileCreatedAt TEXT," &
                        "  FileModifiedAt TEXT," &
                        "  DateTaken  TEXT," &
                        "  DateModified TEXT," &
                        "  Tags       TEXT NOT NULL DEFAULT ''," &
                        "  Width      INTEGER NOT NULL DEFAULT 0," &
                        "  Height     INTEGER NOT NULL DEFAULT 0," &
                        "  IsFavorite INTEGER NOT NULL DEFAULT 0," &
                        "  PRIMARY KEY (ServerKey, AssetId)" &
                        ")"
                    cmd.ExecuteNonQuery()
                End Using
                Using cmd = conn.CreateCommand()
                    ' Lokaler KATALOG der "Alle Fotos"-Timeline: das Öffnen zeigt sofort
                    ' diesen Stand, der Server-Abgleich läuft danach im Hintergrund. Position erhält
                    ' die Server-Reihenfolge (Timeline).
                    cmd.CommandText =
                        "CREATE TABLE IF NOT EXISTS AssetList (" &
                        "  ServerKey     TEXT NOT NULL," &
                        "  Position      INTEGER NOT NULL DEFAULT 0," &
                        "  AssetId       TEXT NOT NULL," &
                        "  FileName      TEXT NOT NULL DEFAULT ''," &
                        "  IsVideo       INTEGER NOT NULL DEFAULT 0," &
                        "  FileCreatedAt TEXT," &
                        "  FileModifiedAt TEXT," &
                        "  FileSize      INTEGER NOT NULL DEFAULT 0," &
                        "  DateTaken     TEXT," &
                        "  DateModified  TEXT," &
                        "  Width         INTEGER NOT NULL DEFAULT 0," &
                        "  Height        INTEGER NOT NULL DEFAULT 0," &
                        "  IsFavorite    INTEGER NOT NULL DEFAULT 0," &
                        "  UpdatedAt     TEXT NOT NULL DEFAULT ''," &
                        "  PRIMARY KEY (ServerKey, AssetId)" &
                        ")"
                    cmd.ExecuteNonQuery()
                End Using
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "CREATE TABLE IF NOT EXISTS AiAssetTag (" &
                        " ServerKey TEXT NOT NULL, AssetId TEXT NOT NULL, Canonical TEXT NOT NULL COLLATE NOCASE," &
                        " Score REAL NOT NULL, ModelKey TEXT NOT NULL DEFAULT '', ModelVersion TEXT NOT NULL DEFAULT ''," &
                        " SourceVersion TEXT NOT NULL DEFAULT '', PRIMARY KEY(ServerKey,AssetId,Canonical))"
                    cmd.ExecuteNonQuery()
                End Using
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "CREATE TABLE IF NOT EXISTS AiAssetScan (ServerKey TEXT NOT NULL, AssetId TEXT NOT NULL, SourceVersion TEXT NOT NULL DEFAULT '', ModelKey TEXT NOT NULL DEFAULT '', ModelVersion TEXT NOT NULL DEFAULT '', PRIMARY KEY(ServerKey,AssetId))"
                    cmd.ExecuteNonQuery()
                End Using
                EnsureColumn(conn, "AssetMeta", "FileCreatedAt", "TEXT")
                EnsureColumn(conn, "AssetMeta", "FileModifiedAt", "TEXT")
                EnsureColumn(conn, "AssetMeta", "DateModified", "TEXT")
                EnsureColumn(conn, "AssetList", "FileModifiedAt", "TEXT")
                EnsureColumn(conn, "AssetList", "FileSize", "INTEGER NOT NULL DEFAULT 0")
                EnsureColumn(conn, "AssetList", "DateTaken", "TEXT")
                EnsureColumn(conn, "AssetList", "DateModified", "TEXT")
                EnsureColumn(conn, "AssetList", "City", "TEXT")
                EnsureColumn(conn, "AssetList", "Country", "TEXT")
                ' DER ALTBESTAND MUSS WEG, NICHT NUR DIE SPALTE DAZU. Ein gespeicherter Eintrag gilt
                ' so lange als gültig, wie sein UpdatedAt zum Server passt - ein alter Eintrag ohne
                ' Ortsspalten sähe damit für immer aus wie "dieses Bild hat keinen Ort", und der
                ' Ort käme nie an. Die Tabelle ist reiner Zwischenspeicher: was hier fehlt, holt der
                ' nächste Detail-Abruf.
                If EnsureColumn(conn, "AssetMeta", "City", "TEXT") Then ClearAssetMeta(conn)
                EnsureColumn(conn, "AssetMeta", "Country", "TEXT")
                ' Dieselben Felder wie im lokalen Katalog. Aus demselben Grund wie bei den
                ' Ortsspalten wird der Altbestand geleert und nicht nur die Spalte angehaengt: ein
                ' alter Eintrag gilt so lange als gueltig, wie sein UpdatedAt passt, und saehe sonst
                ' fuer immer aus wie "dieses Bild hat kein Objektiv".
                Dim addedExtras = EnsureColumn(conn, "AssetMeta", "Lens", "TEXT")
                addedExtras = EnsureColumn(conn, "AssetMeta", "FocalLengthMm", "REAL") OrElse addedExtras
                addedExtras = EnsureColumn(conn, "AssetMeta", "ShutterSpeed", "TEXT") OrElse addedExtras
                addedExtras = EnsureColumn(conn, "AssetMeta", "GpsLatitude", "REAL") OrElse addedExtras
                addedExtras = EnsureColumn(conn, "AssetMeta", "GpsLongitude", "REAL") OrElse addedExtras
                ' KEIN OrElse-Kurzschluss ueber die Aufrufe selbst: jede Spalte muss angelegt
                ' werden, auch wenn eine fruehere schon gemeldet hat, dass sie neu war.
                If addedExtras Then ClearAssetMeta(conn)
            End Using
        End Sub

        ''' <returns>True, wenn die Spalte gerade erst angelegt wurde.</returns>
        Private Shared Function EnsureColumn(conn As SqliteConnection, tableName As String,
                                             columnName As String, definition As String) As Boolean
            Using check = conn.CreateCommand()
                check.CommandText = $"PRAGMA table_info({tableName})"
                Using reader = check.ExecuteReader()
                    While reader.Read()
                        If String.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase) Then Return False
                    End While
                End Using
            End Using
            Using alter = conn.CreateCommand()
                alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition}"
                alter.ExecuteNonQuery()
            End Using
            Return True
        End Function

        Private Shared Sub ClearAssetMeta(conn As SqliteConnection)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "DELETE FROM AssetMeta"
                cmd.ExecuteNonQuery()
            End Using
        End Sub

        ''' <summary>Der lokal gespeicherte "Alle Fotos"-Katalog in Server-Reihenfolge. Leer, wenn
        ''' noch nie ein vollständiger Abgleich lief. Wirft nie.</summary>
        Public Function GetAssetList(serverKey As String) As List(Of ImmichAsset)
            Dim result As New List(Of ImmichAsset)()
            If String.IsNullOrEmpty(serverKey) Then Return result
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT AssetId,FileName,IsVideo,FileCreatedAt,FileModifiedAt,FileSize,DateTaken,DateModified,Width,Height,IsFavorite,UpdatedAt,City,Country " &
                                          "FROM AssetList WHERE ServerKey=$s ORDER BY Position"
                        cmd.Parameters.AddWithValue("$s", serverKey)
                        Using r = cmd.ExecuteReader()
                            While r.Read()
                                result.Add(New ImmichAsset With {
                                    .Id = r.GetString(0),
                                    .FileName = If(r.IsDBNull(1), "", r.GetString(1)),
                                    .IsVideo = Not r.IsDBNull(2) AndAlso r.GetInt32(2) <> 0,
                                    .FileCreatedAt = ParseDate(If(r.IsDBNull(3), "", r.GetString(3))),
                                    .FileModifiedAt = ParseDate(If(r.IsDBNull(4), "", r.GetString(4))),
                                    .FileSizeBytes = If(r.IsDBNull(5), 0L, r.GetInt64(5)),
                                    .ExifDateTaken = ParseDate(If(r.IsDBNull(6), "", r.GetString(6))),
                                    .ExifDateModified = ParseDate(If(r.IsDBNull(7), "", r.GetString(7))),
                                    .Width = If(r.IsDBNull(8), 0, r.GetInt32(8)),
                                    .Height = If(r.IsDBNull(9), 0, r.GetInt32(9)),
                                    .IsFavorite = Not r.IsDBNull(10) AndAlso r.GetInt32(10) <> 0,
                                    .UpdatedAt = If(r.IsDBNull(11), "", r.GetString(11)),
                                    .City = If(r.IsDBNull(12), "", r.GetString(12)),
                                    .Country = If(r.IsDBNull(13), "", r.GetString(13))
                                })
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.GetAssetList", ex)
                result.Clear()
            End Try
            Return result
        End Function

        ''' <summary>Ersetzt den gespeicherten Katalog KOMPLETT durch den frischen Serverstand
        ''' (eine Transaktion). Nur nach einem VOLLSTÄNDIG durchgelaufenen Abgleich aufrufen -
        ''' ein Teilstand würde beim nächsten Start Bilder verstecken. Wirft nie.</summary>
        Public Sub ReplaceAssetList(serverKey As String, assets As IReadOnlyList(Of ImmichAsset))
            If String.IsNullOrEmpty(serverKey) OrElse assets Is Nothing Then Return
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using transaction = conn.BeginTransaction()
                        Using del = conn.CreateCommand()
                            del.Transaction = transaction
                            del.CommandText = "DELETE FROM AssetList WHERE ServerKey=$s"
                            del.Parameters.AddWithValue("$s", serverKey)
                            del.ExecuteNonQuery()
                        End Using
                        Using ins = conn.CreateCommand()
                            ins.Transaction = transaction
                            ins.CommandText = "INSERT INTO AssetList(ServerKey,Position,AssetId,FileName,IsVideo,FileCreatedAt,FileModifiedAt,FileSize,DateTaken,DateModified,Width,Height,IsFavorite,UpdatedAt,City,Country) " &
                                              "VALUES($s,$pos,$a,$n,$v,$c,$m,$fs,$dt,$dm,$w,$h,$f,$u,$city,$country)"
                            Dim pS = ins.Parameters.Add("$s", SqliteType.Text)
                            Dim pPos = ins.Parameters.Add("$pos", SqliteType.Integer)
                            Dim pA = ins.Parameters.Add("$a", SqliteType.Text)
                            Dim pN = ins.Parameters.Add("$n", SqliteType.Text)
                            Dim pV = ins.Parameters.Add("$v", SqliteType.Integer)
                            Dim pC = ins.Parameters.Add("$c", SqliteType.Text)
                            Dim pM = ins.Parameters.Add("$m", SqliteType.Text)
                            Dim pFs = ins.Parameters.Add("$fs", SqliteType.Integer)
                            Dim pDt = ins.Parameters.Add("$dt", SqliteType.Text)
                            Dim pDm = ins.Parameters.Add("$dm", SqliteType.Text)
                            Dim pW = ins.Parameters.Add("$w", SqliteType.Integer)
                            Dim pH = ins.Parameters.Add("$h", SqliteType.Integer)
                            Dim pF = ins.Parameters.Add("$f", SqliteType.Integer)
                            Dim pU = ins.Parameters.Add("$u", SqliteType.Text)
                            Dim pCity = ins.Parameters.Add("$city", SqliteType.Text)
                            Dim pCountry = ins.Parameters.Add("$country", SqliteType.Text)
                            pS.Value = serverKey
                            For i = 0 To assets.Count - 1
                                Dim a = assets(i)
                                If a Is Nothing OrElse String.IsNullOrEmpty(a.Id) Then Continue For
                                pPos.Value = i
                                pA.Value = a.Id
                                pN.Value = If(a.FileName, "")
                                pV.Value = If(a.IsVideo, 1, 0)
                                pC.Value = If(a.FileCreatedAt.HasValue, a.FileCreatedAt.Value.ToString("o", CultureInfo.InvariantCulture), CType(DBNull.Value, Object))
                                pM.Value = If(a.FileModifiedAt.HasValue, a.FileModifiedAt.Value.ToString("o", CultureInfo.InvariantCulture), CType(DBNull.Value, Object))
                                pFs.Value = a.FileSizeBytes
                                pDt.Value = If(a.ExifDateTaken.HasValue, a.ExifDateTaken.Value.ToString("o", CultureInfo.InvariantCulture), CType(DBNull.Value, Object))
                                pDm.Value = If(a.ExifDateModified.HasValue, a.ExifDateModified.Value.ToString("o", CultureInfo.InvariantCulture), CType(DBNull.Value, Object))
                                pW.Value = a.Width
                                pH.Value = a.Height
                                pF.Value = If(a.IsFavorite, 1, 0)
                                pU.Value = If(a.UpdatedAt, "")
                                pCity.Value = If(a.City, "")
                                pCountry.Value = If(a.Country, "")
                                ins.ExecuteNonQuery()
                            Next
                        End Using
                        transaction.Commit()
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.ReplaceAssetList", ex)
            End Try
        End Sub

        ''' <summary>Liefert die gecachten Detaildaten, sofern vorhanden UND das gespeicherte updatedAt
        ''' mit dem übergebenen übereinstimmt (sonst veraltet → Nothing, Aufrufer holt neu).</summary>
        Public Function TryGet(serverKey As String, assetId As String, updatedAt As String) As ImmichAsset
            If String.IsNullOrEmpty(serverKey) OrElse String.IsNullOrEmpty(assetId) Then Return Nothing
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT UpdatedAt,FileSize,Rating,Camera,Iso,Aperture,FileCreatedAt,FileModifiedAt,DateTaken,DateModified,Tags,Width,Height,IsFavorite,City,Country,Lens,FocalLengthMm,ShutterSpeed,GpsLatitude,GpsLongitude " &
                                          "FROM AssetMeta WHERE ServerKey=$s AND AssetId=$a"
                        cmd.Parameters.AddWithValue("$s", serverKey)
                        cmd.Parameters.AddWithValue("$a", assetId)
                        Using r = cmd.ExecuteReader()
                            If Not r.Read() Then Return Nothing
                            Dim storedUpdated = If(r.IsDBNull(0), "", r.GetString(0))
                            ' Veraltet? Dann so tun, als wäre nichts da - der Aufrufer holt neu und
                            ' überschreibt den Eintrag.
                            If Not String.Equals(storedUpdated, If(updatedAt, ""), StringComparison.Ordinal) Then Return Nothing
                            Dim asset = New ImmichAsset With {
                                .Id = assetId,
                                .UpdatedAt = storedUpdated,
                                .FileSizeBytes = If(r.IsDBNull(1), 0L, r.GetInt64(1)),
                                .Rating = If(r.IsDBNull(2), 0, r.GetInt32(2)),
                                .Camera = If(r.IsDBNull(3), "", r.GetString(3)),
                                .Iso = If(r.IsDBNull(4), CType(Nothing, Integer?), r.GetInt32(4)),
                                .Aperture = If(r.IsDBNull(5), CType(Nothing, Double?), r.GetDouble(5)),
                                .FileCreatedAt = ParseDate(If(r.IsDBNull(6), "", r.GetString(6))),
                                .FileModifiedAt = ParseDate(If(r.IsDBNull(7), "", r.GetString(7))),
                                .ExifDateTaken = ParseDate(If(r.IsDBNull(8), "", r.GetString(8))),
                                .ExifDateModified = ParseDate(If(r.IsDBNull(9), "", r.GetString(9))),
                                .Tags = SplitTags(If(r.IsDBNull(10), "", r.GetString(10))),
                                .Width = If(r.IsDBNull(11), 0, r.GetInt32(11)),
                                .Height = If(r.IsDBNull(12), 0, r.GetInt32(12)),
                                .IsFavorite = Not r.IsDBNull(13) AndAlso r.GetInt32(13) <> 0,
                                .City = If(r.IsDBNull(14), "", r.GetString(14)),
                                .Country = If(r.IsDBNull(15), "", r.GetString(15)),
                                .Lens = If(r.IsDBNull(16), "", r.GetString(16)),
                                .FocalLengthMm = If(r.IsDBNull(17), CType(Nothing, Double?), r.GetDouble(17)),
                                .ShutterSpeed = If(r.IsDBNull(18), "", r.GetString(18)),
                                .GpsLatitude = If(r.IsDBNull(19), CType(Nothing, Double?), r.GetDouble(19)),
                                .GpsLongitude = If(r.IsDBNull(20), CType(Nothing, Double?), r.GetDouble(20))
                            }
                            Return asset
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.TryGet", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Ort und Land eines Assets, OHNE die Altersprüfung über <c>UpdatedAt</c>.
        '''
        ''' Für den Editor: er arbeitet auf einer heruntergeladenen Temp-Kopie und kennt vom Asset
        ''' nur noch die ID im Dateinamen - der Änderungszeitstempel, an dem sonst die Gültigkeit
        ''' hängt, ist dort nicht mehr zur Hand. Für einen Ortsnamen reicht der gespeicherte Stand:
        ''' er ändert sich praktisch nie, und die Alternative wäre eine leere Zeile.</summary>
        Public Function GetPlace(serverKey As String, assetId As String) As (City As String, Country As String)
            If String.IsNullOrEmpty(serverKey) OrElse String.IsNullOrEmpty(assetId) Then Return ("", "")
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    ' Zuerst der Detail-Zwischenspeicher, dann der Katalog: beide tragen den Ort,
                    ' aber nur der erste ist bei einem Asset gefüllt, das nie in der Timeline stand
                    ' (Album, Person, Ort).
                    For Each table In {"AssetMeta", "AssetList"}
                        Using cmd = conn.CreateCommand()
                            cmd.CommandText = $"SELECT COALESCE(City,''),COALESCE(Country,'') FROM {table} WHERE ServerKey=$s AND AssetId=$a"
                            cmd.Parameters.AddWithValue("$s", serverKey)
                            cmd.Parameters.AddWithValue("$a", assetId)
                            Using r = cmd.ExecuteReader()
                                If Not r.Read() Then Continue For
                                Dim city = r.GetString(0)
                                Dim country = r.GetString(1)
                                If city.Length > 0 OrElse country.Length > 0 Then Return (city, country)
                            End Using
                        End Using
                    Next
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.GetPlace", ex)
            End Try
            Return ("", "")
        End Function

        ''' <summary>Speichert/aktualisiert die Detaildaten eines Assets.</summary>
        Public Sub Put(serverKey As String, asset As ImmichAsset)
            If String.IsNullOrEmpty(serverKey) OrElse asset Is Nothing OrElse String.IsNullOrEmpty(asset.Id) Then Return
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText =
                            "INSERT INTO AssetMeta(ServerKey,AssetId,UpdatedAt,FileSize,Rating,Camera,Iso,Aperture,FileCreatedAt,FileModifiedAt,DateTaken,DateModified,Tags,Width,Height,IsFavorite,City,Country,Lens,FocalLengthMm,ShutterSpeed,GpsLatitude,GpsLongitude) " &
                            "VALUES($s,$a,$u,$fs,$r,$cam,$iso,$ap,$fc,$fm,$dt,$dm,$tags,$w,$h,$fav,$city,$country,$lens,$focal,$shutter,$lat,$lon) " &
                            "ON CONFLICT(ServerKey,AssetId) DO UPDATE SET " &
                            "UpdatedAt=$u,FileSize=$fs,Rating=$r,Camera=$cam,Iso=$iso,Aperture=$ap,FileCreatedAt=$fc,FileModifiedAt=$fm,DateTaken=$dt,DateModified=$dm,Tags=$tags,Width=$w,Height=$h,IsFavorite=$fav,City=$city,Country=$country,Lens=$lens,FocalLengthMm=$focal,ShutterSpeed=$shutter,GpsLatitude=$lat,GpsLongitude=$lon"
                        cmd.Parameters.AddWithValue("$s", serverKey)
                        cmd.Parameters.AddWithValue("$a", asset.Id)
                        cmd.Parameters.AddWithValue("$u", If(asset.UpdatedAt, ""))
                        cmd.Parameters.AddWithValue("$fs", asset.FileSizeBytes)
                        cmd.Parameters.AddWithValue("$r", asset.Rating)
                        cmd.Parameters.AddWithValue("$cam", If(CObj(asset.Camera), DBNull.Value))
                        cmd.Parameters.AddWithValue("$iso", If(asset.Iso.HasValue, CObj(asset.Iso.Value), DBNull.Value))
                        cmd.Parameters.AddWithValue("$ap", If(asset.Aperture.HasValue, CObj(asset.Aperture.Value), DBNull.Value))
                        cmd.Parameters.AddWithValue("$fc", If(asset.FileCreatedAt.HasValue, CObj(asset.FileCreatedAt.Value.ToString("o", CultureInfo.InvariantCulture)), DBNull.Value))
                        cmd.Parameters.AddWithValue("$fm", If(asset.FileModifiedAt.HasValue, CObj(asset.FileModifiedAt.Value.ToString("o", CultureInfo.InvariantCulture)), DBNull.Value))
                        cmd.Parameters.AddWithValue("$dt", If(asset.ExifDateTaken.HasValue, CObj(asset.ExifDateTaken.Value.ToString("o", CultureInfo.InvariantCulture)), DBNull.Value))
                        cmd.Parameters.AddWithValue("$dm", If(asset.ExifDateModified.HasValue, CObj(asset.ExifDateModified.Value.ToString("o", CultureInfo.InvariantCulture)), DBNull.Value))
                        cmd.Parameters.AddWithValue("$tags", JoinTags(asset.Tags))
                        cmd.Parameters.AddWithValue("$lens", If(CObj(asset.Lens), DBNull.Value))
                        cmd.Parameters.AddWithValue("$focal", If(asset.FocalLengthMm.HasValue, CObj(asset.FocalLengthMm.Value), DBNull.Value))
                        cmd.Parameters.AddWithValue("$shutter", If(CObj(asset.ShutterSpeed), DBNull.Value))
                        cmd.Parameters.AddWithValue("$lat", If(asset.GpsLatitude.HasValue, CObj(asset.GpsLatitude.Value), DBNull.Value))
                        cmd.Parameters.AddWithValue("$lon", If(asset.GpsLongitude.HasValue, CObj(asset.GpsLongitude.Value), DBNull.Value))
                        cmd.Parameters.AddWithValue("$w", asset.Width)
                        cmd.Parameters.AddWithValue("$h", asset.Height)
                        cmd.Parameters.AddWithValue("$fav", If(asset.IsFavorite, 1, 0))
                        cmd.Parameters.AddWithValue("$city", If(asset.City, ""))
                        cmd.Parameters.AddWithValue("$country", If(asset.Country, ""))
                        cmd.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.Put", ex)
            End Try
        End Sub

        ''' <summary>Aktualisiert ein einzelnes Feld eines bereits gecachten Eintrags (No-op, wenn das
        ''' Asset noch nicht im Index ist). Hält den Index nach einer Rückschreibaktion konsistent, ohne
        ''' einen erneuten Detail-Abruf zu erzwingen.</summary>
        Public Sub UpdateFavorite(serverKey As String, assetId As String, isFavorite As Boolean)
            ExecUpdate(serverKey, assetId, "IsFavorite=$v", Function(p) p.AddWithValue("$v", If(isFavorite, 1, 0)))
        End Sub

        Public Sub UpdateRating(serverKey As String, assetId As String, rating As Integer)
            ExecUpdate(serverKey, assetId, "Rating=$v", Function(p) p.AddWithValue("$v", rating))
        End Sub

        ''' <summary>Ergänzt/entfernt ein Stichwort im gecachten Eintrag (read-modify-write; No-op ohne Eintrag).</summary>
        Public Sub UpdateTag(serverKey As String, assetId As String, tag As String, add As Boolean)
            If String.IsNullOrEmpty(serverKey) OrElse String.IsNullOrEmpty(assetId) OrElse String.IsNullOrWhiteSpace(tag) Then Return
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Dim current As String = Nothing
                    Using sel = conn.CreateCommand()
                        sel.CommandText = "SELECT Tags FROM AssetMeta WHERE ServerKey=$s AND AssetId=$a"
                        sel.Parameters.AddWithValue("$s", serverKey)
                        sel.Parameters.AddWithValue("$a", assetId)
                        Dim res = sel.ExecuteScalar()
                        If res Is Nothing OrElse res Is DBNull.Value Then Return   ' nicht gecacht -> nichts tun
                        current = Convert.ToString(res)
                    End Using
                    Dim tags = SplitTags(current)
                    If add Then
                        If Not tags.Any(Function(t) String.Equals(t, tag, StringComparison.OrdinalIgnoreCase)) Then tags.Add(tag)
                    Else
                        tags.RemoveAll(Function(t) String.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                    End If
                    Using upd = conn.CreateCommand()
                        upd.CommandText = "UPDATE AssetMeta SET Tags=$v WHERE ServerKey=$s AND AssetId=$a"
                        upd.Parameters.AddWithValue("$v", JoinTags(tags))
                        upd.Parameters.AddWithValue("$s", serverKey)
                        upd.Parameters.AddWithValue("$a", assetId)
                        upd.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.UpdateTag", ex)
            End Try
        End Sub

        Private Sub ExecUpdate(serverKey As String, assetId As String, setClause As String, bindValue As Func(Of SqliteParameterCollection, Object))
            If String.IsNullOrEmpty(serverKey) OrElse String.IsNullOrEmpty(assetId) Then Return
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = $"UPDATE AssetMeta SET {setClause} WHERE ServerKey=$s AND AssetId=$a"
                        bindValue(cmd.Parameters)
                        cmd.Parameters.AddWithValue("$s", serverKey)
                        cmd.Parameters.AddWithValue("$a", assetId)
                        cmd.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.Update", ex)
            End Try
        End Sub

        ''' <summary>Wirft den Eintrag eines Assets weg (nach Ersetzen oder Löschen auf dem Server - der
        ''' Eintrag beschreibt dann ein Bild, das es so nicht mehr gibt).</summary>
        Public Sub Remove(serverKey As String, assetId As String)
            If String.IsNullOrEmpty(serverKey) OrElse String.IsNullOrEmpty(assetId) Then Return
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "DELETE FROM AssetMeta WHERE ServerKey=$s AND AssetId=$a; DELETE FROM AiAssetTag WHERE ServerKey=$s AND AssetId=$a; DELETE FROM AiAssetScan WHERE ServerKey=$s AND AssetId=$a"
                        cmd.Parameters.AddWithValue("$s", serverKey)
                        cmd.Parameters.AddWithValue("$a", assetId)
                        cmd.ExecuteNonQuery()
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.Remove", ex)
            End Try
        End Sub

        Public Function GetAiTags(serverKey As String, assetId As String) As List(Of AiImageTag)
            Dim result As New List(Of AiImageTag)()
            If String.IsNullOrWhiteSpace(serverKey) OrElse String.IsNullOrWhiteSpace(assetId) Then Return result
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT Canonical,Score,ModelKey,ModelVersion FROM AiAssetTag WHERE ServerKey=$s AND AssetId=$a ORDER BY Score DESC,Canonical"
                        cmd.Parameters.AddWithValue("$s", serverKey)
                        cmd.Parameters.AddWithValue("$a", assetId)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                result.Add(New AiImageTag With {.Canonical = reader.GetString(0), .Score = CSng(reader.GetDouble(1)),
                                                               .ModelKey = reader.GetString(2), .ModelVersion = reader.GetString(3)})
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.GetAiTags", ex)
            End Try
            Return result
        End Function

        Public Function GetAiTagCounts() As List(Of (Canonical As String, Count As Integer))
            Dim result As New List(Of (Canonical As String, Count As Integer))()
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT Canonical,COUNT(DISTINCT AssetId) FROM AiAssetTag WHERE ServerKey=$s GROUP BY Canonical"
                        cmd.Parameters.AddWithValue("$s", ImmichService.ServerKey)
                        Using reader = cmd.ExecuteReader()
                            While reader.Read() : result.Add((reader.GetString(0), reader.GetInt32(1))) : End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.KIStichwortZahlen", ex)
            End Try
            Return result
        End Function

        Public Function GetAssetUpdatedAt(serverKey As String, assetId As String) As String
            If String.IsNullOrWhiteSpace(serverKey) OrElse String.IsNullOrWhiteSpace(assetId) Then Return ""
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT UpdatedAt FROM AssetList WHERE ServerKey=$s AND AssetId=$a LIMIT 1"
                        cmd.Parameters.AddWithValue("$s", serverKey) : cmd.Parameters.AddWithValue("$a", assetId)
                        Dim value = cmd.ExecuteScalar()
                        Return If(value Is Nothing OrElse value Is DBNull.Value, "", Convert.ToString(value, CultureInfo.InvariantCulture))
                    End Using
                End Using
            Catch
                Return ""
            End Try
        End Function

        Public Sub ReplaceAiTags(serverKey As String, assetId As String, sourceVersion As String, tags As IEnumerable(Of AiImageTag))
            If String.IsNullOrWhiteSpace(serverKey) OrElse String.IsNullOrWhiteSpace(assetId) Then Return
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using tx = conn.BeginTransaction()
                        Using deleteCmd = conn.CreateCommand()
                            deleteCmd.Transaction = tx
                            deleteCmd.CommandText = "DELETE FROM AiAssetTag WHERE ServerKey=$s AND AssetId=$a"
                            deleteCmd.Parameters.AddWithValue("$s", serverKey)
                            deleteCmd.Parameters.AddWithValue("$a", assetId)
                            deleteCmd.ExecuteNonQuery()
                        End Using
                        For Each tag In If(tags, Enumerable.Empty(Of AiImageTag)()).Where(Function(t) t IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(t.Canonical))
                            Using insert = conn.CreateCommand()
                                insert.Transaction = tx
                                insert.CommandText = "INSERT INTO AiAssetTag(ServerKey,AssetId,Canonical,Score,ModelKey,ModelVersion,SourceVersion) VALUES($s,$a,$c,$score,$k,$v,$source)"
                                insert.Parameters.AddWithValue("$s", serverKey) : insert.Parameters.AddWithValue("$a", assetId)
                                insert.Parameters.AddWithValue("$c", tag.Canonical.Trim()) : insert.Parameters.AddWithValue("$score", tag.Score)
                                insert.Parameters.AddWithValue("$k", If(tag.ModelKey, "")) : insert.Parameters.AddWithValue("$v", If(tag.ModelVersion, ""))
                                insert.Parameters.AddWithValue("$source", If(sourceVersion, ""))
                                insert.ExecuteNonQuery()
                            End Using
                        Next
                        Using scan = conn.CreateCommand()
                            scan.Transaction = tx
                            scan.CommandText = "INSERT INTO AiAssetScan(ServerKey,AssetId,SourceVersion,ModelKey,ModelVersion) VALUES($s,$a,$source,$k,$v) ON CONFLICT(ServerKey,AssetId) DO UPDATE SET SourceVersion=$source,ModelKey=$k,ModelVersion=$v"
                            scan.Parameters.AddWithValue("$s", serverKey) : scan.Parameters.AddWithValue("$a", assetId)
                            scan.Parameters.AddWithValue("$source", If(sourceVersion, ""))
                            scan.Parameters.AddWithValue("$k", ImageTaggingService.ModelKey) : scan.Parameters.AddWithValue("$v", ImageTaggingService.AnalysisVersion)
                            scan.ExecuteNonQuery()
                        End Using
                        tx.Commit()
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.ReplaceAiTags", ex)
            End Try
        End Sub

        Public Function AiTagsNeedRefresh(serverKey As String, assetId As String, sourceVersion As String) As Boolean
            If String.IsNullOrWhiteSpace(serverKey) OrElse String.IsNullOrWhiteSpace(assetId) Then Return False
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT SourceVersion,ModelKey,ModelVersion FROM AiAssetScan WHERE ServerKey=$s AND AssetId=$a"
                        cmd.Parameters.AddWithValue("$s", serverKey) : cmd.Parameters.AddWithValue("$a", assetId)
                        Using r = cmd.ExecuteReader()
                            Return Not r.Read() OrElse Not String.Equals(If(r.IsDBNull(0), "", r.GetString(0)), If(sourceVersion, ""), StringComparison.Ordinal) OrElse Not String.Equals(If(r.IsDBNull(1), "", r.GetString(1)), ImageTaggingService.ModelKey, StringComparison.Ordinal) OrElse Not String.Equals(If(r.IsDBNull(2), "", r.GetString(2)), ImageTaggingService.AnalysisVersion, StringComparison.Ordinal)
                        End Using
                    End Using
                End Using
            Catch
                Return True
            End Try
        End Function

        ''' <summary>Anzahl gecachter Einträge und ungefähre Dateigröße der Index-DB (für die Einstellungen).</summary>
        Public Function GetInfo() As (Count As Integer, SizeBytes As Long)
            Dim count = 0
            Dim size = 0L
            Try
                If File.Exists(_dbPath) Then size = New FileInfo(_dbPath).Length
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT COUNT(*) FROM AssetMeta"
                        count = Convert.ToInt32(cmd.ExecuteScalar())
                    End Using
                End Using
            Catch
            End Try
            Return (count, size)
        End Function

        ''' <summary>Leert den gesamten Immich-Metadaten-Index. Gibt die Zahl der entfernten Einträge zurück.</summary>
        Public Function Clear() As Integer
            Try
                Dim removed = 0
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using countCmd = conn.CreateCommand()
                        countCmd.CommandText = "SELECT COUNT(*) FROM AssetMeta"
                        removed = Convert.ToInt32(countCmd.ExecuteScalar())
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "DELETE FROM AssetMeta; DELETE FROM AiAssetTag; DELETE FROM AiAssetScan"
                        cmd.ExecuteNonQuery()
                    End Using
                    Using vac = conn.CreateCommand()
                        vac.CommandText = "VACUUM"
                        vac.ExecuteNonQuery()
                    End Using
                End Using
                Return removed
            Catch ex As Exception
                DiagnosticLogService.LogException("ImmichIndex.Clear", ex)
                Return 0
            End Try
        End Function

        Private Shared Function JoinTags(tags As List(Of String)) As String
            If tags Is Nothing OrElse tags.Count = 0 Then Return ""
            Return String.Join(vbLf, tags.Where(Function(t) Not String.IsNullOrWhiteSpace(t)))
        End Function

        Private Shared Function SplitTags(value As String) As List(Of String)
            If String.IsNullOrEmpty(value) Then Return New List(Of String)()
            Return value.Split(vbLf).Where(Function(t) Not String.IsNullOrWhiteSpace(t)).ToList()
        End Function

        Private Shared Function ParseDate(value As String) As DateTime?
            If String.IsNullOrWhiteSpace(value) Then Return Nothing
            Dim parsed As DateTime
            If DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, parsed) Then Return parsed
            Return Nothing
        End Function

    End Class

End Namespace
