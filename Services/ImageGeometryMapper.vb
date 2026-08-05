Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Zentrale Abbildung zwischen ungedrehtem Rezept-/Arbeitsbildraum und sichtbarer Bildgeometrie.
    ''' Rezeptdaten bleiben in SourceSpace; Drehung/Flip werden erst fuer Anzeige und Rendern
    ''' zusammengesetzt.
    ''' </summary>
    Public NotInheritable Class ImageGeometryMapper

        Private Sub New()
        End Sub

        Public Shared Function NormalizeQuarterTurn(degrees As Integer) As Integer
            Dim normalized = ((degrees Mod 360) + 360) Mod 360
            Select Case normalized
                Case 90, 180, 270
                    Return normalized
                Case Else
                    Return 0
            End Select
        End Function

        Public Shared Function NormalizeRotation(degrees As Double) As Double
            Return ((degrees Mod 360.0) + 540.0) Mod 360.0 - 180.0
        End Function

        Public Shared Function DisplaySize(sourceWidth As Integer, sourceHeight As Integer, rotationDegrees As Integer) As SKSizeI
            Dim q = NormalizeQuarterTurn(rotationDegrees)
            If q = 90 OrElse q = 270 Then Return New SKSizeI(sourceHeight, sourceWidth)
            Return New SKSizeI(sourceWidth, sourceHeight)
        End Function

        Public Shared Function SourceObjectRotationToDisplay(localRotationDegrees As Double,
                                                             rotationDegrees As Integer,
                                                             flipHorizontal As Boolean,
                                                             flipVertical As Boolean) As Single
            Dim rotation = localRotationDegrees + NormalizeQuarterTurn(rotationDegrees)
            If flipHorizontal Xor flipVertical Then rotation = -rotation
            Return CSng(NormalizeRotation(rotation))
        End Function

        Public Shared Function DisplayObjectRotationToSource(displayRotationDegrees As Double,
                                                             rotationDegrees As Integer,
                                                             flipHorizontal As Boolean,
                                                             flipVertical As Boolean) As Single
            Dim rotation = displayRotationDegrees
            If flipHorizontal Xor flipVertical Then rotation = -rotation
            rotation -= NormalizeQuarterTurn(rotationDegrees)
            Return CSng(NormalizeRotation(rotation))
        End Function

        ' ── Die Abbildung als MATRIX ────────────────────────────────────────────
        '
        ' Dieselbe Abbildung wie SourcePointToDisplay, nur als 3x3 statt als Fallunterscheidung.
        ' Der Gewinn ist nicht die Kuerze: eine Matrix laesst sich VERKETTEN und UMKEHREN, und sie
        ' traegt eine perspektivische Zeile. Damit bekommen Punkte, Rechtecke, Objekte und Striche
        ' EINE gemeinsame Endstufe - vorher rechnete jede Sorte ihre eigene Naeherung, weshalb
        ' Objekte bei Beschnitt und Leinwand nur proportional skaliert wurden.
        '
        ' Die Punktfunktionen bleiben stehen und sind die WAHRHEIT: ein Pruefstand haelt Matrix und
        ' Punktfunktion ueber alle Drehungen, Spiegelungen und ein Punktraster deckungsgleich. Wer
        ' hier etwas aendert, aendert es an beiden Stellen oder faellt auf.

        ''' <summary>Quelle nach Anzeige: Vierteldrehung und Spiegelungen in einer Matrix.</summary>
        Public Shared Function SourceToDisplayMatrix(sourceWidth As Double, sourceHeight As Double,
                                                     rotationDegrees As Integer,
                                                     flipHorizontal As Boolean,
                                                     flipVertical As Boolean) As SKMatrix
            Dim q = NormalizeQuarterTurn(rotationDegrees)
            Dim dreh As SKMatrix
            Select Case q
                Case 90
                    ' x' = -y + hoehe, y' = x
                    dreh = New SKMatrix(0, -1, CSng(sourceHeight), 1, 0, 0, 0, 0, 1)
                Case 180
                    dreh = New SKMatrix(-1, 0, CSng(sourceWidth), 0, -1, CSng(sourceHeight), 0, 0, 1)
                Case 270
                    ' x' = y, y' = -x + breite
                    dreh = New SKMatrix(0, 1, 0, -1, 0, CSng(sourceWidth), 0, 0, 1)
                Case Else
                    dreh = SKMatrix.Identity
            End Select

            Dim anzeige = DisplaySize(CInt(Math.Round(sourceWidth)), CInt(Math.Round(sourceHeight)), q)
            If Not flipHorizontal AndAlso Not flipVertical Then Return dreh

            Dim mirror = New SKMatrix(
                If(flipHorizontal, -1.0F, 1.0F), 0, If(flipHorizontal, CSng(anzeige.Width), 0.0F),
                0, If(flipVertical, -1.0F, 1.0F), If(flipVertical, CSng(anzeige.Height), 0.0F),
                0, 0, 1)
            ' Erst drehen, dann spiegeln - dieselbe Reihenfolge wie in SourcePointToDisplay.
            Return SKMatrix.Concat(mirror, dreh)
        End Function

        ''' <summary>Anzeige nach Quelle. Rueckgabe der Einheitsmatrix, falls sich die Abbildung
        ''' nicht umkehren laesst - bei Vierteldrehungen und Spiegelungen kann das nicht vorkommen,
        ''' aber die Endstufe soll auch mit einer spaeter angehaengten Verzerrung nicht abstuerzen.</summary>
        Public Shared Function DisplayToSourceMatrix(sourceWidth As Double, sourceHeight As Double,
                                                     rotationDegrees As Integer,
                                                     flipHorizontal As Boolean,
                                                     flipVertical As Boolean) As SKMatrix
            Dim m = SourceToDisplayMatrix(sourceWidth, sourceHeight, rotationDegrees, flipHorizontal, flipVertical)
            Dim invers As SKMatrix = Nothing
            If m.TryInvert(invers) Then Return invers
            Return SKMatrix.Identity
        End Function

        ''' <summary>Die GANZE Kette vom Rezeptraum ins Ausgabebild in einer Matrix: Beschnitt-
        ''' Ursprung abziehen, auf die Ausgabegroesse skalieren, drehen und spiegeln.
        '''
        ''' Genau diese drei Schritte rechnete bisher jede Datensorte fuer sich - Objekte, Striche,
        ''' Masken -, und die Objekte taten es nur naeherungsweise. Eine gemeinsame Endstufe
        ''' beseitigt nicht nur die Doppelung: sie ist auch die Stelle, an der sich eine
        ''' perspektivische Verzerrung anhaengen laesst, ohne dass jede Sorte davon wissen muss.
        '''
        ''' <paramref name="preWidth"/>/<paramref name="preHeight"/> sind die Ausgabemasse VOR der
        ''' Vierteldrehung - bei 90 und 270 Grad also vertauscht gegenueber dem fertigen Bild.</summary>
        Public Shared Function SourceToOutputMatrix(cropLeft As Double, cropTop As Double,
                                                    cropWidth As Double, cropHeight As Double,
                                                    preWidth As Double, preHeight As Double,
                                                    rotationDegrees As Integer,
                                                    flipHorizontal As Boolean,
                                                    flipVertical As Boolean) As SKMatrix
            If cropWidth <= 0 OrElse cropHeight <= 0 Then Return SKMatrix.Identity
            Dim verschieben = SKMatrix.CreateTranslation(CSng(-cropLeft), CSng(-cropTop))
            Dim skalieren = SKMatrix.CreateScale(CSng(preWidth / cropWidth), CSng(preHeight / cropHeight))
            Dim drehen = SourceToDisplayMatrix(preWidth, preHeight, rotationDegrees, flipHorizontal, flipVertical)
            ' Concat(a, b) wendet b zuerst an - die Lesereihenfolge ist also von rechts nach links.
            Return SKMatrix.Concat(drehen, SKMatrix.Concat(skalieren, verschieben))
        End Function

        ' ── Perspektivische Verzerrung ──────────────────────────────────────────
        '
        ' Eine Homographie, also eine 3x3 MIT perspektivischer Zeile. Sie ist die einzige
        ' Verzerrung, die sich noch als Matrix schreiben laesst - alles Freiformige (Gitter) kann
        ' das nicht und muss gebacken werden.
        '
        ' SkiaSharp bringt keine Abbildung "Viereck auf Viereck" mit, also wird sie hier gerechnet.
        ' Das Verfahren ist der Standardweg fuer Einheitsquadrat auf Viereck; der Pruefstand haelt
        ' fest, dass die vier Ecken danach EXAKT auf den vorgegebenen liegen - eine Homographie, die
        ' ihre eigenen Stuetzpunkte verfehlt, ist falsch gerechnet.

        ''' <summary>Bildet das Einheitsquadrat (0,0)-(1,0)-(1,1)-(0,1) auf vier beliebige Punkte ab.
        ''' Rueckgabe der Einheitsmatrix, wenn das Viereck entartet ist.</summary>
        Public Shared Function EinheitsquadratAufViereck(p0 As SKPoint, p1 As SKPoint,
                                                         p2 As SKPoint, p3 As SKPoint) As SKMatrix
            Dim sx = p0.X - p1.X + p2.X - p3.X
            Dim sy = p0.Y - p1.Y + p2.Y - p3.Y
            Dim a, b, c, d, e, f, g, h As Double

            If Math.Abs(sx) < 0.0000001 AndAlso Math.Abs(sy) < 0.0000001 Then
                ' Parallelogramm: keine perspektivische Zeile noetig.
                a = p1.X - p0.X : b = p3.X - p0.X : c = p0.X
                d = p1.Y - p0.Y : e = p3.Y - p0.Y : f = p0.Y
                g = 0 : h = 0
            Else
                Dim dx1 = p1.X - p2.X, dx2 = p3.X - p2.X
                Dim dy1 = p1.Y - p2.Y, dy2 = p3.Y - p2.Y
                Dim nenner = dx1 * dy2 - dx2 * dy1
                If Math.Abs(nenner) < 0.0000001 Then Return SKMatrix.Identity
                g = (sx * dy2 - dx2 * sy) / nenner
                h = (dx1 * sy - sx * dy1) / nenner
                a = p1.X - p0.X + g * p1.X : b = p3.X - p0.X + h * p3.X : c = p0.X
                d = p1.Y - p0.Y + g * p1.Y : e = p3.Y - p0.Y + h * p3.Y : f = p0.Y
            End If
            Return New SKMatrix(CSng(a), CSng(b), CSng(c), CSng(d), CSng(e), CSng(f),
                                CSng(g), CSng(h), 1.0F)
        End Function

        ''' <summary>Wie stark ein Regler auf Vollausschlag die betroffene Kante verschiebt, gemessen
        ''' an der Bildkante. 0,5 heisst: die obere Kante wird bei Vollausschlag um die halbe
        ''' Bildbreite breiter. Bewusst gross genug, dass sich stuerzende Linien eines echten Fotos
        ''' geraderichten lassen, und klein genug, dass die Mitte des Reglerwegs brauchbar bleibt.</summary>
        Private Const PerspectiveFullRange As Double = 0.5

        ''' <summary>Die acht freien Eckenversaetze eines Rezepts, in Prozent, im Uhrzeigersinn ab
        ''' links oben. Hier gebuendelt, damit die Rufer nicht acht Einzelwerte durchreichen und
        ''' beim Ergaenzen einer Ecke nichts vergessen wird.</summary>
        Public Shared Function CornerOffset(adj As ImageAdjustments) As Double()
            If adj Is Nothing Then Return Nothing
            Return New Double() {adj.PerspectiveCorner0X, adj.PerspectiveCorner0Y,
                                 adj.PerspectiveCorner1X, adj.PerspectiveCorner1Y,
                                 adj.PerspectiveCorner2X, adj.PerspectiveCorner2Y,
                                 adj.PerspectiveCorner3X, adj.PerspectiveCorner3Y}
        End Function

        ''' <summary>Wohin die vier Bildecken wandern, in BILDPIXELN, im Uhrzeigersinn ab links oben.
        '''
        ''' Das ist die einzige Stelle, die aus Reglern und freien Eckenversaetzen Eckpunkte macht -
        ''' die Matrix unten und die Anfasser im Bild lesen beide von hier. Zwei Kopien dieser
        ''' Rechnung wuerden frueher oder spaeter auseinanderlaufen, und man saehe es daran, dass ein
        ''' Anfasser neben der Bildecke sitzt, die er anfasst.
        '''
        ''' <paramref name="eckenVersatz"/> darf Nothing sein (dann nur die Regler).</summary>
        Public Shared Function WarpCorners(width As Double, height As Double,
                                                waagerecht As Double, senkrecht As Double,
                                                cornerOffset As Double()) As SKPoint()
            Dim corners = New SKPoint() {
                New SKPoint(0, 0), New SKPoint(CSng(width), 0),
                New SKPoint(CSng(width), CSng(height)), New SKPoint(0, CSng(height))}

            Dim w = waagerecht / 100.0, s = senkrecht / 100.0

            ' SENKRECHT kippt um die waagerechte Achse: die obere Kante wird breiter, die untere
            ' schmaler (oder umgekehrt). Das ist der Griff fuer stuerzende Linien an Gebaeuden.
            Dim dx = width * s * PerspectiveFullRange / 2.0
            corners(0).X -= CSng(dx) : corners(1).X += CSng(dx)
            corners(3).X += CSng(dx) : corners(2).X -= CSng(dx)

            ' WAAGERECHT kippt um die senkrechte Achse: die linke Kante wird hoeher, die rechte
            ' niedriger.
            Dim dy = height * w * PerspectiveFullRange / 2.0
            corners(0).Y -= CSng(dy) : corners(3).Y += CSng(dy)
            corners(1).Y += CSng(dy) : corners(2).Y -= CSng(dy)

            ' Die freien Versaetze kommen OBENDRAUF: erst kippen, dann die einzelne Ecke nachziehen.
            If cornerOffset IsNot Nothing AndAlso cornerOffset.Length = 8 Then
                For i = 0 To 3
                    corners(i).X += CSng(cornerOffset(i * 2) / 100.0 * width)
                    corners(i).Y += CSng(cornerOffset(i * 2 + 1) / 100.0 * height)
                Next
            End If
            Return corners
        End Function

        ''' <summary>Die Verzerrungsmatrix fuer ein Bild dieser Groesse.
        '''
        ''' Alle vier Regler laufen von -100 bis 100 und sind bei 0 wirkungslos, ebenso die acht
        ''' freien Eckenversaetze - die Matrix ist dann EXAKT die Einheitsmatrix, nicht nur beinahe.
        ''' Das ist wichtiger, als es aussieht: die Stufe im Renderer darf bei unbenutztem Werkzeug
        ''' kein einziges Pixel anfassen, sonst kostet jedes Bild eine Neuabtastung fuer nichts.</summary>
        Public Shared Function WarpMatrix(width As Double, height As Double,
                                                 waagerecht As Double, senkrecht As Double,
                                                 seitenverhaeltnis As Double, size As Double,
                                                 Optional cornerOffset As Double() = Nothing) As SKMatrix
            If width <= 0 OrElse height <= 0 Then Return SKMatrix.Identity
            Dim sv = seitenverhaeltnis / 100.0, gr = size / 100.0
            Dim cornersFree = cornerOffset IsNot Nothing AndAlso cornerOffset.Length = 8 AndAlso
                            cornerOffset.Any(Function(v) Math.Abs(v) >= 0.0001)
            If Math.Abs(waagerecht / 100.0) < 0.0001 AndAlso Math.Abs(senkrecht / 100.0) < 0.0001 AndAlso
               Math.Abs(sv) < 0.0001 AndAlso Math.Abs(gr) < 0.0001 AndAlso Not cornersFree Then Return SKMatrix.Identity

            Dim corners = WarpCorners(width, height, waagerecht, senkrecht, cornerOffset)

            Dim m = EinheitsquadratAufViereck(corners(0), corners(1), corners(2), corners(3))
            ' Vom Bildraum ins Einheitsquadrat, dann verzerren.
            m = SKMatrix.Concat(m, SKMatrix.CreateScale(CSng(1.0 / width), CSng(1.0 / height)))

            ' Seitenverhaeltnis und Groesse wirken um die BILDMITTE, nach der Verzerrung: sie sollen
            ' das Ergebnis dehnen und heranholen, nicht die Kippung selbst veraendern.
            Dim centerX = CSng(width / 2.0), centerY = CSng(height / 2.0)
            Dim sxF = CSng((1.0 + sv * 0.5) * (1.0 + gr * 0.5))
            Dim syF = CSng((1.0 - sv * 0.5) * (1.0 + gr * 0.5))
            If Math.Abs(sxF - 1.0F) > 0.000001F OrElse Math.Abs(syF - 1.0F) > 0.000001F Then
                Dim aroundCenter = SKMatrix.Concat(
                    SKMatrix.CreateTranslation(centerX, centerY),
                    SKMatrix.Concat(SKMatrix.CreateScale(sxF, syF),
                                    SKMatrix.CreateTranslation(-centerX, -centerY)))
                m = SKMatrix.Concat(aroundCenter, m)
            End If
            Return m
        End Function

        ' ── Gitterverzerrung ────────────────────────────────────────────────────
        '
        ' Freiform: ein Raster von Stuetzpunkten wird verschoben, das Bild folgt dazwischen weich.
        ' Das laesst sich NICHT als Matrix schreiben - eine Matrix bildet Geraden auf Geraden ab,
        ' ein Gitter tut genau das nicht. Deshalb ist diese Verzerrung die einzige der drei, die in
        ' die Pixel gebacken werden muss, mit allem was daran haengt: Masken passen danach nicht
        ' mehr genau, und rueckgaengig geht es nur innerhalb der Sitzung.
        '
        ' Gezeichnet wird als Dreiecksnetz mit Texturkoordinaten. Jede Masche bekommt damit ihre
        ' eigene Abbildung, und Skia interpoliert dazwischen - das ist derselbe Weg, den
        ' Grafikkarten fuer Netze gehen, und deutlich besser als eine punktweise Umtastung.

        ''' <summary>Das Verschiebungsfeld einer LINIENVERZERRUNG, ausgewertet auf einem
        ''' regelmaessigen Raster.
        '''
        ''' Die Bedienung ist eine andere als beim Stuetzpunktraster, die Rechnung darunter aber
        ''' dieselbe: heraus kommt fuer jeden Rasterknoten seine QUELLposition, und damit zeichnet
        ''' derselbe Dreiecksnetz-Renderer wie bei der Gitterverzerrung. Ein zweiter Zeichenweg waere
        ''' eine zweite Gelegenheit, dass beide unterschiedlich aussehen.
        '''
        ''' Jede Linie tritt zweimal auf: einmal dort, wo sie im Bild liegt (QUELLE), und einmal
        ''' dort, wohin sie gezogen wurde (ZIEL). Ein Bildpunkt wird nach seiner Lage RELATIV zur
        ''' Ziellinie beschrieben - wie weit entlang, wie weit daneben - und dann an der Stelle
        ''' abgeholt, die dieselbe Lage relativ zur Quelllinie hat. Mehrere Linien werden nach
        ''' Abstand gewichtet gemittelt: eine Linie wirkt in ihrer Naehe stark und weiter weg kaum.
        '''
        ''' Das ist der Unterschied zum Raster: dort zieht man Punkte und die Umgebung folgt weich.
        ''' Hier legt man eine Linie auf eine KANTE im Bild und zieht sie, und die Kante geht als
        ''' Ganzes mit - genau das, was ein Raster nicht kann, ohne dass man ihm ein Dutzend Punkte
        ''' einzeln nachfuehrt.</summary>
        ''' <param name="source">Die Linien, wo sie im Bild liegen: je Linie Ax, Ay, Bx, By in Pixeln.</param>
        ''' <param name="target">Dieselben Linien, wohin sie gezogen wurden.</param>
        Public Shared Sub LineField(width As Integer, height As Integer,
                                     columns As Integer, rows As Integer,
                                     source As Double(), target As Double(),
                                     ByRef sourceX As Single(), ByRef quellY As Single())
            sourceX = Nothing
            quellY = Nothing
            If width <= 0 OrElse height <= 0 OrElse columns < 1 OrElse rows < 1 Then Return
            If source Is Nothing OrElse target Is Nothing Then Return
            If source.Length <> target.Length OrElse source.Length < 4 OrElse source.Length Mod 4 <> 0 Then Return

            Dim lines = source.Length \ 4
            Dim count = (columns + 1) * (rows + 1)
            ReDim sourceX(count - 1)
            ReDim quellY(count - 1)
            Dim stepX = width / CDbl(columns)
            Dim stepY = height / CDbl(rows)

            For rowIdx = 0 To rows
                For colIdx = 0 To columns
                    Dim i = rowIdx * (columns + 1) + colIdx
                    Dim px = colIdx * stepX
                    Dim py = rowIdx * stepY
                    Dim sumX = 0.0, sumY = 0.0, sumW = 0.0
                    ' Der naechstgelegenen Linie ihre Laenge merken: daran haengt, wie weit ihre
                    ' Wirkung reicht.
                    Dim nearDistance = Double.MaxValue, nahLaenge = 0.0

                    For k = 0 To lines - 1
                        Dim j = k * 4
                        Dim zax = target(j), zay = target(j + 1), zbx = target(j + 2), zby = target(j + 3)
                        Dim qax = source(j), qay = source(j + 1), qbx = source(j + 2), qby = source(j + 3)

                        Dim zdx = zbx - zax, zdy = zby - zay
                        Dim zlen = Math.Sqrt(zdx * zdx + zdy * zdy)
                        If zlen < 0.5 Then Continue For
                        Dim qdx = qbx - qax, qdy = qby - qay
                        Dim qlen = Math.Sqrt(qdx * qdx + qdy * qdy)
                        If qlen < 0.5 Then Continue For

                        ' Lage des Punktes zur ZIELlinie: u laengs (0 am Anfang, 1 am Ende),
                        ' v quer (in Pixeln, Vorzeichen sagt auf welcher Seite).
                        Dim rx = px - zax, ry = py - zay
                        Dim u = (rx * zdx + ry * zdy) / (zlen * zlen)
                        Dim v = (rx * zdy - ry * zdx) / zlen

                        ' Dieselbe Lage an der QUELLlinie abgreifen. Die Querrichtung wird mit der
                        ' Laenge MITSKALIERT: wird die Linie beim Ziehen laenger, dehnt sich ihre
                        ' Umgebung mit, statt daneben zu verrutschen.
                        Dim streckung = qlen / zlen
                        Dim xx = qax + u * qdx + v * streckung * qdy / qlen
                        Dim xy = qay + u * qdy - v * streckung * qdx / qlen

                        ' Abstand zur ZIELstrecke - nicht zur unendlichen Geraden. Sonst wirkte eine
                        ' kurze Linie noch weit ausserhalb ihrer beiden Enden.
                        Dim uk = Math.Max(0.0, Math.Min(1.0, u))
                        Dim nx = zax + uk * zdx, ny = zay + uk * zdy
                        Dim distance = Math.Sqrt((px - nx) * (px - nx) + (py - ny) * (py - ny))

                        ' Gewicht: nah an der Linie gross, mit dem Abstand fallend. Die Laenge geht
                        ' ein, damit eine lange Linie ueber ihre ganze Ausdehnung mehr zu sagen hat
                        ' als eine kurze daneben.
                        Dim w = Math.Pow(zlen / (GewichtBasis + distance), WeightSteepness)
                        sumX += w * (xx - px)
                        sumY += w * (xy - py)
                        sumW += w
                        If distance < nearDistance Then
                            nearDistance = distance
                            nahLaenge = zlen
                        End If
                    Next

                    Dim sx = px, sy = py
                    If sumW > 0.0000001 Then
                        ' DAEMPFUNG mit dem Abstand. Ohne sie verzoege eine EINZELNE Linie das ganze
                        ' Bild: der gewichtete Mittelwert einer einzigen Linie ist ueberall genau
                        ' ihre eigene Verschiebung, die Gewichtung kuerzt sich weg. Erst mehrere
                        ' Linien wuerden sich gegenseitig begrenzen - und darauf kann man sich bei
                        ' einem Werkzeug nicht verlassen, das mit einer Linie anfaengt.
                        '
                        ' Die Reichweite haengt an der LAENGE der Linie: eine lange Kante zieht ihre
                        ' Umgebung weiter mit als ein kurzer Strich. Das entspricht auch der
                        ' Erwartung - wer eine kurze Linie legt, meint eine kleine Stelle.
                        Dim reichweite = Math.Max(1.0, nahLaenge * RangeFactor)
                        Dim q = nearDistance / reichweite
                        Dim nenner = 1.0 + q * q
                        Dim daempfung = 1.0 / (nenner * nenner)
                        sx = px + sumX / sumW * daempfung
                        sy = py + sumY / sumW * daempfung
                    End If
                    ' In den Bildbereich klemmen: was darueber hinausgriffe, holte Farbe von der
                    ' gegenueberliegenden Kante oder aus dem Nichts.
                    sourceX(i) = CSng(Math.Max(0.0, Math.Min(width - 0.002, sx)))
                    quellY(i) = CSng(Math.Max(0.0, Math.Min(height - 0.002, sy)))
                Next
            Next
        End Sub

        ''' <summary>Wie schnell die Wirkung einer Linie mit dem Abstand nachlaesst. Kleiner Wert am
        ''' Nenner heisst: direkt an der Linie sehr stark. Der Exponent bestimmt, wie rasch es
        ''' abfaellt - unter 1 bliebe die Wirkung bis in den letzten Winkel des Bildes spuerbar.</summary>
        ''' <summary>Wie weit die Wirkung einer Linie reicht, als Vielfaches ihrer Laenge.</summary>
        Private Const RangeFactor As Double = 0.55

        Private Const GewichtBasis As Double = 12.0
        Private Const WeightSteepness As Double = 1.8

        ''' <summary>Wie fein das Raster ist, auf dem eine Linienverzerrung ausgewertet wird.
        ''' Zu grob, und eine Linie knickt statt zu biegen; zu fein, und die Vorschau haengt.</summary>
        Public Const LineGridSteps As Integer = 48

        ''' <summary>Abtastung fuer die Warp-Dreiecke. Ohne ausdrueckliche Wahl nimmt der
        ''' Bitmap-Shader SKSamplingOptions.Default, und das ist NAECHSTER NACHBAR - Treppen an
        ''' jeder kontrastreichen Kante. IsAntialias am Paint hilft dagegen nicht, das wirkt nur
        ''' auf die Dreieckskanten, nicht auf das Textur-Sampling. Mitchell wie bei der
        ''' Perspektive (ImageProcessor.SamplingHigh), damit alle Verzerr-Stufen gleich abtasten.</summary>
        Private Shared ReadOnly WarpSampling As New SKSamplingOptions(SKCubicResampler.Mitchell)


        ''' <summary>Wie <see cref="WarpOverGrid"/>, aber mit frei waehlbarer AUSGABEgroesse.
        '''
        ''' Beim Bild bleibt die Groesse gleich - was aus dem Rahmen faellt, wird abgeschnitten. Ein
        ''' Objekt dagegen darf wachsen: seine Ebene ist nur so gross wie es selbst, und eine
        ''' Verzerrung schiebt es ueber diesen Rand hinaus.</summary>
        Public Shared Function WarpOverGridTo(source As SKBitmap, targetWidth As Integer, targetHeight As Integer,
                                                      columns As Integer, rows As Integer,
                                                      targetX As Single(), targetY As Single(),
                                                      sourceX As Single(), quellY As Single()) As SKBitmap
            If source Is Nothing OrElse columns < 1 OrElse rows < 1 Then Return Nothing
            If targetWidth <= 0 OrElse targetHeight <= 0 Then Return Nothing
            Dim count = (columns + 1) * (rows + 1)
            If targetX Is Nothing OrElse targetY Is Nothing OrElse targetX.Length <> count OrElse targetY.Length <> count Then Return Nothing
            If sourceX Is Nothing OrElse quellY Is Nothing OrElse sourceX.Length <> count OrElse quellY.Length <> count Then Return Nothing

            Dim result = New SKBitmap(targetWidth, targetHeight, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                ' ToShader statt SKShader.CreateBitmap: nur ToShader hat eine Ueberladung mit
                ' Abtastwahl, CreateBitmap bliebe auf Naechster-Nachbar stehen.
                Using shader = source.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, WarpSampling)
                    Using paint = New SKPaint With {.Shader = shader, .IsAntialias = True}
                        Dim corners As New List(Of SKPoint)()
                        Dim texturen As New List(Of SKPoint)()
                        For rowIdx = 0 To rows - 1
                            For colIdx = 0 To columns - 1
                                Dim i00 = rowIdx * (columns + 1) + colIdx
                                Dim i10 = i00 + 1
                                Dim i01 = i00 + columns + 1
                                Dim i11 = i01 + 1
                                ' Zwei Dreiecke je Masche - dieselbe Aufteilung wie beim Bild.
                                For Each three In {({i00, i10, i11}), ({i00, i11, i01})}
                                    For Each k In three
                                        corners.Add(New SKPoint(targetX(k), targetY(k)))
                                        texturen.Add(New SKPoint(sourceX(k), quellY(k)))
                                    Next
                                Next
                            Next
                        Next
                        If corners.Count = 0 Then
                            result.Dispose()
                            Return Nothing
                        End If
                        Using netz = SKVertices.CreateCopy(SKVertexMode.Triangles, corners.ToArray(), texturen.ToArray(), Nothing)
                            canvas.DrawVertices(netz, SKBlendMode.SrcOver, paint)
                        End Using
                    End Using
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Wohin ein Punkt durch ein KNOTENRASTER wandert. Ein- und Ausgabe in Bildpixeln,
        ''' die Knoten stehen in Prozent (je Knoten x, y - zeilenweise ab links oben).
        '''
        ''' Gerechnet wird ueber DIESELBEN zwei Dreiecke je Masche, mit denen
        ''' <see cref="WarpOverGrid"/> das Bild zeichnet und <see cref="MeshInversePoint"/> zurueck
        ''' rechnet. Bilinear ueber die ganze Masche ist die naheliegendere Formel und stand hier
        ''' auch: auf Knoten und Maschenkanten liefert sie dasselbe, in der Maschenmitte nicht. Ein
        ''' Anfasser sass dadurch neben der Stelle, die er anfasst, und Hin- und Rueckweg waren nicht
        ''' mehr umkehrbar, obwohl beide Seiten fuer sich richtig aussahen. Gemessen an 4000 mal 3000
        ''' mit einer Woelbung von 15 Prozent der Bildhoehe: 21,0 Bildpunkte bei einem Raster von
        ''' vier mal vier, 2,5 bei zwoelf mal zwoelf, 0,6 bei vierundzwanzig. Der Fehler faellt mit
        ''' dem QUADRAT der Feinheit - er war deshalb je nach Rastergroesse verschieden gross und bei
        ''' feinen Rastern unter der Wahrnehmungsschwelle, was ihn schwer zu fassen machte.
        '''
        ''' Ausserhalb des Rasters wird FORTGESETZT statt geklemmt: der Zellenindex bleibt begrenzt,
        ''' der Anteil darin darf negativ oder groesser eins werden. Fuer ein unbewegtes Raster ist
        ''' das exakt die Identitaet - mit Klemmung waere alles ausserhalb auf den Rand gefaltet. Die
        ''' Dreiecksformel setzt genauso fort: sie ist je Dreieck affin und gilt ueber dessen Rand
        ''' hinaus weiter.</summary>
        Public Shared Function MeshPoint(nodes As Double(), columns As Integer, rows As Integer,
                                         px As Double, py As Double,
                                         width As Double, height As Double) As SKPoint
            If nodes Is Nothing OrElse columns < 1 OrElse rows < 1 OrElse width <= 0 OrElse height <= 0 Then
                Return New SKPoint(CSng(px), CSng(py))
            End If
            If nodes.Length <> (columns + 1) * (rows + 1) * 2 Then Return New SKPoint(CSng(px), CSng(py))

            Dim u = px / width * columns
            Dim v = py / height * rows
            Dim c0 = Math.Max(0, Math.Min(columns - 1, CInt(Math.Floor(u))))
            Dim r0 = Math.Max(0, Math.Min(rows - 1, CInt(Math.Floor(v))))
            Dim tu = u - c0, tv = v - r0
            Dim K = Function(colIdx As Integer, rowIdx As Integer) As (X As Double, Y As Double)
                        Dim i = (rowIdx * (columns + 1) + colIdx) * 2
                        Return (nodes(i) / 100.0 * width, nodes(i + 1) / 100.0 * height)
                    End Function
            Dim a = K(c0, r0), b = K(c0 + 1, r0), c = K(c0, r0 + 1), d = K(c0 + 1, r0 + 1)

            ' Die Diagonale laeuft von links oben nach rechts unten, wie beim Zeichnen: das erste
            ' Dreieck ist (a, b, d), das zweite (a, d, c). Welches gemeint ist, entscheidet die
            ' Lage im UNVERZERRTEN Quadrat - dort ist die Diagonale die Gerade tv = tu.
            Dim w0 As Double, w1 As Double, w2 As Double
            Dim p1 As (X As Double, Y As Double), p2 As (X As Double, Y As Double)
            If tv <= tu Then
                ' (0,0), (1,0), (1,1)
                w0 = 1.0 - tu : w1 = tu - tv : w2 = tv
                p1 = b : p2 = d
            Else
                ' (0,0), (1,1), (0,1)
                w0 = 1.0 - tv : w1 = tu : w2 = tv - tu
                p1 = d : p2 = c
            End If
            Return New SKPoint(CSng(w0 * a.X + w1 * p1.X + w2 * p2.X),
                               CSng(w0 * a.Y + w1 * p1.Y + w2 * p2.Y))
        End Function

        ''' <summary>Suchindex fuer <see cref="MeshInversePoint"/>: die verzerrte Lage aller Knoten
        ''' in Pixeln und je Kachel eines groben Rasters die Dreiecke, deren Huelle sie schneidet.
        '''
        ''' Ohne ihn testete die Rueckrichtung JEDES der bis zu 2x24x24 Dreiecke je Punkt. Der
        ''' Maskenweg fragt aber jeden Anzeigepixel einzeln - bei grossen Bildern Milliarden
        ''' Dreieckstests, Wartezeit im Minutenbereich. Mit dem Index bleiben je Punkt nur die
        ''' Dreiecke seiner Kachel uebrig.
        '''
        ''' Gehalten wird der ZULETZT gebaute Index (ein Eintrag genuegt: die heissen Schleifen
        ''' fragen immer dasselbe Knotenfeld mit denselben Massen, und die Felder werden nie in
        ''' place veraendert, nur als Ganzes ersetzt). Der Zugriff ist absichtlich sperrenfrei -
        ''' schlimmstenfalls bauen zwei Threads doppelt, das kostet nur den Preis des frueheren
        ''' Volldurchlaufs.</summary>
        Private NotInheritable Class MeshInverseIndex
            Public ReadOnly Nodes As Double()
            Public ReadOnly Columns As Integer
            Public ReadOnly Rows As Integer
            Public ReadOnly Width As Double
            Public ReadOnly Height As Double

            ''' <summary>Die verzerrte Lage jedes Knotens, in Bildpixeln.</summary>
            Public ReadOnly WarpedX As Double()
            Public ReadOnly WarpedY As Double()

            ''' <summary>Je Dreieck seine drei Knotenindizes, in Zeichenreihenfolge.</summary>
            Public ReadOnly TriangleNodes As Integer()

            ''' <summary>Je Kachel die Dreiecke, deren Huelle sie schneidet - aufsteigend, damit die
            ''' Suche dasselbe ERSTE Dreieck findet wie der fruehere Volldurchlauf (bei einer
            ''' Ueberfaltung liegen mehrere Dreiecke ueber einem Punkt).</summary>
            Public ReadOnly CellTriangles As Integer()()
            Public ReadOnly MinX As Double
            Public ReadOnly MinY As Double
            Public ReadOnly CellWidth As Double
            Public ReadOnly CellHeight As Double
            Public ReadOnly GridColumns As Integer
            Public ReadOnly GridRows As Integer

            Private Shared ReadOnly EmptyCell As Integer() = Array.Empty(Of Integer)()

            Public Sub New(nodes As Double(), columns As Integer, rows As Integer,
                           width As Double, height As Double)
                Me.Nodes = nodes
                Me.Columns = columns
                Me.Rows = rows
                Me.Width = width
                Me.Height = height

                Dim nodeCount = (columns + 1) * (rows + 1)
                WarpedX = New Double(nodeCount - 1) {}
                WarpedY = New Double(nodeCount - 1) {}
                Dim lowX = Double.MaxValue, lowY = Double.MaxValue
                Dim highX = Double.MinValue, highY = Double.MinValue
                For i = 0 To nodeCount - 1
                    WarpedX(i) = nodes(i * 2) / 100.0 * width
                    WarpedY(i) = nodes(i * 2 + 1) / 100.0 * height
                    If WarpedX(i) < lowX Then lowX = WarpedX(i)
                    If WarpedX(i) > highX Then highX = WarpedX(i)
                    If WarpedY(i) < lowY Then lowY = WarpedY(i)
                    If WarpedY(i) > highY Then highY = WarpedY(i)
                Next
                ' Ein Pixel Rand, damit die Kantentoleranz der baryzentrischen Pruefung nicht
                ' schon an der Huelle abgewiesen wird.
                lowX -= 1.0 : lowY -= 1.0 : highX += 1.0 : highY += 1.0
                MinX = lowX
                MinY = lowY

                ' Doppelt so fein wie das Knotenraster: die Kachel einer unverzerrten Masche
                ' schneidet dann hoechstens eine Handvoll Dreiecke.
                GridColumns = Math.Max(1, columns * 2)
                GridRows = Math.Max(1, rows * 2)
                CellWidth = Math.Max((highX - lowX) / GridColumns, 0.000001)
                CellHeight = Math.Max((highY - lowY) / GridRows, 0.000001)

                TriangleNodes = New Integer(columns * rows * 2 * 3 - 1) {}
                Dim buckets(GridColumns * GridRows - 1) As List(Of Integer)
                Dim tri = 0
                For rowIdx = 0 To rows - 1
                    For colIdx = 0 To columns - 1
                        Dim i00 = rowIdx * (columns + 1) + colIdx
                        Dim i10 = i00 + 1
                        Dim i01 = i00 + columns + 1
                        Dim i11 = i01 + 1
                        ' Dieselben zwei Dreiecke je Masche wie beim Zeichnen.
                        AddTriangle(tri, i00, i10, i11, buckets) : tri += 1
                        AddTriangle(tri, i00, i11, i01, buckets) : tri += 1
                    Next
                Next
                CellTriangles = New Integer(buckets.Length - 1)() {}
                For i = 0 To buckets.Length - 1
                    CellTriangles(i) = If(buckets(i) Is Nothing, EmptyCell, buckets(i).ToArray())
                Next
            End Sub

            Private Sub AddTriangle(tri As Integer, n0 As Integer, n1 As Integer, n2 As Integer,
                                    buckets As List(Of Integer)())
                TriangleNodes(tri * 3) = n0
                TriangleNodes(tri * 3 + 1) = n1
                TriangleNodes(tri * 3 + 2) = n2
                Dim boxMinX = Math.Min(WarpedX(n0), Math.Min(WarpedX(n1), WarpedX(n2)))
                Dim boxMaxX = Math.Max(WarpedX(n0), Math.Max(WarpedX(n1), WarpedX(n2)))
                Dim boxMinY = Math.Min(WarpedY(n0), Math.Min(WarpedY(n1), WarpedY(n2)))
                Dim boxMaxY = Math.Max(WarpedY(n0), Math.Max(WarpedY(n1), WarpedY(n2)))
                Dim fromX = Math.Max(0, Math.Min(GridColumns - 1, CInt(Math.Floor((boxMinX - MinX) / CellWidth))))
                Dim toX = Math.Max(0, Math.Min(GridColumns - 1, CInt(Math.Floor((boxMaxX - MinX) / CellWidth))))
                Dim fromY = Math.Max(0, Math.Min(GridRows - 1, CInt(Math.Floor((boxMinY - MinY) / CellHeight))))
                Dim toY = Math.Max(0, Math.Min(GridRows - 1, CInt(Math.Floor((boxMaxY - MinY) / CellHeight))))
                For gy = fromY To toY
                    For gx = fromX To toX
                        Dim cell = gy * GridColumns + gx
                        If buckets(cell) Is Nothing Then buckets(cell) = New List(Of Integer)()
                        buckets(cell).Add(tri)
                    Next
                Next
            End Sub
        End Class

        ''' <summary>Der zuletzt gebaute Suchindex, siehe <see cref="MeshInverseIndex"/>.</summary>
        Private Shared _meshInverseIndex As MeshInverseIndex

        ''' <summary>Die Gegenrichtung zu <see cref="MeshPoint"/>: aus welchem Bildpunkt der
        ''' angegebene hervorgegangen ist.
        '''
        ''' Gesucht wird ueber DIESELBE Dreiecksaufteilung, mit der <see cref="WarpOverGrid"/> das
        ''' Bild zeichnet - damit stimmt die Umkehrung exakt mit dem ueberein, was man sieht, und
        ''' nicht nur ungefaehr. Je Masche zwei Dreiecke, baryzentrisch getestet und dieselben
        ''' Gewichte auf das gleichmaessige Ausgangsraster angewendet. Statt alle Dreiecke zu
        ''' testen, fragt die Suche den Kachel-Index (<see cref="MeshInverseIndex"/>).
        '''
        ''' False heisst: dieser Punkt kommt aus keinem Dreieck, dort liegt nach der Verzerrung also
        ''' kein Bildinhalt mehr (die Stelle ist durchsichtig geworden).</summary>
        Public Shared Function MeshInversePoint(nodes As Double(), columns As Integer, rows As Integer,
                                                px As Double, py As Double,
                                                width As Double, height As Double,
                                                ByRef source As SKPoint) As Boolean
            source = New SKPoint(CSng(px), CSng(py))
            If nodes Is Nothing OrElse columns < 1 OrElse rows < 1 OrElse width <= 0 OrElse height <= 0 Then Return True
            If nodes.Length <> (columns + 1) * (rows + 1) * 2 Then Return True

            Dim index = _meshInverseIndex
            If index Is Nothing OrElse index.Nodes IsNot nodes OrElse index.Columns <> columns OrElse
               index.Rows <> rows OrElse index.Width <> width OrElse index.Height <> height Then
                index = New MeshInverseIndex(nodes, columns, rows, width, height)
                _meshInverseIndex = index
            End If

            ' Ausserhalb der Huelle aller verzerrten Knoten (plus Rand) kann kein Dreieck liegen.
            If px < index.MinX OrElse py < index.MinY OrElse
               px > index.MinX + index.GridColumns * index.CellWidth OrElse
               py > index.MinY + index.GridRows * index.CellHeight Then Return False
            Dim gx = Math.Min(index.GridColumns - 1, CInt(Math.Floor((px - index.MinX) / index.CellWidth)))
            Dim gy = Math.Min(index.GridRows - 1, CInt(Math.Floor((py - index.MinY) / index.CellHeight)))

            ' Ein Hauch Toleranz: ein Punkt genau auf einer Dreieckskante darf nicht durchfallen,
            ' nur weil das Vorzeichen in der letzten Stelle kippt.
            Const tolerance As Double = -0.000001
            Dim stride = columns + 1
            For Each t In index.CellTriangles(gy * index.GridColumns + gx)
                Dim n0 = index.TriangleNodes(t * 3)
                Dim n1 = index.TriangleNodes(t * 3 + 1)
                Dim n2 = index.TriangleNodes(t * 3 + 2)
                Dim aX = index.WarpedX(n0), aY = index.WarpedY(n0)
                Dim bX = index.WarpedX(n1), bY = index.WarpedY(n1)
                Dim cX = index.WarpedX(n2), cY = index.WarpedY(n2)
                Dim area = (bX - aX) * (cY - aY) - (cX - aX) * (bY - aY)
                If Math.Abs(area) < 0.0000001 Then Continue For
                Dim w0 = ((bX - px) * (cY - py) - (cX - px) * (bY - py)) / area
                Dim w1 = ((cX - px) * (aY - py) - (aX - px) * (cY - py)) / area
                Dim w2 = 1.0 - w0 - w1
                If w0 < tolerance OrElse w1 < tolerance OrElse w2 < tolerance Then Continue For
                ' Dieselben Gewichte auf das gleichmaessige Ausgangsraster.
                source = New SKPoint(
                    CSng((w0 * (n0 Mod stride) + w1 * (n1 Mod stride) + w2 * (n2 Mod stride)) / CDbl(columns) * width),
                    CSng((w0 * (n0 \ stride) + w1 * (n1 \ stride) + w2 * (n2 \ stride)) / CDbl(rows) * height))
                Return True
            Next
            Return False
        End Function

        ''' <summary>Wohin EIN Punkt durch ein Linienfeld wandert. Dieselbe Rechnung wie in
        ''' <see cref="LinienFeld"/>, nur fuer einen einzelnen Punkt - fuer ein Objekt lohnt kein
        ''' ganzes Raster, und die Formel darf trotzdem nur an einer Stelle stehen.</summary>
        Public Shared Function LinePoint(px As Double, py As Double,
                                           source As Double(), target As Double()) As SKPoint
            If source Is Nothing OrElse target Is Nothing Then Return New SKPoint(CSng(px), CSng(py))
            If source.Length <> target.Length OrElse source.Length < 4 OrElse source.Length Mod 4 <> 0 Then
                Return New SKPoint(CSng(px), CSng(py))
            End If
            Dim lines = source.Length \ 4
            Dim sumX = 0.0, sumY = 0.0, sumW = 0.0
            Dim nearDistance = Double.MaxValue, nahLaenge = 0.0
            For k = 0 To lines - 1
                Dim j = k * 4
                Dim zax = target(j), zay = target(j + 1), zbx = target(j + 2), zby = target(j + 3)
                Dim qax = source(j), qay = source(j + 1), qbx = source(j + 2), qby = source(j + 3)
                Dim zdx = zbx - zax, zdy = zby - zay
                Dim zlen = Math.Sqrt(zdx * zdx + zdy * zdy)
                If zlen < 0.5 Then Continue For
                Dim qdx = qbx - qax, qdy = qby - qay
                Dim qlen = Math.Sqrt(qdx * qdx + qdy * qdy)
                If qlen < 0.5 Then Continue For
                Dim rx = px - zax, ry = py - zay
                Dim u = (rx * zdx + ry * zdy) / (zlen * zlen)
                Dim v = (rx * zdy - ry * zdx) / zlen
                Dim streckung = qlen / zlen
                Dim xx = qax + u * qdx + v * streckung * qdy / qlen
                Dim xy = qay + u * qdy - v * streckung * qdx / qlen
                Dim uk = Math.Max(0.0, Math.Min(1.0, u))
                Dim nx = zax + uk * zdx, ny = zay + uk * zdy
                Dim distance = Math.Sqrt((px - nx) * (px - nx) + (py - ny) * (py - ny))
                Dim w = Math.Pow(zlen / (GewichtBasis + distance), WeightSteepness)
                sumX += w * (xx - px)
                sumY += w * (xy - py)
                sumW += w
                If distance < nearDistance Then
                    nearDistance = distance
                    nahLaenge = zlen
                End If
            Next
            If sumW <= 0.0000001 Then Return New SKPoint(CSng(px), CSng(py))
            Dim reichweite = Math.Max(1.0, nahLaenge * RangeFactor)
            Dim q = nearDistance / reichweite
            Dim nenner = 1.0 + q * q
            Dim daempfung = 1.0 / (nenner * nenner)
            Return New SKPoint(CSng(px + sumX / sumW * daempfung), CSng(py + sumY / sumW * daempfung))
        End Function


        ''' <summary>Verzerrt ein Bild ueber ein Stuetzpunktraster.
        '''
        ''' <paramref name="targetX"/>/<paramref name="targetY"/> sind (spalten+1) mal (zeilen+1) Werte
        ''' in BILDPIXELN, zeilenweise ab links oben: wohin der jeweilige Rasterpunkt wandern soll.
        ''' Liegen sie auf ihren Ausgangsstellen, kommt das Bild unveraendert zurueck - und zwar
        ''' dasselbe Objekt, ohne Neuabtastung.</summary>
        ''' <param name="sourceX">Wo die Rasterpunkte HERKOMMEN, ebenfalls in Bildpixeln. Nothing
        ''' heisst: das gleichmaessige Raster. Fuer die Live-Vorschau wird es gebraucht, weil dort
        ''' das unverzerrte Raster im ANZEIGERAUM liegt und nach Beschnitt oder Drehung nicht mehr
        ''' gleichmaessig ist.</param>
        Public Shared Function WarpOverGrid(source As SKBitmap, columns As Integer, rows As Integer,
                                                   targetX As Single(), targetY As Single(),
                                                   Optional sourceX As Single() = Nothing,
                                                   Optional quellY As Single() = Nothing) As SKBitmap
            If source Is Nothing OrElse columns < 1 OrElse rows < 1 Then Return source
            Dim count = (columns + 1) * (rows + 1)
            If targetX Is Nothing OrElse targetY Is Nothing OrElse targetX.Length <> count OrElse targetY.Length <> count Then Return source
            Dim ownSource = sourceX IsNot Nothing AndAlso quellY IsNot Nothing AndAlso
                               sourceX.Length = count AndAlso quellY.Length = count

            Dim width = source.Width, height = source.Height
            Dim stepX = width / CSng(columns), stepY = height / CSng(rows)
            Dim QX = Function(i As Integer, colIdx As Integer) As Single
                         Return If(ownSource, sourceX(i), colIdx * stepX)
                     End Function
            Dim QY = Function(i As Integer, rowIdx As Integer) As Single
                         Return If(ownSource, quellY(i), rowIdx * stepY)
                     End Function

            ' Unveraendert heisst unveraendert: kein Umkopieren, keine Interpolationsverluste.
            Dim moved = False
            For rowIdx = 0 To rows
                For colIdx = 0 To columns
                    Dim i = rowIdx * (columns + 1) + colIdx
                    If Math.Abs(targetX(i) - QX(i, colIdx)) > 0.01F OrElse
                       Math.Abs(targetY(i) - QY(i, rowIdx)) > 0.01F Then
                        moved = True
                        Exit For
                    End If
                Next
                If moved Then Exit For
            Next
            If Not moved Then Return source

            ' Je Masche zwei Dreiecke. Die Texturkoordinaten bleiben auf dem UNVERZERRTEN Raster -
            ' verschoben wird nur die Lage der Punkte.
            Dim corners As New List(Of SKPoint)(columns * rows * 6)
            Dim textur As New List(Of SKPoint)(columns * rows * 6)
            For rowIdx = 0 To rows - 1
                For colIdx = 0 To columns - 1
                    Dim i00 = rowIdx * (columns + 1) + colIdx
                    Dim i10 = i00 + 1
                    Dim i01 = (rowIdx + 1) * (columns + 1) + colIdx
                    Dim i11 = i01 + 1
                    Dim t00 = New SKPoint(QX(i00, colIdx), QY(i00, rowIdx))
                    Dim t10 = New SKPoint(QX(i10, colIdx + 1), QY(i10, rowIdx))
                    Dim t01 = New SKPoint(QX(i01, colIdx), QY(i01, rowIdx + 1))
                    Dim t11 = New SKPoint(QX(i11, colIdx + 1), QY(i11, rowIdx + 1))
                    Dim p00 = New SKPoint(targetX(i00), targetY(i00))
                    Dim p10 = New SKPoint(targetX(i10), targetY(i10))
                    Dim p01 = New SKPoint(targetX(i01), targetY(i01))
                    Dim p11 = New SKPoint(targetX(i11), targetY(i11))
                    corners.Add(p00) : corners.Add(p10) : corners.Add(p11)
                    textur.Add(t00) : textur.Add(t10) : textur.Add(t11)
                    corners.Add(p00) : corners.Add(p11) : corners.Add(p01)
                    textur.Add(t00) : textur.Add(t11) : textur.Add(t01)
                Next
            Next

            Dim result = New SKBitmap(width, height, source.ColorType, source.AlphaType)
            Using canvas = New SKCanvas(result)
                canvas.Clear(SKColors.Transparent)
                ' Klemmen an den Raendern: ohne das zeigt eine ueber die Kante gezogene Masche
                ' durchsichtige Streifen, weil die Textur dort zu Ende ist.
                Using shader = source.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, WarpSampling)
                    Using paint = New SKPaint With {.IsAntialias = True, .Shader = shader}
                        Using netz = SKVertices.CreateCopy(SKVertexMode.Triangles, corners.ToArray(), textur.ToArray(), Nothing)
                            canvas.DrawVertices(netz, SKBlendMode.Src, paint)
                        End Using
                    End Using
                End Using
            End Using
            Return result
        End Function

        Public Shared Function SourcePointToDisplay(x As Double, y As Double,
                                                    sourceWidth As Double, sourceHeight As Double,
                                                    rotationDegrees As Integer,
                                                    flipHorizontal As Boolean,
                                                    flipVertical As Boolean) As SKPoint
            Dim display = DisplaySize(CInt(Math.Round(sourceWidth)), CInt(Math.Round(sourceHeight)), rotationDegrees)
            Dim dx = x
            Dim dy = y
            Select Case NormalizeQuarterTurn(rotationDegrees)
                Case 90
                    dx = sourceHeight - y
                    dy = x
                Case 180
                    dx = sourceWidth - x
                    dy = sourceHeight - y
                Case 270
                    dx = y
                    dy = sourceWidth - x
            End Select
            If flipHorizontal Then dx = display.Width - dx
            If flipVertical Then dy = display.Height - dy
            Return New SKPoint(CSng(dx), CSng(dy))
        End Function

        Public Shared Function DisplayPointToSource(x As Double, y As Double,
                                                    sourceWidth As Double, sourceHeight As Double,
                                                    rotationDegrees As Integer,
                                                    flipHorizontal As Boolean,
                                                    flipVertical As Boolean) As SKPoint
            Dim display = DisplaySize(CInt(Math.Round(sourceWidth)), CInt(Math.Round(sourceHeight)), rotationDegrees)
            Dim dx = x
            Dim dy = y
            If flipHorizontal Then dx = display.Width - dx
            If flipVertical Then dy = display.Height - dy

            Dim sx = dx
            Dim sy = dy
            Select Case NormalizeQuarterTurn(rotationDegrees)
                Case 90
                    sx = dy
                    sy = sourceHeight - dx
                Case 180
                    sx = sourceWidth - dx
                    sy = sourceHeight - dy
                Case 270
                    sx = sourceWidth - dy
                    sy = dx
            End Select
            Return New SKPoint(CSng(sx), CSng(sy))
        End Function

        Public Shared Function SourceObjectToDisplay(rect As SKRect, sourceWidth As Double, sourceHeight As Double,
                                                     outputWidth As Double, outputHeight As Double,
                                                     rotationDegrees As Integer,
                                                     flipHorizontal As Boolean,
                                                     flipVertical As Boolean,
                                                     localRotationDegrees As Double) As (Rect As SKRect, RotationDegrees As Single)
            Dim q = NormalizeQuarterTurn(rotationDegrees)
            Dim preWidth = If(q = 90 OrElse q = 270, outputHeight, outputWidth)
            Dim preHeight = If(q = 90 OrElse q = 270, outputWidth, outputHeight)
            If sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse preWidth <= 0 OrElse preHeight <= 0 Then
                Return (rect, CSng(NormalizeRotation(localRotationDegrees)))
            End If

            Dim scaleX = preWidth / sourceWidth
            Dim scaleY = preHeight / sourceHeight
            Dim scaledRect = New SKRect(CSng(rect.Left * scaleX),
                                        CSng(rect.Top * scaleY),
                                        CSng(rect.Right * scaleX),
                                        CSng(rect.Bottom * scaleY))
            Dim center = SourcePointToDisplay(scaledRect.MidX, scaledRect.MidY,
                                              preWidth, preHeight,
                                              q, flipHorizontal, flipVertical)
            Dim displayRect = SKRect.Create(center.X - scaledRect.Width / 2.0F,
                                            center.Y - scaledRect.Height / 2.0F,
                                            scaledRect.Width,
                                            scaledRect.Height)
            Return (displayRect, SourceObjectRotationToDisplay(localRotationDegrees, q, flipHorizontal, flipVertical))
        End Function

        Public Shared Function DisplayObjectToSource(rect As SKRect, sourceWidth As Double, sourceHeight As Double,
                                                     displayWidth As Double, displayHeight As Double,
                                                     rotationDegrees As Integer,
                                                     flipHorizontal As Boolean,
                                                     flipVertical As Boolean,
                                                     displayRotationDegrees As Double) As (Rect As SKRect, RotationDegrees As Single)
            Dim q = NormalizeQuarterTurn(rotationDegrees)
            Dim preWidth = If(q = 90 OrElse q = 270, displayHeight, displayWidth)
            Dim preHeight = If(q = 90 OrElse q = 270, displayWidth, displayHeight)
            If sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse preWidth <= 0 OrElse preHeight <= 0 Then
                Return (rect, CSng(NormalizeRotation(displayRotationDegrees)))
            End If

            Dim center = DisplayPointToSource(rect.MidX, rect.MidY,
                                              preWidth, preHeight,
                                              q, flipHorizontal, flipVertical)
            Dim sourceScaleX = sourceWidth / preWidth
            Dim sourceScaleY = sourceHeight / preHeight
            Dim sourceWidthPixels = rect.Width * sourceScaleX
            Dim sourceHeightPixels = rect.Height * sourceScaleY
            Dim sourceRect = SKRect.Create(CSng(center.X * sourceScaleX - sourceWidthPixels / 2.0),
                                           CSng(center.Y * sourceScaleY - sourceHeightPixels / 2.0),
                                           CSng(sourceWidthPixels),
                                           CSng(sourceHeightPixels))

            Return (sourceRect, DisplayObjectRotationToSource(displayRotationDegrees, q, flipHorizontal, flipVertical))
        End Function

    End Class

End Namespace
