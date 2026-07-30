Imports System
Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO
Imports System.Linq
Imports System.Threading.Tasks
Imports Avalonia.Media.Imaging
Imports SkiaSharp

Namespace Services

    ''' <summary>Druckoptionen. Die String-Eigenschaften tragen IMMER die englischen Logikschlüssel
    ''' ("A4", "Fit", ...) - niemals den übersetzten Anzeigetext. Die Anzeige übersetzt das ViewModel,
    ''' sonst bricht der Vergleich in jeder anderen Sprache.</summary>
    Public Class PrintOptions
        ''' "A4" | "A3" | "A5" | "Letter" | "Legal"
        Public Property PageSize As String = "A4"
        Public Property Landscape As Boolean = False
        Public Property MarginMm As Double = 10
        ''' "Fit" (einpassen, Seitenverhältnis bleibt) | "Fill" (Fläche füllen, Bild wird beschnitten)
        Public Property FitMode As String = "Fit"
        ''' 1 | 4 (2x2) | 9 (3x3) | 16 (4x4) - alles andere wird auf die nächste Quadratzahl gerundet.
        Public Property ImagesPerPage As Integer = 1
        ''' Dateiname unter jedem Bild (Kontaktabzug).
        Public Property ShowCaption As Boolean = False
        ''' <summary>Randlos: kein Seitenrand und das Bild deckt die Seite vollständig ab. Überschreibt
        ''' MarginMm und FitMode, statt sie zu überschreiben - so bleiben die zuletzt gewählten Werte
        ''' erhalten, wenn der Nutzer randlos wieder abschaltet.</summary>
        Public Property Borderless As Boolean = False
        ''' <summary>Wie oft JEDES Bild wiederholt wird (1 = einmal). Gedacht für die Einzelauswahl:
        ''' zusammen mit ImagesPerPage ergibt das z.B. dasselbe Bild 4x auf einer Seite.</summary>
        Public Property Copies As Integer = 1
        Public Property BackgroundColor As String = "#FFFFFFFF"

        ''' <summary>Der tatsächlich zu zeichnende Rand in mm - randlos heißt 0.</summary>
        Public Function EffectiveMarginMm() As Double
            If Borderless Then Return 0
            Return Math.Max(0, MarginMm)
        End Function

        ''' <summary>Der tatsächlich wirksame Skalierungsmodus - randlos erzwingt "Fill", sonst bliebe
        ''' bei abweichendem Seitenverhältnis trotz Rand 0 weißer Rand stehen.</summary>
        Public Function EffectiveFitMode() As String
            If Borderless Then Return "Fill"
            Return FitMode
        End Function

        Public Function Clone() As PrintOptions
            Return New PrintOptions With {
                .PageSize = PageSize,
                .Landscape = Landscape,
                .MarginMm = MarginMm,
                .FitMode = FitMode,
                .ImagesPerPage = ImagesPerPage,
                .ShowCaption = ShowCaption,
                .Borderless = Borderless,
                .Copies = Copies,
                .BackgroundColor = BackgroundColor
            }
        End Function
    End Class

    ''' <summary>Drucken über einen PDF-Zwischenschritt. Linux (CUPS), Windows (WinSpool) und macOS
    ''' (CorePrinting) haben unvereinbare Druck-APIs und Avalonia bringt keine mit - deshalb rendert
    ''' FerrumPix ein PDF und übergibt es dem Betriebssystem, das den gewohnten Systemdruckdialog
    ''' zeigt. Aufbau bewusst wie CollageService: zustandslos, Shared, plus RenderPreview für die
    ''' Live-Vorschau im Dialog.</summary>
    Public Class PrintService

        ''' Ein PostScript-Punkt ist 1/72 Zoll - das Koordinatensystem von SKDocument.CreatePdf.
        Private Const PointsPerInch As Double = 72.0
        Private Const MmPerInch As Double = 25.4

        ''' Abstand zwischen den Zellen beim Kontaktabzug (Punkt).
        Private Const CellGapPoints As Single = 8.0F
        ''' Höhe der Bildunterschrift innerhalb einer Zelle (Punkt).
        Private Const CaptionHeightPoints As Single = 12.0F
        Private Const CaptionFontSizePoints As Single = 7.5F

        ''' Obergrenze für PrintOptions.Copies - mehr als eine volle 4x4-Seite pro Bild ergibt hier
        ''' keinen Sinn und schützt vor versehentlich riesigen PDFs.
        Public Const MaxCopies As Integer = 16

        ''' Temp-PDFs älter als das hier werden beim nächsten Druck weggeräumt.
        Private Const TempRetentionHours As Double = 24

        ''' Zielauflösung der eingebetteten Bilder. Skia legt Bitmaps UNKOMPRIMIERT ins PDF - ohne
        ''' diese Begrenzung wurde eine 5-seitige A4-Datei aus normalen Kamerabildern 113 MB groß
        ''' (gemessen). 300 dpi ist Fotodruckqualität; mehr löst kein Drucker auf.
        Private Const TargetDpi As Double = 300
        ''' Qualität der ins PDF eingebetteten JPEGs. Skia übernimmt bereits komprimierte Daten
        ''' unverändert, statt sie roh abzulegen - das ist der eigentliche Größenhebel.
        Private Const EmbeddedJpegQuality As Integer = 90

        ''' <summary>Seitengröße in Punkt (Breite x Höhe im Hochformat).</summary>
        Private Shared Function GetPageSizePoints(pageSize As String) As SKSize
            Select Case If(pageSize, "").Trim().ToUpperInvariant()
                Case "A3" : Return New SKSize(842.0F, 1191.0F)
                Case "A5" : Return New SKSize(420.0F, 595.0F)
                Case "LETTER" : Return New SKSize(612.0F, 792.0F)
                Case "LEGAL" : Return New SKSize(612.0F, 1008.0F)
                Case Else : Return New SKSize(595.0F, 842.0F) ' A4
            End Select
        End Function

        ''' <summary>Die Seitenfläche nach Ausrichtung, in Punkt.</summary>
        Private Shared Function GetOrientedPageSize(options As PrintOptions) As SKSize
            Dim size = GetPageSizePoints(options.PageSize)
            If options.Landscape Then Return New SKSize(size.Height, size.Width)
            Return size
        End Function

        Private Shared Function MmToPoints(mm As Double) As Single
            Return CSng(mm * PointsPerInch / MmPerInch)
        End Function

        ''' <summary>Auf eine unterstützte Zellenzahl normalisieren (1/4/9/16).</summary>
        Private Shared Function NormalizeImagesPerPage(value As Integer) As Integer
            If value >= 16 Then Return 16
            If value >= 9 Then Return 9
            If value >= 4 Then Return 4
            Return 1
        End Function

        ''' <summary>Teilt die bedruckbare Fläche in gleich große Zellen - so viele, wie auf DIESER
        ''' Seite wirklich Bilder stehen. Ein 2x2-Raster mit zwei Bildern ergibt deshalb eine Reihe
        ''' aus zwei großen Zellen statt vier kleiner, von denen die halbe Seite leer bliebe: die
        ''' Rasterwahl ist eine OBERGRENZE ("höchstens 4 pro Seite"), keine Anweisung, Papier zu
        ''' verschenken. Bei mehreren Seiten kann die letzte dadurch größere Bilder tragen als die
        ''' vollen davor - das ist gewollt.
        '''
        ''' Wie die Bilder auf Zeilen und Spalten verteilt werden, entscheidet ihr Seitenverhältnis:
        ''' zwei querformatige Fotos werden auf einer Querformat-Seite ÜBEREINANDER am größten, zwei
        ''' quadratische NEBENEINANDER. Deshalb wird jede mögliche Aufteilung durchgerechnet und die
        ''' genommen, bei der insgesamt die meiste Bildfläche herauskommt. Sind die Maße unbekannt
        ''' (Format ohne schnelle Größenabfrage), bleibt es beim breiten Raster.
        '''
        ''' Rückgabe in Seitenkoordinaten (Punkt), zeilenweise von links oben.</summary>
        ''' <param name="scale">Vorschau-Maßstab. Der Zellabstand steht wie der Rand in Punkt und
        ''' muss mitskaliert werden - sonst wirkt er in der verkleinerten Vorschau breiter, als er
        ''' gedruckt wird.</param>
        Private Shared Function BuildCells(contentRect As SKRect, imagesPerPage As Integer,
                                           imageAspects As IList(Of Double), scale As Single) As List(Of SKRect)
            Dim cells = New List(Of SKRect)()
            Dim perPage = NormalizeImagesPerPage(imagesPerPage)
            Dim rasterColumns = CInt(Math.Sqrt(perPage))
            Dim count = Math.Max(1, Math.Min(perPage, If(imageAspects Is Nothing, 1, imageAspects.Count)))
            Dim gap = CellGapPoints * Math.Max(0.01F, scale)

            Dim bestColumns = Math.Min(count, rasterColumns)
            Dim bestArea = -1.0
            For spalten = 1 To Math.Min(count, rasterColumns)
                Dim zeilen = CInt(Math.Ceiling(count / CDbl(spalten)))
                If zeilen > rasterColumns Then Continue For
                Dim width = (contentRect.Width - gap * (spalten - 1)) / spalten
                Dim height = (contentRect.Height - gap * (zeilen - 1)) / zeilen
                If width <= 0 OrElse height <= 0 Then Continue For

                Dim area = 0.0
                For Each aspect In imageAspects
                    If aspect <= 0 Then Continue For
                    ' Eingepasste Bildfläche in dieser Zelle: die kürzere der beiden Kanten begrenzt.
                    Dim imageWidth = Math.Min(width, height * aspect)
                    area += imageWidth * (imageWidth / aspect)
                Next
                If area > bestArea Then
                    bestArea = area
                    bestColumns = spalten
                End If
            Next

            Dim columns = bestColumns
            Dim rows = CInt(Math.Ceiling(count / CDbl(columns)))
            Dim cellWidth = (contentRect.Width - gap * (columns - 1)) / columns
            Dim cellHeight = (contentRect.Height - gap * (rows - 1)) / rows
            If cellWidth <= 0 OrElse cellHeight <= 0 Then Return cells

            For row = 0 To rows - 1
                For col = 0 To columns - 1
                    Dim x = contentRect.Left + col * (cellWidth + gap)
                    Dim y = contentRect.Top + row * (cellHeight + gap)
                    cells.Add(New SKRect(x, y, x + cellWidth, y + cellHeight))
                Next
            Next
            Return cells
        End Function

        ''' <summary>Seitenverhältnisse (Breite/Höhe) der Bilder einer Seite - Grundlage für die
        ''' Aufteilung in BuildCells. Unlesbare oder unbekannte Maße kommen als 0 zurück und zählen
        ''' dort nicht mit; gemessen wird über die ORIENTIERTEN Maße, sonst läge ein hochkant
        ''' aufgenommenes JPEG quer.</summary>
        Private Shared Function BuildImageAspects(pagePaths As IList(Of String)) As List(Of Double)
            Dim aspects = New List(Of Double)()
            For Each imagePath In If(pagePaths, New List(Of String)())
                Dim aspect = 0.0
                Try
                    Dim size = ImageProcessor.GetOrientedImageSize(imagePath)
                    If size.Width > 0 AndAlso size.Height > 0 Then aspect = size.Width / CDbl(size.Height)
                Catch
                End Try
                aspects.Add(aspect)
            Next
            Return aspects
        End Function

        ''' <summary>Das Zielrechteck für ein Bild innerhalb seiner Zelle. "Fit" passt vollständig
        ''' hinein (Rest bleibt leer), "Fill" deckt die Zelle ab (Überstand wird vom Aufrufer
        ''' weggeclippt).</summary>
        Private Shared Function BuildImageRect(cell As SKRect, imageWidth As Integer, imageHeight As Integer, fitMode As String) As SKRect
            If imageWidth <= 0 OrElse imageHeight <= 0 Then Return cell

            Dim scaleX = cell.Width / imageWidth
            Dim scaleY = cell.Height / imageHeight
            Dim scale = If(String.Equals(fitMode, "Fill", StringComparison.OrdinalIgnoreCase),
                           Math.Max(scaleX, scaleY),
                           Math.Min(scaleX, scaleY))

            Dim targetWidth = imageWidth * scale
            Dim targetHeight = imageHeight * scale
            Dim left = cell.MidX - targetWidth / 2.0F
            Dim top = cell.MidY - targetHeight / 2.0F
            Return New SKRect(left, top, left + targetWidth, top + targetHeight)
        End Function

        ''' <summary>Zeichnet ein Bild in sein Zielrechteck und hält dabei die Dateigröße im Zaum:
        ''' erst auf die bei TargetDpi tatsächlich benötigte Pixelzahl herunterskalieren, dann - für
        ''' die PDF-Ausgabe - als JPEG einbetten statt als Rohbitmap. Für die Bildschirmvorschau
        ''' entfällt der JPEG-Umweg (nur Rechenzeit, kein Nutzen).</summary>
        Private Shared Sub DrawImageIntoRect(canvas As SKCanvas,
                                             bitmap As SKBitmap,
                                             clipRect As SKRect,
                                             destRect As SKRect,
                                             embedAsJpeg As Boolean,
                                             paint As SKPaint)
            ' Wie viele Pixel das Zielrechteck bei Druckauflösung überhaupt aufnehmen kann.
            Dim maxWidth = Math.Max(1, CInt(Math.Ceiling(destRect.Width * TargetDpi / PointsPerInch)))
            Dim maxHeight = Math.Max(1, CInt(Math.Ceiling(destRect.Height * TargetDpi / PointsPerInch)))

            Dim scaled As SKBitmap = Nothing
            Dim source = bitmap
            Try
                If bitmap.Width > maxWidth OrElse bitmap.Height > maxHeight Then
                    Dim ratio = Math.Min(maxWidth / CDbl(bitmap.Width), maxHeight / CDbl(bitmap.Height))
                    Dim targetWidth = Math.Max(1, CInt(Math.Round(bitmap.Width * ratio)))
                    Dim targetHeight = Math.Max(1, CInt(Math.Round(bitmap.Height * ratio)))
                    scaled = bitmap.Resize(New SKImageInfo(targetWidth, targetHeight), ImageProcessor.SamplingHigh)
                    If scaled IsNot Nothing Then source = scaled
                End If

                canvas.Save()
                ' "Fill" lässt das Bild über die Zelle hinauslaufen - der Überstand darf die
                ' Nachbarzellen nicht überschreiben.
                canvas.ClipRect(clipRect)
                If embedAsJpeg Then
                    ' JPEG kennt kein Alpha; ohne Weiß-Untergrund würden transparente Bereiche
                    ' schwarz (derselbe Fall wie beim JPEG-Export in ImageProcessor.SaveImage).
                    Using opaque = FlattenToWhite(source)
                        Using image = SKImage.FromBitmap(opaque)
                            Using data = image.Encode(SKEncodedImageFormat.Jpeg, EmbeddedJpegQuality)
                                Using encoded = SKImage.FromEncodedData(data)
                                    If encoded IsNot Nothing Then
                                        canvas.DrawImage(encoded, destRect, ImageProcessor.SamplingHigh, paint)
                                    End If
                                End Using
                            End Using
                        End Using
                    End Using
                Else
                    ImageProcessor.DrawBitmapSampled(canvas, source,
                                                     New SKRect(0, 0, source.Width, source.Height),
                                                     destRect, ImageProcessor.SamplingHigh, paint)
                End If
                canvas.Restore()
            Finally
                scaled?.Dispose()
            End Try
        End Sub

        ''' <summary>Weißer Untergrund für die JPEG-Einbettung. Liefert immer ein NEUES Bitmap,
        ''' das der Aufrufer disposed.</summary>
        Private Shared Function FlattenToWhite(source As SKBitmap) As SKBitmap
            Dim flattened = New SKBitmap(source.Width, source.Height, source.ColorType, SKAlphaType.Opaque)
            Using canvas = New SKCanvas(flattened)
                canvas.Clear(SKColors.White)
                canvas.DrawBitmap(source, 0, 0)
            End Using
            Return flattened
        End Function

        Private Shared Function ParseColor(value As String, fallback As SKColor) As SKColor
            Dim parsed As SKColor
            If Not String.IsNullOrWhiteSpace(value) AndAlso SKColor.TryParse(value, parsed) Then Return parsed
            Return fallback
        End Function

        ''' <summary>Zeichnet eine Seite: Hintergrund, die Bilder ihrer Zellen und optional die
        ''' Dateinamen. Gemeinsam von PDF-Ausgabe und Vorschau genutzt, damit die Vorschau nicht
        ''' vom Druckergebnis abweichen kann.</summary>
        Private Shared Sub DrawPage(canvas As SKCanvas,
                                    pagePaths As IList(Of String),
                                    pageSize As SKSize,
                                    options As PrintOptions,
                                    scale As Single,
                                    embedAsJpeg As Boolean)
            canvas.Clear(ParseColor(options.BackgroundColor, SKColors.White))

            Dim margin = MmToPoints(options.EffectiveMarginMm()) * scale
            Dim contentRect = New SKRect(margin, margin,
                                         pageSize.Width * scale - margin,
                                         pageSize.Height * scale - margin)
            If contentRect.Width <= 0 OrElse contentRect.Height <= 0 Then Return

            Dim cells = BuildCells(contentRect, options.ImagesPerPage, BuildImageAspects(pagePaths), scale)
            If cells.Count = 0 Then Return

            Using imagePaint = New SKPaint With {.IsAntialias = True}
                Using captionPaint = New SKPaint With {.IsAntialias = True, .Color = SKColors.Black}
                    Using captionFont = New SKFont(SKTypeface.Default, CaptionFontSizePoints * scale) With {.LinearMetrics = True}
                        For index = 0 To Math.Min(pagePaths.Count, cells.Count) - 1
                            ' NICHT "path"/"file" nennen: VB ist case-insensitiv, das verdeckt die
                            ' Typen Path und File im ganzen Block.
                            Dim imagePath = pagePaths(index)
                            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then Continue For

                            Dim cell = cells(index)
                            ' Platz für die Bildunterschrift unten aus der Zelle herausnehmen, damit
                            ' Bild und Text sich nicht überlappen.
                            Dim imageCell = cell
                            If options.ShowCaption Then
                                imageCell = New SKRect(cell.Left, cell.Top, cell.Right,
                                                       cell.Bottom - CaptionHeightPoints * scale)
                                If imageCell.Height <= 0 Then imageCell = cell
                            End If

                            ' DecodeDevelopedForOutput ist der einzige Funnel, der RAW/ICO/WebP, die
                            ' EXIF-Orientierung, .fpx-Projekte UND ein danebenliegendes Rezept
                            ' korrekt behandelt - niemals SKBitmap.Decode direkt. Gedruckt wird die
                            ' BEARBEITETE Fassung; sonst käme aus dem Drucker ein anderes Bild, als
                            ' der Editor zeigt.
                            Dim drawnRect As SKRect
                            Using bitmap = ImageProcessor.DecodeDevelopedForOutput(imagePath)
                                If bitmap Is Nothing Then
                                    ' Ohne diese Spur ist eine leer gebliebene Zelle im fertigen PDF
                                    ' nicht mehr zu erklären (so fiel .fpx lange nicht auf).
                                    DiagnosticLogService.LogAlways("Print", $"Nicht dekodierbar, Zelle bleibt leer: {imagePath}")
                                    Continue For
                                End If
                                drawnRect = BuildImageRect(imageCell, bitmap.Width, bitmap.Height, options.EffectiveFitMode())
                                DrawImageIntoRect(canvas, bitmap, imageCell, drawnRect, embedAsJpeg, imagePaint)
                            End Using

                            If options.ShowCaption Then
                                Dim caption = Path.GetFileName(imagePath)
                                ' Direkt UNTER das Bild setzen, nicht an den Zellenboden: ein Bild,
                                ' das seine Zelle nicht ausfüllt (Hochformat in breiter Zelle), ließe
                                ' die Unterschrift sonst weit abgesetzt schweben.
                                Dim baseline = Math.Min(drawnRect.Bottom + CaptionFontSizePoints * scale,
                                                        cell.Bottom - 2.0F * scale)
                                canvas.DrawText(caption, cell.MidX, baseline,
                                                SKTextAlign.Center, captionFont, captionPaint)
                            End If
                        Next
                    End Using
                End Using
            End Using
        End Sub

        ''' <summary>Verteilt die Bilder auf Seiten.</summary>
        Private Shared Function BuildPages(imagePaths As IEnumerable(Of String), options As PrintOptions) As List(Of List(Of String))
            Dim pages = New List(Of List(Of String))()
            Dim paths = If(imagePaths, Enumerable.Empty(Of String)()).
                Where(Function(p) Not String.IsNullOrWhiteSpace(p) AndAlso File.Exists(p)).
                ToList()
            If paths.Count = 0 Then Return pages

            ' Wiederholungen BILDWEISE, nicht als angehängte zweite Runde: so landen die Kopien
            ' desselben Motivs nebeneinander auf der Seite statt über alle Seiten verstreut.
            Dim copies = Math.Max(1, Math.Min(MaxCopies, options.Copies))
            If copies > 1 Then
                paths = paths.SelectMany(Function(p) Enumerable.Repeat(p, copies)).ToList()
            End If

            Dim perPage = NormalizeImagesPerPage(options.ImagesPerPage)
            For offset = 0 To paths.Count - 1 Step perPage
                pages.Add(paths.Skip(offset).Take(perPage).ToList())
            Next
            Return pages
        End Function

        ''' <summary>Wie viele Seiten das aktuelle Ergebnis hätte - für die Anzeige im Dialog.</summary>
        Public Shared Function GetPageCount(imagePaths As IEnumerable(Of String), options As PrintOptions) As Integer
            If options Is Nothing Then Return 0
            Return BuildPages(imagePaths, options).Count
        End Function

        ''' <summary>Schreibt ein mehrseitiges PDF. Rückgabe False, wenn nichts Druckbares dabei war
        ''' oder das Schreiben fehlschlug.</summary>
        Public Shared Function WritePdf(imagePaths As IEnumerable(Of String), options As PrintOptions, outputPath As String) As Boolean
            If options Is Nothing OrElse String.IsNullOrWhiteSpace(outputPath) Then Return False

            Dim pages = BuildPages(imagePaths, options)
            If pages.Count = 0 Then Return False

            Try
                Dim pageSize = GetOrientedPageSize(options)
                Dim dir = Path.GetDirectoryName(outputPath)
                If Not String.IsNullOrEmpty(dir) Then Directory.CreateDirectory(dir)

                Using stream = New SKFileWStream(outputPath)
                    Using doc = SKDocument.CreatePdf(stream)
                        If doc Is Nothing Then Return False
                        For Each page In pages
                            ' Der Canvas von BeginPage gehört dem Dokument - NICHT disposen,
                            ' EndPage gibt ihn frei.
                            Dim canvas = doc.BeginPage(pageSize.Width, pageSize.Height)
                            If canvas IsNot Nothing Then DrawPage(canvas, page, pageSize, options, 1.0F, embedAsJpeg:=True)
                            doc.EndPage()
                        Next
                        doc.Close()
                    End Using
                End Using
                Return File.Exists(outputPath)
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Einseitiges PDF aus einem bereits fertig gerenderten Bitmap - der Weg für
        ''' "Speichern unter"/"Konvertieren nach" mit Zielformat PDF. Das Bitmap gehört dem
        ''' Aufrufer und wird hier nicht disposed.</summary>
        Public Shared Function WriteSinglePagePdf(bitmap As SKBitmap, outputPath As String, options As PrintOptions) As Boolean
            If bitmap Is Nothing OrElse String.IsNullOrWhiteSpace(outputPath) Then Return False
            Dim opt = If(options, New PrintOptions()).Clone()
            ' Ein Bild pro Seite und keine Unterschrift: der Export soll die Datei abbilden,
            ' nicht einen Kontaktabzug erzeugen.
            opt.ImagesPerPage = 1
            opt.ShowCaption = False
            opt.Copies = 1

            Try
                Dim pageSize = GetOrientedPageSize(opt)
                Dim margin = MmToPoints(opt.EffectiveMarginMm())
                Dim contentRect = New SKRect(margin, margin, pageSize.Width - margin, pageSize.Height - margin)
                If contentRect.Width <= 0 OrElse contentRect.Height <= 0 Then Return False

                Dim dir = Path.GetDirectoryName(outputPath)
                If Not String.IsNullOrEmpty(dir) Then Directory.CreateDirectory(dir)

                Using stream = New SKFileWStream(outputPath)
                    Using doc = SKDocument.CreatePdf(stream)
                        If doc Is Nothing Then Return False
                        Dim canvas = doc.BeginPage(pageSize.Width, pageSize.Height)
                        If canvas IsNot Nothing Then
                            canvas.Clear(ParseColor(opt.BackgroundColor, SKColors.White))
                            Dim target = BuildImageRect(contentRect, bitmap.Width, bitmap.Height, opt.EffectiveFitMode())
                            Using paint = New SKPaint With {.IsAntialias = True}
                                DrawImageIntoRect(canvas, bitmap, contentRect, target, embedAsJpeg:=True, paint:=paint)
                            End Using
                        End If
                        doc.EndPage()
                        doc.Close()
                    End Using
                End Using
                Return File.Exists(outputPath)
            Catch
                Return False
            End Try
        End Function

        ''' <summary>Rendert NUR die erste Seite verkleinert für die Live-Vorschau im Druckdialog -
        ''' identisches Layout wie die Ausgabe, weil dieselbe DrawPage-Routine läuft.</summary>
        Public Shared Function RenderPreview(imagePaths As IEnumerable(Of String), options As PrintOptions, maxDimension As Integer) As Bitmap
            If options Is Nothing Then Return Nothing

            Dim pages = BuildPages(imagePaths, options)
            If pages.Count = 0 Then Return Nothing

            Dim pageSize = GetOrientedPageSize(options)
            Dim longestSide = Math.Max(pageSize.Width, pageSize.Height)
            If longestSide <= 0 Then Return Nothing

            Dim scale = CSng(Math.Max(0.1, Math.Min(4.0, maxDimension / longestSide)))
            Dim width = Math.Max(32, CInt(Math.Round(pageSize.Width * scale)))
            Dim height = Math.Max(32, CInt(Math.Round(pageSize.Height * scale)))

            Try
                Using surfaceBitmap = New SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul)
                    Using canvas = New SKCanvas(surfaceBitmap)
                        DrawPage(canvas, pages(0), pageSize, options, scale, embedAsJpeg:=False)
                    End Using
                    Return ImageProcessor.ToAvaloniaBitmap(surfaceBitmap)
                End Using
            Catch ex As Exception
                ' Eine leere Vorschau ohne jede Spur wäre im Dialog nicht zu diagnostizieren.
                DiagnosticLogService.LogAlways("Print", $"Vorschau fehlgeschlagen: {ex.GetType().Name} {ex.Message}")
                Return Nothing
            End Try
        End Function

        ''' <summary>Der Ordner für die temporären Druck-PDFs.</summary>
        Private Shared ReadOnly Property TempPrintDirectory As String
            Get
                Return Path.Combine(Path.GetTempPath(), "FerrumPix", "Print")
            End Get
        End Property

        ''' <summary>Räumt alte Druck-PDFs weg, damit /tmp nicht zuläuft. Fehler sind hier
        ''' bedeutungslos - im Zweifel bleibt eine Datei liegen.</summary>
        Private Shared Sub CleanupOldTempFiles()
            Try
                If Not Directory.Exists(TempPrintDirectory) Then Return
                Dim cutoff = DateTime.UtcNow.AddHours(-TempRetentionHours)
                For Each pdfFile In Directory.GetFiles(TempPrintDirectory, "*.pdf")
                    Try
                        If File.GetLastWriteTimeUtc(pdfFile) < cutoff Then File.Delete(pdfFile)
                    Catch
                    End Try
                Next
            Catch
            End Try
        End Sub

        ''' <summary>Erzeugt das PDF und übergibt es dem Betriebssystem. UseShellExecute löst .NET
        ''' auf Linux nach xdg-open, auf macOS nach open und auf Windows nach ShellExecute auf -
        ''' ein Codepfad für alle drei Plattformen (wie OpenInFileManager in der Galerie). Der
        ''' Nutzer druckt dann aus dem Systemviewer mit dem gewohnten Druckdialog.</summary>
        Public Shared Async Function PrintAsync(imagePaths As IEnumerable(Of String), options As PrintOptions) As Task(Of Boolean)
            If options Is Nothing Then Return False
            Dim paths = If(imagePaths, Enumerable.Empty(Of String)()).ToList()
            If paths.Count = 0 Then Return False

            Dim opt = options.Clone()
            Dim watch = Stopwatch.StartNew()

            Dim pdfPath = Await Task.Run(Function() As String
                                             CleanupOldTempFiles()
                                             Try
                                                 Directory.CreateDirectory(TempPrintDirectory)
                                             Catch
                                                 Return Nothing
                                             End Try
                                             Dim baseName = Path.GetFileNameWithoutExtension(paths(0))
                                             If String.IsNullOrWhiteSpace(baseName) Then baseName = "Druck"
                                             Dim target = Path.Combine(TempPrintDirectory,
                                                                       $"{baseName}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf")
                                             If WritePdf(paths, opt, target) Then Return target
                                             Return Nothing
                                         End Function)

            If String.IsNullOrEmpty(pdfPath) Then
                DiagnosticLogService.LogAlways("Print", $"PDF-Erzeugung fehlgeschlagen (images={paths.Count} pageSize={opt.PageSize})")
                Return False
            End If

            Dim renderMs = watch.ElapsedMilliseconds
            Try
                Process.Start(New ProcessStartInfo() With {
                    .FileName = pdfPath,
                    .UseShellExecute = True
                })
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Print", $"Öffnen fehlgeschlagen: {ex.Message} ({pdfPath})")
                Return False
            End Try

            DiagnosticLogService.LogAlways("Print",
                $"{Path.GetFileName(pdfPath)} images={paths.Count} pages={GetPageCount(paths, opt)} " &
                $"pageSize={opt.PageSize} landscape={opt.Landscape} perPage={opt.ImagesPerPage} render={renderMs}ms")
            Return True
        End Function

    End Class

End Namespace
