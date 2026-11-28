Imports System
Imports System.Collections.Generic

Namespace Services

    ''' <summary>Eine Perspektivlinie: eine Strecke an einer Kante, die im Ergebnis senkrecht oder
    ''' waagerecht stehen soll. Die Endpunkte stehen in PROZENT der Verzerrungsstufe (Breite bzw.
    ''' Hoehe), und zwar VOR der offenen Perspektive - also dort, wo die Kante im unentzerrten Bild
    ''' liegt. Das ist dieselbe Form, in der ein spaeteres Rezeptfeld sie fuehren koennte: vier
    ''' Anteile je Strecke, die Rolle nach dem Winkel.</summary>
    Public NotInheritable Class PerspectiveGuideLine
        Public Property X0 As Double
        Public Property Y0 As Double
        Public Property X1 As Double
        Public Property Y1 As Double

        Public Sub New()
        End Sub

        Public Sub New(startX As Double, startY As Double, endX As Double, endY As Double)
            Me.X0 = startX
            Me.Y0 = startY
            Me.X1 = endX
            Me.Y1 = endY
        End Sub

        ''' <summary>Steht die Linie steiler als 45 Grad, gemessen in BILDPUNKTEN? Dann soll sie
        ''' senkrecht werden, sonst waagerecht. In Prozent liesse sich das nicht entscheiden: bei
        ''' einem Querformat ist ein Prozent Breite mehr Bildpunkte als ein Prozent Hoehe.</summary>
        Public Function IsVertical(stageWidth As Double, stageHeight As Double) As Boolean
            Dim dx = Math.Abs(X1 - X0) * stageWidth
            Dim dy = Math.Abs(Y1 - Y0) * stageHeight
            Return dy > dx
        End Function

        Public Function Clone() As PerspectiveGuideLine
            Return New PerspectiveGuideLine(X0, Y0, X1, Y1)
        End Function
    End Class

    ''' <summary>Warum es keine Entzerrung gibt.</summary>
    Public Enum PerspectiveGuideFailure
        None = 0
        ''' <summary>Keine Linie: nichts zu tun, die Ecken stehen bei 0.</summary>
        NoLines = 1
        ''' <summary>Eine Linie ist kaputt: Laenge null, keine Zahl, weit ausserhalb, zu viele.</summary>
        InvalidLine = 2
        ''' <summary>Eine senkrechte und eine waagerechte Linie zeigen fast in dieselbe Richtung.</summary>
        Contradictory = 3
        ''' <summary>Selbst ohne projektiven Anteil laege eine Ecke ausserhalb des erlaubten Bereichs.</summary>
        OutOfRange = 4
    End Enum

    ''' <summary>Das Ergebnis der Rechnung: die acht Eckenversaetze im Format von
    ''' <see cref="ImageGeometryMapper.CornerOffset"/> (Prozent der Stufe, im Uhrzeigersinn ab links
    ''' oben) und dieselbe Abbildung als Matrix in Bildpunkten der Stufe, fuer die Pruefungen.</summary>
    Public NotInheritable Class PerspectiveGuideSolution
        Public Property IsValid As Boolean
        ''' <summary>Die Linien verlangten mehr, als die Begrenzung zulaesst; der projektive Anteil
        ''' wurde um <see cref="LimitFactor"/> zurueckgenommen.</summary>
        Public Property IsLimited As Boolean
        Public Property LimitFactor As Double = 1.0
        Public Property Failure As PerspectiveGuideFailure = PerspectiveGuideFailure.None
        ''' <summary>Acht Werte, Prozent von Breite bzw. Hoehe der Stufe; Nothing, wenn ungueltig.</summary>
        Public Property CornerOffset As Double()
        ''' <summary>Neun Werte, zeilenweise, in Bildpunkten der Stufe: vom unentzerrten ins
        ''' entzerrte Bild. Nothing, wenn ungueltig.</summary>
        Public Property Matrix As Double()

        ''' <summary>Ein Punkt der Stufe (Bildpunkte) durch die Abbildung.</summary>
        Public Function MapPoint(x As Double, y As Double) As (X As Double, Y As Double)
            If Matrix Is Nothing Then Return (x, y)
            Dim w = Matrix(6) * x + Matrix(7) * y + Matrix(8)
            Return ((Matrix(0) * x + Matrix(1) * y + Matrix(2)) / w,
                    (Matrix(3) * x + Matrix(4) * y + Matrix(5)) / w)
        End Function
    End Class

    ''' <summary>Aus zwei bis vier Linien die Homographie, die sie senkrecht und waagerecht stellt.
    '''
    ''' <para>Die Rechnung ist der Lehrbuchweg ueber Fluchtpunkte (projektive Geometrie der Ebene):
    ''' die senkrechten Linien bestimmen einen Fluchtpunkt V, die waagerechten einen Fluchtpunkt H.
    ''' Die Gerade durch beide (die Fluchtlinie) wird ins Unendliche geschickt; danach sind die
    ''' Linien jeder Gruppe parallel. Ein affiner Teil dreht und schert sie dann auf die Achsen.</para>
    '''
    ''' <para>Gerechnet wird normiert: Ursprung in der Bildmitte, geteilt durch die halbe lange Seite.
    ''' Der projektive Teil laesst den Ursprung fest und hat dort die Ableitung 1, der affine Teil
    ''' wird auf Flaeche 1 gebracht. Die Bildmitte behaelt damit Lage und Massstab, und eine kleine
    ''' Aenderung an einer Linie verschiebt Zuschnitt, Masken und Objekte nur wenig.</para>
    '''
    ''' <para>Das Ergebnis passt VERLUSTFREI in die acht Eckenversaetze: eine Homographie hat acht
    ''' Freiheitsgrade, und vier Punktpaare in allgemeiner Lage legen sie eindeutig fest. Die
    ''' Begrenzung haelt alle Nenner an den Ecken positiv, das Zielviereck ist also konvex, und
    ''' <see cref="ImageGeometryMapper.EinheitsquadratAufViereck"/> baut daraus genau diese
    ''' Abbildung zurueck. Rezept und Renderer brauchen dafuer nichts Neues.</para></summary>
    Public NotInheritable Class PerspectiveGuideSolver

        Private Sub New()
        End Sub

        ''' <summary>Hoechstzahl der Linien: je zwei fuer jede Richtung. Mehr legt nichts fest, was
        ''' vier nicht schon festlegen, und es ist die Zahl, die ein Austauschformat dafuer kennt.</summary>
        Public Const MaxLines As Integer = 4

        ''' <summary>Kleinster erlaubter Nenner an einer Bildecke. Unter 0,3 rueckt die Fluchtlinie so
        ''' nah an das Bild, dass eine Ecke ins Unendliche gezogen wuerde.</summary>
        Public Const MinDenominator As Double = 0.3

        ''' <summary>Groesstes erlaubtes Verhaeltnis zweier Nenner: keine Bildkante mehr als
        ''' dreieinhalbmal so lang wie die gegenueberliegende.</summary>
        Public Const MaxDenominatorRatio As Double = 3.5

        ''' <summary>Der Bereich der Eckenversaetze, wie ihn auch das Ziehen einer Ecke kennt.</summary>
        Public Const MaxCornerOffsetPercent As Double = 60.0

        ''' <summary>Kleinste Determinante der beiden Normalen (der Sinus des Winkels zwischen den
        ''' Richtungen). Darunter zeigen eine senkrechte und eine waagerechte Linie fast in dieselbe
        ''' Richtung, und es gibt keine Entzerrung statt einer verrueckten.</summary>
        Public Const MinDeterminant As Double = 0.05

        ''' <summary>Wie weit ein Endpunkt ausserhalb liegen darf, in halben langen Seiten ab Mitte.</summary>
        Private Const MaxNormalizedCoordinate As Double = 10.0

        ''' <summary>Schritte der Halbierungssuche nach dem groessten erlaubten Anteil.</summary>
        Private Const LimitSearchSteps As Integer = 40

        ''' <summary>Loest die Linien. <paramref name="stageWidth"/> und
        ''' <paramref name="stageHeight"/> sind die Masse der Verzerrungsstufe in Bildpunkten.</summary>
        Public Shared Function Solve(lines As IReadOnlyList(Of PerspectiveGuideLine),
                                     stageWidth As Double, stageHeight As Double) As PerspectiveGuideSolution
            If lines Is Nothing OrElse lines.Count = 0 Then Return Failed(PerspectiveGuideFailure.NoLines)
            If lines.Count > MaxLines Then Return Failed(PerspectiveGuideFailure.InvalidLine)
            If Not IsFinite(stageWidth) OrElse Not IsFinite(stageHeight) OrElse
               stageWidth <= 0 OrElse stageHeight <= 0 Then Return Failed(PerspectiveGuideFailure.InvalidLine)

            Dim centerX = stageWidth / 2.0, centerY = stageHeight / 2.0
            Dim radius = Math.Max(stageWidth, stageHeight) / 2.0

            ' Die Linien homogen und normiert, getrennt nach Rolle.
            Dim verticalLines As New List(Of Double())()
            Dim horizontalLines As New List(Of Double())()
            Dim verticalDirections As New List(Of Double())()
            Dim horizontalDirections As New List(Of Double())()
            For Each line In lines
                If line Is Nothing Then Return Failed(PerspectiveGuideFailure.InvalidLine)
                Dim px0 = line.X0 / 100.0 * stageWidth, py0 = line.Y0 / 100.0 * stageHeight
                Dim px1 = line.X1 / 100.0 * stageWidth, py1 = line.Y1 / 100.0 * stageHeight
                If Not (IsFinite(px0) AndAlso IsFinite(py0) AndAlso IsFinite(px1) AndAlso IsFinite(py1)) Then
                    Return Failed(PerspectiveGuideFailure.InvalidLine)
                End If
                Dim u0 = (px0 - centerX) / radius, v0 = (py0 - centerY) / radius
                Dim u1 = (px1 - centerX) / radius, v1 = (py1 - centerY) / radius
                If Math.Abs(u0) > MaxNormalizedCoordinate OrElse Math.Abs(v0) > MaxNormalizedCoordinate OrElse
                   Math.Abs(u1) > MaxNormalizedCoordinate OrElse Math.Abs(v1) > MaxNormalizedCoordinate Then
                    Return Failed(PerspectiveGuideFailure.InvalidLine)
                End If
                Dim du = u1 - u0, dv = v1 - v0
                If Math.Sqrt(du * du + dv * dv) < 0.000001 Then Return Failed(PerspectiveGuideFailure.InvalidLine)
                ' Die Gerade durch beide Punkte: (u0, v0, 1) kreuz (u1, v1, 1), auf (a, b) der
                ' Laenge 1 gebracht. Dann ist ihr Produkt mit einem Punkt (x, y, 1) dessen Abstand.
                Dim l = New Double() {v0 - v1, u1 - u0, u0 * v1 - u1 * v0}
                Dim n = Math.Sqrt(l(0) * l(0) + l(1) * l(1))
                l(0) /= n : l(1) /= n : l(2) /= n
                If line.IsVertical(stageWidth, stageHeight) Then
                    verticalLines.Add(l)
                    verticalDirections.Add(New Double() {du, dv})
                Else
                    horizontalLines.Add(l)
                    horizontalDirections.Add(New Double() {du, dv})
                End If
            Next

            Dim vanishingV = VanishingPoint(verticalLines, verticalDirections)
            Dim vanishingH = VanishingPoint(horizontalLines, horizontalDirections)
            ' Fehlt eine Gruppe, steht die Senkrechte zur Richtung der anderen dafuer, als Punkt im
            ' Unendlichen. Mit einer einzigen Linie bleibt so nur eine Drehung.
            If vanishingV Is Nothing Then
                vanishingV = Normalize3(New Double() {-vanishingH(1), vanishingH(0), 0.0})
            ElseIf vanishingH Is Nothing Then
                vanishingH = Normalize3(New Double() {-vanishingV(1), vanishingV(0), 0.0})
            End If
            If vanishingV Is Nothing OrElse vanishingH Is Nothing Then Return Failed(PerspectiveGuideFailure.InvalidLine)

            ' AFFINER TEIL. Die Richtungen von der Mitte zu den Fluchtpunkten werden zur y- und
            ' x-Achse. Der projektive Teil laesst die ersten beiden Koordinaten eines Punktes
            ' unveraendert; die Richtung zu V ist vor und nach ihm dieselbe, auch bei begrenztem Anteil.
            Dim dvx = vanishingV(0), dvy = vanishingV(1)
            Dim dhx = vanishingH(0), dhy = vanishingH(1)
            Dim lengthV = Math.Sqrt(dvx * dvx + dvy * dvy), lengthH = Math.Sqrt(dhx * dhx + dhy * dhy)
            ' Ein Fluchtpunkt genau in der Bildmitte hat keine Richtung.
            If lengthV < 0.000000001 OrElse lengthH < 0.000000001 Then Return Failed(PerspectiveGuideFailure.InvalidLine)
            ' Die Zeilen der Matrix sind die Normalen: n1 senkrecht zu V (zeigt nach rechts), n2
            ' senkrecht zu H (zeigt nach unten). Feste Vorzeichen, sonst kaeme das Bild gespiegelt.
            Dim n1x = dvy / lengthV, n1y = -dvx / lengthV
            If n1x < 0 OrElse (n1x = 0 AndAlso n1y < 0) Then n1x = -n1x : n1y = -n1y
            Dim n2x = dhy / lengthH, n2y = -dhx / lengthH
            If n2y < 0 OrElse (n2y = 0 AndAlso n2x > 0) Then n2x = -n2x : n2y = -n2y
            Dim determinant = n1x * n2y - n1y * n2x
            If Not IsFinite(determinant) OrElse determinant < MinDeterminant Then
                Return Failed(PerspectiveGuideFailure.Contradictory)
            End If
            ' Flaeche 1 an der Mitte.
            Dim areaScale = 1.0 / Math.Sqrt(determinant)
            Dim affine = New Double() {n1x * areaScale, n1y * areaScale, n2x * areaScale, n2y * areaScale}

            ' PROJEKTIVER TEIL. Die Fluchtlinie V kreuz H = (l1, l2, l3) soll ins Unendliche; mit
            ' a = l1/l3 und b = l2/l3 leistet das die Matrix mit der dritten Zeile (a, b, 1). Liegen
            ' beide Fluchtpunkte im Unendlichen (parallele Linien je Gruppe), sind a und b null.
            Dim vanishingLine = Cross3(vanishingV, vanishingH)
            Dim lineNorm = Math.Sqrt(vanishingLine(0) * vanishingLine(0) + vanishingLine(1) * vanishingLine(1))
            ' Eine Fluchtlinie durch die Bildmitte liesse sich nicht ins Unendliche schicken, ohne
            ' die Mitte selbst mitzunehmen.
            If Math.Abs(vanishingLine(2)) < 0.000000001 * lineNorm Then Return Failed(PerspectiveGuideFailure.OutOfRange)
            Dim a = vanishingLine(0) / vanishingLine(2)
            Dim b = vanishingLine(1) / vanishingLine(2)
            If Not IsFinite(a) OrElse Not IsFinite(b) Then Return Failed(PerspectiveGuideFailure.OutOfRange)

            ' BEGRENZUNG. Erst der volle Anteil; reicht er nicht, die Halbierungssuche nach dem
            ' groessten, der noch passt. Ohne projektiven Anteil bleibt nur der affine Teil - passt
            ' selbst der nicht, gibt es keine Entzerrung.
            Dim factor = 1.0
            Dim limited = False
            If Not FitsLimits(affine, a, b, 1.0, stageWidth, stageHeight, centerX, centerY, radius) Then
                If Not FitsLimits(affine, a, b, 0.0, stageWidth, stageHeight, centerX, centerY, radius) Then
                    Return Failed(PerspectiveGuideFailure.OutOfRange)
                End If
                Dim lo = 0.0, hi = 1.0
                For i = 1 To LimitSearchSteps
                    Dim middle = (lo + hi) / 2.0
                    If FitsLimits(affine, a, b, middle, stageWidth, stageHeight, centerX, centerY, radius) Then
                        lo = middle
                    Else
                        hi = middle
                    End If
                Next
                factor = lo
                limited = True
            End If

            Dim matrix = PixelMatrix(affine, a * factor, b * factor, centerX, centerY, radius)
            Dim offsets = CornerOffsets(matrix, stageWidth, stageHeight)
            If offsets Is Nothing Then Return Failed(PerspectiveGuideFailure.OutOfRange)
            Return New PerspectiveGuideSolution With {
                .IsValid = True, .IsLimited = limited, .LimitFactor = factor,
                .Failure = PerspectiveGuideFailure.None,
                .CornerOffset = offsets, .Matrix = matrix}
        End Function

        Private Shared Function Failed(reason As PerspectiveGuideFailure) As PerspectiveGuideSolution
            Return New PerspectiveGuideSolution With {.IsValid = False, .Failure = reason}
        End Function

        Private Shared Function IsFinite(value As Double) As Boolean
            Return Not Double.IsNaN(value) AndAlso Not Double.IsInfinity(value)
        End Function

        ''' <summary>Der Fluchtpunkt einer Gruppe, homogen und auf Laenge 1 gebracht, oder Nothing
        ''' fuer eine leere Gruppe. Eine Linie: ihre Richtung als Punkt im Unendlichen. Zwei: ihr
        ''' Schnitt. Ab drei: die kleinsten Quadrate der Abstaende, also der Eigenvektor zum
        ''' kleinsten Eigenwert der Summe l mal l transponiert.</summary>
        Private Shared Function VanishingPoint(lines As List(Of Double()), directions As List(Of Double())) As Double()
            If lines.Count = 0 Then Return Nothing
            Dim fallback = Normalize3(New Double() {directions(0)(0), directions(0)(1), 0.0})
            If lines.Count = 1 Then Return fallback
            If lines.Count = 2 Then
                ' Zwei gleiche Geraden schneiden sich nicht in einem Punkt; dann zaehlt die Richtung.
                Dim crossing = Normalize3(Cross3(lines(0), lines(1)))
                Return If(crossing, fallback)
            End If
            Dim m(2, 2) As Double
            For Each l In lines
                For r = 0 To 2
                    For c = 0 To 2
                        m(r, c) += l(r) * l(c)
                    Next
                Next
            Next
            Return If(Normalize3(SmallestEigenvector(m)), fallback)
        End Function

        Private Shared Function Cross3(p As Double(), q As Double()) As Double()
            Return New Double() {p(1) * q(2) - p(2) * q(1),
                                 p(2) * q(0) - p(0) * q(2),
                                 p(0) * q(1) - p(1) * q(0)}
        End Function

        Private Shared Function Normalize3(p As Double()) As Double()
            Dim n = Math.Sqrt(p(0) * p(0) + p(1) * p(1) + p(2) * p(2))
            If Not IsFinite(n) OrElse n < 0.000000000001 Then Return Nothing
            Return New Double() {p(0) / n, p(1) / n, p(2) / n}
        End Function

        ''' <summary>Der Eigenvektor zum kleinsten Eigenwert einer symmetrischen 3 mal 3 Matrix, ueber
        ''' Jacobi-Drehungen. Fuer drei mal drei konvergiert das in wenigen Durchgaengen.</summary>
        Private Shared Function SmallestEigenvector(source As Double(,)) As Double()
            Dim m = CType(source.Clone(), Double(,))
            Dim v(2, 2) As Double
            For i = 0 To 2
                v(i, i) = 1.0
            Next
            For sweep = 1 To 50
                Dim offDiagonal = Math.Abs(m(0, 1)) + Math.Abs(m(0, 2)) + Math.Abs(m(1, 2))
                If offDiagonal < 0.000000000000001 Then Exit For
                For p = 0 To 1
                    For q = p + 1 To 2
                        If Math.Abs(m(p, q)) < 1.0E-300 Then Continue For
                        Dim theta = (m(q, q) - m(p, p)) / (2.0 * m(p, q))
                        Dim t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1.0))
                        If theta = 0 Then t = 1.0
                        Dim c = 1.0 / Math.Sqrt(t * t + 1.0)
                        Dim s = t * c
                        For k = 0 To 2
                            Dim mkp = m(k, p), mkq = m(k, q)
                            m(k, p) = c * mkp - s * mkq
                            m(k, q) = s * mkp + c * mkq
                        Next
                        For k = 0 To 2
                            Dim mpk = m(p, k), mqk = m(q, k)
                            m(p, k) = c * mpk - s * mqk
                            m(q, k) = s * mpk + c * mqk
                        Next
                        For k = 0 To 2
                            Dim vkp = v(k, p), vkq = v(k, q)
                            v(k, p) = c * vkp - s * vkq
                            v(k, q) = s * vkp + c * vkq
                        Next
                    Next
                Next
            Next
            Dim smallest = 0
            For i = 1 To 2
                If m(i, i) < m(smallest, smallest) Then smallest = i
            Next
            Return New Double() {v(0, smallest), v(1, smallest), v(2, smallest)}
        End Function

        ''' <summary>Die Abbildung in Bildpunkten der Stufe: in den normierten Raum, dort erst der
        ''' projektive Teil (dritte Zeile a, b, 1), dann der affine, und zurueck.</summary>
        Private Shared Function PixelMatrix(affine As Double(), a As Double, b As Double,
                                            centerX As Double, centerY As Double, radius As Double) As Double()
            ' Normiert: R = [[A11, A12, 0], [A21, A22, 0], [a, b, 1]] (A mal P, P hat Einheitsblock).
            ' In Bildpunkten: x' = c + r * R(x - c) / r, also
            ' R_px = T(c) S(r) R S(1/r) T(-c).
            Dim r = New Double() {affine(0), affine(1), 0.0,
                                  affine(2), affine(3), 0.0,
                                  a, b, 1.0}
            Dim toNormal = New Double() {1.0 / radius, 0.0, -centerX / radius,
                                         0.0, 1.0 / radius, -centerY / radius,
                                         0.0, 0.0, 1.0}
            Dim fromNormal = New Double() {radius, 0.0, centerX,
                                           0.0, radius, centerY,
                                           0.0, 0.0, 1.0}
            Return Multiply(fromNormal, Multiply(r, toNormal))
        End Function

        Private Shared Function Multiply(p As Double(), q As Double()) As Double()
            Dim result(8) As Double
            For row = 0 To 2
                For col = 0 To 2
                    result(row * 3 + col) = p(row * 3) * q(col) + p(row * 3 + 1) * q(3 + col) + p(row * 3 + 2) * q(6 + col)
                Next
            Next
            Return result
        End Function

        ''' <summary>Erfuellt der Anteil <paramref name="factor"/> des projektiven Teils die Grenzen?
        ''' Nenner an den Ecken mindestens <see cref="MinDenominator"/>, groesster durch kleinster
        ''' hoechstens <see cref="MaxDenominatorRatio"/>, jeder Eckenversatz innerhalb von
        ''' <see cref="MaxCornerOffsetPercent"/>.</summary>
        Private Shared Function FitsLimits(affine As Double(), a As Double, b As Double, factor As Double,
                                           width As Double, height As Double,
                                           centerX As Double, centerY As Double, radius As Double) As Boolean
            Dim smallest = Double.MaxValue, largest = Double.MinValue
            For Each corner In {(0.0, 0.0), (width, 0.0), (width, height), (0.0, height)}
                Dim x = (corner.Item1 - centerX) / radius, y = (corner.Item2 - centerY) / radius
                Dim w = 1.0 + factor * (a * x + b * y)
                smallest = Math.Min(smallest, w)
                largest = Math.Max(largest, w)
            Next
            If Not IsFinite(smallest) OrElse smallest < MinDenominator Then Return False
            If largest / smallest > MaxDenominatorRatio Then Return False
            Dim offsets = CornerOffsets(PixelMatrix(affine, a * factor, b * factor, centerX, centerY, radius), width, height)
            Return offsets IsNot Nothing
        End Function

        ''' <summary>Die acht Eckenversaetze aus der Matrix, oder Nothing, wenn einer ausserhalb von
        ''' <see cref="MaxCornerOffsetPercent"/> liegt oder keine Zahl ist.</summary>
        Private Shared Function CornerOffsets(matrix As Double(), width As Double, height As Double) As Double()
            Dim corners = {(0.0, 0.0), (width, 0.0), (width, height), (0.0, height)}
            Dim result(7) As Double
            For i = 0 To 3
                Dim x = corners(i).Item1, y = corners(i).Item2
                Dim w = matrix(6) * x + matrix(7) * y + matrix(8)
                If Not IsFinite(w) OrElse w <= 0 Then Return Nothing
                Dim tx = (matrix(0) * x + matrix(1) * y + matrix(2)) / w
                Dim ty = (matrix(3) * x + matrix(4) * y + matrix(5)) / w
                Dim offsetX = (tx - x) / width * 100.0
                Dim offsetY = (ty - y) / height * 100.0
                If Not IsFinite(offsetX) OrElse Not IsFinite(offsetY) Then Return Nothing
                If Math.Abs(offsetX) > MaxCornerOffsetPercent OrElse Math.Abs(offsetY) > MaxCornerOffsetPercent Then Return Nothing
                result(i * 2) = offsetX
                result(i * 2 + 1) = offsetY
            Next
            Return result
        End Function

    End Class

End Namespace
