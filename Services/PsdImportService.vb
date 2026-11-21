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
    ''' Ebene wäre schlimmer als eine fehlende. Wie viele es waren, steht im Ergebnis; Ebenen mit
    ''' Effekten und unlesbare Ebenen eigens, weil bei ihnen das Gesamtbild die bessere Wahl sein
    ''' kann und der Editor danach fragt.
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
            ''' <summary>Sichtbare Ebenen und Gruppen mit eingeschalteten Ebeneneffekten. Sie
            ''' kommen ohne ihre Effekte an; das Gesamtbild der Datei zeigt sie richtig.</summary>
            Public Property EffectLayers As Integer
            ''' <summary>Ebenen, deren Bildpunkte sich nicht lesen ließen. Sie fehlen im Stapel.</summary>
            Public Property BrokenLayers As Integer
            Public Property AdjustmentLayers As Integer
            ''' <summary>Sichtbare Füll-, Form- oder Smartobjekt-Ebenen ohne Bildpunkte in der Datei
            ''' (<see cref="PsdLayerReader.PsdDocumentInfo.MissingContentLayers"/>). Sie fehlen im
            ''' Stapel; das Gesamtbild der Datei zeigt sie.</summary>
            Public Property MissingContentLayers As Integer
            ''' <summary>Textebenen ohne sichtbaren Punkt, die nicht als Text kamen (Wahl "als
            ''' Bild", oder verzerrt) und deshalb fehlen; als Bild gäbe es von ihnen nichts.</summary>
            Public Property DroppedTextLayers As Integer
            ''' <summary>Textebenen mit Bildpunkten, die gestreckt, geschert oder gespiegelt sind
            ''' (<see cref="IsDistortedText"/>) und deshalb als
            ''' Bild kamen, unabhängig von der Wahl; ob das einen Hinweis wert ist, entscheidet der
            ''' Aufrufer, der weiß, was gewählt wurde.</summary>
            Public Property StretchedTextLayers As Integer
            ''' <summary>Textebenen mit Bildpunkten, die ein Textobjekt nicht treu trägt: Textrahmen,
            ''' senkrechter Satz, mehrere Stilläufe oder ein Merkmal ohne Gegenstück
            ''' (<see cref="UnsupportedTextReason"/>). Sie kommen als Bild, unabhängig von der Wahl;
            ''' wie bei <see cref="StretchedTextLayers"/> entscheidet der Aufrufer über den Hinweis.</summary>
            Public Property UnsupportedTextLayers As Integer
            ''' <summary>Die Schriften der Textebenen, die als Text kamen und deren Schrift auf dem
            ''' Rechner fehlt (<see cref="FindInstalledFamily"/>): je Familie ein Eintrag mit der Zahl
            ''' der Ebenen, in der Reihenfolge des Stapels. Der Text bleibt Text und wird mit
            ''' <see cref="FallbackFontFamily"/> gesetzt; ob das einen Hinweis wert ist, entscheidet
            ''' der Aufrufer. Beim eigenen Rezeptblock zählt <see cref="FindMissingFonts"/> die
            ''' Textobjekte des Blocks.</summary>
            Public Property MissingFonts As New List(Of MissingFont)()
            ''' <summary>Die Schrift, mit der der Text fehlender Schriften tatsächlich gesetzt wird:
            ''' bei fremden Textebenen der Familienname des Schnitts, den das Zeichnen für die
            ''' Vorgabeschrift des Textobjekts bekommt; beim eigenen Rezeptblock die Ersatzschriften
            ''' je fehlender Familie (<see cref="MissingFont.FallbackFamily"/>). Leer, wenn keine
            ''' Schrift fehlt.</summary>
            Public Property FallbackFontFamily As String = ""
            ''' <summary>True, wenn die Datei den eigenen Rezeptblock trug und die Bearbeitung
            ''' vollständig zurückkam - Text als Text statt als Bildpunkte.</summary>
            Public Property FromOwnRecipe As Boolean
        End Class

        ''' <summary>Eine Schrift, die auf dem Rechner fehlt, und wie viele Textebenen sie nennen.
        ''' Dieselbe Form für fremde Textebenen einer PSD und für die Textobjekte eines Rezepts
        ''' (.fpx, eigener Rezeptblock einer PSD).</summary>
        Public Class MissingFont
            ''' <summary>Der Name der Familie, wie er dem Anwender etwas sagt ("Myriad Pro"): bei
            ''' einer fremden Textebene aus dem PostScript-Namen der Datei abgeleitet
            ''' (<see cref="DisplayFamilyName"/>), bei einem Textobjekt sein gespeicherter
            ''' Familienname.</summary>
            Public Property FamilyName As String = ""
            ''' <summary>Wie viele Textebenen oder Textobjekte diese Schrift nennen, auch die in
            ''' Gruppen.</summary>
            Public Property LayerCount As Integer
            ''' <summary>Die Schrift, mit der dieser Text tatsächlich gesetzt wird
            ''' (<see cref="ImageProcessor.ResolveTextFamilyName"/>).</summary>
            Public Property FallbackFamily As String = ""
        End Class

        ''' <summary>Wie viele Ebenen der Datei einen Wortlaut tragen. Damit lässt sich vor dem Öffnen
        ''' fragen, ob sie als Text übernommen werden sollen - ohne die Datei zweimal zu laden, denn
        ''' gelesen wird dafür nur das Verzeichnis, nicht die Bildpunkte.</summary>
        Public Shared Function CountTextLayers(psdPath As String) As Integer
            If String.IsNullOrWhiteSpace(psdPath) OrElse Not File.Exists(psdPath) Then Return 0

            Dim doc = PsdLayerReader.ReadDocument(psdPath, metadataOnly:=True)
            If doc Is Nothing Then Return 0
            ' Trägt die Datei den eigenen Block, ist die Frage gegenstandslos: dann kommt der Text
            ' ohnehin vollständig zurück, mit Schrift und Farbe.
            If doc.HasRecipeBlock Then Return 0
            Dim count = 0
            For Each layer In doc.Layers
                ' Verzerrter und nicht treu abbildbarer Text kommt ohnehin als Bild, nach ihm muss
                ' niemand gefragt werden.
                If Not String.IsNullOrWhiteSpace(layer.TextContent) AndAlso Not IsDistortedText(layer.TextStyle) AndAlso
                   UnsupportedTextReason(layer.TextStyle, layer.TextContent).Length = 0 Then count += 1
            Next
            Return count
        End Function

        ''' <summary>Baut aus der Datei ein Dokument mit Ebenen. Nothing, wenn die Datei keine
        ''' Ebenen trägt oder in einer Spielart vorliegt, die der Leser nicht beherrscht - dann
        ''' bleibt der Aufrufer beim gewohnten flachen Gesamtbild.</summary>
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
            ' Die Sperren der untersten Ebene gehören wie die Ebenen der Datei.
            merged.BaseLayerLockPosition = imported.BaseLayerLockPosition
            merged.BaseLayerLockTransparency = imported.BaseLayerLockTransparency
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
                ' Mitgelesen beim Durchlauf der Ressourcen, statt die Datei eigens zu öffnen.
                Dim payload = doc.RecipePayload
                doc.RecipePayload = Nothing
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
                        Dim asBackground = IsUsableAsBackground(doc.Layers(0), doc.Width, doc.Height)
                        Dim ok = If(asBackground,
                                    WritePng(doc.Layers(0).Pixels, basePath, doc.IccProfile),
                                    WriteEmptyPng(doc.Width, doc.Height, basePath))
                        ' Die Sperren stehen in der Datei, nicht im Block: wer die unterste Ebene
                        ' danach anderswo gesperrt hat, soll das hier wiederfinden.
                        If asBackground Then
                            fromRecipe.BaseLayerLockPosition = doc.Layers(0).LockPosition
                            fromRecipe.BaseLayerLockTransparency = doc.Layers(0).LockTransparency
                        End If
                        If ok Then
                            success = True
                            ' Text aus dem Block behält seine Familie, auch wenn sie hier fehlt;
                            ' gesetzt wird er dann mit der Ersatzschrift des Systems. Gezählt wie
                            ' bei einem .fpx, gemeldet vom Aufrufer.
                            Dim recipeMissingFonts = FindMissingFonts(fromRecipe.Annotations)
                            LogMissingFonts("PsdImport.MissingFonts", psdPath, recipeMissingFonts)
                            Return New PsdImportResult With {
                                .BaseImagePath = basePath,
                                .Adjustments = fromRecipe,
                                .TempDir = tempDir,
                                .LayerCount = If(fromRecipe.Annotations?.Count, 0),
                                .SkippedLayers = 0,
                                .FromOwnRecipe = True,
                                .MissingFonts = recipeMissingFonts,
                                .FallbackFontFamily = String.Join(", ", recipeMissingFonts.Select(Function(m) m.FallbackFamily).Distinct(StringComparer.OrdinalIgnoreCase))
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
                    ' Das Grundbild kennt keine Sperren; sie reisen im Rezept mit, damit der
                    ' Export sie der untersten Ebene zurückgibt.
                    adjustments.BaseLayerLockPosition = layers(0).LockPosition
                    adjustments.BaseLayerLockTransparency = layers(0).LockTransparency
                Else
                    baseImagePath = Path.Combine(tempDir, "base.png")
                    If Not WriteEmptyPng(doc.Width, doc.Height, baseImagePath) Then Return Nothing
                End If

                Dim index = 0
                Dim droppedTextLayers = 0
                Dim stretchedTextLayers = 0
                Dim unsupportedTextLayers = 0
                ' Fehlende Schriften der Ebenen, die als Text kommen, je Familie einmal gezählt.
                Dim missingFonts As New List(Of MissingFont)()
                ' Die offenen Gruppen, von außen nach innen. Die letzte ist die, in der gerade
                ' gelesen wird - dorthin gehört jede Ebene, die jetzt kommt.
                Dim openGroups As New List(Of AnnotationGroup)()

                For i = firstIndex To layers.Count - 1
                    Dim layer = layers(i)

                    If layer.IsGroupMarker Then
                        ApplyGroupMarker(adjustments, layer, openGroups, doc.Width, doc.Height)
                        Continue For
                    End If

                    ' Ohne Bildpunkte bleibt eine leere Textebene nur als Text: als Bild gäbe es
                    ' nichts zu zeigen. Kommt sie nicht als Text (Wahl "als Bild", oder verzerrt),
                    ' fehlt sie - gezählt, damit die Rückfrage beim Öffnen es sagt. Eine gewöhnliche
                    ' leere Bildebene bleibt dagegen; sie wird unten eine leere Malebene.
                    Dim hasText = Not String.IsNullOrWhiteSpace(layer.TextContent)
                    Dim distorted = hasText AndAlso IsDistortedText(layer.TextStyle)
                    Dim unsupported = hasText AndAlso Not distorted AndAlso
                                      UnsupportedTextReason(layer.TextStyle, layer.TextContent).Length > 0
                    Dim asText = textAsText AndAlso hasText AndAlso Not distorted AndAlso Not unsupported
                    If layer.Pixels Is Nothing AndAlso Not asText AndAlso Not layer.IsEmptyLayer Then
                        If hasText Then droppedTextLayers += 1
                        Continue For
                    End If
                    ' Verzerrter und nicht treu abbildbarer Text kommt als Bild, auch wenn Text als
                    ' Text gewählt ist; gezählt, damit der Editor sagen kann, warum dieser Wortlaut
                    ' fest ist. Die Bildpunkte sind die, die Photoshop gesetzt hat.
                    If distorted Then stretchedTextLayers += 1
                    If unsupported Then unsupportedTextLayers += 1
                    Dim groupId = If(openGroups.Count > 0, openGroups(openGroups.Count - 1).Id, "")

                    ' Die leere Malebene deckt das ganze Bild, wie eine im Editor angelegte. Ihre
                    ' Maske wird deshalb auf das ganze Bild aufgezogen (AddLayerMask zieht sie auf
                    ' die Ebenenfläche): wer später darauf malt, soll ausserhalb des alten
                    ' Maskenrechtecks nicht ins Leere malen.
                    Dim emptyLayer = layer.Pixels Is Nothing AndAlso Not asText AndAlso layer.IsEmptyLayer
                    If emptyLayer Then
                        layer.Left = 0
                        layer.Top = 0
                        layer.Width = doc.Width
                        layer.Height = doc.Height
                    End If

                    ' Textebene als Textobjekt, wenn der Nutzer es so wollte. Fehlende Schriftwerte
                    ' werden aus dem Ebenenrechteck und den gerasterten Bildpunkten geschätzt.
                    ' Die Ebenenmaske wird ein eigener Eintrag im Rezept, auf den die Ebene über ihre
                    ' MaskId zeigt - genauso, wie eine hier gebaute Maske am Objekt hängt. Damit
                    ' bleibt sie nach dem Öffnen mit dem Maskenpinsel änderbar, statt in den
                    ' Bildpunkten festzustecken.
                    Dim maskId = AddLayerMask(adjustments, layer, doc.Width, doc.Height)

                    If asText Then
                        Dim style = layer.TextStyle
                        ' Eine leere Ebene hat kein Rechteck; Lage und Groesse setzt dann der
                        ' Rahmen aus TySh (ApplyPsdTextLayout), bis dahin wenigstens ein Punkt.
                        Dim annotation As New ImageAnnotation With {
                            .Kind = "Text",
                            .Text = layer.TextContent,
                            .CustomName = If(layer.Name, ""),
                            .XPixels = layer.Left,
                            .YPixels = layer.Top,
                            .WidthPixels = Math.Max(1, layer.Width),
                            .HeightPixels = Math.Max(1, layer.Height),
                            .FontSizePixels = If(style IsNot Nothing AndAlso style.FontSize > 0 AndAlso style.FontSize <= 100_000, CSng(style.FontSize), EstimateFontSize(layer.Height, layer.TextContent)),
                            .FillColor = If(style IsNot Nothing AndAlso Not String.IsNullOrEmpty(style.FillColor), style.FillColor, MeasureDominantColor(layer.Pixels)),
                            .Opacity = layer.OpacityPercent,
                            .BlendMode = layer.BlendMode,
                            .ClipToLayerBelow = layer.ClipToLayerBelow,
                            .IsVisible = layer.IsVisible,
                            .MaskId = maskId,
                            .GroupId = groupId,
                            .LockAspect = False,
                            .IsLocked = layer.LockPosition,
                            .LockTransparentPixels = layer.LockTransparency
                        }
                        If style IsNot Nothing Then
                            annotation.AllCaps = style.AllCaps
                            CountMissingFont(missingFonts, ApplyPsdFont(annotation, style.FontName))
                            ApplyPsdTextLayout(annotation, style)
                            ApplyPsdLineSpacing(annotation, style)
                        End If
                        adjustments.Annotations.Add(annotation)
                        index += 1
                        Continue For
                    End If

                    Dim assetPath = Path.Combine(tempDir, "layer" & index.ToString() & ".png")
                    If emptyLayer Then
                        ' Dieselbe Gestalt wie eine im Editor angelegte Malebene (AddPaintLayer):
                        ' ein durchsichtiges Raster in Bildgröße, Art SelectionImage, der Name in
                        ' Text. Malen, Radieren und ein erneuter Export gelten damit unverändert.
                        If Not WriteEmptyPng(doc.Width, doc.Height, assetPath) Then Continue For
                        index += 1
                        adjustments.Annotations.Add(New ImageAnnotation With {
                            .Kind = "SelectionImage",
                            .IsPaintLayer = True,
                            .Text = If(String.IsNullOrWhiteSpace(layer.Name), "Layer", layer.Name),
                            .CustomName = If(layer.Name, ""),
                            .ImagePath = assetPath,
                            .XPixels = 0,
                            .YPixels = 0,
                            .WidthPixels = doc.Width,
                            .HeightPixels = doc.Height,
                            .FillColor = "#00FFFFFF",
                            .Opacity = layer.OpacityPercent,
                            .BlendMode = layer.BlendMode,
                            .ClipToLayerBelow = layer.ClipToLayerBelow,
                            .IsVisible = layer.IsVisible,
                            .MaskId = maskId,
                            .GroupId = groupId,
                            .LockAspect = False,
                            .IsLocked = layer.LockPosition,
                            .LockTransparentPixels = layer.LockTransparency
                        })
                        Continue For
                    End If
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
                        .LockAspect = False,
                        .IsLocked = layer.LockPosition,
                        .LockTransparentPixels = layer.LockTransparency
                    })
                Next

                ' Eine Gruppe ohne ihre Zeile - abgeschnittene Datei, fremder Erzeuger - bliebe sonst
                ' offen, und ihre Mitglieder zeigten auf eine Gruppe, die es nirgends gibt. Lieber
                ' eine namenlose Gruppe im Panel als ein Verweis ins Leere.
                For g = openGroups.Count - 1 To 0 Step -1
                    openGroups(g).ParentGroupId = If(g > 0, openGroups(g - 1).Id, "")
                    adjustments.AnnotationGroups.Add(openGroups(g))
                Next

                ' Mitgezählt wird auch die unterste Ebene, wenn sie das Grundbild wurde, und jede
                ' Gruppenzeile: ihre Effekte fehlen genauso.
                Dim effectLayers = 0
                For Each layer In layers
                    If layer.HasEffects AndAlso layer.IsVisible AndAlso layer.SectionType <> 3 Then effectLayers += 1
                Next

                ' Womit der Text fehlender Schriften wirklich gesetzt wird: die Vorgabeschrift des
                ' Textobjekts, so wie das Zeichnen sie auflöst. Fehlt auch sie (Arial unter Linux),
                ' nennt das die Ersatzschrift des Systems statt eines Namens, der nirgends steht.
                Dim fallbackFamily = ""
                If missingFonts.Count > 0 Then
                    fallbackFamily = ImageProcessor.ResolveTextFamilyName(New ImageAnnotation().FontFamily)
                    For Each missing In missingFonts
                        missing.FallbackFamily = fallbackFamily
                    Next
                    LogMissingFonts("PsdImport.MissingFonts", psdPath, missingFonts)
                End If

                success = True
                Return New PsdImportResult With {
                    .BaseImagePath = baseImagePath,
                    .Adjustments = adjustments,
                    .TempDir = tempDir,
                    .LayerCount = index,
                    .SkippedLayers = CountSkipped(doc.LayerRecordCount, layers.Count) + droppedTextLayers,
                    .EffectLayers = effectLayers,
                    .BrokenLayers = doc.BrokenLayers,
                    .AdjustmentLayers = doc.AdjustmentLayers,
                    .MissingContentLayers = doc.MissingContentLayers,
                    .DroppedTextLayers = droppedTextLayers,
                    .StretchedTextLayers = stretchedTextLayers,
                    .UnsupportedTextLayers = unsupportedTextLayers,
                    .MissingFonts = missingFonts,
                    .FallbackFontFamily = fallbackFamily
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
            group.IsLocked = layer.LockPosition
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
                ' Eine leere Ebene (Textebene ohne sichtbaren Punkt) hat kein Rechteck; ihr
                ' Nullrechteck zöge die Maske sonst bis an die linke obere Bildecke auf.
                If layer.MaskDefaultValue > 0 AndAlso layer.Width > 0 AndAlso layer.Height > 0 Then
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
                    Using data = PngEncoder.Encode(canvasBitmap, PngPurpose.Stored)
                        If data Is Nothing Then Return ""
                        encoded = Convert.ToBase64String(data.ToArray())
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
                    .FeatherPixels = layer.MaskFeatherPixels,
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
            ' Sperren hindern NICHT: Photoshops eigene Hintergrundebene gilt immer als gesperrt und
            ' ist von einer gewöhnlichen gesperrten Ebene nicht sicher zu unterscheiden. Sie wird
            ' Grundbild, und ihre Sperren reisen im Rezept mit (BaseLayerLockPosition), damit der
            ' Export sie zurückschreibt.
            Return String.Equals(layer.BlendMode, "Normal", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Wie viele Ebenen die Datei trägt, von denen keine Bildpunkte ankamen. Gezählt
        ''' wird über den Namen im Verzeichnis, nicht über das Ergebnis: Korrekturebenen und
        ''' Gruppenmarken fallen im Leser heraus, und der Nutzer soll erfahren, dass etwas fehlt.</summary>
        Private Shared Function CountSkipped(totalRecords As Integer, deliveredLayers As Integer) As Integer
            ' Der Leser liefert nur Ebenen mit Bildpunkten. Die Gesamtzahl steht im Verzeichnis der
            ' Datei und kommt aus demselben Lesedurchgang; die Differenz sind die uebersprungenen.
            ' Fehlt sie, wird lieber 0 gemeldet als eine erfundene Zahl.
            If totalRecords <= 0 Then Return 0
            Return Math.Max(0, totalRecords - deliveredLayers)
        End Function

        ''' <summary>Schrift und Schnitt aus dem PostScript-Namen der Textebene. Gibt "" zurück, wenn
        ''' die Schrift da ist oder die Datei keine nennt; fehlt sie, den Namen ihrer Familie
        ''' (<see cref="DisplayFamilyName"/>). Dann behält das Textobjekt seine Vorgabeschrift, und
        ''' der Aufrufer zählt die Lücke (<see cref="PsdImportResult.MissingFonts"/>).</summary>
        Private Shared Function ApplyPsdFont(annotation As ImageAnnotation, postscriptName As String) As String
            If String.IsNullOrWhiteSpace(postscriptName) Then Return ""
            Dim family = FindInstalledFamily(postscriptName)
            If family Is Nothing Then Return DisplayFamilyName(postscriptName)
            annotation.FontFamily = family
            Dim parts = postscriptName.Split("-"c, 2)
            If parts.Length > 1 Then
                annotation.Bold = parts(1).IndexOf("bold", StringComparison.OrdinalIgnoreCase) >= 0
                annotation.Italic = parts(1).IndexOf("italic", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
                                    parts(1).IndexOf("oblique", StringComparison.OrdinalIgnoreCase) >= 0
            End If
            Return ""
        End Function

        ''' <summary>Die installierte Familie zum PostScript-Namen einer Textebene, oder Nothing, wenn
        ''' die Schrift auf dem Rechner fehlt.
        '''
        ''' Gefragt wird zuerst die Liste der installierten Familien, nicht
        ''' <c>SKTypeface.FromFamilyName</c> oder <c>SKFontManager.MatchFamily</c>: beide liefern für
        ''' eine fehlende Familie gern eine Ersatzschrift (über Fontconfig etwa Noto Sans für
        ''' "Roboto"), und an ihrem blossen Dasein ist die Lücke nicht zu erkennen. Photoshop nennt
        ''' die Schrift mit dem PostScript-Namen ("Roboto-Regular", "MyriadPro-Bold"); die Familie
        ''' ist der Teil vor dem Bindestrich, ohne Leerzeichen verglichen ("MyriadPro" gegen
        ''' "Myriad Pro"), notfalls ohne Herstellerendung.
        '''
        ''' Erst danach der PostScript-Name selbst, für eine Familie, deren Name anders lautet als
        ''' der Anfang ihres PostScript-Namens. Dort zählt der gelieferte Schnitt nur, wenn ER genau
        ''' diesen Namen trägt, nicht weil überhaupt einer kam.
        '''
        ''' Mit <paramref name="isPostScriptName"/> = False ist der Name ein Familienname, wie ihn
        ''' ein Textobjekt im Rezept trägt ("Noto Sans", auch mit Bindestrich im Namen). Dann
        ''' zählt allein die Liste der installierten Familien: kein Schnitt am Bindestrich, keine
        ''' Herstellerendung, kein Rückgriff auf den PostScript-Namen.
        '''
        ''' In beiden Fällen gilt zuletzt eine maßgleiche Ersatzschrift als vorhanden
        ''' (<see cref="FindMetricCompatibleFamily"/>); zurück kommt dann der gefragte Name, nicht
        ''' der des Ersatzes.</summary>
        Public Shared Function FindInstalledFamily(fontName As String, Optional isPostScriptName As Boolean = True) As String
            If String.IsNullOrWhiteSpace(fontName) Then Return Nothing
            Dim name = fontName.Trim()
            Dim families = SKFontManager.Default.FontFamilies
            Dim resolve As Func(Of String, String) = AddressOf ImageProcessor.ResolveTextFamilyName
            If Not isPostScriptName Then
                Dim plainKey = name.Replace(" ", "")
                Return If(FindFamily(families, plainKey), FindMetricCompatibleFamily(plainKey, families, resolve))
            End If
            Dim familyKey = name.Split("-"c, 2)(0).Replace(" ", "")
            Dim family = FindFamily(families, familyKey)
            ' Manche Schriften tragen ihren Hersteller als Endung im PostScript-Namen: "ArialMT",
            ' "TimesNewRomanPSMT", "TimesNewRomanPS-BoldMT". Die Familie heisst ohne diese Endung;
            ' ohne den zweiten Versuch kam Text in Times New Roman in der Vorgabeschrift.
            Dim keys As New List(Of String) From {familyKey}
            For Each suffix In {"PSMT", "MT", "PS"}
                If familyKey.Length > suffix.Length AndAlso familyKey.EndsWith(suffix, StringComparison.Ordinal) Then
                    keys.Add(familyKey.Substring(0, familyKey.Length - suffix.Length))
                End If
            Next
            If family Is Nothing Then
                For Each key In keys.Skip(1)
                    family = FindFamily(families, key)
                    If family IsNot Nothing Then Exit For
                Next
            End If
            If family IsNot Nothing Then Return family
            ' Nicht freigeben: Skia gibt für denselben Schnitt dieselbe verwaltete Hülle heraus, die
            ' dann womöglich im Schriftzwischenspeicher des Zeichnens steckt.
            Dim byName = SKTypeface.FromFamilyName(name)
            If byName IsNot Nothing AndAlso Not String.IsNullOrEmpty(byName.FamilyName) AndAlso
               String.Equals(byName.PostScriptName, name, StringComparison.OrdinalIgnoreCase) Then
                Return If(FindFamily(families, byName.FamilyName.Replace(" ", "")), byName.FamilyName)
            End If
            For Each key In keys
                Dim compatible = FindMetricCompatibleFamily(key, families, resolve)
                If compatible IsNot Nothing Then Return compatible
            Next
            Return Nothing
        End Function

        ''' <summary>Die drei Schriften, für die es einen maßgleichen freien Ersatz gibt, und dieser
        ''' Ersatz. Liberation Sans, Serif und Mono sind ausdrücklich so gebaut, dass jedes Zeichen
        ''' dieselbe Vorschubbreite hat wie in Arial, Times New Roman und Courier New; Arimo, Tinos
        ''' und Cousine sind dieselben Entwürfe unter dem Namen, den ChromeOS verwendet (fontconfig
        ''' kennt beide als Alias für dieselben drei Familien). Zeilen umbrechen deshalb an derselben
        ''' Stelle, und der Text passt in denselben Rahmen; nur die Formen der Zeichen weichen ab.
        '''
        ''' Bewusst eine feste Liste und keine Ähnlichkeitsregel: maßgleich ist eine Zusage der
        ''' Schrift, keine Eigenschaft, die sich am Namen oder an einer Ähnlichkeit ablesen ließe.
        ''' Helvetica gehört nicht dazu, auch wenn fontconfig sie oft auf Liberation Sans lenkt;
        ''' belegt ist die Maßgleichheit nur für diese drei Paare.</summary>
        Private Shared ReadOnly MetricCompatibleFamilies As New Dictionary(Of String, String())(StringComparer.OrdinalIgnoreCase) From {
            {"Arial", {"Liberation Sans", "Arimo"}},
            {"Times New Roman", {"Liberation Serif", "Tinos"}},
            {"Courier New", {"Liberation Mono", "Cousine"}}
        }

        ''' <summary>Der gefragte Familienname, wenn die Familie selbst fehlt, ihr maßgleicher Ersatz
        ''' (<see cref="MetricCompatibleFamilies"/>) aber installiert ist UND das Zeichnen für den
        ''' gefragten Namen tatsächlich diesen Ersatz bekommt; sonst Nothing. Ohne die zweite
        ''' Bedingung wäre der Ersatz zwar da, gesetzt würde aber mit etwas anderem, und das muss
        ''' weiter gemeldet werden.
        '''
        ''' <paramref name="familyKey"/> ist der Name ohne Leerzeichen ("TimesNewRoman"),
        ''' <paramref name="installedFamilies"/> die Liste der installierten Familien,
        ''' <paramref name="resolveFamily"/> liefert zum Namen die Familie, mit der das Zeichnen
        ''' setzt (in der Anwendung <see cref="ImageProcessor.ResolveTextFamilyName"/>). Beides ist
        ''' übergeben, damit sich die Regel auch auf einem Rechner prüfen lässt, auf dem Arial
        ''' installiert ist.</summary>
        Public Shared Function FindMetricCompatibleFamily(familyKey As String, installedFamilies As IEnumerable(Of String),
                                                          resolveFamily As Func(Of String, String)) As String
            If String.IsNullOrWhiteSpace(familyKey) OrElse installedFamilies Is Nothing OrElse resolveFamily Is Nothing Then Return Nothing
            For Each pair In MetricCompatibleFamilies
                If Not String.Equals(pair.Key.Replace(" ", ""), familyKey.Replace(" ", ""), StringComparison.OrdinalIgnoreCase) Then Continue For
                Dim resolved = If(resolveFamily(pair.Key), "").Trim()
                If resolved.Length = 0 Then Return Nothing
                Dim isSubstitute = pair.Value.Any(Function(s) String.Equals(s.Replace(" ", ""), resolved.Replace(" ", ""), StringComparison.OrdinalIgnoreCase))
                If isSubstitute AndAlso FindFamily(installedFamilies, resolved.Replace(" ", "")) IsNot Nothing Then Return pair.Key
                Return Nothing
            Next
            Return Nothing
        End Function

        ''' <summary>Der Name der Familie, wie er dem Anwender etwas sagt, aus dem PostScript-Namen:
        ''' der Teil vor dem Bindestrich, ohne Herstellerendung, mit Leerzeichen an den Wortgrenzen
        ''' ("MyriadPro-Bold" wird "Myriad Pro", "TimesNewRomanPS-BoldMT" wird "Times New Roman").
        ''' Nur für die Meldung; gesucht wird mit <see cref="FindInstalledFamily"/>.</summary>
        Public Shared Function DisplayFamilyName(postscriptName As String) As String
            Dim key = If(postscriptName, "").Trim().Split("-"c, 2)(0).Trim()
            For Each suffix In {"PSMT", "MT", "PS"}
                ' Nur hinter einem Kleinbuchstaben: "ArialMT" ja, eine Abkürzung in Versalien nein.
                If key.Length > suffix.Length AndAlso key.EndsWith(suffix, StringComparison.Ordinal) AndAlso
                   Char.IsLower(key(key.Length - suffix.Length - 1)) Then
                    key = key.Substring(0, key.Length - suffix.Length)
                    Exit For
                End If
            Next
            Dim builder As New Text.StringBuilder()
            For i = 0 To key.Length - 1
                Dim c = key(i)
                If i > 0 AndAlso Char.IsUpper(c) AndAlso
                   (Char.IsLower(key(i - 1)) OrElse Char.IsDigit(key(i - 1)) OrElse
                    (Char.IsUpper(key(i - 1)) AndAlso i + 1 < key.Length AndAlso Char.IsLower(key(i + 1)))) Then
                    builder.Append(" "c)
                End If
                builder.Append(c)
            Next
            Return If(builder.Length > 0, builder.ToString(), If(postscriptName, "").Trim())
        End Function

        ''' <summary>Zählt eine fehlende Schrift je Familie einmal; "" zählt nicht.</summary>
        Private Shared Sub CountMissingFont(missingFonts As List(Of MissingFont), familyName As String)
            If String.IsNullOrEmpty(familyName) Then Return
            For Each entry In missingFonts
                If String.Equals(entry.FamilyName, familyName, StringComparison.OrdinalIgnoreCase) Then
                    entry.LayerCount += 1
                    Return
                End If
            Next
            missingFonts.Add(New MissingFont With {.FamilyName = familyName, .LayerCount = 1})
        End Sub

        ''' <summary>Die Schriften der Textobjekte eines Rezepts, die auf dem Rechner fehlen: je
        ''' Familie ein Eintrag mit der Zahl der Objekte, in der Reihenfolge des Stapels, und der
        ''' Ersatzschrift, mit der das Zeichnen sie tatsächlich setzt. Für .fpx und den eigenen
        ''' Rezeptblock einer PSD.
        '''
        ''' Gezählt wird jedes Objekt, das Text mit seiner Schrift zeichnet: Text und ein
        ''' Wasserzeichen ohne Bild. Objekte in Gruppen zählen mit, denn sie liegen in derselben
        ''' Liste und verweisen nur auf ihre Gruppe. Ein Objekt ohne Familie zählt nicht, es nimmt
        ''' ohnehin die Vorgabe des Systems. Gesucht wird über <see cref="FindInstalledFamily"/>
        ''' mit dem Familiennamen, damit die Ersatzschrift des Systems nicht als vorhanden gilt.</summary>
        Public Shared Function FindMissingFonts(annotations As IEnumerable(Of ImageAnnotation)) As List(Of MissingFont)
            Dim missingFonts As New List(Of MissingFont)()
            If annotations Is Nothing Then Return missingFonts
            ' Je Familie einmal fragen: die Liste der installierten Familien ist nicht umsonst.
            Dim installed As New Dictionary(Of String, Boolean)(StringComparer.OrdinalIgnoreCase)
            For Each annotation In annotations
                If annotation Is Nothing OrElse Not DrawsOwnText(annotation) Then Continue For
                Dim family = If(annotation.FontFamily, "").Trim()
                If family.Length = 0 Then Continue For
                Dim present As Boolean
                If Not installed.TryGetValue(family, present) Then
                    present = FindInstalledFamily(family, isPostScriptName:=False) IsNot Nothing
                    installed(family) = present
                End If
                If Not present Then CountMissingFont(missingFonts, family)
            Next
            For Each entry In missingFonts
                entry.FallbackFamily = ImageProcessor.ResolveTextFamilyName(entry.FamilyName)
            Next
            Return missingFonts
        End Function

        ''' <summary>Ob das Objekt Text in seiner eigenen Schrift zeichnet: ein Textobjekt, oder ein
        ''' Wasserzeichen, das kein Bild trägt.</summary>
        Private Shared Function DrawsOwnText(annotation As ImageAnnotation) As Boolean
            Dim kind = If(annotation.Kind, "").Trim()
            If String.Equals(kind, "Text", StringComparison.OrdinalIgnoreCase) Then Return True
            Return String.Equals(kind, "Watermark", StringComparison.OrdinalIgnoreCase) AndAlso
                   String.IsNullOrWhiteSpace(annotation.ImagePath)
        End Function

        ''' <summary>Die Protokollzeile zu fehlenden Schriften, auch dort, wo nichts gemeldet wird
        ''' (beim Blättern). Ohne fehlende Schrift keine Zeile.</summary>
        Public Shared Sub LogMissingFonts(marker As String, sourcePath As String, missingFonts As IEnumerable(Of MissingFont))
            If missingFonts Is Nothing OrElse Not missingFonts.Any() Then Return
            Dim listed = missingFonts.Select(Function(m) m.FamilyName & "=" & m.LayerCount.ToString(Globalization.CultureInfo.InvariantCulture))
            Dim fallbacks = missingFonts.Select(Function(m) m.FallbackFamily).Distinct(StringComparer.OrdinalIgnoreCase)
            DiagnosticLogService.LogAlways(marker,
                $"{Path.GetFileName(If(sourcePath, ""))} missing={String.Join(", ", listed)} fallback={String.Join(", ", fallbacks)}")
        End Sub

        ''' <summary>Der Satz für die Rückfrage beim Öffnen: welche Schriften fehlen, wie viele
        ''' Ebenen je Schrift, und womit der Text bis dahin gesetzt wird. "", wenn keine fehlt.
        ''' Ein Text mit Platzhaltern je Teil, damit jede Sprache ihre Wortstellung behält.
        ''' Derselbe Satz für fremde Textebenen einer PSD und für die Textobjekte eines .fpx oder
        ''' des eigenen Rezeptblocks; ein Textobjekt ist im Ebenenpanel eine Ebene.</summary>
        Public Shared Function MissingFontsNotice(missingFonts As IEnumerable(Of MissingFont)) As String
            If missingFonts Is Nothing OrElse Not missingFonts.Any() Then Return ""
            Dim listed As New List(Of String)()
            For Each entry In missingFonts
                listed.Add(If(entry.LayerCount = 1,
                              String.Format(LocalizationService.T("{0} (1 Ebene)"), entry.FamilyName),
                              String.Format(LocalizationService.T("{0} ({1} Ebenen)"), entry.FamilyName, entry.LayerCount)))
            Next
            ' Je fehlender Familie kann das System eine andere Ersatzschrift liefern; genannt wird
            ' jede einmal.
            Dim fallbacks = missingFonts.Select(Function(m) m.FallbackFamily).
                Where(Function(f) Not String.IsNullOrWhiteSpace(f)).
                Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            Dim fallback = If(fallbacks.Count = 0, New ImageAnnotation().FontFamily, String.Join(LocalizationService.T(", "), fallbacks))
            Return String.Format(LocalizationService.T("Auf diesem Rechner fehlen Schriften: {0}. Der Text bleibt bearbeitbar, wird aber mit {1} gesetzt und kann deshalb anders aussehen."),
                                 String.Join(LocalizationService.T(", "), listed), fallback)
        End Function

        ''' <summary>Die installierte Familie, deren Name ohne Leerzeichen dem Schlüssel gleicht,
        ''' ohne Rücksicht auf Groß- und Kleinschreibung; Nothing, wenn es keine gibt.</summary>
        Private Shared Function FindFamily(families As IEnumerable(Of String), familyKey As String) As String
            For Each candidate In families
                If String.Equals(candidate.Replace(" ", ""), familyKey, StringComparison.OrdinalIgnoreCase) Then Return candidate
            Next
            Return Nothing
        End Function

        ''' <summary>Ob die Matrix der Textebene mehr tut als drehen und gleichmäßig skalieren:
        ''' waagerecht anders strecken als senkrecht, scheren oder spiegeln. Ein Textobjekt kennt nur
        ''' EINEN Schriftgrad und eine Drehung; als Text käme ein in Photoshop breit gezogener
        ''' Schriftzug schmaler zurück, ein geschrägter gerade und ein gespiegelter seitenrichtig
        ''' (die Spiegelung läse <see cref="ApplyPsdTextLayout"/> sogar als Drehung). Dann bleibt
        ''' die Ebene ein Bild, und jeder Punkt stimmt.
        '''
        ''' Ebenso verformter Text (Bogen, Flagge) und Text an einem Pfad: beides steht nicht in
        ''' der Matrix, und als Text kämen sie in geraden Zeilen zurück. Den Pfad eines fremden
        ''' Textes liest der Import nicht aus, obwohl ein Textobjekt einen tragen könnte.</summary>
        Private Shared Function IsDistortedText(style As PsdEngineData) As Boolean
            If style IsNot Nothing AndAlso (style.IsWarped OrElse style.IsOnPath) Then Return True
            Dim m = style?.Matrix
            If m Is Nothing Then Return False
            Dim scaleX = Math.Sqrt(m(0) * m(0) + m(1) * m(1))
            Dim scaleY = Math.Sqrt(m(2) * m(2) + m(3) * m(3))
            If scaleX < 0.000001 OrElse scaleY < 0.000001 Then Return False
            If Math.Abs(scaleX / scaleY - 1.0) > 0.01 Then Return True
            ' Scherung: die zweite Achse steht nicht senkrecht auf der ersten. Dieselbe Schranke
            ' wie in ApplyPsdTextLayout, das solche Matrizen sonst stillschweigend uebergeht.
            If Math.Abs(m(0) * m(2) + m(1) * m(3)) > 0.001 * scaleX * scaleY Then Return True
            ' Spiegelung: negative Determinante.
            Return m(0) * m(3) - m(1) * m(2) < 0
        End Function

        ''' <summary>Warum ein Textobjekt diese Textebene nicht treu setzen kann, oder "", wenn es
        ''' kann. Dann bleibt sie das Bild aus den Ebenendaten, auch bei der Wahl „Als Text".
        '''
        ''' Die meisten Gründe stehen schon in den Textdaten (<see cref="PsdEngineData.UnsupportedReason"/>:
        ''' Textrahmen, senkrechter Satz, abweichende Stilläufe, Merkmale ohne Gegenstück, darunter
        ''' ein anderer Zeilenabstand in einem Lauf oder Absatz). Hier kommt der Zeilenabstand des
        ''' ganzen Textes dazu: das Textobjekt trägt EINEN, eingestellt in Prozent des Grads
        ''' (<see cref="ImageAnnotation.LineSpacingPercent"/>), und der Import übernimmt ihn
        ''' (<see cref="ApplyPsdLineSpacing"/>). Mehrzeiliger Text kommt deshalb nur dann als Bild,
        ''' wenn der Abstand nicht zu bestimmen ist oder außerhalb von 50 bis 300 Prozent liegt.
        '''
        ''' Ohne lesbare Textdaten (Nothing) bleibt es beim alten Weg: Text mit geschätztem Grad.</summary>
        Public Shared Function UnsupportedTextReason(style As PsdEngineData, text As String) As String
            If style Is Nothing Then Return ""
            If Not String.IsNullOrEmpty(style.UnsupportedReason) Then Return style.UnsupportedReason
            If style.LineCount > 1 AndAlso style.FontSize > 0 AndAlso style.FontSize <= 100_000 Then
                If Not Double.IsFinite(style.Leading) OrElse style.Leading <= 0 Then Return "leading"
                Dim percent = style.Leading / style.FontSize * 100.0
                If percent < 50.0 OrElse percent > 300.0 Then Return "leading"
            End If
            Return ""
        End Function

        ''' <summary>Der Zeilenabstand der Textebene am Textobjekt. Photoshop setzt die erste
        ''' Grundlinie eines Punkttexts auf den Ursprung und jede weitere um den Zeilenabstand
        ''' tiefer, automatisch ist das der Anteil des Absatzes (meist 120 Prozent) mal Grad, fest
        ''' der eingestellte Wert. Das Textobjekt setzt genauso, mit dem Abstand in Prozent des
        ''' Grads. Liegt der Abstand der Datei auf dem automatischen der gefundenen Schrift, bleibt
        ''' das Objekt auf automatisch: so schreibt FerrumPix selbst seine Textebenen, und sie kommen
        ''' unverändert zurück. Schrift und Grad müssen schon am Objekt stehen.</summary>
        Private Shared Sub ApplyPsdLineSpacing(annotation As ImageAnnotation, style As PsdEngineData)
            If Not Double.IsFinite(style.Leading) OrElse style.Leading <= 0 Then Return
            If style.FontSize <= 0 OrElse style.FontSize > 100_000 Then Return
            Dim percent = style.Leading / style.FontSize * 100.0
            If percent < 50.0 OrElse percent > 300.0 Then Return
            annotation.LineSpacingPercent = 0
            Dim automatic = ImageProcessor.GetTextAnnotationLineHeight(annotation)
            If automatic > 0 AndAlso Math.Abs(style.Leading - automatic) <= 0.01 Then Return
            annotation.LineSpacingPercent = CSng(percent)
        End Sub

        ''' <summary>Lage, Drehung, Ausrichtung und Zeichenabstand aus der Textebene.
        '''
        ''' Das Ebenenrechteck umschliesst nur die gezeichneten Bildpunkte, bei gedrehtem Text sogar
        ''' achsparallel. Der Rahmen im Textraum und die Matrix des TySh-Blocks sagen dagegen, wo
        ''' der Text WIRKLICH steht: die Mitte des Rahmens geht durch die Matrix ins Dokument, die
        ''' Kanten mit dem Massstab ihrer Achse, und der Winkel der ersten Achse ist die Drehung.
        ''' Gestreckt, geschert oder gespiegelt kommt eine Ebene gar nicht hierher, sondern als
        ''' Bild (<see cref="IsDistortedText"/>).</summary>
        Private Shared Sub ApplyPsdTextLayout(annotation As ImageAnnotation, style As PsdEngineData)
            Select Case style.Justification
                Case 1 : annotation.TextAlignment = "Right"
                Case 2 : annotation.TextAlignment = "Center"
                Case 3, 4, 5, 6 : annotation.TextAlignment = "Justify"
                Case 0 : annotation.TextAlignment = "Left"
            End Select
            If Double.IsFinite(style.Tracking) Then annotation.LetterSpacingPercent = CSng(style.Tracking / 10.0)

            Dim m = style.Matrix
            If m Is Nothing OrElse Not style.HasBounds Then Return
            Dim scaleX = Math.Sqrt(m(0) * m(0) + m(1) * m(1))
            Dim scaleY = Math.Sqrt(m(2) * m(2) + m(3) * m(3))
            If scaleX < 0.000001 OrElse scaleY < 0.000001 Then Return
            ' Die zweite Achse muss die um 90 Grad gedrehte erste sein, sonst ist es keine reine
            ' Drehung mit Massstab.
            If Math.Abs(m(0) * m(2) + m(1) * m(3)) > 0.001 * scaleX * scaleY Then Return
            If m(0) * m(3) - m(1) * m(2) <= 0 Then Return

            ' Die erste Grundlinie eines Punkttexts liegt im Textraum auf der Höhe 0; das Textobjekt
            ' setzt sie einen Schriftgrad unter die Oberkante seines Rahmens. Die Oberkante kommt
            ' deshalb aus dem Grad und nicht aus dem Rahmen der Datei: Photoshop legt dessen
            ' Oberkante an die Oberlänge der Schrift, und der Text stand um die Differenz zu tief
            ' (bei Arial 24 drei Bildpunkte). Was FerrumPix selbst schreibt, trägt ohnehin genau
            ' diese Oberkante (BuildPsdTextBlock).
            Dim boundsTop = style.BoundsTop
            Dim localSize = style.FontSize / scaleY
            If Double.IsFinite(localSize) AndAlso localSize > 0 AndAlso -localSize < style.BoundsBottom Then boundsTop = -localSize
            Dim width = (style.BoundsRight - style.BoundsLeft) * scaleX
            Dim height = (style.BoundsBottom - boundsTop) * scaleY
            If Not Double.IsFinite(width) OrElse Not Double.IsFinite(height) OrElse width < 1 OrElse height < 1 Then Return
            Dim localX = (style.BoundsLeft + style.BoundsRight) / 2.0
            Dim localY = (boundsTop + style.BoundsBottom) / 2.0
            Dim centerX = m(0) * localX + m(2) * localY + m(4)
            Dim centerY = m(1) * localX + m(3) * localY + m(5)
            If Not Double.IsFinite(centerX) OrElse Not Double.IsFinite(centerY) Then Return

            annotation.XPixels = CSng(centerX - width / 2.0)
            annotation.YPixels = CSng(centerY - height / 2.0)
            annotation.WidthPixels = CSng(width)
            annotation.HeightPixels = CSng(height)
            annotation.RotationDegrees = CSng(Math.Atan2(m(1), m(0)) * 180.0 / Math.PI)
        End Sub

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
            ' Die Ebenen-Datei bleibt liegen und wandert beim Speichern ins Buendel.
            Using encoded = PngEncoder.Encode(bmp, PngPurpose.Stored)
                If encoded Is Nothing Then Return False
                Using fs = File.Create(targetPath)
                    encoded.SaveTo(fs)
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
