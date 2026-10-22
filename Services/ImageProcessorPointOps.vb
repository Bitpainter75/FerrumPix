Imports System
Imports System.Buffers
Imports System.Linq
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Die verschmolzene Gleitkomma-Tonwertkette.
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
    '''     Tabelle (gemessen). Sie wegzulassen aendert das Bild sichtbar - die
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

            ''' <summary>Nur bei Lichter/Tiefen/Weiss/Schwarz gesetzt, dann ist die Skalarkette
            ''' dreigeteilt: ToneBefore je Kanal (Belichtung, Kontrast, Helligkeit; Nothing, wenn
            ''' neutral), ToneZones ueber die Helligkeit des Pixels, danach ScalarR/G/B mit den
            ''' Kurven. Ohne die vier Regler bleibt alles in ScalarR/G/B verschmolzen.</summary>
            Public ToneBefore As Single()
            Public ToneZones As Single()

            ''' <summary>Lichter und Tiefen allein als Tabelle, ausgewertet nicht am Bildpunkt, sondern
            ''' an der Basisschicht seiner Umgebung (LocalToneBase, gebaut im Durchlauf aus dem Bild
            ''' selbst). ToneZones haelt dann die gemeinsame Tabelle aller fuenf Regler abzueglich
            ''' dieses Hubs. Nothing ohne Lichter und Tiefen.</summary>
            Public LocalToneZones As Single()

            ''' <summary>Um wie viel Lichter und Tiefen die Feinstruktur der Helligkeit zusaetzlich
            ''' verstaerken (Faktor minus 1), ueber dem Grauwert der geglaetteten Umgebung
            ''' (ToneSliderCurves.LuminanceDetailGain). Nothing, wenn kein Faktor von 1 abweicht.</summary>
            Public LocalDetailGain As Single()

            ''' <summary>Ebenso fuer den Abstand der Kanaele vom Grau, also die Buntheit
            ''' (ToneSliderCurves.DetailGain). Nothing, wenn kein Faktor von 1 abweicht.</summary>
            Public LocalColorGain As Single()

            ''' <summary>Weissabgleich als chromatische Adaption, 3x3 zeilenweise, ODER Nothing.
            ''' Nothing ist der Normalfall: Modell 1 rechnet den Weissabgleich weiter in der
            ''' Farbmatrix, und auch unter Modell 2 steht der Regler meist auf dem Anker.
            '''
            ''' Eigene Stufe und nicht in die Farbmatrix gefaltet, weil sie im LINEARLICHT rechnet:
            ''' sie dekodiert, multipliziert und kodiert wieder. Zusammenfalten liesse sich nur mit
            ''' einer Matrix, die im selben Raum arbeitet.</summary>
            Public WhiteBalanceMatrix As Single()

            ''' Farbmatrix (Temperatur/Toenung/Saettigung), 20 Eintraege wie bei Skia.
            ''' Die Offset-Spalte liegt hier bereits in 0..1 vor. Dynamik liegt NICHT mehr hier -
            ''' sie ist chroma-abhaengig und damit keine lineare Matrix (siehe Vibrance).
            Public ColorMatrix As Single()

            ''' Dynamik (Vibrance) als -1..1. Echte, chroma-gewichtete Saettigung: schwach saturierte
            ''' Pixel werden staerker angehoben als bereits kraeftige, ein neutrales Grau (sat=0) bleibt
            ''' neutral. Deshalb per-Pixel im HSL-Raum statt in der Farbmatrix - eine lineare Matrix
            ''' koennte die Chroma-Abhaengigkeit nicht abbilden und wuerde nur wie eine zweite
            ''' Saettigung wirken (und liesse sich von der Saettigung nicht mehr auf Grau ziehen).
            Public Vibrance As Single
            ''' Negative Dynamik: sat * exp(Vibrance * (1 - sat)) statt der linearen Formel. Die
            ''' lineare schlaegt auf Referenzstaerke schon bei -50 an der Null an, und -100 brachte
            ''' nichts mehr dazu.
            Public VibranceExponential As Boolean
            ''' Positive Dynamik je Farbton: v der Formel sat * (1 + v * (1 - sat)) fuer 18 Felder
            ''' zu 20 Grad, Mitte bei 10, 30, ... 350 Grad (siehe MeasuredVibranceByHue). Nothing bei
            ''' negativer Dynamik, die ueber den Farbton gleich wirkt.
            Public VibranceByHue As Single()
            ''' ScalarR/G/B halten nur die Kurven und wirken in ProPhoto-Primaerfarben mit
            ''' sRGB-Gammakurve (ApplyCurvesInMelissa); die Tonwertkurve steht dann in ToneBefore.
            Public CurvesInMelissa As Boolean

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
            ''' Die wirksamen Farbfelder der Punktfarbe, oder Nothing (PointColorMath).
            Public PointColors As PointColorMath.Prepared()
            ''' Die Farbgradierung, oder Nothing. Die Toenung ist ein Versatz in Y/Cb/Cr je Zone
            ''' (ApplyColorGrade) statt einer Mischung zur Tonfarbe. SplitOffsets haelt je Zone
            ''' (Tiefen, Mitten, Lichter, Global) dY, dCb, dCr bei vollem Gewicht.
            Public SplitToning As ImageAdjustments
            Public SplitOffsets As Single()
            ''' Zonengewichte von Tiefen und Lichtern nach Ueberblendung und Balance, und der
            ''' Helligkeitsversatz aller vier Zonen, je 20 Stufen ueber Y (BuildColorGradeTables).
            Public SplitShadowTable As Single()
            Public SplitHighTable As Single()
            Public SplitLumTable As Single()

            ''' Gruen-/Magenta-Verschiebung nur in den Tiefen (crs:ShadowTint). Luminanzabhaengig,
            ''' laesst sich also nicht in die Matrix falten.
            Public ShadowTint As Single

            ''' Preset-Farbmatrix samt Ueberblendstaerke (das Preset wird ueber das Original geblendet).
            Public PresetMatrix As Single()
            Public PresetStrength As Single

            ''' Die Farbgradierung erst NACH der Preset-Matrix: gesetzt, wenn die Matrix ein reines
            ''' Grau ergibt (S/W). Sonst rechnete die Matrix jede Tonung wieder auf Grau, und wer ein
            ''' S/W-Bild tonen wollte, sah im Bild nichts davon. Siehe BuildPointOpChain.
            Public ToneAfterPreset As Boolean

            ''' 3D-Cube-LUT mit trilinearer Interpolation.
            Public CubeTable As Single()
            Public CubeSize As Integer
            Public CubeStrength As Single

            ''' Kanalmixer als 3x4 zeilenweise (Anteile und Konstante, schon durch 100 geteilt).
            Public Mixer As Single()

            ''' Verlaufsumsetzung: Tiefen- und Lichterfarbe 0..1, Staerke 0..1.
            Public MapShadow As Single()
            Public MapHighlight As Single()
            Public MapAmount As Single

            ''' Tontrennung (Stufen, 0 = aus) und Schwellenwert (1..255, 0 = aus), ganz am Ende.
            Public PosterizeLevels As Integer
            Public ThresholdLevel As Integer
        End Class

        ' ── Aufsatzpunkt ─────────────────────────────────────────────────────────

        ''' <summary>Wendet die verschmolzene Kette an. Gibt <paramref name="source"/> unveraendert
        ''' zurueck, wenn nichts zu tun ist - ReplaceBitmap erkennt Referenzgleichheit und disposed
        ''' dann nicht.</summary>
        ''' <param name="measured">Quelle fuer die Filmnegativ-Messung. Die Basis-/Dichtefarbe wird am
        ''' UNVERAENDERTEN Bild gemessen; seit die Umkehr Teil der Kette ist, muss das ausdruecklich
        ''' dasselbe Bitmap sein, das auch in die Kette geht.</param>
        ''' <param name="toneReference">Helligkeit des GANZEN Bildes, nach der Lichter, Tiefen und
        ''' Weiss ihre Kennlinie waehlen. Ungemessen heisst: an <paramref name="source"/> messen.
        ''' Gesetzt nur, wo die Kette auf einem Ausschnitt laeuft (Maskenebene im Rechteck) - dort
        ''' waeren Median und Spitze andere und die Ebene wirkte innen anders als ueber das ganze
        ''' Bild.</param>
        Friend Shared Function ApplyPointOpChain(source As SKBitmap, adj As ImageAdjustments,
                                                 Optional toneReference As ToneReference = Nothing) As SKBitmap
            If source Is Nothing OrElse adj Is Nothing Then Return source
            Dim chain = BuildPointOpChain(adj, source, toneReference)
            If chain.IsIdentity Then Return source
            Return RunPointOpChain(source, chain)
        End Function

        ''' <summary>Baut den Ketten-Zustand. Die Frueh-Ausstiege der Altfunktionen werden hier exakt
        ''' nachgebildet: ein neutraler Regler muss weiterhin GAR NICHTS tun, sonst kostet die Kette
        ''' Zeit, wo heute nur ein If steht.</summary>
        Friend Shared Function BuildPointOpChain(adj As ImageAdjustments,
                                                 Optional measured As SKBitmap = Nothing,
                                                 Optional toneReference As ToneReference = Nothing) As PointOpChain
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

            ' --- Weissabgleich als Adaption (Modell 2), NACH dem Filmnegativ und VOR der Farbmatrix ---
            ' Reihenfolge wie beim Filmnegativ begruendet: der Weissabgleich gehoert an den Anfang
            ' der Farbbearbeitung, alles danach soll auf dem abgeglichenen Bild arbeiten. Unter
            ' Modell 1 liefert der Bauer Nothing, und die Kette ist dann bitgleich wie bisher.
            chain.WhiteBalanceMatrix = BuildWhiteBalanceMatrix(adj)
            If chain.WhiteBalanceMatrix IsNot Nothing Then
                EnsureGammaTables()
                chain.IsIdentity = False
            End If

            ' --- Farbmatrix: exakt die Bedingung aus ApplyColorAdjustments, nur OHNE Dynamik ---
            ' Dynamik ist keine Matrixstufe mehr (chroma-abhaengig, siehe unten), also nicht mehr hier.
            ' Die Belichtung zaehlt nur, soweit sie nicht schon im Quellbild steckt (ExposureInSource).
            Dim wantsColor = adj.ChainExposure() <> 0 OrElse adj.Temperature <> 0 OrElse adj.Tint <> 0 OrElse
                             adj.Saturation <> 0 OrElse adj.Contrast <> 0 OrElse
                             adj.Brightness <> 0
            ' Kalibrierung wirkt auf die Primaerfarben und gehoert damit VOR Weissabgleich und
            ' Saettigung - dieselbe Reihenfolge wie ueblich. Beide Matrizen werden zu EINER
            ' verrechnet, der Pixeldurchlauf bleibt also unveraendert schnell.
            Dim kalibrierung = BuildCalibrationMatrix(adj)
            If wantsColor OrElse kalibrierung IsNot Nothing Then
                Dim wb = If(wantsColor, BuildPointOpColorMatrix(adj), Nothing)
                chain.ColorMatrix = ComposeColorMatrix(wb, kalibrierung)
                chain.IsIdentity = False
            End If

            ' --- Dynamik (Vibrance): eigene, chroma-gewichtete Stufe im HSL-Raum ---
            ' Laeuft NACH der Saettigungsmatrix: eine per Saettigung=-100 auf Grau gezogene Flaeche hat
            ' dann sat=0 und bleibt grau, egal wie hoch die Dynamik steht - genau das Verhalten, das
            ' die flache Alt-Fusion (Dynamik einfach zu sat addiert) verhinderte.
            If adj.Vibrance <> 0 Then
                chain.Vibrance = MeasuredVibrance(adj.Vibrance)
                chain.VibranceExponential = adj.Vibrance < 0
                If adj.Vibrance > 0 Then
                    chain.VibranceByHue = MeasuredVibranceByHue(adj.Vibrance)
                    ' Die gehaltene Leuchtdichte rechnet linear (siehe die Pixelschleife).
                    EnsureGammaTables()
                End If
                chain.IsIdentity = False
            End If

            ' --- Skalarkette: Tonwertkurve und Kurven in EINER Tabelle ---
            ' Die Bedingungen bleiben getrennt (wie die Frueh-Ausstiege der Altfunktionen), die
            ' AUSWERTUNG wird verschmolzen: v durchlaeuft beide Formeln stetig, ohne Zwischenrundung.
            ' Genau hier verschwindet eine der 8-Bit-Stufen.
            ' Der Kontrast gehoert zu den Zonen (gemessene Kennlinie, farbtonerhaltend) und nicht
            ' zur Tonwertkurve.
            Dim wantsTone = adj.ChainExposure() <> 0 OrElse adj.Brightness <> 0 OrElse adj.ChainFollowsRawKnee()
            Dim wantsTonal = adj.Highlights <> 0 OrElse adj.ShadowsLevel <> 0 OrElse
                             adj.Whites <> 0 OrElse adj.Blacks <> 0 OrElse adj.Contrast <> 0
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
                ' Lichter/Tiefen/Weiss/Schwarz wirken FARBTONERHALTEND, nicht je Kanal: je Kanal
                ' rueckte der mittlere Kanal dorthin, wo die Kennlinie flach wird, und ein Orange
                ' kippte bei Lichter -100 / Tiefen +100 ins Gelbgraue (Forum pixls.us, 2026-10-07).
                ' Dafuer muss die Kette an dieser Stelle aufgetrennt werden (ApplyToneZones).
                ' Punkt- und Kanalkurven rechnen im Raum der Referenz: ProPhoto-Primaerfarben mit
                ' sRGB-Gammakurve (CurvesInMelissa). Die Tonwertkurve davor bleibt im sRGB-Gamma, die
                ' Kette wird dafuer auch ohne Tonregler aufgetrennt.
                Dim curvesInMelissa = wantsRgbCurve OrElse wantsChannelCurves
                If wantsTonal OrElse curvesInMelissa Then
                    If wantsTone Then chain.ToneBefore = BuildPointOpScalarTable(adj, True, False)
                    If wantsTonal Then
                        ' Lichter und Tiefen haengen an der Helligkeit des ganzen Bildes, Weiss an
                        ' seiner Spitze (ToneReference). Gemessen VOR der Kette, dann durch die
                        ' Tonwertkurve davor geschickt: Belichtung verschiebt sie mit. Bei einem
                        ' Filmnegativ wird das Positiv gemessen, das bei den Tonzonen ankommt.
                        Dim reference As ToneReference = Nothing
                        If adj.ReadsImageStatistics() Then
                            reference = If(toneReference.IsMeasured, toneReference,
                                           MeasureToneReference(measured, chain))
                            If reference.IsMeasured AndAlso chain.ToneBefore IsNot Nothing Then
                                Dim toneBefore = chain.ToneBefore
                                reference.MedianL = GrayVToLightness(SampleTable(toneBefore, CSng(LightnessToGrayV(reference.MedianL))))
                                reference.TopL = GrayVToLightness(SampleTable(toneBefore, CSng(LightnessToGrayV(reference.TopL))))
                                ' Der Anteil heller Bildpunkte wandert ebenso mit: gezaehlt wird, was
                                ' NACH der Stufe ueber der Grenze liegt.
                                reference.BrightShare = reference.ShareAboveAfter(
                                    Function(l) GrayVToLightness(SampleTable(toneBefore, CSng(LightnessToGrayV(l)))),
                                    ToneSliderCurves.BrightShareLimitL)
                            End If
                        End If
                        ' Lichter und Tiefen in eine eigene Tabelle, die an der Basisschicht
                        ' ausgewertet wird; Kontrast, Weiss und Schwarz bleiben am Bildpunkt. So
                        ' rechnet die Referenz (Messung in RAW_UND_FARBE.md, Grundton).
                        If adj.UsesLocalToneZones() Then
                            EnsureGammaTables()
                            chain.LocalToneZones = BuildToneZoneTable(adj, reference, ToneZonePart.Local)
                            chain.LocalDetailGain = BuildLocalDetailTable(adj, True)
                            chain.LocalColorGain = BuildLocalDetailTable(adj, False)
                            ' Die Tabelle am Bildpunkt ist die gemeinsame monotone Tabelle aller fuenf
                            ' Regler MINUS dem oertlichen Hub. Wo Basis und Bildpunkt gleich sind (glatte
                            ' Flaeche, Verlauf), kommt so genau die gemeinsame Tabelle heraus, samt
                            ' deren Schutz gegen Tonumkehr. Zwei fuer sich monotone Teile waeren
                            ' zusammen nicht monoton: Weiss -100 und Lichter -100 flachen beide die
                            ' Lichter ab.
                            Dim combined = BuildToneZoneTable(adj, reference)
                            For i = 0 To combined.Length - 1
                                combined(i) -= chain.LocalToneZones(i) - i / CSng(PointOpTableSize - 1)
                            Next
                            chain.ToneZones = combined
                        Else
                            chain.ToneZones = BuildToneZoneTable(adj, reference)
                        End If
                    End If
                End If
                If curvesInMelissa Then
                    chain.CurvesInMelissa = True
                    EnsureGammaTables()
                End If
                Dim common = BuildPointOpScalarTable(adj, wantsTone AndAlso Not wantsTonal AndAlso Not curvesInMelissa, wantsRgbCurve)
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
                chain.Hsl = ReferenceHslLuminance(adj)
                chain.IsIdentity = False
            End If

            ' --- Punktfarbe: direkt nach dem Farbmischer, wie beim Referenzprogramm ---
            Dim pointColors = PointColorMath.Prepare(adj.PointColors)
            If pointColors IsNot Nothing Then
                chain.PointColors = pointColors
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
                chain.SplitToning = adj
                ' Ueberblendung, Balance und die Helligkeit der Zonen stecken in den Tabellen.
                BuildColorGradeTables(adj, chain)
                chain.SplitOffsets = New Single(11) {}
                GradeVector(If(hasShadow, adj.ColorGradeShadowHue, 0), If(hasShadow, adj.ColorGradeShadowSaturation, 0), chain.SplitOffsets, 0)
                GradeVector(If(hasMidtone, adj.ColorGradeMidtoneHue, 0), If(hasMidtone, adj.ColorGradeMidtoneSaturation, 0), chain.SplitOffsets, 3)
                GradeVector(If(hasHighlight, adj.ColorGradeHighlightHue, 0), If(hasHighlight, adj.ColorGradeHighlightSaturation, 0), chain.SplitOffsets, 6)
                GradeVector(If(hasGlobal, adj.ColorGradeGlobalHue, 0), If(hasGlobal, adj.ColorGradeGlobalSaturation, 0), chain.SplitOffsets, 9)
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
                    ' Ein S/W-Look rechnet jede Farbe auf Grau, auch die der Farbgradierung davor.
                    ' Die Tonung wandert deshalb hinter die Matrix; alles andere zwischen beiden
                    ' Stufen (Kanalmixer, Verlaufsumsetzung, Schattentoenung) bleibt, wo es ist,
                    ' und wirkt aufs Graubild wie bisher.
                    chain.ToneAfterPreset = chain.SplitToning IsNot Nothing AndAlso IsGrayMatrix(m)
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

            BuildExtraCorrections(adj, chain)
            Return chain
        End Function

        ''' <summary>Die weiteren Korrekturen (Kanalmixer, Verlaufsumsetzung, Tontrennung,
        ''' Schwellenwert). Jede nur, wenn sie vom Werkszustand
        ''' abweicht - ein neutraler Regler kostet keine Zeit.</summary>
        Private Shared Sub BuildExtraCorrections(adj As ImageAdjustments, chain As PointOpChain)
            If adj.HasChannelMixerChanges() Then
                If adj.ChannelMixerMonochrome Then
                    Dim row = {adj.ChannelMixerGrayRed / 100.0F, adj.ChannelMixerGrayGreen / 100.0F,
                               adj.ChannelMixerGrayBlue / 100.0F, adj.ChannelMixerGrayConstant / 100.0F}
                    chain.Mixer = row.Concat(row).Concat(row).ToArray()
                Else
                    chain.Mixer = {adj.ChannelMixerRedRed / 100.0F, adj.ChannelMixerRedGreen / 100.0F, adj.ChannelMixerRedBlue / 100.0F, adj.ChannelMixerRedConstant / 100.0F,
                                   adj.ChannelMixerGreenRed / 100.0F, adj.ChannelMixerGreenGreen / 100.0F, adj.ChannelMixerGreenBlue / 100.0F, adj.ChannelMixerGreenConstant / 100.0F,
                                   adj.ChannelMixerBlueRed / 100.0F, adj.ChannelMixerBlueGreen / 100.0F, adj.ChannelMixerBlueBlue / 100.0F, adj.ChannelMixerBlueConstant / 100.0F}
                End If
                chain.IsIdentity = False
            End If

            If adj.GradientMapAmount > 0 Then
                Dim s = ParseColor(adj.GradientMapShadowColor, SKColors.Black)
                Dim h = ParseColor(adj.GradientMapHighlightColor, SKColors.White)
                chain.MapShadow = {s.Red / 255.0F, s.Green / 255.0F, s.Blue / 255.0F}
                chain.MapHighlight = {h.Red / 255.0F, h.Green / 255.0F, h.Blue / 255.0F}
                chain.MapAmount = Clamp(adj.GradientMapAmount / 100.0F, 0.0F, 1.0F)
                chain.IsIdentity = False
            End If

            If adj.PosterizeLevels >= 2 Then
                chain.PosterizeLevels = CInt(Math.Min(64.0F, Math.Round(adj.PosterizeLevels)))
                chain.IsIdentity = False
            End If

            If adj.ThresholdLevel >= 1 Then
                chain.ThresholdLevel = CInt(Math.Min(255.0F, Math.Round(adj.ThresholdLevel)))
                chain.IsIdentity = False
            End If
        End Sub

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
                    Dim angle = hue / 100.0 * 30.0 * Math.PI / 180.0
                    Dim c = Math.Cos(angle), sn = Math.Sin(angle)
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
                    Dim factor = 1.0F + sat / 100.0F
                    Dim grau = 0.299F * r + 0.587F * g + 0.114F * b
                    Return New Single() {grau + (r - grau) * factor,
                                         grau + (g - grau) * factor,
                                         grau + (b - grau) * factor}
                End Function

            Dim pr = Wandeln(New Single() {1, 0, 0}, adj.CalibrationRedHue, adj.CalibrationRedSaturation)
            Dim pg = Wandeln(New Single() {0, 1, 0}, adj.CalibrationGreenHue, adj.CalibrationGreenSaturation)
            Dim pb = Wandeln(New Single() {0, 0, 1}, adj.CalibrationBlueHue, adj.CalibrationBlueSaturation)

            ' Spalten sind die gewandelten Primaerfarben - Skia-Layout (5 Spalten je Zeile).
            ' ZEILENSUMMEN AUF 1 NORMIEREN: ohne das bleibt Neutralgrau nicht neutral. Grau ist
            ' (1,1,1); die Matrix bildet es auf die SUMME der drei Primaerfarben ab, und sobald auch
            ' nur eine gedreht wurde, ist die Summe nicht mehr grau. Gemessen wurde aus (128,128,128)
            ' ein (116,170,96) - ein deutlicher Gruenstich auf jeder neutralen Flaeche.
            Dim rows = {
                New Single() {pr(0), pg(0), pb(0)},
                New Single() {pr(1), pg(1), pb(1)},
                New Single() {pr(2), pg(2), pb(2)}
            }
            For Each row In rows
                Dim sum = row(0) + row(1) + row(2)
                If Math.Abs(sum) > 0.0001F Then
                    row(0) /= sum : row(1) /= sum : row(2) /= sum
                End If
            Next

            Return New Single() {
                rows(0)(0), rows(0)(1), rows(0)(2), 0, 0,
                rows(1)(0), rows(1)(1), rows(1)(2), 0, 0,
                rows(2)(0), rows(2)(1), rows(2)(2), 0, 0,
                0, 0, 0, 1, 0
            }
        End Function

        ''' <summary>Verkettet zwei Skia-Farbmatrizen zu einer (erst <paramref name="innen"/>, dann
        ''' <paramref name="aussen"/>). So kostet die Kalibrierung KEINEN zweiten Durchlauf pro
        ''' Pixel - sie wird einmal beim Bauen in die vorhandene Matrix hineingerechnet.</summary>
        Private Shared Function ComposeColorMatrix(outer As Single(), inner As Single()) As Single()
            If outer Is Nothing Then Return inner
            If inner Is Nothing Then Return outer
            Dim r = New Single(19) {}
            For row = 0 To 3
                For column = 0 To 3
                    Dim sum = 0.0F
                    For k = 0 To 3
                        sum += outer(row * 5 + k) * inner(k * 5 + column)
                    Next
                    r(row * 5 + column) = sum
                Next
                ' Offset-Spalte: aussen wirkt auf die Offsets von innen, plus eigener Offset.
                Dim off = outer(row * 5 + 4)
                For k = 0 To 3
                    off += outer(row * 5 + k) * inner(k * 5 + 4)
                Next
                r(row * 5 + 4) = off
            Next
            Return r
        End Function

        ''' <summary>Die Adaptionsmatrix fuer diese Anpassungen, oder Nothing.
        '''
        ''' Nothing heisst: keine Stufe rechnen. Das gilt fuer Modell 1 (dort steckt der
        ''' Weissabgleich in der Farbmatrix) und fuer jeden Regler, der auf dem Anker steht.
        '''
        ''' ZWEI REGLERFORMEN, wie bei Adobe: eine absolute Kelvin-Zahl gilt fuer RAW-Dateien und
        ''' nennt das Licht der Szene; ein relativer Wert verschiebt vom Anker aus und gilt fuer
        ''' alles andere. Ist beides gesetzt, gewinnt die absolute Zahl - sie ist die genauere
        ''' Aussage.</summary>
        Friend Shared Function BuildWhiteBalanceMatrix(adj As ImageAdjustments) As Single()
            If adj Is Nothing OrElse adj.WhiteBalanceModel < 2 Then Return Nothing

            Dim anchor = WhiteBalanceAnchorOf(adj)
            Dim claimed = ClaimedWhitePointOf(adj)
            Dim m = WhiteBalanceAdaptation.BuildMatrix(anchor, claimed)
            If m Is Nothing Then Return Nothing
            Dim result(8) As Single
            For i = 0 To 8
                result(i) = CSng(m(i))
            Next
            Return result
        End Function

        ''' <summary>Der Anker dieser Anpassungen: das Licht der Aufnahme, oder D65 fuer alles ohne
        ''' Aufnahme-Weissabgleich.</summary>
        Public Shared Function WhiteBalanceAnchorOf(adj As ImageAdjustments) As WhitePoint
            If adj Is Nothing Then Return WhiteBalanceAdaptation.D65
            If adj.WhiteBalanceAnchorX > 0.0 AndAlso adj.WhiteBalanceAnchorY > 0.0 Then
                Return New WhitePoint(adj.WhiteBalanceAnchorX, adj.WhiteBalanceAnchorY)
            End If
            Return WhiteBalanceAdaptation.D65
        End Function

        ''' <summary>Das Licht, das die Regler BEHAUPTEN. Der Anker selbst, wenn keiner der Regler
        ''' etwas sagt - dann ist die Matrix spaeter die Einheitsmatrix.
        '''
        ''' ZWEI REGLERFORMEN, wie bei Adobe: eine absolute Kelvin-Zahl gilt fuer RAW-Dateien und
        ''' nennt das Licht der Szene; ein relativer Wert verschiebt vom Anker aus und gilt fuer
        ''' alles andere. Ist beides gesetzt, gewinnt die absolute Zahl - sie ist die genauere
        ''' Aussage.
        '''
        ''' EIGENE FUNKTION, weil die Pipette dieselbe Entscheidung braucht: sie rechnet von einem
        ''' Bildpunkt aus auf einen neuen Weisspunkt zurueck und muss dafuer wissen, von welchem
        ''' der Regler gerade ausgeht. Stuende die Kette hier ein zweites Mal im ViewModel, liefen
        ''' Kette und Pipette bei jeder Aenderung auseinander.
        '''
        ''' OEFFENTLICH und nicht Friend, damit die Pipettenprobe des Pruefstands genau diese
        ''' Entscheidung misst und keine nachgebaute.</summary>
        Public Shared Function ClaimedWhitePointOf(adj As ImageAdjustments) As WhitePoint
            Dim anchor = WhiteBalanceAnchorOf(adj)
            If adj Is Nothing Then Return anchor

            If adj.WhiteBalanceKelvin >= 1000.0 Then
                Return WhiteBalanceAdaptation.FromKelvinAndTint(adj.WhiteBalanceKelvin, adj.WhiteBalanceKelvinTint)
            End If
            If adj.WhiteBalanceKelvinTint <> 0.0 Then
                ' NUR DIE TÖNUNG, bei „wie aufgenommen". Ohne diesen Zweig fiel eine reine
                ' Toenungsaenderung durch alle Bedingungen und wirkte nicht: die Kelvin-Zahl steht
                ' dabei auf 0 (der Anker selbst) und die alten relativen Regler auf 0. Der Regler
                ' aenderte damit den Zustand der Oberflaeche und der Datei, aber kein Bildpunkt.
                ' Verschoben wird vom Anker aus, quer zur Kurve.
                Return WhiteBalanceAdaptation.ShiftFromAnchor(anchor, 0.0, adj.WhiteBalanceKelvinTint)
            End If
            If adj.Temperature <> 0.0F OrElse adj.Tint <> 0.0F Then
                Return WhiteBalanceAdaptation.ShiftFromAnchor(anchor, adj.Temperature, adj.Tint)
            End If
            Return anchor
        End Function

        ''' <summary>sRGB-Gamma nach Linearlicht und zurueck, als Tabellen mit der Aufloesung der
        ''' Kette. Einmal gebaut, danach nur noch gelesen: die Adaptionsstufe braucht sie je
        ''' Bildpunkt sechsmal, und <c>Math.Pow</c> wuerde dort die ganze Kette ausbremsen.
        '''
        ''' Der Interpolationsfehler ist rechnerisch belanglos: die Kurve ist unterhalb von 0,0031
        ''' exakt eine Gerade (und genau dort liegt die erste Stuetzstelle), darueber liegt der
        ''' Fehler der linearen Zwischenwerte bei 4097 Stuetzstellen unter einem Hundertstel eines
        ''' 8-Bit-Schritts.</summary>
        Private Shared _srgbToLinearTable As Single()
        Private Shared _linearToSrgbTable As Single()
        Private Shared ReadOnly _gammaTableLock As New Object()

        Private Shared Sub EnsureGammaTables()
            If _srgbToLinearTable IsNot Nothing Then Return
            SyncLock _gammaTableLock
                If _srgbToLinearTable IsNot Nothing Then Return
                Dim decodeTable = New Single(PointOpTableSize - 1) {}
                Dim encodeTable = New Single(PointOpTableSize - 1) {}
                For i = 0 To PointOpTableSize - 1
                    Dim v = i / CDbl(PointOpTableSize - 1)
                    decodeTable(i) = CSng(If(v <= 0.04045, v / 12.92, Math.Pow((v + 0.055) / 1.055, 2.4)))
                    encodeTable(i) = CSng(If(v <= 0.0031308, v * 12.92, 1.055 * Math.Pow(v, 1.0 / 2.4) - 0.055))
                Next
                _linearToSrgbTable = encodeTable
                ' ZULETZT zuweisen: der Wächter oben prüft dieses Feld, und beide Tabellen müssen
                ' fertig sein, bevor ein anderer Faden an ihnen vorbeikommt.
                _srgbToLinearTable = decodeTable
            End SyncLock
        End Sub

        Private Shared Function BuildPointOpColorMatrix(adj As ImageAdjustments) As Single()
            ' MODELL 2 NIMMT TEMPERATUR UND TÖNUNG HIER HERAUS: sie laufen dort als eigene
            ' Adaptionsstufe im Linearlicht (siehe BuildWhiteBalanceMatrix). Bliebe der Anteil
            ' hier stehen, wirkte der Weissabgleich zweimal - einmal richtig und einmal falsch.
            Dim adaptive = adj.WhiteBalanceModel >= 2
            Dim tempR = If(adaptive, 1.0F, 1.0F + adj.Temperature / 200.0F)
            Dim tempB = If(adaptive, 1.0F, 1.0F - adj.Temperature / 200.0F)
            Dim tintG = If(adaptive, 1.0F, 1.0F + adj.Tint / 200.0F)

            Const lumR As Single = 0.299F
            Const lumG As Single = 0.587F
            Const lumB As Single = 0.114F
            ' Dynamik NICHT mehr hier addieren - sie ist eine eigene, chroma-gewichtete Stufe im
            ' Pixeldurchlauf (chain.Vibrance). Frueher war sat = 1 + Saett/100 + Dynamik/200, wodurch
            ' Dynamik nur eine halb so starke, flache Saettigung war und sich von der Saettigung nicht
            ' auf Grau zurueckziehen liess.
            Dim sat = 1.0F + adj.Saturation / 100.0F
            Dim invSat = 1.0F - sat

            Return New Single() {
                (lumR * invSat + sat) * tempR, lumG * invSat * tempR, lumB * invSat * tempR, 0, 0,
                lumR * invSat, (lumG * invSat + sat) * tintG, lumB * invSat, 0, 0,
                lumR * invSat * tempB, lumG * invSat * tempB, (lumB * invSat + sat) * tempB, 0, 0,
                0, 0, 0, 1, 0
            }
        End Function

        ''' <summary>Die Umkehrung des Weissabgleichs aus <see cref="BuildPointOpColorMatrix"/>,
        ''' fuer die Pipette im ALTEN Modell: welche Reglerwerte machen einen Bildpunkt neutral,
        ''' der jetzt <paramref name="sampleR"/>/<paramref name="sampleG"/>/<paramref name="sampleB"/>
        ''' zeigt? Angegeben wird der Bildpunkt gammakodiert, also so, wie er auf dem Schirm steht -
        ''' das alte Modell rechnet ebenfalls auf gammakodierten Werten.
        '''
        ''' <para>DER KNOTEN: drei Kanaele, aber nur zwei Regler. Die Temperatur verstaerkt Rot und
        ''' Blau GEGENLAEUFIG (1+T/200 gegen 1-T/200), ihre Summe ist immer 2. Frei waehlbar ist
        ''' also nur ihr VERHAELTNIS, und daraus faellt die Temperatur; der gemeinsame Faktor, der
        ''' dabei uebrig bleibt, geht anschliessend in die Toenung ein, sonst zoege Gruen nicht
        ''' mit.</para>
        '''
        ''' <para>DIE SAETTIGUNG GEHOERT MIT HINEIN, weil sie in DERSELBEN Matrix steht und VOR der
        ''' Verstaerkung wirkt: die Zeile lautet tempR mal (sat mal Rot plus Rest mal Helligkeit).
        ''' Ohne diesen Schritt griff die Pipette bei Saettigung +40 daneben - gemessen blieben von
        ''' 84 Stufen Farbstich noch 27 stehen. Was HINTER der Matrix steht (Dynamik, Belichtung,
        ''' Kontrast, Kurven), braucht sie dagegen nicht: diese Stufen rechnen fuer alle drei
        ''' Kanaele gleich und lassen Neutral neutral.</para>
        '''
        ''' <para>Geklemmt wird auf die Reglerbereiche. Ein starker Farbstich laesst sich damit
        ''' nicht in einem Zug wegnehmen - das ist eine Eigenschaft des alten Modells, keine der
        ''' Pipette.</para></summary>
        Public Shared Sub NeutralizeLegacyWhiteBalance(temperature As Double, tint As Double,
                                                       saturation As Double,
                                                       sampleR As Double, sampleG As Double, sampleB As Double,
                                                       ByRef newTemperature As Double, ByRef newTint As Double)
            newTemperature = temperature
            newTint = tint
            If sampleR <= 0.0 OrElse sampleG <= 0.0 OrElse sampleB <= 0.0 Then Return

            ' Dieselbe Rechnung wie in BuildPointOpColorMatrix, nur fuer EINE Farbe.
            Dim sat = 1.0 + saturation / 100.0
            Dim invSat = 1.0 - sat
            Dim luma = 0.299 * sampleR + 0.587 * sampleG + 0.114 * sampleB
            Dim satR = sat * sampleR + invSat * luma
            Dim satG = sat * sampleG + invSat * luma
            Dim satB = sat * sampleB + invSat * luma
            If satR <= 0.0 OrElse satG <= 0.0 OrElse satB <= 0.0 Then Return

            Const gainPerPoint As Double = 200.0
            Dim tempR = 1.0 + temperature / gainPerPoint
            Dim tempB = 1.0 - temperature / gainPerPoint
            Dim tintG = 1.0 + tint / gainPerPoint
            If tempR <= 0.0 OrElse tempB <= 0.0 OrElse tintG <= 0.0 Then Return

            ' Das gesuchte Verhaeltnis von Rot- zu Blauverstaerkung, und die Temperatur daraus.
            Dim ratio = (tempR / tempB) * (satB / satR)
            If Not Double.IsFinite(ratio) OrElse ratio <= 0.0 Then Return
            newTemperature = Math.Max(-100.0, Math.Min(100.0, gainPerPoint * (ratio - 1.0) / (ratio + 1.0)))

            ' Gruen folgt der GEKLEMMTEN Temperatur, nicht der gewuenschten: sonst zoege die
            ' Toenung an einem Rot/Blau-Stand vorbei, den es gar nicht gibt.
            Dim newTempR = 1.0 + newTemperature / gainPerPoint
            Dim wantedTintG = tintG * newTempR * satR / (tempR * satG)
            If Not Double.IsFinite(wantedTintG) OrElse wantedTintG <= 0.0 Then Return
            newTint = Math.Max(-150.0, Math.Min(150.0, gainPerPoint * (wantedTintG - 1.0)))
        End Sub

        ''' <summary>Kleinste Steigung, die Lichter/Tiefen/Weiss/Schwarz zusammen der Kennlinie lassen.
        ''' Darunter wird ein Verlauf so flach, dass er im 8-Bit-Bild zur Flaeche wird.</summary>
        Private Const ToneZoneMinSlope As Double = 0.2

        ''' <summary>Der Hub von Kontrast, Lichter, Tiefen, Weiss und Schwarz am Eingangston
        ''' <paramref name="d0"/>, aus den gemessenen Kennlinien (ToneSliderCurves). Jeder Regler fuer
        ''' sich bleibt monoton; zusammen koennen sie es nicht, das faengt BuildMonotoneToneZones ab.
        ''' <paramref name="part"/> Local liefert nur Lichter und Tiefen, die an der Basisschicht der
        ''' Umgebung ausgewertet werden.</summary>
        Private Shared Function ToneZoneLift(adj As ImageAdjustments, d0 As Double,
                                             Optional reference As ToneReference = Nothing,
                                             Optional part As ToneZonePart = ToneZonePart.All) As Double
            ' Die gemessenen Kennlinien, Kontrast eingeschlossen; ihre Enden sind gemessen und laufen
            ' auf null aus. Lichter waehlen ihre Kurve nach dem Median des ganzen Bildes, Tiefen nach
            ' dem Anteil heller Bildpunkte, Weiss nach der Spitze.
            Dim highlightsShadows = ToneSliderCurves.LiftByMedian("Highlights", adj.Highlights, d0, reference.MedianL) +
                                    ToneSliderCurves.LiftByBrightShare("Shadows", adj.ShadowsLevel, d0, reference.BrightShare,
                                                                       reference.MedianL)
            If part = ToneZonePart.Local Then Return highlightsShadows
            Return ToneSliderCurves.Lift("Contrast", adj.Contrast, d0) +
                   ToneSliderCurves.LiftByTop("Whites", adj.Whites, d0, reference.TopL) +
                   ToneSliderCurves.Lift("Blacks", adj.Blacks, d0) + highlightsShadows
        End Function

        ''' <summary>Lichter/Tiefen/Weiss/Schwarz als monotone Tabelle, oder Nothing, solange die
        ''' Kennlinie ohnehin nirgends flacher als ToneZoneMinSlope wird. Die fallenden Flanken der
        ''' Tiefen- und der Lichter-Glocke liegen beide in den Mitten: Lichter -100 / Tiefen +100
        ''' kehrte dort die Toene um (Steigung -0,23 zwischen 0,43 und 0,57). Hier wird jede zu
        ''' flache Stelle auf die Mindeststeigung angehoben und der Zuwachs den steileren Stellen
        ''' anteilig abgezogen, ueber ihren Ueberschuss. Beide Enden bleiben, wo der Hub sie
        ''' hinsetzt, und die Mindeststeigung gilt danach ueberall. Nothing heisst: die Kette rechnet
        ''' wie bisher direkt, bitgleich.</summary>
        Private Shared Function BuildMonotoneToneZones(adj As ImageAdjustments,
                                                       Optional reference As ToneReference = Nothing,
                                                       Optional part As ToneZonePart = ToneZonePart.All) As Single()
            Dim n = PointOpTableSize - 1
            Dim stepSize = 1.0 / n
            Dim curve = New Double(n) {}
            For i = 0 To n
                Dim d = i * stepSize
                curve(i) = d + ToneZoneLift(adj, d, reference, part)
            Next

            Dim slopes = New Double(n - 1) {}
            Dim deficit = 0.0
            Dim surplus = 0.0
            For i = 0 To n - 1
                Dim s = (curve(i + 1) - curve(i)) / stepSize
                slopes(i) = s
                If s < ToneZoneMinSlope Then
                    deficit += (ToneZoneMinSlope - s) * stepSize
                Else
                    surplus += (s - ToneZoneMinSlope) * stepSize
                End If
            Next
            If deficit <= 0.0 Then Return Nothing
            ' Der Ueberschuss reicht immer: die Enden liegen mindestens ToneZoneMinSlope
            ' auseinander, solange kein Regler ueber 100 steht. Sonst so weit wie moeglich.
            Dim keep = If(surplus > deficit, (surplus - deficit) / surplus, 0.0)

            Dim table = New Single(n) {}
            Dim y = curve(0)
            table(0) = CSng(y)
            For i = 0 To n - 1
                Dim s = slopes(i)
                s = If(s < ToneZoneMinSlope, ToneZoneMinSlope, ToneZoneMinSlope + (s - ToneZoneMinSlope) * keep)
                y += s * stepSize
                table(i + 1) = CSng(y)
            Next
            Return table
        End Function

        ''' <summary>Welche Tonregler eine Zonentabelle traegt: All alle fuenf, Local nur Lichter und
        ''' Tiefen, die oertlich an der Basisschicht ausgewertet werden.</summary>
        Friend Enum ToneZonePart
            All
            Local
        End Enum

        ''' <summary>Lichter/Tiefen/Weiss/Schwarz allein als Tabelle, angewandt ueber ApplyToneZones.
        ''' Monoton wie in der verschmolzenen Kette.</summary>
        Private Shared Function BuildToneZoneTable(adj As ImageAdjustments,
                                                   Optional reference As ToneReference = Nothing,
                                                   Optional part As ToneZonePart = ToneZonePart.All) As Single()
            Dim monotone = BuildMonotoneToneZones(adj, reference, part)
            Dim table = New Single(PointOpTableSize - 1) {}
            For i = 0 To PointOpTableSize - 1
                Dim v = i / CSng(PointOpTableSize - 1)
                table(i) = If(monotone IsNot Nothing,
                              Clamp(monotone(i), 0.0F, 1.0F),
                              Clamp(CSng(v + ToneZoneLift(adj, v, reference, part)), 0.0F, 1.0F))
            Next
            Return table
        End Function

        ''' <summary>Helligkeit des ganzen Bildes, nach der Tonregler ihre Kennlinie waehlen: der
        ''' Median von L fuer Lichter, die Spitze (99. Perzentil von L) fuer Weiss. Beide in L, 0 bis
        ''' 100; ungemessen steht beides auf 0. Dazu fuer die Tiefen der Anteil heller Bildpunkte,
        ''' in Prozent (ohne dessen Kurven gilt auch fuer sie der Median).</summary>
        Friend Structure ToneReference
            Public MedianL As Double
            Public TopL As Double
            ''' <summary>Prozent der gezaehlten Bildpunkte ueber ToneSliderCurves.BrightShareLimitL.</summary>
            Public BrightShare As Double
            ''' <summary>Das Histogramm von L in Zehntelstufen (1001 Faecher), aus dem alle drei Werte
            ''' stammen. Damit laesst sich der Anteil nach einer Tonwertstufe neu bestimmen
            ''' (<see cref="ShareAboveAfter"/>); der Median wandert einfach durch die Stufe mit.</summary>
            Public Histogram As Long()
            Public Total As Long

            Public ReadOnly Property IsMeasured As Boolean
                Get
                    Return MedianL > 0.0
                End Get
            End Property

            ''' <summary>Prozent der Bildpunkte, die NACH der Abbildung <paramref name="map"/> (L auf L,
            ''' steigend) ueber <paramref name="limitL"/> liegen. Ohne Histogramm der gemessene Anteil.</summary>
            Public Function ShareAboveAfter(map As Func(Of Double, Double), limitL As Double) As Double
                If Histogram Is Nothing OrElse Total <= 0 Then Return BrightShare
                Dim above = 0L
                For k = Histogram.Length - 1 To 0 Step -1
                    If map(k / 10.0) <= limitL Then Exit For
                    above += Histogram(k)
                Next
                Return 100.0 * above / Total
            End Function
        End Structure

        ''' <summary>Anteil der Bildpunkte unter der Spitze, die Weiss misst. Das 99.
        ''' Perzentil traf in der Kreuzprobe am besten (Weiss +100: 3,3 L gegen 3,8 bei 99,5 und 3,6
        ''' bei 98); geglaettet gemessen war es nicht besser. Diagnostics/Reglereichung/kennlinien.py
        ''' (TOP_PERCENTILE) muss denselben Wert tragen.</summary>
        Private Const ToneTopFraction As Double = 0.99

        ''' <summary>Median und Spitze der Helligkeit L (CIE, 0 bis 100) eines Bildes, aus einem
        ''' Raster von rund 250 000 Bildpunkten; voll durchsichtige zaehlen nicht. Ungemessen, wenn
        ''' kein Bild da ist oder das Format nicht 8 Bit RGBA ist. Gelesen wird zeilenweise ueber den
        ''' nativen Zeiger, ohne das Bild zu kopieren.</summary>
        ''' <param name="negative">Eine Kette mit Filmumkehr (NegR, NegG, NegB): dann wird das POSITIV
        ''' gemessen, so wie es bei den Tonzonen ankommt; die hellen Stellen dort sind die dunklen des
        ''' Negativs. Ohne Umkehr oder Nothing das Bild selbst.
        ''' Halbtransparente Bildpunkte eines vormultiplizierten Bildes werden erst durch Alpha
        ''' geteilt, wie die Kette es vor dem Rechnen tut.</param>
        Friend Shared Function MeasureToneReference(bmp As SKBitmap, Optional negative As PointOpChain = Nothing) As ToneReference
            Dim none As ToneReference = Nothing
            If bmp Is Nothing OrElse bmp.Width <= 0 OrElse bmp.Height <= 0 Then Return none
            Dim rOff, bOff As Integer
            Select Case bmp.ColorType
                Case SKColorType.Bgra8888 : rOff = 2 : bOff = 0
                Case SKColorType.Rgba8888 : rOff = 0 : bOff = 2
                Case Else : Return none
            End Select
            Dim pixels = bmp.GetPixels()
            If pixels = IntPtr.Zero Then Return none
            Dim toLinear = Function(c As Double) If(c <= 0.04045, c / 12.92, Math.Pow((c + 0.055) / 1.055, 2.4))
            Dim linear = New Double(255) {}
            For i = 0 To 255
                linear(i) = toLinear(i / 255.0)
            Next
            ' Die Umkehr als Tabelle je Kanal, auf den Gammawert des Positivs. Einfarbig mischt die
            ' Kette die drei Gammawerte zu einem Grau (wie ApplyStagesBeforeToneZones).
            Dim inverts = negative IsNot Nothing AndAlso negative.NegR IsNot Nothing
            Dim dividesAlpha = bmp.AlphaType = SKAlphaType.Premul
            Dim negGamma As Double()() = Nothing
            Dim negLinear As Double()() = Nothing
            If inverts Then
                negGamma = New Double(2)() {}
                negLinear = New Double(2)() {}
                Dim tables = {negative.NegR, negative.NegG, negative.NegB}
                For ch = 0 To 2
                    negGamma(ch) = New Double(255) {}
                    negLinear(ch) = New Double(255) {}
                    For i = 0 To 255
                        Dim g = CDbl(SampleTable(tables(ch), CSng(i / 255.0)))
                        negGamma(ch)(i) = g
                        negLinear(ch)(i) = toLinear(Math.Max(0.0, Math.Min(1.0, g)))
                    Next
                Next
            End If
            ' DAS RASTER LIEGT AUF DEN SICHTBAREN BILDPUNKTEN, nicht auf der Bitmap. Dasselbe Objekt
            ' entsteht im Bildrender auf einer Ebene in Bildgroesse, im Kompositor auf einem knappen
            ' Bitmap. Mit Schrittweite und Ursprung aus den Bitmap-Massen sah das Raster dort andere
            ' Punkte, und eine duenne Linie auf einem grossen Bild konnte es ganz verfehlen: Weiss
            ' sah dann je nach Weg anders aus. Bei einem deckenden Bild ist das Rechteck die ganze
            ' Bitmap, und es bleibt wie zuvor.
            ' Eine Messmaske (WithToneMeasureMask) laesst nur den Teil eines Objekts zaehlen, der im
            ' Bild liegt: der Bildrender sieht auf seiner Ebene in Bildgroesse nichts anderes.
            Dim mask = If(_toneMeasureMask IsNot Nothing AndAlso _toneMeasureMaskWidth = bmp.Width AndAlso
                          _toneMeasureMaskHeight = bmp.Height AndAlso _toneMeasureMask.Length = bmp.Width * bmp.Height,
                          _toneMeasureMask, Nothing)
            Dim rowBytes = bmp.RowBytes
            Dim bounds = OpaqueBounds(pixels, bmp.Width, bmp.Height, rowBytes, mask)
            If bounds.IsEmpty Then Return none
            Dim stepSize = Math.Max(1, CInt(Math.Sqrt(CDbl(bounds.Width) * bounds.Height / 250000.0)))
            Dim histogram As Long() = Nothing
            Dim total = 0L
            Dim row = New Byte(rowBytes - 1) {}
            ' DUENN BESETZT HEISST: JEDEN BILDPUNKT ZAEHLEN. Ein festes Raster kann sichtbare Punkte
            ' systematisch verfehlen: eine weisse Diagonale in einem grossen durchsichtigen Objekt
            ' liegt bei Schrittweite 4 auf keinem Rasterpunkt, ein einzelner schwarzer daneben schon,
            ' und Median und Spitze beschrieben Schwarz. Trifft das Raster weniger als ein Viertel
            ' seiner Punkte sichtbar, ist der Inhalt duenn, und der zweite Durchgang liest das ganze
            ' Rechteck. Ein deckendes Foto trifft fast jeden Rasterpunkt und bleibt beim Raster.
            Do
                histogram = New Long(1000) {}
                total = 0L
                ' VERSETZT: je Rasterzeile eine feste, pseudozufaellige Zeile in ihrer Zelle, je
                ' Zelle eine ebensolche Spalte. Ein Raster immer in der linken oberen Ecke sah ein
                ' regelmaessiges Muster nur von einer Seite: ein schwarz-weisses Schachbrett mit einem
                ' Bildpunkt je Feld traf bei Schrittweite 4 nur Schwarz. Der Versatz haengt allein an
                ' der Lage der Zelle im Rechteck, Bildrender und Kompositor sehen also dieselben Punkte.
                Dim jitter = stepSize > 1
                For yCell = bounds.Top To bounds.Bottom - 1 Step stepSize
                    Dim cellRow = (yCell - bounds.Top) \ stepSize
                    Dim y = If(jitter, Math.Min(bounds.Bottom - 1, yCell + RasterJitter(cellRow, -1, stepSize)), yCell)
                    Marshal.Copy(IntPtr.Add(pixels, y * rowBytes), row, 0, rowBytes)
                    Dim maskRow = y * bmp.Width
                    For xCell = bounds.Left To bounds.Right - 1 Step stepSize
                        Dim x = If(jitter, Math.Min(bounds.Right - 1, xCell + RasterJitter((xCell - bounds.Left) \ stepSize, cellRow, stepSize)), xCell)
                        Dim o = x * 4
                        If row(o + 3) = 0 Then Continue For
                        If mask IsNot Nothing AndAlso mask(maskRow + x) = 0 Then Continue For
                        Dim lum As Double
                        Dim alpha = row(o + 3)
                        If dividesAlpha AndAlso alpha < 255 Then
                            ' Wie die Kette: erst durch Alpha teilen, dann wie ein deckender Bildpunkt.
                            Dim r = Math.Min(1.0, row(o + rOff) / CDbl(alpha))
                            Dim g = Math.Min(1.0, row(o + 1) / CDbl(alpha))
                            Dim b = Math.Min(1.0, row(o + bOff) / CDbl(alpha))
                            If inverts Then
                                r = SampleTable(negative.NegR, CSng(r))
                                g = SampleTable(negative.NegG, CSng(g))
                                b = SampleTable(negative.NegB, CSng(b))
                                If negative.NegMonochrome Then
                                    Dim gray = 0.299 * r + 0.587 * g + 0.114 * b
                                    r = gray : g = gray : b = gray
                                End If
                            End If
                            lum = 0.2126 * toLinear(Math.Max(0.0, Math.Min(1.0, r))) +
                                  0.7152 * toLinear(Math.Max(0.0, Math.Min(1.0, g))) +
                                  0.0722 * toLinear(Math.Max(0.0, Math.Min(1.0, b)))
                        ElseIf Not inverts Then
                            lum = 0.2126 * linear(row(o + rOff)) + 0.7152 * linear(row(o + 1)) + 0.0722 * linear(row(o + bOff))
                        ElseIf negative.NegMonochrome Then
                            Dim gray = 0.299 * negGamma(0)(row(o + rOff)) + 0.587 * negGamma(1)(row(o + 1)) + 0.114 * negGamma(2)(row(o + bOff))
                            lum = toLinear(Math.Max(0.0, Math.Min(1.0, gray)))
                        Else
                            lum = 0.2126 * negLinear(0)(row(o + rOff)) + 0.7152 * negLinear(1)(row(o + 1)) + 0.0722 * negLinear(2)(row(o + bOff))
                        End If
                        Dim l = If(lum > 0.008856, 116.0 * Math.Pow(lum, 1.0 / 3.0) - 16.0, 903.3 * lum)
                        histogram(Math.Max(0, Math.Min(1000, CInt(l * 10.0)))) += 1
                        total += 1
                    Next
                Next
                If stepSize > 1 Then
                    Dim gridPoints = CLng((bounds.Height + stepSize - 1) \ stepSize) * ((bounds.Width + stepSize - 1) \ stepSize)
                    If total * 4L < gridPoints Then
                        stepSize = 1
                        Continue Do
                    End If
                End If
                Exit Do
            Loop
            If total = 0 Then Return none
            Dim half = total \ 2
            Dim topCount = CLng(Math.Ceiling(total * ToneTopFraction))
            Dim result As New ToneReference With {.MedianL = 100.0, .TopL = 100.0, .Histogram = histogram, .Total = total}
            ' Ueber der Grenze heisst: Fach k mit k / 10 > Grenze, wie im Messwerkzeug (L > 70).
            Dim bright = 0L
            For k = CInt(Math.Floor(ToneSliderCurves.BrightShareLimitL * 10.0)) + 1 To 1000
                bright += histogram(k)
            Next
            result.BrightShare = 100.0 * bright / total
            Dim medianFound = False
            Dim acc = 0L
            For k = 0 To 1000
                acc += histogram(k)
                If Not medianFound AndAlso acc > half Then
                    result.MedianL = Math.Max(0.05, k / 10.0)
                    medianFound = True
                End If
                If acc >= topCount Then
                    result.TopL = Math.Max(0.05, k / 10.0)
                    Exit For
                End If
            Next
            Return result
        End Function

        ''' <summary>Fester Versatz 0 bis <paramref name="stepSize"/> - 1 fuer die Rasterzelle
        ''' (<paramref name="a"/>, <paramref name="b"/>), aus einer Ganzzahl-Streuung. In Long gerechnet:
        ''' VB wirft bei einem Ueberlauf der Integer-Multiplikation.</summary>
        Private Shared Function RasterJitter(a As Integer, b As Integer, stepSize As Integer) As Integer
            Dim h = (CLng(a) * 73856093L) Xor (CLng(b) * 19349663L) Xor 83492791L
            h = (h Xor (h >> 13)) And &H7FFFFFFFL
            h = (h * 1274126177L) And &H7FFFFFFFL
            Return CInt(h Mod stepSize)
        End Function

        ''' <summary>Das kleinste Rechteck um alle nicht voll durchsichtigen Bildpunkte einer 8-Bit-
        ''' RGBA-Bitmap; leer, wenn alle durchsichtig sind. Oben und unten bis zur ersten sichtbaren
        ''' Zeile, links und rechts je Zeile nur, bis der bisherige Rand erreicht ist: ein deckendes
        ''' Bild kostet damit zwei Bildpunkte je Zeile.
        '''
        ''' Ein kleines Objekt auf einer Ebene in Bildgroesse kostet dagegen fast die ganze Flaeche,
        ''' bevor es gefunden ist. Deshalb zeilenweise am Stueck kopiert und als Ganzzahl je
        ''' Bildpunkt geprueft (Alpha ist das oberste Byte), nicht Byte fuer Byte ueber den Zeiger:
        ''' das ist ein Kopieren in Speichergeschwindigkeit statt eines Aufrufs je Bildpunkt. Im
        ''' Verhaeltnis bleibt es klein, denn die Kette rechnet danach ohnehin jeden Bildpunkt der
        ''' Ebene.
        '''
        ''' Mit <paramref name="mask"/> zaehlt ein Bildpunkt nur, wo die Maske nicht 0 ist (siehe
        ''' WithToneMeasureMask).</summary>
        Private Shared Function OpaqueBounds(pixels As IntPtr, width As Integer, height As Integer,
                                             rowBytes As Integer, Optional mask As Byte() = Nothing) As SKRectI
            Const AlphaMask As Integer = &HFF000000
            Dim row = New Integer(width - 1) {}
            Dim loadRow = Sub(y As Integer)
                              Marshal.Copy(IntPtr.Add(pixels, y * rowBytes), row, 0, width)
                              If mask Is Nothing Then Return
                              Dim maskRow = y * width
                              For x = 0 To width - 1
                                  If mask(maskRow + x) = 0 Then row(x) = 0
                              Next
                          End Sub
            Dim top = -1
            For y = 0 To height - 1
                loadRow(y)
                For x = 0 To width - 1
                    If (row(x) And AlphaMask) <> 0 Then top = y : Exit For
                Next
                If top >= 0 Then Exit For
            Next
            If top < 0 Then Return SKRectI.Empty
            Dim bottom = top
            For y = height - 1 To top Step -1
                loadRow(y)
                Dim found = False
                For x = 0 To width - 1
                    If (row(x) And AlphaMask) <> 0 Then found = True : Exit For
                Next
                If found Then bottom = y : Exit For
            Next
            Dim left = width
            Dim right = -1
            For y = top To bottom
                ' Ein deckendes Bild hat beide Raender nach der ersten Zeile; dann gibt es nichts mehr
                ' zu lesen.
                If left = 0 AndAlso right = width - 1 Then Exit For
                loadRow(y)
                For x = 0 To Math.Min(left, width) - 1
                    If (row(x) And AlphaMask) <> 0 Then left = x : Exit For
                Next
                For x = width - 1 To right + 1 Step -1
                    If (row(x) And AlphaMask) <> 0 Then right = x : Exit For
                Next
            Next
            If right < left Then Return SKRectI.Empty
            Return New SKRectI(left, top, right + 1, bottom + 1)
        End Function

        ''' <summary>L (0 bis 100) eines Graus auf seinen sRGB-Gammawert, und zurueck.</summary>
        Private Shared Function LightnessToGrayV(l As Double) As Double
            Dim y = If(l > 8.0, Math.Pow((l + 16.0) / 116.0, 3.0), l / 903.3)
            y = Math.Max(0.0, Math.Min(1.0, y))
            Return If(y <= 0.0031308, 12.92 * y, 1.055 * Math.Pow(y, 1.0 / 2.4) - 0.055)
        End Function

        Private Shared Function GrayVToLightness(v As Double) As Double
            Dim c = Math.Max(0.0, Math.Min(1.0, v))
            Dim y = If(c <= 0.04045, c / 12.92, Math.Pow((c + 0.055) / 1.055, 2.4))
            Return If(y > 0.008856, 116.0 * Math.Pow(y, 1.0 / 3.0) - 16.0, 903.3 * y)
        End Function

        ' ── Dynamik, Farbmischer-Luminanz, Farbgradierung auf gemessener Staerke ─────────────
        ' Gemessen gegen eine verbreitete RAW-Entwicklung an 34 Aufnahmen (Diagnostics/
        ' Reglereichung): Faktor unserer Wirkung durch die Referenz, je Stellung. Die Werte hier sind
        ' die Kehrwerte; dazwischen wird linear geteilt.

        ''' <summary>Dynamik: unsere Formel wirkte bei +50 mit 0,81, bei +100 mit 0,59, bei -50 mit
        ''' 0,43 und bei -100 mit 0,53 der Referenz. Positiv ist der Rueckgabewert das v der Formel
        ''' sat * (1 + v * (1 - sat)) und darf ueber 1 gehen (die Kette klemmt sat); negativ der
        ''' Exponent k in sat * exp(k * (1 - sat)) (PointOpChain.VibranceExponential).</summary>
        Private Shared Function MeasuredVibrance(value As Single) As Single
            Dim v = Math.Max(-1.0, Math.Min(1.0, value / 100.0))
            If v >= 0 Then
                Return CSng(If(v <= 0.5, v / 0.5 * 0.62, 0.62 + (v - 0.5) / 0.5 * (1.7 - 0.62)))
            End If
            Return CSng(v * VibranceNegativeK)
        End Function

        ''' <summary>Exponent der negativen Dynamik bei -100 (linear im Reglerwert).</summary>
        Private Const VibranceNegativeK As Double = 3.2

        ' Positive Dynamik je Farbton, gemessen gegen dieselbe Referenz an 57 Aufnahmen
        ' (Diagnostics/Reglereichung, Stellungen dynamik+50 und dynamik+100): v der Formel
        ' sat * (1 + v * (1 - sat)), abgelesen am Ausgabebild der Referenz gegen ihr Nullbild, Median je
        ' Farbtonfeld zu 20 Grad und Saettigung bis 0,4, ueber die Bilder wieder der Median, leicht
        ' geglaettet (1/4, 1/2, 1/4 ueber die Nachbarfelder). Die Referenz schont Rot, Orange und Pink
        ' - die Hauttoene - deutlich und hebt Gruen bis Cyan am staerksten; die alte, ueber den Farbton
        ' gleiche Formel gab der Haut bei +50 das Dreifache (Nutzerbefund: Dynamik wirkte wie
        ' Saettigung). Die negative Dynamik wirkt bei der Referenz ueber den Farbton gleich und bleibt
        ' bei MeasuredVibrance. Feld i hat seine Mitte bei 20 * i + 10 Grad.
        Private Shared ReadOnly VibranceHue50 As Single() = {
            0.21F, 0.32F, 0.45F, 0.53F, 0.56F, 0.57F, 0.6F, 0.62F, 0.6F,
            0.58F, 0.54F, 0.49F, 0.46F, 0.44F, 0.44F, 0.41F, 0.3F, 0.21F}
        Private Shared ReadOnly VibranceHue100 As Single() = {
            0.85F, 1.19F, 1.71F, 2.14F, 2.33F, 2.33F, 2.28F, 2.24F, 2.2F,
            2.16F, 1.99F, 1.7F, 1.54F, 1.62F, 1.83F, 1.71F, 1.23F, 0.86F}

        ''' <summary>Verstaerkung der Tabellen, weil die Dynamik die Leuchtdichte haelt. Abgelesen
        ''' sind die Tabellen an einer Wirkung, die die Helligkeit mitnimmt; ohne sie fiel die Wirkung
        ''' bei +100 auf 11,9 gegen 14,9 der Referenz. Gemessen an 57 Aufnahmen, Abstand / Treffer:
        ''' 1,0 ergab 6,17 / 59 % bei +100 und 2,40 / 55 % bei +50, 1,25 ergab 6,15 / 59 % und
        ''' 2,23 / 58 %, 1,5 ergab 6,66 / 55 % und 2,43 / 54 %.</summary>
        Private Const VibranceLumaKeepGain As Double = 1.25

        ''' <summary>Positive Dynamik je Farbtonfeld: zwischen 0, +50 und +100 linear in den
        ''' gemessenen Tabellen. Werte ueber 100 (handbearbeitete Rezepte) werden auf 100 begrenzt.</summary>
        Private Shared Function MeasuredVibranceByHue(value As Single) As Single()
            Dim v = Math.Max(0.0, Math.Min(1.0, value / 100.0))
            Dim result(VibranceHue100.Length - 1) As Single
            For i = 0 To result.Length - 1
                result(i) = CSng(VibranceLumaKeepGain * If(v <= 0.5,
                                    v / 0.5 * VibranceHue50(i),
                                    VibranceHue50(i) + (v - 0.5) / 0.5 * (VibranceHue100(i) - VibranceHue50(i))))
            Next
            Return result
        End Function

        ''' <summary>v der positiven Dynamik am Farbton <paramref name="hue"/> (Grad), zwischen den
        ''' Feldmitten linear und ueber 0/360 Grad hinweg geschlossen.</summary>
        Private Shared Function SampleVibranceByHue(table As Single(), hue As Double) As Double
            Dim n = table.Length
            Dim position = hue / 20.0 - 0.5
            Dim lower = CInt(Math.Floor(position))
            Dim t = position - lower
            Dim i0 = ((lower Mod n) + n) Mod n
            Dim i1 = (i0 + 1) Mod n
            Return table(i0) + t * (table(i1) - table(i0))
        End Function

        ''' <summary>Farbmischer: die Luminanz je Band und Richtung gestaucht.
        ''' Unsere Wirkung lag bei +60 1,2- bis 2,9-fach, bei -60 2,1- bis 6,2-fach ueber der
        ''' Referenz. Farbton und Saettigung bleiben (Saettigung traf, der Farbton ist eine Frage der
        ''' Bandform, nicht der Staerke). Magenta ist nur ueber alle Bilder zusammen messbar: nach
        ''' unten -3,7 L bei der Referenz gegen -7,2 mit 0,3, also 0,15; nach oben das Mittel.
        ''' Eine leichte Kopie nur der 24 Bandwerte; mehr liest GetHslBandAdjustments nicht.</summary>
        Private Shared Function ReferenceHslLuminance(adj As ImageAdjustments) As ImageAdjustments
            ' Nach der Bandlage (ReferenceBandHue) nachgemessen; Aqua nach oben und Magenta nach oben
            ' sind in keinem Messbild genug vertreten und behalten den Wert vor der Bandlage.
            Dim up = {0.71F, 0.61F, 0.42F, 0.48F, 0.34F, 0.67F, 0.84F, 0.57F}
            Dim down = {0.36F, 0.34F, 0.24F, 0.34F, 0.15F, 0.28F, 0.39F, 0.15F}
            Dim S = Function(value As Single, band As Integer) value * If(value >= 0, up(band), down(band))
            Return New ImageAdjustments With {
                .RedHue = adj.RedHue, .RedSaturation = adj.RedSaturation, .RedLuminance = S(adj.RedLuminance, 0),
                .OrangeHue = adj.OrangeHue, .OrangeSaturation = adj.OrangeSaturation, .OrangeLuminance = S(adj.OrangeLuminance, 1),
                .YellowHue = adj.YellowHue, .YellowSaturation = adj.YellowSaturation, .YellowLuminance = S(adj.YellowLuminance, 2),
                .GreenHue = adj.GreenHue, .GreenSaturation = adj.GreenSaturation, .GreenLuminance = S(adj.GreenLuminance, 3),
                .AquaHue = adj.AquaHue, .AquaSaturation = adj.AquaSaturation, .AquaLuminance = S(adj.AquaLuminance, 4),
                .BlueHue = adj.BlueHue, .BlueSaturation = adj.BlueSaturation, .BlueLuminance = S(adj.BlueLuminance, 5),
                .PurpleHue = adj.PurpleHue, .PurpleSaturation = adj.PurpleSaturation, .PurpleLuminance = S(adj.PurpleLuminance, 6),
                .MagentaHue = adj.MagentaHue, .MagentaSaturation = adj.MagentaSaturation, .MagentaLuminance = S(adj.MagentaLuminance, 7)}
        End Function

        ''' <summary>Toenung der Farbgradierung, gemessen an 34 Aufnahmen
        ''' (Diagnostics/Reglereichung/gradmodell.py): die Referenz legt einen GLEICHEN Farbversatz auf
        ''' alle Bildpunkte der Zone, statt sie zur Tonfarbe hin zu mischen; ihr Farbrad ist nicht der
        ''' HSL-Farbkreis; ihre Zonen sind breiter (Tiefen bis Y 0,9, Lichter ab Y 0,15); und die
        ''' Helligkeit aendert sich nur um den kleinen Anteil, den der Farbton selbst mitbringt.
        ''' Gerechnet in Y/Cb/Cr (BT.601) auf den Gammawerten: der Versatz je Zone mal deren Gewicht an
        ''' der Helligkeit Y des Bildpunkts, alle vier Zonen addiert.</summary>
        Private Shared Sub ApplyColorGrade(ByRef rr As Single, ByRef gg As Single, ByRef bb As Single,
                                              chain As PointOpChain)
            Dim y = 0.299F * rr + 0.587F * gg + 0.114F * bb
            Dim o = chain.SplitOffsets
            Dim wS = SampleGradeWeight(chain.SplitShadowTable, y)
            Dim wM = SampleGradeWeight(GradeMid, y)
            Dim wH = SampleGradeWeight(chain.SplitHighTable, y)
            Dim wG = SampleGradeWeight(GradeGlobal, y)
            Dim dY = wS * o(0) + wM * o(3) + wH * o(6) + wG * o(9) + SampleGradeWeight(chain.SplitLumTable, y)
            Dim dCb = wS * o(1) + wM * o(4) + wH * o(7) + wG * o(10)
            Dim dCr = wS * o(2) + wM * o(5) + wH * o(8) + wG * o(11)
            Dim dR = dY + dCr / 0.713F
            Dim dB = dY + dCb / 0.564F
            Dim dG = dY - (0.299F * dCr / 0.713F + 0.114F * dCb / 0.564F) / 0.587F
            rr = Clamp(rr + dR, 0.0F, 1.0F)
            gg = Clamp(gg + dG, 0.0F, 1.0F)
            bb = Clamp(bb + dB, 0.0F, 1.0F)
        End Sub

        ''' <summary>Zonengewicht an der Helligkeit <paramref name="y"/>: 20 Stufen, linear dazwischen.</summary>
        Private Shared Function SampleGradeWeight(table As Single(), y As Single) As Single
            Dim pos = y * table.Length - 0.5F
            If pos <= 0.0F Then Return table(0)
            If pos >= table.Length - 1 Then Return table(table.Length - 1)
            Dim i = CInt(Math.Floor(pos))
            Return table(i) + (table(i + 1) - table(i)) * (pos - i)
        End Function

        ''' <summary>Versatz dY, dCb, dCr einer Zone bei vollem Gewicht, aus dem Farbton der
        ''' Farbgradierung (Farbrad der Referenz, 30-Grad-Stufen, linear dazwischen) und ihrer
        ''' Saettigung (linear, gemessen 25/50/100). Schreibt nach <paramref name="target"/> ab
        ''' <paramref name="offset"/>.</summary>
        Private Shared Sub GradeVector(hue As Single, saturation As Single, target As Single(), offset As Integer)
            Dim h = ((hue Mod 360.0F) + 360.0F) Mod 360.0F
            Dim i = CInt(Math.Floor(h / 30.0F)) Mod 12
            Dim f = (h - i * 30.0F) / 30.0F
            Dim j = (i + 1) Mod 12
            Dim k = saturation / 50.0F
            For c = 0 To 2
                target(offset + c) = (GradeHue(i, c) + (GradeHue(j, c) - GradeHue(i, c)) * f) * k
            Next
        End Sub

        ''' Versatz dY, dCb, dCr je 30 Grad Farbton bei Saettigung 50 (gemessen, Mittel Y 0,2 bis 0,8).
        Private Shared ReadOnly GradeHue As Single(,) = {
            {-0.0089F, -0.0185F, 0.0996F}, {0.0184F, -0.0395F, 0.0628F}, {0.0427F, -0.0603F, 0.023F},
            {0.018F, -0.046F, -0.0335F}, {-0.0121F, -0.0288F, -0.0965F}, {-0.0112F, 0.0007F, -0.1094F},
            {-0.0107F, 0.0296F, -0.1226F}, {-0.0267F, 0.0435F, -0.0734F}, {-0.045F, 0.0578F, -0.0265F},
            {-0.0226F, 0.045F, 0.029F}, {-0.0044F, 0.0346F, 0.0808F}, {-0.0063F, 0.0081F, 0.0907F}}

        ''' Zonengewichte ueber Y in 20 Stufen, bezogen auf den globalen Versatz in der Bildmitte.
        Private Shared ReadOnly GradeGlobal As Single() = {0.142F, 0.365F, 0.491F, 0.616F, 0.745F, 0.872F, 0.967F, 1.06F, 1.151F, 1.171F, 1.103F, 1.118F, 1.1F, 1.033F, 0.88F, 0.801F, 0.719F, 0.573F, 0.395F, 0.155F}
        Private Shared ReadOnly GradeMid As Single() = {0.008F, 0.049F, 0.132F, 0.263F, 0.383F, 0.584F, 0.691F, 0.792F, 0.924F, 0.97F, 0.941F, 0.926F, 0.819F, 0.702F, 0.522F, 0.387F, 0.256F, 0.143F, 0.068F, 0.014F}

        ''' Tiefen und Lichter bei Ueberblendung 100 (so rechnet die Referenz auch, wenn ein Preset
        ''' keine Ueberblendung angibt) und 0, dazu bei Balance -50 und +50 (Ueberblendung 100).
        ''' Gemessen an 57 Aufnahmen, gg-*-h30-s50-* (Diagnostics/Reglereichung/stellungen.py).
        Private Shared ReadOnly GradeShadow100 As Single() = {0.316F, 0.821F, 0.997F, 1.291F, 1.405F, 1.428F, 1.423F, 1.453F, 1.360F, 1.263F, 1.104F, 0.993F, 0.840F, 0.698F, 0.563F, 0.425F, 0.292F, 0.171F, 0.081F, 0.022F}
        Private Shared ReadOnly GradeShadow0 As Single() = {0.285F, 0.698F, 0.769F, 0.842F, 0.816F, 0.721F, 0.580F, 0.424F, 0.293F, 0.181F, 0.097F, 0.053F, 0.028F, 0.016F, 0.016F, 0.010F, 0.004F, 0.002F, 0.001F, 0.002F}
        Private Shared ReadOnly GradeShadowBalM As Single() = {0.322F, 0.886F, 1.091F, 1.485F, 1.651F, 1.726F, 1.786F, 1.904F, 1.857F, 1.806F, 1.686F, 1.600F, 1.445F, 1.274F, 1.072F, 0.849F, 0.618F, 0.377F, 0.184F, 0.044F}
        Private Shared ReadOnly GradeShadowBalP As Single() = {0.302F, 0.714F, 0.819F, 0.961F, 1.003F, 0.963F, 0.887F, 0.849F, 0.742F, 0.645F, 0.522F, 0.439F, 0.352F, 0.281F, 0.219F, 0.163F, 0.109F, 0.060F, 0.032F, 0.009F}
        Private Shared ReadOnly GradeHigh100 As Single() = {0.010F, 0.055F, 0.129F, 0.263F, 0.377F, 0.556F, 0.638F, 0.833F, 0.873F, 0.997F, 1.146F, 1.250F, 1.365F, 1.434F, 1.390F, 1.363F, 1.283F, 1.058F, 0.749F, 0.279F}
        Private Shared ReadOnly GradeHigh0 As Single() = {0.001F, 0.000F, 0.001F, 0.004F, 0.020F, 0.046F, 0.053F, 0.066F, 0.070F, 0.090F, 0.157F, 0.220F, 0.356F, 0.491F, 0.620F, 0.764F, 0.875F, 0.829F, 0.633F, 0.247F}
        Private Shared ReadOnly GradeHighBalM As Single() = {0.004F, 0.013F, 0.048F, 0.114F, 0.182F, 0.288F, 0.325F, 0.445F, 0.460F, 0.545F, 0.665F, 0.751F, 0.867F, 0.953F, 0.974F, 1.019F, 1.029F, 0.912F, 0.677F, 0.265F}
        Private Shared ReadOnly GradeHighBalP As Single() = {0.026F, 0.149F, 0.291F, 0.542F, 0.746F, 0.961F, 1.098F, 1.350F, 1.403F, 1.521F, 1.640F, 1.715F, 1.782F, 1.790F, 1.662F, 1.557F, 1.409F, 1.125F, 0.781F, 0.287F}

        ''' Helligkeitsversatz dY der Zonen bei -50 und +50 (gemessen, gg-*-lum*), je 20 Stufen.
        Private Shared ReadOnly GradeLum As Single()() = {
            New Single() {-0.0115F, -0.0296F, -0.0363F, -0.0394F, -0.0403F, -0.0416F, -0.0382F, -0.0324F, -0.0239F, -0.0185F, -0.0147F, -0.0122F, -0.0098F, -0.0081F, -0.0061F, -0.0044F, -0.0029F, -0.0016F, -0.0007F, -0.0003F}, New Single() {0.0581F, 0.0582F, 0.0562F, 0.0501F, 0.0454F, 0.0405F, 0.0318F, 0.0239F, 0.0173F, 0.0128F, 0.0104F, 0.0086F, 0.0069F, 0.0055F, 0.0042F, 0.0029F, 0.0018F, 0.0009F, 0.0003F, -0.0001F},
            New Single() {0.0002F, -0.0002F, -0.0011F, -0.0033F, -0.0071F, -0.0127F, -0.0209F, -0.0293F, -0.0364F, -0.0416F, -0.0441F, -0.0427F, -0.0377F, -0.0320F, -0.0241F, -0.0169F, -0.0104F, -0.0052F, -0.0020F, -0.0006F}, New Single() {0.0002F, 0.0008F, 0.0021F, 0.0049F, 0.0087F, 0.0147F, 0.0233F, 0.0313F, 0.0375F, 0.0416F, 0.0433F, 0.0414F, 0.0361F, 0.0299F, 0.0217F, 0.0145F, 0.0085F, 0.0040F, 0.0013F, -0.0001F},
            New Single() {0.0001F, -0.0000F, -0.0003F, -0.0008F, -0.0012F, -0.0015F, -0.0025F, -0.0041F, -0.0061F, -0.0081F, -0.0101F, -0.0138F, -0.0206F, -0.0283F, -0.0377F, -0.0462F, -0.0537F, -0.0585F, -0.0598F, -0.0563F}, New Single() {0.0001F, 0.0002F, 0.0006F, 0.0014F, 0.0022F, 0.0032F, 0.0043F, 0.0059F, 0.0083F, 0.0105F, 0.0142F, 0.0193F, 0.0269F, 0.0340F, 0.0423F, 0.0477F, 0.0490F, 0.0434F, 0.0313F, 0.0117F},
            New Single() {-0.0047F, -0.0118F, -0.0164F, -0.0209F, -0.0253F, -0.0302F, -0.0347F, -0.0386F, -0.0410F, -0.0430F, -0.0447F, -0.0453F, -0.0448F, -0.0440F, -0.0414F, -0.0380F, -0.0332F, -0.0265F, -0.0181F, -0.0069F}, New Single() {0.0067F, 0.0138F, 0.0182F, 0.0231F, 0.0281F, 0.0338F, 0.0378F, 0.0408F, 0.0425F, 0.0438F, 0.0448F, 0.0448F, 0.0439F, 0.0420F, 0.0388F, 0.0345F, 0.0293F, 0.0224F, 0.0147F, 0.0049F}}

        ''' <summary>Baut die Zonentabellen einer Farbgradierung: Tiefen und
        ''' Lichter linear zwischen Ueberblendung 0 und 100, die Balance als gemessene Abweichung bei
        ''' -50/+50 linear dazu (bei +-100 doppelt); die Helligkeit aller vier Zonen als Summe ihrer
        ''' gemessenen Kurven, je nach Vorzeichen, linear im Wert.</summary>
        Private Shared Sub BuildColorGradeTables(adj As ImageAdjustments, chain As PointOpChain)
            Dim t = Clamp(adj.ColorGradeBlending, 0, 100) / 100.0F
            Dim b = Clamp(adj.ColorGradeBalance, -100, 100) / 50.0F
            chain.SplitShadowTable = BlendGradeTable(GradeShadow0, GradeShadow100, GradeShadowBalM, GradeShadowBalP, t, b)
            chain.SplitHighTable = BlendGradeTable(GradeHigh0, GradeHigh100, GradeHighBalM, GradeHighBalP, t, b)
            Dim lums = {adj.ColorGradeShadowLuminance, adj.ColorGradeMidtoneLuminance,
                        adj.ColorGradeHighlightLuminance, adj.ColorGradeGlobalLuminance}
            Dim lum = New Single(19) {}
            For z = 0 To 3
                If lums(z) = 0.0F Then Continue For
                Dim curve = GradeLum(z * 2 + If(lums(z) > 0, 1, 0))
                Dim k = Clamp(lums(z), -100, 100) / 50.0F
                If k < 0 Then k = -k
                For i = 0 To 19
                    lum(i) += curve(i) * k
                Next
            Next
            chain.SplitLumTable = lum
        End Sub

        Private Shared Function BlendGradeTable(zero As Single(), full As Single(), balM As Single(), balP As Single(),
                                                t As Single, b As Single) As Single()
            Dim result = New Single(19) {}
            For i = 0 To 19
                Dim v = zero(i) + (full(i) - zero(i)) * t
                v += If(b < 0, (balM(i) - full(i)) * -b, (balP(i) - full(i)) * b)
                result(i) = Math.Max(0.0F, v)
            Next
            Return result
        End Function

        ''' <summary>Punkt- und Kanalkurven im Raum der Referenz. Gemessen an 20 Aufnahmen
        ''' (Diagnostics/Reglereichung/kurvenraum.py) wendet die Referenz ihre Kurven in ProPhoto-
        ''' Primaerfarben mit sRGB-Gammakurve an: eine Rot-Kanalkurve lag so 2,1 dE neben ihr, im
        ''' sRGB-Gamma 7,8, eine Blau-Kurve 1,8 gegen 3,5; die Masterkurve 2,5 gegen 2,8. Hin ueber
        ''' Linearlicht und die Primaermatrix (Zeilen auf 1 normiert, Grau bleibt grau), Kurven je
        ''' Kanal, zurueck und auf sRGB geklemmt.</summary>
        Private Shared Sub ApplyCurvesInMelissa(ByRef rr As Single, ByRef gg As Single, ByRef bb As Single,
                                                sr As Single(), sg As Single(), sb As Single(),
                                                toLinear As Single(), toGamma As Single())
            Dim lr = SampleTable(toLinear, rr)
            Dim lg = SampleTable(toLinear, gg)
            Dim lb = SampleTable(toLinear, bb)
            Dim pr = 0.529214F * lr + 0.330063F * lg + 0.140723F * lb
            Dim pg = 0.098324F * lr + 0.873625F * lg + 0.028051F * lb
            Dim pb = 0.016879F * lr + 0.117714F * lg + 0.865407F * lb
            pr = SampleTable(sr, SampleTable(toGamma, Clamp(pr, 0.0F, 1.0F)))
            pg = SampleTable(sg, SampleTable(toGamma, Clamp(pg, 0.0F, 1.0F)))
            pb = SampleTable(sb, SampleTable(toGamma, Clamp(pb, 0.0F, 1.0F)))
            pr = SampleTable(toLinear, pr)
            pg = SampleTable(toLinear, pg)
            pb = SampleTable(toLinear, pb)
            Dim nr = 2.034515F * pr - 0.727257F * pg - 0.307258F * pb
            Dim ng = -0.228704F * pr + 1.23143F * pg - 0.002726F * pb
            Dim nb = -0.008573F * pr - 0.153316F * pg + 1.161889F * pb
            rr = SampleTable(toGamma, Clamp(nr, 0.0F, 1.0F))
            gg = SampleTable(toGamma, Clamp(ng, 0.0F, 1.0F))
            bb = SampleTable(toGamma, Clamp(nb, 0.0F, 1.0F))
        End Sub

        ''' <summary>Farbton fuer die Bandsuche des Farbmischers. Gemessen an 34 Aufnahmen
        ''' (Diagnostics/Reglereichung/bandlage.py) liegen die Baender der Referenz anders auf dem
        ''' Farbkreis: Orange bis Aqua rund 12 bis 15 Grad tiefer, Lila und Magenta rund 9 Grad
        ''' hoeher, Rot und Blau gleich. Der Farbton wird stueckweise linear so verschoben, dass die
        ''' gemessenen Bandmitten der Referenz auf die gemessenen Mitten unseres Farbmischers fallen;
        ''' die Breiten folgen den Abstaenden. Nur fuer die SUCHE, verschoben wird der echte Farbton.</summary>
        Private Shared Function ReferenceBandHue(h As Double) As Double
            Return WarpCircular(h, ReferenceBandFrom, ReferenceBandTo)
        End Function

        Private Shared ReadOnly ReferenceBandFrom As Double() = {18.6, 53.8, 108.8, 156.4, 226.9, 278.4, 324.0, 354.5}
        Private Shared ReadOnly ReferenceBandTo As Double() = {30.5, 68.7, 120.1, 171.1, 224.2, 270.2, 314.6, 355.5}

        ''' <summary>Stueckweise lineare Abbildung auf dem Kreis: die steigend sortierten Stuetzstellen
        ''' <paramref name="from"/> gehen auf <paramref name="target"/>, dazwischen linear, ueber 360
        ''' hinweg geschlossen.</summary>
        Private Shared Function WarpCircular(h As Double, from As Double(), target As Double()) As Double
            Dim n = from.Length
            h = ((h Mod 360.0) + 360.0) Mod 360.0
            For i = 0 To n - 1
                Dim a = from(i)
                Dim b = If(i + 1 < n, from(i + 1), from(0) + 360.0)
                Dim x = h
                If x < a Then x += 360.0
                If x >= a AndAlso x < b Then
                    Dim ta = target(i)
                    Dim tb = If(i + 1 < n, target(i + 1), target(0) + 360.0)
                    If tb < ta Then tb += 360.0
                    Return ((ta + (tb - ta) * (x - a) / (b - a)) Mod 360.0 + 360.0) Mod 360.0
                End If
            Next
            Return h
        End Function


        ''' <summary>Lichter/Tiefen/Weiss/Schwarz auf einen Pixel, farbtonerhaltend wie Adobes
        ''' RGB-Tonkurve im DNG-SDK: die Tabelle wirkt auf den groessten und den kleinsten Kanal, der
        ''' mittlere behaelt seine relative Lage dazwischen. Je Kanal gerechnet rueckte er dorthin, wo
        ''' die Kennlinie flach ist, und ein Orange 0,9/0,6/0,3 wurde bei Lichter -100 / Tiefen +100
        ''' zu Gruen gleich Blau, also gelblich grau (Forum pixls.us, 2026-10-07). Die Saettigung folgt
        ''' dagegen weiter der Steigung, wie bei Lightroom: dieselbe Rechnung mit einem gemeinsamen
        ''' Faktor auf alle drei Kanaele (Farbverhaeltnis ganz erhalten) lag gemessen weiter weg
        ''' (dE 7,69 gegen 5,15 am Export "nur Grundregler", Mitten zu rot). Ein Grau rechnet genau
        ''' wie die Tabelle selbst.</summary>
        Private Shared Sub ApplyToneZones(ByRef rr As Single, ByRef gg As Single, ByRef bb As Single,
                                          zones As Single())
            Dim vMax = Math.Max(rr, Math.Max(gg, bb))
            Dim vMin = Math.Min(rr, Math.Min(gg, bb))
            Dim newMax = SampleTable(zones, vMax)
            Dim newMin = SampleTable(zones, vMin)
            If vMax - vMin < 0.000001F Then
                rr = newMax : gg = newMax : bb = newMax
                Return
            End If
            Dim scale = (newMax - newMin) / (vMax - vMin)
            rr = newMin + (rr - vMin) * scale
            gg = newMin + (gg - vMin) * scale
            bb = newMin + (bb - vMin) * scale
        End Sub

        ''' <summary>Wie <see cref="ApplyToneZones"/>, mit oertlichen Lichtern und Tiefen: <paramref name="pixelZones"/>
        ''' (die gemeinsame Tabelle ohne den oertlichen Hub) wirkt am Kanalwert,
        ''' <paramref name="localZones"/> (Lichter, Tiefen) an der Basisschicht
        ''' <paramref name="baseValue"/> statt am Bildpunkt. Wo die Basis der Grauwert selbst ist,
        ''' ist das genau die gemeinsame Tabelle. <paramref name="detailShift"/>
        ''' ist das Strukturglied: die Feinstruktur des Bildpunkts mal der Verstaerkung seiner Zone
        ''' (PointOpChain.LocalDetailGain).
        '''
        ''' Der oertliche Hub wird EINMAL an der Basis abgelesen und gilt fuer alle Kanaele. Den
        ''' Abstand der Kanaele vom Grau <paramref name="gray"/> streckt
        ''' <paramref name="colorExcess"/> (PointOpChain.LocalColorGain, Faktor minus 1) wie die
        ''' Feinstruktur. Abgelesen je Kanal an seiner eigenen Stelle folgte die Buntheit der
        ''' Steigung der Kennlinie und lief der Referenz entgegen: Lichter -100 machte die Lichter
        ''' blasser (L 70 bis 85 0,77 gegen 1,07), Tiefen +100 die Tiefen (0,84 gegen 1,09).</summary>
        Private Shared Sub ApplyLocalToneZones(ByRef rr As Single, ByRef gg As Single, ByRef bb As Single,
                                               pixelZones As Single(), localZones As Single(), baseValue As Single,
                                               gray As Single, detailShift As Single, colorExcess As Single)
            Dim at = Clamp(baseValue, 0.0F, 1.0F)
            Dim shift = SampleTable(localZones, at) - at + detailShift
            Dim vMax = Math.Max(rr, Math.Max(gg, bb))
            Dim vMin = Math.Min(rr, Math.Min(gg, bb))
            Dim newMax = LocalZoneValue(vMax, pixelZones, shift, gray, colorExcess)
            If vMax - vMin < 0.000001F Then
                rr = newMax : gg = newMax : bb = newMax
                Return
            End If
            Dim newMin = LocalZoneValue(vMin, pixelZones, shift, gray, colorExcess)
            ' Mit Verschiebung ist die Kennlinie an diesem Bildpunkt nicht mehr zwingend monoton.
            ' Kehrte sich die Reihenfolge von groesstem und kleinstem Kanal um, kippte der Farbton;
            ' dann bleibt ein Grau.
            If newMax < newMin Then
                Dim mid = (newMax + newMin) * 0.5F
                rr = mid : gg = mid : bb = mid
                Return
            End If
            Dim scale = (newMax - newMin) / (vMax - vMin)
            rr = newMin + (rr - vMin) * scale
            gg = newMin + (gg - vMin) * scale
            bb = newMin + (bb - vMin) * scale
        End Sub

        Private Shared Function LocalZoneValue(v As Single, pixelZones As Single(), shift As Single,
                                               gray As Single, colorExcess As Single) As Single
            Dim pixel = If(pixelZones IsNot Nothing, SampleTable(pixelZones, v), v)
            Dim result = pixel + shift + colorExcess * (v - gray)
            Return If(result < 0.0F, 0.0F, If(result > 1.0F, 1.0F, result))
        End Function

        ''' <summary>Grauwert eines Bildpunkts fuer die Basisschicht: Luminanz im Linearlicht,
        ''' zurueck in den Gammawert. Derselbe Massstab, an dem die Kennlinien gemessen sind (L des
        ''' Bildpunkts, als Grau gelesen).</summary>
        Private Shared Function LocalToneGray(rr As Single, gg As Single, bb As Single,
                                              toLinear As Single(), toGamma As Single()) As Single
            Dim y = 0.2126F * SampleTable(toLinear, Clamp(rr, 0.0F, 1.0F)) +
                    0.7152F * SampleTable(toLinear, Clamp(gg, 0.0F, 1.0F)) +
                    0.0722F * SampleTable(toLinear, Clamp(bb, 0.0F, 1.0F))
            Return SampleTable(toGamma, Clamp(y, 0.0F, 1.0F))
        End Function

        ''' <summary>Die verschmolzene per-Kanal-Skalarkette als STETIGE Tabelle: erst die
        ''' Tonwertkurve (Belichtung, Helligkeit), dann die Punktkurve - beide an 4097 statt 256
        ''' Stuetzstellen und in Single statt Byte. Die Zwischenrundung zwischen den beiden
        ''' entfaellt damit ersatzlos. Kontrast, Lichter, Tiefen, Weiss und Schwarz rechnen in den
        ''' Tonzonen (BuildToneZoneTable), nicht hier.</summary>
        Private Shared Function BuildPointOpScalarTable(adj As ImageAdjustments,
                                                        includeTone As Boolean,
                                                        includeRgbCurve As Boolean) As Single()
            Dim exposureGain = CSng(Math.Pow(2.0, adj.ChainExposure() / 100.0 * 4.0))
            ' Greift die Belichtung nur dem Neuaufbau aus der RAW-Stufe vor, rechnet sie wie dort und
            ' nicht mehr als Faktor im Linearlicht (ImageAdjustments.SourceFollowsRawExposure).
            Dim rawStageGain = 1.0F
            If adj.ChainExposureInRawStage() AndAlso exposureGain <> 1.0F Then
                rawStageGain = exposureGain
                exposureGain = 1.0F
            End If
            ' Ebenso das Knie der Lichterschulter, das der Lichter-Regler im Decode verschiebt
            ' (ImageAdjustments.ChainFollowsRawKnee). Gleiche Knie rechnen wie bisher.
            Dim kneeFrom = 1.0, kneeTo = 1.0
            If adj.ChainFollowsRawKnee() Then
                kneeFrom = adj.KneeInSource
                kneeTo = adj.RawHighlightKnee()
            End If
            Dim brightness = adj.Brightness / 100.0F * 80.0F / 255.0F

            Dim rgbPoints = If(includeRgbCurve, ParseCurvePoints(adj.CurveRgbPoints), Nothing)

            Dim low = ToneTransfer(RawStageExposureAt(0.0F, rawStageGain, kneeFrom, kneeTo), exposureGain, brightness)
            Dim high = ToneTransfer(RawStageExposureAt(1.0F, rawStageGain, kneeFrom, kneeTo), exposureGain, brightness)
            Dim overshootHigh = Math.Max(0.0F, high - 1.0F)
            Dim overshootLow = Math.Max(0.0F, -low)
            Dim shoulder = Clamp(ToneShoulderBase + 0.5F * overshootHigh, ToneShoulderBase, ToneShoulderMax)
            Dim toe = Clamp(ToneShoulderBase + 0.5F * overshootLow, ToneShoulderBase, ToneShoulderMax)
            Dim rolloff = Clamp((overshootHigh + overshootLow) / ToneShoulderBase, 0.0F, 1.0F)
            Dim peakHard = Clamp(high, 0.0F, 1.0F)
            Dim peakSoft = SoftShoulder(high, toe, shoulder)
            Dim peakAfterShoulder = Clamp(peakHard + (peakSoft - peakHard) * rolloff, 0.0F, 1.0F)

            Dim table = New Single(PointOpTableSize - 1) {}
            For i = 0 To PointOpTableSize - 1
                Dim v = i / CSng(PointOpTableSize - 1)

                If includeTone Then
                    Dim y = ToneTransfer(RawStageExposureAt(v, rawStageGain, kneeFrom, kneeTo), exposureGain, brightness)
                    Dim hard = Clamp(y, 0.0F, 1.0F)
                    Dim soft = SoftShoulder(y, toe, shoulder)
                    v = Clamp(hard + (soft - hard) * rolloff, 0.0F, 1.0F)
                    v = PreserveRecoveredHighlightSeparation(v, peakAfterShoulder, adj)
                End If

                If includeRgbCurve Then
                    ' Stetig ausgewertet in 0..255, nicht aus einer 256er-Tabelle gelesen.
                    v = Clamp(CSng(EvaluateCurveSplineReference(rgbPoints, v * 255.0) / 255.0), 0.0F, 1.0F)
                End If

                table(i) = v
            Next
            Return table
        End Function

        ''' <summary>Die Belichtung wie in der RAW-Stufe auf einen Tonwert des entwickelten Bildes
        ''' (Gammawert): zurueck ins Linearlicht und durch die umgekehrte ACR3-Kurve auf die Rampe,
        ''' dort mal Faktor und bei 1 abgeschnitten, dann wieder durch die Kurve. Je Kanal statt nach
        ''' der RGBTone-Regel des Decodes und ohne den Schwarzabzug der Rampe; fuer die Zeit bis zum
        ''' Neuaufbau nah genug. Bei Faktor 1 der Wert selbst, bitgleich.
        '''
        ''' Unterscheiden sich <paramref name="kneeFrom"/> (das Knie im Quellbild) und
        ''' <paramref name="kneeTo"/>, wird zusaetzlich die Schulter getauscht: auf der Rampe
        ''' zurueck durch die alte, mal Faktor, vorwaerts durch die neue. Was im Quellbild am
        ''' Anschlag liegt, bleibt dort, so wie es der Neuaufbau aus ausgefressenen Daten auch
        ''' liefert.</summary>
        Friend Shared Function RawStageExposureAt(v As Single, gain As Single,
                                                  Optional kneeFrom As Double = 1.0,
                                                  Optional kneeTo As Double = 1.0) As Single
            If gain = 1.0F AndAlso kneeFrom = kneeTo Then Return v
            Dim ramp = RawDecodeService.Acr3CurveInverse(SrgbToLinear(Clamp(v, 0.0F, 1.0F)))
            If kneeFrom = kneeTo Then
                Return Clamp(LinearToSrgb(CSng(RawDecodeService.Acr3CurveAt(Math.Min(1.0, ramp * gain)))), 0.0F, 1.0F)
            End If
            Dim scene = RawDecodeService.ShoulderInverse(ramp, kneeFrom) * gain
            Return Clamp(LinearToSrgb(CSng(RawDecodeService.Acr3CurveAt(RawDecodeService.ShoulderAt(scene, kneeTo)))), 0.0F, 1.0F)
        End Function

        ''' <summary>Gibt einer absichtlich abgesenkten Lichterzone nach positiver Belichtung wieder
        ''' Abstand. Die RAW-Lichterrettung legt oberhalb des Weisspunkts echte Sensorwerte frei;
        ''' die Belichtungs-Schulter muss diese aber vor der 8-Bit-Ausgabe verdichten. Ohne den
        ''' Ausgleich wurden bei +0,30 EV Werte von 0,90 bis 1,00 auf nur etwa 0,94 bis 0,97
        ''' zusammengedrueckt. Der anschliessende Lichter-Regler sah dann praktisch eine Flaeche,
        ''' obwohl die Wolkenzeichnung noch vorhanden war.
        '''
        ''' Der Schritt gilt NUR, wenn die RAW-Rettung eingeschaltet ist, die Belichtung positiv
        ''' steht und der Nutzer die Lichter bewusst absenkt. Er erweitert ausschliesslich die
        ''' obere Zone und laesst den Weisspunkt unveraendert; normale Belichtung, JPEGs und ein
        ''' neutraler oder positiver Lichter-Regler bleiben bitgleich.</summary>
        Private Shared Function PreserveRecoveredHighlightSeparation(v As Single, peak As Single, adj As ImageAdjustments) As Single
            ' Nur die Belichtung, die die Kette rechnet: steckt sie schon im Quellbild (RAW-Stufe),
            ' hat die Rampe die geretteten Lichter selbst ausgerollt.
            ' Ebenso, wenn sie nur dem Neuaufbau aus der RAW-Stufe vorgreift.
            Dim chainExposure = adj?.ChainExposure()
            If adj Is Nothing OrElse Not adj.RawHighlightRecovery OrElse chainExposure <= 0.0F OrElse adj.Highlights >= 0.0F Then Return v
            If adj.ChainExposureInRawStage() Then Return v

            ' Der UI-Wert speichert 25 Punkte je EV. Bei +0,30 EV und Lichter -30 ergibt sich
            ' Faktor 1,54: genug Abstand fuer die gerettete Struktur, mit einer festen Obergrenze
            ' gegen unnatuerlichen Mikrokontrast bei extremen Einstellungen.
            Dim ev = Math.Min(5.0, CDbl(chainExposure) / 25.0)
            Dim loweredHighlights = Math.Min(1.0, -adj.Highlights / 100.0)
            Dim expansion = CSng(1.0 + Math.Min(0.75, ev * loweredHighlights * 6.0))
            If expansion <= 1.0F Then Return v

            ' EINE KUBISCHE HERMITE-KURVE zwischen einem unteren Anker und dem tatsaechlichen
            ' Schulter-Weiss (peak): am Anker Wert und Steigung 1, oben Steigung topSlope, Weiss
            ' trifft sie exakt. Mit 1,0 zu verankern waere falsch: nach positiver Belichtung liegt
            ' auch ein weisser Eingabepunkt durch die Schulter etwas darunter und wuerde sonst
            ' ungewollt dunkler.
            '
            ' Mehr Abstand oben bei festem Weiss heisst zwingend weniger Abstand darunter. Die
            ' Kurve verteilt das gleichmaessig ueber 0,75 bis Weiss: ihre kleinste Steigung ist
            ' 1 - (topSlope - 1) / 3, also 0,76 bei Faktor 1,54 und 0,67 am Deckel. Vorher dehnte
            ' eine Einblendung von 0,82 bis 0,94 den Abstand zum Weiss direkt, und knapp ueber
            ' 0,82 fiel die Steigung bis 0,26: helle Haut und heller Himmel verloren dort bis zur
            ' Haelfte ihrer Tonwertstufen. Monoton bleibt sie bis topSlope 2. Der Zuschlag von 35
            ' Prozent auf die obere Steigung haelt die Dehnung oben mindestens dort, wo sie vorher
            ' lag (Pruefung "+EV und Lichter erhalten die Zeichnung": 12,2 statt 12,0 Stufen).
            Const lowAnchor As Single = 0.75F
            If v <= lowAnchor OrElse peak <= lowAnchor + 0.05F Then Return v
            Dim topSlope = Math.Min(2.0F, 1.0F + (expansion - 1.0F) * 1.35F)
            Dim u = Clamp((v - lowAnchor) / (peak - lowAnchor), 0.0F, 1.0F)
            Dim shaped = u + u * u * (1.0F - u) * (1.0F - topSlope)
            Return Clamp(lowAnchor + (peak - lowAnchor) * shaped, 0.0F, 1.0F)
        End Function

        ''' <summary>Haengt eine Kanalkurve stetig an eine bereits gebaute Tabelle. Ersetzt das
        ''' heutige redLut(rgbLut(i)), bei dem der Zwischenwert auf ein Byte gerundet wird.</summary>
        Private Shared Function ChainCurveOntoTable(source As Single(), pointsCsv As String) As Single()
            If ImageAdjustments.IsIdentityCurve(pointsCsv) Then Return source
            Dim points = ParseCurvePoints(pointsCsv)
            Dim table = New Single(PointOpTableSize - 1) {}
            For i = 0 To PointOpTableSize - 1
                table(i) = Clamp(CSng(EvaluateCurveSplineReference(points, source(i) * 255.0) / 255.0), 0.0F, 1.0F)
            Next
            Return table
        End Function

        ''' <summary>Ergibt die Matrix fuer jede Eingabe ein Grau? Drei gleiche Farbzeilen ohne
        ''' Versatz - so sieht der S/W-Look aus, und so jede kuenftige Graumischung.</summary>
        Friend Shared Function IsGrayMatrix(m As Single()) As Boolean
            If m Is Nothing OrElse m.Length < 15 Then Return False
            For c = 0 To 4
                If m(c) <> m(5 + c) OrElse m(c) <> m(10 + c) Then Return False
            Next
            Return m(4) = 0.0F
        End Function

        ''' <summary>Tabellenzugriff mit linearer Interpolation. <paramref name="v"/> wird geklemmt -
        ''' die Kette darf nie ausserhalb [0,1] indizieren.</summary>
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
        ''' Kette unter Parallel.For laeuft und wiederholte Laeufe bitgleich sein muessen.
        ''' Friend, weil das Entrauschen mit Modell beim Zurueckschreiben dieselbe Matrix nimmt.</summary>
        Friend Shared ReadOnly DitherMatrix As Single() = BuildBayer8()

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
        ' GERECHNET WIRD IN DOUBLE, nicht in Single. Der urspruengliche Grund ist inzwischen weg:
        ' GetHslBandAdjustments waehlte das Band einmal ueber HARTE Grenzen, und dort entschied auf
        ' der Grenze das letzte Bit, welcher Regler greift - gemessen sprang Pixel (128,127,124) bei
        ' Farbton exakt 45,0 in Single nach 44,999998 und damit um 27 Tonwerte. Heute wird zwischen
        ' den Baendern uebergeblendet, eine Grenze in diesem Sinn gibt es nicht mehr. Double bleibt
        ' trotzdem: der Gewinn dieser Stufe liegt im Wegfall der BYTE-Zwischenstufe, Single spart
        ' hier nichts Messbares, und die Zahlen der frueheren Messungen bleiben vergleichbar.

        ''' <summary>Kennlinie der HSL-Band-SAETTIGUNG. Eigene Kurve, nicht die von der Luminanz -
        ''' die beiden Groessen vertragen Verschiedenes: L = 1 ist Weiss (und darf nicht ausbrennen),
        ''' S = 1 ist nur volle Farbe.
        '''
        ''' <c>1 − f = (1−v) · e^(−a·v/(1−v))</c>. Die Enden sind dieselben wie bei der Parabel -
        ''' f(0) = 0 (Schwarz bleibt fest), f(1) = 1 (keine Klemmung), monoton, bei a = 0 exakt die
        ''' Kennlinie der Ruhe (bitgleich, ohne Sonderfall). Die STEIGUNG AM NULLPUNKT ist 1 + a und
        ''' damit dieselbe wie beim globalen Saettigungsregler: schwache Farben werden gleich stark
        ''' angehoben, erst nach oben biegt die Kurve weg, weil x2 auf einer bereits satten Farbe
        ''' nicht mehr in den Wertebereich passt.
        '''
        ''' WARUM NICHT MEHR DIE PARABEL: sie bog viel zu frueh ab. Gemessen an echten Farbwerten
        ''' brachte +100 auf dem Hauptband nur zwischen 16 und 58 Prozent mehr Chroma, waehrend der
        ''' globale Regler bei +100 genau verdoppelt - dieselbe Zahl am Regler bedeutete je nach
        ''' Regler etwas voellig anderes. Die Minusseite war davon nie betroffen: −100 entsaettigt
        ''' seit jeher vollstaendig. Der Regler war also einseitig, und genau so fuehlte er sich an.
        ''' Mit dieser Kurve (und dem Bandkern in GetHslBandAdjustments) kommen dieselben Farben auf
        ''' 28 bis 75 Prozent, ohne dass irgendwo geklemmt wird.
        '''
        ''' FOLGE FUER PRESETS: importierte Adobe-Werte wirken auf der Plusseite staerker als vorher.
        ''' Ein Gegenbeleg aus Lightroom liegt nicht vor (siehe LIGHTROOM_ANGLEICH.md, es fehlen
        ''' preset-freie Referenzexporte); die Eichung stuetzt sich auf den Vergleich mit dem
        ''' eigenen globalen Regler, nicht auf eine Messung gegen Adobe.</summary>
        Private Shared Function ApplyHslSaturationGain(value As Double, amount As Double) As Double
            If amount > 1.0 Then amount = 1.0 Else If amount < -1.0 Then amount = -1.0
            If amount = 0.0 Then Return value
            If amount < 0.0 Then Return value * (1.0 + amount)
            If value <= 0.0 Then Return 0.0
            If value >= 1.0 Then Return 1.0
            Return 1.0 - (1.0 - value) * Math.Exp(-amount * value / (1.0 - value))
        End Function

        ''' <summary>Kennlinie der HSL-Band-LUMINANZ. <paramref name="amount"/>
        ''' ist der Reglerwert geteilt durch 100 und mit der Chroma gewichtet, liegt also in [-1, 1];
        ''' <paramref name="value"/> ist L in [0, 1]. Ergebnis bleibt ohne Klemmung in [0, 1].
        '''
        ''' Die SAETTIGUNG hatte einmal dieselbe Kurve und hat inzwischen eine eigene, siehe
        ''' <see cref="ApplyHslSaturationGain"/>: bei L ist das obere Ende Weiss und darf nicht
        ''' ausbrennen, bei S ist es nur volle Farbe.
        '''
        ''' NACH OBEN: <c>v · (1 + a·(1−v))</c> - multiplikativ am unteren Ende, auslaufend zum
        ''' Endwert. Die Kennlinie hat hier zwei Fehler hinter sich, je einer pro Ende:
        ''' 1. URSPRUENGLICH stand hier <c>v · (1+a)</c> mit Kappung auf 1 - das brannte Farben AUS
        '''    (Luminanz +50: jedes Pixel ab L 0,67 wurde exakt Weiss, eine plattgedrueckte Flaeche
        '''    ohne Binnenzeichnung; bei der Saettigung kippte mit der Kappung auch der Farbton).
        ''' 2. Der ERSTE Fix interpolierte linear zum Endwert (<c>v + (1−v)·a</c>) - der hob dafuer
        '''    SCHWARZ an: f(0) = a, ein dunkles sattes Pixel (L 0,05) sprang bei +28 auf 0,32.
        '''    Gemessen am Konzertfoto-Vergleich: die Magenta/Purpur-Buehnenlichter hoben
        '''    den ganzen dunklen Hintergrund an, waehrend der Schwarzboden der Referenz exakt am
        '''    Kurven-Fusspunkt blieb - Adobes Regler nageln Schwarz fest.
        ''' Die Parabel erfuellt beide Enden: f(0) = 0 (Schwarz bleibt Schwarz, unten wirkt sie wie
        ''' die Multiplikation), f(1) = 1 mit Steigung 1−a (laeuft weich aus statt zu klemmen),
        ''' monoton fuer |a| <= 1 (f' = 1 + a − 2av >= 1 − a >= 0). Kein gekappter Bereich, Verlaeufe
        ''' bleiben ueberall unterscheidbar.
        '''
        ''' NACH UNTEN bleibt die Multiplikation gegen 0 (<c>v · (1+a)</c>). Sie kann nicht klemmen
        ''' (amount >= -1) und war immer richtig. Beide Zweige treffen sich bei a = 0 im Wert; die
        ''' Reglersteigung springt dort minimal (1·(1−v)-Faktor nur auf der Plusseite) - das liegt in
        ''' der REGLER-Achse, das Bild bleibt fuer jede Reglerstellung stetig.
        '''
        ''' EICHUNG GEGEN ADOBE: ±100 bedeutet damit wie bei Adobe "volle Wirkung", der Import
        ''' uebernimmt LuminanceAdjustment*/GrayMixer* weiterhin 1:1 (nur der Farbton braucht
        ''' HueImportScale).</summary>
        Private Shared Function ApplyHslBandGain(value As Double, amount As Double) As Double
            ' Die Parabel ist nur fuer |amount| <= 1 randtreu (f(1)=1). Der Preset-Import klemmt
            ' auf +-100, eine handbearbeitete .fpx/.fpxmp kann aber mehr enthalten - dann liefe
            ' das Ergebnis ueber 1 und HslToRgbF darueber aus dem Wertebereich.
            If amount > 1.0 Then amount = 1.0 Else If amount < -1.0 Then amount = -1.0
            If amount >= 0.0 Then Return value * (1.0 + amount * (1.0 - value))
            Return value * (1.0 + amount)
        End Function

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
        ''' Gemessen: gespeichert (100,50,25,128) gibt GetPixel als (199,100,50,128)
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

        ' ── Die Basisschicht fuer Lichter und Tiefen ─────────────────
        '
        ' Lichter und Tiefen werten ihre Kennlinie an der geglaetteten Helligkeit der Umgebung aus,
        ' nicht am Bildpunkt; die Abweichung des Bildpunkts davon, also die Struktur, wandert
        ' unveraendert mit. Ein Lichterregler staucht so die grossen Helligkeitsunterschiede und
        ' laesst die Zeichnung in der hellen Flaeche stehen. Gemessen an Referenzentwicklungen mit je
        ' einem gesetzten Regler: bei Lichter und Tiefen trifft die Basisschicht besser als der
        ' Bildpunkt, bei Kontrast, Weiss und Schwarz schlechter (Audits, RAW_UND_FARBE.md).
        '
        ' Die Basisschicht ist ein kantenerhaltender Fuehrungsfilter, an sich selbst gefuehrt, auf
        ' einem verkleinerten Raster; die Koeffizienten werden auf die volle Aufloesung gelesen und
        ' dort am eigenen Grauwert des Bildpunkts ausgewertet. Harte Kanten bleiben damit in der
        ' Basis und bekommen keinen Saum.

        ''' <summary>Hoechstens so viele Punkte hat das Raster an der langen Kante. Auf dieser Groesse
        ''' sind Radius und Kantenschwelle gemessen.</summary>
        Private Const LocalToneGridEdge As Integer = 1024

        ''' <summary>Radius des Filters als Anteil der langen Kante. Gemessen lagen 1 und 2 % gleich
        ''' gut, 0,5 % und 4 % etwas schlechter; der Anteil macht die Stufe unabhaengig von der
        ''' Aufloesung, die Vorschau rechnet wie der Export. Auf dem Raster gilt er als gebrochener
        ''' Radius (FractionalBoxMean): auf ganze Rasterpunkte gerundet laege er je nach Bildgroesse
        ''' bis zu einem halben Punkt daneben.</summary>
        Private Const LocalToneRadius As Double = 0.01

        ''' <summary>Kantenschwelle des Filters im Quadrat des Gammawerts: Stufen deutlich ueber
        ''' 0,1 bleiben in der Basis. Gemessen war 0,01 besser als 0,003 und 0,03; bei 0,1 geht die
        ''' Kantentreue verloren.</summary>
        Private Const LocalToneEpsilon As Single = 0.01F

        ''' <summary>Breite der Glaettung, gegen die die Feinstruktur gemessen wird: die
        ''' Standardabweichung als Anteil der langen Kante, angenaehert durch drei Kastenmittel.
        '''
        ''' Die Verstaerkungen je Zone (ToneSliderCurves.DetailGain) sind an Vorschauen mit 1024
        ''' Punkten Kante gemessen, geglaettet mit drei Kastenmitteln vom Radius 3 (nominell „0,5 %",
        ''' Radius 0,5 % durch 1,7, gerundet). Das ist eine Standardabweichung von Wurzel aus 12
        ''' Punkten, also 0,338 % der Kante, und genau diese Breite gilt hier fuer jede Bildgroesse
        ''' (Diagnostics/Reglereichung/oertlich.py rechnet dieselbe). Ein auf ganze Rasterpunkte
        ''' gerundeter Radius traf sie nur bei 1024 Rasterpunkten; sonst lag sie je nach Bildgroesse
        ''' zwischen 0,33 und 0,48 %, und Vorschau und Export zeigten verschieden viel Struktur.</summary>
        Private Const LocalToneDetailSigma As Double = 0.0033829117335329633

        ''' <summary>Lange Kante, auf die sich Radius und Glaettung der Basisschicht beziehen; 0 heisst:
        ''' die Kante der sichtbaren Flaeche des Bildes, das gerade durch die Kette laeuft. Gesetzt nur
        ''' fuer die Ebene eines Objekts mit eigenen Anpassungen (WithLocalToneReference): dieselbe
        ''' Ebene entsteht im Bildrender, im Ausschnitt und im Kompositor verschieden gross und, im
        ''' Kompositor, ungedreht. Das Objektrechteck ist dagegen ueberall dasselbe.
        ''' Je Faden, weil Bildrender und Kompositor gleichzeitig laufen koennen; gelesen wird er auf
        ''' dem Faden, der die Kette aufruft, vor dem verteilten Durchlauf.</summary>
        <ThreadStatic> Private Shared _localToneReferenceEdge As Double

        ''' <summary>Welche Bildpunkte die Bildstatistik (MeasureToneReference) zaehlen darf: ein Byte je
        ''' Bildpunkt, 0 heisst draussen. Gesetzt vom Kompositor fuer ein Objekt, das ueber den
        ''' Bildrand reicht (RenderAnnotationOverlaySk); gilt nur fuer eine Bitmap genau dieser Groesse.
        ''' Je Faden wie _localToneReferenceEdge.</summary>
        <ThreadStatic> Private Shared _toneMeasureMask As Byte()
        <ThreadStatic> Private Shared _toneMeasureMaskWidth As Integer
        <ThreadStatic> Private Shared _toneMeasureMaskHeight As Integer

        ''' <summary>Fuehrt <paramref name="work"/> mit <paramref name="mask"/> als Messmaske aus und
        ''' stellt danach die vorige her. Nothing als Maske misst wie gewohnt alles.</summary>
        Friend Shared Function WithToneMeasureMask(mask As Byte(), width As Integer, height As Integer,
                                                   work As Func(Of SKBitmap)) As SKBitmap
            Dim previous = _toneMeasureMask
            Dim previousWidth = _toneMeasureMaskWidth
            Dim previousHeight = _toneMeasureMaskHeight
            _toneMeasureMask = mask
            _toneMeasureMaskWidth = width
            _toneMeasureMaskHeight = height
            Try
                Return work()
            Finally
                _toneMeasureMask = previous
                _toneMeasureMaskWidth = previousWidth
                _toneMeasureMaskHeight = previousHeight
            End Try
        End Function

        ''' <summary>Fuehrt <paramref name="work"/> mit <paramref name="referenceEdge"/> als Bezug der
        ''' Basisschicht aus (siehe _localToneReferenceEdge) und stellt danach den vorigen Bezug her.</summary>
        Friend Shared Function WithLocalToneReference(referenceEdge As Double, work As Func(Of SKBitmap)) As SKBitmap
            Dim previous = _localToneReferenceEdge
            _localToneReferenceEdge = Math.Max(0.0, referenceEdge)
            Try
                Return work()
            Finally
                _localToneReferenceEdge = previous
            End Try
        End Function

        ''' <summary>Die Basisschicht als Koeffizienten des Fuehrungsfilters auf dem Raster:
        ''' Basis = A * Grauwert + B, A und B bilinear auf den Bildpunkt gelesen. Dazu der geglaettete
        ''' Grauwert fuer das Strukturglied. Das Raster deckt nur die sichtbare Flaeche ab und
        ''' beginnt bei OriginX/OriginY.</summary>
        Friend NotInheritable Class LocalToneBase
            Public Width As Integer
            Public Height As Integer
            Public StepSize As Integer
            Public OriginX As Integer
            Public OriginY As Integer
            Public MeanA As Single()
            Public MeanB As Single()
            Public Smooth As Single()

            Public Sub Sample(x As Integer, y As Integer, gray As Single, ByRef baseValue As Single, ByRef smoothValue As Single)
                Dim gx = Math.Max(0.0F, Math.Min(Width - 1.0F, (x - OriginX + 0.5F) / StepSize - 0.5F))
                Dim gy = Math.Max(0.0F, Math.Min(Height - 1.0F, (y - OriginY + 0.5F) / StepSize - 0.5F))
                Dim x0 = CInt(Math.Floor(gx))
                Dim y0 = CInt(Math.Floor(gy))
                Dim x1 = Math.Min(x0 + 1, Width - 1)
                Dim y1 = Math.Min(y0 + 1, Height - 1)
                Dim fx = gx - x0
                Dim fy = gy - y0
                Dim w00 = (1.0F - fx) * (1.0F - fy), w01 = fx * (1.0F - fy)
                Dim w10 = (1.0F - fx) * fy, w11 = fx * fy
                Dim i00 = y0 * Width + x0, i01 = y0 * Width + x1
                Dim i10 = y1 * Width + x0, i11 = y1 * Width + x1
                Dim a = MeanA(i00) * w00 + MeanA(i01) * w01 + MeanA(i10) * w10 + MeanA(i11) * w11
                Dim b = MeanB(i00) * w00 + MeanB(i01) * w01 + MeanB(i10) * w10 + MeanB(i11) * w11
                baseValue = a * gray + b
                smoothValue = Smooth(i00) * w00 + Smooth(i01) * w01 + Smooth(i10) * w10 + Smooth(i11) * w11
            End Sub
        End Class

        ''' <summary>Das Strukturglied als Tabelle ueber dem geglaetteten Grauwert: Faktor minus 1,
        ''' Lichter und Tiefen addiert, fuer die Helligkeit (<paramref name="luminance"/>) oder fuer
        ''' den Abstand der Kanaele vom Grau. Nothing, wenn beide Faktoren ueberall 1 sind. Der
        ''' Verstaerker der Helligkeit haengt an der ganzen Belichtung des Rezepts, auch dem Teil,
        ''' der schon in der RAW-Stufe steckt: geeicht ist er am Ergebnis nach Belichtung.</summary>
        Private Shared Function BuildLocalDetailTable(adj As ImageAdjustments, luminance As Boolean) As Single()
            Dim table = New Single(PointOpTableSize - 1) {}
            Dim any = False
            Dim exposureEv = adj.Exposure / ImageAdjustments.ExposurePointsPerStop
            Dim gainAt = If(luminance,
                            New Func(Of String, Double, Double, Double)(Function(s, v, l) ToneSliderCurves.LuminanceDetailGain(s, v, l, exposureEv)),
                            New Func(Of String, Double, Double, Double)(AddressOf ToneSliderCurves.DetailGain))
            For i = 0 To PointOpTableSize - 1
                Dim l = GrayVToLightness(i / CDbl(PointOpTableSize - 1))
                Dim excess = (gainAt("Highlights", adj.Highlights, l) - 1.0) +
                             (gainAt("Shadows", adj.ShadowsLevel, l) - 1.0)
                table(i) = CSng(excess)
                If Math.Abs(excess) > 0.000001 Then any = True
            Next
            Return If(any, table, Nothing)
        End Function

        ''' <summary>Baut die Basisschicht aus dem Bild, das gerade durch die Kette laeuft: je
        ''' Rasterfeld das Farbmittel, durch die Stufen VOR den Tonzonen geschickt, als Grauwert
        ''' gefiltert. Ein Ausschnitt bekaeme so eine andere Basis als das ganze Bild; die
        ''' Maskenebene im Rechteck nimmt solche Ebenen deshalb nicht
        ''' (ImageAdjustments.UsesLocalToneZones).
        '''
        ''' Gerechnet wird nur ueber das Rechteck der sichtbaren Bildpunkte: ein Objekt auf einer
        ''' Ebene von Bildgroesse bekommt so dasselbe Raster wie auf einer Ebene, die es knapp
        ''' umschliesst. Radius und Glaettung beziehen sich auf <paramref name="referenceEdge"/>,
        ''' ohne Angabe (0) auf die lange Kante dieses Rechtecks; auf einem deckenden Bild ist das die
        ''' lange Bildkante.</summary>
        Private Shared Function BuildLocalToneBase(srcBuf As Byte(), stride As Integer, width As Integer, height As Integer,
                                                   ri As Integer, gi As Integer, bi As Integer, ai As Integer,
                                                   srcPremul As Boolean, chain As PointOpChain,
                                                   referenceEdge As Double) As LocalToneBase
            Dim bounds = VisibleBounds(srcBuf, stride, width, height, ai)
            If bounds.Width <= 0 OrElse bounds.Height <= 0 Then bounds = New SKRectI(0, 0, width, height)
            Dim originX = bounds.Left, originY = bounds.Top
            Dim boundsRight = bounds.Right, boundsBottom = bounds.Bottom
            Dim visibleEdge = Math.Max(bounds.Width, bounds.Height)
            Dim stepSize = Math.Max(1, CInt(Math.Ceiling(visibleEdge / CDbl(LocalToneGridEdge))))
            Dim gw = (bounds.Width + stepSize - 1) \ stepSize
            Dim gh = (bounds.Height + stepSize - 1) \ stepSize
            Dim gray = New Single(gw * gh - 1) {}
            Dim coverage = New Single(gw * gh - 1) {}
            Dim toLinear = _srgbToLinearTable
            Dim toGamma = _linearToSrgbTable
            ' Eigener Messpunkt: das Mitteln liest ALLE Bildpunkte und haengt nur am Bild, der Rest
            ' rechnet auf dem kleinen Raster. Lohnt ein Zwischenspeicher, steht es hier.
            PerformanceTraceService.Measure("Pixel: oertliche Basis, Raster mitteln", Sub()
            ForEachRow(gw, gh,
                Sub(gy)
                    Dim yStart = originY + gy * stepSize
                    Dim yEnd = Math.Min(boundsBottom, yStart + stepSize)
                    For gx = 0 To gw - 1
                        Dim xStart = originX + gx * stepSize
                        Dim xEnd = Math.Min(boundsRight, xStart + stepSize)
                        Dim sumR = 0.0F, sumG = 0.0F, sumB = 0.0F
                        Dim totalAlpha = 0.0F
                        For y = yStart To yEnd - 1
                            Dim row = y * stride
                            For x = xStart To xEnd - 1
                                Dim o = row + x * 4
                                Dim a = srcBuf(o + ai)
                                If a = 0 Then Continue For
                                Dim weight = a / 255.0F
                                If a = 255 OrElse Not srcPremul Then
                                    sumR += srcBuf(o + ri) / 255.0F * weight
                                    sumG += srcBuf(o + gi) / 255.0F * weight
                                    sumB += srcBuf(o + bi) / 255.0F * weight
                                Else
                                    Dim inv = 1.0F / a
                                    sumR += Math.Min(1.0F, srcBuf(o + ri) * inv) * weight
                                    sumG += Math.Min(1.0F, srcBuf(o + gi) * inv) * weight
                                    sumB += Math.Min(1.0F, srcBuf(o + bi) * inv) * weight
                                End If
                                totalAlpha += weight
                            Next
                        Next
                        Dim value = 0.0F
                        If totalAlpha > 0 Then
                            Dim rr = sumR / totalAlpha, gg = sumG / totalAlpha, bb = sumB / totalAlpha
                            ApplyStagesBeforeToneZones(rr, gg, bb, chain, toLinear, toGamma)
                            value = LocalToneGray(rr, gg, bb, toLinear, toGamma)
                        End If
                        gray(gy * gw + gx) = value
                        coverage(gy * gw + gx) = totalAlpha / ((xEnd - xStart) * (yEnd - yStart))
                    Next
                End Sub)
            End Sub)

            ' Radius und Glaettung in Bildpunkten der vollen Aufloesung, umgerechnet aufs Raster, als
            ' Bruchteil statt auf ganze Rasterpunkte gerundet. Die Glaettung zieht die Breite ab, die
            ' schon das Zusammenfassen eines Rasterfelds mitbringt (ein Kasten aus stepSize Punkten
            ' hat die Varianz (stepSize^2 - 1) / 12 Bildpunkte zum Quadrat).
            Dim edge = If(referenceEdge > 0.0, referenceEdge, CDbl(visibleEdge))
            Dim radius = LocalToneRadius * edge / stepSize
            Dim sigmaPixels = LocalToneDetailSigma * edge
            Dim gridVariance = Math.Max(0.0, (sigmaPixels * sigmaPixels - (CDbl(stepSize) * stepSize - 1.0) / 12.0) /
                                             (CDbl(stepSize) * stepSize))
            Dim smoothRadius = FractionalBoxRadiusForVariance(gridVariance / 3.0)
            Dim squares = New Single(gray.Length - 1) {}
            For i = 0 To gray.Length - 1
                squares(i) = gray(i) * gray(i)
            Next
            ' Ein leeres Rasterfeld ist KEIN schwarzer Bildpunkt. Die Deckung wird bei jedem
            ' Mittel mitgefuehrt; auf deckenden Bildern bleibt der bisherige schnelle Weg. Ihr
            ' Kastenmittel haengt nur am Radius und wird je Radius einmal gerechnet, nicht bei
            ' jedem der sieben Mittel neu.
            Dim hasTransparency = coverage.Any(Function(value) value < 0.999999F)
            Dim weights = If(hasTransparency, coverage, Nothing)
            Dim weightsMean = If(hasTransparency, FractionalBoxMean(coverage, gw, gh, radius), Nothing)
            Dim weightsSmooth = If(hasTransparency, FractionalBoxMean(coverage, gw, gh, smoothRadius), Nothing)
            Dim mean = BoxMeanWithCoverage(gray, weights, weightsMean, gw, gh, radius)
            Dim meanSquare = BoxMeanWithCoverage(squares, weights, weightsMean, gw, gh, radius)
            Dim coefA = New Single(gray.Length - 1) {}
            Dim coefB = New Single(gray.Length - 1) {}
            For i = 0 To gray.Length - 1
                Dim variance = Math.Max(0.0F, meanSquare(i) - mean(i) * mean(i))
                coefA(i) = variance / (variance + LocalToneEpsilon)
                coefB(i) = mean(i) - coefA(i) * mean(i)
            Next
            Dim smooth = gray
            For pass = 1 To 3
                smooth = BoxMeanWithCoverage(smooth, weights, weightsSmooth, gw, gh, smoothRadius)
            Next
            Return New LocalToneBase With {
                .Width = gw, .Height = gh, .StepSize = stepSize,
                .OriginX = originX, .OriginY = originY,
                .MeanA = BoxMeanWithCoverage(coefA, weights, weightsMean, gw, gh, radius),
                .MeanB = BoxMeanWithCoverage(coefB, weights, weightsMean, gw, gh, radius),
                .Smooth = smooth
            }
        End Function

        ''' <summary>Wie weit die Basisschicht eines Bildes mit der langen Kante
        ''' <paramref name="edge"/> um einen Bildpunkt herum liest, in Bildpunkten: der Fuehrungsfilter
        ''' mittelt zweimal mit seinem Radius (erst die Statistik, dann die Koeffizienten), die
        ''' Glaettung des Strukturglieds dreimal mit ihrem; dazu ein Rasterfeld fuer das bilineare
        ''' Lesen. Fuer Ebenen, die nur ueber einem Ausschnitt neu gerechnet werden sollen.</summary>
        Friend Shared Function LocalToneReach(edge As Double) As Double
            If edge <= 0.0 Then Return 0.0
            Dim stepSize = Math.Max(1.0, Math.Ceiling(edge / LocalToneGridEdge))
            Dim guidedReach = 2.0 * (Math.Ceiling(LocalToneRadius * edge / stepSize) + 1.0) * stepSize
            Dim sigma = LocalToneDetailSigma * edge / stepSize
            Dim smoothReach = 3.0 * (Math.Ceiling(FractionalBoxRadiusForVariance(sigma * sigma / 3.0)) + 1.0) * stepSize
            Return Math.Max(guidedReach, smoothReach) + 2.0 * stepSize
        End Function

        ''' <summary>Das Rechteck um alle nicht voll durchsichtigen Bildpunkte (rechte und untere Kante
        ''' ausschliessend); leer, wenn keiner sichtbar ist. Auf einem deckenden Bild liest es je
        ''' Zeile nur die beiden Randpunkte.</summary>
        Private Shared Function VisibleBounds(srcBuf As Byte(), stride As Integer, width As Integer, height As Integer,
                                              ai As Integer) As SKRectI
            Dim top = -1
            For y = 0 To height - 1
                If RowHasVisiblePixel(srcBuf, y * stride, width, ai) Then
                    top = y
                    Exit For
                End If
            Next
            If top < 0 Then Return SKRectI.Empty
            Dim bottom = top
            For y = height - 1 To top Step -1
                If RowHasVisiblePixel(srcBuf, y * stride, width, ai) Then
                    bottom = y
                    Exit For
                End If
            Next
            Dim left = width, right = -1
            For y = top To bottom
                Dim row = y * stride
                For x = 0 To left - 1
                    If srcBuf(row + x * 4 + ai) <> 0 Then
                        left = x
                        Exit For
                    End If
                Next
                For x = width - 1 To right + 1 Step -1
                    If srcBuf(row + x * 4 + ai) <> 0 Then
                        right = x
                        Exit For
                    End If
                Next
            Next
            If right < left Then Return SKRectI.Empty
            Return New SKRectI(left, top, right + 1, bottom + 1)
        End Function

        Private Shared Function RowHasVisiblePixel(srcBuf As Byte(), row As Integer, width As Integer, ai As Integer) As Boolean
            For x = 0 To width - 1
                If srcBuf(row + x * 4 + ai) <> 0 Then Return True
            Next
            Return False
        End Function

        ''' <summary>Mittelt nur sichtbare Anteile; transparente Felder verdunkeln die Umgebung nicht.
        ''' <paramref name="coverageMean"/> ist das Kastenmittel der Deckung mit demselben Radius; der
        ''' Aufrufer rechnet es einmal fuer alle Mittel dieses Radius.</summary>
        Private Shared Function BoxMeanWithCoverage(source As Single(), coverage As Single(), coverageMean As Single(),
                                                    width As Integer, height As Integer, radius As Double) As Single()
            If coverage Is Nothing Then Return FractionalBoxMean(source, width, height, radius)
            Dim weighted = New Single(source.Length - 1) {}
            For i = 0 To source.Length - 1
                weighted(i) = source(i) * coverage(i)
            Next
            Dim sums = FractionalBoxMean(weighted, width, height, radius)
            For i = 0 To sums.Length - 1
                sums(i) = If(coverageMean(i) > 0.0000001F, sums(i) / coverageMean(i), 0.0F)
            Next
            Return sums
        End Function

        ''' <summary>Der gebrochene Radius eines Kastenmittels (FractionalBoxMean) mit der Varianz
        ''' <paramref name="variance"/> in Rasterpunkten zum Quadrat. Mit dem Bruchteil f waechst die
        ''' Varianz stetig von r(r+1)/3 (f = 0) bis (r+1)(r+2)/3 (f = 1); drei solche Mittel
        ''' hintereinander haben die dreifache Varianz.</summary>
        Private Shared Function FractionalBoxRadiusForVariance(variance As Double) As Double
            If variance <= 0.0 Then Return 0.0
            Dim whole = 0
            While (whole + 1.0) * (whole + 2.0) / 3.0 <= variance
                whole += 1
            End While
            ' Summe von k^2 ueber -whole..whole und das Gewicht der beiden Randpunkte.
            Dim inner = whole * (whole + 1.0) * (2.0 * whole + 1.0) / 3.0
            Dim edgeSquare = (whole + 1.0) * (whole + 1.0)
            Dim part = (variance * (2.0 * whole + 1.0) - inner) / (2.0 * (edgeSquare - variance))
            Return whole + Math.Max(0.0, Math.Min(1.0, part))
        End Function

        ''' <summary>Die Stufen der Kette VOR den Tonzonen, fuer ein Rasterfeld der Basisschicht:
        ''' Filmnegativ, Weissabgleich, Farbmatrix und die Tonwertkurve davor. Dieselbe Rechnung und
        ''' Reihenfolge wie im Durchlauf (RunPointOpChain, Stufen 1 bis 3); wer dort eine Stufe vor
        ''' den Tonzonen einfuegt, traegt sie hier nach.</summary>
        Private Shared Sub ApplyStagesBeforeToneZones(ByRef rr As Single, ByRef gg As Single, ByRef bb As Single,
                                                      chain As PointOpChain, toLinear As Single(), toGamma As Single())
            If chain.NegR IsNot Nothing Then
                rr = SampleTable(chain.NegR, rr)
                gg = SampleTable(chain.NegG, gg)
                bb = SampleTable(chain.NegB, bb)
                If chain.NegMonochrome Then
                    Dim gray = 0.299F * rr + 0.587F * gg + 0.114F * bb
                    rr = gray : gg = gray : bb = gray
                End If
            End If
            Dim wbm = chain.WhiteBalanceMatrix
            If wbm IsNot Nothing Then
                Dim linR = SampleTable(toLinear, rr)
                Dim linG = SampleTable(toLinear, gg)
                Dim linB = SampleTable(toLinear, bb)
                rr = SampleTable(toGamma, Clamp(wbm(0) * linR + wbm(1) * linG + wbm(2) * linB, 0.0F, 1.0F))
                gg = SampleTable(toGamma, Clamp(wbm(3) * linR + wbm(4) * linG + wbm(5) * linB, 0.0F, 1.0F))
                bb = SampleTable(toGamma, Clamp(wbm(6) * linR + wbm(7) * linG + wbm(8) * linB, 0.0F, 1.0F))
            End If
            Dim m = chain.ColorMatrix
            If m IsNot Nothing Then
                Dim nr = m(0) * rr + m(1) * gg + m(2) * bb + m(4)
                Dim ng = m(5) * rr + m(6) * gg + m(7) * bb + m(9)
                Dim nb = m(10) * rr + m(11) * gg + m(12) * bb + m(14)
                rr = Clamp(nr, 0.0F, 1.0F)
                gg = Clamp(ng, 0.0F, 1.0F)
                bb = Clamp(nb, 0.0F, 1.0F)
            End If
            If chain.ToneBefore IsNot Nothing Then
                rr = SampleTable(chain.ToneBefore, rr)
                gg = SampleTable(chain.ToneBefore, gg)
                bb = SampleTable(chain.ToneBefore, bb)
            End If
        End Sub

        ''' <summary>Kastenmittel mit gebrochenem Radius <paramref name="radius"/>: die Punkte bis zum
        ''' ganzen Teil des Radius zaehlen voll, der naechste auf jeder Seite mit dem Bruchteil. So
        ''' waechst die Breite stetig mit dem Radius statt in ganzen Rasterpunkten; bei ganzem Radius
        ''' ist es das gewoehnliche Kastenmittel. Zeilen- und dann spaltenweise ueber Praefixsummen;
        ''' am Rand ueber die vorhandenen Punkte gemittelt.</summary>
        Private Shared Function FractionalBoxMean(source As Single(), width As Integer, height As Integer, radius As Double) As Single()
            Dim whole = CInt(Math.Floor(Math.Max(0.0, radius)))
            Dim part = Math.Max(0.0, radius) - whole
            Dim rows = New Single(source.Length - 1) {}
            Dim result = New Single(source.Length - 1) {}
            ForEachRow(width, height,
                Sub(y)
                    Dim offset = y * width
                    Dim prefix = New Double(width) {}
                    For x = 0 To width - 1
                        prefix(x + 1) = prefix(x) + source(offset + x)
                    Next
                    For x = 0 To width - 1
                        rows(offset + x) = CSng(FractionalWindowMean(prefix, width, x, whole, part))
                    Next
                End Sub)
            ' Spalten in STREIFEN von BoxColumnBand nebeneinanderliegenden Spalten, Zeile fuer Zeile
            ' durchlaufen. Je Spalte einzeln hiesse, mit einer ganzen Rasterzeile Abstand durch den
            ' Speicher zu springen - bei 1024 Feldern 4 KB je Schritt, und jeder Schritt ein neuer
            ' Zwischenspeicher-Fehlgriff. Gerechnet wird je Spalte exakt dasselbe in derselben
            ' Reihenfolge wie vorher (Praefixsumme von oben nach unten, dann dasselbe Fenster), das
            ' Ergebnis ist also bitgleich.
            Dim bands = (width + BoxColumnBand - 1) \ BoxColumnBand
            Dim columnBand As Action(Of Integer) =
                Sub(band)
                    Dim x0 = band * BoxColumnBand
                    Dim span = Math.Min(BoxColumnBand, width - x0)
                    ' Praefixsummen des Streifens, zeilenweise abgelegt: (height + 1) Zeilen zu span.
                    Dim prefix = New Double((height + 1) * span - 1) {}
                    For y = 0 To height - 1
                        Dim src = y * width + x0
                        Dim before = y * span
                        Dim after = before + span
                        For j = 0 To span - 1
                            prefix(after + j) = prefix(before + j) + rows(src + j)
                        Next
                    Next
                    For y = 0 To height - 1
                        Dim dst = y * width + x0
                        For j = 0 To span - 1
                            result(dst + j) = CSng(FractionalWindowMeanStrided(prefix, span, j, height, y, whole, part))
                        Next
                    Next
                End Sub
            If CLng(width) * height < ParallelPixelThreshold Then
                For band = 0 To bands - 1
                    columnBand(band)
                Next
            Else
                Parallel.For(0, bands, columnBand)
            End If
            Return result
        End Function

        ''' <summary>Wie viele nebeneinanderliegende Spalten FractionalBoxMean gemeinsam durchlaeuft.
        ''' 64 Werte zu acht Byte je Zeile sind acht Zwischenspeicherzeilen: genug, dass jeder Zugriff
        ''' trifft, und klein genug, dass sich die Streifen auf die Kerne verteilen.</summary>
        Private Const BoxColumnBand As Integer = 64

        ''' <summary><see cref="FractionalWindowMean"/> fuer Praefixsummen, die mit Abstand
        ''' <paramref name="stride"/> ab <paramref name="offset"/> abgelegt sind. Dieselbe Rechnung in
        ''' derselben Reihenfolge, damit das Ergebnis bitgleich bleibt.</summary>
        Private Shared Function FractionalWindowMeanStrided(prefix As Double(), stride As Integer, offset As Integer,
                                                            count As Integer, center As Integer,
                                                            whole As Integer, part As Double) As Double
            Dim i0 = Math.Max(0, center - whole)
            Dim i1 = Math.Min(count, center + whole + 1)
            Dim sum = prefix(i1 * stride + offset) - prefix(i0 * stride + offset)
            Dim weight As Double = i1 - i0
            If part > 0.0 Then
                Dim before = center - whole - 1
                If before >= 0 Then
                    sum += part * (prefix((before + 1) * stride + offset) - prefix(before * stride + offset))
                    weight += part
                End If
                Dim after = center + whole + 1
                If after < count Then
                    sum += part * (prefix((after + 1) * stride + offset) - prefix(after * stride + offset))
                    weight += part
                End If
            End If
            Return sum / weight
        End Function

        ''' <summary>Ein Fenster des gebrochenen Kastenmittels ueber den Praefixsummen einer Zeile oder
        ''' Spalte mit <paramref name="count"/> Punkten.</summary>
        Private Shared Function FractionalWindowMean(prefix As Double(), count As Integer, center As Integer,
                                                     whole As Integer, part As Double) As Double
            Dim i0 = Math.Max(0, center - whole)
            Dim i1 = Math.Min(count, center + whole + 1)
            Dim sum = prefix(i1) - prefix(i0)
            Dim weight As Double = i1 - i0
            If part > 0.0 Then
                Dim before = center - whole - 1
                If before >= 0 Then
                    sum += part * (prefix(before + 1) - prefix(before))
                    weight += part
                End If
                Dim after = center + whole + 1
                If after < count Then
                    sum += part * (prefix(after + 1) - prefix(after))
                    weight += part
                End If
            End If
            Return sum / weight
        End Function

        ' ── Der eine Durchlauf ───────────────────────────────────────────────────

        Private Shared Function RunPointOpChain(source As SKBitmap, chain As PointOpChain) As SKBitmap
            Dim srcBuf As Byte() = Nothing, dstBuf As Byte() = Nothing
            Dim stride, ri, gi, bi, ai As Integer
            Dim sLen = 0, dstLen = 0
            Try
            If Not TryRentRgbaLikeBuffer(source, srcBuf, sLen, stride, ri, gi, bi, ai) Then Return source

            ' Quellen sind IMMER 8 Bit (Bgra8888/Rgba8888; TryBorrowRgbaLikeBuffer lehnt anderes ab
            ' und die Kette laesst die Quelle dann unveraendert). Ein 16-Bit-Arbeitsbild wurde
            ' gebaut, gemessen und wieder ausgebaut (siehe Rendering-Notizen Abschnitt 8) - der
            ' Gewinn lag nicht in der Bittiefe der Quelle, sondern darin, dass diese Kette
            ' ZWISCHENDURCH nicht mehr quantisiert.
            ' Premul oder nicht? Das entscheidet, ob RGB vor dem Rechnen durch Alpha geteilt werden
            ' muss. Vorher wurde IMMER geteilt und am Ende wieder multipliziert - bei einer
            ' Unpremul-Quelle (PSD mit Transparenz, ICO) ergab das gemessen (128,98,24) statt der
            ' korrekten (238,88,13), weil die Division die Werte ueber 1 hebt und die Klemmung sie
            ' dort abschneidet. Zusaetzlich landeten vormultiplizierte Werte in einem Bitmap, das
            ' weiterhin als Unpremul deklariert war.
            Dim srcPremul = source.AlphaType = SKAlphaType.Premul
            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Dim width = source.Width
            ' Dicht gepackt (width*4 je Zeile), also ohne Zeilenauffuellung - und die Kette schreibt
            ' jeden Bildpunkt. Ein geliehenes, ungenulltes Feld ist hier deshalb ohne Folgen.
            dstLen = width * source.Height * 4
            dstBuf = ArrayPool(Of Byte).Shared.Rent(dstLen)
            Dim dstStride = width * 4

            Dim m = chain.ColorMatrix
            Dim wbm = chain.WhiteBalanceMatrix
            Dim toLinear = _srgbToLinearTable
            Dim toGamma = _linearToSrgbTable
            Dim sr = chain.ScalarR
            Dim sg = chain.ScalarG
            Dim sb = chain.ScalarB
            Dim toneBefore = chain.ToneBefore
            Dim toneZones = chain.ToneZones
            Dim negR = chain.NegR
            Dim negG = chain.NegG
            Dim negB = chain.NegB
            Dim negMono = chain.NegMonochrome
            Dim lumCurve = chain.LuminanceCurve
            Dim vibrance = chain.Vibrance
            Dim vibranceExponential = chain.VibranceExponential
            Dim vibranceByHue = chain.VibranceByHue
            Dim curvesMelissa = chain.CurvesInMelissa
            Dim hslAdj = chain.Hsl
            Dim schattenToenung = chain.ShadowTint
            Dim splitAdj = chain.SplitToning
            Dim pm = chain.PresetMatrix
            Dim pmStrength = chain.PresetStrength
            Dim toneAfterPreset = chain.ToneAfterPreset
            Dim cube = chain.CubeTable
            Dim cubeSize = chain.CubeSize
            Dim cubeStrength = chain.CubeStrength
            Dim mixer = chain.Mixer
            Dim mapShadow = chain.MapShadow
            Dim mapHigh = chain.MapHighlight
            Dim mapAmount = chain.MapAmount
            Dim posterize = chain.PosterizeLevels
            Dim threshold = chain.ThresholdLevel
            ' Einmal ausserhalb der Pixelschleife entschieden (schleifeninvariant).
            Dim pointColors = chain.PointColors
            Dim hslBlockAktiv = lumCurve IsNot Nothing OrElse hslAdj IsNot Nothing OrElse
                                splitAdj IsNot Nothing OrElse vibrance <> 0.0F OrElse pointColors IsNot Nothing
            ' Die Basisschicht fuer Lichter und Tiefen entsteht aus DIESEM Bild, vor dem Durchlauf.
            Dim localZones = chain.LocalToneZones
            Dim localDetail = chain.LocalDetailGain
            Dim localColor = chain.LocalColorGain
            Dim localBase As LocalToneBase = Nothing
            If localZones IsNot Nothing Then
                ' Eigener Messpunkt: die Basis ist eine Nachbarschaftsrechnung und steckt sonst
                ' unsichtbar in "Punktkette" - ob deren Zeit in der Punktrechnung oder hier liegt,
                ' entscheidet, was zuerst schneller werden muss.
                localBase = PerformanceTraceService.Measure("Pixel: Punktkette, oertliche Basis",
                    Function() BuildLocalToneBase(srcBuf, stride, width, source.Height, ri, gi, bi, ai, srcPremul, chain,
                                                  _localToneReferenceEdge))
            End If

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
                        ' Gewicht des Farbfelds, dessen Bereich die Vorschau zeigt; -1 ohne Anzeige.
                        Dim pointColorShown = -1.0
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

                        ' --- 1b. Weissabgleich als Adaption, im LINEARLICHT ---
                        ' Dekodieren, 3x3, kodieren. Die Klemmung liegt im LINEAREN Raum, also VOR
                        ' dem Kodieren: eine Adaption kann einen Kanal ueber 1 heben (bei starker
                        ' Erwaermung das Rot), und das Kodieren einer Zahl groesser 1 gaebe Werte
                        ' ausserhalb der Tabelle.
                        If wbm IsNot Nothing Then
                            Dim linR = SampleTable(toLinear, rr)
                            Dim linG = SampleTable(toLinear, gg)
                            Dim linB = SampleTable(toLinear, bb)
                            Dim ar = wbm(0) * linR + wbm(1) * linG + wbm(2) * linB
                            Dim ag = wbm(3) * linR + wbm(4) * linG + wbm(5) * linB
                            Dim ab = wbm(6) * linR + wbm(7) * linG + wbm(8) * linB
                            rr = SampleTable(toGamma, If(ar < 0.0F, 0.0F, If(ar > 1.0F, 1.0F, ar)))
                            gg = SampleTable(toGamma, If(ag < 0.0F, 0.0F, If(ag > 1.0F, 1.0F, ag)))
                            bb = SampleTable(toGamma, If(ab < 0.0F, 0.0F, If(ab > 1.0F, 1.0F, ab)))
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
                        ' Die HSL-Saettigung VOR der Tonstufe festhalten: die per-Kanal-Kurven
                        ' duerfen sie nach unten nicht druecken (Restore unten). Gemessen am
                        ' Adobe-Referenz-Render (DCP-Basis, DNG-SDK-Reihenfolge) desselben Fotos:
                        ' Die Grundabstimmung der Referenz hebt die Tiefen mit KOMPRESSIVER Steigung
                        ' (~0,6) und verdoppelt dabei trotzdem die Chroma - das geht nur, wenn die
                        ' Tonstufen sich konstant-S verhalten (Chroma waechst mit der Hebung mit).
                        ' Unsere per-Kanal-Anwendung skaliert die Chroma dagegen mit der Steigung
                        ' und entsaettigte die Tiefen (0,059 gegen LRs 0,106 am Konzertfoto).
                        ' NUR nach unten begrenzen: wo die Kurve die Saettigung ERHOEHT (S-Kurven-
                        ' Kontrast in den Mitten), bleibt die per-Kanal-Wirkung erhalten. Und NUR
                        ' fuer die TIEFEN (Gewicht 1 unter L 0,10, smoothstep auslaufend bis 0,25):
                        ' der Vergleich traegt nur den Schatten-Lift als konstant-S; ein globaler
                        ' Restore uebersaettigte gemessen die Mitten (Bin 0,2-0,3: 0,169 gegen LRs
                        ' 0,067), wo die Referenz der per-Kanal-Senkung des Presets folgt.
                        '
                        ' CHROMA-TOR ist Pflicht: HSL-S ist in den Tiefen riesig,
                        ' obwohl kaum Farbe da ist - ein fast schwarzes Pixel (4,0,0) hat S = 1,0.
                        ' Ohne Tor haette der Restore aus einem einzelnen Sensor-LSB nach der
                        ' Aufhellung gesaettigtes Rot gemacht ((61,0,0) statt (31,30,30)). Getort
                        ' wird an der ECHTEN Chroma (max-min), die in HSL genau der Zaehler von S
                        ' ist: dunkles Teal (10,25,25) hat 0,059 und kommt voll durch, ein LSB
                        ' Rauschen 0,016 und bleibt draussen.
                        Dim satVorTon As Double = 0.0
                        If sr IsNot Nothing Then
                            ' S und L direkt aus max/min statt ueber RgbToHslF: der Farbton wird hier
                            ' nicht gebraucht, und diese Stufe laeuft bei JEDER Tonbearbeitung.
                            Dim vMax = If(rr > gg, If(rr > bb, rr, bb), If(gg > bb, gg, bb))
                            Dim vMin = If(rr < gg, If(rr < bb, rr, bb), If(gg < bb, gg, bb))
                            Dim lVor = (vMax + vMin) / 2.0
                            Dim chromaVor = vMax - vMin
                            If lVor < 0.25 AndAlso chromaVor > 0.02 Then
                                Dim nenner = 1.0 - Math.Abs(2.0 * lVor - 1.0)
                                If nenner > 0.00001 Then
                                    satVorTon = chromaVor / nenner
                                    Dim torC = Math.Min(1.0, (chromaVor - 0.02) / 0.04)
                                    torC = torC * torC * (3.0 - 2.0 * torC)
                                    Dim tFade = 1.0
                                    If lVor > 0.1 Then
                                        tFade = 1.0 - (lVor - 0.1) / 0.15
                                        tFade = tFade * tFade * (3.0 - 2.0 * tFade)
                                    End If
                                    satVorTon *= torC * tFade
                                End If
                            End If
                            If toneZones IsNot Nothing OrElse localZones IsNot Nothing OrElse curvesMelissa Then
                                If toneBefore IsNot Nothing Then
                                    rr = SampleTable(toneBefore, rr)
                                    gg = SampleTable(toneBefore, gg)
                                    bb = SampleTable(toneBefore, bb)
                                End If
                                If localZones IsNot Nothing Then
                                    Dim gray = LocalToneGray(rr, gg, bb, toLinear, toGamma)
                                    Dim baseValue As Single, smoothValue As Single
                                    localBase.Sample(x, y, gray, baseValue, smoothValue)
                                    Dim smoothAt = Clamp(smoothValue, 0.0F, 1.0F)
                                    Dim detailShift = If(localDetail IsNot Nothing,
                                                         SampleTable(localDetail, smoothAt) * (gray - smoothValue),
                                                         0.0F)
                                    Dim colorExcess = If(localColor IsNot Nothing, SampleTable(localColor, smoothAt), 0.0F)
                                    ApplyLocalToneZones(rr, gg, bb, toneZones, localZones, baseValue, gray, detailShift, colorExcess)
                                ElseIf toneZones IsNot Nothing Then
                                    ApplyToneZones(rr, gg, bb, toneZones)
                                End If
                            End If
                            If curvesMelissa Then
                                ApplyCurvesInMelissa(rr, gg, bb, sr, sg, sb, toLinear, toGamma)
                            Else
                                rr = SampleTable(sr, rr)
                                gg = SampleTable(sg, gg)
                                bb = SampleTable(sb, bb)
                            End If
                        End If

                        ' --- 4./5./6. Luminanzkurve, HSL-Baender, Split-Toning ---
                        ' Alle drei brauchen HSL. Der Roundtrip wird deshalb EINMAL gemacht statt
                        ' dreimal - in der alten Pipeline lief er pro Stufe erneut, jedes Mal ueber
                        ' Bytes und mit eigener Rundung.
                        If hslBlockAktiv Then
                            Dim h As Double, sat As Double, lum As Double
                            RgbToHslF(rr, gg, bb, h, sat, lum)

                            ' Saettigungs-Restore der Tonstufe (siehe Kommentar an Stufe 3).
                            If sat < satVorTon Then sat = satVorTon

                            If lumCurve IsNot Nothing Then
                                lum = SampleTable(lumCurve, Clamp(CSng(lum), 0.0F, 1.0F))
                            End If

                            ' --- Dynamik: chroma-gewichtete Saettigung ---
                            ' Faktor (1 + v*(1-sat)) haengt an der vorhandenen Saettigung: schwach
                            ' saturierte Pixel werden am staerksten angehoben, ein bereits kraeftiges
                            ' (sat=1) bleibt unangetastet, ein neutrales Grau (sat=0) bleibt neutral.
                            ' Damit kann Dynamik keine Farbe erfinden, die die Saettigung entfernt hat.
                            ' Positiv zusaetzlich je Farbton (VibranceByHue): Hauttoene bekommen wie bei
                            ' der Referenz deutlich weniger als Gruen, Cyan und Blau. Und sie haelt die
                            ' LEUCHTDICHTE: mehr HSL-Saettigung bei gleichem HSL-L macht eine Farbe
                            ' sichtbar heller (gemessen +3,3 L in den Mitten bei +100), die Referenz
                            ' dagegen dunkelt leicht ab (-2,4). Mit gehaltener Leuchtdichte sank der
                            ' Abstand zur Referenz bei +100 von 7,45 auf 6,15 (siehe VibranceLumaKeepGain).
                            If vibranceExponential Then
                                sat = sat * Math.Exp(vibrance * (1.0 - sat))
                            ElseIf vibranceByHue IsNot Nothing Then
                                Dim v = SampleVibranceByHue(vibranceByHue, h)
                                Dim satNew = Math.Max(0.0, Math.Min(1.0, sat * (1.0 + v * (1.0 - sat))))
                                If satNew <> sat Then
                                    Dim ar As Double, ag As Double, ab As Double
                                    Dim br As Double, bg As Double, bbv As Double
                                    HslToRgbF(h, sat, lum, ar, ag, ab)
                                    HslToRgbF(h, satNew, lum, br, bg, bbv)
                                    Dim lr = SampleTable(toLinear, CSng(br))
                                    Dim lg = SampleTable(toLinear, CSng(bg))
                                    Dim lb = SampleTable(toLinear, CSng(bbv))
                                    Dim y0 = 0.2126 * SampleTable(toLinear, CSng(ar)) +
                                             0.7152 * SampleTable(toLinear, CSng(ag)) +
                                             0.0722 * SampleTable(toLinear, CSng(ab))
                                    Dim y1 = 0.2126 * lr + 0.7152 * lg + 0.0722 * lb
                                    If y1 > 0.000001 Then
                                        Dim k = y0 / y1
                                        br = SampleTable(toGamma, Clamp(CSng(lr * k), 0.0F, 1.0F))
                                        bg = SampleTable(toGamma, Clamp(CSng(lg * k), 0.0F, 1.0F))
                                        bbv = SampleTable(toGamma, Clamp(CSng(lb * k), 0.0F, 1.0F))
                                    End If
                                    RgbToHslF(br, bg, bbv, h, sat, lum)
                                End If
                            ElseIf vibrance <> 0.0F Then
                                sat = Math.Max(0.0, Math.Min(1.0, sat * (1.0 + vibrance * (1.0 - sat))))
                            End If

                            If hslAdj IsNot Nothing Then
                                Dim hueShift As Single = 0, satShift As Single = 0, lumShift As Single = 0
                                GetHslBandAdjustments(ReferenceBandHue(h), hslAdj, hueShift, satShift, lumShift)
                                ' Bandwirkung mit der Chroma GEWICHTEN: ein neutrales Grau hat keinen
                                ' Farbton, bekam aber ueber h=0 die volle LUMINANZ des Rot-Bands ab (nur
                                ' satShift war durch sat*x=0 automatisch neutral) - der Regler
                                ' "Rot -> Luminanz" verschob damit alle Grauflaechen, und bei
                                ' fast-neutralen Pixeln entschied das Rauschen, welches Band greift
                                ' (fleckige Flaechen). Smoothstep, damit die Schwelle keine Kante zieht.
                                '
                                ' ZWEI Tore statt einem: fuer FARBTON/SAETTIGUNG zaehlt die ECHTE
                                ' CHROMA - HSL-S ist in Tiefen/Lichtern riesig, obwohl kaum Farbe da
                                ' ist (dunkles Teal 10/25/25: S 0,43, Chroma 0,06), der Mischer
                                ' entsaettigte dort voll. Dunkles Rauschen ist die Fehlerklasse dieses
                                ' Tors: S rauscht dort gross, C bleibt klein. Die LUMINANZ behaelt das
                                ' alte S-Tor: ein C-Tor hebelte gemessen die dunklen Luminanzbaender
                                ' (Aqua -43) mit aus und kippte die Tonlage (P50 0,091 -> 0,110). In
                                ' den Mitten ist C ~ S, dort aendert sich praktisch nichts.
                                Dim chromaC = sat * (1.0 - Math.Abs(2.0 * lum - 1.0))
                                Dim gateCs = Math.Min(1.0, chromaC / 0.2)
                                gateCs = gateCs * gateCs * (3.0 - 2.0 * gateCs)
                                Dim chromaW = Math.Min(1.0, sat / 0.1)
                                chromaW = chromaW * chromaW * (3.0 - 2.0 * chromaW)
                                h = (h + hueShift * gateCs + 360.0) Mod 360.0
                                sat = ApplyHslSaturationGain(sat, satShift * gateCs / 100.0)
                                lum = ApplyHslBandGain(lum, lumShift * chromaW / 100.0)
                            End If

                            Dim hr As Double, hg As Double, hb As Double
                            HslToRgbF(h, sat, lum, hr, hg, hb)

                            ' --- Punktfarbe, nach dem Farbmischer und vor der Farbgradierung ---
                            ' Im HSV von LINEAREM ProPhoto, wo sie gemessen ist: dekodieren, in
                            ' ProPhoto umrechnen, die Farbfelder anwenden, zurück. Was danach
                            ' ausserhalb von sRGB liegt, wird je Kanal geklemmt, wie beim
                            ' Referenzprogramm.
                            If pointColors IsNot Nothing Then
                                Dim pr = PointColorMath.SrgbDecode(Math.Max(0.0, Math.Min(1.0, hr)))
                                Dim pg = PointColorMath.SrgbDecode(Math.Max(0.0, Math.Min(1.0, hg)))
                                Dim pb = PointColorMath.SrgbDecode(Math.Max(0.0, Math.Min(1.0, hb)))
                                PointColorMath.Transform(PointColorMath.SrgbToProPhoto, pr, pg, pb)
                                pointColorShown = PointColorMath.Apply(pointColors, pr, pg, pb)
                                PointColorMath.Transform(PointColorMath.ProPhotoToSrgb, pr, pg, pb)
                                hr = PointColorMath.SrgbEncode(Math.Max(0.0, Math.Min(1.0, pr)))
                                hg = PointColorMath.SrgbEncode(Math.Max(0.0, Math.Min(1.0, pg)))
                                hb = PointColorMath.SrgbEncode(Math.Max(0.0, Math.Min(1.0, pb)))
                            End If
                            rr = CSng(hr) : gg = CSng(hg) : bb = CSng(hb)

                            ' Bei S/W wartet die Tonung hinter der Matrix (ToneAfterPreset).
                            If splitAdj IsNot Nothing AndAlso Not toneAfterPreset Then
                                ApplyColorGrade(rr, gg, bb, chain)
                            End If
                        ElseIf sr IsNot Nothing AndAlso satVorTon > 0.0 Then
                            ' Saettigungs-Restore auch OHNE HSL-Stufen (reine Ton-Bearbeitung).
                            ' Der HSL-Roundtrip laeuft nur fuer Pixel, die das Chroma-Tor oben
                            ' ueberhaupt passiert haben - fuer alle anderen (die grosse Mehrheit)
                            ' bleibt der Pfad bitgleich UND kostenfrei.
                            Dim hN As Double, sN As Double, lN As Double
                            RgbToHslF(rr, gg, bb, hN, sN, lN)
                            If sN < satVorTon Then
                                Dim wr As Double, wg As Double, wb As Double
                                HslToRgbF(hN, satVorTon, lN, wr, wg, wb)
                                rr = CSng(wr) : gg = CSng(wg) : bb = CSng(wb)
                            End If
                        End If

                        ' --- Schattentoenung (Kalibrierung): Gruen/Magenta nur in den Tiefen ---
                        If schattenToenung <> 0.0F Then
                            Dim brightness = 0.299F * rr + 0.587F * gg + 0.114F * bb
                            ' Gewicht faellt linear bis zur Bildmitte auf 0 - darueber unberuehrt.
                            Dim gewicht = Math.Max(0.0F, 1.0F - brightness * 2.0F)
                            If gewicht > 0.0F Then
                                Dim strength = schattenToenung / 100.0F * 0.12F * gewicht
                                ' Positiv = Magenta (Gruen runter), negativ = Gruen. Uebliche Belegung.
                                gg = Clamp(gg - strength, 0.0F, 1.0F)
                                rr = Clamp(rr + strength * 0.5F, 0.0F, 1.0F)
                                bb = Clamp(bb + strength * 0.5F, 0.0F, 1.0F)
                            End If
                        End If

                        ' --- 6b. Weitere Korrekturen: auf das fertig korrigierte Farbbild, vor Preset
                        ' und LUT. Erst die Kanaele mischen, dann die Helligkeit auf den Verlauf
                        ' umsetzen.
                        If mixer IsNot Nothing Then
                            Dim mr = mixer(0) * rr + mixer(1) * gg + mixer(2) * bb + mixer(3)
                            Dim mg = mixer(4) * rr + mixer(5) * gg + mixer(6) * bb + mixer(7)
                            Dim mb = mixer(8) * rr + mixer(9) * gg + mixer(10) * bb + mixer(11)
                            rr = Clamp(mr, 0.0F, 1.0F) : gg = Clamp(mg, 0.0F, 1.0F) : bb = Clamp(mb, 0.0F, 1.0F)
                        End If

                        If mapShadow IsNot Nothing Then
                            Dim lumMap = Clamp(0.299F * rr + 0.587F * gg + 0.114F * bb, 0.0F, 1.0F)
                            Dim tr = mapShadow(0) + (mapHigh(0) - mapShadow(0)) * lumMap
                            Dim tg = mapShadow(1) + (mapHigh(1) - mapShadow(1)) * lumMap
                            Dim tb = mapShadow(2) + (mapHigh(2) - mapShadow(2)) * lumMap
                            rr += (tr - rr) * mapAmount : gg += (tg - gg) * mapAmount : bb += (tb - bb) * mapAmount
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
                            ' Tonung eines S/W-Bildes: erst jetzt, auf das Grau.
                            If toneAfterPreset Then ApplyColorGrade(rr, gg, bb, chain)
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

                        ' --- 9. Tontrennung und Schwellenwert, ganz am Ende: sie verfremden das
                        ' Ergebnis und sollen auf allem anderen arbeiten. Die Stufen liegen genau auf
                        ' ganzen Bytewerten - das Dithering darunter laesst sie dann unberuehrt, und
                        ' die Flaechen bleiben wirklich flach.
                        If posterize >= 2 Then
                            Dim steps = posterize - 1
                            rr = CSng(Math.Round(Math.Round(Clamp(rr, 0.0F, 1.0F) * steps) / steps * 255.0) / 255.0)
                            gg = CSng(Math.Round(Math.Round(Clamp(gg, 0.0F, 1.0F) * steps) / steps * 255.0) / 255.0)
                            bb = CSng(Math.Round(Math.Round(Clamp(bb, 0.0F, 1.0F) * steps) / steps * 255.0) / 255.0)
                        End If
                        If threshold >= 1 Then
                            Dim lumT = (0.299F * rr + 0.587F * gg + 0.114F * bb) * 255.0F
                            Dim t = If(lumT >= threshold - 0.0001F, 1.0F, 0.0F)
                            rr = t : gg = t : bb = t
                        End If

                        If pointColorShown >= 0.0 Then PointColorMath.Visualize(pointColorShown, rr, gg, bb)

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

            Marshal.Copy(dstBuf, 0, result.GetPixels(), dstLen)
            Return result
            Finally
                ReturnPooledBuffer(srcBuf)
                ReturnPooledBuffer(dstBuf)
            End Try
        End Function

    End Class

End Namespace
