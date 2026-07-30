Imports System
Imports System.IO
Imports System.Linq

Namespace Services

    Public NotInheritable Class FileOperationPolicy
        Private Sub New()
        End Sub

        Public Shared ReadOnly Property PersonalFolder As String =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)

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

        Public Shared Function IsInPersonalFolder(path As String) As Boolean
            If Not IsAncestorOrSelf(PersonalFolder, path) Then Return False
            If FollowLinkedFolders Then Return True
            Return IsAncestorOrSelf(ResolveLinks(PersonalFolder), ResolveLinks(path))
        End Function

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
            Dim voll As String
            Try
                voll = IO.Path.GetFullPath(path)
            Catch
                Return path
            End Try

            Dim eltern = IO.Path.GetDirectoryName(voll)
            If String.IsNullOrEmpty(eltern) Then Return voll   ' Wurzel erreicht

            Dim basis = ResolveLinks(eltern)
            Dim kandidat = IO.Path.Combine(basis, IO.Path.GetFileName(voll))
            Try
                Dim info As FileSystemInfo =
                    If(Directory.Exists(kandidat),
                       CType(New DirectoryInfo(kandidat), FileSystemInfo),
                       New FileInfo(kandidat))
                Dim ziel = info.ResolveLinkTarget(returnFinalTarget:=True)
                If ziel IsNot Nothing Then Return IO.Path.GetFullPath(ziel.FullName)
            Catch
            End Try
            Return kandidat
        End Function

        Public Shared Function CanCopy(path As String) As Boolean
            Return IsInPersonalFolder(path) AndAlso Not IsHiddenPath(path)
        End Function

        Public Shared Function CanPasteInto(folderPath As String) As Boolean
            Return Not String.IsNullOrEmpty(folderPath) AndAlso
                   Directory.Exists(folderPath) AndAlso
                   IsInPersonalFolder(folderPath) AndAlso
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
                   IsInPersonalFolder(path) AndAlso
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

            Return folders.Where(Function(p) Not String.IsNullOrEmpty(p)).Distinct(StringComparer.OrdinalIgnoreCase)
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
