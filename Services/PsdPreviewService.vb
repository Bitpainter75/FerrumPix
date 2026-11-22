Imports System
Imports System.Collections.Generic
Imports System.IO
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Liest Photoshop-Dateien (.psd/.psb) NUR-LESEND, da SkiaSharps Codec das Format nicht kennt.
    ''' Liefert wie IcoPreviewService/RawPreviewService einen dekodierbaren MemoryStream (PNG), den
    ''' die bestehenden Thumbnail-, Vorschau- und Editor-Pfade wie ein normales Bild weiterverarbeiten.
    ''' Zurückgeschrieben wird eine .psd nie (Speichern-in-place ist wie bei RAW gesperrt).
    '''
    ''' Gelesen wird das FERTIG ZUSAMMENGESETZTE Gesamtbild der Bilddaten-Sektion, das Photoshop bei
    ''' "Maximale Kompatibilität" (Standard) ablegt - Ebenen-Blending muss so nicht nachgebaut werden.
    ''' Aufbau: Header (8BPS, Version 1=PSD/2=PSB, Kanäle, Maße, Bittiefe, Farbmodus) → Farbmodus-Daten
    ''' überspringen → Bildressourcen überspringen → Ebenen-Sektion überspringen → Composite dekodieren
    ''' (unkomprimiert, RLE/PackBits oder ZIP, kanalweise planar). Abgedeckt: 8/16 Bit, RGB(+Alpha),
    ''' Graustufen(+Alpha), CMYK (naiv nach RGB, Photoshop speichert CMYK invertiert), indiziert,
    ''' Lab (nach sRGB gerechnet, siehe ComposeLab) und 32 Bit Gleitkomma in RGB und Graustufen
    ''' (mit derselben Abbildung wie die Ebenen). Fehlt das Composite (Kompatibilität beim
    ''' Speichern deaktiviert), ist es zu groß oder der Modus exotisch (Bitmap, Duplex,
    ''' Mehrkanal), fällt der Leser auf das eingebettete JPEG-Thumbnail (Bildressource 1036)
    ''' zurück; fehlt auch das, gibt es Nothing.
    ''' </summary>
    Public NotInheritable Class PsdPreviewService

        Private Sub New()
        End Sub

        Private Const HeaderSize As Integer = 26
        ''' Format-Limit: PSD erlaubt 30000 px je Seite, PSB 300000. Zusätzlich ein Flächen-Deckel,
        ''' damit eine beschädigte Längenangabe nicht Gigabyte allokiert (BGRA-Puffer muss < 2 GB sein).
        Private Const MaxPixels As Long = 250_000_000L

        ''' <summary>Deckel für den geschätzten Spitzenverbrauch des Composite-Lesers.
        ''' 1,5 GB lässt ein 11000x11000-Bild (121 MP, 4 Kanäle) noch voll durch und fängt darüber
        ''' auf das eingebettete Thumbnail ab. Ohne diesen Deckel konnte eine einzelne große PSB
        ''' rund 3 GB gleichzeitig belegen - der reine Flächendeckel oben schützt nur davor, dass ein
        ''' einzelnes Array das .NET-Limit reißt, nicht vor der Summe der drei Puffer.</summary>
        Private Const MaxCompositeBytes As Long = 1_500_000_000L

        Private Enum PsdColorMode
            Grayscale = 1
            Indexed = 2
            Rgb = 3
            Cmyk = 4
            Lab = 9
        End Enum

        Private Structure PsdHeader
            Public IsPsb As Boolean
            Public Channels As Integer
            Public Width As Integer
            Public Height As Integer
            Public Depth As Integer
            Public ColorMode As Integer
        End Structure

        Public Shared Function IsSupportedPsd(filePath As String) As Boolean
            Dim ext = Path.GetExtension(filePath)
            Return String.Equals(ext, ".psd", StringComparison.OrdinalIgnoreCase) OrElse
                   String.Equals(ext, ".psb", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Maße aus dem Dateikopf, ohne die Bilddaten zu lesen. (0, 0), wenn der Kopf nicht
        ''' lesbar ist oder die Datei über der Grenze des Composite-Lesers liegt.</summary>
        Public Shared Function TryGetSize(filePath As String) As (Width As Integer, Height As Integer)
            Try
                Using fs = File.OpenRead(filePath)
                    Dim header As PsdHeader
                    If Not TryReadHeader(fs, header) Then Return (0, 0)
                    Return (header.Width, header.Height)
                End Using
            Catch
                Return (0, 0)
            End Try
        End Function

        ''' <summary>Die Daten einer Bildressource aus der Datei, ohne die Bilddaten zu lesen, oder
        ''' Nothing. Fuer den Export, der Aufloesung (1005) und XMP (1060) einer PSD-Quelle
        ''' weiterreicht.</summary>
        Friend Shared Function ReadResourceFromFile(filePath As String, id As Integer) As Byte()
            Try
                Using fs = File.OpenRead(filePath)
                    Dim header As PsdHeader
                    If Not TryReadHeader(fs, header) Then Return Nothing
                    If Not SkipBlock(fs, CLng(ReadU32(fs))) Then Return Nothing
                    Return FindResource(ReadResourceSection(fs, ReadU32(fs)), id)
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' Liefert einen MemoryStream mit PNG-Daten (Position 0) oder Nothing bei Fehler.
        Public Shared Function ExtractPreview(filePath As String) As MemoryStream
            ' Das Composite mit Alpha liegt bewusst als Unpremul vor; der Kodierer geht deshalb
            ' ueber das Pixmap (siehe PngEncoder).
            Using bitmap = TryDecode(filePath)
                Return PngEncoder.EncodeToStream(bitmap, PngPurpose.Transient)
            End Using
        End Function

        ''' <summary>Das Gesamtbild als Bgra8888 mit geradlinigem Alpha (Besitz beim Aufrufer), oder
        ''' Nothing. Fehlt die Bilddaten-Sektion oder ist sie zu gross, das eingebettete
        ''' Vorschaubild.</summary>
        Public Shared Function TryDecode(filePath As String) As SKBitmap
            Try
                Dim bitmap As SKBitmap = Nothing
                Using fs = File.OpenRead(filePath)
                    bitmap = DecodeComposite(fs)
                End Using
                If bitmap Is Nothing Then
                    ' Getrennter Durchlauf mit frischem Stream: der Composite-Versuch kann den
                    ' Lesezeiger an beliebiger Stelle zurückgelassen haben.
                    Using fs = File.OpenRead(filePath)
                        bitmap = DecodeThumbnailResource(fs)
                    End Using
                End If
                Return bitmap
            Catch
                Return Nothing
            End Try
        End Function

        ' ── Composite (Bilddaten-Sektion) ────────────────────────────────────────

        ''' <summary>Trägt die Datei ein Gesamtbild, das dieser Leser in VOLLER Auflösung liefert?
        ''' Nein heißt: <see cref="TryDecode"/> käme nur mit dem eingebetteten Vorschaubild zurück
        ''' oder mit gar nichts. Der Editor fragt das, bevor er „Als Gesamtbild" anbietet; sonst
        ''' öffnete diese Wahl eine PSD ohne „Kompatibilität maximieren" in Vorschaugröße.
        ''' Das Gesamtbild wird probeweise dekodiert, damit auch beschädigte oder fehlende
        ''' Bilddaten nicht als verfügbare Alternative gelten.</summary>
        Public Shared Function HasFullComposite(filePath As String) As Boolean
            Try
                Using fs = File.OpenRead(filePath)
                    Using bitmap = DecodeComposite(fs)
                        Return bitmap IsNot Nothing
                    End Using
                End Using
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Nur Bittiefen/Modi, die dieser Leser wirklich beherrscht - alles andere geht in
        ''' den Thumbnail-Fallback statt in ein falsch eingefärbtes Bild.</summary>
        Private Shared Function CompositeModeSupported(header As PsdHeader) As Boolean
            ' 32 Bit ist Gleitkomma und gibt es nur in RGB und Graustufen.
            If header.Depth = 32 Then
                Return header.ColorMode = PsdColorMode.Rgb OrElse header.ColorMode = PsdColorMode.Grayscale
            End If
            If header.Depth <> 8 AndAlso header.Depth <> 16 Then Return False
            If header.ColorMode <> PsdColorMode.Grayscale AndAlso
               header.ColorMode <> PsdColorMode.Rgb AndAlso
               header.ColorMode <> PsdColorMode.Cmyk AndAlso
               header.ColorMode <> PsdColorMode.Indexed AndAlso
               header.ColorMode <> PsdColorMode.Lab Then Return False
            ' Indizierte Farbe gibt es nur mit acht Bit: ein Byte je Punkt ist der Index.
            Return header.ColorMode <> PsdColorMode.Indexed OrElse header.Depth = 8
        End Function

        ''' <summary>Ressource 1057: Version (vier Byte), dann ein Byte "echtes Gesamtbild". Steht
        ''' dort 0, war beim Speichern "Kompatibilität maximieren" aus, und die Bilddaten sind nur
        ''' eine weiße Fläche.</summary>
        Private Shared Function IsPlaceholderComposite(resources As Byte()) As Boolean
            Dim versionInfo = FindResource(resources, 1057)
            Return versionInfo IsNot Nothing AndAlso versionInfo.Length >= 5 AndAlso versionInfo(4) = 0
        End Function

        ''' <summary>Nennt Ressource 1057 Photoshop als schreibendes Programm? Aufbau: Version (vier
        ''' Byte), ein Byte "echtes Gesamtbild", dann der Name des Schreibers als Länge in Zeichen
        ''' (vier Byte) und UTF-16 Big-Endian. Photoshop trägt dort seinen Namen mit Herstellervorsatz
        ''' ein, gesucht wird deshalb nur das Wort selbst.</summary>
        Private Shared Function WrittenByPhotoshop(resources As Byte()) As Boolean
            Dim versionInfo = FindResource(resources, 1057)
            If versionInfo Is Nothing OrElse versionInfo.Length < 9 Then Return False
            Dim length = (CLng(versionInfo(5)) << 24) Or (CLng(versionInfo(6)) << 16) Or
                         (CLng(versionInfo(7)) << 8) Or CLng(versionInfo(8))
            If length <= 0 OrElse 9 + length * 2 > versionInfo.Length Then Return False
            Dim sb As New Text.StringBuilder()
            For i = 0 To CInt(length) - 1
                sb.Append(ChrW(CInt(versionInfo(9 + i * 2)) * 256 + versionInfo(9 + i * 2 + 1)))
            Next
            Return sb.ToString().Contains("Photoshop", StringComparison.Ordinal)
        End Function

        Private Shared Function DecodeComposite(fs As FileStream) As SKBitmap
            Dim header As PsdHeader
            If Not TryReadHeader(fs, header) Then Return Nothing
            If Not CompositeModeSupported(header) Then Return Nothing

            ' Farbmodus-Daten: bei indizierter Farbe die Farbtabelle (768 Byte, erst die 256 Rot-,
            ' dann die Grün-, dann die Blauwerte), sonst überspringen. Die Bildressourcen werden
            ' GELESEN, weil dort das Farbprofil liegt (1039) und bei indizierter Farbe die
            ' durchsichtige Farbe (1047). Danach die Ebenen-Sektion überspringen: ein indiziertes
            ' Dokument hat ohnehin keine Ebenen.
            Dim colorDataLen = CLng(ReadU32(fs))
            Dim palette As Byte() = Nothing
            If header.ColorMode = PsdColorMode.Indexed Then
                If colorDataLen < 768 Then Return Nothing
                palette = New Byte(767) {}
                If Not ReadExactly(fs, palette, 768) Then Return Nothing
                If Not SkipBlock(fs, colorDataLen - 768) Then Return Nothing
            ElseIf Not SkipBlock(fs, colorDataLen) Then
                Return Nothing
            End If
            Dim resources = ReadResourceSection(fs, ReadU32(fs))
            ' Ressource 1047: zwei Byte, der Index der Farbe, die durchsichtig ist. Photoshop schreibt
            ' sie, wenn ein indiziertes Bild Transparenz hat; ohne sie kaeme es voll deckend an.
            Dim transparentIndex = -1
            If header.ColorMode = PsdColorMode.Indexed Then
                Dim tIndex = FindResource(resources, 1047)
                If tIndex IsNot Nothing AndAlso tIndex.Length >= 2 Then
                    Dim value = CInt(tIndex(0)) * 256 + tIndex(1)
                    If value >= 0 AndAlso value <= 255 Then transparentIndex = value
                End If
            End If
            ' Nur ein weißer Platzhalter: dann lieber das eingebettete Vorschaubild als ein weißes
            ' Rechteck in Galerie und Betrachter.
            If IsPlaceholderComposite(resources) Then Return Nothing

            Dim layerLen = If(header.IsPsb, ReadU64(fs), CLng(ReadU32(fs)))
            If layerLen < 0 OrElse fs.Position + layerLen > fs.Length Then Return Nothing
            Dim layerSectionEnd = fs.Position + layerLen
            Dim colorChannels = If(header.ColorMode = PsdColorMode.Rgb OrElse header.ColorMode = PsdColorMode.Lab, 3,
                                   If(header.ColorMode = PsdColorMode.Grayscale, 1, 0))
            Dim mergedAlpha = colorChannels > 0 AndAlso header.Channels > colorChannels AndAlso
                              MergedImageHasAlpha(fs, header, layerSectionEnd, resources, header.Channels - colorChannels)
            fs.Position = layerSectionEnd
            Dim iccProfile = CreateIccOrNothing(FindResource(resources, 1039))

            Dim compression = ReadU16(fs)
            ' 0 roh, 1 RLE, 2 und 3 ZIP. Die beiden ZIP-Spielarten sind hier seltener als bei den
            ' Ebenen, aber ohne sie bleibt eine Datei mit gültigem Gesamtbild verschlossen und landet
            ' im Thumbnail-Rückfall - oder, wenn keins eingebettet ist, gar nicht.
            If compression < 0 OrElse compression > 3 Then Return Nothing

            ' Wie viele der planar abgelegten Kanäle das Ergebnis wirklich braucht: RGB 3 (+Alpha),
            ' Graustufen 1 (+Alpha), CMYK 4. Weitere Kanäle (gespeicherte Auswahlen) werden gelesen
            ' und verworfen, damit die Planar-Reihenfolge nicht verrutscht. Ob der erste Zusatzkanal
            ' Transparenz ist oder nur eine gespeicherte Auswahl, sagt MergedImageHasAlpha.
            Dim neededChannels As Integer
            Dim hasAlpha As Boolean
            Select Case header.ColorMode
                Case PsdColorMode.Rgb, PsdColorMode.Lab
                    If header.Channels < 3 Then Return Nothing
                    hasAlpha = mergedAlpha
                    neededChannels = If(hasAlpha, 4, 3)
                Case PsdColorMode.Grayscale
                    If header.Channels < 1 Then Return Nothing
                    hasAlpha = mergedAlpha
                    neededChannels = If(hasAlpha, 2, 1)
                Case PsdColorMode.Indexed
                    ' Ein Kanal mit dem Index. Durchsichtig ist hoechstens die eine Farbe aus
                    ' Ressource 1047; gibt es sie, bekommt das Bild einen Alphakanal.
                    If header.Channels < 1 Then Return Nothing
                    hasAlpha = transparentIndex >= 0
                    neededChannels = 1
                Case Else ' CMYK
                    If header.Channels < 4 Then Return Nothing
                    hasAlpha = False
                    neededChannels = 4
            End Select

            Dim bytesPerSample = header.Depth \ 8
            Dim rowBytes = header.Width * bytesPerSample
            Dim planeSize = header.Width * header.Height

            ' 32 Bit: jede Zeile geht beim Lesen gleich durch dieselbe Abbildung wie die Ebenen
            ' (PsdLayerReader.FloatSampleToByte), die Gleitkommawerte liegen also nie als Ganzes im
            ' Speicher, und der Bedarf ist der eines 8-Bit-Bilds. Nur mit Transparenz in RGB nicht:
            ' dann muss die weiße Hinterlegung herausgerechnet werden, bevor die Kennlinie greift,
            ' und dafür bleibt die Farbe bis zum Transparenzkanal linear in 16 Bit stehen.
            ' Lab in 16 Bit bleibt ebenfalls in 16 Bit stehen: a und b haben ihre Null bei 32768,
            ' und mit dem oberen Byte allein lag das Ergebnis doppelt so weit neben dem RGB-Bild
            ' derselben Datei wie bei acht Bit (gemessen am Prüfsatz aus Photoshop-Dateien).
            Dim wide As UShort()() = Nothing
            If (header.Depth = 32 AndAlso hasAlpha AndAlso header.ColorMode = PsdColorMode.Rgb) OrElse
               (header.Depth = 16 AndAlso header.ColorMode = PsdColorMode.Lab) Then
                If EstimatedPeakBytes(header) + CLng(planeSize) * 6 > MaxCompositeBytes Then Return Nothing
                wide = New UShort(2)() {}
            End If

            ' RLE: vorab die Zeilenlängen ALLER Kanäle (auch der verworfenen), sonst lässt sich ein
            ' unerwünschter Kanal nicht überspringen.
            Dim rleRowLengths As Integer() = Nothing
            If compression = 1 Then
                rleRowLengths = New Integer(header.Channels * header.Height - 1) {}
                For i = 0 To rleRowLengths.Length - 1
                    rleRowLengths(i) = If(header.IsPsb, CInt(ReadU32(fs)), ReadU16(fs))
                    If rleRowLengths(i) < 0 OrElse rleRowLengths(i) > rowBytes * 2 + 64 Then Return Nothing
                Next
            End If

            Dim planes(neededChannels - 1)() As Byte
            Dim rowBuffer(rowBytes - 1) As Byte
            Dim packed(0) As Byte

            ' Bei ZIP liegt EIN Strom über allen Kanälen, ohne Längen davor - er reicht bis zum
            ' Dateiende, und die Bilddaten sind der letzte Abschnitt. Gelesen wird zeilenweise wie
            ' im unkomprimierten Fall, nur eben aus dem Entpacker. Springen lässt der sich nicht,
            ' deshalb werden auch die verworfenen Kanäle durchgelesen statt übersprungen - Bytes,
            ' die niemand braucht, aber die Reihenfolge muss stimmen.
            Dim zip As Stream = Nothing
            Try
                If compression = 2 OrElse compression = 3 Then
                    zip = OpenInflate(fs)
                    If zip Is Nothing Then Return Nothing
                End If

                For channel = 0 To header.Channels - 1
                    Dim keep = channel < neededChannels
                    Dim colorChannel = channel < colorChannels
                    If keep Then
                        If wide IsNot Nothing AndAlso colorChannel Then
                            wide(channel) = New UShort(planeSize - 1) {}
                        Else
                            planes(channel) = New Byte(planeSize - 1) {}
                        End If
                    End If

                    If zip IsNot Nothing Then
                        For row = 0 To header.Height - 1
                            If Not ReadExactly(zip, rowBuffer, rowBytes) Then Return Nothing
                            If compression = 3 Then
                                ' Bei Gleitkomma ist die Vorhersage eine andere: Byte-Ebenen je
                                ' Zeile, wie beim Gleitkomma-Prädiktor von TIFF.
                                If bytesPerSample = 4 Then
                                    PsdLayerReader.UndoFloatPrediction(rowBuffer, 0, header.Width)
                                Else
                                    UndoPrediction(rowBuffer, 0, header.Width, bytesPerSample)
                                End If
                            End If
                            If keep Then StoreCompositeRow(planes, wide, channel, row, header.Width, rowBuffer, bytesPerSample, colorChannel)
                        Next
                    ElseIf compression = 0 Then
                        If Not keep Then
                            If Not SkipBlock(fs, CLng(rowBytes) * header.Height) Then Return Nothing
                            Continue For
                        End If
                        For row = 0 To header.Height - 1
                            If Not ReadExactly(fs, rowBuffer, rowBytes) Then Return Nothing
                            StoreCompositeRow(planes, wide, channel, row, header.Width, rowBuffer, bytesPerSample, colorChannel)
                        Next
                    Else
                        For row = 0 To header.Height - 1
                            Dim packedLen = rleRowLengths(channel * header.Height + row)
                            If Not keep Then
                                If Not SkipBlock(fs, packedLen) Then Return Nothing
                                Continue For
                            End If
                            If packed.Length < packedLen Then ReDim packed(packedLen - 1)
                            If Not ReadExactly(fs, packed, packedLen) Then Return Nothing
                            If Not UnpackBits(packed, packedLen, rowBuffer, rowBytes) Then Return Nothing
                            StoreCompositeRow(planes, wide, channel, row, header.Width, rowBuffer, bytesPerSample, colorChannel)
                        Next
                    End If
                Next
            Finally
                zip?.Dispose()
            End Try

            If wide IsNot Nothing AndAlso header.Depth = 32 Then UnmatteLinear(wide, planes, planeSize)

            Dim composed As SKBitmap
            If header.ColorMode = PsdColorMode.Lab Then
                composed = ComposeLab(header, planes, wide, hasAlpha)
            Else
                ' Bei 32 Bit ist die Hinterlegung schon linear herausgerechnet (UnmatteLinear).
                composed = ComposePlanes(header, planes, hasAlpha, palette, transparentIndex,
                                         unmatteRgb:=header.Depth <> 32)
            End If
            If composed Is Nothing Then Return Nothing

            ' Lab ist nach der Wandlung schon sRGB; ein Profil der Datei beschreibt Lab und passte
            ' nicht auf das Ergebnis.
            If header.ColorMode = PsdColorMode.Lab Then
                iccProfile?.Dispose()
                Return composed
            End If

            ' Photoshop legt seine Bilder oft in Adobe RGB ab. Ohne diese Wandlung kaeme das
            ' Gesamtbild als sRGB an - der Weg nach draussen fuehrt wie bei TIFF ueber einen
            ' PNG-Strom, und ein Profil ginge dabei verloren, bevor es jemand sehen koennte.
            ' CMYK bleibt aussen vor: der Weg dorthin ist in ComposePlanes ohnehin eine grobe
            ' Naeherung, und ein RGB-Profil passt nicht auf vier Kanaele.
            If header.ColorMode = PsdColorMode.Cmyk Then
                iccProfile?.Dispose()
                Return composed
            End If
            ' Das Profil ist ein eigens erzeugtes natives Objekt und wird nach der Wandlung wieder
            ' freigegeben - sonst haengt es bis zum naechsten Aufraeumlauf am Speicher.
            Using iccProfile
                Dim managed = ColorManagementService.ToSrgb(composed, iccProfile)
                If Not Object.ReferenceEquals(managed, composed) Then composed.Dispose()
                Return managed
            End Using
        End Function

        ''' <summary>Öffnet den Entpacker über der aktuellen Stelle der Datei. Der Aufrufer behält
        ''' die Datei; geschlossen wird nur der Entpacker.</summary>
        Private Shared Function OpenInflate(fs As FileStream) As Stream
            Try
                Dim start = fs.Position
                Dim head(1) As Byte
                If Not ReadExactly(fs, head, 2) Then Return Nothing
                fs.Seek(start, SeekOrigin.Begin)
                If LooksLikeZlib(head(0), head(1)) Then
                    Return New Compression.ZLibStream(fs, Compression.CompressionMode.Decompress, leaveOpen:=True)
                End If
                Return New Compression.DeflateStream(fs, Compression.CompressionMode.Decompress, leaveOpen:=True)
            Catch
                Return Nothing
            End Try
        End Function

        ''' Übernimmt eine dekomprimierte Zeile in die 8-Bit-Ebene; bei 16 Bit zählt das High-Byte
        ''' (Big-Endian, erstes Byte jedes Paars).
        Private Shared Sub StoreRow(plane As Byte(), row As Integer, width As Integer, rowBuffer As Byte(), bytesPerSample As Integer)
            Dim dst = row * width
            If bytesPerSample = 1 Then
                Array.Copy(rowBuffer, 0, plane, dst, width)
            Else
                For x = 0 To width - 1
                    plane(dst + x) = rowBuffer(x * 2)
                Next
            End If
        End Sub

        ''' <summary>Legt eine entpackte Zeile des Gesamtbilds ab. Bei 8 und 16 Bit wie
        ''' <see cref="StoreRow"/>; bei 32 Bit stehen Gleitkommazahlen, oberes Byte zuerst, und jede
        ''' geht durch <see cref="PsdLayerReader.FloatSampleToByte"/>, dieselbe Abbildung wie bei
        ''' den Ebenen. Ist <paramref name="wide"/> für diesen Kanal belegt, bleibt der Wert in 16 Bit
        ''' stehen: bei 32 Bit linear (siehe <see cref="UnmatteLinear"/>), bei 16 Bit unverändert
        ''' (Lab, siehe <see cref="ComposeLab"/>).</summary>
        Private Shared Sub StoreCompositeRow(planes As Byte()(), wide As UShort()(), channel As Integer,
                                             row As Integer, width As Integer, rowBuffer As Byte(),
                                             bytesPerSample As Integer, colorChannel As Boolean)
            Dim dst = row * width
            Dim target = If(wide IsNot Nothing AndAlso colorChannel, wide(channel), Nothing)
            If bytesPerSample = 2 AndAlso target IsNot Nothing Then
                For x = 0 To width - 1
                    target(dst + x) = CUShort((CInt(rowBuffer(x * 2)) << 8) Or CInt(rowBuffer(x * 2 + 1)))
                Next
                Return
            End If
            If bytesPerSample <> 4 Then
                StoreRow(planes(channel), row, width, rowBuffer, bytesPerSample)
                Return
            End If
            Dim plane = planes(channel)
            For x = 0 To width - 1
                Dim o = x * 4
                Dim bits = (CInt(rowBuffer(o)) << 24) Or (CInt(rowBuffer(o + 1)) << 16) Or
                           (CInt(rowBuffer(o + 2)) << 8) Or CInt(rowBuffer(o + 3))
                Dim value = BitConverter.Int32BitsToSingle(bits)
                If target IsNot Nothing Then
                    target(dst + x) = If(Single.IsNaN(value) OrElse value <= 0.0F, 0US,
                                         If(value >= 1.0F, UShort.MaxValue, CUShort(Math.Round(value * 65535.0F))))
                Else
                    plane(dst + x) = PsdLayerReader.FloatSampleToByte(value, colorChannel)
                End If
            Next
        End Sub

        ''' <summary>Rechnet bei 32 Bit mit Transparenz die weiße Hinterlegung aus der linearen
        ''' Farbe heraus und bringt sie danach erst auf die Anzeige. Dieselbe Rückrechnung wie bei 8
        ''' und 16 Bit (<see cref="UnmatteTable"/>), nur dort, wo Photoshop im 32-Bit-Modus mischt:
        ''' im linearen Licht. An einer Datei belegt ist das nicht; der Prüfsatz aus Photoshop-Dateien
        ''' hat kein 32-Bit-Gesamtbild mit Transparenz.</summary>
        Private Shared Sub UnmatteLinear(linear As UShort()(), planes As Byte()(), count As Integer)
            Dim alphaPlane = planes(3)
            For channel = 0 To 2
                Dim source = linear(channel)
                Dim target(count - 1) As Byte
                For i = 0 To count - 1
                    Dim coverage = alphaPlane(i) / 255.0
                    Dim matted = source(i) / 65535.0
                    Dim straight = If(coverage > 0, (matted - (1.0 - coverage)) / coverage, matted)
                    target(i) = PsdLayerReader.FloatSampleToByte(CSng(straight), True)
                Next
                planes(channel) = target
                linear(channel) = Nothing
            Next
        End Sub

        ''' <summary>Das Gesamtbild einer Lab-Datei als sRGB. Photoshop legt Lab mit Weißpunkt D50
        ''' ab: L von 0 bis 255 für 0 bis 100, a und b mit 128 als Null; bei 16 Bit L von 0 bis 65535
        ''' und a und b mit 32768 als Null, 256 Stufen je Einheit (<paramref name="wide"/>). Gerechnet
        ''' wird Lab nach XYZ, dann mit der nach Bradford auf D50
        ''' angepassten Matrix von sRGB ins lineare sRGB und zuletzt durch dieselbe Kennlinie wie bei
        ''' 32 Bit (<see cref="PsdLayerReader.FloatSampleToByte"/>). Das ist farbmetrisch ohne
        ''' Tiefenkompensierung; was außerhalb von sRGB liegt, wird abgeschnitten.
        '''
        ''' Mit Transparenz wird gegen Lab-Weiß (L 100, a und b 0) zurückgerechnet, wie bei RGB
        ''' gegen Weiß; abgeleitet, nicht an einer Datei belegt.</summary>
        Private Shared Function ComposeLab(header As PsdHeader, planes As Byte()(), wide As UShort()(),
                                           hasAlpha As Boolean) As SKBitmap
            Dim info = New SKImageInfo(header.Width, header.Height, SKColorType.Bgra8888,
                                       If(hasAlpha, SKAlphaType.Unpremul, SKAlphaType.Opaque))
            Dim bitmap = New SKBitmap(info)
            Try
                Dim count = header.Width * header.Height
                Dim buffer(count * 4 - 1) As Byte
                Const epsilon As Double = 6.0 / 29.0
                For i = 0 To count - 1
                    ' Alles auf die Skala von acht Bit, ohne zu runden: L bis 255, a und b um 128.
                    Dim lightness As Double = If(wide IsNot Nothing, wide(0)(i) / 257.0, CDbl(planes(0)(i)))
                    Dim aValue As Double = If(wide IsNot Nothing, wide(1)(i) / 256.0, CDbl(planes(1)(i)))
                    Dim bValue As Double = If(wide IsNot Nothing, wide(2)(i) / 256.0, CDbl(planes(2)(i)))
                    Dim alpha = If(hasAlpha, planes(3)(i), CByte(255))
                    If hasAlpha AndAlso alpha > 0 AndAlso alpha < 255 Then
                        Dim coverage = alpha / 255.0
                        lightness = (lightness - 255.0 * (1.0 - coverage)) / coverage
                        aValue = (aValue - 128.0 * (1.0 - coverage)) / coverage
                        bValue = (bValue - 128.0 * (1.0 - coverage)) / coverage
                    End If
                    Dim fy = (lightness * 100.0 / 255.0 + 16.0) / 116.0
                    Dim fx = fy + (aValue - 128.0) / 500.0
                    Dim fz = fy - (bValue - 128.0) / 200.0
                    Dim x = 0.96422 * LabInverse(fx, epsilon)
                    Dim y = LabInverse(fy, epsilon)
                    Dim z = 0.82521 * LabInverse(fz, epsilon)
                    Dim r = 3.1338561 * x - 1.6168667 * y - 0.4906146 * z
                    Dim g = -0.9787684 * x + 1.9161415 * y + 0.033454 * z
                    Dim b = 0.0719453 * x - 0.2289914 * y + 1.4052427 * z
                    buffer(i * 4) = PsdLayerReader.FloatSampleToByte(CSng(b), True)
                    buffer(i * 4 + 1) = PsdLayerReader.FloatSampleToByte(CSng(g), True)
                    buffer(i * 4 + 2) = PsdLayerReader.FloatSampleToByte(CSng(r), True)
                    buffer(i * 4 + 3) = alpha
                Next
                Runtime.InteropServices.Marshal.Copy(buffer, 0, bitmap.GetPixels(), buffer.Length)
                Return bitmap
            Catch
                bitmap.Dispose()
                Return Nothing
            End Try
        End Function

        Private Shared Function LabInverse(t As Double, epsilon As Double) As Double
            If t > epsilon Then Return t * t * t
            Return 3.0 * epsilon * epsilon * (t - 4.0 / 29.0)
        End Function

        Private Shared Function ComposePlanes(header As PsdHeader, planes As Byte()(), hasAlpha As Boolean,
                                              Optional palette As Byte() = Nothing,
                                              Optional transparentIndex As Integer = -1,
                                              Optional unmatteRgb As Boolean = True) As SKBitmap
            Dim info = New SKImageInfo(header.Width, header.Height, SKColorType.Bgra8888,
                                       If(hasAlpha, SKAlphaType.Unpremul, SKAlphaType.Opaque))
            Dim bitmap = New SKBitmap(info)
            Try
                Dim pixels = bitmap.GetPixels()
                Dim buffer(header.Width * header.Height * 4 - 1) As Byte
                Dim count = header.Width * header.Height

                ' Bei RGB legt Photoshop das Gesamtbild mit Transparenz GEGEN WEISS verrechnet ab: in jedem
                ' Kanal steht Farbe mal Deckung plus Weiß mal Rest. Gelesen ohne Rückrechnung
                ' bekäme jede halbdurchsichtige Kante einen hellen Saum. Die Rückrechnung geht über
                ' eine Tafel je Deckung, nicht über eine Division je Punkt.
                Dim unmatte = If(hasAlpha AndAlso unmatteRgb AndAlso header.ColorMode = PsdColorMode.Rgb, UnmatteTable(), Nothing)
                Select Case header.ColorMode
                    Case PsdColorMode.Rgb
                        For i = 0 To count - 1
                            If hasAlpha AndAlso unmatte Is Nothing Then
                                buffer(i * 4) = planes(2)(i)      ' B
                                buffer(i * 4 + 1) = planes(1)(i)  ' G
                                buffer(i * 4 + 2) = planes(0)(i)  ' R
                                buffer(i * 4 + 3) = planes(3)(i)
                            ElseIf hasAlpha Then
                                Dim row = unmatte(planes(3)(i))
                                buffer(i * 4) = row(planes(2)(i))      ' B
                                buffer(i * 4 + 1) = row(planes(1)(i))  ' G
                                buffer(i * 4 + 2) = row(planes(0)(i))  ' R
                                buffer(i * 4 + 3) = planes(3)(i)
                            Else
                                buffer(i * 4) = planes(2)(i)      ' B
                                buffer(i * 4 + 1) = planes(1)(i)  ' G
                                buffer(i * 4 + 2) = planes(0)(i)  ' R
                                buffer(i * 4 + 3) = 255
                            End If
                        Next
                    Case PsdColorMode.Grayscale
                        For i = 0 To count - 1
                            Dim g = planes(0)(i)
                            buffer(i * 4) = g
                            buffer(i * 4 + 1) = g
                            buffer(i * 4 + 2) = g
                            buffer(i * 4 + 3) = If(hasAlpha, planes(1)(i), CByte(255))
                        Next
                    Case PsdColorMode.Indexed
                        If palette Is Nothing OrElse palette.Length < 768 Then
                            bitmap.Dispose()
                            Return Nothing
                        End If
                        For i = 0 To count - 1
                            Dim index = CInt(planes(0)(i))
                            buffer(i * 4) = palette(512 + index)      ' B
                            buffer(i * 4 + 1) = palette(256 + index)  ' G
                            buffer(i * 4 + 2) = palette(index)        ' R
                            buffer(i * 4 + 3) = If(index = transparentIndex, CByte(0), CByte(255))
                        Next
                    Case Else
                        ' Photoshop legt CMYK INVERTIERT ab (255 = keine Farbe). Naive Wandlung ohne
                        ' ICC-Profil: Kanalwert mal Schwarzanteil - für eine Vorschau ausreichend.
                        For i = 0 To count - 1
                            Dim k = CInt(planes(3)(i))
                            buffer(i * 4) = CByte(CInt(planes(2)(i)) * k \ 255)      ' B aus Y'
                            buffer(i * 4 + 1) = CByte(CInt(planes(1)(i)) * k \ 255)  ' G aus M'
                            buffer(i * 4 + 2) = CByte(CInt(planes(0)(i)) * k \ 255)  ' R aus C'
                            buffer(i * 4 + 3) = 255
                        Next
                End Select

                Runtime.InteropServices.Marshal.Copy(buffer, 0, pixels, buffer.Length)
                Return bitmap
            Catch
                bitmap.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Je Deckung a eine Zeile, die einen gegen Weiß verrechneten Wert c auf die
        ''' eigentliche Farbe zurückführt: (c + a - 255) mal 255 durch a, gerundet und auf 0 bis 255
        ''' begrenzt. Bei a = 0 gibt es keine Farbe zurückzugewinnen, der Wert bleibt.</summary>
        Friend Shared Function UnmatteTable() As Byte()()
            Dim table(255)() As Byte
            For a = 0 To 255
                Dim row(255) As Byte
                For c = 0 To 255
                    If a = 0 Then
                        row(c) = CByte(c)
                    Else
                        Dim straight = (Math.Max(0, c + a - 255) * 255 + a \ 2) \ a
                        row(c) = CByte(Math.Min(255, straight))
                    End If
                Next
                table(a) = row
            Next
            Return table
        End Function

        ''' <summary>Ist der erste Zusatzkanal des Gesamtbilds seine Transparenz? Ein Zusatzkanal
        ''' kann auch eine gespeicherte Auswahl sein, und die als Transparenz gelesen stanzte Löcher
        ''' ins Bild.
        '''
        ''' Transparenz ist er, wenn die Ebenenzahl negativ ist, wenn ein globaler Block "Mtrn", "Mt16"
        ''' oder "Mt32" es anzeigt, oder wenn die Ebenen in "Lr16"/"Lr32" stehen und die
        ''' Kanalnamen (Ressource 1045, sonst 1006) genau so viele sind wie Zusatzkanäle und der
        ''' erste "Transparency" heißt. So schreibt es Photoshop; abgeleitet aus Dateien, die es
        ''' selbst geschrieben hat. Bei Dateien ohne Ebenen unterscheiden Kanalnamen eine
        ''' gespeicherte Auswahl von Transparenz. Ohne Kanalnamen ist die Belegung nicht eindeutig;
        ''' bei ebenenlosen Dateien wird der erste Zusatzkanal dann als Transparenz angenommen,
        ''' AUSSER Photoshop hat die Datei geschrieben (Ressource 1057 nennt es als Schreiber): eine
        ''' Datei ohne Ebenen hat dort einen Hintergrund und keine Transparenz, ein Zusatzkanal ist
        ''' also eine gespeicherte Auswahl. Nur auf Photoshop beschränkt, weil auch andere Schreiber
        ''' 1057 anlegen, FerrumPix selbst eingeschlossen, und eine flache Datei mit Transparenz
        ''' schreiben können.
        '''
        ''' Der Strom steht beim Aufruf am Anfang der Ebenen-Sektion, hinter ihrer Länge. Wo er
        ''' danach steht, ist gleich: der Aufrufer springt selbst an das Ende. Lässt sich die
        ''' Sektion nicht lesen, wird Transparenz angenommen.</summary>
        Private Shared Function MergedImageHasAlpha(fs As FileStream, header As PsdHeader, sectionEnd As Long,
                                                    resources As Byte(), extraChannels As Integer) As Boolean
            Try
                Dim namedTransparency = MergedTransparencyFromNames(resources, extraChannels)
                If Not namedTransparency.HasValue AndAlso WrittenByPhotoshop(resources) Then namedTransparency = False
                If fs.Position >= sectionEnd Then Return If(namedTransparency, True)

                Dim hasLayers = False
                Dim negativeCount = False
                Dim infoLen = If(header.IsPsb, ReadU64(fs), CLng(ReadU32(fs)))
                Dim infoStart = fs.Position
                If infoLen < 0 OrElse infoStart + infoLen > sectionEnd Then Return If(namedTransparency, True)
                If infoLen >= 2 Then
                    Dim count = ReadU16(fs)
                    If count <> 0 Then
                        hasLayers = True
                        negativeCount = count > 32767
                    End If
                End If

                ' Hinter der Ebenen-Info: die globale Maske, dann die globalen Zusatzblöcke.
                fs.Position = infoStart + infoLen
                If fs.Position + 4 > sectionEnd Then Return negativeCount OrElse (Not hasLayers AndAlso If(namedTransparency, True))
                If Not SkipBlock(fs, ReadU32(fs)) Then Return negativeCount OrElse (Not hasLayers AndAlso If(namedTransparency, True))

                Dim marked = False
                Dim layersInBlock = False
                While fs.Position + 12 <= sectionEnd
                    Dim sig(3) As Byte
                    If Not ReadExactly(fs, sig, 4) Then Exit While
                    Dim s = Text.Encoding.ASCII.GetString(sig)
                    If s <> "8BIM" AndAlso s <> "8B64" Then Exit While
                    Dim keyBytes(3) As Byte
                    If Not ReadExactly(fs, keyBytes, 4) Then Exit While
                    Dim key = Text.Encoding.ASCII.GetString(keyBytes)
                    Dim length = If(header.IsPsb AndAlso PsdLayerReader.IsPsbLongLengthKey(key), ReadU64(fs), CLng(ReadU32(fs)))
                    Dim dataStart = fs.Position
                    If length < 0 OrElse dataStart + length > sectionEnd Then Exit While

                    If key = "Mtrn" OrElse key = "Mt16" OrElse key = "Mt32" Then marked = True
                    If (key = "Lr16" OrElse key = "Lr32") AndAlso Not hasLayers AndAlso length >= 2 Then
                        layersInBlock = True
                        hasLayers = True
                        ' Wie im Ebenenleser: eine vorangestellte Länge zählt den Rest des
                        ' Blocks, höchstens um die Auffüllung auf vier Byte kürzer.
                        Dim countAt = dataStart
                        If length >= 6 Then
                            Dim lead = If(header.IsPsb, ReadU64(fs), CLng(ReadU32(fs)))
                            Dim leadBytes = If(header.IsPsb, 8L, 4L)
                            If lead > 0 AndAlso lead <= length - leadBytes AndAlso lead >= length - leadBytes - 3 Then countAt = fs.Position
                        End If
                        fs.Position = countAt
                        negativeCount = ReadU16(fs) > 32767
                    End If
                    fs.Position = dataStart + ((length + 3L) And Not 3L)
                End While

                If negativeCount OrElse marked Then Return True
                If Not hasLayers Then Return If(namedTransparency, True)
                Return layersInBlock AndAlso If(namedTransparency, False)
            Catch
                Return True
            End Try
        End Function

        ''' <summary>Tragen die Kanalnamen genau <paramref name="extraChannels"/> Einträge, und
        ''' heißt der erste "Transparency"? Ressource 1045 (Unicode: Länge in Zeichen, dann
        ''' UTF-16), sonst 1006 (Pascal: ein Längenbyte, dann der Text).</summary>
        Private Shared Function MergedTransparencyFromNames(resources As Byte(), extraChannels As Integer) As Boolean?
            Dim names As New List(Of String)()
            Dim unicode = FindResource(resources, 1045)
            If unicode IsNot Nothing Then
                Dim at = 0
                While at + 4 <= unicode.Length
                    Dim length = (CLng(unicode(at)) << 24) Or (CLng(unicode(at + 1)) << 16) Or
                                 (CLng(unicode(at + 2)) << 8) Or CLng(unicode(at + 3))
                    at += 4
                    If length < 0 OrElse at + length * 2 > unicode.Length Then Exit While
                    Dim sb As New Text.StringBuilder()
                    For i = 0 To CInt(length) - 1
                        sb.Append(ChrW(CInt(unicode(at + i * 2)) * 256 + unicode(at + i * 2 + 1)))
                    Next
                    names.Add(sb.ToString().TrimEnd(ChrW(0)))
                    at += CInt(length) * 2
                End While
            Else
                Dim pascal = FindResource(resources, 1006)
                If pascal Is Nothing Then Return Nothing
                Dim at = 0
                While at < pascal.Length
                    Dim length = CInt(pascal(at))
                    If at + 1 + length > pascal.Length Then Exit While
                    names.Add(Text.Encoding.ASCII.GetString(pascal, at + 1, length))
                    at += 1 + length
                End While
            End If
            If names.Count <> extraChannels OrElse names.Count = 0 Then Return Nothing
            Return String.Equals(names(0), "Transparency", StringComparison.Ordinal)
        End Function

        ''' <summary>Das Profil aus seinen Bytes, oder Nothing. Fuer den flachen Weg, der es sofort
        ''' anwendet und wieder freigibt.</summary>
        Private Shared Function CreateIccOrNothing(bytes As Byte()) As SKColorSpace
            If bytes Is Nothing Then Return Nothing
            Try
                Return SKColorSpace.CreateIcc(bytes)
            Catch
                ' Ein Block, der sich Profil nennt, aber keines ist, kostet nur das Farbmanagement -
                ' NICHT das Bild. Ohne dieses Abfangen liefe die Ausnahme bis zum Aufrufer, und eine
                ' PSD mit beschaedigtem Profil liesse sich gar nicht mehr oeffnen.
                Return Nothing
            End Try
        End Function

        ''' <summary>Liest die Bildressourcen und liefert daraus das ICC-Profil (Ressource 1039)
        ''' als ROHE BYTES, oder Nothing. Wer das Profil erst spaeter braucht, nimmt diesen Weg: ein
        ''' Bytefeld raeumt die Laufzeitumgebung selbst ab, ein fertiges Profil dagegen ist natives
        ''' Eigentum und muss durch jeden Abbruch- und Ausnahmepfad getragen werden, der bis dahin
        ''' noch kommt. Der Ebenenleser tut genau das - er erzeugt das Profil erst, wenn sein
        ''' Dokument steht.</summary>
        Friend Shared Function ReadIccBytesFromResources(fs As FileStream, length As Long) As Byte()
            Return FindResource(ReadResourceSection(fs, length), 1039)
        End Function

        ''' <summary>Liest die ganze Sektion der Bildressourcen, oder Nothing. Der Strom steht danach
        ''' hinter der Sektion - auch wenn sie unbrauchbar ist, sonst verruecken alle folgenden
        ''' Angaben.</summary>
        Private Shared Function ReadResourceSection(fs As FileStream, length As Long) As Byte()
            If length <= 0 OrElse fs.Position + length > fs.Length Then
                If length > 0 Then SkipBlock(fs, length)
                Return Nothing
            End If
            Dim section(CInt(Math.Min(length, Integer.MaxValue)) - 1) As Byte
            If Not ReadExactly(fs, section, section.Length) Then Return Nothing
            Return section
        End Function

        ''' <summary>Die Daten der Ressource mit dieser Nummer, oder Nothing.
        '''
        ''' Aufbau eines Blocks: die Kennung "8BIM", zwei Byte Nummer, ein Pascal-Text als Name
        ''' (auf gerade Laenge aufgefuellt), vier Byte Groesse, dann die Daten (ebenfalls auf gerade
        ''' Laenge aufgefuellt).</summary>
        Private Shared Function FindResource(section As Byte(), wanted As Integer) As Byte()
            If section Is Nothing Then Return Nothing
            Try
                Dim offset = 0
                While offset + 12 <= section.Length
                    If Text.Encoding.ASCII.GetString(section, offset, 4) <> "8BIM" Then Exit While
                    Dim id = CInt(section(offset + 4)) * 256 + section(offset + 5)

                    Dim nameLength = CInt(section(offset + 6))
                    ' Der Name zaehlt sein Laengenbyte mit und wird auf gerade Laenge aufgefuellt.
                    Dim nameBlock = nameLength + 1
                    If nameBlock Mod 2 <> 0 Then nameBlock += 1
                    Dim sizeOffset = offset + 6 + nameBlock
                    If sizeOffset + 4 > section.Length Then Exit While

                    Dim size = (CInt(section(sizeOffset)) << 24) Or (CInt(section(sizeOffset + 1)) << 16) Or
                               (CInt(section(sizeOffset + 2)) << 8) Or CInt(section(sizeOffset + 3))
                    Dim dataOffset = sizeOffset + 4
                    ' In Long verglichen: eine Groesse nahe 2^31 liess die Summe sonst ueberlaufen,
                    ' bevor die Pruefung greifen konnte.
                    If size < 0 OrElse CLng(dataOffset) + size > section.Length Then Exit While

                    If id = wanted AndAlso size > 0 Then
                        Dim data(size - 1) As Byte
                        Buffer.BlockCopy(section, dataOffset, data, 0, size)
                        Return data
                    End If

                    Dim padded = size
                    If padded Mod 2 <> 0 Then padded += 1
                    offset = dataOffset + padded
                End While
            Catch ex As Exception
                DiagnosticLogService.LogException("Psd.FindResource", ex)
            End Try
            Return Nothing
        End Function

        ''' PackBits: n >= 0 → n+1 Bytes wörtlich; n = -1..-127 → nächstes Byte (-n)+1-mal; -128 → nichts.
        ''' Friend, weil PsdLayerReader dieselbe Entpackung für die Kanäle der Ebenen braucht.
        Friend Shared Function UnpackBits(packed As Byte(), packedLen As Integer, target As Byte(), targetLen As Integer) As Boolean
            Dim src = 0
            Dim dst = 0
            While src < packedLen AndAlso dst < targetLen
                ' Kein CSByte: das ist eine GEPRÜFTE Konvertierung und wirft ab 128 eine
                ' OverflowException (VB-Falle wie beim Byte-Shift) - Vorzeichen von Hand umrechnen.
                Dim n As Integer = packed(src)
                If n > 127 Then n -= 256
                src += 1
                If n >= 0 Then
                    Dim runLen = n + 1
                    If src + runLen > packedLen OrElse dst + runLen > targetLen Then Return False
                    Array.Copy(packed, src, target, dst, runLen)
                    src += runLen
                    dst += runLen
                ElseIf n <> -128 Then
                    Dim runLen = 1 - n
                    If src >= packedLen OrElse dst + runLen > targetLen Then Return False
                    Dim value = packed(src)
                    src += 1
                    For i = 0 To runLen - 1
                        target(dst + i) = value
                    Next
                    dst += runLen
                End If
            End While
            Return dst = targetLen
        End Function

        ' ── Thumbnail-Fallback (Bildressource 1036) ──────────────────────────────

        Private Shared Function DecodeThumbnailResource(fs As FileStream) As SKBitmap
            Dim header As PsdHeader
            If Not TryReadHeader(fs, header) Then Return Nothing
            If Not SkipBlock(fs, ReadU32(fs)) Then Return Nothing ' Farbmodus-Daten

            Dim resourcesLen = ReadU32(fs)
            Dim resourcesEnd = fs.Position + resourcesLen
            If resourcesEnd > fs.Length Then Return Nothing

            While fs.Position + 12 <= resourcesEnd
                Dim sigBuf(3) As Byte
                If Not ReadExactly(fs, sigBuf, 4) Then Return Nothing
                If Text.Encoding.ASCII.GetString(sigBuf) <> "8BIM" Then Return Nothing
                Dim resourceId = ReadU16(fs)
                ' Pascal-Name: Längenbyte + Text, das GANZE Feld auf gerade Länge aufgefüllt.
                Dim nameLen = fs.ReadByte()
                If nameLen < 0 Then Return Nothing
                Dim namePadded = If((nameLen + 1) Mod 2 = 0, nameLen, nameLen + 1)
                If Not SkipBlock(fs, namePadded) Then Return Nothing
                Dim dataLen = ReadU32(fs)
                Dim dataStart = fs.Position
                If dataStart + dataLen > resourcesEnd Then Return Nothing

                ' 1036 = Thumbnail (RGB), Kopf 28 Bytes: Format(4) Breite(4) Höhe(4) Zeilenbytes(4)
                ' Gesamtgröße(4) komprimierte Größe(4) Bits(2) Ebenen(2), danach die JPEG-Daten.
                If resourceId = 1036 AndAlso dataLen > 28 Then
                    Dim fileFormat = ReadU32(fs)
                    If fileFormat = 1 Then ' kJpegRGB
                        If Not SkipBlock(fs, 24) Then Return Nothing
                        Dim jpegLen = CInt(dataLen - 28)
                        Dim jpeg(jpegLen - 1) As Byte
                        If Not ReadExactly(fs, jpeg, jpegLen) Then Return Nothing
                        Return SKBitmap.Decode(jpeg)
                    End If
                End If

                ' Ressourcendaten sind auf gerade Länge aufgefüllt.
                Dim skip = dataLen + (dataLen And 1L)
                fs.Seek(dataStart + skip, SeekOrigin.Begin)
            End While
            Return Nothing
        End Function

        ' ── Header und Lese-Helfer ───────────────────────────────────────────────

        Private Shared Function TryReadHeader(fs As FileStream, ByRef header As PsdHeader) As Boolean
            Dim buf(HeaderSize - 1) As Byte
            If Not ReadExactly(fs, buf, HeaderSize) Then Return False
            If buf(0) <> Asc("8") OrElse buf(1) <> Asc("B") OrElse buf(2) <> Asc("P") OrElse buf(3) <> Asc("S") Then Return False

            Dim version = CInt(buf(4)) * 256 + buf(5)
            If version <> 1 AndAlso version <> 2 Then Return False

            header.IsPsb = version = 2
            header.Channels = CInt(buf(12)) * 256 + buf(13)
            ' Hoehe und Breite sind vorzeichenlose 32 Bit: erst als Long gegen die Grenze, dann
            ' umwandeln. Ein Wert ueber 2^31 warf sonst schon beim CInt einen Ueberlauf.
            Dim height = (CLng(buf(14)) << 24) Or (CLng(buf(15)) << 16) Or (CLng(buf(16)) << 8) Or buf(17)
            Dim width = (CLng(buf(18)) << 24) Or (CLng(buf(19)) << 16) Or (CLng(buf(20)) << 8) Or buf(21)
            header.Depth = CInt(buf(22)) * 256 + buf(23)
            header.ColorMode = CInt(buf(24)) * 256 + buf(25)

            Dim maxSide = If(header.IsPsb, 300_000, 30_000)
            If header.Channels < 1 OrElse header.Channels > 56 Then Return False
            If width < 1 OrElse width > maxSide Then Return False
            If height < 1 OrElse height > maxSide Then Return False
            header.Height = CInt(height)
            header.Width = CInt(width)
            If CLng(header.Width) * header.Height > MaxPixels Then Return False

            ' Nicht nur die Fläche deckeln, sondern den tatsächlichen SPITZENVERBRAUCH schätzen.
            ' Der reine Flächendeckel schützt vor dem .NET-Array-Limit, nicht vor dem Gesamtbedarf:
            ' bei 250 MP liegen gleichzeitig rund 1 GB Kanal-Ebenen, 1 GB BGRA-Puffer und 1 GB
            ' Bitmap im Speicher - etwa 3 GB für EINE Datei. Wird der Deckel gerissen, liefert der
            ' Composite-Leser Nothing und ExtractPreview fällt auf das eingebettete Thumbnail
            ' zurück; das Bild wird also weiterhin angezeigt, nur nicht in voller Auflösung.
            Return EstimatedPeakBytes(header) <= MaxCompositeBytes
        End Function

        ''' <summary>Spitzenverbrauch des Composite-Lesers: die Kanal-Ebenen (auf 8 Bit je Abtastwert
        ''' normalisiert, also ein Byte je Pixel und Kanal), der BGRA-Ausgabepuffer und das
        ''' Zielbitmap - alle drei liegen gleichzeitig im Speicher.</summary>
        Private Shared Function EstimatedPeakBytes(header As PsdHeader) As Long
            Dim pixels = CLng(header.Width) * header.Height
            Dim planes = Math.Min(header.Channels, 4)
            Return pixels * planes + pixels * 4 + pixels * 4
        End Function

        ''' <summary>Nimmt einen beliebigen Strom, nicht nur die Datei: bei ZIP wird aus dem
        ''' Entpacker gelesen, und der ist kein FileStream.</summary>
        Private Shared Function ReadExactly(fs As Stream, buffer As Byte(), count As Integer) As Boolean
            Dim total = 0
            While total < count
                Dim n = fs.Read(buffer, total, count - total)
                If n <= 0 Then Return False
                total += n
            End While
            Return True
        End Function

        ''' <summary>Sieht der Anfang nach einem zlib-Kopf aus? Untere vier Bit des ersten Bytes sind
        ''' das Verfahren (8 = Deflate), und die beiden Bytes zusammen sind durch 31 teilbar.
        '''
        ''' Gebraucht wird das, weil manche Erzeuger den NACKTEN Deflate-Strom ablegen, ohne die zwei
        ''' Kopfbytes. Die Prüfung kostet nichts und erspart den Fehlversuch.</summary>
        Friend Shared Function LooksLikeZlib(first As Byte, second As Byte) As Boolean
            If (first And &HF) <> 8 Then Return False
            Return (CInt(first) * 256 + CInt(second)) Mod 31 = 0
        End Function

        ''' <summary>Macht die VORHERSAGE einer ZIP-Zeile rückgängig: dort steht in jedem Wert nicht
        ''' der Wert selbst, sondern die Differenz zu seinem linken Nachbarn.
        '''
        ''' Zeilenweise, nie über die Fläche - sonst schleppte der erste Punkt einer Zeile den letzten
        ''' der vorigen mit sich, und das Bild zöge sich schräg auseinander. Bei sechzehn Bit läuft
        ''' die Rechnung über ganze Werte statt über Bytes; byteweise käme aus jedem Übertrag ein
        ''' Fehler, der sich über die restliche Zeile fortpflanzt.
        '''
        ''' Gerechnet wird über Integer und mit einer Maske zurück. VB prüft Ganzzahlüberläufe von
        ''' sich aus, und eine Summe über 255 wäre sonst kein Umlauf, sondern eine Ausnahme mitten
        ''' im Entpacken.</summary>
        Friend Shared Sub UndoPrediction(buffer As Byte(), offset As Integer, width As Integer,
                                         bytesPerSample As Integer)
            If buffer Is Nothing OrElse width < 2 Then Return
            If bytesPerSample = 1 Then
                For x = 1 To width - 1
                    buffer(offset + x) = CByte((CInt(buffer(offset + x)) + CInt(buffer(offset + x - 1))) And &HFF)
                Next
                Return
            End If
            For x = 1 To width - 1
                Dim here = offset + x * 2
                Dim before = here - 2
                Dim sum = ((CInt(buffer(here)) << 8) Or CInt(buffer(here + 1))) +
                          ((CInt(buffer(before)) << 8) Or CInt(buffer(before + 1)))
                sum = sum And &HFFFF
                buffer(here) = CByte((sum >> 8) And &HFF)
                buffer(here + 1) = CByte(sum And &HFF)
            Next
        End Sub

        Private Shared Function SkipBlock(fs As FileStream, length As Long) As Boolean
            If length < 0 OrElse fs.Position + length > fs.Length Then Return False
            fs.Seek(length, SeekOrigin.Current)
            Return True
        End Function

        Private Shared Function ReadU16(fs As FileStream) As Integer
            Dim b(1) As Byte
            If Not ReadExactly(fs, b, 2) Then Throw New EndOfStreamException()
            Return CInt(b(0)) * 256 + b(1)
        End Function

        ' Operanden vor dem Shift weiten - "byteWert << n" bliebe in VB ein Byte (siehe RawPreviewService).
        Private Shared Function ReadU32(fs As FileStream) As Long
            Dim b(3) As Byte
            If Not ReadExactly(fs, b, 4) Then Throw New EndOfStreamException()
            Return (CLng(b(0)) << 24) Or (CLng(b(1)) << 16) Or (CLng(b(2)) << 8) Or CLng(b(3))
        End Function

        Private Shared Function ReadU64(fs As FileStream) As Long
            Dim b(7) As Byte
            If Not ReadExactly(fs, b, 8) Then Throw New EndOfStreamException()
            Return (CLng(b(0)) << 56) Or (CLng(b(1)) << 48) Or (CLng(b(2)) << 40) Or (CLng(b(3)) << 32) Or
                   (CLng(b(4)) << 24) Or (CLng(b(5)) << 16) Or (CLng(b(6)) << 8) Or CLng(b(7))
        End Function

    End Class

End Namespace
