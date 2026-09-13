Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Automatische Geradestellung: die DREHUNG AUF DEN HORIZONT und die PERSPEKTIVKORREKTUR.
    '''
    ''' Zwei Funktionen, eine Messung. Beide leben davon, die geraden Kanten eines Fotos zu kennen -
    ''' der Horizont, die Fensterreihe, die Hauskante -, und beide lesen dasselbe Ergebnis
    ''' verschieden: die Drehung nimmt den gemeinsamen SCHRAEGSTAND aller Kanten, die
    ''' Perspektivkorrektur nimmt den Punkt, in dem sich die senkrechten (oder waagerechten)
    ''' schneiden.
    '''
    ''' Sie bleiben trotzdem GETRENNTE Funktionen, weil sie an verschiedenen Bildern versagen: eine
    ''' Landschaft hat einen Horizont und keine stuerzenden Linien, ein Gebaeude von unten hat
    ''' stuerzende Linien und keinen Horizont. Ein Knopf fuer beides koennte bei der einen Haelfte
    ''' richtig und bei der anderen falsch liegen, und der Nutzer koennte nur das Ganze zuruecknehmen.
    '''
    ''' Gemessen wird das UNBEARBEITETE Bild, und heraus kommen ABSOLUTE Werte fuer die Regler, keine
    ''' Zuschlaege - dieselbe Bauart wie bei der automatischen Bildverbesserung
    ''' (ImageProcessorAutoAdjust): zweimal gedrueckt ergibt dasselbe wie einmal, und jeder gesetzte
    ''' Wert laesst sich danach von Hand nachziehen.
    '''
    ''' Die Kantensuche ist eine Hough-Transformation, die vom GRADIENTEN gefuehrt wird: jeder
    ''' Bildpunkt stimmt nur fuer den einen Winkel ab, den seine eigene Kantenrichtung hergibt,
    ''' statt fuer alle. Das ist der Unterschied zwischen einer Messung, die im Werkzeug spuerbar
    ''' dauert, und einer, die man nicht bemerkt.
    ''' </summary>
    Partial Public Class ImageProcessor

        ''' <summary>Laengste Kante des Arbeitsbildes. Kanten eines Fotos sind lange Gebilde und
        ''' ueberstehen das Verkleinern muehelos; 640 reicht fuer eine Winkelaufloesung weit unter
        ''' einem Grad und haelt die Messung im Bereich weniger Millisekunden.</summary>
        Private Const AutoGeometryWorkingEdge As Integer = 640

        ''' <summary>Winkelfaecher der Hough-Tabelle ueber 180 Grad - 360 Faecher sind ein halbes
        ''' Grad je Fach. Feiner bringt nichts: die Kantenrichtung eines einzelnen Bildpunktes ist
        ''' selbst nicht genauer, und die Drehung kommt ohnehin aus dem Median vieler Kanten.</summary>
        Private Const AutoGeometryAngleBins As Integer = 360

        ''' <summary>Abstandsfaecher in Bildpunkten des Arbeitsbildes.</summary>
        Private Const AutoGeometryOffsetStep As Double = 2.0

        ''' <summary>So viele Kanten werden hoechstens weiterverwendet, die staerksten zuerst.</summary>
        Private Const AutoGeometryMaxLines As Integer = 48

        ''' <summary>Anteil der staerksten Bildpunkte, die ueberhaupt abstimmen duerfen. Eine feste
        ''' Schwelle auf die Gradientenstaerke waere an einem flauen Bild blind und an einem harten
        ''' Bild ueberflutet; der Anteil passt sich von selbst an.</summary>
        Private Const AutoGeometryEdgeFraction As Double = 0.08

        ''' <summary>Wie weit eine Kante von der Waagerechten oder Senkrechten abweichen darf, um als
        ''' "eigentlich gerade gemeint" zu zaehlen. Mehr als 15 Grad Schraegstand ist kein
        ''' verrissenes Foto mehr, sondern eine Diagonale im Motiv.</summary>
        Private Const AutoGeometryTiltWindow As Double = 15.0

        ''' <summary>Fenster fuer die Perspektive - hier darf es weiter sein: stuerzende Linien
        ''' weichen bei einem Gebaeude von unten deutlich mehr ab als ein verrissener Horizont.</summary>
        Private Const AutoGeometryAxisWindow As Double = 32.0

        ''' <summary>Klemmung der Drehung. Die Automatik soll ein schiefes Foto geraderuecken, nicht
        ''' ein Motiv umdeuten; was mehr als 12 Grad braucht, ist fast immer eine Fehlmessung.</summary>
        Private Const AutoGeometryMaxStraighten As Double = 12.0

        ''' <summary>Klemmung der Perspektivregler (der Regler selbst laeuft bis 100).</summary>
        Private Const AutoGeometryMaxPerspective As Double = 70.0

        ''' <summary>Totband: darunter bleibt der Wert auf 0. Ein Viertelgrad sieht niemand, und ein
        ''' Foto, das schon gerade steht, soll durch den Knopfdruck unveraendert herauskommen.</summary>
        Private Const AutoGeometryMinTilt As Double = 0.25
        Private Const AutoGeometryMinPerspective As Double = 2.0

        ''' <summary>So viele Kanten muessen sich einig sein, sonst gilt nichts als gemessen.</summary>
        Private Const AutoGeometryMinTiltLines As Integer = 3
        Private Const AutoGeometryMinAxisLines As Integer = 3

        ''' <summary>Fachbreite des Schraegstand-Histogramms, und wie weit um den Gipfel herum eine
        ''' Kante noch zu ihm zaehlt.</summary>
        Private Const AutoGeometryTiltBin As Double = 0.25
        Private Const AutoGeometryTiltCluster As Double = 1.0

        ''' <summary>Um so viel muss der Gipfel seinen naechststaerkeren Verfolger schlagen, und so
        ''' viel vom Gesamtgewicht muss mindestens auf ihn entfallen. Ohne diese Proben liefert die
        ''' Messung auch dann eine Zahl, wenn die Kanten ueber das ganze Fenster verstreut sind -
        ''' also gerade dann, wenn das Bild gar keine vorherrschende Richtung hat.</summary>
        Private Const AutoGeometryTiltDominance As Double = 2.5
        Private Const AutoGeometryTiltConsensus As Double = 0.12

        ''' <summary>Der Fluchtpunkt muss AUSSERHALB des Bildes liegen (hier: mindestens eine viertel
        ''' Bildhoehe jenseits der Kante). Ein Fluchtpunkt mitten im Bild bedeutet Kanten, die sich im
        ''' Motiv kreuzen - ein Dachfirst, ein Zaun, Schienen -, und keine stuerzenden Linien.</summary>
        Private Const AutoGeometryMinVanishingDistance As Double = 1.25

        ''' <summary>Eine Kante des Bildes: ihre Normalenrichtung und ihr Abstand vom Ursprung, dazu
        ''' wie kraeftig sie abgestimmt hat.
        '''
        ''' <para>Die Gerade ist <c>x·cos(NormalAngle) + y·sin(NormalAngle) = Offset</c>. Der
        ''' NORMALENwinkel und nicht der Richtungswinkel, weil die Hough-Tabelle ihn direkt vom
        ''' Gradienten bekommt: der Gradient steht senkrecht auf der Kante. Ein Winkel um 0 Grad ist
        ''' damit eine SENKRECHTE Kante (ihr Helligkeitssprung laeuft waagerecht), ein Winkel um 90
        ''' Grad eine waagerechte.</para></summary>
        Public Class AutoGeometryLine
            Public Property NormalAngleDegrees As Double
            Public Property Offset As Double
            Public Property Strength As Double
        End Class

        ''' <summary>Ergebnis der Kantenmessung. Die beiden Funktionen lesen jeweils ihren Teil, und
        ''' jeder Teil hat sein eigenes "gemessen": ein Foto kann einen klaren Horizont und keine
        ''' brauchbaren stuerzenden Linien haben, und umgekehrt.</summary>
        Public Class AutoGeometryResult
            ''' <summary>False, wenn schon die Kantensuche nichts hergab (kein Bild, kein Kontrast).</summary>
            Public Property HasMeasurement As Boolean = False

            ''' <summary>True, wenn sich genug Kanten auf einen Schraegstand geeinigt haben.</summary>
            Public Property HasHorizon As Boolean = False
            ''' <summary>Absoluter Wert fuer den Regler "Ausrichten" (Grad).</summary>
            Public Property StraightenDegrees As Double = 0

            ''' <summary>True, wenn mindestens eine der beiden Achsen einen brauchbaren Fluchtpunkt hat.</summary>
            Public Property HasPerspective As Boolean = False
            ''' <summary>Absolute Werte fuer die beiden Perspektivregler (-100 bis 100), gerechnet OHNE
            ''' Spiegeln und Vierteldrehung - also so, wie das gemessene Bild dalag.</summary>
            Public Property PerspectiveHorizontal As Double = 0
            Public Property PerspectiveVertical As Double = 0

            ''' <summary>Die Fluchtpunkte selbst, in Bildpunkten des GEMESSENEN Bildes (Ursprung links
            ''' oben). Der Aufrufer braucht sie, weil die Verzerrungsstufe das Bild erst NACH Spiegeln,
            ''' Vierteldrehung und Begradigung sieht: eine Vierteldrehung vertauscht die beiden Achsen,
            ''' ein Spiegel kehrt eine Richtung um. Wer nur die Reglerwerte oben nimmt, korrigiert bei
            ''' einem hochkant gedrehten Foto die falsche Achse.</summary>
            Public Property HasVerticalVanishing As Boolean = False
            Public Property VerticalVanishingX As Double = 0
            Public Property VerticalVanishingY As Double = 0
            Public Property HasHorizontalVanishing As Boolean = False
            Public Property HorizontalVanishingX As Double = 0
            Public Property HorizontalVanishingY As Double = 0

            ''' <summary>Masse des gemessenen Bildes, zu denen die Fluchtpunkte gehoeren.</summary>
            Public Property MeasuredWidth As Integer = 0
            Public Property MeasuredHeight As Integer = 0

            ''' <summary>Wie viele Kanten die Messung gefunden hat - fuer die Diagnose.</summary>
            Public Property LineCount As Integer = 0

            ''' <summary>Die gefundenen Kanten selbst, die staerksten zuerst. Nur fuer die Diagnose:
            ''' ohne sie laesst sich eine Fehlmessung nicht auseinandernehmen, sondern nur
            ''' feststellen.</summary>
            Public Property Lines As IReadOnlyList(Of AutoGeometryLine) = Array.Empty(Of AutoGeometryLine)()

            ''' <summary>True, wenn nichts zu tun ist: gemessen, aber beide Werte im Totband.</summary>
            Public Function IsNeutral() As Boolean
                Return Math.Abs(StraightenDegrees) < AutoGeometryMinTilt AndAlso
                       Math.Abs(PerspectiveHorizontal) < AutoGeometryMinPerspective AndAlso
                       Math.Abs(PerspectiveVertical) < AutoGeometryMinPerspective
            End Function
        End Class

        ''' <summary>Misst Schraegstand und Fluchtpunkte eines Bildes. Fasst nichts an - der Aufrufer
        ''' entscheidet, welchen Teil des Ergebnisses er in welche Regler schreibt.</summary>
        Public Shared Function AnalyzeAutoGeometry(source As SKBitmap) As AutoGeometryResult
            Dim result As New AutoGeometryResult()
            If source Is Nothing OrElse source.Width < 32 OrElse source.Height < 32 Then Return result

            Dim width As Integer = 0, height As Integer = 0
            Dim opaque As Boolean() = Nothing
            Dim luma = BuildAutoGeometryLuma(source, width, height, opaque)
            If luma Is Nothing Then Return result
            result.HasMeasurement = True

            Dim lines = FindAutoGeometryLines(luma, opaque, width, height)
            result.LineCount = lines.Count
            result.Lines = lines
            If lines.Count = 0 Then Return result

            ' ── Drehung auf den Horizont ────────────────────────────────────────────────────
            Dim tilt As Double = 0
            If TryMeasureAutoTilt(lines, tilt) Then
                ' DAS VORZEICHEN DREHT SICH. Der Schraegstand sagt, wie die Kante im Bild LIEGT;
                ' der Regler sagt, wie das Bild gedreht werden soll, um sie hinzulegen. Die
                ' Begradigung dreht bei positivem Wert im Uhrzeigersinn (ImageProcessor, Punktweg:
                ' x' = cos·dx - sin·dy, y' = sin·dx + cos·dy bei nach unten wachsendem y).
                Dim straighten = AutoGeometryClamp(-tilt, -AutoGeometryMaxStraighten, AutoGeometryMaxStraighten)
                If Math.Abs(straighten) >= AutoGeometryMinTilt Then
                    result.StraightenDegrees = straighten
                    result.HasHorizon = True
                Else
                    ' Gemessen und fuer gerade befunden: das ist ein Ergebnis, kein Fehlschlag.
                    result.HasHorizon = True
                End If
            End If

            ' ── Perspektivkorrektur ─────────────────────────────────────────────────────────
            '
            ' Die senkrechten Kanten ergeben den Regler SENKRECHT (stuerzende Linien), die
            ' waagerechten den Regler WAAGERECHT. Beide werden einzeln gemessen: ein Gebaeude von
            ' unten hat stuerzende Linien, aber selten eine brauchbare waagerechte Flucht.
            result.MeasuredWidth = width
            result.MeasuredHeight = height

            Dim verticalPoint As SKPoint, horizontalPoint As SKPoint
            Dim hasVertical = TryMeasureAutoVanishingPoint(lines, True, width, height, verticalPoint)
            Dim hasHorizontal = TryMeasureAutoVanishingPoint(lines, False, width, height, horizontalPoint)

            If hasVertical Then
                result.HasVerticalVanishing = True
                result.VerticalVanishingX = verticalPoint.X
                result.VerticalVanishingY = verticalPoint.Y
                result.PerspectiveVertical = PerspectiveSliderFromVanishing(height, verticalPoint.Y)
            End If
            If hasHorizontal Then
                result.HasHorizontalVanishing = True
                result.HorizontalVanishingX = horizontalPoint.X
                result.HorizontalVanishingY = horizontalPoint.Y
                result.PerspectiveHorizontal = PerspectiveSliderFromVanishing(width, horizontalPoint.X)
            End If
            result.HasPerspective = Math.Abs(result.PerspectiveVertical) >= AutoGeometryMinPerspective OrElse
                                    Math.Abs(result.PerspectiveHorizontal) >= AutoGeometryMinPerspective

            Return result
        End Function

        ''' <summary>Verkleinertes Graubild plus eine Karte der voll deckenden Bildpunkte.
        '''
        ''' <para>Die Deckungskarte ist noetig, weil ein Bild mit durchsichtigen Raendern (eine
        ''' gekippte Leinwand, ein freigestelltes Objekt) an der Grenze zur Durchsichtigkeit einen
        ''' knallharten Helligkeitssprung hat. Das ist die staerkste "Kante" im ganzen Bild und
        ''' zugleich keine: sie gehoert der Leinwand, nicht dem Motiv.</para></summary>
        Private Shared Function BuildAutoGeometryLuma(source As SKBitmap, ByRef width As Integer,
                                                      ByRef height As Integer,
                                                      ByRef opaque As Boolean()) As Double()
            width = 0 : height = 0 : opaque = Nothing

            Dim longest = Math.Max(source.Width, source.Height)
            Dim scale = If(longest > AutoGeometryWorkingEdge, AutoGeometryWorkingEdge / CDbl(longest), 1.0)
            Dim targetWidth = Math.Max(32, CInt(Math.Round(source.Width * scale)))
            Dim targetHeight = Math.Max(32, CInt(Math.Round(source.Height * scale)))

            Dim working As SKBitmap = Nothing
            Dim ownsWorking = False
            Try
                If targetWidth = source.Width AndAlso targetHeight = source.Height Then
                    working = source
                Else
                    working = source.Resize(New SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul),
                                            New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
                    ownsWorking = True
                End If
                If working Is Nothing Then Return Nothing

                Dim buffer As Byte() = Nothing
                Dim stride As Integer = 0
                Dim ri As Integer, gi As Integer, bi As Integer, ai As Integer
                If Not TryBorrowRgbaLikeBuffer(working, buffer, stride, ri, gi, bi, ai) Then Return Nothing

                width = working.Width
                height = working.Height
                Dim luma = New Double(width * height - 1) {}
                Dim solid = New Boolean(width * height - 1) {}
                For y As Integer = 0 To height - 1
                    Dim row = y * stride
                    Dim target = y * width
                    For x As Integer = 0 To width - 1
                        Dim o = row + x * 4
                        luma(target + x) = 0.299 * buffer(o + ri) + 0.587 * buffer(o + gi) + 0.114 * buffer(o + bi)
                        solid(target + x) = buffer(o + ai) >= 250
                    Next
                Next
                opaque = solid
                Return luma
            Finally
                If ownsWorking AndAlso working IsNot Nothing Then working.Dispose()
            End Try
        End Function

        ''' <summary>Gauss-Weichzeichner, getrennt in zwei Durchlaeufe (erst waagerecht, dann
        ''' senkrecht). Sigma 1,2 als Kompromiss: genug, um die Rasterung einer Kante glattzuziehen,
        ''' wenig genug, dass zwei dicht beieinander liegende Kanten zwei bleiben.
        '''
        ''' <para>Am Rand wird der letzte Bildpunkt fortgesetzt statt abgeschnitten - ein Abschneiden
        ''' erzeugte dort einen Helligkeitsabfall und damit eine Kante, die es nicht gibt.</para></summary>
        Private Shared Function BlurAutoGeometryLuma(luma As Double(), width As Integer, height As Integer) As Double()
            Dim kernel = New Double() {0.0545, 0.2442, 0.4026, 0.2442, 0.0545}
            Dim radius = 2
            Dim pass = New Double(width * height - 1) {}
            Dim result = New Double(width * height - 1) {}

            For y As Integer = 0 To height - 1
                Dim row = y * width
                For x As Integer = 0 To width - 1
                    Dim sum As Double = 0
                    For k As Integer = -radius To radius
                        Dim sx = x + k
                        If sx < 0 Then sx = 0
                        If sx > width - 1 Then sx = width - 1
                        sum += luma(row + sx) * kernel(k + radius)
                    Next
                    pass(row + x) = sum
                Next
            Next

            For y As Integer = 0 To height - 1
                For x As Integer = 0 To width - 1
                    Dim sum As Double = 0
                    For k As Integer = -radius To radius
                        Dim sy = y + k
                        If sy < 0 Then sy = 0
                        If sy > height - 1 Then sy = height - 1
                        sum += pass(sy * width + x) * kernel(k + radius)
                    Next
                    result(y * width + x) = sum
                Next
            Next
            Return result
        End Function

        ''' <summary>Die geraden Kanten des Bildes, die staerksten zuerst.</summary>
        Private Shared Function FindAutoGeometryLines(luma As Double(), opaque As Boolean(),
                                                      width As Integer, height As Integer) As List(Of AutoGeometryLine)
            Dim lines As New List(Of AutoGeometryLine)()

            ' ── 0) Weichzeichnen, und das ist keine Kosmetik ─────────────────────────────────
            '
            ' Ein 3x3-Sobel auf einer harten, duennen Kante liefert eine RICHTUNG, die von Bildpunkt
            ' zu Bildpunkt um zweistellige Gradzahlen springt: die Treppenstufen der Rasterung
            ' schlagen voll durch. Gemessen an einer um 2,5 Grad gekippten Linie zerfiel deren
            ' Abstimmung dadurch auf ueber tausend Faecher - die Kante kam mit einem Hundertfuenfzigstel
            ' der Staerke und um ein Grad verdreht heraus, waehrend dieselbe Linie ungekippt einen
            ' sauberen Gipfel ergab. Ein Foto bringt denselben Fehler als Sensorrauschen mit.
            luma = BlurAutoGeometryLuma(luma, width, height)

            ' ── 1) Sobel, und daraus die Schwelle als Perzentil ──────────────────────────────
            Dim magnitude = New Double(width * height - 1) {}
            Dim angle = New Double(width * height - 1) {}
            Dim histogram = New Integer(511) {}
            Dim strongest As Double = 0
            Dim samples As Long = 0

            For y As Integer = 1 To height - 2
                For x As Integer = 1 To width - 2
                    Dim c = y * width + x
                    ' Nur wo die ganze 3x3-Nachbarschaft deckt - siehe BuildAutoGeometryLuma.
                    If Not (opaque(c) AndAlso opaque(c - 1) AndAlso opaque(c + 1) AndAlso
                            opaque(c - width) AndAlso opaque(c + width) AndAlso
                            opaque(c - width - 1) AndAlso opaque(c - width + 1) AndAlso
                            opaque(c + width - 1) AndAlso opaque(c + width + 1)) Then Continue For

                    Dim gx = (luma(c - width + 1) + 2.0 * luma(c + 1) + luma(c + width + 1)) -
                             (luma(c - width - 1) + 2.0 * luma(c - 1) + luma(c + width - 1))
                    Dim gy = (luma(c + width - 1) + 2.0 * luma(c + width) + luma(c + width + 1)) -
                             (luma(c - width - 1) + 2.0 * luma(c - width) + luma(c - width + 1))

                    Dim m = Math.Sqrt(gx * gx + gy * gy)
                    magnitude(c) = m
                    If m > strongest Then strongest = m
                    ' atan2(gy, gx) ist die Richtung des Gradienten und damit die NORMALE der Kante.
                    ' Auf [0,180) gefaltet, weil eine Gerade keine Vorderseite hat: ein hell-dunkel-
                    ' und ein dunkel-hell-Uebergang derselben Kante landen so im selben Fach.
                    Dim a = Math.Atan2(gy, gx) * 180.0 / Math.PI
                    If a < 0 Then a += 180.0
                    If a >= 180.0 Then a -= 180.0
                    angle(c) = a
                    samples += 1
                Next
            Next

            If samples = 0 OrElse strongest <= 0.0001 Then Return lines

            For y As Integer = 1 To height - 2
                For x As Integer = 1 To width - 2
                    Dim m = magnitude(y * width + x)
                    If m <= 0 Then Continue For
                    Dim slot = CInt(Math.Floor(m / strongest * 511.0))
                    If slot < 0 Then slot = 0
                    If slot > 511 Then slot = 511
                    histogram(slot) += 1
                Next
            Next

            Dim wanted = CLng(Math.Floor(samples * AutoGeometryEdgeFraction))
            If wanted < 64 Then wanted = Math.Min(samples, 64L)
            Dim seen As Long = 0
            Dim threshold As Double = strongest
            For slot As Integer = 511 To 0 Step -1
                seen += histogram(slot)
                If seen >= wanted Then
                    threshold = slot / 511.0 * strongest
                    Exit For
                End If
            Next

            ' ── 2) Abstimmung ───────────────────────────────────────────────────────────────
            Dim diagonal = Math.Sqrt(width * CDbl(width) + height * CDbl(height))
            Dim offsetBins = CInt(Math.Ceiling(diagonal * 2.0 / AutoGeometryOffsetStep)) + 2
            Dim accumulator = New Double(AutoGeometryAngleBins * offsetBins - 1) {}
            Dim angleStep = 180.0 / AutoGeometryAngleBins

            ' Der Winkel JE FACH, einmal ausgerechnet. Er wird gleich zweimal gebraucht: fuer den
            ' Abstand beim Abstimmen und fuer die Gerade, die am Ende herauskommt.
            Dim cosTable = New Double(AutoGeometryAngleBins - 1) {}
            Dim sinTable = New Double(AutoGeometryAngleBins - 1) {}
            For b As Integer = 0 To AutoGeometryAngleBins - 1
                Dim radians = b * angleStep * Math.PI / 180.0
                cosTable(b) = Math.Cos(radians)
                sinTable(b) = Math.Sin(radians)
            Next

            For y As Integer = 1 To height - 2
                For x As Integer = 1 To width - 2
                    Dim c = y * width + x
                    Dim m = magnitude(c)
                    If m < threshold Then Continue For

                    ' BILINEAR VERTEILT statt ins naechste Fach geworfen: eine Kante, die genau
                    ' zwischen zwei Faechern liegt, zerfiele sonst in zwei halbe Gipfel, von denen
                    ' keiner die Schwelle erreicht.
                    Dim angleExact = angle(c) / angleStep
                    Dim angleLow = CInt(Math.Floor(angleExact))
                    Dim angleFraction = angleExact - angleLow

                    For da As Integer = 0 To 1
                        Dim angleWeight = If(da = 0, 1.0 - angleFraction, angleFraction)
                        If angleWeight <= 0 Then Continue For
                        Dim ab = angleLow + da
                        ' Der Winkel laeuft im Kreis: hinter 180 Grad kommt wieder 0.
                        If ab >= AutoGeometryAngleBins Then ab -= AutoGeometryAngleBins
                        If ab < 0 Then ab += AutoGeometryAngleBins

                        ' DER ABSTAND GEHOERT ZUM FACH, nicht zum Bildpunkt. Rechnet man ihn einmal
                        ' mit dem genauen Winkel des Punktes und legt ihn dann in beide Faecher, geht
                        ' das an der NAHT schief: dort ist das Nachbarfach von 179,5 Grad die 0, und
                        ' beim Sprung ueber 180 Grad kehrt der Abstand sein Vorzeichen um. Genau dort
                        ' liegen die SENKRECHTEN Kanten - gemessen kippte ihr Winkel dadurch um mehr
                        ' als ein Grad, waehrend die waagerechten (weit weg von der Naht) stimmten.
                        Dim offset = x * cosTable(ab) + y * sinTable(ab)
                        Dim offsetExact = (offset + diagonal) / AutoGeometryOffsetStep
                        Dim offsetLow = CInt(Math.Floor(offsetExact))
                        Dim offsetFraction = offsetExact - offsetLow

                        For od As Integer = 0 To 1
                            Dim ob = offsetLow + od
                            If ob < 0 OrElse ob >= offsetBins Then Continue For
                            Dim offsetWeight = If(od = 0, 1.0 - offsetFraction, offsetFraction)
                            If offsetWeight <= 0 Then Continue For
                            accumulator(ab * offsetBins + ob) += m * angleWeight * offsetWeight
                        Next
                    Next
                Next
            Next

            ' ── 3) Gipfel ───────────────────────────────────────────────────────────────────
            Dim peak As Double = 0
            For i As Integer = 0 To accumulator.Length - 1
                If accumulator(i) > peak Then peak = accumulator(i)
            Next
            If peak <= 0 Then Return lines
            Dim peakThreshold = peak * 0.02

            For ab As Integer = 0 To AutoGeometryAngleBins - 1
                For ob As Integer = 0 To offsetBins - 1
                    Dim value = accumulator(ab * offsetBins + ob)
                    If value < peakThreshold Then Continue For

                    ' Hoechster Wert seiner Umgebung? Das Fenster ist im Abstand weiter als im
                    ' Winkel, weil eine kraeftige Kante ueber mehrere Abstandsfaecher schmiert.
                    Dim isPeak = True
                    For dab As Integer = -1 To 1
                        Dim nab = ab + dab
                        If nab < 0 OrElse nab >= AutoGeometryAngleBins Then Continue For
                        For dob As Integer = -3 To 3
                            If dab = 0 AndAlso dob = 0 Then Continue For
                            Dim nob = ob + dob
                            If nob < 0 OrElse nob >= offsetBins Then Continue For
                            If accumulator(nab * offsetBins + nob) > value Then
                                isPeak = False
                                Exit For
                            End If
                        Next
                        If Not isPeak Then Exit For
                    Next
                    If Not isPeak Then Continue For

                    ' DAS FACH MEINT SEINEN ANFANG, nicht seine Mitte: beim bilinearen Verteilen
                    ' bekommt Fach n das volle Gewicht, wenn der Wert GENAU auf n liegt. Die Mitte
                    ' anzugeben verschob jede Messung um ein halbes Fach - gemessen stand ein
                    ' kerzengerades Testbild danach auf 0,25 Grad schief.
                    '
                    ' ZWISCHEN DEN FAECHERN wird geschaetzt: durch den Gipfel und seine beiden
                    ' Nachbarn passt genau eine Parabel, und deren Scheitel liegt fast immer neben
                    ' dem Fach. Ohne diese Schaetzung war die Drehung auf ein halbes Grad gerastert,
                    ' und eine um 4 Grad gekippte Kante kam als 3,5 heraus.
                    Dim angleShift As Double = 0
                    If ab > 0 AndAlso ab < AutoGeometryAngleBins - 1 Then
                        angleShift = AutoGeometryPeakShift(accumulator((ab - 1) * offsetBins + ob), value,
                                                           accumulator((ab + 1) * offsetBins + ob))
                    End If
                    Dim offsetShift As Double = 0
                    If ob > 0 AndAlso ob < offsetBins - 1 Then
                        offsetShift = AutoGeometryPeakShift(accumulator(ab * offsetBins + ob - 1), value,
                                                            accumulator(ab * offsetBins + ob + 1))
                    End If

                    lines.Add(New AutoGeometryLine With {
                        .NormalAngleDegrees = AutoGeometryFoldAngle((ab + angleShift) * angleStep),
                        .Offset = (ob + offsetShift) * AutoGeometryOffsetStep - diagonal,
                        .Strength = value})
                Next
            Next

            If lines.Count > AutoGeometryMaxLines Then
                lines = lines.OrderByDescending(Function(l) l.Strength).Take(AutoGeometryMaxLines).ToList()
            Else
                lines = lines.OrderByDescending(Function(l) l.Strength).ToList()
            End If
            Return lines
        End Function

        ''' <summary>Wo der Scheitel der Parabel durch drei benachbarte Faecher liegt, in Faechern
        ''' vom mittleren aus. Auf ein halbes Fach geklemmt: weiter draussen ist der Gipfel nicht der
        ''' Gipfel, sondern der Nachbar.</summary>
        Private Shared Function AutoGeometryPeakShift(before As Double, middle As Double, after As Double) As Double
            Dim denominator = before - 2.0 * middle + after
            If Math.Abs(denominator) < 0.0000001 Then Return 0
            Dim shift = 0.5 * (before - after) / denominator
            Return AutoGeometryClamp(shift, -0.5, 0.5)
        End Function

        ''' <summary>Haelt einen Winkel im Bereich von 0 bis unter 180 Grad.</summary>
        Private Shared Function AutoGeometryFoldAngle(degrees As Double) As Double
            Dim folded = degrees Mod 180.0
            If folded < 0 Then folded += 180.0
            Return folded
        End Function

        ''' <summary>Der Schraegstand einer Kante: wie weit sie von der naechsten Achse abweicht,
        ''' in Grad und mit Vorzeichen.
        '''
        ''' <para>EINE Formel fuer beide Achsen, und das ist kein Kunstgriff, sondern der Kern der
        ''' Sache: ein um zwei Grad verrissenes Foto zeigt seine zwei Grad an den waagerechten
        ''' Kanten UND an den senkrechten. Damit stimmen der Horizont und die Hauskante desselben
        ''' Bildes gemeinsam ueber dieselbe Drehung ab, statt zwei getrennte Messungen zu
        ''' brauchen.</para></summary>
        Private Shared Function AutoGeometryTilt(normalAngleDegrees As Double) As Double
            Dim folded = (normalAngleDegrees + 45.0) Mod 90.0
            If folded < 0 Then folded += 90.0
            Return folded - 45.0
        End Function

        ''' <summary>Abweichung von der Senkrechten (achse = True) oder der Waagerechten, in Grad
        ''' und mit Vorzeichen. Eine senkrechte Kante hat den Normalenwinkel 0, eine waagerechte 90.</summary>
        Private Shared Function AutoGeometryAxisDeviation(normalAngleDegrees As Double, vertical As Boolean) As Double
            Dim deviation = normalAngleDegrees - If(vertical, 0.0, 90.0)
            If deviation > 90.0 Then deviation -= 180.0
            If deviation <= -90.0 Then deviation += 180.0
            Return deviation
        End Function

        ''' <summary>Der gemeinsame Schraegstand aller fast geraden Kanten.
        '''
        ''' <para>GESUCHT WIRD EIN GIPFEL, KEIN MITTELWERT und auch kein Median. An echten Fotos
        ''' gemessen ist das der ganze Unterschied: ein Landschaftsfoto liefert einen ueberm&#228;chtigen
        ''' Horizont und daneben vierzig schwache Kanten aus Gras, Wolken und Bewuchs, die in alle
        ''' Richtungen zeigen. In einer SUMME stimmen die vierzig den einen nieder, so schwach jede
        ''' einzelne auch ist - der Median wanderte weg, und die Einigkeitsprobe fiel durch, obwohl
        ''' das Bild einen tadellosen Horizont hat. Im Histogramm sammelt sich das Gewicht des
        ''' Horizonts dagegen in EINEM Fach, waehrend sich das Rauschen ueber alle verteilt.</para>
        '''
        ''' <para>Der Gipfel gibt die Richtung vor, den genauen Wert macht der gewichtete Mittelwert
        ''' der Kanten um ihn herum: das Fach ist ein viertel Grad breit, die Kanten darin sind es
        ''' nicht.</para></summary>
        Private Shared Function TryMeasureAutoTilt(lines As List(Of AutoGeometryLine), ByRef tilt As Double) As Boolean
            tilt = 0
            Dim values As New List(Of Double)()
            Dim weights As New List(Of Double)()
            Dim total As Double = 0
            For Each line In lines
                Dim value = AutoGeometryTilt(line.NormalAngleDegrees)
                If Math.Abs(value) > AutoGeometryTiltWindow Then Continue For
                values.Add(value)
                weights.Add(line.Strength)
                total += line.Strength
            Next
            If values.Count < AutoGeometryMinTiltLines OrElse total <= 0 Then Return False

            ' Gewichtetes Histogramm ueber das Fenster, bilinear verteilt - eine Kante genau
            ' zwischen zwei Faechern zerfiele sonst in zwei halbe Gipfel.
            Dim bins = CInt(Math.Round(AutoGeometryTiltWindow * 2.0 / AutoGeometryTiltBin)) + 1
            Dim histogram = New Double(bins - 1) {}
            For i As Integer = 0 To values.Count - 1
                Dim exact = (values(i) + AutoGeometryTiltWindow) / AutoGeometryTiltBin
                Dim low = CInt(Math.Floor(exact))
                Dim fraction = exact - low
                If low >= 0 AndAlso low < bins Then histogram(low) += weights(i) * (1.0 - fraction)
                If low + 1 >= 0 AndAlso low + 1 < bins Then histogram(low + 1) += weights(i) * fraction
            Next

            Dim best As Integer = 0
            For b As Integer = 1 To bins - 1
                If histogram(b) > histogram(best) Then best = b
            Next
            Dim peak = best * AutoGeometryTiltBin - AutoGeometryTiltWindow

            ' Den genauen Wert aus den Kanten um den Gipfel.
            Dim near As Double = 0, sum As Double = 0
            For i As Integer = 0 To values.Count - 1
                If Math.Abs(values(i) - peak) > AutoGeometryTiltCluster Then Continue For
                near += weights(i)
                sum += values(i) * weights(i)
            Next
            If near <= 0 Then Return False

            ' EINIGKEITSPROBE: der Gipfel muss seinen VERFOLGER deutlich hinter sich lassen.
            '
            ' Gegen die SUMME zu pruefen waere das Naheliegende und ist falsch, an echten Fotos
            ' gemessen: eine Landschaft brachte ihren Horizont mit 24,5 Prozent des Gesamtgewichts
            ' ein, der naechststaerkere Haufen kam auf 3,6 - also siebenmal weniger. Trotzdem fiel
            ' die Summenprobe durch, weil sich vierzig schwache Kanten aus Gras und Wolken auf die
            ' restlichen drei Viertel verteilten. Ein Bild OHNE vorherrschende Richtung sieht ganz
            ' anders aus: dort liegen Gipfel und Verfolger gleichauf.
            ' Verglichen werden die STAERKSTE Kante im Gipfel und die staerkste ausserhalb, nicht die
            ' Summen der beiden Haufen. Ein Bild ohne vorherrschende Richtung hat viele aehnlich
            ' starke Kanten; ihre Summen liegen dann je nach Zufall auch mal zwei zu eins
            ' auseinander, ihre Spitzen aber nicht.
            Dim strongestInside As Double = 0, strongestOutside As Double = 0
            For i As Integer = 0 To values.Count - 1
                Dim distance = Math.Abs(values(i) - peak)
                If distance <= AutoGeometryTiltCluster Then
                    strongestInside = Math.Max(strongestInside, weights(i))
                ElseIf distance > AutoGeometryTiltCluster * 2.0 Then
                    strongestOutside = Math.Max(strongestOutside, weights(i))
                End If
            Next
            If strongestInside < strongestOutside * AutoGeometryTiltDominance Then Return False
            If near / total < AutoGeometryTiltConsensus Then Return False

            tilt = sum / near
            Return True
        End Function

        ''' <summary>Der Reglerwert fuer eine Achse, aus dem Fluchtpunkt ihrer Kanten.
        '''
        ''' <para>Die Rechnung ist die UMKEHRUNG von <see cref="ImageGeometryMapper.WarpCorners"/>:
        ''' dort schiebt der Regler die beiden Bildkanten um <c>dx = Breite · Regler/100 · 0,5/2</c>
        ''' auseinander, und diese Verschiebung gehoert zu genau einer Fluchtlinie. Setzt man beide
        ''' gleich, bleibt <c>Regler = 200 · (D−1) / (D+1)</c> mit <c>D = 1 − Bildhoehe /
        ''' Fluchtpunkt</c>. Bei einem Fluchtpunkt zwei Bildhoehen ueber dem oberen Rand sind das 40
        ''' von 100 - und bei Vollausschlag genau die halbe Bildbreite Mehrbreite oben, wie es die
        ''' Kennlinie dort beschreibt.</para>
        '''
        ''' <para>Der Regler kippt SYMMETRISCH um die Bildmitte, der gemessene Fluchtpunkt liegt aber
        ''' selten genau ueber ihr. Was daneben liegt, ist eine Drehung - und die gehoert der anderen
        ''' Funktion, nicht dieser. Deshalb geht hier nur die eine Koordinate ein.</para></summary>
        Private Shared Function TryMeasureAutoVanishingPoint(lines As List(Of AutoGeometryLine), vertical As Boolean,
                                                             width As Integer, height As Integer,
                                                             ByRef vanishing As SKPoint) As Boolean
            vanishing = New SKPoint(0, 0)

            Dim selected As New List(Of AutoGeometryLine)()
            For Each line In lines
                If Math.Abs(AutoGeometryAxisDeviation(line.NormalAngleDegrees, vertical)) <= AutoGeometryAxisWindow Then
                    selected.Add(line)
                End If
            Next
            If selected.Count < AutoGeometryMinAxisLines Then Return False

            ' Fluchtpunkt als gewichtete Ausgleichsloesung von x·cos + y·sin = Abstand.
            Dim sumXx As Double = 0, sumXy As Double = 0, sumYy As Double = 0
            Dim sumXr As Double = 0, sumYr As Double = 0
            Dim spread As Double = 0
            Dim reference = AutoGeometryAxisDeviation(selected(0).NormalAngleDegrees, vertical)
            For Each line In selected
                Dim radians = line.NormalAngleDegrees * Math.PI / 180.0
                Dim cx = Math.Cos(radians), cy = Math.Sin(radians)
                Dim weight = line.Strength
                sumXx += weight * cx * cx
                sumXy += weight * cx * cy
                sumYy += weight * cy * cy
                sumXr += weight * cx * line.Offset
                sumYr += weight * cy * line.Offset
                spread = Math.Max(spread, Math.Abs(AutoGeometryAxisDeviation(line.NormalAngleDegrees, vertical) - reference))
            Next

            ' LAUFEN DIE KANTEN PARALLEL, gibt es keinen Fluchtpunkt - und genau das ist der
            ' Normalfall eines geraden Fotos. Ohne diese Probe rechnet die Ausgleichsloesung an
            ' einem beinahe singulaeren Gleichungssystem und liefert eine grosse Zufallszahl.
            If spread < 0.75 Then Return False

            Dim determinant = sumXx * sumYy - sumXy * sumXy
            If Math.Abs(determinant) < 0.000001 * Math.Max(1.0, sumXx + sumYy) Then Return False

            Dim vanishingX = (sumYy * sumXr - sumXy * sumYr) / determinant
            Dim vanishingY = (sumXx * sumYr - sumXy * sumXr) / determinant

            If Double.IsNaN(vanishingX) OrElse Double.IsInfinity(vanishingX) OrElse
               Double.IsNaN(vanishingY) OrElse Double.IsInfinity(vanishingY) Then Return False

            Dim extent = If(vertical, CDbl(height), CDbl(width))
            Dim coordinate = If(vertical, vanishingY, vanishingX)

            ' Der Fluchtpunkt muss weit genug AUSSERHALB liegen. Innerhalb des Bildes kreuzen sich
            ' Kanten des Motivs, nicht stuerzende Linien.
            Dim distance = If(coordinate < 0, -coordinate, coordinate - extent)
            If distance < extent * (AutoGeometryMinVanishingDistance - 1.0) Then Return False
            If Math.Abs(coordinate) > extent * 200.0 Then Return False

            vanishing = New SKPoint(CSng(vanishingX), CSng(vanishingY))
            Return True
        End Function

        ''' <summary>Der Reglerwert fuer eine Achse, aus der Fluchtpunkt-Koordinate quer zu ihr.
        ''' <paramref name="extent"/> ist die Bildkante in derselben Richtung, gemessen vom selben
        ''' Ursprung. 0, wenn daraus keine sinnvolle Korrektur wird.
        '''
        ''' <para>Die Rechnung ist die UMKEHRUNG von <see cref="ImageGeometryMapper.WarpCorners"/>:
        ''' dort schiebt der Regler die beiden Bildkanten um <c>dx = Breite · Regler/100 · 0,5/2</c>
        ''' auseinander, und diese Verschiebung gehoert zu genau einer Fluchtlinie. Setzt man beide
        ''' gleich, bleibt <c>Regler = 200 · (D−1) / (D+1)</c> mit <c>D = 1 − Bildkante /
        ''' Fluchtpunkt</c>. Bei einem Fluchtpunkt zwei Bildhoehen ueber dem oberen Rand sind das 40
        ''' von 100 - und bei Vollausschlag genau die halbe Bildbreite Mehrbreite oben, wie es die
        ''' Kennlinie dort beschreibt.</para>
        '''
        ''' <para>Der Regler kippt SYMMETRISCH um die Bildmitte, der gemessene Fluchtpunkt liegt aber
        ''' selten genau ueber ihr. Was daneben liegt, ist eine Drehung - und die gehoert der anderen
        ''' Funktion, nicht dieser. Deshalb geht hier nur die eine Koordinate ein.</para></summary>
        Public Shared Function PerspectiveSliderFromVanishing(extent As Double, coordinate As Double) As Double
            If extent <= 0 Then Return 0
            If Double.IsNaN(coordinate) OrElse Double.IsInfinity(coordinate) Then Return 0
            If Math.Abs(coordinate) < 0.000001 Then Return 0

            Dim d = 1.0 - extent / coordinate
            If d <= -0.999999 Then Return 0
            Dim value = 200.0 * (d - 1.0) / (d + 1.0)
            value = AutoGeometryClamp(value, -AutoGeometryMaxPerspective, AutoGeometryMaxPerspective)
            If Math.Abs(value) < AutoGeometryMinPerspective Then Return 0
            Return value
        End Function

        ''' <summary>Median mit Gewichten. Der Median und nicht der Mittelwert, weil eine einzige
        ''' kraeftige Diagonale im Motiv den Mittelwert mitzieht, den Median aber nicht.</summary>
        Private Shared Function AutoGeometryWeightedMedian(values As List(Of Double), weights As List(Of Double)) As Double
            Dim order = Enumerable.Range(0, values.Count).OrderBy(Function(i) values(i)).ToList()
            Dim total As Double = 0
            For Each i In order
                total += weights(i)
            Next
            If total <= 0 Then Return values(order(order.Count \ 2))

            Dim seen As Double = 0
            For Each i In order
                seen += weights(i)
                If seen >= total / 2.0 Then Return values(i)
            Next
            Return values(order(order.Count - 1))
        End Function

        Private Shared Function AutoGeometryClamp(value As Double, minimum As Double, maximum As Double) As Double
            If value < minimum Then Return minimum
            If value > maximum Then Return maximum
            Return value
        End Function

    End Class

End Namespace
