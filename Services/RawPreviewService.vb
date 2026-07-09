Imports System
Imports System.IO
Imports System.Linq

Namespace Services

    ''' Extracts embedded JPEG preview from RAW camera files without external libraries.
    ''' Strategy: scan the first 16 MB of each file for embedded JPEG data,
    ''' filtering out lossless-JPEG-encoded RAW sensor data (SOF3/SOF5-7/SOF11/SOF13-15).
    ''' CR3 (Canon BMFF) is additionally parsed via its ISO BMFF box structure.
    Public Class RawPreviewService

        Private Shared ReadOnly AllRaw As String() = {
            ".cr2", ".cr3", ".nef", ".arw", ".dng", ".pef", ".rw2"
        }

        Public Shared Function IsSupportedRaw(filePath As String) As Boolean
            Dim ext = Path.GetExtension(filePath).ToLowerInvariant()
            Return AllRaw.Contains(ext)
        End Function

        ''' Returns a MemoryStream with JPEG bytes (positioned at 0), or Nothing on failure.
        Public Shared Function ExtractPreview(filePath As String) As MemoryStream
            Try
                Dim ext = Path.GetExtension(filePath).ToLowerInvariant()

                ' CR3: try BMFF walker first (faster, targets specific boxes)
                If ext = ".cr3" Then
                    Dim r = ExtractBmffPreview(filePath)
                    If r IsNot Nothing Then Return r
                End If

                ' Universal: scan file for the largest embedded displayable JPEG
                Return ScanForJpeg(filePath)
            Catch
                Return Nothing
            End Try
        End Function

        ' ── Universal JPEG scanner ────────────────────────────────────────────────

        Private Const MaxScanBytes As Long = 16L * 1024 * 1024   ' 16 MB

        Private Shared Function ScanForJpeg(filePath As String) As MemoryStream
            Using fs = File.OpenRead(filePath)
                Dim readLen = CInt(Math.Min(fs.Length, MaxScanBytes))
                Dim data(readLen - 1) As Byte
                Dim totalRead = 0
                Do While totalRead < readLen
                    Dim n = fs.Read(data, totalRead, readLen - totalRead)
                    If n = 0 Then Exit Do
                    totalRead += n
                Loop
                If totalRead < 4 Then Return Nothing

                Dim bestStart As Integer = -1
                Dim bestLen As Integer = 0

                Dim i = 0
                Do While i < totalRead - 3
                    If data(i) = &HFF AndAlso data(i + 1) = &HD8 AndAlso data(i + 2) = &HFF Then
                        If IsDisplayableJpeg(data, i) Then
                            Dim jLen = WalkJpegLength(data, i)
                            If jLen > bestLen Then
                                bestLen = jLen
                                bestStart = i
                            End If
                        End If
                    End If
                    i += 1
                Loop

                If bestStart < 0 OrElse bestLen < 8192 Then Return Nothing

                Dim result(bestLen - 1) As Byte
                Array.Copy(data, bestStart, result, 0, bestLen)
                Return New MemoryStream(result)
            End Using
        End Function

        ''' True when the JPEG at <offset> uses a standard (displayable) SOF type.
        ''' Lossless JPEG (SOF3/5-7/11/13-15) is used to compress RAW sensor data
        ''' and cannot be decoded by normal image decoders.
        Private Shared Function IsDisplayableJpeg(data As Byte(), offset As Integer) As Boolean
            Dim pos = offset + 2           ' skip SOI (FF D8)
            Dim limit = Math.Min(data.Length, offset + 8192)
            Do While pos + 3 < limit
                If data(pos) <> &HFF Then Return True   ' unexpected – assume OK
                Dim marker = data(pos + 1)
                If marker >= &HC0 AndAlso marker <= &HCF AndAlso
                   marker <> &HC4 AndAlso marker <> &HC8 AndAlso marker <> &HCC Then
                    ' C0=baseline, C1=extended, C2=progressive → displayable
                    Return marker = &HC0 OrElse marker = &HC1 OrElse marker = &HC2
                End If
                If (marker >= &HD0 AndAlso marker <= &HD9) OrElse marker = &H01 Then
                    pos += 2
                Else
                    Dim segLen = CInt(data(pos + 2)) * 256 + CInt(data(pos + 3))
                    If segLen < 2 Then Return True
                    pos += 2 + segLen
                End If
            Loop
            Return False   ' SOF not found → not a recognisable standard JPEG
        End Function

        ''' Walk JPEG segment chain (including SOS entropy data) to find exact byte length.
        Private Shared Function WalkJpegLength(data As Byte(), start As Integer) As Integer
            Dim pos = start + 2           ' skip SOI
            Dim limit = data.Length
            Do While pos + 3 < limit
                If data(pos) <> &HFF Then Return 0
                Dim marker = data(pos + 1)
                If marker = &HD9 Then Return pos - start + 2   ' EOI found
                If (marker >= &HD0 AndAlso marker <= &HD7) OrElse marker = &H01 Then
                    pos += 2 : Continue Do
                End If
                Dim segLen = CInt(data(pos + 2)) * 256 + CInt(data(pos + 3))
                If segLen < 2 Then Return 0
                If marker = &HDA Then   ' SOS – walk entropy-coded data
                    pos += 2 + segLen
                    Do While pos + 1 < limit
                        If data(pos) = &HFF Then
                            Dim nxt = data(pos + 1)
                            If nxt = &H00 OrElse nxt = &HFF Then
                                pos += 2
                            ElseIf nxt >= &HD0 AndAlso nxt <= &HD7 Then
                                pos += 2          ' RST marker
                            ElseIf nxt = &HD9 Then
                                Return pos - start + 2   ' EOI
                            Else
                                Exit Do           ' back to segment-walk
                            End If
                        Else
                            pos += 1
                        End If
                    Loop
                    Continue Do
                End If
                pos += 2 + segLen
            Loop
            Return 0
        End Function

        ' ── BMFF-based (CR3) ─────────────────────────────────────────────────────
        ' CR3 structure (Canon EOS):
        '   ftyp → moov → uuid[85c0b687...] → THMB (160×120 thumb)
        '                → uuid[eaf42b5e...] → Canon data block with large JPEG at offset 32

        Private Shared Function ExtractBmffPreview(filePath As String) As MemoryStream
            Dim best As MemoryStream = Nothing
            Using fs = File.OpenRead(filePath)
                Using br = New BinaryReader(fs)
                    CollectBmffJpegs(fs, br, 0, fs.Length, best)
                End Using
            End Using
            Return best
        End Function

        Private Shared Sub CollectBmffJpegs(fs As FileStream, br As BinaryReader, rangeStart As Long, rangeEnd As Long, ByRef best As MemoryStream)
            fs.Seek(rangeStart, SeekOrigin.Begin)

            Do While fs.Position + 8 <= rangeEnd
                Dim boxStart = fs.Position
                Dim size32 = ReadU32BE(br)
                Dim typBuf(3) As Byte
                br.Read(typBuf, 0, 4)
                Dim boxType = Text.Encoding.ASCII.GetString(typBuf)

                Dim boxSize As Long
                Dim dataStart As Long
                If size32 = 1 Then
                    boxSize = ReadU64BE(br)
                    dataStart = boxStart + 16
                Else
                    boxSize = If(size32 = 0, rangeEnd - boxStart, CLng(size32))
                    dataStart = boxStart + 8
                End If
                Dim boxEnd = Math.Min(boxStart + boxSize, rangeEnd)
                If boxEnd <= boxStart Then Exit Do

                Select Case boxType
                    Case "moov", "trak", "mdia", "minf", "stbl", "CRAW"
                        CollectBmffJpegs(fs, br, dataStart, boxEnd, best)

                    Case "uuid"
                        ' Skip 16-byte UUID, recurse into sub-boxes
                        Dim uuidContent = dataStart + 16
                        If uuidContent < boxEnd Then
                            CollectBmffJpegs(fs, br, uuidContent, boxEnd, best)
                            ' Canon uuid blocks embed JPEG as raw data (not sub-boxes) – scan content
                            Dim scanLen = CInt(Math.Min(boxEnd - uuidContent, 8 * 1024 * 1024L))
                            fs.Seek(uuidContent, SeekOrigin.Begin)
                            Dim buf(scanLen - 1) As Byte
                            br.Read(buf, 0, scanLen)
                            TryKeepLarger(FindJpeg(buf), best)
                        End If

                    Case "PRVW", "THMB"
                        Dim dataLen = CInt(Math.Min(boxEnd - dataStart, 4 * 1024 * 1024L))
                        If dataLen > 0 Then
                            fs.Seek(dataStart, SeekOrigin.Begin)
                            Dim boxData(dataLen - 1) As Byte
                            br.Read(boxData, 0, dataLen)
                            TryKeepLarger(FindJpeg(boxData), best)
                        End If
                End Select

                fs.Seek(boxEnd, SeekOrigin.Begin)
            Loop
        End Sub

        Private Shared Sub TryKeepLarger(candidate As MemoryStream, ByRef best As MemoryStream)
            If candidate Is Nothing Then Return
            If best Is Nothing OrElse candidate.Length > best.Length Then
                best = candidate
            End If
        End Sub

        ''' Quick backward-scan JPEG finder used by the BMFF walker for bounded buffers.
        Private Shared Function FindJpeg(data As Byte()) As MemoryStream
            For i = 0 To data.Length - 4
                If data(i) = &HFF AndAlso data(i + 1) = &HD8 AndAlso data(i + 2) = &HFF Then
                    For j = data.Length - 2 To i + 512 Step -1
                        If data(j) = &HFF AndAlso data(j + 1) = &HD9 Then
                            Dim len = j - i + 2
                            If len > 8192 Then Return New MemoryStream(data, i, len, False)
                            Exit For
                        End If
                    Next
                End If
            Next
            Return Nothing
        End Function

        ' ── BMFF read helpers ─────────────────────────────────────────────────────

        ' In VB.NET liefert "byteWert << n" wieder einen Byte und maskiert die Schiebeweite mit 7 -
        ' die Operanden müssen deshalb vor dem Shift geweitet werden.
        Private Shared Function ReadU32BE(br As BinaryReader) As UInteger
            Dim b = br.ReadBytes(4)
            Return CUInt((CLng(b(0)) << 24) Or (CLng(b(1)) << 16) Or (CLng(b(2)) << 8) Or CLng(b(3)))
        End Function

        Private Shared Function ReadU64BE(br As BinaryReader) As Long
            Dim b = br.ReadBytes(8)
            Return (CLng(b(0)) << 56) Or (CLng(b(1)) << 48) Or (CLng(b(2)) << 40) Or (CLng(b(3)) << 32) Or
                   (CLng(b(4)) << 24) Or (CLng(b(5)) << 16) Or (CLng(b(6)) << 8) Or CLng(b(7))
        End Function

    End Class

End Namespace
