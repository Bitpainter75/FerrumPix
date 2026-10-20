Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Reflection
Imports System.Text.Json

Namespace Services

    ''' <summary>Die gemessenen Kennlinien von Lichter, Tiefen, Weiss, Schwarz und Kontrast fuer das
    ''' Tonmodell 2 (ImageAdjustments.ToneModel).
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
        ''' LoadResource fuellt dieses hier.</summary>
        Private Shared ReadOnly MedianCurves As New Dictionary(Of String, (Medians As Double(), Sets As Double()()()))(StringComparer.Ordinal)

        ''' <summary>Tonmodell 3: eigene Gruppenmitten fuer dieselben Kurven (Schluessel
        ''' "mediansModel3"). Die Mitten stammen aus unseren Nullbildern, und die liegen mit der
        ''' Grundbelichtung je Kamera (seit sie ab Werk gilt) rund 6 L tiefer als ohne. Modell 2 behaelt
        ''' die Mitten, mit denen es veroeffentlicht wurde, damit gespeicherte Bearbeitungen gleich
        ''' bleiben. Steht wie MedianCurves VOR Curves.</summary>
        Private Shared ReadOnly MedianCentersModel3 As New Dictionary(Of String, Double())(StringComparer.Ordinal)

        ''' <summary>Weiss je Spitze des Bildes (Schluessel Whites): das 99. Perzentil von L je Gruppe,
        ''' und je Stellung eine Kurve je Gruppe. Gemessen haengt Weiss nicht am Median, sondern daran,
        ''' wie nah die Lichter schon an Weiss stehen: Weiss +100 hebt ein flaues Bild weit und eines,
        ''' das schon oben anstoesst, kaum. In der Kreuzprobe ueber 57 Bilder sank der Fehler bei
        ''' Weiss +100 von 8,1 auf 3,3 L. Nur im Tonmodell 3. Steht wie MedianCurves VOR Curves.</summary>
        Private Shared ReadOnly TopCurves As New Dictionary(Of String, (Medians As Double(), Sets As Double()()()))(StringComparer.Ordinal)

        ''' <summary>Tiefen je Anteil heller Bildpunkte (Schluessel Shadows, Eintrag
        ''' "ShadowsByBrightShare"): Prozent der Bildpunkte ueber L 70 je Gruppe ("centres"), und je
        ''' Stellung eine Kurve je Gruppe. Gemessen hebt Tiefen +100 die dunklen Stellen eines Bildes
        ''' mit viel heller Flaeche weiter als nach dem Median erwartet; mit Kameratabelle an 57
        ''' Eichbildern und 25 Bildern der Gegenprobe besser als die Gruppen nach dem Median (Tiefen
        ''' +100 4,56 auf 4,04 in der Gegenprobe). Nur im Tonmodell 4. Steht wie MedianCurves VOR
        ''' Curves.</summary>
        Private Shared ReadOnly BrightShareCurves As New Dictionary(Of String, (Medians As Double(), Sets As Double()()()))(StringComparer.Ordinal)

        ''' <summary>Die Grenze, ueber der ein Bildpunkt als hell zaehlt, in L. Muss mit
        ''' Diagnostics/Reglereichung/kennlinien.py (SHARE_LIMITS) uebereinstimmen.</summary>
        Public Const BrightShareLimitL As Double = 70.0

        ''' <summary>Kurven je Regler: Index 0 bis 3 fuer -100, -50, +50, +100. Nothing, wenn die
        ''' Ressource fehlt; dann rechnet Modell 2 wie Modell 1.</summary>
        ''' <summary>Verstaerkung je Regler und Stellung (Index wie bei Curves), Schluessel "gains" der
        ''' Datei. Die Kurven sind Mediane ueber die Bilder; die mittlere Wirkung der Referenz ist bei
        ''' manchen Reglern kraeftiger, gemessen als Faktor unserer Wirkung durch ihre. Der Kehrwert
        ''' steht hier. Fehlt ein Eintrag, gilt 1. Steht wie MedianCurves VOR Curves.</summary>
        Private Shared ReadOnly Gains As New Dictionary(Of String, Double())(StringComparer.Ordinal)

        ''' <summary>Tonmodell 3: Verstaerkung der Feinstruktur je Zone, Schluessel "localDetail" der
        ''' Datei. Je Regler und Stellung (Index wie bei Curves) ein Faktor je Zonenmitte
        ''' (DetailCenters, in L). Gemessen lassen Lichter und Tiefen die Struktur nicht einfach
        ''' stehen: in ihrer eigenen Zone verstaerken sie sie beim Zurueckholen (Lichter -100 in den
        ''' Lichtern 1,19, Tiefen +100 in den tiefen Tiefen 1,47) und daempfen sie beim Verstaerken.
        ''' Steht wie MedianCurves VOR Curves.</summary>
        Private Shared ReadOnly DetailGains As New Dictionary(Of String, Double()())(StringComparer.Ordinal)
        Private Shared DetailCenters As Double()

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
                                Gains(slider.Name) = g
                            Next
                        End If
                        Dim detailElement As JsonElement
                        If root.TryGetProperty("localDetail", detailElement) Then
                            Dim centers = detailElement.GetProperty("centers").EnumerateArray().Select(Function(e) e.GetDouble()).ToArray()
                            For Each slider In detailElement.EnumerateObject()
                                If slider.Value.ValueKind <> JsonValueKind.Object Then Continue For
                                Dim sets = New Double(3)() {}
                                Dim dkeys = {"-100", "-50", "50", "100"}
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
                            DetailCenters = centers
                        End If
                        For Each slider In root.GetProperty("curves").EnumerateObject()
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
                                    target(key) = (medians, sets)
                                    Dim model3Element As JsonElement
                                    If byMedian AndAlso slider.Value.TryGetProperty("mediansModel3", model3Element) Then
                                        Dim centres3 = model3Element.EnumerateArray().Select(Function(e) e.GetDouble()).ToArray()
                                        If centres3.Length = medians.Length Then MedianCentersModel3(key) = centres3
                                    End If
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

        ''' <summary>Liegt fuer <paramref name="slider"/> eine gemessene Kurve vor?</summary>
        Public Shared Function Has(slider As String) As Boolean
            Return Curves IsNot Nothing AndAlso Curves.ContainsKey(slider)
        End Function

        ''' <summary>Hub des Reglers <paramref name="slider"/> (Highlights, Shadows, Whites, Blacks,
        ''' Contrast; ParametricShadows/Darks/Lights/Highlights fuer den Preset-Import) bei Wert <paramref name="value"/> (-100 bis 100) am Gammawert
        ''' <paramref name="v"/>. Zwischen 0 und 50 und zwischen 50 und 100 linear. Die Verstaerkung
        ''' haengt am <paramref name="toneModel"/> (siehe <see cref="Gain"/>).</summary>
        Public Shared Function Lift(slider As String, value As Double, v As Double,
                                    Optional toneModel As Integer = 2) As Double
            If value = 0.0 OrElse Curves Is Nothing Then Return 0.0
            Dim set4 As Double()() = Nothing
            If Not Curves.TryGetValue(slider, set4) Then Return 0.0
            Dim magnitude = Math.Min(100.0, Math.Abs(value))
            Dim half = If(value < 0, set4(1), set4(2))
            Dim full = If(value < 0, set4(0), set4(3))
            Dim atHalf = Sample(half, v) * Gain(slider, value, False, toneModel)
            If magnitude <= 50.0 Then Return atHalf * magnitude / 50.0
            Return atHalf + (Sample(full, v) * Gain(slider, value, True, toneModel) - atHalf) * (magnitude - 50.0) / 50.0
        End Function

        ''' <summary>Wie <see cref="Lift"/>, aber nach der Helligkeit des ganzen Bildes: liegen fuer den
        ''' Regler Kurven je Bildmedian vor, wird zwischen den Gruppen linear nach
        ''' <paramref name="imageMedianL"/> (L, 0 bis 100) geteilt, ausserhalb die naechste Gruppe.
        ''' Ohne solche Kurven oder ohne Median (0 oder weniger) die mittlere Kurve. Ab
        ''' <paramref name="toneModel"/> 3 gelten die Mitten aus "mediansModel3", wo es sie gibt.</summary>
        Public Shared Function Lift(slider As String, value As Double, v As Double, imageMedianL As Double,
                                    Optional toneModel As Integer = 2) As Double
            Dim entry As (Medians As Double(), Sets As Double()()()) = Nothing
            If imageMedianL <= 0.0 OrElse value = 0.0 OrElse Not MedianCurves.TryGetValue(slider, entry) Then
                Return Lift(slider, value, v, toneModel)
            End If
            Dim centres3 As Double() = Nothing
            If toneModel >= 3 AndAlso MedianCentersModel3.TryGetValue(slider, centres3) Then
                entry = (centres3, entry.Sets)
            End If
            Return LiftGrouped(slider, entry, value, v, imageMedianL, toneModel:=toneModel)
        End Function

        ''' <summary>Wie <see cref="Lift"/>, aber nach der Spitze des Bildes: liegen fuer den Regler
        ''' Kurven je Spitze vor (bisher nur Weiss), wird zwischen den Gruppen linear nach
        ''' <paramref name="imageTopL"/> (99. Perzentil von L, 0 bis 100) geteilt, ausserhalb die
        ''' naechste Gruppe. Ohne solche Kurven oder ohne Spitze (0 oder weniger) die mittlere Kurve.
        ''' Die Verstaerkung steht unter "WhitesByTop" in "gains", getrennt von der des Reglers: die
        ''' Kurven je Spitze rechnet nur Tonmodell 3, und Modell 2 behaelt so seine Staerke.</summary>
        Public Shared Function LiftByTop(slider As String, value As Double, v As Double, imageTopL As Double) As Double
            Dim entry As (Medians As Double(), Sets As Double()()()) = Nothing
            If imageTopL <= 0.0 OrElse value = 0.0 OrElse Not TopCurves.TryGetValue(slider, entry) Then
                Return Lift(slider, value, v, 3)
            End If
            Return LiftGrouped(slider, entry, value, v, imageTopL, slider & "ByTop", 3)
        End Function

        ''' <summary>Wie <see cref="Lift"/>, aber nach dem Anteil heller Bildpunkte: liegen fuer den
        ''' Regler Kurven je Anteil vor (bisher nur Tiefen), wird zwischen den Gruppen linear nach
        ''' <paramref name="brightSharePercent"/> (Prozent ueber <see cref="BrightShareLimitL"/>)
        ''' geteilt, ausserhalb die naechste Gruppe. Ohne solche Kurven die Gruppen nach dem Median
        ''' (<paramref name="imageMedianL"/>), wie im Tonmodell 3. Die Verstaerkung ist die des Reglers,
        ''' mit der auch gemessen wurde.</summary>
        Public Shared Function LiftByBrightShare(slider As String, value As Double, v As Double,
                                                 brightSharePercent As Double, imageMedianL As Double,
                                                 toneModel As Integer) As Double
            Dim entry As (Medians As Double(), Sets As Double()()()) = Nothing
            If imageMedianL <= 0.0 OrElse value = 0.0 OrElse Not BrightShareCurves.TryGetValue(slider, entry) Then
                Return Lift(slider, value, v, imageMedianL, toneModel)
            End If
            Return LiftGrouped(slider, entry, value, v, brightSharePercent, toneModel:=toneModel)
        End Function

        ''' <summary>Hub aus Kurven je Gruppe: linear zwischen den beiden Gruppen, deren Mitten
        ''' <paramref name="measure"/> einschliessen, ausserhalb die naechste Gruppe; im Wert wie
        ''' <see cref="Lift"/> zwischen 0, 50 und 100 linear.</summary>
        Private Shared Function LiftGrouped(slider As String, entry As (Medians As Double(), Sets As Double()()()),
                                            value As Double, v As Double, measure As Double,
                                            Optional gainKey As String = Nothing,
                                            Optional toneModel As Integer = 2) As Double
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
            Dim atHalf = (Sample(halfSet(gi), v) + (Sample(halfSet(gi + 1), v) - Sample(halfSet(gi), v)) * f) * Gain(gainSlider, value, False, toneModel)
            If magnitude <= 50.0 Then Return atHalf * magnitude / 50.0
            Dim atFull = (Sample(fullSet(gi), v) + (Sample(fullSet(gi + 1), v) - Sample(fullSet(gi), v)) * f) * Gain(gainSlider, value, True, toneModel)
            Return atHalf + (atFull - atHalf) * (magnitude - 50.0) / 50.0
        End Function

        ''' <summary>Tonmodell 3: Faktor auf die Feinstruktur fuer Regler <paramref name="slider"/>
        ''' (Highlights, Shadows) bei Wert <paramref name="value"/>, an der geglaetteten Helligkeit
        ''' <paramref name="lightness"/> (L, 0 bis 100). Zwischen den Zonenmitten linear, ausserhalb
        ''' die naechste; im Wert wie <see cref="Lift"/> zwischen 0, 50 und 100 linear. 1 ohne
        ''' gemessene Werte.</summary>
        Public Shared Function DetailGain(slider As String, value As Double, lightness As Double) As Double
            Dim sets As Double()() = Nothing
            If value = 0.0 OrElse DetailCenters Is Nothing OrElse Not DetailGains.TryGetValue(slider, sets) Then Return 1.0
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
            Dim half = If(value < 0, sets(1), sets(2))
            Dim full = If(value < 0, sets(0), sets(3))
            Dim atHalf = half(gi) + (half(gi + 1) - half(gi)) * f
            If magnitude <= 50.0 Then Return 1.0 + (atHalf - 1.0) * magnitude / 50.0
            Dim atFull = full(gi) + (full(gi + 1) - full(gi)) * f
            Return atHalf + (atFull - atHalf) * (magnitude - 50.0) / 50.0
        End Function

        ''' <summary>Verstaerkung der Stellung +-50 (<paramref name="full"/> False) oder +-100 in der
        ''' Richtung von <paramref name="value"/>. Ab <paramref name="toneModel"/> 3 gilt ein Eintrag
        ''' "&lt;Regler&gt;Model3", wo es ihn gibt (seit der Eichung mit Kameratabelle, 2026-10-20), sonst
        ''' der gemeinsame; Modell 2 behaelt so die Staerke, mit der es veroeffentlicht wurde.</summary>
        Private Shared Function Gain(slider As String, value As Double, full As Boolean,
                                     Optional toneModel As Integer = 2) As Double
            Dim g As Double() = Nothing
            If Not (toneModel >= 3 AndAlso Gains.TryGetValue(slider & "Model3", g)) AndAlso
               Not Gains.TryGetValue(slider, g) Then Return 1.0
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
