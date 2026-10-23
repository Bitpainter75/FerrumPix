Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Runtime.InteropServices

Namespace Services

    ''' <summary>Die Abschattung des Objektivs, die eine DNG als GainMap in OpcodeList2 traegt, auf
    ''' die Sensordaten angewandt, bevor LibRaw entwickelt.
    '''
    ''' WARUM: Telefone (Pixel, Lumia, Xiaomi, Samsung, OnePlus) und die Sigma fp-L schreiben ihre
    ''' Vignettierung nicht ins Bild, sondern als Karte von Verstaerkungen je Bayer-Phase neben die
    ''' Rohdaten. LibRaw wendet sie nicht an. Gemessen am 2026-10-23 gegen Adobes Nullbild, Verhaeltnis
    ''' Ecke zu Mitte in Blendenstufen: Pixel 8 Pro -2,06, Lumia 930 -1,98, Pixel 4a -1,55, Lumia 950 XL
    ''' -1,05, Mi 8 -1,02, Xiaomi M2010J19CG -0,97; die Ecken kamen also bis zu zwei Blenden zu dunkel.
    '''
    ''' DIE REGEL kommt aus der DNG-Spezifikation (Opcode 9, GainMap): OpcodeList2 wirkt auf das Bild
    ''' der Stufe 2, also auf die Werte ueber Schwarz innerhalb der ActiveArea. Die Kartenpunkte liegen
    ''' relativ zum GANZEN Bild dieser Stufe (0 bis 1), gelesen wird an der Mitte des Bildpunkts,
    ''' zwischen den Punkten bilinear, ausserhalb der Karte gilt ihr Randwert. Die Rohwerte tragen bei
    ''' LibRaw das Schwarz noch in sich; gerechnet wird deshalb Schwarz + (Wert - Schwarz) * Gain, mit
    ''' demselben Schwarz und Weiss, die die Datei nennt.
    '''
    ''' WAS NICHT angewandt wird, und dann bleibt die Datei, wie LibRaw sie liest: OpcodeList3 (wirkt
    ''' nach dem Demosaic; an der Sigma fp ohne messbaren Unterschied), Karten fuer andere Ebenen als
    ''' die eine des Sensors (lineare DNGs), Dateien mit LinearizationTable (dann ist der Rohwert noch
    ''' nicht linear) und Karten mit Werten, die nicht endlich oder nicht positiv sind.</summary>
    Public NotInheritable Class DngGainMapService

        Private Sub New()
        End Sub

        ''' <summary>Eine GainMap aus der Opcode-Liste, in Koordinaten des Bildes der Stufe 2.</summary>
        Public NotInheritable Class GainMap
            Public Top, Left, Bottom, Right As Integer
            Public Plane, Planes As Integer
            Public RowPitch, ColPitch As Integer
            Public PointsV, PointsH As Integer
            Public SpacingV, SpacingH As Double
            Public OriginV, OriginH As Double
            Public MapPlanes As Integer
            ''' <summary>PointsV mal PointsH mal MapPlanes, Zeile fuer Zeile, je Punkt alle Ebenen.</summary>
            Public Gains As Single()
        End Class

        ''' <summary>Was es zum Anwenden braucht: die Karten und die Pegel der Datei.</summary>
        Public NotInheritable Class GainMapSet
            Public Maps As New List(Of GainMap)()
            ''' <summary>Lage der ActiveArea in den gespeicherten Rohdaten.</summary>
            Public ActiveTop, ActiveLeft, ActiveHeight, ActiveWidth As Integer
            ''' <summary>BlackLevel im Muster BlackLevelRepeatDim (Zeilen, Spalten), relativ zur ActiveArea.</summary>
            Public BlackRepeatRows As Integer = 1
            Public BlackRepeatCols As Integer = 1
            Public Black As Double() = {0.0}
            Public BlackDeltaH As Double()
            Public BlackDeltaV As Double()
            Public White As Double = 65535
        End Class

        Private Const TagNewSubfileType As Integer = 254
        Private Const TagImageWidth As Integer = 256
        Private Const TagImageLength As Integer = 257
        Private Const TagPhotometric As Integer = 262
        Private Const TagSubIfds As Integer = 330
        Private Const TagLinearizationTable As Integer = 50712
        Private Const TagBlackLevelRepeatDim As Integer = 50713
        Private Const TagBlackLevel As Integer = 50714
        Private Const TagBlackLevelDeltaH As Integer = 50715
        Private Const TagBlackLevelDeltaV As Integer = 50716
        Private Const TagWhiteLevel As Integer = 50717
        Private Const TagActiveArea As Integer = 50829
        Private Const TagOpcodeList2 As Integer = 51009
        Private Const PhotometricCfa As Integer = 32803
        Private Const OpcodeGainMap As Integer = 9

        ''' <summary>Die GainMaps aus OpcodeList2 der Rohdaten-IFD, oder Nothing, wenn die Datei keine
        ''' traegt oder etwas nicht so ist, wie die Regel oben verlangt.</summary>
        Public Shared Function ReadStage2GainMaps(path As String) As GainMapSet
            If String.IsNullOrEmpty(path) OrElse Not path.EndsWith(".dng", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Try
                Using stream = New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                    Dim reader As New TiffReader(stream)
                    If Not reader.ReadHeader() Then Return Nothing
                    Dim raw = reader.FindRawIfd()
                    If raw Is Nothing Then Return Nothing
                    Dim opcodeEntry As TiffEntry = Nothing
                    If Not raw.TryGetValue(TagOpcodeList2, opcodeEntry) Then Return Nothing
                    If raw.ContainsKey(TagLinearizationTable) Then Return Nothing

                    Dim result As New GainMapSet()
                    Dim width = CInt(reader.ReadNumbers(raw(TagImageWidth))(0))
                    Dim height = CInt(reader.ReadNumbers(raw(TagImageLength))(0))
                    result.ActiveTop = 0 : result.ActiveLeft = 0
                    result.ActiveHeight = height : result.ActiveWidth = width
                    Dim areaEntry As TiffEntry = Nothing
                    If raw.TryGetValue(TagActiveArea, areaEntry) Then
                        Dim area = reader.ReadNumbers(areaEntry)
                        If area.Length = 4 Then
                            result.ActiveTop = CInt(area(0)) : result.ActiveLeft = CInt(area(1))
                            result.ActiveHeight = CInt(area(2)) - result.ActiveTop
                            result.ActiveWidth = CInt(area(3)) - result.ActiveLeft
                        End If
                    End If
                    If result.ActiveHeight <= 0 OrElse result.ActiveWidth <= 0 Then Return Nothing

                    Dim entry As TiffEntry = Nothing
                    If raw.TryGetValue(TagBlackLevelRepeatDim, entry) Then
                        Dim dims = reader.ReadNumbers(entry)
                        If dims.Length = 2 Then
                            result.BlackRepeatRows = Math.Max(1, CInt(dims(0)))
                            result.BlackRepeatCols = Math.Max(1, CInt(dims(1)))
                        End If
                    End If
                    If raw.TryGetValue(TagBlackLevel, entry) Then
                        Dim black = reader.ReadNumbers(entry)
                        ' Je Ebene eigene Werte (SamplesPerPixel > 1) kommen bei einer CFA nicht vor.
                        If black.Length < result.BlackRepeatRows * result.BlackRepeatCols Then
                            If black.Length = 1 Then
                                result.BlackRepeatRows = 1 : result.BlackRepeatCols = 1
                            Else
                                Return Nothing
                            End If
                        End If
                        result.Black = black
                    End If
                    If raw.TryGetValue(TagBlackLevelDeltaH, entry) Then result.BlackDeltaH = reader.ReadNumbers(entry)
                    If raw.TryGetValue(TagBlackLevelDeltaV, entry) Then result.BlackDeltaV = reader.ReadNumbers(entry)
                    If raw.TryGetValue(TagWhiteLevel, entry) Then
                        Dim white = reader.ReadNumbers(entry)
                        If white.Length > 0 AndAlso white(0) > 0 Then result.White = white(0)
                    End If

                    For Each map In ParseOpcodeList(reader.ReadBytes(opcodeEntry))
                        ' Nur die eine Ebene des Sensors: Karten fuer weitere Ebenen gehoeren zu
                        ' linearen DNGs, deren Daten LibRaw nicht in den Rohpuffer legt.
                        If map.Plane <> 0 OrElse map.Planes <> 1 Then Continue For
                        result.Maps.Add(map)
                    Next
                    Return If(result.Maps.Count = 0, Nothing, result)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("DngGainMap.Read", ex)
                Return Nothing
            End Try
        End Function

        ''' <summary>Die GainMaps einer Opcode-Liste. Die Liste ist immer grosse Endianness, gleich wie
        ''' die Datei selbst. Andere Opcodes werden uebersprungen; eine Karte, die nicht passt, faellt
        ''' allein heraus.</summary>
        Friend Shared Function ParseOpcodeList(data As Byte()) As List(Of GainMap)
            Dim maps As New List(Of GainMap)()
            If data Is Nothing OrElse data.Length < 4 Then Return maps
            Dim count = ReadU32(data, 0)
            Dim position = 4
            For i = 1 To CInt(Math.Min(count, 1000L))
                If position + 16 > data.Length Then Exit For
                Dim id = ReadU32(data, position)
                Dim size = ReadU32(data, position + 12)
                Dim start = position + 16
                If size < 0 OrElse start + size > data.Length Then Exit For
                If id = OpcodeGainMap Then
                    Dim map = ParseGainMap(data, start, CInt(size))
                    If map IsNot Nothing Then maps.Add(map)
                End If
                position = start + CInt(size)
            Next
            Return maps
        End Function

        Private Shared Function ParseGainMap(data As Byte(), start As Integer, size As Integer) As GainMap
            ' Kopf: 4 mal Bereich, Ebene, Ebenen, 2 mal Schritt, 2 mal Punkte (je 4 Bytes),
            ' 4 Doubles, Kartenebenen (4 Bytes): 76 Bytes, danach die Gains als Float.
            Const HeaderBytes As Integer = 76
            If size < HeaderBytes Then Return Nothing
            Dim p = start
            Dim map As New GainMap With {
                .Top = CInt(ReadU32(data, p)), .Left = CInt(ReadU32(data, p + 4)),
                .Bottom = CInt(ReadU32(data, p + 8)), .Right = CInt(ReadU32(data, p + 12)),
                .Plane = CInt(ReadU32(data, p + 16)), .Planes = CInt(ReadU32(data, p + 20)),
                .RowPitch = CInt(ReadU32(data, p + 24)), .ColPitch = CInt(ReadU32(data, p + 28)),
                .PointsV = CInt(ReadU32(data, p + 32)), .PointsH = CInt(ReadU32(data, p + 36)),
                .SpacingV = ReadF64(data, p + 40), .SpacingH = ReadF64(data, p + 48),
                .OriginV = ReadF64(data, p + 56), .OriginH = ReadF64(data, p + 64),
                .MapPlanes = CInt(ReadU32(data, p + 72))}
            If map.RowPitch < 1 OrElse map.ColPitch < 1 OrElse map.PointsV < 1 OrElse map.PointsH < 1 OrElse
               map.MapPlanes < 1 OrElse map.Bottom <= map.Top OrElse map.Right <= map.Left Then Return Nothing
            If (map.PointsV > 1 AndAlso Not (map.SpacingV > 0)) OrElse (map.PointsH > 1 AndAlso Not (map.SpacingH > 0)) Then Return Nothing
            Dim total = CLng(map.PointsV) * map.PointsH * map.MapPlanes
            If total > 1_000_000 OrElse HeaderBytes + total * 4 > size Then Return Nothing
            Dim gains(CInt(total) - 1) As Single
            For k = 0 To gains.Length - 1
                Dim g = ReadF32(data, p + HeaderBytes + k * 4)
                If Not Single.IsFinite(g) OrElse g <= 0 Then Return Nothing
                gains(k) = g
            Next
            map.Gains = gains
            Return map
        End Function

        ''' <summary>Wendet die Karten auf den Rohpuffer an (16 Bit je Sensel, Zeilenabstand
        ''' <paramref name="pitchBytes"/>). Liefert die Zahl der veraenderten Karten; 0, wenn die Masse
        ''' nicht zum Puffer passen.</summary>
        Friend Shared Function ApplyToRaw(gainMaps As GainMapSet, rawImage As IntPtr, rawWidth As Integer,
                                          rawHeight As Integer, pitchBytes As Integer) As Integer
            If gainMaps Is Nothing OrElse rawImage = IntPtr.Zero Then Return 0
            If gainMaps.ActiveTop + gainMaps.ActiveHeight > rawHeight OrElse
               gainMaps.ActiveLeft + gainMaps.ActiveWidth > rawWidth OrElse pitchBytes < rawWidth * 2 Then Return 0
            Dim applied = 0
            Dim row(rawWidth - 1) As Short
            For Each map In gainMaps.Maps
                Dim r0 = Math.Max(0, map.Top), r1 = Math.Min(gainMaps.ActiveHeight, map.Bottom)
                Dim c0 = Math.Max(0, map.Left), c1 = Math.Min(gainMaps.ActiveWidth, map.Right)
                If r1 <= r0 OrElse c1 <= c0 Then Continue For
                For r = r0 To r1 - 1 Step map.RowPitch
                    Dim line = IntPtr.Add(rawImage, (gainMaps.ActiveTop + r) * pitchBytes)
                    Marshal.Copy(line, row, 0, rawWidth)
                    Dim fv = MapIndex((r + 0.5) / gainMaps.ActiveHeight, map.OriginV, map.SpacingV, map.PointsV)
                    For c = c0 To c1 - 1 Step map.ColPitch
                        Dim fh = MapIndex((c + 0.5) / gainMaps.ActiveWidth, map.OriginH, map.SpacingH, map.PointsH)
                        Dim gain = Bilinear(map, fv, fh)
                        Dim x = gainMaps.ActiveLeft + c
                        Dim value = CInt(row(x)) And &HFFFF
                        Dim black = BlackAt(gainMaps, r, c)
                        Dim above = value - black
                        If above <= 0 Then Continue For
                        Dim corrected = Math.Min(gainMaps.White, black + above * gain)
                        row(x) = CShort(If(corrected >= 32767.5, CInt(Math.Round(corrected)) - 65536, CInt(Math.Round(corrected))))
                    Next
                    Marshal.Copy(row, 0, line, rawWidth)
                Next
                applied += 1
            Next
            Return applied
        End Function

        ''' <summary>Stelle in der Karte als Gleitkommaindex, auf die Karte begrenzt.</summary>
        Private Shared Function MapIndex(relative As Double, origin As Double, spacing As Double, points As Integer) As Double
            If points <= 1 Then Return 0
            Dim index = (relative - origin) / spacing
            Return Math.Max(0.0, Math.Min(points - 1, index))
        End Function

        Private Shared Function Bilinear(map As GainMap, fv As Double, fh As Double) As Double
            Dim v0 = CInt(Math.Floor(fv)), h0 = CInt(Math.Floor(fh))
            Dim v1 = Math.Min(map.PointsV - 1, v0 + 1), h1 = Math.Min(map.PointsH - 1, h0 + 1)
            Dim tv = fv - v0, th = fh - h0
            Dim at = Function(v As Integer, h As Integer) CDbl(map.Gains((v * map.PointsH + h) * map.MapPlanes))
            Dim top = at(v0, h0) + (at(v0, h1) - at(v0, h0)) * th
            Dim bottom = at(v1, h0) + (at(v1, h1) - at(v1, h0)) * th
            Return top + (bottom - top) * tv
        End Function

        ''' <summary>Das Schwarz an dieser Stelle der ActiveArea: Muster aus BlackLevel, dazu die
        ''' Abweichungen je Spalte und Zeile, wenn die Datei welche nennt.</summary>
        Private Shared Function BlackAt(gainMaps As GainMapSet, r As Integer, c As Integer) As Double
            Dim index = (r Mod gainMaps.BlackRepeatRows) * gainMaps.BlackRepeatCols + (c Mod gainMaps.BlackRepeatCols)
            Dim black = gainMaps.Black(Math.Min(index, gainMaps.Black.Length - 1))
            If gainMaps.BlackDeltaH IsNot Nothing AndAlso c < gainMaps.BlackDeltaH.Length Then black += gainMaps.BlackDeltaH(c)
            If gainMaps.BlackDeltaV IsNot Nothing AndAlso r < gainMaps.BlackDeltaV.Length Then black += gainMaps.BlackDeltaV(r)
            Return black
        End Function

        Private Shared Function ReadU32(data As Byte(), offset As Integer) As Long
            Return (CLng(data(offset)) << 24) Or (CLng(data(offset + 1)) << 16) Or (CLng(data(offset + 2)) << 8) Or CLng(data(offset + 3))
        End Function

        Private Shared Function ReadF64(data As Byte(), offset As Integer) As Double
            Dim bits As Long = 0
            For i = 0 To 7
                bits = (bits << 8) Or CLng(data(offset + i))
            Next
            Return BitConverter.Int64BitsToDouble(bits)
        End Function

        Private Shared Function ReadF32(data As Byte(), offset As Integer) As Single
            Return BitConverter.Int32BitsToSingle(CInt(ReadU32(data, offset) - If(data(offset) >= 128, 4294967296L, 0L)))
        End Function

        ' ── Ein kleiner TIFF-Leser: IFD-Kette und SubIFDs, nur was oben gebraucht wird ──────────

        Private NotInheritable Class TiffEntry
            Public Type As Integer
            Public Count As Long
            ''' <summary>Versatz der Werte in der Datei (bei kurzen Werten der des Feldes selbst).</summary>
            Public ValueOffset As Long
        End Class

        Private NotInheritable Class TiffReader
            Private ReadOnly _stream As Stream
            Private _little As Boolean
            Private _firstIfd As Long

            Public Sub New(stream As Stream)
                _stream = stream
            End Sub

            Public Function ReadHeader() As Boolean
                Dim head = ReadAt(0, 8)
                If head Is Nothing Then Return False
                If head(0) = &H49 AndAlso head(1) = &H49 Then
                    _little = True
                ElseIf head(0) = &H4D AndAlso head(1) = &H4D Then
                    _little = False
                Else
                    Return False
                End If
                If U16(head, 2) <> 42 Then Return False
                _firstIfd = U32(head, 4)
                Return True
            End Function

            ''' <summary>Die IFD der Hauptrohdaten: NewSubfileType 0 und ein Sensormuster.</summary>
            Public Function FindRawIfd() As Dictionary(Of Integer, TiffEntry)
                Dim seen As New HashSet(Of Long)()
                Dim pending As New Stack(Of Long)()
                pending.Push(_firstIfd)
                While pending.Count > 0 AndAlso seen.Count < 64
                    Dim offset = pending.Pop()
                    If offset <= 0 OrElse Not seen.Add(offset) Then Continue While
                    Dim nextIfd As Long = 0
                    Dim entries = ReadIfd(offset, nextIfd)
                    If entries Is Nothing Then Continue While
                    If nextIfd > 0 Then pending.Push(nextIfd)
                    Dim subIfds As TiffEntry = Nothing
                    If entries.TryGetValue(TagSubIfds, subIfds) Then
                        For Each child In ReadNumbers(subIfds)
                            pending.Push(CLng(child))
                        Next
                    End If
                    Dim kind As TiffEntry = Nothing
                    Dim photometric As TiffEntry = Nothing
                    Dim isMain = Not entries.TryGetValue(TagNewSubfileType, kind) OrElse ReadNumbers(kind)(0) = 0
                    If isMain AndAlso entries.TryGetValue(TagPhotometric, photometric) AndAlso
                       ReadNumbers(photometric)(0) = PhotometricCfa AndAlso
                       entries.ContainsKey(TagImageWidth) AndAlso entries.ContainsKey(TagImageLength) Then
                        Return entries
                    End If
                End While
                Return Nothing
            End Function

            Private Function ReadIfd(offset As Long, ByRef nextIfd As Long) As Dictionary(Of Integer, TiffEntry)
                Dim countBytes = ReadAt(offset, 2)
                If countBytes Is Nothing Then Return Nothing
                Dim count = U16(countBytes, 0)
                If count = 0 OrElse count > 4000 Then Return Nothing
                Dim block = ReadAt(offset + 2, count * 12 + 4)
                If block Is Nothing Then Return Nothing
                Dim entries As New Dictionary(Of Integer, TiffEntry)()
                For i = 0 To count - 1
                    Dim b = i * 12
                    Dim type = U16(block, b + 2)
                    Dim n = U32(block, b + 4)
                    Dim bytes = TypeSize(type) * n
                    entries(U16(block, b)) = New TiffEntry With {
                        .Type = type, .Count = n,
                        .ValueOffset = If(bytes <= 4, offset + 2 + b + 8, U32(block, b + 8))}
                Next
                nextIfd = U32(block, count * 12)
                Return entries
            End Function

            Public Function ReadBytes(entry As TiffEntry) As Byte()
                Dim length = TypeSize(entry.Type) * entry.Count
                If length <= 0 OrElse length > 64 * 1024 * 1024 Then Return Nothing
                Return ReadAt(entry.ValueOffset, CInt(length))
            End Function

            ''' <summary>Die Werte eines Feldes als Zahlen: SHORT, LONG, RATIONAL und SRATIONAL.</summary>
            Public Function ReadNumbers(entry As TiffEntry) As Double()
                Dim size = TypeSize(entry.Type)
                If size = 0 OrElse entry.Count <= 0 OrElse entry.Count > 1_000_000 Then Return {0.0}
                Dim data = ReadAt(entry.ValueOffset, CInt(size * entry.Count))
                If data Is Nothing Then Return {0.0}
                Dim values(CInt(entry.Count) - 1) As Double
                For i = 0 To values.Length - 1
                    Select Case entry.Type
                        Case 3 : values(i) = U16(data, i * 2)
                        Case 4 : values(i) = U32(data, i * 4)
                        Case 9 : values(i) = S32(data, i * 4)
                        Case 5
                            Dim d = U32(data, i * 8 + 4)
                            values(i) = If(d = 0, 0.0, U32(data, i * 8) / CDbl(d))
                        Case 10
                            Dim d = S32(data, i * 8 + 4)
                            values(i) = If(d = 0, 0.0, S32(data, i * 8) / CDbl(d))
                        Case 1, 7 : values(i) = data(i)
                        Case Else : values(i) = 0
                    End Select
                Next
                Return values
            End Function

            Private Shared Function TypeSize(type As Integer) As Long
                Select Case type
                    Case 1, 2, 6, 7 : Return 1
                    Case 3, 8 : Return 2
                    Case 4, 9, 11, 13 : Return 4
                    Case 5, 10, 12 : Return 8
                    Case Else : Return 0
                End Select
            End Function

            Private Function ReadAt(offset As Long, length As Integer) As Byte()
                If offset < 0 OrElse length <= 0 OrElse offset + length > _stream.Length Then Return Nothing
                _stream.Seek(offset, SeekOrigin.Begin)
                Dim buffer(length - 1) As Byte
                Dim read = 0
                While read < length
                    Dim n = _stream.Read(buffer, read, length - read)
                    If n <= 0 Then Return Nothing
                    read += n
                End While
                Return buffer
            End Function

            Private Function U16(data As Byte(), offset As Integer) As Integer
                Dim a = CInt(data(offset)), b = CInt(data(offset + 1))
                Return If(_little, (b << 8) Or a, (a << 8) Or b)
            End Function

            Private Function U32(data As Byte(), offset As Integer) As Long
                Dim a = CLng(data(offset)), b = CLng(data(offset + 1)), c = CLng(data(offset + 2)), d = CLng(data(offset + 3))
                Return If(_little, (d << 24) Or (c << 16) Or (b << 8) Or a, (a << 24) Or (b << 16) Or (c << 8) Or d)
            End Function

            Private Function S32(data As Byte(), offset As Integer) As Long
                Dim value = U32(data, offset)
                Return If(value >= 2147483648L, value - 4294967296L, value)
            End Function
        End Class

    End Class

End Namespace
