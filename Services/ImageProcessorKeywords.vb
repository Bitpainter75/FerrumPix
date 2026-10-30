Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Security.Cryptography
Imports System.Text
Imports System.Xml.Linq

' Stichwoerter in die Bilddatei selbst: XMP dc:subject in JPEG, PNG und WebP, dazu IPTC 2:25 im
' JPEG. Eingang sind die Einstellungen "Stichwoerter in die Bilddatei schreiben" (beim Vergeben,
' siehe KeywordFileWriter) und SaveImage (Speichern unter, Exportieren, Konvertieren). Kein
' eigener Zustand, reine Byte-Arbeit wie in ImageProcessorSave.
Namespace Services

    ''' <summary>Welche Felder der Bilddatei die Stichwoerter bekommen.</summary>
    <Flags>
    Public Enum KeywordFileFields
        None = 0
        Xmp = 1
        Iptc = 2
    End Enum

    Partial Public Class ImageProcessor

        Private Shared ReadOnly KeywordRdfNs As XNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#"
        Private Shared ReadOnly KeywordDcNs As XNamespace = "http://purl.org/dc/elements/1.1/"
        Private Shared ReadOnly KeywordLrNs As XNamespace = "http://ns.adobe.com/lightroom/1.0/"

        ''' Kennung des XMP-Segments im JPEG, samt abschliessender Null.
        Private Shared ReadOnly JpegXmpSignature As Byte() = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/" & ChrW(0))
        ''' Kennung des Photoshop-Segments (APP13), in dem der IPTC-Block steckt.
        Private Shared ReadOnly JpegPhotoshopSignature As Byte() = Encoding.ASCII.GetBytes("Photoshop 3.0" & ChrW(0))
        ''' IPTC 1:90 mit dem Wert fuer UTF-8 (ESC % G).
        Private Shared ReadOnly IptcUtf8Marker As Byte() = {&H1B, &H25, &H47}

        ''' <summary>Formate, in die Stichwoerter geschrieben werden koennen. IPTC gibt es davon nur
        ''' im JPEG; PNG und WebP bekommen nur XMP.</summary>
        Public Shared Function CanWriteKeywordsInto(path As String) As Boolean
            Select Case IO.Path.GetExtension(If(path, "")).ToLowerInvariant()
                Case ".jpg", ".jpeg", ".png", ".webp"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        ''' <summary>Schreibt die Stichwoerter in die Datei und ERSETZT dabei die vorhandenen:
        ''' was in der Liste fehlt, verschwindet aus dc:subject und IPTC 2:25. Eine leere Liste
        ''' raeumt beide Felder, legt aber kein neues XMP- oder IPTC-Segment an.
        '''
        ''' Alles uebrige bleibt Byte fuer Byte liegen: EXIF, Farbprofil, fremde XMP-Felder, die
        ''' anderen IPTC-Felder und die Bilddaten. Geschrieben wird atomar ueber eine Nachbardatei.
        ''' Laesst sich ein vorhandener Block nicht sicher zerlegen, bleibt die Datei unangetastet.</summary>
        ''' <returns>True, wenn die Datei neu geschrieben wurde.</returns>
        Public Shared Function WriteKeywordsIntoFile(path As String, keywords As IEnumerable(Of String),
                                                     fields As KeywordFileFields) As Boolean
            If fields = KeywordFileFields.None OrElse String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return False
            Dim list = CleanKeywordList(keywords)
            Try
                Select Case IO.Path.GetExtension(path).ToLowerInvariant()
                    Case ".jpg", ".jpeg"
                        Return WriteJpegKeywords(path, list, fields)
                    Case ".png"
                        If (fields And KeywordFileFields.Xmp) = 0 Then Return False
                        Return WritePngKeywords(path, list)
                    Case ".webp"
                        If (fields And KeywordFileFields.Xmp) = 0 Then Return False
                        Return WriteWebpKeywords(path, list)
                End Select
            Catch ex As Exception
                DiagnosticLogService.LogException("Keywords.WriteIntoFile", ex)
            End Try
            Return False
        End Function

        ''' <summary>Leer, doppelt (ohne Ruecksicht auf Gross- und Kleinschreibung) und Leerraum
        ''' fallen weg; die Reihenfolge bleibt.</summary>
        Private Shared Function CleanKeywordList(keywords As IEnumerable(Of String)) As List(Of String)
            Return If(keywords, Enumerable.Empty(Of String)()).
                Select(Function(k) If(k, "").Trim()).
                Where(Function(k) k.Length > 0).
                Distinct(StringComparer.OrdinalIgnoreCase).
                ToList()
        End Function

        ' ---------------------------------------------------------------- XMP-Paket

        ''' <summary>Das XMP-Paket mit den neuen Stichwoertern. <paramref name="packet"/> Nothing
        ''' heisst: es gibt noch keins; dann entsteht eines, aber nur fuer eine nicht leere Liste.
        '''
        ''' Rueckgabe Nothing: nichts zu schreiben (kein Paket und keine Stichwoerter) ODER das
        ''' vorhandene Paket ist nicht lesbar. In beiden Faellen bleibt die Datei, wie sie ist;
        ''' <paramref name="failed"/> trennt die beiden.
        '''
        ''' lr:hierarchicalSubject wird mitgezogen: dort bleibt nur, wessen letzte Stufe noch in
        ''' der Liste steht. Sonst kaeme ein entferntes Stichwort beim naechsten Einlesen ueber die
        ''' Hierarchie zurueck (LibraryService.NormalizeKeyword nimmt von dort das Blatt).</summary>
        Private Shared Function BuildKeywordXmpPacket(packet As Byte(), keywords As List(Of String),
                                                      ByRef failed As Boolean) As Byte()
            failed = False
            If packet Is Nothing Then
                If keywords.Count = 0 Then Return Nothing
                Return Encoding.UTF8.GetBytes(NewKeywordXmpPacket(keywords))
            End If

            Dim doc As XDocument
            Try
                Dim text = Encoding.UTF8.GetString(packet).TrimEnd(ChrW(0), " "c, ControlChars.Cr, ControlChars.Lf, ControlChars.Tab)
                If text.Length > 0 AndAlso text(0) = ChrW(&HFEFF) Then text = text.Substring(1)
                doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace)
            Catch ex As Exception
                DiagnosticLogService.LogException("Keywords.ParseXmp", ex)
                failed = True
                Return Nothing
            End Try

            Dim rdfRoot = doc.Descendants(KeywordRdfNs + "RDF").FirstOrDefault()
            If rdfRoot Is Nothing Then
                failed = True
                Return Nothing
            End If

            Dim wanted As New HashSet(Of String)(keywords, StringComparer.OrdinalIgnoreCase)
            Dim descriptions = rdfRoot.Elements(KeywordRdfNs + "Description").ToList()
            For Each description In descriptions
                description.Elements(KeywordDcNs + "subject").Remove()
                description.Attribute(KeywordDcNs + "subject")?.Remove()
                For Each hierarchy In description.Elements(KeywordLrNs + "hierarchicalSubject").ToList()
                    For Each item In hierarchy.Descendants(KeywordRdfNs + "li").ToList()
                        Dim leaf = item.Value.Split("|"c).Last().Trim()
                        If Not wanted.Contains(leaf) Then item.Remove()
                    Next
                    If Not hierarchy.Descendants(KeywordRdfNs + "li").Any() Then hierarchy.Remove()
                Next
            Next

            If keywords.Count > 0 Then
                Dim target = descriptions.FirstOrDefault()
                If target Is Nothing Then
                    target = New XElement(KeywordRdfNs + "Description", New XAttribute(KeywordRdfNs + "about", ""))
                    rdfRoot.Add(target)
                End If
                If target.GetPrefixOfNamespace(KeywordDcNs) Is Nothing Then
                    target.SetAttributeValue(XNamespace.Xmlns + "dc", KeywordDcNs.NamespaceName)
                End If
                target.Add(BuildKeywordSubject(keywords))
            End If

            Return Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting))
        End Function

        Private Shared Function BuildKeywordSubject(keywords As List(Of String)) As XElement
            Dim bag = New XElement(KeywordRdfNs + "Bag")
            For Each keyword In keywords
                bag.Add(New XElement(KeywordRdfNs + "li", keyword))
            Next
            Return New XElement(KeywordDcNs + "subject", bag)
        End Function

        ''' <summary>Ein neues Paket mit nur den Stichwoertern, im ueblichen Rahmen aus
        ''' xpacket-Anweisungen.</summary>
        Private Shared Function NewKeywordXmpPacket(keywords As List(Of String)) As String
            Dim xNs As XNamespace = "adobe:ns:meta/"
            Dim meta = New XElement(xNs + "xmpmeta",
                New XAttribute(XNamespace.Xmlns + "x", xNs.NamespaceName),
                New XElement(KeywordRdfNs + "RDF",
                    New XAttribute(XNamespace.Xmlns + "rdf", KeywordRdfNs.NamespaceName),
                    New XElement(KeywordRdfNs + "Description",
                        New XAttribute(KeywordRdfNs + "about", ""),
                        New XAttribute(XNamespace.Xmlns + "dc", KeywordDcNs.NamespaceName),
                        BuildKeywordSubject(keywords))))
            Return "<?xpacket begin=""" & ChrW(&HFEFF) & """ id=""W5M0MpCehiHzreSzNTczkc9d""?>" &
                   meta.ToString(SaveOptions.DisableFormatting) &
                   "<?xpacket end=""w""?>"
        End Function

        ' ---------------------------------------------------------------- JPEG

        ''' <summary>Ein Segment aus dem Kopf eines JPEG: Marker, Lage in der Datei und, nur fuer
        ''' APP1 und APP13, der Inhalt.</summary>
        Private NotInheritable Class JpegHeaderSegment
            Public Marker As Byte
            Public Offset As Long
            Public TotalLength As Integer
            Public Payload As Byte()
        End Class

        Private Shared Function WriteJpegKeywords(path As String, keywords As List(Of String), fields As KeywordFileFields) As Boolean
            Dim segments As New List(Of JpegHeaderSegment)()
            Dim headerEnd As Long = 0
            If Not ReadJpegHeaderSegments(path, segments, headerEnd) Then Return False

            Dim xmpIndex = segments.FindIndex(Function(s) IsJpegXmpSegment(s))
            Dim app13Index = segments.FindIndex(Function(s) IsJpegPhotoshopSegment(s))
            If segments.Where(Function(s) IsJpegPhotoshopSegment(s)).Skip(1).Any() Then
                ' Ein IPTC-Block ueber mehrere Segmente verteilt: zu selten, um ihn sicher
                ' zusammenzusetzen, und ein halber waere schlimmer als keiner.
                fields = fields And Not KeywordFileFields.Iptc
            End If

            Dim newXmp As Byte() = Nothing
            Dim newApp13 As Byte() = Nothing
            If (fields And KeywordFileFields.Xmp) <> 0 Then
                Dim existing As Byte() = Nothing
                If xmpIndex >= 0 Then
                    Dim payload = segments(xmpIndex).Payload
                    existing = New Byte(payload.Length - JpegXmpSignature.Length - 1) {}
                    Buffer.BlockCopy(payload, JpegXmpSignature.Length, existing, 0, existing.Length)
                End If
                Dim failed = False
                Dim packet = BuildKeywordXmpPacket(existing, keywords, failed)
                If packet IsNot Nothing Then
                    newXmp = CombineBytes(JpegXmpSignature, packet)
                    If newXmp.Length > 65533 Then
                        DiagnosticLogService.LogAlways("Keywords.Jpeg", "XMP passt nicht mehr in ein Segment: " & path)
                        newXmp = Nothing
                    End If
                End If
            End If
            If (fields And KeywordFileFields.Iptc) <> 0 Then
                Dim existing = If(app13Index >= 0, segments(app13Index).Payload, Nothing)
                newApp13 = BuildKeywordPhotoshopPayload(existing, keywords)
                If newApp13 IsNot Nothing AndAlso newApp13.Length > 65533 Then
                    DiagnosticLogService.LogAlways("Keywords.Jpeg", "IPTC passt nicht mehr in ein Segment: " & path)
                    newApp13 = Nothing
                End If
            End If
            If newXmp Is Nothing AndAlso newApp13 Is Nothing Then Return False

            ' Der neue Kopf: vorhandene Segmente an ihrem Platz ersetzen, fehlende hinter EXIF
            ' (sonst hinter die fuehrenden APP0) einsetzen. Jeder Eintrag ist entweder neu
            ' (Bytes) oder ein Segment der Quelle, das unveraendert kopiert wird (Index).
            Dim output As New List(Of (Bytes As Byte(), SourceIndex As Integer))()
            Dim inserted As New List(Of Byte())()
            If newXmp IsNot Nothing AndAlso xmpIndex < 0 Then inserted.Add(BuildJpegSegment(&HE1, newXmp))
            If newApp13 IsNot Nothing AndAlso app13Index < 0 Then inserted.Add(BuildJpegSegment(&HED, newApp13))
            Dim exifSignature = Encoding.ASCII.GetBytes("Exif" & ChrW(0) & ChrW(0))
            Dim exifIndex = segments.FindIndex(Function(s) s.Marker = &HE1 AndAlso StartsWithBytes(s.Payload, exifSignature))
            Dim leadingApp0 = 0
            While leadingApp0 < segments.Count AndAlso segments(leadingApp0).Marker = &HE0
                leadingApp0 += 1
            End While
            Dim insertAfter = If(exifIndex >= 0, exifIndex, leadingApp0 - 1)

            If insertAfter < 0 Then
                For Each item In inserted
                    output.Add((item, -1))
                Next
            End If
            For i = 0 To segments.Count - 1
                If i = xmpIndex AndAlso newXmp IsNot Nothing Then
                    output.Add((BuildJpegSegment(&HE1, newXmp), -1))
                ElseIf i = app13Index AndAlso newApp13 IsNot Nothing Then
                    output.Add((BuildJpegSegment(&HED, newApp13), -1))
                Else
                    output.Add((Nothing, i))
                End If
                If i = insertAfter Then
                    For Each item In inserted
                        output.Add((item, -1))
                    Next
                End If
            Next

            Return WriteFileAtomic(path,
                Sub(destination)
                    Using source As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                                   64 * 1024, FileOptions.SequentialScan)
                        destination.WriteByte(&HFF)
                        destination.WriteByte(&HD8)
                        Dim buffer(64 * 1024 - 1) As Byte
                        For Each entry In output
                            If entry.Bytes IsNot Nothing Then
                                destination.Write(entry.Bytes, 0, entry.Bytes.Length)
                            Else
                                Dim segment = segments(entry.SourceIndex)
                                source.Seek(segment.Offset, SeekOrigin.Begin)
                                CopyKeywordBytes(source, destination, segment.TotalLength, buffer)
                            End If
                        Next
                        source.Seek(headerEnd, SeekOrigin.Begin)
                        source.CopyTo(destination, 64 * 1024)
                    End Using
                End Sub)
        End Function

        Private Shared Sub CopyKeywordBytes(source As Stream, destination As Stream, count As Integer, buffer As Byte())
            Dim remaining = count
            While remaining > 0
                Dim read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining))
                If read <= 0 Then Throw New EndOfStreamException()
                destination.Write(buffer, 0, read)
                remaining -= read
            End While
        End Sub

        Private Shared Function BuildJpegSegment(marker As Byte, payload As Byte()) As Byte()
            Dim length = payload.Length + 2
            Dim segment(payload.Length + 3) As Byte
            segment(0) = &HFF
            segment(1) = marker
            segment(2) = CByte((length >> 8) And &HFF)
            segment(3) = CByte(length And &HFF)
            Buffer.BlockCopy(payload, 0, segment, 4, payload.Length)
            Return segment
        End Function

        Private Shared Function IsJpegXmpSegment(segment As JpegHeaderSegment) As Boolean
            Return segment.Marker = &HE1 AndAlso StartsWithBytes(segment.Payload, JpegXmpSignature)
        End Function

        Private Shared Function IsJpegPhotoshopSegment(segment As JpegHeaderSegment) As Boolean
            Return segment.Marker = &HED AndAlso StartsWithBytes(segment.Payload, JpegPhotoshopSignature)
        End Function

        Private Shared Function StartsWithBytes(data As Byte(), prefix As Byte()) As Boolean
            If data Is Nothing OrElse data.Length < prefix.Length Then Return False
            For i = 0 To prefix.Length - 1
                If data(i) <> prefix(i) Then Return False
            Next
            Return True
        End Function

        ''' <summary>Liest die Segmente bis zum Bildstrom (SOS). Der Bildstrom selbst wird nie
        ''' gelesen. False, wenn es kein JPEG ist oder der Kopf vorher abbricht.</summary>
        Private Shared Function ReadJpegHeaderSegments(path As String, segments As List(Of JpegHeaderSegment),
                                                       ByRef headerEnd As Long) As Boolean
            Using stream As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan)
                Dim header(3) As Byte
                If stream.Read(header, 0, 2) <> 2 OrElse header(0) <> &HFF OrElse header(1) <> &HD8 Then Return False
                While True
                    Dim offset = stream.Position
                    If stream.Read(header, 0, 2) <> 2 OrElse header(0) <> &HFF Then Return False
                    Dim marker = header(1)
                    If marker = &HDA Then
                        headerEnd = offset
                        Return True
                    End If
                    If marker = &HD9 Then Return False
                    If marker = &H1 OrElse (marker >= &HD0 AndAlso marker <= &HD7) OrElse marker = &HFF Then Return False
                    If stream.Read(header, 2, 2) <> 2 Then Return False
                    Dim length = (CInt(header(2)) << 8) Or CInt(header(3))
                    If length < 2 Then Return False
                    Dim segment As New JpegHeaderSegment With {.Marker = marker, .Offset = offset, .TotalLength = length + 2}
                    If marker = &HE1 OrElse marker = &HED Then
                        Dim payload(length - 3) As Byte
                        Dim read = 0
                        While read < payload.Length
                            Dim n = stream.Read(payload, read, payload.Length - read)
                            If n <= 0 Then Return False
                            read += n
                        End While
                        segment.Payload = payload
                    Else
                        stream.Seek(length - 2, SeekOrigin.Current)
                    End If
                    If stream.Position > stream.Length Then Return False
                    segments.Add(segment)
                End While
            End Using
            Return False
        End Function

        ' ---------------------------------------------------------------- IPTC

        ''' <summary>Eine Photoshop-Ressource im APP13: Signatur (meist "8BIM"), Kennung, Name in
        ''' der Rohform (Pascal-Zeichenkette samt Fuellbyte) und Inhalt.</summary>
        Private NotInheritable Class PhotoshopResource
            Public Signature As Byte()
            Public Id As Integer
            Public RawName As Byte()
            Public Data As Byte()
        End Class

        ''' <summary>Ein IPTC-Datensatz: Record, Nummer und Wert.</summary>
        Private NotInheritable Class IptcDataSet
            Public Record As Integer
            Public DataSet As Integer
            Public Value As Byte()
        End Class

        ''' <summary>Der neue Inhalt des APP13-Segments mit den Stichwoertern als IPTC 2:25, oder
        ''' Nothing, wenn nichts zu tun ist oder der vorhandene Block nicht sicher zerlegbar ist.
        '''
        ''' Drei Regeln: die uebrigen Datensaetze und Ressourcen bleiben, wie sie sind; nicht-ASCII
        ''' wird nur geschrieben, wenn der Block UTF-8 erklaert (1:90) oder noch gar keine
        ''' Zeichensatzangabe traegt, dann wird sie gesetzt; und eine vorhandene Pruefsumme
        ''' (Ressource 0x0425) wird neu gerechnet, sonst haelt ein anderes Programm den IPTC-Block
        ''' fuer veraltet und zieht ihm das XMP vor.
        '''
        ''' Der Standard begrenzt ein Stichwort auf 64 Byte. Laengere werden trotzdem ganz
        ''' geschrieben: gekuerzt kaeme beim naechsten Einlesen ein anderes Wort zurueck, und die
        ''' gaengigen Leser nehmen auch laengere an.</summary>
        Private Shared Function BuildKeywordPhotoshopPayload(existing As Byte(), keywords As List(Of String)) As Byte()
            Dim resources As New List(Of PhotoshopResource)()
            If existing IsNot Nothing Then
                If Not ParsePhotoshopResources(existing, resources) Then
                    DiagnosticLogService.LogAlways("Keywords.Iptc", "Photoshop-Block nicht zerlegbar")
                    Return Nothing
                End If
            ElseIf keywords.Count = 0 Then
                Return Nothing
            End If

            Dim iptcResource = resources.FirstOrDefault(Function(r) r.Id = &H404 AndAlso IsEightBim(r.Signature))
            Dim dataSets As New List(Of IptcDataSet)()
            If iptcResource IsNot Nothing Then
                If Not ParseIptcDataSets(iptcResource.Data, dataSets) Then
                    DiagnosticLogService.LogAlways("Keywords.Iptc", "IPTC-Block nicht zerlegbar")
                    Return Nothing
                End If
            ElseIf keywords.Count = 0 Then
                Return Nothing
            End If

            dataSets.RemoveAll(Function(d) d.Record = 2 AndAlso d.DataSet = 25)

            Dim needsUtf8 = keywords.Any(Function(k) k.Any(Function(c) AscW(c) > 127))
            Dim charset = dataSets.FirstOrDefault(Function(d) d.Record = 1 AndAlso d.DataSet = 90)
            If needsUtf8 Then
                If charset Is Nothing Then
                    Dim lastRecord1 = dataSets.FindLastIndex(Function(d) d.Record = 1)
                    dataSets.Insert(lastRecord1 + 1, New IptcDataSet With {.Record = 1, .DataSet = 90, .Value = IptcUtf8Marker})
                ElseIf Not charset.Value.SequenceEqual(IptcUtf8Marker) Then
                    DiagnosticLogService.LogAlways("Keywords.Iptc", "IPTC-Block mit fremdem Zeichensatz, Stichwoerter nicht geschrieben")
                    Return Nothing
                End If
            End If

            If keywords.Count > 0 AndAlso Not dataSets.Any(Function(d) d.Record = 2 AndAlso d.DataSet = 0) Then
                Dim afterRecord1 = dataSets.FindLastIndex(Function(d) d.Record < 2)
                dataSets.Insert(afterRecord1 + 1, New IptcDataSet With {.Record = 2, .DataSet = 0, .Value = {CByte(0), CByte(4)}})
            End If

            Dim insertAt = dataSets.FindIndex(Function(d) d.Record > 2 OrElse (d.Record = 2 AndAlso d.DataSet > 25))
            If insertAt < 0 Then insertAt = dataSets.Count
            For Each keyword In keywords
                Dim value = Encoding.UTF8.GetBytes(keyword)
                If value.Length > 32767 Then Continue For
                dataSets.Insert(insertAt, New IptcDataSet With {.Record = 2, .DataSet = 25, .Value = value})
                insertAt += 1
            Next

            Dim iptcBytes = SerializeIptcDataSets(dataSets)
            If iptcResource Is Nothing Then
                iptcResource = New PhotoshopResource With {
                    .Signature = Encoding.ASCII.GetBytes("8BIM"), .Id = &H404, .RawName = {CByte(0), CByte(0)}}
                resources.Add(iptcResource)
            End If
            iptcResource.Data = iptcBytes

            Dim digest = resources.FirstOrDefault(Function(r) r.Id = &H425 AndAlso IsEightBim(r.Signature))
            If digest IsNot Nothing Then digest.Data = MD5.HashData(iptcBytes)

            Using result As New MemoryStream()
                result.Write(JpegPhotoshopSignature, 0, JpegPhotoshopSignature.Length)
                For Each resource In resources
                    result.Write(resource.Signature, 0, 4)
                    result.WriteByte(CByte((resource.Id >> 8) And &HFF))
                    result.WriteByte(CByte(resource.Id And &HFF))
                    result.Write(resource.RawName, 0, resource.RawName.Length)
                    Dim size = resource.Data.Length
                    result.WriteByte(CByte((size >> 24) And &HFF))
                    result.WriteByte(CByte((size >> 16) And &HFF))
                    result.WriteByte(CByte((size >> 8) And &HFF))
                    result.WriteByte(CByte(size And &HFF))
                    result.Write(resource.Data, 0, size)
                    If (size Mod 2) = 1 Then result.WriteByte(0)
                Next
                Return result.ToArray()
            End Using
        End Function

        Private Shared Function IsEightBim(signature As Byte()) As Boolean
            Return signature IsNot Nothing AndAlso signature.Length = 4 AndAlso
                   signature(0) = AscW("8"c) AndAlso signature(1) = AscW("B"c) AndAlso
                   signature(2) = AscW("I"c) AndAlso signature(3) = AscW("M"c)
        End Function

        ''' <summary>Zerlegt den Inhalt eines APP13 hinter der Kennung. False, sobald eine Laenge
        ''' ueber das Ende zeigt oder etwas anderes als eine Ressource folgt.</summary>
        Private Shared Function ParsePhotoshopResources(payload As Byte(), resources As List(Of PhotoshopResource)) As Boolean
            Dim pos = JpegPhotoshopSignature.Length
            While pos < payload.Length
                ' Fuellbytes am Ende kommen vor; sie gehoeren zu keiner Ressource.
                If payload.Skip(pos).All(Function(b) b = 0) Then Exit While
                If pos + 12 > payload.Length Then Return False
                Dim signature(3) As Byte
                Buffer.BlockCopy(payload, pos, signature, 0, 4)
                Dim sigText = Encoding.ASCII.GetString(signature)
                If sigText <> "8BIM" AndAlso sigText <> "8B64" AndAlso sigText <> "PHUT" AndAlso
                   sigText <> "AgHg" AndAlso sigText <> "DCSR" AndAlso sigText <> "MeSa" Then Return False
                Dim id = (CInt(payload(pos + 4)) << 8) Or CInt(payload(pos + 5))
                Dim nameLength = CInt(payload(pos + 6))
                Dim rawNameLength = nameLength + 1
                If (rawNameLength Mod 2) = 1 Then rawNameLength += 1
                Dim sizeAt = pos + 6 + rawNameLength
                If sizeAt + 4 > payload.Length Then Return False
                Dim rawName(rawNameLength - 1) As Byte
                Buffer.BlockCopy(payload, pos + 6, rawName, 0, rawNameLength)
                Dim size = (CLng(payload(sizeAt)) << 24) Or (CLng(payload(sizeAt + 1)) << 16) Or
                           (CLng(payload(sizeAt + 2)) << 8) Or CLng(payload(sizeAt + 3))
                Dim dataAt = sizeAt + 4
                If size < 0 OrElse dataAt + size > payload.Length Then Return False
                Dim data(CInt(size) - 1) As Byte
                Buffer.BlockCopy(payload, dataAt, data, 0, CInt(size))
                resources.Add(New PhotoshopResource With {.Signature = signature, .Id = id, .RawName = rawName, .Data = data})
                pos = dataAt + CInt(size)
                If (size Mod 2) = 1 Then pos += 1
            End While
            Return True
        End Function

        ''' <summary>Zerlegt einen IPTC-IIM-Block in Datensaetze. Erweiterte Laengen (Bit 15
        ''' gesetzt) werden abgelehnt: sie kommen bei Text nicht vor, und ein falsch gelesener
        ''' Block wuerde beim Zurueckschreiben zerstoert.</summary>
        Private Shared Function ParseIptcDataSets(data As Byte(), dataSets As List(Of IptcDataSet)) As Boolean
            Dim pos = 0
            While pos < data.Length
                If data(pos) = 0 AndAlso data.Skip(pos).All(Function(b) b = 0) Then Exit While
                If pos + 5 > data.Length OrElse data(pos) <> &H1C Then Return False
                Dim record = CInt(data(pos + 1))
                Dim number = CInt(data(pos + 2))
                Dim length = (CInt(data(pos + 3)) << 8) Or CInt(data(pos + 4))
                If (length And &H8000) <> 0 Then Return False
                If pos + 5 + length > data.Length Then Return False
                Dim value(length - 1) As Byte
                Buffer.BlockCopy(data, pos + 5, value, 0, length)
                dataSets.Add(New IptcDataSet With {.Record = record, .DataSet = number, .Value = value})
                pos += 5 + length
            End While
            Return True
        End Function

        Private Shared Function SerializeIptcDataSets(dataSets As List(Of IptcDataSet)) As Byte()
            Using result As New MemoryStream()
                For Each item In dataSets
                    result.WriteByte(&H1C)
                    result.WriteByte(CByte(item.Record))
                    result.WriteByte(CByte(item.DataSet))
                    result.WriteByte(CByte((item.Value.Length >> 8) And &HFF))
                    result.WriteByte(CByte(item.Value.Length And &HFF))
                    result.Write(item.Value, 0, item.Value.Length)
                Next
                Return result.ToArray()
            End Using
        End Function

        ' ---------------------------------------------------------------- PNG

        Private Const PngXmpKeyword As String = "XML:com.adobe.xmp"

        ''' <summary>PNG: der iTXt-Block mit dem Schluessel "XML:com.adobe.xmp" wird ersetzt oder
        ''' vor dem ersten IDAT eingesetzt. Geschrieben wird er immer unkomprimiert, wie es die
        ''' XMP-Spezifikation fuer PNG empfiehlt.</summary>
        Private Shared Function WritePngKeywords(path As String, keywords As List(Of String)) As Boolean
            Dim bytes = File.ReadAllBytes(path)
            If Not IsPngBytes(bytes) Then Return False

            Dim chunks As New List(Of (Offset As Integer, Length As Integer, Type As String))()
            Dim pos = 8
            While pos + 12 <= bytes.Length
                Dim dataLength = ReadInt32BE(bytes, pos)
                If dataLength < 0 OrElse pos + 12L + dataLength > bytes.Length Then Return False
                Dim chunkType = Encoding.ASCII.GetString(bytes, pos + 4, 4)
                chunks.Add((pos, dataLength + 12, chunkType))
                pos += dataLength + 12
                If chunkType = "IEND" Then Exit While
            End While
            If chunks.Count = 0 OrElse chunks.Last().Type <> "IEND" Then Return False

            Dim xmpIndex = -1
            Dim existing As Byte() = Nothing
            For i = 0 To chunks.Count - 1
                If chunks(i).Type <> "iTXt" Then Continue For
                Dim packet = ReadPngXmpPacket(bytes, chunks(i).Offset + 8, chunks(i).Length - 12)
                If packet Is Nothing Then Continue For
                xmpIndex = i
                existing = packet
                Exit For
            Next

            Dim failed = False
            Dim newPacket = BuildKeywordXmpPacket(existing, keywords, failed)
            If newPacket Is Nothing Then Return False

            Dim keywordBytes = Encoding.ASCII.GetBytes(PngXmpKeyword)
            Dim payload(keywordBytes.Length + 5 + newPacket.Length - 1) As Byte
            Buffer.BlockCopy(keywordBytes, 0, payload, 0, keywordBytes.Length)
            ' Null hinter dem Schluessel, Kompression aus, Methode 0, leere Sprache, leere Uebersetzung.
            Buffer.BlockCopy(newPacket, 0, payload, keywordBytes.Length + 5, newPacket.Length)
            Dim newChunk = CreatePngChunk("iTXt", payload)

            Dim insertBefore = If(xmpIndex >= 0, xmpIndex, chunks.FindIndex(Function(c) c.Type = "IDAT"))
            If insertBefore < 0 Then Return False
            Return WriteFileAtomic(path,
                Sub(destination)
                    destination.Write(bytes, 0, 8)
                    For i = 0 To chunks.Count - 1
                        If i = insertBefore Then destination.Write(newChunk, 0, newChunk.Length)
                        If i = xmpIndex Then Continue For
                        destination.Write(bytes, chunks(i).Offset, chunks(i).Length)
                    Next
                End Sub)
        End Function

        ''' <summary>Das XMP-Paket aus einem iTXt-Block, oder Nothing, wenn der Block etwas anderes
        ''' traegt. Ein komprimierter Block wird entpackt.</summary>
        Private Shared Function ReadPngXmpPacket(bytes As Byte(), dataStart As Integer, dataLength As Integer) As Byte()
            Dim keywordBytes = Encoding.ASCII.GetBytes(PngXmpKeyword)
            If dataLength < keywordBytes.Length + 5 Then Return Nothing
            For i = 0 To keywordBytes.Length - 1
                If bytes(dataStart + i) <> keywordBytes(i) Then Return Nothing
            Next
            Dim pos = dataStart + keywordBytes.Length
            If bytes(pos) <> 0 Then Return Nothing
            Dim compressed = bytes(pos + 1) = 1
            pos += 3
            Dim dataEnd = dataStart + dataLength
            ' Sprache und uebersetzter Schluessel, je mit Null abgeschlossen.
            For field = 1 To 2
                While pos < dataEnd AndAlso bytes(pos) <> 0
                    pos += 1
                End While
                pos += 1
            Next
            If pos > dataEnd Then Return Nothing
            Dim text(dataEnd - pos - 1) As Byte
            Buffer.BlockCopy(bytes, pos, text, 0, text.Length)
            If Not compressed Then Return text
            Using input As New MemoryStream(text), inflater As New ZLibStream(input, CompressionMode.Decompress), output As New MemoryStream()
                inflater.CopyTo(output)
                Return output.ToArray()
            End Using
        End Function

        ' ---------------------------------------------------------------- WebP

        ''' <summary>WebP: der Block "XMP " wird ersetzt oder hinten angehaengt, im VP8X-Kopf wird
        ''' das XMP-Flag gesetzt. Fehlt der VP8X-Kopf (einfaches WebP), entsteht einer aus der
        ''' Bildgroesse. Anders als WithWebpMetadata bleibt das Farbprofil (ICCP) stehen: hier wird
        ''' nicht neu kodiert, die Bildpunkte gehoeren noch zu ihm.</summary>
        Private Shared Function WriteWebpKeywords(path As String, keywords As List(Of String)) As Boolean
            Dim bytes = File.ReadAllBytes(path)
            Dim chunks = ReadWebpChunks(bytes)
            If chunks.Count = 0 Then Return False
            ' Nur weiter, wenn die Bloecke die ganze Datei abdecken; sonst ginge beim Neuschreiben
            ' etwas verloren, das ReadWebpChunks nicht erkannt hat.
            Dim covered = 12L
            For Each chunk In chunks
                covered += 8 + chunk.Data.Length + (chunk.Data.Length Mod 2)
            Next
            If covered <> bytes.Length Then Return False

            Dim xmpChunk = chunks.FirstOrDefault(Function(c) c.Type = "XMP ")
            Dim failed = False
            Dim newPacket = BuildKeywordXmpPacket(If(xmpChunk?.Data, Nothing), keywords, failed)
            If newPacket Is Nothing Then Return False

            If xmpChunk IsNot Nothing Then
                xmpChunk.Data = newPacket
            Else
                chunks.Add(New WebpChunk With {.Type = "XMP ", .Data = newPacket})
            End If

            Dim vp8x = chunks.FirstOrDefault(Function(c) c.Type = "VP8X")
            If vp8x IsNot Nothing Then
                If vp8x.Data.Length < 10 Then Return False
                vp8x.Data(0) = CByte(vp8x.Data(0) Or &H4)
            Else
                Dim imageChunk = chunks.FirstOrDefault(Function(c) c.Type = "VP8 " OrElse c.Type = "VP8L")
                If imageChunk Is Nothing Then Return False
                Dim size = ReadWebpImageSize(imageChunk)
                If size.Width <= 0 OrElse size.Height <= 0 Then Return False
                Dim flags As Byte = &H4
                If chunks.Any(Function(c) c.Type = "ALPH") Then flags = CByte(flags Or &H10)
                If imageChunk.Type = "VP8L" AndAlso imageChunk.Data.Length >= 5 AndAlso (imageChunk.Data(4) And &H10) <> 0 Then
                    flags = CByte(flags Or &H10)
                End If
                chunks.Insert(0, CreateWebpVp8xChunk(flags, size.Width, size.Height))
            End If

            Dim result = BuildWebpFile(chunks)
            Return WriteFileAtomic(path, Sub(destination) destination.Write(result, 0, result.Length))
        End Function

        ' ---------------------------------------------------------------- Speichern

        ''' <summary>Nach SaveImage: die manuellen Stichwoerter der Quelle aus dem Katalog in die
        ''' Zieldatei, wenn die Einstellung das verlangt. KI-Stichwoerter nie, sie stehen gar nicht in
        ''' der Spalte, aus der hier gelesen wird. Ein Fehlschlag laesst das Speichern nicht
        ''' scheitern, das Bild steht dann schon auf der Platte (wie beim Urheberrechtshinweis).</summary>
        Private Shared Sub ApplyCatalogKeywords(sourcePath As String, targetPath As String)
            Try
                Dim fields = KeywordFileWriter.ConfiguredFields()
                If fields = KeywordFileFields.None OrElse Not CanWriteKeywordsInto(targetPath) Then Return
                If LibraryService.IsServerPseudoPath(sourcePath) Then Return
                Dim tags = LibraryService.Instance.GetTags(sourcePath)
                ' Speichern unter in eine neue Datei: ohne Stichwoerter gibt es nichts zu tun. Ueber
                ' das Original gespeichert, kann eine leere Liste ein entferntes Stichwort meinen.
                If tags.Count = 0 AndAlso Not PathIdentity.AreSame(sourcePath, targetPath) Then Return
                WriteKeywordsIntoFile(targetPath, tags, fields)
            Catch ex As Exception
                DiagnosticLogService.LogException("Save.Keywords", ex)
            End Try
        End Sub

    End Class

End Namespace
