Imports System
Imports System.Collections.Generic
Imports SkiaSharp

' Der Reparaturpinsel mit EINEM Versatz je Strich: statt die Stelle Flicken fuer Flicken von aussen
' nach innen zu fuellen, wird fuer den ganzen Strich eine Quelle gesucht und als Ganzes kopiert, und
' der Unterschied im Grundton wird ueber eine Membran angeglichen, die nur am Rand eingespannt ist.
' Gewonnen ist das vor allem an einer Linie, die durch die Stelle laeuft (Baumlinie, Horizont): die
' Fuellung waechst von einer Seite und zieht sie hoch oder kerbt sie ein, ein Versatz entlang der
' Linie setzt sie als Ganzes fort. Passt keine Quelle am Saum, bleibt es bei der Fuellung. Das Teilen
' in Abschnitte ist als Messschalter da, ab Werk aus (siehe HealingOffsetSegmentFactor).
' Eigener Zustand: keiner (nur die Messschalter und der Bericht des letzten Laufs).
Namespace Services

    Partial Public Class ImageProcessor

        ''' <summary>MESSSCHALTER fuer den Versatz: 0 aus (immer die Fuellung), 1 nach dem
        ''' Saumfehler (ab Werk), 2 immer, wenn eine Quelle gefunden wird.</summary>
        Friend Shared HealingOffsetMode As Integer = 1

        ''' <summary>Was der letzte Versuch gefunden hat, fuer die Messung: Versaetze, Saumfehler,
        ''' ob er genommen wurde.</summary>
        Friend Shared LastHealingOffsetReport As String = ""

        ''' <summary>Grenze des Saumfehlers, unter der der Versatz genommen wird: verbliebene
        ''' Strukturabweichung im Saum im Verhaeltnis zur Struktur des Saums selbst. Gemessen an elf
        ''' Fotos mit Horizont und je sechs Strichen (Diagnostics/Reparaturmessung): unter 0,15 war der
        ''' Versatz im Augenschein in den meisten Faellen besser und fast nie schlechter; darueber
        ''' nahmen die Faelle zu, in denen er fremden Inhalt (ein anderes Gebaeude, einen anderen
        ''' Grat) in die Stelle setzt. Die Zahl gegen das Original sagt dazu wenig: ein verwischter
        ''' Mittelwert liegt ihm naeher als echtes, aber anderes Laub.</summary>
        Friend Shared HealingOffsetAcceptRatio As Double = 0.15

        ''' <summary>Aufschlag fuer weite Versaetze: Faktor 1 + Wert mal Versatz durch Ausdehnung.</summary>
        Friend Shared HealingOffsetDistancePenalty As Double = 0.15

        ''' <summary>MESSSCHALTER: Laenge eines Abschnitts als Vielfaches der Strichbreite, 0 (ab Werk)
        ''' teilt nicht. Gemessen mit 2,5 und VERWORFEN: zwischen den Abschnitten entstanden harte,
        ''' eckige Kanten (ein dunkler Block auf einem Bergruecken, eckige Dachstuecke am Ufer), lange
        ''' Striche wurden in Zahl und Augenschein schlechter als ungeteilt.</summary>
        Friend Shared HealingOffsetSegmentFactor As Double = 0

        ''' <summary>Streuung des Unterschieds Ziel minus Quelle in einer Saumzelle (mittlere Abweichung
        ''' vom Median, in Stufen), ab der die Zelle auf einer verfehlten Kante liegt und den Ausgleich
        ''' nicht einspannt.</summary>
        Private Const HealingOffsetCellSpread As Single = 10.0F

        ''' <summary>Rest am Rand, der noch voll hineingezogen wird, und der, ab dem gar nicht mehr.</summary>
        Private Const HealingOffsetRestFullBelow As Single = 6.0F
        Private Const HealingOffsetRestNoneAbove As Single = 16.0F

        ''' <summary>Versucht den Versatz. True, wenn er geschrieben hat; sonst bleibt
        ''' <paramref name="result"/> unberuehrt und die Fuellung uebernimmt.</summary>
        Private Shared Function TryHealByStrokeOffset(result As SKBitmap, maskAlpha As Byte(), blendAlpha As Byte(),
                                                      targetLeft As Integer, targetTop As Integer,
                                                      width As Integer, height As Integer,
                                                      patchRadius As Integer) As Boolean
            LastHealingOffsetReport = ""
            If HealingOffsetMode = 0 Then Return False
            If result Is Nothing OrElse maskAlpha Is Nothing OrElse blendAlpha Is Nothing Then Return False
            If result.ColorType <> SKColorType.Bgra8888 AndAlso result.ColorType <> SKColorType.Rgba8888 Then Return False

            ' Die Punkte der Stelle im Bild, dazu ihre Hauptrichtung.
            Dim points As New List(Of (X As Integer, Y As Integer))()
            Dim mx0 = width, my0 = height, mx1 = -1, my1 = -1
            Dim sumX = 0.0, sumY = 0.0
            For maskY = 0 To height - 1
                Dim y = targetTop + maskY
                If y < 0 OrElse y >= result.Height Then Continue For
                For mx = 0 To width - 1
                    Dim x = targetLeft + mx
                    If x < 0 OrElse x >= result.Width Then Continue For
                    If maskAlpha(maskY * width + mx) <= 8 Then Continue For
                    points.Add((x, y))
                    sumX += x : sumY += y
                    If mx < mx0 Then mx0 = mx
                    If mx > mx1 Then mx1 = mx
                    If maskY < my0 Then my0 = maskY
                    If maskY > my1 Then my1 = maskY
                Next
            Next
            If points.Count = 0 Then Return False

            ' Abschnitte entlang der Hauptachse. Breite = Flaeche durch Laenge.
            Dim meanX = sumX / points.Count, meanY = sumY / points.Count
            Dim sxx = 0.0, sxy = 0.0, syy = 0.0
            For Each p In points
                Dim dx = p.X - meanX, dy = p.Y - meanY
                sxx += dx * dx : sxy += dx * dy : syy += dy * dy
            Next
            Dim angle = 0.5 * Math.Atan2(2.0 * sxy, sxx - syy)
            Dim ax = Math.Cos(angle), ay = Math.Sin(angle)
            Dim tMin = Double.MaxValue, tMax = Double.MinValue
            For Each p In points
                Dim t = (p.X - meanX) * ax + (p.Y - meanY) * ay
                If t < tMin Then tMin = t
                If t > tMax Then tMax = t
            Next
            Dim length = Math.Max(1.0, tMax - tMin + 1.0)
            Dim thickness = Math.Max(1.0, points.Count / length)
            Dim segmentLength = If(HealingOffsetSegmentFactor > 0, thickness * HealingOffsetSegmentFactor, length)
            Dim segmentCount = Math.Max(1, CInt(Math.Round(length / Math.Max(1.0, segmentLength))))
            If length < segmentLength * 1.5 Then segmentCount = 1
            segmentCount = Math.Min(segmentCount, 24)

            ' Groesste Ausdehnung eines Abschnitts: daran haengen Saum und Suchweite.
            Dim segBox(segmentCount - 1) As (X0 As Integer, Y0 As Integer, X1 As Integer, Y1 As Integer)
            For s = 0 To segmentCount - 1
                segBox(s) = (Integer.MaxValue, Integer.MaxValue, Integer.MinValue, Integer.MinValue)
            Next
            Dim segmentOf(points.Count - 1) As Integer
            For k = 0 To points.Count - 1
                Dim p = points(k)
                Dim t = (p.X - meanX) * ax + (p.Y - meanY) * ay
                Dim s = Math.Min(segmentCount - 1, CInt(Math.Floor((t - tMin) / length * segmentCount)))
                segmentOf(k) = s
                Dim b = segBox(s)
                segBox(s) = (Math.Min(b.X0, p.X), Math.Min(b.Y0, p.Y), Math.Max(b.X1, p.X), Math.Max(b.Y1, p.Y))
            Next
            Dim extent = 1
            For s = 0 To segmentCount - 1
                If segBox(s).X1 < segBox(s).X0 Then Continue For
                extent = Math.Max(extent, Math.Max(segBox(s).X1 - segBox(s).X0 + 1, segBox(s).Y1 - segBox(s).Y0 + 1))
            Next

            ' Saum: bekannte Punkte bis zu dieser Entfernung von der Stelle.
            Dim ringW = Math.Min(64, Math.Max(Math.Max(4, patchRadius * 2), extent \ 6))
            ' Suchweite: eine Quelle darf die Stelle nicht beruehren, braucht also mindestens die
            ' Ausdehnung plus Saum Abstand; weiter als das Doppelte lohnt selten.
            Dim reach = Math.Min(1600, extent * 2 + ringW * 4)

            ' Bereich im Bild: Maske plus Saum plus Suchweite.
            Dim rx0 = Math.Max(0, targetLeft + mx0 - reach - ringW)
            Dim ry0 = Math.Max(0, targetTop + my0 - reach - ringW)
            Dim rx1 = Math.Min(result.Width - 1, targetLeft + mx1 + reach + ringW)
            Dim ry1 = Math.Min(result.Height - 1, targetTop + my1 + reach + ringW)
            Dim buffer = RegionPixelBuffer.FromRegion(result, rx0, ry0, rx1, ry1)
            If buffer Is Nothing Then Return False
            Dim rw = buffer.Width, rh = buffer.Height
            Dim n = rw * rh
            Dim red(n - 1) As Single, green(n - 1) As Single, blue(n - 1) As Single
            Dim alphaPlane(n - 1) As Byte
            For y = 0 To rh - 1
                For x = 0 To rw - 1
                    Dim c = buffer.GetColor(rx0 + x, ry0 + y)
                    Dim i = y * rw + x
                    red(i) = c.Red : green(i) = c.Green : blue(i) = c.Blue : alphaPlane(i) = c.Alpha
                Next
            Next

            ' Abschnitt je Punkt im Bereich, -1 heisst bekannt.
            Dim segment(n - 1) As Integer
            For i = 0 To n - 1
                segment(i) = -1
            Next
            For k = 0 To points.Count - 1
                Dim rx = points(k).X - rx0, ry = points(k).Y - ry0
                If rx < 0 OrElse ry < 0 OrElse rx >= rw OrElse ry >= rh Then Continue For
                segment(ry * rw + rx) = segmentOf(k)
            Next

            ' Grobe Stufe: Bloecke von f Punkten, einmal fuer den ganzen Bereich. Ein Block mit einem
            ' Punkt der Stelle oder einem halbdurchsichtigen Punkt taugt nicht als Quelle.
            Dim f = Math.Max(1, CInt(Math.Ceiling(reach * 2.0 / 160.0)))
            Dim cw = rw \ f, ch = rh \ f
            If cw < 3 OrElse ch < 3 Then Return False
            Dim cn = cw * ch
            Dim cRed(cn - 1) As Single, cGreen(cn - 1) As Single, cBlue(cn - 1) As Single
            Dim cBad(cn - 1) As Boolean
            For cy = 0 To ch - 1
                For cx = 0 To cw - 1
                    Dim sr = 0.0F, sg = 0.0F, sb = 0.0F
                    Dim bad = False
                    For y = cy * f To cy * f + f - 1
                        For x = cx * f To cx * f + f - 1
                            Dim i = y * rw + x
                            sr += red(i) : sg += green(i) : sb += blue(i)
                            If segment(i) >= 0 OrElse alphaPlane(i) < 255 Then bad = True
                        Next
                    Next
                    Dim ci = cy * cw + cx
                    Dim area = CSng(f * f)
                    cRed(ci) = sr / area : cGreen(ci) = sg / area : cBlue(ci) = sb / area
                    cBad(ci) = bad
                Next
            Next

            ' Abschnitt fuer Abschnitt; die Ergebnisse landen in red/green/blue, damit der naechste
            ' Abschnitt am reparierten ansetzt.
            Dim dist(n - 1) As Single
            Dim sourceAlpha(n - 1) As Byte
            Dim worstRatio = 0.0
            Dim report As New Text.StringBuilder()
            For s = 0 To segmentCount - 1
                Dim sb = segBox(s)
                If sb.X1 < sb.X0 Then Continue For
                Dim bx0 = Math.Max(0, sb.X0 - rx0 - ringW), by0 = Math.Max(0, sb.Y0 - ry0 - ringW)
                Dim bx1 = Math.Min(rw - 1, sb.X1 - rx0 + ringW), by1 = Math.Min(rh - 1, sb.Y1 - ry0 + ringW)
                Dim found = HealOffsetSegment(red, green, blue, alphaPlane, sourceAlpha, segment, s, dist,
                                              rw, rh, bx0, by0, bx1, by1, ringW, reach, extent, f,
                                              cRed, cGreen, cBlue, cBad, cw, ch)
                If Not found.Ok Then
                    LastHealingOffsetReport = report.ToString() & $"Abschnitt {s + 1}/{segmentCount}: {found.Reason}"
                    Return False
                End If
                worstRatio = Math.Max(worstRatio, found.Ratio)
                report.Append($"{found.Dx},{found.Dy} ({found.Ratio:F2}) ")
                If HealingOffsetMode = 1 AndAlso found.Ratio >= HealingOffsetAcceptRatio Then
                    LastHealingOffsetReport = $"Versatz {report}Verhaeltnis {worstRatio:F2} verworfen"
                    Return False
                End If
            Next
            LastHealingOffsetReport = $"Versatz {report}in {segmentCount} Abschnitten, Verhaeltnis {worstRatio:F2} genommen"

            ' Zurueck ins Bild, mit der sichtbaren Wirkung des Pinsels.
            Dim writer = New PixelWriter(result)
            For maskY = my0 To my1
                Dim y = targetTop + maskY
                Dim ry = y - ry0
                If ry < 0 OrElse ry >= rh Then Continue For
                For mx = mx0 To mx1
                    Dim index = maskY * width + mx
                    If maskAlpha(index) <= 8 OrElse blendAlpha(index) <= 8 Then Continue For
                    Dim x = targetLeft + mx
                    Dim rx = x - rx0
                    If rx < 0 OrElse rx >= rw Then Continue For
                    Dim i = ry * rw + rx
                    If segment(i) < 0 Then Continue For
                    Dim a = Clamp(blendAlpha(index) / 255.0F, 0.0F, 1.0F)
                    Dim targetColor = buffer.GetColor(x, y)
                    writer.SetPixel(x, y, New SKColor(
                        BlendByte(targetColor.Red, ClampByte(CInt(Math.Round(red(i)))), a),
                        BlendByte(targetColor.Green, ClampByte(CInt(Math.Round(green(i)))), a),
                        BlendByte(targetColor.Blue, ClampByte(CInt(Math.Round(blue(i)))), a),
                        BlendByte(targetColor.Alpha, sourceAlpha(i), a)))
                Next
            Next
            Return True
        End Function

        ''' <summary>Ein Abschnitt: Quelle suchen, kopieren, angleichen, in die Ebenen schreiben.
        ''' Punkte spaeterer Abschnitte (<c>segment &gt; current</c>) gelten als unbekannt, frueherer als
        ''' bekannt. Box x0..x1, y0..y1 ist der Abschnitt samt Saum.</summary>
        Private Shared Function HealOffsetSegment(red As Single(), green As Single(), blue As Single(),
                                                  alphaPlane As Byte(), sourceAlpha As Byte(),
                                                  segment As Integer(), current As Integer, dist As Single(),
                                                  rw As Integer, rh As Integer,
                                                  x0 As Integer, y0 As Integer, x1 As Integer, y1 As Integer,
                                                  ringW As Integer, reach As Integer, extent As Integer, f As Integer,
                                                  cRed As Single(), cGreen As Single(), cBlue As Single(), cBad As Boolean(),
                                                  cw As Integer, ch As Integer) As (Ok As Boolean, Dx As Integer, Dy As Integer, Ratio As Double, Reason As String)
            StrokeOffsetDistance(segment, current, dist, rw, x0, y0, x1, y1, ringW)

            ' Saumpunkte mit Gewicht: nahe an der Stelle zaehlen mehr. Unbekannte zaehlen nicht.
            Dim ringIndex As New List(Of Integer)()
            Dim ringWeight As New List(Of Single)()
            Dim cRingSum(cw * ch - 1) As Single
            Dim cRingCount(cw * ch - 1) As Integer
            Dim footSet As New HashSet(Of Integer)()
            For y = y0 To y1
                For x = x0 To x1
                    Dim i = y * rw + x
                    Dim seg = segment(i)
                    Dim cx = x \ f, cy = y \ f
                    Dim inGrid = cx < cw AndAlso cy < ch
                    If seg = current Then
                        If inGrid Then footSet.Add(cy * cw + cx)
                        Continue For
                    End If
                    If seg > current Then Continue For
                    Dim d = dist(i)
                    If d > ringW Then Continue For
                    Dim w = Math.Max(0.05F, 1.0F - (d - 1.0F) / ringW)
                    ringIndex.Add(i)
                    ringWeight.Add(w)
                    If inGrid Then
                        Dim ci = cy * cw + cx
                        footSet.Add(ci)
                        If Not cBad(ci) Then cRingSum(ci) += w : cRingCount(ci) += 1
                    End If
                Next
            Next
            If ringIndex.Count < 16 Then Return (False, 0, 0, 0, "zu wenig Saum")

            ' Was der Versatz mitnehmen muss: Abschnitt und Saum, um einen Block erweitert, damit die
            ' Verfeinerung um bis zu f Punkte nie in die Stelle greift.
            Dim footCells As New HashSet(Of Integer)()
            For Each ci In footSet
                Dim cx = ci Mod cw, cy = ci \ cw
                For oy = -1 To 1
                    For ox = -1 To 1
                        Dim nx = cx + ox, ny = cy + oy
                        If nx < 0 OrElse ny < 0 OrElse nx >= cw OrElse ny >= ch Then Continue For
                        footCells.Add(ny * cw + nx)
                    Next
                Next
            Next
            Dim footList As New List(Of Integer)(footCells)
            Dim cRingWeight(cw * ch - 1) As Single
            Dim ringCells As New List(Of Integer)()
            For ci = 0 To cw * ch - 1
                If cRingCount(ci) = 0 Then Continue For
                cRingWeight(ci) = cRingSum(ci) / cRingCount(ci)
                ringCells.Add(ci)
            Next
            If ringCells.Count < 4 Then Return (False, 0, 0, 0, "zu wenig Saum auf der groben Stufe")

            ' Grobe Suche ueber alle Versaetze.
            Dim reachCells = reach \ f
            Dim top As New List(Of (Dx As Integer, Dy As Integer, Score As Double))()
            Const KeepBest As Integer = 6
            For dy = -reachCells To reachCells
                For dx = -reachCells To reachCells
                    If dx = 0 AndAlso dy = 0 Then Continue For
                    Dim valid = True
                    For Each ci In footList
                        Dim cx = ci Mod cw + dx, cy = ci \ cw + dy
                        If cx < 0 OrElse cy < 0 OrElse cx >= cw OrElse cy >= ch OrElse cBad(cy * cw + cx) Then
                            valid = False
                            Exit For
                        End If
                    Next
                    If Not valid Then Continue For
                    Dim score = StrokeOffsetScore(cRed, cGreen, cBlue, cRingWeight, ringCells, cw, dx, dy)
                    score *= 1.0 + HealingOffsetDistancePenalty * Math.Sqrt(CDbl(dx * dx + dy * dy)) * f / extent
                    InsertStrokeOffsetCandidate(top, (dx, dy, score), KeepBest)
                Next
            Next
            If top.Count = 0 Then Return (False, 0, 0, 0, "keine Quelle ohne Beruehrung der Stelle")

            ' Feine Stufe auf dem Saum selbst, in zwei Schritten: um jeden der besten Kandidaten in
            ' einem Raster von einem Viertel Block, danach um den besten punktgenau. Punkt fuer Punkt
            ' um alle Kandidaten waeren es bei grossen Bloecken tausende Bewertungen des Saums.
            Dim stride = Math.Max(1, CInt(Math.Ceiling(ringIndex.Count / 20000.0)))
            Dim best = (Dx:=0, Dy:=0, Score:=Double.MaxValue)
            Dim parentX = 0, parentY = 0
            Dim gridStep = Math.Max(1, f \ 4)
            For Each candidate In top
                For fy = -f + 1 To f - 1 Step gridStep
                    For fx = -f + 1 To f - 1 Step gridStep
                        Dim dx = candidate.Dx * f + fx, dy = candidate.Dy * f + fy
                        Dim score = StrokeOffsetFineScore(red, green, blue, ringIndex, ringWeight, stride, rw, rh, dx, dy)
                        If Double.IsNaN(score) Then Continue For
                        score *= 1.0 + HealingOffsetDistancePenalty * Math.Sqrt(CDbl(dx * dx + dy * dy)) / extent
                        If score < best.Score Then
                            best = (dx, dy, score)
                            parentX = candidate.Dx : parentY = candidate.Dy
                        End If
                    Next
                Next
            Next
            If best.Score = Double.MaxValue Then Return (False, 0, 0, 0, "keine Quelle im Bild")
            If gridStep > 1 Then
                Dim around = best
                ' Die Verfeinerung bleibt im Block des groben Treffers: nur dort ist sicher, dass die
                ' Quelle die Stelle nicht beruehrt (Abschnitt und Saum sind um einen Block erweitert geprueft).
                For dy = around.Dy - gridStep + 1 To around.Dy + gridStep - 1
                    For dx = around.Dx - gridStep + 1 To around.Dx + gridStep - 1
                        If Math.Abs(dx - parentX * f) >= f OrElse Math.Abs(dy - parentY * f) >= f Then Continue For
                        Dim score = StrokeOffsetFineScore(red, green, blue, ringIndex, ringWeight, stride, rw, rh, dx, dy)
                        If Double.IsNaN(score) Then Continue For
                        score *= 1.0 + HealingOffsetDistancePenalty * Math.Sqrt(CDbl(dx * dx + dy * dy)) / extent
                        If score < best.Score Then best = (dx, dy, score)
                    Next
                Next
            End If

            ' Saumfehler ohne Aufschlag im Verhaeltnis zur Struktur des Saums.
            Dim pureError = StrokeOffsetFineScore(red, green, blue, ringIndex, ringWeight, stride, rw, rh, best.Dx, best.Dy)
            Dim ringStructure = StrokeOffsetRingVariance(red, green, blue, ringIndex, ringWeight, stride)
            Dim ratio = If(ringStructure <= 1.0, pureError, pureError / ringStructure)
            If HealingOffsetMode = 1 AndAlso ratio >= HealingOffsetAcceptRatio Then Return (True, best.Dx, best.Dy, ratio, "")

            ' Kopieren, angleichen, in die Ebenen schreiben.
            Dim offset = best.Dy * rw + best.Dx
            StrokeOffsetCopyAndMatch(red, green, blue, alphaPlane, sourceAlpha, segment, current, dist,
                                     rw, x0, y0, x1, y1, ringW, offset)
            Return (True, best.Dx, best.Dy, ratio, "")
        End Function

        ''' <summary>Entfernung zum Abschnitt <paramref name="current"/>, Fasenmass 3-4 durch 3, nur im
        ''' Ausschnitt x0..x1, y0..y1 (ausserhalb bleibt das Feld unberuehrt und wird nicht gelesen).</summary>
        Private Shared Sub StrokeOffsetDistance(segment As Integer(), current As Integer, d As Single(), rw As Integer,
                                                x0 As Integer, y0 As Integer, x1 As Integer, y1 As Integer,
                                                limit As Integer)
            Dim far = CSng(limit + 10) * 3.0F
            For y = y0 To y1
                For x = x0 To x1
                    Dim i = y * rw + x
                    d(i) = If(segment(i) = current, 0.0F, far)
                Next
            Next
            For y = y0 To y1
                For x = x0 To x1
                    Dim i = y * rw + x
                    If segment(i) = current Then Continue For
                    Dim v = d(i)
                    If x > x0 Then v = Math.Min(v, d(i - 1) + 3.0F)
                    If y > y0 Then
                        v = Math.Min(v, d(i - rw) + 3.0F)
                        If x > x0 Then v = Math.Min(v, d(i - rw - 1) + 4.0F)
                        If x < x1 Then v = Math.Min(v, d(i - rw + 1) + 4.0F)
                    End If
                    d(i) = v
                Next
            Next
            For y = y1 To y0 Step -1
                For x = x1 To x0 Step -1
                    Dim i = y * rw + x
                    If segment(i) = current Then Continue For
                    Dim v = d(i)
                    If x < x1 Then v = Math.Min(v, d(i + 1) + 3.0F)
                    If y < y1 Then
                        v = Math.Min(v, d(i + rw) + 3.0F)
                        If x < x1 Then v = Math.Min(v, d(i + rw + 1) + 4.0F)
                        If x > x0 Then v = Math.Min(v, d(i + rw - 1) + 4.0F)
                    End If
                    d(i) = v
                Next
            Next
            For y = y0 To y1
                For x = x0 To x1
                    d(y * rw + x) /= 3.0F
                Next
            Next
        End Sub

        ''' <summary>Strukturfehler eines Versatzes auf der groben Stufe: gewichtete Abweichung von
        ''' Ziel minus Quelle um ihr eigenes Mittel, also ohne den Unterschied im Grundton.</summary>
        Private Shared Function StrokeOffsetScore(r As Single(), g As Single(), b As Single(), weight As Single(),
                                                  cells As List(Of Integer), cw As Integer,
                                                  dx As Integer, dy As Integer) As Double
            Dim shift = dy * cw + dx
            Dim sw = 0.0, mr = 0.0, mg = 0.0, mb = 0.0
            For Each ci In cells
                Dim w = CDbl(weight(ci))
                Dim si = ci + shift
                mr += w * (r(ci) - r(si)) : mg += w * (g(ci) - g(si)) : mb += w * (b(ci) - b(si))
                sw += w
            Next
            mr /= sw : mg /= sw : mb /= sw
            Dim e = 0.0
            For Each ci In cells
                Dim si = ci + shift
                Dim er = r(ci) - r(si) - mr, eg = g(ci) - g(si) - mg, eb = b(ci) - b(si) - mb
                e += weight(ci) * (er * er + eg * eg + eb * eb)
            Next
            Return e / sw
        End Function

        ''' <summary>Dasselbe auf den Saumpunkten selbst. NaN, wenn eine Quelle ausserhalb liegt.</summary>
        Private Shared Function StrokeOffsetFineScore(r As Single(), g As Single(), b As Single(),
                                                      ring As List(Of Integer), weight As List(Of Single), stride As Integer,
                                                      rw As Integer, rh As Integer, dx As Integer, dy As Integer) As Double
            Dim shift = dy * rw + dx
            Dim sw = 0.0, mr = 0.0, mg = 0.0, mb = 0.0
            For k = 0 To ring.Count - 1 Step stride
                Dim i = ring(k)
                Dim x = i Mod rw + dx, y = i \ rw + dy
                If x < 0 OrElse y < 0 OrElse x >= rw OrElse y >= rh Then Return Double.NaN
                Dim w = CDbl(weight(k))
                Dim si = i + shift
                mr += w * (r(i) - r(si)) : mg += w * (g(i) - g(si)) : mb += w * (b(i) - b(si))
                sw += w
            Next
            If sw <= 0 Then Return Double.NaN
            mr /= sw : mg /= sw : mb /= sw
            Dim e = 0.0
            For k = 0 To ring.Count - 1 Step stride
                Dim i = ring(k)
                Dim si = i + shift
                Dim er = r(i) - r(si) - mr, eg = g(i) - g(si) - mg, eb = b(i) - b(si) - mb
                e += weight(k) * (er * er + eg * eg + eb * eb)
            Next
            Return e / sw
        End Function

        ''' <summary>Gewichtete Streuung des Saums um sein Mittel: wie viel Struktur er traegt.</summary>
        Private Shared Function StrokeOffsetRingVariance(r As Single(), g As Single(), b As Single(),
                                                         ring As List(Of Integer), weight As List(Of Single),
                                                         stride As Integer) As Double
            Dim sw = 0.0, mr = 0.0, mg = 0.0, mb = 0.0
            For k = 0 To ring.Count - 1 Step stride
                Dim i = ring(k)
                Dim w = CDbl(weight(k))
                mr += w * r(i) : mg += w * g(i) : mb += w * b(i) : sw += w
            Next
            If sw <= 0 Then Return 0
            mr /= sw : mg /= sw : mb /= sw
            Dim e = 0.0
            For k = 0 To ring.Count - 1 Step stride
                Dim i = ring(k)
                Dim er = r(i) - mr, eg = g(i) - mg, eb = b(i) - mb
                e += weight(k) * (er * er + eg * eg + eb * eb)
            Next
            Return e / sw
        End Function

        Private Shared Function MedianOf(values As List(Of Single)) As Single
            values.Sort()
            Dim mid = values.Count \ 2
            If values.Count Mod 2 = 1 Then Return values(mid)
            Return (values(mid - 1) + values(mid)) * 0.5F
        End Function

        ''' <summary>Mittlere absolute Abweichung vom Median.</summary>
        Private Shared Function MedianDeviation(values As List(Of Single), median As Single) As Single
            Dim s = 0.0F
            For Each v In values
                s += Math.Abs(v - median)
            Next
            Return s / values.Count
        End Function

        Private Shared Sub InsertStrokeOffsetCandidate(list As List(Of (Dx As Integer, Dy As Integer, Score As Double)),
                                                       candidate As (Dx As Integer, Dy As Integer, Score As Double),
                                                       keep As Integer)
            ' Nachbarn eines besseren Kandidaten verdraengen ihn nicht, sie ersetzen ihn nur.
            For k = 0 To list.Count - 1
                If Math.Abs(list(k).Dx - candidate.Dx) <= 1 AndAlso Math.Abs(list(k).Dy - candidate.Dy) <= 1 Then
                    If candidate.Score < list(k).Score Then list(k) = candidate
                    Return
                End If
            Next
            If list.Count < keep Then
                list.Add(candidate)
            Else
                Dim worst = 0
                For k = 1 To list.Count - 1
                    If list(k).Score > list(worst).Score Then worst = k
                Next
                If candidate.Score >= list(worst).Score Then Return
                list(worst) = candidate
            End If
        End Sub

        ''' <summary>Kopiert die Quelle in den Abschnitt und gleicht sie an die Umgebung an: der
        ''' Unterschied Ziel minus Quelle wird an den bekannten Punkten am Rand gemessen und als glatte
        ''' Flaeche hineingezogen, zuerst auf einem groben Raster, danach punktgenau am Rand
        ''' nachgezogen. Die Struktur der Quelle bleibt, der Grundton kommt aus der Umgebung. Das
        ''' Ergebnis steht danach in r, g, b; die Deckung der Quelle in sourceAlpha.</summary>
        Private Shared Sub StrokeOffsetCopyAndMatch(r As Single(), g As Single(), b As Single(),
                                                    alphaPlane As Byte(), sourceAlpha As Byte(),
                                                    segment As Integer(), current As Integer, dist As Single(), rw As Integer,
                                                    x0 As Integer, y0 As Integer, x1 As Integer, y1 As Integer,
                                                    ringW As Integer, offset As Integer)
            Dim bw = x1 - x0 + 1, bh = y1 - y0 + 1
            Dim cell = Math.Max(2, ringW \ 3)
            Dim gw = (bw + cell - 1) \ cell, gh = (bh + cell - 1) \ cell
            Dim gn = gw * gh
            Dim dr(gn - 1) As Single, dg(gn - 1) As Single, db(gn - 1) As Single
            Dim fixedCell(gn - 1) As Boolean
            Dim sumR = 0.0, sumG = 0.0, sumB = 0.0, sumCount = 0
            ' Der MEDIAN je Zelle, nicht das Mittel. Trifft die Quelle eine Kante um wenige Punkte
            ' versetzt (Baumlinie gegen Himmel), ist der Unterschied dort riesig, und als Mittel
            ' truege die Membran ihn als hellen Schein in die Flaeche (gemessen am Pfahl ueber einer
            ' Baumlinie). Zellen, in denen der Unterschied stark streut, liegen auf so einer Kante und
            ' bleiben frei; die Membran rechnet sie aus den Nachbarn. Zellen mit Punkten spaeterer
            ' Abschnitte bleiben ebenfalls frei: dort ist der Rand noch nicht bekannt.
            Dim cellR As New List(Of Single)(cell * cell), cellG As New List(Of Single)(cell * cell), cellB As New List(Of Single)(cell * cell)
            For gy = 0 To gh - 1
                For gx = 0 To gw - 1
                    Dim open = False
                    cellR.Clear() : cellG.Clear() : cellB.Clear()
                    For y = y0 + gy * cell To Math.Min(y1, y0 + gy * cell + cell - 1)
                        For x = x0 + gx * cell To Math.Min(x1, x0 + gx * cell + cell - 1)
                            Dim i = y * rw + x
                            If segment(i) >= current Then open = True : Continue For
                            If dist(i) > ringW Then Continue For
                            Dim si = i + offset
                            cellR.Add(r(i) - r(si)) : cellG.Add(g(i) - g(si)) : cellB.Add(b(i) - b(si))
                        Next
                    Next
                    Dim gi = gy * gw + gx
                    If open OrElse cellR.Count = 0 Then Continue For
                    Dim mr = MedianOf(cellR), mg = MedianOf(cellG), mb = MedianOf(cellB)
                    Dim spread = Math.Max(MedianDeviation(cellR, mr), Math.Max(MedianDeviation(cellG, mg), MedianDeviation(cellB, mb)))
                    If spread > HealingOffsetCellSpread Then Continue For
                    dr(gi) = mr : dg(gi) = mg : db(gi) = mb
                    fixedCell(gi) = True
                    sumR += dr(gi) : sumG += dg(gi) : sumB += db(gi) : sumCount += 1
                Next
            Next
            If sumCount > 0 Then
                For gi = 0 To gn - 1
                    If fixedCell(gi) Then Continue For
                    dr(gi) = CSng(sumR / sumCount) : dg(gi) = CSng(sumG / sumCount) : db(gi) = CSng(sumB / sumCount)
                Next
                SolveHealingMembrane(dr, dg, db, fixedCell, gw, gh)
            End If

            ' Kopie plus grobe Flaeche; am bekannten Rand der Rest, der punktgenau eingespannt wird.
            ' Lokale Ebenen im Ausschnitt, Index (y - y0) * bw + (x - x0).
            Dim hr(bw * bh - 1) As Single, hg(bw * bh - 1) As Single, hb(bw * bh - 1) As Single
            Dim restR(bw * bh - 1) As Single, restG(bw * bh - 1) As Single, restB(bw * bh - 1) As Single
            For y = y0 To y1
                Dim fy = (y - y0 + 0.5F) / cell - 0.5F
                Dim gy0 = Math.Clamp(CInt(Math.Floor(fy)), 0, gh - 1)
                Dim gy1 = Math.Min(gh - 1, gy0 + 1)
                Dim ty = Math.Clamp(fy - gy0, 0.0F, 1.0F)
                For x = x0 To x1
                    Dim fx = (x - x0 + 0.5F) / cell - 0.5F
                    Dim gx0 = Math.Clamp(CInt(Math.Floor(fx)), 0, gw - 1)
                    Dim gx1 = Math.Min(gw - 1, gx0 + 1)
                    Dim tx = Math.Clamp(fx - gx0, 0.0F, 1.0F)
                    Dim i00 = gy0 * gw + gx0, i01 = gy0 * gw + gx1, i10 = gy1 * gw + gx0, i11 = gy1 * gw + gx1
                    Dim smoothR = If(sumCount > 0, Bilinear(dr, i00, i01, i10, i11, tx, ty), 0.0F)
                    Dim smoothG = If(sumCount > 0, Bilinear(dg, i00, i01, i10, i11, tx, ty), 0.0F)
                    Dim smoothB = If(sumCount > 0, Bilinear(db, i00, i01, i10, i11, tx, ty), 0.0F)
                    Dim i = y * rw + x
                    Dim li = (y - y0) * bw + (x - x0)
                    Dim si = i + offset
                    Dim seg = segment(i)
                    If seg = current Then
                        hr(li) = r(si) + smoothR : hg(li) = g(si) + smoothG : hb(li) = b(si) + smoothB
                    ElseIf seg < current AndAlso dist(i) <= 1.5F Then
                        ' Rest am Rand: was die grobe Flaeche dort noch verfehlt. Ein grosser Rest ist
                        ' eine Kante, die die Quelle nicht genau trifft; ihn hineinzuziehen hiesse, die
                        ' Kante als Schleier in die Stelle zu malen. Er laeuft deshalb aus.
                        Dim er = r(i) - (r(si) + smoothR), eg = g(i) - (g(si) + smoothG), eb = b(i) - (b(si) + smoothB)
                        Dim largest = Math.Max(Math.Abs(er), Math.Max(Math.Abs(eg), Math.Abs(eb)))
                        Dim keep = Math.Clamp((HealingOffsetRestNoneAbove - largest) / (HealingOffsetRestNoneAbove - HealingOffsetRestFullBelow), 0.0F, 1.0F)
                        restR(li) = er * keep : restG(li) = eg * keep : restB(li) = eb * keep
                    End If
                Next
            Next

            ' Den Rest von den Randpunkten aus in den Abschnitt ziehen; nach wenigen Punkten ist er weg.
            Const Iterations As Integer = 40
            Const Relaxation As Single = 1.8F
            For iteration = 1 To Iterations
                For ly = 1 To bh - 2
                    For lx = 1 To bw - 2
                        If segment((ly + y0) * rw + lx + x0) <> current Then Continue For
                        Dim li = ly * bw + lx
                        restR(li) += ((restR(li - 1) + restR(li + 1) + restR(li - bw) + restR(li + bw)) * 0.25F - restR(li)) * Relaxation
                        restG(li) += ((restG(li - 1) + restG(li + 1) + restG(li - bw) + restG(li + bw)) * 0.25F - restG(li)) * Relaxation
                        restB(li) += ((restB(li - 1) + restB(li + 1) + restB(li - bw) + restB(li + bw)) * 0.25F - restB(li)) * Relaxation
                    Next
                Next
            Next
            For y = y0 To y1
                For x = x0 To x1
                    Dim i = y * rw + x
                    If segment(i) <> current Then Continue For
                    Dim li = (y - y0) * bw + (x - x0)
                    r(i) = hr(li) + restR(li) : g(i) = hg(li) + restG(li) : b(i) = hb(li) + restB(li)
                    sourceAlpha(i) = alphaPlane(i + offset)
                Next
            Next
        End Sub

    End Class

End Namespace
