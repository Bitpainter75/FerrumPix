Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports MetadataExtractor

Namespace Services

    ''' <summary>Die Objektivkorrektur, die die Kamera selbst in die RAW schreibt. Gebraucht nur
    ''' als Rueckfall, wenn die Sammlung kein Profil fuer das Objektiv fuehrt
    ''' (<see cref="LensDataService.FindCorrectionForFile"/>).
    '''
    ''' <para>Sony, Olympus/OM und Panasonic. Olympus siehe <see cref="ReadOlympus"/>, Panasonic
    ''' <see cref="ReadPanasonic"/>.</para>
    '''
    ''' <para>Sony: ARW-Dateien tragen im Feld 0x7037 die Verzeichnung und in 0x7035
    ''' den Farbquerfehler als Stuetzwerte. Der erste Wert ist die Zahl der Stuetzstellen
    ''' (Verzeichnung meist 11, Farbquerfehler 22 fuer Rot und Blau zusammen), danach folgen sie
    ''' gleichmaessig von der Bildmitte bis zur Ecke. Verzeichnung: Wert mal 2 hoch -14 plus 1 ist
    ''' das Verhaeltnis verzeichneter zu korrigiertem Radius. Farbquerfehler: Wert mal 2 hoch -21
    ''' plus 1 ist der Faktor des Kanals gegenueber Gruen.</para>
    '''
    ''' <para>Gegengeprueft an den Adobe-DNGs des Bestands, die dieselben Werte als
    ''' Verzerrungsformel tragen: beim Farbquerfehler ueber 172 Dateien im Median 0,07 Pixel
    ''' Abstand, bei der Verzeichnung (nur bei den Kompakten eingetragen) im Median 0,6 Pixel.
    ''' Die Vignettierung im Feld 0x7032 bleibt aussen vor, fuer sie gibt es keinen
    ''' verlaesslichen Vergleich.</para></summary>
    Public NotInheritable Class CameraLensCorrectionService

        Private Sub New()
        End Sub

        Private Const SonyChromaticAberrationTag As Integer = &H7035
        Private Const SonyDistortionTag As Integer = &H7037

        ''' <summary>Die Korrektur aus den Kameradaten, oder Nothing, wenn die Datei keine
        ''' brauchbaren traegt. Lauter Nullen heissen "nichts gemeldet", nicht "kein Fehler":
        ''' am Bestand stehen sie auch bei Brennweiten, an denen das Objektiv sichtbar verzeichnet.</summary>
        Public Shared Function TryCreate(directories As IEnumerable(Of Directory),
                                         width As Integer, height As Integer) As LensDataService.Korrektur
            If directories Is Nothing OrElse width < 2 OrElse height < 2 Then Return Nothing
            Dim curves = ReadSony(directories)
            If curves.Distortion Is Nothing AndAlso curves.Red Is Nothing Then curves = ReadOlympus(directories)
            If curves.Distortion Is Nothing AndAlso curves.Red Is Nothing Then curves = ReadPanasonic(directories, width, height)
            Dim distortionKnots = curves.Distortion
            Dim redKnots = curves.Red
            Dim blueKnots = curves.Blue
            If distortionKnots Is Nothing AndAlso redKnots Is Nothing Then Return Nothing

            ' Normiert wird wie bei einem Profil, das genau an diesem Bild gemessen wurde: das
            ' Seitenverhaeltnis ist das des Bildes, der Crop-Quotient 1. Dann ist der Radius der
            ' Kennlinien mal CornerScale der Radius mit 1 in der Ecke, also die Achse der Stuetzwerte.
            Dim aspect = CDbl(Math.Max(width, height) - 1) / Math.Max(1, Math.Min(width, height) - 1)
            Dim k As New LensDataService.Korrektur With {
                .Source = LensDataService.CorrectionSource.CameraData,
                .CalibrationAspectRatio = aspect,
                .CropRatio = 1.0,
                .CornerScale = 1.0 / Math.Sqrt(aspect * aspect + 1.0),
                .DistortionModel = If(distortionKnots IsNot Nothing, "knots", ""),
                .DistortionKnots = distortionKnots,
                .HasDistortion = distortionKnots IsNot Nothing,
                .TcaRedKnots = redKnots,
                .TcaBlueKnots = blueKnots,
                .HasChromaticAberration = redKnots IsNot Nothing
            }
            k.NormScale = LensDataService.NormScaleFor(k, width, height)
            Return k
        End Function

        ''' <summary>Sony: Stuetzwerte in 0x7037 (Verzeichnung) und 0x7035 (Farbquerfehler).</summary>
        Private Shared Function ReadSony(directories As IEnumerable(Of Directory)) As (Distortion As Double(), Red As Double(), Blue As Double())
            Dim distortion = ReadKnots(directories, SonyDistortionTag)
            Dim chromatic = ReadKnots(directories, SonyChromaticAberrationTag)

            Dim distortionKnots As Double() = Nothing
            If distortion IsNot Nothing AndAlso distortion.Length >= 2 AndAlso distortion.Any(Function(v) v <> 0) Then
                distortionKnots = distortion.Select(Function(v) v * Math.Pow(2, -14) + 1.0).ToArray()
            End If

            Dim redKnots As Double() = Nothing
            Dim blueKnots As Double() = Nothing
            If chromatic IsNot Nothing AndAlso chromatic.Length >= 4 AndAlso chromatic.Length Mod 2 = 0 AndAlso
               chromatic.Any(Function(v) v <> 0) Then
                Dim half = chromatic.Length \ 2
                redKnots = chromatic.Take(half).Select(Function(v) v * Math.Pow(2, -21) + 1.0).ToArray()
                blueKnots = chromatic.Skip(half).Select(Function(v) v * Math.Pow(2, -21) + 1.0).ToArray()
            End If
            Return (distortionKnots, redKnots, blueKnots)
        End Function

        Private Const OlympusDistortionTag As Integer = &H150A
        Private Const OlympusChromaticAberrationTag As Integer = &H150C

        ' Die Formeln werden an so vielen gleichmaessig verteilten Stellen ausgewertet; linear
        ' dazwischen bleibt der Fehler unter einem Zehntelpixel.
        Private Const FormulaKnotCount As Integer = 65

        ''' <summary>Olympus und OM: im Bildverarbeitungsteil des Herstellerteils stehen in 0x150A
        ''' vier Zahlen (k1, k2, k3, s) und in 0x150C sechs (r0, r1, r2, b0, b1, b2), beide als
        ''' Gleitkomma. Mit r dem Radius, 1 in der Ecke:
        ''' Verzeichnung s * (1 + k1 (s r)^2 + k2 (s r)^4 + k3 (s r)^6),
        ''' Rot 1 + r0 + r1 r^2 + r2 r^4, Blau entsprechend. Die Lesart ist gegen Adobes DNG
        ''' derselben Dateien gemessen: Farbquerfehler im Median 0,01 Pixel Abstand ueber 50 Dateien,
        ''' Verzeichnung im Median 1,6 Pixel, hoechstens 6, ueber 42; andere Lesarten des vierten
        ''' Wertes lagen deutlich weiter weg. (0, 0, 0, 1) heisst "nichts".</summary>
        Private Shared Function ReadOlympus(directories As IEnumerable(Of Directory)) As (Distortion As Double(), Red As Double(), Blue As Double())
            Dim processing = directories.OfType(Of MetadataExtractor.Formats.Exif.Makernotes.OlympusImageProcessingMakernoteDirectory)().FirstOrDefault()
            If processing Is Nothing Then Return (Nothing, Nothing, Nothing)

            Dim distortionKnots As Double() = Nothing
            Dim d = ToDoubles(If(processing.ContainsTag(OlympusDistortionTag), processing.GetObject(OlympusDistortionTag), Nothing))
            If d IsNot Nothing AndAlso d.Length >= 4 AndAlso d(3) > 0 AndAlso
               (d(0) <> 0 OrElse d(1) <> 0 OrElse d(2) <> 0 OrElse d(3) <> 1) Then
                distortionKnots = SampleFormula(Function(r)
                                                    Dim x = d(3) * r
                                                    Dim x2 = x * x
                                                    Return d(3) * (1.0 + d(0) * x2 + d(1) * x2 * x2 + d(2) * x2 * x2 * x2)
                                                End Function)
            End If

            Dim redKnots As Double() = Nothing
            Dim blueKnots As Double() = Nothing
            Dim c = ToDoubles(If(processing.ContainsTag(OlympusChromaticAberrationTag), processing.GetObject(OlympusChromaticAberrationTag), Nothing))
            If c IsNot Nothing AndAlso c.Length >= 6 AndAlso c.Take(6).Any(Function(v) v <> 0) Then
                redKnots = SampleFormula(Function(r) 1.0 + c(0) + c(1) * r * r + c(2) * r * r * r * r)
                blueKnots = SampleFormula(Function(r) 1.0 + c(3) + c(4) * r * r + c(5) * r * r * r * r)
            End If
            Return (distortionKnots, redKnots, blueKnots)
        End Function

        ''' <summary>Panasonic: das Feld DistortionInfo (0x0119) im RW2-Kopf, in MetadataExtractor
        ''' ein eigenes Verzeichnis. Werte durch 32768: a = Feld 8, b = Feld 4, c = Feld 11,
        ''' Massstab s = 1 / (1 + Feld 5 / 32768), Feld 7 (untere vier Bits) 1 heisst "an". Die
        ''' Gleichung rechnet vom verzeichneten Radius Rd zum korrigierten:
        ''' Ru = s (Rd + a Rd^3 + b Rd^5 + c Rd^7), beide Radien in Einheiten von Feld 12 (Pixel,
        ''' bei Bildern im eigenen Seitenverhaeltnis die halbe Diagonale). Gebraucht wird die
        ''' Gegenrichtung; sie wird hier an jeder Stuetzstelle gesucht, von der Mitte aus
        ''' aufsteigend, denn bei kraeftig verzeichnenden Kompakten ist das Polynom weiter aussen
        ''' nicht mehr monoton. Die Zuordnung der Felder ist aus allen 60 Moeglichkeiten gegen
        ''' Adobes DNG ermittelt: ueber 198 Dateien im Median 0,3 Pixel Abstand, 90 Prozent unter
        ''' 1,7.</summary>
        Private Shared Function ReadPanasonic(directories As IEnumerable(Of Directory),
                                              width As Integer, height As Integer) As (Distortion As Double(), Red As Double(), Blue As Double())
            Dim info = directories.OfType(Of MetadataExtractor.Formats.Exif.PanasonicRawDistortionDirectory)().FirstOrDefault()
            If info Is Nothing Then Return (Nothing, Nothing, Nothing)
            Dim switchValue, unit As Integer
            If Not info.TryGetInt32(7, switchValue) OrElse (switchValue And &HF) <> 1 Then Return (Nothing, Nothing, Nothing)
            If Not info.TryGetInt32(12, unit) OrElse unit <= 0 Then Return (Nothing, Nothing, Nothing)

            Dim a = PanasonicParameter(info, 8)
            Dim b = PanasonicParameter(info, 4)
            Dim c = PanasonicParameter(info, 11)
            Dim s = 1.0 / (1.0 + PanasonicParameter(info, 5))
            If a = 0 AndAlso b = 0 AndAlso c = 0 AndAlso s = 1.0 Then Return (Nothing, Nothing, Nothing)

            Dim forward = Function(rd As Double) s * (rd + a * rd ^ 3 + b * rd ^ 5 + c * rd ^ 7)
            Dim halfDiagonal = Math.Sqrt(CDbl(width) * width + CDbl(height) * height) / 2.0
            Dim knots = SampleFormula(Function(r)
                                          Dim ru = Math.Max(0.000001, r * halfDiagonal / unit)
                                          Dim rd = InvertIncreasing(forward, ru)
                                          Return If(Double.IsNaN(rd), Double.NaN, rd / ru)
                                      End Function)
            If knots.Any(Function(v) Double.IsNaN(v)) Then Return (Nothing, Nothing, Nothing)
            Return (knots, Nothing, Nothing)
        End Function

        Private Shared Function PanasonicParameter(info As Directory, tag As Integer) As Double
            Dim value As Integer
            Return If(info.TryGetInt32(tag, value), value / 32768.0, 0.0)
        End Function

        ''' <summary>Das kleinste x mit forward(x) = target, gesucht von 0 aus in kleinen Schritten
        ''' und dann halbiert. NaN, wenn bis 3 keines erreicht wird.</summary>
        Private Shared Function InvertIncreasing(forward As Func(Of Double, Double), target As Double) As Double
            Dim low = 0.0
            Dim x = 0.0
            Dim found = False
            While x < 3.0
                x += 0.002
                If forward(x) >= target Then
                    found = True
                    Exit While
                End If
                low = x
            End While
            If Not found Then Return Double.NaN

            Dim high = x
            For i = 1 To 50
                Dim middle = (low + high) / 2.0
                If forward(middle) < target Then low = middle Else high = middle
            Next
            Return (low + high) / 2.0
        End Function

        Private Shared Function SampleFormula(formula As Func(Of Double, Double)) As Double()
            Return Enumerable.Range(0, FormulaKnotCount).
                Select(Function(i) formula(i / CDbl(FormulaKnotCount - 1))).ToArray()
        End Function

        Private Shared Function ToDoubles(value As Object) As Double()
            Select Case True
                Case TypeOf value Is Single() : Return DirectCast(value, Single()).Select(Function(v) CDbl(v)).ToArray()
                Case TypeOf value Is Double() : Return DirectCast(value, Double())
                Case TypeOf value Is Rational() : Return DirectCast(value, Rational()).Select(Function(v) v.ToDouble()).ToArray()
                Case Else : Return Nothing
            End Select
        End Function

        ''' <summary>Die Stuetzwerte eines Feldes ohne die vorangestellte Anzahl; Nothing, wenn das
        ''' Feld fehlt oder die Anzahl nicht zur Laenge passt. Nur in den EXIF-Verzeichnissen:
        ''' dieselbe Feldnummer kann in einem Herstellerteil etwas anderes bedeuten.</summary>
        Private Shared Function ReadKnots(directories As IEnumerable(Of Directory), tag As Integer) As Integer()
            For Each directory In directories
                If Not TypeOf directory Is MetadataExtractor.Formats.Exif.ExifDirectoryBase Then Continue For
                If Not directory.ContainsTag(tag) Then Continue For
                Dim values = ToIntegers(directory.GetObject(tag))
                If values Is Nothing OrElse values.Length < 2 Then Continue For
                Dim count = values(0)
                If count < 2 OrElse count > values.Length - 1 Then Continue For
                Return values.Skip(1).Take(count).ToArray()
            Next
            Return Nothing
        End Function

        Private Shared Function ToIntegers(value As Object) As Integer()
            Select Case True
                Case TypeOf value Is Short() : Return DirectCast(value, Short()).Select(Function(v) CInt(v)).ToArray()
                ' Die Felder sind vorzeichenbehaftet; kommen sie ohne Vorzeichen an, zurueckfalten.
                Case TypeOf value Is UShort() : Return DirectCast(value, UShort()).Select(Function(v) If(v > 32767US, CInt(v) - 65536, CInt(v))).ToArray()
                Case TypeOf value Is Integer() : Return DirectCast(value, Integer())
                Case Else : Return Nothing
            End Select
        End Function
    End Class
End Namespace
