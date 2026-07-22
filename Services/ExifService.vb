Imports System
Imports System.Collections.Generic
Imports System.Collections
Imports System.Globalization
Imports System.Linq
Imports System.Reflection
Imports System.IO
Imports System.IO.Compression
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
        Public Property HasIccProfile As Boolean
        Public Property ExifSummary As String = ""
        Public Property IptcSummary As String = ""
        Public Property XmpSummary As String = ""
        Public Property IccSummary As String = ""
        ''' <summary>Format+Sprache, in denen die Summary-Texte erzeugt wurden (siehe
        ''' ExifService.CurrentSummaryFormat) - der Katalog erkennt daran veraltete Einträge.</summary>
        Public Property SummaryFormat As String = ""
    End Class

    Public Class ExifData
        Public Property FileName As String = ""
        Public Property FileType As String = ""
        Public Property FileSize As String = ""
        ''' <summary>Erstell-/Änderungsdatum der DATEI (nicht aus EXIF), im selben Rohformat wie
        ''' DateTaken: "yyyy:MM:dd HH:mm:ss". Roh gehalten, weil ExifData zwischengespeichert wird -
        ''' eine hier schon fertig formatierte Zeichenkette bliebe nach einem Sprachwechsel stehen.</summary>
        Public Property FileCreated As String = ""
        Public Property FileModified As String = ""
        Public Property DateTaken As String = ""
        Public Property DateModifiedExif As String = ""
        Public Property Camera As String = ""
        Public Property Lens As String = ""
        Public Property FocalLength As String = ""
        ''' Kleinbild-Äquivalent (EXIF-Tag "FocalLengthIn35mmFilm"). Bei Handykameras ist die echte
        ''' Brennweite (4,2 mm) für sich genommen nichtssagend - erst der Äquivalentwert (28 mm) ist
        ''' mit dem eines Objektivs vergleichbar.
        Public Property FocalLength35mm As String = ""
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
        ''' <summary>Die Tags des eingebetteten ICC-Profils, zusätzlich zu ihrem Auftritt im
        ''' EXIF-Tag-Baum - für die Zusammenfassung am ICC-Badge (Profilname, Farbraum, Rendering
        ''' Intent), die sonst zwischen den EXIF-Einträgen untergehen würde.</summary>
        Public Property IccTags As New List(Of ExifTag)()
        ''' <summary>True, wenn die Datei ein eingebettetes ICC-Farbprofil trägt (MetadataExtractor
        ''' IccDirectory). Steuert das ICC-Badge in der Galerie - analog zu den EXIF/IPTC/XMP-Badges.</summary>
        Public Property HasIccProfile As Boolean
    End Class

    Public Class ExifService

        Private Class ExifCacheEntry
            Public Property LastWriteUtc As DateTime
            Public Property Size As Long
            Public Property Data As ExifData
        End Class

        Private Shared ReadOnly _cacheLock As New Object()
        ' Pfad-Schluessel: PathIdentity.Comparer statt OrdinalIgnoreCase. Auf Linux sind
        ' /Bilder/RAW.jpg und /Bilder/raw.jpg zwei Dateien - vorher teilten sie sich den
        ' EXIF-Eintrag, die zweite Datei bekam also die Aufnahmedaten der ersten angezeigt.
        Private Shared ReadOnly _cache As New Dictionary(Of String, ExifCacheEntry)(PathIdentity.Comparer)

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

        ''' <summary>Trägt Größe und Erstell-/Änderungsdatum der Datei nach - für Aufrufer, die ihre
        ''' ExifData nicht aus der angezeigten Datei selbst gelesen haben (Katalog-Provisorium im
        ''' Viewer, .fpx/RAW im Editor, wo der Renderpfad in den Temp-Ordner zeigt).</summary>
        Public Shared Sub FillFileFacts(data As ExifData, imagePath As String)
            If data Is Nothing OrElse String.IsNullOrWhiteSpace(imagePath) Then Return
            If ImmichService.IsImmichPseudoPath(imagePath) Then Return
            Try
                Dim info = New System.IO.FileInfo(imagePath)
                If Not info.Exists Then Return
                data.FileSize = FormatFileSize(info.Length)
                data.FileCreated = FormatFileDate(info.CreationTime)
                data.FileModified = FormatFileDate(info.LastWriteTime)
            Catch
                ' Zugriffsfehler: Felder bleiben leer statt das Infopanel scheitern zu lassen.
            End Try
        End Sub

        Private Shared Function FormatFileSize(bytes As Long) As String
            Dim kb = bytes / 1024.0
            Return If(kb < 1024, $"{kb:F0} KB", $"{kb / 1024:F1} MB")
        End Function

        ''' <summary>Auf Linux liefern Dateisysteme ohne Geburtszeitstempel für CreationTime die
        ''' Unix-Epoche - so ein Scheindatum wäre schlimmer als gar keine Zeile, deshalb fällt alles
        ''' vor 1980 raus (die Zeile blendet sich dann aus).</summary>
        Private Shared Function FormatFileDate(value As DateTime) As String
            If value.Year < 1980 Then Return ""
            Return value.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)
        End Function

        Private Shared Function CloneExifData(source As ExifData) As ExifData
            Return New ExifData With {
                .FileName = source.FileName,
                .FileType = source.FileType,
                .FileSize = source.FileSize,
                .FileCreated = source.FileCreated,
                .FileModified = source.FileModified,
                .DateTaken = source.DateTaken,
                .DateModifiedExif = source.DateModifiedExif,
                .Camera = source.Camera,
                .Lens = source.Lens,
                .FocalLength = source.FocalLength,
                .FocalLength35mm = source.FocalLength35mm,
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
                .XmpTags = source.XmpTags,
                .IccTags = source.IccTags,
                .HasIccProfile = source.HasIccProfile
            }
        End Function

        Private Shared Function ReadExifCore(imagePath As String, info As System.IO.FileInfo) As ExifData
            Dim data = New ExifData()

            data.FileName = System.IO.Path.GetFileName(imagePath)
            data.FileType = System.IO.Path.GetExtension(imagePath).TrimStart("."c).ToUpperInvariant()
            data.FileSize = FormatFileSize(info.Length)
            data.FileCreated = FormatFileDate(info.CreationTime)
            data.FileModified = FormatFileDate(info.LastWriteTime)

            Try
                ' FPX ist ein Hybrid: allgemeine/technische Bilddaten beschreiben das gespeicherte
                ' composite.png; saemtliche Metadaten-Kategorien EXIF/IPTC/XMP/ICC werden aus base.*
                ' (Ursprungsbild) uebernommen. Bei normalen Bildern sind beide Quellen dieselbe Datei.
                Dim isFpx = FpxService.IsFpx(imagePath)
                Dim metaDirectories = If(isFpx,
                                         ReadFpxCompositeMetadata(imagePath),
                                         ImageMetadataReader.ReadMetadata(imagePath))
                Dim captureDirectories = If(isFpx, ReadFpxBaseMetadata(imagePath), metaDirectories)

                If isFpx Then
                    CollectMetadataTags(captureDirectories, data, includeExif:=True, includeIptc:=True,
                                        includeXmp:=True, includeIcc:=True)
                Else
                    CollectMetadataTags(metaDirectories, data, includeExif:=True, includeIptc:=True,
                                        includeXmp:=True, includeIcc:=True)
                End If

                data.DateTaken = GetTagDescAcross(Of ExifSubIfdDirectory)(captureDirectories, ExifSubIfdDirectory.TagDateTimeOriginal)
                data.FocalLength = GetTagDescAcross(Of ExifSubIfdDirectory)(captureDirectories, ExifSubIfdDirectory.TagFocalLength)
                data.FocalLength35mm = GetTagDescAcross(Of ExifSubIfdDirectory)(captureDirectories, ExifSubIfdDirectory.Tag35MMFilmEquivFocalLength)
                data.Aperture = GetTagDescAcross(Of ExifSubIfdDirectory)(captureDirectories, ExifSubIfdDirectory.TagFNumber)
                data.ShutterSpeed = GetTagDescAcross(Of ExifSubIfdDirectory)(captureDirectories, ExifSubIfdDirectory.TagExposureTime)
                data.ISO = GetTagDescAcross(Of ExifSubIfdDirectory)(captureDirectories, ExifSubIfdDirectory.TagIsoEquivalent)
                ' Abmessungen und Farbraum gehoeren zum Composite, nicht zum Ursprungsbild.
                data.ImageWidth = GetTagDescAcross(Of ExifSubIfdDirectory)(metaDirectories, ExifSubIfdDirectory.TagExifImageWidth)
                data.ImageHeight = GetTagDescAcross(Of ExifSubIfdDirectory)(metaDirectories, ExifSubIfdDirectory.TagExifImageHeight)
                ' PNG/WebP und manche RAW-Container haben keine EXIF-Pixeldimensionen, melden ihre
                ' Groesse aber in einem formatspezifischen Verzeichnis. Das groesste passende Paar
                ' ist das Hauptbild (nicht ein eingebettetes Thumbnail) und gehoert ebenfalls in
                ' den Katalog.
                If String.IsNullOrWhiteSpace(data.ImageWidth) Then
                    Dim width = GetLargestImageDimension(metaDirectories, "Width")
                    If width.HasValue Then data.ImageWidth = width.Value.ToString(CultureInfo.InvariantCulture)
                End If
                If String.IsNullOrWhiteSpace(data.ImageHeight) Then
                    Dim height = GetLargestImageDimension(metaDirectories, "Height")
                    If height.HasValue Then data.ImageHeight = height.Value.ToString(CultureInfo.InvariantCulture)
                End If
                data.ColorSpace = GetTagDescAcross(Of ExifSubIfdDirectory)(metaDirectories, ExifSubIfdDirectory.TagColorSpace)
                data.Lens = GetTagDescAcross(Of ExifSubIfdDirectory)(captureDirectories, ExifSubIfdDirectory.TagLensModel)

                Dim make = GetTagDescAcross(Of ExifIfd0Directory)(captureDirectories, ExifIfd0Directory.TagMake)
                Dim model = GetTagDescAcross(Of ExifIfd0Directory)(captureDirectories, ExifIfd0Directory.TagModel)
                data.Camera = (make & " " & model).Trim()
                data.Software = GetTagDescAcross(Of ExifIfd0Directory)(captureDirectories, ExifIfd0Directory.TagSoftware)
                data.Copyright = GetTagDescAcross(Of ExifIfd0Directory)(captureDirectories, ExifIfd0Directory.TagCopyright)
                data.DateModifiedExif = GetTagDescAcross(Of ExifIfd0Directory)(captureDirectories, ExifIfd0Directory.TagDateTime)
                If String.IsNullOrEmpty(data.DateTaken) Then
                    data.DateTaken = data.DateModifiedExif
                End If

                For Each gpsDir In captureDirectories.OfType(Of GpsDirectory)()
                    Dim geoLoc = gpsDir.GetGeoLocation()
                    If geoLoc IsNot Nothing Then
                        data.GPS = $"{geoLoc.Latitude:F5}°, {geoLoc.Longitude:F5}°"
                        Exit For
                    End If
                Next

            Catch
                ' Kein EXIF oder Lesefehler
            End Try

            Return data
        End Function

        ''' <summary>Liest die Metadaten direkt aus base.* im FPX-Buendel. Der Eintrag wird nicht in
        ''' einen weiteren Temp-Ordner entpackt; dadurch funktioniert derselbe Weg auch beim
        ''' Hintergrundscan der Galerie und bei bereits vorhandenen FPX-Dateien.</summary>
        Private Shared Function ReadFpxBaseMetadata(fpxPath As String) As IReadOnlyList(Of MetadataExtractor.Directory)
            Return ReadFpxEntryMetadata(fpxPath,
                Function(entry) entry.FullName.IndexOf("/"c) < 0 AndAlso
                                entry.Name.StartsWith("base.", StringComparison.OrdinalIgnoreCase))
        End Function

        Private Shared Function ReadFpxCompositeMetadata(fpxPath As String) As IReadOnlyList(Of MetadataExtractor.Directory)
            Return ReadFpxEntryMetadata(fpxPath,
                Function(entry) String.Equals(entry.FullName, "composite.png", StringComparison.OrdinalIgnoreCase))
        End Function

        Private Shared Function ReadFpxEntryMetadata(
                fpxPath As String,
                predicate As Func(Of ZipArchiveEntry, Boolean)) As IReadOnlyList(Of MetadataExtractor.Directory)
            Try
                Using zip = ZipFile.OpenRead(fpxPath)
                    Dim imageEntry = zip.Entries.FirstOrDefault(
                        Function(entry) Not String.IsNullOrEmpty(entry.Name) AndAlso predicate(entry))
                    If imageEntry Is Nothing Then Return Array.Empty(Of MetadataExtractor.Directory)()

                    ' ZipArchiveEntry.Open() liefert auch fuer unkomprimierte Eintraege einen
                    ' NICHT seekbaren Stream. MetadataExtractor startet mit FileTypeDetector,
                    ' der nach dem Lesen der Signatur zum Anfang zurueckspringt und deshalb sonst
                    ' ArgumentException wirft. ReadExifCore fing diese Ausnahme fuer den gesamten
                    ' FPX-Zweig ab: Composite-Masse UND EXIF/IPTC/XMP/ICC blieben gemeinsam leer.
                    ' Die Speicher-Kopie ist seekbar und wird nach dem Parsen sofort freigegeben.
                    Using source = imageEntry.Open()
                        Using seekable As New MemoryStream()
                            source.CopyTo(seekable)
                            seekable.Position = 0
                            Return ImageMetadataReader.ReadMetadata(seekable)
                        End Using
                    End Using
                End Using
            Catch
                ' Composite und Basis werden getrennt gelesen. Ein fehlender oder defekter Eintrag
                ' darf daher nur seine eigene Datenquelle leeren, nicht das komplette Infopanel.
                Return Array.Empty(Of MetadataExtractor.Directory)()
            End Try
        End Function

        Private Shared Sub CollectMetadataTags(metaDirectories As IEnumerable(Of MetadataExtractor.Directory),
                                               data As ExifData,
                                               includeExif As Boolean,
                                               includeIptc As Boolean,
                                               includeXmp As Boolean,
                                               includeIcc As Boolean)
            For Each metaDir In If(metaDirectories, Enumerable.Empty(Of MetadataExtractor.Directory)())
                Dim dirType = metaDir.GetType().FullName
                Dim isIcc = String.Equals(dirType, "MetadataExtractor.Formats.Icc.IccDirectory", StringComparison.Ordinal)
                Dim isIptc = String.Equals(dirType, "MetadataExtractor.Formats.Iptc.IptcDirectory", StringComparison.Ordinal)
                Dim isXmp = String.Equals(dirType, "MetadataExtractor.Formats.Xmp.XmpDirectory", StringComparison.Ordinal)

                If isIcc Then
                    If includeIcc Then
                        data.HasIccProfile = True
                        For Each metaTag In metaDir.Tags
                            If Not String.IsNullOrWhiteSpace(metaTag.Description) Then
                                data.IccTags.Add(New ExifTag(metaTag.Name, CleanIccDescription(metaTag.Description)))
                            End If
                        Next
                    End If
                    ' ICC bleibt wie bisher zusaetzlich im technischen EXIF-Baum sichtbar.
                    If Not (includeExif AndAlso includeIcc) Then Continue For
                ElseIf isIptc Then
                    If includeIptc Then
                        For Each metaTag In metaDir.Tags
                            If Not String.IsNullOrWhiteSpace(metaTag.Description) Then
                                data.IptcTags.Add(New ExifTag(metaTag.Name, metaTag.Description))
                            End If
                        Next
                    End If
                    Continue For
                ElseIf isXmp Then
                    If includeXmp Then CollectXmpTags(metaDir, data)
                    Continue For
                End If

                If includeExif Then
                    For Each metaTag In metaDir.Tags
                        If Not String.IsNullOrWhiteSpace(metaTag.Description) Then
                            data.ExifTags.Add(New ExifTag(metaDir.Name & " › " & metaTag.Name, metaTag.Description))
                        End If
                    Next
                End If
            Next
        End Sub

        ''' <summary>Fallback fuer Container ohne EXIF-Pixeldimensionen. Es werden nur Tags wie
        ''' "Image Width"/"Raw Image Full Width" akzeptiert; Aufloesungs-, Crop- und Offsetwerte
        ''' geraten dadurch nicht versehentlich in die Katalogspalten.</summary>
        Private Shared Function GetLargestImageDimension(metaDirectories As IEnumerable(Of MetadataExtractor.Directory),
                                                         dimensionName As String) As Integer?
            Dim largest As Integer? = Nothing
            For Each metaDir In If(metaDirectories, Enumerable.Empty(Of MetadataExtractor.Directory)())
                For Each metaTag In metaDir.Tags
                    Dim name = If(metaTag.Name, "").Trim()
                    If Not name.EndsWith("Image " & dimensionName, StringComparison.OrdinalIgnoreCase) Then Continue For
                    Dim value = ParseLeadingInt(metaTag.Description)
                    If value.HasValue AndAlso value.Value > 0 AndAlso
                       (Not largest.HasValue OrElse value.Value > largest.Value) Then
                        largest = value
                    End If
                Next
            Next
            Return largest
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
            result.FocalLengthMm = ParseLeadingDouble(GetComparableFocalLength(data))
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

        ''' <summary>Erhöhen, sobald sich Auswahl oder Formatierung der Summary-Texte ändert. Zusammen mit
        ''' der Anzeigesprache bildet die Zahl den in der Bibliothek mitgeschriebenen Formatstempel
        ''' (siehe CurrentSummaryFormat): Katalogeinträge aus einer älteren App-Version oder aus einer
        ''' anderen Sprache werden daran erkannt und einmalig neu erzeugt - sonst blieben die alten
        ''' Texte kleben, weil unveränderte Dateien nie erneut eingelesen werden.</summary>
        ' Version 3 invalidiert auch bereits als "leer" gecachte FPX-Eintraege: seit dieser Version
        ' kommen ihre technischen Daten aus composite.png und EXIF/IPTC/XMP/ICC aus base.*.
        Public Const SummaryFormatVersion As Integer = 3

        Public Shared ReadOnly Property CurrentSummaryFormat As String
            Get
                Return $"{SummaryFormatVersion}:{LocalizationService.EffectiveLanguage}"
            End Get
        End Property

        Private Const MaxSummaryLines As Integer = 10
        Private Const MaxSummaryValueLength As Integer = 80

        ''' <summary>Die Reihenfolge, in der MetadataExtractor die Tags liefert, ist technisch und nicht
        ''' inhaltlich sortiert - die ersten Einträge sind bei JPEGs Kompression/Datenpräzision und bei
        ''' Lightroom-XMP dutzende crs:*-Reglerwerte, während Autor, Stichwörter oder Copyright weit
        ''' hinten stehen. Deshalb bestimmt eine Prioritätsliste je Kategorie, was ins Badge-Overlay
        ''' kommt; erst wenn davon nichts zutrifft, fällt die Zusammenfassung auf die Rohreihenfolge
        ''' zurück, damit exotische Dateien nicht mit leerem Overlay dastehen.</summary>
        Private Shared ReadOnly IptcPriorityNames As String() = {
            "Object Name", "Headline", "Caption/Abstract", "Keywords", "By-line", "By-line Title",
            "Credit", "Source", "Copyright Notice", "City", "Sub-location", "Province/State",
            "Country/Primary Location Name", "Date Created", "Special Instructions"
        }

        Private Shared ReadOnly XmpPriorityNames As String() = {
            "xmp:Rating", "xmp:Label", "dc:title", "dc:description", "dc:creator", "dc:subject",
            "lr:hierarchicalSubject", "dc:rights", "photoshop:Headline", "photoshop:City",
            "photoshop:Country", "photoshop:DateCreated", "xmp:CreatorTool", "xmp:ModifyDate"
        }

        ''' Das Nützliche am ICC-Profil ist der Profilname (sagt "Adobe RGB (1998)" statt nur
        ''' "Farbprofil vorhanden") plus Farbraum und Rendering Intent - der Rest sind Kurven und
        ''' Matrizen ohne Aussagewert für die Galerie.
        Private Shared ReadOnly IccPriorityNames As String() = {
            "Profile Description", "Class", "Color space", "Profile Connection Space",
            "Rendering Intent", "Primary Platform", "Device manufacturer", "Device model",
            "Profile Date/Time", "Version", "Copyright"
        }

        Public Shared Function BuildCatalogSummary(data As ExifData, Optional fields As ExifSearchFields = Nothing) As ExifCatalogSummary
            Return New ExifCatalogSummary With {
                .HasExifMetadata = data IsNot Nothing AndAlso data.ExifTags IsNot Nothing AndAlso data.ExifTags.Count > 0,
                .HasIptcMetadata = data IsNot Nothing AndAlso data.IptcTags IsNot Nothing AndAlso data.IptcTags.Count > 0,
                .HasXmpMetadata = data IsNot Nothing AndAlso data.XmpTags IsNot Nothing AndAlso data.XmpTags.Count > 0,
                .HasIccProfile = data IsNot Nothing AndAlso data.HasIccProfile,
                .ExifSummary = BuildExifSummary(data, fields),
                .IptcSummary = BuildTagSummary(If(data?.IptcTags, Nothing), IptcPriorityNames),
                .XmpSummary = BuildTagSummary(If(data?.XmpTags, Nothing), XmpPriorityNames),
                .IccSummary = BuildTagSummary(If(data?.IccTags, Nothing), IccPriorityNames),
                .SummaryFormat = CurrentSummaryFormat
            }
        End Function

        ''' <summary>Die EXIF-Zeilen sind bewusst genau die Felder, die auch in der Bibliothek als eigene
        ''' Spalten liegen und auf die Suche, Filter und Sortierung zugreifen (ExifSearchFields) - was
        ''' das Overlay zeigt, ist damit auch das, wonach man suchen kann. Die Werte stammen aus den
        ''' bereits formatierten Anzeige-Strings ("f/2,8", "1/250 s"), die Maße bevorzugt aus den
        ''' echten Pixelmaßen der Suchfelder statt aus dem oft fehlenden EXIF-Größentag.</summary>
        Public Shared Function BuildExifSummary(data As ExifData, Optional fields As ExifSearchFields = Nothing) As String
            If data Is Nothing Then Return ""

            Dim lines As New List(Of String)()
            AppendSummaryLine(lines, LocalizationService.T("Kamera"), data.Camera)
            AppendSummaryLine(lines, LocalizationService.T("Objektiv"), data.Lens)
            AppendSummaryLine(lines, LocalizationService.T("Brennweite"), FormatFocalLength(data))
            AppendSummaryLine(lines, LocalizationService.T("Blende"), data.Aperture)
            AppendSummaryLine(lines, LocalizationService.T("Belichtungszeit"), data.ShutterSpeed)
            AppendSummaryLine(lines, "ISO", data.ISO)
            AppendSummaryLine(lines, LocalizationService.T("Aufnahmedatum"), FormatExifDate(data.DateTaken))
            AppendSummaryLine(lines, LocalizationService.T("Abmessungen"), FormatDimensions(data, fields))
            AppendSummaryLine(lines, "GPS", data.GPS)
            AppendSummaryLine(lines, LocalizationService.T("Software"), data.Software)
            AppendSummaryLine(lines, LocalizationService.T("Copyright"), data.Copyright)

            If lines.Count > 0 Then Return String.Join(Environment.NewLine, lines)

            ' Kein einziges Aufnahmefeld gefüllt (z.B. gescanntes PNG): lieber die technischen Rohtags
            ' zeigen als ein leeres Overlay unter einem Badge, das "EXIF vorhanden" meldet.
            Return BuildMetadataSummary(data.ExifTags, MaxSummaryLines)
        End Function

        Private Shared Function FormatFocalLength(data As ExifData) As String
            Dim equivalent = If(data.FocalLength35mm, "").Trim()
            Dim actual = If(data.FocalLength, "").Trim()
            If Not ParseLeadingDouble(equivalent).HasValue Then Return actual
            If actual.Length = 0 OrElse String.Equals(equivalent, actual, StringComparison.OrdinalIgnoreCase) Then Return equivalent
            ' Handykameras melden 4,2 mm - erst das Kleinbild-Äquivalent (28 mm) ist einordenbar, deshalb
            ' steht es vorn; die echte Brennweite bleibt dahinter sichtbar.
            Return $"{equivalent} ({LocalizationService.T("Kleinbild")}) · {actual}"
        End Function

        ''' <summary>Rohdatum ("yyyy:MM:dd HH:mm:ss") in die Anzeigeform der AKTUELLEN Sprache. Public,
        ''' weil der ExifDateConverter das Infopanel damit versorgt - dort muss die Formatierung beim
        ''' Rendern passieren, nicht beim Einlesen (zwischengespeicherte ExifData, Sprachwechsel).</summary>
        Public Shared Function FormatExifDate(raw As String) As String
            Dim parsed = ParseExifDateTime(raw)
            If Not parsed.HasValue Then Return If(raw, "")
            Return parsed.Value.ToString("g", LocalizationService.EffectiveCulture)
        End Function

        Private Shared Function FormatDimensions(data As ExifData, fields As ExifSearchFields) As String
            Dim width = If(fields?.ImageWidth, ParseLeadingInt(data.ImageWidth))
            Dim height = If(fields?.ImageHeight, ParseLeadingInt(data.ImageHeight))
            If Not width.HasValue OrElse Not height.HasValue Then Return ""
            Return $"{width.Value} × {height.Value}"
        End Function

        Private Shared Sub AppendSummaryLine(lines As List(Of String), label As String, value As String)
            If lines.Count >= MaxSummaryLines Then Return
            If String.IsNullOrWhiteSpace(value) Then Return
            lines.Add($"{label}: {TrimSummaryValue(value)}")
        End Sub

        ''' <summary>Wählt aus einer Tag-Liste die inhaltlich tragenden Einträge in der Reihenfolge der
        ''' Prioritätsliste aus. Mehrfach indizierte XMP-Werte (dc:subject[1], dc:subject[2], ...) werden
        ''' dabei zu einer Zeile zusammengefasst, sonst würde eine einzige Stichwortliste das ganze
        ''' Overlay füllen.</summary>
        Private Shared Function BuildTagSummary(tags As IEnumerable(Of ExifTag), priorityNames As String()) As String
            Dim grouped = GroupTags(tags)
            If grouped.Count = 0 Then Return ""

            Dim lines As New List(Of String)()
            Dim used As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            For Each wanted In priorityNames
                If lines.Count >= MaxSummaryLines Then Exit For
                For Each tag In grouped
                    If Not String.Equals(tag.Name, wanted, StringComparison.OrdinalIgnoreCase) Then Continue For
                    If Not used.Add(tag.Name) Then Continue For
                    lines.Add($"{tag.Name}: {TrimSummaryValue(tag.Value)}")
                    Exit For
                Next
            Next

            If lines.Count = 0 Then
                Return BuildMetadataSummary(grouped, MaxSummaryLines)
            End If

            Return String.Join(Environment.NewLine, lines)
        End Function

        Private Shared Function GroupTags(tags As IEnumerable(Of ExifTag)) As List(Of ExifTag)
            Dim result As New List(Of ExifTag)()
            Dim byName As New Dictionary(Of String, ExifTag)(StringComparer.OrdinalIgnoreCase)

            For Each tag In If(tags, Enumerable.Empty(Of ExifTag)())
                If tag Is Nothing OrElse String.IsNullOrWhiteSpace(tag.Name) OrElse String.IsNullOrWhiteSpace(tag.Value) Then Continue For
                Dim name = Regex.Replace(tag.Name.Trim(), "\[\d+\]$", "")
                Dim value = tag.Value.Trim()
                Dim existing As ExifTag = Nothing
                If byName.TryGetValue(name, existing) Then
                    existing.Value &= ", " & value
                Else
                    Dim entry = New ExifTag(name, value)
                    byName(name) = entry
                    result.Add(entry)
                End If
            Next

            Return result
        End Function

        Private Shared Function TrimSummaryValue(value As String) As String
            Dim trimmed = If(value, "").Trim()
            If trimmed.Length > MaxSummaryValueLength Then trimmed = trimmed.Substring(0, MaxSummaryValueLength) & "..."
            Return trimmed
        End Function

        Public Shared Function BuildMetadataSummary(tags As IEnumerable(Of ExifTag), maxItems As Integer) As String
            Dim entries = If(tags, Enumerable.Empty(Of ExifTag)()).
                Where(Function(t) t IsNot Nothing AndAlso
                                  Not String.IsNullOrWhiteSpace(t.Name) AndAlso
                                  Not String.IsNullOrWhiteSpace(t.Value)).
                Take(Math.Max(1, maxItems)).
                Select(Function(t) $"{t.Name.Trim()}: {TrimSummaryValue(t.Value)}").
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

        ''' <summary>Die Brennweite, mit der sich Bilder verschiedener Kameras vergleichen lassen:
        ''' das Kleinbild-Äquivalent, falls die Kamera es liefert, sonst die echte Brennweite. Ohne das
        ''' sortierten Handybilder mit 4,2 mm zwischen echten Ultraweitwinkeln, obwohl sie einem 28er
        ''' entsprechen. Der Rohwert bleibt in ExifData.FocalLength und in der EXIF-Liste sichtbar.</summary>
        Public Shared Function GetComparableFocalLength(data As ExifData) As String
            If data Is Nothing Then Return ""
            ' Nicht auf "nicht leer" prüfen: Steht der Tag zwar im Bild, ist aber unbrauchbar belegt,
            ' liefert MetadataExtractor den Text "Unknown". Der gewann hier gegen die echte Brennweite,
            ' und da aus ihm keine Zahl zu holen ist, blieb FocalLengthMm im Katalog leer - solche Bilder
            ' fielen aus jedem Brennweiten-Filter heraus, obwohl der Wert in der Datei steht.
            If ParseLeadingDouble(data.FocalLength35mm).HasValue Then Return data.FocalLength35mm
            Return data.FocalLength
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

        ''' <summary>Sucht einen Tag über ALLE Verzeichnisse des angegebenen Typs und liefert den ersten
        ''' belegten Wert. RAW-Dateien (DNG/ARW/NEF) bringen mehrere "Exif SubIFD"-Verzeichnisse mit - neben
        ''' dem der Aufnahme noch je eines für Vorschaubild- und Rohbild-Ebene. Das zuvor verwendete
        ''' FirstOrDefault() erwischte davon regelmäßig ein Ebenen-Verzeichnis ohne Aufnahmedaten, weshalb
        ''' Blende, ISO, Belichtungszeit und Objektiv bei RAWs leer blieben - und zwar nicht nur in der
        ''' Anzeige, sondern auch in Suche, Filter und Sortierung, die auf denselben Feldern arbeiten.</summary>
        Private Shared Function GetTagDescAcross(Of TDirectory As MetadataExtractor.Directory)(
                metaDirectories As IEnumerable(Of MetadataExtractor.Directory), tagType As Integer) As String
            For Each metaDir In metaDirectories.OfType(Of TDirectory)()
                Dim desc = metaDir.GetDescription(tagType)
                If Not String.IsNullOrWhiteSpace(desc) Then Return desc
            Next
            Return ""
        End Function

        ''' <summary>ICC-v4-Profile hinterlegen ihren Namen mehrsprachig; MetadataExtractor gibt das roh als
        ''' "1 enUS(sRGB IEC61966-2.1)" aus. Da der Profilname der eigentlich interessante Wert am
        ''' ICC-Badge ist, wird die Sprachverpackung entfernt - passt das Muster nicht, bleibt der Text
        ''' unangetastet.</summary>
        Private Shared Function CleanIccDescription(description As String) As String
            Dim match = Regex.Match(If(description, "").Trim(), "^\d+\s+\w+\((.+)\)$")
            If match.Success Then Return match.Groups(1).Value.Trim()
            Return If(description, "").Trim()
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

            ' Eine VORHANDENE Sidecar unter beiden Namenskonventionen suchen ("foto.cr2.xmp" wie
            ' darktable/digiKam, "foto.xmp" wie Adobe). Vorher stand hier nur ChangeExtension - eine
            ' darktable-Sidecar wurde deshalb nie gefunden und die Bewertung lief ins Leere.
            Dim sidecarPath = XmpSidecarService.FindSidecar(imagePath)
            If String.IsNullOrEmpty(sidecarPath) Then
                ' Neu anlegen (nur wenn ausdrücklich erlaubt) in der Adobe-Form.
                If Not createIfMissing Then Return False
                sidecarPath = IO.Path.ChangeExtension(imagePath, ".xmp")
                If String.IsNullOrWhiteSpace(sidecarPath) Then Return False
            End If

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

        ''' <summary>Schreibt Katalogdaten (Bewertung, Farb-Label, Stichworte) in ein Adobe-XMP-Sidecar -
        ''' die Verallgemeinerung von <see cref="WriteXmpRatingSidecar"/>. Vorhandene fremde Knoten
        ''' bleiben erhalten (nur die betroffenen werden gesetzt/ersetzt). <paramref name="colorLabelWord"/>
        ''' ist das englische Lightroom-Farbwort (siehe XmpSidecarService.LabelToXmpWord); leer entfernt
        ''' xmp:Label. Leere Stichwortliste entfernt dc:subject. Legt nur bei
        ''' <paramref name="createIfMissing"/> eine neue Datei an. Gegated wird über die Einstellung im
        ''' Aufrufer (LibraryService), NICHT hier.</summary>
        Public Shared Function WriteXmpCatalogSidecar(imagePath As String, rating As Integer, colorLabelWord As String,
                                                      keywords As IEnumerable(Of String), createIfMissing As Boolean) As Boolean
            If String.IsNullOrWhiteSpace(imagePath) Then Return False

            Dim sidecarPath = XmpSidecarService.FindSidecar(imagePath)
            If String.IsNullOrEmpty(sidecarPath) Then
                If Not createIfMissing Then Return False
                sidecarPath = IO.Path.ChangeExtension(imagePath, ".xmp")
                If String.IsNullOrWhiteSpace(sidecarPath) Then Return False
            End If

            Dim xNamespace As XNamespace = "adobe:ns:meta/"
            Dim rdfNamespace As XNamespace = "http://www.w3.org/1999/02/22-rdf-syntax-ns#"
            Dim xmpNamespace As XNamespace = "http://ns.adobe.com/xap/1.0/"
            Dim dcNamespace As XNamespace = "http://purl.org/dc/elements/1.1/"
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

                ' xmp:Label: bekanntes Farbwort setzen, sonst vorhandenes Attribut entfernen (nicht raten).
                If Not String.IsNullOrEmpty(colorLabelWord) Then
                    description.SetAttributeValue(xmpNamespace + "Label", colorLabelWord)
                Else
                    description.SetAttributeValue(xmpNamespace + "Label", Nothing)
                End If

                ' dc:subject als rdf:Bag neu aufbauen (vorhandenes ersetzen); leere Liste entfernt es.
                Dim existingSubject = description.Element(dcNamespace + "subject")
                If existingSubject IsNot Nothing Then existingSubject.Remove()
                Dim tagList = If(keywords, Enumerable.Empty(Of String)()).
                    Select(Function(k) If(k, "").Trim()).Where(Function(k) k.Length > 0).
                    Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                If tagList.Count > 0 Then
                    description.SetAttributeValue(XNamespace.Xmlns + "dc", dcNamespace.NamespaceName)
                    Dim bag = New XElement(rdfNamespace + "Bag")
                    For Each tag In tagList
                        bag.Add(New XElement(rdfNamespace + "li", tag))
                    Next
                    description.Add(New XElement(dcNamespace + "subject", bag))
                End If

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
