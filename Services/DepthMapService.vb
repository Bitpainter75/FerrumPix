Imports System
Imports System.Collections.Generic
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Threading.Tasks
Imports System.Threading
Imports Microsoft.ML.OnnxRuntime
Imports Microsoft.ML.OnnxRuntime.Tensors
Imports SkiaSharp

Namespace Services

    ''' <summary>Die Tiefenkarte eines Bildes: fuer jeden Bildpunkt, wie weit er entfernt ist.
    '''
    ''' RELATIVE Tiefe, nicht in Metern. Das Modell weiss nicht, wie gross die Szene ist - es weiss
    ''' nur, was naeher und was weiter ist. Fuer alles, wofuer sie hier gebraucht wird, reicht das
    ''' genau: eine Maske nach Entfernung, und spaeter eine Unschaerfe, die mit der Entfernung
    ''' zunimmt. Wer Meter braucht, braucht ein anderes Modell.
    '''
    ''' Ausgeliefert wird sie als Graustufenbild in Bildgroesse: HELL ist nah, DUNKEL ist fern. Das
    ''' ist die Richtung, die das Modell liefert (es gibt inverse Tiefe aus), und sie wird bewusst
    ''' nicht gedreht - "hell = nah" ist auch die Anschauung, die man beim Anschauen einer solchen
    ''' Karte hat.
    '''
    ''' Die Werte werden auf 0 bis 255 GESPREIZT, jedes Bild fuer sich. Absolute Vergleichbarkeit
    ''' zwischen zwei Fotos gibt es bei relativer Tiefe ohnehin nicht; ohne Spreizung laege ein Bild
    ''' mit geringer Tiefenstaffelung in einem schmalen Band und waere als Maske unbrauchbar.</summary>
    Public NotInheritable Class DepthMapService

        Private Sub New()
        End Sub

        Public Const ModelFile As String = "midas-small"

        ''' <summary>Kantenlaenge, auf die das Modell rechnet. Fest: die kleine Fassung ist auf
        ''' 256 Pixel trainiert.</summary>
        Public Const ModelEdge As Integer = 256

        Public Shared ReadOnly Property Available As Boolean
            Get
                Return AiModelService.RuntimeAvailable AndAlso
                       Not String.IsNullOrEmpty(AiModelService.BestFile(ModelFile))
            End Get
        End Property

        ''' <summary>Die Tiefenkarte als Alpha8-Bild in der Groesse des Quellbildes. Nothing bei
        ''' jedem Fehlschlag - eine halbe Tiefenkarte waere schlimmer als keine.</summary>
        Public Shared Function Compute(image As SKBitmap, Optional cancel As CancellationToken = Nothing) As SKBitmap
            If image Is Nothing OrElse image.Width <= 0 OrElse image.Height <= 0 Then Return Nothing

            ' DER FANGBLOCK UMSCHLIESST ALLES, nicht nur den Modelllauf. "Nothing bei jedem
            ' Fehlschlag" ist die Zusage dieser Funktion, und ein Abbruch ist einer davon - auch
            ' wenn er schon beim Skalieren oder beim Fuellen des Tensors auffaellt. Lag der Anfang
            ' ausserhalb, flog von dort eine OperationCanceledException heraus, der Aufrufer kam
            ' nicht mehr an seinen Aufraeumweg, und das Abbruchkreuz blieb auf einem Vorgang
            ' stehen, den es nicht mehr gab.
            Try
                Using small = ComputeModelMap(image, cancel)
                    If small Is Nothing Then Return Nothing
                    Dim aligned = AlignToImage(image, small, cancel:=cancel)
                    If aligned IsNot Nothing Then Return aligned

                    ' Ersatzweg fuer ein Farbformat, das die Ausrichtung nicht liest: nur hochziehen.
                    Dim large = New SKBitmap(New SKImageInfo(image.Width, image.Height, SKColorType.Alpha8, SKAlphaType.Premul))
                    If Not ImageProcessor.ScalePixelsFast(small, large, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then
                        large.Dispose()
                        Return Nothing
                    End If
                    Return large
                End Using
            Catch ex As OperationCanceledException
                Return Nothing
            Catch ex As Exception
                DiagnosticLogService.LogAlways("Tiefenkarte", ex.Message)
                Return Nothing
            End Try
        End Function

        ''' <summary>Die Karte, wie das Modell sie liefert: Alpha8 mit <see cref="ModelEdge"/> Punkten
        ''' Kantenlaenge, gespreizt auf 0 bis 255, noch nicht auf das Bild ausgerichtet. Wirft bei
        ''' Abbruch und Fehler; <see cref="Compute"/> faengt beides.</summary>
        Friend Shared Function ComputeModelMap(image As SKBitmap, cancel As CancellationToken) As SKBitmap
            cancel.ThrowIfCancellationRequested()
            Dim session = AiModelService.SessionFor(ModelFile)
            If session Is Nothing Then Return Nothing

            ' Das Modell rechnet auf einem festen Quadrat. Das Bild wird darauf VERZERRT, nicht
            ' eingepasst: eine Auffuellung mit Schwarz haette das Netz als sehr weit entfernte
            ' Flaeche gelesen und die Spreizung der echten Tiefen zusammengedrueckt.
            Dim tensor = New DenseTensor(Of Single)(New Integer() {1, 3, ModelEdge, ModelEdge})
            Using small = New SKBitmap(ModelEdge, ModelEdge, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                If Not ImageProcessor.ScalePixelsFast(image, small, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                ' Normierung aus dem Training des Modells - nicht geraten.
                Dim average = New Single() {0.485F, 0.456F, 0.406F}
                Dim spread = New Single() {0.229F, 0.224F, 0.225F}
                Dim target = tensor.Buffer.Span
                Dim layer = ModelEdge * ModelEdge
                For y = 0 To ModelEdge - 1
                    If y Mod 16 = 0 Then cancel.ThrowIfCancellationRequested()
                    For x = 0 To ModelEdge - 1
                        Dim p = small.GetPixel(x, y)
                        Dim i = y * ModelEdge + x
                        target(i) = (p.Red / 255.0F - average(0)) / spread(0)
                        target(layer + i) = (p.Green / 255.0F - average(1)) / spread(1)
                        target(layer * 2 + i) = (p.Blue / 255.0F - average(2)) / spread(2)
                    Next
                Next
            End Using

            cancel.ThrowIfCancellationRequested()
            Dim input = New List(Of NamedOnnxValue) From {
                NamedOnnxValue.CreateFromTensor("image", tensor)}
            Using result = session.Run(input)
                Dim output = TryCast(result.First().Value, DenseTensor(Of Single))
                If output Is Nothing Then Return Nothing
                cancel.ThrowIfCancellationRequested()
                Return AsGrayscale(output.Buffer.ToArray(), cancel)
            End Using
        End Function

        Private Shared Function AsGrayscale(values As Single(), cancel As CancellationToken) As SKBitmap
            If values Is Nothing OrElse values.Length < ModelEdge * ModelEdge Then Return Nothing
            Dim offset = values.Length - ModelEdge * ModelEdge

            Dim min = Single.MaxValue, max = Single.MinValue
            For i = offset To values.Length - 1
                If (i - offset) Mod (ModelEdge * 16) = 0 Then cancel.ThrowIfCancellationRequested()
                Dim v = values(i)
                If Single.IsNaN(v) OrElse Single.IsInfinity(v) Then Continue For
                If v < min Then min = v
                If v > max Then max = v
            Next
            Dim span = max - min
            ' Ein voellig flaches Ergebnis ist keine Tiefenkarte, sondern ein Fehlschlag.
            If span <= 0.0001F Then Return Nothing

            Dim small = New SKBitmap(New SKImageInfo(ModelEdge, ModelEdge, SKColorType.Alpha8, SKAlphaType.Premul))
            Dim buffer(ModelEdge * ModelEdge - 1) As Byte
            For i = 0 To buffer.Length - 1
                Dim v = (values(offset + i) - min) / span
                buffer(i) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(v * 255.0F)))))
            Next
            Runtime.InteropServices.Marshal.Copy(buffer, 0, small.GetPixels(), buffer.Length)
            Return small
        End Function

        ''' <summary>Kantenlaenge, auf der <see cref="AlignToImage"/> seine Koeffizienten rechnet.
        ''' Das Vierfache des Modells: fein genug, dass die Kanten des Fotos darin noch scharf
        ''' liegen, und klein genug fuer gut 13 Kastenfilter in einem Bruchteil einer Sekunde.</summary>
        Private Const AlignEdge As Integer = 1024

        ''' <summary>Durchgaenge des gefuehrten Filters in <see cref="AlignToImage"/>. Einer setzt
        ''' die Kante auf die Farbkante, laesst aber auf einer Strecke vom Filterradius eine Schulter
        ''' der weichen Modellkarte stehen (an einer Stufe 0 auf 255 bis zu 67 direkt vor der
        ''' Kante); der zweite nimmt das Ergebnis des ersten als Karte und baut sie weitgehend ab.
        ''' Die Statistik der Farbe wird dafuer nicht neu gerechnet.</summary>
        Private Const AlignPasses As Integer = 2

        ''' <summary>Zieht die Tiefenkarte des Modells (<paramref name="small"/>, 256 x 256) auf die
        ''' Groesse von <paramref name="image"/> und legt dabei ihre Kanten auf die Kanten des Fotos.
        '''
        ''' Das Modell sieht das Bild nur mit 256 Punkten Kantenlaenge, und seine Karte ist auch
        ''' dort an Umrissen weich. Bilinear hochgezogen wurde daraus bei 20 MP ein Uebergang von
        ''' mehreren Dutzend Bildpunkten: der Himmel neben einem Kopf bekam die Naehe des Kopfes,
        ''' und Nebel nach Tiefe liess dort einen klaren Saum stehen.
        '''
        ''' Verfahren: gefuehrter Filter mit dem Farbbild als Fuehrung (He, Sun, Tang 2013), in der
        ''' schnellen Form (He, Sun 2015). Je Umgebung wird die Tiefe als lineare Funktion der
        ''' Farbe angesetzt, q = a . I + b, mit der kleinsten Abweichung von der Karte des Modells.
        ''' Wo die Farbe innerhalb der Umgebung springt, springt damit auch die Tiefe, und zwar
        ''' genau an der Stelle des Fotos; wo die Farbe gleichmaessig ist, bleibt die Karte, wie sie
        ''' war. a und b werden auf hoechstens <see cref="AlignEdge"/> Punkten gerechnet, gemittelt,
        ''' bilinear hochgezogen und dann mit der Farbe jedes einzelnen Bildpunkts angewendet: die
        ''' Kante kommt so in voller Aufloesung aus dem Foto, nicht aus der Rechenstufe.
        '''
        ''' <paramref name="radiusModelPixels"/> ist der Radius in Punkten des Modells, damit er
        ''' unabhaengig von der Bildgroesse dieselbe Unschaerfe der Modellkarte ueberbrueckt.
        ''' <paramref name="epsilon"/> ist die Varianz der Farbe (Kanaele 0 bis 1), unter der eine
        ''' Farbschwankung nicht mehr als Kante gilt.</summary>
        ''' <returns>Alpha8 in Bildgroesse, Nothing bei einem Farbformat ausser BGRA und RGBA.</returns>
        Friend Shared Function AlignToImage(image As SKBitmap, small As SKBitmap,
                                            Optional radiusModelPixels As Double = 3.0,
                                            Optional epsilon As Double = 0.001,
                                            Optional cancel As CancellationToken = Nothing) As SKBitmap
            If image Is Nothing OrElse small Is Nothing Then Return Nothing
            Dim ri, gi, bi As Integer
            Select Case image.ColorType
                Case SKColorType.Bgra8888 : ri = 2 : gi = 1 : bi = 0
                Case SKColorType.Rgba8888 : ri = 0 : gi = 1 : bi = 2
                Case Else : Return Nothing
            End Select
            Dim w = image.Width, h = image.Height
            If w <= 0 OrElse h <= 0 Then Return Nothing
            Dim scale = Math.Min(1.0, AlignEdge / CDbl(Math.Max(w, h)))
            Dim lw = Math.Max(1, CInt(Math.Round(w * scale)))
            Dim lh = Math.Max(1, CInt(Math.Round(h * scale)))
            Dim n = lw * lh
            Dim radius = Math.Max(1, CInt(Math.Round(radiusModelPixels * Math.Max(lw, lh) / ModelEdge)))

            ' Fuehrung und Karte in der Rechenstufe, Werte 0 bis 1.
            Dim gR(n - 1), gG(n - 1), gB(n - 1), p(n - 1) As Single
            Using guide = New SKBitmap(lw, lh, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                If Not ImageProcessor.ScalePixelsFast(image, guide, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                Dim stride = guide.RowBytes
                Dim bytes(stride * lh - 1) As Byte
                Marshal.Copy(guide.GetPixels(), bytes, 0, bytes.Length)
                For y = 0 To lh - 1
                    For x = 0 To lw - 1
                        Dim o = y * stride + x * 4
                        Dim i = y * lw + x
                        gB(i) = bytes(o) / 255.0F
                        gG(i) = bytes(o + 1) / 255.0F
                        gR(i) = bytes(o + 2) / 255.0F
                    Next
                Next
            End Using
            Using lowDepth = New SKBitmap(New SKImageInfo(lw, lh, SKColorType.Alpha8, SKAlphaType.Premul))
                If Not ImageProcessor.ScalePixelsFast(small, lowDepth, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                Dim depthStride As Integer
                Dim depthBytes = ImageProcessor.TryReadAlpha8(lowDepth, depthStride)
                If depthBytes Is Nothing Then Return Nothing
                For y = 0 To lh - 1
                    For x = 0 To lw - 1
                        p(y * lw + x) = depthBytes(y * depthStride + x) / 255.0F
                    Next
                Next
            End Using
            cancel.ThrowIfCancellationRequested()

            Dim Product = Function(u As Single(), v As Single()) As Single()
                              Dim r(n - 1) As Single
                              For i = 0 To n - 1
                                  r(i) = u(i) * v(i)
                              Next
                              Return r
                          End Function
            Dim Mean = Function(u As Single()) BoxMean(u, lw, lh, radius)

            Dim eps = CSng(Math.Max(0.000001, epsilon))
            Dim modelScale = Math.Max(lw, lh) / CDbl(ModelEdge)

            Dim maR As Single() = Nothing, maG As Single() = Nothing, maB As Single() = Nothing
            Dim mOffset As Single() = Nothing
            For pass = 1 To AlignPasses
                If pass > 1 Then
                    ' Karte dieses Durchgangs ist das Ergebnis des vorigen auf der Rechenstufe.
                    Dim nextP(n - 1) As Single
                    For i = 0 To n - 1
                        nextP(i) = Math.Max(0.0F, Math.Min(1.0F, maR(i) * gR(i) + maG(i) * gG(i) + maB(i) * gB(i) + mOffset(i)))
                    Next
                    p = nextP
                End If

                ' GEWICHTET: jeder Punkt zaehlt mit dem Vertrauen in seine Tiefe. Ungewichtet
                ' lernte die Anpassung auch aus der weichen Rampe, die das Modell an jedem Umriss
                ' hat, und liess sie vor der Farbkante als Schulter stehen (an einer Stufe 0 auf 255
                ' bis 67 direkt vor der Kante). Mit wenig Gewicht auf der Rampe kommt der Zusammenhang
                ' von Farbe und Tiefe aus den flachen Stuecken beiderseits, und die Stufe faellt an
                ' die Farbkante.
                Dim confidence = RampConfidence(p, lw, lh, modelScale)
                Dim confidenceMean = Mean(confidence)
                Dim WeightedMean = Function(u As Single()) As Single()
                                       Dim m = Mean(Product(confidence, u))
                                       For i = 0 To n - 1
                                           m(i) /= confidenceMean(i)
                                       Next
                                       Return m
                                   End Function
                Dim mR = WeightedMean(gR), mG = WeightedMean(gG), mB = WeightedMean(gB)
                Dim mRR = WeightedMean(Product(gR, gR)), mRG = WeightedMean(Product(gR, gG)), mRB = WeightedMean(Product(gR, gB))
                Dim mGG = WeightedMean(Product(gG, gG)), mGB = WeightedMean(Product(gG, gB)), mBB = WeightedMean(Product(gB, gB))
                cancel.ThrowIfCancellationRequested()
                Dim mP = WeightedMean(p)
                Dim mRP = WeightedMean(Product(gR, p)), mGP = WeightedMean(Product(gG, p)), mBP = WeightedMean(Product(gB, p))
                cancel.ThrowIfCancellationRequested()

                ' Je Punkt das lineare Modell: (Kovarianz der Farbe + epsilon) a = Kovarianz Farbe/Karte.
                Dim aR(n - 1), aG(n - 1), aB(n - 1), b(n - 1) As Single
                Parallel.For(0, lh, Sub(y)
                                        For x = 0 To lw - 1
                                            Dim i = y * lw + x
                                            Dim cR = mRP(i) - mR(i) * mP(i)
                                            Dim cG = mGP(i) - mG(i) * mP(i)
                                            Dim cB = mBP(i) - mB(i) * mP(i)
                                            Dim s11 = mRR(i) - mR(i) * mR(i) + eps
                                            Dim s12 = mRG(i) - mR(i) * mG(i)
                                            Dim s13 = mRB(i) - mR(i) * mB(i)
                                            Dim s22 = mGG(i) - mG(i) * mG(i) + eps
                                            Dim s23 = mGB(i) - mG(i) * mB(i)
                                            Dim s33 = mBB(i) - mB(i) * mB(i) + eps
                                            ' Inverse der symmetrischen 3x3-Matrix ueber die Adjunkte.
                                            Dim i11 = s22 * s33 - s23 * s23
                                            Dim i12 = s13 * s23 - s12 * s33
                                            Dim i13 = s12 * s23 - s13 * s22
                                            Dim i22 = s11 * s33 - s13 * s13
                                            Dim i23 = s12 * s13 - s11 * s23
                                            Dim i33 = s11 * s22 - s12 * s12
                                            Dim det = s11 * i11 + s12 * i12 + s13 * i13
                                            If Math.Abs(det) < 1.0E-20F Then
                                                aR(i) = 0 : aG(i) = 0 : aB(i) = 0
                                                b(i) = mP(i)
                                                Continue For
                                            End If
                                            Dim kR = (i11 * cR + i12 * cG + i13 * cB) / det
                                            Dim kG = (i12 * cR + i22 * cG + i23 * cB) / det
                                            Dim kB = (i13 * cR + i23 * cG + i33 * cB) / det
                                            aR(i) = kR : aG(i) = kG : aB(i) = kB
                                            b(i) = mP(i) - kR * mR(i) - kG * mG(i) - kB * mB(i)
                                        Next
                                    End Sub)
                cancel.ThrowIfCancellationRequested()
                maR = Mean(aR) : maG = Mean(aG) : maB = Mean(aB) : mOffset = Mean(b)
                cancel.ThrowIfCancellationRequested()
            Next

            ' Anwenden in voller Aufloesung: Koeffizienten bilinear aus der Rechenstufe, Farbe aus
            ' dem Bildpunkt selbst. Spaltenlage und Gewichte einmal je Spalte.
            Dim x0(w - 1), x1(w - 1) As Integer
            Dim xf(w - 1) As Single
            For x = 0 To w - 1
                Dim fx = Math.Max(0.0, Math.Min(lw - 1.0, (x + 0.5) * lw / w - 0.5))
                x0(x) = CInt(Math.Floor(fx))
                x1(x) = Math.Min(lw - 1, x0(x) + 1)
                xf(x) = CSng(fx - x0(x))
            Next
            Dim premul = image.AlphaType = SKAlphaType.Premul
            Dim srcStride = image.RowBytes
            Dim source = image.GetPixels()
            Dim result = New SKBitmap(New SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul))
            Dim outStride = result.RowBytes
            Dim target = result.GetPixels()
            Parallel.For(0, h, Sub(y, state)
                                   If cancel.IsCancellationRequested Then
                                       state.Stop()
                                       Return
                                   End If
                                   Dim fy = Math.Max(0.0, Math.Min(lh - 1.0, (y + 0.5) * lh / h - 0.5))
                                   Dim r0 = CInt(Math.Floor(fy)) * lw
                                   Dim r1 = Math.Min(lh - 1, CInt(Math.Floor(fy)) + 1) * lw
                                   Dim wy = CSng(fy - Math.Floor(fy))
                                   Dim row(srcStride - 1) As Byte
                                   Marshal.Copy(IntPtr.Add(source, y * srcStride), row, 0, srcStride)
                                   Dim line(w - 1) As Byte
                                   For x = 0 To w - 1
                                       Dim i00 = r0 + x0(x), i01 = r0 + x1(x), i10 = r1 + x0(x), i11 = r1 + x1(x)
                                       Dim wx = xf(x)
                                       Dim o = x * 4
                                       Dim alpha = row(o + 3)
                                       Dim unit = 1.0F / 255.0F
                                       If premul AndAlso alpha > 0 AndAlso alpha < 255 Then unit = 1.0F / alpha
                                       Dim q = Bilinear(maR, i00, i01, i10, i11, wx, wy) * row(o + ri) * unit +
                                               Bilinear(maG, i00, i01, i10, i11, wx, wy) * row(o + gi) * unit +
                                               Bilinear(maB, i00, i01, i10, i11, wx, wy) * row(o + bi) * unit +
                                               Bilinear(mOffset, i00, i01, i10, i11, wx, wy)
                                       line(x) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(q * 255.0F)))))
                                   Next
                                   Marshal.Copy(line, 0, IntPtr.Add(target, y * outStride), w)
                               End Sub)
            If cancel.IsCancellationRequested Then
                result.Dispose()
                cancel.ThrowIfCancellationRequested()
            End If
            StretchToFullRange(result)
            Return result
        End Function

        ''' <summary>Spreizt eine Alpha8-Karte wieder auf 0 bis 255. Die Karte des Modells ist
        ''' gespreizt (siehe Klassenkommentar), der gefuehrte Filter glaettet aber ihre Spitzen: der
        ''' naechste Punkt stand danach etwa bei 224 statt 255, und Grenzen wie "100 = am naechsten"
        ''' der Maske nach Tiefe trafen ihn nicht mehr.</summary>
        Private Shared Sub StretchToFullRange(map As SKBitmap)
            Dim stride As Integer
            Dim bytes = ImageProcessor.TryReadAlpha8(map, stride)
            If bytes Is Nothing Then Return
            Dim w = map.Width, h = map.Height
            Dim lo = 255, hi = 0
            For y = 0 To h - 1
                Dim row = y * stride
                For x = 0 To w - 1
                    Dim v = bytes(row + x)
                    If v < lo Then lo = v
                    If v > hi Then hi = v
                Next
            Next
            If hi <= lo OrElse (lo = 0 AndAlso hi = 255) Then Return
            Dim table(255) As Byte
            For v = 0 To 255
                table(v) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round((v - lo) * 255.0 / (hi - lo))))))
            Next
            For i = 0 To bytes.Length - 1
                bytes(i) = table(bytes(i))
            Next
            Marshal.Copy(bytes, 0, map.GetPixels(), Math.Min(bytes.Length, stride * h))
        End Sub

        ''' <summary>Steigung der Karte, ab der ihr Wert als unsicher gilt, in Anteilen des ganzen
        ''' Bereichs je Punkt des Modells. Ein Umriss laeuft im Modell ueber rund vier Punkte von 0 auf
        ''' 1 (Steigung 0,25) und bekommt damit ein Gewicht um 0,04; ein Boden, der gleichmaessig nach
        ''' hinten laeuft, steigt eher um 0,01 bis 0,02 und behaelt fast das volle Gewicht.</summary>
        Private Const RampSlope As Double = 0.05

        ''' <summary>Vertrauen in die Tiefe jedes Punkts, 1 auf flacher Karte, gegen 0 auf einer
        ''' steilen Rampe: 1 / (1 + (Steigung / <see cref="RampSlope"/>)^2), nach unten auf 0,001
        ''' begrenzt, damit ein Fenster ganz auf einer Rampe noch einen Mittelwert hat.</summary>
        Private Shared Function RampConfidence(p As Single(), width As Integer, height As Integer, modelScale As Double) As Single()
            Dim result(p.Length - 1) As Single
            Dim scale = CSng(modelScale / RampSlope)
            Parallel.For(0, height, Sub(y)
                                        Dim up = Math.Max(0, y - 1) * width, down = Math.Min(height - 1, y + 1) * width
                                        Dim rowSpan = If(y = 0 OrElse y = height - 1, 1.0F, 2.0F)
                                        For x = 0 To width - 1
                                            Dim left = Math.Max(0, x - 1), right = Math.Min(width - 1, x + 1)
                                            Dim gx = (p(y * width + right) - p(y * width + left)) / CSng(Math.Max(1, right - left))
                                            Dim gy = (p(down + x) - p(up + x)) / rowSpan
                                            Dim s = (gx * gx + gy * gy) * scale * scale
                                            result(y * width + x) = Math.Max(0.001F, 1.0F / (1.0F + s))
                                        Next
                                    End Sub)
            Return result
        End Function

        Private Shared Function Bilinear(c As Single(), i00 As Integer, i01 As Integer, i10 As Integer, i11 As Integer,
                                         wx As Single, wy As Single) As Single
            Dim top = c(i00) + (c(i01) - c(i00)) * wx
            Dim bottom = c(i10) + (c(i11) - c(i10)) * wx
            Return top + (bottom - top) * wy
        End Function

        ''' <summary>Mittelwert ueber das Quadrat mit Radius <paramref name="radius"/> um jeden Punkt,
        ''' getrennt nach Zeilen und Spalten mit laufender Summe. Am Rand zaehlt nur, was im Bild
        ''' liegt, geteilt wird durch genau diese Anzahl.</summary>
        Private Shared Function BoxMean(values As Single(), width As Integer, height As Integer, radius As Integer) As Single()
            Dim rows(values.Length - 1) As Single
            Parallel.For(0, height, Sub(y)
                                        Dim start = y * width
                                        Dim sum = 0.0
                                        For x = 0 To Math.Min(radius, width - 1)
                                            sum += values(start + x)
                                        Next
                                        For x = 0 To width - 1
                                            Dim lo = Math.Max(0, x - radius), hi = Math.Min(width - 1, x + radius)
                                            rows(start + x) = CSng(sum / (hi - lo + 1))
                                            If x + radius + 1 < width Then sum += values(start + x + radius + 1)
                                            If x - radius >= 0 Then sum -= values(start + x - radius)
                                        Next
                                    End Sub)
            Dim result(values.Length - 1) As Single
            Parallel.For(0, width, Sub(x)
                                       Dim sum = 0.0
                                       For y = 0 To Math.Min(radius, height - 1)
                                           sum += rows(y * width + x)
                                       Next
                                       For y = 0 To height - 1
                                           Dim lo = Math.Max(0, y - radius), hi = Math.Min(height - 1, y + radius)
                                           result(y * width + x) = CSng(sum / (hi - lo + 1))
                                           If y + radius + 1 < height Then sum += rows((y + radius + 1) * width + x)
                                           If y - radius >= 0 Then sum -= rows((y - radius) * width + x)
                                       Next
                                   End Sub)
            Return result
        End Function

        ''' <summary>Aus einer Tiefenkarte eine Maske: alles, dessen Tiefe zwischen den beiden
        ''' Grenzen liegt. Beide in Prozent, 0 = am weitesten entfernt, 100 = am naechsten.
        '''
        ''' <paramref name="featherPct"/> laesst die Maske an beiden Grenzen auslaufen, statt hart
        ''' abzuschneiden. Ohne das entstuende an jeder Tiefenstufe eine sichtbare Kante quer durchs
        ''' Bild - eine Tiefenkarte ist stetig, eine harte Schwelle darin ist immer zu sehen.</summary>
        Public Shared Function MaskFromDepth(depth As SKBitmap, fromPct As Double, toPct As Double,
                                             featherPct As Double) As SKBitmap
            If depth Is Nothing OrElse depth.Width <= 0 OrElse depth.Height <= 0 Then Return Nothing
            Dim from = Math.Max(0.0, Math.Min(100.0, Math.Min(fromPct, toPct)))
            Dim bis = Math.Max(0.0, Math.Min(100.0, Math.Max(fromPct, toPct)))
            Dim soft = Math.Max(0.0, Math.Min(50.0, featherPct))

            Dim w = depth.Width, h = depth.Height
            Dim output = New SKBitmap(New SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul))
            Dim buffer(w * h - 1) As Byte
            ' DER WERT HAENGT NUR AM TIEFENBYTE: also die 256 moeglichen Ergebnisse einmal rechnen,
            ' mit genau derselben Formel, und je Punkt nachschlagen. Vorher lief je Punkt GetPixel -
            ' ein Aufruf in die native Bibliothek - und die Rechnung dazu, bei jeder Reglerbewegung.
            Dim table(255) As Byte
            For value = 0 To 255
                Dim t = value / 255.0 * 100.0
                Dim d As Double
                If t < from Then
                    d = If(soft <= 0, 0.0, 1.0 - Math.Min(1.0, (from - t) / soft))
                ElseIf t > bis Then
                    d = If(soft <= 0, 0.0, 1.0 - Math.Min(1.0, (t - bis) / soft))
                Else
                    d = 1.0
                End If
                table(value) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(d * 255.0)))))
            Next
            Dim stride As Integer
            Dim depthBytes = ImageProcessor.TryReadAlpha8(depth, stride)
            For y = 0 To h - 1
                For x = 0 To w - 1
                    Dim value = If(depthBytes IsNot Nothing, depthBytes(y * stride + x), depth.GetPixel(x, y).Alpha)
                    buffer(y * w + x) = table(value)
                Next
            Next
            Runtime.InteropServices.Marshal.Copy(buffer, 0, output.GetPixels(), buffer.Length)
            Return output
        End Function


        ''' <summary>Die Form des Verwischungskerns, zeilenweise als SPANNE: fuer jede Zeile des
        ''' Kerns, von welcher bis zu welcher Spalte sie reicht.
        '''
        ''' Die Spanne ist der ganze Trick. Eine Scheibe ist konvex, also hat jede ihrer Zeilen genau
        ''' einen zusammenhaengenden Abschnitt - und ueber Praefixsummen kostet so ein Abschnitt zwei
        ''' Zugriffe statt seiner Laenge. Damit kostet ein Bildpunkt 2r+1 Rechenschritte statt
        ''' (2r+1) mal (2r+1). Bei Radius 12 ist das der Unterschied zwischen 25 und 625, und genau
        ''' daran ist die erste Fassung gescheitert: als allgemeine Faltung gerechnet brauchte eine
        ''' Vorschau ueber eine Minute.
        '''
        ''' <paramref name="corners"/> 0 heisst rund, sonst die Anzahl der Blendenlamellen.</summary>
        Private Shared Sub SpannenFuer(radius As Integer, corners As Integer, rotation As Double,
                                       ByRef xFrom As Integer(), ByRef xTo As Integer(),
                                       ByRef count As Long)
            Dim n = radius * 2 + 1
            ReDim xFrom(n - 1)
            ReDim xTo(n - 1)
            count = 0
            Dim eckzahl = If(corners >= 3, corners, 0)
            Dim angleOffset = rotation * Math.PI / 180.0
            For row = 0 To n - 1
                Dim dy = row - radius
                Dim left = Integer.MaxValue, right = Integer.MinValue
                For column = 0 To n - 1
                    Dim dx = column - radius
                    Dim r = Math.Sqrt(dx * dx + dy * dy)
                    Dim inside As Boolean
                    If eckzahl = 0 Then
                        inside = r <= radius + 0.5
                    Else
                        ' Ein regelmaessiges Vieleck: der zulaessige Radius haengt vom Winkel ab.
                        ' Zwischen zwei Ecken liegt die Kante naeher an der Mitte als die Ecke.
                        Dim w = Math.Atan2(dy, dx) - angleOffset
                        Dim part = 2.0 * Math.PI / eckzahl
                        Dim remainder = w - part * Math.Floor(w / part) - part / 2.0
                        Dim limit = radius * Math.Cos(Math.PI / eckzahl) / Math.Max(0.2, Math.Cos(remainder))
                        inside = r <= limit
                    End If
                    If inside Then
                        If dx < left Then left = dx
                        If dx > right Then right = dx
                    End If
                Next
                If right < left Then
                    ' Leere Zeile: als Spanne der Laenge null vermerken.
                    xFrom(row) = 0
                    xTo(row) = -1
                Else
                    xFrom(row) = left
                    xTo(row) = right
                    count += right - left + 1
                End If
            Next
        End Sub

        ''' <summary>Groesster Radius, mit dem direkt gerechnet wird. Darueber wird die Stufe auf
        ''' einer verkleinerten Kopie gerechnet und wieder hochgezogen - das haelt die Kosten je
        ''' Stufe unabhaengig davon, wie stark verwischt wird.</summary>
        Private Const MaxKernelRadius As Integer = 12

        ''' <summary>Zeichnet <paramref name="source"/> mit einer Scheibe verwischt.
        '''
        ''' Die LICHTER werden vor dem Verwischen angehoben und danach zurueckgenommen. Ohne das
        ''' mittelt sich ihre Helligkeit weg, und aus einem Lichtpunkt wird ein matter Fleck statt
        ''' einer leuchtenden Scheibe. Das ist der Schritt, der "unscharf" von "Bokeh" trennt - mehr
        ''' noch als die Form des Kerns.
        '''
        ''' Am Bildrand wird der Randwert fortgesetzt. Die Alternative waere, dort weniger Punkte zu
        ''' mitteln; dann wuerde der Rand heller oder dunkler als seine Umgebung.</summary>
        Private Shared Function BlurWithDisc(source As SKBitmap, radius As Double,
                                                    corners As Integer, highlightsPct As Double) As SKBitmap
            Return BlurWithDisc(source, radius, corners, highlightsPct, Nothing)
        End Function

        ''' <summary>Wie die öffentliche Berechnung: auch der teure Scheiben-Blur steigt an
        ''' Zeilengrenzen aus. Ein Abbruch darf nie eine halbe Fassung ins Arbeitsbild schreiben.</summary>
        Private Shared Function BlurWithDisc(source As SKBitmap, radius As Double,
                                              corners As Integer, highlightsPct As Double,
                                              cancel As CancellationToken,
                                              Optional sourceImage As SKImage = Nothing) As SKBitmap
            If source Is Nothing OrElse radius < 0.5 Then Return Nothing
            cancel.ThrowIfCancellationRequested()
            Dim scale = 1.0
            Dim r = CInt(Math.Round(radius))
            If r > MaxKernelRadius Then
                scale = MaxKernelRadius / radius
                r = MaxKernelRadius
            End If
            If r < 1 Then Return Nothing

            Dim xFrom As Integer() = Nothing, xTo As Integer() = Nothing
            Dim count As Long = 0
            SpannenFuer(r, corners, 12.0, xFrom, xTo, count)
            If count <= 0 Then Return Nothing

            Dim aw = Math.Max(1, CInt(Math.Round(source.Width * scale)))
            Dim ah = Math.Max(1, CInt(Math.Round(source.Height * scale)))
            If aw <= r * 2 OrElse ah <= r * 2 Then Return Nothing

            Dim highlights = Math.Max(0.0, Math.Min(100.0, highlightsPct)) / 100.0
            ' Hin- und Rueckkennlinie der Lichteranhebung, als Tabelle statt als Potenz je Bildpunkt.
            ' Gemittelt wird in einem GESPREIZTEN Raum: v hoch p mit p groesser eins zieht die
            ' hellen Werte auseinander, sodass sie den Mittelwert bestimmen statt in ihm unterzugehen.
            ' Die Wurzel danach bringt die Helligkeit wieder zurecht. Andersherum (p kleiner eins)
            ' waere es das Gegenteil und zoege alles ins Dunkle.
            Dim high(255) As Single
            Dim exponentHigh = 1.0 + highlights * 3.0
            For i = 0 To 255
                high(i) = CSng(Math.Pow(i / 255.0, exponentHigh))
            Next
            Dim exponentDown = 1.0 / exponentHigh
            Dim backThresholds = BackFromLightThresholds(exponentDown)

            Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
            Using small = New SKBitmap(aw, ah, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                Dim sampling = If(scale < 1.0,
                                   New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                                   New SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None))
                ' Aus dem behaltenen Bild, wenn wirklich skaliert wird (gleich gross kopiert
                ' ScalePixels nur) und nicht beide Seiten unvormultipliziert sind (dafuer hat
                ' ScalePixels einen eigenen Weg).
                Dim scaledFromImage = sourceImage IsNot Nothing AndAlso
                                      (aw <> source.Width OrElse ah <> source.Height) AndAlso
                                      source.AlphaType <> SKAlphaType.Unpremul AndAlso
                                      ImageProcessor.ScaleIntoFromImage(sourceImage, small, sampling)
                If Not scaledFromImage AndAlso Not source.ScalePixels(small, sampling) Then
                    result.Dispose()
                    Return Nothing
                End If

                ' Praefixsummen JE ZEILE und je Kanal, auf den angehobenen Werten. Eine Zeile hat
                ' aw+1 Eintraege, damit die Spanne a bis b als P(b+1) minus P(a) herauskommt.
                Dim width1 = aw + 1
                Dim sums As Single()() = New Single(2)() {}
                For k = 0 To 2
                    sums(k) = New Single(ah * width1 - 1) {}
                Next
                ' EINMAL AUSLESEN statt je Bildpunkt: SKBitmap.GetPixel ist ein Aufruf in die
                ' native Bibliothek und lief hier fuer jeden Punkt der verkleinerten Fassung. Die
                ' Schwesterschleife darunter hatte dasselbe Problem beim Schreiben, dort stand
                ' SetPixel gemessen fuer 72 Prozent der Rechenzeit.
                Dim sStride = small.RowBytes
                Dim sBuf(sStride * ah - 1) As Byte
                Marshal.Copy(small.GetPixels(), sBuf, 0, sBuf.Length)

                ' ZEILENWEISE PARALLEL: die Praefixsumme laeuft innerhalb EINER Zeile, die Zeilen
                ' haengen nicht voneinander ab. Der Abbruch geht ueber ParallelOptions und nicht
                ' ueber ThrowIfCancellationRequested im Rumpf - so kommt er als eine
                ' OperationCanceledException heraus und nicht als gebuendelte Ausnahme.
                Dim opts = New ParallelOptions With {.CancellationToken = cancel}
                Parallel.For(0, ah, opts,
                    Sub(y As Integer)
                        Dim row = y * width1
                        Dim sRow = y * sStride
                        Dim pass0 As Single = 0, pass1 As Single = 0, pass2 As Single = 0
                        For x = 0 To aw - 1
                            Dim o = sRow + x * 4
                            ' Bgra8888: Blau, Gruen, Rot, Alpha.
                            pass0 += high(sBuf(o + 2)) : pass1 += high(sBuf(o + 1)) : pass2 += high(sBuf(o))
                            sums(0)(row + x + 1) = pass0
                            sums(1)(row + x + 1) = pass1
                            sums(2)(row + x + 1) = pass2
                        Next
                    End Sub)

                Using blurred = New SKBitmap(aw, ah, SKColorType.Bgra8888, SKAlphaType.Unpremul)
                    ' ERST IN EINEN ROHPUFFER, danach EINMAL in die Bitmap. Der PixelWriter
                    ' schrieb schon direkt in den Speicher, aber je Punkt einen Aufruf; ein eigener
                    ' Puffer macht die Schleife ausserdem teilbar, weil jede Zeile nur ihren
                    ' Abschnitt anfasst.
                    Dim bStride = blurred.RowBytes
                    Dim bBuf(bStride * ah - 1) As Byte
                    Dim divisor = CSng(1.0 / count)
                    ' ZEILENWEISE PARALLEL. Das ist die teuerste Schleife der ganzen Stufe: je
                    ' Bildpunkt laeuft sie ueber alle Zeilen der Blende. Gemessen kostete die
                    ' Tiefenunschaerfe damit rund eine Sekunde in Vorschaugroesse und fuenf beim
                    ' Export.
                    Parallel.For(0, ah, opts,
                        Sub(y As Integer)
                        Dim bRow = y * bStride
                        For x = 0 To aw - 1
                            Dim s0 As Single = 0, s1 As Single = 0, s2 As Single = 0
                            For row = 0 To r * 2
                                If xTo(row) < xFrom(row) Then Continue For
                                Dim qy = y + row - r
                                If qy < 0 Then qy = 0
                                If qy > ah - 1 Then qy = ah - 1
                                Dim basis = qy * width1
                                Dim a = x + xFrom(row)
                                Dim b = x + xTo(row)
                                ' LIEGT DIE GANZE SPANNE NEBEN DEM BILD, zaehlt jeder ihrer Punkte als
                                ' Randwert. Das kommt nur bei einer ECKIGEN Blende vor: sie ist gedreht,
                                ' und eine ihrer Zeilen kann ganz links oder ganz rechts der Mitte
                                ' liegen. Der Weg darunter klemmte dann beide Enden auf dieselbe Seite
                                ' und griff mit der Praefixsumme vor den Zeilenanfang bzw. hinter das
                                ' Zeilenende - in der ersten und letzten Bildzeile ein Absturz, sonst
                                ' still falsche Randpunkte (Befund, sechs Lamellen). Endet die Spanne
                                ' genau einen Punkt vor dem Rand, rechnete er schon richtig; dieser
                                ' Zweig ergibt dort dieselbe Zahl.
                                If b < 0 Then
                                    Dim n = b - a + 1
                                    s0 += (sums(0)(basis + 1) - sums(0)(basis)) * n
                                    s1 += (sums(1)(basis + 1) - sums(1)(basis)) * n
                                    s2 += (sums(2)(basis + 1) - sums(2)(basis)) * n
                                    Continue For
                                End If
                                If a > aw - 1 Then
                                    Dim n = b - a + 1
                                    s0 += (sums(0)(basis + aw) - sums(0)(basis + aw - 1)) * n
                                    s1 += (sums(1)(basis + aw) - sums(1)(basis + aw - 1)) * n
                                    s2 += (sums(2)(basis + aw) - sums(2)(basis + aw - 1)) * n
                                    Continue For
                                End If
                                ' Ausserhalb der Zeile wird der Randwert fortgesetzt: der fehlende
                                ' Teil der Spanne zaehlt so oft, wie er ueber den Rand hinausragt.
                                Dim leftExtra = 0, rightExtra = 0
                                If a < 0 Then
                                    leftExtra = -a
                                    a = 0
                                End If
                                If b > aw - 1 Then
                                    rightExtra = b - (aw - 1)
                                    b = aw - 1
                                End If
                                s0 += sums(0)(basis + b + 1) - sums(0)(basis + a)
                                s1 += sums(1)(basis + b + 1) - sums(1)(basis + a)
                                s2 += sums(2)(basis + b + 1) - sums(2)(basis + a)
                                If leftExtra > 0 Then
                                    s0 += (sums(0)(basis + 1) - sums(0)(basis)) * leftExtra
                                    s1 += (sums(1)(basis + 1) - sums(1)(basis)) * leftExtra
                                    s2 += (sums(2)(basis + 1) - sums(2)(basis)) * leftExtra
                                End If
                                If rightExtra > 0 Then
                                    s0 += (sums(0)(basis + aw) - sums(0)(basis + aw - 1)) * rightExtra
                                    s1 += (sums(1)(basis + aw) - sums(1)(basis + aw - 1)) * rightExtra
                                    s2 += (sums(2)(basis + aw) - sums(2)(basis + aw - 1)) * rightExtra
                                End If
                            Next
                            ' Bgra8888: Blau, Gruen, Rot, Alpha.
                            Dim o = bRow + x * 4
                            bBuf(o) = BackFromLightByThresholds(s2 * divisor, backThresholds)
                            bBuf(o + 1) = BackFromLightByThresholds(s1 * divisor, backThresholds)
                            bBuf(o + 2) = BackFromLightByThresholds(s0 * divisor, backThresholds)
                            bBuf(o + 3) = 255
                        Next
                        End Sub)
                    Marshal.Copy(bBuf, 0, blurred.GetPixels(), bBuf.Length)

                    Dim back = If(scale < 1.0,
                                     New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear),
                                     New SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None))
                    cancel.ThrowIfCancellationRequested()
                    ' Zurueck auf volle Groesse ebenfalls in Baendern, aus einem Bild der kleinen
                    ' Fassung (sie ist unvormultipliziert; nur wenn das Ziel es nicht auch ist).
                    Dim backFromImage = False
                    If (aw <> result.Width OrElse ah <> result.Height) AndAlso result.AlphaType <> SKAlphaType.Unpremul Then
                        Using blurredImage = SKImage.FromBitmap(blurred)
                            backFromImage = ImageProcessor.ScaleIntoFromImage(blurredImage, result, back)
                        End Using
                    End If
                    If Not backFromImage AndAlso Not blurred.ScalePixels(result, back) Then
                        result.Dispose()
                        Return Nothing
                    End If
                End Using
            End Using
            Return result
        End Function

        ''' <summary>Die Rueckkennlinie der Lichteranhebung, zurueck auf 0 bis 255.</summary>
        Private Shared Function BackFromLight(v As Single, exponent As Double) As Byte
            If Single.IsNaN(v) OrElse v <= 0 Then Return 0
            Dim z = Math.Pow(Math.Min(1.0, v), exponent) * 255.0
            Return CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(z)))))
        End Function

        ''' <summary>Die Schwellen von <see cref="BackFromLight"/> fuer einen Exponenten: Eintrag k ist
        ''' die kleinste Gleitkommazahl, fuer die BackFromLight mindestens k liefert. Mit ihnen findet
        ''' <see cref="BackFromLightByThresholds"/> dasselbe Byte ueber acht Vergleiche statt einer
        ''' Potenz. Die Potenz lief je Bildpunkt, Kanal und Stufe - bei acht Stufen in der Vorschau
        ''' rund 67 Millionen Mal, gemessen der groesste Teil der Tiefen-Unschaerfe (1,9 s in der
        ''' Vorschau, 13 s am 20-MP-Bild).
        '''
        ''' Die Schwellen kommen aus BackFromLight SELBST, nicht aus einer Umkehrformel: die
        ''' Kennlinie ist monoton (Potenz und Runden sind es), und auf den Bitmustern positiver
        ''' Gleitkommazahlen ist die Ordnung dieselbe wie auf den Zahlen. Eine Bisektion ueber die
        ''' Bitmuster trifft so die Grenze genau, auch dort, wo Math.Round eine Haelfte zur geraden
        ''' Zahl rundet. Das Ergebnis ist dasselbe Byte wie vorher, fuer jede Eingabe.</summary>
        Private Shared Function BackFromLightThresholds(exponent As Double) As Single()
            Dim thresholds(255) As Single
            ' Unterhalb des kleinsten positiven Werts (und fuer 0) liefert BackFromLight 0.
            Dim lowBits = BitConverter.SingleToInt32Bits(Single.Epsilon)
            Dim highBits = BitConverter.SingleToInt32Bits(1.0F)
            For k = 1 To 255
                If BackFromLight(1.0F, exponent) < k Then
                    thresholds(k) = Single.PositiveInfinity
                    Continue For
                End If
                Dim lo = lowBits, hi = highBits
                ' Kleinstes Bitmuster in [lo, hi] mit BackFromLight >= k.
                While lo < hi
                    Dim middle = lo + (hi - lo) \ 2
                    If BackFromLight(BitConverter.Int32BitsToSingle(middle), exponent) >= k Then
                        hi = middle
                    Else
                        lo = middle + 1
                    End If
                End While
                thresholds(k) = BitConverter.Int32BitsToSingle(lo)
            Next
            Return thresholds
        End Function

        ''' <summary>Dasselbe Byte wie <see cref="BackFromLight"/>, ueber die Schwellen aus
        ''' <see cref="BackFromLightThresholds"/>.</summary>
        Private Shared Function BackFromLightByThresholds(v As Single, thresholds As Single()) As Byte
            If Single.IsNaN(v) OrElse v <= 0 Then Return 0
            ' Groesstes k mit thresholds(k) <= v; thresholds(0) gilt als 0.
            Dim lo = 0, hi = 255
            While lo < hi
                Dim middle = (lo + hi + 1) \ 2
                If thresholds(middle) <= v Then lo = middle Else hi = middle - 1
            End While
            Return CByte(lo)
        End Function

        ''' <summary>Eine Kennlinie als Tabelle: Wert hoch <paramref name="exponent"/>. Kleiner 1
        ''' hebt an, groesser 1 nimmt zurueck.</summary>
        Private Shared Function ToneCurveTable(exponent As Single) As Byte()
            Dim t(255) As Byte
            For i = 0 To 255
                Dim v = Math.Pow(i / 255.0, exponent)
                t(i) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(v * 255.0)))))
            Next
            Return t
        End Function

        ''' <summary>Tiefenabhaengige Unschaerfe: was weiter weg ist, wird staerker verwischt.
        '''
        ''' Der Unterschied zu einem gewoehnlichen Weichzeichner ist nicht die Staerke, sondern dass
        ''' sie je Bildpunkt aus seiner ENTFERNUNG kommt. Ein gleichmaessig unscharfer Hintergrund
        ''' sieht nach Weichzeichner aus; eine Unschaerfe, die mit der Entfernung zunimmt, sieht nach
        ''' offener Blende aus.
        '''
        ''' Gerechnet wird in STUFEN, nicht je Pixel: das Bild wird mehrfach unterschiedlich stark
        ''' verwischt, und dazwischen wird nach der Tiefenkarte ueberblendet. Ein echter Radius je
        ''' Pixel waere bei 45 MP nicht bezahlbar, und der sichtbare Unterschied ist gering, solange
        ''' die Stufen dicht genug liegen und weich ineinander laufen.
        '''
        ''' <paramref name="depth"/> ist die Karte in Bildgroesse: HELL ist nah, DUNKEL ist fern.
        ''' <paramref name="focusPct"/> ist die Entfernung, die scharf bleibt (0 bis 100, 100 = nah).
        ''' <paramref name="strengthPct"/> ist der groesste Radius am fernsten Punkt, in Prozent der
        ''' kurzen Bildkante.
        ''' <paramref name="transitionPct"/> bestimmt, wie schnell die Unschaerfe um die Fokusebene
        ''' herum zunimmt - klein ergibt eine schmale Schaerfeebene, gross eine breite.</summary>
        ''' <param name="cornerCount">Form der Blende: 0 rund, sonst die Zahl der Lamellen.</param>
        ''' <param name="highlightsPct">Wie stark Lichtpunkte als leuchtende Scheiben erhalten bleiben.</param>
        ''' <param name="fromPct">Untere Grenze des scharfen Bandes (0 ist am weitesten weg).</param>
        ''' <param name="toPct">Obere Grenze des scharfen Bandes (100 ist am naechsten).</param>
        Public Shared Function DepthBlur(source As SKBitmap, depth As SKBitmap,
                                                fromPct As Double, toPct As Double,
                                                strengthPct As Double,
                                                transitionPct As Double,
                                                Optional cornerCount As Integer = 0,
                                                Optional highlightsPct As Double = 60.0,
                                                Optional cancel As CancellationToken = Nothing) As SKBitmap
            If source Is Nothing OrElse depth Is Nothing Then Return Nothing
            cancel.ThrowIfCancellationRequested()
            If source.Width <= 0 OrElse source.Height <= 0 Then Return Nothing
            Dim strength = Math.Max(0.0, Math.Min(100.0, strengthPct))
            If strength <= 0.01 Then Return Nothing

            ' Ein BAND statt einer Ebene. Eine echte Schaerfentiefe reicht nicht symmetrisch um
            ' einen Punkt: nach hinten ist sie deutlich groesser als nach vorn. Mit zwei Grenzen
            ' laesst sich das einstellen, mit Mitte plus Breite nicht.
            Dim from = Math.Max(0.0, Math.Min(100.0, Math.Min(fromPct, toPct)))
            Dim bis = Math.Max(0.0, Math.Min(100.0, Math.Max(fromPct, toPct)))
            Dim uebergang = Math.Max(1.0, Math.Min(100.0, transitionPct))
            ' Der groesste Radius bezieht sich auf die KURZE Kante: derselbe Prozentwert soll bei
            ' Hoch- und Querformat gleich aussehen.
            Dim maxRadius = Math.Min(source.Width, source.Height) * strength / 100.0 * 0.08
            If maxRadius < 0.5 Then Return Nothing

            ' Die Tiefenkarte auf Bildgroesse bringen, falls sie kleiner ist.
            Dim map = depth
            Dim ownMap As SKBitmap = Nothing
            Try
                If depth.Width <> source.Width OrElse depth.Height <> source.Height Then
                    ownMap = New SKBitmap(New SKImageInfo(source.Width, source.Height,
                                                               SKColorType.Alpha8, SKAlphaType.Premul))
                    If Not ImageProcessor.ScalePixelsFast(depth, ownMap, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                    map = ownMap
                End If

                ' Mit Scheibenkernen fallen Stufenuebergaenge eher auf als bei weichen Glocken -
                ' eine Scheibe hat einen klaren Rand, und zwei Scheiben verschiedener Groesse
                ' nebeneinander sieht man. Deshalb mehr Stufen als bei der ersten Fassung.
                Const Steps As Integer = 8
                Dim result = New SKBitmap(source.Width, source.Height, source.ColorType, source.AlphaType)
                ' EIN Bild der Quelle fuer alle Stufen: Skia merkt sich seine Mipmaps, und jede Stufe
                ' verkleinert daraus (ScaleIntoFromImage).
                Using sourceImage = SKImage.FromBitmap(source),
                      canvas = New SKCanvas(result)
                    canvas.Clear(SKColors.Transparent)
                    ' Stufe 0 ist das scharfe Bild, darueber jede weitere Stufe staerker verwischt.
                    canvas.DrawBitmap(source, 0, 0)
                    For stufe = 1 To Steps
                        cancel.ThrowIfCancellationRequested()
                        Dim share = stufe / CDbl(Steps)
                        ' Die Maske dieser Stufe: wie weit ist dieser Punkt von der Fokusebene weg,
                        ' gemessen in Anteilen des Uebergangs, geklemmt auf diese Stufe.
                        Using mask = StepMask(map, from, bis, uebergang, share, 1.0 / Steps, cancel)
                            If mask Is Nothing Then Continue For
                            Using blurred = BlurWithDisc(source, maxRadius * share,
                                                                  cornerCount, highlightsPct, cancel, sourceImage)
                                If blurred Is Nothing Then Continue For
                                ' Die verwischte Fassung mit der Stufenmaske daruebermischen, in
                                ' parallelen Baendern: je Band eine eigene Zeichenflaeche ueber dem
                                ' Ergebnis mit Ausschnitt, dieselbe Zeichnung wie vorher auf einer
                                ' Flaeche. Auf einem Faden waren das am 20-MP-Bild 5,6 s fuer acht
                                ' Stufen. Das scharfe Bild darunter steht schon (canvas.DrawBitmap
                                ' oben), die Baender kommen erst danach; vorher wird geleert.
                                canvas.Flush()
                                Using shader = SKShader.CreateBitmap(blurred, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp)
                                    Const BandRows As Integer = 128
                                    Dim bandCount = (result.Height + BandRows - 1) \ BandRows
                                    Dim stageMask = mask
                                    Parallel.For(0, bandCount,
                                        Sub(band)
                                            Using bandCanvas = New SKCanvas(result),
                                                  p3 = New SKPaint With {.Shader = shader}
                                                Dim top = band * BandRows
                                                bandCanvas.ClipRect(New SKRect(0, top, result.Width, Math.Min(result.Height, top + BandRows)))
                                                bandCanvas.DrawBitmap(stageMask, 0, 0, p3)
                                            End Using
                                        End Sub)
                                End Using
                            End Using
                        End Using
                    Next
                End Using
                Return result
            Finally
                ownMap?.Dispose()
            End Try
        End Function

        ''' <summary>So viel ferner als der naechste Punkt liegt der fernste, wenn aus der Tiefenkarte
        ''' eine Entfernung wird (<see cref="FogDistance"/>). Die Karte sagt das nicht: sie ist je Bild
        ''' auf 0 bis 255 gestreckt und kennt weder Massstab noch Versatz. Ein fester Wert ist deshalb
        ''' eine Annahme, keine Messung; zehn entspricht einer Landschaft mit Vordergrund, dem
        ''' haeufigsten Fall fuer Nebel.</summary>
        Friend Const FogDistanceRatio As Double = 10.0

        ''' <summary>Strecke, auf der die Dichte des Nebels am Beginn und an der vollen Dichte weich
        ''' ein- und wieder auslaeuft, als Anteil der Entfernung des fernsten Punkts
        ''' (<see cref="FogOpticalLength"/>).</summary>
        Private Const FogOnsetDistance As Double = 0.05

        ''' <summary>Aus einem Byte der Tiefenkarte (255 nah, 0 fern) die Entfernung von der Kamera,
        ''' 1 am fernsten Punkt, 1/R am naechsten (R = <see cref="FogDistanceRatio"/>).
        '''
        ''' Das Tiefenmodell liefert umgekehrte Entfernung (Disparitaet). Auf ihr draengen sich die
        ''' fernen Bereiche bei 0, die nahen nehmen den groessten Teil der Skala ein. Ein Nebel, der
        ''' auf diesen Werten ansteigt, waere vorn zu breit und hinten zu schmal verteilt. Daher
        ''' D(i) = 1 / ((R - 1) i + 1). Gezaehlt wird ab der KAMERA, nicht ab dem naechsten Punkt:
        ''' auch zwischen Kamera und Motiv liegt Nebel, und so bekommt der naechste Punkt den Anteil,
        ''' der seiner Entfernung entspricht.</summary>
        Friend Shared Function FogDistance(depthByte As Integer) As Double
            Dim i = Math.Max(0, Math.Min(255, depthByte)) / 255.0
            Return 1.0 / ((FogDistanceRatio - 1.0) * i + 1.0)
        End Function

        ''' <summary>Optische Laenge von 0 bis 1 ueber die Strecke <paramref name="x"/> (0 am Beginn, 1
        ''' bei voller Dichte), bei einer Dichte des Nebels, die auf dem Anteil
        ''' <paramref name="onsetShare"/> am Anfang und am Ende der Strecke als Smoothstep ein- und
        ''' auslaeuft und dazwischen gleich bleibt.
        '''
        ''' Ohne das Ein- und Auslaufen knickte die Kennlinie am Beginn und an der vollen Dichte, und
        ''' auf einer stetigen Tiefenkarte saehe man den Knick als Linie quer durchs Bild, etwa
        ''' entlang eines Hangs. Das Integral des Smoothstep 3t^2 - 2t^3 ist t^3 - t^4/2.</summary>
        Friend Shared Function FogOpticalLength(x As Double, onsetShare As Double) As Double
            If x <= 0.0 Then Return 0.0
            If x >= 1.0 Then Return 1.0
            Dim w = Math.Max(0.0, Math.Min(0.5, onsetShare))
            Dim ramp = Function(t As Double) t * t * t - t * t * t * t / 2.0
            Dim length As Double
            If x <= w Then
                length = w * ramp(x / w)
            ElseIf x <= 1.0 - w Then
                length = w / 2.0 + (x - w)
            Else
                length = 1.0 - w - w * ramp((1.0 - x) / w)
            End If
            Return length / (1.0 - w)
        End Function

        ''' <summary>Nebel nach Tiefe: je ferner ein Punkt, desto mehr Nebelfarbe liegt ueber ihm.
        '''
        ''' Gerechnet wird wie ein gleichmaessiges Medium: der Durchlass faellt mit der optischen
        ''' Laenge exponentiell (Beer-Lambert), Anteil der Nebelfarbe f = 1 - (1 - Dichte)^L. Die
        ''' optische Laenge L waechst mit der ENTFERNUNG (<see cref="FogDistance"/>), nicht mit dem
        ''' Wert der Tiefenkarte, und zwar ab <paramref name="nearPct"/> bis
        ''' <paramref name="farPct"/>; dort liegt die volle <paramref name="densityPct"/>, ferner
        ''' bleibt sie. Beide Grenzen zaehlen gleichmaessig ueber die Entfernung, 100 an der Kamera,
        ''' 0 am fernsten Punkt. Mit Beginn 100 bekommt also schon der naechste Punkt etwas Nebel,
        ''' bei der vorgegebenen Annahme ein Zehntel der optischen Laenge des fernsten.
        '''
        ''' Gemischt wird auf den vormultiplizierten Bytes, die Nebelfarbe wird dafuer mit dem Alpha
        ''' des Punktes multipliziert. Ein durchsichtiger Punkt bleibt so durchsichtig.
        '''
        ''' <paramref name="foregroundPct"/> legt fest, wie viel Nebel schon vor dem Beginn liegt, als
        ''' Anteil der Dichte; der Anstieg laeuft darueber bis zur vollen Dichte. Die Tiefenkarte
        ''' ist je Bild gestreckt, der naechste Punkt ist immer 100: ohne diesen Anteil blieb das
        ''' Motiv im Vordergrund bei jeder Einstellung klar, waehrend echter Nebel auch zwischen
        ''' Kamera und Motiv liegt (Befund aus dem Forum, ein Hund und eine Person auf einem
        ''' Kuestenweg).</summary>
        ''' <returns>Nothing, wenn es nichts zu tun gibt oder das Farbformat unbekannt ist.</returns>
        Public Shared Function DepthFog(source As SKBitmap, depth As SKBitmap,
                                        densityPct As Double, nearPct As Double, farPct As Double,
                                        fogColor As SKColor,
                                        Optional cancel As CancellationToken = Nothing,
                                        Optional foregroundPct As Double = 0.0) As SKBitmap
            If source Is Nothing OrElse depth Is Nothing Then Return Nothing
            cancel.ThrowIfCancellationRequested()
            Dim density = Math.Max(0.0, Math.Min(100.0, densityPct)) / 100.0
            If density <= 0.0001 OrElse source.Width <= 0 OrElse source.Height <= 0 Then Return Nothing
            Dim ri, gi, bi As Integer
            Select Case source.ColorType
                Case SKColorType.Bgra8888 : ri = 2 : gi = 1 : bi = 0
                Case SKColorType.Rgba8888 : ri = 0 : gi = 1 : bi = 2
                Case Else : Return Nothing
            End Select
            Dim near = Math.Max(0.0, Math.Min(100.0, Math.Max(nearPct, farPct)))
            Dim far = Math.Max(0.0, Math.Min(100.0, Math.Min(nearPct, farPct)))
            Dim foreground = Math.Max(0.0, Math.Min(100.0, foregroundPct)) / 100.0
            ' Grenzen als Entfernung: 0 an der Kamera, 1 am fernsten Punkt.
            Dim startShare = (100.0 - near) / 100.0
            Dim fullShare = (100.0 - far) / 100.0
            Dim onsetShare = If(fullShare - startShare < 0.0001, 0.0, FogOnsetDistance / (fullShare - startShare))
            ' Bei voller Dichte waere der Durchlass null und der Exponent unbestimmt; 0,999 laesst
            ' den fernsten Punkt bis auf ein Tausendstel in der Nebelfarbe stehen.
            Dim transmissionBase = 1.0 - Math.Min(density, 0.999)

            ' Wie bei den Masken: der Anteil haengt nur am Tiefenbyte.
            Dim table(255) As Single
            For value = 0 To 255
                Dim d = FogDistance(value)
                Dim length As Double
                If d <= startShare Then
                    length = 0.0
                ElseIf d >= fullShare OrElse fullShare - startShare < 0.0001 Then
                    length = 1.0
                Else
                    length = FogOpticalLength((d - startShare) / (fullShare - startShare), onsetShare)
                End If
                Dim medium = 1.0 - Math.Pow(transmissionBase, length)
                table(value) = CSng(foreground * density + (1.0 - foreground) * medium)
            Next

            Dim map = depth
            Dim ownMap As SKBitmap = Nothing
            Try
                If depth.Width <> source.Width OrElse depth.Height <> source.Height Then
                    ownMap = New SKBitmap(New SKImageInfo(source.Width, source.Height,
                                                          SKColorType.Alpha8, SKAlphaType.Premul))
                    If Not ImageProcessor.ScalePixelsFast(depth, ownMap, New SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)) Then Return Nothing
                    map = ownMap
                End If
                Dim depthStride As Integer
                Dim depthBytes = ImageProcessor.TryReadAlpha8(map, depthStride)
                If depthBytes Is Nothing Then Return Nothing

                Dim w = source.Width, h = source.Height
                Dim stride = source.RowBytes
                Dim pixels(stride * h - 1) As Byte
                Marshal.Copy(source.GetPixels(), pixels, 0, pixels.Length)
                Dim fr = CSng(fogColor.Red), fg = CSng(fogColor.Green), fb = CSng(fogColor.Blue)
                ' Kein Werfen im Faden: Parallel.For verpackt es in eine AggregateException, und der
                ' Abbruch kaeme beim Aufrufer nicht als Abbruch an. Angehalten, geworfen wird danach.
                Parallel.For(0, h, Sub(y, state)
                                       If cancel.IsCancellationRequested Then
                                           state.Stop()
                                           Return
                                       End If
                                       Dim row = y * stride
                                       Dim depthRow = y * depthStride
                                       For x = 0 To w - 1
                                           Dim k = table(depthBytes(depthRow + x))
                                           If k <= 0 Then Continue For
                                           Dim o = row + x * 4
                                           Dim a = pixels(o + 3) / 255.0F
                                           pixels(o + ri) = CByte(Math.Min(255.0F, pixels(o + ri) + (fr * a - pixels(o + ri)) * k))
                                           pixels(o + gi) = CByte(Math.Min(255.0F, pixels(o + gi) + (fg * a - pixels(o + gi)) * k))
                                           pixels(o + bi) = CByte(Math.Min(255.0F, pixels(o + bi) + (fb * a - pixels(o + bi)) * k))
                                       Next
                                   End Sub)
                cancel.ThrowIfCancellationRequested()
                Dim result = New SKBitmap(w, h, source.ColorType, source.AlphaType)
                Marshal.Copy(pixels, 0, result.GetPixels(), pixels.Length)
                Return result
            Finally
                ownMap?.Dispose()
            End Try
        End Function

        ''' <summary>Wie stark diese Unschaerfestufe an jedem Punkt beitraegt. Die Stufen ueberlappen
        ''' sich bewusst und laufen weich aus - ohne das saehe man ihre Grenzen als Ringe.</summary>
        Private Shared Function StepMask(depth As SKBitmap, fromPct As Double, toPct As Double,
                                            transitionPct As Double,
                                            share As Double, width As Double,
                                            cancel As CancellationToken) As SKBitmap
            Dim w = depth.Width, h = depth.Height
            Dim output = New SKBitmap(New SKImageInfo(w, h, SKColorType.Alpha8, SKAlphaType.Premul))
            Dim buffer(w * h - 1) As Byte
            Dim empty = True
            ' Wie in MaskFromDepth: der Wert haengt nur am Tiefenbyte, also einmal je moeglichem
            ' Byte rechnen und nachschlagen.
            Dim table(255) As Byte
            Dim contributes(255) As Boolean
            For value = 0 To 255
                Dim t = value / 255.0 * 100.0
                ' Abstand aus dem scharfen BAND heraus, auf 0..1 gebracht. Innerhalb ist er null
                ' - dort bleibt es scharf, egal wie breit das Band ist.
                Dim outside = 0.0
                If t < fromPct Then
                    outside = fromPct - t
                ElseIf t > toPct Then
                    outside = t - toPct
                End If
                Dim d = Math.Min(1.0, outside / transitionPct)
                ' Dreieck um diese Stufe herum: voll bei "anteil", null eine Stufenbreite daneben.
                Dim g = 1.0 - Math.Min(1.0, Math.Abs(d - share) / width)
                contributes(value) = g > 0.0
                table(value) = CByte(Math.Max(0, Math.Min(255, CInt(Math.Round(g * 255.0)))))
            Next
            Dim stride As Integer
            Dim depthBytes = ImageProcessor.TryReadAlpha8(depth, stride)
            For y = 0 To h - 1
                If y Mod 16 = 0 Then cancel.ThrowIfCancellationRequested()
                For x = 0 To w - 1
                    Dim value = If(depthBytes IsNot Nothing, depthBytes(y * stride + x), depth.GetPixel(x, y).Alpha)
                    If contributes(value) Then empty = False
                    buffer(y * w + x) = table(value)
                Next
            Next
            If empty Then
                output.Dispose()
                Return Nothing
            End If
            Runtime.InteropServices.Marshal.Copy(buffer, 0, output.GetPixels(), buffer.Length)
            Return output
        End Function

    End Class

End Namespace
