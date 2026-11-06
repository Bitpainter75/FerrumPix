Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Xml.Linq

Namespace Services

    ''' <summary>Die Angaben, die der Katalog als EIGENE fuehrt und die es zugleich in Datei und
    ''' Beistelldatei gibt. Nur bei ihnen koennen zwei echte Staende auseinanderlaufen; Kamera, Zeit,
    ''' Ort und die beschreibenden Felder sind im Katalog nur eine Abschrift der Datei.</summary>
    <Flags>
    Public Enum CatalogField
        None = 0
        Rating = 1
        ColorLabel = 2
        PickState = 4
        Keywords = 8
        All = 15
    End Enum

    ''' <summary>Welche Angaben "Metadaten entfernen" nimmt. Everything heisst in der Datei:
    ''' ohne EXIF, IPTC, XMP und Farbprofil neu schreiben; in der Beistelldatei: alle bekannten
    ''' Felder; im Katalog: Bewertung, Etikett, Markierung und Stichwoerter.</summary>
    <Flags>
    Public Enum MetadataRemovalFields
        None = 0
        Rating = 1
        ColorLabel = 2
        PickState = 4
        Keywords = 8
        ''' <summary>Titel, Ueberschrift, Beschreibung, Urheber, Quellenangabe.</summary>
        Description = 16
        Copyright = 32
        ''' <summary>Koordinate und Ortsnamen.</summary>
        Place = 64
        Everything = 128
    End Enum

    ''' <summary>Ein Stand dieser vier Angaben, in der Schreibweise des Katalogs: Bewertung 0 bis 5,
    ''' Etikett als Hex-Wert der Palette ("" keins), Markierung -1/0/1, Stichwoerter flach.</summary>
    Public Class CatalogValues
        Public Property Rating As Integer
        Public Property ColorLabel As String = ""
        Public Property PickState As Integer
        Public Property Keywords As New List(Of String)()

        Public Function Clone() As CatalogValues
            Return New CatalogValues With {.Rating = Rating, .ColorLabel = If(ColorLabel, ""), .PickState = PickState,
                                           .Keywords = New List(Of String)(If(Keywords, New List(Of String)()))}
        End Function

        ''' <summary>Gleich in der genannten Angabe? Stichwoerter ohne Ruecksicht auf Reihenfolge
        ''' und Gross- und Kleinschreibung, wie ueberall im Katalog.</summary>
        Public Function SameAs(other As CatalogValues, field As CatalogField) As Boolean
            If other Is Nothing Then Return False
            Select Case field
                Case CatalogField.Rating : Return Rating = other.Rating
                Case CatalogField.ColorLabel : Return String.Equals(If(ColorLabel, ""), If(other.ColorLabel, ""), StringComparison.OrdinalIgnoreCase)
                Case CatalogField.PickState : Return PickState = other.PickState
                Case CatalogField.Keywords
                    Dim mine As New HashSet(Of String)(If(Keywords, New List(Of String)()), StringComparer.OrdinalIgnoreCase)
                    Return mine.SetEquals(If(other.Keywords, New List(Of String)()))
                Case Else : Return True
            End Select
        End Function
    End Class

    ''' <summary>Was an der Datei steht, nach Herkunft getrennt.</summary>
    Public Class FileCatalogSources
        ''' <summary>Was IN der Datei steht (eingebettetes XMP, IPTC).</summary>
        Public Property Embedded As New CatalogValues()
        ''' <summary>Was in der Beistelldatei steht; Nothing, wenn es keine gibt.</summary>
        Public Property Sidecar As CatalogValues
        ''' <summary>Was andere Programme sehen: je Angabe die Beistelldatei, wo sie etwas sagt,
        ''' sonst die Datei. Stichwoerter aus beiden zusammen, wie beim Einlesen in den Katalog.</summary>
        Public Property Effective As New CatalogValues()
        ''' <summary>Je Angabe: sagen Datei und Beistelldatei verschiedenes?</summary>
        Public Property Conflicts As CatalogField
    End Class

    ''' <summary>
    ''' Bewertung, Etikett, Markierung und Stichwoerter zwischen Datei, Beistelldatei und Katalog:
    ''' lesen, in den Katalog uebernehmen, in die Dateien schreiben, aus der Beistelldatei entfernen.
    '''
    ''' Geschrieben wird ueber <see cref="MetadataFileWriter"/>: JPEG, PNG und WebP in die Datei (XMP,
    ''' im JPEG die Stichwoerter auch als IPTC 2:25), alles andere in die Beistelldatei; Beistelldatei
    ''' zuerst, Zuruecksetzen beim Scheitern der Bilddatei.
    '''
    ''' Der Katalog wird ueber die gewohnten Wege von LibraryService gesetzt, also mit Spiegel in die
    ''' .fpxmp und OHNE den Abgleich in die XMP-Beistelldatei: der waere hier ein zweiter, ungefragter
    ''' Schreibweg in eine Datei, die der Aufrufer bewusst getrennt behandelt.
    ''' </summary>
    Public NotInheritable Class CatalogMetadataService

        Private Sub New()
        End Sub

        Private Shared ReadOnly XmpNs As XNamespace = "http://ns.adobe.com/xap/1.0/"
        Private Shared ReadOnly XmpDmNs As XNamespace = "http://ns.adobe.com/xmp/1.0/DynamicMedia/"
        Private Shared ReadOnly DcNs As XNamespace = "http://purl.org/dc/elements/1.1/"
        Private Shared ReadOnly LrNs As XNamespace = "http://ns.adobe.com/lightroom/1.0/"
        Private Shared ReadOnly RdfNs As XNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#"
        Private Shared ReadOnly PhotoshopNs As XNamespace = "http://ns.adobe.com/photoshop/1.0/"
        Private Shared ReadOnly ExifNs As XNamespace = "http://ns.adobe.com/exif/1.0/"

        Public Shared ReadOnly AllFields As CatalogField() = {
            CatalogField.Rating, CatalogField.ColorLabel, CatalogField.PickState, CatalogField.Keywords}

        ' ---------------------------------------------------------------- Lesen

        ''' <summary>Was an der Datei steht: eingebettet, in der Beistelldatei und beides zusammen
        ''' so, wie andere Programme es sehen.</summary>
        Public Shared Function ReadFile(imagePath As String) As FileCatalogSources
            Dim result As New FileCatalogSources()
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then Return result
            Try
                result.Embedded = FromExifData(ExifService.ReadExif(imagePath))
            Catch ex As Exception
                DiagnosticLogService.LogException("CatalogMetadata.ReadEmbedded", ex)
            End Try

            Dim sidecar As XmpSidecarService.XmpSidecarData = Nothing
            Dim sidecarPath = XmpSidecarService.FindSidecar(imagePath)
            If Not String.IsNullOrEmpty(sidecarPath) Then
                sidecar = XmpSidecarService.ReadSidecar(sidecarPath)
                result.Sidecar = If(sidecar Is Nothing, New CatalogValues(), FromSidecar(sidecar))
            End If

            Dim effective = result.Embedded.Clone()
            If sidecar IsNot Nothing Then
                If sidecar.Rating.HasValue Then effective.Rating = sidecar.Rating.Value
                If Not String.IsNullOrEmpty(sidecar.ColorLabel) Then effective.ColorLabel = sidecar.ColorLabel
                If sidecar.PickState.HasValue Then effective.PickState = LibraryService.NormalizePickState(sidecar.PickState.Value)
                effective.Keywords = MergeKeywords(result.Sidecar.Keywords, effective.Keywords)
            End If
            result.Effective = effective

            If result.Sidecar IsNot Nothing Then
                For Each field In AllFields
                    If Not result.Embedded.SameAs(result.Sidecar, field) AndAlso HasAny(result.Embedded, field) AndAlso HasAny(result.Sidecar, field) Then
                        result.Conflicts = result.Conflicts Or field
                    End If
                Next
            End If
            Return result
        End Function

        ''' <summary>Was der Katalog fuehrt.</summary>
        Public Shared Function ReadCatalog(imagePath As String) As CatalogValues
            Dim result As New CatalogValues()
            If String.IsNullOrWhiteSpace(imagePath) Then Return result
            Dim meta As LibraryImageMeta = Nothing
            If Not LibraryService.Instance.GetMetaForPaths({imagePath}).TryGetValue(imagePath, meta) OrElse meta Is Nothing Then Return result
            result.Rating = Math.Max(0, Math.Min(5, meta.Rating))
            result.ColorLabel = If(meta.ColorLabel, "")
            result.PickState = LibraryService.NormalizePickState(meta.PickState)
            result.Keywords = If(meta.Tags, New List(Of String)()).ToList()
            Return result
        End Function

        Private Shared Function HasAny(values As CatalogValues, field As CatalogField) As Boolean
            Select Case field
                Case CatalogField.Rating : Return values.Rating <> 0
                Case CatalogField.ColorLabel : Return Not String.IsNullOrEmpty(values.ColorLabel)
                Case CatalogField.PickState : Return values.PickState <> 0
                Case CatalogField.Keywords : Return values.Keywords.Count > 0
                Case Else : Return False
            End Select
        End Function

        Private Shared Function FromExifData(data As ExifData) As CatalogValues
            Dim result As New CatalogValues()
            If data Is Nothing Then Return result
            result.Rating = Math.Max(0, Math.Min(5, If(data.XmpRating, 0)))
            For Each tag In If(data.XmpTags, New List(Of ExifTag)())
                If tag Is Nothing OrElse String.IsNullOrWhiteSpace(tag.Name) Then Continue For
                Select Case tag.Name.Trim().ToLowerInvariant()
                    Case "xmp:label"
                        If result.ColorLabel.Length = 0 Then result.ColorLabel = XmpSidecarService.MapLabel(tag.Value)
                    Case "xmpdm:good"
                        If String.Equals(If(tag.Value, "").Trim(), "True", StringComparison.OrdinalIgnoreCase) Then result.PickState = 1
                        If String.Equals(If(tag.Value, "").Trim(), "False", StringComparison.OrdinalIgnoreCase) Then result.PickState = -1
                End Select
            Next
            result.Keywords = MergeKeywords(If(data.EmbeddedKeywords, New List(Of String)()))
            Return result
        End Function

        Private Shared Function FromSidecar(sidecar As XmpSidecarService.XmpSidecarData) As CatalogValues
            Return New CatalogValues With {
                .Rating = If(sidecar.Rating, 0),
                .ColorLabel = If(sidecar.ColorLabel, ""),
                .PickState = LibraryService.NormalizePickState(If(sidecar.PickState, 0)),
                .Keywords = MergeKeywords(If(sidecar.Keywords, New List(Of String)()))}
        End Function

        ''' <summary>Flach, entdoppelt und in der Schreibweise der Tags-Spalte
        ''' (LibraryService.NormalizeKeyword), in der Reihenfolge des ersten Auftretens.</summary>
        Public Shared Function MergeKeywords(ParamArray lists As IEnumerable(Of String)()) As List(Of String)
            Dim result As New List(Of String)()
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each list In If(lists, Array.Empty(Of IEnumerable(Of String))())
                For Each raw In If(list, Enumerable.Empty(Of String)())
                    Dim keyword = LibraryService.NormalizeKeyword(raw)
                    If keyword.Length > 0 AndAlso seen.Add(keyword) Then result.Add(keyword)
                Next
            Next
            Return result
        End Function

        ' ---------------------------------------------------------------- In den Katalog

        ''' <summary>Setzt die genannten Angaben im Katalog. Die Bewertung ist dort eine Zahl je
        ''' Bild; deshalb Bild fuer Bild, und mit den gewohnten Wegen, damit die .fpxmp und die
        ''' Anzeige in Galerie und Infopanel mitziehen.</summary>
        Public Shared Sub ApplyToCatalog(imagePath As String, values As CatalogValues, fields As CatalogField)
            If String.IsNullOrWhiteSpace(imagePath) OrElse values Is Nothing OrElse fields = CatalogField.None Then Return
            Dim library = LibraryService.Instance
            If (fields And CatalogField.Rating) <> 0 Then library.SetRating(imagePath, Math.Max(0, Math.Min(5, values.Rating)))
            If (fields And CatalogField.ColorLabel) <> 0 Then library.SetColorLabelForMany({imagePath}, If(values.ColorLabel, ""))
            If (fields And CatalogField.PickState) <> 0 Then library.SetPickStateForMany({imagePath}, values.PickState)
            If (fields And CatalogField.Keywords) <> 0 Then library.SetTags(imagePath, MergeKeywords(values.Keywords))
        End Sub

        ' ---------------------------------------------------------------- In die Dateien

        ''' <summary>Schreibt die genannten Angaben an die Datei und ERSETZT dort die vorhandenen:
        ''' eine Bewertung 0, kein Etikett, keine Markierung und eine leere Stichwortliste entfernen
        ''' das jeweilige Feld.</summary>
        Public Shared Function WriteToFile(imagePath As String, values As CatalogValues, fields As CatalogField) As MetadataWriteResult
            If values Is Nothing OrElse fields = CatalogField.None Then Return New MetadataWriteResult With {.Success = True}
            Dim keywords = MergeKeywords(values.Keywords)
            Dim createsValues = ((fields And CatalogField.Rating) <> 0 AndAlso values.Rating <> 0) OrElse
                                ((fields And CatalogField.ColorLabel) <> 0 AndAlso XmpSidecarService.LabelToXmpWord(values.ColorLabel).Length > 0) OrElse
                                ((fields And CatalogField.PickState) <> 0 AndAlso values.PickState <> 0) OrElse
                                ((fields And CatalogField.Keywords) <> 0 AndAlso keywords.Count > 0)
            Dim iptc As Dictionary(Of Integer, List(Of String)) = Nothing
            If (fields And CatalogField.Keywords) <> 0 Then iptc = New Dictionary(Of Integer, List(Of String)) From {{25, keywords}}
            Dim edit As Func(Of IEnumerable(Of XElement), XElement, Boolean) =
                Function(descriptions, target) ApplyToXmp(descriptions, target, values, fields)
            Return MetadataFileWriter.Write(imagePath,
                                            Function() ImageProcessor.EditXmpInFile(imagePath, createsValues, edit, iptc),
                                            edit, createsValues, "CatalogMetadata")
        End Function

        ''' <summary>Die Angaben in einem XMP-Dokument setzen: der alte Wert verschwindet aus ALLEN
        ''' Description-Knoten, als Attribut wie als Element, der neue kommt als Attribut bzw.
        ''' Bag in <paramref name="target"/>. lr:hierarchicalSubject behaelt nur Pfade, deren letzte
        ''' Stufe noch in der Liste steht; sonst kaeme ein entferntes Stichwort beim naechsten
        ''' Einlesen ueber die Hierarchie zurueck.</summary>
        Friend Shared Function ApplyToXmp(descriptions As IEnumerable(Of XElement), target As XElement,
                                          values As CatalogValues, fields As CatalogField) As Boolean
            Dim all = descriptions.ToList()
            Dim changed = False
            If (fields And CatalogField.Rating) <> 0 Then
                changed = RemoveEverywhere(all, XmpNs + "Rating") Or changed
                If values.Rating <> 0 Then
                    EnsurePrefix(target, "xmp", XmpNs)
                    target.SetAttributeValue(XmpNs + "Rating", Math.Max(0, Math.Min(5, values.Rating)).ToString(CultureInfo.InvariantCulture))
                    changed = True
                End If
            End If
            If (fields And CatalogField.ColorLabel) <> 0 Then
                changed = RemoveEverywhere(all, XmpNs + "Label") Or changed
                Dim word = XmpSidecarService.LabelToXmpWord(values.ColorLabel)
                If word.Length > 0 Then
                    EnsurePrefix(target, "xmp", XmpNs)
                    target.SetAttributeValue(XmpNs + "Label", word)
                    changed = True
                End If
            End If
            If (fields And CatalogField.PickState) <> 0 Then
                changed = RemoveEverywhere(all, XmpDmNs + "good") Or changed
                If values.PickState <> 0 Then
                    EnsurePrefix(target, "xmpDM", XmpDmNs)
                    target.SetAttributeValue(XmpDmNs + "good", If(values.PickState > 0, "True", "False"))
                    changed = True
                End If
            End If
            If (fields And CatalogField.Keywords) <> 0 Then
                Dim keywords = MergeKeywords(values.Keywords)
                Dim wanted As New HashSet(Of String)(keywords, StringComparer.OrdinalIgnoreCase)
                changed = RemoveEverywhere(all, DcNs + "subject") Or changed
                For Each description In all
                    For Each hierarchy In description.Elements(LrNs + "hierarchicalSubject").ToList()
                        For Each item In hierarchy.Descendants(RdfNs + "li").ToList()
                            If Not wanted.Contains(item.Value.Split("|"c).Last().Trim()) Then
                                item.Remove()
                                changed = True
                            End If
                        Next
                        If Not hierarchy.Descendants(RdfNs + "li").Any() Then hierarchy.Remove()
                    Next
                Next
                If keywords.Count > 0 Then
                    EnsurePrefix(target, "dc", DcNs)
                    Dim bag = New XElement(RdfNs + "Bag")
                    For Each keyword In keywords
                        bag.Add(New XElement(RdfNs + "li", keyword))
                    Next
                    target.Add(New XElement(DcNs + "subject", bag))
                    changed = True
                End If
            End If
            Return changed
        End Function

        ' ---------------------------------------------------------------- Entfernen

        ''' <summary>Die XMP-Felder je Angabe, wie FerrumPix sie selbst liest oder schreibt. Fremde
        ''' Knoten, etwa das Entwicklungsrezept eines anderen Programms, stehen in keiner Liste und
        ''' bleiben deshalb immer stehen: es wird bereinigt, nicht geloescht.</summary>
        Private Shared Function XmpNamesFor(what As MetadataRemovalFields) As List(Of XName)
            Dim names As New List(Of XName)()
            Dim everything = (what And MetadataRemovalFields.Everything) <> 0
            If everything OrElse (what And MetadataRemovalFields.Rating) <> 0 Then names.Add(XmpNs + "Rating")
            If everything OrElse (what And MetadataRemovalFields.ColorLabel) <> 0 Then names.Add(XmpNs + "Label")
            If everything OrElse (what And MetadataRemovalFields.PickState) <> 0 Then names.Add(XmpDmNs + "good")
            If everything OrElse (what And MetadataRemovalFields.Keywords) <> 0 Then
                names.Add(DcNs + "subject")
                names.Add(LrNs + "hierarchicalSubject")
            End If
            If everything OrElse (what And MetadataRemovalFields.Description) <> 0 Then
                names.AddRange({DcNs + "title", DcNs + "description", DcNs + "creator", PhotoshopNs + "Headline", PhotoshopNs + "Credit"})
            End If
            If everything OrElse (what And MetadataRemovalFields.Copyright) <> 0 Then names.Add(DcNs + "rights")
            If everything OrElse (what And MetadataRemovalFields.Place) <> 0 Then
                names.AddRange({PhotoshopNs + "City", PhotoshopNs + "State", PhotoshopNs + "Country"})
            End If
            If everything Then
                names.AddRange({XmpNs + "CreateDate", XmpNs + "ModifyDate", PhotoshopNs + "DateCreated",
                                ExifNs + "DateTimeOriginal", ExifNs + "DateTimeDigitized"})
            End If
            Return names
        End Function

        ''' <summary>Die IPTC-Datensaetze (Record 2) je Angabe, fuer den JPEG-Weg.</summary>
        Private Shared Function IptcDataSetsFor(what As MetadataRemovalFields) As Dictionary(Of Integer, List(Of String))
            Dim result As New Dictionary(Of Integer, List(Of String))()
            Dim add = Sub(numbers As Integer())
                          For Each number In numbers
                              result(number) = New List(Of String)()
                          Next
                      End Sub
            If (what And MetadataRemovalFields.Keywords) <> 0 Then add({25})
            If (what And MetadataRemovalFields.Description) <> 0 Then add({5, 80, 105, 110, 120})
            If (what And MetadataRemovalFields.Copyright) <> 0 Then add({116})
            ' Stadt, Ortsteil, Bundesland, Laenderkuerzel, Land.
            If (what And MetadataRemovalFields.Place) <> 0 Then add({90, 92, 95, 100, 101})
            Return result
        End Function

        ''' <summary>Entfernt die XMP-Felder aus den Description-Knoten; bei Ort oder "alles" auch
        ''' die Koordinaten (exif:GPS...).</summary>
        Private Shared Function RemoveFromXmp(descriptions As IEnumerable(Of XElement), what As MetadataRemovalFields) As Boolean
            Dim all = descriptions.ToList()
            Dim changed = False
            For Each name In XmpNamesFor(what)
                changed = RemoveEverywhere(all, name) Or changed
            Next
            If (what And (MetadataRemovalFields.Place Or MetadataRemovalFields.Everything)) <> 0 Then
                For Each description In all
                    For Each attribute In description.Attributes().Where(Function(a) a.Name.Namespace = ExifNs AndAlso a.Name.LocalName.StartsWith("GPS", StringComparison.Ordinal)).ToList()
                        attribute.Remove()
                        changed = True
                    Next
                    For Each element In description.Elements().Where(Function(e) e.Name.Namespace = ExifNs AndAlso e.Name.LocalName.StartsWith("GPS", StringComparison.Ordinal)).ToList()
                        element.Remove()
                        changed = True
                    Next
                Next
            End If
            Return changed
        End Function

        ''' <summary>Entfernt die genannten Angaben aus der Beistelldatei. Keine Beistelldatei heisst:
        ''' nichts zu tun, und das ist Erfolg.</summary>
        Public Shared Function RemoveFromSidecar(imagePath As String, what As MetadataRemovalFields) As Boolean
            If what = MetadataRemovalFields.None OrElse String.IsNullOrEmpty(XmpSidecarService.FindSidecar(imagePath)) Then Return True
            Return Not String.IsNullOrEmpty(ExifService.EditXmpSidecar(imagePath, createIfMissing:=False,
                edit:=Function(descriptions, target) RemoveFromXmp(descriptions, what)))
        End Function

        ''' <summary>Entfernt die genannten Angaben GEZIELT aus der Bilddatei (JPEG, PNG, WebP): aus
        ''' dem XMP, im JPEG auch aus IPTC, beim Copyright aus dem EXIF-Feld und beim Ort die
        ''' Koordinate aus dem EXIF. Die Beistelldatei bleibt unberuehrt. Fuer "alles" gibt es den
        ''' Weg ueber das Neuschreiben ohne Metadaten, nicht diesen.</summary>
        ''' <returns>True, wenn alles geklappt hat (auch, wenn nichts zu entfernen war).</returns>
        Public Shared Function RemoveFromFile(imagePath As String, what As MetadataRemovalFields) As Boolean
            If what = MetadataRemovalFields.None OrElse Not ImageProcessor.CanWriteKeywordsInto(imagePath) Then Return True
            Try
                If ImageProcessor.EditXmpInFile(imagePath, createPacket:=False,
                                                edit:=Function(descriptions, target) RemoveFromXmp(descriptions, what),
                                                iptcReplacements:=IptcDataSetsFor(what)) Then
                    ExifService.Invalidate(imagePath)
                End If
                If (what And MetadataRemovalFields.Copyright) <> 0 Then CopyrightService.RemoveCopyrightFromJpeg(imagePath)
                If (what And MetadataRemovalFields.Place) <> 0 Then GeotagService.RemoveCoordinatesFromJpeg(imagePath)
                ExifService.Invalidate(imagePath)

                ' Die beiden EXIF-Wege melden False sowohl fuer "stand nichts drin" als auch fuer
                ' "EXIF-Block nicht lesbar"; am Rueckgabewert laesst sich der Erfolg nicht ablesen.
                ' Deshalb wird die Datei selbst gefragt: steht noch ein Hinweis oder eine Koordinate
                ' im eingebetteten EXIF, ist das Bild NICHT bereinigt, und Beistelldatei und Katalog
                ' bleiben (beim Aufrufer) unangetastet.
                If (what And MetadataRemovalFields.Copyright) <> 0 AndAlso EmbeddedExifHasCopyright(imagePath) Then
                    DiagnosticLogService.LogAlways("CatalogMetadata.RemoveFromFile", "Copyright steht noch im EXIF: " & imagePath)
                    Return False
                End If
                If (what And MetadataRemovalFields.Place) <> 0 AndAlso EmbeddedExifHasCoordinates(imagePath) Then
                    DiagnosticLogService.LogAlways("CatalogMetadata.RemoveFromFile", "Koordinate steht noch im EXIF: " & imagePath)
                    Return False
                End If
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogException("CatalogMetadata.RemoveFromFile", ex)
                Return False
            End Try
        End Function

        ''' <summary>Steht im EINGEBETTETEN EXIF (IFD0) noch ein Urheberrechtshinweis? Bewusst nicht
        ''' ueber ExifService.ReadExif: das legt die Beistelldatei darueber, und gefragt ist hier
        ''' allein die Datei. Nur JPEG; die anderen Formate haben keinen EXIF-Weg zum Entfernen.</summary>
        Private Shared Function EmbeddedExifHasCopyright(imagePath As String) As Boolean
            If Not GeotagService.IsJpegPath(imagePath) Then Return False
            Dim directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(imagePath)
            Return directories.OfType(Of MetadataExtractor.Formats.Exif.ExifIfd0Directory)().
                Any(Function(d) Not String.IsNullOrWhiteSpace(d.GetDescription(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagCopyright)))
        End Function

        ''' <summary>Steht im eingebetteten EXIF noch eine lesbare Koordinate? Nur JPEG.</summary>
        Private Shared Function EmbeddedExifHasCoordinates(imagePath As String) As Boolean
            If Not GeotagService.IsJpegPath(imagePath) Then Return False
            Dim directories = MetadataExtractor.ImageMetadataReader.ReadMetadata(imagePath)
            Return directories.OfType(Of MetadataExtractor.Formats.Exif.GpsDirectory)().
                Any(Function(d) d.GetGeoLocation() IsNot Nothing)
        End Function

        ''' <summary>Leert die genannten EIGENEN Angaben des Katalogs (Bewertung, Etikett,
        ''' Markierung, Stichwoerter). Beschreibung, Copyright und Ort sind im Katalog eine Abschrift
        ''' der Datei und folgen ihr beim naechsten Einlesen; Personen, Favorit und KI-Stichwoerter
        ''' bleiben in jedem Fall.</summary>
        Public Shared Sub RemoveFromCatalog(imagePaths As IEnumerable(Of String), what As MetadataRemovalFields)
            Dim list = If(imagePaths, Enumerable.Empty(Of String)()).Where(Function(p) Not String.IsNullOrWhiteSpace(p)).ToList()
            If list.Count = 0 Then Return
            Dim everything = (what And MetadataRemovalFields.Everything) <> 0
            Dim library = LibraryService.Instance
            If everything OrElse (what And MetadataRemovalFields.Rating) <> 0 Then library.SetRatingForMany(list, 0)
            If everything OrElse (what And MetadataRemovalFields.ColorLabel) <> 0 Then library.SetColorLabelForMany(list, "")
            If everything OrElse (what And MetadataRemovalFields.PickState) <> 0 Then library.SetPickStateForMany(list, 0)
            If everything OrElse (what And MetadataRemovalFields.Keywords) <> 0 Then
                For Each path In list
                    library.SetTags(path, New List(Of String)())
                Next
            End If
        End Sub

        ''' <summary>Alle bekannten Felder aus der Beistelldatei.</summary>
        Public Shared Function ClearSidecarFields(imagePath As String) As Boolean
            Return RemoveFromSidecar(imagePath, MetadataRemovalFields.Everything)
        End Function

        ''' <summary>Bewertung, Etikett, Markierung und Stichwoerter im Katalog leeren.</summary>
        Public Shared Sub ClearCatalog(imagePaths As IEnumerable(Of String))
            RemoveFromCatalog(imagePaths, MetadataRemovalFields.Everything)
        End Sub

        ' ---------------------------------------------------------------- Hilfen

        Private Shared Function RemoveEverywhere(descriptions As IEnumerable(Of XElement), name As XName) As Boolean
            Dim removed = False
            For Each description In descriptions
                If description.Attribute(name) IsNot Nothing Then
                    description.SetAttributeValue(name, Nothing)
                    removed = True
                End If
                For Each stale In description.Elements(name).ToList()
                    stale.Remove()
                    removed = True
                Next
            Next
            Return removed
        End Function

        Private Shared Sub EnsurePrefix(target As XElement, prefix As String, ns As XNamespace)
            If target.GetPrefixOfNamespace(ns) IsNot Nothing Then Return
            target.SetAttributeValue(XNamespace.Xmlns + prefix, ns.NamespaceName)
        End Sub

    End Class

End Namespace
