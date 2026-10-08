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

        ''' <summary>Kurven je Regler: Index 0 bis 3 fuer -100, -50, +50, +100. Nothing, wenn die
        ''' Ressource fehlt; dann rechnet Modell 2 wie Modell 1.</summary>
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
                        For Each slider In root.GetProperty("curves").EnumerateObject()
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
            Dim atHalf = Sample(half, v)
            If magnitude <= 50.0 Then Return atHalf * magnitude / 50.0
            Return atHalf + (Sample(full, v) - atHalf) * (magnitude - 50.0) / 50.0
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
