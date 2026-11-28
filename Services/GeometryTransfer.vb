Namespace Services

    ''' <summary>Welche Teile der Geometrie "Einfügen mit Auswahl" auf das Ziel überträgt. Ab Werk
    ''' keiner (<see cref="None"/>); die Wahl wird gemerkt (AppSettings.PasteGeometry*).</summary>
    <Flags>
    Public Enum GeometryTransferParts
        None = 0
        ''' Die Schritte der Art "transform": Begradigung samt ihren Leinwandregeln, Vierteldrehung
        ''' und Spiegelung.
        Straighten = 1
        ''' Die Schritte der Art "perspective": Kippregler, Seitenverhältnis, Größe und die acht
        ''' Eckenversätze.
        Perspective = 2
        ''' Die Schritte der Art "crop".
        Crop = 4
    End Enum

    ''' <summary>Was beim Übertragen der Geometrie geschah, für die Rückmeldung.</summary>
    Public NotInheritable Class GeometryTransferOutcome
        ''' Die Teile, die das Ziel jetzt vom kopierten Bild trägt.
        Public Property Applied As GeometryTransferParts = GeometryTransferParts.None
        ''' Der Zuschnitt war gewählt, steht aber nicht im Ziel: das Ziel steht hoch, wo das kopierte
        ''' Bild quer stand, oder umgekehrt. Der Zuschnitt des Ziels ist dann unverändert.
        Public Property CropSkipped As Boolean
    End Class

    ''' <summary>Die Geometrie eines Bildes beim Kopieren in die Ablage legen und beim Einfügen auf
    ''' ein anderes übertragen: Ausrichten, Perspektive und Zuschnitt, jeder Teil einzeln wählbar.
    ''' Gedacht für Serien vom Stativ, bei denen jedes Bild dieselbe Korrektur braucht.
    '''
    ''' DIE ABLAGE TRÄGT IMMER ALLE DREI ARTEN, gewählt wird erst beim Einfügen ("Einfügen mit
    ''' Auswahl"). Ihre Schritte stehen in der Ablage in <c>GeometryOperations</c>, das Maß des
    ''' Quellbildes in <c>SourceWidthPixels</c>/<c>SourceHeightPixels</c>. Beides gehört zu den
    ''' strukturellen Feldern (ImageAdjustments.StructuralPropertyNames): das gewöhnliche Einfügen
    ''' und jede Vorlage sehen es nicht, es wirkt nur über <see cref="Apply"/>.
    '''
    ''' EIN GEWÄHLTER TEIL ERSETZT, wie ein gewählter Regler: alle Schritte dieser Art im Ziel fallen
    ''' weg, die der Ablage kommen hinein. Trägt die Ablage keinen, ist der Teil danach leer. Die
    ''' übrigen Schritte des Ziels (Verzerren, Bildgröße, Leinwand, ein nicht gewählter Zuschnitt)
    ''' bleiben in ihrer Reihenfolge.
    '''
    ''' DIE REIHENFOLGE folgt der Regel des Editors, dass ein bestätigter Zuschnitt am Ende der Kette
    ''' steht: vorn Ausrichten und Perspektive in der Reihenfolge der Ablage, dann die übrigen
    ''' Schritte des Ziels, zuletzt der Zuschnitt der Ablage.
    '''
    ''' DER ZUSCHNITT STEHT IN PROZENT des Bildes, das an seiner Stelle der Kette ankommt. Steht
    ''' dieses Bild im Ziel hoch und im Quellbild quer (oder umgekehrt), wird er ausgelassen, statt
    ''' einen Ausschnitt in verkehrtem Format zu schneiden; das Ergebnis meldet es
    ''' (<see cref="GeometryTransferOutcome.CropSkipped"/>). Gleiche Lage bei anderem
    ''' Seitenverhältnis (3:2 und 4:3) gilt als passend: die Ränder bleiben anteilig dieselben.
    '''
    ''' OBJEKTE, MASKEN UND RETUSCHE stehen im Quellraum und folgen Ausrichten und Zuschnitt über die
    ''' Abbildung von selbst, wie beim Drehen und Zuschneiden im Editor. Die Perspektive ist
    ''' projektiv; der Editor trägt sie beim Bestätigen in das eigene Verzerrungsfeld jedes Objekts
    ''' ein (EditorViewModel.ApplyPerspectiveToObjects), und genau das geschieht hier mit jeder
    ''' übertragenen Perspektive (<see cref="BakePerspectiveIntoAnnotations"/>). Eine Perspektive,
    ''' die dabei im Ziel wegfällt, bleibt wie beim Zurücknehmen im Editor (ResetImageWarp) in den
    ''' Objekten stehen.</summary>
    Public NotInheritable Class GeometryTransfer

        ''' Die Rasterfeinheit, in der eine Perspektive in ein Objekt eingetragen wird. Dieselbe Zahl
        ''' wie MaxImageWarpSteps im Editor: Bild und Objekt muessen auf derselben Feinheit
        ''' ausgewertet werden, sonst zeigen sie im Export andere Knicke.
        Private Const ObjectWarpSteps As Integer = 48

        Private Sub New()
        End Sub

        ''' <summary>Die Geometriearten, die übertragen werden können, als Kleinbuchstaben wie in
        ''' GeometryOperation.Kind.</summary>
        Private Shared Function KindOf(part As GeometryTransferParts) As String
            Select Case part
                Case GeometryTransferParts.Straighten : Return "transform"
                Case GeometryTransferParts.Perspective : Return "perspective"
                Case GeometryTransferParts.Crop : Return "crop"
            End Select
            Return ""
        End Function

        Private Shared Function IsKind(operation As GeometryOperation, kind As String) As Boolean
            Return operation IsNot Nothing AndAlso
                   String.Equals(If(operation.Kind, "").Trim(), kind, StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function IsTransferableKind(operation As GeometryOperation) As Boolean
            Return IsKind(operation, "transform") OrElse IsKind(operation, "perspective") OrElse IsKind(operation, "crop")
        End Function

        ''' <summary>Die zuletzt gewählten Teile, ab Werk keiner.</summary>
        Public Shared Function RememberedParts() As GeometryTransferParts
            Dim settings = AppSettingsService.Load()
            Dim parts = GeometryTransferParts.None
            If settings.PasteGeometryStraighten Then parts = parts Or GeometryTransferParts.Straighten
            If settings.PasteGeometryPerspective Then parts = parts Or GeometryTransferParts.Perspective
            If settings.PasteGeometryCrop Then parts = parts Or GeometryTransferParts.Crop
            Return parts
        End Function

        ''' <summary>Merkt die gewählten Teile für das nächste "Einfügen mit Auswahl".</summary>
        Public Shared Sub RememberParts(parts As GeometryTransferParts)
            AppSettingsService.Update(Sub(s)
                                          s.PasteGeometryStraighten = parts.HasFlag(GeometryTransferParts.Straighten)
                                          s.PasteGeometryPerspective = parts.HasFlag(GeometryTransferParts.Perspective)
                                          s.PasteGeometryCrop = parts.HasFlag(GeometryTransferParts.Crop)
                                      End Sub)
        End Sub

        ''' <summary>Legt die Geometrie aus <paramref name="source"/> in die Ablage
        ''' <paramref name="clipboard"/>: die Schritte von Ausrichten, Perspektive und Zuschnitt in
        ''' ihrer Reihenfolge und das Maß des Quellbildes. Ohne bekanntes Maß bleibt es bei 0, und der
        ''' Zuschnitt wird beim Einfügen ohne Prüfung der Lage übertragen.</summary>
        Public Shared Sub AttachTo(clipboard As ImageAdjustments, source As ImageAdjustments,
                                   sourceWidth As Integer, sourceHeight As Integer)
            If clipboard Is Nothing OrElse source Is Nothing Then Return
            clipboard.GeometryOperations = If(source.GeometryOperations, New List(Of GeometryOperation)()).
                Where(Function(o) IsTransferableKind(o) AndAlso o.Adjustments IsNot Nothing).
                Select(Function(o) o.Clone()).ToList()
            clipboard.SourceWidthPixels = Math.Max(0, sourceWidth)
            clipboard.SourceHeightPixels = Math.Max(0, sourceHeight)
        End Sub

        ''' <summary>Überträgt die gewählten Teile der Geometrie aus der Ablage
        ''' <paramref name="clipboard"/> auf <paramref name="target"/> (Regeln siehe oben).
        ''' <paramref name="targetWidth"/> und <paramref name="targetHeight"/> sind die Maße des
        ''' Zielbildes im Quellraum; 0 nimmt die des Rezepts. Ohne bekannte Maße wird der Zuschnitt
        ''' ohne Prüfung der Lage übertragen und keine Perspektive in Objekte eingetragen.
        '''
        ''' Die oberen Geometriefelder des gewählten Teils gehen auf null: der Editor liest sie beim
        ''' Laden als offenen Zustand, und eine alte Drehung dort käme sonst als neuer Schritt
        ''' zurück.</summary>
        Public Shared Function Apply(target As ImageAdjustments, clipboard As ImageAdjustments,
                                     parts As GeometryTransferParts,
                                     Optional targetWidth As Integer = 0,
                                     Optional targetHeight As Integer = 0) As GeometryTransferOutcome
            Dim outcome As New GeometryTransferOutcome()
            If target Is Nothing OrElse clipboard Is Nothing OrElse parts = GeometryTransferParts.None Then Return outcome
            If targetWidth <= 0 OrElse targetHeight <= 0 Then
                targetWidth = target.SourceWidthPixels
                targetHeight = target.SourceHeightPixels
            End If

            Dim incoming = If(clipboard.GeometryOperations, New List(Of GeometryOperation)()).
                Where(Function(o) o IsNot Nothing AndAlso o.Adjustments IsNot Nothing).ToList()
            Dim chosenKinds As New List(Of String)()
            For Each part In {GeometryTransferParts.Straighten, GeometryTransferParts.Perspective, GeometryTransferParts.Crop}
                If parts.HasFlag(part) Then chosenKinds.Add(KindOf(part))
            Next
            Dim isChosen = Function(o As GeometryOperation) chosenKinds.Any(Function(k) IsKind(o, k))
            Dim existing = If(target.GeometryOperations, New List(Of GeometryOperation)())

            Dim front = incoming.Where(Function(o) isChosen(o) AndAlso Not IsKind(o, "crop")).Select(Function(o) o.Clone()).ToList()
            Dim incomingCrop = incoming.Where(Function(o) isChosen(o) AndAlso IsKind(o, "crop")).Select(Function(o) o.Clone()).ToList()

            ' Der Zuschnitt nur bei gleicher Lage. Gemessen wird das Bild, das an seiner Stelle der
            ' Kette ankommt: im Quellbild alles vor seinem ersten Zuschnitt, im Ziel die neue Kette
            ' ohne ihn.
            Dim transferCrop = parts.HasFlag(GeometryTransferParts.Crop)
            If transferCrop AndAlso incomingCrop.Count > 0 Then
                Dim remainingWithoutCrop = existing.Where(Function(o) o IsNot Nothing AndAlso Not isChosen(o)).ToList()
                Dim targetBefore = front.Concat(remainingWithoutCrop).ToList()
                Dim firstCrop = incoming.FindIndex(Function(o) IsKind(o, "crop"))
                Dim sourceBefore = incoming.Take(Math.Max(0, firstCrop)).ToList()
                If Not SameOrientation(clipboard.SourceWidthPixels, clipboard.SourceHeightPixels, sourceBefore,
                                       targetWidth, targetHeight, targetBefore) Then
                    transferCrop = False
                    outcome.CropSkipped = True
                End If
            End If

            ' Ein ausgelassener Zuschnitt laesst den des Ziels stehen: gewaehlt war, ihn zu ersetzen,
            ' nicht, ihn wegzunehmen.
            Dim replacesKind = Function(o As GeometryOperation)
                                   If Not isChosen(o) Then Return False
                                   If IsKind(o, "crop") Then Return transferCrop
                                   Return True
                               End Function
            Dim remaining = existing.Where(Function(o) o IsNot Nothing AndAlso Not replacesKind(o)).ToList()
            Dim result As New List(Of GeometryOperation)(front)
            result.AddRange(remaining)
            If transferCrop Then result.AddRange(incomingCrop)

            If parts.HasFlag(GeometryTransferParts.Straighten) Then
                target.RotationDegrees = 0
                target.StraightenDegrees = 0
                target.FlipHorizontal = False
                target.FlipVertical = False
                outcome.Applied = outcome.Applied Or GeometryTransferParts.Straighten
            End If
            If parts.HasFlag(GeometryTransferParts.Perspective) Then
                target.PerspectiveHorizontal = 0 : target.PerspectiveVertical = 0
                target.PerspectiveAspect = 0 : target.PerspectiveScale = 0
                target.PerspectiveCorner0X = 0 : target.PerspectiveCorner0Y = 0
                target.PerspectiveCorner1X = 0 : target.PerspectiveCorner1Y = 0
                target.PerspectiveCorner2X = 0 : target.PerspectiveCorner2Y = 0
                target.PerspectiveCorner3X = 0 : target.PerspectiveCorner3Y = 0
                outcome.Applied = outcome.Applied Or GeometryTransferParts.Perspective
            End If
            If transferCrop Then
                target.CropLeftPercent = 0 : target.CropTopPercent = 0
                target.CropRightPercent = 0 : target.CropBottomPercent = 0
                outcome.Applied = outcome.Applied Or GeometryTransferParts.Crop
            End If
            target.GeometryOperations = result

            ' Objekte nehmen jede uebertragene Perspektive mit, wie beim Bestaetigen im Editor.
            If parts.HasFlag(GeometryTransferParts.Perspective) AndAlso targetWidth > 0 AndAlso targetHeight > 0 Then
                For index = 0 To result.Count - 1
                    If IsKind(result(index), "perspective") AndAlso front.Contains(result(index)) Then
                        BakePerspectiveIntoAnnotations(target, index, targetWidth, targetHeight)
                    End If
                Next
            End If
            Return outcome
        End Function

        ''' <summary>Stehen beide Bilder gleich, also beide quer, beide hoch oder eines davon fast
        ''' quadratisch? Gemessen hinter den Schritten <paramref name="sourceSteps"/> bzw.
        ''' <paramref name="targetSteps"/>. Ohne bekannte Maße gilt die Lage als passend.</summary>
        Private Shared Function SameOrientation(sourceWidth As Integer, sourceHeight As Integer, sourceSteps As List(Of GeometryOperation),
                                                targetWidth As Integer, targetHeight As Integer, targetSteps As List(Of GeometryOperation)) As Boolean
            If sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse targetWidth <= 0 OrElse targetHeight <= 0 Then Return True
            Dim sourceSize = ImageProcessor.ComputeGeometryOutputSize(sourceWidth, sourceHeight,
                New ImageAdjustments With {.GeometryOperations = sourceSteps.Select(Function(o) o.Clone()).ToList(),
                                           .SourceWidthPixels = sourceWidth, .SourceHeightPixels = sourceHeight})
            Dim targetSize = ImageProcessor.ComputeGeometryOutputSize(targetWidth, targetHeight,
                New ImageAdjustments With {.GeometryOperations = targetSteps.Select(Function(o) o.Clone()).ToList(),
                                           .SourceWidthPixels = targetWidth, .SourceHeightPixels = targetHeight})
            Dim sourceLie = OrientationOf(sourceSize.Width, sourceSize.Height)
            Dim targetLie = OrientationOf(targetSize.Width, targetSize.Height)
            Return sourceLie = 0 OrElse targetLie = 0 OrElse sourceLie = targetLie
        End Function

        ''' 1 quer, -1 hoch, 0 fast quadratisch (unter einem Prozent Unterschied) oder unbekannt.
        Private Shared Function OrientationOf(width As Integer, height As Integer) As Integer
            If width <= 0 OrElse height <= 0 Then Return 0
            If width > height * 1.01 Then Return 1
            If height > width * 1.01 Then Return -1
            Return 0
        End Function

        ''' <summary>Trägt den Perspektivschritt an <paramref name="stepIndex"/> in das eigene
        ''' Verzerrungsfeld jedes Objekts ein, wie EditorViewModel.ApplyPerspectiveToObjects beim
        ''' Bestätigen: Quellpunkt über die Objektkette VOR dem Schritt in den Anzeigeraum, dort die
        ''' Perspektive, wieder zurück. Ein schon eingetragenes Feld wird zuerst ausgewertet, dann der
        ''' neue Schritt, in der Reihenfolge der Kette.</summary>
        Friend Shared Sub BakePerspectiveIntoAnnotations(target As ImageAdjustments, stepIndex As Integer,
                                                         baseWidth As Integer, baseHeight As Integer)
            If target?.Annotations Is Nothing OrElse target.Annotations.Count = 0 Then Return
            If target.GeometryOperations Is Nothing OrElse stepIndex < 0 OrElse stepIndex >= target.GeometryOperations.Count Then Return
            If baseWidth <= 0 OrElse baseHeight <= 0 Then Return
            Dim perspective = target.GeometryOperations(stepIndex).Adjustments
            If perspective Is Nothing Then Return

            Dim before As New ImageAdjustments With {
                .GeometryOperations = target.GeometryOperations.Take(stepIndex).Select(Function(o) o.Clone()).ToList(),
                .SourceWidthPixels = baseWidth, .SourceHeightPixels = baseHeight}
            Dim size = ImageProcessor.ComputeGeometryOutputSize(baseWidth, baseHeight, before)
            If size.Width <= 0 OrElse size.Height <= 0 Then Return
            Dim corners = New Double() {perspective.PerspectiveCorner0X, perspective.PerspectiveCorner0Y,
                                        perspective.PerspectiveCorner1X, perspective.PerspectiveCorner1Y,
                                        perspective.PerspectiveCorner2X, perspective.PerspectiveCorner2Y,
                                        perspective.PerspectiveCorner3X, perspective.PerspectiveCorner3Y}
            Dim matrix = ImageGeometryMapper.WarpMatrix(size.Width, size.Height,
                                                        perspective.PerspectiveHorizontal, perspective.PerspectiveVertical,
                                                        perspective.PerspectiveAspect, perspective.PerspectiveScale, corners)
            If matrix.IsIdentity Then Return
            Dim objectGeometry = ImageProcessor.GeometryForAnnotations(before)

            Dim mapping = Function(px As Double, py As Double) As (X As Double, Y As Double)
                              Dim shown As SkiaSharp.SKPoint
                              If Not ImageProcessor.TrySourcePointToGeometryOutput(px / 100.0 * baseWidth, py / 100.0 * baseHeight,
                                                                                   baseWidth, baseHeight, objectGeometry, shown) Then Return (px, py)
                              Dim moved = matrix.MapPoint(shown)
                              Dim back As SkiaSharp.SKPoint
                              If Not ImageProcessor.TryGeometryOutputToSourcePoint(moved.X, moved.Y, baseWidth, baseHeight,
                                                                                   objectGeometry, back) Then Return (px, py)
                              Return (back.X / baseWidth * 100.0, back.Y / baseHeight * 100.0)
                          End Function

            For Each annotation In target.Annotations
                If annotation Is Nothing Then Continue For
                Dim previous = annotation.Warp
                Dim nodes((ObjectWarpSteps + 1) * (ObjectWarpSteps + 1) * 2 - 1) As Double
                For rowIndex = 0 To ObjectWarpSteps
                    For columnIndex = 0 To ObjectWarpSteps
                        Dim i = (rowIndex * (ObjectWarpSteps + 1) + columnIndex) * 2
                        Dim px = columnIndex / CDbl(ObjectWarpSteps) * 100.0
                        Dim py = rowIndex / CDbl(ObjectWarpSteps) * 100.0
                        If previous IsNot Nothing AndAlso Not previous.IsEmpty AndAlso previous.Kind = "Gitter" Then
                            Dim p = ImageGeometryMapper.MeshPoint(previous.Nodes, previous.Columns, previous.Rows, px, py, 100.0, 100.0)
                            px = p.X : py = p.Y
                        End If
                        Dim n = mapping(px, py)
                        nodes(i) = n.X
                        nodes(i + 1) = n.Y
                    Next
                Next
                annotation.Warp = New ObjectWarp With {
                    .Kind = "Gitter", .Columns = ObjectWarpSteps, .Rows = ObjectWarpSteps, .Nodes = nodes}
            Next
        End Sub

    End Class

End Namespace
