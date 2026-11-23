Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports BitMiracle.LibTiff.Classic
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Schreibt TIFF mit 8 oder 16 Bit je Kanal. Nur als NEUE Datei: Speichern unter und die Stapelwege
    ''' mit Zielordner, nie ueber ein TIFF-Original (siehe <see cref="ImageProcessor.CanEncodeAsNewFile"/>).
    '''
    ''' DIE BITTIEFE FOLGT DEM BILD. Die Pipeline rechnet hinter dem Decode mit 8 Bit ("Kein
    ''' 16-Bit-Arbeitsbild" in FALLEN_UND_ENTSCHEIDUNGEN.md); ein 16-Bit-TIFF daraus truege nur 8 Bit
    ''' Inhalt, hochskaliert, und verspraeche damit etwas, das die Datei nicht haelt. 16 Bit schreibt
    ''' dieser Schreiber deshalb nur fuer ein tiefes Bild (Rgba16161616, ImageProcessorDeep.vb), das
    ''' die Kette durchgehend mitgerechnet hat.
    '''
    ''' DIE BILDPUNKTE gehen verlustfrei mit Deflate und waagerechter Vorhersage hinein, in Streifen
    ''' zu 64 Zeilen, die parallel gepackt und von LibTiff.NET nur noch geschrieben werden (siehe
    ''' <see cref="StripEncoding"/>). Ein
    ''' Alphakanal kommt nur mit, wenn das Bild wirklich Transparenz traegt; sonst waere jede Datei
    ''' ein Drittel groesser, ohne etwas mehr zu zeigen.
    '''
    ''' DIE AUFNAHMEDATEN kommen aus <see cref="ExifBuilderService"/>, also dieselbe Auswahl wie beim
    ''' Export einer RAW-Datei nach JPEG. Kamera, Zeit und Urheber stehen im Verzeichnis des Bildes
    ''' selbst und gehen ueber LibTiff hinein. Das Aufnahme-Verzeichnis und der Ort werden danach
    ''' ANGEHAENGT, weil LibTiff.NET 2.4 kein eigenes EXIF-Verzeichnis schreiben kann
    ''' (CreateEXIFDirectory und WriteCustomDirectory gibt es dort nicht). Angehaengt wird wie beim
    ''' Aufnahmeort im JPEG: nichts wird verschoben, hinten kommen die Verzeichnisse und eine Kopie
    ''' von IFD0 mit den beiden Verweisen dazu, und nur der Zeiger im Kopf wird umgebogen.
    '''
    ''' KEIN FARBPROFIL. Die Bildpunkte sind sRGB wie beim JPEG-Weg, und das Aufnahme-Verzeichnis
    ''' sagt es ueber ColorSpace = 1.
    ''' </summary>
    Public NotInheritable Class TiffWriterService

        Private Sub New()
        End Sub

        ''' <summary>Darf ein VORHANDENES TIFF durch ein neues ersetzt werden? Nur, wenn es nicht mehr
        ''' traegt, als dieser Schreiber hineinlegt: eine Seite, hoechstens 8 Bit je Kanal, RGB oder
        ''' Graustufen, keine Photoshop-Ebenen. Sonst machte "Speichern unter" mit Ueberschreiben aus
        ''' einem mehrseitigen, 16-Bit-, CMYK- oder Ebenen-TIFF still ein 8-Bit-Einzelbild.
        '''
        ''' Warum nicht jedes vorhandene TIFF sperren: dann scheiterte schon der zweite TIFF-Export in
        ''' denselben Ordner, obwohl der Nutzer im Konfliktdialog "Ueberschreiben" gewaehlt hat - und
        ''' an einer Datei, die dieser Schreiber selbst angelegt hat. Was sich nicht lesen laesst,
        ''' gilt als nicht ersetzbar.</summary>
        Friend Shared Function CanReplaceExisting(path As String, Optional writesDeep As Boolean = False) As Boolean
            ' <writesDeep>: das neue Bild geht mit 16 Bit hinein; dann darf auch ein vorhandenes
            ' TIFF mit 16 Bit weichen, mehr als das weiterhin nicht.
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return True
            Try
                Using tif = Tiff.Open(path, "r")
                    If tif Is Nothing Then Return False
                    If tif.NumberOfDirectories() <> 1 Then Return False
                    Dim bits = tif.GetField(TiffTag.BITSPERSAMPLE)
                    If bits IsNot Nothing AndAlso bits.Length > 0 AndAlso
                       bits(0).ToInt() > If(writesDeep, 16, 8) Then Return False
                    ' Nicht "photometric" nennen: der Name verdeckte in VB die Aufzaehlung Photometric.
                    Dim colorModel = tif.GetField(TiffTag.PHOTOMETRIC)
                    If colorModel IsNot Nothing AndAlso colorModel.Length > 0 Then
                        Select Case CType(colorModel(0).ToInt(), Photometric)
                            Case Photometric.SEPARATED, Photometric.CIELAB, Photometric.ICCLAB, Photometric.ITULAB
                                Return False
                        End Select
                    End If
                    If tif.GetField(CType(TagPhotoshopLayers, TiffTag)) IsNot Nothing Then Return False
                    Return True
                End Using
            Catch
                Return False
            End Try
        End Function

        ''' Photoshop legt die Ebenen eines TIFF in diesem Tag ab (ImageSourceData).
        Private Const TagPhotoshopLayers As Integer = 37724

        Private Const TagCopyright As Integer = &H8298
        Private Const TagExifIfdPointer As Integer = &H8769
        Private Const TagGpsIfdPointer As Integer = &H8825

        ''' <summary>Zeilen je Streifen fuer ein Bild dieser Breite, oder 0, wenn nicht einmal eine
        ''' Zeile in einen Puffer passt. In der Regel <see cref="StripEncoding.RowsPerStrip"/>: klein
        ''' genug, dass ein Leser nicht das ganze Bild auf einmal entpacken muss, gross genug, dass
        ''' die Streifentabelle kurz bleibt. Weniger nur bei sehr breiten Bildern
        ''' (<see cref="StripEncoding.RowsPerStripFor"/>). Dieselbe Zahl steht im Tag ROWSPERSTRIP
        ''' und teilt die Streifen beim Packen ein (siehe PackStrip). Der groessere der beiden
        ''' Puffer zaehlt: RGBA zum Lesen und der gepackte Streifen.</summary>
        Friend Shared Function StripRows(width As Integer, sampleBytes As Integer, samples As Integer) As Integer
            Return StripEncoding.RowsPerStripFor(
                Function(r) Math.Max(r * CLng(width) * 4 * sampleBytes,
                                     StripEncoding.PackedBound(r * CLng(width) * samples * sampleBytes)))
        End Function

        ''' <summary>Schreibt <paramref name="bitmap"/> atomar nach <paramref name="targetPath"/>.
        ''' <paramref name="metadata"/> darf Nothing sein (Uebernahme aus oder nichts lesbar). Ein
        ''' nicht leerer <paramref name="copyrightText"/> ersetzt den Hinweis der Quelle - dieselbe
        ''' Regel wie in den Stapelformularen: leer heisst nicht anfassen.</summary>
        Friend Shared Function Write(targetPath As String, bitmap As SKBitmap,
                                     metadata As ExifBuilderService.ExifFieldSet,
                                     copyrightText As String) As Boolean
            If String.IsNullOrWhiteSpace(targetPath) OrElse bitmap Is Nothing Then Return False
            If bitmap.Width <= 0 OrElse bitmap.Height <= 0 Then Return False

            ' Erst in den Speicher: die Verzeichnisse werden hinten angehaengt und der Kopf danach
            ' umgebogen, und die Datei selbst entsteht wie alle anderen ueber die Nachbardatei.
            Using encoded As New MemoryStream()
                If Not EncodePixels(encoded, bitmap, metadata, copyrightText) Then Return False
                If metadata IsNot Nothing Then AppendDirectories(encoded, metadata)
                Return ImageProcessor.WriteFileAtomic(targetPath, Sub(fs) encoded.WriteTo(fs))
            End Using
        End Function

        Private Shared Function EncodePixels(output As MemoryStream, bitmap As SKBitmap,
                                             metadata As ExifBuilderService.ExifFieldSet,
                                             copyrightText As String) As Boolean
            Dim width = bitmap.Width
            Dim height = bitmap.Height

            Using pixmap = bitmap.PeekPixels()
                If pixmap Is Nothing Then Return False

                ' Nach RGBA ohne vormultipliziertes Alpha - so steht es im TIFF. Skia rechnet dabei
                ' selbst um, gleich in welcher Anordnung das Bitmap vorliegt.
                ' Ein tiefes Bild (ImageProcessorDeep.vb) geht mit 16 Bit je Kanal hinein: dieselbe
                ' Anordnung, zwei Bytes je Wert in der Bytefolge des Rechners, wie LibTiff sie
                ' fuer eine Datei erwartet, die es selbst anlegt.
                Dim deep = bitmap.ColorType = SKColorType.Rgba16161616
                Dim sampleBytes = If(deep, 2, 1)
                Dim hasAlpha = bitmap.AlphaType <> SKAlphaType.Opaque AndAlso
                               StripEncoding.HasTransparency(pixmap, deep)
                Dim samples = If(hasAlpha, 4, 3)
                Dim rowsPerStrip = StripRows(width, sampleBytes, samples)
                If rowsPerStrip = 0 Then Return False
                ' Die Puffer fassen nur so viele Zeilen, wie ein Streifen wirklich hat.
                Dim bufferRows = Math.Min(rowsPerStrip, height)

                Using tif = Tiff.ClientOpen("ferrumpix.tif", "w", output, New RetainingTiffStream())
                    If tif Is Nothing Then Return False

                    tif.SetField(TiffTag.IMAGEWIDTH, width)
                    tif.SetField(TiffTag.IMAGELENGTH, height)
                    tif.SetField(TiffTag.BITSPERSAMPLE, 8 * sampleBytes)
                    tif.SetField(TiffTag.SAMPLESPERPIXEL, samples)
                    tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB)
                    tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG)
                    ' Das Bild ist an dieser Stelle schon gedreht, siehe ExifBuilderService.
                    tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT)
                    tif.SetField(TiffTag.COMPRESSION, Compression.ADOBE_DEFLATE)
                    tif.SetField(TiffTag.PREDICTOR, Predictor.HORIZONTAL)
                    tif.SetField(TiffTag.ROWSPERSTRIP, rowsPerStrip)
                    tif.SetField(TiffTag.XRESOLUTION, 72.0)
                    tif.SetField(TiffTag.YRESOLUTION, 72.0)
                    tif.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH)
                    If hasAlpha Then
                        tif.SetField(TiffTag.EXTRASAMPLES, 1, New Short() {CShort(ExtraSample.UNASSALPHA)})
                    End If
                    ApplyImageDirectoryText(tif, metadata, copyrightText)

                    ' DIE STREIFEN PACKEN WIR SELBST, parallel, und LibTiff schreibt sie nur noch
                    ' in Reihenfolge weg (WriteRawStrip). Vorher packte LibTiff zeilenweise auf
                    ' einem Kern, und das war bei grossen Bildern der groesste Teil der Ausgabe.
                    ' Die Tags oben bleiben, wie sie waren: Deflate im zlib-Format, waagerechte
                    ' Vorhersage, 64 Zeilen je Streifen (weniger nur bei sehr breiten Bildern, siehe
                    ' StripRows). Siehe PackStrip.
                    Dim bufferBytes = bufferRows * width * 4 * sampleBytes
                    Dim outputs As New StripEncoding.PackedBuffers(bufferRows * CLng(width) * samples * sampleBytes)
                    Dim ok = StripEncoding.EncodeInOrder(Of Byte(), MemoryStream)(
                        StripEncoding.StripCount(height, rowsPerStrip),
                        Function() New Byte(bufferBytes - 1) {},
                        Function(buffer, strip) PackStrip(pixmap, strip, rowsPerStrip, deep, samples, buffer, outputs),
                        Function(strip, packed)
                            Dim written = tif.WriteRawStrip(strip, packed.GetBuffer(), CInt(packed.Length)) >= 0
                            outputs.Return(packed)
                            Return written
                        End Function)
                    If Not ok Then Return False
                    ' Das Verzeichnis schreibt LibTiff beim Schliessen selbst.
                End Using
            End Using
            Return output.Length > 8
        End Function

        ''' <summary>Ein Streifen, wie LibTiff ihn mit Deflate und waagerechter Vorhersage selbst
        ''' schriebe: die Zeilen als RGB oder RGBA hintereinander, in jeder Zeile jeder Wert ab dem
        ''' zweiten Bildpunkt als Abstand zum selben Kanal des Bildpunkts davor (modulo 256 bzw.
        ''' 65536, bei 16 Bit auf den 16-Bit-Werten in der Bytefolge des Rechners, also der Datei),
        ''' das Ganze im zlib-Format gepackt. Die erste Zeile eines Streifens braucht nichts von
        ''' aussen, jeder Streifen steht fuer sich. Gerechnet wird an Ort und Stelle in
        ''' <paramref name="buffer"/>, dem Arbeitspuffer des Kerns (Platz fuer einen Streifen RGBA);
        ''' gepackt wird in einen Puffer aus <paramref name="outputs"/>. Nothing, wenn Skia nicht
        ''' lesen kann.</summary>
        Private Shared Function PackStrip(pixmap As SKPixmap, strip As Integer, rowsPerStrip As Integer, deep As Boolean,
                                          samples As Integer, buffer As Byte(),
                                          outputs As StripEncoding.PackedBuffers) As MemoryStream
            Dim width = pixmap.Width
            Dim firstRow = strip * rowsPerStrip
            Dim rows = Math.Min(rowsPerStrip, pixmap.Height - firstRow)
            Dim sampleBytes = If(deep, 2, 1)
            Dim sourcePixelBytes = 4 * sampleBytes
            Dim rowBytes = width * samples * sampleBytes
            Dim rawLength = rows * rowBytes
            If Not StripEncoding.ReadRows(pixmap, firstRow, rows, deep, buffer) Then Return Nothing

            ' Die Werte jedes Bildpunkts; ohne Alpha bleibt der vierte weg. Nach vorn
            ' zusammengeschoben: das Ziel liegt nie hinter der Quelle.
            If samples = 3 Then
                Dim valueBytes = 3 * sampleBytes
                Dim target = 0
                For source = 0 To rows * width * sourcePixelBytes - 1 Step sourcePixelBytes
                    System.Buffer.BlockCopy(buffer, source, buffer, target, valueBytes)
                    target += valueBytes
                Next
            End If

            ' Vorhersage je Zeile, von rechts nach links, damit jeder Wert noch mit dem
            ' unveraenderten Nachbarn verrechnet wird.
            For row = 0 To rows - 1
                Dim start = row * rowBytes
                If deep Then
                    Dim distance = samples * 2
                    For i = start + rowBytes - 2 To start + distance Step -2
                        Dim value = CInt(buffer(i)) Or (CInt(buffer(i + 1)) << 8)
                        Dim before = CInt(buffer(i - distance)) Or (CInt(buffer(i - distance + 1)) << 8)
                        Dim difference = (value - before) And &HFFFF
                        buffer(i) = CByte(difference And &HFF)
                        buffer(i + 1) = CByte(difference >> 8)
                    Next
                Else
                    For i = start + rowBytes - 1 To start + samples Step -1
                        buffer(i) = CByte((CInt(buffer(i)) - CInt(buffer(i - samples))) And &HFF)
                    Next
                End If
            Next

            Dim packed = outputs.Rent()
            Using zlib As New System.IO.Compression.ZLibStream(packed, System.IO.Compression.CompressionLevel.Optimal,
                                                               leaveOpen:=True)
                zlib.Write(buffer, 0, rawLength)
            End Using
            Return packed
        End Function

        ''' <summary>Kamera, Programm, Urheber und Zeit gehoeren in das Verzeichnis des Bildes. Die
        ''' Ausrichtung aus den Feldern bleibt aussen vor, sie steht oben fest auf "normal".</summary>
        Private Shared Sub ApplyImageDirectoryText(tif As Tiff, metadata As ExifBuilderService.ExifFieldSet,
                                                   copyrightText As String)
            Dim copyright = CopyrightService.NormalizeText(copyrightText)
            If metadata?.Ifd0 IsNot Nothing Then
                For Each field In metadata.Ifd0
                    If field.FieldType <> GeotagService.TiffTypeAscii Then Continue For
                    If field.Tag = TagCopyright AndAlso copyright.Length > 0 Then Continue For
                    Dim text = System.Text.Encoding.UTF8.GetString(field.Data).TrimEnd(ChrW(0)).Trim()
                    If text.Length = 0 Then Continue For
                    tif.SetField(CType(field.Tag, TiffTag), Utf8ForLibTiff(text))
                Next
            End If
            If copyright.Length > 0 Then tif.SetField(TiffTag.COPYRIGHT, Utf8ForLibTiff(copyright))
        End Sub

        ''' <summary>LibTiff schreibt Textfelder als Latin-1: je Zeichen ein Byte. Damit in der
        ''' Datei UTF-8 steht wie in den anderen Formaten (siehe
        ''' <see cref="GeotagService.ExifTextBytes"/>), bekommt es die UTF-8-Bytes als Zeichen
        ''' gereicht. Reiner ASCII-Text bleibt dabei, wie er ist.</summary>
        Private Shared Function Utf8ForLibTiff(text As String) As String
            Return System.Text.Encoding.Latin1.GetString(GeotagService.ExifTextBytes(text))
        End Function

        ''' <summary>Haengt das Aufnahme-Verzeichnis und den Ort an die fertig kodierte Datei.
        '''
        ''' Die Werte der Felder sind in "II" kodiert (ExifBuilderService schreibt fest in dieser
        ''' Reihenfolge). LibTiff schreibt in der Reihenfolge des Rechners, und das ist auf allen
        ''' Plattformen der Anwendung "II". Kaeme doch einmal "MM" heraus, bleibt die Datei ohne
        ''' Anhang - falsch gelesene Zahlen waeren schlechter als fehlende.</summary>
        Private Shared Sub AppendDirectories(encoded As MemoryStream, metadata As ExifBuilderService.ExifFieldSet)
            Dim exifFields = If(metadata.Exif, New List(Of GeotagService.TiffField)()).
                             OrderBy(Function(f) f.Tag).ToList()
            If exifFields.Count = 0 AndAlso Not metadata.HasGps Then Return
            ' Klassisches TIFF adressiert mit 32 Bit. Ein Speicherstrom reicht ohnehin nicht weiter.
            If encoded.Length < 8 OrElse encoded.Length > Integer.MaxValue - 65536 Then Return

            Dim bytes = encoded.GetBuffer()
            Dim length = CInt(encoded.Length)
            Dim littleEndian = bytes(0) = AscW("I"c) AndAlso bytes(1) = AscW("I"c)
            If Not littleEndian Then
                DiagnosticLogService.LogAlways("TiffWriter", "Byte-Reihenfolge MM - Aufnahmedaten nicht angehaengt")
                Return
            End If
            If GeotagService.ReadUInt16(bytes, 2, littleEndian) <> 42 Then Return

            Dim ifd0 = CLng(GeotagService.ReadUInt32(bytes, 4, littleEndian))
            If ifd0 < 8 OrElse ifd0 + 2 > length Then Return
            Dim entryCount = GeotagService.ReadUInt16(bytes, CInt(ifd0), littleEndian)
            If entryCount > GeotagService.MaxIfdEntries Then Return
            Dim entriesEnd = CInt(ifd0) + 2 + entryCount * 12
            If entriesEnd + 4 > length Then Return

            ' Eintraege und Folgeverweis von IFD0 VOR dem Anhaengen kopieren: GetBuffer liefert den
            ' inneren Puffer, und der wird ausgetauscht, sobald der Strom waechst.
            Dim entries As New List(Of Byte())()
            For i = 0 To entryCount - 1
                Dim entry(11) As Byte
                Array.Copy(bytes, CInt(ifd0) + 2 + i * 12, entry, 0, 12)
                Dim tag = GeotagService.ReadUInt16(entry, 0, littleEndian)
                If tag = TagExifIfdPointer OrElse tag = TagGpsIfdPointer Then Continue For
                entries.Add(entry)
            Next
            Dim nextIfd(3) As Byte
            Array.Copy(bytes, entriesEnd, nextIfd, 0, 4)

            encoded.Position = encoded.Length
            PadToEven(encoded)

            If exifFields.Count > 0 Then
                Dim exifOffset = CInt(encoded.Length)
                WriteBlock(encoded, GeotagService.BuildIfd(exifFields, exifOffset, littleEndian))
                entries.Add(PointerEntry(TagExifIfdPointer, exifOffset, littleEndian))
            End If
            If metadata.HasGps Then
                Dim gpsOffset = CInt(encoded.Length)
                Dim gpsFields = GeotagService.BuildGpsFields(metadata.Latitude, metadata.Longitude,
                                                             metadata.Altitude, littleEndian)
                WriteBlock(encoded, GeotagService.BuildIfd(gpsFields, gpsOffset, littleEndian))
                entries.Add(PointerEntry(TagGpsIfdPointer, gpsOffset, littleEndian))
            End If

            ' Die Eintraege eines IFD stehen nach Tag-Nummer aufsteigend, sonst hoeren manche Leser
            ' nach dem ersten Rueckschritt auf.
            entries = entries.OrderBy(Function(e) GeotagService.ReadUInt16(e, 0, littleEndian)).ToList()

            Dim newIfd0Offset = CInt(encoded.Length)
            Dim directory As New List(Of Byte)(2 + entries.Count * 12 + 4)
            GeotagService.AppendUInt16(directory, entries.Count, littleEndian)
            For Each entry In entries
                directory.AddRange(entry)
            Next
            ' Der Folgeverweis wird woertlich uebernommen, er zeigt in den unveraenderten Bereich.
            directory.AddRange(nextIfd)
            WriteBlock(encoded, directory.ToArray())

            Dim header(3) As Byte
            GeotagService.WriteUInt32(header, 0, CUInt(newIfd0Offset), littleEndian)
            encoded.Position = 4
            encoded.Write(header, 0, 4)
            encoded.Position = encoded.Length
        End Sub

        Private Shared Function PointerEntry(tag As Integer, offset As Integer, littleEndian As Boolean) As Byte()
            Dim entry(11) As Byte
            GeotagService.WriteUInt16(entry, 0, tag, littleEndian)
            GeotagService.WriteUInt16(entry, 2, GeotagService.TiffTypeLong, littleEndian)
            GeotagService.WriteUInt32(entry, 4, 1UI, littleEndian)
            GeotagService.WriteUInt32(entry, 8, CUInt(offset), littleEndian)
            Return entry
        End Function

        Private Shared Sub WriteBlock(stream As MemoryStream, block As Byte())
            stream.Write(block, 0, block.Length)
            PadToEven(stream)
        End Sub

        ''' TIFF verlangt Wortausrichtung fuer jedes Verzeichnis.
        Private Shared Sub PadToEven(stream As MemoryStream)
            If stream.Length Mod 2 <> 0 Then stream.WriteByte(0)
        End Sub

        ''' <summary>Wie der Standardstrom, nur ohne das Schliessen. LibTiff schliesst beim Beenden
        ''' auch den Strom, und danach waere der Speicher weg, bevor die Verzeichnisse angehaengt
        ''' und die Datei geschrieben sind.</summary>
        Private NotInheritable Class RetainingTiffStream
            Inherits TiffStream

            Public Overrides Sub Close(clientData As Object)
            End Sub
        End Class

    End Class

End Namespace
