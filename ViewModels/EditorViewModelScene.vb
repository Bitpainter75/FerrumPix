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

    ''' <summary>Die Szene auf dem Schirm: der Kompositor-Blit, der Regionen-Renderer mit seinem
    ''' Arbeiter und das Zoom-Detail, das beim Hineinzoomen den sichtbaren Ausschnitt scharf
    ''' nachliefert.
    '''
    ''' Fuenfte Scheibe der Dateiaufteilung (2026-08-04), Regeln wie in
    ''' <c>ViewModels/EditorViewModelMask.vb</c>. Wie die Stufen zusammenspielen, steht in
    ''' <c>Audits/RENDERPIPELINE.md</c>.</summary>
    Partial Public Class EditorViewModel

        ''' <summary>Schlanker Snapshot ausschließlich für den UI-Thread-Kompositor. Ein Blit
        ''' braucht weder Pixelregler noch Kopien von Objekten, Masken und Korrekturebenen: er liest
        ''' nur Geometrie, Sichtbarkeit und die aktuellen Objektverweise. GetCurrentAdjustments
        ''' klont dagegen den kompletten Rezeptbaum und war damit bei jedem Schwenk/Blit unnötige
        ''' CPU- und GC-Last. Die Listen werden ausschließlich auf dem UI-Thread geändert, genau
        ''' dort läuft auch DrawCachedAnnotations; deshalb ist die Referenzsicht hier sicher.</summary>
        Private _compositorBlitSnapshot As ImageAdjustments
        Private _compositorBlitGeometryKey As String = ""

        ''' <summary>Wirft den Schnappschuss weg. NOETIG bei jeder Aenderung der Objektliste, denn
        ''' <c>Annotations</c> ist dort eine KOPIE der Liste (aus ObservableCollection wird List) und
        ''' haelt damit genau die Verweise fest, die zur Zeit des Schnappschusses galten.
        '''
        ''' <c>ApplyAdjustments</c> leert die Objektliste und fuellt sie mit KLONEN neu - beim
        ''' Umschalten des Reglerziels (Objekt markiert, Anpassen/Farbe/Details/Effekte/Filter aktiv)
        ''' passiert das ohne jeden Render. Der Schluessel aus Geometrie und Szenenversion aendert
        ''' sich dabei nicht, der Schnappschuss zeigte also weiter auf die weggeworfenen Instanzen:
        ''' der Blit zeichnete das Objekt an seiner ALTEN Stelle, waehrend Auswahlrahmen und
        ''' Treffertest der neuen folgten (Nutzerbefund 2026-08-25: "der Selektionsrahmen verschiebt
        ''' sich, das Objekt bleibt stehen" - ebenso beim Drehen und Skalieren).</summary>
        Private Sub InvalidateCompositorBlitSnapshot()
            _compositorBlitSnapshot = Nothing
            _compositorBlitGeometryKey = ""
        End Sub

        Private Function GetCompositorBlitAdjustments() As ImageAdjustments
            Dim crop = EffectiveCrop(fuerAnzeige:=True)
            Dim geometry = BuildAppliedGeometryAdjustments()
            Dim key = String.Join("|", GetBaseWidth(), GetBaseHeight(), ImageProcessor.GeometryOperationsKey(geometry.GeometryOperations),
                                  _rotationDegrees, _flipH, _flipV, crop.Left, crop.Top, crop.Right, crop.Bottom, _sceneContentVersion)
            If _compositorBlitSnapshot IsNot Nothing AndAlso String.Equals(_compositorBlitGeometryKey, key, StringComparison.Ordinal) Then
                Return _compositorBlitSnapshot
            End If

            _compositorBlitSnapshot = New ImageAdjustments With {
                .SourceWidthPixels = GetBaseWidth(),
                .SourceHeightPixels = GetBaseHeight(),
                .RotationDegrees = _rotationDegrees,
                .FlipHorizontal = _flipH,
                .FlipVertical = _flipV,
                .CropLeftPercent = CSng(crop.Left),
                .CropTopPercent = CSng(crop.Top),
                .CropRightPercent = CSng(crop.Right),
                .CropBottomPercent = CSng(crop.Bottom),
                .GeometryOperations = geometry.GeometryOperations.Select(Function(operation) operation.Clone()).ToList(),
                .Annotations = _annotations.ToList(),
                .AnnotationGroups = _annotationGroups,
                .MaskedAdjustmentLayers = _maskedAdjustmentLayers
            }
            _compositorBlitGeometryKey = key
            Return _compositorBlitSnapshot
        End Function

        ''' <summary>Anpassungssatz fuer die SZENE - wie die Vorschau, aber mit dem GEBACKENEN BLOCK:
        ''' alle Objekte oberhalb der Kompositor-Grenze werden am Klon ausgeblendet (das Rezept selbst
        ''' bleibt unberuehrt), die Blit-Stufe zeichnet sie aus dem Objekt-Bitmap-Cache darueber.</summary>
        Private Function GetSceneAdjustments() As ImageAdjustments
            Dim adj = GetCurrentAdjustments(forPreview:=True, includeEditorOverlayAnnotations:=True)
            If adj.Annotations IsNot Nothing Then
                Dim startIndex = OverlaySceneRenderer.ComputeCompositorStartIndex(adj)
                For i = startIndex To adj.Annotations.Count - 1
                    Dim annotation = adj.Annotations(i)
                    If annotation IsNot Nothing AndAlso OverlaySceneRenderer.IsOverlayAnnotation(annotation) Then
                        annotation.IsVisible = False
                    End If
                Next
            End If
            ' WÄHREND EINES ZUGES bleiben eingehängte Korrekturen draussen. Sie zwingen sonst zum
            ' Vollrender, und der ist zu langsam für eine Live-Darstellung: beim Drehen einer Gruppe
            ' sah man bis zum Loslassen gar nichts. Ohne sie läuft der
            ' schnelle Region-Patch, die Objekte folgen der Maus - und der Commit rendert danach
            ' EINMAL voll, womit die Korrekturen zurück sind.
            If _annotationPlacementEditActive AndAlso adj.MaskedAdjustmentLayers IsNot Nothing Then
                adj.MaskedAdjustmentLayers.RemoveAll(Function(l) l IsNot Nothing AndAlso
                                                      Not String.IsNullOrEmpty(l.StackAboveAnnotationId))
            End If
            Return adj
        End Function

        ''' <summary>Ersetzt die persistente Szene komplett (nach einem Vollrender) und blittet sie in
        ''' die persistente Anzeige. Uebernimmt die Ownership von sceneSk.</summary>
        Private Sub SetSceneBitmap(sceneSk As SKBitmap)
            If sceneSk Is Nothing Then Return
            ' Ein Vollrender malt die ganze Anzeige neu - die Zugbahn-Verkettung der Blits beginnt
            ' danach frisch, und vorgemerkte Reste der ALTEN Szene sind hinfaellig (der folgende
            ' Blit merkt sich seinen ungesehenen Teil selbst wieder vor).
            _compositorPreviousBlitRect = SKRectI.Empty
            _displayPendingRect = SKRectI.Empty
            _deferredSceneRegionRects.Clear()
            Dim previous = _sceneSk
            _sceneSk = sceneSk
            If previous IsNot Nothing AndAlso Not Object.ReferenceEquals(previous, sceneSk) Then previous.Dispose()
            _sceneContentVersion += 1
            InvalidateZoomDetail()
            EnsureSceneDisplay()
            BlitSceneRegionToDisplay(New SKRectI(0, 0, _sceneSk.Width, _sceneSk.Height))
        End Sub

        ''' <summary>Stellt sicher, dass die persistente Anzeige-Bitmap existiert und zur Szene passt
        ''' (Groesse). Nur bei Groessenwechsel entsteht eine neue Instanz (PreviewImage-Setter disposed
        ''' die alte).</summary>
        Private Sub EnsureSceneDisplay()
            If _sceneSk Is Nothing Then Return
            ' ABSICHERUNG (Ursache offen): eine bereits disposte Anzeige wie Nothing
            ' behandeln und neu aufbauen, statt mit ObjectDisposedException abzustuerzen. Die
            ' Log-Zeile haelt die Faehrte zur eigentlichen Dispose-Quelle offen.
            Dim displayWidth = -1
            Dim displayHeight = -1
            If _sceneDisplay IsNot Nothing Then
                Try
                    displayWidth = _sceneDisplay.PixelSize.Width
                    displayHeight = _sceneDisplay.PixelSize.Height
                Catch ex As ObjectDisposedException
                    DiagnosticLogService.LogAlways("Editor.SceneDisplay",
                                                   "disposedDetected=EnsureSceneDisplay - Anzeige wird neu aufgebaut")
                    _sceneDisplay = Nothing
                End Try
            End If
            If _sceneDisplay Is Nothing OrElse
               displayWidth <> _sceneSk.Width OrElse
               displayHeight <> _sceneSk.Height Then
                _sceneDisplay = New WriteableBitmap(New Avalonia.PixelSize(_sceneSk.Width, _sceneSk.Height),
                                                    New Avalonia.Vector(96, 96),
                                                    Avalonia.Platform.PixelFormat.Rgba8888,
                                                    Avalonia.Platform.AlphaFormat.Premul)
                PreviewImage = _sceneDisplay
            End If
        End Sub

        ''' <summary>Beim Klemmen aufs Sichtfenster liegengebliebene Schmutzregion der Anzeige -
        ''' nachgeblittet von FlushPendingDisplayRegion, sobald das Sichtfenster sie erreicht.</summary>
        Private _displayPendingRect As SKRectI = SKRectI.Empty

        ''' <summary>Dirty-Teile der SZENE, die ausserhalb des Sichtfensters liegen. Anders als der
        ''' Anzeige-Blit darf der Renderer sie nicht einfach vergessen: die persistente Szene wäre
        ''' dort sonst beim späteren Schwenken alt. Deshalb werden Rechtecke exakt um den bereits
        ''' gerenderten sichtbaren Teil beschnitten und beim Erreichen des Sichtfensters nachgereicht.</summary>
        Private ReadOnly _deferredSceneRegionRects As New List(Of SKRectI)()

        ''' <summary>Das Sichtfenster in Szenenpixeln, mit einem halben Fenster Rand je Seite -
        ''' derselbe Rand-Gedanke wie beim Zoom-Detail: Schwenks bleiben billig, weil der Rand schon
        ''' hochgeladen ist. Die Anteile meldet die View bei jedem Layout-Durchlauf
        ''' (UpdateZoomDetailViewport); Empty heisst "unbekannt oder alles sichtbar", dann wird nicht
        ''' geklemmt - so verhalten sich Kopflos-Betrieb (Pruefstand) und Einpassen-Ansicht exakt wie
        ''' bisher.</summary>
        Private Function ComputeBlitViewportRect() As SKRectI
            If _sceneSk Is Nothing Then Return SKRectI.Empty
            Dim l = _zoomDetailVisLeft, t = _zoomDetailVisTop
            Dim r = _zoomDetailVisRight, b = _zoomDetailVisBottom
            If r <= l OrElse b <= t Then Return SKRectI.Empty
            If l <= 0.0 AndAlso t <= 0.0 AndAlso r >= 1.0 AndAlso b >= 1.0 Then Return SKRectI.Empty
            Dim marginX = (r - l) * 0.5
            Dim marginY = (b - t) * 0.5
            Dim rect = New SKRectI(CInt(Math.Floor(Math.Max(0.0, l - marginX) * _sceneSk.Width)),
                                   CInt(Math.Floor(Math.Max(0.0, t - marginY) * _sceneSk.Height)),
                                   CInt(Math.Ceiling(Math.Min(1.0, r + marginX) * _sceneSk.Width)),
                                   CInt(Math.Ceiling(Math.Min(1.0, b + marginY) * _sceneSk.Height)))
            If rect.Left <= 0 AndAlso rect.Top <= 0 AndAlso
               rect.Right >= _sceneSk.Width AndAlso rect.Bottom >= _sceneSk.Height Then Return SKRectI.Empty
            Return rect
        End Function

        ''' <summary>Hängt die Teile von <paramref name="source"/> an, die NICHT in
        ''' <paramref name="cut"/> liegen. Ein Rechteck zerfällt dabei höchstens in vier
        ''' nicht-überlappende Streifen. So wird beim Schwenken nicht wieder die schon gerenderte
        ''' Bildmitte angefordert.</summary>
        Private Shared Sub AddRectDifference(source As SKRectI, cut As SKRectI, target As List(Of SKRectI))
            If source.IsEmpty Then Return
            If cut.IsEmpty Then
                target.Add(source)
                Return
            End If
            If source.Top < cut.Top Then target.Add(New SKRectI(source.Left, source.Top, source.Right, cut.Top))
            If cut.Bottom < source.Bottom Then target.Add(New SKRectI(source.Left, cut.Bottom, source.Right, source.Bottom))
            Dim middleTop = Math.Max(source.Top, cut.Top)
            Dim middleBottom = Math.Min(source.Bottom, cut.Bottom)
            If middleBottom <= middleTop Then Return
            If source.Left < cut.Left Then target.Add(New SKRectI(source.Left, middleTop, cut.Left, middleBottom))
            If cut.Right < source.Right Then target.Add(New SKRectI(cut.Right, middleTop, source.Right, middleBottom))
        End Sub

        Private Sub QueueDeferredSceneRegion(rect As SKRectI)
            If rect.IsEmpty Then Return
            _deferredSceneRegionRects.Add(rect)
        End Sub

        ''' <summary>Holt beim Schwenken ausschließlich die jetzt sichtbaren Teile bereits
        ''' aufgeschobener Szenenänderungen in die Render-Queue. Empty bedeutet wie beim Blit:
        ''' Sichtfenster unbekannt oder das gesamte Bild sichtbar, dann alles rendern.</summary>
        Private Sub FlushDeferredSceneRegions()
            If _deferredSceneRegionRects.Count = 0 Then Return
            Dim viewport = ComputeBlitViewportRect()
            Dim remaining As New List(Of SKRectI)()
            For Each deferredRect In _deferredSceneRegionRects
                Dim visible = If(viewport.IsEmpty, deferredRect, SKRectI.Intersect(deferredRect, viewport))
                If visible.IsEmpty Then
                    remaining.Add(deferredRect)
                Else
                    _sceneRegionPendingRect = ImageProcessor.UnionRects(_sceneRegionPendingRect, visible)
                    AddRectDifference(deferredRect, visible, remaining)
                End If
            Next
            _deferredSceneRegionRects.Clear()
            _deferredSceneRegionRects.AddRange(remaining)
            If Not _sceneRegionPendingRect.IsEmpty AndAlso Not _sceneRegionWorkerBusy Then RunSceneRegionWorker()
        End Sub

        ''' <summary>Blitten, was beim Klemmen aufs Sichtfenster liegenblieb - gerufen bei jeder
        ''' Sichtfenster-Meldung der View, also bei jedem Schwenk und Zoomwechsel. Der Blit laeuft
        ''' synchron im selben Layout-Durchlauf; die Anzeige ist damit gefuellt, bevor gezeichnet
        ''' wird. Erst wenn das Sichtfenster (samt Rand) den Rest ganz abdeckt, ist er erledigt -
        ''' bis dahin wird je Schwenk hoechstens ein fenstergrosses Stueck nachgeholt.</summary>
        Private Sub FlushPendingDisplayRegion()
            If _displayPendingRect.IsEmpty OrElse _sceneSk Is Nothing OrElse _sceneDisplay Is Nothing Then Return
            Dim pending = _displayPendingRect
            Dim viewport = ComputeBlitViewportRect()
            If viewport.IsEmpty OrElse
               (viewport.Left <= pending.Left AndAlso viewport.Top <= pending.Top AndAlso
                viewport.Right >= pending.Right AndAlso viewport.Bottom >= pending.Bottom) Then
                _displayPendingRect = SKRectI.Empty
                BlitSceneRegionToDisplay(pending)
                Return
            End If
            Dim visible = New SKRectI(Math.Max(pending.Left, viewport.Left), Math.Max(pending.Top, viewport.Top),
                                      Math.Min(pending.Right, viewport.Right), Math.Min(pending.Bottom, viewport.Bottom))
            If visible.Width <= 0 OrElse visible.Height <= 0 Then Return
            ' Der sichtbare Teil wird frisch, der Rest bleibt vorgemerkt (ein Rechteck kann keine
            ' Teilmenge abziehen - lieber beim naechsten Schwenk etwas doppelt blitten als Buch
            ' ueber Rechteck-Splitter fuehren).
            BlitSceneRegionToDisplay(visible)
        End Sub

        ''' <summary>Zeichnet die Region (Szene plus Kompositor-Objekte) DIREKT in den gesperrten
        ''' Framebuffer der Anzeige-Bitmap und meldet der View das Neuzeichnen (SceneInvalidated).
        '''
        ''' Frueher wurde je Blit ein Zwischenbitmap in Regionsgroesse angelegt, komponiert und
        ''' zeilenweise hineinkopiert - bei einer Mehrfachauswahl, deren Vereinigungsrechteck die
        ''' Szene deckt, waren das 38 MB Anlage plus zwei Kopien JE MAUSBEWEGUNG auf dem UI-Faden
        ''' (Nutzerbefund 2026-08-05: Ziehen zaeh, CPU hoch, GC-Laeufe im Sekundentakt).
        ''' InstallPixels legt stattdessen eine SKBitmap-Sicht UEBER die Lock-Adresse, und alles
        ''' wird genau einmal gezeichnet.
        '''
        ''' Geklemmt wird aufs Sichtfenster samt Rand (ComputeBlitViewportRect): was ausserhalb
        ''' liegt, merkt sich _displayPendingRect, und der naechste Schwenk holt es nach
        ''' (FlushPendingDisplayRegion). In der Einpassen-Ansicht klemmt nichts.</summary>
        Private Sub BlitSceneRegionToDisplay(rect As SKRectI)
            If _sceneSk Is Nothing OrElse _sceneDisplay Is Nothing OrElse rect.IsEmpty Then Return
            Dim clamped = New SKRectI(Math.Max(0, rect.Left), Math.Max(0, rect.Top),
                                      Math.Min(_sceneSk.Width, rect.Right), Math.Min(_sceneSk.Height, rect.Bottom))
            If clamped.Width <= 0 OrElse clamped.Height <= 0 Then Return

            Dim viewport = ComputeBlitViewportRect()
            If Not viewport.IsEmpty Then
                Dim visible = New SKRectI(Math.Max(clamped.Left, viewport.Left), Math.Max(clamped.Top, viewport.Top),
                                          Math.Min(clamped.Right, viewport.Right), Math.Min(clamped.Bottom, viewport.Bottom))
                If visible.Width <= 0 OrElse visible.Height <= 0 Then
                    _displayPendingRect = ImageProcessor.UnionRects(_displayPendingRect, clamped)
                    Return
                End If
                If visible <> clamped Then
                    ' Grob die GANZE Region vormerken: ein Rechteck kann den sichtbaren Teil nicht
                    ' herausschneiden. Beim Nachholen wird etwas doppelt geblittet, mehr nicht.
                    _displayPendingRect = ImageProcessor.UnionRects(_displayPendingRect, clamped)
                    clamped = visible
                End If
            End If

            Try
                Using fb = _sceneDisplay.Lock()
                    Dim info = New SKImageInfo(_sceneSk.Width, _sceneSk.Height,
                                               SKColorType.Rgba8888, SKAlphaType.Premul)
                    Using target = New SKBitmap()
                        If Not target.InstallPixels(info, fb.Address, fb.RowBytes) Then Return
                        Using canvas = New SKCanvas(target)
                            Dim region = New SKRect(clamped.Left, clamped.Top, clamped.Right, clamped.Bottom)
                            canvas.ClipRect(region)
                            ' Region ERSETZEN statt mischen: die Szene ist das fertige Komposit,
                            ' inkl. Transparenz bei ausgeblendetem Hintergrund.
                            Using replacePaint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                                canvas.DrawBitmap(_sceneSk, region, region, replacePaint)
                            End Using
                            ' Die Anschlagswarnung liegt auf dem BILD, nicht auf den Objekten
                            ' darüber: sie beantwortet, wo die Reglerkette Zeichnung verloren hat.
                            ' Ein Text in Weiss oder eine schwarze Form ist genau das nicht, und
                            ' eine Warnung, die daran angeht, zeigte einen Anschlag, den es nicht
                            ' gibt. Deshalb hier - nach der Szene, vor den Objekten.
                            If ShowClippingWarning Then
                                ImageProcessor.MarkClippingInPlace(target, clamped)
                            End If
                            Try
                                Dim adj = GetCompositorBlitAdjustments()
                                OverlaySceneRenderer.DrawCachedAnnotations(canvas, adj, _sceneSk.Width, _sceneSk.Height,
                                                                            _annotationBitmapCache, clamped)
                            Catch ex As Exception
                                ' Szene ist schon drin - lieber ohne Kompositor-Objekte anzeigen
                                ' als gar nicht (gleiches Verhalten wie der fruehere Rueckfall).
                                DiagnosticLogService.LogException("Editor.Compositor", ex)
                            End Try
                        End Using
                    End Using
                End Using
            Catch ex As ObjectDisposedException
                ' ABSICHERUNG: Anzeige wurde unter uns disposed - neu aufbauen und
                ' die KOMPLETTE Szene blitten (die neue Bitmap ist leer, nicht nur die Region).
                DiagnosticLogService.LogAlways("Editor.SceneDisplay",
                                               "disposedDetected=BlitSceneRegionToDisplay - Anzeige wird neu aufgebaut")
                _sceneDisplay = Nothing
                EnsureSceneDisplay()
                If _sceneDisplay Is Nothing Then Return
                BlitSceneRegionToDisplay(New SKRectI(0, 0, _sceneSk.Width, _sceneSk.Height))
                Return
            End Try
            RaiseEvent SceneInvalidated(Me, EventArgs.Empty)
        End Sub

        ''' <summary>Kennung des aktuellen Bildstandes fuer den Analysebild-Zwischenspeicher. Die
        ''' laufende Nummer der Szene erhoeht sich mit JEDEM neuen Szeneninhalt - also bei jeder
        ''' Reglerbewegung, jedem Objekt und jeder Maske. Genau das ist die Bedingung: solange sie
        ''' steht, gilt ein gerechnetes Diagramm weiter, und das Umschalten zwischen Histogramm,
        ''' Waveform und Parade kostet nur noch das Zeichnen.</summary>
        Friend Function SceneScopeKey() As String
            If String.IsNullOrEmpty(_currentImagePath) Then Return ""
            Return $"scene:{_currentImagePath}|{_sceneContentVersion}"
        End Function

        ''' <summary>Zeichnet die Anzeige aus der vorhandenen Szene neu, ohne die Kette anzufassen.
        ''' Für Umschalter, die nur die DARSTELLUNG ändern (heute die Anschlagswarnung): die Szene
        ''' bleibt, was sie ist, und der Blit legt sie samt Markierung noch einmal auf die Anzeige.
        ''' Das Zoom-Detail wird nur neu AUSGESCHNITTEN - seine Detail-Szene bleibt ebenfalls
        ''' stehen, ein Neu-Render wäre dort Sekunden für nichts.</summary>
        Private Sub RepaintSceneDisplay()
            If _sceneSk Is Nothing Then Return
            EnsureSceneDisplay()
            If _sceneDisplay Is Nothing Then Return
            ' Ein Vollblit hebt die Zugbahn-Verkettung auf: der nächste Zug beginnt frisch, sonst
            ' vereinigte sein erster Blit sich mit einem Rechteck von vor dem Umschalten.
            _compositorPreviousBlitRect = SKRectI.Empty
            _displayPendingRect = SKRectI.Empty
            BlitSceneRegionToDisplay(New SKRectI(0, 0, _sceneSk.Width, _sceneSk.Height))
            If _zoomDetailSk IsNot Nothing AndAlso _zoomDetailWanted Then ExtractZoomDetailRegion()
        End Sub

        ''' <summary>STUFE 2: rendert eine Region (Basis + Striche + ALLE Objekte in Z-Order) in die
        ''' persistente Szene und aktualisiert die Anzeige. Schneidet die Aenderung einen
        ''' Mischmodus-Abhaengigkeitsbereich, wird dieser automatisch mitgerendert (das Blend-Ergebnis
        ''' haengt vom Untergrund ab). False bei kaltem/gesperrtem Base-Cache oder fehlender Szene -
        ''' der Aufrufer plant dann den asynchronen Vollrender.</summary>
        ''' <summary>Passt die Szene noch zur AKTUELLEN Rezept-Geometrie? Ein Region-Patch kann die
        ''' Szenengroesse nie aendern - nach einem Geometriewechsel (etwa dem Live-Zuschnitt beim
        ''' Verlassen des Zuschneide-Werkzeugs) MUSS der Vollrender laufen. Ohne diesen Waechter
        ''' flickten die Patch-Wege in die alte, falsch grosse Szene weiter: die Buehne zeigte das
        ''' ganze Bild, waehrend Auswahlbox und Treffertest bereits im Ausschnitt rechneten - die Box
        ''' stand neben den Objekten und ein Zug legte sie an der falschen Stelle ab (Befund .fpx mit
        ''' gespeichertem Zuschnitt). Der zwischendurch geplante Vollrender ging verloren, weil die
        ''' Patch-Kurzwege den Zeitgeber anhalten und _previewPending loeschen.</summary>
        Private Function SceneMatchesCurrentGeometry(previewSource As SKBitmap, adj As ImageAdjustments) As Boolean
            If _sceneSk Is Nothing OrElse previewSource Is Nothing OrElse adj Is Nothing Then Return False
            Dim expected = ImageProcessor.ComputeGeometryOutputSize(previewSource.Width, previewSource.Height, adj)
            Return _sceneSk.Width = expected.Width AndAlso _sceneSk.Height = expected.Height
        End Function

        Private Function TryRenderSceneRegionSync(dirtyRect As SKRectI) As Boolean
            Dim previewSource = GetPreviewSource()
            If previewSource Is Nothing OrElse _sceneSk Is Nothing OrElse dirtyRect.IsEmpty Then Return False

            Dim adj = GetSceneAdjustments()
            If Not SceneMatchesCurrentGeometry(previewSource, adj) Then
                ' True zurueckgeben: der geplante Vollrender zeichnet ohnehin die ganze Szene, ein
                ' zusaetzlicher Region-Versuch der Aufrufer wuerde nur denselben Waechter treffen.
                DiagnosticLogService.LogAlways("Editor.SceneRegion",
                                               $"fallback=true reason=sceneSizeMismatch scene={_sceneSk.Width}x{_sceneSk.Height}")
                SchedulePreviewUpdate(markDirty:=False)
                Return True
            End If

            Dim rect = dirtyRect
            Dim blendDep = SceneBlendCompositeRequiredRect()
            If blendDep.RequiresComposite AndAlso Not blendDep.Rect.IsEmpty AndAlso
               OverlaySceneRenderer.Intersects(rect, blendDep.Rect) Then
                rect = ImageProcessor.UnionRects(rect, blendDep.Rect)
            End If

            Dim sw = Diagnostics.Stopwatch.StartNew()
            Dim clamped As SKRectI
            Dim cacheState = ImageProcessor.AnnotationPatchCacheState.Unknown
            Dim drawnObjects = 0
            Dim patch As SKBitmap
            Try
                patch = ImageProcessor.TryRenderAnnotationsPatchSkOnCachedBase(previewSource, adj, rect, clamped, cacheState, drawnObjects)
            Catch ex As Exception
                DiagnosticLogService.LogException("EditorSceneRegion", ex)
                Return False
            End Try
            If patch Is Nothing Then
                DiagnosticLogService.LogAlways("Editor.SceneRegion",
                                               $"fallback=true reason=cacheMissOrBusy rect={rect.Left},{rect.Top},{rect.Width}x{rect.Height} ms={sw.ElapsedMilliseconds}")
                Return False
            End If

            Using patch
                Using canvas = New SKCanvas(_sceneSk)
                    ' Region ERSETZEN statt mischen (BlendMode.Src): das Patch ist das fertige Komposit,
                    ' inkl. Transparenz bei ausgeblendetem Hintergrund.
                    Using replacePaint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        canvas.DrawBitmap(patch, clamped.Left, clamped.Top, replacePaint)
                    End Using
                End Using
            End Using
            _sceneContentVersion += 1
            InvalidateZoomDetail()
            EnsureSceneDisplay()
            BlitSceneRegionToDisplay(clamped)
            _annotationDirtyRect = SKRectI.Empty
            _previewPending = False
            ReportPreviewReady()
            ' drawn= ist der Messpunkt fuer den Kompositor-Umbau (OFFENE_PUNKTE Abschnitt 2, Stufe 1).
            DiagnosticLogService.LogAlways("Editor.SceneRegion",
                                           $"rect={clamped.Left},{clamped.Top},{clamped.Width}x{clamped.Height} pixels={CLng(clamped.Width) * CLng(clamped.Height)} drawn={drawnObjects} ms={sw.ElapsedMilliseconds}")
            Return True
        End Function

        ''' <summary>Baut die komplette Szene synchron auf dem gecachten Base neu - fuer
        ''' Strukturaenderungen (Anlegen/Loeschen/Umsortieren/Sichtbarkeit), bei denen kein enges
        ''' Dirty-Rect vorliegt.</summary>
        Private Function TryRenderSceneFullSync() As Boolean
            If _sceneSk Is Nothing Then Return False
            Return TryRenderSceneRegionSync(New SKRectI(0, 0, _sceneSk.Width, _sceneSk.Height))
        End Function

        ''' <summary>ASYNCHRONER Region-Render: reiht das Rect in die Pending-Union ein und startet bei
        ''' Bedarf den Worker. Fuer Regler-Bursts und Drag-Starts - der UI-Thread bleibt frei, waehrend
        ''' der Effekt-Render (Schatten/Gluehen grosser Objekte: 200-800 ms) im Hintergrund laeuft; das
        ''' Anwenden auf die Szene kostet nur ~20 ms. Ueberholende Anforderungen verschmelzen zur Union;
        ''' ein waehrenddessen ausgetauschtes _sceneSk (Vollrender) verwirft das Ergebnis und rendert neu.</summary>
        Private Sub RequestSceneRegionRender(dirtyRect As SKRectI)
            If dirtyRect.IsEmpty Then Return
            ' Nichtnormale Blend-Modi brauchen eine zusammenhängende Komposition über ihren
            ' Abhängigkeitsbereich. Für sie wäre ein Sichtfenster-Schnitt falsch; der Worker
            ' erweitert denselben Bereich anschließend nochmals als Sicherheitsnetz.
            Dim blendDep = SceneBlendCompositeRequiredRect()
            If blendDep.RequiresComposite AndAlso Not blendDep.Rect.IsEmpty AndAlso
               OverlaySceneRenderer.Intersects(dirtyRect, blendDep.Rect) Then
                _sceneRegionPendingRect = ImageProcessor.UnionRects(_sceneRegionPendingRect, dirtyRect)
                If Not _sceneRegionWorkerBusy Then RunSceneRegionWorker()
                Return
            End If

            Dim viewport = ComputeBlitViewportRect()
            Dim visible = If(viewport.IsEmpty, dirtyRect, SKRectI.Intersect(dirtyRect, viewport))
            If visible.IsEmpty Then
                QueueDeferredSceneRegion(dirtyRect)
                DiagnosticLogService.LogAlways("Editor.SceneRegion",
                                                $"deferred=1 rect={dirtyRect.Left},{dirtyRect.Top},{dirtyRect.Width}x{dirtyRect.Height}")
                Return
            End If
            _sceneRegionPendingRect = ImageProcessor.UnionRects(_sceneRegionPendingRect, visible)
            AddRectDifference(dirtyRect, visible, _deferredSceneRegionRects)
            If _sceneRegionWorkerBusy Then Return
            RunSceneRegionWorker()
        End Sub

        Private Async Sub RunSceneRegionWorker()
            _sceneRegionWorkerBusy = True
            Try
                While Not _sceneRegionPendingRect.IsEmpty
                    Dim previewSource = GetPreviewSource()
                    Dim sceneAtStart = _sceneSk
                    If previewSource Is Nothing OrElse sceneAtStart Is Nothing Then
                        ' Keine Szene (kalter Start): der Vollrender ist unterwegs bzw. wird geplant.
                        _sceneRegionPendingRect = SKRectI.Empty
                        Return
                    End If

                    Dim rect = _sceneRegionPendingRect
                    _sceneRegionPendingRect = SKRectI.Empty
                    Dim blendDep = SceneBlendCompositeRequiredRect()
                    If blendDep.RequiresComposite AndAlso Not blendDep.Rect.IsEmpty AndAlso
                       OverlaySceneRenderer.Intersects(rect, blendDep.Rect) Then
                        rect = ImageProcessor.UnionRects(rect, blendDep.Rect)
                    End If

                    Dim adj = GetSceneAdjustments()
                    ' Geometrie-Waechter wie im synchronen Zwilling: eine falsch grosse Szene kann
                    ' kein Patch richten, hier muss der Vollrender ran.
                    If Not SceneMatchesCurrentGeometry(previewSource, adj) Then
                        DiagnosticLogService.LogAlways("Editor.SceneRegion",
                                                       $"fallback=true reason=sceneSizeMismatch async=1 scene={sceneAtStart.Width}x{sceneAtStart.Height}")
                        SchedulePreviewUpdate(markDirty:=False)
                        Return
                    End If
                    Dim versionAtStart = _sceneContentVersion
                    Dim modelAtStart = _annotationModelVersion
                    Dim sw = Diagnostics.Stopwatch.StartNew()
                    Dim clamped As SKRectI = SKRectI.Empty
                    Dim cacheState = ImageProcessor.AnnotationPatchCacheState.Unknown
                    Dim drawnObjects = 0
                    Dim patch As SKBitmap = Nothing
                    Try
                        patch = Await Task.Run(Function()
                                                   Dim localClamped As SKRectI
                                                   Dim localCacheState = ImageProcessor.AnnotationPatchCacheState.Unknown
                                                   Dim localDrawn = 0
                                                   Dim p = ImageProcessor.TryRenderAnnotationsPatchSkOnCachedBase(previewSource, adj, rect,
                                                                                                                  localClamped, localCacheState, localDrawn)
                                                   clamped = localClamped
                                                   cacheState = localCacheState
                                                   drawnObjects = localDrawn
                                                   Return p
                                               End Function)
                    Catch ex As Exception
                        DiagnosticLogService.LogException("EditorSceneRegionAsync", ex)
                        Return
                    End Try

                    If patch Is Nothing Then
                        _annotationDirtyRect = ImageProcessor.UnionRects(_annotationDirtyRect, rect)
                        If cacheState = ImageProcessor.AnnotationPatchCacheState.Stale Then
                            ' Eine aktive Zauberstab-/Lasso-Auswahl ist reiner UI-Zustand und gehört
                            ' nicht in den Base-Key (siehe ComputeBaseKey). Ein echter Stale-Fall bleibt
                            ' trotzdem möglich, z.B. wenn direkt nach einer lokalen Korrektur ein Objekt
                            ' platziert wird: RefreshOverlayAfterAnnotationChange stoppt den noch
                            ' ausstehenden Vollrender zugunsten des schnellen Patches. Ein Patch kann
                            ' einen veralteten Basisstand aber niemals selbst erneuern - statt endlos
                            ' cacheMiss zu wiederholen jetzt EINEN Vollrender der aktuellen Szene planen.
                            _annotationCompositePreviewPending = False
                            _annotationCompositePreviewRetries = 0
                            DiagnosticLogService.LogAlways("Editor.SceneRegion",
                                                           $"fallback=true reason=cacheStale action=fullRender async=1 rect={rect.Left},{rect.Top},{rect.Width}x{rect.Height}")
                            SchedulePreviewUpdate()
                        Else
                            ' Cache ist nur kurz durch einen parallelen Vollrender gesperrt: nachziehen,
                            ' sobald dieser fertig ist. Dessen Base ist danach auch für das Objekt-Patch
                            ' nutzbar; das Patch stellt den aktuellen Objektstapel wieder her.
                            DiagnosticLogService.LogAlways("Editor.SceneRegion",
                                                           $"fallback=true reason=cacheBusy async=1 rect={rect.Left},{rect.Top},{rect.Width}x{rect.Height}")
                            ScheduleAnnotationCompositePreviewUpdate(60.0)
                        End If
                        Return
                    End If

                    If Not Object.ReferenceEquals(_sceneSk, sceneAtStart) OrElse _sceneContentVersion <> versionAtStart Then
                        ' Szene wurde waehrenddessen ersetzt (Vollrender) ODER ihr INHALT hat sich
                        ' geaendert (z.B. Objekt synchron angelegt, waehrend ein langer Strich-Render
                        ' lief): das Ergebnis basiert auf einem alten Snapshot und wuerde die Region
                        ' mit veraltetem Stand ueberschreiben (Sichtbefund: Objekt verschwindet, sobald
                        ' Pinselstriche im Spiel sind). Verwerfen und mit frischem Snapshot neu rendern.
                        patch.Dispose()
                        _sceneRegionPendingRect = ImageProcessor.UnionRects(_sceneRegionPendingRect, rect)
                        Continue While
                    End If

                    Using patch
                        Using canvas = New SKCanvas(_sceneSk)
                            Using replacePaint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                                canvas.DrawBitmap(patch, clamped.Left, clamped.Top, replacePaint)
                            End Using
                        End Using
                    End Using
                    _sceneContentVersion += 1
                    InvalidateZoomDetail()
                    EnsureSceneDisplay()
                    BlitSceneRegionToDisplay(clamped)
                    ' Das Modell hat sich waehrend des Renders geaendert: dieser Patch zeigt einen
                    ' ueberholten Stand. Region nachlegen, damit nicht der alte Stand stehen bleibt.
                    If _annotationModelVersion <> modelAtStart Then
                        _sceneRegionPendingRect = ImageProcessor.UnionRects(_sceneRegionPendingRect, rect)
                    End If
                    _previewPending = False
                    ReportPreviewReady()
                    ' drawn= ist der Messpunkt fuer den Kompositor-Umbau (OFFENE_PUNKTE Abschnitt 2, Stufe 1).
                    DiagnosticLogService.LogAlways("Editor.SceneRegion",
                                                   $"async=1 rect={clamped.Left},{clamped.Top},{clamped.Width}x{clamped.Height} pixels={CLng(clamped.Width) * CLng(clamped.Height)} drawn={drawnObjects} ms={sw.ElapsedMilliseconds}")
                End While
            Finally
                _sceneRegionWorkerBusy = False
            End Try
        End Sub

        ''' <summary>Beim App-Beenden aufrufen (MainWindow.HandleWindowClosing): legt laufende
        ''' Szene-/Zoom-Arbeiten still. Absturzbild (beim Beenden): der Fenster-
        ''' Teardown disposed die Anzeige-Bitmap, waehrend eine Region-Worker-Fortsetzung noch
        ''' aussteht - EnsureSceneDisplay griff dann auf die disposte Instanz. Hier wird NICHTS
        ''' disposed (in-flight-Renders koennten noch lesen), nur Referenzen gekappt und die
        ''' Version gebumpt, damit jede Fortsetzung ihr Ergebnis verwirft. Dokumenteigene Auswahl-
        ''' Assets werden dagegen ausdrücklich entfernt; nach dem Fensterende kann sie kein Undo-/Redo-
        ''' Eintrag mehr erreichen.</summary>
        Public Sub ShutdownSceneWork()
            _sceneContentVersion += 1
            _sceneRegionPendingRect = SKRectI.Empty
            _deferredSceneRegionRects.Clear()
            _displayPendingRect = SKRectI.Empty
            _sceneDisplay = Nothing
            _sceneSk = Nothing
            ResetZoomDetail()
            CleanupCurrentSelectionAssetTempDir()
        End Sub

        ' ===================== STUFE 3: Zoom-Detail =====================

        Public ReadOnly Property ZoomDetailImage As Bitmap
            Get
                Return _zoomDetailImage
            End Get
        End Property

        ''' Vorher-Seite des Zoom-Details (Original nur mit Geometrie) - Nothing, solange kein
        ''' Vergleich aktiv ist oder das Vorher-Detail noch nicht gerendert wurde.
        Public ReadOnly Property ZoomDetailBeforeImage As Bitmap
            Get
                Return _zoomDetailBeforeImage
            End Get
        End Property

        Public ReadOnly Property ZoomDetailFracLeft As Double
            Get
                Return _zoomDetailFracLeft
            End Get
        End Property

        Public ReadOnly Property ZoomDetailFracTop As Double
            Get
                Return _zoomDetailFracTop
            End Get
        End Property

        Public ReadOnly Property ZoomDetailFracWidth As Double
            Get
                Return _zoomDetailFracWidth
            End Get
        End Property

        Public ReadOnly Property ZoomDetailFracHeight As Double
            Get
                Return _zoomDetailFracHeight
            End Get
        End Property

        ''' <summary>STUFE 3: Die View meldet bei jedem Layout-Durchlauf den sichtbaren Bildausschnitt
        ''' (Anteile 0..1) und ob die Anzeige die Szenen-Aufloesung uebersteigt. Passt der gecachte
        ''' Detail-Stand (Version + Abdeckung), passiert nichts bzw. nur ein billiger Region-Blit;
        ''' sonst wird der teure Detail-Render debounced geplant. active=False raeumt alles weg.</summary>
        Public Sub UpdateZoomDetailViewport(visLeft As Double, visTop As Double,
                                            visRight As Double, visBottom As Double,
                                            active As Boolean,
                                            Optional wantBefore As Boolean = False)
            _zoomDetailVisLeft = visLeft
            _zoomDetailVisTop = visTop
            _zoomDetailVisRight = visRight
            _zoomDetailVisBottom = visBottom
            ' Was der Blit beim Klemmen aufs Sichtfenster liegen liess, jetzt nachholen - diese
            ' Meldung kommt bei jedem Schwenk und Zoomwechsel, synchron im Layout-Durchlauf.
            FlushPendingDisplayRegion()
            ' Dasselbe für die SZENE: nicht sichtbare Dirty-Teile wurden beim Rendern aufgeschoben
            ' und werden erst jetzt, wenn sie in den Viewport rücken, in den Worker eingereiht.
            FlushDeferredSceneRegions()

            ' SetZoomDetailImage/SetZoomDetailBeforeImage feuern PropertyChanged synchron. Die View
            ' positioniert daraufhin sofort das Overlay und meldet den Viewport erneut. Ohne Guard
            ' kann das mitten in ExtractZoomDetailRegion wieder eine Extraktion ausloesen - besonders
            ' im Vergleichsmodus, bevor die Vorher-Seite gesetzt ist.
            If _zoomDetailExtracting Then
                If active Then
                    _zoomDetailWanted = True
                    _zoomDetailWantBefore = wantBefore
                End If
                Return
            End If

            If Not active Then
                ResetZoomDetail()
                Return
            End If
            _zoomDetailWanted = True
            _zoomDetailWantBefore = wantBefore
            If Not wantBefore AndAlso _zoomDetailBeforeImage IsNot Nothing Then SetZoomDetailBeforeImage(Nothing)

            ' Waehrend Placement-Edit/Retusche-Zug aendert sich der Szeneninhalt laufend - kein
            ' Detail zeigen (es waere sofort veraltet); der Commit bumpt die Version und plant neu.
            If _annotationPlacementEditActive OrElse _retouchStrokeActive Then
                SetZoomDetailImage(Nothing)
                SetZoomDetailBeforeImage(Nothing)
                Return
            End If

            ' Detail-Stand passt nur, wenn auch die Vorher-Szene da ist, falls sie gebraucht wird -
            ' sonst neu rendern (der Render laedt dann beide Seiten).
            If _zoomDetailSk IsNot Nothing AndAlso _zoomDetailVersion = _sceneContentVersion AndAlso
               (Not wantBefore OrElse _zoomDetailBeforeSk IsNot Nothing) Then
                If _zoomDetailImage IsNot Nothing AndAlso ZoomDetailExtractCoversVisible() AndAlso
                   (Not wantBefore OrElse _zoomDetailBeforeImage IsNot Nothing) Then Return
                ExtractZoomDetailRegion()
                Return
            End If

            ' Kein passender Detail-Stand: nichts Veraltetes zeigen, Render debounced anstossen.
            SetZoomDetailImage(Nothing)
            SetZoomDetailBeforeImage(Nothing)
            RestartZoomDetailTimer()
        End Sub

        ''' <summary>Szeneninhalt hat sich geaendert (Versions-Bump): veraltetes Detail sofort
        ''' ausblenden und - falls der Zoom noch aktiv ist - den Neu-Render debounced planen.</summary>
        Private Sub InvalidateZoomDetail()
            If _zoomDetailImage IsNot Nothing Then SetZoomDetailImage(Nothing)
            If _zoomDetailBeforeImage IsNot Nothing Then SetZoomDetailBeforeImage(Nothing)
            If _zoomDetailWanted Then RestartZoomDetailTimer()
        End Sub

        ''' <summary>Zoom verlassen/Bildwechsel: Overlay aus, Caches freigeben. Laeuft gerade ein
        ''' Render, wird das Dispose deferred (Radiergummi-Lektion: nie unter einem laufenden
        ''' Hintergrund-Render wegdisposen).</summary>
        Private Sub ResetZoomDetail()
            _zoomDetailWanted = False
            _zoomDetailWantBefore = False
            _zoomDetailTimer?.Stop()
            SetZoomDetailImage(Nothing)
            SetZoomDetailBeforeImage(Nothing)
            If _zoomDetailRendering Then
                _zoomDetailDisposePending = True
                Return
            End If
            _zoomDetailSk?.Dispose()
            _zoomDetailSk = Nothing
            _zoomDetailVersion = -1
            _zoomDetailSource?.Dispose()
            _zoomDetailSource = Nothing
            _zoomDetailSourcePath = Nothing
            _zoomDetailSourceWorkingVersion = -1
            _zoomDetailBeforeSk?.Dispose()
            _zoomDetailBeforeSk = Nothing
            _zoomDetailBeforeSource?.Dispose()
            _zoomDetailBeforeSource = Nothing
            _zoomDetailBeforeSourcePath = Nothing
        End Sub

        Private Sub SetZoomDetailImage(value As Bitmap)
            If Object.ReferenceEquals(_zoomDetailImage, value) Then Return
            Dim previous = _zoomDetailImage
            _zoomDetailImage = value
            If previous IsNot Nothing Then DisposeDeferred(previous)
            Me.RaisePropertyChanged(NameOf(ZoomDetailImage))
        End Sub

        Private Sub SetZoomDetailBeforeImage(value As Bitmap)
            If Object.ReferenceEquals(_zoomDetailBeforeImage, value) Then Return
            Dim previous = _zoomDetailBeforeImage
            _zoomDetailBeforeImage = value
            If previous IsNot Nothing Then DisposeDeferred(previous)
            Me.RaisePropertyChanged(NameOf(ZoomDetailBeforeImage))
        End Sub

        Private Sub RestartZoomDetailTimer()
            If _zoomDetailTimer Is Nothing Then
                _zoomDetailTimer = New DispatcherTimer With {.Interval = TimeSpan.FromMilliseconds(350)}
                AddHandler _zoomDetailTimer.Tick, Sub()
                                                      _zoomDetailTimer.Stop()
                                                      BeginZoomDetailRenderAsync()
                                                  End Sub
            End If
            _zoomDetailTimer.Stop()
            _zoomDetailTimer.Start()
        End Sub

        ''' <summary>Rendert die Detail-Szene asynchron (Task.Run): Quelle laden (bzw. Cache), voller
        ''' Renderer mit den aktuellen Szenen-Einstellungen. Sequenz- und Versions-Guards verwerfen
        ''' ueberholte Ergebnisse; ein Versions-Wechsel waehrend des Renders plant automatisch neu.</summary>
        Private Async Sub BeginZoomDetailRenderAsync()
            If Not _zoomDetailWanted OrElse _zoomDetailRendering Then Return
            If _annotationPlacementEditActive OrElse _retouchStrokeActive Then Return
            If _sceneSk Is Nothing Then Return

            ' Lohnt nur, wenn die Quelle spuerbar mehr Aufloesung hat als die Szene.
            Dim baseLongest = Math.Max(GetBaseWidth(), GetBaseHeight())
            Dim sceneLongest = Math.Max(_sceneSk.Width, _sceneSk.Height)
            Dim detailTarget = Math.Min(ZoomDetailMaxDimension, baseLongest)
            If detailTarget <= CInt(sceneLongest * 1.15) Then Return

            Dim path = RenderSourcePath
            If String.IsNullOrWhiteSpace(path) Then Return

            _zoomDetailRendering = True
            _zoomDetailRenderSeq += 1
            Dim seq = _zoomDetailRenderSeq
            Dim versionAtStart = _sceneContentVersion
            ' NICHT GetSceneAdjustments: das blendet mit eingeschalteter Kompositor-Weiche die
            ' Kompositor-Objekte aus (die legt die Blit-Stufe ueber die SZENE, nicht ueber das
            ' Detail). Das Detail rendert voll - gedrehte Objekte sind hier sogar wieder
            ' vektor-scharf. Die beiden Sonderfaelle von GetSceneAdjustments (Zug-Ausblendungen)
            ' treffen es nicht: waehrend eines Zuges rendert das Detail gar nicht (Wache oben).
            Dim adj = GetCurrentAdjustments(forPreview:=True, includeEditorOverlayAnnotations:=True)
            ' Quelle nur wiederverwenden, wenn sie zu Pfad UND Arbeitsbild-Version passt (die
            ' Ziel-Aufloesung ist je Bild konstant; die Version wandert bei jedem Commit weiter).
            Dim workingVersionAtStart = _workingImage.Version
            Dim cachedSource = If(String.Equals(_zoomDetailSourcePath, path, StringComparison.Ordinal) AndAlso
                                  _zoomDetailSourceWorkingVersion = workingVersionAtStart, _zoomDetailSource, Nothing)
            ' Vorher-Seite (Vergleich sichtbar): hochaufgeloester ORIGINAL-Decode, nur pfadabhaengig
            ' (das Original aendert sich durch Commits nicht).
            Dim wantBefore = _zoomDetailWantBefore
            Dim cachedBeforeSource = If(String.Equals(_zoomDetailBeforeSourcePath, path, StringComparison.Ordinal),
                                        _zoomDetailBeforeSource, Nothing)

            Dim sw = Diagnostics.Stopwatch.StartNew()
            Dim source As SKBitmap = Nothing
            Dim rendered As SKBitmap = Nothing
            Dim beforeSource As SKBitmap = Nothing
            Dim beforeRendered As SKBitmap = Nothing
            Try
                Await Task.Run(Sub()
                                   ' Arbeitsbild statt Datei-Decode (Stufe C): RenderDownscale ist
                                   ' threadsicher; Rueckfall auf den Datei-Decode nur, falls das
                                   ' Arbeitsbild (noch) nicht initialisiert ist.
                                   source = If(cachedSource,
                                               If(_workingImage.RenderDownscale(detailTarget),
                                                  ImageProcessor.LoadPreviewSource(path, detailTarget)))
                                   If source Is Nothing Then Return
                                   rendered = ImageProcessor.RenderPreviewSkBitmap(source, adj)
                                   If wantBefore Then
                                       beforeSource = If(cachedBeforeSource,
                                                         ImageProcessor.LoadPreviewSource(path, detailTarget))
                                       If beforeSource IsNot Nothing Then
                                           beforeRendered = ImageProcessor.ApplyGeometryAdjustmentsSk(beforeSource, adj)
                                       End If
                                   End If
                               End Sub)
            Catch ex As Exception
                DiagnosticLogService.LogException("EditorZoomDetail", ex)
            Finally
                _zoomDetailRendering = False
            End Try

            ' Reset lief waehrenddessen (Zoom raus/Bildwechsel): alles Frische entsorgen.
            If _zoomDetailDisposePending OrElse Not _zoomDetailWanted OrElse seq <> _zoomDetailRenderSeq Then
                _zoomDetailDisposePending = False
                rendered?.Dispose()
                beforeRendered?.Dispose()
                If source IsNot Nothing AndAlso Not Object.ReferenceEquals(source, _zoomDetailSource) Then source.Dispose()
                If beforeSource IsNot Nothing AndAlso Not Object.ReferenceEquals(beforeSource, _zoomDetailBeforeSource) Then beforeSource.Dispose()
                If Not _zoomDetailWanted Then ResetZoomDetail()
                Return
            End If

            ' Frisch geladene Quellen in den Cache uebernehmen (fuer die naechsten Renders).
            If source IsNot Nothing AndAlso Not Object.ReferenceEquals(source, _zoomDetailSource) Then
                _zoomDetailSource?.Dispose()
                _zoomDetailSource = source
                _zoomDetailSourcePath = path
                _zoomDetailSourceWorkingVersion = workingVersionAtStart
            End If
            If beforeSource IsNot Nothing AndAlso Not Object.ReferenceEquals(beforeSource, _zoomDetailBeforeSource) Then
                _zoomDetailBeforeSource?.Dispose()
                _zoomDetailBeforeSource = beforeSource
                _zoomDetailBeforeSourcePath = path
            End If
            If rendered Is Nothing Then Return

            If versionAtStart <> _sceneContentVersion Then
                ' Inhalt hat sich waehrend des Renders geaendert: verwerfen und SOFORT neu starten
                ' (der Bump liegt in der Vergangenheit - der 350-ms-Timer wuerde nur Zeit kosten).
                ' Die Log-Zeile zeigt, falls Renders dauerhaft im Kreis verworfen werden.
                rendered.Dispose()
                beforeRendered?.Dispose()
                DiagnosticLogService.LogAlways("Editor.ZoomDetail",
                                               $"discarded=version ms={sw.ElapsedMilliseconds}")
                BeginZoomDetailRenderAsync()
                Return
            End If

            _zoomDetailSk?.Dispose()
            _zoomDetailSk = rendered
            _zoomDetailVersion = versionAtStart
            If beforeRendered IsNot Nothing Then
                _zoomDetailBeforeSk?.Dispose()
                _zoomDetailBeforeSk = beforeRendered
            ElseIf Not wantBefore AndAlso _zoomDetailBeforeSk IsNot Nothing Then
                _zoomDetailBeforeSk.Dispose()
                _zoomDetailBeforeSk = Nothing
            End If
            DiagnosticLogService.LogAlways("Editor.ZoomDetail",
                                           $"rendered={rendered.Width}x{rendered.Height} before={beforeRendered IsNot Nothing} ms={sw.ElapsedMilliseconds}")
            ExtractZoomDetailRegion()
        End Sub

        ''' <summary>Blittet den sichtbaren Ausschnitt (+50 % Rand je Seite) aus der Detail-Szene in
        ''' ein kleines Anzeige-Bitmap. Der Rand macht Pans billig: das Overlay ist bildverankert und
        ''' wandert mit; erst ausserhalb des Rands wird neu geblittet.</summary>
        Private Sub ExtractZoomDetailRegion()
            If _zoomDetailExtracting Then Return
            If _zoomDetailSk Is Nothing Then Return
            _zoomDetailExtracting = True
            Try
                Dim dw = _zoomDetailSk.Width
                Dim dh = _zoomDetailSk.Height
                If dw <= 0 OrElse dh <= 0 Then Return

                Dim marginX = Math.Max(0.01, (_zoomDetailVisRight - _zoomDetailVisLeft) * 0.5)
                Dim marginY = Math.Max(0.01, (_zoomDetailVisBottom - _zoomDetailVisTop) * 0.5)
                Dim fracL = Math.Max(0.0, _zoomDetailVisLeft - marginX)
                Dim fracT = Math.Max(0.0, _zoomDetailVisTop - marginY)
                Dim fracR = Math.Min(1.0, _zoomDetailVisRight + marginX)
                Dim fracB = Math.Min(1.0, _zoomDetailVisBottom + marginY)

                Dim rect = New SKRectI(CInt(Math.Floor(fracL * dw)), CInt(Math.Floor(fracT * dh)),
                                       CInt(Math.Ceiling(fracR * dw)), CInt(Math.Ceiling(fracB * dh)))
                rect = New SKRectI(Math.Max(0, rect.Left), Math.Max(0, rect.Top),
                                   Math.Min(dw, rect.Right), Math.Min(dh, rect.Bottom))
                If rect.Width <= 0 OrElse rect.Height <= 0 Then
                    SetZoomDetailImage(Nothing)
                    Return
                End If

                ' Dieselbe Warnung wie auf der Szene: sie beim Hineinzoomen verschwinden zu lassen
                ' waere genau dann weg, wenn man sie braucht - der Anschlag wird am Detail beurteilt.
                Dim bmp = ImageProcessor.RenderBitmapPatch(_zoomDetailSk, rect, 0, ShowClippingWarning)
                If bmp Is Nothing Then
                    SetZoomDetailImage(Nothing)
                    Return
                End If
                _zoomDetailFracLeft = rect.Left / CDbl(dw)
                _zoomDetailFracTop = rect.Top / CDbl(dh)
                _zoomDetailFracWidth = rect.Width / CDbl(dw)
                _zoomDetailFracHeight = rect.Height / CDbl(dh)
                SetZoomDetailImage(bmp)

                ' Vorher-Seite: gleicher Ausschnitt aus der Vorher-Detail-Szene. Nur bei identischen
                ' Maßen (beide Seiten laufen durch dieselbe Geometrie auf gleich großen Quellen) - bei
                ' Abweichung lieber kein Vorher-Detail als ein verschobenes.
                If _zoomDetailWantBefore AndAlso _zoomDetailBeforeSk IsNot Nothing AndAlso
                   _zoomDetailBeforeSk.Width = dw AndAlso _zoomDetailBeforeSk.Height = dh Then
                    SetZoomDetailBeforeImage(ImageProcessor.RenderBitmapPatch(_zoomDetailBeforeSk, rect))
                Else
                    SetZoomDetailBeforeImage(Nothing)
                End If
            Finally
                _zoomDetailExtracting = False
            End Try
        End Sub

        ''' Deckt der aktuelle Ausschnitt (inkl. Rand) den sichtbaren Bereich noch ab?
        Private Function ZoomDetailExtractCoversVisible() As Boolean
            Const eps As Double = 0.0005
            Return _zoomDetailVisLeft >= _zoomDetailFracLeft - eps AndAlso
                   _zoomDetailVisTop >= _zoomDetailFracTop - eps AndAlso
                   _zoomDetailVisRight <= _zoomDetailFracLeft + _zoomDetailFracWidth + eps AndAlso
                   _zoomDetailVisBottom <= _zoomDetailFracTop + _zoomDetailFracHeight + eps
        End Function

        ' ===================== ENDE STUFE 3 =====================

        ''' Versucht, die Annotationen synchron auf dem bereits gecachten Base-Bitmap neu zu
        ''' komposieren (siehe ImageProcessor.TryRenderAnnotationsOnCachedBase), statt auf den
        ''' asynchronen Task.Run-Render zu warten. Das koppelt "Live-Overlay erscheint" und
        ''' "gebackenes Bild blendet Objekt aus/ein" atomar in denselben Aufruf-Stack und schließt
        ''' damit das Zeitfenster, in dem kurzzeitig beide (Live-Overlay UND gebackener Text)
        ''' sichtbar sind. Bei kaltem Cache oder falls der Cache-Lock gerade von einem
        ''' Hintergrund-Render gehalten wird, liefert dies False - der Aufrufer fällt dann auf den
        ''' bestehenden asynchronen Pfad zurück.
        Private Function TryRenderAnnotationOverlaySync() As Boolean
            Dim previewSource = GetPreviewSource()
            If previewSource Is Nothing Then Return False

            ' STUFE 2: die "komplette Annotations-Neukomposition" ist jetzt ein Szenen-Vollaufbau auf
            ' dem gecachten Base (gleiche Kosten, aber die Szene bleibt die einzige Wahrheit).
            If Not TryRenderSceneFullSync() Then Return False
            Return True
        End Function

        ''' <summary>Die Region, die ein Objekt-Patch als Nächstes schreiben würde: die gesammelte
        ''' Dirty-Region, sonst die Rechtecke ALLER markierten Objekte. Eine Stelle für beide Aufrufer,
        ''' damit die Vollrender-Weiche über DIESELBE Region entscheidet, die danach gerendert wird.
        '''
        ''' ALLE MARKIERTEN, nicht nur der Anker: die Regler der Anpassungswerkzeuge beschreiben die
        ''' ganze Auswahl. Mit dem Rechteck des Ankers allein wurde nur ein Objekt neu gezeichnet, und
        ''' die übrigen sprangen erst beim nächsten Vollrender nach - also beim Abwählen.</summary>
        Private Function CandidateAnnotationDirtyRect() As SKRectI
            If Not _annotationDirtyRect.IsEmpty Then Return _annotationDirtyRect
            If GetPreviewSource() Is Nothing Then Return SKRectI.Empty
            If _selectedAnnotationIndex < 0 OrElse _selectedAnnotationIndex >= _annotations.Count Then Return SKRectI.Empty
            Dim size = GetCurrentScenePixelSize()
            If size.Width <= 0 OrElse size.Height <= 0 Then Return SKRectI.Empty
            Dim adj = GetCurrentAdjustments(forPreview:=True)
            Dim rect = SKRectI.Empty
            For Each a In SelectedAnnotations
                If a Is Nothing Then Continue For
                rect = ImageProcessor.UnionRects(rect, ImageProcessor.ComputeAnnotationDirtyRect(size.Width, size.Height, a, adj))
            Next
            Return rect
        End Function

        Private Function TryRenderAnnotationPatchSync() As Boolean
            Dim previewSource = GetPreviewSource()
            If previewSource Is Nothing Then Return False

            Dim rect = CandidateAnnotationDirtyRect()
            If rect.IsEmpty Then Return False
            If RequiresFullRenderForStackedCorrections(rect) Then Return False
            If _sceneSk Is Nothing Then Return False

            ' STUFE 2: ASYNCHRON einreihen statt synchron zu rendern - der Worker haelt waehrend
            ' langer Renders (grosse Striche/Effekte) den Base-Cache-Lock; ein synchroner Versuch
            ' liefe mit TryEnter(12ms) ins Leere, die Retries erschoepften sich und der finale
            ' Stand (z.B. der Text nach dem Aufziehen) wuerde NIE gebacken. Die Pending-Union und
    ' der Versions-/Placement-Guard des Workers stellen die richtige Endfassung sicher.
            _annotationDirtyRect = SKRectI.Empty
            RequestSceneRegionRender(rect)
            Return True
        End Function

        ' ── Vorschau der Maskierung beim Schaerfen ──────────────────────────────
        '
        ' ALT gedrueckt halten und den Maskierungsregler ziehen zeigt statt des Bildes die
        ' GEWICHTUNG der Schaerfung: weiss = hier wird voll geschaerft, schwarz = hier bleibt es,
        ' wie es ist. Aus dem Nutzerkreis gewuenscht, und aus einem nachvollziehbaren Grund - ohne
        ' sie stellt man den Regler blind ein.
        '
        ' Sie laeuft ueber den vorhandenen WERKZEUG-KANAL (ToolPreviewImage), denselben, den die
        ' Gitterverzerrung und die Tiefen-Unschaerfe benutzen. Der Kanal bringt zwei Dinge mit, die
        ' eine eigene Weiche vor der Anzeige erst haette nachbauen muessen: das Bild darunter wird
        ' ausgeblendet, und das Zoom-Detail bleibt AUS - sonst laege beim Hineinzoomen der scharfe
        ' Ausschnitt des Bildes ueber der Maske, und man saehe die Vorschau ausgerechnet dort nicht,
        ' wo man sie braucht. Wer den Kanal haelt, steht in _vorschauQuelle; geraeumt wird nur der
        ' eigene Halter.
        '
        ' Die KANTENKARTE wird einmal gerechnet und traegt ihren Bildstand als Stempel: sie haengt nur
        ' vom Bild vor der Schaerfung ab, nicht vom Reglerwert. Jede Reglerbewegung kostet deshalb nur
        ' eine Punktrechnung und keinen Kettendurchlauf, und ein zweiter Zug am selben Bild gar nichts.

        ''' <summary>Der Name dieses Halters im Vorschaukanal.</summary>
        Private Const SharpenMaskPreviewHolder As String = "SchaerfeMaske"

        Private _sharpenEdgeMap As Byte() = Nothing
        Private _sharpenEdgeMapWidth As Integer = 0
        Private _sharpenEdgeMapHeight As Integer = 0
        ''' <summary>Der Bildstand, zu dem die Kantenkarte gehoert - siehe ImageProcessor.PreSharpenKey.</summary>
        Private _sharpenEdgeMapKey As String = ""
        ''' <summary>Ob die Vorschau gewuenscht ist - unabhaengig davon, ob ihr Bild schon steht. Die
        ''' Kantenkarte braucht einen Kettendurchlauf; wer ALT nur kurz antippt, hat den Regler
        ''' laengst losgelassen, wenn sie fertig ist. Ohne diese Absicht bliebe das Ergebnis dann
        ''' als Standbild auf der Buehne stehen.</summary>
        Private _sharpenMaskPreviewWanted As Boolean = False
        Private _sharpenMaskPreviewRunning As Boolean = False

        Public ReadOnly Property IsSharpenMaskPreviewActive As Boolean
            Get
                Return _sharpenMaskPreviewWanted AndAlso
                       String.Equals(_vorschauQuelle, SharpenMaskPreviewHolder, StringComparison.Ordinal)
            End Get
        End Property

        ''' <summary>Beginnt die Vorschau: rechnet die Kantenkarte im Hintergrund und zeigt sie an.
        ''' Mehrfach aufzurufen ist harmlos - eine laufende Rechnung wird nicht verdoppelt.</summary>
        Public Sub BeginSharpenMaskPreview()
            If Not Dispatcher.UIThread.CheckAccess() Then
                Dispatcher.UIThread.Post(AddressOf BeginSharpenMaskPreview)
                Return
            End If
            ' EINE SPUR IM PROTOKOLL an jeder Stelle, an der die Vorschau aussteigt. Sie ist ein
            ' Zusammenspiel aus Geste, Werkzeugkanal und Kettendurchlauf; faellt eines davon aus,
            ' sieht der Nutzer nur "es passiert nichts" - und ohne Spur ist nicht zu sagen, welches.
            If _isDocumentLoading OrElse String.IsNullOrEmpty(_currentImagePath) Then
                DiagnosticLogService.LogAlways("Editor.SchaerfeMaske", "nicht gestartet: kein Dokument")
                Return
            End If
            _sharpenMaskPreviewWanted = True

            Dim previewSource = GetPreviewSource()
            If previewSource Is Nothing Then
                DiagnosticLogService.LogAlways("Editor.SchaerfeMaske", "nicht gestartet: keine Vorschauquelle")
                Return
            End If
            Dim adj = GetSceneAdjustments()
            Dim key = ImageProcessor.PreSharpenKey(adj)

            ' Karte schon da und noch gueltig? Dann sofort zeigen. Der Stempel steht auf dem Bild OHNE
            ' Schaerfung: die Schaerfe-Regler selbst entwerten die Karte also nicht, jede andere
            ' Aenderung schon - eine Karte vom Bildstand von vorhin zeigte sonst Kanten, die es
            ' inzwischen nicht mehr gibt.
            If _sharpenEdgeMap IsNot Nothing AndAlso String.Equals(_sharpenEdgeMapKey, key, StringComparison.Ordinal) Then
                PublishSharpenMaskPreview()
                Return
            End If
            If _sharpenMaskPreviewRunning Then Return
            _sharpenMaskPreviewRunning = True
            Dim work = Task.Run(Function()
                                    Dim unsharpened = ImageProcessor.RenderPreSharpenBase(previewSource, adj)
                                    If unsharpened Is Nothing Then Return New SharpenEdgeMapResult(Nothing, 0, 0)
                                    Try
                                        Return New SharpenEdgeMapResult(ImageProcessor.ComputeSharpenEdgeMap(unsharpened),
                                                                        unsharpened.Width, unsharpened.Height)
                                    Finally
                                        unsharpened.Dispose()
                                    End Try
                                End Function)
            work.ContinueWith(Sub(finished)
                                  _sharpenMaskPreviewRunning = False
                                  If finished.IsFaulted OrElse finished.Result.Map Is Nothing Then Return
                                  ' Zwischenzeitlich losgelassen: das Ergebnis ist dann nicht mehr
                                  ' gewollt und darf die Buehne nicht anfassen.
                                  If Not _sharpenMaskPreviewWanted Then Return
                                  _sharpenEdgeMap = finished.Result.Map
                                  _sharpenEdgeMapWidth = finished.Result.Width
                                  _sharpenEdgeMapHeight = finished.Result.Height
                                  _sharpenEdgeMapKey = key
                                  PublishSharpenMaskPreview()
                              End Sub, CancellationToken.None, TaskContinuationOptions.None,
                              TaskScheduler.FromCurrentSynchronizationContext())
        End Sub

        ''' <summary>Rechnet das Anzeigebild zum aktuellen Reglerwert neu. Ohne Karte oder ohne
        ''' Absicht passiert nichts - so darf der Regler-Setter sie bedenkenlos rufen.</summary>
        Public Sub PublishSharpenMaskPreview()
            If Not _sharpenMaskPreviewWanted OrElse _sharpenEdgeMap Is Nothing Then Return
            ' Der Kanal ist einer: haelt ihn ein anderes Werkzeug (Gitterverzerrung,
            ' Tiefen-Unschaerfe), gehoert er ihm. Zwei Vorschauen uebereinander waeren nur eine
            ' Gelegenheit, dass eine haengen bleibt.
            If Not String.IsNullOrEmpty(_vorschauQuelle) AndAlso
               Not String.Equals(_vorschauQuelle, SharpenMaskPreviewHolder, StringComparison.Ordinal) Then
                DiagnosticLogService.LogAlways("Editor.SchaerfeMaske",
                                               $"nicht gezeigt: den Vorschaukanal haelt gerade {_vorschauQuelle}")
                Return
            End If

            Dim maskSk = ImageProcessor.RenderSharpenMaskPreview(_sharpenEdgeMap, _sharpenEdgeMapWidth,
                                                                 _sharpenEdgeMapHeight, SharpenMasking)
            If maskSk Is Nothing Then Return
            Dim image As Bitmap
            Using maskSk
                image = ImageOrientationService.ToAvaloniaBitmapFast(maskSk)
            End Using
            If image Is Nothing Then Return
            ToolPreviewImage = image
            _vorschauQuelle = SharpenMaskPreviewHolder
            Me.RaisePropertyChanged(NameOf(IsSharpenMaskPreviewActive))
            DiagnosticLogService.LogAlways("Editor.SchaerfeMaske",
                                           $"gezeigt: {image.PixelSize.Width}x{image.PixelSize.Height}, Maskierung {SharpenMasking:0}")
        End Sub

        ''' <summary>Beendet die Vorschau und gibt die Buehne wieder frei. Die Kantenkarte BLEIBT
        ''' liegen - sie traegt ihren Bildstand als Stempel und wird beim naechsten Mal entweder
        ''' wiederverwendet oder verworfen. Dasselbe Vorgehen wie bei der Tiefenkarte; ohne es kostete
        ''' jedes erneute Druecken der ALT-Taste einen vollen Kettendurchlauf.</summary>
        Public Sub EndSharpenMaskPreview()
            If Not Dispatcher.UIThread.CheckAccess() Then
                Dispatcher.UIThread.Post(AddressOf EndSharpenMaskPreview)
                Return
            End If
            _sharpenMaskPreviewWanted = False
            If Not String.Equals(_vorschauQuelle, SharpenMaskPreviewHolder, StringComparison.Ordinal) Then Return
            ToolPreviewImage = Nothing
            _vorschauQuelle = ""
            Me.RaisePropertyChanged(NameOf(IsSharpenMaskPreviewActive))
        End Sub

        Private Structure SharpenEdgeMapResult
            Public ReadOnly Map As Byte()
            Public ReadOnly Width As Integer
            Public ReadOnly Height As Integer

            Public Sub New(map As Byte(), width As Integer, height As Integer)
                Me.Map = map
                Me.Width = width
                Me.Height = height
            End Sub
        End Structure
    End Class

End Namespace
