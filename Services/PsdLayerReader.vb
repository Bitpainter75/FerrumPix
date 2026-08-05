Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Liest die EBENEN einer Photoshop-Datei, die PsdPreviewService bewusst überspringt. Damit lässt
    ''' sich eine .psd als Ebenenstapel öffnen, statt nur als fertiges Gesamtbild.
    '''
    ''' Was hier herauskommt, sind Bildebenen: Rechteck, Name, Deckkraft, Mischmethode, Beschneidung
    ''' an der Ebene darunter, Sichtbarkeit und die Bildpunkte selbst. Mehr gibt das Format an dieser
    ''' Stelle nicht her - und mehr braucht es auch nicht, denn genau darauf bildet der eigene
    ''' Ebenenstapel ab.
    '''
    ''' Bewusst nicht gelesen:
    ''' - Korrekturebenen und Ebeneneffekte. Photoshop legt für sie KEINE gerechneten Bildpunkte ab,
    '''   nur ihre Einstellungen in je eigenen, kaum dokumentierten Datensätzen. Sie kämen als leere
    '''   Ebene heraus und würden ein falsches Bild ergeben, deshalb bleiben sie draußen. Ihr Beitrag
    '''   steckt im Gesamtbild, das der Aufrufer als Grundlage nimmt.
    ''' - Ebenenmasken. Sie haben ein eigenes Rechteck und einen eigenen Vorgabewert außerhalb davon;
    '''   ihre Kanäle werden gelesen und verworfen, damit die Reihenfolge nicht verrutscht.
    ''' - Gruppen. Photoshop schreibt sie als Ebenen mit dem Namen "&lt;/Layer group&gt;" um den Inhalt
    '''   herum; sie werden übersprungen, der Inhalt bleibt flach nebeneinander.
    ''' - 16 und 32 Bit je Kanal, sowie alles außer RGB. Dann liefert der Leser Nothing, und der
    '''   Aufrufer bleibt beim flachen Gesamtbild.
    ''' </summary>
    Public NotInheritable Class PsdLayerReader

        Private Sub New()
        End Sub

        ''' Obergrenze für die Summe aller Ebenenflächen. Eine Photoshop-Datei mit hundert Ebenen in
        ''' voller Größe belegt sonst mehr Speicher, als sinnvoll zu halten ist.
        Private Const MaxTotalLayerPixels As Long = 400_000_000L
        Private Const MaxLayers As Integer = 300

        Public Class PsdLayerInfo
            Public Property Name As String = ""
            Public Property Left As Integer
            Public Property Top As Integer
            Public Property Width As Integer
            Public Property Height As Integer
            Public Property OpacityPercent As Single = 100
            ''' Mischmethode in der Schreibweise von FerrumPix, siehe PsdWriterService.
            Public Property BlendMode As String = "Normal"
            Public Property ClipToLayerBelow As Boolean = False
            Public Property IsVisible As Boolean = True
            ''' Die Bildpunkte der Ebene, so groß wie ihr Rechteck. Gehört dem Aufrufer.
            Public Property Pixels As SKBitmap
        End Class

        ''' <summary>Rückübersetzung der Vierzeichenschlüssel. Was hier fehlt, wird "Normal" - die
        ''' Bildpunkte stimmen dann, nur das Zusammenrechnen weicht ab.</summary>
        Private Shared ReadOnly BlendNames As New Dictionary(Of String, String)(StringComparer.Ordinal) From {
            {"norm", "Normal"}, {"mul ", "Multiply"}, {"scrn", "Screen"}, {"over", "Overlay"},
            {"dark", "Darken"}, {"lite", "Lighten"}, {"div ", "ColorDodge"}, {"idiv", "ColorBurn"},
            {"hLit", "HardLight"}, {"sLit", "SoftLight"}, {"diff", "Difference"}, {"smud", "Exclusion"},
            {"hue ", "Hue"}, {"sat ", "Saturation"}, {"colr", "Color"}, {"lum ", "Luminosity"},
            {"lddg", "LinearDodge"}, {"lbrn", "LinearBurn"}, {"vLit", "VividLight"}, {"lLit", "LinearLight"},
            {"pLit", "PinLight"}, {"hMix", "HardMix"}
        }

        ''' <summary>Die Datei als Ganzes: Maße des Dokuments und die Ebenen darin.</summary>
        Public Class PsdDocumentInfo
            Public Property Width As Integer
            Public Property Height As Integer
            Public Property Layers As New List(Of PsdLayerInfo)()
        End Class

        ''' <summary>Liefert die Bildebenen von unten nach oben, oder Nothing, wenn die Datei keine
        ''' Ebenen trägt oder in einer Spielart vorliegt, die dieser Leser nicht beherrscht.</summary>
        Public Shared Function ReadLayers(filePath As String) As List(Of PsdLayerInfo)
            Return ReadDocument(filePath)?.Layers
        End Function

        ''' <summary>Wie <see cref="ReadLayers"/>, liefert zusätzlich die Maße des Dokuments - der
        ''' Import braucht sie, weil eine Ebene kleiner sein kann als das Bild und der Ebenenstapel
        ''' trotzdem auf die richtige Fläche gehört.</summary>
        Public Shared Function ReadDocument(filePath As String) As PsdDocumentInfo
            Dim result As PsdDocumentInfo = Nothing
            Try
                Using fs = File.OpenRead(filePath)
                    result = ReadLayersCore(fs)
                End Using
            Catch
                result = Nothing
            End Try

            If result IsNot Nothing AndAlso result.Layers.Count = 0 Then Return Nothing
            Return result
        End Function

        ''' <summary>Wie viele Ebenen im Verzeichnis der Datei stehen - einschliesslich derer, die
        ''' keine Bildpunkte tragen. Die Differenz zu den gelieferten Ebenen sind die
        ''' uebersprungenen: Korrekturebenen, Effekte, Gruppenmarken. 0, wenn sich das nicht
        ''' feststellen laesst - eine erfundene Zahl waere schlechter als keine.</summary>
        Public Shared Function CountLayerRecords(filePath As String) As Integer
            Try
                Using fs = File.OpenRead(filePath)
                    Dim buf(25) As Byte
                    If Not ReadExactly(fs, buf, 26) Then Return 0
                    If buf(0) <> Asc("8") OrElse buf(1) <> Asc("B") OrElse buf(2) <> Asc("P") OrElse buf(3) <> Asc("S") Then Return 0
                    Dim isPsb = (CInt(buf(4)) * 256 + buf(5)) = 2

                    If Not SkipBlock(fs, ReadU32(fs)) Then Return 0
                    If Not SkipBlock(fs, ReadU32(fs)) Then Return 0
                    Dim sectionLen = If(isPsb, ReadU64(fs), CLng(ReadU32(fs)))
                    If sectionLen <= 0 Then Return 0
                    Dim infoLen = If(isPsb, ReadU64(fs), CLng(ReadU32(fs)))
                    If infoLen <= 0 Then Return 0

                    Dim raw = ReadU16(fs)
                    If raw > 32767 Then raw -= 65536
                    Return Math.Abs(raw)
                End Using
            Catch
                Return 0
            End Try
        End Function

        Private Shared Function ReadLayersCore(fs As FileStream) As PsdDocumentInfo
            Dim buf(25) As Byte
            If Not ReadExactly(fs, buf, 26) Then Return Nothing
            If buf(0) <> Asc("8") OrElse buf(1) <> Asc("B") OrElse buf(2) <> Asc("P") OrElse buf(3) <> Asc("S") Then Return Nothing

            Dim version = CInt(buf(4)) * 256 + buf(5)
            If version <> 1 AndAlso version <> 2 Then Return Nothing
            Dim isPsb = version = 2
            Dim docHeight = CInt((CLng(buf(14)) << 24) Or (CLng(buf(15)) << 16) Or (CLng(buf(16)) << 8) Or buf(17))
            Dim docWidth = CInt((CLng(buf(18)) << 24) Or (CLng(buf(19)) << 16) Or (CLng(buf(20)) << 8) Or buf(21))
            Dim depth = CInt(buf(22)) * 256 + buf(23)
            Dim colorMode = CInt(buf(24)) * 256 + buf(25)

            ' Nur 8 Bit RGB. Bei allem anderen müssten die Bildpunkte umgerechnet werden, und ein
            ' halb richtiges Ergebnis ist hier schlechter als das flache Gesamtbild.
            If depth <> 8 OrElse colorMode <> 3 Then Return Nothing

            If Not SkipBlock(fs, ReadU32(fs)) Then Return Nothing  ' Farbmodus-Daten
            If Not SkipBlock(fs, ReadU32(fs)) Then Return Nothing  ' Bildressourcen

            Dim sectionLen = If(isPsb, ReadU64(fs), CLng(ReadU32(fs)))
            If sectionLen <= 0 Then Return Nothing
            Dim sectionEnd = fs.Position + sectionLen
            If sectionEnd > fs.Length Then Return Nothing

            Dim infoLen = If(isPsb, ReadU64(fs), CLng(ReadU32(fs)))
            If infoLen <= 0 Then Return Nothing
            Dim infoEnd = fs.Position + infoLen
            If infoEnd > fs.Length Then Return Nothing

            ' Negativ heißt: der erste Alphakanal des Gesamtbilds ist die Transparenz. Für die
            ' Ebenen selbst ändert das nichts, nur das Vorzeichen der Anzahl.
            Dim rawCount = ReadU16(fs)
            If rawCount > 32767 Then rawCount -= 65536
            Dim layerCount = Math.Abs(rawCount)
            If layerCount = 0 OrElse layerCount > MaxLayers Then Return Nothing

            Dim records As New List(Of LayerRecord)()
            Dim totalPixels As Long = 0
            For i = 0 To layerCount - 1
                Dim rec = ReadLayerRecord(fs, isPsb)
                If rec Is Nothing Then Return Nothing
                totalPixels += CLng(Math.Max(0, rec.Right - rec.Left)) * Math.Max(0, rec.Bottom - rec.Top)
                If totalPixels > MaxTotalLayerPixels Then Return Nothing
                records.Add(rec)
            Next

            Dim doc As New PsdDocumentInfo With {.Width = docWidth, .Height = docHeight}
            Dim layers = doc.Layers
            For Each rec In records
                Dim bmp = ReadChannelData(fs, rec)
                If bmp Is Nothing AndAlso rec.HasPixels Then Return Nothing
                If bmp Is Nothing Then Continue For

                layers.Add(New PsdLayerInfo With {
                    .Name = rec.Name,
                    .Left = rec.Left,
                    .Top = rec.Top,
                    .Width = rec.Right - rec.Left,
                    .Height = rec.Bottom - rec.Top,
                    .OpacityPercent = CSng(rec.Opacity) * 100.0F / 255.0F,
                    .BlendMode = ResolveBlendName(rec.BlendKey),
                    .ClipToLayerBelow = rec.Clipping <> 0,
                    .IsVisible = (rec.Flags And 2) = 0,
                    .Pixels = bmp
                })
            Next

            Return doc
        End Function

        ' ── Ebenenverzeichnis ────────────────────────────────────────────────────

        Private Class LayerRecord
            Public Property Top As Integer
            Public Property Left As Integer
            Public Property Bottom As Integer
            Public Property Right As Integer
            Public Property Name As String = ""
            Public Property BlendKey As String = "norm"
            Public Property Opacity As Integer = 255
            Public Property Clipping As Integer
            Public Property Flags As Integer
            ''' Kanalkennung und Länge, in genau der Reihenfolge, in der die Daten später folgen.
            Public Property Channels As New List(Of Integer())()
            ''' Eine Gruppenmarke oder eine Ebene ohne Fläche trägt keine Bildpunkte.
            Public Property HasPixels As Boolean = True
        End Class

        Private Shared Function ReadLayerRecord(fs As FileStream, isPsb As Boolean) As LayerRecord
            Dim rec As New LayerRecord With {
                .Top = CInt(ReadU32Signed(fs)),
                .Left = CInt(ReadU32Signed(fs)),
                .Bottom = CInt(ReadU32Signed(fs)),
                .Right = CInt(ReadU32Signed(fs))
            }

            Dim channelCount = ReadU16(fs)
            If channelCount < 1 OrElse channelCount > 56 Then Return Nothing
            For i = 0 To channelCount - 1
                Dim id = ReadU16(fs)
                If id > 32767 Then id -= 65536
                Dim len = If(isPsb, ReadU64(fs), CLng(ReadU32(fs)))
                If len < 0 Then Return Nothing
                rec.Channels.Add(New Integer() {id, CInt(Math.Min(len, Integer.MaxValue))})
            Next

            Dim sig(3) As Byte
            If Not ReadExactly(fs, sig, 4) Then Return Nothing
            If Encoding.ASCII.GetString(sig) <> "8BIM" Then Return Nothing
            Dim key(3) As Byte
            If Not ReadExactly(fs, key, 4) Then Return Nothing
            rec.BlendKey = Encoding.ASCII.GetString(key)

            rec.Opacity = fs.ReadByte()
            rec.Clipping = fs.ReadByte()
            rec.Flags = fs.ReadByte()
            If fs.ReadByte() < 0 Then Return Nothing  ' Füllbyte

            Dim extraLen = ReadU32(fs)
            Dim extraEnd = fs.Position + extraLen
            If extraEnd > fs.Length Then Return Nothing

            Dim maskLen = ReadU32(fs)
            If Not SkipBlock(fs, maskLen) Then Return Nothing
            Dim rangesLen = ReadU32(fs)
            If Not SkipBlock(fs, rangesLen) Then Return Nothing

            ' Alter Name: Längenbyte, Text, das ganze Feld auf ein Vielfaches von vier aufgefüllt.
            Dim nameLen = fs.ReadByte()
            If nameLen < 0 Then Return Nothing
            Dim nameBytes(Math.Max(0, nameLen - 1)) As Byte
            If nameLen > 0 AndAlso Not ReadExactly(fs, nameBytes, nameLen) Then Return Nothing
            rec.Name = If(nameLen > 0, Encoding.GetEncoding(28591).GetString(nameBytes, 0, nameLen), "")
            Dim consumed = 1 + nameLen
            While consumed Mod 4 <> 0
                If fs.ReadByte() < 0 Then Return Nothing
                consumed += 1
            End While

            ' Zusatzblöcke. Interessant sind zwei: der Unicode-Name und die Gruppenmarke.
            While fs.Position + 12 <= extraEnd
                Dim blockSig(3) As Byte
                If Not ReadExactly(fs, blockSig, 4) Then Exit While
                Dim s = Encoding.ASCII.GetString(blockSig)
                If s <> "8BIM" AndAlso s <> "8B64" Then Exit While
                Dim blockKey(3) As Byte
                If Not ReadExactly(fs, blockKey, 4) Then Exit While
                Dim bk = Encoding.ASCII.GetString(blockKey)
                Dim blockLen = ReadU32(fs)
                Dim blockStart = fs.Position
                If blockStart + blockLen > extraEnd Then Exit While

                If bk = "luni" AndAlso blockLen >= 4 Then
                    Dim charCount = ReadU32(fs)
                    If charCount >= 0 AndAlso charCount < 4096 AndAlso blockStart + 4 + charCount * 2 <= extraEnd Then
                        Dim sb As New StringBuilder()
                        For i = 1L To charCount
                            Dim hi = fs.ReadByte()
                            Dim lo = fs.ReadByte()
                            If hi < 0 OrElse lo < 0 Then Exit For
                            sb.Append(ChrW(hi * 256 + lo))
                        Next
                        If sb.Length > 0 Then rec.Name = sb.ToString()
                    End If
                ElseIf bk = "lsct" AndAlso blockLen >= 4 Then
                    ' Abschnittsmarke: 1 und 2 sind Gruppenanfänge, 3 ist das Ende. Solche Ebenen
                    ' tragen keine Bildpunkte und werden übersprungen.
                    Dim sectionType = ReadU32(fs)
                    If sectionType >= 1 AndAlso sectionType <= 3 Then rec.HasPixels = False
                End If

                fs.Seek(blockStart + blockLen + (blockLen And 1L), SeekOrigin.Begin)
            End While

            fs.Seek(extraEnd, SeekOrigin.Begin)

            If rec.Right <= rec.Left OrElse rec.Bottom <= rec.Top Then rec.HasPixels = False
            Return rec
        End Function

        ' ── Kanaldaten ───────────────────────────────────────────────────────────

        ''' <summary>Liest die Kanäle einer Ebene und setzt sie zu einem Bitmap zusammen. Kanäle, die
        ''' nicht zum Bild gehören - Masken etwa - werden mitgelesen und verworfen, sonst verrutscht
        ''' der Lesezeiger für alle folgenden Ebenen.</summary>
        Private Shared Function ReadChannelData(fs As FileStream, rec As LayerRecord) As SKBitmap
            Dim width = rec.Right - rec.Left
            Dim height = rec.Bottom - rec.Top

            Dim red As Byte() = Nothing, green As Byte() = Nothing, blue As Byte() = Nothing, alpha As Byte() = Nothing

            For Each ch In rec.Channels
                Dim id = ch(0)
                Dim declaredLen = ch(1)
                Dim blockEnd = fs.Position + declaredLen

                ' Nur die vier Bildkanäle einer Ebene mit Fläche werden ausgewertet.
                Dim wanted = rec.HasPixels AndAlso (id = 0 OrElse id = 1 OrElse id = 2 OrElse id = -1)
                If wanted Then
                    Dim plane = ReadPlane(fs, width, height)
                    If plane IsNot Nothing Then
                        Select Case id
                            Case 0 : red = plane
                            Case 1 : green = plane
                            Case 2 : blue = plane
                            Case -1 : alpha = plane
                        End Select
                    End If
                End If

                ' In jedem Fall exakt hinter den Block springen: die angegebene Länge ist die
                ' Wahrheit, nicht das, was das Entpacken verbraucht hat.
                If blockEnd > fs.Length Then Return Nothing
                fs.Seek(blockEnd, SeekOrigin.Begin)
            Next

            If Not rec.HasPixels Then Return Nothing
            If red Is Nothing OrElse green Is Nothing OrElse blue Is Nothing Then Return Nothing

            Dim info = New SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul)
            Dim bmp = New SKBitmap(info)
            Try
                Dim count = width * height
                Dim buffer(count * 4 - 1) As Byte
                For i = 0 To count - 1
                    buffer(i * 4) = red(i)
                    buffer(i * 4 + 1) = green(i)
                    buffer(i * 4 + 2) = blue(i)
                    buffer(i * 4 + 3) = If(alpha IsNot Nothing, alpha(i), CByte(255))
                Next
                Runtime.InteropServices.Marshal.Copy(buffer, 0, bmp.GetPixels(), buffer.Length)
                Return bmp
            Catch
                bmp.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Ein Kanal einer Ebene: Kompressionsmarke, bei RLE die Zeilenlängen dieses einen
        ''' Kanals, danach die Zeilen. Anders als beim Gesamtbild stehen die Längen NICHT gesammelt
        ''' für alle Kanäle vorneweg.</summary>
        Private Shared Function ReadPlane(fs As FileStream, width As Integer, height As Integer) As Byte()
            If width < 1 OrElse height < 1 Then Return Nothing
            Dim compression = ReadU16(fs)
            Dim plane(width * height - 1) As Byte

            If compression = 0 Then
                For row = 0 To height - 1
                    If Not ReadExactly(fs, plane, width, row * width) Then Return Nothing
                Next
                Return plane
            End If

            If compression <> 1 Then Return Nothing  ' 2/3 wären ZIP, hier nicht unterstützt

            Dim rowLengths(height - 1) As Integer
            For row = 0 To height - 1
                rowLengths(row) = ReadU16(fs)
                If rowLengths(row) < 0 OrElse rowLengths(row) > width * 2 + 64 Then Return Nothing
            Next

            Dim packed(Math.Max(1, width * 2 + 64) - 1) As Byte
            Dim rowBuffer(width - 1) As Byte
            For row = 0 To height - 1
                Dim len = rowLengths(row)
                If packed.Length < len Then ReDim packed(len - 1)
                If Not ReadExactly(fs, packed, len) Then Return Nothing
                If Not PsdPreviewService.UnpackBits(packed, len, rowBuffer, width) Then Return Nothing
                Array.Copy(rowBuffer, 0, plane, row * width, width)
            Next
            Return plane
        End Function

        Private Shared Function ResolveBlendName(key As String) As String
            Dim name As String = Nothing
            If key IsNot Nothing AndAlso BlendNames.TryGetValue(key, name) Then Return name
            Return "Normal"
        End Function

        ' ── Lese-Helfer ──────────────────────────────────────────────────────────

        Private Shared Function ReadExactly(fs As FileStream, buffer As Byte(), count As Integer,
                                            Optional offset As Integer = 0) As Boolean
            Dim total = 0
            While total < count
                Dim n = fs.Read(buffer, offset + total, count - total)
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

        ' Vor dem Schieben weiten, sonst bliebe der Ausdruck in VB ein Byte und ergäbe still 0.
        Private Shared Function ReadU32(fs As FileStream) As Long
            Dim b(3) As Byte
            If Not ReadExactly(fs, b, 4) Then Throw New EndOfStreamException()
            Return (CLng(b(0)) << 24) Or (CLng(b(1)) << 16) Or (CLng(b(2)) << 8) Or CLng(b(3))
        End Function

        ''' Die Ebenenrechtecke dürfen negativ sein: eine Ebene kann über den Bildrand hinausragen.
        Private Shared Function ReadU32Signed(fs As FileStream) As Long
            Dim v = ReadU32(fs)
            If v > 2147483647L Then v -= 4294967296L
            Return v
        End Function

        Private Shared Function ReadU64(fs As FileStream) As Long
            Dim b(7) As Byte
            If Not ReadExactly(fs, b, 8) Then Throw New EndOfStreamException()
            Return (CLng(b(0)) << 56) Or (CLng(b(1)) << 48) Or (CLng(b(2)) << 40) Or (CLng(b(3)) << 32) Or
                   (CLng(b(4)) << 24) Or (CLng(b(5)) << 16) Or (CLng(b(6)) << 8) Or CLng(b(7))
        End Function

    End Class

End Namespace
