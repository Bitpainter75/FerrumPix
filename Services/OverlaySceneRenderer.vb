Imports SkiaSharp

Namespace Services

    Public NotInheritable Class OverlaySceneRenderer
        Private Sub New()
        End Sub

        Public Shared Function IsOverlayAnnotation(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing OrElse Not annotation.IsVisible Then Return False
            Dim kind = If(annotation.Kind, "").Trim().ToLowerInvariant()
            Return kind <> "brush" AndAlso kind <> "eraser"
        End Function

        Public Shared Function IsNonNormalBlend(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing Then Return False
            Return Not String.Equals(If(annotation.BlendMode, "Normal").Trim(), "Normal", StringComparison.OrdinalIgnoreCase)
        End Function

        Public Shared Function Intersects(a As SKRectI, b As SKRectI) As Boolean
            If a.IsEmpty OrElse b.IsEmpty Then Return False
            Return a.Left < b.Right AndAlso a.Right > b.Left AndAlso a.Top < b.Bottom AndAlso a.Bottom > b.Top
        End Function

        ''' <remarks>sourceWidth/Height ist der ZIEL-Raum (z.B. gedeckelte Preview); baseWidth/Height der
        ''' Basis-Bildpixelraum, in dem die Annotationen gespeichert sind (0 = gleich, keine Skalierung).
        ''' seedRect muss bereits im Zielraum vorliegen.</remarks>
        Public Shared Function ComputeBlendCompositeRect(annotations As IReadOnlyList(Of ImageAnnotation),
                                                         changedIndex As Integer,
                                                         sourceWidth As Integer,
                                                         sourceHeight As Integer,
                                                         seedRect As SKRectI,
                                                         Optional baseWidth As Integer = 0,
                                                         Optional baseHeight As Integer = 0) As (RequiresComposite As Boolean, Rect As SKRectI)
            If annotations Is Nothing OrElse changedIndex < 0 OrElse changedIndex >= annotations.Count Then
                Return (False, SKRectI.Empty)
            End If
            If sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return (False, SKRectI.Empty)

            Dim rect = seedRect
            If rect.IsEmpty Then
                rect = ImageProcessor.ComputeAnnotationDirtyRect(sourceWidth, sourceHeight, annotations(changedIndex), baseWidth, baseHeight)
            End If
            If rect.IsEmpty Then Return (False, SKRectI.Empty)

            Dim requiresComposite = IsNonNormalBlend(annotations(changedIndex))
            Dim changed = True
            While changed
                changed = False
                For i = Math.Max(0, changedIndex) To annotations.Count - 1
                    Dim annotation = annotations(i)
                    If Not IsOverlayAnnotation(annotation) Then Continue For

                    Dim layerRect = ImageProcessor.ComputeAnnotationDirtyRect(sourceWidth, sourceHeight, annotation, baseWidth, baseHeight)
                    If Not Intersects(rect, layerRect) Then Continue For

                    If IsNonNormalBlend(annotation) Then requiresComposite = True
                    Dim union = ImageProcessor.UnionRects(rect, layerRect)
                    If union.Left <> rect.Left OrElse union.Top <> rect.Top OrElse
                       union.Right <> rect.Right OrElse union.Bottom <> rect.Bottom Then
                        rect = union
                        changed = True
                    End If
                Next
            End While

            Return (requiresComposite, rect)
        End Function

        ''' <summary>
        ''' Composite-Rechteck für die GANZE Szene: die Vereinigung der Composite-Bereiche JEDES
        ''' Nicht-Normal-Blend-Overlay-Objekts (jeweils inkl. der per Z-Order darüberliegenden
        ''' Schnittmengen) - unabhängig davon, welches Objekt gerade selektiert/geändert ist.
        '''
        ''' Nötig, weil das Selektieren eines Normal-Blend-Objekts sonst den Composite-Patch eines
        ''' anderen (z.B. darunterliegenden) Blend-Objekts verwirft: die indexbezogene
        ''' <see cref="ComputeBlendCompositeRect"/> schaut nur vom geänderten Objekt aufwärts und
        ''' meldet dann fälschlich "kein Composite nötig", worauf der Aufrufer den Patch löscht und
        ''' der Mischmodus des anderen Objekts verschwindet.
        ''' </summary>
        Public Shared Function ComputeSceneBlendCompositeRect(annotations As IReadOnlyList(Of ImageAnnotation),
                                                              sourceWidth As Integer,
                                                              sourceHeight As Integer,
                                                              Optional baseWidth As Integer = 0,
                                                              Optional baseHeight As Integer = 0) As (RequiresComposite As Boolean, Rect As SKRectI)
            If annotations Is Nothing OrElse sourceWidth <= 0 OrElse sourceHeight <= 0 Then Return (False, SKRectI.Empty)

            Dim requiresComposite = False
            Dim rect = SKRectI.Empty
            For i = 0 To annotations.Count - 1
                Dim annotation = annotations(i)
                If Not IsOverlayAnnotation(annotation) OrElse Not IsNonNormalBlend(annotation) Then Continue For

                Dim dependency = ComputeBlendCompositeRect(annotations, i, sourceWidth, sourceHeight, SKRectI.Empty, baseWidth, baseHeight)
                If dependency.RequiresComposite AndAlso Not dependency.Rect.IsEmpty Then
                    requiresComposite = True
                    rect = ImageProcessor.UnionRects(rect, dependency.Rect)
                End If
            Next

            ' HINWEIS: Ein frueheres transitives Wachsen des Patch (damit jedes beruehrte
            ' Objekt VOLL enthalten ist und kein Kanten-Versatz entsteht) liess den Patch bei sich
            ' kettenden Objekten riesig werden (gemessen 5021x3495 ~17,5 MP, ~2 s/Render, staendig) und
            ' machte den Blend praktisch unbrauchbar. Der Patch bleibt deshalb auf die Blend-Abhaengigkeits-
            ' bereiche begrenzt. Die Versatz-Frage wird spaeter BEGRENZT/gezielt geloest.
            Return (requiresComposite, rect)
        End Function

        ' ===================== Kompositor (OFFENE_PUNKTE Abschnitt 2, Stufe 3) =====================

        ''' <summary>Die EINE Stelle, die den gebackenen Block vom Kompositor trennt (Vorgriff auf
        ''' Stufe 5 des Umbaus): Objekte AB diesem Stapel-Index zeichnet der Kompositor aus dem
        ''' Objekt-Bitmap-Cache ueber die Szene, alles darunter bleibt in der Szene gebacken.
        '''
        ''' Gebacken bleiben MUSS, was das Komposit unter sich braucht oder was der Cache nicht
        ''' zeichnet - und wegen der Z-REIHENFOLGE zwingend auch ALLES DARUNTER, als
        ''' zusammenhaengender Block am unteren Stapelende. Sonst zeichnete der Kompositor ein
        ''' tiefer liegendes Objekt OBEN auf ein gebacken gemischtes:
        ''' - Mischmodus (braucht das fertige Komposit unter sich),
        ''' - Pinsel-/Radierer-Ebenen (Striche im Bildraum, keine freistehende Objektflaeche),
        ''' - verzerrte Objekte, aber nur noch teilweise (siehe <see cref="MustBakeForWarp"/>),
        ''' - Objekte mit Ebenen- oder Schnittmaske (die Deckung entsteht beim KOMPONIEREN, sie steckt
        '''   nicht in der Objekt-Bitmap; die Schnittmaske braucht ausserdem die Basis unter sich),
        ''' - Objekte, ueber denen eine eingehaengte Korrektur liegt (sie wirkt auf das Komposit).</summary>
        Public Shared Function ComputeCompositorStartIndex(adj As ImageAdjustments) As Integer
            If adj Is Nothing Then Return 0
            Return ComputeCompositorStartIndex(adj.Annotations, adj.MaskedAdjustmentLayers,
                                               Function(a) adj.IsAnnotationRenderVisible(a))
        End Function

        ''' <summary>Muss dieses Objekt wegen einer Verzerrung im gebackenen Block bleiben?
        '''
        ''' Frueher hiess die Antwort schlicht "jedes verzerrte Objekt", weil der Objekt-Cache die
        ''' Verzerrung nicht zeichnete. Seit dem 2026-08-03 rechnet er die EIGENE Verzerrung des
        ''' Objekts in seine Bitmap, und damit bleiben nur noch zwei Faelle uebrig:
        '''
        ''' BILDverzerrung: sie gehoert nicht dem Objekt, sondern der Geometriekette, und die laeuft
        ''' vor dem Kompositor. Ein Objekt, das von ihr erfasst wird, muss also mit ihr gebacken
        ''' werden.
        '''
        ''' Verzerrt UND gedreht: hier entscheidet die REIHENFOLGE, und die beiden Wege haben sie
        ''' verschieden. Der Kompositor dreht die fertige - also bereits verzerrte - Bitmap; der
        ''' gebackene Weg verzerrt, was schon gedreht gezeichnet wurde. Erst drehen und dann
        ''' verzerren ist etwas anderes als erst verzerren und dann drehen, und der Unterschied ist
        ''' bei schraegen Winkeln deutlich sichtbar. Solange das nicht aufgeloest ist, bleibt dieser
        ''' Fall gebacken - lieber langsam richtig als schnell daneben.</summary>
        Friend Shared Function MustBakeForWarp(annotation As ImageAnnotation) As Boolean
            If annotation Is Nothing Then Return False
            If annotation.Warp IsNot Nothing AndAlso Not annotation.Warp.IsEmpty Then Return True
            If annotation.OwnWarp Is Nothing OrElse annotation.OwnWarp.IsEmpty Then Return False
            Return Math.Abs(annotation.RotationDegrees) > 0.01F
        End Function

        ''' <summary>Dieselbe Grenze auf den LEBENDEN Listen des Editors - die Sichtbarkeitsfrage
        ''' kommt als Funktion herein, weil die Gruppensichtbarkeit beim Rezept-Klon in
        ''' ImageAdjustments steckt, im Editor aber in dessen eigener Gruppenliste.</summary>
        Public Shared Function ComputeCompositorStartIndex(annotations As IReadOnlyList(Of ImageAnnotation),
                                                           maskedLayers As IEnumerable(Of MaskedAdjustmentLayer),
                                                           isRenderVisible As Func(Of ImageAnnotation, Boolean)) As Integer
            If annotations Is Nothing OrElse annotations.Count = 0 Then Return 0
            Dim stackedAboveIds As New HashSet(Of String)(StringComparer.Ordinal)
            If maskedLayers IsNot Nothing Then
                For Each layer In maskedLayers
                    If layer IsNot Nothing AndAlso Not String.IsNullOrEmpty(layer.StackAboveAnnotationId) Then
                        stackedAboveIds.Add(layer.StackAboveAnnotationId)
                    End If
                Next
            End If

            Dim startIndex = 0
            For i = 0 To annotations.Count - 1
                Dim annotation = annotations(i)
                If annotation Is Nothing Then Continue For
                ' UNSICHTBARE Objekte erzwingen nichts: sie werden weder gebacken noch komponiert.
                If isRenderVisible IsNot Nothing AndAlso Not isRenderVisible(annotation) Then Continue For
                Dim kind = If(annotation.Kind, "").Trim().ToLowerInvariant()
                Dim mustBake = kind = "brush" OrElse kind = "eraser" OrElse
                               IsNonNormalBlend(annotation) OrElse
                               MustBakeForWarp(annotation) OrElse
                               ImageProcessor.UsesLayerCoverage(annotation) OrElse
                               stackedAboveIds.Contains(If(annotation.Id, ""))
                If mustBake Then startIndex = i + 1
            Next
            Return startIndex
        End Function

        ''' <summary>Zeichnet die Kompositor-Objekte (ab <see cref="ComputeCompositorStartIndex"/>)
        ''' aus dem Objekt-Bitmap-Cache ueber die Szene. Die PLATZIERUNG ist dieselbe wie beim
        ''' Backen: TransformAnnotationForGeometry liefert das Objekt im Szenenraum (Schriftgrad und
        ''' Konturbreite bereits skaliert), ComputeAnnotationRect loest den Anker auf, die Drehung
        ''' liegt als Matrix um die Rechteckmitte (die Spiegelung steckt in der Cache-Bitmap, wie
        ''' beim Ghost). Rueckgabe: wie viele Objekte gezeichnet wurden (Messpunkt).
        '''
        ''' NUR auf dem UI-Thread aufrufen - der Cache entsorgt Bitmaps bei Invalidierung sofort,
        ''' und genau dieser Thread ist der einzige, der invalidiert (Vertrag des Caches).</summary>
        Public Shared Function DrawCachedAnnotations(canvas As SKCanvas, adj As ImageAdjustments,
                                                     sceneWidth As Integer, sceneHeight As Integer,
                                                     cache As AnnotationBitmapCache) As Integer
            If canvas Is Nothing OrElse adj Is Nothing OrElse cache Is Nothing Then Return 0
            If adj.Annotations Is Nothing OrElse adj.Annotations.Count = 0 Then Return 0
            If sceneWidth <= 0 OrElse sceneHeight <= 0 Then Return 0

            Dim drawn = 0
            Dim startIndex = ComputeCompositorStartIndex(adj)
            For i = startIndex To adj.Annotations.Count - 1
                Dim annotation = adj.Annotations(i)
                If annotation Is Nothing OrElse Not IsOverlayAnnotation(annotation) Then Continue For
                If Not adj.IsAnnotationRenderVisible(annotation) Then Continue For

                Dim renderAnnotation = ImageProcessor.TransformAnnotationForGeometry(annotation, adj, sceneWidth, sceneHeight)
                If renderAnnotation Is Nothing Then Continue For
                Dim kind = If(renderAnnotation.Kind, "Text").Trim().ToLowerInvariant()
                ' Modul-Funktion (gleiches Namespace): loest auch den Anker eines Wasserzeichens auf.
                Dim rect = ComputeAnnotationRect(sceneWidth, sceneHeight, kind, renderAnnotation)
                If rect.Width <= 0 OrElse rect.Height <= 0 Then Continue For

                Dim targetW = Math.Max(1, CInt(Math.Round(rect.Width)))
                Dim targetH = Math.Max(1, CInt(Math.Round(rect.Height)))
                Dim entry = cache.GetOrRender(renderAnnotation, targetW, targetH)
                If entry Is Nothing OrElse entry.Image Is Nothing Then Continue For
                If entry.ObjectWidth <= 0 OrElse entry.ObjectHeight <= 0 Then Continue For

                ' Das Bild so legen, dass seine OBJEKTflaeche exakt auf dem Rechteck landet - die
                ' Effektraender (Schatten/Gluehen) liegen aussen herum und ragen entsprechend
                ' darueber hinaus, wie beim gebackenen Bild.
                Dim scaleX = rect.Width / CSng(entry.ObjectWidth)
                Dim scaleY = rect.Height / CSng(entry.ObjectHeight)
                Dim dest = New SKRect(rect.Left - entry.ObjectX * scaleX,
                                      rect.Top - entry.ObjectY * scaleY,
                                      rect.Left - entry.ObjectX * scaleX + entry.Image.Width * scaleX,
                                      rect.Top - entry.ObjectY * scaleY + entry.Image.Height * scaleY)

                canvas.Save()
                If Math.Abs(renderAnnotation.RotationDegrees) > 0.01F Then
                    canvas.RotateDegrees(renderAnnotation.RotationDegrees, rect.MidX, rect.MidY)
                End If
                ' LINEAR abtasten: ohne Sampling zeichnet Skia das gedrehte Bild mit
                ' Naechster-Nachbar-Abtastung, und jede Kante wird zur Treppe
                ' (Nutzer-Screenshot 2026-07-31). Mit linearer Abtastung entspricht die Qualitaet
                ' dem Ghost von frueher; unverdreht ist die Abbildung ohnehin 1:1.
                Using paint As New SKPaint With {.IsAntialias = True}
                    canvas.DrawImage(entry.Image, dest, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint)
                End Using
                canvas.Restore()
                drawn += 1
            Next
            Return drawn
        End Function
    End Class
End Namespace
