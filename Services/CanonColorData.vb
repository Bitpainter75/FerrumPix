Imports System.IO

Namespace Services

    ''' <summary>Schwarz- und Weisspunkt, wie Canon sie je Aufnahme in eine CR3 schreibt.
    '''
    ''' WO SIE STEHEN. Eine CR3 ist ein ISO-Medienbehaelter. Neben dem Bild traegt sie eine Spur mit
    ''' Metadaten je Aufnahme (CTMD); darin liegen mehrere eingebettete TIFF-Bloecke, und einer davon
    ''' enthaelt Canons Herstellereintrag ColorData (0x4001), eine Reihe von 16-Bit-Worten. Das erste
    ''' Wort ist die FASSUNG des Aufbaus. Darin stehen je Kanal der Schwarzpunkt, gleich dahinter
    ''' NormalWhiteLevel und SpecularWhiteLevel; der zweite ist der Weisspunkt, den auch Adobe in
    ''' seine DNG uebernimmt.
    '''
    ''' WOZU. LibRaw liest diese Werte ebenfalls, erkennt die Fassung aber an der LAENGE des Blocks.
    ''' Die EOS R6 Mark III schreibt Fassung 66 mit derselben Laenge (3778 Worte) wie die R6 Mark II
    ''' ihre Fassung 48, in der alles 20 Worte frueher steht; LibRaw greift daneben. Gemessen an vier
    ''' Dateien: Schwarz 512 bzw. 2048 je nach Datei, SpecularWhiteLevel 13995 bzw. 14351 - der zweite
    ''' genau Adobes WhiteLevel. An welcher Stelle eine Fassung ihre Werte hat, steht in der
    ''' Kameratabelle (levelOverrides, Eintrag colorData); dieser Leser kennt keine Kamera selbst.
    '''
    ''' Er liest nur, was er braucht: den Kopf der Datei bis zur moov-Box, daraus die Lage der
    ''' Metadaten-Probe, und diese Probe - zusammen einige Dutzend Kilobyte.</summary>
    Public NotInheritable Class CanonColorData

        Private Sub New()
        End Sub

        Private Const ColorDataTag As Integer = &H4001
        Private Const MaxMoovBytes As Long = 16L * 1024 * 1024
        Private Const MaxSampleBytes As Long = 4L * 1024 * 1024

        ''' <summary>Schwarzpunkt (Mittel der vier Kanaele) und SpecularWhiteLevel, oder Nothing, wenn
        ''' die Datei keinen passenden Block traegt: keine CR3, andere Fassung, Worte ausserhalb,
        ''' oder Werte, die nicht zusammenpassen.</summary>
        Public Shared Function TryReadLevels(path As String, version As Integer, blackWord As Integer,
                                             whiteWord As Integer) As (Black As Integer, White As Integer)?
            Dim words = ReadColorData(path)
            If words Is Nothing OrElse words.Length = 0 OrElse words(0) <> version Then Return Nothing
            Return LevelsFromWords(words, blackWord, whiteWord)
        End Function

        ''' <summary>Schwarzpunkt (Mittel der vier Kanaele) und SpecularWhiteLevel aus bereits
        ''' gelesenen Worten, ohne die Fassung zu vergleichen. Plausibel nur, wenn die vier Kanaele
        ''' eng beieinander liegen und der Weisspunkt deutlich darueber.</summary>
        Public Shared Function LevelsFromWords(words As UShort(), blackWord As Integer, whiteWord As Integer) As (Black As Integer, White As Integer)?
            If words Is Nothing OrElse blackWord < 1 OrElse blackWord + 3 >= words.Length OrElse whiteWord < 1 OrElse whiteWord >= words.Length Then Return Nothing
            Dim channels = Enumerable.Range(blackWord, 4).Select(Function(i) CInt(words(i))).ToArray()
            Dim black = CInt(Math.Round(channels.Average()))
            Dim white = CInt(words(whiteWord))
            If channels.Max() - channels.Min() > 64 OrElse black <= 0 OrElse white <= black + 1024 OrElse white > 65535 Then Return Nothing
            Return (black, white)
        End Function

        ''' <summary>Die Worte von ColorData, oder Nothing. Bei einer CR3 aus der CTMD-Spur, bei einer
        ''' CR2 aus dem Herstellerblock der EXIF-Daten.</summary>
        Public Shared Function ReadColorData(path As String) As UShort()
            If String.IsNullOrEmpty(path) Then Return Nothing
            If path.EndsWith(".cr2", StringComparison.OrdinalIgnoreCase) Then Return ReadColorDataCr2(path)
            If Not path.EndsWith(".cr3", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Try
                Using stream As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Dim moov = ReadTopLevelBox(stream, "moov")
                    If moov Is Nothing Then Return Nothing
                    Dim location = FindCtmdSample(moov)
                    If location Is Nothing Then Return Nothing
                    Dim offset = location.Value.Offset
                    Dim size = location.Value.Size
                    If size <= 0 OrElse size > MaxSampleBytes OrElse offset + size > stream.Length Then Return Nothing
                    Dim sample(CInt(size) - 1) As Byte
                    stream.Position = offset
                    stream.ReadExactly(sample, 0, sample.Length)
                    Return FindColorData(sample)
                End Using
            Catch
                Return Nothing
            End Try
        End Function

        ''' Wie weit eine CR2 gelesen wird: der Herstellerblock steht bei allen gemessenen Dateien in
        ''' den ersten Kilobytes, weit vor den Bilddaten.
        Private Const Cr2HeadBytes As Integer = 2 * 1024 * 1024

        ' CR2: TIFF (Intel) -> IFD0 -> EXIF (0x8769) -> MakerNote (0x927C) -> ColorData (0x4001).
        ' Canons Herstellerblock ist ein Verzeichnis ohne eigenen Kopf, seine Verweise zaehlen ab dem
        ' Anfang der Datei.
        Private Shared Function ReadColorDataCr2(path As String) As UShort()
            Try
                Dim head As Byte()
                Using stream As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    ReDim head(CInt(Math.Min(stream.Length, Cr2HeadBytes)) - 1)
                    stream.ReadExactly(head, 0, head.Length)
                End Using
                If head.Length < 16 OrElse head(0) <> &H49 OrElse head(1) <> &H49 Then Return Nothing
                Dim ifd0 = CInt(BitConverter.ToUInt32(head, 4))
                Dim exif = EntryValue(head, ifd0, &H8769)
                If exif <= 0 Then Return Nothing
                Dim makerNote = EntryValue(head, exif, &H927C)
                If makerNote <= 0 Then Return Nothing
                Return ReadTagAt(head, 0, makerNote)
            Catch
                Return Nothing
            End Try
        End Function

        ' Der Wert (bzw. Verweis) eines Eintrags in einem Verzeichnis, oder -1.
        Private Shared Function EntryValue(data As Byte(), ifd As Integer, tag As Integer) As Integer
            If ifd <= 0 OrElse ifd + 2 > data.Length Then Return -1
            Dim count = BitConverter.ToUInt16(data, ifd)
            For i = 0 To count - 1
                Dim entry = ifd + 2 + 12 * i
                If entry + 12 > data.Length Then Return -1
                If BitConverter.ToUInt16(data, entry) = tag Then Return CInt(BitConverter.ToUInt32(data, entry + 8))
            Next
            Return -1
        End Function

        ' Liest eine Box der obersten Ebene ganz in den Speicher.
        Private Shared Function ReadTopLevelBox(stream As Stream, wanted As String) As Byte()
            Dim header(15) As Byte
            Dim position As Long = 0
            While position + 8 <= stream.Length
                stream.Position = position
                stream.ReadExactly(header, 0, 8)
                Dim size As Long = BigEndian32(header, 0)
                Dim kind = Text.Encoding.ASCII.GetString(header, 4, 4)
                Dim headerSize = 8
                If size = 1 Then
                    stream.ReadExactly(header, 8, 8)
                    size = CLng(BigEndian64(header, 8))
                    headerSize = 16
                ElseIf size = 0 Then
                    size = stream.Length - position
                End If
                If size < headerSize Then Return Nothing
                If kind = wanted Then
                    If size - headerSize > MaxMoovBytes Then Return Nothing
                    Dim content(CInt(size - headerSize) - 1) As Byte
                    stream.ReadExactly(content, 0, content.Length)
                    Return content
                End If
                position += size
            End While
            Return Nothing
        End Function

        ' Die Kinder einer Box im Speicher: Art, Anfang des Inhalts, Ende.
        Private Shared Iterator Function Children(data As Byte(), start As Integer, [end] As Integer) As IEnumerable(Of (Kind As String, Start As Integer, [End] As Integer))
            Dim position = start
            While position + 8 <= [end]
                Dim size As Long = BigEndian32(data, position)
                Dim headerSize = 8
                If size = 1 Then
                    size = CLng(BigEndian64(data, position + 8))
                    headerSize = 16
                ElseIf size = 0 Then
                    size = [end] - position
                End If
                If size < headerSize OrElse position + size > [end] Then Exit Function
                Yield (Text.Encoding.ASCII.GetString(data, position + 4, 4), position + headerSize, CInt(position + size))
                position += CInt(size)
            End While
        End Function

        ' Die Spur, deren Beschreibung "CTMD" nennt, und die Lage ihrer ersten Probe.
        Private Shared Function FindCtmdSample(moov As Byte()) As (Offset As Long, Size As Long)?
            For Each trak In Children(moov, 0, moov.Length).Where(Function(b) b.Kind = "trak")
                Dim found As New Dictionary(Of String, (Start As Integer, [End] As Integer))
                Dim walk As Action(Of Integer, Integer) = Nothing
                walk = Sub(a, b)
                           For Each child In Children(moov, a, b)
                               Select Case child.Kind
                                   Case "mdia", "minf", "stbl" : walk(child.Start, child.End)
                                   Case "stsd", "stco", "co64", "stsz" : found(child.Kind) = (child.Start, child.End)
                               End Select
                           Next
                       End Sub
                walk(trak.Start, trak.End)
                If Not found.ContainsKey("stsd") OrElse Not found.ContainsKey("stsz") Then Continue For
                Dim stsd = found("stsd")
                If Text.Encoding.ASCII.GetString(moov, stsd.Start, stsd.End - stsd.Start).IndexOf("CTMD", StringComparison.Ordinal) < 0 Then Continue For
                Dim offset As Long
                If found.ContainsKey("co64") Then
                    offset = CLng(BigEndian64(moov, found("co64").Start + 8))
                ElseIf found.ContainsKey("stco") Then
                    offset = BigEndian32(moov, found("stco").Start + 8)
                Else
                    Continue For
                End If
                ' stsz: Version und Flags, einheitliche Groesse, Anzahl, danach die Einzelgroessen.
                Dim stsz = found("stsz")
                Dim size As Long = BigEndian32(moov, stsz.Start + 4)
                If size = 0 Then size = BigEndian32(moov, stsz.Start + 12)
                Return (offset, size)
            Next
            Return Nothing
        End Function

        ' Sucht in der Metadaten-Probe den TIFF-Block mit ColorData und gibt dessen Worte zurueck.
        Private Shared Function FindColorData(sample As Byte()) As UShort()
            Dim position = 0
            While position + 8 <= sample.Length
                position = IndexOfTiff(sample, position)
                If position < 0 Then Return Nothing
                Dim words = ReadTag(sample, position)
                If words IsNot Nothing Then Return words
                position += 4
            End While
            Return Nothing
        End Function

        Private Shared Function IndexOfTiff(data As Byte(), start As Integer) As Integer
            For i = start To data.Length - 4
                If data(i) = &H49 AndAlso data(i + 1) = &H49 AndAlso data(i + 2) = &H2A AndAlso data(i + 3) = 0 Then Return i
            Next
            Return -1
        End Function

        ' Liest den Eintrag 0x4001 aus dem ersten Verzeichnis eines TIFF-Blocks (Intel-Reihenfolge).
        Private Shared Function ReadTag(data As Byte(), tiff As Integer) As UShort()
            Return ReadTagAt(data, tiff, tiff + CInt(BitConverter.ToUInt32(data, tiff + 4)))
        End Function

        ' Den Eintrag 0x4001 aus dem Verzeichnis bei ifd; Verweise zaehlen ab tiff.
        Private Shared Function ReadTagAt(data As Byte(), tiff As Integer, ifd As Integer) As UShort()
            If ifd < tiff OrElse ifd + 2 > data.Length Then Return Nothing
            Dim count = BitConverter.ToUInt16(data, ifd)
            For i = 0 To count - 1
                Dim entry = ifd + 2 + 12 * i
                If entry + 12 > data.Length Then Return Nothing
                If BitConverter.ToUInt16(data, entry) <> ColorDataTag Then Continue For
                If BitConverter.ToUInt16(data, entry + 2) <> 3 Then Return Nothing ' SHORT
                Dim n = CLng(BitConverter.ToUInt32(data, entry + 4))
                Dim valueOffset = tiff + CLng(BitConverter.ToUInt32(data, entry + 8))
                If n <= 0 OrElse n > 100000 OrElse valueOffset + 2 * n > data.Length Then Return Nothing
                Dim words(CInt(n) - 1) As UShort
                For w = 0 To CInt(n) - 1
                    words(w) = BitConverter.ToUInt16(data, CInt(valueOffset) + 2 * w)
                Next
                Return words
            Next
            Return Nothing
        End Function

        Private Shared Function BigEndian32(data As Byte(), offset As Integer) As Long
            Return (CLng(data(offset)) << 24) Or (CLng(data(offset + 1)) << 16) Or (CLng(data(offset + 2)) << 8) Or data(offset + 3)
        End Function

        Private Shared Function BigEndian64(data As Byte(), offset As Integer) As ULong
            Dim value As ULong = 0
            For i = 0 To 7
                value = (value << 8) Or data(offset + i)
            Next
            Return value
        End Function

    End Class

End Namespace
