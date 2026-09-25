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
        Private _listingFailed As Boolean

        ''' <summary>Der Ordner, fuer den die Auflistung gilt.</summary>
        Public ReadOnly Property FolderPath As String

        Private Sub New(folderPath As String)
            Me.FolderPath = folderPath
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
                For Each sidecar In Directory.EnumerateFiles(folderPath, "*.xmp", SearchOptions)
                    ' ".fpxmp" endet nicht auf ".xmp" und faellt hier nicht mit hinein - der Vergleich
                    ' steht trotzdem da, weil ein Treffer den Stempel still verfaelschen wuerde.
                    If sidecar.EndsWith(RawSidecarService.Extension, StringComparison.OrdinalIgnoreCase) Then Continue For
                    result._xmp(sidecar) = File.GetLastWriteTime(sidecar).ToString("o")
                Next
                ' Zweite Auflistung fuer die eigenen Rezepte: Vorhandensein UND Aenderungszeit gehoeren
                ' in den Stempel. So werden extern geaenderte Katalogwerte aus .fpxmp ebenso erkannt
                ' wie das Loeschen einer Beistelldatei.
                For Each rezept In Directory.EnumerateFiles(folderPath, "*" & RawSidecarService.Extension, SearchOptions)
                    result._fpxmp(rezept) = File.GetLastWriteTime(rezept).ToString("o")
                Next
            Catch
                result._listingFailed = True
                result._xmp.Clear()
                result._fpxmp.Clear()
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
                If _xmp.TryGetValue(candidate, stamp) Then
                    xmpStamp = stamp
                    Exit For
                End If
            Next
            Dim fpxmpStamp As String = Nothing
            If Not _fpxmp.TryGetValue(RawSidecarService.SidecarPathFor(imagePath), fpxmpStamp) Then fpxmpStamp = ""
            If String.IsNullOrEmpty(xmpStamp) AndAlso String.IsNullOrEmpty(fpxmpStamp) Then Return ""
            Return xmpStamp & If(String.IsNullOrEmpty(fpxmpStamp), "|-", "|fpxmp:" & fpxmpStamp)
        End Function

    End Class

End Namespace
