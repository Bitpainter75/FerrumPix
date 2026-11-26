Imports System.Reflection

Namespace Services

    ''' <summary>Ein waehlbarer Eintrag einer Anpassungsvorlage: ein Regler des Editors oder mehrere
    ''' Felder des Rezepts, die nur zusammen Sinn ergeben (eine Kurve, ein Band des Farbmischers,
    ''' eine Zone der Farbgradierung, der Weissabgleich). Der Rahmen ist ein Eintrag ohne Felder.</summary>
    Public NotInheritable Class AdjustmentEntry
        ''' Stabile Kennung, "gruppe.name".
        Public ReadOnly Property Key As String
        ''' Der Schluessel der Gruppe wie in AppSettingsService.HideableAdjustmentGroups.
        Public ReadOnly Property GroupKey As String
        ''' Die deutsche Beschriftung wie am Regler im Editor; sie ist der Schluessel der Uebersetzung.
        Public ReadOnly Property Label As String
        ''' Die Felder von ImageAdjustments, die dieser Eintrag traegt. Beim Rahmen nur der
        ''' Platzhalter <see cref="AdjustmentEntries.FrameField"/>.
        Public ReadOnly Property Fields As String()
        Public ReadOnly Property IsFrame As Boolean

        Friend Sub New(key As String, groupKey As String, label As String, fields As String(), isFrame As Boolean)
            Me.Key = key
            Me.GroupKey = groupKey
            Me.Label = label
            Me.Fields = fields
            Me.IsFrame = isFrame
        End Sub
    End Class

    ''' <summary>Die feste Zuordnung Feld zu Eintrag zu Gruppe, dazu die reinen Funktionen, mit denen
    ''' eine Vorlage nur einen Teil der Anpassungen traegt und anwendet.
    '''
    ''' FEST GESCHRIEBEN UND NICHT ZUR LAUFZEIT ABGELEITET: der Gruppenvergleich des Editors ermittelt
    ''' die Felder einer Gruppe ueber deren Zuruecksetzer, das geht nur mit einem geoeffneten Bild.
    ''' Die Tabelle gilt auch im Stapel der Galerie. Der Pruefstand haelt sie zusammen: jeder
    ''' Pixelregler (ImageAdjustments.PixelAdjustmentProperties) gehoert zu genau einem Eintrag, und
    ''' der Zuruecksetzer einer Gruppe aendert nur Felder ihrer Eintraege. Wer einen Regler ergaenzt,
    ''' traegt ihn hier ein.
    '''
    ''' GESPEICHERT WIRD DIE LISTE DER FELDER, nicht die der Eintraege (AdjustmentPresetSettings.
    ''' Fields): die Feldnamen stehen ohnehin fuer immer im Rezept, die Eintraege duerfen spaeter
    ''' anders geschnitten werden. Ein Eintrag gilt als enthalten, sobald eines seiner Felder in der
    ''' Liste steht. Fehlt die Liste, gilt alles.
    '''
    ''' NUR PIXELREGLER UND DER RAHMEN. Modell und Anker des Weissabgleichs, Geometrie, Masken,
    ''' Objekte und die Objektivkorrektur gehoeren dem Bild und wandern mit keiner Vorlage
    ''' (siehe ImageAdjustments.StructuralPropertyNames).</summary>
    Public NotInheritable Class AdjustmentEntries

        ''' Der Platzhalter des Rahmens in der Feldliste einer Vorlage. Kein Feld des Rezepts: der
        ''' Rahmen ist ein Objekt (ImageAdjustments.IsFrameAnnotation).
        Public Const FrameField As String = "Frame"

        Private Shared ReadOnly _all As AdjustmentEntry() = BuildTable()
        Private Shared _properties As Dictionary(Of String, PropertyInfo) = Nothing
        Private Shared ReadOnly _propertiesLock As New Object()

        Private Sub New()
        End Sub

        Private Shared Function E(key As String, groupKey As String, label As String, ParamArray fields As String()) As AdjustmentEntry
            Return New AdjustmentEntry(groupKey & "." & key, groupKey, label, fields, False)
        End Function

        Private Shared Function BuildTable() As AdjustmentEntry()
            Dim list As New List(Of AdjustmentEntry)()

            list.Add(E("exposure", "light", "Belichtung (EV)", "Exposure"))
            list.Add(E("brightness", "light", "Helligkeit", "Brightness"))
            list.Add(E("contrast", "light", "Kontrast", "Contrast"))
            list.Add(E("highlights", "light", "Lichter", "Highlights", "ToneRuleVersion"))
            list.Add(E("shadows", "light", "Tiefen", "ShadowsLevel"))
            list.Add(E("whites", "light", "Weiß", "Whites"))
            list.Add(E("blacks", "light", "Schwarz", "Blacks"))

            ' Temperatur und Toenung stehen je nach Weissabgleichmodell in zwei Feldpaaren; einzeln
            ' uebertragen ergaebe eine halbe Angabe.
            list.Add(E("white-balance", "color", "Weißabgleich",
                       "Temperature", "Tint", "WhiteBalanceKelvin", "WhiteBalanceKelvinTint"))
            list.Add(E("vibrance", "color", "Dynamik", "Vibrance"))
            list.Add(E("saturation", "color", "Sättigung", "Saturation"))

            list.Add(E("rgb", "curve", "RGB", "CurveRgbPoints"))
            list.Add(E("red", "curve", "Rot", "CurveRedPoints"))
            list.Add(E("green", "curve", "Grün", "CurveGreenPoints"))
            list.Add(E("blue", "curve", "Blau", "CurveBluePoints"))
            list.Add(E("luminance", "curve", "Luminanz", "CurveLuminancePoints"))

            For Each band In {("Red", "Rot"), ("Orange", "Orange"), ("Yellow", "Gelb"), ("Green", "Grün"),
                              ("Aqua", "Aqua"), ("Blue", "Blau"), ("Purple", "Lila"), ("Magenta", "Magenta")}
                list.Add(E(band.Item1.ToLowerInvariant(), "hsl", band.Item2,
                           band.Item1 & "Hue", band.Item1 & "Saturation", band.Item1 & "Luminance"))
            Next
            list.Add(E("point-color", "hsl", "Punktfarbe", "PointColors"))

            For Each zone In {("Shadow", "Schatten"), ("Midtone", "Mitten"), ("Highlight", "Lichter"), ("Global", "Global")}
                list.Add(E(zone.Item1.ToLowerInvariant(), "color-grading", zone.Item2,
                           "ColorGrade" & zone.Item1 & "Hue", "ColorGrade" & zone.Item1 & "Saturation",
                           "ColorGrade" & zone.Item1 & "Luminance"))
            Next
            list.Add(E("balance", "color-grading", "Balance", "ColorGradeBalance"))
            list.Add(E("blending", "color-grading", "Überblendung", "ColorGradeBlending"))

            ' Die Mischmatrix ist EINE Angabe: eine Zeile ohne die anderen oder ohne den Schalter
            ' Monochrom beschreibt keinen Mischer.
            list.Add(E("mixer", "channel-mixer", "Kanalmixer",
                       "ChannelMixerRedRed", "ChannelMixerRedGreen", "ChannelMixerRedBlue", "ChannelMixerRedConstant",
                       "ChannelMixerGreenRed", "ChannelMixerGreenGreen", "ChannelMixerGreenBlue", "ChannelMixerGreenConstant",
                       "ChannelMixerBlueRed", "ChannelMixerBlueGreen", "ChannelMixerBlueBlue", "ChannelMixerBlueConstant",
                       "ChannelMixerMonochrome", "ChannelMixerGrayRed", "ChannelMixerGrayGreen", "ChannelMixerGrayBlue",
                       "ChannelMixerGrayConstant"))

            list.Add(E("map", "gradient-map", "Verlaufsumsetzung",
                       "GradientMapShadowColor", "GradientMapHighlightColor", "GradientMapAmount"))

            list.Add(E("shadow-tint", "calibration", "Tiefen Tönung", "CalibrationShadowTint"))
            list.Add(E("red-hue", "calibration", "Rot Farbton", "CalibrationRedHue"))
            list.Add(E("red-saturation", "calibration", "Rot Sättigung", "CalibrationRedSaturation"))
            list.Add(E("green-hue", "calibration", "Grün Farbton", "CalibrationGreenHue"))
            list.Add(E("green-saturation", "calibration", "Grün Sättigung", "CalibrationGreenSaturation"))
            list.Add(E("blue-hue", "calibration", "Blau Farbton", "CalibrationBlueHue"))
            list.Add(E("blue-saturation", "calibration", "Blau Sättigung", "CalibrationBlueSaturation"))

            ' Umkehr, Filmbasis, Dichtepunkt, Gradation und Rechenweg gehoeren zusammen. Der
            ' Graufarbausgleich steht dabei, weil er mit der Umkehr geleert und am Ziel neu gemessen
            ' wird (ImageAdjustments.ForgetTransferredNegativeGrayBalance).
            list.Add(E("negative", "film-negative", "Filmnegativ",
                       "NegativeEnabled", "NegativeMonochrome", "NegativeBaseColor", "NegativeDensityColor",
                       "NegativeGamma", "NegativeModel", "NegativeGrayBalance"))

            list.Add(E("clarity", "details", "Klarheit", "Clarity"))
            list.Add(E("structure", "details", "Struktur", "Structure"))
            ' Die Fassung des Dunstes geht mit dem Dunst: eingefuegt sieht er aus wie in der Quelle.
            list.Add(E("haze", "details", "Dunst", "Haze", "HazeModel"))
            list.Add(E("glow", "details", "Glühen", "Glow"))
            list.Add(E("blur", "details", "Weichzeichnen", "Blur"))

            list.Add(E("luminance", "rauschen", "Luminanz", "NoiseReduction"))
            list.Add(E("detail", "rauschen", "Details", "NoiseReductionDetail"))
            list.Add(E("method", "rauschen", "Methode", "NoiseReductionMethod"))
            ' Farbflecken und Fleckengroesse haben keinen Regler mehr, ein altes Rezept traegt sie
            ' aber; sie gehoeren zur Farbe der Rauschreduzierung.
            list.Add(E("color", "rauschen", "Farbe", "ColorNoiseReduction", "FarbrauschGrob", "ColorNoiseCoarseScale"))

            list.Add(E("amount", "sharpen", "Stärke", "Sharpness"))
            list.Add(E("radius", "sharpen", "Radius", "SharpenRadius"))
            list.Add(E("detail", "sharpen", "Details", "SharpenDetail"))
            list.Add(E("masking", "sharpen", "Maskieren", "SharpenMasking"))
            list.Add(E("method", "sharpen", "Methode", "SharpenMethod"))

            list.Add(E("amount", "vignette", "Stärke", "Vignette"))
            list.Add(E("transition", "vignette", "Übergang", "VignetteTransition"))
            list.Add(E("roundness", "vignette", "Rundheit", "VignetteRoundness"))
            list.Add(E("feather", "vignette", "Weiche Kante", "VignetteFeather"))
            list.Add(E("center-x", "vignette", "Mitte X", "VignetteCenterX"))
            list.Add(E("center-y", "vignette", "Mitte Y", "VignetteCenterY"))
            list.Add(E("style", "vignette", "Stil", "VignetteStyle"))

            list.Add(New AdjustmentEntry("frame.frame", "frame", "Rahmen", {FrameField}, True))

            ' Rauschen, Farbrauschen und Staub/Kratzer haben keinen Regler mehr; ein altes Rezept
            ' traegt sie, und sie gehen mit der Staerke der Koernung.
            ' Die Fassung der Koernung geht mit der Staerke: eingefuegt sieht das Korn aus wie in der Quelle.
            list.Add(E("amount", "grain", "Stärke", "Grain", "GrainModel", "AddNoise", "ColorNoiseAdd", "DustScratches"))
            list.Add(E("size", "grain", "Größe", "GrainSize"))
            list.Add(E("roughness", "grain", "Rauheit", "GrainFrequency"))
            list.Add(E("color", "grain", "Farbe", "GrainColor"))

            list.Add(E("levels", "posterize", "Stufen", "PosterizeLevels"))
            list.Add(E("level", "threshold", "Schwelle", "ThresholdLevel"))

            list.Add(E("amount", "tiltshift", "Stärke", "TiltShiftAmount"))
            list.Add(E("focus", "tiltshift", "Schärfebereich", "TiltShiftFocus"))
            list.Add(E("transition", "tiltshift", "Übergang", "TiltShiftTransition"))
            list.Add(E("angle", "tiltshift", "Winkel", "TiltShiftAngle"))
            list.Add(E("center-x", "tiltshift", "Mitte X", "TiltShiftCenterX"))
            list.Add(E("center-y", "tiltshift", "Mitte Y", "TiltShiftCenterY"))

            list.Add(E("settings", "extensions", "Erweiterungen", "ExtensionSettings"))
            list.Add(E("filter", "filter", "Filter", "FilterPreset", "FilterStrength"))
            list.Add(E("lut", "lut", "LUT (.cube)", "LutPath", "LutStrength"))

            Return list.ToArray()
        End Function

        ''' <summary>Alle Eintraege in der Reihenfolge der Gruppen und Regler im Editor.</summary>
        Public Shared ReadOnly Property All As IReadOnlyList(Of AdjustmentEntry)
            Get
                Return _all
            End Get
        End Property

        ''' <summary>Die Gruppen mit Eintraegen, in der Reihenfolge der Tabelle.</summary>
        Public Shared Function GroupKeys() As String()
            Return _all.Select(Function(e) e.GroupKey).Distinct(StringComparer.Ordinal).ToArray()
        End Function

        ''' <summary>Die deutsche Beschriftung einer Gruppe, aus der einen Liste der Gruppen
        ''' (AppSettingsService.HideableAdjustmentGroups).</summary>
        Public Shared Function GroupLabel(groupKey As String) As String
            For Each group In AppSettingsService.HideableAdjustmentGroups
                If String.Equals(group.Key, groupKey, StringComparison.Ordinal) Then Return group.Bezeichnung
            Next
            Return If(groupKey, "")
        End Function

        Public Shared Function EntriesOfGroup(groupKey As String) As List(Of AdjustmentEntry)
            Return _all.Where(Function(e) String.Equals(e.GroupKey, groupKey, StringComparison.Ordinal)).ToList()
        End Function

        Public Shared Function Find(key As String) As AdjustmentEntry
            Return _all.FirstOrDefault(Function(e) String.Equals(e.Key, key, StringComparison.Ordinal))
        End Function

        ''' <summary>Die Eintraege, die eine gespeicherte Feldliste meint. Nothing (keine Liste)
        ''' heisst alles. Eine LEERE Liste heisst nichts; unbekannte Namen zaehlen nicht.</summary>
        Public Shared Function EntriesForFields(fields As IEnumerable(Of String)) As List(Of AdjustmentEntry)
            If fields Is Nothing Then Return _all.ToList()
            Dim wanted As New HashSet(Of String)(fields.Where(Function(f) f IsNot Nothing).Select(Function(f) f.Trim()), StringComparer.Ordinal)
            Return _all.Where(Function(e) e.Fields.Any(Function(f) wanted.Contains(f))).ToList()
        End Function

        ''' <summary>Die Feldliste zu einer Auswahl von Eintraegen, in der Reihenfolge der Tabelle
        ''' und ohne Doppel. So steht sie in der Vorlage.</summary>
        Public Shared Function FieldsOf(entries As IEnumerable(Of AdjustmentEntry)) As List(Of String)
            Dim chosen = ToSet(entries)
            Return _all.Where(Function(e) chosen.Contains(e.Key)).SelectMany(Function(e) e.Fields).ToList()
        End Function

        ''' <summary>Deckt die Auswahl jeden Eintrag ab? Dann gilt sie als "alles", und die Vorlage
        ''' traegt keine Liste.</summary>
        Public Shared Function CoversEverything(entries As IEnumerable(Of AdjustmentEntry)) As Boolean
            Dim chosen = ToSet(entries)
            Return _all.All(Function(e) chosen.Contains(e.Key))
        End Function

        Private Shared Function ToSet(entries As IEnumerable(Of AdjustmentEntry)) As HashSet(Of String)
            Return New HashSet(Of String)(
                If(entries, Enumerable.Empty(Of AdjustmentEntry)()).Where(Function(e) e IsNot Nothing).Select(Function(e) e.Key),
                StringComparer.Ordinal)
        End Function

        Private Shared Function PropertyOf(field As String) As PropertyInfo
            SyncLock _propertiesLock
                If _properties Is Nothing Then
                    _properties = ImageAdjustments.PixelAdjustmentProperties().ToDictionary(Function(p) p.Name, StringComparer.Ordinal)
                End If
                Dim found As PropertyInfo = Nothing
                _properties.TryGetValue(field, found)
                Return found
            End SyncLock
        End Function

        ''' <summary>Zieht aus <paramref name="recipe"/> nur die gewaehlten Eintraege heraus: ein
        ''' neues Rezept, in dem alles andere auf dem Neutralwert steht. <paramref name="recipe"/>
        ''' ist dabei schon die uebertragbare Fassung (ImageAdjustments.
        ''' ExtractTransferableAdjustments): ihr Rahmen kommt mit, wenn der Eintrag Rahmen gewaehlt
        ''' ist. Modell und Anker des Weissabgleichs bleiben wie dort im Rezept stehen, solange der
        ''' Weissabgleich gewaehlt ist; beim Anwenden wandern sie ohnehin nicht.</summary>
        Public Shared Function Extract(recipe As ImageAdjustments, entries As IEnumerable(Of AdjustmentEntry)) As ImageAdjustments
            Dim result As New ImageAdjustments()
            If recipe Is Nothing Then Return result
            Dim chosen = ToSet(entries)
            For Each entry In _all
                If Not chosen.Contains(entry.Key) Then Continue For
                If entry.IsFrame Then
                    Dim frame = If(recipe.Annotations, New List(Of ImageAnnotation)()).
                        FirstOrDefault(AddressOf ImageAdjustments.IsFrameAnnotation)
                    If frame IsNot Nothing Then result.Annotations.Add(frame.Clone())
                    Continue For
                End If
                For Each field In entry.Fields
                    Dim p = PropertyOf(field)
                    If p IsNot Nothing Then p.SetValue(result, p.GetValue(recipe))
                Next
                If String.Equals(entry.Key, "color.white-balance", StringComparison.Ordinal) Then
                    result.WhiteBalanceModel = recipe.WhiteBalanceModel
                    result.WhiteBalanceAnchorX = recipe.WhiteBalanceAnchorX
                    result.WhiteBalanceAnchorY = recipe.WhiteBalanceAnchorY
                    result.WhiteBalanceCamera = recipe.WhiteBalanceCamera
                End If
            Next
            Return result
        End Function

        ''' <summary>Setzt die gewaehlten Eintraege aus <paramref name="source"/> in
        ''' <paramref name="target"/>. Ein gewaehlter Eintrag ERSETZT vollstaendig, auch mit dem
        ''' Neutralwert: sonst bliebe in einem gewaehlten Regler der alte Wert des Ziels stehen. Alle
        ''' nicht gewaehlten Felder bleiben, wie sie sind.
        '''
        ''' Der Rahmen folgt seiner eigenen Regel (ImageAdjustments.ApplyTransferredFrameFrom): bringt
        ''' die Quelle einen mit, ersetzt er den des Ziels; bringt sie keinen mit, bleibt der des
        ''' Ziels stehen.</summary>
        Public Shared Sub Apply(target As ImageAdjustments, source As ImageAdjustments, entries As IEnumerable(Of AdjustmentEntry))
            If target Is Nothing OrElse source Is Nothing Then Return
            Dim chosen = ToSet(entries)
            Dim matteredBefore = target.ToneRuleMatters()
            Dim versionBefore = target.ToneRuleVersion
            Dim highlightsChosen = False
            For Each entry In _all
                If Not chosen.Contains(entry.Key) Then Continue For
                If entry.IsFrame Then
                    target.ApplyTransferredFrameFrom(source)
                    Continue For
                End If
                For Each field In entry.Fields
                    Dim p = PropertyOf(field)
                    If p IsNot Nothing Then p.SetValue(target, p.GetValue(source))
                    If field = NameOf(ImageAdjustments.Highlights) Then highlightsChosen = True
                Next
            Next
            ' Die Tonfassung (ImageAdjustments.ToneRuleVersion) steht nur dort, wo sie wirkt. Wirkt sie
            ' in der Quelle und kommen deren Lichter mit, gilt ihre; wirkt sie im Ziel erst jetzt, ist
            ' das eine neue Einstellung und bekommt die aktuelle; sonst bleibt die des Ziels.
            If Not target.ToneRuleMatters() Then
                target.ToneRuleVersion = 0
            ElseIf highlightsChosen AndAlso source.ToneRuleMatters() Then
                target.ToneRuleVersion = source.ToneRuleVersion
            ElseIf Not matteredBefore Then
                target.ToneRuleVersion = ImageAdjustments.CurrentToneRuleVersion
            Else
                target.ToneRuleVersion = versionBefore
            End If
        End Sub

        ''' <summary>Welche Eintraege in <paramref name="recipe"/> vom Neutralwert abweichen: die
        ''' Vorbelegung beim Speichern einer Vorlage. Der Rahmen zaehlt, wenn das Rezept einen
        ''' traegt.</summary>
        Public Shared Function NonNeutralEntries(recipe As ImageAdjustments) As List(Of AdjustmentEntry)
            Dim result As New List(Of AdjustmentEntry)()
            If recipe Is Nothing Then Return result
            Dim neutral As New ImageAdjustments()
            For Each entry In _all
                If entry.IsFrame Then
                    If recipe.Annotations IsNot Nothing AndAlso recipe.Annotations.Any(AddressOf ImageAdjustments.IsFrameAnnotation) Then result.Add(entry)
                    Continue For
                End If
                For Each field In entry.Fields
                    Dim p = PropertyOf(field)
                    If p Is Nothing Then Continue For
                    ' Die Fassung des Dunstes allein macht den Eintrag nicht abweichend, der Dunst schon.
                    If field = NameOf(ImageAdjustments.HazeModel) Then Continue For
                    ' Ebenso die Fassung der Koernung.
                    If field = NameOf(ImageAdjustments.GrainModel) Then Continue For
                    ' Und die Tonfassung beim Eintrag Lichter.
                    If field = NameOf(ImageAdjustments.ToneRuleVersion) Then Continue For
                    If Not Object.Equals(p.GetValue(recipe), p.GetValue(neutral)) Then
                        result.Add(entry)
                        Exit For
                    End If
                Next
            Next
            Return result
        End Function

        ''' <summary>Das Rezept einer gespeicherten Vorlage, beschraenkt auf ihre Eintraege: was nicht
        ''' enthalten ist, steht auf dem Neutralwert, auch wenn das gespeicherte Rezept dort etwas
        ''' traegt. Fuer den Stapel der Galerie, der nur ablegt, was vom Neutralwert abweicht
        ''' (ImageAdjustments.MergeNonDefaultPixelAdjustmentsFrom): ohne diesen Schritt ueberschriebe
        ''' eine Teilvorlage mit vollem Rezept auch, was sie nicht enthaelt. Eine Vorlage ohne Liste
        ''' kommt unveraendert zurueck. Nothing, wenn das Rezept unlesbar ist.</summary>
        Public Shared Function LoadPresetLook(preset As AdjustmentPresetSettings) As ImageAdjustments
            If preset Is Nothing Then Return Nothing
            Dim recipe As ImageAdjustments
            Try
                recipe = FpxService.DeserializeAdjustments(preset.RecipeJson)
            Catch
                Return Nothing
            End Try
            If recipe Is Nothing OrElse preset.Fields Is Nothing Then Return recipe
            Return Extract(recipe, EntriesForFields(preset.Fields))
        End Function

        ''' <summary>Wendet eine gespeicherte Vorlage auf <paramref name="target"/> an, wie der Editor
        ''' es tut: ihre Eintraege ersetzen, alles andere bleibt. Eine Vorlage ohne Liste ersetzt
        ''' wie bisher alle Pixelregler und setzt einen mitgebrachten Rahmen.</summary>
        Public Shared Sub ApplyPreset(target As ImageAdjustments, recipe As ImageAdjustments, fields As IEnumerable(Of String))
            If target Is Nothing OrElse recipe Is Nothing Then Return
            If fields Is Nothing Then
                target.CopyPixelAdjustmentsFrom(recipe)
                target.ApplyTransferredFrameFrom(recipe)
                Return
            End If
            Apply(target, recipe, EntriesForFields(fields))
        End Sub

        ''' <summary>Der Kurzhinweis, was eine Vorlage enthaelt, in der Sprache der Oberflaeche: je
        ''' Gruppe ihr Name, bei einer nur teilweise enthaltenen dazu ihre Regler. Ohne Liste "Alle
        ''' Anpassungen".</summary>
        Public Shared Function Describe(fields As IEnumerable(Of String)) As String
            If fields Is Nothing Then Return LocalizationService.T("Alle Anpassungen")
            Dim chosen = ToSet(EntriesForFields(fields))
            If chosen.Count = 0 Then Return LocalizationService.T("Keine Anpassungen")
            Dim parts As New List(Of String)()
            For Each groupKey In GroupKeys()
                Dim members = EntriesOfGroup(groupKey)
                Dim present = members.Where(Function(e) chosen.Contains(e.Key)).ToList()
                If present.Count = 0 Then Continue For
                Dim groupName = LocalizationService.T(GroupLabel(groupKey))
                If present.Count = members.Count Then
                    parts.Add(groupName)
                Else
                    parts.Add(groupName & ": " & String.Join(", ", present.Select(Function(e) LocalizationService.T(e.Label))))
                End If
            Next
            Return String.Join(Environment.NewLine, parts)
        End Function

    End Class

End Namespace
