Imports System
Imports System.Collections.Generic
Imports System.IO
Imports System.Linq
Imports Avalonia.Media.Imaging
Imports SkiaSharp

Namespace Services

    Public Class CollageOptions
        ''' <summary>Vermerkte, aber noch nicht in den Pixeln steckende Vorgaenge mitrechnen?
        ''' Dieselbe Frage wie beim Drucken, mit derselben Vorgabe: nein, ausser der Nutzer sagt es.
        ''' Die Vorschau rechnet sie nie mit.</summary>
        Public Property ApplyPendingBakedOperations As Boolean = False
        Public Property OutputPath As String
        Public Property Width As Integer = 2400
        Public Property Columns As Integer = 3
        Public Property Gap As Integer = 24
        ''' Rand in Bildpunkten. Im Zufallsmodus auch negativ: dann duerfen die Abzuege so weit ueber
        ''' die Kante ragen; die anderen Layouts nehmen weniger als 0 als 0.
        Public Property Margin As Integer = 48
        Public Property BackgroundColor As String = "#FFFFFFFF"
        Public Property Format As String = "JPG"
        Public Property Quality As Integer = 90
        Public Property SquareCells As Boolean = True
        ''' "Grid" (Standard-Raster), "Hero" (ein großes Bild + Rest daneben), "Random" (Größe/Rotation zufällig).
        Public Property LayoutMode As String = "Grid"
        ''' Index des groß dargestellten Bilds im Hero-Layout (manuell in der Vorschau wählbar).
        Public Property HeroIndex As Integer = 0
        ''' Seite, auf der das Hero-Bild sitzt: "Left"/"Right"/"Top"/"Bottom" - die übrigen Bilder
        ''' füllen die gegenüberliegende Fläche als Streifen.
        Public Property HeroPosition As String = "Left"
        ''' Seed für den Zufallsmodus, damit "Neu mischen" reproduzierbar per Klick ein neues, aber
        ''' bei gleichbleibenden Optionen stabiles Ergebnis liefert (kein Re-Randomize bei jedem Preview-Tick).
        Public Property RandomSeed As Integer = 0
        ''' Nothing = Bilder behalten ihre ursprüngliche Auswahlreihenfolge (Standard, in allen
        ''' Layouts). Gesetzt (durch "Neu mischen") vertauscht die Reihenfolge, in der Bilder den
        ''' Positionen zugeordnet werden - in ALLEN Layouts, nicht nur im Zufallsmodus. Im
        ''' Hero-Layout bleibt das gewählte Hero-Bild davon unberührt, nur die Reihenfolge der
        ''' übrigen Bilder wird gemischt.
        Public Property OrderSeed As Integer? = Nothing
        ''' Wie die Hoehe entsteht: "Auto" aus Layout und Bildzahl (wie bisher), "Aspect" aus der
        ''' Breite und <see cref="AspectRatio"/>, "Custom" ist <see cref="Height"/>.
        Public Property SizeMode As String = "Auto"
        ''' Seitenverhaeltnis fuer "Aspect": "3:2", "4:3", "16:9", "5:4", "1:1" oder "A4" (Wurzel 2,
        ''' wie jedes A-Format). Lange durch kurze Seite; <see cref="Portrait"/> stellt es hoch.
        Public Property AspectRatio As String = "3:2"
        Public Property Portrait As Boolean = False
        ''' Hoehe in Bildpunkten fuer "Custom".
        Public Property Height As Integer = 1600
        ''' Zufallsmodus: Groesse der Abzuege in Prozent der Groesse, die die Flaeche gerade deckt.
        ''' Unter 100 liegen sie verstreut, und der Grund scheint durch.
        Public Property RandomPrintSize As Integer = 100
        ''' Zufallsmodus: wie verschieden gross die Abzuege sind, in Prozent; 100 heisst halb bis
        ''' anderthalbfach.
        Public Property RandomSizeVariation As Integer = 15

        ''' <summary>Die vorgegebene Hoehe zu <paramref name="width"/>, oder 0 bei "Auto": dann
        ''' bestimmt das Layout sie selbst.
        '''
        ''' KEINE MINDESTHOEHE HIER. Die Vorschau rechnet mit verkleinerten Massen; eine Untergrenze
        ''' von 320 hob dort eine flache Collage (12000 x 1600, Vorschau 1200 x 160) auf 320 an, und
        ''' Zuschnitt und Anordnung passten nicht mehr zur Ausgabe. Die Grenzen der Ausgabe haelt der
        ''' Dialog (Breite ab 640, Hoehe ab 320).</summary>
        Public Function FixedHeight(width As Integer) As Integer
            Select Case If(SizeMode, "").Trim().ToUpperInvariant()
                Case "ASPECT"
                    Dim ratio = AspectRatioValue(AspectRatio)
                    Return Math.Max(1, CInt(Math.Round(If(Portrait, width * ratio, width / ratio))))
                Case "CUSTOM"
                    Return Math.Max(1, Math.Min(12000, Height))
                Case Else
                    Return 0
            End Select
        End Function

        ''' <summary>Lange durch kurze Seite eines Seitenverhaeltnisses wie "16:9" oder "A4";
        ''' Unbekanntes rechnet als 3:2.</summary>
        Public Shared Function AspectRatioValue(text As String) As Double
            Dim t = If(text, "").Trim()
            If String.Equals(t, "A4", StringComparison.OrdinalIgnoreCase) Then Return Math.Sqrt(2.0)
            Dim parts = t.Split(":"c)
            Dim a, b As Double
            If parts.Length = 2 AndAlso
               Double.TryParse(parts(0), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, a) AndAlso
               Double.TryParse(parts(1), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, b) AndAlso
               a > 0 AndAlso b > 0 Then
                Return Math.Max(a, b) / Math.Min(a, b)
            End If
            Return 1.5
        End Function
    End Class

    ''' Position/Größe/Rotation eines einzelnen Bilds in der Collage - vom Layout-Algorithmus
    ''' (BuildGridSlots/BuildHeroSlots/BuildRandomSlots) berechnet, vom Render-Pass nur noch gezeichnet.
    Public Class CollageSlot
        Public Property SourceIndex As Integer
        Public Property X As Single
        Public Property Y As Single
        Public Property Width As Single
        Public Property Height As Single
        Public Property RotationDegrees As Single
        ''' True im Zufallsmodus: das Bild liegt als Abzug auf einer weissen Karte mit Schatten,
        ''' statt dass der Hintergrund als Rahmen durchscheint.
        Public Property IsPrint As Boolean
    End Class

    Public Class CollageService
        Public Shared Function SaveCollage(imagePaths As IEnumerable(Of String), options As CollageOptions) As Boolean
            If options Is Nothing OrElse String.IsNullOrWhiteSpace(options.OutputPath) Then Return False

            Using surfaceBitmap = RenderCollage(imagePaths, options)
                If surfaceBitmap Is Nothing Then Return False

                ' PDF ist kein Bildformat, das Skia hier kodieren könnte - es entsteht als
                ' einseitiges Dokument über denselben Weg wie „Speichern unter: PDF" und mit dem
                ' zuletzt im Druckdialog gewählten Seitenlayout, damit Collage und Druck gleich
                ' aussehen.
                If String.Equals(options.Format, "PDF", StringComparison.OrdinalIgnoreCase) Then
                    Return PrintService.WriteSinglePagePdf(surfaceBitmap, options.OutputPath,
                                                           AppSettingsService.Load().ToPrintOptions())
                End If

                ' .fpx: die fertige Collage wird zum Basisbild eines Projektbündels mit leerem
                ' Rezept - man kann sie danach im Editor nicht-destruktiv weiterentwickeln. Das
                ' Bündel braucht das Basisbild als DATEI, deshalb der Umweg über eine Temp-PNG.
                If String.Equals(options.Format, "FPX", StringComparison.OrdinalIgnoreCase) Then
                    If Not FpxService.Enabled Then Return False
                    Dim tempBase = Path.Combine(Path.GetTempPath(), $"ferrumpix-collage-{Guid.NewGuid():N}.png")
                    Try
                        Using basisPng = ImageProcessor.EncodePngStream(surfaceBitmap)
                            If basisPng Is Nothing Then Return False
                            Using fs = File.Create(tempBase)
                                basisPng.CopyTo(fs)
                            End Using
                        End Using
                        Using composite = ImageProcessor.EncodePngStream(surfaceBitmap, ImageProcessor.FpxCompositeMaxDimension)
                            If composite Is Nothing Then Return False
                            FpxService.Save(options.OutputPath, New ImageAdjustments(), tempBase, composite)
                        End Using
                        Return File.Exists(options.OutputPath)
                    Finally
                        Try
                            If File.Exists(tempBase) Then File.Delete(tempBase)
                        Catch
                        End Try
                    End Try
                End If

                ' JPEG XL kennt Skia nicht; es geht ueber libjxl, mit derselben Qualitaetszahl.
                If String.Equals(options.Format, "JXL", StringComparison.OrdinalIgnoreCase) Then
                    If Not JxlEncodeService.IsAvailable Then Return False
                    ImageProcessor.WriteFileAtomic(options.OutputPath,
                        Sub(fs) JxlEncodeService.Encode(surfaceBitmap, fs, Math.Max(1, Math.Min(100, options.Quality))))
                    Return True
                End If

                Dim fileFormat = If(String.Equals(options.Format, "PNG", StringComparison.OrdinalIgnoreCase),
                                SKEncodedImageFormat.Png,
                                If(String.Equals(options.Format, "WEBP", StringComparison.OrdinalIgnoreCase),
                                   SKEncodedImageFormat.Webp,
                                   SKEncodedImageFormat.Jpeg))
                Using image = SKImage.FromBitmap(surfaceBitmap)
                    Using data = If(fileFormat = SKEncodedImageFormat.Png,
                                    PngEncoder.Encode(surfaceBitmap, PngPurpose.Export),
                                    If(fileFormat = SKEncodedImageFormat.Jpeg,
                                       JpegEncoder.Encode(surfaceBitmap, options.Quality),
                                       image.Encode(fileFormat, Math.Max(1, Math.Min(100, options.Quality)))))
                        ' Atomar schreiben: eine bestehende Collage bleibt heil,
                        ' wenn das Schreiben abbricht.
                        ImageProcessor.WriteFileAtomic(options.OutputPath, Sub(fs) data.SaveTo(fs))
                    End Using
                End Using
            End Using

            Return True
        End Function

        ''' Rendert eine verkleinerte Vorschau (Layout, Zuschnitt und Hintergrund identisch zur
        ''' finalen Ausgabe) für die Live-Vorschau im Collage-Dialog, ohne die volle Zielbreite
        ''' zu berechnen. Breite/Abstand/Rand werden proportional auf maxDimension herunterskaliert.
        ''' <param name="cacheGeneration">PreviewImageCache.Generation, erfasst BEVOR der Render in
        ''' den Hintergrund geht. Schliesst der Dialog, waehrend der Render noch Dateien prueft oder
        ''' Bildgroessen liest, laese er sonst schon die neue Generation und fuellte den eben
        ''' geleerten Speicher wieder. Ohne Angabe die Generation beim Aufruf.</param>
        Public Shared Function RenderPreview(imagePaths As IEnumerable(Of String), options As CollageOptions, maxDimension As Integer,
                                             Optional cacheGeneration As Long? = Nothing) As Bitmap
            Dim generation = If(cacheGeneration, PreviewImageCache.Generation)
            If options Is Nothing Then Return Nothing

            ' Nach der laengeren Seite: bei vorgegebener Hoehe ist die bekannt, und eine hohe
            ' Collage (1600 x 12000) gaebe nach der Breite allein eine Vorschau von 1200 x 9000.
            Dim longest = Math.Max(options.Width, options.FixedHeight(options.Width))
            Dim scale = If(longest > 0, Math.Min(1.0, maxDimension / CDbl(longest)), 1.0)
            Dim previewOptions = New CollageOptions With {
                .Width = Math.Max(16, CInt(Math.Round(options.Width * scale))),
                .Columns = options.Columns,
                .Gap = CInt(Math.Round(options.Gap * scale)),
                .Margin = CInt(Math.Round(options.Margin * scale)),
                .BackgroundColor = options.BackgroundColor,
                .LayoutMode = options.LayoutMode,
                .HeroIndex = options.HeroIndex,
                .HeroPosition = options.HeroPosition,
                .RandomSeed = options.RandomSeed,
                .OrderSeed = options.OrderSeed,
                .SizeMode = options.SizeMode,
                .AspectRatio = options.AspectRatio,
                .Portrait = options.Portrait,
                .Height = Math.Max(1, CInt(Math.Round(options.Height * scale))),
                .RandomPrintSize = options.RandomPrintSize,
                .RandomSizeVariation = options.RandomSizeVariation
            }

            ' Vorschau ohne RAW-Entwicklung - siehe RenderCollage(developRaw).
            Using bitmap = RenderCollage(imagePaths, previewOptions, developRaw:=False, cacheGeneration:=generation)
                If bitmap Is Nothing Then Return Nothing
                Return ImageProcessor.ToAvaloniaBitmap(bitmap)
            End Using
        End Function

        ''' Zuletzt für die Vorschau berechnete Slots - dem Dialog-Code-Behind erlaubt das, einen
        ''' Klick auf die Vorschau auf den getroffenen Bildindex zurückzurechnen (manuelle Hero-Auswahl).
        Public Shared Property LastPreviewSlots As List(Of CollageSlot) = New List(Of CollageSlot)()

        ''' <param name="developRaw">False = RAW-Quellen über ihre eingebettete Vorschau lesen statt
        ''' sie zu entwickeln. Für die LIVE-Vorschau: die läuft nach jeder Regleränderung neu, und
        ''' der Entwicklungs-Cache hält nur EIN Bild - mit mehreren RAWs entwickelte er sonst bei
        ''' jedem Durchgang jedes Bild neu. Für die gespeicherte Collage wird voll entwickelt.</param>
        Private Shared Function RenderCollage(imagePaths As IEnumerable(Of String), options As CollageOptions,
                                              Optional developRaw As Boolean = True,
                                              Optional cacheGeneration As Long = 0) As SKBitmap
            If imagePaths Is Nothing OrElse options Is Nothing Then Return Nothing

            Dim paths = imagePaths.
                Where(Function(p) Not String.IsNullOrWhiteSpace(p) AndAlso File.Exists(p)).
                ToList()
            If paths.Count = 0 Then Return Nothing

            ' Keine hoehere Untergrenze: die verkleinerte Vorschau einer hohen Collage ist schmal, und
            ' eine angehobene Breite verschoebe ihr Seitenverhaeltnis. Die Ausgabe haelt der Dialog
            ' bei mindestens 640.
            Dim width = Math.Max(16, options.Width)
            Dim gap = Math.Max(0, options.Gap)
            Dim margin = Math.Max(0, options.Margin)

            ' Der Zufallsmodus legt Abzuege im Seitenverhaeltnis des Bildes; die Masse kommen aus
            ' dem Dateikopf, ohne das Bild zu dekodieren.
            Dim aspects As List(Of Double) = Nothing
            If String.Equals(If(options.LayoutMode, "").Trim(), "Random", StringComparison.OrdinalIgnoreCase) Then
                aspects = paths.Select(Function(p)
                                           Dim size = ImageProcessor.GetOrientedImageSize(p)
                                           Return If(size.Width > 0 AndAlso size.Height > 0, size.Width / CDbl(size.Height), 1.5)
                                       End Function).ToList()
            End If
            Dim layout = BuildSlots(paths.Count, width, gap, margin, options, aspects)
            LastPreviewSlots = layout.Slots
            Dim bg = ParseColor(options.BackgroundColor, SKColors.White)

            Dim surfaceBitmap = New SKBitmap(width, layout.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Using canvas = New SKCanvas(surfaceBitmap)
                canvas.Clear(bg)
                ' SKFilterQuality ist abgekündigt; SamplingHigh entspricht der alten Stufe "High" (kubisch/Mitchell).
                Dim samplingHigh = New SKSamplingOptions(SKCubicResampler.Mitchell)
                Using paint = New SKPaint With {.IsAntialias = True}
                    ' "Gap" wird hier als weißer (Hintergrundfarbe) Rahmen um JEDES einzelne Bild
                    ' interpretiert, nicht als Zwischenraum zwischen Zellen - die Zellen selbst liegen
                    ' kantenweise aneinander (siehe BuildGridSlots/BuildHeroSlots), das Bild wird um
                    ' "gap" Pixel nach innen versetzt gezeichnet, sodass die Hintergrundfarbe rundum
                    ' als gleichmäßiger Rahmen durchscheint (auch bei Rotation im Zufallsmodus).
                    Dim border = Math.Min(gap, CInt(Math.Min(width, layout.Height) * 0.2))
                    For Each slot In layout.Slots
                        If slot.SourceIndex < 0 OrElse slot.SourceIndex >= paths.Count Then Continue For
                        Dim path = paths(slot.SourceIndex)
                        ' NICHT SKBitmap.Decode: das kennt weder RAW noch .fpx (und PSD/HEIC/TIFF
                        ' ebenso wenig) - solche Quellen fielen stillschweigend aus der Collage,
                        ' ihre Kachel blieb leer. DecodeForOutput ist der Funnel, der alle Formate
                        ' UND die EXIF-Orientierung behandelt; ein zweiter Orientierungsschritt
                        ' danach würde das Bild ein zweites Mal drehen.
                        ' Die Vorschau (ohne RAW-Entwicklung) nimmt die verkleinerte Fassung aus
                        ' dem Zwischenspeicher, statt bei jedem Reglerschritt jede Datei zu lesen.
                        Dim applyPending = options.ApplyPendingBakedOperations
                        Using source = If(developRaw,
                                          ImageProcessor.DecodeDevelopedForOutput(path, True, applyPending),
                                          PreviewImageCache.GetCopy(path, $"raw=False;pending={applyPending}", cacheGeneration,
                                              Function() ImageProcessor.DecodeDevelopedForOutput(path, False, applyPending)))
                            If source Is Nothing Then
                                DiagnosticLogService.LogAlways("Collage", $"Nicht dekodierbar, Kachel bleibt leer: {path}")
                                Continue For
                            End If
                            Dim centerX = slot.X + slot.Width / 2.0F
                            Dim centerY = slot.Y + slot.Height / 2.0F
                            canvas.Save()
                            If slot.RotationDegrees <> 0 Then
                                canvas.RotateDegrees(slot.RotationDegrees, centerX, centerY)
                            End If
                            Dim slotBorder = CSng(border)
                            If slot.IsPrint Then slotBorder = DrawPrintCard(canvas, slot, gap)
                            Dim innerWidth = Math.Max(1.0F, slot.Width - slotBorder * 2)
                            Dim innerHeight = Math.Max(1.0F, slot.Height - slotBorder * 2)
                            Dim insetX = slot.X + (slot.Width - innerWidth) / 2.0F
                            Dim insetY = slot.Y + (slot.Height - innerHeight) / 2.0F
                            Dim dst = New SKRect(insetX, insetY, insetX + innerWidth, insetY + innerHeight)
                            Dim src = GetUniformToFillSourceRect(source.Width, source.Height, CInt(innerWidth), CInt(innerHeight))
                            ' Der Ausschnitt wird vorher auf die Kachelgroesse verkleinert
                            ' (ImageResampler): Skias fester kubischer Filter faltet bei einem
                            ' Foto in einer kleinen Kachel feines Muster in grobe Streifen.
                            Dim reduced = ReduceToTile(source, src, CInt(Math.Ceiling(innerWidth)), CInt(Math.Ceiling(innerHeight)))
                            Using image = SKImage.FromBitmap(If(reduced, source))
                                If reduced IsNot Nothing Then
                                    canvas.DrawImage(image, New SKRect(0, 0, reduced.Width, reduced.Height), dst, samplingHigh, paint)
                                Else
                                    canvas.DrawImage(image, src, dst, samplingHigh, paint)
                                End If
                            End Using
                            reduced?.Dispose()
                            canvas.Restore()
                        End Using
                    Next
                End Using
            End Using

            Return surfaceBitmap
        End Function

        ''' <summary>Die weisse Karte eines Abzugs im Zufallsmodus: weicher Schatten nach unten rechts,
        ''' eine feine Kante, damit sie sich auch auf weissem Grund und auf einem hellen Nachbarn
        ''' abhebt. Gezeichnet im schon gedrehten Koordinatensystem. Liefert die Breite des weissen
        ''' Rands: der Abstand-Regler bestimmt sie, gehalten zwischen 2,5 und 6 Prozent der kurzen
        ''' Seite, damit ein Abzug auch bei vielen kleinen Bildern wie einer aussieht; 0 heisst ohne
        ''' Rand.</summary>
        Private Shared Function DrawPrintCard(canvas As SKCanvas, slot As CollageSlot, gap As Integer) As Single
            Dim shortSide = Math.Min(slot.Width, slot.Height)
            Dim card = New SKRect(slot.X, slot.Y, slot.X + slot.Width, slot.Y + slot.Height)
            Dim blur = Math.Max(1.0F, shortSide * 0.015F)
            Using shadow = SKImageFilter.CreateDropShadow(shortSide * 0.006F, shortSide * 0.012F, blur, blur, New SKColor(0, 0, 0, 150))
                Using fill = New SKPaint With {.IsAntialias = True, .Color = SKColors.White, .ImageFilter = shadow}
                    canvas.DrawRect(card, fill)
                End Using
            End Using
            Using edge = New SKPaint With {.IsAntialias = True, .Style = SKPaintStyle.Stroke,
                                           .Color = New SKColor(0, 0, 0, 28), .StrokeWidth = Math.Max(1.0F, shortSide * 0.002F)}
                canvas.DrawRect(card, edge)
            End Using
            If gap <= 0 Then Return 0.0F
            Return Math.Max(shortSide * 0.025F, Math.Min(shortSide * 0.06F, gap))
        End Function

        Private Shared Function BuildSlots(count As Integer, width As Integer, gap As Integer, margin As Integer, options As CollageOptions,
                                           Optional aspects As IList(Of Double) = Nothing) As (Slots As List(Of CollageSlot), Height As Integer)
            Dim fixedHeight = options.FixedHeight(width)
            ' DER RAND BLEIBT UNTER 40 PROZENT der kuerzeren Seite (bei fester Hoehe auch dieser).
            ' Der Dialog erlaubt 800 bei 320 Hoehe: Raster und Hero legten die Zellen dann ab dem
            ' Rand an, also ganz ausserhalb, und die Collage blieb leer.
            margin = Math.Min(margin, MaxMargin(width, fixedHeight))
            Select Case If(options.LayoutMode, "Grid").Trim().ToUpperInvariant()
                Case "HERO"
                    Return FitToHeight(BuildHeroSlots(count, width, gap, margin, options), fixedHeight, margin)
                Case "RANDOM"
                    Return BuildRandomSlots(count, width, gap, margin, options, aspects, fixedHeight)
                Case Else
                    Return FitToHeight(BuildGridSlots(count, width, gap, margin, options), fixedHeight, margin)
            End Select
        End Function

        ''' <summary>Groesster wirksamer Rand: 40 Prozent der kuerzeren Seite, bei Hoehe 0 (aus dem
        ''' Layout) nur der Breite.</summary>
        Private Shared Function MaxMargin(width As Integer, fixedHeight As Integer) As Integer
            Dim shortest = If(fixedHeight > 0, Math.Min(width, fixedHeight), width)
            Return Math.Max(0, CInt(Math.Floor(shortest * 0.4)))
        End Function

        ''' <summary>Ein Layout auf eine vorgegebene Hoehe bringen: alles innerhalb des Rands wird
        ''' senkrecht gestreckt oder gestaucht. Raster und Hero bestehen aus geraden Zellen, die das
        ''' Bild auf sich zuschneiden; verzerrt wird dabei nichts, die Zellen werden nur hoeher oder
        ''' flacher. 0 laesst das Layout, wie es ist.</summary>
        Private Shared Function FitToHeight(layout As (Slots As List(Of CollageSlot), Height As Integer),
                                            fixedHeight As Integer, margin As Integer) As (Slots As List(Of CollageSlot), Height As Integer)
            If fixedHeight <= 0 OrElse fixedHeight = layout.Height Then Return layout
            Dim inner = layout.Height - margin * 2
            Dim target = Math.Max(1, fixedHeight - margin * 2)
            If inner <= 0 Then Return (layout.Slots, fixedHeight)
            Dim scale = CSng(target / CDbl(inner))
            For Each slot In layout.Slots
                slot.Y = margin + (slot.Y - margin) * scale
                slot.Height *= scale
            Next
            Return (layout.Slots, fixedHeight)
        End Function

        ''' Fisher-Yates-Shuffle mit festem Seed - liefert bei gleichem Seed/gleicher Anzahl immer
        ''' dieselbe Reihenfolge (reproduzierbar für die Vorschau), bei neuem Seed ("Neu mischen")
        ''' eine andere.
        Private Shared Function ShuffledIndices(count As Integer, seed As Integer) As List(Of Integer)
            Dim indices = Enumerable.Range(0, count).ToList()
            Dim rng = New Random(seed)
            For i = indices.Count - 1 To 1 Step -1
                Dim j = rng.Next(i + 1)
                Dim temp = indices(i)
                indices(i) = indices(j)
                indices(j) = temp
            Next
            Return indices
        End Function

        Private Shared Function BuildGridSlots(count As Integer, width As Integer, gap As Integer, margin As Integer, options As CollageOptions) As (Slots As List(Of CollageSlot), Height As Integer)
            Dim columns = Math.Max(1, Math.Min(12, options.Columns))
            Dim cellWidth = Math.Max(1.0F, CSng(Math.Floor((width - margin * 2) / CDbl(columns))))
            Dim cellHeight = cellWidth
            Dim rows = CInt(Math.Ceiling(count / CDbl(columns)))
            Dim height = margin * 2 + CInt(rows * cellHeight)
            Dim order = If(options.OrderSeed.HasValue, ShuffledIndices(count, options.OrderSeed.Value), Enumerable.Range(0, count).ToList())

            Dim slots As New List(Of CollageSlot)()
            For index = 0 To count - 1
                Dim col = index Mod columns
                Dim row = index \ columns
                slots.Add(New CollageSlot With {
                    .SourceIndex = order(index),
                    .X = margin + col * cellWidth,
                    .Y = margin + row * cellHeight,
                    .Width = cellWidth,
                    .Height = cellHeight,
                    .RotationDegrees = 0
                })
            Next
            Return (slots, height)
        End Function

        ''' Ein Bild (HeroIndex) groß (60% der nutzbaren Fläche, quadratisch) an der über
        ''' HeroPosition gewählten Seite, die übrigen Bilder als Streifen auf der gegenüberliegenden
        ''' Seite, gleichmäßig verteilt.
        Private Shared Function BuildHeroSlots(count As Integer, width As Integer, gap As Integer, margin As Integer, options As CollageOptions) As (Slots As List(Of CollageSlot), Height As Integer)
            If count <= 1 Then Return BuildGridSlots(count, width, gap, margin, options)

            Dim heroIndex = Math.Max(0, Math.Min(count - 1, options.HeroIndex))
            Dim otherIndices = Enumerable.Range(0, count).Where(Function(i) i <> heroIndex).ToList()
            ' Das Hero-Bild selbst bleibt vom "Neu mischen" unberührt (sonst würde die manuell
            ' getroffene Auswahl bei jedem Reshuffle wieder verlorengehen) - nur die Reihenfolge der
            ' übrigen Bilder in ihrem Streifen wird gemischt.
            If options.OrderSeed.HasValue Then
                Dim shuffleOrder = ShuffledIndices(otherIndices.Count, options.OrderSeed.Value)
                otherIndices = shuffleOrder.Select(Function(i) otherIndices(i)).ToList()
            End If
            Dim otherCount = otherIndices.Count
            Dim position = If(options.HeroPosition, "Left").Trim().ToUpperInvariant()
            Dim usableWidth = Math.Max(1, width - margin * 2)
            Dim heroSize = CSng(usableWidth * 0.6)

            Dim slots As New List(Of CollageSlot)()
            Dim height As Integer

            Select Case position
                Case "CENTER"
                    ' Hero mittig, die übrigen Bilder als Rahmen ringsherum verteilt (oben/unten/
                    ' links/rechts), reihum zugewiesen - mosaikartiger "Bild im Rahmen"-Look.
                    Dim usableHeight = usableWidth
                    Dim heroCenterSize = CSng(Math.Min(usableWidth, usableHeight) * 0.5)
                    height = margin * 2 + CInt(usableHeight)
                    Dim sideMargin = Math.Max(1.0F, (usableWidth - heroCenterSize) / 2.0F)
                    Dim topBottomHeight = Math.Max(1.0F, (usableHeight - heroCenterSize) / 2.0F)
                    Dim heroX = margin + sideMargin
                    Dim heroY = margin + topBottomHeight
                    slots.Add(New CollageSlot With {.SourceIndex = heroIndex, .X = heroX, .Y = heroY, .Width = heroCenterSize, .Height = heroCenterSize, .RotationDegrees = 0})

                    If otherCount > 0 Then
                        Dim topOthers = otherIndices.Where(Function(idx, i) i Mod 4 = 0).ToList()
                        Dim bottomOthers = otherIndices.Where(Function(idx, i) i Mod 4 = 1).ToList()
                        Dim leftOthers = otherIndices.Where(Function(idx, i) i Mod 4 = 2).ToList()
                        Dim rightOthers = otherIndices.Where(Function(idx, i) i Mod 4 = 3).ToList()

                        If topOthers.Count > 0 Then
                            ' CSng vor dem Teilen: VBs "/" liefert auf zwei Integer ein Double,
                            ' und die Slot-Felder sind Single. Ohne das rechnet die halbe Zeile
                            ' in doppelter Genauigkeit und wird beim Zuweisen wieder verengt.
                            Dim w = Math.Max(1.0F, CSng(usableWidth) / topOthers.Count)
                            For i = 0 To topOthers.Count - 1
                                slots.Add(New CollageSlot With {.SourceIndex = topOthers(i), .X = margin + i * w, .Y = margin, .Width = w, .Height = topBottomHeight, .RotationDegrees = 0})
                            Next
                        End If
                        If bottomOthers.Count > 0 Then
                            Dim w = Math.Max(1.0F, CSng(usableWidth) / bottomOthers.Count)
                            For i = 0 To bottomOthers.Count - 1
                                slots.Add(New CollageSlot With {.SourceIndex = bottomOthers(i), .X = margin + i * w, .Y = heroY + heroCenterSize, .Width = w, .Height = topBottomHeight, .RotationDegrees = 0})
                            Next
                        End If
                        If leftOthers.Count > 0 Then
                            Dim h = Math.Max(1.0F, heroCenterSize / leftOthers.Count)
                            For i = 0 To leftOthers.Count - 1
                                slots.Add(New CollageSlot With {.SourceIndex = leftOthers(i), .X = margin, .Y = heroY + i * h, .Width = sideMargin, .Height = h, .RotationDegrees = 0})
                            Next
                        End If
                        If rightOthers.Count > 0 Then
                            Dim h = Math.Max(1.0F, heroCenterSize / rightOthers.Count)
                            For i = 0 To rightOthers.Count - 1
                                slots.Add(New CollageSlot With {.SourceIndex = rightOthers(i), .X = heroX + heroCenterSize, .Y = heroY + i * h, .Width = sideMargin, .Height = h, .RotationDegrees = 0})
                            Next
                        End If
                    End If

                Case "TOP", "BOTTOM"
                    Dim heroHeight = heroSize
                    Dim otherHeight = Math.Max(1.0F, usableWidth - heroHeight)
                    height = margin * 2 + CInt(heroHeight + otherHeight)
                    Dim heroY = If(position = "TOP", CSng(margin), margin + otherHeight)
                    Dim otherY = If(position = "TOP", margin + heroHeight, CSng(margin))
                    slots.Add(New CollageSlot With {.SourceIndex = heroIndex, .X = margin, .Y = heroY, .Width = usableWidth, .Height = heroHeight, .RotationDegrees = 0})
                    If otherCount > 0 Then
                        Dim otherWidth = Math.Max(1.0F, CSng(usableWidth) / otherCount)
                        For i = 0 To otherCount - 1
                            slots.Add(New CollageSlot With {.SourceIndex = otherIndices(i), .X = margin + i * otherWidth, .Y = otherY, .Width = otherWidth, .Height = otherHeight, .RotationDegrees = 0})
                        Next
                    End If

                Case "RIGHT"
                    Dim heroWidth = heroSize
                    Dim otherWidth = Math.Max(1.0F, usableWidth - heroWidth)
                    height = margin * 2 + CInt(heroWidth)
                    slots.Add(New CollageSlot With {.SourceIndex = heroIndex, .X = margin + otherWidth, .Y = margin, .Width = heroWidth, .Height = heroWidth, .RotationDegrees = 0})
                    If otherCount > 0 Then
                        Dim otherHeight = Math.Max(1.0F, heroWidth / otherCount)
                        For i = 0 To otherCount - 1
                            slots.Add(New CollageSlot With {.SourceIndex = otherIndices(i), .X = margin, .Y = margin + i * otherHeight, .Width = otherWidth, .Height = otherHeight, .RotationDegrees = 0})
                        Next
                    End If

                Case Else ' LEFT
                    Dim heroWidth = heroSize
                    Dim otherWidth = Math.Max(1.0F, usableWidth - heroWidth)
                    height = margin * 2 + CInt(heroWidth)
                    slots.Add(New CollageSlot With {.SourceIndex = heroIndex, .X = margin, .Y = margin, .Width = heroWidth, .Height = heroWidth, .RotationDegrees = 0})
                    If otherCount > 0 Then
                        Dim otherHeight = Math.Max(1.0F, heroWidth / otherCount)
                        For i = 0 To otherCount - 1
                            slots.Add(New CollageSlot With {.SourceIndex = otherIndices(i), .X = margin + heroWidth, .Y = margin + i * otherHeight, .Width = otherWidth, .Height = otherHeight, .RotationDegrees = 0})
                        Next
                    End If
            End Select

            Return (slots, height)
        End Function

        ''' <summary>Ein Haufen Abzuege: jedes Bild als Karte in seinem eigenen Seitenverhaeltnis,
        ''' leicht gedreht, dicht ueberlappend ueber die ganze Flaeche und ueber ihren Rand hinaus.
        '''
        ''' WIE ES DECKT. Die Flaeche (Hoehe wie beim Raster) wird in Zeilen und Spalten geteilt, so
        ''' dass es hoechstens so viele Zellen wie Bilder gibt; jede Zelle bekommt ein Bild, bis zu
        ''' 0,15 der Zelle aus der Mitte verschoben (am Rand der Flaeche nur nach aussen), mit dem
        ''' 1,45- bis 1,65-fachen ihrer Flaeche (Bildgroesse 100, Groessenunterschied 15) und in jeder
        ''' Richtung mindestens dem 1,35-fachen ihrer Kante. So bleibt ab Bildgroesse 100 auch mit
        ''' Drehung und beliebigem Groessenunterschied keine Luecke (gemessen in der Pruefung „Collage Zufall"),
        ''' die Nachbarn liegen uebereinander wie geschuettete Fotos, und die Zeilen sind nicht mehr
        ''' zu sehen. Was ueber
        ''' die Zellen hinaus uebrig ist, faellt an zufaellige Stellen. Bei weniger als vier Bildern
        ''' liegen die Abzuege ganz auf der Flaeche, mit Grund dazwischen: einzeln uebergross und
        ''' angeschnitten saehen sie nicht nach einem Haufen aus.
        '''
        ''' Die Drehung ist gleichmaessig bis 10 Grad (bei wenigen Bildern 6, damit die Ecken auf der
        ''' Flaeche bleiben); dreieckig verteilt lagen fast alle Karten gerade, und der Haufen wirkte
        ''' wie ein Raster. Die Reihenfolge, in der die Karten gezeichnet werden, ist gemischt, damit
        ''' nicht jede Zeile ueber der vorigen liegt. Alles haengt am Seed, „Neu mischen" bleibt
        ''' reproduzierbar. Der Rand (CollageOptions.Margin) traegt hier ein Vorzeichen: positiv
        ''' bleiben die Karten samt gedrehter Ecken innerhalb, negativ duerfen sie so weit ueber die
        ''' Kante ragen (siehe unten).</summary>
        ''' <param name="aspects">Seitenverhaeltnis (Breite durch Hoehe) je Bild, in der Reihenfolge
        ''' der Quellen; Nothing heisst 3:2 fuer alle.</param>
        ''' <param name="fixedHeight">Vorgegebene Hoehe der Flaeche (CollageOptions.FixedHeight), 0
        ''' fuer die Hoehe des Rasters.</param>
        Private Shared Function BuildRandomSlots(count As Integer, width As Integer, gap As Integer, margin As Integer, options As CollageOptions,
                                                 aspects As IList(Of Double), fixedHeight As Integer) As (Slots As List(Of CollageSlot), Height As Integer)
            Dim height = If(fixedHeight > 0, fixedHeight, BuildGridSlots(count, width, gap, margin, options).Height)
            Dim rng = New Random(options.RandomSeed)
            Dim order = If(options.OrderSeed.HasValue, ShuffledIndices(count, options.OrderSeed.Value), Enumerable.Range(0, count).ToList())

            ' DER RAND HAT HIER EIN VORZEICHEN. Positiv: die Abzuege bleiben samt gedrehter Ecken so
            ' weit von der Kante weg, der Haufen liegt wie auf einem Blatt. Null: bis an die Kante.
            ' Negativ: so weit duerfen sie ueber die Kante hinausragen; sehr negativ heisst randlos
            ' angeschnitten. Gedeckt wird die Flaeche innerhalb des Rands (bei negativem die ganze).
            Dim edge = CDbl(options.Margin)
            ' Wie bei Raster und Hero hoechstens 40 Prozent der kuerzeren Seite (MaxMargin).
            Dim inset = Math.Max(0.0, Math.Min(edge, Math.Min(width, height) * 0.4))
            Dim innerWidth = Math.Max(1.0, width - inset * 2.0)
            Dim innerHeight = Math.Max(1.0, height - inset * 2.0)
            ' Wohin eine Karte samt Drehung reichen darf.
            Dim reach = If(edge >= 0, -inset, -edge)
            Dim allowedLeft = -reach, allowedTop = -reach
            Dim allowedRight = width + reach, allowedBottom = height + reach

            Dim rows = Math.Max(1, CInt(Math.Round(Math.Sqrt(count * innerHeight / innerWidth))))
            Dim columns = Math.Max(1, count \ rows)
            rows = Math.Max(1, Math.Min(rows, count \ columns))
            Dim cellWidth = innerWidth / columns
            Dim cellHeight = innerHeight / rows
            Dim few = count < 4
            Dim sizeFactor = Math.Max(0.4, Math.Min(2.0, options.RandomPrintSize / 100.0))
            Dim sizeVariation = Math.Max(0.0, Math.Min(1.0, options.RandomSizeVariation / 100.0))

            Dim slots As New List(Of CollageSlot)()
            For index = 0 To count - 1
                Dim source = order(index)
                Dim centerX, centerY As Double
                If index < columns * rows Then
                    ' Unter Bildgroesse 100 deckt nichts mehr lueckenlos, also darf es weiter
                    ' streuen; sonst bliebe das Raster als Reihen kleiner Karten zu sehen.
                    Dim jitter = If(few, 0.05, 0.15 + Math.Max(0.0, 1.0 - sizeFactor) * 0.6)
                    Dim col = index Mod columns
                    Dim row = index \ columns
                    Dim dx = (rng.NextDouble() * 2.0 - 1.0) * jitter
                    Dim dy = (rng.NextDouble() * 2.0 - 1.0) * jitter
                    ' Am Rand der Flaeche nur nach aussen: nach innen geschoben und gedreht liess
                    ' eine Randkarte den Grund in der Ecke durchscheinen.
                    ' Gibt es nur eine Spalte oder Zeile, liegt die Zelle an beiden Raendern: dann in
                    ' dieser Richtung gar nicht.
                    If Not few Then
                        If columns = 1 Then
                            dx = 0.0
                        ElseIf col = 0 Then
                            dx = -Math.Abs(dx)
                        ElseIf col = columns - 1 Then
                            dx = Math.Abs(dx)
                        End If
                        If rows = 1 Then
                            dy = 0.0
                        ElseIf row = 0 Then
                            dy = -Math.Abs(dy)
                        ElseIf row = rows - 1 Then
                            dy = Math.Abs(dy)
                        End If
                    End If
                    centerX = inset + (col + 0.5 + dx) * cellWidth
                    centerY = inset + (row + 0.5 + dy) * cellHeight
                Else
                    centerX = inset + rng.NextDouble() * innerWidth
                    centerY = inset + rng.NextDouble() * innerHeight
                End If
                ' Sehr schmale oder hohe Bilder werden fuer die Karte beschnitten, sonst deckt ein
                ' Panorama seine Zelle nicht.
                Dim aspect = If(aspects IsNot Nothing AndAlso source < aspects.Count, aspects(source), 1.5)
                aspect = Math.Max(0.6, Math.Min(1.7, aspect))
                ' Groesse (RandomPrintSize) und Streuung (RandomSizeVariation): bei 100 und 15 das
                ' 1,45- bis 1,65-fache der Zellflaeche wie vorher.
                Dim vary = 1.0 + (rng.NextDouble() * 2.0 - 1.0) * sizeVariation * 0.5
                Dim cover = If(few, 0.8, 1.55) * sizeFactor * vary
                Dim area = cellWidth * cellHeight * cover * cover
                Dim printWidth = CSng(Math.Sqrt(area * aspect))
                Dim printHeight = CSng(Math.Sqrt(area / aspect))
                If Not few Then
                    ' In beiden Richtungen mindestens das 1,35-fache der Zelle: ein Hochformat in
                    ' einer breiten Zelle deckte sie nach der Flaeche gerechnet nicht mehr. Die halbe
                    ' Kante (0,675) bleibt so groesser als halbe Zelle plus Verschiebung (0,65).
                    ' Unter Bildgroesse 100 mit ihr kleiner: dann soll der Grund durchscheinen.
                    Dim minimum = 1.35 * Math.Min(1.0, sizeFactor)
                    Dim grow = Math.Max(1.0, Math.Max(cellWidth * minimum / printWidth, cellHeight * minimum / printHeight))
                    printWidth *= CSng(grow)
                    printHeight *= CSng(grow)
                End If
                If few Then
                    ' Ganz auf der Zelle: notfalls kleiner.
                    Dim fit = Math.Min(1.0, Math.Min(cellWidth * 0.8 / printWidth, cellHeight * 0.8 / printHeight))
                    printWidth *= CSng(fit)
                    printHeight *= CSng(fit)
                End If
                Dim rotation = (rng.NextDouble() * 2.0 - 1.0) * If(few, 6.0, 10.0)
                ' Die gedrehte Karte samt Ecken in den erlaubten Bereich schieben; passt sie nicht
                ' hinein, in dessen Mitte.
                Dim radians = rotation * Math.PI / 180.0
                Dim halfX = (printWidth * Math.Abs(Math.Cos(radians)) + printHeight * Math.Abs(Math.Sin(radians))) / 2.0
                Dim halfY = (printWidth * Math.Abs(Math.Sin(radians)) + printHeight * Math.Abs(Math.Cos(radians))) / 2.0
                ' Groesser als der erlaubte Bereich (flache Flaeche, wenige Zeilen, breiter Rand):
                ' verkleinern, sonst ragte die Karte trotz Rand hinaus.
                Dim shrink = Math.Min(1.0, Math.Min((allowedRight - allowedLeft) / (halfX * 2.0),
                                                    (allowedBottom - allowedTop) / (halfY * 2.0)))
                If shrink < 1.0 Then
                    printWidth *= CSng(shrink)
                    printHeight *= CSng(shrink)
                    halfX *= shrink
                    halfY *= shrink
                End If
                centerX = ClampCenter(centerX, halfX, allowedLeft, allowedRight)
                centerY = ClampCenter(centerY, halfY, allowedTop, allowedBottom)
                slots.Add(New CollageSlot With {
                    .SourceIndex = source,
                    .X = CSng(centerX) - printWidth / 2.0F,
                    .Y = CSng(centerY) - printHeight / 2.0F,
                    .Width = printWidth,
                    .Height = printHeight,
                    .RotationDegrees = CSng(rotation),
                    .IsPrint = True
                })
            Next

            ' Zeichenreihenfolge mischen (Fisher-Yates mit demselben Zufall).
            For i = slots.Count - 1 To 1 Step -1
                Dim j = rng.Next(i + 1)
                Dim temp = slots(i)
                slots(i) = slots(j)
                slots(j) = temp
            Next
            Return (slots, height)
        End Function

        ''' <summary>Mitte so, dass <paramref name="half"/> zu beiden Seiten in [low, high] liegt;
        ''' ist der Bereich zu klein, seine Mitte.</summary>
        Private Shared Function ClampCenter(center As Double, half As Double, low As Double, high As Double) As Double
            If high - low <= half * 2.0 Then Return (low + high) / 2.0
            Return Math.Max(low + half, Math.Min(high - half, center))
        End Function

        Private Shared Function GetUniformToFillSourceRect(sourceWidth As Integer, sourceHeight As Integer, targetWidth As Integer, targetHeight As Integer) As SKRect
            Dim sourceAspect = sourceWidth / CDbl(sourceHeight)
            Dim targetAspect = targetWidth / CDbl(targetHeight)
            If sourceAspect > targetAspect Then
                Dim cropWidth = CSng(sourceHeight * targetAspect)
                Dim left = (sourceWidth - cropWidth) / 2.0F
                Return New SKRect(left, 0, left + cropWidth, sourceHeight)
            End If

            Dim cropHeight = CSng(sourceWidth / targetAspect)
            Dim top = (sourceHeight - cropHeight) / 2.0F
            Return New SKRect(0, top, sourceWidth, top + cropHeight)
        End Function

        ''' <summary>Der Ausschnitt <paramref name="sourceRect"/>, verkleinert auf die Kachelgroesse.
        ''' Nothing, wenn nichts zu verkleinern ist oder der Farbtyp nicht passt; dann zeichnet der
        ''' Aufrufer wie bisher direkt aus der Quelle. Der Ausschnitt wird auf ganze Punkte gerundet,
        ''' die Verschiebung bleibt unter einem Quellpunkt.</summary>
        Private Shared Function ReduceToTile(source As SKBitmap, sourceRect As SKRect, tileWidth As Integer, tileHeight As Integer) As SKBitmap
            Dim area = SKRectI.Round(sourceRect)
            area.Intersect(New SKRectI(0, 0, source.Width, source.Height))
            If area.Width <= 0 OrElse area.Height <= 0 Then Return Nothing
            If Not ImageResampler.IsDownscale(area.Width, area.Height, tileWidth, tileHeight) Then Return Nothing
            Using subset = New SKBitmap()
                If Not source.ExtractSubset(subset, area) Then Return Nothing
                Return ImageResampler.Resize(subset, Math.Min(tileWidth, area.Width), Math.Min(tileHeight, area.Height),
                                             ImageResampler.Kernel.CatmullRom)
            End Using
        End Function

        Private Shared Function ParseColor(value As String, fallback As SKColor) As SKColor
            If String.IsNullOrWhiteSpace(value) Then Return fallback
            Dim text = value.Trim().TrimStart("#"c)
            Dim raw As UInteger
            If UInteger.TryParse(text, Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture, raw) Then
                If text.Length <= 6 Then
                    Return New SKColor(CByte((raw >> 16) And &HFFUI), CByte((raw >> 8) And &HFFUI), CByte(raw And &HFFUI), 255)
                End If
                Return New SKColor(CByte((raw >> 16) And &HFFUI), CByte((raw >> 8) And &HFFUI), CByte(raw And &HFFUI), CByte((raw >> 24) And &HFFUI))
            End If
            Return fallback
        End Function
    End Class

End Namespace
