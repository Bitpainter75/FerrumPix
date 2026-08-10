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

                If adj?.Annotations IsNot Nothing Then
                    For Each annotation In adj.Annotations
                        If annotation Is Nothing Then Continue For

                        ' Die Bildpunkte OHNE Deckkraft, Mischmethode und Beschneidung rendern: die
                        ' drei trägt das PSD selbst. Würde der Renderer sie einbacken und das Format
                        ' sie noch einmal anwenden, käme alles doppelt heraus - eine halb
                        ' durchsichtige Ebene wäre plötzlich zu einem Viertel sichtbar.
                        ' Sichtbarkeit ebenso: eine ausgeblendete Ebene wird trotzdem gezeichnet und
                        ' reist als ausgeblendete Ebene mit, statt unterwegs verloren zu gehen.
                        Dim forRender = annotation.Clone()
                        forRender.Opacity = 100
                        forRender.BlendMode = "Normal"
                        forRender.ClipToLayerBelow = False
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

                        layers.Add(New PsdWriterService.PsdLayerInput With {
                            .Name = layerName,
                            .Pixels = layerBitmap,
                            .Left = layerLeft,
                            .Top = layerTop,
                            .OpacityPercent = annotation.Opacity,
                            .BlendMode = annotation.BlendMode,
                            .ClipToLayerBelow = annotation.ClipToLayerBelow,
                            .IsVisible = adj.IsAnnotationRenderVisible(annotation)
                        })
                    Next
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
                ' gehören dem Aufrufer und werden hier nur gelesen.
                For Each layer In layers
                    If layer.Pixels IsNot Nothing AndAlso Not Object.ReferenceEquals(layer.Pixels, background) Then
                        layer.Pixels.Dispose()
                    End If
                Next
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
                                         Optional applyPendingBaked As Boolean = False) As Boolean
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
                    Dim reapplied = ApplyPendingBakedOperations(decoded, adj)
                    If reapplied IsNot Nothing Then
                        decoded.Dispose()
                        decoded = reapplied
                    End If
                End If

                ' Hochskalieren mit Modell - HIER und nur hier, also im Speicherweg. Es steht VOR der
                ' Reglerkette: dann kann deren Groessenaenderung danach noch auf ein Zielmass
                ' verkleinern, und das ist die richtige Reihenfolge. Faellt es aus (Modell fehlt,
                ' Speicher reicht nicht), wird gespeichert wie ohne - eine Vergroesserung, die nicht
                ' geht, darf keine Datei kosten.
                If decoded IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(adj.UpscaleModel) Then
                    Dim hochskaliert = UpscaleModelService.Upscale(decoded, adj.UpscaleModel)
                    If hochskaliert IsNot Nothing Then
                        decoded.Dispose()
                        decoded = hochskaliert
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

        Private Shared Function ReadJpegMetadataSegments(path As String) As List(Of Byte())
            Dim bytes = File.ReadAllBytes(path)
            Dim result As New List(Of Byte())()
            If bytes.Length < 4 OrElse bytes(0) <> &HFF OrElse bytes(1) <> &HD8 Then Return result

            Dim offset = 2
            While offset + 4 <= bytes.Length
                If bytes(offset) <> &HFF Then Exit While
                Dim marker = bytes(offset + 1)
                If marker = &HDA OrElse marker = &HD9 Then Exit While
                If marker = &H1 OrElse (marker >= &HD0 AndAlso marker <= &HD7) Then
                    offset += 2
                    Continue While
                End If

                Dim length = ReadUInt16BE(bytes, offset + 2)
                If length < 2 OrElse offset + 2 + length > bytes.Length Then Exit While
                Dim totalLength = 2 + length

                If IsJpegMetadataMarker(marker) Then
                    Dim segment(totalLength - 1) As Byte
                    Buffer.BlockCopy(bytes, offset, segment, 0, totalLength)
                    If marker = &HE1 AndAlso IsExifSegment(segment) Then PatchExifOrientationToNormal(segment)
                    result.Add(segment)
                End If

                offset += totalLength
            End While

            Return result
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

        Private Shared Function IsPngMetadataChunk(chunkType As String) As Boolean
            Select Case chunkType
                Case "eXIf", "iTXt", "tEXt", "zTXt", "iCCP"
                    Return True
                Case Else
                    Return False
            End Select
        End Function

        Private Shared Sub CopyWebpMetadata(sourcePath As String, targetPath As String)
            Dim sourceChunks = If(IO.Path.GetExtension(sourcePath).ToLowerInvariant() = ".webp",
                                  ReadWebpChunks(File.ReadAllBytes(sourcePath)).
                                      Where(Function(c) c.Type = "EXIF" OrElse c.Type = "XMP " OrElse c.Type = "ICCP").
                                      ToList(),
                                  BuildWebpMetadataChunksFromSource(sourcePath))
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

            If sourceChunks.Any(Function(c) c.Type = "ICCP") Then flags = CByte(flags Or &H20)
            If sourceChunks.Any(Function(c) c.Type = "EXIF") Then flags = CByte(flags Or &H8)
            If sourceChunks.Any(Function(c) c.Type = "XMP ") Then flags = CByte(flags Or &H4)

            Dim outputChunks As New List(Of WebpChunk)()
            outputChunks.Add(CreateWebpVp8xChunk(flags, width, height))

            For Each chunk In sourceChunks.Where(Function(c) c.Type = "ICCP")
                outputChunks.Add(chunk)
            Next
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
                                Return data
                            End If
                        Next
                    Case ".webp"
                        Dim chunk = ReadWebpChunks(File.ReadAllBytes(path)).FirstOrDefault(Function(c) c.Type = "EXIF")
                        If chunk IsNot Nothing Then Return chunk.Data
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
                            Dim zero = Array.IndexOf(data, CByte(0))
                            If zero <= 0 Then Continue For
                            Dim keyword = Text.Encoding.ASCII.GetString(data, 0, zero)
                            If keyword <> "XML:com.adobe.xmp" OrElse zero + 5 >= data.Length Then Continue For
                            If data(zero + 1) <> 0 Then Continue For
                            Dim textOffset = zero + 5
                            Dim xmp(data.Length - textOffset - 1) As Byte
                            Buffer.BlockCopy(data, textOffset, xmp, 0, xmp.Length)
                            Return xmp
                        Next
                    Case ".webp"
                        Dim chunk = ReadWebpChunks(File.ReadAllBytes(path)).FirstOrDefault(Function(c) c.Type = "XMP ")
                        If chunk IsNot Nothing Then Return chunk.Data
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
