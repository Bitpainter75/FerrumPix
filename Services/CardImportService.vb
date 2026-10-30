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
        ''' <summary>Kamera aus den EXIF-Daten, fuer den Platzhalter {camera}.</summary>
        Public Property Camera As String = ""
        ''' <summary>Aenderungszeit der Datei, fuer den Platzhalter {date:...}.</summary>
        Public Property ModifiedAt As DateTime
        ''' <summary>Liegt die Datei auf einem MTP-Geraet hinter kmtpd, dessen Speicher; sonst
        ''' Nothing. <see cref="SourcePath"/> ist dann die Adresse "mtp:/...".</summary>
        Public Property MtpStorage As MtpStorage
        ''' <summary>Der Pfad auf dem Speicher ("/DCIM/Camera/IMG_0001.jpg").</summary>
        Public Property MtpPath As String = ""
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

    ''' <summary>Ein Ziel zur Auswahl im Import-Dialog: Anzeige und Vorlage.</summary>
    Public Class ImportTargetChoice
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

        ''' <summary>Eingehaengte Wechseldatentraeger und angeschlossene Geraete. Unter Linux die
        ''' Einhaengeorte unter /run/media und /media des Benutzers, dazu die Telefone und Kameras, die
        ''' gvfs einhaengt (MTP und PTP), und die Speicher der Geraete, die kmtpd unter KDE haelt
        ''' (<see cref="MtpSession"/>). Unter macOS /Volumes ohne das Systemlaufwerk, unter Windows die
        ''' Laufwerke vom Typ "Wechseldatentraeger". Datentraeger mit einem DCIM-Ordner stehen vorn und
        ''' zeigen gleich auf ihn. Ein traeges Geraet braucht dafuer Sekunden, der Aufruf gehoert also
        ''' nicht auf den UI-Faden.</summary>
        Public Shared Function DetectSources() As List(Of ImportSource)
            Dim roots As New List(Of (Path As String, Name As String))()
            Try
                If OperatingSystem.IsWindows() Then
                    For Each drive In DriveInfo.GetDrives()
                        If drive.DriveType = DriveType.Removable AndAlso drive.IsReady Then roots.Add((drive.RootDirectory.FullName, ""))
                    Next
                ElseIf OperatingSystem.IsMacOS() Then
                    If Directory.Exists("/Volumes") Then
                        For Each volume In Directory.GetDirectories("/Volumes")
                            If Not IsMacSystemVolume(volume) Then roots.Add((volume, ""))
                        Next
                    End If
                Else
                    Dim user = Environment.UserName
                    For Each parent In {"/run/media/" & user, "/media/" & user, "/media"}
                        If Not Directory.Exists(parent) Then Continue For
                        For Each volume In Directory.GetDirectories(parent)
                            If String.Equals(volume, "/media/" & user, StringComparison.Ordinal) Then Continue For
                            If Not roots.Any(Function(r) r.Path = volume) Then roots.Add((volume, ""))
                        Next
                    Next
                    roots.AddRange(GvfsDeviceRoots(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")))
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Import.DetectSources", ex)
            End Try

            Dim result As New List(Of ImportSource)()
            For Each root In roots
                Dim dcim = Path.Combine(root.Path, "DCIM")
                Dim hasDcim = Directory.Exists(dcim)
                Dim name = root.Name
                If String.IsNullOrEmpty(name) Then name = Path.GetFileName(root.Path.TrimEnd(Path.DirectorySeparatorChar))
                If String.IsNullOrEmpty(name) Then name = root.Path
                result.Add(New ImportSource With {
                    .Path = If(hasDcim, dcim, root.Path),
                    .Label = If(hasDcim, name & "  (DCIM)", name)})
            Next
            If OperatingSystem.IsLinux() Then result.AddRange(MtpSources())
            Return result.OrderByDescending(Function(s) s.Path.EndsWith("DCIM", StringComparison.OrdinalIgnoreCase)).ToList()
        End Function

        ''' <summary>Was gvfs unter <c>$XDG_RUNTIME_DIR/gvfs</c> an Geraeten einhaengt. Ein MTP-Geraet
        ''' ("mtp:host=...") traegt seine Speicher als Unterordner, und jeder Speicher wird eine eigene
        ''' Quelle; eine Kamera ueber PTP ("gphoto2:host=...") ist selbst die Quelle. Der Name kommt aus
        ''' dem Teil nach "host=", mit Leerzeichen statt Unterstrichen.</summary>
        Public Shared Function GvfsDeviceRoots(runtimeDir As String) As List(Of (Path As String, Name As String))
            Dim result As New List(Of (Path As String, Name As String))()
            If String.IsNullOrEmpty(runtimeDir) Then Return result
            Dim gvfs = Path.Combine(runtimeDir, "gvfs")
            Try
                If Not Directory.Exists(gvfs) Then Return result
                For Each mount In Directory.GetDirectories(gvfs).OrderBy(Function(d) d, StringComparer.Ordinal)
                    Dim mountName = Path.GetFileName(mount)
                    Dim isMtp = mountName.StartsWith("mtp:", StringComparison.OrdinalIgnoreCase)
                    Dim isPtp = mountName.StartsWith("gphoto2:", StringComparison.OrdinalIgnoreCase)
                    If Not isMtp AndAlso Not isPtp Then Continue For
                    Dim host = mountName.IndexOf("host=", StringComparison.OrdinalIgnoreCase)
                    Dim deviceName = If(host >= 0, mountName.Substring(host + 5), mountName).Replace("_"c, " "c).Trim()
                    If isPtp Then
                        result.Add((mount, deviceName))
                        Continue For
                    End If
                    For Each storage In Directory.GetDirectories(mount).OrderBy(Function(d) d, StringComparer.Ordinal)
                        result.Add((storage, deviceName & " / " & Path.GetFileName(storage)))
                    Next
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Import.GvfsDevices", ex)
            End Try
            Return result
        End Function

        ''' <summary>Die Speicher der Geraete, die kmtpd haelt, je als Quelle "mtp:/Geraet/Speicher",
        ''' mit DCIM, wenn es den Ordner gibt. Nach DCIM wird nur mit dem kurzen Zeitlimit gefragt;
        ''' antwortet ein Speicher nicht rechtzeitig, steht er ohne DCIM da.</summary>
        Private Shared Function MtpSources() As List(Of ImportSource)
            Dim result As New List(Of ImportSource)()
            Try
                Using session = MtpSession.TryOpen()
                    If session Is Nothing Then Return result
                    For Each storage In session.ListStorages()
                        Dim top As List(Of MtpEntry) = Nothing
                        Try
                            top = session.ListFolder(storage, "/", quick:=True)
                        Catch ex As Exception
                            DiagnosticLogService.LogException("Import.MtpSources", ex)
                        End Try
                        Dim hasDcim = top IsNot Nothing AndAlso top.Any(Function(e) e.IsFolder AndAlso e.Name = "DCIM")
                        Dim label = storage.DeviceName & " / " & storage.StorageName
                        result.Add(New ImportSource With {
                            .Path = If(hasDcim, storage.Address & "/DCIM", storage.Address),
                            .Label = If(hasDcim, label & "  (DCIM)", label)})
                    Next
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Import.MtpSources", ex)
            End Try
            Return result
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

        ''' <summary>Alle Medien unter <paramref name="root"/>, ohne versteckte Ordner; mit
        ''' <paramref name="includeSubfolders"/> auch in allen Unterordnern (ab Werk, eine Karte legt ihre
        ''' Bilder in DCIM/100XXXXX ab). Liest je Datei nur die Aufnahmedaten.</summary>
        Public Shared Function Scan(root As String, token As CancellationToken, Optional includeSubfolders As Boolean = True) As List(Of ImportCandidate)
            If MtpSession.IsAddress(root) Then Return ScanMtp(root, token, includeSubfolders)
            Dim result As New List(Of ImportCandidate)()
            If String.IsNullOrWhiteSpace(root) OrElse Not Directory.Exists(root) Then Return result
            Dim pending As New Stack(Of String)()
            pending.Push(root)
            While pending.Count > 0
                token.ThrowIfCancellationRequested()
                Dim folder = pending.Pop()
                Try
                    If includeSubfolders Then
                        For Each subfolder In Directory.EnumerateDirectories(folder)
                            If Path.GetFileName(subfolder).StartsWith("."c) Then Continue For
                            pending.Push(subfolder)
                        Next
                    End If
                    For Each file In Directory.EnumerateFiles(folder)
                        token.ThrowIfCancellationRequested()
                        If Path.GetFileName(file).StartsWith("."c) OrElse Not MediaFileTypes.IsDisplayable(file) Then Continue For
                        Dim info As New FileInfo(file)
                        Dim camera = ""
                        result.Add(New ImportCandidate With {
                            .SourcePath = file,
                            .Size = info.Length,
                            .CapturedAt = CaptureTimeOf(file, info, camera),
                            .Camera = camera,
                            .ModifiedAt = info.LastWriteTime,
                            .IsRaw = RawPreviewService.IsSupportedRaw(file)})
                    Next
                Catch ex As OperationCanceledException
                    Throw
                Catch ex As Exception
                    DiagnosticLogService.LogException("Import.Scan", ex)
                End Try
            End While
            Return Ordered(MarkRawPartners(result))
        End Function

        ''' <summary>Wie <see cref="Scan"/>, aber auf einem Geraet hinter kmtpd. Gelesen wird nur die
        ''' Ordnerliste: Name, Groesse und Aenderungszeit, ohne die Dateien selbst. Die Aufnahmezeit
        ''' ist hier deshalb die Aenderungszeit, die ein Telefon beim Fotografieren setzt; die EXIF-Daten
        ''' liest erst der Lauf aus der geholten Datei (siehe <see cref="Run"/>).</summary>
        Private Shared Function ScanMtp(address As String, token As CancellationToken, includeSubfolders As Boolean) As List(Of ImportCandidate)
            Dim result As New List(Of ImportCandidate)()
            Using session = MtpSession.TryOpen()
                If session Is Nothing Then Return result
                Dim storage As MtpStorage = Nothing
                Dim rootPath = ""
                If Not session.TryResolve(address, storage, rootPath) Then Return result
                Dim pending As New Stack(Of String)()
                pending.Push(rootPath)
                While pending.Count > 0
                    token.ThrowIfCancellationRequested()
                    Dim folder = pending.Pop()
                    Dim entries As List(Of MtpEntry)
                    Try
                        entries = session.ListFolder(storage, folder)
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Import.ScanMtp", ex)
                        Continue While
                    End Try
                    If entries Is Nothing Then Continue While
                    For Each entry In entries
                        If entry.Name.StartsWith("."c) Then Continue For
                        Dim entryPath = folder.TrimEnd("/"c) & "/" & entry.Name
                        If entry.IsFolder Then
                            If includeSubfolders Then pending.Push(entryPath)
                            Continue For
                        End If
                        If Not MediaFileTypes.IsDisplayable(entry.Name) Then Continue For
                        result.Add(New ImportCandidate With {
                            .SourcePath = storage.Address & entryPath,
                            .Size = entry.Size,
                            .CapturedAt = entry.ModifiedAt,
                            .ModifiedAt = entry.ModifiedAt,
                            .IsRaw = RawPreviewService.IsSupportedRaw(entry.Name),
                            .MtpStorage = storage,
                            .MtpPath = entryPath})
                    Next
                End While
            End Using
            Return Ordered(MarkRawPartners(result))
        End Function

        Private Shared Function Ordered(candidates As List(Of ImportCandidate)) As List(Of ImportCandidate)
            Return candidates.OrderBy(Function(c) c.CapturedAt).ThenBy(Function(c) c.SourcePath, StringComparer.Ordinal).ToList()
        End Function

        ''' <summary>Die Begleitdatei einer RAW: gleicher Ordner, gleicher Name, JPEG oder HEIF.</summary>
        Private Shared Function MarkRawPartners(result As List(Of ImportCandidate)) As List(Of ImportCandidate)
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
            Return result
        End Function

        Private Shared Function StemOf(filePath As String) As String
            Return Path.Combine(If(Path.GetDirectoryName(filePath), ""), Path.GetFileNameWithoutExtension(filePath))
        End Function

        Private Shared Function CaptureTimeOf(file As String, info As FileInfo, ByRef camera As String) As DateTime
            camera = ""
            Try
                Dim exif = ExifService.ReadExif(file)
                camera = If(exif?.Camera, "").Trim()
                Dim taken = ExifService.ParseExifDateTime(exif?.DateTaken)
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

        ''' <summary>Der Zielordner einer Aufnahme. Traegt das Ziel Platzhalter, legt die Vorlage den
        ''' ganzen Weg fest und das Muster der Unterordner gilt nicht; ein schlichter Ordner bekommt
        ''' die Unterordner nach dem Muster.</summary>
        Public Shared Function TargetFolderFor(target As String, pattern As String, candidate As ImportCandidate) As String
            If HasPlaceholders(target) Then Return ExpandTargetTemplate(target, candidate)
            Return TargetFolderFor(target, pattern, candidate.CapturedAt)
        End Function

        Public Shared Function HasPlaceholders(target As String) As Boolean
            Return If(target, "").Contains("{"c)
        End Function

        ''' <summary>Loest eine Vorlage fuer eine Aufnahme auf. Dieselben Namen wie beim
        ''' Stapel-Umbenennen, damit es nur einen Dialekt gibt: {datetaken:FORMAT} (Aufnahmezeit,
        ''' ersatzweise Aenderungszeit), {date:FORMAT} (Aenderungszeit), {camera}; dazu fuer Ordner
        ''' {pictures} (Bilder-Ordner des Benutzers) und {home}. Ein Wert, der selbst einen
        ''' Schraegstrich traegt (eine Kamera "A/B"), wird dabei entschaerft, damit er keine
        ''' Ordnerebene erfindet. Ein unbekannter Platzhalter bleibt stehen, damit er auffaellt.</summary>
        Public Shared Function ExpandTargetTemplate(template As String, candidate As ImportCandidate) As String
            Dim text = If(template, "").Trim()
            text = text.Replace("{pictures}", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)).
                        Replace("{home}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            Dim camera = SafeSegment(If(candidate?.Camera, ""))
            If camera.Length = 0 Then camera = LocalizationService.T("Ohne Kamera")
            text = text.Replace("{camera}", camera)
            Dim taken = If(candidate Is Nothing, DateTime.Now, candidate.CapturedAt)
            Dim modified = If(candidate Is Nothing OrElse candidate.ModifiedAt = DateTime.MinValue, taken, candidate.ModifiedAt)
            text = System.Text.RegularExpressions.Regex.Replace(text, "\{datetaken:([^}]+)\}", Function(m) FormatSafe(taken, m.Groups(1).Value))
            text = System.Text.RegularExpressions.Regex.Replace(text, "\{date:([^}]+)\}", Function(m) FormatSafe(modified, m.Groups(1).Value))
            Return text
        End Function

        Private Shared Function FormatSafe(value As DateTime, format As String) As String
            Try
                Return SafeSegment(value.ToString(format, CultureInfo.InvariantCulture))
            Catch
                Return ""
            End Try
        End Function

        Private Shared Function SafeSegment(value As String) As String
            Dim invalid = Path.GetInvalidFileNameChars()
            Return New String(value.Select(Function(c) If(invalid.Contains(c) OrElse c = "/"c OrElse c = "\"c, "_"c, c)).ToArray()).Trim()
        End Function

        ''' <summary>Der feste Anfang eines Ziels: der Ordner vor dem ersten Platzhalter, mit
        ''' {pictures} und {home} aufgeloest. An ihm werden Platz und Dateiregel geprueft.</summary>
        Public Shared Function FixedRootOf(target As String) As String
            Dim text = If(target, "").Trim().
                Replace("{pictures}", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)).
                Replace("{home}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
            Dim brace = text.IndexOf("{"c)
            If brace < 0 Then Return text
            Dim prefix = text.Substring(0, brace)
            Dim cut = prefix.LastIndexOfAny({"/"c, "\"c})
            If cut <= 0 Then Return If(cut = 0, prefix.Substring(0, 1), "")
            Return prefix.Substring(0, cut)
        End Function

        ''' <summary>Wie viele der Dateien schon im Ziel zu liegen scheinen (gleicher Name, gleiche
        ''' Groesse). Nur fuer den Hinweis im Dialog; das Einlesen prueft es genau.</summary>
        Public Shared Function CountProbablyPresent(candidates As IEnumerable(Of ImportCandidate), targetRoot As String, pattern As String) As Integer
            If String.IsNullOrWhiteSpace(targetRoot) Then Return 0
            Dim count = 0
            For Each candidate In candidates
                Try
                    Dim target = Path.Combine(TargetFolderFor(targetRoot, pattern, candidate), Path.GetFileName(candidate.SourcePath))
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
                ' Bei einer Vorlage zaehlt ihr fester Anfang; ein schlichter Ordner ist es selbst.
                Dim existing = FixedRootOf(targetRoot)
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
        ''' <summary>Liegt <paramref name="folder"/> in <paramref name="root"/> oder ist er es selbst?
        ''' Verglichen werden volle Pfade mit Trennzeichen am Ende, damit "/fotos2" nicht als Teil von
        ''' "/fotos" gilt.</summary>
        Public Shared Function IsWithin(folder As String, root As String) As Boolean
            If String.IsNullOrWhiteSpace(folder) OrElse String.IsNullOrWhiteSpace(root) Then Return False
            ' Ein Geraet hinter kmtpd hat keinen Ordner im Dateisystem, in dem ein Ziel liegen koennte.
            If MtpSession.IsAddress(root) Then Return False
            ' Wörtlich UND mit aufgelösten Verweisen: zeigt das Ziel über einen symbolischen Verweis
            ' in die Quelle (oder die Quelle über einen auf einen anderen Ort), folgen Anlegen und
            ' Kopieren dem Verweis, und der wörtliche Vergleich allein sähe das nicht.
            If IsWithinLiteral(folder, root) Then Return True
            Return IsWithinLiteral(FileOperationPolicy.ResolveLinks(folder), FileOperationPolicy.ResolveLinks(root))
        End Function

        Private Shared Function IsWithinLiteral(folder As String, root As String) As Boolean
            If String.IsNullOrWhiteSpace(folder) OrElse String.IsNullOrWhiteSpace(root) Then Return False
            Try
                Dim f = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) & Path.DirectorySeparatorChar
                Dim r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) & Path.DirectorySeparatorChar
                Dim comparison = If(PathIdentity.IgnoresCase, StringComparison.OrdinalIgnoreCase, StringComparison.Ordinal)
                Return f.StartsWith(r, comparison)
            Catch
                Return False
            End Try
        End Function

        ''' <param name="sourceRoot">Die Quelle. Ein Zielordner darin wird nie beschrieben: die Datei
        ''' zaehlt dann als fehlgeschlagen. Der Dialog laesst das gar nicht erst zu; das hier ist die
        ''' letzte Absicherung der Zusage, dass auf der Quelle nichts veraendert wird.</param>
        Public Shared Function Run(candidates As IList(Of ImportCandidate), targetRoot As String, pattern As String,
                                   backupRoot As String, token As CancellationToken,
                                   progress As Action(Of Integer, Integer),
                                   Optional sourceRoot As String = "") As ImportResult
            Dim result As New ImportResult()
            Dim roots = {targetRoot, backupRoot}.Where(Function(r) Not String.IsNullOrWhiteSpace(r)).ToList()
            Dim cleaned As New HashSet(Of String)(PathIdentity.Comparer)
            Dim mtp As MtpSession = Nothing
            Try
                For index = 0 To candidates.Count - 1
                    If token.IsCancellationRequested Then
                        result.Cancelled = True
                        Exit For
                    End If
                    Dim candidate = candidates(index)
                    Dim ok = True
                    Dim present = False
                    Dim fileName = Path.GetFileName(candidate.SourcePath)
                    ' Vom Geraet hinter kmtpd erst in eine Zwischendatei; sie ist dann die Quelle der
                    ' gewohnten geprueften Kopie.
                    Dim fetched = ""
                    If candidate.MtpStorage IsNot Nothing Then
                        Try
                            If mtp Is Nothing Then mtp = MtpSession.TryOpen()
                            If mtp Is Nothing Then Throw New IOException("Kein Zugang zu kmtpd")
                            fetched = FetchFromMtp(mtp, candidate, FixedRootOf(roots(0)), cleaned, token)
                        Catch ex As OperationCanceledException
                            result.Cancelled = True
                            Exit For
                        Catch ex As Exception
                            DiagnosticLogService.LogException("Import.MtpFetch", ex)
                            ok = False
                        End Try
                    End If
                    Try
                        For rootIndex = 0 To roots.Count - 1
                            If Not ok AndAlso candidate.MtpStorage IsNot Nothing Then Exit For
                            Dim folder = TargetFolderFor(roots(rootIndex), pattern, candidate)
                            If IsWithin(folder, sourceRoot) Then
                                DiagnosticLogService.LogAlways("Import.TargetInSource", folder)
                                ok = False
                                Continue For
                            End If
                            Try
                                Directory.CreateDirectory(folder)
                                If cleaned.Add(folder) Then RemoveStaleParts(folder)
                                Dim outcome = CopyVerified(If(fetched.Length > 0, fetched, candidate.SourcePath), fileName, folder, token)
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
                    Finally
                        If fetched.Length > 0 Then
                            Try
                                File.Delete(fetched)
                            Catch ex As Exception
                                DiagnosticLogService.LogException("Import.MtpCleanup", ex)
                            End Try
                        End If
                    End Try
                    If result.Cancelled Then Exit For
                    If Not ok Then
                        result.Failed.Add(fileName)
                    ElseIf present Then
                        result.AlreadyPresent += 1
                    Else
                        result.Copied += 1
                    End If
                    progress?.Invoke(index + 1, candidates.Count)
                Next
            Finally
                mtp?.Dispose()
            End Try
            Return result
        End Function

        ''' <summary>Holt eine Datei vom Geraet in eine versteckte Zwischendatei im festen Anfang des
        ''' Ziels, mit demselben Namensschema wie die Zwischendateien der Kopie, damit ein
        ''' abgebrochener Lauf sie ebenso wegraeumt. Danach stimmt die Groesse mit der Ordnerliste
        ''' ueberein, die Aenderungszeit ist die des Geraets, und Aufnahmezeit und Kamera kommen aus
        ''' den EXIF-Daten der geholten Datei.
        '''
        ''' Eine Pruefsumme der Quelle gibt es hier nicht: MTP liefert keine. Geprueft wird die
        ''' Uebertragung vom Geraet deshalb an Groesse und Rueckmeldung von kmtpd; von der
        ''' Zwischendatei ins Ziel dann wie sonst mit SHA-256.</summary>
        Private Shared Function FetchFromMtp(session As MtpSession, candidate As ImportCandidate, stagingFolder As String,
                                             cleaned As HashSet(Of String), token As CancellationToken) As String
            If String.IsNullOrWhiteSpace(stagingFolder) Then Throw New IOException("Kein Ordner fuer die Zwischendatei")
            Directory.CreateDirectory(stagingFolder)
            If cleaned.Add(stagingFolder) Then RemoveStaleParts(stagingFolder)
            Dim staged = PartPathFor(stagingFolder, Path.GetFileName(candidate.SourcePath))
            Try
                session.FetchFile(candidate.MtpStorage, candidate.MtpPath, staged, token)
                Dim info As New FileInfo(staged)
                If info.Length <> candidate.Size Then
                    Throw New IOException("Vom Geraet kamen " & info.Length.ToString(CultureInfo.InvariantCulture) & " statt " &
                                          candidate.Size.ToString(CultureInfo.InvariantCulture) & " Byte: " & candidate.MtpPath)
                End If
                File.SetLastWriteTime(staged, candidate.ModifiedAt)
                info.Refresh()
                Dim camera = ""
                candidate.CapturedAt = CaptureTimeOf(staged, info, camera)
                candidate.Camera = camera
                Return staged
            Catch
                Try
                    If File.Exists(staged) Then File.Delete(staged)
                Catch
                End Try
                Throw
            End Try
        End Function

        Private Shared Function PartPathFor(folder As String, fileName As String) As String
            Return Path.Combine(folder, "." & fileName & "." & DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) &
                                        "." & Guid.NewGuid().ToString("N").Substring(0, 8) & PartSuffix)
        End Function

        ''' <summary>Eine Datei in einen Ordner, geprueft und ohne Ersetzen, unter
        ''' <paramref name="fileName"/>. Present = True: dieselbe Datei lag schon dort.</summary>
        Private Shared Function CopyVerified(source As String, fileName As String, folder As String, token As CancellationToken) As (Present As Boolean, FinalPath As String)
            Dim partPath = PartPathFor(folder, fileName)
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
