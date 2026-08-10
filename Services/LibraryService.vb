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
        ''' Farbetikett fürs Culling ("Red","Yellow","Green","Blue","Purple", "" = keins) -
        ''' rein lokale FerrumPix-Zuordnung, wird in keine Datei geschrieben.
        Public Property ColorLabel As String = ""
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
        ''' <summary>Ortsname und Land. Sie stammen ENTWEDER aus den Metadaten der Datei
        ''' (IPTC/XMP, dort hat jemand sie bewusst gesetzt) ODER aus der Ortstabelle. Das erste
        ''' hat Vorrang: wer seine Fotos von Hand mit Orten versehen hat, will die sehen und
        ''' nicht den naechstgelegenen Ort ab 1000 Einwohnern.</summary>
        Public Property City As String = ""
        Public Property Country As String = ""

        ''' <summary>Laenderkuerzel nach ISO 3166 - der Schluessel zur Uebersetzung des Landesnamens
        ''' (siehe PlaceLookupService.LocalizedCountry). Leer bei Eintraegen, deren Ort aus der Datei
        ''' selbst stammt oder die vor dieser Spalte eingelesen wurden.</summary>
        Public Property CountryCode As String = ""
        Public Property ImageWidth As Integer?
        Public Property ImageHeight As Integer?
        Public Property FileCreatedAt As String = ""
        Public Property HasExifMetadata As Boolean
        Public Property HasIptcMetadata As Boolean
        Public Property HasXmpMetadata As Boolean
        Public Property HasIccProfile As Boolean
        ''' Vorformatierte "Name: Wert"-Zeilen (siehe ExifService.BuildCatalogSummary) für das
        ''' Metadaten-Hover-Overlay - werden separat von den Has*Metadata-Flags gecacht, da diese sonst
        ''' bei jedem warmen Cache-Treffer leer blieben (Overlay zeigte "vorhanden", aber keinen Inhalt).
        Public Property ExifSummary As String = ""
        Public Property IptcSummary As String = ""
        Public Property XmpSummary As String = ""
        Public Property IccSummary As String = ""
        ''' <summary>Format+Sprache der obigen Summary-Texte (ExifService.CurrentSummaryFormat). Weicht der
        ''' Stempel vom aktuellen ab, stammt der Eintrag aus einer älteren App-Version oder einer anderen
        ''' Anzeigesprache und wird beim nächsten Ordner-Scan einmalig neu erzeugt.</summary>
        Public Property SummaryFormat As String = ""
        ''' <summary>Dateisystem-LastWriteTime (ISO-8601), das zum Zeitpunkt der letzten erfolgreichen
        ''' EXIF-Extraktion galt. Dient als Invalidierungs-Schlüssel: stimmt dieser Wert noch mit dem
        ''' aktuellen Dateisystem-Änderungsdatum überein, müssen EXIF-Daten nicht erneut gelesen werden.</summary>
        Public Property ScannedSourceModifiedAt As String = ""
        ''' <summary>Dasselbe für die XMP-Beistelldatei, falls es eine gibt (leer sonst). Sie braucht einen
        ''' EIGENEN Stempel: ein Fremdprogramm ändert Bewertung oder Stichworte in "foto.cr2.xmp", ohne die
        ''' Bilddatei anzufassen. Ohne diesen Wert bliebe der Schnappschuss "frisch", der Hintergrundscan
        ''' überspränge die Datei, und der Sidecar-Import wäre bei bereits eingelesenen Ordnern wirkungslos.</summary>
        Public Property ScannedSidecarModifiedAt As String = ""
    End Class

    ''' Der Personen-Teil liegt in LibraryServicePeople.vb - eigene Datei, weil er eine
    ''' zusammenhaengende eigene Feldgruppe hat (Face, Person, ScannedImage) und mit dem Rest der
    ''' Bibliothek nur die Verbindungszeichenfolge teilt.
    Partial Public Class LibraryService

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
                EnsurePeopleTables(conn)
                EnsureFaceColumns(conn)
            End Using
        End Sub

        ''' <summary>Die beiden Tabellen fuer Personen. Eigene Tabellen statt Spalten in ImageMeta:
        ''' ein Bild kann mehrere Gesichter tragen, und eine Person steht in vielen Bildern - das
        ''' passt in keine Spalte.
        '''
        ''' Face haelt den Merkmalsvektor als BLOB (128 Single, 512 Byte). Als Text waere er dreimal
        ''' so gross und muesste bei jedem Vergleich geparst werden; verglichen wird bei jedem neuen
        ''' Gesicht gegen den ganzen Bestand.
        '''
        ''' ScannedAt und SourceModifiedAt tragen den Wiederholungslauf: ein zweiter Durchgang ueber
        ''' denselben Ordner soll nur anfassen, was neu oder geaendert ist. Deshalb wird auch ein
        ''' Bild OHNE Gesichter vermerkt (Zeile in ScannedImage) - sonst wuerde es bei jedem Lauf
        ''' erneut durchsucht, und gerade Landschaftsordner bestehen fast nur daraus.
        '''
        ''' PersonId ist NULL, solange ein Gesicht keiner Gruppe zugeordnet ist. Eine Person ohne
        ''' Namen ist ausdruecklich erlaubt: die Gruppierung entsteht automatisch, der Name kommt
        ''' vom Benutzer und oft erst viel spaeter.
        '''
        ''' IsManual heisst: der Benutzer hat dieses Gesicht angefasst - einen Namen eingetragen oder
        ''' eine Fehlzuordnung geloest. Ein erneuter Durchlauf laesst es dann stehen, statt es zu
        ''' loeschen und neu zu erkennen. Fest ist genau das ANGEFASSTE Gesicht und nicht die ganze
        ''' Gruppe: sonst waere nach dem Benennen einer Person mit hundert Bildern keine einzige
        ''' Fehlzuordnung darin je wieder zu berichtigen.</summary>
        Private Shared Sub EnsurePeopleTables(conn As SqliteConnection)
            Using cmd = conn.CreateCommand()
                cmd.CommandText =
                    "CREATE TABLE IF NOT EXISTS Person (" &
                    "  Id        TEXT PRIMARY KEY," &
                    "  Name      TEXT NOT NULL DEFAULT ''," &
                    "  CreatedAt TEXT NOT NULL DEFAULT ''" &
                    ");" &
                    "CREATE TABLE IF NOT EXISTS Face (" &
                    "  Id          TEXT PRIMARY KEY," &
                    "  FilePath    TEXT NOT NULL," &
                    "  PersonId    TEXT," &
                    "  X           REAL NOT NULL," &
                    "  Y           REAL NOT NULL," &
                    "  Width       REAL NOT NULL," &
                    "  Height      REAL NOT NULL," &
                    "  Score       REAL NOT NULL DEFAULT 0," &
                    "  Embedding   BLOB," &
                    "  ScannedAt   TEXT NOT NULL DEFAULT ''," &
                    "  IsManual    INTEGER NOT NULL DEFAULT 0" &
                    ");" &
                    "CREATE TABLE IF NOT EXISTS ScannedImage (" &
                    "  FilePath         TEXT PRIMARY KEY," &
                    "  SourceModifiedAt TEXT NOT NULL DEFAULT ''," &
                    "  FaceCount        INTEGER NOT NULL DEFAULT 0," &
                    "  ScannedAt        TEXT NOT NULL DEFAULT ''" &
                    ");" &
                    "CREATE INDEX IF NOT EXISTS IX_Face_FilePath ON Face(FilePath);" &
                    "CREATE INDEX IF NOT EXISTS IX_Face_PersonId ON Face(PersonId);"
                cmd.ExecuteNonQuery()
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
            ("ScannedSourceModifiedAt", "TEXT"),
            ("ExifSummary", "TEXT"),
            ("IptcSummary", "TEXT"),
            ("XmpSummary", "TEXT"),
            ("HasIccProfile", "INTEGER NOT NULL DEFAULT 0"),
            ("IccSummary", "TEXT"),
            ("SummaryFormat", "TEXT"),
            ("ColorLabel", "TEXT"),
            ("ScannedSidecarModifiedAt", "TEXT"),
            ("City", "TEXT"),
            ("Country", "TEXT"),
            ("CountryCode", "TEXT")
        }

        ''' <summary>Spalten, die spaeter zur Gesichtstabelle dazugekommen sind. Bestehende
        ''' Bibliotheken tragen sie nicht - ohne dieses Nachziehen faende jede Abfrage darauf
        ''' nichts.</summary>
        Private Shared ReadOnly FaceColumns As (Name As String, Sql As String)() = {
            ("IsManual", "INTEGER NOT NULL DEFAULT 0")
        }

        ''' <summary>Dasselbe fuer die Personentabelle. IsUnknownBin kennzeichnet die EINE Gruppe,
        ''' in die herausgeloeste Gesichter wandern.</summary>
        Private Shared ReadOnly PersonColumns As (Name As String, Sql As String)() = {
            ("IsUnknownBin", "INTEGER NOT NULL DEFAULT 0")
        }

        Private Shared Sub EnsureFaceColumns(conn As SqliteConnection)
            AddMissingColumns(conn, "Face", FaceColumns)
            AddMissingColumns(conn, "Person", PersonColumns)
        End Sub

        Private Shared Sub AddMissingColumns(conn As SqliteConnection, table As String,
                                             columns As (Name As String, Sql As String)())
            Dim existing As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Using cmd = conn.CreateCommand()
                cmd.CommandText = $"PRAGMA table_info({table})"
                Using reader = cmd.ExecuteReader()
                    Dim nameOrdinal = reader.GetOrdinal("name")
                    While reader.Read()
                        existing.Add(reader.GetString(nameOrdinal))
                    End While
                End Using
            End Using

            For Each column In columns
                If existing.Contains(column.Name) Then Continue For
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column.Name} {column.Sql}"
                    cmd.ExecuteNonQuery()
                End Using
            Next
        End Sub

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
            SyncCatalogToFpxmp(filePath)
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

            ' Katalog (Rating/Label/Stichworte) optional zusätzlich ins XMP-Sidecar - gegated über die
            ' Einstellung SyncCatalogToXmp (siehe SyncCatalogToXmpSidecar). .fpxmp bleibt primär.
            SyncCatalogToFpxmp(filePath)
            If syncToXmp Then SyncCatalogToXmpSidecar(filePath)
        End Sub

        Public Function GetColorLabel(filePath As String) As String
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT ColorLabel FROM ImageMeta WHERE FilePath=$p"
                    cmd.Parameters.AddWithValue("$p", filePath)
                    Dim r = cmd.ExecuteScalar()
                    If r Is Nothing OrElse TypeOf r Is DBNull Then Return ""
                    Return CStr(r)
                End Using
            End Using
        End Function

        ''' <summary>Übernimmt Bewertung, Favorit, Farbetikett und Stichworte eines Eintrags auf
        ''' eine NEUE Datei - für „Speichern unter" und „Konvertieren nach"
        ''': das Katalog-Wissen wandert zur Kopie mit, das Original behält seins.
        ''' Die Flags kommen aus den Einzeloptionen des Speichern-unter-/Konvertieren-Dialogs.</summary>
        Public Sub CopyEntryMeta(sourcePath As String, targetPath As String,
                                 Optional copyRating As Boolean = True,
                                 Optional copyFavorite As Boolean = True,
                                 Optional copyColorLabel As Boolean = True,
                                 Optional copyKeywords As Boolean = True)
            If String.IsNullOrWhiteSpace(sourcePath) OrElse String.IsNullOrWhiteSpace(targetPath) Then Return
            If String.Equals(sourcePath, targetPath, PathIdentity.Comparison) Then Return
            Try
                If copyRating Then
                    Dim rating = GetRating(sourcePath)
                    If rating > 0 Then SetRating(targetPath, rating)
                End If
                If copyFavorite AndAlso GetFavorite(sourcePath) Then SetFavorite(targetPath, True)
                If copyColorLabel Then
                    Dim label = GetColorLabel(sourcePath)
                    If Not String.IsNullOrEmpty(label) Then SetColorLabelForMany({targetPath}, label)
                End If
                If copyKeywords Then
                    Dim tags = GetTags(sourcePath)
                    If tags.Count > 0 Then SetTags(targetPath, tags)
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Library.CopyEntryMeta", ex)
            End Try
        End Sub

        ''' <summary>Alle vergebenen Farbetiketten auf einmal (Pfad → Hex). Für die Galerie-Kacheln von
        ''' Immich-Items: statt zehntausende Pseudo-Pfade einzeln abzufragen, wird die kleine Menge
        ''' tatsächlich etikettierter Einträge geladen und darüber zugeordnet.</summary>
        Public Function GetAllColorLabels() As Dictionary(Of String, String)
            Dim result As New Dictionary(Of String, String)(PathIdentity.Comparer)
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT FilePath, ColorLabel FROM ImageMeta WHERE ColorLabel IS NOT NULL AND ColorLabel <> ''"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            result(reader.GetString(0)) = reader.GetString(1)
                        End While
                    End Using
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Setzt das Farbetikett für mehrere Dateien in einer Transaktion (Mehrfachauswahl).
        ''' Leerstring = Etikett entfernen.</summary>
        Public Sub SetColorLabelForMany(filePaths As IEnumerable(Of String), colorLabel As String, Optional syncToXmp As Boolean = False)
            Dim list = If(filePaths, Enumerable.Empty(Of String)()).Where(Function(p) Not String.IsNullOrWhiteSpace(p)).ToList()
            If list.Count = 0 Then Return
            Dim value = If(colorLabel, "")

            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using transaction = conn.BeginTransaction()
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = transaction
                        cmd.CommandText =
                            "INSERT INTO ImageMeta(FilePath,ColorLabel) VALUES($p,$c) " &
                            "ON CONFLICT(FilePath) DO UPDATE SET ColorLabel=$c"
                        Dim pParam = cmd.Parameters.Add("$p", SqliteType.Text)
                        Dim cParam = cmd.Parameters.Add("$c", SqliteType.Text)
                        For Each path In list
                            pParam.Value = path
                            cParam.Value = value
                            cmd.ExecuteNonQuery()
                        Next
                    End Using
                    transaction.Commit()
                End Using
            End Using

            For Each path In list
                SyncCatalogToFpxmp(path)
            Next
            If syncToXmp Then
                For Each path In list
                    SyncCatalogToXmpSidecar(path)
                Next
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

            For Each path In list
                SyncCatalogToFpxmp(path)
            Next
            If syncToXmp Then
                For Each path In list
                    SyncCatalogToXmpSidecar(path)
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

        ''' <summary>Jedes benutzte Stichwort mit der Anzahl Bilder, die es tragen.
        '''
        ''' Gezaehlt wird in der Anwendung und nicht in SQL: die Stichwoerter stehen als eine
        ''' Zeichenkette je Zeile, eine Aufteilung in SQLite waere eine rekursive Abfrage fuer
        ''' dasselbe Ergebnis. Eine Datei zaehlt je Stichwort einmal, auch wenn sie es doppelt
        ''' traegt.</summary>
        Public Function GetTagCounts() As List(Of (Tag As String, Count As Integer))
            Dim counts As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT Tags FROM ImageMeta WHERE Tags<>''"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            If reader.IsDBNull(0) Then Continue While
                            For Each tag In ParseTags(reader.GetString(0)).Distinct(StringComparer.OrdinalIgnoreCase)
                                Dim before = 0
                                counts.TryGetValue(tag, before)
                                counts(tag) = before + 1
                            Next
                        End While
                    End Using
                End Using
            End Using
            Return counts.OrderBy(Function(p) p.Key, StringComparer.OrdinalIgnoreCase).
                          Select(Function(p) (p.Key, p.Value)).ToList()
        End Function

        Public Sub SetTags(filePath As String, tags As IEnumerable(Of String), Optional syncToXmp As Boolean = False)
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

            SyncCatalogToFpxmp(filePath)
            If syncToXmp Then SyncCatalogToXmpSidecar(filePath)
        End Sub

        ''' <summary>Importiert den Katalogblock einer vorhandenen .fpxmp exakt in SQLite. Die
        ''' Sidecar ist fuer RAW/PSD die portable Quelle; fehlende Felder alter Zwischenversionen
        ''' lassen den jeweiligen Katalogwert unangetastet. Es wird absichtlich direkt geschrieben,
        ''' damit der Import nicht dieselbe Sidecar erneut speichert und ihren Frische-Stempel bewegt.</summary>
        Public Function ImportFpxmpCatalogData(filePath As String) As RawSidecarService.RawSidecarCatalogData
            If String.IsNullOrWhiteSpace(filePath) OrElse Not RawSidecarService.IsSidecarFormat(filePath) Then Return Nothing
            Dim data = RawSidecarService.TryReadCatalog(filePath)
            If data Is Nothing Then Return Nothing

            Try
                Dim currentRating = GetRating(filePath)
                Dim currentFavorite = GetFavorite(filePath)
                Dim currentColorLabel = GetColorLabel(filePath)
                Dim currentTags = GetTags(filePath)
                Dim rating = If(data.Rating, currentRating)
                Dim favorite = If(data.IsFavorite, currentFavorite)
                Dim colorLabel = If(data.ColorLabel Is Nothing, currentColorLabel, data.ColorLabel)
                Dim tags = If(data.HasKeywords, data.Keywords, currentTags)
                Dim tagsMatch = currentTags.Count = tags.Count AndAlso
                                currentTags.All(Function(value) tags.Any(Function(other) String.Equals(value, other, StringComparison.OrdinalIgnoreCase)))
                If currentRating = rating AndAlso currentFavorite = favorite AndAlso
                   String.Equals(currentColorLabel, colorLabel, StringComparison.OrdinalIgnoreCase) AndAlso tagsMatch Then
                    Return data
                End If
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText =
                            "INSERT INTO ImageMeta(FilePath,Rating,IsFavorite,ColorLabel,Tags) VALUES($p,$r,$f,$c,$t) " &
                            "ON CONFLICT(FilePath) DO UPDATE SET Rating=$r,IsFavorite=$f,ColorLabel=$c,Tags=$t"
                        cmd.Parameters.AddWithValue("$p", filePath)
                        cmd.Parameters.AddWithValue("$r", Math.Max(0, Math.Min(5, rating)))
                        cmd.Parameters.AddWithValue("$f", If(favorite, 1, 0))
                        cmd.Parameters.AddWithValue("$c", If(colorLabel, ""))
                        cmd.Parameters.AddWithValue("$t", String.Join(",", If(tags, New List(Of String)())))
                        cmd.ExecuteNonQuery()
                    End Using
                End Using
                Return data
            Catch ex As Exception
                DiagnosticLogService.LogException("Library.ImportFpxmpCatalogData", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Jede bewusste lokale Katalogaenderung an RAW/PSD wird ohne Einstellungs-Gate in
        ''' die primaere .fpxmp gespiegelt. Ein vorhandenes Entwicklungsrezept bleibt dabei erhalten.</summary>
        Private Sub SyncCatalogToFpxmp(filePath As String)
            If String.IsNullOrWhiteSpace(filePath) OrElse Not RawSidecarService.IsSidecarFormat(filePath) Then Return
            Try
                RawSidecarService.TryWriteCatalog(filePath,
                                                  GetRating(filePath),
                                                  GetFavorite(filePath),
                                                  GetColorLabel(filePath),
                                                  GetTags(filePath))
            Catch ex As Exception
                DiagnosticLogService.LogException("Library.SyncCatalogToFpxmp", ex)
            End Try
        End Sub

        ''' <summary>Schreibt den aktuellen Katalog (Rating/Farb-Label/Stichworte) dieser Datei zusätzlich
        ''' in ein Adobe-XMP-Sidecar - nur wenn die Einstellung <c>SyncCatalogToXmp</c> an ist. Legt nur
        ''' bei <c>CreateXmpSidecarIfMissing</c> eine neue Datei an. .fpxmp bleibt die primäre Quelle.
        ''' Wird ausschließlich aus BEWUSSTEN Katalog-Änderungen aufgerufen (nicht aus Kopier-/Importwegen),
        ''' damit XMP nicht als Nebenwirkung entsteht.</summary>
        Private Sub SyncCatalogToXmpSidecar(filePath As String)
            If String.IsNullOrWhiteSpace(filePath) Then Return
            Try
                Dim settings = AppSettingsService.Load()
                If Not settings.SyncCatalogToXmp Then Return
                Dim labelWord = XmpSidecarService.LabelToXmpWord(GetColorLabel(filePath))
                ExifService.WriteXmpCatalogSidecar(filePath, GetRating(filePath), labelWord, GetTags(filePath),
                                                   settings.CreateXmpSidecarIfMissing)
            Catch ex As Exception
                DiagnosticLogService.LogException("Library.SyncCatalogToXmpSidecar", ex)
            End Try
        End Sub

        ''' <summary>Speichert die durchsuchbaren EXIF-Felder für eine Datei, ohne Favorit/Bewertung/
        ''' Stichworte anzutasten (partielles Upsert wie bei SetFavorite/SetRating/SetTags). Liest
        ''' zusätzlich Dateisystem-Erstellungsdatum und -Änderungsdatum der Datei und schreibt Letzteres
        ''' als ScannedSourceModifiedAt-Snapshot mit - das ist der Invalidierungs-Schlüssel, der beim
        ''' <summary>Wird nach JEDEM frischen Auslesen von Metadaten aus einer Datei aufgerufen (Viewer/
        ''' Editor-Bildwechsel, Gallery-Scan, Suchauswertung) - vergleicht das Ergebnis mit dem aktuell
        ''' gecachten Katalog-Eintrag und schreibt nur bei tatsächlicher Abweichung (oder fehlendem
        ''' Eintrag). Verhindert zwei Probleme: unnötige SQLite-Writes bei jedem Bildwechsel auf ein
        ''' bereits bekanntes, unverändertes Bild, und - wichtiger - dass ein Aufrufer versehentlich
        ''' bereits korrekte Has*Metadata-Flags/Zusammenfassungen mit Default-Werten überschreibt, weil
        ''' er SetExifData ohne diese optionalen Parameter aufruft.</summary>
        Public Sub SyncExifData(filePath As String, exif As ExifSearchFields, summary As ExifCatalogSummary)
            If String.IsNullOrWhiteSpace(filePath) OrElse exif Is Nothing OrElse summary Is Nothing Then Return

            Dim currentModifiedAt = ""
            Try
                Dim fi = New FileInfo(filePath)
                If fi.Exists Then currentModifiedAt = fi.LastWriteTime.ToString("o")
            Catch
            End Try

            Dim currentSidecarAt = SidecarStamp(filePath)

            Dim existing = GetMetaForPaths({filePath}).Values.FirstOrDefault()
            If existing IsNot Nothing AndAlso
               String.Equals(existing.ScannedSourceModifiedAt, currentModifiedAt, StringComparison.Ordinal) AndAlso
               String.Equals(existing.ScannedSidecarModifiedAt, currentSidecarAt, StringComparison.Ordinal) AndAlso
               ExifDataMatches(existing, exif, summary) Then
                Return
            End If

            SetExifData(filePath, exif, summary)
        End Sub

        Private Shared Function ExifDataMatches(existing As LibraryImageMeta, exif As ExifSearchFields, summary As ExifCatalogSummary) As Boolean
            Return existing.HasExifMetadata = summary.HasExifMetadata AndAlso
                   existing.HasIptcMetadata = summary.HasIptcMetadata AndAlso
                   existing.HasXmpMetadata = summary.HasXmpMetadata AndAlso
                   existing.HasIccProfile = summary.HasIccProfile AndAlso
                   String.Equals(existing.ExifSummary, If(summary.ExifSummary, ""), StringComparison.Ordinal) AndAlso
                   String.Equals(existing.IptcSummary, If(summary.IptcSummary, ""), StringComparison.Ordinal) AndAlso
                   String.Equals(existing.XmpSummary, If(summary.XmpSummary, ""), StringComparison.Ordinal) AndAlso
                   String.Equals(existing.IccSummary, If(summary.IccSummary, ""), StringComparison.Ordinal) AndAlso
                   String.Equals(existing.SummaryFormat, If(summary.SummaryFormat, ""), StringComparison.Ordinal) AndAlso
                   String.Equals(existing.DateTaken, If(exif.DateTaken, ""), StringComparison.Ordinal) AndAlso
                   String.Equals(existing.DateModifiedExif, If(exif.DateModifiedExif, ""), StringComparison.Ordinal) AndAlso
                   String.Equals(existing.Camera, If(exif.Camera, ""), StringComparison.Ordinal) AndAlso
                   String.Equals(existing.Lens, If(exif.Lens, ""), StringComparison.Ordinal) AndAlso
                   NullableEquals(existing.Aperture, exif.Aperture) AndAlso
                   NullableEquals(existing.FocalLengthMm, exif.FocalLengthMm) AndAlso
                   NullableEquals(existing.Iso, exif.Iso) AndAlso
                   String.Equals(existing.ShutterSpeed, If(exif.ShutterSpeed, ""), StringComparison.Ordinal) AndAlso
                   NullableEquals(existing.GpsLatitude, exif.GpsLatitude) AndAlso
                   NullableEquals(existing.GpsLongitude, exif.GpsLongitude) AndAlso
                   NullableEquals(existing.ImageWidth, exif.ImageWidth) AndAlso
                   NullableEquals(existing.ImageHeight, exif.ImageHeight)
        End Function

        ''' <summary>Zustand der Begleitdateien als eine Zeichenkette: Änderungsdatum der XMP-Beistelldatei
        ''' plus ein Merker, ob daneben schon ein eigenes Rezept (.fpxmp) liegt.
        '''
        ''' Änderungszeit und Vorhandensein der .fpxmp gehören dazu, damit sowohl externe Änderungen
        ''' an Rezept/Katalogblock als auch das LÖSCHEN bemerkt werden. Ohne diesen Teil ändert sich
        ''' weder die Bilddatei noch eine XMP; der Hintergrundscan würde die Datei überspringen.
        '''
        ''' EINE Quelle für Schreiben (SetExifData) und Prüfen (SyncExifData, GalleryViewModel) - zwei
        ''' Fassungen davon liefen garantiert auseinander und der Schnappschuss wäre nie mehr frisch.</summary>
        Public Shared Function SidecarStamp(filePath As String) As String
            Try
                Dim sidecar = XmpSidecarService.FindSidecar(filePath)
                Dim xmpStamp = If(String.IsNullOrEmpty(sidecar), "", File.GetLastWriteTime(sidecar).ToString("o"))
                Dim fpxmpPath = RawSidecarService.SidecarPathFor(filePath)
                Dim fpxmpStamp = If(File.Exists(fpxmpPath), File.GetLastWriteTime(fpxmpPath).ToString("o"), "")
                If String.IsNullOrEmpty(xmpStamp) AndAlso String.IsNullOrEmpty(fpxmpStamp) Then Return ""
                Return xmpStamp & If(String.IsNullOrEmpty(fpxmpStamp), "|-", "|fpxmp:" & fpxmpStamp)
            Catch
                Return ""
            End Try
        End Function

        Private Shared Function NullableEquals(Of T As Structure)(a As T?, b As T?) As Boolean
            If Not a.HasValue AndAlso Not b.HasValue Then Return True
            If a.HasValue <> b.HasValue Then Return False
            Return a.Value.Equals(b.Value)
        End Function

        ''' nächsten Ordner-Scan entscheidet, ob diese EXIF-Daten noch aktuell sind.</summary>
        Public Sub SetExifData(filePath As String, exif As ExifSearchFields, summary As ExifCatalogSummary)
            If String.IsNullOrWhiteSpace(filePath) OrElse exif Is Nothing OrElse summary Is Nothing Then Return

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
                        "INSERT INTO ImageMeta(FilePath,DateTaken,DateModifiedExif,Camera,Lens,Aperture,FocalLengthMm,Iso,ShutterSpeed,GpsLatitude,GpsLongitude,ImageWidth,ImageHeight,FileCreatedAt,HasExifMetadata,HasIptcMetadata,HasXmpMetadata,ScannedSourceModifiedAt,ScannedSidecarModifiedAt,ExifSummary,IptcSummary,XmpSummary,IccSummary,SummaryFormat,HasIccProfile,City,Country,CountryCode) " &
                        "VALUES($p,$dateTaken,$dateModifiedExif,$camera,$lens,$aperture,$focalLength,$iso,$shutterSpeed,$gpsLat,$gpsLon,$width,$height,$fileCreatedAt,$hasExifMetadata,$hasIptcMetadata,$hasXmpMetadata,$scannedSourceModifiedAt,$scannedSidecarModifiedAt,$exifSummary,$iptcSummary,$xmpSummary,$iccSummary,$summaryFormat,$hasIccProfile,$city,$country,$countryCode) " &
                        "ON CONFLICT(FilePath) DO UPDATE SET " &
                        "DateTaken=excluded.DateTaken, DateModifiedExif=excluded.DateModifiedExif, Camera=excluded.Camera, Lens=excluded.Lens, " &
                        "Aperture=excluded.Aperture, FocalLengthMm=excluded.FocalLengthMm, Iso=excluded.Iso, " &
                        "ShutterSpeed=excluded.ShutterSpeed, GpsLatitude=excluded.GpsLatitude, GpsLongitude=excluded.GpsLongitude, " &
                        "ImageWidth=excluded.ImageWidth, ImageHeight=excluded.ImageHeight, " &
                        "FileCreatedAt=excluded.FileCreatedAt, HasExifMetadata=excluded.HasExifMetadata, " &
                        "HasIptcMetadata=excluded.HasIptcMetadata, HasXmpMetadata=excluded.HasXmpMetadata, " &
                        "ScannedSourceModifiedAt=excluded.ScannedSourceModifiedAt, " &
                        "ScannedSidecarModifiedAt=excluded.ScannedSidecarModifiedAt, " &
                        "ExifSummary=excluded.ExifSummary, IptcSummary=excluded.IptcSummary, XmpSummary=excluded.XmpSummary, " &
                        "IccSummary=excluded.IccSummary, SummaryFormat=excluded.SummaryFormat, " &
                        "HasIccProfile=excluded.HasIccProfile, City=excluded.City, Country=excluded.Country, " &
                        "CountryCode=excluded.CountryCode"
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
                    cmd.Parameters.AddWithValue("$city", If(exif.City, ""))
                    cmd.Parameters.AddWithValue("$country", If(exif.Country, ""))
                    cmd.Parameters.AddWithValue("$countryCode", If(exif.CountryCode, ""))
                    cmd.Parameters.AddWithValue("$width", NullableToDbValue(exif.ImageWidth))
                    cmd.Parameters.AddWithValue("$height", NullableToDbValue(exif.ImageHeight))
                    cmd.Parameters.AddWithValue("$fileCreatedAt", fileCreatedAt)
                    cmd.Parameters.AddWithValue("$scannedSidecarModifiedAt", SidecarStamp(filePath))
                    cmd.Parameters.AddWithValue("$hasExifMetadata", If(summary.HasExifMetadata, 1, 0))
                    cmd.Parameters.AddWithValue("$hasIptcMetadata", If(summary.HasIptcMetadata, 1, 0))
                    cmd.Parameters.AddWithValue("$hasXmpMetadata", If(summary.HasXmpMetadata, 1, 0))
                    cmd.Parameters.AddWithValue("$scannedSourceModifiedAt", scannedSourceModifiedAt)
                    cmd.Parameters.AddWithValue("$exifSummary", If(summary.ExifSummary, ""))
                    cmd.Parameters.AddWithValue("$iptcSummary", If(summary.IptcSummary, ""))
                    cmd.Parameters.AddWithValue("$xmpSummary", If(summary.XmpSummary, ""))
                    cmd.Parameters.AddWithValue("$iccSummary", If(summary.IccSummary, ""))
                    cmd.Parameters.AddWithValue("$summaryFormat", If(summary.SummaryFormat, ""))
                    cmd.Parameters.AddWithValue("$hasIccProfile", If(summary.HasIccProfile, 1, 0))
                    cmd.ExecuteNonQuery()
                End Using
            End Using
        End Sub

        ''' ACHTUNG: ReadMetaRow greift über SPALTENNUMMERN zu - neue Spalten gehören ans Ende, sonst
        ''' verschieben sich alle folgenden Indizes stillschweigend auf die falschen Werte.
        Private Const MetaColumnList As String =
            "FilePath, IsFavorite, Rating, Tags, DateTaken, Camera, Lens, Aperture, FocalLengthMm, Iso, ShutterSpeed, GpsLatitude, GpsLongitude, ImageWidth, ImageHeight, DateModifiedExif, FileCreatedAt, HasExifMetadata, HasIptcMetadata, HasXmpMetadata, ScannedSourceModifiedAt, ExifSummary, IptcSummary, XmpSummary, HasIccProfile, IccSummary, SummaryFormat, ColorLabel, ScannedSidecarModifiedAt, City, Country, CountryCode"

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
                .ScannedSourceModifiedAt = If(reader.IsDBNull(20), "", reader.GetString(20)),
                .ExifSummary = If(reader.IsDBNull(21), "", reader.GetString(21)),
                .IptcSummary = If(reader.IsDBNull(22), "", reader.GetString(22)),
                .XmpSummary = If(reader.IsDBNull(23), "", reader.GetString(23)),
                .HasIccProfile = Not reader.IsDBNull(24) AndAlso reader.GetInt32(24) <> 0,
                .IccSummary = If(reader.IsDBNull(25), "", reader.GetString(25)),
                .SummaryFormat = If(reader.IsDBNull(26), "", reader.GetString(26)),
                .ColorLabel = If(reader.IsDBNull(27), "", reader.GetString(27)),
                .ScannedSidecarModifiedAt = If(reader.IsDBNull(28), "", reader.GetString(28)),
                .City = If(reader.IsDBNull(29), "", reader.GetString(29)),
                .Country = If(reader.IsDBNull(30), "", reader.GetString(30)),
                .CountryCode = If(reader.IsDBNull(31), "", reader.GetString(31))
            }
        End Function

        ''' <summary>Lädt alle Metadaten (inkl. EXIF) für alle Dateien im angegebenen Ordner in einem einzigen Query.</summary>
        Public Function GetFolderMeta(folderPath As String) As Dictionary(Of String, LibraryImageMeta)
            Dim result As New Dictionary(Of String, LibraryImageMeta)(PathIdentity.Comparer)
            Dim prefix = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) & Path.DirectorySeparatorChar
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    ' Maskiert wie beim Loeschen: ein Unterstrich im Ordnernamen holte sonst auch die
                    ' Nachbarordner herein - siehe EscapeLikeValue.
                    cmd.CommandText = $"SELECT {MetaColumnList} FROM ImageMeta WHERE FilePath LIKE $prefix" &
                                      LikeEscapeClause
                    cmd.Parameters.AddWithValue("$prefix", EscapeLikeValue(prefix) & "%")
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
            Dim result As New Dictionary(Of String, LibraryImageMeta)(PathIdentity.Comparer)
            Dim list = If(paths, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrWhiteSpace(p)).
                Distinct(PathIdentity.Comparer).
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

        ''' <summary>True fuer den Pseudo-Pfad eines Serverbildes ("nextcloud://…", "immich://…").
        ''' Solche Eintraege haben keine Datei auf der Platte; jede Pruefung mit File.Exists muss sie
        ''' ausnehmen.</summary>
        Public Shared Function IsServerPseudoPath(filePath As String) As Boolean
            Return NextcloudService.IsNextcloudPseudoPath(filePath) OrElse ImmichService.IsImmichPseudoPath(filePath)
        End Function

        ''' <summary>Alle Katalogeintraege, deren Pfad mit diesem Anfang beginnt.
        '''
        ''' Gedacht fuer die Pseudo-Pfade der beiden Server ("nextcloud://", "immich://"): sie sind
        ''' der einzige Weg, ein Serverbild im Katalog zu finden, ohne den ganzen Bestand zu lesen.
        ''' Der Anfang wird als LIKE-Muster benutzt, deshalb werden seine Platzhalter entschaerft -
        ''' ein Unterstrich im Praefix wuerde sonst auf jedes Zeichen passen.</summary>
        Public Function GetImagesWithPathPrefix(pathPrefix As String) As List(Of LibraryImageMeta)
            If String.IsNullOrWhiteSpace(pathPrefix) Then Return New List(Of LibraryImageMeta)()
            Dim likePattern = pathPrefix.Replace("\", "\\").Replace("%", "\%").Replace("_", "\_") & "%"
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = $"SELECT {MetaColumnList} FROM ImageMeta WHERE FilePath LIKE @p ESCAPE '\'"
                    cmd.Parameters.AddWithValue("@p", likePattern)
                    Return ReadImageMeta(cmd)
                End Using
            End Using
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
        ''' <summary>Je Ordner die Zahl der Katalogzeilen - fuer die Aufraeumliste in den
        ''' Einstellungen.
        '''
        ''' Gruppiert wird HIER und nicht in SQL: SQLite hat keine Funktion, die aus einem Pfad das
        ''' Verzeichnis macht, und ein Ausdruck aus instr und substr waere unlesbar und nicht
        ''' schneller. 13000 Zeilen einmal durchzugehen kostet nichts, und der Aufruf laeuft ohnehin
        ''' im Hintergrund.</summary>
        Public Function GetCatalogFolderCounts() As Dictionary(Of String, Integer)
            Dim result As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
            Try
                Using conn = New SqliteConnection(_connectionString)
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT FilePath FROM ImageMeta"
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                Dim ordner = Path.GetDirectoryName(reader.GetString(0))
                                If String.IsNullOrEmpty(ordner) Then Continue While
                                Dim anzahl = 0
                                result.TryGetValue(ordner, anzahl)
                                result(ordner) = anzahl + 1
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Library.GetCatalogFolderCounts", ex)
            End Try
            Return result
        End Function

        ' ORDNERPFADE ALS LIKE-MUSTER.
        ' In SQLite steht "%" fuer beliebig viele Zeichen und "_" fuer genau eines. In einem
        ' Ordnernamen sind beide voellig normal, und unmaskiert wirken sie als Platzhalter: der
        ' Ordner "100_Fotos" traf damit auch "100aFotos". Beim Lesen ist das eine zu grosse
        ' Ergebnismenge, beim Aufraeumen loescht es die Zeilen fremder Ordner mit.
        ' SQLite kennt von sich aus KEIN Fluchtzeichen - es muss dem Befehl mit ESCAPE genannt
        ' werden, sonst bleibt der Rueckstrich ein gewoehnliches Zeichen und nichts ist gewonnen.

        Private Const LikeEscapeClause As String = " ESCAPE '\'"

        ''' <summary>Maskiert die LIKE-Platzhalter in einem Pfad. Der Rueckstrich zuerst: sonst
        ''' verdoppelt der naechste Schritt die gerade gesetzten Fluchtzeichen mit.</summary>
        Private Shared Function EscapeLikeValue(value As String) As String
            Return If(value, "").
                Replace("\", "\\").
                Replace("%", "\%").
                Replace("_", "\_")
        End Function

        ''' <summary>Vergisst alles, was der Katalog ueber einen Ordner weiss: Aufnahmedaten,
        ''' Ortsnamen, gefundene Gesichter und den Vermerk, dass er durchsucht wurde.
        '''
        ''' NICHT die Bilder - die Dateien bleiben unangetastet. Und nicht Bewertung, Favorit,
        ''' Etikett oder Stichworte... doch, auch die: sie stehen in derselben Zeile. Deshalb ist das
        ''' eine ausdrueckliche Handlung mit eigenem Knopf und keine Nebenwirkung von irgendetwas.
        ''' Was in einer Beistelldatei liegt, kommt beim naechsten Einlesen zurueck.
        '''
        ''' Die GESICHTER muessen mit: sie zeigen ueber den Pfad auf Bilder, die der Katalog nicht
        ''' mehr kennt. Bleiben sie stehen, zaehlen Personen weiter Bilder mit, die verschwunden
        ''' sind. Gruppen, die dadurch leer und namenlos zurueckbleiben, verschwinden ebenfalls.</summary>
        ''' <returns>Wie viele Katalogzeilen entfernt wurden.</returns>
        Public Function DeleteFolderCatalogData(folderPath As String) As Integer
            If String.IsNullOrWhiteSpace(folderPath) Then Return 0
            ' Maskiert, sonst raeumt "100_Fotos" auch bei "100aFotos" auf - siehe EscapeLikeValue.
            Dim prefix = EscapeLikeValue(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) &
                                         Path.DirectorySeparatorChar) & "%"
            Dim removed = 0
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using tx = conn.BeginTransaction()
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "DELETE FROM Face WHERE FilePath LIKE $p" & LikeEscapeClause
                        cmd.Parameters.AddWithValue("$p", prefix)
                        cmd.ExecuteNonQuery()
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "DELETE FROM ScannedImage WHERE FilePath LIKE $p" & LikeEscapeClause
                        cmd.Parameters.AddWithValue("$p", prefix)
                        cmd.ExecuteNonQuery()
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "DELETE FROM ImageMeta WHERE FilePath LIKE $p" & LikeEscapeClause
                        cmd.Parameters.AddWithValue("$p", prefix)
                        removed = cmd.ExecuteNonQuery()
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText =
                            "DELETE FROM Person WHERE Name='' AND IsUnknownBin=0 " &
                            "AND NOT EXISTS(SELECT 1 FROM Face WHERE Face.PersonId = Person.Id)"
                        cmd.ExecuteNonQuery()
                    End Using
                    tx.Commit()
                End Using
            End Using
            Return removed
        End Function

        ''' <summary>Denselben Schnitt fuer den GANZEN Katalog. Nicht als Schleife ueber die
        ''' Ordnerliste gebaut, sondern als ein Satz Loeschungen: die Liste zeigt nur, was gerade
        ''' bekannt ist, und ein Ordner, dessen Bilder nicht mehr am Platz liegen, faellt aus ihr
        ''' heraus - seine Zeilen blieben sonst zurueck. "Alles" soll auch alles heissen.
        '''
        ''' Personen fallen hier vollstaendig weg, auch benannte: ohne Gesichter zaehlt jede Gruppe
        ''' null Bilder, und leere Gruppen sollen nirgends auftauchen.</summary>
        ''' <returns>Wie viele Katalogzeilen entfernt wurden.</returns>
        Public Function DeleteAllCatalogData() As Integer
            Dim removed = 0
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using tx = conn.BeginTransaction()
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "DELETE FROM Face; DELETE FROM Person; DELETE FROM ScannedImage;"
                        cmd.ExecuteNonQuery()
                    End Using
                    Using cmd = conn.CreateCommand()
                        cmd.Transaction = tx
                        cmd.CommandText = "DELETE FROM ImageMeta"
                        removed = cmd.ExecuteNonQuery()
                    End Using
                    tx.Commit()
                End Using
            End Using
            Return removed
        End Function

        Public Function PurgeOrphanedRecords() As Integer
            Dim orphans As New List(Of String)()
            Using conn = New SqliteConnection(_connectionString)
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "SELECT FilePath FROM ImageMeta"
                    Using reader = cmd.ExecuteReader()
                        While reader.Read()
                            Dim p = reader.GetString(0)
                            ' Serverbilder stehen unter einem Pseudo-Pfad und liegen auf KEINER
                            ' Platte - File.Exists ist fuer sie immer False. Ohne diese Ausnahme
                            ' raeumt "Verwaiste Eintraege entfernen" jede Bewertung, jedes Stichwort
                            ' und jede Personenzuordnung zu einem Nextcloud- oder Immich-Bild weg.
                            If IsServerPseudoPath(p) Then Continue While
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

                ' Gruppen ohne ein einziges Gesicht. Sie entstehen im Betrieb: beim Verschmelzen
                ' zweier Namen, beim Verschieben eines Gesichts, bei einem erneuten Durchlauf ueber
                ' ein geaendertes Bild. Zurueck bleibt eine Person, die auf nichts mehr zeigt - in
                ' der Personenwand eine Kachel ohne Bild und mit "0 Bilder".
                '
                ' Ein NAME bleibt trotzdem verschont: er ist Handarbeit, und wer gerade alle Bilder
                ' einer benannten Person geloescht hat, will den Namen vielleicht behalten. Dieser
                ' Fall wird in der Wand ausgeblendet, nicht geloescht.
                Using cmd = conn.CreateCommand()
                    cmd.CommandText =
                        "DELETE FROM Person WHERE Name='' AND IsUnknownBin=0 " &
                        "AND NOT EXISTS(SELECT 1 FROM Face WHERE Face.PersonId = Person.Id)"
                    orphans.AddRange(Enumerable.Repeat("", cmd.ExecuteNonQuery()))
                End Using
            End Using
            Return orphans.Count
        End Function

    End Class

End Namespace
