Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks

Namespace Services

    ' Lichterrekonstruktion: geklemmte Kamerakanaele aus den unbeschaedigten wieder aufbauen.
    '
    ' Ein ausgefressener Bildpunkt klemmt fast nie in allen drei Kanaelen zugleich. Die Kanaele, die
    ' noch unter der Klemmgrenze liegen, tragen das wahre Verhaeltnis - in der Nachbarschaft, wo
    ' nichts klemmt, laesst es sich ablesen und auf den geklemmten Punkt uebertragen. Das Verfahren
    ' folgt RAWmakase (develop/quality/highlights.rs, MIT): erst Kanalverhaeltnisse aus
    ' ungeklemmten Nachbarn im 7x7-Fenster; findet sich dort keiner, eine grobe Karte (hoechstens 512
    ' Punkte Kante) mit Farben aus GLATTEN, ungeklemmten Punkten derselben zusammenhaengenden hellen
    ' Flaeche; ohne jeden Farbbeleg neutral. Mit EINEM geklemmten Kanal ist sein Wert eine Untergrenze
    ' (das Licht war mindestens so hell), mit zweien bleibt die Schaetzung, wie sie ist - die Referenz
    ' rendert einen Sonnenkern mit Rot und Blau geklemmt gelbweiss, nicht magenta.
    '
    ' ES MUSS IM KAMERARAUM LAUFEN, vor der Farbmatrix: danach ist nicht mehr zu erkennen, welcher
    ' Sensorkanal geklemmt hat, die Matrix hat sie gemischt. Darum entwickelt LibRaw im Kameraraum,
    ' und die Matrix (rgb_cam oder die eigene) kommt erst nach dieser Stufe (DecodeCore). Die
    ' Klemmgrenze je Kanal folgt aus dcraws Normierung: mit Entklemmen teilt dcraw die Multiplikatoren
    ' durch ihr MAXIMUM, ein gesaettigter Sensel landet also bei 65535 * mul[c] / max(mul).
    '
    ' AN ORT UND STELLE: geschrieben werden nur Punkte, deren hellster Kanal an der Grenze liegt,
    ' gelesen als Nachbarn nur Punkte, deren Kanaele alle darunter liegen - die beiden Mengen sind
    ' getrennt, ein geschriebener Punkt wird nie als Nachbar gelesen.
    Partial Public NotInheritable Class RawDecodeService

        ''' <summary>Lichter rekonstruieren, AB WERK AUS seit dem 2026-10-27, mit dem zweiten Verfahren
        ''' (<see cref="HighlightReconstructionMethod"/>), wenn eingeschaltet.
        '''
        ''' WARUM AUS: in farbigem Buehnenlicht macht sie den Uebergang vom gesaettigten Licht in die
        ''' ausgefressene Flaeche hart. An einer R6 im roten Licht (WID_4447) hielt sie Gruen am Rand
        ''' der Flaeche bei 13 bis 17, wo das Referenzprogramm schon 126 bis 161 zeigt, und sprang dann
        ''' auf 150; ohne sie lag der Verlauf naeher an der Referenz (83 bis 149). Die aufgebauten Werte
        ''' finden bei uns keinen Platz: eine Blende Kopfraum im 16-Bit-Speicher, danach ein festes
        ''' Knie. Wieder einschalten, wenn die RAW-Stufe ueber Weiss in Gleitkomma rechnet
        ''' (OFFENE_PUNKTE.md).
        '''
        ''' Gemessen punktweise in der ausgefressenen
        ''' Flaeche der 29 Bilder mit ausgefressenen Lichtern, bei Belichtung -1 und Lichter -100, wo
        ''' sichtbar wird, was ueber Weiss liegt: Farbabstand zur Referenz 10,36 ohne, 9,82 mit dem
        ''' ersten, 9,67 mit dem zweiten Verfahren; im Nullbild 3,58 auf 3,51. Die Flaechenmasse vom
        ''' 2026-10-22 (reines Weiss, L-Abfall) bewegten sich dagegen kaum: die Luecke dort liegt in
        ''' der Tonabbildung ueber Weiss. Die Messung legt den Schalter per Reflexion um
        ''' (Reglereichung, REGLER_LICHTER_REKONSTRUKTION).</summary>
        Friend Shared HighlightReconstruction As Boolean = False

        ''' <summary>Messschalter: mit <see cref="HighlightReconstruction"/> an zwar im Kameraraum
        ''' entwickeln, aber nichts rekonstruieren. Damit laesst sich pruefen, dass der Weg ueber den
        ''' Kameraraum allein dasselbe Bild gibt wie LibRaws eigene Umrechnung.</summary>
        Friend Shared HighlightReconstructionRebuild As Boolean = True

        ''' <summary>Wie viel Platz die Rekonstruktion ueber der Klemmgrenze bekommt (Faktor; 2 ist
        ''' eine Blende). Siehe DecodeCore, „Platz ueber der Grenze“.</summary>
        Friend Shared ReconstructionHeadroom As Double = 2.0

        ''' <summary>Multipliziert alle 16-Bit-Werte mit <paramref name="factor"/>.</summary>
        Private Shared Sub ScaleSamples(data As IntPtr, width As Integer, height As Integer, factor As Single)
            Dim rowValues = width * 3
            Dim rowBytes = width * 6
            Parallel.For(0, height,
                Sub(y As Integer)
                    Dim row(rowValues - 1) As Short
                    Dim rowPtr = data + y * rowBytes
                    Marshal.Copy(rowPtr, row, 0, rowValues)
                    For i = 0 To rowValues - 1
                        row(i) = ToUInt16Bits((CInt(row(i)) And &HFFFF) * factor)
                    Next
                    Marshal.Copy(row, 0, rowPtr, rowValues)
                End Sub)
        End Sub

        ''' Anteil der Klemmgrenze, ab dem ein Kanal als geklemmt gilt (Demosaic und Rauschminderung
        ''' verwischen die Grenze; RAWmakase nimmt denselben Wert).
        Private Const ClipFraction As Single = 0.97F
        ''' Ab diesem Anteil zaehlt ein Punkt als hell fuer die Farbkarte.
        Private Const BrightFraction As Single = 0.75F
        Private Const NeighborRadius As Integer = 3
        Private Const ReconstructBandRows As Integer = 64
        Private Const ColorMapMaxEdge As Integer = 512

        ''' <summary>Baut geklemmte Kanaele im Kameraraum wieder auf. <paramref name="clipLevels"/> ist
        ''' die Klemmgrenze je Kanal in 16-Bit-Werten. Liefert die Zahl geaenderter Bildpunkte.</summary>
        Friend Shared Function ReconstructClippedHighlights(data As IntPtr, width As Integer, height As Integer,
                                                            clipLevels As Double()) As Integer
            If data = IntPtr.Zero OrElse width <= 0 OrElse height <= 0 Then Return 0
            If clipLevels Is Nothing OrElse clipLevels.Length < 3 Then Return 0
            Dim invClip(2) As Single
            For c = 0 To 2
                If Not Double.IsFinite(clipLevels(c)) OrElse clipLevels(c) <= 1.0 Then Return 0
                invClip(c) = CSng(1.0 / clipLevels(c))
            Next

            Dim rowValues = width * 3
            Dim rowBytes = width * 6
            Dim bandCount = (height + ReconstructBandRows - 1) \ ReconstructBandRows
            Dim changed = 0
            ' Die Farbkarte entsteht nur, wenn ein Punkt sie braucht (die meisten Bilder nie), und
            ' dann genau einmal fuer alle Streifen.
            Dim colorMap As Lazy(Of RecoveryColorMap) =
                New Lazy(Of RecoveryColorMap)(Function() RecoveryColorMap.Build(data, width, height, invClip),
                                              LazyThreadSafetyMode.ExecutionAndPublication)

            Parallel.For(0, bandCount,
                Sub(band As Integer)
                    Dim y0 = band * ReconstructBandRows
                    Dim y1 = Math.Min(height, y0 + ReconstructBandRows) - 1
                    ' Der Streifen samt Rand von NeighborRadius Zeilen, damit das Fenster nicht
                    ' am Streifenende abreisst.
                    Dim top = Math.Max(0, y0 - NeighborRadius)
                    Dim bottom = Math.Min(height - 1, y1 + NeighborRadius)
                    Dim rows = bottom - top + 1
                    Dim buffer(rows * rowValues - 1) As Short
                    For y = top To bottom
                        Marshal.Copy(data + y * rowBytes, buffer, (y - top) * rowValues, rowValues)
                    Next

                    Dim local = 0
                    For y = y0 To y1
                        Dim rowChanged = False
                        Dim by = y - top
                        For x = 0 To width - 1
                            Dim i = by * rowValues + x * 3
                            Dim r = CInt(buffer(i)) And &HFFFF
                            Dim g = CInt(buffer(i + 1)) And &HFFFF
                            Dim b = CInt(buffer(i + 2)) And &HFFFF
                            Dim sr = r * invClip(0), sg = g * invClip(1), sb = b * invClip(2)
                            If Math.Max(sr, Math.Max(sg, sb)) < ClipFraction Then Continue For

                            Dim result = ReconstructPixel(buffer, rowValues, rows, width, x, by, r, g, b,
                                                          sr, sg, sb, invClip, colorMap, y)
                            buffer(i) = ToUInt16Bits(result.R)
                            buffer(i + 1) = ToUInt16Bits(result.G)
                            buffer(i + 2) = ToUInt16Bits(result.B)
                            rowChanged = True
                            local += 1
                        Next
                        If rowChanged Then
                            Marshal.Copy(buffer, by * rowValues, data + y * rowBytes, rowValues)
                        End If
                    Next
                    If local > 0 Then Interlocked.Add(changed, local)
                End Sub)
            Return changed
        End Function

        Private Shared Function ReconstructPixel(buffer As Short(), rowValues As Integer, rows As Integer, width As Integer,
                                                 x As Integer, by As Integer,
                                                 r As Integer, g As Integer, b As Integer,
                                                 sr As Single, sg As Single, sb As Single,
                                                 invClip As Single(), colorMap As Lazy(Of RecoveryColorMap),
                                                 imageY As Integer) As (R As Single, G As Single, B As Single)
            Dim p = New Single() {r, g, b}
            Dim sensor = New Single() {sr, sg, sb}
            Dim valid(2) As Boolean
            Dim validCount = 0
            For c = 0 To 2
                valid(c) = sensor(c) < ClipFraction
                If valid(c) Then validCount += 1
            Next

            Dim estimate(2) As Single
            Dim weightSum = 0.0F
            If validCount > 0 Then
                Dim target = 0.0F
                For c = 0 To 2
                    If valid(c) Then target += p(c)
                Next
                For dy = -NeighborRadius To NeighborRadius
                    Dim yy = Math.Max(0, Math.Min(rows - 1, by + dy))
                    For dx = -NeighborRadius To NeighborRadius
                        Dim xx = Math.Max(0, Math.Min(width - 1, x + dx))
                        Dim j = yy * rowValues + xx * 3
                        Dim q0 = CSng(CInt(buffer(j)) And &HFFFF)
                        Dim q1 = CSng(CInt(buffer(j + 1)) And &HFFFF)
                        Dim q2 = CSng(CInt(buffer(j + 2)) And &HFFFF)
                        If q0 * invClip(0) >= ClipFraction OrElse q1 * invClip(1) >= ClipFraction OrElse
                           q2 * invClip(2) >= ClipFraction Then Continue For
                        Dim denom = If(valid(0), q0, 0.0F) + If(valid(1), q1, 0.0F) + If(valid(2), q2, 0.0F)
                        If denom < 1.0F Then Continue For
                        Dim gain = Math.Max(0.25F, Math.Min(4.0F, target / denom))
                        Dim weight = 1.0F / (1.0F + dx * dx + dy * dy)
                        weightSum += weight
                        estimate(0) += q0 * gain * weight
                        estimate(1) += q1 * gain * weight
                        estimate(2) += q2 * gain * weight
                    Next
                Next
            End If

            Dim neutral As Single
            If validCount = 0 Then
                neutral = Math.Max(p(0), Math.Max(p(1), p(2)))
            Else
                Dim sum = 0.0F
                For c = 0 To 2
                    If valid(c) Then sum += p(c)
                Next
                neutral = sum / validCount
            End If

            If weightSum = 0.0F AndAlso validCount > 0 Then
                Dim color = colorMap.Value.At(x, imageY)
                Dim denom = 0.0F, target = 0.0F
                For c = 0 To 2
                    If valid(c) Then
                        denom += color(c)
                        target += p(c)
                    End If
                Next
                If denom > 0.00001F Then
                    Dim gain = target / denom
                    For c = 0 To 2
                        estimate(c) = Math.Min(color(c) * gain, neutral * 4.0F)
                    Next
                    weightSum = 1.0F
                End If
            End If

            Dim blend(2) As Single
            For c = 0 To 2
                blend(c) = Math.Max(0.0F, Math.Min(1.0F, (sensor(c) - ClipFraction) / (1.0F - ClipFraction)))
            Next
            Dim result(2) As Single
            For c = 0 To 2
                Dim target = If(weightSum > 0.0F, estimate(c) / weightSum, neutral)
                ' Ein einzelner geklemmter Kanal: sein Wert ist eine Untergrenze. Sie blendet aus,
                ' sobald ein anderer Kanal selbst an die Grenze kommt, damit dort keine Stufe entsteht.
                Dim alone = 1.0F
                For d = 0 To 2
                    If d <> c Then alone *= 1.0F - blend(d)
                Next
                target += (Math.Max(target, p(c)) - target) * alone
                result(c) = p(c) * (1.0F - blend(c)) + target * blend(c)
            Next
            Return (result(0), result(1), result(2))
        End Function

        ''' <summary>Farbreferenzen fuer breite, teilweise geklemmte Lichter: glatte, ungeklemmte
        ''' Punkte derselben zusammenhaengenden hellen Flaeche. Dunkle Luecken trennen Gegenstaende;
        ''' ein fremder heller Fleck liefert keine Farbe.</summary>
        Private NotInheritable Class RecoveryColorMap
            Private _width As Integer
            Private _height As Integer
            Private _scaleX As Single
            Private _scaleY As Single
            Private _channels As Single()()

            Friend Shared Function Build(data As IntPtr, width As Integer, height As Integer, invClip As Single()) As RecoveryColorMap
                Dim scale = Math.Min(1.0F, CSng(ColorMapMaxEdge) / Math.Max(width, height))
                Dim gw = CInt(Math.Ceiling(width * scale))
                Dim gh = CInt(Math.Ceiling(height * scale))
                Dim cells = gw * gh
                Dim grid(cells * 4 - 1) As Double
                Dim bright(cells - 1) As Integer
                Dim total(cells - 1) As Integer
                Dim rowValues = width * 3
                Dim rowBytes = width * 6
                ' Drei Zeilen Abstand fuer den Glattheitstest; der Lauf geht einmal ueber das Bild.
                Dim window(7 * rowValues - 1) As Short
                Dim peak = Function(q0 As Single, q1 As Single, q2 As Single) _
                               Math.Max(q0 * invClip(0), Math.Max(q1 * invClip(1), q2 * invClip(2)))
                For y = 0 To height - 1
                    For k = -3 To 3
                        Dim yy = Math.Max(0, Math.Min(height - 1, y + k))
                        Marshal.Copy(data + yy * rowBytes, window, (k + 3) * rowValues, rowValues)
                    Next
                    Dim cy = y * gh \ height
                    For x = 0 To width - 1
                        Dim cell = cy * gw + x * gw \ width
                        total(cell) += 1
                        Dim i = 3 * rowValues + x * 3
                        Dim p0 = CSng(CInt(window(i)) And &HFFFF)
                        Dim p1 = CSng(CInt(window(i + 1)) And &HFFFF)
                        Dim p2 = CSng(CInt(window(i + 2)) And &HFFFF)
                        Dim value = peak(p0, p1, p2)
                        If value < BrightFraction Then Continue For
                        bright(cell) += 1
                        If value >= ClipFraction Then Continue For
                        ' Keine Farbe von einer demosaicten Kante gegen einen anderen Gegenstand.
                        Dim smooth = True
                        For Each offset In {(-3, 0), (3, 0), (0, -3), (0, 3)}
                            Dim xx = Math.Max(0, Math.Min(width - 1, x + offset.Item1))
                            Dim j = (3 + offset.Item2) * rowValues + xx * 3
                            Dim q0 = CSng(CInt(window(j)) And &HFFFF)
                            Dim q1 = CSng(CInt(window(j + 1)) And &HFFFF)
                            Dim q2 = CSng(CInt(window(j + 2)) And &HFFFF)
                            If peak(q0, q1, q2) >= ClipFraction OrElse
                               Math.Abs(q0 - p0) > Math.Max(p0, 1.0F) * 0.15F OrElse
                               Math.Abs(q1 - p1) > Math.Max(p1, 1.0F) * 0.15F OrElse
                               Math.Abs(q2 - p2) > Math.Max(p2, 1.0F) * 0.15F Then
                                smooth = False
                                Exit For
                            End If
                        Next
                        If Not smooth Then Continue For
                        Dim sum = p0 + p1 + p2
                        Dim weight = CDbl(value) ^ 4
                        grid(cell * 4) += p0 / sum * weight
                        grid(cell * 4 + 1) += p1 / sum * weight
                        grid(cell * 4 + 2) += p2 / sum * weight
                        grid(cell * 4 + 3) += weight
                    Next
                Next

                Dim channels = New Single()() {New Single(cells - 1) {}, New Single(cells - 1) {}, New Single(cells - 1) {}}
                For c = 0 To 2
                    For i = 0 To cells - 1
                        channels(c)(i) = 1.0F / 3.0F
                    Next
                Next
                Dim visited(cells - 1) As Boolean
                Dim component As New List(Of Integer)()
                For start = 0 To cells - 1
                    If visited(start) OrElse bright(start) * 2 < total(start) Then Continue For
                    component.Clear()
                    component.Add(start)
                    visited(start) = True
                    Dim head = 0
                    Dim s0 = 0.0, s1 = 0.0, s2 = 0.0, s3 = 0.0
                    While head < component.Count
                        Dim i = component(head)
                        head += 1
                        s0 += grid(i * 4) : s1 += grid(i * 4 + 1) : s2 += grid(i * 4 + 2) : s3 += grid(i * 4 + 3)
                        Dim cx = i Mod gw, cyy = i \ gw
                        For Each [next] In {If(cx > 0, i - 1, -1), If(cx + 1 < gw, i + 1, -1),
                                            If(cyy > 0, i - gw, -1), If(cyy + 1 < gh, i + gw, -1)}
                            If [next] < 0 OrElse visited([next]) Then Continue For
                            If bright([next]) * 2 < total([next]) Then Continue For
                            visited([next]) = True
                            component.Add([next])
                        Next
                    End While
                    If s3 > 0.0 Then
                        For Each i In component
                            channels(0)(i) = CSng(s0 / s3)
                            channels(1)(i) = CSng(s1 / s3)
                            channels(2)(i) = CSng(s2 / s3)
                        Next
                    End If
                Next
                Return New RecoveryColorMap With {
                    ._width = gw, ._height = gh,
                    ._scaleX = CSng(gw) / width, ._scaleY = CSng(gh) / height,
                    ._channels = channels}
            End Function

            ''' <summary>Farbe am Bildpunkt, bilinear zwischen den Kartenzellen.</summary>
            Friend Function At(x As Integer, y As Integer) As Single()
                Dim fx = Math.Max(0.0F, Math.Min(_width - 1.0F, (x + 0.5F) * _scaleX - 0.5F))
                Dim fy = Math.Max(0.0F, Math.Min(_height - 1.0F, (y + 0.5F) * _scaleY - 0.5F))
                Dim x0 = CInt(Math.Floor(fx)), y0 = CInt(Math.Floor(fy))
                Dim x1 = Math.Min(_width - 1, x0 + 1), y1 = Math.Min(_height - 1, y0 + 1)
                Dim tx = fx - x0, ty = fy - y0
                Dim result(2) As Single
                For c = 0 To 2
                    Dim ch = _channels(c)
                    Dim top = ch(y0 * _width + x0) + tx * (ch(y0 * _width + x1) - ch(y0 * _width + x0))
                    Dim bottom = ch(y1 * _width + x0) + tx * (ch(y1 * _width + x1) - ch(y1 * _width + x0))
                    result(c) = top + ty * (bottom - top)
                Next
                Return result
            End Function
        End Class

        ''' <summary>Die Klemmgrenze je Kanal, wie sie in den Daten WIRKLICH liegt.
        '''
        ''' Die gerechnete Grenze (<see cref="CameraClipLevels"/>) geht vom nominellen Weisspunkt der
        ''' Datei aus. Viele Sensoren saettigen darunter, und LibRaws Nachfuehrung auf den echten
        ''' Hoechstwert ist bei uns bewusst aus (AdjustMaximumThreshold). Dann laege die Grenze in den
        ''' Daten tiefer, und kein geklemmter Punkt erreichte die gerechnete: an einer XZ-1 mit 8,8 %
        ''' fast weisser Flaeche wurden so 260 Punkte erkannt. Ein Kanal, der wirklich saettigt,
        ''' haeuft viele Punkte an seinem Hoechstwert. Liegt dieser unter der gerechneten Grenze und
        ''' steht dort eine solche Haeufung (mindestens ein Zehntausendstel des Bildes im obersten
        ''' halben Prozent unter dem Hoechstwert), gilt er als Grenze.</summary>
        Friend Shared Function MeasureClipLevels(data As IntPtr, width As Integer, height As Integer,
                                                 computed As Double()) As Double()
            If computed Is Nothing Then Return Nothing
            Dim rowValues = width * 3
            Dim rowBytes = width * 6
            Dim histogram(3 * 65536 - 1) As Long
            Dim gate As New Object()
            Parallel.For(0, height,
                Function() New Long(3 * 65536 - 1) {},
                Function(y As Integer, state As ParallelLoopState, local As Long())
                    Dim row(rowValues - 1) As Short
                    Marshal.Copy(data + y * rowBytes, row, 0, rowValues)
                    For x = 0 To rowValues - 1 Step 3
                        local(CInt(row(x)) And &HFFFF) += 1
                        local(65536 + (CInt(row(x + 1)) And &HFFFF)) += 1
                        local(131072 + (CInt(row(x + 2)) And &HFFFF)) += 1
                    Next
                    Return local
                End Function,
                Sub(local As Long())
                    SyncLock gate
                        For i = 0 To histogram.Length - 1
                            histogram(i) += local(i)
                        Next
                    End SyncLock
                End Sub)

            Dim pixelCount = CLng(width) * height
            Dim needed = Math.Max(50L, pixelCount \ 10000)
            Dim result(2) As Double
            For c = 0 To 2
                result(c) = computed(c)
                Dim offset = c * 65536
                Dim top = 65535
                While top > 0 AndAlso histogram(offset + top) = 0
                    top -= 1
                End While
                If top <= 0 OrElse top >= computed(c) * 0.98 Then Continue For
                Dim bandStart = CInt(Math.Floor(top * 0.995))
                Dim inBand = 0L
                For v = bandStart To top
                    inBand += histogram(offset + v)
                Next
                If inBand >= needed Then result(c) = top
            Next
            Return result
        End Function

        ''' <summary>Klemmgrenze je Kanal im Kameraraum nach dcraws Normierung mit Entklemmen:
        ''' 65535 * mul[c] / max(mul). Der vierte Multiplikator (zweites Gruen) zaehlt fuer das
        ''' Maximum mit, wie bei dcraw; ist er null, gilt der gruene.</summary>
        Friend Shared Function CameraClipLevels(multipliers As Single()) As Double()
            If multipliers Is Nothing OrElse multipliers.Length < 3 Then Return Nothing
            Dim values(3) As Double
            For i = 0 To 2
                values(i) = multipliers(i)
            Next
            values(3) = If(multipliers.Length > 3 AndAlso multipliers(3) > 0.0F, CDbl(multipliers(3)), values(1))
            Dim largest = 0.0
            For i = 0 To 3
                If Not Double.IsFinite(values(i)) OrElse values(i) <= 0.0 Then Return Nothing
                largest = Math.Max(largest, values(i))
            Next
            Return New Double() {65535.0 * values(0) / largest, 65535.0 * values(1) / largest, 65535.0 * values(2) / largest}
        End Function

        ' ZWEITES VERFAHREN, nur als Messschalter: Gegenkanaele mit Push-Pull, wie RapidRAW 1.6.7
        ' (highlight_reconstruction.rs, AGPL; uebertragen ist das Verfahren, die Konstanten sind
        ' dieselben). Ein geklemmter Kanal wird aus dem Mittel der Kubikwurzeln der beiden anderen
        ' geschaetzt, verschoben um einen Versatz, den eine Push-Pull-Karte aus ungeklemmten Punkten
        ' am Rand der hellen Flaeche liefert. Nahe am Vollbeschnitt wird die Farbe zurueckgenommen
        ' (Lichtquellen bleiben neutral), und ganz ausgebrannte Punkte bekommen zur Haelfte die Farbe
        ' der Umgebung aus einer zweiten Push-Pull-Karte. Alles skaliert mit den Daten, die
        ' Konstanten gelten deshalb auf den 16-Bit-Werten genauso wie auf RapidRAWs 0 bis 1.

        ''' <summary>Welches Verfahren <see cref="HighlightReconstruction"/> nimmt: 0 Nachbarn und
        ''' Farbkarte, 1 Gegenkanaele mit Push-Pull (ab Werk, in der Messung oben das bessere).
        ''' Gesetzt per Reflexion (REGLER_LICHTER_VERFAHREN=0 oder 1).</summary>
        Friend Shared HighlightReconstructionMethod As Integer = 1

        Private Const OpposedClipThreshold As Single = 0.985F
        Private Const OpposedSoftThreshold As Single = 0.9F
        Private Const OpposedMinSampleLevel As Single = 0.2F
        Private Const OpposedBorderExclusion As Integer = 2
        Private Const OpposedSampleBand As Integer = 24
        Private Const OpposedIterations As Integer = 3
        Private Const OpposedFullClipChroma As Single = 0.35F
        Private Const OpposedHeadroomFade As Single = 0.3F
        Private Const OpposedFillSupportDivisor As Single = 28.0F
        Private Const OpposedFillSpreadStart As Single = 0.25F
        Private Const OpposedFillSpreadEnd As Single = 0.6F
        Private Const OpposedFillStrength As Single = 0.5F
        Private Const OpposedFillSampleSpread As Single = 0.25F

        ''' <summary>Ein grobes Raster mit drei Werten je Zelle, bilinear abgetastet.</summary>
        Private NotInheritable Class OpposedGrid
            Public ReadOnly Width As Integer
            Public ReadOnly Height As Integer
            Public ReadOnly Data As Single()

            Public Sub New(width As Integer, height As Integer, data As Single())
                Me.Width = width
                Me.Height = height
                Me.Data = data
            End Sub

            Public Sub Sample(x As Single, y As Single, result As Single())
                x = Math.Clamp(x, 0.0F, Width - 1)
                y = Math.Clamp(y, 0.0F, Height - 1)
                Dim x0 = CInt(Math.Floor(x)), y0 = CInt(Math.Floor(y))
                Dim x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1)
                Dim fx = x - x0, fy = y - y0
                For i = 0 To 2
                    Dim a = Data((y0 * Width + x0) * 3 + i), b = Data((y0 * Width + x1) * 3 + i)
                    Dim c = Data((y1 * Width + x0) * 3 + i), d = Data((y1 * Width + x1) * 3 + i)
                    Dim upper = a + (b - a) * fx
                    Dim lower = c + (d - c) * fx
                    result(i) = upper + (lower - upper) * fy
                Next
            End Sub
        End Class

        Private Shared Function SmoothStep(edge0 As Single, edge1 As Single, x As Single) As Single
            Dim t = Math.Clamp((x - edge0) / (edge1 - edge0), 0.0F, 1.0F)
            Return t * t * (3.0F - 2.0F * t)
        End Function

        Private Shared Function Opposed(roots As Single(), c As Integer) As Single
            Dim m = 0.5F * (roots((c + 1) Mod 3) + roots((c + 2) Mod 3))
            Return m * m * m
        End Function

        ''' <summary>Baut geklemmte Kanaele nach dem zweiten Verfahren auf; Gegenstueck zu
        ''' <see cref="ReconstructClippedHighlights"/> mit derselben Schnittstelle.</summary>
        Friend Shared Function ReconstructOpposed(data As IntPtr, width As Integer, height As Integer,
                                                  clipLevels As Double()) As Integer
            If data = IntPtr.Zero OrElse width < 4 OrElse height < 4 Then Return 0
            If clipLevels Is Nothing OrElse clipLevels.Length < 3 Then Return 0
            Dim clips(2) As Single, thresholds(2) As Single, softStarts(2) As Single
            For c = 0 To 2
                If Not Double.IsFinite(clipLevels(c)) OrElse clipLevels(c) <= 1.0 Then Return 0
                clips(c) = CSng(clipLevels(c))
                thresholds(c) = clips(c) * OpposedClipThreshold
                softStarts(c) = clips(c) * OpposedSoftThreshold
            Next

            Dim rowValues = width * 3
            Dim rowBytes = width * 6
            ' ERST DIE MASKE, dann der Gleitkommapuffer: der kostet zwoelf Byte je Punkt (bei 24 MP
            ' rund 290 MB) und wird nur gebraucht, wenn wirklich etwas klemmt.
            Dim mask(width * height - 1) As Byte
            Dim anyClipped = 0
            Parallel.For(0, height,
                Sub(y As Integer)
                    Dim row(rowValues - 1) As Short
                    Marshal.Copy(data + y * rowBytes, row, 0, rowValues)
                    Dim local = False
                    For x = 0 To width - 1
                        Dim bits = 0
                        For c = 0 To 2
                            If (CInt(row(x * 3 + c)) And &HFFFF) >= thresholds(c) Then bits = bits Or (1 << c)
                        Next
                        mask(y * width + x) = CByte(bits)
                        If bits <> 0 Then local = True
                    Next
                    If local Then Interlocked.Exchange(anyClipped, 1)
                End Sub)
            If anyClipped = 0 Then Return 0
            Dim px(CInt(CLng(width) * height * 3) - 1) As Single
            Parallel.For(0, height,
                Sub(y As Integer)
                    Dim row(rowValues - 1) As Short
                    Marshal.Copy(data + y * rowBytes, row, 0, rowValues)
                    Dim start = y * width * 3
                    For k = 0 To rowValues - 1
                        px(start + k) = CInt(row(k)) And &HFFFF
                    Next
                End Sub)

            Dim cell = Math.Clamp(Math.Max(width, height) \ 1024, 1, 8)
            Dim offsets = OpposedInpaintOffsets(px, width, height, mask, cell, clips, thresholds)

            Dim invCell = 1.0F / cell
            Dim blownX(height - 1) As List(Of Integer)
            Dim blownObserved(height - 1) As List(Of Single)
            Dim blownFull(height - 1) As List(Of Single)
            Dim changedRows(height - 1) As Boolean
            Parallel.For(0, height,
                Sub(y As Integer)
                    Dim offset(2) As Single, observed(2) As Single, blend(2) As Single, roots(2) As Single
                    Dim gy = (y + 0.5F) * invCell - 0.5F
                    For x = 0 To width - 1
                        Dim i = (y * width + x) * 3
                        Dim anyBlend = False
                        For c = 0 To 2
                            observed(c) = px(i + c)
                            blend(c) = SmoothStep(softStarts(c), thresholds(c), observed(c))
                            If blend(c) > 0.0F Then anyBlend = True
                        Next
                        If Not anyBlend Then Continue For
                        changedRows(y) = True
                        Dim full = Math.Min(blend(0), Math.Min(blend(1), blend(2)))
                        If full > 0.0F Then
                            If blownX(y) Is Nothing Then
                                blownX(y) = New List(Of Integer)()
                                blownObserved(y) = New List(Of Single)()
                                blownFull(y) = New List(Of Single)()
                            End If
                            blownX(y).Add(x)
                            blownObserved(y).Add(observed(0)) : blownObserved(y).Add(observed(1)) : blownObserved(y).Add(observed(2))
                            blownFull(y).Add(full)
                        End If

                        offsets.Sample((x + 0.5F) * invCell - 0.5F, gy, offset)
                        For c = 0 To 2
                            roots(c) = MathF.Cbrt(Math.Max(0.0F, observed(c)))
                        Next
                        For iteration = 0 To OpposedIterations - 1
                            For c = 0 To 2
                                If blend(c) > 0.0F Then
                                    Dim rebuilt = Math.Max(observed(c), Opposed(roots, c) + offset(c))
                                    px(i + c) = observed(c) + (rebuilt - observed(c)) * blend(c)
                                End If
                            Next
                            If iteration + 1 < OpposedIterations Then
                                For c = 0 To 2
                                    If blend(c) > 0.0F Then roots(c) = MathF.Cbrt(Math.Max(0.0F, px(i + c)))
                                Next
                            End If
                        Next

                        ' Wie viel Abstand zur Grenze der hellste ungeklemmte Kanal noch hat:
                        ' ohne jeden wird die Farbe auf ein Drittel zurueckgenommen.
                        Dim bits = mask(y * width + x)
                        Dim headroom = 0.0F
                        For c = 0 To 2
                            If (bits And (1 << c)) = 0 Then headroom = Math.Max(headroom, 1.0F - observed(c) / clips(c))
                        Next
                        Dim keep = OpposedFullClipChroma + (1.0F - OpposedFullClipChroma) * SmoothStep(0.0F, OpposedHeadroomFade, headroom)
                        Dim mean = (px(i) + px(i + 1) + px(i + 2)) / 3.0F
                        For c = 0 To 2
                            If blend(c) > 0.0F Then
                                Dim faded = Math.Max(observed(c), mean + (px(i + c) - mean) * keep)
                                px(i + c) = observed(c) + (faded - observed(c)) * blend(c)
                            End If
                        Next
                    Next
                End Sub)

            If blownX.Any(Function(list) list IsNot Nothing) Then
                OpposedFillBlown(px, width, height, mask, blownX, blownObserved, blownFull, cell)
            End If

            Dim changed = 0
            Parallel.For(0, height,
                Sub(y As Integer)
                    If Not changedRows(y) Then Return
                    Dim row(rowValues - 1) As Short
                    Dim start = y * width * 3
                    For k = 0 To rowValues - 1
                        row(k) = ToUInt16Bits(px(start + k))
                    Next
                    Marshal.Copy(row, 0, data + y * rowBytes, rowValues)
                    Interlocked.Increment(changed)
                End Sub)
            Return changed
        End Function

        ''' <summary>Der Versatz zwischen echtem Kanal und Gegenkanal-Schaetzung, gesammelt an
        ''' ungeklemmten Punkten nahe der geklemmten Flaeche und per Push-Pull ueber das Raster
        ''' verteilt.</summary>
        Private Shared Function OpposedInpaintOffsets(px As Single(), width As Integer, height As Integer, mask As Byte(),
                                                      cell As Integer, clips As Single(), thresholds As Single()) As OpposedGrid
            Dim nearClip = DilateMask(mask, width, height, OpposedBorderExclusion)
            Dim low(2) As Single, invClips(2) As Single
            For c = 0 To 2
                low(c) = clips(c) * OpposedMinSampleLevel
                invClips(c) = 1.0F / clips(c)
            Next
            Dim gw = (width + cell - 1) \ cell
            Dim gh = (height + cell - 1) \ cell
            Dim clippedCells = CellMask(mask, width, height, cell, gw, gh)
            Dim band = DilateMask(clippedCells, gw, gh, (OpposedSampleBand + cell - 1) \ cell)

            Dim weights(gw * gh * 3 - 1) As Single
            Dim sums(gw * gh * 3 - 1) As Single
            Parallel.For(0, gh,
                Sub(gy As Integer)
                    Dim roots(2) As Single
                    For y = gy * cell To Math.Min(height, (gy + 1) * cell) - 1
                        For gx = 0 To gw - 1
                            Dim channels = band(gy * gw + gx)
                            If channels = 0 Then Continue For
                            For x = gx * cell To Math.Min(width, (gx + 1) * cell) - 1
                                Dim i = (y * width + x) * 3
                                Dim usable = channels And Not nearClip(y * width + x)
                                Dim haveRoots = False
                                For c = 0 To 2
                                    Dim p = px(i + c)
                                    If (usable And (1 << c)) = 0 OrElse p <= low(c) OrElse p >= thresholds(c) Then Continue For
                                    If Not haveRoots Then
                                        For k = 0 To 2
                                            roots(k) = MathF.Cbrt(Math.Max(0.0F, px(i + k)))
                                        Next
                                        haveRoots = True
                                    End If
                                    Dim level = p * invClips(c)
                                    Dim w = level * level
                                    Dim target = (gy * gw + gx) * 3 + c
                                    weights(target) += w
                                    sums(target) += w * (p - Opposed(roots, c))
                                Next
                            Next
                        Next
                    Next
                End Sub)
            Return PushPull(weights, sums, gw, gh, 0.25F * cell * cell)
        End Function

        ''' <summary>Ganz ausgebrannte Punkte zur Haelfte in die Farbe der Umgebung ziehen, gesammelt
        ''' an Punkten mit genau einem geklemmten Kanal und per Push-Pull verteilt.</summary>
        Private Shared Sub OpposedFillBlown(px As Single(), width As Integer, height As Integer, mask As Byte(),
                                            blownX As List(Of Integer)(), blownObserved As List(Of Single)(),
                                            blownFull As List(Of Single)(), cell As Integer)
            Dim blownMask(width * height - 1) As Byte
            For y = 0 To height - 1
                If blownX(y) Is Nothing Then Continue For
                For Each x In blownX(y)
                    blownMask(y * width + x) = 1
                Next
            Next
            Dim nearBlown = DilateMask(blownMask, width, height, OpposedBorderExclusion)
            Dim gw = (width + cell - 1) \ cell
            Dim gh = (height + cell - 1) \ cell
            Dim blownCells = CellMask(blownMask, width, height, cell, gw, gh)
            Dim band = DilateMask(blownCells, gw, gh, (OpposedSampleBand + cell - 1) \ cell)

            Dim weights(gw * gh * 3 - 1) As Single
            Dim sums(gw * gh * 3 - 1) As Single
            Parallel.For(0, gh,
                Sub(gy As Integer)
                    For y = gy * cell To Math.Min(height, (gy + 1) * cell) - 1
                        For x = 0 To width - 1
                            Dim cellIndex = gy * gw + x \ cell
                            If band(cellIndex) = 0 Then Continue For
                            Dim m = mask(y * width + x)
                            If m <> 1 AndAlso m <> 2 AndAlso m <> 4 Then Continue For
                            If nearBlown(y * width + x) <> 0 Then Continue For
                            Dim i = (y * width + x) * 3
                            If px(i) <= 0.0F OrElse px(i + 1) <= 0.0F OrElse px(i + 2) <= 0.0F Then Continue For
                            Dim rg = MathF.Log(px(i) / px(i + 1))
                            Dim bg = MathF.Log(px(i + 2) / px(i + 1))
                            Dim spread = (Math.Max(Math.Max(rg, bg), 0.0F) - Math.Min(Math.Min(rg, bg), 0.0F)) / OpposedFillSampleSpread
                            Dim w = 1.0F / (1.0F + spread * spread)
                            weights(cellIndex * 3) += w
                            weights(cellIndex * 3 + 1) += w
                            sums(cellIndex * 3) += w * rg
                            sums(cellIndex * 3 + 1) += w * bg
                        Next
                    Next
                End Sub)
            Dim support = Math.Max(width, height) / OpposedFillSupportDivisor
            Dim chroma = PushPull(weights, sums, gw, gh, 0.5F * support * support)

            Dim invCell = 1.0F / cell
            Parallel.For(0, height,
                Sub(y As Integer)
                    If blownX(y) Is Nothing Then Return
                    Dim sample(2) As Single, tint(2) As Single
                    Dim gy = (y + 0.5F) * invCell - 0.5F
                    For k = 0 To blownX(y).Count - 1
                        Dim x = blownX(y)(k)
                        Dim full = blownFull(y)(k)
                        chroma.Sample((x + 0.5F) * invCell - 0.5F, gy, sample)
                        Dim rg = sample(0), bg = sample(1)
                        Dim spread = Math.Max(Math.Max(rg, bg), 0.0F) - Math.Min(Math.Min(rg, bg), 0.0F)
                        Dim strength = OpposedFillStrength * (1.0F - SmoothStep(OpposedFillSpreadStart, OpposedFillSpreadEnd, spread))
                        tint(0) = MathF.Exp(rg * strength) : tint(1) = 1.0F : tint(2) = MathF.Exp(bg * strength)
                        Dim i = (y * width + x) * 3
                        Dim brightness = (px(i) + px(i + 1) + px(i + 2)) / (tint(0) + tint(1) + tint(2))
                        Dim scale = brightness
                        For c = 0 To 2
                            scale = Math.Max(scale, blownObserved(y)(k * 3 + c) / tint(c))
                        Next
                        For c = 0 To 2
                            px(i + c) += (scale * tint(c) - px(i + c)) * full
                        Next
                    Next
                End Sub)
        End Sub

        ''' <summary>Die Bits einer Maske je Rasterzelle zusammengefasst (ODER).</summary>
        Private Shared Function CellMask(mask As Byte(), width As Integer, height As Integer, cell As Integer,
                                         gw As Integer, gh As Integer) As Byte()
            Dim cells(gw * gh - 1) As Byte
            Parallel.For(0, gh,
                Sub(gy As Integer)
                    For y = gy * cell To Math.Min(height, (gy + 1) * cell) - 1
                        For x = 0 To width - 1
                            cells(gy * gw + x \ cell) = cells(gy * gw + x \ cell) Or mask(y * width + x)
                        Next
                    Next
                End Sub)
            Return cells
        End Function

        ''' <summary>Erweitert eine Bitmaske um <paramref name="radius"/> in jede Richtung (ODER im
        ''' Quadrat).</summary>
        Private Shared Function DilateMask(mask As Byte(), width As Integer, height As Integer, radius As Integer) As Byte()
            Dim result(mask.Length - 1) As Byte
            Parallel.For(0, height,
                Function() New Byte(width - 1) {},
                Function(y As Integer, state As ParallelLoopState, vertical As Byte())
                    Array.Clear(vertical)
                    For yy = Math.Max(0, y - radius) To Math.Min(height - 1, y + radius)
                        For x = 0 To width - 1
                            vertical(x) = vertical(x) Or mask(yy * width + x)
                        Next
                    Next
                    For x = 0 To width - 1
                        Dim value As Byte = 0
                        For xx = Math.Max(0, x - radius) To Math.Min(width - 1, x + radius)
                            value = value Or vertical(xx)
                        Next
                        result(y * width + x) = value
                    Next
                    Return vertical
                End Function,
                Sub(vertical As Byte())
                End Sub)
            Return result
        End Function

        ''' <summary>Push-Pull: Gewichte und gewichtete Summen in einer Pyramide bis auf eine Zelle
        ''' zusammenfassen, dann von oben nach unten auffuellen. Eine Zelle mit eigenem Gewicht
        ''' setzt sich gegen das Grobere durch, voll ab <paramref name="fullWeight"/>.</summary>
        Private Shared Function PushPull(weights As Single(), sums As Single(), width As Integer, height As Integer,
                                         fullWeight As Single) As OpposedGrid
            Dim levelWeights As New List(Of Single()) From {weights}
            Dim levelSums As New List(Of Single()) From {sums}
            Dim levelSizes As New List(Of (W As Integer, H As Integer)) From {(width, height)}
            While levelSizes(levelSizes.Count - 1).W > 1 OrElse levelSizes(levelSizes.Count - 1).H > 1
                Dim pw = levelSizes(levelSizes.Count - 1).W, ph = levelSizes(levelSizes.Count - 1).H
                Dim prevW = levelWeights(levelWeights.Count - 1), prevS = levelSums(levelSums.Count - 1)
                Dim nw = (pw + 1) \ 2, nh = (ph + 1) \ 2
                Dim nextW(nw * nh * 3 - 1) As Single, nextS(nw * nh * 3 - 1) As Single
                For y = 0 To nh - 1
                    For x = 0 To nw - 1
                        For yy = 2 * y To Math.Min(2 * y + 2, ph) - 1
                            For xx = 2 * x To Math.Min(2 * x + 2, pw) - 1
                                For i = 0 To 2
                                    nextW((y * nw + x) * 3 + i) += prevW((yy * pw + xx) * 3 + i)
                                    nextS((y * nw + x) * 3 + i) += prevS((yy * pw + xx) * 3 + i)
                                Next
                            Next
                        Next
                    Next
                Next
                levelWeights.Add(nextW) : levelSums.Add(nextS) : levelSizes.Add((nw, nh))
            End While

            Dim top = levelWeights.Count - 1
            Dim topData(2) As Single
            For i = 0 To 2
                topData(i) = If(levelWeights(top)(i) > 0.0F, levelSums(top)(i) / levelWeights(top)(i), 0.0F)
            Next
            Dim filled As New OpposedGrid(1, 1, topData)
            For level = top - 1 To 0 Step -1
                Dim w = levelSizes(level).W, h = levelSizes(level).H
                Dim lw = levelWeights(level), ls = levelSums(level)
                Dim scaleX = CSng(filled.Width / w), scaleY = CSng(filled.Height / h)
                Dim data(w * h * 3 - 1) As Single
                Dim coarseGrid = filled
                Parallel.For(0, h,
                    Sub(y As Integer)
                        Dim coarse(2) As Single
                        Dim sy = (y + 0.5F) * scaleY - 0.5F
                        For x = 0 To w - 1
                            coarseGrid.Sample((x + 0.5F) * scaleX - 0.5F, sy, coarse)
                            For i = 0 To 2
                                Dim k = (y * w + x) * 3 + i
                                If lw(k) > 0.0F Then
                                    Dim alpha = Math.Min(lw(k) / fullWeight, 1.0F)
                                    data(k) = coarse(i) + (ls(k) / lw(k) - coarse(i)) * alpha
                                Else
                                    data(k) = coarse(i)
                                End If
                            Next
                        Next
                    End Sub)
                filled = New OpposedGrid(w, h, data)
            Next
            Return filled
        End Function

        ''' <summary>Taugt die Matrix als Kameraraum-nach-sRGB? Endlich und mit Zeilensummen um 1
        ''' (LibRaw normiert rgb_cam so); eine Einheits- oder Nullmatrix (Kamera ohne Matrix) nicht.</summary>
        Private Shared Function IsUsableCameraMatrix(m As Single()) As Boolean
            If m Is Nothing OrElse m.Length < 9 Then Return False
            Dim identity = True
            For i = 0 To 8
                If Not Single.IsFinite(m(i)) Then Return False
                Dim expected = If(i Mod 4 = 0, 1.0F, 0.0F)
                If Math.Abs(m(i) - expected) > 0.0001F Then identity = False
            Next
            If identity Then Return False
            For row = 0 To 2
                Dim sum = m(row * 3) + m(row * 3 + 1) + m(row * 3 + 2)
                If sum < 0.5F OrElse sum > 1.5F Then Return False
            Next
            Return True
        End Function

    End Class

End Namespace
