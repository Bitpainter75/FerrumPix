Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports MetadataExtractor

Namespace Services

    ''' <summary>Die Objektivkorrektur, die die Kamera selbst in die RAW schreibt. Gebraucht nur
    ''' als Rueckfall, wenn die Sammlung kein Profil fuer das Objektiv fuehrt
    ''' (<see cref="LensDataService.FindCorrectionForFile"/>).
    '''
    ''' <para>Sony, Olympus/OM, Panasonic, Fujifilm und Nikon Z. Olympus siehe <see cref="ReadOlympus"/>,
    ''' Panasonic <see cref="ReadPanasonic"/>, Fujifilm <see cref="ReadFujifilm"/>, Nikon
    ''' <see cref="ReadNikon"/>.</para>
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
                                         width As Integer, height As Integer,
                                         Optional path As String = Nothing) As LensDataService.Korrektur
            If directories Is Nothing OrElse width < 2 OrElse height < 2 Then Return Nothing
            Dim curves = ReadSony(directories)
            If curves.Distortion Is Nothing AndAlso curves.Red Is Nothing Then curves = ReadOlympus(directories)
            If curves.Distortion Is Nothing AndAlso curves.Red Is Nothing Then curves = ReadPanasonic(directories, width, height)
            If curves.Distortion Is Nothing AndAlso curves.Red Is Nothing Then curves = ReadFujifilm(path, width, height)
            Dim nikon = ReadNikon(directories)
            If curves.Distortion Is Nothing AndAlso curves.Red Is Nothing Then curves = (nikon.Distortion, nikon.Red, nikon.Blue)
            Dim distortionKnots = curves.Distortion
            Dim redKnots = curves.Red
            Dim blueKnots = curves.Blue
            ' Die Vignettierung kennen Fujifilm und Nikon; sie steht auch in Dateien, deren
            ' Verzeichnungstabelle leer ist.
            Dim vignettingKnots = If(ReadFujifilmVignetting(path, width, height), nikon.Vignetting)
            If distortionKnots Is Nothing AndAlso redKnots Is Nothing AndAlso vignettingKnots Is Nothing Then Return Nothing

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
                .HasChromaticAberration = redKnots IsNot Nothing,
                .VignettingKnots = vignettingKnots,
                .HasVignetting = vignettingKnots IsNot Nothing
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
            ' Der Farbquerfehler gilt auch ohne eingeschaltete Verzeichnungskorrektur der Kamera.
            Dim chromatic = ReadPanasonicChromatic(directories, width, height)
            Dim distortion = ReadPanasonicDistortion(directories, width, height)
            Return (distortion, chromatic.Red, chromatic.Blue)
        End Function

        Private Shared Function ReadPanasonicDistortion(directories As IEnumerable(Of Directory),
                                                        width As Integer, height As Integer) As Double()
            Dim info = directories.OfType(Of MetadataExtractor.Formats.Exif.PanasonicRawDistortionDirectory)().FirstOrDefault()
            If info Is Nothing Then Return Nothing
            Dim switchValue, unit As Integer
            If Not info.TryGetInt32(7, switchValue) OrElse (switchValue And &HF) <> 1 Then Return Nothing
            If Not info.TryGetInt32(12, unit) OrElse unit <= 0 Then Return Nothing

            Dim a = PanasonicParameter(info, 8)
            Dim b = PanasonicParameter(info, 4)
            Dim c = PanasonicParameter(info, 11)
            Dim s = 1.0 / (1.0 + PanasonicParameter(info, 5))
            If a = 0 AndAlso b = 0 AndAlso c = 0 AndAlso s = 1.0 Then Return Nothing

            Dim forward = Function(rd As Double) s * (rd + a * rd ^ 3 + b * rd ^ 5 + c * rd ^ 7)
            Dim halfDiagonal = Math.Sqrt(CDbl(width) * width + CDbl(height) * height) / 2.0
            Dim knots = SampleFormula(Function(r)
                                          Dim ru = Math.Max(0.000001, r * halfDiagonal / unit)
                                          Dim rd = InvertIncreasing(forward, ru)
                                          Return If(Double.IsNaN(rd), Double.NaN, rd / ru)
                                      End Function)
            If knots.Any(Function(v) Double.IsNaN(v)) Then Return Nothing
            Return knots
        End Function

        Private Const PanasonicChromaticAberrationTag As Integer = &H11B

        ''' <summary>Panasonic: der Farbquerfehler im Feld 0x011B des RW2-Kopfes, 32 Werte mit
        ''' Vorzeichen. Wert 14 ist 256, wenn die
        ''' Tabelle Daten traegt. Die Werte 17, 16, 4 und 11 sind vier Quellradien in Pixeln, der
        ''' letzte etwa die halbe Diagonale; 12, 23, 8 und 26 (Rot) sowie 27, 10, 20 und 29 (Blau)
        ''' die Verschiebung des Kanals gegen Gruen an diesen Radien in minus Tausendstel Pixel.
        '''
        ''' Das Referenzprogramm zieht keine Geraden zwischen den Werten, sondern legt ein glattes
        ''' Polynom in der Form seiner Verzerrungsformel hindurch: 1 + c0 + c1 s^2 + c2 s^4 + c3 s^6
        ''' mit s dem Quellradius in halben Diagonalen, kleinste Quadrate mit Gewicht s bis zur Ecke.
        ''' So gerechnet liegt es an 309 Fotos aus 111 Gehaeusen hoechstens 0,15 Pixel neben dessen
        ''' DNG, die Geraden selbst bis 0,86. Der Faktor gilt am Quellradius von Gruen,
        ''' genau dort, wo der Decode ihn anwendet.</summary>
        Private Shared Function ReadPanasonicChromatic(directories As IEnumerable(Of Directory),
                                                       width As Integer, height As Integer) As (Red As Double(), Blue As Double())
            Dim none As (Red As Double(), Blue As Double()) = (Nothing, Nothing)
            Dim bytes As Byte() = Nothing
            For Each directory In directories.OfType(Of MetadataExtractor.Formats.Exif.PanasonicRawIfd0Directory)()
                If directory.ContainsTag(PanasonicChromaticAberrationTag) Then bytes = TryCast(directory.GetObject(PanasonicChromaticAberrationTag), Byte())
            Next
            If bytes Is Nothing OrElse bytes.Length < 64 Then Return none
            ' RW2 ist immer klein-endian ("IIU").
            Dim v(31) As Double
            For i = 0 To 31
                v(i) = BitConverter.ToInt16(bytes, i * 2)
            Next
            Dim knots = {v(17), v(16), v(4), v(11)}
            If v(14) <> 256 OrElse knots(0) <= 0 Then Return none
            For i = 1 To 3
                If knots(i) <= knots(i - 1) Then Return none
            Next
            Dim red = {-v(12) / 1000.0, -v(23) / 1000.0, -v(8) / 1000.0, -v(26) / 1000.0}
            Dim blue = {-v(27) / 1000.0, -v(10) / 1000.0, -v(20) / 1000.0, -v(29) / 1000.0}
            If red.All(Function(d) d = 0) AndAlso blue.All(Function(d) d = 0) Then Return none
            Dim halfDiagonal = Math.Sqrt(CDbl(width) * width + CDbl(height) * height) / 2.0
            Dim redFactor = PanasonicChromaticFit(knots, red, halfDiagonal)
            Dim blueFactor = PanasonicChromaticFit(knots, blue, halfDiagonal)
            If redFactor Is Nothing OrElse blueFactor Is Nothing Then Return none
            Return (SampleFormula(redFactor), SampleFormula(blueFactor))
        End Function

        ''' <summary>Das Polynom durch die Verschiebungen (siehe oben) als Faktor des Kanals ueber
        ''' dem Quellradius in halben Diagonalen; Nothing, wenn das Gleichungssystem entartet.</summary>
        Private Shared Function PanasonicChromaticFit(knots As Double(), shifts As Double(), halfDiagonal As Double) As Func(Of Double, Double)
            If halfDiagonal <= 0 Then Return Nothing
            ' Verschiebung in Pixeln an einem Quellradius: Geraden zwischen den Stuetzstellen, null
            ' in der Mitte, hinter der letzten gehalten.
            Dim lines = Function(g As Double) As Double
                            Dim k0 = 0.0, d0 = 0.0
                            For i = 0 To 3
                                If g < knots(i) Then Return d0 + (shifts(i) - d0) * (g - k0) / (knots(i) - k0)
                                k0 = knots(i) : d0 = shifts(i)
                            Next
                            Return d0
                        End Function
            Dim normal(3, 4) As Double
            For i = 1 To 256
                Dim s = i / 256.0
                Dim s2 = s * s
                Dim row = {s, s * s2, s * s2 * s2, s * s2 * s2 * s2}
                Dim y = lines(s * halfDiagonal) / halfDiagonal
                For j = 0 To 3
                    For k = 0 To 3
                        normal(j, k) += row(j) * row(k)
                    Next
                    normal(j, 4) += row(j) * y
                Next
            Next
            Dim c = Solve4(normal)
            If c Is Nothing Then Return Nothing
            Return Function(s As Double)
                       Dim s2 = s * s
                       Return 1.0 + c(0) + s2 * (c(1) + s2 * (c(2) + s2 * c(3)))
                   End Function
        End Function

        ''' <summary>Gauss mit Spaltenpivot fuer ein 4x4-System in erweiterter Form; Nothing, wenn
        ''' es singulaer ist.</summary>
        Private Shared Function Solve4(m As Double(,)) As Double()
            For col = 0 To 3
                Dim pivot = col
                For row = col + 1 To 3
                    If Math.Abs(m(row, col)) > Math.Abs(m(pivot, col)) Then pivot = row
                Next
                If Math.Abs(m(pivot, col)) < 1.0E-300 Then Return Nothing
                If pivot <> col Then
                    For k = 0 To 4
                        Dim t = m(col, k) : m(col, k) = m(pivot, k) : m(pivot, k) = t
                    Next
                End If
                For row = col + 1 To 3
                    Dim f = m(row, col) / m(col, col)
                    For k = col To 4
                        m(row, k) -= f * m(col, k)
                    Next
                Next
            Next
            Dim x(3) As Double
            For row = 3 To 0 Step -1
                Dim rest = 0.0
                For k = row + 1 To 3
                    rest += m(row, k) * x(k)
                Next
                x(row) = (m(row, 4) - rest) / m(row, row)
            Next
            Return If(x.All(Function(value) Double.IsFinite(value)), x, Nothing)
        End Function

        Private Const NikonLensInfoTag As Integer = &HC7D5

        ''' <summary>Nikon Z: die Objektivkorrektur der Kamera in Feld 0xC7D5 des RAW-Unterverzeichnisses
        ''' (ExifTools NikonNEFInfo), ein kleines TIFF hinter "Nikon\0" und vier Versionsbytes
        ''' Die Eintraege 5 bis 7 sind Saetze, die mit "0100" beginnen,
        ''' Werte als Brueche mit Vorzeichen, Polynome im Radius q mit den Beiwerten fuer absteigende
        ''' Potenzen bis q^1, q = 1 in der Ecke des Standardausschnitts:
        ''' Verzeichnung (Satz 5): Byte 4 ist der Modus, ohne Bit 0 (Verzeichnungskontrolle der Kamera
        ''' aus) gibt es nur den Farbquerfehler, wie beim Referenzprogramm. Bei 0x10 die Anzahl 4 und die Beiwerte,
        ''' Quell- durch Ausgaberadius von Gruen ist (1 + k(q)) / (1 + x), x bei 0x40.
        ''' Vignettierung (Satz 6): Anzahl 8 bei 0x10, Verstaerkung 1 + a v(q), a bei 0x60.
        ''' Farbquerfehler (Satz 7): Rot bei 0x10, Blau bei 0x34, je Anzahl 3, die Beiwerte und eine
        ''' Konstante e; der Faktor gegen Gruen am AUSGABEradius ist 1 + e + c(q). Der Decode wendet
        ''' ihn am Quellradius an, deshalb wird er dorthin umgerechnet.
        '''
        ''' Die optische Mitte (Satz 8, bis 27 Pixel neben der Bildmitte, mit einem Fremdobjektiv 66)
        ''' wird NICHT gelesen: die Korrektur sitzt auf der Bildmitte, das liegt so bis 2,1 Pixel neben
        ''' dem Referenzprogramm.</summary>
        Private Shared Function ReadNikon(directories As IEnumerable(Of Directory)) As (Distortion As Double(), Red As Double(), Blue As Double(), Vignetting As Double())
            Dim none As (Distortion As Double(), Red As Double(), Blue As Double(), Vignetting As Double()) = (Nothing, Nothing, Nothing, Nothing)
            Dim info As Byte() = Nothing
            For Each directory In directories.OfType(Of MetadataExtractor.Formats.Exif.ExifSubIfdDirectory)()
                If directory.ContainsTag(NikonLensInfoTag) Then
                    info = TryCast(directory.GetObject(NikonLensInfoTag), Byte())
                    If info IsNot Nothing Then Exit For
                End If
            Next
            If info Is Nothing OrElse info.Length < 18 OrElse Text.Encoding.ASCII.GetString(info, 0, 6) <> "Nikon" & ChrW(0) Then Return none
            Const TiffStart = 10
            Dim little As Boolean
            If info(TiffStart) = &H49 AndAlso info(TiffStart + 1) = &H49 Then
                little = True
            ElseIf info(TiffStart) = &H4D AndAlso info(TiffStart + 1) = &H4D Then
                little = False
            Else
                Return none
            End If
            Dim u16 = Function(o As Integer) As Integer
                          If o < 0 OrElse o + 2 > info.Length Then Return -1
                          Return If(little, (CInt(info(o + 1)) << 8) Or info(o), (CInt(info(o)) << 8) Or info(o + 1))
                      End Function
            Dim u32 = Function(b As Byte(), o As Integer) As Long
                          If o < 0 OrElse o + 4 > b.Length Then Return -1
                          Return If(little,
                                    (CLng(b(o + 3)) << 24) Or (CLng(b(o + 2)) << 16) Or (CLng(b(o + 1)) << 8) Or b(o),
                                    (CLng(b(o)) << 24) Or (CLng(b(o + 1)) << 16) Or (CLng(b(o + 2)) << 8) Or b(o + 3))
                      End Function
            Dim record = Function(tag As Integer, minimum As Integer) As Byte()
                             Dim ifd = u32(info, TiffStart + 4)
                             If ifd < 0 Then Return Nothing
                             Dim count = u16(TiffStart + CInt(ifd))
                             If count <= 0 OrElse count > 200 Then Return Nothing
                             For i = 0 To count - 1
                                 Dim e = TiffStart + CInt(ifd) + 2 + i * 12
                                 If u16(e) <> tag Then Continue For
                                 Dim length = u32(info, e + 4)
                                 Dim offset = If(length <= 4, e + 8, TiffStart + u32(info, e + 8))
                                 If length < minimum OrElse offset < 0 OrElse offset + length > info.Length Then Return Nothing
                                 Dim bytes(CInt(length) - 1) As Byte
                                 Array.Copy(info, offset, bytes, 0, length)
                                 Return If(Text.Encoding.ASCII.GetString(bytes, 0, 4) = "0100", bytes, Nothing)
                             Next
                             Return Nothing
                         End Function
            Dim rational = Function(b As Byte(), o As Integer) As Double
                               Dim numerator = u32(b, o), denominator = u32(b, o + 4)
                               If numerator < 0 OrElse denominator < 0 Then Return Double.NaN
                               If numerator >= 2147483648L Then numerator -= 4294967296L
                               If denominator >= 2147483648L Then denominator -= 4294967296L
                               Return If(denominator = 0, Double.NaN, numerator / CDbl(denominator))
                           End Function
            Dim coefficients = Function(b As Byte(), o As Integer, n As Integer) As Double()
                                   If u32(b, o) <> n Then Return Nothing
                                   Dim c(n - 1) As Double
                                   For i = 0 To n - 1
                                       c(i) = rational(b, o + 4 + 8 * i)
                                       If Not Double.IsFinite(c(i)) Then Return Nothing
                                   Next
                                   Return c
                               End Function
            ' Beiwerte fuer absteigende Potenzen, der letzte fuer q^1.
            Dim descending = Function(c As Double(), q As Double) As Double
                                 Dim sum = 0.0
                                 For Each k In c
                                     sum = (sum + k) * q
                                 Next
                                 Return sum
                             End Function

            ' Ohne Bit 0 des Modus (Verzeichnungskontrolle der Kamera aus, Z 8, Z6 III, Z f, Z5 II im
            ' Bestand) traegt die DNG des Referenzprogramms nur den Farbquerfehler: Gruen bleibt,
            ' keine Vignette. Der Farbquerfehler wird dann trotzdem gelesen.
            Dim distortionRecord = record(5, &H48)
            If distortionRecord Is Nothing Then Return none
            Dim cameraCorrects = (distortionRecord(4) And 1) <> 0
            Dim ratio As Func(Of Double, Double) = Function(q As Double) 1.0
            Dim distortion As Double() = Nothing
            If cameraCorrects Then
                Dim k4 = coefficients(distortionRecord, &H10, 4)
                Dim fill = rational(distortionRecord, &H40)
                If k4 Is Nothing OrElse Not Double.IsFinite(fill) OrElse fill <= -1 Then Return none
                Dim scale = 1.0 / (1.0 + fill)
                ratio = Function(q As Double) scale * (1.0 + descending(k4, q))
                distortion = SampleFormula(ratio)
                If distortion.All(Function(v) Math.Abs(v - 1.0) <= 0.000001) Then distortion = Nothing
            End If

            Dim vignetting As Double() = Nothing
            Dim vignettingRecord = If(cameraCorrects, record(6, &H68), Nothing)
            If vignettingRecord IsNot Nothing Then
                Dim c8 = coefficients(vignettingRecord, &H10, 8)
                Dim strength = rational(vignettingRecord, &H60)
                If c8 IsNot Nothing AndAlso Double.IsFinite(strength) Then
                    ' Gespeichert wird die Verstaerkung; die Kennlinie hier ist das verbleibende Licht.
                    Dim light = SampleFormula(Function(q) 1.0 / Math.Max(0.05, 1.0 + strength * descending(c8, q)))
                    If light.Any(Function(v) Math.Abs(v - 1.0) > 0.0001) Then vignetting = light
                End If
            End If

            Dim red As Double() = Nothing, blue As Double() = Nothing
            Dim chromaticRecord = record(7, &H58)
            If chromaticRecord IsNot Nothing Then
                Dim plane = Function(o As Integer) As Func(Of Double, Double)
                                Dim c3 = coefficients(chromaticRecord, o, 3)
                                Dim e = rational(chromaticRecord, o + 28)
                                If c3 Is Nothing OrElse Not Double.IsFinite(e) Then Return Nothing
                                Return Function(q) 1.0 + e + descending(c3, q)
                            End Function
                Dim redAtOutput = plane(&H10)
                Dim blueAtOutput = plane(&H34)
                If redAtOutput IsNot Nothing AndAlso blueAtOutput IsNot Nothing Then
                    red = OnSourceRadius(redAtOutput, ratio)
                    blue = OnSourceRadius(blueAtOutput, ratio)
                    If red IsNot Nothing AndAlso blue IsNot Nothing AndAlso
                       red.Concat(blue).All(Function(v) Math.Abs(v - 1.0) <= 0.0000001) Then
                        red = Nothing : blue = Nothing
                    End If
                End If
            End If
            If distortion Is Nothing AndAlso red Is Nothing AndAlso vignetting Is Nothing Then Return none
            Return (distortion, red, blue, vignetting)
        End Function

        ''' <summary>Ein Faktor, der am AUSGABEradius angegeben ist, als Stuetzwerte am Quellradius von
        ''' Gruen, wo der Decode den Farbquerfehler anwendet. <paramref name="ratio"/> ist Quell- durch
        ''' Ausgaberadius von Gruen; Nothing, wenn der Quellradius nicht mit dem Ausgaberadius waechst.</summary>
        Private Shared Function OnSourceRadius(atOutput As Func(Of Double, Double), ratio As Func(Of Double, Double)) As Double()
            Const Steps = 512
            Dim sources(Steps) As Double, values(Steps) As Double
            For i = 0 To Steps
                Dim q = 1.2 * i / Steps
                sources(i) = q * ratio(q)
                values(i) = atOutput(q)
                If i > 0 AndAlso sources(i) <= sources(i - 1) Then Return Nothing
            Next
            Return SampleFormula(Function(rho)
                                     For i = 1 To Steps
                                         If rho <= sources(i) Then
                                             Dim t = (rho - sources(i - 1)) / (sources(i) - sources(i - 1))
                                             Return values(i - 1) + t * (values(i) - values(i - 1))
                                         End If
                                     Next
                                     Return values(Steps)
                                 End Function)
        End Function

        Private Const FujifilmLensDirectoryTag As Integer = &HF000
        Private Const FujifilmDistortionTag As Integer = &HF00B

        ''' <summary>Fujifilm (RAF): die Verzeichnung steht nicht im EXIF, sondern im TIFF des
        ''' RAW-Teils (Versatz in Byte 100 des RAF-Kopfes), Unterverzeichnis 0xF000, Feld 0xF00B, als
        ''' vorzeichenbehaftete Brueche: ein Kopfwert, n Stuetzstellen, n Werte in Prozent.
        '''
        ''' DIE STUETZSTELLEN SIND RADIEN IN HALBEN DIAGONALEN, mit 1 in der Ecke. Aeltere Gehaeuse
        ''' haben elf (0 bis 1), die GFX 50 acht (bis 1), die neueren neun, gleichmaessig in r^2
        ''' (Wurzel aus k/8): dort liegt die ACHTE in der Ecke und die neunte (1,0607) dahinter. Die
        ''' erste Lesart nahm die letzte Stuetzstelle als Ecke und las bei neun jeden Wert 6 % zu weit
        ''' aussen. Gegen Adobes DNG derselben Dateien, Form der Kurve (beide auf die Mitte bezogen,
        ''' Adobe vergroessert selbst), groesster Abstand bis zur Ecke in Prozent des Radius: an zehn
        ''' Dateien mit neun Stuetzstellen 0,68 % mit der alten, 0,26 % mit dieser Lesart; ueber alle 53
        ''' vergleichbaren Dateien im Median 0,27 %. Ausreisser sind Kompakte (XQ1, XQ2 3,6 %, X20,
        ''' X30, X-S1 gut 1 %), fuer die Adobe vermutlich ein eigenes Profil nimmt.
        '''
        ''' Die Kamera tastet die Rohdaten an source = r (1 + D(source) / 100) ab, die Tabelle gilt also
        ''' am VERZEICHNETEN Radius; geloest wird je Stuetzwert durch Einsetzen. Die Vergroesserung
        ''' der Kamera, die den Rahmen fuellt, bleibt weg: die macht die Korrektur selbst.
        '''
        ''' Kopfwert mal Zahl der Stuetzstellen ist die halbe Diagonale des Bildes in Pixeln. Passt sie
        ''' nicht zum Bild (X-S20, X-T30 III und X-M5 schreiben den Wert des 40-MP-Sensors, eine X-T5
        ''' im Kamera-Ausschnitt einen anderen), bleibt die Datei unkorrigiert: an diesen Dateien lag
        ''' die Kurve gegen Adobe bis 300 Pixel daneben. Der Farbquerfehler (0xF00F) steht in
        ''' <see cref="ReadFujifilmChromatic"/>, die Vignettierung (0xF010) in
        ''' <see cref="ReadFujifilmVignetting"/>.</summary>
        Private Shared Function ReadFujifilm(path As String, width As Integer, height As Integer) As (Distortion As Double(), Red As Double(), Blue As Double())
            Dim none As (Distortion As Double(), Red As Double(), Blue As Double()) = (Nothing, Nothing, Nothing)
            If String.IsNullOrEmpty(path) OrElse Not path.EndsWith(".raf", StringComparison.OrdinalIgnoreCase) Then Return none
            Dim distortion = ReadFujifilmDistortion(path, width, height)
            Dim chromatic = ReadFujifilmChromatic(path, width, height, distortion)
            Return (distortion, chromatic.Red, chromatic.Blue)
        End Function

        Private Shared Function ReadFujifilmDistortion(path As String, width As Integer, height As Integer) As Double()
            Dim table = ReadFujifilmTable(path, FujifilmDistortionTag)
            If table Is Nothing OrElse table.Length < 5 OrElse (table.Length - 1) Mod 2 <> 0 Then Return Nothing
            Dim n = (table.Length - 1) \ 2
            Dim positions = table.Skip(1).Take(n).ToArray()
            Dim values = table.Skip(1 + n).Take(n).ToArray()
            If values.All(Function(v) v = 0) Then Return Nothing
            For i = 1 To n - 1
                If positions(i) <= positions(i - 1) Then Return Nothing
            Next
            Dim halfDiagonal = Math.Sqrt(CDbl(width) * width + CDbl(height) * height) / 2.0
            If Math.Abs(table(0) * n - halfDiagonal) > 0.02 * halfDiagonal Then Return Nothing
            Return FujifilmDistortionKnots(positions, values)
        End Function

        Private Const FujifilmChromaticAberrationTag As Integer = &HF00F

        ''' <summary>Fujifilm: der Farbquerfehler in Feld 0xF00F, gebaut wie die Verzeichnung, nur mit
        ''' zwei Wertereihen: ein Kopfwert, n Stuetzstellen in halben Diagonalen, n Werte fuer Rot,
        ''' n fuer Blau. 1 plus Wert ist der Faktor des Kanals gegen Gruen am AUSGABEradius; der Decode wendet ihn am Quellradius an, deshalb wird er mit der
        ''' Verzeichnung der Datei dorthin umgerechnet. Ohne Verzeichnung sind beide Radien gleich.
        ''' Wie dort muss Kopfwert mal n die halbe Diagonale treffen.</summary>
        Private Shared Function ReadFujifilmChromatic(path As String, width As Integer, height As Integer,
                                                      distortion As Double()) As (Red As Double(), Blue As Double())
            Dim none As (Red As Double(), Blue As Double()) = (Nothing, Nothing)
            Dim table = ReadFujifilmTable(path, FujifilmChromaticAberrationTag)
            ' Die Gehaeuse mit neun Stuetzstellen wiederholen den Kopfwert am Ende (29 statt 28 Werte,
            ' gesehen an GFX 100 II, X-T5 und weiteren); ohne diesen Schluss fielen sie alle heraus.
            If table IsNot Nothing AndAlso table.Length >= 8 AndAlso (table.Length - 2) Mod 3 = 0 AndAlso
               Math.Abs(table(table.Length - 1) - table(0)) <= 0.000001 * Math.Abs(table(0)) Then
                table = table.Take(table.Length - 1).ToArray()
            End If
            If table Is Nothing OrElse table.Length < 7 OrElse (table.Length - 1) Mod 3 <> 0 Then Return none
            Dim n = (table.Length - 1) \ 3
            Dim positions = table.Skip(1).Take(n).ToList()
            Dim red = table.Skip(1 + n).Take(n).ToList()
            Dim blue = table.Skip(1 + 2 * n).Take(n).ToList()
            If red.Concat(blue).All(Function(v) Math.Abs(v) <= 0.0000001) Then Return none
            For i = 1 To n - 1
                If positions(i) <= positions(i - 1) Then Return none
            Next
            Dim halfDiagonal = Math.Sqrt(CDbl(width) * width + CDbl(height) * height) / 2.0
            If Math.Abs(table(0) * n - halfDiagonal) > 0.02 * halfDiagonal Then Return none
            ' Liegt die erste Stuetzstelle ausserhalb der Mitte, gilt dort der erste Wert.
            If positions(0) > 0 Then
                positions.Insert(0, 0.0)
                red.Insert(0, red(0))
                blue.Insert(0, blue(0))
            End If
            Dim at = Function(values As List(Of Double), r As Double) As Double
                         For i = 1 To positions.Count - 1
                             If r <= positions(i) Then
                                 Dim t = (r - positions(i - 1)) / (positions(i) - positions(i - 1))
                                 Return values(i - 1) + t * (values(i) - values(i - 1))
                             End If
                         Next
                         Return values(values.Count - 1)
                     End Function
            Dim ratio = Function(r As Double) If(distortion Is Nothing, 1.0, LensDataService.InterpolateKnots(distortion, r))
            Dim redKnots = OnSourceRadius(Function(r) 1.0 + at(red, r), ratio)
            Dim blueKnots = OnSourceRadius(Function(r) 1.0 + at(blue, r), ratio)
            If redKnots Is Nothing OrElse blueKnots Is Nothing Then Return none
            Return (redKnots, blueKnots)
        End Function

        Private Const FujifilmVignettingTag As Integer = &HF010

        ''' <summary>Fujifilm: die Vignettierung in Feld 0xF010, gebaut wie die Verzeichnung (Kopfwert,
        ''' n Stuetzstellen in halben Diagonalen, n Werte). Die Werte sind der VERBLEIBENDE Anteil
        ''' des Lichts in Prozent, 100 in der Mitte; also genau der Abfall, durch den die Korrektur
        ''' teilt. Zurueck kommen Stuetzwerte von der Mitte bis zur Ecke (1 = kein Abfall).
        '''
        ''' Gemessen gegen die Vignettierung, die der DNG-Konverter derselben Datei in OpcodeList2
        ''' schreibt (FixVignetteRadial, Diagnostics/Fujimessung/vignette.py): er uebernimmt die
        ''' Tabelle unveraendert. Der beste Exponent auf den Gewinn lag an 54 Dateien im Median bei
        ''' 1,01 (0,97 bis 1,04); ein abgeschwaechter Gewinn hoch 0,85 laege bis 0,21 Blenden
        ''' daneben. Zur Mitte siehe <see cref="FujifilmVignettingKnots"/>.
        '''
        ''' Wie bei der Verzeichnung muss Kopfwert mal n die halbe Diagonale treffen, sonst bleibt
        ''' die Datei ohne.</summary>
        Private Shared Function ReadFujifilmVignetting(path As String, width As Integer, height As Integer) As Double()
            If String.IsNullOrEmpty(path) OrElse Not path.EndsWith(".raf", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Dim table = ReadFujifilmTable(path, FujifilmVignettingTag)
            If table Is Nothing OrElse table.Length < 5 OrElse (table.Length - 1) Mod 2 <> 0 Then Return Nothing
            Dim n = (table.Length - 1) \ 2
            Dim positions = table.Skip(1).Take(n).ToArray()
            Dim values = table.Skip(1 + n).Take(n).ToArray()
            ' Lauter 100 heisst "kein Abfall", nicht gemessen; Werte ausserhalb sind unbrauchbar.
            If values.All(Function(v) v = 100) OrElse values.Any(Function(v) v <= 0 OrElse v > 200) Then Return Nothing
            For i = 1 To n - 1
                If positions(i) <= positions(i - 1) Then Return Nothing
            Next
            Dim halfDiagonal = Math.Sqrt(CDbl(width) * width + CDbl(height) * height) / 2.0
            If Math.Abs(table(0) * n - halfDiagonal) > 0.02 * halfDiagonal Then Return Nothing
            Return FujifilmVignettingKnots(positions, values)
        End Function

        ''' <summary>Aus Stuetzstellen (Radien in halben Diagonalen) und Prozentwerten des
        ''' verbleibenden Lichts die Stuetzwerte der Vignettierung, gleichmaessig von der Mitte bis
        ''' zur Ecke; dazwischen linear.
        '''
        ''' IN DER MITTE GILT 100 %. Die neueren Gehaeuse mit neun Stuetzstellen beginnen erst bei
        ''' Wurzel(1/8) = 0,354 und tragen dort schon etwa 98,5. Wie bei der Verzeichnung (dort 0 %)
        ''' wird die Mitte eingesetzt und bis zur ersten Stuetzstelle linear verbunden. Gegen den
        ''' DNG-Konverter: groesster Fehler bis zur Ecke im Median 0,011, 90 % unter 0,026, hoechstens
        ''' 0,045 Blenden. Den Wert der ersten Stuetzstelle bis zur Mitte durchzuziehen hellte das
        ''' ganze Bild um gut 1 % auf (90 % unter 0,044 Blenden), ihn als Mitte zu nehmen verschob
        ''' die Kurve ebenso weit.</summary>
        Friend Shared Function FujifilmVignettingKnots(positions As Double(), values As Double()) As Double()
            Dim knots = positions.ToList()
            Dim percent = values.ToList()
            If knots(0) > 0 Then
                knots.Insert(0, 0.0)
                percent.Insert(0, 100.0)
            End If
            Return SampleFormula(Function(r)
                                     If r <= knots(0) Then Return percent(0) / 100.0
                                     For i = 1 To knots.Count - 1
                                         If r <= knots(i) Then
                                             Dim t = (r - knots(i - 1)) / (knots(i) - knots(i - 1))
                                             Return (percent(i - 1) + t * (percent(i) - percent(i - 1))) / 100.0
                                         End If
                                     Next
                                     Return percent(knots.Count - 1) / 100.0
                                 End Function)
        End Function

        ''' <summary>Aus Stuetzstellen (Radien in halben Diagonalen) und Prozentwerten die
        ''' Stuetzwerte dieses Dienstes: verzeichneter durch korrigierten Radius an gleichmaessig
        ''' verteilten korrigierten Radien von der Mitte bis zur Ecke.</summary>
        Friend Shared Function FujifilmDistortionKnots(positions As Double(), values As Double()) As Double()
            ' Liegt die erste Stuetzstelle ausserhalb der Mitte, gilt dort 0 %.
            Dim knots = positions.ToList()
            Dim percent = values.ToList()
            If knots(0) > 0 Then
                knots.Insert(0, 0.0)
                percent.Insert(0, 0.0)
            End If
            Dim at = Function(x As Double) As Double
                         If x <= knots(0) Then Return percent(0)
                         For i = 1 To knots.Count - 1
                             If x <= knots(i) Then
                                 Dim t = (x - knots(i - 1)) / (knots(i) - knots(i - 1))
                                 Return percent(i - 1) + t * (percent(i) - percent(i - 1))
                             End If
                         Next
                         Return percent(knots.Count - 1)
                     End Function
            Return SampleFormula(Function(r)
                                     If r <= 0 Then Return 1.0 + at(0.0) / 100.0
                                     Dim source = r
                                     For iteration = 1 To 40
                                         source = r * (1.0 + at(source) / 100.0)
                                     Next
                                     Return source / r
                                 End Function)
        End Function

        ''' <summary>Ein Feld aus dem Unterverzeichnis 0xF000 des RAW-Teils einer RAF, als Zahlen;
        ''' Nothing, wenn es fehlt oder die Datei anders gebaut ist.</summary>
        Private Shared Function ReadFujifilmTable(path As String, tag As Integer) As Double()
            Try
                Using stream = New IO.FileStream(path, IO.FileMode.Open, IO.FileAccess.Read, IO.FileShare.Read)
                    Dim head = ReadExactly(stream, 0, 104)
                    If head Is Nothing OrElse Text.Encoding.ASCII.GetString(head, 0, 16) <> "FUJIFILMCCD-RAW " Then Return Nothing
                    Dim baseOffset = (CLng(head(100)) << 24) Or (CLng(head(101)) << 16) Or (CLng(head(102)) << 8) Or CLng(head(103))
                    Dim tiffHead = ReadExactly(stream, baseOffset, 8)
                    If tiffHead Is Nothing Then Return Nothing
                    Dim little As Boolean
                    If tiffHead(0) = &H49 AndAlso tiffHead(1) = &H49 Then
                        little = True
                    ElseIf tiffHead(0) = &H4D AndAlso tiffHead(1) = &H4D Then
                        little = False
                    Else
                        Return Nothing
                    End If
                    Dim u16 = Function(b As Byte(), o As Integer) As Integer
                                  Return If(little, (CInt(b(o + 1)) << 8) Or CInt(b(o)), (CInt(b(o)) << 8) Or CInt(b(o + 1)))
                              End Function
                    Dim u32 = Function(b As Byte(), o As Integer) As Long
                                  Return If(little,
                                            (CLng(b(o + 3)) << 24) Or (CLng(b(o + 2)) << 16) Or (CLng(b(o + 1)) << 8) Or CLng(b(o)),
                                            (CLng(b(o)) << 24) Or (CLng(b(o + 1)) << 16) Or (CLng(b(o + 2)) << 8) Or CLng(b(o + 3)))
                              End Function
                    Dim findEntry = Function(ifdOffset As Long, wanted As Integer) As (Type As Integer, Count As Long, Value As Long)?
                                        Dim countBytes = ReadExactly(stream, baseOffset + ifdOffset, 2)
                                        If countBytes Is Nothing Then Return Nothing
                                        Dim count = u16(countBytes, 0)
                                        If count <= 0 OrElse count > 1000 Then Return Nothing
                                        Dim block = ReadExactly(stream, baseOffset + ifdOffset + 2, count * 12)
                                        If block Is Nothing Then Return Nothing
                                        For i = 0 To count - 1
                                            If u16(block, i * 12) = wanted Then
                                                Return (u16(block, i * 12 + 2), u32(block, i * 12 + 4), u32(block, i * 12 + 8))
                                            End If
                                        Next
                                        Return Nothing
                                    End Function
                    Dim directoryEntry = findEntry(u32(tiffHead, 4), FujifilmLensDirectoryTag)
                    If directoryEntry Is Nothing Then Return Nothing
                    Dim tableEntry = findEntry(directoryEntry.Value.Value, tag)
                    If tableEntry Is Nothing OrElse tableEntry.Value.Type <> 10 Then Return Nothing ' SRATIONAL
                    Dim valueCount = tableEntry.Value.Count
                    If valueCount < 1 OrElse valueCount > 200 Then Return Nothing
                    Dim data = ReadExactly(stream, baseOffset + tableEntry.Value.Value, CInt(valueCount * 8))
                    If data Is Nothing Then Return Nothing
                    Dim result(CInt(valueCount) - 1) As Double
                    For i = 0 To result.Length - 1
                        Dim numerator = u32(data, i * 8)
                        Dim denominator = u32(data, i * 8 + 4)
                        If numerator >= 2147483648L Then numerator -= 4294967296L
                        If denominator >= 2147483648L Then denominator -= 4294967296L
                        If denominator = 0 Then Return Nothing
                        result(i) = numerator / CDbl(denominator)
                    Next
                    Return result
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("CameraLensCorrection.Fujifilm", ex)
                Return Nothing
            End Try
        End Function

        Private Shared Function ReadExactly(stream As IO.Stream, offset As Long, length As Integer) As Byte()
            If offset < 0 OrElse length <= 0 OrElse offset + length > stream.Length Then Return Nothing
            stream.Seek(offset, IO.SeekOrigin.Begin)
            Dim buffer(length - 1) As Byte
            Dim read = 0
            While read < length
                Dim n = stream.Read(buffer, read, length - read)
                If n <= 0 Then Return Nothing
                read += n
            End While
            Return buffer
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
