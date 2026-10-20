Imports System
Imports System.Collections.Concurrent
Imports SkiaSharp

Namespace Services
    ''' <summary>Photoshop-Mischmethoden ohne Skia-Gegenstück. Eingabe und Ausgabe des
    ''' Blenders sind vormultipliziert; die Mischfunktion arbeitet auf geraden Farben.</summary>
    Friend NotInheritable Class PsdBlendModes
        Private Sub New()
        End Sub

        Private Shared ReadOnly Cache As New ConcurrentDictionary(Of String, SKBlender)(StringComparer.Ordinal)

        Public Shared Function IsCustom(name As String) As Boolean
            Select Case If(name, "").Trim().ToLowerInvariant()
                Case "linearburn", "vividlight", "linearlight", "pinlight", "hardmix", "subtract", "divide", "softlight", "psdsoftlight"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        Public Shared Function GetBlender(name As String) As SKBlender
            If Not IsCustom(name) Then Return Nothing
            Return Cache.GetOrAdd(name.Trim().ToLowerInvariant(), AddressOf BuildBlender)
        End Function

        Private Shared Function BuildBlender(name As String) As SKBlender
            Dim expression As String
            Select Case name
                Case "linearburn" : expression = "max(0.0, b + s - 1.0)"
                Case "vividlight" : expression = "vivid(b, s)"
                Case "linearlight" : expression = "clamp(b + 2.0 * s - 1.0, 0.0, 1.0)"
                Case "pinlight" : expression = "s < 0.5 ? min(b, 2.0 * s) : max(b, 2.0 * s - 1.0)"
                Case "hardmix" : expression = "b >= 1.0 ? 1.0 : (b <= 0.0 ? 0.0 : (b + s >= 1.0 ? 1.0 : 0.0))"
                Case "subtract" : expression = "max(0.0, b - s)"
                Case "divide" : expression = "s <= 0.0 ? 1.0 : min(1.0, b / s)"
                Case "softlight", "psdsoftlight" : expression = "s <= 0.5 ? 2.0 * b * s + b * b * (1.0 - 2.0 * s) : 2.0 * b * (1.0 - s) + sqrt(b) * (2.0 * s - 1.0)"
                Case Else : Throw New ArgumentException("Unknown blend mode", NameOf(name))
            End Select
            Dim source = "half vivid(half b, half s) {" & vbLf &
                "  if (s <= 0.0) return 0.0;" & vbLf &
                "  if (s >= 1.0) return 1.0;" & vbLf &
                "  if (s < 0.5) return 1.0 - min(1.0, (1.0 - b) / (2.0 * s));" & vbLf &
                "  return min(1.0, b / (2.0 * (1.0 - s)));" & vbLf &
                "}" & vbLf &
                "half blend(half b, half s) { return " & expression & "; }" & vbLf &
                "half4 main(half4 src, half4 dst) {" & vbLf &
                "  half3 s = src.a > 0.0 ? src.rgb / src.a : half3(0.0);" & vbLf &
                "  half3 b = dst.a > 0.0 ? dst.rgb / dst.a : half3(0.0);" & vbLf &
                "  half3 c = half3(blend(b.r, s.r), blend(b.g, s.g), blend(b.b, s.b));" & vbLf &
                "  return half4(src.rgb * (1.0 - dst.a) + dst.rgb * (1.0 - src.a) + c * src.a * dst.a," & vbLf &
                "               src.a + dst.a * (1.0 - src.a));" & vbLf &
                "}"
            Dim errors As String = Nothing
            Using effect = SKRuntimeEffect.CreateBlender(source, errors)
                If effect Is Nothing Then Throw New InvalidOperationException("PSD blend shader: " & errors)
                Dim blender = effect.ToBlender()
                If blender Is Nothing Then Throw New InvalidOperationException("PSD blend shader could not be instantiated")
                Return blender
            End Using
        End Function
    End Class
End Namespace
