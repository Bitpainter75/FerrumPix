Imports System
Imports System.Globalization
Imports System.IO

Namespace Services

    ''' <summary>Verschieben und Kopieren einer Datei so, dass unter dem Endnamen nie etwas
    ''' Halbfertiges liegt, und das Aufraeumen der Teildateien, die ein abgebrochener Lauf
    ''' hinterlaesst.
    '''
    ''' ZWEI ARTEN VON NACHBARDATEIEN, und nur eine davon darf je weggeraeumt werden:
    '''
    ''' - ".name.ferrumpix-new-xxxxxxxx" entsteht durch reines UMBENENNEN der Quelle. Ihr Inhalt ist
    '''   vollstaendig und beim Verschieben die EINZIGE Fassung: bleibt sie nach einem Absturz liegen,
    '''   ist die Quelle schon fort. Sie wird deshalb nie automatisch geloescht.
    ''' - ".name.ferrumpix-part-SEKUNDEN-xxxxxxxx" entsteht durch KOPIEREN, und die Quelle bleibt dabei
    '''   stehen, bis die Kopie unter dem Endnamen liegt. Eine liegengebliebene Teildatei ist also
    '''   immer halb oder ueberzaehlig und darf weg, sobald sie alt genug ist, dass kein laufender
    '''   Vorgang mehr an ihr schreibt. Der Beginn steht im Namen wie bei den Zwischendateien des
    '''   Einlesens (CardImportService): die Aenderungszeit einer FERTIGEN Kopie setzt das Kopieren
    '''   auf die der Quelle. Waehrend der Kopie ist sie dagegen frisch, und das Aufraeumen verlangt
    '''   beides: einen alten Namen und eine Schreibzeit, die seit der Frist ruht.
    '''
    ''' Damit das Verschieben nie selbst zur Kopie wird, die es nicht als solche kennt, geht das
    ''' Umbenennen ueber Directory.Move: das ist auch fuer Dateien ein reines rename und scheitert
    ''' ueber Dateisystemgrenzen, statt wie File.Move still zu kopieren und die Quelle zu loeschen.
    ''' Erst dann wird kopiert, in eine Teildatei, und die Quelle verschwindet erst, wenn die Kopie
    ''' umgehaengt ist.</summary>
    Public NotInheritable Class StagedFileTransfer

        ''' Kennung der Teildateien; davor steht der Name des Ziels, danach Sekunden und Zufall.
        Friend Const PartMarker As String = ".ferrumpix-part-"

        ''' Kennung der umbenannten Nachbardateien; sie bleiben, siehe oben.
        Friend Const StagingMarker As String = ".ferrumpix-new-"

        ''' Teildateien, deren Name und Schreibzeit beide aelter sind, gelten als Rest eines
        ''' abgebrochenen Laufs. Eine Stunde wie beim Einlesen; eine Kopie, die laenger braucht,
        ''' haelt ihre Schreibzeit frisch und bleibt deshalb stehen.
        Friend Shared ReadOnly StalePartAge As TimeSpan = TimeSpan.FromHours(1)

        Private Sub New()
        End Sub

        ''' <summary>Eine Teildatei neben <paramref name="target"/>, mit der Zeit ihres Entstehens im
        ''' Namen. Platz bleibt fuer die Endungen der Begleiter wie bei den uebrigen Nachbardateien.</summary>
        Public Shared Function PartPathFor(target As String) As String
            Return PartPathFor(target, DateTimeOffset.UtcNow)
        End Function

        Public Shared Function PartPathFor(target As String, createdAt As DateTimeOffset) As String
            Dim seconds = createdAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
            Return ImageProcessor.SiblingPath(target, PartMarker & seconds & "-", "", reserveBytes:=16)
        End Function

        ''' <summary>Eine Nachbardatei neben <paramref name="target"/>, die nur durch Umbenennen
        ''' entsteht.</summary>
        Friend Shared Function StagingPathFor(target As String) As String
            Return ImageProcessor.SiblingPath(target, StagingMarker, "", reserveBytes:=16)
        End Function

        ''' <summary>Benennt eine Datei oder einen Ordner um, ohne je zu kopieren. False, wenn das
        ''' nur ueber eine Kopie ginge (anderes Dateisystem oder Laufwerk): dann liegt die Quelle
        ''' unveraendert da und das Ziel fehlt weiter. Jeder andere Fehler fliegt weiter.</summary>
        Friend Shared Function TryRenameOnly(source As String, target As String) As Boolean
            Try
                Directory.Move(source, target)
                Return True
            Catch ex As IOException When Not (TypeOf ex Is FileNotFoundException OrElse TypeOf ex Is DirectoryNotFoundException) AndAlso
                                         (File.Exists(source) OrElse Directory.Exists(source)) AndAlso
                                         Not File.Exists(target) AndAlso Not Directory.Exists(target)
                Return False
            End Try
        End Function

        ''' <summary>Verschiebt oder kopiert eine Begleitdatei wie File.Move und File.Copy ohne
        ''' Ueberschreiben, aber unter dem Endnamen erscheint sie erst vollstaendig: auf demselben
        ''' Dateisystem ueber zwei Umbenennungen (die zweite ersetzt nie), sonst ueber eine Teildatei,
        ''' die erst nach dem Kopieren umgehaengt wird. Beim Verschieben ueber Laufwerke geht die
        ''' Quelle erst danach weg. Ist das Ziel belegt, fliegt eine IOException, und an der Quelle
        ''' wie am Ziel ist alles wie vorher.</summary>
        Public Shared Sub CarryFile(source As String, target As String, moveFile As Boolean)
            ' Fehlt die Quelle, dieselbe Ausnahme wie bei File.Move: die Aufrufer unterscheiden
            ' "inzwischen entfernt" von einem echten Fehler (Directory.Move meldete einen Ordner).
            If Not File.Exists(source) Then Throw New FileNotFoundException("Begleitdatei nicht gefunden", source)
            If moveFile Then
                Dim staging = StagingPathFor(target)
                If TryRenameOnly(source, staging) Then
                    Try
                        File.Move(staging, target, overwrite:=False)
                    Catch
                        Try
                            Directory.Move(staging, source)
                        Catch backEx As Exception
                            DiagnosticLogService.LogException("FileOps.CarryFile.Back", backEx)
                            Throw New IOException("Die Begleitdatei liegt noch unter " & staging, backEx)
                        End Try
                        Throw
                    End Try
                    Return
                End If
            End If
            Dim part = PartPathFor(target)
            Try
                File.Copy(source, part, overwrite:=False)
                File.Move(part, target, overwrite:=False)
            Catch
                Try
                    If File.Exists(part) Then File.Delete(part)
                Catch cleanupEx As Exception
                    DiagnosticLogService.LogException("FileOps.CarryFile.Cleanup", cleanupEx)
                End Try
                Throw
            End Try
            ' Die Kopie liegt vollstaendig am Ziel. Laesst sich die Quelle nicht entfernen, bleibt sie
            ' als zweite Fassung stehen und wird gemeldet; verloren ist nichts.
            If moveFile Then File.Delete(source)
        End Sub

        ''' <summary>Ob <paramref name="fileName"/> eine Teildatei dieser Anwendung ist, und wann sie
        ''' entstand: Punkt am Anfang, dann die Kennung, danach genau "SEKUNDEN-xxxxxxxx" in
        ''' Kleinbuchstaben. Alles andere ist eine fremde Datei, auch wenn sie aehnlich heisst.</summary>
        Public Shared Function TryParsePartName(fileName As String, ByRef createdAt As DateTimeOffset) As Boolean
            If String.IsNullOrEmpty(fileName) OrElse fileName(0) <> "."c Then Return False
            Dim at = fileName.LastIndexOf(PartMarker, StringComparison.Ordinal)
            If at <= 1 Then Return False
            Dim rest = fileName.Substring(at + PartMarker.Length)
            Dim dash = rest.IndexOf("-"c)
            If dash < 1 OrElse dash > 12 OrElse rest.Length - dash - 1 <> 8 Then Return False
            For i = 0 To dash - 1
                If rest(i) < "0"c OrElse rest(i) > "9"c Then Return False
            Next
            For i = dash + 1 To rest.Length - 1
                Dim c = rest(i)
                If Not ((c >= "0"c AndAlso c <= "9"c) OrElse (c >= "a"c AndAlso c <= "f"c)) Then Return False
            Next
            Dim seconds As Long
            If Not Long.TryParse(rest.Substring(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, seconds) Then Return False
            If seconds > 253402300799L Then Return False
            createdAt = DateTimeOffset.FromUnixTimeSeconds(seconds)
            Return True
        End Function

        ''' <summary>Raeumt liegengebliebene Teildateien (und Teilordner einer Ordnerkopie) aus einem
        ''' Ordner, nicht rekursiv. Nur, was genau dem Namensschema folgt, dessen Name aelter als
        ''' <see cref="StalePartAge"/> ist UND dessen letzte Schreibzeit ebenso (eine langsame Kopie,
        ''' die laenger als die Frist laeuft, schreibt noch und bleibt), und eine Datei nur, wenn sie
        ''' sich exklusiv oeffnen laesst; ein Verweis wird nie angefasst, und eine Nachbardatei aus
        ''' einem Umbenennen (".ferrumpix-new-") bleibt, weil sie die einzige Fassung sein kann.
        ''' Liefert die Zahl der entfernten Eintraege. Fehler werden protokolliert und halten den
        ''' Vorgang nicht auf: das Aufraeumen ist ein Nebenweg.</summary>
        Public Shared Function RemoveStaleParts(folder As String) As Integer
            Return RemoveStaleParts(folder, DateTimeOffset.UtcNow)
        End Function

        Public Shared Function RemoveStaleParts(folder As String, now As DateTimeOffset) As Integer
            If String.IsNullOrEmpty(folder) Then Return 0
            Dim removed = 0
            Try
                If Not Directory.Exists(folder) Then Return 0
                For Each entry In Directory.EnumerateFileSystemEntries(folder, ".*" & PartMarker & "*")
                    Try
                        Dim createdAt As DateTimeOffset
                        If Not TryParsePartName(Path.GetFileName(entry), createdAt) Then Continue For
                        If now - createdAt <= StalePartAge Then Continue For
                        Dim attributes = File.GetAttributes(entry)
                        If (attributes And FileAttributes.ReparsePoint) <> 0 Then Continue For
                        Dim isFolder = (attributes And FileAttributes.Directory) <> 0
                        ' Der Name sagt nur, wann die Kopie BEGANN. Eine große Datei über eine langsame
                        ' Freigabe kann länger als die Frist laufen; sie schreibt dann noch, und ihre
                        ' Schreibzeit ist frisch. Weg darf nur, was auch seit der Frist ruht.
                        If now - LatestWriteTime(entry, isFolder) <= StalePartAge Then Continue For
                        If isFolder Then
                            Directory.Delete(entry, recursive:=True)
                        Else
                            ' Zusätzlich: hält ein anderer Vorgang die Datei offen, lässt sie sich nicht
                            ' exklusiv öffnen, und sie bleibt. Unter Windows sperrt jedes offene Schreiben
                            ' so; unter Linux und macOS ist die Sperre nur beratend (sie greift nur gegen
                            ' eine Kopie, die selbst exklusiv geöffnet hat, und nicht auf jeder Freigabe),
                            ' deshalb ist die Schreibzeit oben das Hauptkriterium.
                            Try
                                Using probe As New FileStream(entry, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
                                End Using
                            Catch ex As IOException
                                Continue For
                            End Try
                            File.Delete(entry)
                        End If
                        removed += 1
                        DiagnosticLogService.LogAlways("FileOps.RemoveStaleParts", entry)
                    Catch ex As Exception
                        DiagnosticLogService.LogException("FileOps.RemoveStaleParts", ex)
                    End Try
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("FileOps.RemoveStaleParts", ex)
            End Try
            Return removed
        End Function

        ''' <summary>Die jüngste Schreibzeit einer Teildatei, bei einem Teilordner über ihn und
        ''' alles darin: eine laufende Ordnerkopie schreibt an einer Datei tief unten, der Ordner
        ''' selbst ändert sich nur, wenn ein Eintrag hinzukommt. File.Copy setzt die Zeit einer
        ''' fertigen Datei auf die der Quelle, eine fertige Datei zählt deshalb nur als alt; die
        ''' gerade geschriebene trägt die laufende Zeit.</summary>
        Private Shared Function LatestWriteTime(entry As String, isFolder As Boolean) As DateTimeOffset
            Dim latest = New DateTimeOffset(File.GetLastWriteTimeUtc(entry), TimeSpan.Zero)
            If Not isFolder Then Return latest
            For Each inner In Directory.EnumerateFileSystemEntries(entry, "*", SearchOption.AllDirectories)
                Dim written = New DateTimeOffset(File.GetLastWriteTimeUtc(inner), TimeSpan.Zero)
                If written > latest Then latest = written
            Next
            Return latest
        End Function

    End Class

End Namespace
