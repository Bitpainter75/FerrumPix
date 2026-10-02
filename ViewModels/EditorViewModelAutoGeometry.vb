Imports System
Imports FerrumPix.Services
Imports ReactiveUI
Imports SkiaSharp

Namespace ViewModels

    ''' <summary>Die beiden automatischen Geradesteller: DREHUNG AUF DEN HORIZONT (im Werkzeug
    ''' "Transformieren", Gruppe "Drehen") und PERSPEKTIVKORREKTUR (im Werkzeug "Verzerren", Gruppe
    ''' "Perspektive").
    '''
    ''' Sie liegen zusammen, weil sie dieselbe Messung teilen
    ''' (<see cref="ImageProcessor.AnalyzeAutoGeometry"/>), aber sie bleiben ZWEI Knoepfe an zwei
    ''' Stellen - jeder dort, wo die Werte sitzen, die er setzt. Das ist keine Geschmacksfrage: was
    ''' der Knopf schreibt, muss danach von Hand nachziehbar sein, und die Anfasser dafuer gibt es
    ''' nur im jeweiligen Werkzeug. Ein gemeinsamer Knopf haette ausserdem an einem Bild richtig und
    ''' am anderen falsch gelegen, ohne dass sich die falsche Haelfte allein zuruecknehmen liesse.
    '''
    ''' Beide setzen ABSOLUTE Werte: zweimal gedrueckt ergibt dasselbe wie einmal.</summary>
    Partial Public Class EditorViewModel

        ''' <summary>Dreht das Bild so, dass seine Kanten waagerecht und senkrecht stehen.</summary>
        Public Async Function ApplyAutoStraightenAsync() As Task
            Dim measured = Await MeasureAutoGeometryAsync()
            If measured Is Nothing Then Return

            If Not measured.HasHorizon Then
                StatusText = LocalizationService.T("Keine gerade Kante gefunden, an der sich ausrichten ließe")
                Return
            End If

            If Math.Abs(measured.StraightenDegrees - _straightenDegrees) < 0.05 Then
                StatusText = LocalizationService.T("Das Bild steht bereits gerade")
                Return
            End If

            PushUndo(LocalizationService.T("Automatisch ausgerichtet"))
            _suppressUndoCapture = True
            Try
                ' UEBER DEN BILDWEG, nicht ueber die Eigenschaft: mit markiertem Objekt dreht die
                ' Eigenschaft das Objekt, gemessen wurde aber das Bild.
                SetImageStraightenDegrees(measured.StraightenDegrees)
            Finally
                _suppressUndoCapture = False
            End Try

            StatusText = String.Format(LocalizationService.T("Ausgerichtet um {0} Grad"),
                                       measured.StraightenDegrees.ToString("F1"))
        End Function

        ''' <summary>Zieht stuerzende Linien gerade: setzt die beiden Perspektivregler auf die Werte,
        ''' die zu den gemessenen Fluchtpunkten gehoeren.</summary>
        Public Async Function ApplyAutoPerspectiveAsync() As Task
            Dim measured = Await MeasureAutoGeometryAsync()
            If measured Is Nothing Then Return

            If Not measured.HasPerspective Then
                StatusText = LocalizationService.T("Keine stürzenden Linien gefunden")
                Return
            End If

            Dim vertical As Double = 0, horizontal As Double = 0
            ProjectAutoPerspectiveOntoStage(measured, vertical, horizontal)
            If Math.Abs(vertical) < 0.5 AndAlso Math.Abs(horizontal) < 0.5 Then
                StatusText = LocalizationService.T("Keine stürzenden Linien gefunden")
                Return
            End If

            If Math.Abs(vertical - _perspectiveVertical) < 0.5 AndAlso
               Math.Abs(horizontal - _perspectiveHorizontal) < 0.5 Then
                StatusText = LocalizationService.T("Die Perspektive steht bereits richtig")
                Return
            End If

            PushUndo(LocalizationService.T("Perspektive automatisch korrigiert"))
            ' EIN Undo-Schritt fuer beide Regler: PushUndo hat den Stand gesichert, die Setter
            ' duerfen nicht jeder noch einen eigenen anlegen.
            _suppressUndoCapture = True
            Try
                PerspectiveVertical = vertical
                PerspectiveHorizontal = horizontal
            Finally
                _suppressUndoCapture = False
            End Try

            StatusText = LocalizationService.T("Perspektive korrigiert")
        End Function

        ''' <summary>Rechnet die gemessenen Fluchtpunkte auf die Achsen um, die die VERZERRUNGSSTUFE
        ''' sieht, und macht daraus die beiden Reglerwerte.
        '''
        ''' <para>GEMESSEN WIRD DAS UNBEARBEITETE BILD, gekippt wird aber erst hinter Spiegeln,
        ''' Vierteldrehung und Begradigung (siehe die Kette in <c>ImageProcessor</c>). Eine
        ''' Vierteldrehung vertauscht dabei die beiden Achsen, ein Spiegel kehrt eine Richtung um.
        ''' Wer die Reglerwerte direkt aus der Messung nimmt, korrigiert bei einem hochkant gedrehten
        ''' Foto die falsche Achse - und zwar lautlos.</para>
        '''
        ''' <para>Statt fuer jede Lage die Vorzeichen einzeln zu ueberlegen, wandert der FLUCHTPUNKT
        ''' durch dieselben Schritte wie das Bild. Danach sagt seine Lage von selbst, welcher Regler
        ''' gemeint ist: die Achse, die jetzt senkrecht steht, fuellt "Senkrecht".</para></summary>
        Private Sub ProjectAutoPerspectiveOntoStage(measured As ImageProcessor.AutoGeometryResult,
                                                    ByRef vertical As Double, ByRef horizontal As Double)
            vertical = 0
            horizontal = 0
            Dim width = CDbl(measured.MeasuredWidth), height = CDbl(measured.MeasuredHeight)
            If width <= 0 OrElse height <= 0 Then Return

            Dim quarter = ImageGeometryMapper.NormalizeQuarterTurn(AppliedRotationDegrees)
            Dim swapsAxes = (quarter = 90 OrElse quarter = 270)
            ' Die Masse der Stufe: nach einer Viertel- oder Dreivierteldrehung steht das Bild quer.
            Dim stageWidth = If(swapsAxes, height, width)
            Dim stageHeight = If(swapsAxes, width, height)

            If measured.HasVerticalVanishing Then
                Dim p = MapAutoGeometryPointOntoStage(measured.VerticalVanishingX, measured.VerticalVanishingY,
                                                      width, height, quarter)
                ' Senkrechte Kanten bleiben senkrecht, solange nicht um eine Vierteldrehung gedreht
                ' wurde - dann sind sie waagerecht geworden und fuellen den anderen Regler.
                If swapsAxes Then
                    horizontal = ImageProcessor.PerspectiveSliderFromVanishing(stageWidth, p.X)
                Else
                    vertical = ImageProcessor.PerspectiveSliderFromVanishing(stageHeight, p.Y)
                End If
            End If

            If measured.HasHorizontalVanishing Then
                Dim p = MapAutoGeometryPointOntoStage(measured.HorizontalVanishingX, measured.HorizontalVanishingY,
                                                      width, height, quarter)
                If swapsAxes Then
                    vertical = ImageProcessor.PerspectiveSliderFromVanishing(stageHeight, p.Y)
                Else
                    horizontal = ImageProcessor.PerspectiveSliderFromVanishing(stageWidth, p.X)
                End If
            End If
        End Sub

        ''' <summary>Ein Punkt des gemessenen Bildes an seiner Stelle in der Verzerrungsstufe: erst
        ''' spiegeln, dann die Vierteldrehung - dieselbe Reihenfolge wie im Punktweg des
        ''' <c>ImageProcessor</c>.
        '''
        ''' <para>Die BEGRADIGUNG bleibt bewusst aussen vor. Sie dreht den Fluchtpunkt um denselben
        ''' kleinen Winkel um die Bildmitte; auf die eine Koordinate, die hier eingeht, wirkt das
        ''' zweiter Ordnung, und der Fluchtpunkt liegt ohnehin weit ausserhalb des Bildes. Beide
        ''' Automatiken nacheinander gedrueckt ergeben deshalb dasselbe wie einzeln.</para></summary>
        Private Function MapAutoGeometryPointOntoStage(x As Double, y As Double,
                                                       width As Double, height As Double,
                                                       quarter As Integer) As SKPoint
            Dim px = x, py = y
            If AppliedFlipHorizontal Then px = width - px
            If AppliedFlipVertical Then py = height - py

            Select Case quarter
                Case 90
                    Dim turned = height - py
                    py = px
                    px = turned
                Case 180
                    px = width - px
                    py = height - py
                Case 270
                    Dim turned = py
                    py = width - px
                    px = turned
            End Select
            Return New SKPoint(CSng(px), CSng(py))
        End Function

        ' ── Ausrichten mit einer gezogenen Linie ──────────────────────────────────
        '
        ' Issue #80: man legt eine Linie auf eine Kante, die waagerecht oder senkrecht sein soll -
        ' Horizont, Hauskante, Fensterreihe -, und das Bild dreht sich so, dass sie es ist. Der
        ' Regler allein war dafuer zu grob, und die Automatik findet nicht jede Kante.

        Private _isStraightenLineActive As Boolean

        ''' <summary>Der naechste Zug auf dem Bild legt eine Linie, statt etwas anderes zu tun. Der
        ''' Knopf im Panel "Drehen" schaltet es ein; nach einer Linie, einem Werkzeugwechsel oder
        ''' einem zweiten Klick auf den Knopf ist es wieder aus.</summary>
        Public Property IsStraightenLineActive As Boolean
            Get
                Return _isStraightenLineActive
            End Get
            Set(value As Boolean)
                Me.RaiseAndSetIfChanged(_isStraightenLineActive, value)
            End Set
        End Property

        ''' <summary>Dreht das Bild so, dass die gezogene Linie waagerecht steht - oder senkrecht,
        ''' wenn sie steiler als 45 Grad liegt. <paramref name="dx"/> und <paramref name="dy"/> sind
        ''' die Strecke von Anfang zu Ende in BILDSCHIRMPUNKTEN (y nach unten); fuer den Winkel zaehlt
        ''' nur ihr Verhaeltnis, eine Umrechnung in Bildpunkte braucht es also nicht.
        '''
        ''' DIE LINIE LIEGT AUF DEM ANGEZEIGTEN BILD, also hinter der bisherigen Begradigung. Die
        ''' Kette dreht im Schritt "transform" erst um Viertel und spiegelt, DANN begradigt sie
        ''' (ApplyGeometryTransforms, ApplyStraighten) - eine weitere Drehung um den Schraegstand
        ''' addiert sich also schlicht, auch bei gespiegeltem Bild. Positiv dreht im Uhrzeigersinn;
        ''' eine Linie, die nach rechts abfaellt (positiver Winkel), braucht deshalb eine Drehung
        ''' gegen den Uhrzeigersinn. Gemessen an der Pruefung "Ausrichten mit Linie".</summary>
        Public Sub ApplyStraightenFromLine(dx As Double, dy As Double)
            IsStraightenLineActive = False
            ' Ein Klick ohne Zug ist keine Linie.
            If Math.Sqrt(dx * dx + dy * dy) < 8 Then Return
            Dim angle = Math.Atan2(dy, dx) * 180.0 / Math.PI
            ' Zur naechsten Achse: flacher als 45 Grad wird waagerecht, steiler senkrecht.
            Dim tilt = angle - Math.Round(angle / 90.0) * 90.0
            If Math.Abs(tilt) < 0.05 Then
                StatusText = LocalizationService.T("Das Bild steht bereits gerade")
                Return
            End If
            Dim target = _straightenDegrees - tilt
            ' Im Bereich -180..180 halten, wie der Regler ihn kennt.
            target = ((target + 180.0) Mod 360.0 + 360.0) Mod 360.0 - 180.0
            target = Math.Round(target, 2)

            PushUndo(LocalizationService.T("Mit Linie ausgerichtet"))
            _suppressUndoCapture = True
            Try
                ' UEBER DEN BILDWEG, wie die Automatik: mit markiertem Objekt drehte die Eigenschaft
                ' das Objekt, gemeint ist aber das Bild.
                SetImageStraightenDegrees(target)
            Finally
                _suppressUndoCapture = False
            End Try
            StatusText = String.Format(LocalizationService.T("Ausgerichtet um {0} Grad"), target.ToString("F1"))
        End Sub

        ' Laeuft gerade eine Messung? Zaehlt in IsBusy, damit die Beschaeftigt-Anzeige kommt.
        Private _autoGeometryRunning As Boolean

        ''' <summary>Misst das Bild, oder sagt in der Fusszeile, warum es nicht ging.
        '''
        ''' IM HINTERGRUND, mit der Beschaeftigt-Anzeige ("Bild wird vermessen"). Vorher lief die
        ''' Messung auf dem UI-Faden: die Oberflaeche stand still, und man wusste nicht, ob gerechnet
        ''' wird oder ob der Klick nicht ankam (Issue #80). Gemessen wird an einer KOPIE der
        ''' Vorschauquelle: das Original gibt der Editor beim naechsten Render frei. Hat inzwischen
        ''' ein anderes Bild den Platz eingenommen, wird das Ergebnis verworfen; ein zweiter Klick
        ''' waehrend der Messung tut nichts.</summary>
        Private Async Function MeasureAutoGeometryAsync() As Task(Of ImageProcessor.AutoGeometryResult)
            If _autoGeometryRunning Then Return Nothing
            Dim source = GetPreviewSource()
            If source Is Nothing Then
                StatusText = LocalizationService.T("Das Bild konnte nicht analysiert werden")
                Return Nothing
            End If
            Dim imagePath = _currentImagePath
            Dim copy = source.Copy()
            Dim measured As ImageProcessor.AutoGeometryResult = Nothing
            _autoGeometryRunning = True
            SetBusyReason(LocalizationService.T("Bild wird vermessen"))
            RefreshBusyState()
            Try
                measured = Await Task.Run(Function() If(copy Is Nothing, Nothing, ImageProcessor.AnalyzeAutoGeometry(copy)))
            Finally
                copy?.Dispose()
                _autoGeometryRunning = False
                RefreshBusyState()
            End Try
            If Not String.Equals(imagePath, _currentImagePath, StringComparison.Ordinal) Then Return Nothing
            If measured Is Nothing OrElse Not measured.HasMeasurement Then
                StatusText = LocalizationService.T("Das Bild konnte nicht analysiert werden")
                Return Nothing
            End If
            Return measured
        End Function

    End Class

End Namespace
