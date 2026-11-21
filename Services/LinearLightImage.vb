Imports System

Namespace Services

    ''' <summary>
    ''' Ein Bild in linearem Licht, wie es EXR, Radiance HDR und TIFF mit Gleitkomma liefern: drei
    ''' Werte je Bildpunkt (Rot, Gruen, Blau, zeilenweise von oben links), 1,0 ist Weiss im Sinne der
    ''' Datei, Werte darueber sind Licht ueber Weiss und bleiben erhalten. Der Weg danach ist die
    ''' RAW-Grundstufe (RawDecodeServiceLinear.vb): Belichtung als Faktor, Lichterschulter,
    ''' Normierung, Tonkurve.
    '''
    ''' Die Werte stehen in den Primaerfarben der DATEI. Weichen die von sRGB ab (EXR mit dem
    ''' Attribut chromaticities, Radiance mit PRIMARIES), traegt <see cref="ToSrgb"/> die Matrix
    ''' nach linearem sRGB; sie wirkt erst in der Tonstufe, in Gleitkomma, wie die Kameramatrix
    ''' einer RAW. Ohne Matrix sind die Werte schon lineares sRGB (Rec.709, D65).
    ''' </summary>
    Friend NotInheritable Class LinearLightImage

        Public Property Width As Integer
        Public Property Height As Integer
        ''' <summary>Rot, Gruen, Blau je Bildpunkt, Width mal Height mal drei Werte.</summary>
        Public Property Rgb As Single()
        ''' <summary>Die Matrix nach linearem sRGB, zeilenweise (Rot aus den ersten drei), oder
        ''' Nothing, wenn die Werte schon sRGB sind.</summary>
        Public Property ToSrgb As Single()
        ''' <summary>Woher die Werte kamen, fuer das Protokoll (Teil, Kanaele, Primaerfarben).</summary>
        Public Property Description As String = ""

        ''' <summary>Hoechstzahl der Bildpunkte, die ein Leser fuer lineares Licht anlegt. Ein
        ''' Bildpunkt kostet hier 12 Byte als Gleitkomma und danach 6 Byte im Zwischenspeicher der
        ''' RAW-Stufe; 100 Millionen sind damit rund 1,8 GB, mehr wird abgewiesen, bevor ein Feld
        ''' angelegt wird.</summary>
        Public Const MaxPixels As Long = 100L * 1000L * 1000L

        ''' <summary>Darf ein Bild dieser Masse angelegt werden?</summary>
        Public Shared Function SizeAllowed(width As Long, height As Long) As Boolean
            Return width > 0 AndAlso height > 0 AndAlso width <= 65535 AndAlso height <= 65535 AndAlso width * height <= MaxPixels
        End Function

        ' --- Primaerfarben -------------------------------------------------------------------

        ''' <summary>Die Matrix von den Primaerfarben und dem Weisspunkt einer Datei nach linearem
        ''' sRGB (Rec.709, D65), der Weisspunkt mit Bradford auf D65 angepasst. Nothing, wenn die
        ''' Angaben unbrauchbar sind (keine endlichen Zahlen, ein Dreieck ohne Flaeche) oder praktisch
        ''' schon sRGB beschreiben; dann bleiben die Werte, wie sie sind.</summary>
        Public Shared Function MatrixToSrgb(rx As Double, ry As Double, gx As Double, gy As Double,
                                            bx As Double, by As Double, wx As Double, wy As Double) As Single()
            Dim values = {rx, ry, gx, gy, bx, by, wx, wy}
            For Each v In values
                If Not Double.IsFinite(v) OrElse v < -1.0 OrElse v > 2.0 Then Return Nothing
            Next
            If ry <= 0.0 OrElse gy <= 0.0 OrElse by <= 0.0 OrElse wy <= 0.0 Then Return Nothing

            ' Fast sRGB: dann keine Matrix, damit eine Datei mit dem Attribut so aussieht wie eine ohne.
            Const tolerance As Double = 0.0005
            If Math.Abs(rx - 0.64) < tolerance AndAlso Math.Abs(ry - 0.33) < tolerance AndAlso
               Math.Abs(gx - 0.3) < tolerance AndAlso Math.Abs(gy - 0.6) < tolerance AndAlso
               Math.Abs(bx - 0.15) < tolerance AndAlso Math.Abs(by - 0.06) < tolerance AndAlso
               Math.Abs(wx - 0.3127) < tolerance AndAlso Math.Abs(wy - 0.329) < tolerance Then Return Nothing

            Dim toXyz = RgbToXyz(rx, ry, gx, gy, bx, by, wx, wy)
            If toXyz Is Nothing Then Return Nothing
            ' Weiss der Datei nach D65 (Bradford), dann XYZ nach sRGB.
            Dim adapt = BradfordAdaptation(wx / wy, 1.0, (1.0 - wx - wy) / wy, 0.95047, 1.0, 1.08883)
            Dim srgbFromXyz = {3.2404542, -1.5371385, -0.4985314,
                               -0.969266, 1.8760108, 0.041556,
                               0.0556434, -0.2040259, 1.0572252}
            Dim combined = Multiply(srgbFromXyz, Multiply(adapt, toXyz))
            Dim result(8) As Single
            For i = 0 To 8
                If Not Double.IsFinite(combined(i)) Then Return Nothing
                result(i) = CSng(combined(i))
            Next
            Return result
        End Function

        ''' <summary>Die Matrix von CIE XYZ mit dem Weiss (wx, wy) nach linearem sRGB: Bradford auf
        ''' D65, dann die Normmatrix von sRGB.</summary>
        Public Shared Function XyzToSrgb(wx As Double, wy As Double) As Single()
            If Not Double.IsFinite(wx) OrElse Not Double.IsFinite(wy) OrElse wy <= 0.0 Then Return Nothing
            Dim adapt = BradfordAdaptation(wx / wy, 1.0, (1.0 - wx - wy) / wy, 0.95047, 1.0, 1.08883)
            Dim srgbFromXyz = {3.2404542, -1.5371385, -0.4985314,
                               -0.969266, 1.8760108, 0.041556,
                               0.0556434, -0.2040259, 1.0572252}
            Dim combined = Multiply(srgbFromXyz, adapt)
            Dim result(8) As Single
            For i = 0 To 8
                result(i) = CSng(combined(i))
            Next
            Return result
        End Function

        ''' <summary>Die Anteile von Rot, Gruen und Blau an der Helligkeit Y fuer diese
        ''' Primaerfarben (die mittlere Zeile von RGB nach XYZ); ohne brauchbare Angaben die von
        ''' Rec.709.</summary>
        Public Shared Function LuminanceWeights(rx As Double, ry As Double, gx As Double, gy As Double,
                                                bx As Double, by As Double, wx As Double, wy As Double) As Double()
            Dim rec709 = {0.2126, 0.7152, 0.0722}
            If ry <= 0.0 OrElse gy <= 0.0 OrElse by <= 0.0 OrElse wy <= 0.0 Then Return rec709
            Dim m = RgbToXyz(rx, ry, gx, gy, bx, by, wx, wy)
            If m Is Nothing OrElse Not Double.IsFinite(m(3)) OrElse Not Double.IsFinite(m(4)) OrElse Not Double.IsFinite(m(5)) Then Return rec709
            Return {m(3), m(4), m(5)}
        End Function

        ''' <summary>RGB nach XYZ aus den Normfarbwertanteilen der drei Primaerfarben und des Weiss.</summary>
        Private Shared Function RgbToXyz(rx As Double, ry As Double, gx As Double, gy As Double,
                                         bx As Double, by As Double, wx As Double, wy As Double) As Double()
            Dim p = {rx / ry, gx / gy, bx / by,
                     1.0, 1.0, 1.0,
                     (1.0 - rx - ry) / ry, (1.0 - gx - gy) / gy, (1.0 - bx - by) / by}
            Dim inverse = Invert(p)
            If inverse Is Nothing Then Return Nothing
            Dim w = {wx / wy, 1.0, (1.0 - wx - wy) / wy}
            Dim s(2) As Double
            For i = 0 To 2
                s(i) = inverse(i * 3) * w(0) + inverse(i * 3 + 1) * w(1) + inverse(i * 3 + 2) * w(2)
            Next
            Dim m(8) As Double
            For row = 0 To 2
                For col = 0 To 2
                    m(row * 3 + col) = p(row * 3 + col) * s(col)
                Next
            Next
            Return m
        End Function

        ''' <summary>Bradford von einem Weiss (XYZ) zum anderen.</summary>
        Private Shared Function BradfordAdaptation(sx As Double, sy As Double, sz As Double,
                                                   dx As Double, dy As Double, dz As Double) As Double()
            Dim b = {0.8951, 0.2664, -0.1614,
                     -0.7502, 1.7135, 0.0367,
                     0.0389, -0.0685, 1.0296}
            Dim bInverse = Invert(b)
            Dim source = {b(0) * sx + b(1) * sy + b(2) * sz, b(3) * sx + b(4) * sy + b(5) * sz, b(6) * sx + b(7) * sy + b(8) * sz}
            Dim target = {b(0) * dx + b(1) * dy + b(2) * dz, b(3) * dx + b(4) * dy + b(5) * dz, b(6) * dx + b(7) * dy + b(8) * dz}
            Dim scale = {target(0) / source(0), 0.0, 0.0, 0.0, target(1) / source(1), 0.0, 0.0, 0.0, target(2) / source(2)}
            Return Multiply(bInverse, Multiply(scale, b))
        End Function

        Private Shared Function Multiply(a As Double(), b As Double()) As Double()
            Dim m(8) As Double
            For row = 0 To 2
                For col = 0 To 2
                    m(row * 3 + col) = a(row * 3) * b(col) + a(row * 3 + 1) * b(3 + col) + a(row * 3 + 2) * b(6 + col)
                Next
            Next
            Return m
        End Function

        Private Shared Function Invert(m As Double()) As Double()
            Dim det = m(0) * (m(4) * m(8) - m(5) * m(7)) - m(1) * (m(3) * m(8) - m(5) * m(6)) + m(2) * (m(3) * m(7) - m(4) * m(6))
            If Not Double.IsFinite(det) OrElse Math.Abs(det) < 0.000000001 Then Return Nothing
            Dim r(8) As Double
            r(0) = (m(4) * m(8) - m(5) * m(7)) / det
            r(1) = (m(2) * m(7) - m(1) * m(8)) / det
            r(2) = (m(1) * m(5) - m(2) * m(4)) / det
            r(3) = (m(5) * m(6) - m(3) * m(8)) / det
            r(4) = (m(0) * m(8) - m(2) * m(6)) / det
            r(5) = (m(2) * m(3) - m(0) * m(5)) / det
            r(6) = (m(3) * m(7) - m(4) * m(6)) / det
            r(7) = (m(1) * m(6) - m(0) * m(7)) / det
            r(8) = (m(0) * m(4) - m(1) * m(3)) / det
            Return r
        End Function

    End Class

End Namespace
