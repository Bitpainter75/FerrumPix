Imports System
Imports System.Collections.Generic
Imports System.IO

Namespace Services

    ''' <summary>Die Stempel der Beistelldateien eines Ordners, aus EINER Auflistung statt aus
    ''' Proben je Bild.
    '''
    ''' <para><see cref="LibraryService.SidecarStamp"/> fragt je Bild drei Namen ab
    ''' ("foto.jpg.xmp", "foto.xmp", "foto.jpg.fpxmp"), und fast immer ist keiner davon da. Auf
    ''' einer Netzfreigabe ist jede Probe ein Weg zum Server: gemessen an 1015 JPEG ueber SMB waren
    ''' das drei von fuenf Dateizugriffen je Bild und der groesste Teil eines unveraenderten
    ''' Indexlaufs. Eine Auflistung je Ordner beantwortet dieselbe Frage fuer alle Bilder
    ''' darin.</para>
    '''
    ''' <para>Galerie und Katalogindex gehen beide hierueber. <see cref="StampFor"/> muss
    ''' ZEICHENGLEICH zu <see cref="LibraryService.SidecarStamp"/> sein: das ist die Gegenseite
    ''' desselben Vergleichs, und ein Auseinanderlaufen liesse jeden Lauf den Ordner komplett neu
    ''' einlesen, ohne dass etwas darauf hindeutet.</para></summary>
    Public NotInheritable Class FolderSidecarStamps

        ''' Nur der Ordner selbst, und case-insensitiv: unter Linux matcht das Suchmuster sonst
        ''' case-sensitiv, ".XMP" kaeme nicht vor (kommt bei Exporten aus Windows-Programmen aber vor).
        ''' IgnoreInaccessible AUS: die Vorgabe ist an, und ein Ordner ohne Leserecht lieferte damit
        ''' still eine leere Liste statt eines Fehlers - genau der Fall, den Read unten abfangen muss.
        Private Shared ReadOnly SearchOptions As New EnumerationOptions With {
            .RecurseSubdirectories = False,
            .MatchCasing = MatchCasing.CaseInsensitive,
            .IgnoreInaccessible = False
        }

        Private ReadOnly _xmp As New Dictionary(Of String, String)(PathIdentity.Comparer)
        Private ReadOnly _fpxmp As New Dictionary(Of String, String)(PathIdentity.Comparer)
        Private ReadOnly _pp3 As New Dictionary(Of String, String)(PathIdentity.Comparer)
        Private ReadOnly _rrdata As New Dictionary(Of String, String)(PathIdentity.Comparer)
        Private _listingFailed As Boolean
        ''' Die RAW des Ordners nach Stamm, erst gelesen, wenn ein Nicht-RAW-Bild eine ersetzte Form
        ''' "foto.xmp" hat (siehe OwnedByRawSibling). Nothing, wenn das Lesen scheiterte.
        Private ReadOnly _rawByStem As Lazy(Of Dictionary(Of String, List(Of String)))

        ''' <summary>Der Ordner, fuer den die Auflistung gilt.</summary>
        Public ReadOnly Property FolderPath As String

        Private Sub New(folderPath As String)
            Me.FolderPath = folderPath
            _rawByStem = New Lazy(Of Dictionary(Of String, List(Of String)))(AddressOf ReadRawByStem)
        End Sub

        ''' <summary>Listet die Beistelldateien des Ordners auf.
        '''
        ''' <para>Scheitert das, auch mittendrin, gilt die Liste NICHT als leer, sondern als
        ''' unbrauchbar: <see cref="StampFor"/> probt dann wieder je Bild. Eine leere oder halbe Liste
        ''' saehe falsch aus, ohne es zu merken - steht im Katalog "keine Beistelldatei" und ist seither
        ''' eine dazugekommen, die die Liste nicht mehr erfasst hat, gaelte das Bild als unveraendert
        ''' und die neue .xmp bliebe ungelesen.</para></summary>
        Public Shared Function Read(folderPath As String) As FolderSidecarStamps
            Dim result As New FolderSidecarStamps(If(folderPath, ""))
            If String.IsNullOrWhiteSpace(folderPath) Then Return result
            Try
                ' Kleingeschriebener Schluessel -> anders geschriebene Datei, die ihn gerade belegt.
                Dim otherCasing As New Dictionary(Of String, String)(StringComparer.Ordinal)
                For Each sidecar In Directory.EnumerateFiles(folderPath, "*.xmp", SearchOptions)
                    ' ".fpxmp" endet nicht auf ".xmp" und faellt hier nicht mit hinein - der Vergleich
                    ' steht trotzdem da, weil ein Treffer den Stempel still verfaelschen wuerde.
                    If sidecar.EndsWith(RawSidecarService.Extension, StringComparison.OrdinalIgnoreCase) Then Continue For
                    ' Abgelegt unter der kleingeschriebenen Endung, denn so fragt StampFor nach
                    ' (SidecarCandidates). Liegen beide Schreibweisen da, gilt die kleingeschriebene,
                    ' wie bei XmpSidecarService.FindSidecar; sonst die erste in Ordnungsfolge.
                    Dim key = sidecar.Substring(0, sidecar.Length - 4) & ".xmp"
                    Dim isLower = String.Equals(key, sidecar, StringComparison.Ordinal)
                    Dim known As String = Nothing
                    If Not isLower AndAlso otherCasing.TryGetValue(key, known) AndAlso
                       String.CompareOrdinal(known, sidecar) < 0 Then Continue For
                    If Not isLower AndAlso result._xmp.ContainsKey(key) AndAlso Not otherCasing.ContainsKey(key) Then Continue For
                    result._xmp(key) = File.GetLastWriteTime(sidecar).ToString("o")
                    If isLower Then otherCasing.Remove(key) Else otherCasing(key) = sidecar
                Next
                ' Zweite Auflistung fuer die eigenen Rezepte: Vorhandensein UND Aenderungszeit gehoeren
                ' in den Stempel. So werden extern geaenderte Katalogwerte aus .fpxmp ebenso erkannt
                ' wie das Loeschen einer Beistelldatei.
                For Each rezept In Directory.EnumerateFiles(folderPath, "*" & RawSidecarService.Extension, SearchOptions)
                    result._fpxmp(rezept) = File.GetLastWriteTime(rezept).ToString("o")
                Next
                ' Die .pp3 eines fremden Entwicklers nur mit eingeschalteter Einstellung - sonst
                ' gehoert sie nicht in den Stempel (siehe LibraryService.SidecarStamp).
                If Pp3SidecarService.IsEnabled Then
                    For Each pp3 In Directory.EnumerateFiles(folderPath, "*" & Pp3SidecarService.Extension, SearchOptions)
                        result._pp3(pp3) = File.GetLastWriteTime(pp3).ToString("o")
                    Next
                    For Each arp In Directory.EnumerateFiles(folderPath, "*" & Pp3SidecarService.ArpExtension, SearchOptions)
                        result._pp3(arp) = File.GetLastWriteTime(arp).ToString("o")
                    Next
                    ' Die .rrdata eines anderen Entwicklers am selben Schalter, wie sie auch nur
                    ' damit gelesen wird (RrdataSidecarService). Virtuelle Kopien kommen mit in die
                    ' Liste, gefragt wird aber nur nach der Datei des Originals.
                    For Each rrdata In Directory.EnumerateFiles(folderPath, "*" & RrdataSidecarService.Extension, SearchOptions)
                        result._rrdata(rrdata) = File.GetLastWriteTime(rrdata).ToString("o")
                    Next
                End If
            Catch
                result._listingFailed = True
                result._xmp.Clear()
                result._fpxmp.Clear()
                result._pp3.Clear()
                result._rrdata.Clear()
            End Try
            Return result
        End Function

        ''' <summary>Der Stempel eines Bildes in diesem Ordner, gebildet wie
        ''' <see cref="LibraryService.SidecarStamp"/>: beide Namensformen der XMP in derselben
        ''' Reihenfolge wie <see cref="XmpSidecarService.FindSidecar"/>, dazu die .fpxmp. Leer, wenn
        ''' es keine Beistelldatei gibt.</summary>
        Public Function StampFor(imagePath As String) As String
            If _listingFailed Then Return LibraryService.SidecarStamp(imagePath)
            Dim xmpStamp = ""
            For Each candidate In XmpSidecarService.SidecarCandidates(imagePath)
                Dim stamp As String = Nothing
                If _xmp.TryGetValue(candidate, stamp) AndAlso Not OwnedByRawSibling(candidate, imagePath) Then
                    xmpStamp = stamp
                    Exit For
                End If
            Next
            Dim fpxmpStamp As String = Nothing
            If Not _fpxmp.TryGetValue(RawSidecarService.SidecarPathFor(imagePath), fpxmpStamp) Then fpxmpStamp = ""
            Dim pp3Only As String = Nothing
            If Not _pp3.TryGetValue(Pp3SidecarService.PathFor(imagePath), pp3Only) Then pp3Only = ""
            Dim arpOnly As String = Nothing
            If Not _pp3.TryGetValue(Pp3SidecarService.ArpPathFor(imagePath), arpOnly) Then arpOnly = ""
            Dim pp3Stamp = Pp3SidecarService.ComposeStamp(pp3Only, arpOnly)
            Dim rrdataStamp As String = Nothing
            If Not _rrdata.TryGetValue(RrdataSidecarService.PathFor(imagePath), rrdataStamp) Then rrdataStamp = ""
            Return LibraryService.ComposeSidecarStamp(xmpStamp, fpxmpStamp, pp3Stamp, rrdataStamp)
        End Function

        ''' <summary>Ist <paramref name="candidate"/> die ersetzte Form ("foto.xmp") und liegt neben dem
        ''' Nicht-RAW-Bild eine RAW desselben Stamms? Dann gehoert sie der RAW und steht nicht im
        ''' Stempel des Bildes, so wie <see cref="XmpSidecarService.FindSidecar"/> sie ihm nicht
        ''' liefert. Dieselbe Frage wie dort (RawSidecarService.HasCameraRawStemSiblingAmong), nur aus
        ''' einer Auflistung je Ordner statt je Bild. Scheitert sie, fragt das Bild selbst, wie dort.</summary>
        Private Function OwnedByRawSibling(candidate As String, imagePath As String) As Boolean
            If String.Equals(candidate, imagePath & ".xmp", StringComparison.Ordinal) Then Return False
            If RawPreviewService.IsCameraRaw(imagePath) Then Return False
            Dim byStem = _rawByStem.Value
            If byStem Is Nothing Then Return RawSidecarService.HasCameraRawStemSibling(imagePath)
            Dim sameStem As List(Of String) = Nothing
            If Not byStem.TryGetValue(Path.GetFileNameWithoutExtension(imagePath), sameStem) Then Return False
            Return RawSidecarService.HasCameraRawStemSiblingAmong(sameStem, imagePath)
        End Function

        Private Function ReadRawByStem() As Dictionary(Of String, List(Of String))
            Dim byStem As New Dictionary(Of String, List(Of String))(PathIdentity.Comparer)
            Try
                For Each filePath In Directory.EnumerateFiles(FolderPath)
                    If Not RawPreviewService.IsCameraRaw(filePath) Then Continue For
                    Dim stem = Path.GetFileNameWithoutExtension(filePath)
                    Dim paths As List(Of String) = Nothing
                    If Not byStem.TryGetValue(stem, paths) Then
                        paths = New List(Of String)()
                        byStem(stem) = paths
                    End If
                    paths.Add(filePath)
                Next
            Catch ex As Exception
                DiagnosticLogService.LogException("Sidecar.FolderRawListing", ex)
                Return Nothing
            End Try
            Return byStem
        End Function

    End Class

End Namespace
