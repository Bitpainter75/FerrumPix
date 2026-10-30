Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Threading
Imports ReactiveUI

Namespace Services

    ''' <summary>Eine Quelle zum Einlesen: ein Wechseldatentraeger (Speicherkarte, Kamera als
    ''' Laufwerk) oder ein beliebiger Ordner.</summary>
    Public Class ImportSource
        Public Property Path As String = ""
        Public Property Label As String = ""

        Public Overrides Function ToString() As String
            Return Label
        End Function
    End Class

    ''' <summary>Eine Datei der Quelle mit allem, was die Planung braucht.</summary>
    Public Class ImportCandidate
        Public Property SourcePath As String = ""
        Public Property Size As Long
        Public Property CapturedAt As DateTime
        Public Property IsRaw As Boolean
        ''' <summary>Das JPEG oder HEIF neben einer RAW mit demselben Namen im selben Ordner.</summary>
        Public Property IsRawPartner As Boolean
    End Class

    ''' <summary>Ein Aufnahmetag der Quelle; im Dialog an- und abwaehlbar.</summary>
    Public Class ImportDay
        Inherits ReactiveObject

        Public Property Day As Date
        Public Property Count As Integer
        Public Property Bytes As Long
        Public Property OnSelectionChanged As Action

        Private _isSelected As Boolean = True
        Public Property IsSelected As Boolean
            Get
                Return _isSelected
            End Get
            Set(value As Boolean)
                If _isSelected = value Then Return
                Me.RaiseAndSetIfChanged(_isSelected, value)
                OnSelectionChanged?.Invoke()
            End Set
        End Property

        Public ReadOnly Property Label As String
            Get
                Return String.Format(CultureInfo.CurrentCulture, LocalizationService.T("{0}: {1} Dateien, {2}"),
                                     Day.ToString("D", CultureInfo.CurrentUICulture), Count, CardImportService.FormatBytes(Bytes))
            End Get
        End Property
    End Class

    ''' <summary>Eine Wahl fuer die Unterordner im Ziel.</summary>
    Public Class ImportFolderPattern
        Public Property Pattern As String = ""
        Public Property Label As String = ""

        Public Overrides Function ToString() As String
            Return Label
        End Function
    End Class

    ''' <summary>Was beim Einlesen herauskam.</summary>
    Public Class ImportResult
        Public Property Copied As Integer
        ''' <summary>Lag schon bytegleich im Ziel und wurde nicht noch einmal kopiert.</summary>
        Public Property AlreadyPresent As Integer
        Public Property Failed As New List(Of String)()
        Public Property Cancelled As Boolean
        ''' <summary>Der Ordner der ersten kopierten (oder vorhandenen) Datei, zum Hinspringen.</summary>
        Public Property FirstTargetFolder As String = ""
    End Class

    ''' <summary>
    ''' Liest Bilder von einer Speicherkarte oder einem Ordner in den Bestand ein.
    '''
    ''' DIE ZUSAGEN, in dieser Reihenfolge:
    ''' <list type="number">
    ''' <item>Die Quelle wird nur gelesen. Es wird dort nichts geloescht, umbenannt oder
    ''' angelegt.</item>
    ''' <item>Im Ziel wird NIE eine vorhandene Datei ersetzt. Liegt dort schon dieselbe Datei
    ''' (gleiche Groesse und gleiche Pruefsumme), zaehlt sie als vorhanden; liegt dort eine andere mit
    ''' demselben Namen, bekommt die neue einen Zusatz (_1, _2, ...).</item>
    ''' <item>Unter ihrem Namen erscheint eine Datei erst, wenn sie vollstaendig und geprueft ist.
    ''' Kopiert wird in eine versteckte Zwischendatei daneben, auf die Platte gezwungen, dann noch
    ''' einmal gelesen und mit der Pruefsumme (SHA-256) der Quelle verglichen, die beim Lesen
    ''' gerechnet wurde. Erst danach bekommt sie ihren Namen, und zwar ohne Ueberschreiben. Bricht der
    ''' Lauf ab, bleibt hoechstens eine Zwischendatei liegen, nie eine halbe Datei unter echtem
    ''' Namen; uebrig gebliebene raeumt der naechste Lauf in den Zielordnern weg.</item>
    ''' <item>Vor dem Kopieren wird der freie Platz geprueft.</item>
    ''' </list>
    '''
    ''' Die Aenderungszeit der Kopie ist die der Quelle, damit Sortierung und Katalog die Aufnahme
    ''' so sehen wie auf der Karte. Die Aufnahmezeit fuer die Unterordner kommt aus den EXIF-Daten,
    ''' ersatzweise aus der Aenderungszeit.
    ''' </summary>
    Public NotInheritable Class CardImportService

        ''' Endung der Zwischendateien; der Name beginnt mit einem Punkt und ist damit versteckt.
        Private Const PartSuffix As String = ".ferrumpix-import"

        ''' Zwischendateien, die aelter sind, gelten als Rest eines abgebrochenen Laufs.
        Private Shared ReadOnly StalePartAge As TimeSpan = TimeSpan.FromHours(1)

        Private Sub New()
        End Sub

        ''' <summary>Die Wahlen fuer die Unterordner im Ziel. Das Muster ist ein Datumsformat, der
        ''' Schraegstrich trennt Ordnerebenen.</summary>
        Public Shared Function FolderPatterns() As List(Of ImportFolderPattern)
            Return New List(Of ImportFolderPattern) From {
                New ImportFolderPattern With {.Pattern = "yyyy/yyyy-MM-dd", .Label = LocalizationService.T("Jahr und Tag, etwa 2026/2026-10-30")},
                New ImportFolderPattern With {.Pattern = "yyyy-MM-dd", .Label = LocalizationService.T("Tag, etwa 2026-10-30")},
                New ImportFolderPattern With {.Pattern = "yyyy/MM", .Label = LocalizationService.T("Jahr und Monat, etwa 2026/10")},
                New ImportFolderPattern With {.Pattern = "", .Label = LocalizationService.T("Ohne Unterordner")}
            }
        End Function

        ''' <summary>Eingehaengte Wechseldatentraeger. Unter Linux die Einhaengeorte unter /run/media
        ''' und /media des Benutzers, unter macOS /Volumes ohne das Systemlaufwerk, unter Windows die
        ''' Laufwerke vom Typ "Wechseldatentraeger". Datentraeger mit einem DCIM-Ordner stehen vorn und
        ''' zeigen gleich auf ihn.</summary>
        Public Shared Function DetectSources() As List(Of ImportSource)
            Dim roots As New List(Of String)()
            Try
                If OperatingSystem.IsWindows() Then
                    For Each drive In DriveInfo.GetDrives()
                        If drive.DriveType = DriveType.Removable AndAlso drive.IsReady Then roots.Add(drive.RootDirectory.FullName)
                    Next
                ElseIf OperatingSystem.IsMacOS() Then
                    If Directory.Exists("/Volumes") Then
                        For Each volume In Directory.GetDirectories("/Volumes")
                            If Not IsMacSystemVolume(volume) Then roots.Add(volume)
                        Next
                    End If
                Else
                    Dim user = Environment.UserName
                    For Each parent In {"/run/media/" & user, "/media/" & user, "/media"}
                        If Not Directory.Exists(parent) Then Continue For
                        For Each volume In Directory.GetDirectories(parent)
                            If String.Equals(volume, "/media/" & user, StringComparison.Ordinal) Then Continue For
                            If Not roots.Contains(volume) Then roots.Add(volume)
                        Next
                    Next
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Import.DetectSources", ex)
            End Try

            Dim result As New List(Of ImportSource)()
            For Each root In roots
                Dim dcim = Path.Combine(root, "DCIM")
                Dim hasDcim = Directory.Exists(dcim)
                Dim name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))
                If String.IsNullOrEmpty(name) Then name = root
                result.Add(New ImportSource With {
                    .Path = If(hasDcim, dcim, root),
                    .Label = If(hasDcim, name & "  (DCIM)", name)})
            Next
            Return result.OrderByDescending(Function(s) s.Path.EndsWith("DCIM", StringComparison.OrdinalIgnoreCase)).ToList()
        End Function

        Private Shared Function IsMacSystemVolume(volume As String) As Boolean
            Try
                Dim info = New DirectoryInfo(volume)
                ' Das Startlaufwerk steht als Verweis auf "/" in /Volumes.
                If info.LinkTarget IsNot Nothing AndAlso info.LinkTarget = "/" Then Return True
                Return String.Equals(info.Name, "Macintosh HD", StringComparison.OrdinalIgnoreCase)
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Alle Medien unter <paramref name="root"/>, rekursiv, ohne versteckte Ordner.
        ''' Liest je Datei nur die Aufnahmedaten.</summary>
        Public Shared Function Scan(root As String, token As CancellationToken) As List(Of ImportCandidate)
            Dim result As New List(Of ImportCandidate)()
            If String.IsNullOrWhiteSpace(root) OrElse Not Directory.Exists(root) Then Return result
            Dim pending As New Stack(Of String)()
            pending.Push(root)
            While pending.Count > 0
                token.ThrowIfCancellationRequested()
                Dim folder = pending.Pop()
                Try
                    For Each subfolder In Directory.EnumerateDirectories(folder)
                        If Path.GetFileName(subfolder).StartsWith("."c) Then Continue For
                        pending.Push(subfolder)
                    Next
                    For Each file In Directory.EnumerateFiles(folder)
                        token.ThrowIfCancellationRequested()
                        If Path.GetFileName(file).StartsWith("."c) OrElse Not MediaFileTypes.IsDisplayable(file) Then Continue For
                        Dim info As New FileInfo(file)
                        result.Add(New ImportCandidate With {
                            .SourcePath = file,
                            .Size = info.Length,
                            .CapturedAt = CaptureTimeOf(file, info),
                            .IsRaw = RawPreviewService.IsSupportedRaw(file)})
                    Next
                Catch ex As OperationCanceledException
                    Throw
                Catch ex As Exception
                    DiagnosticLogService.LogException("Import.Scan", ex)
                End Try
            End While

            ' Die Begleitdatei einer RAW: gleicher Ordner, gleicher Name, JPEG oder HEIF.
            Dim rawStems As New HashSet(Of String)(PathIdentity.Comparer)
            For Each candidate In result.Where(Function(c) c.IsRaw)
                rawStems.Add(StemOf(candidate.SourcePath))
            Next
            For Each candidate In result
                If candidate.IsRaw Then Continue For
                Dim ext = Path.GetExtension(candidate.SourcePath).ToLowerInvariant()
                If (ext = ".jpg" OrElse ext = ".jpeg" OrElse ext = ".heic" OrElse ext = ".heif" OrElse ext = ".hif") AndAlso
                   rawStems.Contains(StemOf(candidate.SourcePath)) Then candidate.IsRawPartner = True
            Next
            Return result.OrderBy(Function(c) c.CapturedAt).ThenBy(Function(c) c.SourcePath, StringComparer.Ordinal).ToList()
        End Function

        Private Shared Function StemOf(filePath As String) As String
            Return Path.Combine(If(Path.GetDirectoryName(filePath), ""), Path.GetFileNameWithoutExtension(filePath))
        End Function

        Private Shared Function CaptureTimeOf(file As String, info As FileInfo) As DateTime
            Try
                Dim taken = ExifService.ParseExifDateTime(ExifService.ReadExif(file)?.DateTaken)
                If taken.HasValue Then Return taken.Value
            Catch
            End Try
            Return info.LastWriteTime
        End Function

        ''' <summary>Der Zielordner einer Aufnahme nach dem Muster.</summary>
        Public Shared Function TargetFolderFor(targetRoot As String, pattern As String, capturedAt As DateTime) As String
            If String.IsNullOrWhiteSpace(pattern) Then Return targetRoot
            Dim parts = pattern.Split("/"c).Where(Function(p) p.Length > 0).
                Select(Function(p) capturedAt.ToString(p, CultureInfo.InvariantCulture)).ToArray()
            Return Path.Combine({targetRoot}.Concat(parts).ToArray())
        End Function

        ''' <summary>Wie viele der Dateien schon im Ziel zu liegen scheinen (gleicher Name, gleiche
        ''' Groesse). Nur fuer den Hinweis im Dialog; das Einlesen prueft es genau.</summary>
        Public Shared Function CountProbablyPresent(candidates As IEnumerable(Of ImportCandidate), targetRoot As String, pattern As String) As Integer
            If String.IsNullOrWhiteSpace(targetRoot) Then Return 0
            Dim count = 0
            For Each candidate In candidates
                Try
                    Dim target = Path.Combine(TargetFolderFor(targetRoot, pattern, candidate.CapturedAt), Path.GetFileName(candidate.SourcePath))
                    Dim info As New FileInfo(target)
                    If info.Exists AndAlso info.Length = candidate.Size Then count += 1
                Catch
                End Try
            Next
            Return count
        End Function

        ''' <summary>Freier Platz am Ziel in Byte, oder -1, wenn er sich nicht bestimmen laesst.</summary>
        Public Shared Function FreeSpace(targetRoot As String) As Long
            Try
                Dim existing = targetRoot
                While Not String.IsNullOrEmpty(existing) AndAlso Not Directory.Exists(existing)
                    existing = Path.GetDirectoryName(existing)
                End While
                If String.IsNullOrEmpty(existing) Then Return -1
                Return New DriveInfo(existing).AvailableFreeSpace
            Catch
                Return -1
            End Try
        End Function

        ''' <summary>Liest die Dateien ein, siehe Klassenbeschreibung. <paramref name="backupRoot"/>
        ''' leer heisst: kein zweites Ziel. Meldet nach jeder Datei den Stand.</summary>
        Public Shared Function Run(candidates As IList(Of ImportCandidate), targetRoot As String, pattern As String,
                                   backupRoot As String, token As CancellationToken,
                                   progress As Action(Of Integer, Integer)) As ImportResult
            Dim result As New ImportResult()
            Dim roots = {targetRoot, backupRoot}.Where(Function(r) Not String.IsNullOrWhiteSpace(r)).ToList()
            Dim cleaned As New HashSet(Of String)(PathIdentity.Comparer)
            For index = 0 To candidates.Count - 1
                If token.IsCancellationRequested Then
                    result.Cancelled = True
                    Exit For
                End If
                Dim candidate = candidates(index)
                Dim ok = True
                Dim present = False
                For rootIndex = 0 To roots.Count - 1
                    Dim folder = TargetFolderFor(roots(rootIndex), pattern, candidate.CapturedAt)
                    Try
                        Directory.CreateDirectory(folder)
                        If cleaned.Add(folder) Then RemoveStaleParts(folder)
                        Dim outcome = CopyVerified(candidate.SourcePath, folder, token)
                        ' Vorhanden zaehlt nur am Hauptziel; die Sicherung darf es schon haben.
                        If rootIndex = 0 AndAlso outcome.Present Then present = True
                        If rootIndex = 0 AndAlso String.IsNullOrEmpty(result.FirstTargetFolder) Then result.FirstTargetFolder = folder
                    Catch ex As OperationCanceledException
                        result.Cancelled = True
                        ok = False
                        Exit For
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Import.Copy", ex)
                        ok = False
                    End Try
                Next
                If result.Cancelled Then Exit For
                If Not ok Then
                    result.Failed.Add(Path.GetFileName(candidate.SourcePath))
                ElseIf present Then
                    result.AlreadyPresent += 1
                Else
                    result.Copied += 1
                End If
                progress?.Invoke(index + 1, candidates.Count)
            Next
            Return result
        End Function

        ''' <summary>Eine Datei in einen Ordner, geprueft und ohne Ersetzen. Present = True: dieselbe
        ''' Datei lag schon dort.</summary>
        Private Shared Function CopyVerified(source As String, folder As String, token As CancellationToken) As (Present As Boolean, FinalPath As String)
            Dim fileName = Path.GetFileName(source)
            Dim partPath = Path.Combine(folder, "." & fileName & "." & DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) &
                                                "." & Guid.NewGuid().ToString("N").Substring(0, 8) & PartSuffix)
            Dim sourceHash As Byte()
            Dim sourceInfo As New FileInfo(source)
            Try
                Using input As New FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan),
                      output As New FileStream(partPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024),
                      hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                    Dim buffer(1024 * 1024 - 1) As Byte
                    While True
                        token.ThrowIfCancellationRequested()
                        Dim read = input.Read(buffer, 0, buffer.Length)
                        If read <= 0 Then Exit While
                        hasher.AppendData(buffer, 0, read)
                        output.Write(buffer, 0, read)
                    End While
                    output.Flush(flushToDisk:=True)
                    sourceHash = hasher.GetHashAndReset()
                End Using
                If Not HashOf(partPath, token).AsSpan().SequenceEqual(sourceHash) Then
                    Throw New IOException("Die Kopie weicht von der Quelle ab: " & fileName)
                End If
                File.SetLastWriteTimeUtc(partPath, sourceInfo.LastWriteTimeUtc)

                ' Unter dem Namen veroeffentlichen, ohne Ersetzen. Liegt dort schon dieselbe Datei,
                ' ist die Kopie ueberfluessig; liegt dort eine andere, kommt ein Zusatz dran.
                Dim stem = Path.GetFileNameWithoutExtension(fileName)
                Dim ext = Path.GetExtension(fileName)
                For attempt = 0 To 999
                    Dim candidateName = If(attempt = 0, fileName, stem & "_" & attempt.ToString(CultureInfo.InvariantCulture) & ext)
                    Dim finalPath = Path.Combine(folder, candidateName)
                    If File.Exists(finalPath) Then
                        If New FileInfo(finalPath).Length = sourceInfo.Length AndAlso
                           HashOf(finalPath, token).AsSpan().SequenceEqual(sourceHash) Then
                            File.Delete(partPath)
                            Return (True, finalPath)
                        End If
                        Continue For
                    End If
                    Try
                        File.Move(partPath, finalPath, overwrite:=False)
                        Return (False, finalPath)
                    Catch ex As IOException When File.Exists(finalPath)
                        ' Dazwischen angelegt: der naechste Name.
                    End Try
                Next
                Throw New IOException("Kein freier Name fuer " & fileName)
            Catch
                Try
                    If File.Exists(partPath) Then File.Delete(partPath)
                Catch
                End Try
                Throw
            End Try
        End Function

        Private Shared Function HashOf(filePath As String, token As CancellationToken) As Byte()
            Using stream As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan),
                  hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                Dim buffer(1024 * 1024 - 1) As Byte
                While True
                    token.ThrowIfCancellationRequested()
                    Dim read = stream.Read(buffer, 0, buffer.Length)
                    If read <= 0 Then Exit While
                    hasher.AppendData(buffer, 0, read)
                End While
                Return hasher.GetHashAndReset()
            End Using
        End Function

        ''' <summary>Raeumt Zwischendateien abgebrochener Laeufe aus einem Zielordner, nicht rekursiv.
        ''' Das Alter steht im Namen (Sekunden seit 1970): die Aenderungszeit taugt dafuer nicht, weil
        ''' sie auf die der Quelle gesetzt wird, und exFAT kennt keine echte Erstellzeit.</summary>
        Private Shared Sub RemoveStaleParts(folder As String)
            Try
                For Each part In Directory.EnumerateFiles(folder, ".*" & PartSuffix)
                    Dim name = Path.GetFileName(part)
                    Dim pieces = name.Substring(0, name.Length - PartSuffix.Length).Split("."c)
                    If pieces.Length < 3 Then Continue For
                    Dim seconds As Long
                    If Not Long.TryParse(pieces(pieces.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, seconds) Then Continue For
                    If DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds) > StalePartAge Then File.Delete(part)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Import.RemoveStaleParts", ex)
            End Try
        End Sub

        Public Shared Function FormatBytes(bytes As Long) As String
            Dim culture = CultureInfo.CurrentCulture
            If bytes >= 1024L * 1024 * 1024 Then Return (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", culture) & " GB"
            If bytes >= 1024L * 1024 Then Return (bytes / (1024.0 * 1024)).ToString("0", culture) & " MB"
            Return (bytes / 1024.0).ToString("0", culture) & " KB"
        End Function

    End Class

End Namespace
