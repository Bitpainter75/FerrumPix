Imports System.IO
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Encodings.Web
Imports System.Text.Json
Imports System.Text.Json.Nodes

Namespace Services

    ''' <summary>Eine Anpassung als Datei: dieselben Angaben wie bisher in den Einstellungen
    ''' (Kennung, Name, Rezept, Feldliste), dazu der Ordner und der Pfad der Datei.</summary>
    Public Class AdjustmentPresetFile
        Inherits AdjustmentPresetSettings

        ''' Der Ordner relativ zum Wurzelordner, mit "/" getrennt; leer ist der Wurzelordner.
        Public Property Folder As String = ""
        ''' Der volle Pfad der Datei.
        Public Property FilePath As String = ""
        ''' Die Kennung, unter der diese Fassung angeliefert wurde, wenn sie beim Import oder beim
        ''' Mitziehen eine neue bekam (dieselbe Kennung meinte am Ziel schon etwas anderes). Daran
        ''' erkennt ein wiederholter Import sie wieder. Leer bei allem anderen.
        Public Property ImportedFrom As String = ""

        ''' <summary>Name mit Ordnerpfad davor, wie ihn die Auswahllisten zeigen:
        ''' "Ordner / Unterordner / Name". Im Wurzelordner nur der Name.</summary>
        Public ReadOnly Property DisplayPath As String
            Get
                If String.IsNullOrEmpty(Folder) Then Return Name
                Return Folder.Replace("/", " / ") & " / " & Name
            End Get
        End Property

        ''' So zeigt eine Auswahlliste die Anpassung.
        Public Overrides Function ToString() As String
            Return DisplayPath
        End Function
    End Class

    ''' <summary>Der gelesene Stand des Ordners der Anpassungen: alle Ordner und alle Anpassungen
    ''' darin. Reachable ist False, wenn der Ordner nicht erreichbar ist; dann ist beides leer.</summary>
    Public Class AdjustmentPresetListing
        Public Property Root As String = ""
        Public Property Reachable As Boolean = True
        ''' Alle Unterordner relativ zum Wurzelordner, mit "/" getrennt, auch die leeren.
        Public ReadOnly Property Folders As New List(Of String)()
        Public ReadOnly Property Presets As New List(Of AdjustmentPresetFile)()
        ''' Dateien mit der Endung der Anpassungen, die sich nicht lesen liessen.
        Public Property SkippedFiles As Integer

        Public Function FindById(id As String) As AdjustmentPresetFile
            Dim wanted = If(id, "").Trim()
            If wanted.Length = 0 Then Return Nothing
            Return Presets.FirstOrDefault(Function(p) String.Equals(p.Id, wanted, StringComparison.Ordinal))
        End Function

        ''' <summary>Die erste Anpassung dieses Namens, der Wurzelordner zuerst. Der Weg fuer
        ''' Verweise von frueher, die nur den Namen kennen.</summary>
        Public Function FindByName(name As String) As AdjustmentPresetFile
            Dim wanted = If(name, "").Trim()
            If wanted.Length = 0 Then Return Nothing
            Return Presets.FirstOrDefault(Function(p) String.Equals(p.Name, wanted, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>Ueber die Kennung, und erst wenn die nichts findet, ueber den Namen.</summary>
        Public Function Find(id As String, name As String) As AdjustmentPresetFile
            Return If(FindById(id), FindByName(name))
        End Function

        Public Function PresetsIn(folder As String) As List(Of AdjustmentPresetFile)
            Dim wanted = AdjustmentPresetStore.NormalizeFolder(folder)
            Return Presets.Where(Function(p) String.Equals(p.Folder, wanted, StringComparison.Ordinal)).ToList()
        End Function
    End Class

    ''' <summary>Was ein Import getan hat.</summary>
    Public Class AdjustmentPresetImportResult
        ''' Neu aufgenommen.
        Public Property Imported As Integer
        ''' Schon vorhanden (gleiche Kennung, gleicher Inhalt) und deshalb nicht noch einmal angelegt.
        Public Property AlreadyPresent As Integer
        ''' Keine lesbare Anpassung (fremde oder kaputte Datei).
        Public Property Skipped As Integer
    End Class

    ''' <summary>Die Anpassungen als Dateien in echten Ordnern.
    '''
    ''' JE ANPASSUNG EINE DATEI mit der Endung <see cref="FileExtension"/>, JSON mit Kennung des
    ''' Formats, Formatversion, Kennung, Name, Feldliste und Rezept. Der Ordnerbaum ist der
    ''' Verzeichnisbaum. Wer Anpassungen weitergibt, kopiert Dateien oder einen ganzen Ordner.
    '''
    ''' DER ANZEIGENAME STEHT IN DER DATEI, der Dateiname ist nur seine fuer jedes Dateisystem
    ''' sichere Fassung (<see cref="SafeFileName"/>). Gefunden wird eine Anpassung ueber ihre
    ''' KENNUNG; sie uebersteht Umbenennen und Verschieben, auch im Dateimanager.
    '''
    ''' WO: immer "Presets" im Datenordner (<see cref="DefaultRoot"/>). Einen eigenen Ort gibt es
    ''' nicht mehr; wer mit dem Datenordner umzieht, nimmt die Anpassungen mit (AppFolders). Ein
    ''' bis 0.9.91 gewaehlter Ordner wird beim Start einmal hierher kopiert
    ''' (FolderLayoutMigration). Ist der Ordner nicht erreichbar, ist der Stand leer und Reachable
    ''' False.
    '''
    ''' FREMDES STOERT NICHT UND WIRD NICHT ANGEFASST: Dateien mit anderer Endung zaehlen nicht,
    ''' eine Datei mit der Endung, die sich nicht lesen laesst, wird uebergangen. Loeschen eines
    ''' Ordners nimmt nur die Anpassungen darin und die danach leeren Verzeichnisse; der Ordner
    ''' kann in einer Ablage liegen, in der auch anderes steht.
    '''
    ''' ALLES HIER GREIFT AUF DIE PLATTE ZU, womoeglich auf einen entfernten Ordner. Die
    ''' Oberflaeche ruft es deshalb im Hintergrund und arbeitet sonst mit dem gemerkten Stand
    ''' (<see cref="Cached"/>).</summary>
    Public NotInheritable Class AdjustmentPresetStore

        Public Const FileExtension As String = ".fpxpreset"
        Public Const FormatName As String = "ferrumpix-adjustment-preset"
        Public Const CurrentFormatVersion As Integer = 1
        ''' Groesser ist keine Anpassung: ein Rezept hat wenige Kilobyte.
        Private Const MaxFileBytes As Long = 4L * 1024 * 1024
        Private Const MaxDepth As Integer = 12

        Private Shared ReadOnly ForbiddenNameChars As Char() = {"<"c, ">"c, ":"c, """"c, "/"c, "\"c, "|"c, "?"c, "*"c}
        Private Shared ReadOnly ReservedNames As String() = {
            "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"}

        Private Shared ReadOnly _cacheLock As New Object()
        Private Shared _cached As AdjustmentPresetListing = Nothing
        Private Shared _version As Integer = 0

        Private ReadOnly _root As String

        Public Sub New(root As String)
            _root = If(root, "").Trim()
        End Sub

        Public ReadOnly Property Root As String
            Get
                Return _root
            End Get
        End Property

        ' ── Wo ──────────────────────────────────────────────────────────────────────────────

        ''' <summary>"Presets" im Datenordner der Anwendung (AppFolders.PresetsFolder).</summary>
        Public Shared ReadOnly Property DefaultRoot As String
            Get
                If Not String.IsNullOrEmpty(RootOverride) Then Return RootOverride
                Return AppFolders.PresetsFolder
            End Get
        End Property

        ''' <summary>Fuer den Pruefstand: ein eigener Ordner statt des Datenordners. Anders als der im
        ''' Datenordner wird er nicht angelegt, wenn er fehlt; so laesst sich ein nicht erreichbarer
        ''' Ordner nachstellen.</summary>
        Friend Shared RootOverride As String = Nothing

        Public Shared Function Current() As AdjustmentPresetStore
            Return New AdjustmentPresetStore(DefaultRoot)
        End Function

        ''' <summary>Ist der Ordner da? Die Vorgabe im Datenordner wird angelegt, wenn sie fehlt;
        ''' ein anderer Ordner (ein Ziel beim Einlesen) nicht.</summary>
        Public Function IsReachable() As Boolean
            If _root.Length = 0 Then Return False
            Try
                If Directory.Exists(_root) Then Return True
                If Not SamePath(_root, AppFolders.PresetsFolder) Then Return False
                Directory.CreateDirectory(_root)
                Return Directory.Exists(_root)
            Catch
                Return False
            End Try
        End Function

        Private Shared Function SamePath(a As String, b As String) As Boolean
            Try
                Return String.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                                     Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), StringComparison.Ordinal)
            Catch
                Return False
            End Try
        End Function

        ' ── Der gemerkte Stand ──────────────────────────────────────────────────────────────

        ''' <summary>Zaehlt mit jeder Aenderung am Bestand hoch. Wer eine Liste zeigt, merkt sich
        ''' die Zahl beim Lesen und liest neu, wenn sie nicht mehr stimmt.</summary>
        Public Shared ReadOnly Property Version As Integer
            Get
                SyncLock _cacheLock
                    Return _version
                End SyncLock
            End Get
        End Property

        ''' <summary>Der zuletzt gelesene Stand des geltenden Ordners, oder Nothing, wenn seit der
        ''' letzten Aenderung keiner gelesen wurde. Ohne Plattenzugriff.</summary>
        Public Shared ReadOnly Property Cached As AdjustmentPresetListing
            Get
                SyncLock _cacheLock
                    Return _cached
                End SyncLock
            End Get
        End Property

        ''' <summary>Vergisst den gemerkten Stand: der Bestand, der Ordner oder die Einstellung
        ''' hat sich geaendert.</summary>
        Public Shared Sub Invalidate()
            SyncLock _cacheLock
                _cached = Nothing
                _version += 1
            End SyncLock
        End Sub

        ''' <summary>Liest den geltenden Ordner und merkt den Stand. Davor wandern Vorlagen, die
        ''' noch in den Einstellungen stehen, als Dateien in den Wurzelordner
        ''' (<see cref="MigrateFromSettings"/>). Plattenzugriff: im Hintergrund rufen.</summary>
        Public Shared Function LoadCurrent() As AdjustmentPresetListing
            Dim store = Current()
            Dim versionBefore = Version
            Try
                MigrateFromSettings(store)
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresetStore.Migrate", ex)
            End Try
            Dim listing = store.ReadAll()
            SyncLock _cacheLock
                ' Hat sich dazwischen etwas geaendert, ist dieser Stand schon alt und wird nicht gemerkt.
                If _version = versionBefore OrElse _cached Is Nothing Then _cached = listing
            End SyncLock
            Return listing
        End Function

        ''' <summary>Der gemerkte Stand, und nur wenn es keinen gibt, ein frisch gelesener.</summary>
        Public Shared Function CachedOrLoad() As AdjustmentPresetListing
            Return If(Cached, LoadCurrent())
        End Function

        ' ── Namen ───────────────────────────────────────────────────────────────────────────

        ''' <summary>Der Dateiname zu einem Anzeigenamen, sicher fuer jedes Dateisystem: Zeichen,
        ''' die Windows, macOS oder Linux nicht erlauben, und Steuerzeichen werden zu "_", Punkte
        ''' und Leerzeichen am Ende fallen weg, ein unter Windows reservierter Name bekommt einen
        ''' Unterstrich davor. Auch unter Linux so streng: der Ordner kann in einer Ablage liegen,
        ''' die ein anderes System mitliest. Ohne Endung.</summary>
        Public Shared Function SafeFileName(name As String) As String
            Dim builder As New StringBuilder()
            For Each c In If(name, "").Trim()
                If Char.IsControl(c) OrElse Array.IndexOf(ForbiddenNameChars, c) >= 0 Then
                    builder.Append("_"c)
                Else
                    builder.Append(c)
                End If
            Next
            Dim result = builder.ToString().TrimEnd("."c, " "c).TrimStart(" "c)
            If result.StartsWith(".", StringComparison.Ordinal) Then result = "_" & result.Substring(1)
            If result.Length > 120 Then result = result.Substring(0, 120).TrimEnd("."c, " "c)
            If result.Length = 0 Then result = "preset"
            Dim stem = result
            Dim dot = stem.IndexOf("."c)
            If dot >= 0 Then stem = stem.Substring(0, dot)
            If ReservedNames.Contains(stem.Trim(), StringComparer.OrdinalIgnoreCase) Then result = "_" & result
            Return result
        End Function

        ''' <summary>Ein Ordnerpfad relativ zum Wurzelordner in seiner einen Schreibweise: mit "/"
        ''' getrennt, ohne leere Teile, ohne "." und "..".
        '''
        ''' DIE TEILE BLEIBEN, WIE SIE SIND. Der Pfad meint einen vorhandenen Ordner, und der kann
        ''' einen Namen tragen, den die Anwendung selbst nie vergaebe ("A:B" oder "Foo." aus dem
        ''' Dateimanager). Wuerde er hier zu seiner sicheren Fassung, traefe jede Aktion den
        ''' Zwilling "A_B" oder "Foo", oder gar nichts. <see cref="SafeFileName"/> gilt nur beim
        ''' ANLEGEN eines neuen Namens (CreateFolder, RenameFolder, Import).</summary>
        Public Shared Function NormalizeFolder(folder As String) As String
            Dim parts = If(folder, "").
                Replace(Path.DirectorySeparatorChar, "/"c).Replace(Path.AltDirectorySeparatorChar, "/"c).Split("/"c).
                Where(Function(p) p.Trim().Length > 0 AndAlso p <> "." AndAlso p <> "..")
            Return String.Join("/", parts)
        End Function

        Public Shared Function ParentFolder(folder As String) As String
            Dim normalized = NormalizeFolder(folder)
            Dim slash = normalized.LastIndexOf("/"c)
            Return If(slash < 0, "", normalized.Substring(0, slash))
        End Function

        Public Shared Function FolderName(folder As String) As String
            Dim normalized = NormalizeFolder(folder)
            Dim slash = normalized.LastIndexOf("/"c)
            Return If(slash < 0, normalized, normalized.Substring(slash + 1))
        End Function

        Public Shared Function CombineFolder(parent As String, name As String) As String
            Return NormalizeFolder(NormalizeFolder(parent) & "/" & If(name, ""))
        End Function

        ''' <summary>Ordnerpfade in der Reihenfolge des Baums: jeder Ordner unmittelbar vor seinen
        ''' Unterordnern, Geschwister nach Namen, ohne Doppel. Eine blosse Sortierung der Pfade
        ''' reicht dafuer nicht: "A B" stuende zwischen "A" und "A/Unter". Ein Ordner, dessen
        ''' Elternordner in der Liste fehlt, steht dort, wo der Elternordner stuende.</summary>
        Public Shared Function OrderFoldersAsTree(folders As IEnumerable(Of String)) As List(Of String)
            Dim known = If(folders, Enumerable.Empty(Of String)()).
                Select(Function(f) NormalizeFolder(f)).Where(Function(f) f.Length > 0).
                Distinct(StringComparer.Ordinal).ToList()
            ' Geschwister nach Namen: verglichen wird Teil fuer Teil, der Pfad eines Ordners ist
            ' dabei immer kleiner als der seiner Unterordner.
            known.Sort(Function(a, b)
                           Dim partsA = a.Split("/"c)
                           Dim partsB = b.Split("/"c)
                           For i = 0 To Math.Min(partsA.Length, partsB.Length) - 1
                               Dim order = StringComparer.OrdinalIgnoreCase.Compare(partsA(i), partsB(i))
                               If order = 0 Then order = StringComparer.Ordinal.Compare(partsA(i), partsB(i))
                               If order <> 0 Then Return order
                           Next
                           Return partsA.Length.CompareTo(partsB.Length)
                       End Function)
            Return known
        End Function

        ''' <summary>Der volle Pfad zu einem Ordner relativ zum Wurzelordner.</summary>
        Public Function DirectoryOf(folder As String) As String
            Dim normalized = NormalizeFolder(folder)
            If normalized.Length = 0 Then Return _root
            Return Path.Combine(_root, normalized.Replace("/"c, Path.DirectorySeparatorChar))
        End Function

        Private Shared Function UniquePath(directory As String, stem As String, extension As String) As String
            Dim candidate = Path.Combine(directory, stem & extension)
            Dim counter = 2
            While File.Exists(candidate) OrElse IO.Directory.Exists(candidate)
                candidate = Path.Combine(directory, stem & " (" & counter.ToString(Globalization.CultureInfo.InvariantCulture) & ")" & extension)
                counter += 1
            End While
            Return candidate
        End Function

        ''' <summary>Ein Name, der in <paramref name="taken"/> noch nicht vorkommt: der gewuenschte,
        ''' sonst mit " (2)", " (3)" dahinter.</summary>
        Private Shared Function UniqueName(wanted As String, taken As IEnumerable(Of String)) As String
            Dim used As New HashSet(Of String)(taken, StringComparer.OrdinalIgnoreCase)
            If Not used.Contains(wanted) Then Return wanted
            Dim counter = 2
            While used.Contains(wanted & " (" & counter.ToString(Globalization.CultureInfo.InvariantCulture) & ")")
                counter += 1
            End While
            Return wanted & " (" & counter.ToString(Globalization.CultureInfo.InvariantCulture) & ")"
        End Function

        ' ── Das Dateiformat ─────────────────────────────────────────────────────────────────

        ''' <summary>Der Inhalt der Datei zu einer Anpassung. Das Rezept steht als JSON-Objekt
        ''' darin, nicht als Text im Text: die Datei soll sich lesen und vergleichen lassen.</summary>
        Public Shared Function Serialize(preset As AdjustmentPresetSettings) As String
            Dim root As New JsonObject()
            root("format") = JsonValue.Create(FormatName)
            root("version") = JsonValue.Create(CurrentFormatVersion)
            root("id") = JsonValue.Create(If(preset.Id, ""))
            root("name") = JsonValue.Create(If(preset.Name, "").Trim())
            If preset.Fields IsNot Nothing Then
                Dim fields As New JsonArray()
                For Each field In preset.Fields
                    fields.Add(JsonValue.Create(field))
                Next
                root("fields") = fields
            End If
            root("recipe") = JsonNode.Parse(preset.RecipeJson)
            ' Nur wo es eine gibt. Ein zusaetzliches Feld aendert die Formatversion nicht: Parse
            ' uebergeht, was es nicht kennt, auch in aelteren Fassungen der Anwendung.
            Dim origin = If(TryCast(preset, AdjustmentPresetFile)?.ImportedFrom, "").Trim()
            If origin.Length > 0 Then root("importedFrom") = JsonValue.Create(origin)
            Return root.ToJsonString(New JsonSerializerOptions With {
                .WriteIndented = True,
                .Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping})
        End Function

        ''' <summary>Liest den Inhalt einer Datei. Nothing bei allem, was keine Anpassung dieses
        ''' Formats ist: kein JSON, anderes Format, neuere Formatversion, kein Name, kein lesbares
        ''' Rezept. Fehlt die Kennung, entsteht eine aus Name und Rezept, die bei jedem Lesen
        ''' dieselbe ist.</summary>
        Public Shared Function Parse(text As String) As AdjustmentPresetFile
            If String.IsNullOrWhiteSpace(text) Then Return Nothing
            Try
                Dim root = TryCast(JsonNode.Parse(text), JsonObject)
                If root Is Nothing Then Return Nothing
                If Not String.Equals(StringOf(root("format")), FormatName, StringComparison.Ordinal) Then Return Nothing
                Dim versionNode = TryCast(root("version"), JsonValue)
                Dim version As Integer = 0
                If versionNode Is Nothing OrElse Not versionNode.TryGetValue(version) Then Return Nothing
                If version < 1 OrElse version > CurrentFormatVersion Then Return Nothing
                Dim name = If(StringOf(root("name")), "").Trim()
                If name.Length = 0 Then Return Nothing
                Dim recipeNode = TryCast(root("recipe"), JsonObject)
                If recipeNode Is Nothing Then Return Nothing
                Dim recipeJson = recipeNode.ToJsonString()
                If FpxService.DeserializeAdjustments(recipeJson) Is Nothing Then Return Nothing

                Dim fields As List(Of String) = Nothing
                Dim fieldsNode = root("fields")
                If fieldsNode IsNot Nothing Then
                    Dim fieldArray = TryCast(fieldsNode, JsonArray)
                    If fieldArray Is Nothing Then Return Nothing
                    fields = AppSettingsService.NormalizeAdjustmentPresetFields(
                        fieldArray.Select(Function(n) StringOf(n)).ToList())
                End If

                Dim id = If(StringOf(root("id")), "").Trim()
                If id.Length = 0 Then id = HashOf(name & vbLf & recipeJson)
                Return New AdjustmentPresetFile With {.Id = id, .Name = name, .RecipeJson = recipeJson, .Fields = fields,
                                                      .ImportedFrom = If(StringOf(root("importedFrom")), "").Trim()}
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function StringOf(node As JsonNode) As String
            Dim value = TryCast(node, JsonValue)
            Dim text As String = Nothing
            If value IsNot Nothing AndAlso value.TryGetValue(text) Then Return text
            Return Nothing
        End Function

        Private Shared Function HashOf(text As String) As String
            Return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text))).Substring(0, 32).ToLowerInvariant()
        End Function

        Private Shared Function ReadFile(filePath As String) As AdjustmentPresetFile
            Try
                Dim info As New FileInfo(filePath)
                If Not info.Exists OrElse info.Length > MaxFileBytes Then Return Nothing
                Dim preset = Parse(File.ReadAllText(filePath, Encoding.UTF8))
                If preset IsNot Nothing Then preset.FilePath = filePath
                Return preset
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Schreibt erst daneben und setzt dann um: eine halb geschriebene Datei gibt es
        ''' unter dem endgueltigen Namen nie. Danach wird zurueckgelesen; nur was sich wieder lesen
        ''' laesst, gilt als geschrieben.</summary>
        Private Shared Function WriteFile(filePath As String, preset As AdjustmentPresetSettings) As Boolean
            Dim temp = filePath & ".tmp-" & Guid.NewGuid().ToString("N").Substring(0, 8)
            Try
                File.WriteAllText(temp, Serialize(preset), New UTF8Encoding(False))
                File.Move(temp, filePath, True)
                Dim back = ReadFile(filePath)
                Return back IsNot Nothing AndAlso String.Equals(back.Id, preset.Id, StringComparison.Ordinal)
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresetStore.Write", ex)
                Try
                    If File.Exists(temp) Then File.Delete(temp)
                Catch
                End Try
                Return False
            End Try
        End Function

        Private Shared Function IsPresetFileName(filePath As String) As Boolean
            Return String.Equals(Path.GetExtension(filePath), FileExtension, StringComparison.OrdinalIgnoreCase)
        End Function

        ' ── Lesen ───────────────────────────────────────────────────────────────────────────

        ''' <summary>Liest den ganzen Ordner mit allen Unterordnern. Versteckte Ordner und
        ''' Verknuepfungen auf Ordner bleiben aussen vor. Tragen zwei Dateien dieselbe Kennung
        ''' (im Dateimanager kopiert), behaelt die erste sie; die zweite bekommt fuer diesen Stand
        ''' eine aus Kennung und Pfad, damit sich beide waehlen lassen.</summary>
        Public Function ReadAll() As AdjustmentPresetListing
            Dim listing As New AdjustmentPresetListing With {.Root = _root, .Reachable = IsReachable()}
            If Not listing.Reachable Then Return listing
            Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
            Walk(_root, "", 0, listing, seen)
            Return listing
        End Function

        Private Sub Walk(directory As String, folder As String, depth As Integer,
                         listing As AdjustmentPresetListing, seen As HashSet(Of String))
            Dim files As String()
            Dim directories As String()
            Try
                files = IO.Directory.GetFiles(directory)
                directories = IO.Directory.GetDirectories(directory)
            Catch
                Return
            End Try
            Dim found As New List(Of AdjustmentPresetFile)()
            For Each filePath In files.OrderBy(Function(f) Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                If Not IsPresetFileName(filePath) Then Continue For
                Dim preset = ReadFile(filePath)
                If preset Is Nothing Then
                    listing.SkippedFiles += 1
                    Continue For
                End If
                preset.Folder = folder
                If Not seen.Add(preset.Id) Then
                    preset.Id = HashOf(preset.Id & vbLf & folder & "/" & Path.GetFileName(filePath))
                    seen.Add(preset.Id)
                End If
                found.Add(preset)
            Next
            listing.Presets.AddRange(found.OrderBy(Function(p) p.Name, StringComparer.OrdinalIgnoreCase))
            If depth >= MaxDepth Then Return
            For Each child In directories.OrderBy(Function(d) Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                Dim name = Path.GetFileName(child)
                If name.StartsWith(".", StringComparison.Ordinal) Then Continue For
                Try
                    If (File.GetAttributes(child) And FileAttributes.ReparsePoint) <> 0 Then Continue For
                Catch
                    Continue For
                End Try
                Dim childFolder = If(folder.Length = 0, name, folder & "/" & name)
                listing.Folders.Add(childFolder)
                Walk(child, childFolder, depth + 1, listing, seen)
            Next
        End Sub

        ''' <summary>Die Anpassungen unmittelbar in einem Verzeichnis, ohne Unterordner.</summary>
        Private Function ReadDirectory(folder As String) As List(Of AdjustmentPresetFile)
            Dim result As New List(Of AdjustmentPresetFile)()
            Dim directory = DirectoryOf(folder)
            Try
                If Not IO.Directory.Exists(directory) Then Return result
                For Each filePath In IO.Directory.GetFiles(directory).OrderBy(Function(f) Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    If Not IsPresetFileName(filePath) Then Continue For
                    Dim preset = ReadFile(filePath)
                    If preset Is Nothing Then Continue For
                    preset.Folder = NormalizeFolder(folder)
                    result.Add(preset)
                Next
            Catch
            End Try
            Return result
        End Function

        ' ── Aendern ─────────────────────────────────────────────────────────────────────────

        ''' <summary>Legt eine Anpassung im Ordner an oder ERSETZT die gleichnamige dort; die
        ''' ersetzte behaelt Datei und Kennung, Verweise auf sie gelten also weiter. In zwei
        ''' Ordnern darf derselbe Name stehen. Nothing, wenn der Ordner nicht erreichbar ist oder
        ''' sich die Datei nicht schreiben liess.</summary>
        ''' <param name="id">Die Kennung einer Anpassung, die es schon gibt (Import, Uebernahme);
        ''' ohne sie entsteht eine neue.</param>
        ''' <param name="importedFrom">Die Kennung, unter der eine Fassung angeliefert wurde, wenn
        ''' sie hier eine neue bekommt (<see cref="AdjustmentPresetFile.ImportedFrom"/>). Ersetzt
        ''' das Speichern eine vorhandene, behaelt die ihre.</param>
        Public Function Save(folder As String, name As String, recipeJson As String,
                             fields As List(Of String), Optional id As String = Nothing,
                             Optional importedFrom As String = Nothing) As AdjustmentPresetFile
            Dim cleanName = If(name, "").Trim()
            Dim recipe = If(recipeJson, "").Trim()
            If cleanName.Length = 0 OrElse recipe.Length = 0 OrElse Not IsReachable() Then Return Nothing
            Dim target = NormalizeFolder(folder)
            Try
                Dim directory = DirectoryOf(target)
                IO.Directory.CreateDirectory(directory)
                Dim existing = ReadDirectory(target).
                    FirstOrDefault(Function(p) String.Equals(p.Name, cleanName, StringComparison.OrdinalIgnoreCase))
                Dim preset As New AdjustmentPresetFile With {
                    .Name = cleanName,
                    .RecipeJson = recipe,
                    .Fields = AppSettingsService.NormalizeAdjustmentPresetFields(fields),
                    .Folder = target}
                If existing IsNot Nothing Then
                    preset.Id = existing.Id
                    preset.FilePath = existing.FilePath
                    preset.ImportedFrom = existing.ImportedFrom
                Else
                    If Not String.IsNullOrWhiteSpace(id) Then preset.Id = id.Trim()
                    preset.ImportedFrom = If(importedFrom, "").Trim()
                    preset.FilePath = UniquePath(directory, SafeFileName(cleanName), FileExtension)
                End If
                If Not WriteFile(preset.FilePath, preset) Then Return Nothing
                Invalidate()
                Return preset
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresetStore.Save", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Gibt einer Anpassung einen neuen Namen; die Datei heisst danach nach ihm, die
        ''' Kennung bleibt. False, wenn im selben Ordner schon eine andere so heisst.</summary>
        Public Function RenamePreset(preset As AdjustmentPresetFile, newName As String) As Boolean
            Dim cleanName = If(newName, "").Trim()
            If preset Is Nothing OrElse cleanName.Length = 0 OrElse Not File.Exists(preset.FilePath) Then Return False
            Try
                If ReadDirectory(preset.Folder).Any(
                    Function(p) Not String.Equals(p.FilePath, preset.FilePath, StringComparison.Ordinal) AndAlso
                                String.Equals(p.Name, cleanName, StringComparison.OrdinalIgnoreCase)) Then Return False
                Dim directory = Path.GetDirectoryName(preset.FilePath)
                Dim wantedPath = Path.Combine(directory, SafeFileName(cleanName) & FileExtension)
                ' NUR DIE SCHREIBWEISE AENDERT SICH ("abend" zu "Abend"). Wo das Dateisystem Gross
                ' und Klein nicht unterscheidet (Windows, macOS), meldet es unter dem neuen Namen die
                ' eigene Datei; die zaehlt nicht als belegt. Belegt ist der Name nur, wenn dort eine
                ' Datei mit genau dieser Schreibweise steht, also eine andere.
                Dim caseOnly = Not String.Equals(wantedPath, preset.FilePath, StringComparison.Ordinal) AndAlso
                               String.Equals(wantedPath, preset.FilePath, StringComparison.OrdinalIgnoreCase) AndAlso
                               Not IO.Directory.EnumerateFiles(directory).Any(Function(f) String.Equals(f, wantedPath, StringComparison.Ordinal))
                Dim newPath = If(String.Equals(wantedPath, preset.FilePath, StringComparison.Ordinal) OrElse caseOnly,
                                 wantedPath, UniquePath(directory, SafeFileName(cleanName), FileExtension))
                Dim renamed As New AdjustmentPresetFile With {
                    .Id = preset.Id, .Name = cleanName, .RecipeJson = preset.RecipeJson, .Fields = preset.Fields,
                    .ImportedFrom = preset.ImportedFrom}
                If caseOnly Then
                    ' Ueber einen Zwischennamen: sonst schriebe WriteFile ueber die eigene Datei, und
                    ' das Loeschen des alten Namens naehme danach die neue mit.
                    Dim parked = Path.Combine(directory, "." & Guid.NewGuid().ToString("N").Substring(0, 12) & ".rename")
                    File.Move(preset.FilePath, parked)
                    If Not WriteFile(newPath, renamed) Then
                        File.Move(parked, preset.FilePath)
                        Return False
                    End If
                    File.Delete(parked)
                Else
                    If Not WriteFile(newPath, renamed) Then Return False
                    If Not String.Equals(newPath, preset.FilePath, StringComparison.Ordinal) Then File.Delete(preset.FilePath)
                End If
                preset.Name = cleanName
                preset.FilePath = newPath
                Invalidate()
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresetStore.RenamePreset", ex)
                Return False
            End Try
        End Function

        ''' <summary>Verschiebt eine Anpassung in einen anderen Ordner; die Kennung bleibt. False,
        ''' wenn dort schon eine gleichen Namens liegt.</summary>
        Public Function MovePreset(preset As AdjustmentPresetFile, targetFolder As String) As Boolean
            If preset Is Nothing OrElse Not File.Exists(preset.FilePath) Then Return False
            Dim target = NormalizeFolder(targetFolder)
            If String.Equals(target, NormalizeFolder(preset.Folder), StringComparison.Ordinal) Then Return True
            Try
                If ReadDirectory(target).Any(Function(p) String.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase)) Then Return False
                Dim directory = DirectoryOf(target)
                IO.Directory.CreateDirectory(directory)
                Dim newPath = UniquePath(directory, Path.GetFileNameWithoutExtension(preset.FilePath), FileExtension)
                File.Move(preset.FilePath, newPath)
                preset.FilePath = newPath
                preset.Folder = target
                Invalidate()
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresetStore.MovePreset", ex)
                Return False
            End Try
        End Function

        Public Function DeletePreset(preset As AdjustmentPresetFile) As Boolean
            If preset Is Nothing OrElse String.IsNullOrEmpty(preset.FilePath) Then Return False
            Try
                If Not IsPresetFileName(preset.FilePath) OrElse Not File.Exists(preset.FilePath) Then Return False
                File.Delete(preset.FilePath)
                Invalidate()
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresetStore.DeletePreset", ex)
                Return False
            End Try
        End Function

        ''' <summary>Legt einen Unterordner an und gibt seinen Pfad relativ zum Wurzelordner
        ''' zurueck. Nothing, wenn es ihn schon gibt oder er sich nicht anlegen laesst.</summary>
        Public Function CreateFolder(parent As String, name As String) As String
            Dim cleanName = SafeFileName(name)
            If String.IsNullOrWhiteSpace(name) OrElse Not IsReachable() Then Return Nothing
            Dim folder = CombineFolder(parent, cleanName)
            Try
                Dim directory = DirectoryOf(folder)
                If IO.Directory.Exists(directory) OrElse File.Exists(directory) Then Return Nothing
                IO.Directory.CreateDirectory(directory)
                Invalidate()
                Return folder
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresetStore.CreateFolder", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Benennt einen Ordner um und gibt seinen neuen Pfad zurueck, Nothing bei
        ''' Misserfolg. Die Anpassungen darin behalten ihre Kennungen.</summary>
        Public Function RenameFolder(folder As String, newName As String) As String
            If String.IsNullOrWhiteSpace(newName) Then Return Nothing
            Return MoveDirectory(folder, CombineFolder(ParentFolder(folder), SafeFileName(newName)))
        End Function

        ''' <summary>Verschiebt einen Ordner samt Inhalt unter einen anderen. Nicht in sich selbst
        ''' oder einen seiner Unterordner.</summary>
        Public Function MoveFolder(folder As String, targetParent As String) As String
            Dim source = NormalizeFolder(folder)
            Dim parent = NormalizeFolder(targetParent)
            If source.Length = 0 Then Return Nothing
            If String.Equals(parent, source, StringComparison.Ordinal) OrElse
               parent.StartsWith(source & "/", StringComparison.Ordinal) Then Return Nothing
            Return MoveDirectory(source, CombineFolder(parent, FolderName(source)))
        End Function

        Private Function MoveDirectory(folder As String, target As String) As String
            Dim source = NormalizeFolder(folder)
            If source.Length = 0 OrElse target.Length = 0 Then Return Nothing
            If String.Equals(source, target, StringComparison.Ordinal) Then Return target
            Try
                Dim origin = DirectoryOf(source)
                Dim destination = DirectoryOf(target)
                If Not IO.Directory.Exists(origin) OrElse IO.Directory.Exists(destination) OrElse File.Exists(destination) Then Return Nothing
                IO.Directory.CreateDirectory(Path.GetDirectoryName(destination))
                IO.Directory.Move(origin, destination)
                Invalidate()
                Return target
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresetStore.MoveDirectory", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Loescht die Anpassungen in einem Ordner und seinen Unterordnern und danach
        ''' jedes Verzeichnis, das leer geworden ist. FREMDE DATEIEN BLEIBEN, und mit ihnen ihr
        ''' Verzeichnis: der Ordner kann in einer Ablage liegen, in der auch anderes steht.</summary>
        ''' <returns>Wie viele Anpassungen geloescht wurden; -1, wenn es den Ordner nicht gibt
        ''' oder er der Wurzelordner ist.</returns>
        Public Function DeleteFolder(folder As String) As Integer
            Dim target = NormalizeFolder(folder)
            If target.Length = 0 Then Return -1
            Dim directory = DirectoryOf(target)
            If Not IO.Directory.Exists(directory) Then Return -1
            Dim deleted = 0
            Try
                deleted = DeleteTree(directory, 0)
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresetStore.DeleteFolder", ex)
            End Try
            Invalidate()
            Return deleted
        End Function

        Private Shared Function DeleteTree(directory As String, depth As Integer) As Integer
            Dim deleted = 0
            If depth <= MaxDepth Then
                For Each child In IO.Directory.GetDirectories(directory)
                    If (File.GetAttributes(child) And FileAttributes.ReparsePoint) <> 0 Then Continue For
                    deleted += DeleteTree(child, depth + 1)
                Next
            End If
            For Each filePath In IO.Directory.GetFiles(directory)
                ' Nur was sich als Anpassung lesen laesst: eine Datei mit der Endung, die keine ist,
                ' gehoert nicht zum Bestand und bleibt liegen wie jede fremde.
                If Not IsPresetFileName(filePath) OrElse ReadFile(filePath) Is Nothing Then Continue For
                File.Delete(filePath)
                deleted += 1
            Next
            If Not IO.Directory.EnumerateFileSystemEntries(directory).Any() Then IO.Directory.Delete(directory)
            Return deleted
        End Function

        ' ── Importieren ─────────────────────────────────────────────────────────────────────
        ' Die Verwaltung bietet keinen Import an; gebraucht wird er beim Zurueckholen einer
        ' Sicherung (CatalogBackupService).

        ''' <summary>Nimmt eine Datei oder einen Ordner samt Unterordnern auf. Ein Ordner kommt
        ''' als Unterordner gleichen Namens unter <paramref name="targetFolder"/> an. Was es mit
        ''' gleicher Kennung und gleichem Inhalt schon gibt, wird nicht noch einmal angelegt; eine
        ''' Kennung, die schon etwas anderes meint, wird durch eine neue ersetzt; ein Name, der im
        ''' Zielordner vergeben ist, bekommt eine Zahl dahinter. Fremde und kaputte Dateien werden
        ''' gezaehlt und uebergangen.</summary>
        Public Function Import(sourcePath As String, targetFolder As String) As AdjustmentPresetImportResult
            Dim result As New AdjustmentPresetImportResult()
            If String.IsNullOrWhiteSpace(sourcePath) OrElse Not IsReachable() Then Return result
            Dim known = ReadAll().Presets.ToList()
            Try
                If IO.Directory.Exists(sourcePath) Then
                    ' Nicht den eigenen Bestand in sich selbst einlesen.
                    Dim fullSource = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
                    Dim fullRoot = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
                    If fullRoot.StartsWith(fullSource, StringComparison.Ordinal) OrElse fullSource.StartsWith(fullRoot, StringComparison.Ordinal) Then Return result
                    Dim name = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                    ImportDirectory(sourcePath, CombineFolder(targetFolder, SafeFileName(name)), 0, known, result)
                ElseIf File.Exists(sourcePath) Then
                    ImportFile(sourcePath, NormalizeFolder(targetFolder), known, result)
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("AdjustmentPresetStore.Import", ex)
            End Try
            Return result
        End Function

        Private Sub ImportDirectory(directory As String, folder As String, depth As Integer,
                                    known As List(Of AdjustmentPresetFile), result As AdjustmentPresetImportResult)
            IO.Directory.CreateDirectory(DirectoryOf(folder))
            Invalidate()
            For Each filePath In IO.Directory.GetFiles(directory).OrderBy(Function(f) f, StringComparer.OrdinalIgnoreCase)
                If Not IsPresetFileName(filePath) Then Continue For
                ImportFile(filePath, folder, known, result)
            Next
            If depth >= MaxDepth Then Return
            For Each child In IO.Directory.GetDirectories(directory).OrderBy(Function(d) d, StringComparer.OrdinalIgnoreCase)
                Dim name = Path.GetFileName(child)
                If name.StartsWith(".", StringComparison.Ordinal) Then Continue For
                If (File.GetAttributes(child) And FileAttributes.ReparsePoint) <> 0 Then Continue For
                ' Ein NEUER Ordner im Bestand: er bekommt die sichere Fassung des Namens.
                ImportDirectory(child, CombineFolder(folder, SafeFileName(name)), depth + 1, known, result)
            Next
        End Sub

        Private Sub ImportFile(filePath As String, folder As String,
                               known As List(Of AdjustmentPresetFile), result As AdjustmentPresetImportResult)
            Dim incoming = ReadFile(filePath)
            If incoming Is Nothing Then
                result.Skipped += 1
                Return
            End If
            Dim id = incoming.Id
            ' Schon einmal unter neuer Kennung uebernommen (dieselbe Kennung meinte hier damals
            ' etwas anderes, ImportedFrom): dieselbe Fassung wird wiedererkannt und nicht noch
            ' einmal angelegt. Sonst legte jedes Zurueckholen derselben Sicherung eine weitere Kopie
            ' daneben, sobald die Vorlage hier geaendert wurde.
            If known.Any(Function(p) String.Equals(p.ImportedFrom, id, StringComparison.Ordinal) AndAlso SameSettings(p, incoming)) Then
                result.AlreadyPresent += 1
                Return
            End If
            Dim importedFrom As String = Nothing
            Dim sameId = known.FirstOrDefault(Function(p) String.Equals(p.Id, id, StringComparison.Ordinal))
            If sameId IsNot Nothing Then
                ' Ohne den Namen: die vorhandene kann umbenannt sein, von Hand oder weil ihr Name
                ' beim ersten Import im Zielordner vergeben war. Sie ist trotzdem dieselbe.
                If SameSettings(sameId, incoming) Then
                    result.AlreadyPresent += 1
                    Return
                End If
                importedFrom = id
                id = Guid.NewGuid().ToString("N")
            End If
            Dim taken = known.Where(Function(p) String.Equals(p.Folder, folder, StringComparison.Ordinal)).Select(Function(p) p.Name)
            Dim saved = Save(folder, UniqueName(incoming.Name, taken), incoming.RecipeJson, incoming.Fields, id, importedFrom)
            If saved Is Nothing Then
                result.Skipped += 1
                Return
            End If
            known.Add(saved)
            result.Imported += 1
        End Sub

        ''' <summary>Tragen zwei Anpassungen dasselbe: Name, Feldliste und Rezept?</summary>
        Public Shared Function SameContent(a As AdjustmentPresetSettings, b As AdjustmentPresetSettings) As Boolean
            If a Is Nothing OrElse b Is Nothing Then Return False
            If Not String.Equals(a.Name, b.Name, StringComparison.Ordinal) Then Return False
            Return SameSettings(a, b)
        End Function

        ''' <summary>Stellen zwei Anpassungen dasselbe ein: Feldliste und Rezept, ohne den Namen?</summary>
        Public Shared Function SameSettings(a As AdjustmentPresetSettings, b As AdjustmentPresetSettings) As Boolean
            If a Is Nothing OrElse b Is Nothing Then Return False
            If (a.Fields Is Nothing) <> (b.Fields Is Nothing) Then Return False
            If a.Fields IsNot Nothing AndAlso Not a.Fields.SequenceEqual(b.Fields, StringComparer.Ordinal) Then Return False
            Return SameRecipe(a.RecipeJson, b.RecipeJson)
        End Function

        Private Shared Function SameRecipe(a As String, b As String) As Boolean
            Try
                Return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b))
            Catch
                Return False
            End Try
        End Function

        ' ── Ordnerwechsel und Uebernahme ────────────────────────────────────────────────────

        ''' <summary>Liegt einer der beiden Ordner im anderen (oder sind es dieselben, auch ueber
        ''' eine Verknuepfung)? Dann sieht jeder Bestand Dateien des anderen.</summary>
        Public Shared Function AreNested(a As String, b As String) As Boolean
            If String.IsNullOrWhiteSpace(a) OrElse String.IsNullOrWhiteSpace(b) Then Return False
            Dim realA = FileOperationPolicy.ResolveLinks(a)
            Dim realB = FileOperationPolicy.ResolveLinks(b)
            Return PathIdentity.IsAncestorOrSelf(realA, realB) OrElse PathIdentity.IsAncestorOrSelf(realB, realA)
        End Function

        ''' <summary>Zieht alle Anpassungen samt Ordnern in einen anderen Bestand um. Eine Datei
        ''' verlaesst den alten Ordner erst, wenn sie im neuen belegt geschrieben ist. Die
        ''' Anwendung selbst ruft das nicht mehr, seit es keinen eigenen Ort der Anpassungen gibt;
        ''' der Pruefstand haelt die Regel fest.
        '''
        ''' VERSCHACHTELTE ORDNER WERDEN ABGELEHNT, in beide Richtungen (Refused): liegt der neue
        ''' im alten oder der alte im neuen, sieht der eine Bestand die Dateien des anderen mit
        ''' derselben Kennung, und "schon angekommen" hiesse, die einzige Fassung zu loeschen.
        ''' Bewegt wird dann nichts.
        '''
        ''' DIE KENNUNG ALLEIN BELEGT NICHT, DASS EINE FASSUNG ANGEKOMMEN IST. Die Quelle geht nur,
        ''' wenn im Ziel eine ANDERE Datei mit derselben Kennung und derselben Einstellung liegt
        ''' (SameSettings, wie beim Import). Meint die Kennung dort etwas anderes, kommt die Quelle
        ''' unter freiem Namen und neuer Kennung dazu (KeptBeside), mit der alten als Herkunft;
        ''' beide Fassungen bleiben.</summary>
        ''' <returns>Wie viele im neuen Bestand angekommen sind, wie viele nicht, wie viele davon
        ''' neben einer anderen Fassung gleicher Kennung stehen, und ob abgelehnt wurde.</returns>
        Public Function MoveAllTo(target As AdjustmentPresetStore) As (Moved As Integer, Failed As Integer, KeptBeside As Integer, Refused As Boolean)
            Dim moved = 0
            Dim failed = 0
            Dim keptBeside = 0
            If target Is Nothing OrElse Not IsReachable() OrElse Not target.IsReachable() Then Return (0, 0, 0, False)
            If SamePath(_root, target.Root) Then Return (0, 0, 0, False)
            If AreNested(_root, target.Root) Then Return (0, 0, 0, True)
            Dim source = ReadAll()
            Dim there = target.ReadAll().Presets.ToList()
            For Each folder In source.Folders
                Try
                    IO.Directory.CreateDirectory(target.DirectoryOf(folder))
                Catch
                End Try
            Next
            For Each preset In source.Presets
                Dim sameId = there.FirstOrDefault(Function(p) String.Equals(p.Id, preset.Id, StringComparison.Ordinal))
                Dim arrived = sameId IsNot Nothing AndAlso SameSettings(sameId, preset) AndAlso
                              Not PathIdentity.AreSame(FileOperationPolicy.ResolveLinks(sameId.FilePath), FileOperationPolicy.ResolveLinks(preset.FilePath))
                If Not arrived Then
                    Dim taken = there.Where(Function(p) String.Equals(p.Folder, preset.Folder, StringComparison.Ordinal)).Select(Function(p) p.Name)
                    ' Dieselbe Kennung meint im Ziel etwas anderes: neue Kennung, alte als Herkunft.
                    Dim id = If(sameId Is Nothing, preset.Id, Guid.NewGuid().ToString("N"))
                    Dim origin = If(sameId Is Nothing, preset.ImportedFrom, preset.Id)
                    Dim saved = target.Save(preset.Folder, UniqueName(preset.Name, taken), preset.RecipeJson, preset.Fields, id, origin)
                    If saved IsNot Nothing AndAlso Not PathIdentity.AreSame(saved.FilePath, preset.FilePath) Then
                        there.Add(saved)
                        arrived = True
                        If sameId IsNot Nothing Then keptBeside += 1
                    End If
                End If
                If arrived Then
                    DeletePreset(preset)
                    moved += 1
                Else
                    failed += 1
                End If
            Next
            ' Die leer gewordenen Ordner des alten Bestands, die tiefsten zuerst.
            For Each folder In source.Folders.OrderByDescending(Function(f) f.Length)
                Try
                    Dim directory = DirectoryOf(folder)
                    If IO.Directory.Exists(directory) AndAlso Not IO.Directory.EnumerateFileSystemEntries(directory).Any() Then IO.Directory.Delete(directory)
                Catch
                End Try
            Next
            Invalidate()
            Return (moved, failed, keptBeside, False)
        End Function

        ''' <summary>Die einmalige Uebernahme: jede Vorlage, die noch in den Einstellungen steht,
        ''' wird mit ihrer Kennung als Datei in den Wurzelordner geschrieben.
        '''
        ''' DIE EINSTELLUNGEN BEHALTEN SIE, BIS DIE DATEI BELEGT IST: erst nach dem Schreiben wird
        ''' der Ordner neu gelesen, und nur was dort mit seiner Kennung steht, verlaesst die
        ''' Einstellungen. Ist der Ordner nicht erreichbar, geschieht nichts.
        '''
        ''' OHNE DOPPEL BEI WIEDERHOLUNG: entschieden wird ueber die Kennung. Bricht der Lauf nach
        ''' dem Schreiben und vor dem Bereinigen der Einstellungen ab, findet der naechste die
        ''' Datei vor und schreibt sie nicht noch einmal, auch wenn sie inzwischen umbenannt oder
        ''' verschoben wurde.</summary>
        ''' <returns>Wie viele Vorlagen die Einstellungen verlassen haben.</returns>
        Public Shared Function MigrateFromSettings(store As AdjustmentPresetStore) As Integer
            Dim pending = If(AppSettingsService.Load().AdjustmentPresets, New List(Of AdjustmentPresetSettings)()).
                Where(Function(p) p IsNot Nothing).ToList()
            If pending.Count = 0 OrElse store Is Nothing OrElse Not store.IsReachable() Then Return 0
            Dim present = store.ReadAll().Presets.ToList()
            For Each preset In pending
                If present.Any(Function(p) String.Equals(p.Id, preset.Id, StringComparison.Ordinal)) Then Continue For
                Dim taken = present.Where(Function(p) p.Folder.Length = 0).Select(Function(p) p.Name)
                Dim saved = store.Save("", UniqueName(If(preset.Name, "").Trim(), taken), preset.RecipeJson, preset.Fields, preset.Id)
                If saved IsNot Nothing Then present.Add(saved)
            Next
            Dim proof = store.ReadAll()
            Dim proven = pending.Where(Function(p) proof.FindById(p.Id) IsNot Nothing).Select(Function(p) p.Id).ToList()
            If proven.Count > 0 Then AppSettingsService.RemoveMigratedAdjustmentPresets(proven)
            Return proven.Count
        End Function

    End Class

End Namespace
