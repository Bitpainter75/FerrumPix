Imports System

Namespace Services

    ''' <summary>Die Farbdaten, die LibRaw zu einer RAW-Datei kennt, ohne jeden Bildpunkt.
    ''' Gefüllt von <see cref="RawDecodeService.ReadCameraColorFacts"/>.</summary>
    Public NotInheritable Class CameraColorFacts

        ''' <summary>Multiplikatoren der AUFNAHME (cam_mul), vier Einträge. Sie machen das Licht
        ''' neutral, unter dem fotografiert wurde - der Decode setzt sie als user_mul.</summary>
        Public Property CamMul As Single()

        ''' <summary>Multiplikatoren der Referenzbeleuchtung der Kamera (pre_mul), vier Einträge.
        ''' LibRaw bildet sie aus der Kameramatrix für Tageslicht; sie sind der Nullpunkt, gegen
        ''' den die Aufnahme-Multiplikatoren gemessen werden.</summary>
        Public Property PreMul As Single()

        ''' <summary>Matrix Kameraraum nach sRGB (rgb_cam), 3x3 zeilenweise. Die Zeilensummen sind
        ''' 1: LibRaw normiert die Matrix so, dass der Weißabgleich getrennt in den Multiplikatoren
        ''' steckt. Genau darauf baut die Rechnung auf.</summary>
        Public Property RgbCam As Single()

        ''' <summary>Dieselbe Matrix nach dcraw_process, oder Nothing. Nur die Messung füllt sie:
        ''' es ist offen, ob LibRaw sie schon nach unpack fertig hat.</summary>
        Public Property RgbCamAfterProcess As Single()

    End Class

    ''' <summary>Der Weißabgleich der Aufnahme als Zahl.</summary>
    Public NotInheritable Class CaptureWhiteBalance

        ''' <summary>Farbtemperatur der Aufnahme in Kelvin.</summary>
        Public Property Kelvin As Double

        ''' <summary>Tönung als Abstand von der Tageslichtlinie, positiv nach Magenta, negativ nach
        ''' Grün, in Einheiten von 1000 v (CIE 1960). NaN unterhalb von 4000 K: dort gilt die
        ''' Tageslichtlinie nicht, und ein Wert wäre eine Erfindung.</summary>
        Public Property Tint As Double

        ''' <summary>Farbort des Aufnahmelichts (CIE 1931).</summary>
        Public Property X As Double
        Public Property Y As Double

    End Class

    ''' <summary>Rechnet aus den Kameradaten einer RAW-Datei die Farbtemperatur der Aufnahme.
    '''
    ''' <para>DER GEDANKENGANG. Eine neutrale Fläche im Bild liefert im Sensor nicht drei gleiche
    ''' Werte, sondern die Antwort des Sensors auf das Licht der Aufnahme; cam_mul sind genau die
    ''' Faktoren, die diese Antwort gleich machen. Die Sensorantwort ist also proportional zu
    ''' 1/cam_mul. LibRaw normiert eine solche Antwort für seine REFERENZBELEUCHTUNG mit pre_mul,
    ''' und rgb_cam ist auf dieselbe Normierung gerechnet. Wer 1/cam_mul mit pre_mul normiert und
    ''' durch rgb_cam schickt, sieht also die Farbe, die eine neutrale Fläche hätte, wenn man sie
    ''' als tageslichtbeleuchtet entwickelt - und deren Farbort IST der Farbort des
    ''' Aufnahmelichts. Kurz: v = pre_mul / cam_mul, dann rgb_cam mal v.</para>
    '''
    ''' <para>DIE PROBE DARAUF steht in <see cref="ReferenceKelvin"/>: sind cam_mul und pre_mul
    ''' gleich, muss genau sRGB-Weiß herauskommen, also D65 mit 6504 K. Wer an dieser Rechnung
    ''' etwas ändert, prüft zuerst diese eine Zahl - ein verdrehtes Vorzeichen fällt dort sofort
    ''' auf, im Bild dagegen erst als Farbstich.</para></summary>
    Public NotInheritable Class CaptureWhiteBalanceService

        Private Sub New()
        End Sub

        ''' <summary>Was die Probe liefern MUSS: der Farbort von sRGB-Weiß ist D65, und dessen
        ''' Farbtemperatur ist 6504 K. Toleranz eine Kelvin-Stelle, mehr sagt die Näherung
        ''' unten nicht zu.</summary>
        Public Const ReferenceKelvin As Double = 6504.0

        ''' <summary>Unterhalb dieser Temperatur wird keine Tönung mehr angegeben: die
        ''' Tageslichtlinie ist dort nicht definiert.</summary>
        Private Const DaylightLocusFloorKelvin As Double = 4000.0

        ''' <summary>Der Weißabgleich der Aufnahme direkt aus einer RAW-Datei, oder Nothing.
        ''' Nothing bei allem, was keine RAW-Datei ist, bei fehlender libraw und bei Dateien ohne
        ''' gültige Multiplikatoren - der Aufrufer behandelt alle drei Fälle gleich, nämlich als
        ''' "keine Angabe".
        '''
        ''' Kostet gemessen unter einer Millisekunde, weil dafür kein Entpacken nötig ist. Wer die
        ''' Zahl in einer Schleife über viele Dateien braucht, darf sie deshalb einfach holen.</summary>
        Public Shared Function ForRawFile(path As String) As CaptureWhiteBalance
            If String.IsNullOrWhiteSpace(path) Then Return Nothing
            If Not RawPreviewService.IsSupportedRaw(path) Then Return Nothing
            Return FromCameraFacts(RawDecodeService.ReadCameraColorFacts(path))
        End Function

        ''' <summary>Der Weißabgleich der Aufnahme, oder Nothing, wenn die Daten dafür nicht
        ''' reichen. Nothing ist ein normaler Fall: manche Dateien tragen keine gültigen
        ''' Multiplikatoren, und dann gibt es die Zahl eben nicht.</summary>
        Public Shared Function FromCameraFacts(facts As CameraColorFacts) As CaptureWhiteBalance
            If facts Is Nothing Then Return Nothing
            Dim matrix = If(facts.RgbCamAfterProcess, facts.RgbCam)
            Return FromMultipliers(facts.CamMul, facts.PreMul, matrix)
        End Function

        ''' <summary>Dieselbe Rechnung mit einzeln übergebenen Größen - so kann die Messung eine
        ''' Matrix gegen die andere stellen, ohne die Reihenfolge zu kennen.</summary>
        Public Shared Function FromMultipliers(camMul As Single(), preMul As Single(),
                                               rgbCam As Single()) As CaptureWhiteBalance
            If camMul Is Nothing OrElse preMul Is Nothing OrElse rgbCam Is Nothing Then Return Nothing
            If camMul.Length < 3 OrElse preMul.Length < 3 OrElse rgbCam.Length < 9 Then Return Nothing

            ' Grün ist der Bezug. Ist es nicht positiv, trägt die Datei keine brauchbaren
            ' Multiplikatoren - das kommt vor und ist kein Fehler.
            If Not IsPositiveFinite(camMul(1)) OrElse Not IsPositiveFinite(preMul(1)) Then Return Nothing

            Dim neutral(2) As Double
            For i = 0 To 2
                If Not IsPositiveFinite(camMul(i)) OrElse Not IsPositiveFinite(preMul(i)) Then Return Nothing
                ' v = pre_mul / cam_mul, auf Grün normiert. Die Normierung ändert den Farbort nicht,
                ' hält die Zahlen aber um 1 herum und damit die Rechnung gutartig.
                neutral(i) = (preMul(i) / preMul(1)) / (camMul(i) / camMul(1))
            Next

            Dim red = rgbCam(0) * neutral(0) + rgbCam(1) * neutral(1) + rgbCam(2) * neutral(2)
            Dim green = rgbCam(3) * neutral(0) + rgbCam(4) * neutral(1) + rgbCam(5) * neutral(2)
            Dim blue = rgbCam(6) * neutral(0) + rgbCam(7) * neutral(1) + rgbCam(8) * neutral(2)

            ' sRGB (linear, D65) nach CIE XYZ.
            Dim bigX = 0.4124564 * red + 0.3575761 * green + 0.1804375 * blue
            Dim bigY = 0.2126729 * red + 0.7151522 * green + 0.0721750 * blue
            Dim bigZ = 0.0193339 * red + 0.1191920 * green + 0.9503041 * blue
            Dim sum = bigX + bigY + bigZ
            If Not IsPositiveFinite(sum) Then Return Nothing

            Dim x = bigX / sum
            Dim y = bigY / sum
            Dim kelvin = CorrelatedColorTemperature(x, y)
            If Double.IsNaN(kelvin) Then Return Nothing

            Return New CaptureWhiteBalance With {
                .Kelvin = kelvin,
                .Tint = TintFromDaylightLocus(x, y, kelvin),
                .X = x,
                .Y = y
            }
        End Function

        ''' <summary>Farbtemperatur aus dem Farbort, Näherung nach McCamy (1992).
        '''
        ''' VORZEICHEN: der Nenner ist (0,1858 − y), NICHT (y − 0,1858). Verdreht liefert die
        ''' Formel für D65 rund 4664 K statt 6504 K - ein Wert, der plausibel genug aussieht, um
        ''' unbemerkt zu bleiben. Genau dagegen steht <see cref="ReferenceKelvin"/>.
        '''
        ''' GRENZE: die Näherung gilt in der Nähe der Planckschen Linie und liegt dort im
        ''' Bereich von etwa 2800 bis 6500 K wenige Kelvin daneben. Weit abseits der Linie, also
        ''' bei stark grünem oder magentafarbenem Licht, wird sie ungenau; für den Vergleich mit
        ''' der Angabe der Kamera reicht sie.</summary>
        Public Shared Function CorrelatedColorTemperature(x As Double, y As Double) As Double
            Dim denominator = 0.1858 - y
            If Math.Abs(denominator) < 0.000001 Then Return Double.NaN
            Dim n = (x - 0.3320) / denominator
            Dim kelvin = 449.0 * n * n * n + 3525.0 * n * n + 6823.3 * n + 5520.33
            If Double.IsNaN(kelvin) OrElse Double.IsInfinity(kelvin) Then Return Double.NaN
            Return kelvin
        End Function

        ''' <summary>Abstand des Farborts von der Tageslichtlinie, in Einheiten von 1000 v
        ''' (CIE 1960). Positiv heißt oberhalb der Linie, also nach Magenta.
        '''
        ''' Die Tageslichtlinie ist die genormte CIE-D-Reihe. Unterhalb von 4000 K ist sie nicht
        ''' definiert; dort gibt es keinen Wert, statt eines erfundenen.</summary>
        Public Shared Function TintFromDaylightLocus(x As Double, y As Double, kelvin As Double) As Double
            If kelvin < DaylightLocusFloorKelvin OrElse kelvin > 25000.0 Then Return Double.NaN
            Dim locus = DaylightLocusPoint(kelvin)
            If Double.IsNaN(locus.X) Then Return Double.NaN
            Return (ToCie1960V(x, y) - ToCie1960V(locus.X, locus.Y)) * 1000.0
        End Function

        ''' <summary>Farbort der genormten Tageslichtbeleuchtung zu einer Temperatur (CIE D-Reihe).
        ''' Zwei Abschnitte für x, ein Polynom für y.</summary>
        Private Shared Function DaylightLocusPoint(kelvin As Double) As (X As Double, Y As Double)
            If kelvin < 4000.0 OrElse kelvin > 25000.0 Then Return (Double.NaN, Double.NaN)
            Dim t = kelvin
            Dim x As Double
            If t <= 7000.0 Then
                x = -4.6070E+09 / (t * t * t) + 2.9678E+06 / (t * t) + 99.11 / t + 0.244063
            Else
                x = -2.0064E+09 / (t * t * t) + 1.9018E+06 / (t * t) + 247.48 / t + 0.237040
            End If
            Dim y = -3.0 * x * x + 2.87 * x - 0.275
            Return (x, y)
        End Function

        ''' <summary>v der CIE-1960-Ebene. Nur dort ist ein Abstand quer zur Linie überhaupt
        ''' vergleichbar; in x/y wäre er von der Temperatur abhängig.</summary>
        Private Shared Function ToCie1960V(x As Double, y As Double) As Double
            Dim denominator = -2.0 * x + 12.0 * y + 3.0
            If Math.Abs(denominator) < 0.000001 Then Return Double.NaN
            Return 6.0 * y / denominator
        End Function

        Private Shared Function IsPositiveFinite(value As Single) As Boolean
            Return Single.IsFinite(value) AndAlso value > 0.0F
        End Function

        Private Shared Function IsPositiveFinite(value As Double) As Boolean
            Return Double.IsFinite(value) AndAlso value > 0.0
        End Function

    End Class

End Namespace
