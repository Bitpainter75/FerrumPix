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

        ''' <summary>Kurven je Regler: Index 0 bis 3 fuer -100, -50, +50, +100. Nothing, wenn die
        ''' Ressource fehlt; dann rechnet Modell 2 wie Modell 1.</summary>
        ''' <summary>Verstaerkung je Regler und Stellung (Index wie bei Curves), Schluessel "gains" der
        ''' Datei. Die Kurven sind Mediane ueber die Bilder; die mittlere Wirkung der Referenz ist bei
        ''' manchen Reglern kraeftiger, gemessen als Faktor unserer Wirkung durch ihre. Der Kehrwert
        ''' steht hier. Fehlt ein Eintrag, gilt 1. Steht wie MedianCurves VOR Curves.</summary>
        Private Shared ReadOnly Gains As New Dictionary(Of String, Double())(StringComparer.Ordinal)

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
                        For Each slider In root.GetProperty("curves").EnumerateObject()
                            If slider.Name.EndsWith("ByMedian", StringComparison.Ordinal) Then
                                Dim medians = slider.Value.GetProperty("medians").EnumerateArray().Select(Function(e) e.GetDouble()).ToArray()
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
                                If complete Then MedianCurves(slider.Name.Substring(0, slider.Name.Length - "ByMedian".Length)) = (medians, sets)
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
        Public Shared Function Lift(slider As String, value As Double, v As Double, imageMedianL As Double) As Double
            Dim entry As (Medians As Double(), Sets As Double()()()) = Nothing
            If imageMedianL <= 0.0 OrElse value = 0.0 OrElse Not MedianCurves.TryGetValue(slider, entry) Then
                Return Lift(slider, value, v)
            End If
            Dim m = entry.Medians
            Dim gi = 0
            Dim f = 0.0
            If imageMedianL <= m(0) Then
                gi = 0 : f = 0.0
            ElseIf imageMedianL >= m(m.Length - 1) Then
                gi = m.Length - 2 : f = 1.0
            Else
                For i = 0 To m.Length - 2
                    If imageMedianL <= m(i + 1) Then
                        gi = i : f = (imageMedianL - m(i)) / (m(i + 1) - m(i))
                        Exit For
                    End If
                Next
            End If
            Dim magnitude = Math.Min(100.0, Math.Abs(value))
            Dim halfSet = If(value < 0, entry.Sets(1), entry.Sets(2))
            Dim fullSet = If(value < 0, entry.Sets(0), entry.Sets(3))
            Dim atHalf = (Sample(halfSet(gi), v) + (Sample(halfSet(gi + 1), v) - Sample(halfSet(gi), v)) * f) * Gain(slider, value, False)
            If magnitude <= 50.0 Then Return atHalf * magnitude / 50.0
            Dim atFull = (Sample(fullSet(gi), v) + (Sample(fullSet(gi + 1), v) - Sample(fullSet(gi), v)) * f) * Gain(slider, value, True)
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
