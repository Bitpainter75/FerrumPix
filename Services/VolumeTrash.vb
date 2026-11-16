Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Text

Namespace Services

    ''' <summary>
    ''' Der Papierkorb eines Laufwerks nach der Regel der Linux-Desktops, von FerrumPix selbst
    ''' bedient. Nur der Rueckfall, wenn das Papierkorb-Programm des Systems ablehnt
    ''' (TrashService).
    '''
    ''' WOZU: im Flatpak laeuft das Verschieben in den Papierkorb ueber das Portal, und dafuer wird
    ''' die Datei zum Lesen UND Schreiben geoeffnet. Eine Datei, die der Nutzer nicht schreiben
    ''' darf, scheitert dort mit "Failed to open", obwohl fuer den Papierkorb das Schreibrecht am
    ''' Ordner genuegt; die Dateiverwaltung daneben kann es (Nutzerbefund, nachgestellt mit einer
    ''' nur lesbaren Datei).
    '''
    ''' WIE: die Datei wird in <c>&lt;Laufwerk&gt;/.Trash-&lt;Benutzernummer&gt;/files</c>
    ''' umbenannt, daneben entsteht in <c>info</c> die Datei mit Herkunft und Zeitpunkt, an der
    ''' die Dateiverwaltungen sie erkennen und wiederherstellen. Liegt auf dem Laufwerk ein
    ''' gemeinsamer Ordner <c>.Trash</c> mit gesetztem Haftbit, gilt
    ''' <c>.Trash/&lt;Benutzernummer&gt;</c>. Erst die Infodatei (ausschliesslich angelegt, sie
    ''' reserviert den Namen), dann das Umbenennen; scheitert das, geht die Infodatei wieder weg.
    '''
    ''' WO NICHT: auf dem Laufwerk des Benutzerordners. Dessen Papierkorb liegt im Benutzerordner
    ''' selbst, und im Flatpak zeigt dieser Ort in den Ordner der Anwendung, wo keine
    ''' Dateiverwaltung ihn sucht. Auch nicht, wenn nur ein Unterordner des Laufwerks eingehaengt
    ''' ist (im Flatpak eine Freigabe unterhalb der Wurzel): der Papierkorb gehoert an die Wurzel
    ''' des Laufwerks, und die ist dann nicht zu sehen. Und nicht auf einem nur lesend
    ''' eingehaengten Laufwerk. In allen drei Faellen bleibt es bei der Meldung des Systems.
    ''' </summary>
    Public NotInheritable Class VolumeTrash

        Private Sub New()
        End Sub

        ''' <summary>Ein Eintrag der Einhaengetabelle (/proc/self/mountinfo), soweit gebraucht.</summary>
        Private NotInheritable Class Mount
            Public Device As String = ""
            Public Root As String = ""
            Public Point As String = ""
            Public ReadOnlyMount As Boolean
            Public SuperOptions As String = ""
        End Class

        ''' <summary>Legt Datei oder Ordner in den Papierkorb ihres Laufwerks. False mit leerem
        ''' Grund, wenn dieser Weg hier nicht gilt (siehe oben); False mit Grund, wenn er galt
        ''' und scheiterte.</summary>
        Public Shared Function TryMove(path As String, ByRef reason As String) As Boolean
            reason = ""
            If Not OperatingSystem.IsLinux() Then Return False
            Try
                Dim top = ResolveTopDirectory(path, File.ReadAllText("/proc/self/mountinfo"),
                                              Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                If String.IsNullOrEmpty(top) Then Return False
                Dim uid = CurrentUserId()
                If uid < 0 Then Return False
                MoveInto(path, top, uid, DateTime.Now)
                Return True
            Catch ex As Exception
                reason = If(ex.Message, "").Trim()
                Return False
            End Try
        End Function

        ''' <summary>Die Wurzel des Laufwerks, auf dem <paramref name="path"/> liegt, wenn dort ein
        ''' eigener Papierkorb in Frage kommt, sonst Nothing. Die Tabelle und der Benutzerordner
        ''' sind Parameter, damit sich die Regel ohne echtes Laufwerk pruefen laesst.</summary>
        Friend Shared Function ResolveTopDirectory(path As String, mountInfo As String, homeDirectory As String) As String
            If String.IsNullOrEmpty(path) OrElse String.IsNullOrEmpty(mountInfo) Then Return Nothing
            Dim mounts = ParseMounts(mountInfo)
            Dim own = MountOf(path, mounts)
            If own Is Nothing OrElse own.ReadOnlyMount Then Return Nothing
            ' Nur ein ganzes Laufwerk: die Wurzel des Dateisystems, oder bei einem Teilvolumen
            ' genau dieses. Alles andere ist ein eingehaengter Unterordner.
            Dim wholeVolume = own.Root = "/" OrElse
                              ("," & own.SuperOptions & ",").Contains(",subvol=" & own.Root & ",", StringComparison.Ordinal)
            If Not wholeVolume Then Return Nothing
            If Not String.IsNullOrEmpty(homeDirectory) Then
                Dim home = MountOf(homeDirectory, mounts)
                If home IsNot Nothing AndAlso String.Equals(home.Device, own.Device, StringComparison.Ordinal) Then Return Nothing
            End If
            ' Die Datei, die selbst der Einhaengepunkt ist, laesst sich nicht wegbewegen.
            If String.Equals(TrimSeparator(path), own.Point, StringComparison.Ordinal) Then Return Nothing
            Return own.Point
        End Function

        ''' <summary>Der Eintrag mit dem laengsten Einhaengepunkt, unter dem der Pfad liegt; bei
        ''' mehreren am selben Punkt der letzte, er liegt obenauf.</summary>
        Private Shared Function MountOf(path As String, mounts As List(Of Mount)) As Mount
            Dim full = TrimSeparator(path)
            Dim best As Mount = Nothing
            For Each m In mounts
                Dim covers = m.Point = "/" OrElse String.Equals(full, m.Point, StringComparison.Ordinal) OrElse
                             full.StartsWith(m.Point & "/", StringComparison.Ordinal)
                If Not covers Then Continue For
                If best Is Nothing OrElse m.Point.Length >= best.Point.Length Then best = m
            Next
            Return best
        End Function

        Private Shared Function TrimSeparator(path As String) As String
            If path.Length > 1 AndAlso path.EndsWith("/", StringComparison.Ordinal) Then Return path.TrimEnd("/"c)
            Return path
        End Function

        ''' <summary>Zeilen der Form "Nummer Eltern Geraet Wurzel Punkt Optionen ... - Typ Quelle
        ''' Optionen". Leerzeichen und Sonderzeichen in Pfaden stehen dort als \040 und aehnlich.</summary>
        Private Shared Function ParseMounts(text As String) As List(Of Mount)
            Dim result As New List(Of Mount)()
            For Each line In text.Split({vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)
                Dim parts = line.Split(" "c, StringSplitOptions.RemoveEmptyEntries)
                If parts.Length < 7 Then Continue For
                Dim separator = Array.IndexOf(parts, "-")
                Dim m As New Mount With {
                    .Device = parts(2),
                    .Root = Unescape(parts(3)),
                    .Point = Unescape(parts(4)),
                    .ReadOnlyMount = ("," & parts(5) & ",").Contains(",ro,", StringComparison.Ordinal)
                }
                If separator >= 0 AndAlso separator + 3 < parts.Length Then m.SuperOptions = Unescape(parts(separator + 3))
                result.Add(m)
            Next
            Return result
        End Function

        Private Shared Function Unescape(value As String) As String
            If value.IndexOf("\"c) < 0 Then Return value
            Dim bytes As New List(Of Byte)()
            Dim i = 0
            While i < value.Length
                If value(i) = "\"c AndAlso i + 3 < value.Length AndAlso
                   value.Substring(i + 1, 3).All(Function(c) c >= "0"c AndAlso c <= "7"c) Then
                    bytes.Add(Convert.ToByte(value.Substring(i + 1, 3), 8))
                    i += 4
                Else
                    bytes.AddRange(Encoding.UTF8.GetBytes(value(i).ToString()))
                    i += 1
                End If
            End While
            Return Encoding.UTF8.GetString(bytes.ToArray())
        End Function

        ''' <summary>Die Benutzernummer des Prozesses, aus /proc/self/status (Zeile "Uid:", zweiter
        ''' Wert ist die wirksame). -1, wenn sie sich nicht lesen laesst.</summary>
        Private Shared Function CurrentUserId() As Integer
            For Each line In File.ReadLines("/proc/self/status")
                If Not line.StartsWith("Uid:", StringComparison.Ordinal) Then Continue For
                Dim parts = line.Substring(4).Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                Dim uid As Integer
                If parts.Length >= 2 AndAlso Integer.TryParse(parts(1), NumberStyles.None, CultureInfo.InvariantCulture, uid) Then Return uid
            Next
            Return -1
        End Function

        ''' <summary>Der Papierkorbordner des Laufwerks fuer diesen Benutzer: der gemeinsame
        ''' <c>.Trash</c>, wenn es ihn als echten Ordner mit Haftbit gibt, sonst der eigene
        ''' <c>.Trash-&lt;Nummer&gt;</c>.</summary>
        Friend Shared Function TrashDirectory(topDirectory As String, uid As Integer) As String
            Dim number = uid.ToString(CultureInfo.InvariantCulture)
            Dim sharedTrash = Path.Combine(topDirectory, ".Trash")
            Try
                Dim info As New DirectoryInfo(sharedTrash)
                If info.Exists AndAlso info.LinkTarget Is Nothing AndAlso
                   (File.GetUnixFileMode(sharedTrash) And UnixFileMode.StickyBit) <> 0 Then
                    Return Path.Combine(sharedTrash, number)
                End If
            Catch
            End Try
            Return Path.Combine(topDirectory, ".Trash-" & number)
        End Function

        ''' <summary>Legt die Datei in den Papierkorb unter <paramref name="topDirectory"/> und gibt
        ''' ihren neuen Ort zurueck. Wirft mit dem Grund, wenn es nicht geht; dann liegt die Datei
        ''' unveraendert an ihrem Platz, und keine Infodatei bleibt uebrig.</summary>
        Friend Shared Function MoveInto(path As String, topDirectory As String, uid As Integer, deletedAt As DateTime) As String
            Dim isDirectory = Directory.Exists(path)
            If Not isDirectory AndAlso Not File.Exists(path) Then Throw New FileNotFoundException("Datei nicht gefunden", path)
            Dim top = TrimSeparator(topDirectory)
            Dim full = TrimSeparator(path)
            If Not full.StartsWith(top & "/", StringComparison.Ordinal) Then
                Throw New IOException("Die Datei liegt nicht auf diesem Laufwerk: " & path)
            End If

            Dim trash = TrashDirectory(top, uid)
            Dim filesFolder = IO.Path.Combine(trash, "files")
            Dim infoFolder = IO.Path.Combine(trash, "info")
            For Each folder In {trash, filesFolder, infoFolder}
                If Directory.Exists(folder) Then Continue For
                ' Nur der Besitzer darf hinein, wie die Regel es fuer den eigenen Ordner verlangt.
                Directory.CreateDirectory(folder, UnixFileMode.UserRead Or UnixFileMode.UserWrite Or UnixFileMode.UserExecute)
            Next

            ' Der Pfad in der Infodatei zaehlt von der Wurzel des Laufwerks, jede Stufe maskiert.
            Dim relative = String.Join("/", full.Substring(top.Length + 1).Split("/"c).Select(Function(part) Uri.EscapeDataString(part)))
            Dim content = "[Trash Info]" & vbLf &
                          "Path=" & relative & vbLf &
                          "DeletionDate=" & deletedAt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) & vbLf

            Dim original = IO.Path.GetFileName(full)
            Dim stem = IO.Path.GetFileNameWithoutExtension(original)
            Dim extension = IO.Path.GetExtension(original)
            For attempt = 1 To 10000
                Dim name = If(attempt = 1, original, stem & "." & attempt.ToString(CultureInfo.InvariantCulture) & extension)
                Dim target = IO.Path.Combine(filesFolder, name)
                If File.Exists(target) OrElse Directory.Exists(target) Then Continue For
                Dim infoPath = IO.Path.Combine(infoFolder, name & ".trashinfo")
                Try
                    ' Ausschliesslich anlegen: wer die Infodatei hat, hat den Namen.
                    Using stream As New FileStream(infoPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)
                        Dim bytes = New UTF8Encoding(False).GetBytes(content)
                        stream.Write(bytes, 0, bytes.Length)
                    End Using
                Catch ex As IOException When File.Exists(infoPath)
                    Continue For
                End Try
                Try
                    If isDirectory Then Directory.Move(full, target) Else File.Move(full, target)
                Catch
                    Try : File.Delete(infoPath) : Catch : End Try
                    Throw
                End Try
                Return target
            Next
            Throw New IOException("Kein freier Name im Papierkorb: " & trash)
        End Function

    End Class

End Namespace
