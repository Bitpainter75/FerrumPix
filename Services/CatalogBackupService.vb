Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Reflection
Imports System.Security.Cryptography
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Microsoft.Data.Sqlite

Namespace Services

    ''' <summary>Die Teile einer Sicherung, im Dialog einzeln waehlbar.</summary>
    <Flags>
    Public Enum BackupParts
        None = 0
        ''' Der Bildkatalog: Bewertungen, Etiketten, Stichwoerter, KI-Stichwoerter, Personen, Orte.
        Catalog = 1
        ''' Die Indizes der Immich- und Nextcloud-Anbindung.
        ServerIndexes = 2
        ''' Die Anwendungseinstellungen.
        Settings = 4
        Favorites = 8
        ''' Gespeicherte Suchen.
        Searches = 16
        ''' Die Zugangsdaten der Server in den Einstellungen; ohne diesen Teil fehlen sie in der Datei.
        Secrets = 32
    End Enum

    ''' <summary>Eine Datei in der Sicherung mit Pruefsumme.</summary>
    Public Class BackupFileEntry
        Public Property Name As String = ""
        Public Property Size As Long
        Public Property Sha256 As String = ""
    End Class

    ''' <summary>Das Inhaltsverzeichnis einer Sicherung (manifest.json).</summary>
    Public Class BackupManifest
        Public Property FormatVersion As Integer = 1
        Public Property AppVersion As String = ""
        Public Property CreatedUtc As DateTime
        Public Property MachineName As String = ""
        Public Property Parts As BackupParts
        Public Property Files As New List(Of BackupFileEntry)()
        Public Property ImageCount As Integer
        Public Property PersonCount As Integer
        Public Property KeywordImageCount As Integer
    End Class

    ''' <summary>
    ''' Sichert Katalog, Server-Indizes, Einstellungen, Favoriten und gespeicherte Suchen in eine ZIP
    ''' und holt sie wieder zurueck.
    '''
    ''' SICHERN: Die Datenbanken laufen im WAL-Modus, eine Dateikopie waere kein verlaesslicher Stand.
    ''' Jede wird deshalb mit <c>VACUUM INTO</c> in eine Zwischendatei geschrieben, einen in sich
    ''' stimmigen Schnappschuss, auch waehrend die Anwendung liest und schreibt, und dieser wird mit
    ''' <c>PRAGMA integrity_check</c> geprueft, bevor er in die ZIP geht. Die ZIP entsteht als
    ''' Nachbardatei und bekommt ihren Namen erst fertig. Ohne den Teil "Zugangsdaten" fehlen der
    ''' Immich-Schluessel und das Nextcloud-App-Passwort in der gesicherten Einstellungsdatei; mit ihm
    ''' stehen sie dort so verschluesselt wie auf der Platte (SecretProtectionService, nicht an den
    ''' Rechner gebunden).
    '''
    ''' WIEDERHERSTELLEN in zwei Schritten, und das ist der Kern: die laufende Anwendung haelt
    ''' Verbindungen zu den Datenbanken und einen Stand der Einstellungen im Speicher, den sie beim
    ''' Beenden zurueckschreiben koennte. Deshalb legt <see cref="StageRestore"/> die gewaehlten Teile
    ''' nur geprueft bereit, und <see cref="ApplyPendingRestore"/> spielt sie beim naechsten Start ein,
    ''' als erstes in Main, bevor irgendetwas Katalog oder Einstellungen oeffnet. Vorher sichert
    ''' StageRestore den jetzigen Stand vollstaendig in den Ordner "backups" im Datenordner;
    ''' ein versehentliches Zurueckholen laesst sich so umkehren. Fehlen in der Sicherung die
    ''' Zugangsdaten, behaelt die zurueckgeholte Einstellungsdatei die jetzigen.
    '''
    ''' Nicht in der Sicherung: Vorschaubilder, Modelle, Kartenkacheln, die Zwischenspeicher fuer
    ''' Schaerfe und Aehnlichkeit. Das laesst sich alles neu erzeugen oder laden.
    ''' </summary>
    Public NotInheritable Class CatalogBackupService

        Private Const ManifestName As String = "manifest.json"
        Private Const CurrentFormatVersion As Integer = 1
        Private Shared ReadOnly SecretFieldNames As String() = {"ImmichApiKey", "NextcloudAppPassword"}

        Private Sub New()
        End Sub

        Private Shared ReadOnly Property DataDirectory As String
            Get
                Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FerrumPix")
            End Get
        End Property

        Private Shared ReadOnly Property ConfigDirectory As String
            Get
                Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FerrumPix")
            End Get
        End Property

        ''' <summary>Wo die bereitgelegte Wiederherstellung auf den naechsten Start wartet.</summary>
        Private Shared ReadOnly Property StagingDirectory As String
            Get
                Return Path.Combine(DataDirectory, "restore-pending")
            End Get
        End Property

        ''' <summary>Der Ordner fuer die Sicherung vor dem Wiederherstellen.</summary>
        Public Shared ReadOnly Property SafetyBackupDirectory As String
            Get
                Return Path.Combine(DataDirectory, "backups")
            End Get
        End Property

        ''' <summary>Name in der ZIP, Teil und Ort auf der Platte jeder Datei.</summary>
        Private Shared Function KnownFiles() As List(Of (Name As String, Part As BackupParts, Target As String, IsDatabase As Boolean))
            Return New List(Of (String, BackupParts, String, Boolean)) From {
                ("library.db", BackupParts.Catalog, Path.Combine(DataDirectory, "library.db"), True),
                ("immich-index.db", BackupParts.ServerIndexes, Path.Combine(DataDirectory, "immich-index.db"), True),
                ("nextcloud-index.db", BackupParts.ServerIndexes, Path.Combine(DataDirectory, "nextcloud-index.db"), True),
                ("settings.json", BackupParts.Settings, Path.Combine(ConfigDirectory, "settings.json"), False),
                ("favorites.json", BackupParts.Favorites, Path.Combine(ConfigDirectory, "favorites.json"), False),
                ("searchlists.json", BackupParts.Searches, Path.Combine(ConfigDirectory, "searchlists.json"), False)
            }
        End Function

        ''' <summary>Ein Dateiname mit Datum fuer den Speichern-Dialog.</summary>
        Public Shared Function SuggestedFileName() As String
            Return "FerrumPix-Backup-" & DateTime.Now.ToString("yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture) & ".zip"
        End Function

        ''' <summary>Schreibt die Sicherung. Teile, deren Datei es nicht gibt (etwa ohne Nextcloud),
        ''' fehlen still; das Inhaltsverzeichnis sagt, was drin ist. Wirft bei einem Fehler, und
        ''' dann bleibt keine halbe ZIP liegen.</summary>
        Public Shared Function Create(zipPath As String, parts As BackupParts) As BackupManifest
            If String.IsNullOrWhiteSpace(zipPath) Then Throw New ArgumentException("zipPath")
            Dim work = Path.Combine(Path.GetTempPath(), "ferrumpix-backup-" & Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(work)
            Dim tempZip = Path.Combine(If(Path.GetDirectoryName(zipPath), "."), "." & Path.GetFileName(zipPath) & ".part")
            Try
                Dim manifest As New BackupManifest With {
                    .FormatVersion = CurrentFormatVersion, .AppVersion = AppVersion(),
                    .CreatedUtc = DateTime.UtcNow, .MachineName = Environment.MachineName}
                Dim prepared As New List(Of (Name As String, File As String))()
                For Each known In KnownFiles()
                    If (parts And known.Part) = 0 OrElse Not File.Exists(known.Target) Then Continue For
                    Dim copy = Path.Combine(work, known.Name)
                    If known.IsDatabase Then
                        SnapshotDatabase(known.Target, copy)
                    ElseIf known.Name = "settings.json" Then
                        Dim json = File.ReadAllText(known.Target)
                        If (parts And BackupParts.Secrets) = 0 Then json = WithoutSecrets(json)
                        File.WriteAllText(copy, json)
                    Else
                        File.Copy(known.Target, copy)
                    End If
                    prepared.Add((known.Name, copy))
                    manifest.Parts = manifest.Parts Or known.Part
                Next
                If (parts And BackupParts.Secrets) <> 0 AndAlso (manifest.Parts And BackupParts.Settings) <> 0 Then
                    manifest.Parts = manifest.Parts Or BackupParts.Secrets
                End If
                If prepared.Count = 0 Then Throw New InvalidOperationException("Nichts zu sichern")
                Dim catalog = prepared.FirstOrDefault(Function(p) p.Name = "library.db")
                If catalog.File IsNot Nothing Then FillCounts(catalog.File, manifest)

                Directory.CreateDirectory(If(Path.GetDirectoryName(zipPath), "."))
                If File.Exists(tempZip) Then File.Delete(tempZip)
                Using stream As New FileStream(tempZip, FileMode.CreateNew, FileAccess.Write)
                    Using archive As New ZipArchive(stream, ZipArchiveMode.Create, leaveOpen:=True)
                        For Each entry In prepared
                            manifest.Files.Add(New BackupFileEntry With {
                                .Name = entry.Name, .Size = New FileInfo(entry.File).Length, .Sha256 = HashOf(entry.File)})
                            archive.CreateEntryFromFile(entry.File, entry.Name, CompressionLevel.Optimal)
                        Next
                        Dim manifestEntry = archive.CreateEntry(ManifestName, CompressionLevel.Optimal)
                        Using writer As New StreamWriter(manifestEntry.Open())
                            writer.Write(JsonSerializer.Serialize(manifest, New JsonSerializerOptions With {.WriteIndented = True}))
                        End Using
                    End Using
                    ' Bis auf die Platte, bevor die Datei ihren Namen bekommt.
                    stream.Flush(flushToDisk:=True)
                End Using
                File.Move(tempZip, zipPath, overwrite:=True)
                Return manifest
            Catch
                Try
                    If File.Exists(tempZip) Then File.Delete(tempZip)
                Catch
                End Try
                Throw
            Finally
                Try
                    Directory.Delete(work, True)
                Catch
                End Try
            End Try
        End Function

        ''' <summary>Liest und prueft das Inhaltsverzeichnis. Nothing, wenn die Datei keine
        ''' FerrumPix-Sicherung ist; eine Sicherung aus einer neueren Fassung wird abgelehnt
        ''' (<paramref name="problem"/> sagt warum).</summary>
        Public Shared Function ReadManifest(zipPath As String, ByRef problem As String) As BackupManifest
            problem = ""
            Try
                Using archive = ZipFile.OpenRead(zipPath)
                    Dim entry = archive.GetEntry(ManifestName)
                    If entry Is Nothing Then
                        problem = LocalizationService.T("Die Datei ist keine FerrumPix-Sicherung.")
                        Return Nothing
                    End If
                    Dim manifest As BackupManifest
                    Using reader As New StreamReader(entry.Open())
                        manifest = JsonSerializer.Deserialize(Of BackupManifest)(reader.ReadToEnd())
                    End Using
                    If manifest Is Nothing Then
                        problem = LocalizationService.T("Die Datei ist keine FerrumPix-Sicherung.")
                        Return Nothing
                    End If
                    If manifest.FormatVersion > CurrentFormatVersion Then
                        problem = LocalizationService.T("Die Sicherung stammt aus einer neueren FerrumPix-Fassung und lässt sich hier nicht zurückholen.")
                        Return Nothing
                    End If
                    Return manifest
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Backup.ReadManifest", ex)
                problem = LocalizationService.T("Die Datei ist keine FerrumPix-Sicherung.")
                Return Nothing
            End Try
        End Function

        ''' <summary>Legt die gewaehlten Teile geprueft fuer den naechsten Start bereit und sichert
        ''' vorher den jetzigen Stand. Rueckgabe: leer bei Erfolg, sonst die Meldung.</summary>
        Public Shared Function StageRestore(zipPath As String, parts As BackupParts) As String
            Dim problem = ""
            Dim manifest = ReadManifest(zipPath, problem)
            If manifest Is Nothing Then Return problem
            Dim chosen = manifest.Files.Where(Function(f) (parts And PartOf(f.Name)) <> 0).ToList()
            If chosen.Count = 0 Then Return LocalizationService.T("Nichts ausgewählt, das in der Sicherung steckt.")

            Dim staging = StagingDirectory
            Try
                If Directory.Exists(staging) Then Directory.Delete(staging, True)
                Directory.CreateDirectory(staging)
                Using archive = ZipFile.OpenRead(zipPath)
                    For Each fileEntry In chosen
                        If PartOf(fileEntry.Name) = BackupParts.None Then Continue For
                        Dim entry = archive.GetEntry(fileEntry.Name)
                        If entry Is Nothing Then Throw New InvalidDataException(fileEntry.Name)
                        Dim target = Path.Combine(staging, fileEntry.Name)
                        entry.ExtractToFile(target, overwrite:=True)
                        If Not String.Equals(HashOf(target), fileEntry.Sha256, StringComparison.OrdinalIgnoreCase) Then
                            Throw New InvalidDataException("Pruefsumme stimmt nicht: " & fileEntry.Name)
                        End If
                        If fileEntry.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) AndAlso Not IntegrityOk(target) Then
                            Throw New InvalidDataException("Datenbank beschaedigt: " & fileEntry.Name)
                        End If
                    Next
                End Using
                ' Der Teil "Zugangsdaten" gilt nur, wenn er gewaehlt UND in der Sicherung ist.
                Dim keepSecrets = (parts And BackupParts.Secrets) <> 0 AndAlso (manifest.Parts And BackupParts.Secrets) <> 0
                File.WriteAllText(Path.Combine(staging, "restore.json"),
                                  JsonSerializer.Serialize(New With {.UseBackupSecrets = keepSecrets, .Source = zipPath}))

                ' Den jetzigen Stand vollstaendig sichern, bevor er ersetzt wird.
                Directory.CreateDirectory(SafetyBackupDirectory)
                Create(Path.Combine(SafetyBackupDirectory,
                                    "before-restore-" & DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture) & ".zip"),
                       BackupParts.Catalog Or BackupParts.ServerIndexes Or BackupParts.Settings Or
                       BackupParts.Favorites Or BackupParts.Searches Or BackupParts.Secrets)
                Return ""
            Catch ex As Exception
                DiagnosticLogService.LogException("Backup.StageRestore", ex)
                Try
                    If Directory.Exists(staging) Then Directory.Delete(staging, True)
                Catch
                End Try
                Return LocalizationService.T("Die Sicherung ließ sich nicht prüfen, es wurde nichts verändert.") & " " & ex.Message
            End Try
        End Function

        ''' <summary>Ob eine Wiederherstellung auf den naechsten Start wartet.</summary>
        Public Shared Function HasPendingRestore() As Boolean
            Return Directory.Exists(StagingDirectory)
        End Function

        ''' <summary>Spielt eine bereitgelegte Wiederherstellung ein. Gehoert an den Anfang von Main,
        ''' bevor Katalog oder Einstellungen geoeffnet werden. Jede Datei wird ueber eine
        ''' Nachbardatei ersetzt; bei einer Datenbank gehen die WAL- und SHM-Dateien des alten Stands
        ''' mit weg, sonst spielte SQLite sie in den neuen ein. Schlaegt etwas fehl, bleibt der
        ''' Bereitstellungsordner fuer die Fehlersuche als "restore-failed" stehen.</summary>
        Public Shared Sub ApplyPendingRestore()
            Dim staging = StagingDirectory
            If Not Directory.Exists(staging) Then Return
            Try
                Dim useBackupSecrets = False
                Dim info = Path.Combine(staging, "restore.json")
                If File.Exists(info) Then
                    Dim node = JsonNode.Parse(File.ReadAllText(info))
                    useBackupSecrets = If(node?("UseBackupSecrets")?.GetValue(Of Boolean)(), False)
                End If
                For Each known In KnownFiles()
                    Dim staged = Path.Combine(staging, known.Name)
                    If Not File.Exists(staged) Then Continue For
                    Directory.CreateDirectory(Path.GetDirectoryName(known.Target))
                    If known.Name = "settings.json" Then
                        Dim json = File.ReadAllText(staged)
                        If Not useBackupSecrets Then json = WithSecretsFrom(json, known.Target)
                        File.WriteAllText(staged, json)
                    End If
                    If known.IsDatabase Then
                        For Each suffix In {"-wal", "-shm"}
                            If File.Exists(known.Target & suffix) Then File.Delete(known.Target & suffix)
                        Next
                    End If
                    File.Move(staged, known.Target, overwrite:=True)
                Next
                Directory.Delete(staging, True)
                AppSettingsService.InvalidateCache()
            Catch ex As Exception
                DiagnosticLogService.LogException("Backup.ApplyPendingRestore", ex)
                Try
                    Dim failed = Path.Combine(DataDirectory, "restore-failed")
                    If Directory.Exists(failed) Then Directory.Delete(failed, True)
                    Directory.Move(staging, failed)
                Catch
                End Try
            End Try
        End Sub

        Private Shared Function PartOf(name As String) As BackupParts
            Dim known = KnownFiles().FirstOrDefault(Function(k) k.Name = name)
            Return If(known.Name Is Nothing, BackupParts.None, known.Part)
        End Function

        ''' <summary>Ein stimmiger Schnappschuss der Datenbank, geprueft.</summary>
        Private Shared Sub SnapshotDatabase(source As String, target As String)
            Using conn As New SqliteConnection(New SqliteConnectionStringBuilder With {
                    .DataSource = source, .Mode = SqliteOpenMode.ReadOnly, .Pooling = False}.ToString())
                conn.Open()
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = "VACUUM INTO $t"
                    cmd.Parameters.AddWithValue("$t", target)
                    cmd.ExecuteNonQuery()
                End Using
            End Using
            If Not IntegrityOk(target) Then Throw New InvalidDataException("Schnappschuss beschaedigt: " & Path.GetFileName(source))
        End Sub

        Private Shared Function IntegrityOk(databaseFile As String) As Boolean
            Try
                Using conn As New SqliteConnection(New SqliteConnectionStringBuilder With {
                        .DataSource = databaseFile, .Mode = SqliteOpenMode.ReadOnly, .Pooling = False}.ToString())
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "PRAGMA integrity_check"
                        Return String.Equals(Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase)
                    End Using
                End Using
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Zahlen fuer den Dialog: Bilder im Katalog, benannte Personen, Bilder mit
        ''' Stichwoertern. Fehlt eine Tabelle, bleibt die Zahl 0.</summary>
        Private Shared Sub FillCounts(databaseFile As String, manifest As BackupManifest)
            Try
                Using conn As New SqliteConnection(New SqliteConnectionStringBuilder With {
                        .DataSource = databaseFile, .Mode = SqliteOpenMode.ReadOnly, .Pooling = False}.ToString())
                    conn.Open()
                    manifest.ImageCount = CountOf(conn, "SELECT COUNT(*) FROM ImageMeta")
                    manifest.PersonCount = CountOf(conn, "SELECT COUNT(*) FROM Person WHERE Name<>''")
                    manifest.KeywordImageCount = CountOf(conn, "SELECT COUNT(*) FROM ImageMeta WHERE Tags<>''")
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Backup.Counts", ex)
            End Try
        End Sub

        Private Shared Function CountOf(conn As SqliteConnection, sql As String) As Integer
            Try
                Using cmd = conn.CreateCommand()
                    cmd.CommandText = sql
                    Return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture)
                End Using
            Catch
                Return 0
            End Try
        End Function

        Private Shared Function WithoutSecrets(json As String) As String
            Try
                Dim root = TryCast(JsonNode.Parse(json), JsonObject)
                If root Is Nothing Then Return json
                For Each name In SecretFieldNames
                    root.Remove(name)
                Next
                Return root.ToJsonString(New JsonSerializerOptions With {.WriteIndented = True})
            Catch
                Return json
            End Try
        End Function

        ''' <summary>Die zurueckgeholte Einstellungsdatei mit den Zugangsdaten der jetzigen.</summary>
        Private Shared Function WithSecretsFrom(json As String, currentFile As String) As String
            Try
                Dim root = TryCast(JsonNode.Parse(json), JsonObject)
                If root Is Nothing Then Return json
                Dim current = If(File.Exists(currentFile), TryCast(JsonNode.Parse(File.ReadAllText(currentFile)), JsonObject), New JsonObject())
                If current Is Nothing Then current = New JsonObject()
                ' Ganz ersetzen, nicht nur ergaenzen: wer die Zugangsdaten abgewaehlt hat, bekommt
                ' auch dann keine aus der Sicherung, wenn sie welche enthielt.
                For Each name In SecretFieldNames
                    root.Remove(name)
                    Dim value As JsonNode = Nothing
                    If current.TryGetPropertyValue(name, value) AndAlso value IsNot Nothing Then root(name) = value.DeepClone()
                Next
                Return root.ToJsonString(New JsonSerializerOptions With {.WriteIndented = True})
            Catch
                Return json
            End Try
        End Function

        Private Shared Function HashOf(file As String) As String
            Using stream = IO.File.OpenRead(file)
                Return Convert.ToHexString(SHA256.HashData(stream))
            End Using
        End Function

        Private Shared Function AppVersion() As String
            Return If(Assembly.GetExecutingAssembly().GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()?.InformationalVersion, "")
        End Function

    End Class

End Namespace
