Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Threading

Namespace Services

    ''' <summary>Wie ein Umzug eines Ordnerinhalts ausgegangen ist.</summary>
    Public Enum FolderRelocationOutcome
        ''' <summary>Alles liegt am neuen Ort, der alte ist geleert.</summary>
        Moved
        ''' <summary>Abgebrochen; der alte Ort ist unveraendert.</summary>
        Cancelled
        ''' <summary>Am Ziel reicht der Platz nicht; der alte Ort ist unveraendert.</summary>
        TargetFull
        ''' <summary>Anderer Fehler; der alte Ort ist unveraendert.</summary>
        Failed
    End Enum

    ''' <summary>Ergebnis von <see cref="FolderRelocation.MoveContents"/>.</summary>
    Public NotInheritable Class FolderRelocationResult
        Public Property Outcome As FolderRelocationOutcome = FolderRelocationOutcome.Failed
        ''' <summary>Wie viele Dateien umgezogen sind.</summary>
        Public Property MovedFiles As Integer
        ''' <summary>True, wenn kopiert statt umbenannt wurde.</summary>
        Public Property Copied As Boolean
        ''' <summary>Die Meldung des Dateisystems oder die Datei, an der es scheiterte.</summary>
        Public Property Detail As String = ""
    End Class

    ''' <summary>Ein Zwischenstand eines Umzugs fuer die Anzeige: wie viele Dateien und Bytes
    ''' schon am Ziel sind, welche gerade dran ist und zwischen welchen Orten es geht.</summary>
    Public NotInheritable Class FolderMoveProgress
        Public Property FilesDone As Integer
        ''' <summary>0, wenn die Gesamtzahl nicht vorab bekannt ist.</summary>
        Public Property FilesTotal As Integer
        Public Property BytesDone As Long
        ''' <summary>0, wenn die Gesamtgroesse nicht vorab bekannt ist.</summary>
        Public Property BytesTotal As Long
        ''' <summary>Die Datei, die gerade umzieht, "" zwischen zwei Dateien.</summary>
        Public Property CurrentFile As String = ""
        Public Property Source As String = ""
        Public Property Target As String = ""

        Public Function Clone() As FolderMoveProgress
            Return DirectCast(MemberwiseClone(), FolderMoveProgress)
        End Function
    End Class

    ''' <summary>Was ein Aufrufer an einem Umzug selbst bestimmt.</summary>
    Public NotInheritable Class FolderRelocationOptions
        ''' <summary>Welche Eintraege der obersten Ebene mitgehen (Name ohne Pfad). Nothing: alle,
        ''' und nur dann darf der ganze Ordner in einem Schritt umbenannt werden.</summary>
        Public Property IncludeEntry As Func(Of String, Boolean)

        ''' <summary>Dateien der obersten Ebene, die der Aufrufer in <see cref="CompleteCopy"/>
        ''' selbst hinueberbringt (etwa einen Index, der mit einem am Ziel liegenden
        ''' zusammengefuehrt wird). Sie werden nicht kopiert, am alten Ort aber wie der Rest
        ''' entfernt; entstehen sie am Ziel neu, nimmt ein Rueckbau sie mit.</summary>
        Public Property CallerHandledFiles As New HashSet(Of String)(StringComparer.Ordinal)

        ''' <summary>Laeuft nach dem Kopieren, vor dem Leeren des alten Orts, mit (alter Ort, neuer
        ''' Ort). Eine Ausnahme darin baut alles zurueck.</summary>
        Public Property CompleteCopy As Action(Of String, String)

        ''' <summary>Freier Platz an einem Ort; Nothing misst ueber DriveInfo. Fuer den Pruefstand.</summary>
        Public Property FreeSpaceProbe As Func(Of String, Long)

        ''' <summary>Was am Ziel ueber die Dateien hinaus frei bleiben muss.</summary>
        Public Property FreeSpaceReserveBytes As Long = 64L * 1024L * 1024L

        ''' <summary>Eine Datei am Ziel gilt nur als schon vorhanden, wenn auch ihre
        ''' Aenderungszeit stimmt; jede kopierte Datei bekommt dafuer die der Quelle. Fuer den
        ''' Datenordner: eine Datenbank aendert sich oft, ohne ihre Groesse zu aendern, und ein
        ''' Umzug, der nach einem Abbruch fortsetzt, liesse sonst eine alte Fassung am Ziel stehen.</summary>
        Public Property RequireSameTimestamp As Boolean

        ''' <summary>Bekommt vor und nach jeder Datei den Stand (Anzahl, Bytes, aktuelle Datei),
        ''' fuer das Fenster beim Start. Aendert am Umzug selbst nichts; Nothing: keine Meldung.</summary>
        Public Property DetailProgress As IProgress(Of FolderMoveProgress)
    End Class

    ''' <summary>Zieht den Inhalt eines Ordners an einen anderen Ort um, ohne dass dabei etwas
    ''' verloren gehen kann. Er traegt den Umzug des ganzen Datenordners (AppFolders,
    ''' packaging/Plaene/ABLAGEORTE.md).
    '''
    ''' Erst ein einziges Umbenennen des ganzen Ordners: auf derselben Platte geschieht das ganz oder
    ''' gar nicht. Auf eine andere Platte, in einen nicht leeren Zielordner oder bei einer Auswahl
    ''' von Eintraegen wird kopiert: vorher der freie Platz, dann Datei fuer Datei unter einem
    ''' Zwischennamen (<see cref="PartSuffix"/>) mit Pruefung der Groesse und erst dann unter dem
    ''' eigenen, Abbruch zwischen zwei Dateien. Erst wenn alles am Ziel liegt und der Aufrufer
    ''' abgeschlossen hat, wird der alte Ort geleert, und zwar genau die kopierten Dateien und die
    ''' dabei leer gewordenen Ordner. Bei Abbruch, vollem Ziel oder Fehler wird alles entfernt, was
    ''' am Ziel entstanden ist; der alte Ort bleibt, wie er war.
    '''
    ''' Gleichzeitiges Schreiben in den alten Ort muss der Aufrufer anhalten. Eine Datei, die
    ''' waehrend des Kopierens am alten Ort verschwindet, fehlt am Ziel einfach. Eine Datei, die am
    ''' Ziel schon gleich gross liegt (auf Wunsch auch gleich alt,
    ''' <see cref="FolderRelocationOptions.RequireSameTimestamp"/>), gilt als vorhanden und wird
    ''' nicht ueberschrieben; so setzt ein abgebrochener Umzug beim naechsten Mal fort.
    '''
    ''' Eine andere Datei, die am Ziel ersetzt wird, liegt bis zum Ende unter
    ''' <see cref="ReplacedSuffix"/> beiseite und kommt beim Rueckbau zurueck; ebenso Begleiter
    ''' einer Datenbank (-wal, -shm, -journal) am Ziel, die die Quelle nicht hat. Verweise
    ''' (symbolische Links, Verzweigungen) werden nie durchlaufen, sondern am Ziel als Verweis mit
    ''' demselben Ziel neu angelegt und am alten Ort als Verweis entfernt.</summary>
    Public NotInheritable Class FolderRelocation
        Private Sub New()
        End Sub

        ''' <summary>Zieht um. Laeuft lange und gehoert auf einen Hintergrundfaden;
        ''' <paramref name="progress"/> bekommt den Anteil von 0 bis 1.</summary>
        Public Shared Function MoveContents(sourceRoot As String, targetRoot As String,
                                            Optional options As FolderRelocationOptions = Nothing,
                                            Optional progress As IProgress(Of Double) = Nothing,
                                            Optional cancellationToken As CancellationToken = Nothing) As FolderRelocationResult
            Dim opts = If(options, New FolderRelocationOptions())
            Dim result As New FolderRelocationResult()
            If String.IsNullOrWhiteSpace(sourceRoot) OrElse String.IsNullOrWhiteSpace(targetRoot) Then Return result
            Dim source = TrimDir(sourceRoot)
            Dim target = TrimDir(targetRoot)
            If PathIdentity.IsFilesystemRoot(source) OrElse PathIdentity.IsFilesystemRoot(target) Then Return result
            If PathIdentity.IsAncestorOrSelf(source, target) OrElse PathIdentity.IsAncestorOrSelf(target, source) Then Return result
            If cancellationToken.IsCancellationRequested Then
                result.Outcome = FolderRelocationOutcome.Cancelled
                Return result
            End If
            If Not Directory.Exists(source) Then
                Directory.CreateDirectory(target)
                result.Outcome = FolderRelocationOutcome.Moved
                Return result
            End If

            If opts.IncludeEntry Is Nothing Then
                Dim renamed = TryRename(source, target)
                If renamed >= 0 Then
                    result.Outcome = FolderRelocationOutcome.Moved
                    result.MovedFiles = renamed
                    progress?.Report(1.0)
                    opts.DetailProgress?.Report(New FolderMoveProgress With {
                        .FilesDone = renamed, .FilesTotal = renamed, .Source = source, .Target = target})
                    Return result
                End If
            End If
            Return CopyThenRemove(source, target, opts, progress, cancellationToken)
        End Function

        Private Shared Function TrimDir(path As String) As String
            Return IO.Path.GetFullPath(path).TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
        End Function

        Friend Shared Function IsEmptyDirectory(path As String) As Boolean
            Try
                Return Directory.Exists(path) AndAlso Not Directory.EnumerateFileSystemEntries(path).Any()
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Benennt den ganzen Ordner um. Gibt die Zahl der Dateien zurueck, -1 wenn es
        ''' nicht ging; dann ist nichts veraendert (ein leerer Zielordner steht wieder da).</summary>
        Private Shared Function TryRename(source As String, target As String) As Integer
            Dim targetExisted = Directory.Exists(target)
            If targetExisted AndAlso Not IsEmptyDirectory(target) Then Return -1
            Dim parent = IO.Path.GetDirectoryName(target)
            If String.IsNullOrEmpty(parent) OrElse Not Directory.Exists(parent) Then Return -1
            Dim count = 0
            Try
                ' Nur zum Anzeigen; ohne einem Verweis zu folgen (siehe EnumerateTree).
                count = EnumerateTree(source).Count
            Catch
            End Try
            Dim removedEmptyTarget = False
            Try
                If targetExisted Then
                    Directory.Delete(target, recursive:=False)
                    removedEmptyTarget = True
                End If
                Directory.Move(source, target)
                Return count
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                ' Andere Platte (EXDEV), Einhaengepunkt oder fehlende Rechte: dann wird kopiert.
                If removedEmptyTarget AndAlso Not Directory.Exists(target) Then
                    Try
                        Directory.CreateDirectory(target)
                    Catch
                    End Try
                End If
                Return -1
            End Try
        End Function

        ''' <summary>Eine Datei des Umzugs.</summary>
        Private Structure CopyItem
            Public Source As String
            Public Target As String
            Public Length As Long
            Public WriteTimeUtc As DateTime
            ''' <summary>Ein Verweis (symbolischer Link, unter Windows auch eine Verzweigung): er
            ''' wird am Ziel als Verweis mit demselben Ziel neu angelegt, nie durchlaufen.</summary>
            Public LinkTarget As String
            Public LinkIsDirectory As Boolean

            Public ReadOnly Property IsLink As Boolean
                Get
                    Return LinkTarget IsNot Nothing
                End Get
            End Property
        End Structure

        ''' <summary>Das Verweisziel eines Eintrags, Nothing fuer eine gewoehnliche Datei oder einen
        ''' gewoehnlichen Ordner. Andere Analysepunkte (Platzhalter eines Cloud-Speichers) haben
        ''' kein Verweisziel und gelten als gewoehnlich.</summary>
        Friend Shared Function LinkTargetOf(info As FileSystemInfo) As String
            Try
                Return info.LinkTarget
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Alle Dateien und Verweise unter <paramref name="folder"/>, OHNE einem Verweis
        ''' auf einen Ordner zu folgen. Directory.EnumerateFiles mit AllDirectories folgt ihnen
        ''' unter .NET 10 (gemessen unter Linux): ein Ordner "Fonts", der auf ~/.fonts zeigt, kaeme
        ''' sonst mit seinem Inhalt in den Umzug, und das Leeren des alten Orts loeschte die
        ''' Schriften am Ziel des Verweises.</summary>
        Friend Shared Function EnumerateTree(folder As String) As List(Of FileSystemInfo)
            Dim found As New List(Of FileSystemInfo)()
            Dim pending As New Stack(Of DirectoryInfo)()
            pending.Push(New DirectoryInfo(folder))
            While pending.Count > 0
                For Each info In pending.Pop().EnumerateFileSystemInfos()
                    If LinkTargetOf(info) Is Nothing AndAlso TypeOf info Is DirectoryInfo Then
                        pending.Push(DirectCast(info, DirectoryInfo))
                    Else
                        found.Add(info)
                    End If
                Next
            End While
            Return found
        End Function

        ''' <summary>Die Ordner unter <paramref name="folder"/> ohne Verweise und ohne das, was
        ''' hinter einem Verweis liegt.</summary>
        Friend Shared Function EnumerateRealDirectories(folder As String) As List(Of String)
            Dim found As New List(Of String)()
            Dim pending As New Stack(Of DirectoryInfo)()
            pending.Push(New DirectoryInfo(folder))
            While pending.Count > 0
                For Each child In pending.Pop().EnumerateDirectories()
                    If LinkTargetOf(child) IsNot Nothing Then Continue For
                    found.Add(child.FullName)
                    pending.Push(child)
                Next
            End While
            Return found
        End Function

        ''' <summary>Legt einen Verweis mit diesem Ziel an.</summary>
        Friend Shared Sub CreateLink(path As String, linkTarget As String, isDirectory As Boolean)
            If isDirectory Then
                Directory.CreateSymbolicLink(path, linkTarget)
            Else
                File.CreateSymbolicLink(path, linkTarget)
            End If
        End Sub

        ''' <summary>Entfernt einen Verweis selbst, nie das, worauf er zeigt.</summary>
        Friend Shared Sub DeleteLink(path As String)
            Dim info As New FileInfo(path)
            If (info.Attributes And FileAttributes.Directory) <> 0 AndAlso OperatingSystem.IsWindows() Then
                Directory.Delete(path, recursive:=False)
            Else
                File.Delete(path)
            End If
        End Sub

        ''' <summary>Das Verweisziel, wenn an <paramref name="path"/> ein Verweis liegt, sonst
        ''' Nothing.</summary>
        Private Shared Function LinkAt(path As String) As String
            Try
                Return New FileInfo(path).LinkTarget
            Catch
                Return Nothing
            End Try
        End Function

        ''' <summary>Zieht einen Verweis um: am Ziel als Verweis mit demselben Ziel neu, am alten Ort
        ''' entfernt; was dahinter liegt, bleibt unberuehrt. False, wenn hier kein Verweis liegt oder
        ''' am Ziel schon etwas.</summary>
        Friend Shared Function MoveLink(source As String, target As String) As Boolean
            Dim linkTarget = LinkAt(source)
            If linkTarget Is Nothing OrElse AnythingAt(target) Then Return False
            Dim parent = IO.Path.GetDirectoryName(target)
            If Not String.IsNullOrEmpty(parent) Then Directory.CreateDirectory(parent)
            CreateLink(target, linkTarget, Directory.Exists(source))
            DeleteLink(source)
            Return True
        End Function

        ''' <summary>Liegt hier ein Verweis?</summary>
        Friend Shared Function IsLink(path As String) As Boolean
            Return LinkAt(path) IsNot Nothing
        End Function

        ''' <summary>Liegt an <paramref name="path"/> irgendetwas: Datei, Ordner oder ein Verweis,
        ''' auch einer, dessen Ziel fehlt.</summary>
        Private Shared Function AnythingAt(path As String) As Boolean
            Return File.Exists(path) OrElse Directory.Exists(path) OrElse LinkAt(path) IsNot Nothing
        End Function

        ''' <summary>Die Begleiter einer SQLite-Datenbank, die neben ihr liegen.</summary>
        Private Shared ReadOnly DatabaseCompanionSuffixes As String() = {"-wal", "-shm", "-journal"}

        ''' <summary>Die Endung einer Zieldatei, die der Umzug ersetzt: sie liegt bis zum Ende
        ''' beiseite und kommt beim Rueckbau zurueck.</summary>
        Friend Const ReplacedSuffix As String = ".ferrumpix-replaced"

        Private Shared Function CopyThenRemove(source As String, target As String, opts As FolderRelocationOptions,
                                               progress As IProgress(Of Double),
                                               cancellationToken As CancellationToken) As FolderRelocationResult
            Dim result As New FolderRelocationResult With {.Copied = True}
            Dim items As New List(Of CopyItem)()
            Dim callerHandled As New List(Of String)()
            For Each entryInfo In New DirectoryInfo(source).EnumerateFileSystemInfos()
                Dim name = entryInfo.Name
                If opts.IncludeEntry IsNot Nothing AndAlso Not opts.IncludeEntry(name) Then Continue For
                Dim entryIsLink = LinkTargetOf(entryInfo) IsNot Nothing
                If TypeOf entryInfo Is FileInfo AndAlso Not entryIsLink AndAlso opts.CallerHandledFiles.Contains(name) Then
                    callerHandled.Add(name)
                    Continue For
                End If
                Dim found As IEnumerable(Of FileSystemInfo)
                If TypeOf entryInfo Is DirectoryInfo AndAlso Not entryIsLink Then
                    found = EnumerateTree(entryInfo.FullName)
                Else
                    found = {entryInfo}
                End If
                For Each info In found
                    Dim relative = IO.Path.GetRelativePath(source, info.FullName)
                    Dim linkTarget = LinkTargetOf(info)
                    If linkTarget IsNot Nothing Then
                        items.Add(New CopyItem With {.Source = info.FullName, .Target = IO.Path.Combine(target, relative),
                                                     .LinkTarget = linkTarget, .LinkIsDirectory = TypeOf info Is DirectoryInfo})
                    Else
                        Dim fileInfo = DirectCast(info, FileInfo)
                        items.Add(New CopyItem With {.Source = info.FullName, .Target = IO.Path.Combine(target, relative), .Length = fileInfo.Length,
                                                     .WriteTimeUtc = fileInfo.LastWriteTimeUtc})
                    End If
                Next
            Next
            Dim sourceFiles = items.Select(Function(i) i.Source).ToHashSet(StringComparer.Ordinal)

            Dim totalBytes = Math.Max(1L, items.Sum(Function(i) i.Length))
            Dim needed = items.Where(Function(i) Not AlreadyAtTarget(i, opts)).Sum(Function(i) i.Length)
            Dim free = FreeBytesAt(opts, If(Directory.Exists(target), target, IO.Path.GetDirectoryName(target)))
            If free >= 0 AndAlso free < needed + opts.FreeSpaceReserveBytes Then
                result.Outcome = FolderRelocationOutcome.TargetFull
                Return result
            End If

            Dim createdFiles As New List(Of String)()
            Dim createdDirs As New List(Of String)()
            Dim copiedSources As New List(Of String)()
            Dim createdRoot = Not Directory.Exists(target)
            Dim callerFilesBefore = callerHandled.Where(Function(n) File.Exists(IO.Path.Combine(target, n))).ToHashSet(StringComparer.Ordinal)
            ' Was am Ziel schon lag und ersetzt wird (Original, beiseitegelegt unter ReplacedSuffix).
            Dim replaced As New Dictionary(Of String, String)(StringComparer.Ordinal)
            Dim rollback =
                Sub()
                    For Each path In createdFiles
                        Try
                            If LinkAt(path) IsNot Nothing Then
                                DeleteLink(path)
                            Else
                                File.Delete(path)
                            End If
                        Catch
                        End Try
                    Next
                    ' Was ersetzt wurde, kommt zurueck, samt den Begleitern einer Datenbank.
                    For Each pair In replaced
                        Try
                            If File.Exists(pair.Value) Then File.Move(pair.Value, pair.Key, overwrite:=True)
                        Catch ex As Exception
                            DiagnosticLogService.LogException("FolderRelocation.RestoreReplaced", ex)
                        End Try
                    Next
                    For Each name In callerHandled
                        If callerFilesBefore.Contains(name) Then Continue For
                        Try
                            File.Delete(IO.Path.Combine(target, name))
                        Catch
                        End Try
                    Next
                    ' Tiefste zuerst, damit ein Ordner leer ist, wenn er an der Reihe ist.
                    For Each path In createdDirs.OrderByDescending(Function(p) p.Length)
                        Try
                            If IsEmptyDirectory(path) Then Directory.Delete(path, recursive:=False)
                        Catch
                        End Try
                    Next
                    Try
                        If createdRoot AndAlso IsEmptyDirectory(target) Then Directory.Delete(target, recursive:=False)
                    Catch
                    End Try
                End Sub

            ' Nur fuer die Anzeige: dieselbe Zaehlung wie unten, mit der Datei, die gerade dran ist.
            Dim detail = opts.DetailProgress
            Dim doneFiles = 0
            Dim reportDetail =
                Sub(doneBytesNow As Long, current As String)
                    detail?.Report(New FolderMoveProgress With {
                        .FilesDone = doneFiles, .FilesTotal = items.Count, .BytesDone = doneBytesNow,
                        .BytesTotal = totalBytes, .CurrentFile = If(current, ""), .Source = source, .Target = target})
                End Sub

            ' Eine Zieldatei beiseitelegen, bevor sie ersetzt wird. Liegt schon ein Original
            ' beiseite (ein abgebrochener Umzug), bleibt es: es ist der Stand vor dem ersten Versuch.
            Dim setAside =
                Sub(path As String)
                    Dim aside = path & ReplacedSuffix
                    If Not File.Exists(aside) Then File.Move(path, aside)
                    replaced(path) = aside
                End Sub
            ' Begleiter einer Datenbank am Ziel, die die Quelle nicht hat: eine alte library.db-wal
            ' neben der neuen library.db spielte SQLite beim Oeffnen in sie ein.
            Dim setAsideStaleCompanions =
                Sub(item As CopyItem)
                    For Each suffix In DatabaseCompanionSuffixes
                        Dim companion = item.Target & suffix
                        If sourceFiles.Contains(item.Source & suffix) OrElse Not File.Exists(companion) Then Continue For
                        setAside(companion)
                    Next
                End Sub

            Try
                Directory.CreateDirectory(target)
                Dim doneBytes = 0L
                reportDetail(0L, "")
                For Each item In items
                    If cancellationToken.IsCancellationRequested Then
                        rollback()
                        result.Outcome = FolderRelocationOutcome.Cancelled
                        Return result
                    End If
                    ' Ein Original aus einem abgebrochenen Versuch gehoert zum Rueckbau dieses.
                    If File.Exists(item.Target & ReplacedSuffix) Then replaced(item.Target) = item.Target & ReplacedSuffix
                    If AlreadyAtTarget(item, opts) Then
                        TryDeleteFile(item.Target & PartSuffix)
                        If Not item.IsLink Then setAsideStaleCompanions(item)
                        copiedSources.Add(item.Source)
                        doneBytes += item.Length
                        doneFiles += 1
                        reportDetail(doneBytes, "")
                        Continue For
                    End If
                    reportDetail(doneBytes, item.Source)
                    EnsureDirectory(IO.Path.GetDirectoryName(item.Target), target, createdDirs)
                    If item.IsLink Then
                        ' Ein Verweis wird als Verweis neu angelegt. Liegt dort schon etwas anderes,
                        ' oder legt das System keinen an (Windows ohne das Recht dazu), bleibt er am
                        ' alten Ort stehen; dort wird er dann auch nicht entfernt.
                        If AnythingAt(item.Target) Then
                            DiagnosticLogService.LogAlways("FolderRelocation", $"link {item.Source} not moved, {item.Target} exists")
                        Else
                            Try
                                CreateLink(item.Target, item.LinkTarget, item.LinkIsDirectory)
                                createdFiles.Add(item.Target)
                                copiedSources.Add(item.Source)
                                result.MovedFiles += 1
                            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                                DiagnosticLogService.LogAlways("FolderRelocation", $"link {item.Source} not moved: {ex.Message}")
                            End Try
                        End If
                        doneFiles += 1
                        reportDetail(doneBytes, "")
                        Continue For
                    End If
                    Dim existedBefore = File.Exists(item.Target)
                    ' Erst unter einem Zwischennamen, erst vollstaendig unter dem eigenen: bricht der
                    ' Prozess mitten in einer Datei ab, liegt am Ziel nie eine halbe Datei unter dem
                    ' richtigen Namen. Je nach System legt das Kopieren die Groesse vorab an oder
                    ' uebernimmt die Aenderungszeit; eine halbe Kopie saehe dann vollstaendig aus.
                    Dim part = item.Target & PartSuffix
                    createdFiles.Add(part)
                    Try
                        File.Copy(item.Source, part, overwrite:=True)
                    Catch ex As IOException
                        If Not File.Exists(item.Source) Then
                            TryDeleteFile(part)
                            Continue For
                        End If
                        rollback()
                        result.Outcome = If(IsDiskFull(opts, ex, item.Target, item.Length),
                                            FolderRelocationOutcome.TargetFull, FolderRelocationOutcome.Failed)
                        result.Detail = ex.Message
                        Return result
                    End Try
                    If opts.RequireSameTimestamp Then File.SetLastWriteTimeUtc(part, item.WriteTimeUtc)
                    If New FileInfo(part).Length <> item.Length Then
                        rollback()
                        result.Detail = item.Target
                        Return result
                    End If
                    If existedBefore Then setAside(item.Target)
                    setAsideStaleCompanions(item)
                    File.Move(part, item.Target, overwrite:=True)
                    If Not existedBefore Then createdFiles.Add(item.Target)
                    copiedSources.Add(item.Source)
                    result.MovedFiles += 1
                    doneBytes += item.Length
                    doneFiles += 1
                    progress?.Report(Math.Min(0.99, doneBytes / CDbl(totalBytes)))
                    reportDetail(doneBytes, "")
                Next
                opts.CompleteCopy?.Invoke(source, target)
            Catch ex As Exception
                rollback()
                result.Outcome = If(TypeOf ex Is IOException AndAlso IsDiskFull(opts, DirectCast(ex, IOException), target, 0),
                                    FolderRelocationOutcome.TargetFull, FolderRelocationOutcome.Failed)
                result.Detail = ex.Message
                Return result
            End Try

            ' Der neue Stand gilt: die beiseitegelegten Originale am Ziel werden nicht mehr gebraucht.
            For Each aside In replaced.Values
                TryDeleteFile(aside)
            Next
            ' Erst jetzt den alten Ort leeren: genau das Kopierte, dann die leer gewordenen Ordner.
            ' Ein Verweis wird als Verweis entfernt, nie das, worauf er zeigt.
            For Each path In copiedSources
                Try
                    If LinkAt(path) IsNot Nothing Then
                        DeleteLink(path)
                    Else
                        File.Delete(path)
                    End If
                Catch ex As Exception
                    DiagnosticLogService.LogException("FolderRelocation.RemoveSource", ex)
                End Try
            Next
            For Each name In callerHandled
                Try
                    File.Delete(IO.Path.Combine(source, name))
                Catch
                End Try
            Next
            RemoveEmptyDirectories(source)
            result.Outcome = FolderRelocationOutcome.Moved
            progress?.Report(1.0)
            Return result
        End Function

        ''' <summary>Die Endung einer Datei, die gerade kopiert wird.</summary>
        Friend Const PartSuffix As String = ".ferrumpix-part"

        Private Shared Sub TryDeleteFile(path As String)
            Try
                If File.Exists(path) Then File.Delete(path)
            Catch
            End Try
        End Sub

        Private Shared Sub EnsureDirectory(folder As String, root As String, created As List(Of String))
            If Directory.Exists(folder) Then Return
            ' Von oben nach unten anlegen und jeden neuen Ordner fuer den Rueckbau merken.
            Dim missing As New Stack(Of String)()
            Dim current = folder
            While Not String.IsNullOrEmpty(current) AndAlso Not Directory.Exists(current) AndAlso
                  PathIdentity.IsAncestorOrSelf(root, current)
                missing.Push(current)
                current = IO.Path.GetDirectoryName(current)
            End While
            While missing.Count > 0
                Dim path = missing.Pop()
                Directory.CreateDirectory(path)
                created.Add(path)
            End While
        End Sub

        ''' <summary>Entfernt leere Ordner unter <paramref name="root"/> und ihn selbst, wenn er
        ''' danach leer ist. Ordner mit Inhalt bleiben.</summary>
        Private Shared Sub RemoveEmptyDirectories(root As String)
            Try
                If Not Directory.Exists(root) Then Return
                For Each folder In EnumerateRealDirectories(root).OrderByDescending(Function(p) p.Length).ToList()
                    If IsEmptyDirectory(folder) Then Directory.Delete(folder, recursive:=False)
                Next
                If IsEmptyDirectory(root) Then Directory.Delete(root, recursive:=False)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                DiagnosticLogService.LogException("FolderRelocation.RemoveEmpty", ex)
            End Try
        End Sub

        ''' <summary>Liegt die Datei am Ziel schon? Gleich gross, mit
        ''' <see cref="FolderRelocationOptions.RequireSameTimestamp"/> auch gleich alt.</summary>
        Private Shared Function AlreadyAtTarget(item As CopyItem, opts As FolderRelocationOptions) As Boolean
            If item.IsLink Then Return String.Equals(LinkAt(item.Target), item.LinkTarget, StringComparison.Ordinal)
            Try
                Dim info As New FileInfo(item.Target)
                If Not info.Exists OrElse info.Length <> item.Length Then Return False
                Return Not opts.RequireSameTimestamp OrElse info.LastWriteTimeUtc = item.WriteTimeUtc
            Catch
                Return False
            End Try
        End Function

        Private Shared Function FreeBytesAt(opts As FolderRelocationOptions, path As String) As Long
            If opts.FreeSpaceProbe IsNot Nothing Then Return opts.FreeSpaceProbe(path)
            Try
                Return New DriveInfo(path).AvailableFreeSpace
            Catch
                Return -1
            End Try
        End Function

        ''' <summary>ENOSPC unter Unix, ERROR_DISK_FULL und ERROR_HANDLE_DISK_FULL unter Windows,
        ''' oder schlicht zu wenig Platz fuer diese Datei.</summary>
        Private Shared Function IsDiskFull(opts As FolderRelocationOptions, ex As IOException, path As String, length As Long) As Boolean
            Dim code = ex.HResult And &HFFFF
            If code = 112 OrElse code = 39 OrElse code = 28 Then Return True
            Dim free = FreeBytesAt(opts, If(Directory.Exists(path), path, IO.Path.GetDirectoryName(path)))
            Return free >= 0 AndAlso free <= length
        End Function

    End Class

End Namespace
