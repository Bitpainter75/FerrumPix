Imports System
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Die verschmolzene Gleitkomma-Tonwertkette (Umbau 2026-07-20).
    '''
    ''' PROBLEM: In ProcessBitmapBase liefen acht Farbstufen NACHEINANDER, jede erzeugte ein neues
    ''' 8-Bit-Bitmap und rundete dabei. Der Verlust summiert sich - gemessen an einem Verlauf durch
    ''' Belichtung + Kontrast + Gamma + Tiefen: 40 auf 37 Tonwerte, groesster Sprung 1 auf 3 Stufen.
    ''' Das ist die Streifenbildung in Himmel und Hauttoenen.
    '''
    ''' LOESUNG: Alle acht sind reine PUNKTOPERATIONEN (Ergebnis haengt nur vom eigenen Pixel ab).
    ''' Sie werden zu EINER Gleitkomma-Stufe verschmolzen, die einmal ueber die Pixel laeuft und
    ''' EINMAL am Ende quantisiert. Nachbarschaftsoperationen (Weichzeichnen, Schaerfe, Vignette)
    ''' bleiben unberuehrt bei 8 Bit.
    '''
    ''' NICHT die ganze Pipeline auf 16 Bit: gemessen 2x Speicher und 2,8x langsameres Weichzeichnen,
    ''' waehrend eigene Pixelschleifen in Gleitkomma sogar 0,76x der Zeit von Bytes brauchen. Die
    ''' Praezision ist genau dort gratis, wo sie gebraucht wird.
    '''
    ''' DREI REGELN, die den Umbau tragen:
    '''  1. KLEMMUNGEN BLEIBEN, exakt an den heutigen Stellen. Jede Altstufe klemmt implizit auf
    '''     [0,1], weil sie ein 8-Bit-Bitmap erzeugt; Skia klemmt zusaetzlich zwischen Farbmatrix und
    '''     Tabelle (gemessen 2026-07-20). Sie wegzulassen aendert das Bild sichtbar - die
    '''     Kontrast-Presets mit negativem Offset und Saettigungsmatrizen ueber 1 laufen regelmaessig
    '''     aus dem Bereich.
    '''  2. ALPHA einheitlich korrekt: entpremultiplizieren, rechnen, premultiplizieren. Die alten
    '''     Skia-Stufen taten das, die alten Pixelschleifen rechneten dagegen auf vormultiplizierten
    '''     Werten, als waeren es Farben - bei Objekt-Ebenen mit weicher Kante also falsch.
    '''  3. DITHERING: geordnetes 8x8-Bayer, positionsbasiert, nur EINMAL am Ende. Keine
    '''     Fehlerdiffusion - die ist weder zeilenunabhaengig (ForEachRow) noch deterministisch unter
    '''     Parallel.For, und die Diagnose prueft beides.
    ''' </summary>
    Partial Public Class ImageProcessor

        ''' <summary>Stuetzstellen der verschmolzenen Skalartabellen. 4096 Schritte liegen drei
        ''' Groessenordnungen unter einer 8-Bit-Stufe; das 4097. Element traegt exakt x=1.0, damit der
        ''' obere Rand ohne Sonderfall interpoliert.</summary>
        Friend Const PointOpTableSize As Integer = 4097

        ''' <summary>Vorberechneter Zustand der Kette - einmal pro Bild gebaut, nicht pro Pixel.
        ''' Die teuren Teile (Spline-Auswertung, Exp in SoftShoulder) landen damit in der
        ''' Vorberechnung; pro Pixel bleiben Tabellenzugriffe mit linearer Interpolation.</summary>
        Friend NotInheritable Class PointOpChain
            ''' Nichts zu tun - der Aufrufer gibt dann die Quelle unveraendert zurueck.
            Public IsIdentity As Boolean = True

            ''' Verschmolzene per-Kanal-Skalarkette (Tonwertkurve + Lichter/Tiefen/Weiss/Schwarz +
            ''' RGB- und Kanalkurven). Nothing = neutral.
            Public ScalarR As Single()
            Public ScalarG As Single()
            Public ScalarB As Single()

            ''' Farbmatrix (Temperatur/Toenung/Saettigung/Dynamik), 20 Eintraege wie bei Skia.
            ''' Die Offset-Spalte liegt hier bereits in 0..1 vor.
            Public ColorMatrix As Single()

            ''' Filmnegativ - laeuft als ERSTE Stufe, noch vor der Farbmatrix. Eigene Tabellen statt
            ''' der verschmolzenen, weil im S/W-Fall die Graumatrix DAZWISCHEN liegt.
            Public NegR As Single()
            Public NegG As Single()
            Public NegB As Single()
            Public NegMonochrome As Boolean

            ''' Luminanzkurve - kanaluebergreifend (HSL-Roundtrip), deshalb keine Skalartabelle
            ''' sondern eine Nachschlagetabelle auf der L-Achse.
            Public LuminanceCurve As Single()

            ''' HSL-Baender und Split-Toning brauchen die Reglerwerte im Pixel - der Zustand wird
            ''' mitgefuehrt statt kopiert.
            Public Hsl As ImageAdjustments
            Public SplitToning As ImageAdjustments
            Public SplitPivot As Double
            Public SplitHasShadow As Boolean
            Public SplitHasHighlight As Boolean
            Public SplitHasMidtone As Boolean
            Public SplitHasGlobal As Boolean
            Public SplitHasLuminance As Boolean
            ''' Exponent auf die Zonengewichte (ColorGradeBlending). 1 = wie frueheres Split-Toning.
            Public SplitBlendExponent As Single

            ''' Gruen-/Magenta-Verschiebung nur in den Tiefen (crs:ShadowTint). Luminanzabhaengig,
            ''' laesst sich also nicht in die Matrix falten.
            Public ShadowTint As Single

            ''' Preset-Farbmatrix samt Ueberblendstaerke (das Preset wird ueber das Original geblendet).
            Public PresetMatrix As Single()
            Public PresetStrength As Single

            ''' 3D-Cube-LUT mit trilinearer Interpolation.
            Public CubeTable As Single()
            Public CubeSize As Integer
            Public CubeStrength As Single
        End Class

        ' ── Aufsatzpunkt ─────────────────────────────────────────────────────────

        ''' <summary>Wendet die verschmolzene Kette an. Gibt <paramref name="source"/> unveraendert
        ''' zurueck, wenn nichts zu tun ist - ReplaceBitmap erkennt Referenzgleichheit und disposed
        ''' dann nicht.</summary>
        ''' <param name="measured">Quelle fuer die Filmnegativ-Messung. Die Basis-/Dichtefarbe wird am
        ''' UNVERAENDERTEN Bild gemessen; seit die Umkehr Teil der Kette ist, muss das ausdruecklich
        ''' dasselbe Bitmap sein, das auch in die Kette geht.</param>
        Friend Shared Function ApplyPointOpChain(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            If source Is Nothing OrElse adj Is Nothing Then Return source
            Dim chain = BuildPointOpChain(adj, source)
            If chain.IsIdentity Then Return source
            Return RunPointOpChain(source, chain)
        End Function

        ''' <summary>Baut den Ketten-Zustand. Die Frueh-Ausstiege der Altfunktionen werden hier exakt
        ''' nachgebildet: ein neutraler Regler muss weiterhin GAR NICHTS tun, sonst kostet die Kette
        ''' Zeit, wo heute nur ein If steht.</summary>
        Friend Shared Function BuildPointOpChain(adj As ImageAdjustments,
                                                 Optional measured As SKBitmap = Nothing) As PointOpChain
            Dim chain As New PointOpChain()

            ' --- Filmnegativ: ERSTE Stufe, exakt die Bedingung aus ApplyFilmNegative ---
            ' Die Umkehr steht vor allen Farbanpassungen: Belichtung, Weissabgleich, Kurven und Filter
            ' sollen auf dem fertigen Positiv arbeiten - auf dem Negativ waeren sie seitenverkehrt.
            If adj.NegativeEnabled AndAlso measured IsNot Nothing Then
                Dim stats = ResolveFilmNegativeStats(measured, adj)
                Dim gamma = CSng(Math.Pow(2.0, adj.NegativeGamma / 100.0))
                chain.NegR = BuildPointOpFilmNegativeTable(stats.BaseColor.Red, stats.DensityColor.Red, gamma)
                chain.NegG = BuildPointOpFilmNegativeTable(stats.BaseColor.Green, stats.DensityColor.Green, gamma)
                chain.NegB = BuildPointOpFilmNegativeTable(stats.BaseColor.Blue, stats.DensityColor.Blue, gamma)
                chain.NegMonochrome = adj.NegativeMonochrome
                chain.IsIdentity = False
            End If

            ' --- Farbmatrix: exakt die Bedingung aus ApplyColorAdjustments ---
            Dim wantsColor = adj.Exposure <> 0 OrElse adj.Temperature <> 0 OrElse adj.Tint <> 0 OrElse
                             adj.Saturation <> 0 OrElse adj.Vibrance <> 0 OrElse adj.Contrast <> 0 OrElse
                             adj.Brightness <> 0
            ' Kalibrierung wirkt auf die Primaerfarben und gehoert damit VOR Weissabgleich und
            ' Saettigung - dieselbe Reihenfolge wie in Lightroom. Beide Matrizen werden zu EINER
            ' verrechnet, der Pixeldurchlauf bleibt also unveraendert schnell.
            Dim kalibrierung = BuildCalibrationMatrix(adj)
            If wantsColor OrElse kalibrierung IsNot Nothing Then
                Dim wb = If(wantsColor, BuildPointOpColorMatrix(adj), Nothing)
                chain.ColorMatrix = ComposeColorMatrix(wb, kalibrierung)
                chain.IsIdentity = False
            End If

            ' --- Skalarkette: Tonwertkurve UND Lichter/Tiefen/Weiss/Schwarz in EINER Tabelle ---
            ' Die Bedingungen bleiben getrennt (wie die Frueh-Ausstiege der Altfunktionen), die
            ' AUSWERTUNG wird verschmolzen: v durchlaeuft beide Formeln stetig, ohne Zwischenrundung.
            ' Genau hier verschwindet eine der 8-Bit-Stufen.
            Dim wantsTone = adj.Exposure <> 0 OrElse adj.Contrast <> 0 OrElse adj.Brightness <> 0
            Dim wantsTonal = adj.Highlights <> 0 OrElse adj.ShadowsLevel <> 0 OrElse
                             adj.Whites <> 0 OrElse adj.Blacks <> 0
            ' RGB-Kurve und Kanalkurven kommen in DIESELBE Tabelle. Heute steht dort
            ' redLut(rgbLut(i)) - eine DOPPELTE Byte-Rundung, die schlimmste Stelle der ganzen
            ' Kette. Stetig verkettet verschwindet sie ersatzlos.
            Dim wantsRgbCurve = Not ImageAdjustments.IsIdentityCurve(adj.CurveRgbPoints)
            Dim wantsChannelCurves = Not ImageAdjustments.IsIdentityCurve(adj.CurveRedPoints) OrElse
                                     Not ImageAdjustments.IsIdentityCurve(adj.CurveGreenPoints) OrElse
                                     Not ImageAdjustments.IsIdentityCurve(adj.CurveBluePoints)

            If wantsTone OrElse wantsTonal OrElse wantsRgbCurve OrElse wantsChannelCurves Then
                ' Die Kanaele trennen sich erst bei den Kanalkurven - vorher ist die Kette identisch,
                ' deshalb wird der gemeinsame Teil nur EINMAL gerechnet.
                Dim common = BuildPointOpScalarTable(adj, wantsTone, wantsTonal, wantsRgbCurve)
                If wantsChannelCurves Then
                    chain.ScalarR = ChainCurveOntoTable(common, adj.CurveRedPoints)
                    chain.ScalarG = ChainCurveOntoTable(common, adj.CurveGreenPoints)
                    chain.ScalarB = ChainCurveOntoTable(common, adj.CurveBluePoints)
                Else
                    chain.ScalarR = common
                    chain.ScalarG = common
                    chain.ScalarB = common
                End If
                chain.IsIdentity = False
            End If

            ' --- Luminanzkurve (kanaluebergreifend, HSL-Roundtrip) ---
            ' Bleibt eine eigene Stufe: sie wirkt auf L, nicht auf die Kanaele, und laesst sich deshalb
            ' nicht in die Skalartabelle falten. Stetig ausgewertet statt aus einer 256er-Bytetabelle.
            If Not ImageAdjustments.IsIdentityCurve(adj.CurveLuminancePoints) Then
                Dim points = ParseCurvePoints(adj.CurveLuminancePoints)
                Dim table = New Single(PointOpTableSize - 1) {}
                For i = 0 To PointOpTableSize - 1
                    Dim v = i / CSng(PointOpTableSize - 1)
                    table(i) = Clamp(CSng(EvaluateCurveSpline(points, v * 255.0) / 255.0), 0.0F, 1.0F)
                Next
                chain.LuminanceCurve = table
                chain.IsIdentity = False
            End If

            ' --- HSL-Baender: exakt die Bedingung aus ApplyHsl ---
            If adj.HasHslChanges() Then
                chain.Hsl = adj
                chain.IsIdentity = False
            End If

            If adj.CalibrationShadowTint <> 0 Then
                chain.ShadowTint = adj.CalibrationShadowTint
                chain.IsIdentity = False
            End If

            ' --- Farbgradierung (frueher Split-Toning) ---
            ' Jede Zone einzeln pruefen: sonst faellt eine reine Mitten- oder Global-Toenung durch das
            ' Gitter und die Stufe wird uebersprungen, obwohl sie etwas zu tun haette.
            Dim hasShadow = adj.ColorGradeShadowSaturation <> 0
            Dim hasHighlight = adj.ColorGradeHighlightSaturation <> 0
            Dim hasMidtone = adj.ColorGradeMidtoneSaturation <> 0
            Dim hasGlobal = adj.ColorGradeGlobalSaturation <> 0
            Dim hasLuminance = adj.ColorGradeShadowLuminance <> 0 OrElse adj.ColorGradeMidtoneLuminance <> 0 OrElse
                               adj.ColorGradeHighlightLuminance <> 0 OrElse adj.ColorGradeGlobalLuminance <> 0
            If hasShadow OrElse hasHighlight OrElse hasMidtone OrElse hasGlobal OrElse hasLuminance Then
                Dim balance = Clamp(adj.ColorGradeBalance, -100, 100) / 100.0
                chain.SplitToning = adj
                chain.SplitPivot = Math.Max(0.1, Math.Min(0.9, 0.5 - balance * 0.4))
                chain.SplitHasShadow = hasShadow
                chain.SplitHasHighlight = hasHighlight
                chain.SplitHasMidtone = hasMidtone
                chain.SplitHasGlobal = hasGlobal
                chain.SplitHasLuminance = hasLuminance
                ' Blending 50 ergibt Exponent 1 und damit exakt die frueheren Rampen; darunter bleiben
                ' die Toenungen staerker in ihrer Zone, darueber greifen sie weiter ineinander.
                chain.SplitBlendExponent = CSng(Math.Pow(2.0, (50.0 - Clamp(adj.ColorGradeBlending, 0, 100)) / 50.0))
                chain.IsIdentity = False
            End If

            ' --- Preset-Farbmatrix: exakt die Bedingungen aus ApplyFilterPreset ---
            ' "weich" liefert bewusst KEINE Matrix (es ist ein Weichzeichner) und bleibt draussen.
            Dim presetStrength = Clamp(adj.FilterStrength / 100.0F, 0, 1)
            If presetStrength > 0 AndAlso Not String.IsNullOrWhiteSpace(adj.FilterPreset) AndAlso
               Not String.Equals(adj.FilterPreset, "Keine", StringComparison.OrdinalIgnoreCase) Then
                Dim m = BuildFilterPresetMatrix(adj.FilterPreset)
                If m IsNot Nothing Then
                    chain.PresetMatrix = m
                    ' Die Altstufe blendet mit einem BYTE-Alpha ueber - der Wert wird hier genauso
                    ' quantisiert, sonst weicht die Staerke um bis zu 1/255 ab.
                    chain.PresetStrength = ClampToByte(255 * presetStrength) / 255.0F
                    chain.IsIdentity = False
                End If
            End If

            ' --- Cube-LUT: exakt die Bedingungen aus ApplyCubeLut ---
            Dim lutStrength = Clamp(adj.LutStrength / 100.0F, 0, 1)
            If lutStrength > 0 AndAlso Not String.IsNullOrWhiteSpace(adj.LutPath) Then
                Dim lut = LoadCubeLut(adj.LutPath)
                If lut IsNot Nothing Then
                    chain.CubeTable = lut.Table
                    chain.CubeSize = lut.Size
                    chain.CubeStrength = lutStrength
                    chain.IsIdentity = False
                End If
            End If

            Return chain
        End Function

        ''' <summary>Filmnegativ-Tonwertkurve eines Kanals als stetige Tabelle - wortgleich zu
        ''' BuildFilmNegativeLut, nur an 4097 statt 256 Stuetzstellen und ohne Byte-Rundung. Diese
        ''' Stufe streckt den Tonwertumfang am aggressivsten und rundete bisher als ERSTE, also bevor
        ''' Belichtung und Kurven ueberhaupt greifen konnten.</summary>
        Private Shared Function BuildPointOpFilmNegativeTable(baseValue As Byte, densityValue As Byte,
                                                              gamma As Single) As Single()
            Dim baseLevel = Math.Max(2.0, CDbl(baseValue))
            Dim densityLevel = Math.Min(Math.Max(1.0, CDbl(densityValue)), baseLevel - 1.0)
            Dim span = Math.Log(baseLevel / densityLevel)
            Dim invGamma = 1.0 / Math.Max(0.05, CDbl(gamma))

            Dim table = New Single(PointOpTableSize - 1) {}
            For i = 0 To PointOpTableSize - 1
                ' Die Altstufe indiziert mit dem Byte i und klemmt level auf mindestens 1 - in 0..1
                ' entspricht das v*255.
                Dim level = Math.Max(1.0, i / CDbl(PointOpTableSize - 1) * 255.0)
                Dim t = Clamp(CSng(Math.Log(baseLevel / level) / span), 0.0F, 1.0F)
                table(i) = Clamp(CSng(Math.Pow(t, invGamma)), 0.0F, 1.0F)
            Next
            Return table
        End Function

        ' ── Vorberechnung ────────────────────────────────────────────────────────

        ''' <summary>Farbmatrix wie in ApplyColorAdjustments - Belichtung bleibt bewusst DRAUSSEN und
        ''' wandert in die Tonwerttabelle, weil dort die weiche Schulter greift.</summary>
        ''' <summary>Matrix der KAMERAKALIBRIERUNG. Jede Primaerfarbe wird um die Grauachse gedreht
        ''' (Farbton) und in ihrem Abstand davon skaliert (Saettigung); die gedrehten Primaerfarben
        ''' bilden die Spalten der Matrix.
        ''' Die Drehung um die Grauachse haelt Neutralgrau neutral - eine naive Kanalvertauschung
        ''' wuerde dagegen jedes Grau einfaerben.
        ''' Nothing, wenn alle sechs Regler auf 0 stehen (kein Rechenaufwand im Normalfall).</summary>
        Private Shared Function BuildCalibrationMatrix(adj As ImageAdjustments) As Single()
            If adj.CalibrationRedHue = 0 AndAlso adj.CalibrationRedSaturation = 0 AndAlso
               adj.CalibrationGreenHue = 0 AndAlso adj.CalibrationGreenSaturation = 0 AndAlso
               adj.CalibrationBlueHue = 0 AndAlso adj.CalibrationBlueSaturation = 0 Then Return Nothing

            ' Primaerfarbe drehen und saettigen. Rueckgabe: die neue Spalte der Matrix.
            Dim Wandeln =
                Function(p As Single(), hue As Single, sat As Single) As Single()
                    ' +/-100 entsprechen +/-30 Grad - der Bereich, in dem sich Adobes Regler bewegen.
                    Dim winkel = hue / 100.0 * 30.0 * Math.PI / 180.0
                    Dim c = Math.Cos(winkel), sn = Math.Sin(winkel)
                    ' Drehung um die Grauachse (1,1,1)/sqrt(3), Standardform.
                    Dim k = (1.0 - c) / 3.0
                    Dim w = Math.Sqrt(1.0 / 3.0) * sn
                    Dim m = {
                        c + k, k - w, k + w,
                        k + w, c + k, k - w,
                        k - w, k + w, c + k
                    }
                    Dim r = CSng(m(0) * p(0) + m(1) * p(1) + m(2) * p(2))
                    Dim g = CSng(m(3) * p(0) + m(4) * p(1) + m(5) * p(2))
                    Dim b = CSng(m(6) * p(0) + m(7) * p(1) + m(8) * p(2))
                    ' Saettigung: Abstand von der Grauachse skalieren, Helligkeit unangetastet.
                    Dim faktor = 1.0F + sat / 100.0F
                    Dim grau = 0.299F * r + 0.587F * g + 0.114F * b
                    Return New Single() {grau + (r - grau) * faktor,
                                         grau + (g - grau) * faktor,
                                         grau + (b - grau) * faktor}
                End Function

            Dim pr = Wandeln(New Single() {1, 0, 0}, adj.CalibrationRedHue, adj.CalibrationRedSaturation)
            Dim pg = Wandeln(New Single() {0, 1, 0}, adj.CalibrationGreenHue, adj.CalibrationGreenSaturation)
            Dim pb = Wandeln(New Single() {0, 0, 1}, adj.CalibrationBlueHue, adj.CalibrationBlueSaturation)

            ' Spalten sind die gewandelten Primaerfarben - Skia-Layout (5 Spalten je Zeile).
            ' ZEILENSUMMEN AUF 1 NORMIEREN: ohne das bleibt Neutralgrau nicht neutral. Grau ist
            ' (1,1,1); die Matrix bildet es auf die SUMME der drei Primaerfarben ab, und sobald auch
            ' nur eine gedreht wurde, ist die Summe nicht mehr grau. Gemessen wurde aus (128,128,128)
            ' ein (116,170,96) - ein deutlicher Gruenstich auf jeder neutralen Flaeche.
            Dim zeilen = {
                New Single() {pr(0), pg(0), pb(0)},
                New Single() {pr(1), pg(1), pb(1)},
                New Single() {pr(2), pg(2), pb(2)}
            }
            For Each zeile In zeilen
                Dim summe = zeile(0) + zeile(1) + zeile(2)
                If Math.Abs(summe) > 0.0001F Then
                    zeile(0) /= summe : zeile(1) /= summe : zeile(2) /= summe
                End If
            Next

            Return New Single() {
                zeilen(0)(0), zeilen(0)(1), zeilen(0)(2), 0, 0,
                zeilen(1)(0), zeilen(1)(1), zeilen(1)(2), 0, 0,
                zeilen(2)(0), zeilen(2)(1), zeilen(2)(2), 0, 0,
                0, 0, 0, 1, 0
            }
        End Function

        ''' <summary>Verkettet zwei Skia-Farbmatrizen zu einer (erst <paramref name="innen"/>, dann
        ''' <paramref name="aussen"/>). So kostet die Kalibrierung KEINEN zweiten Durchlauf pro
        ''' Pixel - sie wird einmal beim Bauen in die vorhandene Matrix hineingerechnet.</summary>
        Private Shared Function ComposeColorMatrix(aussen As Single(), innen As Single()) As Single()
            If aussen Is Nothing Then Return innen
            If innen Is Nothing Then Return aussen
            Dim r = New Single(19) {}
            For zeile = 0 To 3
                For spalte = 0 To 3
                    Dim summe = 0.0F
                    For k = 0 To 3
                        summe += aussen(zeile * 5 + k) * innen(k * 5 + spalte)
                    Next
                    r(zeile * 5 + spalte) = summe
                Next
                ' Offset-Spalte: aussen wirkt auf die Offsets von innen, plus eigener Offset.
                Dim off = aussen(zeile * 5 + 4)
                For k = 0 To 3
                    off += aussen(zeile * 5 + k) * innen(k * 5 + 4)
                Next
                r(zeile * 5 + 4) = off
            Next
            Return r
        End Function

        Private Shared Function BuildPointOpColorMatrix(adj As ImageAdjustments) As Single()
            Dim tempR = 1.0F + adj.Temperature / 200.0F
            Dim tempB = 1.0F - adj.Temperature / 200.0F
            Dim tintG = 1.0F + adj.Tint / 200.0F

            Const lumR As Single = 0.299F
            Const lumG As Single = 0.587F
            Const lumB As Single = 0.114F
            Dim sat = 1.0F + adj.Saturation / 100.0F + adj.Vibrance / 200.0F
            Dim invSat = 1.0F - sat

            Return New Single() {
                (lumR * invSat + sat) * tempR, lumG * invSat * tempR, lumB * invSat * tempR, 0, 0,
                lumR * invSat, (lumG * invSat + sat) * tintG, lumB * invSat, 0, 0,
                lumR * invSat * tempB, lumG * invSat * tempB, (lumB * invSat + sat) * tempB, 0, 0,
                0, 0, 0, 1, 0
            }
        End Function

        ''' <summary>Die verschmolzene per-Kanal-Skalarkette als STETIGE Tabelle: erst die
        ''' Tonwertkurve (Belichtung/Kontrast/Helligkeit, identisch zu BuildToneCurveLut), dann die
        ''' Lichter/Tiefen/Weiss/Schwarz-Kaskade (identisch zu ApplyTonalLUT) - beide an 4097 statt
        ''' 256 Stuetzstellen und in Single statt Byte. Die Zwischenrundung zwischen den beiden
        ''' entfaellt damit ersatzlos.</summary>
        Private Shared Function BuildPointOpScalarTable(adj As ImageAdjustments,
                                                        includeTone As Boolean,
                                                        includeTonal As Boolean,
                                                        includeRgbCurve As Boolean) As Single()
            Dim exposureGain = CSng(Math.Pow(2.0, adj.Exposure / 100.0 * 4.0))
            Dim contrast = Math.Max(0.0F, 1.0F + adj.Contrast / 100.0F * 0.75F)
            Dim brightness = adj.Brightness / 100.0F * 80.0F / 255.0F

            Dim rgbPoints = If(includeRgbCurve, ParseCurvePoints(adj.CurveRgbPoints), Nothing)

            Dim low = ToneTransfer(0.0F, exposureGain, contrast, brightness)
            Dim high = ToneTransfer(1.0F, exposureGain, contrast, brightness)
            Dim overshootHigh = Math.Max(0.0F, high - 1.0F)
            Dim overshootLow = Math.Max(0.0F, -low)
            Dim shoulder = Clamp(ToneShoulderBase + 0.5F * overshootHigh, ToneShoulderBase, ToneShoulderMax)
            Dim toe = Clamp(ToneShoulderBase + 0.5F * overshootLow, ToneShoulderBase, ToneShoulderMax)
            Dim rolloff = Clamp((overshootHigh + overshootLow) / ToneShoulderBase, 0.0F, 1.0F)

            Dim table = New Single(PointOpTableSize - 1) {}
            For i = 0 To PointOpTableSize - 1
                Dim v = i / CSng(PointOpTableSize - 1)

                If includeTone Then
                    Dim y = ToneTransfer(v, exposureGain, contrast, brightness)
                    Dim hard = Clamp(y, 0.0F, 1.0F)
                    Dim soft = SoftShoulder(y, toe, shoulder)
                    v = Clamp(hard + (soft - hard) * rolloff, 0.0F, 1.0F)
                End If

                If includeTonal Then
                    ' Wortgleich zu ApplyTonalLUT - nur ohne die Byte-Quantisierung am Ende.
                    Dim d = CDbl(v)
                    Dim bw = Math.Max(0.0, 1.0 - d * 2.5)
                    d += (adj.Blacks / 100.0) * bw * 0.4
                    Dim sw = Math.Max(0.0, 1.0 - Math.Abs(d - 0.25) * 6.0)
                    d += (adj.ShadowsLevel / 100.0) * sw * 0.35
                    Dim ww = Math.Max(0.0, 1.0 - (1.0 - d) * 2.5)
                    d += (adj.Whites / 100.0) * ww * 0.4
                    Dim hw = Math.Max(0.0, 1.0 - Math.Abs(d - 0.75) * 6.0)
                    d += (adj.Highlights / 100.0) * hw * 0.35
                    v = Clamp(CSng(d), 0.0F, 1.0F)
                End If

                If includeRgbCurve Then
                    ' EvaluateCurveSpline rechnet in 0..255 - stetig ausgewertet, nicht aus einer
                    ' 256er-Tabelle gelesen.
                    v = Clamp(CSng(EvaluateCurveSpline(rgbPoints, v * 255.0) / 255.0), 0.0F, 1.0F)
                End If

                table(i) = v
            Next
            Return table
        End Function

        ''' <summary>Haengt eine Kanalkurve stetig an eine bereits gebaute Tabelle. Ersetzt das
        ''' heutige redLut(rgbLut(i)), bei dem der Zwischenwert auf ein Byte gerundet wird.</summary>
        Private Shared Function ChainCurveOntoTable(source As Single(), pointsCsv As String) As Single()
            If ImageAdjustments.IsIdentityCurve(pointsCsv) Then Return source
            Dim points = ParseCurvePoints(pointsCsv)
            Dim table = New Single(PointOpTableSize - 1) {}
            For i = 0 To PointOpTableSize - 1
                table(i) = Clamp(CSng(EvaluateCurveSpline(points, source(i) * 255.0) / 255.0), 0.0F, 1.0F)
            Next
            Return table
        End Function

        ''' <summary>Tabellenzugriff mit linearer Interpolation. <paramref name="v"/> wird geklemmt -
        ''' die Kette darf nie ausserhalb [0,1] indizieren.</summary>
        ''' <summary>Mischt eine Zonen-Toenung in den Pixel. Die Tintfarbe uebernimmt die Luminanz des
        ''' Pixels - es wird also nur chromatisch verschoben, nicht aufgehellt (dafuer ist die getrennte
        ''' Luminanz-Achse da). Anteil = Zonengewicht mal Saettigung, wie in der Altstufe.</summary>
        Private Shared Sub ApplyColorGradeTint(ByRef rr As Single, ByRef gg As Single, ByRef bb As Single,
                                               weight As Single, hue As Single, saturation As Single, lum As Double)
            If weight <= 0.0F OrElse saturation = 0.0F Then Return
            Dim tintSat = Math.Max(0.0, Math.Min(1.0, saturation / 100.0))
            Dim tr As Double, tg As Double, tb As Double
            HslToRgbF(hue, tintSat, lum, tr, tg, tb)
            Dim amount = CSng(weight * tintSat)
            rr += CSng(tr - rr) * amount
            gg += CSng(tg - gg) * amount
            bb += CSng(tb - bb) * amount
        End Sub

        Private Shared Function SampleTable(table As Single(), v As Single) As Single
            If v <= 0.0F Then Return table(0)
            If v >= 1.0F Then Return table(PointOpTableSize - 1)
            Dim pos = v * (PointOpTableSize - 1)
            Dim i = CInt(Math.Floor(pos))
            Dim f = pos - i
            Return table(i) + (table(i + 1) - table(i)) * f
        End Function

        ' ── Dithering ────────────────────────────────────────────────────────────

        ''' <summary>Geordnete 8x8-Bayer-Matrix, auf [-0.5, +0.5) normiert. Amplitude also genau
        ''' 1 LSB, Mittelwert 0 - der Rundungsfehler wird raeumlich verteilt statt aufaddiert.
        ''' Positionsbasiert und damit zeilenunabhaengig und deterministisch: Pflicht, weil die
        ''' Kette unter Parallel.For laeuft und die Diagnose (Abschnitt C) Bitgleichheit prueft.</summary>
        Private Shared ReadOnly DitherMatrix As Single() = BuildBayer8()

        Private Shared Function BuildBayer8() As Single()
            ' Rekursive Bayer-Konstruktion: M(2n) = [4M(n), 4M(n)+2; 4M(n)+3, 4M(n)+1]
            Dim base2 = New Integer() {0, 2, 3, 1}
            Dim m4 = ExpandBayer(base2, 2)
            Dim m8 = ExpandBayer(m4, 4)
            Dim result = New Single(63) {}
            For i = 0 To 63
                result(i) = (m8(i) + 0.5F) / 64.0F - 0.5F
            Next
            Return result
        End Function

        Private Shared Function ExpandBayer(src As Integer(), size As Integer) As Integer()
            Dim n = size * 2
            Dim dst = New Integer(n * n - 1) {}
            For y = 0 To size - 1
                For x = 0 To size - 1
                    Dim v = src(y * size + x) * 4
                    dst(y * n + x) = v
                    dst(y * n + (x + size)) = v + 2
                    dst((y + size) * n + x) = v + 3
                    dst((y + size) * n + (x + size)) = v + 1
                Next
            Next
            Return dst
        End Function

        ''' <summary>Quantisierung mit Dither. Abschneiden statt Runden, weil der Dither-Term den
        ''' Rundungsversatz bereits enthaelt. Klemmt VOR der Konvertierung - CByte wirft bei
        ''' Ueberlauf (VB-Falle).</summary>
        Private Shared Function QuantizeDithered(v As Single, dither As Single) As Byte
            Dim scaled = v * 255.0F + 0.5F + dither
            If scaled <= 0.0F Then Return 0
            If scaled >= 255.0F Then Return 255
            Return CByte(Math.Floor(scaled))
        End Function

        ' ── Pufferzugriff mit Kanalindizes ───────────────────────────────────────

        ''' <summary>Wie TryBorrowBgraBuffer, akzeptiert aber AUCH Rgba8888 und liefert die
        ''' Kanalindizes mit. Objekt-Ebenen sind Rgba8888 - TryBorrowBgraBuffer lehnt die ab, weshalb
        ''' HSL/Split-Toning/Cube-LUT dort bisher auf GetPixel/SetPixel zurueckfielen (P/Invoke pro
        ''' Pixel). Damit entfaellt dieser Rueckfall ersatzlos.</summary>
        Private Shared Function TryBorrowRgbaLikeBuffer(bmp As SKBitmap, ByRef buffer As Byte(),
                                                        ByRef stride As Integer,
                                                        ByRef ri As Integer, ByRef gi As Integer,
                                                        ByRef bi As Integer, ByRef ai As Integer) As Boolean
            buffer = Nothing
            stride = 0
            ri = 0 : gi = 0 : bi = 0 : ai = 0
            If bmp Is Nothing Then Return False

            Select Case bmp.ColorType
                Case SKColorType.Bgra8888
                    ri = 2 : gi = 1 : bi = 0 : ai = 3
                Case SKColorType.Rgba8888
                    ri = 0 : gi = 1 : bi = 2 : ai = 3
                Case Else
                    Return False
            End Select

            stride = bmp.RowBytes
            Dim length = stride * bmp.Height
            If length <= 0 Then Return False
            buffer = New Byte(length - 1) {}
            Marshal.Copy(bmp.GetPixels(), buffer, 0, length)
            Return True
        End Function

        ' ── HSL ohne Byte-Zwischenstufe ──────────────────────────────────────────
        ' Bewusst DANEBENGELEGT statt RgbToHsl/HslToRgb ersetzt: die haben zehn weitere Nutzer und
        ' nehmen Bytes entgegen. Der Algorithmus ist 1:1 uebernommen - einschliesslich des Epsilons
        ' 0.00001 und der Reihenfolge der maxV-Vergleiche, weil beides bei Grautoenen und exakt
        ' gleichen Kanaelen ueber das Ergebnis entscheidet.
        '
        ' GERECHNET WIRD IN DOUBLE, nicht in Single - und das ist keine Bequemlichkeit:
        ' GetHslBandAdjustments waehlt das Farbband ueber HARTE Grenzen (15/45/75/165/195/255/285).
        ' Liegt ein Farbton exakt auf einer Grenze, entscheidet das letzte Bit darueber, welches Band
        ' greift - und Nachbarbaender koennen voellig verschiedene Regler tragen. Gemessen am
        ' Testbild: Pixel (128,127,124) ergibt Farbton exakt 45,0; in Single kippt (g-b)/d auf
        ' 44,999998, das Pixel faellt von Gelb nach Orange und weicht um 27 Tonwerte ab.
        ' Der Gewinn dieser Stufe liegt ohnehin im Wegfall der BYTE-Zwischenstufe, nicht in Single.

        Private Shared Sub RgbToHslF(r As Double, g As Double, b As Double,
                                     ByRef h As Double, ByRef s As Double, ByRef l As Double)
            Dim maxV = Math.Max(r, Math.Max(g, b))
            Dim minV = Math.Min(r, Math.Min(g, b))
            l = (maxV + minV) / 2.0

            If Math.Abs(maxV - minV) < 0.00001 Then
                h = 0.0
                s = 0.0
                Return
            End If

            Dim d = maxV - minV
            s = If(l > 0.5, d / (2.0 - maxV - minV), d / (maxV + minV))
            If maxV = r Then
                h = (g - b) / d + If(g < b, 6.0, 0.0)
            ElseIf maxV = g Then
                h = (b - r) / d + 2.0
            Else
                h = (r - g) / d + 4.0
            End If
            h *= 60.0
        End Sub

        Private Shared Sub HslToRgbF(h As Double, s As Double, l As Double,
                                     ByRef r As Double, ByRef g As Double, ByRef b As Double)
            If s <= 0.0 Then
                r = l : g = l : b = l
                Return
            End If

            Dim q = If(l < 0.5, l * (1.0 + s), l + s - l * s)
            Dim p = 2.0 * l - q
            Dim hk = h / 360.0
            r = HueToRgbF(p, q, hk + 1.0 / 3.0)
            g = HueToRgbF(p, q, hk)
            b = HueToRgbF(p, q, hk - 1.0 / 3.0)
        End Sub

        Private Shared Function HueToRgbF(p As Double, q As Double, t As Double) As Double
            If t < 0 Then t += 1
            If t > 1 Then t -= 1
            If t < 1.0 / 6.0 Then Return p + (q - p) * 6.0 * t
            If t < 1.0 / 2.0 Then Return q
            If t < 2.0 / 3.0 Then Return p + (q - p) * (2.0 / 3.0 - t) * 6.0
            Return p
        End Function

        ''' <summary>Trilineare Cube-LUT-Abtastung in Gleitkomma - wie ProcessCubeLutPixel, aber mit
        ''' stetigem Eingang statt eines Bytes.</summary>
        Private Shared Sub SampleCubeLutF(table As Single(), size As Integer,
                                          ByRef r As Single, ByRef g As Single, ByRef b As Single)
            Dim maxIndex = size - 1
            Dim rf = Clamp(r, 0.0F, 1.0F) * maxIndex
            Dim gf = Clamp(g, 0.0F, 1.0F) * maxIndex
            Dim bf = Clamp(b, 0.0F, 1.0F) * maxIndex

            Dim r0 = CInt(Math.Floor(rf)) : Dim g0 = CInt(Math.Floor(gf)) : Dim b0 = CInt(Math.Floor(bf))
            Dim r1 = Math.Min(maxIndex, r0 + 1)
            Dim g1 = Math.Min(maxIndex, g0 + 1)
            Dim b1 = Math.Min(maxIndex, b0 + 1)
            Dim rt = rf - r0 : Dim gt = gf - g0 : Dim bt = bf - b0

            r = TrilinearChannel(table, size, r0, r1, g0, g1, b0, b1, rt, gt, bt, 0)
            g = TrilinearChannel(table, size, r0, r1, g0, g1, b0, b1, rt, gt, bt, 1)
            b = TrilinearChannel(table, size, r0, r1, g0, g1, b0, b1, rt, gt, bt, 2)
        End Sub

        ' ── Pixelzugriff mit GetPixel-Semantik ───────────────────────────────────

        ''' <summary>Liest einen Pixel aus einem geliehenen Puffer GENAU so, wie SKBitmap.GetPixel es
        ''' liefert: entpremultipliziert.
        '''
        ''' Gemessen 2026-07-20: gespeichert (100,50,25,128) gibt GetPixel als (199,100,50,128)
        ''' zurueck. Wer eine Stufe von GetPixel/SetPixel auf Rohpuffer umstellt und das uebersieht,
        ''' aendert das Bild bei jedem teiltransparenten Pixel - lautlos.</summary>
        Friend Shared Sub ReadUnpremultiplied(buf As Byte(), o As Integer, ri As Integer, gi As Integer, bi As Integer, ai As Integer,
                                              ByRef r As Integer, ByRef g As Integer, ByRef b As Integer, ByRef a As Integer)
            a = buf(o + ai)
            If a = 255 Then
                r = buf(o + ri) : g = buf(o + gi) : b = buf(o + bi)
            ElseIf a = 0 Then
                r = 0 : g = 0 : b = 0
            Else
                r = Math.Min(255, buf(o + ri) * 255 \ a)
                g = Math.Min(255, buf(o + gi) * 255 \ a)
                b = Math.Min(255, buf(o + bi) * 255 \ a)
            End If
        End Sub

        ''' <summary>Schreibt einen Pixel GENAU so, wie SKBitmap.SetPixel es tut: premultipliziert.
        ''' Gegenstueck zu <see cref="ReadUnpremultiplied"/>.</summary>
        Friend Shared Sub WritePremultiplied(buf As Byte(), o As Integer, ri As Integer, gi As Integer, bi As Integer, ai As Integer,
                                             r As Byte, g As Byte, b As Byte, a As Integer)
            If a = 0 Then
                buf(o) = 0 : buf(o + 1) = 0 : buf(o + 2) = 0 : buf(o + 3) = 0
                Return
            End If
            If a <> 255 Then
                r = CByte(Math.Min(a, CInt(r) * a \ 255))
                g = CByte(Math.Min(a, CInt(g) * a \ 255))
                b = CByte(Math.Min(a, CInt(b) * a \ 255))
            End If
            buf(o + ri) = r
            buf(o + gi) = g
            buf(o + bi) = b
            buf(o + ai) = CByte(a)
        End Sub

        ' ── Der eine Durchlauf ───────────────────────────────────────────────────

        Private Shared Function RunPointOpChain(source As SKBitmap, chain As PointOpChain) As SKBitmap
            Dim srcBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            If Not TryBorrowRgbaLikeBuffer(source, srcBuf, stride, ri, gi, bi, ai) Then Return source

            ' 16-Bit-Quelle (echte RAW-Entwicklung): gelesen wird mit voller Praezision, geschrieben
            ' immer 8 Bit. Der Gewinn liegt darin, dass die GANZE Farbkette auf den feinen Werten
            ' rechnet und erst am Ende einmal quantisiert - gemessen tragen die 16 Bit im
            ' Schattenbereich 23- bis 31-mal so viele unterscheidbare Stufen wie 8 Bit.
            ' Premul oder nicht? Das entscheidet, ob RGB vor dem Rechnen durch Alpha geteilt werden
            ' muss. Vorher wurde IMMER geteilt und am Ende wieder multipliziert - bei einer
            ' Unpremul-Quelle (PSD mit Transparenz, ICO) ergab das gemessen (128,98,24) statt der
            ' korrekten (238,88,13), weil die Division die Werte ueber 1 hebt und die Klemmung sie
            ' dort abschneidet. Zusaetzlich landeten vormultiplizierte Werte in einem Bitmap, das
            ' weiterhin als Unpremul deklariert war.
            Dim srcPremul = source.AlphaType = SKAlphaType.Premul
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim width = source.Width
            Dim dstBuf = New Byte(width * source.Height * 4 - 1) {}
            Dim dstStride = width * 4

            Dim m = chain.ColorMatrix
            Dim sr = chain.ScalarR
            Dim sg = chain.ScalarG
            Dim sb = chain.ScalarB
            Dim negR = chain.NegR
            Dim negG = chain.NegG
            Dim negB = chain.NegB
            Dim negMono = chain.NegMonochrome
            Dim lumCurve = chain.LuminanceCurve
            Dim hslAdj = chain.Hsl
            Dim schattenToenung = chain.ShadowTint
            Dim splitAdj = chain.SplitToning
            Dim splitPivot = chain.SplitPivot
            Dim splitShadow = chain.SplitHasShadow
            Dim splitHighlight = chain.SplitHasHighlight
            Dim splitMidtone = chain.SplitHasMidtone
            Dim splitGlobal = chain.SplitHasGlobal
            Dim splitLumShift = chain.SplitHasLuminance
            Dim splitBlendExp = chain.SplitBlendExponent
            Dim pm = chain.PresetMatrix
            Dim pmStrength = chain.PresetStrength
            Dim cube = chain.CubeTable
            Dim cubeSize = chain.CubeSize
            Dim cubeStrength = chain.CubeStrength

            ForEachRow(width, source.Height,
                Sub(y)
                    Dim rowOffset = y * stride
                    Dim dstRow = y * dstStride
                    Dim ditherRow = (y And 7) << 3
                    For x = 0 To width - 1
                        Dim o = rowOffset + x * 4
                        Dim d = dstRow + x * 4
                        Dim a = srcBuf(o + ai)

                        ' Vollstaendig transparent: RGB bleibt 0. Ohne diesen Zweig erfinden die
                        ' Farbstufen dort Farbe, und A=0 mit RGB>0 ist ein ungueltiger
                        ' Premul-Zustand, der beim Ueberblenden als Farbsaum durchschlaegt.
                        If a = 0 Then
                            dstBuf(d) = 0 : dstBuf(d + 1) = 0 : dstBuf(d + 2) = 0 : dstBuf(d + 3) = 0
                            Continue For
                        End If

                        Dim rr As Single, gg As Single, bb As Single
                        If a = 255 OrElse Not srcPremul Then
                            ' Deckend, oder die Werte liegen ohnehin schon unvormultipliziert vor.
                            rr = srcBuf(o + ri) / 255.0F
                            gg = srcBuf(o + gi) / 255.0F
                            bb = srcBuf(o + bi) / 255.0F
                        Else
                            Dim inv = 1.0F / a
                            rr = srcBuf(o + ri) * inv
                            gg = srcBuf(o + gi) * inv
                            bb = srcBuf(o + bi) * inv
                            If rr > 1.0F Then rr = 1.0F
                            If gg > 1.0F Then gg = 1.0F
                            If bb > 1.0F Then bb = 1.0F
                        End If

                        ' --- 1. Filmnegativ (vor allen Farbanpassungen) ---
                        If negR IsNot Nothing Then
                            rr = SampleTable(negR, rr)
                            gg = SampleTable(negG, gg)
                            bb = SampleTable(negB, bb)
                            If negMono Then
                                ' Erst kanalweise auf die eigene Basis normiert (nimmt auch dem
                                ' S/W-Traeger seinen Eigenfarbton), DANN entsaettigen - wie die
                                ' Compose-Reihenfolge der Altstufe (Tabelle innen, Graumatrix aussen).
                                Dim gray = 0.299F * rr + 0.587F * gg + 0.114F * bb
                                rr = gray : gg = gray : bb = gray
                            End If
                        End If

                        ' --- 2. Farbmatrix. Skia klemmt danach auf [0,1] (gemessen) - hier ebenso. ---
                        If m IsNot Nothing Then
                            Dim nr = m(0) * rr + m(1) * gg + m(2) * bb + m(4)
                            Dim ng = m(5) * rr + m(6) * gg + m(7) * bb + m(9)
                            Dim nb = m(10) * rr + m(11) * gg + m(12) * bb + m(14)
                            rr = If(nr < 0.0F, 0.0F, If(nr > 1.0F, 1.0F, nr))
                            gg = If(ng < 0.0F, 0.0F, If(ng > 1.0F, 1.0F, ng))
                            bb = If(nb < 0.0F, 0.0F, If(nb > 1.0F, 1.0F, nb))
                        End If

                        ' --- 3. Verschmolzene Skalarkette (Ton + Tonwerte + RGB-/Kanalkurven) ---
                        If sr IsNot Nothing Then
                            rr = SampleTable(sr, rr)
                            gg = SampleTable(sg, gg)
                            bb = SampleTable(sb, bb)
                        End If

                        ' --- 4./5./6. Luminanzkurve, HSL-Baender, Split-Toning ---
                        ' Alle drei brauchen HSL. Der Roundtrip wird deshalb EINMAL gemacht statt
                        ' dreimal - in der alten Pipeline lief er pro Stufe erneut, jedes Mal ueber
                        ' Bytes und mit eigener Rundung.
                        If lumCurve IsNot Nothing OrElse hslAdj IsNot Nothing OrElse splitAdj IsNot Nothing Then
                            Dim h As Double, sat As Double, lum As Double
                            RgbToHslF(rr, gg, bb, h, sat, lum)

                            If lumCurve IsNot Nothing Then
                                lum = SampleTable(lumCurve, Clamp(CSng(lum), 0.0F, 1.0F))
                            End If

                            If hslAdj IsNot Nothing Then
                                Dim hueShift As Single = 0, satShift As Single = 0, lumShift As Single = 0
                                GetHslBandAdjustments(h, hslAdj, hueShift, satShift, lumShift)
                                h = (h + hueShift + 360.0) Mod 360.0
                                sat = Math.Max(0.0, Math.Min(1.0, sat * (1.0 + satShift / 100.0)))
                                ' Luminanz multiplikativ wie die Saettigung; graue Pixel haben keinen
                                ' Farbton und bleiben unberuehrt.
                                lum = Math.Max(0.0, Math.Min(1.0, lum * (1.0 + lumShift / 100.0)))
                            End If

                            ' --- Farbgradierung: Zonengewichte ---
                            ' Schatten und Lichter sind zwei lineare Rampen, die sich am Pivot treffen;
                            ' die MITTEN sind bewusst als REST definiert (1 - Schatten - Lichter) statt
                            ' als eigene Kurve. Dadurch summieren sich die drei Zonen fuer jeden
                            ' Exponenten exakt auf 1, und bei Mitten-Saettigung 0 rechnet die Kette
                            ' bitgenau wie das frueher hier stehende Split-Toning - der Umbau ist fuer
                            ' bestehende Bilder wirkungsneutral (siehe Diagnose-Pruefung dazu).
                            Dim wShadow As Single = 0.0F, wMid As Single = 0.0F, wHigh As Single = 0.0F
                            If splitAdj IsNot Nothing Then
                                wShadow = Clamp(CSng((splitPivot - lum) / splitPivot), 0.0F, 1.0F)
                                wHigh = Clamp(CSng((lum - splitPivot) / (1.0 - splitPivot)), 0.0F, 1.0F)
                                If splitBlendExp <> 1.0F Then
                                    wShadow = CSng(Math.Pow(wShadow, splitBlendExp))
                                    wHigh = CSng(Math.Pow(wHigh, splitBlendExp))
                                End If
                                wMid = Clamp(1.0F - wShadow - wHigh, 0.0F, 1.0F)

                                ' Luminanz je Zone: noch VOR der Rueckrechnung nach RGB, weil wir hier
                                ' ohnehin im HSL-Raum stehen. Die Gewichte stammen aus dem Wert VOR der
                                ' Verschiebung - sonst wanderte ein Pixel beim Aufhellen in die naechste
                                ' Zone und tuente sich selbst um.
                                If splitLumShift Then
                                    Dim shift = (wShadow * splitAdj.ColorGradeShadowLuminance +
                                                 wMid * splitAdj.ColorGradeMidtoneLuminance +
                                                 wHigh * splitAdj.ColorGradeHighlightLuminance +
                                                 splitAdj.ColorGradeGlobalLuminance) / 100.0F
                                    lum = Math.Max(0.0, Math.Min(1.0, lum + shift * 0.5))
                                End If
                            End If

                            Dim hr As Double, hg As Double, hb As Double
                            HslToRgbF(h, sat, lum, hr, hg, hb)
                            rr = CSng(hr) : gg = CSng(hg) : bb = CSng(hb)

                            If splitAdj IsNot Nothing Then
                                ' Die Altstufe rechnet in 0..255; hier auf 0..1 normiert, sonst
                                ' stimmt der Anteil nicht.
                                If splitShadow Then
                                    ApplyColorGradeTint(rr, gg, bb, wShadow, splitAdj.ColorGradeShadowHue,
                                                        splitAdj.ColorGradeShadowSaturation, lum)
                                End If
                                If splitMidtone Then
                                    ApplyColorGradeTint(rr, gg, bb, wMid, splitAdj.ColorGradeMidtoneHue,
                                                        splitAdj.ColorGradeMidtoneSaturation, lum)
                                End If
                                If splitHighlight Then
                                    ApplyColorGradeTint(rr, gg, bb, wHigh, splitAdj.ColorGradeHighlightHue,
                                                        splitAdj.ColorGradeHighlightSaturation, lum)
                                End If
                                ' Global wirkt ueberall gleich stark - Gewicht 1, keine Zonengrenze.
                                If splitGlobal Then
                                    ApplyColorGradeTint(rr, gg, bb, 1.0F, splitAdj.ColorGradeGlobalHue,
                                                        splitAdj.ColorGradeGlobalSaturation, lum)
                                End If
                                rr = Clamp(rr, 0.0F, 1.0F)
                                gg = Clamp(gg, 0.0F, 1.0F)
                                bb = Clamp(bb, 0.0F, 1.0F)
                            End If
                        End If

                        ' --- Schattentoenung (Kalibrierung): Gruen/Magenta nur in den Tiefen ---
                        If schattenToenung <> 0.0F Then
                            Dim helligkeit = 0.299F * rr + 0.587F * gg + 0.114F * bb
                            ' Gewicht faellt linear bis zur Bildmitte auf 0 - darueber unberuehrt.
                            Dim gewicht = Math.Max(0.0F, 1.0F - helligkeit * 2.0F)
                            If gewicht > 0.0F Then
                                Dim staerke = schattenToenung / 100.0F * 0.12F * gewicht
                                ' Positiv = Magenta (Gruen runter), negativ = Gruen. Wie in Lightroom.
                                gg = Clamp(gg - staerke, 0.0F, 1.0F)
                                rr = Clamp(rr + staerke * 0.5F, 0.0F, 1.0F)
                                bb = Clamp(bb + staerke * 0.5F, 0.0F, 1.0F)
                            End If
                        End If

                        ' --- 7. Preset-Farbmatrix, ueber das Original geblendet ---
                        If pm IsNot Nothing Then
                            Dim fr = pm(0) * rr + pm(1) * gg + pm(2) * bb + pm(4)
                            Dim fg = pm(5) * rr + pm(6) * gg + pm(7) * bb + pm(9)
                            Dim fb = pm(10) * rr + pm(11) * gg + pm(12) * bb + pm(14)
                            ' Skia klemmt die Matrixausgabe, BEVOR sie ueberblendet wird - ohne diese
                            ' Klemmung zoege ein ueberschiessendes Preset das Original mit hoch.
                            fr = Clamp(fr, 0.0F, 1.0F)
                            fg = Clamp(fg, 0.0F, 1.0F)
                            fb = Clamp(fb, 0.0F, 1.0F)
                            rr += (fr - rr) * pmStrength
                            gg += (fg - gg) * pmStrength
                            bb += (fb - bb) * pmStrength
                        End If

                        ' --- 8. Cube-LUT ---
                        If cube IsNot Nothing Then
                            Dim lr = rr, lg = gg, lb = bb
                            SampleCubeLutF(cube, cubeSize, lr, lg, lb)
                            If cubeStrength >= 0.999F Then
                                rr = lr : gg = lg : bb = lb
                            Else
                                rr += (lr - rr) * cubeStrength
                                gg += (lg - gg) * cubeStrength
                                bb += (lb - bb) * cubeStrength
                            End If
                        End If

                        Dim dth = DitherMatrix(ditherRow Or (x And 7))
                        Dim outR = QuantizeDithered(rr, dth)
                        Dim outG = QuantizeDithered(gg, dth)
                        Dim outB = QuantizeDithered(bb, dth)

                        ' Nur zurueck nach Premultipliziert, wenn die AUSGABE premultipliziert ist.
                        ' Bei einer Unpremul-Ausgabe bleiben die Werte die Farbe selbst.
                        If a <> 255 AndAlso srcPremul Then
                            ' Nie groesser als Alpha, sonst ist der Premul-Zustand ungueltig.
                            Dim af = a / 255.0F
                            outR = CByte(Math.Min(CInt(a), CInt(outR * af)))
                            outG = CByte(Math.Min(CInt(a), CInt(outG * af)))
                            outB = CByte(Math.Min(CInt(a), CInt(outB * af)))
                        End If

                        dstBuf(d + ri) = outR
                        dstBuf(d + gi) = outG
                        dstBuf(d + bi) = outB
                        dstBuf(d + ai) = a
                    Next
                End Sub)

            Marshal.Copy(dstBuf, 0, result.GetPixels(), dstBuf.Length)
            Return result
        End Function

    End Class

End Namespace
