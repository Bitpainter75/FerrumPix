Imports System
Imports System.Buffers
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.IO
Imports System.Linq
Imports System.Runtime.CompilerServices
Imports System.Threading
Imports System.Threading.Tasks
Imports SkiaSharp
Imports Avalonia.Media.Imaging
Imports Avalonia.Platform
Imports System.Text.RegularExpressions
Imports System.Text.Json.Serialization
Imports System.Runtime.InteropServices
Imports QRCoder

' Speichern und Ausgabe: SaveImage mit der ganzen Kette, der PSD-Export mit Ebenen und das
' Uebertragen der Metadaten in die Zieldatei (JPEG-Segmente, PNG-Chunks, WebP-Chunks) samt der
' Byte-Helfer dafuer. Kein geteilter Zustand, reine Byte-Arbeit.
' Herausgeloest am 2026-08-06 aus ImageProcessor.vb, Zeile fuer Zeile unveraendert.
Namespace Services

    Partial Public Class ImageProcessor

        ''' <summary>True, wenn SaveImage das Ziel-FORMAT dieser Endung tatsächlich erzeugen kann
        ''' (JPEG/PNG/WEBP/PDF). Alles andere lehnt SaveImage ab, statt still JPEG-Bytes unter
        ''' fremder Endung zu schreiben - Aufrufer nutzen die Funktion, um
        ''' in-place-Speichern/Umschreiben vorab auszuschließen und auf "Speichern unter" zu lenken.</summary>
        Public Shared Function CanEncodeToTargetExtension(path As String) As Boolean
            Select Case IO.Path.GetExtension(If(path, "")).ToLowerInvariant()
                Case ".jpg", ".jpeg", ".png", ".webp", ".pdf"
                    Return True
                Case ".fpx"
                    ' Kein Bildformat, sondern ein Projektbündel - geschrieben wird es unten über
                    ' FpxService, nicht über Skia. Ist das Format abgeschaltet, bleibt es verboten.
                    Return FpxService.Enabled
                Case Else
                    Return False
            End Select
        End Function

        ''' <paramref name="workingFull"/>: voll aufgelöstes ARBEITSBILD des Editors (Umbau Stufe C) -
        ''' wenn gesetzt, ersetzt es den Datei-Decode als Pipeline-Eingang (Besitz wechselt hierher,
        ''' wird disposed). Aufrufer übergeben einen Klon (WorkingImageService.CloneFull).
        ''' <param name="applyPendingBaked">Vermerkte, aber noch nicht in den Pixeln steckende
        ''' Vorgaenge mitrechnen (Entrauschen, Objektentfernen, Retusche, Striche)? Wirkt NUR, wenn
        ''' hier selbst dekodiert wird - also im Stapel. Kommt ein fertiges Arbeitsbild herein
        ''' (<paramref name="workingFull"/>), stecken die Vorgaenge bereits darin, und ein zweiter
        ''' Durchlauf wuerde sie doppelt anwenden.</param>
        ''' <summary>Schreibt das Dokument als Photoshop-Datei MIT Ebenen - der Weg aus dem eigenen
        ''' Format hinaus zu Photoshop, Affinity oder GIMP.
        '''
        ''' Bewusst ein eigener Einstieg und NICHT Teil von SaveImage: dort ist ein Ziel mit
        ''' .psd-Endung streng verboten, und das aus gutem Grund - die Sperre hat schon einmal
        ''' verhindert, dass eine Originaldatei durch ihre eigene Vorschau ersetzt wird. Ein Export
        ''' ist etwas anderes als ein Speichern: er wird ausdrücklich verlangt, schreibt nie über die
        ''' Quelle und geht durch einen eigenen Schreiber.
        '''
        ''' <paramref name="composite"/> ist das fertige Bild, wie der Nutzer es sieht; es wird als
        ''' Gesamtbild eingebettet, damit fremde Programme sofort das Richtige zeigen.
        ''' <paramref name="background"/> ist dasselbe Bild OHNE die Objekte - es wird die unterste
        ''' Ebene. Jedes Objekt darüber bekommt seine eigene Ebene.
        '''
        ''' Was dabei fest wird: Korrekturebenen, Text und Formen kommen als Bildpunkte heraus. In
        ''' der .fpx bleiben sie veränderbar, im PSD nicht - Photoshop legt sie je Art in einem
        ''' eigenen, kaum dokumentierten Datensatz ab.</summary>
        Public Shared Function ExportLayeredPsd(targetPath As String, composite As SKBitmap,
                                                background As SKBitmap, adj As ImageAdjustments) As Boolean
            If String.IsNullOrWhiteSpace(targetPath) OrElse composite Is Nothing Then Return False

            Dim layers As New List(Of PsdWriterService.PsdLayerInput)()
            Dim width = composite.Width
            Dim height = composite.Height

            Try
                If background IsNot Nothing Then
                    layers.Add(New PsdWriterService.PsdLayerInput With {
                        .Name = LocalizationService.T("Hintergrund"),
                        .Pixels = background,
                        .Left = 0,
                        .Top = 0
                    })
                End If

                ' Die Gruppen, die gerade offen sind - von außen nach innen. Eine Gruppe ist im PSD
                ' kein Behälter, sondern eine Klammer aus zwei Datensätzen; welche davon fällig
                ' sind, ergibt sich aus dem Vergleich mit der Gruppenkette der nächsten Ebene.
                Dim openChain As New List(Of AnnotationGroup)()

                If adj?.Annotations IsNot Nothing Then
                    For Each annotation In adj.Annotations
                        If annotation Is Nothing Then Continue For

                        ' Die Bildpunkte OHNE Deckkraft, Mischmethode, Beschneidung und Maske
                        ' rendern: die vier trägt das PSD selbst. Würde der Renderer sie einbacken
                        ' und das Format sie noch einmal anwenden, käme alles doppelt heraus - eine
                        ' halb durchsichtige Ebene wäre plötzlich zu einem Viertel sichtbar.
                        ' Sichtbarkeit ebenso: eine ausgeblendete Ebene wird trotzdem gezeichnet und
                        ' reist als ausgeblendete Ebene mit, statt unterwegs verloren zu gehen.
                        Dim forRender = annotation.Clone()
                        forRender.Opacity = 100
                        forRender.BlendMode = "Normal"
                        forRender.ClipToLayerBelow = False
                        forRender.MaskId = ""
                        forRender.IsVisible = True

                        Dim layerBitmap = New SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul)
                        Dim drawn = 0
                        Using canvas = New SKCanvas(layerBitmap)
                            canvas.Clear(SKColors.Transparent)
                            drawn = DrawAnnotationsOnCanvas(canvas, adj, width, height, 0, 0, width, height,
                                                            New List(Of ImageAnnotation) From {forRender})
                        End Using

                        If drawn = 0 Then
                            layerBitmap.Dispose()
                            Continue For
                        End If

                        ' Eng um den Inhalt zuschneiden. Gezeichnet wird auf einer Leinwand in voller
                        ' Bildgroesse, weil das Zeichnen die Lage im Bild braucht - in die Datei
                        ' gehoert aber nur das belegte Rechteck. Ohne das ist jede Ebene beim
                        ' Zurueckladen bildgross, und Auswahlrahmen, Anfasser und Miniaturbild
                        ' ziehen sich ueber das ganze Bild.
                        Dim layerLeft = 0
                        Dim layerTop = 0
                        Dim cropped = PsdWriterService.CropToContent(layerBitmap, layerLeft, layerTop)
                        If cropped IsNot Nothing Then
                            layerBitmap.Dispose()
                            layerBitmap = cropped
                        End If

                        ' Fällt die Beschriftung aus, tritt die Art an ihre Stelle - ein technisches
                        ' Wort, aber nie leer und ohne neuen Ressourcentext.
                        Dim layerName = If(String.IsNullOrWhiteSpace(annotation.LayerLabel),
                                           If(annotation.Kind, "Layer"), annotation.LayerLabel)

                        ' Die Maske als eigener Kanal statt eingebacken. Sie kommt fertig gerechnet
                        ' aus dem Renderweg - mit allen Bestandteilen, ihrer Dichte und ihrer
                        ' Umkehrung -, denn ein Verlauf oder eine mehrteilige Maske hat im PSD kein
                        ' Gegenstück und muss dort ein Raster werden. Anfassbar bleibt sie trotzdem:
                        ' in Photoshop lässt sie sich weiter übermalen.
                        Dim maskBitmap = BuildMaskRaster(adj, annotation.MaskId, width, height,
                                                         layerLeft, layerTop,
                                                         layerBitmap.Width, layerBitmap.Height)

                        ' Erst die Klammern nachziehen, dann die Ebene. In dieser Reihenfolge, weil
                        ' das untere Ende einer Gruppe VOR ihrem ersten Mitglied stehen muss.
                        SyncGroupMarkers(layers, openChain, adj, adj.GroupChainOf(annotation.GroupId),
                                         width, height)

                        ' Bei der Sichtbarkeit steht hier NUR das eigene Auge. Die der Gruppe steht
                        ' an der Gruppe, und beides zugleich hieße, sie zweimal anzuwenden - beim
                        ' Zurückladen wäre die Ebene dann dauerhaft ausgeblendet. Der Kommentar
                        ' steht vor der Liste und nicht darin: VB bricht die Zeilenfortsetzung nach
                        ' einem Komma ab, sobald ein Kommentar dazwischenkommt.
                        layers.Add(New PsdWriterService.PsdLayerInput With {
                            .Name = layerName,
                            .Pixels = layerBitmap,
                            .Left = layerLeft,
                            .Top = layerTop,
                            .OpacityPercent = annotation.Opacity,
                            .BlendMode = annotation.BlendMode,
                            .ClipToLayerBelow = annotation.ClipToLayerBelow,
                            .IsVisible = annotation.IsVisible,
                            .MaskPixels = maskBitmap,
                            .MaskLeft = layerLeft,
                            .MaskTop = layerTop
                        })
                    Next

                    ' Was am Ende noch offen ist, wird geschlossen - sonst stünde in der Datei eine
                    ' Gruppe ohne Kopfzeile, und Photoshop meldet das als beschädigt.
                    SyncGroupMarkers(layers, openChain, adj, New List(Of AnnotationGroup)(), width, height)
                End If

                ' Das Rezept fuer den Rueckweg mit hineinlegen, damit die eigene Datei spaeter wieder
                ' mit Text als Text und Formen als Formen aufgeht. Fremde Programme ueberspringen den
                ' Block; wird er zu gross, bleibt er weg und die Datei ist eine gewoehnliche PSD.
                '
                ' NICHT die volle Bearbeitung: die unterste Ebene ist bereits fertig durchgerechnet -
                ' Regler, Korrekturebenen, Retusche und eingebackene Vorgaenge stecken in ihren
                ' Bildpunkten. Truege das Rezept sie noch einmal, wirkte beim Wiederoeffnen alles
                ' doppelt (Belichtung +1 wuerde +2, der Beschnitt schnitte zweimal), denn das
                ' Grundbild des Rezeptwegs IST diese unterste Ebene.
                Dim roundtrip = BuildPsdRoundtripRecipe(adj)
                Return PsdWriterService.Save(targetPath, composite, layers,
                                             If(roundtrip Is Nothing, Nothing, PsdRecipeService.Build(roundtrip)))
            Finally
                ' Nur die selbst erzeugten Objektebenen freigeben - Hintergrund und Gesamtbild
                ' gehören dem Aufrufer und werden hier nur gelesen. Die Maskenraster entstehen
                ' immer hier und gehen immer mit.
                For Each layer In layers
                    If layer.Pixels IsNot Nothing AndAlso Not Object.ReferenceEquals(layer.Pixels, background) Then
                        layer.Pixels.Dispose()
                    End If
                    layer.MaskPixels?.Dispose()
                Next
            End Try
        End Function

        ''' <summary>Zieht die Gruppenklammern nach, bis die offene Kette der gewünschten entspricht.
        '''
        ''' Verglichen wird das gemeinsame Stück von außen her: was darüber hinaus offen ist, wird
        ''' geschlossen (von innen nach außen), was fehlt, wird geöffnet (von außen nach innen).
        ''' Damit entstehen genau so viele Klammern wie nötig - eine Reihe von Ebenen derselben
        ''' Gruppe öffnet sie einmal und schließt sie einmal.
        '''
        ''' Die Reihenfolge im Format ist dabei umgekehrt zur Anschauung: die Datei zählt von unten
        ''' nach oben, also kommt das ENDE einer Gruppe vor ihren Inhalt und ihre Zeile mit Namen und
        ''' Deckkraft danach.</summary>
        Private Shared Sub SyncGroupMarkers(layers As List(Of PsdWriterService.PsdLayerInput),
                                            openChain As List(Of AnnotationGroup),
                                            adj As ImageAdjustments,
                                            wanted As List(Of AnnotationGroup),
                                            sourceWidth As Integer, sourceHeight As Integer)
            Dim target = If(wanted, New List(Of AnnotationGroup)())

            Dim common = 0
            While common < openChain.Count AndAlso common < target.Count AndAlso
                  String.Equals(openChain(common).Id, target(common).Id, StringComparison.Ordinal)
                common += 1
            End While

            For k = openChain.Count - 1 To common Step -1
                Dim group = openChain(k)
                openChain.RemoveAt(k)
                ' Die Gruppenmaske gilt für alles, was in der Gruppe liegt. Sie bekommt das ganze
                ' Dokument als Rechteck, weil die Gruppenzeile selbst keine Fläche hat, an der sich
                ' ein engeres Rechteck festmachen ließe.
                Dim groupMask = BuildMaskRaster(adj, group.MaskId, sourceWidth, sourceHeight,
                                                0, 0, sourceWidth, sourceHeight)
                layers.Add(New PsdWriterService.PsdLayerInput With {
                    .Name = group.Name,
                    .SectionType = If(group.IsCollapsed, 2, 1),
                    .OpacityPercent = CSng(group.Opacity),
                    .BlendMode = group.BlendMode,
                    .IsVisible = group.IsVisible,
                    .MaskPixels = groupMask,
                    .MaskLeft = 0,
                    .MaskTop = 0
                })
            Next

            For k = common To target.Count - 1
                openChain.Add(target(k))
                ' Der Name des unteren Endes ist Konvention und steht in jeder Photoshop-Datei so
                ' drin; er wird nie angezeigt, aber ältere Leser suchen genau danach.
                layers.Add(New PsdWriterService.PsdLayerInput With {
                    .Name = "</Layer group>",
                    .SectionType = 3
                })
            Next
        End Sub

        ''' <summary>Eine Maske als Alpha8-Raster im angegebenen Rechteck, oder Nothing, wenn es
        ''' keine gibt.
        '''
        ''' Bei einer Objektebene wird auf GENAU ihr Rechteck zugeschnitten. Außerhalb davon hat die
        ''' Ebene keine Bildpunkte, dort ist die Maske also gegenstandslos - und eine bildgroße Maske
        ''' je Ebene bliese die Datei um ein Vielfaches auf.
        '''
        ''' Die SCHNITTMASKE bleibt draußen, obwohl sie im Renderweg dieselbe Deckung erzeugt: sie
        ''' steht im PSD als eigenes Merkmal am Ebenendatensatz und wird dort geschrieben. Beides
        ''' zugleich hieße, sie zweimal anzuwenden.</summary>
        Private Shared Function BuildMaskRaster(adj As ImageAdjustments, maskId As String,
                                                sourceWidth As Integer, sourceHeight As Integer,
                                                layerLeft As Integer, layerTop As Integer,
                                                layerWidth As Integer, layerHeight As Integer) As SKBitmap
            If adj Is Nothing OrElse String.IsNullOrEmpty(maskId) OrElse adj.Masks Is Nothing Then Return Nothing
            If layerWidth < 1 OrElse layerHeight < 1 Then Return Nothing

            Try
                Dim maskData = adj.Masks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso
                                                            String.Equals(m.Id, maskId, StringComparison.Ordinal))
                Dim coverage = GetAnnotationMaskCoverage(maskData, adj, sourceWidth, sourceHeight)
                If coverage Is Nothing Then Return Nothing

                Dim plane(layerWidth * layerHeight - 1) As Byte
                For y = 0 To layerHeight - 1
                    Dim sy = layerTop + y
                    If sy < 0 OrElse sy >= sourceHeight Then Continue For
                    For x = 0 To layerWidth - 1
                        Dim sx = layerLeft + x
                        If sx < 0 OrElse sx >= sourceWidth Then Continue For
                        plane(y * layerWidth + x) = coverage(sy * sourceWidth + sx)
                    Next
                Next

                Dim bmp = New SKBitmap(New SKImageInfo(layerWidth, layerHeight, SKColorType.Alpha8, SKAlphaType.Premul))
                Try
                    ' ZEILENWEISE über RowBytes: Skia darf die Zeilenlänge aufrunden, und ein
                    ' Kopieren am Stück verschöbe dann jede Zeile gegen die vorige.
                    Dim target = bmp.GetPixels()
                    Dim stride = bmp.RowBytes
                    For y = 0 To layerHeight - 1
                        Runtime.InteropServices.Marshal.Copy(plane, y * layerWidth,
                                                             IntPtr.Add(target, y * stride), layerWidth)
                    Next
                    Return bmp
                Catch
                    bmp.Dispose()
                    Return Nothing
                End Try
            Catch
                ' Eine Maske, die sich nicht schreiben lässt, kostet die Maske und nicht die Datei.
                Return Nothing
            End Try
        End Function

        ''' <summary>Das Rezept, das in eine exportierte PSD gehoert: NUR die Objekte samt ihren
        ''' Gruppen und Masken, alles andere neutral.
        '''
        ''' Aufgebaut als AUFZAEHLUNG dessen, was mitkommt, nicht als Streichliste: ein neuer
        ''' globaler Regler bleibt damit von selbst draussen, statt beim Vergessen doppelt zu
        ''' wirken. Was die unterste Ebene schon traegt (Regler, Korrekturebenen, Retusche,
        ''' Striche, eingebackene Vorgaenge), kommt beim Wiederoeffnen aus ihren Bildpunkten -
        ''' es ist dann Bestandteil des Fotos, so wie es auch jedes fremde Programm sieht.
        '''
        ''' Nothing heisst: KEIN Rezeptblock, die Datei oeffnet als gewoehnliche Ebenen-PSD. Zwei
        ''' Faelle erzwingen das, weil das Rezept sonst sichtbar Falsches ergaebe:
        ''' - Aktive GEOMETRIE (Beschnitt, Drehung, Begradigen, Perspektive, Verzerren, Zielmass,
        '''   Leinwand, Hochskalier-Modell): Objekte und Masken stehen im Quellraum des
        '''   UNBESCHNITTENEN Bilds, das Grundbild des Rezeptwegs ist aber die fertig gerechnete
        '''   Ausgabe. Die Objekte saessen neben ihrer Stelle, die Masken ebenso - die Umrechnung
        '''   der Masken durch die Geometriekette gibt es bislang nur zur Renderzeit.
        ''' - TRANSPARENZ im Arbeitsbild (Radierer): die unterste Ebene ist dann nicht deckend,
        '''   der Import nimmt sie nicht als Grundbild und der Rezeptweg begaenne mit einer
        '''   leeren Flaeche.</summary>
        Friend Shared Function BuildPsdRoundtripRecipe(adj As ImageAdjustments) As ImageAdjustments
            If adj Is Nothing Then Return Nothing
            If adj.WorkingImageHasTransparency Then Return Nothing

            ' Dieselben Felder, durch die auch eine Maske auf dem Weg in die Ausgabe laeuft
            ' (BuildMaskGeometry) - dazu das Hochskalier-Modell, das die Ausgabegroesse aendert.
            Dim geometryActive =
                adj.CropLeftPercent <> 0 OrElse adj.CropTopPercent <> 0 OrElse
                adj.CropRightPercent <> 0 OrElse adj.CropBottomPercent <> 0 OrElse
                adj.RotationDegrees <> 0 OrElse adj.StraightenDegrees <> 0 OrElse
                adj.FlipHorizontal OrElse adj.FlipVertical OrElse
                adj.PerspectiveHorizontal <> 0 OrElse adj.PerspectiveVertical <> 0 OrElse
                adj.PerspectiveAspect <> 0 OrElse adj.PerspectiveScale <> 0 OrElse
                adj.PerspectiveCorner0X <> 0 OrElse adj.PerspectiveCorner0Y <> 0 OrElse
                adj.PerspectiveCorner1X <> 0 OrElse adj.PerspectiveCorner1Y <> 0 OrElse
                adj.PerspectiveCorner2X <> 0 OrElse adj.PerspectiveCorner2Y <> 0 OrElse
                adj.PerspectiveCorner3X <> 0 OrElse adj.PerspectiveCorner3Y <> 0 OrElse
                (adj.ImageWarp IsNot Nothing AndAlso Not adj.ImageWarp.IsEmpty) OrElse
                adj.ResizeWidth > 0 OrElse adj.ResizeHeight > 0 OrElse adj.ResizeScalePercent > 0 OrElse
                adj.CanvasWidth > 0 OrElse adj.CanvasHeight > 0 OrElse
                Not String.IsNullOrEmpty(adj.UpscaleModel)
            If geometryActive Then Return Nothing

            Return New ImageAdjustments With {
                .SourceWidthPixels = adj.SourceWidthPixels,
                .SourceHeightPixels = adj.SourceHeightPixels,
                .RecipeCoordinateVersion = adj.RecipeCoordinateVersion,
                .Annotations = If(adj.Annotations, New List(Of ImageAnnotation)()).
                    Where(Function(a) a IsNot Nothing).Select(Function(a) a.Clone()).ToList(),
                .AnnotationGroups = If(adj.AnnotationGroups, New List(Of AnnotationGroup)()).
                    Where(Function(g) g IsNot Nothing).Select(Function(g) g.Clone()).ToList(),
                .Masks = If(adj.Masks, New List(Of ImageMask)()).
                    Where(Function(m) m IsNot Nothing).Select(Function(m) m.Clone()).ToList()}
        End Function

        Public Shared Function SaveImage(sourcePath As String, targetPath As String, adj As ImageAdjustments, quality As Integer,
                                         Optional preserveMetadata As Boolean = True,
                                         Optional workingFull As SKBitmap = Nothing,
                                         Optional developRaw As Boolean = True,
                                         Optional applyPendingBaked As Boolean = False,
                                         Optional copyrightText As String = "",
                                         Optional cancel As Threading.CancellationToken = Nothing) As Boolean
            ' Zentraler Schutz: Bearbeitung einer RAW-Quelle wirkt nur auf deren eingebettete
            ' JPEG-Vorschau (siehe OpenSourceStream/DecodeOriented) - ein Speichern-in-place würde
            ' hier fälschlich die RAW-Rohdaten JPEG-kodiert über die Original-RAW-Datei schreiben.
            ' PSD/PSB sind NUR-LESEND (die Pipeline sieht nur das zusammengesetzte Gesamtbild,
            ' Ebenen gingen beim Überschreiben verloren) - gleiches Verbot.
            '
            ' Verboten ist nicht nur der GLEICHE Pfad, sondern jedes Ziel mit RAW-/PSD-Endung:
            ' die Viewer-Drehung schrieb in eine Temp-Datei MIT der Endung des Originals
            ' (".foto.ferrumpix-rotate-1234.cr2") und kopierte sie erst danach über die Quelle -
            ' der Pfadvergleich sah zwei verschiedene Dateien, und die Formatwahl unten machte aus
            ' ".cr2" mangels eigenem Zweig ein JPEG. Ergebnis: die Original-RAW war unwiederbringlich
            ' durch ihre eigene eingebettete Vorschau ersetzt. Ein Ziel
            ' mit RAW-/PSD-Endung ist IMMER falsch - wir können diese Formate nicht schreiben.
            If RawPreviewService.IsSupportedRaw(targetPath) OrElse PsdPreviewService.IsSupportedPsd(targetPath) OrElse
               HeifDecodeService.IsSupportedHeif(targetPath) OrElse TiffPreviewService.IsSupportedTiff(targetPath) Then
                workingFull?.Dispose()
                Return False
            End If
            If (RawPreviewService.IsSupportedRaw(sourcePath) OrElse PsdPreviewService.IsSupportedPsd(sourcePath) OrElse
                HeifDecodeService.IsSupportedHeif(sourcePath) OrElse TiffPreviewService.IsSupportedTiff(sourcePath)) AndAlso
               PathIdentity.AreSame(sourcePath, targetPath) Then
                workingFull?.Dispose()
                Return False
            End If
            ' Dieselbe Fehlerklasse eine Formatstufe weiter: die Formatwahl unten
            ' kennt nur PNG/WEBP/PDF und faellt sonst auf JPEG zurueck. Ein Ziel ".tiff"/".bmp"/
            ' ".gif"/".heic"/... bekam damit still JPEG-Bytes unter fremder Endung - beim
            ' in-place-Speichern wurde das Original verlustbehaftet konvertiert UND falsch
            ' etikettiert. Verboten wird am Ziel-FORMAT, nicht am Pfad (siehe RAW-Lehre oben).
            If Not CanEncodeToTargetExtension(targetPath) Then
                DiagnosticLogService.LogAlways("ImageProcessor.Save",
                    $"refused: target extension not encodable target={IO.Path.GetFileName(targetPath)}")
                workingFull?.Dispose()
                Return False
            End If
            Try
                ' .fpx-Projekte beim echten Speichern/Konvertieren immer aus Basisbild + Rezept rendern.
                ' composite.png ist nur ein schnelles Anzeige-/Thumbnail-Bild und kann bewusst verkleinert sein.
                Dim isFpxSource = FpxService.IsFpx(sourcePath)
                Dim decoded = If(workingFull, If(isFpxSource, RenderFpxFullResolution(sourcePath), DecodeOriented(sourcePath, developRaw)))
                ' Nur auf dem SELBST dekodierten Bild: ein hereingereichtes Arbeitsbild traegt die
                ' Vorgaenge schon, und ein zweites Entrauschen sieht man erst, wenn man die Bilder
                ' nebeneinanderlegt. Und VOR der Reglerkette, weil sie zum Bild gehoeren und nicht
                ' zu den Reglern.
                If workingFull Is Nothing AndAlso applyPendingBaked AndAlso decoded IsNot Nothing Then
                    Dim reapplied = ApplyPendingBakedOperations(decoded, adj, cancel)
                    If reapplied IsNot Nothing Then
                        decoded.Dispose()
                        decoded = reapplied
                    End If
                End If

                ' ABGEBROCHEN heisst: KEINE Datei. Ein Entrauschen, das der Nutzer angehalten hat,
                ' gibt Nothing zurueck - ohne diese Frage liefe der Weg weiter und schriebe das
                ' Bild ohne den Vorgang, den der Stapel gerade versprochen hat.
                If cancel.IsCancellationRequested Then
                    decoded?.Dispose()
                    Return False
                End If

                ' Hochskalieren mit Modell - HIER und nur hier, also im Speicherweg. Es steht VOR der
                ' Reglerkette: dann kann deren Groessenaenderung danach noch auf ein Zielmass
                ' verkleinern, und das ist die richtige Reihenfolge. Faellt es aus (Modell fehlt,
                ' Speicher reicht nicht), wird gespeichert wie ohne - eine Vergroesserung, die nicht
                ' geht, darf keine Datei kosten.
                If decoded IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(adj.UpscaleModel) Then
                    Dim upscaled = UpscaleModelService.Upscale(decoded, adj.UpscaleModel, cancel)
                    If upscaled IsNot Nothing Then
                        decoded.Dispose()
                        decoded = upscaled
                    ElseIf cancel.IsCancellationRequested Then
                        ' Angehalten, nicht ausgefallen: hier darf NICHTS geschrieben werden. Sonst
                        ' laege nach einem Abbruch eine Datei in Originalgroesse im Zielordner, und
                        ' der Nutzer haelt sie fuer das vergroesserte Ergebnis.
                        decoded.Dispose()
                        Return False
                    Else
                        DiagnosticLogService.LogAlways("Hochskalieren",
                            $"nicht angewandt auf {IO.Path.GetFileName(sourcePath)} - es wird ohne gespeichert")
                    End If
                End If

                Using original = decoded
                    If original Is Nothing Then Return False

                    Dim ext = IO.Path.GetExtension(targetPath).ToLowerInvariant()
                    Dim isPdf = ext = ".pdf"
                    ' Ziel .fpx: kein Encode, sondern ein Projektbündel aus Originaldatei + Rezept +
                    ' gerendertem Vorschaubild. Ein Stapel-Export bleibt damit weiterbearbeitbar,
                    ' statt nur ein fertiges Bild zu liefern. Eine .fpx als QUELLE bleibt außen vor -
                    ' das Bündel würde sich sonst selbst als Basisbild eintragen.
                    Dim isFpxTarget = ext = ".fpx" AndAlso FpxService.Enabled AndAlso Not isFpxSource
                    If ext = ".fpx" AndAlso Not isFpxTarget Then
                        DiagnosticLogService.LogAlways("ImageProcessor.Save",
                            $"refused: .fpx-Ziel nicht moeglich target={IO.Path.GetFileName(targetPath)}")
                        Return False
                    End If
                    Dim fileFormat = If(ext = ".png", SKEncodedImageFormat.Png,
                                 If(ext = ".webp", SKEncodedImageFormat.Webp,
                                    SKEncodedImageFormat.Jpeg))

                    Using processed = ProcessBitmap(original, adj)
                        ' DIE LETZTE FRAGE VOR DEM SCHREIBEN. Die Modellwege steigen an ihrer
                        ' Kachelgrenze aus, aber die Reglerkette darunter laeuft am Stueck und kostet
                        ' bei einem grossen Bild Sekunden. Wer in dieser Zeit abbricht, bekaeme sonst
                        ' doch noch eine Datei - und die saehe aus wie das Ergebnis.
                        If cancel.IsCancellationRequested Then Return False
                        If isFpxTarget Then
                            ' composite.png ist nur das Anzeigebild des Bündels und bewusst gedeckelt -
                            ' die volle Auflösung entsteht beim Öffnen wieder aus Basisbild + Rezept.
                            Using composite = EncodePngStream(processed, FpxCompositeMaxDimension)
                                If composite Is Nothing Then Return False
                                FpxService.Save(targetPath, adj, sourcePath, composite)
                            End Using
                            Return IO.File.Exists(targetPath)
                        End If

                        ' JPEG und PDF kennen kein Alpha: transparente Bereiche (Radierer-Löcher,
                        ' ausgeblendeter Hintergrund) liefen beim Encode auf SCHWARZ
                        '. Auf WEISS flatten - wie Photoshop.
                        Dim toEncode = processed
                        If isPdf OrElse fileFormat = SKEncodedImageFormat.Jpeg Then
                            toEncode = FlattenAlphaToWhite(processed)
                        End If
                        Try
                            If isPdf Then
                                ' Druckfertiges einseitiges PDF mit dem zuletzt im Druckdialog
                                ' gewählten Seitenlayout - so sehen Drucken und PDF-Export gleich aus.
                                If Not PrintService.WriteSinglePagePdf(toEncode, targetPath,
                                                                      AppSettingsService.Load().ToPrintOptions()) Then Return False
                            Else
                                Using image = SKImage.FromBitmap(toEncode)
                                    Using data = image.Encode(fileFormat, quality)
                                        ' Atomar: erst daneben schreiben, dann darüberbewegen - ein
                                        ' abgebrochener Encode darf das Original nicht zerstören.
                                        WriteFileAtomic(targetPath, Sub(fs) data.SaveTo(fs))
                                    End Using
                                End Using
                            End If
                        Finally
                            If Not Object.ReferenceEquals(toEncode, processed) Then toEncode.Dispose()
                        End Try
                    End Using
                    ' Metadaten nur von echten Bildquellen kopieren (ein .fpx-Bündel trägt keine).
                    ' In ein PDF lässt sich kein EXIF-Block kopieren - der Versuch würde die Datei
                    ' beschädigen.
                    If preserveMetadata AndAlso Not isFpxSource AndAlso Not isPdf AndAlso Not isFpxTarget Then TryCopyMetadata(sourcePath, targetPath)
                    ' Der Urheberrechtshinweis kommt NACH dem Kopieren der Metadaten: sonst
                    ' ueberschriebe der Hinweis aus der Quelle den gerade gesetzten wieder. Ein
                    ' leerer Text tut nichts - das ist die Regel der Stapelformulare, "leer heisst
                    ' dieses Feld nicht anfassen". In ein PDF und in ein Buendel geht er nicht.
                    If Not isPdf AndAlso Not isFpxTarget Then ApplyCopyright(targetPath, copyrightText)
                    Return True
                End Using
            Catch ex As Exception
                Return False
            End Try
        End Function

        ''' <summary>Weißer Untergrund für Formate ohne Alphakanal (JPEG). Liefert das Original
        ''' zurück, wenn nichts zu tun ist; sonst ein NEUES Bitmap (Aufrufer disposed es).</summary>
        Private Shared Function FlattenAlphaToWhite(source As SKBitmap) As SKBitmap
            If source Is Nothing Then Return source
            Dim flattened = New SKBitmap(source.Width, source.Height, source.ColorType, SKAlphaType.Premul)
            Using canvas = New SKCanvas(flattened)
                canvas.Clear(SKColors.White)
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return flattened
        End Function

        ''' <summary>Rendert ein .fpx-Bündel in voller Basisauflösung. Der Aufrufer übernimmt das SKBitmap.</summary>
        Private Shared Function RenderFpxFullResolution(fpxPath As String) As SKBitmap
            Dim loaded = FpxService.Load(fpxPath)
            If loaded Is Nothing OrElse String.IsNullOrWhiteSpace(loaded.BaseImagePath) OrElse Not File.Exists(loaded.BaseImagePath) Then Return Nothing
            Try
                ' Gebackenes Arbeitsbild (voll aufgelöstes retouch.png) als Pipeline-Eingang -
                ' Pinsel-/Radiererstriche stehen seit Stufe D NUR noch dort, nicht mehr im Rezept.
                ' Ein Vorschauauflösungs-Altbestand (Seed) wird ignoriert (Maße-Check).
                Dim inputPath = loaded.BaseImagePath
                If Not String.IsNullOrWhiteSpace(loaded.RetouchStagePath) AndAlso File.Exists(loaded.RetouchStagePath) Then
                    Dim baseSize = GetOrientedImageSize(loaded.BaseImagePath)
                    Dim stageSize = GetOrientedImageSize(loaded.RetouchStagePath)
                    If baseSize.Width > 0 AndAlso stageSize.Width = baseSize.Width AndAlso stageSize.Height = baseSize.Height Then
                        inputPath = loaded.RetouchStagePath
                    End If
                End If
                Using baseBitmap = DecodeOriented(inputPath)
                    If baseBitmap Is Nothing Then Return Nothing
                    Return ProcessBitmap(baseBitmap, If(loaded.Adjustments, New ImageAdjustments()))
                End Using
            Finally
                If Not String.IsNullOrWhiteSpace(loaded.TempDir) Then
                    Try : Directory.Delete(loaded.TempDir, True) : Catch : End Try
                End If
            End Try
        End Function

        Public Shared Function RenderFpxFullResolutionBitmap(fpxPath As String) As Bitmap
            Using rendered = RenderFpxFullResolution(fpxPath)
                If rendered Is Nothing Then Return Nothing
                Return ToAvaloniaBitmap(rendered)
            End Using
        End Function

        ''' <summary>Setzt den Urheberrechtshinweis an die eben geschriebene Datei. Ein leerer Text
        ''' laesst sie unangetastet - so ist das Feld in den Stapelformularen gemeint.
        '''
        ''' Ein Fehlschlag darf das Speichern NICHT scheitern lassen: das Bild steht dann bereits auf
        ''' der Platte, und eine Rueckmeldung "nicht gespeichert" waere schlicht falsch. Er wandert
        ''' ins Diagnoselog.</summary>
        Private Shared Sub ApplyCopyright(targetPath As String, copyrightText As String)
            If String.IsNullOrWhiteSpace(copyrightText) Then Return
            Try
                Dim result = CopyrightService.WriteCopyright(targetPath, copyrightText)
                If Not result.Success Then
                    DiagnosticLogService.LogAlways("Save.Copyright", "nicht gesetzt: " & result.FailureReason)
                End If
            Catch ex As Exception
                DiagnosticLogService.LogException("Save.Copyright", ex)
            End Try
        End Sub

        Private Shared Sub TryCopyMetadata(sourcePath As String, targetPath As String)
            If String.IsNullOrWhiteSpace(sourcePath) OrElse String.IsNullOrWhiteSpace(targetPath) Then Return
            If Not File.Exists(sourcePath) OrElse Not File.Exists(targetPath) Then Return

            Try
                Dim targetExt = IO.Path.GetExtension(targetPath).ToLowerInvariant()

                Select Case targetExt
                    Case ".jpg", ".jpeg"
                        CopyJpegMetadata(sourcePath, targetPath)
                    Case ".png"
                        CopyPngMetadata(sourcePath, targetPath)
                    Case ".webp"
                        CopyWebpMetadata(sourcePath, targetPath)
                End Select
            Catch
            End Try
        End Sub

        ''' <summary>
        ''' Schreibt eine Datei so, dass die alte Fassung bis zum letzten Moment unangetastet bleibt:
        ''' erst in eine Nachbardatei, dann darüberbewegen.
        '''
        ''' `File.Open(..., FileMode.Create)` kürzt das Ziel SOFORT auf 0 Byte. Bricht das Encodieren
        ''' danach ab - voller Datenträger, Encoder-Ausnahme, abgezogenes Netzlaufwerk, Absturz -,
        ''' ist das Original weg und an seiner Stelle liegt ein Torso. Beim Speichern ÜBER das
        ''' Original (nicht "Speichern unter") ist das ein Datenverlust ohne Rückweg.
        ''' `FpxService` macht es seit jeher so; hier gilt derselbe Weg für alle Bild-Schreibpfade.
        '''
        ''' Die Nachbardatei liegt bewusst im ZIELVERZEICHNIS - `File.Move` ist nur innerhalb
        ''' desselben Dateisystems ein Umhängen und damit unteilbar; über `Path.GetTempPath` wäre
        ''' es wieder ein Kopieren mit denselben Abbruchstellen.
        ''' </summary>
        Friend Shared Function WriteFileAtomic(targetPath As String, writer As Action(Of Stream)) As Boolean
            If String.IsNullOrWhiteSpace(targetPath) OrElse writer Is Nothing Then Return False
            Dim targetDirectory = IO.Path.GetDirectoryName(targetPath)
            If Not String.IsNullOrEmpty(targetDirectory) Then Directory.CreateDirectory(targetDirectory)
            ' Prozess-ID im Namen: zwei gleichzeitig laufende FerrumPix-Instanzen (oder der
            ' Prüfstand daneben) dürfen sich nicht dieselbe Zwischendatei teilen.
            Dim tempPath = targetPath & ".fpwrite" & Environment.ProcessId.ToString() & ".tmp"
            Try
                Using fs = File.Open(tempPath, FileMode.Create, FileAccess.Write)
                    writer(fs)
                    fs.Flush()
                End Using
                File.Move(tempPath, targetPath, overwrite:=True)
                Return True
            Catch
                Try
                    If File.Exists(tempPath) Then File.Delete(tempPath)
                Catch
                End Try
                Throw
            End Try
        End Function

        ''' <summary>Byte-Fassung von <see cref="WriteFileAtomic"/> - für die Metadaten-Nachläufe,
        ''' die die fertige Datei einlesen, umbauen und zurückschreiben.</summary>
        Friend Shared Function WriteAllBytesAtomic(targetPath As String, bytes As Byte()) As Boolean
            If bytes Is Nothing Then Return False
            Return WriteFileAtomic(targetPath, Sub(fs) fs.Write(bytes, 0, bytes.Length))
        End Function

        Private Shared Function IsJpegPath(path As String) As Boolean
            Dim ext = IO.Path.GetExtension(path).ToLowerInvariant()
            Return ext = ".jpg" OrElse ext = ".jpeg"
        End Function

        Private Shared Sub CopyJpegMetadata(sourcePath As String, targetPath As String)
            Dim metadataSegments = If(IsJpegPath(sourcePath),
                                      ReadJpegMetadataSegments(sourcePath),
                                      BuildJpegMetadataSegmentsFromSource(sourcePath))
            If metadataSegments.Count = 0 Then Return

            Dim targetBytes = File.ReadAllBytes(targetPath)
            If targetBytes.Length < 4 OrElse targetBytes(0) <> &HFF OrElse targetBytes(1) <> &HD8 Then Return

            Dim stripped = StripJpegMetadataSegments(targetBytes)
            Dim insertAt = FindJpegMetadataInsertOffset(stripped)
            Dim output As New List(Of Byte)(stripped.Length + metadataSegments.Sum(Function(s) s.Length))
            output.AddRange(stripped.Take(insertAt))
            For Each segment In metadataSegments
                output.AddRange(segment)
            Next
            output.AddRange(stripped.Skip(insertAt))
            WriteAllBytesAtomic(targetPath, output.ToArray())
        End Sub

        ''' <summary>Die Metadaten-Segmente eines JPEG, SEQUENZIELL gelesen statt die Datei am Stueck.
        '''
        ''' Sie stehen alle vor dem Bilddatenstrom, und die Schleife bricht an dessen Marke ohnehin
        ''' ab - vorher wurde trotzdem die ganze Datei eingelesen. Bei einem 50-MB-Foto sind das
        ''' 50 MB fuer ein paar Kilobyte Metadaten, und der Weg laeuft bei jedem Speichern und bei
        ''' jeder Uebernahme von Metadaten. Uninteressante Segmente werden jetzt uebersprungen
        ''' (Seek) statt kopiert.</summary>
        Private Shared Function ReadJpegMetadataSegments(path As String) As List(Of Byte())
            Dim result As New List(Of Byte())()
            Try
                ' SequentialScan sagt dem System, dass vorwaerts gelesen wird - es liest dann
                ' vorausschauend und wirft die Seiten hinter uns frueher weg.
                Using stream = New FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                                              64 * 1024, FileOptions.SequentialScan)
                    Dim header(3) As Byte
                    If Not ReadExactly(stream, header, 0, 2) Then Return result
                    If header(0) <> &HFF OrElse header(1) <> &HD8 Then Return result

                    While True
                        If Not ReadExactly(stream, header, 0, 2) Then Exit While
                        If header(0) <> &HFF Then Exit While
                        Dim marker = header(1)
                        ' Bilddatenstrom und Dateiende: ab hier kommen keine Metadaten mehr.
                        If marker = &HDA OrElse marker = &HD9 Then Exit While
                        ' Marken ohne Laengenfeld.
                        If marker = &H1 OrElse (marker >= &HD0 AndAlso marker <= &HD7) Then Continue While

                        If Not ReadExactly(stream, header, 2, 2) Then Exit While
                        Dim length = (CInt(header(2)) << 8) Or CInt(header(3))
                        If length < 2 Then Exit While
                        Dim totalLength = 2 + length

                        If IsJpegMetadataMarker(marker) Then
                            Dim segment(totalLength - 1) As Byte
                            segment(0) = header(0) : segment(1) = header(1)
                            segment(2) = header(2) : segment(3) = header(3)
                            If length > 2 AndAlso Not ReadExactly(stream, segment, 4, length - 2) Then Exit While
                            If marker = &HE1 AndAlso IsExifSegment(segment) Then
                                PatchExifOrientationToNormal(segment)
                                PatchExifColorSpaceToSrgb(segment, 10)
                            End If
                            segment = WithoutXmpColorFields(segment)
                            ' Das Farbprofil der Quelle bleibt draussen, siehe IsJpegIccSegment.
                            If Not IsJpegIccSegment(segment) Then result.Add(segment)
                        Else
                            If length > 2 Then stream.Seek(length - 2, SeekOrigin.Current)
                        End If
                    End While
                End Using
            Catch ex As Exception
                DiagnosticLogService.LogException("ImageProcessor.ReadJpegMetadataSegments", ex)
            End Try

            Return result
        End Function

        ''' <summary>Liest genau so viele Bytes, wie verlangt. Ein FileStream DARF weniger liefern,
        ''' als angefordert wurde - wer das nicht behandelt, bekommt bei grossen Dateien
        ''' gelegentlich ein halb gefuelltes Segment und merkt es nie.</summary>
        Private Shared Function ReadExactly(stream As Stream, buffer As Byte(), offset As Integer, count As Integer) As Boolean
            Dim gelesen = 0
            While gelesen < count
                Dim jetzt = stream.Read(buffer, offset + gelesen, count - gelesen)
                If jetzt <= 0 Then Return False
                gelesen += jetzt
            End While
            Return True
        End Function

        Private Shared Function BuildJpegMetadataSegmentsFromSource(sourcePath As String) As List(Of Byte())
            Dim result As New List(Of Byte())()
            Dim exif = ExtractExifTiffBytes(sourcePath)
            If exif IsNot Nothing AndAlso exif.Length > 0 Then
                result.Add(CreateJpegAppSegment(&HE1, CombineBytes(Text.Encoding.ASCII.GetBytes("Exif" & ChrW(0) & ChrW(0)), exif)))
            End If

            Dim xmp = ExtractXmpBytes(sourcePath)
            If xmp IsNot Nothing AndAlso xmp.Length > 0 Then
                result.Add(CreateJpegAppSegment(&HE1, CombineBytes(Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/" & ChrW(0)), xmp)))
            End If

            Return result.Where(Function(s) s IsNot Nothing).ToList()
        End Function

        Private Shared Function CreateJpegAppSegment(marker As Byte, payload As Byte()) As Byte()
            If payload Is Nothing OrElse payload.Length + 2 > UShort.MaxValue Then Return Nothing
            Dim segment(payload.Length + 3) As Byte
            segment(0) = &HFF
            segment(1) = marker
            Dim length = payload.Length + 2
            segment(2) = CByte((length >> 8) And &HFF)
            segment(3) = CByte(length And &HFF)
            Buffer.BlockCopy(payload, 0, segment, 4, payload.Length)
            Return segment
        End Function

        Private Shared Function StripJpegMetadataSegments(bytes As Byte()) As Byte()
            Dim output As New List(Of Byte)(bytes.Length)
            output.Add(bytes(0))
            output.Add(bytes(1))

            Dim offset = 2
            While offset + 4 <= bytes.Length
                If bytes(offset) <> &HFF Then Exit While
                Dim marker = bytes(offset + 1)
                If marker = &HDA OrElse marker = &HD9 Then Exit While
                If marker = &H1 OrElse (marker >= &HD0 AndAlso marker <= &HD7) Then
                    output.Add(bytes(offset))
                    output.Add(bytes(offset + 1))
                    offset += 2
                    Continue While
                End If

                Dim length = ReadUInt16BE(bytes, offset + 2)
                If length < 2 OrElse offset + 2 + length > bytes.Length Then Exit While
                Dim totalLength = 2 + length

                If Not IsJpegMetadataMarker(marker) Then
                    output.AddRange(bytes.Skip(offset).Take(totalLength))
                End If

                offset += totalLength
            End While

            output.AddRange(bytes.Skip(offset))
            Return output.ToArray()
        End Function

        Private Shared Function FindJpegMetadataInsertOffset(bytes As Byte()) As Integer
            Dim offset = 2
            While offset + 4 <= bytes.Length AndAlso bytes(offset) = &HFF
                Dim marker = bytes(offset + 1)
                If marker <> &HE0 AndAlso marker <> &HEE Then Exit While
                Dim length = ReadUInt16BE(bytes, offset + 2)
                If length < 2 OrElse offset + 2 + length > bytes.Length Then Exit While
                offset += 2 + length
            End While
            Return offset
        End Function

        Private Shared Function IsJpegMetadataMarker(marker As Byte) As Boolean
            Return marker = &HE1 OrElse marker = &HED OrElse marker = &HE2
        End Function

        ''' <summary>Ein APP2-Segment, das das ICC-Farbprofil der QUELLE traegt.
        '''
        ''' Es darf NICHT in die Zieldatei: seit dem Farbmanagement (siehe ColorManagementService)
        ''' sind die geschriebenen Bildpunkte immer sRGB. Ein mitgereistes Adobe-RGB-Profil wuerde
        ''' sie ein zweites Mal umdeuten - das Ergebnis waere staerker verschoben als vor dem ganzen
        ''' Umbau. APP2 traegt auch anderes (etwa die Bildreihen mancher Kameras), deshalb wird an
        ''' der Kennung erkannt und nicht am Marker.</summary>
        Private Shared Function IsJpegIccSegment(segment As Byte()) As Boolean
            If segment.Length < 6 + 12 Then Return False
            If segment(1) <> &HE2 Then Return False
            ' Die Kennung steht direkt hinter Marker und Laenge, null-terminiert.
            If segment(4 + 11) <> 0 Then Return False
            Return Text.Encoding.ASCII.GetString(segment, 4, 11) = "ICC_PROFILE"
        End Function

        Private Shared Function IsExifSegment(segment As Byte()) As Boolean
            Return segment.Length >= 12 AndAlso
                   segment(4) = AscW("E"c) AndAlso segment(5) = AscW("x"c) AndAlso
                   segment(6) = AscW("i"c) AndAlso segment(7) = AscW("f"c) AndAlso
                   segment(8) = 0 AndAlso segment(9) = 0
        End Function

        Private Shared Sub PatchExifOrientationToNormal(segment As Byte())
            Try
                Dim tiff = 10
                If segment.Length < tiff + 8 Then Return
                Dim littleEndian = segment(tiff) = AscW("I"c) AndAlso segment(tiff + 1) = AscW("I"c)
                Dim bigEndian = segment(tiff) = AscW("M"c) AndAlso segment(tiff + 1) = AscW("M"c)
                If Not littleEndian AndAlso Not bigEndian Then Return

                Dim ifd0Offset = ReadUInt32Endian(segment, tiff + 4, littleEndian)
                Dim ifd0 = tiff + CInt(ifd0Offset)
                If ifd0 < 0 OrElse ifd0 + 2 > segment.Length Then Return

                Dim count = ReadUInt16Endian(segment, ifd0, littleEndian)
                Dim entryOffset = ifd0 + 2
                For i = 0 To count - 1
                    Dim entry = entryOffset + i * 12
                    If entry + 12 > segment.Length Then Return
                    Dim tag = ReadUInt16Endian(segment, entry, littleEndian)
                    If tag <> &H112 Then Continue For

                    Dim type = ReadUInt16Endian(segment, entry + 2, littleEndian)
                    Dim itemCount = ReadUInt32Endian(segment, entry + 4, littleEndian)
                    If type <> 3 OrElse itemCount < 1 Then Return

                    WriteUInt16Endian(segment, entry + 8, 1, littleEndian)
                    Return
                Next
            Catch
            End Try
        End Sub

        ''' <summary>Setzt die FARBRAUM-Angabe der uebernommenen Aufnahmedaten auf sRGB.
        '''
        ''' Notwendig geworden mit dem Farbmanagement: die geschriebenen Bildpunkte sind seit dem
        ''' Umbau immer sRGB, und das ICC-Profil der Quelle bleibt beim Kopieren draussen (siehe
        ''' IsJpegIccSegment). Die Aufnahmedaten reisen aber sonst bytegenau mit - stand dort
        ''' "Uncalibrated", behauptete die Zieldatei weiterhin einen anderen Farbraum, als sie hat.
        ''' Ein Bildprogramm, das diese Angabe auswertet, rechnete daraufhin ein zweites Mal um.
        '''
        ''' Zwei Angaben tragen die Aussage, und beide werden gesetzt:
        ''' 0xA001 (ColorSpace) im Exif-Unterverzeichnis auf 1, und 0x0001 (InteropIndex) im
        ''' Kompatibilitaets-Unterverzeichnis von "R03" (Adobe RGB) auf "R98" (sRGB). Beide Werte
        ''' passen in die vier Byte des Eintrags selbst, die Laenge des Blocks aendert sich also
        ''' nicht - genau wie beim Patch der Ausrichtung daneben.
        '''
        ''' GRENZE: XMP bleibt unangetastet. Ein Feld wie photoshop:ICCProfile stuende dort als
        ''' Text, und ein Ersetzen aenderte die Laenge des Blocks. Steht in OFFENE_PUNKTE.md.</summary>
        ''' <param name="tiffStart">Wo der TIFF-Kopf im Puffer beginnt: 10 in einem JPEG-Segment
        ''' (hinter Marker, Laenge und "Exif\0\0"), 0 in einem nackten Block aus PNG oder WebP.</param>
        ''' <summary>Wo in einem nackten EXIF-Block der TIFF-Kopf beginnt. MANCHE ERZEUGER SCHREIBEN
        ''' DEN JPEG-VORSPANN "Exif\0\0" AUCH IN DEN PNG- ODER WEBP-BLOCK. Fest mit 0 gerechnet sucht
        ''' der Fleck den Kopf an der falschen Stelle, findet weder II noch MM und kehrt STILL
        ''' zurueck - die Datei behaelt dann ihren alten Farbraumeintrag, obwohl die Bildpunkte sRGB
        ''' sind und das ICC-Profil entfernt wurde. Genau die Doppeldeutung, die der Fleck
        ''' verhindern soll.</summary>
        Private Shared Function ExifTiffStart(buffer As Byte()) As Integer
            If buffer Is Nothing OrElse buffer.Length < 14 Then Return 0
            If buffer(0) = AscW("E"c) AndAlso buffer(1) = AscW("x"c) AndAlso
               buffer(2) = AscW("i"c) AndAlso buffer(3) = AscW("f"c) AndAlso
               buffer(4) = 0 AndAlso buffer(5) = 0 Then Return 6
            Return 0
        End Function

        ''' <summary>Derselbe Block OHNE den Vorspann. Fuer jeden Weg, der die nackten TIFF-Bytes
        ''' erwartet: der JPEG-Export setzt "Exif\0\0" selbst davor, und ein durchgereichter
        ''' Vorspann stuende dann doppelt in der Datei - ungueltige Aufnahmedaten, die kein Leser
        ''' mehr aufmacht. Ohne Vorspann wird der Puffer unveraendert zurueckgegeben.</summary>
        ''' Der Parameter heisst bewusst NICHT "buffer": der Name verdeckt in VB die Klasse
        ''' System.Buffer, und BlockCopy waere darin kein Member mehr.
        Private Shared Function WithoutExifPreamble(block As Byte()) As Byte()
            Dim start = ExifTiffStart(block)
            If start <= 0 Then Return block
            Dim rest(block.Length - start - 1) As Byte
            Buffer.BlockCopy(block, start, rest, 0, rest.Length)
            Return rest
        End Function

        Private Shared Sub PatchExifColorSpaceToSrgb(buffer As Byte(), tiffStart As Integer)
            Try
                If buffer Is Nothing OrElse buffer.Length < tiffStart + 8 Then Return
                Dim littleEndian = buffer(tiffStart) = AscW("I"c) AndAlso buffer(tiffStart + 1) = AscW("I"c)
                Dim bigEndian = buffer(tiffStart) = AscW("M"c) AndAlso buffer(tiffStart + 1) = AscW("M"c)
                If Not littleEndian AndAlso Not bigEndian Then Return

                Dim ifd0 = tiffStart + CInt(ReadUInt32Endian(buffer, tiffStart + 4, littleEndian))
                Dim exifIfd = FindIfdPointer(buffer, tiffStart, ifd0, &H8769, littleEndian)
                If exifIfd <= 0 Then Return

                ' Der Farbraum selbst.
                Dim colorSpaceEntry = FindIfdEntry(buffer, exifIfd, &HA001, littleEndian)
                If colorSpaceEntry > 0 Then
                    Dim type = ReadUInt16Endian(buffer, colorSpaceEntry + 2, littleEndian)
                    If type = 3 Then WriteUInt16Endian(buffer, colorSpaceEntry + 8, 1, littleEndian)
                End If

                ' Die zweite Angabe liegt ein Verzeichnis tiefer.
                Dim interopIfd = FindIfdPointer(buffer, tiffStart, exifIfd, &HA005, littleEndian)
                If interopIfd <= 0 Then Return
                Dim indexEntry = FindIfdEntry(buffer, interopIfd, &H1, littleEndian)
                If indexEntry <= 0 Then Return

                Dim indexType = ReadUInt16Endian(buffer, indexEntry + 2, littleEndian)
                Dim indexCount = ReadUInt32Endian(buffer, indexEntry + 4, littleEndian)
                ' Nur der Fall, der inline liegt: vier Zeichen wie "R03" samt Abschluss.
                If indexType <> 2 OrElse indexCount <> 4 OrElse indexEntry + 12 > buffer.Length Then Return
                buffer(indexEntry + 8) = AscW("R"c)
                buffer(indexEntry + 9) = AscW("9"c)
                buffer(indexEntry + 10) = AscW("8"c)
                buffer(indexEntry + 11) = 0
            Catch
                ' Die Aufnahmedaten sind Beiwerk: lieber unveraendert weitergeben als den Export
                ' an einem unerwarteten Aufbau scheitern lassen.
            End Try
        End Sub

        ''' <summary>Entfernt die FARBRAUM-Felder aus einem XMP-Block.
        '''
        ''' Derselbe Grund wie bei PatchExifColorSpaceToSrgb: die Bildpunkte sind sRGB, das
        ''' ICC-Profil der Quelle bleibt draussen, und ein XMP, das weiterhin "Adobe RGB (1998)"
        ''' behauptet, brachte ein Programm, das diese Felder auswertet, erneut auf den falschen
        ''' Wert. Entfernt statt umgeschrieben: ein fehlendes Feld heisst schlicht "keine Angabe",
        ''' und ohne Profil gilt sRGB. Ein Wert waere eine zweite Wahrheit, die gepflegt werden
        ''' muesste.
        '''
        ''' Rueckgabe: der neue Block, oder Nothing, wenn nichts zu tun war. Der Aufrufer erkennt
        ''' daran, ob er den umgebenden Block neu bauen muss - die Laenge aendert sich ja.
        '''
        ''' GRENZE: ein KOMPRIMIERT abgelegtes XMP (PNG kann das) bleibt unangetastet. Die Felder
        ''' stehen dort nicht im Klartext, also findet der Ausdruck nichts und die Datei geht
        ''' unveraendert weiter - schlechter als vorher ist sie damit nicht.</summary>
        Private Shared Function StripXmpColorFields(xmp As Byte()) As Byte()
            If xmp Is Nothing OrElse xmp.Length = 0 Then Return Nothing
            Try
                ' NICHT "text" nennen: der Name verdeckt den Namensraum Text, und die Zeile
                ' darunter kaeme dann nicht mehr an Text.Encoding heran.
                Dim xmpText = Text.Encoding.UTF8.GetString(xmp)
                If xmpText.IndexOf("ICCProfile", StringComparison.OrdinalIgnoreCase) < 0 AndAlso
                   xmpText.IndexOf("ColorSpace", StringComparison.OrdinalIgnoreCase) < 0 Then Return Nothing

                Dim cleaned = xmpText
                For Each field In {"photoshop:ICCProfile", "exif:ColorSpace"}
                    ' Als Attribut, in beiden Anfuehrungsarten, und als eigenes Element.
                    cleaned = Regex.Replace(cleaned, "\s*" & Regex.Escape(field) & "\s*=\s*""[^""]*""", "")
                    cleaned = Regex.Replace(cleaned, "\s*" & Regex.Escape(field) & "\s*=\s*'[^']*'", "")
                    cleaned = Regex.Replace(cleaned, "\s*<" & Regex.Escape(field) & "(\s[^>]*)?/>", "")
                    cleaned = Regex.Replace(cleaned, "\s*<" & Regex.Escape(field) & "(\s[^>]*)?>.*?</" & Regex.Escape(field) & ">", "",
                                            RegexOptions.Singleline)
                Next

                If String.Equals(cleaned, xmpText, StringComparison.Ordinal) Then Return Nothing
                Return Text.Encoding.UTF8.GetBytes(cleaned)
            Catch
                ' Ein unerwarteter Aufbau darf den Export nicht kosten.
                Return Nothing
            End Try
        End Function

        ''' <summary>Ein PNG-Textblock ohne die XMP-Farbfelder. Traegt er kein XMP oder liegt es
        ''' komprimiert vor, kommt er UNVERAENDERT zurueck.
        '''
        ''' Der Kopf des Blocks (Schluesselwort, die beiden Kompressionsbytes, Sprache und
        ''' uebersetztes Schluesselwort) bleibt Byte fuer Byte stehen; ersetzt wird allein der Text
        ''' dahinter.</summary>
        Private Shared Function WithoutXmpColorFieldsInPngText(chunk As Byte(), length As Integer) As Byte()
            Try
                Dim data(length - 1) As Byte
                Buffer.BlockCopy(chunk, 8, data, 0, length)

                Dim textOffset = XmpTextOffsetInPngItxt(data)
                If textOffset <= 0 Then Return chunk

                Dim xmp(data.Length - textOffset - 1) As Byte
                Buffer.BlockCopy(data, textOffset, xmp, 0, xmp.Length)

                Dim cleaned = StripXmpColorFields(xmp)
                If cleaned Is Nothing Then Return chunk

                Dim header(textOffset - 1) As Byte
                Buffer.BlockCopy(data, 0, header, 0, textOffset)
                Return CreatePngChunk("iTXt", CombineBytes(header, cleaned))
            Catch
                Return chunk
            End Try
        End Function

        ''' <summary>Wo im Inhalt eines iTXt-Blocks der eigentliche Text beginnt, wenn es sich um
        ''' UNKOMPRIMIERTES XMP handelt. 0 heisst: Finger weg von diesem Block.
        '''
        ''' Der Aufbau ist Schluesselwort, Null, Kompressionskennzeichen, Kompressionsverfahren,
        ''' Sprachangabe, Null, uebersetztes Schluesselwort, Null, Text. Die beiden mittleren Felder
        ''' DUERFEN leer sein, muessen es aber nicht - ihre Nullzeichen werden deshalb gesucht und
        ''' nicht gezaehlt. Ein fester Versatz stimmte nur fuer den leeren Fall und schnitt sonst
        ''' mitten in die Sprachangabe; beim Neubau des Blocks entstuende daraus eine kaputte
        ''' Datei.</summary>
        Private Shared Function XmpTextOffsetInPngItxt(data As Byte()) As Integer
            Dim keywordEnd = Array.IndexOf(data, CByte(0))
            If keywordEnd <= 0 OrElse keywordEnd + 2 >= data.Length Then Return 0
            If Text.Encoding.ASCII.GetString(data, 0, keywordEnd) <> "XML:com.adobe.xmp" Then Return 0
            ' Nur unkomprimiert: sonst stehen die Felder nicht im Klartext.
            If data(keywordEnd + 1) <> 0 Then Return 0

            Dim languageEnd = Array.IndexOf(data, CByte(0), keywordEnd + 3)
            If languageEnd < 0 Then Return 0
            Dim translatedEnd = Array.IndexOf(data, CByte(0), languageEnd + 1)
            If translatedEnd < 0 OrElse translatedEnd + 1 >= data.Length Then Return 0
            Return translatedEnd + 1
        End Function

        ''' <summary>Ein JPEG-Segment ohne die XMP-Farbfelder. Ist es keines oder war nichts zu
        ''' entfernen, kommt es UNVERAENDERT zurueck. Das Segment wird neu gebaut, weil sich seine
        ''' Laengenangabe mitaendert.</summary>
        Private Shared Function WithoutXmpColorFields(segment As Byte()) As Byte()
            Dim identifier = Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/" & ChrW(0))
            If segment.Length <= 4 + identifier.Length Then Return segment
            If segment(1) <> &HE1 OrElse Not StartsWithBytes(segment, 4, identifier) Then Return segment

            Dim xmpLength = segment.Length - 4 - identifier.Length
            Dim xmp(xmpLength - 1) As Byte
            Buffer.BlockCopy(segment, 4 + identifier.Length, xmp, 0, xmpLength)

            Dim cleaned = StripXmpColorFields(xmp)
            If cleaned Is Nothing Then Return segment
            Return CreateJpegAppSegment(&HE1, CombineBytes(identifier, cleaned))
        End Function

        ''' <summary>Position eines Eintrags in einem Verzeichnis, oder 0.</summary>
        Private Shared Function FindIfdEntry(buffer As Byte(), ifd As Integer, tag As Integer, littleEndian As Boolean) As Integer
            If ifd <= 0 OrElse ifd + 2 > buffer.Length Then Return 0
            Dim count = ReadUInt16Endian(buffer, ifd, littleEndian)
            For i = 0 To count - 1
                Dim entry = ifd + 2 + i * 12
                If entry + 12 > buffer.Length Then Return 0
                If ReadUInt16Endian(buffer, entry, littleEndian) = tag Then Return entry
            Next
            Return 0
        End Function

        ''' <summary>Folgt einem Verweis auf ein Unterverzeichnis und liefert dessen Position im
        ''' Puffer, oder 0. Der Wert im Eintrag zaehlt ab dem TIFF-Kopf, nicht ab dem Puffer.</summary>
        Private Shared Function FindIfdPointer(buffer As Byte(), tiffStart As Integer, ifd As Integer,
                                               tag As Integer, littleEndian As Boolean) As Integer
            Dim entry = FindIfdEntry(buffer, ifd, tag, littleEndian)
            If entry <= 0 Then Return 0
            If ReadUInt16Endian(buffer, entry + 2, littleEndian) <> 4 Then Return 0
            Dim target = tiffStart + CInt(ReadUInt32Endian(buffer, entry + 8, littleEndian))
            If target <= tiffStart OrElse target + 2 > buffer.Length Then Return 0
            Return target
        End Function

        Private Shared Sub CopyPngMetadata(sourcePath As String, targetPath As String)
            Dim metadataChunks = If(IO.Path.GetExtension(sourcePath).ToLowerInvariant() = ".png",
                                    ReadPngMetadataChunks(sourcePath),
                                    BuildPngMetadataChunksFromSource(sourcePath))
            If metadataChunks.Count = 0 Then Return

            Dim targetBytes = File.ReadAllBytes(targetPath)
            If Not IsPngBytes(targetBytes) Then Return

            Dim output As New List(Of Byte)(targetBytes.Length + metadataChunks.Sum(Function(c) c.Length))
            output.AddRange(targetBytes.Take(8))

            Dim inserted = False
            Dim offset = 8
            While offset + 12 <= targetBytes.Length
                Dim length = ReadInt32BE(targetBytes, offset)
                Dim chunkEnd = offset + 12 + length
                If length < 0 OrElse chunkEnd > targetBytes.Length Then Return
                Dim chunkType = Text.Encoding.ASCII.GetString(targetBytes, offset + 4, 4)

                If Not inserted AndAlso chunkType = "IDAT" Then
                    For Each chunk In metadataChunks
                        output.AddRange(chunk)
                    Next
                    inserted = True
                End If

                If Not IsPngMetadataChunk(chunkType) Then
                    output.AddRange(targetBytes.Skip(offset).Take(12 + length))
                End If

                offset = chunkEnd
            End While

            WriteAllBytesAtomic(targetPath, output.ToArray())
        End Sub

        Private Shared Function ReadPngMetadataChunks(path As String) As List(Of Byte())
            Dim bytes = File.ReadAllBytes(path)
            Dim result As New List(Of Byte())()
            If Not IsPngBytes(bytes) Then Return result

            Dim offset = 8
            While offset + 12 <= bytes.Length
                Dim length = ReadInt32BE(bytes, offset)
                Dim chunkEnd = offset + 12 + length
                If length < 0 OrElse chunkEnd > bytes.Length Then Exit While
                Dim chunkType = Text.Encoding.ASCII.GetString(bytes, offset + 4, 4)

                If IsPngMetadataChunk(chunkType) Then
                    Dim chunk(12 + length - 1) As Byte
                    Buffer.BlockCopy(bytes, offset, chunk, 0, chunk.Length)
                    If chunkType = "eXIf" AndAlso length > 0 Then
                        ' Farbraum-Angabe auf sRGB stellen, siehe PatchExifColorSpaceToSrgb. Der
                        ' Block wird dafuer NEU gebaut und nicht an Ort und Stelle geaendert: ein
                        ' PNG-Block traegt eine Pruefsumme ueber seinen Inhalt, und die waere danach
                        ' falsch. CreatePngChunk rechnet sie mit.
                        Dim data(length - 1) As Byte
                        Buffer.BlockCopy(chunk, 8, data, 0, length)
                        ' Trug die Quelle den JPEG-Vorspann "Exif\0\0" im Block, faellt er hier
                        ' weg: in ein PNG gehoeren die nackten TIFF-Bytes. Der Block wird ohnehin
                        ' neu gebaut, die geaenderte Laenge kostet also nichts.
                        data = WithoutExifPreamble(data)
                        PatchExifColorSpaceToSrgb(data, 0)
                        chunk = CreatePngChunk("eXIf", data)
                    ElseIf chunkType = "iTXt" AndAlso length > 0 Then
                        chunk = WithoutXmpColorFieldsInPngText(chunk, length)
                    End If
                    result.Add(chunk)
                End If

                offset = chunkEnd
            End While

            Return result
        End Function

        Private Shared Function BuildPngMetadataChunksFromSource(sourcePath As String) As List(Of Byte())
            Dim result As New List(Of Byte())()
            Dim exif = ExtractExifTiffBytes(sourcePath)
            If exif IsNot Nothing AndAlso exif.Length > 0 Then result.Add(CreatePngChunk("eXIf", exif))

            Dim xmp = ExtractXmpBytes(sourcePath)
            If xmp IsNot Nothing AndAlso xmp.Length > 0 Then
                Dim keyword = Text.Encoding.ASCII.GetBytes("XML:com.adobe.xmp")
                Dim payload As New List(Of Byte)()
                payload.AddRange(keyword)
                payload.Add(0)
                payload.Add(0)
                payload.Add(0)
                payload.Add(0)
                payload.Add(0)
                payload.AddRange(xmp)
                result.Add(CreatePngChunk("iTXt", payload.ToArray()))
            End If

            Return result
        End Function

        Private Shared Function IsPngBytes(bytes As Byte()) As Boolean
            Return bytes.Length >= 8 AndAlso bytes(0) = &H89 AndAlso bytes(1) = &H50 AndAlso bytes(2) = &H4E AndAlso bytes(3) = &H47 AndAlso
                   bytes(4) = &HD AndAlso bytes(5) = &HA AndAlso bytes(6) = &H1A AndAlso bytes(7) = &HA
        End Function

        ''' <summary>Welche PNG-Bloecke aus der Quelle in die Zieldatei uebernommen werden.
        '''
        ''' OHNE "iCCP": das Farbprofil der Quelle passt nach dem Farbmanagement nicht mehr zu den
        ''' geschriebenen Bildpunkten, die immer sRGB sind. Dieselbe Begruendung wie bei
        ''' IsJpegIccSegment.</summary>
        Private Shared Function IsPngMetadataChunk(chunkType As String) As Boolean
            Select Case chunkType
                Case "eXIf", "iTXt", "tEXt", "zTXt"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        Private Shared Sub CopyWebpMetadata(sourcePath As String, targetPath As String)
            ' "ICCP" fehlt hier bewusst: das Farbprofil der Quelle gehoert nach dem Farbmanagement
            ' nicht mehr zu den geschriebenen Bildpunkten, siehe IsJpegIccSegment.
            Dim sourceChunks = If(IO.Path.GetExtension(sourcePath).ToLowerInvariant() = ".webp",
                                  ReadWebpChunks(File.ReadAllBytes(sourcePath)).
                                      Where(Function(c) c.Type = "EXIF" OrElse c.Type = "XMP ").
                                      ToList(),
                                  BuildWebpMetadataChunksFromSource(sourcePath))

            ' Farbraum-Angabe auf sRGB stellen (siehe PatchExifColorSpaceToSrgb). Ein WebP-Block
            ' traegt keine Pruefsumme, er laesst sich also an Ort und Stelle aendern; die Laenge
            ' steht am Block selbst und wird beim Schreiben aus den Daten genommen. Deshalb darf
            ' hier auch der JPEG-Vorspann "Exif\0\0" wegfallen, den die Quelle mitgebracht haben
            ' kann - in den Block gehoeren die nackten TIFF-Bytes.
            For Each chunk In sourceChunks.Where(Function(c) c.Type = "EXIF")
                chunk.Data = WithoutExifPreamble(chunk.Data)
                PatchExifColorSpaceToSrgb(chunk.Data, 0)
            Next
            For Each chunk In sourceChunks.Where(Function(c) c.Type = "XMP ")
                Dim cleaned = StripXmpColorFields(chunk.Data)
                If cleaned IsNot Nothing Then chunk.Data = cleaned
            Next
            If sourceChunks.Count = 0 Then Return

            Dim targetBytes = File.ReadAllBytes(targetPath)
            Dim targetChunks = ReadWebpChunks(targetBytes)
            If targetChunks.Count = 0 Then Return

            Dim vp8x = targetChunks.FirstOrDefault(Function(c) c.Type = "VP8X")
            Dim imageChunk = targetChunks.FirstOrDefault(Function(c) c.Type = "VP8 " OrElse c.Type = "VP8L")
            If vp8x Is Nothing AndAlso imageChunk Is Nothing Then Return

            Dim flags As Byte = 0
            Dim width As Integer = 0
            Dim height As Integer = 0
            If vp8x IsNot Nothing AndAlso vp8x.Data.Length >= 10 Then
                flags = vp8x.Data(0)
                width = 1 + CInt(vp8x.Data(4)) + (CInt(vp8x.Data(5)) << 8) + (CInt(vp8x.Data(6)) << 16)
                height = 1 + CInt(vp8x.Data(7)) + (CInt(vp8x.Data(8)) << 8) + (CInt(vp8x.Data(9)) << 16)
            ElseIf imageChunk IsNot Nothing Then
                Dim size = ReadWebpImageSize(imageChunk)
                width = size.Width
                height = size.Height
                If targetChunks.Any(Function(c) c.Type = "ALPH") Then flags = CByte(flags Or &H10)
            End If
            If width <= 0 OrElse height <= 0 Then Return

            ' Das Profil-Flag wird GELOESCHT, weil unten kein ICCP-Block mehr geschrieben wird -
            ' weder aus der Quelle noch aus dem Ziel. Ein gesetztes Flag ohne zugehoerigen Block
            ' waere eine kaputte Datei: die Kopfzeile verspricht etwas, das nicht da ist. Das Flag
            ' kommt aus dem ZIEL und kann dort gesetzt sein, wenn Skia beim Kodieren eines
            ' angehaengt hat.
            flags = CByte(flags And &HDF)
            If sourceChunks.Any(Function(c) c.Type = "EXIF") Then flags = CByte(flags Or &H8)
            If sourceChunks.Any(Function(c) c.Type = "XMP ") Then flags = CByte(flags Or &H4)

            Dim outputChunks As New List(Of WebpChunk)()
            outputChunks.Add(CreateWebpVp8xChunk(flags, width, height))
            For Each chunk In targetChunks
                If chunk.Type = "VP8X" OrElse chunk.Type = "EXIF" OrElse chunk.Type = "XMP " OrElse chunk.Type = "ICCP" Then Continue For
                outputChunks.Add(chunk)
            Next
            For Each chunk In sourceChunks.Where(Function(c) c.Type = "EXIF" OrElse c.Type = "XMP ")
                outputChunks.Add(chunk)
            Next

            WriteWebpChunks(targetPath, outputChunks)
        End Sub

        Private Class WebpChunk
            Public Property Type As String = ""
            Public Property Data As Byte() = Array.Empty(Of Byte)()
        End Class

        Private Shared Function ReadWebpChunks(bytes As Byte()) As List(Of WebpChunk)
            Dim result As New List(Of WebpChunk)()
            If bytes.Length < 12 OrElse Text.Encoding.ASCII.GetString(bytes, 0, 4) <> "RIFF" OrElse Text.Encoding.ASCII.GetString(bytes, 8, 4) <> "WEBP" Then Return result

            Dim offset = 12
            While offset + 8 <= bytes.Length
                Dim chunkType = Text.Encoding.ASCII.GetString(bytes, offset, 4)
                Dim length = ReadUInt32LE(bytes, offset + 4)
                If length > Integer.MaxValue OrElse offset + 8L + length > bytes.Length Then Exit While

                Dim data As Byte() = Array.Empty(Of Byte)()
                If length > 0 Then data = New Byte(CInt(length) - 1) {}
                If length > 0 Then Buffer.BlockCopy(bytes, offset + 8, data, 0, CInt(length))
                result.Add(New WebpChunk With {.Type = chunkType, .Data = data})

                offset += 8 + CInt(length)
                If (length Mod 2UI) = 1UI Then offset += 1
            End While

            Return result
        End Function

        Private Shared Function ReadWebpImageSize(chunk As WebpChunk) As (Width As Integer, Height As Integer)
            ' Der VP8-Keyframe-Header (verlustbehaftet) speichert die tatsächliche Breite/Höhe in je 14 Bit.
            ' VP8L (verlustfrei) speichert dagegen width-1/height-1 - deshalb nur dort das "1 +".
            ' Ein Off-by-one macht die VP8X-Canvas-Größe inkonsistent zum Bild und libwebp lehnt die Datei ab.
            If chunk.Type = "VP8 " AndAlso chunk.Data.Length >= 10 Then
                Return (Width:=CInt(chunk.Data(6)) Or ((CInt(chunk.Data(7)) And &H3F) << 8),
                        Height:=CInt(chunk.Data(8)) Or ((CInt(chunk.Data(9)) And &H3F) << 8))
            End If
            If chunk.Type = "VP8L" AndAlso chunk.Data.Length >= 5 Then
                Dim b1 = CInt(chunk.Data(1))
                Dim b2 = CInt(chunk.Data(2))
                Dim b3 = CInt(chunk.Data(3))
                Dim b4 = CInt(chunk.Data(4))
                Dim width = 1 + (((b2 And &H3F) << 8) Or b1)
                Dim height = 1 + (((b4 And &HF) << 10) Or (b3 << 2) Or ((b2 And &HC0) >> 6))
                Return (width, height)
            End If
            Return (0, 0)
        End Function

        Private Shared Function CreateWebpVp8xChunk(flags As Byte, width As Integer, height As Integer) As WebpChunk
            Dim data(9) As Byte
            data(0) = flags
            Dim storedWidth = Math.Max(0, width - 1)
            Dim storedHeight = Math.Max(0, height - 1)
            data(4) = CByte(storedWidth And &HFF)
            data(5) = CByte((storedWidth >> 8) And &HFF)
            data(6) = CByte((storedWidth >> 16) And &HFF)
            data(7) = CByte(storedHeight And &HFF)
            data(8) = CByte((storedHeight >> 8) And &HFF)
            data(9) = CByte((storedHeight >> 16) And &HFF)
            Return New WebpChunk With {.Type = "VP8X", .Data = data}
        End Function

        Private Shared Sub WriteWebpChunks(path As String, chunks As List(Of WebpChunk))
            Dim body As New List(Of Byte)()
            For Each chunk In chunks
                body.AddRange(Text.Encoding.ASCII.GetBytes(chunk.Type))
                body.AddRange(BitConverter.GetBytes(CUInt(chunk.Data.Length)))
                body.AddRange(chunk.Data)
                If (chunk.Data.Length Mod 2) = 1 Then body.Add(0)
            Next

            Dim bytes As New List(Of Byte)(12 + body.Count)
            bytes.AddRange(Text.Encoding.ASCII.GetBytes("RIFF"))
            bytes.AddRange(BitConverter.GetBytes(CUInt(4 + body.Count)))
            bytes.AddRange(Text.Encoding.ASCII.GetBytes("WEBP"))
            bytes.AddRange(body)
            File.WriteAllBytes(path, bytes.ToArray())
        End Sub

        Private Shared Function BuildWebpMetadataChunksFromSource(sourcePath As String) As List(Of WebpChunk)
            Dim result As New List(Of WebpChunk)()
            Dim exif = ExtractExifTiffBytes(sourcePath)
            If exif IsNot Nothing AndAlso exif.Length > 0 Then result.Add(New WebpChunk With {.Type = "EXIF", .Data = exif})

            Dim xmp = ExtractXmpBytes(sourcePath)
            If xmp IsNot Nothing AndAlso xmp.Length > 0 Then result.Add(New WebpChunk With {.Type = "XMP ", .Data = xmp})

            Return result
        End Function

        ''' <summary>Der TIFF-Block der Aufnahmedaten aus einer Quelldatei.
        '''
        ''' Die Farbraum-Angabe ist hier schon auf sRGB gestellt: das geschieht bei den LESERN der
        ''' Quelle (ReadJpegMetadataSegments und ReadPngMetadataChunks), damit jeder Weg sie
        ''' mitbekommt und nicht nur dieser. Allein WebP wird unten selbst nachgezogen - dort liest
        ''' diese Funktion die Bloecke direkt.</summary>
        Private Shared Function ExtractExifTiffBytes(path As String) As Byte()
            Dim ext = IO.Path.GetExtension(path).ToLowerInvariant()
            Try
                Select Case ext
                    Case ".jpg", ".jpeg"
                        For Each segment In ReadJpegMetadataSegments(path)
                            If IsExifSegment(segment) Then
                                Dim tiffLength = segment.Length - 10
                                If tiffLength <= 0 Then Return Nothing
                                Dim tiff(tiffLength - 1) As Byte
                                Buffer.BlockCopy(segment, 10, tiff, 0, tiffLength)
                                Return tiff
                            End If
                        Next
                    Case ".png"
                        For Each chunk In ReadPngMetadataChunks(path)
                            Dim chunkType = Text.Encoding.ASCII.GetString(chunk, 4, 4)
                            If chunkType = "eXIf" Then
                                Dim length = ReadInt32BE(chunk, 0)
                                Dim data(length - 1) As Byte
                                Buffer.BlockCopy(chunk, 8, data, 0, length)
                                ' Auch ein PNG-Block kann den JPEG-Vorspann tragen (siehe
                                ' ExifTiffStart). Hier gehoeren die nackten TIFF-Bytes heraus:
                                ' der JPEG-Export setzt "Exif\0\0" selbst davor.
                                Return WithoutExifPreamble(data)
                            End If
                        Next
                    Case ".webp"
                        Dim chunk = ReadWebpChunks(File.ReadAllBytes(path)).FirstOrDefault(Function(c) c.Type = "EXIF")
                        If chunk IsNot Nothing Then
                            ' Den JPEG-Vorspann "Exif\0\0" abziehen, den manche Erzeuger auch in den
                            ' WEBP-Chunk schreiben (siehe ExifTiffStart): der Aufrufer erwartet hier
                            ' die nackten TIFF-Bytes, nicht den Block mit Vorspann.
                            Dim tiff = WithoutExifPreamble(chunk.Data)
                            PatchExifColorSpaceToSrgb(tiff, 0)
                            Return tiff
                        End If
                End Select
            Catch
            End Try
            Return Nothing
        End Function

        Private Shared Function ExtractXmpBytes(path As String) As Byte()
            Dim ext = IO.Path.GetExtension(path).ToLowerInvariant()
            Try
                Select Case ext
                    Case ".jpg", ".jpeg"
                        For Each segment In ReadJpegMetadataSegments(path)
                            If segment.Length <= 33 OrElse segment(1) <> &HE1 OrElse IsExifSegment(segment) Then Continue For
                            Dim identifier = Text.Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/" & ChrW(0))
                            If StartsWithBytes(segment, 4, identifier) Then
                                Dim xmpLength = segment.Length - 4 - identifier.Length
                                Dim xmp(xmpLength - 1) As Byte
                                Buffer.BlockCopy(segment, 4 + identifier.Length, xmp, 0, xmpLength)
                                Return xmp
                            End If
                        Next
                    Case ".png"
                        For Each chunk In ReadPngMetadataChunks(path)
                            Dim chunkType = Text.Encoding.ASCII.GetString(chunk, 4, 4)
                            Dim length = ReadInt32BE(chunk, 0)
                            If chunkType <> "iTXt" OrElse length <= 0 Then Continue For
                            Dim data(length - 1) As Byte
                            Buffer.BlockCopy(chunk, 8, data, 0, length)
                            ' Anfang des Textes suchen statt zaehlen, siehe XmpTextOffsetInPngItxt:
                            ' ein fester Versatz stimmt nur, wenn Sprachangabe und uebersetztes
                            ' Schluesselwort beide leer sind.
                            Dim textOffset = XmpTextOffsetInPngItxt(data)
                            If textOffset <= 0 Then Continue For
                            Dim xmp(data.Length - textOffset - 1) As Byte
                            Buffer.BlockCopy(data, textOffset, xmp, 0, xmp.Length)
                            Return xmp
                        Next
                    Case ".webp"
                        ' Anders als bei JPEG und PNG kommen die Bloecke hier ungereinigt herein -
                        ' die beiden anderen Wege laufen ueber ihre Leser, die das schon erledigen.
                        Dim chunk = ReadWebpChunks(File.ReadAllBytes(path)).FirstOrDefault(Function(c) c.Type = "XMP ")
                        If chunk IsNot Nothing Then Return If(StripXmpColorFields(chunk.Data), chunk.Data)
                End Select
            Catch
            End Try
            Return Nothing
        End Function

        Private Shared Function CreatePngChunk(chunkType As String, data As Byte()) As Byte()
            Dim typeBytes = Text.Encoding.ASCII.GetBytes(chunkType)
            Dim chunk(12 + data.Length - 1) As Byte
            WriteInt32BE(chunk, 0, data.Length)
            Buffer.BlockCopy(typeBytes, 0, chunk, 4, 4)
            If data.Length > 0 Then Buffer.BlockCopy(data, 0, chunk, 8, data.Length)
            Dim crc = Crc32(chunk, 4, 4 + data.Length)
            WriteUInt32BE(chunk, 8 + data.Length, crc)
            Return chunk
        End Function

        Private Shared Function CombineBytes(first As Byte(), second As Byte()) As Byte()
            Dim result(first.Length + second.Length - 1) As Byte
            Buffer.BlockCopy(first, 0, result, 0, first.Length)
            Buffer.BlockCopy(second, 0, result, first.Length, second.Length)
            Return result
        End Function

        Private Shared Function StartsWithBytes(bytes As Byte(), offset As Integer, prefix As Byte()) As Boolean
            If bytes.Length < offset + prefix.Length Then Return False
            For i = 0 To prefix.Length - 1
                If bytes(offset + i) <> prefix(i) Then Return False
            Next
            Return True
        End Function

        ' ACHTUNG: In VB.NET liefert "byteWert << n" wieder einen Byte und maskiert die Schiebeweite
        ' mit 7 (Byte << 8 ist also ein No-Op). Jeder Byte-Operand MUSS vor dem Shift nach Integer
        ' geweitet werden - sonst liest z.B. ReadUInt16BE(&H01, &H2E) 47 statt 302.
        Private Shared Function ReadUInt16BE(bytes As Byte(), offset As Integer) As Integer
            Return (CInt(bytes(offset)) << 8) Or CInt(bytes(offset + 1))
        End Function

        Private Shared Function ReadInt32BE(bytes As Byte(), offset As Integer) As Integer
            Return (CInt(bytes(offset)) << 24) Or (CInt(bytes(offset + 1)) << 16) Or (CInt(bytes(offset + 2)) << 8) Or CInt(bytes(offset + 3))
        End Function

        Private Shared Function ReadUInt16Endian(bytes As Byte(), offset As Integer, littleEndian As Boolean) As Integer
            If littleEndian Then Return CInt(bytes(offset)) Or (CInt(bytes(offset + 1)) << 8)
            Return ReadUInt16BE(bytes, offset)
        End Function

        Private Shared Function ReadUInt32Endian(bytes As Byte(), offset As Integer, littleEndian As Boolean) As UInteger
            If littleEndian Then Return ReadUInt32LE(bytes, offset)
            Return CUInt(CLng(bytes(offset)) << 24) Or CUInt(CLng(bytes(offset + 1)) << 16) Or CUInt(CInt(bytes(offset + 2)) << 8) Or CUInt(bytes(offset + 3))
        End Function

        Private Shared Function ReadUInt32LE(bytes As Byte(), offset As Integer) As UInteger
            Return CUInt(bytes(offset)) Or CUInt(CInt(bytes(offset + 1)) << 8) Or CUInt(CLng(bytes(offset + 2)) << 16) Or CUInt(CLng(bytes(offset + 3)) << 24)
        End Function

        Private Shared Sub WriteInt32BE(bytes As Byte(), offset As Integer, value As Integer)
            bytes(offset) = CByte((value >> 24) And &HFF)
            bytes(offset + 1) = CByte((value >> 16) And &HFF)
            bytes(offset + 2) = CByte((value >> 8) And &HFF)
            bytes(offset + 3) = CByte(value And &HFF)
        End Sub

        Private Shared Sub WriteUInt32BE(bytes As Byte(), offset As Integer, value As UInteger)
            bytes(offset) = CByte((value >> 24) And &HFFUI)
            bytes(offset + 1) = CByte((value >> 16) And &HFFUI)
            bytes(offset + 2) = CByte((value >> 8) And &HFFUI)
            bytes(offset + 3) = CByte(value And &HFFUI)
        End Sub

        Private Shared Function Crc32(bytes As Byte(), offset As Integer, count As Integer) As UInteger
            Dim crc As UInteger = &HFFFFFFFFUI
            For i = offset To offset + count - 1
                crc = crc Xor bytes(i)
                For bit = 0 To 7
                    If (crc And 1UI) <> 0UI Then
                        crc = (crc >> 1) Xor &HEDB88320UI
                    Else
                        crc >>= 1
                    End If
                Next
            Next
            Return Not crc
        End Function

        Private Shared Sub WriteUInt16Endian(bytes As Byte(), offset As Integer, value As Integer, littleEndian As Boolean)
            If littleEndian Then
                bytes(offset) = CByte(value And &HFF)
                bytes(offset + 1) = CByte((value >> 8) And &HFF)
            Else
                bytes(offset) = CByte((value >> 8) And &HFF)
                bytes(offset + 1) = CByte(value And &HFF)
            End If
        End Sub

    End Class

End Namespace
