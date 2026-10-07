Imports System
Imports System.Collections
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq

Namespace Services

    Public NotInheritable Class FileOperationPolicy
        Private Sub New()
        End Sub

        Public Shared ReadOnly Property PersonalFolder As String =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)

        ''' <summary>
        ''' Darf Dateiarbeit einem Verweis folgen, der aus dem Benutzerordner hinausfuehrt?
        '''
        ''' Standard AUS. Ein Verweis kann irgendwohin zeigen, und dann greifen Loeschen oder
        ''' Verschieben ausserhalb dessen zu, was der Nutzer als seinen Bestand ansieht. Wer den
        ''' Bilderordner aber bewusst auf eine andere Platte legt, braucht genau das - deshalb ist
        ''' es eine Einstellung und keine feste Regel.
        '''
        ''' Gesetzt wird der Wert beim Start und bei jeder Aenderung aus den Einstellungen.
        ''' </summary>
        Public Shared Property FollowLinkedFolders As Boolean = False

        ''' <summary>
        ''' Darf Dateiarbeit auch AUSSERHALB des Benutzerordners stattfinden - auf einer zweiten
        ''' Platte, einem eingehaengten Laufwerk, einem Netzordner?
        '''
        ''' Standard AUS. Eingeschaltet bleibt trotzdem gesperrt, was
        ''' das System traegt (<see cref="IsSystemPath"/>), dazu die Wurzel jedes Laufwerks und die
        ''' Sammelordner wie /home oder /media selbst (<see cref="IsProtectedFolder"/>).
        ''' </summary>
        Public Shared Property AllowOutsidePersonalFolder As Boolean = False

        ''' <summary>Liegt der Pfad dort, wo Dateiarbeit erlaubt ist? Ab Werk der Benutzerordner,
        ''' mit <see cref="AllowOutsidePersonalFolder"/> alles ausser den Systemordnern.</summary>
        Public Shared Function IsInAllowedArea(path As String) As Boolean
            If IsInPersonalFolder(path) Then Return True
            If Not AllowOutsidePersonalFolder OrElse String.IsNullOrEmpty(path) Then Return False
            ' Beide Schreibweisen: ein Verweis aus einem harmlosen Ordner ins System waere sonst ein
            ' Ausbruch, genau wie beim Benutzerordner.
            Return Not IsSystemPath(path) AndAlso Not IsSystemPath(ResolveLinks(path))
        End Function

        ''' <summary>Ordner, die das System traegt und in denen FerrumPix nie Dateien anfasst, auch
        ''' nicht mit <see cref="AllowOutsidePersonalFolder"/>. Unter Linux und macOS feste Pfade,
        ''' unter Windows die Sonderordner des Systems.</summary>
        Friend Shared Function IsSystemPath(path As String) As Boolean
            Dim full As String
            Try
                full = IO.Path.GetFullPath(path)
            Catch
                Return True
            End Try
            Dim root = IO.Path.GetPathRoot(full)
            If String.IsNullOrEmpty(root) OrElse PathIdentity.Comparer.Equals(NormalizePath(full), NormalizePath(root)) Then Return True
            ' /run gehoert dem System, aber unter /run/media haengt Linux die Laufwerke ein.
            If Not OperatingSystem.IsWindows() AndAlso IsAncestorOrSelf("/run/media", full) Then Return False
            Return SystemFolders().Any(Function(s) IsAncestorOrSelf(s, full))
        End Function

        Private Shared ReadOnly _unixSystemFolders As String() = {
            "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/libx32", "/opt", "/proc",
            "/root", "/run", "/sbin", "/srv", "/sys", "/usr", "/var", "/snap", "/efi",
            "/System", "/Library", "/Applications", "/private", "/cores"
        }

        Private Shared Function SystemFolders() As IEnumerable(Of String)
            If Not OperatingSystem.IsWindows() Then Return _unixSystemFolders
            Return {Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)}.
                Where(Function(p) Not String.IsNullOrEmpty(p))
        End Function

        ''' <summary>
        ''' Liegt der Pfad im Benutzerordner? Geprueft wird ZWEIMAL: einmal so, wie er geschrieben
        ''' steht, und einmal mit aufgeloesten Verweisen.
        '''
        ''' Der zweite Durchgang ist der eigentliche Schutz. `Path.GetFullPath` loest ".." auf,
        ''' aber KEINE Symlinks und Junctions. Ein Verweis im Bilderordner, der nach draussen zeigt,
        ''' bestand die rein buchstaebliche Pruefung - und Kopieren, Verschieben oder Loeschen
        ''' griffen dann ausserhalb des Benutzerordners zu.
        '''
        ''' Aufgeloest wird erst NACH der buchstaeblichen Pruefung: was schon dem Namen nach
        ''' draussen liegt, ist ohnehin abgelehnt, und der Weg ueber das Dateisystem kostet Zeit.
        ''' </summary>
        Public Shared Function IsInPersonalFolder(path As String) As Boolean
            If Not IsAncestorOrSelf(PersonalFolder, path) Then Return False
            If FollowLinkedFolders Then Return True
            Return IsAncestorOrSelf(ResolveLinks(PersonalFolder), ResolveLinks(path))
        End Function

        ''' <summary>Kurzlebiger Zwischenspeicher fuer <see cref="ResolveLinks"/>.
        '''
        ''' Der Aufloeser geht je Pfadabschnitt einmal aufs Dateisystem, und die
        ''' CanFileOperation-Eigenschaften rufen ihn bei JEDEM Kontextmenue fuer JEDES markierte
        ''' Element. Lokal faellt das nicht auf, auf einem Netzlaufwerk haengt das Menue dadurch.
        '''
        ''' Bewusst nur kurz gueltig: das Ergebnis entscheidet mit, ob eine Datei angefasst werden
        ''' darf. Ein Verweis, der sich aendert, muss sich schnell durchsetzen - zwei Sekunden decken
        ''' den Ausbruch eines Menueaufbaus ab und nicht mehr.</summary>
        Private Const LinkCacheMs As Long = 2000
        Private Shared ReadOnly _linkCache As New Concurrent.ConcurrentDictionary(Of String, (Stamp As Long, Value As String))(StringComparer.Ordinal)

        ''' <summary>
        ''' Loest Verweise auf, auch solche MITTEN im Pfad: jeder Abschnitt wird einzeln geprueft,
        ''' von der Wurzel her. Ein Verweis nur auf den letzten Abschnitt zu pruefen reichte nicht -
        ''' der Ausbruch kann schon eine Ebene darueber sitzen.
        '''
        ''' Ringe aus Verweisen fangen wir nicht selbst ab; `ResolveLinkTarget` wirft dabei, und der
        ''' Abschnitt bleibt dann unaufgeloest stehen. Das ist die sichere Richtung: im Zweifel
        ''' bleibt der Pfad, wie er ist, und die buchstaebliche Pruefung entscheidet.
        ''' </summary>
        Friend Shared Function ResolveLinks(path As String) As String
            If String.IsNullOrEmpty(path) Then Return ""
            Dim full As String
            Try
                full = IO.Path.GetFullPath(path)
            Catch
                Return path
            End Try

            Dim now = Environment.TickCount64
            Dim cached As (Stamp As Long, Value As String) = Nothing
            If _linkCache.TryGetValue(full, cached) AndAlso now - cached.Stamp < LinkCacheMs Then
                Return cached.Value
            End If
            ' Nicht unbegrenzt wachsen lassen: der Zwischenspeicher lebt ohnehin nur Sekunden.
            If _linkCache.Count > 4096 Then _linkCache.Clear()

            Dim resolved = ResolveLinksUncached(full)
            _linkCache(full) = (now, resolved)
            Return resolved
        End Function

        Private Shared Function ResolveLinksUncached(full As String) As String
            Dim parent = IO.Path.GetDirectoryName(full)
            If String.IsNullOrEmpty(parent) Then Return full   ' Wurzel erreicht

            Dim basis = ResolveLinks(parent)
            Dim candidate = IO.Path.Combine(basis, IO.Path.GetFileName(full))
            Try
                Dim info As FileSystemInfo =
                    If(Directory.Exists(candidate),
                       CType(New DirectoryInfo(candidate), FileSystemInfo),
                       New FileInfo(candidate))
                Dim target = info.ResolveLinkTarget(returnFinalTarget:=True)
                If target IsNot Nothing Then Return IO.Path.GetFullPath(target.FullName)
            Catch
            End Try
            Return candidate
        End Function

        Public Shared Function CanCopy(path As String) As Boolean
            Return IsInAllowedArea(path) AndAlso Not IsHiddenPath(path)
        End Function

        Public Shared Function CanPasteInto(folderPath As String) As Boolean
            Return Not String.IsNullOrEmpty(folderPath) AndAlso
                   Directory.Exists(folderPath) AndAlso
                   IsInAllowedArea(folderPath) AndAlso
                   Not IsHiddenPath(folderPath)
        End Function

        Public Shared Function CanRename(path As String) As Boolean
            Return CanModify(path) AndAlso Not IsProtectedFolder(path)
        End Function

        Public Shared Function CanDelete(path As String) As Boolean
            Return CanModify(path) AndAlso Not IsProtectedFolder(path)
        End Function

        Public Shared Function CanMove(path As String, targetFolder As String) As Boolean
            Return CanModify(path) AndAlso
                   CanPasteInto(targetFolder) AndAlso
                   Not IsProtectedFolder(path) AndAlso
                   Not IsAncestorOrSelf(path, targetFolder)
        End Function

        Public Shared Function CanDuplicate(path As String, targetFolder As String) As Boolean
            Return CanCopy(path) AndAlso CanPasteInto(targetFolder)
        End Function

        Private Shared Function CanModify(path As String) As Boolean
            Return Not String.IsNullOrEmpty(path) AndAlso
                   (File.Exists(path) OrElse Directory.Exists(path)) AndAlso
                   IsInAllowedArea(path) AndAlso
                   Not IsHiddenPath(path)
        End Function

        Public Shared Function IsProtectedFolder(path As String) As Boolean
            If String.IsNullOrEmpty(path) OrElse Not Directory.Exists(path) Then Return False
            If IsHiddenPath(path) Then Return True

            Dim protectedFolders = GetProtectedFolders()
            Dim normalized = NormalizePath(path)
            Return protectedFolders.Any(Function(p) String.Equals(NormalizePath(p), normalized, StringComparison.OrdinalIgnoreCase))
        End Function

        Private Shared Function GetProtectedFolders() As IEnumerable(Of String)
            Dim home = PersonalFolder
            Dim folders As New List(Of String) From {
                home,
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Path.Combine(home, "Desktop"),
                Path.Combine(home, "Schreibtisch"),
                Path.Combine(home, "Documents"),
                Path.Combine(home, "Dokumente"),
                Path.Combine(home, "Pictures"),
                Path.Combine(home, "Bilder"),
                Path.Combine(home, "Downloads")
            }
            ' Die Sammelordner, unter denen Benutzer und Laufwerke haengen. Was DARIN liegt, ist mit
            ' AllowOutsidePersonalFolder erlaubt; sie selbst umzubenennen oder zu loeschen nie.
            If Not OperatingSystem.IsWindows() Then
                Dim user = Environment.UserName
                folders.AddRange({"/home", "/Users", "/mnt", "/media", "/Volumes", "/run/media",
                                  "/media/" & user, "/run/media/" & user})
            End If

            Return folders.Where(Function(p) Not String.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase)
        End Function

        ''' <summary>True fuer einen Papierkorb des Systems - den je Datentraeger (".Trash-1000",
        ''' ".Trash") ebenso wie den des Benutzers (~/.local/share/Trash).
        '''
        ''' Was dort liegt, hat jemand bewusst weggeworfen. Es gehoert in keine Trefferliste, und
        ''' anfassen laesst es sich ohnehin nicht: Loeschen und Verschieben sind fuer versteckte
        ''' Pfade gesperrt.</summary>
        Public Shared Function IsTrashFolder(path As String) As Boolean
            If String.IsNullOrEmpty(path) Then Return False
            Try
                Dim segments = NormalizePath(path).Split(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
                For Each segment In segments
                    If String.Equals(segment, "Trash", StringComparison.OrdinalIgnoreCase) Then Return True
                    If segment.StartsWith(".Trash", StringComparison.OrdinalIgnoreCase) Then Return True
                    ' Der Papierkorb von Windows, damit die Regel auf jedem System dasselbe meint.
                    If String.Equals(segment, "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase) Then Return True
                    If String.Equals(segment, "RECYCLER", StringComparison.OrdinalIgnoreCase) Then Return True
                Next
            Catch
            End Try
            Return False
        End Function

        ''' <summary>True, wenn irgendein ORDNER im Pfad mit einem Punkt beginnt.
        '''
        ''' Der sparsame Bruder von <see cref="IsHiddenPath"/>: reine Zeichenkettenarbeit, kein
        ''' Griff auf die Platte, und der Dateiname selbst bleibt aussen vor. Gedacht fuer
        ''' Durchlaeufe ueber tausende Katalogeintraege, wo ein zusaetzliches File.GetAttributes je
        ''' Eintrag spuerbar waere.</summary>
        Public Shared Function IsInHiddenFolder(path As String) As Boolean
            If String.IsNullOrEmpty(path) Then Return False
            Try
                Dim normalized = NormalizePath(path)
                Dim home = NormalizePath(PersonalFolder)
                Dim relative = If(IsAncestorOrSelf(home, normalized),
                                  normalized.Substring(home.Length).TrimStart(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar),
                                  normalized)
                Dim segments = relative.Split(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
                ' Das letzte Stueck ist der Dateiname - eine Datei ".xmp" macht keinen Ordner versteckt.
                For i = 0 To segments.Length - 2
                    If segments(i).StartsWith(".", StringComparison.Ordinal) Then Return True
                Next
            Catch
            End Try
            Return False
        End Function

        Public Shared Function IsHiddenPath(path As String) As Boolean
            If String.IsNullOrEmpty(path) Then Return False
            Try
                Dim normalized = NormalizePath(path)
                Dim home = NormalizePath(PersonalFolder)
                Dim relative = If(IsAncestorOrSelf(home, normalized),
                                  normalized.Substring(home.Length).TrimStart(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar),
                                  normalized)
                If relative.Split(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar).
                    Any(Function(part) part.StartsWith(".", StringComparison.Ordinal)) Then Return True

                If File.Exists(path) OrElse Directory.Exists(path) Then
                    Return (File.GetAttributes(path) And FileAttributes.Hidden) = FileAttributes.Hidden
                End If
            Catch
            End Try
            Return False
        End Function

        Private Shared Function IsAncestorOrSelf(parentPath As String, childPath As String) As Boolean
            Dim parent = NormalizePath(parentPath)
            Dim child = NormalizePath(childPath)
            If String.IsNullOrEmpty(parent) OrElse String.IsNullOrEmpty(child) Then Return False
            Return child.Equals(parent, StringComparison.OrdinalIgnoreCase) OrElse
                   child.StartsWith(AppendDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function NormalizePath(path As String) As String
            If String.IsNullOrEmpty(path) Then Return ""
            Try
                Dim fullPath = IO.Path.GetFullPath(path)
                Dim root = IO.Path.GetPathRoot(fullPath)
                If String.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) Then Return fullPath
                Return fullPath.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
            Catch
                Return path.TrimEnd(IO.Path.DirectorySeparatorChar, IO.Path.AltDirectorySeparatorChar)
            End Try
        End Function

        Private Shared Function AppendDirectorySeparator(path As String) As String
            If path.EndsWith(IO.Path.DirectorySeparatorChar) OrElse path.EndsWith(IO.Path.AltDirectorySeparatorChar) Then Return path
            Return path & IO.Path.DirectorySeparatorChar
        End Function
    End Class

End Namespace
