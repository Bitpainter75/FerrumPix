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
    ''' nicht ueberschrieben; so setzt ein abgebrochener Umzug beim naechsten Mal fort.</summary>
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
                count = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count()
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
        End Structure

        Private Shared Function CopyThenRemove(source As String, target As String, opts As FolderRelocationOptions,
                                               progress As IProgress(Of Double),
                                               cancellationToken As CancellationToken) As FolderRelocationResult
            Dim result As New FolderRelocationResult With {.Copied = True}
            Dim items As New List(Of CopyItem)()
            Dim callerHandled As New List(Of String)()
            For Each entry In Directory.EnumerateFileSystemEntries(source)
                Dim name = IO.Path.GetFileName(entry)
                If opts.IncludeEntry IsNot Nothing AndAlso Not opts.IncludeEntry(name) Then Continue For
                If File.Exists(entry) Then
                    If opts.CallerHandledFiles.Contains(name) Then
                        callerHandled.Add(name)
                        Continue For
                    End If
                    Dim fileInfo As New FileInfo(entry)
                    items.Add(New CopyItem With {.Source = entry, .Target = IO.Path.Combine(target, name), .Length = fileInfo.Length,
                                                 .WriteTimeUtc = fileInfo.LastWriteTimeUtc})
                ElseIf Directory.Exists(entry) Then
                    For Each info In New DirectoryInfo(entry).EnumerateFiles("*", SearchOption.AllDirectories)
                        Dim relative = IO.Path.GetRelativePath(source, info.FullName)
                        items.Add(New CopyItem With {.Source = info.FullName, .Target = IO.Path.Combine(target, relative), .Length = info.Length,
                                                     .WriteTimeUtc = info.LastWriteTimeUtc})
                    Next
                End If
            Next

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
            Dim rollback =
                Sub()
                    For Each path In createdFiles
                        Try
                            File.Delete(path)
                        Catch
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

            Try
                Directory.CreateDirectory(target)
                Dim doneBytes = 0L
                For Each item In items
                    If cancellationToken.IsCancellationRequested Then
                        rollback()
                        result.Outcome = FolderRelocationOutcome.Cancelled
                        Return result
                    End If
                    If AlreadyAtTarget(item, opts) Then
                        TryDeleteFile(item.Target & PartSuffix)
                        copiedSources.Add(item.Source)
                        doneBytes += item.Length
                        Continue For
                    End If
                    EnsureDirectory(IO.Path.GetDirectoryName(item.Target), target, createdDirs)
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
                    File.Move(part, item.Target, overwrite:=True)
                    If Not existedBefore Then createdFiles.Add(item.Target)
                    copiedSources.Add(item.Source)
                    result.MovedFiles += 1
                    doneBytes += item.Length
                    progress?.Report(Math.Min(0.99, doneBytes / CDbl(totalBytes)))
                Next
                opts.CompleteCopy?.Invoke(source, target)
            Catch ex As Exception
                rollback()
                result.Outcome = If(TypeOf ex Is IOException AndAlso IsDiskFull(opts, DirectCast(ex, IOException), target, 0),
                                    FolderRelocationOutcome.TargetFull, FolderRelocationOutcome.Failed)
                result.Detail = ex.Message
                Return result
            End Try

            ' Erst jetzt den alten Ort leeren: genau das Kopierte, dann die leer gewordenen Ordner.
            For Each path In copiedSources
                Try
                    File.Delete(path)
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
                For Each folder In Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).
                                       OrderByDescending(Function(p) p.Length).ToList()
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
