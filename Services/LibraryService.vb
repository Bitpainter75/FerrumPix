Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text.RegularExpressions
Imports Microsoft.Data.Sqlite

Namespace Services

    Public Class LibraryImageMeta
        Public Property FilePath As String
        Public Property IsFavorite As Boolean
        Public Property Rating As Integer
        Public Property Tags As New List(Of String)()
        Public Property DateTaken As String = ""
        Public Property DateModifiedExif As String = ""
        Public Property Camera As String = ""
        Public Property Lens As String = ""
        Public Property Aperture As Double?
        Public Property FocalLengthMm As Double?
        Public Property Iso As Integer?
        Public Property ShutterSpeed As String = ""
        Public Property GpsLatitude As Double?
        Public Property GpsLongitude As Double?
        Public Property ImageWidth As Integer?
        Public Property ImageHeight As Integer?
        Public Property FileCreatedAt As String = ""
        Public Property HasExifMetadata As Boolean
        Public Property HasIptcMetadata As Boolean
        Public Property HasXmpMetadata As Boolean
        ''' <summary>Dateisystem-LastWriteTime (ISO-8601), das zum Zeitpunkt der letzten erfolgreichen
        ''' EXIF-Extraktion galt. Dient als Invalidierungs-Schlüssel: stimmt dieser Wert noch mit dem
        ''' aktuellen Dateisystem-Änderungsdatum überein, müssen EXIF-Daten nicht erneut gelesen werden.</summary>
        Public Property ScannedSourceModifiedAt As String = ""
    End Class

    Public Class LibraryService

        Private Shared _instance As LibraryService
        Private ReadOnly _connectionString As String

        Public Shared ReadOnly Property Instance As LibraryService
            Get
                If _instance Is Nothing Then _instance = New LibraryService()
                Return _instance
            End Get
        End Property

        Private Sub New()
            Dim dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix")
            Directory.CreateDirectory(dir)
            Dim dbPath = Path.Combine(dir, "library.db")
            _connectionString = $"Data Source={dbPath}"
            InitDb()
        End Sub

        Private Sub InitDb()
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                ' WAL statt des Standard-Rollback-Journals: Schreibvorgänge müssen nicht mehr bei
                ' jedem Commit auf die Haupt-DB-Datei fsyncen, sondern nur auf die WAL-Datei -
                ' spürbar schneller bei den vielen kleinen Writes (Rating/Favorit/Tags pro Bild).
                Using pragmaCmd = conn.CreateCommand()
                    pragmaCmd.CommandText = "PRAGMA journal_mode=WAL"
                    pragmaCmd.ExecuteNonQuery()
                End Using
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "CREATE TABLE IF NOT EXISTS ImageMeta (" &
                        "  FilePath TEXT PRIMARY KEY," &
                        "  IsFavorite INTEGER NOT NULL DEFAULT 0," &
                        "  Rating    INTEGER NOT NULL DEFAULT 0," &
                        "  Tags      TEXT    NOT NULL DEFAULT ''" &
                        ")"
                    cmd.ExecuteNonQuery()
                End Using
                EnsureExifColumns(conn)
            End Using
        End Sub

        ' SQLite kennt kein "ADD COLUMN IF NOT EXISTS" - deshalb erst per PRAGMA table_info
        ' prüfen, welche Spalten schon existieren, damit bestehende library.db-Dateien (aus
        ' Versionen ohne EXIF-Unterstützung) sicher migriert werden, ohne die Tabelle neu anzulegen.
        Private Shared ReadOnly ExifColumns As (Name As String, Sql As String)() = {
            ("DateTaken", "TEXT"),
            ("Camera", "TEXT"),
            ("Lens", "TEXT"),
            ("Aperture", "REAL"),
            ("FocalLengthMm", "REAL"),
            ("Iso", "INTEGER"),
            ("ShutterSpeed", "TEXT"),
            ("GpsLatitude", "REAL"),
            ("GpsLongitude", "REAL"),
            ("ImageWidth", "INTEGER"),
            ("ImageHeight", "INTEGER"),
            ("DateModifiedExif", "TEXT"),
            ("FileCreatedAt", "TEXT"),
            ("HasExifMetadata", "INTEGER NOT NULL DEFAULT 0"),
            ("HasIptcMetadata", "INTEGER NOT NULL DEFAULT 0"),
            ("HasXmpMetadata", "INTEGER NOT NULL DEFAULT 0"),
            ("ScannedSourceModifiedAt", "TEXT")
        }

        Private Shared Sub EnsureExifColumns(conn As SqliteConnection)
            Dim existing As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = "PRAGMA table_info(ImageMeta)"
                Using reader = cmd.ExecuteReader()
                    Dim nameOrdinal = reader.GetOrdinal("name")
                    While reader.Read()
                        existing.Add(reader.GetString(nameOrdinal))
                    End While
                End Using
            End Using

            For Each column In ExifColumns
                If existing.Contains(column.Name) Then Continue For
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = $"ALTER TABLE ImageMeta ADD COLUMN {column.Name} {column.Sql}"
                    cmd.ExecuteNonQuery()
                End Using
            Next
        End Sub

        Public Function GetFavorite(filePath As String) As Boolean
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT IsFavorite FROM ImageMeta WHERE FilePath=$p"
                    cmd.Parameters.AddWithValue("$p", filePath)
                    Dim r = cmd.ExecuteScalar()
                    Return r IsNot Nothing AndAlso Not TypeOf r Is DBNull AndAlso CInt(r) <> 0
                End Using
            End Using
        End Function

        Public Sub SetFavorite(filePath As String, isFavorite As Boolean)
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "INSERT INTO ImageMeta(FilePath,IsFavorite) VALUES($p,$f) " &
                        "ON CONFLICT(FilePath) DO UPDATE SET IsFavorite=$f"
                    cmd.Parameters.AddWithValue("$p", filePath)
                    cmd.Parameters.AddWithValue("$f", If(isFavorite, 1, 0))
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Public Function GetRating(filePath As String) As Integer
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT Rating FROM ImageMeta WHERE FilePath=$p"
                    cmd.Parameters.AddWithValue("$p", filePath)
                    Dim r = cmd.ExecuteScalar()
                    If r Is Nothing OrElse TypeOf r Is DBNull Then Return 0
                    Return CInt(r)
                End Using
            End Using
        End Function

        Public Function HasXmpMetadata(filePath As String) As Boolean
            If String.IsNullOrWhiteSpace(filePath) Then Return False
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT HasXmpMetadata FROM ImageMeta WHERE FilePath=$p"
                    cmd.Parameters.AddWithValue("$p", filePath)
                    Dim r = cmd.ExecuteScalar()
                    Return r IsNot Nothing AndAlso Not TypeOf r Is DBNull AndAlso CInt(r) <> 0
                End Using
            End Using
        End Function

        Public Sub SetRating(filePath As String, rating As Integer, Optional syncToXmp As Boolean = False)
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "INSERT INTO ImageMeta(FilePath,Rating) VALUES($p,$r) " &
                        "ON CONFLICT(FilePath) DO UPDATE SET Rating=$r"
                    cmd.Parameters.AddWithValue("$p", filePath)
                    cmd.Parameters.AddWithValue("$r", rating)
                    cmd.ExecuteNonQuery()
                End Using
            End Using

            If syncToXmp AndAlso HasXmpMetadata(filePath) Then
                ExifService.WriteXmpRatingSidecar(filePath, rating, createIfMissing:=True)
            End If
        End Sub

        ''' <summary>Setzt die Bewertung für mehrere Dateien in einer einzigen Transaktion/Verbindung
        ''' (statt einer eigenen Verbindung + eigenem Commit pro Datei) - wichtig bei Mehrfachauswahl.</summary>
        Public Sub SetRatingForMany(filePaths As IEnumerable(Of String), rating As Integer, Optional syncToXmp As Boolean = False)
            Dim list = If(filePaths, Enumerable.Empty(Of String)()).Where(Function(p) Not String.IsNullOrWhiteSpace(p)).ToList()
            If list.Count = 0 Then Return

            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using transaction = conn.BeginTransaction()
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = transaction
                        cmd.CommandText =
                            "INSERT INTO ImageMeta(FilePath,Rating) VALUES($p,$r) " &
                            "ON CONFLICT(FilePath) DO UPDATE SET Rating=$r"
                        Dim pParam = cmd.Parameters.Add("$p", SqliteType.Text)
                        Dim rParam = cmd.Parameters.Add("$r", SqliteType.Integer)
                        rParam.Value = rating
                        For Each path In list
                            pParam.Value = path
                            cmd.ExecuteNonQuery()
                        Next
                    End Using
                    transaction.Commit()
                End Using
            End Using

            If syncToXmp Then
                Dim metaByPath = GetMetaForPaths(list)
                For Each path In list
                    Dim meta As LibraryImageMeta = Nothing
                    If metaByPath.TryGetValue(path, meta) AndAlso meta IsNot Nothing AndAlso meta.HasXmpMetadata Then
                        ExifService.WriteXmpRatingSidecar(path, rating, createIfMissing:=True)
                    End If
                Next
            End If
        End Sub

        Public Function GetTags(filePath As String) As List(Of String)
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT Tags FROM ImageMeta WHERE FilePath=$p"
                    cmd.Parameters.AddWithValue("$p", filePath)
                    Dim r = cmd.ExecuteScalar()
                    If r Is Nothing OrElse TypeOf r Is DBNull OrElse String.IsNullOrWhiteSpace(r.ToString()) Then
                        Return New List(Of String)()
                    End If
                    Return ParseTags(r.ToString())
                End Using
            End Using
        End Function

        Public Function GetAllTags() As List(Of String)
            Dim result As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT Tags FROM ImageMeta WHERE Tags<>''"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            If reader.IsDBNull(0) Then Continue While
                            For Each tag In ParseTags(reader.GetString(0))
                                result.Add(tag)
                            Next
                        End While
                    End Using
                End Using
            End Using
            Return result.OrderBy(Function(t) t, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        Public Sub SetTags(filePath As String, tags As IEnumerable(Of String))
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "INSERT INTO ImageMeta(FilePath,Tags) VALUES($p,$t) " &
                        "ON CONFLICT(FilePath) DO UPDATE SET Tags=$t"
                    cmd.Parameters.AddWithValue("$p", filePath)
                    cmd.Parameters.AddWithValue("$t", String.Join(",", tags))
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' <summary>Speichert die durchsuchbaren EXIF-Felder für eine Datei, ohne Favorit/Bewertung/
        ''' Stichworte anzutasten (partielles Upsert wie bei SetFavorite/SetRating/SetTags). Liest
        ''' zusätzlich Dateisystem-Erstellungsdatum und -Änderungsdatum der Datei und schreibt Letzteres
        ''' als ScannedSourceModifiedAt-Snapshot mit - das ist der Invalidierungs-Schlüssel, der beim
        ''' nächsten Ordner-Scan entscheidet, ob diese EXIF-Daten noch aktuell sind.</summary>
        Public Sub SetExifData(filePath As String,
                               exif As ExifSearchFields,
                               Optional hasExifMetadata As Boolean = False,
                               Optional hasIptcMetadata As Boolean = False,
                               Optional hasXmpMetadata As Boolean = False)
            If String.IsNullOrWhiteSpace(filePath) OrElse exif Is Nothing Then Return

            Dim fileCreatedAt = ""
            Dim scannedSourceModifiedAt = ""
            Try
                Dim fi = New FileInfo(filePath)
                If fi.Exists Then
                    fileCreatedAt = fi.CreationTime.ToString("o")
                    scannedSourceModifiedAt = fi.LastWriteTime.ToString("o")
                End If
            Catch
            End Try

            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "INSERT INTO ImageMeta(FilePath,DateTaken,DateModifiedExif,Camera,Lens,Aperture,FocalLengthMm,Iso,ShutterSpeed,GpsLatitude,GpsLongitude,ImageWidth,ImageHeight,FileCreatedAt,HasExifMetadata,HasIptcMetadata,HasXmpMetadata,ScannedSourceModifiedAt) " &
                        "VALUES($p,$dateTaken,$dateModifiedExif,$camera,$lens,$aperture,$focalLength,$iso,$shutterSpeed,$gpsLat,$gpsLon,$width,$height,$fileCreatedAt,$hasExifMetadata,$hasIptcMetadata,$hasXmpMetadata,$scannedSourceModifiedAt) " &
                        "ON CONFLICT(FilePath) DO UPDATE SET " &
                        "DateTaken=excluded.DateTaken, DateModifiedExif=excluded.DateModifiedExif, Camera=excluded.Camera, Lens=excluded.Lens, " &
                        "Aperture=excluded.Aperture, FocalLengthMm=excluded.FocalLengthMm, Iso=excluded.Iso, " &
                        "ShutterSpeed=excluded.ShutterSpeed, GpsLatitude=excluded.GpsLatitude, GpsLongitude=excluded.GpsLongitude, " &
                        "ImageWidth=excluded.ImageWidth, ImageHeight=excluded.ImageHeight, " &
                        "FileCreatedAt=excluded.FileCreatedAt, HasExifMetadata=excluded.HasExifMetadata, " &
                        "HasIptcMetadata=excluded.HasIptcMetadata, HasXmpMetadata=excluded.HasXmpMetadata, " &
                        "ScannedSourceModifiedAt=excluded.ScannedSourceModifiedAt"
                    cmd.Parameters.AddWithValue("$p", filePath)
                    cmd.Parameters.AddWithValue("$dateTaken", If(exif.DateTaken, ""))
                    cmd.Parameters.AddWithValue("$dateModifiedExif", If(exif.DateModifiedExif, ""))
                    cmd.Parameters.AddWithValue("$camera", If(exif.Camera, ""))
                    cmd.Parameters.AddWithValue("$lens", If(exif.Lens, ""))
                    cmd.Parameters.AddWithValue("$aperture", NullableToDbValue(exif.Aperture))
                    cmd.Parameters.AddWithValue("$focalLength", NullableToDbValue(exif.FocalLengthMm))
                    cmd.Parameters.AddWithValue("$iso", NullableToDbValue(exif.Iso))
                    cmd.Parameters.AddWithValue("$shutterSpeed", If(exif.ShutterSpeed, ""))
                    cmd.Parameters.AddWithValue("$gpsLat", NullableToDbValue(exif.GpsLatitude))
                    cmd.Parameters.AddWithValue("$gpsLon", NullableToDbValue(exif.GpsLongitude))
                    cmd.Parameters.AddWithValue("$width", NullableToDbValue(exif.ImageWidth))
                    cmd.Parameters.AddWithValue("$height", NullableToDbValue(exif.ImageHeight))
                    cmd.Parameters.AddWithValue("$fileCreatedAt", fileCreatedAt)
                    cmd.Parameters.AddWithValue("$hasExifMetadata", If(hasExifMetadata, 1, 0))
                    cmd.Parameters.AddWithValue("$hasIptcMetadata", If(hasIptcMetadata, 1, 0))
                    cmd.Parameters.AddWithValue("$hasXmpMetadata", If(hasXmpMetadata, 1, 0))
                    cmd.Parameters.AddWithValue("$scannedSourceModifiedAt", scannedSourceModifiedAt)
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        Private Const MetaColumnList As String =
            "FilePath, IsFavorite, Rating, Tags, DateTaken, Camera, Lens, Aperture, FocalLengthMm, Iso, ShutterSpeed, GpsLatitude, GpsLongitude, ImageWidth, ImageHeight, DateModifiedExif, FileCreatedAt, HasExifMetadata, HasIptcMetadata, HasXmpMetadata, ScannedSourceModifiedAt"

        Private Shared Function ReadMetaRow(reader As SqliteDataReader) As LibraryImageMeta
            Return New LibraryImageMeta With {
                .FilePath = reader.GetString(0),
                .IsFavorite = reader.GetInt32(1) <> 0,
                .Rating = reader.GetInt32(2),
                .Tags = If(reader.IsDBNull(3), New List(Of String)(), ParseTags(reader.GetString(3))),
                .DateTaken = If(reader.IsDBNull(4), "", reader.GetString(4)),
                .Camera = If(reader.IsDBNull(5), "", reader.GetString(5)),
                .Lens = If(reader.IsDBNull(6), "", reader.GetString(6)),
                .Aperture = If(reader.IsDBNull(7), CType(Nothing, Double?), reader.GetDouble(7)),
                .FocalLengthMm = If(reader.IsDBNull(8), CType(Nothing, Double?), reader.GetDouble(8)),
                .Iso = If(reader.IsDBNull(9), CType(Nothing, Integer?), reader.GetInt32(9)),
                .ShutterSpeed = If(reader.IsDBNull(10), "", reader.GetString(10)),
                .GpsLatitude = If(reader.IsDBNull(11), CType(Nothing, Double?), reader.GetDouble(11)),
                .GpsLongitude = If(reader.IsDBNull(12), CType(Nothing, Double?), reader.GetDouble(12)),
                .ImageWidth = If(reader.IsDBNull(13), CType(Nothing, Integer?), reader.GetInt32(13)),
                .ImageHeight = If(reader.IsDBNull(14), CType(Nothing, Integer?), reader.GetInt32(14)),
                .DateModifiedExif = If(reader.IsDBNull(15), "", reader.GetString(15)),
                .FileCreatedAt = If(reader.IsDBNull(16), "", reader.GetString(16)),
                .HasExifMetadata = Not reader.IsDBNull(17) AndAlso reader.GetInt32(17) <> 0,
                .HasIptcMetadata = Not reader.IsDBNull(18) AndAlso reader.GetInt32(18) <> 0,
                .HasXmpMetadata = Not reader.IsDBNull(19) AndAlso reader.GetInt32(19) <> 0,
                .ScannedSourceModifiedAt = If(reader.IsDBNull(20), "", reader.GetString(20))
            }
        End Function

        ''' <summary>Lädt alle Metadaten (inkl. EXIF) für alle Dateien im angegebenen Ordner in einem einzigen Query.</summary>
        Public Function GetFolderMeta(folderPath As String) As Dictionary(Of String, LibraryImageMeta)
            Dim result As New Dictionary(Of String, LibraryImageMeta)(StringComparer.OrdinalIgnoreCase)
            Dim prefix = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) & Path.DirectorySeparatorChar
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = $"SELECT {MetaColumnList} FROM ImageMeta WHERE FilePath LIKE $prefix"
                    cmd.Parameters.AddWithValue("$prefix", prefix & "%")
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            Dim meta = ReadMetaRow(reader)
                            result(meta.FilePath) = meta
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        Public Function GetMetaForPaths(paths As IEnumerable(Of String)) As Dictionary(Of String, LibraryImageMeta)
            Dim result As New Dictionary(Of String, LibraryImageMeta)(StringComparer.OrdinalIgnoreCase)
            Dim list = If(paths, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
            If list.Count = 0 Then Return result

            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                For i = 0 To list.Count - 1 Step 500
                    Dim chunk = list.Skip(i).Take(500).ToList()
                    Using cmd = conn.CreateCommand()
                        Dim parameterNames As New List(Of String)()
                        For index = 0 To chunk.Count - 1
                            Dim parameterName = "$p" & index
                            parameterNames.Add(parameterName)
                            cmd.Parameters.AddWithValue(parameterName, chunk(index))
                        Next
                        cmd.CommandText =
                            $"SELECT {MetaColumnList} FROM ImageMeta WHERE FilePath IN (" &
                            String.Join(",", parameterNames) & ")"
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                Dim meta = ReadMetaRow(reader)
                                result(meta.FilePath) = meta
                            End While
                        End Using
                    End Using
                Next
            End Using
            Return result
        End Function

        Public Function GetFavoriteImages() As List(Of LibraryImageMeta)
            Return QueryImageMeta("WHERE IsFavorite<>0")
        End Function

        Public Function GetAllImages() As List(Of LibraryImageMeta)
            Return QueryImageMeta("")
        End Function

        Public Function SearchImages(query As String) As List(Of LibraryImageMeta)
            query = If(query, "").Trim()
            If String.IsNullOrWhiteSpace(query) Then Return New List(Of LibraryImageMeta)()
            Dim criteria = ParseSearchCriteria(query)

            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    Dim whereParts As New List(Of String)()
                    If Not String.IsNullOrWhiteSpace(criteria.TextQuery) Then
                        whereParts.Add("(FilePath LIKE $q OR Tags LIKE $q OR Camera LIKE $q OR Lens LIKE $q)")
                        cmd.Parameters.AddWithValue("$q", "%" & criteria.TextQuery & "%")
                    End If
                    If criteria.Rating.HasValue Then
                        whereParts.Add("Rating " & criteria.RatingOperator & " $rating")
                        cmd.Parameters.AddWithValue("$rating", criteria.Rating.Value)
                    End If
                    If criteria.IsFavorite.HasValue Then
                        whereParts.Add("IsFavorite=$favorite")
                        cmd.Parameters.AddWithValue("$favorite", If(criteria.IsFavorite.Value, 1, 0))
                    End If
                    If whereParts.Count = 0 Then Return New List(Of LibraryImageMeta)()

                    cmd.CommandText =
                        $"SELECT {MetaColumnList} FROM ImageMeta WHERE " &
                        String.Join(" AND ", whereParts)
                    Return ReadImageMeta(cmd)
                End Using
            End Using
        End Function

        Private Class ImageSearchCriteria
            Public Property TextQuery As String = ""
            Public Property Rating As Integer?
            Public Property RatingOperator As String = "="
            Public Property IsFavorite As Boolean?
        End Class

        Private Shared Function ParseSearchCriteria(query As String) As ImageSearchCriteria
            Dim result = New ImageSearchCriteria With {.TextQuery = If(query, "").Trim()}
            If String.IsNullOrWhiteSpace(result.TextQuery) Then Return result

            Dim favoriteMatch = Regex.Match(result.TextQuery, "(?i)\b(?:is:)?(?:fav(?:orit(?:e|en)?)?|favorite)\s*[:=]?\s*(true|ja|yes|1)?\b")
            Dim notFavoriteMatch = Regex.Match(result.TextQuery, "(?i)\b(?:kein(?:e|en)?|not|ohne)\s+(?:fav(?:orit(?:e|en)?)?|favorite)\b|\b(?:fav(?:orit(?:e|en)?)?|favorite)\s*[:=]\s*(false|nein|no|0)\b")
            If notFavoriteMatch.Success Then
                result.IsFavorite = False
                result.TextQuery = RemoveMatch(result.TextQuery, notFavoriteMatch)
            ElseIf favoriteMatch.Success Then
                result.IsFavorite = True
                result.TextQuery = RemoveMatch(result.TextQuery, favoriteMatch)
            End If

            Dim starMatch = Regex.Match(result.TextQuery, "([★☆]{1,5})")
            If starMatch.Success Then
                Dim filled = starMatch.Groups(1).Value.Count(Function(c) c = "★"c)
                If filled > 0 Then
                    result.Rating = Math.Min(5, filled)
                    result.RatingOperator = "="
                    result.TextQuery = RemoveMatch(result.TextQuery, starMatch)
                    Return result
                End If
            End If

            Dim ratingPattern = "(?i)\b(?:rating|bewertung|stars?|sterne?)\s*[:=]?\s*(>=|<=|>|<|=)?\s*([0-5])\b"
            Dim ratingMatch = Regex.Match(result.TextQuery, ratingPattern)
            If Not ratingMatch.Success Then
                ratingPattern = "(?i)\b(>=|<=|>|<|=)?\s*([0-5])\s*(?:sterne?|stars?)\b"
                ratingMatch = Regex.Match(result.TextQuery, ratingPattern)
            End If
            If Not ratingMatch.Success Then
                ratingPattern = "(?i)\b(?:ab|mindestens|min)\s*([0-5])\s*(?:sterne?|stars?)?\b"
                ratingMatch = Regex.Match(result.TextQuery, ratingPattern)
                If ratingMatch.Success Then
                    result.RatingOperator = ">="
                    result.Rating = Math.Max(0, Math.Min(5, Integer.Parse(ratingMatch.Groups(1).Value)))
                    result.TextQuery = RemoveMatch(result.TextQuery, ratingMatch)
                    Return result
                End If
            End If

            If ratingMatch.Success Then
                Dim op = ratingMatch.Groups(1).Value
                If String.IsNullOrWhiteSpace(op) Then op = "="
                result.RatingOperator = op
                result.Rating = Math.Max(0, Math.Min(5, Integer.Parse(ratingMatch.Groups(2).Value)))
                result.TextQuery = RemoveMatch(result.TextQuery, ratingMatch)
            End If

            Return result
        End Function

        Private Shared Function RemoveMatch(value As String, match As Match) As String
            Dim text = value.Remove(match.Index, match.Length)
            Return Regex.Replace(text, "\s{2,}", " ").Trim()
        End Function

        Private Function QueryImageMeta(whereClause As String) As List(Of LibraryImageMeta)
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = $"SELECT {MetaColumnList} FROM ImageMeta " & whereClause
                    Return ReadImageMeta(cmd)
                End Using
            End Using
        End Function

        Private Shared Function ReadImageMeta(cmd As SqliteCommand) As List(Of LibraryImageMeta)
            Dim result As New List(Of LibraryImageMeta)()
            Using reader = cmd.ExecuteReader()
                While reader.Read()
                    result.Add(ReadMetaRow(reader))
                End While
            End Using
            Return result
        End Function

        Private Shared Function NullableToDbValue(Of T As Structure)(value As T?) As Object
            If value.HasValue Then Return value.Value
            Return DBNull.Value
        End Function

        Private Shared Function ParseTags(value As String) As List(Of String)
            If String.IsNullOrWhiteSpace(value) Then Return New List(Of String)()
            Return value.Split(","c, StringSplitOptions.RemoveEmptyEntries).
                Select(Function(t) t.Trim()).
                Where(Function(t) Not String.IsNullOrWhiteSpace(t)).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
        End Function

        ''' <summary>Entfernt Metadaten-Einträge, deren Bilddatei nicht mehr existiert. Gibt die Anzahl gelöschter Einträge zurück.</summary>
        Public Function PurgeOrphanedRecords() As Integer
            Dim orphans As New List(Of String)()
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT FilePath FROM ImageMeta"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            Dim p = reader.GetString(0)
                            If Not File.Exists(p) Then orphans.Add(p)
                        End While
                    End Using
                End Using
                If orphans.Count > 0 Then
                    Using transaction = conn.BeginTransaction()
                        Using cmd = conn.CreateCommand()
                            cmd.Transaction = transaction
                            cmd.CommandText = "DELETE FROM ImageMeta WHERE FilePath=$p"
                            Dim pParam = cmd.Parameters.Add("$p", SqliteType.Text)
                            For Each p In orphans
                                pParam.Value = p
                                cmd.ExecuteNonQuery()
                            Next
                        End Using
                        transaction.Commit()
                    End Using
                End If
            End Using
            Return orphans.Count
        End Function

    End Class

End Namespace
