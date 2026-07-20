Imports System
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
    ''' (unkomprimiert oder RLE/PackBits, kanalweise planar). Abgedeckt: 8/16 Bit, RGB(+Alpha),
    ''' Graustufen(+Alpha), CMYK (naiv nach RGB, Photoshop speichert CMYK invertiert). Fehlt das
    ''' Composite (Kompatibilität beim Speichern deaktiviert) oder ist der Modus exotisch (Lab,
    ''' indiziert, 32 Bit), fällt der Leser auf das eingebettete JPEG-Thumbnail (Bildressource 1036)
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
            Rgb = 3
            Cmyk = 4
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

        ''' Liefert einen MemoryStream mit PNG-Daten (Position 0) oder Nothing bei Fehler.
        Public Shared Function ExtractPreview(filePath As String) As MemoryStream
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
                If bitmap Is Nothing Then Return Nothing

                Using bitmap
                    ' Encode über das Pixmap, nicht über SKImage.FromBitmap: Raster-SKImages
                    ' verlangen Premul/Opaque, das Composite mit Alpha liegt aber bewusst als
                    ' Unpremul vor (PNG speichert geradliniges Alpha - so bleibt es verlustfrei).
                    Using pixmap = bitmap.PeekPixels()
                        If pixmap Is Nothing Then Return Nothing
                        Using encoded = pixmap.Encode(SKEncodedImageFormat.Png, 100)
                            If encoded Is Nothing Then Return Nothing
                            Dim ms As New MemoryStream()
                            encoded.SaveTo(ms)
                            ms.Position = 0
                            Return ms
                        End Using
                    End Using
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ' ── Composite (Bilddaten-Sektion) ────────────────────────────────────────

        Private Shared Function DecodeComposite(fs As FileStream) As SKBitmap
            Dim header As PsdHeader
            If Not TryReadHeader(fs, header) Then Return Nothing

            ' Nur Bittiefen/Modi, die dieser Leser wirklich beherrscht - alles andere geht in den
            ' Thumbnail-Fallback statt in ein falsch eingefärbtes Bild.
            If header.Depth <> 8 AndAlso header.Depth <> 16 Then Return Nothing
            If header.ColorMode <> PsdColorMode.Grayscale AndAlso
               header.ColorMode <> PsdColorMode.Rgb AndAlso
               header.ColorMode <> PsdColorMode.Cmyk Then Return Nothing

            ' Farbmodus-Daten, Bildressourcen und Ebenen-Sektion überspringen.
            If Not SkipBlock(fs, ReadU32(fs)) Then Return Nothing
            If Not SkipBlock(fs, ReadU32(fs)) Then Return Nothing
            Dim layerLen = If(header.IsPsb, ReadU64(fs), CLng(ReadU32(fs)))
            If Not SkipBlock(fs, layerLen) Then Return Nothing

            Dim compression = ReadU16(fs)
            If compression <> 0 AndAlso compression <> 1 Then Return Nothing ' 2/3 = ZIP, nur in Ebenen üblich

            ' Wie viele der planar abgelegten Kanäle das Ergebnis wirklich braucht: RGB 3 (+Alpha),
            ' Graustufen 1 (+Alpha), CMYK 4. Weitere Kanäle (gespeicherte Auswahlen) werden gelesen
            ' und verworfen, damit die Planar-Reihenfolge nicht verrutscht.
            Dim neededChannels As Integer
            Dim hasAlpha As Boolean
            Select Case header.ColorMode
                Case PsdColorMode.Rgb
                    If header.Channels < 3 Then Return Nothing
                    hasAlpha = header.Channels >= 4
                    neededChannels = If(hasAlpha, 4, 3)
                Case PsdColorMode.Grayscale
                    If header.Channels < 1 Then Return Nothing
                    hasAlpha = header.Channels >= 2
                    neededChannels = If(hasAlpha, 2, 1)
                Case Else ' CMYK
                    If header.Channels < 4 Then Return Nothing
                    hasAlpha = False
                    neededChannels = 4
            End Select

            Dim bytesPerSample = header.Depth \ 8
            Dim rowBytes = header.Width * bytesPerSample
            Dim planeSize = header.Width * header.Height

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

            For channel = 0 To header.Channels - 1
                Dim keep = channel < neededChannels
                If keep Then planes(channel) = New Byte(planeSize - 1) {}

                If compression = 0 Then
                    If Not keep Then
                        If Not SkipBlock(fs, CLng(rowBytes) * header.Height) Then Return Nothing
                        Continue For
                    End If
                    For row = 0 To header.Height - 1
                        If Not ReadExactly(fs, rowBuffer, rowBytes) Then Return Nothing
                        StoreRow(planes(channel), row, header.Width, rowBuffer, bytesPerSample)
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
                        StoreRow(planes(channel), row, header.Width, rowBuffer, bytesPerSample)
                    Next
                End If
            Next

            Return ComposePlanes(header, planes, hasAlpha)
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

        Private Shared Function ComposePlanes(header As PsdHeader, planes As Byte()(), hasAlpha As Boolean) As SKBitmap
            Dim info = New SKImageInfo(header.Width, header.Height, SKColorType.Bgra8888,
                                       If(hasAlpha, SKAlphaType.Unpremul, SKAlphaType.Opaque))
            Dim bitmap = New SKBitmap(info)
            Try
                Dim pixels = bitmap.GetPixels()
                Dim buffer(header.Width * header.Height * 4 - 1) As Byte
                Dim count = header.Width * header.Height

                Select Case header.ColorMode
                    Case PsdColorMode.Rgb
                        For i = 0 To count - 1
                            buffer(i * 4) = planes(2)(i)      ' B
                            buffer(i * 4 + 1) = planes(1)(i)  ' G
                            buffer(i * 4 + 2) = planes(0)(i)  ' R
                            buffer(i * 4 + 3) = If(hasAlpha, planes(3)(i), CByte(255))
                        Next
                    Case PsdColorMode.Grayscale
                        For i = 0 To count - 1
                            Dim g = planes(0)(i)
                            buffer(i * 4) = g
                            buffer(i * 4 + 1) = g
                            buffer(i * 4 + 2) = g
                            buffer(i * 4 + 3) = If(hasAlpha, planes(1)(i), CByte(255))
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

        ''' PackBits: n >= 0 → n+1 Bytes wörtlich; n = -1..-127 → nächstes Byte (-n)+1-mal; -128 → nichts.
        Private Shared Function UnpackBits(packed As Byte(), packedLen As Integer, target As Byte(), targetLen As Integer) As Boolean
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
                    Dim format = ReadU32(fs)
                    If format = 1 Then ' kJpegRGB
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
            header.Height = CInt((CLng(buf(14)) << 24) Or (CLng(buf(15)) << 16) Or (CLng(buf(16)) << 8) Or buf(17))
            header.Width = CInt((CLng(buf(18)) << 24) Or (CLng(buf(19)) << 16) Or (CLng(buf(20)) << 8) Or buf(21))
            header.Depth = CInt(buf(22)) * 256 + buf(23)
            header.ColorMode = CInt(buf(24)) * 256 + buf(25)

            Dim maxSide = If(header.IsPsb, 300_000, 30_000)
            If header.Channels < 1 OrElse header.Channels > 56 Then Return False
            If header.Width < 1 OrElse header.Width > maxSide Then Return False
            If header.Height < 1 OrElse header.Height > maxSide Then Return False
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

        Private Shared Function ReadExactly(fs As FileStream, buffer As Byte(), count As Integer) As Boolean
            Dim total = 0
            While total < count
                Dim n = fs.Read(buffer, total, count - total)
                If n <= 0 Then Return False
                total += n
            End While
            Return True
        End Function

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
