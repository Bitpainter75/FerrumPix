Imports System
Imports System.Collections.Generic
Imports System.Collections
Imports System.Globalization
Imports System.Linq
Imports System.Reflection
Imports System.Text.RegularExpressions
Imports System.Text
Imports System.Xml.Linq
Imports MetadataExtractor
Imports MetadataExtractor.Formats.Exif

Namespace Services

    ' Typisierte, für die Datenbank/Suche geeignete Teilmenge der EXIF-Daten - abgeleitet aus den
    ' bereits von ReadExif() erzeugten formatierten Anzeige-Strings (kein zweites Einlesen der Datei).
    Public Class ExifSearchFields
        Public Property DateTaken As String = ""       ' EXIF-Rohformat "YYYY:MM:DD HH:MM:SS" - sortiert korrekt als Text
        Public Property DateModifiedExif As String = ""
        Public Property Camera As String = ""
        Public Property Lens As String = ""
        Public Property Aperture As Double?
        Public Property FocalLengthMm As Double?
        Public Property Iso As Integer?
        Public Property ShutterSpeed As String = ""
        Public Property GpsLatitude As Double?
        Public Property GpsLongitude As Double?
        Public Property ImageWidth As Integer?
        Public Property ImageHeight As Integer?
    End Class

    Public Class ExifTag
        Public Property Name As String
        Public Property Value As String
        Public Sub New(name As String, value As String)
            Me.Name = name
            Me.Value = value
        End Sub
    End Class

    ''' Katalog-Flags/Zusammenfassungen, die LibraryService.SyncExifData zusätzlich zu den
    ''' durchsuchbaren Feldern (ExifSearchFields) braucht - gebündelt, damit nicht jeder Aufrufer
    ''' (Viewer/Editor/Gallery) dieselbe Auswertung von ExifTags/IptcTags/XmpTags dupliziert.
    Public Class ExifCatalogSummary
        Public Property HasExifMetadata As Boolean
        Public Property HasIptcMetadata As Boolean
        Public Property HasXmpMetadata As Boolean
        Public Property ExifSummary As String = ""
        Public Property IptcSummary As String = ""
        Public Property XmpSummary As String = ""
    End Class

    Public Class ExifData
        Public Property FileName As String = ""
        Public Property FileType As String = ""
        Public Property FileSize As String = ""
        Public Property DateTaken As String = ""
        Public Property DateModifiedExif As String = ""
        Public Property Camera As String = ""
        Public Property Lens As String = ""
        Public Property FocalLength As String = ""
        Public Property Aperture As String = ""
        Public Property ShutterSpeed As String = ""
        Public Property ISO As String = ""
        Public Property ImageWidth As String = ""
        Public Property ImageHeight As String = ""
        Public Property Megapixels As String = ""
        Public Property AspectRatio As String = ""
        Public Property ColorSpace As String = ""
        Public Property GPS As String = ""
        Public Property Copyright As String = ""
        Public Property Software As String = ""
        Public Property XmpRating As Integer?
        Public Property ExifTags As New List(Of ExifTag)()
        Public Property IptcTags As New List(Of ExifTag)()
        Public Property XmpTags As New List(Of ExifTag)()
    End Class

    Public Class ExifService

        Private Class ExifCacheEntry
            Public Property LastWriteUtc As DateTime
            Public Property Size As Long
            Public Property Data As ExifData
        End Class

        Private Shared ReadOnly _cacheLock As New Object()
        Private Shared ReadOnly _cache As New Dictionary(Of String, ExifCacheEntry)(StringComparer.OrdinalIgnoreCase)

        ''' <summary>
        ''' EXIF ändert sich nach der Aufnahme normalerweise nicht mehr nachträglich - ein
        ''' In-Memory-Cache (pro Datei über LastWriteTimeUtc+Größe validiert, analog zum
        ''' Frische-Muster in ThumbnailCacheService) erspart das erneute Einlesen+Parsen, wenn
        ''' Viewer/Editor beim Navigieren wiederholt auf dasselbe Bild treffen. Liefert stets eine
        ''' frische Kopie zurück, da BuildImageInfo (ViewerViewModel/EditorViewModel) einzelne
        ''' Felder des Ergebnisses direkt nachträglich setzt (Megapixel, Seitenverhältnis etc.) -
        ''' ein geteiltes Cache-Objekt dürfte dadurch nicht verändert werden.
        ''' </summary>
        Public Shared Function ReadExif(imagePath As String) As ExifData
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not System.IO.File.Exists(imagePath) Then Return New ExifData()

            Dim fileInfo = New System.IO.FileInfo(imagePath)
            Dim lastWriteUtc = fileInfo.LastWriteTimeUtc
            Dim size = fileInfo.Length

            SyncLock _cacheLock
                Dim entry As ExifCacheEntry = Nothing
                If _cache.TryGetValue(imagePath, entry) AndAlso entry.LastWriteUtc = lastWriteUtc AndAlso entry.Size = size Then
                    Return CloneExifData(entry.Data)
                End If
            End SyncLock

            Dim freshData = ReadExifCore(imagePath, fileInfo)

            SyncLock _cacheLock
                _cache(imagePath) = New ExifCacheEntry With {.LastWriteUtc = lastWriteUtc, .Size = size, .Data = freshData}
            End SyncLock

            Return CloneExifData(freshData)
        End Function

        Public Shared Sub Invalidate(imagePath As String)
            If String.IsNullOrWhiteSpace(imagePath) Then Return
            SyncLock _cacheLock
                _cache.Remove(imagePath)
            End SyncLock
        End Sub

        Private Shared Function CloneExifData(source As ExifData) As ExifData
            Return New ExifData With {
                .FileName = source.FileName,
                .FileType = source.FileType,
                .FileSize = source.FileSize,
                .DateTaken = source.DateTaken,
                .DateModifiedExif = source.DateModifiedExif,
                .Camera = source.Camera,
                .Lens = source.Lens,
                .FocalLength = source.FocalLength,
                .Aperture = source.Aperture,
                .ShutterSpeed = source.ShutterSpeed,
                .ISO = source.ISO,
                .ImageWidth = source.ImageWidth,
                .ImageHeight = source.ImageHeight,
                .Megapixels = source.Megapixels,
                .AspectRatio = source.AspectRatio,
                .ColorSpace = source.ColorSpace,
                .GPS = source.GPS,
                .Copyright = source.Copyright,
                .Software = source.Software,
                .XmpRating = source.XmpRating,
                .ExifTags = source.ExifTags,
                .IptcTags = source.IptcTags,
                .XmpTags = source.XmpTags
            }
        End Function

        Private Shared Function ReadExifCore(imagePath As String, info As System.IO.FileInfo) As ExifData
            Dim data = New ExifData()

            data.FileName = System.IO.Path.GetFileName(imagePath)
            data.FileType = System.IO.Path.GetExtension(imagePath).TrimStart("."c).ToUpperInvariant()
            Dim kb = info.Length / 1024.0
            data.FileSize = If(kb < 1024, $"{kb:F0} KB", $"{kb / 1024:F1} MB")

            Try
                Dim metaDirectories = ImageMetadataReader.ReadMetadata(imagePath)

                For Each metaDir As MetadataExtractor.Directory In metaDirectories
                    Dim dirType = metaDir.GetType().FullName

                    If String.Equals(dirType, "MetadataExtractor.Formats.Iptc.IptcDirectory", StringComparison.Ordinal) Then
                        For Each metaTag In metaDir.Tags
                            If Not String.IsNullOrWhiteSpace(metaTag.Description) Then
                                data.IptcTags.Add(New ExifTag(metaTag.Name, metaTag.Description))
                            End If
                        Next
                    ElseIf String.Equals(dirType, "MetadataExtractor.Formats.Xmp.XmpDirectory", StringComparison.Ordinal) Then
                        CollectXmpTags(metaDir, data)
                    Else
                        For Each metaTag In metaDir.Tags
                            If Not String.IsNullOrWhiteSpace(metaTag.Description) Then
                                data.ExifTags.Add(New ExifTag(metaDir.Name & " › " & metaTag.Name, metaTag.Description))
                            End If
                        Next
                    End If
                Next

                Dim exifSub = metaDirectories.OfType(Of ExifSubIfdDirectory)().FirstOrDefault()
                Dim exifIfd = metaDirectories.OfType(Of ExifIfd0Directory)().FirstOrDefault()
                Dim gpsDir = metaDirectories.OfType(Of GpsDirectory)().FirstOrDefault()

                If exifSub IsNot Nothing Then
                    data.DateTaken = GetTagDesc(exifSub, ExifSubIfdDirectory.TagDateTimeOriginal)
                    data.FocalLength = GetTagDesc(exifSub, ExifSubIfdDirectory.TagFocalLength)
                    data.Aperture = GetTagDesc(exifSub, ExifSubIfdDirectory.TagFNumber)
                    data.ShutterSpeed = GetTagDesc(exifSub, ExifSubIfdDirectory.TagExposureTime)
                    data.ISO = GetTagDesc(exifSub, ExifSubIfdDirectory.TagIsoEquivalent)
                    data.ImageWidth = GetTagDesc(exifSub, ExifSubIfdDirectory.TagExifImageWidth)
                    data.ImageHeight = GetTagDesc(exifSub, ExifSubIfdDirectory.TagExifImageHeight)
                    data.ColorSpace = GetTagDesc(exifSub, ExifSubIfdDirectory.TagColorSpace)
                    data.Lens = GetTagDesc(exifSub, ExifSubIfdDirectory.TagLensModel)
                End If

                If exifIfd IsNot Nothing Then
                    Dim make = GetTagDesc(exifIfd, ExifIfd0Directory.TagMake)
                    Dim model = GetTagDesc(exifIfd, ExifIfd0Directory.TagModel)
                    data.Camera = (make & " " & model).Trim()
                    data.Software = GetTagDesc(exifIfd, ExifIfd0Directory.TagSoftware)
                    data.Copyright = GetTagDesc(exifIfd, ExifIfd0Directory.TagCopyright)
                    data.DateModifiedExif = GetTagDesc(exifIfd, ExifIfd0Directory.TagDateTime)
                    If String.IsNullOrEmpty(data.DateTaken) Then
                        data.DateTaken = data.DateModifiedExif
                    End If
                End If

                If gpsDir IsNot Nothing Then
                    Dim geoLoc = gpsDir.GetGeoLocation()
                    If geoLoc IsNot Nothing Then
                        data.GPS = $"{geoLoc.Latitude:F5}°, {geoLoc.Longitude:F5}°"
                    End If
                End If

            Catch
                ' Kein EXIF oder Lesefehler
            End Try

            Return data
        End Function

        ' Leitet durchsuchbare typisierte Felder aus den bereits geparsten Anzeige-Strings ab.
        ' Jedes Feld fällt bei einem Parse-Fehler einzeln auf Nothing/"" zurück statt die ganze
        ' Funktion abzubrechen - die formatierten Strings variieren je nach Kamera/Locale.
        ''' <summary>
        ''' imagePath ist optional, aber empfohlen: Breite/Höhe kommen bevorzugt direkt aus den
        ''' echten Pixel-Maßen der Datei (SKCodec, liest nur den Header, kein Volldecode) statt aus
        ''' dem optionalen, oft fehlenden EXIF-Bildgrößen-Tag - so bleiben sie auch nach externem
        ''' Zuschnitt/Resize (ohne EXIF-Update) korrekt.
        ''' </summary>
        Public Shared Function ExtractSearchFields(data As ExifData, Optional imagePath As String = Nothing) As ExifSearchFields
            Dim result As New ExifSearchFields()
            If data Is Nothing Then Return result

            result.DateTaken = data.DateTaken
            result.DateModifiedExif = data.DateModifiedExif
            result.Camera = data.Camera
            result.Lens = data.Lens
            result.ShutterSpeed = data.ShutterSpeed
            result.Aperture = ParseLeadingDouble(data.Aperture)
            result.FocalLengthMm = ParseLeadingDouble(data.FocalLength)
            result.Iso = ParseLeadingInt(data.ISO)

            If Not String.IsNullOrWhiteSpace(data.GPS) Then
                Dim m = Regex.Match(data.GPS, "(-?\d+(?:[.,]\d+)?)°?\s*,\s*(-?\d+(?:[.,]\d+)?)°?")
                If m.Success Then
                    result.GpsLatitude = ParseInvariantDouble(m.Groups(1).Value)
                    result.GpsLongitude = ParseInvariantDouble(m.Groups(2).Value)
                End If
            End If

            Dim dimensions = If(Not String.IsNullOrWhiteSpace(imagePath), ReadImageDimensions(imagePath), (Width:=CType(Nothing, Integer?), Height:=CType(Nothing, Integer?)))
            result.ImageWidth = If(dimensions.Width, ParseLeadingInt(data.ImageWidth))
            result.ImageHeight = If(dimensions.Height, ParseLeadingInt(data.ImageHeight))

            Return result
        End Function

        Public Shared Function BuildCatalogSummary(data As ExifData) As ExifCatalogSummary
            Return New ExifCatalogSummary With {
                .HasExifMetadata = data IsNot Nothing AndAlso data.ExifTags IsNot Nothing AndAlso data.ExifTags.Count > 0,
                .HasIptcMetadata = data IsNot Nothing AndAlso data.IptcTags IsNot Nothing AndAlso data.IptcTags.Count > 0,
                .HasXmpMetadata = data IsNot Nothing AndAlso data.XmpTags IsNot Nothing AndAlso data.XmpTags.Count > 0,
                .ExifSummary = BuildMetadataSummary(If(data?.ExifTags, Nothing), 6),
                .IptcSummary = BuildMetadataSummary(If(data?.IptcTags, Nothing), 6),
                .XmpSummary = BuildMetadataSummary(If(data?.XmpTags, Nothing), 6)
            }
        End Function

        Public Shared Function BuildMetadataSummary(tags As IEnumerable(Of ExifTag), maxItems As Integer) As String
            Dim entries = If(tags, Enumerable.Empty(Of ExifTag)()).
                Where(Function(t) t IsNot Nothing AndAlso
                                  Not String.IsNullOrWhiteSpace(t.Name) AndAlso
                                  Not String.IsNullOrWhiteSpace(t.Value)).
                Take(Math.Max(1, maxItems)).
                Select(Function(t)
                           Dim name = t.Name.Trim()
                           Dim value = t.Value.Trim()
                           If value.Length > 48 Then value = value.Substring(0, 48) & "..."
                           Return $"{name}: {value}"
                       End Function).
                ToList()
            Return String.Join(Environment.NewLine, entries)
        End Function

        Public Shared Function GetXmpRating(data As ExifData) As Integer?
            If data Is Nothing Then Return Nothing
            Return data.XmpRating
        End Function

        ''' Liest nur den Bild-Header (SKCodec.Create öffnet die Datei, dekodiert aber keine
        ''' Pixel) - deutlich billiger als ein Volldecode, nur für die Maße gebraucht.
        Public Shared Function ReadImageDimensions(imagePath As String) As (Width As Integer?, Height As Integer?)
            Try
                If Not System.IO.File.Exists(imagePath) Then Return (Nothing, Nothing)
                Using codec = SkiaSharp.SKCodec.Create(imagePath)
                    If codec Is Nothing Then Return (Nothing, Nothing)
                    Return (codec.Info.Width, codec.Info.Height)
                End Using
            Catch
                Return (Nothing, Nothing)
            End Try
        End Function

        Private Shared Function ParseLeadingDouble(text As String) As Double?
            If String.IsNullOrWhiteSpace(text) Then Return Nothing
            Dim m = Regex.Match(text, "-?\d+(?:[.,]\d+)?")
            If Not m.Success Then Return Nothing
            Return ParseInvariantDouble(m.Value)
        End Function

        Private Shared Function ParseLeadingInt(text As String) As Integer?
            If String.IsNullOrWhiteSpace(text) Then Return Nothing
            Dim m = Regex.Match(text, "-?\d+")
            If Not m.Success Then Return Nothing
            Dim value As Integer
            If Integer.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, value) Then Return value
            Return Nothing
        End Function

        Private Shared Function ParseInvariantDouble(text As String) As Double?
            Dim normalized = text.Replace(","c, "."c)
            Dim value As Double
            If Double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, value) Then Return value
            Return Nothing
        End Function

        ''' <summary>Parst das EXIF-Rohdatumsformat ("yyyy:MM:dd HH:mm:ss") aus DateTaken/DateModifiedExif
        ''' in ein DateTime für Anzeige/Sortierung. Liefert Nothing, wenn das Feld leer/nicht parsbar ist.</summary>
        Public Shared Function ParseExifDateTime(raw As String) As DateTime?
            If String.IsNullOrWhiteSpace(raw) Then Return Nothing
            Dim result As DateTime
            If DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, result) Then Return result
            Return Nothing
        End Function

        Private Shared Function GetTagDesc(metaDir As MetadataExtractor.Directory, tagType As Integer) As String
            Dim desc = metaDir.GetDescription(tagType)
            Return If(desc, "")
        End Function

        Private Shared Sub CollectXmpTags(metaDir As MetadataExtractor.Directory, data As ExifData)
            Try
                Dim method = metaDir.GetType().GetMethod("GetXmpProperties", BindingFlags.Public Or BindingFlags.Instance)
                If method Is Nothing Then Return

                Dim result = method.Invoke(metaDir, Nothing)
                If result Is Nothing Then Return

                If TypeOf result Is IDictionary Then
                    For Each entry As DictionaryEntry In DirectCast(result, IDictionary)
                        AddXmpTag(data, entry.Key, entry.Value)
                    Next
                    Return
                End If

                If TypeOf result Is IEnumerable Then
                    For Each item In DirectCast(result, IEnumerable)
                        AddXmpTag(data, TryGetMemberValue(item, "Key"), TryGetMemberValue(item, "Value"))
                    Next
                End If
            Catch
            End Try
        End Sub

        Private Shared Sub AddXmpTag(data As ExifData, name As Object, value As Object)
            Dim tagName = If(name, "").ToString().Trim()
            Dim tagValue = If(value, "").ToString().Trim()
            If String.IsNullOrWhiteSpace(tagName) OrElse String.IsNullOrWhiteSpace(tagValue) Then Return
            data.XmpTags.Add(New ExifTag(tagName, tagValue))
            If Not data.XmpRating.HasValue Then
                Dim rating = TryParseXmpRating(tagName, tagValue)
                If rating.HasValue Then data.XmpRating = rating
            End If
        End Sub

        Private Shared Function TryParseXmpRating(tagName As String, tagValue As String) As Integer?
            Dim normalizedName = If(tagName, "").Trim()
            If normalizedName.IndexOf("rating", StringComparison.OrdinalIgnoreCase) < 0 Then Return Nothing

            Dim parsed As Integer
            If Integer.TryParse(If(tagValue, "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, parsed) Then
                If parsed < 0 Then Return 0
                Return Math.Max(0, Math.Min(5, parsed))
            End If

            Dim numericMatch = Regex.Match(If(tagValue, ""), "-?\d+")
            If numericMatch.Success AndAlso Integer.TryParse(numericMatch.Value, parsed) Then
                If parsed < 0 Then Return 0
                Return Math.Max(0, Math.Min(5, parsed))
            End If

            Return Nothing
        End Function

        Public Shared Function WriteXmpRatingSidecar(imagePath As String, rating As Integer, createIfMissing As Boolean) As Boolean
            If String.IsNullOrWhiteSpace(imagePath) Then Return False

            Dim sidecarPath = IO.Path.ChangeExtension(imagePath, ".xmp")
            If String.IsNullOrWhiteSpace(sidecarPath) Then Return False
            If Not System.IO.File.Exists(sidecarPath) AndAlso Not createIfMissing Then Return False

            Dim xNamespace As XNamespace = "adobe:ns:meta/"
            Dim rdfNamespace As XNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#"
            Dim xmpNamespace As XNamespace = "http://ns.adobe.com/xap/1.0/"
            Dim safeRating = Math.Max(0, Math.Min(5, rating)).ToString(CultureInfo.InvariantCulture)

            Try
                Dim doc As XDocument
                If System.IO.File.Exists(sidecarPath) Then
                    doc = XDocument.Parse(System.IO.File.ReadAllText(sidecarPath, Encoding.UTF8), LoadOptions.PreserveWhitespace)
                Else
                    doc = New XDocument(
                        New XDeclaration("1.0", "utf-8", "yes"),
                        New XElement(xNamespace + "xmpmeta",
                            New XAttribute(XNamespace.Xmlns + "x", xNamespace.NamespaceName),
                            New XElement(rdfNamespace + "RDF",
                                New XAttribute(XNamespace.Xmlns + "rdf", rdfNamespace.NamespaceName),
                                New XElement(rdfNamespace + "Description",
                                    New XAttribute(XNamespace.Xmlns + "xmp", xmpNamespace.NamespaceName)))))
                End If

                Dim description = doc.Descendants(rdfNamespace + "Description").FirstOrDefault()
                If description Is Nothing Then
                    Dim rdfRoot = doc.Descendants(rdfNamespace + "RDF").FirstOrDefault()
                    If rdfRoot Is Nothing Then
                        Dim root = doc.Root
                        If root Is Nothing Then Return False
                        rdfRoot = New XElement(rdfNamespace + "RDF", New XAttribute(XNamespace.Xmlns + "rdf", rdfNamespace.NamespaceName))
                        root.Add(rdfRoot)
                    End If
                    description = New XElement(rdfNamespace + "Description")
                    rdfRoot.Add(description)
                End If

                description.SetAttributeValue(XNamespace.Xmlns + "xmp", xmpNamespace.NamespaceName)
                description.SetAttributeValue(xmpNamespace + "Rating", safeRating)

                System.IO.File.WriteAllText(sidecarPath, doc.ToString(SaveOptions.DisableFormatting), New UTF8Encoding(False))
                Return True
            Catch
                Return False
            End Try
        End Function

        Private Shared Function TryGetMemberValue(item As Object, memberName As String) As Object
            If item Is Nothing Then Return Nothing
            Dim prop = item.GetType().GetProperty(memberName, BindingFlags.Public Or BindingFlags.Instance)
            If prop IsNot Nothing Then Return prop.GetValue(item)
            Return Nothing
        End Function
    End Class

End Namespace
