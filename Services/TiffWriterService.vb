Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports BitMiracle.LibTiff.Classic
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Schreibt TIFF mit 8 Bit je Kanal. Nur als NEUE Datei: Speichern unter und die Stapelwege mit
    ''' Zielordner, nie ueber ein TIFF-Original (siehe <see cref="ImageProcessor.CanEncodeAsNewFile"/>).
    '''
    ''' WARUM 8 BIT. Die Pipeline rechnet hinter dem Decode mit 8 Bit ("Kein 16-Bit-Arbeitsbild" in
    ''' FALLEN_UND_ENTSCHEIDUNGEN.md). Ein 16-Bit-TIFF truege nur 8 Bit Inhalt, hochskaliert, und
    ''' verspraeche damit etwas, das die Datei nicht haelt.
    '''
    ''' DIE BILDPUNKTE schreibt LibTiff.NET, verlustfrei mit Deflate und waagerechter Vorhersage. Ein
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
        Friend Shared Function CanReplaceExisting(path As String) As Boolean
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return True
            Try
                Using tif = Tiff.Open(path, "r")
                    If tif Is Nothing Then Return False
                    If tif.NumberOfDirectories() <> 1 Then Return False
                    Dim bits = tif.GetField(TiffTag.BITSPERSAMPLE)
                    If bits IsNot Nothing AndAlso bits.Length > 0 AndAlso bits(0).ToInt() > 8 Then Return False
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

        ''' Zeilen je Streifen. Klein genug, dass ein Leser nicht das ganze Bild auf einmal
        ''' entpacken muss, gross genug, dass die Streifentabelle kurz bleibt.
        Private Const RowsPerStrip As Integer = 64

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

                ' Zeilenweise nach RGBA ohne vormultipliziertes Alpha - so steht es im TIFF. Skia
                ' rechnet dabei selbst um, gleich in welcher Anordnung das Bitmap vorliegt.
                Dim rowInfo = New SKImageInfo(width, 1, SKColorType.Rgba8888, SKAlphaType.Unpremul)
                Dim rgba(width * 4 - 1) As Byte
                Dim handle = GCHandle.Alloc(rgba, GCHandleType.Pinned)
                Try
                    Dim pointer = handle.AddrOfPinnedObject()
                    Dim hasAlpha = bitmap.AlphaType <> SKAlphaType.Opaque AndAlso
                                   HasTransparency(pixmap, height, rowInfo, rgba, pointer)
                    Dim samples = If(hasAlpha, 4, 3)
                    Dim row(width * samples - 1) As Byte

                    Using tif = Tiff.ClientOpen("ferrumpix.tif", "w", output, New RetainingTiffStream())
                        If tif Is Nothing Then Return False

                        tif.SetField(TiffTag.IMAGEWIDTH, width)
                        tif.SetField(TiffTag.IMAGELENGTH, height)
                        tif.SetField(TiffTag.BITSPERSAMPLE, 8)
                        tif.SetField(TiffTag.SAMPLESPERPIXEL, samples)
                        tif.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB)
                        tif.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG)
                        ' Das Bild ist an dieser Stelle schon gedreht, siehe ExifBuilderService.
                        tif.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT)
                        tif.SetField(TiffTag.COMPRESSION, Compression.ADOBE_DEFLATE)
                        tif.SetField(TiffTag.PREDICTOR, Predictor.HORIZONTAL)
                        tif.SetField(TiffTag.ROWSPERSTRIP, RowsPerStrip)
                        tif.SetField(TiffTag.XRESOLUTION, 72.0)
                        tif.SetField(TiffTag.YRESOLUTION, 72.0)
                        tif.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH)
                        If hasAlpha Then
                            tif.SetField(TiffTag.EXTRASAMPLES, 1, New Short() {CShort(ExtraSample.UNASSALPHA)})
                        End If
                        ApplyImageDirectoryText(tif, metadata, copyrightText)

                        For y = 0 To height - 1
                            If Not pixmap.ReadPixels(rowInfo, pointer, rgba.Length, 0, y) Then Return False
                            If hasAlpha Then
                                Array.Copy(rgba, row, rgba.Length)
                            Else
                                Dim target = 0
                                For source = 0 To rgba.Length - 1 Step 4
                                    row(target) = rgba(source)
                                    row(target + 1) = rgba(source + 1)
                                    row(target + 2) = rgba(source + 2)
                                    target += 3
                                Next
                            End If
                            If Not tif.WriteScanline(row, y) Then Return False
                        Next
                        ' Das Verzeichnis schreibt LibTiff beim Schliessen selbst.
                    End Using
                Finally
                    handle.Free()
                End Try
            End Using
            Return output.Length > 8
        End Function

        ''' <summary>Traegt irgendein Bildpunkt Transparenz? Zeilenweise und mit fruehem Ausstieg,
        ''' ein deckendes Bild kostet also genau einen Lesedurchgang.</summary>
        Private Shared Function HasTransparency(pixmap As SKPixmap, height As Integer, rowInfo As SKImageInfo,
                                                rgba As Byte(), pointer As IntPtr) As Boolean
            For y = 0 To height - 1
                ' Im Zweifel MIT Alpha: ein ueberfluessiger Kanal kostet Platz, ein fehlender das Bild.
                If Not pixmap.ReadPixels(rowInfo, pointer, rgba.Length, 0, y) Then Return True
                For i = 3 To rgba.Length - 1 Step 4
                    If rgba(i) <> 255 Then Return True
                Next
            Next
            Return False
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
                    Dim text = System.Text.Encoding.ASCII.GetString(field.Data).TrimEnd(ChrW(0)).Trim()
                    If text.Length = 0 Then Continue For
                    tif.SetField(CType(field.Tag, TiffTag), text)
                Next
            End If
            If copyright.Length > 0 Then tif.SetField(TiffTag.COPYRIGHT, copyright)
        End Sub

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
