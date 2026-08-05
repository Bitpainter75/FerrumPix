Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading.Tasks
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

            ' Der QUELLPFAD ist nicht zwingend der, auf den das Objekt gerade zeigt: liegt noch ein
            ' Zug in der Warteschlange, baut dieser hier auf DESSEN Ergebnis auf. Sonst ginge der
            ' vorige Strich verloren, sobald zwei schnell hintereinander kommen.
            If Not Object.ReferenceEquals(_objectPaintTarget, target) Then
                _objectPaintTarget = target
                _objectPaintNextSource = target.ImagePath
                _objectPaintSize = ReadImageSize(target.ImagePath)
            End If
            Dim sourcePath = If(String.IsNullOrEmpty(_objectPaintNextSource), target.ImagePath, _objectPaintNextSource)
            If _objectPaintSize.Width <= 0 OrElse _objectPaintSize.Height <= 0 Then
                _objectPaintSize = ReadImageSize(target.ImagePath)
                If _objectPaintSize.Width <= 0 OrElse _objectPaintSize.Height <= 0 Then Return False
            End If

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

        ''' <summary>Reiht den schweren Teil ein: dekodieren, zeichnen, schreiben. Die Züge laufen
        ''' STRENG NACHEINANDER - jeder baut auf der Datei des vorigen auf, und eine Verzahnung
        ''' verlöre den früheren Strich.</summary>
        Private Sub EnqueueObjectPaint(target As ImageAnnotation, sourcePath As String, targetPath As String,
                                       renderAnnotation As ImageAnnotation, dirty As SKRectI, coverage As SKBitmap)
            ' Merkmal des Dokuments beim Einreihen: kommt der Zug erst nach einem Bildwechsel an die
            ' Reihe, gehört er zum alten Bild und verfällt (dieselbe Regel wie bei den
            ' Arbeitsbild-Commits).
            Dim documentStamp = _selectionAssetTempDir
            _objectPaintChain = _objectPaintChain.ContinueWith(
                Sub(prev)
                    Dim ok = False
                    Try
                        ok = PaintObjectStrokeToFile(sourcePath, targetPath, renderAnnotation, dirty, coverage)
                    Catch ex As Exception
                        DiagnosticLogService.LogException("Editor.ObjectPaint", ex)
                    Finally
                        coverage?.Dispose()
                    End Try
                    Avalonia.Threading.Dispatcher.UIThread.Post(
                        Sub()
                            If Not String.Equals(documentStamp, _selectionAssetTempDir, StringComparison.Ordinal) Then Return
                            ApplyObjectPaintResult(target, targetPath, ok)
                        End Sub)
                End Sub, TaskScheduler.Default)
        End Sub

        ''' <summary>Das Ergebnis eines Zuges übernehmen - auf dem UI-Faden und in der Reihenfolge,
        ''' in der die Züge gemalt wurden. Der Schnappschuss entsteht genau hier: er trägt den
        ''' vorigen Pfad, und der IST das Rückgängig.</summary>
        Private Sub ApplyObjectPaintResult(target As ImageAnnotation, newPath As String, ok As Boolean)
            If Not ok OrElse String.IsNullOrEmpty(newPath) Then
                StatusText = LocalizationService.T("Malen fehlgeschlagen")
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
                                                 dirty As SKRectI, coverage As SKBitmap) As Boolean
            Using decoded = SKBitmap.Decode(sourcePath)
                If decoded Is Nothing OrElse decoded.Width <= 0 OrElse decoded.Height <= 0 Then Return False
                Dim clamped = ClampRectToBitmap(dirty, decoded.Width, decoded.Height)
                If clamped.Width <> dirty.Width OrElse clamped.Height <> dirty.Height Then Return False
                Using painted = PaintStrokeOntoImageCopy(decoded, renderAnnotation, dirty, coverage)
                    If painted Is Nothing Then Return False
                    Return WriteObjectPaintFile(painted, targetPath)
                End Using
            End Using
        End Function

        ''' <summary>Zeichnet den Strich in eine ARBEITSKOPIE des Objektbilds. Die Kopie liegt in
        ''' Bgra8888/Premul: nur dort stanzt der Radierer echte Löcher (DstOut), und ein JPEG kommt
        ''' ganz ohne Alphakanal herein. Eine Auswahl wird nach dem Zeichnen wieder herausgerechnet -
        ''' dieselbe Nachnahme wie im Foto, damit die Zeichenroutine nichts davon wissen muss.</summary>
        Private Shared Function PaintStrokeOntoImageCopy(source As SKBitmap, renderAnnotation As ImageAnnotation,
                                                         dirty As SKRectI, coverage As SKBitmap) As SKBitmap
            Dim copy = New SKBitmap(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Try
                Using canvas = New SKCanvas(copy)
                    canvas.Clear(SKColors.Transparent)
                    Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                        canvas.DrawBitmap(source, 0, 0, paint)
                    End Using
                End Using

                Dim before As SKBitmap = Nothing
                If coverage IsNot Nothing Then before = ImageProcessor.CopyRegion(copy, dirty)
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
        ''' nicht".</summary>
        Private Function BuildSelectionCoverage(ByRef rect As SKRectI,
                                                mapToDisplay As Func(Of Double, Double, SKPoint?),
                                                mapFromDisplay As Func(Of Double, Double, SKPoint?),
                                                ByRef anyCoverage As Boolean) As SKBitmap
            anyCoverage = False
            If Not HasPixelSelectionScope Then Return Nothing
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
