Imports System
Imports System.Collections.Generic
Imports System.Runtime.InteropServices
Imports SkiaSharp

Namespace Services

    ''' <summary>
    ''' Den Hintergrund einer Bildebene durchsichtig machen, wenn er eine einheitliche Farbe hat:
    ''' ein Produktfoto auf weissem Grund, ein Scan, eine Grafik auf farbiger Flaeche. Ohne Modell.
    '''
    ''' Drei Schritte:
    ''' 1. Die Hintergrundfarbe kommt vom RAND des Rasters (Median je Kanal ueber einen schmalen
    '''    Streifen). Stimmt weniger als die Haelfte des Randes mit ihr ueberein, gibt es keinen
    '''    einheitlichen Hintergrund, und es geschieht nichts.
    ''' 2. Von aussen her wird zusammenhaengend geflutet, was dieser Farbe innerhalb der Toleranz
    '''    gleicht. Das wird ganz durchsichtig. Eine gleich gefaerbte Stelle INNEN im Motiv (ein
    '''    weisses Hemd vor weissem Grund) bleibt, weil sie vom Rand aus nicht erreichbar ist.
    ''' 3. Im schmalen Saum um das Geflutete wird ENTMISCHT: je Punkt die kleinste Deckung, mit der
    '''    er sich als Mischung aus einer Vordergrundfarbe und dem Hintergrund erklaeren laesst, und
    '''    die Vordergrundfarbe dazu. So bleibt an Kanten kein heller Saum stehen, und eine weiche
    '''    Kante bleibt weich.
    '''
    ''' Nur im Saum, nicht im ganzen Motiv: dort wuerde das Entmischen jede helle Stelle eines
    ''' Motivs auf hellem Grund halb durchsichtig machen.
    ''' </summary>
    Public NotInheritable Class EdgeColorTransparency

        Public Enum Outcome
            Done
            ''' <summary>Der Rand hat keine einheitliche Farbe.</summary>
            NoUniformEdge
            ''' <summary>Der Rand ist schon durchsichtig.</summary>
            NoOpaqueEdge
            ''' <summary>Nichts gleicht der Randfarbe so, dass es zu entfernen waere.</summary>
            NothingRemoved
        End Enum

        ''' <summary>Breite des Randstreifens, aus dem die Hintergrundfarbe kommt, in Punkten.</summary>
        Private Const EdgeSampleDepth As Integer = 2

        ''' <summary>Kleinste Toleranz fuer "gleicht dem Hintergrund", als groesste Abweichung eines
        ''' Kanals (0 bis 255). Ein verrauschter Grund hebt sie an (siehe Estimate).</summary>
        Private Const MinimumTolerance As Integer = 12

        ''' <summary>Breite des Saums, in dem entmischt wird, in Punkten. Kantenglaettung und eine
        ''' leichte Unschaerfe liegen darin; ein weicher Schatten reicht weiter und bleibt deshalb
        ''' zum Teil stehen.</summary>
        Private Const EdgeBandWidth As Integer = 3

        ''' <summary>Deckungen darunter werden 0, darueber 1: ein Rest von 2 Prozent Deckung ist
        ''' Rauschen, kein Inhalt.</summary>
        Private Const AlphaSnap As Double = 0.03

        Private Sub New()
        End Sub

        ''' <summary>Das Ergebnis als neues Raster (Bgra8888, vormultipliziert), oder Nothing, wenn
        ''' <paramref name="status"/> nicht Done ist. <paramref name="background"/> ist die
        ''' gefundene Randfarbe.</summary>
        Public Shared Function Apply(source As SKBitmap, ByRef status As Outcome,
                                     ByRef background As SKColor) As SKBitmap
            status = Outcome.NothingRemoved
            background = SKColors.Transparent
            If source Is Nothing OrElse source.Width <= 0 OrElse source.Height <= 0 Then Return Nothing
            Dim width = source.Width, height = source.Height

            ' Ungemultipliert lesen: das Entmischen rechnet mit der Farbe, nicht mit Farbe mal
            ' Deckung. ReadPixels rechnet Farbtyp und Deckungsart um; eine Leinwand auf einem
            ' ungemultiplizierten Raster kann Skia nicht.
            Dim info = New SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul)
            Dim pixels(width * height * 4 - 1) As Byte
            Dim handle = GCHandle.Alloc(pixels, GCHandleType.Pinned)
            Try
                Using pixmap = source.PeekPixels()
                    If pixmap Is Nothing OrElse
                       Not pixmap.ReadPixels(info, handle.AddrOfPinnedObject(), info.RowBytes) Then Return Nothing
                End Using
            Finally
                handle.Free()
            End Try

            Dim estimate = EstimateBackground(pixels, width, height)
            If Not estimate.HasOpaqueEdge Then
                status = Outcome.NoOpaqueEdge
                Return Nothing
            End If
            If Not estimate.Uniform Then
                status = Outcome.NoUniformEdge
                Return Nothing
            End If
            background = New SKColor(estimate.R, estimate.G, estimate.B)

            ' 2. Vom Rand her fluten. 0 = unberuehrt, 1 = Hintergrund, 2 = im Saum.
            Dim state(width * height - 1) As Byte
            Dim queue As New Queue(Of Integer)()
            For x = 0 To width - 1
                TrySeed(pixels, state, queue, x, 0, width, estimate)
                TrySeed(pixels, state, queue, x, height - 1, width, estimate)
            Next
            For y = 0 To height - 1
                TrySeed(pixels, state, queue, 0, y, width, estimate)
                TrySeed(pixels, state, queue, width - 1, y, width, estimate)
            Next
            Dim removed = 0
            While queue.Count > 0
                Dim i = queue.Dequeue()
                removed += 1
                Dim x = i Mod width, y = i \ width
                If x > 0 Then TrySeed(pixels, state, queue, x - 1, y, width, estimate)
                If x < width - 1 Then TrySeed(pixels, state, queue, x + 1, y, width, estimate)
                If y > 0 Then TrySeed(pixels, state, queue, x, y - 1, width, estimate)
                If y < height - 1 Then TrySeed(pixels, state, queue, x, y + 1, width, estimate)
            End While
            If removed = 0 Then Return Nothing

            ' 3. Der Saum: EdgeBandWidth Ringe um das Geflutete, je Ring die Nachbarn des vorigen.
            Dim ring As New List(Of Integer)()
            For i = 0 To state.Length - 1
                If state(i) = 1 Then ring.Add(i)
            Next
            Dim band As New List(Of Integer)()
            For r = 1 To EdgeBandWidth
                Dim nextRing As New List(Of Integer)()
                For Each i In ring
                    Dim x = i Mod width, y = i \ width
                    For Each n In {If(x > 0, i - 1, -1), If(x < width - 1, i + 1, -1),
                                   If(y > 0, i - width, -1), If(y < height - 1, i + width, -1)}
                        If n < 0 OrElse state(n) <> 0 Then Continue For
                        state(n) = 2
                        nextRing.Add(n)
                        band.Add(n)
                    Next
                Next
                ring = nextRing
                If ring.Count = 0 Then Exit For
            Next

            For i = 0 To state.Length - 1
                If state(i) = 1 Then
                    pixels(i * 4 + 3) = 0
                End If
            Next
            For Each i In band
                Unmix(pixels, i * 4, estimate)
            Next

            Dim result = New SKBitmap(New SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
            Dim back = GCHandle.Alloc(pixels, GCHandleType.Pinned)
            Try
                Using pixmap = New SKPixmap(info, back.AddrOfPinnedObject(), info.RowBytes)
                    If Not pixmap.ReadPixels(result.Info, result.GetPixels(), result.RowBytes) Then
                        result.Dispose()
                        Return Nothing
                    End If
                End Using
            Finally
                back.Free()
            End Try
            status = Outcome.Done
            Return result
        End Function

        Private Structure BackgroundEstimate
            Public R As Byte
            Public G As Byte
            Public B As Byte
            Public Tolerance As Integer
            Public Uniform As Boolean
            Public HasOpaqueEdge As Boolean
        End Structure

        ''' <summary>Median je Kanal ueber den Randstreifen, nur deckende Punkte. Die Toleranz waechst
        ''' mit der Streuung des Randes: das Dreifache des mittleren Abstands vom Median, wenigstens
        ''' MinimumTolerance. So bleibt ein rauschender Scan-Grund nicht als Sprenkel stehen.</summary>
        Private Shared Function EstimateBackground(pixels As Byte(), width As Integer, height As Integer) As BackgroundEstimate
            Dim rs As New List(Of Byte)(), gs As New List(Of Byte)(), bs As New List(Of Byte)()
            Dim depth = Math.Max(1, Math.Min(EdgeSampleDepth, Math.Min(width, height) \ 2))
            For y = 0 To height - 1
                For x = 0 To width - 1
                    If x >= depth AndAlso x < width - depth AndAlso y >= depth AndAlso y < height - depth Then Continue For
                    Dim o = (y * width + x) * 4
                    If pixels(o + 3) < 250 Then Continue For
                    rs.Add(pixels(o))
                    gs.Add(pixels(o + 1))
                    bs.Add(pixels(o + 2))
                Next
            Next
            Dim result As New BackgroundEstimate()
            Dim total = 2 * depth * (width + height) - 4 * depth * depth
            ' Ist der Rand ueberwiegend schon durchsichtig, gibt es nichts zu entfernen.
            If rs.Count < Math.Max(1, total \ 4) Then Return result
            result.HasOpaqueEdge = True
            result.R = Median(rs)
            result.G = Median(gs)
            result.B = Median(bs)
            Dim distances As New List(Of Integer)(rs.Count)
            For i = 0 To rs.Count - 1
                distances.Add(Math.Max(Math.Abs(CInt(rs(i)) - result.R),
                                       Math.Max(Math.Abs(CInt(gs(i)) - result.G), Math.Abs(CInt(bs(i)) - result.B))))
            Next
            distances.Sort()
            Dim meanDistance = 0.0
            ' Der Mittelwert ueber die naeheren drei Viertel: Motivteile, die den Rand beruehren,
            ' sollen die Toleranz nicht aufblasen.
            Dim inner = Math.Max(1, distances.Count * 3 \ 4)
            For i = 0 To inner - 1
                meanDistance += distances(i)
            Next
            meanDistance /= inner
            result.Tolerance = Math.Max(MinimumTolerance, CInt(Math.Ceiling(meanDistance * 3.0)))
            Dim matching = 0
            For Each d In distances
                If d <= result.Tolerance Then matching += 1
            Next
            result.Uniform = matching * 2 >= distances.Count
            Return result
        End Function

        Private Shared Function Median(values As List(Of Byte)) As Byte
            Dim counts(255) As Integer
            For Each v In values
                counts(v) += 1
            Next
            Dim half = values.Count \ 2, seen = 0
            For v = 0 To 255
                seen += counts(v)
                If seen > half Then Return CByte(v)
            Next
            Return 255
        End Function

        Private Shared Sub TrySeed(pixels As Byte(), state As Byte(), queue As Queue(Of Integer),
                                   x As Integer, y As Integer, width As Integer, estimate As BackgroundEstimate)
            Dim i = y * width + x
            If state(i) <> 0 Then Return
            Dim o = i * 4
            ' Schon Durchsichtiges gehoert zum Hintergrund und fuehrt die Flut weiter.
            If pixels(o + 3) <> 0 Then
                Dim d = Math.Max(Math.Abs(CInt(pixels(o)) - estimate.R),
                                 Math.Max(Math.Abs(CInt(pixels(o + 1)) - estimate.G), Math.Abs(CInt(pixels(o + 2)) - estimate.B)))
                If d > estimate.Tolerance Then Return
            End If
            state(i) = 1
            queue.Enqueue(i)
        End Sub

        ''' <summary>Ein Saumpunkt C als Mischung a*F + (1 - a)*B. Je Kanal braucht es mindestens so
        ''' viel Deckung, dass F zwischen 0 und 255 bleibt: liegt C ueber B, ist a mindestens
        ''' (C - B)/(255 - B), liegt es darunter, (B - C)/B. Die groesste dieser Schranken ist die
        ''' kleinste Deckung, die alle drei Kanaele erklaert; F folgt daraus. Die Deckung, die der
        ''' Punkt schon hatte, bleibt als Faktor erhalten.</summary>
        Private Shared Sub Unmix(pixels As Byte(), o As Integer, estimate As BackgroundEstimate)
            Dim original = pixels(o + 3) / 255.0
            If original <= 0 Then Return
            Dim bg = {CDbl(estimate.R), CDbl(estimate.G), CDbl(estimate.B)}
            Dim alpha = 0.0
            For c = 0 To 2
                Dim v = CDbl(pixels(o + c)), b = bg(c)
                Dim needed As Double
                If v > b Then
                    needed = If(b >= 255, 1.0, (v - b) / (255.0 - b))
                ElseIf v < b Then
                    needed = If(b <= 0, 1.0, (b - v) / b)
                Else
                    needed = 0
                End If
                If needed > alpha Then alpha = needed
            Next
            If alpha < AlphaSnap Then
                pixels(o + 3) = 0
                Return
            End If
            If alpha > 1.0 - AlphaSnap Then alpha = 1.0
            For c = 0 To 2
                Dim v = CDbl(pixels(o + c))
                Dim f = (v - (1.0 - alpha) * bg(c)) / alpha
                pixels(o + c) = CByte(Math.Max(0, Math.Min(255, Math.Round(f))))
            Next
            pixels(o + 3) = CByte(Math.Max(0, Math.Min(255, Math.Round(alpha * original * 255.0))))
        End Sub

    End Class

End Namespace
