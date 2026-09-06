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

        ''' <summary>Wie viel v (CIE 1960) ein Tönungspunkt verschiebt. Adobes Tönung läuft von
        ''' -150 bis +150; der Faktor hier ist NICHT auf Adobes Skala geeicht, sondern so gewählt,
        ''' dass ±150 ein sichtbarer, aber noch physikalischer Farbstich ist. Die Eichung gegen
        ''' Adobe ist eine eigene Messung und steht noch aus.
        '''
        ''' GEMESSENE OBERGRENZE: mit 0,0004 je Punkt liegt der Weißpunkt bei +150 ausserhalb des
        ''' Sichtbaren (bei 5500 K auf y = 0,558), die Zapfenantwort wird negativ und es gäbe gar
        ''' keine Matrix mehr. Ein Regler, der am Ende nicht mehr wirkt, ist schlimmer als ein
        ''' schwächerer - deshalb dieser Wert, und zusätzlich die Rücknahme in
        ''' <see cref="ShiftTint"/> für die Ränder des Kelvin-Bereichs.</summary>
        Private Const TintPerPoint As Double = 0.0001

        ' ── Farbort aus Kelvin und Tönung ────────────────────────────────────────

        ''' <summary>Der Weißpunkt zu einer Temperatur und einer Tönung.
        '''
        ''' Zwei Kurven: unterhalb von 4000 K der Plancksche Strahler, oberhalb von 4500 K die
        ''' genormte Tageslichtreihe, dazwischen überblendet mit einem Smoothstep. Der Smoothstep
        ''' und nicht eine Gerade, weil seine Steigung an beiden Enden null ist - damit hängen nicht
        ''' nur die Werte der beiden Kurven zusammen, sondern auch ihre Steigungen, und der Regler
        ''' läuft ohne Knick durch den Übergang.
        '''
        ''' Die Tönung verschiebt danach quer zur Kurve, in v der CIE-1960-Ebene: positiv nach
        ''' Magenta, negativ nach Grün. Quer zur Kurve ist nur dort vergleichbar; in x/y hinge der
        ''' gleiche Zahlenwert von der Temperatur ab.</summary>
        Public Shared Function FromKelvinAndTint(kelvin As Double, tint As Double) As WhitePoint
            If Not Double.IsFinite(kelvin) OrElse kelvin <= 0.0 Then Return D65
            Dim locus = LocusPoint(kelvin)
            If Not locus.IsValid Then Return D65
            If Not Double.IsFinite(tint) OrElse tint = 0.0 Then Return locus
            Return ShiftTint(locus, tint)
        End Function

        ''' <summary>Verschiebt den Farbort quer zur Kurve, und nimmt die Verschiebung so weit
        ''' zurück, dass ein rechenbarer Weißpunkt herauskommt.
        '''
        ''' Ohne diese Rücknahme läuft der Regler an den Rändern des Kelvin-Bereichs aus dem
        ''' Sichtbaren heraus: bei 2000 K liegt die Kurve schon nahe am Rand, und ein voller
        ''' Tönungsausschlag schiebt sie darüber. Die Folge wäre keine sichtbare Grenze, sondern
        ''' eine Stufe, die stillschweigend nichts mehr tut. Zurückgenommen wird in Zehnteln, damit
        ''' der Regler auch dort noch so weit trägt, wie es physikalisch geht.</summary>
        Private Shared Function ShiftTint(locus As WhitePoint, tint As Double) As WhitePoint
            Dim uv = ToCie1960(locus)
            If Double.IsNaN(uv.U) Then Return locus
            Dim wanted = tint * TintPerPoint
            For attempt = 0 To 20
                Dim factor = Math.Pow(0.9, attempt)
                Dim candidate = FromCie1960(New Cie1960(uv.U, uv.V + wanted * factor))
                If candidate.IsValid AndAlso candidate.X + candidate.Y < 1.0 AndAlso HasPositiveConeResponse(candidate) Then
                    Return candidate
                End If
            Next
            Return locus
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
        ''' NICHT über den Umweg „Anker in Kelvin, Kelvin plus Versatz, zurück auf die Kurve".
        ''' Der Aufnahme-Weißpunkt liegt fast nie genau auf der Kurve, und dieser Umweg würde ihn
        ''' bei Reglerstellung 0 auf sie ziehen - also einen Farbstich einbauen, ohne dass jemand
        ''' etwas verstellt hat. Verschoben wird deshalb um die DIFFERENZ zweier Kurvenpunkte,
        ''' angewandt auf den Anker selbst. Bei Versatz 0 kommt der Anker unverändert zurück, und
        ''' die Matrix ist dann garantiert die Einheitsmatrix.
        '''
        ''' Vorzeichen wie beim gespeicherten Regler: ein POSITIVER Wert ist wärmer, weil höhere
        ''' Kelvin niedrigere mired haben.</summary>
        Public Shared Function ShiftFromAnchor(anchor As WhitePoint, miredShift As Double,
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

            If tintPoints <> 0.0 Then shifted = ShiftTint(shifted, tintPoints)
            Return shifted
        End Function

        ''' <summary>Der Farbort auf der Kurve, ohne Tönung.</summary>
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
