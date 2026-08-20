Imports System
Imports System.Collections.Generic
Imports System.Collections.ObjectModel
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Input
Imports Avalonia.Media.Imaging
Imports Avalonia.Threading
Imports FerrumPix.Controls
Imports SkiaSharp
Imports ReactiveUI
Imports FerrumPix.Services
Imports FerrumPix.Models

Namespace ViewModels

    ''' <summary>Retusche und Stempel: das Setzen der Punkte, die Live-Vorschau waehrend des Zuges
    ''' mit ihren beiden Puffern und der Abschluss in das Arbeitsbild.
    '''
    ''' Sechste Scheibe der Dateiaufteilung (2026-08-04), Regeln wie in
    ''' <c>ViewModels/EditorViewModelMask.vb</c>. Warum die Live-Puffer eine Doppelrolle haben und
    ''' woran ihre Gueltigkeit haengt, steht in <c>Audits/RENDERPIPELINE.md</c>.</summary>
    Partial Public Class EditorViewModel

        Private Function TransformWorkingPixelToDisplayPixel(x As Double, y As Double,
                                                             baseWidth As Integer, baseHeight As Integer,
                                                             displayWidth As Integer, displayHeight As Integer) As (X As Double, Y As Double)
            ' VOLLSTÄNDIGE Vorwärtsabbildung (Gegenstück zu
            ' DisplayPercentToWorkingImagePercent): mit der Mapper-Kurzform stand das Overlay
            ' eines Retusche-Punkts nach angewendetem Crop/Resize/Canvas neben der gebackenen
            ' Stelle. Vom Crop entfernte Punkte wandern weit nach außen - das Overlay zeigt sie
            ' dann schlicht nicht.
            Dim output As SKPoint
            If ImageProcessor.TrySourcePointToGeometryOutput(x, y, baseWidth, baseHeight,
                                                             BuildAppliedGeometryAdjustments(), output) Then
                Return (output.X, output.Y)
            End If
            Return (-100000.0, -100000.0)
        End Function

        Public Sub AddRetouchSpot(xPercent As Double, yPercent As Double, Optional captureUndo As Boolean = True)
            If Not CanUsePixelTools Then Return
            ' Ist eine Bild-Ebene markiert, geht der Zug in DEREN Bild - dieselbe Regel wie beim
            ' Pinsel. Entschieden wird das EINMAL, beim Zugbeginn (captureUndo): ein Punkt mitten im
            ' Zug darf nicht plötzlich ins Foto rutschen, nur weil sich unterwegs etwas an der
            ' Markierung ändert. Der Weg selbst steht in EditorViewModelObjectRetouch.vb.
            If TryAddObjectRetouchSpot(xPercent, yPercent, captureUndo) Then Return
            ' Klick liegt im Anzeigebild (nach Crop/Drehung/Resize/Canvas) - vollständig ins
            ' Arbeitsbild zurückrechnen. NaN = außerhalb des Bildinhalts (Canvas-Rand): dort gibt
            ' es keinen Source-Pixel, der Punkt wird verworfen.
            Dim wip = DisplayPercentToWorkingImagePercent(xPercent, yPercent)
            If Double.IsNaN(wip.X) OrElse Double.IsNaN(wip.Y) Then Return
            xPercent = wip.X
            yPercent = wip.Y
            ' Der Stempel braucht eine Quelle. Ohne sie würde er stillschweigend zur Retusche -
            ' der Nutzer soll stattdessen erst Alt+Klick machen (siehe RetouchHintText).
            If IsCloneMode AndAlso Not HasCloneSource Then Return
            If captureUndo Then
                PushUndo()
                _retouchStrokeActive = True
                RaiseSaveAvailabilityChanged()
                _retouchStrokeStartSpotIndex = _retouchSpots.Count
                _activeRetouchStrokeId = _nextRetouchStrokeId
                _nextRetouchStrokeId += 1
                _previewTimer.Stop()
                If _clearRetouchLivePatchAfterPreview Then
                    ' BEFUND ("Reparatur 1 bei Reparatur 2 wieder weg"): Der Commit-Render
                    ' des VORHERIGEN Zugs laeuft noch. Ihn abzubrechen (InvalidatePreviewWork)
                    ' hiesse, dass Zug 1 NIE in die Szene gebacken wird; die Live-Bruecke samt
                    ' Rect zu loeschen, liesse Zug 1 bis dahin verschwinden. Beides stehen
                    ' lassen - das Patch-Rect waechst mit dem neuen Zug einfach weiter.
                Else
                    _retouchLivePatchRect = SKRectI.Empty
                    ClearRetouchLivePatch()
                    InvalidatePreviewWork()
                End If
                ' STEMPEL-LIVE: vorgewaermte Puffer NICHT blind wegwerfen - sonst
                ' beginnt jeder Zug wieder ohne Live-Ansicht. Der BaseKey (enthaelt Spots UND alle
                ' Anpassungen) entscheidet, ob der Puffer noch den committeten Stand zeigt.
                If Not RetouchLiveBuffersMatchCommittedState() Then
                    DiagnosticLogService.LogAlways("Editor.RetouchBuffers",
                        $"discardAtStrokeStart hadBuffers={_retouchLiveBitmap IsNot Nothing} hadKey={_retouchBuffersKey IsNot Nothing}")
                    DisposeRetouchLiveBuffers()
                End If
            End If

            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            ' Der Mittelpunkt darf bis zu EINEN RADIUS ausserhalb liegen: wer am Bildrand ansetzt,
            ' meint den Teil des Kreises, der ueber dem Bild liegt. Frueher wurde hart auf den Rand
            ' geklemmt - dann wirkte dort ein VOLLER Kreis, also mehr als der Cursorring anzeigte.
            ' Weiter als einen Radius hinaus beruehrt der Kreis das Bild ohnehin nicht mehr; das
            ' Zeichnen selbst klemmt zusaetzlich auf die Bitmapgrenzen (DrawRetouchSpot).
            Dim reach = Math.Max(1, CInt(Math.Ceiling(_retouchRadius)))
            Dim targetX = Math.Max(-reach, Math.Min(baseWidth + reach, PercentXToPixels(xPercent)))
            Dim targetY = Math.Max(-reach, Math.Min(baseHeight + reach, PercentYToPixels(yPercent)))

            Dim spot = New RetouchSpot With {
                .XPixels = CSng(targetX),
                .YPixels = CSng(targetY),
                .RadiusPixels = CSng(_retouchRadius),
                .StrengthPercent = CSng(_brushHardness),
                .OpacityPercent = CSng(_brushOpacity),
                .FlowPercent = CSng(_brushFlow),
                .Mode = If(_isRepairMode AndAlso Not _isCloneMode, "Heal", "Blur"),
                .StrokeId = If(_isRepairMode AndAlso Not _isCloneMode, _activeRetouchStrokeId, 0)
            }

            If IsCloneMode AndAlso HasCloneSource Then
                ' Der Versatz entsteht beim ersten Punkt nach dem Setzen der Quelle und bleibt dann
                ' stehen - so wandert beim Ziehen ein zusammenhängender Ausschnitt mit.
                If Not _hasCloneOffset Then
                    ' Die Klonquelle liegt (wie der Klick) im Anzeigebild - für den Bake vollständig
                    ' in die Arbeitsbild-Koordinaten umrechnen, damit der Versatz stimmt. NaN =
                    ' Quelle außerhalb des Bildinhalts, damit ist kein gültiger Versatz bestimmbar.
                    Dim cloneWip = DisplayPercentToWorkingImagePercent(_cloneSourceXPercent, _cloneSourceYPercent)
                    If Double.IsNaN(cloneWip.X) OrElse Double.IsNaN(cloneWip.Y) Then Return
                    _cloneOffsetXPixels = targetX - PercentXToPixels(cloneWip.X)
                    _cloneOffsetYPixels = targetY - PercentYToPixels(cloneWip.Y)
                    _hasCloneOffset = True
                End If

                Dim sourceX = targetX - _cloneOffsetXPixels
                Dim sourceY = targetY - _cloneOffsetYPixels
                ' Wandert die Quelle beim Ziehen aus dem Bild, bleibt der Punkt ohne Quelle und fällt
                ' auf den Ringmittelwert zurück, statt an der Bildkante Pixel zu wiederholen.
                If sourceX >= 0 AndAlso sourceY >= 0 AndAlso sourceX <= baseWidth AndAlso sourceY <= baseHeight Then
                    spot.SourceXPixels = CSng(sourceX)
                    spot.SourceYPixels = CSng(sourceY)
                End If
            End If

            _retouchSpots.Add(spot)
            ' Retusche hat das Dokument bisher nicht als geändert markiert: UpdatePreview setzt
            ' _hasChanges nicht, und AddRetouchSpot lief nie über SchedulePreviewUpdate. Wer nur
            ' retuschierte und den Editor verließ, wurde nicht gefragt und verlor die Arbeit.
            _hasChanges = True

            ' Nur der Zugbeginn schreibt in die Historie - die Zwischenpunkte eines Zuges würden sonst
            ' die 30 Einträge fluten. Beim Ziehen rendert der Stempel/Retusche live gedrosselt:
            ' schnell genug für sichtbares Zeichnen, aber ohne für jeden Pointer-Punkt einen kompletten
            ' Pipeline-Render zu starten.
            If captureUndo Then AddHistoryEntry(If(IsCloneMode, "Stempeln", If(IsRepairMode, "Reparatur", "Verwischen")))
            UpdateRetouchLivePreview(spot, captureUndo)
        End Sub

        ''' <summary>Fuegt die Strecke zwischen zwei Display-Punkten mit einem vom Pinselradius
        ''' abgeleiteten Abstand ein. Pointer-Ereignisse koennen auf grossen Bildern dutzende
        ''' Bildpixel auseinanderliegen; einzelne Kreise liessen dann Luecken in der Heal-Maske.</summary>
        Public Sub AddRetouchSegment(fromXPercent As Double, fromYPercent As Double,
                                     toXPercent As Double, toYPercent As Double)
            If Not CanUsePixelTools OrElse Not _retouchStrokeActive Then Return
            Dim displayWidth = Math.Max(1, DisplayImageWidthPixels)
            Dim displayHeight = Math.Max(1, DisplayImageHeightPixels)
            Dim dxPixels = (toXPercent - fromXPercent) / 100.0 * displayWidth
            Dim dyPixels = (toYPercent - fromYPercent) / 100.0 * displayHeight
            Dim distance = Math.Sqrt(dxPixels * dxPixels + dyPixels * dyPixels)
            If distance <= 0.001 Then Return

            Dim spacingPixels = Math.Max(1.0, _retouchRadius * 0.35)
            Dim steps = Math.Max(1, CInt(Math.Ceiling(distance / spacingPixels)))
            For stepIndex = 1 To steps
                Dim t = stepIndex / CDbl(steps)
                AddRetouchSpot(fromXPercent + (toXPercent - fromXPercent) * t,
                               fromYPercent + (toYPercent - fromYPercent) * t,
                               captureUndo:=False)
            Next
        End Sub

        Private Sub UpdateRetouchLivePreview(spot As RetouchSpot, forcePublish As Boolean)
            If spot Is Nothing Then Return
            If Not _retouchStrokeActive Then
                ScheduleRetouchPreviewUpdate(forcePublish)
                Return
            End If

            If String.Equals(spot.Mode, "Heal", StringComparison.OrdinalIgnoreCase) AndAlso IsRepairMode Then
                If Not EnsureRetouchMaskPreviewSize() Then
                    ScheduleRetouchPreviewUpdate(forcePublish)
                    Return
                End If
                Dim maskSpot = TransformRetouchSpotToDisplayGeometry(spot)
                ExpandRetouchMaskPatchRect(maskSpot)
                PublishRetouchMaskPreview(forcePublish, maskSpot)
                Return
            End If

            ' BEFUND: Die Schwelle gilt im PREVIEW-Raum - die Kosten der Live-Anwendung
            ' haengen am preview-skalierten Radius, nicht an den Basis-Pixeln. Der alte Vergleich
            ' in Basis-Pixeln schickte grosse Pinsel immer in die Masken-Vorschau ("keine
            ' Live-Ansicht"), obwohl der effektive Radius harmlos war.
            ' STEMPEL IMMER LIVE ("Live nur beim ersten Zug"): Klon-Spots sind ein
            ' billiger Shader-Draw (DrawCloneStamp) - die Schwelle schuetzt nur vor dem teuren
            ' Box-Blur des VERWISCHENS.
            If Not spot.HasCloneSource AndAlso spot.RadiusPixels * RetouchPreviewRadiusScale() > 240 Then
                ' Grosser Radius: Live-Anwendung pro Mauspunkt waere zu teuer - aber GAR KEIN Feedback
                ' ("es passiert nichts") ist schlimmer. Die orangene Masken-Vorschau zeigt den
                ' bearbeiteten Bereich; der Commit backt das Ergebnis.
                If EnsureRetouchMaskPreviewSize() Then
                    Dim maskSpot = TransformRetouchSpotToDisplayGeometry(spot)
                    ExpandRetouchMaskPatchRect(maskSpot)
                    PublishRetouchMaskPreview(forcePublish, maskSpot)
                End If
                Return
            End If

            If _retouchLiveBitmap Is Nothing OrElse _retouchLiveSampleBitmap Is Nothing Then
                ' STUFE 5: Puffer-Aufbau ASYNCHRON - der synchrone Aufbau rendert bis zu zwei volle
                ' Pipelines (~1-1,5 s je) und fror die UI beim ersten Punkt ein ("CPU hoch, nichts
                ' passiert"). Bis die Puffer stehen, zeigt die Masken-Vorschau den Strich; danach
                ' zieht der Init-Abschluss alle aufgelaufenen Zug-Punkte nach.
                BeginRetouchLiveBuffersAsync()
                If EnsureRetouchMaskPreviewSize() Then
                    Dim maskSpot = TransformRetouchSpotToDisplayGeometry(spot)
                    ExpandRetouchMaskPatchRect(maskSpot)
                    PublishRetouchMaskPreview(forcePublish, maskSpot)
                End If
                Return
            End If

            ResetRetouchLivePatchForBitmap(_retouchLiveBitmap)
            Dim liveSpot = TransformRetouchSpotToLiveGeometry(spot)
            Dim liveSize = GetRetouchLiveGeometrySize()
            ImageProcessor.ApplyRetouchSpotInPlace(_retouchLiveBitmap, _retouchLiveSampleBitmap, liveSpot,
                                                   liveSize.Width,
                                                   liveSize.Height)
            ExpandRetouchLivePatchRect(liveSpot, liveSize.Width, liveSize.Height)
            PublishRetouchLivePreview(forcePublish)
        End Sub

        Private _retouchBuffersInitSeq As Long = 0
        Private _retouchBuffersInitializing As Boolean = False
        ''' BaseKey (Spots + Anpassungen), zu dem die Live-Puffer passen - Gueltigkeitsstempel
        ''' fuers Vorwaermen (siehe AddRetouchSpot).
        Private _retouchBuffersKey As String = Nothing

        ''' Preview-Pixel je Basis-Pixel fuer Retusche-Radien - dieselbe sqrt(sx*sy)-Formel wie der
        ''' Renderer (DrawRetouchSpot/DrawHealingRegion).
        Private Function RetouchPreviewRadiusScale() As Single
            Dim src = GetPreviewSource()
            Dim baseW = GetBaseWidth()
            Dim baseH = GetBaseHeight()
            If src Is Nothing OrElse baseW <= 0 OrElse baseH <= 0 Then Return 1.0F
            Return CSng(Math.Sqrt((src.Width / CDbl(baseW)) * (src.Height / CDbl(baseH))))
        End Function

        ''' <summary>True, wenn die vorhandenen Live-Puffer exakt den committeten Stand spiegeln -
        ''' dann darf das Vorwaermen sie behalten. Seit Stufe E steckt die committete Retusche im
        ''' Arbeitsbild (WorkingImageVersion ist Teil des Keys) - kein Spot-Jonglieren mehr.</summary>
        Private Function RetouchLiveBuffersMatchCommittedState() As Boolean
            If _retouchLiveBitmap Is Nothing OrElse _retouchLiveSampleBitmap Is Nothing OrElse
               _retouchBuffersKey Is Nothing Then Return False
            Return String.Equals(_retouchBuffersKey, ImageProcessor.ComputeBaseKey(GetCurrentAdjustments(forPreview:=True)), StringComparison.Ordinal)
        End Function

        Private Sub BeginRetouchLiveBuffersAsync()
            ' Ist eine Bild-Ebene markiert, gehören die Live-Puffer IHR: sie tragen dann die Ebene
            ' allein statt der Szene (siehe EditorViewModelObjectRetouch.vb). Der Weg hierher ist
            ' derselbe - Werkzeugwechsel, Alt-Klick, Zugbeginn -, nur das Ziel ist ein anderes.
            If FindStrokeTargetImageAnnotation() IsNot Nothing Then
                BeginObjectRetouchLiveBuffersAsync()
                Return
            End If
            If _retouchBuffersInitializing Then Return
            ' Vorwaermen (Werkzeugwechsel/Alt+Klick): passende Puffer nicht neu bauen.
            If RetouchLiveBuffersMatchCommittedState() Then Return
            _retouchBuffersInitializing = True
            RunRetouchBufferInit()
        End Sub

        ''' <summary>Baut Ziel- und Sample-Bitmap im Hintergrund auf. Seit Stufe E braucht es nur
        ''' noch EINEN Render: die committete Retusche steckt im Arbeitsbild, Ziel = Klon der
        ''' warmen Basis (sonst Voll-Render), Sample = Kopie des Ziels (die Werkzeuge lesen beim
        ''' Commit ebenfalls vom aktuellen Stand des Arbeitsbilds). Nach dem Aufbau werden alle
        ''' bis dahin aufgelaufenen Punkte des aktiven Zugs nachgezogen. Bildwechsel/Dispose
        ''' invalidieren per Sequenznummer.</summary>
        Private Async Sub RunRetouchBufferInit()
            Dim seq = Threading.Interlocked.Increment(_retouchBuffersInitSeq)
            Try
                Dim previewSource = GetPreviewSource()
                If previewSource Is Nothing Then Return

                Dim strokeStart = If(_retouchStrokeActive,
                                     Math.Max(0, Math.Min(_retouchStrokeStartSpotIndex, _retouchSpots.Count)),
                                     _retouchSpots.Count)
                Dim targetAdj = GetCurrentAdjustments(forPreview:=True)

                Dim target As SKBitmap = Nothing
                Dim sample As SKBitmap = Nothing
                Try
                    Await Task.Run(Sub()
                                       target = ImageProcessor.TryCloneBaseCachedBitmap(previewSource, targetAdj)
                                       If target Is Nothing Then target = ImageProcessor.RenderPreviewSkBitmap(previewSource, targetAdj)
                                       sample = target?.Copy()
                                   End Sub)
                Catch ex As Exception
                    target?.Dispose()
                    sample?.Dispose()
                    DiagnosticLogService.LogException("EditorRetouchInit", ex)
                    Return
                End Try

                If seq <> _retouchBuffersInitSeq OrElse target Is Nothing OrElse sample Is Nothing OrElse
                   Not Object.ReferenceEquals(GetPreviewSource(), previewSource) Then
                    target?.Dispose()
                    sample?.Dispose()
                    Return
                End If

                DisposeRetouchLiveBuffers(keepInitSeq:=True)
                _retouchLiveBitmap = target
                _retouchLiveSampleBitmap = sample
                _retouchBuffersKey = ImageProcessor.ComputeBaseKey(targetAdj)
                ResetRetouchLivePatchForBitmap(_retouchLiveBitmap)

                ' Aufgelaufene Punkte des aktiven Zugs nachziehen und die echte Vorschau uebernehmen.
                If _retouchStrokeActive Then
                    Dim pendingSpots = _retouchSpots.Skip(strokeStart).Where(Function(s) s IsNot Nothing).ToList()
                    If pendingSpots.Count > 0 Then
                        Dim liveSpots = pendingSpots.Select(Function(s) TransformRetouchSpotToLiveGeometry(s)).ToList()
                        Dim liveSize = GetRetouchLiveGeometrySize()
                        ImageProcessor.ApplyRetouchSpotsInPlace(_retouchLiveBitmap, _retouchLiveSampleBitmap, liveSpots,
                                                                liveSize.Width,
                                                                liveSize.Height)
                        For Each s In liveSpots
                            ExpandRetouchLivePatchRect(s, liveSize.Width, liveSize.Height)
                        Next
                        PublishRetouchLivePreview(True)
                    End If
                End If
            Finally
                _retouchBuffersInitializing = False
            End Try
        End Sub

        ''' <param name="markPreviewPending">Ob danach ein Vorschaudurchlauf ansteht. Im FOTO ja: der
        ''' Flicken überbrückt nur, bis der Commit-Render landet. Auf einer EBENE nicht - dort IST der
        ''' Flicken die Ansicht, und das fertige Bild kommt über den Objekt-Überblendweg. Stünde die
        ''' Marke trotzdem, bliebe die Fußzeile auf "Vorschau wird aktualisiert" stehen, ohne dass
        ''' jemals ein Durchlauf käme, der sie zurücknimmt.</param>
        Private Sub PublishRetouchLivePreview(force As Boolean, Optional markPreviewPending As Boolean = True)
            If _retouchLiveBitmap Is Nothing OrElse _retouchLivePatchRect.IsEmpty Then Return
            Dim now = DateTime.UtcNow
            If force OrElse (now - _lastRetouchLivePreviewUtc).TotalMilliseconds >= 24.0 Then
                _lastRetouchLivePreviewUtc = now
                ' Nur der zuletzt hinzugekommene Bereich wird kopiert. Der fruehere Gesamtflicken
                ' wurde mit jedem Punkt groesser und machte lange Zuege zunehmend zaeh.
                If CopyRetouchOverlayRegion(_retouchLiveBitmap, _retouchLivePendingRect) Then
                    _retouchLivePendingRect = SKRectI.Empty
                    UpdateRetouchLivePatchPercentages()
                End If
                If markPreviewPending Then
                    _previewPending = True
                    StatusText = LocalizationService.T("Vorschau wird aktualisiert...")
                End If
            End If
        End Sub

        Private Sub ResetRetouchLivePatchForBitmap(bitmap As SKBitmap)
            If bitmap Is Nothing Then Return
            If _retouchLivePatchBitmapWidth = bitmap.Width AndAlso
               _retouchLivePatchBitmapHeight = bitmap.Height Then Return

            _retouchLivePatchRect = SKRectI.Empty
            _retouchLivePatchBitmapWidth = bitmap.Width
            _retouchLivePatchBitmapHeight = bitmap.Height
        End Sub

        Private Function EnsureRetouchMaskPreviewSize() As Boolean
            Dim displayWidth = DisplayImageWidthPixels
            Dim displayHeight = DisplayImageHeightPixels
            If displayWidth <= 0 OrElse displayHeight <= 0 Then Return False
            If _retouchLiveMaskBitmapWidth = displayWidth AndAlso _retouchLiveMaskBitmapHeight = displayHeight Then Return True
            _retouchLiveMaskBitmapWidth = displayWidth
            _retouchLiveMaskBitmapHeight = displayHeight
            Return True
        End Function

        Private Sub PublishRetouchMaskPreview(force As Boolean, Optional newest As RetouchSpot = Nothing)
            If _retouchLivePatchRect.IsEmpty OrElse _retouchLiveMaskBitmapWidth <= 0 OrElse _retouchLiveMaskBitmapHeight <= 0 Then Return
            EnsureRetouchLiveOverlay(_retouchLiveMaskBitmapWidth, _retouchLiveMaskBitmapHeight)
            EnsureRetouchMaskOverlay()
            If newest Is Nothing Then
                Dim spots = CurrentStrokeDisplaySpots()
                If spots.Count > 0 Then newest = spots(spots.Count - 1)
            End If
            If newest IsNot Nothing AndAlso _retouchLiveMaskOverlay IsNot Nothing Then
                ImageProcessor.DrawRetouchMaskSpot(_retouchLiveMaskOverlay, newest,
                                                   Math.Max(1, DisplayImageWidthPixels), Math.Max(1, DisplayImageHeightPixels))
            End If
            Dim now = DateTime.UtcNow
            If force OrElse (now - _lastRetouchLivePreviewUtc).TotalMilliseconds >= 24.0 Then
                _lastRetouchLivePreviewUtc = now
                If _retouchLiveMaskOverlay IsNot Nothing Then
                    If CopyRetouchOverlayRegion(_retouchLiveMaskOverlay, _retouchLivePendingRect) Then
                        _retouchLivePendingRect = SKRectI.Empty
                        UpdateRetouchLivePatchPercentages()
                    End If
                End If
            End If
        End Sub

        ''' <summary>Die Punkte des laufenden Zuges im ANZEIGERASTER - die orange Maske wird dort
        ''' gezeichnet. Bei einem Zug auf einer EBENE liegen sie schon so vor (sie entstehen aus der
        ''' Zeigerspur), beim Foto stehen sie im Arbeitsbild und werden hierher abgebildet. EINE
        ''' Stelle für beide Herkünfte: die Vorschau selbst darf nicht wissen müssen, wohin der Zug
        ''' geht.</summary>
        Private Function CurrentStrokeDisplaySpots() As List(Of RetouchSpot)
            If _objectRetouchDisplaySpots.Count > 0 Then Return _objectRetouchDisplaySpots.ToList()
            Return _retouchSpots.
                Skip(Math.Max(0, Math.Min(_retouchStrokeStartSpotIndex, _retouchSpots.Count))).
                Where(Function(s) s IsNot Nothing).
                Select(Function(s) TransformRetouchSpotToDisplayGeometry(s)).
                Where(Function(s) s IsNot Nothing).
                ToList()
        End Function

        Private Function RetouchLiveBufferUsesDisplayGeometry() As Boolean
            ' Die Live-Puffer entstehen aus RenderPreviewSkBitmap/TryCloneBaseCachedBitmap mit
            ' targetAdj. Sie sind damit bereits im gerenderten Bildraum. Bei grossen Bildern sind
            ' sie oft nur preview-skaliert; die Geometrie bleibt trotzdem Display-Geometrie und darf
            ' nicht anhand der absoluten Bitmap-Masse als Source-Geometrie fehlklassifiziert werden.
            Return _retouchLiveBitmap IsNot Nothing
        End Function

        Private Function TransformRetouchSpotToLiveGeometry(spot As RetouchSpot) As RetouchSpot
            If RetouchLiveBufferUsesDisplayGeometry() Then Return TransformRetouchSpotToDisplayGeometry(spot)
            Return spot?.Clone()
        End Function

        Private Function GetRetouchLiveGeometrySize() As (Width As Integer, Height As Integer)
            If RetouchLiveBufferUsesDisplayGeometry() Then
                Return (Math.Max(1, DisplayImageWidthPixels), Math.Max(1, DisplayImageHeightPixels))
            End If
            Return (Math.Max(1, GetBaseWidth()), Math.Max(1, GetBaseHeight()))
        End Function

        Private Sub ExpandRetouchLivePatchRect(spot As RetouchSpot, sourceWidthPixels As Integer, sourceHeightPixels As Integer)
            If spot Is Nothing OrElse _retouchLiveBitmap Is Nothing Then Return

            Dim scaleX As Single = 1.0F
            Dim scaleY As Single = 1.0F
            If sourceWidthPixels > 0 AndAlso sourceHeightPixels > 0 Then
                scaleX = _retouchLiveBitmap.Width / CSng(sourceWidthPixels)
                scaleY = _retouchLiveBitmap.Height / CSng(sourceHeightPixels)
            End If

            Dim radiusScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))
            Dim cx = CSng(spot.XPixels * scaleX)
            Dim cy = CSng(spot.YPixels * scaleY)
            Dim radius = CSng(Math.Max(2.0F, spot.RadiusPixels * radiusScale + 3.0F))
            Dim rect = New SKRectI(Math.Max(0, CInt(Math.Floor(cx - radius))),
                                   Math.Max(0, CInt(Math.Floor(cy - radius))),
                                   Math.Min(_retouchLiveBitmap.Width, CInt(Math.Ceiling(cx + radius))),
                                   Math.Min(_retouchLiveBitmap.Height, CInt(Math.Ceiling(cy + radius))))
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return
            _retouchLiveDirtyRect = rect
            _retouchLivePendingRect = UnionRetouchRects(_retouchLivePendingRect, rect)

            If _retouchLivePatchRect.IsEmpty Then
                _retouchLivePatchRect = rect
            Else
                _retouchLivePatchRect = New SKRectI(Math.Min(_retouchLivePatchRect.Left, rect.Left),
                                                    Math.Min(_retouchLivePatchRect.Top, rect.Top),
                                                    Math.Max(_retouchLivePatchRect.Right, rect.Right),
                                                    Math.Max(_retouchLivePatchRect.Bottom, rect.Bottom))
            End If
        End Sub

        Private Sub ExpandRetouchMaskPatchRect(spot As RetouchSpot)
            If spot Is Nothing OrElse _retouchLiveMaskBitmapWidth <= 0 OrElse _retouchLiveMaskBitmapHeight <= 0 Then Return

            Dim scaleX As Single = 1.0F
            Dim scaleY As Single = 1.0F
            Dim displayWidth = DisplayImageWidthPixels
            Dim displayHeight = DisplayImageHeightPixels
            If displayWidth > 0 AndAlso displayHeight > 0 Then
                scaleX = _retouchLiveMaskBitmapWidth / CSng(displayWidth)
                scaleY = _retouchLiveMaskBitmapHeight / CSng(displayHeight)
            End If

            Dim radiusScale = CSng(Math.Sqrt(Math.Max(0.0001F, scaleX * scaleY)))
            Dim cx = CSng(spot.XPixels * scaleX)
            Dim cy = CSng(spot.YPixels * scaleY)
            Dim radius = CSng(Math.Max(2.0F, spot.RadiusPixels * radiusScale + 3.0F))
            Dim rect = New SKRectI(Math.Max(0, CInt(Math.Floor(cx - radius))),
                                   Math.Max(0, CInt(Math.Floor(cy - radius))),
                                   Math.Min(_retouchLiveMaskBitmapWidth, CInt(Math.Ceiling(cx + radius))),
                                   Math.Min(_retouchLiveMaskBitmapHeight, CInt(Math.Ceiling(cy + radius))))
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return
            _retouchLiveDirtyRect = rect
            _retouchLivePendingRect = UnionRetouchRects(_retouchLivePendingRect, rect)

            If _retouchLivePatchRect.IsEmpty Then
                _retouchLivePatchRect = rect
            Else
                _retouchLivePatchRect = New SKRectI(Math.Min(_retouchLivePatchRect.Left, rect.Left),
                                                    Math.Min(_retouchLivePatchRect.Top, rect.Top),
                                                    Math.Max(_retouchLivePatchRect.Right, rect.Right),
                                                    Math.Max(_retouchLivePatchRect.Bottom, rect.Bottom))
            End If
        End Sub

        Private Sub UpdateRetouchLivePatchPercentages()
            Dim bitmapWidth = _retouchLivePatchBitmapWidth
            Dim bitmapHeight = _retouchLivePatchBitmapHeight
            If _retouchLivePatchRect.IsEmpty OrElse bitmapWidth <= 0 OrElse bitmapHeight <= 0 Then Return

            ' Die persistente Overlay-Bitmap hat volle Bildgroesse; ihr Inhalt ist transparent
            ' ausserhalb der bereits kopierten Teilbereiche.
            Dim l = 0.0, t = 0.0, w = 100.0, h = 100.0
            _retouchLivePatchLeftPercent = l
            _retouchLivePatchTopPercent = t
            _retouchLivePatchWidthPercent = w
            _retouchLivePatchHeightPercent = h
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchLeftPercent))
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchTopPercent))
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchWidthPercent))
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchHeightPercent))
        End Sub

        Private Shared Function UnionRetouchRects(first As SKRectI, second As SKRectI) As SKRectI
            If first.IsEmpty Then Return second
            If second.IsEmpty Then Return first
            Return New SKRectI(Math.Min(first.Left, second.Left), Math.Min(first.Top, second.Top),
                               Math.Max(first.Right, second.Right), Math.Max(first.Bottom, second.Bottom))
        End Function

        ''' <summary>Schreibt nur die neue lokale Aenderung in die persistente Anzeige. Anders als
        ''' ein neues Gesamt-Patch bleibt der Aufwand damit vom bisherigen Zug unabhaengig.</summary>
        Private Function CopyRetouchOverlayRegion(source As SKBitmap, rect As SKRectI) As Boolean
            If source Is Nothing OrElse rect.IsEmpty Then Return False
            If Not EnsureRetouchLiveOverlay(source.Width, source.Height) Then Return False
            Dim clipped = New SKRectI(Math.Max(0, rect.Left), Math.Max(0, rect.Top),
                                      Math.Min(source.Width, rect.Right), Math.Min(source.Height, rect.Bottom))
            If clipped.Width <= 0 OrElse clipped.Height <= 0 Then Return False
            Dim bytes = clipped.Width * 4
            Dim row(bytes - 1) As Byte
            Try
                Using fb = _retouchLiveOverlay.Lock()
                    Dim sourcePixels = source.GetPixels()
                    For y = 0 To clipped.Height - 1
                        Marshal.Copy(IntPtr.Add(sourcePixels, (clipped.Top + y) * source.RowBytes + clipped.Left * 4), row, 0, bytes)
                        Marshal.Copy(row, 0, IntPtr.Add(fb.Address, (clipped.Top + y) * fb.RowBytes + clipped.Left * 4), bytes)
                    Next
                End Using
                Me.RaisePropertyChanged(NameOf(RetouchLivePatchImage))
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Editor.RetouchOverlay", ex.Message)
                Return False
            End Try
        End Function

        Private Function EnsureRetouchLiveOverlay(width As Integer, height As Integer) As Boolean
            If width <= 0 OrElse height <= 0 Then Return False
            If _retouchLiveOverlay IsNot Nothing AndAlso _retouchLiveOverlay.PixelSize.Width = width AndAlso
               _retouchLiveOverlay.PixelSize.Height = height Then Return True
            RetouchLivePatchImage = Nothing
            _retouchLiveOverlay = Nothing
            Try
                _retouchLiveOverlay = New WriteableBitmap(New Avalonia.PixelSize(width, height), New Avalonia.Vector(96, 96),
                                                           Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Premul)
                Using fb = _retouchLiveOverlay.Lock()
                    Dim zero(Math.Max(0, fb.RowBytes - 1)) As Byte
                    For y = 0 To height - 1
                        Marshal.Copy(zero, 0, IntPtr.Add(fb.Address, y * fb.RowBytes), fb.RowBytes)
                    Next
                End Using
                RetouchLivePatchImage = _retouchLiveOverlay
                _retouchLivePatchBitmapWidth = width
                _retouchLivePatchBitmapHeight = height
                Return True
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Editor.RetouchOverlay", ex.Message)
                _retouchLiveOverlay = Nothing
                Return False
            End Try
        End Function

        Private Sub EnsureRetouchMaskOverlay()
            Dim w = _retouchLiveMaskBitmapWidth, h = _retouchLiveMaskBitmapHeight
            If w <= 0 OrElse h <= 0 Then Return
            If _retouchLiveMaskOverlay IsNot Nothing AndAlso _retouchLiveMaskOverlay.Width = w AndAlso _retouchLiveMaskOverlay.Height = h Then Return
            _retouchLiveMaskOverlay?.Dispose()
            _retouchLiveMaskOverlay = New SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul)
            Using canvas As New SKCanvas(_retouchLiveMaskOverlay)
                canvas.Clear(SKColors.Transparent)
            End Using
        End Sub

        Private Sub ClearRetouchLivePatch()
            RetouchLivePatchImage = Nothing
            _retouchLiveOverlay = Nothing
            _retouchLiveMaskOverlay?.Dispose()
            _retouchLiveMaskOverlay = Nothing
            _retouchLivePatchRect = SKRectI.Empty
            _retouchLiveDirtyRect = SKRectI.Empty
            _retouchLivePendingRect = SKRectI.Empty
            _retouchLivePatchLeftPercent = 0
            _retouchLivePatchTopPercent = 0
            _retouchLivePatchWidthPercent = 0
            _retouchLivePatchHeightPercent = 0
            _retouchLivePatchBitmapWidth = 0
            _retouchLivePatchBitmapHeight = 0
            _retouchLiveMaskBitmapWidth = 0
            _retouchLiveMaskBitmapHeight = 0
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchLeftPercent))
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchTopPercent))
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchWidthPercent))
            Me.RaisePropertyChanged(NameOf(RetouchLivePatchHeightPercent))
        End Sub

        Private Sub ScheduleRetouchPreviewUpdate(forceImmediate As Boolean)
            Dim now = DateTime.UtcNow
            If forceImmediate OrElse (now - _lastRetouchLivePreviewUtc).TotalMilliseconds >= RetouchLivePreviewMinIntervalMs Then
                _lastRetouchLivePreviewUtc = now
                UpdatePreview()
            Else
                SchedulePreviewUpdate()
            End If
        End Sub

        ''' ARBEITSBILD (Stufe E): der Zug wird REGIONAL in Vollauflösung ins Arbeitsbild
        ''' eingebacken (Hintergrund-Queue) - kein Rezept-Replay mehr. Der Live-Patch (bzw. die
        ''' Orange-Maske bei Heal/Großradius) überbrückt, bis der Commit-Render landet; Undo
        ''' läuft über den Vorher-Patch des Commits.
        Public Sub CommitRetouchStroke()
            If Not _retouchStrokeActive Then Return
            ' Ein Zug auf einer Ebene geht in ihre Bilddatei, nicht ins Arbeitsbild.
            If TryCommitObjectRetouchStroke() Then Return
            _retouchStrokeActive = False
            RaiseSaveAvailabilityChanged()
            Dim strokeStart = Math.Max(0, Math.Min(_retouchStrokeStartSpotIndex, _retouchSpots.Count))
            Dim strokeSpots = _retouchSpots.Skip(strokeStart).
                Where(Function(s) s IsNot Nothing).
                Select(Function(s) s.Clone()).
                ToList()
            Dim strokeHasHeal = strokeSpots.Any(
                Function(s) Not s.HasCloneSource AndAlso String.Equals(s.Mode, "Heal", StringComparison.OrdinalIgnoreCase))
            ' Spots sind TRANSIENT: ab dem Commit sind die Pixel des Arbeitsbilds die Wahrheit.
            _retouchSpots.Clear()
            _retouchStrokeStartSpotIndex = 0
            _lastRetouchLivePreviewUtc = DateTime.MinValue
            _previewTimer.Stop()
            _previewPending = False
            If strokeSpots.Count = 0 Then Return

            Dim undoEntry = _lastPushedUndoEntry   ' PushUndo kam beim Zugstart (AddRetouchSpot)
            Dim baseW = GetBaseWidth()
            Dim baseH = GetBaseHeight()
            Dim rect = ComputeRetouchStrokeFullRect(strokeSpots, baseW, baseH)
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return

            ' Live-Patch/Maske stehen lassen - die Brücke, bis der Commit-Render landet.
            PublishRetouchLivePreview(True)
            _clearRetouchLivePatchAfterPreview = True
            StatusText = LocalizationService.T("Vorschau wird aktualisiert...")

            Dim sw = Diagnostics.Stopwatch.StartNew()
            EnqueueWorkingCommit(
                Function()
                    Return _workingImage.CommitRegion(rect,
                        Sub(full)
                            ' Schreibt nur innerhalb der Spot-Masken (liegen in rect); die
                            ' Heal-Kandidatensuche LIEST frei aus der Umgebung des Vollbilds.
                            ImageProcessor.ApplyRetouchSpotsInPlace(full, full, strokeSpots, baseW, baseH)
                        End Sub)
                End Function,
                Sub(patch)
                    ' Ohne Flicken ist der Zug nicht im Arbeitsbild gelandet. Die Puffer trotzdem
                    ' auf den vermeintlich neuen Stand zu ziehen hiesse, mit einem Sample
                    ' weiterzuarbeiten, das es im Bild gar nicht gibt - der naechste Zug baute dann
                    ' auf einer Erfindung auf.
                    If patch Is Nothing Then
                        StatusText = LocalizationService.T("Retusche fehlgeschlagen")
                        DiagnosticLogService.LogAlways("Editor.RetouchCommit", "kein Flicken - Zug verworfen")
                        SchedulePreviewUpdate()
                        Return
                    End If
                    If undoEntry IsNot Nothing Then undoEntry.Patch = patch
                    DiagnosticLogService.LogAlways("Editor.RetouchCommit",
                        $"spots={strokeSpots.Count} heal={strokeHasHeal} rect={rect.Left},{rect.Top},{rect.Width}x{rect.Height} pixels={CLng(rect.Width) * CLng(rect.Height)} ms={sw.ElapsedMilliseconds}")
                    ' Nicht-Heal-Zug: das Live-Target enthält den Zug bereits (live gemalt) und das
                    ' Arbeitsbild jetzt auch - Sample auf den neuen Stand kopieren und NUR den
                    ' Gültigkeitsstempel auf die neue Arbeitsbild-Version nachziehen. Ohne das warf
                    ' jeder Zugstart die Puffer weg (Log: discardAtStrokeStart hadBuffers=True) und
                    ' der Stempel verlor seine Live-Ansicht ab Zug 2 (alter Nutzertest-24-Befund).
                    If Not strokeHasHeal AndAlso _retouchLiveBitmap IsNot Nothing Then
                        Dim refreshedSample = _retouchLiveBitmap.Copy()
                        If refreshedSample IsNot Nothing Then
                            _retouchLiveSampleBitmap?.Dispose()
                            _retouchLiveSampleBitmap = refreshedSample
                            _retouchBuffersKey = ImageProcessor.ComputeBaseKey(GetCurrentAdjustments(forPreview:=True))
                        Else
                            DisposeRetouchLiveBuffers()
                        End If
                    End If
                    SchedulePreviewUpdate()
                End Sub)

            ' Live-Puffer nach Heal-Zügen: das Target zeigt den ungeheilten Stand -> wegwerfen;
            ' der nächste Zugstart baut sie asynchron neu auf (dann inkl. Heilung).
            If strokeHasHeal Then
                DisposeRetouchLiveBuffers()
            End If
        End Sub

        ''' Zug-Region in Basis-Bildpixeln: Vereinigung aller Spot-Kreise plus Rand für weiche
        ''' Kanten und Blur (DrawBlurSpot-Pad = r + 3*sigma + 2 mit sigma <= 0,22r - der Faktor 2
        ''' deckt das großzügig ab). Geschrieben wird nur innerhalb dieser Region (harte
        ''' Anforderung des Umbaus); gelesen werden darf außerhalb.
        Private Shared Function ComputeRetouchStrokeFullRect(spots As List(Of RetouchSpot),
                                                             baseW As Integer, baseH As Integer) As SKRectI
            If spots Is Nothing OrElse spots.Count = 0 OrElse baseW <= 0 OrElse baseH <= 0 Then Return SKRectI.Empty
            Dim left As Double = Double.MaxValue, top As Double = Double.MaxValue
            Dim right As Double = Double.MinValue, bottom As Double = Double.MinValue
            For Each s In spots
                Dim margin = s.RadiusPixels * 2.0F + 16.0F
                left = Math.Min(left, s.XPixels - margin)
                top = Math.Min(top, s.YPixels - margin)
                right = Math.Max(right, s.XPixels + margin)
                bottom = Math.Max(bottom, s.YPixels + margin)
            Next
            If left > right OrElse top > bottom Then Return SKRectI.Empty
            Return New SKRectI(Math.Max(0, CInt(Math.Floor(left))),
                               Math.Max(0, CInt(Math.Floor(top))),
                               Math.Min(baseW, CInt(Math.Ceiling(right))),
                               Math.Min(baseH, CInt(Math.Ceiling(bottom))))
        End Function

        Private Sub DisposeRetouchLiveBuffers(Optional keepInitSeq As Boolean = False)
            ' Laufende asynchrone Puffer-Initialisierung invalidieren (Bildwechsel, Werkzeugwechsel) -
            ' ausser der Init-Abschluss selbst raeumt gerade die alten Puffer weg (keepInitSeq).
            If Not keepInitSeq Then _retouchBuffersInitSeq += 1
            If _retouchLiveBitmap IsNot Nothing Then
                _retouchLiveBitmap.Dispose()
                _retouchLiveBitmap = Nothing
            End If
            If _retouchLiveSampleBitmap IsNot Nothing Then
                _retouchLiveSampleBitmap.Dispose()
                _retouchLiveSampleBitmap = Nothing
            End If
            _retouchBuffersKey = Nothing
            ' Die Puffer sind EINE Einrichtung für zwei Ziele (Foto und Ebene) - der Vermerk der
            ' Ebene muss deshalb mit ihnen fallen, sonst gälte er für Puffer, die es nicht mehr gibt.
            _objectRetouchBufferStamp = ""
            _objectRetouchLiveReady = False
            _retouchLiveMaskBitmapWidth = 0
            _retouchLiveMaskBitmapHeight = 0
        End Sub
    End Class

End Namespace
