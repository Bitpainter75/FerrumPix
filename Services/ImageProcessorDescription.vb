Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Xml.Linq

' Metadaten in die Bilddatei selbst, ueber eine Aenderung am XMP-Paket: XMP in JPEG, PNG und WebP,
' dazu IPTC im JPEG. Derselbe Byteweg wie bei den Stichwoertern (ImageProcessorKeywords.vb).
' Nutzer sind ImageDescriptionService (Titel, Beschreibung ...) und CatalogMetadataService
' (Bewertung, Etikett, Markierung, Stichwoerter; Felder entfernen). Kein eigener Zustand.
Namespace Services

    Partial Public Class ImageProcessor

        ''' <summary>Schreibt die genannten beschreibenden Felder in die Datei und ERSETZT dabei die
        ''' vorhandenen; ein leerer Wert entfernt das Feld. Im JPEG geht IPTC IMMER mit, nicht nur,
        ''' wenn die Einstellung fuer die Stichwoerter es verlangt: der Dialog ist ein bewusster
        ''' Schreibbefehl, und ein Programm, das nur IPTC liest, zeigte sonst den alten Titel neben
        ''' einem neuen im XMP. Die Regeln des Schreibens stehen bei <see cref="EditXmpInFile"/>.</summary>
        Public Shared Function WriteDescriptionIntoFile(path As String,
                                                        changes As IReadOnlyDictionary(Of ImageDescriptionField, String)) As Boolean
            If changes Is Nothing OrElse changes.Count = 0 Then Return False
            Dim replacements As New Dictionary(Of Integer, List(Of String))()
            For Each pair In changes
                replacements(ImageDescriptionService.IptcDataSet(pair.Key)) =
                    ImageDescriptionService.IptcValues(pair.Key, pair.Value)
            Next
            Return EditXmpInFile(path,
                                 createPacket:=changes.Values.Any(Function(v) Not String.IsNullOrEmpty(v)),
                                 edit:=Function(descriptions, target) ImageDescriptionService.ApplyToXmp(descriptions, target, changes),
                                 iptcReplacements:=replacements)
        End Function

        ''' <summary>Aendert das eingebettete XMP-Paket einer JPEG-, PNG- oder WebP-Datei und, im
        ''' JPEG, die genannten IPTC-Datensaetze von Record 2 (je Nummer die neue Werteliste, leer
        ''' entfernt sie). Alle uebrigen Felder und die Bilddaten bleiben Byte fuer Byte liegen.
        '''
        ''' <paramref name="edit"/> bekommt alle rdf:Description-Knoten und den, in den neue Werte
        ''' gehoeren, und meldet, ob es etwas geaendert hat. Fehlt das Paket, entsteht eines nur bei
        ''' <paramref name="createPacket"/>; ohne Paket und ohne Wert gibt es nichts zu tun.
        '''
        ''' Laesst sich ein vorhandener Block nicht sicher zerlegen, bleibt die Datei GANZ
        ''' unangetastet: im JPEG wird dann auch der andere Block nicht geschrieben
        ''' (RewriteJpegXmpAndIptc, alles oder nichts).</summary>
        ''' <returns>True, wenn die Datei neu geschrieben wurde; False, wenn es nichts zu aendern
        ''' gab.</returns>
        ''' <exception cref="InvalidDataException">Ein Block oder der Aufbau der Datei laesst sich
        ''' nicht sicher lesen; die Datei ist unveraendert.</exception>
        ''' <exception cref="IOException">Das Schreiben ist gescheitert; die Datei ist unveraendert
        ''' (WriteFileAtomic).</exception>
        Friend Shared Function EditXmpInFile(path As String, createPacket As Boolean,
                                             edit As Func(Of IEnumerable(Of XElement), XElement, Boolean),
                                             Optional iptcReplacements As Dictionary(Of Integer, List(Of String)) = Nothing) As Boolean
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Throw New FileNotFoundException("Datei nicht gefunden", path)
            Dim xmpBuilder As Func(Of Byte(), Byte()) = Function(existing) BuildEditedXmpPacket(existing, createPacket, edit)
            ' Bewusst OHNE Fangnetz: "nichts zu aendern" (False) und "geht nicht" (Ausnahme) muss
            ' der Aufrufer auseinanderhalten koennen, sonst meldet er einen Fehlschlag als Erfolg.
            Select Case IO.Path.GetExtension(path).ToLowerInvariant()
                Case ".jpg", ".jpeg"
                    Dim iptcBuilder As Func(Of Byte(), Byte()) = Nothing
                    If iptcReplacements IsNot Nothing AndAlso iptcReplacements.Count > 0 Then
                        iptcBuilder = Function(existing) BuildPhotoshopIptcPayload(existing, iptcReplacements)
                    End If
                    Return RewriteJpegXmpAndIptc(path, xmpBuilder, iptcBuilder)
                Case ".png"
                    Return RewritePngXmp(path, xmpBuilder)
                Case ".webp"
                    Return RewriteWebpXmp(path, xmpBuilder)
                Case Else
                    Throw New NotSupportedException("Format traegt kein XMP in der Datei: " & path)
            End Select
        End Function

        ''' <summary>Das geaenderte XMP-Paket, oder Nothing, wenn nichts zu schreiben ist (kein
        ''' Paket und keines anzulegen, oder am Paket aendert sich nichts). Ein vorhandenes Paket,
        ''' das sich nicht lesen laesst, WIRFT: dann darf auch der IPTC-Block nicht allein neu
        ''' geschrieben werden.</summary>
        Private Shared Function BuildEditedXmpPacket(packet As Byte(), createPacket As Boolean,
                                                     edit As Func(Of IEnumerable(Of XElement), XElement, Boolean)) As Byte()
            Dim doc As XDocument
            If packet Is Nothing Then
                If Not createPacket Then Return Nothing
                doc = XDocument.Parse(NewEmptyXmpPacket(), LoadOptions.PreserveWhitespace)
            Else
                Try
                    Dim text = Encoding.UTF8.GetString(packet).TrimEnd(ChrW(0), " "c, ControlChars.Cr, ControlChars.Lf, ControlChars.Tab)
                    If text.Length > 0 AndAlso text(0) = ChrW(&HFEFF) Then text = text.Substring(1)
                    doc = XDocument.Parse(text, LoadOptions.PreserveWhitespace)
                Catch ex As Exception
                    Throw New InvalidDataException("XMP-Paket nicht lesbar", ex)
                End Try
            End If

            Dim rdfRoot = doc.Descendants(KeywordRdfNs + "RDF").FirstOrDefault()
            If rdfRoot Is Nothing Then Throw New InvalidDataException("XMP-Paket ohne rdf:RDF")
            Dim descriptions = rdfRoot.Elements(KeywordRdfNs + "Description").ToList()
            Dim target = descriptions.FirstOrDefault()
            If target Is Nothing Then
                target = New XElement(KeywordRdfNs + "Description", New XAttribute(KeywordRdfNs + "about", ""))
                rdfRoot.Add(target)
                descriptions.Add(target)
            End If
            If Not edit(descriptions, target) Then Return Nothing
            Return Encoding.UTF8.GetBytes(doc.ToString(SaveOptions.DisableFormatting))
        End Function

        ''' <summary>Ein leeres Paket im ueblichen Rahmen aus xpacket-Anweisungen.</summary>
        Private Shared Function NewEmptyXmpPacket() As String
            Dim xNs As XNamespace = "adobe:ns:meta/"
            Dim meta = New XElement(xNs + "xmpmeta",
                New XAttribute(XNamespace.Xmlns + "x", xNs.NamespaceName),
                New XElement(KeywordRdfNs + "RDF",
                    New XAttribute(XNamespace.Xmlns + "rdf", KeywordRdfNs.NamespaceName),
                    New XElement(KeywordRdfNs + "Description",
                        New XAttribute(KeywordRdfNs + "about", ""))))
            Return "<?xpacket begin=""" & ChrW(&HFEFF) & """ id=""W5M0MpCehiHzreSzNTczkc9d""?>" &
                   meta.ToString(SaveOptions.DisableFormatting) &
                   "<?xpacket end=""w""?>"
        End Function

    End Class

End Namespace
