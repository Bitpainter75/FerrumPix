Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq

Namespace Services

    ''' <summary>Feste Fotoordner: beim Start festgelegte Wurzeln, ausserhalb derer FerrumPix keinen
    ''' Ordner zeigt, keinen Ordner waehlen laesst und keine Datei anfasst.
    '''
    ''' <para>GEDACHT FUER DEN BEHAELTER. Dort sieht die Anwendung das ganze Dateisystem des
    ''' Abbilds (/, /usr, /data ...), die Fotos liegen aber nur unter dem Einhaengepunkt /photos.
    ''' Das Dockerfile belegt die Angabe mit /photos vor. Ohne Angabe aendert sich nichts: der
    ''' Rechner zeigt seinen Baum wie immer, und die Dateiarbeit haengt weiter am Benutzerordner
    ''' und am Schalter dafuer (FileOperationPolicy).</para>
    '''
    ''' <para>MIT ANGABE ist genau dieser Bereich der Ort der Dateiarbeit (statt des Benutzerordners,
    ''' ohne den Schalter "ausserhalb des persoenlichen Ordners"): im Behaelter ist der
    ''' Benutzerordner /data/home, und die Fotos laegen sonst immer draussen. Die Wurzel selbst
    ''' bleibt geschuetzt wie ein Laufwerk, Systemordner bleiben gesperrt.</para>
    '''
    ''' <para>Der Datenordner der Anwendung (Katalog, Vorschauen, Modelle) liegt ausserhalb und
    ''' bleibt fuer die Anwendung selbst nutzbar; er ist nur kein Fotoordner.</para></summary>
    Public NotInheritable Class FolderRoots

        ''' <summary>Die Wurzeln aus der Umgebung, durch <see cref="Path.PathSeparator"/> getrennt
        ''' (":" unter Linux und macOS, ";" unter Windows), etwa <c>/photos</c> oder
        ''' <c>/srv/fotos:/mnt/archiv</c>.</summary>
        Public Const Variable As String = "FERRUMPIX_FOLDER_ROOTS"

        ''' <summary>Startparameter mit derselben Liste. Er geht der Umgebung vor.</summary>
        Public Const Switch As String = "--folder-roots="

        Private Shared _roots As IReadOnlyList(Of String) = Array.Empty(Of String)()

        Private Sub New()
        End Sub

        ''' <summary>Die Wurzeln als volle Pfade ohne Schlusstrenner, in der angegebenen Reihenfolge.
        ''' Leer heisst: keine Einschraenkung.</summary>
        Public Shared ReadOnly Property Roots As IReadOnlyList(Of String)
            Get
                Return _roots
            End Get
        End Property

        Public Shared ReadOnly Property IsActive As Boolean
            Get
                Return _roots.Count > 0
            End Get
        End Property

        ''' <summary>Liest Startparameter und Umgebung. Gerufen aus Main, vor allem anderen, was
        ''' einen Ordner zeigt.</summary>
        Public Shared Sub Configure(args As String())
            Dim argument = args?.FirstOrDefault(Function(a) a IsNot Nothing AndAlso a.StartsWith(Switch, StringComparison.OrdinalIgnoreCase))
            Dim text = If(argument IsNot Nothing, argument.Substring(Switch.Length), Environment.GetEnvironmentVariable(Variable))
            SetRoots(Parse(text))
            If IsActive Then
                DiagnosticLogService.LogAlways("App.FolderRoots", String.Join(Path.PathSeparator, _roots))
            End If
        End Sub

        ''' <summary>Setzt die Wurzeln. Nothing oder eine leere Liste schaltet die Einschraenkung ab.
        ''' Fuer den Start und den Pruefstand.</summary>
        Public Shared Sub SetRoots(paths As IEnumerable(Of String))
            _roots = If(paths Is Nothing, New List(Of String)(), Parse(String.Join(Path.PathSeparator, paths))).AsReadOnly()
        End Sub

        ''' <summary>Zerlegt die Angabe. Leere Teile fallen weg, ebenso die Wurzel des Dateisystems
        ''' ("/" schraenkte nichts ein und machte jeden Pfadvergleich leer, siehe
        ''' PathIdentity.IsFilesystemRoot) und Doppelte. Relative Pfade werden voll gemacht.</summary>
        Public Shared Function Parse(text As String) As List(Of String)
            Dim result As New List(Of String)()
            If String.IsNullOrWhiteSpace(text) Then Return result
            For Each part In text.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries Or StringSplitOptions.TrimEntries)
                If PathIdentity.IsFilesystemRoot(part) Then Continue For
                Dim full As String
                Try
                    full = Path.GetFullPath(part).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                Catch
                    Continue For
                End Try
                If full.Length = 0 OrElse result.Any(Function(r) PathIdentity.AreSame(r, full)) Then Continue For
                result.Add(full)
            Next
            Return result
        End Function

        ''' <summary>Ob ein Startparameter dieser Angabe gehoert. Die Anwendung bekommt ihn nicht zu
        ''' sehen, sie hielte ihn sonst fuer eine Datei.</summary>
        Public Shared Function IsOwnArgument(argument As String) As Boolean
            Return argument IsNot Nothing AndAlso argument.StartsWith(Switch, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Liegt der Pfad in einer Wurzel oder ist er eine? Ohne Wurzeln immer True.
        '''
        ''' <para>ZWEIMAL GEPRUEFT wie beim Benutzerordner (FileOperationPolicy.IsInPersonalFolder):
        ''' so, wie er geschrieben steht, und mit aufgeloesten Verweisen. Ein Verweis in /photos, der
        ''' nach /etc zeigt, besteht die erste Pruefung und faellt bei der zweiten. Wer Verweisen
        ''' ausdruecklich folgen laesst (FileOperationPolicy.FollowLinkedFolders), bekommt nur die
        ''' erste.</para></summary>
        Public Shared Function Allows(path As String) As Boolean
            If Not IsActive Then Return True
            If String.IsNullOrWhiteSpace(path) Then Return False
            Dim root = _roots.FirstOrDefault(Function(r) PathIdentity.IsAncestorOrSelf(r, path))
            If root Is Nothing Then Return False
            If FileOperationPolicy.FollowLinkedFolders Then Return True
            Dim resolved = FileOperationPolicy.ResolveLinks(path)
            Return _roots.Any(Function(r) PathIdentity.IsAncestorOrSelf(FileOperationPolicy.ResolveLinks(r), resolved) OrElse
                                          PathIdentity.IsAncestorOrSelf(r, resolved))
        End Function

        ''' <summary>Ist der Pfad selbst eine der Wurzeln?</summary>
        Public Shared Function IsRoot(path As String) As Boolean
            Return IsActive AndAlso Not String.IsNullOrWhiteSpace(path) AndAlso _roots.Any(Function(r) PathIdentity.AreSame(r, path))
        End Function

        ''' <summary>Die erste Wurzel, die es gibt, sonst die erste ueberhaupt. Leer ohne Wurzeln.</summary>
        Public Shared Function FirstRoot() As String
            If Not IsActive Then Return ""
            Dim existing = _roots.FirstOrDefault(Function(r)
                                                     Try
                                                         Return Directory.Exists(r)
                                                     Catch
                                                         Return False
                                                     End Try
                                                 End Function)
            Return If(existing, _roots(0))
        End Function

        ''' <summary>Ein Ordner, der in einer Wurzel liegt: der gegebene, wenn er das tut, sonst die
        ''' erste Wurzel. Ohne Wurzeln unveraendert. Fuer Startordner, Ziele und Waehler, die einen
        ''' gemerkten Ort von frueher vorschlagen wuerden.</summary>
        Public Shared Function ClampToRoots(folder As String) As String
            If Not IsActive Then Return folder
            If Not String.IsNullOrWhiteSpace(folder) AndAlso Allows(folder) Then Return folder
            Return FirstRoot()
        End Function

        ''' <summary>Was die Anwendung als persoenlichen Ordner zeigt und als Startpunkt nimmt: ohne
        ''' feste Fotoordner der Benutzerordner, mit ihnen die erste Wurzel. Das HOME der Umgebung
        ''' (im Behaelter /data/home) bleibt fuer Laufzeit und Bibliotheken, wie es ist; gemeint ist
        ''' nur der Startpunkt fuer Baum, Waehler, Ziele und den Platzhalter {home}.</summary>
        Public Shared Function HomeFolder() As String
            If IsActive Then Return FirstRoot()
            Return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        End Function

        ''' <summary>Der Bilderordner als Startpunkt und fuer den Platzhalter {pictures}: ohne feste
        ''' Fotoordner der des Systems, mit ihnen die erste Wurzel.</summary>
        Public Shared Function PicturesFolder() As String
            If IsActive Then Return FirstRoot()
            Return Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        End Function

        ''' <summary>Ein echter Ordnerpfad (kein Ziel einer Serverquelle wie immich://)? Nur solche
        ''' werden auf die Wurzeln umgelenkt.</summary>
        Public Shared Function IsLocalFolderPath(path As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(path) AndAlso Not path.Contains("://", StringComparison.Ordinal) AndAlso
                   IO.Path.IsPathRooted(path)
        End Function

    End Class

End Namespace
