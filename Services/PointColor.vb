Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.Linq

Namespace Services

    ''' <summary>Ein Farbfeld der Punktfarbe: eine aufgenommene Farbe, ihre Verschiebung in
    ''' Farbton, Sättigung und Helligkeit, ihre Streuung und der Bereich, den sie trifft.
    '''
    ''' <para>DIE EINHEITEN SIND DIE DES REFERENZPROGRAMMS, wie es sie in <c>crs:PointColors</c>
    ''' und <c>crs:ColorVariance</c> ablegt. Die aufgenommene Farbe steht im HSV von LINEAREM
    ''' ProPhoto RGB: Farbton in Sechsteln einer Umdrehung (0 bis 6), Sättigung 0 bis 1, Wert linear
    ''' 0 bis 1. Verschiebungen und Streuung -1 bis 1 (am Regler -100 bis 100), der Bereich 0 bis 1
    ''' (am Regler 0 bis 100, ab Werk 50). Die drei Bereiche sind Trapeze aus je vier Punkten,
    ''' aussen, innen, innen, aussen: der Farbton 0 bis 1 über sein Fenster mit der Farbe bei 0,5,
    ''' die Sättigung auf der HSV-Sättigung, die Helligkeit auf dem gammakodierten HSV-Wert.</para>
    '''
    ''' <para>Im Rezept steht die Liste als Text (<see cref="ImageAdjustments.PointColors"/>): je
    ''' Farbfeld 20 Zahlen, die 19 aus <c>crs:PointColors</c> und dahinter die Streuung, Felder
    ''' durch Semikolon getrennt. So bleibt das Rezept ein flaches Objekt, und der Weg in die
    ''' XMP-Form ist eine reine Umformatierung.</para></summary>
    Public NotInheritable Class PointColorSwatch

        ''' <summary>Mehr Farbfelder nimmt das Referenzprogramm nicht an.</summary>
        Public Const MaxSwatches As Integer = 8

        Public Property Hue As Double
        Public Property Saturation As Double
        Public Property Value As Double
        Public Property HueShift As Double
        Public Property SaturationShift As Double
        Public Property LuminanceShift As Double
        Public Property Range As Double = 0.5
        Public Property HueRange As Double() = {0.0, 1.0 / 3.0, 2.0 / 3.0, 1.0}
        Public Property SaturationRange As Double() = {0.0, 0.0, 1.0, 1.0}
        Public Property LuminanceRange As Double() = {0.0, 0.0, 1.0, 1.0}
        Public Property Variance As Double
        ''' <summary>Zeigt die Vorschau, was dieses Feld trifft? Steht nur im Vorschau-Rezept, als
        ''' 21. Zahl des Felds (<see cref="FormatList"/> mit includeVisualize), nie im gespeicherten.
        ''' So reist die Anzeige mit den Reglern dorthin, wo sie gerade gelten: Foto, Ebene oder
        ''' Korrekturebene, und jeder Schlüssel eines Zwischenspeichers sieht sie mit.</summary>
        Public Property Visualize As Boolean

        ''' <summary>Ein frisch aufgenommenes Farbfeld mit den Bereichen, die das Referenzprogramm
        ''' dabei setzt: der Farbton über das ganze Fenster mit innerem Drittel, Sättigung und
        ''' Helligkeit 0,18 innen und 0,73 aussen um die aufgenommene Farbe.</summary>
        Public Shared Function Sampled(hue As Double, saturation As Double, value As Double) As PointColorSwatch
            Dim around = Function(c As Double) New Double() {
                Clamp01(c - 0.73), Clamp01(c - 0.18), Clamp01(c + 0.18), Clamp01(c + 0.73)}
            Return New PointColorSwatch With {
                .Hue = Math.Min(5.9999, PositiveModulo(hue, 6.0)),
                .Saturation = saturation,
                .Value = value,
                .SaturationRange = around(saturation),
                .LuminanceRange = around(PointColorMath.SrgbEncode(value))}
        End Function

        ''' <summary>Ein Farbfeld aus einer gammakodierten sRGB-Farbe (0 bis 1), wie sie die
        ''' Punktfarbe an ihrer Stelle in der Kette sieht. Nothing, wenn die Farbe zu neutral oder zu
        ''' dunkel ist, um sie aufzunehmen: unter der Sättigung, ab der jede Auswahl ausblendet,
        ''' oder unter einem kodierten Wert von 0,06.</summary>
        Public Shared Function SampleFromSrgb(r As Double, g As Double, b As Double) As PointColorSwatch
            Dim lr = PointColorMath.SrgbDecode(Clamp01(r))
            Dim lg = PointColorMath.SrgbDecode(Clamp01(g))
            Dim lb = PointColorMath.SrgbDecode(Clamp01(b))
            PointColorMath.Transform(PointColorMath.SrgbToProPhoto, lr, lg, lb)
            Dim h As Double, s As Double, v As Double
            If Not PointColorMath.ToHsv(Math.Max(0.0, lr), Math.Max(0.0, lg), Math.Max(0.0, lb), h, s, v) Then Return Nothing
            If s < PointColorMath.NeutralSaturation OrElse PointColorMath.SrgbEncode(Math.Min(1.0, v)) < 0.06 Then Return Nothing
            Return Sampled(h / (2.0 * Math.PI) * 6.0, s, Math.Min(1.0, v))
        End Function

        ''' <summary>Die aufgenommene Farbe als gammakodiertes sRGB (0 bis 1), für die Anzeige.</summary>
        Public Function SourceSrgb() As Double()
            Dim r As Double, g As Double, b As Double
            PointColorMath.FromHsv(Hue / 6.0 * 2.0 * Math.PI, Saturation, Value, r, g, b)
            Return ToEncodedSrgb(r, g, b)
        End Function

        ''' <summary>Die aufgenommene Farbe, so wie das Farbfeld sie verändert, als gammakodiertes
        ''' sRGB (0 bis 1): das Gegenstück zu <see cref="SourceSrgb"/> in der Anzeige.</summary>
        Public Function ResultSrgb() As Double()
            Dim r As Double, g As Double, b As Double
            PointColorMath.FromHsv(Hue / 6.0 * 2.0 * Math.PI, Saturation, Value, r, g, b)
            Dim prepared = PointColorMath.Prepare(FormatList({Me}))
            If prepared IsNot Nothing Then PointColorMath.Apply(prepared, r, g, b)
            Return ToEncodedSrgb(r, g, b)
        End Function

        Private Shared Function ToEncodedSrgb(r As Double, g As Double, b As Double) As Double()
            PointColorMath.Transform(PointColorMath.ProPhotoToSrgb, r, g, b)
            Return {PointColorMath.SrgbEncode(Clamp01(r)), PointColorMath.SrgbEncode(Clamp01(g)), PointColorMath.SrgbEncode(Clamp01(b))}
        End Function

        ''' <summary>Würde das Referenzprogramm das Farbfeld annehmen? Es verwirft eines mit Werten
        ''' ausserhalb ihres Bereichs, mit einem Farbtonbereich ohne inneren Abschnitt, und eines,
        ''' dessen Sättigungs- oder Helligkeitsbereich die aufgenommene Farbe nicht zwischen seinen
        ''' inneren Punkten hält.</summary>
        Public Function IsValid() As Boolean
            Dim ordered = Function(r As Double()) r IsNot Nothing AndAlso r.Length = 4 AndAlso
                                                  r.All(Function(x) x >= 0.0 AndAlso x <= 1.0) AndAlso
                                                  r(0) <= r(1) AndAlso r(1) <= r(2) AndAlso r(2) <= r(3)
            Dim holds = Function(r As Double(), x As Double) r(1) <= x + 0.0001 AndAlso x <= r(2) + 0.0001
            Dim inUnit = Function(x As Double) x >= -1.0 AndAlso x <= 1.0
            If Not (Hue >= 0.0 AndAlso Hue < 6.0) Then Return False
            If Not (Saturation >= 0.0 AndAlso Saturation <= 1.0) Then Return False
            If Not (Value >= 0.0 AndAlso Value <= 1.0) Then Return False
            If Not (inUnit(HueShift) AndAlso inUnit(SaturationShift) AndAlso inUnit(LuminanceShift) AndAlso inUnit(Variance)) Then Return False
            If Not (Range >= 0.0 AndAlso Range <= 1.0) Then Return False
            If Not (ordered(HueRange) AndAlso ordered(SaturationRange) AndAlso ordered(LuminanceRange)) Then Return False
            If Not HueRange(1) < HueRange(2) Then Return False
            Return holds(SaturationRange, Saturation) AndAlso holds(LuminanceRange, PointColorMath.SrgbEncode(Value))
        End Function

        ''' <summary>Ändert das Farbfeld überhaupt etwas?</summary>
        Public Function IsActive() As Boolean
            Return HueShift <> 0.0 OrElse SaturationShift <> 0.0 OrElse LuminanceShift <> 0.0 OrElse Variance <> 0.0
        End Function

        Public Function Clone() As PointColorSwatch
            Dim copy = DirectCast(MemberwiseClone(), PointColorSwatch)
            copy.HueRange = DirectCast(HueRange.Clone(), Double())
            copy.SaturationRange = DirectCast(SaturationRange.Clone(), Double())
            copy.LuminanceRange = DirectCast(LuminanceRange.Clone(), Double())
            Return copy
        End Function

        ' ── Textform ──────────────────────────────────────────────────────────────────

        ''' <summary>Liest die Liste aus dem Rezept. Felder, die das Referenzprogramm verwerfen
        ''' würde, und alles über acht fallen weg; ein leerer oder kaputter Text ergibt eine leere
        ''' Liste, nie einen Fehler, denn der Text kommt aus Dateien.</summary>
        Public Shared Function ParseList(text As String) As List(Of PointColorSwatch)
            Dim result As New List(Of PointColorSwatch)()
            If String.IsNullOrWhiteSpace(text) Then Return result
            For Each entry In text.Split(";"c)
                Dim numbers = ParseNumbers(entry)
                If numbers Is Nothing OrElse numbers.Length < 19 Then Continue For
                Dim swatch = FromNumbers(numbers, If(numbers.Length >= 20, numbers(19), 0.0))
                If swatch IsNot Nothing AndAlso numbers.Length >= 21 Then swatch.Visualize = numbers(20) <> 0.0
                If swatch IsNot Nothing AndAlso swatch.IsValid() AndAlso result.Count < MaxSwatches Then result.Add(swatch)
            Next
            Return result
        End Function

        ''' <summary>Die Liste als Rezepttext, sechs Nachkommastellen wie im Referenzformat. Eine
        ''' leere Liste ergibt den leeren Text, also den Werkszustand.</summary>
        Public Shared Function FormatList(list As IEnumerable(Of PointColorSwatch), Optional includeVisualize As Boolean = False) As String
            If list Is Nothing Then Return ""
            Return String.Join("; ", list.Select(Function(s) String.Join(", ",
                s.CrsNumbers().Concat({s.Variance}).
                    Concat(If(includeVisualize AndAlso s.Visualize, {1.0}, Array.Empty(Of Double)())).
                    Select(Function(x) x.ToString("0.######", CultureInfo.InvariantCulture)))))
        End Function

        ''' <summary>Liest die Farbfelder aus den Einträgen von <c>crs:PointColors</c> und
        ''' <c>crs:ColorVariance</c>, wie ein XMP-Preset sie trägt. Die leere Auswahl des
        ''' Referenzprogramms (19 mal -1) und Felder, die es selbst verwerfen würde, fallen weg.</summary>
        Public Shared Function FromCrs(points As IList(Of String), variances As IList(Of String)) As List(Of PointColorSwatch)
            Dim result As New List(Of PointColorSwatch)()
            If points Is Nothing Then Return result
            For i = 0 To points.Count - 1
                Dim numbers = ParseNumbers(points(i))
                If numbers Is Nothing OrElse numbers.Length <> 19 Then Continue For
                If numbers.All(Function(x) x = -1.0) Then Continue For
                Dim variance = 0.0
                If variances IsNot Nothing AndAlso i < variances.Count Then
                    Dim v = ParseNumbers(variances(i))
                    If v IsNot Nothing AndAlso v.Length > 0 Then variance = v(0)
                End If
                Dim swatch = FromNumbers(numbers, variance)
                If swatch IsNot Nothing AndAlso swatch.IsValid() AndAlso result.Count < MaxSwatches Then result.Add(swatch)
            Next
            Return result
        End Function

        Private Function CrsNumbers() As Double()
            Return New Double() {Hue, Saturation, Value, HueShift, SaturationShift, LuminanceShift, Range}.
                Concat(HueRange).Concat(SaturationRange).Concat(LuminanceRange).ToArray()
        End Function

        Private Shared Function FromNumbers(v As Double(), variance As Double) As PointColorSwatch
            Return New PointColorSwatch With {
                .Hue = v(0), .Saturation = v(1), .Value = v(2),
                .HueShift = v(3), .SaturationShift = v(4), .LuminanceShift = v(5),
                .Range = v(6),
                .HueRange = {v(7), v(8), v(9), v(10)},
                .SaturationRange = {v(11), v(12), v(13), v(14)},
                .LuminanceRange = {v(15), v(16), v(17), v(18)},
                .Variance = variance}
        End Function

        Private Shared Function ParseNumbers(text As String) As Double()
            If text Is Nothing Then Return Nothing
            Dim parts = text.Split(","c).Select(Function(p) p.Trim()).Where(Function(p) p.Length > 0).ToArray()
            Dim numbers(parts.Length - 1) As Double
            For i = 0 To parts.Length - 1
                If Not Double.TryParse(parts(i), NumberStyles.Float, CultureInfo.InvariantCulture, numbers(i)) OrElse
                   Not Double.IsFinite(numbers(i)) Then Return Nothing
            Next
            Return numbers
        End Function

        Private Shared Function Clamp01(x As Double) As Double
            Return Math.Max(0.0, Math.Min(1.0, x))
        End Function

        Private Shared Function PositiveModulo(x As Double, m As Double) As Double
            Dim r = x Mod m
            Return If(r < 0.0, r + m, r)
        End Function

    End Class

    ''' <summary>Die Rechnung der Punktfarbe.
    '''
    ''' <para>GEMESSEN, NICHT ENTWORFEN. Die Konstanten sind eine Anpassung an Renderings des
    ''' Referenzprogramms auf synthetischen Farbtafeln: Dichte Raster in Farbton, Sättigung und
    ''' Wert, einzelne Farbfelder mit jeder Verschiebung, jedem Bereich und der Streuung, dazu
    ''' überlappende Paare. Die Messung hat auch den Farbraum festgelegt: eine Farbtonverschiebung
    ''' lässt HSV-Sättigung und -Wert in linearem ProPhoto RGB auf 0,001 genau stehen, also rechnet
    ''' das Referenzprogramm dort, wo auch sein Farbmischer rechnet.</para>
    '''
    ''' <para>JE FARBFELD: das Gewicht eines Bildpunkts ist das Produkt aus Farbton-, Sättigungs-
    ''' und Helligkeitsgewicht, je ein Trapez mit geglätteten Rampen, und blendet unter einer
    ''' Sättigung von 0,058 aus. Der Bereich streckt vorher den Abstand zur aufgenommenen Farbe.
    ''' Innerhalb der Auswahl dreht der Farbton um bis zu 35 Grad, die Sättigung wird mit bis zu
    ''' 1,9 multipliziert oder geteilt, der Wert mit 2^(1,39 |x| - 0,24 x²). Die Streuung schiebt
    ''' Farbtöne von der aufgenommenen Farbe weg oder zu ihr hin und streckt Sättigung und Wert um
    ''' sie. Die Farbfelder wirken nacheinander, jedes auf das Ergebnis der vorigen.</para></summary>
    Friend NotInheritable Class PointColorMath

        Private Const HueWindow As Double = 0.7236
        Private Shared ReadOnly RangeBelow As Double() = {0.767, 1.7637, 0.5482}
        Private Shared ReadOnly RangeAbove As Double() = {0.1186, 0.3886, 0.0598}
        Private Const HueRamp As Double = 1.0282
        Private Shared ReadOnly SaturationRamp As Double() = {1.8348, 0.9897}
        Private Shared ReadOnly LuminanceRamp As Double() = {2.7765, 1.1354}
        ''' <summary>Unter dieser HSV-Sättigung blendet jede Auswahl aus.</summary>
        Friend Const NeutralSaturation As Double = 0.0578
        Private Const HueShiftRadians As Double = 0.6109
        Private Const SaturationShiftGain As Double = 0.9
        Private Shared ReadOnly LuminanceShiftGain As Double() = {1.3932, -0.2366}
        Private Const VarianceSaturation As Double = 0.7904
        Private Const VarianceLuminance As Double = 0.2123

        Private Const TwoPi As Double = 2.0 * Math.PI

        ''' <summary>Ein Farbfeld, fertig zum Rechnen.</summary>
        Friend NotInheritable Class Prepared
            Friend Hue As Double
            Friend Saturation As Double
            Friend Luminance As Double
            Friend Scale(2) As Double
            Friend HuePoints(3) As Double
            Friend SaturationPoints(3) As Double
            Friend LuminancePoints(3) As Double
            Friend HueTurn As Double
            Friend LogSaturation As Double
            Friend LogValue As Double
            Friend Variance As Double
            ''' Zeigt dieses Farbfeld gerade, was es trifft (nur in der Vorschau)?
            Friend Visualize As Boolean
        End Class

        ''' <summary>Die wirksamen Farbfelder eines Rezepts, oder Nothing, wenn keines etwas tut. Ein
        ''' Feld, dessen Bereich die Vorschau zeigen soll, kommt auch ohne Verschiebung mit, sonst
        ''' gäbe es für ein frisch aufgenommenes Feld nichts zu sehen.</summary>
        Friend Shared Function Prepare(recipeText As String) As Prepared()
            Dim all = PointColorSwatch.ParseList(recipeText)
            Dim result As New List(Of Prepared)()
            For i = 0 To all.Count - 1
                Dim show = all(i).Visualize
                If Not all(i).IsActive() AndAlso Not show Then Continue For
                Dim p = PrepareOne(all(i))
                p.Visualize = show
                result.Add(p)
            Next
            Return If(result.Count = 0, Nothing, result.ToArray())
        End Function

        Private Shared Function PrepareOne(s As PointColorSwatch) As Prepared
            Dim p As New Prepared With {
                .Hue = s.Hue / 6.0 * TwoPi,
                .Saturation = s.Saturation,
                .Luminance = SrgbEncode(s.Value),
                .HueTurn = s.HueShift * HueShiftRadians,
                .LogSaturation = Math.Log(1.0 + SaturationShiftGain * Math.Abs(s.SaturationShift), 2.0) * Math.Sign(s.SaturationShift),
                .Variance = s.Variance}
            Dim l = Math.Abs(s.LuminanceShift)
            p.LogValue = (LuminanceShiftGain(0) * l + LuminanceShiftGain(1) * l * l) * Math.Sign(s.LuminanceShift)
            For d = 0 To 2
                Dim k = If(s.Range < 0.5, RangeBelow(d), RangeAbove(d))
                p.Scale(d) = Math.Pow(2.0, k * (0.5 - s.Range))
            Next
            For i = 0 To 3
                p.HuePoints(i) = (s.HueRange(i) - 0.5) * 2.0 * HueWindow
                p.SaturationPoints(i) = s.SaturationRange(i)
                p.LuminancePoints(i) = s.LuminanceRange(i)
            Next
            Return p
        End Function

        ''' <summary>Wie stark ein Farbfeld eine Farbe in linearem ProPhoto RGB trifft, 0 bis 1.</summary>
        Friend Shared Function Weight(p As Prepared, r As Double, g As Double, b As Double) As Double
            Dim h As Double, s As Double, v As Double
            If Not ToHsv(Math.Max(0.0, r), Math.Max(0.0, g), Math.Max(0.0, b), h, s, v) Then Return 0.0
            Return SelectionWeight(p, h, s, SrgbEncode(Math.Min(1.0, v)), WrapAngle(h - p.Hue))
        End Function

        ''' <summary>Wendet alle Farbfelder nacheinander auf eine Farbe in linearem ProPhoto RGB an.
        ''' Gibt das Gewicht des angezeigten Farbfelds zurück, gemessen an der Farbe, die es sieht,
        ''' oder -1, wenn keines angezeigt wird.</summary>
        Friend Shared Function Apply(swatches As Prepared(), ByRef r As Double, ByRef g As Double, ByRef b As Double) As Double
            Dim shown = -1.0
            For Each p In swatches
                If p.Visualize Then shown = Weight(p, r, g, b)
                ApplyOne(p, r, g, b)
            Next
            Return shown
        End Function

        ''' <summary>Die Anzeige des Bereichs auf einer fertigen, gammakodierten Farbe: grau, wo das
        ''' Farbfeld nichts trifft, die Farbe, wo es voll trifft. Auf das ENDERGEBNIS, damit keine
        ''' spätere Farbstufe das Grau wieder einfärbt.</summary>
        Friend Shared Sub Visualize(weight As Double, ByRef r As Single, ByRef g As Single, ByRef b As Single)
            Dim y = 0.2126 * SrgbDecode(Math.Max(0.0F, Math.Min(1.0F, r))) +
                    0.7152 * SrgbDecode(Math.Max(0.0F, Math.Min(1.0F, g))) +
                    0.0722 * SrgbDecode(Math.Max(0.0F, Math.Min(1.0F, b)))
            Dim gray = SrgbEncode(y)
            r = CSng(gray + (r - gray) * weight)
            g = CSng(gray + (g - gray) * weight)
            b = CSng(gray + (b - gray) * weight)
        End Sub

        Private Shared Sub ApplyOne(p As Prepared, ByRef r As Double, ByRef g As Double, ByRef b As Double)
            Dim qr = Math.Max(0.0, r), qg = Math.Max(0.0, g), qb = Math.Max(0.0, b)
            Dim h As Double, s As Double, v As Double
            If Not ToHsv(qr, qg, qb, h, s, v) Then Return
            Dim ev = SrgbEncode(Math.Min(1.0, v))
            Dim d = WrapAngle(h - p.Hue)
            Dim w = SelectionWeight(p, h, s, ev, d)
            If w <= 0.0 Then Return

            Dim turn = w * (p.HueTurn + p.Variance * d)
            Dim logS = w * p.LogSaturation
            Dim logV = w * p.LogValue
            If p.Variance <> 0.0 Then
                Dim s2 = Math.Max(0.0001, Math.Min(1.0, p.Saturation + (s - p.Saturation) * Math.Pow(2.0, VarianceSaturation * p.Variance)))
                Dim e2 = Math.Max(0.0001, Math.Min(1.0, p.Luminance + (ev - p.Luminance) * Math.Pow(2.0, VarianceLuminance * p.Variance)))
                logS += w * Math.Log(s2 / Math.Max(0.0001, s), 2.0)
                logV += w * Math.Log(SrgbDecode(e2) / Math.Max(0.000001, SrgbDecode(ev)), 2.0)
            End If
            Dim nr As Double, ng As Double, nb As Double
            FromHsv(h + turn, Math.Max(0.0, Math.Min(1.0, s * Math.Pow(2.0, logS))), v * Math.Pow(2.0, logV), nr, ng, nb)
            ' Kanäle klemmen an Weiss: ein aufgehelltes Orange wird gelber, statt über Weiss
            ' hinauszulaufen. Was HSV nicht tragen kann (negative Kanäle), bleibt als Versatz.
            r = Math.Min(nr, Math.Max(qr, 1.0)) + (r - qr)
            g = Math.Min(ng, Math.Max(qg, 1.0)) + (g - qg)
            b = Math.Min(nb, Math.Max(qb, 1.0)) + (b - qb)
        End Sub

        Private Shared Function SelectionWeight(p As Prepared, h As Double, s As Double, ev As Double, d As Double) As Double
            Return Smoothed(d * p.Scale(0), p.HuePoints, HueRamp, HueRamp) *
                   Smoothed(p.Saturation + (s - p.Saturation) * p.Scale(1), p.SaturationPoints, SaturationRamp(0), SaturationRamp(1)) *
                   Smoothed(p.Luminance + (ev - p.Luminance) * p.Scale(2), p.LuminancePoints, LuminanceRamp(0), LuminanceRamp(1)) *
                   Math.Min(1.0, s / NeutralSaturation)
        End Function

        ''' <summary>Ein Trapez über x mit den äusseren Punkten 0 und 3 und den inneren 1 und 2,
        ''' die Rampen geglättet und mit einem Exponenten versehen.</summary>
        Private Shared Function Smoothed(x As Double, pts As Double(), riseExponent As Double, fallExponent As Double) As Double
            Dim rise As Double
            If x >= pts(1) Then
                rise = 1.0
            ElseIf x <= pts(0) Then
                rise = 0.0
            Else
                rise = Math.Pow(SmoothStep((x - pts(0)) / (pts(1) - pts(0))), riseExponent)
            End If
            Dim fall As Double
            If x <= pts(2) Then
                fall = 1.0
            ElseIf x >= pts(3) Then
                fall = 0.0
            Else
                fall = Math.Pow(SmoothStep((pts(3) - x) / (pts(3) - pts(2))), fallExponent)
            End If
            Return Math.Min(rise, fall)
        End Function

        Private Shared Function SmoothStep(t As Double) As Double
            Return t * t * (3.0 - 2.0 * t)
        End Function

        Private Shared Function WrapAngle(d As Double) As Double
            Dim r = (d + Math.PI) Mod TwoPi
            If r < 0.0 Then r += TwoPi
            Return r - Math.PI
        End Function

        ''' <summary>HSV mit dem Farbton im Bogenmass. False für Schwarz, das kein Farbfeld trifft.</summary>
        Friend Shared Function ToHsv(r As Double, g As Double, b As Double, ByRef h As Double, ByRef s As Double, ByRef v As Double) As Boolean
            Dim max = Math.Max(r, Math.Max(g, b))
            Dim min = Math.Min(r, Math.Min(g, b))
            v = max
            If max <= 0.000001 Then Return False
            Dim c = max - min
            s = c / max
            If c <= 0.0 Then
                h = 0.0
            ElseIf max = r Then
                h = ((g - b) / c) Mod 6.0
            ElseIf max = g Then
                h = (b - r) / c + 2.0
            Else
                h = (r - g) / c + 4.0
            End If
            h = h / 6.0 * TwoPi
            If h < 0.0 Then h += TwoPi
            Return True
        End Function

        Friend Shared Sub FromHsv(h As Double, s As Double, v As Double, ByRef r As Double, ByRef g As Double, ByRef b As Double)
            Dim hh = (h / TwoPi * 6.0) Mod 6.0
            If hh < 0.0 Then hh += 6.0
            Dim c = v * s
            Dim x = c * (1.0 - Math.Abs((hh Mod 2.0) - 1.0))
            Dim m = v - c
            Select Case CInt(Math.Floor(hh))
                Case 0 : r = c : g = x : b = 0
                Case 1 : r = x : g = c : b = 0
                Case 2 : r = 0 : g = c : b = x
                Case 3 : r = 0 : g = x : b = c
                Case 4 : r = x : g = 0 : b = c
                Case Else : r = c : g = 0 : b = x
            End Select
            r += m : g += m : b += m
        End Sub

        Friend Shared Function SrgbEncode(v As Double) As Double
            If v <= 0.0031308 Then Return 12.92 * v
            Return 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055
        End Function

        Friend Shared Function SrgbDecode(v As Double) As Double
            If v <= 0.04045 Then Return v / 12.92
            Return Math.Pow((v + 0.055) / 1.055, 2.4)
        End Function

        ' ── sRGB und ProPhoto ─────────────────────────────────────────────────────────

        ''' <summary>Lineares sRGB (D65) nach linearem ProPhoto RGB (D50), Bradford-Adaption. Aus
        ''' den Normmatrizen ausgerechnet statt abgeschrieben, damit keine gerundete Tabelle den
        ''' Hin- und Rückweg auseinanderlaufen lässt.</summary>
        Friend Shared ReadOnly SrgbToProPhoto As Double() = BuildSrgbToProPhoto()
        Friend Shared ReadOnly ProPhotoToSrgb As Double() = Invert(SrgbToProPhoto)

        Private Shared Function BuildSrgbToProPhoto() As Double()
            Dim srgbToXyz = {0.4124564, 0.3575761, 0.1804375,
                             0.2126729, 0.7151522, 0.072175,
                             0.0193339, 0.119192, 0.9503041}
            Dim proPhotoToXyz = {0.7976749, 0.1351917, 0.0313534,
                                 0.2880402, 0.7118741, 0.0000857,
                                 0.0, 0.0, 0.82521}
            Dim bradford = {0.8951, 0.2664, -0.1614,
                            -0.7502, 1.7135, 0.0367,
                            0.0389, -0.0685, 1.0296}
            Dim d65 = {0.95047, 1.0, 1.08883}
            Dim d50 = {0.96422, 1.0, 0.82521}
            Dim coneD65 = Multiply(bradford, d65)
            Dim coneD50 = Multiply(bradford, d50)
            Dim scale = {coneD50(0) / coneD65(0), 0.0, 0.0,
                         0.0, coneD50(1) / coneD65(1), 0.0,
                         0.0, 0.0, coneD50(2) / coneD65(2)}
            Dim adapt = Multiply3(Invert(bradford), Multiply3(scale, bradford))
            Return Multiply3(Invert(proPhotoToXyz), Multiply3(adapt, srgbToXyz))
        End Function

        Friend Shared Sub Transform(m As Double(), ByRef r As Double, ByRef g As Double, ByRef b As Double)
            Dim nr = m(0) * r + m(1) * g + m(2) * b
            Dim ng = m(3) * r + m(4) * g + m(5) * b
            Dim nb = m(6) * r + m(7) * g + m(8) * b
            r = nr : g = ng : b = nb
        End Sub

        Private Shared Function Multiply(m As Double(), v As Double()) As Double()
            Return {m(0) * v(0) + m(1) * v(1) + m(2) * v(2),
                    m(3) * v(0) + m(4) * v(1) + m(5) * v(2),
                    m(6) * v(0) + m(7) * v(1) + m(8) * v(2)}
        End Function

        Private Shared Function Multiply3(a As Double(), b As Double()) As Double()
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
            Dim inv = 1.0 / det
            Return {(m(4) * m(8) - m(5) * m(7)) * inv, (m(2) * m(7) - m(1) * m(8)) * inv, (m(1) * m(5) - m(2) * m(4)) * inv,
                    (m(5) * m(6) - m(3) * m(8)) * inv, (m(0) * m(8) - m(2) * m(6)) * inv, (m(2) * m(3) - m(0) * m(5)) * inv,
                    (m(3) * m(7) - m(4) * m(6)) * inv, (m(1) * m(6) - m(0) * m(7)) * inv, (m(0) * m(4) - m(1) * m(3)) * inv}
        End Function

    End Class

End Namespace
