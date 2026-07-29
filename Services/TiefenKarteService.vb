Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors
Imports SkiaSharp

Namespace Services

    ''' <summary>Die Tiefenkarte eines Bildes: fuer jeden Bildpunkt, wie weit er entfernt ist.
    '''
    ''' RELATIVE Tiefe, nicht in Metern. Das Modell weiss nicht, wie gross die Szene ist - es weiss
    ''' nur, was naeher und was weiter ist. Fuer alles, wofuer sie hier gebraucht wird, reicht das
    ''' genau: eine Maske nach Entfernung, und spaeter eine Unschaerfe, die mit der Entfernung
    ''' zunimmt. Wer Meter braucht, braucht ein anderes Modell.
    '''
    ''' Ausgeliefert wird sie als Graustufenbild in Bildgroesse: HELL ist nah, DUNKEL ist fern. Das
    ''' ist die Richtung, die das Modell liefert (es gibt inverse Tiefe aus), und sie wird bewusst
    ''' nicht gedreht - "hell = nah" ist auch die Anschauung, die man beim Anschauen einer solchen
    ''' Karte hat.
    '''
    ''' Die Werte werden auf 0 bis 255 GESPREIZT, jedes Bild fuer sich. Absolute Vergleichbarkeit
    ''' zwischen zwei Fotos gibt es bei relativer Tiefe ohnehin nicht; ohne Spreizung laege ein Bild
    ''' mit geringer Tiefenstaffelung in einem schmalen Band und waere als Maske unbrauchbar.</summary>
    Public NotInheritable Class TiefenKarteService

        Private Sub New()
        End Sub

        Public Const ModellDatei As String = "midas-small"

        ''' <summary>Kantenlaenge, auf die das Modell rechnet. Fest: die kleine Fassung ist auf
        ''' 256 Pixel trainiert.</summary>
        Public Const ModellKante As Integer = 256

        Public Shared ReadOnly Property Verfuegbar As Boolean
            Get
                Return KiModellService.LaufzeitVerfuegbar AndAlso
                       Not String.IsNullOrEmpty(KiModellService.BesteDatei(ModellDatei))
            End Get
        End Property

        ''' <summary>Die Tiefenkarte als Alpha8-Bild in der Groesse des Quellbildes. Nothing bei
        ''' jedem Fehlschlag - eine halbe Tiefenkarte waere schlimmer als keine.</summary>
        Public Shared Function Berechne(bild As SKBitmap) As SKBitmap
            If bild Is Nothing OrElse bild.Width <= 0 OrElse bild.Height <= 0 Then Return Nothing
            Dim sitzung = KiModellService.SitzungFuer(ModellDatei)
            If sitzung Is Nothing Then Return Nothing

            ' Das Modell rechnet auf einem festen Quadrat. Das Bild wird darauf VERZERRT, nicht
            ' eingepasst: eine Auffuellung mit Schwarz haette das Netz als sehr weit entfernte
            ' Flaeche gelesen und die Spreizung der echten Tiefen zusammengedrueckt.
            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 3, ModellKante, ModellKante})
            Using klein = New SKBitmap(ModellKante, ModellKante, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                If Not bild.ScalePixels(klein, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                ' Normierung aus dem Training des Modells - nicht geraten.
                Dim mittel = New Single() {0.485F, 0.456F, 0.406F}
                Dim streuung = New Single() {0.229F, 0.224F, 0.225F}
                Dim ziel = tensor.Buffer.Span
                Dim ebene = ModellKante * ModellKante
                For y = 0 To ModellKante - 1
                    For x = 0 To ModellKante - 1
                        Dim p = klein.GetPixel(x, y)
                        Dim i = y * ModellKante + x
                        ziel(i) = (p.Red / 255.0F - mittel(0)) / streuung(0)
                        ziel(ebene + i) = (p.Green / 255.0F - mittel(1)) / streuung(1)
                        ziel(ebene * 2 + i) = (p.Blue / 255.0F - mittel(2)) / streuung(2)
                    Next
                Next
            End Using

            Try
                Dim eingabe = New List(Of NamedOnnxValue) From {
                    NamedOnnxValue.CreateFromTensor("image", tensor)}
                Using ergebnis = sitzung.Run(eingabe)
                    Dim raus = TryCast(ergebnis.First().Value, DenseTensor(Of Single))
                    If raus Is Nothing Then Return Nothing
                    Return AlsGraustufen(raus.Buffer.ToArray(), bild.Width, bild.Height)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Tiefenkarte", ex.Message)
                Return Nothing
            End Try
        End Function

        Private Shared Function AlsGraustufen(werte As Single(), zielBreite As Integer, zielHoehe As Integer) As SKBitmap
            If werte Is Nothing OrElse werte.Length < ModellKante * ModellKante Then Return Nothing
            Dim versatz = werte.Length - ModellKante * ModellKante

            Dim min = Single.MaxValue, max = Single.MinValue
            For i = versatz To werte.Length - 1
                Dim v = werte(i)
                If Single.IsNaN(v) OrElse Single.IsInfinity(v) Then Continue For
                If v < min Then min = v
                If v > max Then max = v
            Next
            Dim spanne = max - min
            ' Ein voellig flaches Ergebnis ist keine Tiefenkarte, sondern ein Fehlschlag.
            If spanne <= 0.0001F Then Return Nothing

            Using klein = New SKBitmap(New SKImageInfo(ModellKante, ModellKante, SKColorType.Alpha8, SKAlphaType.Premul))
                Dim puffer(ModellKante * ModellKante - 1) As Byte
                For i = 0 To puffer.Length - 1
                    Dim v = (werte(versatz + i) - min) / spanne
                    puffer(i) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(v * 255.0F)))))
                Next
                Runtime.InteropServices.Marshal.Copy(puffer, 0, klein.GetPixels(), puffer.Length)

                Dim gross = New SKBitmap(New SKImageInfo(zielBreite, zielHoehe, SKColorType.Alpha8, SKAlphaType.Premul))
                If Not klein.ScalePixels(gross, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then
                    gross.Dispose()
                    Return Nothing
                End If
                Return gross
            End Using
        End Function

        ''' <summary>Aus einer Tiefenkarte eine Maske: alles, dessen Tiefe zwischen den beiden
        ''' Grenzen liegt. Beide in Prozent, 0 = am weitesten entfernt, 100 = am naechsten.
        '''
        ''' <paramref name="weichePct"/> laesst die Maske an beiden Grenzen auslaufen, statt hart
        ''' abzuschneiden. Ohne das entstuende an jeder Tiefenstufe eine sichtbare Kante quer durchs
        ''' Bild - eine Tiefenkarte ist stetig, eine harte Schwelle darin ist immer zu sehen.</summary>
        Public Shared Function MaskeAusTiefe(tiefe As SKBitmap, vonPct As Double, bisPct As Double,
                                             weichePct As Double) As SKBitmap
            If tiefe Is Nothing OrElse tiefe.Width <= 0 OrElse tiefe.Height <= 0 Then Return Nothing
            Dim von = Math.Max(0.0, Math.Min(100.0, Math.Min(vonPct, bisPct)))
            Dim bis = Math.Max(0.0, Math.Min(100.0, Math.Max(vonPct, bisPct)))
            Dim weich = Math.Max(0.0, Math.Min(50.0, weichePct))

            Dim w = tiefe.Width, h = tiefe.Height
            Dim raus = New SKBitmap(New SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul))
            Dim puffer(w * h - 1) As Byte
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim t = tiefe.GetPixel(x, y).Alpha / 255.0 * 100.0
                    Dim d As Double
                    If t < von Then
                        d = If(weich <= 0, 0.0, 1.0 - Math.Min(1.0, (von - t) / weich))
                    ElseIf t > bis Then
                        d = If(weich <= 0, 0.0, 1.0 - Math.Min(1.0, (t - bis) / weich))
                    Else
                        d = 1.0
                    End If
                    puffer(y * w + x) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(d * 255.0)))))
                Next
            Next
            Runtime.InteropServices.Marshal.Copy(puffer, 0, raus.GetPixels(), puffer.Length)
            Return raus
        End Function


        ''' <summary>Die Form des Verwischungskerns, zeilenweise als SPANNE: fuer jede Zeile des
        ''' Kerns, von welcher bis zu welcher Spalte sie reicht.
        '''
        ''' Die Spanne ist der ganze Trick. Eine Scheibe ist konvex, also hat jede ihrer Zeilen genau
        ''' einen zusammenhaengenden Abschnitt - und ueber Praefixsummen kostet so ein Abschnitt zwei
        ''' Zugriffe statt seiner Laenge. Damit kostet ein Bildpunkt 2r+1 Rechenschritte statt
        ''' (2r+1) mal (2r+1). Bei Radius 12 ist das der Unterschied zwischen 25 und 625, und genau
        ''' daran ist die erste Fassung gescheitert: als allgemeine Faltung gerechnet brauchte eine
        ''' Vorschau ueber eine Minute.
        '''
        ''' <paramref name="ecken"/> 0 heisst rund, sonst die Anzahl der Blendenlamellen.</summary>
        Private Shared Sub SpannenFuer(radius As Integer, ecken As Integer, drehung As Double,
                                       ByRef xVon As Integer(), ByRef xBis As Integer(),
                                       ByRef anzahl As Long)
            Dim n = radius * 2 + 1
            ReDim xVon(n - 1)
            ReDim xBis(n - 1)
            anzahl = 0
            Dim eckzahl = If(ecken >= 3, ecken, 0)
            Dim winkelVersatz = drehung * Math.PI / 180.0
            For zeile = 0 To n - 1
                Dim dy = zeile - radius
                Dim links = Integer.MaxValue, rechts = Integer.MinValue
                For spalte = 0 To n - 1
                    Dim dx = spalte - radius
                    Dim r = Math.Sqrt(dx * dx + dy * dy)
                    Dim drin As Boolean
                    If eckzahl = 0 Then
                        drin = r <= radius + 0.5
                    Else
                        ' Ein regelmaessiges Vieleck: der zulaessige Radius haengt vom Winkel ab.
                        ' Zwischen zwei Ecken liegt die Kante naeher an der Mitte als die Ecke.
                        Dim w = Math.Atan2(dy, dx) - winkelVersatz
                        Dim teil = 2.0 * Math.PI / eckzahl
                        Dim rest = w - teil * Math.Floor(w / teil) - teil / 2.0
                        Dim grenze = radius * Math.Cos(Math.PI / eckzahl) / Math.Max(0.2, Math.Cos(rest))
                        drin = r <= grenze
                    End If
                    If drin Then
                        If dx < links Then links = dx
                        If dx > rechts Then rechts = dx
                    End If
                Next
                If rechts < links Then
                    ' Leere Zeile: als Spanne der Laenge null vermerken.
                    xVon(zeile) = 0
                    xBis(zeile) = -1
                Else
                    xVon(zeile) = links
                    xBis(zeile) = rechts
                    anzahl += rechts - links + 1
                End If
            Next
        End Sub

        ''' <summary>Groesster Radius, mit dem direkt gerechnet wird. Darueber wird die Stufe auf
        ''' einer verkleinerten Kopie gerechnet und wieder hochgezogen - das haelt die Kosten je
        ''' Stufe unabhaengig davon, wie stark verwischt wird.</summary>
        Private Const MaxKernRadius As Integer = 12

        ''' <summary>Zeichnet <paramref name="quelle"/> mit einer Scheibe verwischt.
        '''
        ''' Die LICHTER werden vor dem Verwischen angehoben und danach zurueckgenommen. Ohne das
        ''' mittelt sich ihre Helligkeit weg, und aus einem Lichtpunkt wird ein matter Fleck statt
        ''' einer leuchtenden Scheibe. Das ist der Schritt, der "unscharf" von "Bokeh" trennt - mehr
        ''' noch als die Form des Kerns.
        '''
        ''' Am Bildrand wird der Randwert fortgesetzt. Die Alternative waere, dort weniger Punkte zu
        ''' mitteln; dann wuerde der Rand heller oder dunkler als seine Umgebung.</summary>
        Private Shared Function VerwischeMitScheibe(quelle As SKBitmap, radius As Double,
                                                    ecken As Integer, lichterPct As Double) As SKBitmap
            If quelle Is Nothing OrElse radius < 0.5 Then Return Nothing
            Dim massstab = 1.0
            Dim r = CInt(Math.Round(radius))
            If r > MaxKernRadius Then
                massstab = MaxKernRadius / radius
                r = MaxKernRadius
            End If
            If r < 1 Then Return Nothing

            Dim xVon As Integer() = Nothing, xBis As Integer() = Nothing
            Dim anzahl As Long = 0
            SpannenFuer(r, ecken, 12.0, xVon, xBis, anzahl)
            If anzahl <= 0 Then Return Nothing

            Dim aw = Math.Max(1, CInt(Math.Round(quelle.Width * massstab)))
            Dim ah = Math.Max(1, CInt(Math.Round(quelle.Height * massstab)))
            If aw <= r * 2 OrElse ah <= r * 2 Then Return Nothing

            Dim lichter = Math.Max(0.0, Math.Min(100.0, lichterPct)) / 100.0
            ' Hin- und Rueckkennlinie der Lichteranhebung, als Tabelle statt als Potenz je Bildpunkt.
            ' Gemittelt wird in einem GESPREIZTEN Raum: v hoch p mit p groesser eins zieht die
            ' hellen Werte auseinander, sodass sie den Mittelwert bestimmen statt in ihm unterzugehen.
            ' Die Wurzel danach bringt die Helligkeit wieder zurecht. Andersherum (p kleiner eins)
            ' waere es das Gegenteil und zoege alles ins Dunkle.
            Dim hoch(255) As Single
            Dim exponentHoch = 1.0 + lichter * 3.0
            For i = 0 To 255
                hoch(i) = CSng(Math.Pow(i / 255.0, exponentHoch))
            Next
            Dim exponentRunter = 1.0 / exponentHoch

            Dim ergebnis = New SKBitmap(quelle.Width, quelle.Height, quelle.ColorType, quelle.AlphaType)
            Using klein = New SKBitmap(aw, ah, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Dim abtastung = If(massstab < 1.0,
                                   New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                                   New SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None))
                If Not quelle.ScalePixels(klein, abtastung) Then
                    ergebnis.Dispose()
                    Return Nothing
                End If

                ' Praefixsummen JE ZEILE und je Kanal, auf den angehobenen Werten. Eine Zeile hat
                ' aw+1 Eintraege, damit die Spanne a bis b als P(b+1) minus P(a) herauskommt.
                Dim breite1 = aw + 1
                Dim summen As Single()() = New Single(2)() {}
                For k = 0 To 2
                    summen(k) = New Single(ah * breite1 - 1) {}
                Next
                For y = 0 To ah - 1
                    Dim zeile = y * breite1
                    Dim lauf0 As Single = 0, lauf1 As Single = 0, lauf2 As Single = 0
                    For x = 0 To aw - 1
                        Dim p = klein.GetPixel(x, y)
                        lauf0 += hoch(p.Red) : lauf1 += hoch(p.Green) : lauf2 += hoch(p.Blue)
                        summen(0)(zeile + x + 1) = lauf0
                        summen(1)(zeile + x + 1) = lauf1
                        summen(2)(zeile + x + 1) = lauf2
                    Next
                Next

                Using verwischt = New SKBitmap(aw, ah, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                    Dim teiler = CSng(1.0 / anzahl)
                    For y = 0 To ah - 1
                        For x = 0 To aw - 1
                            Dim s0 As Single = 0, s1 As Single = 0, s2 As Single = 0
                            For zeile = 0 To r * 2
                                If xBis(zeile) < xVon(zeile) Then Continue For
                                Dim qy = y + zeile - r
                                If qy < 0 Then qy = 0
                                If qy > ah - 1 Then qy = ah - 1
                                Dim basis = qy * breite1
                                Dim a = x + xVon(zeile)
                                Dim b = x + xBis(zeile)
                                ' Ausserhalb der Zeile wird der Randwert fortgesetzt: der fehlende
                                ' Teil der Spanne zaehlt so oft, wie er ueber den Rand hinausragt.
                                Dim linksExtra = 0, rechtsExtra = 0
                                If a < 0 Then
                                    linksExtra = -a
                                    a = 0
                                End If
                                If b > aw - 1 Then
                                    rechtsExtra = b - (aw - 1)
                                    b = aw - 1
                                End If
                                s0 += summen(0)(basis + b + 1) - summen(0)(basis + a)
                                s1 += summen(1)(basis + b + 1) - summen(1)(basis + a)
                                s2 += summen(2)(basis + b + 1) - summen(2)(basis + a)
                                If linksExtra > 0 Then
                                    s0 += (summen(0)(basis + 1) - summen(0)(basis)) * linksExtra
                                    s1 += (summen(1)(basis + 1) - summen(1)(basis)) * linksExtra
                                    s2 += (summen(2)(basis + 1) - summen(2)(basis)) * linksExtra
                                End If
                                If rechtsExtra > 0 Then
                                    s0 += (summen(0)(basis + aw) - summen(0)(basis + aw - 1)) * rechtsExtra
                                    s1 += (summen(1)(basis + aw) - summen(1)(basis + aw - 1)) * rechtsExtra
                                    s2 += (summen(2)(basis + aw) - summen(2)(basis + aw - 1)) * rechtsExtra
                                End If
                            Next
                            verwischt.SetPixel(x, y, New SKColor(
                                ZurueckAusLicht(s0 * teiler, exponentRunter),
                                ZurueckAusLicht(s1 * teiler, exponentRunter),
                                ZurueckAusLicht(s2 * teiler, exponentRunter), 255))
                        Next
                    Next

                    Dim zurueck = If(massstab < 1.0,
                                     New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                                     New SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None))
                    If Not verwischt.ScalePixels(ergebnis, zurueck) Then
                        ergebnis.Dispose()
                        Return Nothing
                    End If
                End Using
            End Using
            Return ergebnis
        End Function

        ''' <summary>Die Rueckkennlinie der Lichteranhebung, zurueck auf 0 bis 255.</summary>
        Private Shared Function ZurueckAusLicht(v As Single, exponent As Double) As Byte
            If Single.IsNaN(v) OrElse v <= 0 Then Return 0
            Dim z = Math.Pow(Math.Min(1.0, v), exponent) * 255.0
            Return CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(z)))))
        End Function

        ''' <summary>Eine Kennlinie als Tabelle: Wert hoch <paramref name="exponent"/>. Kleiner 1
        ''' hebt an, groesser 1 nimmt zurueck.</summary>
        Private Shared Function TonKurveTabelle(exponent As Single) As Byte()
            Dim t(255) As Byte
            For i = 0 To 255
                Dim v = Math.Pow(i / 255.0, exponent)
                t(i) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(v * 255.0)))))
            Next
            Return t
        End Function

        ''' <summary>Tiefenabhaengige Unschaerfe: was weiter weg ist, wird staerker verwischt.
        '''
        ''' Der Unterschied zu einem gewoehnlichen Weichzeichner ist nicht die Staerke, sondern dass
        ''' sie je Bildpunkt aus seiner ENTFERNUNG kommt. Ein gleichmaessig unscharfer Hintergrund
        ''' sieht nach Weichzeichner aus; eine Unschaerfe, die mit der Entfernung zunimmt, sieht nach
        ''' offener Blende aus.
        '''
        ''' Gerechnet wird in STUFEN, nicht je Pixel: das Bild wird mehrfach unterschiedlich stark
        ''' verwischt, und dazwischen wird nach der Tiefenkarte ueberblendet. Ein echter Radius je
        ''' Pixel waere bei 45 MP nicht bezahlbar, und der sichtbare Unterschied ist gering, solange
        ''' die Stufen dicht genug liegen und weich ineinander laufen.
        '''
        ''' <paramref name="tiefe"/> ist die Karte in Bildgroesse: HELL ist nah, DUNKEL ist fern.
        ''' <paramref name="fokusPct"/> ist die Entfernung, die scharf bleibt (0 bis 100, 100 = nah).
        ''' <paramref name="staerkePct"/> ist der groesste Radius am fernsten Punkt, in Prozent der
        ''' kurzen Bildkante.
        ''' <paramref name="uebergangPct"/> bestimmt, wie schnell die Unschaerfe um die Fokusebene
        ''' herum zunimmt - klein ergibt eine schmale Schaerfeebene, gross eine breite.</summary>
        ''' <param name="eckenZahl">Form der Blende: 0 rund, sonst die Zahl der Lamellen.</param>
        ''' <param name="lichterPct">Wie stark Lichtpunkte als leuchtende Scheiben erhalten bleiben.</param>
        ''' <param name="vonPct">Untere Grenze des scharfen Bandes (0 ist am weitesten weg).</param>
        ''' <param name="bisPct">Obere Grenze des scharfen Bandes (100 ist am naechsten).</param>
        Public Shared Function TiefenUnschaerfe(quelle As SKBitmap, tiefe As SKBitmap,
                                                vonPct As Double, bisPct As Double,
                                                staerkePct As Double,
                                                uebergangPct As Double,
                                                Optional eckenZahl As Integer = 0,
                                                Optional lichterPct As Double = 60.0) As SKBitmap
            If quelle Is Nothing OrElse tiefe Is Nothing Then Return Nothing
            If quelle.Width <= 0 OrElse quelle.Height <= 0 Then Return Nothing
            Dim staerke = Math.Max(0.0, Math.Min(100.0, staerkePct))
            If staerke <= 0.01 Then Return Nothing

            ' Ein BAND statt einer Ebene. Eine echte Schaerfentiefe reicht nicht symmetrisch um
            ' einen Punkt: nach hinten ist sie deutlich groesser als nach vorn. Mit zwei Grenzen
            ' laesst sich das einstellen, mit Mitte plus Breite nicht.
            Dim von = Math.Max(0.0, Math.Min(100.0, Math.Min(vonPct, bisPct)))
            Dim bis = Math.Max(0.0, Math.Min(100.0, Math.Max(vonPct, bisPct)))
            Dim uebergang = Math.Max(1.0, Math.Min(100.0, uebergangPct))
            ' Der groesste Radius bezieht sich auf die KURZE Kante: derselbe Prozentwert soll bei
            ' Hoch- und Querformat gleich aussehen.
            Dim maxRadius = Math.Min(quelle.Width, quelle.Height) * staerke / 100.0 * 0.08
            If maxRadius < 0.5 Then Return Nothing

            ' Die Tiefenkarte auf Bildgroesse bringen, falls sie kleiner ist.
            Dim karte = tiefe
            Dim eigeneKarte As SKBitmap = Nothing
            Try
                If tiefe.Width <> quelle.Width OrElse tiefe.Height <> quelle.Height Then
                    eigeneKarte = New SKBitmap(New SKImageInfo(quelle.Width, quelle.Height,
                                                               SKColorType.Alpha8, SKAlphaType.Premul))
                    If Not tiefe.ScalePixels(eigeneKarte, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                    karte = eigeneKarte
                End If

                ' Mit Scheibenkernen fallen Stufenuebergaenge eher auf als bei weichen Glocken -
                ' eine Scheibe hat einen klaren Rand, und zwei Scheiben verschiedener Groesse
                ' nebeneinander sieht man. Deshalb mehr Stufen als bei der ersten Fassung.
                Const Stufen As Integer = 8
                Dim ergebnis = New SKBitmap(quelle.Width, quelle.Height, quelle.ColorType, quelle.AlphaType)
                Using canvas = New SKCanvas(ergebnis)
                    canvas.Clear(SKColors.Transparent)
                    ' Stufe 0 ist das scharfe Bild, darueber jede weitere Stufe staerker verwischt.
                    canvas.DrawBitmap(quelle, 0, 0)
                    For stufe = 1 To Stufen
                        Dim anteil = stufe / CDbl(Stufen)
                        ' Die Maske dieser Stufe: wie weit ist dieser Punkt von der Fokusebene weg,
                        ' gemessen in Anteilen des Uebergangs, geklemmt auf diese Stufe.
                        Using maske = StufenMaske(karte, von, bis, uebergang, anteil, 1.0 / Stufen)
                            If maske Is Nothing Then Continue For
                            Using verwischt = VerwischeMitScheibe(quelle, maxRadius * anteil,
                                                                  eckenZahl, lichterPct)
                                If verwischt Is Nothing Then Continue For
                                ' Die verwischte Fassung mit der Stufenmaske daruebermischen.
                                Using p3 = New SKPaint()
                                    Using shader = SKShader.CreateBitmap(verwischt, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp)
                                        p3.Shader = shader
                                        canvas.DrawBitmap(maske, 0, 0, p3)
                                    End Using
                                End Using
                            End Using
                        End Using
                    Next
                End Using
                Return ergebnis
            Finally
                eigeneKarte?.Dispose()
            End Try
        End Function

        ''' <summary>Wie stark diese Unschaerfestufe an jedem Punkt beitraegt. Die Stufen ueberlappen
        ''' sich bewusst und laufen weich aus - ohne das saehe man ihre Grenzen als Ringe.</summary>
        Private Shared Function StufenMaske(tiefe As SKBitmap, vonPct As Double, bisPct As Double,
                                            uebergangPct As Double,
                                            anteil As Double, breite As Double) As SKBitmap
            Dim w = tiefe.Width, h = tiefe.Height
            Dim raus = New SKBitmap(New SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul))
            Dim puffer(w * h - 1) As Byte
            Dim leer = True
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim t = tiefe.GetPixel(x, y).Alpha / 255.0 * 100.0
                    ' Abstand aus dem scharfen BAND heraus, auf 0..1 gebracht. Innerhalb ist er null
                    ' - dort bleibt es scharf, egal wie breit das Band ist.
                    Dim ausserhalb = 0.0
                    If t < vonPct Then
                        ausserhalb = vonPct - t
                    ElseIf t > bisPct Then
                        ausserhalb = t - bisPct
                    End If
                    Dim d = Math.Min(1.0, ausserhalb / uebergangPct)
                    ' Dreieck um diese Stufe herum: voll bei "anteil", null eine Stufenbreite daneben.
                    Dim g = 1.0 - Math.Min(1.0, Math.Abs(d - anteil) / breite)
                    If g > 0.0 Then leer = False
                    puffer(y * w + x) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(g * 255.0)))))
                Next
            Next
            If leer Then
                raus.Dispose()
                Return Nothing
            End If
            Runtime.InteropServices.Marshal.Copy(puffer, 0, raus.GetPixels(), puffer.Length)
            Return raus
        End Function

    End Class

End Namespace
