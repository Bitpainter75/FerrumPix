Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Liest die EBENEN einer Photoshop-Datei, die PsdPreviewService bewusst überspringt. Damit lässt
    ''' sich eine .psd als Ebenenstapel öffnen, statt nur als fertiges Gesamtbild.
    '''
    ''' Was hier herauskommt, sind Bildebenen: Rechteck, Name, Deckkraft, Mischmethode, Beschneidung
    ''' an der Ebene darunter, Sichtbarkeit, die Ebenenmaske und die Bildpunkte selbst. Dazu die
    ''' Gruppen, in denen sie liegen. Mehr gibt das Format an dieser Stelle nicht her - und mehr
    ''' braucht es auch nicht, denn genau darauf bildet der eigene Ebenenstapel ab.
    '''
    ''' Kanäle liegen unkomprimiert, als RLE oder als ZIP vor. Alle drei werden gelesen; beim ZIP
    ''' gibt es zwei Spielarten, die zweite legt jeden Punkt als Differenz zu seinem linken Nachbarn
    ''' ab (siehe <see cref="ReadZipPlane"/>).
    '''
    ''' Acht und sechzehn Bit je Kanal, RGB, Graustufen und CMYK. Sechzehn Bit werden auf acht
    ''' gebracht, wie es der flache Weg seit jeher tut - der Ebenenstapel rechnet in acht Bit.
    '''
    ''' Gruppen sind im Format kein Behälter, sondern eine KLAMMER aus zwei Ebenen ohne Bildpunkte.
    ''' Sie werden als solche weitergereicht (<see cref="PsdLayerInfo.SectionType"/>); wer daraus
    ''' eine Struktur baut, ist der Import.
    '''
    ''' Bewusst nicht gelesen:
    ''' - Korrekturebenen und Ebeneneffekte. Photoshop legt für sie KEINE gerechneten Bildpunkte ab,
    '''   nur ihre Einstellungen in je eigenen, kaum dokumentierten Datensätzen. Sie kämen als leere
    '''   Ebene heraus und würden ein falsches Bild ergeben, deshalb bleiben sie draußen. Ihr Beitrag
    '''   steckt im Gesamtbild, das der Aufrufer als Grundlage nimmt.
    ''' - Vektormasken. Nur die gemalte Ebenenmaske kommt mit (Kanal -2); die zusammengerechnete
    '''   Fassung aus beidem (Kanal -3) bliebe ohne ihren Vektoranteil eine halbe Wahrheit.
    ''' - 32 Bit je Kanal. Dann liefert der Leser Nothing, und der Aufrufer bleibt beim flachen
    '''   Gesamtbild, das diese Variante besser auffangen kann.
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
            ''' <summary>Der Wortlaut, wenn es eine Textebene ist, sonst "". Nur der Wortlaut - was
            ''' die Schrift angeht, siehe PsdTextReader.</summary>
            Public Property TextContent As String = ""

            ''' <summary>Die Ebenenmaske als Alpha8-Raster, oder Nothing. Gehört dem Aufrufer.
            '''
            ''' Ihr Rechteck ist ein EIGENES und hat mit dem der Ebene nichts zu tun: eine Maske darf
            ''' kleiner sein, größer, versetzt. Deshalb stehen die vier Werte hier noch einmal, statt
            ''' die der Ebene mitzubenutzen.
            '''
            ''' Die Werte sind Deckung, wie überall: 255 zeigt die Ebene, 0 versteckt sie. Photoshop
            ''' meint dasselbe (Weiß zeigt, Schwarz versteckt), also wird nichts umgekehrt.</summary>
            Public Property MaskPixels As SKBitmap
            Public Property MaskLeft As Integer
            Public Property MaskTop As Integer
            Public Property MaskWidth As Integer
            Public Property MaskHeight As Integer

            ''' <summary>Die Deckung AUSSERHALB des Maskenrechtecks. Nicht wegzudenken: eine Maske,
            ''' die nur ein Loch in eine sonst sichtbare Ebene stanzt, steht als kleines Rechteck mit
            ''' Vorgabewert 255 in der Datei. Wer den Wert übergeht, versteckt die ganze Ebene bis auf
            ''' dieses Rechteck - also genau das Gegenteil.
            '''
            ''' Üblich sind 0 und 255, aber in der Datei steht ein ganzes Byte, und ein Wert dazwischen
            ''' heißt: außerhalb deckt die Ebene eben halb. Er wird deshalb unverändert übernommen.</summary>
            Public Property MaskDefaultValue As Byte = 0

            ''' <summary>Die Maske ist in Photoshop abgeschaltet. Sie bleibt erhalten und wirkt
            ''' nicht - dasselbe kennt der eigene Maskentyp als <c>IsDisabled</c>.</summary>
            Public Property MaskDisabled As Boolean

            Public ReadOnly Property HasMask As Boolean
                Get
                    Return MaskPixels IsNot Nothing AndAlso MaskWidth > 0 AndAlso MaskHeight > 0
                End Get
            End Property

            ''' <summary>0 für eine gewöhnliche Ebene, sonst eine GRUPPENMARKE: 1 ist eine offene
            ''' Gruppe, 2 eine zugeklappte, 3 das untere Ende einer Gruppe.
            '''
            ''' Die Reihenfolge in der Datei geht von UNTEN nach oben, und eine Gruppe steht darin
            ''' verkehrt herum: erst das Ende (3), dann ihr Inhalt, dann die Zeile mit Namen,
            ''' Deckkraft und Mischmethode (1 oder 2). Wer die Struktur nachbaut, öffnet also bei 3
            ''' und schließt bei 1 oder 2 - nicht umgekehrt.</summary>
            Public Property SectionType As Integer = 0

            Public ReadOnly Property IsGroupMarker As Boolean
                Get
                    Return SectionType <> 0
                End Get
            End Property
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
        ''' <param name="metadataOnly">Nur das Verzeichnis lesen, die Bildpunkte überspringen. Für
        ''' die Frage "gibt es hier Textebenen?" - die kostet sonst das Entpacken jeder einzelnen
        ''' Ebene, und gleich darauf wird die Datei ohnehin richtig geladen.</param>
        Public Shared Function ReadDocument(filePath As String, Optional metadataOnly As Boolean = False) As PsdDocumentInfo
            Dim result As PsdDocumentInfo = Nothing
            Try
                Using fs = File.OpenRead(filePath)
                    result = ReadLayersCore(fs, metadataOnly)
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

        Private Shared Function ReadLayersCore(fs As FileStream, Optional metadataOnly As Boolean = False) As PsdDocumentInfo
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

            ' 8 und 16 Bit, RGB, Graustufen und CMYK. Sechzehn Bit werden dabei auf acht gebracht,
            ' wie es der flache Weg seit jeher tut - der Ebenenstapel rechnet in acht Bit, und ein
            ' Ebenenstapel mit etwas Genauigkeitsverlust ist mehr wert als gar keiner. CMYK nimmt
            ' denselben vorsichtigen, profilfreien Weg wie die Vorschau: die Ebenen bleiben damit
            ' editierbar, statt dass die Datei nur als Gesamtbild aufgeht.
            If depth <> 8 AndAlso depth <> 16 Then Return Nothing
            If colorMode <> 3 AndAlso colorMode <> 1 AndAlso colorMode <> 4 Then Return Nothing
            Dim bytesPerSample = depth \ 8
            Dim grayscale = colorMode = 1
            Dim cmyk = colorMode = 4

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
                ' Die Maske zählt mit. Sie ist ein weiteres Raster im Speicher, und eine Datei, in
                ' der jede Ebene eine bildgroße Maske trägt, käme sonst auf das Doppelte des Deckels.
                If rec.HasMaskRect Then
                    totalPixels += CLng(Math.Max(0, rec.MaskRight - rec.MaskLeft)) *
                                   Math.Max(0, rec.MaskBottom - rec.MaskTop)
                End If
                If totalPixels > MaxTotalLayerPixels Then Return Nothing
                records.Add(rec)
            Next

            Dim doc As New PsdDocumentInfo With {.Width = docWidth, .Height = docHeight}
            Dim layers = doc.Layers
            ' Bricht das Lesen mittendrin ab - eine defekte Ebene oder eine Ausnahme -, sind die
            ' Ebenen davor bereits dekodiert und haengen als Bitmap in der Liste, die danach
            ' niemand mehr in die Hand bekommt. Bei vierhundert Megapixeln Deckel ist das
            ' entsprechend viel nativer Speicher bis zum Finalisierer.
            Dim completed = False
            Try
                For Each rec In records
                    Dim maskBmp As SKBitmap = Nothing
                    Dim bmp = ReadChannelData(fs, rec, metadataOnly, maskBmp, bytesPerSample, grayscale, cmyk)
                    If bmp Is Nothing AndAlso rec.HasPixels AndAlso Not metadataOnly Then Return Nothing
                    ' Gruppenmarken haben keine Bildpunkte und gehen trotzdem mit: ohne sie wüsste
                    ' der Import nicht, wo eine Gruppe anfängt und wo sie aufhört.
                    If bmp Is Nothing AndAlso rec.SectionType = 0 AndAlso Not metadataOnly Then Continue For
                    If metadataOnly AndAlso Not rec.HasPixels AndAlso rec.SectionType = 0 Then Continue For

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
                        .Pixels = bmp,
                        .TextContent = rec.TextContent,
                        .MaskPixels = maskBmp,
                        .MaskLeft = rec.MaskLeft,
                        .MaskTop = rec.MaskTop,
                        .MaskWidth = If(maskBmp IsNot Nothing, rec.MaskRight - rec.MaskLeft, 0),
                        .MaskHeight = If(maskBmp IsNot Nothing, rec.MaskBottom - rec.MaskTop, 0),
                        .MaskDefaultValue = rec.MaskDefaultValue,
                        .MaskDisabled = rec.MaskDisabled,
                        .SectionType = rec.SectionType
                    })
                Next
                completed = True
                Return doc
            Finally
                If Not completed Then DisposeLayerPixels(doc)
            End Try
        End Function

        ''' <summary>Gibt die Bildpunkte aller schon gelesenen Ebenen frei. Fuer die Abbruchwege -
        ''' wer Nothing zurueckbekommt, hat keine Handhabe mehr auf das, was bis dahin entstand.</summary>
        Private Shared Sub DisposeLayerPixels(doc As PsdDocumentInfo)
            If doc Is Nothing Then Return
            For Each layer In doc.Layers
                layer.Pixels?.Dispose()
                layer.Pixels = Nothing
                layer.MaskPixels?.Dispose()
                layer.MaskPixels = Nothing
            Next
            doc.Layers.Clear()
        End Sub

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
            Public Property TextContent As String = ""
            ''' Das Rechteck der Ebenenmaske, in Dokumentkoordinaten wie das der Ebene.
            Public Property MaskTop As Integer
            Public Property MaskLeft As Integer
            Public Property MaskBottom As Integer
            Public Property MaskRight As Integer
            Public Property MaskDefaultValue As Byte = 0
            Public Property MaskDisabled As Boolean
            Public Property HasMaskRect As Boolean = False
            Public Property SectionType As Integer = 0
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
            If maskLen < 0 OrElse fs.Position + maskLen > fs.Length Then Return Nothing
            If maskLen > 0 Then
                Dim maskEnd = fs.Position + maskLen
                ReadMaskBlock(fs, rec, maskLen)
                ' In JEDEM Fall auf das Blockende springen. Der Block ist 20 oder 36 Byte lang, und
                ' die zweite Fassung trägt hinter der Maske noch ein zweites Rechteck, das hier
                ' niemanden angeht - gelesen wird der Anfang, verlassen wird er über die Länge.
                fs.Seek(maskEnd, SeekOrigin.Begin)
            End If
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
                ElseIf bk = "TySh" AndAlso blockLen > 100 AndAlso blockLen < 8_000_000 Then
                    ' Textebene: den Block als Ganzes einlesen und den Wortlaut daraus holen.
                    Dim textBlock(CInt(blockLen) - 1) As Byte
                    If ReadExactly(fs, textBlock, textBlock.Length) Then
                        rec.TextContent = PsdTextReader.ExtractText(textBlock)
                    End If
                ElseIf bk = "lsct" AndAlso blockLen >= 4 Then
                    ' Abschnittsmarke: 1 und 2 sind die Gruppenzeile selbst, 3 ist ihr unteres Ende.
                    ' Solche Datensätze tragen keine Bildpunkte, werden aber weitergereicht - aus
                    ' ihnen baut der Import die Gruppen nach.
                    Dim sectionType = ReadU32(fs)
                    If sectionType >= 1 AndAlso sectionType <= 3 Then
                        rec.HasPixels = False
                        rec.SectionType = CInt(sectionType)
                    End If
                End If

                fs.Seek(blockStart + blockLen + (blockLen And 1L), SeekOrigin.Begin)
            End While

            fs.Seek(extraEnd, SeekOrigin.Begin)

            If rec.Right <= rec.Left OrElse rec.Bottom <= rec.Top Then rec.HasPixels = False
            Return rec
        End Function

        ''' <summary>Der Maskenteil eines Ebenendatensatzes: Rechteck, Vorgabewert außerhalb davon,
        ''' Merkmale. Der Aufrufer springt danach über die Blocklänge weiter, hier wird also nur
        ''' gelesen, was gebraucht wird.
        '''
        ''' Das Merkmal-Byte trägt in Bit 1 das Abschalten der Maske und in Bit 0 die Aussage, dass
        ''' ihr Rechteck RELATIV zur Ebene gemeint ist statt absolut im Dokument. Das kommt selten
        ''' vor, kostet aber nichts - und übergangen säße die Maske um den Ebenenversatz verschoben,
        ''' was bei einer Ebene am Bildrand sofort auffiele und bei einer bildgroßen nie.</summary>
        Private Shared Sub ReadMaskBlock(fs As FileStream, rec As LayerRecord, maskLen As Long)
            ' Rechteck, Vorgabewert und Merkmale sind zusammen achtzehn Byte. Weniger heißt: hier
            ' steht etwas anderes, als diese Fassung des Formats vorsieht.
            If maskLen < 18 Then Return
            Try
                Dim top = CInt(ReadU32Signed(fs))
                Dim left = CInt(ReadU32Signed(fs))
                Dim bottom = CInt(ReadU32Signed(fs))
                Dim right = CInt(ReadU32Signed(fs))
                Dim defaultValue = fs.ReadByte()
                Dim flags = fs.ReadByte()
                If defaultValue < 0 OrElse flags < 0 Then Return

                If (flags And 1) <> 0 Then
                    left += rec.Left : right += rec.Left
                    top += rec.Top : bottom += rec.Top
                End If

                If right <= left OrElse bottom <= top Then Return
                rec.MaskTop = top
                rec.MaskLeft = left
                rec.MaskBottom = bottom
                rec.MaskRight = right
                ' Der Wert wird UNVERAENDERT uebernommen. Das Format nennt zwar nur 0 und 255, aber
                ' es steht ein ganzes Byte dort, und ein Grauwert dazwischen ist eine Maske, die
                ' ausserhalb ihres Rechtecks halb deckt. Auf die beiden Enden gerundet aendert sich
                ' die Deckkraft der Ebene sichtbar, und zwar ueberall dort, wo die Maske nichts sagt.
                rec.MaskDefaultValue = CByte(defaultValue And &HFF)
                rec.MaskDisabled = (flags And 2) <> 0
                rec.HasMaskRect = True
            Catch
                ' Ein unlesbarer Maskenblock kostet die Maske, nicht die Ebene.
                rec.HasMaskRect = False
            End Try
        End Sub

        ' ── Kanaldaten ───────────────────────────────────────────────────────────

        ''' <summary>Liest die Kanäle einer Ebene und setzt sie zu einem Bitmap zusammen. Kanäle, die
        ''' nicht zum Bild gehören, werden mitgelesen und verworfen, sonst verrutscht der Lesezeiger
        ''' für alle folgenden Ebenen.</summary>
        ''' <param name="maskBitmap">Nimmt die Ebenenmaske auf, wenn eine dabei ist. Sie steht in
        ''' einem eigenen Kanal mit eigenem Rechteck und kann deshalb nicht aus dem Rückgabewert
        ''' kommen.</param>
        ''' <param name="grayscale">Das Dokument hat nur einen Farbkanal. Er wird auf alle drei
        ''' gelegt - Grau ist Rot gleich Grün gleich Blau, und der Ebenenstapel kennt nur Farbe.</param>
        ''' <param name="cmyk">Die vier Farbkanäle sind Cyan, Magenta, Gelb und Schwarz. Photoshop
        ''' speichert sie invertiert; die profilfreie Umrechnung entspricht dem flachen PSD-Weg.</param>
        Private Shared Function ReadChannelData(fs As FileStream, rec As LayerRecord,
                                                metadataOnly As Boolean,
                                                ByRef maskBitmap As SKBitmap,
                                                bytesPerSample As Integer,
                                                grayscale As Boolean,
                                                cmyk As Boolean) As SKBitmap
            Dim width = rec.Right - rec.Left
            Dim height = rec.Bottom - rec.Top
            Dim maskWidth = rec.MaskRight - rec.MaskLeft
            Dim maskHeight = rec.MaskBottom - rec.MaskTop
            maskBitmap = Nothing

            Dim red As Byte() = Nothing, green As Byte() = Nothing, blue As Byte() = Nothing, alpha As Byte() = Nothing
            Dim black As Byte() = Nothing

            For Each ch In rec.Channels
                Dim id = ch(0)
                Dim declaredLen = ch(1)
                Dim blockEnd = fs.Position + declaredLen

                ' Nur die Bildkanäle einer Ebene mit Fläche werden ausgewertet: RGB/Grau braucht
                ' drei beziehungsweise einen, CMYK vier; Transparenz liegt bei allen in Kanal -1.
                Dim wanted = Not metadataOnly AndAlso rec.HasPixels AndAlso
                             (id = 0 OrElse id = 1 OrElse id = 2 OrElse id = -1 OrElse (cmyk AndAlso id = 3))
                ' Die Maske liegt in Kanal -2 und hat die Maße IHRES Rechtecks, nicht die der Ebene.
                ' Kanal -3 wäre die zusammengerechnete Fassung aus Ebenen- und Vektormaske; die
                ' bleibt draußen, weil ihr Vektoranteil hier ohnehin nicht nachvollzogen wird und
                ' eine halb übernommene Maske schlechter ist als die ehrliche Ebenenmaske.
                ' Auch eine GRUPPENZEILE darf eine Maske tragen; sie gilt dann für alles, was in der
                ' Gruppe liegt. Deshalb hängt das Lesen hier nicht an den Bildpunkten.
                Dim wantMask = Not metadataOnly AndAlso (rec.HasPixels OrElse rec.SectionType <> 0) AndAlso
                               rec.HasMaskRect AndAlso id = -2 AndAlso maskWidth > 0 AndAlso maskHeight > 0
                If wanted Then
                    Dim plane = ReadPlane(fs, width, height, declaredLen, bytesPerSample)
                    If plane IsNot Nothing Then
                        Select Case id
                            Case 0 : red = plane
                            Case 1 : green = plane
                            Case 2 : blue = plane
                            Case 3 : black = plane
                            Case -1 : alpha = plane
                        End Select
                    End If
                ElseIf wantMask Then
                    ' Scheitert die Maske, bleibt die Ebene gültig - sie sieht dann aus wie ohne
                    ' Maske, und das ist der Stand von vorher, kein neuer Schaden.
                    Dim maskPlane = ReadPlane(fs, maskWidth, maskHeight, declaredLen, bytesPerSample)
                    If maskPlane IsNot Nothing Then
                        maskBitmap?.Dispose()
                        maskBitmap = BuildAlphaBitmap(maskPlane, maskWidth, maskHeight)
                    End If
                End If

                ' In jedem Fall exakt hinter den Block springen: die angegebene Länge ist die
                ' Wahrheit, nicht das, was das Entpacken verbraucht hat.
                If blockEnd > fs.Length Then Return DropMask(maskBitmap)
                fs.Seek(blockEnd, SeekOrigin.Begin)
            Next

            ' Kommt keine Ebene zustande, ist auch ihre Maske gegenstandslos - und ein Bitmap, das
            ' niemand mehr in die Hand bekommt, wäre nativer Speicher bis zum Finalisierer. Bei
            ' einer Gruppenzeile ist das anders: sie hat nie Bildpunkte, ihre Maske gilt trotzdem.
            If Not rec.HasPixels Then
                If rec.SectionType <> 0 Then Return Nothing
                Return DropMask(maskBitmap)
            End If
            ' Bei Graustufen gibt es nur den einen Kanal 0. Er wird auf alle drei gelegt, statt einen
            ' zweiten Zusammenbauweg daneben zu stellen - die Werte sind dieselben.
            If grayscale AndAlso red IsNot Nothing Then
                green = red
                blue = red
            End If
            If red Is Nothing OrElse green Is Nothing OrElse blue Is Nothing OrElse (cmyk AndAlso black Is Nothing) Then Return DropMask(maskBitmap)

            Dim info = New SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul)
            Dim bmp = New SKBitmap(info)
            Try
                Dim count = width * height
                Dim buffer(count * 4 - 1) As Byte
                For i = 0 To count - 1
                    If cmyk Then
                        ' Photoshop speichert CMYK invertiert (255 = keine Farbe). Ohne das
                        ' eingebettete Druckprofil ist dies bewusst dieselbe robuste Näherung wie
                        ' PsdPreviewService: C', M', Y' jeweils mit K' multiplizieren.
                        Dim k = CInt(black(i))
                        buffer(i * 4) = CByte(CInt(blue(i)) * k \ 255)      ' B aus Y'
                        buffer(i * 4 + 1) = CByte(CInt(green(i)) * k \ 255) ' G aus M'
                        buffer(i * 4 + 2) = CByte(CInt(red(i)) * k \ 255)   ' R aus C'
                    Else
                        buffer(i * 4) = red(i)
                        buffer(i * 4 + 1) = green(i)
                        buffer(i * 4 + 2) = blue(i)
                    End If
                    buffer(i * 4 + 3) = If(alpha IsNot Nothing, alpha(i), CByte(255))
                Next
                Runtime.InteropServices.Marshal.Copy(buffer, 0, bmp.GetPixels(), buffer.Length)
                Return bmp
            Catch
                bmp.Dispose()
                Return DropMask(maskBitmap)
            End Try
        End Function

        ''' <summary>Gibt eine schon gelesene Maske wieder frei und liefert Nothing. Für die
        ''' Abbruchwege des Kanallesers, damit dort nicht an drei Stellen dasselbe steht.</summary>
        Private Shared Function DropMask(ByRef maskBitmap As SKBitmap) As SKBitmap
            maskBitmap?.Dispose()
            maskBitmap = Nothing
            Return Nothing
        End Function

        ''' <summary>Eine Graustufenfläche als Alpha8-Bitmap. Genau die Bauform, die der eigene
        ''' Maskentyp erwartet - dort liegen die Deckungswerte im Alphakanal, nicht in Helligkeiten.</summary>
        Private Shared Function BuildAlphaBitmap(plane As Byte(), width As Integer, height As Integer) As SKBitmap
            If plane Is Nothing OrElse width < 1 OrElse height < 1 Then Return Nothing
            If plane.Length < width * height Then Return Nothing
            Dim bmp = New SKBitmap(New SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul))
            Try
                ' ZEILENWEISE über RowBytes. Skia darf die Zeilenlänge aufrunden, und ein Kopieren
                ' der Fläche am Stück verschöbe dann jede Zeile gegen die vorige.
                Dim target = bmp.GetPixels()
                Dim stride = bmp.RowBytes
                For y = 0 To height - 1
                    Runtime.InteropServices.Marshal.Copy(plane, y * width, IntPtr.Add(target, y * stride), width)
                Next
                Return bmp
            Catch
                bmp.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Ein Kanal einer Ebene: Kompressionsmarke, bei RLE die Zeilenlängen dieses einen
        ''' Kanals, danach die Zeilen. Anders als beim Gesamtbild stehen die Längen NICHT gesammelt
        ''' für alle Kanäle vorneweg.</summary>
        ''' <param name="declaredLen">Die im Verzeichnis angegebene Länge dieses Kanalblocks,
        ''' EINSCHLIESSLICH der zwei Byte für die Kompressionsmarke. Nur der ZIP-Weg braucht sie: ein
        ''' Deflate-Strom sagt selbst nicht, wo er aufhört, und weiterzulesen als der Block reicht
        ''' hieße in den nächsten Kanal hinein.</param>
        ''' <param name="bytesPerSample">1 bei acht Bit, 2 bei sechzehn. Herauskommt in beiden Fällen
        ''' ein Byte je Bildpunkt - bei sechzehn Bit das obere, wie es der flache Weg auch tut.</param>
        Private Shared Function ReadPlane(fs As FileStream, width As Integer, height As Integer,
                                          declaredLen As Integer, bytesPerSample As Integer) As Byte()
            If width < 1 OrElse height < 1 Then Return Nothing
            Dim compression = ReadU16(fs)
            Dim plane(width * height - 1) As Byte
            Dim rowBytes = width * bytesPerSample

            If compression = 0 Then
                Dim rowBuffer(rowBytes - 1) As Byte
                For row = 0 To height - 1
                    If Not ReadExactly(fs, rowBuffer, rowBytes) Then Return Nothing
                    StoreRow(plane, row, width, rowBuffer, bytesPerSample)
                Next
                Return plane
            End If

            ' 2 und 3 sind ZIP: derselbe Deflate-Strom, bei 3 zusätzlich zeilenweise als Differenz
            ' zum linken Nachbarn abgelegt. Photoshop schreibt bei acht Bit meist RLE, andere
            ' Programme und alles ab sechzehn Bit greifen zu ZIP - ohne diesen Zweig lieferte der
            ' Leser dort gar keine Ebenen, und die Datei fiel auf das flache Gesamtbild zurück.
            If compression = 2 OrElse compression = 3 Then
                Return ReadZipPlane(fs, width, height, declaredLen - 2, compression = 3, bytesPerSample)
            End If

            If compression <> 1 Then Return Nothing

            ' Die Zeilenlängen sind GEPACKTE Längen. Der Schlimmstfall von PackBits ist etwas mehr
            ' als die rohe Zeile, deshalb der großzügige Rand - alles darüber ist keine gültige Datei.
            Dim rowLengths(height - 1) As Integer
            For row = 0 To height - 1
                rowLengths(row) = ReadU16(fs)
                If rowLengths(row) < 0 OrElse rowLengths(row) > rowBytes * 2 + 64 Then Return Nothing
            Next

            Dim packed(Math.Max(1, rowBytes * 2 + 64) - 1) As Byte
            Dim unpacked(rowBytes - 1) As Byte
            For row = 0 To height - 1
                Dim len = rowLengths(row)
                If packed.Length < len Then ReDim packed(len - 1)
                If Not ReadExactly(fs, packed, len) Then Return Nothing
                If Not PsdPreviewService.UnpackBits(packed, len, unpacked, rowBytes) Then Return Nothing
                StoreRow(plane, row, width, unpacked, bytesPerSample)
            Next
            Return plane
        End Function

        ''' <summary>Legt eine entpackte Zeile in die Kanalfläche. Bei sechzehn Bit wird dabei das
        ''' obere Byte genommen: der Ebenenstapel rechnet in acht Bit, und derselbe Griff steht seit
        ''' jeher im flachen Weg.</summary>
        Private Shared Sub StoreRow(plane As Byte(), row As Integer, width As Integer,
                                    rowBuffer As Byte(), bytesPerSample As Integer)
            Dim target = row * width
            If bytesPerSample = 1 Then
                Array.Copy(rowBuffer, 0, plane, target, width)
                Return
            End If
            For x = 0 To width - 1
                plane(target + x) = rowBuffer(x * 2)
            Next
        End Sub

        ''' <summary>Ein ZIP-komprimierter Kanal. <paramref name="payloadLen"/> ist die Länge des
        ''' Stroms ohne die Kompressionsmarke, <paramref name="predicted"/> unterscheidet die Marken
        ''' 2 und 3.
        '''
        ''' Bei der VORHERSAGE (Marke 3) steht in jedem Byte nicht der Wert, sondern die Differenz
        ''' zum linken Nachbarn; die Zeile beginnt jeweils neu. Die Rückrechnung läuft deshalb
        ''' ZEILENWEISE und nicht über die Fläche - sonst schleppte der erste Punkt einer Zeile den
        ''' letzten der vorigen mit sich, und das Bild zöge sich schräg auseinander.
        '''
        ''' Gerechnet wird über Integer und mit einer Maske zurück auf ein Byte. VB prüft
        ''' Ganzzahlüberläufe von sich aus, und eine Summe über 255 wäre sonst kein Umlauf, sondern
        ''' eine Ausnahme mitten im Entpacken.
        '''
        ''' Bei SECHZEHN Bit läuft die Vorhersage über ganze Werte und nicht über Bytes: die
        ''' Differenz steht dort als Wort, oberes Byte zuerst. Byteweise gerechnet käme aus jedem
        ''' Übertrag ein sichtbarer Fehler, der sich über die ganze Zeile fortpflanzt.</summary>
        Private Shared Function ReadZipPlane(fs As FileStream, width As Integer, height As Integer,
                                             payloadLen As Integer, predicted As Boolean,
                                             bytesPerSample As Integer) As Byte()
            If payloadLen <= 0 Then Return Nothing
            If fs.Position + payloadLen > fs.Length Then Return Nothing

            Dim packed(payloadLen - 1) As Byte
            If Not ReadExactly(fs, packed, payloadLen) Then Return Nothing

            Dim rowBytes = width * bytesPerSample
            Dim raw(rowBytes * height - 1) As Byte
            If Not Inflate(packed, raw) Then Return Nothing

            If predicted Then
                For row = 0 To height - 1
                    PsdPreviewService.UndoPrediction(raw, row * rowBytes, width, bytesPerSample)
                Next
            End If

            If bytesPerSample = 1 Then Return raw

            Dim plane(width * height - 1) As Byte
            For row = 0 To height - 1
                For x = 0 To width - 1
                    plane(row * width + x) = raw(row * rowBytes + x * 2)
                Next
            Next
            Return plane
        End Function

        ''' <summary>Entpackt <paramref name="packed"/> nach <paramref name="target"/> und meldet, ob
        ''' die Zielfläche VOLLSTAENDIG gefüllt wurde. Ein halb entpackter Kanal ist kein brauchbares
        ''' Ergebnis: die untere Hälfte der Ebene bliebe schwarz, und das fiele erst im fertigen Bild
        ''' auf.
        '''
        ''' Erwartet wird ein zlib-Strom, so schreibt Photoshop ihn. Fehlen die zwei Kopfbytes -
        ''' andere Erzeuger legen gelegentlich den nackten Deflate-Strom ab -, wird der zweite Weg
        ''' versucht, statt die Ebene aufzugeben.</summary>
        Private Shared Function Inflate(packed As Byte(), target As Byte()) As Boolean
            Dim withHeader = packed IsNot Nothing AndAlso packed.Length >= 2 AndAlso
                             PsdPreviewService.LooksLikeZlib(packed(0), packed(1))
            If withHeader AndAlso InflateWith(packed, target, True) Then Return True
            Return InflateWith(packed, target, False)
        End Function

        Private Shared Function InflateWith(packed As Byte(), target As Byte(), zlibHeader As Boolean) As Boolean
            Try
                Using source = New MemoryStream(packed, False)
                    Using raw As Stream = If(zlibHeader,
                                             CType(New ZLibStream(source, CompressionMode.Decompress), Stream),
                                             CType(New DeflateStream(source, CompressionMode.Decompress), Stream))
                        Dim total = 0
                        While total < target.Length
                            Dim n = raw.Read(target, total, target.Length - total)
                            If n <= 0 Then Exit While
                            total += n
                        End While
                        Return total = target.Length
                    End Using
                End Using
            Catch
                Return False
            End Try
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
