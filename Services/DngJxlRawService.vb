Imports System
Imports System.Buffers.Binary
Imports System.Collections.Generic
Imports System.IO

Namespace Services

    ''' <summary>Die Rohdaten eines DNG 1.7 mit JPEG-XL-Kompression (52546) entwickelbar machen.
    '''
    ''' LibRaw 0.22 hat fuer diese Kompression nur einen Platzhalter und dekodiert sie nicht. Der Weg
    ''' hier: die Datei bleibt Byte fuer Byte erhalten, die JPEG-XL-Teilstuecke der Rohdaten werden
    ''' dekodiert und als unkomprimierte 16-Bit-Werte HINTEN angehaengt, und in der Rohdaten-IFD
    ''' werden nur Compression, die Offset- und Laengenliste und OpcodeList2 umgebogen. Das Ergebnis
    ''' liegt im Speicher und geht ueber libraw_open_dng an LibRaw; geschrieben wird nichts. Alle
    ''' anderen Angaben (Farbmatrizen, Pegel, Weissabgleich, Objektiv) liest LibRaw also aus
    ''' denselben Bytes wie sonst, die Farbkette bleibt bei ihr.
    '''
    ''' DIE KURVE VOR DER KOMPRESSION: ein verlustbehaftet gespeichertes DNG traegt nicht die
    ''' linearen Werte, sondern eine gestauchte Fassung (etwa die Kubikwurzel), damit die Kompression
    ''' in den Schatten nicht zu grob wird. Zurueck fuehrt ein MapPolynomial (Opcode 8) in
    ''' OpcodeList2, je Ebene eins. LibRaw wendet OpcodeList2 nicht an; ohne das Polynom kam die
    ''' Sony-Datei aus Issue 83 rosa und viel zu hell heraus. Das Polynom wird deshalb hier
    ''' eingerechnet und aus der Liste genommen, damit eine spaetere LibRaw es nicht ein zweites Mal
    ''' anwendet. Es wirkt nach der Regel der Spezifikation auf (Wert - Schwarz) / (Weiss - Schwarz),
    ''' das Ergebnis wird auf 0 bis 1 begrenzt und auf dieselbe Skala zurueckgerechnet.
    '''
    ''' WAS GEHT: LinearRaw (34892) mit drei Kanaelen und 16 Bit, in Kacheln oder Streifen, so wie
    ''' der Konverter des Referenzprogramms und Telefone (Samsung) sie schreiben. Mit Farbfilter-Rohdaten (32803) ist
    ''' JPEG XL laut Spezifikation nicht vorgesehen. Alles, was nicht passt, liefert Nothing, und es
    ''' bleibt bei der eingebetteten Vorschau wie bisher: ein Schwarzpegel mit Muster oder Deltas
    ''' zusammen mit einem Polynom, ein Polynom hinter einem anderen Opcode (die Reihenfolge waere
    ''' dann eine andere), eine LinearizationTable, eine andere Bittiefe.
    '''
    ''' Die Werte kommen aus libjxl als Gleitkomma in der Kodierung des Datenstroms
    ''' (<see cref="JxlDecodeService.TryDecodeFloat"/>), also genau so, wie sie vor der Kompression
    ''' dastanden. Ein Farbmanagement waere hier falsch: es saehe in den Rohwerten Farben.</summary>
    Public NotInheritable Class DngJxlRawService

        Private Const CompressionJpegXl As Integer = 52546
        Private Const PhotometricLinearRaw As Integer = 34892
        Private Const OpcodeMapPolynomial As Integer = 8

        Private Const TagNewSubfileType As Integer = 254
        Private Const TagWidth As Integer = 256
        Private Const TagHeight As Integer = 257
        Private Const TagBitsPerSample As Integer = 258
        Private Const TagCompression As Integer = 259
        Private Const TagPhotometric As Integer = 262
        Private Const TagStripOffsets As Integer = 273
        Private Const TagSamplesPerPixel As Integer = 277
        Private Const TagRowsPerStrip As Integer = 278
        Private Const TagStripByteCounts As Integer = 279
        Private Const TagPlanarConfiguration As Integer = 284
        Private Const TagTileWidth As Integer = 322
        Private Const TagTileLength As Integer = 323
        Private Const TagTileOffsets As Integer = 324
        Private Const TagTileByteCounts As Integer = 325
        Private Const TagSubIfds As Integer = 330
        Private Const TagSampleFormat As Integer = 339
        Private Const TagLinearizationTable As Integer = 50712
        Private Const TagBlackLevelRepeatDim As Integer = 50713
        Private Const TagBlackLevel As Integer = 50714
        Private Const TagBlackLevelDeltaH As Integer = 50715
        Private Const TagBlackLevelDeltaV As Integer = 50716
        Private Const TagWhiteLevel As Integer = 50717
        Private Const TagActiveArea As Integer = 50829
        Private Const TagOpcodeList2 As Integer = 51009

        Private Const TypeShort As Integer = 3
        Private Const TypeLong As Integer = 4
        Private Const TypeUndefined As Integer = 7

        ' Obergrenzen gegen kaputte Dateien.
        Private Const MaxIfds As Integer = 64
        Private Const MaxEntries As Integer = 1000
        Private Const MaxSegments As Integer = 100000
        Private Const MaxPolynomialDegree As Integer = 8

        Private Sub New()
        End Sub

        ''' <summary>Ein Eintrag einer IFD: wo er in der Datei steht und was er traegt.</summary>
        Private NotInheritable Class Entry
            Public Position As Long
            Public Type As Integer
            Public Count As Long
        End Class

        ''' <summary>Ein MapPolynomial aus OpcodeList2, Koordinaten relativ zur ActiveArea.</summary>
        Private NotInheritable Class Polynomial
            Public Top, Left, Bottom, Right As Integer
            Public Plane, Planes As Integer
            Public RowPitch, ColPitch As Integer
            Public Coefficients As Double()
        End Class

        ''' <summary>Was es zum Umbau braucht, gelesen vor dem Einlesen der ganzen Datei.</summary>
        Private NotInheritable Class Layout
            Public Little As Boolean
            Public Width, Height, Samples As Integer
            Public Tiled As Boolean
            Public PieceWidth, PieceHeight As Integer
            Public Offsets As Long()
            Public Counts As Long()
            Public CompressionEntry, OffsetsEntry, CountsEntry As Entry
            Public OpcodeEntry As Entry
            Public Polynomials As New List(Of Polynomial)()
            ''' <summary>Die Opcodes, die stehen bleiben, schon fertig als Big-Endian-Bloecke.</summary>
            Public KeptOpcodes As New List(Of Byte())()
            Public Black As Double()
            Public White As Double()
            Public ActiveTop, ActiveLeft, ActiveBottom, ActiveRight As Integer
        End Class

        ''' <summary>Das DNG mit unkomprimierten Rohdaten, im Speicher, oder Nothing, wenn die Datei
        ''' keine JPEG-XL-Rohdaten traegt oder sie sich nach den Regeln oben nicht umbauen laesst.
        ''' Fuer jedes andere DNG endet der Weg nach den Verzeichnissen.</summary>
        Public Shared Function TryBuildUncompressed(path As String) As Byte()
            If Not IsDngPath(path) Then Return Nothing
            Try
                Dim clock = Diagnostics.Stopwatch.StartNew()
                Dim result As Byte()
                Using fs As New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Dim plan = ReadLayout(fs)
                    If plan Is Nothing Then Return Nothing
                    If Not JxlDecodeService.IsAvailable Then
                        DiagnosticLogService.LogAlways("DngJxlRaw", $"{IO.Path.GetFileName(path)}: JPEG XL in den Rohdaten, aber kein Dekoder geladen")
                        Return Nothing
                    End If
                    result = Build(fs, plan, path)
                    If result Is Nothing Then Return Nothing
                    DiagnosticLogService.LogAlways("DngJxlRaw",
                        $"file={IO.Path.GetFileName(path)}; {plan.Width}x{plan.Height}; pieces={plan.Offsets.Length}; polynomials={plan.Polynomials.Count}; {clock.ElapsedMilliseconds} ms")
                End Using
                Return result
            Catch ex As Exception
                DiagnosticLogService.LogException("DngJxlRaw", ex)
                Return Nothing
            End Try
        End Function

        Private Shared Function IsDngPath(path As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(path) AndAlso path.EndsWith(".dng", StringComparison.OrdinalIgnoreCase)
        End Function

        ' Lesen der Verzeichnisse

        ''' <summary>Die IFD der Rohdaten (NewSubFileType 0) aus IFD0, ihren SubIFDs und der Kette.</summary>
        Private Shared Function FindRawIfd(fs As FileStream, ByRef little As Boolean) As Dictionary(Of Integer, Entry)
            Dim header = ReadAt(fs, 0, 8)
            If header Is Nothing Then Return Nothing
            If header(0) = &H49 AndAlso header(1) = &H49 Then
                little = True
            ElseIf header(0) = &H4D AndAlso header(1) = &H4D Then
                little = False
            Else
                Return Nothing
            End If
            If ReadU16(header, 0 + 2, little) <> 42 Then Return Nothing

            Dim pending As New Queue(Of Long)()
            Dim seen As New HashSet(Of Long)()
            pending.Enqueue(ReadU32(header, 4, little))
            Do While pending.Count > 0 AndAlso seen.Count < MaxIfds
                Dim offset = pending.Dequeue()
                If offset <= 0 OrElse offset >= fs.Length OrElse Not seen.Add(offset) Then Continue Do
                Dim nextIfd As Long = 0
                Dim entries = ReadIfd(fs, offset, little, nextIfd)
                If entries Is Nothing Then Continue Do
                If nextIfd > 0 Then pending.Enqueue(nextIfd)
                Dim subs = Values(fs, entries, TagSubIfds, little)
                If subs IsNot Nothing Then
                    For Each s In subs
                        pending.Enqueue(CLng(s))
                    Next
                End If
                ' Ohne den Eintrag gilt 0 (TIFF), Vorschauen tragen ihn immer.
                If FirstValue(fs, entries, TagNewSubfileType, little, 0) = 0 Then Return entries
            Loop
            Return Nothing
        End Function

        Private Shared Function ReadIfd(fs As FileStream, offset As Long, little As Boolean, ByRef nextIfd As Long) As Dictionary(Of Integer, Entry)
            Dim countBytes = ReadAt(fs, offset, 2)
            If countBytes Is Nothing Then Return Nothing
            Dim count = ReadU16(countBytes, 0, little)
            If count <= 0 OrElse count > MaxEntries Then Return Nothing
            Dim raw = ReadAt(fs, offset + 2, count * 12 + 4)
            If raw Is Nothing Then Return Nothing
            nextIfd = ReadU32(raw, count * 12, little)
            Dim result As New Dictionary(Of Integer, Entry)()
            For i = 0 To count - 1
                Dim e = i * 12
                result(ReadU16(raw, e, little)) = New Entry With {
                    .Position = offset + 2 + e,
                    .Type = ReadU16(raw, e + 2, little),
                    .Count = ReadU32(raw, e + 4, little)}
            Next
            Return result
        End Function

        ''' <summary>Alle Werte eines Eintrags als Zahl: BYTE, SHORT, LONG, IFD und die rationalen
        ''' Typen. Nothing, wenn der Eintrag fehlt oder einen anderen Typ hat.</summary>
        Private Shared Function Values(fs As FileStream, entries As Dictionary(Of Integer, Entry), tag As Integer, little As Boolean) As Double()
            Dim entry As Entry = Nothing
            If Not entries.TryGetValue(tag, entry) Then Return Nothing
            Dim size = TypeSize(entry.Type)
            If size = 0 OrElse entry.Count <= 0 OrElse entry.Count > MaxSegments Then Return Nothing
            Dim raw = ValueBytes(fs, entry, little)
            If raw Is Nothing Then Return Nothing
            Dim n = CInt(entry.Count)
            Dim result(n - 1) As Double
            For i = 0 To n - 1
                Dim o = i * size
                Select Case entry.Type
                    Case 1 : result(i) = raw(o)
                    Case 3 : result(i) = ReadU16(raw, o, little)
                    Case 4, 13 : result(i) = ReadU32(raw, o, little)
                    Case 5
                        Dim d = ReadU32(raw, o + 4, little)
                        result(i) = If(d = 0, 0.0, ReadU32(raw, o, little) / CDbl(d))
                    Case 10
                        Dim d = Signed32(ReadU32(raw, o + 4, little))
                        result(i) = If(d = 0, 0.0, Signed32(ReadU32(raw, o, little)) / CDbl(d))
                    Case 11 : result(i) = BitConverter.Int32BitsToSingle(Signed32(ReadU32(raw, o, little)))
                    Case 12 : result(i) = BitConverter.Int64BitsToDouble(ReadU64(raw, o, little))
                End Select
            Next
            Return result
        End Function

        ''' <summary>Vier Byte, gelesen als vorzeichenlose Zahl, als Int32 mit Vorzeichen.</summary>
        Private Shared Function Signed32(value As Long) As Integer
            Return CInt(If(value > Integer.MaxValue, value - 4294967296L, value))
        End Function

        Private Shared Function TypeSize(type As Integer) As Integer
            Select Case type
                Case 1, 7 : Return 1
                Case 3 : Return 2
                Case 4, 11, 13 : Return 4
                Case 5, 10, 12 : Return 8
                Case Else : Return 0
            End Select
        End Function

        ''' <summary>Die rohen Wertbytes eines Eintrags: im Wertfeld, wenn sie hineinpassen, sonst an
        ''' der Stelle, auf die es zeigt.</summary>
        Private Shared Function ValueBytes(fs As FileStream, entry As Entry, little As Boolean) As Byte()
            Dim total = entry.Count * TypeSize(entry.Type)
            If total <= 0 OrElse total > Integer.MaxValue Then Return Nothing
            If total <= 4 Then Return ReadAt(fs, entry.Position + 8, CInt(total))
            Dim pointer = ReadAt(fs, entry.Position + 8, 4)
            If pointer Is Nothing Then Return Nothing
            Return ReadAt(fs, ReadU32(pointer, 0, little), CInt(total))
        End Function

        Private Shared Function FirstValue(fs As FileStream, entries As Dictionary(Of Integer, Entry), tag As Integer,
                                           little As Boolean, fallback As Double) As Double
            Dim v = Values(fs, entries, tag, little)
            Return If(v Is Nothing OrElse v.Length = 0, fallback, v(0))
        End Function

        ''' <summary>Prueft die Rohdaten-IFD gegen die Regeln im Klassenkommentar und sammelt, was der
        ''' Umbau braucht. Nothing heisst: nichts zu tun oder nicht sicher machbar.</summary>
        Private Shared Function ReadLayout(fs As FileStream) As Layout
            Dim little As Boolean
            Dim raw = FindRawIfd(fs, little)
            If raw Is Nothing Then Return Nothing
            If FirstValue(fs, raw, TagCompression, little, 1) <> CompressionJpegXl Then Return Nothing

            Dim plan As New Layout With {.Little = little}
            plan.Width = CInt(FirstValue(fs, raw, TagWidth, little, 0))
            plan.Height = CInt(FirstValue(fs, raw, TagHeight, little, 0))
            plan.Samples = CInt(FirstValue(fs, raw, TagSamplesPerPixel, little, 1))
            If plan.Width <= 0 OrElse plan.Height <= 0 Then Return Nothing
            If FirstValue(fs, raw, TagPhotometric, little, 0) <> PhotometricLinearRaw OrElse plan.Samples <> 3 Then
                Return Refuse("nur LinearRaw mit drei Kanaelen")
            End If
            If FirstValue(fs, raw, TagPlanarConfiguration, little, 1) <> 1 Then Return Refuse("Ebenen getrennt gespeichert")
            Dim sampleFormat = Values(fs, raw, TagSampleFormat, little)
            If sampleFormat IsNot Nothing AndAlso Array.Exists(sampleFormat, Function(v) v <> 1) Then Return Refuse("keine vorzeichenlosen Ganzzahlen")
            Dim bits = Values(fs, raw, TagBitsPerSample, little)
            If bits Is Nothing OrElse Array.Exists(bits, Function(v) v <> 16) Then Return Refuse("nicht 16 Bit")
            If raw.ContainsKey(TagLinearizationTable) Then Return Refuse("LinearizationTable")

            plan.Tiled = raw.ContainsKey(TagTileWidth) AndAlso raw.ContainsKey(TagTileLength)
            If plan.Tiled Then
                plan.PieceWidth = CInt(FirstValue(fs, raw, TagTileWidth, little, 0))
                plan.PieceHeight = CInt(FirstValue(fs, raw, TagTileLength, little, 0))
                plan.OffsetsEntry = raw(TagTileOffsets)
                plan.CountsEntry = If(raw.ContainsKey(TagTileByteCounts), raw(TagTileByteCounts), Nothing)
            Else
                plan.PieceWidth = plan.Width
                plan.PieceHeight = CInt(Math.Min(plan.Height, FirstValue(fs, raw, TagRowsPerStrip, little, plan.Height)))
                plan.OffsetsEntry = If(raw.ContainsKey(TagStripOffsets), raw(TagStripOffsets), Nothing)
                plan.CountsEntry = If(raw.ContainsKey(TagStripByteCounts), raw(TagStripByteCounts), Nothing)
            End If
            If plan.PieceWidth <= 0 OrElse plan.PieceHeight <= 0 OrElse plan.OffsetsEntry Is Nothing OrElse plan.CountsEntry Is Nothing Then Return Nothing
            Dim offsets = Values(fs, raw, If(plan.Tiled, TagTileOffsets, TagStripOffsets), little)
            Dim counts = Values(fs, raw, If(plan.Tiled, TagTileByteCounts, TagStripByteCounts), little)
            If offsets Is Nothing OrElse counts Is Nothing OrElse offsets.Length <> counts.Length Then Return Nothing
            Dim across = (plan.Width + plan.PieceWidth - 1) \ plan.PieceWidth
            Dim down = (plan.Height + plan.PieceHeight - 1) \ plan.PieceHeight
            If offsets.Length <> across * down Then Return Refuse("Zahl der Teilstuecke passt nicht zur Groesse")
            plan.Offsets = Array.ConvertAll(offsets, Function(v) CLng(v))
            plan.Counts = Array.ConvertAll(counts, Function(v) CLng(v))
            plan.CompressionEntry = raw(TagCompression)

            ' Pegel je Ebene: ohne Eintrag gilt Schwarz 0 und Weiss 2^16 - 1.
            Dim black = Values(fs, raw, TagBlackLevel, little)
            Dim white = Values(fs, raw, TagWhiteLevel, little)
            plan.Black = New Double(plan.Samples - 1) {}
            plan.White = New Double(plan.Samples - 1) {}
            For c = 0 To plan.Samples - 1
                plan.Black(c) = If(black Is Nothing OrElse black.Length = 0, 0.0, black(Math.Min(c, black.Length - 1)))
                plan.White(c) = If(white Is Nothing OrElse white.Length = 0, 65535.0, white(Math.Min(c, white.Length - 1)))
            Next
            Dim blackPattern = Values(fs, raw, TagBlackLevelRepeatDim, little)
            Dim simpleBlack = (blackPattern Is Nothing OrElse (blackPattern.Length = 2 AndAlso blackPattern(0) = 1 AndAlso blackPattern(1) = 1)) AndAlso
                              Not raw.ContainsKey(TagBlackLevelDeltaH) AndAlso Not raw.ContainsKey(TagBlackLevelDeltaV) AndAlso
                              (black Is Nothing OrElse black.Length = 1 OrElse black.Length = plan.Samples)

            Dim area = Values(fs, raw, TagActiveArea, little)
            If area IsNot Nothing AndAlso area.Length = 4 Then
                plan.ActiveTop = CInt(area(0)) : plan.ActiveLeft = CInt(area(1))
                plan.ActiveBottom = CInt(area(2)) : plan.ActiveRight = CInt(area(3))
            Else
                plan.ActiveBottom = plan.Height : plan.ActiveRight = plan.Width
            End If

            If raw.ContainsKey(TagOpcodeList2) Then
                plan.OpcodeEntry = raw(TagOpcodeList2)
                If Not ReadOpcodes(ValueBytes(fs, plan.OpcodeEntry, little), plan) Then Return Nothing
                If plan.Polynomials.Count > 0 AndAlso Not simpleBlack Then Return Refuse("Polynom mit Schwarzpegel-Muster")
            End If
            Return plan
        End Function

        ''' <summary>Liest OpcodeList2 (immer Big Endian). Die MapPolynomial am ANFANG der Liste werden
        ''' eingerechnet, alles danach bleibt stehen. Ein Polynom hinter einem anderen Opcode haette
        ''' eine andere Reihenfolge und fuehrt zur Ablehnung.</summary>
        Private Shared Function ReadOpcodes(list As Byte(), plan As Layout) As Boolean
            If list Is Nothing OrElse list.Length < 4 Then Return True
            Dim count = BigU32(list, 0)
            Dim pos = 4
            Dim prefix = True
            For i = 0L To count - 1
                If pos + 16 > list.Length Then Return Refuse("OpcodeList2 abgeschnitten") IsNot Nothing
                Dim id = BigU32(list, pos)
                Dim size = BigU32(list, pos + 12)
                If pos + 16 + size > list.Length Then Return Refuse("OpcodeList2 abgeschnitten") IsNot Nothing
                If id = OpcodeMapPolynomial Then
                    If Not prefix Then Return Refuse("Polynom hinter einem anderen Opcode") IsNot Nothing
                    Dim p = ReadPolynomial(list, pos + 16, CInt(size))
                    If p Is Nothing Then Return Refuse("Polynom nicht lesbar") IsNot Nothing
                    plan.Polynomials.Add(p)
                Else
                    prefix = False
                    Dim block(CInt(16 + size) - 1) As Byte
                    Buffer.BlockCopy(list, pos, block, 0, block.Length)
                    plan.KeptOpcodes.Add(block)
                End If
                pos += CInt(16 + size)
            Next
            Return True
        End Function

        ''' <summary>Die Parameter eines MapPolynomial ab <paramref name="at"/>: Bereich, Ebenen,
        ''' Raster, Grad und die Koeffizienten als Double, alles Big Endian.</summary>
        Private Shared Function ReadPolynomial(list As Byte(), at As Integer, size As Integer) As Polynomial
            If size < 36 Then Return Nothing
            Dim u = Function(o As Integer) CInt(Math.Min(Integer.MaxValue, BigU32(list, at + o)))
            Dim result As New Polynomial With {
                .Top = u(0), .Left = u(4), .Bottom = u(8), .Right = u(12),
                .Plane = u(16), .Planes = u(20), .RowPitch = u(24), .ColPitch = u(28)}
            Dim degree = u(32)
            If degree < 0 OrElse degree > MaxPolynomialDegree OrElse size < 36 + 8 * (degree + 1) Then Return Nothing
            If result.RowPitch <= 0 OrElse result.ColPitch <= 0 OrElse result.Planes <= 0 Then Return Nothing
            result.Coefficients = New Double(degree) {}
            For k = 0 To degree
                Dim bits = (BigU32(list, at + 36 + 8 * k) << 32) Or BigU32(list, at + 40 + 8 * k)
                result.Coefficients(k) = BitConverter.Int64BitsToDouble(bits)
                If Double.IsNaN(result.Coefficients(k)) OrElse Double.IsInfinity(result.Coefficients(k)) Then Return Nothing
            Next
            Return result
        End Function

        ''' <summary>Vier Byte Big Endian als vorzeichenlose Zahl.</summary>
        Private Shared Function BigU32(b As Byte(), o As Integer) As Long
            Return (CLng(b(o)) << 24) Or (CLng(b(o + 1)) << 16) Or (CLng(b(o + 2)) << 8) Or CLng(b(o + 3))
        End Function

        Private Shared Function Refuse(reason As String) As Layout
            DiagnosticLogService.LogAlways("DngJxlRaw", "nicht umgebaut: " & reason)
            Return Nothing
        End Function

        ' Umbau

        ''' <summary>Liest die Datei in einen Puffer, der hinten Platz fuer die dekodierten
        ''' Teilstuecke, die beiden Listen und die neue OpcodeList2 hat, und biegt die Eintraege um.</summary>
        Private Shared Function Build(fs As FileStream, plan As Layout, path As String) As Byte()
            Dim across = (plan.Width + plan.PieceWidth - 1) \ plan.PieceWidth
            Dim pieces = plan.Offsets.Length
            Dim pieceBytes(pieces - 1) As Long
            For i = 0 To pieces - 1
                ' Kacheln sind immer ganz, auch am Rand. Ein Streifen am Ende nur so hoch wie der Rest.
                Dim rows = If(plan.Tiled, plan.PieceHeight, Math.Min(plan.PieceHeight, plan.Height - i * plan.PieceHeight))
                pieceBytes(i) = CLng(rows) * plan.PieceWidth * plan.Samples * 2
            Next

            Dim polynomialsBaked = plan.OpcodeEntry IsNot Nothing AndAlso plan.Polynomials.Count > 0
            Dim opcodeList As Byte() = Nothing
            If polynomialsBaked Then
                Dim ms As New MemoryStream()
                Dim countBytes(3) As Byte
                BinaryPrimitives.WriteUInt32BigEndian(countBytes, CUInt(plan.KeptOpcodes.Count))
                ms.Write(countBytes, 0, 4)
                For Each block In plan.KeptOpcodes
                    ms.Write(block, 0, block.Length)
                Next
                opcodeList = ms.ToArray()
            End If

            ' Platz hinten: jedes Stueck auf gerader Adresse, dazu die beiden Listen und die Opcodes.
            Dim total = fs.Length
            total += total And 1L
            Dim newOffsets(pieces - 1) As Long
            For i = 0 To pieces - 1
                newOffsets(i) = total
                total += pieceBytes(i)
                total += total And 1L
            Next
            Dim offsetsAt = total
            If pieces > 1 Then total += pieces * 4L
            Dim countsAt = total
            If pieces > 1 Then total += pieces * 4L
            Dim opcodeAt = total
            If opcodeList IsNot Nothing AndAlso opcodeList.Length > 4 Then total += opcodeList.Length
            If total > Integer.MaxValue OrElse total > UInteger.MaxValue Then
                Refuse("zu gross fuer einen Puffer")
                Return Nothing
            End If

            Dim dng(CInt(total) - 1) As Byte
            fs.Position = 0
            Dim read = 0
            Do While read < fs.Length
                Dim n = fs.Read(dng, read, CInt(fs.Length) - read)
                If n <= 0 Then Return Nothing
                read += n
            Loop

            For i = 0 To pieces - 1
                If plan.Counts(i) <= 0 OrElse plan.Offsets(i) < 0 OrElse plan.Offsets(i) + plan.Counts(i) > fs.Length Then Return Nothing
                Dim x0 = If(plan.Tiled, (i Mod across) * plan.PieceWidth, 0)
                Dim y0 = If(plan.Tiled, (i \ across) * plan.PieceHeight, i * plan.PieceHeight)
                Dim rows = CInt(pieceBytes(i) \ (CLng(plan.PieceWidth) * plan.Samples * 2))
                Dim w = 0, h = 0
                Dim values = JxlDecodeService.TryDecodeFloat(dng, CInt(plan.Offsets(i)), CInt(plan.Counts(i)), plan.Samples, w, h)
                If values Is Nothing OrElse Not PieceSizeFits(plan, w, h, rows, x0, y0) Then
                    DiagnosticLogService.LogAlways("DngJxlRaw", $"{IO.Path.GetFileName(path)}: Teilstueck {i} nicht lesbar oder falsch gross ({w}x{h})")
                    Return Nothing
                End If
                WritePiece(dng, CInt(newOffsets(i)), values, w, h, rows, x0, y0, plan)
            Next

            ' Eintraege umbiegen. Die Typen werden dabei LONG, auch wo vorher SHORT stand.
            WriteEntry(dng, plan.CompressionEntry, TypeShort, 1, 1, plan.Little)
            WriteLongList(dng, plan.OffsetsEntry, newOffsets, offsetsAt, plan.Little)
            WriteLongList(dng, plan.CountsEntry, pieceBytes, countsAt, plan.Little)
            If polynomialsBaked Then
                Dim at = CInt(plan.OpcodeEntry.Position)
                WriteU16(dng, at + 2, TypeUndefined, plan.Little)
                WriteU32(dng, at + 4, CLng(opcodeList.Length), plan.Little)
                If opcodeList.Length > 4 Then
                    Buffer.BlockCopy(opcodeList, 0, dng, CInt(opcodeAt), opcodeList.Length)
                    WriteU32(dng, at + 8, opcodeAt, plan.Little)
                Else
                    Buffer.BlockCopy(opcodeList, 0, dng, at + 8, opcodeList.Length)
                End If
            End If
            Return dng
        End Function

        ''' <summary>Hat ein dekodiertes Teilstueck genau die Groesse, die ihm zusteht? Ein Streifen
        ''' ist so breit wie das Bild und so hoch wie seine Zeilen. Eine Kachel ist entweder ganz (so
        ''' schreibt der Konverter des Referenzprogramms auch die Kacheln am Rand, gemessen an der Sony-Datei aus Issue 83)
        ''' oder genau auf den Bildrand beschnitten. Alles andere ist kaputt oder falsch verstanden: ein
        ''' zu kleines Stueck liesse den Rest der Kachel schwarz, und der Umbau wird abgebrochen, damit
        ''' es bei der eingebetteten Vorschau bleibt.</summary>
        Private Shared Function PieceSizeFits(plan As Layout, w As Integer, h As Integer, rows As Integer,
                                              x0 As Integer, y0 As Integer) As Boolean
            If Not plan.Tiled Then Return w = plan.Width AndAlso h = rows
            If w = plan.PieceWidth AndAlso h = plan.PieceHeight Then Return True
            Return w = Math.Min(plan.PieceWidth, plan.Width - x0) AndAlso h = Math.Min(plan.PieceHeight, plan.Height - y0)
        End Function

        ''' <summary>Schreibt ein dekodiertes Teilstueck als 16-Bit-Werte in Byte-Reihenfolge der
        ''' Datei, mit eingerechneten Polynomen. Was das Teilstueck nicht bedeckt (Rand einer Kachel),
        ''' bleibt 0.</summary>
        Private Shared Sub WritePiece(dng As Byte(), at As Integer, values As Single(), w As Integer, h As Integer,
                                      rows As Integer, x0 As Integer, y0 As Integer, plan As Layout)
            Dim samples = plan.Samples
            Dim rowBytes = plan.PieceWidth * samples * 2
            Threading.Tasks.Parallel.For(0, h,
                Sub(y)
                    Dim imageY = y0 + y
                    Dim target = at + y * rowBytes
                    For x = 0 To w - 1
                        Dim imageX = x0 + x
                        For c = 0 To samples - 1
                            Dim v = CDbl(values((y * w + x) * samples + c)) * 65535.0
                            v = ApplyPolynomials(v, imageX, imageY, c, plan)
                            Dim stored = CUShort(Math.Max(0.0, Math.Min(65535.0, Math.Round(v))))
                            Dim o = target + (x * samples + c) * 2
                            If plan.Little Then
                                BinaryPrimitives.WriteUInt16LittleEndian(dng.AsSpan(o, 2), stored)
                            Else
                                BinaryPrimitives.WriteUInt16BigEndian(dng.AsSpan(o, 2), stored)
                            End If
                        Next
                    Next
                End Sub)
        End Sub

        ''' <summary>Die MapPolynomial der Reihe nach auf einen Wert. Ihre Lage zaehlt ab der
        ''' ActiveArea; ausserhalb davon und ausserhalb des Bereichs, der Ebenen und des Rasters aus
        ''' RowPitch und ColPitch bleibt der Wert, wie er ist.</summary>
        Private Shared Function ApplyPolynomials(v As Double, imageX As Integer, imageY As Integer, plane As Integer, plan As Layout) As Double
            If plan.Polynomials.Count = 0 Then Return v
            If imageY < plan.ActiveTop OrElse imageY >= plan.ActiveBottom OrElse imageX < plan.ActiveLeft OrElse imageX >= plan.ActiveRight Then Return v
            Dim ay = imageY - plan.ActiveTop
            Dim ax = imageX - plan.ActiveLeft
            Dim black = plan.Black(plane)
            Dim range = plan.White(plane) - black
            If range <= 0 Then Return v
            For Each p In plan.Polynomials
                If plane < p.Plane OrElse plane >= p.Plane + p.Planes Then Continue For
                If ay < p.Top OrElse ay >= p.Bottom OrElse ax < p.Left OrElse ax >= p.Right Then Continue For
                If (ay - p.Top) Mod p.RowPitch <> 0 OrElse (ax - p.Left) Mod p.ColPitch <> 0 Then Continue For
                Dim x = (v - black) / range
                ' Horner von oben nach unten.
                Dim y = 0.0
                For k = p.Coefficients.Length - 1 To 0 Step -1
                    y = y * x + p.Coefficients(k)
                Next
                v = black + Math.Max(0.0, Math.Min(1.0, y)) * range
            Next
            Return v
        End Function

        Private Shared Sub WriteEntry(dng As Byte(), entry As Entry, type As Integer, count As Long, value As Integer, little As Boolean)
            Dim at = CInt(entry.Position)
            WriteU16(dng, at + 2, type, little)
            WriteU32(dng, at + 4, count, little)
            WriteU32(dng, at + 8, 0, little)
            If type = TypeShort Then
                WriteU16(dng, at + 8, value, little)
            Else
                WriteU32(dng, at + 8, value, little)
            End If
        End Sub

        ''' <summary>Eine LONG-Liste: ein Wert steht im Wertfeld, mehrere an <paramref name="listAt"/>.</summary>
        Private Shared Sub WriteLongList(dng As Byte(), entry As Entry, values As Long(), listAt As Long, little As Boolean)
            Dim at = CInt(entry.Position)
            WriteU16(dng, at + 2, TypeLong, little)
            WriteU32(dng, at + 4, values.Length, little)
            If values.Length = 1 Then
                WriteU32(dng, at + 8, values(0), little)
                Return
            End If
            For i = 0 To values.Length - 1
                WriteU32(dng, CInt(listAt) + i * 4, values(i), little)
            Next
            WriteU32(dng, at + 8, listAt, little)
        End Sub

        ' Bytes

        Private Shared Function ReadAt(fs As FileStream, offset As Long, length As Integer) As Byte()
            If offset < 0 OrElse length <= 0 OrElse offset + length > fs.Length Then Return Nothing
            Dim result(length - 1) As Byte
            fs.Position = offset
            Dim read = 0
            Do While read < length
                Dim n = fs.Read(result, read, length - read)
                If n <= 0 Then Return Nothing
                read += n
            Loop
            Return result
        End Function

        Private Shared Function ReadU16(b As Byte(), o As Integer, little As Boolean) As Integer
            Return If(little, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(o, 2)))
        End Function

        Private Shared Function ReadU32(b As Byte(), o As Integer, little As Boolean) As Long
            Return If(little, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o, 4)), BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(o, 4)))
        End Function

        Private Shared Function ReadU64(b As Byte(), o As Integer, little As Boolean) As Long
            Return If(little, BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(o, 8)), BinaryPrimitives.ReadInt64BigEndian(b.AsSpan(o, 8)))
        End Function

        Private Shared Sub WriteU16(b As Byte(), o As Integer, value As Integer, little As Boolean)
            If little Then
                BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), CUShort(value))
            Else
                BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(o, 2), CUShort(value))
            End If
        End Sub

        Private Shared Sub WriteU32(b As Byte(), o As Integer, value As Long, little As Boolean)
            If little Then
                BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), CUInt(value))
            Else
                BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(o, 4), CUInt(value))
            End If
        End Sub

    End Class

End Namespace
