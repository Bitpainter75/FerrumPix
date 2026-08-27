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

        ''' <summary>Legt die Bearbeitung aus der Begleitdatei über den frisch eingelesenen
        ''' Ebenenstapel.
        '''
        ''' Beides gehört zusammen und schliesst sich nicht aus: die .psd trägt den INHALT, die
        ''' .fpxmp die BEARBEITUNG. Früher stach die Begleitdatei den Import, und die Datei öffnete
        ''' dann flach - schon eine Bewertung genügte dafür, denn der Katalog legt für sie eine
        ''' Begleitdatei mit neutralem Rezept an.
        '''
        ''' Aus der Begleitdatei kommt alles Globale: Regler, Geometrie, Maskenebenen. Aus der Datei
        ''' kommen die Ebenen. Sie gehören nach UNTEN in den Stapel, denn sie sind der Inhalt des
        ''' Dokuments; was in der Begleitdatei steht, liegt darüber.
        '''
        ''' Eine Begleitdatei kann heute keinen Ebenenstapel tragen (der Editor bietet den Weg für
        ''' eine PSD mit Ebenen gar nicht an), deshalb reicht das Anhängen. Sobald sie Änderungen an
        ''' den Ursprungsebenen festhält, ist HIER die Stelle, an der sie angewandt werden.</summary>
        Public Shared Function MergeSidecarRecipe(imported As ImageAdjustments, sidecar As ImageAdjustments) As ImageAdjustments
            If imported Is Nothing Then Return sidecar
            If sidecar Is Nothing Then Return imported

            Dim merged = sidecar.Clone()
            If merged.Annotations Is Nothing Then merged.Annotations = New List(Of ImageAnnotation)()
            If merged.AnnotationGroups Is Nothing Then merged.AnnotationGroups = New List(Of AnnotationGroup)()
            If merged.Masks Is Nothing Then merged.Masks = New List(Of ImageMask)()

            If imported.Annotations IsNot Nothing Then merged.Annotations.InsertRange(0, imported.Annotations)
            If imported.AnnotationGroups IsNot Nothing Then merged.AnnotationGroups.AddRange(imported.AnnotationGroups)
            If imported.Masks IsNot Nothing Then merged.Masks.AddRange(imported.Masks)
            Return merged
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
                                    WritePng(doc.Layers(0).Pixels, basePath, doc.IccProfile),
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
                        layer.MaskPixels?.Dispose()
                    Next
                    doc.IccProfile?.Dispose()
                    doc.IccProfile = Nothing
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
                    If Not WritePng(layers(0).Pixels, baseImagePath, doc.IccProfile) Then Return Nothing
                    firstIndex = 1
                Else
                    baseImagePath = Path.Combine(tempDir, "base.png")
                    If Not WriteEmptyPng(doc.Width, doc.Height, baseImagePath) Then Return Nothing
                End If

                Dim index = 0
                ' Die offenen Gruppen, von außen nach innen. Die letzte ist die, in der gerade
                ' gelesen wird - dorthin gehört jede Ebene, die jetzt kommt.
                Dim openGroups As New List(Of AnnotationGroup)()

                For i = firstIndex To layers.Count - 1
                    Dim layer = layers(i)

                    If layer.IsGroupMarker Then
                        ApplyGroupMarker(adjustments, layer, openGroups, doc.Width, doc.Height)
                        Continue For
                    End If

                    If layer.Pixels Is Nothing Then Continue For
                    Dim groupId = If(openGroups.Count > 0, openGroups(openGroups.Count - 1).Id, "")

                    ' Textebene als Textobjekt, wenn der Nutzer es so wollte. Lage und Größe kommen
                    ' aus dem Ebenenrechteck, die Farbe wird aus den gerasterten Bildpunkten
                    ' gemessen, der Schriftgrad aus der Höhe geschätzt. Die Schrift selbst bleibt die
                    ' Vorgabe - was in der Datei dazu steht, ist nicht verlässlich zu lesen.
                    ' Die Ebenenmaske wird ein eigener Eintrag im Rezept, auf den die Ebene über ihre
                    ' MaskId zeigt - genauso, wie eine hier gebaute Maske am Objekt hängt. Damit
                    ' bleibt sie nach dem Öffnen mit dem Maskenpinsel änderbar, statt in den
                    ' Bildpunkten festzustecken.
                    Dim maskId = AddLayerMask(adjustments, layer, doc.Width, doc.Height)

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
                            .MaskId = maskId,
                            .GroupId = groupId,
                            .LockAspect = False
                        })
                        index += 1
                        Continue For
                    End If

                    Dim assetPath = Path.Combine(tempDir, "layer" & index.ToString() & ".png")
                    If Not WritePng(layer.Pixels, assetPath, doc.IccProfile) Then Continue For
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
                        .MaskId = maskId,
                        .GroupId = groupId,
                        .LockAspect = False
                    })
                Next

                ' Eine Gruppe ohne ihre Zeile - abgeschnittene Datei, fremder Erzeuger - bliebe sonst
                ' offen, und ihre Mitglieder zeigten auf eine Gruppe, die es nirgends gibt. Lieber
                ' eine namenlose Gruppe im Panel als ein Verweis ins Leere.
                For g = openGroups.Count - 1 To 0 Step -1
                    openGroups(g).ParentGroupId = If(g > 0, openGroups(g - 1).Id, "")
                    adjustments.AnnotationGroups.Add(openGroups(g))
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
                    layer.MaskPixels?.Dispose()
                Next
                ' Das Farbprofil des Dokuments ist ein eigenes natives Objekt und haengt sonst bis
                ' zum naechsten Aufraeumlauf am Speicher - dieselbe Regel wie im flachen Weg.
                doc.IccProfile?.Dispose()
                doc.IccProfile = Nothing
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

        ''' <summary>Verarbeitet eine Gruppenmarke und führt dabei den Stapel der offenen Gruppen
        ''' nach.
        '''
        ''' Die Datei zählt von UNTEN nach oben, und eine Gruppe steht darin verkehrt herum: zuerst
        ''' kommt ihr unteres Ende (Art 3), dann ihr Inhalt, und ganz zuletzt die Zeile mit Namen,
        ''' Deckkraft und Mischmethode (Art 1 oder 2). Deshalb wird bei Art 3 eine noch leere Gruppe
        ''' GEOEFFNET - ihre Kennung braucht schon der erste Inhalt - und bei 1 oder 2 mit allem
        ''' gefüllt, was die Zeile mitbringt.
        '''
        ''' EIN Unterschied bleibt: Photoshop kennt bei Gruppen den Durchgriff als eigene
        ''' Mischmethode, und wer stattdessen ausdrücklich "Normal" wählt, kapselt die Gruppe. Hier
        ''' sind Deckkraft 100 und Normal immer der Durchgriff (siehe <c>AnnotationGroup</c>), beide
        ''' Fälle kommen also gleich an. Sichtbar wird das nur, wenn Mitglieder einer gekapselten
        ''' Gruppe ungewöhnlich gemischt sind.</summary>
        Private Shared Sub ApplyGroupMarker(adjustments As ImageAdjustments,
                                            layer As PsdLayerReader.PsdLayerInfo,
                                            openGroups As List(Of AnnotationGroup),
                                            docWidth As Integer, docHeight As Integer)
            If layer.SectionType = 3 Then
                openGroups.Add(New AnnotationGroup())
                Return
            End If

            ' Eine Gruppenzeile ohne zugehöriges Ende: es gibt nichts zu schließen.
            If openGroups.Count = 0 Then Return

            Dim group = openGroups(openGroups.Count - 1)
            openGroups.RemoveAt(openGroups.Count - 1)

            group.Name = If(String.IsNullOrWhiteSpace(layer.Name),
                            LocalizationService.T("Gruppe"), layer.Name)
            group.IsVisible = layer.IsVisible
            ' Art 2 heißt zugeklappt. Reiner Panel-Zustand, aber einer, den der Nutzer selbst so
            ' gesetzt hat - und ein Dokument mit dreißig aufgeklappten Gruppen ist unbenutzbar.
            group.IsCollapsed = layer.SectionType = 2
            group.Opacity = Math.Max(0.0, Math.Min(100.0, CDbl(layer.OpacityPercent)))
            group.BlendMode = layer.BlendMode
            group.MaskId = AddLayerMask(adjustments, layer, docWidth, docHeight)
            group.ParentGroupId = If(openGroups.Count > 0, openGroups(openGroups.Count - 1).Id, "")
            adjustments.AnnotationGroups.Add(group)
        End Sub

        ''' <summary>Legt die Ebenenmaske als eigenen Maskeneintrag ins Rezept und liefert ihre
        ''' Kennung, oder "" wenn die Ebene keine trägt.
        '''
        ''' Zwei Dinge müssen dabei stimmen, sonst kippt das Bild ins Gegenteil:
        '''
        ''' Die POLARITAET. Photoshop meint mit Weiß "hier ist die Ebene zu sehen", und genau so
        ''' liest der eigene Renderer sein Deckungsraster. Es wird also nichts umgekehrt - was hier
        ''' nach Fleißarbeit aussieht, ist die Stelle, an der ein Vorzeichenfehler jede maskierte
        ''' Ebene zum Negativ ihrer selbst machte.
        '''
        ''' Der VORGABEWERT AUSSERHALB des Maskenrechtecks. Steht dort 255, ist die Ebene überall
        ''' sichtbar, wo die Maske gar nichts sagt - der übliche Fall, wenn jemand nur ein Loch in
        ''' eine Ebene stanzt. Der eigene Maskentyp kennt außerhalb seines Rechtecks nur die Null,
        ''' deshalb wird die Maske dann auf die Ebenenfläche AUFGEZOGEN und der Zuwachs mit 255
        ''' gefüllt. Ohne das verschwände die Ebene bis auf das Loch.</summary>
        Private Shared Function AddLayerMask(adjustments As ImageAdjustments,
                                             layer As PsdLayerReader.PsdLayerInfo,
                                             docWidth As Integer, docHeight As Integer) As String
            If adjustments Is Nothing OrElse layer Is Nothing OrElse Not layer.HasMask Then Return ""

            Try
                ' Zielrechteck: die Maske selbst, bei Vorgabewert 255 zusätzlich die ganze Ebene -
                ' weiter reicht die Wirkung nie, denn außerhalb der Ebene gibt es nichts zu zeigen.
                Dim left = layer.MaskLeft
                Dim top = layer.MaskTop
                Dim right = layer.MaskLeft + layer.MaskWidth
                Dim bottom = layer.MaskTop + layer.MaskHeight
                If layer.MaskDefaultValue > 0 Then
                    left = Math.Min(left, layer.Left)
                    top = Math.Min(top, layer.Top)
                    right = Math.Max(right, layer.Left + layer.Width)
                    bottom = Math.Max(bottom, layer.Top + layer.Height)
                End If

                Dim width = right - left
                Dim height = bottom - top
                If width < 1 OrElse height < 1 Then Return ""

                Dim encoded As String
                Using canvasBitmap = New SKBitmap(New SKImageInfo(width, height, SKColorType.Alpha8, SKAlphaType.Premul))
                    Using canvas = New SKCanvas(canvasBitmap)
                        ' Der Vorgabewert füllt die Fläche, danach setzt sich die Maske darüber. Er
                        ' geht als Deckung in den Alphakanal - bei 0 ist das Füllen ein Löschen, bei
                        ' einem Wert dazwischen deckt die Ebene außerhalb der Maske eben halb.
                        canvas.Clear(New SKColor(0, 0, 0, layer.MaskDefaultValue))
                        ' ERSETZEN, nicht überlagern. Beim üblichen Darüberzeichnen bliebe der
                        ' Vorgabewert überall dort stehen, wo die Maske durchsichtig ist - also
                        ' genau dort, wo sie die Ebene verstecken soll.
                        Using paint = New SKPaint With {.BlendMode = SKBlendMode.Src}
                            canvas.DrawBitmap(layer.MaskPixels,
                                              CSng(layer.MaskLeft - left), CSng(layer.MaskTop - top), paint)
                        End Using
                    End Using
                    Using image = SKImage.FromPixels(canvasBitmap.PeekPixels())
                        Using data = image.Encode(SKEncodedImageFormat.Png, 100)
                            If data Is Nothing Then Return ""
                            encoded = Convert.ToBase64String(data.ToArray())
                        End Using
                    End Using
                End Using

                Dim mask As New ImageMask With {
                    .Name = If(String.IsNullOrWhiteSpace(layer.Name),
                               LocalizationService.T("Ebenenmaske"), layer.Name),
                    .SourceWidthPixels = docWidth,
                    .SourceHeightPixels = docHeight,
                    .Left = left,
                    .Top = top,
                    .Right = right,
                    .Bottom = bottom,
                    .PngBase64 = encoded,
                    .IsDisabled = layer.MaskDisabled
                }
                adjustments.Masks.Add(mask)
                Return mask.Id
            Catch
                ' Eine Maske, die sich nicht übernehmen lässt, kostet die Maske und nicht die Ebene.
                Return ""
            End Try
        End Function

        ''' <summary>Die unterste Ebene taugt als Grundbild, wenn sie genau auf dem Dokument liegt,
        ''' sichtbar und voll deckend ist und normal gemischt wird. Sonst ginge beim Übernehmen
        ''' etwas verloren, das nur als Ebene richtig wirkt.</summary>
        Private Shared Function IsUsableAsBackground(layer As PsdLayerReader.PsdLayerInfo,
                                                     docWidth As Integer, docHeight As Integer) As Boolean
            If layer Is Nothing OrElse layer.Pixels Is Nothing Then Return False
            ' Eine maskierte Ebene taugt NIE als Grundbild: das Grundbild ist eine schlichte
            ' Bilddatei und hat keinen Platz für eine Maske - sie fiele beim Übernehmen weg, und die
            ' Ebene wäre plötzlich überall zu sehen.
            If layer.HasMask Then Return False
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

        ''' <param name="profile">Das Farbprofil des DOKUMENTS. Es gilt fuer jede Ebene darin, und
        ''' gewandelt wird genau hier: die Ebene geht als PNG nach draussen, und ein Profil ginge
        ''' dabei verloren, bevor es jemand sehen koennte - derselbe Grund wie beim Gesamtbild.
        ''' Nothing oder sRGB heisst: nichts zu tun, kein Kopieren, keine Rechnung.</param>
        Private Shared Function WritePng(bmp As SKBitmap, targetPath As String,
                                         Optional profile As SKColorSpace = Nothing) As Boolean
            If bmp Is Nothing Then Return False
            Dim managed = ColorManagementService.ToSrgb(bmp, profile)
            Try
                Return WritePngCore(managed, targetPath)
            Finally
                If Not Object.ReferenceEquals(managed, bmp) Then managed?.Dispose()
            End Try
        End Function

        Private Shared Function WritePngCore(bmp As SKBitmap, targetPath As String) As Boolean
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
