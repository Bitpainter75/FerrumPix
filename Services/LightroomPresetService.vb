Imports System
Imports System.Collections.Generic
Imports System.Globalization
Imports System.IO
Imports System.Text.RegularExpressions

Namespace Services

    ''' <summary>Liest ein Lightroom-/Camera-Raw-Preset (.xmp) und übersetzt es in ein
    ''' <see cref="ImageAdjustments"/>, das nur den LOOK trägt (Licht, Farbe, Details, Effekte, HSL,
    ''' Split-Toning, Tonwertkurven) - keine Geometrie, keine Objekte, keine Auswahl.
    ''' Bewusst ohne ViewModel: den Editor interessiert derselbe Look wie die Stapelverarbeitung der
    ''' Galerie, und zwei Abbildungen derselben XMP-Schlüssel würden garantiert auseinanderlaufen.</summary>
    Public Class LightroomPresetService

        ''' <summary>Nothing, wenn die Datei fehlt oder keine crs:-Werte enthält. Alle Felder, die das
        ''' Preset nicht setzt, bleiben auf ihrem neutralen Standard - ein geladenes Preset ersetzt den
        ''' Look also vollständig, statt sich mit dem vorherigen zu vermischen.</summary>
        Public Shared Function LoadLook(xmpPath As String) As ImageAdjustments
            If String.IsNullOrWhiteSpace(xmpPath) OrElse Not File.Exists(xmpPath) Then Return Nothing
            Dim xmpText = File.ReadAllText(xmpPath)
            Dim values = ParseLightroomXmpValues(xmpText)
            If values.Count = 0 Then Return Nothing

            Dim adj As New ImageAdjustments()
            Dim d As Double

            If TryGetXmpDouble(values, "Exposure2012", d) Then adj.Exposure = Clamp100(d * 25.0)
            If TryGetXmpDouble(values, "Contrast2012", d) Then adj.Contrast = Clamp100(d)
            If TryGetXmpDouble(values, "Highlights2012", d) Then adj.Highlights = Clamp100(d)
            If TryGetXmpDouble(values, "Shadows2012", d) Then adj.ShadowsLevel = Clamp100(d)
            If TryGetXmpDouble(values, "Whites2012", d) Then adj.Whites = Clamp100(d)
            If TryGetXmpDouble(values, "Blacks2012", d) Then adj.Blacks = Clamp100(d)
            If TryGetXmpDouble(values, "Clarity2012", d) Then adj.Clarity = Clamp100(d)
            If TryGetXmpDouble(values, "Texture", d) Then adj.[Structure] = Clamp100(d)
            If TryGetXmpDouble(values, "Dehaze", d) Then adj.Haze = Clamp100(-d)
            If TryGetXmpDouble(values, "Vibrance", d) Then adj.Vibrance = Clamp100(d)
            If TryGetXmpDouble(values, "Saturation", d) Then adj.Saturation = Clamp100(d)
            If TryGetXmpDouble(values, "Sharpness", d) Then adj.Sharpness = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "LuminanceSmoothing", d) Then adj.NoiseReduction = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "ColorNoiseReduction", d) Then adj.ColorNoiseReduction = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "GrainAmount", d) Then adj.Grain = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "PostCropVignetteAmount", d) Then
                adj.Vignette = Clamp(-d, -150, 150)
                ' Mittelpunkt und weiche Kante sind semantisch deckungsgleich mit VignetteTransition/
                ' VignetteFeather (beide 0-100, hoeher = weiter aussen bzw. weicher) - aber nur bei
                ' AKTIVER Vignette uebernehmen, sonst ueberschrieben Preset-Defaults die App-Defaults.
                ' PostCropVignetteRoundness wird bewusst NICHT uebertragen: bei Adobe steuert es die
                ' Kreisform, bei uns die Achsen-Verzerrung des Ovals - eine Uebernahme saehe anders aus.
                If d <> 0 Then
                    Dim v As Double
                    If TryGetXmpDouble(values, "PostCropVignetteMidpoint", v) Then adj.VignetteTransition = Clamp(v, 0, 100)
                    If TryGetXmpDouble(values, "PostCropVignetteFeather", v) Then adj.VignetteFeather = Clamp(v, 0, 100)
                End If
            End If

            ''' crs:Temperature/crs:WhiteBalance sind NICHT übernehmbar: Lightroom speichert dort einen
            ''' absoluten Kelvin-Wert (z.B. 5500) bzw. "As Shot"/"Custom", während der Temperatur-Regler
            ''' dieser App eine relative ±100-Verschiebung ist - ohne die kamera-/aufnahmespezifische
            ''' Referenztemperatur wäre jede Übernahme falsch. crs:IncrementalTemperature/-Tint dagegen SIND
            ''' genau diese relative ±100-Verschiebung (Lightroom schreibt sie für Nicht-RAW-Dateien, und
            ''' Presets liegen praktisch immer in dieser Form vor). Ohne sie ging die Farbstimmung jedes
            ''' Presets verloren, das seinen Look über die Weißabgleich-Regler aufbaut. crs:Tint ohne Präfix
            ''' wird weiterhin akzeptiert, ist bei RAW-Presets aber ebenfalls relativ gemeint.
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

            If TryGetXmpDouble(values, "IncrementalTemperature", d) Then adj.Temperature = Clamp100(d)
            If TryGetXmpDouble(values, "IncrementalTint", d) Then
                adj.Tint = Clamp100(d)
            ElseIf TryGetXmpDouble(values, "Tint", d) Then
                adj.Tint = Clamp100(d)
            End If

            If TryGetXmpDouble(values, "HueAdjustmentRed", d) Then adj.RedHue = Clamp100(d)
            If TryGetXmpDouble(values, "SaturationAdjustmentRed", d) Then adj.RedSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentRed", d) Then adj.RedLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentOrange", d) Then adj.OrangeHue = Clamp100(d)
            If TryGetXmpDouble(values, "SaturationAdjustmentOrange", d) Then adj.OrangeSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentOrange", d) Then adj.OrangeLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentYellow", d) Then adj.YellowHue = Clamp100(d)
            If TryGetXmpDouble(values, "SaturationAdjustmentYellow", d) Then adj.YellowSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentYellow", d) Then adj.YellowLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentGreen", d) Then adj.GreenHue = Clamp100(d)
            If TryGetXmpDouble(values, "SaturationAdjustmentGreen", d) Then adj.GreenSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentGreen", d) Then adj.GreenLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentAqua", d) Then adj.AquaHue = Clamp100(d)
            If TryGetXmpDouble(values, "SaturationAdjustmentAqua", d) Then adj.AquaSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentAqua", d) Then adj.AquaLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentBlue", d) Then adj.BlueHue = Clamp100(d)
            If TryGetXmpDouble(values, "SaturationAdjustmentBlue", d) Then adj.BlueSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentBlue", d) Then adj.BlueLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentPurple", d) Then adj.PurpleHue = Clamp100(d)
            If TryGetXmpDouble(values, "SaturationAdjustmentPurple", d) Then adj.PurpleSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentPurple", d) Then adj.PurpleLuminance = Clamp100(d)
            If TryGetXmpDouble(values, "HueAdjustmentMagenta", d) Then adj.MagentaHue = Clamp100(d)
            If TryGetXmpDouble(values, "SaturationAdjustmentMagenta", d) Then adj.MagentaSaturation = Clamp100(d)
            If TryGetXmpDouble(values, "LuminanceAdjustmentMagenta", d) Then adj.MagentaLuminance = Clamp100(d)

            ''' crs:SplitToning*Hue ist bereits 0..360, *Saturation 0..100 - beides deckungsgleich mit den
            ''' Split-Toning-Reglern dieser App, keine Skalierung nötig. Balance ist bei beiden -100..100.
            If TryGetXmpDouble(values, "SplitToningShadowHue", d) Then adj.SplitToningShadowHue = Clamp(d, 0, 360)
            If TryGetXmpDouble(values, "SplitToningShadowSaturation", d) Then adj.SplitToningShadowSaturation = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "SplitToningHighlightHue", d) Then adj.SplitToningHighlightHue = Clamp(d, 0, 360)
            If TryGetXmpDouble(values, "SplitToningHighlightSaturation", d) Then adj.SplitToningHighlightSaturation = Clamp(d, 0, 100)
            If TryGetXmpDouble(values, "SplitToningBalance", d) Then adj.SplitToningBalance = Clamp100(d)

            ''' Tonwertkurven liegen als verschachtelte rdf:Seq/rdf:li-Listen vor, nicht als einfache
            ''' Attribute - der Attribut-Regex oben kann sie nicht erfassen, daher eine eigene, gezielte
            ''' Extraktion je Kurven-Element.
            ''' Neben der Punktkurve führt Lightroom eine zweite, PARAMETRISCHE Kurve: vier Zonenregler
            ''' (Schatten/Dunkel/Licht/Lichter), deren Zonengrenzen selbst wieder Parameter sind. Beide
            ''' wirken übereinander. Wird sie ignoriert, fehlt Presets, die ihren Tonwert-Look darüber
            ''' aufbauen, genau dieser Teil. Sie wird deshalb in die Punktkurve eingerechnet - eine
            ''' Annäherung an Adobes Kurvenform, kein exakter Nachbau.
            Dim combinedCurve = ApplyParametricCurve(values, ParseLightroomCurvePoints(xmpText, "ToneCurvePV2012"))
            If combinedCurve IsNot Nothing Then adj.CurveRgbPoints = combinedCurve
            Dim redCurve = ParseLightroomCurvePoints(xmpText, "ToneCurvePV2012Red")
            If redCurve IsNot Nothing Then adj.CurveRedPoints = redCurve
            Dim greenCurve = ParseLightroomCurvePoints(xmpText, "ToneCurvePV2012Green")
            If greenCurve IsNot Nothing Then adj.CurveGreenPoints = greenCurve
            Dim blueCurve = ParseLightroomCurvePoints(xmpText, "ToneCurvePV2012Blue")
            If blueCurve IsNot Nothing Then adj.CurveBluePoints = blueCurve

            Return adj
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
            Const MaxParametricShift As Double = 50.0

            Dim nodesX = {0.0, shadowSplit / 2.0, (shadowSplit + midtoneSplit) / 2.0,
                          (midtoneSplit + highlightSplit) / 2.0, (highlightSplit + 255.0) / 2.0, 255.0}
            Dim nodesY = {0.0, shadowsAmount / 100.0 * MaxParametricShift, darksAmount / 100.0 * MaxParametricShift,
                          lightsAmount / 100.0 * MaxParametricShift, highlightsAmount / 100.0 * MaxParametricShift, 0.0}

            Dim basePoints = ParseCurvePointString(pointCurve)
            Dim result As New List(Of String)()
            For Each x In {0, 32, 64, 96, 128, 160, 192, 224, 255}
                Dim y = InterpolatePoints(basePoints, x) + InterpolateNodes(nodesX, nodesY, x)
                result.Add($"{x},{CInt(Math.Max(0, Math.Min(255, Math.Round(y))))}")
            Next
            Return String.Join(";", result)
        End Function

        Private Shared Function GetXmpDoubleOrDefault(values As Dictionary(Of String, String), name As String, fallback As Double) As Double
            Dim d As Double
            If TryGetXmpDouble(values, name, d) Then Return d
            Return fallback
        End Function

        ''' Zerlegt "x,y;x,y;..." wieder in Punkte. Leer/Nothing ergibt die Identität (0,0)-(255,255).
        Private Shared Function ParseCurvePointString(text As String) As List(Of (X As Double, Y As Double))
            Dim points As New List(Of (X As Double, Y As Double))()
            If Not String.IsNullOrWhiteSpace(text) Then
                For Each part In text.Split(";"c)
                    Dim xy = part.Split(","c)
                    If xy.Length <> 2 Then Continue For
                    Dim px, py As Double
                    If Double.TryParse(xy(0).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, px) AndAlso
                       Double.TryParse(xy(1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, py) Then
                        points.Add((px, py))
                    End If
                Next
            End If
            If points.Count < 2 Then
                points.Clear()
                points.Add((0, 0))
                points.Add((255, 255))
            End If
            Return points
        End Function

        Private Shared Function InterpolatePoints(points As List(Of (X As Double, Y As Double)), x As Double) As Double
            If x <= points(0).X Then Return points(0).Y
            For i = 1 To points.Count - 1
                If x <= points(i).X Then
                    Dim span = points(i).X - points(i - 1).X
                    If span <= 0 Then Return points(i).Y
                    Dim t = (x - points(i - 1).X) / span
                    Return points(i - 1).Y + (points(i).Y - points(i - 1).Y) * t
                End If
            Next
            Return points(points.Count - 1).Y
        End Function

        Private Shared Function InterpolateNodes(nodesX As Double(), nodesY As Double(), x As Double) As Double
            For i = 1 To nodesX.Length - 1
                If x <= nodesX(i) Then
                    Dim span = nodesX(i) - nodesX(i - 1)
                    If span <= 0 Then Return nodesY(i)
                    Dim t = (x - nodesX(i - 1)) / span
                    Return nodesY(i - 1) + (nodesY(i) - nodesY(i - 1)) * t
                End If
            Next
            Return nodesY(nodesY.Length - 1)
        End Function

        Private Shared Function ParseLightroomXmpValues(text As String) As Dictionary(Of String, String)
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

        Private Shared Function TryGetXmpDouble(values As Dictionary(Of String, String), name As String, ByRef result As Double) As Boolean
            Dim raw As String = Nothing
            If Not values.TryGetValue(name, raw) Then Return False
            raw = raw.Replace("+", "")
            Return Double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, result)
        End Function

        ''' Extrahiert eine crs:ToneCurvePV2012[Red|Green|Blue]-Punktliste (rdf:Seq aus rdf:li-Einträgen
        ''' "x, y") und liefert sie im gleichen "x,y;x,y;..."-Format wie ImageAdjustments.Curve*Points.
        ''' Nothing wenn das Element fehlt oder keine gültigen Punkte enthält.
        Private Shared Function ParseLightroomCurvePoints(text As String, elementName As String) As String
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

    End Class

End Namespace
