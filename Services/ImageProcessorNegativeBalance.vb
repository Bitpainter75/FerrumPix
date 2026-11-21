Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq
Imports SkiaSharp

Namespace Services

    ' Der Graufarbausgleich der Filmnegativ-Umkehr (Modell 2, ImageAdjustments.NegativeModel).
    '
    ' Die Umkehr normiert jeden Kanal fuer sich im Logarithmus zwischen Filmbasis und Dichtepunkt.
    ' Ein Grau ist damit an der Basis neutral und, wenn der Dichtepunkt grau ist, auch dort;
    ' dazwischen nur, wenn die drei Kennlinien des Films dieselbe Form haben. Echte Filme haben Fuss
    ' und Schulter je Kanal an anderer Stelle, und der Dichtepunkt ist je Kanal ein eigenes
    ' Perzentil, also ein Weissabgleich auf das Hellste jedes Kanals. Gemessen an Negativen aus den
    ' Kennlinien von 18 Farbnegativfilmen (Diagnostics/Negativrundweg): ein Grau weicht im Mittel
    ' um 14 Prozent ab, in den Tiefen um bis zu 50.
    '
    ' Der Ausgleich legt nach der Umkehr je eine Kurve auf Rot und Blau, Gruen bleibt und traegt die
    ' Tonwerte. Die Stuetzstellen kommen aus dem Bild selbst: es wird in Zonen geteilt, jede Zone
    ' zaehlt nach ihrer Naehe zu Grau (wie bei einem Minilab-Printer), nur gleichfarbige Zonen
    ' zaehlen, und nur, was schon VOR dem Ausgleich grau ist, entscheidet, ob er ueberhaupt wirkt.
    ' Die Kurven sind streng steigend, gehen durch 0 (die Basis bleibt Schwarz) und weichen
    ' hoechstens um GrayBalanceMaxDeviation von der Umkehr ab.
    Partial Public Class ImageProcessor

        ''' <summary>Der Wert im Rezept, wenn gemessen wurde, das Bild aber zu wenig Grau traegt: die
        ''' Umkehr bleibt dann wie im Modell 1.</summary>
        Public Const FilmNegativeGrayBalanceNone As String = "none"

        ''' <summary>Zonen entlang der langen Seite.</summary>
        Private Const GrayBalanceZones As Integer = 40
        ''' <summary>Stuetzstellen der Kurven, gleichmaessig ueber die normierte Dichte 0 bis 1.</summary>
        Private Const GrayBalanceKnots As Integer = 8
        ''' <summary>Zonen mit weniger normierter Dichte im Gruen sind Traeger oder tiefes Schwarz
        ''' und sagen ueber die Farbe nichts.</summary>
        Private Const GrayBalanceMinDensity As Double = 0.03
        ''' <summary>Zonen, in denen mehr als dieser Anteil der Punkte an 0 oder 1 anstoesst, fallen weg.</summary>
        Private Const GrayBalanceMaxClipped As Double = 0.05
        ''' <summary>Streuung der Farbe ueber die Teilbloecke einer Zone, bei der sie noch als
        ''' gleichfarbig zaehlt (Gewicht 1/e).</summary>
        Private Const GrayBalanceUniformity As Double = 0.04
        ''' <summary>Abstand zu Grau, bei dem eine Zone noch zaehlt (Gewicht 1/e): zu Beginn und am
        ''' Ende der Nachfuehrung. Eng, weil ein blasser Himmel am Horizont sonst als Grau gilt:
        ''' mit 0,04 und 0,03 zog er an einem der 18 Filme die Wolken nach Gelb (Graufehler 15 auf 17
        ''' Prozent statt auf 9), und bei zwei Blenden Ueberbelichtung wurden 10 von 18 Filmen
        ''' bunter statt grauer. Mit 0,03 und 0,02 wird keiner schlechter.</summary>
        Private Const GrayBalanceSigmaStart As Double = 0.03
        Private Const GrayBalanceSigmaEnd As Double = 0.02
        ''' <summary>Dasselbe fuer die Entscheidung, ob ueberhaupt genug Grau da ist.</summary>
        Private Const GrayBalanceEvidenceSigma As Double = 0.02
        ''' <summary>So viele graue, gleichfarbige Zonen (gewichtet) muss das Bild VOR dem Ausgleich
        ''' tragen, sonst bleibt die Umkehr wie im Modell 1. Gemessen: Motive ohne Grau 0, Wolken
        ''' mit Strasse 15 bis 80, das Farbnegativ vom LS-5000 (Eichhoernchen) 6.</summary>
        Private Const GrayBalanceMinEvidence As Double = 4.0
        ''' <summary>Glaette (Strafe auf die zweite Differenz) und Zug zur Umkehr ohne Ausgleich, in
        ''' Zonen gerechnet. Der Zug entscheidet, wie weit die Kurve dort, wo keine graue Zone
        ''' liegt, der Fortsetzung folgt: an den synthetischen Negativen mit Grau nur in den Lichtern
        ''' (Wolken) senkt ein schwacher Zug (0,05) den Graufehler weiter (15 auf 7 statt 10
        ''' Prozent), am Testnegativ der Ablage bleibt dann in den Mitteltoenen mehr Stich (4,4 auf
        ''' 4,1 statt 3,3), und den Scan vom LS-5000 verschiebt er dreimal so weit (dE 6,7 statt
        ''' 2,0), ohne dass dort bekannt waere, was richtig ist. 1 ist der vorsichtigere Wert.</summary>
        Private Const GrayBalanceSmoothness As Double = 2.0
        Private Const GrayBalanceIdentityPull As Double = 1.0
        ''' <summary>Kleinste Steigung der Kurven: streng steigend, nirgends flach.</summary>
        Private Const GrayBalanceMinSlope As Double = 0.25
        ''' <summary>Groesste Abweichung einer Stuetzstelle von der Umkehr ohne Ausgleich.</summary>
        Private Const GrayBalanceMaxDeviation As Double = 0.1

        ''' <summary>Misst den Graufarbausgleich am BESCHNITTENEN Bild, wie AnalyzeFilmNegative.
        ''' Basis und Dichtepunkt kommen aus <paramref name="adj"/> (leer: gemessen). Ergebnis ist
        ''' der Wert fuer ImageAdjustments.NegativeGrayBalance.</summary>
        Public Shared Function MeasureFilmNegativeGrayBalance(source As SKBitmap, adj As ImageAdjustments) As String
            If source Is Nothing Then Return FilmNegativeGrayBalanceNone
            Dim recipe = If(adj, New ImageAdjustments())
            Dim owned = False
            Dim cropped = ApplyGeometryPipelineOwned(source, recipe, owned)
            Try
                Dim stats = ResolveFilmNegativeStats(cropped, recipe)
                Return MeasureFilmNegativeGrayBalanceCore(cropped, stats.BaseColor, stats.DensityColor)
            Finally
                If owned Then cropped?.Dispose()
            End Try
        End Function

        ''' <summary>Braucht <paramref name="adj"/> noch einen Graufarbausgleich? Umkehr an, Modell 2,
        ''' kein Schwarzweiss und das Feld leer, etwa nach dem Uebertragen eines Looks
        ''' (ImageAdjustments.ForgetTransferredNegativeGrayBalance).</summary>
        Public Shared Function NeedsFilmNegativeGrayBalance(adj As ImageAdjustments) As Boolean
            Return adj IsNot Nothing AndAlso adj.NegativeEnabled AndAlso adj.NegativeModel >= 2 AndAlso
                   Not adj.NegativeMonochrome AndAlso String.IsNullOrEmpty(adj.NegativeGrayBalance)
        End Function

        ''' <summary>Traegt den Graufarbausgleich in <paramref name="adj"/> ein, gemessen EINMAL an der
        ''' Datei <paramref name="path"/>, fuer Einfuegen und Stapel: das Rezept des Zielbilds traegt
        ''' danach einen festen Wert (oder "none"), und Kachel, Betrachter, Editor und Export rechnen
        ''' damit, statt bei jedem Rendern neu zu messen. Basis und Dichtepunkt kommen aus dem Rezept,
        ''' wie in der Kette. Eine RAW wird halb gross entwickelt, wie fuer die Kacheln, und linear
        ''' wie fuer die Umkehr (wie CheckFilmNegativeFile).
        '''
        ''' Schreibt nichts: der Wert kommt nur in das Rezept, das der Aufrufer ohnehin schreibt oder
        ''' rendert. Laesst sich die Datei nicht lesen, oder ist sie ein .fpx (kommt samt Umkehr
        ''' gerendert heraus), bleibt das Feld leer, und die Kette misst wie bisher am Bild.</summary>
        Public Shared Sub MeasureFilmNegativeGrayBalanceFor(adj As ImageAdjustments, path As String)
            If Not NeedsFilmNegativeGrayBalance(adj) Then Return
            If String.IsNullOrWhiteSpace(path) OrElse Not IO.File.Exists(path) OrElse FpxService.IsFpx(path) Then Return
            Dim bmp As SKBitmap = Nothing
            Try
                If RawPreviewService.IsSupportedRaw(path) Then
                    bmp = RawDecodeService.TryDecodeThumbnail(path, LensChoiceFrom(adj), adj.RawHighlightRecovery,
                                                              0.0, adj.RawHighlightKnee())
                End If
                If bmp Is Nothing Then bmp = DecodeForAdjustments(path, adj)
                If bmp Is Nothing Then Return
                adj.NegativeGrayBalance = MeasureFilmNegativeGrayBalance(bmp, adj)
            Catch ex As Exception
                DiagnosticLogService.LogException("ImageProcessor.MeasureFilmNegativeGrayBalanceFor", ex)
            Finally
                bmp?.Dispose()
            End Try
        End Sub

        ''' <summary>Die Kurven fuer Rot und Blau aus dem Rezept, oder aus dem Bild, wenn das Rezept
        ''' keine traegt (Stapel, aeltere Werkzeuge). Nothing: kein Ausgleich.</summary>
        Private Shared Function ResolveFilmNegativeGrayBalance(measured As SKBitmap, adj As ImageAdjustments,
                                                               baseColor As SKColor, densityColor As SKColor) As (Red As Double(), Blue As Double())?
            Dim stored = adj.NegativeGrayBalance
            If String.IsNullOrWhiteSpace(stored) Then
                stored = MeasureFilmNegativeGrayBalanceCore(measured, baseColor, densityColor)
            End If
            Return ParseFilmNegativeGrayBalance(stored)
        End Function

        ''' <summary>Liest "r0,...,rK|b0,...,bK" (Stuetzstellen bei k/K). Nothing fuer "none",
        ''' Leeres und alles, was keine streng steigende Kurve ab 0 ergibt.</summary>
        Friend Shared Function ParseFilmNegativeGrayBalance(text As String) As (Red As Double(), Blue As Double())?
            If String.IsNullOrWhiteSpace(text) OrElse text = FilmNegativeGrayBalanceNone Then Return Nothing
            Dim parts = text.Split("|"c)
            If parts.Length <> 2 Then Return Nothing
            Dim red = ParseGrayBalanceKnots(parts(0))
            Dim blue = ParseGrayBalanceKnots(parts(1))
            If red Is Nothing OrElse blue Is Nothing OrElse red.Length <> blue.Length Then Return Nothing
            Return (red, blue)
        End Function

        Private Shared Function ParseGrayBalanceKnots(text As String) As Double()
            Dim items = text.Split(","c)
            If items.Length < 2 Then Return Nothing
            Dim values = New Double(items.Length - 1) {}
            For i = 0 To items.Length - 1
                If Not Double.TryParse(items(i), NumberStyles.Float, CultureInfo.InvariantCulture, values(i)) Then Return Nothing
                If Double.IsNaN(values(i)) OrElse Double.IsInfinity(values(i)) Then Return Nothing
                If i > 0 AndAlso values(i) <= values(i - 1) Then Return Nothing
            Next
            If Math.Abs(values(0)) > 0.000001 Then Return Nothing
            Return values
        End Function

        Private Shared Function FormatGrayBalanceKnots(values As Double()) As String
            Return String.Join(",", values.Select(Function(v) v.ToString("0.0000", CultureInfo.InvariantCulture)))
        End Function

        ''' <summary>Eine gleichfarbige Zone des umgekehrten Bildes: mittlere normierte Dichte je Kanal
        ''' und ihr Gewicht nach der Gleichfarbigkeit.</summary>
        Private Structure GrayBalanceZone
            Public Red As Double
            Public Green As Double
            Public Blue As Double
            Public Uniformity As Double
        End Structure

        ''' <summary>Der Kern: Zonen messen, Kurven anpassen. <paramref name="bmp"/> ist das Bild,
        ''' auf dem die Umkehr laeuft (beschnitten), Basis und Dichtepunkt wie in der Umkehr.</summary>
        Friend Shared Function MeasureFilmNegativeGrayBalanceCore(bmp As SKBitmap, baseColor As SKColor, densityColor As SKColor) As String
            Dim buffer As Byte() = Nothing
            Dim stride As Integer = 0
            If bmp Is Nothing OrElse Not TryBorrowBgraBuffer(bmp, buffer, stride) Then Return FilmNegativeGrayBalanceNone

            ' Normierte Dichte je Kanal und Byte, wortgleich mit BuildPointOpFilmNegativeTable im
            ' Modell 1 (ohne Gradation: die wirkt nach dem Ausgleich und auf alle Kanaele gleich).
            Dim bases = New Byte() {baseColor.Red, baseColor.Green, baseColor.Blue}
            Dim densities = New Byte() {densityColor.Red, densityColor.Green, densityColor.Blue}
            Dim tables = New Double(2)() {}
            For c = 0 To 2
                Dim baseLevel = Math.Max(2.0, CDbl(bases(c)))
                Dim densityLevel = Math.Min(Math.Max(1.0, CDbl(densities(c))), baseLevel - 1.0)
                Dim baseLinear = SrgbToLinear(baseLevel / 255.0)
                Dim linearSpan = Math.Log(baseLinear / SrgbToLinear(densityLevel / 255.0))
                tables(c) = New Double(255) {}
                For i = 0 To 255
                    Dim level = Math.Max(0.000001, SrgbToLinear(i / 255.0))
                    tables(c)(i) = Math.Max(0.0, Math.Min(1.0, Math.Log(baseLinear / level) / linearSpan))
                Next
            Next

            ' Gemessen wird ohne den Halter am Rand, wie Basis und Dichtepunkt.
            Dim holderStep = Math.Max(1, CInt(Math.Sqrt(bmp.Width * CDbl(bmp.Height) / 250000.0)))
            Dim area = FindFilmHolderArea(buffer, stride, bmp.Width, bmp.Height, holderStep)
            Dim zones = CollectGrayBalanceZones(buffer, stride, area, tables)

            ' Der Schutz: was schon VOR dem Ausgleich grau ist. Der Ausgleich selbst macht die
            ' Zonen grau, denen er folgt; zaehlte das mit, zoege er sich an einem blauen Himmel
            ' oder einer Wiese selbst hoch.
            Dim evidence = 0.0
            For Each z In zones
                evidence += z.Uniformity * Math.Exp(-Square(GrayDistance(z, Nothing, Nothing) / GrayBalanceEvidenceSigma))
            Next
            If evidence < GrayBalanceMinEvidence Then Return FilmNegativeGrayBalanceNone

            ' Nachfuehren: das Gewicht jeder Zone folgt ihrem Abstand zu Grau UNTER dem bisherigen
            ' Ausgleich, mit enger werdendem Fenster. Begonnen wird ohne Ausgleich.
            Dim red As Double() = Nothing
            Dim blue As Double() = Nothing
            Dim weights = New Double(zones.Count - 1) {}
            Dim xr = zones.Select(Function(z) z.Red).ToArray()
            Dim xb = zones.Select(Function(z) z.Blue).ToArray()
            Dim yg = zones.Select(Function(z) z.Green).ToArray()
            Const rounds As Integer = 5
            For round = 0 To rounds - 1
                Dim sigma = GrayBalanceSigmaStart * Math.Pow(GrayBalanceSigmaEnd / GrayBalanceSigmaStart, round / CDbl(rounds - 1))
                For i = 0 To zones.Count - 1
                    weights(i) = zones(i).Uniformity * Math.Exp(-Square(GrayDistance(zones(i), red, blue) / sigma))
                Next
                red = FitGrayBalanceCurve(xr, yg, weights)
                blue = FitGrayBalanceCurve(xb, yg, weights)
            Next
            Return FormatGrayBalanceKnots(red) & "|" & FormatGrayBalanceKnots(blue)
        End Function

        ''' <summary>Abstand einer Zone zu Grau unter den Kurven (Nothing: ohne Ausgleich).</summary>
        Private Shared Function GrayDistance(z As GrayBalanceZone, red As Double(), blue As Double()) As Double
            Dim r = If(red Is Nothing, z.Red, EvaluateGrayBalanceCurve(red, z.Red))
            Dim b = If(blue Is Nothing, z.Blue, EvaluateGrayBalanceCurve(blue, z.Blue))
            Return Math.Max(Math.Abs(r - z.Green), Math.Abs(b - z.Green))
        End Function

        Private Shared Function Square(v As Double) As Double
            Return v * v
        End Function

        ''' <summary>Teilt die Flaeche in quadratische Zonen (GrayBalanceZones entlang der langen
        ''' Seite) und mittelt je Zone die normierte Dichte. Die Gleichfarbigkeit zaehlt ueber 4x4
        ''' Teilbloecke und nicht ueber einzelne Punkte: das Korn eines Scans streut je Punkt mehr
        ''' als jede Farbkante, im Blockmittel faellt es heraus (Farbnegativ vom LS-5000: ueber
        ''' Punkte gemessen galten nur 2 von 730 Zonen als gleichfarbig).</summary>
        Private Shared Function CollectGrayBalanceZones(buffer As Byte(), stride As Integer, area As SKRectI,
                                                        tables As Double()()) As List(Of GrayBalanceZone)
            Dim zones As New List(Of GrayBalanceZone)()
            Dim longSide = Math.Max(area.Width, area.Height)
            Dim edge = Math.Max(4, CInt(Math.Round(longSide / CDbl(GrayBalanceZones))))
            Dim stepPx = Math.Max(1, edge \ 16)
            Dim part = Math.Max(1, edge \ 4)
            Dim tr = tables(0), tg = tables(1), tb = tables(2)
            For zy = area.Top To area.Bottom - 1 Step edge
                If zy + edge \ 2 >= area.Bottom Then Exit For
                Dim zyEnd = Math.Min(area.Bottom, zy + edge)
                For zx = area.Left To area.Right - 1 Step edge
                    If zx + edge \ 2 >= area.Right Then Exit For
                    Dim zxEnd = Math.Min(area.Right, zx + edge)
                    Dim sumR = 0.0, sumG = 0.0, sumB = 0.0
                    Dim sumRg = 0.0, sumBg = 0.0, sqRg = 0.0, sqBg = 0.0
                    Dim count = 0, clipped = 0, blocks = 0
                    For by = zy To zyEnd - 1 Step part
                        For bx = zx To zxEnd - 1 Step part
                            Dim br = 0.0, bg = 0.0, bb = 0.0
                            Dim bn = 0
                            For y = by To Math.Min(zyEnd, by + part) - 1 Step stepPx
                                Dim row = y * stride
                                For x = bx To Math.Min(zxEnd, bx + part) - 1 Step stepPx
                                    Dim o = row + x * 4
                                    If buffer(o + 3) < 8 Then Continue For
                                    Dim r = tr(buffer(o + 2)), g = tg(buffer(o + 1)), b = tb(buffer(o))
                                    bn += 1
                                    ' Stoesst nur ein Teil der Kanaele an, ist die Farbe dort
                                    ' verloren. Stoessen alle drei an (Weiss ueber dem Dichtepunkt,
                                    ' Schwarz an der Basis), bleibt die Stelle grau und zaehlt: so
                                    ' haelt der Ausgleich ein ausgefressenes Weiss weiss.
                                    Dim low = (If(r <= 0.0005, 1, 0) + If(g <= 0.0005, 1, 0) + If(b <= 0.0005, 1, 0))
                                    Dim high = (If(r >= 0.9995, 1, 0) + If(g >= 0.9995, 1, 0) + If(b >= 0.9995, 1, 0))
                                    If (low > 0 AndAlso low < 3) OrElse (high > 0 AndAlso high < 3) Then clipped += 1
                                    br += r : bg += g : bb += b
                                Next
                            Next
                            If bn = 0 Then Continue For
                            count += bn
                            sumR += br : sumG += bg : sumB += bb
                            Dim dRg = (br - bg) / bn, dBg = (bb - bg) / bn
                            sumRg += dRg : sumBg += dBg
                            sqRg += dRg * dRg : sqBg += dBg * dBg
                            blocks += 1
                        Next
                    Next
                    If count < 8 OrElse blocks < 4 OrElse clipped > GrayBalanceMaxClipped * count Then Continue For
                    Dim meanG = sumG / count
                    If meanG < GrayBalanceMinDensity Then Continue For
                    Dim varRg = Math.Max(0.0, sqRg / blocks - Square(sumRg / blocks))
                    Dim varBg = Math.Max(0.0, sqBg / blocks - Square(sumBg / blocks))
                    Dim spread = Math.Sqrt(Math.Max(varRg, varBg))
                    zones.Add(New GrayBalanceZone With {
                        .Red = sumR / count, .Green = meanG, .Blue = sumB / count,
                        .Uniformity = Math.Exp(-Square(spread / GrayBalanceUniformity))})
                Next
            Next
            Return zones
        End Function

        ''' <summary>Eine Kurve f mit f(x_i) nahe y_i (gewichtet), als Stuetzstellen bei k/K.
        ''' Kleinste Quadrate auf Dachfunktionen, f(0) = 0 fest; dazu eine Strafe auf die zweite
        ''' Differenz (glatt, wo keine Zone liegt setzt sie fort) und ein schwacher Zug zur Geraden
        ''' (kleinste Aenderung). Danach streng steigend (isotone Regression mit Mindeststeigung)
        ''' und hoechstens GrayBalanceMaxDeviation von der Geraden.</summary>
        Private Shared Function FitGrayBalanceCurve(x As Double(), y As Double(), weights As Double()) As Double()
            Const k As Integer = GrayBalanceKnots
            Dim a = New Double(k - 1, k - 1) {}
            Dim rhs = New Double(k - 1) {}
            For i = 0 To x.Length - 1
                Dim w = weights(i)
                If w <= 0 Then Continue For
                Dim pos = Math.Max(0.0, Math.Min(1.0, x(i))) * k
                Dim j = Math.Min(k - 1, CInt(Math.Floor(pos)))
                Dim frac = pos - j
                ' Stuetzstelle j mit (1 - frac), j + 1 mit frac; Stuetzstelle 0 ist fest 0 und
                ' faellt heraus. Unbekannte sind die Stuetzstellen 1 bis K (Index 0 bis K-1).
                Dim idx = New List(Of (Index As Integer, Factor As Double))()
                If j >= 1 Then idx.Add((j - 1, 1.0 - frac))
                idx.Add((j, frac))
                For Each p In idx
                    rhs(p.Index) += w * p.Factor * y(i)
                    For Each q In idx
                        a(p.Index, q.Index) += w * p.Factor * q.Factor
                    Next
                Next
            Next
            For m = 1 To k - 1
                Dim terms = New List(Of (Index As Integer, Factor As Double))()
                If m - 1 >= 1 Then terms.Add((m - 2, 1.0))
                terms.Add((m - 1, -2.0))
                terms.Add((m, 1.0))
                For Each p In terms
                    For Each q In terms
                        a(p.Index, q.Index) += GrayBalanceSmoothness * p.Factor * q.Factor
                    Next
                Next
            Next
            For m = 1 To k
                a(m - 1, m - 1) += GrayBalanceIdentityPull
                rhs(m - 1) += GrayBalanceIdentityPull * m / CDbl(k)
            Next
            Dim solved = SolveSymmetric(a, rhs, k)

            ' Streng steigend: z_m = v_m - m * eps darf nicht fallen, z_0 = 0 steht fest.
            Dim eps = GrayBalanceMinSlope / k
            Dim z = New Double(k) {}
            Dim zw = New Double(k) {}
            zw(0) = 1.0E+9
            For m = 1 To k
                z(m) = solved(m - 1) - m * eps
                zw(m) = Math.Max(0.000000000001, a(m - 1, m - 1))
            Next
            z = IsotonicRegression(z, zw)
            Dim knots = New Double(k) {}
            For m = 1 To k
                Dim u = m / CDbl(k)
                knots(m) = Math.Max(u - GrayBalanceMaxDeviation, Math.Min(u + GrayBalanceMaxDeviation, z(m) + m * eps))
            Next
            knots(0) = 0.0
            Return knots
        End Function

        ''' <summary>Gauss mit Spaltenpivot fuer das kleine System der Kurvenanpassung.</summary>
        Private Shared Function SolveSymmetric(a As Double(,), b As Double(), n As Integer) As Double()
            Dim m = CType(a.Clone(), Double(,))
            Dim r = CType(b.Clone(), Double())
            For i = 0 To n - 1
                Dim pivot = i
                For k = i + 1 To n - 1
                    If Math.Abs(m(k, i)) > Math.Abs(m(pivot, i)) Then pivot = k
                Next
                If pivot <> i Then
                    For k = 0 To n - 1
                        Dim t = m(i, k) : m(i, k) = m(pivot, k) : m(pivot, k) = t
                    Next
                    Dim tr = r(i) : r(i) = r(pivot) : r(pivot) = tr
                End If
                For k = i + 1 To n - 1
                    Dim f = m(k, i) / m(i, i)
                    For l = i To n - 1
                        m(k, l) -= f * m(i, l)
                    Next
                    r(k) -= f * r(i)
                Next
            Next
            Dim x = New Double(n - 1) {}
            For i = n - 1 To 0 Step -1
                Dim s = r(i)
                For k = i + 1 To n - 1
                    s -= m(i, k) * x(k)
                Next
                x(i) = s / m(i, i)
            Next
            Return x
        End Function

        ''' <summary>Gewichtete isotone Regression (Pool Adjacent Violators): die naechste nicht
        ''' fallende Folge.</summary>
        Private Shared Function IsotonicRegression(values As Double(), weights As Double()) As Double()
            Dim blockValue As New List(Of Double)()
            Dim blockWeight As New List(Of Double)()
            Dim blockLength As New List(Of Integer)()
            For i = 0 To values.Length - 1
                blockValue.Add(values(i)) : blockWeight.Add(weights(i)) : blockLength.Add(1)
                While blockValue.Count > 1 AndAlso blockValue(blockValue.Count - 2) > blockValue(blockValue.Count - 1)
                    Dim last = blockValue.Count - 1
                    Dim w = blockWeight(last - 1) + blockWeight(last)
                    Dim v = (blockValue(last - 1) * blockWeight(last - 1) + blockValue(last) * blockWeight(last)) / w
                    Dim l = blockLength(last - 1) + blockLength(last)
                    blockValue.RemoveAt(last) : blockWeight.RemoveAt(last) : blockLength.RemoveAt(last)
                    blockValue(last - 1) = v : blockWeight(last - 1) = w : blockLength(last - 1) = l
                End While
            Next
            Dim result = New Double(values.Length - 1) {}
            Dim p = 0
            For b = 0 To blockValue.Count - 1
                For n = 1 To blockLength(b)
                    result(p) = blockValue(b)
                    p += 1
                Next
            Next
            Return result
        End Function

        ''' <summary>Die Kurve an der Stelle <paramref name="x"/> (0 bis 1): dieselbe monotone
        ''' kubische Hermite-Kurve (PCHIP) wie die Luminanzkurve, durch die Stuetzstellen bei k/K.
        ''' Steigen die Stuetzstellen streng, steigt die Kurve streng; sie ist stetig in der
        ''' Steigung, eine Kante wie bei linearer Interpolation saehe man in einem Verlauf als
        ''' Farbstufe.</summary>
        Friend Shared Function EvaluateGrayBalanceCurve(knots As Double(), x As Double) As Double
            Return EvaluateCurveSpline(GrayBalancePoints(knots), Math.Max(0.0, Math.Min(1.0, x)))
        End Function

        Private Shared Function GrayBalancePoints(knots As Double()) As List(Of (X As Double, Y As Double))
            Dim k = knots.Length - 1
            Dim points = New List(Of (X As Double, Y As Double))(k + 1)
            For i = 0 To k
                points.Add((i / CDbl(k), knots(i)))
            Next
            Return points
        End Function

    End Class

End Namespace
