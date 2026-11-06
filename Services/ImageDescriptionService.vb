Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Xml.Linq

Namespace Services

    ''' <summary>Die beschreibenden Felder eines Bildes, wie sie IPTC Core und XMP fuehren. Bewusst
    ''' nur die fuenf, die ein Bild BESCHREIBEN; Stichwoerter, Urheberrechtshinweis, Aufnahmeort und
    ''' Aufnahmezeit haben ihre eigenen Wege und bleiben dort.</summary>
    Public Enum ImageDescriptionField
        Title
        Headline
        Description
        Creator
        Credit
    End Enum

    ''' <summary>Die fuenf Felder als Werte. Leer heisst: steht nicht in der Datei.</summary>
    Public Class ImageDescription
        Public Property Title As String = ""
        Public Property Headline As String = ""
        Public Property Description As String = ""
        ''' <summary>Mehrere Urheber stehen mit Semikolon getrennt in EINER Zeile. In der Datei sind
        ''' es einzelne Eintraege (dc:creator ist eine Folge, IPTC 2:80 wiederholbar).</summary>
        Public Property Creator As String = ""
        Public Property Credit As String = ""

        Public Shared ReadOnly AllFields As ImageDescriptionField() = {
            ImageDescriptionField.Title, ImageDescriptionField.Headline, ImageDescriptionField.Description,
            ImageDescriptionField.Creator, ImageDescriptionField.Credit}

        Public Function GetValue(field As ImageDescriptionField) As String
            Select Case field
                Case ImageDescriptionField.Title : Return If(Title, "")
                Case ImageDescriptionField.Headline : Return If(Headline, "")
                Case ImageDescriptionField.Description : Return If(Description, "")
                Case ImageDescriptionField.Creator : Return If(Creator, "")
                Case ImageDescriptionField.Credit : Return If(Credit, "")
                Case Else : Return ""
            End Select
        End Function

        Public Sub SetValue(field As ImageDescriptionField, value As String)
            Select Case field
                Case ImageDescriptionField.Title : Title = If(value, "")
                Case ImageDescriptionField.Headline : Headline = If(value, "")
                Case ImageDescriptionField.Description : Description = If(value, "")
                Case ImageDescriptionField.Creator : Creator = If(value, "")
                Case ImageDescriptionField.Credit : Credit = If(value, "")
            End Select
        End Sub

        Public ReadOnly Property IsEmpty As Boolean
            Get
                Return AllFields.All(Function(f) GetValue(f).Length = 0)
            End Get
        End Property
    End Class

    Public Enum ImageDescriptionTarget
        None = 0
        InFile = 1
        XmpSidecar = 2
    End Enum

    Public Class ImageDescriptionWriteResult
        Public Property Success As Boolean
        Public Property Target As ImageDescriptionTarget = ImageDescriptionTarget.None
    End Class

    ''' <summary>
    ''' Liest und schreibt Titel, Ueberschrift, Beschreibung, Urheber und Quellenangabe.
    '''
    ''' WOHIN: dieselbe Aufteilung wie beim Urheberrechtshinweis. JPEG, PNG und WebP bekommen die
    ''' Felder IN die Datei, weil das die Datei ist, die weitergegeben wird: als XMP, im JPEG
    ''' zusaetzlich als IPTC, weil etliche Programme und Webdienste nur das lesen. Alles andere,
    ''' vor allem RAW, bekommt eine XMP-Beistelldatei; an eine RAW-Datei geht nichts direkt heran.
    ''' Liegt neben einer JPEG schon eine Beistelldatei, wird sie mitgezogen (nie neu angelegt),
    ''' sonst zeigte ein Programm, das die Beistelldatei vorzieht, den alten Stand.
    '''
    ''' WELCHE FELDER: die Zuordnung von IPTC Core.
    ''' <list type="bullet">
    ''' <item>Titel: dc:title und IPTC 2:05 Object Name</item>
    ''' <item>Ueberschrift: photoshop:Headline und IPTC 2:105</item>
    ''' <item>Beschreibung: dc:description und IPTC 2:120 Caption/Abstract</item>
    ''' <item>Urheber: dc:creator und IPTC 2:80 By-line</item>
    ''' <item>Quellenangabe: photoshop:Credit und IPTC 2:110</item>
    ''' </list>
    '''
    ''' GELESEN wird in der Reihenfolge des Standards: Beistelldatei, eingebettetes XMP, IPTC. Das
    ''' erste belegte Feld gewinnt, je Feld fuer sich.
    ''' </summary>
    Public NotInheritable Class ImageDescriptionService

        Private Sub New()
        End Sub

        Private Shared ReadOnly RdfNs As XNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#"
        Private Shared ReadOnly DcNs As XNamespace = "http://purl.org/dc/elements/1.1/"
        Private Shared ReadOnly PhotoshopNs As XNamespace = "http://ns.adobe.com/photoshop/1.0/"

        ''' <summary>Eine Zeile ist eine Zeile; die Beschreibung darf mehrere haben und so lang
        ''' werden, wie IPTC sie fuer 2:120 vorsieht.</summary>
        Public Const MaxLineLength As Integer = 256
        Public Const MaxDescriptionLength As Integer = 2000

        ''' <summary>Raeumt einen eingegebenen Wert auf. Die Beschreibung behaelt ihre
        ''' Zeilenumbrueche (vereinheitlicht auf LF), alle anderen Felder werden einzeilig.
        ''' Steuerzeichen fallen weg, die Laenge wird begrenzt.</summary>
        Public Shared Function NormalizeValue(field As ImageDescriptionField, value As String) As String
            If String.IsNullOrWhiteSpace(value) Then Return ""
            Dim multiline = field = ImageDescriptionField.Description
            Dim text = value.Replace(ControlChars.CrLf, ControlChars.Lf).Replace(ControlChars.Cr, ControlChars.Lf)
            Dim builder As New StringBuilder(text.Length)
            For Each character In text
                If character = ControlChars.Lf Then
                    builder.Append(If(multiline, ControlChars.Lf, " "c))
                ElseIf character = ControlChars.Tab Then
                    builder.Append(" "c)
                ElseIf Not Char.IsControl(character) Then
                    builder.Append(character)
                End If
            Next
            Dim cleaned = builder.ToString().Trim()
            If Not multiline Then cleaned = Regex.Replace(cleaned, " {2,}", " ")
            If field = ImageDescriptionField.Creator Then cleaned = String.Join("; ", SplitCreators(cleaned))
            Dim limit = If(multiline, MaxDescriptionLength, MaxLineLength)
            If cleaned.Length > limit Then cleaned = cleaned.Substring(0, limit).Trim()
            Return cleaned
        End Function

        ''' <summary>Die einzelnen Urheber aus der Zeile des Dialogs.</summary>
        Public Shared Function SplitCreators(value As String) As List(Of String)
            Return If(value, "").Split(";"c).
                Select(Function(n) n.Trim()).
                Where(Function(n) n.Length > 0).
                Distinct(StringComparer.Ordinal).
                ToList()
        End Function

        ''' <summary>Die fuenf Felder als EIN Text fuer die Suche: Volltextsuche der Galerie
        ''' (ImageItem.SearchText) und Suchtext der Suchlisten. Eine Stelle, damit beide dasselbe
        ''' finden.</summary>
        Public Shared Function JoinForSearch(ParamArray values As String()) As String
            Return String.Join(" ", If(values, Array.Empty(Of String)()).Where(Function(v) Not String.IsNullOrWhiteSpace(v)).Select(Function(v) v.Trim()))
        End Function

        Public Shared Function JoinForSearch(description As ImageDescription) As String
            If description Is Nothing Then Return ""
            Return JoinForSearch(description.Title, description.Headline, description.Description, description.Creator, description.Credit)
        End Function

        ''' <summary>Kann das Format die Felder IN der Datei tragen?</summary>
        Public Shared Function CanWriteIntoFile(path As String) As Boolean
            Return ImageProcessor.CanWriteKeywordsInto(path)
        End Function

        ' ---------------------------------------------------------------- Lesen

        ''' <summary>Die Felder, die an der Datei gelten: Beistelldatei vor eingebettetem XMP vor
        ''' IPTC. Gelesen ueber denselben Weg wie die Info-Leiste (ExifService.ReadExif), damit
        ''' Dialog, Katalog und Anzeige nie zwei verschiedene Werte nennen.</summary>
        Public Shared Function Read(imagePath As String) As ImageDescription
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then Return New ImageDescription()
            Try
                Return FromExifData(ExifService.ReadExif(imagePath), imagePath)
            Catch ex As Exception
                DiagnosticLogService.LogException("Description.Read", ex)
                Return New ImageDescription()
            End Try
        End Function

        ''' <summary>Die Felder aus schon gelesenen Daten plus der Beistelldatei daneben. Der Weg des
        ''' Katalogs: er hat die Daten bereits und soll die Bilddatei kein zweites Mal oeffnen.</summary>
        Public Shared Function FromExifData(data As ExifData, imagePath As String) As ImageDescription
            Dim result = If(String.IsNullOrWhiteSpace(imagePath), New ImageDescription(), ExifService.ReadSidecarDescription(imagePath))
            If data Is Nothing Then Return result
            Dim embedded = FromEmbeddedTags(data)
            For Each field In ImageDescription.AllFields
                If result.GetValue(field).Length = 0 Then result.SetValue(field, embedded.GetValue(field))
            Next
            Return result
        End Function

        ''' <summary>Eingebettetes XMP vor IPTC. Die XMP-Namen kommen von MetadataExtractor mit
        ''' laufender Nummer ("dc:title[1]", "dc:creator[2]"); Qualifier wie die Sprache tragen
        ''' einen Zusatz hinter der Nummer und fallen damit von selbst heraus.</summary>
        Private Shared Function FromEmbeddedTags(data As ExifData) As ImageDescription
            Dim result As New ImageDescription()
            Dim creators As New List(Of String)()
            For Each tag In If(data.XmpTags, New List(Of ExifTag)())
                If tag Is Nothing OrElse String.IsNullOrWhiteSpace(tag.Name) Then Continue For
                Dim value = If(tag.Value, "").Trim()
                If value.Length = 0 Then Continue For
                Dim bareName = Regex.Replace(tag.Name.Trim(), "\[\d+\]$", "")
                Select Case bareName.ToLowerInvariant()
                    Case "dc:title" : If result.Title.Length = 0 Then result.Title = value
                    Case "photoshop:headline" : If result.Headline.Length = 0 Then result.Headline = value
                    Case "dc:description" : If result.Description.Length = 0 Then result.Description = value
                    Case "photoshop:credit" : If result.Credit.Length = 0 Then result.Credit = value
                    Case "dc:creator" : If Not creators.Contains(value) Then creators.Add(value)
                End Select
            Next
            result.Creator = String.Join("; ", creators)

            For Each tag In If(data.IptcTags, New List(Of ExifTag)())
                If tag Is Nothing OrElse String.IsNullOrWhiteSpace(tag.Name) Then Continue For
                Dim value = If(tag.Value, "").Trim()
                If value.Length = 0 Then Continue For
                Select Case tag.Name.Trim().ToLowerInvariant()
                    Case "object name" : If result.Title.Length = 0 Then result.Title = value
                    Case "headline" : If result.Headline.Length = 0 Then result.Headline = value
                    Case "caption/abstract" : If result.Description.Length = 0 Then result.Description = value
                    Case "credit" : If result.Credit.Length = 0 Then result.Credit = value
                    ' MetadataExtractor fuegt die wiederholten 2:80 mit Semikolon zusammen; das ist
                    ' zugleich die Schreibweise des Dialogs.
                    Case "by-line" : If result.Creator.Length = 0 Then result.Creator = String.Join("; ", SplitCreators(value))
                End Select
            Next
            Return result
        End Function

        ''' <summary>Die Felder aus den rdf:Description-Knoten eines XMP-Dokuments (Beistelldatei).
        ''' Beide Schreibweisen zaehlen: Element (mit rdf:Alt, rdf:Seq oder Text) und Attribut.</summary>
        Friend Shared Function FromXmpDescriptions(descriptions As IEnumerable(Of XElement)) As ImageDescription
            Dim result As New ImageDescription()
            For Each description In descriptions
                For Each field In ImageDescription.AllFields
                    If result.GetValue(field).Length > 0 Then Continue For
                    result.SetValue(field, ReadXmpField(description, field))
                Next
            Next
            Return result
        End Function

        Private Shared Function ReadXmpField(description As XElement, field As ImageDescriptionField) As String
            Dim name = XmpName(field)
            Dim attribute = description.Attribute(name)
            If attribute IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(attribute.Value) Then Return attribute.Value.Trim()
            Dim element = description.Element(name)
            If element Is Nothing Then Return ""
            Dim items = element.Descendants(RdfNs + "li").ToList()
            If items.Count = 0 Then Return element.Value.Trim()
            If field = ImageDescriptionField.Creator Then
                Return String.Join("; ", items.Select(Function(i) i.Value.Trim()).Where(Function(v) v.Length > 0))
            End If
            ' Sprachalternative: x-default bevorzugt, sonst der erste Eintrag.
            Dim chosen = items.FirstOrDefault(Function(i) String.Equals(i.Attribute(XNamespace.Xml + "lang")?.Value, "x-default", StringComparison.OrdinalIgnoreCase))
            Return If(chosen, items(0)).Value.Trim()
        End Function

        ' ---------------------------------------------------------------- Schreiben

        ''' <summary>Schreibt die genannten Felder an die Datei. Ein Feld, das in
        ''' <paramref name="changes"/> fehlt, bleibt unangetastet; ein LEERER Wert entfernt es.
        ''' Anders als beim Urheberrechtshinweis ist Leeren hier eine bewusste Eingabe: der Dialog
        ''' zeigt, was gilt, und uebergibt nur, was sich daran geaendert hat.</summary>
        Public Shared Function Write(imagePath As String,
                                     changes As IReadOnlyDictionary(Of ImageDescriptionField, String)) As ImageDescriptionWriteResult
            Dim result As New ImageDescriptionWriteResult()
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then Return result
            Dim cleaned = CleanChanges(changes)
            If cleaned.Count = 0 Then
                result.Success = True
                Return result
            End If

            If CanWriteIntoFile(imagePath) Then
                result.Target = ImageDescriptionTarget.InFile
                ' KEIN Ausweichen in die Beistelldatei, wenn die Datei sich nicht schreiben laesst:
                ' die Datei behielte dann ihren alten Wert, und jedes Programm, das nur in die Datei
                ' sieht, zeigte ihn weiter. Ein ehrlicher Fehlschlag ist besser als ein halber Erfolg.
                '
                ' Eine VORHANDENE Beistelldatei muss mit, denn sie geht beim Lesen vor. Zwei Dateien
                ' lassen sich nicht in einem Zug schreiben, deshalb diese Reihenfolge:
                '   1. alten Inhalt der Beistelldatei sichern, dann sie schreiben (klein, atomar).
                '      Scheitert das, ist noch NICHTS veraendert.
                '   2. die Bilddatei schreiben (atomar). Scheitert DAS, wird die Beistelldatei auf
                '      den gesicherten Stand zurueckgesetzt.
                ' So stehen am Ende beide auf dem neuen oder beide auf dem alten Stand. Andersherum
                ' (Bild zuerst) liesse sich eine schon geschriebene Bilddatei nicht ohne eine Kopie
                ' des ganzen Fotos zuruecknehmen.
                Dim existingSidecar = XmpSidecarService.FindSidecar(imagePath)
                Dim sidecarBefore As Byte() = Nothing
                If Not String.IsNullOrEmpty(existingSidecar) Then
                    Try
                        sidecarBefore = File.ReadAllBytes(existingSidecar)
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Description.ReadSidecar", ex)
                        Return result
                    End Try
                    If String.IsNullOrEmpty(ExifService.WriteXmpDescriptionSidecar(imagePath, cleaned, createIfMissing:=False)) Then
                        DiagnosticLogService.LogAlways("Description.SyncSidecar",
                                                       "Beistelldatei nicht geschrieben, Bild unveraendert: " & imagePath)
                        Return result
                    End If
                End If

                Try
                    ' False heisst hier "nichts zu aendern" (etwa ein Feld leeren, das gar nicht in
                    ' der Datei stand); jedes "geht nicht" kommt als Ausnahme.
                    If ImageProcessor.WriteDescriptionIntoFile(imagePath, cleaned) Then ExifService.Invalidate(imagePath)
                Catch ex As Exception
                    DiagnosticLogService.LogException("Description.WriteIntoFile", ex)
                    If sidecarBefore IsNot Nothing AndAlso Not ExifService.RestoreXmpSidecar(existingSidecar, sidecarBefore) Then
                        DiagnosticLogService.LogAlways("Description.SyncSidecar",
                                                       "Bild nicht geschrieben und Beistelldatei nicht zurueckgesetzt, sie traegt den neuen Stand: " & existingSidecar)
                    End If
                    Return result
                End Try
                result.Success = True
                Return result
            End If

            ' Ohne Beistelldatei und ohne einen Wert, der gesetzt wird, wird keine angelegt: eine
            ' Datei, die nur das Fehlen von Feldern festhaelt, hat niemand verlangt.
            Dim needsNewSidecar = cleaned.Values.Any(Function(v) v.Length > 0)
            If Not needsNewSidecar AndAlso String.IsNullOrEmpty(XmpSidecarService.FindSidecar(imagePath)) Then
                result.Success = True
                result.Target = ImageDescriptionTarget.XmpSidecar
                Return result
            End If
            Dim sidecarPath = ExifService.WriteXmpDescriptionSidecar(imagePath, cleaned, createIfMissing:=True)
            result.Success = Not String.IsNullOrEmpty(sidecarPath)
            If result.Success Then result.Target = ImageDescriptionTarget.XmpSidecar
            Return result
        End Function

        Private Shared Function CleanChanges(changes As IReadOnlyDictionary(Of ImageDescriptionField, String)) As Dictionary(Of ImageDescriptionField, String)
            Dim result As New Dictionary(Of ImageDescriptionField, String)()
            If changes Is Nothing Then Return result
            For Each pair In changes
                result(pair.Key) = NormalizeValue(pair.Key, pair.Value)
            Next
            Return result
        End Function

        ''' <summary>Setzt die Felder in einem XMP-Dokument: in ALLEN rdf:Description-Knoten wird
        ''' der alte Wert entfernt (als Element wie als Attribut, sonst laese ein Programm je nach
        ''' Vorliebe den alten), der neue kommt in <paramref name="target"/>.
        '''
        ''' Eine Sprachalternative wird dabei ganz ersetzt, nicht nur ihr x-default: der Dialog
        ''' kennt eine Fassung je Feld, und eine stehengebliebene zweite Sprache zeigte ein anderes
        ''' Programm statt der eben eingegebenen.</summary>
        ''' <returns>True, wenn sich am Dokument etwas geaendert hat.</returns>
        Friend Shared Function ApplyToXmp(descriptions As IEnumerable(Of XElement), target As XElement,
                                          changes As IReadOnlyDictionary(Of ImageDescriptionField, String)) As Boolean
            Dim changed = False
            Dim all = descriptions.ToList()
            For Each pair In changes
                Dim name = XmpName(pair.Key)
                For Each description In all
                    If description.Attribute(name) IsNot Nothing Then
                        description.SetAttributeValue(name, Nothing)
                        changed = True
                    End If
                    For Each stale In description.Elements(name).ToList()
                        stale.Remove()
                        changed = True
                    Next
                Next
                If pair.Value.Length = 0 Then Continue For

                EnsurePrefix(target, If(name.Namespace = DcNs, "dc", "photoshop"), name.Namespace)
                target.Add(BuildXmpValue(pair.Key, name, pair.Value))
                changed = True
            Next
            Return changed
        End Function

        Private Shared Function BuildXmpValue(field As ImageDescriptionField, name As XName, value As String) As XElement
            Select Case field
                Case ImageDescriptionField.Title, ImageDescriptionField.Description
                    Return New XElement(name,
                        New XElement(RdfNs + "Alt",
                            New XElement(RdfNs + "li", New XAttribute(XNamespace.Xml + "lang", "x-default"), value)))
                Case ImageDescriptionField.Creator
                    Dim sequence = New XElement(RdfNs + "Seq")
                    For Each creator In SplitCreators(value)
                        sequence.Add(New XElement(RdfNs + "li", creator))
                    Next
                    Return New XElement(name, sequence)
                Case Else
                    Return New XElement(name, value)
            End Select
        End Function

        Private Shared Sub EnsurePrefix(target As XElement, prefix As String, ns As XNamespace)
            If target.GetPrefixOfNamespace(ns) IsNot Nothing Then Return
            target.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName)
        End Sub

        Friend Shared Function XmpName(field As ImageDescriptionField) As XName
            Select Case field
                Case ImageDescriptionField.Title : Return DcNs + "title"
                Case ImageDescriptionField.Headline : Return PhotoshopNs + "Headline"
                Case ImageDescriptionField.Description : Return DcNs + "description"
                Case ImageDescriptionField.Creator : Return DcNs + "creator"
                Case Else : Return PhotoshopNs + "Credit"
            End Select
        End Function

        ''' <summary>Die IPTC-Datensatznummer in Record 2.</summary>
        Friend Shared Function IptcDataSet(field As ImageDescriptionField) As Integer
            Select Case field
                Case ImageDescriptionField.Title : Return 5
                Case ImageDescriptionField.Headline : Return 105
                Case ImageDescriptionField.Description : Return 120
                Case ImageDescriptionField.Creator : Return 80
                Case Else : Return 110
            End Select
        End Function

        ''' <summary>Die Werte eines Feldes fuer IPTC: der Urheber als einzelne Datensaetze, die
        ''' Beschreibung mit CR als Zeilenende, wie es IPTC fuer 2:120 vorsieht.</summary>
        Friend Shared Function IptcValues(field As ImageDescriptionField, value As String) As List(Of String)
            If String.IsNullOrEmpty(value) Then Return New List(Of String)()
            If field = ImageDescriptionField.Creator Then Return SplitCreators(value)
            If field = ImageDescriptionField.Description Then Return New List(Of String) From {value.Replace(ControlChars.Lf, ControlChars.Cr)}
            Return New List(Of String) From {value}
        End Function

        ' ---------------------------------------------------------------- Speichern unter

        ''' <summary>Nach dem Speichern einer NEUEN Datei: die Felder der Beistelldatei der Quelle in
        ''' die Zieldatei. Eingebettete Felder reisen schon mit den kopierten Metadaten; was eine RAW
        ''' traegt, steht dagegen in ihrer Beistelldatei, und die liest das Kopieren nicht. Ohne
        ''' diesen Schritt kaeme ein Titel, den ein anderes Programm vergeben hat, nie im
        ''' exportierten JPEG an.
        '''
        ''' Nur Felder MIT Wert; ein leeres Feld der Beistelldatei raeumt nichts in der Zieldatei.</summary>
        Friend Shared Sub CarrySidecarIntoSavedFile(sourcePath As String, targetPath As String)
            Try
                If String.IsNullOrWhiteSpace(sourcePath) OrElse String.IsNullOrWhiteSpace(targetPath) Then Return
                If PathIdentity.AreSame(sourcePath, targetPath) Then Return
                If Not CanWriteIntoFile(targetPath) Then Return
                Dim fromSidecar = ExifService.ReadSidecarDescription(sourcePath)
                If fromSidecar.IsEmpty Then Return
                Dim changes As New Dictionary(Of ImageDescriptionField, String)()
                For Each field In ImageDescription.AllFields
                    Dim value = NormalizeValue(field, fromSidecar.GetValue(field))
                    If value.Length > 0 Then changes(field) = value
                Next
                If changes.Count = 0 Then Return
                ' Wirft, wenn die Zieldatei sich nicht sicher beschreiben laesst; dann bleibt sie,
                ' wie sie gespeichert wurde, und das Speichern selbst ist nicht gescheitert.
                ImageProcessor.WriteDescriptionIntoFile(targetPath, changes)
            Catch ex As Exception
                DiagnosticLogService.LogException("Save.Description", ex)
            End Try
        End Sub

    End Class

End Namespace
