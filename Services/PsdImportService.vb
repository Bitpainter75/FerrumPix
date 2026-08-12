Imports System
Imports System.Collections.Generic
Imports System.IO
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Öffnet eine Photoshop-Datei als Ebenenstapel, statt nur ihr fertiges Gesamtbild zu zeigen.
    ''' Das Gegenstück zum Export in <c>ImageProcessor.ExportLayeredPsd</c>.
    '''
    ''' Herauskommt dasselbe Gespann, das auch ein .fpx-Bündel liefert: ein Grundbild als Datei und
    ''' eine Bearbeitung mit Objekt-Ebenen darüber. Der Editor kann damit weiterarbeiten wie mit
    ''' jedem anderen Dokument, ohne dass er von PSD wissen muss.
    '''
    ''' Zwei Entscheidungen stecken darin:
    '''
    ''' Das Grundbild ist NICHT das Gesamtbild der Datei. Läge es unter den Ebenen, wäre jedes
    ''' Bildelement doppelt zu sehen - einmal eingerechnet im Gesamtbild und einmal als Ebene
    ''' darüber. Deckt die unterste Ebene das ganze Dokument, deckend und unverändert gemischt,
    ''' wird SIE das Grundbild; sonst entsteht eine leere Fläche in Dokumentgröße.
    '''
    ''' Was Photoshop nicht als Bildpunkte ablegt - Korrekturebenen, Effekte, Textebenen als solche -
    ''' kommt nicht mit. Es steckt im Gesamtbild der Datei, aber nicht in den Ebenen, und eine leere
    ''' Ebene wäre schlimmer als eine fehlende. Wie viele es waren, steht im Ergebnis.
    ''' </summary>
    Public NotInheritable Class PsdImportService

        Private Sub New()
        End Sub

        Public Class PsdImportResult
            ''' Das entpackte Grundbild als Datei im Temp-Ordner.
            Public Property BaseImagePath As String = ""
            Public Property Adjustments As ImageAdjustments
            ''' Der Temp-Ordner mit Grundbild und Ebenendateien; gehört nach dem Laden dem Editor.
            Public Property TempDir As String = ""
            ''' Wie viele Ebenen die Datei trug, die keine Bildpunkte haben - Korrekturebenen etwa.
            Public Property SkippedLayers As Integer
            Public Property LayerCount As Integer
            ''' <summary>True, wenn die Datei den eigenen Rezeptblock trug und die Bearbeitung
            ''' vollständig zurückkam - Text als Text statt als Bildpunkte.</summary>
            Public Property FromOwnRecipe As Boolean
        End Class

        ''' <summary>Baut aus der Datei ein Dokument mit Ebenen. Nothing, wenn die Datei keine
        ''' Ebenen trägt oder in einer Spielart vorliegt, die der Leser nicht beherrscht - dann
        ''' bleibt der Aufrufer beim gewohnten flachen Gesamtbild.</summary>
        ''' <summary>Wie viele Ebenen der Datei einen Wortlaut tragen. Damit lässt sich vor dem Öffnen
        ''' fragen, ob sie als Text übernommen werden sollen - ohne die Datei zweimal zu laden, denn
        ''' gelesen wird dafür nur das Verzeichnis, nicht die Bildpunkte.</summary>
        Public Shared Function CountTextLayers(psdPath As String) As Integer
            If String.IsNullOrWhiteSpace(psdPath) OrElse Not File.Exists(psdPath) Then Return 0
            ' Trägt die Datei den eigenen Block, ist die Frage gegenstandslos: dann kommt der Text
            ' ohnehin vollständig zurück, mit Schrift und Farbe.
            If PsdRecipeService.ExtractPayload(psdPath) IsNot Nothing Then Return 0

            Dim doc = PsdLayerReader.ReadDocument(psdPath, metadataOnly:=True)
            If doc Is Nothing Then Return 0
            Dim count = 0
            For Each layer In doc.Layers
                If Not String.IsNullOrWhiteSpace(layer.TextContent) Then count += 1
            Next
            Return count
        End Function

        ''' <param name="textAsText">Textebenen als Textobjekte übernehmen statt als Bild. Der
        ''' Wortlaut lässt sich dann weiterbearbeiten, das Aussehen kann aber abweichen - Schrift und
        ''' Grad stehen in der Datei an einer Stelle, die nicht verlässlich zu lesen ist.</param>
        Public Shared Function Import(psdPath As String, Optional textAsText As Boolean = False) As PsdImportResult
            If String.IsNullOrWhiteSpace(psdPath) OrElse Not File.Exists(psdPath) Then Return Nothing
            ' DURCH DIE SCHLEUSE, wie jeder teure Bildweg (siehe DecodeGate). Hier hing sie bisher
            ' nicht, obwohl gerade dieser Weg sie braucht: gelesen werden ALLE Ebenen der Datei -
            ' der Deckel liegt bei 400 Megapixeln Gesamtflaeche - und jede wird danach noch als PNG
            ' geschrieben. Faellt das mit einem RAW-Decode oder einem Gesichtsscan zusammen, teilen
            ' sich zwei Laeufe Kerne, Speicherbandbreite und Platte, und keiner wird schneller.
            ' CountTextLayers bleibt bewusst DRAUSSEN: es liest nur das Verzeichnis, keine
            ' Bildpunkte - dieselbe Abwaegung wie bei TryGetSize.
            Return DecodeGate.Run(Function() ImportIntern(psdPath, textAsText))
        End Function

        ''' <summary>Die eigentliche Arbeit. Getrennt, damit die Schleuse EINE Klammer um alles legt
        ''' und nicht um jeden Abschnitt einzeln.</summary>
        Private Shared Function ImportIntern(psdPath As String, textAsText As Boolean) As PsdImportResult
            Dim doc = PsdLayerReader.ReadDocument(psdPath)
            If doc Is Nothing OrElse doc.Layers.Count = 0 Then Return Nothing
            If doc.Width < 1 OrElse doc.Height < 1 Then Return Nothing

            Dim tempDir = Path.Combine(Path.GetTempPath(), "FerrumPix", "psd", Guid.NewGuid().ToString("N"))
            Directory.CreateDirectory(tempDir)
            Dim success = False

            ' Hat FerrumPix die Datei selbst geschrieben, liegt die Bearbeitung der Objekte bei. Dann
            ' wird SIE genommen und nicht der Umweg über Bildebenen: Text bleibt Text, Formen bleiben
            ' Formen. Der Ebenenstapel darunter wird gar nicht gebraucht - bis auf die unterste
            ' Ebene, die das Grundbild wird.
            Try
                Dim payload = PsdRecipeService.ExtractPayload(psdPath)
                If payload IsNot Nothing Then
                    Dim fromRecipe = PsdRecipeService.Parse(payload, tempDir)
                    ' Auf die Objekte NORMALISIEREN, egal was im Block steht: das Grundbild hier ist
                    ' die fertig durchgerechnete unterste Ebene, alles Globale steckt also schon in
                    ' den Bildpunkten. Fruehe Exporte trugen die VOLLE Bearbeitung im Block - deren
                    ' Regler, Korrekturebenen und Geometrie wirkten beim Wiederoeffnen doppelt.
                    ' Liefert die Normalisierung nichts (aktive Geometrie oder Transparenz im Block,
                    ' siehe BuildPsdRoundtripRecipe), oeffnet die Datei als gewoehnliche Ebenen-PSD.
                    If fromRecipe IsNot Nothing Then fromRecipe = ImageProcessor.BuildPsdRoundtripRecipe(fromRecipe)
                    If fromRecipe IsNot Nothing Then
                        Dim basePath = Path.Combine(tempDir, "base.png")
                        Dim ok = If(IsUsableAsBackground(doc.Layers(0), doc.Width, doc.Height),
                                    WritePng(doc.Layers(0).Pixels, basePath),
                                    WriteEmptyPng(doc.Width, doc.Height, basePath))
                        If ok Then
                            success = True
                            Return New PsdImportResult With {
                                .BaseImagePath = basePath,
                                .Adjustments = fromRecipe,
                                .TempDir = tempDir,
                                .LayerCount = If(fromRecipe.Annotations?.Count, 0),
                                .SkippedLayers = 0,
                                .FromOwnRecipe = True
                            }
                        End If
                    End If
                End If
            Catch
                ' Ein beschädigter eigener Block darf das Öffnen nicht verhindern - dann eben der
                ' gewöhnliche Weg über die Bildebenen darunter.
            Finally
                ' Nur auf dem Erfolgsweg hier freigeben; sonst braucht der gewöhnliche Weg unten die
                ' Bildpunkte noch und räumt selbst auf.
                If success Then
                    For Each layer In doc.Layers
                        layer.Pixels?.Dispose()
                    Next
                End If
            End Try

            Try
                Dim layers = doc.Layers
                Dim adjustments As New ImageAdjustments()
                Dim baseImagePath As String

                ' Deckt die unterste Ebene das ganze Dokument und wird sie unverändert gemischt, ist
                ' sie das Foto - dann wird sie das Grundbild und nicht noch einmal als Objekt gelegt.
                Dim firstIndex = 0
                If IsUsableAsBackground(layers(0), doc.Width, doc.Height) Then
                    baseImagePath = Path.Combine(tempDir, "base.png")
                    If Not WritePng(layers(0).Pixels, baseImagePath) Then Return Nothing
                    firstIndex = 1
                Else
                    baseImagePath = Path.Combine(tempDir, "base.png")
                    If Not WriteEmptyPng(doc.Width, doc.Height, baseImagePath) Then Return Nothing
                End If

                Dim index = 0
                For i = firstIndex To layers.Count - 1
                    Dim layer = layers(i)
                    If layer.Pixels Is Nothing Then Continue For

                    ' Textebene als Textobjekt, wenn der Nutzer es so wollte. Lage und Größe kommen
                    ' aus dem Ebenenrechteck, die Farbe wird aus den gerasterten Bildpunkten
                    ' gemessen, der Schriftgrad aus der Höhe geschätzt. Die Schrift selbst bleibt die
                    ' Vorgabe - was in der Datei dazu steht, ist nicht verlässlich zu lesen.
                    If textAsText AndAlso Not String.IsNullOrWhiteSpace(layer.TextContent) Then
                        adjustments.Annotations.Add(New ImageAnnotation With {
                            .Kind = "Text",
                            .Text = layer.TextContent,
                            .CustomName = If(layer.Name, ""),
                            .XPixels = layer.Left,
                            .YPixels = layer.Top,
                            .WidthPixels = layer.Width,
                            .HeightPixels = layer.Height,
                            .FontSizePixels = EstimateFontSize(layer.Height, layer.TextContent),
                            .FillColor = MeasureDominantColor(layer.Pixels),
                            .Opacity = layer.OpacityPercent,
                            .BlendMode = layer.BlendMode,
                            .ClipToLayerBelow = layer.ClipToLayerBelow,
                            .IsVisible = layer.IsVisible,
                            .LockAspect = False
                        })
                        index += 1
                        Continue For
                    End If

                    Dim assetPath = Path.Combine(tempDir, "layer" & index.ToString() & ".png")
                    If Not WritePng(layer.Pixels, assetPath) Then Continue For
                    index += 1

                    adjustments.Annotations.Add(New ImageAnnotation With {
                        .Kind = "Image",
                        .ImagePath = assetPath,
                        .SourceFileName = If(String.IsNullOrWhiteSpace(layer.Name), "Layer", layer.Name),
                        .CustomName = If(layer.Name, ""),
                        .XPixels = layer.Left,
                        .YPixels = layer.Top,
                        .WidthPixels = layer.Width,
                        .HeightPixels = layer.Height,
                        .Opacity = layer.OpacityPercent,
                        .BlendMode = layer.BlendMode,
                        .ClipToLayerBelow = layer.ClipToLayerBelow,
                        .IsVisible = layer.IsVisible,
                        .LockAspect = False
                    })
                Next

                success = True
                Return New PsdImportResult With {
                    .BaseImagePath = baseImagePath,
                    .Adjustments = adjustments,
                    .TempDir = tempDir,
                    .LayerCount = index,
                    .SkippedLayers = CountSkipped(psdPath, layers.Count)
                }
            Catch
                Return Nothing
            Finally
                For Each layer In doc.Layers
                    layer.Pixels?.Dispose()
                Next
                ' Bei erfolgreichem Laden übernimmt der Editor den Temp-Ordner. Bei jedem Fehler wird
                ' er sofort entfernt, sonst bleiben halbe Entpackungen liegen - dieselbe Regel wie
                ' beim .fpx-Bündel.
                If Not success Then
                    Try
                        Directory.Delete(tempDir, True)
                    Catch
                    End Try
                End If
            End Try
        End Function

        ''' <summary>Die unterste Ebene taugt als Grundbild, wenn sie genau auf dem Dokument liegt,
        ''' sichtbar und voll deckend ist und normal gemischt wird. Sonst ginge beim Übernehmen
        ''' etwas verloren, das nur als Ebene richtig wirkt.</summary>
        Private Shared Function IsUsableAsBackground(layer As PsdLayerReader.PsdLayerInfo,
                                                     docWidth As Integer, docHeight As Integer) As Boolean
            If layer Is Nothing OrElse layer.Pixels Is Nothing Then Return False
            If layer.Left <> 0 OrElse layer.Top <> 0 Then Return False
            If layer.Width <> docWidth OrElse layer.Height <> docHeight Then Return False
            If Not layer.IsVisible Then Return False
            If layer.ClipToLayerBelow Then Return False
            If layer.OpacityPercent < 99.5F Then Return False
            Return String.Equals(layer.BlendMode, "Normal", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Wie viele Ebenen die Datei trägt, von denen keine Bildpunkte ankamen. Gezählt
        ''' wird über den Namen im Verzeichnis, nicht über das Ergebnis: Korrekturebenen und
        ''' Gruppenmarken fallen im Leser heraus, und der Nutzer soll erfahren, dass etwas fehlt.</summary>
        Private Shared Function CountSkipped(psdPath As String, deliveredLayers As Integer) As Integer
            ' Der Leser liefert nur Ebenen mit Bildpunkten. Die Gesamtzahl steht im Verzeichnis der
            ' Datei; die Differenz sind die uebersprungenen. Schlaegt das Zaehlen fehl, wird lieber
            ' 0 gemeldet als eine erfundene Zahl.
            Try
                Dim total = PsdLayerReader.CountLayerRecords(psdPath)
                If total <= 0 Then Return 0
                Return Math.Max(0, total - deliveredLayers)
            Catch
                Return 0
            End Try
        End Function

        ''' <summary>Schätzt den Schriftgrad aus der Höhe des Ebenenrechtecks. Das Rechteck umfasst
        ''' alle Zeilen samt Ober- und Unterlängen; ein Anteil davon je Zeile kommt dem Schriftgrad
        ''' nahe genug, um den Text an seinem Platz stehen zu lassen. Genau ist das nicht und kann es
        ''' nicht sein - der wahre Wert steht in einem Block, der sich nicht verlässlich lesen lässt.</summary>
        Private Shared Function EstimateFontSize(layerHeight As Integer, text As String) As Single
            Dim lines = 1
            If Not String.IsNullOrEmpty(text) Then
                For Each ch In text
                    If ch = ChrW(10) Then lines += 1
                Next
            End If
            Dim perLine = CSng(Math.Max(1, layerHeight)) / Math.Max(1, lines)
            Return Math.Max(6.0F, Math.Min(2000.0F, perLine * 0.78F))
        End Function

        ''' <summary>Die häufigste deckende Farbe der gerasterten Ebene. Für einen einfarbigen Text -
        ''' und das ist er fast immer - trifft das genau; bei einem Farbverlauf im Text kommt die
        ''' vorherrschende Farbe heraus, was allemal besser ist als ein festes Schwarz.</summary>
        Private Shared Function MeasureDominantColor(bmp As SKBitmap) As String
            If bmp Is Nothing Then Return "#FF000000"
            Try
                Dim counts As New Dictionary(Of Integer, Integer)()
                ' Bei großen Ebenen nur jeden n-ten Punkt ansehen; für die Mehrheitsfarbe reicht das
                ' und spart bei einer bildgroßen Ebene Millionen Abfragen.
                Dim stepX = Math.Max(1, bmp.Width \ 200)
                Dim stepY = Math.Max(1, bmp.Height \ 200)
                For y = 0 To bmp.Height - 1 Step stepY
                    For x = 0 To bmp.Width - 1 Step stepX
                        Dim px = bmp.GetPixel(x, y)
                        If px.Alpha < 200 Then Continue For
                        Dim key = (CInt(px.Red) << 16) Or (CInt(px.Green) << 8) Or CInt(px.Blue)
                        Dim n = 0
                        counts.TryGetValue(key, n)
                        counts(key) = n + 1
                    Next
                Next
                If counts.Count = 0 Then Return "#FF000000"

                Dim bestKey = 0
                Dim bestCount = -1
                For Each kv In counts
                    If kv.Value > bestCount Then
                        bestCount = kv.Value
                        bestKey = kv.Key
                    End If
                Next
                Return "#FF" & ((bestKey >> 16) And &HFF).ToString("X2") &
                                ((bestKey >> 8) And &HFF).ToString("X2") &
                                (bestKey And &HFF).ToString("X2")
            Catch
                Return "#FF000000"
            End Try
        End Function

        Private Shared Function WritePng(bmp As SKBitmap, targetPath As String) As Boolean
            If bmp Is Nothing Then Return False
            Using pixmap = bmp.PeekPixels()
                If pixmap Is Nothing Then Return False
                Using encoded = pixmap.Encode(SKEncodedImageFormat.Png, 100)
                    If encoded Is Nothing Then Return False
                    Using fs = File.Create(targetPath)
                        encoded.SaveTo(fs)
                    End Using
                End Using
            End Using
            Return True
        End Function

        ''' <summary>Eine leere, durchsichtige Fläche als Grundbild. Sie ist der Ersatz für das
        ''' Gesamtbild: unter den Ebenen darf nichts liegen, was schon in ihnen steckt.</summary>
        Private Shared Function WriteEmptyPng(width As Integer, height As Integer, targetPath As String) As Boolean
            Using bmp = New SKBitmap(New SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul))
                Using canvas = New SKCanvas(bmp)
                    canvas.Clear(SKColors.Transparent)
                End Using
                Return WritePng(bmp, targetPath)
            End Using
        End Function

    End Class

End Namespace
