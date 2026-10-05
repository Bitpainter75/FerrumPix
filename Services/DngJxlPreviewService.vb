Imports System
Imports System.Collections.Generic
Imports System.IO
Imports SkiaSharp

Namespace Services

    ''' <summary>Die JPEG-XL-Vorschau eines DNG 1.7.
    '''
    ''' Ein DNG mit JPEG-XL-Kompression (52546) entwickelt LibRaw 0.22 nicht, FerrumPix faellt dann
    ''' auf die eingebettete Vorschau zurueck (RawPreviewService.ExtractPreviewWithFallback). In
    ''' solchen Dateien ist aber oft auch die grosse Vorschau JPEG XL, und die kommt ueber keinen der
    ''' beiden bisherigen Wege an: der eigene Scanner sucht JPEG, und LibRaws Vorschau-API meldet nur
    ''' das JPEG-Miniaturbild. Beim Sony-DNG aus Issue 83 blieben so 256 x 171 Punkte, waehrend die
    ''' Datei eine Vorschau von 1024 x 683 traegt.
    '''
    ''' Gelesen wird deshalb selbst: IFD0 und seine SubIFDs (330) durchgehen, unter den
    ''' RGB-Ebenen (Photometric 2 oder 6) mit Kompression 52546 die groesste nehmen, ihre Streifen
    ''' oder Kacheln einzeln mit libjxl dekodieren (JxlDecodeService.TryDecodeBytes) und zu einem Bild
    ''' zusammensetzen. Jeder Streifen und jede Kachel ist ein eigener JPEG-XL-Datenstrom. Die
    ''' LinearRaw-Ebenen (34892) sind keine Vorschau, sondern Rohdaten in kleinerer Aufloesung, und
    ''' bleiben aussen vor.
    '''
    ''' Gelesen wird nur, was gebraucht wird, ueber Sprungstellen in der Datei: ein DNG ist schnell
    ''' hundert Megabyte gross, und fuer jedes andere DNG endet der Weg schon nach den
    ''' Verzeichnissen.</summary>
    Public NotInheritable Class DngJxlPreviewService

        Private Const CompressionJpegXl As Integer = 52546
        Private Const TagWidth As Integer = 256
        Private Const TagHeight As Integer = 257
        Private Const TagCompression As Integer = 259
        Private Const TagPhotometric As Integer = 262
        Private Const TagStripOffsets As Integer = 273
        Private Const TagRowsPerStrip As Integer = 278
        Private Const TagStripByteCounts As Integer = 279
        Private Const TagTileWidth As Integer = 322
        Private Const TagTileLength As Integer = 323
        Private Const TagTileOffsets As Integer = 324
        Private Const TagTileByteCounts As Integer = 325
        Private Const TagSubIfds As Integer = 330

        ' Obergrenzen gegen kaputte Dateien: so viele Verzeichnisse, Eintraege und Teilstuecke hat
        ' kein echtes DNG.
        Private Const MaxIfds As Integer = 64
        Private Const MaxEntries As Integer = 1000
        Private Const MaxSegments As Integer = 100000

        Private Sub New()
        End Sub

        ''' <summary>Eine Ebene des DNG, so weit sie hier gebraucht wird.</summary>
        Private NotInheritable Class Ifd
            Public Width As Integer
            Public Height As Integer
            Public Compression As Integer
            Public Photometric As Integer
            Public RowsPerStrip As Integer
            Public TileWidth As Integer
            Public TileLength As Integer
            Public Offsets As Long()
            Public Counts As Long()
            Public SubIfds As Long()
        End Class

        ''' <summary>Die groesste JPEG-XL-Vorschau als JPEG-Strom, oder Nothing, wenn die Datei
        ''' keine traegt oder sie sich nicht lesen laesst.</summary>
        Public Shared Function TryExtract(filePath As String) As MemoryStream
            If String.IsNullOrWhiteSpace(filePath) OrElse Not JxlDecodeService.IsAvailable Then Return Nothing
            Try
                Using fs As New FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                    Dim best = FindLargestJxlPreview(fs)
                    If best Is Nothing Then Return Nothing
                    Using bmp = DecodeIfd(fs, best)
                        If bmp Is Nothing Then Return Nothing
                        Using data = bmp.Encode(SKEncodedImageFormat.Jpeg, 92)
                            If data Is Nothing Then Return Nothing
                            Return New MemoryStream(data.ToArray())
                        End Using
                    End Using
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("DngJxlPreview", ex)
                Return Nothing
            End Try
        End Function

        Private Shared Function FindLargestJxlPreview(fs As FileStream) As Ifd
            Dim header(7) As Byte
            If fs.Read(header, 0, 8) <> 8 Then Return Nothing
            Dim little As Boolean
            If header(0) = &H49 AndAlso header(1) = &H49 Then
                little = True
            ElseIf header(0) = &H4D AndAlso header(1) = &H4D Then
                little = False
            Else
                Return Nothing
            End If
            If ReadU16(header, 2, little) <> 42 Then Return Nothing

            Dim best As Ifd = Nothing
            Dim pending As New Queue(Of Long)()
            Dim seen As New HashSet(Of Long)()
            pending.Enqueue(ReadU32(header, 4, little))
            Do While pending.Count > 0 AndAlso seen.Count < MaxIfds
                Dim offset = pending.Dequeue()
                If offset <= 0 OrElse offset >= fs.Length OrElse Not seen.Add(offset) Then Continue Do
                Dim nextIfd As Long = 0
                Dim ifd = ReadIfd(fs, offset, little, nextIfd)
                If ifd Is Nothing Then Continue Do
                If nextIfd > 0 Then pending.Enqueue(nextIfd)
                If ifd.SubIfds IsNot Nothing Then
                    For Each s In ifd.SubIfds
                        pending.Enqueue(s)
                    Next
                End If
                If ifd.Compression <> CompressionJpegXl Then Continue Do
                If ifd.Photometric <> 2 AndAlso ifd.Photometric <> 6 Then Continue Do
                If ifd.Width <= 0 OrElse ifd.Height <= 0 OrElse ifd.Offsets Is Nothing OrElse ifd.Counts Is Nothing Then Continue Do
                If ifd.Offsets.Length = 0 OrElse ifd.Offsets.Length <> ifd.Counts.Length Then Continue Do
                If best Is Nothing OrElse CLng(ifd.Width) * ifd.Height > CLng(best.Width) * best.Height Then best = ifd
            Loop
            Return best
        End Function

        Private Shared Function ReadIfd(fs As FileStream, offset As Long, little As Boolean, ByRef nextIfd As Long) As Ifd
            Dim countBytes = ReadAt(fs, offset, 2)
            If countBytes Is Nothing Then Return Nothing
            Dim count = ReadU16(countBytes, 0, little)
            If count <= 0 OrElse count > MaxEntries Then Return Nothing
            Dim entries = ReadAt(fs, offset + 2, count * 12 + 4)
            If entries Is Nothing Then Return Nothing
            nextIfd = ReadU32(entries, count * 12, little)

            Dim result As New Ifd()
            For i = 0 To count - 1
                Dim e = i * 12
                Dim tag = ReadU16(entries, e, little)
                Dim type = ReadU16(entries, e + 2, little)
                Dim n = ReadU32(entries, e + 4, little)
                Select Case tag
                    Case TagWidth : result.Width = CInt(Math.Min(Integer.MaxValue, FirstValue(entries, e, type, little)))
                    Case TagHeight : result.Height = CInt(Math.Min(Integer.MaxValue, FirstValue(entries, e, type, little)))
                    Case TagCompression : result.Compression = CInt(FirstValue(entries, e, type, little))
                    Case TagPhotometric : result.Photometric = CInt(FirstValue(entries, e, type, little))
                    Case TagRowsPerStrip : result.RowsPerStrip = CInt(Math.Min(Integer.MaxValue, FirstValue(entries, e, type, little)))
                    Case TagTileWidth : result.TileWidth = CInt(Math.Min(Integer.MaxValue, FirstValue(entries, e, type, little)))
                    Case TagTileLength : result.TileLength = CInt(Math.Min(Integer.MaxValue, FirstValue(entries, e, type, little)))
                    Case TagStripOffsets, TagTileOffsets
                        result.Offsets = ReadValues(fs, entries, e, type, n, little)
                    Case TagStripByteCounts, TagTileByteCounts
                        result.Counts = ReadValues(fs, entries, e, type, n, little)
                    Case TagSubIfds
                        result.SubIfds = ReadValues(fs, entries, e, type, n, little)
                End Select
            Next
            Return result
        End Function

        ''' <summary>Setzt die Ebene aus ihren Streifen oder Kacheln zusammen. Jedes Teilstueck ist
        ''' ein eigener JPEG-XL-Datenstrom; Kacheln am rechten und unteren Rand ragen ueber das Bild
        ''' hinaus und werden beschnitten.</summary>
        Private Shared Function DecodeIfd(fs As FileStream, ifd As Ifd) As SKBitmap
            Dim tiled = ifd.TileWidth > 0 AndAlso ifd.TileLength > 0
            Dim across = If(tiled, (ifd.Width + ifd.TileWidth - 1) \ ifd.TileWidth, 1)
            Dim pieceHeight = If(tiled, ifd.TileLength, If(ifd.RowsPerStrip > 0, ifd.RowsPerStrip, ifd.Height))
            If ifd.Offsets.Length > MaxSegments Then Return Nothing

            Dim result As New SKBitmap(New SKImageInfo(ifd.Width, ifd.Height, SKColorType.Bgra8888, SKAlphaType.Premul))
            Dim ok = False
            Try
                Using canvas As New SKCanvas(result)
                    canvas.Clear(SKColors.Black)
                    For i = 0 To ifd.Offsets.Length - 1
                        If ifd.Counts(i) <= 0 OrElse ifd.Counts(i) > Integer.MaxValue Then Return Nothing
                        Dim data = ReadAt(fs, ifd.Offsets(i), CInt(ifd.Counts(i)))
                        If data Is Nothing Then Return Nothing
                        Using piece = JxlDecodeService.TryDecodeBytes(data)
                            If piece Is Nothing Then Return Nothing
                            Dim x = If(tiled, (i Mod across) * ifd.TileWidth, 0)
                            Dim y = If(tiled, (i \ across) * ifd.TileLength, i * pieceHeight)
                            canvas.DrawBitmap(piece, x, y)
                        End Using
                    Next
                End Using
                ok = True
                Return result
            Finally
                If Not ok Then result.Dispose()
            End Try
        End Function

        Private Shared Function ReadAt(fs As FileStream, offset As Long, length As Integer) As Byte()
            If offset < 0 OrElse length <= 0 OrElse offset + length > fs.Length Then Return Nothing
            Dim buffer(length - 1) As Byte
            fs.Position = offset
            Dim read = 0
            Do While read < length
                Dim n = fs.Read(buffer, read, length - read)
                If n <= 0 Then Return Nothing
                read += n
            Loop
            Return buffer
        End Function

        ''' <summary>Der erste Wert eines Eintrags, der in seinem Wertfeld steht (SHORT oder LONG).</summary>
        Private Shared Function FirstValue(entries As Byte(), e As Integer, type As Integer, little As Boolean) As Long
            Select Case type
                Case 3 : Return ReadU16(entries, e + 8, little)
                Case 4, 13 : Return ReadU32(entries, e + 8, little)
                Case 1 : Return entries(e + 8)
                Case Else : Return 0
            End Select
        End Function

        ''' <summary>Alle Werte eines SHORT- oder LONG-Eintrags; liegen sie nicht im Wertfeld, stehen
        ''' sie an der Stelle, auf die das Wertfeld zeigt.</summary>
        Private Shared Function ReadValues(fs As FileStream, entries As Byte(), e As Integer, type As Integer,
                                           count As Long, little As Boolean) As Long()
            Dim size = If(type = 3, 2, If(type = 4 OrElse type = 13, 4, 0))
            If size = 0 OrElse count <= 0 OrElse count > MaxSegments Then Return Nothing
            Dim total = CInt(count) * size
            Dim raw As Byte()
            Dim start As Integer
            If total <= 4 Then
                raw = entries
                start = e + 8
            Else
                raw = ReadAt(fs, ReadU32(entries, e + 8, little), total)
                If raw Is Nothing Then Return Nothing
                start = 0
            End If
            Dim values(CInt(count) - 1) As Long
            For i = 0 To CInt(count) - 1
                values(i) = If(size = 2, ReadU16(raw, start + i * 2, little), ReadU32(raw, start + i * 4, little))
            Next
            Return values
        End Function

        Private Shared Function ReadU16(b As Byte(), o As Integer, little As Boolean) As Integer
            Return If(little, b(o) Or (CInt(b(o + 1)) << 8), (CInt(b(o)) << 8) Or b(o + 1))
        End Function

        Private Shared Function ReadU32(b As Byte(), o As Integer, little As Boolean) As Long
            If little Then
                Return CLng(b(o)) Or (CLng(b(o + 1)) << 8) Or (CLng(b(o + 2)) << 16) Or (CLng(b(o + 3)) << 24)
            End If
            Return (CLng(b(o)) << 24) Or (CLng(b(o + 1)) << 16) Or (CLng(b(o + 2)) << 8) Or CLng(b(o + 3))
        End Function

    End Class

End Namespace
