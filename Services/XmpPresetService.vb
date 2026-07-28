Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Text.RegularExpressions

Namespace Services

    ''' <summary>Liest ein XMP-Preset (.xmp) und übersetzt es in ein
    ''' <see cref="ImageAdjustments"/>, das nur den LOOK trägt (Licht, Farbe, Details, Effekte, HSL,
    ''' Split-Toning, Tonwertkurven) - keine Geometrie, keine Objekte, keine Auswahl.
    ''' Bewusst ohne ViewModel: den Editor interessiert derselbe Look wie die Stapelverarbeitung der
    ''' Galerie, und zwei Abbildungen derselben XMP-Schlüssel würden garantiert auseinanderlaufen.</summary>
    Public Class XmpPresetService

        ''' <summary>Nothing, wenn die Datei fehlt oder keine crs:-Werte enthält. Alle Felder, die das
        ''' Preset nicht setzt, bleiben auf ihrem neutralen Standard - ein geladenes Preset ersetzt den
        ''' Look also vollständig, statt sich mit dem vorherigen zu vermischen.</summary>
        Public Shared Function LoadLook(xmpPath As String) As ImageAdjustments
            If String.IsNullOrWhiteSpace(xmpPath) OrElse Not File.Exists(xmpPath) Then Return Nothing
            Dim rawText = File.ReadAllText(xmpPath)
            ' Das Profil MUSS aus dem ungekuerzten Text gelesen werden - gleich danach faellt sein Block
            ' weg, damit seine Attribute (crs:Name, crs:Amount, crs:Copyright ...) nicht in die flache
            ' Wertetabelle sickern und dort gleichnamige Regler des Kopf-Blocks ueberschreiben.
            Dim look = ParseLookProfile(rawText)
            Dim xmpText = StripNestedCrsBlocks(rawText)
            Dim values = ParseXmpValues(xmpText)
            If values.Count = 0 Then Return Nothing

            Dim adj As New ImageAdjustments()
            Dim d As Double

            ''' Die *2012-Schlüssel stammen aus Prozessversion 2012 und sind bei allem
            ''' üblich, was danach entstand. Ältere Presets (PV2003/PV2010) schreiben dieselben Regler OHNE
            ''' Suffix und teils unter anderen Namen. Ohne Rückfall kommen sie vollständig leer an, ohne dass
            ''' irgendetwas darauf hindeutet - der Nutzer sieht ein Preset, das nichts tut. Deshalb je Regler
            ''' erst der moderne Schlüssel, dann der alte.
            If TryGetXmpDouble(values, "Exposure2012", d) Then
                ' Belichtung deckt den vollen Adobe-Bereich ±5 EV ab: ×25 → ±125.
                adj.Exposure = Clamp(d * 25.0, -125, 125)
            ElseIf TryGetXmpDouble(values, "Exposure", d) Then
                ' Auch die alte Belichtung steht in Blendenstufen, gleiche Skalierung.
                adj.Exposure = Clamp(d * 25.0, -125, 125)
            End If
            ' Das alte crs:Brightness (-150..+150) hat in PV2012 keine Entsprechung mehr; es kommt der
            ' Helligkeit am nächsten und wird auf deren ±100 gestaucht.
            If TryGetXmpDouble(values, "Brightness", d) Then adj.Brightness = Clamp100(d / 1.5)
            If TryGetXmpDouble(values, "Contrast2012", d) Then
                adj.Contrast = Clamp100(d)
            ElseIf TryGetXmpDouble(values, "Contrast", d) Then
                adj.Contrast = Clamp100(d)
            End If
            If TryGetXmpDouble(values, "Highlights2012", d) Then adj.Highlights = Clamp100(d)
            If TryGetXmpDouble(values, "Shadows2012", d) Then
                adj.ShadowsLevel = Clamp100(d)
            ElseIf TryGetXmpDouble(values, "FillLight", d) Then
                ' PV2003/2010 hellte Schatten über „Aufhelllicht" auf: 0..100, nur in eine Richtung.
                adj.ShadowsLevel = Clamp(d, 0, 100)
            End If
            If TryGetXmpDouble(values, "Whites2012", d) Then adj.Whites = Clamp100(d)
            If TryGetXmpDouble(values, "Blacks2012", d) Then adj.Blacks = Clamp100(d)
            If TryGetXmpDouble(values, "Clarity2012", d) Then adj.Clarity = Clamp100(d)
            If TryGetXmpDouble(values, "Texture", d) Then adj.[Structure] = Clamp100(d)
            If TryGetXmpDouble(values, "Dehaze", d) Then adj.Haze = Clamp100(-d)
            If TryGetXmpDouble(values, "Vibrance", d) Then adj.Vibrance = Clamp100(d)
            If TryGetXmpDouble(values, "Saturation", d) Then adj.Saturation = Clamp100(d)
            If TryGetXmpDouble(values, "Sharpness", d) Then adj.Sharpness = Clamp(d, 0, 150)   ' Adobe-Bereich 0..150
            ' Schärfen-Feinregler. crs:SharpenRadius ist 0.5..3.0 (Adobe-Standard 1.0 = neutral),
            ' unser Radius 0..100: (r-1)*50, unter 1.0 auf 0 geklemmt. crs:SharpenDetail ist 0..100.
            If TryGetXmpDouble(values, "SharpenRadius", d) Then adj.SharpenRadius = Clamp((d - 1.0) * 50.0, 0, 100)
            If TryGetXmpDouble(values, "SharpenDetail", d) Then adj.SharpenDetail = Clamp(d, 0, 100)
            ' crs:SharpenEdgeMasking ist 0..100 wie unser Regler. Stand in 15 von 25 untersuchten Presets
            ' mit Werten bis 85 und fiel bisher komplett weg: unser Import schärfte damit die ganze Fläche
            ' inklusive Himmel und Haut, wo Adobe nur die Kanten anfasst.
            If TryGetXmpDouble(values, "SharpenEdgeMasking", d) Then adj.SharpenMasking = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "LuminanceSmoothing", d) Then adj.NoiseReduction = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "LuminanceNoiseReductionDetail", d) Then adj.NoiseReductionDetail = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "ColorNoiseReduction", d) Then adj.ColorNoiseReduction = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "GrainAmount", d) Then adj.Grain = Clamp(d, 0, 100)
            ' crs:GrainSize/GrainFrequency sind 0..100 wie unsere Regler.
            If TryGetXmpDouble(values, "GrainSize", d) Then adj.GrainSize = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "GrainFrequency", d) Then adj.GrainFrequency = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "PostCropVignetteAmount", d) Then
                adj.Vignette = Clamp(-d, -150, 150)
                ' Mittelpunkt und weiche Kante sind semantisch deckungsgleich mit VignetteTransition/
                ' VignetteFeather (beide 0-100, hoeher = weiter aussen bzw. weicher) - aber nur bei
                ' AKTIVER Vignette uebernehmen, sonst ueberschrieben Preset-Defaults die App-Defaults.
                ' PostCropVignetteRoundness (-100..100) uebernehmen wir jetzt ebenfalls: Adobe steuert
                ' damit die Kreisform, wir die Achsen-Verzerrung des Ovals. Die Skalen sind gleich, die
                ' Wirkung ist eine ANNAEHERUNG - eine runde/eckige Adobe-Vignette wird so als weiter/enger
                ' gestrecktes Oval nachgebildet, statt ganz zu fehlen.
                If d <> 0 Then
                    Dim v As Double
                    If TryGetXmpDouble(values, "PostCropVignetteMidpoint", v) Then adj.VignetteTransition = Clamp(v, 0, 100)
                    If TryGetXmpDouble(values, "PostCropVignetteFeather", v) Then adj.VignetteFeather = Clamp(v, 0, 100)
                    If TryGetXmpDouble(values, "PostCropVignetteRoundness", v) Then adj.VignetteRoundness = Clamp100(v)
                End If
            End If
            ' crs:PostCropVignetteStyle: 1 = Highlight Priority, 2 = Color Priority, 3 = Paint Overlay.
            ' Unser Standard (ColorPriority) entspricht Adobes 2; nur die abweichenden Werte umschalten.
            Dim styleVal As Double
            If TryGetXmpDouble(values, "PostCropVignetteStyle", styleVal) Then
                Select Case CInt(Math.Round(styleVal))
                    Case 1 : adj.VignetteStyle = VignetteStyle.HighlightPriority
                    Case 3 : adj.VignetteStyle = VignetteStyle.PaintOverlay
                    Case Else : adj.VignetteStyle = VignetteStyle.ColorPriority
                End Select
            End If

            ''' Schwarzweiß. PV2012 schreibt crs:Treatment="Black &amp; White", ältere Fassungen
            ''' crs:ConvertToGrayscale="True" - beide Formen kommen in freier Wildbahn vor. Ohne das kam
            ''' ein S/W-Preset in voller Farbe an, und zwar wortlos: alle anderen Regler stimmten, nur die
            ''' Umwandlung fehlte. Der Filtername ist zugleich der Schaltschlüssel in BuildFilterPresetMatrix.
            ''' DRITTE Quelle: das PROFIL (crs:Look). Presets neuerer Erzeuger setzen keinen der
            ''' beiden Schlüssel, sondern verweisen nur auf ein monochromes Kameraprofil ("Adobe
            ''' Monochrome", Gruppe "B&W") - gemessen an einer echten Sammlung betraf das 3 von 25
            ''' Presets, und alle drei kamen BUNT an. Dieselbe Falle wie oben, nur eine Ebene höher.
            Dim monoFromKeys = IsXmpTrue(values, "ConvertToGrayscale") OrElse
                               GetXmpString(values, "Treatment").StartsWith("Black", StringComparison.OrdinalIgnoreCase)
            Dim monoFromLook = look IsNot Nothing AndAlso look.IsMonochrome()
            If monoFromKeys OrElse monoFromLook Then
                adj.FilterPreset = "S/W"
                adj.FilterStrength = ImageAdjustments.DefaultFilterStrength("S/W")
                ' Kommt das Schwarzweiß NUR aus dem Profil, trägt dessen crs:Amount die Stärke: damit
                ' wird ein Profil gegen die Farbwiedergabe geblendet (1.0 = voll). Ein B/W-Profil bei 0,67
                ' ergibt dort ein teilentsättigtes Bild - genau das macht unsere FilterStrength auch.
                ' Bei crs:SupportsAmount="false" ist der Regler beim Erzeuger gesperrt, dann gilt voll.
                ' Steht crs:Treatment/ConvertToGrayscale in der Datei, ist die Umwandlung ausdrücklich
                ' gewollt und bleibt voll - eine halbe Umwandlung ergäbe nur ein blasses Bild.
                If Not monoFromKeys AndAlso look.HasAmount AndAlso look.SupportsAmount Then
                    adj.FilterStrength = Clamp(look.Amount * 100.0, 0, 100)
                End If
            End If

            ''' WEISSABGLEICH. crs:IncrementalTemperature/-Tint ist die relative ±100-Verschiebung, die
            ''' unserem Temperatur-/Tönungsregler direkt entspricht (Erzeuger schreiben sie für Nicht-RAW-
            ''' Dateien, portable Presets liegen praktisch immer in dieser Form vor) - sie hat deshalb
            ''' IMMER Vorrang. crs:Temperature dagegen ist ein ABSOLUTER Kelvin-Wert (z.B. 5500), wie ihn
            ''' RAW-Presets schreiben. Es gibt keine aufnahmeunabhängig korrekte Umrechnung in unseren
            ''' relativen Regler - ohne die aufnahmespezifische Referenztemperatur ist jede Übernahme eine
            ''' NÄHERUNG. Wir rechnen sie über eine feste Tageslicht-Referenz (D65) im mired-Raum um
            ''' (siehe KelvinToRelativeTemperature) und übernehmen sie nur als RÜCKFALL, wenn kein
            ''' Incremental vorliegt: für ein bei Tageslicht aufgenommenes Bild trifft sie gut, für
            ''' Kunstlicht/Nacht liegt sie systematisch daneben. crs:Tint ohne Präfix wird weiterhin
            ''' als relative Tönung akzeptiert.
            ' KAMERAKALIBRIERUNG. Steckte in 3 von 5 untersuchten Presets und war der groesste
            ' verbliebene Import-Ausfall: sie dreht und saettigt die Primaerfarben und macht damit
            ' einen guten Teil des charakteristischen Farbstichs aus. Ohne sie kam ein Preset
            ' strukturell unvollstaendig an, ohne dass etwas darauf hindeutete.
            ' Achtung bei den Namen: crs:RedHue ist die KALIBRIERUNG, crs:HueAdjustmentRed dagegen
            ' das HSL-Farbband - zwei verschiedene Regler mit aehnlichem Namen.
            If TryGetXmpDouble(values, "RedHue", d) Then adj.CalibrationRedHue = Clamp100(d)
            If TryGetXmpDouble(values, "RedSaturation", d) Then adj.CalibrationRedSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "GreenHue", d) Then adj.CalibrationGreenHue = Clamp100(d)
            If TryGetXmpDouble(values, "GreenSaturation", d) Then adj.CalibrationGreenSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "BlueHue", d) Then adj.CalibrationBlueHue = Clamp100(d)
            If TryGetXmpDouble(values, "BlueSaturation", d) Then adj.CalibrationBlueSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "ShadowTint", d) Then adj.CalibrationShadowTint = Clamp100(d)

            ' TORWÄCHTER crs:WhiteBalance. "As Shot" heißt: den Weißabgleich der AUFNAHME
            ' behalten, das Preset fasst ihn nicht an. Ein Preset in diesem Modus schleppt trotzdem oft
            ' crs:Temperature/crs:Tint mit (der Erzeuger schreibt den zuletzt gesehenen Stand einfach mit),
            ' und die Werte gehören dann zu einem fremden Foto. Ohne diesen Wächter zog ein solches
            ' Preset den Weißabgleich des eigenen Bildes auf die Aufnahmebedingungen eines anderen.
            ' NUR bei "As Shot" gesperrt: "Custom", "Auto" und die Vorgaben ("Daylight", "Cloudy" …)
            ' meinen ausdrücklich eine Änderung.
            Dim whiteBalanceMode = GetXmpString(values, "WhiteBalance")
            Dim keepsCaptureWhiteBalance = whiteBalanceMode.Replace(" ", "").Equals("AsShot", StringComparison.OrdinalIgnoreCase)
            If Not keepsCaptureWhiteBalance Then
                If TryGetXmpDouble(values, "IncrementalTemperature", d) Then
                    adj.Temperature = Clamp100(d)
                ElseIf TryGetXmpDouble(values, "Temperature", d) AndAlso d >= 1000 Then
                    ' Absolutes Kelvin (Adobe-Bereich 2000..50000). Der >=1000-Wächter trennt es sicher von
                    ' einem versehentlich relativen Wert; crs:Temperature ist bei Adobe immer Kelvin.
                    ' Referenz: crs:AsShotTemperature, wenn vorhanden (Sidecar-XMPs von RAWs tragen den
                    ' Aufnahme-Weißabgleich) - damit ist die Näherung aufnahmespezifisch exakt. Der
                    ' gleiche >=1000-Wächter, damit ein kaputter Wert nicht als Referenz durchgeht.
                    Dim asShotKelvin As Double
                    If TryGetXmpDouble(values, "AsShotTemperature", asShotKelvin) AndAlso asShotKelvin >= 1000 Then
                        adj.Temperature = KelvinToRelativeTemperature(d, asShotKelvin)
                    Else
                        adj.Temperature = KelvinToRelativeTemperature(d)
                    End If
                End If
                If TryGetXmpDouble(values, "IncrementalTint", d) Then
                    ' Tint deckt den vollen Adobe-Bereich ab: absolutes crs:Tint reicht bis ±150.
                    adj.Tint = Clamp(d, -150, 150)
                ElseIf TryGetXmpDouble(values, "Tint", d) Then
                    adj.Tint = Clamp(d, -150, 150)
                End If
            End If

            ' HUE-Skalierung: Adobes HueAdjustment* ±100 verschiebt den Farbton
            ' nur etwa bis zum NACHBARBAND (Bandabstand 30°, also ~±30°). Unsere Engine wendet den
            ' Bandwert dagegen direkt als Grad-Rotation an (±100 => ±100°) - 1:1 uebernommen drehte
            ' ein importiertes Preset Farbtoene rund dreimal so weit wie im Original (Rot wurde
            ' Gelbgruen statt Orange). Nur die Hue-Werte skalieren; Saettigung/Luminanz sind
            ' Prozent-Faktoren mit vergleichbarer Semantik. Kein Rueckweg betroffen: HueAdjustment*
            ' wird nirgends exportiert (nur gelesen).
            Const HueImportScale As Double = 0.3
            If TryGetXmpDouble(values, "HueAdjustmentRed", d) Then adj.RedHue = Clamp100(d * HueImportScale)
            If TryGetXmpDouble(values, "SaturationAdjustmentRed", d) Then adj.RedSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentRed", d) Then adj.RedLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentOrange", d) Then adj.OrangeHue = Clamp100(d * HueImportScale)
            If TryGetXmpDouble(values, "SaturationAdjustmentOrange", d) Then adj.OrangeSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentOrange", d) Then adj.OrangeLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentYellow", d) Then adj.YellowHue = Clamp100(d * HueImportScale)
            If TryGetXmpDouble(values, "SaturationAdjustmentYellow", d) Then adj.YellowSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentYellow", d) Then adj.YellowLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentGreen", d) Then adj.GreenHue = Clamp100(d * HueImportScale)
            If TryGetXmpDouble(values, "SaturationAdjustmentGreen", d) Then adj.GreenSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentGreen", d) Then adj.GreenLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentAqua", d) Then adj.AquaHue = Clamp100(d * HueImportScale)
            If TryGetXmpDouble(values, "SaturationAdjustmentAqua", d) Then adj.AquaSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentAqua", d) Then adj.AquaLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentBlue", d) Then adj.BlueHue = Clamp100(d * HueImportScale)
            If TryGetXmpDouble(values, "SaturationAdjustmentBlue", d) Then adj.BlueSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentBlue", d) Then adj.BlueLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentPurple", d) Then adj.PurpleHue = Clamp100(d * HueImportScale)
            If TryGetXmpDouble(values, "SaturationAdjustmentPurple", d) Then adj.PurpleSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentPurple", d) Then adj.PurpleLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentMagenta", d) Then adj.MagentaHue = Clamp100(d * HueImportScale)
            If TryGetXmpDouble(values, "SaturationAdjustmentMagenta", d) Then adj.MagentaSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentMagenta", d) Then adj.MagentaLuminance = Clamp100(d)

            ''' SCHWARZWEISS-MISCHER (crs:GrayMixer*). Im S/W-Modus ersetzt das Schema das ganze HSL-Panel
            ''' durch diese acht Regler - sie bestimmen, wie hell jeder Farbbereich im Grau landet, und
            ''' sind damit der eigentliche Charakter eines S/W-Presets (gemessen an einer echten Sammlung:
            ''' Silvertide zieht Aqua +32, Blau +39, Purpur +27, Magenta +26 hoch). Ohne sie kam JEDES
            ''' S/W-Preset als dieselbe flache Entsättigung an.
            ''' Abgebildet auf die vorhandenen HSL-LUMINANZ-Bänder - es sind dieselben acht Farbbereiche,
            ''' und die HSL-Stufe läuft in der Punktoperationskette VOR der S/W-Matrix (siehe
            ''' ImageProcessorPointOps: HSL-Bänder, dann Preset-Farbmatrix). Die Gewichtung greift also
            ''' noch am farbigen Pixel, genau wie bei Adobe.
            ''' ÜBERSCHREIBT LuminanceAdjustment* bewusst: im S/W-Modus wertet das Schema die Farb-HSL-
            ''' Regler nicht mehr aus, ein aus dem Farbteil geerbter Wert wäre ein Rest, kein Look.
            ''' Nur im S/W-Modus - sonst würde ein Farbpreset, das die Werte für einen möglichen
            ''' S/W-Wechsel bloß mitschleppt, seine Farbluminanzen verlieren.
            If String.Equals(adj.FilterPreset, "S/W", StringComparison.Ordinal) Then
                If TryGetXmpDouble(values, "GrayMixerRed", d) Then adj.RedLuminance = Clamp100(d)
                If TryGetXmpDouble(values, "GrayMixerOrange", d) Then adj.OrangeLuminance = Clamp100(d)
                If TryGetXmpDouble(values, "GrayMixerYellow", d) Then adj.YellowLuminance = Clamp100(d)
                If TryGetXmpDouble(values, "GrayMixerGreen", d) Then adj.GreenLuminance = Clamp100(d)
                If TryGetXmpDouble(values, "GrayMixerAqua", d) Then adj.AquaLuminance = Clamp100(d)
                If TryGetXmpDouble(values, "GrayMixerBlue", d) Then adj.BlueLuminance = Clamp100(d)
                If TryGetXmpDouble(values, "GrayMixerPurple", d) Then adj.PurpleLuminance = Clamp100(d)
                If TryGetXmpDouble(values, "GrayMixerMagenta", d) Then adj.MagentaLuminance = Clamp100(d)
            End If

            ''' crs:SplitToning*Hue ist bereits 0..360, *Saturation 0..100 - beides deckungsgleich mit den
            ''' Split-Toning-Reglern dieser App, keine Skalierung nötig. Balance ist bei beiden -100..100.
            If TryGetXmpDouble(values, "SplitToningShadowHue", d) Then adj.ColorGradeShadowHue = Clamp(d, 0, 360)
            If TryGetXmpDouble(values, "SplitToningShadowSaturation", d) Then adj.ColorGradeShadowSaturation = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "SplitToningHighlightHue", d) Then adj.ColorGradeHighlightHue = Clamp(d, 0, 360)
            If TryGetXmpDouble(values, "SplitToningHighlightSaturation", d) Then adj.ColorGradeHighlightSaturation = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "SplitToningBalance", d) Then adj.ColorGradeBalance = Clamp100(d)

            ''' FARBGRADIERUNG (crs:ColorGrade*, ab den neueren Prozessversionen). Sie hat das Split-Toning oben
            ''' abgelöst: Presets aus dieser Zeit schreiben NUR noch diese Schlüssel, und weil wir bis
            ''' allein die alten lasen, kam ihre Farbstimmung überhaupt nicht an - wortlos,
            ''' denn alle übrigen Regler stimmten. Sie stehen bewusst NACH den alten Schlüsseln: liegen
            ''' beide in derselben Datei (zur Rückwärtskompatibilität steht oft beides in der Datei),
            ''' gewinnt die neuere Angabe. Die Skalen sind deckungsgleich, keine Umrechnung nötig.
            If TryGetXmpDouble(values, "ColorGradeShadowHue", d) Then adj.ColorGradeShadowHue = Clamp(d, 0, 360)
            If TryGetXmpDouble(values, "ColorGradeShadowSat", d) Then adj.ColorGradeShadowSaturation = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "ColorGradeShadowLum", d) Then adj.ColorGradeShadowLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "ColorGradeMidtoneHue", d) Then adj.ColorGradeMidtoneHue = Clamp(d, 0, 360)
            If TryGetXmpDouble(values, "ColorGradeMidtoneSat", d) Then adj.ColorGradeMidtoneSaturation = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "ColorGradeMidtoneLum", d) Then adj.ColorGradeMidtoneLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "ColorGradeHighlightHue", d) Then adj.ColorGradeHighlightHue = Clamp(d, 0, 360)
            If TryGetXmpDouble(values, "ColorGradeHighlightSat", d) Then adj.ColorGradeHighlightSaturation = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "ColorGradeHighlightLum", d) Then adj.ColorGradeHighlightLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "ColorGradeGlobalHue", d) Then adj.ColorGradeGlobalHue = Clamp(d, 0, 360)
            If TryGetXmpDouble(values, "ColorGradeGlobalSat", d) Then adj.ColorGradeGlobalSaturation = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "ColorGradeGlobalLum", d) Then adj.ColorGradeGlobalLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "ColorGradeBlending", d) Then adj.ColorGradeBlending = Clamp(d, 0, 100)

            ''' Tonwertkurven liegen als verschachtelte rdf:Seq/rdf:li-Listen vor, nicht als einfache
            ''' Attribute - der Attribut-Regex oben kann sie nicht erfassen, daher eine eigene, gezielte
            ''' Extraktion je Kurven-Element.
            ''' Neben der Punktkurve führt das Schema eine zweite, PARAMETRISCHE Kurve: vier Zonenregler
            ''' (Schatten/Dunkel/Licht/Lichter), deren Zonengrenzen selbst wieder Parameter sind. Beide
            ''' wirken übereinander. Wird sie ignoriert, fehlt Presets, die ihren Tonwert-Look darüber
            ''' aufbauen, genau dieser Teil. Sie wird deshalb in die Punktkurve eingerechnet - eine
            ''' Annäherung an Adobes Kurvenform, kein exakter Nachbau.
            ' Punktkurve: bevorzugt PV2012; fehlt sie (alte PV2003/2010-Presets), auf die Alt-Kurve
            ' <crs:ToneCurve> zurückfallen (gleiches rdf:Seq-Format). Beide durchlaufen dieselbe Faltung.
            Dim mainCurvePoints = ParseXmpCurvePoints(xmpText, "ToneCurvePV2012")
            If mainCurvePoints Is Nothing Then mainCurvePoints = ParseXmpCurvePoints(xmpText, "ToneCurve")
            Dim combinedCurve = ApplyParametricCurve(values, mainCurvePoints)
            If combinedCurve IsNot Nothing Then adj.CurveRgbPoints = combinedCurve
            Dim redCurve = ParseXmpCurvePoints(xmpText, "ToneCurvePV2012Red")
            If redCurve IsNot Nothing Then adj.CurveRedPoints = redCurve
            Dim greenCurve = ParseXmpCurvePoints(xmpText, "ToneCurvePV2012Green")
            If greenCurve IsNot Nothing Then adj.CurveGreenPoints = greenCurve
            Dim blueCurve = ParseXmpCurvePoints(xmpText, "ToneCurvePV2012Blue")
            If blueCurve IsNot Nothing Then adj.CurveBluePoints = blueCurve

            Return adj
        End Function

        ''' <summary>Rückfall-Referenz für die Kelvin-Näherung: D65 = sRGB-Weißpunkt = neutrales
        ''' Tageslicht. Ein Preset mit genau diesem Kelvin-Wert lässt die Temperatur unverändert
        ''' (Regler 0). Greift nur, wenn das XMP keine crs:AsShotTemperature trägt.</summary>
        Private Const WhiteBalanceReferenceKelvin As Double = 6500.0

        ''' <summary>Näherung eines absoluten Kelvin-Weißabgleichs an unseren relativen ±100-Regler.
        ''' Gerechnet wird im mired-Raum (1e6/K), weil eine Kelvin-Differenz dort perzeptuell viel
        ''' gleichmäßiger wirkt als in Kelvin selbst. Adobe-Konvention: höhere Kelvin = wärmeres Bild,
        ''' also positiver Regler - das ergibt sich hier von selbst, weil höhere Kelvin niedrigere mireds
        ''' haben. Skala bewusst 1 mired ≈ 1 Reglerpunkt und hart auf ±100 geclampt.
        '''
        ''' <paramref name="referenceKelvin"/> ist der Aufnahme-Weißabgleich (crs:AsShotTemperature),
        ''' wenn das XMP ihn trägt - Sidecars von RAW-Dateien schreiben ihn, portable Presets meist
        ''' nicht. MIT ihm ist die Umrechnung aufnahmespezifisch exakt (der Regler bildet die Differenz
        ''' Aufnahme→Ziel ab, genau das, was beim Anwenden passieren soll); OHNE ihn bleibt die feste
        ''' D65-Annahme - gut für Tageslicht, für Kunstlicht/Nacht bewusst nur annähernd (siehe
        ''' Kommentar am Aufrufer).</summary>
        Private Shared Function KelvinToRelativeTemperature(kelvin As Double, Optional referenceKelvin As Double = WhiteBalanceReferenceKelvin) As Single
            If kelvin <= 0 Then Return 0
            If referenceKelvin <= 0 Then referenceKelvin = WhiteBalanceReferenceKelvin
            Dim miredRef = 1000000.0 / referenceKelvin
            Dim miredTarget = 1000000.0 / kelvin
            Return Clamp100(miredRef - miredTarget)
        End Function

        Private Shared Function Clamp(value As Double, min As Double, max As Double) As Single
            Return CSng(Math.Max(min, Math.Min(max, value)))
        End Function

        Private Shared Function Clamp100(value As Double) As Single
            Return Clamp(value, -100, 100)
        End Function

        Private Shared Function ApplyParametricCurve(values As Dictionary(Of String, String), pointCurve As String) As String
            ' "Shadows" ist in VB der Shadowing-Modifier und als Variablenname nicht zulässig - daher
            ' die -Amount-Endungen.
            Dim shadowsAmount = GetXmpDoubleOrDefault(values, "ParametricShadows", 0)
            Dim darksAmount = GetXmpDoubleOrDefault(values, "ParametricDarks", 0)
            Dim lightsAmount = GetXmpDoubleOrDefault(values, "ParametricLights", 0)
            Dim highlightsAmount = GetXmpDoubleOrDefault(values, "ParametricHighlights", 0)
            If shadowsAmount = 0 AndAlso darksAmount = 0 AndAlso lightsAmount = 0 AndAlso highlightsAmount = 0 Then Return pointCurve

            Dim shadowSplit = GetXmpDoubleOrDefault(values, "ParametricShadowSplit", 25) * 2.55
            Dim midtoneSplit = GetXmpDoubleOrDefault(values, "ParametricMidtoneSplit", 50) * 2.55
            Dim highlightSplit = GetXmpDoubleOrDefault(values, "ParametricHighlightSplit", 75) * 2.55

            ' Vollausschlag eines Zonenreglers verschiebt seine Zone um diesen Betrag (von 255).
            ' Am 28.07.2026 gegen die Referenzbasis abgetastet (50 bis 130): der Wert bringt die
            ' Bandform zwar naeher heran, die Gesamtabweichung steigt an einem der beiden Motive
            ' aber deutlich. Also nicht die gesuchte Kennlinie, siehe RAW_UND_FARBE.md.
            Const MaxParametricShift As Double = 50.0

            Dim nodesX = {0.0, shadowSplit / 2.0, (shadowSplit + midtoneSplit) / 2.0,
                          (midtoneSplit + highlightSplit) / 2.0, (highlightSplit + 255.0) / 2.0, 255.0}
            Dim nodesY = {0.0, shadowsAmount / 100.0 * MaxParametricShift, darksAmount / 100.0 * MaxParametricShift,
                          lightsAmount / 100.0 * MaxParametricShift, highlightsAmount / 100.0 * MaxParametricShift, 0.0}

            ' Adobe fuehrt Parametrik UND Punktkurve als EINE Tonwertkurve - wir falten sie ebenfalls
            ' zusammen, jetzt aber verlustarm: (1) die Punktkurve mit dem ECHTEN Engine-Spline auswerten
            ' statt linear (Kruemmung bleibt erhalten), (2) die Parametrik GLATT statt kantig (siehe
            ' InterpolateNodes), (3) DICHT abtasten (alle 8 Stufen statt 9 Punkte), damit beim erneuten
            ' Splinen der Engine praktisch nichts liegen bleibt - faktisch wie eine eigene Parametrik-
            ' Stufe, aber ohne neue Adjustments-Felder/Serialisierung, und die kombinierte Kurve bleibt
            ' im Kurven-Editor sichtbar (genau wie Adobes Tonwertkurve).
            Dim basePoints = ImageProcessor.ParseCurvePoints(pointCurve)
            Dim result As New List(Of String)()
            Dim x = 0
            Do
                Dim y = ImageProcessor.EvaluateCurveSpline(basePoints, x) + InterpolateNodes(nodesX, nodesY, x)
                result.Add($"{x},{CInt(Math.Max(0, Math.Min(255, Math.Round(y))))}")
                If x = 255 Then Exit Do
                x = Math.Min(255, x + 8)
            Loop
            Return String.Join(";", result)
        End Function

        Private Shared Function GetXmpDoubleOrDefault(values As Dictionary(Of String, String), name As String, fallback As Double) As Double
            Dim d As Double
            If TryGetXmpDouble(values, name, d) Then Return d
            Return fallback
        End Function

        ''' <summary>Parametrik-Zonenkurve: glatte (smoothstep) Interpolation zwischen den fuenf
        ''' Zonenknoten. Frueher linear - das setzte an jeden Knoten einen Knick und ergab eine
        ''' kantige Tonwertkurve. Smoothstep hat an den Knoten Ableitung null, die Zonen gehen also
        ''' weich ineinander ueber (naeher an Adobes parametrischer Kurve).</summary>
        Private Shared Function InterpolateNodes(nodesX As Double(), nodesY As Double(), x As Double) As Double
            For i = 1 To nodesX.Length - 1
                If x <= nodesX(i) Then
                    Dim span = nodesX(i) - nodesX(i - 1)
                    If span <= 0 Then Return nodesY(i)
                    Dim t = (x - nodesX(i - 1)) / span
                    Dim w = t * t * (3.0 - 2.0 * t)
                    Return nodesY(i - 1) + (nodesY(i) - nodesY(i - 1)) * w
                End If
            Next
            Return nodesY(nodesY.Length - 1)
        End Function

        ''' <summary>Das PROFIL eines Presets (crs:Look), soweit es aus der .xmp ablesbar ist. Die
        ''' eigentlichen Profildaten (eine 3D-Farbtabelle) stecken NICHT in der Datei - der Erzeuger holt sie
        ''' über die UUID aus seiner Profilbibliothek und schreibt deshalb crs:Stubbed="true". Ein Profil
        ''' exakt nachzubilden ist damit unmöglich; ablesbar bleiben Name, Gruppe und Stärke. Genau daran
        ''' hängt aber die Entscheidung "ist dieses Preset schwarzweiß?" - siehe LoadLook.</summary>
        Private Class LookProfile
            Public Name As String = ""
            Public Group As String = ""
            Public Amount As Double = 1.0
            Public HasAmount As Boolean = False
            ''' Manche Erzeuger sperren den Stärkeregler bei manchen Profilen (crs:SupportsAmount="false").
            Public SupportsAmount As Boolean = True

            ''' <summary>Erkennt ein monochromes Profil an Name ODER Gruppe. Adobe benennt sie
            ''' "Adobe Monochrome" bzw. gruppiert sie unter "B&amp;W" (Namen wie "B&amp;W 11"); Fremd-
            ''' profile halten sich in der Praxis daran. Bewusst eine NAMENSPRÜFUNG: die Profildaten
            ''' fehlen in der Datei, es gibt nichts Messbares.</summary>
            Public Function IsMonochrome() As Boolean
                Dim probe = ((Name & " " & Group)).ToLowerInvariant()
                Return probe.Contains("monochrome") OrElse probe.Contains("b&w") OrElse
                       probe.Contains("black & white") OrElse probe.Contains("schwarzweiß")
            End Function
        End Class

        ''' <summary>Nothing, wenn das Preset kein Profil nennt. Der Name steht als ATTRIBUT in der
        ''' verschachtelten rdf:Description, die Gruppe dagegen als Elementkörper in einem rdf:Alt -
        ''' beides braucht seinen eigenen Zugriff.</summary>
        Private Shared Function ParseLookProfile(xmpText As String) As LookProfile
            Dim block = Regex.Match(xmpText, "<crs:Look>(?<b>.*?)</crs:Look>", RegexOptions.Singleline)
            If Not block.Success Then Return Nothing
            Dim body = block.Groups("b").Value

            Dim result As New LookProfile()
            Dim nameMatch = Regex.Match(body, "crs:Name\s*=\s*""(?<v>[^""]*)""")
            If nameMatch.Success Then result.Name = DecodeXmlEntities(nameMatch.Groups("v").Value)
            ' Gruppe: <crs:Group><rdf:Alt><rdf:li xml:lang="x-default">B&amp;W</rdf:li>…
            Dim groupMatch = Regex.Match(body, "<crs:Group>.*?<rdf:li[^>]*>(?<v>[^<]*)</rdf:li>", RegexOptions.Singleline)
            If groupMatch.Success Then result.Group = DecodeXmlEntities(groupMatch.Groups("v").Value)

            Dim amountMatch = Regex.Match(body, "crs:Amount\s*=\s*""(?<v>[^""]*)""")
            If amountMatch.Success Then
                Dim parsed As Double
                If Double.TryParse(amountMatch.Groups("v").Value.Replace("+", ""), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, parsed) Then
                    result.Amount = parsed
                    result.HasAmount = True
                End If
            End If
            Dim supportsMatch = Regex.Match(body, "crs:SupportsAmount\s*=\s*""(?<v>[^""]*)""")
            If supportsMatch.Success Then
                result.SupportsAmount = Not String.Equals(supportsMatch.Groups("v").Value, "false",
                                                          StringComparison.OrdinalIgnoreCase)
            End If
            Return result
        End Function

        ''' Profilnamen wie "B&amp;W 11" kommen XML-kodiert an; ohne Auflösung greift keine Namensprüfung.
        Private Shared Function DecodeXmlEntities(value As String) As String
            If String.IsNullOrEmpty(value) Then Return ""
            Return value.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", """").
                         Replace("&apos;", "'").Replace("&amp;", "&").Trim()
        End Function

        ''' <summary>Entfernt die VERSCHACHTELTEN crs-Blöcke (Profil, lokale Korrekturen, Retusche) aus dem
        ''' Text. ParseXmpValues sammelt crs:-Attribute flach über die ganze Datei ein und kennt
        ''' die Verschachtelung nicht - ein crs:Amount aus dem Profil oder ein crs:Feather aus einer Maske
        ''' landet dort unter demselben Schlüssel wie ein Regler des Kopf-Blocks und würde ihn stumm
        ''' überschreiben. Solange LoadLook nur lange, eindeutige Namen las, ging das gut; mit dem Profil
        ''' kommen kurze Namen ins Spiel, und dann ist die Trennung Pflicht. Betrifft nur die flache
        ''' Wertetabelle - ParseLocalCorrections und ParseLookProfile bekommen weiter den ganzen Text.</summary>
        Private Shared Function StripNestedCrsBlocks(text As String) As String
            If String.IsNullOrEmpty(text) Then Return ""
            For Each name In {"Look", "GradientBasedCorrections", "CircularGradientBasedCorrections",
                              "PaintBasedCorrections", "MaskGroupBasedCorrections", "RetouchAreas"}
                text = Regex.Replace(text, "<crs:" & name & ">.*?</crs:" & name & ">", "", RegexOptions.Singleline)
            Next
            Return text
        End Function

        Private Shared Function ParseXmpValues(text As String) As Dictionary(Of String, String)
            Dim result As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            If String.IsNullOrWhiteSpace(text) Then Return result
            ''' Nur "crs:"-Attribute (Camera Raw Settings) - ohne den Namespace-Zwang würde jedes andere
            ''' XMP-Attribut mit gleichem lokalen Namen (z.B. xmp:CreatorTool, photoshop:...) denselben
            ''' Dictionary-Key überschreiben und crs:-Werte stillschweigend verfälschen.
            For Each m As Match In Regex.Matches(text, "crs:(?<name>[A-Za-z0-9]+)\s*=\s*""(?<value>[^""]*)""")
                result(m.Groups("name").Value) = m.Groups("value").Value
            Next
            Return result
        End Function

        ''' Leer, wenn der Schlüssel fehlt - Aufrufer können damit ohne Fallunterscheidung vergleichen.
        Private Shared Function GetXmpString(values As Dictionary(Of String, String), name As String) As String
            Dim raw As String = Nothing
            If Not values.TryGetValue(name, raw) Then Return ""
            Return If(raw, "").Trim()
        End Function

        ''' XMP kennt für Wahrheitswerte "True"/"False"; manche Erzeuger schreiben "1"/"0".
        Private Shared Function IsXmpTrue(values As Dictionary(Of String, String), name As String) As Boolean
            Dim raw = GetXmpString(values, name)
            Return String.Equals(raw, "True", StringComparison.OrdinalIgnoreCase) OrElse raw = "1"
        End Function

        Private Shared Function TryGetXmpDouble(values As Dictionary(Of String, String), name As String, ByRef result As Double) As Boolean
            Dim raw As String = Nothing
            If Not values.TryGetValue(name, raw) Then Return False
            raw = raw.Replace("+", "")
            Return Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, result)
        End Function

        ''' Extrahiert eine crs:ToneCurvePV2012[Red|Green|Blue]-Punktliste (rdf:Seq aus rdf:li-Einträgen
        ''' "x, y") und liefert sie im gleichen "x,y;x,y;..."-Format wie ImageAdjustments.Curve*Points.
        ''' Nothing wenn das Element fehlt oder keine gültigen Punkte enthält.
        Private Shared Function ParseXmpCurvePoints(text As String, elementName As String) As String
            Dim blockMatch = Regex.Match(text, $"<crs:{elementName}>(?<body>.*?)</crs:{elementName}>", RegexOptions.Singleline)
            If Not blockMatch.Success Then Return Nothing

            Dim points As New List(Of String)()
            For Each liMatch As Match In Regex.Matches(blockMatch.Groups("body").Value, "<rdf:li>(?<point>[^<]*)</rdf:li>")
                Dim parts = liMatch.Groups("point").Value.Split(","c)
                If parts.Length <> 2 Then Continue For
                Dim px As Double
                Dim py As Double
                If Double.TryParse(parts(0).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, px) AndAlso
                   Double.TryParse(parts(1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, py) Then
                    points.Add(px.ToString(CultureInfo.InvariantCulture) & "," & py.ToString(CultureInfo.InvariantCulture))
                End If
            Next
            If points.Count < 2 Then Return Nothing
            Return String.Join(";", points)
        End Function

        ''' <summary>Eine lokale Korrektur aus einem XMP-Preset: Masken-Geometrie (roh, wird erst zur
        ''' Anwendungszeit mit den Bildmaßen gerastert) plus die auf unsere Regler abgebildeten
        ''' Local*-Werte.</summary>
        Public Class LocalCorrectionSpec
            Public MaskType As String                 ' "Gradient" | "CircularGradient"
            Public MaskValue As Double = 1.0
            Public ZeroX, ZeroY, FullX, FullY As Double
            Public Top, Left, Bottom, Right, Angle, Feather As Double
            Public Flipped As Boolean
            Public Adjustments As ImageAdjustments
        End Class

        Private Shared Function CorrAttr(chunk As String, name As String, fallback As Double) As Double
            Dim m = Regex.Match(chunk, "crs:" & name & "=""(?<v>[^""]*)""")
            If Not m.Success Then Return fallback
            Dim raw = m.Groups("v").Value.Replace("+", "")
            Dim d As Double
            If Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, d) Then Return d
            Return fallback
        End Function

        ''' Wahrheitswert einer Korrektur-Eigenschaft; fehlt sie, gilt der Rückfall.
        Private Shared Function CorrFlag(chunk As String, name As String, fallback As Boolean) As Boolean
            Dim m = Regex.Match(chunk, "crs:" & name & "=""(?<v>[^""]*)""")
            If Not m.Success Then Return fallback
            Dim raw = m.Groups("v").Value.Trim()
            If String.Equals(raw, "true", StringComparison.OrdinalIgnoreCase) OrElse raw = "1" Then Return True
            If String.Equals(raw, "false", StringComparison.OrdinalIgnoreCase) OrElse raw = "0" Then Return False
            Return fallback
        End Function

        ''' <summary>Bildet die Local*-Werte einer Correction auf ImageAdjustments ab. Achtung auf die
        ''' Einheiten: Local*2012 (außer Exposure) sind BRÜCHE -1..1 (global sind sie ganze -100..100),
        ''' LocalExposure2012 ist EV wie global. Toning (Hue/Sat) → Farbgradierung-Global der Ebene.</summary>
        Private Shared Function BuildLocalAdjustments(chunk As String) As ImageAdjustments
            Dim a As New ImageAdjustments()
            a.Exposure = Clamp100(CorrAttr(chunk, "LocalExposure2012", 0) * 25.0)
            a.Contrast = Clamp100(CorrAttr(chunk, "LocalContrast2012", 0) * 100.0)
            a.Highlights = Clamp100(CorrAttr(chunk, "LocalHighlights2012", 0) * 100.0)
            a.ShadowsLevel = Clamp100(CorrAttr(chunk, "LocalShadows2012", 0) * 100.0)
            a.Whites = Clamp100(CorrAttr(chunk, "LocalWhites2012", 0) * 100.0)
            a.Blacks = Clamp100(CorrAttr(chunk, "LocalBlacks2012", 0) * 100.0)
            a.Clarity = Clamp100(CorrAttr(chunk, "LocalClarity2012", 0) * 100.0)
            a.Haze = Clamp100(-CorrAttr(chunk, "LocalDehaze", 0) * 100.0)
            a.Saturation = Clamp100(CorrAttr(chunk, "LocalSaturation", 0) * 100.0)
            a.Temperature = Clamp100(CorrAttr(chunk, "LocalTemperature", 0) * 100.0)
            a.Tint = Clamp100(CorrAttr(chunk, "LocalTint", 0) * 100.0)
            Dim tSat = CorrAttr(chunk, "LocalToningSaturation", 0)
            If tSat > 0 Then
                a.ColorGradeGlobalHue = Clamp(CorrAttr(chunk, "LocalToningHue", 0), 0, 360)
                a.ColorGradeGlobalSaturation = Clamp(tSat, 0, 100)
            End If
            Return a
        End Function

        Private Shared Function HasAnyLocalEffect(a As ImageAdjustments) As Boolean
            Return a IsNot Nothing AndAlso (a.Exposure <> 0 OrElse a.Contrast <> 0 OrElse a.Highlights <> 0 OrElse
                a.ShadowsLevel <> 0 OrElse a.Whites <> 0 OrElse a.Blacks <> 0 OrElse a.Clarity <> 0 OrElse
                a.Haze <> 0 OrElse a.Saturation <> 0 OrElse a.Temperature <> 0 OrElse a.Tint <> 0 OrElse
                a.ColorGradeGlobalSaturation <> 0)
        End Function

        ''' <summary>Parst die lokalen Korrekturen (Radial-/Verlaufsmasken) eines XMP-Presets. Jede
        ''' Correction (in GradientBasedCorrections bzw. CircularGradientBasedCorrections) hat genau eine
        ''' Maske und ihre Local*-Werte; Pinsel-/Bereichsmasken werden (noch) übersprungen.</summary>
        Public Shared Function ParseLocalCorrections(xmpText As String) As List(Of LocalCorrectionSpec)
            Dim result As New List(Of LocalCorrectionSpec)()
            If String.IsNullOrEmpty(xmpText) Then Return result
            For Each container In {"GradientBasedCorrections", "CircularGradientBasedCorrections"}
                Dim block = Regex.Match(xmpText, "<crs:" & container & ">(?<b>.*?)</crs:" & container & ">", RegexOptions.Singleline)
                If Not block.Success Then Continue For
                Dim parts = Regex.Split(block.Groups("b").Value, "crs:What=""Correction""")
                For i = 1 To parts.Length - 1
                    Dim chunk = parts(i)
                    ' crs:CorrectionActive="false" heißt: der Haken ist raus, die Korrektur
                    ' liegt zwar im Preset, wirkt aber nicht. Bisher ungelesen - eine abgeschaltete Maske
                    ' kam als aktive Anpassungsebene an, und der Nutzer sah einen Effekt, den das Preset
                    ' ausdrücklich nicht will. Fehlt das Attribut, gilt aktiv (so schreiben es ältere
                    ' Erzeuger).
                    If Not CorrFlag(chunk, "CorrectionActive", True) Then Continue For
                    ' crs:CorrectionAmount ist die Gesamtstärke der Korrektur (1.0 = voll). Sie gehört mit
                    ' crs:MaskValue zusammen in die MASKE, nicht in die Regler: damit blendet der Erzeuger die
                    ' ganze Korrektur aus, und genau das macht ein flacherer Maskenwert bei uns auch. Über
                    ' die Regler zu skalieren wäre falsch - Werte wie Farbton skalieren nicht linear.
                    Dim correctionAmount = CorrAttr(chunk, "CorrectionAmount", 1.0)
                    If correctionAmount <= 0 Then Continue For

                    Dim mm = Regex.Match(chunk, "<rdf:li\s(?<body>[^>]*?crs:What=""Mask/(?<t>[A-Za-z]+)""[^>]*?)/>", RegexOptions.Singleline)
                    If Not mm.Success Then Continue For
                    Dim body = mm.Groups("body").Value
                    Dim spec As New LocalCorrectionSpec With {
                        .MaskType = mm.Groups("t").Value,
                        .MaskValue = Math.Max(0.0, Math.Min(1.0, CorrAttr(body, "MaskValue", 1.0) * correctionAmount)),
                        .Adjustments = BuildLocalAdjustments(chunk)
                    }
                    If Not HasAnyLocalEffect(spec.Adjustments) Then Continue For
                    If String.Equals(spec.MaskType, "CircularGradient", StringComparison.Ordinal) Then
                        spec.Top = CorrAttr(body, "Top", 0) : spec.Left = CorrAttr(body, "Left", 0)
                        spec.Bottom = CorrAttr(body, "Bottom", 1) : spec.Right = CorrAttr(body, "Right", 1)
                        spec.Angle = CorrAttr(body, "Angle", 0)
                        spec.Feather = CorrAttr(body, "Feather", 50)
                        Dim m2 = Regex.Match(body, "crs:Flipped=""(?<v>[^""]*)""")
                        spec.Flipped = m2.Success AndAlso String.Equals(m2.Groups("v").Value, "true", StringComparison.OrdinalIgnoreCase)
                    Else
                        spec.ZeroX = CorrAttr(body, "ZeroX", 0) : spec.ZeroY = CorrAttr(body, "ZeroY", 0)
                        spec.FullX = CorrAttr(body, "FullX", 1) : spec.FullY = CorrAttr(body, "FullY", 0)
                    End If
                    result.Add(spec)
                Next
            Next
            Return result
        End Function

    End Class

End Namespace
