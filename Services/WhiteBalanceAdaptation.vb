Imports System
Imports System.Collections.Generic

Namespace Services

    ''' <summary>Ein Weißpunkt als Farbort.</summary>
    Public Structure WhitePoint

        Public Property X As Double
        Public Property Y As Double

        Public Sub New(x As Double, y As Double)
            Me.X = x
            Me.Y = y
        End Sub

        Public ReadOnly Property IsValid As Boolean
            Get
                Return Double.IsFinite(X) AndAlso Double.IsFinite(Y) AndAlso X > 0.0 AndAlso Y > 0.0
            End Get
        End Property

    End Structure

    ''' <summary>Der Weißabgleich als chromatische Adaption, statt als Verstärkung auf Rot und Blau.
    '''
    ''' <para>WAS DER REGLER SAGT. Er nennt das Licht, unter dem die Aufnahme entstanden sein soll.
    ''' Der Decode hat das echte Aufnahmelicht schon neutral gemacht (die Kamera-Multiplikatoren
    ''' stecken in den Daten), das Bild ist also auf den ANKER abgeglichen. Behauptet der Nutzer ein
    ''' anderes Licht, muss die Kette den Anker zurücknehmen und das behauptete Licht neutral
    ''' machen. Beides zusammen ist EINE Adaption von der Behauptung auf den Anker:
    '''
    ''' <code>M = Bradford^-1 · diag(Zapfen(Anker) / Zapfen(Behauptung)) · Bradford</code>
    '''
    ''' Steht der Regler auf dem Anker, ist der Bruch 1 und die Matrix die Einheitsmatrix - das Bild
    ''' läuft unverändert durch, ohne Rundungsrest. Genau deshalb ist der Anker eine eigene Größe
    ''' und nicht aus dem Reglerwert zurückgerechnet: jede Hin-und-Rück-Umrechnung über Kelvin
    ''' würde diese Eigenschaft zerstören.</para>
    '''
    ''' <para>WARUM IM LINEARLICHT. Eine Adaption ist eine Verstärkung der Zapfenantworten, und die
    ''' sind linear in der Lichtmenge. Auf gammakodierten Werten gerechnet verschiebt sie deshalb
    ''' die Helligkeit mit und lässt Neutralgrau nicht neutral. Die Stufe dekodiert also, rechnet
    ''' und kodiert wieder.</para>
    '''
    ''' <para>DIE PROBEN dazu stehen in <see cref="SelfCheck"/>: bekannte Farborte für Normlicht A
    ''' und D65, die Einheitsmatrix bei Anker gleich Behauptung, und eine Adaption mit
    ''' nachgerechnetem Ergebnis. Wer an dieser Datei etwas ändert, lässt sie laufen.</para></summary>
    Public NotInheritable Class WhiteBalanceAdaptation

        Private Sub New()
        End Sub

        ''' <summary>Der Weißpunkt von D65, also von sRGB. Anker für alles, was keine RAW-Datei ist:
        ''' dort gibt es keinen Aufnahme-Weißabgleich, und die Zahlen im Bild sind sRGB.</summary>
        Public Shared ReadOnly D65 As New WhitePoint(0.312727, 0.329024)

        ''' <summary>Grenzen des Kelvin-Reglers. Adobe lässt 2000 bis 50000 zu, zeigt aber 2000 bis
        ''' 50000 nur als Zahlenfeld; als Regler ist der obere Bereich toter Weg, weil sich oberhalb
        ''' von 12000 K kaum noch etwas ändert. Der Import darf trotzdem darüber liegen, deshalb
        ''' klemmt nur der Regler, nicht die Rechnung.</summary>
        Public Const MinKelvin As Double = 2000.0
        Public Const MaxKelvin As Double = 12000.0

        ' ── Farbort aus Kelvin und Tönung, nach der DNG-Spezifikation ────────────

        ''' <summary>Die Isothermen nach Robertson (1968), wie sie die DNG-Spezifikation für
        ''' Temperatur und Tönung verwendet: je Zeile mired, u und v des Planckschen Strahlers
        ''' (CIE 1960) und die Steigung der Isotherme durch diesen Punkt.
        '''
        ''' <para>WARUM DIESE DEFINITION. Kelvin-Zahl und Tönung einer XMP-Datei SIND nach ihr
        ''' festgelegt, und unser Regler soll dieselben Zahlen tragen (Import und Export ohne
        ''' Umrechnung). Gemessen gegen den DNG-Konverter: mit der früheren eigenen Rechnung (Kurve
        ''' aus Planck und Tageslicht, Tönung 0,0001 v je Punkt) wirkte Tönung -66 vier- bis
        ''' fünfmal zu schwach und in einer anderen Richtung, weil die Isotherme nicht senkrecht
        ''' in v liegt, und oberhalb von 4500 K lag schon Tönung 0 rund 10 Punkte daneben, weil
        ''' die Kurve dort die Tageslichtreihe war.</para></summary>
        Private Shared ReadOnly RobertsonMired As Double() = {
            0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 125, 150, 175, 200, 225, 250, 275, 300, 325,
            350, 375, 400, 425, 450, 475, 500, 525, 550, 575, 600}
        Private Shared ReadOnly RobertsonU As Double() = {
            0.18006, 0.18066, 0.18133, 0.18208, 0.18293, 0.18388, 0.18494, 0.18611, 0.1874, 0.1888,
            0.19032, 0.19462, 0.19962, 0.20525, 0.21142, 0.21807, 0.22511, 0.23247, 0.2401, 0.24792,
            0.25591, 0.264, 0.27218, 0.28039, 0.28863, 0.29685, 0.30505, 0.3132, 0.32129, 0.32931,
            0.33724}
        Private Shared ReadOnly RobertsonV As Double() = {
            0.26352, 0.26589, 0.26846, 0.27119, 0.27407, 0.27709, 0.28021, 0.28342, 0.28668, 0.28997,
            0.29326, 0.30141, 0.30921, 0.31647, 0.32312, 0.32909, 0.33439, 0.33904, 0.34308, 0.34655,
            0.34951, 0.352, 0.35407, 0.35577, 0.35714, 0.35823, 0.35907, 0.35968, 0.36011, 0.36038,
            0.36051}
        Private Shared ReadOnly RobertsonSlope As Double() = {
            -0.24341, -0.25479, -0.26876, -0.28539, -0.3047, -0.32675, -0.35156, -0.37915, -0.40955,
            -0.44278, -0.47888, -0.58204, -0.70471, -0.84901, -1.0182, -1.2168, -1.4512, -1.7298,
            -2.0637, -2.4681, -2.9641, -3.5814, -4.3633, -5.3762, -6.7262, -8.5955, -11.324, -15.628,
            -23.325, -40.77, -116.45}

        ''' <summary>Wie viele Tönungspunkte ein Schritt von 1 in uv entlang der Isotherme ist, mit
        ''' Vorzeichen: ein POSITIVER Wert legt den behaupteten Weißpunkt ins Grüne, das Bild wird
        ''' also magenta, wie am Regler. Der Wert der DNG-Spezifikation.</summary>
        Private Const TintScale As Double = -3000.0

        ''' <summary>Der Punkt der Planckschen Linie und die Richtung der Isotherme (Länge 1, u
        ''' positiv) zu einer Temperatur, zwischen den Tabellenzeilen linear in mired.</summary>
        Private Shared Function IsothermAt(kelvin As Double) As (U As Double, V As Double, Du As Double, Dv As Double)
            Dim r = 1000000.0 / kelvin
            Dim last = RobertsonMired.Length - 1
            For index = 0 To last - 1
                If r < RobertsonMired(index + 1) OrElse index = last - 1 Then
                    Dim f = (RobertsonMired(index + 1) - r) / (RobertsonMired(index + 1) - RobertsonMired(index))
                    Dim u = RobertsonU(index) * f + RobertsonU(index + 1) * (1.0 - f)
                    Dim v = RobertsonV(index) * f + RobertsonV(index + 1) * (1.0 - f)
                    Dim len1 = Math.Sqrt(1.0 + RobertsonSlope(index) * RobertsonSlope(index))
                    Dim len2 = Math.Sqrt(1.0 + RobertsonSlope(index + 1) * RobertsonSlope(index + 1))
                    Dim du = f / len1 + (1.0 - f) / len2
                    Dim dv = RobertsonSlope(index) / len1 * f + RobertsonSlope(index + 1) / len2 * (1.0 - f)
                    Dim len3 = Math.Sqrt(du * du + dv * dv)
                    Return (u, v, du / len3, dv / len3)
                End If
            Next
            Return (Double.NaN, Double.NaN, Double.NaN, Double.NaN)
        End Function

        ''' <summary>Der Weißpunkt zu einer Temperatur und einer Tönung, nach der DNG-Spezifikation:
        ''' der Punkt der Planckschen Linie, verschoben um Tönung / -3000 entlang der Isotherme.
        '''
        ''' Liegt das Ergebnis ausserhalb des Sichtbaren, wird die Tönung in Zehnteln
        ''' zurückgenommen, bis ein rechenbarer Weißpunkt herauskommt. Sonst wäre die Folge an den
        ''' Rändern keine sichtbare Grenze, sondern eine Stufe, die stillschweigend nichts mehr tut.</summary>
        Public Shared Function FromKelvinAndTint(kelvin As Double, tint As Double) As WhitePoint
            If Not Double.IsFinite(kelvin) OrElse kelvin <= 0.0 Then Return D65
            If Not Double.IsFinite(tint) Then tint = 0.0
            Dim iso = IsothermAt(kelvin)
            If Double.IsNaN(iso.U) Then Return D65
            Dim locus = FromCie1960(New Cie1960(iso.U, iso.V))
            If tint = 0.0 Then Return locus
            Dim offset = tint / TintScale
            For attempt = 0 To 20
                Dim factor = Math.Pow(0.9, attempt)
                Dim candidate = FromCie1960(New Cie1960(iso.U + iso.Du * offset * factor, iso.V + iso.Dv * offset * factor))
                If candidate.IsValid AndAlso candidate.X + candidate.Y < 1.0 AndAlso HasPositiveConeResponse(candidate) Then
                    Return candidate
                End If
            Next
            Return locus
        End Function

        ''' <summary>Temperatur und Tönung eines Farborts, nach der DNG-Spezifikation und ohne
        ''' Klemmen: die genaue Umkehrung von <see cref="FromKelvinAndTint"/>. Gesucht wird das
        ''' Paar Isothermen, zwischen denen der Farbort liegt; die Temperatur ist dazwischen
        ''' linear in mired, die Tönung der Abstand entlang der gemittelten Isotherme.</summary>
        Friend Shared Function KelvinAndTintOf(point As WhitePoint) As (Kelvin As Double, Tint As Double)
            Dim uv = ToCie1960(point)
            If Double.IsNaN(uv.U) Then Return (Double.NaN, Double.NaN)
            Dim lastDt = 0.0, lastDu = 0.0, lastDv = 0.0
            Dim last = RobertsonMired.Length - 1
            For index = 1 To last
                Dim len = Math.Sqrt(1.0 + RobertsonSlope(index) * RobertsonSlope(index))
                Dim du = 1.0 / len
                Dim dv = RobertsonSlope(index) / len
                ' Abstand über oder unter der Isotherme; darunter ist das Paar gefunden.
                Dim dt = -(uv.U - RobertsonU(index)) * dv + (uv.V - RobertsonV(index)) * du
                If dt <= 0.0 OrElse index = last Then
                    If dt > 0.0 Then dt = 0.0
                    dt = -dt
                    Dim f = If(index = 1, 0.0, dt / (lastDt + dt))
                    Dim kelvin = 1000000.0 / (RobertsonMired(index - 1) * f + RobertsonMired(index) * (1.0 - f))
                    Dim uu = uv.U - (RobertsonU(index - 1) * f + RobertsonU(index) * (1.0 - f))
                    Dim vv = uv.V - (RobertsonV(index - 1) * f + RobertsonV(index) * (1.0 - f))
                    du = du * (1.0 - f) + lastDu * f
                    dv = dv * (1.0 - f) + lastDv * f
                    len = Math.Sqrt(du * du + dv * dv)
                    Return (kelvin, (uu * du / len + vv * dv / len) * TintScale)
                End If
                lastDt = dt : lastDu = du : lastDv = dv
            Next
            Return (Double.NaN, Double.NaN)
        End Function

        ''' <summary>Trägt der Farbort positive Zapfenantworten? Nur dann ist er als Weißpunkt
        ''' brauchbar; ausserhalb des Sichtbaren wird eine davon negativ.</summary>
        Private Shared Function HasPositiveConeResponse(point As WhitePoint) As Boolean
            Dim cone = ConeResponse(point)
            For i = 0 To 2
                If Not Double.IsFinite(cone(i)) OrElse cone(i) <= 0.0 Then Return False
            Next
            Return True
        End Function

        ''' <summary>Der behauptete Weißpunkt für einen RELATIVEN Regler: verschiebt den Anker um
        ''' <paramref name="miredShift"/> und <paramref name="tintPoints"/>.
        '''
        ''' Der Anker wird als Paar aus Temperatur und Tönung gelesen (<see cref="KelvinAndTintOf"/>,
        ''' die genaue Umkehrung), beide Versätze kommen darauf, und das Paar geht über
        ''' <see cref="FromKelvinAndTint"/> zurück. Liegt der Anker neben der Planckschen Linie,
        ''' bleibt er dort: seine eigene Tönung steckt im Paar. Bei Versatz 0 kommt der Anker
        ''' unverändert zurück, und die Matrix ist dann garantiert die Einheitsmatrix.
        '''
        ''' Vorzeichen wie beim gespeicherten Regler: ein POSITIVER Wert ist wärmer, weil höhere
        ''' Kelvin niedrigere mired haben.</summary>
        Public Shared Function ShiftFromAnchor(anchor As WhitePoint, miredShift As Double,
                                               tintPoints As Double) As WhitePoint
            If Not anchor.IsValid Then Return D65
            If miredShift = 0.0 AndAlso tintPoints = 0.0 Then Return anchor
            If Not Double.IsFinite(miredShift) OrElse Not Double.IsFinite(tintPoints) Then Return anchor
            Dim pair = KelvinAndTintOf(anchor)
            If Not Double.IsFinite(pair.Kelvin) OrElse pair.Kelvin <= 0.0 OrElse Not Double.IsFinite(pair.Tint) Then Return anchor
            Dim targetMired = 1000000.0 / pair.Kelvin - miredShift
            If targetMired < 1000000.0 / 50000.0 Then targetMired = 1000000.0 / 50000.0
            If targetMired > 1000000.0 / 1000.0 Then targetMired = 1000000.0 / 1000.0
            Dim candidate = FromKelvinAndTint(1000000.0 / targetMired, pair.Tint + tintPoints)
            Return If(candidate.IsValid, candidate, anchor)
        End Function

        ' ── Die fruehere Rechnung, nur noch fuer die Umrechnung alter Rezepte ───

        ''' <summary>Wie viel v (CIE 1960) ein Tönungspunkt in der früheren Rechnung verschob.</summary>
        Private Const LegacyTintPerPoint As Double = 0.0001

        ''' <summary>Der Weißpunkt zu Temperatur und Tönung in der FRÜHEREN Rechnung (Weißabgleich
        ''' Modell 2): Kurve aus Planck und Tageslicht, Tönung 0,0001 v je Punkt. Nur für
        ''' <c>ImageProcessor.UpgradeWhiteBalanceModel</c>, das alte Rezepte auf denselben
        ''' Weißpunkt in der heutigen Rechnung umschreibt.</summary>
        Friend Shared Function LegacyFromKelvinAndTint(kelvin As Double, tint As Double) As WhitePoint
            If Not Double.IsFinite(kelvin) OrElse kelvin <= 0.0 Then Return D65
            Dim locus = LocusPoint(kelvin)
            If Not locus.IsValid Then Return D65
            If Not Double.IsFinite(tint) OrElse tint = 0.0 Then Return locus
            Return LegacyShiftTint(locus, tint)
        End Function

        ''' <summary>Die Tönung der früheren Rechnung: quer zur Kurve in v, mit Rücknahme in
        ''' Zehnteln am Rand des Sichtbaren.</summary>
        Private Shared Function LegacyShiftTint(locus As WhitePoint, tint As Double) As WhitePoint
            Dim uv = ToCie1960(locus)
            If Double.IsNaN(uv.U) Then Return locus
            Dim wanted = tint * LegacyTintPerPoint
            For attempt = 0 To 20
                Dim factor = Math.Pow(0.9, attempt)
                Dim candidate = FromCie1960(New Cie1960(uv.U, uv.V + wanted * factor))
                If candidate.IsValid AndAlso candidate.X + candidate.Y < 1.0 AndAlso HasPositiveConeResponse(candidate) Then
                    Return candidate
                End If
            Next
            Return locus
        End Function

        ''' <summary>Die Verschiebung vom Anker in der FRÜHEREN Rechnung: um die Differenz zweier
        ''' Kurvenpunkte, danach die Tönung in v. Nur für die Umrechnung alter Rezepte.</summary>
        Friend Shared Function LegacyShiftFromAnchor(anchor As WhitePoint, miredShift As Double,
                                                     tintPoints As Double) As WhitePoint
            If Not anchor.IsValid Then Return D65
            If miredShift = 0.0 AndAlso tintPoints = 0.0 Then Return anchor
            If Not Double.IsFinite(miredShift) OrElse Not Double.IsFinite(tintPoints) Then Return anchor

            Dim shifted = anchor
            If miredShift <> 0.0 Then
                ' Die Temperatur des Ankers wird nur als STÜTZSTELLE gebraucht, nicht als Ergebnis.
                Dim anchorKelvin = CaptureWhiteBalanceService.CorrelatedColorTemperature(anchor.X, anchor.Y)
                If Double.IsNaN(anchorKelvin) OrElse anchorKelvin <= 0.0 Then anchorKelvin = 6504.0
                Dim anchorMired = 1000000.0 / anchorKelvin
                Dim targetMired = anchorMired - miredShift
                If targetMired < 1000000.0 / 50000.0 Then targetMired = 1000000.0 / 50000.0
                If targetMired > 1000000.0 / 1000.0 Then targetMired = 1000000.0 / 1000.0
                Dim from = LocusPoint(anchorKelvin)
                Dim upto = LocusPoint(1000000.0 / targetMired)
                If from.IsValid AndAlso upto.IsValid Then
                    Dim fromUv = ToCie1960(from)
                    Dim uptoUv = ToCie1960(upto)
                    Dim anchorUv = ToCie1960(anchor)
                    If Not Double.IsNaN(fromUv.U) AndAlso Not Double.IsNaN(uptoUv.U) AndAlso Not Double.IsNaN(anchorUv.U) Then
                        Dim candidate = FromCie1960(New Cie1960(anchorUv.U + (uptoUv.U - fromUv.U),
                                                                anchorUv.V + (uptoUv.V - fromUv.V)))
                        If candidate.IsValid AndAlso candidate.X + candidate.Y < 1.0 AndAlso HasPositiveConeResponse(candidate) Then
                            shifted = candidate
                        End If
                    End If
                End If
            End If

            If tintPoints <> 0.0 Then shifted = LegacyShiftTint(shifted, tintPoints)
            Return shifted
        End Function

        ' ── Die Pipette: von einem Bildpunkt zurück auf den Weißpunkt ────────────

        ''' <summary>Der behauptete Weißpunkt, unter dem ein Bildpunkt neutral wird, der jetzt
        ''' <paramref name="displayed"/> zeigt. Angegeben wird der Bildpunkt so, wie er auf dem
        ''' Schirm steht, als LINEARES sRGB - der Aufrufer dekodiert also vorher das Gamma.
        '''
        ''' <para>DIE HERLEITUNG in einer Zeile. Die Kette verstärkt im Zapfenraum mit
        ''' Zapfen(Anker)/Zapfen(Behauptung). Damit der Bildpunkt neutral wird, muss sie zusätzlich
        ''' mit Zapfen(D65)/Zapfen(Bildpunkt) verstärken; beides zusammengefasst ergibt die neue
        ''' Behauptung Zapfen(Behauptung) mal Zapfen(Bildpunkt) durch Zapfen(D65). Neutral heißt
        ''' dabei D65, weil das der Weißpunkt von sRGB ist. Der Anker steht nicht in der Formel -
        ''' er kürzt sich heraus.</para>
        '''
        ''' <para>WELCHEN BILDPUNKT DER AUFRUFER GEBEN MUSS: den, den die Adaptionsstufe SIEHT.
        ''' Wer <paramref name="claimed"/> auf den Anker stellt, gibt den Bildpunkt ohne jede
        ''' Farbbearbeitung und bekommt den Weißpunkt in einem Zug; wer einen bereits
        ''' abgeglichenen Bildpunkt gibt, muss dazu passend den geltenden behaupteten Weißpunkt
        ''' nennen. Ein Bildpunkt vom SCHIRM passt zu keinem von beidem: Tonwertkurve und
        ''' Sättigung stehen hinter der Stufe und verschieben den Farbort noch einmal. Gemessen
        ''' hat das die Pipettenprobe des Prüfstands (whitebalanceprobe --pick).</para>
        '''
        ''' <para>Klickt man auf eine Stelle, die schon neutral ist, kommt
        ''' <paramref name="claimed"/> unverändert zurück - das ist die Probe darauf.</para></summary>
        Public Shared Function NeutralizeSample(claimed As WhitePoint, displayed As Double()) As WhitePoint
            If displayed Is Nothing OrElse displayed.Length < 3 OrElse Not claimed.IsValid Then Return claimed

            Dim sampleCone = ApplyMatrix(Bradford, ApplyMatrix(LinearSrgbToXyz, displayed))
            Dim claimedCone = ConeResponse(claimed)
            Dim neutralCone = ConeResponse(D65)

            Dim wanted(2) As Double
            For i = 0 To 2
                ' Ein Kanal auf null trägt keine Farbe, und der Bruch wäre keine Zahl. Schwarz und
                ' ausgefressenes Weiß fängt der Aufrufer schon vorher ab; hier steht der Riegel für
                ' alles, was trotzdem durchkommt.
                If Not Double.IsFinite(sampleCone(i)) OrElse sampleCone(i) <= 0.0 Then Return claimed
                If Not Double.IsFinite(claimedCone(i)) OrElse claimedCone(i) <= 0.0 Then Return claimed
                wanted(i) = claimedCone(i) * sampleCone(i) / neutralCone(i)
            Next

            Dim xyz = ApplyMatrix(BradfordInverse, wanted)
            Dim sum = xyz(0) + xyz(1) + xyz(2)
            If Not Double.IsFinite(sum) OrElse sum <= 0.0 Then Return claimed
            Dim candidate = New WhitePoint(xyz(0) / sum, xyz(1) / sum)
            If Not candidate.IsValid OrElse candidate.X + candidate.Y >= 1.0 Then Return claimed
            If Not HasPositiveConeResponse(candidate) Then Return claimed
            Return candidate
        End Function

        ''' <summary>Die Temperatur zu einem Farbort, geklemmt auf den Reglerbereich.
        '''
        ''' <para>DIE GENAUE UMKEHRUNG von <see cref="FromKelvinAndTint"/>, nach der
        ''' DNG-Spezifikation (<see cref="KelvinAndTintOf"/>): die Temperatur der Isotherme, auf
        ''' der der Farbort liegt. Die Tönung dazu liefert <see cref="TintPointsOf"/>; Kelvin-Zahl
        ''' und Tönung ergeben zusammen wieder genau den Farbort, solange die Zahl nicht geklemmt
        ''' ist.</para></summary>
        Public Shared Function KelvinOf(point As WhitePoint) As Double
            If Not point.IsValid Then Return CaptureWhiteBalanceService.ReferenceKelvin
            Dim kelvin = KelvinAndTintOf(point).Kelvin
            If Not Double.IsFinite(kelvin) OrElse kelvin <= 0.0 Then Return CaptureWhiteBalanceService.ReferenceKelvin
            Return Math.Max(MinKelvin, Math.Min(MaxKelvin, kelvin))
        End Function

        ''' <summary>Die Tönung zu einem Farbort, in Reglerpunkten, bezogen auf die Isotherme von
        ''' <paramref name="kelvin"/>: der Abstand vom Punkt der Planckschen Linie, entlang der
        ''' Isotherme gemessen. Liegt der Farbort auf dieser Isotherme (die Temperatur kam aus
        ''' <see cref="KelvinOf"/> und ist nicht geklemmt), ist das genau die Tönung der
        ''' DNG-Spezifikation. Die Temperatur wird mitgegeben, statt sie hier noch einmal zu
        ''' bestimmen; der Aufrufer hat sie ohnehin schon.</summary>
        Public Shared Function TintPointsOf(point As WhitePoint, kelvin As Double) As Double
            If Not point.IsValid OrElse Not Double.IsFinite(kelvin) OrElse kelvin <= 0.0 Then Return 0.0
            Dim iso = IsothermAt(kelvin)
            Dim pointUv = ToCie1960(point)
            If Double.IsNaN(iso.U) OrElse Double.IsNaN(pointUv.U) Then Return 0.0
            Return ((pointUv.U - iso.U) * iso.Du + (pointUv.V - iso.V) * iso.Dv) * TintScale
        End Function

        ''' <summary>Matrix mal Vektor, beides mit drei Gliedern.</summary>
        Private Shared Function ApplyMatrix(m As Double(), v As Double()) As Double()
            Return New Double(2) {
                m(0) * v(0) + m(1) * v(1) + m(2) * v(2),
                m(3) * v(0) + m(4) * v(1) + m(5) * v(2),
                m(6) * v(0) + m(7) * v(1) + m(8) * v(2)
            }
        End Function

        ''' <summary>Der Farbort auf der Kurve der FRÜHEREN Rechnung, ohne Tönung: unterhalb von
        ''' 4000 K Planck, oberhalb von 4500 K die Tageslichtreihe, dazwischen ein Smoothstep. Nur
        ''' noch für die Umrechnung alter Rezepte.</summary>
        Private Shared Function LocusPoint(kelvin As Double) As WhitePoint
            Const planckOnlyBelow As Double = 4000.0
            Const daylightOnlyAbove As Double = 4500.0

            If kelvin <= planckOnlyBelow Then Return PlanckianPoint(kelvin)
            If kelvin >= daylightOnlyAbove Then Return DaylightPoint(kelvin)

            Dim q = (kelvin - planckOnlyBelow) / (daylightOnlyAbove - planckOnlyBelow)
            Dim w = q * q * (3.0 - 2.0 * q)
            Dim a = PlanckianPoint(kelvin)
            Dim b = DaylightPoint(kelvin)
            If Not a.IsValid Then Return b
            If Not b.IsValid Then Return a
            Return New WhitePoint(a.X + (b.X - a.X) * w, a.Y + (b.Y - a.Y) * w)
        End Function

        ''' <summary>Farbort eines Planckschen Strahlers, Näherung nach Krystek (1985), gerechnet in
        ''' der CIE-1960-Ebene.
        '''
        ''' ACHTUNG BEIM NENNER von v: das lineare Glied ist NEGATIV, das quadratische positiv.
        ''' Wer beide gleich vorzeichnet, bekommt Farborte, die noch plausibel aussehen, aber
        ''' systematisch daneben liegen. Die Probe dagegen ist Normlicht A bei 2856 K mit
        ''' u = 0,2560 und v = 0,3495 (siehe <see cref="SelfCheck"/>).</summary>
        Private Shared Function PlanckianPoint(kelvin As Double) As WhitePoint
            Dim t = Math.Max(1000.0, Math.Min(15000.0, kelvin))
            Dim t2 = t * t
            Dim u = (0.860117757 + 0.000154118254 * t + 0.000000128641212 * t2) /
                    (1.0 + 0.000842420235 * t + 0.000000708145163 * t2)
            Dim v = (0.317398726 + 0.0000422806245 * t + 0.0000000420481691 * t2) /
                    (1.0 - 0.0000289741816 * t + 0.000000161456053 * t2)
            Return FromCie1960(New Cie1960(u, v))
        End Function

        ''' <summary>Farbort der genormten Tageslichtreihe (CIE D). Zwei Abschnitte für x, ein
        ''' Polynom für y.</summary>
        Private Shared Function DaylightPoint(kelvin As Double) As WhitePoint
            Dim t = Math.Max(4000.0, Math.Min(25000.0, kelvin))
            Dim x As Double
            If t <= 7000.0 Then
                x = -4.6070E+09 / (t * t * t) + 2.9678E+06 / (t * t) + 99.11 / t + 0.244063
            Else
                x = -2.0064E+09 / (t * t * t) + 1.9018E+06 / (t * t) + 247.48 / t + 0.237040
            End If
            Return New WhitePoint(x, -3.0 * x * x + 2.87 * x - 0.275)
        End Function

        ' ── CIE 1960 hin und zurück ──────────────────────────────────────────────

        Private Structure Cie1960
            Public ReadOnly U As Double
            Public ReadOnly V As Double
            Public Sub New(u As Double, v As Double)
                Me.U = u
                Me.V = v
            End Sub
        End Structure

        Private Shared Function ToCie1960(point As WhitePoint) As Cie1960
            Dim denominator = -2.0 * point.X + 12.0 * point.Y + 3.0
            If Math.Abs(denominator) < 0.000001 Then Return New Cie1960(Double.NaN, Double.NaN)
            Return New Cie1960(4.0 * point.X / denominator, 6.0 * point.Y / denominator)
        End Function

        Private Shared Function FromCie1960(uv As Cie1960) As WhitePoint
            Dim denominator = 2.0 * uv.U - 8.0 * uv.V + 4.0
            If Math.Abs(denominator) < 0.000001 Then Return New WhitePoint(Double.NaN, Double.NaN)
            Return New WhitePoint(3.0 * uv.U / denominator, 2.0 * uv.V / denominator)
        End Function

        ' ── Die Adaptionsmatrix ──────────────────────────────────────────────────

        ''' <summary>Die 3x3-Matrix für LINEARES sRGB, zeilenweise, die das behauptete Licht auf den
        ''' Anker abbildet. Nothing, wenn nichts zu tun ist - das ist der häufigste Fall und der
        ''' Aufrufer soll dann keine Stufe rechnen.</summary>
        Public Shared Function BuildMatrix(anchor As WhitePoint, claimed As WhitePoint) As Double()
            If Not anchor.IsValid OrElse Not claimed.IsValid Then Return Nothing
            ' Genau gleich heißt: nichts tun, und zwar ohne Rundungsrest.
            If Math.Abs(anchor.X - claimed.X) < 0.0000005 AndAlso Math.Abs(anchor.Y - claimed.Y) < 0.0000005 Then
                Return Nothing
            End If

            Dim anchorCone = ConeResponse(anchor)
            Dim claimedCone = ConeResponse(claimed)
            For i = 0 To 2
                If Not Double.IsFinite(anchorCone(i)) OrElse Not Double.IsFinite(claimedCone(i)) Then Return Nothing
                If claimedCone(i) <= 0.0 Then Return Nothing
            Next

            ' diag(Anker/Behauptung) im Zapfenraum, eingebettet in Bradford und sRGB.
            Dim gain = New Double(2) {anchorCone(0) / claimedCone(0),
                                      anchorCone(1) / claimedCone(1),
                                      anchorCone(2) / claimedCone(2)}

            Dim m = Multiply(BradfordInverse, ScaleRows(Bradford, gain))
            Return NormalizeLuminance(Multiply(XyzToLinearSrgb, Multiply(m, LinearSrgbToXyz)))
        End Function

        ''' <summary>Wie <see cref="BuildMatrix(WhitePoint, WhitePoint)"/>, aber mit den beiden
        ''' Kameramatrizen der Aufnahme: das Bild wird so umgerechnet, als hätte der Decode es mit
        ''' dem behaupteten Licht entwickelt, samt der dafür gemischten Kameramatrix
        ''' (<see cref="CameraIlluminantMatrixTable.RebalanceMatrix"/>). Ohne Kamera der reine
        ''' Bradford-Weg.</summary>
        Public Shared Function BuildMatrix(anchor As WhitePoint, claimed As WhitePoint,
                                           camera As CameraIlluminantMatrixTable.Entry) As Double()
            If camera Is Nothing Then Return BuildMatrix(anchor, claimed)
            If Not anchor.IsValid OrElse Not claimed.IsValid Then Return Nothing
            If Math.Abs(anchor.X - claimed.X) < 0.0000005 AndAlso Math.Abs(anchor.Y - claimed.Y) < 0.0000005 Then
                Return Nothing
            End If
            If Not HasPositiveConeResponse(claimed) Then Return Nothing
            Dim m = CameraIlluminantMatrixTable.RebalanceMatrix(camera, anchor.X, anchor.Y, claimed.X, claimed.Y)
            If m Is Nothing Then Return BuildMatrix(anchor, claimed)
            For i = 0 To 8
                If Not Double.IsFinite(m(i)) Then Return BuildMatrix(anchor, claimed)
            Next
            Return NormalizeLuminance(m)
        End Function

        ''' <summary>Wie <see cref="NeutralizeSample(WhitePoint, Double())"/>, mit den Kameramatrizen
        ''' der Aufnahme: der Bildpunkt wird im Kameraraum als neutral genommen und wie ein
        ''' Aufnahmeweiss gelöst (<see cref="CameraIlluminantMatrixTable.NeutralWhite"/>). So trifft
        ''' die Pipette in einem Zug, auch wenn die Kette die Matrix mit dem Licht wechselt.
        ''' <paramref name="anchor"/> ist das Licht, mit dem <paramref name="displayed"/> entwickelt
        ''' ist; ohne Kamera der reine Bradford-Weg.</summary>
        Public Shared Function NeutralizeSample(anchor As WhitePoint, displayed As Double(),
                                                camera As CameraIlluminantMatrixTable.Entry) As WhitePoint
            If camera Is Nothing Then Return NeutralizeSample(anchor, displayed)
            If displayed Is Nothing OrElse displayed.Length < 3 OrElse Not anchor.IsValid Then Return anchor
            Dim solution = CameraIlluminantMatrixTable.NeutralWhite(camera, anchor.X, anchor.Y, displayed)
            If solution Is Nothing Then Return NeutralizeSample(anchor, displayed)
            Dim candidate = New WhitePoint(solution.WhiteX, solution.WhiteY)
            If Not candidate.IsValid OrElse candidate.X + candidate.Y >= 1.0 OrElse Not HasPositiveConeResponse(candidate) Then Return anchor
            Return candidate
        End Function

        ''' <summary>Zieht die Matrix so, dass Weiß seine Luminanz behält.
        '''
        ''' WARUM ÜBERHAUPT: eine reine Adaption hält die Luminanz nur für DEN Weißpunkt, auf den
        ''' sie abbildet - nicht für das Grau, das in den Bildpunkten steht. Gemessen hob die
        ''' Rechnung ohne diesen Schritt ein neutrales Grau bei 3200 K um 7 Prozent an und bei
        ''' 2000 K um 46. Der Weißabgleich wäre damit ein zweiter Belichtungsregler, und jeder
        ''' Griff daran hätte die Tonwertkurve mitverschoben. Die Adaption bleibt dabei
        ''' unverändert: ein gemeinsamer Faktor auf allen neun Gliedern ändert die Farbe nicht,
        ''' nur die Menge.</summary>
        Private Shared Function NormalizeLuminance(m As Double()) As Double()
            Dim whiteR = m(0) + m(1) + m(2)
            Dim whiteG = m(3) + m(4) + m(5)
            Dim whiteB = m(6) + m(7) + m(8)
            Dim luminance = 0.2126729 * whiteR + 0.7151522 * whiteG + 0.0721750 * whiteB
            If Not Double.IsFinite(luminance) OrElse luminance <= 0.000001 Then Return m
            For i = 0 To 8
                m(i) /= luminance
            Next
            Return m
        End Function

        ''' <summary>Zapfenantworten (Bradford) auf den Weißpunkt, auf Y = 1 normiert.</summary>
        Private Shared Function ConeResponse(point As WhitePoint) As Double()
            Dim bigX = point.X / point.Y
            Dim bigY = 1.0
            Dim bigZ = (1.0 - point.X - point.Y) / point.Y
            Return New Double(2) {
                Bradford(0) * bigX + Bradford(1) * bigY + Bradford(2) * bigZ,
                Bradford(3) * bigX + Bradford(4) * bigY + Bradford(5) * bigZ,
                Bradford(6) * bigX + Bradford(7) * bigY + Bradford(8) * bigZ
            }
        End Function

        ''' Bradford-Anpassung: die verbreitete Wahl, und die, die Adobe im DNG-Weg benutzt.
        Private Shared ReadOnly Bradford As Double() = {
            0.8951, 0.2664, -0.1614,
            -0.7502, 1.7135, 0.0367,
            0.0389, -0.0685, 1.0296
        }

        Private Shared ReadOnly BradfordInverse As Double() = {
            0.9869929, -0.1470543, 0.1599627,
            0.4323053, 0.5183603, 0.0492912,
            -0.0085287, 0.0400428, 0.9684867
        }

        Private Shared ReadOnly LinearSrgbToXyz As Double() = {
            0.4124564, 0.3575761, 0.1804375,
            0.2126729, 0.7151522, 0.0721750,
            0.0193339, 0.1191920, 0.9503041
        }

        Private Shared ReadOnly XyzToLinearSrgb As Double() = {
            3.2404542, -1.5371385, -0.4985314,
            -0.9692660, 1.8760108, 0.0415560,
            0.0556434, -0.2040259, 1.0572252
        }

        Private Shared Function Multiply(a As Double(), b As Double()) As Double()
            Dim result(8) As Double
            For row = 0 To 2
                For column = 0 To 2
                    Dim sum = 0.0
                    For k = 0 To 2
                        sum += a(row * 3 + k) * b(k * 3 + column)
                    Next
                    result(row * 3 + column) = sum
                Next
            Next
            Return result
        End Function

        ''' <summary>diag(scale) mal Matrix, also jede ZEILE mit ihrem Faktor.</summary>
        Private Shared Function ScaleRows(m As Double(), scale As Double()) As Double()
            Dim result(8) As Double
            For row = 0 To 2
                For column = 0 To 2
                    result(row * 3 + column) = m(row * 3 + column) * scale(row)
                Next
            Next
            Return result
        End Function

        ' ── Proben ───────────────────────────────────────────────────────────────

        ''' <summary>Eine Zeile Prüfergebnis: was geprüft wurde, was herauskam, was herauskommen
        ''' muss, und ob es passt.</summary>
        Public NotInheritable Class CheckResult
            Public Property Name As String
            Public Property Value As Double
            Public Property Expected As Double
            Public Property Tolerance As Double
            Public ReadOnly Property Passed As Boolean
                Get
                    Return Double.IsFinite(Value) AndAlso Math.Abs(Value - Expected) <= Tolerance
                End Get
            End Property
        End Class

        ''' <summary>Die Proben auf diese Rechnung, mit Sollwerten aus der Literatur und aus
        ''' Eigenschaften, die die Rechnung von sich aus haben muss. Sie stehen hier und nicht im
        ''' Prüfstand, damit sie mit der Rechnung zusammen wandern.</summary>
        Public Shared Function SelfCheck() As CheckResult()
            Dim results As New List(Of CheckResult)

            ' 1. Normlicht A: 2856 K, bekannter Farbort u = 0,2560 / v = 0,3495 (CIE 1960).
            Dim illuminantA = PlanckianPoint(2856.0)
            Dim uvA = ToCie1960(illuminantA)
            results.Add(New CheckResult With {.Name = "Normlicht A, u", .Value = uvA.U, .Expected = 0.2560, .Tolerance = 0.0005})
            results.Add(New CheckResult With {.Name = "Normlicht A, v", .Value = uvA.V, .Expected = 0.3495, .Tolerance = 0.0005})

            ' 2. Die Tageslichtreihe bei 6504 K MUSS auf D65 fallen - das ist ihre Definition.
            Dim d65 = DaylightPoint(6504.0)
            results.Add(New CheckResult With {.Name = "Tageslicht 6504 K, x", .Value = d65.X, .Expected = D65.X, .Tolerance = 0.0015})
            results.Add(New CheckResult With {.Name = "Tageslicht 6504 K, y", .Value = d65.Y, .Expected = D65.Y, .Tolerance = 0.0015})

            ' 2a. Die DNG-Rechnung trifft Normlicht A ebenso, und Tönung 0 liegt auf Planck.
            Dim dngA = ToCie1960(FromKelvinAndTint(2856.0, 0.0))
            results.Add(New CheckResult With {.Name = "DNG 2856 K, u", .Value = dngA.U, .Expected = 0.256, .Tolerance = 0.0005})
            results.Add(New CheckResult With {.Name = "DNG 2856 K, v", .Value = dngA.V, .Expected = 0.3495, .Tolerance = 0.0005})

            ' 2b. Hin und zurück über Kelvin und Tönung muss genau dieselben Zahlen ergeben. Nur
            '     mit Tönungen, die überall rechenbar sind: bei 2500 K und +60 läge der Weißpunkt
            '     ausserhalb, und die Rücknahme in FromKelvinAndTint liefert gewollt weniger.
            Dim worstPair = 0.0
            For Each kelvin In {2500.0, 3200.0, 5500.0, 9000.0}
                For Each tint In {-80.0, 0.0, 40.0}
                    Dim pair = KelvinAndTintOf(FromKelvinAndTint(kelvin, tint))
                    worstPair = Math.Max(worstPair, Math.Max(Math.Abs(pair.Kelvin - kelvin) / kelvin * 1000.0, Math.Abs(pair.Tint - tint)))
                Next
            Next
            results.Add(New CheckResult With {.Name = "DNG Kelvin und Tönung hin und zurück", .Value = worstPair, .Expected = 0.0, .Tolerance = 0.01})

            ' 2c. Positive Tönung behauptet grüneres Licht (größeres v), das Bild wird magenta.
            Dim greener = ToCie1960(FromKelvinAndTint(5500.0, 50.0)).V - ToCie1960(FromKelvinAndTint(5500.0, 0.0)).V
            results.Add(New CheckResult With {.Name = "Tönung +50: Behauptung grüner", .Value = If(greener > 0.0, 1.0, 0.0), .Expected = 1.0, .Tolerance = 0.0})

            ' 3. Anker gleich Behauptung: keine Matrix, nicht bloß eine fast neutrale.
            Dim identity = BuildMatrix(D65, D65)
            results.Add(New CheckResult With {.Name = "Anker = Behauptung: keine Stufe",
                                              .Value = If(identity Is Nothing, 0.0, 1.0), .Expected = 0.0, .Tolerance = 0.0})

            ' 4. Eine echte Adaption: Anker D65, Behauptung 5000 K. Die Behauptung ist WÄRMER als
            '    der Anker, das Bild muss also KÜHLER werden - die Matrix zieht Rot zurück und hebt
            '    Blau. Geprüft an der Wirkung auf Weiß, nicht an einzelnen Matrixgliedern: das ist
            '    die Aussage, auf die es ankommt.
            Dim cooling = BuildMatrix(D65, FromKelvinAndTint(5000.0, 0.0))
            If cooling Is Nothing Then
                results.Add(New CheckResult With {.Name = "Adaption 5000 K auf D65 vorhanden", .Value = 0.0, .Expected = 1.0, .Tolerance = 0.0})
            Else
                Dim red = cooling(0) + cooling(1) + cooling(2)
                Dim blue = cooling(6) + cooling(7) + cooling(8)
                results.Add(New CheckResult With {.Name = "5000 K auf D65: Rot unter 1", .Value = If(red < 1.0, 1.0, 0.0), .Expected = 1.0, .Tolerance = 0.0})
                results.Add(New CheckResult With {.Name = "5000 K auf D65: Blau über 1", .Value = If(blue > 1.0, 1.0, 0.0), .Expected = 1.0, .Tolerance = 0.0})
                ' Und die Gegenrichtung muss sie zurückholen. NICHT als Einheitsmatrix: weil jede
                ' Richtung für sich auf gleiche Luminanz gezogen wird, bleibt hin und zurück ein
                ' gemeinsamer FAKTOR übrig. Zu prüfen ist deshalb genau das, was zählt - dass
                ' keine FARBE hängen bleibt: die Nebendiagonale muss null sein und die drei
                ' Diagonalglieder müssen untereinander gleich sein. Der Faktor selbst steht als
                ' eigene, weiche Probe daneben.
                Dim back = BuildMatrix(FromKelvinAndTint(5000.0, 0.0), D65)
                If back Is Nothing Then
                    results.Add(New CheckResult With {.Name = "Rückrichtung vorhanden", .Value = 0.0, .Expected = 1.0, .Tolerance = 0.0})
                Else
                    Dim roundTrip = Multiply(back, cooling)
                    Dim worstOff = 0.0
                    For i = 0 To 8
                        If i Mod 4 <> 0 Then worstOff = Math.Max(worstOff, Math.Abs(roundTrip(i)))
                    Next
                    Dim spread = Math.Max(Math.Abs(roundTrip(0) - roundTrip(4)),
                                          Math.Abs(roundTrip(0) - roundTrip(8)))
                    results.Add(New CheckResult With {.Name = "hin und zurück: keine Farbe übrig", .Value = worstOff, .Expected = 0.0, .Tolerance = 0.000001})
                    results.Add(New CheckResult With {.Name = "hin und zurück: reiner Faktor", .Value = spread, .Expected = 0.0, .Tolerance = 0.000001})
                    results.Add(New CheckResult With {.Name = "hin und zurück: Faktor nahe 1", .Value = roundTrip(0), .Expected = 1.0, .Tolerance = 0.01})
                End If
            End If

            ' 5. Relativer Regler auf 0: der Anker muss UNVERÄNDERT zurückkommen, auch wenn er
            '    neben der Kurve liegt. Geprüft mit einem bewusst schief liegenden Anker.
            Dim offLocus = New WhitePoint(0.3400, 0.3400)
            Dim unmoved = ShiftFromAnchor(offLocus, 0.0, 0.0)
            results.Add(New CheckResult With {.Name = "Regler 0 lässt den Anker liegen",
                                              .Value = Math.Abs(unmoved.X - offLocus.X) + Math.Abs(unmoved.Y - offLocus.Y),
                                              .Expected = 0.0, .Tolerance = 0.0})
            Dim noStage = BuildMatrix(offLocus, unmoved)
            results.Add(New CheckResult With {.Name = "Regler 0 baut keine Stufe",
                                              .Value = If(noStage Is Nothing, 0.0, 1.0), .Expected = 0.0, .Tolerance = 0.0})

            ' 6. Und ein Ausschlag muss in die richtige Richtung gehen: positiv heißt wärmer, also
            '    mehr Rot. Am schief liegenden Anker, damit die Verschiebung auch dort trägt.
            Dim warmer = BuildMatrix(offLocus, ShiftFromAnchor(offLocus, 20.0, 0.0))
            If warmer Is Nothing Then
                results.Add(New CheckResult With {.Name = "Ausschlag +20 mired wirkt", .Value = 0.0, .Expected = 1.0, .Tolerance = 0.0})
            Else
                Dim red = warmer(0) + warmer(1) + warmer(2)
                results.Add(New CheckResult With {.Name = "+20 mired: Rot über 1", .Value = If(red > 1.0, 1.0, 0.0), .Expected = 1.0, .Tolerance = 0.0})
            End If

            ' 7. Grauer Bildpunkt unter der Adaption: die Helligkeit darf nicht weglaufen. Y ist die
            '    Luminanzzeile der sRGB-Matrix; eine Adaption verschiebt die Farbe, nicht die Menge.
            Dim toneKeep = BuildMatrix(D65, FromKelvinAndTint(3200.0, 0.0))
            If toneKeep IsNot Nothing Then
                Dim r = toneKeep(0) + toneKeep(1) + toneKeep(2)
                Dim g = toneKeep(3) + toneKeep(4) + toneKeep(5)
                Dim b = toneKeep(6) + toneKeep(7) + toneKeep(8)
                Dim luminance = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b
                results.Add(New CheckResult With {.Name = "Weiß behält seine Luminanz", .Value = luminance, .Expected = 1.0, .Tolerance = 0.02})
            End If

            Return results.ToArray()
        End Function

    End Class

End Namespace
