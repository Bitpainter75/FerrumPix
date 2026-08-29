Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports SkiaSharp
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>STEMPEL, VERWISCHEN UND REPARATURPINSEL AUF EINER MARKIERTEN BILD-EBENE statt im
    ''' Foto. Dieselbe Regel wie beim Pinsel: ist genau eine sichtbare Ebene markiert, die ein Bild
    ''' trägt, geht der Zug in DEREN Bild - auch wenn er sie verfehlt. Ein Rückfall aufs Foto wäre
    ''' das Schlimmere; er säße in den falschen Pixeln und fiele erst beim Verschieben der Ebene auf.
    '''
    ''' Drei Unterschiede zum Weg ins Arbeitsbild (<c>EditorViewModelRetouch.vb</c>), und alle drei
    ''' folgen daraus, dass das Ziel eine DATEI ist und kein Arbeitsbild:
    '''
    ''' 1. Die Punkte liegen im Raster des EBENENBILDS, nicht im Arbeitsbild. Umgerechnet wird mit
    '''    derselben Kette wie beim Malen (<c>AnnotationImagePlacement</c>), der Radius mit demselben
    '''    Faktor wie die Pinselbreite - er steht in Anzeigepunkten, so bemisst die Ansicht ihren Ring.
    ''' 2. Während des Zuges zeigt die ORANGE Maske den bearbeiteten Bereich, nicht das Ergebnis. Die
    '''    Live-Puffer des Fotos sind zwei Renderdurchläufe der ganzen Szene; für eine Ebenendatei
    '''    gibt es nichts Entsprechendes, und das Ergebnis kommt beim Loslassen. Denselben Weg nimmt
    '''    das Foto beim Reparaturpinsel und bei großen Radien.
    ''' 3. Rückgängig fällt vom Dateiwechsel ab, wie beim Malen: der Zug schreibt eine neue Datei,
    '''    <c>PushUndo</c> sichert den vorigen Pfad. Es gibt deshalb KEINEN Rückgängig-Punkt beim
    '''    Zugbeginn - der käme sonst doppelt.
    '''
    ''' Objekt entfernen bleibt außen vor: es arbeitet mit einem gelernten Modell und einer eigenen
    ''' Regionenmechanik (siehe <c>Audits/OFFENE_PUNKTE.md</c>).</summary>
    Partial Public Class EditorViewModel

        ''' Die Punkte des laufenden Zuges im Raster des EBENENBILDS - sie werden beim Loslassen
        ''' hineingebacken.
        Private ReadOnly _objectRetouchSpots As New List(Of RetouchSpot)()
        ''' Dieselben Punkte im ANZEIGERASTER, allein für die orange Vorschau. Zwei Listen statt einer
        ''' Umrechnung beim Veröffentlichen: die Vorschau läuft je Zeigerbewegung, das Zurückrechnen
        ''' aus dem Ebenenraster wäre dieselbe Arbeit noch einmal.
        Private ReadOnly _objectRetouchDisplaySpots As New List(Of RetouchSpot)()

        ''' Die Ebene, in deren Bild der LAUFENDE Zug geht. Entschieden wird das einmal, beim
        ''' Zugbeginn - ein Punkt mittendrin darf nicht plötzlich ins Foto rutschen.
        Private _objectRetouchTarget As ImageAnnotation
        Private _objectRetouchPlacement As AnnotationImagePlacement

        ''' Versatz des Stempels in ANZEIGEPUNKTEN ("kopiere von so weit links"). Bewusst nicht im
        ''' Ebenenraster: so gilt er unverändert weiter, wenn die nächste Ebene ein anders großes,
        ''' gedrehtes oder gespiegeltes Bild trägt, und die Quellmarke der Ansicht rechnet ohne
        ''' Umweg mit.
        Private _objectCloneOffsetXDisplay As Double = 0
        Private _objectCloneOffsetYDisplay As Double = 0
        Private _objectHasCloneOffset As Boolean = False

        ''' <summary>Nimmt einen Retusche-Punkt für die markierte Bild-Ebene entgegen. True heißt:
        ''' hier behandelt, der Aufrufer nimmt seinen Weg ins Arbeitsbild NICHT mehr.</summary>
        Private Function TryAddObjectRetouchSpot(xPercent As Double, yPercent As Double,
                                                 isStrokeStart As Boolean) As Boolean
            If isStrokeStart Then BeginObjectRetouchStroke()
            Dim target = _objectRetouchTarget
            Dim placement = _objectRetouchPlacement
            If target Is Nothing OrElse placement Is Nothing Then Return False

            ' Der Stempel braucht eine Quelle - ohne sie würde er stillschweigend zum Verwischen.
            If IsCloneMode AndAlso Not HasCloneSource Then Return True

            Dim displaySize = GetAnnotationDisplayPixelSize()
            If displaySize.Width <= 0 OrElse displaySize.Height <= 0 Then Return True
            Dim displayX = xPercent / 100.0 * displaySize.Width
            Dim displayY = yPercent / 100.0 * displaySize.Height

            ' Der Radius steht in ANZEIGEPUNKTEN (so bemisst die Ansicht ihren Ring) - im Bild der
            ' Ebene ist ein Anzeigepunkt je nach Größe des Objekts mehr oder weniger als ein Bildpunkt.
            Dim imageRadius = Math.Max(1.0, _retouchRadius * placement.ImagePixelsPerDisplayPixel)
            Dim imagePoint = placement.DisplayToImage(displayX, displayY)

            ' VERFEHLT die Ebene: der Punkt tut nichts. Weiter als einen Radius neben dem Bild
            ' berührt der Kreis es ohnehin nicht mehr, und ohne diese Grenze klemmten die
            ' Zeichenwege den Mittelpunkt auf die Kante - ein Zug NEBEN der Ebene verschmierte
            ' dann ihren Rand.
            If imagePoint.X < -imageRadius OrElse imagePoint.Y < -imageRadius OrElse
               imagePoint.X > placement.ImageWidth + imageRadius OrElse
               imagePoint.Y > placement.ImageHeight + imageRadius Then Return True

            Dim isHeal = _isRepairMode AndAlso Not _isCloneMode
            Dim spot = New RetouchSpot With {
                .XPixels = CSng(imagePoint.X),
                .YPixels = CSng(imagePoint.Y),
                .RadiusPixels = CSng(imageRadius),
                .StrengthPercent = CSng(_brushHardness),
                .OpacityPercent = CSng(_brushOpacity),
                .FlowPercent = CSng(_brushFlow),
                .Mode = If(isHeal, "Heal", "Blur"),
                .StrokeId = If(isHeal, _activeRetouchStrokeId, 0)
            }

            Dim sourceDisplayX As Double = -1
            Dim sourceDisplayY As Double = -1
            If IsCloneMode AndAlso HasCloneSource Then
                If Not _objectHasCloneOffset Then
                    _objectCloneOffsetXDisplay = displayX - _cloneSourceXPercent / 100.0 * displaySize.Width
                    _objectCloneOffsetYDisplay = displayY - _cloneSourceYPercent / 100.0 * displaySize.Height
                    _objectHasCloneOffset = True
                End If
                Dim candidateX = displayX - _objectCloneOffsetXDisplay
                Dim candidateY = displayY - _objectCloneOffsetYDisplay
                Dim source = placement.DisplayToImage(candidateX, candidateY)
                ' Wandert die Quelle aus dem Ebenenbild, bleibt der Punkt ohne sie und fällt auf den
                ' Ringmittelwert zurück - dieselbe Regel wie im Foto.
                If source.X >= 0 AndAlso source.Y >= 0 AndAlso
                   source.X <= placement.ImageWidth AndAlso source.Y <= placement.ImageHeight Then
                    spot.SourceXPixels = source.X
                    spot.SourceYPixels = source.Y
                    sourceDisplayX = candidateX
                    sourceDisplayY = candidateY
                End If
            End If

            ' Erst der erste Punkt, der die Ebene wirklich trifft, schreibt in die Historie - ein Zug
            ' NEBEN der Ebene tut nichts und soll dort auch nichts hinterlassen.
            If _objectRetouchSpots.Count = 0 Then
                NameHistoryStep(If(IsCloneMode, "Stempeln", If(IsRepairMode, "Reparatur", "Verwischen")))
            End If
            _objectRetouchSpots.Add(spot)
            ' Der Zwilling im Anzeigeraster trägt ALLES mit, was das Ergebnis ausmacht - Stärke,
            ' Deckkraft, Fluss und die Klonquelle. Ohne die Quelle wäre der Stempel in der Ansicht
            ' ein Verwischen, und die Live-Ansicht zeigte etwas anderes als das Ergebnis.
            _objectRetouchDisplaySpots.Add(New RetouchSpot With {
                .XPixels = CSng(displayX),
                .YPixels = CSng(displayY),
                .RadiusPixels = CSng(Math.Max(1.0, _retouchRadius)),
                .StrengthPercent = spot.StrengthPercent,
                .OpacityPercent = spot.OpacityPercent,
                .FlowPercent = spot.FlowPercent,
                .Mode = spot.Mode,
                .StrokeId = spot.StrokeId,
                .SourceXPixels = CSng(sourceDisplayX),
                .SourceYPixels = CSng(sourceDisplayY)
            })
            _hasChanges = True
            PublishObjectRetouchPreview(_objectRetouchDisplaySpots(_objectRetouchDisplaySpots.Count - 1), isStrokeStart)
            Return True
        End Function

        ''' <summary>Die Ansicht während des Zuges: das ECHTE Ergebnis, wenn ein Live-Puffer steht,
        ''' sonst die orange Maske über dem bearbeiteten Bereich.
        '''
        ''' Dieselbe Abstufung wie im Foto, und aus denselben Gründen: der Reparaturpinsel und sehr
        ''' große Radien kosten je Zeigerbewegung zu viel und bekommen die Maske; solange die Puffer
        ''' noch gebaut werden, überbrückt sie ebenfalls.</summary>
        Private Sub PublishObjectRetouchPreview(displaySpot As RetouchSpot, force As Boolean)
            If displaySpot Is Nothing Then Return
            Dim displayW = Math.Max(1, DisplayImageWidthPixels)
            Dim displayH = Math.Max(1, DisplayImageHeightPixels)

            ' Der REPARATURPINSEL bekommt gar keine Live-Ansicht: seine Kandidatensuche kostet je
            ' Zeigerbewegung zu viel. Für ihn werden auch keine Puffer gebaut - das wäre ein Decode
            ' für nichts.
            Dim istReparatur = String.Equals(displaySpot.Mode, "Heal", StringComparison.OrdinalIgnoreCase)
            Dim liveTaugt = _objectRetouchLiveReady AndAlso _retouchLiveBitmap IsNot Nothing AndAlso
                            Not istReparatur AndAlso
                            displaySpot.RadiusPixels * RetouchPreviewRadiusScale() <= 240
            If Not liveTaugt Then
                ' Die Puffer entstehen im Hintergrund; bis dahin (und bei sehr großen Radien) zeigt
                ' die Maske, woran gearbeitet wird.
                If Not istReparatur Then BeginObjectRetouchLiveBuffersAsync()
                PublishObjectRetouchMaskPreview(force, displaySpot)
                Return
            End If

            ResetRetouchLivePatchForBitmap(_retouchLiveBitmap)
            ImageProcessor.ApplyRetouchSpotInPlace(_retouchLiveBitmap, _retouchLiveBitmap, displaySpot, displayW, displayH)
            ExpandRetouchLivePatchRect(displaySpot, displayW, displayH)
            PublishRetouchLivePreview(force, markPreviewPending:=False)
        End Sub

        ''' <summary>Zugbeginn: Ziel und Einpassung feststellen und die Punkte des vorigen Zuges
        ''' wegräumen. Ohne Ziel bleibt <c>_objectRetouchTarget</c> leer, und der Aufrufer nimmt
        ''' seinen Weg ins Arbeitsbild.</summary>
        Private Sub BeginObjectRetouchStroke()
            ' Ein noch offener Zug auf einer Ebene wird zuerst abgeschlossen. Ohne das fiele er weg,
            ' ohne je im Bild zu landen - und genau das passiert, wenn ein Druck ankommt, bevor das
            ' Loslassen des vorigen durch ist.
            If _retouchStrokeActive AndAlso _objectRetouchTarget IsNot Nothing Then TryCommitObjectRetouchStroke()
            _objectRetouchSpots.Clear()
            _objectRetouchDisplaySpots.Clear()
            _objectRetouchPlacement = Nothing
            _objectRetouchTarget = FindStrokeTargetImageAnnotation()
            If _objectRetouchTarget Is Nothing Then Return

            Dim sourcePath = ""
            If Not BeginObjectImageEdit(_objectRetouchTarget, sourcePath) Then
                _objectRetouchTarget = Nothing
                Return
            End If
            _objectRetouchPlacement = BuildAnnotationImagePlacement(_objectRetouchTarget,
                                                                   _objectPaintSize.Width, _objectPaintSize.Height)
            If _objectRetouchPlacement Is Nothing Then
                _objectRetouchTarget = Nothing
                Return
            End If

            ' KEIN PushUndo hier: der Rückgängig-Punkt entsteht beim Übernehmen des Ergebnisses
            ' (ApplyObjectPaintResult), und zwar mit dem vorigen Dateipfad.
            _retouchStrokeActive = True
            RaiseSaveAvailabilityChanged()
            _activeRetouchStrokeId = _nextRetouchStrokeId
            _nextRetouchStrokeId += 1
            _retouchLivePatchRect = SKRectI.Empty
            ClearRetouchLivePatch()
        End Sub

        ' ── Die Live-Ansicht auf der Ebene ────────────────────────────────────────────────
        '
        ' Sie benutzt DIESELBEN Puffer wie das Foto (Ziel, Probe, Flickenrechteck) und damit auch
        ' dasselbe Veröffentlichen und dieselbe Drosselung. Der einzige Unterschied ist, WAS im Ziel
        ' steht: beim Foto die gerenderte Szene, hier die EBENE ALLEIN, in den Anzeigeraum gezeichnet.
        '
        ' Das ist der Punkt, an dem sich entscheidet, ob die Vorschau die Wahrheit sagt. Die Szene zu
        ' nehmen wäre billiger und sähe im Zug sogar richtiger aus - aber der Stempel läse dann aus
        ' dem FOTO, und auf einer durchsichtigen Malebene erschiene beim Ziehen etwas, das nach dem
        ' Loslassen nicht da ist. Die Ebene allein liest genau das, was auch der Commit liest.
        '
        ' Der Flicken trägt nur die GEÄNDERTEN Punkte (RenderChangedBitmapPatch räumt alles weg, was
        ' der Probe gleicht) - deshalb scheint die Szene überall dort weiter durch, wo nichts
        ' passiert ist, und die Ebene liegt nicht ein zweites Mal über sich selbst.

        ''' Stand, zu dem die Live-Puffer passen: Datei der Ebene, Anzeigemaße und Einpassung. Leer
        ''' heißt, die Puffer gehören nicht (mehr) einer Ebene.
        Private _objectRetouchBufferStamp As String = ""
        Private _objectRetouchLiveReady As Boolean = False
        Private _objectRetouchBuffersInitializing As Boolean = False
        ''' Sequenznummer des Aufbaus, der gerade läuft. Sie entscheidet, ob ein neuer Anlauf nötig
        ''' ist: ein laufender Aufbau, dessen Nummer nicht mehr die aktuelle ist, wurde bereits
        ''' entwertet und wird verworfen, wenn er ankommt.
        Private _objectRetouchBuffersPendingSeq As Long = -1

        ''' <summary>Gibt es die Live-Ansicht für diese Ebene? Nur bei VOLLER Deckkraft und normaler
        ''' Mischmethode: der Flicken trägt die Pixel der Ebene, und über eine halbdurchsichtige oder
        ''' gemischte Ebene gelegt läge sie ein zweites Mal über sich selbst - der Zug sähe kräftiger
        ''' aus, als er ist. Dann bleibt es bei der orangen Maske, die nichts über Farben behauptet.</summary>
        Private Shared Function ObjectRetouchLivePreviewAllowed(target As ImageAnnotation) As Boolean
            If target Is Nothing Then Return False
            If target.Opacity < 99.5F Then Return False
            Dim blend = If(target.BlendMode, "").Trim()
            Return blend.Length = 0 OrElse blend.Equals("Normal", StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function ObjectRetouchBufferStamp(sourcePath As String, placement As AnnotationImagePlacement,
                                                         displayWidth As Integer, displayHeight As Integer) As String
            If placement Is Nothing Then Return ""
            Dim ci = Globalization.CultureInfo.InvariantCulture
            Return String.Join("|", New String() {
                If(sourcePath, ""),
                displayWidth.ToString(ci), displayHeight.ToString(ci),
                placement.FitRect.Left.ToString(ci), placement.FitRect.Top.ToString(ci),
                placement.FitRect.Right.ToString(ci), placement.FitRect.Bottom.ToString(ci),
                placement.RotationDegrees.ToString(ci),
                placement.FlipHorizontal.ToString(), placement.FlipVertical.ToString()})
        End Function

        ''' <summary>Baut die Live-Puffer für die markierte Ebene auf, falls sie fehlen oder zu einem
        ''' anderen Stand gehören. Der Aufbau läuft im HINTERGRUND: er dekodiert das Ebenenbild, und
        ''' das kostet bei einem eingefügten Foto zu viel für einen Zeigerdruck.
        '''
        ''' Gerufen wird das aus zwei Richtungen: beim Vorwärmen (Werkzeugwechsel, Alt-Klick - dort
        ''' läuft noch kein Zug, das Ziel wird deshalb selbst bestimmt) und aus dem laufenden Zug,
        ''' solange die Puffer noch fehlen.</summary>
        Private Sub BeginObjectRetouchLiveBuffersAsync()
            ' Ein laufender Aufbau bremst nur, solange er noch GILT. Wurde er zwischendurch entwertet
            ' (jedes Wegwerfen der Puffer zählt die Sequenznummer hoch), muss ein neuer Anlauf her -
            ' sonst bliebe es beim Nichts: der Werkzeugwechsel ruft hier ZWEIMAL herein und wirft
            ' dazwischen die Puffer weg, der erste Aufbau kam also nie an und der zweite fand sich
            ' selbst als "läuft schon" vor. Ergebnis war eine Live-Ansicht, die nie erschien.
            If _objectRetouchBuffersInitializing AndAlso
               _objectRetouchBuffersPendingSeq = _retouchBuffersInitSeq Then Return
            Dim target = If(_objectRetouchTarget, FindStrokeTargetImageAnnotation())
            If target Is Nothing OrElse Not ObjectRetouchLivePreviewAllowed(target) Then Return

            Dim sourcePath = ""
            If Not BeginObjectImageEdit(target, sourcePath) Then Return
            Dim placement = If(_objectRetouchTarget IsNot Nothing AndAlso _objectRetouchPlacement IsNot Nothing,
                               _objectRetouchPlacement,
                               BuildAnnotationImagePlacement(target, _objectPaintSize.Width, _objectPaintSize.Height))
            If placement Is Nothing Then Return

            Dim displayWidth = Math.Max(1, DisplayImageWidthPixels)
            Dim displayHeight = Math.Max(1, DisplayImageHeightPixels)
            Dim stamp = ObjectRetouchBufferStamp(sourcePath, placement, displayWidth, displayHeight)
            If _objectRetouchLiveReady AndAlso _retouchLiveBitmap IsNot Nothing AndAlso
               String.Equals(_objectRetouchBufferStamp, stamp, StringComparison.Ordinal) Then Return

            _objectRetouchBuffersInitializing = True
            RunObjectRetouchBufferInit(sourcePath, placement, displayWidth, displayHeight, stamp)
        End Sub

        ''' <summary>Der Aufbau selbst: rechnen im Hintergrund, übernehmen auf dem UI-Faden.
        '''
        ''' Bewusst ÜBER EINEN AUFTRAG AN DEN UI-FADEN und nicht über Async/Await - derselbe Weg, den
        ''' auch die Warteschlange der Ebenenbilder geht (<c>EnqueueObjectImageEdit</c>). Ein
        ''' <c>Await</c> hängt an einem Ausführungskontext, der hier nicht immer da ist: im Prüfstand
        ''' kehrte die Fortsetzung nie zurück, der Aufbau blieb für immer "läuft gerade", und ab da
        ''' gab es überhaupt keine Live-Ansicht mehr. Ein Async Sub verschluckt dabei auch noch jede
        ''' Ausnahme.</summary>
        Private Sub RunObjectRetouchBufferInit(sourcePath As String, placement As AnnotationImagePlacement,
                                               displayWidth As Integer, displayHeight As Integer, stamp As String)
            ' Dieselbe Sequenznummer wie beim Foto: ein Bildwechsel oder ein anderes Ziel macht einen
            ' laufenden Aufbau ungültig, egal aus welcher Richtung er kam.
            Dim seq = Threading.Interlocked.Increment(_retouchBuffersInitSeq)
            _objectRetouchBuffersPendingSeq = seq
            ' So fein wie die Vorschau des Fotos - eine schärfere Live-Ansicht als das Bild darunter
            ' wäre verschenkte Rechenzeit.
            Dim scale = Math.Min(1.0, CDbl(RetouchPreviewRadiusScale()))
            Dim bufferWidth = Math.Max(1, CInt(Math.Round(displayWidth * scale)))
            Dim bufferHeight = Math.Max(1, CInt(Math.Round(displayHeight * scale)))

            Task.Run(
                Sub()
                    Dim liveTarget As SKBitmap = Nothing
                    Dim sample As SKBitmap = Nothing
                    Try
                        liveTarget = BuildAnnotationDisplayBuffer(sourcePath, placement,
                                                                  bufferWidth, bufferHeight,
                                                                  displayWidth, displayHeight)
                        sample = liveTarget?.Copy()
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Editor.ObjectRetouchInit", ex)
                        liveTarget?.Dispose()
                        sample?.Dispose()
                        liveTarget = Nothing
                        sample = Nothing
                    End Try
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        Sub()
                            Try
                                AdoptObjectRetouchBuffers(seq, liveTarget, sample, stamp, displayWidth, displayHeight)
                            Finally
                                ' Nur der JÜNGSTE Aufbau gibt die Bremse wieder frei - ein überholter
                                ' darf einen laufenden nicht als beendet melden.
                                If _objectRetouchBuffersPendingSeq = seq Then _objectRetouchBuffersInitializing = False
                            End Try
                        End Sub)
                End Sub)
        End Sub

        ''' Übernimmt die fertigen Puffer - auf dem UI-Faden, wie alles am Anzeigezustand.
        Private Sub AdoptObjectRetouchBuffers(seq As Long, liveTarget As SKBitmap, sample As SKBitmap,
                                              stamp As String, displayWidth As Integer, displayHeight As Integer)
            If seq <> _retouchBuffersInitSeq OrElse liveTarget Is Nothing OrElse sample Is Nothing Then
                liveTarget?.Dispose()
                sample?.Dispose()
                Return
            End If

            DisposeRetouchLiveBuffers(keepInitSeq:=True)
            _retouchLiveBitmap = liveTarget
            _retouchLiveSampleBitmap = sample
            ' KEIN Schlüssel des Fotos: die Puffer gehören einer Ebene, und der Foto-Weg muss sie
            ' als "passt nicht" erkennen und wegwerfen, sobald wieder ins Bild retuschiert wird.
            _retouchBuffersKey = Nothing
            _objectRetouchBufferStamp = stamp
            _objectRetouchLiveReady = True
            ResetRetouchLivePatchForBitmap(_retouchLiveBitmap)

            ' Die Punkte, die während des Aufbaus aufgelaufen sind, nachziehen - sonst fehlte der
            ' Anfang des Zuges in der Ansicht. Reparaturpunkte bleiben aussen vor, die zeigt die Maske.
            If Not _retouchStrokeActive OrElse _objectRetouchDisplaySpots.Count = 0 Then Return
            Dim pending = _objectRetouchDisplaySpots.
                Where(Function(s) s IsNot Nothing AndAlso
                                  Not String.Equals(s.Mode, "Heal", StringComparison.OrdinalIgnoreCase)).ToList()
            If pending.Count = 0 Then Return
            ImageProcessor.ApplyRetouchSpotsInPlace(_retouchLiveBitmap, _retouchLiveBitmap, pending,
                                                    displayWidth, displayHeight)
            For Each s In pending
                ExpandRetouchLivePatchRect(s, displayWidth, displayHeight)
            Next
            PublishRetouchLivePreview(True, markPreviewPending:=False)
        End Sub

        ''' <summary>Zeichnet die Ebene in ein Raster des ANZEIGERAUMS - dieselbe Kette wie in
        ''' <c>ImageToDisplay</c>, nur als Leinwand-Transformation (siehe
        ''' <c>BuildAnnotationConfineMask</c>): erst die Drehung, dann die Spiegelungen, zuletzt die
        ''' Einpassung. Läuft im HINTERGRUND (hier steht der Decode) und fasst deshalb nichts am
        ''' ViewModel an.</summary>
        Private Shared Function BuildAnnotationDisplayBuffer(sourcePath As String, placement As AnnotationImagePlacement,
                                                             bufferWidth As Integer, bufferHeight As Integer,
                                                             displayWidth As Integer, displayHeight As Integer) As SKBitmap
            If placement Is Nothing OrElse bufferWidth <= 0 OrElse bufferHeight <= 0 Then Return Nothing
            If displayWidth <= 0 OrElse displayHeight <= 0 Then Return Nothing
            Dim buffer As SKBitmap = Nothing
            Try
                Using decoded = SKBitmap.Decode(sourcePath)
                    If decoded Is Nothing OrElse decoded.Width <= 0 OrElse decoded.Height <= 0 Then Return Nothing
                    buffer = New SKBitmap(bufferWidth, bufferHeight, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(buffer)
                        canvas.Clear(SKColors.Transparent)
                        canvas.Scale(CSng(bufferWidth / CDbl(displayWidth)), CSng(bufferHeight / CDbl(displayHeight)))
                        If Math.Abs(placement.RotationDegrees) > 0.001 Then
                            canvas.RotateDegrees(CSng(placement.RotationDegrees),
                                                 CSng(placement.CenterX), CSng(placement.CenterY))
                        End If
                        If placement.FlipHorizontal Then
                            canvas.Translate(CSng(2.0 * placement.CenterX), 0)
                            canvas.Scale(-1, 1)
                        End If
                        If placement.FlipVertical Then
                            canvas.Translate(0, CSng(2.0 * placement.CenterY))
                            canvas.Scale(1, -1)
                        End If
                        Using image = SKImage.FromBitmap(decoded)
                            canvas.DrawImage(image, placement.FitRect,
                                             New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), Nothing)
                        End Using
                    End Using
                End Using
                Return buffer
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.ObjectRetouchBuffer", ex)
                buffer?.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Nach dem Einbacken: die Probe auf den neuen Stand ziehen, damit der NÄCHSTE Zug
        ''' sofort wieder live ist. Ohne das verlöre jede Ebene ihre Live-Ansicht ab dem zweiten Zug -
        ''' derselbe Befund, der beim Stempel im Foto schon einmal dahinterstand.
        '''
        ''' Nach einer REPARATUR wird weggeworfen: das Ziel zeigt den ungeheilten Stand, weil die
        ''' Heilung nur beim Einbacken läuft.</summary>
        Private Sub RefreshObjectRetouchBuffersAfterCommit(target As ImageAnnotation, newPath As String,
                                                           newStamp As String, hadHeal As Boolean)
            If Not _objectRetouchLiveReady Then Return
            If hadHeal OrElse _retouchLiveBitmap Is Nothing OrElse target Is Nothing OrElse
               Not _annotations.Contains(target) OrElse
               Not String.Equals(target.ImagePath, newPath, StringComparison.Ordinal) Then
                DisposeRetouchLiveBuffers()
                Return
            End If
            Dim refreshed = _retouchLiveBitmap.Copy()
            If refreshed Is Nothing Then
                DisposeRetouchLiveBuffers()
                Return
            End If
            _retouchLiveSampleBitmap?.Dispose()
            _retouchLiveSampleBitmap = refreshed
            _objectRetouchBufferStamp = newStamp
        End Sub

        ''' Die orange Maske über dem bearbeiteten Bereich - dieselbe Anzeige, die das Foto beim
        ''' Reparaturpinsel und bei großen Radien zeigt.
        Private Sub PublishObjectRetouchMaskPreview(force As Boolean, Optional newest As RetouchSpot = Nothing)
            If _objectRetouchDisplaySpots.Count = 0 Then Return
            If Not EnsureRetouchMaskPreviewSize() Then Return
            If newest Is Nothing Then newest = _objectRetouchDisplaySpots(_objectRetouchDisplaySpots.Count - 1)
            ExpandRetouchMaskPatchRect(newest)
            PublishRetouchMaskPreview(force, newest)
        End Sub

        ''' <summary>Schließt einen Zug auf einer Ebene ab. True heißt: hier behandelt. Der schwere
        ''' Teil (dekodieren, rechnen, schreiben) läuft im Hintergrund, in DERSELBEN Kette wie das
        ''' Malen - ein Strich und eine Retusche auf derselben Ebene dürfen sich nicht überholen.</summary>
        Private Function TryCommitObjectRetouchStroke() As Boolean
            Dim target = _objectRetouchTarget
            If target Is Nothing Then Return False

            _retouchStrokeActive = False
            RaiseSaveAvailabilityChanged()
            Dim spots = _objectRetouchSpots.Select(Function(s) s.Clone()).ToList()
            Dim placement = _objectRetouchPlacement
            Dim imageWidth = If(placement Is Nothing, 0, placement.ImageWidth)
            Dim imageHeight = If(placement Is Nothing, 0, placement.ImageHeight)
            ' Ob in diesem Zug REPARIERT wurde, entscheidet über die Live-Puffer danach: die Heilung
            ' läuft erst beim Einbacken, das Ziel zeigt also weiter den ungeheilten Stand.
            Dim hadHeal = spots.Any(Function(s) Not s.HasCloneSource AndAlso
                                                String.Equals(s.Mode, "Heal", StringComparison.OrdinalIgnoreCase))
            _objectRetouchSpots.Clear()
            _objectRetouchDisplaySpots.Clear()
            _objectRetouchTarget = Nothing
            _objectRetouchPlacement = Nothing
            If spots.Count = 0 OrElse imageWidth <= 0 OrElse imageHeight <= 0 Then
                ClearRetouchLivePatch()
                Return True
            End If

            Dim sourcePath = ""
            If Not BeginObjectImageEdit(target, sourcePath) Then
                ClearRetouchLivePatch()
                Return True
            End If
            Dim targetPath = CreateSelectionAssetTempPath("retouch")
            _objectPaintNextSource = targetPath
            Dim newStamp = ObjectRetouchBufferStamp(targetPath, placement,
                                                    Math.Max(1, DisplayImageWidthPixels),
                                                    Math.Max(1, DisplayImageHeightPixels))
            ' Die Ansicht des Zuges (Live-Flicken oder orange Maske) bleibt stehen, bis das Ergebnis
            ' da ist - sie ist die Brücke über die Zeit, die Dekodieren und Rechnen kosten. Ohne sie
            ' sähe man dazwischen den alten Stand.
            Dim lockTransparent = target.LockTransparentPixels
            EnqueueObjectImageEdit(target, targetPath, LocalizationService.T("Retusche fehlgeschlagen"),
                                   Function() RetouchObjectImageToFile(sourcePath, targetPath, spots, imageWidth, imageHeight, lockTransparent),
                                   Nothing,
                                   Sub()
                                       ClearRetouchLivePatch()
                                       RefreshObjectRetouchBuffersAfterCommit(target, targetPath, newStamp, hadHeal)
                                   End Sub)
            Return True
        End Function

        ''' <summary>Der schwere Teil, im Hintergrund: Ebenenbild dekodieren, die Punkte des Zuges
        ''' hineinrechnen, Ergebnis als neue Datei schreiben.
        '''
        ''' Gerechnet wird in einer Arbeitskopie in Bgra8888/Premul - derselbe Grund wie beim Malen:
        ''' ein JPEG kommt ohne Alphakanal herein, und die Zeichenwege der Retusche mischen mit
        ''' Alpha. Quelle und Ziel sind DASSELBE Bild: Stempel, Verwischen und Reparatur sollen den
        ''' bereits bearbeiteten Stand sehen, sonst holte der nächste Punkt die eben ersetzte Textur
        ''' zurück.</summary>
        Private Shared Function RetouchObjectImageToFile(sourcePath As String, targetPath As String,
                                                         spots As List(Of RetouchSpot),
                                                         imageWidth As Integer, imageHeight As Integer,
                                                         lockTransparent As Boolean) As Boolean
            Using decoded = SKBitmap.Decode(sourcePath)
                If decoded Is Nothing OrElse decoded.Width <= 0 OrElse decoded.Height <= 0 Then Return False
                Using copy = New SKBitmap(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(copy)
                        canvas.Clear(SKColors.Transparent)
                        Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                            canvas.DrawBitmap(decoded, 0, 0, paint)
                        End Using
                    End Using
                    ' Die Punkte liegen im Raster, in dem sie entstanden sind; weicht die dekodierte
                    ' Größe davon ab, rechnet ApplyRetouchSpotsInPlace sie selbst um.
                    ImageProcessor.ApplyRetouchSpotsInPlace(copy, copy, spots, imageWidth, imageHeight)
                    ' Gesperrte Transparenz: der Stempel darf Deckung nicht in durchsichtiges Gebiet
                    ' tragen. Der Ausschnitt ist hier das ganze Bild - ein Zug kann überall liegen.
                    If lockTransparent Then
                        Dim whole = New SKRectI(0, 0, copy.Width, copy.Height)
                        Using before = ImageProcessor.CopyRegion(decoded, whole)
                            If before Is Nothing Then Return False
                            If Not ApplyTransparencyLock(copy, before, decoded, whole) Then Return False
                        End Using
                    End If
                    Return WriteObjectPaintFile(copy, targetPath)
                End Using
            End Using
        End Function

        ''' <summary>OBJEKT ENTFERNEN aus dem Bild der markierten Ebene. True heißt: hier behandelt,
        ''' der Aufrufer nimmt seinen Weg ins Arbeitsbild nicht mehr.
        '''
        ''' Die AUSWAHL sagt, was weg soll - wie im Foto. Umgerechnet wird sie mit derselben Kette
        ''' wie ein Pinselstrich in das Raster der Ebene; das ist genau die Deckung, die auch
        ''' <c>Entf</c> ausstanzt, nur wird sie hier nicht zu einem Loch, sondern zum Auftrag an das
        ''' Modell.
        '''
        ''' Drei Unterschiede zum Weg ins Arbeitsbild:
        ''' - **Außerhalb der Maske bleibt JEDER Punkt unverändert.** Das Modell rechnet über das
        '''   ganze Bild und gibt ein ganzes Bild zurück; im Foto wird es unbesehen übernommen, weil
        '''   dort ohnehin alles undurchsichtig ist. Eine Ebene hat einen Alphakanal - würde das
        '''   Ergebnis unbesehen übernommen, wäre eine durchsichtige Ebene danach überall gefüllt.
        ''' - **Kein Vermerk im Rezept** (<c>BakedOperation</c>): der ist dafür da, das Entfernen an
        '''   einem RAW nachzuziehen. Hier stehen die Pixel in der Datei der Ebene, und die wird
        '''   mitgespeichert.
        ''' - **Rückgängig fällt vom Dateiwechsel ab**, es braucht also keinen Vorher-Flicken.</summary>
        Private Function TryRemoveObjectFromImageAnnotation() As Boolean
            Dim target = FindStrokeTargetImageAnnotation()
            If target Is Nothing Then Return False
            ' Kein zweiter Lauf, solange der erste rechnet. Der Schleier braucht eine Viertelsekunde,
            ' bis er sperrt - ein schneller Doppelklick kaeme also durch, rechnete auf dem Ergebnis
            ' des ersten und raeumte dessen Abbruchmerker weg. True heisst weiterhin "hier behandelt":
            ' ein Rueckfall ins Arbeitsbild waere die falsche Antwort auf "einmal genuegt".
            If _pendingLayerModelRuns > 0 Then Return True

            Dim sourcePath = ""
            If Not BeginObjectImageEdit(target, sourcePath) Then Return False
            Dim placement = BuildAnnotationImagePlacement(target, _objectPaintSize.Width, _objectPaintSize.Height)
            If placement Is Nothing Then Return False

            ' Das ganze Ebenenbild als Region - BuildSelectionCoverage schneidet selbst auf das
            ' zurecht, was die Auswahl überhaupt erreichen kann.
            Dim region = New SKRectI(0, 0, _objectPaintSize.Width, _objectPaintSize.Height)
            Dim anyCoverage = False
            ' MIT Masken: hier ist die Auswahl der AUFTRAG und nicht die Grenze eines Striches. Die
            ' Objektauswahl per Klick liefert eine MASKE, und sie ist der übliche Weg, das zu
            ' Entfernende zu bestimmen - ohne diesen Schalter kam sie auf einer Ebene nie an, und die
            ' Fußzeile behauptete dann, die Auswahl liege daneben.
            Dim coverage = BuildSelectionCoverage(region,
                                                  Function(px, py) CType(placement.ImageToDisplay(px, py), SKPoint?),
                                                  Function(dx, dy) CType(placement.DisplayToImage(dx, dy), SKPoint?),
                                                  anyCoverage, allowMaskSelection:=True)
            ' Deckt die Auswahl nichts von der Ebene, passiert nichts: kein Modelllauf, kein
            ' Rückgängig-Schritt. Ohne Deckung gäbe es gar keinen Auftrag.
            If coverage Is Nothing OrElse Not anyCoverage OrElse region.Width <= 0 OrElse region.Height <= 0 Then
                coverage?.Dispose()
                StatusText = LocalizationService.T("Die Auswahl liegt nicht auf der markierten Ebene.")
                Return True
            End If

            Dim targetPath = CreateSelectionAssetTempPath("removal")
            _objectPaintNextSource = targetPath
            StatusText = LocalizationService.T("Objekt wird entfernt…")
            ' MIT Sperre und Abbruchknopf, wie der Weg ins Arbeitsbild: der Modelllauf dauert bei
            ' einem großen Ebenenbild Sekunden, und ohne Schleier sähe das aus wie ein Hänger. Der
            ' Abbruchmerker ist ein EIGENER (siehe _layerRunCancellation) - der gemeinsame hätte den
            ' eines laufenden Arbeitsbild-Vorgangs verworfen. Die übrigen Schritte auf einer Ebene
            ' (Strich, Radierer, Stempel) sperren weiterhin nichts; sie sind sofort vorbei.
            Dim lockTransparent = target.LockTransparentPixels
            Dim cancel = BeginCancellableLayerRun()
            SetBusyReason(LocalizationService.T("Objekt wird entfernt"))
            EnqueueObjectImageEdit(target, targetPath, LocalizationService.T("Entfernen fehlgeschlagen"),
                                   Function() RemoveObjectFromImageToFile(sourcePath, targetPath, region, coverage, lockTransparent, cancel),
                                   Sub() coverage?.Dispose(),
                                   Sub()
                                       ' Der Fehlschlag hat seine Meldung schon gesetzt; erkennbar ist
                                       ' er daran, dass die Ebene NICHT auf die neue Datei zeigt.
                                       If Not String.Equals(target.ImagePath, targetPath, StringComparison.Ordinal) Then Return
                                       ' Die Auswahl hat ihren Zweck erfüllt - sie stehen zu lassen
                                       ' hiesse, die Ameisenlinie um etwas zu behalten, das nicht mehr
                                       ' da ist. OHNE eigenen Rückgängig-Schritt: der für das
                                       ' Entfernen liegt schon auf dem Stapel und trägt die Pixel.
                                       ClearSelection(captureUndo:=False)
                                       ' Sagt AUSDRÜCKLICH, wo es passiert ist. "Objekt entfernt"
                                       ' allein lässt offen, ob das Foto oder die Ebene getroffen
                                       ' wurde - und genau diese Frage stellt sich hier.
                                       StatusText = LocalizationService.T("Objekt aus der Ebene entfernt")
                                       NameHistoryStep(LocalizationService.T("Objekt aus der Ebene entfernt"))
                                   End Sub,
                                   countsAsBusy:=True,
                                   cancelledMessage:=LocalizationService.T("Entfernen abgebrochen - die Ebene ist unverändert"))
            Return True
        End Function

        ''' <summary>Der schwere Teil, im Hintergrund: Ebenenbild dekodieren, die Deckung der Auswahl
        ''' als Maske übergeben, das Modell füllen lassen und AUSSERHALB der Maske den Vorher-Stand
        ''' wieder einblenden. Genau dieselbe Nachnahme wie beim Malen innerhalb einer Auswahl.
        '''
        ''' <paramref name="cancel"/> geht bis in den Dienst durch. Ein Abbruch wirkt nicht sofort:
        ''' das Füllen ist EIN Modelldurchlauf, der sich nicht anhalten lässt (siehe
        ''' <c>ObjectRemovalService.Fill</c>). Zurück kommt dann False, und die Ebene behält ihre
        ''' bisherige Datei.</summary>
        Private Shared Function RemoveObjectFromImageToFile(sourcePath As String, targetPath As String,
                                                            region As SKRectI, coverage As SKBitmap,
                                                            lockTransparent As Boolean,
                                                            cancel As Threading.CancellationToken) As Boolean
            If coverage Is Nothing Then Return False
            Using decoded = SKBitmap.Decode(sourcePath)
                If decoded Is Nothing OrElse decoded.Width <= 0 OrElse decoded.Height <= 0 Then Return False
                Dim clamped = ClampRectToBitmap(region, decoded.Width, decoded.Height)
                If clamped.Width <= 0 OrElse clamped.Height <= 0 Then Return False

                ' Arbeitskopie in Bgra8888/Premul, wie bei jedem Zug auf einem Ebenenbild: ein JPEG
                ' kommt ohne Alphakanal herein.
                Using copy = New SKBitmap(decoded.Width, decoded.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(copy)
                        canvas.Clear(SKColors.Transparent)
                        Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                            canvas.DrawBitmap(decoded, 0, 0, paint)
                        End Using
                    End Using

                    ' Die Maske muss das GANZE Bild abdecken: eine kleinere würde vom Dienst auf die
                    ' Bildgröße gestreckt, und der Auftrag läge dann woanders.
                    Using mask = BuildFullMaskFromCoverage(coverage, clamped, decoded.Width, decoded.Height)
                        If mask Is Nothing Then Return False
                        Using before = ImageProcessor.CopyRegion(copy, clamped)
                            Using filled = ObjectRemovalService.Fill(copy, mask, cancel)
                                If filled Is Nothing Then Return False
                                Using canvas = New SKCanvas(copy)
                                    canvas.Clear(SKColors.Transparent)
                                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                                        canvas.DrawBitmap(filled, 0, 0, paint)
                                    End Using
                                End Using
                            End Using
                            If before Is Nothing Then Return False
                            If Not ImageProcessor.RestoreOutsideCoverage(copy, before, coverage, clamped) Then Return False
                            ' Gesperrte Transparenz: das Modell füllt frei, die Ebene behält trotzdem
                            ' ihre Form.
                            If lockTransparent Then
                                If Not ApplyTransparencyLock(copy, before, decoded, clamped) Then Return False
                            End If
                        End Using
                    End Using
                    Return WriteObjectPaintFile(copy, targetPath)
                End Using
            End Using
        End Function

        ''' <summary>Die Deckung der Auswahl als Maske über das ganze Ebenenbild - außerhalb schwarz.
        ''' Alpha8 wie die Deckung selbst; der Dienst liest den Alphakanal.</summary>
        Private Shared Function BuildFullMaskFromCoverage(coverage As SKBitmap, region As SKRectI,
                                                          width As Integer, height As Integer) As SKBitmap
            If coverage Is Nothing OrElse width <= 0 OrElse height <= 0 Then Return Nothing
            Dim mask As SKBitmap = Nothing
            Try
                mask = New SKBitmap(width, height, SKColorType.Alpha8, SKAlphaType.Premul)
                Using canvas = New SKCanvas(mask)
                    canvas.Clear(SKColors.Transparent)
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src, .IsAntialias = False}
                        canvas.DrawBitmap(coverage, region.Left, region.Top, paint)
                    End Using
                End Using
                Return mask
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.ObjectRemovalMask", ex)
                mask?.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Die Stelle, aus der der Stempel auf einer EBENE kopiert - in Anzeige-Prozent,
        ''' für die Quellmarke der Ansicht. Nothing heißt „hier ist nichts anders als im Foto",
        ''' dann rechnet der gewöhnliche Weg.</summary>
        Private Function TryGetObjectCloneSamplePercent(xPercent As Double, yPercent As Double) As (X As Double, Y As Double, IsValid As Boolean)?
            If Not _objectHasCloneOffset Then Return Nothing
            If FindStrokeTargetImageAnnotation() Is Nothing Then Return Nothing

            Dim displaySize = GetAnnotationDisplayPixelSize()
            If displaySize.Width <= 0 OrElse displaySize.Height <= 0 Then Return Nothing
            Dim sampleX = xPercent - _objectCloneOffsetXDisplay / displaySize.Width * 100.0
            Dim sampleY = yPercent - _objectCloneOffsetYDisplay / displaySize.Height * 100.0
            If sampleX < 0 OrElse sampleY < 0 OrElse sampleX > 100 OrElse sampleY > 100 Then Return (0, 0, False)
            Return (sampleX, sampleY, True)
        End Function

    End Class

End Namespace
