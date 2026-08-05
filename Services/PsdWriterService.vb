Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Schreibt Photoshop-Dateien (.psd) MIT Ebenen. Das Gegenstueck zu PsdPreviewService, der nur
    ''' liest, und die Bruecke aus dem eigenen Projektformat hinaus: was in einer .fpx als Ebenenstapel
    ''' liegt, laesst sich damit an Photoshop, Affinity oder GIMP weiterreichen.
    '''
    ''' Aufbau der Datei, in dieser Reihenfolge:
    '''   Kopf (8BPS, Version 1, Kanaele, Hoehe, Breite, 8 Bit, Farbmodus RGB)
    '''   Farbmodus-Daten   - leer, RGB braucht keine Tabelle
    '''   Bildressourcen    - leer
    '''   Ebenen-Sektion    - Ebenenverzeichnis und danach die Kanaldaten je Ebene
    '''   Bilddaten         - das fertige Gesamtbild, planar je Kanal
    '''
    ''' Das Gesamtbild am Ende ist NICHT verzichtbar. Photoshop nennt es "Maximale Kompatibilitaet",
    ''' und praktisch jedes fremde Programm zeigt genau dieses Bild an, statt die Ebenen selbst
    ''' zusammenzurechnen - der eigene Leser eingeschlossen. Ohne den Block bliebe die Datei fuer die
    ''' meisten Betrachter leer.
    '''
    ''' Bewusst nicht abgedeckt: Korrekturebenen, Textebenen und Effekte als solche. Photoshop legt
    ''' die je Art in einem eigenen, kaum dokumentierten Datensatz ab. Sie kommen deshalb als
    ''' Bildpunkte heraus - in der .fpx bleiben sie weiterhin veraenderbar, im PSD sind sie fest.
    ''' </summary>
    Public NotInheritable Class PsdWriterService

        Private Sub New()
        End Sub

        ''' Photoshop laesst je Seite 30000 Bildpunkte zu; darueber verlangt es das PSB-Format.
        Private Const MaxSide As Integer = 30_000

        ''' <summary>Eine Ebene, wie sie in die Datei soll. Die Bildpunkte liegen als eigenes Bitmap
        ''' vor, <see cref="Left"/>/<see cref="Top"/> sagen, wo es im Dokument sitzt.</summary>
        Public Class PsdLayerInput
            Public Property Name As String = ""
            Public Property Pixels As SKBitmap
            Public Property Left As Integer
            Public Property Top As Integer
            Public Property OpacityPercent As Single = 100
            ''' Mischmethode in der Schreibweise von FerrumPix ("Normal", "Multiply" …).
            Public Property BlendMode As String = "Normal"
            Public Property ClipToLayerBelow As Boolean = False
            Public Property IsVisible As Boolean = True
        End Class

        ''' <summary>Uebersetzt die Mischmethoden von FerrumPix in die Vierzeichenschluessel von
        ''' Photoshop. Was hier fehlt, wird als "norm" geschrieben - die Bildpunkte der Ebene stimmen
        ''' dann trotzdem, nur das Zusammenrechnen weicht ab. Deshalb traegt das Gesamtbild am Ende
        ''' der Datei immer das Ergebnis, das FerrumPix selbst gerechnet hat.</summary>
        Private Shared ReadOnly BlendKeys As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"Normal", "norm"}, {"Multiply", "mul "}, {"Screen", "scrn"}, {"Overlay", "over"},
            {"Darken", "dark"}, {"Lighten", "lite"}, {"ColorDodge", "div "}, {"ColorBurn", "idiv"},
            {"HardLight", "hLit"}, {"SoftLight", "sLit"}, {"Difference", "diff"}, {"Exclusion", "smud"},
            {"Hue", "hue "}, {"Saturation", "sat "}, {"Color", "colr"}, {"Luminosity", "lum "},
            {"LinearDodge", "lddg"}, {"LinearBurn", "lbrn"}, {"VividLight", "vLit"}, {"LinearLight", "lLit"},
            {"PinLight", "pLit"}, {"HardMix", "hMix"}, {"Add", "lddg"}, {"Plus", "lddg"}
        }

        ''' <summary>Schreibt Gesamtbild und Ebenen als .psd. Liefert False, wenn die Masse ausserhalb
        ''' dessen liegen, was das Format zulaesst, oder das Schreiben fehlschlaegt.
        ''' <paramref name="layers"/> darf leer sein - dann entsteht eine Datei mit einer einzigen
        ''' Hintergrundebene aus dem Gesamtbild.</summary>
        Public Shared Function Save(filePath As String, composite As SKBitmap,
                                    layers As IList(Of PsdLayerInput)) As Boolean
            If String.IsNullOrWhiteSpace(filePath) OrElse composite Is Nothing Then Return False
            If composite.Width < 1 OrElse composite.Height < 1 Then Return False
            If composite.Width > MaxSide OrElse composite.Height > MaxSide Then Return False

            Dim usable As New List(Of PsdLayerInput)()
            If layers IsNot Nothing Then
                For Each layer In layers
                    If layer Is Nothing OrElse layer.Pixels Is Nothing Then Continue For
                    If layer.Pixels.Width < 1 OrElse layer.Pixels.Height < 1 Then Continue For
                    usable.Add(layer)
                Next
            End If

            ' Erst vollstaendig in den Arbeitsspeicher, dann in einem Zug auf die Platte: die Laengen
            ' der Ebenen-Sektion stehen VOR ihrem Inhalt und sind erst bekannt, wenn er gebaut ist.
            Dim tempPath = filePath & ".tmp"
            Try
                Using fs = File.Create(tempPath)
                    WriteHeader(fs, composite.Width, composite.Height)
                    WriteU32(fs, 0)  ' Farbmodus-Daten: keine
                    WriteU32(fs, 0)  ' Bildressourcen: keine
                    WriteLayerSection(fs, usable)
                    WriteMergedImage(fs, composite)
                End Using
                File.Move(tempPath, filePath, overwrite:=True)
                Return True
            Catch
                ' Kein einzeiliges Try mit einzeiligem If davor: der Doppelpunkt zieht das Catch in
                ' den If-Rumpf, und der Compiler sieht ein Catch ohne Try.
                Try
                    If File.Exists(tempPath) Then File.Delete(tempPath)
                Catch
                End Try
                Return False
            End Try
        End Function

        ' ── Kopf ─────────────────────────────────────────────────────────────────

        Private Shared Sub WriteHeader(fs As Stream, width As Integer, height As Integer)
            fs.Write(Encoding.ASCII.GetBytes("8BPS"), 0, 4)
            WriteU16(fs, 1)          ' Version 1 = PSD
            For i = 1 To 6           ' sechs Nullbytes, vom Format so verlangt
                fs.WriteByte(0)
            Next
            WriteU16(fs, 4)          ' Kanaele: R, G, B und Transparenz
            WriteU32(fs, height)
            WriteU32(fs, width)
            WriteU16(fs, 8)          ' 8 Bit je Kanal
            WriteU16(fs, 3)          ' Farbmodus RGB
        End Sub

        ' ── Ebenen-Sektion ───────────────────────────────────────────────────────

        ''' <summary>Baut das Ebenenverzeichnis samt Kanaldaten und schreibt es mit den beiden
        ''' vorangestellten Laengen. Ohne Ebenen bleibt die Sektion leer - eine Laenge 0 ist gueltig
        ''' und heisst "nur ein Gesamtbild".</summary>
        Private Shared Sub WriteLayerSection(fs As Stream, layers As IList(Of PsdLayerInput))
            If layers.Count = 0 Then
                WriteU32(fs, 0)
                Return
            End If

            ' Die Kanaldaten zuerst packen: ihre Laengen gehoeren in das Verzeichnis, das VOR ihnen
            ' steht. Je Ebene vier Bloecke in der Reihenfolge Transparenz, Rot, Gruen, Blau.
            Dim packed As New List(Of Byte()())()
            For Each layer In layers
                packed.Add(PackLayerChannels(layer.Pixels))
            Next

            Using info As New MemoryStream()
                WriteU16(info, layers.Count)

                For i = 0 To layers.Count - 1
                    Dim layer = layers(i)
                    Dim bmp = layer.Pixels
                    Dim top = layer.Top
                    Dim left = layer.Left
                    WriteU32(info, top)
                    WriteU32(info, left)
                    WriteU32(info, top + bmp.Height)
                    WriteU32(info, left + bmp.Width)

                    WriteU16(info, 4)
                    ' Kanalkennungen: -1 ist die Transparenz, danach Rot, Gruen, Blau. Zu jeder Laenge
                    ' zaehlen die zwei Bytes der Kompressionsmarke, die im Datenblock selbst stehen.
                    Dim ids = New Integer() {-1, 0, 1, 2}
                    For c = 0 To 3
                        WriteU16(info, ids(c))
                        WriteU32(info, packed(i)(c).Length)
                    Next

                    info.Write(Encoding.ASCII.GetBytes("8BIM"), 0, 4)
                    info.Write(Encoding.ASCII.GetBytes(ResolveBlendKey(layer.BlendMode)), 0, 4)

                    Dim opacity = CInt(Math.Round(Math.Max(0.0F, Math.Min(100.0F, layer.OpacityPercent)) * 255.0F / 100.0F))
                    info.WriteByte(CByte(Math.Max(0, Math.Min(255, opacity))))
                    info.WriteByte(If(layer.ClipToLayerBelow, CByte(1), CByte(0)))
                    ' Kennzeichen: Bit 1 bedeutet AUSGEBLENDET, nicht sichtbar. Andersherum gelesen
                    ' waere jede sichtbare Ebene in Photoshop unsichtbar.
                    info.WriteByte(If(layer.IsVisible, CByte(0), CByte(2)))
                    info.WriteByte(0)  ' Fuellbyte

                    ' Zusatzdaten: Maske, Mischbereiche, Name. Die Laenge davor.
                    Using extra As New MemoryStream()
                        WriteU32(extra, 0)   ' keine Ebenenmaske
                        WriteU32(extra, 0)   ' keine Mischbereiche
                        WritePascalName(extra, layer.Name)
                        WriteUnicodeName(extra, layer.Name)
                        WriteU32(info, CInt(extra.Length))
                        extra.Position = 0
                        extra.CopyTo(info)
                    End Using
                Next

                For i = 0 To layers.Count - 1
                    For c = 0 To 3
                        info.Write(packed(i)(c), 0, packed(i)(c).Length)
                    Next
                Next

                ' Das Verzeichnis wird auf eine gerade Laenge aufgefuellt; die Sektion darum enthaelt
                ' zusaetzlich die vier Bytes der globalen Maskenlaenge.
                Dim infoLen = CInt(info.Length)
                Dim padded = infoLen + (infoLen And 1)
                WriteU32(fs, padded + 4 + 4)
                WriteU32(fs, padded)
                info.Position = 0
                info.CopyTo(fs)
                If padded <> infoLen Then fs.WriteByte(0)
                WriteU32(fs, 0)  ' globale Ebenenmaske: keine
            End Using
        End Sub

        ''' <summary>Packt die vier Kanaele einer Ebene, jeder mit seiner eigenen Kompressionsmarke
        ''' vorneweg. Reihenfolge wie im Verzeichnis: Transparenz, Rot, Gruen, Blau.</summary>
        Private Shared Function PackLayerChannels(bmp As SKBitmap) As Byte()()
            Dim width = bmp.Width
            Dim height = bmp.Height
            Dim planes = ExtractPlanes(bmp)
            Dim result As Byte()() = New Byte(3)() {}
            ' ExtractPlanes liefert R, G, B, A - hier wird auf A, R, G, B umsortiert.
            Dim order = New Integer() {3, 0, 1, 2}
            For c = 0 To 3
                result(c) = PackPlaneRle(planes(order(c)), width, height)
            Next
            Return result
        End Function

        ' ── Gesamtbild ───────────────────────────────────────────────────────────

        ''' <summary>Schreibt das fertige Bild planar je Kanal. Anders als bei den Ebenen stehen hier
        ''' die Zeilenlaengen ALLER Kanaele gesammelt vor den Daten - genau so, wie der eigene Leser
        ''' sie erwartet.</summary>
        Private Shared Sub WriteMergedImage(fs As Stream, composite As SKBitmap)
            Dim width = composite.Width
            Dim height = composite.Height
            Dim planes = ExtractPlanes(composite)

            WriteU16(fs, 1)  ' Kompression: RLE/PackBits

            Dim rows As Byte()()() = New Byte(3)()() {}
            For c = 0 To 3
                rows(c) = PackPlaneRows(planes(c), width, height)
            Next

            For c = 0 To 3
                For row = 0 To height - 1
                    WriteU16(fs, rows(c)(row).Length)
                Next
            Next
            For c = 0 To 3
                For row = 0 To height - 1
                    fs.Write(rows(c)(row), 0, rows(c)(row).Length)
                Next
            Next
        End Sub

        ' ── Bildpunkte und Packen ────────────────────────────────────────────────

        ''' <summary>Zerlegt ein Bitmap in vier Kanal-Ebenen (R, G, B, A), je ein Byte pro Bildpunkt.
        ''' Vormultipliziertes Alpha wird dabei zurueckgerechnet: Photoshop erwartet geradliniges
        ''' Alpha, sonst saeumen halbdurchsichtige Kanten dunkel.</summary>
        Private Shared Function ExtractPlanes(bmp As SKBitmap) As Byte()()
            Dim width = bmp.Width
            Dim height = bmp.Height
            Dim count = width * height
            Dim r(count - 1) As Byte
            Dim g(count - 1) As Byte
            Dim b(count - 1) As Byte
            Dim a(count - 1) As Byte

            ' Ueber eine einheitliche Zielform gehen, statt auf das Eingangsformat zu vertrauen:
            ' die Ebenen kommen aus verschiedenen Ecken der Pipeline, mal Rgba, mal Bgra.
            Dim info = New SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul)
            Dim buffer(count * 4 - 1) As Byte
            Dim handle = Runtime.InteropServices.GCHandle.Alloc(buffer, Runtime.InteropServices.GCHandleType.Pinned)
            Try
                Dim converted = False
                Using pixmap = bmp.PeekPixels()
                    If pixmap IsNot Nothing Then
                        converted = pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), width * 4)
                    End If
                End Using
                If Not converted Then
                    ' Schlaegt die Wandlung fehl, bleibt ein durchsichtiges Ergebnis - besser als
                    ' zufaellige Bytes aus einem ungefuellten Puffer.
                    Array.Clear(buffer, 0, buffer.Length)
                End If
            Finally
                handle.Free()
            End Try

            For i = 0 To count - 1
                r(i) = buffer(i * 4)
                g(i) = buffer(i * 4 + 1)
                b(i) = buffer(i * 4 + 2)
                a(i) = buffer(i * 4 + 3)
            Next

            Return New Byte()() {r, g, b, a}
        End Function

        ''' <summary>Packt eine Kanal-Ebene zeilenweise und stellt die Kompressionsmarke voran.</summary>
        Private Shared Function PackPlaneRle(plane As Byte(), width As Integer, height As Integer) As Byte()
            Dim rows = PackPlaneRows(plane, width, height)
            Dim total = 2 + height * 2
            For row = 0 To height - 1
                total += rows(row).Length
            Next

            Dim result(total - 1) As Byte
            Dim pos = 0
            result(pos) = 0 : result(pos + 1) = 1 : pos += 2   ' Kompression 1 = RLE
            For row = 0 To height - 1
                Dim len = rows(row).Length
                result(pos) = CByte((len >> 8) And &HFF)
                result(pos + 1) = CByte(len And &HFF)
                pos += 2
            Next
            For row = 0 To height - 1
                Array.Copy(rows(row), 0, result, pos, rows(row).Length)
                pos += rows(row).Length
            Next
            Return result
        End Function

        Private Shared Function PackPlaneRows(plane As Byte(), width As Integer, height As Integer) As Byte()()
            Dim rows(height - 1)() As Byte
            For row = 0 To height - 1
                rows(row) = PackBits(plane, row * width, width)
            Next
            Return rows
        End Function

        ''' <summary>PackBits, das Gegenstueck zu UnpackBits im Leser: gleiche Bytes werden zu einem
        ''' Wiederholungslauf zusammengezogen, alles andere woertlich uebernommen. Ein Lauf fasst
        ''' hoechstens 128 Bytes, ein woertlicher Block ebenso.</summary>
        Private Shared Function PackBits(source As Byte(), offset As Integer, length As Integer) As Byte()
            ' Schlimmstfall: je 127 woertliche Bytes ein Markierungsbyte, plus Sicherheitsrand.
            Dim out(length + length \ 127 + 2) As Byte
            Dim outPos = 0
            Dim i = 0

            While i < length
                ' Wie viele gleiche Bytes folgen?
                Dim runLen = 1
                While i + runLen < length AndAlso runLen < 128 AndAlso
                      source(offset + i + runLen) = source(offset + i)
                    runLen += 1
                End While

                If runLen >= 2 Then
                    ' Wiederholung: Laengenbyte als negative Zahl, danach der Wert einmal.
                    ' Kein CSByte - das wirft ab 128 (VB prueft die Wandlung), deshalb von Hand.
                    Dim marker = 257 - runLen
                    out(outPos) = CByte(marker And &HFF)
                    out(outPos + 1) = source(offset + i)
                    outPos += 2
                    i += runLen
                Else
                    ' Woertlich, bis wieder zwei gleiche Bytes hintereinander kommen.
                    Dim start = i
                    Dim literal = 0
                    While i < length AndAlso literal < 128
                        Dim sameNext = i + 1 < length AndAlso source(offset + i + 1) = source(offset + i)
                        Dim sameAfter = i + 2 < length AndAlso source(offset + i + 2) = source(offset + i)
                        If sameNext AndAlso sameAfter Then Exit While
                        i += 1
                        literal += 1
                    End While
                    out(outPos) = CByte(literal - 1)
                    outPos += 1
                    Array.Copy(source, offset + start, out, outPos, literal)
                    outPos += literal
                End If
            End While

            Dim result(outPos - 1) As Byte
            Array.Copy(out, 0, result, 0, outPos)
            Return result
        End Function

        ' ── Namen ────────────────────────────────────────────────────────────────

        ''' <summary>Der alte Name: ein Laengenbyte und der Text, das ganze Feld auf ein Vielfaches
        ''' von vier aufgefuellt. Photoshop liest ihn nur, wenn kein Unicode-Name daneben steht,
        ''' aeltere Programme dagegen ausschliesslich ihn.</summary>
        Private Shared Sub WritePascalName(ms As Stream, name As String)
            Dim text = If(name, "")
            Dim bytes = Encoding.GetEncoding(28591).GetBytes(text)
            If bytes.Length > 255 Then ReDim Preserve bytes(254)
            ms.WriteByte(CByte(bytes.Length))
            ms.Write(bytes, 0, bytes.Length)
            Dim written = 1 + bytes.Length
            While written Mod 4 <> 0
                ms.WriteByte(0)
                written += 1
            End While
        End Sub

        ''' <summary>Der Name noch einmal als Unicode, im Zusatzblock "luni". Ohne ihn verlieren
        ''' Umlaute und alles jenseits von Latin-1 ihre Zeichen.</summary>
        Private Shared Sub WriteUnicodeName(ms As Stream, name As String)
            Dim text = If(name, "")
            ms.Write(Encoding.ASCII.GetBytes("8BIM"), 0, 4)
            ms.Write(Encoding.ASCII.GetBytes("luni"), 0, 4)

            ' Laenge in Zeichen, danach der Text als UTF-16 Big-Endian mit abschliessender Null.
            Dim payloadLen = 4 + (text.Length + 1) * 2
            Dim padded = payloadLen + (payloadLen And 1)
            WriteU32(ms, padded)
            WriteU32(ms, text.Length)
            For Each ch In text
                Dim code = AscW(ch)
                ms.WriteByte(CByte((code >> 8) And &HFF))
                ms.WriteByte(CByte(code And &HFF))
            Next
            ms.WriteByte(0)
            ms.WriteByte(0)
            If padded <> payloadLen Then ms.WriteByte(0)
        End Sub

        Private Shared Function ResolveBlendKey(blendMode As String) As String
            Dim key As String = Nothing
            If Not String.IsNullOrWhiteSpace(blendMode) AndAlso BlendKeys.TryGetValue(blendMode.Trim(), key) Then
                Return key
            End If
            Return "norm"
        End Function

        ' ── Schreib-Helfer ───────────────────────────────────────────────────────

        ' Alle Zahlen im Format liegen Big-Endian. Vor dem Schieben weiten, sonst bliebe der Ausdruck
        ' in VB ein Byte und lieferte still 0 (dieselbe Falle wie im Leser).
        Private Shared Sub WriteU16(fs As Stream, value As Integer)
            fs.WriteByte(CByte((value >> 8) And &HFF))
            fs.WriteByte(CByte(value And &HFF))
        End Sub

        Private Shared Sub WriteU32(fs As Stream, value As Long)
            fs.WriteByte(CByte((value >> 24) And &HFF))
            fs.WriteByte(CByte((value >> 16) And &HFF))
            fs.WriteByte(CByte((value >> 8) And &HFF))
            fs.WriteByte(CByte(value And &HFF))
        End Sub

    End Class

End Namespace
