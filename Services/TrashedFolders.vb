Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text

Namespace Services

    ''' <summary>
    ''' Sagt fuer einen Ordner, den es auf der Platte nicht mehr gibt, ob er im Papierkorb liegt.
    '''
    ''' WOZU: ein Ordner, der nur noch im Katalog steht, erscheint in Ordnerbaum und Galerie als
    ''' "nicht mehr vorhanden" (FolderListing). Ein Ordner im Papierkorb ist das nicht: er kann
    ''' zurueckgeholt werden, und dann gehoeren ihm seine Katalogzeilen mit Bewertungen,
    ''' Etiketten, Stichwoertern und Personen wieder. Er erscheint deshalb gar nicht, und seine
    ''' Zeilen bleiben unangetastet. Erst wenn der Papierkorb geleert ist, gilt er als geloescht.
    '''
    ''' WIE, je System:
    ''' <list type="bullet">
    ''' <item>Linux (freedesktop): je Eintrag eine Datei <c>info/NAME.trashinfo</c> mit der Zeile
    ''' <c>Path=</c>, dem urspruenglichen Ort. Gelesen werden der Papierkorb des Benutzers
    ''' (<c>$XDG_DATA_HOME/Trash</c>, sonst <c>~/.local/share/Trash</c>, Pfade absolut) und die
    ''' beiden des Datentraegers, auf dem der Ordner lag (<c>WURZEL/.Trash/UID</c> und
    ''' <c>WURZEL/.Trash-UID</c>, Pfade relativ zur Wurzel). Nur Eintraege, deren Dateiname mit dem
    ''' Ordnernamen beginnt, werden geoeffnet; der Name im Papierkorb ist der urspruengliche, bei
    ''' Gleichnamigen mit Zusatz.</item>
    ''' <item>Windows: im Papierkorb des Laufwerks (<c>X:\$Recycle.Bin\SID</c>) haelt je Eintrag
    ''' eine Datei <c>$I...</c> den urspruenglichen Pfad. Die Namen sind zufaellig, gelesen wird
    ''' also jede; klein sind sie alle.</item>
    ''' <item>macOS: der Papierkorb haelt den urspruenglichen Ort nicht lesbar fest. Geprueft wird
    ''' nur, ob unter <c>~/.Trash</c> ein Ordner mit dem Namen des Ordners liegt. Das ist eine
    ''' Schaetzung; der Systemschutz kann den Blick dorthin auch ganz verwehren, dann erscheint
    ''' der Ordner als nicht mehr vorhanden.</item>
    ''' </list>
    '''
    ''' Gelesen wird nur fuer Ordner, die auf der Platte fehlen und noch im Katalog stehen, und
    ''' der Stand gilt fuenf Sekunden (wie bei OfflineVolumes, das ihn mit verwirft).
    ''' </summary>
    Public NotInheritable Class TrashedFolders

        Private Sub New()
        End Sub

        Private Const SnapshotLifetimeMs As Long = 5000

        Private Shared ReadOnly _lock As New Object()
        Private Shared _snapshotTicks As Long
        Private Shared _infoNames As New Dictionary(Of String, String())(StringComparer.Ordinal)
        Private Shared _infoTargets As New Dictionary(Of String, String)(StringComparer.Ordinal)
        Private Shared _recycleTargets As New Dictionary(Of String, HashSet(Of String))(StringComparer.OrdinalIgnoreCase)
        Private Shared _testTrashRoots As List(Of String)
        Private Shared _userId As String

        ''' <summary>Liegt dieser Ordner, oder einer, unter dem er lag, im Papierkorb?</summary>
        Public Shared Function IsTrashed(folderPath As String) As Boolean
            If String.IsNullOrWhiteSpace(folderPath) OrElse folderPath.Contains("://", StringComparison.Ordinal) Then Return False
            Dim trimmed = folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            If trimmed.Length = 0 Then Return False
            Try
                SyncLock _lock
                    RefreshLocked()
                    If _testTrashRoots IsNot Nothing OrElse OperatingSystem.IsLinux() Then Return IsInFreedesktopTrashLocked(trimmed)
                    If OperatingSystem.IsWindows() Then Return IsInRecycleBinLocked(trimmed)
                    If OperatingSystem.IsMacOS() Then Return IsInMacTrash(trimmed)
                End SyncLock
            Catch ex As Exception
                DiagnosticLogService.LogException("TrashedFolders.IsTrashed", ex)
            End Try
            Return False
        End Function

        ''' <summary>Verwirft den gemerkten Stand; die naechste Frage liest neu.</summary>
        Public Shared Sub Invalidate()
            SyncLock _lock
                ClearLocked()
            End SyncLock
        End Sub

        ''' <summary>NUR fuer den Pruefstand: diese Papierkoerbe (je ein Ordner mit <c>info/</c>
        ''' darin, Pfade absolut) statt der echten, auf jedem System. Nothing stellt zurueck.</summary>
        Friend Shared Sub UseForTest(trashRoots As IEnumerable(Of String))
            SyncLock _lock
                _testTrashRoots = If(trashRoots Is Nothing, Nothing, trashRoots.ToList())
                ClearLocked()
            End SyncLock
        End Sub

        Private Shared Sub ClearLocked()
            _infoNames.Clear()
            _infoTargets.Clear()
            _recycleTargets.Clear()
            _snapshotTicks = Environment.TickCount64
        End Sub

        Private Shared Sub RefreshLocked()
            If Environment.TickCount64 - _snapshotTicks > SnapshotLifetimeMs Then ClearLocked()
        End Sub

        ' ---- Linux und Pruefstand: freedesktop ------------------------------------------------

        Private Shared Function IsInFreedesktopTrashLocked(folderPath As String) As Boolean
            ' Je Papierkorb die Wurzel, gegen die ein relativer Path= gilt ("" fuer absolut).
            Dim trashes As New List(Of (Root As String, Base As String))()
            If _testTrashRoots IsNot Nothing Then
                For Each root In _testTrashRoots
                    trashes.Add((root, ""))
                Next
            Else
                Dim dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME")
                If String.IsNullOrWhiteSpace(dataHome) Then
                    dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                End If
                trashes.Add((Path.Combine(dataHome, "Trash"), ""))
                Dim volumeRoot = OfflineVolumes.MountPointOf(folderPath)
                Dim uid = UserId()
                If volumeRoot.Length > 0 AndAlso uid.Length > 0 Then
                    trashes.Add((Path.Combine(volumeRoot, ".Trash", uid), volumeRoot))
                    trashes.Add((Path.Combine(volumeRoot, ".Trash-" & uid), volumeRoot))
                End If
            End If

            ' Der Ordner selbst oder einer seiner Elternordner kann der weggeworfene sein.
            Dim candidate = folderPath
            While Not String.IsNullOrEmpty(candidate)
                Dim name = Path.GetFileName(candidate)
                If String.IsNullOrEmpty(name) Then Exit While
                For Each trash In trashes
                    For Each infoName In InfoNamesLocked(trash.Root)
                        If Not infoName.StartsWith(name, StringComparison.Ordinal) Then Continue For
                        Dim original = InfoTargetLocked(Path.Combine(trash.Root, "info", infoName), trash.Base)
                        If original.Length > 0 AndAlso String.Equals(original, candidate, PathIdentity.Comparison) Then Return True
                    Next
                Next
                candidate = Path.GetDirectoryName(candidate)
            End While
            Return False
        End Function

        Private Shared Function InfoNamesLocked(trashRoot As String) As String()
            Dim names As String() = Nothing
            If _infoNames.TryGetValue(trashRoot, names) Then Return names
            names = Array.Empty(Of String)()
            Try
                Dim infoDir = Path.Combine(trashRoot, "info")
                If Directory.Exists(infoDir) Then
                    names = Directory.EnumerateFiles(infoDir, "*.trashinfo").
                        Select(Function(f) Path.GetFileName(f)).ToArray()
                End If
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            _infoNames(trashRoot) = names
            Return names
        End Function

        ''' <summary>Der urspruengliche Ort aus einer <c>.trashinfo</c>, "" wenn keiner lesbar ist.
        ''' Der Wert ist URL-kodiert; ein relativer gilt gegen die Wurzel des Datentraegers.</summary>
        Private Shared Function InfoTargetLocked(infoFile As String, baseDir As String) As String
            Dim target As String = Nothing
            If _infoTargets.TryGetValue(infoFile, target) Then Return target
            target = ""
            Try
                For Each line In File.ReadLines(infoFile)
                    If Not line.StartsWith("Path=", StringComparison.Ordinal) Then Continue For
                    Dim value = Uri.UnescapeDataString(line.Substring(5).Trim())
                    If value.Length = 0 Then Exit For
                    If Not value.StartsWith("/"c) Then
                        If baseDir.Length = 0 Then Exit For
                        value = Path.Combine(baseDir, value)
                    End If
                    target = value.TrimEnd("/"c)
                    Exit For
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            _infoTargets(infoFile) = target
            Return target
        End Function

        ''' <summary>Die Benutzernummer fuer die Papierkoerbe der Datentraeger, aus
        ''' <c>/proc/self/status</c>.</summary>
        Private Shared Function UserId() As String
            If _userId IsNot Nothing Then Return _userId
            _userId = ""
            Try
                For Each line In File.ReadLines("/proc/self/status")
                    If Not line.StartsWith("Uid:", StringComparison.Ordinal) Then Continue For
                    Dim fields = line.Substring(4).Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                    If fields.Length > 0 Then _userId = fields(0)
                    Exit For
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            Return _userId
        End Function

        ' ---- Windows: $Recycle.Bin ------------------------------------------------------------

        Private Shared Function IsInRecycleBinLocked(folderPath As String) As Boolean
            Dim driveRoot = Path.GetPathRoot(folderPath)
            If String.IsNullOrEmpty(driveRoot) Then Return False
            Dim targets As HashSet(Of String) = Nothing
            If Not _recycleTargets.TryGetValue(driveRoot, targets) Then
                targets = ReadRecycleBin(driveRoot)
                _recycleTargets(driveRoot) = targets
            End If
            If targets.Count = 0 Then Return False
            Dim candidate = folderPath
            While Not String.IsNullOrEmpty(candidate)
                If targets.Contains(candidate.TrimEnd(Path.DirectorySeparatorChar)) Then Return True
                candidate = Path.GetDirectoryName(candidate)
            End While
            Return False
        End Function

        ''' <summary>Alle urspruenglichen Pfade im Papierkorb eines Laufwerks. Die Ordner der anderen
        ''' Benutzer sind nicht lesbar und fallen still weg.</summary>
        Private Shared Function ReadRecycleBin(driveRoot As String) As HashSet(Of String)
            Dim result As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim bin = Path.Combine(driveRoot, "$Recycle.Bin")
            Try
                If Not Directory.Exists(bin) Then Return result
                For Each userBin In Directory.EnumerateDirectories(bin)
                    Try
                        For Each entry In Directory.EnumerateFiles(userBin, "$I*")
                            Dim original = ParseRecycleInfo(entry)
                            If original.Length > 0 Then result.Add(original.TrimEnd(Path.DirectorySeparatorChar))
                        Next
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    End Try
                Next
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
            End Try
            Return result
        End Function

        ''' <summary>Der urspruengliche Pfad aus einer <c>$I</c>-Datei. Fassung 1: ab Byte 24 fest
        ''' 520 Byte UTF-16. Fassung 2: ab Byte 24 die Zeichenzahl, ab Byte 28 der Pfad.</summary>
        Friend Shared Function ParseRecycleInfo(infoFile As String) As String
            Try
                Dim bytes = File.ReadAllBytes(infoFile)
                If bytes.Length < 28 Then Return ""
                Dim version = BitConverter.ToInt64(bytes, 0)
                Dim text As String
                If version = 1 Then
                    text = Encoding.Unicode.GetString(bytes, 24, Math.Min(520, bytes.Length - 24))
                ElseIf version = 2 Then
                    Dim chars = BitConverter.ToInt32(bytes, 24)
                    If chars <= 0 Then Return ""
                    text = Encoding.Unicode.GetString(bytes, 28, Math.Min(chars * 2, bytes.Length - 28))
                Else
                    Return ""
                End If
                Dim terminator = text.IndexOf(ChrW(0))
                Return If(terminator >= 0, text.Substring(0, terminator), text)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return ""
            End Try
        End Function

        ' ---- macOS ----------------------------------------------------------------------------

        ''' <summary>Nur der Ordner selbst, nicht seine Elternordner: ueber den Namen allein traefe
        ''' die Schaetzung sonst jeden Ordner, dessen Elternordner zufaellig so heisst wie etwas im
        ''' Papierkorb.</summary>
        Private Shared Function IsInMacTrash(folderPath As String) As Boolean
            Dim name = Path.GetFileName(folderPath)
            If String.IsNullOrEmpty(name) Then Return False
            Dim trash = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".Trash")
            Try
                Return Directory.Exists(Path.Combine(trash, name))
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                Return False
            End Try
        End Function

    End Class

End Namespace
