Imports System.Globalization
Imports System.Reflection
Imports System.Text
Imports System.Text.Json

Namespace Services

    ''' <summary>Schreibt die Anpassung eines Bildes als XMP-Preset (.xmp), fuer den Dialog
    ''' "Anpassung speichern". Die Datei sieht aus wie ein Preset des Referenzprogramms und laesst
    ''' sich von FerrumPix vollstaendig wieder einlesen (XmpPresetService.LoadLook).
    '''
    ''' ZWEI NAMENSRAEUME, EIN FELD STEHT NIE IN BEIDEN. Jeder Regler, dessen Wert sich als crs:
    ''' ausdruecken laesst, geht als crs: hinaus, in der Bedeutung des Referenzprogramms (siehe
    ''' XmpPresetService fuer die Lese-Abbildung, die hier umgekehrt wird). Alles andere steht im
    ''' eigenen Namensraum <see cref="PresetNamespace"/> unter dem Feldnamen des Rezepts, als
    ''' JSON-Wert (Zeichenketten roh). Ein fremdes Programm uebergeht ihn. Ausdrueckbar heisst
    ''' auch: im Bereich des crs:-Schluessels. Ein Farbton des Farbmischers etwa, der nach der
    ''' Skalierung ueber 100 laege, oder eine Vignette ueber 100 stehen deshalb im eigenen Raum.
    ''' Welche Felder nicht als crs: gingen, ergibt sich von selbst: jedes gewaehlte Feld, das die
    ''' Abbildung unten nicht abdeckt. Ein neuer Regler landet so ohne Zutun im eigenen Raum.
    '''
    ''' DIE AUSWAHL steht als <c>fpx:PresetFields</c> in der Datei (die Feldliste wie bei einer
    ''' gespeicherten Anpassung). Beim Anwenden setzt eine solche Datei nur diese Regler, auch mit
    ''' dem Neutralwert; jede andere .xmp verhaelt sich wie bisher.
    '''
    ''' ZAHLEN: unveraendert lineare Regler mit ihrem Wert als Single, skalierte (Belichtung,
    ''' Farbton des Farbmischers, Radius der Schaerfe) als Double mit allen Stellen, damit der
    ''' Rueckweg genau denselben Single ergibt. Kurvenpunkte und Kelvin auf ganze Zahlen gerundet,
    ''' wie das Referenzprogramm sie schreibt.
    '''
    ''' DIE KURVE: beim Lesen werden Punktkurve und parametrische Kurve zu einer Punktkurve
    ''' gefaltet. Geschrieben wird deshalb nur die Punktkurve, die parametrischen Regler auf null.
    ''' Der Rahmen hat kein Gegenstueck und bleibt draussen (<see cref="BlockReason"/>).</summary>
    Public NotInheritable Class XmpPresetWriter

        ''' Der eigene Namensraum: dieselbe Adresse wie die der Beistelldatei, eigener Pfad.
        Public Const PresetNamespace As String = "https://github.com/Bitpainter75/FerrumPix/ns/preset/1.0"
        Public Const PresetPrefix As String = "fpx"
        ''' Das Attribut mit der Feldliste der Auswahl.
        Public Const PresetFieldsName As String = "PresetFields"
        Public Const FileExtension As String = ".xmp"
        ''' Die hoechste Prozessversion, die der Leser kennt.
        Public Const ProcessVersion As String = "15.4"
        ''' Die Fassung des Erzeugers, wie sie ein Preset mit dieser Prozessversion traegt.
        Private Const WriterVersion As String = "16.5"

        Private Shared ReadOnly HslBands As String() = {"Red", "Orange", "Yellow", "Green", "Aqua", "Blue", "Purple", "Magenta"}
        Private Shared ReadOnly GradeZones As String() = {"Shadow", "Midtone", "Highlight", "Global"}

        Private Sub New()
        End Sub

        ''' <summary>Was beim Schreiben herauskam: der Text und welche Felder als crs: und welche
        ''' im eigenen Raum stehen. Die beiden Mengen sind getrennt und decken zusammen jedes
        ''' gewaehlte Feld ab.</summary>
        Public NotInheritable Class Output
            Public Property Text As String = ""
            Public Property CrsFields As New HashSet(Of String)(StringComparer.Ordinal)
            Public Property OwnFields As New HashSet(Of String)(StringComparer.Ordinal)
            Public Property SelectedFields As New List(Of String)()
        End Class

        ''' <summary>Warum <paramref name="entry"/> in einem XMP-Preset nicht enthalten sein kann,
        ''' als Satz in der Sprache der Oberflaeche; Nothing, wenn er es kann.</summary>
        Public Shared Function BlockReason(entry As AdjustmentEntry) As String
            If entry IsNot Nothing AndAlso entry.IsFrame Then
                Return LocalizationService.T("Der Rahmen hat in einem XMP-Preset kein Gegenstück.")
            End If
            Return Nothing
        End Function

        ''' <summary>Baut das Preset. <paramref name="recipe"/> ist das uebertragbare Rezept des
        ''' Bildes, <paramref name="entries"/> die gewaehlten Eintraege; der Rahmen zaehlt nicht.</summary>
        ''' <param name="uuid">Die Kennung des Presets (32 Hexziffern); ohne Angabe eine neue.</param>
        Public Shared Function Build(name As String, recipe As ImageAdjustments, entries As IEnumerable(Of AdjustmentEntry),
                                     Optional uuid As String = Nothing) As Output
            If recipe Is Nothing Then recipe = New ImageAdjustments()
            Dim chosen = If(entries, Enumerable.Empty(Of AdjustmentEntry)()).Where(Function(e) e IsNot Nothing AndAlso Not e.IsFrame).ToList()
            Dim fields = AdjustmentEntries.FieldsOf(chosen)
            Dim work As New Writer(recipe, fields)
            work.WriteAll()

            Dim result As New Output()
            result.SelectedFields = fields
            Dim props = ImageAdjustments.PixelAdjustmentProperties().ToDictionary(Function(p) p.Name, StringComparer.Ordinal)
            Dim own As New List(Of KeyValuePair(Of String, String))()
            For Each field In fields
                If work.Covered.Contains(field) Then
                    result.CrsFields.Add(field)
                    Continue For
                End If
                Dim p As PropertyInfo = Nothing
                If Not props.TryGetValue(field, p) Then Continue For
                own.Add(New KeyValuePair(Of String, String)(field, OwnValue(p, p.GetValue(recipe))))
                result.OwnFields.Add(field)
            Next

            Dim id = If(String.IsNullOrWhiteSpace(uuid), Guid.NewGuid().ToString("N").ToUpperInvariant(), uuid.Trim())
            Dim text As New StringBuilder()
            text.Append("<x:xmpmeta xmlns:x=""adobe:ns:meta/"" x:xmptk=""FerrumPix"">").Append(vbLf)
            text.Append(" <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">").Append(vbLf)
            text.Append("  <rdf:Description rdf:about=""""").Append(vbLf)
            text.Append("    xmlns:crs=""http://ns.adobe.com/camera-raw-settings/1.0/""").Append(vbLf)
            text.Append("    xmlns:").Append(PresetPrefix).Append("=""").Append(PresetNamespace).Append("""").Append(vbLf)
            Dim header As New List(Of KeyValuePair(Of String, String)) From {
                Pair("PresetType", "Normal"), Pair("Cluster", ""), Pair("UUID", id),
                Pair("SupportsAmount", "False"), Pair("SupportsColor", "True"), Pair("SupportsMonochrome", "True"),
                Pair("SupportsHighDynamicRange", "True"), Pair("SupportsNormalDynamicRange", "True"),
                Pair("SupportsSceneReferred", "True"), Pair("SupportsOutputReferred", "True"),
                Pair("CameraModelRestriction", ""), Pair("Copyright", ""), Pair("ContactInfo", ""),
                Pair("Version", WriterVersion), Pair("ProcessVersion", ProcessVersion)}
            For Each attribute In header.Concat(work.Attributes)
                text.Append("   crs:").Append(attribute.Key).Append("=""").Append(Escape(attribute.Value)).Append("""").Append(vbLf)
            Next
            For Each attribute In own
                text.Append("   ").Append(PresetPrefix).Append(":").Append(attribute.Key).Append("=""").Append(Escape(attribute.Value)).Append("""").Append(vbLf)
            Next
            text.Append("   ").Append(PresetPrefix).Append(":").Append(PresetFieldsName).Append("=""").
                 Append(Escape(String.Join(",", fields))).Append("""").Append(vbLf)
            text.Append("   crs:HasSettings=""True"">").Append(vbLf)
            text.Append("   <crs:Name>").Append(vbLf)
            text.Append("    <rdf:Alt>").Append(vbLf)
            text.Append("     <rdf:li xml:lang=""x-default"">").Append(Escape(If(name, "").Trim())).Append("</rdf:li>").Append(vbLf)
            text.Append("    </rdf:Alt>").Append(vbLf)
            text.Append("   </crs:Name>").Append(vbLf)
            text.Append(work.Elements.ToString())
            text.Append("  </rdf:Description>").Append(vbLf)
            text.Append(" </rdf:RDF>").Append(vbLf)
            text.Append("</x:xmpmeta>").Append(vbLf)
            result.Text = text.ToString()
            Return result
        End Function

        ''' <summary>Schreibt das Preset atomar (erst daneben, dann umbenannt).</summary>
        Public Shared Function WriteFile(targetPath As String, name As String, recipe As ImageAdjustments,
                                         entries As IEnumerable(Of AdjustmentEntry)) As Output
            Dim result = Build(name, recipe, entries)
            JsonStoreService.WriteAtomicOrThrow(targetPath, result.Text)
            Return result
        End Function

        ''' <summary>Der Wert eines Felds im eigenen Raum: Zeichenketten roh, alles andere als JSON.</summary>
        Friend Shared Function OwnValue(p As PropertyInfo, value As Object) As String
            If p.PropertyType Is GetType(String) Then Return If(CStr(value), "")
            Return JsonSerializer.Serialize(value, p.PropertyType)
        End Function

        ''' <summary>Liest einen Wert aus dem eigenen Raum zurueck; Nothing, wenn er nicht passt.</summary>
        Friend Shared Function ReadOwnValue(p As PropertyInfo, raw As String) As Object
            If p.PropertyType Is GetType(String) Then Return If(raw, "")
            Try
                Return JsonSerializer.Deserialize(raw, p.PropertyType)
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function Pair(key As String, value As String) As KeyValuePair(Of String, String)
            Return New KeyValuePair(Of String, String)(key, value)
        End Function

        ''' Fuer Attribute und Elementtext: die fuenf Zeichen, die XML vorbehalten sind, dazu
        ''' Zeilenumbruch und Tabulator, die ein Leser in einem Attribut sonst zu Leerzeichen macht.
        Friend Shared Function Escape(value As String) As String
            If String.IsNullOrEmpty(value) Then Return ""
            Return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").
                         Replace("""", "&quot;").Replace("'", "&apos;").
                         Replace(vbCr, "&#13;").Replace(vbLf, "&#10;").Replace(vbTab, "&#9;")
        End Function

        ''' <summary>Die Abbildung selbst: je gewaehltem Feld der crs:-Schluessel, wenn der Wert
        ''' sich dort ausdruecken laesst. <see cref="Covered"/> sammelt die Felder, die so
        ''' abgedeckt sind.</summary>
        Private NotInheritable Class Writer
            Private ReadOnly _recipe As ImageAdjustments
            Private ReadOnly _fields As HashSet(Of String)
            Public ReadOnly Attributes As New List(Of KeyValuePair(Of String, String))()
            Public ReadOnly Elements As New StringBuilder()
            Public ReadOnly Covered As New HashSet(Of String)(StringComparer.Ordinal)

            Public Sub New(recipe As ImageAdjustments, fields As IEnumerable(Of String))
                _recipe = recipe
                _fields = New HashSet(Of String)(fields, StringComparer.Ordinal)
            End Sub

            Private Function Has(field As String) As Boolean
                Return _fields.Contains(field)
            End Function

            Private Sub Put(key As String, value As String)
                Attributes.Add(New KeyValuePair(Of String, String)(key, value))
            End Sub

            ''' Ein Regler in derselben Skala wie der crs:-Schluessel.
            Private Sub Direct(field As String, key As String, value As Single, min As Double, max As Double)
                If Not Has(field) OrElse value < min OrElse value > max Then Return
                Put(key, Number(value))
                Covered.Add(field)
            End Sub

            ''' Ein Regler, dessen crs:-Wert erst umgerechnet ist; Bereich auf der crs:-Seite.
            Private Sub Scaled(field As String, key As String, value As Double, min As Double, max As Double)
                If Not Has(field) OrElse Double.IsNaN(value) OrElse value < min OrElse value > max Then Return
                Put(key, Number(value))
                Covered.Add(field)
            End Sub

            Public Sub WriteAll()
                Dim r = _recipe
                ' Licht. Die Helligkeit kennt ab Prozessversion 2012 keinen Schluessel mehr.
                Scaled("Exposure", "Exposure2012", r.Exposure / ImageAdjustments.ExposurePointsPerStop, -5, 5)
                Direct("Contrast", "Contrast2012", r.Contrast, -100, 100)
                Direct("Highlights", "Highlights2012", r.Highlights, -100, 100)
                Direct("ShadowsLevel", "Shadows2012", r.ShadowsLevel, -100, 100)
                Direct("Whites", "Whites2012", r.Whites, -100, 100)
                Direct("Blacks", "Blacks2012", r.Blacks, -100, 100)

                WriteWhiteBalance()
                Direct("Vibrance", "Vibrance", r.Vibrance, -100, 100)
                Direct("Saturation", "Saturation", r.Saturation, -100, 100)

                ' Details. Dunst ist crs:Dehaze mit umgekehrtem Vorzeichen; seine Fassung hat kein
                ' Gegenstueck und geht in den eigenen Raum.
                Direct("Clarity", "Clarity2012", r.Clarity, -100, 100)
                Direct("Structure", "Texture", r.[Structure], -100, 100)
                Scaled("Haze", "Dehaze", -CDbl(r.Haze), -100, 100)

                Direct("NoiseReduction", "LuminanceSmoothing", r.NoiseReduction, 0, 100)
                Direct("NoiseReductionDetail", "LuminanceNoiseReductionDetail", r.NoiseReductionDetail, 0, 100)
                Direct("ColorNoiseReduction", "ColorNoiseReduction", r.ColorNoiseReduction, 0, 100)

                Direct("Sharpness", "Sharpness", r.Sharpness, 0, 150)
                ' Unser Radius 0 bis 100 ist crs:SharpenRadius 1 bis 3.
                Scaled("SharpenRadius", "SharpenRadius", r.SharpenRadius / 50.0 + 1.0, 1, 3)
                Direct("SharpenDetail", "SharpenDetail", r.SharpenDetail, 0, 100)
                Direct("SharpenMasking", "SharpenEdgeMasking", r.SharpenMasking, 0, 100)

                WriteVignette()

                Direct("Grain", "GrainAmount", r.Grain, 0, 100)
                Direct("GrainSize", "GrainSize", r.GrainSize, 0, 100)
                Direct("GrainFrequency", "GrainFrequency", r.GrainFrequency, 0, 100)

                Direct("CalibrationRedHue", "RedHue", r.CalibrationRedHue, -100, 100)
                Direct("CalibrationRedSaturation", "RedSaturation", r.CalibrationRedSaturation, -100, 100)
                Direct("CalibrationGreenHue", "GreenHue", r.CalibrationGreenHue, -100, 100)
                Direct("CalibrationGreenSaturation", "GreenSaturation", r.CalibrationGreenSaturation, -100, 100)
                Direct("CalibrationBlueHue", "BlueHue", r.CalibrationBlueHue, -100, 100)
                Direct("CalibrationBlueSaturation", "BlueSaturation", r.CalibrationBlueSaturation, -100, 100)
                Direct("CalibrationShadowTint", "ShadowTint", r.CalibrationShadowTint, -100, 100)

                Dim monochrome = WriteFilter()
                WriteMixer(monochrome)
                WriteColorGrading()
                WriteCurves()
            End Sub

            ''' <summary>DER WEISSABGLEICH, vier Felder in einem Eintrag. Mit Kelvin-Zahl (eine RAW)
            ''' geht sie als crs:Temperature hinaus, ihre Toenung als crs:Tint; sonst der relative
            ''' Regler als crs:IncrementalTemperature und -Tint. Was daneben noch steht, geht in den
            ''' eigenen Raum, der beim Lesen zuletzt gilt. Ganz neutral heisst "wie Aufnahme".</summary>
            Private Sub WriteWhiteBalance()
                If Not Has("Temperature") AndAlso Not Has("Tint") AndAlso
                   Not Has("WhiteBalanceKelvin") AndAlso Not Has("WhiteBalanceKelvinTint") Then Return
                Dim r = _recipe
                If r.WhiteBalanceKelvin >= WhiteBalanceAdaptation.MinKelvin Then
                    Put("WhiteBalance", "Custom")
                    Put("Temperature", Number(Math.Round(Math.Min(WhiteBalanceAdaptation.MaxKelvin, r.WhiteBalanceKelvin))))
                    Covered.Add("WhiteBalanceKelvin")
                    If Math.Abs(r.WhiteBalanceKelvinTint) <= 150 Then
                        Put("Tint", Number(r.WhiteBalanceKelvinTint))
                        Covered.Add("WhiteBalanceKelvinTint")
                    End If
                    ' Ein absoluter Weissabgleich traegt keinen relativen daneben. Stuende die Null im
                    ' eigenen Raum, loeschte sie auf einem Bild ohne Aufnahme-Weisspunkt die
                    ' Temperatur, die der Leser aus der Kelvin-Zahl naehert.
                    If r.Temperature = 0 Then Covered.Add("Temperature")
                    If r.Tint = 0 Then Covered.Add("Tint")
                ElseIf r.Temperature <> 0 OrElse r.Tint <> 0 Then
                    Put("WhiteBalance", "Custom")
                    Put("IncrementalTemperature", Number(r.Temperature))
                    Covered.Add("Temperature")
                    If Math.Abs(r.Tint) <= 100 Then
                        Put("IncrementalTint", Number(r.Tint))
                        Covered.Add("Tint")
                    End If
                    ' Ebenso umgekehrt: der relative Regler an einer RAW wird beim Lesen zur
                    ' Kelvin-Zahl mit der Toenung der Aufnahme.
                    Covered.Add("WhiteBalanceKelvin")
                    If r.WhiteBalanceKelvinTint = 0 Then Covered.Add("WhiteBalanceKelvinTint")
                ElseIf r.WhiteBalanceKelvinTint <> 0 Then
                    ' Nur eine Toenung zur Aufnahme: an einer RAW liest sie sich als Versatz.
                    Put("WhiteBalance", "Custom")
                    If Math.Abs(r.WhiteBalanceKelvinTint) <= 150 Then
                        Put("Tint", Number(r.WhiteBalanceKelvinTint))
                        Covered.Add("WhiteBalanceKelvinTint")
                    End If
                    Covered.Add("WhiteBalanceKelvin")
                    Covered.Add("Temperature")
                    Covered.Add("Tint")
                Else
                    Put("WhiteBalance", "As Shot")
                    Covered.Add("Temperature")
                    Covered.Add("Tint")
                    Covered.Add("WhiteBalanceKelvin")
                    Covered.Add("WhiteBalanceKelvinTint")
                End If
                ' Nur, was gewaehlt ist, zaehlt als abgedeckt.
                Covered.RemoveWhere(Function(f) Not Has(f))
            End Sub

            ''' <summary>Die Vignette. Mittelpunkt, weiche Kante und Rundheit liest der Leser nur
            ''' bei einer Menge ungleich null; ohne sie gehen sie in den eigenen Raum. Der Stil gilt
            ''' immer (1 Lichterprioritaet, 2 Farbprioritaet, 3 Farbueberlagerung).</summary>
            Private Sub WriteVignette()
                Dim r = _recipe
                Scaled("Vignette", "PostCropVignetteAmount", -CDbl(r.Vignette), -100, 100)
                If Covered.Contains("Vignette") AndAlso r.Vignette <> 0 Then
                    Direct("VignetteTransition", "PostCropVignetteMidpoint", r.VignetteTransition, 0, 100)
                    Direct("VignetteFeather", "PostCropVignetteFeather", r.VignetteFeather, 0, 100)
                    Direct("VignetteRoundness", "PostCropVignetteRoundness", r.VignetteRoundness, -100, 100)
                End If
                If Has("VignetteStyle") Then
                    Dim style As Integer
                    Select Case r.VignetteStyle
                        Case VignetteStyle.HighlightPriority : style = 1
                        Case VignetteStyle.PaintOverlay : style = 3
                        Case Else : style = 2
                    End Select
                    Put("PostCropVignetteStyle", Number(style))
                    Covered.Add("VignetteStyle")
                End If
            End Sub

            ''' <summary>Der Filter. Nur Schwarzweiss in voller Staerke hat ein Gegenstueck
            ''' (crs:Treatment), "kein Filter" ist die Farbbehandlung. Jeder andere Filter und jede
            ''' andere Staerke geht in den eigenen Raum. Gibt zurueck, ob Schwarzweiss als crs:
            ''' geschrieben ist: dann gehoert der Graumischer dazu.</summary>
            Private Function WriteFilter() As Boolean
                If Not Has("FilterPreset") Then Return False
                Dim r = _recipe
                Dim preset = If(r.FilterPreset, "").Trim()
                If String.Equals(preset, "S/W", StringComparison.Ordinal) AndAlso
                   r.FilterStrength = ImageAdjustments.DefaultFilterStrength("S/W") Then
                    Put("Treatment", "Black & White")
                    Put("ConvertToGrayscale", "True")
                    Covered.Add("FilterPreset")
                    Covered.Add("FilterStrength")
                    Return True
                End If
                If String.Equals(preset, New ImageAdjustments().FilterPreset, StringComparison.Ordinal) Then
                    Put("Treatment", "Color")
                    Put("ConvertToGrayscale", "False")
                    Covered.Add("FilterPreset")
                    If r.FilterStrength = New ImageAdjustments().FilterStrength Then Covered.Add("FilterStrength")
                End If
                Return False
            End Function

            ''' <summary>Der Farbmischer. Der Farbton skaliert beim Lesen (XmpPresetService.
            ''' HueImportScales) und wird hier zurueckgeteilt; was danach ueber 100 laege, geht in den
            ''' eigenen Raum. In Schwarzweiss ist die Luminanz zugleich der Graumischer.</summary>
            Private Sub WriteMixer(monochrome As Boolean)
                Dim r = _recipe
                Dim scales = XmpPresetService.HueImportScales
                For i = 0 To HslBands.Length - 1
                    Dim band = HslBands(i)
                    Dim hue = CSng(GetType(ImageAdjustments).GetProperty(band & "Hue").GetValue(r))
                    Dim saturation = CSng(GetType(ImageAdjustments).GetProperty(band & "Saturation").GetValue(r))
                    Dim luminance = CSng(GetType(ImageAdjustments).GetProperty(band & "Luminance").GetValue(r))
                    If scales(i) > 0 Then Scaled(band & "Hue", "HueAdjustment" & band, hue / scales(i), -100, 100)
                    Direct(band & "Saturation", "SaturationAdjustment" & band, saturation, -100, 100)
                    Direct(band & "Luminance", "LuminanceAdjustment" & band, luminance, -100, 100)
                    If monochrome AndAlso Covered.Contains(band & "Luminance") Then Put("GrayMixer" & band, Number(luminance))
                Next
            End Sub

            ''' <summary>Die Farbgradierung in den neueren Schluesseln, die Balance im alten.</summary>
            Private Sub WriteColorGrading()
                Dim r = _recipe
                For Each zone In GradeZones
                    Dim hue = CSng(GetType(ImageAdjustments).GetProperty("ColorGrade" & zone & "Hue").GetValue(r))
                    Dim saturation = CSng(GetType(ImageAdjustments).GetProperty("ColorGrade" & zone & "Saturation").GetValue(r))
                    Dim luminance = CSng(GetType(ImageAdjustments).GetProperty("ColorGrade" & zone & "Luminance").GetValue(r))
                    Direct("ColorGrade" & zone & "Hue", "ColorGrade" & zone & "Hue", hue, 0, 360)
                    Direct("ColorGrade" & zone & "Saturation", "ColorGrade" & zone & "Sat", saturation, 0, 100)
                    Direct("ColorGrade" & zone & "Luminance", "ColorGrade" & zone & "Lum", luminance, -100, 100)
                Next
                Direct("ColorGradeBalance", "SplitToningBalance", r.ColorGradeBalance, -100, 100)
                Direct("ColorGradeBlending", "ColorGradeBlending", r.ColorGradeBlending, 0, 100)
            End Sub

            ''' <summary>Die Kurven als Punktkurven, die parametrischen Regler auf null. Die
            ''' Kanalkurven liest der Leser nur mit einer Gesamtkurve daneben; ist die nicht gewaehlt,
            ''' steht die Gerade dort.</summary>
            Private Sub WriteCurves()
                Dim r = _recipe
                Dim wantsMaster = Has("CurveRgbPoints")
                Dim wantsChannels = Has("CurveRedPoints") OrElse Has("CurveGreenPoints") OrElse Has("CurveBluePoints")
                If Not wantsMaster AndAlso Not wantsChannels Then Return
                Put("ToneCurveName2012", "Custom")
                If wantsMaster Then
                    Put("ParametricShadows", "0")
                    Put("ParametricDarks", "0")
                    Put("ParametricLights", "0")
                    Put("ParametricHighlights", "0")
                    Put("ParametricShadowSplit", "25")
                    Put("ParametricMidtoneSplit", "50")
                    Put("ParametricHighlightSplit", "75")
                    Covered.Add("CurveRgbPoints")
                End If
                AppendCurve("ToneCurvePV2012", If(wantsMaster, r.CurveRgbPoints, ""))
                If Has("CurveRedPoints") Then AppendCurve("ToneCurvePV2012Red", r.CurveRedPoints) : Covered.Add("CurveRedPoints")
                If Has("CurveGreenPoints") Then AppendCurve("ToneCurvePV2012Green", r.CurveGreenPoints) : Covered.Add("CurveGreenPoints")
                If Has("CurveBluePoints") Then AppendCurve("ToneCurvePV2012Blue", r.CurveBluePoints) : Covered.Add("CurveBluePoints")
            End Sub

            Private Sub AppendCurve(elementName As String, points As String)
                Elements.Append("   <crs:").Append(elementName).Append(">").Append(vbLf)
                Elements.Append("    <rdf:Seq>").Append(vbLf)
                For Each point In CurvePoints(points)
                    Elements.Append("     <rdf:li>").Append(point.X.ToString(CultureInfo.InvariantCulture)).
                             Append(", ").Append(point.Y.ToString(CultureInfo.InvariantCulture)).Append("</rdf:li>").Append(vbLf)
                Next
                Elements.Append("    </rdf:Seq>").Append(vbLf)
                Elements.Append("   </crs:").Append(elementName).Append(">").Append(vbLf)
            End Sub
        End Class

        ''' <summary>Die Punkte einer Kurve des Rezepts ("x,y;x,y"), gerundet auf ganze Stufen 0 bis
        ''' 255, nach x geordnet, je x der letzte. Weniger als zwei brauchbare Punkte ergeben die
        ''' Gerade.</summary>
        Friend Shared Function CurvePoints(points As String) As List(Of (X As Integer, Y As Integer))
            Dim byX As New SortedDictionary(Of Integer, Integer)()
            For Each part In If(points, "").Split(";"c)
                Dim xy = part.Split(","c)
                If xy.Length <> 2 Then Continue For
                Dim x, y As Double
                If Not Double.TryParse(xy(0).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, x) OrElse
                   Not Double.TryParse(xy(1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, y) OrElse
                   Not Double.IsFinite(x) OrElse Not Double.IsFinite(y) Then Continue For
                byX(CInt(Math.Max(0, Math.Min(255, Math.Round(x))))) = CInt(Math.Max(0, Math.Min(255, Math.Round(y))))
            Next
            If byX.Count < 2 Then Return New List(Of (X As Integer, Y As Integer)) From {(0, 0), (255, 255)}
            Return byX.Select(Function(kv) (kv.Key, kv.Value)).ToList()
        End Function

        ''' <summary>Eine Zahl wie im Referenzprogramm: positive mit Pluszeichen, ganze ohne
        ''' Nachkommastellen, sonst so viele Stellen, wie der Rueckweg braucht.</summary>
        Private Shared Function Number(value As Double) As String
            Dim text As String
            If value = Math.Round(value) AndAlso Math.Abs(value) < 1000000000.0 Then
                text = CLng(value).ToString(CultureInfo.InvariantCulture)
            Else
                text = value.ToString("R", CultureInfo.InvariantCulture)
            End If
            Return If(value > 0, "+" & text, text)
        End Function

        Private Shared Function Number(value As Single) As String
            If value = Math.Round(value) Then Return Number(CDbl(value))
            Dim text = value.ToString("R", CultureInfo.InvariantCulture)
            Return If(value > 0, "+" & text, text)
        End Function

    End Class

End Namespace
