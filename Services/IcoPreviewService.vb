Imports System
Imports System.IO
Imports System.Linq
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Dekodiert ICO-Dateien, da SkiaSharps Codec das Format nicht kennt. Liefert wie
    ''' RawPreviewService und SvgPreviewService einen dekodierbaren MemoryStream (PNG), den die
    ''' bestehenden Thumbnail- und Vorschau-Pfade wie ein normales Bild weiterverarbeiten.
    '''
    ''' Eine ICO-Datei ist ein Container mit mehreren Auflösungen. Jeder Eintrag ist entweder eine
    ''' vollständige PNG-Datei (ab Windows Vista üblich) oder ein "DIB": ein BITMAPINFOHEADER ohne
    ''' Dateikopf, gefolgt von Farbtabelle, Bilddaten und einer 1-Bit-Transparenzmaske. Ausgewählt
    ''' wird der größte Eintrag, bei Gleichstand der mit der höheren Farbtiefe.
    ''' </summary>
    Public NotInheritable Class IcoPreviewService

        Private Sub New()
        End Sub

        Private Const IconDirSize As Integer = 6
        Private Const IconDirEntrySize As Integer = 16
        Private Const BitmapInfoHeaderSize As Integer = 40

        Private Shared ReadOnly PngSignature As Byte() = {&H89, &H50, &H4E, &H47, &HD, &HA, &H1A, &HA}

        Public Shared Function IsSupportedIco(filePath As String) As Boolean
            Return String.Equals(Path.GetExtension(filePath), ".ico", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' Liefert einen MemoryStream mit PNG-Daten (Position 0) oder Nothing bei Fehler.
        Public Shared Function ExtractPreview(filePath As String) As MemoryStream
            Try
                Dim data = File.ReadAllBytes(filePath)
                Dim bitmap = DecodeBestEntry(data)
                If bitmap Is Nothing Then Return Nothing

                Using bitmap
                    Using image = SKImage.FromBitmap(bitmap)
                        Using encoded = image.Encode(SKEncodedImageFormat.Png, 100)
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

        Private Shared Function DecodeBestEntry(data As Byte()) As SKBitmap
            If data Is Nothing OrElse data.Length < IconDirSize Then Return Nothing
            ' Reserviert muss 0 sein, Typ 1 = Icon (2 wäre ein Mauszeiger).
            If BitConverter.ToUInt16(data, 0) <> 0US OrElse BitConverter.ToUInt16(data, 2) <> 1US Then Return Nothing

            Dim count As Integer = BitConverter.ToUInt16(data, 4)
            If count <= 0 OrElse data.Length < IconDirSize + count * IconDirEntrySize Then Return Nothing

            Dim best As Integer = -1
            Dim bestPixels As Integer = -1
            Dim bestDepth As Integer = -1
            For i = 0 To count - 1
                Dim entry = IconDirSize + i * IconDirEntrySize
                ' Kantenlänge 0 steht laut Format für 256.
                Dim width = If(data(entry) = 0, 256, CInt(data(entry)))
                Dim height = If(data(entry + 1) = 0, 256, CInt(data(entry + 1)))
                Dim depth As Integer = BitConverter.ToUInt16(data, entry + 6)
                Dim size = BitConverter.ToInt32(data, entry + 8)
                Dim offset = BitConverter.ToInt32(data, entry + 12)
                If size <= 0 OrElse offset < 0 OrElse offset + size > data.Length Then Continue For

                Dim pixels = width * height
                If pixels > bestPixels OrElse (pixels = bestPixels AndAlso depth > bestDepth) Then
                    best = entry
                    bestPixels = pixels
                    bestDepth = depth
                End If
            Next
            If best < 0 Then Return Nothing

            Dim payloadSize = BitConverter.ToInt32(data, best + 8)
            Dim payloadOffset = BitConverter.ToInt32(data, best + 12)

            If IsPng(data, payloadOffset, payloadSize) Then
                Return SKBitmap.Decode(New MemoryStream(data, payloadOffset, payloadSize, writable:=False))
            End If
            Return DecodeDib(data, payloadOffset, payloadSize)
        End Function

        Private Shared Function IsPng(data As Byte(), offset As Integer, length As Integer) As Boolean
            If length < PngSignature.Length Then Return False
            For i = 0 To PngSignature.Length - 1
                If data(offset + i) <> PngSignature(i) Then Return False
            Next
            Return True
        End Function

        ''' <summary>
        ''' Liest einen DIB-Eintrag: BITMAPINFOHEADER, Farbtabelle, XOR-Bilddaten und AND-Maske.
        ''' Die Zeilen stehen von unten nach oben und sind auf 4 Byte aufgefüllt. biHeight zählt
        ''' Bild und Maske zusammen, ist also doppelt so hoch wie das Icon.
        ''' </summary>
        Private Shared Function DecodeDib(data As Byte(), offset As Integer, length As Integer) As SKBitmap
            If length < BitmapInfoHeaderSize Then Return Nothing
            If BitConverter.ToInt32(data, offset) <> BitmapInfoHeaderSize Then Return Nothing

            Dim width = BitConverter.ToInt32(data, offset + 4)
            Dim doubledHeight = BitConverter.ToInt32(data, offset + 8)
            Dim bitCount As Integer = BitConverter.ToUInt16(data, offset + 14)
            Dim compression = BitConverter.ToInt32(data, offset + 16)
            Dim colorsUsed = BitConverter.ToInt32(data, offset + 32)

            ' Nur unkomprimierte DIBs (BI_RGB). BI_BITFIELDS und JPEG/PNG-in-DIB sind bei Icons
            ' praktisch nicht anzutreffen.
            If compression <> 0 Then Return Nothing
            Dim height = doubledHeight \ 2
            If width <= 0 OrElse height <= 0 OrElse width > 4096 OrElse height > 4096 Then Return Nothing
            If bitCount <> 1 AndAlso bitCount <> 4 AndAlso bitCount <> 8 AndAlso bitCount <> 24 AndAlso bitCount <> 32 Then Return Nothing

            Dim paletteEntries = 0
            If bitCount <= 8 Then
                paletteEntries = If(colorsUsed > 0, colorsUsed, 1 << bitCount)
            End If
            Dim paletteOffset = offset + BitmapInfoHeaderSize
            Dim pixelOffset = paletteOffset + paletteEntries * 4

            Dim xorRowSize = ((width * bitCount + 31) \ 32) * 4
            Dim andRowSize = ((width + 31) \ 32) * 4
            Dim maskOffset = pixelOffset + xorRowSize * height
            Dim hasMask = maskOffset + andRowSize * height <= offset + length

            If pixelOffset + xorRowSize * height > offset + length Then Return Nothing

            Dim bitmap As New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul)
            Dim anyAlpha = False

            For y = 0 To height - 1
                ' Zeile 0 des Bildes ist die letzte Zeile im Datenstrom.
                Dim rowStart = pixelOffset + (height - 1 - y) * xorRowSize
                For x = 0 To width - 1
                    Dim color As SKColor
                    Select Case bitCount
                        Case 32
                            Dim p = rowStart + x * 4
                            Dim a = data(p + 3)
                            If a <> 0 Then anyAlpha = True
                            color = New SKColor(data(p + 2), data(p + 1), data(p), a)
                        Case 24
                            Dim p = rowStart + x * 3
                            color = New SKColor(data(p + 2), data(p + 1), data(p), 255)
                        Case Else
                            Dim index = ReadPaletteIndex(data, rowStart, x, bitCount)
                            If index >= paletteEntries Then index = 0
                            Dim p = paletteOffset + index * 4
                            color = New SKColor(data(p + 2), data(p + 1), data(p), 255)
                    End Select
                    bitmap.SetPixel(x, y, color)
                Next
            Next

            ' 32-Bit-Icons tragen ihre Transparenz im Alphakanal. Ist der komplett leer - was bei
            ' älteren Dateien vorkommt -, wäre das Bild sonst unsichtbar; dann gilt die AND-Maske.
            Dim useMask = hasMask AndAlso (bitCount <> 32 OrElse Not anyAlpha)
            If bitCount = 32 AndAlso Not anyAlpha Then
                For y = 0 To height - 1
                    For x = 0 To width - 1
                        Dim c = bitmap.GetPixel(x, y)
                        bitmap.SetPixel(x, y, New SKColor(c.Red, c.Green, c.Blue, 255))
                    Next
                Next
            End If

            If useMask Then
                For y = 0 To height - 1
                    Dim rowStart = maskOffset + (height - 1 - y) * andRowSize
                    For x = 0 To width - 1
                        Dim bit = (data(rowStart + (x >> 3)) >> (7 - (x And 7))) And 1
                        ' Gesetztes Maskenbit bedeutet "durchsichtig".
                        If bit = 1 Then bitmap.SetPixel(x, y, SKColors.Transparent)
                    Next
                Next
            End If

            Return bitmap
        End Function

        Private Shared Function ReadPaletteIndex(data As Byte(), rowStart As Integer, x As Integer, bitCount As Integer) As Integer
            Select Case bitCount
                Case 8
                    Return data(rowStart + x)
                Case 4
                    Dim b = data(rowStart + (x >> 1))
                    Return If((x And 1) = 0, (b >> 4) And &HF, b And &HF)
                Case Else ' 1
                    Dim b = data(rowStart + (x >> 3))
                    Return (b >> (7 - (x And 7))) And 1
            End Select
        End Function

    End Class

End Namespace
