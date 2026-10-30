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
        ''' <summary>Die Pfadanfaenge des Katalogs mit ihrer Bildzahl, fuer das Umbiegen beim
        ''' Zurueckholen auf einem anderen Rechner.</summary>
        Public Property PathRoots As New List(Of BackupPathRoot)()
    End Class

    ''' <summary>Ein Pfadanfang im gesicherten Katalog: ein Benutzerordner, ein Laufwerk.</summary>
    Public Class BackupPathRoot
        Public Property Root As String = ""
        Public Property Count As Integer
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
                If catalog.File IsNot Nothing Then
                    FillCounts(catalog.File, manifest)
                    manifest.PathRoots = ComputePathRoots(catalog.File)
                End If

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
        ''' <param name="remaps">Alter Pfadanfang zu neuem, fuer einen anderen Rechner. Umgeschrieben
        ''' werden die bereitgelegten Dateien, nach der Pruefung ihrer Pruefsummen: der Katalog und
        ''' jeder Pfad in Einstellungen, Favoriten und gespeicherten Suchen.</param>
        Public Shared Function StageRestore(zipPath As String, parts As BackupParts,
                                            Optional remaps As IDictionary(Of String, String) = Nothing) As String
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
                ApplyRemaps(staging, remaps)
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
            ' Zuerst ein abgebrochenes Einspielen erkennen und zurueckbauen, siehe ReplaceAllOrNothing.
            RecoverInterruptedRestore()
            Dim staging = StagingDirectory
            If Not Directory.Exists(staging) Then Return
            Try
                Dim useBackupSecrets = False
                Dim info = Path.Combine(staging, "restore.json")
                If File.Exists(info) Then
                    Dim node = JsonNode.Parse(File.ReadAllText(info))
                    useBackupSecrets = If(node?("UseBackupSecrets")?.GetValue(Of Boolean)(), False)
                End If
                ' Erst alles vorbereiten, was die jetzigen Dateien noch lesen muss.
                Dim settingsStaged = Path.Combine(staging, "settings.json")
                If File.Exists(settingsStaged) AndAlso Not useBackupSecrets Then
                    Dim settingsTarget = KnownFiles().First(Function(k) k.Name = "settings.json").Target
                    File.WriteAllText(settingsStaged, WithSecretsFrom(File.ReadAllText(settingsStaged), settingsTarget))
                End If
                ReplaceAllOrNothing(staging)
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

        ''' <summary>Ersetzt die Dateien ALLE ODER KEINE. Die jetzige Datei wandert zuerst samt ihrer
        ''' WAL- und SHM-Datei in einen Rueckweg-Ordner, statt geloescht zu werden: so bleibt ein
        ''' noch nicht eingecheckter Stand erhalten, und SQLite spielt keine fremde WAL in die neue
        ''' Datenbank. Erst dann kommt die neue an ihren Platz. Scheitert irgendein Schritt, wird in
        ''' umgekehrter Reihenfolge zurueckgebaut: neue Dateien weg, alte zurueck. Der Rueckweg-Ordner
        ''' liegt neben dem Datenordner auf demselben Datentraeger, ein Verschieben ist also ein
        ''' Umbenennen. Wirft nach dem Zurueckbauen weiter.
        '''
        ''' <para>AUCH UEBER EINEN ABSTURZ HINWEG: jeder Schritt steht VOR seiner Ausfuehrung im
        ''' Journal (restore-rollback/journal.txt, je Zeile auf die Platte gezwungen), am Ende die
        ''' Zeile DONE. Bricht der Prozess mittendrin ab, findet der naechste Start das Journal ohne
        ''' DONE und baut nach ihm zurueck (<see cref="RecoverInterruptedRestore"/>).</para></summary>
        Private Shared Sub ReplaceAllOrNothing(staging As String)
            Dim rollback = RollbackDirectory
            If Directory.Exists(rollback) Then Directory.Delete(rollback, True)
            Directory.CreateDirectory(rollback)
            Dim journal = Path.Combine(rollback, JournalName)
            Try
                For Each known In KnownFiles()
                    Dim staged = Path.Combine(staging, known.Name)
                    If Not File.Exists(staged) Then Continue For
                    Directory.CreateDirectory(Path.GetDirectoryName(known.Target))
                    Dim companions = If(known.IsDatabase, {"", "-wal", "-shm"}, {""})
                    For Each suffix In companions
                        Dim original = known.Target & suffix
                        If Not File.Exists(original) Then Continue For
                        Dim parked = Path.Combine(rollback, known.Name & suffix)
                        AppendJournal(journal, "P" & ControlChars.Tab & original & ControlChars.Tab & parked)
                        File.Move(original, parked)
                    Next
                    AppendJournal(journal, "N" & ControlChars.Tab & known.Target)
                    File.Move(staged, known.Target)
                Next
                AppendJournal(journal, "DONE")
            Catch
                RollBackFromJournal(journal)
                ' Das Journal ist abgearbeitet und darf beim naechsten Start nicht noch einmal
                ' laufen: sein N-Schritt loeschte dann die eben zurueckgeholte Datei.
                FinishRollback(rollback, journal)
                Throw
            End Try
            ' Alles steht: der Rueckweg wird nicht mehr gebraucht. Die vollstaendige Sicherung des
            ' alten Stands liegt ohnehin unter backups/.
            Try
                Directory.Delete(rollback, True)
            Catch ex As Exception
                DiagnosticLogService.LogException("Backup.Rollback.Cleanup", ex)
            End Try
        End Sub

        Private Const JournalName As String = "journal.txt"

        Private Shared ReadOnly Property RollbackDirectory As String
            Get
                Return Path.Combine(DataDirectory, "restore-rollback")
            End Get
        End Property

        ''' <summary>Eine Zeile ans Journal, bis auf die Platte, bevor der Schritt geschieht.</summary>
        Private Shared Sub AppendJournal(journal As String, line As String)
            Using stream As New FileStream(journal, FileMode.Append, FileAccess.Write, FileShare.Read)
                Dim bytes = Text.Encoding.UTF8.GetBytes(line & ControlChars.Lf)
                stream.Write(bytes, 0, bytes.Length)
                stream.Flush(flushToDisk:=True)
            End Using
        End Sub

        ''' <summary>Baut nach dem Journal zurueck, in umgekehrter Reihenfolge: eine neu gesetzte
        ''' Datei weg, eine geparkte zurueck an ihren Ort. Ein Schritt, der im Journal steht, aber
        ''' nicht mehr geschah, findet seine Datei nicht und wird uebergangen. Schritte vor dem
        ''' Parken stehen nicht im Journal, ein N-Schritt loescht also nur, was an Stelle einer
        ''' geparkten oder nicht vorhandenen Datei gesetzt wurde.</summary>
        Private Shared Sub RollBackFromJournal(journal As String)
            If Not File.Exists(journal) Then Return
            Dim lines = File.ReadAllLines(journal)
            For index = lines.Length - 1 To 0 Step -1
                Dim fields = lines(index).Split(ControlChars.Tab)
                Try
                    If fields(0) = "N" AndAlso fields.Length >= 2 Then
                        If File.Exists(fields(1)) Then File.Delete(fields(1))
                    ElseIf fields(0) = "P" AndAlso fields.Length >= 3 Then
                        If File.Exists(fields(2)) Then File.Move(fields(2), fields(1), overwrite:=True)
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("Backup.Rollback", ex)
                End Try
            Next
        End Sub

        ''' <summary>Nach dem Zurueckbauen: ist alles an seinem Platz, faellt der Rueckweg-Ordner weg.
        ''' Liegt noch eine geparkte Datei darin, die nicht zurueck konnte, bleibt er stehen, mit
        ''' ROLLED_BACK im Journal; der naechste Start benennt ihn dann um, statt ihn zu loeschen.</summary>
        Private Shared Sub FinishRollback(rollback As String, journal As String)
            Try
                AppendJournal(journal, RolledBackMark)
                Dim leftovers = Directory.EnumerateFiles(rollback).Any(Function(f) Not String.Equals(Path.GetFileName(f), JournalName, StringComparison.Ordinal))
                If leftovers Then
                    DiagnosticLogService.LogAlways("Backup.Rollback", "geparkte Dateien bleiben in " & rollback)
                Else
                    Directory.Delete(rollback, True)
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Backup.Rollback.Finish", ex)
            End Try
        End Sub

        Private Const RolledBackMark As String = "ROLLED_BACK"

        ''' <summary>Beim Start, vor allem anderen: liegt ein Journal ohne DONE, ist ein Einspielen
        ''' mitten im Ersetzen abgebrochen. Dann wird zurueckgebaut, und das Bereitgelegte wandert
        ''' nach restore-failed: es ist zum Teil schon verschoben und taugt nicht mehr fuer einen
        ''' zweiten Versuch; die Sicherung selbst gibt es ja noch. Mit DONE stand schon alles, nur
        ''' das Aufraeumen fehlte.</summary>
        Private Shared Sub RecoverInterruptedRestore()
            Dim rollback = RollbackDirectory
            If Not Directory.Exists(rollback) Then Return
            Try
                Dim journal = Path.Combine(rollback, JournalName)
                Dim lines = If(File.Exists(journal), File.ReadAllLines(journal), Array.Empty(Of String)())
                If lines.Contains(RolledBackMark) Then
                    ' Schon zurueckgebaut, nur nicht vollstaendig: die Reste bleiben zum Nachsehen.
                    Directory.Move(rollback, rollback & "-" & DateTime.Now.ToString("yyyyMMdd-HHmmss", Globalization.CultureInfo.InvariantCulture))
                    Return
                End If
                Dim finished = lines.Contains("DONE")
                If finished Then
                    If Directory.Exists(StagingDirectory) Then Directory.Delete(StagingDirectory, True)
                Else
                    RollBackFromJournal(journal)
                    If Directory.Exists(StagingDirectory) Then
                        Dim failed = Path.Combine(DataDirectory, "restore-failed")
                        If Directory.Exists(failed) Then Directory.Delete(failed, True)
                        Directory.Move(StagingDirectory, failed)
                    End If
                    DiagnosticLogService.LogAlways("Backup.Recover", "abgebrochenes Einspielen zurueckgebaut")
                End If
                Directory.Delete(rollback, True)
            Catch ex As Exception
                DiagnosticLogService.LogException("Backup.Recover", ex)
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

        ' ── Pfadanfaenge ──────────────────────────────────────────────────────────

        ''' <summary>Der Anfang, auf den es fuer einen Pfad ankommt: ein Einhaengeort
        ''' (/run/media/BENUTZER/NAME, /media/..., /mnt/NAME, /Volumes/NAME), ein Benutzerordner
        ''' (/home/NAME, /Users/NAME, C:\Users\NAME), ein Laufwerk (D:\), eine Freigabe
        ''' (\\server\name), sonst die erste Ebene. Arbeitet nur am Text und gilt deshalb auch fuer
        ''' Pfade eines anderen Systems.</summary>
        Public Shared Function PathRootOf(path As String) As String
            If String.IsNullOrWhiteSpace(path) OrElse path.Contains("://") Then Return ""
            If path.StartsWith("\\", StringComparison.Ordinal) Then
                Dim uncParts = path.Substring(2).Split("\"c)
                Return If(uncParts.Length >= 2, "\\" & uncParts(0) & "\" & uncParts(1), "")
            End If
            If path.Length >= 3 AndAlso path(1) = ":"c AndAlso (path(2) = "\"c OrElse path(2) = "/"c) Then
                Dim winParts = path.Split({"\"c, "/"c}, StringSplitOptions.RemoveEmptyEntries)
                If winParts.Length >= 3 AndAlso String.Equals(winParts(1), "Users", StringComparison.OrdinalIgnoreCase) Then
                    Return winParts(0) & "\" & winParts(1) & "\" & winParts(2)
                End If
                Return path.Substring(0, 3)
            End If
            Dim parts = path.Split("/"c, StringSplitOptions.RemoveEmptyEntries)
            If parts.Length < 2 Then Return ""
            If parts(0) = "run" AndAlso parts.Length >= 5 AndAlso parts(1) = "media" Then Return "/run/media/" & parts(2) & "/" & parts(3)
            If parts(0) = "media" AndAlso parts.Length >= 4 Then Return "/media/" & parts(1) & "/" & parts(2)
            If (parts(0) = "media" OrElse parts(0) = "mnt" OrElse parts(0) = "Volumes" OrElse parts(0) = "home" OrElse parts(0) = "Users") AndAlso parts.Length >= 3 Then
                Return "/" & parts(0) & "/" & parts(1)
            End If
            Return "/" & parts(0)
        End Function

        ''' <summary>Der Vorschlag fuer einen neuen Anfang: ein fremder Benutzerordner wird zum
        ''' eigenen, alles andere bleibt, wie es war.</summary>
        Public Shared Function SuggestedTargetFor(root As String) As String
            Dim home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            Dim isHome = root.StartsWith("/home/", StringComparison.Ordinal) OrElse root.StartsWith("/Users/", StringComparison.Ordinal) OrElse
                         (root.Length > 3 AndAlso root(1) = ":"c AndAlso root.IndexOf("\Users\", StringComparison.OrdinalIgnoreCase) = 2)
            If isHome AndAlso Not String.IsNullOrEmpty(home) Then Return home
            Return root
        End Function

        Private Shared Function ComputePathRoots(databaseFile As String) As List(Of BackupPathRoot)
            Dim counts As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
            Try
                Using conn As New SqliteConnection(New SqliteConnectionStringBuilder With {
                        .DataSource = databaseFile, .Mode = SqliteOpenMode.ReadOnly, .Pooling = False}.ToString())
                    conn.Open()
                    Using cmd = conn.CreateCommand()
                        cmd.CommandText = "SELECT FilePath FROM ImageMeta UNION SELECT FilePath FROM Face UNION SELECT FilePath FROM ScannedImage"
                        Using reader = cmd.ExecuteReader()
                            While reader.Read()
                                Dim root = PathRootOf(reader.GetString(0))
                                If root.Length = 0 Then Continue While
                                Dim n As Integer
                                counts.TryGetValue(root, n)
                                counts(root) = n + 1
                            End While
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Backup.PathRoots", ex)
            End Try
            Return counts.OrderByDescending(Function(c) c.Value).
                Select(Function(c) New BackupPathRoot With {.Root = c.Key, .Count = c.Value}).ToList()
        End Function

        ''' <summary>Ein Pfad mit neuem Anfang, oder Nothing, wenn er nicht darunter liegt. Wechselt
        ''' dabei die Art der Trennzeichen (Windows nach Linux oder umgekehrt), folgt der Rest.</summary>
        Friend Shared Function RemapPath(path As String, oldRoot As String, newRoot As String) As String
            If String.IsNullOrEmpty(path) OrElse String.IsNullOrEmpty(oldRoot) Then Return Nothing
            If path = oldRoot Then Return newRoot
            Dim trimmed = oldRoot.TrimEnd("/"c, "\"c)
            If path.Length <= trimmed.Length OrElse Not path.StartsWith(trimmed, StringComparison.Ordinal) Then Return Nothing
            Dim sep = path(trimmed.Length)
            If sep <> "/"c AndAlso sep <> "\"c Then Return Nothing
            Dim rest = path.Substring(trimmed.Length)
            Dim newSep = If(newRoot.Contains("\"c) AndAlso Not newRoot.Contains("/"c), "\"c, "/"c)
            If newSep <> sep Then rest = rest.Replace(sep, newSep)
            Return newRoot.TrimEnd("/"c, "\"c) & rest
        End Function

        Private Shared Function RemapAny(path As String, remaps As List(Of KeyValuePair(Of String, String))) As String
            For Each remap In remaps
                Dim mapped = RemapPath(path, remap.Key, remap.Value)
                If mapped IsNot Nothing Then Return mapped
            Next
            Return Nothing
        End Function

        ''' <summary>Schreibt die Pfade der bereitgelegten Dateien um. Der laengste alte Anfang zuerst,
        ''' damit /mnt/nas/fotos vor /mnt/nas greift. Stoesst ein umgeschriebener Pfad im Katalog auf
        ''' einen schon vorhandenen, bleibt der alte stehen (UPDATE OR IGNORE).</summary>
        Private Shared Sub ApplyRemaps(staging As String, remaps As IDictionary(Of String, String))
            If remaps Is Nothing Then Return
            Dim active = remaps.Where(Function(r) Not String.IsNullOrWhiteSpace(r.Key) AndAlso Not String.IsNullOrWhiteSpace(r.Value) AndAlso
                                                  r.Key <> r.Value).
                OrderByDescending(Function(r) r.Key.Length).ToList()
            If active.Count = 0 Then Return

            Dim catalog = Path.Combine(staging, "library.db")
            If File.Exists(catalog) Then
                Using conn As New SqliteConnection(New SqliteConnectionStringBuilder With {.DataSource = catalog, .Pooling = False}.ToString())
                    conn.Open()
                    Using transaction = conn.BeginTransaction()
                        For Each table In {"ImageMeta", "Face", "ScannedImage", "AiImageTag", "AiTagScan"}
                            Dim pathsInTable As New List(Of String)()
                            Using cmd = conn.CreateCommand()
                                cmd.Transaction = transaction
                                cmd.CommandText = "SELECT DISTINCT FilePath FROM " & table
                                Try
                                    Using reader = cmd.ExecuteReader()
                                        While reader.Read()
                                            pathsInTable.Add(reader.GetString(0))
                                        End While
                                    End Using
                                Catch
                                    Continue For
                                End Try
                            End Using
                            Using update = conn.CreateCommand()
                                update.Transaction = transaction
                                update.CommandText = "UPDATE OR IGNORE " & table & " SET FilePath=$n WHERE FilePath=$o"
                                Dim o = update.Parameters.Add("$o", SqliteType.Text)
                                Dim n = update.Parameters.Add("$n", SqliteType.Text)
                                For Each oldPath In pathsInTable
                                    Dim mapped = RemapAny(oldPath, active)
                                    If mapped Is Nothing Then Continue For
                                    o.Value = oldPath
                                    n.Value = mapped
                                    update.ExecuteNonQuery()
                                Next
                            End Using
                        Next
                        transaction.Commit()
                    End Using
                End Using
            End If

            For Each name In {"settings.json", "favorites.json", "searchlists.json"}
                Dim file = Path.Combine(staging, name)
                If Not IO.File.Exists(file) Then Continue For
                Try
                    Dim root = JsonNode.Parse(IO.File.ReadAllText(file))
                    Dim changed = RemapJson(root, active)
                    If changed Then IO.File.WriteAllText(file, root.ToJsonString(New JsonSerializerOptions With {.WriteIndented = True}))
                Catch ex As Exception
                    DiagnosticLogService.LogException("Backup.RemapJson", ex)
                End Try
            Next
        End Sub

        ''' <summary>Jeder Textwert, der unter einem alten Anfang liegt, bekommt den neuen.</summary>
        Private Shared Function RemapJson(node As JsonNode, remaps As List(Of KeyValuePair(Of String, String))) As Boolean
            Dim changed = False
            Dim obj = TryCast(node, JsonObject)
            If obj IsNot Nothing Then
                For Each key In obj.Select(Function(p) p.Key).ToList()
                    Dim child = obj(key)
                    Dim value = TryCast(child, JsonValue)
                    Dim text As String = Nothing
                    If value IsNot Nothing AndAlso value.TryGetValue(Of String)(text) Then
                        Dim mapped = RemapAny(text, remaps)
                        If mapped IsNot Nothing Then
                            obj(key) = mapped
                            changed = True
                        End If
                    ElseIf child IsNot Nothing Then
                        changed = RemapJson(child, remaps) OrElse changed
                    End If
                Next
            End If
            Dim arr = TryCast(node, JsonArray)
            If arr IsNot Nothing Then
                For i = 0 To arr.Count - 1
                    Dim value = TryCast(arr(i), JsonValue)
                    Dim text As String = Nothing
                    If value IsNot Nothing AndAlso value.TryGetValue(Of String)(text) Then
                        Dim mapped = RemapAny(text, remaps)
                        If mapped IsNot Nothing Then
                            arr(i) = mapped
                            changed = True
                        End If
                    ElseIf arr(i) IsNot Nothing Then
                        changed = RemapJson(arr(i), remaps) OrElse changed
                    End If
                Next
            End If
            Return changed
        End Function

        Private Shared Function AppVersion() As String
            Return If(Assembly.GetExecutingAssembly().GetCustomAttribute(Of AssemblyInformationalVersionAttribute)()?.InformationalVersion, "")
        End Function

    End Class

End Namespace
