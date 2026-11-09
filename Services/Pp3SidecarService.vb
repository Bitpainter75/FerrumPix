Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text

Namespace Services

    ''' <summary>Liest die Bearbeitungsdatei eines fremden RAW-Entwicklers (".pp3" neben dem Bild,
    ''' "foto.orf.pp3") fuer den Katalog: Stichwoerter, Bewertung und Farbetikett. Dasselbe fuer
    ''' seinen Ableger, der dieselben Gruppen in eine ".arp" schreibt; Schnappschuesse stehen dort in
    ''' Gruppen mit vorangestelltem Namen ("Snapshot_1 IPTC") und bleiben aussen vor, es zaehlt der
    ''' Hauptstand.
    '''
    ''' WARUM: Dieser Entwickler legt die drei ab Werk NUR dort ab. In eine .xmp schreibt er sie erst,
    ''' wenn sein XMP-Abgleich auf Lesen und Schreiben steht, und in die Bilddatei nur beim Export.
    ''' Wer dort verschlagwortet, fand seine RAWs in FerrumPix deshalb ueber kein einziges Stichwort,
    ''' gezaehlt wurden nur die exportierten JPEGs (Nutzerbefund).
    '''
    ''' NUR LESEN, und nur mit eingeschalteter Einstellung (<see cref="AppSettings.ReadPp3Sidecars"/>,
    ''' ab Werk aus). Die Datei ist das Bearbeitungsrezept des anderen Programms; FerrumPix schreibt
    ''' nie hinein und legt nie eine an.
    '''
    ''' Das Format ist eine GLib-Schluesseldatei: Gruppen in eckigen Klammern, "Schluessel=Wert",
    ''' Kommentare mit "#". Gebraucht werden "[IPTC] Keywords" (Liste, getrennt durch ";"),
    ''' "[General] Rank" (0 bis 5) und "[General] ColorLabel" (0 keins, 1 rot, 2 gelb, 3 gruen,
    ''' 4 blau, 5 lila).</summary>
    Public NotInheritable Class Pp3SidecarService

        Public Const Extension As String = ".pp3"
        Public Const ArpExtension As String = ".arp"

        Private Sub New()
        End Sub

        Public NotInheritable Class Pp3Data
            Public ReadOnly Property Keywords As New List(Of String)()
            ''' <summary>1 bis 5, oder Nothing, wenn die Datei keine Bewertung traegt.</summary>
            Public Property Rating As Integer?
            ''' <summary>Der Hex-Wert unserer Palette, oder leer.</summary>
            Public Property ColorLabel As String = ""
            ''' <summary>1 behalten, -1 verworfen, oder Nothing. Kommt nur aus dem Katalog des Referenzprogramms;
            ''' die .pp3 und die .arp kennen keine Markierung.</summary>
            Public Property PickState As Integer?
        End Class

        ''' <summary>Ist das Einlesen eingeschaltet?</summary>
        Public Shared ReadOnly Property IsEnabled As Boolean
            Get
                Try
                    Return AppSettingsService.Load().ReadPp3Sidecars
                Catch
                    Return False
                End Try
            End Get
        End Property

        ''' <summary>Der Pfad, unter dem die Datei laege: der volle Bildname mit ".pp3" dahinter.</summary>
        Public Shared Function PathFor(imagePath As String) As String
            If String.IsNullOrWhiteSpace(imagePath) Then Return ""
            Return imagePath & Extension
        End Function

        ''' <summary>Der Pfad der ".arp" des Ablegers, gebildet wie bei der ".pp3".</summary>
        Public Shared Function ArpPathFor(imagePath As String) As String
            If String.IsNullOrWhiteSpace(imagePath) Then Return ""
            Return imagePath & ArpExtension
        End Function

        ''' <summary>Die Daten zur Datei, oder Nothing: Einstellung aus, keine Datei, nicht lesbar.
        ''' Liegen beide Dateien da, werden die Stichwoerter vereinigt, Bewertung und Etikett kommen
        ''' zuerst aus der ".pp3".
        '''
        ''' DER LIGHTROOM-KATALOG haengt an derselben Einstellung (ForeignCatalogService): kennt er
        ''' das Foto, kommen seine Angaben dazu, nach denen der Dateien neben dem Bild, die die
        ''' speziellere Quelle sind. So gehen sie ueber dieselben Wege und Regeln in den Katalog.</summary>
        Public Shared Function Read(imagePath As String) As Pp3Data
            If Not IsEnabled Then Return Nothing
            Dim result = Merge(ReadOne(PathFor(imagePath)), ReadOne(ArpPathFor(imagePath)))
            Return Merge(result, FromForeignCatalog(imagePath))
        End Function

        ''' <summary>Vereinigt zwei Ergebnisse; das erste geht bei Bewertung, Etikett und Markierung vor.</summary>
        Private Shared Function Merge(first As Pp3Data, second As Pp3Data) As Pp3Data
            If first Is Nothing Then Return second
            If second Is Nothing Then Return first
            For Each keyword In second.Keywords
                If Not first.Keywords.Contains(keyword, StringComparer.OrdinalIgnoreCase) Then first.Keywords.Add(keyword)
            Next
            If Not first.Rating.HasValue Then first.Rating = second.Rating
            If String.IsNullOrEmpty(first.ColorLabel) Then first.ColorLabel = second.ColorLabel
            If Not first.PickState.HasValue Then first.PickState = second.PickState
            Return first
        End Function

        Private Shared Function FromForeignCatalog(imagePath As String) As Pp3Data
            Dim entry = ForeignCatalogService.Lookup(imagePath)
            If entry Is Nothing Then Return Nothing
            Dim data As New Pp3Data With {
                .Rating = entry.Rating,
                .ColorLabel = If(entry.ColorLabel, ""),
                .PickState = entry.PickState}
            data.Keywords.AddRange(entry.Keywords)
            Return data
        End Function

        Private Shared Function ReadOne(path As String) As Pp3Data
            If String.IsNullOrEmpty(path) Then Return Nothing
            Try
                If Not File.Exists(path) Then Return Nothing
                Return Parse(File.ReadAllText(path, Encoding.UTF8))
            Catch ex As Exception
                DiagnosticLogService.LogException("Pp3.Lesen", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Der Anteil fuer den Frische-Stempel der Beistelldateien, oder leer. Muss
        ''' zeichengleich zu dem sein, was <see cref="FolderSidecarStamps"/> aus seiner Auflistung
        ''' bildet; beide gehen deshalb ueber <see cref="ComposeStamp"/>.</summary>
        Public Shared Function StampFor(imagePath As String) As String
            If Not IsEnabled Then Return ""
            Return ComposeStamp(FileStamp(PathFor(imagePath)), FileStamp(ArpPathFor(imagePath)))
        End Function

        ''' <summary>Setzt die Zeitstempel beider Dateien zu EINEM Anteil zusammen; leer, wenn keine
        ''' von beiden da ist. Mit einem eingestellten Katalog des Referenzprogramms kommt dessen Stempel dazu,
        ''' fuer jedes Bild derselbe (ForeignCatalogService.StampForAll): aendert sich der Katalog,
        ''' wird neu eingelesen.</summary>
        Public Shared Function ComposeStamp(pp3Stamp As String, arpStamp As String) As String
            Dim stamp = If(String.IsNullOrEmpty(arpStamp), If(pp3Stamp, ""), If(pp3Stamp, "") & "+arp:" & arpStamp)
            Dim catalog = ForeignCatalogService.StampForAll()
            If catalog.Length = 0 Then Return stamp
            Return stamp & "+" & catalog
        End Function

        Private Shared Function FileStamp(path As String) As String
            Try
                If String.IsNullOrEmpty(path) OrElse Not File.Exists(path) Then Return ""
                Return File.GetLastWriteTime(path).ToString("o")
            Catch
                Return ""
            End Try
        End Function

        ''' <summary>Zerlegt den Text einer .pp3. Unbekanntes wird uebergangen.</summary>
        Public Shared Function Parse(text As String) As Pp3Data
            Dim result As New Pp3Data()
            If String.IsNullOrEmpty(text) Then Return result
            Dim group = ""
            For Each rawLine In text.Split({vbLf}, StringSplitOptions.None)
                Dim line = rawLine.TrimEnd(ChrW(13)).Trim()
                If line.Length = 0 OrElse line.StartsWith("#", StringComparison.Ordinal) Then Continue For
                If line.StartsWith("[", StringComparison.Ordinal) AndAlso line.EndsWith("]", StringComparison.Ordinal) Then
                    group = line.Substring(1, line.Length - 2).Trim()
                    Continue For
                End If
                Dim eq = line.IndexOf("="c)
                If eq <= 0 Then Continue For
                Dim key = line.Substring(0, eq).Trim()
                Dim value = line.Substring(eq + 1).TrimStart()

                If String.Equals(group, "IPTC", StringComparison.Ordinal) AndAlso
                   String.Equals(key, "Keywords", StringComparison.Ordinal) Then
                    For Each keyword In SplitList(value)
                        Dim trimmed = keyword.Trim()
                        If trimmed.Length > 0 AndAlso Not result.Keywords.Contains(trimmed, StringComparer.OrdinalIgnoreCase) Then
                            result.Keywords.Add(trimmed)
                        End If
                    Next
                ElseIf String.Equals(group, "General", StringComparison.Ordinal) Then
                    Dim number As Integer
                    If Not Integer.TryParse(Unescape(value).Trim(), Globalization.NumberStyles.Integer,
                                            Globalization.CultureInfo.InvariantCulture, number) Then Continue For
                    If String.Equals(key, "Rank", StringComparison.Ordinal) Then
                        ' 0 heisst unbewertet; das fuellt nichts.
                        result.Rating = If(number >= 1 AndAlso number <= 5, number, CType(Nothing, Integer?))
                    ElseIf String.Equals(key, "ColorLabel", StringComparison.Ordinal) Then
                        result.ColorLabel = XmpSidecarService.MapLabel(LabelWord(number))
                    End If
                End If
            Next
            Return result
        End Function

        Private Shared Function LabelWord(number As Integer) As String
            Select Case number
                Case 1 : Return "red"
                Case 2 : Return "yellow"
                Case 3 : Return "green"
                Case 4 : Return "blue"
                Case 5 : Return "purple"
                Case Else : Return ""
            End Select
        End Function

        ''' <summary>Eine GLib-Liste: Eintraege durch ";" getrennt, ein maskiertes "\;" gehoert zum
        ''' Eintrag. Das letzte ";" schliesst nur ab und erzeugt keinen leeren Eintrag.</summary>
        Private Shared Function SplitList(value As String) As List(Of String)
            Dim items As New List(Of String)()
            Dim current As New StringBuilder()
            Dim i = 0
            While i < value.Length
                Dim c = value(i)
                If c = "\"c AndAlso i + 1 < value.Length Then
                    current.Append(c).Append(value(i + 1))
                    i += 2
                    Continue While
                End If
                If c = ";"c Then
                    items.Add(Unescape(current.ToString()))
                    current.Clear()
                Else
                    current.Append(c)
                End If
                i += 1
            End While
            If current.Length > 0 Then items.Add(Unescape(current.ToString()))
            Return items
        End Function

        ''' <summary>Die Maskierungen der GLib-Schluesseldatei: \s, \n, \t, \r, \\ und \;.</summary>
        Private Shared Function Unescape(value As String) As String
            If value.IndexOf("\"c) < 0 Then Return value
            Dim sb As New StringBuilder(value.Length)
            Dim i = 0
            While i < value.Length
                Dim c = value(i)
                If c = "\"c AndAlso i + 1 < value.Length Then
                    Dim n = value(i + 1)
                    Select Case n
                        Case "s"c : sb.Append(" "c)
                        Case "n"c : sb.Append(vbLf)
                        Case "t"c : sb.Append(vbTab)
                        Case "r"c : sb.Append(vbCr)
                        Case Else : sb.Append(n)
                    End Select
                    i += 2
                Else
                    sb.Append(c)
                    i += 1
                End If
            End While
            Return sb.ToString()
        End Function

    End Class

End Namespace
