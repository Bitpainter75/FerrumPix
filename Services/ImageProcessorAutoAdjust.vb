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

        ''' <summary>Die Brücke fuer Stapelverarbeitung und Export: dekodiert die Datei, misst sie
        ''' wie der Editor-Knopf "Automatische Bildverbesserung" und schreibt die gemessenen Werte
        ''' als ABSOLUTE Reglerwerte in die uebergebenen Anpassungen. Bewusst pro Bild aufzurufen -
        ''' die Messung des einen Bildes taugt nicht als Vorlage fuer ein anderes. Ohne Messung
        ''' (Datei nicht lesbar, neutrales Bild) bleiben die Anpassungen unveraendert.</summary>
        Public Shared Sub ApplyAutoAdjustmentsTo(adj As ImageAdjustments, sourcePath As String)
            If adj Is Nothing OrElse String.IsNullOrWhiteSpace(sourcePath) Then Return
            ' DecodeForOutput statt DecodeOriented: .fpx-Projekte muessen aus Basisbild + Rezept
            ' gerendert werden - DecodeOriented reicht das ZIP an den Codec weiter und liefert
            ' Nothing, die Messung waere fuer Projektdateien still ausgefallen.
            ' Ohne .fpx derselbe Decode wie DecodeForOutput, nur mit der Auskunft, ob LibRaw die Datei
            ' entwickelt hat: eine RAW bekommt die Belichtung nur dann in der RAW-Stufe. Springt die eingebettete Vorschau ein, rechnet die Ausgabe sie in der Kette,
            ' und die Automatik muss ihre Werte fuer diesen Weg waehlen. Die Belichtung kommt hier
            ' nicht mit, gemessen wird das Bild ohne sie.
            Dim isFpx = FpxService.IsFpx(sourcePath)
            Dim applied = 0.0
            Dim developed = False
            Dim throughRawStage = False
            Using bmp = If(isFpx, DecodeForOutput(sourcePath),
                           DecodeOrientedWithExposure(sourcePath, True, Nothing, True, 0.0, applied, developed,
                                                      rawStage:=throughRawStage))
                If bmp Is Nothing Then Return
                ' Ein .fpx kommt hier schon fertig gerendert an, samt Umkehr aus seinem Rezept; ein
                ' zweites Umkehren gaebe wieder das Negativ.
                Dim rawStage = throughRawStage AndAlso adj.ExposureBelongsToRawStage()
                Dim r = If(isFpx, AnalyzeAutoAdjustments(bmp), AnalyzeAutoAdjustments(bmp, adj, rawStage))
                If Not r.HasMeasurement OrElse r.IsNeutral() Then Return
                ' Die Messung rechnet in Double, die Regler sind Single. Das Verengen stand
                ' vorher unsichtbar im Compiler; ausgeschrieben ist es nachvollziehbar.
                adj.Exposure = CSng(r.Exposure)
                adj.Contrast = CSng(r.Contrast)
                adj.Highlights = CSng(r.Highlights)
                adj.ShadowsLevel = CSng(r.ShadowsLevel)
                adj.Whites = CSng(r.Whites)
                adj.Blacks = CSng(r.Blacks)
                adj.Vibrance = CSng(r.Vibrance)
                adj.Temperature = CSng(r.Temperature)
                adj.Tint = CSng(r.Tint)
            End Using
        End Sub

        ''' <summary>Wie <see cref="AnalyzeAutoAdjustments(SKBitmap)"/>, aber bei einem Filmnegativ am
        ''' UMGEKEHRTEN Bild gemessen.
        '''
        ''' BEFUND (Forum, C-41-Scan: "way too aggressive"): gemessen wurde die Quelle, also das
        ''' Negativ, die Regler wirken aber nach der Umkehr. Die Automatik sah die orange Maske und
        ''' setzte die Temperatur kaelter, sah ein helles Negativ und dunkelte ein dunkles Positiv
        ''' weiter ab. Gemessen am Farb-Testnegativ: Temperatur -20 und Schwarz -45 statt +4 und 0.
        '''
        ''' Gemessen wird deshalb das Bild nach Geometrie und Umkehr, OHNE die uebrigen Regler: die
        ''' Automatik liefert absolute Werte, ein Messen nach den Reglern rechnete deren Stand doppelt.
        ''' Ohne Negativ bleibt alles wie bisher (die Quelle, ungeschnitten).
        '''
        ''' Wie viel Belichtung schon in <paramref name="source"/> steckt, sagt
        ''' <see cref="ImageAdjustments.ExposureInSource"/> (das Arbeitsbild des Editors).
        ''' <paramref name="exposureInRawStage"/>: wirkt eine neue Belichtung in der RAW-Stufe, weil
        ''' das Bild mit ihr neu entwickelt wird? Siehe die Kernfunktion.</summary>
        Public Shared Function AnalyzeAutoAdjustments(source As SKBitmap, adj As ImageAdjustments,
                                                      Optional exposureInRawStage As Boolean = False) As AutoAdjustResult
            If source Is Nothing OrElse adj Is Nothing OrElse Not adj.NegativeEnabled Then
                Return AnalyzeAutoAdjustments(source, exposureInRawStage,
                                              If(adj Is Nothing, 0.0, CDbl(adj.ExposureInSource)))
            End If
            Dim measure As New ImageAdjustments With {
                .NegativeEnabled = True,
                .NegativeMonochrome = adj.NegativeMonochrome,
                .NegativeBaseColor = adj.NegativeBaseColor,
                .NegativeDensityColor = adj.NegativeDensityColor,
                .NegativeGamma = adj.NegativeGamma,
                .RotationDegrees = adj.RotationDegrees,
                .StraightenDegrees = adj.StraightenDegrees,
                .StraightenExpandCanvas = adj.StraightenExpandCanvas,
                .StraightenAutoCrop = adj.StraightenAutoCrop,
                .FlipHorizontal = adj.FlipHorizontal,
                .FlipVertical = adj.FlipVertical,
                .CropLeftPercent = adj.CropLeftPercent,
                .CropTopPercent = adj.CropTopPercent,
                .CropRightPercent = adj.CropRightPercent,
                .CropBottomPercent = adj.CropBottomPercent}
            ' Nur die Schritte, die festlegen, WELCHER Bildteil bleibt. Groesse aendert das
            ' Histogramm nicht, kostet aber auf einer Vorschau den Weg zur vollen Pixelzahl; eine
            ' Leinwand legte ihre Randfarbe mit in die Messung. Die Kette fasst die Schritte nicht
            ' an, eine Abschrift der Liste genuegt.
            If adj.GeometryOperations IsNot Nothing Then
                For Each op In adj.GeometryOperations
                    Select Case If(op?.Kind, "").Trim().ToLowerInvariant()
                        Case "crop", "transform", "perspective", "warp"
                            measure.GeometryOperations.Add(op)
                    End Select
                Next
            End If
            Using inverted = ProcessBitmap(source, measure)
                If inverted Is Nothing Then Return AnalyzeAutoAdjustments(source)
                Return AnalyzeAutoAdjustments(inverted)
            End Using
        End Function

        ''' <summary>Misst ein Bild und liefert die Reglerwerte der automatischen Bildverbesserung.
        ''' Rein lesend, deterministisch und ohne Seiteneffekte - genau deshalb prüfbar.
        ''' Kontrast, Weiss und Schwarz loest sie mit den gemessenen Kennlinien auf, mit denen die
        ''' Kette sie rechnet (ToneSliderCurves).
        '''
        ''' WO DIE BELICHTUNG WIRKT. Bei einer RAW, die mit der Belichtung neu
        ''' entwickelt wird (<paramref name="exposureInRawStage"/>), ist sie ein Faktor in der Rampe
        ''' VOR der ACR3-Kurve, sonst ein Faktor im Linearlicht nach allem (ToneTransfer). Beide
        ''' Kennlinien sind verschieden: vor der Kurve wirkt dieselbe Blende in den Mitten
        ''' kraeftiger, und die Kurve rollt die Lichter weich aus, statt sie abzuschneiden.
        '''
        ''' <paramref name="sourceExposure"/> ist die Belichtung in Reglerpunkten, die schon im
        ''' gemessenen Bild steckt. Wirkt die Belichtung in der RAW-Stufe, wird sie vor der Messung
        ''' herausgerechnet: das Bild entsteht mit dem neuen Wert ja neu, und nur so ergibt zweimal
        ''' „Auto" dasselbe wie einmal. Sonst bleibt das Bild, wie es ist (eingebackener Inhalt), und
        ''' die Automatik waehlt den Unterschied, den die Kette darauf rechnet.</summary>
        Public Shared Function AnalyzeAutoAdjustments(source As SKBitmap,
                                                      Optional exposureInRawStage As Boolean = False,
                                                      Optional sourceExposure As Double = 0.0) As AutoAdjustResult
            Dim rawStage = exposureInRawStage
            ' Die Belichtung, von der aus die Automatik rechnet: in der RAW-Stufe ist sie
            ' herausgerechnet, sonst steht das Bild mit ihr fest.
            Dim baseExposure = If(rawStage, 0.0, sourceExposure)
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

            ' Die Belichtung, die schon in der RAW-Stufe steckt, je Kanal zurueckrechnen. Je Kanal
            ' statt nach der RGBTone-Regel des Decodes: fuer die Messung von Helligkeit und Stich
            ' genau genug. Was im Bild schon ausgefressen ist, bleibt verloren und kommt als hoher
            ' Ton unter Weiss zurueck.
            Dim undo As Double() = Nothing
            If rawStage AndAlso sourceExposure <> 0.0 Then
                undo = New Double(255) {}
                Dim undoGain = Math.Pow(2.0, -sourceExposure / ImageAdjustments.ExposurePointsPerStop)
                For i = 0 To 255
                    undo(i) = AutoMapExposure(i / 255.0, undoGain, True) * 255.0
                Next
            End If

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

                    Dim r = If(undo Is Nothing, CDbl(buffer(o + ri)), undo(buffer(o + ri)))
                    Dim g = If(undo Is Nothing, CDbl(buffer(o + gi)), undo(buffer(o + gi)))
                    Dim b = If(undo Is Nothing, CDbl(buffer(o + bi)), undo(buffer(o + bi)))
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
            ' ToneTransfer die Belichtung an, deshalb trifft die Umrechnung in Blendenstufen. In der
            ' RAW-Stufe ist es derselbe Quotient, nur auf der Rampe vor der ACR3-Kurve.
            Dim gain As Double
            If median > 0.004 AndAlso rawStage Then
                gain = AutoRampOf(AutoTargetMidtone) / Math.Max(0.000001, AutoRampOf(median))
            ElseIf median > 0.004 Then
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
            ' Reglerwert: die Engine rechnet Belichtung = 4 Blendenstufen je 100 Punkte. Steht das
            ' Bild mit einer Belichtung fest, kommt der Unterschied auf sie drauf.
            result.Exposure = baseExposure + Math.Round(ev / 4.0 * 100.0)

            ' Alle folgenden Stufen sehen das Bild NACH der Belichtung. Weil die Belichtung streng
            ' monoton ist, dürfen Perzentile einfach durch dieselbe Kennlinie geschoben werden.
            Dim usedGain = Math.Pow(2.0, (result.Exposure - baseExposure) / 100.0 * 4.0)

            ' ── 2) Kontrast ──────────────────────────────────────────────────────────────────
            ' VOR Schwarz/Weiss, weil die Engine in dieser Reihenfolge rechnet: der Kontrast spreizt
            ' um die Bildmitte, und erst danach steht fest, wo die Bildenden liegen, die Schwarz/Weiss
            ' setzen sollen. (Andersherum gerechnet blieb ein flaues Bild flau: seine Tonwerte liegen
            ' alle um 0,5, und genau dort haben die Schwarz/Weiss-Zonengewichte ihr Minimum - der
            ' Umfang wuchs gemessen nur von 67 auf 83 Tonwerte.)
            '
            ' Gemessen wird der Quartilsabstand, also die Spreizung der MITTE.
            Dim iqrAfter = AutoMapExposure(p75, usedGain, rawStage) - AutoMapExposure(p25, usedGain, rawStage)
            Dim k = AutoTargetIqr / Math.Max(0.02, iqrAfter)
            ' Totband als VERHÄLTNIS, bewusst BREIT und UNSYMMETRISCH: flau ist ein Mangel, knackig
            ' ist eine Gestaltung. Fotos streuen im Quartilsabstand stark (0,20 bis 0,61 gemessen),
            ' deshalb greift die Automatik erst weit ausserhalb der Mitte: aufgezogen ab k > 1,25
            ' (also IQR unter 0,24), weggenommen erst ab k < 0,6 (IQR über 0,5) und dann höchstens
            ' 15 Punkte. Und selbst dann nur halb so weit wie gerechnet - dieselbe Dämpfung wie bei
            ' der Belichtung, aus demselben Grund.
            ' Der Kontrast ist eine gemessene Kennlinie, kein Faktor. Gesucht wird der Reglerwert,
            ' der den Quartilsabstand um den gedaempften Faktor spreizt (halb so weit wie gerechnet).
            Dim spread = Function(c As Double) AutoMapToneMeasured(p75, usedGain, c, rawStage) - AutoMapToneMeasured(p25, usedGain, c, rawStage)
            If k > 1.25 Then
                Dim wanted = iqrAfter * (1.0 + (AutoClamp(k, 1.0, 2.5) - 1.0) * 0.5)
                result.Contrast = AutoDeadband(AutoSolveRising(spread, wanted, 0.0, 50.0))
            ElseIf k < 0.6 Then
                Dim wanted = iqrAfter * (1.0 + (AutoClamp(k, 0.5, 1.0) - 1.0) * 0.5)
                result.Contrast = AutoDeadband(AutoSolveRising(spread, wanted, -15.0, 0.0))
            End If
            Dim contrastValue = result.Contrast

            Dim lowAfter = AutoMapToneMeasured(pLow, usedGain, contrastValue, rawStage)
            Dim highAfter = AutoMapToneMeasured(pHigh, usedGain, contrastValue, rawStage)

            ' ── 3) Schwarz- und Weisspunkt ───────────────────────────────────────────────────
            ' Flaue Bilder (Dunst, Scan, Screenshot mit Grauschleier) nutzen den Tonwertumfang nicht
            ' aus. Kontrast, Schwarz und Weiss heben denselben Eingangston, ihre Huebe addieren sich
            ' (ImageProcessor.ToneZoneLift). Gesucht wird der Reglerwert, der den Bildrand auf sein
            ' Ziel setzt. Die gemessenen Regler wirken in den Mitten schwach, die Grenzen sind
            ' deshalb weit: ein flaues Bild kam mit +-45 nur von 67 auf 93 Tonwerte.
            Dim lowIn = AutoMapExposure(pLow, usedGain, rawStage)
            Dim highIn = AutoMapExposure(pHigh, usedGain, rawStage)
            Dim wantsBlack = clippedBlack <= 0.005 AndAlso lowAfter > AutoTargetBlackPoint
            Dim wantsWhite = clippedWhite <= 0.005 AndAlso highAfter < AutoTargetWhitePoint
            ' Weiss waehlt seine Kurve nach der Spitze des Bildes, gemessen wie in der Kette und wie
            ' dort durch die Belichtung geschoben; die Automatik loest mit derselben Kurve auf, die
            ' das Bild dann rechnet.
            Dim topL = 0.0
            Dim reference = MeasureToneReference(source)
            If reference.IsMeasured Then
                ' Gemessen am Bild, wie es vorliegt: die schon enthaltene Belichtung der RAW-Stufe
                ' erst heraus, dann die neue hinein.
                Dim topV = LightnessToGrayV(reference.TopL)
                If undo IsNot Nothing Then
                    topV = AutoMapExposure(topV, Math.Pow(2.0, -sourceExposure / ImageAdjustments.ExposurePointsPerStop), True)
                End If
                topL = GrayVToLightness(AutoMapExposure(topV, usedGain, rawStage))
            End If
            ' SCHWARZ UND WEISS GEMEINSAM, WEISS NACH DEM GEWINN AN UMFANG. Bei einem flauen Bild
            ' liegt die Spitze unter allen Eichbildern, und die Kurve dieser Gruppe hebt die Mitten
            ' und das untere Ende kraeftig, dort sogar staerker als oben (Weiss +100 bei v 0,45 um
            ' +0,43, bei 0,6 um +0,33). Weiss bis zum oberen Ziel aufzuloesen hob dann alles mit, und
            ' auch Schwarz -100 holte das untere Ende nicht zurueck: aus einem Umfang von 67
            ' Tonwerten wurden 76, mit gemeinsamem Aufloesen gar keiner mehr. Deshalb wird Weiss so
            ' gewaehlt, dass der Abstand zwischen oberem und unterem Ende am groessten ist, wobei oben
            ' nichts ueber das Ziel hinaus zaehlt; Schwarz loest je Versuch mit dem Hub von Weiss am
            ' unteren Ende. Bei einer gewoehnlichen Kurve ist das genau der Wert, der das obere Ziel
            ' trifft.
            Dim blacksFor = Function(w As Double) As Double
                                If Not wantsBlack Then Return 0.0
                                Return AutoSolveRising(
                                    Function(b) lowAfter + ToneSliderCurves.LiftByTop("Whites", w, lowIn, topL) +
                                                ToneSliderCurves.Lift("Blacks", b, lowIn),
                                    AutoTargetBlackPoint, -100.0, 0.0)
                            End Function
            Dim spanFor = Function(w As Double, b As Double) As Double
                              Dim high = highAfter + ToneSliderCurves.Lift("Blacks", b, highIn) +
                                         ToneSliderCurves.LiftByTop("Whites", w, highIn, topL)
                              Dim low = lowAfter + ToneSliderCurves.LiftByTop("Whites", w, lowIn, topL) +
                                        ToneSliderCurves.Lift("Blacks", b, lowIn)
                              Return Math.Min(high, AutoTargetWhitePoint) - Math.Max(low, AutoTargetBlackPoint)
                          End Function
            Dim whites = 0.0
            Dim blacks = blacksFor(0.0)
            If wantsWhite Then
                Dim bestSpan = spanFor(0.0, blacks)
                For candidate = 1 To 100
                    Dim b = blacksFor(candidate)
                    Dim span = spanFor(candidate, b)
                    ' Nur ein echter Gewinn zaehlt; bei Gleichstand bleibt der kleinere Wert.
                    If span > bestSpan + 0.0005 Then
                        bestSpan = span : whites = candidate : blacks = b
                    End If
                Next
            End If
            If wantsBlack Then result.Blacks = AutoDeadband(blacks)
            If wantsWhite Then result.Whites = AutoDeadband(whites)

            ' ── 4) Lichter und Tiefen ────────────────────────────────────────────────────────
            ' Nicht an einem einzelnen Extremwert, sondern an der MASSE in der jeweiligen Endzone:
            ' ein paar helle Spitzlichter sind normal, ein weiss zugelaufenes Fünftel des Bildes
            ' nicht. Die Schwellen sind auf das Ausgangsbild zurückgerechnet.
            Dim hiMass = AutoFractionAbove(histL, total,
                AutoSolveRising(Function(v) AutoMapToneMeasured(v, usedGain, contrastValue, rawStage), 0.86, 0.0, 1.0))
            Dim loMass = AutoFractionBelow(histL, total,
                AutoSolveRising(Function(v) AutoMapToneMeasured(v, usedGain, contrastValue, rawStage), 0.14, 0.0, 1.0))
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
            ' Der Regler wirkt bei 50 je Farbton verschieden (ImageProcessor.VibranceHue50), im
            ' Mittel ueber die achtzehn Farbtonfelder mit 0,37; umgerechnet, damit die Automatik so
            ' weit geht wie die gemessene Buntheit verlangt. Hauttoene bekommen dabei weniger,
            ' Gruen und Blau mehr - wie beim Regler selbst.
            Dim vibranceWanted = AutoClamp((0.10 - meanChroma) * 250.0, 0.0, 30.0)
            result.Vibrance = AutoDeadband(vibranceWanted * 0.5 / 0.37)

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

        ''' <summary>Tonwert durch die Belichtungskennlinie schieben: Linearlicht mal Faktor, oder mit
        ''' <paramref name="rawStage"/> die Rampe der RAW-Stufe mal Faktor, danach die ACR3-Kurve,
        ''' mit derselben Rechnung, die die Kette bis zum Neuaufbau nimmt
        ''' (<see cref="RawStageExposureAt"/>).</summary>
        Private Shared Function AutoMapExposure(value01 As Double, gain As Double, Optional rawStage As Boolean = False) As Double
            If gain = 1.0 Then Return value01
            If rawStage Then Return RawStageExposureAt(CSng(AutoClamp(value01, 0.0, 1.0)), CSng(gain))
            Return AutoClamp(LinearToSrgb(CSng(SrgbToLinear(CSng(value01)) * gain)), 0.0, 1.0)
        End Function

        ''' <summary>Die Stelle der Rampe in der RAW-Stufe, aus der ein Tonwert des entwickelten Bildes
        ''' (Gammawert 0..1) hervorging: zurueck ins Linearlicht, dann durch die umgekehrte
        ''' ACR3-Kurve.</summary>
        Private Shared Function AutoRampOf(value01 As Double) As Double
            Return RawDecodeService.Acr3CurveInverse(SrgbToLinear(CSng(AutoClamp(value01, 0.0, 1.0))))
        End Function

        ''' <summary>Rundet auf ganze Reglerpunkte und wirft Kleinstausschläge weg. Unter drei Punkten
        ''' ist kein Regler sichtbar wirksam - ein Bild, das nur solche Werte bekäme, soll ganz in Ruhe
        ''' gelassen werden, statt mit lauter Einsen und Zweien "bearbeitet" auszusehen.</summary>
        Private Shared Function AutoDeadband(value As Double, Optional threshold As Double = 3.0) As Double
            If Math.Abs(value) < threshold Then Return 0.0
            Return Math.Round(value)
        End Function

        ''' <summary>Tonwert durch Belichtung UND Kontrast schieben: der Kontrast ist die gemessene
        ''' Kennlinie (ToneSliderCurves) auf dem Ton nach der Belichtung.</summary>
        Private Shared Function AutoMapToneMeasured(value01 As Double, gain As Double, contrast As Double,
                                                    Optional rawStage As Boolean = False) As Double
            Dim v = AutoMapExposure(value01, gain, rawStage)
            Return AutoClamp(v + ToneSliderCurves.Lift("Contrast", contrast, v), 0.0, 1.0)
        End Function

        ''' <summary>Sucht in [<paramref name="low"/>, <paramref name="high"/>] den Wert, an dem die
        ''' steigende Funktion <paramref name="f"/> das Ziel erreicht; liegt es ausserhalb, die
        ''' naechste Grenze. Halbierung, 40 Schritte: fuer Reglerwerte und Tonwerte mehr als genau.</summary>
        Private Shared Function AutoSolveRising(f As Func(Of Double, Double), target As Double,
                                                low As Double, high As Double) As Double
            If f(low) >= target Then Return low
            If f(high) <= target Then Return high
            For i = 1 To 40
                Dim mid = (low + high) / 2.0
                If f(mid) < target Then low = mid Else high = mid
            Next
            Return (low + high) / 2.0
        End Function

        Private Shared Function AutoClamp(value As Double, min As Double, max As Double) As Double
            If Double.IsNaN(value) OrElse Double.IsInfinity(value) Then Return 0.0
            Return Math.Max(min, Math.Min(max, value))
        End Function

    End Class

End Namespace
