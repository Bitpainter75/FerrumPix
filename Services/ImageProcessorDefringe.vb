Imports System
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' FARBSAUM ENTFERNEN: der Laengsfehler des Objektivs, die violetten Saeume an harten, hellen
    ''' Kanten. Anders als der Querfehler (Farbsaeume an den Bildecken, im Decode ueber das
    ''' Objektivprofil) laesst er sich nicht zurueckschieben: Rot und Blau liegen nicht versetzt,
    ''' sondern unscharf. Also wird an solchen Stellen die Farbe herausgenommen, die Helligkeit
    ''' bleibt.
    '''
    ''' DAS VERFAHREN. Rot und Blau werden in einem Gaussfenster um jeden Bildpunkt je als
    ''' a*G + c*LoG(G) + b an Gruen angepasst (LoG: der Laplace des geglaetteten Gruen). Ein Saum ist
    ''' genau das, was der Laplace-Term erklaert: Farbe, die an der Kruemmung der Helligkeit haengt.
    ''' Wo dieser Term deutlich ist (t-Wert des Beiwerts c gegen seinen Fehler), wird die Farbe des
    ''' Bildpunkts auf den Anteil entsaettigt, den die Anpassung OHNE den Laplace-Term behaelt. Nur
    ''' Farbe wird weggenommen, nie hinzugefuegt; Gruen bleibt unberuehrt, und warme Toene (Rot,
    ''' Orange, Gelb) bleiben ganz aus, weil sie an Kanten fast immer Motiv sind.
    '''
    ''' WO. Eine eigene Stufe am Anfang der Pixelkette, VOR der Geometrie und vor allen Ton- und
    ''' Farbstufen, auf Linearlicht aus den sRGB-Werten. Vor der Geometrie, weil die Fenster dort in
    ''' Punkten der Quelle stehen: ein Zuschnitt oder eine Groessenaenderung der Ausgabe aendert an
    ''' den Fenstern nichts. Fuer alle Formate, nicht im RAW-Dekoder.
    '''
    ''' MASSSTAB. Die Fensterradien beziehen sich auf Punkte der VOLLEN Quelle
    ''' (ImageAdjustments.SourceWidthPixels). Die Vorschau und die verkleinerte Quelle beim Reglerzug
    ''' rechnen auf weniger Punkten; dort schrumpfen die Radien im selben Verhaeltnis, bis zu den
    ''' Untergrenzen (Fenster 0,75, Laplace 0,3), und die effektive Probenzahl des t-Werts waechst
    ''' mit dem Quadrat des Faktors, wie im Prototyp mit seinem Massstab.
    '''
    ''' Die Rechnung ist eine treue Uebertragung des Prototyps
    ''' (packaging/Diagnostics/Farbsaummessung/farbsaum.py), mit der Sicherheitsschwelle t von 1 bis 2.
    ''' Der Pruefstand rechnet ein Testbild durch beide und vergleicht.
    ''' </summary>
    Partial Public Class ImageProcessor

        ''' <summary>Sigma des Anpassungsfensters in Punkten der vollen Quelle.</summary>
        Private Const DefringeWindowSigma As Double = 3.5
        ''' <summary>Sigma der Glaettung vor dem Laplace.</summary>
        Private Const DefringeLaplacianSigma As Double = 1.5
        ''' <summary>Boden der Gruenvarianz als Anteil der mittleren Gruenhelligkeit: ohne ihn
        ''' liesse sich auf einer flachen Flaeche jede Farbe aus dem Rauschen "erklaeren".</summary>
        Private Const DefringeGreenFloorShare As Double = 0.02
        ''' <summary>Dunkelboden der Farbverhaeltnisse in Linearlicht.</summary>
        Private Const DefringeDarkFloor As Double = 0.002
        ''' <summary>Gratregularisierung des Laplace-Terms, relativ zum Gruenboden.</summary>
        Private Const DefringeLaplacianRidge As Double = 0.0001
        ''' <summary>Sicherheitsschwelle: ab diesem t-Wert setzt die Wirkung ein ...</summary>
        Private Const DefringeSignificanceStart As Double = 1.0
        ''' <summary>... und ab diesem wirkt sie voll.</summary>
        Private Const DefringeSignificanceFull As Double = 2.0
        ''' <summary>Farbtonbereich der warmen Toene in Grad (Winkel aus log(B/G) gegen log(R/G)),
        ''' mit weichen Raendern der Breite DefringeWarmRamp.</summary>
        Private Const DefringeWarmStart As Double = -105.0
        Private Const DefringeWarmEnd As Double = 15.0
        Private Const DefringeWarmRamp As Double = 20.0
        ''' <summary>Zeilen je Band. Jedes Band liest dazu oben und unten seinen Rand (Fenster,
        ''' Laplace und Glaettung, hoechstens 13 Zeilen) und rechnet unabhaengig von den anderen.</summary>
        Private Const DefringeBandRows As Integer = 48

        ''' <summary>sRGB-Byte nach Linearlicht, wie lade_linear im Prototyp.</summary>
        Private Shared ReadOnly DefringeByteToLinear As New Lazy(Of Single())(
            Function()
                Dim table = New Single(255) {}
                For v = 0 To 255
                    table(v) = CSng(SrgbToLinear(v / 255.0))
                Next
                Return table
            End Function)

        ''' <summary>Dasselbe fuer die 16 Bit des tiefen Bilds.</summary>
        Private Shared ReadOnly DefringeDeepToLinear As New Lazy(Of Single())(
            Function()
                Dim table = New Single(65535) {}
                For v = 0 To 65535
                    table(v) = CSng(SrgbToLinear(v / 65535.0))
                Next
                Return table
            End Function)

        ''' <summary>Die Stufe in der Kette: tut ohne Schalter nichts und gibt die Quelle unveraendert
        ''' zurueck, ein ausgeschalteter Schalter ist also bitgleich mit dem Stand ohne die Stufe. Mit
        ''' tiefem Bild (Ausgabe in 16 Bit) rechnet sie das tiefe Bild und leitet das 8-Bit-Bild daraus ab.</summary>
        Private Shared Function ApplyDefringeStageOwned(processed As SKBitmap, adj As ImageAdjustments,
                                                        ByRef owned As Boolean) As SKBitmap
            If processed Is Nothing OrElse adj Is Nothing OrElse Not adj.DefringePurple Then Return processed
            Dim input = processed
            Dim scale = DefringeScaleFor(input, adj)
            Dim fromDeep = RunDeepStage(input, "Pixel: Farbsaum", Function(d) ApplyPurpleDefringeDeep(d, scale))
            If fromDeep IsNot Nothing Then Return ReplaceBitmapOwned(processed, fromDeep, owned)
            Return ReplaceBitmapOwned(processed, PerformanceTraceService.Measure(
                "Pixel: Farbsaum", Function() ApplyPurpleDefringe(input, scale)), owned)
        End Function

        ''' <summary>Verhaeltnis der Quelle, auf der gerechnet wird, zur vollen Quelle des Rezepts.
        ''' Ohne Bezugsgroesse 1: dann gelten die Fenster in Punkten des uebergebenen Bilds.</summary>
        Friend Shared Function DefringeScaleFor(source As SKBitmap, adj As ImageAdjustments) As Double
            If source Is Nothing OrElse adj Is Nothing OrElse adj.SourceWidthPixels <= 0 OrElse source.Width <= 0 Then Return 1.0
            Return Math.Min(1.0, source.Width / CDbl(adj.SourceWidthPixels))
        End Function

        ''' <summary>Farbsaum entfernen auf einem 8-Bit-Bild (BGRA oder RGBA). Nothing, wenn kein
        ''' Bildpunkt sich aendert oder der Farbtyp nicht passt; sonst ein neues Bild. Bildpunkte ohne
        ''' Wirkung behalten ihre Bytes.</summary>
        ''' <param name="scale">Massstab gegen die volle Quelle, 1 in voller Aufloesung.</param>
        Friend Shared Function ApplyPurpleDefringe(source As SKBitmap, scale As Double) As SKBitmap
            If source Is Nothing OrElse source.Width < 2 OrElse source.Height < 2 Then Return Nothing
            Dim srcBuf As Byte() = Nothing
            Dim dstBuf As Byte() = Nothing
            Dim length, stride, ri, gi, bi, ai As Integer
            Try
                If Not TryRentRgbaLikeBuffer(source, srcBuf, length, stride, ri, gi, bi, ai) Then Return Nothing
                dstBuf = PlanePool(Of Byte).Rent(length)
                Buffer.BlockCopy(srcBuf, 0, dstBuf, 0, length)
                Dim src = srcBuf, dst = dstBuf
                Dim rowStride = stride
                Dim rIndex = ri, gIndex = gi, bIndex = bi, aIndex = ai
                Dim w = source.Width
                Dim toLinear = DefringeByteToLinear.Value
                Dim changedRows = 0

                Dim load As Action(Of Integer, Single(), Single(), Single(), Integer) =
                    Sub(y As Integer, rr As Single(), gg As Single(), bb As Single(), offset As Integer)
                               Dim rowStart = y * rowStride
                               For x = 0 To w - 1
                                   Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                                   ReadUnpremultiplied(src, rowStart + x * 4, rIndex, gIndex, bIndex, aIndex, cr, cg, cb, a)
                                   rr(offset + x) = toLinear(cr)
                                   gg(offset + x) = toLinear(cg)
                                   bb(offset + x) = toLinear(cb)
                               Next
                           End Sub
                Dim store As Action(Of Integer, Single(), Single(), Boolean()) =
                    Sub(y As Integer, newR As Single(), newB As Single(), changed As Boolean())
                                Dim rowStart = y * rowStride
                                Dim any = False
                                For x = 0 To w - 1
                                    If Not changed(x) Then Continue For
                                    Dim cr As Integer, cg As Integer, cb As Integer, a As Integer
                                    Dim o = rowStart + x * 4
                                    ReadUnpremultiplied(src, o, rIndex, gIndex, bIndex, aIndex, cr, cg, cb, a)
                                    Dim nr = DefringeEncodeByte(newR(x))
                                    Dim nb = DefringeEncodeByte(newB(x))
                                    If nr = cr AndAlso nb = cb Then Continue For
                                    WritePremultiplied(dst, o, rIndex, gIndex, bIndex, aIndex, nr, CByte(cg), nb, a)
                                    any = True
                                Next
                                If any Then Interlocked.Increment(changedRows)
                            End Sub

                DefringeBands(w, source.Height, scale, load, store)
                If Volatile.Read(changedRows) = 0 Then Return Nothing

                Dim result = New SKBitmap(New SKImageInfo(w, source.Height, source.ColorType, source.AlphaType))
                Marshal.Copy(dstBuf, 0, result.GetPixels(), length)
                Return result
            Finally
                ReturnPooledBuffer(srcBuf)
                ReturnPooledBuffer(dstBuf)
            End Try
        End Function

        ''' <summary>Dieselbe Stufe auf dem tiefen Bild (16 Bit je Kanal). Liefert immer ein neues
        ''' tiefes Bild, auch wenn sich nichts aendert: ein Nothing liesse die 8-Bit-Stufe laufen, und
        ''' deren Ergebnis haette kein tiefes Bild mehr.</summary>
        Private Shared Function ApplyPurpleDefringeDeep(deep As SKBitmap, scale As Double) As SKBitmap
            Dim planes = DeepPlanes.Load(deep)
            If planes Is Nothing OrElse planes.Width < 2 OrElse planes.Height < 2 Then Return Nothing
            Dim w = planes.Width
            Dim toLinear = DefringeDeepToLinear.Value
            Dim inR = planes.R, inG = planes.G, inB = planes.B
            ' Die Ausgabe in eigene Ebenen: die Baender lesen ihre Randzeilen aus den Eingangsebenen,
            ' die ein Nachbarband sonst schon ueberschrieben haette.
            Dim outR = CType(inR.Clone(), Single())
            Dim outB = CType(inB.Clone(), Single())
            Dim linearOf = Function(byteUnits As Single) toLinear(Math.Min(65535, Math.Max(0, CInt(byteUnits * 257.0F + 0.5F))))

            Dim load As Action(Of Integer, Single(), Single(), Single(), Integer) =
                Sub(y As Integer, rr As Single(), gg As Single(), bb As Single(), offset As Integer)
                           Dim at = y * w
                           For x = 0 To w - 1
                               rr(offset + x) = linearOf(inR(at + x))
                               gg(offset + x) = linearOf(inG(at + x))
                               bb(offset + x) = linearOf(inB(at + x))
                           Next
                       End Sub
            Dim store As Action(Of Integer, Single(), Single(), Boolean()) =
                Sub(y As Integer, newR As Single(), newB As Single(), changed As Boolean())
                            Dim at = y * w
                            For x = 0 To w - 1
                                If Not changed(x) Then Continue For
                                outR(at + x) = CSng(LinearToSrgb(CDbl(newR(x))) * 255.0)
                                outB(at + x) = CSng(LinearToSrgb(CDbl(newB(x))) * 255.0)
                            Next
                        End Sub

            DefringeBands(w, planes.Height, scale, load, store)
            planes.R = outR
            planes.B = outB
            Return planes.ToBitmap()
        End Function

        ''' <summary>Linearlicht nach sRGB-Byte, wie kodiere im Prototyp (begrenzt, gerundet).</summary>
        Private Shared Function DefringeEncodeByte(linear As Single) As Byte
            Dim v = Math.Floor(LinearToSrgb(CDbl(linear)) * 255.0 + 0.5)
            If v <= 0.0 Then Return 0
            If v >= 255.0 Then Return 255
            Return CByte(v)
        End Function

        ''' <summary>Normierter Gausskern mit 2*radius+1 Gewichten (Single, wie im Prototyp), dazu die
        ''' Summe der ungenormten Gewichte und die ihrer Quadrate fuer die effektive Probenzahl.</summary>
        Private Shared Function DefringeGaussKernel(sigma As Double, radius As Integer,
                                                    ByRef rawSum As Double, ByRef rawSquareSum As Double) As Single()
            Dim raw = New Double(2 * radius) {}
            rawSum = 0.0
            rawSquareSum = 0.0
            For i = 0 To 2 * radius
                Dim x = CDbl(i - radius)
                raw(i) = Math.Exp(-0.5 * x * x / (sigma * sigma))
                rawSum += raw(i)
                rawSquareSum += raw(i) * raw(i)
            Next
            Dim kernel = New Single(2 * radius) {}
            For i = 0 To 2 * radius
                kernel(i) = CSng(raw(i) / rawSum)
            Next
            Return kernel
        End Function

        Private Shared Function DefringeSmoothstep(edge0 As Double, edge1 As Double, x As Double) As Double
            Dim t = (x - edge0) / (edge1 - edge0)
            If t <= 0.0 Then Return 0.0
            If t >= 1.0 Then Return 1.0
            Return t * t * (3.0 - 2.0 * t)
        End Function

        ''' <summary>Der Kern, unabhaengig von der Bildform. <paramref name="load"/> fuellt fuer eine
        ''' Zeile y die Ebenen R, G und B in Linearlicht ab einem Versatz; <paramref name="store"/>
        ''' bekommt je Zeile das neue Rot und Blau in Linearlicht und je Bildpunkt, ob er sich aendert.
        ''' Gruen bleibt immer, wie es ist.
        '''
        ''' Gerechnet wird in Baendern von DefringeBandRows Zeilen, parallel. Jedes Band laedt seine
        ''' Zeilen samt Rand und rechnet jede Zwischenstufe nur fuer Zeilen im Bild; am Bildrand wird
        ''' die Zeile wiederholt wie im Prototyp (np.pad mit "edge"), und zwar auf jeder Stufe fuer
        ''' sich. Das Ergebnis haengt damit nicht an der Bandaufteilung.</summary>
        Private Shared Sub DefringeBands(width As Integer, height As Integer, scale As Double,
                                         load As Action(Of Integer, Single(), Single(), Single(), Integer),
                                         store As Action(Of Integer, Single(), Single(), Boolean()))
            If width < 1 OrElse height < 1 Then Return
            Dim s = If(scale > 0.0 AndAlso Not Double.IsNaN(scale), Math.Min(1.0, scale), 1.0)

            ' Der Laplace des geglaetteten Gruen (laplace_von_gruen).
            Dim sigmaL = Math.Max(DefringeLaplacianSigma * s, 0.3)
            Dim taps = CInt(Math.Min(Math.Max(Math.Ceiling(3.0 * sigmaL), 1.0), 5.0))
            Dim unusedSum As Double, unusedSquares As Double
            Dim kernelL = DefringeGaussKernel(sigmaL, taps, unusedSum, unusedSquares)

            ' Das Anpassungsfenster und seine effektive Probenzahl (entferne_saeume, anpassen).
            Dim sigmaW = Math.Max(DefringeWindowSigma * s, 0.75)
            Dim radius = CInt(Math.Min(Math.Max(Math.Ceiling(2.0 * sigmaW), 1.0), 7.0))
            Dim rawSum As Double, rawSquareSum As Double
            Dim kernelW = DefringeGaussKernel(sigmaW, radius, rawSum, rawSquareSum)
            ' Das 2D-Fenster ist das aeussere Produkt: Summe = rawSum^2, Quadratsumme = rawSquareSum^2.
            Dim weightSum2d = rawSum * rawSum
            Dim weightSquareSum2d = rawSquareSum * rawSquareSum
            Dim samples = Math.Max((weightSum2d * weightSum2d / Math.Max(weightSquareSum2d, 0.000000000001)) / (s * s), 1.0)

            Dim margin = radius + 1 + taps
            Dim bandCount = (height + DefringeBandRows - 1) \ DefringeBandRows
            Dim lastRow = height - 1
            Dim lastColumn = width - 1

            Parallel.For(0, bandCount,
                Sub(band)
                    Dim y0 = band * DefringeBandRows
                    Dim y1 = Math.Min(height, y0 + DefringeBandRows)
                    Dim baseRow = Math.Max(0, y0 - margin)
                    Dim topRow = Math.Min(height, y1 + margin)
                    Dim count = (topRow - baseRow) * width
                    Dim planeWidth = width + 2 * radius
                    Dim r = PlanePool(Of Single).Rent(count)
                    Dim g = PlanePool(Of Single).Rent(count)
                    Dim b = PlanePool(Of Single).Rent(count)
                    Dim smooth = PlanePool(Of Single).Rent(count)
                    Dim lap = PlanePool(Of Single).Rent(count)
                    Dim padded = PlanePool(Of Single).Rent(width + 2 * taps)
                    Dim acc = PlanePool(Of Single).Rent(13 * planeWidth)
                    Dim newR = New Single(width - 1) {}
                    Dim newB = New Single(width - 1) {}
                    Dim changed = New Boolean(width - 1) {}
                    Try
                        For yy = baseRow To topRow - 1
                            load(yy, r, g, b, (yy - baseRow) * width)
                        Next

                        ' Glaetten: erst senkrecht, dann waagerecht, Rand wiederholt (falte).
                        Dim s0 = Math.Max(0, y0 - radius - 1)
                        Dim s1 = Math.Min(height, y1 + radius + 1)
                        For yy = s0 To s1 - 1
                            Array.Clear(padded, 0, width + 2 * taps)
                            For j = 0 To 2 * taps
                                Dim src = (Math.Min(lastRow, Math.Max(0, yy + j - taps)) - baseRow) * width
                                Dim k = kernelL(j)
                                For x = 0 To lastColumn
                                    padded(taps + x) += k * g(src + x)
                                Next
                            Next
                            For i = 0 To taps - 1
                                padded(i) = padded(taps)
                                padded(taps + width + i) = padded(taps + lastColumn)
                            Next
                            Dim o = (yy - baseRow) * width
                            For x = 0 To lastColumn
                                Dim sum = 0.0F
                                For i = 0 To 2 * taps
                                    sum += kernelL(i) * padded(x + i)
                                Next
                                smooth(o + x) = sum
                            Next
                        Next

                        ' Laplace mit den vier Nachbarn, Rand wiederholt.
                        Dim l0 = Math.Max(0, y0 - radius)
                        Dim l1 = Math.Min(height, y1 + radius)
                        For yy = l0 To l1 - 1
                            Dim o = (yy - baseRow) * width
                            Dim up = (Math.Max(0, yy - 1) - baseRow) * width
                            Dim down = (Math.Min(lastRow, yy + 1) - baseRow) * width
                            For x = 0 To lastColumn
                                Dim left = smooth(o + Math.Max(0, x - 1))
                                Dim right = smooth(o + Math.Min(lastColumn, x + 1))
                                lap(o + x) = left + right + smooth(up + x) + smooth(down + x) - 4.0F * smooth(o + x)
                            Next
                        Next

                        ' Fenstermomente je Ausgabezeile: senkrecht in die Ebenen von acc (13 Groessen
                        ' nebeneinander, je mit Rand fuer den waagerechten Durchgang), dann waagerecht
                        ' und gleich die Anpassung je Bildpunkt.
                        For y = y0 To y1 - 1
                            Array.Clear(acc, 0, 13 * planeWidth)
                            For j = 0 To 2 * radius
                                Dim src = (Math.Min(lastRow, Math.Max(0, y + j - radius)) - baseRow) * width
                                Dim k = kernelW(j)
                                For x = 0 To lastColumn
                                    Dim gv = g(src + x), lv = lap(src + x), rv = r(src + x), bv = b(src + x)
                                    Dim p = radius + x
                                    acc(p) += k * gv
                                    acc(planeWidth + p) += k * lv
                                    acc(2 * planeWidth + p) += k * rv
                                    acc(3 * planeWidth + p) += k * bv
                                    acc(4 * planeWidth + p) += k * (gv * gv)
                                    acc(5 * planeWidth + p) += k * (lv * lv)
                                    acc(6 * planeWidth + p) += k * (gv * lv)
                                    acc(7 * planeWidth + p) += k * (gv * rv)
                                    acc(8 * planeWidth + p) += k * (lv * rv)
                                    acc(9 * planeWidth + p) += k * (rv * rv)
                                    acc(10 * planeWidth + p) += k * (gv * bv)
                                    acc(11 * planeWidth + p) += k * (lv * bv)
                                    acc(12 * planeWidth + p) += k * (bv * bv)
                                Next
                            Next
                            For plane = 0 To 12
                                Dim at = plane * planeWidth
                                For i = 0 To radius - 1
                                    acc(at + i) = acc(at + radius)
                                    acc(at + radius + width + i) = acc(at + radius + lastColumn)
                                Next
                            Next

                            Dim rowOffset = (y - baseRow) * width
                            For x = 0 To lastColumn
                                Dim mG = 0.0F, mL = 0.0F, mR = 0.0F, mB = 0.0F
                                Dim sGG = 0.0F, sLL = 0.0F, sGL = 0.0F, sGR = 0.0F, sLR = 0.0F, sRR = 0.0F
                                Dim sGB = 0.0F, sLB = 0.0F, sBB = 0.0F
                                For i = 0 To 2 * radius
                                    Dim k = kernelW(i)
                                    Dim p = x + i
                                    mG += k * acc(p)
                                    mL += k * acc(planeWidth + p)
                                    mR += k * acc(2 * planeWidth + p)
                                    mB += k * acc(3 * planeWidth + p)
                                    sGG += k * acc(4 * planeWidth + p)
                                    sLL += k * acc(5 * planeWidth + p)
                                    sGL += k * acc(6 * planeWidth + p)
                                    sGR += k * acc(7 * planeWidth + p)
                                    sLR += k * acc(8 * planeWidth + p)
                                    sRR += k * acc(9 * planeWidth + p)
                                    sGB += k * acc(10 * planeWidth + p)
                                    sLB += k * acc(11 * planeWidth + p)
                                    sBB += k * acc(12 * planeWidth + p)
                                Next

                                Dim gc = CDbl(g(rowOffset + x)), rc = CDbl(r(rowOffset + x)), bc = CDbl(b(rowOffset + x))
                                changed(x) = False

                                ' Gemeinsamer Teil beider Anpassungen.
                                Dim meanG = CDbl(mG), meanL = CDbl(mL)
                                Dim varGG = Math.Max(sGG - meanG * meanG, 0.0)
                                Dim varLL = Math.Max(sLL - meanL * meanL, 0.0)
                                Dim covGL = sGL - meanG * meanL
                                Dim floor = DefringeGreenFloorShare * (Math.Max(meanG, 0.0) + DefringeDarkFloor)
                                Dim eps = floor * floor
                                Dim a11 = varGG + eps
                                Dim a22 = varLL + DefringeLaplacianRidge * eps
                                Dim det = Math.Max(a11 * a22 - covGL * covGL, 1.0E-30)

                                Dim sigR As Double, fitR As Double
                                DefringeFit(mR, sGR, sLR, sRR, meanG, meanL, a11, a22, covGL, det, samples, gc, sigR, fitR)
                                Dim sigB As Double, fitB As Double
                                DefringeFit(mB, sGB, sLB, sBB, meanG, meanL, a11, a22, covGL, det, samples, gc, sigB, fitB)

                                Dim significance = Math.Max(sigR, sigB)
                                If significance <= 0.0 Then Continue For

                                ' Farbe als Logarithmus des Verhaeltnisses zu Gruen (log_verhaeltnis).
                                Dim greenRef = Math.Max(gc, 0.0) + DefringeDarkFloor
                                Dim observedR = Math.Log2((Math.Max(rc, 0.0) + DefringeDarkFloor) / greenRef)
                                Dim observedB = Math.Log2((Math.Max(bc, 0.0) + DefringeDarkFloor) / greenRef)
                                Dim fittedR = Math.Log2((Math.Max(fitR, 0.0) + DefringeDarkFloor) / greenRef)
                                Dim fittedB = Math.Log2((Math.Max(fitB, 0.0) + DefringeDarkFloor) / greenRef)
                                Dim keep = (observedR * fittedR + observedB * fittedB) /
                                           Math.Max(observedR * observedR + observedB * observedB, 0.000000000001)
                                keep = Math.Min(1.0, Math.Max(0.0, keep))

                                ' Warme Toene bleiben aus (warm_gewicht).
                                Dim hue = Math.Atan2(observedB, observedR) * 180.0 / Math.PI
                                Dim warm = DefringeSmoothstep(DefringeWarmStart - DefringeWarmRamp, DefringeWarmStart, hue) *
                                           (1.0 - DefringeSmoothstep(DefringeWarmEnd, DefringeWarmEnd + DefringeWarmRamp, hue))
                                Dim effect = significance * (1.0 - warm)
                                Dim reduction = effect * (1.0 - keep)
                                If Not reduction > 0.0 Then Continue For

                                Dim factor = 1.0 - reduction
                                newR(x) = CSng(greenRef * Math.Pow(2.0, observedR * factor) - DefringeDarkFloor)
                                newB(x) = CSng(greenRef * Math.Pow(2.0, observedB * factor) - DefringeDarkFloor)
                                changed(x) = True
                            Next
                            store(y, newR, newB, changed)
                        Next
                    Finally
                        PlanePool(Of Single).Return(r)
                        PlanePool(Of Single).Return(g)
                        PlanePool(Of Single).Return(b)
                        PlanePool(Of Single).Return(smooth)
                        PlanePool(Of Single).Return(lap)
                        PlanePool(Of Single).Return(padded)
                        PlanePool(Of Single).Return(acc)
                    End Try
                End Sub)
        End Sub

        ''' <summary>Die Anpassung eines Kanals x (Rot oder Blau) an a*G + c*L + b im Fenster
        ''' (anpassen im Prototyp). Liefert die geglaettete Wirkung der Sicherheitsschwelle und den
        ''' Wert am Zentrum OHNE den Laplace-Term, a*G + b.</summary>
        Private Shared Sub DefringeFit(meanX As Single, sGX As Single, sLX As Single, sXX As Single,
                                       meanG As Double, meanL As Double,
                                       a11 As Double, a22 As Double, covGL As Double, det As Double,
                                       samples As Double, centerG As Double,
                                       ByRef significance As Double, ByRef fittedCenter As Double)
            Dim mx = CDbl(meanX)
            Dim covGX = sGX - meanG * mx
            Dim covLX = sLX - meanL * mx
            Dim varXX = Math.Max(sXX - mx * mx, 0.0)
            Dim a = (covGX * a22 - covLX * covGL) / det
            Dim c = (a11 * covLX - covGL * covGX) / det
            Dim intercept = mx - a * meanG - c * meanL
            Dim rest = Math.Max(varXX - a * covGX - c * covLX, 1.0E-20)
            Dim errorOfC = Math.Sqrt(rest * (a11 / det) / samples)
            Dim t = Math.Abs(c) / Math.Max(errorOfC, 1.0E-20)
            significance = DefringeSmoothstep(DefringeSignificanceStart, DefringeSignificanceFull, t)
            fittedCenter = a * centerG + intercept
        End Sub

    End Class

End Namespace
