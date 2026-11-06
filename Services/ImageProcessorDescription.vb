Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Text
Imports System.Xml.Linq

' Titel, Ueberschrift, Beschreibung, Urheber und Quellenangabe in die Bilddatei selbst: XMP in
' JPEG, PNG und WebP, dazu IPTC im JPEG. Derselbe Schreibweg wie bei den Stichwoertern
' (ImageProcessorKeywords.vb), nur mit anderen Feldern. Eingang ist ImageDescriptionService.
' Kein eigener Zustand.
Namespace Services

    Partial Public Class ImageProcessor

        ''' <summary>Schreibt die genannten Felder in die Datei und ERSETZT dabei die vorhandenen;
        ''' ein leerer Wert entfernt das Feld. Alle uebrigen Felder und die Bilddaten bleiben Byte
        ''' fuer Byte liegen. Laesst sich ein vorhandener Block nicht sicher zerlegen, bleibt die
        ''' Datei unangetastet, und zwar GANZ: im JPEG wird dann auch der andere Block nicht neu
        ''' geschrieben (RewriteJpegXmpAndIptc, alles oder nichts).
        '''
        ''' Im JPEG geht IPTC IMMER mit, nicht nur, wenn die Einstellung fuer die Stichwoerter es
        ''' verlangt: der Dialog ist ein bewusster Schreibbefehl, und ein Programm, das nur IPTC
        ''' liest, zeigte sonst den alten Titel neben einem neuen im XMP.</summary>
        ''' <returns>True, wenn die Datei neu geschrieben wurde; False, wenn es nichts zu aendern
        ''' gab.</returns>
        ''' <exception cref="InvalidDataException">Ein Block oder der Aufbau der Datei laesst sich
        ''' nicht sicher lesen; die Datei ist unveraendert.</exception>
        ''' <exception cref="IOException">Das Schreiben ist gescheitert; die Datei ist unveraendert
        ''' (WriteFileAtomic).</exception>
        Public Shared Function WriteDescriptionIntoFile(path As String,
                                                        changes As IReadOnlyDictionary(Of ImageDescriptionField, String)) As Boolean
            If changes Is Nothing OrElse changes.Count = 0 Then Return False
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Throw New FileNotFoundException("Datei nicht gefunden", path)
            Dim xmpBuilder As Func(Of Byte(), Byte()) = Function(existing) BuildDescriptionXmpPacket(existing, changes)
            ' Bewusst OHNE Fangnetz: "nichts zu aendern" (False) und "geht nicht" (Ausnahme) muss
            ' der Aufrufer auseinanderhalten koennen, sonst meldet er einen Fehlschlag als Erfolg.
            Select Case IO.Path.GetExtension(path).ToLowerInvariant()
                Case ".jpg", ".jpeg"
                    Dim replacements As New Dictionary(Of Integer, List(Of String))()
                    For Each pair In changes
                        replacements(ImageDescriptionService.IptcDataSet(pair.Key)) =
                            ImageDescriptionService.IptcValues(pair.Key, pair.Value)
                    Next
                    Return RewriteJpegXmpAndIptc(path, xmpBuilder,
                                                 Function(existing) BuildPhotoshopIptcPayload(existing, replacements))
                Case ".png"
                    Return RewritePngXmp(path, xmpBuilder)
                Case ".webp"
                    Return RewriteWebpXmp(path, xmpBuilder)
                Case Else
                    Throw New NotSupportedException("Format traegt die Felder nicht in der Datei: " & path)
            End Select
        End Function

        ''' <summary>Das XMP-Paket mit den geaenderten Feldern, oder Nothing, wenn nichts zu
        ''' schreiben ist (kein Paket und nur leere Werte, oder am Paket aendert sich nichts). Ein
        ''' vorhandenes Paket, das sich nicht lesen laesst, WIRFT: dann darf auch der IPTC-Block
        ''' nicht allein neu geschrieben werden (RewriteJpegXmpAndIptc, alles oder nichts).</summary>
        Private Shared Function BuildDescriptionXmpPacket(packet As Byte(),
                                                          changes As IReadOnlyDictionary(Of ImageDescriptionField, String)) As Byte()
            Dim doc As XDocument
            If packet Is Nothing Then
                If Not changes.Values.Any(Function(v) Not String.IsNullOrEmpty(v)) Then Return Nothing
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
            If Not ImageDescriptionService.ApplyToXmp(descriptions, target, changes) Then Return Nothing
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
