Imports System.Reflection
Imports System.Windows.Input
Imports FerrumPix.Services
Imports ReactiveUI

Namespace ViewModels

    Partial Public Class EditorViewModel

        ''' <summary>Der Vergleich einer ganzen Anpassungsgruppe: was er ausgeblendet hat und worauf.
        '''
        ''' DAS GEGENSTUECK ZUM REGLER (RoundSlider.ToggleCompare) UND ZUR KURVE (ToggleCurveCompare),
        ''' nur fuer alle Regler einer Gruppe auf einmal. Der Knopf im Gruppenkopf setzt die Gruppe
        ''' ueber ihren eigenen Zuruecksetzer auf den Startwert, merkt sich, was vorher dastand, und
        ''' der naechste Klick holt es zurueck.
        '''
        ''' WELCHE EIGENSCHAFTEN DAZUGEHOEREN, sagt der Zuruecksetzer selbst: gemerkt wird genau das,
        ''' was er geaendert hat. Eine zweite Liste je Gruppe gibt es damit nicht, und ein neuer Regler,
        ''' den der Zuruecksetzer kennt, ist automatisch dabei. Ein Regler der Gruppe, der schon auf dem
        ''' Startwert stand, gehoert deshalb nicht zum Vergleich; wer ihn waehrenddessen bewegt, behaelt
        ''' seinen neuen Wert, und der Vergleich der uebrigen laeuft weiter.
        '''
        ''' Wie am Regler gilt: aendert etwas anderes einen der ausgeblendeten Werte (Bearbeiten,
        ''' Rueckgaengig, ein Preset), gilt der neue Stand, und der gemerkte ist verworfen.</summary>
        Private NotInheritable Class AdjustmentGroupCompare
            ''' Die eingestellten Werte, je Eigenschaft des Rezepts.
            Public ReadOnly Saved As New Dictionary(Of String, Object)(StringComparer.Ordinal)
            ''' Die Werte, auf die der Vergleich sie gesetzt hat.
            Public ReadOnly Neutral As New Dictionary(Of String, Object)(StringComparer.Ordinal)
        End Class

        ''' Die Gruppen im Vergleich, nach dem Schluessel ihres Aufklappers (ExpanderState.Key).
        Private ReadOnly _groupCompares As New Dictionary(Of String, AdjustmentGroupCompare)(StringComparer.Ordinal)
        Private _comparingAdjustmentGroups As String = ""

        Private Shared _groupCompareProperties As PropertyInfo() = Nothing

        ''' <summary>Was ein Gruppenvergleich ueberhaupt merken kann: die Pixelregler und dazu die
        ''' Feld des Rezepts, das ein Gruppen-Zuruecksetzer ausserdem anfasst, obwohl es strukturell
        ''' ist - die Weissabgleich-Vorgabe der Gruppe Farbe. (Die Lichterrettung der Gruppe Licht
        ''' ist immer an und hat nichts zu merken.)</summary>
        Private Shared Function GroupCompareProperties() As PropertyInfo()
            If _groupCompareProperties Is Nothing Then
                Dim extra = {NameOf(ImageAdjustments.WhiteBalanceMode)}.
                    Select(Function(n) GetType(ImageAdjustments).GetProperty(n))
                _groupCompareProperties = ImageAdjustments.PixelAdjustmentProperties().Concat(extra).ToArray()
            End If
            Return _groupCompareProperties
        End Function

        ''' <summary>Der Name der Gruppe in der Historie und der Weg, sie auf den Startwert zu setzen.
        ''' Nothing fuer eine Gruppe ohne Vergleich.
        '''
        ''' Es sind die Gruppen der RAW-Entwicklung, dazu die vier weiteren Korrekturen (Kanalmixer,
        ''' Verlaufsumsetzung, Tontrennung, Schwellenwert). Wo der Zuruecksetzer der Gruppe ueber sie hinaus
        ''' greift, steht ein eigener Weg: der Knopf der Vignette setzt das ganze Werkzeug Effekte
        ''' zurueck, samt Objektivkorrektur, und der des Rauschens wirft das gespeicherte Entrauschen
        ''' weg, das kein Vergleich zurueckholen koennte. Die Objektivkorrektur selbst hat keinen
        ''' Vergleich: ihr Zuruecksetzer loescht auch die gemerkte Zuordnung des Objektivs, und die
        ''' gehoert nicht zum Bild.</summary>
        Private Function AdjustmentGroupCompareStep(groupKey As String) As (Header As String, Neutralize As Action)
            Select Case groupKey
                Case "light" : Return ("Licht", AddressOf ResetLightInternal)
                Case "color" : Return ("Farbe", AddressOf ResetColorInternal)
                Case "curve"
                    Return ("Tonwertkurve", Sub()
                                                ResetCurvePoints()
                                                RaiseResetButtonStateChanged()
                                                SchedulePreviewForCurrentTarget()
                                            End Sub)
                Case "hsl" : Return ("Farbmischer", AddressOf ResetHslInternal)
                Case "color-grading" : Return ("Farbgradierung", AddressOf ResetColorGradingInternal)
                Case "calibration" : Return ("Kalibrierung", AddressOf ResetCalibrationInternal)
                Case "channel-mixer" : Return ("Kanalmixer", Sub() NeutralizeCorrectionGroup(AddressOf ResetChannelMixerValues))
                Case "gradient-map" : Return ("Verlaufsumsetzung", Sub() NeutralizeCorrectionGroup(AddressOf ResetGradientMapValues))
                Case "posterize" : Return ("Tontrennung", Sub() NeutralizeCorrectionGroup(AddressOf ResetPosterizeValues))
                Case "threshold" : Return ("Schwellenwert", Sub() NeutralizeCorrectionGroup(AddressOf ResetThresholdValues))
                Case "tiltshift" : Return ("Tilt-Shift", Sub() NeutralizeCorrectionGroup(AddressOf ResetTiltShiftValues))
                Case "sharpen" : Return ("Schärfen", AddressOf ResetSharpenGroupInternal)
                Case "soften" : Return ("Weichzeichnen", AddressOf ResetSoftenGroupInternal)
                Case "rauschen" : Return ("Rauschreduzierung", AddressOf ResetNoiseSlidersInternal)
                Case "details" : Return ("Details", AddressOf ResetDetailGroupInternal)
                Case "vignette" : Return ("Vignette", AddressOf ResetVignetteGroupInternal)
                Case "grain" : Return ("Körnung", AddressOf ResetGrainGroupInternal)
                Case Else : Return (Nothing, Nothing)
            End Select
        End Function

        ''' <summary>Die Schluessel der Gruppen, die gerade im Vergleich stehen, durch Komma getrennt.
        ''' Die Knoepfe binden daran ueber den InListConverter, wie die Sichtbarkeit der Gruppen an
        ''' HiddenAdjustmentGroups.</summary>
        Public ReadOnly Property ComparingAdjustmentGroups As String
            Get
                Return _comparingAdjustmentGroups
            End Get
        End Property

        ''' <summary>Gibt es den Gruppenvergleich? Für jedes Reglerziel: das Foto, eine markierte Ebene
        ''' und eine Korrekturebene. Bei den beiden letzten steht das Foto geparkt neben den Reglern,
        ''' das Zurückholen nimmt deshalb den Ladeweg des jeweiligen Ziels
        ''' (<see cref="ApplyGroupCompareRecipe"/>), und vor jedem Wechsel des Ziels kommt ein
        ''' laufender Vergleich zurück (RefreshObjectAdjustMode, RefreshSelectionAdjustMode).</summary>
        Public ReadOnly Property CanCompareAdjustmentGroups As Boolean
            Get
                Return True
            End Get
        End Property

        Private _toggleAdjustmentGroupCompareCommand As ICommand

        ''' Der Parameter ist der Schluessel der Gruppe, wie an ihrem Aufklapper.
        Public ReadOnly Property ToggleAdjustmentGroupCompareCommand As ICommand
            Get
                If _toggleAdjustmentGroupCompareCommand Is Nothing Then
                    _toggleAdjustmentGroupCompareCommand = ReactiveCommand.Create(Of String)(
                        Sub(groupKey) ToggleAdjustmentGroupCompare(groupKey))
                End If
                Return _toggleAdjustmentGroupCompareCommand
            End Get
        End Property

        Private Shared Function NormalizeGroupKey(groupKey As String) As String
            Return If(groupKey, "").Trim().ToLowerInvariant()
        End Function

        ''' <summary>Steht die Gruppe im Vergleich?</summary>
        Public Function IsAdjustmentGroupComparing(groupKey As String) As Boolean
            Dim key = NormalizeGroupKey(groupKey)
            Dim compare As AdjustmentGroupCompare = Nothing
            Return CanCompareAdjustmentGroups AndAlso _groupCompares.TryGetValue(key, compare) AndAlso
                   GroupCompareStillNeutral(compare, BuildAdjustmentsFromFields())
        End Function

        ''' <summary>Schaltet den Vergleich einer Gruppe um. Steht die Gruppe schon ganz auf dem
        ''' Startwert, gibt es nichts zu vergleichen, und es geschieht nichts.</summary>
        ''' <returns>Ob sich etwas getan hat.</returns>
        Public Function ToggleAdjustmentGroupCompare(groupKey As String) As Boolean
            If Not CanCompareAdjustmentGroups Then Return False
            Dim key = NormalizeGroupKey(groupKey)
            Dim groupStep = AdjustmentGroupCompareStep(key)
            If groupStep.Neutralize Is Nothing Then Return False
            Dim label = CombineHistoryLabel("Vergleich", groupStep.Header)

            Dim existing As AdjustmentGroupCompare = Nothing
            If _groupCompares.TryGetValue(key, existing) Then
                _groupCompares.Remove(key)
                Dim target = BuildAdjustmentsFromFields()
                If GroupCompareStillNeutral(existing, target) Then
                    CopyGroupCompareValues(existing.Saved, target)
                    ApplyGroupCompareRecipe(target, label)
                    RaiseAdjustmentGroupCompareChanged()
                    Return True
                End If
                ' Inzwischen von anderer Seite geaendert: der gemerkte Stand ist verworfen, und
                ' dieser Klick beginnt einen neuen Vergleich mit dem, was jetzt dasteht.
            End If

            ' Ein Kurvenkanal im Einzelvergleich kommt erst zurueck: der Zuruecksetzer der Kurve
            ' verwirft seinen gemerkten Stand, und der Gruppenvergleich soll die GANZE Kurve
            ' zurueckholen, nicht die halbe.
            If key = "curve" Then RestoreAllCurveCompares()

            Dim before = BuildAdjustmentsFromFields()
            Dim entry = PrepareUndo(label)
            _suppressUndoCapture = True
            Try
                groupStep.Neutralize()
            Finally
                _suppressUndoCapture = False
            End Try
            Dim after = BuildAdjustmentsFromFields()
            Dim compare As New AdjustmentGroupCompare()
            For Each p In GroupCompareProperties()
                Dim saved = p.GetValue(before)
                Dim neutral = p.GetValue(after)
                If Not Object.Equals(saved, neutral) Then
                    compare.Saved(p.Name) = saved
                    compare.Neutral(p.Name) = neutral
                End If
            Next
            If compare.Saved.Count = 0 Then
                RaiseAdjustmentGroupCompareChanged()
                Return False
            End If
            CommitPreparedUndo(entry)
            _groupCompares(key) = compare
            RaiseAdjustmentGroupCompareChanged()
            Return True
        End Function

        ''' <summary>Holt die gemerkten Werte aller Gruppen zurueck. Laeuft vor jedem Verlassen des
        ''' Bildes, wie RestoreAllCurveCompares und RoundSlider.RestoreAllCompared: so sieht schon die
        ''' Speichern-Rueckfrage die eingestellten Werte. Ebenso vor jedem Wechsel des Reglerziels:
        ''' die gemerkten Werte gehören dem Ziel, an dem sie ausgeblendet wurden.</summary>
        Private Sub RestoreAllAdjustmentGroupCompares()
            If _groupCompares.Count = 0 Then Return
            Dim entries = _groupCompares.Values.ToArray()
            _groupCompares.Clear()
            Dim target = BuildAdjustmentsFromFields()
            Dim restored = False
            For Each compare In entries
                If Not GroupCompareStillNeutral(compare, target) Then Continue For
                CopyGroupCompareValues(compare.Saved, target)
                restored = True
            Next
            If restored Then ApplyGroupCompareRecipe(target, LocalizationService.T("Vergleich"))
            RaiseAdjustmentGroupCompareChanged()
        End Sub

        ''' <summary>Vergisst alle Gruppenvergleiche, ohne etwas zurueckzuholen: fuer ein anderes
        ''' Dokument. Ein gemerkter Stand gehoert zum Bild, auf dem er entstand.</summary>
        Private Sub ForgetAdjustmentGroupCompares()
            If _groupCompares.Count = 0 Then Return
            _groupCompares.Clear()
            RaiseAdjustmentGroupCompareChanged()
        End Sub

        ''' <summary>Meldet, welche Gruppen im Vergleich stehen, und beendet dabei die, deren Werte
        ''' sich von anderer Seite geaendert haben. Haengt an RaiseResetButtonStateChanged, das nach
        ''' jeder Reglerbewegung, jedem Zuruecksetzen und jedem Rueckgaengig laeuft.
        '''
        ''' SOFORT BEENDEN und nicht erst beim naechsten Klick: wer im Vergleich einen Regler bewegt
        ''' und ihn danach von Hand auf den Startwert zurueckzieht, stuende sonst wieder im Vergleich,
        ''' und der naechste Klick holte den alten Stand - dieselbe Falle wie an der Kurve.
        '''
        ''' Nicht waehrend das Reglerziel wechselt: dann stehen in den Feldern fuer einen Augenblick
        ''' die Werte eines Objekts, und ein Abgleich dort beendete einen Vergleich grundlos.</summary>
        Private Sub RaiseAdjustmentGroupCompareChanged()
            Dim list = ""
            If _groupCompares.Count > 0 AndAlso CanCompareAdjustmentGroups AndAlso
               Not _objectAdjustSwapInProgress AndAlso Not _selectionAdjustSwapInProgress Then
                Dim current = BuildAdjustmentsFromFields()
                For Each key In _groupCompares.Keys.ToArray()
                    If Not GroupCompareStillNeutral(_groupCompares(key), current) Then _groupCompares.Remove(key)
                Next
                list = String.Join(",", _groupCompares.Keys.OrderBy(Function(k) k, StringComparer.Ordinal))
            End If
            If String.Equals(list, _comparingAdjustmentGroups, StringComparison.Ordinal) Then Return
            _comparingAdjustmentGroups = list
            Me.RaisePropertyChanged(NameOf(ComparingAdjustmentGroups))
        End Sub

        Private Shared Function GroupCompareStillNeutral(compare As AdjustmentGroupCompare, current As ImageAdjustments) As Boolean
            For Each p In GroupCompareProperties()
                Dim neutral As Object = Nothing
                If compare.Neutral.TryGetValue(p.Name, neutral) AndAlso Not Object.Equals(p.GetValue(current), neutral) Then Return False
            Next
            Return True
        End Function

        Private Shared Sub CopyGroupCompareValues(values As Dictionary(Of String, Object), target As ImageAdjustments)
            For Each p In GroupCompareProperties()
                Dim value As Object = Nothing
                If values.TryGetValue(p.Name, value) Then p.SetValue(target, value)
            Next
        End Sub

        ''' <summary>Schreibt das Rezept mit den zurueckgeholten Werten in die Regler, als EIN Schritt
        ''' in der Historie. Derselbe Weg wie Rueckgaengig: er kennt Lichterrettung und Kurven, die
        ''' beim blossen Setzen der Felder nicht mitkaemen, und baut das Arbeitsbild neu auf, wenn sich
        ''' am Decode etwas aendert.
        '''
        ''' <para>BEI EINER EBENE ODER KORREKTUREBENE tragen die Felder deren Werte, das Foto steht
        ''' geparkt daneben. ApplyAdjustments würde dann die Werte des Ziels mit denen des Fotos
        ''' vertauschen; der Schalter des laufenden Zielwechsels hält das fern, wie beim Laden der
        ''' Ebenenwerte nach Rückgängig. Danach gehen die Werte in die Ebene
        ''' (SchedulePreviewForCurrentTarget).</para></summary>
        Private Sub ApplyGroupCompareRecipe(target As ImageAdjustments, label As String)
            PushUndo(label)
            _suppressUndoCapture = True
            Dim objectTarget = IsObjectAdjustModeActive()
            Dim selectionTarget = IsSelectionAdjustModeActive()
            Dim keepIndex = _selectedAnnotationIndex
            Try
                If objectTarget Then _objectAdjustSwapInProgress = True
                If selectionTarget Then _selectionAdjustSwapInProgress = True
                ApplyAdjustmentsKeepingSelection(target, keepIndex)
            Finally
                If objectTarget Then _objectAdjustSwapInProgress = False
                If selectionTarget Then _selectionAdjustSwapInProgress = False
                _suppressUndoCapture = False
            End Try
            If objectTarget OrElse selectionTarget Then SchedulePreviewForCurrentTarget()
        End Sub

        ''' <summary>Die Zuruecksetzer der vier weiteren Korrekturen setzen nur ihre Werte; Meldung und Vorschau
        ''' haengt ihr Zuruecksetzer selbst an (ResetCorrectionGroup), samt eigenem Historienschritt,
        ''' den der Vergleich nicht braucht.</summary>
        Private Sub NeutralizeCorrectionGroup(reset As Action)
            reset()
            RaiseResetButtonStateChanged()
            SchedulePreviewForCurrentTarget()
        End Sub

        ''' <summary>Nur die Vignette. Ihr Knopf im Gruppenkopf setzt das ganze Werkzeug Effekte
        ''' zurueck (ResetEffectsInternal), samt Objektivkorrektur und Koernung; der Vergleich der
        ''' Vignette blendet nur die Vignette aus.</summary>
        Private Sub ResetVignetteGroupInternal()
            _vignette = 0
            _vignetteTransition = 55
            _vignetteRoundness = 0
            _vignetteFeather = 70
            _vignetteCenterX = 50
            _vignetteCenterY = 50
            _vignetteStyle = VignetteStyle.ColorPriority
            For Each n In {NameOf(Vignette), NameOf(VignetteTransition), NameOf(VignetteRoundness),
                           NameOf(VignetteFeather), NameOf(VignetteCenterX), NameOf(VignetteCenterY),
                           NameOf(VignetteStyleLabel)}
                Me.RaisePropertyChanged(n)
            Next
            RaiseResetButtonStateChanged()
            SchedulePreviewForCurrentTarget()
        End Sub

    End Class

End Namespace
