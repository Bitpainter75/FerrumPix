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

' Das Zeichnen der Objekte auf der Buehne: die eigene Ebene je Objekt, Deckung und Schnittmaske,
' Effekte (Schatten, Schein, Kontur), die Objektverzerrung, alle Formen, SVG, Text und Textpfad.
' Eigener Zustand: der Schriftschnitt- und der Formpfad-Zwischenspeicher.
' Herausgeloest am 2026-08-06 aus ImageProcessor.vb, Zeile fuer Zeile unveraendert.
' Die Pinselstriche liegen daneben in ImageProcessorBrush.vb.
Namespace Services

    Partial Public Class ImageProcessor

        ''' <summary>Trägt das Objekt eigene Pixel-Anpassungen, und sind sie eingeschaltet? Nur dann
        ''' lohnt die eigene Ebene. AUSGESCHALTETE Anpassungen bleiben vollständig erhalten und werden
        ''' nur übersprungen - genau wie das Auge der Bildanpassungen es fürs ganze Bild tut. Die eine
        ''' Stelle dafür: jeder Renderweg fragt hier.</summary>
        Private Shared Function HasObjectAdjustments(annotation As ImageAnnotation) As Boolean
            Return annotation IsNot Nothing AndAlso Not annotation.AdjustmentsHidden AndAlso
                   annotation.Adjustments IsNot Nothing AndAlso annotation.Adjustments.HasPixelAdjustments()
        End Function

        ''' <summary>Zeichnet ein Objekt (samt Drehung, Spiegelung, Schatten/Glühen) auf die übergebene
        ''' Leinwand. Ausgelagert, weil dieselbe Zeichnung entweder direkt aufs Bild geht oder - wenn das
        ''' Objekt eigene Anpassungen trägt - zuerst auf eine eigene transparente Ebene.</summary>
        Private Shared Sub DrawAnnotationOnCanvas(canvas As SKCanvas, kind As String, renderAnnotation As ImageAnnotation,
                                                  rect As SKRect, sourceWidth As Integer, sourceHeight As Integer)
            Dim x = rect.Left
            Dim y = rect.Top
            Dim maxWidth = rect.Width
            Dim fontSize = Math.Max(8.0F, renderAnnotation.FontSizePixels)
            Dim alphaFactor = Clamp(renderAnnotation.Opacity, 0, 100) / 100.0F
            Dim fill = ApplyAlpha(ParseColor(renderAnnotation.FillColor, SKColors.White), alphaFactor)
            Dim stroke = ApplyAlpha(ParseColor(renderAnnotation.StrokeColor, SKColors.Black), alphaFactor)
            ' NULL HEISST KEINE KONTUR. Das Mindestmaß von einem Punkt gilt erst AB einer gewollten
            ' Kontur: es fängt den Fall ab, dass eine sehr dünne Kontur beim Verkleinern ganz
            ' verschwindet. Vorher galt es auch für die Null, und dann zeichnete ein Objekt mit
            ' ausdrücklich abgeschalteter Kontur trotzdem eine Haarlinie - beim freien Pfad, dessen
            ' ganze Erscheinung die Kontur ist, war das besonders sichtbar (Nutzerbefund 2026-08-08:
            ' "ich habe die Kontur beim Pfad auf 0, dennoch wird eine Linie gezeichnet").
            Dim strokeWidth = If(renderAnnotation.StrokeWidth <= 0.0F, 0.0F,
                                 Math.Max(1.0F, renderAnnotation.StrokeWidth))

            canvas.Save()
            If Math.Abs(renderAnnotation.RotationDegrees) > 0.01F Then
                canvas.RotateDegrees(renderAnnotation.RotationDegrees, rect.MidX, rect.MidY)
            End If
            ' Spiegeln um die eigene Mitte - NACH der Drehung, damit „gedreht und gespiegelt" das Objekt
            ' nicht zusätzlich verschiebt. Schatten/Glühen und die Füllung folgen mit, weil alles Weitere
            ' auf derselben Leinwand-Transformation zeichnet.
            If renderAnnotation.FlipHorizontal OrElse renderAnnotation.FlipVertical Then
                canvas.Translate(rect.MidX, rect.MidY)
                canvas.Scale(If(renderAnnotation.FlipHorizontal, -1.0F, 1.0F),
                             If(renderAnnotation.FlipVertical, -1.0F, 1.0F))
                canvas.Translate(-rect.MidX, -rect.MidY)
            End If

            If renderAnnotation.ShadowEnabled OrElse renderAnnotation.GlowEnabled Then
                DrawAnnotationEffects(canvas, kind, renderAnnotation, rect, x, y, maxWidth, fontSize, fill, stroke, strokeWidth, alphaFactor, sourceWidth, sourceHeight)
            End If
            DrawAnnotationShape(canvas, kind, renderAnnotation, rect, x, y, maxWidth, fontSize, fill, stroke, strokeWidth, alphaFactor)
            canvas.Restore()
        End Sub

        Private Shared Function ApplyAnnotations(source As SKBitmap, adj As ImageAdjustments) As SKBitmap
            ' ARBEITSBILD-Umbau (Stufe D): Pinsel-/Radiererstriche sind ins Arbeitsbild eingebacken
            ' und laufen nicht mehr hier durch - hier rendern nur noch die Z-Order-Objekte.
            ' Ohne Objekte UND mit sichtbarem Hintergrund gibt es nichts zu tun. Ist der Hintergrund
            ' ausgeblendet, muss aber selbst ohne Objekte ein transparentes Bild herauskommen.
            Dim hasObjects = adj.Annotations IsNot Nothing AndAlso adj.Annotations.Count > 0
            If Not adj.BackgroundHidden AndAlso Not hasObjects Then Return source

            Dim result As SKBitmap
            If adj.BackgroundHidden Then
                ' Hintergrund-Ebene aus: die Objekte schweben auf dem GRUND DES DOKUMENTS - der
                ' Hintergrundfarbe aus der Bildgrößen-Gruppe, ab Werk durchsichtig (im Editor das
                ' Schachbrett, gespeichert ein durchsichtiges PNG). Die teure Basis-Pipeline lief zwar,
                ' wird hier aber verworfen - das ist der Preis fürs saubere Ein-/Ausschalten über einen
                ' einzigen Schalter.
                '
                ' Die Farbe MUSS hier noch einmal aufgelegt werden: sie kommt in der Basis-Kette unter
                ' das Bild (ApplyDocumentBackground, gleich nach der Leinwandgröße), und genau diese
                ' Basis wird hier weggeworfen. Ohne das war der Grund nach dem Ausblenden immer
                ' durchsichtig, auch wenn im Werkzeug eine Farbe stand (Nutzerbefund 2026-08-08).
                result = New SKBitmap(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using clearCanvas = New SKCanvas(result)
                    clearCanvas.Clear(DocumentBackgroundColor(adj))
                End Using
            Else
                result = CloneBitmapForAnnotationComposite(source)
            End If

            If Not hasObjects Then Return result

            ' Korrekturebenen, die IN den Objektstapel einsortiert sind, wirken auf alles, was unter
            ' ihnen liegt. Dafür werden die Objekte der Reihe nach gezeichnet und nach jedem Objekt die
            ' dort eingehängten Korrekturen auf das bisherige Komposit angewendet. Ohne solche Ebenen
            ' (Normalfall) bleibt es bei EINEM Aufruf über alle Objekte - kein Mehraufwand.
            Dim stacked = If(adj.MaskedAdjustmentLayers, New System.Collections.Generic.List(Of MaskedAdjustmentLayer)()).
                Where(Function(l) l IsNot Nothing AndAlso Not String.IsNullOrEmpty(l.StackAboveAnnotationId)).ToList()
            Dim hasRenderStepGroup = HasRenderStepGroupMember(adj)
            If stacked.Count = 0 AndAlso Not hasRenderStepGroup Then
                Using canvas = New SKCanvas(result)
                    DrawAnnotationsOnCanvas(canvas, adj, source.Width, source.Height, 0, 0, source.Width, source.Height, adj.Annotations)
                End Using
                Return result
            End If

            ' FARBTYP: das Objekt-Komposit ist Rgba8888, die Masken-Komposition rechnet aber in
            ' Bgra8888 (TryBorrowBgraBuffer) und fiele sonst STILL auf die Baseline zurück - die
            ' Korrektur wäre wirkungslos.
            '
            ' Umgewandelt wird EINMAL für den ganzen Durchlauf statt zweimal je Korrektur: vorher
            ' kostete jede eingehängte Korrektur drei Vollkopien des Bildes (hin, Klon in
            ' ApplyMaskedAdjustmentLayers, zurück) - bei 24 MP rund 300 MB Speicherverkehr pro
            ' Korrektur und Frame. Jetzt bleibt es bei einer Umwandlung am Anfang, einer am Ende und
            ' dem unvermeidbaren Klon je Korrektur.
            Dim targetColorType = result.ColorType
            If targetColorType <> SKColorType.Bgra8888 Then
                result = ReplaceBitmap(result, ConvertBitmapToColorType(result, SKColorType.Bgra8888))
            End If

            Dim index = 0
            While index < adj.Annotations.Count
                Dim annotation = adj.Annotations(index)
                Dim chain = RenderStepChainFor(adj, annotation)
                If chain.Count = 0 Then
                    Using canvas = New SKCanvas(result)
                        DrawAnnotationsOnCanvas(canvas, adj, source.Width, source.Height, 0, 0, source.Width, source.Height,
                                                New System.Collections.Generic.List(Of ImageAnnotation) From {annotation})
                    End Using
                    If stacked.Any(Function(l) String.Equals(l.StackAboveAnnotationId, annotation.Id, StringComparison.Ordinal)) Then
                        ' KEIN Using um das Ergebnis: es WIRD das neue Komposit (ReplaceBitmap gibt das
                        ' alte frei). Ein Using würde genau das Bitmap freigeben, mit dem weitergezeichnet
                        ' wird.
                        Dim corrected = ApplyMaskedAdjustmentLayers(result, adj, source.Width, source.Height, annotation.Id)
                        If corrected IsNot Nothing Then result = ReplaceBitmap(result, corrected)
                    End If
                    index += 1
                    Continue While
                End If

                ' DIE GRUPPE ALS RENDERSCHRITT, und zwar in beliebiger Verschachtelung: die ÄUSSERSTE
                ' Gruppe der Kette öffnet eine Ebene, jede Untergruppe darin eine weitere. Der Lauf
                ' rechnet sich selbst rekursiv (siehe DrawGroupRun).
                Dim groupX = 0, groupY = 0
                Dim groupLayer = DrawGroupRun(adj, source, stacked, chain(0), index, groupX, groupY)
                If groupLayer IsNot Nothing Then
                    Try
                        ' Das Ziel ist hier das ganze Bild, seine Ecke also 0/0.
                        CompositeGroupLayer(result, 0, 0, groupLayer, groupX, groupY, chain(0), adj,
                                            source.Width, source.Height)
                    Finally
                        groupLayer.Dispose()
                    End Try
                End If
            End While

            If result.ColorType <> targetColorType Then
                result = ReplaceBitmap(result, ConvertBitmapToColorType(result, targetColorType))
            End If
            Return result
        End Function

        ''' <summary>Die Kette der WIRKSAMEN Gruppen eines Objekts, von außen nach innen. Leer heißt
        ''' Durchgriff: das Objekt wird einzeln gezeichnet, wie eh und je.
        '''
        ''' Gruppen im Durchgriff (volle Deckkraft, Normal, keine Maske) fallen aus der Kette heraus -
        ''' sie öffnen keine Ebene, und eine Untergruppe darin gehört damit unmittelbar in die nächste
        ''' wirksame Gruppe darüber.</summary>
        Friend Shared Function RenderStepChainFor(adj As ImageAdjustments, annotation As ImageAnnotation) As List(Of AnnotationGroup)
            If adj Is Nothing OrElse annotation Is Nothing OrElse String.IsNullOrEmpty(annotation.GroupId) Then
                Return New List(Of AnnotationGroup)()
            End If
            Return adj.GroupChainOf(annotation.GroupId).Where(Function(g) g.IsRenderStep()).ToList()
        End Function

        ''' <summary>Die äußerste wirksame Gruppe eines Objekts - oder Nothing.</summary>
        Friend Shared Function RenderStepGroupFor(adj As ImageAdjustments, annotation As ImageAnnotation) As AnnotationGroup
            Dim chain = RenderStepChainFor(adj, annotation)
            Return If(chain.Count = 0, Nothing, chain(0))
        End Function

        ''' <summary>Zeichnet einen zusammenhängenden Lauf von Objekten, die alle in
        ''' <paramref name="group"/> liegen, auf EINE durchsichtige Ebene und gibt sie zurück.
        ''' <paramref name="index"/> steht danach hinter dem letzten verarbeiteten Objekt.
        '''
        ''' Trifft der Lauf auf ein Objekt, das zusätzlich in einer UNTERgruppe liegt, ruft er sich
        ''' für diese selbst auf und komponiert deren fertige Ebene mit ihrer Deckkraft, Mischmethode
        ''' und Maske ein. So entsteht die Verschachtelung ohne einen zweiten Weg.</summary>
        Private Shared Function DrawGroupRun(adj As ImageAdjustments, source As SKBitmap,
                                             stacked As List(Of MaskedAdjustmentLayer),
                                             group As AnnotationGroup, ByRef index As Integer,
                                             ByRef originX As Integer, ByRef originY As Integer) As SKBitmap
            ' WIE GROSS MUSS DIE EBENE SEIN? Ein Vorlauf ohne zu zeichnen beantwortet das: er geht
            ' denselben Weg wie die Schleife darunter und sammelt die Rechtecke ein.
            '
            ' DAS IST KEINE FEINHEIT. Eine Gruppenebene in voller Bildgroesse kostet bei 45
            ' Megapixeln rund 180 MiB, und verschachtelte Gruppen legen je Stufe eine weitere an.
            ' Gezeichnet wird darauf aber nur die Flaeche der Mitglieder.
            Dim bounds = ComputeGroupRunBounds(adj, source, stacked, group, index)
            ' MIT EINER EINGEHAENGTEN KORREKTUR BLEIBT ES BEI VOLLER GROESSE: die Korrekturebenen
            ' rechnen in Bildkoordinaten und kennen keinen Versatz - auf einem zugeschnittenen
            ' Traeger saesse ihre Maske falsch. Der Regelfall ohne Korrektur bekommt den Zuschnitt.
            Dim rect = bounds.Rect
            If bounds.HasStackedAdjustment OrElse rect.Width <= 0 OrElse rect.Height <= 0 Then
                rect = New SKRectI(0, 0, source.Width, source.Height)
            End If
            originX = rect.Left
            originY = rect.Top

            Dim layer = New SKBitmap(rect.Width, rect.Height, SKColorType.Bgra8888, SKAlphaType.Premul)
            Using layerCanvas = New SKCanvas(layer)
                layerCanvas.Clear(SKColors.Transparent)
            End Using

            While index < adj.Annotations.Count
                Dim current = adj.Annotations(index)
                Dim chain = RenderStepChainFor(adj, current)
                Dim position = chain.FindIndex(Function(g) Object.ReferenceEquals(g, group))
                ' Gehört das Objekt gar nicht (mehr) zu dieser Gruppe, ist der Lauf hier zu Ende.
                If position < 0 Then Exit While

                If position = chain.Count - 1 Then
                    ' Unmittelbares Mitglied: auf diese Ebene zeichnen.
                    Using layerCanvas = New SKCanvas(layer)
                        DrawAnnotationsOnCanvas(layerCanvas, adj, source.Width, source.Height,
                                                originX, originY, layer.Width, layer.Height,
                                                New System.Collections.Generic.List(Of ImageAnnotation) From {current})
                    End Using
                    ' EINE KORREKTUR IN DER GRUPPE BLEIBT IN DER GRUPPE. Genau das ist der zweite
                    ' Teil des Renderschritts: sie sieht nur, was die Gruppe bisher gezeichnet hat,
                    ' und nicht das Bild darunter.
                    If stacked.Any(Function(l) String.Equals(l.StackAboveAnnotationId, current.Id, StringComparison.Ordinal)) Then
                        Dim corrected = ApplyMaskedAdjustmentLayers(layer, adj, source.Width, source.Height, current.Id)
                        If corrected IsNot Nothing Then layer = ReplaceBitmap(layer, corrected)
                    End If
                    index += 1
                Else
                    ' Es liegt tiefer: die nächste Gruppe der Kette bekommt eine eigene Ebene.
                    Dim child = chain(position + 1)
                    Dim childX = 0, childY = 0
                    Dim childLayer = DrawGroupRun(adj, source, stacked, child, index, childX, childY)
                    If childLayer IsNot Nothing Then
                        Try
                            CompositeGroupLayer(layer, originX, originY, childLayer, childX, childY,
                                                child, adj, source.Width, source.Height)
                        Finally
                            childLayer.Dispose()
                        End Try
                    End If
                End If
            End While
            Return layer
        End Function

        ''' <summary>Der Vorlauf zu <see cref="DrawGroupRun"/>: wie weit reicht der Lauf, welche
        ''' Flaeche nimmt er ein, und haengt an einem seiner Objekte eine Korrektur?
        '''
        ''' Er geht genau denselben Weg wie die Schleife dort (Abbruch, sobald ein Objekt nicht mehr
        ''' zu dieser Gruppe gehoert) und zaehlt die Objekte der UNTERgruppen mit - deren Ebenen
        ''' landen ja in dieser. <paramref name="startIndex"/> wird nicht veraendert.</summary>
        Private Shared Function ComputeGroupRunBounds(adj As ImageAdjustments, source As SKBitmap,
                                                      stacked As List(Of MaskedAdjustmentLayer),
                                                      group As AnnotationGroup, startIndex As Integer) _
                                                      As (Rect As SKRectI, HasStackedAdjustment As Boolean)
            Dim union = SKRectI.Empty
            Dim hasStacked = False
            Dim scan = startIndex
            While scan < adj.Annotations.Count
                Dim current = adj.Annotations(scan)
                If Not RenderStepChainFor(adj, current).Any(Function(g) Object.ReferenceEquals(g, group)) Then Exit While
                ' Nur was wirklich gezeichnet wird, spannt die Ebene auf. Die Sichtbarkeit geht
                ' ueber denselben Chokepoint wie beim Zeichnen, sonst spannte ein ausgeblendetes
                ' Objekt die Ebene weiter auf, als sie sein muss.
                If adj.IsAnnotationRenderVisible(current) Then
                    Dim rendered = TransformAnnotationForGeometry(current, adj, source.Width, source.Height)
                    If rendered IsNot Nothing Then
                        Dim rect = ComputeAnnotationDirtyRectCore(source.Width, source.Height, rendered)
                        If rect.Width > 0 AndAlso rect.Height > 0 Then
                            union = If(union.IsEmpty, rect, SKRectI.Union(union, rect))
                        End If
                    End If
                End If
                If stacked IsNot Nothing AndAlso
                   stacked.Any(Function(l) String.Equals(l.StackAboveAnnotationId, current.Id, StringComparison.Ordinal)) Then
                    hasStacked = True
                End If
                scan += 1
            End While
            If Not union.IsEmpty Then
                union = SKRectI.Intersect(union, New SKRectI(0, 0, source.Width, source.Height))
            End If
            Return (union, hasStacked)
        End Function

        ''' <summary>Legt die fertige Ebene einer Gruppe ins Ziel: erst ihre MASKE darauf, dann mit
        ''' Deckkraft und Mischmethode der Gruppe hinein.</summary>
        ''' <param name="targetX">Ecke des ZIELS im Bildraum - bei einer Gruppe in einer Gruppe ist
        ''' das Ziel selbst zugeschnitten.</param>
        ''' <param name="layerX">Ecke der Gruppenebene im Bildraum.</param>
        Private Shared Sub CompositeGroupLayer(target As SKBitmap, targetX As Integer, targetY As Integer,
                                               groupLayer As SKBitmap, layerX As Integer, layerY As Integer,
                                               group As AnnotationGroup, adj As ImageAdjustments,
                                               width As Integer, height As Integer)
            If target Is Nothing OrElse groupLayer Is Nothing OrElse group Is Nothing Then Return
            ' DIE MASKE DER GRUPPE liegt auf der fertigen Gruppenebene - sie deckt damit alles ab,
            ' was die Gruppe gezeichnet hat, statt jedes Mitglied einzeln. Genau dafür gibt es die
            ' Ebene; ohne sie liesse sich eine Gruppenmaske nur als Kopie an jedes Mitglied
            ' verteilen, und an den Überlappungen sähe man es.
            If Not String.IsNullOrEmpty(group.MaskId) AndAlso adj.Masks IsNot Nothing Then
                Dim groupMask = adj.Masks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso
                                                            String.Equals(m.Id, group.MaskId, StringComparison.Ordinal))
                Dim coverage = GetAnnotationMaskCoverage(groupMask, adj, width, height)
                ' Die Deckung steht in BILDgroesse, die Ebene kann zugeschnitten sein - deshalb ihre
                ' Ecke mitgeben, sonst laege die Maske um den Zuschnitt verschoben.
                If coverage IsNot Nothing Then ApplyCoverageToLayer(groupLayer, layerX, layerY, coverage, width, height)
            End If
            Using canvas = New SKCanvas(target)
                Dim alpha = CByte(Math.Max(0, Math.Min(255, Math.Round(Clamp(CSng(group.Opacity), 0, 100) / 100.0 * 255.0))))
                Using paint = New SKPaint With {
                    .BlendMode = ResolveAnnotationBlendMode(group.BlendMode),
                    .IsAntialias = True,
                    .Color = New SKColor(255, 255, 255, alpha)}
                    canvas.DrawBitmap(groupLayer, layerX - targetX, layerY - targetY, paint)
                End Using
            End Using
        End Sub

        ''' <summary>Gibt es ueberhaupt ein Objekt in einer wirksamen Gruppe? Die Frage entscheidet,
        ''' ob der teure Weg ueber Einzelzeichnungen noetig ist - ohne sie bleibt es beim EINEN
        ''' Aufruf ueber alle Objekte.</summary>
        Private Shared Function HasRenderStepGroupMember(adj As ImageAdjustments) As Boolean
            If adj Is Nothing OrElse adj.Annotations Is Nothing Then Return False
            For Each a In adj.Annotations
                If RenderStepGroupFor(adj, a) IsNot Nothing Then Return True
            Next
            Return False
        End Function

        ''' <summary>Zeichnet die Objekte in Z-Reihenfolge und gibt zurueck, WIE VIELE wirklich
        ''' gezeichnet wurden (sichtbar, mit Geometrie, nicht vom Clip verworfen). Die Zahl ist der
        ''' Messpunkt fuer den Kompositor-Umbau (OFFENE_PUNKTE Abschnitt 2, Stufe 1): sie zeigt je
        ''' Patch, wie viel Objektarbeit der Region-Weg heute leistet. Aufrufer duerfen den
        ''' Rueckgabewert ignorieren.</summary>
        ''' <summary>Das Objekt, auf dessen Deckung eine Schnittmaske sich bezieht: das naechste
        ''' SICHTBARE darunter, das nicht selbst beschraenkt ist. Mehrere beschraenkte Objekte
        ''' uebereinander teilen sich damit dieselbe Basis. Nothing = keine Basis vorhanden; der
        ''' Schalter bleibt dann wirkungslos, statt das Objekt verschwinden zu lassen.</summary>
        Private Shared Function FindClipBase(adj As ImageAdjustments, annotations As IReadOnlyList(Of ImageAnnotation),
                                             annotation As ImageAnnotation) As ImageAnnotation
            If annotations Is Nothing Then Return Nothing
            Dim index = -1
            For i = 0 To annotations.Count - 1
                If Object.ReferenceEquals(annotations(i), annotation) Then
                    index = i
                    Exit For
                End If
            Next
            If index <= 0 Then Return Nothing
            For i = index - 1 To 0 Step -1
                Dim candidate = annotations(i)
                If candidate Is Nothing OrElse Not adj.IsAnnotationRenderVisible(candidate) Then Continue For
                If candidate.ClipToLayerBelow Then Continue For
                Return candidate
            Next
            Return Nothing
        End Function

        ''' <summary>Deckung der Basis einer Schnittmaske im Gitter des Aufrufers (layerWidth mal
        ''' layerHeight ab offsetX/offsetY), ein Byte je Pixel.
        '''
        ''' Die Basis wird dafuer ein ZWEITES Mal gezeichnet. Das ist bewusst: ihre eigene Zeichnung
        ''' geht in das Komposit und ist dort nicht mehr von dem zu trennen, was schon darunter lag -
        ''' aus dem fertigen Bild laesst sich ihre Deckung nicht zurueckgewinnen.</summary>
        Private Shared Function BuildClipBaseCoverage(adj As ImageAdjustments, baseAnnotation As ImageAnnotation,
                                                      sourceWidth As Integer, sourceHeight As Integer,
                                                      offsetX As Integer, offsetY As Integer,
                                                      layerWidth As Integer, layerHeight As Integer) As Byte()
            Dim renderAnnotation = TransformAnnotationForGeometry(baseAnnotation, adj, sourceWidth, sourceHeight)
            If renderAnnotation Is Nothing Then Return Nothing
            Dim kind = If(renderAnnotation.Kind, "Text").Trim().ToLowerInvariant()
            ' Pinsel und Radiergummi haben keine freistehende Objektflaeche - ihre Striche liegen im
            ' Bildraum, und als Basis einer Schnittmaske taugen sie deshalb nicht.
            If IsPaintKind(kind) Then Return Nothing
            Dim rect = ComputeAnnotationRect(sourceWidth, sourceHeight, kind, renderAnnotation)
            Dim vx = offsetX, vy = offsetY
            Dim layer = RenderAnnotationToLayer(baseAnnotation, renderAnnotation, kind, rect,
                                                sourceWidth, sourceHeight, layerWidth, layerHeight, vx, vy, adj)
            If layer Is Nothing Then Return Nothing
            Try
                Dim coverage = New Byte(layerWidth * layerHeight - 1) {}
                Dim stride = layer.RowBytes
                Dim raw = New Byte(stride * layer.Height - 1) {}
                Marshal.Copy(layer.GetPixels(), raw, 0, raw.Length)
                Dim shiftX = vx - offsetX, shiftY = vy - offsetY
                For y = 0 To layer.Height - 1
                    Dim ty = shiftY + y
                    If ty < 0 OrElse ty >= layerHeight Then Continue For
                    Dim rowIn = y * stride, rowOut = ty * layerWidth
                    For x = 0 To layer.Width - 1
                        Dim tx = shiftX + x
                        If tx < 0 OrElse tx >= layerWidth Then Continue For
                        coverage(rowOut + tx) = raw(rowIn + x * 4 + 3)
                    Next
                Next
                Return coverage
            Finally
                layer.Dispose()
            End Try
        End Function

        ''' <summary>Die Deckung, mit der ein Objekt gezeichnet wird: seine Ebenenmaske, seine
        ''' Schnittmaske, oder das Produkt aus beidem. Nothing heisst volle Deckung - der Normalfall,
        ''' und dann kostet die Sache keinen einzigen Rechenschritt.</summary>
        Private Shared Function BuildAnnotationCoverage(adj As ImageAdjustments, annotation As ImageAnnotation,
                                                        annotations As IReadOnlyList(Of ImageAnnotation),
                                                        sourceWidth As Integer, sourceHeight As Integer,
                                                        offsetX As Integer, offsetY As Integer,
                                                        layerWidth As Integer, layerHeight As Integer,
                                                        clipCache As Dictionary(Of String, Byte())) As Byte()
            If Not UsesLayerCoverage(annotation) Then Return Nothing

            Dim result As Byte() = Nothing

            If Not String.IsNullOrEmpty(annotation.MaskId) AndAlso adj.Masks IsNot Nothing Then
                Dim maskData = adj.Masks.FirstOrDefault(Function(m) m IsNot Nothing AndAlso
                                                            String.Equals(m.Id, annotation.MaskId, StringComparison.Ordinal))
                Dim full = GetAnnotationMaskCoverage(maskData, adj, sourceWidth, sourceHeight)
                If full IsNot Nothing Then
                    If offsetX = 0 AndAlso offsetY = 0 AndAlso layerWidth = sourceWidth AndAlso layerHeight = sourceHeight Then
                        ' Vollrender: das Raster passt schon, es wird nur GELESEN.
                        result = full
                    Else
                        result = New Byte(layerWidth * layerHeight - 1) {}
                        For y = 0 To layerHeight - 1
                            Dim sy = offsetY + y
                            If sy < 0 OrElse sy >= sourceHeight Then Continue For
                            Dim copyLeft = Math.Max(0, -offsetX)
                            Dim copyCount = Math.Min(layerWidth - copyLeft, sourceWidth - (offsetX + copyLeft))
                            If copyCount <= 0 Then Continue For
                            Array.Copy(full, sy * sourceWidth + offsetX + copyLeft,
                                       result, y * layerWidth + copyLeft, copyCount)
                        Next
                    End If
                End If
            End If

            If annotation.ClipToLayerBelow Then
                Dim baseAnnotation = FindClipBase(adj, annotations, annotation)
                If baseAnnotation IsNot Nothing Then
                    Dim baseKey = baseAnnotation.Id
                    Dim clip As Byte() = Nothing
                    If clipCache Is Nothing OrElse Not clipCache.TryGetValue(baseKey, clip) Then
                        clip = BuildClipBaseCoverage(adj, baseAnnotation, sourceWidth, sourceHeight,
                                                     offsetX, offsetY, layerWidth, layerHeight)
                        If clipCache IsNot Nothing Then clipCache(baseKey) = clip
                    End If
                    If clip IsNot Nothing Then
                        If result Is Nothing Then
                            result = clip
                        Else
                            ' Nicht in result hineinschreiben, wenn das noch das GETEILTE Raster des
                            ' Maskenspeichers ist - das gehoert dem Speicher und wird nur gelesen.
                            Dim combined = New Byte(layerWidth * layerHeight - 1) {}
                            For i = 0 To combined.Length - 1
                                combined(i) = CByte(CInt(result(i)) * CInt(clip(i)) \ 255)
                            Next
                            result = combined
                        End If
                    End If
                End If
            End If

            Return result
        End Function

        Friend Shared Function DrawAnnotationsOnCanvas(canvas As SKCanvas, adj As ImageAdjustments,
                                                   sourceWidth As Integer, sourceHeight As Integer,
                                                   offsetX As Integer, offsetY As Integer,
                                                   layerWidth As Integer, layerHeight As Integer,
                                                   Optional renderAnnotations As IReadOnlyList(Of ImageAnnotation) = Nothing) As Integer
            If canvas Is Nothing OrElse adj Is Nothing Then Return 0
            Dim annotations = If(renderAnnotations, adj.Annotations)
            If annotations Is Nothing OrElse annotations.Count = 0 Then Return 0
            If sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse layerWidth <= 0 OrElse layerHeight <= 0 Then Return 0

            ' Die Schnittmaske schaut auf das Objekt DARUNTER - und damit auf den ganzen Stapel, nicht
            ' auf die Liste, die dieser Aufruf gerade zeichnet. ApplyAnnotations reicht bei
            ' eingehängten Korrekturen einzelne Objekte herein; mit dieser Liste als Bezug fände ein
            ' beschränktes Objekt nie seine Basis.
            Dim stack As IReadOnlyList(Of ImageAnnotation) = If(adj.Annotations, annotations)
            ' Mehrere Objekte über derselben Basis zeichnen sie sonst jedes für sich noch einmal.
            Dim clipCache As Dictionary(Of String, Byte()) = Nothing

            Dim drawn = 0
            For Each annotation In annotations
                ' Sichtbarkeit IMMER über den Chokepoint: er verundet das eigene IsVisible mit dem
                ' Schalter der Gruppe, zu der das Objekt gehört.
                If Not adj.IsAnnotationRenderVisible(annotation) Then Continue For
                Dim renderAnnotation = TransformAnnotationForGeometry(annotation, adj, sourceWidth, sourceHeight)
                If renderAnnotation Is Nothing Then Continue For
                ' AUSSERHALB DES CLIPS GAR NICHT ERST ZEICHNEN. Ein Region-Patch ist nur wenige hundert
                ' Pixel groß, zeichnete bisher aber JEDES Objekt des Dokuments - bei 36 eingefügten
                ' Bildern hieß das 36 Decodes und Skalierungen für einen 400x400-Fleck (gemessen 550 ms
                ' statt ~20 ms; der Zug wirkte dadurch zäh und die Ghost-Übergabe kam nicht durch,
                '). QuickReject prüft gegen den aktuellen Clip des Canvas und ist
                ' selbst praktisch kostenlos.
                Dim eigenRect = ComputeAnnotationDirtyRectCore(sourceWidth, sourceHeight, renderAnnotation)
                If Not eigenRect.IsEmpty Then
                    Dim clipTest = New SKRect(eigenRect.Left - offsetX, eigenRect.Top - offsetY,
                                              eigenRect.Right - offsetX, eigenRect.Bottom - offsetY)
                    If canvas.QuickReject(clipTest) Then Continue For
                End If
                drawn += 1
                Dim kind = If(renderAnnotation.Kind, "Text").Trim().ToLowerInvariant()

                ' Ebenenmaske und Schnittmaske des Objekts, zusammengerechnet. Nothing = volle
                ' Deckung, und dann bleibt alles Weitere Zeile für Zeile so, wie es war.
                Dim coverage As Byte() = Nothing
                If UsesLayerCoverage(annotation) Then
                    If clipCache Is Nothing Then clipCache = New Dictionary(Of String, Byte())(StringComparer.Ordinal)
                    coverage = BuildAnnotationCoverage(adj, annotation, stack, sourceWidth, sourceHeight,
                                                       offsetX, offsetY, layerWidth, layerHeight, clipCache)
                End If

                If IsPaintKind(kind) Then
                    Dim alphaFactor = Clamp(renderAnnotation.Opacity, 0, 100) / 100.0F
                    Dim stroke = ApplyAlpha(ParseColor(renderAnnotation.StrokeColor, SKColors.Black), alphaFactor)
                    Dim strokeWidth = Math.Max(1.0F, Clamp(renderAnnotation.StrokeWidth, 1, Math.Max(sourceWidth, sourceHeight)))
                    Dim isEraser = kind = "eraser"
                    Dim eraserFill As SKColor? = Nothing
                    If isEraser AndAlso Not String.IsNullOrWhiteSpace(renderAnnotation.EraserFillColor) Then
                        eraserFill = ApplyAlpha(ParseColor(renderAnnotation.EraserFillColor, SKColors.Transparent), alphaFactor)
                    End If
                    ' Mischmodus auch für Pinselstriche (nicht Radiergummi - der entfernt Pixel und ignoriert
                    ' den Modus): erst auf eine eigene transparente Ebene malen, dann mit dem Blend-Modus
                    ' einkomponieren - wie bei Formen/Text. Bei "Normal" direkt zeichnen (kein Extra-Speicher).
                    ' Eine Deckung braucht eine eigene Ebene, auf der sie wirken kann - der Radierer
                    ' bleibt ausgenommen, er entfernt Pixel und liesse sich nicht sinnvoll maskieren.
                    Dim useBrushBlendLayer = (Not isEraser) AndAlso
                        (Not IsNormalAnnotationBlendMode(renderAnnotation.BlendMode) OrElse coverage IsNot Nothing)
                    If useBrushBlendLayer Then
                        Using brushLayer = New SKBitmap(layerWidth, layerHeight, SKColorType.Rgba8888, SKAlphaType.Premul)
                            Using brushLayerCanvas = New SKCanvas(brushLayer)
                                brushLayerCanvas.Clear(SKColors.Transparent)
                                brushLayerCanvas.Translate(-offsetX, -offsetY)
                                If renderAnnotation.ShadowEnabled OrElse renderAnnotation.GlowEnabled Then
                                    DrawBrushStrokeWithEffects(brushLayerCanvas, renderAnnotation, sourceWidth, sourceHeight, stroke, strokeWidth)
                                Else
                                    DrawBrushStroke(brushLayerCanvas, renderAnnotation.Strokes, sourceWidth, sourceHeight, stroke, strokeWidth,
                                                    renderAnnotation.HardnessPercent, renderAnnotation.FlowPercent, renderAnnotation.BrushPreset, False, Nothing)
                                End If
                            End Using
                            ApplyCoverageToLayer(brushLayer, 0, 0, coverage, layerWidth, layerHeight)
                            DrawAnnotationLayer(canvas, brushLayer, renderAnnotation.BlendMode)
                        End Using
                    Else
                        canvas.Save()
                        canvas.Translate(-offsetX, -offsetY)
                        If (Not isEraser) AndAlso (renderAnnotation.ShadowEnabled OrElse renderAnnotation.GlowEnabled) Then
                            DrawBrushStrokeWithEffects(canvas, renderAnnotation, sourceWidth, sourceHeight, stroke, strokeWidth)
                        Else
                            DrawBrushStroke(canvas, renderAnnotation.Strokes, sourceWidth, sourceHeight, stroke, strokeWidth,
                                            renderAnnotation.HardnessPercent, renderAnnotation.FlowPercent, renderAnnotation.BrushPreset, isEraser, eraserFill)
                        End If
                        canvas.Restore()
                    End If
                    Continue For
                End If

                Dim rect = ComputeAnnotationRect(sourceWidth, sourceHeight, kind, renderAnnotation)

                ' Objekt MIT eigenen Anpassungen: erst allein auf eine transparente Ebene zeichnen, dann
                ' die Pixel-Pipeline darauf laufen lassen (Belichtung, Farbe, Filter … treffen so nur das
                ' Objekt), dann an Ort und Stelle in der Z-Reihenfolge einkomponieren. Ohne eigene
                ' Anpassungen wird wie bisher direkt gezeichnet - kein zusätzlicher Speicher, keine Zeit.
                If SplitsStrokeFromBlend(renderAnnotation, kind) Then
                    ' Kontur NICHT mitmischen: die Fuellung geht durch den Mischmodus, die Kontur wird
                    ' anschliessend normal darueber gelegt. Gezeichnet wird dafuer zweimal mit je einer
                    ' durchsichtig gesetzten Farbe - so bleibt jede der Zeichenroutinen unberuehrt.
                    DrawAnnotationViaLayer(canvas, annotation, AnnotationFillOnly(renderAnnotation), kind, rect,
                                           sourceWidth, sourceHeight, layerWidth, layerHeight, offsetX, offsetY,
                                           renderAnnotation.BlendMode, coverage, adj)
                    DrawAnnotationViaLayer(canvas, annotation, AnnotationStrokeOnly(renderAnnotation), kind, rect,
                                           sourceWidth, sourceHeight, layerWidth, layerHeight, offsetX, offsetY,
                                           "Normal", coverage, adj)
                ElseIf HasObjectAdjustments(annotation) OrElse Not IsNormalAnnotationBlendMode(renderAnnotation.BlendMode) OrElse
                       HasWarp(annotation) OrElse coverage IsNot Nothing Then
                    DrawAnnotationViaLayer(canvas, annotation, renderAnnotation, kind, rect,
                                           sourceWidth, sourceHeight, layerWidth, layerHeight, offsetX, offsetY,
                                           renderAnnotation.BlendMode, coverage, adj)
                Else
                    canvas.Save()
                    canvas.Translate(-offsetX, -offsetY)
                    DrawAnnotationOnCanvas(canvas, kind, renderAnnotation, rect, sourceWidth, sourceHeight)
                    canvas.Restore()
                End If
            Next
            Return drawn
        End Function

        ''' <summary>Zeichnet ein Objekt auf eine eigene transparente Ebene, laesst - falls vorhanden - die
        ''' Pixel-Anpassungen des Objekts darauf laufen und komponiert sie mit dem angegebenen Mischmodus
        ''' ein.</summary>

        ''' <summary>Verzerrt die fertig gezeichnete Ebene eines Objekts, bevor sie ins Bild kommt.
        '''
        ''' Der Weg ist derselbe wie beim Bild: fuer jeden Knoten eines Rasters wird die QUELLposition
        ''' bestimmt und das Ganze als Dreiecksnetz gezeichnet. Neu ist nur, dass das Raster hier ueber
        ''' der EBENE liegt, die Verzerrung aber am BILD eingestellt wurde - jeder Knoten muss also
        ''' erst in Bildkoordinaten gebracht, dort verschoben und wieder zurueckgerechnet werden.
        '''
        ''' Die Ebene WAECHST dabei: eine Verzerrung schiebt Bildpunkte nach aussen, und was ueber den
        ''' bisherigen Rand hinausgeht, waere sonst abgeschnitten. <paramref name="offsetX"/> und
        ''' <paramref name="offsetY"/> werden entsprechend nachgefuehrt.</summary>
        Friend Shared Function WarpObjectLayer(layer As SKBitmap, v As ObjectWarp,
                                                   imageWidth As Integer, imageHeight As Integer,
                                                   ByRef offsetX As Integer, ByRef offsetY As Integer) As SKBitmap
            If layer Is Nothing OrElse v Is Nothing OrElse v.IsEmpty Then Return Nothing
            If imageWidth <= 0 OrElse imageHeight <= 0 Then Return Nothing

            ' Die Anzeige wertet Freiform-Verzerrungen mit 48×48 Stützstellen aus. Der finale
            ' Objekt-Render muss dieselbe Feinheit verwenden, sonst bleiben gekrümmte Kanten beim
            ' Export sichtbar kantiger als in der Vorschau.
            Const Steps As Integer = 48
            Dim node = (Steps + 1) * (Steps + 1)

            ' Wie weit sich ein Punkt der Ebene verschiebt, in BILDkoordinaten. Erst einmal fuer die
            ' Ecken und den Rand, um zu wissen, wie weit die Ebene wachsen muss.
            Dim targetX(node - 1) As Single, targetY(node - 1) As Single
            Dim sourceX(node - 1) As Single, quellY(node - 1) As Single
            Dim minX = Double.MaxValue, minY = Double.MaxValue
            Dim maxX = Double.MinValue, maxY = Double.MinValue

            For rowIdx = 0 To Steps
                For colIdx = 0 To Steps
                    Dim i = rowIdx * (Steps + 1) + colIdx
                    ' Der Knoten in Ebenenkoordinaten, dann in Bildkoordinaten.
                    Dim ex = colIdx / CDbl(Steps) * layer.Width
                    Dim ey = rowIdx / CDbl(Steps) * layer.Height
                    Dim bx = offsetX + ex, by = offsetY + ey
                    Dim z = MovePoint(v, bx, by, imageWidth, imageHeight)
                    targetX(i) = CSng(z.X)
                    targetY(i) = CSng(z.Y)
                    sourceX(i) = CSng(ex)
                    quellY(i) = CSng(ey)
                    If z.X < minX Then minX = z.X
                    If z.X > maxX Then maxX = z.X
                    If z.Y < minY Then minY = z.Y
                    If z.Y > maxY Then maxY = z.Y
                Next
            Next
            If maxX <= minX OrElse maxY <= minY Then Return Nothing

            ' Ein Rand von einem Bildpunkt, damit die aeusserste Reihe nicht auf der Kante liegt.
            Dim neuX = CInt(Math.Floor(minX)) - 1
            Dim neuY = CInt(Math.Floor(minY)) - 1
            Dim neuB = CInt(Math.Ceiling(maxX)) - neuX + 2
            Dim neuH = CInt(Math.Ceiling(maxY)) - neuY + 2
            ' Eine Verzerrung, die eine Ebene um das Zwanzigfache aufblaeht, ist keine Verzerrung
            ' mehr, sondern ein Rechenfehler - dann lieber unveraendert lassen.
            If neuB <= 0 OrElse neuH <= 0 Then Return Nothing
            If neuB > layer.Width * 20 + 64 OrElse neuH > layer.Height * 20 + 64 Then Return Nothing

            For i = 0 To node - 1
                targetX(i) = CSng(targetX(i) - neuX)
                targetY(i) = CSng(targetY(i) - neuY)
            Next

            Dim result = ImageGeometryMapper.WarpOverGridTo(layer, neuB, neuH, Steps, Steps,
                                                                     targetX, targetY, sourceX, quellY)
            If result Is Nothing Then Return Nothing
            offsetX = neuX
            offsetY = neuY
            Return result
        End Function

        ''' <summary>Wohin ein Bildpunkt durch die Verzerrung wandert. Alle drei Arten an einer
        ''' Stelle, damit die Zuordnung Punkt zu Ziel nur einmal existiert.</summary>
        Private Shared Function MovePoint(v As ObjectWarp, bx As Double, by As Double,
                                                imageWidth As Integer, imageHeight As Integer) As SKPoint
            Select Case v.Kind
                Case "Perspektive"
                    ' Bilineare Abbildung des Einheitsquadrats auf das verzerrte Viereck. Genau genug
                    ' fuer ein Objekt und ohne die Sonderfaelle einer projektiven Matrix.
                    Dim u = bx / imageWidth, w = by / imageHeight
                    Dim e = v.Corners
                    Dim top = (e(0) + (e(2) - e(0)) * u, e(1) + (e(3) - e(1)) * u)
                    Dim bottom = (e(6) + (e(4) - e(6)) * u, e(7) + (e(5) - e(7)) * u)
                    Dim px = top.Item1 + (bottom.Item1 - top.Item1) * w
                    Dim py = top.Item2 + (bottom.Item2 - top.Item2) * w
                    Return New SKPoint(CSng(px / 100.0 * imageWidth), CSng(py / 100.0 * imageHeight))

                Case "Gitter"
                    ' Zwischen den vier umgebenden Stuetzpunkten interpolieren - ueber
                    ' ImageGeometryMapper.MeshPoint, also mit DERSELBEN Formel, mit der das Bild
                    ' gezeichnet wird (zwei Dreiecke je Masche). Hier stand sie ein drittes Mal und
                    ' dabei bilinear ueber die ganze Masche: die Ebene wurde damit anders verzogen,
                    ' als das Bild darunter gezeichnet wird, und am staerksten in den Maschenmitten.
                    ' Auffallen konnte das kaum, weil es auf den Knoten selbst uebereinstimmt.
                    '
                    ' Die zweite Eigenschaft, auf die es hier ankommt, bringt MeshPoint mit: es
                    ' KLEMMT NICHT auf 0..1. Der Bezugsrahmen ist bei der objekteigenen Verzerrung
                    ' das OBJEKTRECHTECK, und ein Objekt zeichnet regelmaessig darueber hinaus - ein
                    ' Text auf Pfad legt seine Grundlinie auf einen Kreis, die Zeichen stehen
                    ' senkrecht darauf. Eine Klemmung faltet alles ausserhalb auf die Rechteckkante,
                    ' und genau das war zu sehen: gemessen an einem Kreispfad-Text blieben von der
                    ' Tinte 25 Prozent uebrig, der Rest lag ausserhalb und wurde auf den Rand
                    ' gestaucht - bei einer Verzerrung, die gar nichts verschiebt. Ausserhalb wird
                    ' stattdessen ueber die Randzelle FORTGESETZT.
                    Return ImageGeometryMapper.MeshPoint(v.Nodes, v.Columns, v.Rows,
                                                         bx, by, imageWidth, imageHeight)

                Case "Linien"
                    Dim qp(v.LineSource.Length - 1) As Double
                    Dim zp(v.LineTarget.Length - 1) As Double
                    For i = 0 To qp.Length - 1
                        Dim istX = (i Mod 2) = 0
                        qp(i) = v.LineSource(i) / 100.0 * If(istX, imageWidth, imageHeight)
                        zp(i) = v.LineTarget(i) / 100.0 * If(istX, imageWidth, imageHeight)
                    Next
                    ' Das Feld sagt, WOHER ein Zielpunkt seine Farbe holt. Fuer ein Objekt brauchen
                    ' wir die Gegenrichtung - wohin ein Punkt wandert -, also werden Quelle und Ziel
                    ' vertauscht.
                    Return ImageGeometryMapper.LinePoint(bx, by, zp, qp)
            End Select
            Return New SKPoint(CSng(bx), CSng(by))
        End Function


        ''' <summary>Traegt dieses Objekt eine Verzerrung - eine eigene oder die des Bildes?
        '''
        ''' Entscheidet mit darueber, ob ueber eine eigene Ebene gezeichnet wird. Ohne diese Frage
        ''' nahm der direkte Weg jedes Objekt ohne Anpassungen und ohne Mischmodus - also die
        ''' allermeisten -, und der zeichnet unverzerrt: die Verzerrung stand im Objekt, kam im Bild
        ''' aber nie an.</summary>
        ''' Friend statt Private: der Kompositor-Grenzschnitt (OverlaySceneRenderer) braucht dieselbe
        ''' Frage - ein verzerrtes Objekt bleibt im gebackenen Block, weil der Objekt-Bitmap-Cache
        ''' die Verzerrung nicht zeichnet.
        Friend Shared Function HasWarp(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing Then Return False
            If annotation.OwnWarp IsNot Nothing AndAlso Not annotation.OwnWarp.IsEmpty Then Return True
            Return annotation.Warp IsNot Nothing AndAlso Not annotation.Warp.IsEmpty
        End Function

        ''' <summary>Zeichnet ein Objekt allein auf eine transparente Ebene und verzerrt sie.
        '''
        ''' Rueckgabe ist die fertige Ebene; der Aufrufer gibt sie frei. <paramref name="layerX"/>
        ''' und <paramref name="layerY"/> kommen als Lage der Ebene im Bild herein und gehen als ihre
        ''' NEUE Lage wieder hinaus - eine Verzerrung laesst die Ebene wachsen.
        '''
        ''' Ausgelagert, weil zwei Wege dasselbe brauchen: das Einkomponieren des Objekts und das
        ''' Abnehmen seines Alphas als Basis einer Schnittmaske. Zweimal gezeichnet heisst zweimal
        ''' dieselbe Reihenfolge - die haette sonst nur eine der beiden Stellen.</summary>
        Private Shared Function RenderAnnotationToLayer(annotation As ImageAnnotation,
                                                        renderAnnotation As ImageAnnotation, kind As String, rect As SKRect,
                                                        sourceWidth As Integer, sourceHeight As Integer,
                                                        layerWidth As Integer, layerHeight As Integer,
                                                        ByRef layerX As Integer, ByRef layerY As Integer,
                                                        Optional adj As ImageAdjustments = Nothing) As SKBitmap
            Dim layer = New SKBitmap(layerWidth, layerHeight, SKColorType.Rgba8888, SKAlphaType.Premul)
            Using layerCanvas = New SKCanvas(layer)
                layerCanvas.Clear(SKColors.Transparent)
                layerCanvas.Translate(-layerX, -layerY)
                DrawAnnotationOnCanvas(layerCanvas, kind, renderAnnotation, rect, sourceWidth, sourceHeight)
            End Using

            ' VERZERREN, bevor die Objektanpassungen greifen: die Verzerrung ist Geometrie und
            ' gehoert vor die Farbe, so wie beim Bild auch. Die Ebene kann dabei wachsen, deshalb
            ' wandert der Versatz mit.
            Dim drawn = layer
            Dim vx = layerX, vy = layerY

            ' ERST die eigene Verzerrung des Objekts, DANN die des Bildes. Die eigene beschreibt
            ' seine Form, die des Bildes, wo es im Bild liegt - in dieser Reihenfolge gelesen
            ' ergibt beides zusammen genau das, was man auf dem Schirm erwartet.
            ' Die eigene Verzerrung stammt aus der für Drehung/Flip in den Renderraum überführten
            ' Kopie. Der gespeicherte Datensatz selbst bleibt dabei im Objektkoordinatensystem.
            If renderAnnotation IsNot Nothing AndAlso renderAnnotation.OwnWarp IsNot Nothing AndAlso
               Not renderAnnotation.OwnWarp.IsEmpty Then
                ' Der Bezug ist das OBJEKTRECHTECK, nicht die Ebene: die eigene Verzerrung steht in
                ' Prozent DES OBJEKTS, die Ebene ist aber so gross wie die gerenderte Flaeche. Mit
                ' der Ebene als Bezug las eine kleine Verzerrung sich als eine ueber das ganze
                ' Bild - der Text landete weit neben seinem Rahmen.
                Dim rx = CInt(Math.Floor(rect.Left)), ry = CInt(Math.Floor(rect.Top))
                Dim rw = Math.Max(1, CInt(Math.Round(rect.Width)))
                Dim rh = Math.Max(1, CInt(Math.Round(rect.Height)))
                Dim ox = vx - rx, oy = vy - ry
                Dim warped = WarpObjectLayer(drawn, renderAnnotation.OwnWarp, rw, rh, ox, oy)
                If warped IsNot Nothing Then
                    If Not Object.ReferenceEquals(drawn, layer) Then drawn.Dispose()
                    drawn = warped
                    ' Der zurueckgegebene Versatz liegt im Raum des Rechtecks und muss zurueck in
                    ' Bildkoordinaten.
                    vx = rx + ox
                    vy = ry + oy
                End If
            End If

            If annotation IsNot Nothing AndAlso annotation.Warp IsNot Nothing AndAlso
               Not annotation.Warp.IsEmpty Then
                ' Die Bildverzerrung steht in Prozent des UNBESCHNITTENEN Quellbilds, die Ebene
                ' liegt aber im Ausgaberaum nach der Geometrie. Ohne die Umrechnung liefen Objekte
                ' und Bild bei Beschnitt oder Vierteldrehung sichtbar auseinander.
                Dim effective = MapImageWarpToOutput(annotation.Warp, adj, sourceWidth, sourceHeight)
                Dim warped = WarpObjectLayer(drawn, effective, sourceWidth, sourceHeight, vx, vy)
                If warped IsNot Nothing Then
                    If Not Object.ReferenceEquals(drawn, layer) Then drawn.Dispose()
                    drawn = warped
                End If
            End If

            If Not Object.ReferenceEquals(drawn, layer) Then layer.Dispose()
            layerX = vx
            layerY = vy
            Return drawn
        End Function

        ''' <summary>Bringt die BILD-Verzerrung eines Objekts (Knoten in Prozent des unbeschnittenen
        ''' Quellbilds) in den AUSGABEraum, in dem die Objektebene gerendert wird.
        '''
        ''' Das Bild selbst laeuft ApplyImageWarp als ERSTE Stufe im Quellraum und wird danach
        ''' beschnitten, skaliert und gedreht. Die Objektebenen entstehen dagegen erst NACH der
        ''' Geometrie im Ausgaberaum - dasselbe Knotenraster dort unveraendert anzuwenden hiesse,
        ''' Quell-Prozent als Ausgabe-Prozent zu lesen, und mit Beschnitt oder Vierteldrehung
        ''' liefen Objekte und Bild auseinander. Umgerechnet wird durch ABTASTEN: je Knoten des
        ''' neuen, gleichmaessigen Ausgaberasters wird sein Quellpunkt bestimmt, durch das alte
        ''' Raster geschickt und zurueck in die Ausgabe gelegt. Die Kette ist dieselbe Teilmenge
        ''' der Geometrie, die auch die Objekte selbst durchlaufen (Beschnitt, Skalierung,
        ''' Vierteldrehung, Spiegelung - KEIN Begradigen und keine Perspektive, siehe
        ''' TransformAnnotationForGeometry).</summary>
        Private Shared Function MapImageWarpToOutput(warp As ObjectWarp, adj As ImageAdjustments,
                                                     outputWidth As Integer, outputHeight As Integer) As ObjectWarp
            If warp Is Nothing OrElse warp.IsEmpty Then Return warp
            If Not String.Equals(warp.Kind, "Gitter", StringComparison.Ordinal) Then Return warp
            If adj Is Nothing OrElse adj.SourceWidthPixels <= 0 OrElse adj.SourceHeightPixels <= 0 Then Return warp
            If outputWidth <= 0 OrElse outputHeight <= 0 Then Return warp

            Dim rotation = ImageGeometryMapper.NormalizeQuarterTurn(adj.RotationDegrees)
            Dim crop = ComputeGeometryCropRect(adj.SourceWidthPixels, adj.SourceHeightPixels, adj)
            If crop.Width <= 0 OrElse crop.Height <= 0 Then Return warp
            Dim preWidth = If(rotation = 90 OrElse rotation = 270, outputHeight, outputWidth)
            Dim preHeight = If(rotation = 90 OrElse rotation = 270, outputWidth, outputHeight)
            If preWidth <= 0 OrElse preHeight <= 0 Then Return warp

            ' Ohne wirksame Geometrie ist der Quellraum der Ausgaberaum - nichts umzurechnen.
            If rotation = 0 AndAlso Not adj.FlipHorizontal AndAlso Not adj.FlipVertical AndAlso
               crop.Left = 0 AndAlso crop.Top = 0 AndAlso
               crop.Width = adj.SourceWidthPixels AndAlso crop.Height = adj.SourceHeightPixels AndAlso
               preWidth = crop.Width AndAlso preHeight = crop.Height Then
                Return warp
            End If

            Dim sx = preWidth / CDbl(crop.Width), sy = preHeight / CDbl(crop.Height)
            Dim m = ImageGeometryMapper.SourceToDisplayMatrix(preWidth, preHeight, rotation,
                                                              adj.FlipHorizontal, adj.FlipVertical)
            Dim inverse As SKMatrix
            If Not m.TryInvert(inverse) Then Return warp

            Dim result = New ObjectWarp With {
                .Kind = "Gitter", .Columns = warp.Columns, .Rows = warp.Rows,
                .Nodes = New Double((warp.Columns + 1) * (warp.Rows + 1) * 2 - 1) {}}
            For rowIdx = 0 To warp.Rows
                For colIdx = 0 To warp.Columns
                    Dim i = (rowIdx * (warp.Columns + 1) + colIdx) * 2
                    ' Ausgabeknoten -> Quellpunkt -> durch das Knotenraster -> zurueck in die Ausgabe.
                    Dim pre = inverse.MapPoint(New SKPoint(CSng(colIdx / CDbl(warp.Columns) * outputWidth),
                                                           CSng(rowIdx / CDbl(warp.Rows) * outputHeight)))
                    Dim srcX = pre.X / sx + crop.Left
                    Dim srcY = pre.Y / sy + crop.Top
                    Dim moved = ImageGeometryMapper.MeshPoint(warp.Nodes, warp.Columns, warp.Rows,
                                                              srcX, srcY,
                                                              adj.SourceWidthPixels, adj.SourceHeightPixels)
                    Dim back = m.MapPoint(New SKPoint(CSng((moved.X - crop.Left) * sx),
                                                      CSng((moved.Y - crop.Top) * sy)))
                    result.Nodes(i) = back.X / outputWidth * 100.0
                    result.Nodes(i + 1) = back.Y / outputHeight * 100.0
                Next
            Next
            Return result
        End Function

        ''' <summary>Multipliziert das Alpha einer fertigen Objektebene mit einer Deckung.
        '''
        ''' Die Ebene liegt bei <paramref name="layerX"/>/<paramref name="layerY"/> IM GITTER der
        ''' Deckung (nicht im Bild): eine Verzerrung kann sie ueber den Rand hinaus haben wachsen
        ''' lassen, und was dort liegt, hat keine Deckung mehr und faellt weg.
        '''
        ''' Gerechnet wird auf ALLEN VIER Kanaelen: die Ebene ist vormultipliziert, ein Alpha allein
        ''' zu senken ergaebe ungueltige Pixel und beim Mischen helle Saeume.</summary>
        Private Shared Sub ApplyCoverageToLayer(layer As SKBitmap, layerX As Integer, layerY As Integer,
                                                coverage As Byte(), coverageWidth As Integer, coverageHeight As Integer)
            If layer Is Nothing OrElse coverage Is Nothing Then Return
            If coverageWidth <= 0 OrElse coverageHeight <= 0 Then Return
            Dim stride = layer.RowBytes
            Dim raw = New Byte(stride * layer.Height - 1) {}
            Marshal.Copy(layer.GetPixels(), raw, 0, raw.Length)
            For y = 0 To layer.Height - 1
                Dim cy = layerY + y
                Dim rowIn = y * stride
                If cy < 0 OrElse cy >= coverageHeight Then
                    Array.Clear(raw, rowIn, layer.Width * 4)
                    Continue For
                End If
                Dim rowCoverage = cy * coverageWidth
                For x = 0 To layer.Width - 1
                    Dim cx = layerX + x
                    Dim o = rowIn + x * 4
                    If cx < 0 OrElse cx >= coverageWidth Then
                        raw(o) = 0 : raw(o + 1) = 0 : raw(o + 2) = 0 : raw(o + 3) = 0
                        Continue For
                    End If
                    Dim c = CInt(coverage(rowCoverage + cx))
                    If c = 255 Then Continue For
                    If c = 0 Then
                        raw(o) = 0 : raw(o + 1) = 0 : raw(o + 2) = 0 : raw(o + 3) = 0
                    Else
                        raw(o) = CByte(raw(o) * c \ 255)
                        raw(o + 1) = CByte(raw(o + 1) * c \ 255)
                        raw(o + 2) = CByte(raw(o + 2) * c \ 255)
                        raw(o + 3) = CByte(raw(o + 3) * c \ 255)
                    End If
                Next
            Next
            Marshal.Copy(raw, 0, layer.GetPixels(), raw.Length)
        End Sub

        Private Shared Sub DrawAnnotationViaLayer(canvas As SKCanvas, annotation As ImageAnnotation,
                                                  renderAnnotation As ImageAnnotation, kind As String, rect As SKRect,
                                                  sourceWidth As Integer, sourceHeight As Integer,
                                                  layerWidth As Integer, layerHeight As Integer,
                                                  offsetX As Integer, offsetY As Integer,
                                                  blendModeName As String,
                                                  Optional coverage As Byte() = Nothing,
                                                  Optional adj As ImageAdjustments = Nothing)
            Dim vx = offsetX, vy = offsetY
            Dim drawn = RenderAnnotationToLayer(annotation, renderAnnotation, kind, rect,
                                                sourceWidth, sourceHeight, layerWidth, layerHeight, vx, vy, adj)
            If drawn Is Nothing Then Return
            Try
                ' Die Deckung kommt NACH den Objektanpassungen: eine Weichzeichnung oder ein
                ' Filter soll die vollen Objektpixel sehen und nicht den abgeschnittenen Rest -
                ' sonst zoege die Maskenkante ihre eigene Unschaerfe ins Bild.
                If HasObjectAdjustments(annotation) Then
                    Dim objectAdj = annotation.Adjustments.ExtractPixelAdjustments()
                    objectAdj.SourceWidthPixels = drawn.Width
                    objectAdj.SourceHeightPixels = drawn.Height
                    Using processedLayer = ProcessBitmapBase(drawn, objectAdj)
                        ApplyCoverageToLayer(processedLayer, vx - offsetX, vy - offsetY, coverage, layerWidth, layerHeight)
                        DrawAnnotationLayerAt(canvas, processedLayer, blendModeName, vx - offsetX, vy - offsetY)
                    End Using
                Else
                    ApplyCoverageToLayer(drawn, vx - offsetX, vy - offsetY, coverage, layerWidth, layerHeight)
                    DrawAnnotationLayerAt(canvas, drawn, blendModeName, vx - offsetX, vy - offsetY)
                End If
            Finally
                drawn.Dispose()
            End Try
        End Sub

        Private Const TransparentColorHex As String = "#00000000"

        ''' <summary>Objekte, bei denen sich Fuellung und Kontur getrennt zeichnen lassen. Ausgenommen sind
        ''' Arten, deren Inhalt nicht die "Fuellung" ist (Bild, QR-Code, SVG, Wasserzeichen mit Bild - die
        ''' kaemen sonst doppelt), reine Kontur-Arten (Linie, Pfeil, Spirale - da bliebe zum Mischen nichts
        ''' uebrig) und Pinselstriche. Ohne sichtbare Kontur ist das Aufteilen ohnehin sinnlos.</summary>
        Private Shared Function SplitsStrokeFromBlend(renderAnnotation As ImageAnnotation, kind As String) As Boolean
            If renderAnnotation Is Nothing OrElse renderAnnotation.BlendIncludesStroke Then Return False
            If IsNormalAnnotationBlendMode(renderAnnotation.BlendMode) Then Return False
            If renderAnnotation.StrokeWidth <= 0 Then Return False
            If ParseColor(renderAnnotation.StrokeColor, SKColors.Black).Alpha = 0 Then Return False
            Select Case kind
                Case "image", "selectionimage", "qr", "qrcode", "qr-code", "svg", "watermark",
                     "line", "arrow", "spiral", "brush", "eraser"
                    Return False
                Case Else
                    Return True
            End Select
        End Function

        ''' Fuellung ohne Kontur bzw. Kontur ohne Fuellung: statt jede der Zeichenroutinen um einen Schalter
        ''' zu erweitern, wird mit durchsichtig gesetzter Kontur- bzw. Fuellfarbe gezeichnet.
        Private Shared Function AnnotationFillOnly(source As ImageAnnotation) As ImageAnnotation
            Dim clone = source.Clone()
            clone.StrokeColor = TransparentColorHex
            Return clone
        End Function

        Private Shared Function AnnotationStrokeOnly(source As ImageAnnotation) As ImageAnnotation
            Dim clone = source.Clone()
            clone.FillColor = TransparentColorHex
            clone.FillColor2 = TransparentColorHex
            ' Schatten und Gluehen gehoeren zur Silhouette und liegen bereits unter der gemischten
            ' Fuellung - ein zweites Mal gezeichnet wuerden sie sich selbst verdoppeln.
            clone.ShadowEnabled = False
            clone.GlowEnabled = False
            Return clone
        End Function

        ''' <summary>Wie <see cref="DrawAnnotationLayer"/>, aber mit Versatz - eine verzerrte Ebene
        ''' ist groesser als die urspruengliche und faengt weiter oben links an.</summary>
        Private Shared Sub DrawAnnotationLayerAt(canvas As SKCanvas, layer As SKBitmap,
                                                 blendModeName As String, dx As Integer, dy As Integer)
            If dx = 0 AndAlso dy = 0 Then
                DrawAnnotationLayer(canvas, layer, blendModeName)
                Return
            End If
            Using paint = New SKPaint With {.BlendMode = ResolveAnnotationBlendMode(blendModeName), .IsAntialias = True}
                canvas.DrawBitmap(layer, dx, dy, paint)
            End Using
        End Sub

        Private Shared Sub DrawAnnotationLayer(canvas As SKCanvas, layer As SKBitmap, blendModeName As String)
            Using paint = New SKPaint With {.BlendMode = ResolveAnnotationBlendMode(blendModeName), .IsAntialias = True}
                canvas.DrawBitmap(layer, 0, 0, paint)
            End Using
        End Sub

        Private Shared Function IsNormalAnnotationBlendMode(blendModeName As String) As Boolean
            Return ResolveAnnotationBlendMode(blendModeName) = SKBlendMode.SrcOver
        End Function

        Private Shared Function ResolveAnnotationBlendMode(blendModeName As String) As SKBlendMode
            Select Case If(blendModeName, "Normal").Trim().ToLowerInvariant()
                Case "multiply" : Return SKBlendMode.Multiply
                Case "screen" : Return SKBlendMode.Screen
                Case "overlay" : Return SKBlendMode.Overlay
                Case "darken" : Return SKBlendMode.Darken
                Case "lighten" : Return SKBlendMode.Lighten
                Case "colordodge" : Return SKBlendMode.ColorDodge
                Case "colorburn" : Return SKBlendMode.ColorBurn
                Case "hardlight" : Return SKBlendMode.HardLight
                Case "softlight" : Return SKBlendMode.SoftLight
                Case "difference" : Return SKBlendMode.Difference
                Case "exclusion" : Return SKBlendMode.Exclusion
                Case "plus" : Return SKBlendMode.Plus
                Case "hue" : Return SKBlendMode.Hue
                Case "saturation" : Return SKBlendMode.Saturation
                Case "color" : Return SKBlendMode.Color
                Case "luminosity" : Return SKBlendMode.Luminosity
                Case Else : Return SKBlendMode.SrcOver
            End Select
        End Function

        ' Zeichnet ein einzelnes Objekt anhand seiner Art (Kind) - wird sowohl für das normale
        ' Zeichnen als auch (auf einer separaten Offscreen-Maske) für Schatten/Glow in
        ' DrawAnnotationEffects wiederverwendet, damit beide Pfade exakt dieselbe Silhouette ergeben.
        Private Shared Sub DrawAnnotationShape(canvas As SKCanvas, kind As String, annotation As ImageAnnotation, rect As SKRect, x As Single, y As Single, maxWidth As Single, fontSize As Single, fill As SKColor, stroke As SKColor, strokeWidth As Single, alphaFactor As Single)
            Select Case kind
                Case "frame"
                    ' Der Rahmen sitzt am Rechteck des Objekts, und das ist beim Rahmen immer das
                    ' ganze Bild (siehe ComputeAnnotationRect) - er bleibt damit an der Bildkante,
                    ' auch wenn spaeter zugeschnitten oder die Leinwand geaendert wird.
                    Dim frameFill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawFrameOnCanvas(canvas, rect, annotation.FrameSizePercent / 100.0F, fill,
                                      annotation.FrameCornerRadiusPercent / 100.0F, annotation.FrameEffect,
                                      annotation.FillKind, frameFill2,
                                      annotation.GradientAngleDegrees, annotation.GradientInverted,
                                      annotation.FrameSymbol, annotation.FrameSymbolSpacingPercent,
                                      annotation.FrameSymbolRotate, stroke, annotation.StrokeWidth)
                Case "rectangle", "rect", "selectionfill"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawShape(canvas, rect, fill, stroke, strokeWidth, False, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "roundedrectangle", "rounded-rectangle"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawRoundedRectangle(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "ellipse", "circle"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawShape(canvas, rect, fill, stroke, strokeWidth, True, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "square"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawSquare(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "triangle"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawTriangle(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "cone"
                    DrawCone(canvas, rect, fill, stroke, strokeWidth)
                Case "pyramid"
                    DrawPyramid(canvas, rect, fill, stroke, strokeWidth)
                Case "trapezoid"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawTrapezoid(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "diamond"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawDiamond(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "polygon"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawRegularPolygon(canvas, rect, 6, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "star"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawStar(canvas, rect, 5, 0.45F, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "doublestar", "double-star"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawStar(canvas, rect, 8, 0.42F, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "spiral"
                    DrawSpiral(canvas, rect, stroke, strokeWidth)
                Case "droplet"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawDroplet(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "ellipsespeechbubble", "ellipse-speech-bubble"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawEllipseSpeechBubble(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "rectspeechbubble", "rect-speech-bubble"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawRectSpeechBubble(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "speechbubble", "speech-bubble", "bubble"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawSpeechBubble(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "heart"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawHeart(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "cloud"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawCloud(canvas, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "path"
                    ' Der freie Pfad geht durch DIESELBE Fuell- und Konturroutine wie jede andere
                    ' Form - Verlauf, Schatten, Leuchten und Mischmethode gelten damit unveraendert.
                    Dim pathFill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    Using freePath = BuildFreePath(rect, annotation.PathPoints, annotation.PathClosed)
                        If freePath IsNot Nothing Then
                            DrawClosedPath(canvas, freePath, fill, stroke, strokeWidth, rect,
                                           annotation.FillKind, pathFill2,
                                           annotation.GradientAngleDegrees, annotation.GradientInverted)
                        End If
                    End Using
                Case "line"
                    DrawLine(canvas, rect, stroke, strokeWidth, False)
                Case "arrow"
                    DrawLine(canvas, rect, stroke, strokeWidth, True)
                Case "symbol"
                    DrawSingleGlyph(canvas, If(String.IsNullOrWhiteSpace(annotation.Text), "★", annotation.Text), rect, fill, stroke, annotation.StrokeWidth, annotation.FontFamily)
                Case "qr", "qrcode", "qr-code"
                    ' Beim QR-Code ist FillColor die Hintergrundfarbe, StrokeColor die Modulfarbe.
                    DrawQrCode(canvas, If(String.IsNullOrWhiteSpace(annotation.Text), "FerrumPix", annotation.Text), rect, stroke, fill)
                Case "image", "selectionimage"
                    ' Ohne Seitenverhaeltnis-Sperre wird das Bild auf die Objekt-Box gestreckt.
                    DrawImageAnnotation(canvas, annotation.ImagePath, rect, annotation.Opacity, stroke, annotation.StrokeWidth, stretchToFill:=(kind = "selectionimage" OrElse Not annotation.LockAspect))
                Case "svg"
                    Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                    DrawSvgAnnotation(canvas, annotation.ImagePath, rect, fill, stroke, strokeWidth, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted)
                Case "watermark"
                    If Not String.IsNullOrWhiteSpace(annotation.ImagePath) Then
                        DrawImageAnnotation(canvas, annotation.ImagePath, rect, annotation.Opacity, stroke, annotation.StrokeWidth, stretchToFill:=Not annotation.LockAspect)
                    Else
                        Dim watermark = If(String.IsNullOrWhiteSpace(annotation.Text), "FerrumPix", annotation.Text)
                        Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                        ' Pfad-Parameter durchreichen wie beim normalen Text: der Renderer kann
                        ' das laengst, hier wurden sie nur nicht weitergegeben - das Wasserzeichen
                        ' blieb dadurch immer gerade.
                        DrawAnnotationText(canvas, watermark, x, y, maxWidth, fontSize, WithAlpha(fill, If(fill.Alpha = 255, CByte(130), fill.Alpha)), stroke, annotation.StrokeWidth, annotation.FontFamily, rect, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted, annotation.TextPathKind, annotation.TextPathBend, annotation.TextPathStartOffset, annotation.LetterSpacingPercent, annotation.Bold, annotation.Italic, annotation.PathPoints, annotation.PathClosed)
                    End If
                Case Else
                    If Not String.IsNullOrWhiteSpace(annotation.Text) Then
                        Dim fill2 = ApplyAlpha(ParseColor(annotation.FillColor2, SKColors.White), alphaFactor)
                        DrawAnnotationText(canvas, annotation.Text, x, y, maxWidth, fontSize, fill, stroke, annotation.StrokeWidth, annotation.FontFamily, rect, annotation.FillKind, fill2, annotation.GradientAngleDegrees, annotation.GradientInverted, annotation.TextPathKind, annotation.TextPathBend, annotation.TextPathStartOffset, annotation.LetterSpacingPercent, annotation.Bold, annotation.Italic, annotation.PathPoints, annotation.PathClosed)
                    End If
            End Select
        End Sub

        ' Rendert das Objekt einmal auf eine transparente Offscreen-Maske derselben Canvas-Größe,
        ' färbt diese Silhouette per SKBlendMode.SrcIn auf Schatten-/Glow-Farbe um, blurrt sie und
        ' komponiert sie (versetzt bzw. additiv) VOR dem eigentlichen Objekt auf den Haupt-Canvas.
        ''' Die Maske wird nur so groß wie nötig (Objekt-Bounds + Blur/Versatz-Rand) statt
        ''' bildschirmfüllend angelegt - bei größeren Fotos und häufigen Live-Neuzeichnungen
        ''' (z.B. während des Verschiebens per Slider) spart das pro Aufruf eine große
        ''' Bitmap-Allokation samt Blur über die gesamte Canvas. Bei rotierten Objekten (der
        ''' Canvas hat zu diesem Zeitpunkt schon die RotateDegrees-Transformation aktiv, siehe
        ''' ApplyAnnotations) ist die tatsächliche Bildschirm-Bounding-Box in unrotierten
        ''' rect-Koordinaten nicht trivial zu bestimmen - dort bleibt es beim sicheren,
        ''' bildschirmfüllenden Fallback.
        ''' <summary>Grenzen, in denen das Glühen gerechnet wird (siehe DrawAnnotationEffects). Die Kosten
        ''' hängen an FLÄCHE × RADIUS, deshalb müssen beide gedeckelt werden: Skias Dilate kostet linear im
        ''' Radius, und der Radius wächst mit der Objektgröße - aber auch ein kleiner Radius auf einer sehr
        ''' großen Maske ist teuer. Was darüber liegt, wird verkleinert gerechnet und wieder hochgezogen.</summary>
        ''' <summary>Regler "Weichzeichnen" (0-100) -> Gauss-Sigma, bemessen an der Objektgroesse.
        ''' Der Faktor lag bei 0.6, solange die Weichzeichnung wirkungslos war (MaskFilter bei
        ''' DrawBitmap, siehe unten) - er war nie an einem sichtbaren Ergebnis geeicht. Mit wirksamem
        ''' Blur war damit schon der Standardwert 6 stark verwaschen und die obere Haelfte des Reglers
        ''' loeste den Schatten vollstaendig auf. Erst 0.15, dann 0.075: gemessen wurde nicht die
        ''' Deckung in der Schattenmitte (die bleibt hoch), sondern der SICHTBARE Saum neben dem
        ''' Objekt - und dessen Kontrast steckt fast ganz in der ersten Reglerhaelfte (Deckung 254 bei
        ''' Regler 10, 177 bei 50, danach nur noch 161/153). Groesseres Sigma verteilt den Schatten
        ''' dann bloss breiter, statt ihn sichtbar zu veraendern. Mit 0.075 liegt das alte Verhalten
        ''' bei Regler 50 am Ende des Reglers, und der ganze Weg ist nutzbar
        ''' ("macht nur bis zur Haelfte Sinn, danach ist der Schatten quasi weg").</summary>
        Private Const ShadowBlurSigmaFactor As Single = 0.075F

        Private Const MaxGlowDilatePx As Single = 12.0F
        Private Const MaxGlowDim As Single = 512.0F

        Private Shared Sub DrawAnnotationEffects(canvas As SKCanvas, kind As String, annotation As ImageAnnotation, rect As SKRect, x As Single, y As Single, maxWidth As Single, fontSize As Single, fill As SKColor, stroke As SKColor, strokeWidth As Single, alphaFactor As Single, canvasWidth As Integer, canvasHeight As Integer)
            ' Bewusst relativ zur Objekt-Bounding-Box (nicht zur ganzen Canvas wie bei RetouchRadius/
            ' BrushSize) skaliert: bei kleinem Text auf einem großen Foto wäre ein an der Canvas-Größe
            ' bemessener Blur-Radius so riesig, dass er sich fast unsichtbar verwaschen würde (genau das
            ' Problem "Glow hat bei Text keine Auswirkung") - Text-/Objektgröße variiert unabhängig von
            ' der Fotoauflösung, der Effekt soll aber immer proportional zum jeweiligen Objekt wirken.
            ' Skalierungsfaktor 0.4 (vormals 0.12): bei 0.12 blieb der Blur-Radius bei üblichen
            ' Slider-Werten (Default Glow=10, Shadow=6) so klein (wenige Zehntel-Prozent der
            ' Objektgröße), dass er komplett unter dem später deckend gezeichneten Objekt
            ' verschwand - "Glow wirkungslos"/"Shadow-Stärke ohne Auswirkung".
            Dim objSize = Math.Max(1.0F, Math.Min(rect.Width, rect.Height))
            ' Glühen als echtes AUSSEN-Glühen: die Silhouette wird per Dilate nach außen vergrößert und
            ' erst danach weich gezeichnet. Ein reiner (großer) Gauß-Blur verteilt die Glow-Energie so
            ' dünn, dass außerhalb des Objekts fast nichts sichtbar bleibt ("Glühen bleibt in den
            ' Objektgrenzen") - mit Dilate reicht das Glühen sichtbar und deckend über die Kante hinaus.
            Dim glowReach = objSize * Clamp(annotation.GlowBlur, 0, 100) / 100.0F * 1.5F
            Dim glowDilate = Math.Max(0, CInt(Math.Round(glowReach * 0.5F)))
            Dim glowSigma = Math.Max(0.1F, glowReach * 0.17F)
            Dim glowMaskReach = glowDilate + 3.0F * glowSigma
            Dim shadowBlurPx = objSize * Clamp(annotation.ShadowBlur, 0, 100) / 100.0F * ShadowBlurSigmaFactor
            Dim offsetX = objSize * annotation.ShadowOffsetXPercent / 100.0F
            Dim offsetY = objSize * annotation.ShadowOffsetYPercent / 100.0F

            Dim maskLeft As Integer = 0
            Dim maskTop As Integer = 0
            Dim maskWidth = canvasWidth
            Dim maskHeight = canvasHeight
            If Math.Abs(annotation.RotationDegrees) <= 0.01F Then
                Dim pad = Math.Max(glowMaskReach, shadowBlurPx * 3.0F) + Math.Max(Math.Abs(offsetX), Math.Abs(offsetY)) + 4.0F
                ' Text an Pfad: die Glyphen ragen bis zu einer Schrifthoehe ueber das
                ' Layout-Rechteck hinaus. Ohne den Zusatzrand beschneidet der Masken-Ausschnitt
                ' die Silhouette - Schatten/Gluehen fehlten an den Enden des gebogenen Textes
                ' bzw. brachen hart ab.
                If Not String.IsNullOrWhiteSpace(annotation.TextPathKind) Then
                    pad += annotation.FontSizePixels * ComputeTextPathFitRatio(annotation) * 1.2F
                End If
                maskLeft = Math.Max(0, CInt(Math.Floor(rect.Left - pad)))
                maskTop = Math.Max(0, CInt(Math.Floor(rect.Top - pad)))
                Dim maskRight = Math.Min(canvasWidth, CInt(Math.Ceiling(rect.Right + pad)))
                Dim maskBottom = Math.Min(canvasHeight, CInt(Math.Ceiling(rect.Bottom + pad)))
                maskWidth = Math.Max(1, maskRight - maskLeft)
                maskHeight = Math.Max(1, maskBottom - maskTop)
            End If

            Using mask = New SKBitmap(maskWidth, maskHeight, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using maskCanvas = New SKCanvas(mask)
                    maskCanvas.Clear(SKColors.Transparent)
                    maskCanvas.Translate(-maskLeft, -maskTop)
                    DrawAnnotationShape(maskCanvas, kind, annotation, rect, x, y, maxWidth, fontSize, fill, stroke, strokeWidth, alphaFactor)
                End Using

                If annotation.GlowEnabled Then
                    Dim glowColor = ApplyAlpha(ParseColor(annotation.GlowColor, SKColors.Yellow), alphaFactor * Clamp(annotation.GlowStrength, 0, 100) / 100.0F)

                    ' Das Glühen wird in KLEINERER Auflösung gerechnet und danach hochskaliert. Grund: Skias
                    ' Dilate kostet linear im Radius, und der Radius hängt an der Objektgröße - ein großer
                    ' Text mit vollem Glühen kam auf Radius 180 und brauchte über zehn Sekunden PRO Render,
                    ' bei jedem Reglertick neu. Das Ergebnis ist ohnehin ein weichgezeichneter Klumpen
                    ' (Dilate + Gauß) und enthält keine hohen Frequenzen, die beim Verkleinern verlorengehen
                    ' könnten: klein gerechnet und wieder hochgezogen sieht es genauso aus - nur schnell.
                    Dim glowScale = 1.0F
                    If glowDilate > MaxGlowDilatePx Then glowScale = MaxGlowDilatePx / CSng(glowDilate)
                    Dim longestSide = CSng(Math.Max(maskWidth, maskHeight))
                    If longestSide > MaxGlowDim Then glowScale = Math.Min(glowScale, MaxGlowDim / longestSide)
                    Dim glowW = Math.Max(1, CInt(Math.Round(maskWidth * glowScale)))
                    Dim glowH = Math.Max(1, CInt(Math.Round(maskHeight * glowScale)))
                    Dim scaledDilate = Math.Max(0, CInt(Math.Round(glowDilate * glowScale)))
                    Dim scaledSigma = Math.Max(0.1F, glowSigma * glowScale)

                    Using smallMask = New SKBitmap(glowW, glowH, SKColorType.Rgba8888, SKAlphaType.Premul)
                        Using smallCanvas = New SKCanvas(smallMask)
                            smallCanvas.Clear(SKColors.Transparent)
                            Using scalePaint = New SKPaint With {.IsAntialias = True}
                                DrawBitmapSampled(smallCanvas, mask,
                                                  New SKRect(0, 0, maskWidth, maskHeight),
                                                  New SKRect(0, 0, glowW, glowH), SamplingHigh, scalePaint)
                            End Using
                        End Using

                        ' Silhouette einfärben -> nach außen vergrößern (Dilate) -> weichzeichnen. Als
                        ' verkettete ImageFilter, damit das Glühen sichtbar über die Objektkante hinausreicht.
                        Using glowSmall = New SKBitmap(glowW, glowH, SKColorType.Rgba8888, SKAlphaType.Premul)
                            Using glowCanvas = New SKCanvas(glowSmall)
                                glowCanvas.Clear(SKColors.Transparent)
                                Using glowColorFilter = SKColorFilter.CreateBlendMode(glowColor, SKBlendMode.SrcIn)
                                    Using coloredFilter = SKImageFilter.CreateColorFilter(glowColorFilter)
                                        Dim spreadFilter As SKImageFilter = coloredFilter
                                        Dim dilatedOwned As SKImageFilter = Nothing
                                        Try
                                            If scaledDilate > 0 Then
                                                dilatedOwned = SKImageFilter.CreateDilate(scaledDilate, scaledDilate, coloredFilter)
                                                spreadFilter = dilatedOwned
                                            End If
                                            Using glowImageFilter = SKImageFilter.CreateBlur(scaledSigma, scaledSigma, spreadFilter)
                                                Using paint = New SKPaint With {.ImageFilter = glowImageFilter}
                                                    glowCanvas.DrawBitmap(smallMask, 0, 0, paint)
                                                End Using
                                            End Using
                                        Finally
                                            dilatedOwned?.Dispose()
                                        End Try
                                    End Using
                                End Using
                            End Using

                            ' Bewusst SrcOver statt additiv (Plus): Das Overlay zeichnet das Glühen auf
                            ' Transparenz (Plus und SrcOver liefern dort dasselbe) und blendet es dann per
                            ' SrcOver übers Foto - beim gebackenen Bild würde Plus die Glow-Farbe hingegen
                            ' aufs Foto ADDIEREN und dadurch auswaschen. SrcOver macht beide Pfade gleich kräftig.
                            Using paint = New SKPaint With {.BlendMode = SKBlendMode.SrcOver, .IsAntialias = True}
                                DrawBitmapSampled(canvas, glowSmall,
                                                  New SKRect(0, 0, glowW, glowH),
                                                  New SKRect(maskLeft, maskTop, maskLeft + maskWidth, maskTop + maskHeight),
                                                  SamplingHigh, paint)
                            End Using
                        End Using
                    End Using
                End If

                If annotation.ShadowEnabled Then
                    Dim shadowColor = ApplyAlpha(ParseColor(annotation.ShadowColor, New SKColor(0, 0, 0, 128)), alphaFactor * Clamp(annotation.ShadowStrength, 0, 100) / 100.0F)
                    Dim shadowSource = mask
                    Dim roundedShadowMask As SKBitmap = Nothing
                    Try
                        ' Abgerundeter Schatten rundet die ECKEN DER SILHOUETTE ab (siehe
                        ' BuildRoundedSilhouette) - die Objektform bleibt erhalten. Der Glow-Effekt
                        ' bleibt davon unberührt und folgt weiter der ungerundeten Form.
                        If annotation.ShadowRounded Then
                            Dim cornerRadius = Math.Min(rect.Width, rect.Height) / 2.0F * Clamp(annotation.ShadowCornerRadiusPercent, 0, 100) / 100.0F
                            roundedShadowMask = BuildRoundedSilhouette(mask, cornerRadius)
                            If roundedShadowMask IsNot Nothing Then shadowSource = roundedShadowMask
                        End If

                        ' Schattengröße: um die Objektmitte skalieren, sodass der Schatten über das Objekt
                        ' hinauswachsen (oder schrumpfen) kann. Der Blur-Radius wird durch den Skalierungs-
                        ' faktor geteilt, weil die anschließende Canvas-Skalierung ihn wieder hochmultipliziert -
                        ' so bleibt die Weichzeichnung unabhängig von der gewählten Größe.
                        Dim shadowScale = Clamp(annotation.ShadowSizePercent, 10, 400) / 100.0F
                        Dim shadowSigma = Math.Max(0.1F, shadowBlurPx / shadowScale)
                        ' Weichzeichnung MUSS hier ein ImageFilter sein, kein MaskFilter: der Schatten
                        ' wird per DrawBitmap gezeichnet, und ein MaskFilter wirkt nur auf die Deckung
                        ' gezeichneter GEOMETRIE - bei DrawBitmap tut er schlicht nichts (gemessen mit
                        ' SkiaSharp 3.119, sigma 4/12/30 alle unveraendert hart). Genau daran lag der
                        ' Befund "Schatten hat keine weiche Kante": der Weichzeichnen-Regler war
                        ' wirkungslos, der Schatten immer eine harte Silhouette.
                        Using shadowColorFilter = SKColorFilter.CreateBlendMode(shadowColor, SKBlendMode.SrcIn)
                            Using shadowImageFilter = SKImageFilter.CreateBlur(shadowSigma, shadowSigma)
                                Using paint = New SKPaint With {
                                    .ColorFilter = shadowColorFilter,
                                    .ImageFilter = shadowImageFilter
                                }
                                    canvas.Save()
                                    ' Versatz unskaliert (außerhalb der Skalierung angewandt), Skalierung um die Objektmitte.
                                    canvas.Translate(rect.MidX + offsetX, rect.MidY + offsetY)
                                    canvas.Scale(shadowScale, shadowScale)
                                    canvas.Translate(-rect.MidX, -rect.MidY)
                                    canvas.DrawBitmap(shadowSource, maskLeft, maskTop, paint)
                                    canvas.Restore()
                                End Using
                            End Using
                        End Using
                    Finally
                        roundedShadowMask?.Dispose()
                    End Try
                End If
            End Using
        End Sub

        ''' Harte Alpha-Schwelle bei halber Deckung: aus einer weichgezeichneten Maske wird wieder eine
        ''' scharfe Form - nur eben mit runden Ecken. Die Farbkanaele bleiben unveraendert; die
        ''' SkiaSharp-Bindung verlangt trotzdem ALLE VIER Tabellen (Nothing wirft).
        Private Shared ReadOnly IdentityColorTable As Byte() = Enumerable.Range(0, 256).Select(Function(i) CByte(i)).ToArray()
        Private Shared ReadOnly AlphaThresholdTable As Byte() = Enumerable.Range(0, 256).Select(Function(i) CByte(If(i < 128, 0, 255))).ToArray()

        ''' <summary>Rundet die ECKEN einer Silhouette ab, ohne ihre Form zu ersetzen: weichzeichnen und
        ''' anschliessend wieder hart schwellen. Ein Gauss zieht Ecken staerker ein als gerade Kanten,
        ''' die Schwelle macht daraus wieder eine scharfe Kontur - der klassische Weg, weil Skias
        ''' Dilate/Erode ein RECHTECKIGES Strukturelement nutzen und damit eckig blieben.
        ''' Vorher zeichnete der Schalter ein abgerundetes Rechteck der Bounding-Box, warf also die
        ''' Objektform weg - bei Text oder Ellipse wurde der Schatten zum Kasten
        '''. Nothing = kein Rundungsbedarf, der Aufrufer nimmt dann die Originalmaske.</summary>
        Private Shared Function BuildRoundedSilhouette(mask As SKBitmap, cornerRadius As Single) As SKBitmap
            If mask Is Nothing OrElse cornerRadius < 0.5F Then Return Nothing
            ' Der Gauss rundet mit etwa dem doppelten Sigma - so trifft der Regler die gewuenschte Ecke.
            Dim sigma = Math.Max(0.1F, cornerRadius * 0.5F)
            Dim rounded As SKBitmap = Nothing
            Try
                rounded = New SKBitmap(mask.Width, mask.Height, SKColorType.Rgba8888, SKAlphaType.Premul)
                Using roundCanvas = New SKCanvas(rounded)
                    roundCanvas.Clear(SKColors.Transparent)
                    Using blur = SKImageFilter.CreateBlur(sigma, sigma)
                        Using threshold = SKColorFilter.CreateTable(AlphaThresholdTable, IdentityColorTable,
                                                                    IdentityColorTable, IdentityColorTable)
                            Using sharpen = SKImageFilter.CreateColorFilter(threshold, blur)
                                Using paint = New SKPaint With {.ImageFilter = sharpen}
                                    roundCanvas.DrawBitmap(mask, 0, 0, paint)
                                End Using
                            End Using
                        End Using
                    End Using
                End Using
                Return rounded
            Catch
                rounded?.Dispose()
                Return Nothing
            End Try
        End Function

        Private Shared Function ApplyAlpha(color As SKColor, factor As Single) As SKColor
            Return New SKColor(color.Red, color.Green, color.Blue, CByte(Math.Max(0, Math.Min(255, color.Alpha * Clamp(factor, 0, 1)))))
        End Function

        ' SKTypeface.FromFamilyName ist unter Linux/Fontconfig ein bekannt langsamer Pfad
        ' (Font-Matching-Scan), der ohne Cache bei jedem einzelnen Bake einer Text-/Wasserzeichen-
        ' Annotation erneut ausgeführt wurde. SKTypeface-Instanzen sind immutable und threadsicher
        ' wiederverwendbar, daher genügt ein einfacher, nie geleerter Cache über die kleine,
        ' begrenzte Menge an im Editor tatsächlich genutzten Font-Familiennamen.
        Private Shared ReadOnly _typefaceCache As New Dictionary(Of String, SKTypeface)()
        Private Shared ReadOnly _typefaceCacheLock As New Object()

        ''' <summary>Schriftschnitt aus Familie und Stil. Der Cacheschluessel enthaelt den STIL -
        ''' ohne ihn haette der erste Aufruf (etwa normal) alle spaeteren ueberdeckt und Fett/Kursiv
        ''' waeren wirkungslos geblieben, ohne Fehlermeldung.
        ''' Fehlt der Familie ein echter Fett- oder Kursivschnitt, faellt Skia auf den naechsten
        ''' vorhandenen zurueck; ein synthetisches Schraegstellen macht es NICHT.</summary>
        Private Shared Function GetTypeface(fontFamily As String, Optional bold As Boolean = False,
                                            Optional italic As Boolean = False) As SKTypeface
            Dim key = If(fontFamily, "") & "|" & If(bold, "b", "") & If(italic, "i", "")
            SyncLock _typefaceCacheLock
                Dim cached As SKTypeface = Nothing
                If _typefaceCache.TryGetValue(key, cached) Then Return cached
                Dim stil = New SKFontStyle(If(bold, SKFontStyleWeight.Bold, SKFontStyleWeight.Normal),
                                           SKFontStyleWidth.Normal,
                                           If(italic, SKFontStyleSlant.Italic, SKFontStyleSlant.Upright))
                Dim created = SKTypeface.FromFamilyName(fontFamily, stil)
                _typefaceCache(key) = created
                Return created
            End SyncLock
        End Function

        Private Shared Sub DrawAnnotationText(canvas As SKCanvas, text As String, x As Single, y As Single, maxWidth As Single, fontSize As Single, fill As SKColor, stroke As SKColor, strokeWidth As Single, fontFamily As String, bounds As SKRect, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False, Optional textPathKind As String = "", Optional textPathBend As Single = 0, Optional textPathStartOffset As Single = 0, Optional letterSpacingPercent As Single = 0, Optional bold As Boolean = False, Optional italic As Boolean = False, Optional pathPoints As String = "", Optional pathClosed As Boolean = False)
            ' Text an Pfad: EIN Zweig fuer Kontur und Fuellung, damit beide exakt dieselben
            ' Glyphenpositionen bekommen (und damit auch die Effekt-Maske, die ueber dieselbe
            ' Routine laeuft - Regel "Objektinhalt nur aus GENAU EINEM Renderpfad").
            ' warpGlyphs:=False ist entscheidend: mit dem Standard (True) VERBIEGT Skia jede
            ' Buchstabenkontur entlang der Kruemmung (innen gestaucht, aussen gedehnt) - bei
            ' grosser Schrift auf enger Kurve wirkte der Text stark verzerrt
            '. False platziert die Glyphen STARR und rotiert sie nur zur Tangente,
            ' wie Illustrator/Photoshop es tun.
            Dim path As SKPath = Nothing
            If Not String.IsNullOrWhiteSpace(textPathKind) Then
                path = BuildTextPath(bounds, textPathKind, textPathBend, textPathStartOffset, pathPoints, pathClosed)
            End If
            Try
                Using font = CreateFont(fontFamily, fontSize, bold, italic)
                    ' Abstand in Pixeln aus dem Prozentwert - relativ zur EFFEKTIVEN Schriftgroesse,
                    ' die die Pfad-Einpassung unten noch aendern kann. Wird deshalb nach jeder
                    ' Groessenaenderung neu berechnet.
                    Dim spacing = font.Size * letterSpacingPercent / 100.0F
                    ' Auf dem Pfad gibt es keinen Zeilenumbruch - Absaetze laufen als eine Zeile weiter.
                    Dim pathText = If(path IsNot Nothing,
                                      text.Replace(vbCrLf, " ").Replace(vbCr, " ").Replace(vbLf, " "),
                                      text)
                    If path IsNot Nothing Then
                        ' Text mittig auf den Pfad setzen; der Startversatz verschiebt von dort.
                        ' Der Start wird IN DEN PFAD gebacken (GetSegment) statt als hOffset
                        ' uebergeben: beide DrawTextOnPath-Ueberladungen wenden den Offset in
                        ' SkiaSharp 3.119 DOPPELT an (gemessen: Offset 100 -> Start +200) und
                        ' verschieben auf gekruemmten Pfaden zusaetzlich quer zur Kurve.
                        Using measure = New SKPathMeasure(path, False)
                            Dim pathLength = measure.Length
                            Dim textWidth = MeasureTextSpaced(font, pathText, spacing)
                            ' Schrift an den Pfad anpassen, damit kein Buchstabe wegfaellt: Kreis
                            ' waechst UND schrumpft auf den Umfang (kleine Fuge), Bogen/Welle
                            ' schrumpfen nur bei Ueberlaenge. Gleiche Formel wie
                            ' ComputeTextPathFitRatio - die Rand-Berechnungen rechnen damit.
                            If textWidth > 0 Then
                                Dim fit As Single
                                ' StartsWith, nicht Equals: "CircleInverted" ist derselbe geschlossene
                                ' Kreis. Mit Equals waere der invertierte Modus ohne Groessenanpassung
                                ' geblieben - der Text haette den Kreis ueber- oder unterlaufen.
                                If textPathKind.StartsWith("Circle", StringComparison.OrdinalIgnoreCase) Then
                                    ' Gleiche Deckelung wie in ComputeTextPathFitRatio (halber
                                    ' Radius als Obergrenze der Glyphenhoehe) - beide Formeln muessen
                                    ' synchron bleiben, sonst weichen Raender und Render voneinander ab.
                                    Dim maxGrow = Math.Max(1.0F, Math.Min(bounds.Width, bounds.Height) * 0.25F / Math.Max(1.0F, font.Size))
                                    fit = Math.Max(0.02F, Math.Min(maxGrow, pathLength * 0.97F / textWidth))
                                Else
                                    fit = Math.Max(0.02F, Math.Min(1.0F, pathLength / textWidth))
                                End If
                                If Math.Abs(fit - 1.0F) > 0.005F Then
                                    font.Size = font.Size * fit
                                    ' Abstand haengt an der Schriftgroesse - nach dem Einpassen neu.
                                    spacing = font.Size * letterSpacingPercent / 100.0F
                                    textWidth = MeasureTextSpaced(font, pathText, spacing)
                                End If
                            End If
                            Dim startDistance = Math.Max(0.0F, (pathLength - textWidth) / 2.0F)
                            ' Beim geschlossenen Kreis steckt der Startversatz bereits im Startwinkel.
                            ' Auch hier StartsWith: beim geschlossenen Kreis - egal ob normal oder
                            ' invertiert - steckt der Startversatz bereits im Startwinkel und darf
                            ' nicht ein zweites Mal aufaddiert werden.
                            If Not textPathKind.StartsWith("Circle", StringComparison.OrdinalIgnoreCase) Then
                                startDistance += textPathStartOffset / 100.0F * pathLength
                            End If
                            If startDistance > 0.5F AndAlso startDistance < pathLength - 1.0F Then
                                Dim segment As New SKPath()
                                If measure.GetSegment(startDistance, pathLength, segment, startWithMoveTo:=True) Then
                                    path.Dispose()
                                    path = segment
                                Else
                                    segment.Dispose()
                                End If
                            End If
                        End Using
                    End If

                    If strokeWidth > 0 Then
                        Using strokePaint = New SKPaint With {
                            .Color = stroke,
                            .IsAntialias = True,
                            .Style = SKPaintStyle.Stroke,
                            .StrokeWidth = Math.Max(1.0F, strokeWidth)
                        }
                            If path IsNot Nothing Then
                                DrawTextOnPathSpaced(canvas, pathText, path, font, strokePaint, spacing)
                            Else
                                DrawWrappedText(canvas, text, x, y, maxWidth, fontSize, font, strokePaint, spacing)
                            End If
                        End Using
                    End If

                    Using fillPaint = New SKPaint With {
                        .Color = fill,
                        .IsAntialias = True,
                        .Style = SKPaintStyle.Fill
                    }
                        Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
                        If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                            fillPaint.Shader = CreateFillGradientShader(bounds, normalizedFillKind, fill, fill2, gradientAngleDegrees, gradientInverted)
                        End If
                        If path IsNot Nothing Then
                            DrawTextOnPathSpaced(canvas, pathText, path, font, fillPaint, spacing)
                        Else
                            DrawWrappedText(canvas, text, x, y, maxWidth, fontSize, font, fillPaint, spacing)
                        End If
                    End Using
                End Using
            Finally
                path?.Dispose()
            End Try
        End Sub

        ''' <summary>Faktor, um den die Schrift eines Pfadtextes skaliert wird, damit ALLE Buchstaben
        ''' auf den Pfad passen (beim Kreis fielen ueberzaehlige Buchstaben
        ''' einfach weg). Kreis: Text laeuft immer genau einmal um den Umfang - waechst UND schrumpft
        ''' (kleine Fuge, damit Ende und Anfang nicht kollidieren). Bogen/Welle: nur schrumpfen bei
        ''' Ueberlaenge, sonst bleibt der Groessen-Regler das Mass. Skalenunabhaengig (Pfadlaenge und
        ''' Textbreite wachsen mit demselben Faktor), daher fuer Basis- wie Vorschau-Koordinaten gueltig.
        ''' Wird auch von den Rand-Berechnungen (Dirty-Rect/Overlay/Effekt-Maske) benutzt - die muessen
        ''' mit der EFFEKTIVEN Groesse rechnen, sonst beschneiden sie gewachsene Kreis-Texte.</summary>
        Friend Shared Function ComputeTextPathFitRatio(annotation As ImageAnnotation) As Single
            If annotation Is Nothing OrElse String.IsNullOrWhiteSpace(annotation.TextPathKind) Then Return 1.0F
            Dim text = If(annotation.Text, "").Replace(vbCrLf, " ").Replace(vbCr, " ").Replace(vbLf, " ")
            ' Ein Wasserzeichen ohne eigenen Text wird als "FerrumPix" gezeichnet - die Einpassung
            ' muss auf DEMSELBEN Text rechnen, sonst passt der Kreis nicht zum sichtbaren Wort.
            If String.IsNullOrWhiteSpace(text) AndAlso
               String.Equals(annotation.Kind, "Watermark", StringComparison.OrdinalIgnoreCase) Then
                text = "FerrumPix"
            End If
            If String.IsNullOrWhiteSpace(text) Then Return 1.0F
            Try
                Dim rect = SKRect.Create(0, 0, Math.Max(1.0F, annotation.WidthPixels), Math.Max(1.0F, annotation.HeightPixels))
                Using path = BuildTextPath(rect, annotation.TextPathKind, annotation.TextPathBend, annotation.TextPathStartOffset, annotation.PathPoints, annotation.PathClosed)
                    Using measure = New SKPathMeasure(path, False)
                        Using font = CreateFont(annotation.FontFamily, Math.Max(1.0F, annotation.FontSizePixels), annotation.Bold, annotation.Italic)
                            ' Abstand einrechnen - sonst weicht die Einpassung vom gezeichneten
                            ' Text ab, und beim Kreis liefe der Text ueber den Umfang hinaus.
                            Dim distance = font.Size * annotation.LetterSpacingPercent / 100.0F
                            Dim textWidth = MeasureTextSpaced(font, text, distance)
                            If textWidth <= 0 Then Return 1.0F
                            ' StartsWith statt Equals: "CircleInverted" ist geometrisch derselbe
                            ' geschlossene Kreis und braucht dieselbe Deckelung. Mit Equals waere
                            ' der neue Modus stillschweigend ohne Groessenanpassung geblieben.
                            If annotation.TextPathKind.StartsWith("Circle", StringComparison.OrdinalIgnoreCase) Then
                                ' Wachstum gedeckelt: Glyphenhoehe hoechstens der HALBE Radius -
                                ' beim vollen Radius sprengten zwei Riesenbuchstaben Box und Kreis
                                ' (visuell verifiziert).
                                Dim maxGrow = Math.Max(1.0F, Math.Min(rect.Width, rect.Height) * 0.25F / Math.Max(1.0F, annotation.FontSizePixels))
                                Return Math.Max(0.02F, Math.Min(maxGrow, measure.Length * 0.97F / textWidth))
                            End If
                            Return Math.Max(0.02F, Math.Min(1.0F, measure.Length / textWidth))
                        End Using
                    End Using
                End Using
            Catch
                Return 1.0F
            End Try
        End Function

        ''' <summary>Pfad fuer "Text an Pfad", aus dem Objektrechteck abgeleitet und als dichte
        ''' Punktfolge aufgebaut (die Glyphen werden per Bogenlaenge platziert, eine Polylinie mit
        ''' 96 Stuetzen ist dafuer unsichtbar glatt und erspart die Winkelmathematik dreier
        ''' Sonderfaelle). Bogen: Kreisbogen ueber die Rechteckbreite, Pfeilhoehe aus der Kruemmung
        ''' (negativ = nach unten). Welle: eine Sinusperiode, Amplitude aus der Kruemmung. Kreis:
        ''' ins Rechteck eingepasst, Start oben plus Startversatz; negative Kruemmung laeuft innen
        ''' (gegen den Uhrzeigersinn).</summary>
        ''' <summary>Ein Stützpunkt eines freien Pfades, alles in Prozent des Objektrechtecks.
        ''' <see cref="HandleIn"/> und <see cref="HandleOut"/> liegen ABSOLUT in demselben Raum, nicht
        ''' als Abstand zum Stützpunkt: so ist jede Rechnung darauf dieselbe wie auf dem Stützpunkt,
        ''' und beim Skalieren gibt es keine zweite Formel.</summary>
        Public Structure PathNode
            Public Anchor As SKPoint
            Public HandleIn As SKPoint
            Public HandleOut As SKPoint

            ''' <summary>Eckpunkt: beide Griffe liegen auf dem Stützpunkt.</summary>
            Public Shared Function Corner(x As Single, y As Single) As PathNode
                Dim p = New SKPoint(x, y)
                Return New PathNode With {.Anchor = p, .HandleIn = p, .HandleOut = p}
            End Function
        End Structure

        Private Shared Function ParsePathNumber(text As String) As Single
            Dim value As Single
            If Single.TryParse(text, Globalization.NumberStyles.Float,
                               Globalization.CultureInfo.InvariantCulture, value) Then Return value
            Return 0.0F
        End Function

        ''' <summary>Liest die Stützpunkte eines freien Pfades. Eine unlesbare oder zu kurze Liste
        ''' ergibt eine LEERE Liste - der Pfad zeichnet dann nichts, statt an einer halben Zahl zu
        ''' scheitern.</summary>
        Public Shared Function ParsePathPoints(text As String) As List(Of PathNode)
            Dim nodes As New List(Of PathNode)()
            If String.IsNullOrWhiteSpace(text) Then Return nodes
            For Each part In text.Split(";"c)
                If String.IsNullOrWhiteSpace(part) Then Continue For
                Dim values = part.Split(","c)
                If values.Length < 2 Then Continue For
                Dim ax = ParsePathNumber(values(0)), ay = ParsePathNumber(values(1))
                Dim node = PathNode.Corner(ax, ay)
                If values.Length >= 6 Then
                    node.HandleIn = New SKPoint(ParsePathNumber(values(2)), ParsePathNumber(values(3)))
                    node.HandleOut = New SKPoint(ParsePathNumber(values(4)), ParsePathNumber(values(5)))
                End If
                nodes.Add(node)
            Next
            Return nodes
        End Function

        Public Shared Function FormatPathPoints(nodes As IEnumerable(Of PathNode)) As String
            If nodes Is Nothing Then Return ""
            Dim culture = Globalization.CultureInfo.InvariantCulture
            Return String.Join(";", nodes.Select(Function(n) String.Join(",",
                n.Anchor.X.ToString("0.####", culture), n.Anchor.Y.ToString("0.####", culture),
                n.HandleIn.X.ToString("0.####", culture), n.HandleIn.Y.ToString("0.####", culture),
                n.HandleOut.X.ToString("0.####", culture), n.HandleOut.Y.ToString("0.####", culture))))
        End Function

        ''' <summary>Baut den freien Pfad im Zielraum des Objektrechtecks. Nothing = zu wenige Punkte.
        '''
        ''' Jeder Abschnitt ist eine Bezierkurve vom ausgehenden Griff des einen zum eingehenden des
        ''' naechsten Punktes. Liegen beide Griffe auf ihren Stuetzpunkten, ist die Kurve eine
        ''' Gerade - ein Eckpunkt braucht deshalb keinen eigenen Zweig.</summary>
        Public Shared Function BuildFreePath(rect As SKRect, pointsText As String, closed As Boolean) As SKPath
            Dim nodes = ParsePathPoints(pointsText)
            If nodes.Count < 2 OrElse rect.Width <= 0 OrElse rect.Height <= 0 Then Return Nothing
            Dim toCanvas = Function(p As SKPoint) New SKPoint(rect.Left + p.X / 100.0F * rect.Width,
                                                              rect.Top + p.Y / 100.0F * rect.Height)
            Dim path = New SKPath()
            Dim start = toCanvas(nodes(0).Anchor)
            path.MoveTo(start)
            For i = 0 To nodes.Count - 2
                Dim c1 = toCanvas(nodes(i).HandleOut)
                Dim c2 = toCanvas(nodes(i + 1).HandleIn)
                Dim target = toCanvas(nodes(i + 1).Anchor)
                path.CubicTo(c1, c2, target)
            Next
            If closed Then
                path.CubicTo(toCanvas(nodes(nodes.Count - 1).HandleOut), toCanvas(nodes(0).HandleIn), start)
                path.Close()
            End If
            Return path
        End Function

        Private Shared Function BuildTextPath(rect As SKRect, kind As String, bend As Single, startOffset As Single,
                                              Optional pathPoints As String = "",
                                              Optional pathClosed As Boolean = False) As SKPath
            ' Freier Pfad: die Grundlinie kommt aus den Stuetzpunkten des Objekts statt aus einer
            ' Formel. Faellt sie aus (zu wenige Punkte), gilt weiter der Bogen darunter - ein Text
            ' ohne Grundlinie waere sonst gar nicht zu sehen.
            If String.Equals(kind, "Free", StringComparison.OrdinalIgnoreCase) Then
                Dim free = BuildFreePath(rect, pathPoints, pathClosed)
                If free IsNot Nothing Then Return free
            End If
            Return BuildTextPathCore(rect, kind, bend, startOffset)
        End Function

        Private Shared Function BuildTextPathCore(rect As SKRect, kind As String, bend As Single, startOffset As Single) As SKPath
            Const Steps As Integer = 96
            Dim path = New SKPath()
            Dim normalized = If(kind, "").Trim().ToLowerInvariant()
            Dim amount = Math.Max(-100.0F, Math.Min(100.0F, bend)) / 100.0F

            Select Case normalized
                Case "circle", "circleinverted"
                    ' Radius aus min(Breite, Hoehe): ein Kreis bleibt ein Kreis. Ihn ueber das
                    ' Rechteck zu strecken ergaebe bei breiten Objekten eine flache Ellipse - also
                    ' faktisch einen Bogen (ausprobiert und wieder verworfen).
                    Dim radius = Math.Min(rect.Width, rect.Height) / 2.0F
                    Dim cx = rect.MidX, cy = rect.MidY
                    Dim inverted = normalized = "circleinverted"

                    ' Bildschirmkoordinaten (y nach UNTEN): der Punkt zum Winkel a ist
                    ' (cos a, sin a), also a=90 Grad = unten, a=270 Grad = oben.
                    ' Der Text wird auf dem Pfad zentriert; seine Mitte liegt damit eine halbe Runde
                    ' hinter dem Start. Beide Varianten starten deshalb UNTEN, damit der Text OBEN
                    ' sitzt - sie unterscheiden sich NUR in der Laufrichtung.
                    '
                    ' NORMAL (Winkel waechst): oben laeuft die Tangente nach rechts. Die Buchstaben
                    ' stehen mit dem Fuss auf dem Kreis und dem Kopf nach AUSSEN - Abzeichen-Oberseite.
                    '
                    ' INVERTIERT (Winkel faellt): oben laeuft die Tangente nach links, und damit
                    ' kippt die Aufrechte der Buchstaben mit. Sie haengen dann mit dem Kopf nach
                    ' INNEN und dem Fuss nach aussen - der Text liegt gleichsam auf der Innenseite
                    ' des Rings. Es ist derselbe Ort wie bei "Kreis", nur die Schrift ist auf der
                    ' Linie umgeschlagen.
                    Dim baseDirection = If(inverted, -1.0, 1.0)
                    Dim startAngle = Math.PI / 2.0 + baseDirection * startOffset / 100.0 * 2.0 * Math.PI
                    ' Negative Kruemmung dreht die Laufrichtung wie bisher zusaetzlich um.
                    Dim direction = If(amount < 0, -baseDirection, baseDirection)
                    For i = 0 To Steps
                        Dim a = startAngle + direction * 2.0 * Math.PI * i / Steps
                        Dim px = CSng(cx + radius * Math.Cos(a))
                        Dim py = CSng(cy + radius * Math.Sin(a))
                        If i = 0 Then path.MoveTo(px, py) Else path.LineTo(px, py)
                    Next

                Case "wave"
                    Dim amplitude = amount * rect.Height / 2.0F
                    For i = 0 To Steps
                        Dim t = i / CSng(Steps)
                        Dim px = rect.Left + t * rect.Width
                        Dim py = CSng(rect.MidY - amplitude * Math.Sin(t * 2.0 * Math.PI))
                        If i = 0 Then path.MoveTo(px, py) Else path.LineTo(px, py)
                    Next

                Case Else ' "arc"
                    ' Kreisbogen durch die beiden Seitenmitten, Pfeilhoehe aus der Kruemmung.
                    ' Praktisch keine Biegung -> gerade Linie (die Sehnenformel wuerde degenerieren).
                    Dim sagitta = amount * rect.Height / 2.0F
                    If Math.Abs(sagitta) < 0.5F Then
                        path.MoveTo(rect.Left, rect.MidY)
                        path.LineTo(rect.Right, rect.MidY)
                    Else
                        Dim half = rect.Width / 2.0F
                        Dim radius = (half * half + sagitta * sagitta) / (2.0F * Math.Abs(sagitta))
                        Dim cy = rect.MidY + Math.Sign(sagitta) * (radius - Math.Abs(sagitta))
                        Dim halfSweep = Math.Asin(Math.Min(1.0, half / radius))
                        For i = 0 To Steps
                            Dim a = -halfSweep + 2.0 * halfSweep * i / Steps
                            Dim px = CSng(rect.MidX + radius * Math.Sin(a))
                            Dim py = CSng(cy - Math.Sign(sagitta) * radius * Math.Cos(a))
                            If i = 0 Then path.MoveTo(px, py) Else path.LineTo(px, py)
                        Next
                    End If
            End Select
            Return path
        End Function

        ''' opacity ist auf der 0-100-Skala (wie annotation.Opacity), NICHT der bereits normalisierten
        ''' 0-1 alphaFactor-Skala, die für die übrigen (bereits alpha-vorgemischten) Fill/Stroke-Farben
        ''' verwendet wird - siehe Aufrufstelle im Select Case (Kind "image").
        Private Shared Sub DrawImageAnnotation(canvas As SKCanvas, imagePath As String, rect As SKRect, opacity As Single, stroke As SKColor, strokeWidth As Single, Optional stretchToFill As Boolean = False)
            If String.IsNullOrWhiteSpace(imagePath) OrElse Not File.Exists(imagePath) Then Return

            Using bitmap = SKBitmap.Decode(imagePath)
                If bitmap Is Nothing OrElse bitmap.Width <= 0 OrElse bitmap.Height <= 0 Then Return

                Dim fitRect = If(stretchToFill, rect, FitRectKeepingAspectRatio(rect, bitmap.Width, bitmap.Height))
                Using paint = New SKPaint With {
                    .IsAntialias = True,
                    .Color = New SKColor(255, 255, 255, CByte(Math.Max(0, Math.Min(255, 255 * Clamp(opacity, 0, 100) / 100.0F))))
                }
                    DrawBitmapSampled(canvas, bitmap, SKRect.Create(0, 0, bitmap.Width, bitmap.Height), fitRect, SamplingHigh, paint)
                End Using

                If strokeWidth > 0 Then
                    Using strokePaint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = strokeWidth, .IsAntialias = True}
                        canvas.DrawRect(fitRect, strokePaint)
                    End Using
                End If
            End Using
        End Sub

        ' Zeichnet ein beliebiges SVG-Icon (aus Assets/Icons/**) als Objekt auf dem Foto - die
        ' Pfad-Geometrie kommt aus der SVG-Datei, Füllung/Kontur/Deckkraft aus den Live-Einstellungen
        ' im Anpassungspanel (wie bei den übrigen Formen).
        Private Class ShapePathData
            Public Property Path As SKPath
            Public Property Bounds As SKRect
        End Class

        Private Shared ReadOnly _shapePathCache As New Dictionary(Of String, ShapePathData)()
        Private Shared ReadOnly _shapePathCacheLock As New Object()

        ''' Skaliert wie SvgIcon.vb (uniform/"contain", zentriert anhand der eigenen Bounds) statt
        ''' pro Achse getrennt zu strecken - sonst weicht das gebackene Rendering bei nicht-quadratischen
        ''' Ziel-Rects (jedes nicht-quadratische Foto) sichtbar von der Live-Vorschau ab.
        Private Shared Sub DrawSvgAnnotation(canvas As SKCanvas, iconPath As String, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            If String.IsNullOrWhiteSpace(iconPath) Then Return
            Dim shape = GetShapePath(iconPath)
            If shape Is Nothing OrElse shape.Path.IsEmpty OrElse shape.Bounds.Width <= 0 OrElse shape.Bounds.Height <= 0 Then Return

            Dim scaleX = rect.Width / shape.Bounds.Width
            Dim scaleY = rect.Height / shape.Bounds.Height

            canvas.Save()
            canvas.Translate(rect.Left, rect.Top)
            canvas.Scale(scaleX, scaleY)
            canvas.Translate(-shape.Bounds.Left, -shape.Bounds.Top)

            Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
            If fill.Alpha > 0 Then
                If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                    ''' shape.Bounds statt rect: der Canvas ist an dieser Stelle bereits in den lokalen
                    ''' Pfad-Koordinatenraum transformiert (s.o.), der Shader muss im selben Koordinatenraum
                    ''' wie der gezeichnete Pfad definiert werden, sonst landet der Verlauf weit außerhalb
                    ''' des sichtbaren Bereichs und wirkt wie eine einfarbige Füllung.
                    Using shader = CreateFillGradientShader(shape.Bounds, normalizedFillKind, fill, fill2, gradientAngleDegrees, gradientInverted)
                        Using fillPaint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                            canvas.DrawPath(shape.Path, fillPaint)
                        End Using
                    End Using
                Else
                    Using fillPaint = New SKPaint With {.Color = fill, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                        canvas.DrawPath(shape.Path, fillPaint)
                    End Using
                End If
            End If
            If strokeWidth > 0 Then
                Dim adjustedStroke = strokeWidth / Math.Max(0.0001F, Math.Min(scaleX, scaleY))
                Using strokePaint = New SKPaint With {
                    .Color = stroke,
                    .Style = SKPaintStyle.Stroke,
                    .StrokeWidth = adjustedStroke,
                    .IsAntialias = True,
                    .StrokeCap = SKStrokeCap.Round,
                    .StrokeJoin = SKStrokeJoin.Round
                }
                    canvas.DrawPath(shape.Path, strokePaint)
                End Using
            End If
            canvas.Restore()
        End Sub

        Private Shared Function GetShapePath(iconPath As String) As ShapePathData
            SyncLock _shapePathCacheLock
                Dim cached As ShapePathData = Nothing
                If _shapePathCache.TryGetValue(iconPath, cached) Then Return cached

                Dim parsed = ParseSvgToPath(iconPath)
                _shapePathCache(iconPath) = parsed
                Return parsed
            End SyncLock
        End Function

        Private Shared Function ParseSvgToPath(iconPath As String) As ShapePathData
            Try
                Dim svgText As String
                If iconPath.StartsWith("avares://", StringComparison.OrdinalIgnoreCase) Then
                    Using stream = AssetLoader.Open(New Uri(iconPath))
                        Using reader = New StreamReader(stream)
                            svgText = reader.ReadToEnd()
                        End Using
                    End Using
                Else
                    If Not File.Exists(iconPath) Then Return Nothing
                    svgText = File.ReadAllText(iconPath)
                End If

                Dim combined = New SKPath()
                Dim shapeRegex = New Regex("<(?<tag>path|rect|circle|ellipse|line)\b(?<attrs>[^>]*?)/?>", RegexOptions.Singleline)
                For Each m As Match In shapeRegex.Matches(svgText)
                    Dim d As String = Nothing
                    Dim attrs = m.Groups("attrs").Value
                    Select Case m.Groups("tag").Value
                        Case "path" : d = GetSvgAttr(attrs, "d")
                        Case "rect" : d = SvgRectToPath(attrs)
                        Case "circle" : d = SvgCircleToPath(attrs)
                        Case "ellipse" : d = SvgEllipseToPath(attrs)
                        Case "line" : d = SvgLineToPath(attrs)
                    End Select

                    If Not String.IsNullOrWhiteSpace(d) Then
                        Try
                            Dim subPath = SKPath.ParseSvgPathData(d)
                            If subPath IsNot Nothing Then combined.AddPath(subPath)
                        Catch
                        End Try
                    End If
                Next

                If combined.IsEmpty Then Return Nothing
                Return New ShapePathData With {.Path = combined, .Bounds = combined.Bounds}
            Catch
                Return Nothing
            End Try
        End Function

        Private Shared Function GetSvgAttr(attrs As String, name As String) As String
            Dim m = Regex.Match(attrs, name & "\s*=\s*""(?<v>[^""]*)""")
            Return If(m.Success, m.Groups("v").Value, Nothing)
        End Function

        Private Shared Function GetSvgAttrNumber(attrs As String, name As String, fallback As Double) As Double
            Dim v = GetSvgAttr(attrs, name)
            If v Is Nothing Then Return fallback
            Return Double.Parse(v, Globalization.CultureInfo.InvariantCulture)
        End Function

        Private Shared Function SvgRectToPath(attrs As String) As String
            Dim x = GetSvgAttrNumber(attrs, "x", 0)
            Dim y = GetSvgAttrNumber(attrs, "y", 0)
            Dim w = GetSvgAttrNumber(attrs, "width", 0)
            Dim h = GetSvgAttrNumber(attrs, "height", 0)
            If w <= 0 OrElse h <= 0 Then Return Nothing

            Dim rx = GetSvgAttrNumber(attrs, "rx", GetSvgAttrNumber(attrs, "ry", 0))
            If rx <= 0 Then
                Return $"M{SvgNum(x)},{SvgNum(y)} H{SvgNum(x + w)} V{SvgNum(y + h)} H{SvgNum(x)} Z"
            End If
            rx = Math.Min(rx, Math.Min(w / 2, h / 2))

            Return $"M{SvgNum(x + rx)},{SvgNum(y)} " &
                   $"H{SvgNum(x + w - rx)} A{SvgNum(rx)},{SvgNum(rx)} 0 0 1 {SvgNum(x + w)},{SvgNum(y + rx)} " &
                   $"V{SvgNum(y + h - rx)} A{SvgNum(rx)},{SvgNum(rx)} 0 0 1 {SvgNum(x + w - rx)},{SvgNum(y + h)} " &
                   $"H{SvgNum(x + rx)} A{SvgNum(rx)},{SvgNum(rx)} 0 0 1 {SvgNum(x)},{SvgNum(y + h - rx)} " &
                   $"V{SvgNum(y + rx)} A{SvgNum(rx)},{SvgNum(rx)} 0 0 1 {SvgNum(x + rx)},{SvgNum(y)} Z"
        End Function

        Private Shared Function SvgCircleToPath(attrs As String) As String
            Dim cx = GetSvgAttrNumber(attrs, "cx", 0)
            Dim cy = GetSvgAttrNumber(attrs, "cy", 0)
            Dim r = GetSvgAttrNumber(attrs, "r", 0)
            If r <= 0 Then Return Nothing
            Return $"M{SvgNum(cx - r)},{SvgNum(cy)} A{SvgNum(r)},{SvgNum(r)} 0 1 0 {SvgNum(cx + r)},{SvgNum(cy)} A{SvgNum(r)},{SvgNum(r)} 0 1 0 {SvgNum(cx - r)},{SvgNum(cy)} Z"
        End Function

        Private Shared Function SvgEllipseToPath(attrs As String) As String
            Dim cx = GetSvgAttrNumber(attrs, "cx", 0)
            Dim cy = GetSvgAttrNumber(attrs, "cy", 0)
            Dim rx = GetSvgAttrNumber(attrs, "rx", 0)
            Dim ry = GetSvgAttrNumber(attrs, "ry", 0)
            If rx <= 0 OrElse ry <= 0 Then Return Nothing
            Return $"M{SvgNum(cx - rx)},{SvgNum(cy)} A{SvgNum(rx)},{SvgNum(ry)} 0 1 0 {SvgNum(cx + rx)},{SvgNum(cy)} A{SvgNum(rx)},{SvgNum(ry)} 0 1 0 {SvgNum(cx - rx)},{SvgNum(cy)} Z"
        End Function

        Private Shared Function SvgLineToPath(attrs As String) As String
            Dim x1 = GetSvgAttrNumber(attrs, "x1", 0)
            Dim y1 = GetSvgAttrNumber(attrs, "y1", 0)
            Dim x2 = GetSvgAttrNumber(attrs, "x2", 0)
            Dim y2 = GetSvgAttrNumber(attrs, "y2", 0)
            Return $"M{SvgNum(x1)},{SvgNum(y1)} L{SvgNum(x2)},{SvgNum(y2)}"
        End Function

        Private Shared Function SvgNum(value As Double) As String
            Return value.ToString(Globalization.CultureInfo.InvariantCulture)
        End Function

        ''' <summary>Friend, weil der Editor dieselbe Einpassung braucht, um einen Mauspunkt in den
        ''' BILDPUNKT eines Bild-Objekts zurückzurechnen (Malen auf einem Objekt). Zwei Kopien dieser
        ''' Formel liefen unweigerlich auseinander, und der Strich säße dann neben dem Zeiger.</summary>
        Friend Shared Function FitRectKeepingAspectRatio(target As SKRect, sourceWidth As Integer, sourceHeight As Integer) As SKRect
            If sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return target
            Dim targetWidth = Math.Max(1.0F, target.Width)
            Dim targetHeight = Math.Max(1.0F, target.Height)
            Dim sourceRatio = sourceWidth / CSng(sourceHeight)
            Dim targetRatio = targetWidth / targetHeight
            Dim drawWidth As Single
            Dim drawHeight As Single
            If sourceRatio > targetRatio Then
                drawWidth = targetWidth
                drawHeight = drawWidth / sourceRatio
            Else
                drawHeight = targetHeight
                drawWidth = drawHeight * sourceRatio
            End If
            Dim left = target.Left + (targetWidth - drawWidth) / 2.0F
            Dim top = target.Top + (targetHeight - drawHeight) / 2.0F
            Return New SKRect(left, top, left + drawWidth, top + drawHeight)
        End Function

        Private Shared Sub DrawShape(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, ellipse As Boolean, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
            If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                Using shader = CreateFillGradientShader(rect, normalizedFillKind, fill, fill2, gradientAngleDegrees, gradientInverted)
                    Using fillPaint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                        If ellipse Then canvas.DrawOval(rect, fillPaint) Else canvas.DrawRect(rect, fillPaint)
                    End Using
                End Using
            Else
                Using fillPaint = New SKPaint With {.Color = fill, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                    If fill.Alpha > 0 Then
                        If ellipse Then canvas.DrawOval(rect, fillPaint) Else canvas.DrawRect(rect, fillPaint)
                    End If
                End Using
            End If

            Using strokePaint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = strokeWidth, .IsAntialias = True}
                If ellipse Then canvas.DrawOval(rect, strokePaint) Else canvas.DrawRect(rect, strokePaint)
            End Using
        End Sub

        ' Verlauf ist bewusst auf das übergebene Rect begrenzt (nicht die ganze Canvas wie beim
        ' bestehenden Vignette-Radialgradient in ApplyVignette) - Zentrum/Winkel beziehen sich auf
        ' die Objekt-Bounds, damit der Verlauf mit dem Objekt mitwandert/rotiert.
        Private Shared Function CreateFillGradientShader(rect As SKRect, normalizedFillKind As String, color1 As SKColor, color2 As SKColor, angleDegrees As Single, Optional inverted As Boolean = False) As SKShader
            Dim startColor = If(inverted, color2, color1)
            Dim endColor = If(inverted, color1, color2)

            If normalizedFillKind = "radialgradient" Then
                Dim center = New SKPoint(rect.MidX, rect.MidY)
                Dim radius = CSng(Math.Sqrt(CDbl(rect.Width) * rect.Width + CDbl(rect.Height) * rect.Height) / 2.0)
                Return SKShader.CreateRadialGradient(center, Math.Max(1.0F, radius), New SKColor() {startColor, endColor}, Nothing, SKShaderTileMode.Clamp)
            End If

            Dim angleRad = angleDegrees * Math.PI / 180.0
            Dim dx = CSng(Math.Cos(angleRad)) * rect.Width / 2.0F
            Dim dy = CSng(Math.Sin(angleRad)) * rect.Height / 2.0F
            Dim startPoint = New SKPoint(rect.MidX - dx, rect.MidY - dy)
            Dim endPoint = New SKPoint(rect.MidX + dx, rect.MidY + dy)
            Return SKShader.CreateLinearGradient(startPoint, endPoint, New SKColor() {startColor, endColor}, Nothing, SKShaderTileMode.Clamp)
        End Function

        Private Shared Sub DrawRoundedRectangle(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Dim radius = Math.Min(rect.Width, rect.Height) * 0.18F
            Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
            If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                Using shader = CreateFillGradientShader(rect, normalizedFillKind, fill, fill2, gradientAngleDegrees, gradientInverted)
                    Using fillPaint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                        canvas.DrawRoundRect(rect, radius, radius, fillPaint)
                    End Using
                End Using
            ElseIf fill.Alpha > 0 Then
                Using fillPaint = New SKPaint With {.Color = fill, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                    canvas.DrawRoundRect(rect, radius, radius, fillPaint)
                End Using
            End If
            Using strokePaint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = strokeWidth, .IsAntialias = True}
                canvas.DrawRoundRect(rect, radius, radius, strokePaint)
            End Using
        End Sub

        Private Shared Sub DrawSquare(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Dim side = Math.Min(rect.Width, rect.Height)
            Dim x = rect.MidX - side / 2.0F
            Dim y = rect.MidY - side / 2.0F
            DrawShape(canvas, New SKRect(x, y, x + side, y + side), fill, stroke, strokeWidth, False, fillKind, fill2, gradientAngleDegrees, gradientInverted)
        End Sub

        Private Shared Sub DrawTriangle(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top)
                path.LineTo(rect.Right, rect.Bottom)
                path.LineTo(rect.Left, rect.Bottom)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawCone(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top + rect.Height * 0.12F)
                path.LineTo(rect.Right * 0.86F, rect.Bottom * 0.74F)
                path.ArcTo(New SKRect(rect.Left + rect.Width * 0.15F, rect.Bottom * 0.60F, rect.Right - rect.Width * 0.15F, rect.Bottom * 0.94F), 0, 180, False)
                path.LineTo(rect.MidX, rect.Top + rect.Height * 0.12F)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth)
            End Using
        End Sub

        Private Shared Sub DrawPyramid(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top)
                path.LineTo(rect.Right, rect.Bottom)
                path.LineTo(rect.Left, rect.Bottom)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth)
            End Using
            Using linePaint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = Math.Max(1.0F, strokeWidth * 0.7F), .IsAntialias = True, .StrokeCap = SKStrokeCap.Round}
                canvas.DrawLine(rect.MidX, rect.Top, rect.MidX, rect.Bottom - rect.Height * 0.18F, linePaint)
            End Using
        End Sub

        Private Shared Sub DrawTrapezoid(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                Dim inset = rect.Width * 0.22F
                path.MoveTo(rect.Left + inset, rect.Top)
                path.LineTo(rect.Right - inset, rect.Top)
                path.LineTo(rect.Right, rect.Bottom)
                path.LineTo(rect.Left, rect.Bottom)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawDiamond(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top)
                path.LineTo(rect.Right, rect.MidY)
                path.LineTo(rect.MidX, rect.Bottom)
                path.LineTo(rect.Left, rect.MidY)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawRegularPolygon(canvas As SKCanvas, rect As SKRect, sides As Integer, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                AddRegularPoints(path, rect, Math.Max(3, sides), 0.45F, -Math.PI / 2)
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawStar(canvas As SKCanvas, rect As SKRect, points As Integer, innerRadiusFactor As Single, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                Dim cx = rect.MidX
                Dim cy = rect.MidY
                Dim outerRadius = Math.Min(rect.Width, rect.Height) * 0.46F
                Dim innerRadius = outerRadius * innerRadiusFactor
                Dim total = Math.Max(3, points) * 2
                For i = 0 To total - 1
                    Dim radius = If(i Mod 2 = 0, outerRadius, innerRadius)
                    Dim angle = -Math.PI / 2 + i * Math.PI / points
                    Dim x = CSng(cx + Math.Cos(angle) * radius)
                    Dim y = CSng(cy + Math.Sin(angle) * radius)
                    If i = 0 Then path.MoveTo(x, y) Else path.LineTo(x, y)
                Next
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawDroplet(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top + rect.Height * 0.04F)
                path.CubicTo(rect.Right - rect.Width * 0.18F, rect.Top + rect.Height * 0.34F,
                             rect.Right - rect.Width * 0.06F, rect.Top + rect.Height * 0.58F,
                             rect.Right - rect.Width * 0.22F, rect.Top + rect.Height * 0.79F)
                path.CubicTo(rect.Right - rect.Width * 0.38F, rect.Bottom - rect.Height * 0.01F,
                             rect.Left + rect.Width * 0.38F, rect.Bottom - rect.Height * 0.01F,
                             rect.Left + rect.Width * 0.22F, rect.Top + rect.Height * 0.79F)
                path.CubicTo(rect.Left + rect.Width * 0.06F, rect.Top + rect.Height * 0.58F,
                             rect.Left + rect.Width * 0.18F, rect.Top + rect.Height * 0.34F,
                             rect.MidX, rect.Top + rect.Height * 0.04F)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawSpeechBubble(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Dim tailHeight = rect.Height * 0.20F
            Dim radius = Math.Min(rect.Width, rect.Height) * 0.12F
            Dim body = New SKRect(rect.Left + rect.Width * 0.04F,
                                  rect.Top + rect.Height * 0.06F,
                                  rect.Right - rect.Width * 0.04F,
                                  rect.Bottom - tailHeight)
            Using path = New SKPath()
                path.MoveTo(body.Left + radius, body.Top)
                path.LineTo(body.Right - radius, body.Top)
                path.QuadTo(body.Right, body.Top, body.Right, body.Top + radius)
                path.LineTo(body.Right, body.Bottom - radius)
                path.QuadTo(body.Right, body.Bottom, body.Right - radius, body.Bottom)
                path.LineTo(rect.Left + rect.Width * 0.46F, body.Bottom)
                path.LineTo(rect.Left + rect.Width * 0.24F, rect.Bottom - rect.Height * 0.04F)
                path.LineTo(rect.Left + rect.Width * 0.27F, body.Bottom)
                path.LineTo(body.Left + radius, body.Bottom)
                path.QuadTo(body.Left, body.Bottom, body.Left, body.Bottom - radius)
                path.LineTo(body.Left, body.Top + radius)
                path.QuadTo(body.Left, body.Top, body.Left + radius, body.Top)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawEllipseSpeechBubble(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Top + rect.Height * 0.07F)
                path.CubicTo(rect.Right - rect.Width * 0.12F, rect.Top + rect.Height * 0.07F,
                             rect.Right - rect.Width * 0.03F, rect.Top + rect.Height * 0.28F,
                             rect.Right - rect.Width * 0.04F, rect.Top + rect.Height * 0.48F)
                path.CubicTo(rect.Right - rect.Width * 0.05F, rect.Top + rect.Height * 0.70F,
                             rect.Right - rect.Width * 0.25F, rect.Top + rect.Height * 0.84F,
                             rect.Right - rect.Width * 0.45F, rect.Top + rect.Height * 0.86F)
                path.LineTo(rect.Left + rect.Width * 0.24F, rect.Bottom - rect.Height * 0.05F)
                path.LineTo(rect.Left + rect.Width * 0.32F, rect.Top + rect.Height * 0.82F)
                path.CubicTo(rect.Left + rect.Width * 0.13F, rect.Top + rect.Height * 0.72F,
                             rect.Left + rect.Width * 0.04F, rect.Top + rect.Height * 0.56F,
                             rect.Left + rect.Width * 0.04F, rect.Top + rect.Height * 0.40F)
                path.CubicTo(rect.Left + rect.Width * 0.04F, rect.Top + rect.Height * 0.20F,
                             rect.Left + rect.Width * 0.18F, rect.Top + rect.Height * 0.07F,
                             rect.MidX, rect.Top + rect.Height * 0.07F)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawRectSpeechBubble(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Dim tailHeight = rect.Height * 0.20F
            Dim body = New SKRect(rect.Left + rect.Width * 0.04F,
                                  rect.Top + rect.Height * 0.05F,
                                  rect.Right - rect.Width * 0.04F,
                                  rect.Bottom - tailHeight)
            Using path = New SKPath()
                path.MoveTo(body.Left, body.Top)
                path.LineTo(body.Right, body.Top)
                path.LineTo(body.Right, body.Bottom)
                path.LineTo(rect.MidX + rect.Width * 0.16F, body.Bottom)
                path.LineTo(rect.MidX + rect.Width * 0.03F, rect.Bottom - rect.Height * 0.04F)
                path.LineTo(rect.MidX - rect.Width * 0.10F, body.Bottom)
                path.LineTo(body.Left, body.Bottom)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawHeart(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                path.MoveTo(rect.MidX, rect.Bottom - rect.Height * 0.10F)
                path.CubicTo(rect.Left + rect.Width * 0.08F, rect.Top + rect.Height * 0.58F, rect.Left, rect.Top + rect.Height * 0.24F, rect.Left + rect.Width * 0.26F, rect.Top + rect.Height * 0.12F)
                path.CubicTo(rect.Left + rect.Width * 0.40F, rect.Top + rect.Height * 0.05F, rect.MidX, rect.Top + rect.Height * 0.17F, rect.MidX, rect.Top + rect.Height * 0.32F)
                path.CubicTo(rect.MidX, rect.Top + rect.Height * 0.17F, rect.Left + rect.Width * 0.60F, rect.Top + rect.Height * 0.05F, rect.Left + rect.Width * 0.74F, rect.Top + rect.Height * 0.12F)
                path.CubicTo(rect.Right, rect.Top + rect.Height * 0.24F, rect.Right - rect.Width * 0.08F, rect.Top + rect.Height * 0.58F, rect.MidX, rect.Bottom - rect.Height * 0.10F)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub DrawCloud(canvas As SKCanvas, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Using path = New SKPath()
                path.MoveTo(rect.Left + rect.Width * 0.24F, rect.Bottom - rect.Height * 0.22F)
                path.CubicTo(rect.Left + rect.Width * 0.07F, rect.Bottom - rect.Height * 0.22F, rect.Left + rect.Width * 0.04F, rect.Top + rect.Height * 0.47F, rect.Left + rect.Width * 0.18F, rect.Top + rect.Height * 0.39F)
                path.CubicTo(rect.Left + rect.Width * 0.20F, rect.Top + rect.Height * 0.19F, rect.Left + rect.Width * 0.42F, rect.Top + rect.Height * 0.12F, rect.Left + rect.Width * 0.55F, rect.Top + rect.Height * 0.27F)
                path.CubicTo(rect.Left + rect.Width * 0.66F, rect.Top + rect.Height * 0.20F, rect.Left + rect.Width * 0.82F, rect.Top + rect.Height * 0.28F, rect.Left + rect.Width * 0.83F, rect.Top + rect.Height * 0.44F)
                path.CubicTo(rect.Right - rect.Width * 0.02F, rect.Top + rect.Height * 0.49F, rect.Right - rect.Width * 0.06F, rect.Bottom - rect.Height * 0.22F, rect.Right - rect.Width * 0.22F, rect.Bottom - rect.Height * 0.22F)
                path.Close()
                DrawClosedPath(canvas, path, fill, stroke, strokeWidth, rect, fillKind, fill2, gradientAngleDegrees, gradientInverted)
            End Using
        End Sub

        Private Shared Sub AddRegularPoints(path As SKPath, rect As SKRect, count As Integer, radiusFactor As Single, startAngle As Double)
            Dim radius = Math.Min(rect.Width, rect.Height) * radiusFactor
            For i = 0 To count - 1
                Dim angle = startAngle + i * Math.PI * 2.0 / count
                Dim x = CSng(rect.MidX + Math.Cos(angle) * radius)
                Dim y = CSng(rect.MidY + Math.Sin(angle) * radius)
                If i = 0 Then path.MoveTo(x, y) Else path.LineTo(x, y)
            Next
            path.Close()
        End Sub

        Private Shared Sub DrawSpiral(canvas As SKCanvas, rect As SKRect, stroke As SKColor, strokeWidth As Single)
            Using path = New SKPath()
                Dim cx = rect.MidX
                Dim cy = rect.MidY
                Dim maxRadius = Math.Min(rect.Width, rect.Height) * 0.40F
                Dim minRadius = Math.Max(3.0F, maxRadius * 0.12F)
                Dim turns = 2.6F
                Dim points = 80
                For i = 0 To points
                    Dim t = i / CDbl(points)
                    Dim angle = -Math.PI / 2 + turns * 2 * Math.PI * t
                    Dim radius = maxRadius - (maxRadius - minRadius) * t
                    Dim x = cx + Math.Cos(angle) * radius
                    Dim y = cy + Math.Sin(angle) * radius
                    If i = 0 Then
                        path.MoveTo(CSng(x), CSng(y))
                    Else
                        path.LineTo(CSng(x), CSng(y))
                    End If
                Next
                Using paint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = Math.Max(1.0F, strokeWidth), .IsAntialias = True, .StrokeCap = SKStrokeCap.Round, .StrokeJoin = SKStrokeJoin.Round}
                    canvas.DrawPath(path, paint)
                End Using
            End Using
        End Sub

        Private Shared Sub DrawClosedPath(canvas As SKCanvas, path As SKPath, fill As SKColor, stroke As SKColor, strokeWidth As Single, Optional fillBounds As SKRect = Nothing, Optional fillKind As String = "Solid", Optional fill2 As SKColor = Nothing, Optional gradientAngleDegrees As Single = 0, Optional gradientInverted As Boolean = False)
            Dim normalizedFillKind = If(fillKind, "Solid").Trim().ToLowerInvariant()
            If normalizedFillKind = "lineargradient" OrElse normalizedFillKind = "radialgradient" Then
                Dim bounds = If(fillBounds.IsEmpty, path.Bounds, fillBounds)
                Using shader = CreateFillGradientShader(bounds, normalizedFillKind, fill, fill2, gradientAngleDegrees, gradientInverted)
                    Using fillPaint = New SKPaint With {.Shader = shader, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                        canvas.DrawPath(path, fillPaint)
                    End Using
                End Using
            ElseIf fill.Alpha > 0 Then
                Using fillPaint = New SKPaint With {.Color = fill, .Style = SKPaintStyle.Fill, .IsAntialias = True}
                    canvas.DrawPath(path, fillPaint)
                End Using
            End If
            ' Ohne Breite keine Kontur: eine Breite von null bedeutet ausdrücklich "keine", und Skia
            ' zeichnet bei null trotzdem eine Haarlinie.
            If strokeWidth <= 0.0F OrElse stroke.Alpha = 0 Then Return
            Using strokePaint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = strokeWidth, .IsAntialias = True, .StrokeCap = SKStrokeCap.Round, .StrokeJoin = SKStrokeJoin.Round}
                canvas.DrawPath(path, strokePaint)
            End Using
        End Sub

        Private Shared Sub DrawLine(canvas As SKCanvas, rect As SKRect, stroke As SKColor, strokeWidth As Single, arrow As Boolean)
            Dim effectiveStrokeWidth = If(arrow, strokeWidth, Math.Max(2.0F, strokeWidth))
            Using paint = New SKPaint With {.Color = stroke, .Style = SKPaintStyle.Stroke, .StrokeWidth = effectiveStrokeWidth, .StrokeCap = SKStrokeCap.Round, .IsAntialias = True}
                If arrow Then
                    Dim head = Math.Min(rect.Width * 0.28F, Math.Max(12.0F, strokeWidth * 4.0F))
                    Dim pad = Math.Max(strokeWidth * 0.5F, 1.0F)
                    Dim start = New SKPoint(rect.Left + pad, rect.MidY)
                    Dim tip = New SKPoint(rect.Right - pad, rect.MidY)
                    Dim headBackX = tip.X - head
                    Dim headHalfHeight = Math.Min(rect.Height * 0.36F, head * 0.55F)
                    canvas.DrawLine(start, tip, paint)
                    canvas.DrawLine(tip, New SKPoint(headBackX, tip.Y - headHalfHeight), paint)
                    canvas.DrawLine(tip, New SKPoint(headBackX, tip.Y + headHalfHeight), paint)
                Else
                    canvas.DrawLine(rect.Left, rect.MidY, rect.Right, rect.MidY, paint)
                End If
            End Using
        End Sub

        Private Shared Sub DrawSingleGlyph(canvas As SKCanvas, glyph As String, rect As SKRect, fill As SKColor, stroke As SKColor, strokeWidth As Single, fontFamily As String)
            Dim text = glyph.Trim()
            If text.Length = 0 Then text = "★"
            Dim fontSize = Math.Max(12.0F, Math.Min(rect.Width, rect.Height) * 0.82F)
            Using font = CreateFont(fontFamily, fontSize)
                Using paint = New SKPaint With {.IsAntialias = True}
                    Dim bounds As SKRect
                    font.MeasureText(text, bounds, paint)
                    Dim x = rect.MidX - bounds.MidX
                    Dim y = rect.MidY - bounds.MidY
                    If strokeWidth > 0 Then
                        paint.Style = SKPaintStyle.Stroke
                        paint.StrokeWidth = strokeWidth
                        paint.Color = stroke
                        canvas.DrawText(text, x, y, font, paint)
                    End If
                    paint.Style = SKPaintStyle.Fill
                    paint.Color = fill
                    canvas.DrawText(text, x, y, font, paint)
                End Using
            End Using
        End Sub

        Private Shared Sub DrawQrCode(canvas As SKCanvas, text As String, rect As SKRect, dark As SKColor, light As SKColor)
            Using generator = New QRCodeGenerator()
                Using data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.Q)
                    Dim modules = data.ModuleMatrix
                    Dim count = modules.Count
                    If count <= 0 Then Return
                    Dim size = Math.Min(rect.Width, rect.Height)
                    Dim left = rect.Left + (rect.Width - size) / 2.0F
                    Dim top = rect.Top + (rect.Height - size) / 2.0F
                    Dim cell = size / count
                    Using bg = New SKPaint With {.Color = If(light.Alpha = 0, SKColors.White, light), .Style = SKPaintStyle.Fill, .IsAntialias = False}
                        canvas.DrawRect(left, top, size, size, bg)
                    End Using
                    Using fg = New SKPaint With {.Color = dark, .Style = SKPaintStyle.Fill, .IsAntialias = False}
                        For row As Integer = 0 To count - 1
                            For col As Integer = 0 To modules(row).Count - 1
                                If modules(row)(col) Then
                                    ' Math.Ceiling liefert Double, DrawRect nimmt Single.
                                    Dim edge = CSng(Math.Ceiling(cell))
                                    canvas.DrawRect(left + col * cell, top + row * cell, edge, edge, fg)
                                End If
                            Next
                        Next
                    End Using
                End Using
            End Using
        End Sub

        Private Shared Function WithAlpha(color As SKColor, alpha As Byte) As SKColor
            Return New SKColor(color.Red, color.Green, color.Blue, alpha)
        End Function

        ''' <summary>Textbreite EINSCHLIESSLICH Zeichenabstand. Muss ueberall dort benutzt werden,
        ''' wo bisher font.MeasureText stand - sonst passt die Einpassung auf den Pfad nicht mehr
        ''' zum tatsaechlich gezeichneten Text.</summary>
        Private Shared Function MeasureTextSpaced(font As SKFont, text As String, spacing As Single) As Single
            If String.IsNullOrEmpty(text) Then Return 0.0F
            Dim w = font.MeasureText(text)
            If spacing <> 0.0F AndAlso text.Length > 1 Then w += spacing * (text.Length - 1)
            Return w
        End Function

        ''' <summary>Zeichnet eine Zeile mit Zeichenabstand. Bei spacing = 0 exakt der bisherige
        ''' Weg (ein DrawText fuer die ganze Zeile, mit Kerning) - der Normalfall bleibt also
        ''' unveraendert. Erst ein gesetzter Abstand setzt die Zeichen einzeln.</summary>
        Private Shared Sub DrawTextSpaced(canvas As SKCanvas, text As String, x As Single, baseline As Single,
                                          font As SKFont, paint As SKPaint, spacing As Single)
            If spacing = 0.0F Then
                canvas.DrawText(text, x, baseline, font, paint)
                Return
            End If
            Dim cx = x
            For Each ch In text
                Dim einzeln = ch.ToString()
                canvas.DrawText(einzeln, cx, baseline, font, paint)
                cx += font.MeasureText(einzeln) + spacing
            Next
        End Sub

        ''' <summary>Text auf einem Pfad mit Zeichenabstand. Bei spacing = 0 bleibt es bei Skias
        ''' DrawTextOnPath; sonst werden die Zeichen einzeln gesetzt und zur Tangente gedreht -
        ''' dasselbe Verhalten wie warpGlyphs:=False, nur mit eigenem Vorschub.
        ''' Ohne diesen Zweig waere der Zeichenabstand bei gesetztem Pfad wirkungslos gewesen.</summary>
        Private Shared Sub DrawTextOnPathSpaced(canvas As SKCanvas, text As String, path As SKPath,
                                                font As SKFont, paint As SKPaint, spacing As Single)
            If spacing = 0.0F Then
                canvas.DrawTextOnPath(text, path, New SKPoint(0, 0), warpGlyphs:=False, font, paint)
                Return
            End If
            Using measure = New SKPathMeasure(path, False)
                Dim laenge = measure.Length
                Dim d As Single = 0.0F
                For Each ch In text
                    Dim einzeln = ch.ToString()
                    Dim width = font.MeasureText(einzeln)
                    Dim center = d + width / 2.0F
                    If center > laenge Then Exit For
                    Dim pos As SKPoint, tangente As SKPoint
                    If measure.GetPositionAndTangent(center, pos, tangente) Then
                        Dim angle = CSng(Math.Atan2(tangente.Y, tangente.X) * 180.0 / Math.PI)
                        Dim state = canvas.Save()
                        canvas.Translate(pos.X, pos.Y)
                        canvas.RotateDegrees(angle)
                        canvas.DrawText(einzeln, -width / 2.0F, 0, font, paint)
                        canvas.RestoreToCount(state)
                    End If
                    d += width + spacing
                Next
            End Using
        End Sub

        ''' <summary>Zeichnet Textzeilen eines Text-Objekts. KEIN automatischer Umbruch an der
        ''' Boxbreite mehr: die Auswahlbox wird aus den EXPLIZITEN Zeilen
        ''' gemessen (EstimateTextAnnotationSizePercent) - ein Breiten-Umbruch hier zeichnete dann
        ''' hoeher als die Box, und beim Verschieben blieben Fragmente des alten Stands stehen
        ''' (Geist ausserhalb des uebergebenen Rechtecks). Neue Zeilen entstehen ausschliesslich
        ''' ueber echte Zeilenumbrueche im Text (Strg+Enter in den Eingabefeldern).
        ''' <paramref name="maxWidth"/> bleibt in der Signatur, weil die Aufrufer sie fuer Formen
        ''' weiterreichen - fuer Text ist sie bewusst ohne Wirkung.</summary>
        Private Shared Sub DrawWrappedText(canvas As SKCanvas, text As String, x As Single, y As Single, maxWidth As Single, fontSize As Single, font As SKFont, paint As SKPaint, Optional spacing As Single = 0)
            If String.IsNullOrEmpty(text) Then Return
            Dim lineHeight = GetLineHeight(font.Metrics)
            Dim baseline = y + fontSize

            For Each line In text.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(ControlChars.Lf)
                If line.Length > 0 Then
                    DrawTextSpaced(canvas, line, x, baseline, font, paint, spacing)
                End If
                baseline += lineHeight
            Next
        End Sub

        Private Shared Function ParseColor(value As String, fallback As SKColor) As SKColor
            If String.IsNullOrWhiteSpace(value) Then Return fallback
            Dim text = value.Trim().TrimStart("#"c)
            Try
                Dim raw As UInteger
                If UInteger.TryParse(text, Globalization.NumberStyles.HexNumber, Globalization.CultureInfo.InvariantCulture, raw) Then
                    If text.Length <= 6 Then
                        Return New SKColor(CByte((raw >> 16) And &HFFUI), CByte((raw >> 8) And &HFFUI), CByte(raw And &HFFUI), 255)
                    End If
                    Return New SKColor(CByte((raw >> 16) And &HFFUI), CByte((raw >> 8) And &HFFUI), CByte(raw And &HFFUI), CByte((raw >> 24) And &HFFUI))
                End If
            Catch
            End Try
            Return fallback
        End Function

    End Class

End Namespace
