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

            ' HINWEIS (2026-07-16): Ein frueheres transitives Wachsen des Patch (damit jedes beruehrte
            ' Objekt VOLL enthalten ist und kein Kanten-Versatz entsteht) liess den Patch bei sich
            ' kettenden Objekten riesig werden (gemessen 5021x3495 ~17,5 MP, ~2 s/Render, staendig) und
            ' machte den Blend praktisch unbrauchbar. Der Patch bleibt deshalb auf die Blend-Abhaengigkeits-
            ' bereiche begrenzt. Die Versatz-Frage (Phase 2) wird spaeter BEGRENZT/gezielt geloest.
            Return (requiresComposite, rect)
        End Function
    End Class
End Namespace
