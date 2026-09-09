Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports MetadataExtractor
Imports MetadataExtractor.Formats.Exif

Namespace Services

    ''' <summary>Baut einen EXIF-Block aus dem, was sich aus einer Quelle LESEN laesst.
    '''
    ''' WOFUER. Die Uebernahme der Aufnahmedaten beim Speichern arbeitet sonst bytegenau: sie holt
    ''' den EXIF-Block als Bytes aus der Quelle und legt ihn unveraendert ins Ziel. Das geht nur bei
    ''' JPEG, PNG und WEBP - dort kennen wir den Container und finden den Block darin. Fuer alles
    ''' andere gab es gar nichts: eine RAW-Datei, als JPEG exportiert, kam OHNE jede Aufnahmeangabe
    ''' heraus, obwohl die Uebernahme eingeschaltet war und die Info-Leiste Kamera, Zeit und
    ''' Objektiv die ganze Zeit angezeigt hat. Genau die Datei, bei der man die Angaben am ehesten
    ''' behalten will, verlor sie.
    '''
    ''' WARUM NICHT DIE BYTES DER RAW-DATEI. Die meisten RAW-Formate sind selbst TIFF, ihr EXIF
    ''' liesse sich also scheinbar herausschneiden. Es waere unbrauchbar: die Eintraege verweisen
    ''' ueber absolute Adressen auf Bildstreifen und Herstellernotizen der RAW-Datei, im JPEG zeigen
    ''' sie damit ins Leere, und ein JPEG-Segment fasst ohnehin nur knapp 64 KB. Deshalb wird hier
    ''' ein KLEINER, frischer Block gebaut - aus gelesenen Werten, mit eigenen Adressen.
    '''
    ''' WAS MITKOMMT, ist das, was ein Mensch als Aufnahmeangabe versteht und was die Info-Leiste
    ''' zeigt: Kamera, Objektiv, Zeit, Blende, Belichtungszeit, ISO, Brennweite, Urheberrecht,
    ''' Aufnahmeort. Nicht mitkommen Herstellernotizen (sie sind undokumentiert und ohne ihre
    ''' Originaladressen wertlos) und das eingebettete Vorschaubild (es zeigte das unbearbeitete
    ''' Bild).
    '''
    ''' AUSRICHTUNG UND FARBRAUM stehen fest auf "normal" und sRGB. Das Bild ist an dieser Stelle
    ''' bereits gedreht und ins Ziel gerechnet; die Werte der Quelle zu uebernehmen hiesse, eine
    ''' Drehung ein zweites Mal anzukuendigen. Es ist dieselbe Regel wie beim bytegenauen Weg, der
    ''' beide Felder ebenfalls geradezieht.</summary>
    Public Class ExifBuilderService

        ' TIFF/EXIF-Tags. Nummern statt Namen aus der Fremdbibliothek, weil sie hier GESCHRIEBEN
        ' werden und die Zahl das ist, was in der Datei steht.
        Private Const TagMake As Integer = &H10F
        Private Const TagModel As Integer = &H110
        Private Const TagOrientation As Integer = &H112
        Private Const TagSoftware As Integer = &H131
        Private Const TagDateTime As Integer = &H132
        Private Const TagArtist As Integer = &H13B
        Private Const TagCopyright As Integer = &H8298
        Private Const TagExifIfdPointer As Integer = &H8769

        Private Const TagExposureTime As Integer = &H829A
        Private Const TagFNumber As Integer = &H829D
        Private Const TagIsoSpeed As Integer = &H8827
        Private Const TagDateTimeOriginal As Integer = &H9003
        Private Const TagDateTimeDigitized As Integer = &H9004
        Private Const TagFocalLength As Integer = &H920A
        Private Const TagColorSpace As Integer = &HA001
        Private Const TagFocalLength35mm As Integer = &HA405
        Private Const TagLensMake As Integer = &HA433
        Private Const TagLensModel As Integer = &HA434

        Private Const OrientationNormal As Integer = 1
        Private Const ColorSpaceSrgb As Integer = 1

        ''' Kleinschreibendes "II" wie beim Block, den GeotagService neu anlegt.
        Private Const LittleEndian As Boolean = True

        ''' <summary>Der EXIF-Block als nackte TIFF-Bytes, ohne die JPEG-Kennung "Exif" davor - der
        ''' Aufrufer setzt sie selbst. Nothing, wenn sich nichts Nennenswertes lesen laesst; dann
        ''' wird wie bisher ohne Aufnahmedaten geschrieben, statt einen leeren Block anzulegen.</summary>
        Friend Shared Function BuildExifTiff(sourcePath As String) As Byte()
            If String.IsNullOrWhiteSpace(sourcePath) OrElse Not IO.File.Exists(sourcePath) Then Return Nothing

            Try
                Dim directories = ImageMetadataReader.ReadMetadata(sourcePath)
                Dim ifd0 = CollectIfd0Fields(directories)
                Dim exif = CollectExifFields(directories)
                ' DER AUFNAHMEORT WIRD VOR DER SUBSTANZFRAGE GELESEN, weil er selbst Substanz ist.
                ' Stand die Frage davor, fiel eine Quelle durch, die NUR eine Koordinate traegt -
                ' ein mit dem Telefon aufgenommenes HEIC ohne Kameraangaben etwa. Der Ort war
                ' lesbar, der Block wurde trotzdem nicht gebaut, und beim Export ging er verloren.
                Dim gps = TryReadCoordinates(directories)

                ' WAS DER METADATENLESER NICHT KENNT, KOMMT AUS LIBRAW. Fuenf Containerarten bringen
                ' ihm gar nichts bei - CIFF von Canon (.crw), Minoltas .mrw, die alten .raw von
                ' Leica, Panasonic und Kodak sowie .mos von Leaf: er erkennt den Typ sauber und
                ' liefert dann null Angaben. Ohne diesen Nachlauf blieb der Export dieser Dateien
                ' leer, obwohl die Info-Leiste Kamera und Zeit anzeigt - sie holt sie ueber
                ' denselben Weg (siehe ExifService.FillGapsFromRawFile).
                '
                ' NUR LUECKEN FUELLEN, nie ueberschreiben: der eingebettete Wert ist der genauere,
                ' er traegt Sekundenbruchteile und die Schreibweise des Herstellers. Und die Regel
                ' haelt den Preis bei null - bei einer Datei mit vollstaendigen Angaben wird LibRaw
                ' gar nicht erst gefragt.
                FillGapsFromRawFile(sourcePath, ifd0, exif)

                ' EIN Block, der nur Ausrichtung und Farbraum traegt, ist keiner: er saehe im Ziel
                ' aus wie eine Aufnahmeangabe und enthielte keine. Erst ein echter Wert zaehlt.
                If Not HasSubstance(ifd0) AndAlso Not HasSubstance(exif) AndAlso Not gps.HasValue Then Return Nothing

                Dim tiff = Assemble(ifd0, exif)
                If tiff Is Nothing Then Return Nothing

                If gps.HasValue Then
                    Dim withGps = GeotagService.AppendGpsToTiff(tiff, gps.Value.Latitude, gps.Value.Longitude, gps.Value.Altitude)
                    If withGps IsNot Nothing Then tiff = withGps
                End If

                ' Ein Block, der nicht mehr in ein JPEG-Segment passt, wird nicht halbiert, sondern
                ' gar nicht geschrieben - eine abgeschnittene Adresstabelle ist schlimmer als keine.
                If tiff.Length > GeotagService.MaxTiffBlockInJpeg Then
                    DiagnosticLogService.LogAlways("ExifBuilder",
                        $"Block zu gross fuer ein JPEG-Segment ({tiff.Length} Byte) - {IO.Path.GetFileName(sourcePath)} bekommt keine Aufnahmedaten")
                    Return Nothing
                End If
                Return tiff
            Catch ex As Exception
                ' Ein unlesbarer Kopf darf das Speichern nicht kosten: das BILD ist der Auftrag,
                ' die Aufnahmedaten sind die Zugabe.
                DiagnosticLogService.LogException("ExifBuilderService.BuildExifTiff", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Traegt die Liste etwas anderes als die beiden festgesetzten Felder?</summary>
        Private Shared Function HasSubstance(fields As List(Of GeotagService.TiffField)) As Boolean
            Return fields.Any(Function(f) f.Tag <> TagOrientation AndAlso f.Tag <> TagColorSpace)
        End Function

        Private Shared Function Has(fields As List(Of GeotagService.TiffField), tag As Integer) As Boolean
            Return fields.Any(Function(f) f.Tag = tag)
        End Function

        ''' <summary>Fehlende Aufnahmeangaben aus der RAW-Datei selbst nachtragen - ueber LibRaw,
        ''' das beim Oeffnen ohnehin alles davon liest. Ein Aufruf kostet ein open_file.
        '''
        ''' Die Belichtungszeit wird als Bruch gefuehrt, wie EXIF sie erwartet: unter einer Sekunde
        ''' als Stammbruch (1/250), darueber in Hundertstel. Blende und Brennweite gehen als
        ''' Hundertstel-Bruch, damit f/6,3 und 10,5 mm nicht auf ganze Zahlen gerundet werden.</summary>
        Private Shared Sub FillGapsFromRawFile(sourcePath As String,
                                               ifd0 As List(Of GeotagService.TiffField),
                                               exif As List(Of GeotagService.TiffField))
            If Not RawPreviewService.IsSupportedRaw(sourcePath) Then Return

            ' HERSTELLER UND MODELL EINZELN, wie Breite und Hoehe im ExifService: eine Datei, die nur
            ' eine der beiden Angaben traegt, bekaeme die fehlende sonst nicht. Ein gemeinsames
            ' Kennzeichen fuer zwei Felder hatte dort genau das getan.
            Dim needsMake = Not Has(ifd0, TagMake)
            Dim needsModel = Not Has(ifd0, TagModel)
            Dim needsTaken = Not Has(exif, TagDateTimeOriginal)
            Dim needsIso = Not Has(exif, TagIsoSpeed)
            Dim needsAperture = Not Has(exif, TagFNumber)
            Dim needsShutter = Not Has(exif, TagExposureTime)
            Dim needsFocal = Not Has(exif, TagFocalLength)
            If Not (needsMake OrElse needsModel OrElse needsTaken OrElse needsIso OrElse
                    needsAperture OrElse needsShutter OrElse needsFocal) Then Return

            Try
                Dim fromFile = RawDecodeService.ReadFileMetadata(sourcePath)
                If fromFile Is Nothing Then Return

                If needsMake Then AddAscii(ifd0, TagMake, If(fromFile.Make, "").Trim())
                If needsModel Then AddAscii(ifd0, TagModel, If(fromFile.Model, "").Trim())
                If needsTaken AndAlso fromFile.Taken.HasValue Then
                    AddAscii(exif, TagDateTimeOriginal,
                             fromFile.Taken.Value.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture))
                End If
                If needsIso AndAlso fromFile.Iso > 0 Then
                    AddShort(exif, TagIsoSpeed, CInt(Math.Min(UShort.MaxValue, Math.Round(fromFile.Iso))))
                End If
                If needsAperture AndAlso fromFile.Aperture > 0 Then
                    AddRational(exif, TagFNumber, AsRational(fromFile.Aperture))
                End If
                If needsShutter AndAlso fromFile.ShutterSeconds > 0 Then
                    AddRational(exif, TagExposureTime, AsShutterRational(fromFile.ShutterSeconds))
                End If
                If needsFocal AndAlso fromFile.FocalLengthMm > 0 Then
                    AddRational(exif, TagFocalLength, AsRational(fromFile.FocalLengthMm))
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("ExifBuilderService.FillGapsFromRawFile", ex)
            End Try
        End Sub

        ''' Hundertstel reichen fuer Blende und Brennweite und halten den Nenner klein.
        Private Shared Function AsRational(value As Double) As Rational?
            If value <= 0 OrElse Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return Nothing
            Return New Rational(CLng(Math.Round(value * 100.0)), 100L)
        End Function

        ''' <summary>Die Belichtungszeit so, wie eine Kamera sie schreibt: 1/250 statt 0,004.
        ''' Ueber einer Sekunde als Hundertstel, damit 2,5 Sekunden nicht zu 1/0 werden.</summary>
        Private Shared Function AsShutterRational(seconds As Double) As Rational?
            If seconds <= 0 OrElse Double.IsNaN(seconds) OrElse Double.IsInfinity(seconds) Then Return Nothing
            If seconds >= 1.0 Then Return New Rational(CLng(Math.Round(seconds * 100.0)), 100L)
            Dim denominator = CLng(Math.Round(1.0 / seconds))
            If denominator < 1 Then denominator = 1
            Return New Rational(1L, denominator)
        End Function

        ' ---------------------------------------------------------------------------------------
        ' Die Werte einsammeln
        ' ---------------------------------------------------------------------------------------

        Private Shared Function CollectIfd0Fields(directories As IEnumerable(Of MetadataExtractor.Directory)) As List(Of GeotagService.TiffField)
            Dim fields As New List(Of GeotagService.TiffField)()

            AddAscii(fields, TagMake, FindText(Of ExifIfd0Directory)(directories, ExifDirectoryBase.TagMake))
            AddAscii(fields, TagModel, FindText(Of ExifIfd0Directory)(directories, ExifDirectoryBase.TagModel))
            AddAscii(fields, TagSoftware, FindText(Of ExifIfd0Directory)(directories, ExifDirectoryBase.TagSoftware))
            AddAscii(fields, TagArtist, FindText(Of ExifIfd0Directory)(directories, ExifDirectoryBase.TagArtist))
            AddAscii(fields, TagCopyright, FindText(Of ExifIfd0Directory)(directories, ExifDirectoryBase.TagCopyright))
            AddAscii(fields, TagDateTime, FindText(Of ExifIfd0Directory)(directories, ExifDirectoryBase.TagDateTime))

            ' Fest, nicht uebernommen - siehe der Absatz zur Ausrichtung oben.
            AddShort(fields, TagOrientation, OrientationNormal)
            Return fields
        End Function

        Private Shared Function CollectExifFields(directories As IEnumerable(Of MetadataExtractor.Directory)) As List(Of GeotagService.TiffField)
            Dim fields As New List(Of GeotagService.TiffField)()

            AddAscii(fields, TagDateTimeOriginal, FindText(Of ExifSubIfdDirectory)(directories, ExifDirectoryBase.TagDateTimeOriginal))
            AddAscii(fields, TagDateTimeDigitized, FindText(Of ExifSubIfdDirectory)(directories, ExifDirectoryBase.TagDateTimeDigitized))
            AddAscii(fields, TagLensMake, FindText(Of ExifSubIfdDirectory)(directories, ExifDirectoryBase.TagLensMake))
            AddAscii(fields, TagLensModel, FindText(Of ExifSubIfdDirectory)(directories, ExifDirectoryBase.TagLensModel))

            AddRational(fields, TagExposureTime, FindRational(Of ExifSubIfdDirectory)(directories, ExifDirectoryBase.TagExposureTime))
            AddRational(fields, TagFNumber, FindRational(Of ExifSubIfdDirectory)(directories, ExifDirectoryBase.TagFNumber))
            AddRational(fields, TagFocalLength, FindRational(Of ExifSubIfdDirectory)(directories, ExifDirectoryBase.TagFocalLength))

            AddShort(fields, TagIsoSpeed, FindShort(Of ExifSubIfdDirectory)(directories, ExifDirectoryBase.TagIsoEquivalent))
            AddShort(fields, TagFocalLength35mm, FindShort(Of ExifSubIfdDirectory)(directories, ExifDirectoryBase.Tag35MMFilmEquivFocalLength))

            ' Fest sRGB: das Ziel wird in sRGB geschrieben, egal was die Quelle fuehrte.
            AddShort(fields, TagColorSpace, ColorSpaceSrgb)
            Return fields
        End Function

        Private Structure Coordinates
            Public Latitude As Double
            Public Longitude As Double
            Public Altitude As Double?
        End Structure

        Private Shared Function TryReadCoordinates(directories As IEnumerable(Of MetadataExtractor.Directory)) As Coordinates?
            Dim gps = directories.OfType(Of GpsDirectory)().FirstOrDefault()
            If gps Is Nothing Then Return Nothing

            Dim location As GeoLocation = Nothing
            If Not gps.TryGetGeoLocation(location) Then Return Nothing
            If location.IsZero Then Return Nothing

            Dim altitude As Double? = Nothing
            Dim meters As Double
            If gps.TryGetDouble(GpsDirectory.TagAltitude, meters) AndAlso Not Double.IsNaN(meters) AndAlso Not Double.IsInfinity(meters) Then
                ' Das Vorzeichen steht in einem eigenen Feld: 1 heisst unter dem Meeresspiegel.
                Dim reference As Integer
                If gps.TryGetInt32(GpsDirectory.TagAltitudeRef, reference) AndAlso reference = 1 Then meters = -meters
                altitude = meters
            End If

            Return New Coordinates With {.Latitude = location.Latitude, .Longitude = location.Longitude, .Altitude = altitude}
        End Function

        ' ---------------------------------------------------------------------------------------
        ' Lesen und Eintragen
        ' ---------------------------------------------------------------------------------------

        ''' <summary>ALLE Verzeichnisse dieser Art durchsehen, nicht nur das erste.
        '''
        ''' Eine RAW-Datei bringt MEHRERE Aufnahme-Verzeichnisse mit: die eingebetteten Vorschauen
        ''' haengen als eigene Unterverzeichnisse an IFD0 und kommen als derselbe Typ herein. Das
        ''' erste ist dann oft eine 160x120-Vorschau ohne Objektiv und ohne Blende. Wer
        ''' FirstOrDefault nimmt, bekommt Kamera und Modell (die stehen in IFD0) und verliert
        ''' Objektiv, Brennweite und Blende - gefunden hat das die Messung, dem Quelltext sah man es
        ''' nicht an. ExifService liest aus demselben Grund ueber alle Verzeichnisse hinweg.</summary>
        Private Shared Function FindText(Of T As MetadataExtractor.Directory)(
                directories As IEnumerable(Of MetadataExtractor.Directory), tag As Integer) As String
            For Each directory In directories.OfType(Of T)()
                If Not directory.ContainsTag(tag) Then Continue For
                Dim text = If(directory.GetString(tag), "").Trim()
                If text.Length > 0 Then Return text
            Next
            Return ""
        End Function

        ''' Nothing statt 0/1, damit ein FEHLENDER Wert nicht als "null Sekunden" im Ziel landet.
        Private Shared Function FindRational(Of T As MetadataExtractor.Directory)(
                directories As IEnumerable(Of MetadataExtractor.Directory), tag As Integer) As Rational?
            For Each directory In directories.OfType(Of T)()
                If Not directory.ContainsTag(tag) Then Continue For
                Dim value As Rational = Nothing
                If Not directory.TryGetRational(tag, value) Then Continue For
                If value.Denominator = 0 Then Continue For
                If value.Numerator < 0 OrElse value.Denominator < 0 Then Continue For
                Return value
            Next
            Return Nothing
        End Function

        Private Shared Function FindShort(Of T As MetadataExtractor.Directory)(
                directories As IEnumerable(Of MetadataExtractor.Directory), tag As Integer) As Integer?
            For Each directory In directories.OfType(Of T)()
                If Not directory.ContainsTag(tag) Then Continue For
                Dim value As Integer
                If Not directory.TryGetInt32(tag, value) Then Continue For
                ' Das Feld ist zwei Byte breit; was nicht hineinpasst, wird weggelassen statt gekappt.
                If value < 0 OrElse value > UShort.MaxValue Then Continue For
                Return value
            Next
            Return Nothing
        End Function

        Private Shared Sub AddAscii(fields As List(Of GeotagService.TiffField), tag As Integer, value As String)
            If String.IsNullOrWhiteSpace(value) Then Return
            fields.Add(GeotagService.AsciiField(tag, value))
        End Sub

        Private Shared Sub AddShort(fields As List(Of GeotagService.TiffField), tag As Integer, value As Integer?)
            If Not value.HasValue Then Return
            ' Zwei Byte stehen im Eintrag selbst; die hinteren zwei bleiben leer, so verlangt es der
            ' Aufbau fuer einen einzelnen SHORT.
            Dim data(1) As Byte
            GeotagService.WriteUInt16(data, 0, value.Value, LittleEndian)
            fields.Add(New GeotagService.TiffField With {
                .Tag = tag, .FieldType = GeotagService.TiffTypeShort, .Count = 1, .Data = data})
        End Sub

        Private Shared Sub AddRational(fields As List(Of GeotagService.TiffField), tag As Integer, value As Rational?)
            If Not value.HasValue Then Return
            fields.Add(New GeotagService.TiffField With {
                .Tag = tag, .FieldType = GeotagService.TiffTypeRational, .Count = 1,
                .Data = GeotagService.EncodeRational(value.Value.Numerator, value.Value.Denominator, LittleEndian)})
        End Sub

        ' ---------------------------------------------------------------------------------------
        ' Zusammensetzen
        ' ---------------------------------------------------------------------------------------

        ''' <summary>Kopf, IFD0 und das Aufnahme-Verzeichnis dahinter.
        '''
        ''' DIE REIHENFOLGE IST PFLICHT: die Eintraege eines IFD stehen nach Tag-Nummer aufsteigend,
        ''' sonst hoeren manche Leser nach dem ersten Rueckschritt auf. Und der Zeiger von IFD0 auf
        ''' das Aufnahme-Verzeichnis braucht dessen Adresse, bevor es geschrieben ist - deshalb wird
        ''' die Groesse von IFD0 vorher gerechnet, statt sie hinterher zu messen.</summary>
        Private Shared Function Assemble(ifd0Fields As List(Of GeotagService.TiffField),
                                         exifFields As List(Of GeotagService.TiffField)) As Byte()
            Const headerSize As Integer = 8
            Dim ifd0 = ifd0Fields.OrderBy(Function(f) f.Tag).ToList()
            Dim exif = exifFields.OrderBy(Function(f) f.Tag).ToList()

            If exif.Count > 0 Then
                ' Der Zeiger zaehlt als Eintrag von IFD0 und geht damit in dessen Groesse ein. Sein
                ' Wert wird gleich eingesetzt, den Platz belegt er schon jetzt.
                Dim pointer(3) As Byte
                ifd0.Add(New GeotagService.TiffField With {
                    .Tag = TagExifIfdPointer, .FieldType = GeotagService.TiffTypeLong, .Count = 1, .Data = pointer})
                ifd0 = ifd0.OrderBy(Function(f) f.Tag).ToList()

                Dim exifIfdOffset = headerSize + GeotagService.IfdBlockSize(ifd0)
                For i = 0 To ifd0.Count - 1
                    If ifd0(i).Tag <> TagExifIfdPointer Then Continue For
                    Dim replaced = ifd0(i)
                    Dim data(3) As Byte
                    GeotagService.WriteUInt32(data, 0, CUInt(exifIfdOffset), LittleEndian)
                    replaced.Data = data
                    ifd0(i) = replaced
                    Exit For
                Next
            End If

            If ifd0.Count = 0 Then Return Nothing
            If ifd0.Count > GeotagService.MaxIfdEntries OrElse exif.Count > GeotagService.MaxIfdEntries Then Return Nothing

            Dim output As New List(Of Byte)(256)
            output.Add(CByte(AscW("I"c)))
            output.Add(CByte(AscW("I"c)))
            GeotagService.AppendUInt16(output, 42, LittleEndian)
            GeotagService.AppendUInt32(output, CUInt(headerSize), LittleEndian)

            output.AddRange(GeotagService.BuildIfd(ifd0, headerSize, LittleEndian))
            If exif.Count > 0 Then
                output.AddRange(GeotagService.BuildIfd(exif, output.Count, LittleEndian))
            End If
            Return output.ToArray()
        End Function

    End Class

End Namespace
