Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading.Tasks
Imports ReactiveUI
Imports SkiaSharp
Imports FerrumPix.Services

Namespace ViewModels

    ''' <summary>Malen und Radieren AUF EINEM OBJEKT statt im Foto, und die Begrenzung eines Strichs
    ''' auf eine aktive Auswahl.
    '''
    ''' Der Strich geht dorthin, wo die markierte Ebene liegt: trägt sie ein Bild, wird er in dieses
    ''' Bild gebacken; sonst wie bisher ins Arbeitsbild (siehe <c>AddBrushStroke</c>). Das Ergebnis
    ''' landet IMMER in einer neuen Datei im Sitzungsordner des Dokuments - nie in der Quelldatei,
    ''' die das Original des Nutzers sein kann. Rückgängig fällt damit von selbst ab: der Schnappschuss
    ''' von <c>PushUndo</c> trägt den vorigen Pfad, und der zeigt weiter auf die vorige Datei.
    '''
    ''' Die Regeln zu Objekten stehen in <c>Audits/EDITOR_OBJEKTE.md</c>, die zur Auswahl in
    ''' <c>Audits/MASKEN_EBENEN_AUSWAHL.md</c>.</summary>
    Partial Public Class EditorViewModel

        ''' <summary>Speicherdeckel für die Zwischenstände des Objekt-Malens. Jeder Zug legt eine neue
        ''' Datei an; die ältesten fallen weg, sobald die Summe den Deckel reißt - genau wie die
        ''' Vorher-Patches des Arbeitsbilds (<c>WorkingImageService</c>). Ein Rückgängig, das so weit
        ''' zurückreicht, findet seinen Zwischenstand dann nicht mehr; die Datei, auf die das Objekt
        ''' GERADE zeigt, wird nie entfernt.</summary>
        Friend Shared ObjectPaintBudgetBytes As Long = 192L * 1024L * 1024L

        ''' Angelegte Zwischenstände in Reihenfolge ihrer Entstehung (Pfad + Größe).
        Private ReadOnly _objectPaintFiles As New List(Of (Path As String, Bytes As Long))()

        ''' Die Ebene, auf der gerade gemalt wird, samt Maßen ihres Bildes und dem Pfad, auf dem der
        ''' NÄCHSTE Zug aufbaut - der kann noch in der Warteschlange stecken (siehe EnqueueObjectPaint).
        Private _objectPaintTarget As ImageAnnotation
        Private _objectPaintNextSource As String = ""
        Private _objectPaintSize As (Width As Integer, Height As Integer) = (0, 0)
        Private _objectPaintChain As Task = Task.CompletedTask

        ''' <summary>Wie viele MODELLAEUFE gerade in der Warteschlange der Ebenenbilder stecken. Nur
        ''' sie zaehlen in den Beschaeftigt-Zustand (siehe <c>IsBusy</c>); ein Strich, ein
        ''' Radierer-Zug oder ein Stempel bleibt aussen vor.</summary>
        Private _pendingLayerModelRuns As Integer = 0

        Private _nextPaintLayerNumber As Integer = 1

        ''' <summary>Legt eine leere MALEBENE an: ein durchsichtiges Raster in der Größe des
        ''' Anzeigebilds, als oberste Ebene, markiert und im Zeichnen-Werkzeug - man kann also sofort
        ''' loslegen.
        '''
        ''' Sie ist technisch eine Bild-Ebene, und das ist der ganze Trick: Malen, Radieren,
        ''' Retuschieren, Verschieben, Deckkraft, Mischmethode, Ebenenmaske, Duplizieren, Rastern und
        ''' das Einbetten beim Speichern gelten damit ohne eine einzige neue Zeile. Neu ist allein,
        ''' dass sie leer anfängt.
        '''
        ''' Zwei Entscheidungen dahinter:
        ''' - Das Raster hat die volle Größe des Anzeigebilds. Ein kleineres wäre ein Pinselstrich in
        '''   niedrigerer Auflösung, und das sähe man beim Speichern. Der Preis steht in
        '''   <c>Audits/EDITOR_OBJEKTE.md</c>: jeder Zug dekodiert und schreibt dieses Raster.
        ''' - Die Art ist <c>SelectionImage</c> und nicht <c>Image</c>. Nur dort trägt die Zeile den
        '''   eigenen Namen ("Malebene 1") statt eines Dateinamens, und nur dort wird das Raster auf
        '''   das Ebenenrechteck gespannt statt seitenverhältnisgetreu eingepasst - beides ist genau
        '''   das, was eine Malebene braucht.</summary>
        Public Sub AddPaintLayer()
            Dim displaySize = GetAnnotationDisplayPixelSize()
            If displaySize.Width <= 0 OrElse displaySize.Height <= 0 Then Return

            Dim path = CreateSelectionAssetTempPath("layer")
            If Not WriteEmptyPaintLayerFile(displaySize.Width, displaySize.Height, path) Then
                StatusText = LocalizationService.T("Die Malebene konnte nicht angelegt werden.")
                Return
            End If

            PushUndo()
            ' Eine Malebene entsteht nie über ein scharfgestelltes Werkzeug: sie darf weder Kontur
            ' noch Mischmodus noch "ausgeblendet" aus den Puffern eines anderen Objekts erben.
            ResetAnnotationBuffersToImageDefaults()
            Dim stored = DisplayAnnotationRectToStoredPercent("SelectionImage", 0, 0, 100, 100)
            Dim annotation = New ImageAnnotation With {
                .Kind = "SelectionImage",
                .IsPaintLayer = True,
                .Text = LocalizationService.T("Malebene") & " " & _nextPaintLayerNumber.ToString(),
                .ImagePath = path,
                .XPixels = CSng(PercentXToPixels(stored.X)),
                .YPixels = CSng(PercentYToPixels(stored.Y)),
                .WidthPixels = CSng(Math.Max(1.0, PercentXToPixels(stored.Width))),
                .HeightPixels = CSng(Math.Max(1.0, PercentYToPixels(stored.Height))),
                .FillColor = "#00FFFFFF",
                .RotationDegrees = CSng(DisplayAnnotationRotationToStored("SelectionImage", 0)),
                .FlipHorizontal = DisplayAnnotationFlipHorizontalToStored(False),
                .FlipVertical = DisplayAnnotationFlipVerticalToStored(False),
                .IsVisible = True
            }
            _nextPaintLayerNumber += 1
            HardenAnnotationBuffersForNewObject()
            _annotations.Add(annotation)
            ' Das leere Raster zählt beim Speicherdeckel mit: es ist der Stand, auf dem der erste Zug
            ' aufbaut, und danach genauso ein Zwischenstand wie jeder andere.
            RegisterObjectPaintFile(path)
            ' ERST das Werkzeug, dann die Markierung: der Setter entscheidet an beidem, ob er stehen
            ' bleibt (siehe dort), und im Zeichnen-Werkzeug bleibt eine Bild-Ebene stehen. Der PINSEL
            ' und nicht der Radiergummi: auf einer leeren Ebene hätte der nichts zu tun.
            CurrentTool = EditorTool.Draw
            IsEraserMode = False
            SelectedAnnotationIndex = _annotations.Count - 1
            _hasChanges = True
            RaiseResetButtonStateChanged()
            AddHistoryEntry(LocalizationService.T("Malebene angelegt"))
            RefreshPreviewImmediately()
        End Sub

        ''' Ein vollständig durchsichtiges Raster als Startpunkt der Malebene.
        Private Shared Function WriteEmptyPaintLayerFile(width As Integer, height As Integer, path As String) As Boolean
            If width <= 0 OrElse height <= 0 Then Return False
            Try
                Using bitmap = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(bitmap)
                        canvas.Clear(SKColors.Transparent)
                    End Using
                    Return WriteObjectPaintFile(bitmap, path)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.PaintLayer", ex)
                Return False
            End Try
        End Function

        ''' <summary>Liegt eine freie PIXELAUSWAHL an - Laufameisen aus Rechteck, Ellipse, Lasso oder
        ''' Zauberstab? Eine MASKE zählt bewusst nicht dazu: sie gehört einer Ebene, ihr rotes Overlay
        ''' ist in den Malwerkzeugen ohnehin ausgeblendet (<c>CoversMaskOverlay</c>), und eine
        ''' unsichtbare Begrenzung wäre nicht zu erklären.</summary>
        Friend ReadOnly Property HasPixelSelectionScope As Boolean
            Get
                Return _hasActiveSelection AndAlso Not _activeSelectionIsMask AndAlso _editingLayerMaskId = ""
            End Get
        End Property

        ''' <summary>Die markierte Ebene, in deren BILD ein Strich gehört - oder Nothing, wenn der
        ''' Strich wie bisher ins Foto geht. Bedingungen: genau eine markierte Ebene (bei mehreren wäre
        ''' nicht gesagt, welche gemeint ist), sichtbar (auf einer ausgeblendeten Ebene zu malen sieht
        ''' aus, als täte der Pinsel nichts) und mit einer lesbaren Bilddatei. Ein SVG-Objekt gehört
        ''' nicht dazu - es ist Geometrie, und ein Strich darin machte aus ihm ein Rasterbild.</summary>
        Private Function FindStrokeTargetImageAnnotation() As ImageAnnotation
            Dim selected = SelectedAnnotations
            If selected Is Nothing OrElse selected.Count <> 1 Then Return Nothing
            Return If(IsPaintableImageAnnotation(selected(0)), selected(0), Nothing)
        End Function

        ''' <summary>Trägt diese Ebene ein Bild, in das sich malen lässt? Auch die Frage, ob eine
        ''' Markierung den Wechsel in Pinsel und Radierer überlebt (siehe <c>SetPaintMode</c>) und ob
        ''' ein Klick auf ihre Zeile im Zeichnen-Werkzeug stehen bleibt (siehe den Setter von
        ''' <c>SelectedAnnotationIndex</c>) - drei Stellen, eine Bedingung.</summary>
        Friend Shared Function IsPaintableImageAnnotation(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing OrElse Not annotation.IsVisible Then Return False
            Select Case NormalizeAnnotationKind(annotation.Kind)
                Case "Image", "SelectionImage", "Watermark"
                    ' Watermark trägt nur dann ein Bild, wenn ein Pfad gesetzt ist - sonst ist es Text.
                Case Else
                    Return False
            End Select
            Return Not String.IsNullOrWhiteSpace(annotation.ImagePath) AndAlso File.Exists(annotation.ImagePath)
        End Function

        ''' <summary>Wo das Bild eines Objekts auf der Anzeige liegt, und wie man zwischen beiden
        ''' Räumen hin und her rechnet.
        '''
        ''' Die Kette ist dieselbe, die auch der Treffertest benutzt (<c>PointHitsDisplayAnnotationRect</c>):
        ''' Objektrechteck und Drehung kommen aus <c>StoredAnnotationRectToDisplayPercent</c> und
        ''' <c>StoredAnnotationRotationToDisplay</c>, dazu die beiden Spiegelungen. Der Renderer dreht
        ''' ZUERST und spiegelt danach um dieselbe Mitte (<c>DrawAnnotationOnCanvas</c>) - zurück geht
        ''' es deshalb umgekehrt: erst die Drehung herausrechnen, dann spiegeln.</summary>
        Private NotInheritable Class AnnotationImagePlacement
            ''' Einpassung des Bildes im Objektrechteck, in ANZEIGE-Pixeln.
            Public FitRect As SKRect
            ''' Mitte des Objektrechtecks (Dreh- und Spiegelachse), in ANZEIGE-Pixeln.
            Public CenterX As Double
            Public CenterY As Double
            Public RotationDegrees As Double
            Public FlipHorizontal As Boolean
            Public FlipVertical As Boolean
            Public ImageWidth As Integer
            Public ImageHeight As Integer

            ''' <summary>Bildpunkte des Objekts je Anzeigepunkt - gleichmäßig gemittelt, damit ein
            ''' gestrecktes Bild eine einzige Pinselbreite bekommt.</summary>
            Public ReadOnly Property ImagePixelsPerDisplayPixel As Double
                Get
                    If FitRect.Width <= 0 OrElse FitRect.Height <= 0 Then Return 1.0
                    Return Math.Sqrt((ImageWidth / CDbl(FitRect.Width)) * (ImageHeight / CDbl(FitRect.Height)))
                End Get
            End Property

            Public Function DisplayToImage(displayX As Double, displayY As Double) As SKPoint
                Dim localX = displayX, localY = displayY
                If Math.Abs(RotationDegrees) > 0.001 Then
                    Dim radians = -RotationDegrees * Math.PI / 180.0
                    Dim dx = displayX - CenterX
                    Dim dy = displayY - CenterY
                    localX = CenterX + dx * Math.Cos(radians) - dy * Math.Sin(radians)
                    localY = CenterY + dx * Math.Sin(radians) + dy * Math.Cos(radians)
                End If
                If FlipHorizontal Then localX = 2.0 * CenterX - localX
                If FlipVertical Then localY = 2.0 * CenterY - localY
                Return New SKPoint(CSng((localX - FitRect.Left) / FitRect.Width * ImageWidth),
                                   CSng((localY - FitRect.Top) / FitRect.Height * ImageHeight))
            End Function

            Public Function ImageToDisplay(imageX As Double, imageY As Double) As SKPoint
                Dim localX = FitRect.Left + imageX / ImageWidth * FitRect.Width
                Dim localY = FitRect.Top + imageY / ImageHeight * FitRect.Height
                If FlipHorizontal Then localX = 2.0 * CenterX - localX
                If FlipVertical Then localY = 2.0 * CenterY - localY
                If Math.Abs(RotationDegrees) > 0.001 Then
                    Dim radians = RotationDegrees * Math.PI / 180.0
                    Dim dx = localX - CenterX
                    Dim dy = localY - CenterY
                    localX = CenterX + dx * Math.Cos(radians) - dy * Math.Sin(radians)
                    localY = CenterY + dx * Math.Sin(radians) + dy * Math.Cos(radians)
                End If
                Return New SKPoint(CSng(localX), CSng(localY))
            End Function
        End Class

        Private Function BuildAnnotationImagePlacement(annotation As ImageAnnotation,
                                                       imageWidth As Integer, imageHeight As Integer) As AnnotationImagePlacement
            If annotation Is Nothing OrElse imageWidth <= 0 OrElse imageHeight <= 0 Then Return Nothing
            Dim rect = GetAnnotationDisplayPixelRect(annotation)
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return Nothing

            Dim objectRect = New SKRect(CSng(rect.X), CSng(rect.Y),
                                        CSng(rect.X + rect.Width), CSng(rect.Y + rect.Height))
            ' Dieselbe Entscheidung wie im Renderer (DrawAnnotationShape): ohne
            ' Seitenverhältnis-Sperre wird das Bild auf die Objektbox gestreckt, sonst mittig
            ' eingepasst. Eine zweite Formel dafür säße neben dem Zeiger.
            Dim stretchToFill = NormalizeAnnotationKind(annotation.Kind) = "SelectionImage" OrElse Not annotation.LockAspect
            Dim fit = If(stretchToFill, objectRect,
                         ImageProcessor.FitRectKeepingAspectRatio(objectRect, imageWidth, imageHeight))
            If fit.Width <= 0 OrElse fit.Height <= 0 Then Return Nothing

            Return New AnnotationImagePlacement With {
                .FitRect = fit,
                .CenterX = objectRect.MidX,
                .CenterY = objectRect.MidY,
                .RotationDegrees = StoredAnnotationRotationToDisplay(annotation),
                .FlipHorizontal = annotation.FlipHorizontal Xor _appliedFlipH,
                .FlipVertical = annotation.FlipVertical Xor _appliedFlipV,
                .ImageWidth = imageWidth,
                .ImageHeight = imageHeight
            }
        End Function

        ''' <summary>Die markierte Ebene als GRENZE einer Pixelauswahl - der Rückweg zu
        ''' <see cref="BuildSelectionCoverage"/>: dort begrenzt die Auswahl, was auf der Ebene
        ''' passiert, hier begrenzt die Ebene, wo eine Auswahl überhaupt entstehen darf.
        '''
        ''' Auf dem UI-Faden entsteht nur diese BESCHREIBUNG. Das Bild der Ebene zu dekodieren kostet
        ''' bei einem eingefügten Foto zu viel für einen Zeigerdruck; die Deckung baut daraus der
        ''' Worker (<see cref="BuildAnnotationConfineMask"/>).</summary>
        Private NotInheritable Class AnnotationConfinePlan
            ''' Hülle der Ebene im ANZEIGE-Raster. Außerhalb liegt nichts von ihr.
            Public Rect As SKRectI
            ''' Bilddatei der Ebene. Leer heißt: die Ebene trägt kein Bild (Text, Form, SVG), dann
            ''' ist ihr RECHTECK die Grenze.
            Public ImagePath As String = ""
            Public Placement As AnnotationImagePlacement
        End Class

        ''' <summary>Der Bauplan für die markierte Ebene, oder Nothing, wenn es nichts zu begrenzen
        ''' gibt: keine oder mehrere markierte Ebenen (bei mehreren wäre nicht gesagt, welche gemeint
        ''' ist), eine unsichtbare Ebene, oder der Rahmen - dessen Rechteck ist immer das ganze Bild,
        ''' ihn zu begrenzen hieße gar nicht begrenzen.</summary>
        Private Function BuildSelectedAnnotationConfinePlan() As AnnotationConfinePlan
            Dim selected = SelectedAnnotations
            If selected Is Nothing OrElse selected.Count <> 1 Then Return Nothing
            Return BuildAnnotationConfinePlan(selected(0))
        End Function

        ''' <summary>Derselbe Bauplan für eine BESTIMMTE Ebene - für den Weg, der die Form einer Zeile
        ''' als Auswahl lädt, ohne sie dafür zu markieren.</summary>
        Private Function BuildAnnotationConfinePlan(target As ImageAnnotation) As AnnotationConfinePlan
            If target Is Nothing OrElse Not target.IsVisible Then Return Nothing
            If String.Equals(NormalizeAnnotationKind(target.Kind), "Frame", StringComparison.Ordinal) Then Return Nothing

            Dim displaySize = GetAnnotationDisplayPixelSize()
            If displaySize.Width <= 0 OrElse displaySize.Height <= 0 Then Return Nothing
            Dim rect = GetAnnotationDisplayPixelRect(target)
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return Nothing

            Dim placement As AnnotationImagePlacement = Nothing
            Dim imagePath = ""
            If IsPaintableImageAnnotation(target) Then
                ' Nur der KOPF der Datei, nicht der Decode - der gehört in den Worker.
                Dim size = ReadImageSize(target.ImagePath)
                If size.Width > 0 AndAlso size.Height > 0 Then
                    placement = BuildAnnotationImagePlacement(target, size.Width, size.Height)
                    If placement IsNot Nothing Then imagePath = target.ImagePath
                End If
            End If
            If placement Is Nothing Then
                ' Ohne Bild ist das Objektrechteck selbst die Grenze. Ein Raster von 1 mal 1 macht
                ' daraus denselben Fall wie beim Bild: Drehung und Spiegelung liegen schon in der
                ' Einpassung, eine zweite Formel dafür braucht es nicht.
                Dim objectRect = New SKRect(CSng(rect.X), CSng(rect.Y),
                                            CSng(rect.X + rect.Width), CSng(rect.Y + rect.Height))
                placement = New AnnotationImagePlacement With {
                    .FitRect = objectRect,
                    .CenterX = objectRect.MidX,
                    .CenterY = objectRect.MidY,
                    .RotationDegrees = StoredAnnotationRotationToDisplay(target),
                    .FlipHorizontal = target.FlipHorizontal Xor _appliedFlipH,
                    .FlipVertical = target.FlipVertical Xor _appliedFlipV,
                    .ImageWidth = 1,
                    .ImageHeight = 1
                }
            End If

            Dim hull = ComputeConfineHull(placement, displaySize.Width, displaySize.Height)
            If hull.Width <= 0 OrElse hull.Height <= 0 Then Return Nothing
            Return New AnnotationConfinePlan With {.Rect = hull, .ImagePath = imagePath, .Placement = placement}
        End Function

        ''' <summary>RADIEREN AUF EINER EBENE GEHT IN IHRE EBENENMASKE, nicht in ihre Bildpunkte.
        ''' True heißt: hier behandelt.
        '''
        ''' Das ist der Unterschied zwischen "weg" und "nicht mehr zu sehen". Die Pixel bleiben, die
        ''' Maske nimmt die Deckung: der Zug lässt sich mit dem Maskenpinsel zurückholen, die Maske
        ''' verschieben, abstufen, umkehren oder ganz entfernen. Und es geht auf JEDER Ebene, die eine
        ''' Maske tragen kann - auch auf Text, Formen und SVG, die gar kein Raster haben und in die
        ''' man deshalb bisher nicht radieren konnte.
        '''
        ''' Gebaut aus vorhandenen Teilen, keine neue Rechnung: der Strich wird zum weichen
        ''' Alpha8-Stempel wie beim Maskenpinsel, über denselben Weg wie eine eingefrorene Auswahl in
        ''' den QUELLRAUM gebracht und mit <c>ApplyMaskBrushStroke</c> abgezogen - dieselbe Funktion,
        ''' die auch der Maskenpinsel benutzt.
        '''
        ''' NICHT hier landet der Radierer mit gesetzter Hintergrundfarbe: der malt eine Farbe, das
        ''' ist ein gewöhnlicher Strich und gehört in die Bildpunkte.</summary>
        Private Function TryEraseIntoAnnotationMask(target As ImageAnnotation,
                                                    displayPoints As IReadOnlyList(Of Avalonia.Point)) As Boolean
            If target Is Nothing OrElse displayPoints Is Nothing OrElse displayPoints.Count < 2 Then Return False
            Dim displaySize = GetAnnotationDisplayPixelSize()
            If displaySize.Width <= 0 OrElse displaySize.Height <= 0 Then Return False

            Dim pts = displayPoints.
                Select(Function(p) New SKPoint(CSng(p.X / 100.0 * displaySize.Width),
                                               CSng(p.Y / 100.0 * displaySize.Height))).ToList()
            ' Radius und weiche Kante wie beim Zeichnen: der Ring, den die Ansicht zeigt, ist der
            ' Durchmesser, und die Härte bestimmt, wie weit die Kante ausläuft.
            Dim radius = CSng(Math.Max(0.5, _brushSize / 2.0))
            Dim softness = CSng(Math.Max(0.0, radius * (1.0 - Math.Max(0.0, Math.Min(100.0, _brushHardness)) / 100.0)))

            Dim margin = CInt(Math.Ceiling(radius + softness + 2))
            Dim minX = Integer.MaxValue, minY = Integer.MaxValue, maxX = Integer.MinValue, maxY = Integer.MinValue
            For Each p In pts
                minX = Math.Min(minX, CInt(Math.Floor(p.X))) : maxX = Math.Max(maxX, CInt(Math.Ceiling(p.X)))
                minY = Math.Min(minY, CInt(Math.Floor(p.Y))) : maxY = Math.Max(maxY, CInt(Math.Ceiling(p.Y)))
            Next
            Dim rectPx = New SKRectI(Math.Max(0, minX - margin), Math.Max(0, minY - margin),
                                     Math.Min(displaySize.Width, maxX + margin),
                                     Math.Min(displaySize.Height, maxY + margin))
            If rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then Return True

            Dim strokeMask As ImageMask
            Using stamp = ImageProcessor.BuildSoftBrushStampMask(pts, radius, softness, rectPx)
                If stamp Is Nothing Then Return True
                strokeMask = BuildSourceMaskFromDisplayStamp(stamp, rectPx)
            End Using
            If strokeMask Is Nothing Then Return False

            PushUndo()
            Dim mask = EnsureAnnotationLayerMask(target)
            If mask Is Nothing Then Return False
            If Not ImageProcessor.ApplyMaskBrushStroke(mask, strokeMask, subtract:=True) Then Return False

            _hasChanges = True
            RaiseAnnotationMaskStateChanged()
            RebuildLayerRows()
            AddHistoryEntry(LocalizationService.T("Radiert"))
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(target))
            Return True
        End Function

        ''' <summary>Die Ebenenmaske dieser Ebene - vorhandene oder eine neue, die ihren Bereich
        ''' deckt. OHNE eigenen Rückgängig-Punkt und ohne Verlaufseintrag: sie entsteht mitten in
        ''' einem Radierstrich, und der ist EIN Schritt.</summary>
        Private Function EnsureAnnotationLayerMask(target As ImageAnnotation) As ImageMask
            If target Is Nothing Then Return Nothing
            If Not String.IsNullOrEmpty(target.MaskId) Then
                Dim vorhanden = _imageMasks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso
                                                               String.Equals(m.Id, target.MaskId, StringComparison.Ordinal))
                If vorhanden IsNot Nothing Then Return vorhanden
            End If
            Dim maskName = LocalizationService.T("Ebenenmaske")
            Dim mask = CreateObjectCoverageMask(target, maskName)
            If mask Is Nothing Then mask = ImageProcessor.CreateFullCoverageMask(BuildAdjustmentsFromFields(), maskName)
            If mask Is Nothing Then Return Nothing
            _imageMasks.Add(mask)
            target.MaskId = mask.Id
            Return mask
        End Function

        ''' <summary>Ein Alpha8-Stempel aus dem ANZEIGERAUM als Maske im QUELLRAUM. Derselbe Weg, den
        ''' auch eine eingefrorene Auswahl nimmt (<c>CreateSourceMaskFromSelection</c>) - er kennt
        ''' Zuschnitt, Drehung, Spiegelung und Verzerrung. Ein zweiter Weg dorthin liefe
        ''' auseinander.</summary>
        Private Function BuildSourceMaskFromDisplayStamp(stamp As SKBitmap, rectPx As SKRectI) As ImageMask
            If stamp Is Nothing OrElse rectPx.Width <= 0 OrElse rectPx.Height <= 0 Then Return Nothing
            Dim adj = BuildAdjustmentsFromFields()
            If adj Is Nothing Then Return Nothing
            ' Die Auswahlfelder dieser KOPIE tragen den Strich - der Auswahlzustand des Editors
            ' bleibt unangetastet.
            adj.HasActiveSelection = True
            adj.SelectionShapeMode = "MagicWand"
            adj.SelectionShapePointsX = Nothing
            adj.SelectionShapePointsY = Nothing
            adj.SelectionFeatherPixels = 0
            ' Die weiche Kante steckt schon in den Stempelwerten - ein zweites Weichzeichnen wäre
            ' doppelt.
            adj.SelectionMaskSoftBaked = True
            adj.SelectionMaskLeft = rectPx.Left
            adj.SelectionMaskTop = rectPx.Top
            adj.SelectionMaskRight = rectPx.Right
            adj.SelectionMaskBottom = rectPx.Bottom
            adj.SelectionMaskPngBase64 = EncodeMaskBitmapToBase64(stamp)
            If String.IsNullOrEmpty(adj.SelectionMaskPngBase64) Then Return Nothing
            Return ImageProcessor.CreateSourceMaskFromSelection(adj, LocalizationService.T("Radierer"), rectPx)
        End Function

        ''' <summary>Die eigenen Anpassungen einer Ebene EIN- oder AUSSCHALTEN. Sie bleiben dabei
        ''' vollständig erhalten - genau dafür ist der Schalter da: um zu sehen, wie die Ebene ohne
        ''' sie aussieht. Über die ZEILE, nicht über die Markierung, wie das Auge daneben.</summary>
        Public Sub ToggleLayerAdjustments(row As LayerPanelRow)
            If row Is Nothing OrElse row.Annotation Is Nothing Then Return
            If row.Annotation.Adjustments Is Nothing OrElse Not row.Annotation.Adjustments.HasPixelAdjustments() Then Return
            PushUndo()
            row.Annotation.AdjustmentsHidden = Not row.Annotation.AdjustmentsHidden
            _hasChanges = True
            RebuildLayerRows()
            RaiseResetButtonStateChanged()
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(row.Annotation))
        End Sub

        ''' <summary>Die eigenen Anpassungen einer Ebene VERWERFEN. Anders als das Ausschalten ist das
        ''' endgültig (bis zum Rückgängig): die Ebene rendert danach wieder wie unbearbeitet, und das
        ''' Symbol verschwindet aus ihrer Zeile.</summary>
        Public Sub ClearLayerAdjustments(row As LayerPanelRow)
            Dim target = If(row?.Annotation, CurrentObject())
            If target Is Nothing OrElse target.Adjustments Is Nothing Then Return
            If Not target.Adjustments.HasPixelAdjustments() Then Return
            PushUndo()
            target.Adjustments = Nothing
            target.AdjustmentsHidden = False
            _hasChanges = True
            ' Die Regler zeigen die Werte der markierten Ebene - nach dem Verwerfen müssen sie den
            ' neutralen Stand zeigen, sonst stünde dort noch, was es nicht mehr gibt.
            If Object.ReferenceEquals(target, CurrentObject()) Then RefreshObjectAdjustMode()
            RebuildLayerRows()
            RaiseResetButtonStateChanged()
            AddHistoryEntry(LocalizationService.T("Anpassungen der Ebene verworfen"))
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(target))
        End Sub

        ''' <summary>Trägt die markierte Ebene eigene Anpassungen? Der Kontextmenü-Eintrag zum
        ''' Verwerfen hängt daran.</summary>
        Public ReadOnly Property SelectedLayerHasOwnAdjustments As Boolean
            Get
                Dim target = CurrentObject()
                Return target IsNot Nothing AndAlso target.Adjustments IsNot Nothing AndAlso
                       target.Adjustments.HasPixelAdjustments()
            End Get
        End Property

        ''' <summary>Geht ein Strich gerade in das BILD EINER EBENE statt ins Foto? Die Ansicht fragt
        ''' danach, weil der Radiergummi dort etwas anderes freilegt: im Foto entsteht ein echtes Loch
        ''' (Schachbrett), auf einer Ebene kommt das zum Vorschein, was darunter liegt.</summary>
        Public ReadOnly Property PaintsOnImageLayer As Boolean
            Get
                Return FindStrokeTargetImageAnnotation() IsNot Nothing
            End Get
        End Property

        ''' <summary>TRANSPARENTE PUNKTE DER MARKIERTEN EBENE SPERREN. Ein Strich, eine Retusche und
        ''' das Entfernen bleiben dann in der Form, die die Ebene schon hat.
        '''
        ''' MIT Rückgängig-Punkt: der Schalter ändert nicht das Bild, wohl aber, was der nächste Zug
        ''' anrichtet - und er steht im Rezept, gehört also zum Zustand, den ein Schritt zurück
        ''' wiederherstellt.</summary>
        Public Property LayerTransparencyLocked As Boolean
            Get
                Dim target = FindStrokeTargetImageAnnotation()
                Return target IsNot Nothing AndAlso target.LockTransparentPixels
            End Get
            Set(value As Boolean)
                Dim target = FindStrokeTargetImageAnnotation()
                If target Is Nothing OrElse target.LockTransparentPixels = value Then Return
                PushUndo()
                target.LockTransparentPixels = value
                _hasChanges = True
                Me.RaisePropertyChanged(NameOf(LayerTransparencyLocked))
                RaiseResetButtonStateChanged()
            End Set
        End Property

        ''' <summary>DIE FORM DER EBENE ALS AUSWAHL - der Griff, der in üblichen Bildbearbeitungen am
        ''' Ebenenbild hängt (Klick auf die Miniatur mit Taste). True heißt: es gibt jetzt eine
        ''' Auswahl.
        '''
        ''' Sie kommt aus derselben Deckung, mit der eine Auswahl auf eine Ebene BEGRENZT wird
        ''' (<see cref="BuildAnnotationConfineMask"/>) - dieselbe Form, nur andersherum benutzt. Ein
        ''' zweiter Weg zur Form einer Ebene liefe unweigerlich auseinander.
        '''
        ''' Eine Ebene OHNE Bild (Text, Form, SVG) liefert ihr Rechteck; das ist ehrlicher als gar
        ''' nichts und deckt sich mit dem, was die Begrenzung dort tut. Der Decode läuft hier auf dem
        ''' UI-Faden: es ist ein bewusster Klick, kein Zeigerzug, und der Griff soll sofort greifen.</summary>
        Public Function LoadSelectionFromAnnotationAlpha(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing OrElse Not annotation.IsVisible Then Return False
            If Not _annotations.Contains(annotation) Then Return False

            Dim plan = BuildAnnotationConfinePlan(annotation)
            If plan Is Nothing Then Return False
            Dim mask = BuildAnnotationConfineMask(plan)
            If mask Is Nothing Then Return False
            Try
                ' Als AUSWAHL, nicht als Maske: es sind Laufameisen um die Form der Ebene, und sie
                ' verrechnet sich mit dem eingestellten Verknüpfungsmodus wie jede andere Auswahl.
                ApplySelectionCandidate(mask, plan.Rect, "MagicWand", Nothing, Nothing)
            Finally
                mask.Dispose()
            End Try

            ' DANACH GEHÖRT DIE BÜHNE DER AUSWAHL, NICHT DER EBENE. Die Ebene wird dafür abgewählt und
            ' der Editor stellt auf Auswahl/Verschieben:
            ' - Ihr Rahmen läge sonst über dem Bild, bei einer Malebene über dem GANZEN Bild, und
            '   verdeckte genau die Ameisenlinie, die man gerade geholt hat.
            ' - Und ein Zug darin verschöbe die EBENE statt der Auswahl - der Rahmen fängt jeden
            '   Druck ab (siehe IsLayerPlacementTool).
            ' Erst die Auswahl, dann das Werkzeug: der Wechsel ins Auswahl-Werkzeug setzt den
            ' Verknüpfungsmodus zurück, und der galt noch für DIESEN Zug.
            CurrentTool = EditorTool.Selection
            SelectionMode = "Move"
            SelectedAnnotationIndex = -1
            AddHistoryEntry(LocalizationService.T("Auswahl aus Ebene"))
            Return True
        End Function

        ''' <summary>Die Hülle der Ebene im Anzeige-Raster: die vier Ecken ihres Bildes hinüber,
        ''' davon das umschließende Rechteck. Ein Punkt Rand fängt die Rundung ab. Eine gedrehte
        ''' Ebene füllt diese Hülle nicht aus - was in ihr wirklich zur Ebene gehört, sagt erst die
        ''' Deckung.</summary>
        Private Shared Function ComputeConfineHull(placement As AnnotationImagePlacement,
                                                   displayWidth As Integer, displayHeight As Integer) As SKRectI
            Dim corners = {placement.ImageToDisplay(0, 0),
                           placement.ImageToDisplay(placement.ImageWidth, 0),
                           placement.ImageToDisplay(0, placement.ImageHeight),
                           placement.ImageToDisplay(placement.ImageWidth, placement.ImageHeight)}
            Dim minX = corners.Min(Function(p) CDbl(p.X))
            Dim maxX = corners.Max(Function(p) CDbl(p.X))
            Dim minY = corners.Min(Function(p) CDbl(p.Y))
            Dim maxY = corners.Max(Function(p) CDbl(p.Y))
            Dim left = Math.Max(0, CInt(Math.Floor(minX)) - 1)
            Dim top = Math.Max(0, CInt(Math.Floor(minY)) - 1)
            Dim right = Math.Min(displayWidth, CInt(Math.Ceiling(maxX)) + 1)
            Dim bottom = Math.Min(displayHeight, CInt(Math.Ceiling(maxY)) + 1)
            If right <= left OrElse bottom <= top Then Return SKRectI.Empty
            Return New SKRectI(left, top, right, bottom)
        End Function

        ''' <summary>Die Deckung der Ebene im Anzeige-Raster, als Alpha8 in der Größe von
        ''' <c>plan.Rect</c>. GEZEICHNET statt Punkt für Punkt gerechnet: dieselbe Kette wie in
        ''' <c>ImageToDisplay</c>, nur als Leinwand-Transformation - damit setzt Skia die Ebene
        ''' dorthin, wo der Renderer sie auch hinsetzt, und das Skalieren fällt ab. Eine gespiegelte
        ''' Pixelschleife über ein bildschirmfüllendes Objekt kostete Millionen Rückrechnungen.
        '''
        ''' Läuft im HINTERGRUND (hier steht der Decode) und fasst deshalb nichts am ViewModel an.
        ''' Nothing heißt „nicht begrenzen".</summary>
        Private Shared Function BuildAnnotationConfineMask(plan As AnnotationConfinePlan) As SKBitmap
            If plan Is Nothing OrElse plan.Placement Is Nothing Then Return Nothing
            If plan.Rect.Width <= 0 OrElse plan.Rect.Height <= 0 Then Return Nothing
            Dim placement = plan.Placement
            Dim mask As SKBitmap = Nothing
            Try
                mask = New SKBitmap(plan.Rect.Width, plan.Rect.Height, SKColorType.Alpha8, SKAlphaType.Premul)
                Using canvas = New SKCanvas(mask)
                    canvas.Clear(SKColors.Transparent)
                    canvas.Translate(-plan.Rect.Left, -plan.Rect.Top)
                    ' Die Reihenfolge ist die von ImageToDisplay, von außen nach innen: zuerst die
                    ' Drehung, dann die Spiegelungen, zuletzt die Einpassung des Bildes.
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
                    If String.IsNullOrWhiteSpace(plan.ImagePath) Then
                        Using paint As New SKPaint With {.Color = SKColors.White, .IsAntialias = False}
                            canvas.DrawRect(placement.FitRect, paint)
                        End Using
                    Else
                        Using decoded = SKBitmap.Decode(plan.ImagePath)
                            If decoded Is Nothing Then
                                mask.Dispose()
                                Return Nothing
                            End If
                            Using image = SKImage.FromBitmap(decoded)
                                canvas.DrawImage(image, placement.FitRect,
                                                 New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), Nothing)
                            End Using
                        End Using
                    End If
                End Using
                Return mask
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.AnnotationConfine", ex)
                mask?.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Backt einen Pinsel-/Radiererstrich in das Bild eines Objekts. True heißt: der
        ''' Strich ist hier behandelt worden und gehört NICHT mehr ins Foto - auch dann, wenn er das
        ''' Objekt gar nicht getroffen hat (die markierte Ebene ist das Ziel, wie in üblichen
        ''' Bildbearbeitungen).
        '''
        ''' Aufgeteilt wie beim Malen ins Foto: hier auf dem UI-Faden entsteht nur die BESCHREIBUNG
        ''' des Strichs (Punkte im Bildraster des Objekts, Deckung der Auswahl, Zielpfad). Dekodieren,
        ''' Zeichnen und Schreiben kosten bei einem eingefügten Foto in voller Auflösung leicht eine
        ''' Zehntelsekunde und mehr und laufen deshalb im Hintergrund; erst das Ergebnis kommt zurück
        ''' auf den UI-Faden.</summary>
        Private Function TryPaintStrokeIntoImageAnnotation(target As ImageAnnotation,
                                                           displayPoints As IReadOnlyList(Of Avalonia.Point),
                                                           isEraser As Boolean) As Boolean
            If target Is Nothing OrElse displayPoints Is Nothing OrElse displayPoints.Count < 2 Then Return False
            Dim displaySize = GetAnnotationDisplayPixelSize()
            If displaySize.Width <= 0 OrElse displaySize.Height <= 0 Then Return False

            Dim sourcePath = ""
            If Not BeginObjectImageEdit(target, sourcePath) Then Return False

            Dim placement = BuildAnnotationImagePlacement(target, _objectPaintSize.Width, _objectPaintSize.Height)
            If placement Is Nothing Then Return False

            Dim imagePoints = displayPoints.
                Select(Function(p) placement.DisplayToImage(p.X / 100.0 * displaySize.Width,
                                                            p.Y / 100.0 * displaySize.Height)).
                Select(Function(p) New Avalonia.Point(p.X, p.Y)).ToList()

            Dim options = BuildPixelPaintOptions(isEraser)
            ' Die Pinselbreite steht in ANZEIGE-Punkten (so bemisst die Ansicht ihren Ring) - im Bild
            ' des Objekts ist ein Anzeigepunkt je nach Zoomstand mehr oder weniger als ein Bildpunkt.
            options.StrokeWidth = CSng(Math.Max(1.0, _brushSize * placement.ImagePixelsPerDisplayPixel))

            Dim dirty As SKRectI
            Dim stroke = PixelEditLayer.CreateTransientStroke(imagePoints, options,
                                                             _objectPaintSize.Width, _objectPaintSize.Height, dirty)
            If stroke Is Nothing Then Return True
            dirty = ClampRectToBitmap(dirty, _objectPaintSize.Width, _objectPaintSize.Height)
            If dirty.Width <= 0 OrElse dirty.Height <= 0 Then Return True

            ' Auswahl: nur innerhalb darf gemalt und radiert werden. Die Deckung wird im Raster des
            ' OBJEKTBILDS gebraucht - jeder Punkt darin wird dafür auf die Anzeige abgebildet, wo die
            ' Auswahl liegt. Sie entsteht HIER, weil der Hintergrund den Auswahlzustand nicht anfassen
            ' darf.
            Dim anyCoverage = False
            Dim coverage = BuildSelectionCoverage(dirty,
                                                  Function(px, py) CType(placement.ImageToDisplay(px, py), SKPoint?),
                                                  Function(dx, dy) CType(placement.DisplayToImage(dx, dy), SKPoint?),
                                                  anyCoverage)
            If coverage IsNot Nothing AndAlso Not anyCoverage Then
                coverage.Dispose()
                Return True
            End If
            If dirty.Width <= 0 OrElse dirty.Height <= 0 Then
                coverage?.Dispose()
                Return True
            End If

            Dim renderAnn = stroke.ToRenderAnnotation()
            Dim targetPath = CreateSelectionAssetTempPath("paint")
            _objectPaintNextSource = targetPath
            EnqueueObjectPaint(target, sourcePath, targetPath, renderAnn, dirty, coverage)
            Return True
        End Function

        ''' <summary>Löscht den Inhalt der aktiven Pixelauswahl aus dem BILD DER MARKIERTEN EBENE.
        ''' True heißt: die Entf-Taste ist hier behandelt worden - auch dann, wenn die Auswahl die
        ''' Ebene gar nicht berührt. Die markierte Ebene ist das Ziel, dieselbe Regel wie beim
        ''' Pinsel; ein Rückfall aufs Foto säße in den falschen Pixeln und fiele erst beim
        ''' Verschieben der Ebene auf.
        '''
        ''' Trägt die markierte Ebene kein Bild (Text, Form, SVG) oder ist keine markiert, gibt es
        ''' hier nichts zu löschen und der Aufrufer nimmt seinen bisherigen Weg ins Arbeitsbild.</summary>
        Private Function TryEraseSelectionFromImageAnnotation() As Boolean
            Dim target = FindStrokeTargetImageAnnotation()
            If target Is Nothing Then Return False

            Dim sourcePath = ""
            If Not BeginObjectImageEdit(target, sourcePath) Then Return False

            Dim placement = BuildAnnotationImagePlacement(target, _objectPaintSize.Width, _objectPaintSize.Height)
            If placement Is Nothing Then Return False

            ' Das ganze Ebenenbild als Region: BuildSelectionCoverage schneidet es selbst auf das
            ' zurecht, was die Auswahl überhaupt erreichen kann.
            Dim dirty = New SKRectI(0, 0, _objectPaintSize.Width, _objectPaintSize.Height)
            Dim anyCoverage = False
            Dim coverage = BuildSelectionCoverage(dirty,
                                                  Function(px, py) CType(placement.ImageToDisplay(px, py), SKPoint?),
                                                  Function(dx, dy) CType(placement.DisplayToImage(dx, dy), SKPoint?),
                                                  anyCoverage)
            ' Ohne Deckung gäbe es keine Grenze - dann wäre das Löschen das ganze Ebenenbild, und
            ' genau das ist nicht gemeint. Deckt die Auswahl nichts von der Ebene, passiert nichts:
            ' kein Undo-Schritt, keine neue Datei.
            If coverage Is Nothing OrElse Not anyCoverage OrElse dirty.Width <= 0 OrElse dirty.Height <= 0 Then
                coverage?.Dispose()
                Return True
            End If

            Dim targetPath = CreateSelectionAssetTempPath("erase")
            _objectPaintNextSource = targetPath
            EnqueueObjectErase(target, sourcePath, targetPath, dirty, coverage)
            Return True
        End Function

        ''' <summary>Der Stand, auf dem der nächste Schritt an dieser Ebene aufbaut, und ihre Maße.
        '''
        ''' Der QUELLPFAD ist nicht zwingend der, auf den das Objekt gerade zeigt: liegt noch ein Zug
        ''' in der Warteschlange, baut dieser hier auf DESSEN Ergebnis auf. Sonst ginge der vorige
        ''' Schritt verloren, sobald zwei schnell hintereinander kommen. Deshalb führen Malen,
        ''' Löschen und Retusche denselben Vermerk - eine eigene Buchhaltung je Werkzeug ließe die
        ''' drei einander überschreiben. False heißt: die Ebene trägt kein lesbares Bild.</summary>
        Private Function BeginObjectImageEdit(target As ImageAnnotation, ByRef sourcePath As String) As Boolean
            If target Is Nothing Then Return False
            If Not Object.ReferenceEquals(_objectPaintTarget, target) Then
                _objectPaintTarget = target
                _objectPaintNextSource = target.ImagePath
                _objectPaintSize = ReadImageSize(target.ImagePath)
            End If
            sourcePath = If(String.IsNullOrEmpty(_objectPaintNextSource), target.ImagePath, _objectPaintNextSource)
            If _objectPaintSize.Width <= 0 OrElse _objectPaintSize.Height <= 0 Then
                _objectPaintSize = ReadImageSize(target.ImagePath)
                If _objectPaintSize.Width <= 0 OrElse _objectPaintSize.Height <= 0 Then Return False
            End If
            Return True
        End Function

        ''' <summary>Reiht den schweren Teil ein: dekodieren, zeichnen, schreiben. Die Züge laufen
        ''' STRENG NACHEINANDER - jeder baut auf der Datei des vorigen auf, und eine Verzahnung
        ''' verlöre den früheren Strich.</summary>
        Private Sub EnqueueObjectPaint(target As ImageAnnotation, sourcePath As String, targetPath As String,
                                       renderAnnotation As ImageAnnotation, dirty As SKRectI, coverage As SKBitmap)
            ' Der Text wird HIER aufgelöst, nicht drinnen aus einer Variablen: T() liest den Schlüssel
            ' aus dem Literal, ein T(variable) fiele aus der Lokalisierung heraus.
            ' Die Sperre der transparenten Punkte wird HIER gelesen, auf dem UI-Faden: der Hintergrund
            ' darf die Ebene nicht anfassen, und wer sie mitten im Zug umlegt, meint den nächsten.
            Dim lockTransparent = target.LockTransparentPixels
            EnqueueObjectImageEdit(target, targetPath, LocalizationService.T("Malen fehlgeschlagen"),
                                   Function() PaintObjectStrokeToFile(sourcePath, targetPath, renderAnnotation, dirty, coverage, lockTransparent),
                                   Sub() coverage?.Dispose())
        End Sub

        ''' Dasselbe für das Ausstanzen der Auswahl - derselbe Weg, damit ein Löschen und ein Strich
        ''' auf derselben Ebene sich nicht überholen können.
        Private Sub EnqueueObjectErase(target As ImageAnnotation, sourcePath As String, targetPath As String,
                                       dirty As SKRectI, coverage As SKBitmap)
            EnqueueObjectImageEdit(target, targetPath, LocalizationService.T("Löschen fehlgeschlagen"),
                                   Function() EraseCoverageToFile(sourcePath, targetPath, dirty, coverage),
                                   Sub() coverage?.Dispose())
        End Sub

        ''' <summary>Die gemeinsame Warteschlange für jede Änderung am Bild einer Ebene. Sie ist
        ''' EINE Kette und keine je Art: Malen, Löschen und Retusche bauen alle auf der Datei des
        ''' vorigen Schrittes auf. <paramref name="failureMessage"/> kommt FERTIG ÜBERSETZT herein.
        '''
        ''' <paramref name="cleanup"/> läuft im HINTERGRUND (dort werden die Puffer des Schrittes
        ''' frei), <paramref name="onUiDone"/> dagegen auf dem UI-Faden, nachdem das Ergebnis
        ''' übernommen ist - für alles, was so lange stehen bleiben muss, bis das neue Bild da ist.
        ''' Beides ist optional.
        '''
        ''' <paramref name="countsAsBusy"/> markiert einen MODELLLAUF: er sperrt die Oberfläche und
        ''' bekommt das X zum Abbrechen. Der Zähler wird hier geführt und nicht beim Aufrufer, weil
        ''' der Schritt an einer Stelle aussteigt, die der Aufrufer nicht sieht - nach einem
        ''' Bildwechsel verfällt er, und ein dort vergessenes Herunterzählen ließe den Schleier für
        ''' immer stehen. <paramref name="cancelledMessage"/> ist die Meldung für den gewollten
        ''' Abbruch; "fehlgeschlagen" wäre dort die falsche Auskunft.</summary>
        Private Sub EnqueueObjectImageEdit(target As ImageAnnotation, targetPath As String,
                                           failureMessage As String,
                                           work As Func(Of Boolean), cleanup As Action,
                                           Optional onUiDone As Action = Nothing,
                                           Optional countsAsBusy As Boolean = False,
                                           Optional cancelledMessage As String = "")
            ' Merkmal des Dokuments beim Einreihen: kommt der Schritt erst nach einem Bildwechsel an
            ' die Reihe, gehört er zum alten Bild und verfällt (dieselbe Regel wie bei den
            ' Arbeitsbild-Commits).
            Dim documentStamp = _selectionAssetTempDir
            If countsAsBusy Then
                _pendingLayerModelRuns += 1
                RefreshBusyState()
            End If
            _objectPaintChain = _objectPaintChain.ContinueWith(
                Sub(prev)
                    Dim ok = False
                    Try
                        ok = work()
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Editor.ObjectPaint", ex)
                    Finally
                        cleanup?.Invoke()
                    End Try
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        Sub()
                            Try
                                If Not String.Equals(documentStamp, _selectionAssetTempDir, StringComparison.Ordinal) Then Return
                                ' Abgebrochen heißt: kein Ergebnis, aber auch kein Fehler. Die Ebene
                                ' zeigt weiter auf ihre bisherige Datei.
                                Dim cancelled = countsAsBusy AndAlso LayerRunWasCancelled()
                                ApplyObjectPaintResult(target, targetPath, ok,
                                                       If(cancelled AndAlso cancelledMessage <> "",
                                                          cancelledMessage, failureMessage))
                                If Not cancelled Then onUiDone?.Invoke()
                            Finally
                                If countsAsBusy Then
                                    _pendingLayerModelRuns -= 1
                                    EndCancellableLayerRun()
                                    RefreshBusyState()
                                End If
                            End Try
                        End Sub)
                End Sub, TaskScheduler.Default)
        End Sub

        ''' <summary>Das Ergebnis eines Zuges übernehmen - auf dem UI-Faden und in der Reihenfolge,
        ''' in der die Züge gemalt wurden. Der Schnappschuss entsteht genau hier: er trägt den
        ''' vorigen Pfad, und der IST das Rückgängig.</summary>
        Private Sub ApplyObjectPaintResult(target As ImageAnnotation, newPath As String, ok As Boolean,
                                           failureMessage As String)
            If Not ok OrElse String.IsNullOrEmpty(newPath) Then
                StatusText = failureMessage
                ' Der nächste Zug darf nicht auf einer Datei aufbauen, die nie entstanden ist.
                If Object.ReferenceEquals(_objectPaintTarget, target) Then _objectPaintNextSource = target.ImagePath
                Return
            End If
            If Not _annotations.Contains(target) Then Return

            RegisterObjectPaintFile(newPath)
            PushUndo()
            target.ImagePath = newPath
            _hasChanges = True
            RaiseResetButtonStateChanged()
            RefreshOverlayAfterAnnotationChange(ComputeSceneDirtyRectFor(target))
        End Sub

        ''' <summary>Die Maße einer Bilddatei, ohne sie zu dekodieren - der Kopf genügt. Gebraucht
        ''' schon beim Vorbereiten des Strichs (die Einpassung hängt daran), und dort ist ein voller
        ''' Decode auf dem UI-Faden genau das, was hier vermieden werden soll.</summary>
        Private Shared Function ReadImageSize(path As String) As (Width As Integer, Height As Integer)
            If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then Return (0, 0)
            Try
                Using codec = SKCodec.Create(path)
                    If codec Is Nothing Then Return (0, 0)
                    Return (codec.Info.Width, codec.Info.Height)
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.ObjectPaintSize", ex)
                Return (0, 0)
            End Try
        End Function

        ''' <summary>Der schwere Teil, im Hintergrund: Objektbild dekodieren, Strich hineinzeichnen,
        ''' Ergebnis als neue Datei schreiben.</summary>
        Private Function PaintObjectStrokeToFile(sourcePath As String, targetPath As String,
                                                 renderAnnotation As ImageAnnotation,
                                                 dirty As SKRectI, coverage As SKBitmap,
                                                 lockTransparent As Boolean) As Boolean
            Using decoded = SKBitmap.Decode(sourcePath)
                If decoded Is Nothing OrElse decoded.Width <= 0 OrElse decoded.Height <= 0 Then Return False
                Dim clamped = ClampRectToBitmap(dirty, decoded.Width, decoded.Height)
                If clamped.Width <> dirty.Width OrElse clamped.Height <> dirty.Height Then Return False
                Using painted = PaintStrokeOntoImageCopy(decoded, renderAnnotation, dirty, coverage, lockTransparent)
                    If painted Is Nothing Then Return False
                    Return WriteObjectPaintFile(painted, targetPath)
                End Using
            End Using
        End Function

        ''' <summary>Der schwere Teil des Löschens, im Hintergrund: Objektbild dekodieren, die Deckung
        ''' der Auswahl als echtes Alpha-Loch ausstanzen, Ergebnis als neue Datei schreiben.</summary>
        Private Function EraseCoverageToFile(sourcePath As String, targetPath As String,
                                             dirty As SKRectI, coverage As SKBitmap) As Boolean
            If coverage Is Nothing Then Return False
            Using decoded = SKBitmap.Decode(sourcePath)
                If decoded Is Nothing OrElse decoded.Width <= 0 OrElse decoded.Height <= 0 Then Return False
                Dim clamped = ClampRectToBitmap(dirty, decoded.Width, decoded.Height)
                If clamped.Width <> dirty.Width OrElse clamped.Height <> dirty.Height Then Return False
                Using punched = PunchCoverageFromImageCopy(decoded, dirty, coverage)
                    If punched Is Nothing Then Return False
                    Return WriteObjectPaintFile(punched, targetPath)
                End Using
            End Using
        End Function

        ''' <summary>Stanzt die Deckung aus einer ARBEITSKOPIE des Objektbilds. Bgra8888/Premul aus
        ''' demselben Grund wie beim Malen: nur dort entstehen echte Alpha-Löcher, und ein JPEG kommt
        ''' ganz ohne Alphakanal herein. <c>DstOut</c> behält, was die Deckung NICHT abdeckt - die
        ''' weiche Kante der Auswahl steckt im Alpha der Deckung und kommt von selbst mit. Dieselbe
        ''' Rechnung wie in <c>EraseSelection</c> fürs Arbeitsbild, nur im Raster der Ebene.</summary>
        Private Shared Function PunchCoverageFromImageCopy(source As SKBitmap, dirty As SKRectI,
                                                           coverage As SKBitmap) As SKBitmap
            Dim copy = New SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Try
                Using canvas = New SKCanvas(copy)
                    canvas.Clear(SKColors.Transparent)
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        canvas.DrawBitmap(source, 0, 0, paint)
                    End Using
                End Using
                Using canvas = New SKCanvas(copy)
                    canvas.ClipRect(SKRect.Create(dirty.Left, dirty.Top, dirty.Width, dirty.Height))
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.DstOut}
                        canvas.DrawBitmap(coverage, dirty.Left, dirty.Top, paint)
                    End Using
                End Using
                Return copy
            Catch
                copy.Dispose()
                Throw
            End Try
        End Function

        ''' <summary>DIE VORHANDENE FORM DER EBENE ALS DECKUNG - der Alphakanal des Bildes selbst,
        ''' als Alpha8 im Ausschnitt. Damit sperrt <c>LockTransparentPixels</c>: was die Ebene nicht
        ''' schon deckt, wird nach dem Zeichnen wieder auf den Vorher-Stand zurückgeblendet.
        '''
        ''' GEZEICHNET statt Punkt für Punkt gerechnet: Skia übernimmt beim Zeichnen in ein
        ''' Alpha8-Ziel genau den Alphakanal, und das ist derselbe Weg, den auch die Begrenzung einer
        ''' Auswahl auf eine Ebene geht.</summary>
        Private Shared Function BuildAlphaCoverage(source As SKBitmap, rect As SKRectI) As SKBitmap
            If source Is Nothing OrElse rect.Width <= 0 OrElse rect.Height <= 0 Then Return Nothing
            Dim coverage As SKBitmap = Nothing
            Try
                coverage = New SKBitmap(rect.Width, rect.Height, SKColorType.Alpha8, SKAlphaType.Premul)
                Using canvas = New SKCanvas(coverage)
                    canvas.Clear(SKColors.Transparent)
                    canvas.DrawBitmap(source,
                                      New SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                                      New SKRect(0, 0, rect.Width, rect.Height))
                End Using
                Return coverage
            Catch ex As Exception
                DiagnosticLogService.LogException("Editor.AlphaCoverage", ex)
                coverage?.Dispose()
                Return Nothing
            End Try
        End Function

        ''' <summary>Sperrt die transparenten Punkte NACHTRÄGLICH: außerhalb der vorhandenen Form der
        ''' Ebene kommt der Vorher-Stand zurück. Dieselbe Nachnahme wie bei einer Auswahl, nur ist die
        ''' Deckung hier der eigene Alphakanal - und deshalb dürfen beide nacheinander laufen: jede
        ''' blendet für sich auf denselben Vorher-Stand zurück, gemalt bleibt nur, was BEIDE
        ''' erlauben.</summary>
        Private Shared Function ApplyTransparencyLock(copy As SKBitmap, before As SKBitmap,
                                                      original As SKBitmap, dirty As SKRectI) As Boolean
            If copy Is Nothing OrElse before Is Nothing OrElse original Is Nothing Then Return False
            Using alphaCoverage = BuildAlphaCoverage(original, dirty)
                If alphaCoverage Is Nothing Then Return False
                Return ImageProcessor.RestoreOutsideCoverage(copy, before, alphaCoverage, dirty)
            End Using
        End Function

        ''' <summary>Zeichnet den Strich in eine ARBEITSKOPIE des Objektbilds. Die Kopie liegt in
        ''' Bgra8888/Premul: nur dort stanzt der Radierer echte Löcher (DstOut), und ein JPEG kommt
        ''' ganz ohne Alphakanal herein. Eine Auswahl wird nach dem Zeichnen wieder herausgerechnet -
        ''' dieselbe Nachnahme wie im Foto, damit die Zeichenroutine nichts davon wissen muss.</summary>
        Private Shared Function PaintStrokeOntoImageCopy(source As SKBitmap, renderAnnotation As ImageAnnotation,
                                                         dirty As SKRectI, coverage As SKBitmap,
                                                         Optional lockTransparent As Boolean = False) As SKBitmap
            Dim copy = New SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Try
                Using canvas = New SKCanvas(copy)
                    canvas.Clear(SKColors.Transparent)
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        canvas.DrawBitmap(source, 0, 0, paint)
                    End Using
                End Using

                Dim before As SKBitmap = Nothing
                If coverage IsNot Nothing OrElse lockTransparent Then before = ImageProcessor.CopyRegion(copy, dirty)
                Try
                    Using canvas = New SKCanvas(copy)
                        canvas.ClipRect(SKRect.Create(dirty.Left, dirty.Top, dirty.Width, dirty.Height))
                        Dim adjDraw As New ImageAdjustments With {
                            .SourceWidthPixels = copy.Width, .SourceHeightPixels = copy.Height}
                        ImageProcessor.DrawAnnotationsOnCanvas(canvas, adjDraw, copy.Width, copy.Height,
                                                               0, 0, copy.Width, copy.Height,
                                                               New List(Of ImageAnnotation) From {renderAnnotation})
                    End Using
                    If coverage IsNot Nothing Then
                        If Not ImageProcessor.RestoreOutsideCoverage(copy, before, coverage, dirty) Then
                            copy.Dispose()
                            Return Nothing
                        End If
                    End If
                    If lockTransparent Then
                        If Not ApplyTransparencyLock(copy, before, source, dirty) Then
                            copy.Dispose()
                            Return Nothing
                        End If
                    End If
                Finally
                    before?.Dispose()
                End Try
                Return copy
            Catch
                copy.Dispose()
                Throw
            End Try
        End Function

        ''' <summary>Schreibt einen Zwischenstand in den Sitzungsordner des Dokuments. NIE in die
        ''' Quelldatei: die kann das Original des Nutzers sein, und an ein herangezogenes Original
        ''' wird nicht geschrieben. Der Ordner ist derselbe, in dem auch die Auswahl-Assets liegen -
        ''' er wird beim Dokumentwechsel geräumt und beim .fpx-Speichern eingebettet.
        '''
        ''' Läuft im HINTERGRUND und fasst deshalb nichts an, was dem UI-Faden gehört: den Pfad legt
        ''' der Aufrufer vorher fest, und beim Deckel meldet sich der Zug erst beim Übernehmen an.</summary>
        Private Shared Function WriteObjectPaintFile(bitmap As SKBitmap, path As String) As Boolean
            If bitmap Is Nothing OrElse String.IsNullOrWhiteSpace(path) Then Return False
            Using image = SKImage.FromBitmap(bitmap)
                If image Is Nothing Then Return False
                ' Schnelle Kompressionsstufe: PNG ist verlustfrei, die Stufe kostet nur Zeit, und
                ' die Datei lebt ohnehin nur bis zum Schließen des Dokuments.
                Using data = image.Encode(SKEncodedImageFormat.Png, 60)
                    If data Is Nothing Then Return False
                    Using fs = File.Create(path)
                        data.SaveTo(fs)
                    End Using
                End Using
            End Using
            Return True
        End Function

        ''' <summary>Meldet einen fertig geschriebenen Zwischenstand beim Deckel an. Auf dem
        ''' UI-Faden, wie alles an der Liste.</summary>
        Private Sub RegisterObjectPaintFile(path As String)
            Dim bytes As Long = 0
            Try
                bytes = New FileInfo(path).Length
            Catch
            End Try
            _objectPaintFiles.Add((path, bytes))
            EnforceObjectPaintBudget()
        End Sub

        ''' Ältester Zwischenstand zuerst, und niemals der letzte - auf den zeigt das Objekt gerade.
        Private Sub EnforceObjectPaintBudget()
            Dim total As Long = 0
            For Each entry In _objectPaintFiles
                total += entry.Bytes
            Next
            While total > ObjectPaintBudgetBytes AndAlso _objectPaintFiles.Count > 1
                Dim oldest = _objectPaintFiles(0)
                _objectPaintFiles.RemoveAt(0)
                total -= oldest.Bytes
                Try
                    If File.Exists(oldest.Path) Then File.Delete(oldest.Path)
                Catch ex As Exception
                    DiagnosticLogService.LogException("Editor.ObjectPaintBudget", ex)
                End Try
            End While
        End Sub

        ''' <summary>Die Deckung der aktiven Auswahl über einer Region eines beliebigen Zielrasters.
        '''
        ''' <paramref name="mapToDisplay"/> bildet einen Punkt dieses Rasters auf ANZEIGE-Pixel ab -
        ''' dort liegt die Auswahl; Nothing steht für „dasselbe Raster" und spart die Rechnung ganz.
        ''' <paramref name="mapFromDisplay"/> geht denselben Weg zurück und wird nur für die vier Ecken
        ''' der Auswahl gebraucht: <paramref name="rect"/> schrumpft damit VOR der Pixelschleife auf
        ''' das, was die Auswahl überhaupt erreichen kann. Ohne das kostete ein langer Zug über ein
        ''' großes Foto Millionen Rückrechnungen für lauter Punkte, die ohnehin unverändert bleiben.
        '''
        ''' Rückgabe Nothing heißt „keine Auswahl, nichts zu begrenzen"; davon unterscheidet
        ''' <paramref name="anyCoverage"/> den Fall „Auswahl vorhanden, deckt diese Region aber
        ''' nicht".
        '''
        ''' <paramref name="allowMaskSelection"/> nimmt auch eine MASKE als Quelle an. Für einen
        ''' STRICH ist das falsch (siehe <c>HasPixelSelectionScope</c>): die Maske gehört einer Ebene,
        ''' ihr rotes Overlay ist in den Malwerkzeugen ausgeblendet, und eine unsichtbare Begrenzung
        ''' wäre nicht zu erklären. Wo die Auswahl dagegen der AUFTRAG ist und nicht die Grenze -
        ''' beim Entfernen -, ist die Art der Auswahl gleichgültig: der Weg ins Arbeitsbild nimmt dort
        ''' seit jeher jede, und eine Maske aus der Objektauswahl ist genau der übliche Weg, das zu
        ''' Entfernende zu bestimmen.</summary>
        Private Function BuildSelectionCoverage(ByRef rect As SKRectI,
                                                mapToDisplay As Func(Of Double, Double, SKPoint?),
                                                mapFromDisplay As Func(Of Double, Double, SKPoint?),
                                                ByRef anyCoverage As Boolean,
                                                Optional allowMaskSelection As Boolean = False) As SKBitmap
            anyCoverage = False
            If Not If(allowMaskSelection, _hasActiveSelection, HasPixelSelectionScope) Then Return Nothing
            If rect.Width <= 0 OrElse rect.Height <= 0 Then Return Nothing

            Dim ownsMask = False
            Dim maskRect As SKRectI = SKRectI.Empty
            Dim mask = GetSelectionMaskForOutput(maskRect, ownsMask)
            Try
                Dim maskBuffer As Byte() = Nothing
                Dim maskStride = 0
                If mask IsNot Nothing Then
                    If mask.ColorType <> SKColorType.Alpha8 Then Return Nothing
                    maskStride = mask.RowBytes
                    maskBuffer = New Byte(maskStride * mask.Height - 1) {}
                    Marshal.Copy(mask.GetPixels(), maskBuffer, 0, maskBuffer.Length)
                End If
                Dim maskWidth = If(mask Is Nothing, 0, mask.Width)
                Dim maskHeight = If(mask Is Nothing, 0, mask.Height)

                rect = ShrinkRectToSelectionBounds(rect, maskRect, mapFromDisplay)
                If rect.Width <= 0 OrElse rect.Height <= 0 Then
                    ' Die Auswahl liegt vollständig neben der Region - eine leere Deckung sagt dem
                    ' Aufrufer „hier ist nichts zu malen", ohne dass er den Fall doppelt prüfen muss.
                    Return New SKBitmap(1, 1, SKColorType.Alpha8, SKAlphaType.Premul)
                End If

                Dim coverage = New SKBitmap(rect.Width, rect.Height, SKColorType.Alpha8, SKAlphaType.Premul)
                Dim cStride = coverage.RowBytes
                Dim cBuffer = New Byte(cStride * rect.Height - 1) {}
                Dim any = False
                Dim left = rect.Left, top = rect.Top
                For y = 0 To rect.Height - 1
                    Dim row = y * cStride
                    For x = 0 To rect.Width - 1
                        Dim dx As Integer, dy As Integer
                        If mapToDisplay Is Nothing Then
                            dx = left + x
                            dy = top + y
                        Else
                            Dim display = mapToDisplay(left + x + 0.5, top + y + 0.5)
                            If Not display.HasValue Then Continue For
                            dx = CInt(Math.Floor(display.Value.X))
                            dy = CInt(Math.Floor(display.Value.Y))
                        End If
                        Dim alpha As Byte
                        If maskBuffer IsNot Nothing Then
                            Dim lx = dx - maskRect.Left, ly = dy - maskRect.Top
                            If lx < 0 OrElse ly < 0 OrElse lx >= maskWidth OrElse ly >= maskHeight Then Continue For
                            alpha = maskBuffer(ly * maskStride + lx)
                        Else
                            ' Ohne Maskenbild ist die Auswahl ihr Rechteck - hart, ohne weiche Kante
                            ' (die steckt sonst schon im Maskenbild, siehe GetSelectionMaskForOutput).
                            If dx < maskRect.Left OrElse dy < maskRect.Top OrElse
                               dx >= maskRect.Right OrElse dy >= maskRect.Bottom Then Continue For
                            alpha = 255
                        End If
                        If alpha = 0 Then Continue For
                        cBuffer(row + x) = alpha
                        any = True
                    Next
                Next
                Marshal.Copy(cBuffer, 0, coverage.GetPixels(), cBuffer.Length)
                anyCoverage = any
                Return coverage
            Finally
                If ownsMask Then mask?.Dispose()
            End Try
        End Function

        ''' <summary>Beschneidet eine Region auf die Hülle der Auswahl. Die Abbildung zwischen Anzeige
        ''' und Zielraster ist bis auf die Rasterverzerrung affin, die vier Ecken spannen sie also auf;
        ''' zwei Punkte Rand fangen die Rundung. Lässt sich eine Ecke nicht zurückrechnen, wird NICHT
        ''' beschnitten - lieber langsam als abgeschnitten.</summary>
        Private Shared Function ShrinkRectToSelectionBounds(rect As SKRectI, selectionDisplayRect As SKRectI,
                                                            mapFromDisplay As Func(Of Double, Double, SKPoint?)) As SKRectI
            If selectionDisplayRect.Width <= 0 OrElse selectionDisplayRect.Height <= 0 Then Return SKRectI.Empty
            Dim minX = CDbl(selectionDisplayRect.Left), minY = CDbl(selectionDisplayRect.Top)
            Dim maxX = CDbl(selectionDisplayRect.Right), maxY = CDbl(selectionDisplayRect.Bottom)
            If mapFromDisplay IsNot Nothing Then
                Dim corners = {(minX, minY), (maxX, minY), (minX, maxY), (maxX, maxY)}
                Dim first = True
                For Each corner In corners
                    Dim mapped = mapFromDisplay(corner.Item1, corner.Item2)
                    If Not mapped.HasValue Then Return rect
                    If first Then
                        minX = mapped.Value.X : maxX = mapped.Value.X
                        minY = mapped.Value.Y : maxY = mapped.Value.Y
                        first = False
                    Else
                        minX = Math.Min(minX, mapped.Value.X) : maxX = Math.Max(maxX, mapped.Value.X)
                        minY = Math.Min(minY, mapped.Value.Y) : maxY = Math.Max(maxY, mapped.Value.Y)
                    End If
                Next
            End If
            Dim left = Math.Max(rect.Left, CInt(Math.Floor(minX)) - 2)
            Dim top = Math.Max(rect.Top, CInt(Math.Floor(minY)) - 2)
            Dim right = Math.Min(rect.Right, CInt(Math.Ceiling(maxX)) + 2)
            Dim bottom = Math.Min(rect.Bottom, CInt(Math.Ceiling(maxY)) + 2)
            If right <= left OrElse bottom <= top Then Return SKRectI.Empty
            Return New SKRectI(left, top, right, bottom)
        End Function

        ''' <summary>Die Deckung der Auswahl im Raster des ARBEITSBILDS. Ohne angewendete Geometrie
        ''' sind Anzeige und Arbeitsbild dasselbe Raster, dann entfällt die Punktabbildung ganz -
        ''' das ist der Normalfall und spart bei einem großen Foto die teure Rückrechnung je Pixel.
        ''' <paramref name="rect"/> kommt beschnitten zurück (siehe BuildSelectionCoverage).</summary>
        Private Function BuildSelectionCoverageForWorkingRect(ByRef rect As SKRectI, ByRef anyCoverage As Boolean) As SKBitmap
            anyCoverage = False
            If Not HasPixelSelectionScope Then Return Nothing
            Dim baseWidth = GetBaseWidth()
            Dim baseHeight = GetBaseHeight()
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return Nothing

            Dim displaySize = GetAnnotationDisplayPixelSize()
            Dim neutralGeometry = ((_appliedRotationDegrees Mod 360) + 360) Mod 360 = 0 AndAlso
                                  Not _appliedFlipH AndAlso Not _appliedFlipV AndAlso
                                  Not HasAppliedNonRotationGeometry() AndAlso
                                  displaySize.Width = baseWidth AndAlso displaySize.Height = baseHeight
            If neutralGeometry Then Return BuildSelectionCoverage(rect, Nothing, Nothing, anyCoverage)

            Dim geometry = BuildAppliedGeometryAdjustments()
            Return BuildSelectionCoverage(rect,
                                          Function(x, y)
                                              Dim output As SKPoint
                                              If Not ImageProcessor.TrySourcePointToGeometryOutput(x, y, baseWidth, baseHeight, geometry, output) Then
                                                  Return CType(Nothing, SKPoint?)
                                              End If
                                              Return CType(output, SKPoint?)
                                          End Function,
                                          Function(x, y)
                                              Dim source As SKPoint
                                              If Not ImageProcessor.TryGeometryOutputToSourcePoint(x, y, baseWidth, baseHeight, geometry, source) Then
                                                  Return CType(Nothing, SKPoint?)
                                              End If
                                              Return CType(source, SKPoint?)
                                          End Function,
                                          anyCoverage)
        End Function

        Private Shared Function ClampRectToBitmap(rect As SKRectI, width As Integer, height As Integer) As SKRectI
            Dim left = Math.Max(0, rect.Left)
            Dim top = Math.Max(0, rect.Top)
            Dim right = Math.Min(width, rect.Right)
            Dim bottom = Math.Min(height, rect.Bottom)
            If right <= left OrElse bottom <= top Then Return SKRectI.Empty
            Return New SKRectI(left, top, right, bottom)
        End Function

    End Class

End Namespace
