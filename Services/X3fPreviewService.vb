Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Text
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Die Vorschau, die die Kamera in eine Sigma-X3F-Datei legt (Foveon-Sensor, dazu die Polaroid
    ''' x530), und die Aufnahmedaten daraus. ENTWICKELT wird eine X3F wie jede RAW ueber LibRaw
    ''' (<see cref="RawDecodeService"/>): die mitgelieferten Bauten setzen den Schalter USE_X3FTOOLS,
    ''' der LibRaws X3F-Leser einschaltet (packaging/libraw). Die Endung steht deshalb unter
    ''' <see cref="RawPreviewService.SupportedExtensions"/>, und dieser Leser ist dort, was der
    ''' Scanner fuer die uebrigen RAWs ist: <see cref="RawPreviewService.ExtractPreview"/> nimmt ihn.
    '''
    ''' WARUM NEBEN LIBRAW EIN EIGENER LESER. LibRaws Vorschau-Schnittstelle gibt bei X3F nur das
    ''' JPEG und die ungepackte Form heraus, nicht die Huffman-gepackte: bei der SD10 bekommt die
    ''' Kachel dort nur ein Miniaturbild von 189 x 126 statt der 567 x 378 hier. Und eine LibRaw ohne
    ''' den Schalter (ein Flatpak, das LibRaw selbst baut, oder eine des Systems) oeffnet keine X3F;
    ''' dann bleibt diese Vorschau der ganze Weg, wie die eingebettete Vorschau bei jeder RAW ohne
    ''' LibRaw. Fuer die Kachel kommt LibRaw zuerst, wie bei allen RAWs.
    '''
    ''' Die Vorschauarten folgen der oeffentlichen Formatbeschreibung (Art 0x00020003 rohes RGB,
    ''' 0x0002000B Huffman, 0x00020012 JPEG); Code ist keiner uebernommen.
    '''
    ''' AUFBAU DER DATEI. Am Anfang "FOVb", in den letzten vier Bytes der Abstand des
    ''' Inhaltsverzeichnisses ("SECd": Fassung, Anzahl, dann je Eintrag Abstand, Laenge und Art). Bilder
    ''' stehen in Abschnitten der Art "IMA2" oder "IMAG", jeder mit eigenem Kopf "SECi": Fassung, Typ,
    ''' Format, Breite, Hoehe, Zeilenlaenge, danach die Daten. Typ 2 ist eine Vorschau. Gelesen werden
    ''' drei Vorschauformen:
    ''' - ein JPEG (an FF D8 erkannt, nicht an der Formatnummer: die dp2 Quattro legt ihr kleines
    '''   JPEG unter einer anderen Nummer ab als die grossen). DP1, DP1S, DP2 Merrill, dp2 Quattro,
    '''   SD14 und die Polaroid tragen eines in voller Anzeigegroesse.
    ''' - Format 3: ungepackt, je Punkt drei Bytes in der Folge Rot, Gruen, Blau, Zeilen mit der
    '''   Zeilenlaenge aus dem Kopf (SD9: 567 x 378).
    ''' - Format 11: dieselben Punkte, je Zeile und Kanal als Differenz zum linken Nachbarn mit einem
    '''   Huffman-Code gepackt (SD10: 567 x 378). Die Codetabelle steht als 256 Eintraege vor den
    '''   Daten, je Eintrag oben fuenf Bit Codelaenge, darunter der Code.
    ''' Genommen wird die groesste lesbare Vorschau. SD9 und SD10 haben keine groessere; ihre
    ''' Anzeige bleibt klein.
    '''
    ''' Jeder Abstand und jede Laenge wird gegen das Dateiende geprueft, bevor gelesen wird: eine
    ''' beschaedigte Datei liefert Nothing, nie eine Ausnahme nach aussen und nie einen Lesezugriff
    ''' hinter das Ende.
    ''' </summary>
    Public NotInheritable Class X3fPreviewService

        Private Sub New()
        End Sub

        Public Const Extension As String = ".x3f"

        ''' Obergrenzen gegen unsinnige Werte aus einer beschaedigten Datei, bevor sie zur
        ''' Speicheranforderung werden.
        Private Const MaxDirectoryEntries As Integer = 256
        Private Const MaxPreviewBytes As Long = 256L * 1024 * 1024
        Private Const MaxEdge As Integer = 30000
        Private Const MaxPixels As Long = 100_000_000L
        Private Const SectionHeaderSize As Integer = 28
        Private Const PreviewType As Integer = 2
        Private Const FormatUncompressedRgb As Integer = 3
        Private Const FormatHuffmanRgb As Integer = 11
        Private Const HuffmanTableEntries As Integer = 256

        ''' Fuer die Masse genuegt der Anfang des JPEG bis zu seinem Bildkopf; die EXIF-Daten der
        ''' Sigma-Kameras davor sind knapp 60 KB gross.
        Private Const JpegHeadBytes As Integer = 512 * 1024

        Private Enum PreviewKind
            Jpeg
            UncompressedRgb
            HuffmanRgb
        End Enum

        Private NotInheritable Class PreviewSection
            Public Kind As PreviewKind
            Public DataOffset As Long
            Public DataLength As Long
            Public Width As Integer
            Public Height As Integer
            Public RowStride As Integer
        End Class

        Public Shared Function IsSupportedX3f(filePath As String) As Boolean
            If String.IsNullOrEmpty(filePath) Then Return False
            Return String.Equals(Path.GetExtension(filePath), Extension, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Die groesste Vorschau als Bild (Besitz beim Aufrufer), aufrecht und in sRGB, oder
        ''' Nothing. Bgra8888, deckend.</summary>
        Public Shared Function TryDecode(filePath As String) As SKBitmap
            If String.IsNullOrWhiteSpace(filePath) OrElse Not File.Exists(filePath) Then Return Nothing
            Try
                Using stream = New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Dim best = FindLargestPreview(stream)
                    If best Is Nothing Then Return Nothing
                    Dim bytes = ReadRange(stream, best.DataOffset, best.DataLength)
                    If bytes Is Nothing Then Return Nothing
                    Select Case best.Kind
                        Case PreviewKind.Jpeg
                            Return DecodeJpeg(bytes)
                        Case PreviewKind.UncompressedRgb
                            Return DecodeUncompressed(bytes, best.Width, best.Height, best.RowStride)
                        Case Else
                            Return DecodeHuffman(bytes, best.Width, best.Height)
                    End Select
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("X3F.Decode", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Die groesste Vorschau als Strom, an den Anfang gespult, oder Nothing: ein JPEG so,
        ''' wie es in der Datei steht (Drehung und Farbprofil in seinem Kopf, wie bei der
        ''' eingebetteten Vorschau jeder anderen RAW), die beiden anderen Formen als PNG. Das ist der
        ''' Strom, den <see cref="RawPreviewService.ExtractPreview"/> fuer eine X3F herausgibt; ein
        ''' JPEG erst zu entschluesseln und als PNG neu zu packen, kostete bei der dp2 Quattro
        ''' (5424 x 3616) nur Zeit.</summary>
        Public Shared Function ExtractPreview(filePath As String) As MemoryStream
            If String.IsNullOrWhiteSpace(filePath) OrElse Not File.Exists(filePath) Then Return Nothing
            Try
                Using stream = New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Dim best = FindLargestPreview(stream)
                    If best Is Nothing Then Return Nothing
                    If best.Kind = PreviewKind.Jpeg Then
                        Dim bytes = ReadRange(stream, best.DataOffset, best.DataLength)
                        Return If(bytes Is Nothing, Nothing, New MemoryStream(bytes))
                    End If
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("X3F.Preview", ex)
                Return Nothing
            End Try
            Using bitmap = TryDecode(filePath)
                Return PngEncoder.EncodeToStream(bitmap, PngPurpose.Transient)
            End Using
        End Function

        ''' <summary>Die Masse der groessten Vorschau, so gedreht, wie <see cref="TryDecode"/> sie
        ''' liefert, ohne die Bilddaten zu entschluesseln. (0,0), wenn keine lesbar ist.</summary>
        Public Shared Function TryGetSize(filePath As String) As (Width As Integer, Height As Integer)
            If String.IsNullOrWhiteSpace(filePath) OrElse Not File.Exists(filePath) Then Return (0, 0)
            Try
                Using stream = New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Dim best = FindLargestPreview(stream)
                    If best Is Nothing Then Return (0, 0)
                    If best.Kind <> PreviewKind.Jpeg Then Return (best.Width, best.Height)
                    ' Beim JPEG zaehlt die Drehung aus seinem EXIF; der Kopf des Codecs nennt sie.
                    Dim head = ReadRange(stream, best.DataOffset, Math.Min(best.DataLength, JpegHeadBytes))
                    Dim size = JpegOrientedSize(head)
                    If size.Width > 0 Then Return size
                    ' Kopf zu kurz gelesen (ungewoehnlich grosse Vorspanne): dann das ganze JPEG.
                    Return JpegOrientedSize(ReadRange(stream, best.DataOffset, best.DataLength))
                End Using
            Catch
                Return (0, 0)
            End Try
        End Function

        ''' <summary>Die Metadaten fuer Infopanel, Katalog und Export, in der Form, die
        ''' MetadataExtractor liefert: die EXIF-Verzeichnisse aus dem eingebetteten JPEG, wenn es
        ''' welche traegt; sonst gebaut aus dem Kopfdatenblock "PROP" der Datei (Hersteller, Modell,
        ''' Aufnahmezeit, Belichtungszeit, Blende, ISO, Brennweite). Dazu wie im Original "File".
        ''' MetadataExtractor selbst kennt das Format nicht und wirft.</summary>
        Public Shared Function ReadMetadataDirectories(filePath As String) As IReadOnlyList(Of MetadataExtractor.Directory)
            Dim result As New List(Of MetadataExtractor.Directory)()
            Try
                Using stream = New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Dim sections = ReadDirectory(stream)
                    If sections IsNot Nothing Then
                        Dim jpeg = FindLargestPreview(stream, sections, onlyJpeg:=True)
                        If jpeg IsNot Nothing Then
                            Dim head = ReadRange(stream, jpeg.DataOffset, Math.Min(jpeg.DataLength, JpegHeadBytes))
                            Dim app1 = FindExifSegment(head)
                            If app1 IsNot Nothing Then
                                result.AddRange(New MetadataExtractor.Formats.Exif.ExifReader().Extract(
                                    New MetadataExtractor.IO.ByteArrayReader(app1, ExifPreambleLength), ExifPreambleLength))
                            End If
                        End If
                        Dim hasCamera = result.OfType(Of MetadataExtractor.Formats.Exif.ExifIfd0Directory)().
                                        Any(Function(d) d.ContainsTag(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagModel))
                        If Not hasCamera Then
                            Dim properties = ReadProperties(stream, sections)
                            If properties IsNot Nothing Then result.AddRange(DirectoriesFromProperties(properties))
                        End If
                    End If
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("X3F.Metadata", ex)
            End Try
            Try
                result.Add(New MetadataExtractor.Formats.FileSystem.FileMetadataReader().Read(filePath))
            Catch
            End Try
            Return result
        End Function

#Region "Inhaltsverzeichnis"

        Private Structure SectionEntry
            Public Offset As Long
            Public Length As Long
            Public Kind As String
        End Structure

        ''' <summary>Die Eintraege des Inhaltsverzeichnisses, jeder vollstaendig innerhalb der Datei,
        ''' oder Nothing, wenn die Datei keine X3F ist oder ihr Verzeichnis nicht stimmt.</summary>
        Private Shared Function ReadDirectory(stream As Stream) As List(Of SectionEntry)
            Dim fileLength = stream.Length
            If fileLength < 16 Then Return Nothing
            Dim magic = ReadRange(stream, 0, 4)
            If magic Is Nothing OrElse Encoding.ASCII.GetString(magic) <> "FOVb" Then Return Nothing

            Dim tail = ReadRange(stream, fileLength - 4, 4)
            If tail Is Nothing Then Return Nothing
            Dim directoryOffset = CLng(BitConverter.ToUInt32(tail, 0))
            If directoryOffset < 4 OrElse directoryOffset + 12 > fileLength - 4 Then Return Nothing
            Dim head = ReadRange(stream, directoryOffset, 12)
            If head Is Nothing OrElse Encoding.ASCII.GetString(head, 0, 4) <> "SECd" Then Return Nothing
            Dim count = CLng(BitConverter.ToUInt32(head, 8))
            If count <= 0 OrElse count > MaxDirectoryEntries Then Return Nothing
            If directoryOffset + 12 + count * 12 > fileLength Then Return Nothing
            Dim table = ReadRange(stream, directoryOffset + 12, count * 12)
            If table Is Nothing Then Return Nothing

            Dim result As New List(Of SectionEntry)()
            For i = 0 To CInt(count) - 1
                Dim at = i * 12
                Dim offset = CLng(BitConverter.ToUInt32(table, at))
                Dim length = CLng(BitConverter.ToUInt32(table, at + 4))
                If length <= 0 OrElse offset < 4 OrElse offset + length > fileLength Then Continue For
                result.Add(New SectionEntry With {
                    .Offset = offset, .Length = length, .Kind = Encoding.ASCII.GetString(table, at + 8, 4)})
            Next
            Return result
        End Function

        Private Shared Function FindLargestPreview(stream As Stream) As PreviewSection
            Dim sections = ReadDirectory(stream)
            If sections Is Nothing Then Return Nothing
            Return FindLargestPreview(stream, sections, onlyJpeg:=False)
        End Function

        ''' <summary>Die groesste Vorschau, die sich lesen laesst. Bei gleicher Groesse das JPEG.</summary>
        Private Shared Function FindLargestPreview(stream As Stream, sections As List(Of SectionEntry), onlyJpeg As Boolean) As PreviewSection
            Dim best As PreviewSection = Nothing
            For Each entry In sections
                If entry.Kind <> "IMA2" AndAlso entry.Kind <> "IMAG" Then Continue For
                If entry.Length <= SectionHeaderSize + 2 Then Continue For
                Dim header = ReadRange(stream, entry.Offset, SectionHeaderSize + 2)
                If header Is Nothing OrElse Encoding.ASCII.GetString(header, 0, 4) <> "SECi" Then Continue For
                Dim type = BitConverter.ToUInt32(header, 8)
                Dim format = BitConverter.ToUInt32(header, 12)
                Dim width = BitConverter.ToUInt32(header, 16)
                Dim height = BitConverter.ToUInt32(header, 20)
                Dim rowStride = BitConverter.ToUInt32(header, 24)
                If type <> PreviewType Then Continue For
                If width = 0 OrElse height = 0 OrElse width > MaxEdge OrElse height > MaxEdge Then Continue For
                If CLng(width) * CLng(height) > MaxPixels Then Continue For
                Dim dataLength = entry.Length - SectionHeaderSize
                If dataLength > MaxPreviewBytes Then Continue For

                Dim candidate = New PreviewSection With {
                    .DataOffset = entry.Offset + SectionHeaderSize, .DataLength = dataLength,
                    .Width = CInt(width), .Height = CInt(height), .RowStride = 0}
                If header(SectionHeaderSize) = &HFF AndAlso header(SectionHeaderSize + 1) = &HD8 Then
                    candidate.Kind = PreviewKind.Jpeg
                ElseIf onlyJpeg Then
                    Continue For
                ElseIf format = FormatUncompressedRgb Then
                    If rowStride < width * 3UI OrElse CLng(rowStride) * (CLng(height) - 1) + CLng(width) * 3 > dataLength Then Continue For
                    candidate.Kind = PreviewKind.UncompressedRgb
                    candidate.RowStride = CInt(rowStride)
                ElseIf format = FormatHuffmanRgb Then
                    If dataLength <= HuffmanTableEntries * 4 Then Continue For
                    candidate.Kind = PreviewKind.HuffmanRgb
                Else
                    Continue For
                End If

                If best Is Nothing Then
                    best = candidate
                    Continue For
                End If
                Dim area = CLng(candidate.Width) * candidate.Height
                Dim bestArea = CLng(best.Width) * best.Height
                If area > bestArea OrElse (area = bestArea AndAlso candidate.Kind = PreviewKind.Jpeg AndAlso best.Kind <> PreviewKind.Jpeg) Then
                    best = candidate
                End If
            Next
            Return best
        End Function

        ''' <summary>Genau <paramref name="length"/> Bytes ab <paramref name="offset"/>, oder Nothing,
        ''' wenn der Bereich nicht ganz in der Datei liegt.</summary>
        Private Shared Function ReadRange(stream As Stream, offset As Long, length As Long) As Byte()
            If offset < 0 OrElse length < 0 OrElse length > MaxPreviewBytes OrElse offset + length > stream.Length Then Return Nothing
            Dim buffer = New Byte(CInt(length) - 1) {}
            stream.Position = offset
            Dim read = 0
            While read < buffer.Length
                Dim got = stream.Read(buffer, read, buffer.Length - read)
                If got <= 0 Then Return Nothing
                read += got
            End While
            Return buffer
        End Function

#End Region

#Region "Vorschau entschluesseln"

        ''' <summary>Das JPEG aufrecht (Drehung aus seinem EXIF) und in sRGB.</summary>
        Private Shared Function DecodeJpeg(bytes As Byte()) As SKBitmap
            Using data = SKData.CreateCopy(bytes)
                Using codec = SKCodec.Create(data)
                    If codec Is Nothing Then Return Nothing
                    Dim info = codec.Info
                    Dim sourceProfile = ColorManagementService.EffectiveProfile(info.ColorSpace, data)
                    Dim decodeInfo = New SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Opaque)
                    Dim decoded = New SKBitmap(decodeInfo)
                    Dim status = codec.GetPixels(decodeInfo, decoded.GetPixels())
                    If status <> SKCodecResult.Success AndAlso status <> SKCodecResult.IncompleteInput Then
                        decoded.Dispose()
                        Return Nothing
                    End If
                    Dim upright = ImageOrientationService.ApplyOrientation(decoded, codec.EncodedOrigin)
                    If Not Object.ReferenceEquals(upright, decoded) Then decoded.Dispose()
                    Dim managed = ColorManagementService.ToSrgb(upright, sourceProfile)
                    If Not Object.ReferenceEquals(managed, upright) Then upright.Dispose()
                    Return managed
                End Using
            End Using
        End Function

        Private Shared Function JpegOrientedSize(bytes As Byte()) As (Width As Integer, Height As Integer)
            If bytes Is Nothing Then Return (0, 0)
            Try
                Using data = SKData.CreateCopy(bytes)
                    Using codec = SKCodec.Create(data)
                        If codec Is Nothing Then Return (0, 0)
                        Select Case codec.EncodedOrigin
                            Case SKEncodedOrigin.LeftTop, SKEncodedOrigin.RightTop,
                                 SKEncodedOrigin.RightBottom, SKEncodedOrigin.LeftBottom
                                Return (codec.Info.Height, codec.Info.Width)
                            Case Else
                                Return (codec.Info.Width, codec.Info.Height)
                        End Select
                    End Using
                End Using
            Catch
                Return (0, 0)
            End Try
        End Function

        ''' <summary>Format 3: Zeile fuer Zeile Rot, Gruen, Blau.</summary>
        Private Shared Function DecodeUncompressed(bytes As Byte(), width As Integer, height As Integer, rowStride As Integer) As SKBitmap
            Dim pixels = New Byte(width * height * 4 - 1) {}
            For y = 0 To height - 1
                Dim source = y * rowStride
                Dim target = y * width * 4
                For x = 0 To width - 1
                    pixels(target) = bytes(source + 2)
                    pixels(target + 1) = bytes(source + 1)
                    pixels(target + 2) = bytes(source)
                    pixels(target + 3) = 255
                    source += 3
                    target += 4
                Next
            Next
            Return ToBitmap(pixels, width, height)
        End Function

        ''' <summary>Format 11: je Zeile und Kanal eine Differenzfolge, Huffman-gepackt. Die Bits
        ''' werden in Worten zu vier Bytes gelesen, das hoechste zuerst; jede Zeile beginnt mit einem
        ''' neuen Wort. Ging die vorige Zeile genau an einer Wortgrenze zu Ende, folgt ein leeres Wort,
        ''' das uebersprungen wird. Der Vorhersagewert beginnt je Zeile bei 0 und laeuft modulo 256.</summary>
        Private Shared Function DecodeHuffman(bytes As Byte(), width As Integer, height As Integer) As SKBitmap
            ' Code samt Laenge (oben fuenf Bit) auf den Wert. Bei doppelten Codes gilt der erste.
            Dim codes As New Dictionary(Of UInteger, Integer)()
            For i = 0 To HuffmanTableEntries - 1
                Dim entry = BitConverter.ToUInt32(bytes, i * 4)
                Dim length = CInt(entry >> 27)
                If length = 0 OrElse length > 26 Then Continue For
                If Not codes.ContainsKey(entry) Then codes(entry) = i
            Next
            If codes.Count = 0 Then Return Nothing

            Dim pixels = New Byte(width * height * 4 - 1) {}
            Dim position = HuffmanTableEntries * 4
            Dim word As UInteger = 0
            Dim bit = 1
            Dim prediction = New Integer(2) {}
            For y = 0 To height - 1
                prediction(0) = 0 : prediction(1) = 0 : prediction(2) = 0
                If bit = 0 Then position += 4
                bit = 0
                Dim target = y * width * 4
                For x = 0 To width - 1
                    For channel = 0 To 2
                        Dim code As UInteger = 0
                        Dim length = 0
                        Dim value As Integer
                        Do
                            bit = (bit - 1) And 31
                            If bit = 31 Then
                                If position + 4 > bytes.Length Then Return Nothing
                                word = (CUInt(bytes(position)) << 24) Or (CUInt(bytes(position + 1)) << 16) Or
                                       (CUInt(bytes(position + 2)) << 8) Or CUInt(bytes(position + 3))
                                position += 4
                            End If
                            code = (code << 1) Or ((word >> bit) And 1UI)
                            length += 1
                            If length > 26 Then Return Nothing
                        Loop Until codes.TryGetValue((CUInt(length) << 27) Or code, value)
                        prediction(channel) = (prediction(channel) + value) And 255
                    Next
                    pixels(target) = CByte(prediction(2))
                    pixels(target + 1) = CByte(prediction(1))
                    pixels(target + 2) = CByte(prediction(0))
                    pixels(target + 3) = 255
                    target += 4
                Next
            Next
            Return ToBitmap(pixels, width, height)
        End Function

        Private Shared Function ToBitmap(pixels As Byte(), width As Integer, height As Integer) As SKBitmap
            Dim bitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque))
            Dim rowBytes = bitmap.RowBytes
            Dim address = bitmap.GetPixels()
            For y = 0 To height - 1
                Marshal.Copy(pixels, y * width * 4, IntPtr.Add(address, y * rowBytes), width * 4)
            Next
            Return bitmap
        End Function

#End Region

#Region "Metadaten"

        ''' "Exif" und zwei Nullbytes vor dem TIFF-Kopf im APP1-Segment.
        Private Const ExifPreambleLength As Integer = 6

        ''' <summary>Der Inhalt des APP1-Segments mit "Exif" (ab der Kennung), oder Nothing. Geht die
        ''' Segmente vom Anfang bis zum Bildkopf durch.</summary>
        Private Shared Function FindExifSegment(jpeg As Byte()) As Byte()
            If jpeg Is Nothing OrElse jpeg.Length < 4 OrElse jpeg(0) <> &HFF OrElse jpeg(1) <> &HD8 Then Return Nothing
            Dim position = 2
            While position + 4 <= jpeg.Length
                If jpeg(position) <> &HFF Then Return Nothing
                Dim marker = jpeg(position + 1)
                ' Bildkopf oder Bilddaten: dahinter steht kein EXIF mehr.
                If marker = &HDA OrElse marker = &HD9 Then Return Nothing
                Dim length = (CInt(jpeg(position + 2)) << 8) Or CInt(jpeg(position + 3))
                If length < 2 OrElse position + 2 + length > jpeg.Length Then Return Nothing
                If marker = &HE1 AndAlso length - 2 > ExifPreambleLength AndAlso
                   Encoding.ASCII.GetString(jpeg, position + 4, 4) = "Exif" Then
                    Dim segment = New Byte(length - 3) {}
                    Array.Copy(jpeg, position + 4, segment, 0, segment.Length)
                    Return segment
                End If
                position += 2 + length
            End While
            Return Nothing
        End Function

        ''' <summary>Der Block "PROP" als Namen und Werte, oder Nothing. Aufbau: "SECp", Fassung,
        ''' Anzahl, Zeichenformat (0 = UTF-16), reserviert, Gesamtzahl der Zeichen; dann je Eintrag
        ''' der Abstand von Name und Wert in Zeichen, danach die Zeichen selbst, jedes Feld mit einer
        ''' Null abgeschlossen.</summary>
        Private Shared Function ReadProperties(stream As Stream, sections As List(Of SectionEntry)) As Dictionary(Of String, String)
            For Each entry In sections
                If entry.Kind <> "PROP" OrElse entry.Length < 24 OrElse entry.Length > 1024 * 1024 Then Continue For
                Dim block = ReadRange(stream, entry.Offset, entry.Length)
                If block Is Nothing OrElse Encoding.ASCII.GetString(block, 0, 4) <> "SECp" Then Continue For
                Dim count = CLng(BitConverter.ToUInt32(block, 8))
                Dim charFormat = BitConverter.ToUInt32(block, 12)
                If charFormat <> 0UI OrElse count <= 0 OrElse 24 + count * 8 > block.Length Then Continue For
                Dim textStart = 24 + CInt(count) * 8
                Dim result As New Dictionary(Of String, String)(StringComparer.Ordinal)
                For i = 0 To CInt(count) - 1
                    Dim name = ReadUtf16(block, textStart, BitConverter.ToUInt32(block, 24 + i * 8))
                    Dim value = ReadUtf16(block, textStart, BitConverter.ToUInt32(block, 28 + i * 8))
                    If name Is Nothing OrElse value Is Nothing OrElse name.Length = 0 Then Continue For
                    If Not result.ContainsKey(name) Then result(name) = value.Trim()
                Next
                Return result
            Next
            Return Nothing
        End Function

        Private Shared Function ReadUtf16(block As Byte(), textStart As Integer, charOffset As UInteger) As String
            Dim start = CLng(textStart) + CLng(charOffset) * 2
            If start < textStart OrElse start + 2 > block.Length Then Return Nothing
            Dim finish = start
            While finish + 1 < block.Length AndAlso (block(CInt(finish)) <> 0 OrElse block(CInt(finish) + 1) <> 0)
                finish += 2
            End While
            Return Encoding.Unicode.GetString(block, CInt(start), CInt(finish - start))
        End Function

        ''' <summary>EXIF-Verzeichnisse aus den Feldern von "PROP". TIME ist die Uhrzeit der Kamera als
        ''' Sekunden seit 1970, ohne Zone gezaehlt (an DP1S, DP1 und SD14 gegen das EXIF ihres JPEG
        ''' geprueft: dieselbe Uhrzeit). EXPTIME steht in Mikrosekunden.</summary>
        Private Shared Function DirectoriesFromProperties(properties As Dictionary(Of String, String)) As IEnumerable(Of MetadataExtractor.Directory)
            Dim ifd0 = New MetadataExtractor.Formats.Exif.ExifIfd0Directory()
            Dim exif = New MetadataExtractor.Formats.Exif.ExifSubIfdDirectory()
            Dim value As String = Nothing
            If properties.TryGetValue("CAMMANUF", value) AndAlso value.Length > 0 Then
                ifd0.Set(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagMake, value)
            End If
            If properties.TryGetValue("CAMMODEL", value) AndAlso value.Length > 0 Then
                ifd0.Set(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagModel, value)
            End If
            Dim seconds As Long
            If properties.TryGetValue("TIME", value) AndAlso Long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, seconds) AndAlso
               seconds > 0 AndAlso seconds < 253402300799L Then
                Dim clock = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy:MM:dd HH:mm:ss", CultureInfo.InvariantCulture)
                ifd0.Set(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagDateTime, clock)
                exif.Set(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagDateTimeOriginal, clock)
                exif.Set(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagDateTimeDigitized, clock)
            End If
            Dim number As Double
            If properties.TryGetValue("EXPTIME", value) AndAlso TryParseNumber(value, number) AndAlso number > 0 Then
                exif.Set(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagExposureTime,
                         New MetadataExtractor.Rational(CLng(Math.Round(number)), 1000000L))
            End If
            If properties.TryGetValue("APERTURE", value) AndAlso TryParseNumber(value, number) AndAlso number > 0 Then
                exif.Set(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagFNumber,
                         New MetadataExtractor.Rational(CLng(Math.Round(number * 10)), 10L))
            End If
            If properties.TryGetValue("ISO", value) AndAlso TryParseNumber(value, number) AndAlso number > 0 AndAlso number < 1000000 Then
                exif.Set(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagIsoEquivalent, CInt(Math.Round(number)))
            End If
            If properties.TryGetValue("FLENGTH", value) AndAlso TryParseNumber(value, number) AndAlso number > 0 Then
                exif.Set(MetadataExtractor.Formats.Exif.ExifDirectoryBase.TagFocalLength,
                         New MetadataExtractor.Rational(CLng(Math.Round(number * 10)), 10L))
            End If
            Dim result As New List(Of MetadataExtractor.Directory)()
            If ifd0.TagCount > 0 Then result.Add(ifd0)
            If exif.TagCount > 0 Then result.Add(exif)
            Return result
        End Function

        Private Shared Function TryParseNumber(text As String, ByRef number As Double) As Boolean
            Return Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, number) AndAlso
                   Not Double.IsNaN(number) AndAlso Not Double.IsInfinity(number)
        End Function

#End Region

    End Class

End Namespace
