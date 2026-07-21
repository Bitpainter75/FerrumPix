Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Xml.Linq

Namespace Services

    ''' <summary>Liest die Katalogdaten aus einer XMP-Beistelldatei: Bewertung, Farbetikett, Stichworte.
    '''
    ''' Warum das nötig ist: <see cref="ExifService"/> kommt nur an XMP heran, das IM Bild steckt. RAW-
    ''' Dateien tragen praktisch nie eingebettetes XMP - Lightroom, darktable und digiKam legen alles in
    ''' eine Nachbardatei. Ohne diesen Dienst ist eine über Jahre gepflegte Sammlung für uns unsichtbar.
    '''
    ''' Bewusst NUR lesend: geschrieben wird weiterhin allein die Bewertung, und nur in eine bereits
    ''' vorhandene Datei (<see cref="ExifService.WriteXmpRatingSidecar"/>).</summary>
    Public Class XmpSidecarService

        Private Shared ReadOnly RdfNs As XNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#"
        Private Shared ReadOnly XmpNs As XNamespace = "http://ns.adobe.com/xap/1.0/"
        Private Shared ReadOnly DcNs As XNamespace = "http://purl.org/dc/elements/1.1/"
        Private Shared ReadOnly LrNs As XNamespace = "http://ns.adobe.com/lightroom/1.0/"

        Public Class XmpSidecarData
            Public Property Rating As Integer?
            Public Property ColorLabel As String = ""
            Public Property Keywords As New List(Of String)()

            Public ReadOnly Property IsEmpty As Boolean
                Get
                    Return Not Rating.HasValue AndAlso String.IsNullOrEmpty(ColorLabel) AndAlso Keywords.Count = 0
                End Get
            End Property
        End Class

        ''' <summary>Der Pfad der Beistelldatei zu <paramref name="imagePath"/>, oder Nothing.
        '''
        ''' Zwei Konventionen sind im Umlauf und beide sind verbreitet: Adobe ERSETZT die Endung
        ''' ("foto.cr2" -> "foto.xmp"), darktable/digiKam/exiftool HÄNGEN AN ("foto.cr2.xmp"). Wer nur
        ''' <c>Path.ChangeExtension</c> benutzt, findet die halbe Welt nicht. Angehängt wird zuerst
        ''' geprüft, weil diese Form eindeutig zur Bilddatei gehört - "foto.xmp" könnte daneben auch zu
        ''' "foto.jpg" gehören, wenn beide im selben Ordner liegen.</summary>
        Public Shared Function FindSidecar(imagePath As String) As String
            If String.IsNullOrWhiteSpace(imagePath) Then Return Nothing
            Try
                Dim appended = imagePath & ".xmp"
                If File.Exists(appended) Then Return appended
                Dim replaced = Path.ChangeExtension(imagePath, ".xmp")
                If Not String.IsNullOrEmpty(replaced) AndAlso File.Exists(replaced) Then Return replaced
            Catch
            End Try
            Return Nothing
        End Function

        ''' <summary>Beide Namensformen als Kandidaten - für den Abgleich gegen eine bereits eingelesene
        ''' Ordnerliste, ohne je Datei zweimal auf die Platte zu greifen (siehe GalleryViewModel).</summary>
        Public Shared Function SidecarCandidates(imagePath As String) As String()
            If String.IsNullOrWhiteSpace(imagePath) Then Return Array.Empty(Of String)()
            Try
                Dim replaced = Path.ChangeExtension(imagePath, ".xmp")
                If String.IsNullOrEmpty(replaced) Then Return {imagePath & ".xmp"}
                Return {imagePath & ".xmp", replaced}
            Catch
                Return Array.Empty(Of String)()
            End Try
        End Function

        ''' <summary>Nothing, wenn die Datei fehlt oder kein lesbares XMP ist. Ein kaputtes Sidecar darf
        ''' den Scan nicht aufhalten - es gibt in freier Wildbahn abgeschnittene und doppelt geschriebene
        ''' Dateien.</summary>
        Public Shared Function ReadSidecar(sidecarPath As String) As XmpSidecarData
            If String.IsNullOrWhiteSpace(sidecarPath) OrElse Not File.Exists(sidecarPath) Then Return Nothing
            Dim doc As XDocument
            Try
                Dim settings As New Xml.XmlReaderSettings With {
                    .DtdProcessing = Xml.DtdProcessing.Ignore,
                    .XmlResolver = Nothing
                }
                Using stream = File.OpenRead(sidecarPath)
                    Using reader = Xml.XmlReader.Create(stream, settings)
                        doc = XDocument.Load(reader, LoadOptions.None)
                    End Using
                End Using
            Catch
                Return Nothing
            End Try

            Dim result As New XmpSidecarData()
            Try
                For Each description In doc.Descendants(RdfNs + "Description")
                    ' Beide Schreibweisen abdecken: derselbe Wert steht je nach Erzeuger als ATTRIBUT
                    ' am rdf:Description oder als KINDELEMENT darunter. Lightroom bevorzugt Attribute,
                    ' exiftool schreibt Elemente - wer nur eine Form liest, verliert die halbe Welt.
                    If Not result.Rating.HasValue Then
                        Dim ratingText = ReadValue(description, XmpNs + "Rating")
                        Dim parsed As Double
                        If Not String.IsNullOrWhiteSpace(ratingText) AndAlso
                           Double.TryParse(ratingText, NumberStyles.Float, CultureInfo.InvariantCulture, parsed) Then
                            ' Lightroom kennt "abgelehnt" als Bewertung -1; unsere Skala geht bei 0 los.
                            result.Rating = CInt(Math.Max(0, Math.Min(5, Math.Round(parsed))))
                        End If
                    End If

                    If String.IsNullOrEmpty(result.ColorLabel) Then
                        result.ColorLabel = MapLabel(ReadValue(description, XmpNs + "Label"))
                    End If

                    CollectKeywords(description, DcNs + "subject", result.Keywords)
                    CollectKeywords(description, LrNs + "hierarchicalSubject", result.Keywords)
                Next
            Catch
                Return Nothing
            End Try

            Return If(result.IsEmpty, Nothing, result)
        End Function

        Private Shared Function ReadValue(description As XElement, name As XName) As String
            Dim attr = description.Attribute(name)
            If attr IsNot Nothing Then Return attr.Value.Trim()
            Dim child = description.Element(name)
            If child Is Nothing Then Return ""
            ' Auch ein Einzelwert kann in eine rdf:Alt/rdf:Seq verpackt sein (Sprachvarianten).
            Dim li = child.Descendants(RdfNs + "li").FirstOrDefault()
            Return If(li IsNot Nothing, li.Value.Trim(), child.Value.Trim())
        End Function

        ''' Stichworte liegen als rdf:Bag/rdf:Seq aus rdf:li vor, nie als einfacher Text.
        Private Shared Sub CollectKeywords(description As XElement, name As XName, target As List(Of String))
            Dim container = description.Element(name)
            If container Is Nothing Then Return
            For Each li In container.Descendants(RdfNs + "li")
                Dim value = If(li.Value, "").Trim()
                If value.Length = 0 Then Continue For

                ' Hierarchische Stichworte kommen als "Reise|Italien|Rom". Unsere Stichworte sind flach,
                ' deshalb nur das letzte Glied - der ganze Pfad wäre als ein Wort unbrauchbar.
                Dim leaf = value.Split("|"c).Last().Trim()
                If leaf.Length = 0 Then Continue For

                ' KOMMA IST UNSER TRENNZEICHEN: LibraryService.SetTags fügt mit Komma zusammen und
                ' ParseTags trennt daran. Ein Stichwort mit Komma zerfiele beim nächsten Lesen in zwei.
                leaf = leaf.Replace(","c, " "c).Trim()
                If leaf.Length = 0 Then Continue For

                If Not target.Any(Function(t) String.Equals(t, leaf, StringComparison.OrdinalIgnoreCase)) Then
                    target.Add(leaf)
                End If
            Next
        End Sub

        ''' <summary>xmp:Label führt einen freien Text; Lightroom und die meisten anderen schreiben dort
        ''' Farbwörter. Unsere ColorLabel-Spalte hält dagegen Hex-Werte aus der Akzentfarben-Palette
        ''' (siehe AppSettingsService.NormalizeAccentColor), und der Galerie-Filter vergleicht sie
        ''' ORDINAL - die Großschreibung hier ist deshalb Teil der Zusicherung, nicht Kosmetik.
        ''' Unbekannte Beschriftungen werden verworfen statt geraten.</summary>
        Public Shared Function MapLabel(label As String) As String
            If String.IsNullOrWhiteSpace(label) Then Return ""
            Select Case label.Trim().ToLowerInvariant()
                Case "red", "rot", "rojo", "rouge", "rosso" : Return "#E74C3C"
                Case "yellow", "gelb", "amarillo", "jaune", "giallo" : Return "#FACC15"
                Case "green", "grün", "gruen", "verde", "vert" : Return "#22C55E"
                Case "blue", "blau", "azul", "bleu", "blu" : Return "#3B82F6"
                Case "purple", "lila", "violett", "morado", "violet", "viola" : Return "#8B5CF6"
                Case "orange", "naranja" : Return "#F08A1A"
                Case "pink", "rosa", "rose" : Return "#F03B88"
                Case Else : Return ""
            End Select
        End Function

        ''' <summary>Umkehrung von <see cref="MapLabel"/> für den Schreibweg: unser Hex-Akzent → das
        ''' englische Lightroom-Farbwort (das andere Programme in xmp:Label erwarten). Round-Trip-sicher:
        ''' das zurückgegebene Wort ergibt über MapLabel wieder exakt denselben Hex. Leer, wenn der Hex
        ''' kein Palettenwert ist (dann wird kein xmp:Label geschrieben, statt zu raten). Vergleich über
        ''' die 6 RGB-Hexstellen, groß-/kleinschreib- und Alpha-tolerant.</summary>
        Public Shared Function LabelToXmpWord(colorHex As String) As String
            If String.IsNullOrWhiteSpace(colorHex) Then Return ""
            Dim hex = colorHex.Trim().TrimStart("#"c).ToUpperInvariant()
            If hex.Length = 8 Then hex = hex.Substring(2) ' AARRGGBB → RRGGBB
            If hex.Length <> 6 Then Return ""
            Select Case hex
                Case "E74C3C" : Return "Red"
                Case "FACC15" : Return "Yellow"
                Case "22C55E" : Return "Green"
                Case "3B82F6" : Return "Blue"
                Case "8B5CF6" : Return "Purple"
                Case "F08A1A" : Return "Orange"
                Case "F03B88" : Return "Pink"
                Case Else : Return ""
            End Select
        End Function

    End Class

End Namespace
