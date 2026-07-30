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

            Dim spiegel = New SKMatrix(
                If(flipHorizontal, -1.0F, 1.0F), 0, If(flipHorizontal, CSng(anzeige.Width), 0.0F),
                0, If(flipVertical, -1.0F, 1.0F), If(flipVertical, CSng(anzeige.Height), 0.0F),
                0, 0, 1)
            ' Erst drehen, dann spiegeln - dieselbe Reihenfolge wie in SourcePointToDisplay.
            Return SKMatrix.Concat(spiegel, dreh)
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
        Private Const PerspektiveVollausschlag As Double = 0.5

        ''' <summary>Die acht freien Eckenversaetze eines Rezepts, in Prozent, im Uhrzeigersinn ab
        ''' links oben. Hier gebuendelt, damit die Rufer nicht acht Einzelwerte durchreichen und
        ''' beim Ergaenzen einer Ecke nichts vergessen wird.</summary>
        Public Shared Function EckenVersatz(adj As ImageAdjustments) As Double()
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
        Public Shared Function VerzerrungsEcken(width As Double, height As Double,
                                                waagerecht As Double, senkrecht As Double,
                                                eckenVersatz As Double()) As SKPoint()
            Dim ecken = New SKPoint() {
                New SKPoint(0, 0), New SKPoint(CSng(width), 0),
                New SKPoint(CSng(width), CSng(height)), New SKPoint(0, CSng(height))}

            Dim w = waagerecht / 100.0, s = senkrecht / 100.0

            ' SENKRECHT kippt um die waagerechte Achse: die obere Kante wird breiter, die untere
            ' schmaler (oder umgekehrt). Das ist der Griff fuer stuerzende Linien an Gebaeuden.
            Dim dx = width * s * PerspektiveVollausschlag / 2.0
            ecken(0).X -= CSng(dx) : ecken(1).X += CSng(dx)
            ecken(3).X += CSng(dx) : ecken(2).X -= CSng(dx)

            ' WAAGERECHT kippt um die senkrechte Achse: die linke Kante wird hoeher, die rechte
            ' niedriger.
            Dim dy = height * w * PerspektiveVollausschlag / 2.0
            ecken(0).Y -= CSng(dy) : ecken(3).Y += CSng(dy)
            ecken(1).Y += CSng(dy) : ecken(2).Y -= CSng(dy)

            ' Die freien Versaetze kommen OBENDRAUF: erst kippen, dann die einzelne Ecke nachziehen.
            If eckenVersatz IsNot Nothing AndAlso eckenVersatz.Length = 8 Then
                For i = 0 To 3
                    ecken(i).X += CSng(eckenVersatz(i * 2) / 100.0 * width)
                    ecken(i).Y += CSng(eckenVersatz(i * 2 + 1) / 100.0 * height)
                Next
            End If
            Return ecken
        End Function

        ''' <summary>Die Verzerrungsmatrix fuer ein Bild dieser Groesse.
        '''
        ''' Alle vier Regler laufen von -100 bis 100 und sind bei 0 wirkungslos, ebenso die acht
        ''' freien Eckenversaetze - die Matrix ist dann EXAKT die Einheitsmatrix, nicht nur beinahe.
        ''' Das ist wichtiger, als es aussieht: die Stufe im Renderer darf bei unbenutztem Werkzeug
        ''' kein einziges Pixel anfassen, sonst kostet jedes Bild eine Neuabtastung fuer nichts.</summary>
        Public Shared Function VerzerrungsMatrix(width As Double, height As Double,
                                                 waagerecht As Double, senkrecht As Double,
                                                 seitenverhaeltnis As Double, groesse As Double,
                                                 Optional eckenVersatz As Double() = Nothing) As SKMatrix
            If width <= 0 OrElse height <= 0 Then Return SKMatrix.Identity
            Dim sv = seitenverhaeltnis / 100.0, gr = groesse / 100.0
            Dim eckenFrei = eckenVersatz IsNot Nothing AndAlso eckenVersatz.Length = 8 AndAlso
                            eckenVersatz.Any(Function(v) Math.Abs(v) >= 0.0001)
            If Math.Abs(waagerecht / 100.0) < 0.0001 AndAlso Math.Abs(senkrecht / 100.0) < 0.0001 AndAlso
               Math.Abs(sv) < 0.0001 AndAlso Math.Abs(gr) < 0.0001 AndAlso Not eckenFrei Then Return SKMatrix.Identity

            Dim ecken = VerzerrungsEcken(width, height, waagerecht, senkrecht, eckenVersatz)

            Dim m = EinheitsquadratAufViereck(ecken(0), ecken(1), ecken(2), ecken(3))
            ' Vom Bildraum ins Einheitsquadrat, dann verzerren.
            m = SKMatrix.Concat(m, SKMatrix.CreateScale(CSng(1.0 / width), CSng(1.0 / height)))

            ' Seitenverhaeltnis und Groesse wirken um die BILDMITTE, nach der Verzerrung: sie sollen
            ' das Ergebnis dehnen und heranholen, nicht die Kippung selbst veraendern.
            Dim mitteX = CSng(width / 2.0), mitteY = CSng(height / 2.0)
            Dim sxF = CSng((1.0 + sv * 0.5) * (1.0 + gr * 0.5))
            Dim syF = CSng((1.0 - sv * 0.5) * (1.0 + gr * 0.5))
            If Math.Abs(sxF - 1.0F) > 0.000001F OrElse Math.Abs(syF - 1.0F) > 0.000001F Then
                Dim umMitte = SKMatrix.Concat(
                    SKMatrix.CreateTranslation(mitteX, mitteY),
                    SKMatrix.Concat(SKMatrix.CreateScale(sxF, syF),
                                    SKMatrix.CreateTranslation(-mitteX, -mitteY)))
                m = SKMatrix.Concat(umMitte, m)
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
        ''' <param name="quelle">Die Linien, wo sie im Bild liegen: je Linie Ax, Ay, Bx, By in Pixeln.</param>
        ''' <param name="ziel">Dieselben Linien, wohin sie gezogen wurden.</param>
        Public Shared Sub LineField(breite As Integer, hoehe As Integer,
                                     spalten As Integer, zeilen As Integer,
                                     quelle As Double(), ziel As Double(),
                                     ByRef quellX As Single(), ByRef quellY As Single())
            quellX = Nothing
            quellY = Nothing
            If breite <= 0 OrElse hoehe <= 0 OrElse spalten < 1 OrElse zeilen < 1 Then Return
            If quelle Is Nothing OrElse ziel Is Nothing Then Return
            If quelle.Length <> ziel.Length OrElse quelle.Length < 4 OrElse quelle.Length Mod 4 <> 0 Then Return

            Dim linien = quelle.Length \ 4
            Dim anzahl = (spalten + 1) * (zeilen + 1)
            ReDim quellX(anzahl - 1)
            ReDim quellY(anzahl - 1)
            Dim schrittX = breite / CDbl(spalten)
            Dim schrittY = hoehe / CDbl(zeilen)

            For zi = 0 To zeilen
                For si = 0 To spalten
                    Dim i = zi * (spalten + 1) + si
                    Dim px = si * schrittX
                    Dim py = zi * schrittY
                    Dim summeX = 0.0, summeY = 0.0, summeW = 0.0
                    ' Der naechstgelegenen Linie ihre Laenge merken: daran haengt, wie weit ihre
                    ' Wirkung reicht.
                    Dim nahAbstand = Double.MaxValue, nahLaenge = 0.0

                    For k = 0 To linien - 1
                        Dim j = k * 4
                        Dim zax = ziel(j), zay = ziel(j + 1), zbx = ziel(j + 2), zby = ziel(j + 3)
                        Dim qax = quelle(j), qay = quelle(j + 1), qbx = quelle(j + 2), qby = quelle(j + 3)

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
                        Dim abstand = Math.Sqrt((px - nx) * (px - nx) + (py - ny) * (py - ny))

                        ' Gewicht: nah an der Linie gross, mit dem Abstand fallend. Die Laenge geht
                        ' ein, damit eine lange Linie ueber ihre ganze Ausdehnung mehr zu sagen hat
                        ' als eine kurze daneben.
                        Dim w = Math.Pow(zlen / (GewichtBasis + abstand), GewichtSteilheit)
                        summeX += w * (xx - px)
                        summeY += w * (xy - py)
                        summeW += w
                        If abstand < nahAbstand Then
                            nahAbstand = abstand
                            nahLaenge = zlen
                        End If
                    Next

                    Dim sx = px, sy = py
                    If summeW > 0.0000001 Then
                        ' DAEMPFUNG mit dem Abstand. Ohne sie verzoege eine EINZELNE Linie das ganze
                        ' Bild: der gewichtete Mittelwert einer einzigen Linie ist ueberall genau
                        ' ihre eigene Verschiebung, die Gewichtung kuerzt sich weg. Erst mehrere
                        ' Linien wuerden sich gegenseitig begrenzen - und darauf kann man sich bei
                        ' einem Werkzeug nicht verlassen, das mit einer Linie anfaengt.
                        '
                        ' Die Reichweite haengt an der LAENGE der Linie: eine lange Kante zieht ihre
                        ' Umgebung weiter mit als ein kurzer Strich. Das entspricht auch der
                        ' Erwartung - wer eine kurze Linie legt, meint eine kleine Stelle.
                        Dim reichweite = Math.Max(1.0, nahLaenge * ReichweiteFaktor)
                        Dim q = nahAbstand / reichweite
                        Dim nenner = 1.0 + q * q
                        Dim daempfung = 1.0 / (nenner * nenner)
                        sx = px + summeX / summeW * daempfung
                        sy = py + summeY / summeW * daempfung
                    End If
                    ' In den Bildbereich klemmen: was darueber hinausgriffe, holte Farbe von der
                    ' gegenueberliegenden Kante oder aus dem Nichts.
                    quellX(i) = CSng(Math.Max(0.0, Math.Min(breite - 0.002, sx)))
                    quellY(i) = CSng(Math.Max(0.0, Math.Min(hoehe - 0.002, sy)))
                Next
            Next
        End Sub

        ''' <summary>Wie schnell die Wirkung einer Linie mit dem Abstand nachlaesst. Kleiner Wert am
        ''' Nenner heisst: direkt an der Linie sehr stark. Der Exponent bestimmt, wie rasch es
        ''' abfaellt - unter 1 bliebe die Wirkung bis in den letzten Winkel des Bildes spuerbar.</summary>
        ''' <summary>Wie weit die Wirkung einer Linie reicht, als Vielfaches ihrer Laenge.</summary>
        Private Const ReichweiteFaktor As Double = 0.55

        Private Const GewichtBasis As Double = 12.0
        Private Const GewichtSteilheit As Double = 1.8

        ''' <summary>Wie fein das Raster ist, auf dem eine Linienverzerrung ausgewertet wird.
        ''' Zu grob, und eine Linie knickt statt zu biegen; zu fein, und die Vorschau haengt.</summary>
        Public Const LineGridSteps As Integer = 48


        ''' <summary>Wie <see cref="VerzerreUeberGitter"/>, aber mit frei waehlbarer AUSGABEgroesse.
        '''
        ''' Beim Bild bleibt die Groesse gleich - was aus dem Rahmen faellt, wird abgeschnitten. Ein
        ''' Objekt dagegen darf wachsen: seine Ebene ist nur so gross wie es selbst, und eine
        ''' Verzerrung schiebt es ueber diesen Rand hinaus.</summary>
        Public Shared Function WarpOverGridTo(quelle As SKBitmap, zielBreite As Integer, zielHoehe As Integer,
                                                      spalten As Integer, zeilen As Integer,
                                                      zielX As Single(), zielY As Single(),
                                                      quellX As Single(), quellY As Single()) As SKBitmap
            If quelle Is Nothing OrElse spalten < 1 OrElse zeilen < 1 Then Return Nothing
            If zielBreite <= 0 OrElse zielHoehe <= 0 Then Return Nothing
            Dim anzahl = (spalten + 1) * (zeilen + 1)
            If zielX Is Nothing OrElse zielY Is Nothing OrElse zielX.Length <> anzahl OrElse zielY.Length <> anzahl Then Return Nothing
            If quellX Is Nothing OrElse quellY Is Nothing OrElse quellX.Length <> anzahl OrElse quellY.Length <> anzahl Then Return Nothing

            Dim ergebnis = New SKBitmap(zielBreite, zielHoehe, quelle.ColorType, quelle.AlphaType)
            Using canvas = New SKCanvas(ergebnis)
                canvas.Clear(SKColors.Transparent)
                Using shader = SKShader.CreateBitmap(quelle, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp)
                    Using paint = New SKPaint With {.Shader = shader, .IsAntialias = True}
                        Dim ecken As New List(Of SKPoint)()
                        Dim texturen As New List(Of SKPoint)()
                        For zi = 0 To zeilen - 1
                            For si = 0 To spalten - 1
                                Dim i00 = zi * (spalten + 1) + si
                                Dim i10 = i00 + 1
                                Dim i01 = i00 + spalten + 1
                                Dim i11 = i01 + 1
                                ' Zwei Dreiecke je Masche - dieselbe Aufteilung wie beim Bild.
                                For Each drei In {({i00, i10, i11}), ({i00, i11, i01})}
                                    For Each k In drei
                                        ecken.Add(New SKPoint(zielX(k), zielY(k)))
                                        texturen.Add(New SKPoint(quellX(k), quellY(k)))
                                    Next
                                Next
                            Next
                        Next
                        If ecken.Count = 0 Then
                            ergebnis.Dispose()
                            Return Nothing
                        End If
                        Using netz = SKVertices.CreateCopy(SKVertexMode.Triangles, ecken.ToArray(), texturen.ToArray(), Nothing)
                            canvas.DrawVertices(netz, SKBlendMode.SrcOver, paint)
                        End Using
                    End Using
                End Using
            End Using
            Return ergebnis
        End Function

        ''' <summary>Wohin EIN Punkt durch ein Linienfeld wandert. Dieselbe Rechnung wie in
        ''' <see cref="LinienFeld"/>, nur fuer einen einzelnen Punkt - fuer ein Objekt lohnt kein
        ''' ganzes Raster, und die Formel darf trotzdem nur an einer Stelle stehen.</summary>
        Public Shared Function LinePoint(px As Double, py As Double,
                                           quelle As Double(), ziel As Double()) As SKPoint
            If quelle Is Nothing OrElse ziel Is Nothing Then Return New SKPoint(CSng(px), CSng(py))
            If quelle.Length <> ziel.Length OrElse quelle.Length < 4 OrElse quelle.Length Mod 4 <> 0 Then
                Return New SKPoint(CSng(px), CSng(py))
            End If
            Dim linien = quelle.Length \ 4
            Dim summeX = 0.0, summeY = 0.0, summeW = 0.0
            Dim nahAbstand = Double.MaxValue, nahLaenge = 0.0
            For k = 0 To linien - 1
                Dim j = k * 4
                Dim zax = ziel(j), zay = ziel(j + 1), zbx = ziel(j + 2), zby = ziel(j + 3)
                Dim qax = quelle(j), qay = quelle(j + 1), qbx = quelle(j + 2), qby = quelle(j + 3)
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
                Dim abstand = Math.Sqrt((px - nx) * (px - nx) + (py - ny) * (py - ny))
                Dim w = Math.Pow(zlen / (GewichtBasis + abstand), GewichtSteilheit)
                summeX += w * (xx - px)
                summeY += w * (xy - py)
                summeW += w
                If abstand < nahAbstand Then
                    nahAbstand = abstand
                    nahLaenge = zlen
                End If
            Next
            If summeW <= 0.0000001 Then Return New SKPoint(CSng(px), CSng(py))
            Dim reichweite = Math.Max(1.0, nahLaenge * ReichweiteFaktor)
            Dim q = nahAbstand / reichweite
            Dim nenner = 1.0 + q * q
            Dim daempfung = 1.0 / (nenner * nenner)
            Return New SKPoint(CSng(px + summeX / summeW * daempfung), CSng(py + summeY / summeW * daempfung))
        End Function


        ''' <summary>Verzerrt ein Bild ueber ein Stuetzpunktraster.
        '''
        ''' <paramref name="zielX"/>/<paramref name="zielY"/> sind (spalten+1) mal (zeilen+1) Werte
        ''' in BILDPIXELN, zeilenweise ab links oben: wohin der jeweilige Rasterpunkt wandern soll.
        ''' Liegen sie auf ihren Ausgangsstellen, kommt das Bild unveraendert zurueck - und zwar
        ''' dasselbe Objekt, ohne Neuabtastung.</summary>
        ''' <param name="quellX">Wo die Rasterpunkte HERKOMMEN, ebenfalls in Bildpixeln. Nothing
        ''' heisst: das gleichmaessige Raster. Fuer die Live-Vorschau wird es gebraucht, weil dort
        ''' das unverzerrte Raster im ANZEIGERAUM liegt und nach Beschnitt oder Drehung nicht mehr
        ''' gleichmaessig ist.</param>
        Public Shared Function WarpOverGrid(quelle As SKBitmap, spalten As Integer, zeilen As Integer,
                                                   zielX As Single(), zielY As Single(),
                                                   Optional quellX As Single() = Nothing,
                                                   Optional quellY As Single() = Nothing) As SKBitmap
            If quelle Is Nothing OrElse spalten < 1 OrElse zeilen < 1 Then Return quelle
            Dim anzahl = (spalten + 1) * (zeilen + 1)
            If zielX Is Nothing OrElse zielY Is Nothing OrElse zielX.Length <> anzahl OrElse zielY.Length <> anzahl Then Return quelle
            Dim eigeneQuelle = quellX IsNot Nothing AndAlso quellY IsNot Nothing AndAlso
                               quellX.Length = anzahl AndAlso quellY.Length = anzahl

            Dim breite = quelle.Width, hoehe = quelle.Height
            Dim schrittX = breite / CSng(spalten), schrittY = hoehe / CSng(zeilen)
            Dim QX = Function(i As Integer, si As Integer) As Single
                         Return If(eigeneQuelle, quellX(i), si * schrittX)
                     End Function
            Dim QY = Function(i As Integer, zi As Integer) As Single
                         Return If(eigeneQuelle, quellY(i), zi * schrittY)
                     End Function

            ' Unveraendert heisst unveraendert: kein Umkopieren, keine Interpolationsverluste.
            Dim bewegt = False
            For zi = 0 To zeilen
                For si = 0 To spalten
                    Dim i = zi * (spalten + 1) + si
                    If Math.Abs(zielX(i) - QX(i, si)) > 0.01F OrElse
                       Math.Abs(zielY(i) - QY(i, zi)) > 0.01F Then
                        bewegt = True
                        Exit For
                    End If
                Next
                If bewegt Then Exit For
            Next
            If Not bewegt Then Return quelle

            ' Je Masche zwei Dreiecke. Die Texturkoordinaten bleiben auf dem UNVERZERRTEN Raster -
            ' verschoben wird nur die Lage der Punkte.
            Dim ecken As New List(Of SKPoint)(spalten * zeilen * 6)
            Dim textur As New List(Of SKPoint)(spalten * zeilen * 6)
            For zi = 0 To zeilen - 1
                For si = 0 To spalten - 1
                    Dim i00 = zi * (spalten + 1) + si
                    Dim i10 = i00 + 1
                    Dim i01 = (zi + 1) * (spalten + 1) + si
                    Dim i11 = i01 + 1
                    Dim t00 = New SKPoint(QX(i00, si), QY(i00, zi))
                    Dim t10 = New SKPoint(QX(i10, si + 1), QY(i10, zi))
                    Dim t01 = New SKPoint(QX(i01, si), QY(i01, zi + 1))
                    Dim t11 = New SKPoint(QX(i11, si + 1), QY(i11, zi + 1))
                    Dim p00 = New SKPoint(zielX(i00), zielY(i00))
                    Dim p10 = New SKPoint(zielX(i10), zielY(i10))
                    Dim p01 = New SKPoint(zielX(i01), zielY(i01))
                    Dim p11 = New SKPoint(zielX(i11), zielY(i11))
                    ecken.Add(p00) : ecken.Add(p10) : ecken.Add(p11)
                    textur.Add(t00) : textur.Add(t10) : textur.Add(t11)
                    ecken.Add(p00) : ecken.Add(p11) : ecken.Add(p01)
                    textur.Add(t00) : textur.Add(t11) : textur.Add(t01)
                Next
            Next

            Dim ergebnis = New SKBitmap(breite, hoehe, quelle.ColorType, quelle.AlphaType)
            Using canvas = New SKCanvas(ergebnis)
                canvas.Clear(SKColors.Transparent)
                ' Klemmen an den Raendern: ohne das zeigt eine ueber die Kante gezogene Masche
                ' durchsichtige Streifen, weil die Textur dort zu Ende ist.
                Using shader = quelle.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp)
                    Using paint = New SKPaint With {.IsAntialias = True, .Shader = shader}
                        Using netz = SKVertices.CreateCopy(SKVertexMode.Triangles, ecken.ToArray(), textur.ToArray(), Nothing)
                            canvas.DrawVertices(netz, SKBlendMode.Src, paint)
                        End Using
                    End Using
                End Using
            End Using
            Return ergebnis
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
