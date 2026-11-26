Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions

Namespace Services

    ''' <summary>Was der einmalige Umzug beim ersten Start von 0.9.92 geschafft hat.</summary>
    Public NotInheritable Class FolderLayoutMigrationResult
        ''' <summary>Der Datenordner liess sich anlegen und beschreiben.</summary>
        Public Property TargetWritable As Boolean
        Public Property ModelsDone As Boolean
        Public Property ExtensionsDone As Boolean
        Public Property FavoritesDone As Boolean
        Public Property SearchListsDone As Boolean
        Public Property GpuStateDone As Boolean
        ''' <summary>Die Anpassungen aus dem eigenen Ordner bis 0.9.91 sind kopiert, oder es gab
        ''' keinen.</summary>
        Public Property PresetsDone As Boolean

        Public ReadOnly Property Complete As Boolean
            Get
                Return TargetWritable AndAlso ModelsDone AndAlso ExtensionsDone AndAlso FavoritesDone AndAlso
                       SearchListsDone AndAlso GpuStateDone AndAlso PresetsDone
            End Get
        End Property
    End Class

    ''' <summary>Der einmalige Umzug beim ersten Start von 0.9.92: was bis 0.9.91 im Ordner der
    ''' Einstellungen lag, kommt in den Datenordner (packaging/Plaene/ABLAGEORTE.md).
    '''
    ''' WAS: die Modelle (aus "Modelle" wird "Models"), die Erweiterungen (dazu der alte Ordner
    ''' "Erweiterungen"), Favoriten, Suchlisten und die Sperren der Grafikkarte. Die Anpassungen
    ''' eines bis 0.9.91 gewaehlten eigenen Ordners werden in "presets" KOPIERT: der Ordner gehoert
    ''' dem Nutzer, und die Anwendung loescht dort nichts. Eine leere library.db (0 Byte), die bei
    ''' den Einstellungen liegengeblieben ist, faellt weg; eine mit Inhalt nie.
    '''
    ''' WIE: jeder Teil fuer sich und Datei fuer Datei. Eine Datei zieht erst unter dem Namen
    ''' "&lt;ziel&gt;.moving" um und bekommt danach ihren Namen; auf derselben Platte ist beides ein
    ''' Umbenennen. Bricht der Prozess dazwischen ab, liegt die Quelle noch da (dann wird die
    ''' halbe Kopie verworfen) oder nur noch die ".moving"-Datei (dann bekommt sie ihren Namen).
    ''' Jeder Schritt ist wiederholbar; was nicht geklappt hat, versucht der naechste Start, und
    ''' bis dahin gilt fuer diesen Teil der alte Ort (AppFolders).
    '''
    ''' GIBT ES AM ZIEL SCHON ETWAS, bleibt es: eine gleiche Datei am alten Ort faellt weg, eine
    ''' andere kommt als "&lt;name&gt;.old&lt;endung&gt;" daneben. Bei Modellen entscheidet die
    ''' Pruefsumme, die AiModelService kennt.
    '''
    ''' MODELLE: verschoben wird nur, was diese Fassung kennt (aktuelle Datei und die noch
    ''' benutzbaren Vorgaenger). Eine alte Fassung einer bekannten Familie (Name
    ''' "&lt;familie&gt;-v&lt;n&gt;.&lt;endung&gt;") nutzt keine Funktion mehr und wird geloescht, mit
    ''' Name und Groesse im Protokoll. Alles Unbekannte bleibt am alten Ort und steht in der
    ''' MOVED.txt, die dort auch den neuen Ort nennt; eine aeltere Fassung der Anwendung laedt ihre
    ''' Modelle dann neu.</summary>
    Public NotInheritable Class FolderLayoutMigration
        Private Sub New()
        End Sub

        ''' <summary>Die Aufteilung, die dieser Umzug herstellt (AppSettings.FolderLayoutVersion).</summary>
        Public Const LayoutVersion As Integer = 1

        Friend Const MovedNoteFileName As String = "MOVED.txt"
        Private Const MovingSuffix As String = ".moving"

        ''' <summary>Fuer den Pruefstand: laeuft vor jedem Verschieben einer Datei mit ihrem Pfad.
        ''' Eine Ausnahme darin stellt einen Abbruch mitten im Umzug nach.</summary>
        Friend Shared BeforeEachMove As Action(Of String)

        Private Shared ReadOnly OldVersionPattern As New Regex("^(?<family>[A-Za-z0-9][A-Za-z0-9-]*?)-v(?<n>\d+)\.(?<ext>[A-Za-z0-9]+)$",
                                                               RegexOptions.CultureInvariant)

        ''' <summary>Fuehrt den Umzug aus. <paramref name="log"/> bekommt je Schritt eine Zeile.</summary>
        Public Shared Function Run(configRoot As String, dataRoot As String, legacyPresetFolder As String,
                                   log As List(Of String)) As FolderLayoutMigrationResult
            Dim result As New FolderLayoutMigrationResult()
            Try
                Directory.CreateDirectory(dataRoot)
            Catch ex As Exception
                log.Add($"data folder not usable: {dataRoot} - {ex.Message}")
                Return result
            End Try
            If Not AppFolders.CanWriteInto(dataRoot) Then
                log.Add($"data folder not writable: {dataRoot}")
                Return result
            End If
            result.TargetWritable = True

            result.ModelsDone = MigrateModels(Path.Combine(configRoot, AppFolders.LegacyModelsFolderName),
                                              Path.Combine(dataRoot, AppFolders.ModelsFolderName), log)
            result.ExtensionsDone = MigrateFolderMerging(Path.Combine(configRoot, AppFolders.ExtensionsFolderName),
                                                         Path.Combine(dataRoot, AppFolders.ExtensionsFolderName), log) And
                                    MigrateFolderMerging(Path.Combine(configRoot, AppFolders.OlderExtensionsFolderName),
                                                         Path.Combine(dataRoot, AppFolders.ExtensionsFolderName), log)
            result.FavoritesDone = MoveFileMerging(Path.Combine(configRoot, AppFolders.FavoritesFileName),
                                                   Path.Combine(dataRoot, AppFolders.FavoritesFileName), log)
            result.SearchListsDone = MoveFileMerging(Path.Combine(configRoot, AppFolders.SearchListsFileName),
                                                     Path.Combine(dataRoot, AppFolders.SearchListsFileName), log)
            result.GpuStateDone = MoveFileMerging(Path.Combine(configRoot, AppFolders.GpuBlockFileName & ".json"),
                                                  Path.Combine(dataRoot, AppFolders.GpuBlockFileName & ".json"), log) And
                                  MoveFileMerging(Path.Combine(configRoot, AppFolders.GpuBusyMarkerName & ".marker"),
                                                  Path.Combine(dataRoot, AppFolders.GpuBusyMarkerName & ".marker"), log)
            RemoveEmptyCatalogLeftover(Path.Combine(configRoot, AppFolders.CatalogFileName), dataRoot, log)
            result.PresetsDone = CopyPresets(legacyPresetFolder, Path.Combine(dataRoot, AppFolders.PresetsFolderName), log)
            log.Add(If(result.Complete, "folder layout complete", "folder layout incomplete, next start tries again"))
            Return result
        End Function

        ' --- Modelle ----------------------------------------------------------------------

        ''' <summary>Die Dateinamen, die diese Fassung benutzt: die aktuelle Datei und die noch
        ''' benutzbaren Vorgaenger jedes Modells.</summary>
        Friend Shared Function UsableModelFiles() As HashSet(Of String)
            Dim names As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each entry In AiModelService.KnownEntries
                names.Add(entry.FileName)
                For Each older In If(entry.Predecessors, Array.Empty(Of String)())
                    names.Add(older)
                Next
            Next
            Return names
        End Function

        ''' <summary>Die Familien, deren alte Fassungen eindeutig erkannt werden: aus jedem Namen,
        ''' den diese Fassung kennt, auch den abgeloesten.</summary>
        Friend Shared Function KnownModelFamilies() As HashSet(Of String)
            Dim families As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each entry In AiModelService.KnownEntries
                Dim all = {entry.FileName}.Concat(If(entry.Predecessors, Array.Empty(Of String)())).
                                           Concat(If(entry.ReplacesFiles, Array.Empty(Of String)()))
                For Each name In all
                    Dim match = OldVersionPattern.Match(name)
                    If match.Success Then families.Add(match.Groups("family").Value)
                Next
            Next
            Return families
        End Function

        ''' <summary>Ist das eine alte Fassung eines bekannten Modells, die keine Funktion mehr
        ''' nutzt?</summary>
        Friend Shared Function IsObsoleteModelFile(name As String, usable As HashSet(Of String), families As HashSet(Of String)) As Boolean
            If usable.Contains(name) Then Return False
            Dim match = OldVersionPattern.Match(name)
            Return match.Success AndAlso families.Contains(match.Groups("family").Value)
        End Function

        Private Shared Function MigrateModels(source As String, target As String, log As List(Of String)) As Boolean
            If PathIdentity.AreSame(source, target) Then Return True
            RecoverMovingFiles(source, target)
            If Not Directory.Exists(source) Then Return True
            Dim usable = UsableModelFiles()
            Dim families = KnownModelFamilies()
            Dim allDone = True
            Dim left As New List(Of String)()
            Try
                Directory.CreateDirectory(target)
            Catch ex As Exception
                log.Add($"models: target not usable {target} - {ex.Message}")
                Return False
            End Try
            For Each entryPath In Directory.EnumerateFileSystemEntries(source).OrderBy(Function(p) p, StringComparer.Ordinal).ToList()
                Dim name = Path.GetFileName(entryPath)
                If String.Equals(name, MovedNoteFileName, StringComparison.OrdinalIgnoreCase) Then Continue For
                If Directory.Exists(entryPath) Then
                    left.Add(name & Path.DirectorySeparatorChar)
                    Continue For
                End If
                Try
                    If usable.Contains(name) Then
                        If Not MoveModelFile(entryPath, Path.Combine(target, name), log) Then left.Add(name)
                    ElseIf IsObsoleteModelFile(name, usable, families) Then
                        Dim size = New FileInfo(entryPath).Length
                        File.Delete(entryPath)
                        log.Add($"models: removed old version {name} ({FormatSize(size)})")
                    Else
                        left.Add(name)
                    End If
                Catch ex As Exception
                    allDone = False
                    log.Add($"models: {name} failed - {ex.Message}")
                End Try
            Next
            Try
                WriteMovedNote(source, target, left)
            Catch ex As Exception
                log.Add($"models: {MovedNoteFileName} not written - {ex.Message}")
            End Try
            If left.Count > 0 Then log.Add("models: left in old folder: " & String.Join(", ", left))
            Return allDone
        End Function

        ''' <summary>Eine bekannte Modelldatei. Liegt am Ziel schon eine, entscheidet die
        ''' Pruefsumme; ohne eine (die Vorgaenger) die Groesse. False: beide Dateien bleiben, die
        ''' am alten Ort steht in der MOVED.txt.</summary>
        Private Shared Function MoveModelFile(source As String, target As String, log As List(Of String)) As Boolean
            Dim name = Path.GetFileName(source)
            FinishInterruptedMove(source, target)
            If Not File.Exists(source) Then Return True
            If Not File.Exists(target) Then
                MoveFileAtomically(source, target)
                log.Add($"models: moved {name}")
                Return True
            End If
            Dim entry = AiModelService.KnownEntries.FirstOrDefault(Function(e) String.Equals(e.FileName, name, StringComparison.OrdinalIgnoreCase))
            If entry IsNot Nothing AndAlso Not String.IsNullOrEmpty(entry.Sha256) Then
                Dim targetIntact = MatchesChecksum(target, entry)
                If targetIntact Then
                    File.Delete(source)
                    log.Add($"models: {name} already intact in target, old copy removed")
                    Return True
                End If
                If MatchesChecksum(source, entry) Then
                    File.Delete(target)
                    MoveFileAtomically(source, target)
                    log.Add($"models: {name} in target was damaged, replaced")
                    Return True
                End If
                log.Add($"models: {name} damaged in both places, left")
                Return False
            End If
            If New FileInfo(source).Length = New FileInfo(target).Length Then
                File.Delete(source)
                log.Add($"models: {name} already in target, old copy removed")
                Return True
            End If
            log.Add($"models: {name} differs from the file in target, left")
            Return False
        End Function

        Private Shared Function MatchesChecksum(file As String, entry As AiModelService.ModelEntry) As Boolean
            Try
                If entry.Bytes > 0 AndAlso New FileInfo(file).Length <> entry.Bytes Then Return False
            Catch
                Return False
            End Try
            Return String.Equals(AiModelService.ChecksumOf(file), entry.Sha256, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Die Notiz am alten Modellordner: wohin die Modelle gezogen sind und was dort
        ''' geblieben ist. Englisch und deutsch, sie steht in einem Ordner, den auch eine aeltere
        ''' Fassung oder ein Mensch im Dateimanager findet.</summary>
        Private Shared Sub WriteMovedNote(source As String, target As String, left As List(Of String))
            Dim text As New StringBuilder()
            text.Append("FerrumPix 0.9.92 and later keep their models in:").Append(vbLf)
            text.Append("FerrumPix ab 0.9.92 legt seine Modelle hier ab:").Append(vbLf)
            text.Append(target).Append(vbLf)
            If left.Count > 0 Then
                text.Append(vbLf)
                text.Append("These files are unknown to FerrumPix and stayed here:").Append(vbLf)
                text.Append("Diese Dateien kennt FerrumPix nicht; sie sind hier geblieben:").Append(vbLf)
                For Each name In left
                    text.Append("- ").Append(name).Append(vbLf)
                Next
            End If
            File.WriteAllText(Path.Combine(source, MovedNoteFileName), text.ToString(), New UTF8Encoding(False))
        End Sub

        ' --- Ordner und Dateien -----------------------------------------------------------

        ''' <summary>Fuehrt einen Ordner in einen anderen: was fehlt, kommt dazu; was da ist,
        ''' bleibt; eine andere Fassung kommt als ".old" daneben. Danach ist die Quelle leer und
        ''' entfernt. True, wenn nichts mehr in der Quelle liegt.</summary>
        Private Shared Function MigrateFolderMerging(source As String, target As String, log As List(Of String)) As Boolean
            If PathIdentity.AreSame(source, target) Then Return True
            RecoverMovingFiles(source, target)
            If Not Directory.Exists(source) Then Return True
            Dim allDone = True
            For Each file In Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList()
                If file.EndsWith(MovingSuffix, StringComparison.Ordinal) Then Continue For
                Dim relative = Path.GetRelativePath(source, file)
                If Not MoveFileMerging(file, Path.Combine(target, relative), log) Then allDone = False
            Next
            RemoveEmptyFolders(source)
            Return allDone AndAlso Not Directory.Exists(source)
        End Function

        ''' <summary>Eine Datei an ihren neuen Ort. True, wenn sie dort angekommen ist (oder es
        ''' nichts zu tun gab).</summary>
        Friend Shared Function MoveFileMerging(source As String, target As String, log As List(Of String)) As Boolean
            If PathIdentity.AreSame(source, target) Then Return True
            Try
                FinishInterruptedMove(source, target)
                If Not File.Exists(source) Then Return True
                Directory.CreateDirectory(Path.GetDirectoryName(target))
                If Not File.Exists(target) Then
                    MoveFileAtomically(source, target)
                    log.Add($"moved {source} to {target}")
                    Return True
                End If
                If SameContent(source, target) Then
                    File.Delete(source)
                    log.Add($"{target} already there, removed {source}")
                    Return True
                End If
                Dim aside = FreeOldName(target)
                MoveFileAtomically(source, aside)
                log.Add($"{target} already there with other content, kept, older version as {aside}")
                Return True
            Catch ex As Exception
                log.Add($"{source} not moved - {ex.Message}")
                Return False
            End Try
        End Function

        ''' <summary>Verschiebt ueber "&lt;ziel&gt;.moving": so ist nach einem Abbruch eindeutig,
        ''' welche Datei vollstaendig ist (<see cref="FinishInterruptedMove"/>).</summary>
        Private Shared Sub MoveFileAtomically(source As String, target As String)
            BeforeEachMove?.Invoke(source)
            Dim moving = target & MovingSuffix
            If File.Exists(moving) Then File.Delete(moving)
            File.Move(source, moving)
            File.Move(moving, target, overwrite:=False)
        End Sub

        ''' <summary>Raeumt nach einem Abbruch auf: liegt die Quelle noch da, ist die
        ''' ".moving"-Datei eine halbe Kopie und faellt weg; liegt nur noch sie da, war sie
        ''' vollstaendig und bekommt ihren Namen.</summary>
        Private Shared Sub FinishInterruptedMove(source As String, target As String)
            Dim moving = target & MovingSuffix
            If Not File.Exists(moving) Then Return
            If File.Exists(source) Then
                File.Delete(moving)
            ElseIf Not File.Exists(target) Then
                File.Move(moving, target)
            End If
        End Sub

        ''' <summary>Nach einem Abbruch liegt am Ziel womoeglich eine ".moving"-Datei, deren Quelle
        ''' es schon nicht mehr gibt; die Schleife ueber die Quelle saehe sie nie. Jede wird hier zu
        ''' Ende gebracht (<see cref="FinishInterruptedMove"/>).</summary>
        Private Shared Sub RecoverMovingFiles(sourceRoot As String, targetRoot As String)
            If Not Directory.Exists(targetRoot) Then Return
            For Each moving In Directory.EnumerateFiles(targetRoot, "*" & MovingSuffix, SearchOption.AllDirectories).ToList()
                Dim relative = Path.GetRelativePath(targetRoot, moving)
                relative = relative.Substring(0, relative.Length - MovingSuffix.Length)
                FinishInterruptedMove(Path.Combine(sourceRoot, relative), Path.Combine(targetRoot, relative))
            Next
        End Sub

        ''' <summary>"name.old.json", dann "name.old-2.json" und so weiter.</summary>
        Private Shared Function FreeOldName(target As String) As String
            Dim folder = Path.GetDirectoryName(target)
            Dim stem = Path.GetFileNameWithoutExtension(target)
            Dim extension = Path.GetExtension(target)
            Dim candidate = Path.Combine(folder, stem & ".old" & extension)
            Dim number = 2
            While File.Exists(candidate)
                candidate = Path.Combine(folder, stem & ".old-" & number.ToString(CultureInfo.InvariantCulture) & extension)
                number += 1
            End While
            Return candidate
        End Function

        Private Shared Function SameContent(a As String, b As String) As Boolean
            Dim infoA As New FileInfo(a)
            Dim infoB As New FileInfo(b)
            If infoA.Length <> infoB.Length Then Return False
            Return File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b))
        End Function

        Private Shared Sub RemoveEmptyFolders(root As String)
            Try
                If Not Directory.Exists(root) Then Return
                For Each folder In Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).
                                       OrderByDescending(Function(p) p.Length).ToList()
                    If FolderRelocation.IsEmptyDirectory(folder) Then Directory.Delete(folder)
                Next
                If FolderRelocation.IsEmptyDirectory(root) Then Directory.Delete(root)
            Catch
            End Try
        End Sub

        ''' <summary>Die leere library.db bei den Einstellungen: ein Rest ohne Inhalt, der echte
        ''' Katalog liegt im Datenordner. Nur bei genau 0 Byte, nie eine mit Inhalt.</summary>
        Private Shared Sub RemoveEmptyCatalogLeftover(file As String, dataRoot As String, log As List(Of String))
            Try
                If PathIdentity.AreSame(Path.GetDirectoryName(file), dataRoot) OrElse Not IO.File.Exists(file) Then Return
                Dim length = New FileInfo(file).Length
                If length = 0 Then
                    IO.File.Delete(file)
                    log.Add($"removed empty {file}")
                Else
                    log.Add($"{file} is not empty ({FormatSize(length)}), left untouched")
                End If
            Catch ex As Exception
                log.Add($"{file} - {ex.Message}")
            End Try
        End Sub

        ' --- Anpassungen ------------------------------------------------------------------

        ''' <summary>Kopiert die Anpassungen eines bis 0.9.91 gewaehlten Ordners in den Datenordner,
        ''' ueber die Import-Regel des Bestands: was mit gleicher Kennung und gleichem Inhalt schon
        ''' da ist, kommt nicht noch einmal. Im Ordner selbst bleibt alles, wie es war. Ist er nicht
        ''' erreichbar, versucht es der naechste Start.</summary>
        Private Shared Function CopyPresets(legacyFolder As String, target As String, log As List(Of String)) As Boolean
            Dim folder = If(legacyFolder, "").Trim()
            If folder.Length = 0 Then Return True
            Try
                If PathIdentity.AreSame(folder, target) Then Return True
                If Not Directory.Exists(folder) Then
                    log.Add($"presets: own folder not reachable, next start tries again: {folder}")
                    Return False
                End If
                Directory.CreateDirectory(target)
                Dim store As New AdjustmentPresetStore(target)
                Dim created = 0
                Dim present = 0
                For Each file In Directory.GetFiles(folder, "*" & AdjustmentPresetStore.FileExtension).OrderBy(Function(f) f, StringComparer.Ordinal)
                    Dim imported = store.Import(file, "")
                    created += imported.Imported
                    present += imported.AlreadyPresent
                Next
                For Each child In Directory.GetDirectories(folder).OrderBy(Function(d) d, StringComparer.Ordinal)
                    If Path.GetFileName(child).StartsWith(".", StringComparison.Ordinal) Then Continue For
                    Dim imported = store.Import(child, "")
                    created += imported.Imported
                    present += imported.AlreadyPresent
                Next
                AdjustmentPresetStore.Invalidate()
                log.Add($"presets: copied from {folder}: {created} new, {present} already there")
                Return True
            Catch ex As Exception
                log.Add($"presets: copy from {folder} failed - {ex.Message}")
                Return False
            End Try
        End Function

        Private Shared Function FormatSize(bytes As Long) As String
            If bytes >= 1024L * 1024L Then Return (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture) & " MB"
            Return (bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) & " KB"
        End Function

    End Class

End Namespace
