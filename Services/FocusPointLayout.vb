Imports System
Imports System.Collections.Generic

Namespace Services

    ''' <summary>Was der Betrachter zu den Fokusfeldern des gezeigten Bildes sagen kann. Alles
    ''' ausser <see cref="FocusPointStatus.Off"/> und <see cref="FocusPointStatus.Shown"/> ist ein
    ''' Grund, warum trotz eingeschaltetem Schalter nichts zu sehen ist.</summary>
    Public Enum FocusPointStatus
        ''' <summary>Schalter aus, kein Bild oder noch in Arbeit.</summary>
        Off
        Shown
        ''' <summary>Die Aufnahmedaten tragen keine Angabe.</summary>
        NoData
        ''' <summary>Die Kamera hat kein Feld gemeldet.</summary>
        Empty
        ''' <summary>Nur die Nummer eines Messfelds, keine Lage.</summary>
        IndexOnly
        ''' <summary>Die Angabe bezieht sich auf ein anderes Bildformat als das gezeigte.</summary>
        Mismatch
        ''' <summary>Der Zuschnitt des Rezepts hat das Feld weggenommen.</summary>
        OutsideCrop
    End Enum

    ''' <summary>Ein Fokusfeld fuer die Anzeige, in Anteilen des GEZEIGTEN Bildes (0 bis 1). Ohne
    ''' Groesse (<see cref="HasSize"/> False) sind die vier Kanten ein Punkt, und die Ansicht
    ''' zeichnet ein Kreuz statt eines Rahmens, der eine Genauigkeit vortaeuschte.</summary>
    Public NotInheritable Class FocusFrame
        Public Sub New(left As Double, top As Double, right As Double, bottom As Double, hasSize As Boolean)
            Me.Left = left
            Me.Top = top
            Me.Right = right
            Me.Bottom = bottom
            Me.HasSize = hasSize
        End Sub

        Public ReadOnly Property Left As Double
        Public ReadOnly Property Top As Double
        Public ReadOnly Property Right As Double
        Public ReadOnly Property Bottom As Double
        Public ReadOnly Property HasSize As Boolean

        Public ReadOnly Property CenterX As Double
            Get
                Return (Left + Right) / 2
            End Get
        End Property

        Public ReadOnly Property CenterY As Double
            Get
                Return (Top + Bottom) / 2
            End Get
        End Property
    End Class

    ''' <summary>Bringt ein Fokusfeld aus den Aufnahmedaten auf das gezeigte Bild.
    '''
    ''' Zwei Schritte. Die Kamera legt die Lage im UNGEDREHTEN Bild ab; der Betrachter zeigt es
    ''' aufgerichtet. Zuerst also die Ausrichtung aus den Aufnahmedaten (<see cref="ToUpright"/>),
    ''' dieselben acht Faelle wie <see cref="ImageOrientationService.ApplyOrientation"/>. Danach
    ''' liegt das Feld im selben Raum wie der Kasten eines Gesichts, und den Rest (Geometrie des
    ''' Rezepts oder gespeicherte Vierteldrehung) rechnet <see cref="FaceFrameLayout.MapBox"/>.
    '''
    ''' Reine Funktionen ohne Anwendungszustand, damit sie ohne Oberflaeche pruefbar sind.</summary>
    Public NotInheritable Class FocusPointLayout

        Private Sub New()
        End Sub

        ''' <summary>Ein Feld (Mitte und Groesse als Anteile) aus dem ungedrehten Bild ins
        ''' aufgerichtete. Bei den vier Faellen mit Vierteldrehung tauschen Breite und Hoehe.</summary>
        Public Shared Function ToUpright(x As Double, y As Double, width As Double, height As Double,
                                         orientation As Integer) As (X As Double, Y As Double, Width As Double, Height As Double)
            Select Case orientation
                Case 2 : Return (1.0 - x, y, width, height)
                Case 3 : Return (1.0 - x, 1.0 - y, width, height)
                Case 4 : Return (x, 1.0 - y, width, height)
                Case 5 : Return (y, x, height, width)
                Case 6 : Return (1.0 - y, x, height, width)
                Case 7 : Return (1.0 - y, 1.0 - x, height, width)
                Case 8 : Return (y, 1.0 - x, height, width)
                Case Else : Return (x, y, width, height)
            End Select
        End Function

        ''' <summary>True, wenn die Flaeche, auf die sich die Felder beziehen, ein anderes
        ''' Seitenverhaeltnis hat als das Bild, auf das sie gelegt werden sollen. Dann hat die
        ''' Kamera in einem anderen Format gemessen als gespeichert (oder die eingebettete
        ''' Vorschau ist anders beschnitten), und die Anteile traefen daneben. Beide Groessen
        ''' ungedreht oder beide aufgerichtet; ohne Angabe laesst sich nichts sagen.</summary>
        Public Shared Function AspectDiffers(referenceWidth As Double, referenceHeight As Double,
                                             imageWidth As Double, imageHeight As Double) As Boolean
            If referenceWidth <= 0 OrElse referenceHeight <= 0 OrElse imageWidth <= 0 OrElse imageHeight <= 0 Then Return False
            Dim reference = referenceWidth / referenceHeight
            Dim image = imageWidth / imageHeight
            Return Math.Abs(reference - image) > image * AspectTolerance
        End Function

        ''' Zwei Prozent: die Bezugsflaeche weicht regelmaessig um ein paar Bildpunkte vom Bild ab
        ''' (640 x 428 fuer ein Bild im Verhaeltnis 3:2), ein anderes Format (16:9 gegen 3:2)
        ''' liegt weit darueber.
        Private Const AspectTolerance As Double = 0.02

        ''' Die Kante des Kastens, der fuer ein Feld ohne Groesse durch die Abbildung geschickt
        ''' wird, als Anteil. Klein genug, um als Punkt zu gelten, gross genug fuer MapBox.
        Private Const PointProbe As Double = 0.002

        ''' <summary>Die Felder einer Lesung auf dem gezeigten Bild. Felder, die der Zuschnitt
        ''' wegnimmt, fallen weg.</summary>
        ''' <param name="referenceWidth">Die aufgerichtete Groesse des Bildes, auf dem auch die
        ''' Gesichtskaesten liegen (<see cref="XmpFaceRegionService.ReadOrientedPixelSize"/>).</param>
        Public Shared Function Map(reading As FocusPointReading, referenceWidth As Integer, referenceHeight As Integer,
                                   geometry As ImageAdjustments, quarterTurn As Integer) As List(Of FocusFrame)
            Dim result As New List(Of FocusFrame)()
            If reading Is Nothing OrElse reading.State <> FocusPointState.Found Then Return result
            If referenceWidth <= 0 OrElse referenceHeight <= 0 Then Return result
            For Each point In reading.Points
                If point Is Nothing Then Continue For
                Dim upright = ToUpright(point.X, point.Y, point.Width, point.Height, reading.Orientation)
                Dim hasSize = point.HasSize
                Dim w = If(hasSize, upright.Width, PointProbe), h = If(hasSize, upright.Height, PointProbe)
                Dim box = FaceFrameLayout.MapBox((upright.X - w / 2) * referenceWidth, (upright.Y - h / 2) * referenceHeight,
                                                 w * referenceWidth, h * referenceHeight,
                                                 referenceWidth, referenceHeight, geometry, quarterTurn)
                If Not box.HasValue Then Continue For
                If hasSize Then
                    result.Add(New FocusFrame(box.Value.Left, box.Value.Top, box.Value.Right, box.Value.Bottom, True))
                Else
                    Dim cx = (box.Value.Left + box.Value.Right) / 2, cy = (box.Value.Top + box.Value.Bottom) / 2
                    result.Add(New FocusFrame(cx, cy, cx, cy, False))
                End If
            Next
            Return result
        End Function

        ''' <summary>Der ganze Weg von der Lesung zu den Feldern auf dem gezeigten Bild, samt dem
        ''' Grund, wenn nichts zu zeigen ist. <paramref name="frames"/> ist danach nie Nothing.
        '''
        ''' Zwei Vergleiche des Seitenverhaeltnisses schuetzen vor einer Stelle daneben: die
        ''' Flaeche der Kamera gegen das Bild, auf dem gerechnet wird, und dessen Ergebnis nach der
        ''' Geometrie gegen das Bild, das wirklich auf dem Schirm steht (eine entwickelte RAW im
        ''' vollen Format neben einer eingebetteten Vorschau in 16:9 etwa).</summary>
        ''' <param name="referenceWidth">Aufgerichtete Groesse des Bildes, auf dem auch die
        ''' Gesichtskaesten liegen; 0, wenn sie nicht lesbar war. Ohne Geometrie tritt dann das
        ''' gezeigte Bild selbst an ihre Stelle.</param>
        ''' <param name="shownWidth">Bildpunkte des gezeigten Bildes.</param>
        Public Shared Function Place(reading As FocusPointReading, referenceWidth As Integer, referenceHeight As Integer,
                                     geometry As ImageAdjustments, quarterTurn As Integer,
                                     shownWidth As Integer, shownHeight As Integer,
                                     ByRef frames As List(Of FocusFrame)) As FocusPointStatus
            frames = New List(Of FocusFrame)()
            If reading Is Nothing Then Return FocusPointStatus.NoData
            Select Case reading.State
                Case FocusPointState.Found
                Case FocusPointState.Empty : Return FocusPointStatus.Empty
                Case FocusPointState.CropMode : Return FocusPointStatus.Mismatch
                Case FocusPointState.IndexOnly : Return FocusPointStatus.IndexOnly
                Case Else : Return FocusPointStatus.NoData
            End Select

            Dim turned = ImageOrientationService.NormalizeQuarterTurn(quarterTurn)
            Dim turnSwaps = turned = 90 OrElse turned = 270
            If referenceWidth <= 0 OrElse referenceHeight <= 0 Then
                If geometry IsNot Nothing OrElse shownWidth <= 0 OrElse shownHeight <= 0 Then Return FocusPointStatus.Mismatch
                referenceWidth = If(turnSwaps, shownHeight, shownWidth)
                referenceHeight = If(turnSwaps, shownWidth, shownHeight)
            End If

            Dim orientationSwaps = reading.Orientation >= 5 AndAlso reading.Orientation <= 8
            If AspectDiffers(If(orientationSwaps, reading.ReferenceHeight, reading.ReferenceWidth),
                             If(orientationSwaps, reading.ReferenceWidth, reading.ReferenceHeight),
                             referenceWidth, referenceHeight) Then Return FocusPointStatus.Mismatch

            Dim expectedWidth = If(turnSwaps, referenceHeight, referenceWidth)
            Dim expectedHeight = If(turnSwaps, referenceWidth, referenceHeight)
            If geometry IsNot Nothing Then
                Dim output = ImageProcessor.ComputeGeometryOutputSize(referenceWidth, referenceHeight, geometry)
                expectedWidth = output.Width
                expectedHeight = output.Height
            End If
            If AspectDiffers(expectedWidth, expectedHeight, shownWidth, shownHeight) Then Return FocusPointStatus.Mismatch

            frames = Map(reading, referenceWidth, referenceHeight, geometry, quarterTurn)
            Return If(frames.Count > 0, FocusPointStatus.Shown, FocusPointStatus.OutsideCrop)
        End Function

        ''' <summary>Das Feld, auf das gezoomt wird: bei mehreren das, dessen Mitte der Bildmitte
        ''' am naechsten liegt. Nothing ohne Felder.</summary>
        Public Shared Function NearestToCenter(frames As IEnumerable(Of FocusFrame)) As FocusFrame
            If frames Is Nothing Then Return Nothing
            Dim best As FocusFrame = Nothing
            Dim bestDistance = Double.MaxValue
            For Each frame In frames
                If frame Is Nothing Then Continue For
                Dim dx = frame.CenterX - 0.5, dy = frame.CenterY - 0.5
                Dim distance = dx * dx + dy * dy
                If distance < bestDistance Then
                    bestDistance = distance
                    best = frame
                End If
            Next
            Return best
        End Function

    End Class

End Namespace
