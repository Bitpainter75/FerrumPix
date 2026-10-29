Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Text.Json

Namespace Services

    ''' <summary>Die gemessenen Kennlinien von Lichter, Tiefen, Weiss, Schwarz und Kontrast.
    '''
    ''' WO DIE ZAHLEN STEHEN: in Resources/ToneSliderCurves.json, als eingebettete Ressource. Gebaut
    ''' von packaging/Diagnostics/Reglereichung/kennlinien.py aus Referenzentwicklungen mit je EINEM
    ''' gesetzten Regler; je Regler die Stellungen -100, -50, +50 und +100, jede als Hub auf dem
    ''' Gammawert an 65 Stuetzstellen. Dazwischen wird linear interpoliert, im Wert wie im Ton.
    '''
    ''' Gemessene Kurven statt einer Formel, weil die Formel sich als falsch herausgestellt hat und
    ''' nicht nur ihre Staerke: Lichter wirkten am staerksten in den hellen Mitten statt in den
    ''' Lichtern, Weiss +100 fast gar nicht, Kontrast negativ viermal zu stark.</summary>
    Public NotInheritable Class ToneSliderCurves

        Private Sub New()
        End Sub

        Private Const ResourceFileName As String = "ToneSliderCurves.json"

        ''' <summary>Lichter und Tiefen je Helligkeit des ganzen Bildes (Schluessel Highlights und
        ''' Shadows): Bildmedian je Gruppe in L, und je Stellung (-100, -50, +50, +100) eine Kurve je
        ''' Gruppe. Gemessen wirken beide Regler stark bildabhaengig: dunkle Stellen in einem hellen
        ''' Bild hebt Tiefen +100 viermal so weit wie in einem dunklen (Diagnostics/Reglereichung/
        ''' lokal.py). Steht VOR Curves: statische Felder entstehen in der Reihenfolge der Datei, und
        ''' LoadResource fuellt dieses hier.
        '''
        ''' Die Gruppenmitten kommen aus "mediansModel3", wo es sie gibt, sonst aus "medians". Erstere
        ''' stammen aus unseren Nullbildern mit der Grundbelichtung je Kamera und liegen rund 6 L
        ''' tiefer. Der Zusatz "Model3" (ebenso bei den Verstaerkungen) ist ein Name aus der Zeit
        ''' mehrerer Tonmodelle, so schreibt ihn kennlinien.py; er bezeichnet die gueltige Eichung.</summary>
        Private Shared ReadOnly MedianCurves As New Dictionary(Of String, (Medians As Double(), Sets As Double()()()))(StringComparer.Ordinal)

        ''' <summary>Der Zusatz, unter dem die Datei die gueltige Eichung eines Eintrags fuehrt, der
        ''' daneben auch ohne Zusatz steht. Siehe <see cref="MedianCurves"/>.</summary>
        Private Const CalibratedSuffix As String = "Model3"

        ''' <summary>Weiss je Spitze des Bildes (Schluessel Whites): das 99. Perzentil von L je Gruppe,
        ''' und je Stellung eine Kurve je Gruppe. Gemessen haengt Weiss nicht am Median, sondern daran,
        ''' wie nah die Lichter schon an Weiss stehen: Weiss +100 hebt ein flaues Bild weit und eines,
        ''' das schon oben anstoesst, kaum. In der Kreuzprobe ueber 57 Bilder sank der Fehler bei
        ''' Weiss +100 von 8,1 auf 3,3 L. Steht wie MedianCurves VOR Curves.</summary>
        Private Shared ReadOnly TopCurves As New Dictionary(Of String, (Medians As Double(), Sets As Double()()()))(StringComparer.Ordinal)

        ''' <summary>Tiefen je Anteil heller Bildpunkte (Schluessel Shadows, Eintrag
        ''' "ShadowsByBrightShare"): Prozent der Bildpunkte ueber L 70 je Gruppe ("centres"), und je
        ''' Stellung eine Kurve je Gruppe. Gemessen hebt Tiefen +100 die dunklen Stellen eines Bildes
        ''' mit viel heller Flaeche weiter als nach dem Median erwartet; mit Kameratabelle an 57
        ''' Eichbildern und 25 Bildern der Gegenprobe besser als die Gruppen nach dem Median (Tiefen
        ''' +100 4,56 auf 4,04 in der Gegenprobe). Steht wie MedianCurves VOR Curves.</summary>
        Private Shared ReadOnly BrightShareCurves As New Dictionary(Of String, (Medians As Double(), Sets As Double()()()))(StringComparer.Ordinal)

        ''' <summary>Tiefen nach dem ganzen Histogramm (Eintrag "ShadowsByHistogram"): ein lineares
        ''' Modell, je Knoten der Basis (Dreiecke ueber den Gammawert) eine Gewichtung von 19
        ''' Merkmalen, erst mit Mittel und Streuung normiert. Die Merkmale sind 11 des ganzen Bildes
        ''' (Median, Perzentile 0,1 bis 99,9, Anteile unter L 20, ueber L 70 und ueber L 90) und 7 je
        ''' Stufe der Basis (Abstand unter der Spitze und ueber dem Fuss, Summenanteil, Masse in vier
        ''' Baendern um die Stufe), dazu die Eins. Die FORM des Histogramms traegt ueber jeden
        ''' einzelnen Schluessel hinaus. In der Kreuzprobe mit
        ''' Merkmalen aus unseren Nullbildern gegen die Gruppen nach hellem Anteil: Fehler in L bei
        ''' -100/+100 von 2,01/2,87 auf 1,77/2,60 (Diagnostics/Reglereichung/histogrammregel.py).
        ''' Steht wie MedianCurves VOR Curves.</summary>
        Private Shared HistogramRule As HistogramRuleEntry

        ''' <summary>Lichter nach den naechsten Eichbildern (Eintrag "HighlightsByNeighbours"): je
        ''' Eichbild die Summenkurve seines Histogramms (100 Faecher von L), der Anteil seiner
        ''' Bildpunkte je Stufe der Basis und je Stellung die Kurve. Ein Bild bekommt das nach diesem
        ''' Anteil und nach 1 / (1 + Abstand) gewichtete Mittel der Kurven der Eichbilder mit dem
        ''' naechsten Histogramm; Abstand ist die Flaeche zwischen den Summenkurven. Ein lineares
        ''' Modell wie bei den Tiefen gewann hier nur 1 %, die Nachbarn in der Kreuzprobe 5 bis 8 %
        ''' (Lichter -100/+100 von 1,88/1,77 auf 1,78/1,63). Steht wie MedianCurves VOR Curves.</summary>
        Private Shared NeighbourSet As NeighbourSetEntry

        ''' <summary>Die Schluessel der vier Stellungen in der Datei, Index wie bei Curves. Steht VOR
        ''' Curves, weil LoadResource es braucht.</summary>
        Private Shared ReadOnly StanceKeys As String() = {"-100", "-50", "50", "100"}

        Private NotInheritable Class HistogramRuleEntry
            Public Knots As Integer
            Public Mu As Double()
            Public Sd As Double()
            ''' <summary>Je Stellung (Index wie bei Curves) Knoten mal Merkmale, zeilenweise.</summary>
            Public Coefficients As Double()()
            Public Gains As Double()
        End Class

        Private NotInheritable Class NeighbourSetEntry
            Public Count As Integer
            Public Cdfs As Double()()
            Public Weights As Double()()
            ''' <summary>Je Stellung (Index wie bei Curves) je Eichbild die Kurve auf den Stufen.</summary>
            Public Curves As Double()()()
            Public Gains As Double()
        End Class

        ''' <summary>Die Grenze, ueber der ein Bildpunkt als hell zaehlt, in L. Muss mit
        ''' Diagnostics/Reglereichung/kennlinien.py (SHARE_LIMITS) uebereinstimmen.</summary>
        Public Const BrightShareLimitL As Double = 70.0

        ''' <summary>Verstaerkung je Regler und Stellung (Index wie bei Curves), Schluessel "gains" der
        ''' Datei. Die Kurven sind Mediane ueber die Bilder; die mittlere Wirkung der Referenz ist bei
        ''' manchen Reglern kraeftiger, gemessen als Faktor unserer Wirkung durch ihre. Der Kehrwert
        ''' steht hier. Fehlt ein Eintrag, gilt 1. Ein Eintrag mit <see cref="CalibratedSuffix"/>
        ''' ersetzt den ohne. Steht wie MedianCurves VOR Curves.</summary>
        Private Shared ReadOnly Gains As New Dictionary(Of String, Double())(StringComparer.Ordinal)

        ''' <summary>Verstaerkung der Feinstruktur je Zone, Schluessel "localDetail" der
        ''' Datei. Je Regler und Stellung (Index wie bei Curves) ein Faktor je Zonenmitte
        ''' (DetailCenters, in L). Gemessen lassen Lichter und Tiefen die Struktur nicht einfach
        ''' stehen: in ihrer eigenen Zone verstaerken sie sie beim Zurueckholen (Lichter -100 in den
        ''' Lichtern 1,19, Tiefen +100 in den tiefen Tiefen 1,47) und daempfen sie beim Verstaerken.
        ''' Mit demselben Faktor waechst der Abstand der Kanaele vom Grau, also die Buntheit.
        ''' Steht wie MedianCurves VOR Curves.</summary>
        Private Shared ReadOnly DetailGains As New Dictionary(Of String, Double()())(StringComparer.Ordinal)
        Private Shared DetailCenters As Double()

        ''' <summary>Verstaerker der Feinstruktur der HELLIGKEIT je Regler, Stellung (Index wie bei
        ''' Curves) und Zone: "lumaBoost" aus "localDetail", Vorgabe 1. Der Ueberschuss des
        ''' gemessenen Faktors ueber 1 wird damit multipliziert. Fuer Lichter nach unten steht dort
        ''' in den hellen Zonen das Vier- bis Fuenffache, im Ergebnis geeicht: Knie und Tonzonen
        ''' davor flachen die Struktur sonst wieder ab, und vom gemessenen 1,19 kam nichts an.</summary>
        Private Shared ReadOnly LumaBoosts As New Dictionary(Of String, Double()())(StringComparer.Ordinal)

        ''' <summary>Um wie viel ein Verstaerker ungleich 1 je Blendenstufe POSITIVER Belichtung
        ''' faellt ("lumaBoostEvSlope", je Regler und Stellung, Vorgabe 0), nicht unter 0. Gemessen
        ''' trifft bei Lichter -100 ohne Belichtung das Fuenffache, mit +1 rund das Doppelte und mit
        ''' +2 keiner mehr: die Belichtung zieht die Lichter in die Schulter, und dort nimmt auch die
        ''' Referenz Struktur weg. Bei negativer Belichtung bleibt der Wert fuer 0.</summary>
        Private Shared ReadOnly LumaBoostEvSlopes As New Dictionary(Of String, Double())(StringComparer.Ordinal)

        ''' <summary>Kurven je Regler: Index 0 bis 3 fuer -100, -50, +50, +100. Nothing, wenn die
        ''' Ressource fehlt; dann bewegen die Tonregler nichts.</summary>
        Private Shared ReadOnly Curves As Dictionary(Of String, Double()()) = LoadResource()
        Private Shared _grid As Integer

        Public Shared ReadOnly Property Available As Boolean
            Get
                Return Curves IsNot Nothing
            End Get
        End Property

        Private Shared Function LoadResource() As Dictionary(Of String, Double()())
            Try
                Dim assembly = GetType(ToneSliderCurves).GetTypeInfo().Assembly
                Dim resourceName = assembly.GetManifestResourceNames().
                    FirstOrDefault(Function(name) name.EndsWith(ResourceFileName, StringComparison.OrdinalIgnoreCase))
                If resourceName Is Nothing Then Return Nothing
                Using stream = assembly.GetManifestResourceStream(resourceName)
                    If stream Is Nothing Then Return Nothing
                    Using document = JsonDocument.Parse(stream)
                        Dim root = document.RootElement
                        Dim grid = root.GetProperty("grid").GetInt32()
                        Dim result As New Dictionary(Of String, Double()())(StringComparer.Ordinal)
                        Dim gainsElement As JsonElement
                        If root.TryGetProperty("gains", gainsElement) Then
                            For Each slider In gainsElement.EnumerateObject()
                                Dim g = New Double() {1.0, 1.0, 1.0, 1.0}
                                Dim gkeys = {"-100", "-50", "50", "100"}
                                For k = 0 To 3
                                    Dim value As JsonElement
                                    If slider.Value.TryGetProperty(gkeys(k), value) Then g(k) = value.GetDouble()
                                Next
                                If slider.Name.EndsWith(CalibratedSuffix, StringComparison.Ordinal) Then
                                    Gains(slider.Name.Substring(0, slider.Name.Length - CalibratedSuffix.Length)) = g
                                ElseIf Not Gains.ContainsKey(slider.Name) Then
                                    Gains(slider.Name) = g
                                End If
                            Next
                        End If
                        Dim detailElement As JsonElement
                        If root.TryGetProperty("localDetail", detailElement) Then
                            Dim centers = detailElement.GetProperty("centers").EnumerateArray().Select(Function(e) e.GetDouble()).ToArray()
                            Dim dkeys = {"-100", "-50", "50", "100"}
                            For Each slider In detailElement.EnumerateObject()
                                If slider.Value.ValueKind <> JsonValueKind.Object OrElse
                                   slider.Name = "lumaBoost" OrElse slider.Name = "lumaBoostEvSlope" Then Continue For
                                Dim sets = New Double(3)() {}
                                Dim complete = centers.Length >= 2
                                For k = 0 To 3
                                    Dim value As JsonElement
                                    If Not slider.Value.TryGetProperty(dkeys(k), value) Then complete = False : Exit For
                                    sets(k) = value.EnumerateArray().Select(Function(e) e.GetDouble()).ToArray()
                                    complete = complete AndAlso sets(k).Length = centers.Length
                                Next
                                ' Unvollstaendig: dann rechnet der Regler ohne Strukturglied.
                                If complete Then DetailGains(slider.Name) = sets
                            Next
                            Dim boostElement As JsonElement
                            Dim hasBoost = detailElement.TryGetProperty("lumaBoost", boostElement)
                            Dim slopeElement As JsonElement
                            Dim hasSlope = detailElement.TryGetProperty("lumaBoostEvSlope", slopeElement)
                            For Each entry In DetailGains
                                Dim boosts = New Double(3)() {}
                                Dim slopes = New Double(3) {}
                                Dim sliderBoost As JsonElement
                                Dim sliderSlope As JsonElement
                                Dim withBoost = hasBoost AndAlso boostElement.TryGetProperty(entry.Key, sliderBoost)
                                Dim withSlope = hasSlope AndAlso slopeElement.TryGetProperty(entry.Key, sliderSlope)
                                For k = 0 To 3
                                    boosts(k) = Enumerable.Repeat(1.0, centers.Length).ToArray()
                                    Dim factors As JsonElement
                                    If withBoost AndAlso sliderBoost.TryGetProperty(dkeys(k), factors) AndAlso
                                       factors.GetArrayLength() = centers.Length Then
                                        For z = 0 To centers.Length - 1
                                            boosts(k)(z) = factors(z).GetDouble()
                                        Next
                                    End If
                                    Dim slope As JsonElement
                                    If withSlope AndAlso sliderSlope.TryGetProperty(dkeys(k), slope) Then slopes(k) = slope.GetDouble()
                                Next
                                LumaBoosts(entry.Key) = boosts
                                LumaBoostEvSlopes(entry.Key) = slopes
                            Next
                            DetailCenters = centers
                        End If
                        For Each slider In root.GetProperty("curves").EnumerateObject()
                            ' Die Regeln nach dem ganzen Histogramm haben einen eigenen Aufbau; ein
                            ' unvollstaendiger Eintrag laesst den Regler bei seinen Gruppen.
                            If slider.Name = "ShadowsByHistogram" OrElse slider.Name = "HighlightsByNeighbours" Then
                                Try
                                    If slider.Name = "ShadowsByHistogram" Then
                                        HistogramRule = ReadHistogramRule(slider.Value)
                                    Else
                                        NeighbourSet = ReadNeighbourSet(slider.Value)
                                    End If
                                Catch ex As Exception When TypeOf ex Is KeyNotFoundException OrElse
                                                           TypeOf ex Is InvalidOperationException OrElse
                                                           TypeOf ex Is FormatException
                                    ' Kaputter Eintrag: der Regler bleibt bei seinen Gruppen, die
                                    ' uebrigen Kennlinien gelten weiter.
                                End Try
                                Continue For
                            End If
                            Dim byMedian = slider.Name.EndsWith("ByMedian", StringComparison.Ordinal)
                            Dim byTop = slider.Name.EndsWith("ByTop", StringComparison.Ordinal)
                            Dim byBrightShare = slider.Name.EndsWith("ByBrightShare", StringComparison.Ordinal)
                            ' Andere Eintraege nach Anteil (etwa Lichter nach dunklem Anteil) sind
                            ' gemessen, aber nicht in Gebrauch.
                            If slider.Name.EndsWith("Share", StringComparison.Ordinal) AndAlso Not byBrightShare Then Continue For
                            If byMedian OrElse byTop OrElse byBrightShare Then
                                Dim centresName = If(byTop, "tops", If(byBrightShare, "centres", "medians"))
                                Dim medians = slider.Value.GetProperty(centresName).EnumerateArray().Select(Function(e) e.GetDouble()).ToArray()
                                Dim sets = New Double(3)()() {}
                                Dim mkeys = {"-100", "-50", "50", "100"}
                                Dim complete = medians.Length >= 2
                                For k = 0 To 3
                                    sets(k) = slider.Value.GetProperty(mkeys(k)).EnumerateArray().
                                        Select(Function(g) g.EnumerateArray().Select(Function(e) e.GetDouble()).ToArray()).ToArray()
                                    complete = complete AndAlso sets(k).Length = medians.Length AndAlso
                                               sets(k).All(Function(c) c.Length = grid)
                                Next
                                ' Unvollstaendig: dann bleibt es bei der mittleren Kurve des Reglers.
                                If complete Then
                                    Dim suffix = If(byTop, "ByTop", If(byBrightShare, "ByBrightShare", "ByMedian"))
                                    Dim target = If(byTop, TopCurves, If(byBrightShare, BrightShareCurves, MedianCurves))
                                    Dim key = slider.Name.Substring(0, slider.Name.Length - suffix.Length)
                                    Dim calibratedElement As JsonElement
                                    If byMedian AndAlso slider.Value.TryGetProperty("medians" & CalibratedSuffix, calibratedElement) Then
                                        Dim calibrated = calibratedElement.EnumerateArray().Select(Function(e) e.GetDouble()).ToArray()
                                        If calibrated.Length = medians.Length Then medians = calibrated
                                    End If
                                    target(key) = (medians, sets)
                                End If
                                Continue For
                            End If
                            Dim set4 = New Double(3)() {}
                            Dim keys = {"-100", "-50", "50", "100"}
                            For k = 0 To 3
                                Dim values = slider.Value.GetProperty(keys(k)).EnumerateArray().
                                    Select(Function(e) e.GetDouble()).ToArray()
                                If values.Length <> grid Then Return Nothing
                                set4(k) = values
                            Next
                            result(slider.Name) = set4
                        Next
                        _grid = grid
                        Return result
                    End Using
                End Using
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse
                                        TypeOf ex Is KeyNotFoundException OrElse TypeOf ex Is InvalidOperationException
                Return Nothing
            End Try
        End Function

        Private Shared Function ReadNumbers(element As JsonElement) As Double()
            Return element.EnumerateArray().Select(Function(e) e.GetDouble()).ToArray()
        End Function

        Private Shared Function ReadStanceGains(element As JsonElement) As Double()
            Dim g = New Double() {1.0, 1.0, 1.0, 1.0}
            Dim gainsElement As JsonElement
            If Not element.TryGetProperty("gains", gainsElement) Then Return g
            For k = 0 To 3
                Dim value As JsonElement
                If gainsElement.TryGetProperty(StanceKeys(k), value) Then g(k) = value.GetDouble()
            Next
            Return g
        End Function

        Private Shared Function ReadHistogramRule(element As JsonElement) As HistogramRuleEntry
            Dim rule As New HistogramRuleEntry With {
                .Knots = element.GetProperty("knots").GetInt32(),
                .Mu = ReadNumbers(element.GetProperty("mu")),
                .Sd = ReadNumbers(element.GetProperty("sd")),
                .Coefficients = New Double(3)() {},
                .Gains = ReadStanceGains(element)}
            If rule.Knots < 2 OrElse rule.Mu.Length <> HistogramFeatureCount OrElse rule.Sd.Length <> HistogramFeatureCount Then Return Nothing
            For k = 0 To 3
                rule.Coefficients(k) = ReadNumbers(element.GetProperty(StanceKeys(k)))
                If rule.Coefficients(k).Length <> rule.Knots * HistogramFeatureCount Then Return Nothing
            Next
            Return rule
        End Function

        Private Shared Function ReadNeighbourSet(element As JsonElement) As NeighbourSetEntry
            Dim levels = element.GetProperty("levels").GetInt32()
            Dim entry As New NeighbourSetEntry With {
                .Count = element.GetProperty("neighbours").GetInt32(),
                .Cdfs = element.GetProperty("cdf").EnumerateArray().Select(Function(e) ReadNumbers(e)).ToArray(),
                .Weights = element.GetProperty("weight").EnumerateArray().Select(Function(e) ReadNumbers(e)).ToArray(),
                .Curves = New Double(3)()() {},
                .Gains = ReadStanceGains(element)}
            Dim images = entry.Cdfs.Length
            If entry.Count < 1 OrElse images < entry.Count OrElse entry.Weights.Length <> images OrElse
               entry.Cdfs.Any(Function(c) c.Length <> HistogramBins) OrElse entry.Weights.Any(Function(w) w.Length <> levels) Then Return Nothing
            For k = 0 To 3
                entry.Curves(k) = element.GetProperty(StanceKeys(k)).EnumerateArray().Select(Function(e) ReadNumbers(e)).ToArray()
                If entry.Curves(k).Length <> images OrElse entry.Curves(k).Any(Function(c) c.Length <> levels) Then Return Nothing
            Next
            Return entry
        End Function

        ''' <summary>Faecher des Histogramms von L fuer die Merkmale (je ein L breit), wie
        ''' histogrammregel.py (bildmerkmale).</summary>
        Private Const HistogramBins As Integer = 100
        Private Const HistogramFeatureCount As Integer = 19

        ''' <summary>Die vier Kurven (Stellungen wie bei Curves, schon mit ihrer Verstaerkung) eines
        ''' Bildes fuer Lichter oder Tiefen nach seinem ganzen Histogramm. <paramref name="shape"/> ist
        ''' das Histogramm von L in Zehntelstufen (1001 Faecher, Anteile, Summe 1), so wie es bei den
        ''' Tonzonen ankommt. Nothing, wenn fuer den Regler keine solche Regel vorliegt; dann gelten
        ''' die Gruppen.</summary>
        Public Shared Function ShapeCurves(slider As String, shape As Double()) As Double()()
            If Curves Is Nothing OrElse shape Is Nothing OrElse shape.Length < 1001 Then Return Nothing
            Dim coarse = New Double(HistogramBins - 1) {}
            For k = 0 To 1000
                coarse(Math.Min(HistogramBins - 1, k \ 10)) += shape(k)
            Next
            If slider = "Shadows" AndAlso HistogramRule IsNot Nothing Then Return RuleCurves(HistogramRule, shape, coarse)
            If slider = "Highlights" AndAlso NeighbourSet IsNot Nothing Then Return NeighbourCurves(NeighbourSet, coarse)
            Return Nothing
        End Function

        ''' <summary>Hub aus den Kurven eines Bildes (<see cref="ShapeCurves"/>); im Wert wie
        ''' <see cref="Lift"/> zwischen 0, 50 und 100 linear.</summary>
        Public Shared Function LiftFromCurves(curvesOfImage As Double()(), value As Double, v As Double) As Double
            If value = 0.0 OrElse curvesOfImage Is Nothing Then Return 0.0
            Dim magnitude = Math.Min(100.0, Math.Abs(value))
            Dim atHalf = Sample(If(value < 0, curvesOfImage(1), curvesOfImage(2)), v)
            If magnitude <= 50.0 Then Return atHalf * magnitude / 50.0
            Return atHalf + (Sample(If(value < 0, curvesOfImage(0), curvesOfImage(3)), v) - atHalf) * (magnitude - 50.0) / 50.0
        End Function

        Private Shared Function RuleCurves(rule As HistogramRuleEntry, shape As Double(), coarse As Double()) As Double()()
            ' Perzentile aus den Zehntelstufen: die erste Stufe, an der der Summenanteil erreicht ist.
            Dim percentile = Function(p As Double) As Double
                                 Dim acc = 0.0
                                 For k = 0 To 1000
                                     acc += shape(k)
                                     If acc >= p Then Return k / 10.0
                                 Next
                                 Return 100.0
                             End Function
            Dim share = Function(fromBin As Integer, toBin As Integer) As Double
                            Dim s = 0.0
                            For k = Math.Max(0, fromBin) To Math.Min(1000, toBin)
                                s += shape(k)
                            Next
                            Return s * 100.0
                        End Function
            Dim q0 = percentile(0.001), q999 = percentile(0.999)
            ' Die Merkmale des ganzen Bildes, Reihenfolge wie bildvektor in histogrammregel.py.
            Dim image = New Double() {percentile(0.5), q0, percentile(0.05), percentile(0.25), percentile(0.75),
                                      percentile(0.95), percentile(0.99), q999,
                                      share(0, 199), share(701, 1000), share(901, 1000)}
            Dim cdf = New Double(HistogramBins - 1) {}
            Dim running = 0.0
            For i = 0 To HistogramBins - 1
                running += coarse(i)
                cdf(i) = running
            Next
            Dim band = Function(i As Integer, a As Integer, b As Integer) As Double
                           Dim s = 0.0
                           For j = Math.Max(0, i + a) To Math.Max(0, Math.Min(HistogramBins, i + b)) - 1
                               s += coarse(j)
                           Next
                           Return s * 100.0
                       End Function
            Dim result = New Double(3)() {}
            For k = 0 To 3
                result(k) = New Double(_grid - 1) {}
            Next
            Dim features = New Double(HistogramFeatureCount - 1) {}
            For g = 0 To _grid - 1
                Dim v = g / CDbl(_grid - 1)
                Dim lc = GammaToLightness(v)
                Dim i = CInt(Math.Max(0.0, Math.Min(HistogramBins - 1.0, Math.Floor(lc))))
                features(0) = 1.0
                Array.Copy(image, 0, features, 1, image.Length)
                Dim o = 1 + image.Length
                features(o) = q999 - lc
                features(o + 1) = lc - q0
                features(o + 2) = cdf(i) * 100.0
                features(o + 3) = band(i, -10, 10)
                features(o + 4) = band(i, -25, -10)
                features(o + 5) = band(i, 10, 25)
                features(o + 6) = band(i, 25, 100)
                For f = 0 To HistogramFeatureCount - 1
                    features(f) = (features(f) - rule.Mu(f)) / rule.Sd(f)
                Next
                ' Dreiecke ueber die Knoten, wie dreiecke() in histogrammregel.py.
                Dim x = v * (rule.Knots - 1)
                Dim j0 = CInt(Math.Max(0.0, Math.Min(rule.Knots - 2.0, Math.Floor(x))))
                Dim t = x - j0
                For k = 0 To 3
                    Dim c = rule.Coefficients(k)
                    Dim at0 = 0.0, at1 = 0.0
                    For f = 0 To HistogramFeatureCount - 1
                        at0 += c(j0 * HistogramFeatureCount + f) * features(f)
                        at1 += c((j0 + 1) * HistogramFeatureCount + f) * features(f)
                    Next
                    result(k)(g) = (at0 * (1.0 - t) + at1 * t) * rule.Gains(k)
                Next
            Next
            Return result
        End Function

        Private Shared Function NeighbourCurves(entry As NeighbourSetEntry, coarse As Double()) As Double()()
            Dim cdf = New Double(HistogramBins - 1) {}
            Dim running = 0.0
            For i = 0 To HistogramBins - 1
                running += coarse(i)
                cdf(i) = running
            Next
            Dim images = entry.Cdfs.Length
            Dim distance = New Double(images - 1) {}
            For n = 0 To images - 1
                Dim d = 0.0
                Dim other = entry.Cdfs(n)
                For i = 0 To HistogramBins - 1
                    d += Math.Abs(other(i) - cdf(i))
                Next
                distance(n) = d
            Next
            Dim nearest = Enumerable.Range(0, images).OrderBy(Function(n) distance(n)).Take(entry.Count).ToArray()
            Dim levels = entry.Weights(0).Length
            Dim result = New Double(3)() {}
            For k = 0 To 3
                ' Je Stufe der Basis das gewichtete Mittel, dann auf das Raster der Kurven.
                Dim atLevel = New Double(levels - 1) {}
                For s = 0 To levels - 1
                    Dim sum = 0.0, weight = 0.0
                    For Each n In nearest
                        Dim w = entry.Weights(n)(s) / (distance(n) + 1.0)
                        sum += entry.Curves(k)(n)(s) * w
                        weight += w
                    Next
                    atLevel(s) = sum / Math.Max(weight, 0.000000001)
                Next
                result(k) = New Double(_grid - 1) {}
                For g = 0 To _grid - 1
                    ' Stufe s hat ihre Mitte bei (s + 0,5) / levels.
                    Dim pos = Math.Max(0.0, Math.Min(levels - 1.0, g / CDbl(_grid - 1) * levels - 0.5))
                    Dim s0 = CInt(Math.Floor(pos)), s1 = Math.Min(levels - 1, s0 + 1)
                    result(k)(g) = (atLevel(s0) + (atLevel(s1) - atLevel(s0)) * (pos - s0)) * entry.Gains(k)
                Next
            Next
            Return result
        End Function

        ''' <summary>Gammawert (sRGB) nach L (CIE), wie v_to_l in den Messwerkzeugen.</summary>
        Private Shared Function GammaToLightness(v As Double) As Double
            v = Math.Max(0.0, Math.Min(1.0, v))
            Dim y = If(v <= 0.04045, v / 12.92, Math.Pow((v + 0.055) / 1.055, 2.4))
            Return If(y > 0.008856, 116.0 * Math.Pow(y, 1.0 / 3.0) - 16.0, 903.3 * y)
        End Function

        ''' <summary>Liegt fuer <paramref name="slider"/> eine gemessene Kurve vor?</summary>
        Public Shared Function Has(slider As String) As Boolean
            Return Curves IsNot Nothing AndAlso Curves.ContainsKey(slider)
        End Function

        ''' <summary>Hub des Reglers <paramref name="slider"/> (Highlights, Shadows, Whites, Blacks,
        ''' Contrast; ParametricShadows/Darks/Lights/Highlights fuer den Preset-Import) bei Wert <paramref name="value"/> (-100 bis 100) am Gammawert
        ''' <paramref name="v"/>. Zwischen 0 und 50 und zwischen 50 und 100 linear.</summary>
        Public Shared Function Lift(slider As String, value As Double, v As Double) As Double
            If value = 0.0 OrElse Curves Is Nothing Then Return 0.0
            Dim set4 As Double()() = Nothing
            If Not Curves.TryGetValue(slider, set4) Then Return 0.0
            Dim magnitude = Math.Min(100.0, Math.Abs(value))
            Dim half = If(value < 0, set4(1), set4(2))
            Dim full = If(value < 0, set4(0), set4(3))
            Dim atHalf = Sample(half, v) * Gain(slider, value, False)
            If magnitude <= 50.0 Then Return atHalf * magnitude / 50.0
            Return atHalf + (Sample(full, v) * Gain(slider, value, True) - atHalf) * (magnitude - 50.0) / 50.0
        End Function

        ''' <summary>Wie <see cref="Lift"/>, aber nach der Helligkeit des ganzen Bildes: liegen fuer den
        ''' Regler Kurven je Bildmedian vor, wird zwischen den Gruppen linear nach
        ''' <paramref name="imageMedianL"/> (L, 0 bis 100) geteilt, ausserhalb die naechste Gruppe.
        ''' Ohne solche Kurven oder ohne Median (0 oder weniger) die mittlere Kurve.</summary>
        Public Shared Function LiftByMedian(slider As String, value As Double, v As Double, imageMedianL As Double) As Double
            Dim entry As (Medians As Double(), Sets As Double()()()) = Nothing
            If imageMedianL <= 0.0 OrElse value = 0.0 OrElse Not MedianCurves.TryGetValue(slider, entry) Then
                Return Lift(slider, value, v)
            End If
            Return LiftGrouped(slider, entry, value, v, imageMedianL)
        End Function

        ''' <summary>Wie <see cref="Lift"/>, aber nach der Spitze des Bildes: liegen fuer den Regler
        ''' Kurven je Spitze vor (bisher nur Weiss), wird zwischen den Gruppen linear nach
        ''' <paramref name="imageTopL"/> (99. Perzentil von L, 0 bis 100) geteilt, ausserhalb die
        ''' naechste Gruppe. Ohne solche Kurven oder ohne Spitze (0 oder weniger) die mittlere Kurve.
        ''' Die Verstaerkung steht unter "WhitesByTop" in "gains", getrennt von der des Reglers.</summary>
        Public Shared Function LiftByTop(slider As String, value As Double, v As Double, imageTopL As Double) As Double
            Dim entry As (Medians As Double(), Sets As Double()()()) = Nothing
            If imageTopL <= 0.0 OrElse value = 0.0 OrElse Not TopCurves.TryGetValue(slider, entry) Then
                Return Lift(slider, value, v)
            End If
            Return LiftGrouped(slider, entry, value, v, imageTopL, slider & "ByTop")
        End Function

        ''' <summary>Wie <see cref="Lift"/>, aber nach dem Anteil heller Bildpunkte: liegen fuer den
        ''' Regler Kurven je Anteil vor (bisher nur Tiefen), wird zwischen den Gruppen linear nach
        ''' <paramref name="brightSharePercent"/> (Prozent ueber <see cref="BrightShareLimitL"/>)
        ''' geteilt, ausserhalb die naechste Gruppe. Ohne solche Kurven die Gruppen nach dem Median
        ''' (<paramref name="imageMedianL"/>). Die Verstaerkung ist die des Reglers, mit der auch
        ''' gemessen wurde.</summary>
        Public Shared Function LiftByBrightShare(slider As String, value As Double, v As Double,
                                                 brightSharePercent As Double, imageMedianL As Double) As Double
            Dim entry As (Medians As Double(), Sets As Double()()()) = Nothing
            If imageMedianL <= 0.0 OrElse value = 0.0 OrElse Not BrightShareCurves.TryGetValue(slider, entry) Then
                Return LiftByMedian(slider, value, v, imageMedianL)
            End If
            Return LiftGrouped(slider, entry, value, v, brightSharePercent)
        End Function

        ''' <summary>Hub aus Kurven je Gruppe: linear zwischen den beiden Gruppen, deren Mitten
        ''' <paramref name="measure"/> einschliessen, ausserhalb die naechste Gruppe; im Wert wie
        ''' <see cref="Lift"/> zwischen 0, 50 und 100 linear.</summary>
        Private Shared Function LiftGrouped(slider As String, entry As (Medians As Double(), Sets As Double()()()),
                                            value As Double, v As Double, measure As Double,
                                            Optional gainKey As String = Nothing) As Double
            Dim gainSlider = If(gainKey IsNot Nothing AndAlso Gains.ContainsKey(gainKey), gainKey, slider)
            Dim m = entry.Medians
            Dim gi = 0
            Dim f = 0.0
            If measure <= m(0) Then
                gi = 0 : f = 0.0
            ElseIf measure >= m(m.Length - 1) Then
                gi = m.Length - 2 : f = 1.0
            Else
                For i = 0 To m.Length - 2
                    If measure <= m(i + 1) Then
                        gi = i : f = (measure - m(i)) / (m(i + 1) - m(i))
                        Exit For
                    End If
                Next
            End If
            Dim magnitude = Math.Min(100.0, Math.Abs(value))
            Dim halfSet = If(value < 0, entry.Sets(1), entry.Sets(2))
            Dim fullSet = If(value < 0, entry.Sets(0), entry.Sets(3))
            Dim atHalf = (Sample(halfSet(gi), v) + (Sample(halfSet(gi + 1), v) - Sample(halfSet(gi), v)) * f) * Gain(gainSlider, value, False)
            If magnitude <= 50.0 Then Return atHalf * magnitude / 50.0
            Dim atFull = (Sample(fullSet(gi), v) + (Sample(fullSet(gi + 1), v) - Sample(fullSet(gi), v)) * f) * Gain(gainSlider, value, True)
            Return atHalf + (atFull - atHalf) * (magnitude - 50.0) / 50.0
        End Function

        ''' <summary>Faktor auf den Abstand der Kanaele vom Grau fuer Regler <paramref name="slider"/>
        ''' (Highlights, Shadows) bei Wert <paramref name="value"/>, an der geglaetteten Helligkeit
        ''' <paramref name="lightness"/> (L, 0 bis 100). Zwischen den Zonenmitten linear, ausserhalb
        ''' die naechste; im Wert wie <see cref="Lift"/> zwischen 0, 50 und 100 linear. 1 ohne
        ''' gemessene Werte.</summary>
        Public Shared Function DetailGain(slider As String, value As Double, lightness As Double) As Double
            Dim sets As Double()() = Nothing
            If value = 0.0 OrElse DetailCenters Is Nothing OrElse Not DetailGains.TryGetValue(slider, sets) Then Return 1.0
            Return GainOverZones(Function(k, z) sets(k)(z), value, lightness)
        End Function

        ''' <summary>Wie <see cref="DetailGain"/>, fuer die Feinstruktur der Helligkeit: je Zone der
        ''' Ueberschuss mal dem Verstaerker (<see cref="LumaBoosts"/>), der bei positiver Belichtung
        ''' <paramref name="exposureEv"/> (Blendenstufen) nach <see cref="LumaBoostEvSlopes"/> faellt.</summary>
        Public Shared Function LuminanceDetailGain(slider As String, value As Double, lightness As Double,
                                                   Optional exposureEv As Double = 0.0) As Double
            Dim sets As Double()() = Nothing
            Dim boosts As Double()() = Nothing
            Dim slopes As Double() = Nothing
            If value = 0.0 OrElse DetailCenters Is Nothing OrElse Not DetailGains.TryGetValue(slider, sets) Then Return 1.0
            If Not LumaBoosts.TryGetValue(slider, boosts) OrElse Not LumaBoostEvSlopes.TryGetValue(slider, slopes) Then
                Return GainOverZones(Function(k, z) sets(k)(z), value, lightness)
            End If
            Dim ev = Math.Max(0.0, exposureEv)
            Return GainOverZones(
                Function(k, z)
                    Dim boost = boosts(k)(z)
                    If boost <> 1.0 Then boost = Math.Max(0.0, boost - slopes(k) * ev)
                    Return 1.0 + (sets(k)(z) - 1.0) * boost
                End Function, value, lightness)
        End Function

        ''' <summary>Der Faktor zwischen den Zonenmitten und zwischen den Stellungen 50 und 100,
        ''' aus <paramref name="zoneValue"/> (Stellung wie bei Curves, Zone).</summary>
        Private Shared Function GainOverZones(zoneValue As Func(Of Integer, Integer, Double),
                                              value As Double, lightness As Double) As Double
            Dim c = DetailCenters
            Dim gi = 0
            Dim f = 0.0
            If lightness <= c(0) Then
                gi = 0 : f = 0.0
            ElseIf lightness >= c(c.Length - 1) Then
                gi = c.Length - 2 : f = 1.0
            Else
                For i = 0 To c.Length - 2
                    If lightness <= c(i + 1) Then
                        gi = i : f = (lightness - c(i)) / (c(i + 1) - c(i))
                        Exit For
                    End If
                Next
            End If
            Dim magnitude = Math.Min(100.0, Math.Abs(value))
            Dim half = If(value < 0, 1, 2)
            Dim full = If(value < 0, 0, 3)
            Dim atHalf = zoneValue(half, gi) + (zoneValue(half, gi + 1) - zoneValue(half, gi)) * f
            If magnitude <= 50.0 Then Return 1.0 + (atHalf - 1.0) * magnitude / 50.0
            Dim atFull = zoneValue(full, gi) + (zoneValue(full, gi + 1) - zoneValue(full, gi)) * f
            Return atHalf + (atFull - atHalf) * (magnitude - 50.0) / 50.0
        End Function

        ''' <summary>Verstaerkung der Stellung +-50 (<paramref name="full"/> False) oder +-100 in der
        ''' Richtung von <paramref name="value"/>.</summary>
        Private Shared Function Gain(slider As String, value As Double, full As Boolean) As Double
            Dim g As Double() = Nothing
            If Not Gains.TryGetValue(slider, g) Then Return 1.0
            If value < 0 Then Return If(full, g(0), g(1))
            Return If(full, g(3), g(2))
        End Function

        Private Shared Function Sample(curve As Double(), v As Double) As Double
            If v <= 0.0 Then Return curve(0)
            If v >= 1.0 Then Return curve(_grid - 1)
            Dim pos = v * (_grid - 1)
            Dim i = CInt(Math.Floor(pos))
            Dim f = pos - i
            Return curve(i) + (curve(i + 1) - curve(i)) * f
        End Function

    End Class

End Namespace
