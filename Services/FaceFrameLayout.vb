Imports SkiaSharp

Namespace Services

    ''' <summary>Wie ein Gesichtsrahmen auf dem gezeigten Bild liegt.</summary>
    Public Enum FaceFrameKind
        ''' <summary>Kein Name: nur der Rahmen, ohne Beschriftung.</summary>
        Unnamed
        ''' <summary>Der Name stammt von der Erkennung und ist nicht bestaetigt.</summary>
        Suggested
        ''' <summary>Der Benutzer hat den Namen gesetzt oder bestaetigt.</summary>
        Confirmed
    End Enum

    ''' <summary>Ein Gesichtsrahmen fuer die Anzeige, in Anteilen des GEZEIGTEN Bildes (0 bis 1).
    ''' Anteile statt Bildpunkte, weil das gezeigte Bild je nach Weg eine andere Aufloesung hat
    ''' (halbe Entwicklung, eingebettete Vorschau, volle Entwicklung).</summary>
    Public NotInheritable Class FaceFrame
        Public Sub New(left As Double, top As Double, right As Double, bottom As Double,
                       label As String, kind As FaceFrameKind)
            Me.Left = left
            Me.Top = top
            Me.Right = right
            Me.Bottom = bottom
            Me.Label = If(label, "")
            Me.Kind = kind
        End Sub

        Public ReadOnly Property Left As Double
        Public ReadOnly Property Top As Double
        Public ReadOnly Property Right As Double
        Public ReadOnly Property Bottom As Double
        Public ReadOnly Property Label As String
        Public ReadOnly Property Kind As FaceFrameKind
    End Class

    ''' <summary>Bildet den gespeicherten Kasten eines Gesichts auf das gezeigte Bild ab.
    '''
    ''' Der Kasten liegt in den Bildpunkten der Quelle, auf der gesucht wurde
    ''' (<see cref="XmpFaceRegionService.ReadOrientedPixelSize"/>, bei RAW die groesste eingebettete
    ''' Vorschau, aufgerichtet). Das gezeigte Bild ist dieselbe Flaeche, aber womoeglich durch die
    ''' Geometrie des Rezepts gegangen (Zuschnitt, Drehung, Spiegeln, Begradigen) oder nur um eine
    ''' gespeicherte Vierteldrehung gedreht (eingebettete Vorschau einer RAW, PSD). Beides rechnet
    ''' diese Stelle nach, ueber dieselbe Abbildung wie der Editor
    ''' (<see cref="ImageProcessor.TrySourcePointToGeometryOutputUnclipped"/>), keine eigene Naeherung.
    '''
    ''' Eine reine Funktion ohne Anwendungszustand, damit sie ohne Oberflaeche pruefbar ist.</summary>
    Public NotInheritable Class FaceFrameLayout

        Private Sub New()
        End Sub

        ''' <summary>Der Kasten in Anteilen des gezeigten Bildes, auf das Bild beschnitten. Nothing,
        ''' wenn die Bezugsgroesse fehlt oder der Kasten ganz ausserhalb liegt (weggeschnitten).</summary>
        ''' <param name="geometry">Das Rezept, dessen Geometrie das gezeigte Bild durchlaufen hat,
        ''' oder Nothing, wenn es unveraendert gezeigt wird.</param>
        ''' <param name="quarterTurn">Nur ohne <paramref name="geometry"/>: eine Drehung im
        ''' Uhrzeigersinn um ein Vielfaches von 90 Grad, wie ImageOrientationService.ApplyQuarterRotation.</param>
        Public Shared Function MapBox(x As Double, y As Double, width As Double, height As Double,
                                      referenceWidth As Integer, referenceHeight As Integer,
                                      geometry As ImageAdjustments, quarterTurn As Integer) As (Left As Double, Top As Double, Right As Double, Bottom As Double)?
            If referenceWidth <= 0 OrElse referenceHeight <= 0 Then Return Nothing
            If width <= 0 OrElse height <= 0 Then Return Nothing
            Dim corners = {(x, y), (x + width, y), (x, y + height), (x + width, y + height)}
            ' Mit Geometrie ein Raster ueber den Kasten statt nur der Ecken: das Stuetzpunktraster
            ' des Verzerrens bildet nicht gleichmaessig ab, eine Kante oder die Mitte kann weiter
            ' wandern als alle vier Ecken.
            Const WarpSamples As Integer = 4
            Dim minX = Double.MaxValue, minY = Double.MaxValue, maxX = Double.MinValue, maxY = Double.MinValue
            Dim mappedCount = 0

            If geometry IsNot Nothing Then
                Dim outSize = ImageProcessor.ComputeGeometryOutputSize(referenceWidth, referenceHeight, geometry)
                If outSize.Width <= 0 OrElse outSize.Height <= 0 Then Return Nothing
                For iy = 0 To WarpSamples
                    For ix = 0 To WarpSamples
                        Dim p As SKPoint
                        If Not ImageProcessor.TrySourcePointToGeometryOutputUnclipped(
                               x + width * ix / WarpSamples, y + height * iy / WarpSamples,
                               referenceWidth, referenceHeight, geometry, p) Then Continue For
                        Dim rx = p.X / outSize.Width, ry = p.Y / outSize.Height
                        minX = Math.Min(minX, rx) : maxX = Math.Max(maxX, rx)
                        minY = Math.Min(minY, ry) : maxY = Math.Max(maxY, ry)
                        mappedCount += 1
                    Next
                Next
            Else
                Dim turn = ImageOrientationService.NormalizeQuarterTurn(quarterTurn)
                For Each c In corners
                    Dim rx = c.Item1 / referenceWidth, ry = c.Item2 / referenceHeight
                    Dim tx = rx, ty = ry
                    Select Case turn
                        Case 90
                            tx = 1.0 - ry : ty = rx
                        Case 180
                            tx = 1.0 - rx : ty = 1.0 - ry
                        Case 270
                            tx = ry : ty = 1.0 - rx
                    End Select
                    minX = Math.Min(minX, tx) : maxX = Math.Max(maxX, tx)
                    minY = Math.Min(minY, ty) : maxY = Math.Max(maxY, ty)
                    mappedCount += 1
                Next
            End If

            If mappedCount < 2 Then Return Nothing
            ' Auf das Bild beschneiden: ein Gesicht an der Schnittkante zeigt seinen sichtbaren Teil,
            ' eines ganz ausserhalb gar nichts.
            minX = Math.Max(0.0, minX) : minY = Math.Max(0.0, minY)
            maxX = Math.Min(1.0, maxX) : maxY = Math.Min(1.0, maxY)
            If maxX - minX <= 0.0 OrElse maxY - minY <= 0.0 Then Return Nothing
            Return (minX, minY, maxX, maxY)
        End Function

        ''' <summary>Welche Darstellung ein Gesicht bekommt: dieselbe Unterscheidung wie in der
        ''' Leiste (Vorschlag gegen Handarbeit).</summary>
        Public Shared Function KindOf(name As String, isManual As Boolean) As FaceFrameKind
            If String.IsNullOrWhiteSpace(name) Then Return FaceFrameKind.Unnamed
            Return If(isManual, FaceFrameKind.Confirmed, FaceFrameKind.Suggested)
        End Function

    End Class

End Namespace
