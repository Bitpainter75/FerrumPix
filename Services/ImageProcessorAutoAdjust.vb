Imports System
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Automatische Bildverbesserung ("Auto" im Filter-Werkzeug).
    '''
    ''' Gemessen wird das UNBEARBEITETE Bild; herauskommen ABSOLUTE Reglerwerte, keine Zuschläge auf
    ''' den aktuellen Stand. Das hat zwei Folgen, die so gewollt sind: zweimal "Auto" ergibt dasselbe
    ''' Ergebnis wie einmal, und der Nutzer kann jeden gesetzten Regler danach von Hand nachziehen -
    ''' es ist dieselbe Bearbeitung wie von Hand, nur schon einmal grob eingestellt.
    '''
    ''' Die Zahlenwerte sind an der ECHTEN Kennlinie der Engine ausgerechnet, nicht geraten:
    ''' Belichtung wirkt als Faktor im Linearlicht (ToneTransfer), Schwarz/Weiss verschieben den
    ''' Tonwert um höchstens 0,28 mal ihrem Zonengewicht (BuildPointOpScalarTable). Ändert sich dort
    ''' die Mathematik, gehören die Konstanten hier mit angefasst.
    '''
    ''' Alles ist bewusst gedämpft und geklemmt: eine Automatik, die ein Bild bis an die Anschläge
    ''' zieht, ist schlechter als eine, die es in die richtige Richtung schiebt und den Rest dem
    ''' Nutzer lässt. Bilder, die schon gut stehen, kommen durch die Totbänder unverändert heraus.
    ''' </summary>
    Partial Public Class ImageProcessor

        ''' <summary>Zielhelligkeit für den Median (Gamma-Raum). An 20 Landschafts-/Strandfotos
        ''' gemessen: deren Mediane liegen um 0,55, nicht bei den 0,46, die als "Mittelgrau" im Kopf
        ''' herumgeistern. Mit 0,46 dunkelte die Automatik reihenweise gut belichtete Fotos ab.</summary>
        Private Const AutoTargetMidtone As Double = 0.5

        ''' <summary>Zielposition für den dunkelsten (0,5%) und hellsten (99,5%) Bildton. Nicht 0 und
        ''' 1: ein Schwarzpunkt exakt auf 0 frisst die Zeichnung, die gerade gerettet werden soll.</summary>
        Private Const AutoTargetBlackPoint As Double = 0.02
        ''' Der Weisspunkt liegt bei echten Fotos bei 0,89 bis 0,95 - mit dem frueheren Ziel 0,97 lief
        ''' der Weiss-Regler bei 12 von 20 Bildern in seine Klemmung. 0,94 ist das, was ein gut
        ''' durchgezeichnetes Foto tatsaechlich zeigt.
        Private Const AutoTargetWhitePoint As Double = 0.94

        ''' <summary>Zielspreizung der Mitteltöne (Quartilsabstand). An echten Fotos gemessen liegt sie
        ''' zwischen 0,20 und 0,61 mit Schwerpunkt um 0,40 - der frühere Wert 0,24 stammte aus einer
        ''' synthetischen Glockenkurve und liess die Automatik jedes zweite Foto flacher rechnen.</summary>
        Private Const AutoTargetIqr As Double = 0.3

        ''' <summary>Wie weit die rechnerisch nötige Korrektur tatsächlich gefahren wird. Der volle
        ''' Wert ist bei Belichtung und Weissabgleich fast immer zu viel: Gegenlicht, Sonnenuntergang
        ''' und Nachtaufnahmen sind absichtlich nicht mittelgrau und nicht neutral.</summary>
        Private Const AutoExposureDamping As Double = 0.6
        Private Const AutoWhiteBalanceDamping As Double = 0.6

        ''' <summary>Ergebnis der Bildanalyse: die Reglerwerte, die "Auto" setzt. Alles andere (Kurven,
        ''' HSL, Effekte, Filter) fasst die Automatik nicht an.</summary>
        Public Class AutoAdjustResult
            ''' <summary>False, wenn nichts messbar war (kein Bild, nur transparente Pixel) - dann
            ''' darf der Aufrufer KEINE Regler setzen.</summary>
            Public Property HasMeasurement As Boolean = False
            Public Property Exposure As Double = 0
            Public Property Contrast As Double = 0
            Public Property Highlights As Double = 0
            Public Property ShadowsLevel As Double = 0
            Public Property Whites As Double = 0
            Public Property Blacks As Double = 0
            Public Property Vibrance As Double = 0
            Public Property Temperature As Double = 0
            Public Property Tint As Double = 0

            ''' <summary>True, wenn die Messung das Bild schon für gut befunden hat und alle Regler auf
            ''' 0 stehen bleiben.</summary>
            Public Function IsNeutral() As Boolean
                Return Exposure = 0 AndAlso Contrast = 0 AndAlso Highlights = 0 AndAlso ShadowsLevel = 0 AndAlso
                       Whites = 0 AndAlso Blacks = 0 AndAlso Vibrance = 0 AndAlso Temperature = 0 AndAlso Tint = 0
            End Function
        End Class

        ''' <summary>Misst ein Bild und liefert die Reglerwerte der automatischen Bildverbesserung.
        ''' Rein lesend, deterministisch und ohne Seiteneffekte - genau deshalb prüfbar.</summary>
        Public Shared Function AnalyzeAutoAdjustments(source As SKBitmap) As AutoAdjustResult
            Dim result As New AutoAdjustResult()
            If source Is Nothing OrElse source.Width <= 0 OrElse source.Height <= 0 Then Return result

            Dim buffer As Byte() = Nothing
            Dim stride As Integer = 0
            Dim ri As Integer, gi As Integer, bi As Integer, ai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, buffer, stride, ri, gi, bi, ai) Then Return result

            Dim histL = New Integer(255) {}
            Dim total As Long = 0
            Dim sumR As Double = 0, sumG As Double = 0, sumB As Double = 0
            Dim midCount As Long = 0
            Dim sumChroma As Double = 0
            Dim chromaCount As Long = 0

            ' Wie bei der Filmnegativ-Messung: ab gut hunderttausend Stichproben sind Perzentile
            ' stabil, jedes Pixel eines 40-MP-Bildes anzufassen macht die Messung nur langsamer.
            Dim stepPx = Math.Max(1, CInt(Math.Sqrt(source.Width * CDbl(source.Height) / 250000.0)))
            For y As Integer = 0 To source.Height - 1 Step stepPx
                Dim row = y * stride
                For x As Integer = 0 To source.Width - 1 Step stepPx
                    Dim o = row + x * 4
                    ' NUR voll deckende Pixel: bei Premultiplied-Alpha sind halbtransparente Pixel
                    ' bereits abgedunkelt und würden die Helligkeitsmessung nach unten ziehen.
                    If buffer(o + ai) < 250 Then Continue For

                    Dim r = CDbl(buffer(o + ri))
                    Dim g = CDbl(buffer(o + gi))
                    Dim b = CDbl(buffer(o + bi))
                    Dim lum = 0.299 * r + 0.587 * g + 0.114 * b
                    Dim li = CInt(Math.Floor(Math.Max(0.0, Math.Min(255.0, lum))))
                    histL(li) += 1
                    total += 1

                    ' Farbstich und Buntheit NUR aus den Mitteltönen: tiefe Schatten rauschen, und
                    ' ausgefressene Lichter sind ohnehin farblos - beide würden die Messung verfälschen.
                    If lum >= 38.0 AndAlso lum <= 230.0 Then
                        Dim maxV = Math.Max(r, Math.Max(g, b))
                        Dim minV = Math.Min(r, Math.Min(g, b))
                        Dim chroma = (maxV - minV) / 255.0
                        sumChroma += chroma
                        chromaCount += 1
                        ' WEISSABGLEICH nur aus NAHEZU NEUTRALEN Pixeln. Die reine Grauwelt ("im Mittel
                        ' ist ein Bild grau") scheitert an jedem Foto mit dominanter Farbe: an echten
                        ' Fotos gemessen lief sie reihenweise in ihre Klemmung (Nachtaufnahme mit
                        ' orangen Straßenlaternen, Hauttöne, Sonnenuntergang) und hätte sie alle kühl
                        ' gerechnet. Kräftige Farben sind Bildinhalt, kein Farbstich - der Stich zeigt
                        ' sich an den Flächen, die neutral sein SOLLTEN.
                        If chroma <= 0.16 Then
                            sumR += r : sumG += g : sumB += b
                            midCount += 1
                        End If
                    End If
                Next
            Next

            If total = 0 Then Return result
            result.HasMeasurement = True

            Dim median = AutoPercentile(histL, total, 0.5)
            Dim pLow = AutoPercentile(histL, total, 0.005)
            Dim pHigh = AutoPercentile(histL, total, 0.995)
            Dim p25 = AutoPercentile(histL, total, 0.25)
            Dim p75 = AutoPercentile(histL, total, 0.75)
            Dim clippedBlack = AutoFractionBelow(histL, total, 2.0 / 255.0)
            Dim clippedWhite = AutoFractionAbove(histL, total, 253.0 / 255.0)

            ' ── 1) Belichtung ────────────────────────────────────────────────────────────────
            ' Den Median auf den Zielmittelton ziehen, gerechnet in LINEARLICHT - genau so wendet
            ' ToneTransfer die Belichtung an, deshalb trifft die Umrechnung in Blendenstufen.
            Dim gain As Double
            If median > 0.004 Then
                gain = SrgbToLinear(CSng(AutoTargetMidtone)) / Math.Max(0.000001, CDbl(SrgbToLinear(CSng(median))))
            Else
                ' Praktisch schwarzes Bild: ein rechnerisch riesiger Faktor würde nur Rauschen
                ' hochziehen, deshalb hart gedeckelt.
                gain = 4.0
            End If
            Dim ev = Math.Log(Math.Max(0.000001, gain), 2.0) * AutoExposureDamping
            ev = AutoClamp(ev, -1.5, 1.5)

            ' LOW-KEY/HIGH-KEY: Nicht jedes dunkle Bild ist unterbelichtet. Eine Nachtaufnahme, ein
            ' Studioporträt vor Schwarz oder ein Schneefeld sind absichtlich nicht mittelgrau -
            ' gemessen an echten Fotos zog die Automatik eine Nachtstadt um 1,2 Blenden hoch und
            ' machte aus der Nacht einen grauen Abend. Je mehr Bildfläche in einem Ende liegt, desto
            ' weniger wird in diese Richtung korrigiert.
            '
            ' Der Unterschied zum wirklich unterbelichteten Foto steht NICHT im dunklen Ende (beide
            ' sind dunkel), sondern im HELLEN: ein Dämmerungs-, Nacht- oder Low-Key-Bild reicht mit
            ' seinen Lichtern schon bis oben (es NUTZT seinen Tonwertumfang, es ist nur dunkel
            ' gestaltet); ein unterbelichtetes Foto hat oben nichts stehen. Gemessen an einem
            ' Dämmerungsfoto (Median 0,26, hellster Ton 0,94): die Automatik zog es um 1,2 Blenden
            ' hoch und wusch die Abendfarben aus - die Belichtung war aber gar nicht das Problem.
            ' Also: je weniger Luft nach oben, desto weniger wird aufgehellt (und umgekehrt unten).
            Dim headroom = AutoClamp((0.92 - pHigh) / 0.22, 0.0, 1.0)
            Dim footroom = AutoClamp((pLow - 0.08) / 0.22, 0.0, 1.0)
            Dim lowKeyDamping = AutoClamp(0.35 + 0.65 * headroom, 0.35, 1.0)
            Dim highKeyDamping = AutoClamp(0.35 + 0.65 * footroom, 0.35, 1.0)
            If ev > 0 Then ev *= lowKeyDamping
            If ev < 0 Then ev *= highKeyDamping

            ' Wer schon ausgefressene Lichter hat, braucht keine Aufhellung mehr - und ein Bild mit
            ' viel echtem Schwarz (Nacht, Studio) soll nicht abgedunkelt werden.
            If clippedWhite > 0.02 Then ev = Math.Min(ev, 0.0)
            If clippedBlack > 0.05 Then ev = Math.Max(ev, 0.0)
            If Math.Abs(ev) < 0.08 Then ev = 0.0
            ' Reglerwert: die Engine rechnet Belichtung = 4 Blendenstufen je 100 Punkte.
            result.Exposure = Math.Round(ev / 4.0 * 100.0)

            ' Alle folgenden Stufen sehen das Bild NACH der Belichtung. Weil die Belichtung streng
            ' monoton ist, dürfen Perzentile einfach durch dieselbe Kennlinie geschoben werden.
            Dim usedGain = Math.Pow(2.0, result.Exposure / 100.0 * 4.0)

            ' ── 2) Kontrast ──────────────────────────────────────────────────────────────────
            ' VOR Schwarz/Weiss, weil die Engine in dieser Reihenfolge rechnet: der Kontrast spreizt
            ' um die Bildmitte, und erst danach steht fest, wo die Bildenden liegen, die Schwarz/Weiss
            ' setzen sollen. (Andersherum gerechnet blieb ein flaues Bild flau: seine Tonwerte liegen
            ' alle um 0,5, und genau dort haben die Schwarz/Weiss-Zonengewichte ihr Minimum - der
            ' Umfang wuchs gemessen nur von 67 auf 83 Tonwerte.)
            '
            ' Gemessen wird der Quartilsabstand, also die Spreizung der MITTE. Gebraucht wird daraus
            ' ein FAKTOR, kein Abstand - der Kontrastregler multipliziert den Abstand zur Bildmitte
            ' mit (1 + Wert/100 * 0,75).
            Dim iqrAfter = AutoMapExposure(p75, usedGain) - AutoMapExposure(p25, usedGain)
            Dim k = AutoTargetIqr / Math.Max(0.02, iqrAfter)
            ' Totband als VERHÄLTNIS, bewusst BREIT und UNSYMMETRISCH: flau ist ein Mangel, knackig
            ' ist eine Gestaltung. Fotos streuen im Quartilsabstand stark (0,20 bis 0,61 gemessen),
            ' deshalb greift die Automatik erst weit ausserhalb der Mitte: aufgezogen ab k > 1,25
            ' (also IQR unter 0,24), weggenommen erst ab k < 0,6 (IQR über 0,5) und dann höchstens
            ' 15 Punkte. Und selbst dann nur halb so weit wie gerechnet - dieselbe Dämpfung wie bei
            ' der Belichtung, aus demselben Grund.
            If k > 1.25 Then
                result.Contrast = AutoDeadband(AutoClamp((AutoClamp(k, 1.0, 2.5) - 1.0) / 0.75 * 100.0 * 0.5, 0.0, 50.0))
            ElseIf k < 0.6 Then
                result.Contrast = AutoDeadband(AutoClamp((AutoClamp(k, 0.5, 1.0) - 1.0) / 0.75 * 100.0 * 0.5, -15.0, 0.0))
            End If
            Dim contrastFactor = Math.Max(0.05, 1.0 + result.Contrast / 100.0 * 0.75)

            Dim lowAfter = AutoMapTone(pLow, usedGain, contrastFactor)
            Dim highAfter = AutoMapTone(pHigh, usedGain, contrastFactor)

            ' ── 3) Schwarz- und Weisspunkt ───────────────────────────────────────────────────
            ' Flaue Bilder (Dunst, Scan, Screenshot mit Grauschleier) nutzen den Tonwertumfang nicht
            ' aus. Schwarz/Weiss verschieben den Ton um Wert/100 * 0,28 * Zonengewicht - der nötige
            ' Reglerwert ist damit eine Auflösung nach dem Wert, kein Erfahrungswert.
            ' Das Zonengewicht steht im NENNER: es darf klemmen, aber nicht als Sperre wirken. Eine
            ' Mindestschwelle liess den Regler frueher auf 0 stehen; der Wert laeuft stattdessen in
            ' seine Klemmung, und weil das Gewicht mit dem Tonwert waechst, holt die Kaskade nach.
            If clippedBlack <= 0.005 AndAlso lowAfter > AutoTargetBlackPoint Then
                Dim wBlacks = Math.Max(0.02, ToneSmoothFade(1.0 - lowAfter / 0.5))
                result.Blacks = AutoDeadband(AutoClamp(-100.0 * (lowAfter - AutoTargetBlackPoint) / (0.28 * wBlacks), -45.0, 0.0))
            End If
            If clippedWhite <= 0.005 AndAlso highAfter < AutoTargetWhitePoint Then
                Dim wWhites = Math.Max(0.02, ToneSmoothFade((highAfter - 0.5) / 0.5))
                result.Whites = AutoDeadband(AutoClamp(100.0 * (AutoTargetWhitePoint - highAfter) / (0.28 * wWhites), 0.0, 45.0))
            End If

            ' ── 4) Lichter und Tiefen ────────────────────────────────────────────────────────
            ' Nicht an einem einzelnen Extremwert, sondern an der MASSE in der jeweiligen Endzone:
            ' ein paar helle Spitzlichter sind normal, ein weiss zugelaufenes Fünftel des Bildes
            ' nicht. Die Schwellen sind auf das Ausgangsbild zurückgerechnet.
            Dim hiMass = AutoFractionAbove(histL, total, AutoUnmapTone(0.86, usedGain, contrastFactor))
            Dim loMass = AutoFractionBelow(histL, total, AutoUnmapTone(0.14, usedGain, contrastFactor))
            ' Die Schwelle von 8% ist gemessen, nicht geraten: Fotos haben regelmässig 10-40% ihrer
            ' Fläche in einer Endzone (Gegenlicht, dunkles Vordergrund-Ufer, heller Himmel) - das ist
            ' Bildaufbau, kein Fehler. Erst darüber wird geöffnet bzw. zurückgeholt, und höchstens
            ' 30 Punkte weit.
            result.Highlights = -AutoDeadband(AutoClamp((hiMass - 0.08) * 150.0, 0.0, 30.0) * highKeyDamping)
            ' Die Tiefen-Anhebung teilt sich die Low-Key-Dämpfung mit der Belichtung: ein Nachtbild
            ' hat viel dunkle Fläche, WEIL es eine Nacht zeigt - sie voll aufzuziehen macht daraus
            ' einen grauen Schleier.
            result.ShadowsLevel = AutoDeadband(AutoClamp((loMass - 0.08) * 150.0, 0.0, 30.0) * lowKeyDamping)

            ' ── 5) Dynamik ───────────────────────────────────────────────────────────────────
            ' Nur anheben, nie entsättigen: kräftige Farben sind eine gestalterische Entscheidung,
            ' blasse sind meistens keine. Dynamik statt Sättigung, weil sie schwach gesättigte Töne
            ' anhebt und bereits kräftige in Ruhe lässt (Hauttöne).
            Dim meanChroma = If(chromaCount > 0, sumChroma / chromaCount, 0.0)
            ' Buntheit = mittlerer Kanalabstand (max-min) der Mitteltoene. Ein normales Farbfoto liegt
            ' bei etwa 0,10-0,20; erst darunter ist ein Bild wirklich blass.
            result.Vibrance = AutoDeadband(AutoClamp((0.10 - meanChroma) * 250.0, 0.0, 30.0))

            ' ── 6) Weissabgleich ─────────────────────────────────────────────────────────────
            ' Grauwelt über die Mitteltöne: im Mittel sollte ein Bild neutral sein. Gedämpft und eng
            ' geklemmt, weil die Annahme bei Sonnenuntergang, Kerzenlicht oder einer einfarbigen
            ' Fläche gerade NICHT stimmt - dort soll die Automatik nur den groben Stich nehmen.
            ' Unter 5% neutralen Stichproben ist die Messung Zufall (Makro auf eine Blüte, Sonnenuntergang
            ' formatfüllend) - dann lieber gar kein Weissabgleich als ein geratener.
            If midCount > 0 AndAlso midCount >= total \ 20 Then
                Dim mr = sumR / midCount
                Dim mg = sumG / midCount
                Dim mb = sumB / midCount
                ' Engine: Rot wird mit (1 + Temp/200), Blau mit (1 - Temp/200) multipliziert. Gleich
                ' gesetzt und nach Temp aufgelöst ergibt das genau diesen Ausdruck.
                If mr + mb > 1.0 Then
                    Dim temperature = 200.0 * (mb - mr) / (mr + mb) * AutoWhiteBalanceDamping
                    temperature = AutoClamp(temperature, -20.0, 20.0)
                    If Math.Abs(temperature) < 1.5 Then temperature = 0.0
                    result.Temperature = Math.Round(temperature)
                End If
                If mg > 1.0 Then
                    ' Grün gegen den (bereits temperaturkorrigierten) Rot/Blau-Mittelwert abgleichen.
                    Dim neutral = (mr * (1.0 + result.Temperature / 200.0) + mb * (1.0 - result.Temperature / 200.0)) / 2.0
                    Dim tint = 200.0 * (neutral - mg) / mg * AutoWhiteBalanceDamping
                    tint = AutoClamp(tint, -20.0, 20.0)
                    If Math.Abs(tint) < 1.5 Then tint = 0.0
                    result.Tint = Math.Round(tint)
                End If
            End If

            Return result
        End Function

        ''' <summary>Kleinster Tonwert (0..1), unterhalb dessen <paramref name="fraction"/> aller
        ''' gezählten Pixel liegen.</summary>
        Private Shared Function AutoPercentile(histogram As Integer(), total As Long, fraction As Double) As Double
            Dim target = Math.Max(1L, Math.Min(total, CLng(Math.Round(total * fraction))))
            Dim running As Long = 0
            For i As Integer = 0 To 255
                running += histogram(i)
                If running >= target Then Return i / 255.0
            Next
            Return 1.0
        End Function

        Private Shared Function AutoFractionBelow(histogram As Integer(), total As Long, value01 As Double) As Double
            If total <= 0 Then Return 0.0
            Dim limit = CInt(Math.Floor(AutoClamp(value01, 0.0, 1.0) * 255.0))
            Dim running As Long = 0
            For i As Integer = 0 To limit
                running += histogram(i)
            Next
            Return running / CDbl(total)
        End Function

        Private Shared Function AutoFractionAbove(histogram As Integer(), total As Long, value01 As Double) As Double
            If total <= 0 Then Return 0.0
            Dim limit = CInt(Math.Ceiling(AutoClamp(value01, 0.0, 1.0) * 255.0))
            Dim running As Long = 0
            For i As Integer = limit To 255
                running += histogram(i)
            Next
            Return running / CDbl(total)
        End Function

        ''' <summary>Tonwert durch die Belichtungskennlinie schieben (Linearlicht mal Faktor).</summary>
        Private Shared Function AutoMapExposure(value01 As Double, gain As Double) As Double
            If gain = 1.0 Then Return value01
            Return AutoClamp(LinearToSrgb(CSng(SrgbToLinear(CSng(value01)) * gain)), 0.0, 1.0)
        End Function

        ''' <summary>Umkehrung von <see cref="AutoMapExposure"/>: welcher Tonwert im Ausgangsbild
        ''' landet nach der Belichtung auf <paramref name="value01"/>?</summary>
        Private Shared Function AutoUnmapExposure(value01 As Double, gain As Double) As Double
            If gain = 1.0 OrElse gain <= 0.0 Then Return value01
            Return AutoClamp(LinearToSrgb(CSng(SrgbToLinear(CSng(value01)) / gain)), 0.0, 1.0)
        End Function

        ''' <summary>Rundet auf ganze Reglerpunkte und wirft Kleinstausschläge weg. Unter drei Punkten
        ''' ist kein Regler sichtbar wirksam - ein Bild, das nur solche Werte bekäme, soll ganz in Ruhe
        ''' gelassen werden, statt mit lauter Einsen und Zweien "bearbeitet" auszusehen.</summary>
        Private Shared Function AutoDeadband(value As Double, Optional threshold As Double = 3.0) As Double
            If Math.Abs(value) < threshold Then Return 0.0
            Return Math.Round(value)
        End Function

        ''' <summary>Tonwert durch Belichtung UND Kontrast schieben. Der Kontrast der Engine spreizt
        ''' im Gamma-Raum um 0,5 (ToneTransfer) - genau das bildet die zweite Zeile ab.</summary>
        Private Shared Function AutoMapTone(value01 As Double, gain As Double, contrastFactor As Double) As Double
            Dim v = AutoMapExposure(value01, gain)
            Return AutoClamp((v - 0.5) * contrastFactor + 0.5, 0.0, 1.0)
        End Function

        ''' <summary>Umkehrung von <see cref="AutoMapTone"/>: welcher Tonwert im Ausgangsbild landet
        ''' nach Belichtung und Kontrast auf <paramref name="value01"/>?</summary>
        Private Shared Function AutoUnmapTone(value01 As Double, gain As Double, contrastFactor As Double) As Double
            Dim v = AutoClamp((value01 - 0.5) / Math.Max(0.05, contrastFactor) + 0.5, 0.0, 1.0)
            Return AutoUnmapExposure(v, gain)
        End Function

        Private Shared Function AutoClamp(value As Double, min As Double, max As Double) As Double
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return 0.0
            Return Math.Max(min, Math.Min(max, value))
        End Function

    End Class

End Namespace
